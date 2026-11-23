using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Deterministic frame history for a rollback session of 2..4 players: per-player input rings keyed by frame, with
    /// an "actual vs predicted" flag per slot, per-remote prediction, misprediction detection and per-remote
    /// contiguous-confirmation cursors. Pure C#, no allocation after construction, not thread-safe.
    ///
    /// Owned by <see cref="ChibiRollbackSession"/> (the one rollback engine, README decision); extracted so the
    /// input rules are unit testable on their own.
    ///
    /// Model
    ///   * A slot is valid for frame N only while <c>frame[slot] == N</c>; older contents are simply stale.
    ///   * Local input is always actual. It is stored <see cref="InputDelay"/> frames ahead of the frame being
    ///     simulated, so the remotes have that long to receive it before it is needed.
    ///   * Remote input that has not arrived is PREDICTED as "same as that remote's newest contiguous actual input".
    ///     The prediction is written into the ring (flagged predicted), so when the actual input arrives we can
    ///     tell whether the frame was simulated with the wrong value.
    ///   * Prediction never invents press edges: inputs are held state and the sim derives presses from the previous
    ///     frame, so repeating the last input can only extend a hold, never fire a new attack.
    ///   * Team play (protocol 6): a remote can be DROPPED at an agreed frame (<see cref="DropRemote"/>). From that frame on
    ///     its input is the actual <see cref="ChibiInputPacket.DroppedRaw"/> sentinel, it no longer holds back
    ///     <see cref="RemoteContiguous"/>, and anything it sends for those frames is ignored.
    /// </summary>
    public sealed class RollbackInputBuffer
    {
        /// <summary>Ring length (power of two). Must exceed the deepest rollback plus the input-delay lead.</summary>
        public const int RingSize = 128;
        const int RingMask = RingSize - 1;
        /// <summary>Most players a buffer can hold (the simulation's fighter limit).</summary>
        public const int MaxPlayers = 4;
        const int NotDropped = int.MaxValue;

        readonly int _local, _count;
        readonly ushort[][] _in;
        readonly int[][] _frame;
        readonly bool[][] _actual;
        readonly int[] _contig = new int[MaxPlayers];
        readonly ushort[] _predBase = new ushort[MaxPlayers];
        readonly int[] _dropAt = new int[MaxPlayers];
        int _firstWrong = -1;

        /// <summary>Frames of local input lead. Frames [0, InputDelay) are neutral for every player on every peer.</summary>
        public int InputDelay { get; }

        /// <summary>Number of players (2..4).</summary>
        public int Count => _count;

        /// <summary>Local player slot.</summary>
        public int LocalIndex => _local;

        /// <summary>
        /// Highest frame such that every frame &lt;= it is actual for EVERY remote (the minimum of the per-remote cursors;
        /// a dropped remote counts as fully known). With one remote this is exactly that remote's cursor. When every
        /// remote is dropped it is <see cref="LocalLast"/>.
        /// </summary>
        public int RemoteContiguous
        {
            get
            {
                int m = int.MaxValue;
                for (int p = 0; p < _count; p++)
                    if (p != _local && _contig[p] < m) m = _contig[p];
                return m == int.MaxValue ? LocalLast : m;
            }
        }

        /// <summary>Newest local frame already stored.</summary>
        public int LocalLast { get; private set; }

        /// <summary>Two-player form: the prediction base of the single remote (the lowest remote slot otherwise).</summary>
        public ushort PredictionBase => _predBase[FirstRemote];

        /// <summary>Earliest frame simulated with a wrong prediction, or -1. Cleared by <see cref="TakeFirstMisprediction"/>.</summary>
        public int FirstMisprediction => _firstWrong;

        int FirstRemote => _local == 0 ? 1 : 0;

        /// <summary>Two-player history for the peer that controls <paramref name="localIndex"/> (0 or 1).</summary>
        /// <example><code>var buf = new RollbackInputBuffer(0, inputDelay: 2);</code></example>
        public RollbackInputBuffer(int localIndex, int inputDelay) : this(localIndex, 2, inputDelay) { }

        /// <summary>History for <paramref name="count"/> players (2..4), local slot <paramref name="localIndex"/>.</summary>
        /// <example><code>var buf = new RollbackInputBuffer(2, 4, inputDelay: 2);</code></example>
        public RollbackInputBuffer(int localIndex, int count, int inputDelay)
        {
            if (count < 2 || count > MaxPlayers) throw new ArgumentOutOfRangeException(nameof(count));
            if (localIndex < 0 || localIndex >= count) throw new ArgumentOutOfRangeException(nameof(localIndex));
            if (inputDelay < 0 || inputDelay >= RingSize / 4) throw new ArgumentOutOfRangeException(nameof(inputDelay));
            _local = localIndex;
            _count = count;
            InputDelay = inputDelay;
            _in = new ushort[count][];
            _frame = new int[count][];
            _actual = new bool[count][];
            for (int p = 0; p < count; p++)
            {
                _in[p] = new ushort[RingSize];
                _frame[p] = new int[RingSize];
                _actual[p] = new bool[RingSize];
                for (int i = 0; i < RingSize; i++) _frame[p][i] = -1;
                for (int f = 0; f < inputDelay; f++) Store(p, f, 0, true);
                _contig[p] = inputDelay - 1;
            }
            for (int p = 0; p < MaxPlayers; p++) _dropAt[p] = NotDropped;
            for (int p = count; p < MaxPlayers; p++) _contig[p] = int.MaxValue;
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

        /// <summary>Two-player form of <see cref="AddRemote(int,int,ushort,int)"/> for the single remote.</summary>
        /// <example><code>buf.AddRemote(f, raw, session.Frame);</code></example>
        public bool AddRemote(int frame, ushort raw, int simulatedBelow) => AddRemote(FirstRemote, frame, raw, simulatedBelow);

        /// <summary>
        /// Record an actual input of remote <paramref name="player"/> received from the wire. <paramref name="simulatedBelow"/>
        /// is the session's next frame to simulate: a differing value for an already simulated frame marks a misprediction.
        /// Returns false when the frame was ignored (bad slot, already confirmed, a duplicate, implausibly far ahead, or at
        /// or after that remote's drop frame).
        /// </summary>
        /// <example><code>buf.AddRemote(3, f, raw, session.Frame);</code></example>
        public bool AddRemote(int player, int frame, ushort raw, int simulatedBelow)
        {
            if (player < 0 || player >= _count || player == _local) return false;
            if (frame >= _dropAt[player]) return false;                         // dropped: the sentinel is final
            int contig = _contig[player];
            if (frame <= contig) return false;                                  // already known
            if (frame > contig + RingSize / 2) return false;                    // implausibly far ahead: would overwrite live slots
            int slot = frame & RingMask;
            bool had = _frame[player][slot] == frame;
            if (had && _actual[player][slot]) return false;                     // duplicate (redundant resend)

            // We already simulated this frame with a prediction that turned out wrong -> roll back to it.
            if (frame < simulatedBelow && had && _in[player][slot] != raw) MarkWrong(frame);
            Store(player, frame, raw, true);

            while (IsActual(player, _contig[player] + 1) && _contig[player] + 1 < _dropAt[player])
            {
                _contig[player]++;
                _predBase[player] = _in[player][_contig[player] & RingMask];
            }
            return true;
        }

        /// <summary>Highest frame such that every frame &lt;= it is actual for <paramref name="player"/> (int.MaxValue once dropped).</summary>
        public int RemoteContiguousOf(int player) => player >= 0 && player < _count ? _contig[player] : int.MaxValue;

        /// <summary>
        /// Last frame of <paramref name="player"/> received contiguously BEFORE any drop (the value a drop vote reports).
        /// Equals <see cref="RemoteContiguousOf"/> while the player is live.
        /// </summary>
        public int LastRealFrameOf(int player)
        {
            if (player < 0 || player >= _count) return -1;
            return _dropAt[player] != NotDropped ? _dropAt[player] - 1 : _contig[player];
        }

        /// <summary>Frame from which <paramref name="player"/> is dropped, or -1 when it is not.</summary>
        public int DropFrameOf(int player) => player >= 0 && player < _count && _dropAt[player] != NotDropped ? _dropAt[player] : -1;

        /// <summary>
        /// Drop remote <paramref name="player"/> from <paramref name="frame"/> on: every input from that frame is the actual
        /// <see cref="ChibiInputPacket.DroppedRaw"/>. Frames before it must already be actual (the drop vote agrees on the
        /// minimum last contiguous frame + 1). If a frame at or after it was already simulated, a misprediction is marked
        /// so the session rolls back and re-simulates with the sentinel. Returns false when the slot is invalid, already
        /// dropped, below the pre-seeded frames, or beyond what that remote confirmed + 1 (the caller must wait).
        /// </summary>
        /// <example><code>buf.DropRemote(3, agreedFrame, session.Frame);</code></example>
        public bool DropRemote(int player, int frame, int simulatedBelow)
        {
            if (player < 0 || player >= _count || player == _local) return false;
            if (_dropAt[player] != NotDropped) return false;
            if (frame < InputDelay || frame > _contig[player] + 1) return false;
            _dropAt[player] = frame;
            if (frame < simulatedBelow) MarkWrong(frame);   // simulated with a prediction: re-run with the sentinel
            _contig[player] = int.MaxValue;
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
            if (frame >= _dropAt[player]) return ChibiInputPacket.DroppedRaw;
            int slot = frame & RingMask;
            if (_frame[player][slot] == frame && (_actual[player][slot] || player == _local)) return _in[player][slot];
            if (player == _local) return 0;      // unreachable in a session: local input is stored ahead of the frame

            ushort pred = _predBase[player];
            Store(player, frame, pred, false);
            return pred;
        }

        /// <summary>True when the input of <paramref name="player"/> for <paramref name="frame"/> is final.</summary>
        public bool IsActual(int player, int frame)
        {
            if (frame >= _dropAt[player]) return true;
            int slot = frame & RingMask;
            return _frame[player][slot] == frame && _actual[player][slot];
        }

        /// <summary>Input of a frame whose inputs are final for every player (replay / confirmation output).</summary>
        public ushort Confirmed(int player, int frame) => frame >= _dropAt[player] ? ChibiInputPacket.DroppedRaw : _in[player][frame & RingMask];

        /// <summary>
        /// Second review, BLOCKER 2: <see cref="Confirmed"/> that checks the ring slot still holds <paramref name="frame"/>
        /// as an actual input. False means the frame was overwritten (or never final): the caller must not emit it.
        /// </summary>
        public bool TryConfirmed(int player, int frame, out ushort raw)
        {
            if (frame >= _dropAt[player]) { raw = ChibiInputPacket.DroppedRaw; return true; }
            int slot = frame & RingMask;
            raw = _in[player][slot];
            return _frame[player][slot] == frame && _actual[player][slot];
        }

        /// <summary>Return the earliest mispredicted frame (or -1) and clear it.</summary>
        public int TakeFirstMisprediction()
        {
            int f = _firstWrong;
            _firstWrong = -1;
            return f;
        }

        void MarkWrong(int frame)
        {
            if (_firstWrong < 0 || frame < _firstWrong) _firstWrong = frame;
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
