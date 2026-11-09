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
    /// Unity Transport (UTP 6.6) datagram pipe for one 1v1 match: either through Unity Relay (internet play, hides IPs)
    /// or direct UDP (LAN, and the EditMode integration tests on 127.0.0.1). Unreliable, unsequenced pipeline
    /// (<c>NetworkPipeline.Null</c>): the rollback protocol already repairs loss by resending the oldest unacked inputs
    /// in every datagram, so a reliable pipeline would only add head-of-line latency.
    ///
    /// The host binds and listens and accepts exactly ONE peer; the joiner connects. Datagrams larger than
    /// <see cref="ChibiChannelMux.MaxDatagram"/> are dropped on receive (never truncated). Receive copies from the
    /// driver into a preallocated ring, so steady-state traffic allocates nothing. Single-threaded: call from the main thread.
    /// </summary>
    /// <example><code>
    /// var t = ChibiUtpTransport.HostRelay(allocation.ToRelayServerData("dtls"));   // or HostDirect(7777)
    /// // per 60 Hz tick: t.Pump(); match.Tick(); t.Flush();
    /// </code></example>
    public sealed class ChibiUtpTransport : IChibiTransport, IChibiLinkStatus, IChibiPumpedTransport, IDisposable
    {
        /// <summary>Datagrams buffered between ticks (a 60 Hz peer sends one per tick; 64 covers a long hitch).</summary>
        const int QueueSize = 64;
        /// <summary>UTP connect timeout and attempts: about 10 s, like the handshake timeout.</summary>
        const int ConnectTimeoutMs = 1000, MaxConnectAttempts = 10;
        /// <summary>UTP gives up on a silent connection after 10 s; the rollback session reacts earlier (5 s).</summary>
        const int DisconnectTimeoutMs = 10000, HeartbeatMs = 500;

        NetworkDriver _driver;
        NetworkConnection _conn;
        readonly bool _isHost;
        readonly byte[][] _queue = new byte[QueueSize][];
        readonly int[] _queueLen = new int[QueueSize];
        int _head, _count;

        /// <inheritdoc/>
        public ChibiLinkState LinkState { get; private set; } = ChibiLinkState.Connecting;
        /// <summary>Datagrams dropped because the receive queue was full or a datagram was oversized (diagnostics).</summary>
        public int Dropped { get; private set; }
        /// <summary>True when this side listens (host, sim player 0).</summary>
        public bool IsHost => _isHost;
        /// <summary>Local port the driver is bound to (direct mode; 0 before binding).</summary>
        public ushort LocalPort { get; private set; }

        ChibiUtpTransport(bool isHost)
        {
            _isHost = isHost;
            for (int i = 0; i < QueueSize; i++) _queue[i] = new byte[ChibiChannelMux.MaxDatagram];
        }

        static NetworkSettings BaseSettings()
        {
            var s = new NetworkSettings();
            s.WithNetworkConfigParameters(connectTimeoutMS: ConnectTimeoutMs, maxConnectAttempts: MaxConnectAttempts,
                                          disconnectTimeoutMS: DisconnectTimeoutMs, heartbeatTimeoutMS: HeartbeatMs);
            return s;
        }

        /// <summary>Host over Unity Relay: <paramref name="relay"/> from the host's Allocation (ToRelayServerData("dtls")).</summary>
        public static ChibiUtpTransport HostRelay(RelayServerData relay)
        {
            var t = new ChibiUtpTransport(true);
            var settings = BaseSettings();
            settings.WithRelayParameters(ref relay);
            t._driver = NetworkDriver.Create(settings);
            if (t._driver.Bind(NetworkEndpoint.AnyIpv4) != 0 || t._driver.Listen() != 0) t.Fail();
            return t;
        }

        /// <summary>Joiner over Unity Relay: <paramref name="relay"/> from the JoinAllocation (ToRelayServerData("dtls")).</summary>
        public static ChibiUtpTransport JoinRelay(RelayServerData relay)
        {
            var t = new ChibiUtpTransport(false);
            var settings = BaseSettings();
            settings.WithRelayParameters(ref relay);
            t._driver = NetworkDriver.Create(settings);
            if (t._driver.Bind(NetworkEndpoint.AnyIpv4) != 0) { t.Fail(); return t; }
            t._conn = t._driver.Connect(relay.Endpoint);
            return t;
        }

        /// <summary>Host over direct UDP (LAN / tests). <paramref name="port"/> 0 picks a free port (see <see cref="LocalPort"/>).</summary>
        public static ChibiUtpTransport HostDirect(ushort port, bool loopbackOnly = false)
        {
            var t = new ChibiUtpTransport(true);
            t._driver = NetworkDriver.Create(BaseSettings());
            var ep = (loopbackOnly ? NetworkEndpoint.LoopbackIpv4 : NetworkEndpoint.AnyIpv4).WithPort(port);
            if (t._driver.Bind(ep) != 0 || t._driver.Listen() != 0) { t.Fail(); return t; }
            t.LocalPort = t._driver.GetLocalEndpoint().Port;
            return t;
        }

        /// <summary>Joiner over direct UDP (LAN / tests).</summary>
        public static ChibiUtpTransport JoinDirect(string address, ushort port)
        {
            var t = new ChibiUtpTransport(false);
            t._driver = NetworkDriver.Create(BaseSettings());
            if (!NetworkEndpoint.TryParse(address, port, out var ep)) { t.Fail(); return t; }
            t._conn = t._driver.Connect(ep);
            return t;
        }

        void Fail() => LinkState = ChibiLinkState.Lost;

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
                    if (!_conn.IsCreated && LinkState != ChibiLinkState.Lost) _conn = c;
                    else _driver.Disconnect(c);   // 1v1: a second peer is refused
                }
            }
            if (!_conn.IsCreated) return;

            NetworkEvent.Type ev;
            while ((ev = _driver.PopEventForConnection(_conn, out var reader)) != NetworkEvent.Type.Empty)
            {
                switch (ev)
                {
                    case NetworkEvent.Type.Connect:
                        if (LinkState == ChibiLinkState.Connecting) LinkState = ChibiLinkState.Connected;
                        break;
                    case NetworkEvent.Type.Data:
                        if (LinkState == ChibiLinkState.Connecting) LinkState = ChibiLinkState.Connected;
                        Enqueue(ref reader);
                        break;
                    case NetworkEvent.Type.Disconnect:
                        LinkState = ChibiLinkState.Lost;
                        _conn = default;
                        return;
                }
            }
            // The host learns about the peer from Accept (UTP raises no Connect event on the listening side).
            if (_isHost && LinkState == ChibiLinkState.Connecting && _conn.IsCreated &&
                _driver.GetConnectionState(_conn) == NetworkConnection.State.Connected)
                LinkState = ChibiLinkState.Connected;
        }

        void Enqueue(ref DataStreamReader reader)
        {
            int len = reader.Length;
            if (len <= 0 || len > ChibiChannelMux.MaxDatagram || _count == QueueSize) { Dropped++; return; }
            int slot = (_head + _count) % QueueSize;
            reader.ReadBytes(new Span<byte>(_queue[slot], 0, len));
            _queueLen[slot] = len;
            _count++;
        }

        /// <inheritdoc/>
        public void Flush()
        {
            if (_driver.IsCreated) _driver.ScheduleFlushSend().Complete();
        }

        /// <inheritdoc/>
        public void Send(byte[] data, int length)
        {
            if (LinkState != ChibiLinkState.Connected || !_conn.IsCreated || length <= 0) return;
            if (_driver.BeginSend(NetworkPipeline.Null, _conn, out var writer) != 0) return;
            if (!writer.WriteBytes(new Span<byte>(data, 0, length))) { _driver.AbortSend(writer); return; }
            _driver.EndSend(writer);
        }

        /// <inheritdoc/>
        public int Receive(byte[] buffer)
        {
            if (_count == 0) return 0;
            int len = _queueLen[_head];
            if (len > buffer.Length) { Dropped++; len = 0; }
            else Buffer.BlockCopy(_queue[_head], 0, buffer, 0, len);
            _head = (_head + 1) % QueueSize;
            _count--;
            return len;
        }

        /// <summary>Close the connection and free the native driver. Safe to call twice.</summary>
        public void Dispose()
        {
            if (_driver.IsCreated)
            {
                if (_conn.IsCreated) { _driver.Disconnect(_conn); _driver.ScheduleFlushSend().Complete(); }
                _driver.Dispose();
            }
            _conn = default;
            LinkState = ChibiLinkState.Lost;
        }
    }
}
