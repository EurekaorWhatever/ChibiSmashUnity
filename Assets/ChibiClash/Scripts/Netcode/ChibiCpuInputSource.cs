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
    /// Rules v20/v21 loop (design/gdd/percent-knockback.md, design/gdd/stages/): kill reads by percent (each reachable
    /// option's knockback from <see cref="ChibiSimulation.KnockbackM"/> and a rough launch distance against the stage's
    /// ACTIVE KO edges, so smashes are saved for kill percent and Mountain finishes upward), tilts first at low percent,
    /// a survival mode at its own kill percent (more shield, wider ledge margins, no off-stage chasing), ledge discipline
    /// on every stage (the end of the walkable ground is the ledge), side / lower platforms (moving ones predicted) as
    /// landing targets, and the stage events when hazards are on: Volcano fire lanes, Bird Lair gust and body band, Ocean
    /// whale (down to the tiers until it has risen). No input while KO'd or hovering, except one tick that leaves the hover.
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

        /// <summary>Total live projectiles above which no new projectile move is started (pool is 64).</summary>
        const int ProjSoftCap = 40;

        // Rules v20 (percent, stocks, stages, hazards). Distances milli, chances permille, times ticks.
        /// <summary>Launch travel along the launch ~ KB² x 0.44 milli (percent-knockback.md 4.8, KB in units).</summary>
        const int TravelPerKb2P = 440;
        /// <summary>Chance per close-range decision to take a kill move when one is estimated to KO: base + per difficulty.</summary>
        const int KillTakeBase = 300, KillTakePerDiff = 200;
        /// <summary>Weight multiplier of ground smashes among the kill moves (smashes are the finishers).</summary>
        const int KillSmashBoost = 2;
        /// <summary>Ground smash appetite outside kill range: permille at 0 %, rising linearly to 1000 at 100 % (tilts first at low percent).</summary>
        const int SmashScaleLowP = 250, SmashScaleFullPctM = 100_000;
        /// <summary>Distance kept from a ledge / from an active side KO line while walking, plus the extra when in danger.</summary>
        const int LedgeMargin = 300, WalkOffMargin = 2500, DangerLedgeExtra = 600, DangerWalkOffExtra = 2000;
        /// <summary>Extra shield chance (permille) when the CPU's own percent is in KO range.</summary>
        const int DangerBlockBonus = 200;
        /// <summary>Fight-for-centre stages: past this permille of the main half-width with the opponent between us and the centre = cornered.</summary>
        const int CorneredP = 450, CorneredJumpP = 350;
        /// <summary>Volcano: margin around a fire lane's danger band (lane half-width + hurtbox).</summary>
        const int FireMargin = 300;
        /// <summary>Ocean: feet stay at or below MainBottom - this on a tier during the whale; the whale is clear once its bottom is above this.</summary>
        const int TierBelowMain = 300, WhaleClearY = 5500;
        /// <summary>Bird Lair: during the telegraph / gust, stand at least this far upwind of the centre.</summary>
        const int BirdUpwindX = 2000;
        /// <summary>Respawn hover: minimum wait before leaving (ticks, + Range), and how near below an opponent must be to keep waiting.</summary>
        const int HoverWaitBase = 10, HoverWaitSpan = 30, HoverCampX = 2500, HoverCampMinLeft = 30;

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

        // Rules v20 situation of this decision.
        int _hoverWait = -1;                                    // ticks still to wait on the respawn point (-1 = not drawn)
        bool _danger;                                           // own percent: the opponent's smash from here would KO us
        bool _noJump;                                           // whale: holding a tier under MAIN, never jump up
        bool _allowOff;                                         // this decision may walk off the footing (edge-guard hop)
        int _smashScaleP = 1000;                                // ground smash weight outside kill range (percent appetite)

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

            // KO'd (off-screen) or hovering on the respawn point: nothing at all (any direction, button or gesture bit ends
            // the hover in the sim), except one stick tick that leaves the hover once it is safe (percent-knockback.md 3.4).
            var me = _state.F[_player];
            if (me.OutTicks > 0 || me.HoverTicks > 0)
            {
                _planLen = 0; _prevEmitted = 0; _pendingStep = -1;
                if (me.OutTicks > 0 || _difficulty <= 0 || !_state.Running || !HoverLeaveSafe(me)) return default;
                _hoverWait = -1;
                var opp0 = _state.F[1 - _player];
                int away = opp0.X > me.X ? -1 : (opp0.X < me.X ? 1 : (me.X > 0 ? -1 : 1));
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(away, 0), ChibiButtons.None);
            }
            _hoverWait = -1;

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

            var st = ChibiStageTable.Get(_state.Stage);
            _noJump = false; _allowOff = false;
            _danger = InDanger(self, opp, st);
            _smashScaleP = SmashScale(opp);

            // Ocean whale (hazards on): get under MAIN onto a tier before it lands and stay there until it has risen.
            if (WhaleThreat(st))
            {
                if (!(self.Grounded && self.Y <= st.MainBottom - TierBelowMain)) { WhaleDescend(self, opp, st, ref press); return false; }
                _noJump = true;                                       // on a tier: fight here, never jump up into it
            }

            // Recover to the stage when knocked off (FI:163-171). Pivot = feet + 0.725, so pivot.y < groundY - 0.5
            // is feet Y < -1225; |pivot.x| > halfWidth - 0.3 is the same on the feet. Grounded: only a surface below the
            // main top or a platform beside the main stage counts (rules v20 tiers, cloud-sea floor, Mountain A / B).
            if (!_noJump && OffStage(self, st))
            {
                Recover(self, st, ref press);
                AvoidFire(self, st, ref press);
                return false;
            }

            // Bird Lair telegraph / gust: grounded, upwind, out of the body band.
            if (BirdAlert(st))
            {
                BirdBrace(self, opp, st, ref block, ref press);
                return false;
            }

            // Opponent KO'd or hovering on the respawn point: take the centre, never chase off-screen.
            if (!opp.InPlay)
            {
                int toward = st.RespawnX - self.X;
                _dx = Math.Abs(toward) > 3000 ? Math.Sign(toward) : 0;
                FinishTick(self, st, ref press);
                return false;
            }

            int dx = opp.X - self.X, dy = opp.Y - self.Y;
            int sdx = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int adx = Math.Abs(dx);
            int cls = self.Class;
            var prof = Profile[cls];
            bool ranged = cls == (int)FighterClass.Ranged || cls == (int)FighterClass.Magic;
            int reach = prof[PReach];

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
                _wantBlock = oppAttacking && adx < 2500 &&
                             _rng.Roll(150 + 350 * _skillPermille / 1000 + (_danger ? DangerBlockBonus : 0));
                if (dy > 1500 && _rng.Roll(600)) press |= ChibiButtons.Jump;
                // Approach jump into an aerial (moveset layer): from just outside reach, at the opponent's height.
                else if (self.Grounded && adx > reach && adx < reach + 2600 && Math.Abs(dy) < 1500 &&
                         _rng.Roll(_danger ? prof[PJump] / 2 : prof[PJump]))
                {
                    press |= ChibiButtons.Jump;
                    _wantMove = sdx;
                }
                // Fight-for-centre stages (Plain, Bird Lair): cornered (nearer the ledge, the opponent between us and the
                // centre): jump over toward the centre instead of fighting with the back to the ledge.
                else if (adx < 3500 && Cornered(self, opp, st) && _rng.Roll(CorneredJumpP))
                {
                    press |= ChibiButtons.Jump;
                    _wantMove = self.X > 0 ? -1 : 1;
                }
            }

            _dx = _wantMove;
            block = _wantBlock;
            bool free = self.MoveSlot < 0 && self.Stun == 0 && self.DashTicks == 0 && !self.Held;

            // Counters / reflectors (difficulty 2+) replace the shield when the timing fits.
            if (free && _difficulty >= 2 && TryDefend(self, opp, dx, dy, ref press))
            {
                block = false;
                FinishTick(self, st, ref press);
                return false;
            }
            if (_wantBlock) return false;

            bool facingOpp = sdx == self.Facing || adx < 200;
            // The original creeps at MoveX 0.2 to turn; the sim's stick is digital, so one tick of full input turns it.
            if (_wantMove == 0 && !facingOpp && self.Grounded) _dx = sdx;

            // Edge-guard hop (difficulty 2+, not in danger): the opponent is off the stage close by, not far below.
            _allowOff = !OnStageArea(opp, st) && !_danger && _difficulty >= 2 && st.KoBottom && self.AirJumps > 0 && !_noJump &&
                        adx < 3500 && dy > -1500;

            if (free && press == ChibiButtons.None)
            {
                if (self.Grounded) GroundAttack(self, opp, st, dx, dy, ranged, reach, ref press);
                else AirAttack(self, opp, st, dx, dy, ref press);
            }

            // Random existing skill (FI:201-205), neutral stick so the sim resolves the old skill. Never toward a ledge:
            // the old skills lunge along Facing.
            if (press == ChibiButtons.None && _rng.Roll(12 * _skillPermille / 1000))
            {
                int count = SkillCount(cls);
                int k = _rng.Range(Math.Max(1, count));
                bool inMelee = adx < reach + 400 && Math.Abs(dy) < 1200;
                if (k < count && SkillReady(self, k) && (inMelee || ranged || adx < 5000) &&
                    (self.Grounded ? SafeGround(st, self.X + self.Facing * 2500, self.Y)
                                   : Math.Abs(self.X + self.Facing * 2500) < st.HalfWidth - LedgeMargin))
                {
                    var b = (ChibiButtons)((int)ChibiButtons.Skill1 << k);
                    if (CanPress(b)) { press |= b; _dy = 0; }
                }
            }

            // Ult: more eager when the opponent sits at kill percent (spec 3.5 CPU rule; ult hits get the base bonus).
            if (self.Ult >= ChibiSimConst.UltMax && self.UltWindowTicks == 0 && _rng.Roll(HighPercent(opp) ? 60 : 20))
                press |= ChibiButtons.Ultimate;
            FinishTick(self, st, ref press);
            return false;
        }

        /// <summary>Last filters of a normal decision: no jumping up under the whale, keep the footing, stay out of fire lanes.</summary>
        void FinishTick(ChibiFighterSim self, ChibiStageDef st, ref ChibiButtons press)
        {
            if (_noJump)
            {
                press &= ~ChibiButtons.Jump;
                if (_dy > 0)
                {
                    _dy = 0;
                    press &= ~(ChibiButtons.Light | ChibiButtons.Heavy | ChibiButtons.Special |
                               ChibiButtons.Skill1 | ChibiButtons.Skill2 | ChibiButtons.Skill3);
                }
            }
            // A move press keeps its stick (it selects the move; lunges were checked against the footing in Eligible).
            const ChibiButtons moves = ChibiButtons.Light | ChibiButtons.Heavy | ChibiButtons.Special | ChibiButtons.Skill1 |
                                       ChibiButtons.Skill2 | ChibiButtons.Skill3 | ChibiButtons.Dash;
            if (!_allowOff && (press & moves) == 0) KeepFooting(self, st);
            AvoidFire(self, st, ref press);
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
            if (self.Grounded) { RecoverFromGround(self, st, toCentre, ref press); return; }

            // A platform beside the main stage (moving ones at their predicted landing position) that we can drift onto
            // without a jump is a landing target too (Mountain A / B, the Ocean tiers, Volcano L1's overhang).
            if (PlatformLanding(self, st, out int landX))
            {
                int d = landX - self.X;
                _dx = Math.Abs(d) > 250 ? Math.Sign(d) : 0;
                return;
            }

            bool under = self.Y < -300 && Math.Abs(self.X) < st.HalfWidth - 200;   // truly beneath, not at the side wall
            _dx = under ? (toCentre != 0 ? -toCentre : 1) : toCentre;
            int outside = Math.Abs(self.X) - st.HalfWidth;             // > 0: beyond the edge
            bool canAct = self.MoveSlot < 0 && self.Stun == 0 && !self.Held && self.DashTicks == 0 && !self.Grounded;

            if (self.Vy < 0 && self.Y < 500 && (self.AirJumps > 0 || !canAct)) press |= ChibiButtons.Jump;
            // Air dash for distance only over a void (a solid floor below makes it a waste of the dash).
            else if (canAct && !under && !OverFloor(st, self.X) && outside > 1200 && self.DashCd == 0 && CanPress(ChibiButtons.Dash))
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

            // Close quarters (FI:196-200 rate): tilts / jab, up moves when above. Rules v20: a move estimated to KO from
            // the opponent's percent, weight and position against the ACTIVE edges is taken first (smashes saved for that);
            // below kill range punishes lean on tilts, more so the lower the opponent's percent.
            if (adx < reach + 1800 && dy > -1200 && dy < 3500)
            {
                if (_rng.Roll(60 + 120 * skill / 1000))
                {
                    if (_rng.Roll(KillTakeBase + KillTakePerDiff * _difficulty) && FindKillRow(self, opp, st, dx, dy, out int krow))
                    {
                        Execute(krow, self, opp, dx, ref press);
                        return;
                    }
                    int ctx = dy > 900 && adx < 1800 ? CtxAbove : CtxClose;
                    if (Vulnerable(opp) && _rng.Roll(250 * _difficulty * _smashScaleP / 1000)) ctx = CtxPunish;
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
            if (oppOff && !_danger) rate *= 2;                         // edge-guard: swing harder (dair spikes), not at our own kill percent
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
                total += RowWeight(opts, r, self);
            }
            if (n == 0 || total <= 0) return false;
            int roll = _rng.Range(total);
            for (int k = 0; k < n; k++)
            {
                roll -= RowWeight(opts, _pick[k], self);
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
            var stg = ChibiStageTable.Get(_state.Stage);
            int half = Math.Min(stg.HalfWidth - 300, stg.KoSides ? stg.BlastX - WalkOffMargin : int.MaxValue);
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

        // ================================================================== rules v20: percent kill reads and survival

        /// <summary>
        /// Option weight in a pick: ground smashes (Heavy + direction on the ground) are scaled by the percent appetite
        /// (<see cref="SmashScale"/>) so tilts and jabs dominate at low percent; kill moves are chosen by
        /// <see cref="FindKillRow"/> before any pick.
        /// </summary>
        int RowWeight(int[] opts, int r, ChibiFighterSim self)
        {
            int w = opts[r + 3];
            if (opts[r + 1] == BtnH && opts[r + 2] != DN && self.Grounded) w = Math.Max(1, w * _smashScaleP / 1000);
            return w;
        }

        /// <summary>Kill-move weight: the row's weight, doubled for ground smashes (the moves the CPU saves for the kill).</summary>
        static int KillWeight(int[] opts, int r, ChibiFighterSim self) =>
            opts[r + 3] * (opts[r + 1] == BtnH && opts[r + 2] != DN && self.Grounded ? KillSmashBoost : 1);

        /// <summary>Smash appetite (permille) outside kill range: 250 at 0 % rising to 1000 at 100 %.</summary>
        static int SmashScale(ChibiFighterSim opp)
        {
            int p = Math.Min(opp.PctM, SmashScaleFullPctM);
            return SmashScaleLowP + (1000 - SmashScaleLowP) * p / SmashScaleFullPctM;
        }

        /// <summary>The spec's CPU kill-percent rule (percent-knockback.md 3.5): percent &gt;= weight + CpuKillPctOffset.</summary>
        static bool HighPercent(ChibiFighterSim f) =>
            f.PctM >= (f.Weight + ChibiPercentConst.CpuKillPctOffset) * 1000;

        /// <summary>
        /// A close-range option (punish, close or above rows the CPU may use) that can start, reaches, and is estimated to
        /// KO the opponent from here (<see cref="WouldKo"/>). Weighted pick among them, one RNG draw when any exists.
        /// </summary>
        bool FindKillRow(ChibiFighterSim self, ChibiFighterSim opp, ChibiStageDef st, int dx, int dy, out int row)
        {
            row = -1;
            if (!opp.InPlay || opp.Invuln > 0) return false;
            var opts = Options[self.Class];
            int n = 0, total = 0;
            int dir = dx > 0 ? 1 : (dx < 0 ? -1 : self.Facing);
            for (int r = 0; r + OptW <= opts.Length && n < _pick.Length; r += OptW)
            {
                int ctx = opts[r];
                if (ctx != CtxPunish && ctx != CtxClose && ctx != CtxAbove) continue;
                if (opts[r + 4] > _difficulty || opts[r + 1] == BtnDash) continue;
                if (!Eligible(opts, r, self, opp, dx, dy, 0)) continue;
                int slot = SlotOf(self.Class, opts[r + 1], opts[r + 2], !self.Grounded);
                if (!WouldKo(self.Class, self.Stats, opp, ChibiMoveTable.Get(self.Class, slot), dir, st)) continue;
                _pick[n++] = r;
                total += KillWeight(opts, r, self);
            }
            if (n == 0 || total <= 0) return false;
            int roll = _rng.Range(total);
            for (int k = 0; k < n; k++)
            {
                roll -= KillWeight(opts, _pick[k], self);
                if (roll < 0) { row = _pick[k]; return true; }
            }
            row = _pick[n - 1];
            return true;
        }

        /// <summary>
        /// Rough KO estimate of one uncharged, non-crit hit of <paramref name="mv"/> (attacker class / stats) on
        /// <paramref name="victim"/> launched toward <paramref name="dir"/>: knockback from the sim's own formula
        /// (<see cref="ChibiSimulation.KnockbackM"/> with the victim's percent after the hit, the hit's percent, weight, the
        /// row's KbX/KbY magnitude and the class factor), travel ~ 0.44 KB² milli along the launch (spec 4.8; gravity trims
        /// the rise below KB 120), compared with the stage's ACTIVE KO edges only (walls, the sky cap and solid floors
        /// never KO). Set-knockback hits never KO.
        /// </summary>
        static bool WouldKo(int atkCls, in ChibiCombatStats atk, ChibiFighterSim victim, in ChibiMove mv, int dir, ChibiStageDef st)
        {
            long kx = mv.KbX, ky = mv.KbY;
            if (mv.Ext != null && mv.Ext.Has(ChibiMoveExt.FBack)) dir = -dir;
            int mag = (int)ChibiFixed.ISqrt(kx * kx + ky * ky);
            if (mag < ChibiPercentConst.SetKbMag || mv.Mult <= 0) return false;
            if (ky < 0 && victim.Grounded) ky = -ky / 2;               // a spike on the ground pops up (Movesets 420)
            int hit = EstimateHitPct(atk, victim, mv.Mult);
            int after = (int)Math.Min(ChibiPercentConst.PctCapM, (long)victim.PctM + hit);
            int kb = ChibiSimulation.KnockbackM(after, hit, victim.Weight, mag, false, ChibiSimulation.ClassKbP(atkCls));
            long kb2 = (long)kb * kb / 1_000_000;                      // KB² in units
            long along = kb2 * TravelPerKb2P / 1000;                   // milli along the launch
            long tx = along * Math.Abs(kx) / mag, ty = along * ky / mag;
            if (ty > 0) ty = ty * (1000 - ChibiSimulation.LaunchGravityP(kb) / 2) / 1000;
            long nx = victim.X + dir * tx, ny = victim.Y + ty;
            if (st.KoSides && (nx > st.BlastX || nx < -st.BlastX)) return true;
            if (st.KoTop && ny > st.CeilingY) return true;
            // Spike on an opponent already off the stage: it will not come back from below the stage.
            if (st.KoBottom && ty < 0 && Math.Abs(victim.X) > st.HalfWidth && ny < st.KillY / 2) return true;
            return false;
        }

        /// <summary>The hit's percent (milli) without crit: the sim's damage pipeline (Attack x Mult, damage bonus, defense) x 0.2.</summary>
        static int EstimateHitPct(in ChibiCombatStats atk, ChibiFighterSim victim, int mult)
        {
            long n = (long)atk.Attack * ChibiCombatStats.NanoPerHp * mult / 1000;
            n = n * (1000 + atk.DamageBonusPermille) / 1000;
            int dmgU = ChibiCombatStats.FinalDamageMicro(n, ChibiCombatStats.DefenseH(victim.Stats, victim.ArmorBreakTicks > 0), false);
            return ChibiSimulation.Pct(dmgU);
        }

        /// <summary>
        /// Survival read: the opponent's forward or up smash would KO us from here (same estimate), or our percent passed the
        /// spec's kill-percent rule. In danger the CPU shields more, approach-jumps less, keeps further from ledges and KO
        /// lines and does not chase off the stage.
        /// </summary>
        static bool InDanger(ChibiFighterSim self, ChibiFighterSim opp, ChibiStageDef st)
        {
            if (!self.InPlay) return false;
            if (HighPercent(self)) return true;
            int dir = self.X >= opp.X ? 1 : -1;
            int fs = ChibiMovesetData.SlotFor(opp.Class, BtnH, DF, false), us = ChibiMovesetData.SlotFor(opp.Class, BtnH, DU, false);
            return (fs >= 0 && WouldKo(opp.Class, opp.Stats, self, ChibiMoveTable.Get(opp.Class, fs), dir, st)) ||
                   (us >= 0 && WouldKo(opp.Class, opp.Stats, self, ChibiMoveTable.Get(opp.Class, us), dir, st));
        }

        // ================================================================== rules v20: stage awareness

        /// <summary>
        /// Needs to recover: airborne past the ledge zone or below the stage (the original's rule), or standing on a surface
        /// that is not the main stage top: under it (Ocean tiers, Bird Lair's cloud sea) or beside it (Mountain A / B).
        /// </summary>
        static bool OffStage(ChibiFighterSim self, ChibiStageDef st)
        {
            if (!self.Grounded) return Math.Abs(self.X) > st.HalfWidth - 300 || self.Y < -1225;
            return self.Y < -300 || Math.Abs(self.X) > st.HalfWidth + 200;
        }

        /// <summary>On the main stage or a platform above its level, over the stage's width (not off the side, not below).</summary>
        static bool OnStageArea(ChibiFighterSim f, ChibiStageDef st) => Math.Abs(f.X) <= st.HalfWidth && f.Y >= -300;

        /// <summary>
        /// Recovery while standing on a lower or side surface: beside the main stage (or on a platform above its level) jump
        /// toward the centre; under the main stage walk out from under it first, jumping off the end of the surface.
        /// </summary>
        void RecoverFromGround(ChibiFighterSim self, ChibiStageDef st, int toCentre, ref ChibiButtons press)
        {
            int ax = Math.Abs(self.X);
            bool under = self.Y < -300 && ax < st.HalfWidth + ChibiSimConst.HurtHalfW + 100;
            if (!under)
            {
                _dx = toCentre;
                if (CanPress(ChibiButtons.Jump)) press |= ChibiButtons.Jump;
                return;
            }
            _dx = toCentre != 0 ? -toCentre : 1;
            if (!SafeGround(st, self.X + _dx * 500, self.Y) && CanPress(ChibiButtons.Jump)) press |= ChibiButtons.Jump;
        }

        /// <summary>
        /// Landing target beside the main stage: a platform that extends past the ledge, below us, that we can reach by
        /// drifting while we fall to its top (moving platforms at their predicted position at that time, F1 via
        /// <see cref="ChibiPlatform.CxAt"/> / <see cref="ChibiPlatform.TopAt"/>, starting from
        /// <see cref="ChibiSimulation.PlatformCx"/> / <see cref="ChibiSimulation.PlatformTop"/>). The main stage wins when it
        /// can be drifted onto the same way.
        /// </summary>
        bool PlatformLanding(ChibiFighterSim self, ChibiStageDef st, out int landX)
        {
            landX = 0;
            var plats = st.Platforms;
            if (plats.Length == 0 || self.Vy > 0) return false;
            int speed = Math.Max(1, ChibiCombatStats.EffectiveMoveSpeed(self.Stats, self.SlowTicks > 0, self.TailwindTicks > 0) / ChibiSimConst.VelSub);
            int g = Math.Max(1, ChibiSimConst.Gravity / ChibiSimConst.VelSub);
            if (self.Y > 200)
            {
                int tMain = (int)ChibiFixed.ISqrt(2L * self.Y / g);
                if (Math.Abs(self.X) - st.HalfWidth + 300 <= tMain * speed * 7 / 10) return false;
            }
            int clock = ChibiSimulation.PlatformClock(_state);
            int best = int.MaxValue;
            for (int p = 0; p < plats.Length; p++)
            {
                var q = plats[p];
                int cx = ChibiSimulation.PlatformCx(_state, p), top = ChibiSimulation.PlatformTop(_state, p);
                if (Math.Abs(cx) + q.HalfW <= st.HalfWidth + 400) continue;    // over the main stage: not a side target
                if (self.Y < top + 150) continue;
                int t = (int)ChibiFixed.ISqrt(2L * (self.Y - top) / g) + 1;
                if (q.Moves) { cx = q.CxAt(clock + t); top = q.TopAt(clock + t); if (self.Y < top + 150) continue; }
                int edge = Math.Max(0, Math.Abs(self.X - cx) - (q.HalfW - 400));
                if (edge > t * speed * 7 / 10) continue;
                if (edge < best)
                {
                    best = edge;
                    int lo = cx - q.HalfW + 500, hi = cx + q.HalfW - 500;
                    landX = Math.Max(lo, Math.Min(hi, self.X));
                }
            }
            return best != int.MaxValue;
        }

        /// <summary>
        /// Footing check for walking: feet x at height y still stand on something wanted, never near an active side KO line
        /// (margin), on the main stage top short of its ledge (the end of the walkable ground on every stage, rules v21),
        /// on the solid floor short of its end, on the same platform, or with the
        /// main stage / a platform close below to drop onto. Margins grow in danger.
        /// </summary>
        bool SafeGround(ChibiStageDef st, int x, int y)
        {
            int ax = Math.Abs(x);
            int koMargin = WalkOffMargin + (_danger ? DangerWalkOffExtra : 0);
            int ledge = LedgeMargin + (_danger ? DangerLedgeExtra : 0);
            if (st.KoSides && ax > st.BlastX - koMargin) return false;
            if (!st.KoSides && ax > st.WallClampX) return false;
            if (y > -200 && y < 200 && ax <= st.HalfWidth - ledge) return true;
            if (st.HasFloor && y <= st.FloorY + 200 && y >= st.FloorY - 200 && ax <= st.FloorHalfW - ledge) return true;
            if (!st.KoBottom && y <= st.KillY + 200) return true;
            var plats = st.Platforms;
            for (int p = 0; p < plats.Length; p++)
            {
                int cx = ChibiSimulation.PlatformCx(_state, p), top = ChibiSimulation.PlatformTop(_state, p);
                if (Math.Abs(x - cx) <= plats[p].HalfW - 150 && top <= y + 200 && top >= y - 2500) return true;
            }
            return y > 200 && ax <= st.HalfWidth - ledge;              // dropping off a platform onto the main stage
        }

        /// <summary>Grounded walking never steps where <see cref="SafeGround"/> fails (stopping distance included); a turn tick passes.</summary>
        void KeepFooting(ChibiFighterSim self, ChibiStageDef st)
        {
            if (!self.Grounded)
            {
                // Also never drift (momentum included) past the ledge over a KO bottom (past the floor's end where there is
                // one) or toward an active side line; steer back instead.
                long av = self.Vx;
                int airStop = (int)(av * Math.Abs(av) / (2L * ChibiSimConst.AirAccel * ChibiSimConst.VelSub));
                int lim = st.KoSides ? st.BlastX - WalkOffMargin - (_danger ? DangerWalkOffExtra : 0) : st.WallClampX;
                if (st.KoBottom && self.Y > -300) lim = Math.Min(lim, st.HasFloor ? st.FloorHalfW - 500 : st.HalfWidth + 300);
                int px = self.X + airStop + _dx * 300;
                if (px > lim && _dx >= 0) _dx = -1;
                else if (px < -lim && _dx <= 0) _dx = 1;
                return;
            }
            if (_dx == 0 || self.Facing != _dx) return;
            long v = self.Vx;
            int stop = (int)(v * v / (2L * ChibiSimConst.GroundAccel * ChibiSimConst.VelSub));
            if (!SafeGround(st, self.X + _dx * (350 + stop), self.Y)) _dx = 0;
        }

        /// <summary>
        /// Fight-for-centre stages (side KO, no top KO: Plain, Bird Lair): we stand past 45 % of the main stage's half-width
        /// (and within 3000 of its ledge or further out) with the opponent between us and the centre.
        /// </summary>
        static bool Cornered(ChibiFighterSim self, ChibiFighterSim opp, ChibiStageDef st)
        {
            if (!st.KoSides || st.KoTop || !self.Grounded) return false;
            int side = self.X > 0 ? 1 : -1;
            int from = Math.Max(st.HalfWidth * CorneredP / 1000, Math.Min(st.HalfWidth - 3000, st.HalfWidth * 3 / 4));
            return Math.Abs(self.X) > from && opp.X * side < self.X * side && opp.X * side > -2000;
        }

        /// <summary>A solid floor (rules v21, Bird Lair's cloud shelf) lies below feet X <paramref name="x"/>.</summary>
        static bool OverFloor(ChibiStageDef st, int x) => st.HasFloor && Math.Abs(x) < st.FloorHalfW;

        // ================================================================== rules v20: hazards (only when s.HazardsOn)

        /// <summary>Ocean whale is coming or holding (Telegraph..Hold), or rising but still low enough to catch a jump.</summary>
        bool WhaleThreat(ChibiStageDef st)
        {
            if (!_state.HazardsOn || st.Hazard != ChibiStageTable.HazardWhale) return false;
            int ph = _state.HazPhase;
            if (ph == ChibiHazardConst.Idle) return false;
            if (ph == ChibiHazardConst.PhaseWhaleRise) return ChibiSimulation.WhaleBottom(_state) < WhaleClearY;
            return true;
        }

        /// <summary>
        /// Gets under the main stage before the whale: off a platform (Down + Jump drops through), off the main stage's
        /// nearer edge, then steer onto that side's lower tier (fast fall over it), air jump back up to it if we fell past.
        /// </summary>
        void WhaleDescend(ChibiFighterSim self, ChibiFighterSim opp, ChibiStageDef st, ref ChibiButtons press)
        {
            int side = self.X > 0 ? 1 : (self.X < 0 ? -1 : (opp.X > 0 ? -1 : 1));
            int tier = LowTier(st, side);
            if (tier < 0) { Recover(self, st, ref press); return; }
            int cx = ChibiSimulation.PlatformCx(_state, tier), top = ChibiSimulation.PlatformTop(_state, tier);
            int hw = st.Platforms[tier].HalfW;
            int clearX = st.HalfWidth + ChibiSimConst.HurtHalfW + 300;    // past the main stage's side
            if (self.Grounded)
            {
                if (self.Y > 300 && CanPress(ChibiButtons.Jump)) { press |= ChibiButtons.Jump; _dx = 0; _dy = -1; return; }
                _dx = side;                                               // walk off the main stage's edge
                return;
            }
            int tx = side * Math.Max(Math.Abs(cx), clearX + 200);
            if (self.Y > st.MainBottom && Math.Abs(self.X) < clearX) tx = side * (clearX + 600);
            int d = tx - self.X;
            _dx = Math.Abs(d) > 250 ? Math.Sign(d) : 0;
            bool overTier = Math.Abs(self.X - cx) < hw - 400 && Math.Abs(self.X) > clearX - 300;
            if (self.Y < top - 200)
            {
                if (self.Vy < 0 && self.AirJumps > 0 && CanPress(ChibiButtons.Jump)) press |= ChibiButtons.Jump;
                else if (self.AirJumps == 0 && self.Y < top - 1500) Recover(self, st, ref press);
                return;
            }
            if (overTier && self.Y > top + 300) _dy = -1;                 // fast fall onto it
        }

        /// <summary>The highest platform under the main stage's underside on <paramref name="side"/> (else any), -1 when none.</summary>
        int LowTier(ChibiStageDef st, int side)
        {
            int best = -1, bestTop = int.MinValue, any = -1, anyTop = int.MinValue;
            var plats = st.Platforms;
            for (int p = 0; p < plats.Length; p++)
            {
                int top = ChibiSimulation.PlatformTop(_state, p), cx = ChibiSimulation.PlatformCx(_state, p);
                if (top > st.MainBottom - 1000) continue;
                if (top > anyTop) { any = p; anyTop = top; }
                if (cx * side > 0 && top > bestTop) { best = p; bestTop = top; }
            }
            return best >= 0 ? best : any;
        }

        /// <summary>Bird Lair telegraph or swipe (gust + body pass).</summary>
        bool BirdAlert(ChibiStageDef st) =>
            _state.HazardsOn && st.Hazard == ChibiStageTable.HazardBird &&
            (_state.HazPhase == ChibiHazardConst.Telegraph || _state.HazPhase == ChibiHazardConst.PhaseBirdSwipe);

        /// <summary>
        /// Bird Lair: drop off a mesa (Down + Jump, the body band starts just above a standing head on MAIN), land (fast fall,
        /// drift upwind), stand at least <see cref="BirdUpwindX"/> upwind of the centre and hold against the gust there;
        /// shield a close attacker. No jumps, no attacks while it lasts.
        /// </summary>
        void BirdBrace(ChibiFighterSim self, ChibiFighterSim opp, ChibiStageDef st, ref bool block, ref ChibiButtons press)
        {
            int up = _state.HazArg > 0 ? -1 : 1;                          // gust pushes toward HazArg
            if (self.Grounded)
            {
                if (self.Y > 300)
                {
                    if (CanPress(ChibiButtons.Jump)) { press |= ChibiButtons.Jump; _dy = -1; }
                    _dx = 0;
                    return;
                }
                _dx = self.X * up < BirdUpwindX ? up : 0;
                if (opp.InPlay && opp.MoveSlot >= 0 && Math.Abs(opp.X - self.X) < 2500 && Math.Abs(opp.Y - self.Y) < 2000) { block = true; _dx = 0; }
                KeepFooting(self, st);
                return;
            }
            _dx = up;
            if (self.Vy < ChibiSimConst.FastFallTriggerVy) _dy = -1;
        }

        /// <summary>
        /// Volcano fire lanes (Telegraph and Erupt of an active lane, below the column top): leave a lane we are in (toward
        /// the stage unless the outer side is much nearer), cross from the outer side only if the telegraph leaves time,
        /// otherwise hold outside (air jump to stall over the lava) and never drift into it from the stage side. The
        /// position used includes the momentum's stopping distance.
        /// </summary>
        void AvoidFire(ChibiFighterSim self, ChibiStageDef st, ref ChibiButtons press)
        {
            if (!_state.HazardsOn || st.Hazard != ChibiStageTable.HazardFire) return;
            int ph = _state.HazPhase;
            if (ph != ChibiHazardConst.Telegraph && ph != ChibiHazardConst.PhaseFireErupt) return;
            if (self.Y > ChibiHazardConst.FireBottom + ChibiHazardConst.FireRise + 300) return;   // above the column top
            int speed = Math.Max(1, ChibiCombatStats.EffectiveMoveSpeed(self.Stats, self.SlowTicks > 0, self.TailwindTicks > 0) / ChibiSimConst.VelSub);
            int accel = self.Grounded ? ChibiSimConst.GroundAccel : ChibiSimConst.AirAccel;
            int px = self.X + (int)((long)self.Vx * Math.Abs(self.Vx) / (2L * accel * ChibiSimConst.VelSub));
            int left = ph == ChibiHazardConst.Telegraph ? _state.HazTimer : 0;
            const ChibiButtons moving = ChibiButtons.Special | ChibiButtons.Dash | ChibiButtons.Skill1 | ChibiButtons.Skill2 |
                                        ChibiButtons.Skill3 | ChibiButtons.Light | ChibiButtons.Heavy;
            for (int lane = 0; lane < 2; lane++)
            {
                if (!ChibiSimulation.FireLaneOn(_state, lane)) continue;
                int lx = lane == 0 ? ChibiHazardConst.FireLaneLeftX : ChibiHazardConst.FireLaneRightX;
                int outward = lx > 0 ? 1 : -1;
                int band = ChibiHazardConst.FireLaneHalfW + ChibiSimConst.HurtHalfW + FireMargin;
                int rel = (self.X - lx) * outward, relP = (px - lx) * outward;   // > 0: beyond the lane, < 0: stage side
                bool low = self.Y < st.KillY + 2500;                             // about to fall out: go in anyway
                if (Math.Abs(rel) < band || Math.Abs(relP) < band && rel * relP <= 0 || Math.Abs(relP) < band && Math.Abs(rel) < band + 600)
                {
                    int toIn = band + rel, toOut = band - rel;
                    _dx = toIn <= toOut + 900 ? -outward : outward;
                    press &= ~moving;
                    if (!self.Grounded && self.Vy < 0 && self.Y < 0 && self.AirJumps > 0 && CanPress(ChibiButtons.Jump)) press |= ChibiButtons.Jump;
                }
                else if (rel > 0 && !low)
                {
                    int dist = rel + band;
                    if (ph == ChibiHazardConst.Telegraph && dist / speed + 12 < left) _dx = -outward;   // time to cross now
                    else
                    {
                        _dx = rel < band + 1500 ? outward : 0;
                        press &= ~moving;
                        if (!self.Grounded && self.Vy < 0 && self.Y < 0 && self.AirJumps > 0 && CanPress(ChibiButtons.Jump)) press |= ChibiButtons.Jump;
                    }
                }
                else if (rel < 0 && _dx == outward && -relP < band + 1200)
                {
                    _dx = 0;
                    press &= ~(ChibiButtons.Dash | ChibiButtons.Special);
                }
            }
        }

        /// <summary>
        /// Respawn hover: leave after a short random wait, not while the Bird Lair gust is coming or blowing, and not while
        /// the opponent waits right below (until the hover is nearly over).
        /// </summary>
        bool HoverLeaveSafe(ChibiFighterSim me)
        {
            if (_hoverWait < 0) _hoverWait = HoverWaitBase + _rng.Range(HoverWaitSpan) * (4 - Math.Min(3, _difficulty)) / 2;
            if (_hoverWait > 0) { _hoverWait--; return false; }
            var st = ChibiStageTable.Get(_state.Stage);
            if (BirdAlert(st) || WhaleThreat(st) && me.Y > 0) return false;
            var opp = _state.F[1 - _player];
            if (opp.InPlay && me.HoverTicks > HoverCampMinLeft && Math.Abs(opp.X - me.X) < HoverCampX && opp.Y < me.Y && opp.Stun == 0)
                return false;
            return true;
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
