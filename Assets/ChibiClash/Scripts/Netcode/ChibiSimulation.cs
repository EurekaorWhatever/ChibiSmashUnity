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
        public const int MatchTicks = 180 * TickRate;

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
        /// <summary>Respawn feet Y after a ring-out, the same on all three stages (0, 6) transform.</summary>
        public const int RespawnY = 7775;
        /// <summary>
        /// Projectile despawn bounds from the original (CombatObjects.cs:108): world |x| &gt; 40, y &lt; -30, y &gt; 40.
        /// Sim Y is relative to the stage top (world groundY = -2.5), so world y -30 / 40 become -27500 / 42500 milli.
        /// Same for every stage. Checked after the hit check, together with Life.
        /// </summary>
        public const int ProjBoundX = 40000, ProjBlastBottom = -27500, ProjBlastTop = 42500;
        public const int RingOutPenaltyPermille = 150;
        public const int RespawnInvuln = 90;

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
    }

    public struct ChibiMatchConfig
    {
        /// <summary>Bump whenever simulation rules change so old replays / mismatched peers are detected.</summary>
        /// <summary>Bump whenever simulation rules or move data change, so mismatched peers/replays are rejected.</summary>
        /// <remarks>2: Melee Dash &amp; Thrust matched to the original. 3: melee light/heavy hit areas matched to the original MeleeBox. 4: input message widened to 16 bits (Dash, gesture, PoseVerified). 5: movement matched to original (1/900 velocity units, C50 arc term, friction/fast-fall/air-block/jump-during-attack). 6: Dash (8 ticks, 33-tick cd, 6-tick invuln) and Dash &amp; Thrust end speed matched to original. 7: three stages, one-way platforms, drop-through, solid stage sides/underside, per-stage ring-out. 8: normal attacks matched to original (timing, kb, stun, combo, reload, projectiles, aura on heavy); no input buffer (D-N1). 9: damage rules matched to original (exact micro-HP, crit/defense/block/guard/parry/passives, roll order). 10: hitstop + ult cinematic via integer time accumulator (D10, A-held), GameTicks clock, replay v2.</remarks>
        /// <remarks>11: projectile kinds (projectile-conversion.md, port step 11): circle hit radius per kind, 2D constant-speed homing, spear return + SpearOut lock, growth / pierce / harmless flags, Thousand Arrows split machinery, destroy check from Center (strict), kb sign Vx &gt;= 0, 64-slot pool with DroppedShots; MaxProjectiles in the config hash.</remarks>
        public const int RulesVersion = 11;

        public uint Seed;
        public FighterClass Class0, Class1;
        public int Passive0, Passive1;
        /// <summary>
        /// Stage index: 0 Sky Dojo, 1 Neon City, 2 Lava Forge. Any other value plays as Sky Dojo (Stage.cs
        /// <c>default:</c>). Source: stage-conversion.md section 8.
        /// </summary>
        public int Stage;

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
            return h;
        }
    }

    // ====================================================================================== move data

    /// <summary>Frame data for one attack. Slots: 0 light, 1 heavy, 2-4 skills, 5 ultimate.</summary>
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

        public bool Exists => Active > 0;
        public const int FParry = 1, FUnblockable = 2, FCentered = 4, FAura = 8, FRing = 16, FHoming = 32,
                         FSpinGuard = 64, FTargeted = 128;
        /// <summary>The SelfVx window also forces Vy = 0 every tick, like the original's (vx, 0) writes (Fighter.cs:760).</summary>
        public const int FLevel = 256;
    }

    public static class ChibiMoveTable
    {
        static readonly ChibiMove[][] Table = Build();

        public static ChibiMove Get(int cls, int slot) => Table[cls][slot];

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
            const int P = ChibiMove.FParry, U = ChibiMove.FUnblockable, C = ChibiMove.FCentered, A = ChibiMove.FAura,
                      R = ChibiMove.FRing, G = ChibiMove.FSpinGuard, T = ChibiMove.FTargeted;
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
                Mv(0, 13, 11, 1600, 0, 1100, 75, 1200, 115, 65, 27, vx: 300, cd: 240, flags: L, ev: 50),
                Mv(3, 21, 6, flags: P, cd: 300),                                             // Parry (reflects damage)
                Mv(4, 36, 10, 550, 200, 1500, 100, 1100, 20, 15, 10, rehit: 6, cd: 420),     // Thrust Barrage (6 pulses)
                Mv(10, 1, 20, flags: A),                                                     // ULT Sword Aura (10 s wave buff)
            };
            t[(int)FighterClass.Heavy] = new[]
            {
                Nm(lms, 1000, 0, 1850, 25, 1300, 37500, 30000, 17, 105000, 105000, 27),
                Nm(hms, 1800, 0, 2083, 25, 1300, 120000, 90000, 30),
                Mv(8, 3, 24, 2200, 150, 1000, 100, 1200, 40, 150, 40, cd: 360, flags: U),    // Grab & smash
                Mv(6, 60, 14, 700, 0, 2400, 0, 1400, 50, 50, 15, rehit: 12, cd: 420, flags: C), // Tornado Spin (5 hits)
                new ChibiMove(),                                                             // (Heavy has only two skills)
                Mv(20, 4, 20, 3000, 0, 5000, 0, 900, 100, 200, 48, flags: C),                // ULT Smash shockwave
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
                Mv(8, 3, 20, 1200, 150, 1050, 100, 1300, 180, 80, 36, cd: 360),              // Bowstring choke
                // Arrow Barrage x5 (F:871, row 7): (500, 675 + yOff), 18 u/s, life 78, kb (1.2, 0.8), stun 0.18. RNG yOff + cadence: step 12.
                Shot(Mv(6, 25, 10, 600, rehit: 5, cd: 420), ChibiProjKind.Arrow, 250, PD, 500, 675, 270000, 78,
                     18000, 12000, 11),
                // ULT Thousand Arrows: horizontal stream until step 14 wires the lead + split (rows 12-13).
                Shot(Mv(14, 30, 20, 450, rehit: 3), ChibiProjKind.VolleyArrow, 250, PD, 450, ChibiSimConst.ProjectileY,
                     340 * ChibiSimConst.VelSub, 45, 25 * ChibiSimConst.VelSub, 17 * ChibiSimConst.VelSub, 9),
            };
            t[(int)FighterClass.Magic] = new[]
            {
                // Bolts r = size x 0.8 (F:479-484): light/finisher 0.35 -> 280, heavy 0.55 -> 440.
                Nm(lms, 1000, kx: 22500, ky: 18000, stun: 17, fkx: 63000, fky: 63000, fstun: 27, ps: 210000, pl: 72, ox: 600, oy: 925,
                   kind: ChibiProjKind.Bolt, r: 280),
                Nm(hms, 1800, kx: 72000, ky: 54000, stun: 30, ps: 210000, pl: 72, ox: 600, oy: 925, kind: ChibiProjKind.BoltBig, r: 440),
                // Homing Fireball (F:876-884, row 8): (600, 975), 9 u/s, r 380, life 3 s, kb (5, 4), stun 0.4, 2D homing.
                Shot(Mv(12, 1, 12, 1600, cd: 240), ChibiProjKind.Fireball, 380, PH | PD, 600, 975, 135000, 180,
                     75000, 60000, 24),
                // 3 seeking orbs (sim-only minion stand-in until step 13, D-P3 = A): real 2D homing, r 250.
                Shot(Mv(10, 25, 10, 350, rehit: 12, cd: 720), ChibiProjKind.SeekerOrb, 250, PH | PD, 450,
                     ChibiSimConst.ProjectileY, 110 * ChibiSimConst.VelSub, 300, 30 * ChibiSimConst.VelSub, 30 * ChibiSimConst.VelSub, 8),
                Mv(40, 3, 14, 1400, 0, 1400, 0, 3200, 0, 0, 20, cd: 600, flags: C | T),      // Thunder cloud strike (tracks during startup)
                Mv(12, 1, 20, flags: R),                                                     // ULT Fire Ring
            };
            t[(int)FighterClass.Polearm] = new[]
            {
                Nm(lms, 1000, 0, 2300, 25, 1300, 37500, 30000, 17, 105000, 105000, 27),
                Nm(hms, 1800, 0, 2600, 25, 1300, 120000, 90000, 30),
                Mv(22, 5, 14, 1800, 100, 1900, 0, 1200, 100, 150, 30, vx: 100, vy: 230, cd: 300), // Leap slam
                Mv(2, 70, 4, 300, 0, 3000, 0, 1200, 40, 35, 12, rehit: 18, cd: 360, flags: C | G), // Spin guard
                // Throw spear (F:935-950, row 9): (600, 775), 16 u/s, r 300, life 3 s, kb (5, 3), stun 0.35, !Destructible,
                // returns after 0.45 s at 0.8 (x Patience). Sets SpearOut on a successful spawn.
                Shot(Mv(10, 1, 14, 1400, cd: 360), ChibiProjKind.Spear, 300, 0, 600, 775, 240000, 180,
                     75000, 45000, 21, 27, 800),
                Mv(18, 4, 30, 4000, 150, 1500, 0, 1300, 140, 190, 72, flags: U),             // ULT Polearm kick
            };
            return t;
        }
    }

    // ====================================================================================== state

    /// <summary>Everything about one fighter that can change. Plain ints only, so copy and hash are trivial.</summary>
    public sealed class ChibiFighterSim
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
        /// <summary>HP in micro-HP (1 HP = <see cref="ChibiCombatStats.HpScale"/>), exact like the original's float HP (rules v9).</summary>
        public int HpU;
        /// <summary>
        /// Whole-HP compatibility accessor for display and older readers: get = ceil(HpU / HpScale) (like Hud.cs CeilToInt),
        /// set = whole HP. Not part of the rollback state itself (HpU is).
        /// </summary>
        public int Hp
        {
            get => HpU <= 0 ? 0 : (int)(((long)HpU + ChibiCombatStats.HpScale - 1) / ChibiCombatStats.HpScale);
            set => HpU = value * ChibiCombatStats.HpScale;
        }

        public int MoveSlot, MoveTick, MoveStartup, MoveActive, MoveRecovery, MoveMult, ShotIdx, TargetX;
        /// <summary>1 while the running light is the combo finisher (own kb/stun, reload on its shot), else 0 (rollback state).</summary>
        public int MoveFinisher;
        public bool MoveHit;
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

        /// <summary>
        /// A thrown spear is in flight (F:455, F:938-949; rules v11): light / heavy presses start nothing. Set only when the
        /// spear spawn succeeds (A1), cleared when that spear is removed for any reason. Ring-out keeps it (A7).
        /// </summary>
        public bool SpearOut;

        public void CopyFrom(ChibiFighterSim o)
        {
            Class = o.Class; Passive = o.Passive; Stats = o.Stats;
            X = o.X; Y = o.Y; Vx = o.Vx; Vy = o.Vy; XRem = o.XRem; YRem = o.YRem; Facing = o.Facing; Grounded = o.Grounded;
            AirJumps = o.AirJumps; HpU = o.HpU; Ult = o.Ult; Stun = o.Stun; Invuln = o.Invuln;
            MoveSlot = o.MoveSlot; MoveTick = o.MoveTick; MoveStartup = o.MoveStartup; MoveActive = o.MoveActive;
            MoveRecovery = o.MoveRecovery; MoveMult = o.MoveMult; MoveFinisher = o.MoveFinisher; ShotIdx = o.ShotIdx;
            TargetX = o.TargetX; MoveHit = o.MoveHit;
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
            SpearOut = o.SpearOut;
        }

        public void Hash(ref uint h)
        {
            ChibiSimState.Mix(ref h, Class); ChibiSimState.Mix(ref h, Passive);
            ChibiSimState.Mix(ref h, X); ChibiSimState.Mix(ref h, Y); ChibiSimState.Mix(ref h, Vx); ChibiSimState.Mix(ref h, Vy);
            ChibiSimState.Mix(ref h, XRem); ChibiSimState.Mix(ref h, YRem);
            ChibiSimState.Mix(ref h, Facing); ChibiSimState.Mix(ref h, Grounded ? 1 : 0);
            ChibiSimState.Mix(ref h, AirJumps); ChibiSimState.Mix(ref h, HpU); ChibiSimState.Mix(ref h, Ult);
            ChibiSimState.Mix(ref h, Stun); ChibiSimState.Mix(ref h, Invuln);
            ChibiSimState.Mix(ref h, MoveSlot); ChibiSimState.Mix(ref h, MoveTick); ChibiSimState.Mix(ref h, MoveStartup);
            ChibiSimState.Mix(ref h, MoveActive); ChibiSimState.Mix(ref h, MoveRecovery); ChibiSimState.Mix(ref h, MoveMult);
            ChibiSimState.Mix(ref h, MoveFinisher); ChibiSimState.Mix(ref h, ShotIdx); ChibiSimState.Mix(ref h, TargetX);
            ChibiSimState.Mix(ref h, MoveHit ? 1 : 0);
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
        }
    }

    /// <summary>
    /// Projectile kind (projectile-conversion.md section 6): selects the view sprite and the two behaviour hooks
    /// (Spear: OnGone clears SpearOut; VolleyLead: OnExpire splits). Everything else is data on the projectile.
    /// </summary>
    public enum ChibiProjKind
    {
        Arrow, ArrowBig, Bolt, BoltBig, AuraWave, Fireball, Spear, SmashWave, VolleyLead, VolleyArrow, PoseFireball, SeekerOrb
    }

    /// <summary>One projectile pool slot (CombatObjects.Projectile, rules v11). Plain ints only; copied and hashed whole.</summary>
    public sealed class ChibiProjectileSim
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
        }
    }

    /// <summary>
    /// The complete rollback state. Rolling back is "CopyFrom(snapshot)"; there is no hidden state anywhere else
    /// (no statics, no Unity objects, no floats, no UnityEngine.Random), which is what makes both peers agree.
    /// </summary>
    public sealed class ChibiSimState
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

        public readonly ChibiFighterSim[] F = { new ChibiFighterSim(), new ChibiFighterSim() };
        public readonly ChibiProjectileSim[] Proj = new ChibiProjectileSim[ChibiSimConst.MaxProjectiles];

        public ChibiSimState()
        {
            for (int i = 0; i < Proj.Length; i++) Proj[i] = new ChibiProjectileSim();
        }

        public void CopyFrom(ChibiSimState o)
        {
            Frame = o.Frame; Rng = o.Rng; MatchOver = o.MatchOver; Winner = o.Winner; Stage = o.Stage;
            HitstopRealTicks = o.HitstopRealTicks; CinematicRealTicks = o.CinematicRealTicks; CinematicFighter = o.CinematicFighter;
            TimeAccum = o.TimeAccum; GameTicks = o.GameTicks; RealTicksSinceStep = o.RealTicksSinceStep;
            DroppedShots = o.DroppedShots;
            F[0].CopyFrom(o.F[0]); F[1].CopyFrom(o.F[1]);
            for (int i = 0; i < Proj.Length; i++) Proj[i].CopyFrom(o.Proj[i]);
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
            }
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
    public static class ChibiSimulation
    {
        struct PendingHit
        {
            public bool Valid, Unblockable;
            public int Mult, KbX, KbY, Stun, Dir;
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
            s.Stage = ChibiStageTable.Normalize(cfg.Stage);
            var st = ChibiStageTable.Get(s.Stage);
            // Spawn transform (+-halfWidth*0.45, groundY+1.5) -> feet (+-SpawnX, 775), airborne (stage-conversion.md section 1).
            InitFighter(s.F[0], cfg.Class0, cfg.Passive0, -st.SpawnX, st.SpawnY, 1);
            InitFighter(s.F[1], cfg.Class1, cfg.Passive1, st.SpawnX, st.SpawnY, -1);
        }

        static void InitFighter(ChibiFighterSim f, FighterClass cls, int passive, int x, int y, int facing)
        {
            f.Class = (int)cls;
            f.Passive = passive == 0 ? 0 : 1;
            f.Stats = ChibiCombatStats.Create(cls, passive);
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Facing = facing; f.Grounded = false; f.AirJumps = 1;
            f.HpU = f.Stats.MaxHp * ChibiCombatStats.HpScale; f.Ult = 0; f.Stun = 0; f.Invuln = 0;
            f.MoveSlot = -1; f.MoveTick = 0; f.MoveStartup = 0; f.MoveActive = 0; f.MoveRecovery = 0;
            f.MoveMult = 0; f.MoveFinisher = 0; f.ShotIdx = 0; f.TargetX = 0; f.MoveHit = false;
            f.ComboIdx = 0; f.ComboWindow = 0; f.ComboHits = 0; f.ComboTimer = 0;
            f.Shielding = false; f.ShieldTick = 0; f.ParryTicks = 0; f.GuardTicks = 0;
            f.Cd[0] = f.Cd[1] = f.Cd[2] = 0;
            f.SlowTicks = f.WeakenTicks = f.SluggishTicks = f.ArmorBreakTicks = 0;
            f.TailwindTicks = 0; f.TailwindCd = 0; f.AuraTicks = 0; f.RingTicks = 0; f.SinceAttackTicks = ChibiSimConst.PatienceInit; f.SetCd = 0;
            f.GuardU = ChibiCombatStats.GuardMaxU(f.Stats.Defense); f.GuardBrokenTicks = 0;
            f.PrevInput = 0; f.LatchedEdges = 0; f.LatchedHeld = 0;
            f.HitsLanded = f.CombosLanded = f.UltsUsed = f.Parries = 0;
            f.DashTicks = 0; f.DashCd = 0; f.DashDir = 0;
            f.DropTicks = 0; f.DropPlatform = -1;
            f.SpearOut = false;
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
            if (s.MatchOver) { s.Frame++; return; }

            // ---- real-tick part
            in0 = in0.Sanitized();
            in1 = in1.Sanitized();
            Latch(s.F[0], in0.Raw);
            Latch(s.F[1], in1.Raw);
            int scale = CurrentScalePermille(s);
            RealTickTimers(s);
            s.TimeAccum += scale;

            // ---- game-step part
            if (s.TimeAccum >= ChibiSimConst.ScaleNormal)
            {
                s.TimeAccum -= ChibiSimConst.ScaleNormal;
                GameStep(s, in0, in1);
                s.GameTicks++;
                s.RealTicksSinceStep = 0;
            }
            else s.RealTicksSinceStep++;
            s.Frame++;
        }

        /// <summary>D-N1 latch (section 8.2): OR this real tick's press edges and Block level into the fighter's latch.</summary>
        static void Latch(ChibiFighterSim f, int raw)
        {
            f.LatchedEdges |= raw & ~f.PrevInput;
            f.LatchedHeld |= raw & (int)ChibiButtons.Shield;
            f.PrevInput = raw;
        }

        /// <summary>
        /// Every unscaled (real-time) timer, decremented once per real tick before the game step (section 8.3 item 4).
        /// Future unscaled timers (ult gesture window, ultRetryCd, prompt) belong HERE, not in the game step.
        /// </summary>
        static void RealTickTimers(ChibiSimState s)
        {
            Dec(ref s.HitstopRealTicks);
            Dec(ref s.CinematicRealTicks);
            if (s.CinematicRealTicks == 0) s.CinematicFighter = -1;
        }

        /// <summary>
        /// One game step: the v9 body. Each fighter acts on this real tick's raw | LatchedHeld and on LatchedEdges, then
        /// both latches are cleared whether the step used them or not (D-N1: never carried to a second step).
        /// </summary>
        static void GameStep(ChibiSimState s, ChibiInputPacket in0, ChibiInputPacket in1)
        {
            UpdateFighter(s, 0, ConsumeLatch(s.F[0], in0, out int e0), e0);
            UpdateFighter(s, 1, ConsumeLatch(s.F[1], in1, out int e1), e1);

            // Probe both melee hitboxes first, then apply: simultaneous swings trade.
            PendingHit h0 = MeleeProbe(s, 0);
            PendingHit h1 = MeleeProbe(s, 1);
            if (h0.Valid) ApplyHit(s, 0, 1, h0);
            if (h1.Valid) ApplyHit(s, 1, 0, h1);

            TickFireRings(s);
            UpdateProjectiles(s);
            ResolveBoundsAndMatchEnd(s);
        }

        /// <summary>Builds the step input (raw | LatchedHeld), returns the latched edges and clears both latches.</summary>
        static ChibiInputPacket ConsumeLatch(ChibiFighterSim f, ChibiInputPacket input, out int edges)
        {
            edges = f.LatchedEdges;
            var p = new ChibiInputPacket { Raw = (ushort)(input.Raw | f.LatchedHeld) };
            f.LatchedEdges = 0;
            f.LatchedHeld = 0;
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
        static void UpdateFighter(ChibiSimState s, int i, ChibiInputPacket input, int edges)
        {
            var f = s.F[i];
            var st = ChibiStageTable.Get(s.Stage);

            // ---- timers
            Dec(ref f.Stun); Dec(ref f.Invuln); Dec(ref f.ComboWindow);
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
            if (f.ComboTimer > 0 && --f.ComboTimer == 0) f.ComboHits = 0;
            if (f.Stats.TailwindCooldown > 0)
            {
                if (f.TailwindCd > 0) f.TailwindCd--;
                else { f.TailwindTicks = f.Stats.TailwindDuration; f.TailwindCd = f.Stats.TailwindCooldown; }
            }

            // D-N1 = A (normals-conversion.md section 5, Fighter.cs:318-333): no input buffer. Actions read this tick's
            // press edges in FreeActions only, so a press during a move, dash, hitstun or reload is lost.

            // Doc 5.1 / Fighter.cs:179-189: no x-velocity change at all during hitstun (knockback keeps its speed).
            // The 30 u/s² DisabledFriction branch (grounded, not stunned, cannot act) has no reachable state here:
            // the sim has no intro/pause, and a KO ends the match before the next Step.
            bool level = false;                          // original forces vy = 0 this tick (doubled 50 Hz arc term)
            bool dashing = f.Stun == 0 && f.DashTicks > 0;
            if (f.Stun > 0)
            {
                // Interrupt() keeps parryUntil / spinGuardUntil (F:670-677): ParryTicks and GuardTicks are NOT cleared (section 8.5).
                f.MoveSlot = -1; f.Shielding = false; f.ShieldTick = 0;
            }
            else if (dashing)
            {
                // Dash motion (dash-conversion.md section 3, Fighter.cs:268-272): velocity forced to (DashDir·17, 0);
                // no shield, movement, facing, jump, fast-fall, attack or dash start. A running attack keeps its
                // timeline and hits, and its SelfVx/SelfVy writes override the dash (coroutines run after Update).
                f.Vx = f.DashDir * ChibiSimConst.DashSpeedSub;
                f.Vy = 0;
                f.Shielding = false; f.ShieldTick = 0;
                if (f.MoveSlot >= 0) AdvanceMove(s, i, true);
                level = f.Vy == 0;                       // false only if a move's SelfVy launch overrode the dash
            }
            else if (f.MoveSlot >= 0)
            {
                level = AdvanceMove(s, i, false);
                // Doc 5.9 / Fighter.cs:291-307: jump and fast-fall sit outside the actionLock gate.
                JumpAndFastFall(f, st, input.MoveY, edges);
                // Dash start is outside the actionLock gate too (Fighter.cs:309): back-dash keeps Facing.
                TryDashStart(f, input, edges);
            }
            else
            {
                FreeActions(s, i, input, edges);
            }

            Physics(f, level, st);
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
            if (dirX != 0) f.Facing = dirX;
            JumpAndFastFall(f, ChibiStageTable.Get(s.Stage), dirY, edges);
            TryDashStart(f, input, edges);               // after jump, before attacks; never while shielding (returned above)

            // Priority: ultimate > skills > light > heavy, from this tick's press edges only (D-N1 = A). Light beats Heavy
            // (Fighter.cs:318-333); skills-vs-normals order is decided in step 12. The reload blocks normals only.
            int b = edges;
            if ((b & (int)ChibiButtons.Ultimate) != 0 && f.Ult >= ChibiSimConst.UltMax) { StartMove(s, i, 5); return; }
            for (int k = 0; k < 3; k++)
            {
                int bit = (int)ChibiButtons.Skill1 << k;
                if ((b & bit) != 0 && f.Cd[k] == 0 && ChibiMoveTable.Get(f.Class, 2 + k).Exists) { StartMove(s, i, 2 + k); return; }
            }
            bool reloading = f.SetCd > 0 && (f.Class == (int)FighterClass.Ranged || f.Class == (int)FighterClass.Magic);
            if (reloading) return;
            // Spear in flight (F:455): the normal press returns before combo / lastAttackTime, so it changes nothing.
            if (f.SpearOut) return;
            if ((b & (int)ChibiButtons.Light) != 0) StartMove(s, i, 0);
            else if ((b & (int)ChibiButtons.Heavy) != 0) StartMove(s, i, 1);
        }

        /// <summary>
        /// Jump (sets Vy) then fast-fall, in that order (doc section 2 step 1, Fighter.cs:291-307).
        /// Fast-fall: Vy = max(Vy - 45 u/s², -24 u/s) while Down is held, airborne and Vy &lt; 3 u/s.
        /// Drop-through (stage-conversion.md section 3, Fighter.cs:291-297,347-361): Jump with Down held while Grounded
        /// and the probe overlaps a platform consumes the jump and ignores that platform for 21 ticks. On the main stage
        /// no platform is found, so it is a normal ground jump.
        /// </summary>
        static void JumpAndFastFall(ChibiFighterSim f, ChibiStageDef st, int dirY, int edges)
        {
            if ((edges & (int)ChibiButtons.Jump) != 0)
            {
                int drop = dirY < 0 && f.Grounded ? ProbePlatform(f, st) : -1;
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

            f.MoveSlot = slot; f.MoveTick = 0; f.MoveHit = false; f.ShotIdx = 0; f.MoveFinisher = 0;
            f.MoveActive = mv.Active;
            if (slot <= 1)
            {
                // Normals: k_strike / k_lock from baseMs and multiplicative attack speed (normals-conversion.md section 1).
                ChibiCombatStats.NormalFrames(mv.BaseMs, ChibiCombatStats.AttackSpeedPpm(f.Stats, hexed),
                                              out f.MoveStartup, out f.MoveRecovery);
            }
            else
            {
                int sp = ChibiCombatStats.AttackSpeedPermille(f.Stats, hexed);
                f.MoveStartup = ChibiCombatStats.ScaleFrames(mv.Startup, sp);
                f.MoveRecovery = ChibiCombatStats.ScaleFrames(mv.Recovery, sp);
            }

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
            else if (slot == 1) { f.ComboIdx = 0; f.ComboWindow = 0; }

            // Patience (section 5): normals and skills only (not the ult); 180 ticks since the last start qualifies, 179 does not.
            if (slot <= 4)
            {
                if (f.Stats.PatienceTicks > 0 && f.SinceAttackTicks >= f.Stats.PatienceTicks && mult > 0)
                    mult = mult * (1000 + ChibiCombatStats.PatienceBonusPermille) / 1000;
                f.SinceAttackTicks = 0;
            }

            f.MoveMult = mult;
            if (slot >= 2 && slot <= 4) f.Cd[slot - 2] = mv.Cooldown;
            if (slot == 5)
            {
                // Ult cinematic (FireUltimate F:961 -> Cinematic MC:184-193): assignment, a second ult restarts the window.
                s.CinematicRealTicks = ChibiSimConst.CinematicTicks;
                s.CinematicFighter = i;
                f.Ult = 0; f.UltsUsed++;
                if (f.Invuln < 36) f.Invuln = 36;   // ultimate wind-up is protected
            }
            f.TargetX = s.F[1 - i].X;
        }

        /// <summary>
        /// Advances the running move one tick. <paramref name="dashing"/>: a dash is active, so the move's SelfVx/SelfVy/EndVx
        /// writes still override the dash velocity but MoveFriction is skipped. Returns true when the move forced Vy = 0
        /// this tick (FLevel window or EndVx tick), which selects the doubled arc term in <see cref="Physics"/>.
        /// </summary>
        static bool AdvanceMove(ChibiSimState s, int i, bool dashing)
        {
            var f = s.F[i];
            var o = s.F[1 - i];
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
                    f.MoveHit = false;
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
            if (f.MoveTick >= f.MoveStartup + f.MoveActive + f.MoveRecovery) f.MoveSlot = -1;
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
        static void Physics(ChibiFighterSim f, bool level, ChibiStageDef st)
        {
            int prevX = f.X, prevY = f.Y;
            bool airborne = !IsResting(f, st) || f.Vy > 0;
            f.Vy -= ChibiSimConst.Gravity;
            Integrate(ref f.X, ref f.XRem, f.Vx);
            int arc = level ? ChibiSimConst.LevelArcSub : ChibiSimConst.ArcC50;
            Integrate(ref f.Y, ref f.YRem, airborne ? f.Vy - arc : f.Vy);

            // ---- section 4: solid main stage, box x in [-HW, HW], y in [MainBottom, 0]
            int hw = st.HalfWidth, sup = hw + ChibiStageTable.SupportHalfW;
            int landTop = int.MinValue;
            bool overMain = Math.Abs(f.X) < sup;
            if (f.Vy <= 0 && prevY >= 0 && f.Y <= 0 && overMain) landTop = 0;                      // 1. top
            else if (overMain && f.Y < 0 && f.Y + ChibiSimConst.HurtH > ChibiStageTable.MainBottom)
            {
                if (prevY + ChibiSimConst.HurtH <= ChibiStageTable.MainBottom)                       // 2. underside bonk
                {
                    f.Y = ChibiStageTable.MainBottom - ChibiSimConst.HurtH; f.YRem = 0;
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

            // ---- section 2: one-way platforms; the highest qualifying top wins
            var plats = st.Platforms;
            for (int p = 0; p < plats.Length; p++)
            {
                if (p == f.DropPlatform) continue;
                int top = plats[p].Top;
                if (f.Vy <= 0 && prevY >= top && f.Y <= top && top > landTop &&
                    Math.Abs(f.X - plats[p].Cx) < plats[p].HalfW + ChibiStageTable.SupportHalfW)
                    landTop = top;
            }
            if (landTop != int.MinValue) { f.Y = landTop; f.YRem = 0; f.Vy = 0; }

            // ---- section 5 with D-S1: Grounded only when resting, i.e. feet exactly on a surface top (after the landing
            // snap) within the probe half-width 337.5 (doubled for exactness). The original's 100-milli band above the top
            // is not used: it reported Grounded mid-air while the fighter was still falling toward the surface.
            bool probe = 2 * Math.Abs(f.X) < 2 * hw + ChibiStageTable.ProbeHalfW2 && f.Y == 0;
            if (!probe) probe = ProbePlatform(f, st) >= 0;
            f.Grounded = probe && f.Vy <= ChibiSimConst.GroundProbeMaxVy;
            if (f.Grounded) f.AirJumps = 1;
        }

        /// <summary>
        /// Index of the first platform (not the dropped one) inside the ground probe, or -1. D-S1: the feet must rest
        /// exactly on the top, never above it or inside the slab (stage-conversion.md section 5).
        /// </summary>
        static int ProbePlatform(ChibiFighterSim f, ChibiStageDef st)
        {
            var plats = st.Platforms;
            for (int p = 0; p < plats.Length; p++)
            {
                if (p == f.DropPlatform) continue;
                int top = plats[p].Top;
                if (f.Y == top &&
                    2 * Math.Abs(f.X - plats[p].Cx) < 2 * plats[p].HalfW + ChibiStageTable.ProbeHalfW2)
                    return p;
            }
            return -1;
        }

        /// <summary>
        /// True when the feet sit exactly on a surface top within the support width (main stage or a platform that is
        /// not being dropped through). Gates the C50 arc term (decision D-S1, stage-conversion.md section 5 option 1).
        /// </summary>
        static bool IsResting(ChibiFighterSim f, ChibiStageDef st)
        {
            if (f.Y == 0 && Math.Abs(f.X) < st.HalfWidth + ChibiStageTable.SupportHalfW) return true;
            var plats = st.Platforms;
            for (int p = 0; p < plats.Length; p++)
                if (p != f.DropPlatform && f.Y == plats[p].Top &&
                    Math.Abs(f.X - plats[p].Cx) < plats[p].HalfW + ChibiStageTable.SupportHalfW) return true;
            return false;
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

        static PendingHit MeleeProbe(ChibiSimState s, int i)
        {
            var f = s.F[i];
            var o = s.F[1 - i];
            var none = default(PendingHit);
            if (f.MoveSlot < 0 || f.MoveHit) return none;
            var mv = ChibiMoveTable.Get(f.Class, f.MoveSlot);
            if (mv.ProjSpeed > 0 || mv.Mult <= 0) return none;

            int t = f.MoveTick - 1;   // the tick AdvanceMove just processed
            if (t < f.MoveStartup || t >= f.MoveStartup + f.MoveActive) return none;

            bool centered = (mv.Flags & ChibiMove.FCentered) != 0;
            int cx = (mv.Flags & ChibiMove.FTargeted) != 0 ? f.TargetX : f.X;
            int x0, x1;
            if (centered) { x0 = cx - mv.BoxW / 2; x1 = x0 + mv.BoxW; }
            else if (f.Facing > 0) { x0 = f.X + mv.BoxX; x1 = x0 + mv.BoxW; }
            else { x1 = f.X - mv.BoxX; x0 = x1 - mv.BoxW; }
            int y0 = f.Y + mv.BoxY, y1 = y0 + mv.BoxH;

            if (!Overlaps(o, x0, x1, y0, y1)) return none;

            f.MoveHit = true;
            int dir = centered ? (Sign(o.X - f.X) != 0 ? Sign(o.X - f.X) : f.Facing) : f.Facing;
            bool fin = f.MoveFinisher != 0;
            return new PendingHit
            {
                Valid = true, Mult = f.MoveMult, KbX = fin ? mv.FinKbX : mv.KbX, KbY = fin ? mv.FinKbY : mv.KbY,
                Stun = fin ? mv.FinStun : mv.Stun, Dir = dir, Unblockable = (mv.Flags & ChibiMove.FUnblockable) != 0
            };
        }

        static bool Overlaps(ChibiFighterSim target, int x0, int x1, int y0, int y1)
        {
            return x0 < target.X + ChibiSimConst.HurtHalfW && x1 > target.X - ChibiSimConst.HurtHalfW &&
                   y0 < target.Y + ChibiSimConst.HurtH && y1 > target.Y;
        }

        // ------------------------------------------------------------------ projectiles (rules v11, projectile-conversion.md)

        /// <summary>Spawn spec for <see cref="SpawnProjectile"/> (section 6). Positions milli; Vx/Vy/KbX/KbY sub-units.</summary>
#pragma warning disable CS0649   // GrowPerTick: written by the Smash wave spawn, wired in port step 14
        struct ProjSpec
        {
            public int Owner;
            public ChibiProjKind Kind;
            public int X, Y, Vx, Vy, Radius, Life, Mult, KbX, KbY, Stun, Flags, GrowPerTick, ReturnTicks, ReturnMult;
        }
#pragma warning restore CS0649

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
            for (int n = 0; n < s.Proj.Length; n++)
            {
                var p = s.Proj[n];
                if (!p.Active || p.SpawnFrame == s.GameTicks) continue;   // first update on the next game step
                var owner = s.F[p.Owner];
                var target = s.F[1 - p.Owner];

                p.Life--;                                                                         // CO:47
                if ((p.Flags & ChibiProjectileSim.FHoming) != 0)                                  // CO:51-55
                    SteerHoming(ref p.Vx, ref p.Vy, p.Speed, target.X - p.X, target.Y + cy - p.Y);
                if (p.ReturnTicks > 0)                                                            // CO:57-66
                {
                    p.ReturnTicks--;
                    if (p.ReturnTicks == 0 && (p.Flags & ChibiProjectileSim.FReturning) == 0)
                    {
                        p.Flags = (p.Flags | ChibiProjectileSim.FReturning) & ~ChibiProjectileSim.FHasHit;
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
                Integrate(ref p.X, ref p.XRem, p.Vx);                                             // CO:74
                Integrate(ref p.Y, ref p.YRem, p.Vy);
                p.Radius += p.GrowPerTick;                                                        // CO:77, CO:97

                if ((p.Flags & (ChibiProjectileSim.FHarmless | ChibiProjectileSim.FHasHit)) == 0) // CO:89
                {
                    if ((p.Flags & ChibiProjectileSim.FDestructible) != 0 && DestroyedBy(target, p.X, p.Y))
                    { Despawn(s, p); continue; }
                    if (CircleHitsFighter(target, p.X, p.Y, p.Radius))
                    {
                        // HasHit is set even if the hit misses (invulnerable / KO'd), CO:102-104; kb sign Vel.x >= 0 (CO:101).
                        p.Flags |= ChibiProjectileSim.FHasHit;
                        var hit = new PendingHit
                        {
                            Valid = true, Mult = p.Mult, KbX = ChibiFixed.Abs(p.KbX), KbY = p.KbY, Stun = p.Stun,
                            Dir = p.Vx >= 0 ? 1 : -1
                        };
                        ApplyHit(s, p.Owner, 1 - p.Owner, hit);
                        if ((p.Flags & (ChibiProjectileSim.FPierce | ChibiProjectileSim.FReturning)) == 0 && p.ReturnTicks == 0)
                        { Despawn(s, p); continue; }
                    }
                }
                // CO:108: Life <= 0 || |x| > 40 || y < -30 || y > 40 (world units).
                if (p.Life <= 0 || Math.Abs(p.X) > ChibiSimConst.ProjBoundX ||
                    p.Y < ChibiSimConst.ProjBlastBottom || p.Y > ChibiSimConst.ProjBlastTop)
                {
                    int lx = p.X, ly = p.Y;
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
        static void SplitVolley(ChibiSimState s, int owner, int lx, int ly)
        {
            var t = s.F[1 - owner];
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
            for (int i = 0; i < 2; i++)
            {
                var f = s.F[i];
                if (f.RingTicks <= 0 || f.RingTicks % 30 != 0) continue;
                var o = s.F[1 - i];
                int dx = o.X - f.X, dy = o.Y - f.Y;
                if ((long)dx * dx + (long)dy * dy > (long)ChibiSimConst.RingRadius * ChibiSimConst.RingRadius) continue;
                ApplyHit(s, i, 1 - i, new PendingHit { Valid = true, Mult = 400, KbX = 60 * ChibiSimConst.VelSub, KbY = 45 * ChibiSimConst.VelSub, Stun = 12, Dir = Sign(dx) != 0 ? Sign(dx) : f.Facing });
            }
        }

        // ------------------------------------------------------------------ damage

        /// <summary>
        /// Resolves one hit (damage-conversion.md sections 2-6, F:545-649). Fixed order and RNG draws:
        ///   0. match over / defender KO'd / invulnerable: nothing, 0 draws
        ///   1. Parry skill stance (not unblockable): reflect on the attacker, 1 draw (crit), stop
        ///   2. perfect shield parry (ShieldTick 0..10): 0 draws, stop
        ///   3. Ward (defender Magic P1): 1 draw, stop if it fires
        ///   4. crit: always 1 draw
        ///   5/6. Hex / Armor Break (attacker passive): 1 draw, only on a landed unblocked or guard-breaking hit.
        /// Damage is exact in nano-HP with one half-up rounding to micro-HP (decision D9-1 = a).
        /// </summary>
        static void ApplyHit(ChibiSimState s, int a, int d, PendingHit h)
        {
            var A = s.F[a];
            var D = s.F[d];
            // !Match.Running: a KO earlier this tick (e.g. h0 of a trade) ends the match before h1 resolves.
            if (s.MatchOver || A.HpU <= 0 || D.HpU <= 0 || D.Invuln > 0) return;
            if (ChibiCombatStats.HalfKnockback(A.Class)) { h.KbX /= 2; h.KbY /= 2; }

            // 1. Parry skill: the attack is turned around on the attacker (section 3b)
            if (D.ParryTicks > 0 && !h.Unblockable)
            {
                D.ParryTicks = 0;
                D.Parries++;
                GainUlt(D, ChibiSimConst.UltPerParry);
                long refN = ChibiCombatStats.RawDamageNano(A.Stats, h.Mult, A.WeakenTicks > 0, ref s.Rng, out _);
                int refU = ChibiCombatStats.FinalDamageMicro(refN, ChibiCombatStats.DefenseH(A.Stats, A.ArmorBreakTicks > 0), false);
                A.HpU = Math.Max(0, A.HpU - refU);
                A.Stun = Math.Max(A.Stun, ChibiSimConst.ReflectStun);
                Interrupt(A);
                A.Vx = (A.X >= D.X ? 1 : -1) * ChibiSimConst.ReflectPushX;   // Mathf.Sign(0) = +1, positions not hit Dir
                A.Vy = ChibiSimConst.ReflectPushY;
                GainUlt(A, ChibiSimConst.UltWhenHit);
                Hitstop(s, ChibiSimConst.HitstopReflect);   // F:559; the reflect's own ApplyDamage 3 is absorbed by max
                return;
            }

            // 2. Perfect parry (section 3a): no damage, no guard drain, no draws; attacker Stun() zeroes Vx, caps Vy.
            bool blocking = D.Shielding && !h.Unblockable;
            if (blocking && D.ShieldTick < ChibiSimConst.ParryWindow)
            {
                D.Parries++;
                GainUlt(D, ChibiSimConst.UltPerParry);
                StunF(A, ChibiSimConst.PerfectParryStun);
                Hitstop(s, ChibiSimConst.HitstopParry);     // F:570
                return;
            }

            // 3. Ward negates everything (section 5)
            if (D.Stats.WardPermille > 0 && s.Rng.Roll(D.Stats.WardPermille)) return;

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
            D.HpU = Math.Max(0, D.HpU - dmgU);
            // ApplyDamage F:625 HitStop(crit ? 0.08 : 0.045). Blocked chip also goes through ApplyDamage (held = true,
            // damage-conversion.md section 3 row 95), so it triggers too; Ward (returned above) does not.
            Hitstop(s, crit ? ChibiSimConst.HitstopCrit : ChibiSimConst.HitstopNormal);

            if (blocking)
            {
                // Chip: no stun, no Interrupt (shield stays up), kb x0.25 on X and Y (section 2)
                SetKnockback(D, h.Dir * (h.KbX / 4), h.KbY / 4);
                GainUlt(D, ChibiSimConst.UltWhenHit);
                GainUlt(A, 10);                             // OnHitLanded blocked: +1 ult and return
                return;
            }

            // ApplyDamage (section 4): stun + Interrupt only when stun > 0; velocity set only above the threshold
            if (stun > 0) { D.Stun = Math.Max(D.Stun, stun); Interrupt(D); }
            SetKnockback(D, h.Dir * h.KbX, h.KbY);
            GainUlt(D, ChibiSimConst.UltWhenHit);

            // OnHitLanded (F:630-649)
            A.HitsLanded++;
            A.ComboHits++;
            A.ComboTimer = 66;
            if (A.ComboHits % A.Stats.PatternLength == 0) A.CombosLanded++;
            int gain = ChibiSimConst.UltPerHit + (A.ComboHits >= 3 ? ChibiSimConst.UltPerComboHit - ChibiSimConst.UltPerHit : 0);
            if ((long)A.HpU * 10 < (long)A.Stats.MaxHp * ChibiCombatStats.HpScale * 3) gain *= 2;   // under 30 % HP
            GainUlt(A, gain);
            if (A.Stats.LifestealPermille > 0)
                A.HpU = (int)Math.Min((long)A.Stats.MaxHp * ChibiCombatStats.HpScale,
                                      A.HpU + ChibiCombatStats.LifestealMicro(dmgU, A.Stats.LifestealPermille));
            if (A.Stats.OnHitHexPermille > 0 && s.Rng.Roll(A.Stats.OnHitHexPermille))
            { D.SlowTicks = D.WeakenTicks = D.SluggishTicks = ChibiCombatStats.HexDuration; }
            if (A.Stats.OnHitArmorBreakPermille > 0 && s.Rng.Roll(A.Stats.OnHitArmorBreakPermille))
                D.ArmorBreakTicks = ChibiCombatStats.ArmorBreakDuration;
        }

        /// <summary>Hitstop stacking (MC:177): HitstopRealTicks = max(current, n real ticks).</summary>
        static void Hitstop(ChibiSimState s, int n)
        {
            if (s.HitstopRealTicks < n) s.HitstopRealTicks = n;
        }

        /// <summary>Original Interrupt() (F:670-677): stop move and dash, drop the shield. Keeps ParryTicks / GuardTicks.</summary>
        static void Interrupt(ChibiFighterSim f)
        {
            f.MoveSlot = -1; f.DashTicks = 0; f.Shielding = false; f.ShieldTick = 0;
        }

        /// <summary>Original Stun(t) (F:663-668): stun = max, Interrupt, Vx = 0, Vy = min(0, Vy).</summary>
        static void StunF(ChibiFighterSim f, int ticks)
        {
            f.Stun = Math.Max(f.Stun, ticks);
            Interrupt(f);
            f.Vx = 0;
            if (f.Vy > 0) f.Vy = 0;
        }

        /// <summary>Sets velocity to the knockback only when |kb|² exceeds <see cref="ChibiSimConst.KbMinSq"/> (F:611-628).</summary>
        static void SetKnockback(ChibiFighterSim f, int vx, int vy)
        {
            if ((long)vx * vx + (long)vy * vy > ChibiSimConst.KbMinSq) { f.Vx = vx; f.Vy = vy; }
        }

        static void GainUlt(ChibiFighterSim f, int amount)
        {
            f.Ult = Math.Min(ChibiSimConst.UltMax, f.Ult + amount);
        }

        // ------------------------------------------------------------------ bounds / match end

        static void ResolveBoundsAndMatchEnd(ChibiSimState s)
        {
            var st = ChibiStageTable.Get(s.Stage);
            for (int i = 0; i < 2; i++)
            {
                var f = s.F[i];
                // Strict bounds on feet coordinates; the ceiling is a ring-out, not a KO (stage-conversion.md section 6).
                // DropTicks / DropPlatform are deliberately kept (Fighter.cs:692-708 does not touch them).
                if (Math.Abs(f.X) > st.BlastX || f.Y < st.KillY || f.Y > st.CeilingY)
                {
                    // -15 % of max HP, exact in micro-HP (section 8.8): MaxHp x 150 000 µHP
                    f.HpU = (int)Math.Max(0L, f.HpU - (long)f.Stats.MaxHp * ChibiCombatStats.HpScale * ChibiSimConst.RingOutPenaltyPermille / 1000);
                    f.X = st.RespawnX; f.Y = st.RespawnY; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Stun = 0;
                    f.Grounded = false;
                    f.Invuln = ChibiSimConst.RespawnInvuln;
                    f.MoveSlot = -1; f.Shielding = false; f.ParryTicks = 0; f.GuardTicks = 0;
                    f.DashTicks = 0;                        // RingOut -> Interrupt(); DashCd is kept (Fighter.cs:692-708)
                }
            }

            bool ko0 = s.F[0].HpU <= 0, ko1 = s.F[1].HpU <= 0;
            if (ko0 || ko1)
            {
                s.MatchOver = true;
                s.Winner = ko0 && ko1 ? -1 : (ko0 ? 1 : 0);
            }
            else if (s.GameTicks + 1 >= ChibiSimConst.MatchTicks)   // match clock counts game steps (section 4)
            {
                s.MatchOver = true;
                long r0 = (long)s.F[0].HpU * 1000 / ((long)s.F[0].Stats.MaxHp * ChibiCombatStats.HpScale),
                     r1 = (long)s.F[1].HpU * 1000 / ((long)s.F[1].Stats.MaxHp * ChibiCombatStats.HpScale);
                s.Winner = r0 == r1 ? -1 : (r0 > r1 ? 0 : 1);
            }
        }
    }
}
