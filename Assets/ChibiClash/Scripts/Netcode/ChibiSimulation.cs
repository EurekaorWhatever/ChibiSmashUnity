using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Tuning constants. Units: positions milli-units (1000 = 1 world unit). Fighter and projectile velocities are
    /// velocity sub-units: 1/<see cref="VelSub"/> milli-unit per tick (accelerations likewise per tick²), so every
    /// movement constant of the original Fighter.cs is an exact integer.
    /// Source: docs/architecture/netcode/movement-conversion.md sections 1-3.
    /// </summary>
    public static class ChibiSimConst
    {
        public const int TickRate = 60;
        /// <summary>Match timer: 300 s of running game steps with 3 stocks (rules v20, GL-2; was 180 s).</summary>
        public const int MatchTicks = 300 * TickRate;

        /// <summary>Velocity sub-units per milli-unit (LCM of the 100/9/18/3/2 denominators; doc section 3).</summary>
        public const int VelSub = 900;

        /// <summary>Gravity 9.81 x 3.6 u/s² = 9.81 milli/tick² (doc row 1). No global fall cap (doc row 21).</summary>
        public const int Gravity = 8829;
        /// <summary>50 Hz arc correction 0.1·g = 0.981 milli/tick, subtracted from airborne Y steps (doc section 2, D1).</summary>
        public const int ArcC50 = 883;
        /// <summary>Ground jump 13 u/s and air jump 11.5 u/s (doc rows 16-17, Fighter.cs:296,302).</summary>
        public const int JumpVel = 195000, AirJumpVel = 172500;
        /// <summary>Free movement accel: ground 70 u/s², air 40 u/s² (doc rows 12-13, Fighter.cs:283).</summary>
        public const int GroundAccel = 17500, AirAccel = 10000;
        /// <summary>Grounded decels: block 60 u/s², attack 25 u/s², disabled 30 u/s² (doc rows 14, 15, 24).</summary>
        public const int ShieldFriction = 15000, MoveFriction = 6250, DisabledFriction = 7500;
        /// <summary>Fast-fall: +45 u/s² while vy &lt; 3 u/s, clamped with max() at -24 u/s (doc rows 18-20, Fighter.cs:307).</summary>
        public const int FastFallAccel = 11250, FastFallCap = 360000, FastFallTriggerVy = 45000;
        /// <summary>Ground probe: grounded up to 100 milli above the floor while vy ≤ 0.5 u/s (doc row 22, Fighter.cs:244,253).</summary>
        public const int GroundProbeHeight = 100, GroundProbeMaxVy = 7500;

        /// <summary>
        /// Sky Dojo aliases only (stage-conversion.md section 8). The simulation reads per-stage values from
        /// <see cref="ChibiStageTable"/>; these stay for readers that predate stages. Feet-offset fixed (rules v7).
        /// </summary>
        public const int StageHalfWidth = 8000;
        /// <summary>Sky Dojo ring-out aliases (feet coordinates): see <see cref="ChibiStageDef"/>.</summary>
        public const int BlastX = 17000, BlastBottom = -9725, BlastTop = 17775;
        /// <summary>Classic-stage respawn feet Y (stages 0..2, the (0, 6) transform). Per stage: <see cref="ChibiStageDef.RespawnY"/>.</summary>
        public const int RespawnY = 7775;
        /// <summary>
        /// Projectile despawn bounds from the original (CombatObjects.cs:108): world |x| &gt; 40, y &lt; -30, y &gt; 40.
        /// Sim Y is relative to the stage top (world groundY = -2.5), so world y -30 / 40 become -27500 / 42500 milli.
        /// Same for every stage. Checked after the hit check, together with Life.
        /// </summary>
        public const int ProjBoundX = 40000, ProjBlastBottom = -27500, ProjBlastTop = 42500;
        /// <summary>Invuln after the respawn hover ends (rules v20: 120, was 90 after a ring-out).</summary>
        public const int RespawnInvuln = ChibiPercentConst.RespawnInvuln;

        public const int HurtHalfW = 375, HurtH = 1350;
        /// <summary>Default skill projectile spawn height above the feet (seeker orbs; Thousand Arrows until step 14).</summary>
        public const int ProjectileY = 700;
        /// <summary>
        /// Projectile pool size (projectile-conversion.md section 5, D-P2 = A): worst match about 52 live. A full pool
        /// drops the shot and counts it in <see cref="ChibiSimState.DroppedShots"/>. Mixed into the config hash (A8).
        /// </summary>
        public const int MaxProjectiles = 64;
        /// <summary>Fighter Center = feet + 0.675 (BoxCollider2D offset, F:32, F:97-99): homing / return / destroy origin.</summary>
        public const int CenterOffY = 675;
        /// <summary>Spear return (CO:57-72, section 3c): 18 u/s, arrive when pre-move distance &lt; 0.5 (strict), Life floor 0.5 s.</summary>
        public const int ReturnSpeedSub = 270000, ReturnArriveR = 500, ReturnLifeFloor = 30;
        /// <summary>
        /// Thousand Arrows split (F:1019-1032, section 4, D-P1 = A): 18 arrows; target offset Range(-2.5, 2.5) x Range(-0.5, 0.5);
        /// start inside a 0.6 disc by rejection (max 16 tries, then the centre); speed 14..19 u/s; r 250, life 1.5 s,
        /// mult 0.45, kb (1.5, 1) u/s, stun 0.15 s.
        /// </summary>
        public const int VolleyCount = 18, VolleyOxHalf = 2500, VolleyOyHalf = 500, VolleyDiscR = 600, VolleyMaxTries = 16,
                         VolleySpeedMin = 210000, VolleySpeedSpan = 75001, VolleyR = 250, VolleyLife = 90,
                         VolleyMult = 450, VolleyKbX = 22500, VolleyKbY = 15000, VolleyStun = 9;
        /// <summary>Sword Aura wave hit radius 0.5 (F:490-494, projectile-conversion.md section 1 row 5).</summary>
        public const int AuraWaveR = 500;

        public const int UltMax = 1000;
        public const int UltPerHit = 30, UltPerComboHit = 50, UltWhenHit = 15, UltPerParry = 150;
        public const int ParryWindow = 11;        // ticks after raising the shield in which a block is a perfect parry
        /// <summary>Perfect parry stuns the attacker 0.45 s = 27 ticks via Stun() (damage-conversion.md section 3a, F:563-572).</summary>
        public const int PerfectParryStun = 27;
        /// <summary>Parry skill reflect: attacker stun 0.5 s = 30 ticks (damage-conversion.md section 3b, F:550-561).</summary>
        public const int ReflectStun = 30;
        /// <summary>Parry skill reflect push (sx·5, 4) u/s in velocity sub-units, sx from positions (section 3b).</summary>
        public const int ReflectPushX = 75_000, ReflectPushY = 60_000;
        /// <summary>Knockback is applied only when vx² + vy² exceeds this (|kb| &gt; 0.1 u/s = 1500 sub, F:611-628, section 2).</summary>
        public const long KbMinSq = 2_250_000;
        /// <summary>SinceAttackTicks at match start: lastAttackTime = -10 s, so the first attack qualifies for Patience (section 5).</summary>
        public const int PatienceInit = 600;
        /// <summary>Saturation cap of SinceAttackTicks (keeps the counter bounded in long matches).</summary>
        public const int SinceAttackCap = 1 << 20;
        /// <summary>
        /// Combo window: a light chains when started at most 54 ticks after the previous normal's start (elapsed &gt; 0.9 s
        /// resets, Fighter.cs:457). Set at StartMove, Dec at the tick top, reset when 0 (normals-conversion.md section 3).
        /// No input buffer exists (decision D-N1 = A): actions fire only from a press edge on a free tick.
        /// </summary>
        public const int ComboWindowTicks = 55;
        public const int AuraDuration = 600, RingDuration = 600, RingRadius = 2100, GuardRadius = 1600;
        /// <summary>Archer / mage reload 1.5 s, set on the finisher's SHOT tick (Fighter.cs:474, normals-conversion.md section 4).</summary>
        public const int SetCooldownTicks = 90;
        /// <summary>Normal timing base durations in ms: light 0.24 s, heavy 0.42 s (Fighter.cs:456).</summary>
        public const int LightBaseMs = 240, HeavyBaseMs = 420;
        /// <summary>Light finisher damage multiplier, permille (Fighter.cs:461).</summary>
        public const int FinisherMult = 1500;
        /// <summary>
        /// Sword Aura wave (Fighter.cs:490-494, normals-conversion.md section 6): spawn feet + (F·700, 675), 15 u/s =
        /// 225000 sub/tick, life 0.9 s = 54 moves, mult 0.8, kb (4, 2) u/s = 60000 / 30000 sub, stun 0.25 s = 15, non-piercing.
        /// </summary>
        public const int AuraWaveOffX = 700, AuraWaveOffY = 675, AuraWaveSpeedSub = 225000, AuraWaveLife = 54,
                         AuraWaveMult = 800, AuraWaveKbX = 60000, AuraWaveKbY = 30000, AuraWaveStun = 15;

        // Dash (docs/architecture/netcode/dash-conversion.md section 1, decision D2 = A: original at 60 fps).
        /// <summary>Dash speed 17 u/s = 283.333 milli/tick (row 1, Fighter.cs:270). Speed modifiers do not apply (row 9).</summary>
        public const int DashSpeedSub = 255000;
        /// <summary>DashTicks set at start: 0.13 s → ceil(7.8) = 8, so motion runs on ticks 1..7 (row 2, Fighter.cs:312).</summary>
        public const int DashDuration = 8;
        /// <summary>Dash cooldown 0.55 s = 33 ticks; a new dash may start on tick 33 (row 3, Fighter.cs:313).</summary>
        public const int DashCooldown = 33;
        /// <summary>Invuln = max(Invuln, 6) at dash start: 0.1 s, immune on ticks 0..5 (row 4, Fighter.cs:314).</summary>
        public const int DashInvuln = 6;
        /// <summary>
        /// Extra arc term subtracted from airborne Y steps on ticks where the original forces vy = 0 every frame
        /// (dash motion, Dash &amp; Thrust): total sag Gravity + 2·ArcC50 = 10595 sub/tick (row 6).
        /// </summary>
        public const int LevelArcSub = 2 * ArcC50;

        // Time scale (docs/architecture/netcode/timescale-conversion.md sections 1 and 5, decision D10 = A-held).
        // Scales are permille of one game step per real tick; durations are REAL ticks (unscaled, MC:177/188).
        /// <summary>Hitstop factor 0.05 (MC:112).</summary>
        public const int ScaleHitstop = 50;
        /// <summary>Ult cinematic factor 0.2 (MC:113).</summary>
        public const int ScaleCinematic = 200;
        /// <summary>Full speed: one game step per real tick. Also the TimeAccum cost of one game step.</summary>
        public const int ScaleNormal = 1000;
        /// <summary>Normal hit hitstop: ceil(0.045 x 60) = 3 real ticks (F:625, H1).</summary>
        public const int HitstopNormal = 3;
        /// <summary>Critical hit hitstop: ceil(0.08 x 60) = 5 real ticks (F:625, H2).</summary>
        public const int HitstopCrit = 5;
        /// <summary>Perfect shield parry hitstop: 0.1 s = 6 real ticks (F:570, H3).</summary>
        public const int HitstopParry = 6;
        /// <summary>Parry-skill reflect hitstop: ceil(0.12 x 60) = 8 real ticks (F:559, H4); absorbs the reflect's own 3.</summary>
        public const int HitstopReflect = 8;
        /// <summary>Ult cinematic: 0.8 s = 48 real ticks, restarted (not extended) by a second ult (MC:188, C).</summary>
        public const int CinematicTicks = 48;
        /// <summary>Finish slow-mo, VIEW ONLY (MC:114): 72 real ticks at 0.3. Not used by the sim, not in the config hash.</summary>
        public const int FinishSlowTicks = 72, FinishScale = 300;

        // Program moves (docs/architecture/netcode/multiphase-conversion.md, port step 12, rules v12). Frames are game
        // steps counted from the press (frame 0 runs on the press tick); locks = first tick a new action is accepted
        // (MoveLockEnd, section 1). kb in velocity sub-units, stun in ticks, boxes feet-relative milli toward Facing.
        /// <summary>Thrust Barrage (2.1): hits every 7 frames on 0..35 (6 hits), lock 48; last hit kb (6, 4) u/s.</summary>
        public const int ThrustPeriod = 7, ThrustHits = 6, ThrustLock = 48, ThrustKbX = 12000, ThrustKbY = 7500,
                         ThrustLastKbX = 90000, ThrustLastKbY = 60000, ThrustStun = 15,
                         ThrustBoxX = 0, ThrustBoxW = 1800, ThrustBoxY = 125, ThrustBoxH = 1100;
        /// <summary>
        /// Heavy Grab (2.2): inclusive box, whiff lock 21, hit lock 66, victim Stun 72; lift 18 frames to holder + (200, 1500),
        /// slam path 5j/36 toward holder(f18) + (1000, 0), release + slam hit on frame 26 (kb (4, 8) u/s, stun 42, unblockable).
        /// </summary>
        public const int GrabWhiffLock = 21, GrabHitLock = 66, GrabStun = 72,
                         GrabBoxX = 150, GrabBoxW = 1300, GrabBoxY = -25, GrabBoxH = 1400,
                         GrabTopDx = 200, GrabTopDy = 1500, GrabSlamDx = 1000,
                         GrabLiftFrames = 18, GrabSlamStep = 5, GrabSlamDen = 36, GrabReleaseFrame = 26,
                         GrabSlamKbX = 60000, GrabSlamKbY = 120000, GrabSlamStun = 42;
        /// <summary>Tornado Spin (2.3): circle r 1.6 from Center every 12 frames on 0..48, steer MoveX·3.5 u/s on 1..59, lock 65.</summary>
        public const int TornadoPeriod = 12, TornadoLastHit = 48, TornadoSteerEnd = 60, TornadoLock = 65, TornadoR = 1600,
                         TornadoKb = 45000, TornadoStun = 15, TornadoSteer = 52500;
        /// <summary>Backstep Shot (2.4): V (-F·16, 2) u/s on frames 1..8, frame 9 Vx -F·2 u/s + big arrow, lock 24.</summary>
        public const int BackstepShotFrame = 9, BackstepLock = 24, BackstepVx = 240000, BackstepVy = 30000, BackstepExitVx = 30000;
        /// <summary>Arrow Barrage (2.5): arrows every 6 frames on 0..24, yOff = -250 + Rng.Range(551) milli, lock 36.</summary>
        public const int BarragePeriod = 6, BarrageShots = 5, BarrageLock = 36, BarrageYOffMin = -250, BarrageYOffSpan = 551;
        /// <summary>
        /// Leap Slam (2.6, D-M2 = B): leap V (F·6, 14) u/s on frame 1; slam on the first frame &gt;= 10 whose start-of-tick
        /// Grounded is true, else frame 78; lock ends slam + 12 (90 until the slam). Box strict, kb (6, 8) u/s, stun 30.
        /// </summary>
        public const int LeapVx = 90000, LeapVy = 210000, LeapEarliest = 10, LeapTimeout = 78, LeapAfterSlam = 12,
                         LeapBoxX = 0, LeapBoxW = 2500, LeapBoxY = -125, LeapBoxH = 1600, LeapKbX = 90000, LeapKbY = 120000,
                         LeapStun = 30;
        /// <summary>Spin Guard (2.7): circle r 1.5 every 18 frames on 0..54, aura GuardTicks 72 set once on frame 0, lock 72.</summary>
        public const int SpinPeriod = 18, SpinLastHit = 54, SpinLock = 72, SpinR = 1500, SpinKbX = 37500, SpinKbY = 30000,
                         SpinStun = 12, SpinGuardTicks = 72;

        // ---- rules v14: ultimates (ultimates-conversion.md sections 1, 2, 4 + technical-director review).
        /// <summary>Common ult start (section 1, F:960): Invuln = max(Invuln, 36) before the class part.</summary>
        public const int UltCommonInvuln = 36;
        /// <summary>
        /// Smash (2.2, F:984-1008): frame 0 V (0, 15) u/s (applied, fact 3; a same-tick jump keeps its vy, TD A2), invuln 84
        /// (assignment); slam on the first frame &gt;= 10 whose start-of-tick Grounded is true, else frame 66; two waves dir -1
        /// then +1 at (X ± 600, Y + 550), ±13 u/s, r 600 + 14 / update, life 66, x3.0, kb (6, 12), stun 48, Pierce, indestructible;
        /// lock ends slam + 18 (84 until the slam).
        /// </summary>
        public const int SmashJumpVel = 225000, SmashMinFrame = 10, SmashTimeout = 66, SmashAfterSlam = 18, SmashInvuln = 84,
                         SmashWaveDx = 600, SmashWaveDy = 550, SmashWaveVel = 195000, SmashWaveR = 600, SmashWaveGrow = 14,
                         SmashWaveLife = 66, SmashWaveMult = 3000, SmashWaveKbX = 90000, SmashWaveKbY = 180000, SmashWaveStun = 48;
        /// <summary>Thousand Arrows (2.3, F:1010-1034): lock 48, invuln 48 (assignment); harmless lead (X + F·500, Y + 975), V (F·9, 12) u/s, life 27.</summary>
        public const int ArrowsLock = 48, ArrowsInvuln = 48, LeadDx = 500, LeadDy = 975, LeadVx = 135000, LeadVy = 180000,
                         LeadLife = 27;
        /// <summary>
        /// Kick (2.5, F:1036-1079): lock 114, invuln 120 (assignment); in range = alive and |dx| &lt; 8000, |dy| &lt; 3000 (strict).
        /// A: dash F·20 u/s on frames 0..14, frame 15 box near edge 0, width 2500, height 1400 around Center (TD A1), x2.0,
        /// kb (8, 6), stun 30, blockable. B: lerp to (oX - F·1300, oY) on frames 1..8 (k / 9), frame 9 Stun(96) + hold + x1.5
        /// unblockable hit (kb 0, stun 72), frame 30 rise Vy 12 u/s, frame 50 release + x2.5 unblockable hit kb (F·12, 9), stun 48.
        /// </summary>
        public const int KickLock = 114, KickInvuln = 120, KickRangeX = 8000, KickRangeY = 3000, KickDashVel = 300000,
                         KickDashFrames = 15, KickBoxBx = 0, KickBoxBw = 2500, KickBoxH = 1400, KickBoxMult = 2000,
                         KickBoxKbX = 120000, KickBoxKbY = 90000, KickBoxStun = 30, KickLerpFrames = 9, KickBehind = 1300,
                         KickStun = 96, KickHit1Mult = 1500, KickHit1Stun = 72, KickRiseFrame = 30, KickRiseVel = 180000,
                         KickReleaseFrame = 50, KickHit2Mult = 2500, KickHit2KbX = 180000, KickHit2KbY = 135000, KickHit2Stun = 48;

        /// <summary>Every step-14 ult constant, mixed into the config hash (TD amendment 8).</summary>
        public static readonly int[] UltConstants =
        {
            UltCommonInvuln,
            SmashJumpVel, SmashMinFrame, SmashTimeout, SmashAfterSlam, SmashInvuln, SmashWaveDx, SmashWaveDy, SmashWaveVel,
            SmashWaveR, SmashWaveGrow, SmashWaveLife, SmashWaveMult, SmashWaveKbX, SmashWaveKbY, SmashWaveStun,
            ArrowsLock, ArrowsInvuln, LeadDx, LeadDy, LeadVx, LeadVy, LeadLife,
            KickLock, KickInvuln, KickRangeX, KickRangeY, KickDashVel, KickDashFrames, KickBoxBx, KickBoxBw, KickBoxH,
            KickBoxMult, KickBoxKbX, KickBoxKbY, KickBoxStun, KickLerpFrames, KickBehind, KickStun, KickHit1Mult, KickHit1Stun,
            KickRiseFrame, KickRiseVel, KickReleaseFrame, KickHit2Mult, KickHit2KbX, KickHit2KbY, KickHit2Stun,
        };

        /// <summary>Every step-12 program constant, mixed into the config hash (TD amendment 6): peers with other values desync.</summary>
        public static readonly int[] ProgramConstants =
        {
            ThrustPeriod, ThrustHits, ThrustLock, ThrustKbX, ThrustKbY, ThrustLastKbX, ThrustLastKbY, ThrustStun,
            ThrustBoxX, ThrustBoxW, ThrustBoxY, ThrustBoxH,
            GrabWhiffLock, GrabHitLock, GrabStun, GrabBoxX, GrabBoxW, GrabBoxY, GrabBoxH, GrabTopDx, GrabTopDy, GrabSlamDx,
            GrabLiftFrames, GrabSlamStep, GrabSlamDen, GrabReleaseFrame, GrabSlamKbX, GrabSlamKbY, GrabSlamStun,
            TornadoPeriod, TornadoLastHit, TornadoSteerEnd, TornadoLock, TornadoR, TornadoKb, TornadoStun, TornadoSteer,
            BackstepShotFrame, BackstepLock, BackstepVx, BackstepVy, BackstepExitVx,
            BarragePeriod, BarrageShots, BarrageLock, BarrageYOffMin, BarrageYOffSpan,
            LeapVx, LeapVy, LeapEarliest, LeapTimeout, LeapAfterSlam, LeapBoxX, LeapBoxW, LeapBoxY, LeapBoxH, LeapKbX, LeapKbY, LeapStun,
            SpinPeriod, SpinLastHit, SpinLock, SpinR, SpinKbX, SpinKbY, SpinStun, SpinGuardTicks,
        };

        // ---- rules v13: Mage minions + Thunder Cloud (minion-cloud-conversion.md sections 0, 1, 2, 3.2 + TD review).
        /// <summary>Summon pool sizes (section 3.1): a full pool drops the spawn and counts it in DroppedSummons. In the config hash.</summary>
        public const int MaxMinions = 12, MaxClouds = 4;
        /// <summary>
        /// Minion body and physics (CO:119-171, section 1.3): gravityScale 3 = 7357.5 sub/tick² rounded, C50 arc 735.75
        /// rounded, circle r 0.22. D-C1 = A (TD amendment 1, 50 Hz basis): Unity default friction 0.4 x 29.43 x 0.02 s =
        /// 0.2354 u/s removed per Box2D step on the ground, so the grounded walk is 52500 - 3532 = 48968 sub/tick.
        /// Wall cling (amendment 2): 1.4 u/s x 50/60 = 17500 sub/tick of Vy friction while pressing into the stage side.
        /// Landing step (amendment 3): friction removes MinionLandFricPermille/1000 x |Vy| of Vx on the landing tick.
        /// </summary>
        public const int MinionGravity = 7358, MinionArc = 736, MinionR = 220, MinionWalkSub = 52500,
                         MinionFrictionSub = 3532, MinionWallFrictionSub = 17500, MinionLandFricPermille = 400;
        /// <summary>Minion AI (CO:149-171): stop |dx| &lt;= 0.4, bonk |dx| &lt; 0.7 and |dy| &lt; 0.9, life 6 s, cd 0.4 s then 1 s, kill y -20.</summary>
        public const int MinionStopDx = 400, MinionBonkDx = 700, MinionBonkDy = 900, MinionLife = 360, MinionFirstCd = 24,
                         MinionCd = 60, MinionKillY = -20000;
        /// <summary>Bonk HitSpec.Make(0.35, (2 dir, 2), 0.15) and the hop back (-2 dir, 5) u/s (CO:168-169).</summary>
        public const int MinionMult = 350, MinionKb = 30000, MinionStun = 9, MinionHopVx = 30000, MinionHopVy = 75000;
        /// <summary>Spawn (F:886-892): Center + (Facing (0.6 + 0.5k), 0.3), k = 0..2; actionLock 0.4 s.</summary>
        public const int MinionSpawnOx = 600, MinionSpawnStep = 500, MinionSpawnOy = 300, MinionCount = 3, MinionLock = 24;
        /// <summary>
        /// Thunder Cloud (F:894-899, CO:175-220): spawn Center + 3 up, follow goal Center + 3.2 up with Lerp(2.5 dt) = 1/24
        /// per tick (D-C5 = A, position in micro = 1/1000 milli), life 4.6 s, strikes at 0.8 s then every 1.1 s, bolt 0.15 s,
        /// x-only window 0.75, HitSpec.Make(0.8, (0, -3), 0.35); actionLock 0.3 s.
        /// </summary>
        public const int CloudSpawnOy = 3000, CloudGoalOy = 3200, CloudLerpDiv = 24, CloudLife = 276, CloudFirstStrike = 48,
                         CloudPeriod = 66, CloudBolt = 9, CloudStrikeDx = 750, CloudMult = 800, CloudKbY = -45000, CloudStun = 21,
                         CloudLock = 18;

        /// <summary>Every step-13 constant, mixed into the config hash next to the pool sizes.</summary>
        public static readonly int[] SummonConstants =
        {
            MaxMinions, MaxClouds,
            MinionGravity, MinionArc, MinionR, MinionWalkSub, MinionFrictionSub, MinionWallFrictionSub, MinionLandFricPermille,
            MinionStopDx, MinionBonkDx, MinionBonkDy, MinionLife, MinionFirstCd, MinionCd, MinionKillY,
            MinionMult, MinionKb, MinionStun, MinionHopVx, MinionHopVy,
            MinionSpawnOx, MinionSpawnStep, MinionSpawnOy, MinionCount, MinionLock,
            CloudSpawnOy, CloudGoalOy, CloudLerpDiv, CloudLife, CloudFirstStrike, CloudPeriod, CloudBolt, CloudStrikeDx,
            CloudMult, CloudKbY, CloudStun, CloudLock,
        };

        // ---- rules v15: gesture controls (port step 15, docs/architecture/netcode/gesture-conversion.md and
        // security-gesture-review.md rules 1-6). A gesture is a HELD input (bits 13-14) exactly like a button: every window
        // and cooldown below is a hashed sim counter, never wall-clock time. No camera claim travels in the packet: bit 15 was
        // the self-reported PoseVerified hint until protocol 2 and is the Special button since protocol 3 / rules v18.
        /// <summary>Ult gesture window 3 s and retry wait 1.5 s, in REAL ticks: unscaled like the original (F:14, F:405-433).</summary>
        public const int UltWindowTicks = 180, UltRetryTicks = 90;
        /// <summary>The class gesture must be held for StableTime &gt;= 0.15 s = 9 consecutive real ticks (F:412).</summary>
        public const int UltGestureHoldTicks = 9;
        /// <summary>Saturation cap of <see cref="ChibiFighterSim.GestureTicks"/> (only the 9-tick threshold matters).</summary>
        public const int GestureTicksCap = 255;
        /// <summary>HUD prompt timers (F:422 Detected 2 s, F:431 Failed 1.3 s), real ticks. Presentation only, deterministic.</summary>
        public const int PromptDetectedTicks = 120, PromptFailedTicks = 78;
        /// <summary>Super Jump pose (F:375-382): RaiseHands onset, cd 3 s (game ticks, scaled dt), Vy 19 u/s, air jump refilled.</summary>
        public const int SuperJumpCd = 180, SuperJumpVel = 285000;
        /// <summary>
        /// Pose Fireball (F:383-391): PunchForward onset while free (actionLock &lt;= 0), cd 4 s, actionLock 0.25 s (move
        /// slot <see cref="PoseSlot"/>), Shoot((0.6, 0.1)) from Center = feet + (F·600, 775), 13 u/s, r 0.3, life 1.6 s,
        /// HitSpec.Make(0.8, (4, 3), 0.3), destructible. Not a normal or skill: no Patience, no lastAttackTime reset.
        /// </summary>
        public const int PoseShotCd = 240, PoseShotLock = 15, PoseShotOx = 600, PoseShotOy = 775, PoseShotVel = 195000,
                         PoseShotR = 300, PoseShotLife = 96, PoseShotMult = 800, PoseShotKbX = 60000, PoseShotKbY = 45000,
                         PoseShotStun = 18;
        /// <summary>Move slot of the Pose Fireball's 0.25 s actionLock (no hitbox; the shot spawns on the pose tick).</summary>
        public const int PoseSlot = 6;

        // ---- rules v16: match flow (match-flow-conversion.md).
        /// <summary>Intro countdown 2.2 s (MC:26) in REAL ticks (unscaled, MC:124): real ticks 0..131 run with input off.</summary>
        public const int IntroTicks = 132;
        /// <summary>The "FIGHT!" banner shows for the last 0.8 s of the intro (Hud.cs:49): 48 real ticks. View only.</summary>
        public const int IntroFightTicks = 48;

        // ---- rules v22: responsiveness pass (user decisions UL-1 / EL-1 / TN-1, 2026-11-14).
        /// <summary>
        /// Attack buffer: an attack press (Light, Heavy, Special, Skill1-3) in the last 6 lock ticks of a move, or during
        /// the attack lock left by a walk cancel, is replayed on the first free tick (replaces D-N1).
        /// </summary>
        public const int AttackBufferTicks = 6;
        /// <summary>Walk / turn cancel: a new stick direction on the ground ends the last 6 recovery ticks of a move.</summary>
        public const int MoveCancelTicks = 6;
        /// <summary>The buttons the attack buffer holds.</summary>
        public const int AttackBufferMask = (int)(ChibiButtons.Light | ChibiButtons.Heavy | ChibiButtons.Special |
                                                  ChibiButtons.Skill1 | ChibiButtons.Skill2 | ChibiButtons.Skill3);
        /// <summary>Every rules-v22 constant, mixed into the config hash.</summary>
        public static readonly int[] FeelConstants = { AttackBufferTicks, MoveCancelTicks, AttackBufferMask };

        /// <summary>Every step-15 constant, mixed into the config hash.</summary>
        public static readonly int[] GestureConstants =
        {
            UltWindowTicks, UltRetryTicks, UltGestureHoldTicks, GestureTicksCap, PromptDetectedTicks, PromptFailedTicks,
            SuperJumpCd, SuperJumpVel, PoseShotCd, PoseShotLock, PoseShotOx, PoseShotOy, PoseShotVel, PoseShotR,
            PoseShotLife, PoseShotMult, PoseShotKbX, PoseShotKbY, PoseShotStun, PoseSlot,
        };

        /// <summary>
        /// Ult gesture per <see cref="FighterClass"/> index (ClassData.cs ultGesture, pinned by a test): Melee CrossArms,
        /// Heavy RaiseHands, Ranged PunchForward, Magic CrossArms, Polearm PunchForward. Values are <see cref="Gesture"/> ids.
        /// </summary>
        public static readonly int[] UltGesture =
        {
            (int)Gesture.CrossArms, (int)Gesture.RaiseHands, (int)Gesture.PunchForward, (int)Gesture.CrossArms,
            (int)Gesture.PunchForward,
        };
    }

    /// <summary>
    /// Ult prompt shown on the HUD (Fighter.cs PromptState, F:8). Lives in sim state so both peers and replays show the
    /// same prompt; it never affects gameplay.
    /// </summary>
    public enum ChibiUltPrompt { Hidden = 0, Preparing = 1, Detected = 2, Failed = 3 }

    public partial struct ChibiMatchConfig
    {
        /// <summary>Bump whenever simulation rules change so old replays / mismatched peers are detected.</summary>
        /// <summary>Bump whenever simulation rules or move data change, so mismatched peers/replays are rejected.</summary>
        /// <remarks>2: Melee Dash &amp; Thrust matched to the original. 3: melee light/heavy hit areas matched to the original MeleeBox. 4: input message widened to 16 bits (Dash, gesture, PoseVerified). 5: movement matched to original (1/900 velocity units, C50 arc term, friction/fast-fall/air-block/jump-during-attack). 6: Dash (8 ticks, 33-tick cd, 6-tick invuln) and Dash &amp; Thrust end speed matched to original. 7: three stages, one-way platforms, drop-through, solid stage sides/underside, per-stage ring-out. 8: normal attacks matched to original (timing, kb, stun, combo, reload, projectiles, aura on heavy); no input buffer (D-N1). 9: damage rules matched to original (exact micro-HP, crit/defense/block/guard/parry/passives, roll order). 10: hitstop + ult cinematic via integer time accumulator (D10, A-held), GameTicks clock, replay v2.</remarks>
        /// <remarks>11: projectile kinds (projectile-conversion.md, port step 11): circle hit radius per kind, 2D constant-speed homing, spear return + SpearOut lock, growth / pierce / harmless flags, Thousand Arrows split machinery, destroy check from Center (strict), kb sign Vx &gt;= 0, 64-slot pool with DroppedShots; MaxProjectiles in the config hash.</remarks>
        /// <remarks>12: multi-phase skills matched to original (program moves, held state, priority) — multiphase-conversion.md: per-move frame programs with MoveLockEnd, Heavy Grab hold (Held / Holding, GrabPhase before the melee probes), Leap Slam really leaps (D-M2 = B), same-tick grabs both whiff (D-M3 = B), Light &gt; Heavy &gt; skills (D-M1 = A), no attack speed on skills; step-12 constants in the config hash.</remarks>
        /// <remarks>13: Mage minions + Thunder Cloud (minion-cloud-conversion.md, port step 13): 12-slot minion pool (circle body, gravity x3, ground friction 48968 walk, wall cling, landing-step friction, bonk + hop), 4-slot cloud pool (micro-unit lerp follow, strikes 48 + 66n), D-C3 order fighters -> projectiles -> minions -> clouds, DroppedSummons; SeekerOrb and the thunder strike stand-in removed; pool sizes and step-13 constants in the config hash.</remarks>
        /// <remarks>14: ultimates matched to original (ultimates-conversion.md, port step 14): full common start (Interrupt, Stun 0, invuln 36 then the class assignment), Sword Aura / Fire Ring on the press tick with no lock (D-U2 = A), Smash jump + landing slam + two growing SmashWaves, Thousand Arrows lead + split, Polearm Kick dash box / teleport lerp + hold + two hits (KickFrom/To fields, D-U3 = A; invulnerable victim still held, D-U4 = A); ult frame-0 velocity applied but a same-tick jump keeps its vy (TD A2); step-14 constants in the config hash.</remarks>
        /// <remarks>15: gesture controls (gesture-conversion.md, port step 15): the Ult button opens the 3 s gesture window (180 real ticks, 1.5 s retry wait), the class gesture held 9 real ticks fires the ult even while stunned or held; CrossArms blocks like Shield; RaiseHands Super Jump; PunchForward Pose Fireball (slot 6 lock); the instant-ult button stand-in is removed. PoseVerified is never read.</remarks>
        /// <remarks>16: match flow (match-flow-conversion.md, port step 16): optional 2.2 s intro (132 real ticks, input off, no clock, no ring-out), match clock counts running game steps only, timeout winner by exact HP fraction (cross-multiplied), training (no timer, KO revives, ult fill command), same-tick double KO = draw (D-F1).</remarks>
        /// <remarks>17: MaxCombo per fighter (rewards / results, port step 18), in state and hash.</remarks>
        /// <remarks>18: character movesets (design/gdd/movesets, CH-1..CH-4, OD-1..OD-4): 108 CSV rows as slots 7+ (generated by tools/gameplay/gen_movesets.py), README 1.2 input resolution (tilts, smashes, aerials, dash attack window 16, Special button on input bit 15 / protocol 3, Skill + Up / Down techniques with fallback), facing locked in the air, generic mechanics (charge / charge levels, hold, multi-hit links, circle boxes, spike / trip / back / pull, armor, invuln window, counter, reflect, contact / timed / periodic zones with slow, dive + landing hit, teleport, tether pull, roll, recoil, explicit / fan velocities, pierce, bounce, aironce, restorejump), new state (TechCd, charge, hold, air mask, pull, zone pool, projectile SrcSlot / Bounces / Rearm), all rows and constants in the config hash.</remarks>
        /// <remarks>19: security review SEC-101..106: Jade Stance cd 240 sharing Jade Counter's technique slot, reflect growth capped at two scaled reflections (projectile Reflects), teleport clamped above the kill line, cooldown / aironce gate on every moveset start path, charge / riposte / hold / landing-hit / pull flags cleared by Interrupt and by every move start.</remarks>
        /// <remarks>20: damage percent + stocks (design/gdd/percent-knockback.md, GL-1 / GL-2): HP removed from the state, every hit's micro-HP becomes percent (x 0.2), Smash knockback from percent / hit percent (capped 15 %) / weight / base and growth derived from KbX-KbY (set knockback under magnitude 30 000 and for grounded trips), Ren / Lyra x0.85, hitstun KB x 0.4 capped 90, launch decay and hitstun gravity fade, armor breaks at KB 150; 3 stocks, KO only on an active stage edge, out 60 / hover 180 / invuln 120, last stock loses, same-tick double last stock = draw, 300 s timer, timeout by stocks then percent then draw, the -15 % ring-out rule removed. Five new stages 3..7 (ST-1..ST-5): per-edge KO / walls / solid floor / sky cap, per-stage main underside, moving platforms (F1 of GameTicks, riders carried), hazards with their own RNG stream (HazardsOff in the config hash); stage geometry and the percent constants in the config hash.</remarks>
        /// <remarks>21: GL-2 applied consistently (lead): every side KO line is at least 8000 beyond the outermost walkable surface (Plain ground +-16991 -> KO +-25000 with a bottom line, Bird Lair cloud floor finite +-12000 -> KO +-20000 with a bottom line, Volcano 17500, Ocean 19800); finite solid floors (HasFloor / FloorY / FloorHalfW, in the stage hash); Sword Aura waves no longer get the ult base bonus.</remarks>
        /// <remarks>22: responsiveness pass (UL-1, EL-1, TN-1, 2026-11-14): the Ult press with a full meter fires the ultimate on its game step (the gesture window is no longer opened); recovery trimmed (CSV rows by category with floors, counters / rolls / armor / reflect / dive rows unchanged; normals x 3/4; Dash &amp; Thrust, Choke, Fireball, Spear rows); 6-tick attack buffer (BufferedEdges / BufferTicks, replaces D-N1, fresh press wins, cleared by Interrupt); grounded walk / turn cancel in the last 6 recovery ticks of non-program moves when the stick differs from the move's start direction (Light rows and the light normal always, other moves on hit), attacks stay locked until the old lock end (AttackLockTicks) and go to the buffer; constants in the config hash.</remarks>
        /// <remarks>23: balance pass (review 2026-11-16 batch 4, B4-1..B4-3, 2026-11-17): trip loops removed (grounded trip stun Bruna down tilt 22, Ren down tilt 26, Kaia low sweep 28, was 34); Bruna down smash knockback 120000/90000 (was 45000/157500, straight up); the walk cancel's "on hit" needs a landed or blocked melee hit (MoveConnected, rolled back and hashed; a touch on an invulnerable, hovering, countered or parried target no longer counts); attack presses in a dash's last AttackBufferTicks are buffered and come out when the dash ends.</remarks>
        /// <remarks>24: escape dash (ED-1, 2026-11-17): a Dash press during a knockback launch (not set knockback) after 10 game ticks of it, with DashCd 0, halves Vx / Vy, ends the hitstun and the launch and starts the 33-tick dash cooldown (no invuln, no dash motion); LaunchAge and EscapeDashes per fighter in state and hash; EscapeMinAge / EscapeSpeedP / LaunchAgeCap in the config hash.</remarks>
        public const int RulesVersion = 24;

        public uint Seed;
        public FighterClass Class0, Class1;
        public int Passive0, Passive1;
        /// <summary>Play the 2.2 s intro countdown before input is enabled (production matches). Unit tests usually leave it off.</summary>
        public bool Intro;
        /// <summary>Training mode (MC:68, MC:157-162): no match timer, a KO revives at full HP instead of ending the match.</summary>
        public bool Training;
        /// <summary>
        /// Stage index: 0 Sky Dojo, 1 Neon City, 2 Lava Forge (Classic), 3 Mountain of the Beginning, 4 Plain of Unity,
        /// 5 Volcano Abyss, 6 Giant Bird Lair, 7 Abandoned Ocean Monument (rules v20, ST-1). Any other value plays as Sky
        /// Dojo (Stage.cs <c>default:</c>). Source: stage-conversion.md section 8.
        /// </summary>
        public int Stage;
        /// <summary>
        /// Rules v20 (ST-5): stage hazards OFF. The default (false) means hazards ON, the design default; the host decides
        /// in rooms and it travels in the handshake Start message. Moving platforms move either way. In the config hash.
        /// </summary>
        public bool HazardsOff;

        /// <summary>
        /// FNV-style hash of RulesVersion and every config field. Peers / replays with different hashes
        /// would not simulate the same match.
        /// </summary>
        /// <example><code>bool same = a.ComputeHash() == b.ComputeHash();</code></example>
        public uint ComputeHash()
        {
            uint h = 2166136261u;
            ChibiSimState.Mix(ref h, RulesVersion);
            ChibiSimState.Mix(ref h, unchecked((int)Seed));
            ChibiSimState.Mix(ref h, (int)Class0);
            ChibiSimState.Mix(ref h, (int)Class1);
            ChibiSimState.Mix(ref h, Passive0);
            ChibiSimState.Mix(ref h, Passive1);
            ChibiSimState.Mix(ref h, Stage);
            // Time-scale gameplay constants (timescale-conversion.md section 5): peers with other values would desync.
            ChibiSimState.Mix(ref h, ChibiSimConst.ScaleHitstop);
            ChibiSimState.Mix(ref h, ChibiSimConst.ScaleCinematic);
            ChibiSimState.Mix(ref h, ChibiSimConst.HitstopNormal);
            ChibiSimState.Mix(ref h, ChibiSimConst.HitstopCrit);
            ChibiSimState.Mix(ref h, ChibiSimConst.HitstopParry);
            ChibiSimState.Mix(ref h, ChibiSimConst.HitstopReflect);
            ChibiSimState.Mix(ref h, ChibiSimConst.CinematicTicks);
            // Pool size (A8): peers with another MaxProjectiles drop different shots and would desync.
            ChibiSimState.Mix(ref h, ChibiSimConst.MaxProjectiles);
            // Step-12 program-move constants (TD amendment 6): v11 replays fail the hash check instead of desyncing.
            var pc = ChibiSimConst.ProgramConstants;
            for (int k = 0; k < pc.Length; k++) ChibiSimState.Mix(ref h, pc[k]);
            // Step-13 summon pool sizes and constants (minion-cloud-conversion.md section 3.1).
            var sc = ChibiSimConst.SummonConstants;
            for (int k = 0; k < sc.Length; k++) ChibiSimState.Mix(ref h, sc[k]);
            // Step-14 ult constants (ultimates-conversion.md section 4, TD amendment 8).
            var uc = ChibiSimConst.UltConstants;
            for (int k = 0; k < uc.Length; k++) ChibiSimState.Mix(ref h, uc[k]);
            // Step-15 gesture constants and the class -> ult gesture table.
            var gc = ChibiSimConst.GestureConstants;
            for (int k = 0; k < gc.Length; k++) ChibiSimState.Mix(ref h, gc[k]);
            var ug = ChibiSimConst.UltGesture;
            for (int k = 0; k < ug.Length; k++) ChibiSimState.Mix(ref h, ug[k]);
            // Step-16 match flow.
            ChibiSimState.Mix(ref h, Intro ? 1 : 0);
            ChibiSimState.Mix(ref h, Training ? 1 : 0);
            ChibiSimState.Mix(ref h, ChibiSimConst.IntroTicks);
            ChibiSimState.Mix(ref h, ChibiSimConst.MatchTicks);
            // Rules v18: every moveset row, the input resolution table and the moveset constants.
            ChibiMovesetData.MixHash(ref h);
            // Rules v22: attack buffer and walk-cancel constants.
            var fc = ChibiSimConst.FeelConstants;
            for (int k = 0; k < fc.Length; k++) ChibiSimState.Mix(ref h, fc[k]);
            // Rules v20: percent / knockback / stock constants, the hazards switch, the selected stage's geometry and the
            // hazard constants.
            var pk = ChibiPercentConst.Constants;
            for (int k = 0; k < pk.Length; k++) ChibiSimState.Mix(ref h, pk[k]);
            ChibiSimState.Mix(ref h, HazardsOff ? 1 : 0);
            ChibiSimulation.MixStageHash(ref h, ChibiStageTable.Get(Stage));
            MixPlayers(ref h);   // four players: no-op for every 1v1 config (ChibiSimulation.Players.cs)
            return h;
        }
    }

    // ====================================================================================== move data

    /// <summary>
    /// Frame data for one attack. Slots: 0 light, 1 heavy, 2-4 skills, 5 ultimate, 6 Pose Fireball lock (v15),
    /// 7+ moveset rows generated from design/gdd/movesets/movesets.csv (v18, <see cref="ChibiMovesetData"/>).
    /// </summary>
    public struct ChibiMove
    {
        public int Startup, Active, Recovery;       // ticks; startup+recovery scale with attack speed (skills; normals use BaseMs)
        public int Mult;                             // damage multiplier, permille of Attack (0 = no damage)
        public int BoxX, BoxW, BoxY, BoxH;           // melee hitbox: BoxX = near edge offset from fighter centre (toward facing)
        /// <summary>Knockback in velocity sub-units (1/VelSub milli per tick), set on the victim as velocity; and hitstun ticks.</summary>
        public int KbX, KbY, Stun;
        /// <summary>Normals only (slots 0/1): base duration in ms (240 / 420); Startup/Recovery are derived at StartMove.</summary>
        public int BaseMs;
        /// <summary>Light finisher knockback (sub-units) and stun ticks (Fighter.cs:471-472; not light x1.5).</summary>
        public int FinKbX, FinKbY, FinStun;
        /// <summary>Projectile spawn offset from the feet, milli (X toward Facing): arrow 500/725, bolt 600/925, skills 450/700.</summary>
        public int ProjOffX, ProjOffY;
        public int SelfVx, SelfVy;                   // self motion (milli/tick; x VelSub when applied); SelfVx applies through startup+active, SelfVy once at tick 0
        /// <summary>
        /// Self X speed (milli/tick, x VelSub, toward Facing) written once, with Vy = 0, on the first move tick after the
        /// SelfVx window (t == startup + active). 0 = none. Dash &amp; Thrust: 3 u/s (dash-conversion.md section 4).
        /// </summary>
        public int EndVx;
        /// <summary>&gt;0 turns the hit into a projectile: speed in velocity sub-units per tick, life in moves.</summary>
        public int ProjSpeed, ProjLife;
        /// <summary>
        /// Projectile kind, hit radius (milli), projectile flags (<see cref="ChibiProjectileSim"/> F* bits), return delay
        /// (ticks, 0 = none) and return mult (permille before Patience) — projectile-conversion.md sections 1 and 6.
        /// </summary>
        public ChibiProjKind ProjKind;
        public int ProjR, ProjFlags, ProjReturnTicks, ProjReturnMult;
        public int Rehit;                            // >0: re-arm the hit (or fire another projectile) every N active ticks
        public int Cooldown;                         // ticks (skills only)
        public int Flags;
        /// <summary>
        /// Rules v18: moveset mechanics of a CSV row (slots &gt;= 7, generated, see Movesets/ChibiMoveExt.cs); null for
        /// slots 0-6. Constant data, shared by reference, never mutated.
        /// </summary>
        public ChibiMoveExt Ext;

        public bool Exists => Active > 0;
        public const int FParry = 1, FUnblockable = 2, FCentered = 4, FAura = 8, FRing = 16, FHoming = 32,
                         FSpinGuard = 64, FTargeted = 128;
        /// <summary>Rules v13: the move's first active frame summons the Mage minions / the Thunder Cloud (no hitbox).</summary>
        public const int FSummonMinions = 512, FSummonCloud = 1024;
        /// <summary>The SelfVx window also forces Vy = 0 every tick, like the original's (vx, 0) writes (Fighter.cs:760).</summary>
        public const int FLevel = 256;
    }

    public static class ChibiMoveTable
    {
        static readonly ChibiMove[][] Table = Build();

        public static ChibiMove Get(int cls, int slot) => Table[cls][slot];

        /// <summary>Number of slots of class <paramref name="cls"/> (7 base slots + its moveset rows, rules v18).</summary>
        public static int Count(int cls) => Table[cls].Length;

        static ChibiMove Mv(int su = 0, int ac = 0, int re = 0, int mult = 0, int bx = 0, int bw = 0, int by = 0, int bh = 0,
                            int kx = 0, int ky = 0, int stun = 0, int vx = 0, int vy = 0, int ps = 0, int pl = 0,
                            int rehit = 0, int cd = 0, int flags = 0, int ev = 0)
        {
            return new ChibiMove
            {
                Startup = su, Active = ac, Recovery = re, Mult = mult, BoxX = bx, BoxW = bw, BoxY = by, BoxH = bh,
                // Skill rows are authored in milli/tick; stored in sub-units (x VelSub), behaviour unchanged.
                KbX = kx * ChibiSimConst.VelSub, KbY = ky * ChibiSimConst.VelSub, Stun = stun, SelfVx = vx, SelfVy = vy,
                ProjSpeed = ps * ChibiSimConst.VelSub, ProjLife = pl,
                ProjOffX = 450, ProjOffY = ChibiSimConst.ProjectileY,
                Rehit = rehit, Cooldown = cd, Flags = flags, EndVx = ev
            };
        }

        /// <summary>
        /// Normal attack row (normals-conversion.md section 2): Active 1, timing from <paramref name="ms"/>, kb in sub-units,
        /// projectile speed in sub-units per tick, offsets feet-relative milli.
        /// </summary>
        static ChibiMove Nm(int ms, int mult, int bx = 0, int bw = 0, int by = 0, int bh = 0, int kx = 0, int ky = 0, int stun = 0,
                            int fkx = 0, int fky = 0, int fstun = 0, int ps = 0, int pl = 0, int ox = 0, int oy = 0,
                            ChibiProjKind kind = ChibiProjKind.Arrow, int r = 0)
        {
            return new ChibiMove
            {
                BaseMs = ms, Active = 1, Mult = mult, BoxX = bx, BoxW = bw, BoxY = by, BoxH = bh,
                KbX = kx, KbY = ky, Stun = stun, FinKbX = fkx, FinKbY = fky, FinStun = fstun,
                ProjSpeed = ps, ProjLife = pl, ProjOffX = ox, ProjOffY = oy,
                ProjKind = kind, ProjR = r, ProjFlags = ps > 0 ? ChibiProjectileSim.FDestructible : 0
            };
        }

        /// <summary>
        /// Overrides the projectile of a skill row with exact sim units (projectile-conversion.md section 1 rows 6-9):
        /// offset milli, speed and kb in sub-units, life in updates, radius milli, flags, return delay and mult.
        /// </summary>
        static ChibiMove Shot(ChibiMove m, ChibiProjKind kind, int r, int flags, int ox, int oy, int ps, int pl,
                              int kx, int ky, int stun, int retTicks = 0, int retMult = 0)
        {
            m.ProjKind = kind; m.ProjR = r; m.ProjFlags = flags; m.ProjOffX = ox; m.ProjOffY = oy;
            m.ProjSpeed = ps; m.ProjLife = pl; m.KbX = kx; m.KbY = ky; m.Stun = stun;
            m.ProjReturnTicks = retTicks; m.ProjReturnMult = retMult;
            return m;
        }

        static ChibiMove[][] Build()
        {
            const int L = ChibiMove.FLevel;
            // Normals (normals-conversion.md section 2): kb (2.5, 2) / finisher (7, 7) / heavy (8, 6) u/s x 15000 sub;
            // stun 17 / 27 / 30. Ranged/Magic store kb x0.6 (halved again at hit time). Arrow 18/21 u/s, bolt 14 u/s.
            const int lms = ChibiSimConst.LightBaseMs, hms = ChibiSimConst.HeavyBaseMs;
            const int P = ChibiMove.FParry, U = ChibiMove.FUnblockable, C = ChibiMove.FCentered, G = ChibiMove.FSpinGuard,
                      SM = ChibiMove.FSummonMinions, SC = ChibiMove.FSummonCloud;
            // Projectile flags and kinds (projectile-conversion.md section 6).
            const int PH = ChibiProjectileSim.FHoming, PD = ChibiProjectileSim.FDestructible;
            var t = new ChibiMove[5][];

            // Melee normals match Fighter.NormalStrike: MeleeBox(Def.reach * (heavy ? 1.15 : 1), 1.3) spans from the
            // fighter's centre to reach + 0.3 ahead, 1.3 tall around the body centre (0.675 above the feet).
            // Def.reach (ClassData.cs): Melee 1.35, Heavy 1.55, Polearm 2.0. Timing is ported separately (step 8).
            t[(int)FighterClass.Melee] = new[]
            {
                Nm(lms, 1000, 0, 1650, 25, 1300, 37500, 30000, 17, 105000, 105000, 27),     // light thrust
                Nm(hms, 1800, 0, 1853, 25, 1300, 120000, 90000, 30),                        // heavy slash
                // Dash & Thrust — matches Fighter.DashThrust: hit check from the first frame, every frame of the 0.22 s
                // (13-tick) dash at 18 u/s; whole move locked for 0.4 s (24 ticks); box = MeleeBox(0.8, 1.2) =
                // from the fighter's centre to 1.1 u ahead, 1.2 u tall around the body centre (0.725 above feet).
                // Velocity (Facing·18, 0) every tick of the window, then (Facing·3, 0) once (dash-conversion.md section 4).
                Mv(0, 13, 10, 1600, 0, 1100, 75, 1200, 115, 65, 27, vx: 300, cd: 240, flags: L, ev: 50),   // v22 recovery 11 -> 10
                Mv(3, 21, 6, flags: P, cd: 300),                                             // Parry (reflects damage)
                Mv(4, 36, 10, 550, 200, 1500, 100, 1100, 20, 15, 10, rehit: 6, cd: 420),     // Thrust Barrage (6 pulses)
                Mv(0, 1, 0),                                                                 // ULT Sword Aura: inline in FireUlt (v14, D-U2 = A)
                Mv(0, 1, ChibiSimConst.PoseShotLock - 1),                                    // Pose Fireball actionLock (v15, slot 6)
            };
            t[(int)FighterClass.Heavy] = new[]
            {
                Nm(lms, 1000, 0, 1850, 25, 1300, 37500, 30000, 17, 105000, 105000, 27),
                Nm(hms, 1800, 0, 2083, 25, 1300, 120000, 90000, 30),
                Mv(8, 3, 24, 2200, 150, 1000, 100, 1200, 40, 150, 40, cd: 360, flags: U),    // Grab & smash
                Mv(6, 60, 14, 700, 0, 2400, 0, 1400, 50, 50, 15, rehit: 12, cd: 420, flags: C), // Tornado Spin (5 hits)
                new ChibiMove(),                                                             // (Heavy has only two skills)
                Mv(0, 1, 0),                                                                 // ULT Smash: program PSmash (v14)
                Mv(0, 1, ChibiSimConst.PoseShotLock - 1),                                    // Pose Fireball actionLock (v15, slot 6)
            };
            t[(int)FighterClass.Ranged] = new[]
            {
                // Arrows r 250 (F:536-541): light/finisher 0.6 x 0.08 sprite, heavy 0.9 x 0.14.
                Nm(lms, 1000, kx: 22500, ky: 18000, stun: 17, fkx: 63000, fky: 63000, fstun: 27, ps: 270000, pl: 78, ox: 500, oy: 725,
                   kind: ChibiProjKind.Arrow, r: 250),
                Nm(hms, 1800, kx: 72000, ky: 54000, stun: 30, ps: 315000, pl: 78, ox: 500, oy: 725, kind: ChibiProjKind.ArrowBig, r: 250),
                // Backstep shot = big arrow (F:854, row 6): (500, 725), 21 u/s, life 78, kb (5, 3), stun 0.4. Timing: step 12.
                Shot(Mv(9, 1, 12, 1500, vx: -280, cd: 240), ChibiProjKind.ArrowBig, 250, PD, 500, 725, 315000, 78,
                     75000, 45000, 24),
                Mv(8, 3, 17, 1200, 150, 1050, 100, 1300, 180, 80, 36, cd: 360),              // Bowstring choke (v22 recovery 20 -> 17)
                // Arrow Barrage x5 (F:871, row 7): (500, 675 + yOff), 18 u/s, life 78, kb (1.2, 0.8), stun 0.18. RNG yOff + cadence: step 12.
                Shot(Mv(6, 25, 10, 600, rehit: 5, cd: 420), ChibiProjKind.Arrow, 250, PD, 500, 675, 270000, 78,
                     18000, 12000, 11),
                Mv(0, 1, 0),                                                                 // ULT Thousand Arrows: program PArrows (v14)
                Mv(0, 1, ChibiSimConst.PoseShotLock - 1),                                    // Pose Fireball actionLock (v15, slot 6)
            };
            t[(int)FighterClass.Magic] = new[]
            {
                // Bolts r = size x 0.8 (F:479-484): light/finisher 0.35 -> 280, heavy 0.55 -> 440.
                Nm(lms, 1000, kx: 22500, ky: 18000, stun: 17, fkx: 63000, fky: 63000, fstun: 27, ps: 210000, pl: 72, ox: 600, oy: 925,
                   kind: ChibiProjKind.Bolt, r: 280),
                Nm(hms, 1800, kx: 72000, ky: 54000, stun: 30, ps: 210000, pl: 72, ox: 600, oy: 925, kind: ChibiProjKind.BoltBig, r: 440),
                // Homing Fireball (F:876-884, row 8): (600, 975), 9 u/s, r 380, life 3 s, kb (5, 4), stun 0.4, 2D homing.
                Shot(Mv(12, 1, 11, 1600, cd: 240), ChibiProjKind.Fireball,   // v22 recovery 12 -> 11
                     380, PH | PD, 600, 975, 135000, 180,
                     75000, 60000, 24),
                // Summon Minions (F:886-892, rules v13): no hitbox, frame 0 spawns 3 minions, actionLock 0.4 s = 24, cd 12 s.
                Mv(0, 1, ChibiSimConst.MinionLock - 1, cd: 720, flags: SM),
                // Summon Thunder Cloud (F:894-899, rules v13): no hitbox, frame 0 spawns the cloud, actionLock 0.3 s = 18, cd 10 s.
                Mv(0, 1, ChibiSimConst.CloudLock - 1, cd: 600, flags: SC),
                Mv(0, 1, 0),                                                                 // ULT Fire Ring: inline in FireUlt (v14, D-U2 = A)
                Mv(0, 1, ChibiSimConst.PoseShotLock - 1),                                    // Pose Fireball actionLock (v15, slot 6)
            };
            t[(int)FighterClass.Polearm] = new[]
            {
                Nm(lms, 1000, 0, 2300, 25, 1300, 37500, 30000, 17, 105000, 105000, 27),
                Nm(hms, 1800, 0, 2600, 25, 1300, 120000, 90000, 30),
                Mv(22, 5, 14, 1800, 100, 1900, 0, 1200, 100, 150, 30, vx: 100, vy: 230, cd: 300), // Leap slam
                Mv(2, 70, 4, 300, 0, 3000, 0, 1200, 40, 35, 12, rehit: 18, cd: 360, flags: C | G), // Spin guard
                // Throw spear (F:935-950, row 9): (600, 775), 16 u/s, r 300, life 3 s, kb (5, 3), stun 0.35, !Destructible,
                // returns after 0.45 s at 0.8 (x Patience). Sets SpearOut on a successful spawn.
                Shot(Mv(10, 1, 12, 1400, cd: 360), ChibiProjKind.Spear,   // v22 recovery 14 -> 12
                     300, 0, 600, 775, 240000, 180,
                     75000, 45000, 21, 27, 800),
                Mv(0, 1, 0),                                                                 // ULT Kick: program PKick (v14)
                Mv(0, 1, ChibiSimConst.PoseShotLock - 1),                                    // Pose Fireball actionLock (v15, slot 6)
            };
            return ChibiMovesetData.Extend(t);   // rules v18: CSV moveset rows as slots 7+ (generated)
        }
    }

    // ====================================================================================== state

    /// <summary>Everything about one fighter that can change. Plain ints only, so copy and hash are trivial.</summary>
    public sealed partial class ChibiFighterSim
    {
        public int Class, Passive;
        public ChibiCombatStats Stats;                // derived from Class/Passive; copied for speed, not hashed
        /// <summary>Position in milli-units.</summary>
        public int X, Y;
        /// <summary>Velocity in sub-units (1/<see cref="ChibiSimConst.VelSub"/> milli per tick).</summary>
        public int Vx, Vy;
        /// <summary>Sub-milli position remainders in [0, VelSub), carried between ticks (rollback state).</summary>
        public int XRem, YRem;
        public int Facing;
        public bool Grounded;
        public int AirJumps, Ult, Stun, Invuln;
        // Rules v20: HP is gone from the state; damage percent, stocks and the launch fields live in
        // ChibiSimulation.Percent.cs (PctM, Stocks, OutTicks, HoverTicks, LaunchDecX/Y, LaunchGravP, KosTaken).

        public int MoveSlot, MoveTick, MoveStartup, MoveActive, MoveRecovery, MoveMult, ShotIdx, TargetX;
        /// <summary>1 while the running light is the combo finisher (own kb/stun, reload on its shot), else 0 (rollback state).</summary>
        public int MoveFinisher;
        // MoveHit (hit-once flag) is now MoveHitMask, one bit per victim (ChibiSimulation.Players.cs).
        /// <summary>
        /// Rules v23 (B4-2): the running move's melee hit was LANDED or BLOCKED (not a touch on an invulnerable, hovering or
        /// countered / parried target). The walk cancel's "on hit" reads this; MoveHit stays the hit-once probe flag.
        /// </summary>
        public bool MoveConnected;
        public int ComboIdx, ComboWindow, ComboHits, ComboTimer;

        public bool Shielding;
        public int ShieldTick, ParryTicks, GuardTicks;
        public readonly int[] Cd = new int[3];

        public int SlowTicks, WeakenTicks, SluggishTicks, ArmorBreakTicks;
        public int TailwindTicks, TailwindCd, AuraTicks, RingTicks, SetCd;
        /// <summary>
        /// Ticks since the START of the last normal or skill (Patience, damage-conversion.md section 5): init
        /// <see cref="ChibiSimConst.PatienceInit"/>, +1 at the top of every Step in every state, reset by StartMove slots 0..4.
        /// </summary>
        public int SinceAttackTicks;
        /// <summary>Guard durability in micro-HP (max <see cref="ChibiCombatStats.GuardMaxU"/>); drained by the raw hit.</summary>
        public int GuardU;
        public int GuardBrokenTicks;

        /// <summary>Last REAL tick's raw buttons, for press edges. No input buffer exists (decision D-N1 = A, rules v8).</summary>
        public int PrevInput;
        /// <summary>
        /// Press edges OR-ed over the real ticks since the last game step (rules v10, timescale-conversion.md 8.2). The
        /// next game step reads them as this step's edges and clears them, used or not (never carried to a second step).
        /// </summary>
        public int LatchedEdges;
        /// <summary>
        /// Block (Shield) level OR-ed over the real ticks since the last game step (8.3 item 1); the game step uses
        /// raw | LatchedHeld, then clears it. At full speed it equals this tick's Shield bit.
        /// </summary>
        public int LatchedHeld;

        /// <summary>Dash ticks left (rollback state): set to 8 at start, &gt; 0 while a dash is active, 0 when cancelled.</summary>
        public int DashTicks;
        /// <summary>Dash cooldown ticks left (rollback state); a dash can start when this is 0. Not reset by ring-out.</summary>
        public int DashCd;
        /// <summary>Direction of the current / last dash: -1 left, +1 right (rollback state).</summary>
        public int DashDir;

        /// <summary>
        /// Drop-through ticks left (rollback state, 0..21): while &gt; 0 the platform <see cref="DropPlatform"/> is
        /// ignored for landing and the ground probe. Not cleared by ring-out (stage-conversion.md section 3).
        /// </summary>
        public int DropTicks;
        /// <summary>Index of the platform being dropped through, or -1 when none (rollback state).</summary>
        public int DropPlatform = -1;

        public int HitsLanded, CombosLanded, UltsUsed, Parries;
        /// <summary>Highest ComboHits reached this match (rules v17, MaxCombo F:42, F:636): results screen and quests.</summary>
        public int MaxCombo;
        /// <summary>Rules v24: escape dashes used this match (stats; the view plays the escape cue when it rises).</summary>
        public int EscapeDashes;

        /// <summary>
        /// A thrown spear is in flight (F:455, F:938-949; rules v11): light / heavy presses start nothing. Set only when the
        /// spear spawn succeeds (A1), cleared when that spear is removed for any reason. Ring-out keeps it (A7).
        /// </summary>
        public bool SpearOut;

        /// <summary>
        /// First tick index (MoveTick) on which a new action is accepted (multiphase-conversion.md section 1, rules v12):
        /// MoveSlot becomes -1 at the end of the tick where MoveTick reaches it. Normals / row skills: startup + active +
        /// recovery; program moves: section 2 lock (Leap Slam: 90 until the slam, then slam + 12).
        /// </summary>
        public int MoveLockEnd;
        /// <summary>Program phase (rules v12): Leap 0 waiting / 1 slammed; Grab 0 none or whiff / 1 connected (stays 1 after the release).</summary>
        public int MovePhase;
        // Held / Holding are HeldBy / HoldTarget now (ChibiSimulation.Players.cs): F[v].HeldBy == h <=> F[h].HoldTarget == v.
        /// <summary>Hold path points in feet milli (section 2.2): S = victim at frame 0, T = lift top, L = slam point (frame 18).</summary>
        public int GrabSX, GrabSY, GrabTX, GrabTY, GrabLX, GrabLY;
        /// <summary>
        /// Kick (rules v14, D-U3 = A): in range, the lerp start (feet on the press tick) and target (oX - F·1300, oY);
        /// out of range, From holds the pre-Physics feet of frame 15 for the box. Copied, hashed, cleared in InitFighter.
        /// </summary>
        public int KickFromX, KickFromY, KickToX, KickToY;

        // ---- rules v15: gesture controls (gesture-conversion.md). All rolled back and hashed.
        /// <summary>Ult gesture window real ticks left; &gt; 0 = window open (UltWindow, F:396-433). Opened by the Ult press edge.</summary>
        public int UltWindowTicks;
        /// <summary>Retry wait after a timed-out window, real ticks (ultRetryCd, F:210, F:432); the window cannot open while &gt; 0.</summary>
        public int UltRetryTicks;
        /// <summary>
        /// Consecutive real ticks the current gesture id (bits 13-14) has been held, 1 on its first tick, 0 for None, capped
        /// at <see cref="ChibiSimConst.GestureTicksCap"/>: the sim's StableTime (F:412).
        /// </summary>
        public int GestureTicks;
        /// <summary>The window matched on a real tick (F:417-424); the ult fires at the top of the next game step, then clears.</summary>
        public bool UltFirePending;
        /// <summary>Gesture id whose onset (JustStarted, Pose.cs:190-195) was seen since the last game step; 0 = none. Cleared every step (D-N1).</summary>
        public int LatchedPose;
        /// <summary>Rules v22: buffered attack press edges (<see cref="ChibiSimConst.AttackBufferMask"/> bits), 0 = none.</summary>
        public int BufferedEdges;
        /// <summary>Rules v22: game steps the buffered press stays valid (counted down with the timers; 0 clears it).</summary>
        public int BufferTicks;
        /// <summary>Rules v22: after a walk cancel, game steps until attacks may start again (the cancelled move's lock end).</summary>
        public int AttackLockTicks;
        /// <summary>Super Jump / Pose Fireball cooldowns in game ticks (superJumpCd / poseShotCd, scaled dt, F:208-209).</summary>
        public int SuperJumpCd, PoseShotCd;
        /// <summary>HUD prompt (<see cref="ChibiUltPrompt"/>) and its real-tick timer for Detected / Failed (F:226-230).</summary>
        public int UltPrompt, UltPromptTicks;

        public void CopyFrom(ChibiFighterSim o)
        {
            Class = o.Class; Passive = o.Passive; Stats = o.Stats;
            X = o.X; Y = o.Y; Vx = o.Vx; Vy = o.Vy; XRem = o.XRem; YRem = o.YRem; Facing = o.Facing; Grounded = o.Grounded;
            AirJumps = o.AirJumps; Ult = o.Ult; Stun = o.Stun; Invuln = o.Invuln;
            MoveSlot = o.MoveSlot; MoveTick = o.MoveTick; MoveStartup = o.MoveStartup; MoveActive = o.MoveActive;
            MoveRecovery = o.MoveRecovery; MoveMult = o.MoveMult; MoveFinisher = o.MoveFinisher; ShotIdx = o.ShotIdx;
            TargetX = o.TargetX; MoveConnected = o.MoveConnected;
            ComboIdx = o.ComboIdx; ComboWindow = o.ComboWindow; ComboHits = o.ComboHits; ComboTimer = o.ComboTimer;
            Shielding = o.Shielding; ShieldTick = o.ShieldTick; ParryTicks = o.ParryTicks; GuardTicks = o.GuardTicks;
            Cd[0] = o.Cd[0]; Cd[1] = o.Cd[1]; Cd[2] = o.Cd[2];
            SlowTicks = o.SlowTicks; WeakenTicks = o.WeakenTicks; SluggishTicks = o.SluggishTicks; ArmorBreakTicks = o.ArmorBreakTicks;
            TailwindTicks = o.TailwindTicks; TailwindCd = o.TailwindCd; AuraTicks = o.AuraTicks; RingTicks = o.RingTicks; SinceAttackTicks = o.SinceAttackTicks; SetCd = o.SetCd;
            GuardU = o.GuardU; GuardBrokenTicks = o.GuardBrokenTicks;
            PrevInput = o.PrevInput; LatchedEdges = o.LatchedEdges; LatchedHeld = o.LatchedHeld;
            DashTicks = o.DashTicks; DashCd = o.DashCd; DashDir = o.DashDir;
            DropTicks = o.DropTicks; DropPlatform = o.DropPlatform;
            HitsLanded = o.HitsLanded; CombosLanded = o.CombosLanded; UltsUsed = o.UltsUsed; Parries = o.Parries;
            MaxCombo = o.MaxCombo;
            EscapeDashes = o.EscapeDashes;
            SpearOut = o.SpearOut;
            MoveLockEnd = o.MoveLockEnd; MovePhase = o.MovePhase; CopySeats(o);   // four players: HeldBy, HoldTarget, MoveHitMask, LastHitBy, ProgTarget
            GrabSX = o.GrabSX; GrabSY = o.GrabSY; GrabTX = o.GrabTX; GrabTY = o.GrabTY; GrabLX = o.GrabLX; GrabLY = o.GrabLY;
            KickFromX = o.KickFromX; KickFromY = o.KickFromY; KickToX = o.KickToX; KickToY = o.KickToY;
            UltWindowTicks = o.UltWindowTicks; UltRetryTicks = o.UltRetryTicks; GestureTicks = o.GestureTicks;
            UltFirePending = o.UltFirePending; LatchedPose = o.LatchedPose; SuperJumpCd = o.SuperJumpCd; PoseShotCd = o.PoseShotCd;
            UltPrompt = o.UltPrompt; UltPromptTicks = o.UltPromptTicks;
            BufferedEdges = o.BufferedEdges; BufferTicks = o.BufferTicks; AttackLockTicks = o.AttackLockTicks;   // rules v22
            CopyMoveset(o);   // rules v18 (Movesets/ChibiMovesetState.cs)
            CopyPercent(o);   // rules v20 (ChibiSimulation.Percent.cs)
        }

        public void Hash(ref uint h)
        {
            ChibiSimState.Mix(ref h, Class); ChibiSimState.Mix(ref h, Passive);
            ChibiSimState.Mix(ref h, X); ChibiSimState.Mix(ref h, Y); ChibiSimState.Mix(ref h, Vx); ChibiSimState.Mix(ref h, Vy);
            ChibiSimState.Mix(ref h, XRem); ChibiSimState.Mix(ref h, YRem);
            ChibiSimState.Mix(ref h, Facing); ChibiSimState.Mix(ref h, Grounded ? 1 : 0);
            ChibiSimState.Mix(ref h, AirJumps); ChibiSimState.Mix(ref h, Ult);
            ChibiSimState.Mix(ref h, Stun); ChibiSimState.Mix(ref h, Invuln);
            ChibiSimState.Mix(ref h, MoveSlot); ChibiSimState.Mix(ref h, MoveTick); ChibiSimState.Mix(ref h, MoveStartup);
            ChibiSimState.Mix(ref h, MoveActive); ChibiSimState.Mix(ref h, MoveRecovery); ChibiSimState.Mix(ref h, MoveMult);
            ChibiSimState.Mix(ref h, MoveFinisher); ChibiSimState.Mix(ref h, ShotIdx); ChibiSimState.Mix(ref h, TargetX);
            ChibiSimState.Mix(ref h, MoveHit ? 1 : 0); ChibiSimState.Mix(ref h, MoveConnected ? 1 : 0);   // v23
            ChibiSimState.Mix(ref h, ComboIdx); ChibiSimState.Mix(ref h, ComboWindow);
            ChibiSimState.Mix(ref h, ComboHits); ChibiSimState.Mix(ref h, ComboTimer);
            ChibiSimState.Mix(ref h, Shielding ? 1 : 0); ChibiSimState.Mix(ref h, ShieldTick);
            ChibiSimState.Mix(ref h, ParryTicks); ChibiSimState.Mix(ref h, GuardTicks);
            ChibiSimState.Mix(ref h, Cd[0]); ChibiSimState.Mix(ref h, Cd[1]); ChibiSimState.Mix(ref h, Cd[2]);
            ChibiSimState.Mix(ref h, SlowTicks); ChibiSimState.Mix(ref h, WeakenTicks);
            ChibiSimState.Mix(ref h, SluggishTicks); ChibiSimState.Mix(ref h, ArmorBreakTicks);
            ChibiSimState.Mix(ref h, TailwindTicks); ChibiSimState.Mix(ref h, TailwindCd);
            ChibiSimState.Mix(ref h, AuraTicks); ChibiSimState.Mix(ref h, RingTicks); ChibiSimState.Mix(ref h, SinceAttackTicks);
            ChibiSimState.Mix(ref h, SetCd); ChibiSimState.Mix(ref h, GuardU); ChibiSimState.Mix(ref h, GuardBrokenTicks);
            ChibiSimState.Mix(ref h, PrevInput);
            ChibiSimState.Mix(ref h, HitsLanded); ChibiSimState.Mix(ref h, CombosLanded);
            ChibiSimState.Mix(ref h, UltsUsed); ChibiSimState.Mix(ref h, Parries);
            ChibiSimState.Mix(ref h, DashTicks); ChibiSimState.Mix(ref h, DashCd); ChibiSimState.Mix(ref h, DashDir);
            ChibiSimState.Mix(ref h, DropTicks); ChibiSimState.Mix(ref h, DropPlatform);
            ChibiSimState.Mix(ref h, LatchedEdges); ChibiSimState.Mix(ref h, LatchedHeld);
            ChibiSimState.Mix(ref h, SpearOut ? 1 : 0);
            // Rules v12 (multiphase-conversion.md section 6), in this order after SpearOut.
            ChibiSimState.Mix(ref h, MoveLockEnd); ChibiSimState.Mix(ref h, MovePhase);
            ChibiSimState.Mix(ref h, Held ? 1 : 0); ChibiSimState.Mix(ref h, Holding ? 1 : 0);
            ChibiSimState.Mix(ref h, GrabSX); ChibiSimState.Mix(ref h, GrabSY); ChibiSimState.Mix(ref h, GrabTX);
            ChibiSimState.Mix(ref h, GrabTY); ChibiSimState.Mix(ref h, GrabLX); ChibiSimState.Mix(ref h, GrabLY);
            ChibiSimState.Mix(ref h, KickFromX); ChibiSimState.Mix(ref h, KickFromY); ChibiSimState.Mix(ref h, KickToX); ChibiSimState.Mix(ref h, KickToY);
            // Rules v15 (gesture-conversion.md), after the Kick fields.
            ChibiSimState.Mix(ref h, UltWindowTicks); ChibiSimState.Mix(ref h, UltRetryTicks); ChibiSimState.Mix(ref h, GestureTicks);
            ChibiSimState.Mix(ref h, UltFirePending ? 1 : 0); ChibiSimState.Mix(ref h, LatchedPose);
            ChibiSimState.Mix(ref h, SuperJumpCd); ChibiSimState.Mix(ref h, PoseShotCd);
            ChibiSimState.Mix(ref h, UltPrompt); ChibiSimState.Mix(ref h, UltPromptTicks);
            ChibiSimState.Mix(ref h, MaxCombo);  // rules v17
            ChibiSimState.Mix(ref h, EscapeDashes);  // rules v24
            ChibiSimState.Mix(ref h, BufferedEdges); ChibiSimState.Mix(ref h, BufferTicks); ChibiSimState.Mix(ref h, AttackLockTicks);   // rules v22
            HashMoveset(ref h);                  // rules v18
            HashPercent(ref h);                  // rules v20
        }
    }

    /// <summary>
    /// Projectile kind (projectile-conversion.md section 6): selects the view sprite and the two behaviour hooks
    /// (Spear: OnGone clears SpearOut; VolleyLead: OnExpire splits). Everything else is data on the projectile.
    /// </summary>
    public enum ChibiProjKind
    {
        Arrow, ArrowBig, Bolt, BoltBig, AuraWave, Fireball, Spear, SmashWave, VolleyLead, VolleyArrow, PoseFireball,
        /// <summary>Rules v18 moveset projectiles (README 5 `pk:`), appended so the older values keep their numbers.</summary>
        WindBlade, GroundWave, Grapple, LanceArrow,
    }

    /// <summary>One projectile pool slot (CombatObjects.Projectile, rules v11). Plain ints only; copied and hashed whole.</summary>
    public sealed partial class ChibiProjectileSim
    {
        /// <summary><see cref="Flags"/> bits (section 6).</summary>
        public const int FHoming = 1, FPierce = 2, FHarmless = 4, FDestructible = 8, FReturning = 16, FHasHit = 32;

        public bool Active;
        /// <summary>X/Y in milli; Vx/Vy and KbX/KbY in velocity sub-units (1/VelSub milli per tick).</summary>
        public int Owner, X, Y, Vx, Vy, Life, Mult, KbX, KbY, Stun;
        /// <summary>Game tick (<see cref="ChibiSimState.GameTicks"/>, not Frame) the projectile was spawned on; it does not move or hit until the next game step (rollback state).</summary>
        public int SpawnFrame;
        /// <summary>Sub-milli position remainders in [0, VelSub) (rollback state).</summary>
        public int XRem, YRem;
        /// <summary>Projectile kind (view sprite + Spear / VolleyLead hooks).</summary>
        public ChibiProjKind Kind;
        /// <summary>Bitmask of FHoming / FPierce / FHarmless / FDestructible / FReturning / FHasHit.</summary>
        public int Flags;
        /// <summary>Current hit radius, milli (grows by <see cref="GrowPerTick"/> each update).</summary>
        public int Radius;
        /// <summary>Radius growth per update, milli (Smash wave 14 = 600 x 1.4 / 60; else 0).</summary>
        public int GrowPerTick;
        /// <summary>Homing speed magnitude in sub-units = ISqrt(Vx² + Vy²) at spawn.</summary>
        public int Speed;
        /// <summary>&gt; 0: updates until the return starts; 0 = no return pending.</summary>
        public int ReturnTicks;
        /// <summary>Mult (permille) once the return starts.</summary>
        public int ReturnMult;

        public void CopyFrom(ChibiProjectileSim o)
        {
            Active = o.Active; Owner = o.Owner; X = o.X; Y = o.Y; Vx = o.Vx; Vy = o.Vy; XRem = o.XRem; YRem = o.YRem; Life = o.Life;
            Mult = o.Mult; KbX = o.KbX; KbY = o.KbY; Stun = o.Stun; SpawnFrame = o.SpawnFrame;
            Kind = o.Kind; Flags = o.Flags; Radius = o.Radius; GrowPerTick = o.GrowPerTick; Speed = o.Speed;
            ReturnTicks = o.ReturnTicks; ReturnMult = o.ReturnMult;
            HitMask = o.HitMask;   // four players
            CopyMoveset(o);   // rules v18
        }
    }

    /// <summary>
    /// One Mage minion pool slot (CombatObjects.Minion, rules v13, minion-cloud-conversion.md section 1). X/Y = circle
    /// centre in milli, Vx/Vy in velocity sub-units, XRem/YRem in [0, VelSub). Spawn writes every field (TD amendment 7);
    /// Active is always hashed, the rest only while Active (same as projectiles).
    /// </summary>
    public sealed class ChibiMinionSim
    {
        public bool Active;
        public int Owner, X, Y, XRem, YRem, Vx, Vy, Life, AttackCd;
        /// <summary>+1 / -1 toward the opponent (view flip only).</summary>
        public int Facing;
        /// <summary>GameTicks of the spawning step (same convention as ChibiProjectileSim.SpawnFrame): first update next step.</summary>
        public int SpawnTick;

        public void CopyFrom(ChibiMinionSim o)
        {
            Active = o.Active; Owner = o.Owner; X = o.X; Y = o.Y; XRem = o.XRem; YRem = o.YRem; Vx = o.Vx; Vy = o.Vy;
            Life = o.Life; AttackCd = o.AttackCd; Facing = o.Facing; SpawnTick = o.SpawnTick;
        }
    }

    /// <summary>
    /// One Thunder Cloud pool slot (CombatObjects.ThunderCloud, rules v13, section 2). Position in micro-units
    /// (1/1000 milli, D-C5 = A); milli X/Y are derived with floor division (<see cref="MilliX"/>, <see cref="MilliY"/>).
    /// </summary>
    public sealed class ChibiCloudSim
    {
        public bool Active;
        public int Owner, CX, CY, Life, StrikeTicks;
        /// <summary>Bolt view timer: 9 on a strike tick, counts down (TD amendment 6: the view draws while 0 &lt; BoltTicks &lt; 9).</summary>
        public int BoltTicks;
        /// <summary>GameTicks of the spawning step (same convention as ChibiProjectileSim.SpawnFrame): first update next step.</summary>
        public int SpawnTick;

        /// <summary>Cloud X in milli = floor(CX / 1000).</summary>
        public int MilliX => ChibiSimulation.FloorDiv1000(CX);
        /// <summary>Cloud Y in milli = floor(CY / 1000).</summary>
        public int MilliY => ChibiSimulation.FloorDiv1000(CY);

        public void CopyFrom(ChibiCloudSim o)
        {
            Active = o.Active; Owner = o.Owner; CX = o.CX; CY = o.CY; Life = o.Life; StrikeTicks = o.StrikeTicks;
            BoltTicks = o.BoltTicks; SpawnTick = o.SpawnTick;
        }
    }

    /// <summary>
    /// The complete rollback state. Rolling back is "CopyFrom(snapshot)"; there is no hidden state anywhere else
    /// (no statics, no Unity objects, no floats, no UnityEngine.Random), which is what makes both peers agree.
    /// </summary>
    public sealed partial class ChibiSimState
    {
        public int Frame;
        public ChibiRng Rng;
        public bool MatchOver;
        public int Winner = -1;                  // 0 / 1 / -1 draw (valid once MatchOver)
        /// <summary>Normalized stage index (0..2) copied from the config at Init; selects <see cref="ChibiStageTable"/> geometry.</summary>
        public int Stage;

        // ---- time scale (rules v10, timescale-conversion.md sections 5 and 8). All rolled back and hashed.
        /// <summary>Hitstop real ticks left (scale 50 while &gt; 0). Stacks as max (MC:177).</summary>
        public int HitstopRealTicks;
        /// <summary>Ult cinematic real ticks left (scale 200 while &gt; 0). Counts down independently of hitstop.</summary>
        public int CinematicRealTicks;
        /// <summary>Fighter whose ult started the running cinematic, -1 when none (camera focus in the view).</summary>
        public int CinematicFighter = -1;
        /// <summary>Game-step accumulator in permille, 0..999 after each real tick; a game step costs 1000.</summary>
        public int TimeAccum;
        /// <summary>Game steps run so far: the gameplay clock (match timer, projectile SpawnFrame). Frame counts real ticks.</summary>
        public int GameTicks;
        /// <summary>Real ticks since GameTicks last changed (0 on a tick that ran a game step). For view interpolation (8.3 item 2).</summary>
        public int RealTicksSinceStep;
        /// <summary>Shots dropped because the pool was full (A4). Rolled back and hashed; tests assert it stays 0.</summary>
        public int DroppedShots;
        /// <summary>Minion / cloud spawns dropped because the pool was full (rules v13). Rolled back and hashed; tests assert 0.</summary>
        public int DroppedSummons;

        // ---- rules v16: match flow (match-flow-conversion.md). Rolled back and hashed.
        /// <summary>Intro real ticks left (0 = running). Input is off while &gt; 0; decremented at the END of each real tick.</summary>
        public int IntroRealTicks;
        /// <summary>Game steps run while the match was running: the match timer (MC:135 counts scaled dt only while Running).</summary>
        public int MatchClock;
        /// <summary>Training mode copied from the config: no timer, KO revives.</summary>
        public bool Training;

        /// <summary>True once the intro is over and the match has not ended (MatchController.Running, MC:35).</summary>
        public bool Running => IntroRealTicks == 0 && !MatchOver;
        /// <summary>Game ticks left on the match timer (0 in training, where the timer is off).</summary>
        public int TimeLeftTicks => Training ? 0 : Math.Max(0, ChibiSimConst.MatchTicks - MatchClock);

        /// <summary>Fighter slots: always <see cref="MaxFighters"/>; the first <see cref="PlayerCount"/> play.</summary>
        public readonly ChibiFighterSim[] F = new ChibiFighterSim[MaxFighters];
        public readonly ChibiProjectileSim[] Proj = new ChibiProjectileSim[ChibiSimConst.MaxProjectiles];
        /// <summary>Mage minion pool (rules v13), updated in slot order after the projectiles (D-C3 = A).</summary>
        public readonly ChibiMinionSim[] Minions = new ChibiMinionSim[ChibiSimConst.MaxMinions];
        /// <summary>Thunder Cloud pool (rules v13), updated in slot order after the minions (D-C3 = A).</summary>
        public readonly ChibiCloudSim[] Clouds = new ChibiCloudSim[ChibiSimConst.MaxClouds];

        public ChibiSimState()
        {
            for (int i = 0; i < F.Length; i++) F[i] = new ChibiFighterSim();
            for (int i = 0; i < Proj.Length; i++) Proj[i] = new ChibiProjectileSim();
            for (int i = 0; i < Minions.Length; i++) Minions[i] = new ChibiMinionSim();
            for (int i = 0; i < Clouds.Length; i++) Clouds[i] = new ChibiCloudSim();
        }

        public void CopyFrom(ChibiSimState o)
        {
            Frame = o.Frame; Rng = o.Rng; MatchOver = o.MatchOver; Winner = o.Winner; Stage = o.Stage;
            HitstopRealTicks = o.HitstopRealTicks; CinematicRealTicks = o.CinematicRealTicks; CinematicFighter = o.CinematicFighter;
            TimeAccum = o.TimeAccum; GameTicks = o.GameTicks; RealTicksSinceStep = o.RealTicksSinceStep;
            DroppedShots = o.DroppedShots;
            for (int i = 0; i < F.Length; i++) F[i].CopyFrom(o.F[i]);   // all 4 slots
            CopyPlayers(o);   // four players
            for (int i = 0; i < Proj.Length; i++) Proj[i].CopyFrom(o.Proj[i]);
            DroppedSummons = o.DroppedSummons;
            for (int i = 0; i < Minions.Length; i++) Minions[i].CopyFrom(o.Minions[i]);
            for (int i = 0; i < Clouds.Length; i++) Clouds[i].CopyFrom(o.Clouds[i]);
            IntroRealTicks = o.IntroRealTicks; MatchClock = o.MatchClock; Training = o.Training;
            CopyMoveset(o);   // rules v18 zones
            CopyHazards(o);   // rules v20 stage events
        }

        public static void Mix(ref uint h, int v)
        {
            unchecked
            {
                h ^= (uint)v;
                h *= 16777619u;
                h ^= h >> 15;
            }
        }

        /// <summary>FNV-style hash of the entire state. Compared between peers to detect desyncs.</summary>
        public uint ComputeChecksum()
        {
            uint h = 2166136261u;
            Mix(ref h, Frame); Mix(ref h, (int)Rng.State); Mix(ref h, MatchOver ? 1 : 0); Mix(ref h, Winner); Mix(ref h, Stage);
            Mix(ref h, HitstopRealTicks); Mix(ref h, CinematicRealTicks); Mix(ref h, CinematicFighter);
            Mix(ref h, TimeAccum); Mix(ref h, GameTicks); Mix(ref h, RealTicksSinceStep);
            Mix(ref h, DroppedShots);
            F[0].Hash(ref h); F[1].Hash(ref h);
            for (int i = 0; i < Proj.Length; i++)
            {
                var p = Proj[i];
                Mix(ref h, p.Active ? 1 : 0);
                if (!p.Active) continue;
                Mix(ref h, p.Owner); Mix(ref h, p.X); Mix(ref h, p.Y); Mix(ref h, p.Vx); Mix(ref h, p.Vy);
                Mix(ref h, p.XRem); Mix(ref h, p.YRem);
                Mix(ref h, p.Life); Mix(ref h, p.Mult); Mix(ref h, p.KbX); Mix(ref h, p.KbY); Mix(ref h, p.Stun);
                Mix(ref h, p.SpawnFrame);
                Mix(ref h, (int)p.Kind); Mix(ref h, p.Flags); Mix(ref h, p.Radius); Mix(ref h, p.GrowPerTick);
                Mix(ref h, p.Speed); Mix(ref h, p.ReturnTicks); Mix(ref h, p.ReturnMult);
                p.HashMoveset(ref h);   // rules v18
            }
            Mix(ref h, DroppedSummons);
            for (int i = 0; i < Minions.Length; i++)
            {
                var m = Minions[i];
                Mix(ref h, m.Active ? 1 : 0);
                if (!m.Active) continue;
                Mix(ref h, m.Owner); Mix(ref h, m.X); Mix(ref h, m.Y); Mix(ref h, m.XRem); Mix(ref h, m.YRem);
                Mix(ref h, m.Vx); Mix(ref h, m.Vy); Mix(ref h, m.Life); Mix(ref h, m.AttackCd); Mix(ref h, m.Facing);
                Mix(ref h, m.SpawnTick);
            }
            for (int i = 0; i < Clouds.Length; i++)
            {
                var c = Clouds[i];
                Mix(ref h, c.Active ? 1 : 0);
                if (!c.Active) continue;
                Mix(ref h, c.Owner); Mix(ref h, c.CX); Mix(ref h, c.CY); Mix(ref h, c.Life); Mix(ref h, c.StrikeTicks);
                Mix(ref h, c.BoltTicks); Mix(ref h, c.SpawnTick);
            }
            Mix(ref h, IntroRealTicks); Mix(ref h, MatchClock); Mix(ref h, Training ? 1 : 0);   // rules v16
            HashMoveset(ref h);   // rules v18 zones
            HashHazards(ref h);   // rules v20 stage events
            HashPlayers(ref h);   // four players: fighters 2..3 and the new fields; 1v1 mixes nothing new
            return h;
        }
    }

    // ====================================================================================== step function

    /// <summary>
    /// The pure, deterministic game step: (state at frame N, both inputs for frame N) -> state at frame N+1.
    /// Rules that keep it deterministic: integer math only, fixed iteration order, all randomness from state.Rng,
    /// no wall clock, no Unity physics. Melee hits are probed for both fighters before either is applied, so
    /// simultaneous attacks trade instead of favouring player 0.
    /// </summary>
    public static partial class ChibiSimulation
    {
        struct PendingHit
        {
            public bool Valid, Unblockable;
            public int Mult, KbX, KbY, Stun, Dir;
            /// <summary>Rules v18: SrcOther / SrcMelee / SrcProj (counters catch melee, or projectiles with counter:proj).</summary>
            public int Src;
            /// <summary>Rules v20: hit created by an ultimate (slot 5, its programs, projectiles, ring): base + UltBkbBonusM.</summary>
            public bool Ult;
            /// <summary>Rules v20: grounded trip, always set knockback (spec 4.5).</summary>
            public bool Trip;
        }

        public static void Init(ChibiSimState s, in ChibiMatchConfig cfg)
        {
            s.Frame = 0;
            s.Rng = new ChibiRng(cfg.Seed);
            s.MatchOver = false;
            s.Winner = -1;
            s.HitstopRealTicks = 0; s.CinematicRealTicks = 0; s.CinematicFighter = -1;
            s.TimeAccum = 0; s.GameTicks = 0; s.RealTicksSinceStep = 0; s.DroppedShots = 0;
            for (int i = 0; i < s.Proj.Length; i++) s.Proj[i].Active = false;
            s.DroppedSummons = 0;
            for (int i = 0; i < s.Minions.Length; i++) s.Minions[i].Active = false;
            for (int i = 0; i < s.Clouds.Length; i++) s.Clouds[i].Active = false;
            InitZones(s);   // rules v18
            s.Stage = ChibiStageTable.Normalize(cfg.Stage);
            s.IntroRealTicks = cfg.Intro ? ChibiSimConst.IntroTicks : 0;
            s.MatchClock = 0;
            s.Training = cfg.Training;
            InitHazards(s, cfg);   // rules v20: own RNG stream, first delay
            var st = ChibiStageTable.Get(s.Stage);
            // Spawn transform (+-halfWidth*0.45, groundY+1.5) -> feet (+-SpawnX, 775), airborne (stage-conversion.md section 1).
            InitFighter(s.F[0], cfg.Class0, cfg.Passive0, -st.SpawnX, st.SpawnY, 1);
            InitFighter(s.F[1], cfg.Class1, cfg.Passive1, st.SpawnX, st.SpawnY, -1);
            // Four players (Phase 1 placeholder spawns until the level-designer points of Phase 2): fighters 2 and 3 start
            // halfway in, facing the centre. Unused slots are initialised the same way but never stepped or hashed.
            InitPlayers(s, cfg);
            InitFighter(s.F[2], cfg.Class2, cfg.Passive2, -st.SpawnX / 2, st.SpawnY, 1);
            InitFighter(s.F[3], cfg.Class3, cfg.Passive3, st.SpawnX / 2, st.SpawnY, -1);
        }

        static void InitFighter(ChibiFighterSim f, FighterClass cls, int passive, int x, int y, int facing)
        {
            f.Class = (int)cls;
            f.Passive = passive == 0 ? 0 : 1;
            f.Stats = ChibiCombatStats.Create(cls, passive);
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Facing = facing; f.Grounded = false; f.AirJumps = 1;
            f.Ult = 0; f.Stun = 0; f.Invuln = 0;
            f.MoveSlot = -1; f.MoveTick = 0; f.MoveStartup = 0; f.MoveActive = 0; f.MoveRecovery = 0;
            f.MoveMult = 0; f.MoveFinisher = 0; f.ShotIdx = 0; f.TargetX = 0; f.MoveHitMask = 0; f.MoveConnected = false;
            f.ComboIdx = 0; f.ComboWindow = 0; f.ComboHits = 0; f.ComboTimer = 0;
            f.Shielding = false; f.ShieldTick = 0; f.ParryTicks = 0; f.GuardTicks = 0;
            f.Cd[0] = f.Cd[1] = f.Cd[2] = 0;
            f.SlowTicks = f.WeakenTicks = f.SluggishTicks = f.ArmorBreakTicks = 0;
            f.TailwindTicks = 0; f.TailwindCd = 0; f.AuraTicks = 0; f.RingTicks = 0; f.SinceAttackTicks = ChibiSimConst.PatienceInit; f.SetCd = 0;
            f.GuardU = ChibiCombatStats.GuardMaxU(f.Stats.Defense); f.GuardBrokenTicks = 0;
            f.PrevInput = 0; f.LatchedEdges = 0; f.LatchedHeld = 0;
            f.HitsLanded = f.CombosLanded = f.UltsUsed = f.Parries = 0;
            f.MaxCombo = 0;
            f.EscapeDashes = 0;
            f.DashTicks = 0; f.DashCd = 0; f.DashDir = 0;
            f.DropTicks = 0; f.DropPlatform = -1;
            f.SpearOut = false;
            f.MoveLockEnd = 0; f.MovePhase = 0; f.ResetSeats();   // HeldBy / HoldTarget / MoveHitMask / LastHitBy / ProgTarget
            f.GrabSX = f.GrabSY = f.GrabTX = f.GrabTY = f.GrabLX = f.GrabLY = 0;
            f.KickFromX = f.KickFromY = f.KickToX = f.KickToY = 0;
            f.UltWindowTicks = 0; f.UltRetryTicks = 0; f.GestureTicks = 0; f.UltFirePending = false; f.LatchedPose = 0;
            f.SuperJumpCd = 0; f.PoseShotCd = 0; f.UltPrompt = (int)ChibiUltPrompt.Hidden; f.UltPromptTicks = 0;
            f.BufferedEdges = 0; f.BufferTicks = 0; f.AttackLockTicks = 0;   // rules v22
            f.ResetMoveset();   // rules v18
            f.ResetPercent();   // rules v20
        }

        /// <summary>
        /// Current time scale in permille (timescale-conversion.md section 1): 50 during hitstop, else 200 during the ult
        /// cinematic, else 1000 (the minimum of the active factors). Pure read; the view uses it for dt and interpolation.
        /// </summary>
        public static int CurrentScalePermille(ChibiSimState s)
        {
            if (s.HitstopRealTicks > 0) return ChibiSimConst.ScaleHitstop;
            if (s.CinematicRealTicks > 0) return ChibiSimConst.ScaleCinematic;
            return ChibiSimConst.ScaleNormal;
        }

        /// <summary>
        /// One REAL tick (rules v10, timescale-conversion.md sections 5 and 8): latch press edges / Block level, read the
        /// scale, run the unscaled timers, add the scale to TimeAccum and run at most one game step when it reaches 1000.
        /// A slow-mo started inside the game step on real tick k slows k+1 .. k+N (scale read before this tick's step).
        /// </summary>
        public static void Step(ChibiSimState s, ChibiInputPacket in0, ChibiInputPacket in1)
        {
            Span<ChibiInputPacket> inputs = stackalloc ChibiInputPacket[ChibiSimState.MaxFighters];
            inputs[0] = in0;
            inputs[1] = in1;
            Step(s, inputs);
        }

        /// <summary>
        /// One REAL tick for 2..4 fighters (four-player-design.md 1.1): <paramref name="inputs"/>[i] is fighter i's input;
        /// a missing entry is neutral, entries at or above <see cref="ChibiSimState.PlayerCount"/> are ignored. Does not
        /// allocate. The 1v1 overload <see cref="Step(ChibiSimState, ChibiInputPacket, ChibiInputPacket)"/> forwards here.
        /// </summary>
        /// <example><code>ChibiSimulation.Step(state, new[] { p0, p1, p2, p3 });</code></example>
        public static void Step(ChibiSimState s, ReadOnlySpan<ChibiInputPacket> inputs)
        {
            if (s.MatchOver) { s.Frame++; return; }

            // ---- real-tick part
            int n = s.PlayerCount;
            Span<ChibiInputPacket> clean = stackalloc ChibiInputPacket[ChibiSimState.MaxFighters];
            for (int i = 0; i < n; i++)
            {
                clean[i] = i < inputs.Length ? inputs[i].Sanitized() : default(ChibiInputPacket);
                Latch(s.F[i], clean[i].Raw);
            }
            int scale = CurrentScalePermille(s);
            RealTickTimers(s);
            s.TimeAccum += scale;

            // ---- game-step part
            if (s.TimeAccum >= ChibiSimConst.ScaleNormal)
            {
                s.TimeAccum -= ChibiSimConst.ScaleNormal;
                GameStep(s, clean);
                s.GameTicks++;
                s.RealTicksSinceStep = 0;
            }
            else s.RealTicksSinceStep++;
            // Intro (rules v16, MC:122-126): counted at the end of the real tick, so real ticks 0..IntroTicks-1 run with
            // input off and the first tick with input is real tick IntroTicks.
            Dec(ref s.IntroRealTicks);
            s.Frame++;
        }

        /// <summary>
        /// Training "T" key (MC:129-132): fill fighter <paramref name="i"/>'s meter. Offline training only: it is a no-op
        /// unless the match is a running training match. Call it between Steps (an out-of-band command, not an input bit).
        /// </summary>
        /// <returns>True when the meter was filled.</returns>
        public static bool TrainingFillUlt(ChibiSimState s, int i)
        {
            if (!s.Training || !s.Running || i < 0 || i >= s.PlayerCount) return false;
            s.F[i].Ult = ChibiSimConst.UltMax;
            return true;
        }

        /// <summary>Gesture id (0 None, 1 CrossArms, 2 PunchForward, 3 RaiseHands) held in raw input bits 13-14.</summary>
        static int GestureOf(int raw) => (raw & ChibiInputPacket.GestureMask) >> ChibiInputPacket.GestureShift;

        /// <summary>
        /// D-N1 latch (section 8.2): OR this real tick's press edges and Block level into the fighter's latch. Rules v15
        /// (gesture-conversion.md section 2): the gesture id is a held input. Its hold count (StableTime) runs per REAL tick
        /// like the original's unscaled tracker; an onset (JustStarted) is latched for the next game step; CrossArms counts
        /// as a held Shield while no ult window is open (F:261). Bit 15 is the Special button (protocol 3), latched like any button.
        /// </summary>
        static void Latch(ChibiFighterSim f, int raw)
        {
            int g = GestureOf(raw);
            if (g != GestureOf(f.PrevInput))
            {
                f.GestureTicks = g == 0 ? 0 : 1;
                if (g != 0) f.LatchedPose = g;
            }
            else if (g != 0 && f.GestureTicks < ChibiSimConst.GestureTicksCap) f.GestureTicks++;

            f.LatchedEdges |= raw & ~f.PrevInput;
            f.LatchedHeld |= raw & (int)ChibiButtons.Shield;
            if (g == (int)Gesture.CrossArms && f.UltWindowTicks == 0) f.LatchedHeld |= (int)ChibiButtons.Shield;
            f.PrevInput = raw;
        }

        /// <summary>
        /// Gameplay input gate (MatchController.InputEnabled, MC:41): actions, poses and the ult fire need it. Pause is
        /// not modelled here: an offline pause simply stops stepping the sim.
        /// </summary>
        static bool InputEnabled(ChibiSimState s) => s.Running;

        /// <summary>
        /// Every unscaled (real-time) timer, decremented once per real tick before the game step (section 8.3 item 4):
        /// hitstop, cinematic, and (rules v15) per fighter the ult retry wait, the HUD prompt and the ult gesture window.
        /// </summary>
        static void RealTickTimers(ChibiSimState s)
        {
            Dec(ref s.HitstopRealTicks);
            Dec(ref s.CinematicRealTicks);
            if (s.CinematicRealTicks == 0) s.CinematicFighter = -1;
            for (int i = 0; i < s.PlayerCount; i++) UltWindowRealTick(s, i);
        }

        /// <summary>
        /// One real tick of the ult gesture flow (F:210, F:226-230, F:408-434), in the original's order: retry wait and
        /// prompt timer (TickTimers), then the window: count down, match = class gesture held &gt;= 9 real ticks, fire
        /// (needs alive + input enabled; NOT free of stun or hold), else time out into Failed + 1.5 s retry wait.
        /// A match on a frozen real tick (hitstop / cinematic) closes the window now and fires at the next game step (D10).
        /// </summary>
        static void UltWindowRealTick(ChibiSimState s, int i)
        {
            var f = s.F[i];
            Dec(ref f.UltRetryTicks);
            if ((f.UltPrompt == (int)ChibiUltPrompt.Detected || f.UltPrompt == (int)ChibiUltPrompt.Failed) && --f.UltPromptTicks <= 0)
            {
                f.UltPrompt = (int)ChibiUltPrompt.Hidden;
                f.UltPromptTicks = 0;
            }
            if (f.UltWindowTicks == 0) return;

            int left = f.UltWindowTicks - 1;
            bool match = GestureOf(f.PrevInput) == ChibiSimConst.UltGesture[f.Class] &&
                         f.GestureTicks >= ChibiSimConst.UltGestureHoldTicks;
            if (match && f.InPlay && InputEnabled(s))
            {
                f.UltWindowTicks = 0;
                f.UltPrompt = (int)ChibiUltPrompt.Detected;
                f.UltPromptTicks = ChibiSimConst.PromptDetectedTicks;
                f.UltFirePending = true;
            }
            else if (left <= 0)
            {
                f.UltWindowTicks = 0;
                f.UltPrompt = (int)ChibiUltPrompt.Failed;
                f.UltPromptTicks = ChibiSimConst.PromptFailedTicks;
                f.UltRetryTicks = ChibiSimConst.UltRetryTicks;
            }
            else f.UltWindowTicks = left;
        }

        /// <summary>
        /// Rules v22 (user decision UL-1, 2026-11-14): the Ult press edge while able to act fires the ultimate on this game
        /// step when the meter is full and the fighter is alive; no gesture window opens (the v15 window stays in state but
        /// nothing opens it any more). Returns true when the caller must start the ult. The gesture prompt stays hidden:
        /// the HUD's "move detected" panel shows the class gesture, which would be wrong for a key press.
        /// </summary>
        static bool TryFireUltPress(ChibiFighterSim f, int edges)
        {
            if ((edges & (int)ChibiButtons.Ultimate) == 0) return false;
            if (f.Ult < ChibiSimConst.UltMax || f.UltFirePending || !f.InPlay) return false;
            f.UltWindowTicks = 0;
            return true;
        }

        /// <summary>
        /// One game step: the v9 body. Each fighter acts on this real tick's raw | LatchedHeld and on LatchedEdges, then
        /// both latches are cleared whether the step used them or not (D-N1: never carried to a second step).
        /// </summary>
        static void GameStep(ChibiSimState s, ReadOnlySpan<ChibiInputPacket> inputs)
        {
            int n = s.PlayerCount;
            CarryRiders(s);   // rules v20: moving platforms carry their riders first (stages README 3.2)
            for (int i = 0; i < n; i++)
            {
                var input = ConsumeLatch(s.F[i], inputs[i], out int e, out int p);
                if (Eliminated(s, i)) continue;   // four players: out of the match for good (never in 1v1)
                UpdateFighter(s, i, input, e, p);
            }

            // Rules v12 (TD amendment 1): grab start, hold path and release run after both fighters' Physics and BEFORE the
            // melee probes, so a grab beats any same-tick strike of the victim (Update runs before coroutines).
            HoldPhase(s);

            // Probe every melee hitbox first, then apply: simultaneous swings trade. Four players (section 1.3): each
            // attacker probes every foe (hit once per victim); applied in attacker, then victim index order. 1v1: probe 0,
            // probe 1, apply 0 -> 1, apply 1 -> 0, exactly as before.
            const int M = ChibiSimState.MaxFighters;
            Span<PendingHit> hits = stackalloc PendingHit[M * M];
            for (int i = 0; i < n; i++) MeleeProbe(s, i, hits.Slice(i * M, M));
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (hits[i * M + j].Valid) ApplyHit(s, i, j, hits[i * M + j]);

            TickFireRings(s);
            UpdateProjectiles(s);
            UpdateZones(s);   // rules v18: moveset zones after the projectiles (README 5)
            // Rules v13, D-C3 = A: summons after the projectiles, each pool in slot order.
            UpdateMinions(s);
            UpdateClouds(s);
            HazardHits(s);      // rules v20: stage event hits after combat (stages README 3.3 tick order)
            HazardAdvance(s);   // then the event machine
            ResolveBoundsAndMatchEnd(s);
        }

        /// <summary>
        /// Builds the step input (raw | LatchedHeld, plus Shield for a CrossArms held with no ult window open, F:261 read
        /// after the window update), returns the latched edges and pose onset, and clears the latches.
        /// </summary>
        static ChibiInputPacket ConsumeLatch(ChibiFighterSim f, ChibiInputPacket input, out int edges, out int pose)
        {
            edges = f.LatchedEdges;
            pose = f.LatchedPose;
            int raw = input.Raw | f.LatchedHeld;
            if (GestureOf(input.Raw) == (int)Gesture.CrossArms && f.UltWindowTicks == 0) raw |= (int)ChibiButtons.Shield;
            var p = new ChibiInputPacket { Raw = (ushort)raw };
            f.LatchedEdges = 0;
            f.LatchedHeld = 0;
            f.LatchedPose = 0;
            return p;
        }

        // ------------------------------------------------------------------ per fighter

        static void Dec(ref int v) { if (v > 0) v--; }
        static int Sign(int v) => v > 0 ? 1 : (v < 0 ? -1 : 0);

        static int Approach(int v, int target, int step)
        {
            if (v < target) { v += step; return v > target ? target : v; }
            v -= step;
            return v < target ? target : v;
        }

        /// <summary>
        /// One game step of one fighter. <paramref name="edges"/> = press edges latched since the last game step (equal to
        /// this tick's edges at full speed); PrevInput is maintained per real tick in <see cref="Latch"/>.
        /// </summary>
        static void UpdateFighter(ChibiSimState s, int i, ChibiInputPacket input, int edges, int pose)
        {
            var f = s.F[i];
            var st = ChibiStageTable.Get(s.Stage);
            // GroundCheck value of this frame (TD amendment 3): Leap Slam reads it, a jump this tick does not change it.
            bool groundedAtStart = f.Grounded;

            // ---- timers
            Dec(ref f.Stun); Dec(ref f.Invuln); Dec(ref f.ComboWindow);
            if (f.Stun == 0) f.ClearLaunch();               // rules v20: the launch fields end with the hitstun (spec 3.3)
            else if (f.LaunchAge > 0 && f.LaunchAge < ChibiPercentConst.LaunchAgeCap) f.LaunchAge++;   // rules v24 (ED-1)
            Dec(ref f.DashTicks); Dec(ref f.DashCd);       // dash timers tick unconditionally (Fighter.cs:205-207)
            // Drop-through ends at the start of tick T+21, before this tick's probe and physics (stage-conversion.md section 3).
            if (f.DropTicks > 0 && --f.DropTicks == 0) f.DropPlatform = -1;
            Dec(ref f.ParryTicks); Dec(ref f.GuardTicks);
            Dec(ref f.SlowTicks); Dec(ref f.WeakenTicks); Dec(ref f.SluggishTicks); Dec(ref f.ArmorBreakTicks);
            Dec(ref f.TailwindTicks); Dec(ref f.AuraTicks); Dec(ref f.RingTicks); Dec(ref f.SetCd); Dec(ref f.GuardBrokenTicks);
            // Guard regen +24 HP/s exact (0.4 HP per tick) while last tick's shield is down and not broken (F:202-203, section 2).
            int guardMax = ChibiCombatStats.GuardMaxU(f.Stats.Defense);
            if (!f.Shielding && f.GuardBrokenTicks == 0 && f.GuardU < guardMax)
                f.GuardU = Math.Min(guardMax, f.GuardU + ChibiCombatStats.GuardRegenPerTickU);
            // Patience clock counts every tick in every state (stun, dash, block included; section 5).
            if (f.SinceAttackTicks < ChibiSimConst.SinceAttackCap) f.SinceAttackTicks++;
            Dec(ref f.Cd[0]); Dec(ref f.Cd[1]); Dec(ref f.Cd[2]);
            MovesetTimers(f);                                // rules v18 technique cooldowns
            Dec(ref f.AttackLockTicks);                      // rules v22 walk-cancel attack lock
            if (f.BufferTicks > 0 && --f.BufferTicks == 0) f.BufferedEdges = 0;   // rules v22 buffer expiry
            if (f.ComboTimer > 0 && --f.ComboTimer == 0) f.ComboHits = 0;
            if (f.Stats.TailwindCooldown > 0)
            {
                if (f.TailwindCd > 0) f.TailwindCd--;
                else { f.TailwindTicks = f.Stats.TailwindDuration; f.TailwindCd = f.Stats.TailwindCooldown; }
            }
            Dec(ref f.SuperJumpCd); Dec(ref f.PoseShotCd);   // scaled dt (F:208-209)

            // Rules v20 (percent-knockback.md 3.4): KO'd fighters wait off-screen, respawned fighters hover until input.
            if (OutAndHover(s, i, input, edges)) return;

            // Rules v15: the gesture ult fires here, before every action gate (UpdateUltWindow F:176 runs before canAct,
            // F:179), so it fires while stunned or held; FireUltimate clears the stun (F:958). Frame-0 velocity is written
            // now and the rest of this tick sees it (F:178 reads velocity after the ult started).
            bool ultFired = false;
            if (f.UltFirePending)
            {
                f.UltFirePending = false;
                if (f.InPlay && InputEnabled(s))
                {
                    StartMove(s, i, 5);
                    UltFireVelocity(f);
                    ultFired = true;
                }
            }

            // Held (multiphase-conversion.md 2.2, F:179/190/685-690): kinematic, no input, no Physics; the holder's
            // HoldPhase writes the position. Timers above still tick. Rules v15: an ult fired while held keeps running
            // (the original's coroutines are not stopped by being held), but the held body stays at V 0.
            if (f.Held)
            {
                if (f.MoveSlot >= 0 && !ultFired) AdvanceMove(s, i, false, groundedAtStart);
                f.Shielding = false; f.ShieldTick = 0;
                f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = false;
                return;
            }

            // D-N1 = A (normals-conversion.md section 5, Fighter.cs:318-333): no input buffer. Actions read this tick's
            // press edges in FreeActions only, so a press during a move, dash, hitstun or reload is lost.

            // Doc 5.1 / Fighter.cs:179-189: no x-velocity change at all during hitstun (knockback keeps its speed).
            // The 30 u/s² DisabledFriction branch (grounded, not stunned, cannot act) has no reachable state here:
            // the sim has no intro/pause, and a KO ends the match before the next Step.
            bool level = false;                          // original forces vy = 0 this tick (doubled 50 Hz arc term)
            bool dashing = f.Stun == 0 && f.DashTicks > 0;
            bool acting = f.Stun == 0 && InputEnabled(s);
            bool poseLock = false;
            if (acting)
            {
                // canAct section (F:181-185): poses first, then HandleActions, whose first act is the Ult press (F:266,
                // before the dash / block early returns, so it opens the window while dashing, blocking or attacking).
                poseLock = HandlePoses(s, i, pose);
                if (TryFireUltPress(f, edges))
                {
                    // Rules v22: fire now (same path as the gesture fire above). StartMove(5) interrupts a dash, so the
                    // `dashing` flag computed above must be refreshed or the dash branch would advance ult frame 0 again.
                    StartMove(s, i, 5);
                    UltFireVelocity(f);
                    ultFired = true;
                    dashing = false;
                }
                // Rules v22 walk / turn cancel: ends the move here, so this tick falls through to FreeActions below.
                if (!dashing && !ultFired && !poseLock) TryWalkCancel(s, i, input);
            }

            if (f.Stun > 0)
            {
                // Interrupt() keeps parryUntil / spinGuardUntil (F:670-677): ParryTicks and GuardTicks are NOT cleared (section 8.5).
                // Single release path (TD amendment 4); DashTicks is already 0 here (every stun source interrupts).
                Interrupt(s, i);
                // Rules v24 escape dash (ED-1): halves the launch and ends the hitstun; control returns next tick.
                if (!TryEscapeDash(s, f, edges)) LaunchDecayStep(f);   // rules v20: launch decay (spec 3.3)
            }
            else if (!acting)
            {
                // No input (step 16: intro / match over). F:186-189: grounded and not stunned -> 30 u/s² slowdown.
                f.Shielding = false; f.ShieldTick = 0;
                if (f.Grounded) f.Vx = Approach(f.Vx, 0, ChibiSimConst.DisabledFriction);
            }
            else if (!dashing && (ultFired || poseLock) && f.MoveSlot >= 0)
            {
                // A lock started on this tick by the ult fire or the Pose Fireball (both before HandleActions): the rest of
                // HandleActions runs with actionLock > 0 (F:262 no block, F:286-288 grounded 25 u/s², jump, fast-fall, dash).
                // The move itself first advances next tick (program frame 0 already ran inline).
                f.Shielding = false; f.ShieldTick = 0;
                if (f.Grounded) f.Vx = Approach(f.Vx, 0, ChibiSimConst.MoveFriction);
                JumpAndFastFall(s, f, input.MoveY, edges);
                TryDashStart(f, input, edges);
            }
            else if (dashing)
            {
                // Dash motion (dash-conversion.md section 3, Fighter.cs:268-272): velocity forced to (DashDir·17, 0);
                // no shield, movement, facing, jump, fast-fall, attack or dash start. A running attack keeps its
                // timeline and hits, and its SelfVx/SelfVy writes override the dash (coroutines run after Update).
                f.Vx = f.DashDir * ChibiSimConst.DashSpeedSub;
                f.Vy = 0;
                f.Shielding = false; f.ShieldTick = 0;
                if (f.MoveSlot >= 0 && !poseLock)          // a Pose Fireball lock started this tick advances from next tick
                {
                    int prog = ProgramOf(f), frame = f.MoveTick;
                    AdvanceMove(s, i, true, groundedAtStart);
                    if (prog != PNone) ProgramVelocity(f, prog, frame, input.MoveX);
                }
                level = f.Vy == 0;                       // false only if a move's SelfVy launch overrode the dash
                DashingActions(s, i, input, edges);      // rules v18: Light while dashing = dash attack (README 1.2)
                // Rules v23 (B4-3): other attack presses in the dash's last AttackBufferTicks are buffered and come out when
                // the dash ends (a grounded Light was the dash attack above, or is refused by its own gates).
                if (f.MoveSlot < 0) BufferAttack(f, f.Grounded ? edges & ~(int)ChibiButtons.Light : edges, f.DashTicks);
            }
            else if (f.MoveSlot >= 0)
            {
                int prog = ProgramOf(f), frame = f.MoveTick;
                BufferAttack(f, edges, f.MoveLockEnd - f.MoveTick);   // rules v22, before this tick advances the move
                level = AdvanceMove(s, i, false, groundedAtStart);
                // Doc 5.9 / Fighter.cs:291-307: jump and fast-fall sit outside the actionLock gate.
                JumpAndFastFall(s, f, input.MoveY, edges);
                // Dash start is outside the actionLock gate too (Fighter.cs:309): back-dash keeps Facing.
                TryDashStart(f, input, edges);
                // TD amendment 2: program velocity writes go last (the coroutine runs after Update), so they override a
                // same-tick jump. Row moves (Dash & Thrust FLevel) keep the pre-v12 order inside AdvanceMove.
                if (prog != PNone) ProgramVelocity(f, prog, frame, input.MoveX);
            }
            else
            {
                ApplyAttackBuffer(f, ref input, ref edges);   // rules v22: cancel lock, then replay a buffered press
                FreeActions(s, i, input, edges);
            }

            HazardForces(s, i);                          // rules v20: Bird Lair gust (README 3.3, after FreeActions)
            MovesetPrePhysics(s, i);                     // rules v18: tether pull, written last
            Physics(s, f, level, st);
            MovesetPostPhysics(s, i);                    // rules v18: landing (aironce, dive landing)
        }

        /// <summary>
        /// Starts a dash on a Dash press edge when the cooldown is 0 (dash-conversion.md section 3, Fighter.cs:309-316).
        /// Direction = held MoveX, else Facing. The start tick integrates the normal velocity; motion begins next tick.
        /// </summary>
        static void TryDashStart(ChibiFighterSim f, ChibiInputPacket input, int edges)
        {
            if ((edges & (int)ChibiButtons.Dash) == 0 || f.DashCd > 0) return;
            f.DashDir = input.MoveX != 0 ? input.MoveX : f.Facing;
            f.DashTicks = ChibiSimConst.DashDuration;
            f.DashCd = ChibiSimConst.DashCooldown;
            f.Invuln = Math.Max(f.Invuln, ChibiSimConst.DashInvuln);
        }

        static void FreeActions(ChibiSimState s, int i, ChibiInputPacket input, int edges)
        {
            var f = s.F[i];
            // Doc 5.5 / Fighter.cs:261-277: blocking works in the air too. Only the 60 u/s² slowdown is ground-gated;
            // in the air Vx is frozen, and jump / fast-fall / attacks are skipped (the original returns here).
            if (input.Has(ChibiButtons.Shield) && f.GuardBrokenTicks == 0)
            {
                f.ShieldTick = f.Shielding ? f.ShieldTick + 1 : 0;
                f.Shielding = true;
                if (f.Grounded) f.Vx = Approach(f.Vx, 0, ChibiSimConst.ShieldFriction);
                return;
            }
            f.Shielding = false;
            f.ShieldTick = 0;

            int dirX = input.MoveX, dirY = input.MoveY;
            int speed = ChibiCombatStats.EffectiveMoveSpeed(f.Stats, f.SlowTicks > 0, f.TailwindTicks > 0);
            f.Vx = Approach(f.Vx, dirX * speed, f.Grounded ? ChibiSimConst.GroundAccel : ChibiSimConst.AirAccel);
            if (dirX != 0 && f.Grounded) f.Facing = dirX;   // rules v18 OD-1: facing is locked in the air
            JumpAndFastFall(s, f, dirY, edges);
            TryDashStart(f, input, edges);               // after jump, before attacks; never while shielding (returned above)

            // Priority D-M1 = A extended by rules v18 (design/gdd/movesets/README.md section 1.2), this tick's press edges
            // only (D-N1): Light > Heavy > Special > Skill1 > Skill2 > Skill3, each resolved by direction and ground / air.
            // Neutral Light / Heavy and neutral skills keep the old moves and gates (reload, SpearOut). Rules v15: the Ult
            // press only opens the gesture window (TryOpenUltWindow, before this) and never eats another press.
            ResolveActions(s, i, input, edges);
        }

        /// <summary>
        /// Jump (sets Vy) then fast-fall, in that order (doc section 2 step 1, Fighter.cs:291-307).
        /// Fast-fall: Vy = max(Vy - 45 u/s², -24 u/s) while Down is held, airborne and Vy &lt; 3 u/s.
        /// Drop-through (stage-conversion.md section 3, Fighter.cs:291-297,347-361): Jump with Down held while Grounded
        /// and the probe overlaps a platform consumes the jump and ignores that platform for 21 ticks. On the main stage
        /// no platform is found, so it is a normal ground jump.
        /// </summary>
        static void JumpAndFastFall(ChibiSimState s, ChibiFighterSim f, int dirY, int edges)
        {
            if ((edges & (int)ChibiButtons.Jump) != 0)
            {
                int drop = dirY < 0 && f.Grounded ? ProbePlatform(f, ChibiStageTable.Get(s.Stage), s.GameTicks) : -1;
                if (drop >= 0) { f.DropTicks = ChibiStageTable.DropDuration; f.DropPlatform = drop; }
                else if (f.Grounded) { f.Vy = ChibiSimConst.JumpVel; f.Grounded = false; }
                else if (f.AirJumps > 0) { f.AirJumps--; f.Vy = ChibiSimConst.AirJumpVel; }
            }
            if (dirY < 0 && !f.Grounded && f.Vy < ChibiSimConst.FastFallTriggerVy)
                f.Vy = Math.Max(f.Vy - ChibiSimConst.FastFallAccel, -ChibiSimConst.FastFallCap);
        }

        static void StartMove(ChibiSimState s, int i, int slot)
        {
            var f = s.F[i];
            var mv = ChibiMoveTable.Get(f.Class, slot);
            bool hexed = f.SluggishTicks > 0;
            f.ClearMoveRuntime();   // rules v19 SEC-106: every move start, slots 0-6 and the ult included
            if (slot < FirstNew)
            {
                // Rules v22: the start direction of slots 0-6 too (rows set it in StartNewMove), for the walk cancel.
                var pk = new ChibiInputPacket { Raw = (ushort)f.PrevInput };
                f.MoveDir = PackDir(pk.MoveX, pk.MoveY);
            }
            if (slot == 5)
            {
                // Common ult body (rules v14, section 1, F:956-959): Interrupt() then hitstun = 0, before the class part.
                Interrupt(s, i);
                f.Stun = 0;
            }

            f.MoveSlot = slot; f.MoveTick = 0; f.MoveHitMask = 0; f.MoveConnected = false; f.ShotIdx = 0; f.MoveFinisher = 0;
            f.MoveActive = mv.Active;
            if (slot <= 1)
            {
                // Normals: k_strike / k_lock from baseMs and multiplicative attack speed (normals-conversion.md section 1).
                ChibiCombatStats.NormalFrames(mv.BaseMs, ChibiCombatStats.AttackSpeedPpm(f.Stats, hexed),
                                              out f.MoveStartup, out f.MoveRecovery);
            }
            else
            {
                // Skills 2-4 AND the ult (slot 5) never read attack speed (multiphase-conversion.md fact 7 / rule 6, TD
                // amendment 7): AttackSpeed is read only in DoNormal (F:456). The ult row is a stand-in until step 14; it
                // does not wait for step 14 here (assumed no AttackSpeed in the ult coroutines, section 9).
                f.MoveStartup = mv.Startup;
                f.MoveRecovery = mv.Recovery;
            }
            f.MoveLockEnd = f.MoveStartup + f.MoveActive + f.MoveRecovery;
            f.MovePhase = 0;
            int prog = ProgramOf(f);
            if (prog != PNone)
            {
                // Program move (section 1): lock from section 2; Startup/Active/Recovery only carry the lock for the view.
                f.MoveLockEnd = ProgramLock(prog);
                f.MoveStartup = 0; f.MoveActive = 0; f.MoveRecovery = f.MoveLockEnd;
            }
            // Rules v13 (sections 1.1 / 2.1, TD amendment 8): SummonMinions / SummonCloud run inline in the press handler
            // (F:740-744), so the summons spawn on the press tick from the pre-Physics position read here. The row only
            // carries the actionLock (24 / 18) and the cooldown; it has no hitbox.
            if ((mv.Flags & ChibiMove.FSummonMinions) != 0) SpawnMinions(s, i);
            if ((mv.Flags & ChibiMove.FSummonCloud) != 0) SpawnCloud(s, i);

            int mult = mv.Mult;
            if (slot == 0)
            {
                if (f.ComboWindow <= 0) f.ComboIdx = 0;
                int idx = f.ComboIdx;
                if (idx == f.Stats.PatternLength - 1)                                              // finisher (F:460-461)
                {
                    mult = mult * ChibiSimConst.FinisherMult / 1000;
                    f.MoveFinisher = 1;                     // reload (Ranged/Magic) is set on the shot tick, AdvanceMove
                }
                f.ComboIdx = (idx + 1) % f.Stats.PatternLength;
                f.ComboWindow = ChibiSimConst.ComboWindowTicks;
            }
            else if (slot == 1 || slot >= FirstNew) { f.ComboIdx = 0; f.ComboWindow = 0; }   // v18 rows: like the heavy

            // Patience (section 5): normals and skills only (not the ult); 180 ticks since the last start qualifies, 179 does not.
            if (slot <= 4 || slot >= FirstNew)                // rules v18: moveset rows count as attacks too
            {
                if (f.Stats.PatienceTicks > 0 && f.SinceAttackTicks >= f.Stats.PatienceTicks && mult > 0)
                    mult = mult * (1000 + ChibiCombatStats.PatienceBonusPermille) / 1000;
                f.SinceAttackTicks = 0;
            }

            f.MoveMult = mult;
            if (slot >= FirstNew) StartNewMove(s, i, mv);    // rules v18 bookkeeping (README 1.3)
            if (slot >= 2 && slot <= 4) f.Cd[slot - 2] = mv.Cooldown;
            if (slot == 5)
            {
                // Ult cinematic (FireUltimate F:961 -> Cinematic MC:184-193): assignment, a second ult restarts the window.
                s.CinematicRealTicks = ChibiSimConst.CinematicTicks;
                s.CinematicFighter = i;
                f.Ult = 0; f.UltsUsed++;
                if (f.Invuln < ChibiSimConst.UltCommonInvuln) f.Invuln = ChibiSimConst.UltCommonInvuln;   // F:960 max
                FireUltClass(s, i);
            }
            f.TargetX = s.F[Foe(s, i)].X;
            if (prog != PNone)
            {
                // Frame-0 rule (section 1): frame 0 runs inline on the press tick; its velocity writes are skipped (fact 2).
                ProgramFrame(s, i, prog, 0, f.Grounded);
                f.MoveTick = 1;
            }
        }

        // ------------------------------------------------------------------ program moves (rules v12)

        /// <summary>Program move ids (multiphase-conversion.md section 2): the seven skills whose coroutine is not a row.</summary>
        const int PNone = 0, PThrust = 1, PGrab = 2, PTornado = 3, PBackstep = 4, PBarrage = 5, PLeap = 6, PSpin = 7;
        /// <summary>Ult programs (rules v14, ultimates-conversion.md 2.2, 2.3, 2.5). Sword Aura / Fire Ring have none (D-U2 = A).</summary>
        const int PSmash = 8, PArrows = 9, PKick = 10;

        /// <summary>Kick MovePhase (section 2.5): 1 dash (out of range), 2 lerp, 3 holding, 4 released.</summary>
        const int KickDash = 1, KickLerp = 2, KickHolding = 3, KickReleased = 4;

        /// <summary>
        /// Class part of FireUltimate (rules v14, F:966-979), after the common body. Sword Aura / Fire Ring start on the press
        /// tick with no lock (D-U2 = A). Smash / Arrows / Kick ASSIGN their invuln (F:987, 1013, 1039). The Kick decides its
        /// branch here from the opponent's position as of this fighter's slot in the fighter loop (TD A4).
        /// </summary>
        static void FireUltClass(ChibiSimState s, int i)
        {
            var f = s.F[i];
            switch ((FighterClass)f.Class)
            {
                case FighterClass.Melee: f.AuraTicks = ChibiSimConst.AuraDuration; f.MoveSlot = -1; break;
                case FighterClass.Magic: f.RingTicks = ChibiSimConst.RingDuration; f.MoveSlot = -1; break;
                case FighterClass.Heavy: f.Invuln = ChibiSimConst.SmashInvuln; break;
                case FighterClass.Ranged: f.Invuln = ChibiSimConst.ArrowsInvuln; break;
                case FighterClass.Polearm:
                {
                    f.Invuln = ChibiSimConst.KickInvuln;
                    int oi = Foe(s, i);
                    var o = s.F[oi];
                    // F:1041 strict on Centers (same 0.675 offset, so feet); dead opponent -> dash branch.
                    bool inRange = o.InPlay && Math.Abs(o.X - f.X) < ChibiSimConst.KickRangeX &&
                                   Math.Abs(o.Y - f.Y) < ChibiSimConst.KickRangeY;
                    f.KickFromX = f.X; f.KickFromY = f.Y;
                    if (inRange)
                    {
                        f.Facing = o.X > f.X ? 1 : -1;                                   // SetFacing, tie -> -1
                        f.KickToX = o.X - f.Facing * ChibiSimConst.KickBehind;
                        f.KickToY = o.Y;
                        f.MovePhase = KickLerp;
                        f.ProgTarget = oi;   // four players: the kick holds and hits this fighter
                    }
                    else { f.KickToX = 0; f.KickToY = 0; f.MovePhase = KickDash; }
                    break;
                }
            }
        }

        /// <summary>
        /// Ult frame-0 velocity writes (rules v14 fact 3, applied unlike skills), made at fire time (rules v15). The original
        /// starts the ult coroutine at F:176, before the velocity read at F:178, so the same tick's HandleActions then works
        /// on this velocity: grounded 25 u/s² decel, a jump overrides vy (TD A2), fast-fall (see the lock branch in
        /// UpdateFighter). Grounded is not touched here: GroundCheck already ran, so a same-tick jump is a ground jump.
        /// Sword Aura / Fire Ring and Thousand Arrows write no velocity.
        /// </summary>
        static void UltFireVelocity(ChibiFighterSim f)
        {
            switch ((FighterClass)f.Class)
            {
                case FighterClass.Heavy: f.Vx = 0; f.Vy = ChibiSimConst.SmashJumpVel; break;
                case FighterClass.Polearm:
                    f.Vx = f.MovePhase == KickDash ? f.Facing * ChibiSimConst.KickDashVel : 0;
                    f.Vy = 0;
                    break;
            }
        }

        /// <summary>
        /// HandlePoses (F:371-392) on this step's latched gesture onset, only while able to act and with no ult window open
        /// (a telemetry-invalid camera sends gesture None on the input side). RaiseHands: Super Jump (Vy 19 u/s, air jump
        /// refilled, cd 3 s). PunchForward while free: Pose Fireball (cd 4 s, 0.25 s lock in move slot 6). An onset on
        /// cooldown is lost, like JustStarted. Returns true when the fireball started its lock this tick.
        /// </summary>
        static bool HandlePoses(ChibiSimState s, int i, int pose)
        {
            var f = s.F[i];
            if (pose == 0 || f.UltWindowTicks > 0) return false;
            if (pose == (int)Gesture.RaiseHands)
            {
                if (f.SuperJumpCd > 0) return false;
                f.SuperJumpCd = ChibiSimConst.SuperJumpCd;
                f.Vy = ChibiSimConst.SuperJumpVel;
                f.AirJumps = 1;
                return false;
            }
            if (pose != (int)Gesture.PunchForward || f.PoseShotCd > 0 || f.MoveSlot >= 0) return false;
            f.PoseShotCd = ChibiSimConst.PoseShotCd;
            StartMove(s, i, ChibiSimConst.PoseSlot);
            // actionLock = 0.25 is set on this tick and counted down from the next one (F:204, F:386), so the pose tick
            // is lock frame 0 (like a program's inline frame 0): a press 15 ticks later is accepted.
            f.MoveTick = 1;
            SpawnProjectile(s, new ProjSpec
            {
                Owner = i, Kind = ChibiProjKind.PoseFireball, X = f.X + f.Facing * ChibiSimConst.PoseShotOx,
                Y = f.Y + ChibiSimConst.PoseShotOy, Vx = f.Facing * ChibiSimConst.PoseShotVel, Vy = 0,
                Radius = ChibiSimConst.PoseShotR, Life = ChibiSimConst.PoseShotLife, Mult = ChibiSimConst.PoseShotMult,
                KbX = ChibiSimConst.PoseShotKbX, KbY = ChibiSimConst.PoseShotKbY, Stun = ChibiSimConst.PoseShotStun,
                Flags = ChibiProjectileSim.FDestructible,
            });
            return true;
        }

        /// <summary>Program id of the fighter's running move (by class and slot), or <see cref="PNone"/>.</summary>
        static int ProgramOf(ChibiFighterSim f)
        {
            switch ((FighterClass)f.Class)
            {
                case FighterClass.Melee: return f.MoveSlot == 4 ? PThrust : PNone;
                case FighterClass.Heavy: return f.MoveSlot == 2 ? PGrab : f.MoveSlot == 3 ? PTornado : f.MoveSlot == 5 ? PSmash : PNone;
                case FighterClass.Ranged: return f.MoveSlot == 2 ? PBackstep : f.MoveSlot == 4 ? PBarrage : f.MoveSlot == 5 ? PArrows : PNone;
                case FighterClass.Polearm: return f.MoveSlot == 2 ? PLeap : f.MoveSlot == 3 ? PSpin : f.MoveSlot == 5 ? PKick : PNone;
                default: return PNone;
            }
        }

        /// <summary>Initial MoveLockEnd per program (section 2). Grab starts as a whiff (21); Leap at its timeout bound (78 + 12).</summary>
        static int ProgramLock(int prog)
        {
            switch (prog)
            {
                case PThrust: return ChibiSimConst.ThrustLock;
                case PGrab: return ChibiSimConst.GrabWhiffLock;
                case PTornado: return ChibiSimConst.TornadoLock;
                case PBackstep: return ChibiSimConst.BackstepLock;
                case PBarrage: return ChibiSimConst.BarrageLock;
                case PLeap: return ChibiSimConst.LeapTimeout + ChibiSimConst.LeapAfterSlam;
                case PSmash: return ChibiSimConst.SmashTimeout + ChibiSimConst.SmashAfterSlam;
                case PArrows: return ChibiSimConst.ArrowsLock;
                case PKick: return ChibiSimConst.KickLock;
                default: return ChibiSimConst.SpinLock;
            }
        }

        /// <summary>
        /// Non-velocity events of program frame <paramref name="frame"/> (runs before this tick's Physics, so spawns and
        /// snapshots use the pre-motion position, fact 6). Hits are derived by <see cref="ProgramProbe"/>, the grab check and
        /// hold path by <see cref="HoldPhase"/>, velocity by <see cref="ProgramVelocity"/>.
        /// </summary>
        static void ProgramFrame(ChibiSimState s, int i, int prog, int frame, bool groundedAtStart)
        {
            var f = s.F[i];
            var mv = ChibiMoveTable.Get(f.Class, f.MoveSlot);
            switch (prog)
            {
                case PGrab:
                    if (frame == ChibiSimConst.GrabLiftFrames && f.Holding)
                    {
                        f.GrabLX = f.X + f.Facing * ChibiSimConst.GrabSlamDx;   // L from the holder before its Physics
                        f.GrabLY = f.Y;
                    }
                    break;
                case PBackstep:
                    if (frame == ChibiSimConst.BackstepShotFrame) SpawnRowShot(s, i, mv, 0);
                    break;
                case PBarrage:
                    if (frame % ChibiSimConst.BarragePeriod == 0 && frame < ChibiSimConst.BarragePeriod * ChibiSimConst.BarrageShots)
                    {
                        // Random.Range(-0.25, 0.3) is drawn before SpawnArrow, so even a dropped shot consumes it (2.5).
                        int yOff = ChibiSimConst.BarrageYOffMin + s.Rng.Range(ChibiSimConst.BarrageYOffSpan);
                        SpawnRowShot(s, i, mv, yOff);
                        f.ShotIdx++;
                    }
                    break;
                case PLeap:
                    // t > 0.15 && Grounded (GroundCheck at the frame start) breaks the wait; timeout 1.3 s (2.6, A3).
                    if (f.MovePhase == 0 && frame >= ChibiSimConst.LeapEarliest &&
                        (groundedAtStart || frame >= ChibiSimConst.LeapTimeout))
                    {
                        f.MovePhase = 1;
                        f.MoveLockEnd = frame + ChibiSimConst.LeapAfterSlam;   // actionLock = 0.2 (assignment)
                    }
                    break;
                case PSpin:
                    if (frame == 0) f.GuardTicks = ChibiSimConst.SpinGuardTicks;   // once; survives Interrupt (fact 9)
                    break;
                case PSmash:
                    // F:990: break when t > 0.15 && Grounded (GroundCheck at the frame start), else the 1.1 s loop end.
                    if (f.MovePhase == 0 && frame >= ChibiSimConst.SmashMinFrame &&
                        (groundedAtStart || frame >= ChibiSimConst.SmashTimeout))
                    {
                        f.MovePhase = 1;
                        for (int dir = -1; dir <= 1; dir += 2)                         // F:994: dir -1 then +1
                            SpawnProjectile(s, new ProjSpec
                            {
                                Owner = i, Kind = ChibiProjKind.SmashWave, X = f.X + dir * ChibiSimConst.SmashWaveDx,
                                Y = f.Y + ChibiSimConst.SmashWaveDy, Vx = dir * ChibiSimConst.SmashWaveVel, Vy = 0,
                                Radius = ChibiSimConst.SmashWaveR, GrowPerTick = ChibiSimConst.SmashWaveGrow,
                                Life = ChibiSimConst.SmashWaveLife, Mult = ChibiSimConst.SmashWaveMult,
                                KbX = ChibiSimConst.SmashWaveKbX, KbY = ChibiSimConst.SmashWaveKbY, Stun = ChibiSimConst.SmashWaveStun,
                                Flags = ChibiProjectileSim.FPierce,                     // !Destructible; hit-once via HasHit (TD A5)
                            });
                        f.MoveLockEnd = frame + ChibiSimConst.SmashAfterSlam;          // actionLock = 0.3 (assignment)
                    }
                    break;
                case PArrows:
                    if (frame == 0)                                                    // F:1014 Shoot lead, yield break
                        SpawnProjectile(s, new ProjSpec
                        {
                            Owner = i, Kind = ChibiProjKind.VolleyLead, X = f.X + f.Facing * ChibiSimConst.LeadDx,
                            Y = f.Y + ChibiSimConst.LeadDy, Vx = f.Facing * ChibiSimConst.LeadVx, Vy = ChibiSimConst.LeadVy,
                            Radius = 0, Life = ChibiSimConst.LeadLife, Flags = ChibiProjectileSim.FHarmless,
                        });
                    break;
                case PKick:
                    // Out of range: the frame-15 MeleeBox reads the pre-Physics feet (coroutine before the physics step).
                    if (f.MovePhase == KickDash && frame == ChibiSimConst.KickDashFrames) { f.KickFromX = f.X; f.KickFromY = f.Y; }
                    break;
            }
        }

        /// <summary>Program velocity writes for frame <paramref name="frame"/> &gt;= 1 (frame 0 is skipped, fact 2), after jump/dash (A2).</summary>
        static void ProgramVelocity(ChibiFighterSim f, int prog, int frame, int moveX)
        {
            if (frame <= 0) return;
            switch (prog)
            {
                case PTornado:
                    if (frame < ChibiSimConst.TornadoSteerEnd) f.Vx = moveX * ChibiSimConst.TornadoSteer;   // Vy kept
                    break;
                case PBackstep:
                    if (frame < ChibiSimConst.BackstepShotFrame)
                    { f.Vx = -f.Facing * ChibiSimConst.BackstepVx; f.Vy = ChibiSimConst.BackstepVy; }
                    else if (frame == ChibiSimConst.BackstepShotFrame) f.Vx = -f.Facing * ChibiSimConst.BackstepExitVx;
                    break;
                case PLeap:
                    if (frame == 1) { f.Vx = f.Facing * ChibiSimConst.LeapVx; f.Vy = ChibiSimConst.LeapVy; f.Grounded = false; }
                    break;
                case PKick:
                    if (f.MovePhase == KickDash)
                    {
                        if (frame < ChibiSimConst.KickDashFrames) { f.Vx = f.Facing * ChibiSimConst.KickDashVel; f.Vy = 0; }
                    }
                    else if (f.MovePhase == KickLerp)
                    {
                        if (frame < ChibiSimConst.KickLerpFrames)
                        {
                            // F:1058-1062: pos = Lerp(from, to, k / 9) then V 0; Physics still runs this tick. Remainders
                            // cleared (TD A3). C# division truncates toward 0.
                            f.X = f.KickFromX + (int)((long)(f.KickToX - f.KickFromX) * frame / ChibiSimConst.KickLerpFrames);
                            f.Y = f.KickFromY + (int)((long)(f.KickToY - f.KickFromY) * frame / ChibiSimConst.KickLerpFrames);
                            f.XRem = 0; f.YRem = 0; f.Vx = 0; f.Vy = 0;
                        }
                    }
                    else if (frame == ChibiSimConst.KickRiseFrame)
                    { f.Vx = 0; f.Vy = ChibiSimConst.KickRiseVel; f.Grounded = false; }   // F:1071, sticks
                    break;
            }
        }

        /// <summary>Spawns the row's projectile (Backstep big arrow, Barrage arrow) from the feet + row offset (+ yOff), toward Facing.</summary>
        static void SpawnRowShot(ChibiSimState s, int i, in ChibiMove mv, int yOff)
        {
            var f = s.F[i];
            SpawnProjectile(s, new ProjSpec
            {
                Owner = i, Kind = mv.ProjKind, X = f.X + f.Facing * mv.ProjOffX, Y = f.Y + mv.ProjOffY + yOff,
                Vx = f.Facing * mv.ProjSpeed, Vy = 0, Radius = mv.ProjR, Life = mv.ProjLife, Mult = f.MoveMult,
                KbX = mv.KbX, KbY = mv.KbY, Stun = mv.Stun, Flags = mv.ProjFlags,
            });
        }

        /// <summary>Facing-relative box [x0, x1] from the feet: near edge <paramref name="bx"/>, width <paramref name="bw"/>.</summary>
        static void FacingBox(ChibiFighterSim f, int bx, int bw, out int x0, out int x1)
        {
            if (f.Facing > 0) { x0 = f.X + bx; x1 = x0 + bw; }
            else { x1 = f.X - bx; x0 = x1 - bw; }
        }

        static PendingHit ProgHit(int mult, int kbX, int kbY, int stun, int dir, bool unblockable) =>
            new PendingHit { Valid = true, Mult = mult, KbX = kbX, KbY = kbY, Stun = stun, Dir = dir, Unblockable = unblockable, Src = SrcMelee };

        /// <summary>
        /// Program hit of frame <paramref name="t"/> (pure function of state; section 2). Every listed frame checks again
        /// (no hit-once). Barrage / Leap boxes keep the step-8 strict <see cref="Overlaps"/>; Tornado / Spin Guard use the
        /// inclusive circle from Center with dir = Mathf.Sign(dx), Sign(0) = +1. The grab slam (frame 26, A1) needs no box.
        /// </summary>
        static PendingHit ProgramProbe(ChibiSimState s, int i, int j, int prog, int t)
        {
            var f = s.F[i];
            var o = s.F[j];
            var none = default(PendingHit);
            int x0, x1;
            switch (prog)
            {
                case PThrust:
                {
                    int lastHit = ChibiSimConst.ThrustPeriod * (ChibiSimConst.ThrustHits - 1);
                    if (t % ChibiSimConst.ThrustPeriod != 0 || t > lastHit) return none;
                    FacingBox(f, ChibiSimConst.ThrustBoxX, ChibiSimConst.ThrustBoxW, out x0, out x1);
                    int y0 = f.Y + ChibiSimConst.ThrustBoxY;
                    if (!Overlaps(o, x0, x1, y0, y0 + ChibiSimConst.ThrustBoxH)) return none;
                    bool last = t == lastHit;
                    return ProgHit(f.MoveMult, last ? ChibiSimConst.ThrustLastKbX : ChibiSimConst.ThrustKbX,
                                   last ? ChibiSimConst.ThrustLastKbY : ChibiSimConst.ThrustKbY, ChibiSimConst.ThrustStun, f.Facing, false);
                }
                case PGrab:
                    if (t != ChibiSimConst.GrabReleaseFrame || f.MovePhase != 1 || j != ProgVictim(s, i)) return none;
                    return ProgHit(f.MoveMult, ChibiSimConst.GrabSlamKbX, ChibiSimConst.GrabSlamKbY, ChibiSimConst.GrabSlamStun,
                                   f.Facing, true);
                case PTornado:
                    if (t % ChibiSimConst.TornadoPeriod != 0 || t > ChibiSimConst.TornadoLastHit) return none;
                    if (!CircleHitsFighter(o, f.X, f.Y + ChibiSimConst.CenterOffY, ChibiSimConst.TornadoR)) return none;
                    return ProgHit(f.MoveMult, ChibiSimConst.TornadoKb, ChibiSimConst.TornadoKb, ChibiSimConst.TornadoStun,
                                   o.X >= f.X ? 1 : -1, false);
                case PLeap:
                {
                    if (f.MovePhase != 1 || t != f.MoveLockEnd - ChibiSimConst.LeapAfterSlam) return none;
                    FacingBox(f, ChibiSimConst.LeapBoxX, ChibiSimConst.LeapBoxW, out x0, out x1);
                    int y0 = f.Y + ChibiSimConst.LeapBoxY;
                    if (!Overlaps(o, x0, x1, y0, y0 + ChibiSimConst.LeapBoxH)) return none;
                    return ProgHit(f.MoveMult, ChibiSimConst.LeapKbX, ChibiSimConst.LeapKbY, ChibiSimConst.LeapStun, f.Facing, false);
                }
                case PSpin:
                    if (t % ChibiSimConst.SpinPeriod != 0 || t > ChibiSimConst.SpinLastHit) return none;
                    if (!CircleHitsFighter(o, f.X, f.Y + ChibiSimConst.CenterOffY, ChibiSimConst.SpinR)) return none;
                    return ProgHit(f.MoveMult, ChibiSimConst.SpinKbX, ChibiSimConst.SpinKbY, ChibiSimConst.SpinStun,
                                   o.X >= f.X ? 1 : -1, false);
                case PKick:
                    if (f.MovePhase == KickDash && t == ChibiSimConst.KickDashFrames)
                    {
                        // MeleeBox(2.2, 1.4) (TD A1): near edge 0, width 2500 from the frame-15 pre-Physics feet; height
                        // 1400 around Center; the strict Overlaps of the ported normal MeleeBox.
                        if (f.Facing > 0) { x0 = f.KickFromX + ChibiSimConst.KickBoxBx; x1 = x0 + ChibiSimConst.KickBoxBw; }
                        else { x1 = f.KickFromX - ChibiSimConst.KickBoxBx; x0 = x1 - ChibiSimConst.KickBoxBw; }
                        int y0 = f.KickFromY + ChibiSimConst.CenterOffY - ChibiSimConst.KickBoxH / 2;
                        if (!Overlaps(o, x0, x1, y0, y0 + ChibiSimConst.KickBoxH)) return none;
                        return ProgHit(ChibiSimConst.KickBoxMult, ChibiSimConst.KickBoxKbX, ChibiSimConst.KickBoxKbY,
                                       ChibiSimConst.KickBoxStun, f.Facing, false);
                    }
                    if (j != ProgVictim(s, i)) return none;                                      // four players: held victim only
                    if (f.MovePhase == KickHolding && t == ChibiSimConst.KickLerpFrames)        // F:1068, after Stun + SetHeld
                        return ProgHit(ChibiSimConst.KickHit1Mult, 0, 0, ChibiSimConst.KickHit1Stun, f.Facing, true);
                    if (f.MovePhase == KickReleased && t == ChibiSimConst.KickReleaseFrame)     // F:1076, after ReleaseHeld
                        return ProgHit(ChibiSimConst.KickHit2Mult, ChibiSimConst.KickHit2KbX, ChibiSimConst.KickHit2KbY,
                                       ChibiSimConst.KickHit2Stun, f.Facing, true);
                    return none;
                default:
                    return none;   // Backstep / Barrage / Arrows: projectiles only
            }
        }

        /// <summary>Grab frame 0 just ran this tick (StartMove set MoveTick to 1) and the check has not happened yet.</summary>
        static bool GrabStartsNow(ChibiFighterSim f) =>
            f.MoveSlot >= 0 && f.MoveTick == 1 && f.MovePhase == 0 && ProgramOf(f) == PGrab;

        /// <summary>
        /// Hold phase (section 2.2 + TD amendment 1), after both UpdateFighter and before the melee probes:
        /// 1. grab checks of this tick (D-M3 = B: two same-tick grabs both whiff, keeping the 21-tick whiff lock);
        /// 2. per holder, the victim position for frames 1..25, and the release on frame 26 (the slam itself is the
        ///    holder's pending melee hit from <see cref="ProgramProbe"/>, applied after this release).
        /// Symmetric: both fighters' Physics already ran, and the victim skips Physics on every tick it starts held.
        /// </summary>
        static void HoldPhase(ChibiSimState s)
        {
            int n = s.PlayerCount;
            if (n <= 2)
            {
                bool g0 = GrabStartsNow(s.F[0]), g1 = GrabStartsNow(s.F[1]);
                if (g0 != g1) TryGrab(s, g0 ? 0 : 1);
            }
            else MultiGrabs(s);
            for (int i = 0; i < n; i++)
            {
                var h = s.F[i];
                if (ProgramOf(h) == PKick) { KickHoldPhase(s, i); continue; }
                if (!h.Holding) continue;
                int frame = h.MoveTick - 1;
                var v = s.F[h.HoldTarget];
                if (frame >= 1 && frame < ChibiSimConst.GrabLiftFrames)
                {
                    v.X = h.GrabSX + DivRound((long)(h.GrabTX - h.GrabSX) * frame, ChibiSimConst.GrabLiftFrames);
                    v.Y = h.GrabSY + DivRound((long)(h.GrabTY - h.GrabSY) * frame, ChibiSimConst.GrabLiftFrames);
                }
                else if (frame >= ChibiSimConst.GrabLiftFrames && frame < ChibiSimConst.GrabReleaseFrame)
                {
                    long j5 = (long)ChibiSimConst.GrabSlamStep * (frame - ChibiSimConst.GrabLiftFrames);
                    v.X = h.GrabTX + DivRound((h.GrabLX - h.GrabTX) * j5, ChibiSimConst.GrabSlamDen);
                    v.Y = h.GrabTY + DivRound((h.GrabLY - h.GrabTY) * j5, ChibiSimConst.GrabSlamDen);
                }
                else if (frame >= ChibiSimConst.GrabReleaseFrame) { Release(s, i); continue; }
                v.XRem = 0; v.YRem = 0; v.Vx = 0; v.Vy = 0;
            }
        }

        /// <summary>
        /// Grab check (F:793-803): opponent alive, not invulnerable, inside the inclusive box (A5). It is not a hit: shield,
        /// parry, Parry stance and Spin Guard do not stop it. Success: lock 66, victim Stun(1.2) (Interrupt, drops its
        /// shield), held; S and T snapshot after this tick's Physics (section 2.2).
        /// </summary>
        static void TryGrab(ChibiSimState s, int i)
        {
            if (GrabHits(s, i, 1 - i)) GrabConnect(s, i, 1 - i);
        }

        /// <summary>The grab check of <see cref="TryGrab"/> against fighter <paramref name="j"/> (pure).</summary>
        static bool GrabHits(ChibiSimState s, int i, int j)
        {
            var f = s.F[i];
            var o = s.F[j];
            if (!f.InPlay || !o.InPlay || o.Invuln > 0) return false;
            FacingBox(f, ChibiSimConst.GrabBoxX, ChibiSimConst.GrabBoxW, out int x0, out int x1);
            int y0 = f.Y + ChibiSimConst.GrabBoxY;
            return OverlapsInclusive(o, x0, x1, y0, y0 + ChibiSimConst.GrabBoxH);
        }

        /// <summary>
        /// Grabs of this tick with 3..4 fighters (four-player-design.md 1.3): each grabber's candidate is the lowest-index
        /// foe that is not held and passes the grab check. A mutual grab (A on B while B on A) whiffs both (D-M3); the rest
        /// resolve in index order, and nobody grabs or is grabbed once held.
        /// </summary>
        static void MultiGrabs(ChibiSimState s)
        {
            int n = s.PlayerCount;
            Span<int> cand = stackalloc int[ChibiSimState.MaxFighters];
            int starts = 0;
            for (int i = 0; i < n; i++)
            {
                cand[i] = -1;
                if (!GrabStartsNow(s.F[i])) continue;
                starts |= 1 << i;
                for (int j = 0; j < n; j++)
                    if (IsFoe(s, i, j) && !s.F[j].Held && GrabHits(s, i, j)) { cand[i] = j; break; }
            }
            for (int i = 0; i < n; i++)
            {
                int c = cand[i];
                if (c < 0) continue;
                if (((starts >> c) & 1) != 0 && cand[c] == i) continue;   // mutual grab: both whiff
                if (s.F[i].Held || s.F[c].Held) continue;
                GrabConnect(s, i, c);
            }
        }

        /// <summary>A grab that connected: lock 66, victim Stun(1.2), held; hold path snapshot (section 2.2).</summary>
        static void GrabConnect(ChibiSimState s, int i, int j)
        {
            var f = s.F[i];
            var o = s.F[j];
            f.MoveLockEnd = ChibiSimConst.GrabHitLock;
            f.MovePhase = 1;
            StunF(s, j, ChibiSimConst.GrabStun);
            Hold(s, i, j);
            f.ProgTarget = j;
            f.GrabSX = o.X; f.GrabSY = o.Y;
            f.GrabTX = f.X + f.Facing * ChibiSimConst.GrabTopDx; f.GrabTY = f.Y + ChibiSimConst.GrabTopDy;
            f.GrabLX = f.GrabTX; f.GrabLY = f.GrabTY;   // replaced on frame 18
        }

        /// <summary>
        /// Kick hold (rules v14, section 2.5), in the HoldPhase slot after both fighters' Physics: frame 9 Stun(96) (no
        /// invuln check, D-U4 = A) then SetHeld; frame 50 ReleaseHeld. The victim is not re-positioned while held (it stays
        /// where frame 9 left it), so a ring-out of the held victim is seen by the bounds check (TD A7).
        /// </summary>
        static void KickHoldPhase(ChibiSimState s, int i)
        {
            var h = s.F[i];
            int frame = h.MoveTick - 1;
            if (h.MovePhase == KickLerp && frame == ChibiSimConst.KickLerpFrames)
            {
                int v = ProgVictim(s, i);
                if (v < 0) { h.MovePhase = KickReleased; return; }   // four players only: no victim recorded
                StunF(s, v, ChibiSimConst.KickStun);
                Hold(s, i, v);
                h.MovePhase = KickHolding;
            }
            else if (h.MovePhase == KickHolding && frame == ChibiSimConst.KickReleaseFrame)
            {
                Release(s, i);
                h.MovePhase = KickReleased;
            }
        }

        /// <summary>Generic hold start (SetHeld(true), F:685-690): holder i holds the opponent; victim kinematic, V 0.</summary>
        static void Hold(ChibiSimState s, int i, int vi)
        {
            var v = s.F[vi];
            // Four players: a victim held by someone else is taken over, and a holder drops its old victim, so the invariant
            // F[v].HeldBy == h <=> F[h].HoldTarget == v always holds. Neither can happen in 1v1.
            if (v.HeldBy >= 0 && v.HeldBy != i) Release(s, v.HeldBy);
            if (s.F[i].HoldTarget >= 0 && s.F[i].HoldTarget != vi) Release(s, i);
            s.F[i].HoldTarget = vi;
            v.HeldBy = i;
            v.Vx = 0; v.Vy = 0; v.XRem = 0; v.YRem = 0; v.Grounded = false;
        }

        /// <summary>
        /// ReleaseHeld (F:681, F:685-690): if fighter <paramref name="i"/> holds the opponent, free it in place with V 0 (it
        /// keeps its Stun). Idempotent, so double ring-outs and repeated Interrupts are safe.
        /// </summary>
        static void Release(ChibiSimState s, int i)
        {
            var h = s.F[i];
            if (!h.Holding) return;
            var v = s.F[h.HoldTarget];
            h.HoldTarget = -1;
            v.HeldBy = -1;
            v.Vx = 0; v.Vy = 0;
        }

        /// <summary>
        /// Advances the running move one tick. <paramref name="dashing"/>: a dash is active, so the move's SelfVx/SelfVy/EndVx
        /// writes still override the dash velocity but MoveFriction is skipped. Returns true when the move forced Vy = 0
        /// this tick (FLevel window or EndVx tick), which selects the doubled arc term in <see cref="Physics"/>.
        /// </summary>
        static bool AdvanceMove(ChibiSimState s, int i, bool dashing, bool groundedAtStart)
        {
            var f = s.F[i];
            var o = s.F[Foe(s, i)];
            if (f.MoveSlot >= FirstNew) return AdvanceNewMove(s, i, dashing);   // rules v18 moveset rows
            int prog = ProgramOf(f);
            if (prog != PNone)
            {
                // Program move (rules v12, section 1): run frame MoveTick; grounded attack friction as today (a program
                // velocity write later this tick replaces it). Ends at MoveLockEnd.
                f.Shielding = false;
                if (!dashing && f.Grounded) f.Vx = Approach(f.Vx, 0, ChibiSimConst.MoveFriction);
                ProgramFrame(s, i, prog, f.MoveTick, groundedAtStart);
                f.MoveTick++;
                if (f.MoveTick >= f.MoveLockEnd) f.MoveSlot = -1;
                return false;
            }
            var mv = ChibiMoveTable.Get(f.Class, f.MoveSlot);
            int t = f.MoveTick;
            int activeStart = f.MoveStartup;
            int activeEnd = activeStart + f.MoveActive;
            f.Shielding = false;

            bool level = false;
            if (t == 0 && mv.SelfVy != 0) { f.Vy = mv.SelfVy * ChibiSimConst.VelSub; f.Grounded = false; }
            if (mv.SelfVx != 0 && t < activeEnd)
            {
                f.Vx = mv.SelfVx * ChibiSimConst.VelSub * f.Facing;
                if ((mv.Flags & ChibiMove.FLevel) != 0) { f.Vy = 0; level = true; }
            }
            else if (mv.EndVx != 0 && t == activeEnd)
            {
                f.Vx = mv.EndVx * ChibiSimConst.VelSub * f.Facing;   // Dash & Thrust exit speed, once (section 4)
                f.Vy = 0; level = true;
            }
            else if (!dashing && f.Grounded) f.Vx = Approach(f.Vx, 0, ChibiSimConst.MoveFriction);
            if ((mv.Flags & ChibiMove.FTargeted) != 0 && t < activeStart) f.TargetX = o.X; // cloud follows, then locks

            if (t >= activeStart && t < activeEnd)
            {
                int k = t - activeStart;
                if ((mv.Flags & ChibiMove.FParry) != 0) f.ParryTicks = 1;
                if ((mv.Flags & ChibiMove.FSpinGuard) != 0) f.GuardTicks = 1;

                bool arm = k == 0 || (mv.Rehit > 0 && k % mv.Rehit == 0);
                if (arm)
                {
                    f.MoveHitMask = 0;
                    if (mv.ProjSpeed > 0)
                    {
                        int yOff = mv.Rehit > 0 ? ((f.ShotIdx * 3) % 5 - 2) * 110 : 0;
                        bool fin = f.MoveFinisher != 0;
                        // Return mult carries the same Patience factor as the move (0.8 x b, F:945): exact for b = 1.5.
                        int retMult = mv.ProjReturnTicks > 0 && mv.Mult > 0
                            ? (int)((long)mv.ProjReturnMult * f.MoveMult / mv.Mult) : 0;
                        int slotN = SpawnProjectile(s, new ProjSpec
                        {
                            Owner = i, Kind = mv.ProjKind, X = f.X + f.Facing * mv.ProjOffX, Y = f.Y + mv.ProjOffY + yOff,
                            Vx = f.Facing * mv.ProjSpeed, Vy = 0, Radius = mv.ProjR, Life = mv.ProjLife, Mult = f.MoveMult,
                            KbX = fin ? mv.FinKbX : mv.KbX, KbY = fin ? mv.FinKbY : mv.KbY, Stun = fin ? mv.FinStun : mv.Stun,
                            Flags = mv.ProjFlags, ReturnTicks = mv.ProjReturnTicks, ReturnMult = retMult,
                        });
                        if (slotN >= 0 && mv.ProjKind == ChibiProjKind.Spear) f.SpearOut = true;   // A1: only on success
                        f.ShotIdx++;
                    }
                    // Reload starts on the finisher's strike (F:474); never set if the move is interrupted first.
                    if (f.MoveFinisher != 0 && (f.Class == (int)FighterClass.Ranged || f.Class == (int)FighterClass.Magic))
                        f.SetCd = ChibiSimConst.SetCooldownTicks;
                    if ((mv.Flags & ChibiMove.FAura) != 0) f.AuraTicks = ChibiSimConst.AuraDuration;
                    if ((mv.Flags & ChibiMove.FRing) != 0) f.RingTicks = ChibiSimConst.RingDuration;
                    // Sword Aura: every normal strike (light, finisher, heavy) also throws a wave (F:490-494, doc section 6)
                    if (f.MoveSlot <= 1 && f.Class == (int)FighterClass.Melee && f.AuraTicks > 0)
                        SpawnProjectile(s, new ProjSpec
                        {
                            Owner = i, Kind = ChibiProjKind.AuraWave, X = f.X + f.Facing * ChibiSimConst.AuraWaveOffX,
                            Y = f.Y + ChibiSimConst.AuraWaveOffY, Vx = f.Facing * ChibiSimConst.AuraWaveSpeedSub,
                            Radius = ChibiSimConst.AuraWaveR, Life = ChibiSimConst.AuraWaveLife, Mult = ChibiSimConst.AuraWaveMult,
                            KbX = ChibiSimConst.AuraWaveKbX, KbY = ChibiSimConst.AuraWaveKbY, Stun = ChibiSimConst.AuraWaveStun,
                            Flags = ChibiProjectileSim.FDestructible,
                        });
                }
            }

            f.MoveTick++;
            if (f.MoveTick >= f.MoveLockEnd) f.MoveSlot = -1;   // = startup + active + recovery for row moves
            return level;
        }

        /// <summary>
        /// Movement doc section 2 steps 2-4 plus stage-conversion.md sections 2, 4, 5: gravity (no global cap), position
        /// integration with the sub-milli remainder and the C50 50 Hz arc term while airborne, then the solid main stage
        /// (top landing, underside bonk, side wall), the one-way platforms (land only when crossing the top from above
        /// while falling, D-S2), and the ground probe.
        /// <paramref name="level"/>: Vy was forced to 0 this tick (dash, Dash &amp; Thrust), so airborne Y steps use the
        /// doubled arc term, Gravity + 2·ArcC50 = 10595 sub (dash-conversion.md row 6).
        /// D-S1: the arc term is gated on "resting on a surface at the start of the tick" (not on Grounded), and Grounded
        /// needs the feet at or above a surface top (within the probe band), never inside a platform slab.
        /// </summary>
        static void Physics(ChibiSimState s, ChibiFighterSim f, bool level, ChibiStageDef st)
        {
            int t = s.GameTicks;
            int prevX = f.X, prevY = f.Y;
            bool airborne = !IsResting(f, st, t) || f.Vy > 0;
            f.Vy -= GravityOf(f);                                                                    // v20: hitstun gravity fade
            Integrate(ref f.X, ref f.XRem, f.Vx);
            int arc = level ? ChibiSimConst.LevelArcSub : ChibiSimConst.ArcC50;
            Integrate(ref f.Y, ref f.YRem, airborne ? f.Vy - arc : f.Vy);

            // ---- rules v20 (stages README 3.1): inactive edges are solid. Side walls clamp the centre and stop Vx into
            // them; the sky cap stops the rise (fall back). The solid floor is a landing surface below.
            if (!st.KoSides)
            {
                int wx = st.WallClampX;
                if (f.X > wx) { f.X = wx; f.XRem = 0; if (f.Vx > 0) f.Vx = 0; }
                else if (f.X < -wx) { f.X = -wx; f.XRem = 0; if (f.Vx < 0) f.Vx = 0; }
            }
            if (!st.KoTop && f.Y > st.CeilingY) { f.Y = st.CeilingY; f.YRem = 0; if (f.Vy > 0) f.Vy = 0; }

            // ---- section 4: solid main stage, box x in [-HW, HW], y in [MainBottom, 0]
            int hw = st.HalfWidth, sup = hw + ChibiStageTable.SupportHalfW, mainBottom = st.MainBottom;
            int landTop = int.MinValue;
            bool overMain = Math.Abs(f.X) < sup;
            if (f.Vy <= 0 && prevY >= 0 && f.Y <= 0 && overMain) landTop = 0;                      // 1. top
            else if (overMain && f.Y < 0 && f.Y + ChibiSimConst.HurtH > mainBottom)
            {
                if (prevY + ChibiSimConst.HurtH <= mainBottom)                                       // 2. underside bonk
                {
                    f.Y = mainBottom - ChibiSimConst.HurtH; f.YRem = 0;
                    if (f.Vy > 0) f.Vy = 0;
                }
                else if (Math.Abs(prevX) >= sup)                                                     // 3. side wall
                {
                    int side = prevX < 0 ? -1 : 1;
                    f.X = side * sup; f.XRem = 0;
                    if (f.Vx * side < 0) f.Vx = 0;
                }
                else landTop = 0;                                                                    // 4. fallback
            }

            // ---- section 2: one-way platforms; the highest qualifying top wins. Rules v20: moving platforms use this game
            // step's position; a rising top catches a fighter whose feet were above min(previous top, current top).
            var plats = st.Platforms;
            for (int p = 0; p < plats.Length; p++)
            {
                if (p == f.DropPlatform) continue;
                int top = plats[p].TopAt(t);
                int from = plats[p].Axis == ChibiPlatform.AxisY ? Math.Min(top, plats[p].TopAt(t - 1)) : top;
                if (f.Vy <= 0 && prevY >= from && f.Y <= top && top > landTop &&
                    Math.Abs(f.X - plats[p].CxAt(t)) < plats[p].HalfW + ChibiStageTable.SupportHalfW)
                    landTop = top;
            }
            if (st.HasFloor && f.Vy <= 0 && prevY >= st.FloorY && f.Y <= st.FloorY && st.FloorY > landTop &&         // v21 floor
                Math.Abs(f.X) < st.FloorHalfW + ChibiStageTable.SupportHalfW) landTop = st.FloorY;
            if (landTop != int.MinValue) { f.Y = landTop; f.YRem = 0; f.Vy = 0; }

            // ---- section 5 with D-S1: Grounded only when resting, i.e. feet exactly on a surface top (after the landing
            // snap) within the probe half-width 337.5 (doubled for exactness). The original's 100-milli band above the top
            // is not used: it reported Grounded mid-air while the fighter was still falling toward the surface.
            bool probe = 2 * Math.Abs(f.X) < 2 * hw + ChibiStageTable.ProbeHalfW2 && f.Y == 0;
            if (!probe) probe = st.HasFloor && f.Y == st.FloorY && 2 * Math.Abs(f.X) < 2 * st.FloorHalfW + ChibiStageTable.ProbeHalfW2;
            if (!probe) probe = ProbePlatform(f, st, t) >= 0;
            f.Grounded = probe && f.Vy <= ChibiSimConst.GroundProbeMaxVy;
            if (f.Grounded) f.AirJumps = 1;
        }

        /// <summary>
        /// Index of the first platform (not the dropped one) inside the ground probe at game tick <paramref name="t"/>, or
        /// -1. D-S1: the feet must rest exactly on the top, never above it or inside the slab (stage-conversion.md section 5).
        /// </summary>
        static int ProbePlatform(ChibiFighterSim f, ChibiStageDef st, int t)
        {
            var plats = st.Platforms;
            for (int p = 0; p < plats.Length; p++)
            {
                if (p == f.DropPlatform) continue;
                if (f.Y == plats[p].TopAt(t) &&
                    2 * Math.Abs(f.X - plats[p].CxAt(t)) < 2 * plats[p].HalfW + ChibiStageTable.ProbeHalfW2)
                    return p;
            }
            return -1;
        }

        /// <summary>
        /// True when the feet sit exactly on a surface top within the support width (main stage, solid floor, or a
        /// platform at tick <paramref name="t"/> that is not being dropped through). Gates the C50 arc term (D-S1).
        /// </summary>
        static bool IsResting(ChibiFighterSim f, ChibiStageDef st, int t)
        {
            if (f.Y == 0 && Math.Abs(f.X) < st.HalfWidth + ChibiStageTable.SupportHalfW) return true;
            if (st.HasFloor && f.Y == st.FloorY && Math.Abs(f.X) < st.FloorHalfW + ChibiStageTable.SupportHalfW) return true;
            return RestingPlatform(f, st, t) >= 0;
        }

        /// <summary>Platform the feet rest on at tick <paramref name="t"/> (support width, not the dropped one), or -1.</summary>
        static int RestingPlatform(ChibiFighterSim f, ChibiStageDef st, int t)
        {
            var plats = st.Platforms;
            for (int p = 0; p < plats.Length; p++)
                if (p != f.DropPlatform && f.Y == plats[p].TopAt(t) &&
                    Math.Abs(f.X - plats[p].CxAt(t)) < plats[p].HalfW + ChibiStageTable.SupportHalfW) return p;
            return -1;
        }

        /// <summary>pos (milli) += vel (sub-units) using floor division, so negative velocities round consistently.</summary>
        static void Integrate(ref int pos, ref int rem, int vel)
        {
            int t = rem + vel;
            int q = t / ChibiSimConst.VelSub;
            if (t % ChibiSimConst.VelSub < 0) q--;
            pos += q;
            rem = t - q * ChibiSimConst.VelSub;
        }

        // ------------------------------------------------------------------ hit detection

        /// <summary>
        /// Melee probe of fighter <paramref name="i"/> against every foe: <paramref name="hits"/>[j] receives the hit on
        /// fighter j (left default when none). Hit once per victim per arm (MoveHitMask). 1v1: only j = 1 - i.
        /// </summary>
        static void MeleeProbe(ChibiSimState s, int i, Span<PendingHit> hits)
        {
            var f = s.F[i];
            int n = s.PlayerCount;
            if (f.MoveSlot < 0) return;
            if (f.MoveSlot >= FirstNew) { NewMoveProbe(s, i, hits); return; }   // rules v18 moveset rows
            int prog = ProgramOf(f);
            if (prog != PNone)
            {
                for (int j = 0; j < n; j++)
                {
                    if (!IsFoe(s, i, j)) continue;
                    var ph = ProgramProbe(s, i, j, prog, f.MoveTick - 1);
                    ph.Ult = f.MoveSlot == 5;            // rules v20: Smash / Kick ult program hits
                    hits[j] = ph;
                }
                return;
            }
            var mv = ChibiMoveTable.Get(f.Class, f.MoveSlot);
            if (mv.ProjSpeed > 0 || mv.Mult <= 0) return;

            int t = f.MoveTick - 1;   // the tick AdvanceMove just processed
            if (t < f.MoveStartup || t >= f.MoveStartup + f.MoveActive) return;

            bool centered = (mv.Flags & ChibiMove.FCentered) != 0;
            int cx = (mv.Flags & ChibiMove.FTargeted) != 0 ? f.TargetX : f.X;
            int x0, x1;
            if (centered) { x0 = cx - mv.BoxW / 2; x1 = x0 + mv.BoxW; }
            else if (f.Facing > 0) { x0 = f.X + mv.BoxX; x1 = x0 + mv.BoxW; }
            else { x1 = f.X - mv.BoxX; x0 = x1 - mv.BoxW; }
            int y0 = f.Y + mv.BoxY, y1 = y0 + mv.BoxH;

            for (int j = 0; j < n; j++)
            {
                if (!IsFoe(s, i, j) || (f.MoveHitMask & (1 << j)) != 0) continue;
                var o = s.F[j];
                if (!Overlaps(o, x0, x1, y0, y1)) continue;

                f.MoveHitMask |= 1 << j;
                int dir = centered ? (Sign(o.X - f.X) != 0 ? Sign(o.X - f.X) : f.Facing) : f.Facing;
                bool fin = f.MoveFinisher != 0;
                hits[j] = new PendingHit
                {
                    Valid = true, Mult = f.MoveMult, KbX = fin ? mv.FinKbX : mv.KbX, KbY = fin ? mv.FinKbY : mv.KbY,
                    Stun = fin ? mv.FinStun : mv.Stun, Dir = dir, Unblockable = (mv.Flags & ChibiMove.FUnblockable) != 0,
                    Src = SrcMelee,
                };
            }
        }

        static bool Overlaps(ChibiFighterSim target, int x0, int x1, int y0, int y1)
        {
            return x0 < target.X + ChibiSimConst.HurtHalfW && x1 > target.X - ChibiSimConst.HurtHalfW &&
                   y0 < target.Y + ChibiSimConst.HurtH && y1 > target.Y;
        }

        /// <summary>Inclusive AABB test like the original OverlapsBox (F:507-511, TD amendment 5): used by the grab only.</summary>
        static bool OverlapsInclusive(ChibiFighterSim target, int x0, int x1, int y0, int y1)
        {
            return x0 <= target.X + ChibiSimConst.HurtHalfW && x1 >= target.X - ChibiSimConst.HurtHalfW &&
                   y0 <= target.Y + ChibiSimConst.HurtH && y1 >= target.Y;
        }

        // ------------------------------------------------------------------ projectiles (rules v11, projectile-conversion.md)

        /// <summary>Spawn spec for <see cref="SpawnProjectile"/> (section 6). Positions milli; Vx/Vy/KbX/KbY sub-units.</summary>
        struct ProjSpec
        {
            public int Owner;
            public ChibiProjKind Kind;
            public int X, Y, Vx, Vy, Radius, Life, Mult, KbX, KbY, Stun, Flags, GrowPerTick, ReturnTicks, ReturnMult;
        }

        /// <summary>
        /// Spawns a projectile in the first free slot and returns the slot, or -1 when the pool is full (the shot is dropped
        /// and counted in <see cref="ChibiSimState.DroppedShots"/>, A4). Writes every field (slots are reused, A2):
        /// Speed = ISqrt(Vx² + Vy²), remainders 0, SpawnFrame = GameTicks, so it first moves on the next game step.
        /// </summary>
        static int SpawnProjectile(ChibiSimState s, in ProjSpec sp)
        {
            for (int n = 0; n < s.Proj.Length; n++)
            {
                var p = s.Proj[n];
                if (p.Active) continue;
                p.Active = true; p.Owner = sp.Owner; p.X = sp.X; p.Y = sp.Y; p.Vx = sp.Vx; p.Vy = sp.Vy;
                p.XRem = 0; p.YRem = 0; p.Life = sp.Life; p.SpawnFrame = s.GameTicks;
                p.Mult = sp.Mult; p.KbX = sp.KbX; p.KbY = sp.KbY; p.Stun = sp.Stun;
                p.Kind = sp.Kind; p.Flags = sp.Flags; p.Radius = sp.Radius; p.GrowPerTick = sp.GrowPerTick;
                p.Speed = (int)ChibiFixed.ISqrt((long)sp.Vx * sp.Vx + (long)sp.Vy * sp.Vy);
                p.ReturnTicks = sp.ReturnTicks; p.ReturnMult = sp.ReturnMult;
                p.HitMask = 0;      // four players
                p.ResetMoveset();   // rules v18: SrcSlot -1, no bounces
                return n;
            }
            s.DroppedShots++;   // deterministic: the pool is rollback state
            return -1;
        }

        /// <summary>Removes a projectile. Spear OnGone (CO:115) clears the owner's SpearOut on every removal path.</summary>
        static void Despawn(ChibiSimState s, ChibiProjectileSim p)
        {
            p.Active = false;
            if (p.Kind == ChibiProjKind.Spear) s.F[p.Owner].SpearOut = false;
        }

        static int DivRound(long n, long d) => (int)(n >= 0 ? (n + d / 2) / d : -((-n + d / 2) / d));

        /// <summary>
        /// Section 3 + A3: (dx, dy) scaled to length <paramref name="speed"/>, each component rounded half away from zero.
        /// Short vectors (max |component| &lt; 2^20) are pre-scaled x1024 before the integer sqrt, so the floor error stays
        /// below 1 sub. A zero vector gives (0, 0), like Unity's normalized.
        /// </summary>
        /// <example><code>ChibiSimulation.ScaleTo(3, 4, 5000, out int x, out int y); // x = 3000, y = 4000</code></example>
        public static void ScaleTo(long dx, long dy, int speed, out int rx, out int ry)
        {
            long ax = dx < 0 ? -dx : dx, ay = dy < 0 ? -dy : dy;
            if ((ax > ay ? ax : ay) < (1L << 20)) { dx *= 1024; dy *= 1024; }
            long l = ChibiFixed.ISqrt(dx * dx + dy * dy);
            if (l == 0) { rx = 0; ry = 0; return; }
            rx = DivRound(dx * speed, l);
            ry = DivRound(dy * speed, l);
        }

        /// <summary>
        /// Constant-speed homing (CO:51-55, section 3b): V = normalize(Lerp(V, dir·speed, 7/120))·speed, in integers:
        /// D = ScaleTo(to target, speed); B = 113 V + 7 D; V = ScaleTo(B, speed). (dx, dy) = target Center - position
        /// before the move.
        /// </summary>
        /// <example><code>int vx = 135000, vy = 0; ChibiSimulation.SteerHoming(ref vx, ref vy, 135000, 0, 1000); // (134742, 8347)</code></example>
        public static void SteerHoming(ref int vx, ref int vy, int speed, int dx, int dy)
        {
            ScaleTo(dx, dy, speed, out int ddx, out int ddy);
            long bx = 113L * vx + 7L * ddx, by = 113L * vy + 7L * ddy;
            ScaleTo(bx, by, speed, out vx, out vy);
        }

        /// <summary>Circle (px, py, r) vs the target's hurtbox, inclusive (F:514-519, section 3a).</summary>
        public static bool CircleHitsFighter(ChibiFighterSim t, int px, int py, int r) =>
            ChibiFixed.CircleOverlapsAabb(px, py, r, t.X - ChibiSimConst.HurtHalfW, t.Y,
                                          t.X + ChibiSimConst.HurtHalfW, t.Y + ChibiSimConst.HurtH);

        /// <summary>Spin Guard (1.6) or Fire Ring (2.1) of the target eats a Destructible shot: strict, from Center (F:522-528).</summary>
        static bool DestroyedBy(ChibiFighterSim t, int px, int py)
        {
            long d2 = ChibiFixed.DistSq(px - t.X, py - (t.Y + ChibiSimConst.CenterOffY));
            if (t.GuardTicks > 0 && d2 < (long)ChibiSimConst.GuardRadius * ChibiSimConst.GuardRadius) return true;
            return t.RingTicks > 0 && d2 < (long)ChibiSimConst.RingRadius * ChibiSimConst.RingRadius;
        }

        /// <summary>
        /// One update of every projectile, slots in index order (CO:44-111, projectile-conversion.md section 2):
        /// Life, homing, return timer, return steering (arrive check before the move), move, growth, destroy / hit
        /// (skipped when Harmless or HasHit), then expiry by Life or bounds (VolleyLead splits on expiry).
        /// </summary>
        static void UpdateProjectiles(ChibiSimState s)
        {
            const int cy = ChibiSimConst.CenterOffY;
            var pst = ChibiStageTable.Get(s.Stage);
            for (int n = 0; n < s.Proj.Length; n++)
            {
                var p = s.Proj[n];
                if (!p.Active || p.SpawnFrame == s.GameTicks) continue;   // first update on the next game step
                var owner = s.F[p.Owner];
                var target = s.F[Foe(s, p.Owner)];   // homing: nearest enemy (1v1: the opponent)

                p.Life--;                                                                         // CO:47
                ProjTimers(p);                                                                    // v18 bounce re-arm
                if ((p.Flags & ChibiProjectileSim.FHoming) != 0)                                  // CO:51-55
                    SteerHoming(ref p.Vx, ref p.Vy, p.Speed, target.X - p.X, target.Y + cy - p.Y);
                if (p.ReturnTicks > 0)                                                            // CO:57-66
                {
                    p.ReturnTicks--;
                    if (p.ReturnTicks == 0 && (p.Flags & ChibiProjectileSim.FReturning) == 0)
                    {
                        p.Flags = (p.Flags | ChibiProjectileSim.FReturning) & ~ChibiProjectileSim.FHasHit;
                        p.HitMask = 0;
                        p.Mult = p.ReturnMult;
                    }
                }
                if ((p.Flags & ChibiProjectileSim.FReturning) != 0)                               // CO:67-72
                {
                    int dx = owner.X - p.X, dy = owner.Y + cy - p.Y;
                    ScaleTo(dx, dy, ChibiSimConst.ReturnSpeedSub, out p.Vx, out p.Vy);
                    if (ChibiFixed.DistSq(dx, dy) < (long)ChibiSimConst.ReturnArriveR * ChibiSimConst.ReturnArriveR)
                    { Despawn(s, p); continue; }
                    if (p.Life < ChibiSimConst.ReturnLifeFloor) p.Life = ChibiSimConst.ReturnLifeFloor;
                }
                int prevY = p.Y;
                Integrate(ref p.X, ref p.XRem, p.Vx);                                             // CO:74
                Integrate(ref p.Y, ref p.YRem, p.Vy);
                p.Radius += p.GrowPerTick;                                                        // CO:77, CO:97
                if (p.SrcSlot >= 0 && ProjStage(s, p, prevY)) continue;                           // v18 bounce / tether

                // CO:89. 1v1 keeps the HasHit gate; with more fighters a pierce shot may still hit the foes it has not hit.
                if ((p.Flags & ChibiProjectileSim.FHarmless) == 0 &&
                    (s.PlayerCount > 2 || (p.Flags & ChibiProjectileSim.FHasHit) == 0) && ProjHitFoes(s, p))
                    continue;
                // CO:108: Life <= 0 || |x| > 40 || y < -30 || y > 40 (world units).
                if (p.Life <= 0 || Math.Abs(p.X) > ChibiSimConst.ProjBoundX ||
                    p.Y < ChibiSimConst.ProjBlastBottom || p.Y > ChibiSimConst.ProjBlastTop ||
                    (!pst.KoSides && Math.Abs(p.X) > pst.BlastX))                                // v20: walls despawn shots
                {
                    int lx = p.X, ly = p.Y;
                    if (p.SrcSlot >= 0) ProjExpire(s, p);                                         // v18 air tether anchor
                    Despawn(s, p);                                                                // frees the slot first
                    if (p.Kind == ChibiProjKind.VolleyLead) SplitVolley(s, p.Owner, lx, ly);      // CO:110 OnExpire
                }
            }
        }

        /// <summary>
        /// Thousand Arrows split (F:1019-1032, section 4, D-P1 = A, A5): 18 arrows from (lx, ly). Per arrow, s.Rng draws in
        /// this order: ox, oy, (cx, cy) until inside the 0.6 disc (max 16 tries, then the centre), speed. Each flies at its
        /// speed toward the opponent's Center + (ox, oy), and first moves on the next game step (SpawnFrame = GameTicks).
        /// </summary>
        /// <summary>
        /// Destroy / reflect / hit checks of one projectile against every foe of its owner it has not hit yet, in index
        /// order (four-player-design.md 1.3; 1v1: the opponent only, the rules-v24 body). Returns true when the projectile
        /// is gone or reflected, so the caller skips the expiry check.
        /// </summary>
        static bool ProjHitFoes(ChibiSimState s, ChibiProjectileSim p)
        {
            int n = s.PlayerCount;
            for (int j = 0; j < n; j++)
            {
                // 1v1 gates on FHasHit only (the caller), exactly rules v24; HitMask gates only with more than 2 fighters.
                if (!IsFoe(s, p.Owner, j) || (n > 2 && (p.HitMask & (1 << j)) != 0)) continue;
                var target = s.F[j];
                if ((p.Flags & ChibiProjectileSim.FDestructible) != 0 && DestroyedBy(target, p.X, p.Y))
                { Despawn(s, p); return true; }
                if (ProjReflected(s, p, j)) return true;                                          // v18 reflect row
                if (!CircleHitsFighter(target, p.X, p.Y, p.Radius)) continue;
                // HasHit is set even if the hit misses (invulnerable / KO'd), CO:102-104; kb sign Vel.x >= 0 (CO:101).
                p.Flags |= ChibiProjectileSim.FHasHit;
                p.HitMask |= 1 << j;
                var hit = new PendingHit
                {
                    Valid = true, Mult = p.Mult, KbX = ChibiFixed.Abs(p.KbX), KbY = p.KbY, Stun = p.Stun,
                    Dir = p.Vx >= 0 ? 1 : -1, Src = SrcProj, Ult = IsUltProjectile(p.Kind),
                };
                if (p.SrcSlot >= 0) ProjAdjustHit(s, p, j, ref hit);                              // v18 pull / spike / trip
                int res = ApplyHit(s, p.Owner, j, hit);
                if (res == HitCountered) { Despawn(s, p); return true; }                          // v18 counter:proj
                if (p.SrcSlot >= 0 && ProjAfterHit(s, p)) return true;                            // v18 bounce / tether
                if ((p.Flags & (ChibiProjectileSim.FPierce | ChibiProjectileSim.FReturning)) == 0 && p.ReturnTicks == 0)
                { Despawn(s, p); return true; }
                if (!p.Active) return true;
            }
            return false;
        }

        static void SplitVolley(ChibiSimState s, int owner, int lx, int ly)
        {
            var t = s.F[Foe(s, owner)];
            const int r = ChibiSimConst.VolleyDiscR;
            for (int k = 0; k < ChibiSimConst.VolleyCount; k++)
            {
                int ox = s.Rng.Range(2 * ChibiSimConst.VolleyOxHalf + 1) - ChibiSimConst.VolleyOxHalf;
                int oy = s.Rng.Range(2 * ChibiSimConst.VolleyOyHalf + 1) - ChibiSimConst.VolleyOyHalf;
                int cx = 0, cy = 0;
                bool inside = false;
                for (int tries = 0; tries < ChibiSimConst.VolleyMaxTries && !inside; tries++)
                {
                    cx = s.Rng.Range(2 * r + 1) - r;
                    cy = s.Rng.Range(2 * r + 1) - r;
                    inside = ChibiFixed.DistSq(cx, cy) <= (long)r * r;
                }
                if (!inside) { cx = 0; cy = 0; }
                int speed = ChibiSimConst.VolleySpeedMin + s.Rng.Range(ChibiSimConst.VolleySpeedSpan);
                int fx = lx + cx, fy = ly + cy;
                ScaleTo(t.X + ox - fx, t.Y + ChibiSimConst.CenterOffY + oy - fy, speed, out int vx, out int vy);
                SpawnProjectile(s, new ProjSpec
                {
                    Owner = owner, Kind = ChibiProjKind.VolleyArrow, X = fx, Y = fy, Vx = vx, Vy = vy,
                    Radius = ChibiSimConst.VolleyR, Life = ChibiSimConst.VolleyLife, Mult = ChibiSimConst.VolleyMult,
                    KbX = ChibiSimConst.VolleyKbX, KbY = ChibiSimConst.VolleyKbY, Stun = ChibiSimConst.VolleyStun,
                    Flags = ChibiProjectileSim.FDestructible,
                });
            }
        }

        /// <summary>Fire Ring ultimate: damages anyone inside the ring every half second.</summary>
        static void TickFireRings(ChibiSimState s)
        {
            int n = s.PlayerCount;
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var f = s.F[i];
                if (f.RingTicks <= 0 || f.RingTicks % 30 != 0 || !IsFoe(s, i, j)) continue;
                var o = s.F[j];
                int dx = o.X - f.X, dy = o.Y - f.Y;
                if ((long)dx * dx + (long)dy * dy > (long)ChibiSimConst.RingRadius * ChibiSimConst.RingRadius) continue;
                ApplyHit(s, i, j, new PendingHit { Valid = true, Mult = 400, KbX = 60 * ChibiSimConst.VelSub, KbY = 45 * ChibiSimConst.VelSub, Stun = 12, Dir = Sign(dx) != 0 ? Sign(dx) : f.Facing, Ult = true });   // v20: Fire Ring = ult
            }
        }

        // ------------------------------------------------------------------ damage

        /// <summary>
        /// Resolves one hit (damage-conversion.md sections 2-6, F:545-649). Fixed order and RNG draws:
        ///   0. match over / attacker Out / defender Out, hovering or invulnerable: nothing, 0 draws
        ///   1. Parry skill stance (not unblockable): reflect on the attacker, 1 draw (crit), stop
        ///   2. perfect shield parry (ShieldTick 0..10): 0 draws, stop
        ///   3. Ward (defender Magic P1): 1 draw, stop if it fires
        ///   4. crit: always 1 draw
        ///   5/6. Hex / Armor Break (attacker passive): 1 draw, only on a landed unblocked or guard-breaking hit.
        /// Damage is exact in nano-HP with one half-up rounding to micro-HP (decision D9-1 = a). Rules v20
        /// (percent-knockback.md 3.2): the micro-HP becomes percent through <see cref="Pct"/>, and an unblocked hit launches
        /// with the knockback formula (<see cref="ComputeLaunch"/>); order of steps and RNG draws unchanged.
        /// </summary>
        static int ApplyHit(ChibiSimState s, int a, int d, PendingHit h)
        {
            var A = s.F[a];
            var D = s.F[d];
            // !Match.Running: a KO earlier this tick (e.g. h0 of a trade) ends the match before h1 resolves.
            if (s.MatchOver || A.OutTicks > 0 || D.OutTicks > 0 || D.HoverTicks > 0 || D.Invuln > 0) return HitNormal;
            // Rules v18: a moveset counter stance catches the hit before everything else (README 5 counter).
            if (CounterCatches(s, a, d, h)) return HitCountered;

            // 1. Parry skill: the attack is turned around on the attacker (section 3b)
            // Rules v18: a moveset reflect row (Prism Reflection) turns a weak frontal melee hit around the same way.
            if ((D.ParryTicks > 0 || ReflectsMelee(s, a, d, h)) && !h.Unblockable)
            {
                D.ParryTicks = 0;
                D.Parries++;
                GainUlt(D, ChibiSimConst.UltPerParry);
                long refN = ChibiCombatStats.RawDamageNano(A.Stats, h.Mult, A.WeakenTicks > 0, ref s.Rng, out _);
                int refU = ChibiCombatStats.FinalDamageMicro(refN, ChibiCombatStats.DefenseH(A.Stats, A.ArmorBreakTicks > 0), false);
                AddPct(A, Pct(refU));                       // rules v20: reflect damage as percent; push stays fixed
                A.LastHitBy = d;                            // four players: hit attribution
                A.Stun = Math.Max(A.Stun, ChibiSimConst.ReflectStun);
                A.LaunchAge = 0;                            // rules v24 (ED-1 review): a reflect stun cannot be escaped
                Interrupt(s, a);
                if (!A.Held)                                // a held (kinematic) attacker keeps V 0
                {
                    A.Vx = (A.X >= D.X ? 1 : -1) * ChibiSimConst.ReflectPushX;   // Mathf.Sign(0) = +1, positions not hit Dir
                    A.Vy = ChibiSimConst.ReflectPushY;
                }
                GainUlt(A, ChibiSimConst.UltWhenHit);
                Hitstop(s, ChibiSimConst.HitstopReflect);   // F:559; the reflect's own ApplyDamage 3 is absorbed by max
                return HitReflected;
            }

            // 2. Perfect parry (section 3a): no damage, no guard drain, no draws; attacker Stun() zeroes Vx, caps Vy.
            bool blocking = D.Shielding && !h.Unblockable;
            if (blocking && D.ShieldTick < ChibiSimConst.ParryWindow)
            {
                D.Parries++;
                GainUlt(D, ChibiSimConst.UltPerParry);
                StunF(s, a, ChibiSimConst.PerfectParryStun);
                Hitstop(s, ChibiSimConst.HitstopParry);     // F:570
                return HitNormal;
            }

            // 3. Ward negates everything (section 5)
            if (D.Stats.WardPermille > 0 && s.Rng.Roll(D.Stats.WardPermille)) return HitNormal;

            // 4. Raw damage with the crit draw; the guard soaks the raw hit (crit included, before the block fraction)
            long rawN = ChibiCombatStats.RawDamageNano(A.Stats, h.Mult, A.WeakenTicks > 0, ref s.Rng, out bool crit);
            int stun = h.Stun;
            if (blocking)
            {
                long g = D.GuardU - rawN / 1000;
                if (g <= 0)
                {
                    D.GuardU = 0;                           // guard break: full damage, stun >= 1 s, no blocking for 3 s
                    D.GuardBrokenTicks = ChibiCombatStats.GuardBrokenTicks;
                    D.Shielding = false; D.ShieldTick = 0;
                    blocking = false;
                    if (stun < ChibiCombatStats.GuardBreakStunTicks) stun = ChibiCombatStats.GuardBreakStunTicks;
                }
                else D.GuardU = (int)g;
            }
            int dmgU = ChibiCombatStats.FinalDamageMicro(rawN, ChibiCombatStats.DefenseH(D.Stats, D.ArmorBreakTicks > 0), blocking);
            int dPctM = Pct(dmgU);                          // rules v20 (spec 4.1): the hit's percent d
            AddPct(D, dPctM);
            D.LastHitBy = a;                                // four players: hit attribution (landed or blocked)
            // ApplyDamage F:625 HitStop(crit ? 0.08 : 0.045). Blocked chip also goes through ApplyDamage (held = true,
            // damage-conversion.md section 3 row 95), so it triggers too; Ward (returned above) does not.
            Hitstop(s, crit ? ChibiSimConst.HitstopCrit : ChibiSimConst.HitstopNormal);

            if (h.Src == SrcMelee) A.MoveConnected = true;   // rules v23 (B4-2): landed or blocked counts for the walk cancel
            if (blocking)
            {
                // Chip: no stun, no Interrupt (shield stays up), kb x0.25 on X and Y (section 2)
                SetKnockback(D, h.Dir * (h.KbX / 4), h.KbY / 4);
                GainUlt(D, ChibiSimConst.UltWhenHit);
                GainUlt(A, 10);                             // OnHitLanded blocked: +1 ult and return
                return HitNormal;
            }

            // ApplyDamage (section 4): stun + Interrupt only when stun > 0; velocity set only above the threshold
            // Rules v18: armor (README 5) keeps the move running: no stun, no Interrupt, kb scaled or none.
            // Rules v20 (spec 3.2 steps 6-7): knockback from percent; set knockback keeps the legacy velocity and stun.
            var launch = ComputeLaunch(A, D, h, dPctM, h.Stun);
            if (stun > h.Stun && launch.Stun < stun) launch.Stun = stun;   // guard break keeps its 60-tick floor (spec 3.2 step 6)
            if (!ArmorAbsorbs(s, d, h, launch.KbM, launch.Vx, launch.Vy))
            {
                if (launch.Stun > 0) { D.Stun = Math.Max(D.Stun, launch.Stun); Interrupt(s, d); D.AirUsedMask = 0; }
                D.LaunchDecX = launch.DecX; D.LaunchDecY = launch.DecY; D.LaunchGravP = launch.GravP;
                D.StartLaunchAge(StrongKb(h.KbX, h.KbY));   // rules v24 (ED-1)
                SetKnockback(D, launch.Vx, launch.Vy);
            }
            GainUlt(D, ChibiSimConst.UltWhenHit);

            // OnHitLanded (F:630-649)
            A.HitsLanded++;
            A.ComboHits++;
            A.ComboTimer = 66;
            if (A.ComboHits > A.MaxCombo) A.MaxCombo = A.ComboHits;   // F:636
            if (A.ComboHits % A.Stats.PatternLength == 0) A.CombosLanded++;
            int gain = ChibiSimConst.UltPerHit + (A.ComboHits >= 3 ? ChibiSimConst.UltPerComboHit - ChibiSimConst.UltPerHit : 0);
            if (A.PctM >= ChibiPercentConst.ComebackPctM) gain *= 2;   // rules v20: comeback at 100 % (was under 30 % HP)
            GainUlt(A, gain);
            if (A.Stats.LifestealPermille > 0)              // rules v20: lifesteal lowers the attacker's percent, floor 0
                A.PctM = Math.Max(0, A.PctM - Pct(ChibiCombatStats.LifestealMicro(dmgU, A.Stats.LifestealPermille)));
            if (A.Stats.OnHitHexPermille > 0 && s.Rng.Roll(A.Stats.OnHitHexPermille))
            { D.SlowTicks = D.WeakenTicks = D.SluggishTicks = ChibiCombatStats.HexDuration; }
            if (A.Stats.OnHitArmorBreakPermille > 0 && s.Rng.Roll(A.Stats.OnHitArmorBreakPermille))
                D.ArmorBreakTicks = ChibiCombatStats.ArmorBreakDuration;
            return HitNormal;
        }

        /// <summary>Hitstop stacking (MC:177): HitstopRealTicks = max(current, n real ticks).</summary>
        static void Hitstop(ChibiSimState s, int n)
        {
            if (s.HitstopRealTicks < n) s.HitstopRealTicks = n;
        }

        /// <summary>
        /// Original Interrupt() (F:670-683): stop move and dash, drop the shield, release the fighter THIS one holds. Keeps
        /// ParryTicks / GuardTicks and its own Held flag (fact 9). The single cancel path (TD amendment 4): reflect, stun
        /// hits, StunF, the UpdateFighter stun branch and ring-out. Safe to call twice.
        /// </summary>
        static void Interrupt(ChibiSimState s, int i)
        {
            var f = s.F[i];
            f.MoveSlot = -1; f.MoveLockEnd = 0; f.DashTicks = 0; f.Shielding = false; f.ShieldTick = 0;   // actionLock = 0 (F:673)
            f.BufferedEdges = 0; f.BufferTicks = 0; f.AttackLockTicks = 0;   // rules v22: hitstun / ult start drop the buffer
            f.ClearMoveRuntime();   // rules v19 SEC-106: charge / riposte / hold / landing hit / pull end with the move
            Release(s, i);
        }

        /// <summary>Original Stun(t) (F:663-668): stun = max, Interrupt, Vx = 0, Vy = min(0, Vy); no velocity change while held.</summary>
        static void StunF(ChibiSimState s, int i, int ticks)
        {
            var f = s.F[i];
            f.Stun = Math.Max(f.Stun, ticks);
            Interrupt(s, i);
            f.ClearLaunch();                             // rules v20: a set stun is not a launch
            if (f.Held) return;
            f.Vx = 0;
            if (f.Vy > 0) f.Vy = 0;
        }

        /// <summary>Sets velocity to the knockback only when |kb|² exceeds <see cref="ChibiSimConst.KbMinSq"/> (F:611-628); never while held (F:619).</summary>
        static void SetKnockback(ChibiFighterSim f, int vx, int vy)
        {
            if (f.Held) return;
            if ((long)vx * vx + (long)vy * vy > ChibiSimConst.KbMinSq) { f.Vx = vx; f.Vy = vy; }
        }

        static void GainUlt(ChibiFighterSim f, int amount)
        {
            f.Ult = Math.Min(ChibiSimConst.UltMax, f.Ult + amount);
        }

        // ------------------------------------------------------------------ bounds / match end

        // ====================================================================================== rules v13: summons

        /// <summary>floor(<paramref name="v"/> / 1000): cloud micro position to milli (D-C5 = A).</summary>
        public static int FloorDiv1000(int v) => v >= 0 ? v / 1000 : (int)(-((-(long)v + 999) / 1000));

        /// <summary>sign(n) x ((|n| + d / 2) / d): the cloud's per-tick lerp step (section 2.2, D-C5 = A).</summary>
        static long RoundHalfAway(long n, long d) => n >= 0 ? (n + d / 2) / d : -((-n + d / 2) / d);

        /// <summary>
        /// SummonMinions (F:886-892, section 1.1): 3 minions at Center + (Facing (0.6 + 0.5k), 0.3) for k = 0, 1, 2, each in
        /// the lowest free slot (slot order = k order). Writes every field (TD amendment 7). A full pool drops the minion
        /// and counts it in DroppedSummons. SpawnTick = GameTicks, so the first update is the next game step.
        /// </summary>
        static void SpawnMinions(ChibiSimState s, int i)
        {
            var f = s.F[i];
            int y = f.Y + ChibiSimConst.CenterOffY + ChibiSimConst.MinionSpawnOy;
            for (int k = 0; k < ChibiSimConst.MinionCount; k++)
            {
                ChibiMinionSim m = null;
                for (int n = 0; n < s.Minions.Length; n++) if (!s.Minions[n].Active) { m = s.Minions[n]; break; }
                if (m == null) { s.DroppedSummons++; continue; }
                m.Active = true; m.Owner = i;
                m.X = f.X + f.Facing * (ChibiSimConst.MinionSpawnOx + ChibiSimConst.MinionSpawnStep * k); m.Y = y;
                m.XRem = 0; m.YRem = 0; m.Vx = 0; m.Vy = 0;
                m.Life = ChibiSimConst.MinionLife; m.AttackCd = ChibiSimConst.MinionFirstCd; m.Facing = 1;
                m.SpawnTick = s.GameTicks;
            }
        }

        /// <summary>SummonCloud (F:894-899, section 2.1): one cloud at Center + 3 up (micro units), lowest free slot; writes every field.</summary>
        static void SpawnCloud(ChibiSimState s, int i)
        {
            var f = s.F[i];
            ChibiCloudSim c = null;
            for (int n = 0; n < s.Clouds.Length; n++) if (!s.Clouds[n].Active) { c = s.Clouds[n]; break; }
            if (c == null) { s.DroppedSummons++; return; }
            c.Active = true; c.Owner = i;
            c.CX = f.X * 1000; c.CY = (f.Y + ChibiSimConst.CenterOffY + ChibiSimConst.CloudSpawnOy) * 1000;
            c.Life = ChibiSimConst.CloudLife; c.StrikeTicks = ChibiSimConst.CloudFirstStrike; c.BoltTicks = 0;
            c.SpawnTick = s.GameTicks;
        }

        /// <summary>
        /// One update of every minion, slots in index order (CO:149-171, section 1.2): Life / cd, despawn (Life or centre
        /// below -20 u, checked BEFORE Physics on the start-of-update position), chase the opponent's Center, bonk + hop
        /// (cd and hop happen even when the hit misses, is parried or blocked), then <see cref="MinionPhysics"/>.
        /// The bonk is a normal ApplyHit from the owner (D-C4 = A: parry / perfect shield punish the Mage).
        /// </summary>
        static void UpdateMinions(ChibiSimState s)
        {
            var st = ChibiStageTable.Get(s.Stage);
            for (int n = 0; n < s.Minions.Length; n++)
            {
                var m = s.Minions[n];
                if (!m.Active || m.SpawnTick == s.GameTicks) continue;   // first update on the next game step
                m.Life--; m.AttackCd--;
                if (m.Life <= 0 || m.Y < ChibiSimConst.MinionKillY) { m.Active = false; continue; }

                int ti = Foe(s, m.Owner);                                 // nearest enemy (1v1: the opponent)
                var t = s.F[ti];
                int dx = t.X - m.X, dy = t.Y + ChibiSimConst.CenterOffY - m.Y;
                int dir = dx >= 0 ? 1 : -1;                               // Mathf.Sign(0) = +1
                m.Facing = dir;
                int adx = Math.Abs(dx);
                m.Vx = adx > ChibiSimConst.MinionStopDx ? dir * ChibiSimConst.MinionWalkSub : 0;
                if (adx < ChibiSimConst.MinionBonkDx && Math.Abs(dy) < ChibiSimConst.MinionBonkDy && m.AttackCd <= 0)
                {
                    m.AttackCd = ChibiSimConst.MinionCd;
                    ApplyHit(s, m.Owner, ti, new PendingHit
                    {
                        Valid = true, Mult = ChibiSimConst.MinionMult, KbX = ChibiSimConst.MinionKb, KbY = ChibiSimConst.MinionKb,
                        Stun = ChibiSimConst.MinionStun, Dir = dir,
                    });
                    m.Vx = -dir * ChibiSimConst.MinionHopVx; m.Vy = ChibiSimConst.MinionHopVy;
                }
                MinionPhysics(m, st, s.GameTicks);
            }
        }

        /// <summary>True when the circle's bottom sits exactly on a surface top and its centre is over that surface (D-C2 = A).</summary>
        static bool MinionResting(ChibiMinionSim m, ChibiStageDef st, int t)
        {
            const int r = ChibiSimConst.MinionR;
            if (m.YRem != 0) return false;                                  // exact contact only (landing sets YRem 0)
            if (m.Y == r && Math.Abs(m.X) < st.HalfWidth) return true;
            if (st.HasFloor && m.Y == st.FloorY + r && Math.Abs(m.X) < st.FloorHalfW) return true;   // v21 floor
            var plats = st.Platforms;
            for (int p = 0; p < plats.Length; p++)
                if (m.Y == plats[p].TopAt(t) + r && Math.Abs(m.X - plats[p].CxAt(t)) < plats[p].HalfW) return true;
            return false;
        }

        /// <summary>
        /// Minion body (section 1.3, D-C1 = A, D-C2 = A, TD amendments 1-3): gravity x3 with its own C50 arc term, ground
        /// friction (3532 sub per tick while resting), wall cling against the main stage side (Vy toward 0 by 17500, no arc,
        /// while the circle presses into the side with Vx), the solid main stage (top landing with the centre over the box,
        /// underside, side push-out to HW + r) and the one-way platforms (land only falling from above, centre over the slab).
        /// Landing-step friction (amendment 3): on a landing from the air, Box2D's normal impulse cancels the fall speed, so
        /// friction removes up to 0.4 x |Vy| of Vx for that step; the landing tick's x travel is re-integrated with it.
        /// </summary>
        static void MinionPhysics(ChibiMinionSim m, ChibiStageDef st, int t)
        {
            const int r = ChibiSimConst.MinionR;
            int hw = st.HalfWidth, wallX = hw + r, bottom = st.MainBottom;
            int prevX = m.X, prevY = m.Y, prevXRem = m.XRem;
            bool resting = MinionResting(m, st, t);
            bool airborne = !resting || m.Vy > 0;
            m.Vy -= ChibiSimConst.MinionGravity;
            if (resting && m.Vy <= 0) m.Vx = Approach(m.Vx, 0, ChibiSimConst.MinionFrictionSub);
            Integrate(ref m.X, ref m.XRem, m.Vx);

            // Wall cling (amendment 2): circle beside the box (below the top, above the underside) pressing into the side.
            int side = prevX < 0 ? -1 : 1;
            bool cling = prevY - r < 0 && prevY + r > bottom && Math.Abs(prevX) >= hw && Math.Abs(m.X) < wallX
                         && m.Vx * side < 0;
            if (cling)
            {
                m.Vy = Approach(m.Vy, 0, ChibiSimConst.MinionWallFrictionSub);
                Integrate(ref m.Y, ref m.YRem, m.Vy);
            }
            else Integrate(ref m.Y, ref m.YRem, airborne ? m.Vy - ChibiSimConst.MinionArc : m.Vy);
            if (!st.KoSides)                                                // v20 side walls (circle inside the wall plane)
            {
                int mw = st.BlastX - r;
                if (m.X > mw) { m.X = mw; m.XRem = 0; if (m.Vx > 0) m.Vx = 0; }
                else if (m.X < -mw) { m.X = -mw; m.XRem = 0; if (m.Vx < 0) m.Vx = 0; }
            }

            // Solid main stage, box x in [-HW, HW], y in [MainBottom, 0].
            int landTop = int.MinValue;
            // Crossing tests use the exact position Y + YRem/VelSub (the bottom must really reach the top, R1: 679146 sub
            // after 13 ticks is not yet 679500), so a floor-truncated Y == top + r with YRem > 0 is still airborne.
            if (m.Vy <= 0 && prevY - r >= 0 && Below(m, r) && Math.Abs(m.X) < hw) landTop = 0;        // top
            else if (Math.Abs(m.X) < wallX && m.Y - r < 0 && m.Y + r > bottom)
            {
                if (prevY + r <= bottom)                                                                 // underside
                {
                    m.Y = bottom - r; m.YRem = 0;
                    if (m.Vy > 0) m.Vy = 0;
                }
                else if (Math.Abs(m.X) >= hw || Math.Abs(prevX) >= hw)                                   // side push-out
                {
                    int sd = m.X < 0 ? -1 : 1;
                    m.X = sd * wallX; m.XRem = 0;
                    if (m.Vx * sd < 0) m.Vx = 0;
                }
                else landTop = 0;                                                                        // fallback
            }

            // One-way platforms: land only when the bottom crosses the top from above while falling, centre over the slab.
            var plats = st.Platforms;
            for (int p = 0; p < plats.Length; p++)
            {
                int top = plats[p].TopAt(t);
                int from = plats[p].Axis == ChibiPlatform.AxisY ? Math.Min(top, plats[p].TopAt(t - 1)) : top;
                if (m.Vy <= 0 && prevY - r >= from && Below(m, top + r) && top > landTop &&
                    Math.Abs(m.X - plats[p].CxAt(t)) < plats[p].HalfW)
                    landTop = top;
            }
            if (st.HasFloor && m.Vy <= 0 && prevY - r >= st.FloorY && Below(m, st.FloorY + r) && st.FloorY > landTop &&
                Math.Abs(m.X) < st.FloorHalfW) landTop = st.FloorY;                                         // v21 floor
            if (landTop != int.MinValue)
            {
                if (!resting)
                {
                    // Landing-step friction (amendment 3): this step's x travel uses the reduced Vx.
                    int red = (int)((long)Math.Abs(m.Vy) * ChibiSimConst.MinionLandFricPermille / 1000);
                    int vx = Approach(m.Vx, 0, red);
                    if (vx != m.Vx)
                    {
                        m.Vx = vx; m.X = prevX; m.XRem = prevXRem;
                        Integrate(ref m.X, ref m.XRem, m.Vx);
                    }
                }
                m.Y = landTop + r; m.YRem = 0; m.Vy = 0;
            }
        }

        /// <summary>True when the exact centre height Y + YRem/VelSub is at or below <paramref name="y"/> milli.</summary>
        static bool Below(ChibiMinionSim m, int y) => m.Y < y || (m.Y == y && m.YRem == 0);

        /// <summary>
        /// One update of every Thunder Cloud, slots in index order (CO:199-220, section 2.2): Life (despawn before the strike
        /// check, so at most 4 strikes), follow the opponent's Center + 3.2 up by 1/24 of the gap in micro-units, bolt timer,
        /// strike timer (assignment, no drift). The strike is x-only, |T.X - X| &lt; 750 milli after this tick's follow with
        /// X = floor(CX / 1000), and resolves through the normal ApplyHit from the owner (section 2.3, D-C4 = A).
        /// </summary>
        static void UpdateClouds(ChibiSimState s)
        {
            for (int n = 0; n < s.Clouds.Length; n++)
            {
                var c = s.Clouds[n];
                if (!c.Active || c.SpawnTick == s.GameTicks) continue;   // first update on the next game step
                c.Life--;
                if (c.Life <= 0) { c.Active = false; continue; }

                int ti = Foe(s, c.Owner);                                 // nearest enemy (1v1: the opponent)
                var t = s.F[ti];
                long gx = (long)t.X * 1000, gy = ((long)t.Y + ChibiSimConst.CenterOffY + ChibiSimConst.CloudGoalOy) * 1000;
                c.CX += (int)RoundHalfAway(gx - c.CX, ChibiSimConst.CloudLerpDiv);
                c.CY += (int)RoundHalfAway(gy - c.CY, ChibiSimConst.CloudLerpDiv);

                if (c.BoltTicks > 0) c.BoltTicks--;
                c.StrikeTicks--;
                if (c.StrikeTicks <= 0)
                {
                    c.StrikeTicks = ChibiSimConst.CloudPeriod;
                    c.BoltTicks = ChibiSimConst.CloudBolt;
                    if (Math.Abs(t.X - c.MilliX) < ChibiSimConst.CloudStrikeDx)
                        ApplyHit(s, c.Owner, ti, new PendingHit
                        {
                            Valid = true, Mult = ChibiSimConst.CloudMult, KbX = 0, KbY = ChibiSimConst.CloudKbY,
                            Stun = ChibiSimConst.CloudStun, Dir = 1,
                        });
                }
            }
        }

        // Rules v20: ResolveBoundsAndMatchEnd and TimeoutWinner moved to ChibiSimulation.Percent.cs (stocks, KO edges).
    }
}
