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
        /// <summary>
        /// Team play (Count &gt; 2): a drop vote did not become unanimous within MaxDisconnectTicks, or the survivors are
        /// not a strict majority of the roster (we are on the losing side of a partition, or lost every link). Scored as
        /// a no contest for us.
        /// </summary>
        VoteFailed = 4,
        /// <summary>
        /// Team play: a peer sent something an honest peer cannot (a drop vote below the frame it had already reported
        /// holding, a drop that would rewrite a confirmed frame). No contest; the slot is in
        /// <see cref="ChibiRollbackSession.CulpritSlot"/>.
        /// </summary>
        ProtocolViolation = 5,
    }

    /// <summary>
    /// GGPO-style rollback session for 2..4 peers in a full mesh (protocol 6). Pure C# (no Unity types), so it can be
    /// unit tested headless. Input history and prediction live in <see cref="RollbackInputBuffer"/>, snapshots in
    /// <see cref="RollbackStateRing"/>.
    ///
    /// Frame model
    ///   * Frame is the next frame to simulate; State is the simulation at the START of Frame.
    ///   * Local input sampled during tick T is scheduled for frame (Frame + InputDelay) and sent at once to every link.
    ///   * Remote input for a frame that has not arrived is PREDICTED as "same as that remote's newest known input".
    ///     When the real input arrives and differs from what was simulated, we load the snapshot of the
    ///     first wrong frame and re-simulate up to the present in the same tick (rollback).
    ///   * We refuse to run more than MaxPrediction frames ahead of the newest input that is contiguous for EVERY remote.
    ///
    /// Topology: one <see cref="IChibiTransport"/> per remote slot (links[slot], null at the local slot). Each link is bound
    /// to exactly that slot: a datagram whose Sender field is not the link's slot is rejected (security BLOCKER 1), and
    /// the inputs it carries are only ever stored for that slot.
    ///
    /// Netcode safety
    ///   * Every datagram re-sends the OLDEST inputs that link has not acknowledged (up to 16), so loss of any length is
    ///     repaired without timers (gap report 2.1). Acks, frame advantage, silence and stall are all per link.
    ///   * An ack for a frame we never produced is impossible from an honest peer; such datagrams are dropped (2.8).
    ///   * Every 30 confirmed frames each peer hashes the (now final) state and ships it; a mismatch with ANY peer raises
    ///     OnDesync once (the match then ends as a no contest for everyone).
    ///   * The peer that is ahead of its furthest-behind remote gets TimeDilation &gt; 1 so the simulations converge.
    ///
    /// Leavers (Count &gt; 2). A remote that times out, stalls past the window, says goodbye or loses its transport is
    /// voted out: we freeze the last frame we hold contiguously from it, stop talking to it, and repeat the vote in every
    /// datagram. Once every survivor's vote set equals the same target set, each target is dropped at
    /// min(votes) + 1 on every survivor (<see cref="RollbackInputBuffer.DropRemote"/>, which rolls back if needed) and the
    /// simulation receives <see cref="ChibiInputPacket.DroppedRaw"/> for it from that frame. Survivors stall meanwhile;
    /// a vote that is not unanimous within <see cref="MaxDisconnectTicks"/>, or survivors that are not a strict majority
    /// of the roster (a partition), end the session with <see cref="ChibiDisconnectReason.VoteFailed"/>. Votes naming
    /// ourselves are ignored, so a lying peer can only cause a no contest, never kick an opponent. With Count == 2 there
    /// is no vote: a lost remote raises <see cref="OnDisconnect"/> exactly as before.
    /// </summary>
    public sealed class ChibiRollbackSession
    {
        public const int RingSize = RollbackInputBuffer.RingSize;
        public const int DefaultInputDelay = 2;
        public const int DefaultMaxPrediction = 8;
        public const int ChecksumInterval = 30;
        public const int DefaultDisconnectTicks = 300;   // 5 s of silence
        /// <summary>Largest disconnect timeout a handshake may ask for (NET-02): 10 s. Also the drop-vote deadline.</summary>
        public const int MaxDisconnectTicks = 600;
        const int MaxPeers = RollbackInputBuffer.MaxPlayers;

        /// <summary>Frame, local hash, remote hash. Raised at most once per session (a desync ends the match).</summary>
        public event Action<int, uint, uint> OnDesync;
        /// <summary>
        /// Raised at most once per session: the single remote of a 1v1 is gone, or (Count &gt; 2) the drop vote failed /
        /// we lost the majority. After it, the session keeps ticking harmlessly but should be ended.
        /// </summary>
        public event Action<ChibiDisconnectReason> OnDisconnect;
        /// <summary>(slot, reason): a remote stopped being heard. Raised at most once per remote, before any vote.</summary>
        public event Action<int, ChibiDisconnectReason> OnPeerLost;
        /// <summary>(slot, dropFrame): the survivors agreed to drop that remote from that frame on (Count &gt; 2 only).</summary>
        public event Action<int, int> OnPeerDropped;
        /// <summary>Both inputs final, ascending frame order (Count == 2 only). Signature matches ChibiReplayRecorder.Add.</summary>
        public event Action<int, ChibiInputPacket, ChibiInputPacket> OnFrameConfirmed;
        /// <summary>Every input final, ascending frame order, any Count. The array is reused: copy it if you keep it.</summary>
        public event Action<int, ChibiInputPacket[]> OnFrameConfirmedAll;
        /// <summary>
        /// (from, to): the frames [from, to) were just re-simulated. Presentation code uses it to cancel one-shot effects
        /// spawned for frames that no longer happened (rollback-gap-report.md 2.5).
        /// </summary>
        public event Action<int, int> OnRollback;

        readonly int _local, _count;
        readonly IChibiTransport[] _links;
        readonly IChibiInputSource _source;
        readonly ChibiSimState _state = new ChibiSimState();
        readonly RollbackInputBuffer _inputs;
        readonly RollbackStateRing _snaps = new RollbackStateRing(RingSize);
        readonly ChibiRollbackSettings _settings;
        readonly ChibiInputPacket[] _stepIn = new ChibiInputPacket[MaxPeers];
        readonly ChibiInputPacket[] _confirmIn = new ChibiInputPacket[MaxPeers];

        int _frame;
        // ---- per remote slot (index = roster slot; the local slot's entries are unused)
        readonly int[] _remoteAck = new int[MaxPeers];      // newest local frame that remote reports holding contiguously
        readonly int[] _remoteSeen = new int[MaxPeers];     // newest frame number seen from that remote (NET-04 bounded)
        readonly int[] _remoteAdv = new int[MaxPeers];
        readonly int[] _silent = new int[MaxPeers];          // ticks without a NEW frame while that remote holds us back
        readonly int[] _quiet = new int[MaxPeers];           // ticks without ANY accepted datagram
        readonly bool[] _heard = new bool[MaxPeers];         // at least one gameplay datagram accepted from it
        readonly int[,] _hold = new int[MaxPeers, MaxPeers]; // [reporter, slot]: newest frame of slot the reporter holds
        readonly bool[] _lost = new bool[MaxPeers];          // we stopped hearing it (link closed, our vote cast)
        readonly bool[][] _stallRing = new bool[MaxPeers][]; // NET-01 window, blamed on the remotes that held us back
        readonly int[] _stalledIn = new int[MaxPeers];
        int _nextConfirmEmit;
        int _tickCount;
        // global stall window (diagnostics / StalledInWindow)
        readonly bool[] _anyStallRing;
        int _anyStalled;

        // ---- drop vote (Count > 2)
        int _myVotes;                                         // bit s: we vote slot s out
        readonly int[] _myVoteFrame = new int[MaxPeers];      // last frame of s we held contiguously when we voted
        readonly int[] _peerVotes = new int[MaxPeers];        // [voter] = its vote mask as last received
        readonly int[,] _peerVoteFrame = new int[MaxPeers, MaxPeers]; // [voter, target], fixed once seen
        int _droppedMask;
        int _voteTicks = -1;                                  // ticks a vote has been pending, -1 = none
        int _myAgreeMask;                                     // targets whose drop frame we computed (fixed once set)
        int _myWait;                                          // slots we are stalled on this tick (sent as WaitMask)
        readonly int[] _peerWait = new int[MaxPeers];         // latest WaitMask each remote sent
        readonly int[] _pausedTicks = new int[MaxPeers];      // consecutive ticks we paused counting against it
        readonly bool[] _pausedNow = new bool[MaxPeers];
        readonly int[] _peerWaitFrame = new int[MaxPeers];    // LastFrame of the datagram _peerWait came from
        int _noProgressTicks;                                 // consecutive stalled ticks with no vote pending
        readonly int[] _myAgree = new int[MaxPeers];
        readonly int[,] _peerAgree = new int[MaxPeers, MaxPeers];   // [voter, target] drop frame it reported, MinValue = none

        // RTT estimate: tick on which each local frame was first sent; sampled when any ack passes it.
        readonly int[] _sentTick = new int[RingSize];
        int _newestSent = -1;

        // desync detection: tiny rings keyed by (frame / interval) & 7, remote rings per slot
        readonly int[] _localHashFrame = new int[8];
        readonly uint[] _localHash = new uint[8];
        readonly int[,] _remoteHashFrame = new int[MaxPeers, 8];
        readonly uint[,] _remoteHash = new uint[MaxPeers, 8];
        readonly int[,] _comparedFrame = new int[MaxPeers, 8]; // NET-05: frame already compared (-1 = none)
        int _nextChecksumFrame = ChecksumInterval;
        int _latestHashFrame = -1;
        uint _latestHash;

        readonly BitBuffer _txBuf = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
        readonly BitBuffer _rxBuf = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
        readonly ChibiNetMessage _tx = new ChibiNetMessage(), _rx = new ChibiNetMessage();
        readonly byte[] _rxRaw = new byte[256];

        // ---- diagnostics (never part of sim state)
        public int RollbackCount { get; private set; }
        public int MaxRollbackDepth { get; private set; }
        public int ResimulatedFrames { get; private set; }
        public int StalledTicks { get; private set; }
        public int ChecksumsCompared { get; private set; }
        /// <summary>Datagrams that parsed but carried impossible values (forged ack, wrong sender slot); dropped.</summary>
        public int RejectedPackets { get; private set; }
        public bool Desynced { get; private set; }
        /// <summary>Roster slot whose checksum disagreed with ours (-1 = none / not attributable).</summary>
        public int DesyncPeer { get; private set; } = -1;
        public bool Disconnected { get; private set; }
        /// <summary>Why <see cref="OnDisconnect"/> was raised (meaningful only when <see cref="Disconnected"/>).</summary>
        public ChibiDisconnectReason DisconnectReason { get; private set; }
        /// <summary>True when the last Tick could not advance (waiting for a remote). Drive a "waiting" HUD from it.</summary>
        public bool IsStalled { get; private set; }
        /// <summary>True while a drop vote is pending (survivors stall until it completes).</summary>
        public bool VotePending => _voteTicks >= 0;
        /// <summary>Ticks since the slowest live remote's contiguous input last advanced (0 while every link is healthy).</summary>
        public int SilentTicks
        {
            get
            {
                int m = 0;
                for (int p = 0; p < _count; p++) if (IsLiveRemote(p)) m = Math.Max(m, Math.Max(_silent[p], _quiet[p]));
                return m;
            }
        }
        /// <summary>Length of the stall window in ticks: 4 x DisconnectTicks (1200 = 20 s by default).</summary>
        public int StallWindowTicks => _anyStallRing.Length;
        /// <summary>
        /// Stalled ticks allowed inside the window: two thirds of it (800 by default). One more raises Stalled for the
        /// remote that held us back. Measured on the loopback link (2026-11-16, 60 s, share of the window stalled):
        /// 300 ms RTT + 15 % loss 26 %, 400 ms 33 %, 500 ms 47 %, 500 ms + 10 % loss 52 %, 670 ms 60 %, 1 s 73 %. So honest
        /// links up to about 500 ms RTT with loss (about 650 ms clean) stay under the limit; the price is that a peer can
        /// still slow a match to about a third of full speed without ending it (the sim timer still ends the match).
        /// </summary>
        public int StallLimit => _anyStallRing.Length * 2 / 3;
        /// <summary>
        /// Ticks inside the last <see cref="StallWindowTicks"/> that could not advance (any cause). Per remote, only the
        /// ticks that remote held us back count toward its <see cref="ChibiDisconnectReason.Stalled"/> (NET-01 per peer).
        /// </summary>
        public int StalledInWindow => _anyStalled;
        /// <summary>Datagrams accepted from the peers (diagnostics; a stale datagram counts, so it proves nothing about play).</summary>
        public int AcceptedPackets { get; private set; }
        /// <summary>
        /// True once EVERY remote delivered at least one REAL input frame (frames below InputDelay are pre-seeded, not
        /// sent). With one remote: that remote played at least one tick with us (NET-03). See <see cref="HasInputFrom"/>.
        /// </summary>
        public bool HasRemoteInput
        {
            get { for (int p = 0; p < _count; p++) if (p != _local && !HasInputFrom(p)) return false; return true; }
        }
        /// <summary>Smoothed round trip in ticks, x8 fixed point (divide by 8), over every link. -1 until the first sample.</summary>
        public int RttTicksX8 { get; private set; } = -1;
        public float TimeDilation { get; private set; } = 1f;

        public ChibiSimState State => _state;
        public int Frame => _frame;
        public int LocalIndex => _local;
        /// <summary>Number of roster slots (2..4).</summary>
        public int Count => _count;
        public ChibiRollbackSettings Settings => _settings;
        public int ConfirmedFrame => Math.Min(_frame, ConfirmedContig() + 1);

        /// <summary>
        /// Slot blamed for the last no-contest cause this session saw (desync peer, protocol violator, one side of a mutual
        /// accusation); -1 = none. Telemetry only (review S1).
        /// </summary>
        public int CulpritSlot { get; private set; } = -1;

        /// <summary>Lowest hold remote <paramref name="p"/> reported for any undropped slot other than its own.</summary>
        int MinHoldOf(int p)
        {
            int m = int.MaxValue;
            for (int k = 0; k < _count; k++)
                if (k != p && (_droppedMask & (1 << k)) == 0 && _hold[p, k] < m) m = _hold[p, k];
            return m;
        }

        /// <summary>
        /// Newest frame whose inputs are final for us AND, in team play, reported held by every live peer for every live
        /// slot (review B1/N2): no agreed drop can reach below it, so confirmed output, replays and checksums are final for
        /// everyone. 1v1: exactly our own contiguous frame (protocol-5 behaviour).
        /// </summary>
        int ConfirmedContig()
        {
            int c = _inputs.RemoteContiguous;
            if (_count == 2) return c;
            for (int p = 0; p < _count; p++)
            {
                if (!IsLiveRemote(p)) continue;
                for (int k = 0; k < _count; k++)
                    if (k != p && (_droppedMask & (1 << k)) == 0 && _hold[p, k] < c) c = _hold[p, k];
            }
            return c;
        }
        /// <summary>Bit s: slot s was dropped by an agreed vote.</summary>
        public int DroppedMask => _droppedMask;

        /// <summary>
        /// The state at the start of <see cref="ConfirmedFrame"/>: every input before it is final, so no rollback can
        /// change it. Use it (not <see cref="State"/>, which may be predicted) for anything that must be final, such as
        /// declaring the winner. Null only if the snapshot fell out of the ring: the advance gate keeps Frame within
        /// RingSize / 2 of the confirmed frame (second review, BLOCKER 2), so it cannot while that gate holds. Do not mutate.
        /// </summary>
        public ChibiSimState ConfirmedState
        {
            get
            {
                int c = ConfirmedFrame;
                return c == _frame ? _state : _snaps.Peek(c);
            }
        }
        /// <summary>Largest frame advantage over any live remote (what time dilation corrects).</summary>
        public int LocalFrameAdvantage
        {
            get
            {
                int m = int.MinValue;
                for (int p = 0; p < _count; p++) if (IsLiveRemote(p)) m = Math.Max(m, AdvantageOver(p));
                return m == int.MinValue ? 0 : m;
            }
        }

        /// <summary>True once remote <paramref name="slot"/> delivered at least one real input frame (NET-03, per peer).</summary>
        public bool HasInputFrom(int slot) => slot != _local && _inputs.LastRealFrameOf(slot) >= _settings.InputDelay;
        /// <summary>True when we stopped hearing <paramref name="slot"/> (lost link or our own vote against it).</summary>
        public bool IsPeerLost(int slot) => slot >= 0 && slot < _count && _lost[slot];
        /// <summary>Newest frame of <paramref name="slot"/> we hold contiguously (diagnostics / tests; int.MaxValue once dropped).</summary>
        public int RemoteContiguousOf(int slot) => _inputs.RemoteContiguousOf(slot);
        /// <summary>Ticks since remote <paramref name="slot"/>'s contiguous input last advanced.</summary>
        public int SilentTicksOf(int slot) => slot >= 0 && slot < _count ? _silent[slot] : 0;

        /// <summary>Two-peer session with the production <see cref="ChibiRollbackSettings.Default"/>.</summary>
        public ChibiRollbackSession(int localIndex, in ChibiMatchConfig cfg, IChibiTransport net, IChibiInputSource source)
            : this(localIndex, cfg, net, source, ChibiRollbackSettings.Default) { }

        /// <summary>Two-peer session with explicit settings (must equal the peer's: they travel in the handshake).</summary>
        /// <example><code>var s = new ChibiRollbackSession(0, cfg, transport, input, ChibiRollbackSettings.Default);</code></example>
        public ChibiRollbackSession(int localIndex, in ChibiMatchConfig cfg, IChibiTransport net, IChibiInputSource source,
                                    ChibiRollbackSettings settings)
            : this(localIndex, cfg, TwoPeerLinks(localIndex, net), source, settings) { }

        /// <summary>
        /// Mesh session: <paramref name="links"/>[slot] is the link bound to roster slot <paramref name="links"/> index
        /// (length = the config's fighter count, null at <paramref name="localIndex"/>).
        /// </summary>
        /// <example><code>var s = new ChibiRollbackSession(2, cfg, new IChibiTransport[] { l0, l1, null, l3 }, input, settings);</code></example>
        public ChibiRollbackSession(int localIndex, in ChibiMatchConfig cfg, IChibiTransport[] links, IChibiInputSource source,
                                    ChibiRollbackSettings settings)
        {
            settings.Validate();
            if (links == null) throw new ArgumentNullException(nameof(links));
            _settings = settings;
            _source = source ?? throw new ArgumentNullException(nameof(source));
            ChibiSimulation.Init(_state, cfg);
            _count = _state.Count;
            if (links.Length != _count) throw new ArgumentException("one link slot per fighter (" + _count + ")", nameof(links));
            if (localIndex < 0 || localIndex >= _count) throw new ArgumentOutOfRangeException(nameof(localIndex));
            for (int p = 0; p < _count; p++)
                if (p != localIndex && links[p] == null) throw new ArgumentNullException(nameof(links), "missing link for slot " + p);
            _local = localIndex;
            _links = (IChibiTransport[])links.Clone();
            _links[_local] = null;
            _inputs = new RollbackInputBuffer(localIndex, _count, settings.InputDelay);
            _anyStallRing = new bool[settings.DisconnectTicks * 4];
            for (int p = 0; p < MaxPeers; p++)
            {
                _remoteAck[p] = _remoteSeen[p] = -1;
                _stallRing[p] = p < _count && p != _local ? new bool[settings.DisconnectTicks * 4] : null;
                for (int i = 0; i < 8; i++) _remoteHashFrame[p, i] = _comparedFrame[p, i] = -1;
                for (int t = 0; t < MaxPeers; t++)
                {
                    _peerVoteFrame[p, t] = int.MinValue;
                    _peerAgree[p, t] = int.MinValue;
                    _hold[p, t] = settings.InputDelay - 1;
                }
            }
            for (int i = 0; i < 8; i++) _localHashFrame[i] = -1;
            for (int i = 0; i < RingSize; i++) _sentTick[i] = -1;
        }

        static IChibiTransport[] TwoPeerLinks(int localIndex, IChibiTransport net)
        {
            if (net == null) throw new ArgumentNullException(nameof(net));
            if (localIndex != 0 && localIndex != 1) throw new ArgumentOutOfRangeException(nameof(localIndex));
            var a = new IChibiTransport[2];
            a[1 - localIndex] = net;
            return a;
        }

        bool IsLiveRemote(int p) => p != _local && !_lost[p] && (_droppedMask & (1 << p)) == 0;

        int AdvantageOver(int p) => _frame - Math.Max(0, _remoteSeen[p] - _settings.InputDelay);

        // ================================================================== one 60 Hz tick

        /// <summary>Run one network+simulation tick. Returns true if the simulation advanced (false = stalled).</summary>
        public bool Tick()
        {
            _tickCount++;
            PumpNetwork();
            UpdateVote();

            int wrong = _inputs.TakeFirstMisprediction();
            if (wrong >= 0) Rollback(wrong);

            bool advanced = false;
            // Second review, BLOCKER 2: in team play also stay within RingSize / 2 of the CONFIRMED frame, so a peer that
            // stops raising its holds cannot run the ring past what is confirmed (null ConfirmedState, skipped checksums,
            // overwritten inputs). That stall is blamed on the peer(s) holding confirmation back, below.
            int cc = ConfirmedContig();
            bool holdOk = _count == 2 || _frame - cc <= RingSize / 2;
            if (holdOk && _frame - _inputs.RemoteContiguous <= _settings.MaxPrediction)
            {
                var sample = new ChibiInputPacket { Raw = (ushort)(_source.SampleTick().Raw & ChibiInputPacket.WireMask) }.Sanitized();
                _inputs.AddLocal(sample.Raw);
                Advance();
                advanced = true;
            }
            else StalledTicks++;   // too far ahead of some remote's confirmed input: wait for it
            IsStalled = !advanced;

            // Final security review (collusion backstop): two peers that freeze together and claim to wait on each other
            // are paused by everyone and exempt from the cap, so nothing else ends the match. In team play, no simulation
            // progress for DisconnectTicks + MaxDisconnectTicks with no vote pending is a no contest (nobody blamed).
            // Every legitimate path fires first: a dead peer times out after DisconnectTicks (or the stall window after
            // 800 stalled ticks for one never heard from), and a pending vote suspends this count.
            if (advanced || _voteTicks >= 0) _noProgressTicks = 0;
            else if (_count > 2 && ++_noProgressTicks > _settings.DisconnectTicks + MaxDisconnectTicks)
                RaiseDisconnect(ChibiDisconnectReason.VoteFailed);

            // Final security review (gate-stall framing). Say whom we wait on, and do not count against a remote that says it
            // waits on someone else: it is alive and blocked, and blaming it would let a cheater who stalls one honest peer
            // get that peer voted out by the bystanders. A remote that says it waits on US is still counted, so the
            // stalled peer itself ejects (or counter-accuses) the real cause, which is plainly waiting on it. Dead peers are
            // never paused (the claim must be fresh: a datagram within the last second). A claim that holds the pause past
            // MaxDisconnectTicks ends the match as a no contest with nobody blamed, never an ejection of the claimer.
            _myWait = 0;
            if (!advanced && _count > 2)
                for (int p = 0; p < _count; p++)
                {
                    if (p == _local || (_droppedMask & (1 << p)) != 0) continue;
                    if (_frame - _inputs.RemoteContiguousOf(p) > _settings.MaxPrediction || (!holdOk && MinHoldOf(p) <= cc))
                        _myWait |= 1 << p;
                }
            for (int p = 0; p < _count; p++)
            {
                _pausedNow[p] = _count > 2 && p != _local && IsLiveRemote(p) && _quiet[p] < ChibiSimConst.TickRate
                                && (_peerWait[p] & ~(1 << _local)) != 0;   // it waits on someone other than us
                if (!_pausedNow[p]) { _pausedTicks[p] = 0; continue; }
                // The cap only runs while the remote waits on someone WE are not waiting on. Survivors blocked by the
                // same silent slot (a leaver, a peer that never started) are both honest; that slot is handled by the
                // normal timeout / stall path and must not be pre-empted by a no contest.
                if (_voteTicks >= 0 || (_peerWait[p] & ~_myWait & ~(1 << _local)) == 0) continue;
                if (++_pausedTicks[p] > MaxDisconnectTicks) { RaiseDisconnect(ChibiDisconnectReason.VoteFailed); }
            }

            int ring = _tickCount % _anyStallRing.Length;
            if (_anyStallRing[ring]) _anyStalled--;
            _anyStallRing[ring] = !advanced;
            if (!advanced) _anyStalled++;
            for (int p = 0; p < _count; p++)
            {
                if (p == _local) continue;
                // NET-01 per peer: a stalled tick is blamed on every live remote that holds us back. While a vote is
                // pending, the stall is the vote's fault, not the survivors'.
                bool blame = !advanced && IsLiveRemote(p) && _voteTicks < 0 && !_pausedNow[p]
                             && (_frame - _inputs.RemoteContiguousOf(p) > _settings.MaxPrediction || (!holdOk && MinHoldOf(p) <= cc));
                var r = _stallRing[p];
                if (r[ring]) _stalledIn[p]--;
                r[ring] = blame;
                if (blame) _stalledIn[p]++;
            }

            EmitConfirmedFrames();
            RunChecksums();
            SendPackets();

            int targets = _voteTicks >= 0 ? VoteTargets() : 0;
            for (int p = 0; p < _count; p++)
            {
                if (!IsLiveRemote(p)) continue;
                // A pending vote stalls everyone, so survivors are not silent by choice: only a slot someone voted against
                // keeps counting (so we confirm or refute that vote with our own timeout).
                //
                // Two tiers (mesh fix, run 2). When one peer dies, EVERY survivor stalls within MaxPrediction frames and
                // so stops producing new frames for the others too; counting "no new frame" for all of them made the
                // survivors time each other out (a race won by link latency) and the vote failed. So:
                //   * no datagram at all (a dead or cut-off peer) always counts;
                //   * datagrams without new frames (NET-01's frozen-but-talking peer) count only while that remote is
                //     the one holding us back (its contiguous frame is the minimum). With one remote that is always
                //     true, so 1v1 keeps exactly the protocol-5 rule.
                //
                // Review N3 (liar race): while a vote is pending only _quiet (no datagram at all) counts, and only against
                // a target. A target that still talks to us is never confirmed on "no new frames": the accuser may simply
                // have stopped sending to it. Review S (slow PeerHello): in team play nothing counts against a remote
                // before its first gameplay datagram, bounded by MaxDisconnectTicks of grace.
                bool counts = _count == 2 || _heard[p] || _tickCount > MaxDisconnectTicks;
                if (!counts) { }
                else if (_voteTicks < 0)
                {
                    _quiet[p]++;
                    // Stalled on the confirmation gate (BLOCKER 2): only the peers holding confirmation back are silent
                    // "by choice"; the others are waiting for us exactly like we wait for them.
                    //
                    // Silence caused by the confirmation gate counts double (DisconnectTicks / 2), so a gate-stalled peer
                    // votes the freezer out before anyone else could time IT out. Honest margin: confirmation trails the live
                    // frame by about two one-way latencies plus the input delay, so the 64-frame gate leaves ~40 frames
                    // of slack at 500 ms one-way; above ~700 ms one-way expect gate stalls (not ejections: everyone then
                    // says it is waiting, which pauses the counting).
                    if (_pausedNow[p]) { }
                    else if (holdOk) { if (_inputs.RemoteContiguousOf(p) <= _inputs.RemoteContiguous) _silent[p]++; }
                    else if (MinHoldOf(p) <= cc) _silent[p] += 2;
                }
                else if ((targets & (1 << p)) != 0) _quiet[p]++;
                if (_silent[p] > _settings.DisconnectTicks || _quiet[p] > _settings.DisconnectTicks)
                    PeerLost(p, ChibiDisconnectReason.Timeout);
                else if (_stalledIn[p] > StallLimit) PeerLost(p, ChibiDisconnectReason.Stalled);
            }
            return advanced;
        }

        /// <summary>
        /// Report a disconnect seen outside the input stream: a goodbye on the control channel or a transport loss.
        /// Two-peer form: the single remote. Count &gt; 2: every remote (we lost them all).
        /// </summary>
        public void NotifyDisconnected(ChibiDisconnectReason reason)
        {
            if (_count == 2) { PeerLost(_local == 0 ? 1 : 0, reason); return; }
            RaiseDisconnect(reason);
        }

        /// <summary>Report that remote <paramref name="slot"/> is gone (goodbye, link loss). Count &gt; 2: starts a drop vote.</summary>
        public void NotifyPeerDisconnected(int slot, ChibiDisconnectReason reason)
        {
            if (slot < 0 || slot >= _count || slot == _local) return;
            PeerLost(slot, reason);
        }

        void PeerLost(int p, ChibiDisconnectReason reason)
        {
            if (_count == 2)
            {
                // 1v1 keeps the protocol-5 behaviour: no vote, the link stays open, OnDisconnect decides the match.
                if (Disconnected) return;
                OnPeerLost?.Invoke(p, reason);
                RaiseDisconnect(reason);
                return;
            }
            if (_lost[p] || (_droppedMask & (1 << p)) != 0) return;
            _lost[p] = true;
            OnPeerLost?.Invoke(p, reason);
            // Cast our vote: frozen at what we hold contiguously from it now; its link is closed from here on.
            _myVotes |= 1 << p;
            _myVoteFrame[p] = _inputs.LastRealFrameOf(p);
            if (_voteTicks < 0) _voteTicks = 0;
        }

        void Violation(int slot)
        {
            if (CulpritSlot < 0) CulpritSlot = slot;
            RaiseDisconnect(ChibiDisconnectReason.ProtocolViolation);
        }

        void RaiseDisconnect(ChibiDisconnectReason reason)
        {
            if (Disconnected) return;
            Disconnected = true;
            DisconnectReason = reason;
            OnDisconnect?.Invoke(reason);
        }

        void RaiseDesync(int frame, uint local, uint remote, int peer)
        {
            if (Desynced) return;
            Desynced = true;
            DesyncPeer = peer;
            if (CulpritSlot < 0) CulpritSlot = peer;
            OnDesync?.Invoke(frame, local, remote);
        }

        // ================================================================== drop vote (Count > 2)

        /// <summary>
        /// Target set T = our votes plus every vote of a peer we still hear (votes naming us are ignored: a lying peer can
        /// then only cause a failed vote, never our elimination), minus slots already dropped.
        /// </summary>
        int VoteTargets()
        {
            int targets = _myVotes;
            for (int v = 0; v < _count; v++)
                if (v != _local && IsLiveRemote(v)) targets |= _peerVotes[v] & ~(1 << _local);
            return targets & ~_droppedMask;
        }

        void UpdateVote()
        {
            if (_count == 2 || Disconnected || _voteTicks < 0) return;
            _voteTicks++;

            int targets = VoteTargets();
            int survivors = 0, nSurvivors = 0;
            for (int p = 0; p < _count; p++)
                if ((_droppedMask & (1 << p)) == 0 && (targets & (1 << p)) == 0) { survivors |= 1 << p; nSurvivors++; }

            // A partition must not let both halves play on: only a strict majority of the roster may continue.
            if (nSurvivors * 2 <= _count) { RaiseDisconnect(ChibiDisconnectReason.VoteFailed); return; }

            // Review N3 / one-way partition (decision: no contest): two peers that each vote the other out cannot both be
            // right, and we cannot tell which one lies.
            for (int v = 0; v < _count; v++)
            {
                int vv = v == _local ? _myVotes : IsLiveRemote(v) ? _peerVotes[v] : 0;
                for (int t = 0; t < _count; t++)
                {
                    if (t == v || (vv & (1 << t)) == 0 || (_droppedMask & (1 << t)) != 0) continue;
                    int tv = t == _local ? _myVotes : IsLiveRemote(t) ? _peerVotes[t] : 0;
                    if ((tv & (1 << v)) != 0) { RaiseDisconnect(ChibiDisconnectReason.VoteFailed); return; }   // no blame: either could be the liar
                }
            }

            bool unanimous = (_myVotes & ~_droppedMask) == targets;
            for (int v = 0; v < _count && unanimous; v++)
                if (v != _local && (survivors & (1 << v)) != 0 && (_peerVotes[v] & ~_droppedMask) != targets) unanimous = false;

            if (!unanimous)
            {
                if (_voteTicks > MaxDisconnectTicks) RaiseDisconnect(ChibiDisconnectReason.VoteFailed);
                return;
            }

            // Second review, BLOCKER 1b: compute our drop frame per target once, publish it, and apply only when every
            // survivor published the same one. An equivocating voter makes survivors compute different frames: that is a
            // no contest for everyone (no blame: we cannot tell who lied to whom), never a split outcome.
            for (int t = 0; t < _count; t++)
            {
                if ((targets & (1 << t)) == 0 || (_myAgreeMask & (1 << t)) != 0) continue;
                int min = _myVoteFrame[t];
                for (int v = 0; v < _count; v++)
                    if (v != _local && (survivors & (1 << v)) != 0) min = Math.Min(min, _peerVoteFrame[v, t]);
                int dropAt = Math.Max(min + 1, _settings.InputDelay);
                // Review B1/N2: never rewrite a frame we already emitted (replay) or hashed. Unreachable from honest peers
                // (votes are checked against reported holds and frames confirm only once everybody holds them).
                if (dropAt < _nextConfirmEmit || dropAt < _latestHashFrame)
                {
                    int culprit = -1;
                    for (int v = 0; v < _count; v++)
                        if (v != _local && (survivors & (1 << v)) != 0 && _peerVoteFrame[v, t] == min) culprit = v;
                    Violation(culprit);
                    return;
                }
                _myAgree[t] = dropAt;
                _myAgreeMask |= 1 << t;
            }
            bool allAgreed = true;
            for (int t = 0; t < _count; t++)
            {
                if ((targets & (1 << t)) == 0) continue;
                for (int v = 0; v < _count; v++)
                {
                    if (v == _local || (survivors & (1 << v)) == 0) continue;
                    int a = _peerAgree[v, t];
                    if (a == int.MinValue) { allAgreed = false; continue; }
                    if (a != _myAgree[t]) { RaiseDisconnect(ChibiDisconnectReason.VoteFailed); return; }
                }
            }
            if (!allAgreed)
            {
                if (_voteTicks > MaxDisconnectTicks) RaiseDisconnect(ChibiDisconnectReason.VoteFailed);
                return;
            }
            for (int t = 0; t < _count; t++)
            {
                if ((targets & (1 << t)) == 0) continue;
                int dropAt = _myAgree[t];
                if (!_inputs.DropRemote(t, dropAt, _frame)) { RaiseDisconnect(ChibiDisconnectReason.VoteFailed); return; }
                _droppedMask |= 1 << t;
                OnPeerDropped?.Invoke(t, dropAt);
            }
            _voteTicks = -1;
            for (int p = 0; p < _count; p++) _silent[p] = _quiet[p] = 0;   // the vote stall is over; survivors start fresh
        }

        // ================================================================== simulation stepping

        /// <summary>Snapshot the current state, gather every input for Frame, and step. Used by ticks and by rollback.</summary>
        void Advance()
        {
            _snaps.Save(_frame, _state);
            for (int p = 0; p < _count; p++) _stepIn[p] = new ChibiInputPacket { Raw = _inputs.Get(p, _frame) };
            ChibiSimulation.Step(_state, _stepIn);
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
                RaiseDesync(from, 0, 0, -1);
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
            for (int p = 0; p < _count; p++)
            {
                var link = _links[p];
                if (link == null) continue;
                int contiguousBefore = _inputs.RemoteContiguousOf(p);
                // Bounded per link per tick: a flooding peer cannot starve the others' links (security BLOCKER 6).
                for (int budget = 0; budget < MaxDatagramsPerLinkPerTick; budget++)
                {
                    int n = link.Receive(_rxRaw);
                    if (n <= 0) break;
                    if (!IsLiveRemote(p)) continue;                        // drained but ignored once we voted it out
                    _rxBuf.LoadFrom(_rxRaw, n);
                    if (!ChibiPacketCodec.TryRead(_rxBuf, _rx)) continue;   // garbage / truncated / foreign datagram
                    if (!Process(p, _rx)) { RejectedPackets++; continue; }
                    AcceptedPackets++;
                    _quiet[p] = 0;
                    _heard[p] = true;
                }
                // NET-01: only a NEW input frame proves the peer is still playing.
                if (_inputs.RemoteContiguousOf(p) > contiguousBefore) _silent[p] = 0;
            }
        }

        /// <summary>Datagrams read from one link per tick; the rest wait for the next tick (honest peers send one per tick).</summary>
        public const int MaxDatagramsPerLinkPerTick = 16;

        /// <summary>Apply one parsed datagram from the link bound to <paramref name="p"/>. False (nothing changed) if impossible from an honest peer.</summary>
        bool Process(int p, ChibiNetMessage m)
        {
            // Security BLOCKER 1: the link is bound to slot p; a datagram claiming another slot is forged or misrouted.
            if (m.Sender != p) return false;
            // An honest peer can only ack frames we have produced and sent (gap report 2.8).
            if (m.AckFrame > _inputs.LocalLast) return false;
            if (_count == 2 && m.VoteMask != 0) return false;                  // no votes in 1v1
            if ((m.VoteMask & (1 << p)) != 0 || (m.VoteMask >> _count) != 0) return false;   // votes itself / no such slot
            if (_count == 2 && m.HoldMask != 0) return false;                  // no holds in 1v1
            if ((m.HoldMask & (1 << p)) != 0 || (m.HoldMask >> _count) != 0) return false;
            // Second review, BLOCKER 1a: in team play every datagram reports a hold for every slot but the sender's
            // (dropped slots included), so a peer cannot show holds to one survivor and hide them from another.
            if (_count > 2 && m.HoldMask != (((1 << _count) - 1) & ~(1 << p))) return false;
            if ((m.AgreeMask & (1 << p)) != 0 || (m.AgreeMask >> _count) != 0 || (_count == 2 && m.AgreeMask != 0)) return false;
            if ((m.WaitMask & (1 << p)) != 0 || (m.WaitMask >> _count) != 0 || (_count == 2 && m.WaitMask != 0)) return false;
            // Our own slot: like the ack, a peer cannot hold frames of ours that we never produced.
            if ((m.HoldMask & (1 << _local)) != 0 && m.Hold[_local] > _inputs.LocalLast) return false;

            // NET-04: sanity bound only, per peer.
            if (m.LastFrame > _remoteSeen[p] && m.LastFrame <= _inputs.RemoteContiguousOf(p) + ChibiNetMessage.MaxInputs)
                _remoteSeen[p] = m.LastFrame;
            _remoteAdv[p] = m.Advantage;
            if (m.AckFrame > _remoteAck[p])
            {
                int sent = _sentTick[m.AckFrame & (RingSize - 1)];
                if (sent >= 0) SampleRtt(_tickCount - sent);
                _remoteAck[p] = m.AckFrame;
            }

            int firstFrame = m.LastFrame - m.Count + 1;
            for (int k = 0; k < m.Count; k++)
                _inputs.AddRemote(p, firstFrame + k, m.Inputs[k].Raw, _frame);

            if (m.HasChecksum) AcceptRemoteChecksum(p, m.ChecksumFrame, m.Checksum);

            // Holds are monotonic (a reordered older datagram cannot lower them); frozen once that voter voted the slot.
            for (int k = 0; k < _count; k++)
                if ((m.HoldMask & (1 << k)) != 0 && m.Hold[k] > _hold[p, k] && _peerVoteFrame[p, k] == int.MinValue)
                    _hold[p, k] = m.Hold[k];

            for (int t = 0; t < _count; t++)
                if ((m.AgreeMask & (1 << t)) != 0 && _peerAgree[p, t] == int.MinValue) _peerAgree[p, t] = m.AgreeFrame[t];
            // Only the newest datagram's claim counts: a reordered older one must not restore a stale "waiting on".
            if (m.LastFrame >= _peerWaitFrame[p]) { _peerWait[p] = m.WaitMask; _peerWaitFrame[p] = m.LastFrame; }

            if (m.VoteMask != 0)
            {
                // A vote is fixed once cast: the first frame seen per (voter, target) is kept, later values ignored.
                for (int t = 0; t < _count; t++)
                {
                    if ((m.VoteMask & (1 << t)) == 0 || _peerVoteFrame[p, t] != int.MinValue) continue;
                    // Review B1/N2: a vote below what this voter already reported holding of that slot is a lie meant to
                    // drag the drop frame under confirmed frames. No contest, blamed on the voter.
                    if (m.VoteFrame[t] < _hold[p, t]) { Violation(p); return true; }
                    _peerVoteFrame[p, t] = m.VoteFrame[t];
                }
                _peerVotes[p] |= m.VoteMask;
                if (_voteTicks < 0 && (_peerVotes[p] & ~_droppedMask & ~(1 << _local)) != 0) _voteTicks = 0;
            }
            return true;
        }

        void SampleRtt(int ticks)
        {
            int x8 = ticks * 8;
            RttTicksX8 = RttTicksX8 < 0 ? x8 : RttTicksX8 + (x8 - RttTicksX8) / 8;   // EWMA, alpha 1/8
        }

        // ================================================================== send

        void SendPackets()
        {
            int last = _inputs.LocalLast;
            int lead = 0;
            for (int p = 0; p < _count; p++)
            {
                if (!IsLiveRemote(p)) continue;
                // Window = the OLDEST inputs this link has not acknowledged (gap report 2.1 fix a + b).
                int first = _remoteAck[p] + 1;
                if (first > last) first = last;                          // all acked: resend the newest as a keep-alive
                if (first < 0) first = 0;
                // NET-06: a peer that never acks must not walk the window out of the input ring.
                if (first < last - (RingSize - 1)) first = last - (RingSize - 1);
                int count = last - first + 1;
                if (count > ChibiNetMessage.MaxInputs) count = ChibiNetMessage.MaxInputs;

                _tx.Sender = _local;
                _tx.LastFrame = first + count - 1;
                _tx.Count = count;
                for (int k = 0; k < count; k++)
                    _tx.Inputs[k] = new ChibiInputPacket { Raw = _inputs.LocalInput(first + k) };
                for (int f = Math.Max(_newestSent + 1, first); f <= _tx.LastFrame; f++) _sentTick[f & (RingSize - 1)] = _tickCount;
                _tx.AckFrame = _inputs.RemoteContiguousOf(p);
                int adv = AdvantageOver(p);
                _tx.Advantage = adv;
                // Both peers estimate "how far ahead am I of what I see of you". The difference is twice the true lead.
                lead = Math.Max(lead, (adv - _remoteAdv[p]) / 2);

                _tx.HasChecksum = _latestHashFrame >= 0;
                _tx.ChecksumFrame = _latestHashFrame;
                _tx.Checksum = _latestHash;
                // Votes stay in every datagram even after the drop: a survivor that finalized first must not stop
                // repeating its vote before a slower survivor (who needs that frame for the same minimum) has it.
                _tx.VoteMask = _myVotes;
                _tx.HoldMask = 0;
                _tx.AgreeMask = _myAgreeMask;
                _tx.WaitMask = _myWait & ~(1 << _local);
                for (int t = 0; t < MaxPeers; t++) _tx.AgreeFrame[t] = (_myAgreeMask & (1 << t)) != 0 ? _myAgree[t] : 0;
                if (_count > 2)
                    for (int k = 0; k < _count; k++)
                    {
                        if (k == _local) continue;
                        _tx.HoldMask |= 1 << k;   // every other slot, dropped ones too (their last real frame)
                        _tx.Hold[k] = _inputs.LastRealFrameOf(k);
                    }
                for (int t = 0; t < MaxPeers; t++) _tx.VoteFrame[t] = (_tx.VoteMask & (1 << t)) != 0 ? _myVoteFrame[t] : -1;

                _txBuf.Clear();
                // Write throws InvalidOperationException on overrun; deliberately not caught.
                ChibiPacketCodec.Write(_txBuf, _tx);
                _links[p].Send(_txBuf.Bytes, _txBuf.ByteLength);
                if (_tx.LastFrame > _newestSent) _newestSent = _tx.LastFrame;
            }
            TimeDilation = 1f + Math.Min(4, Math.Max(0, lead)) * 0.02f;
        }

        // ================================================================== confirmation, replay hook, desync check

        void EmitConfirmedFrames()
        {
            // A frame's inputs are final once every remote input is actual (local always is).
            int contig = ConfirmedContig();
            while (_nextConfirmEmit <= contig && _nextConfirmEmit <= _inputs.LocalLast)
            {
                int f = _nextConfirmEmit++;
                for (int p = 0; p < _count; p++)
                {
                    // Second review, BLOCKER 2: never emit an overwritten ring slot (corrupt replay); that is a desync.
                    if (!_inputs.TryConfirmed(p, f, out ushort raw)) { _nextConfirmEmit = f; RaiseDesync(f, 0, 0, -1); return; }
                    _confirmIn[p] = new ChibiInputPacket { Raw = raw };
                }
                if (_count == 2) OnFrameConfirmed?.Invoke(f, _confirmIn[0], _confirmIn[1]);
                OnFrameConfirmedAll?.Invoke(f, _confirmIn);
            }
        }

        void RunChecksums()
        {
            // State at the start of frame c is final once c <= RemoteContiguous + 1 (all earlier inputs are actual).
            int confirmed = ConfirmedFrame;   // once per tick (nit)
            while (_nextChecksumFrame <= confirmed)
            {
                int c = _nextChecksumFrame;
                _nextChecksumFrame += ChecksumInterval;

                uint hash;
                if (c == _frame) hash = _state.ComputeChecksum();
                else
                {
                    var snap = _snaps.Peek(c);
                    // Second review, BLOCKER 2: a missing snapshot used to skip the check silently (desync detection off).
                    if (snap == null) { RaiseDesync(c, 0, 0, -1); return; }
                    hash = snap.ComputeChecksum();
                }

                int idx = (c / ChecksumInterval) & 7;
                _localHashFrame[idx] = c;
                _localHash[idx] = hash;
                _latestHashFrame = c;
                _latestHash = hash;
                for (int p = 0; p < _count; p++) if (p != _local) CompareChecksum(p, idx);
            }
        }

        void AcceptRemoteChecksum(int p, int frame, uint hash)
        {
            if (frame <= 0 || frame % ChecksumInterval != 0) return;
            int idx = (frame / ChecksumInterval) & 7;
            if (_remoteHashFrame[p, idx] == frame) return;                // already recorded
            if (_comparedFrame[p, idx] == frame) return;                  // NET-05: already compared
            _remoteHashFrame[p, idx] = frame;
            _remoteHash[p, idx] = hash;
            CompareChecksum(p, idx);
        }

        void CompareChecksum(int p, int idx)
        {
            if (_localHashFrame[idx] < 0 || _localHashFrame[idx] != _remoteHashFrame[p, idx]) return;
            ChecksumsCompared++;
            _comparedFrame[p, idx] = _localHashFrame[idx];
            if (_localHash[idx] != _remoteHash[p, idx]) RaiseDesync(_localHashFrame[idx], _localHash[idx], _remoteHash[p, idx], p);
            _remoteHashFrame[p, idx] = -2;   // consumed
        }
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
        IChibiTransport[] _transports;
        readonly ChibiTickClock _clock = new ChibiTickClock();
        Func<float> _dilation;   // cached once in Begin so Update allocates nothing
        Action _tick;

        public void Begin(int localIndex, in ChibiMatchConfig cfg, IChibiTransport transport, IChibiInputSource source, bool recordReplay = true)
            => Begin(localIndex, cfg, transport, source, ChibiRollbackSettings.Default, recordReplay);

        /// <summary>Start a two-peer session with settings agreed in the handshake.</summary>
        public void Begin(int localIndex, in ChibiMatchConfig cfg, IChibiTransport transport, IChibiInputSource source,
                          ChibiRollbackSettings settings, bool recordReplay = true)
        {
            var links = new IChibiTransport[2];
            links[1 - localIndex] = transport;
            Begin(localIndex, cfg, links, source, settings, recordReplay);
        }

        /// <summary>Start a mesh session: <paramref name="links"/>[slot] is the link bound to that roster slot.</summary>
        public void Begin(int localIndex, in ChibiMatchConfig cfg, IChibiTransport[] links, IChibiInputSource source,
                          ChibiRollbackSettings settings, bool recordReplay = true)
        {
            _transports = links;
            _source = source;
            Session = new ChibiRollbackSession(localIndex, cfg, links, source, settings);
            if (recordReplay)
            {
                Replay = new ChibiReplayRecorder(cfg);
                if (Session.Count == 2) Session.OnFrameConfirmed += Replay.Add;
                else Session.OnFrameConfirmedAll += Replay.Add;
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
            if (_transports == null) return;
            foreach (var t in _transports) (t as IDisposable)?.Dispose();
        }
    }
}
