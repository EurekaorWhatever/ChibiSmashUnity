using System;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>Datagram pipe to the remote peer. Receive returns the datagram length, or 0 when nothing is waiting.</summary>
    public interface IChibiTransport
    {
        void Send(byte[] data, int length);
        int Receive(byte[] buffer);
    }

    /// <summary>
    /// Source of local input. Kept as an interface so a camera/gesture source can be plugged in later:
    /// it only has to produce a ChibiInputPacket (the reserved bits are there for a gesture id).
    /// </summary>
    public interface IChibiInputSource
    {
        /// <summary>Called every render frame so very short taps are latched until the next simulation tick.</summary>
        void PollRenderFrame();
        /// <summary>Called exactly once per simulation tick that consumes a new local input.</summary>
        ChibiInputPacket SampleTick();
    }

    /// <summary>
    /// Rollback tuning that BOTH peers must share (it decides which frames are pre-seeded and how far prediction
    /// runs). Travels in the match handshake (<see cref="ChibiHandshake"/>) and is checked there, never assumed.
    /// </summary>
    public struct ChibiRollbackSettings
    {
        /// <summary>Frames of local input delay (2 = 33 ms). Trades a little latency for far fewer rollbacks.</summary>
        public int InputDelay;
        /// <summary>Most frames we simulate past the newest confirmed remote input before stalling (8 = 133 ms).</summary>
        public int MaxPrediction;
        /// <summary>
        /// Ticks without a NEW remote input frame before the opponent counts as disconnected (300 = 5 s). The stall
        /// window (<see cref="ChibiRollbackSession.StallWindowTicks"/>) is four times this. 60..<see cref="ChibiRollbackSession.MaxDisconnectTicks"/>.
        /// </summary>
        public int DisconnectTicks;

        /// <summary>Production defaults (rollback-gap-report.md 2.6: 150 ms RTT target, 3-5 s timeout).</summary>
        public static ChibiRollbackSettings Default => new ChibiRollbackSettings
        {
            InputDelay = ChibiRollbackSession.DefaultInputDelay,
            MaxPrediction = ChibiRollbackSession.DefaultMaxPrediction,
            DisconnectTicks = ChibiRollbackSession.DefaultDisconnectTicks,
        };

        /// <summary>Throws if a value would break the session's ring invariants.</summary>
        public void Validate()
        {
            if (InputDelay < 0 || InputDelay > 8) throw new ArgumentOutOfRangeException(nameof(InputDelay), "0..8");
            if (MaxPrediction < 1 || MaxPrediction > 15) throw new ArgumentOutOfRangeException(nameof(MaxPrediction), "1..15");
            if (DisconnectTicks < ChibiSimConst.TickRate) throw new ArgumentOutOfRangeException(nameof(DisconnectTicks), ">= 1 s");
            // NET-02: the host picks this value and the joiner must live with it; 16 bits on the wire would allow 18 minutes.
            if (DisconnectTicks > ChibiRollbackSession.MaxDisconnectTicks)
                throw new ArgumentOutOfRangeException(nameof(DisconnectTicks), "<= 10 s");
        }
    }

    /// <summary>Why a session stopped talking to its peer.</summary>
    public enum ChibiDisconnectReason : byte
    {
        /// <summary>
        /// No new remote input frame for <see cref="ChibiRollbackSettings.DisconnectTicks"/>. Datagrams that only repeat
        /// old frames do not count as life (NET-01).
        /// </summary>
        Timeout = 0,
        /// <summary>The peer said goodbye (quit, forfeit) on the control channel.</summary>
        RemoteLeft = 1,
        /// <summary>The transport itself lost the connection (Relay severed it, socket error).</summary>
        TransportLost = 2,
        /// <summary>
        /// The peer kept sending new input, but so slowly that we were stalled for more than two thirds of the stall
        /// window (NET-01). A bad link looks exactly like a deliberate drip-feed, so this is scored as no contest, not a win.
        /// </summary>
        Stalled = 3,
    }

    /// <summary>
    /// GGPO-style rollback session for 2-4 peers. Pure C# (no Unity types), so it can be unit tested headless.
    /// Input history and prediction live in <see cref="RollbackInputBuffer"/>, snapshots in <see cref="RollbackStateRing"/>.
    ///
    /// Frame model
    ///   * Frame is the next frame to simulate; State is the simulation at the START of Frame.
    ///   * Local input sampled during tick T is scheduled for frame (Frame + InputDelay) and sent at once.
    ///   * Remote input for a frame that has not arrived is PREDICTED as "same as their newest known input".
    ///     When the real input arrives and differs from what was simulated, we load the snapshot of the
    ///     first wrong frame and re-simulate up to the present in the same tick (rollback).
    ///   * We refuse to run more than MaxPrediction frames ahead of the newest input that is contiguous for EVERY remote.
    ///
    /// Topology (four-player-design.md 2.1, star): <c>links[s]</c> is the pipe to the peer of slot s. A guest has one link,
    /// to the host (slot 0). The host has one link per guest and forwards every player's inputs (from its own input
    /// ring, so it also repairs loss on the forwarded streams) and checksums to the other guests. The host is a dumb
    /// forwarder: every peer simulates from inputs only. A 1v1 is the same star with one guest, which is exactly the
    /// protocol 5 peer-to-peer exchange.
    ///
    /// Leaving (3-4 players): only the host declares a leave (<see cref="DeclareLeft"/>, its own timeout, or the stall
    /// limit on the slowest guest). It picks the first frame it has NOT received from that player, so every frame before it
    /// is already on every peer or still being forwarded; the frame travels in every host datagram, and each peer clears
    /// that player's presentMask bit from it on (rolling back if it had simulated past it).
    ///
    /// Netcode safety
    ///   * Every datagram re-sends the OLDEST unacknowledged inputs (up to 16) per stream, so loss of any length is
    ///     repaired without timers (gap report 2.1).
    ///   * An ack for a frame we never produced is impossible from an honest peer; such datagrams are dropped (2.8).
    ///     A guest may only send its own slot; the host only blocks for the other players.
    ///   * Every 30 confirmed frames each peer hashes the (now final) state; every peer compares its hash with every
    ///     remote's (the host forwards them). A mismatch raises OnDesync once.
    ///   * Peers exchange a frame-advantage estimate; the peer that is ahead gets TimeDilation &gt; 1 (slightly
    ///     longer ticks) so the simulations converge and rollbacks stay shallow.
    /// </summary>
    public sealed class ChibiRollbackSession
    {
        public const int RingSize = RollbackInputBuffer.RingSize;
        public const int DefaultInputDelay = 2;
        public const int DefaultMaxPrediction = 8;
        /// <summary>3-4 players (design 2.2): one more frame of delay for the star's extra hop.</summary>
        public const int GroupInputDelay = 3;
        /// <summary>3-4 players (design 2.2): prediction window for the star's extra hop.</summary>
        public const int GroupMaxPrediction = 10;
        public const int ChecksumInterval = 30;
        public const int DefaultDisconnectTicks = 300;   // 5 s of silence
        /// <summary>Largest disconnect timeout a handshake may ask for (NET-02): 10 s.</summary>
        public const int MaxDisconnectTicks = 600;
        const int Max = ChibiSimState.MaxFighters;

        /// <summary>Frame, local hash, remote hash. Raised at most once per session (a desync ends the match).</summary>
        public event Action<int, uint, uint> OnDesync;
        /// <summary>
        /// Raised at most once per session. 1v1: the opponent is gone. 3-4 players: a guest lost the host (or the guest was
        /// stalled); the host never raises it for a guest (it declares a leave instead). After it, the session keeps
        /// ticking harmlessly but should be ended.
        /// </summary>
        public event Action<ChibiDisconnectReason> OnDisconnect;
        /// <summary>Both inputs final, ascending frame order (1v1 only). Signature matches ChibiReplayRecorder.Add.</summary>
        public event Action<int, ChibiInputPacket, ChibiInputPacket> OnFrameConfirmed;
        /// <summary>Every player's final input for a frame, ascending order. The array is reused: copy what you keep.</summary>
        public event Action<int, ChibiInputPacket[]> OnFrameConfirmedAll;
        /// <summary>
        /// (from, to): the frames [from, to) were just re-simulated. Presentation code uses it to cancel one-shot effects
        /// spawned for frames that no longer happened (rollback-gap-report.md 2.5).
        /// </summary>
        public event Action<int, int> OnRollback;
        /// <summary>(slot, frame): a player left from that frame on (raised once per player, on every peer).</summary>
        public event Action<int, int> OnPlayerLeft;

        readonly int _local, _players;
        readonly bool _star;
        readonly IChibiTransport[] _links;
        readonly IChibiInputSource _source;
        readonly ChibiSimState _state = new ChibiSimState();
        readonly RollbackInputBuffer _inputs;
        readonly RollbackStateRing _snaps = new RollbackStateRing(RingSize);
        readonly ChibiRollbackSettings _settings;
        readonly ChibiInputPacket[] _stepIn = new ChibiInputPacket[Max];
        readonly ChibiInputPacket[] _confirmIn = new ChibiInputPacket[Max];

        int _frame;
        readonly int[] _ackOfMine = new int[Max];       // per link: newest local frame that peer holds contiguously
        readonly int[][] _fwdAck = new int[Max][];      // host: [guest][stream] newest forwarded frame the guest holds
        readonly int[] _remoteSeen = new int[Max];      // per stream: newest frame number seen
        readonly int[] _remoteAdv = new int[Max];       // per link: the peer's advantage estimate
        int _nextConfirmEmit;
        int _silentTicks;
        readonly int[] _silent = new int[Max];          // host (3-4 players): per guest
        int _tickCount;
        // NET-01 stall window: one flag per tick for the last StallWindowTicks ticks, and how many of them are set.
        readonly bool[] _stallRing;
        int _stalledInWindow;

        // RTT estimate: tick on which each local frame was first sent; sampled when an ack passes it.
        readonly int[] _sentTick = new int[RingSize];

        // desync detection: tiny rings keyed by (frame / interval) & 7; remote rings per slot
        readonly int[] _localHashFrame = new int[8];
        readonly uint[] _localHash = new uint[8];
        readonly int[][] _remoteHashFrame = new int[Max][];
        readonly uint[][] _remoteHash = new uint[Max][];
        readonly int[][] _comparedFrame = new int[Max][];   // NET-05: frame already compared in this slot (-1 = none)
        int _nextChecksumFrame = ChecksumInterval;
        int _latestHashFrame = -1;
        uint _latestHash;

        readonly BitBuffer _txBuf = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
        readonly BitBuffer _rxBuf = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
        readonly ChibiNetMessage _tx = new ChibiNetMessage(), _rx = new ChibiNetMessage();
        readonly byte[] _rxRaw = new byte[ChibiChannelMux.MaxDatagram];

        // ---- diagnostics (never part of sim state)
        public int RollbackCount { get; private set; }
        public int MaxRollbackDepth { get; private set; }
        public int ResimulatedFrames { get; private set; }
        public int StalledTicks { get; private set; }
        public int ChecksumsCompared { get; private set; }
        /// <summary>Datagrams that parsed but carried impossible values (forged ack, wrong slot, bad leave); dropped.</summary>
        public int RejectedPackets { get; private set; }
        public bool Desynced { get; private set; }
        public bool Disconnected { get; private set; }
        /// <summary>3-4 players, guest: the host announced that THIS player left (we were dropped).</summary>
        public bool LocallyDropped { get; private set; }
        /// <summary>True when the last Tick could not advance (waiting for a remote). Drive a "waiting" HUD from it.</summary>
        public bool IsStalled { get; private set; }
        /// <summary>Ticks since the remote input last advanced (0 while the link is healthy).</summary>
        public int SilentTicks => _silentTicks;
        /// <summary>Length of the stall window in ticks: 4 x DisconnectTicks (1200 = 20 s by default).</summary>
        public int StallWindowTicks => _stallRing.Length;
        /// <summary>
        /// Stalled ticks allowed inside the window: two thirds of it (800 by default). One more raises Stalled (1v1) or
        /// drops the slowest guest (3-4 players, host). A 3-4 player guest waits five sixths, so the host decides first.
        /// Measured on the loopback link (2026-11-16, 60 s, share of the window stalled): 300 ms RTT + 15 % loss 26 %,
        /// 400 ms 33 %, 500 ms 47 %, 500 ms + 10 % loss 52 %, 670 ms 60 %, 1 s 73 %.
        /// </summary>
        public int StallLimit => _star && _local != 0 ? _stallRing.Length * 5 / 6 : _stallRing.Length * 2 / 3;
        /// <summary>Ticks inside the last <see cref="StallWindowTicks"/> that could not advance (NET-01).</summary>
        public int StalledInWindow => _stalledInWindow;
        /// <summary>Datagrams accepted from peers (diagnostics; a stale datagram counts, so it proves nothing about play).</summary>
        public int AcceptedPackets { get; private set; }
        /// <summary>
        /// True once at least one REAL remote input frame arrived from any remote (frames below InputDelay are pre-seeded,
        /// not sent). False means no peer ever played a single tick with us (NET-03).
        /// </summary>
        public bool HasRemoteInput
        {
            get
            {
                for (int p = 0; p < _players; p++) if (p != _local && _inputs.ContiguousOf(p) >= _settings.InputDelay) return true;
                return false;
            }
        }
        /// <summary>True once player <paramref name="slot"/> delivered at least one real input frame.</summary>
        public bool HasInputFrom(int slot) => _inputs.ContiguousOf(slot) >= _settings.InputDelay;
        /// <summary>Smoothed round trip in ticks, x8 fixed point (divide by 8). -1 until the first sample.</summary>
        public int RttTicksX8 { get; private set; } = -1;
        public float TimeDilation { get; private set; } = 1f;

        public ChibiSimState State => _state;
        public int Frame => _frame;
        public int LocalIndex => _local;
        /// <summary>Players in the match (2..4).</summary>
        public int Players => _players;
        public ChibiRollbackSettings Settings => _settings;
        public int ConfirmedFrame => Math.Min(_frame, _inputs.RemoteContiguous + 1);
        /// <summary>Leave frame of a player, or <see cref="RollbackInputBuffer.Never"/>.</summary>
        public int LeftAt(int slot) => _inputs.LeftAt(slot);

        /// <summary>
        /// The state at the start of <see cref="ConfirmedFrame"/>: every input before it is final, so no rollback can
        /// change it. Use it (not <see cref="State"/>, which may be predicted) for anything that must be final, such as
        /// declaring the winner. Null only if the snapshot fell out of the ring (cannot happen while Validate holds). Do not mutate.
        /// </summary>
        public ChibiSimState ConfirmedState
        {
            get
            {
                int c = ConfirmedFrame;
                return c == _frame ? _state : _snaps.Peek(c);
            }
        }

        /// <summary>How far we are ahead of the slowest live remote we have seen.</summary>
        public int LocalFrameAdvantage
        {
            get
            {
                int seen = int.MaxValue;
                for (int p = 0; p < _players; p++)
                    if (p != _local && _inputs.LeftAt(p) == RollbackInputBuffer.Never && _remoteSeen[p] < seen) seen = _remoteSeen[p];
                if (seen == int.MaxValue) return 0;
                return _frame - Math.Max(0, seen - _settings.InputDelay);
            }
        }

        /// <summary>1v1 session with the production <see cref="ChibiRollbackSettings.Default"/>.</summary>
        public ChibiRollbackSession(int localIndex, in ChibiMatchConfig cfg, IChibiTransport net, IChibiInputSource source)
            : this(localIndex, cfg, net, source, ChibiRollbackSettings.Default) { }

        /// <summary>
        /// Session over ONE link: a 1v1 (either side), or a 3-4 player guest whose link goes to the host.
        /// Settings must equal the peers' (they travel in the handshake).
        /// </summary>
        /// <example><code>var s = new ChibiRollbackSession(0, cfg, transport, input, ChibiRollbackSettings.Default);</code></example>
        public ChibiRollbackSession(int localIndex, in ChibiMatchConfig cfg, IChibiTransport net, IChibiInputSource source,
                                    ChibiRollbackSettings settings)
            : this(localIndex, cfg, OneLink(localIndex, net), source, settings) { }

        static IChibiTransport[] OneLink(int localIndex, IChibiTransport net)
        {
            if (net == null) throw new ArgumentNullException(nameof(net));
            var links = new IChibiTransport[Max];
            links[localIndex == 0 ? 1 : 0] = net;
            return links;
        }

        /// <summary>
        /// Star session: <paramref name="links"/>[s] is the pipe to slot s (length &gt;= players). The host (slot 0) passes
        /// one link per guest; a guest passes only links[0]. A null guest link on the host is a guest that never connected.
        /// </summary>
        /// <example><code>var host = new ChibiRollbackSession(0, cfg4, new[] { null, toP1, toP2, toP3 }, input, settings);</code></example>
        public ChibiRollbackSession(int localIndex, in ChibiMatchConfig cfg, IChibiTransport[] links, IChibiInputSource source,
                                    ChibiRollbackSettings settings)
        {
            settings.Validate();
            _settings = settings;
            _players = cfg.Players;
            if (localIndex < 0 || localIndex >= _players) throw new ArgumentOutOfRangeException(nameof(localIndex));
            if (links == null || links.Length < _players) throw new ArgumentException("one link slot per player", nameof(links));
            _local = localIndex;
            _star = _players > 2;
            _links = new IChibiTransport[Max];
            for (int s = 0; s < _players; s++)
            {
                if (s == localIndex) continue;
                if (localIndex != 0 && s != 0) continue;   // a guest talks to the host only
                _links[s] = links[s];
            }
            if (localIndex != 0 && _links[0] == null) throw new ArgumentNullException(nameof(links), "a guest needs links[0]");
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _inputs = new RollbackInputBuffer(localIndex, settings.InputDelay, _players);
            _stallRing = new bool[settings.DisconnectTicks * 4];
            for (int i = 0; i < 8; i++) _localHashFrame[i] = -1;
            for (int s = 0; s < Max; s++)
            {
                _ackOfMine[s] = -1;
                _remoteSeen[s] = -1;
                _fwdAck[s] = new int[Max];
                _remoteHashFrame[s] = new int[8];
                _remoteHash[s] = new uint[8];
                _comparedFrame[s] = new int[8];
                for (int i = 0; i < Max; i++) _fwdAck[s][i] = -1;
                for (int i = 0; i < 8; i++) _remoteHashFrame[s][i] = _comparedFrame[s][i] = -1;
            }
            for (int i = 0; i < RingSize; i++) _sentTick[i] = -1;
            ChibiSimulation.Init(_state, cfg);
        }

        // ================================================================== one 60 Hz tick

        /// <summary>Run one network+simulation tick. Returns true if the simulation advanced (false = stalled).</summary>
        public bool Tick()
        {
            _tickCount++;
            PumpNetwork();

            int wrong = _inputs.TakeFirstMisprediction();
            if (wrong >= 0) Rollback(wrong);

            bool advanced = false;
            if (_frame - _inputs.RemoteContiguous <= _settings.MaxPrediction)
            {
                var sample = new ChibiInputPacket { Raw = (ushort)(_source.SampleTick().Raw & ChibiInputPacket.WireMask) }.Sanitized();
                _inputs.AddLocal(sample.Raw);
                Advance();
                advanced = true;
            }
            else StalledTicks++;   // too far ahead of the remotes' confirmed input: wait for it
            IsStalled = !advanced;
            int slot = _tickCount % _stallRing.Length;
            if (_stallRing[slot]) _stalledInWindow--;
            _stallRing[slot] = !advanced;
            if (!advanced) _stalledInWindow++;

            EmitConfirmedFrames();
            RunChecksums();
            SendPackets();

            if (_star && _local == 0) HostWatch();
            else if (++_silentTicks > _settings.DisconnectTicks) RaiseDisconnect(ChibiDisconnectReason.Timeout);
            else if (_stalledInWindow > StallLimit) RaiseDisconnect(ChibiDisconnectReason.Stalled);
            return advanced;
        }

        /// <summary>Host with 3-4 players: a silent guest, or the slowest guest past the stall limit, is declared gone.</summary>
        void HostWatch()
        {
            // H1 (review 2026-11-24): a guest that waits for a silent guest stops sending new frames too (it stalls). Only
            // the guest(s) holding everyone back - the lowest contiguous frame - count as silent, so at high RTT an innocent
            // guest is never dropped along with the silent one.
            int lowest = int.MaxValue;
            for (int g = 1; g < _players; g++)
                if (_inputs.LeftAt(g) == RollbackInputBuffer.Never && _inputs.ContiguousOf(g) < lowest) lowest = _inputs.ContiguousOf(g);
            for (int g = 1; g < _players; g++)
            {
                if (_inputs.LeftAt(g) != RollbackInputBuffer.Never) continue;
                if (_inputs.ContiguousOf(g) > lowest) { _silent[g] = 0; continue; }
                if (++_silent[g] > _settings.DisconnectTicks) DeclareLeft(g);
            }
            if (_stalledInWindow > StallLimit)
            {
                int slow = -1;
                for (int g = 1; g < _players; g++)
                    if (_inputs.LeftAt(g) == RollbackInputBuffer.Never && (slow < 0 || _inputs.ContiguousOf(g) < _inputs.ContiguousOf(slow))) slow = g;
                if (slow > 0) DeclareLeft(slow);
                Array.Clear(_stallRing, 0, _stallRing.Length);
                _stalledInWindow = 0;
            }
        }

        /// <summary>
        /// Host, 3-4 players: player <paramref name="slot"/> is gone (quit, timed out, link lost, too slow). The leave frame is
        /// the first frame the host does not hold for that player; every host datagram carries it from now on. Returns the
        /// frame, or -1 when it cannot be declared here (not the host of a 3-4 player match, bad slot, already gone).
        /// </summary>
        /// <example><code>int f = hostSession.DeclareLeft(2);</code></example>
        public int DeclareLeft(int slot)
        {
            if (!_star || _local != 0 || slot <= 0 || slot >= _players) return -1;
            if (_inputs.LeftAt(slot) != RollbackInputBuffer.Never) return -1;
            int f = Math.Max(_inputs.ContiguousOf(slot) + 1, _settings.InputDelay);
            if (!_inputs.SetLeft(slot, f, _frame)) return -1;
            // The others were waiting for this player: their silence and stalls were not their fault (H1).
            for (int g = 1; g < _players; g++) _silent[g] = 0;
            ResetStallWindow();
            OnPlayerLeft?.Invoke(slot, f);
            ReviewPendingChecks();   // SEC-007: a check that waited for this player decides now
            return f;
        }

        /// <summary>
        /// Forget the stall window (and the silence count): the stalls were caused by a player who has now left. Done on the
        /// host AND on every guest when a leave is applied (SEC-008), so two players cannot add their stalls up to force a
        /// no contest after the host already removed the slow one.
        /// </summary>
        void ResetStallWindow()
        {
            Array.Clear(_stallRing, 0, _stallRing.Length);
            _stalledInWindow = 0;
            _silentTicks = 0;
        }

        /// <summary>
        /// Report a disconnect seen outside the input stream: a goodbye on the control channel or a transport loss.
        /// Raises <see cref="OnDisconnect"/> (once).
        /// </summary>
        public void NotifyDisconnected(ChibiDisconnectReason reason) => RaiseDisconnect(reason);

        void RaiseDisconnect(ChibiDisconnectReason reason)
        {
            if (Disconnected) return;
            Disconnected = true;
            OnDisconnect?.Invoke(reason);
        }

        void RaiseDesync(int frame, uint local, uint remote)
        {
            if (Desynced) return;
            Desynced = true;
            OnDesync?.Invoke(frame, local, remote);
        }

        // ================================================================== simulation stepping

        /// <summary>Snapshot the current state, gather every input for Frame, and step. Used by ticks and by rollback.</summary>
        void Advance()
        {
            _snaps.Save(_frame, _state);
            for (int p = 0; p < _players; p++) _stepIn[p] = new ChibiInputPacket { Raw = _inputs.Get(p, _frame) };
            int mask = _inputs.PresentMask(_frame);
            if (_players == 2 && mask == 3) ChibiSimulation.Step(_state, _stepIn[0], _stepIn[1]);   // 1v1: exactly as before
            else ChibiSimulation.StepPresent(_state, new ReadOnlySpan<ChibiInputPacket>(_stepIn, 0, _players), mask);
            _frame++;
        }

        void Rollback(int from)
        {
            int target = _frame;
            if (from >= target) return;
            if (!_snaps.TryLoad(from, _state))
            {
                // Unreachable while MaxPrediction < RingSize (Validate); if it ever happens the sims can no longer
                // be proven equal, so end the match the same way a checksum mismatch would (gap report A6).
                RaiseDesync(from, 0, 0);
                return;
            }
            _frame = from;
            while (_frame < target) Advance();   // re-simulates with the corrected inputs, re-saving snapshots

            RollbackCount++;
            ResimulatedFrames += target - from;
            if (target - from > MaxRollbackDepth) MaxRollbackDepth = target - from;
            OnRollback?.Invoke(from, target);
        }

        // ================================================================== receive

        void PumpNetwork()
        {
            int before = _inputs.RemoteContiguous;
            for (int l = 0; l < _players; l++)
            {
                var net = _links[l];
                if (net == null) continue;
                int contigBefore = _inputs.ContiguousOf(l);
                bool fresh = false;
                while (true)
                {
                    int n = net.Receive(_rxRaw);
                    if (n <= 0) break;
                    _rxBuf.LoadFrom(_rxRaw, n);
                    if (!ChibiPacketCodec.TryRead(_rxBuf, _rx)) continue;   // garbage / truncated / foreign datagram
                    if (!Process(_rx, l)) { RejectedPackets++; continue; }
                    AcceptedPackets++;
                    fresh = true;
                }
                if (_star && _local == 0 && _inputs.ContiguousOf(l) > contigBefore) _silent[l] = 0;
                // A 3-4 player guest hears every player through the host: the host stalls while waiting for a slow guest,
                // so its own stream may not move. Any accepted host datagram proves the host is there; the host decides
                // about the slow guest, and the stall limit still ends a frozen match.
                if (_star && _local != 0 && fresh) _silentTicks = 0;
            }
            // NET-01: only a NEW input frame proves a 1v1 peer is still playing. A well-formed datagram that repeats old
            // frames used to reset this, so a losing peer could freeze the match forever without ever timing out.
            if (!_star && _inputs.RemoteContiguous > before) _silentTicks = 0;
        }

        /// <summary>Apply one parsed datagram from link <paramref name="link"/>. Returns false (and changes nothing) if it is
        /// impossible from an honest peer.</summary>
        bool Process(ChibiNetMessage m, int link)
        {
            // An honest peer can only ack frames we have produced and sent (gap report 2.8).
            if (m.AckFrame > _inputs.LocalLast) return false;

            if (!_star)
            {
                // 1v1: one remote; the slot is redundant (the link says who it is), so only block 0 is read.
                AcceptBlock(m.Blocks[0], 1 - _local);
            }
            else if (_local == 0)
            {
                // Host: a guest may only speak for itself (no forged inputs for another player) and never announces leaves.
                if (m.BlockCount != 1 || m.Slot != link || m.LeaveMask != 0) return false;
                for (int s = 1; s < _players; s++)
                    if ((m.ExtraAckMask & (1 << s)) != 0 && s != link && m.ExtraAck[s] > _inputs.ContiguousOf(s)) return false;
                if (_inputs.LeftAt(link) != RollbackInputBuffer.Never) return true;   // already gone: ignore quietly
                for (int s = 1; s < _players; s++)
                    if ((m.ExtraAckMask & (1 << s)) != 0 && s != link && m.ExtraAck[s] > _fwdAck[link][s]) _fwdAck[link][s] = m.ExtraAck[s];
                AcceptBlock(m.Blocks[0], link);
            }
            else
            {
                // Guest: blocks for the other players, all through the host; never one for us.
                if (m.ExtraAckMask != 0) return false;
                for (int k = 0; k < m.BlockCount; k++)
                    if (m.Blocks[k].Slot == _local || m.Blocks[k].Slot >= _players) return false;
                if ((m.LeaveMask & ~((1 << _players) - 1)) != 0 || (m.LeaveMask & 1) != 0) return false;
                // Validate every leave first, then apply: a rejected datagram changes nothing (review nit 2026-11-24).
                for (int s = 1; s < _players; s++)
                    if ((m.LeaveMask & (1 << s)) != 0 && s != _local && !_inputs.CanSetLeft(s, m.LeaveFrame[s])) return false;
                for (int s = 1; s < _players; s++)
                {
                    if ((m.LeaveMask & (1 << s)) == 0) continue;
                    if (s == _local) { if (!LocallyDropped) { LocallyDropped = true; RaiseDisconnect(ChibiDisconnectReason.RemoteLeft); } continue; }
                    bool known = _inputs.LeftAt(s) != RollbackInputBuffer.Never;
                    _inputs.SetLeft(s, m.LeaveFrame[s], _frame);
                    if (known) continue;
                    ResetStallWindow();   // SEC-008: our stalls waited for that player
                    OnPlayerLeft?.Invoke(s, m.LeaveFrame[s]);
                }
                for (int k = 0; k < m.BlockCount; k++) AcceptBlock(m.Blocks[k], m.Blocks[k].Slot);
            }

            _remoteAdv[link] = m.Advantage;
            if (m.AckFrame > _ackOfMine[link])
            {
                int sent = _sentTick[m.AckFrame & (RingSize - 1)];
                if (sent >= 0) SampleRtt(_tickCount - sent);
                _ackOfMine[link] = m.AckFrame;
            }
            return true;
        }

        void AcceptBlock(ChibiInputBlock b, int slot)
        {
            // NET-04: sanity bound only. An honest LastFrame is at most MaxInputs past what we hold contiguously (the
            // window starts at our ack + 1), so anything further is ignored instead of dragging LocalFrameAdvantage far negative.
            if (b.LastFrame > _remoteSeen[slot] && b.LastFrame <= _inputs.ContiguousOf(slot) + ChibiNetMessage.MaxInputs)
                _remoteSeen[slot] = b.LastFrame;
            int firstFrame = b.LastFrame - b.Count + 1;
            for (int k = 0; k < b.Count; k++)
                _inputs.AddRemote(slot, firstFrame + k, b.Inputs[k].Raw, _frame);
            if (b.HasChecksum) AcceptRemoteChecksum(slot, b.ChecksumFrame, b.Checksum);
        }

        void SampleRtt(int ticks)
        {
            int x8 = ticks * 8;
            RttTicksX8 = RttTicksX8 < 0 ? x8 : RttTicksX8 + (x8 - RttTicksX8) / 8;   // EWMA, alpha 1/8
        }

        // ================================================================== send

        void SendPackets()
        {
            int localAdv = LocalFrameAdvantage;
            int lead = 0;
            for (int l = 0; l < _players; l++)
            {
                if (_links[l] == null) continue;
                // Both peers estimate "how far ahead am I of what I see of you". The difference is twice the true lead.
                int ld = (localAdv - _remoteAdv[l]) / 2;
                if (_inputs.LeftAt(l) == RollbackInputBuffer.Never && ld > lead) lead = ld;
                SendTo(l, localAdv);
            }
            TimeDilation = 1f + Math.Min(4, Math.Max(0, lead)) * 0.02f;
        }

        void SendTo(int link, int localAdv)
        {
            int last = _inputs.LocalLast;
            // Window = the OLDEST unacknowledged inputs (gap report 2.1 fix a + b). The frame the peer is missing is
            // therefore in every packet until acked, so any burst of loss heals as soon as one datagram gets through.
            int first = _ackOfMine[link] + 1;
            if (first > last) first = last;                          // all acked: resend the newest as a keep-alive
            if (first < 0) first = 0;
            // NET-06: a peer that never acks (honest peers cannot fall this far behind: both sides stall first) must not
            // walk the window out of the input ring, where LocalInput throws and Tick would never finish again.
            if (first < last - (RingSize - 1)) first = last - (RingSize - 1);
            int count = last - first + 1;
            if (count > ChibiNetMessage.MaxInputs) count = ChibiNetMessage.MaxInputs;

            var own = _tx.Blocks[0];
            own.Slot = _local;
            own.LastFrame = first + count - 1;
            own.Count = count;
            for (int k = 0; k < count; k++)
                own.Inputs[k] = new ChibiInputPacket { Raw = _inputs.LocalInput(first + k) };
            // RTT bookkeeping: remember the tick each frame was FIRST sent (resends keep the original time).
            for (int f = Math.Max(_newestSent + 1, first); f <= own.LastFrame; f++) _sentTick[f & (RingSize - 1)] = _tickCount;
            if (own.LastFrame > _newestSent) _newestSent = own.LastFrame;
            own.HasChecksum = _latestHashFrame >= 0;
            own.ChecksumFrame = _latestHashFrame;
            own.Checksum = _latestHash;
            _tx.BlockCount = 1;
            _tx.AckFrame = _star ? _inputs.ContiguousOf(link) : _inputs.RemoteContiguous;
            _tx.Advantage = localAdv;
            _tx.ExtraAckMask = 0;
            _tx.LeaveMask = 0;

            if (_star && _local == 0)
            {
                for (int s = 1; s < _players; s++)
                {
                    if (s == link) continue;
                    AddForwardBlock(s, link);
                    if (_inputs.LeftAt(s) != RollbackInputBuffer.Never) { _tx.LeaveMask |= 1 << s; _tx.LeaveFrame[s] = _inputs.LeftAt(s); }
                }
                if (_inputs.LeftAt(link) != RollbackInputBuffer.Never) { _tx.LeaveMask |= 1 << link; _tx.LeaveFrame[link] = _inputs.LeftAt(link); }
            }
            else if (_star)
            {
                for (int s = 1; s < _players; s++)
                {
                    if (s == _local) continue;
                    _tx.ExtraAckMask |= 1 << s;
                    _tx.ExtraAck[s] = Math.Min(_inputs.ContiguousOf(s), Math.Max(-1, _inputs.LeftAt(s) - 1));
                }
            }

            _txBuf.Clear();
            // Write throws InvalidOperationException on overrun; deliberately not caught, so a too-large
            // message surfaces as an error instead of a silently truncated datagram.
            ChibiPacketCodec.Write(_txBuf, _tx);
            _links[link].Send(_txBuf.Bytes, _txBuf.ByteLength);
        }

        /// <summary>Host: append player <paramref name="s"/>'s actual inputs (oldest the receiving guest lacks) to the datagram.</summary>
        void AddForwardBlock(int s, int link)
        {
            int last = Math.Min(_inputs.ContiguousOf(s), _inputs.LeftAt(s) - 1);
            if (last < 0) return;
            int first = _fwdAck[link][s] + 1;
            if (first > last) first = last;
            if (first < 0) first = 0;
            if (first < last - (RingSize / 2 - 1)) first = last - (RingSize / 2 - 1);
            int count = last - first + 1;
            if (count > ChibiNetMessage.MaxInputs) count = ChibiNetMessage.MaxInputs;
            for (int f = first; f < first + count; f++) if (!_inputs.IsActual(s, f)) return;   // never forward a guess
            var b = _tx.Blocks[_tx.BlockCount++];
            b.Slot = s;
            b.LastFrame = first + count - 1;
            b.Count = count;
            for (int k = 0; k < count; k++) b.Inputs[k] = new ChibiInputPacket { Raw = _inputs.Confirmed(s, first + k) };
            // M3 / SEC-002: guests compare only with the host; the host compares with every guest and decides. A guest's
            // checksum is therefore never forwarded (one forged checksum cannot void the others' match).
            b.HasChecksum = false;
        }

        int _newestSent = -1;       // newest local frame ever put on the wire

        // ================================================================== confirmation, replay hook, desync check

        void EmitConfirmedFrames()
        {
            // A frame's inputs are final once every remote input is actual (local always is).
            while (_nextConfirmEmit <= _inputs.RemoteContiguous && _nextConfirmEmit <= _inputs.LocalLast)
            {
                int f = _nextConfirmEmit++;
                for (int p = 0; p < _players; p++) _confirmIn[p] = new ChibiInputPacket { Raw = _inputs.Confirmed(p, f) };
                if (_players == 2) OnFrameConfirmed?.Invoke(f, _confirmIn[0], _confirmIn[1]);
                OnFrameConfirmedAll?.Invoke(f, _confirmIn);
            }
        }

        void RunChecksums()
        {
            // State at the start of frame c is final once c <= RemoteContiguous + 1 (all earlier inputs are actual).
            while (_nextChecksumFrame <= ConfirmedFrame)
            {
                int c = _nextChecksumFrame;
                _nextChecksumFrame += ChecksumInterval;

                uint hash;
                if (c == _frame) hash = _state.ComputeChecksum();
                else
                {
                    var snap = _snaps.Peek(c);
                    if (snap == null) continue;
                    hash = snap.ComputeChecksum();
                }

                int idx = (c / ChecksumInterval) & 7;
                _localHashFrame[idx] = c;
                _localHash[idx] = hash;
                _latestHashFrame = c;
                _latestHash = hash;
                for (int s = 0; s < _players; s++) if (Compares(s)) CompareChecksum(s, idx);
            }
        }

        /// <summary>Whose checksums this peer compares: 1v1 the opponent; star host every guest; star guest the host only.</summary>
        bool Compares(int slot) => slot != _local && slot < _players && (!_star || _local == 0 || slot == 0);

        void AcceptRemoteChecksum(int slot, int frame, uint hash)
        {
            if (frame <= 0 || frame % ChecksumInterval != 0 || !Compares(slot)) return;
            // M1: a player who left keeps simulating without its own leave; from its leave frame on its state differs.
            if (frame >= _inputs.LeftAt(slot)) return;
            int idx = (frame / ChecksumInterval) & 7;
            if (_remoteHashFrame[slot][idx] == frame) return;           // already recorded
            if (_comparedFrame[slot][idx] == frame) return;             // NET-05: already compared (every datagram repeats it)
            _remoteHashFrame[slot][idx] = frame;
            _remoteHash[slot][idx] = hash;
            CompareChecksum(slot, idx);
        }

        void CompareChecksum(int slot, int idx)
        {
            if (_localHashFrame[idx] < 0 || _localHashFrame[idx] != _remoteHashFrame[slot][idx]) return;
            if (_localHashFrame[idx] >= _inputs.LeftAt(slot)) return;   // M1 (the leave arrived after the checksum)
            ChecksumsCompared++;
            _comparedFrame[slot][idx] = _localHashFrame[idx];
            bool same = _localHash[idx] == _remoteHash[slot][idx];
            uint remote = _remoteHash[slot][idx];
            _remoteHashFrame[slot][idx] = -2;   // consumed
            if (_star && _local == 0) Arbitrate(slot, idx, same, remote);
            else if (!same) RaiseDesync(_localHashFrame[idx], _localHash[idx], remote);
        }

        // M3 / SEC-002: the host's verdict per checksum frame (bit per guest that matched / did not).
        readonly int[] _arbFrame = { -1, -1, -1, -1, -1, -1, -1, -1 };
        readonly int[] _arbOk = new int[8], _arbBad = new int[8];

        /// <summary>
        /// Host of a 3-4 player match. Once every live guest's checksum for a frame is in: if exactly ONE guest disagrees
        /// while another agrees with the host, that guest is the odd one out (a forged checksum or a broken client) and is
        /// dropped like a leaver; the match goes on. Any other disagreement (the host itself is the odd one out, or two
        /// guests disagree) ends the match as a desync, as before.
        /// </summary>
        void Arbitrate(int slot, int idx, bool same, uint remote)
        {
            int f = _localHashFrame[idx];
            if (_arbFrame[idx] != f) { _arbFrame[idx] = f; _arbOk[idx] = 0; _arbBad[idx] = 0; _arbDone[idx] = false; }
            if (same) _arbOk[idx] |= 1 << slot;
            else { _arbBad[idx] |= 1 << slot; _arbRemote[idx] = remote; }
            Decide(idx);
        }

        readonly bool[] _arbDone = new bool[8];
        readonly uint[] _arbRemote = new uint[8];

        /// <summary>Decide one checksum frame once every LIVE guest reported it (a guest that left no longer counts).</summary>
        void Decide(int idx)
        {
            if (_arbFrame[idx] < 0 || _arbDone[idx]) return;
            int live = 0;
            for (int g = 1; g < _players; g++) if (_inputs.LeftAt(g) == RollbackInputBuffer.Never) live |= 1 << g;
            if (((_arbOk[idx] | _arbBad[idx]) & live) != live) return;   // wait for the others
            _arbDone[idx] = true;
            int bad = _arbBad[idx] & live;
            if (bad == 0) return;
            if ((bad & (bad - 1)) == 0 && (_arbOk[idx] & live) != 0)
            {
                int odd = 0;
                while ((bad & (1 << odd)) == 0) odd++;
                ChecksumDrops++;
                DeclareLeft(odd);
                return;
            }
            RaiseDesync(_arbFrame[idx], _localHash[idx], _arbRemote[idx]);
        }

        /// <summary>SEC-007: after a leave, any checksum frame that only waited for that player is decided at once.</summary>
        void ReviewPendingChecks()
        {
            if (!_star || _local != 0) return;
            for (int i = 0; i < 8; i++) Decide(i);
        }

        /// <summary>Host, 3-4 players: guests dropped because only their checksum disagreed (M3 / SEC-002).</summary>
        public int ChecksumDrops { get; private set; }
    }

    /// <summary>
    /// Unity host for a rollback session. Drives it from a time accumulator so the simulation runs at exactly
    /// 60 ticks/second regardless of render framerate (it deliberately does NOT use FixedUpdate, whose rate is
    /// a project setting shared with Unity physics). Rendering reads Session.State; RenderAlpha is the
    /// fraction of the next tick that has elapsed, for visual interpolation.
    /// </summary>
    public sealed class ChibiP2PPeerLoop : MonoBehaviour
    {
        public const float TickSeconds = ChibiTickClock.TickSeconds;

        public ChibiRollbackSession Session { get; private set; }
        public ChibiReplayRecorder Replay { get; private set; }
        public float RenderAlpha => _clock.RenderAlpha;

        IChibiInputSource _source;
        IChibiTransport _transport;
        readonly ChibiTickClock _clock = new ChibiTickClock();
        Func<float> _dilation;   // cached once in Begin so Update allocates nothing
        Action _tick;

        public void Begin(int localIndex, in ChibiMatchConfig cfg, IChibiTransport transport, IChibiInputSource source, bool recordReplay = true)
            => Begin(localIndex, cfg, transport, source, ChibiRollbackSettings.Default, recordReplay);

        /// <summary>Start a session with settings agreed in the handshake.</summary>
        public void Begin(int localIndex, in ChibiMatchConfig cfg, IChibiTransport transport, IChibiInputSource source,
                          ChibiRollbackSettings settings, bool recordReplay = true)
        {
            _transport = transport;
            _source = source;
            Session = new ChibiRollbackSession(localIndex, cfg, transport, source, settings);
            if (recordReplay)
            {
                Replay = new ChibiReplayRecorder(cfg);
                Session.OnFrameConfirmed += Replay.Add;
            }
            _dilation = () => Session.TimeDilation;
            _tick = () => Session.Tick();
            _clock.Reset();
        }

        void Update()
        {
            if (Session == null) return;
            _source.PollRenderFrame();

            _clock.Run(Time.unscaledDeltaTime, _dilation, _tick);   // re-reads TimeDilation after every tick
        }

        void OnDestroy()
        {
            (_transport as IDisposable)?.Dispose();
        }
    }
}
