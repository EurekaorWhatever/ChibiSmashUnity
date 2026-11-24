using System;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;

namespace ChibiClash.Netcode
{
    /// <summary>A transport that must be serviced once per tick (receive before the tick, flush sends after it).</summary>
    public interface IChibiPumpedTransport
    {
        /// <summary>Run the driver: accept / connect, receive datagrams into the queue. Call at the start of a tick.</summary>
        void Pump();
        /// <summary>Push queued sends onto the wire now. Call at the end of a tick (lowest input latency).</summary>
        void Flush();
    }

    /// <summary>
    /// Unity Transport (UTP 6.6) datagram pipe: either through Unity Relay (internet play, hides IPs) or direct UDP (LAN,
    /// and the EditMode integration tests on 127.0.0.1). Unreliable, unsequenced pipeline (<c>NetworkPipeline.Null</c>):
    /// the rollback protocol already repairs loss by resending the oldest unacked inputs in every datagram, so a reliable
    /// pipeline would only add head-of-line latency.
    ///
    /// The joiner connects to one host. The host binds, listens and accepts up to <see cref="MaxPeers"/> peers (1 for a
    /// 1v1, up to 3 guests for the protocol 6 star; one Relay allocation serves them all, four-player-design.md 3). With one
    /// peer the transport itself is the link (<see cref="IChibiTransport"/>); with more, each accepted guest is reached
    /// through <see cref="Peer"/>. Datagrams larger than <see cref="ChibiChannelMux.MaxDatagram"/> are dropped on receive
    /// (never truncated). Receive copies from the driver into preallocated rings, so steady-state traffic allocates nothing.
    /// Works on the WebGL wss path too: the driver comes from <see cref="CreateRelayDriver"/>, and Relay routes every guest
    /// of the allocation to the host's single socket. Single-threaded: call from the main thread.
    /// </summary>
    /// <example><code>
    /// var t = ChibiUtpTransport.HostRelay(allocation.ToRelayServerData("dtls"), maxPeers: 3);   // or HostDirect(7777)
    /// // per 60 Hz tick: t.Pump(); match.Tick(); t.Flush();
    /// </code></example>
    public sealed class ChibiUtpTransport : IChibiTransport, IChibiLinkStatus, IChibiPumpedTransport, IDisposable
    {
        /// <summary>Most guests one host accepts (a 4-player match).</summary>
        public const int MaxGuests = ChibiSimState.MaxFighters - 1;
        /// <summary>Datagrams buffered between ticks per peer (a 60 Hz peer sends one per tick; 64 covers a long hitch).</summary>
        const int QueueSize = 64;
        /// <summary>UTP connect timeout and attempts: about 10 s, like the handshake timeout.</summary>
        const int ConnectTimeoutMs = 1000, MaxConnectAttempts = 10;
        /// <summary>UTP gives up on a silent connection after 10 s; the rollback session reacts earlier (5 s).</summary>
        const int DisconnectTimeoutMs = 10000, HeartbeatMs = 500;

        /// <summary>One accepted guest of a host (or the host, for a joiner): its own queue and state.</summary>
        sealed class PeerSlot
        {
            public NetworkConnection Conn;
            public ChibiLinkState State = ChibiLinkState.Connecting;
            public bool Used;
            public readonly byte[][] Queue = new byte[QueueSize][];
            public readonly int[] Len = new int[QueueSize];
            public int Head, Count;
            public PeerSlot() { for (int i = 0; i < QueueSize; i++) Queue[i] = new byte[ChibiChannelMux.MaxDatagram]; }
        }

        /// <summary>Datagram pipe to one accepted guest (<see cref="Peer"/>).</summary>
        sealed class PeerLink : IChibiTransport, IChibiLinkStatus
        {
            readonly ChibiUtpTransport _t;
            readonly int _i;
            public PeerLink(ChibiUtpTransport t, int i) { _t = t; _i = i; }
            public void Send(byte[] data, int length) => _t.SendTo(_i, data, length);
            public int Receive(byte[] buffer) => _t.ReceiveFrom(_i, buffer);
            public ChibiLinkState LinkState => _t._peers[_i].State;
        }

        NetworkDriver _driver;
        readonly bool _isHost;
        readonly PeerSlot[] _peers;
        readonly PeerLink[] _links;
        bool _failed;

        /// <summary>Peers this host accepts (1 for a joiner).</summary>
        public int MaxPeers => _peers.Length;
        /// <summary>
        /// Overall state. Joiner / 1v1 host: the one peer's state. Host of a group: Connected while any guest is connected,
        /// Lost only when the driver failed or every accepted guest is gone after the transport was sealed.
        /// </summary>
        public ChibiLinkState LinkState
        {
            get
            {
                if (_failed) return ChibiLinkState.Lost;
                if (_peers.Length == 1) return _peers[0].State;
                bool any = false, live = false;
                foreach (var p in _peers)
                {
                    if (p.State == ChibiLinkState.Connected) return ChibiLinkState.Connected;
                    any |= p.Used;
                    live |= p.Used && p.State != ChibiLinkState.Lost;
                }
                return Sealed && any && !live ? ChibiLinkState.Lost : ChibiLinkState.Connecting;
            }
        }
        /// <summary>Datagrams dropped because a receive queue was full or a datagram was oversized (diagnostics).</summary>
        public int Dropped { get; private set; }
        /// <summary>True when this side listens (host, sim player 0).</summary>
        public bool IsHost => _isHost;
        /// <summary>Local port the driver is bound to (direct mode; 0 before binding).</summary>
        public ushort LocalPort { get; private set; }
        /// <summary>
        /// Once true the host accepts nobody new (every later connection is refused at once). Set when the waiting room hands
        /// the guests to a match, so a stranger can never take a seat mid-match.
        /// </summary>
        public bool Sealed { get; set; }

        /// <summary>Guests currently connected (host).</summary>
        public int ConnectedPeers
        {
            get { int n = 0; foreach (var p in _peers) if (p.State == ChibiLinkState.Connected) n++; return n; }
        }

        /// <summary>Link to peer slot <paramref name="i"/> (0..MaxPeers-1, in accept order). Its state is the guest's own.</summary>
        public IChibiTransport Peer(int i) => _links[i];

        /// <summary>State of peer slot <paramref name="i"/>.</summary>
        public ChibiLinkState PeerState(int i) => _peers[i].State;

        ChibiUtpTransport(bool isHost, int maxPeers)
        {
            if (maxPeers < 1 || maxPeers > MaxGuests) throw new ArgumentOutOfRangeException(nameof(maxPeers));
            _isHost = isHost;
            _peers = new PeerSlot[maxPeers];
            _links = new PeerLink[maxPeers];
            for (int i = 0; i < maxPeers; i++) { _peers[i] = new PeerSlot(); _links[i] = new PeerLink(this, i); }
        }

        static NetworkSettings BaseSettings()
        {
            var s = new NetworkSettings();
            s.WithNetworkConfigParameters(connectTimeoutMS: ConnectTimeoutMs, maxConnectAttempts: MaxConnectAttempts,
                                          disconnectTimeoutMS: DisconnectTimeoutMs, heartbeatTimeoutMS: HeartbeatMs);
            return s;
        }

        /// <summary>
        /// Driver for a Relay link. In a browser (WebGL player) it must be a WebSocket driver: browsers cannot open UDP
        /// sockets, and UTP's default WebGL interface (IPC) is local-only. The Relay data must then be "wss"
        /// (<see cref="NetworkRoomManager.RelayConnectionType"/>). Everywhere else: UDP, as before.
        /// </summary>
        static NetworkDriver CreateRelayDriver(NetworkSettings settings)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
#else
            return NetworkDriver.Create(settings);
#endif
        }

        /// <summary>Host over Unity Relay: <paramref name="relay"/> from the host's Allocation (ToRelayServerData(type)),
        /// created for <paramref name="maxPeers"/> connections.</summary>
        public static ChibiUtpTransport HostRelay(RelayServerData relay, int maxPeers = 1)
        {
            var t = new ChibiUtpTransport(true, maxPeers);
            var settings = BaseSettings();
            settings.WithRelayParameters(ref relay);
            t._driver = CreateRelayDriver(settings);
            if (t._driver.Bind(NetworkEndpoint.AnyIpv4) != 0 || t._driver.Listen() != 0) t.Fail();
            return t;
        }

        /// <summary>Joiner over Unity Relay: <paramref name="relay"/> from the JoinAllocation (ToRelayServerData(type)).</summary>
        public static ChibiUtpTransport JoinRelay(RelayServerData relay)
        {
            var t = new ChibiUtpTransport(false, 1);
            var settings = BaseSettings();
            settings.WithRelayParameters(ref relay);
            t._driver = CreateRelayDriver(settings);
            if (t._driver.Bind(NetworkEndpoint.AnyIpv4) != 0) { t.Fail(); return t; }
            t.Attach(0, t._driver.Connect(relay.Endpoint));
            return t;
        }

        /// <summary>Host over direct UDP (LAN / tests). <paramref name="port"/> 0 picks a free port (see <see cref="LocalPort"/>).</summary>
        public static ChibiUtpTransport HostDirect(ushort port, bool loopbackOnly = false, int maxPeers = 1)
        {
            var t = new ChibiUtpTransport(true, maxPeers);
            t._driver = NetworkDriver.Create(BaseSettings());
            var ep = (loopbackOnly ? NetworkEndpoint.LoopbackIpv4 : NetworkEndpoint.AnyIpv4).WithPort(port);
            if (t._driver.Bind(ep) != 0 || t._driver.Listen() != 0) { t.Fail(); return t; }
            t.LocalPort = t._driver.GetLocalEndpoint().Port;
            return t;
        }

        /// <summary>Joiner over direct UDP (LAN / tests).</summary>
        public static ChibiUtpTransport JoinDirect(string address, ushort port)
        {
            var t = new ChibiUtpTransport(false, 1);
            t._driver = NetworkDriver.Create(BaseSettings());
            if (!NetworkEndpoint.TryParse(address, port, out var ep)) { t.Fail(); return t; }
            t.Attach(0, t._driver.Connect(ep));
            return t;
        }

        void Fail()
        {
            _failed = true;
            foreach (var p in _peers) p.State = ChibiLinkState.Lost;
        }

        void Attach(int i, NetworkConnection c)
        {
            var p = _peers[i];
            p.Conn = c;
            p.Used = true;
            p.State = ChibiLinkState.Connecting;
            p.Head = p.Count = 0;
        }

        /// <summary>Free slot for a new guest: never used, or (before <see cref="Sealed"/>) one whose guest left.</summary>
        int FreeSlot()
        {
            for (int i = 0; i < _peers.Length; i++) if (!_peers[i].Used) return i;
            if (_peers.Length > 1)
                for (int i = 0; i < _peers.Length; i++) if (_peers[i].State == ChibiLinkState.Lost && !_peers[i].Conn.IsCreated) return i;
            return -1;
        }

        /// <inheritdoc/>
        public void Pump()
        {
            if (!_driver.IsCreated) return;
            _driver.ScheduleUpdate().Complete();

            if (_isHost)
            {
                NetworkConnection c;
                while ((c = _driver.Accept()).IsCreated)
                {
                    int slot = _failed || Sealed ? -1 : FreeSlot();
                    if (slot >= 0) Attach(slot, c);
                    else _driver.Disconnect(c);   // full (1v1: a second peer) or sealed: refused
                }
            }

            for (int i = 0; i < _peers.Length; i++)
            {
                var p = _peers[i];
                if (!p.Conn.IsCreated) continue;
                NetworkEvent.Type ev;
                while ((ev = _driver.PopEventForConnection(p.Conn, out var reader)) != NetworkEvent.Type.Empty)
                {
                    if (ev == NetworkEvent.Type.Connect) { if (p.State == ChibiLinkState.Connecting) p.State = ChibiLinkState.Connected; }
                    else if (ev == NetworkEvent.Type.Data)
                    {
                        if (p.State == ChibiLinkState.Connecting) p.State = ChibiLinkState.Connected;
                        Enqueue(p, ref reader);
                    }
                    else if (ev == NetworkEvent.Type.Disconnect)
                    {
                        p.State = ChibiLinkState.Lost;
                        p.Conn = default;
                        break;
                    }
                }
                // The host learns about a peer from Accept (UTP raises no Connect event on the listening side).
                if (_isHost && p.State == ChibiLinkState.Connecting && p.Conn.IsCreated &&
                    _driver.GetConnectionState(p.Conn) == NetworkConnection.State.Connected)
                    p.State = ChibiLinkState.Connected;
            }
        }

        void Enqueue(PeerSlot p, ref DataStreamReader reader)
        {
            int len = reader.Length;
            if (len <= 0 || len > ChibiChannelMux.MaxDatagram || p.Count == QueueSize) { Dropped++; return; }
            int slot = (p.Head + p.Count) % QueueSize;
            reader.ReadBytes(new Span<byte>(p.Queue[slot], 0, len));
            p.Len[slot] = len;
            p.Count++;
        }

        /// <inheritdoc/>
        public void Flush()
        {
            if (_driver.IsCreated) _driver.ScheduleFlushSend().Complete();
        }

        /// <summary>1v1 / joiner: send to the one peer.</summary>
        public void Send(byte[] data, int length) => SendTo(0, data, length);

        /// <summary>1v1 / joiner: receive from the one peer.</summary>
        public int Receive(byte[] buffer) => ReceiveFrom(0, buffer);

        void SendTo(int i, byte[] data, int length)
        {
            var p = _peers[i];
            if (p.State != ChibiLinkState.Connected || !p.Conn.IsCreated || length <= 0) return;
            if (_driver.BeginSend(NetworkPipeline.Null, p.Conn, out var writer) != 0) return;
            if (!writer.WriteBytes(new Span<byte>(data, 0, length))) { _driver.AbortSend(writer); return; }
            _driver.EndSend(writer);
        }

        int ReceiveFrom(int i, byte[] buffer)
        {
            var p = _peers[i];
            if (p.Count == 0) return 0;
            int len = p.Len[p.Head];
            if (len > buffer.Length) { Dropped++; len = 0; }
            else Buffer.BlockCopy(p.Queue[p.Head], 0, buffer, 0, len);
            p.Head = (p.Head + 1) % QueueSize;
            p.Count--;
            return len;
        }

        /// <summary>Host: close one guest's connection (the room manager drops a guest that is not part of the match).</summary>
        public void DropPeer(int i)
        {
            var p = _peers[i];
            if (_driver.IsCreated && p.Conn.IsCreated) _driver.Disconnect(p.Conn);
            p.Conn = default;
            p.State = ChibiLinkState.Lost;
        }

        /// <summary>Close every connection and free the native driver. Safe to call twice.</summary>
        public void Dispose()
        {
            if (_driver.IsCreated)
            {
                bool any = false;
                foreach (var p in _peers) if (p.Conn.IsCreated) { _driver.Disconnect(p.Conn); any = true; }
                if (any) _driver.ScheduleFlushSend().Complete();
                _driver.Dispose();
            }
            foreach (var p in _peers) { p.Conn = default; p.State = ChibiLinkState.Lost; }
            _failed = true;
        }
    }
}
