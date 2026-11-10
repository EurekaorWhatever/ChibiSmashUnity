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
    /// the original <c>CpuInput</c> (FighterInput.cs:138-228) extended for the rules v18 movesets
    /// (design/gdd/movesets/README.md section 1). It plays ONLY through <see cref="ChibiInputPacket"/>s, exactly like a
    /// human, so vs-AI uses the same combat code as online play.
    ///
    /// Moveset use (per-class preferences in <see cref="Options"/> / <see cref="Profile"/>): tilts / jab at close range,
    /// up moves when the opponent is above, smashes as punish / kill moves (charged against long hitstun at difficulty
    /// 2+), dash attacks and side specials to approach, projectiles to zone, traps, positional aerials (fair / bair by
    /// side, uair above, dair below, more often when edge-guarding), air down specials (dives) onto a grounded opponent,
    /// up special (then a technique) to recover, counters / reflectors timed against the opponent's startup
    /// (difficulty 2+), Skill+Up / Skill+Down techniques mixed into those pools. Whether a move connects is read from
    /// its own row (hitbox, lunge, projectile range), so the AI follows the data.
    ///
    /// Determinism: integer math only, one decision per 60 Hz tick, and its own <see cref="ChibiRng"/> seeded from the
    /// match seed and the player index. It never touches <c>state.Rng</c> and never writes the state, so replays
    /// (which store inputs only) stay exact. Difficulty 0 is the still training dummy (always neutral).
    ///
    /// Inputs: a press is one tick, released for at least one tick before the same button is pressed again; the stick
    /// direction that selects the move is held on the press tick. Deliberate holds (charge / hold moves) and the dash
    /// attack sequence run as short pre-built plans.
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
        /// <summary>Longest deliberate hold of a button (charge / hold moves), ticks. Only Heavy, Special and Skill3 are held.</summary>
        public const int MaxHoldTicks = 48;

        // ================================================================== tuning data (integers / permille)

        /// <summary>Option contexts.</summary>
        const int CtxClose = 0, CtxAbove = 1, CtxPunish = 2, CtxMid = 3, CtxFar = 4, CtxCounter = 5, CtxProjDef = 6,
                  CtxTrap = 7, CtxAir = 8;
        /// <summary>Option buttons: 0 L, 1 H, 2 B, 3-5 S1-S3 (as <see cref="ChibiMoveExt.Button"/>), 6 dash attack.</summary>
        const int BtnL = 0, BtnH = 1, BtnB = 2, BtnS1 = 3, BtnS2 = 4, BtnS3 = 5, BtnDash = 6;
        /// <summary>Stick directions (as <see cref="ChibiMoveExt.Dir"/>): neutral, forward, back, up, down.</summary>
        const int DN = 0, DF = 1, DBK = 2, DU = 3, DD = 4;
        /// <summary>Row width of <see cref="Options"/>: context, button, dir, weight, min difficulty, hold ticks.</summary>
        const int OptW = 6;

        /// <summary>
        /// Per-class move preferences (class index = <see cref="FighterClass"/>: Kaia Melee, Bruna Heavy, Ren Ranged,
        /// Lyra Magic, Taro Polearm). Rows of { context, button, dir, weight, min difficulty, hold ticks }. A row is only
        /// eligible when its move exists, can start (cooldown, aironce, reload, SpearOut) and its hitbox reaches.
        /// Hold 0 on a chargeable smash = charge against hitstun at difficulty 2+.
        /// </summary>
        static readonly int[][] Options =
        {
            new[] // Kaia (Melee): rushdown, lunges, counters
            {
                CtxClose, BtnL, DN, 300, 1, 0,     CtxClose, BtnL, DF, 300, 1, 0,     CtxClose, BtnL, DD, 200, 1, 0,
                CtxClose, BtnB, DN, 150, 1, 0,     CtxClose, BtnH, DN, 100, 1, 0,
                CtxAbove, BtnL, DU, 500, 1, 0,     CtxAbove, BtnH, DU, 200, 2, 0,     CtxAbove, BtnB, DU, 100, 2, 0,
                CtxPunish, BtnH, DF, 400, 1, 0,    CtxPunish, BtnH, DD, 200, 1, 0,    CtxPunish, BtnH, DU, 150, 1, 0,
                CtxPunish, BtnS3, DU, 200, 2, 40,  CtxPunish, BtnS2, DD, 150, 2, 0,
                CtxMid, BtnB, DF, 400, 1, 0,       CtxMid, BtnDash, DF, 300, 1, 0,    CtxMid, BtnDash, DD, 200, 1, 0,
                CtxCounter, BtnS2, DU, 600, 2, 0,  CtxCounter, BtnS1, DD, 150, 2, 0,
                CtxTrap, BtnB, DD, 1000, 1, 0,
                CtxAir, BtnB, DN, 150, 1, 0,       CtxAir, BtnB, DF, 100, 1, 0,
            },
            new[] // Bruna (Heavy): armour, big punishes, hammer rush approach
            {
                CtxClose, BtnL, DN, 300, 1, 0,     CtxClose, BtnL, DF, 300, 1, 0,     CtxClose, BtnL, DD, 250, 1, 0,
                CtxClose, BtnH, DN, 100, 1, 0,     CtxClose, BtnS1, DD, 80, 2, 0,
                CtxAbove, BtnL, DU, 400, 1, 0,     CtxAbove, BtnS1, DU, 300, 1, 0,    CtxAbove, BtnH, DU, 200, 2, 0,
                CtxPunish, BtnH, DF, 400, 1, 0,    CtxPunish, BtnH, DD, 250, 1, 0,    CtxPunish, BtnH, DU, 150, 1, 0,
                CtxPunish, BtnS1, DD, 200, 1, 0,   CtxPunish, BtnS2, DU, 150, 2, 0,
                CtxMid, BtnB, DF, 400, 1, 0,       CtxMid, BtnB, DN, 300, 1, 0,       CtxMid, BtnDash, DF, 250, 1, 0,
                CtxCounter, BtnS2, DD, 500, 2, 0,  CtxCounter, BtnB, DD, 300, 2, 30,
                CtxAir, BtnB, DF, 100, 1, 0,
            },
            new[] // Ren (Ranged): zoning, traps, keeps away
            {
                CtxClose, BtnL, DF, 300, 1, 0,     CtxClose, BtnL, DD, 300, 1, 0,     CtxClose, BtnS1, DD, 250, 1, 0,
                CtxAbove, BtnL, DU, 400, 1, 0,     CtxAbove, BtnS3, DU, 300, 1, 0,    CtxAbove, BtnH, DU, 150, 2, 0,
                CtxPunish, BtnH, DF, 400, 1, 0,    CtxPunish, BtnH, DD, 200, 1, 0,    CtxPunish, BtnH, DU, 150, 1, 0,
                CtxMid, BtnB, DF, 350, 1, 0,       CtxMid, BtnS3, DD, 250, 1, 0,      CtxMid, BtnS1, DU, 200, 2, 0,
                CtxMid, BtnB, DN, 300, 1, 0,
                CtxFar, BtnL, DN, 450, 1, 0,       CtxFar, BtnH, DN, 150, 1, 0,       CtxFar, BtnB, DN, 300, 1, 40,
                CtxFar, BtnB, DF, 200, 1, 0,       CtxFar, BtnS3, DD, 100, 1, 0,
                CtxCounter, BtnS1, DD, 300, 2, 0,
                CtxTrap, BtnB, DD, 1000, 1, 0,
                CtxAir, BtnB, DN, 200, 1, 20,      CtxAir, BtnB, DF, 150, 1, 0,
            },
            new[] // Lyra (Magic): zoning, glyphs, reflector
            {
                CtxClose, BtnL, DF, 300, 1, 0,     CtxClose, BtnL, DD, 250, 1, 0,     CtxClose, BtnS1, DU, 150, 1, 0,
                CtxClose, BtnB, DD, 150, 1, 0,
                CtxAbove, BtnL, DU, 400, 1, 0,     CtxAbove, BtnS1, DU, 300, 1, 0,    CtxAbove, BtnH, DU, 150, 2, 0,
                CtxPunish, BtnH, DF, 350, 1, 0,    CtxPunish, BtnH, DD, 250, 1, 0,    CtxPunish, BtnH, DU, 150, 1, 0,
                CtxMid, BtnB, DF, 400, 1, 0,       CtxMid, BtnS1, DD, 300, 1, 0,      CtxMid, BtnB, DN, 300, 1, 0,
                CtxFar, BtnL, DN, 450, 1, 0,       CtxFar, BtnH, DN, 150, 1, 0,       CtxFar, BtnB, DN, 400, 1, 0,
                CtxCounter, BtnS2, DD, 400, 2, 0,
                CtxProjDef, BtnS2, DD, 1000, 2, 0,
                CtxAir, BtnB, DN, 200, 1, 0,       CtxAir, BtnB, DF, 150, 1, 0,
            },
            new[] // Taro (Polearm): long pokes, launchers, counters
            {
                CtxClose, BtnL, DN, 300, 1, 0,     CtxClose, BtnL, DF, 300, 1, 0,     CtxClose, BtnL, DD, 200, 1, 0,
                CtxClose, BtnS2, DU, 150, 1, 0,    CtxClose, BtnH, DN, 100, 1, 0,
                CtxAbove, BtnL, DU, 400, 1, 0,     CtxAbove, BtnS1, DU, 300, 1, 0,    CtxAbove, BtnH, DU, 150, 2, 0,
                CtxPunish, BtnH, DF, 450, 1, 0,    CtxPunish, BtnH, DD, 200, 1, 0,    CtxPunish, BtnH, DU, 150, 1, 0,
                CtxMid, BtnB, DF, 350, 1, 0,       CtxMid, BtnB, DN, 300, 1, 0,       CtxMid, BtnDash, DF, 200, 1, 0,
                CtxFar, BtnB, DN, 400, 1, 0,
                CtxCounter, BtnS2, DD, 400, 2, 0,  CtxCounter, BtnB, DD, 300, 2, 0,
                CtxProjDef, BtnB, DD, 1000, 2, 0,
                CtxAir, BtnS2, DU, 150, 1, 0,      CtxAir, BtnB, DN, 100, 1, 0,
            },
        };

        /// <summary>Profile columns.</summary>
        const int PReach = 0, PJump = 1, PApproach = 2, PZone = 3, PTrap = 4, PDive = 5, PCounter = 6, PRecoverBtn = 7;

        /// <summary>
        /// Per-class profile: { spacing reach (milli, the original ClassDef.reach), approach-jump permille per think,
        /// approach permille per tick at mid range, zoning permille per tick at range, trap permille per tick, dive
        /// permille per tick, counter permille at difficulty 3 (half at 2), recovery technique button (Skill+Up, -1 none) }.
        /// </summary>
        static readonly int[][] Profile =
        {
            new[] { 1350, 200, 30, 0, 4, 0, 500, BtnS1 },    // Kaia: Air Kick-off restores the air jump
            new[] { 1550, 100, 22, 6, 0, 25, 450, BtnS2 },   // Bruna: Rocket Hammer Strike
            new[] { 900, 80, 12, 80, 8, 20, 300, -1 },      // Ren
            new[] { 900, 80, 12, 80, 6, 20, 400, BtnS2 },   // Lyra: Blink Ascension+
            new[] { 2000, 150, 25, 12, 0, 25, 500, -1 },     // Taro
        };

        /// <summary>Opponent HP at or below this permille of max counts as killable (smash pressure).</summary>
        const int KillHpPermille = 300;
        /// <summary>Total live projectiles above which no new projectile move is started (pool is 64).</summary>
        const int ProjSoftCap = 40;

        // ================================================================== state

        readonly int _player, _difficulty, _skillPermille;
        ChibiRng _rng;
        ChibiSimState _state;

        // CpuInput fields (FI:140-147), in ticks / milli.
        int _think;                 // ticks until the next think (think -= dt)
        int _wantMove;              // -1, 0, +1
        bool _wantBlock;
        int _thinkSide;             // side of the opponent at the last think (cross-up re-think)
        int _gestureDelay = -1;     // ticks, -1 = not drawn yet
        bool _gestureWillSucceed;
        int _pose;                  // Gesture id the CPU is performing
        int _poseCandidate, _poseCandidateTicks, _poseStable;   // input-side stability filter
        ushort _prevEmitted;

        // Moveset additions.
        int _dx, _dy;                                           // stick of this tick
        readonly ushort[] _plan = new ushort[MaxHoldTicks + 16];  // pre-built packets (dir + buttons) for the next ticks
        int _planLen, _planPos;
        int _oppSlotSeen = -1, _oppTickSeen;                    // opponent move tracking (one counter roll per move)
        bool _counterRolled;
        readonly int[] _pick = new int[32];                     // eligible option rows of this decision (no allocation)
        int _pendingStep = -1;                                  // GameTicks when the last press was sent (-1 = consumed)
        ushort _pendingDir;                                     // stick of that press

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

            // Hitstop / cinematic / slow motion: real ticks without a game step. The sim latches a press edge but resolves
            // it with the stick of the tick whose game step runs, so until a step has consumed the last press the CPU
            // keeps that stick and presses nothing (no new decisions, no RNG draws). A deliberate hold is dropped (the
            // charge just ends early) so a frozen stretch never lengthens a hold or resumes it as a second press.
            if (_pendingStep >= 0 && _state.GameTicks == _pendingStep && _state.Running)
            {
                _planLen = 0;
                _prevEmitted = 0;
                var frozen = new ChibiInputPacket { Raw = _pendingDir };
                frozen.Gesture = (Gesture)StableGesture();
                return frozen;
            }
            _pendingStep = -1;
            _dx = 0; _dy = 0;
            bool block = false;
            ChibiButtons press = ChibiButtons.None;
            bool planned = Think(ref block, ref press);
            int gesture = StableGesture();

            // Presses are edges in the sim (PrevInput): a press asked for on two ticks in a row must release in between.
            // Plan ticks are emitted as built (a hold is one deliberate press).
            ushort pressBits = planned ? (ushort)press : (ushort)((ushort)press & ~_prevEmitted);
            var buttons = (ChibiButtons)pressBits;
            if (block) buttons |= ChibiButtons.Shield;
            var p = ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(_dx, _dy), buttons, (Gesture)gesture);
            ushort edges = (ushort)(pressBits & ~_prevEmitted);
            _prevEmitted = pressBits;
            if (edges != 0)
            {
                _pendingStep = _state.GameTicks;
                _pendingDir = (ushort)(p.Raw & ChibiInputPacket.DirMask);
            }
            return p;
        }

        /// <summary>One CpuInput.Tick (FI:154-208) at dt = 1/60 plus the moveset layer, on the state at the start of this tick.</summary>
        bool Think(ref bool block, ref ChibiButtons press)
        {
            if (_difficulty <= 0) return false;
            var self = _state.F[_player];
            var opp = _state.F[1 - _player];

            UpdateUltGesture(self);
            if (!_state.Running) { _planLen = 0; return false; }   // InputEnabled gate: the original Fighter ignores the CPU too
            TrackOpponentMove(opp);

            if (_planLen > 0)
            {
                if (self.Stun > 0 || self.Held) _planLen = 0;          // interrupted: drop the rest (releases everything)
                else
                {
                    var pk = new ChibiInputPacket { Raw = _plan[_planPos++] };
                    if (_planPos >= _planLen) _planLen = 0;
                    _dx = pk.MoveX; _dy = pk.MoveY;
                    press = (ChibiButtons)(pk.Raw & ~ChibiInputPacket.DirMask);
                    return true;
                }
            }

            int dx = opp.X - self.X, dy = opp.Y - self.Y;
            int sdx = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int adx = Math.Abs(dx);
            int cls = self.Class;
            var prof = Profile[cls];
            bool ranged = cls == (int)FighterClass.Ranged || cls == (int)FighterClass.Magic;
            int reach = prof[PReach];

            // Recover to the stage when knocked off (FI:163-171). Pivot = feet + 0.725, so pivot.y < groundY - 0.5
            // is feet Y < -1225; |pivot.x| > halfWidth - 0.3 is the same on the feet.
            var st = ChibiStageTable.Get(_state.Stage);
            if (Math.Abs(self.X) > st.HalfWidth - 300 || self.Y < -1225)
            {
                Recover(self, st, ref press);
                return false;
            }

            // A cross-up (the opponent changed sides since the last think) is re-evaluated at once.
            if (sdx != 0 && sdx != _thinkSide) _think = 0;
            if (--_think <= 0)
            {
                // think = Range(0.1, 0.3) / max(0.4, skill): 6..18 ticks scaled.
                int baseTicks = 6 + _rng.Range(13);
                _think = baseTicks * 1000 / Math.Max(400, _skillPermille);
                _thinkSide = sdx;
                int preferred = ranged ? 4500 : reach * 8 / 10;
                if (adx > preferred + 400) _wantMove = sdx;
                else if (ranged && adx < preferred - 1500) _wantMove = -sdx;
                else _wantMove = 0;
                bool oppAttacking = opp.MoveSlot >= 0;                              // IsAttacking = actionLock > 0
                _wantBlock = oppAttacking && adx < 2500 && _rng.Roll(150 + 350 * _skillPermille / 1000);
                if (dy > 1500 && _rng.Roll(600)) press |= ChibiButtons.Jump;
                // Approach jump into an aerial (moveset layer): from just outside reach, at the opponent's height.
                else if (self.Grounded && adx > reach && adx < reach + 2600 && Math.Abs(dy) < 1500 && _rng.Roll(prof[PJump]))
                {
                    press |= ChibiButtons.Jump;
                    _wantMove = sdx;
                }
            }

            _dx = _wantMove;
            block = _wantBlock;
            bool free = self.MoveSlot < 0 && self.Stun == 0 && self.DashTicks == 0 && !self.Held;

            // Counters / reflectors (difficulty 2+) replace the shield when the timing fits.
            if (free && _difficulty >= 2 && TryDefend(self, opp, dx, dy, ref press)) { block = false; return false; }
            if (_wantBlock) return false;

            bool facingOpp = sdx == self.Facing || adx < 200;
            // The original creeps at MoveX 0.2 to turn; the sim's stick is digital, so one tick of full input turns it.
            if (_wantMove == 0 && !facingOpp && self.Grounded) _dx = sdx;

            if (free && press == ChibiButtons.None)
            {
                if (self.Grounded) GroundAttack(self, opp, st, dx, dy, ranged, reach, ref press);
                else AirAttack(self, opp, st, dx, dy, ref press);
            }

            // Random existing skill (FI:201-205), neutral stick so the sim resolves the old skill.
            if (press == ChibiButtons.None && _rng.Roll(12 * _skillPermille / 1000))
            {
                int count = SkillCount(cls);
                int k = _rng.Range(Math.Max(1, count));
                bool inMelee = adx < reach + 400 && Math.Abs(dy) < 1200;
                if (k < count && SkillReady(self, k) && (inMelee || ranged || adx < 5000))
                {
                    var b = (ChibiButtons)((int)ChibiButtons.Skill1 << k);
                    if (CanPress(b)) { press |= b; _dy = 0; }
                }
            }

            if (self.Ult >= ChibiSimConst.UltMax && self.UltWindowTicks == 0 && _rng.Roll(20)) press |= ChibiButtons.Ultimate;
            return false;
        }

        // ================================================================== recovery

        /// <summary>
        /// Off-stage (FI:163-171 plus the moveset): drift back (out from under the stage first), air jump once below stage
        /// level, air dash to cover distance (it also stops the fall), then the up special once drifting in (or close, or
        /// low), then the class's recovery technique (Skill+Up), and Kaia's Dash &amp; Thrust when falling fast while facing
        /// the stage (air facing is locked since rules v18, OD-1, so facing out would thrust away).
        /// </summary>
        void Recover(ChibiFighterSim self, ChibiStageDef st, ref ChibiButtons press)
        {
            int toCentre = self.X > 0 ? -1 : (self.X < 0 ? 1 : 0);
            bool under = self.Y < -300 && Math.Abs(self.X) < st.HalfWidth - 200;   // truly beneath, not at the side wall
            _dx = under ? (toCentre != 0 ? -toCentre : 1) : toCentre;
            int outside = Math.Abs(self.X) - st.HalfWidth;             // > 0: beyond the edge
            bool canAct = self.MoveSlot < 0 && self.Stun == 0 && !self.Held && self.DashTicks == 0 && !self.Grounded;

            if (self.Vy < 0 && self.Y < 500 && (self.AirJumps > 0 || !canAct)) press |= ChibiButtons.Jump;
            else if (canAct && !under && outside > 1200 && self.DashCd == 0 && CanPress(ChibiButtons.Dash))
            {
                press = ChibiButtons.Dash;
                return;
            }

            int upSlot = ChibiMovesetData.SlotFor(self.Class, BtnB, DU, true);
            bool tether = upSlot >= 0 && ChibiMoveTable.Get(self.Class, upSlot).Ext.Has(ChibiMoveExt.FTetherSelf);
            if (canAct && !under && self.Vy < 0 && (self.AirJumps == 0 || self.Y < -4500) && (self.Y < 300 || outside < 1500 || tether))
            {
                int cls = self.Class;
                int up = upSlot;
                if (up >= 0 && CanStartNew(self, up))
                {
                    // Below the stage top the side wall stops all horizontal speed (and there is no air control during
                    // the move): start the rise only when it clears the top before reaching the wall, and steer to keep
                    // that room. Teleports and tethers do not need it; a row with its own SelfVx brings its own speed.
                    var mv = ChibiMoveTable.Get(cls, up);
                    bool timed = mv.SelfVy > 0 && !mv.Ext.Has(ChibiMoveExt.FTeleport) && self.Y < 0 && self.Y > -3500;
                    if (timed)
                    {
                        int vin = mv.SelfVx != 0 ? mv.SelfVx * self.Facing * toCentre : (int)((long)self.Vx * toCentre / ChibiSimConst.VelSub);
                        int room = outside - ChibiSimConst.HurtHalfW;
                        int clear = (-self.Y + 200) * 13 / (mv.SelfVy * 10) + 1;
                        if (vin > 0 && (long)vin * clear > room) { if (mv.SelfVx == 0) _dx = -toCentre; timed = true; }
                        else timed = vin <= 0 && mv.SelfVx == 0;              // not moving in yet: keep drifting in
                    }
                    // Tether (Ren's Grapple Arrow, 60 deg up-forward): the pull starts after startup + flight (free fall,
                    // no air control) and runs straight at the anchor, so where the pull will START must clear the stage
                    // corner (room x tan 60 >= depth) or it drags into the underside.
                    if (mv.Ext.Has(ChibiMoveExt.FTetherSelf) && self.Y > -3500)
                    {
                        int T = mv.Startup + 6;
                        int vy = self.Vy / ChibiSimConst.VelSub, vx = (int)((long)self.Vx * toCentre / ChibiSimConst.VelSub);
                        int py = self.Y + vy * T - ChibiSimConst.Gravity * T * T / (2 * ChibiSimConst.VelSub);
                        int room = outside - ChibiSimConst.HurtHalfW - vx * T;
                        if (py < 200 && (long)room * 1732 < (long)(-py + 400) * 1000)
                        {
                            _dx = -toCentre;                                   // make room first
                            timed = true;
                        }
                    }
                    if (!timed && CanPress(ChibiButtons.Special))
                    {
                        press = ChibiButtons.Special; _dy = 1;       // vertical wins: B+U (diagonal toward the stage)
                        return;
                    }
                    if (timed) return;
                }
                int btn = Profile[cls][PRecoverBtn];
                if (btn >= BtnS1)
                {
                    int sl = ChibiMovesetData.SlotFor(cls, btn, DU, true);
                    var b = ButtonBit(btn);
                    if (sl >= 0 && CanStartNew(self, sl) && CanPress(b)) { press = b; _dy = 1; return; }
                }
            }
            if (self.Vy < -30000 && self.Class == (int)FighterClass.Melee && self.Facing == toCentre && SkillReady(self, 0))
                press |= ChibiButtons.Skill1;
        }

        // ================================================================== defence (difficulty 2+)

        /// <summary>Notices a new opponent move (slot changed or its tick went back) so the counter roll happens once per move.</summary>
        void TrackOpponentMove(ChibiFighterSim opp)
        {
            if (opp.MoveSlot < 0) { _oppSlotSeen = -1; _counterRolled = false; return; }
            if (opp.MoveSlot != _oppSlotSeen || opp.MoveTick < _oppTickSeen) _counterRolled = false;
            _oppSlotSeen = opp.MoveSlot;
            _oppTickSeen = opp.MoveTick;
        }

        /// <summary>
        /// Counter / armour / invulnerable roll when the opponent's hit lands inside that move's window (one roll per
        /// opponent move), and reflect / projectile counters against an enemy shot arriving inside the window.
        /// </summary>
        bool TryDefend(ChibiFighterSim self, ChibiFighterSim opp, int dx, int dy, ref ChibiButtons press)
        {
            int cls = self.Class;
            int chance = Profile[cls][PCounter] * (_difficulty - 1) / 2;

            // Melee: opponent in startup, close.
            if (opp.MoveSlot >= 0 && !_counterRolled && Math.Abs(dx) < 3500 && Math.Abs(dy) < 2000 && !opp.Charging)
            {
                int remaining = opp.MoveStartup - opp.MoveTick;
                if (remaining > 0 && Pick(CtxCounter, self, opp, dx, dy, remaining, out int row))
                {
                    _counterRolled = true;
                    if (_rng.Roll(chance)) { Execute(row, self, opp, dx, ref press); return true; }
                }
            }

            // Projectiles: the first enemy shot coming at us.
            int arrive = IncomingShotTicks(self);
            if (arrive > 0 && Pick(CtxProjDef, self, opp, dx, dy, arrive, out int prow) && _rng.Roll(chance / 2))
            {
                Execute(prow, self, opp, dx, ref press);
                return true;
            }
            return false;
        }

        /// <summary>Ticks until the nearest live enemy projectile reaches this fighter's body (0 = none within 3500 milli).</summary>
        int IncomingShotTicks(ChibiFighterSim self)
        {
            int best = 0;
            var pr = _state.Proj;
            for (int k = 0; k < pr.Length; k++)
            {
                var p = pr[k];
                if (p == null || !p.Active || p.Owner == _player || p.Vx == 0) continue;
                int rx = self.X - p.X;
                if ((rx > 0) != (p.Vx > 0) || Math.Abs(rx) > 3500) continue;
                int cy = self.Y + ChibiSimConst.HurtH / 2;
                if (Math.Abs(p.Y - cy) > ChibiSimConst.HurtH / 2 + p.Radius + 200) continue;
                int t = (int)((long)(Math.Abs(rx) - ChibiSimConst.HurtHalfW) * ChibiSimConst.VelSub / Math.Abs(p.Vx));
                if (t < 1) t = 1;
                if (best == 0 || t < best) best = t;
            }
            return best;
        }

        // ================================================================== ground / air offence

        void GroundAttack(ChibiFighterSim self, ChibiFighterSim opp, ChibiStageDef st, int dx, int dy, bool ranged, int reach,
                          ref ChibiButtons press)
        {
            int adx = Math.Abs(dx);
            int skill = _skillPermille;
            var prof = Profile[self.Class];

            // Close quarters (FI:196-200 rate): tilts / jab, up moves when above, smashes when it punishes or kills.
            if (adx < reach + 1800 && dy > -1200 && dy < 3500)
            {
                if (_rng.Roll(60 + 120 * skill / 1000))
                {
                    int ctx = dy > 900 && adx < 1800 ? CtxAbove : CtxClose;
                    if ((Vulnerable(opp) || Killable(opp, st, dx)) && _rng.Roll(250 * _difficulty)) ctx = CtxPunish;
                    if (TryContext(ctx, self, opp, dx, dy, ref press)) return;
                    if (ctx != CtxClose && TryContext(CtxClose, self, opp, dx, dy, ref press)) return;
                }
                if (!ranged) return;
            }

            // Mid range: approach (lunges, dash attacks, short projectiles).
            if (adx < 6000 && Math.Abs(dy) < 1500 && _rng.Roll(prof[PApproach] * (500 + skill) / 1000))
                if (TryContext(CtxMid, self, opp, dx, dy, ref press)) return;

            // Traps between the fighters, one live zone per CPU.
            if (adx > 2000 && adx < 7000 && _rng.Roll(prof[PTrap]) && TryContext(CtxTrap, self, opp, dx, dy, ref press)) return;

            // Zoning at range, in line (the original's ranged attack, FI:196-200).
            if (Math.Abs(dy) < 1000 && _rng.Roll(prof[PZone] * (500 + skill) / 1000))
                TryContext(CtxFar, self, opp, dx, dy, ref press);
        }

        void AirAttack(ChibiFighterSim self, ChibiFighterSim opp, ChibiStageDef st, int dx, int dy, ref ChibiButtons press)
        {
            int cls = self.Class;
            int adx = Math.Abs(dx);
            var prof = Profile[cls];
            bool oppOff = Math.Abs(opp.X) > st.HalfWidth || opp.Y < -300;

            // Air down special (dives / falling pin) onto a grounded opponent below, only over the stage.
            if (prof[PDive] > 0 && opp.Grounded && dy < -1500 && adx < 3000 && Math.Abs(self.X) < st.HalfWidth - 1500
                && _rng.Roll(prof[PDive]))
            {
                int sl = ChibiMovesetData.SlotFor(cls, BtnB, DD, true);
                if (sl >= 0 && CanStartNew(self, sl) && CanPress(ChibiButtons.Special) && DiveLandsOnStage(self, st, ChibiMoveTable.Get(cls, sl)))
                {
                    press = ChibiButtons.Special; _dx = 0; _dy = -1;
                    return;
                }
            }

            if (adx > 3200 || Math.Abs(dy) > 3200) return;
            int rate = 80 + 150 * _skillPermille / 1000;
            if (oppOff) rate *= 2;                                     // edge-guard: swing harder (dair spikes)
            if (!_rng.Roll(rate)) return;

            if (_rng.Roll(250) && TryContext(CtxAir, self, opp, dx, dy, ref press)) return;

            // Positional aerial: uair above, dair below, nair on top, fair / bair by side (facing is locked in the air).
            int fx = dx * self.Facing;
            int dir = dy > 700 && adx < 1500 ? DU : dy < -700 && adx < 1500 ? DD : adx < 400 ? DN : fx > 0 ? DF : DBK;
            if (TryAerial(self, opp, dir, dx, dy, ref press)) return;
            if (dir != DN) TryAerial(self, opp, DN, dx, dy, ref press);
        }

        bool TryAerial(ChibiFighterSim self, ChibiFighterSim opp, int dir, int dx, int dy, ref ChibiButtons press)
        {
            if (self.SpearOut || !CanPress(ChibiButtons.Light)) return false;
            int sl = ChibiMovesetData.SlotFor(self.Class, BtnL, dir, true);
            if (sl < 0 || !CanStartNew(self, sl)) return false;
            if (!Reaches(ChibiMoveTable.Get(self.Class, sl), dx * self.Facing, dy, 600)) return false;
            int sdx = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            press = ChibiButtons.Light;
            _dx = dir == DN ? 0 : sdx;                                // F / BK: toward the opponent (sim resolves by Facing)
            _dy = dir == DU ? 1 : dir == DD ? -1 : 0;
            return true;
        }

        // ================================================================== option selection

        bool TryContext(int ctx, ChibiFighterSim self, ChibiFighterSim opp, int dx, int dy, ref ChibiButtons press)
        {
            if (!Pick(ctx, self, opp, dx, dy, 0, out int row)) return false;
            Execute(row, self, opp, dx, ref press);
            return true;
        }

        /// <summary>Weighted pick among the eligible rows of <paramref name="ctx"/>; one RNG draw when anything is eligible.</summary>
        bool Pick(int ctx, ChibiFighterSim self, ChibiFighterSim opp, int dx, int dy, int window, out int row)
        {
            row = -1;
            var opts = Options[self.Class];
            int n = 0, total = 0;
            for (int r = 0; r + OptW <= opts.Length && n < _pick.Length; r += OptW)
            {
                if (opts[r] != ctx || opts[r + 4] > _difficulty) continue;
                if (!Eligible(opts, r, self, opp, dx, dy, window)) continue;
                _pick[n++] = r;
                total += opts[r + 3];
            }
            if (n == 0 || total <= 0) return false;
            int roll = _rng.Range(total);
            for (int k = 0; k < n; k++)
            {
                roll -= opts[_pick[k] + 3];
                if (roll < 0) { row = _pick[k]; return true; }
            }
            row = _pick[n - 1];
            return true;
        }

        bool Eligible(int[] opts, int r, ChibiFighterSim self, ChibiFighterSim opp, int dx, int dy, int window)
        {
            int ctx = opts[r], btn = opts[r + 1], dir = opts[r + 2];
            int cls = self.Class;
            bool air = !self.Grounded;
            int sdx = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            if (!CanPress(btn == BtnDash ? ChibiButtons.Dash : ButtonBit(btn))) return false;
            if (self.SpearOut && (btn == BtnL || btn == BtnH || btn == BtnDash)) return false;

            int slot = SlotOf(cls, btn, dir, air);
            if (slot < 0) return false;
            var mv = ChibiMoveTable.Get(cls, slot);
            if (!mv.Exists) return false;

            if (slot < ChibiMovesetData.FirstNewSlot)
            {
                // Pre-v18 moves keep their gates and the original's range test (FI:196-200).
                bool ranged = cls == (int)FighterClass.Ranged || cls == (int)FighterClass.Magic;
                if (slot <= 1 && ranged && self.SetCd > 0) return false;
                if (slot >= 2 && slot <= 4 && self.Cd[slot - 2] > 0) return false;
                if (dir == DN && !(sdx == self.Facing || Math.Abs(dx) < 200)) return false;
                if (ranged) return Math.Abs(dy) < 1000;
                return Math.Abs(dx) < Profile[cls][PReach] + 400 && Math.Abs(dy) < 1200;
            }

            if (!CanStartNew(self, slot)) return false;
            var e = mv.Ext;
            if (e.Has(ChibiMoveExt.FZone) && !ZoneFree()) return false;
            if (mv.ProjSpeed > 0 && LiveShots() >= ProjSoftCap) return false;

            // Facing the move will have: ground horizontal / up / down input turns first; air keeps Facing except B+F.
            bool turns = !air ? dir != DN : btn == BtnB && dir == DF;
            if (!turns && dir == DN && !(sdx == self.Facing || Math.Abs(dx) < 200)) return false;
            int face = turns ? (sdx != 0 ? sdx : self.Facing) : self.Facing;
            int fx = dx * face;

            // Never lunge off the stage: a self-propelled row (or the dash + dash attack) must end over the stage.
            int half = ChibiStageTable.Get(_state.Stage).HalfWidth - 300;
            if (btn == BtnDash)
                return self.DashCd == 0 && Math.Abs(dy) < 600 && Math.Abs(dx) > Profile[cls][PReach] + 300 && Math.Abs(dx) < 5000
                       && Math.Abs(self.X + sdx * (Math.Abs(dx) + mv.SelfVx * (mv.Startup + mv.Active) / 2)) < half;
            if (mv.SelfVx > 0 && !e.Has(ChibiMoveExt.FRoll) && Math.Abs(self.X + face * mv.SelfVx * (mv.Startup + mv.Active)) >= half)
                return false;

            if (ctx == CtxCounter || ctx == CtxProjDef)
            {
                WindowOf(mv, out int w0, out int w1);
                if (window < w0 || window > w1) return false;
                if (ctx == CtxProjDef) return e.Has(ChibiMoveExt.FReflect) || e.Has(ChibiMoveExt.FCounterProj);
                return true;
            }
            if (ctx == CtxTrap) return true;
            if (ctx == CtxFar && mv.ProjSpeed == 0 && !e.Has(ChibiMoveExt.FTetherFoe)) return false;
            return Reaches(mv, fx, dy, ctx == CtxMid ? 500 : 250);
        }

        /// <summary>Emits the option's press tick (stick held) and queues its hold or dash-attack plan.</summary>
        void Execute(int row, ChibiFighterSim self, ChibiFighterSim opp, int dx, ref ChibiButtons press)
        {
            var opts = Options[self.Class];
            int btn = opts[row + 1], dir = opts[row + 2], hold = opts[row + 5];
            int sdx = dx > 0 ? 1 : (dx < 0 ? -1 : self.Facing);
            bool air = !self.Grounded;
            int px = dir == DN ? 0 : sdx;
            int py = dir == DU ? 1 : dir == DD ? -1 : 0;
            if (!air && dir == DD && btn != BtnDash) px = sdx;        // ground SE / SW: faces the opponent, no jump pressed
            _dx = px; _dy = py;
            _planLen = 0; _planPos = 0;

            if (btn == BtnDash)
            {
                // Dash toward, then Light (+down for Low Sweep) inside the dash window once close enough.
                press = ChibiButtons.Dash; _dx = sdx; _dy = 0;
                int travel = Math.Abs(dx) - Profile[self.Class][PReach];
                int lightTick = Math.Max(2, Math.Min(10, travel / 300));
                var walk = ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(sdx, 0), ChibiButtons.None).Raw;
                for (int k = 1; k < lightTick; k++) _plan[_planLen++] = walk;
                _plan[_planLen++] = ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(sdx, dir == DD ? -1 : 0), ChibiButtons.Light).Raw;
                return;
            }

            var bit = ButtonBit(btn);
            press = bit;
            int slot = SlotOf(self.Class, btn, dir, air);
            var mv = ChibiMoveTable.Get(self.Class, slot);
            if (hold == 0 && slot >= ChibiMovesetData.FirstNewSlot && mv.Ext.Has(ChibiMoveExt.FCharge) && _difficulty >= 2)
                hold = Math.Min(mv.Ext.ChargeMax, opp.Stun - mv.Startup - 4);   // charge only what the hitstun allows
            if (hold > 0 && (btn == BtnH || btn == BtnB || btn == BtnS3))
            {
                hold = Math.Min(MaxHoldTicks, hold - (opts[row + 5] > 0 ? _rng.Range(hold / 2 + 1) : 0));
                var held = ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(px, py), bit).Raw;
                for (int k = 0; k < hold; k++) _plan[_planLen++] = held;
            }
        }

        // ================================================================== move data helpers

        /// <summary>Slot an option resolves to (README 1.2), or -1.</summary>
        static int SlotOf(int cls, int btn, int dir, bool air)
        {
            if (btn == BtnDash)
            {
                if (air) return -1;
                int d = dir == DD ? ChibiMovesetData.DashDownSlot[cls] : -1;
                return d >= 0 ? d : ChibiMovesetData.DashSlot[cls];
            }
            if (btn == BtnL && dir == DN && !air) return 0;
            if (btn == BtnH && (air || dir == DN)) return 1;
            if (btn >= BtnS1 && dir != DU && dir != DD) return 2 + (btn - BtnS1);
            int sl = ChibiMovesetData.SlotFor(cls, btn, btn == BtnB && dir == DBK ? DF : dir, air);
            return sl;
        }

        /// <summary>CanStartRow (sim) mirror plus the ground / air mask: technique cooldown and aironce.</summary>
        static bool CanStartNew(ChibiFighterSim f, int slot)
        {
            var mv = ChibiMoveTable.Get(f.Class, slot);
            var e = mv.Ext;
            if (e == null) return false;
            if ((e.AirMask & (f.Grounded ? 1 : 2)) == 0) return false;
            if (e.TechIdx >= 0 && f.TechCd[e.TechIdx] > 0) return false;
            if (e.Has(ChibiMoveExt.FAirOnce) && !f.Grounded && (f.AirUsedMask & (1 << (slot - ChibiMovesetData.FirstNewSlot))) != 0) return false;
            return true;
        }

        /// <summary>
        /// Whether the row's hit can touch the opponent's hurtbox: <paramref name="fx"/> = opponent X ahead of the move's
        /// facing, <paramref name="dy"/> = opponent feet above ours (milli). Lunges (SelfVx) stretch the box forward;
        /// horizontal shots reach speed x life. Zones, counters, teleports, tethers, dives and angled shots are decided
        /// by their context, not here.
        /// </summary>
        static bool Reaches(in ChibiMove mv, int fx, int dy, int slack)
        {
            var e = mv.Ext;
            if (e != null && (e.Has(ChibiMoveExt.FZone) || e.Has(ChibiMoveExt.FCounter) || e.Has(ChibiMoveExt.FTeleport)
                              || e.Has(ChibiMoveExt.FTetherSelf) || e.Has(ChibiMoveExt.FTetherFoe) || e.Has(ChibiMoveExt.FDive)))
                return true;
            int hx0 = fx - ChibiSimConst.HurtHalfW - slack, hx1 = fx + ChibiSimConst.HurtHalfW + slack;
            int hy0 = dy - slack, hy1 = dy + ChibiSimConst.HurtH + slack;
            if (mv.ProjSpeed > 0)
            {
                if (e != null && (e.FanVx != null || e.Has(ChibiMoveExt.FExplicitPv))) return true;
                long range = (long)mv.ProjSpeed * Math.Max(1, mv.ProjLife) / ChibiSimConst.VelSub + mv.ProjOffX;
                int py = mv.ProjOffY != 0 ? mv.ProjOffY : ChibiSimConst.ProjectileY;
                return hx1 > 0 && hx0 < range && py + mv.ProjR > hy0 && py - mv.ProjR < hy1;
            }
            int radius = e != null ? e.Radius : 0;
            if (mv.BoxW <= 0 && radius <= 0) return true;              // no hitbox (stance): context decides
            int lunge = mv.SelfVx > 0 && (e == null || !e.Has(ChibiMoveExt.FRoll)) ? mv.SelfVx * (mv.Startup + mv.Active) : 0;
            if (radius > 0)
            {
                int cx0 = mv.BoxX, cx1 = mv.BoxX + lunge, cy = mv.BoxY;
                long gx = Math.Max(0, Math.Max(hx0 - cx1, cx0 - hx1));
                long gy = Math.Max(0, Math.Max(hy0 - cy, cy - hy1));
                return gx * gx + gy * gy < (long)radius * radius;
            }
            int x0, x1;
            if ((mv.Flags & ChibiMove.FCentered) != 0) { x0 = -mv.BoxW / 2; x1 = x0 + mv.BoxW; }
            else if (e != null && e.Has(ChibiMoveExt.FBack)) { x0 = -(mv.BoxX + mv.BoxW); x1 = -mv.BoxX; }
            else { x0 = mv.BoxX; x1 = mv.BoxX + mv.BoxW + lunge; }
            int y0 = mv.BoxY, y1 = mv.BoxY + mv.BoxH + (mv.SelfVy > 0 ? mv.SelfVy * mv.Active : 0);
            return x0 < hx1 && x1 > hx0 && y0 < hy1 && y1 > hy0;
        }

        /// <summary>Move-tick window in which the row protects: invulnerable, armoured, else its active ticks (counters).</summary>
        static void WindowOf(in ChibiMove mv, out int w0, out int w1)
        {
            var e = mv.Ext;
            if (e.Has(ChibiMoveExt.FInvulnWindow)) { w0 = e.InvulnFrom; w1 = e.InvulnTo; return; }
            w0 = mv.Startup;
            w1 = mv.Startup + mv.Active - 1;
            if (e.Has(ChibiMoveExt.FArmor))
            {
                w0 = e.ArmorFrom;
                w1 = e.ArmorTo >= 0 ? e.ArmorTo : w1 + e.HoldMax;
            }
        }

        /// <summary>A dive keeps going until it lands: only start one whose path stays over the stage.</summary>
        static bool DiveLandsOnStage(ChibiFighterSim self, ChibiStageDef st, in ChibiMove mv)
        {
            if (!mv.Ext.Has(ChibiMoveExt.FDive) || mv.SelfVy >= 0) return true;
            int ticks = Math.Min(mv.Ext.DiveMax > 0 ? mv.Ext.DiveMax : mv.Active, self.Y / Math.Max(1, -mv.SelfVy) + 1);
            int landX = self.X + self.Facing * mv.SelfVx * ticks;
            return Math.Abs(landX) < st.HalfWidth - 800;
        }

        /// <summary>The opponent can be punished: in hitstun, guard-broken, or in the recovery of a long move.</summary>
        static bool Vulnerable(ChibiFighterSim opp)
        {
            if (opp.Stun > 0 || opp.GuardBrokenTicks > 0) return true;
            return opp.MoveSlot >= 0 && opp.MoveTick >= opp.MoveStartup + opp.MoveActive && opp.MoveLockEnd - opp.MoveTick > 12;
        }

        /// <summary>Kill pressure: low HP, or near the edge with us between them and the centre.</summary>
        static bool Killable(ChibiFighterSim opp, ChibiStageDef st, int dx)
        {
            if ((long)opp.HpU * 1000 <= (long)opp.Stats.MaxHp * ChibiCombatStats.HpScale * KillHpPermille) return true;
            int side = opp.X > 0 ? 1 : -1;
            return Math.Abs(opp.X) > st.HalfWidth - 2500 && (dx > 0 ? 1 : -1) == side;
        }

        bool ZoneFree()
        {
            int live = 0;
            var z = _state.Zones;
            for (int k = 0; k < z.Length; k++)
            {
                if (!z[k].Active) continue;
                if (z[k].Owner == _player) return false;
                live++;
            }
            return live < z.Length - 1;
        }

        int LiveShots()
        {
            int n = 0;
            var pr = _state.Proj;
            for (int k = 0; k < pr.Length; k++) if (pr[k] != null && pr[k].Active) n++;
            return n;
        }

        bool CanPress(ChibiButtons b) => ((ushort)b & _prevEmitted) == 0;

        static ChibiButtons ButtonBit(int btn)
        {
            switch (btn)
            {
                case BtnL: return ChibiButtons.Light;
                case BtnH: return ChibiButtons.Heavy;
                case BtnB: return ChibiButtons.Special;
                case BtnS1: return ChibiButtons.Skill1;
                case BtnS2: return ChibiButtons.Skill2;
                case BtnS3: return ChibiButtons.Skill3;
                default: return ChibiButtons.Dash;
            }
        }

        static int SkillCount(int cls)
        {
            int n = 0;
            for (int k = 0; k < 3; k++) if (ChibiMoveTable.Get(cls, 2 + k).Exists) n = k + 1;
            return n;
        }

        // ================================================================== ult gesture (unchanged)

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
