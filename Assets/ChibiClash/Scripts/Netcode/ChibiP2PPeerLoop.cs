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
    /// GGPO-style rollback session for two peers. Pure C# (no Unity types), so it can be unit tested headless.
    ///
    /// Frame model
    ///   * _frame is the next frame to simulate; _state is the simulation at the START of _frame.
    ///   * Local input sampled during tick T is scheduled for frame (_frame + InputDelay) and sent at once.
    ///   * Remote input for a frame that has not arrived is PREDICTED as "same as their newest known input".
    ///     When the real input arrives and differs from what was simulated, we load the snapshot of the
    ///     first wrong frame and re-simulate up to the present in the same tick (rollback).
    ///   * We refuse to run more than MaxPrediction frames ahead of the newest contiguous remote input.
    ///
    /// Netcode safety
    ///   * Every datagram redundantly carries the last (up to) 8 unacknowledged local inputs, so loss is
    ///     repaired by the next packet without timers.
    ///   * Every 30 confirmed frames each peer hashes the (now final) state and ships it; a mismatch raises OnDesync.
    ///   * Peers exchange a frame-advantage estimate; the peer that is ahead gets TimeDilation &gt; 1 (slightly
    ///     longer ticks) so the two simulations converge and rollbacks stay shallow.
    /// </summary>
    public sealed class ChibiRollbackSession
    {
        public const int RingSize = 128;
        const int RingMask = RingSize - 1;
        public const int InputDelay = 2;
        public const int MaxPrediction = 8;
        public const int ChecksumInterval = 30;
        public const int DisconnectTicks = 600;     // 10 s of silence

        public event Action<int, uint, uint> OnDesync;                       // frame, local hash, remote hash
        public event Action OnDisconnect;
        public event Action<int, ChibiInputPacket, ChibiInputPacket> OnFrameConfirmed; // both inputs final, ascending order

        readonly int _local, _remote;
        readonly IChibiTransport _net;
        readonly IChibiInputSource _source;
        readonly ChibiSimState _state = new ChibiSimState();

        // snapshot ring: state at the start of frame N lives in slot N & mask
        readonly ChibiSimState[] _snap = new ChibiSimState[RingSize];
        readonly int[] _snapFrame = new int[RingSize];

        // input history ring per player; slot is valid for frame N only if _inFrame[p][slot] == N
        readonly ushort[][] _in = { new ushort[RingSize], new ushort[RingSize] };
        readonly int[][] _inFrame = { new int[RingSize], new int[RingSize] };
        readonly bool[][] _inActual = { new bool[RingSize], new bool[RingSize] };

        int _frame;
        int _remoteContig;          // highest remote frame such that every remote frame <= it is known
        int _localLast;             // newest local frame already sampled
        int _remoteAck = -1;        // newest local frame the remote reports having
        int _remoteSeen = -1;       // newest remote frame number seen
        int _remoteAdv;
        int _firstWrong = -1;       // earliest simulated frame whose remote input was mispredicted
        ushort _predBase;           // prediction = newest contiguous remote input
        int _nextConfirmEmit;
        int _silentTicks;
        bool _disconnected;

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

        // ---- diagnostics
        public int RollbackCount { get; private set; }
        public int MaxRollbackDepth { get; private set; }
        public int ResimulatedFrames { get; private set; }
        public int StalledTicks { get; private set; }
        public int ChecksumsCompared { get; private set; }
        public bool Desynced { get; private set; }
        public float TimeDilation { get; private set; } = 1f;

        public ChibiSimState State => _state;
        public int Frame => _frame;
        public int LocalIndex => _local;
        public int ConfirmedFrame => Math.Min(_frame, _remoteContig + 1);
        public int LocalFrameAdvantage => _frame - Math.Max(0, _remoteSeen - InputDelay);

        public ChibiRollbackSession(int localIndex, in ChibiMatchConfig cfg, IChibiTransport net, IChibiInputSource source)
        {
            _local = localIndex;
            _remote = 1 - localIndex;
            _net = net;
            _source = source;

            for (int i = 0; i < RingSize; i++)
            {
                _snap[i] = new ChibiSimState();
                _snapFrame[i] = -1;
                _inFrame[0][i] = _inFrame[1][i] = -1;
            }
            for (int i = 0; i < 8; i++) _localHashFrame[i] = _remoteHashFrame[i] = -1;

            ChibiSimulation.Init(_state, cfg);

            // Frames [0, InputDelay) are neutral for both players on both peers: nobody has to send them.
            for (int f = 0; f < InputDelay; f++)
            {
                Store(0, f, 0, true);
                Store(1, f, 0, true);
            }
            _remoteContig = InputDelay - 1;
            _localLast = InputDelay - 1;
        }

        // ================================================================== one 60 Hz tick

        /// <summary>Run one network+simulation tick. Returns true if the simulation advanced (false = stalled).</summary>
        public bool Tick()
        {
            PumpNetwork();
            if (_firstWrong >= 0) Rollback();

            bool advanced = false;
            if (_frame - _remoteContig <= MaxPrediction)
            {
                int f = _localLast + 1;
                Store(_local, f, (ushort)(_source.SampleTick().Raw & ChibiInputPacket.WireMask), true);
                _localLast = f;
                Advance();
                advanced = true;
            }
            else StalledTicks++;   // too far ahead of the remote's confirmed input: wait for it

            EmitConfirmedFrames();
            RunChecksums();
            SendPacket();

            if (++_silentTicks > DisconnectTicks && !_disconnected)
            {
                _disconnected = true;
                OnDisconnect?.Invoke();
            }
            return advanced;
        }

        // ================================================================== simulation stepping

        /// <summary>Snapshot the current state, gather both inputs for _frame, and step. Used by ticks and by rollback.</summary>
        void Advance()
        {
            int slot = _frame & RingMask;
            _snap[slot].CopyFrom(_state);
            _snapFrame[slot] = _frame;

            ushort i0 = InputFor(0, _frame);
            ushort i1 = InputFor(1, _frame);
            ChibiSimulation.Step(_state, new ChibiInputPacket { Raw = i0 }, new ChibiInputPacket { Raw = i1 });
            _frame++;
        }

        ushort InputFor(int player, int frame)
        {
            int slot = frame & RingMask;
            if (_inFrame[player][slot] == frame && (_inActual[player][slot] || player == _local)) return _in[player][slot];
            if (player == _local) return 0;      // unreachable: local input is always sampled ahead of the frame

            // Predict: repeat the newest contiguous remote input. Record it so a later mismatch is detected.
            Store(player, frame, _predBase, false);
            return _predBase;
        }

        void Store(int player, int frame, ushort raw, bool actual)
        {
            int slot = frame & RingMask;
            _in[player][slot] = raw;
            _inFrame[player][slot] = frame;
            _inActual[player][slot] = actual;
        }

        bool IsActual(int player, int frame)
        {
            int slot = frame & RingMask;
            return _inFrame[player][slot] == frame && _inActual[player][slot];
        }

        void Rollback()
        {
            int target = _frame;
            int from = _firstWrong;
            _firstWrong = -1;
            if (from >= target) return;

            int slot = from & RingMask;
            if (_snapFrame[slot] != from)
                throw new InvalidOperationException("Rollback snapshot for frame " + from + " was overwritten (depth > ring).");

            _state.CopyFrom(_snap[slot]);
            _frame = from;
            while (_frame < target) Advance();   // re-simulates with the corrected inputs, re-saving snapshots

            RollbackCount++;
            ResimulatedFrames += target - from;
            if (target - from > MaxRollbackDepth) MaxRollbackDepth = target - from;
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
                _silentTicks = 0;
                _disconnected = false;
                Process(_rx);
            }
        }

        void Process(ChibiNetMessage m)
        {
            if (m.LastFrame > _remoteSeen) _remoteSeen = m.LastFrame;
            _remoteAdv = m.Advantage;
            if (m.AckFrame > _remoteAck) _remoteAck = m.AckFrame;

            int firstFrame = m.LastFrame - m.Count + 1;
            for (int k = 0; k < m.Count; k++)
            {
                int f = firstFrame + k;
                if (f <= _remoteContig) continue;                       // already known
                if (f > _remoteContig + RingSize / 2) continue;         // implausibly far ahead
                int slot = f & RingMask;
                bool had = _inFrame[_remote][slot] == f;
                if (had && _inActual[_remote][slot]) continue;          // duplicate (redundant resend)

                ushort raw = m.Inputs[k].Raw;
                // We already simulated frame f with a prediction that turned out wrong -> must roll back to f.
                if (f < _frame && had && _in[_remote][slot] != raw && (_firstWrong < 0 || f < _firstWrong)) _firstWrong = f;
                Store(_remote, f, raw, true);
            }

            while (IsActual(_remote, _remoteContig + 1))
            {
                _remoteContig++;
                _predBase = _in[_remote][_remoteContig & RingMask];
            }

            if (m.HasChecksum) AcceptRemoteChecksum(m.ChecksumFrame, m.Checksum);
        }

        // ================================================================== send

        void SendPacket()
        {
            int count = _localLast - _remoteAck;                 // everything the remote has not acknowledged
            if (count < 1) count = 1;
            if (count > ChibiNetMessage.MaxInputs) count = ChibiNetMessage.MaxInputs;
            if (count > _localLast + 1) count = _localLast + 1;

            _tx.LastFrame = _localLast;
            _tx.Count = count;
            for (int k = 0; k < count; k++)
                _tx.Inputs[k] = new ChibiInputPacket { Raw = _in[_local][(_localLast - count + 1 + k) & RingMask] };
            _tx.AckFrame = _remoteContig;

            int localAdv = LocalFrameAdvantage;
            _tx.Advantage = localAdv;
            // Both peers estimate "how far ahead am I of what I see of you". The difference is twice the true lead.
            int lead = (localAdv - _remoteAdv) / 2;
            TimeDilation = 1f + Math.Min(4, Math.Max(0, lead)) * 0.02f;

            _tx.HasChecksum = _latestHashFrame >= 0;
            _tx.ChecksumFrame = _latestHashFrame;
            _tx.Checksum = _latestHash;

            _txBuf.Clear();
            ChibiPacketCodec.Write(_txBuf, _tx);
            _net.Send(_txBuf.Bytes, _txBuf.ByteLength);
        }

        // ================================================================== confirmation, replay hook, desync check

        void EmitConfirmedFrames()
        {
            // A frame's inputs are final once the remote input is actual (local always is).
            while (_nextConfirmEmit <= _remoteContig && _nextConfirmEmit <= _localLast)
            {
                int f = _nextConfirmEmit++;
                OnFrameConfirmed?.Invoke(f, new ChibiInputPacket { Raw = _in[0][f & RingMask] }, new ChibiInputPacket { Raw = _in[1][f & RingMask] });
            }
        }

        void RunChecksums()
        {
            // State at the start of frame c is final once c <= _remoteContig + 1 (all earlier inputs are actual).
            while (_nextChecksumFrame <= ConfirmedFrame)
            {
                int c = _nextChecksumFrame;
                _nextChecksumFrame += ChecksumInterval;

                uint hash;
                if (c == _frame) hash = _state.ComputeChecksum();
                else if (_snapFrame[c & RingMask] == c) hash = _snap[c & RingMask].ComputeChecksum();
                else continue;

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
            if (_localHash[idx] != _remoteHash[idx])
            {
                Desynced = true;
                OnDesync?.Invoke(_localHashFrame[idx], _localHash[idx], _remoteHash[idx]);
            }
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
        public const float TickSeconds = 1f / ChibiSimConst.TickRate;
        const int MaxTicksPerRenderFrame = 5;

        public ChibiRollbackSession Session { get; private set; }
        public ChibiReplayRecorder Replay { get; private set; }
        public float RenderAlpha => _acc / TickSeconds;

        IChibiInputSource _source;
        IChibiTransport _transport;
        float _acc;

        public void Begin(int localIndex, in ChibiMatchConfig cfg, IChibiTransport transport, IChibiInputSource source, bool recordReplay = true)
        {
            _transport = transport;
            _source = source;
            Session = new ChibiRollbackSession(localIndex, cfg, transport, source);
            if (recordReplay)
            {
                Replay = new ChibiReplayRecorder(cfg);
                Session.OnFrameConfirmed += Replay.Add;
            }
            _acc = 0f;
        }

        void Update()
        {
            if (Session == null) return;
            _source.PollRenderFrame();

            _acc += Time.unscaledDeltaTime;
            float step = TickSeconds * Session.TimeDilation;
            int ran = 0;
            while (_acc >= step && ran < MaxTicksPerRenderFrame)
            {
                Session.Tick();
                _acc -= step;
                ran++;
                step = TickSeconds * Session.TimeDilation;
            }
            if (ran == MaxTicksPerRenderFrame) _acc = 0f;   // hitch: drop the backlog instead of spiralling
        }

        void OnDestroy()
        {
            (_transport as IDisposable)?.Dispose();
        }
    }
}
