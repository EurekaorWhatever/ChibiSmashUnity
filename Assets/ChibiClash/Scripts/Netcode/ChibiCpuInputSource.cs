using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// An input source that reads the simulation state it feeds. The host (<see cref="ChibiLocalSession"/>) calls
    /// <see cref="Bind"/> once with its live state; the source must treat it as read-only.
    /// </summary>
    public interface IChibiStateAwareSource
    {
        /// <summary>Give the source the state it may read at <see cref="IChibiInputSource.SampleTick"/> (start of that tick).</summary>
        void Bind(ChibiSimState state);
    }

    /// <summary>
    /// The CPU opponent as a deterministic input source (port step 17, docs/architecture/netcode/cpu-ai-conversion.md):
    /// a behaviour-for-behaviour port of the original <c>CpuInput</c> (FighterInput.cs:138-228) that plays ONLY through
    /// <see cref="ChibiInputPacket"/>s, exactly like a human, so vs-AI uses the same combat code as online play.
    ///
    /// Determinism: integer math only, one decision per 60 Hz tick (the original's per-frame chances are scaled by
    /// dt·60 = 1), and its own <see cref="ChibiRng"/> seeded from the match seed and the player index. It never touches
    /// <c>state.Rng</c> and never writes the state, so replays (which store inputs only) stay exact.
    /// Difficulty 0 is the still training dummy (always neutral).
    /// </summary>
    /// <example><code>
    /// var cpu = new ChibiCpuInputSource(player: 1, difficulty: 2, matchSeed: cfg.Seed);
    /// var session = new ChibiLocalSession(cfg, human, cpu);   // the session binds its state to the CPU
    /// </code></example>
    public sealed class ChibiCpuInputSource : IChibiInputSource, IChibiStateAwareSource
    {
        /// <summary>Seed salt so the CPU stream never equals the sim stream for the same match seed.</summary>
        public const uint SeedSalt = 0xA15EED00u;
        /// <summary>GestureTracker candidate delay 0.1 s (Pose.cs:190): a CPU pose becomes "stable" 6 ticks after it starts.</summary>
        public const int GestureStableTicks = 6;

        readonly int _player, _difficulty, _skillPermille;
        ChibiRng _rng;
        ChibiSimState _state;

        // CpuInput fields (FI:140-147), in ticks / milli.
        int _think;                 // ticks until the next think (think -= dt)
        int _wantMove;              // -1, 0, +1
        bool _wantBlock;
        int _gestureDelay = -1;     // ticks, -1 = not drawn yet
        bool _gestureWillSucceed;
        int _pose;                  // Gesture id the CPU is performing
        int _poseCandidate, _poseCandidateTicks, _poseStable;   // input-side stability filter
        ushort _prevEmitted;

        /// <summary>Create a CPU for fighter <paramref name="player"/> (0 or 1) at <paramref name="difficulty"/> (0 dummy .. 3 hard).</summary>
        public ChibiCpuInputSource(int player, int difficulty, uint matchSeed)
        {
            if (player < 0 || player > 1) throw new ArgumentOutOfRangeException(nameof(player));
            _player = player;
            _difficulty = difficulty;
            _skillPermille = difficulty * 1000 / 3;                       // skill = difficulty / 3 (FI:158)
            _rng = new ChibiRng(matchSeed ^ SeedSalt ^ (uint)(player + 1) * 0x9E3779B9u);
        }

        /// <summary>Difficulty this CPU plays at (0 = training dummy).</summary>
        public int Difficulty => _difficulty;

        /// <summary>Current CPU RNG state (tests: proves the CPU stream is independent of the sim's).</summary>
        public uint RngState => _rng.State;

        /// <inheritdoc/>
        public void Bind(ChibiSimState state) => _state = state;

        /// <inheritdoc/>
        public void PollRenderFrame() { }

        /// <inheritdoc/>
        public ChibiInputPacket SampleTick()
        {
            if (_state == null) throw new InvalidOperationException("ChibiCpuInputSource: Bind(state) was never called");
            int moveX = 0;
            bool down = false, block = false;
            ChibiButtons press = ChibiButtons.None;
            Think(ref moveX, ref down, ref block, ref press);
            int gesture = StableGesture();

            // Presses are edges in the sim (PrevInput): a press asked for on two ticks in a row must release in between.
            ushort pressBits = (ushort)((ushort)press & ~_prevEmitted);
            var buttons = (ChibiButtons)pressBits;
            if (block) buttons |= ChibiButtons.Shield;
            var p = ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(moveX, down ? -1 : 0), buttons, (Gesture)gesture);
            _prevEmitted = pressBits;
            return p;
        }

        /// <summary>One CpuInput.Tick (FI:154-208) at dt = 1/60, on the state at the start of this tick.</summary>
        void Think(ref int moveX, ref bool down, ref bool block, ref ChibiButtons press)
        {
            if (_difficulty <= 0) return;
            var self = _state.F[_player];
            var opp = _state.F[1 - _player];
            int dx = opp.X - self.X, dy = opp.Y - self.Y;
            int sdx = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            var def = ClassDef.Get((FighterClass)self.Class);
            bool ranged = def.rangedNormals;
            int reach = (int)Math.Round(def.reach * 1000f);

            UpdateUltGesture(self);
            if (!_state.Running) return;   // InputEnabled gate: the original Fighter ignores the CPU's commands too

            // Recover to the stage when knocked off (FI:163-171). Pivot = feet + 0.725, so pivot.y < groundY - 0.5
            // is feet Y < -1225; |pivot.x| > halfWidth - 0.3 is the same on the feet.
            var st = ChibiStageTable.Get(_state.Stage);
            if (Math.Abs(self.X) > st.HalfWidth - 300 || self.Y < -1225)
            {
                moveX = self.X > 0 ? -1 : (self.X < 0 ? 1 : 0);
                if (self.Vy < 0) press |= ChibiButtons.Jump;
                if (self.Vy < -30000 && self.Class == (int)FighterClass.Melee && SkillReady(self, 0)) press |= ChibiButtons.Skill1;
                return;
            }

            if (--_think <= 0)
            {
                // think = Range(0.1, 0.3) / max(0.4, skill): 6..18 ticks scaled.
                int baseTicks = 6 + _rng.Range(13);
                _think = baseTicks * 1000 / Math.Max(400, _skillPermille);
                int preferred = ranged ? 4500 : reach * 8 / 10;
                int dist = Math.Abs(dx);
                if (dist > preferred + 400) _wantMove = sdx;
                else if (ranged && dist < preferred - 1500) _wantMove = -sdx;
                else _wantMove = 0;
                bool oppAttacking = opp.MoveSlot >= 0;                              // IsAttacking = actionLock > 0
                _wantBlock = oppAttacking && dist < 2500 && _rng.Roll(150 + 350 * _skillPermille / 1000);
                if (dy > 1500 && _rng.Roll(600)) press |= ChibiButtons.Jump;
            }

            moveX = _wantMove;
            block = _wantBlock;
            if (_wantBlock) return;

            bool facingOpp = sdx == self.Facing || Math.Abs(dx) < 200;
            // The original creeps at MoveX 0.2 to turn; the sim's stick is digital, so one tick of full input turns it.
            if (_wantMove == 0 && !facingOpp) moveX = sdx;

            int attackPermille = 60 + 120 * _skillPermille / 1000;
            bool inMelee = Math.Abs(dx) < reach + 400 && Math.Abs(dy) < 1200;
            bool inLine = Math.Abs(dy) < 1000;
            if ((ranged ? inLine : inMelee) && facingOpp && _rng.Roll(attackPermille))
                press |= _rng.Roll(750) ? ChibiButtons.Light : ChibiButtons.Heavy;

            if (_rng.Roll(12 * _skillPermille / 1000))
            {
                int k = _rng.Range(Math.Max(1, def.SkillCount));
                if (k < def.SkillCount && SkillReady(self, k) && (inMelee || ranged || Math.Abs(dx) < 5000))
                    press |= (ChibiButtons)((int)ChibiButtons.Skill1 << k);
            }

            if (self.Ult >= ChibiSimConst.UltMax && self.UltWindowTicks == 0 && _rng.Roll(20)) press |= ChibiButtons.Ultimate;
        }

        /// <summary>UpdateUltGesture (FI:210-227): inside the window, wait 0.6..2.2 s, then perform the class gesture (60% + 12%·difficulty).</summary>
        void UpdateUltGesture(ChibiFighterSim self)
        {
            if (self.UltWindowTicks > 0)
            {
                if (_gestureDelay < 0)
                {
                    _gestureDelay = 36 + _rng.Range(97);                             // 0.6 .. 2.2 s
                    _gestureWillSucceed = _rng.Roll(600 + 120 * _difficulty);
                }
                _gestureDelay--;
                if (_gestureDelay <= 0 && _gestureWillSucceed) _pose = ChibiSimConst.UltGesture[self.Class];
            }
            else
            {
                _gestureDelay = -1;
                _pose = 0;
            }
        }

        /// <summary>The CPU's SimulatedPoseSource goes through a GestureTracker too: a pose is sent once held 0.1 s.</summary>
        int StableGesture()
        {
            if (_pose == _poseCandidate) _poseCandidateTicks++;
            else { _poseCandidate = _pose; _poseCandidateTicks = 1; }
            if (_poseCandidate != _poseStable && _poseCandidateTicks >= GestureStableTicks) _poseStable = _poseCandidate;
            return _poseStable;
        }

        static bool SkillReady(ChibiFighterSim f, int k) =>
            k >= 0 && k < 3 && f.Cd[k] <= 0 && ChibiMoveTable.Get(f.Class, 2 + k).Exists;
    }
}
