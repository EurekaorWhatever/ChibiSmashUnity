using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Deterministic frame history for a two-player rollback session: per-player input rings keyed by frame, with
    /// an "actual vs predicted" flag per slot, remote-input prediction, misprediction detection and the
    /// contiguous-confirmation cursor. Pure C#, no allocation after construction, not thread-safe.
    ///
    /// Owned by <see cref="ChibiRollbackSession"/> (the one rollback engine, README decision); extracted so the
    /// input rules are unit testable on their own.
    ///
    /// Model
    ///   * A slot is valid for frame N only while <c>frame[slot] == N</c>; older contents are simply stale.
    ///   * Local input is always actual. It is stored <see cref="InputDelay"/> frames ahead of the frame being
    ///     simulated, so the remote has that long to receive it before it is needed.
    ///   * Remote input that has not arrived is PREDICTED as "same as the newest contiguous actual remote input".
    ///     The prediction is written into the ring (flagged predicted), so when the actual input arrives we can
    ///     tell whether the frame was simulated with the wrong value.
    ///   * Prediction never invents press edges: inputs are held state and the sim derives presses from the previous
    ///     frame, so repeating the last input can only extend a hold, never fire a new attack.
    /// </summary>
    public sealed class RollbackInputBuffer
    {
        /// <summary>Ring length (power of two). Must exceed the deepest rollback plus the input-delay lead.</summary>
        public const int RingSize = 128;
        const int RingMask = RingSize - 1;

        readonly int _local, _remote;
        readonly ushort[][] _in = { new ushort[RingSize], new ushort[RingSize] };
        readonly int[][] _frame = { new int[RingSize], new int[RingSize] };
        readonly bool[][] _actual = { new bool[RingSize], new bool[RingSize] };
        int _firstWrong = -1;

        /// <summary>Frames of local input lead. Frames [0, InputDelay) are neutral for both players on both peers.</summary>
        public int InputDelay { get; }

        /// <summary>Highest remote frame such that every remote frame &lt;= it is known (actual).</summary>
        public int RemoteContiguous { get; private set; }

        /// <summary>Newest local frame already stored.</summary>
        public int LocalLast { get; private set; }

        /// <summary>The value used to predict missing remote frames: the newest contiguous actual remote input.</summary>
        public ushort PredictionBase { get; private set; }

        /// <summary>Earliest frame simulated with a wrong prediction, or -1. Cleared by <see cref="TakeFirstMisprediction"/>.</summary>
        public int FirstMisprediction => _firstWrong;

        /// <summary>Create the history for the peer that controls <paramref name="localIndex"/> (0 or 1).</summary>
        /// <example><code>var buf = new RollbackInputBuffer(0, inputDelay: 2);</code></example>
        public RollbackInputBuffer(int localIndex, int inputDelay)
        {
            if (localIndex != 0 && localIndex != 1) throw new ArgumentOutOfRangeException(nameof(localIndex));
            if (inputDelay < 0 || inputDelay >= RingSize / 4) throw new ArgumentOutOfRangeException(nameof(inputDelay));
            _local = localIndex;
            _remote = 1 - localIndex;
            InputDelay = inputDelay;
            for (int i = 0; i < RingSize; i++) _frame[0][i] = _frame[1][i] = -1;
            for (int f = 0; f < inputDelay; f++)
            {
                Store(0, f, 0, true);
                Store(1, f, 0, true);
            }
            RemoteContiguous = inputDelay - 1;
            LocalLast = inputDelay - 1;
        }

        // ================================================================== local side

        /// <summary>Append the next local input (frame <see cref="LocalLast"/> + 1). Returns the frame it was stored at.</summary>
        /// <example><code>int f = buf.AddLocal(packet.Raw);</code></example>
        public int AddLocal(ushort raw)
        {
            int f = LocalLast + 1;
            Store(_local, f, raw, true);
            LocalLast = f;
            return f;
        }

        /// <summary>Local input of <paramref name="frame"/> (must be within the last RingSize frames and stored).</summary>
        public ushort LocalInput(int frame)
        {
            int slot = frame & RingMask;
            if (_frame[_local][slot] != frame) throw new InvalidOperationException("local input for frame " + frame + " is not in the ring");
            return _in[_local][slot];
        }

        // ================================================================== remote side

        /// <summary>
        /// Record an actual remote input received from the wire. <paramref name="simulatedBelow"/> is the session's
        /// next frame to simulate: a differing value for an already simulated frame marks a misprediction.
        /// Returns false when the frame was ignored (already confirmed, a duplicate, or implausibly far ahead).
        /// </summary>
        /// <example><code>buf.AddRemote(f, raw, session.Frame);</code></example>
        public bool AddRemote(int frame, ushort raw, int simulatedBelow)
        {
            if (frame <= RemoteContiguous) return false;                    // already known
            if (frame > RemoteContiguous + RingSize / 2) return false;      // implausibly far ahead: would overwrite live slots
            int slot = frame & RingMask;
            bool had = _frame[_remote][slot] == frame;
            if (had && _actual[_remote][slot]) return false;                // duplicate (redundant resend)

            // We already simulated this frame with a prediction that turned out wrong -> roll back to it.
            if (frame < simulatedBelow && had && _in[_remote][slot] != raw && (_firstWrong < 0 || frame < _firstWrong))
                _firstWrong = frame;
            Store(_remote, frame, raw, true);

            while (IsActual(_remote, RemoteContiguous + 1))
            {
                RemoteContiguous++;
                PredictionBase = _in[_remote][RemoteContiguous & RingMask];
            }
            return true;
        }

        // ================================================================== simulation reads

        /// <summary>
        /// Input of <paramref name="player"/> for <paramref name="frame"/>: the actual value when known, otherwise the
        /// remote prediction (recorded in the ring so a later correction is detected).
        /// </summary>
        /// <example><code>ushort i1 = buf.Get(1, frame);</code></example>
        public ushort Get(int player, int frame)
        {
            int slot = frame & RingMask;
            if (_frame[player][slot] == frame && (_actual[player][slot] || player == _local)) return _in[player][slot];
            if (player == _local) return 0;      // unreachable in a session: local input is stored ahead of the frame

            Store(player, frame, PredictionBase, false);
            return PredictionBase;
        }

        /// <summary>True when the input of <paramref name="player"/> for <paramref name="frame"/> is final.</summary>
        public bool IsActual(int player, int frame)
        {
            int slot = frame & RingMask;
            return _frame[player][slot] == frame && _actual[player][slot];
        }

        /// <summary>Input of a frame whose inputs are final for both players (replay / confirmation output).</summary>
        public ushort Confirmed(int player, int frame) => _in[player][frame & RingMask];

        /// <summary>Return the earliest mispredicted frame (or -1) and clear it.</summary>
        public int TakeFirstMisprediction()
        {
            int f = _firstWrong;
            _firstWrong = -1;
            return f;
        }

        void Store(int player, int frame, ushort raw, bool actual)
        {
            int slot = frame & RingMask;
            _in[player][slot] = raw;
            _frame[player][slot] = frame;
            _actual[player][slot] = actual;
        }
    }

    /// <summary>
    /// State save/restore hooks for rollback: a ring of preallocated <see cref="ChibiSimState"/> snapshots. The state at
    /// the START of frame N lives in slot N &amp; mask. <see cref="ChibiSimState.CopyFrom"/> copies every field the
    /// simulation reads, so a restored snapshot re-simulates bit-identically (ChibiDeterminismTests).
    /// </summary>
    public sealed class RollbackStateRing
    {
        readonly ChibiSimState[] _snap;
        readonly int[] _frame;
        readonly int _mask;

        /// <summary>Number of frames kept.</summary>
        public int Capacity => _snap.Length;

        /// <summary>Preallocate <paramref name="capacity"/> snapshots (power of two).</summary>
        /// <example><code>var ring = new RollbackStateRing(RollbackInputBuffer.RingSize);</code></example>
        public RollbackStateRing(int capacity)
        {
            if (capacity <= 0 || (capacity & (capacity - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacity), "power of two");
            _snap = new ChibiSimState[capacity];
            _frame = new int[capacity];
            _mask = capacity - 1;
            for (int i = 0; i < capacity; i++) { _snap[i] = new ChibiSimState(); _frame[i] = -1; }
        }

        /// <summary>Save <paramref name="state"/> as the state at the start of <paramref name="frame"/>.</summary>
        public void Save(int frame, ChibiSimState state)
        {
            int slot = frame & _mask;
            _snap[slot].CopyFrom(state);
            _frame[slot] = frame;
        }

        /// <summary>True when the snapshot of <paramref name="frame"/> is still in the ring.</summary>
        public bool Has(int frame) => frame >= 0 && _frame[frame & _mask] == frame;

        /// <summary>Copy the snapshot of <paramref name="frame"/> into <paramref name="into"/>. False if it was overwritten.</summary>
        public bool TryLoad(int frame, ChibiSimState into)
        {
            if (!Has(frame)) return false;
            into.CopyFrom(_snap[frame & _mask]);
            return true;
        }

        /// <summary>Read-only access to a stored snapshot (checksums). Null if overwritten. Do not mutate.</summary>
        public ChibiSimState Peek(int frame) => Has(frame) ? _snap[frame & _mask] : null;
    }
}
