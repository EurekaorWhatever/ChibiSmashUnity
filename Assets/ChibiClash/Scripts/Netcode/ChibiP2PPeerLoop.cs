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
        /// <summary>Ticks without a valid datagram before the opponent counts as disconnected (300 = 5 s).</summary>
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
        }
    }

    /// <summary>Why a session stopped talking to its peer.</summary>
    public enum ChibiDisconnectReason : byte
    {
        /// <summary>No valid datagram for <see cref="ChibiRollbackSettings.DisconnectTicks"/>.</summary>
        Timeout = 0,
        /// <summary>The peer said goodbye (quit, forfeit) on the control channel.</summary>
        RemoteLeft = 1,
        /// <summary>The transport itself lost the connection (Relay severed it, socket error).</summary>
        TransportLost = 2,
    }

    /// <summary>
    /// GGPO-style rollback session for two peers. Pure C# (no Unity types), so it can be unit tested headless.
    /// Input history and prediction live in <see cref="RollbackInputBuffer"/>, snapshots in <see cref="RollbackStateRing"/>.
    ///
    /// Frame model
    ///   * Frame is the next frame to simulate; State is the simulation at the START of Frame.
    ///   * Local input sampled during tick T is scheduled for frame (Frame + InputDelay) and sent at once.
    ///   * Remote input for a frame that has not arrived is PREDICTED as "same as their newest known input".
    ///     When the real input arrives and differs from what was simulated, we load the snapshot of the
    ///     first wrong frame and re-simulate up to the present in the same tick (rollback).
    ///   * We refuse to run more than MaxPrediction frames ahead of the newest contiguous remote input.
    ///
    /// Netcode safety
    ///   * Every datagram re-sends the OLDEST unacknowledged local inputs (up to 16), so loss of any length is
    ///     repaired without timers: the frame the remote is missing is always in the next packet (gap report 2.1).
    ///   * An ack for a frame we never produced is impossible from an honest peer; such datagrams are dropped (2.8).
    ///   * Every 30 confirmed frames each peer hashes the (now final) state and ships it; a mismatch raises OnDesync once.
    ///   * Peers exchange a frame-advantage estimate; the peer that is ahead gets TimeDilation &gt; 1 (slightly
    ///     longer ticks) so the two simulations converge and rollbacks stay shallow.
    /// </summary>
    public sealed class ChibiRollbackSession
    {
        public const int RingSize = RollbackInputBuffer.RingSize;
        public const int DefaultInputDelay = 2;
        public const int DefaultMaxPrediction = 8;
        public const int ChecksumInterval = 30;
        public const int DefaultDisconnectTicks = 300;   // 5 s of silence

        /// <summary>Frame, local hash, remote hash. Raised at most once per session (a desync ends the match).</summary>
        public event Action<int, uint, uint> OnDesync;
        /// <summary>Raised at most once per session. After it, the session keeps ticking harmlessly but should be ended.</summary>
        public event Action<ChibiDisconnectReason> OnDisconnect;
        /// <summary>Both inputs final, ascending frame order. Signature matches ChibiReplayRecorder.Add.</summary>
        public event Action<int, ChibiInputPacket, ChibiInputPacket> OnFrameConfirmed;
        /// <summary>
        /// (from, to): the frames [from, to) were just re-simulated. Presentation code uses it to cancel one-shot effects
        /// spawned for frames that no longer happened (rollback-gap-report.md 2.5).
        /// </summary>
        public event Action<int, int> OnRollback;

        readonly int _local, _remote;
        readonly IChibiTransport _net;
        readonly IChibiInputSource _source;
        readonly ChibiSimState _state = new ChibiSimState();
        readonly RollbackInputBuffer _inputs;
        readonly RollbackStateRing _snaps = new RollbackStateRing(RingSize);
        readonly ChibiRollbackSettings _settings;

        int _frame;
        int _remoteAck = -1;        // newest local frame the remote reports holding contiguously
        int _remoteSeen = -1;       // newest remote frame number seen
        int _remoteAdv;
        int _nextConfirmEmit;
        int _silentTicks;
        int _tickCount;

        // RTT estimate: tick on which each local frame was first sent; sampled when the ack passes it.
        readonly int[] _sentTick = new int[RingSize];

        // desync detection: tiny rings keyed by (frame / interval) & 7
        readonly int[] _localHashFrame = new int[8], _remoteHashFrame = new int[8];
        readonly uint[] _localHash = new uint[8], _remoteHash = new uint[8];
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
        /// <summary>Datagrams that parsed but carried impossible values (forged ack); dropped.</summary>
        public int RejectedPackets { get; private set; }
        public bool Desynced { get; private set; }
        public bool Disconnected { get; private set; }
        /// <summary>True when the last Tick could not advance (waiting for the remote). Drive a "waiting" HUD from it.</summary>
        public bool IsStalled { get; private set; }
        /// <summary>Ticks since the last valid datagram (0 while the link is healthy).</summary>
        public int SilentTicks => _silentTicks;
        /// <summary>Smoothed round trip in ticks, x8 fixed point (divide by 8). -1 until the first sample.</summary>
        public int RttTicksX8 { get; private set; } = -1;
        public float TimeDilation { get; private set; } = 1f;

        public ChibiSimState State => _state;
        public int Frame => _frame;
        public int LocalIndex => _local;
        public ChibiRollbackSettings Settings => _settings;
        public int ConfirmedFrame => Math.Min(_frame, _inputs.RemoteContiguous + 1);

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
        public int LocalFrameAdvantage => _frame - Math.Max(0, _remoteSeen - _settings.InputDelay);

        /// <summary>Session with the production <see cref="ChibiRollbackSettings.Default"/>.</summary>
        public ChibiRollbackSession(int localIndex, in ChibiMatchConfig cfg, IChibiTransport net, IChibiInputSource source)
            : this(localIndex, cfg, net, source, ChibiRollbackSettings.Default) { }

        /// <summary>Session with explicit settings (must equal the peer's: they travel in the handshake).</summary>
        /// <example><code>var s = new ChibiRollbackSession(0, cfg, transport, input, ChibiRollbackSettings.Default);</code></example>
        public ChibiRollbackSession(int localIndex, in ChibiMatchConfig cfg, IChibiTransport net, IChibiInputSource source,
                                    ChibiRollbackSettings settings)
        {
            settings.Validate();
            _settings = settings;
            _local = localIndex;
            _remote = 1 - localIndex;
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _inputs = new RollbackInputBuffer(localIndex, settings.InputDelay);
            for (int i = 0; i < 8; i++) _localHashFrame[i] = _remoteHashFrame[i] = -1;
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
            else StalledTicks++;   // too far ahead of the remote's confirmed input: wait for it
            IsStalled = !advanced;

            EmitConfirmedFrames();
            RunChecksums();
            SendPacket();

            if (++_silentTicks > _settings.DisconnectTicks) RaiseDisconnect(ChibiDisconnectReason.Timeout);
            return advanced;
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

        /// <summary>Snapshot the current state, gather both inputs for Frame, and step. Used by ticks and by rollback.</summary>
        void Advance()
        {
            _snaps.Save(_frame, _state);
            ushort i0 = _inputs.Get(0, _frame);
            ushort i1 = _inputs.Get(1, _frame);
            ChibiSimulation.Step(_state, new ChibiInputPacket { Raw = i0 }, new ChibiInputPacket { Raw = i1 });
            _frame++;
        }

        void Rollback(int from)
        {
            int target = _frame;
            if (from >= target) return;
            if (!_snaps.TryLoad(from, _state))
            {
                // Unreachable while MaxPrediction < RingSize (Validate); if it ever happens the two sims can no longer
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
            while (true)
            {
                int n = _net.Receive(_rxRaw);
                if (n <= 0) break;
                _rxBuf.LoadFrom(_rxRaw, n);
                if (!ChibiPacketCodec.TryRead(_rxBuf, _rx)) continue;   // garbage / truncated / foreign datagram
                if (!Process(_rx)) { RejectedPackets++; continue; }
                _silentTicks = 0;
            }
        }

        /// <summary>Apply one parsed datagram. Returns false (and changes nothing) if it is impossible from an honest peer.</summary>
        bool Process(ChibiNetMessage m)
        {
            // An honest peer can only ack frames we have produced and sent (gap report 2.8).
            if (m.AckFrame > _inputs.LocalLast) return false;

            if (m.LastFrame > _remoteSeen) _remoteSeen = m.LastFrame;
            _remoteAdv = m.Advantage;
            if (m.AckFrame > _remoteAck)
            {
                int sent = _sentTick[m.AckFrame & (RingSize - 1)];
                if (sent >= 0) SampleRtt(_tickCount - sent);
                _remoteAck = m.AckFrame;
            }

            int firstFrame = m.LastFrame - m.Count + 1;
            for (int k = 0; k < m.Count; k++)
                _inputs.AddRemote(firstFrame + k, m.Inputs[k].Raw, _frame);

            if (m.HasChecksum) AcceptRemoteChecksum(m.ChecksumFrame, m.Checksum);
            return true;
        }

        void SampleRtt(int ticks)
        {
            int x8 = ticks * 8;
            RttTicksX8 = RttTicksX8 < 0 ? x8 : RttTicksX8 + (x8 - RttTicksX8) / 8;   // EWMA, alpha 1/8
        }

        // ================================================================== send

        void SendPacket()
        {
            int last = _inputs.LocalLast;
            // Window = the OLDEST unacknowledged inputs (gap report 2.1 fix a + b). The frame the remote is missing is
            // therefore in every packet until acked, so any burst of loss heals as soon as one datagram gets through.
            int first = _remoteAck + 1;
            if (first > last) first = last;                          // all acked: resend the newest as a keep-alive
            if (first < 0) first = 0;
            int count = last - first + 1;
            if (count > ChibiNetMessage.MaxInputs) count = ChibiNetMessage.MaxInputs;

            _tx.LastFrame = first + count - 1;
            _tx.Count = count;
            for (int k = 0; k < count; k++)
                _tx.Inputs[k] = new ChibiInputPacket { Raw = _inputs.LocalInput(first + k) };
            // RTT bookkeeping: remember the tick each frame was FIRST sent (resends keep the original time).
            for (int f = Math.Max(_newestSent + 1, first); f <= _tx.LastFrame; f++) _sentTick[f & (RingSize - 1)] = _tickCount;
            if (_tx.LastFrame > _newestSent) _newestSent = _tx.LastFrame;
            _tx.AckFrame = _inputs.RemoteContiguous;

            int localAdv = LocalFrameAdvantage;
            _tx.Advantage = localAdv;
            // Both peers estimate "how far ahead am I of what I see of you". The difference is twice the true lead.
            int lead = (localAdv - _remoteAdv) / 2;
            TimeDilation = 1f + Math.Min(4, Math.Max(0, lead)) * 0.02f;

            _tx.HasChecksum = _latestHashFrame >= 0;
            _tx.ChecksumFrame = _latestHashFrame;
            _tx.Checksum = _latestHash;

            _txBuf.Clear();
            // Write throws InvalidOperationException on overrun; deliberately not caught, so a too-large
            // message surfaces as an error instead of a silently truncated datagram.
            ChibiPacketCodec.Write(_txBuf, _tx);
            _net.Send(_txBuf.Bytes, _txBuf.ByteLength);
        }

        int _newestSent = -1;       // newest local frame ever put on the wire

        // ================================================================== confirmation, replay hook, desync check

        void EmitConfirmedFrames()
        {
            // A frame's inputs are final once the remote input is actual (local always is).
            while (_nextConfirmEmit <= _inputs.RemoteContiguous && _nextConfirmEmit <= _inputs.LocalLast)
            {
                int f = _nextConfirmEmit++;
                OnFrameConfirmed?.Invoke(f, new ChibiInputPacket { Raw = _inputs.Confirmed(0, f) },
                                            new ChibiInputPacket { Raw = _inputs.Confirmed(1, f) });
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
                CompareChecksum(idx);
            }
        }

        void AcceptRemoteChecksum(int frame, uint hash)
        {
            if (frame <= 0 || frame % ChecksumInterval != 0) return;
            int idx = (frame / ChecksumInterval) & 7;
            if (_remoteHashFrame[idx] == frame) return;                // already recorded
            _remoteHashFrame[idx] = frame;
            _remoteHash[idx] = hash;
            CompareChecksum(idx);
        }

        void CompareChecksum(int idx)
        {
            if (_localHashFrame[idx] < 0 || _localHashFrame[idx] != _remoteHashFrame[idx]) return;
            ChecksumsCompared++;
            if (_localHash[idx] != _remoteHash[idx]) RaiseDesync(_localHashFrame[idx], _localHash[idx], _remoteHash[idx]);
            _remoteHashFrame[idx] = -2;   // consumed
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
