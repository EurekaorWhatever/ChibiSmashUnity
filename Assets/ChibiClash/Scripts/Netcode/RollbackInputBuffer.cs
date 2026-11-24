using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Deterministic frame history for a 2-4 player rollback session: per-player input rings keyed by frame, with an
    /// "actual vs predicted" flag per slot, remote-input prediction, misprediction detection and one contiguous-confirmation
    /// cursor PER REMOTE stream (four-player-design.md 2.2). Pure C#, no allocation after construction, not thread-safe.
    ///
    /// Owned by <see cref="ChibiRollbackSession"/> (the one rollback engine, README decision); extracted so the
    /// input rules are unit testable on their own.
    ///
    /// Model
    ///   * A slot is valid for frame N only while <c>frame[slot] == N</c>; older contents are simply stale.
    ///   * Local input is always actual. It is stored <see cref="InputDelay"/> frames ahead of the frame being
    ///     simulated, so the remotes have that long to receive it before it is needed.
    ///   * Remote input that has not arrived is PREDICTED as "same as that player's newest contiguous actual input".
    ///     The prediction is written into the ring (flagged predicted), so when the actual input arrives we can
    ///     tell whether the frame was simulated with the wrong value.
    ///   * Prediction never invents press edges: inputs are held state and the sim derives presses from the previous
    ///     frame, so repeating the last input can only extend a hold, never fire a new attack.
    ///   * A player who LEFT at frame F (<see cref="SetLeft"/>) has neutral, actual input from F on and is not present
    ///     (<see cref="PresentMask"/>); its stream stops holding back <see cref="RemoteContiguous"/> once frames before F are in.
    /// </summary>
    public sealed class RollbackInputBuffer
    {
        /// <summary>Ring length (power of two). Must exceed the deepest rollback plus the input-delay lead.</summary>
        public const int RingSize = 128;
        const int RingMask = RingSize - 1;
        /// <summary>"Never left" / "no live remote" sentinel, far from overflow when 1 is added.</summary>
        public const int Never = int.MaxValue / 2;

        readonly int _local, _players;
        readonly ushort[][] _in;
        readonly int[][] _frame;
        readonly bool[][] _actual;
        readonly int[] _contig;
        readonly ushort[] _predict;
        readonly int[] _leftAt;
        int _firstWrong = -1;

        /// <summary>Frames of local input lead. Frames [0, InputDelay) are neutral for every player on every peer.</summary>
        public int InputDelay { get; }

        /// <summary>Players in the match (2..4).</summary>
        public int Players => _players;

        /// <summary>Slot of the local player.</summary>
        public int LocalIndex => _local;

        /// <summary>
        /// Highest frame such that every remote input &lt;= it is known (minimum over the remotes still in the match;
        /// a remote that left counts once every frame before its leave frame is in). <see cref="Never"/> when no remote is live.
        /// </summary>
        public int RemoteContiguous
        {
            get
            {
                int min = Never;
                for (int p = 0; p < _players; p++)
                {
                    if (p == _local) continue;
                    int c = _contig[p];
                    if (_leftAt[p] != Never && c >= _leftAt[p] - 1) continue;   // left and complete: no longer limits
                    if (c < min) min = c;
                }
                return min;
            }
        }

        /// <summary>Contiguous actual frame of one remote stream (its own cursor, leave not applied).</summary>
        public int ContiguousOf(int player) => _contig[player];

        /// <summary>Newest local frame already stored.</summary>
        public int LocalLast { get; private set; }

        /// <summary>The value used to predict missing frames of the first remote (2-player compatibility).</summary>
        public ushort PredictionBase => _predict[FirstRemote];

        /// <summary>The value used to predict missing frames of <paramref name="player"/>.</summary>
        public ushort PredictionOf(int player) => _predict[player];

        /// <summary>Earliest frame simulated with a wrong prediction, or -1. Cleared by <see cref="TakeFirstMisprediction"/>.</summary>
        public int FirstMisprediction => _firstWrong;

        int FirstRemote => _local == 0 ? 1 : 0;

        /// <summary>Create the history for the peer that controls <paramref name="localIndex"/> (0 or 1) in a 1v1.</summary>
        /// <example><code>var buf = new RollbackInputBuffer(0, inputDelay: 2);</code></example>
        public RollbackInputBuffer(int localIndex, int inputDelay) : this(localIndex, inputDelay, 2) { }

        /// <summary>Create the history for <paramref name="players"/> (2..4) players; this peer controls <paramref name="localIndex"/>.</summary>
        /// <example><code>var buf = new RollbackInputBuffer(2, inputDelay: 3, players: 4);</code></example>
        public RollbackInputBuffer(int localIndex, int inputDelay, int players)
        {
            if (players < 2 || players > ChibiSimState.MaxFighters) throw new ArgumentOutOfRangeException(nameof(players));
            if (localIndex < 0 || localIndex >= players) throw new ArgumentOutOfRangeException(nameof(localIndex));
            if (inputDelay < 0 || inputDelay >= RingSize / 4) throw new ArgumentOutOfRangeException(nameof(inputDelay));
            _local = localIndex;
            _players = players;
            InputDelay = inputDelay;
            _in = new ushort[players][];
            _frame = new int[players][];
            _actual = new bool[players][];
            _contig = new int[players];
            _predict = new ushort[players];
            _leftAt = new int[players];
            for (int p = 0; p < players; p++)
            {
                _in[p] = new ushort[RingSize];
                _frame[p] = new int[RingSize];
                _actual[p] = new bool[RingSize];
                for (int i = 0; i < RingSize; i++) _frame[p][i] = -1;
                for (int f = 0; f < inputDelay; f++) Store(p, f, 0, true);
                _contig[p] = inputDelay - 1;
                _leftAt[p] = Never;
            }
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
            _contig[_local] = f;
            return f;
        }

        /// <summary>Local input of <paramref name="frame"/> (must be within the last RingSize frames and stored).</summary>
        public ushort LocalInput(int frame) => StreamInput(_local, frame);

        /// <summary>Actual input of any player for a frame still in the ring (forwarding). Throws if it is not actual.</summary>
        public ushort StreamInput(int player, int frame)
        {
            int slot = frame & RingMask;
            if (_frame[player][slot] != frame || !_actual[player][slot])
                throw new InvalidOperationException("input of player " + player + " for frame " + frame + " is not in the ring");
            return _in[player][slot];
        }

        // ================================================================== remote side

        /// <summary>2-player form of <see cref="AddRemote(int,int,ushort,int)"/> for the single remote.</summary>
        /// <example><code>buf.AddRemote(f, raw, session.Frame);</code></example>
        public bool AddRemote(int frame, ushort raw, int simulatedBelow) => AddRemote(FirstRemote, frame, raw, simulatedBelow);

        /// <summary>
        /// Record an actual input of remote <paramref name="player"/> received from the wire. <paramref name="simulatedBelow"/>
        /// is the session's next frame to simulate: a differing value for an already simulated frame marks a misprediction.
        /// Returns false when the frame was ignored (local slot, already confirmed, a duplicate, implausibly far ahead, or
        /// at / after the player's leave frame).
        /// </summary>
        public bool AddRemote(int player, int frame, ushort raw, int simulatedBelow)
        {
            if (player < 0 || player >= _players || player == _local) return false;
            if (frame <= _contig[player]) return false;                       // already known
            if (frame >= _leftAt[player]) return false;                       // the player is gone from this frame on
            if (frame > _contig[player] + RingSize / 2) return false;         // implausibly far ahead: would overwrite live slots
            int slot = frame & RingMask;
            bool had = _frame[player][slot] == frame;
            if (had && _actual[player][slot]) return false;                   // duplicate (redundant resend)

            // We already simulated this frame with a prediction that turned out wrong -> roll back to it.
            if (frame < simulatedBelow && had && _in[player][slot] != raw) MarkWrong(frame);
            Store(player, frame, raw, true);

            while (IsActual(player, _contig[player] + 1))
            {
                _contig[player]++;
                _predict[player] = _in[player][_contig[player] & RingMask];
            }
            return true;
        }

        /// <summary>
        /// Player <paramref name="player"/> leaves at <paramref name="frame"/>: from that frame on its input is neutral and its
        /// presentMask bit is clear. Frames already simulated at or after it are re-simulated (misprediction). Returns false
        /// (and changes nothing) for the local slot, a player already gone, or a frame the player's actual inputs already
        /// cover (an honest host never announces one).
        /// </summary>
        /// <example><code>buf.SetLeft(2, 431, session.Frame);</code></example>
        public bool SetLeft(int player, int frame, int simulatedBelow)
        {
            if (player < 0 || player >= _players || player == _local) return false;
            if (_leftAt[player] != Never) return _leftAt[player] == frame;    // repeated announcement: idempotent
            if (frame <= _contig[player] || frame < InputDelay) return false;
            _leftAt[player] = frame;
            if (frame < simulatedBelow) MarkWrong(frame);
            return true;
        }

        /// <summary>True when <see cref="SetLeft"/> would accept (or already holds) this leave; changes nothing.</summary>
        public bool CanSetLeft(int player, int frame)
        {
            if (player < 0 || player >= _players || player == _local) return false;
            if (_leftAt[player] != Never) return _leftAt[player] == frame;
            return frame > _contig[player] && frame >= InputDelay;
        }

        /// <summary>Leave frame of <paramref name="player"/>, or <see cref="Never"/>.</summary>
        public int LeftAt(int player) => _leftAt[player];

        /// <summary>presentMask for <paramref name="frame"/>: bit p set while player p has not left.</summary>
        public int PresentMask(int frame)
        {
            int m = 0;
            for (int p = 0; p < _players; p++) if (frame < _leftAt[p]) m |= 1 << p;
            return m;
        }

        // ================================================================== simulation reads

        /// <summary>
        /// Input of <paramref name="player"/> for <paramref name="frame"/>: the actual value when known, neutral after the
        /// player left, otherwise the prediction (recorded in the ring so a later correction is detected).
        /// </summary>
        /// <example><code>ushort i1 = buf.Get(1, frame);</code></example>
        public ushort Get(int player, int frame)
        {
            if (frame >= _leftAt[player]) return 0;
            int slot = frame & RingMask;
            if (_frame[player][slot] == frame && (_actual[player][slot] || player == _local)) return _in[player][slot];
            if (player == _local) return 0;      // unreachable in a session: local input is stored ahead of the frame

            Store(player, frame, _predict[player], false);
            return _predict[player];
        }

        /// <summary>True when the input of <paramref name="player"/> for <paramref name="frame"/> is final.</summary>
        public bool IsActual(int player, int frame)
        {
            if (frame >= _leftAt[player]) return true;
            int slot = frame & RingMask;
            return _frame[player][slot] == frame && _actual[player][slot];
        }

        /// <summary>Input of a frame whose inputs are final for every player (replay / confirmation output).</summary>
        public ushort Confirmed(int player, int frame) => frame >= _leftAt[player] ? (ushort)0 : _in[player][frame & RingMask];

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
