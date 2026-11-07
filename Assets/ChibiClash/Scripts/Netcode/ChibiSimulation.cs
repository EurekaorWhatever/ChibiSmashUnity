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
        /// Projectile despawn Y bounds, unchanged from rules v6 until the projectile step checks the original rule
        /// (stage-conversion.md section 7). The X bound is the stage's BlastX.
        /// </summary>
        public const int ProjBlastBottom = -9000, ProjBlastTop = 18000;
        public const int RingOutPenaltyPermille = 150;
        public const int RespawnInvuln = 90;

        public const int HurtHalfW = 375, HurtH = 1350;
        public const int ProjHalfW = 150, ProjHalfH = 100, ProjectileY = 700;
        public const int MaxProjectiles = 24;

        public const int UltMax = 1000;
        public const int UltPerHit = 30, UltPerComboHit = 50, UltWhenHit = 15, UltPerParry = 150;
        public const int ParryWindow = 11;        // ticks after raising the shield in which a block is a perfect parry
        public const int ComboWindowTicks = 54;   // 0.9 s to chain the next light attack
        public const int InputBufferTicks = 6;
        public const int AuraDuration = 600, RingDuration = 600, RingRadius = 2100, GuardRadius = 1600;
        public const int SetCooldownTicks = 90;   // archer / mage reload after a full set of 4 shots (1.5 s)

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
    }

    public struct ChibiMatchConfig
    {
        /// <summary>Bump whenever simulation rules change so old replays / mismatched peers are detected.</summary>
        /// <summary>Bump whenever simulation rules or move data change, so mismatched peers/replays are rejected.</summary>
        /// <remarks>2: Melee Dash &amp; Thrust matched to the original. 3: melee light/heavy hit areas matched to the original MeleeBox. 4: input message widened to 16 bits (Dash, gesture, PoseVerified). 5: movement matched to original (1/900 velocity units, C50 arc term, friction/fast-fall/air-block/jump-during-attack). 6: Dash (8 ticks, 33-tick cd, 6-tick invuln) and Dash &amp; Thrust end speed matched to original. 7: three stages, one-way platforms, drop-through, solid stage sides/underside, per-stage ring-out.</remarks>
        public const int RulesVersion = 7;

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
            return h;
        }
    }

    // ====================================================================================== move data

    /// <summary>Frame data for one attack. Slots: 0 light, 1 heavy, 2-4 skills, 5 ultimate.</summary>
    public struct ChibiMove
    {
        public int Startup, Active, Recovery;       // ticks; startup+recovery scale with attack speed
        public int Mult;                             // damage multiplier, permille of Attack (0 = no damage)
        public int BoxX, BoxW, BoxY, BoxH;           // melee hitbox: BoxX = near edge offset from fighter centre (toward facing)
        public int KbX, KbY, Stun;                   // knockback (milli/tick; x VelSub when applied) and hitstun ticks
        public int SelfVx, SelfVy;                   // self motion (milli/tick; x VelSub when applied); SelfVx applies through startup+active, SelfVy once at tick 0
        /// <summary>
        /// Self X speed (milli/tick, x VelSub, toward Facing) written once, with Vy = 0, on the first move tick after the
        /// SelfVx window (t == startup + active). 0 = none. Dash &amp; Thrust: 3 u/s (dash-conversion.md section 4).
        /// </summary>
        public int EndVx;
        public int ProjSpeed, ProjLife;              // >0 turns the hit into a projectile (speed milli/tick; x VelSub at spawn)
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
                KbX = kx, KbY = ky, Stun = stun, SelfVx = vx, SelfVy = vy, ProjSpeed = ps, ProjLife = pl,
                Rehit = rehit, Cooldown = cd, Flags = flags, EndVx = ev
            };
        }

        static ChibiMove[][] Build()
        {
            const int L = ChibiMove.FLevel;
            const int P = ChibiMove.FParry, U = ChibiMove.FUnblockable, C = ChibiMove.FCentered, A = ChibiMove.FAura,
                      R = ChibiMove.FRing, H = ChibiMove.FHoming, G = ChibiMove.FSpinGuard, T = ChibiMove.FTargeted;
            var t = new ChibiMove[5][];

            // Melee normals match Fighter.NormalStrike: MeleeBox(Def.reach * (heavy ? 1.15 : 1), 1.3) spans from the
            // fighter's centre to reach + 0.3 ahead, 1.3 tall around the body centre (0.675 above the feet).
            // Def.reach (ClassData.cs): Melee 1.35, Heavy 1.55, Polearm 2.0. Timing is ported separately (step 8).
            t[(int)FighterClass.Melee] = new[]
            {
                Mv(4, 3, 9, 1000, 0, 1650, 25, 1300, 45, 35, 16),                          // light thrust
                Mv(10, 4, 16, 1800, 0, 1853, 25, 1300, 130, 100, 30),                      // heavy slash
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
                Mv(6, 4, 12, 1000, 0, 1850, 25, 1300, 55, 40, 18),
                Mv(16, 5, 20, 1800, 0, 2083, 25, 1300, 140, 110, 32),
                Mv(8, 3, 24, 2200, 150, 1000, 100, 1200, 40, 150, 40, cd: 360, flags: U),    // Grab & smash
                Mv(6, 60, 14, 700, 0, 2400, 0, 1400, 50, 50, 15, rehit: 12, cd: 420, flags: C), // Tornado Spin (5 hits)
                new ChibiMove(),                                                             // (Heavy has only two skills)
                Mv(20, 4, 20, 3000, 0, 5000, 0, 900, 100, 200, 48, flags: C),                // ULT Smash shockwave
            };
            t[(int)FighterClass.Ranged] = new[]
            {
                Mv(5, 1, 10, 1000, kx: 45, ky: 30, stun: 10, ps: 300, pl: 45),
                Mv(12, 1, 18, 1800, kx: 110, ky: 80, stun: 24, ps: 350, pl: 50),
                Mv(9, 1, 12, 1500, kx: 80, ky: 50, stun: 24, vx: -280, ps: 300, pl: 45, cd: 240), // Backstep shot
                Mv(8, 3, 20, 1200, 150, 1050, 100, 1300, 180, 80, 36, cd: 360),              // Bowstring choke
                Mv(6, 25, 10, 600, kx: 20, ky: 15, stun: 9, ps: 300, pl: 40, rehit: 5, cd: 420), // Arrow Barrage
                Mv(14, 30, 20, 450, kx: 25, ky: 17, stun: 9, ps: 340, pl: 45, rehit: 3),     // ULT Thousand Arrows
            };
            t[(int)FighterClass.Magic] = new[]
            {
                Mv(6, 1, 12, 1000, kx: 40, ky: 30, stun: 14, ps: 230, pl: 50),
                Mv(14, 1, 20, 1800, kx: 100, ky: 70, stun: 26, ps: 200, pl: 60),
                Mv(12, 1, 12, 1600, kx: 85, ky: 65, stun: 24, ps: 150, pl: 150, cd: 240, flags: H), // Homing fireball
                Mv(10, 25, 10, 350, kx: 30, ky: 30, stun: 8, ps: 110, pl: 300, rehit: 12, cd: 720, flags: H), // 3 seeking orbs (minion stand-in)
                Mv(40, 3, 14, 1400, 0, 1400, 0, 3200, 0, 0, 20, cd: 600, flags: C | T),      // Thunder cloud strike (tracks during startup)
                Mv(12, 1, 20, flags: R),                                                     // ULT Fire Ring
            };
            t[(int)FighterClass.Polearm] = new[]
            {
                Mv(5, 3, 10, 1000, 0, 2300, 25, 1300, 45, 35, 16),
                Mv(12, 4, 18, 1800, 0, 2600, 25, 1300, 130, 100, 30),
                Mv(22, 5, 14, 1800, 100, 1900, 0, 1200, 100, 150, 30, vx: 100, vy: 230, cd: 300), // Leap slam
                Mv(2, 70, 4, 300, 0, 3000, 0, 1200, 40, 35, 12, rehit: 18, cd: 360, flags: C | G), // Spin guard
                Mv(10, 1, 14, 1400, kx: 85, ky: 50, stun: 21, ps: 270, pl: 45, cd: 360),     // Throw spear
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
        public int AirJumps, Hp, Ult, Stun, Invuln;

        public int MoveSlot, MoveTick, MoveStartup, MoveActive, MoveRecovery, MoveMult, MoveKbScale, ShotIdx, TargetX;
        public bool MoveHit;
        public int ComboIdx, ComboWindow, ComboHits, ComboTimer;

        public bool Shielding;
        public int ShieldTick, ParryTicks, GuardTicks;
        public readonly int[] Cd = new int[3];

        public int SlowTicks, WeakenTicks, SluggishTicks, ArmorBreakTicks;
        public int TailwindTicks, TailwindCd, AuraTicks, RingTicks, IdleTicks, SetCd;
        public int GuardHp10, GuardBrokenTicks;

        public int PrevInput, BufBtn, BufAge;

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

        public void CopyFrom(ChibiFighterSim o)
        {
            Class = o.Class; Passive = o.Passive; Stats = o.Stats;
            X = o.X; Y = o.Y; Vx = o.Vx; Vy = o.Vy; XRem = o.XRem; YRem = o.YRem; Facing = o.Facing; Grounded = o.Grounded;
            AirJumps = o.AirJumps; Hp = o.Hp; Ult = o.Ult; Stun = o.Stun; Invuln = o.Invuln;
            MoveSlot = o.MoveSlot; MoveTick = o.MoveTick; MoveStartup = o.MoveStartup; MoveActive = o.MoveActive;
            MoveRecovery = o.MoveRecovery; MoveMult = o.MoveMult; MoveKbScale = o.MoveKbScale; ShotIdx = o.ShotIdx;
            TargetX = o.TargetX; MoveHit = o.MoveHit;
            ComboIdx = o.ComboIdx; ComboWindow = o.ComboWindow; ComboHits = o.ComboHits; ComboTimer = o.ComboTimer;
            Shielding = o.Shielding; ShieldTick = o.ShieldTick; ParryTicks = o.ParryTicks; GuardTicks = o.GuardTicks;
            Cd[0] = o.Cd[0]; Cd[1] = o.Cd[1]; Cd[2] = o.Cd[2];
            SlowTicks = o.SlowTicks; WeakenTicks = o.WeakenTicks; SluggishTicks = o.SluggishTicks; ArmorBreakTicks = o.ArmorBreakTicks;
            TailwindTicks = o.TailwindTicks; TailwindCd = o.TailwindCd; AuraTicks = o.AuraTicks; RingTicks = o.RingTicks; IdleTicks = o.IdleTicks; SetCd = o.SetCd;
            GuardHp10 = o.GuardHp10; GuardBrokenTicks = o.GuardBrokenTicks;
            PrevInput = o.PrevInput; BufBtn = o.BufBtn; BufAge = o.BufAge;
            DashTicks = o.DashTicks; DashCd = o.DashCd; DashDir = o.DashDir;
            DropTicks = o.DropTicks; DropPlatform = o.DropPlatform;
            HitsLanded = o.HitsLanded; CombosLanded = o.CombosLanded; UltsUsed = o.UltsUsed; Parries = o.Parries;
        }

        public void Hash(ref uint h)
        {
            ChibiSimState.Mix(ref h, Class); ChibiSimState.Mix(ref h, Passive);
            ChibiSimState.Mix(ref h, X); ChibiSimState.Mix(ref h, Y); ChibiSimState.Mix(ref h, Vx); ChibiSimState.Mix(ref h, Vy);
            ChibiSimState.Mix(ref h, XRem); ChibiSimState.Mix(ref h, YRem);
            ChibiSimState.Mix(ref h, Facing); ChibiSimState.Mix(ref h, Grounded ? 1 : 0);
            ChibiSimState.Mix(ref h, AirJumps); ChibiSimState.Mix(ref h, Hp); ChibiSimState.Mix(ref h, Ult);
            ChibiSimState.Mix(ref h, Stun); ChibiSimState.Mix(ref h, Invuln);
            ChibiSimState.Mix(ref h, MoveSlot); ChibiSimState.Mix(ref h, MoveTick); ChibiSimState.Mix(ref h, MoveStartup);
            ChibiSimState.Mix(ref h, MoveActive); ChibiSimState.Mix(ref h, MoveRecovery); ChibiSimState.Mix(ref h, MoveMult);
            ChibiSimState.Mix(ref h, MoveKbScale); ChibiSimState.Mix(ref h, ShotIdx); ChibiSimState.Mix(ref h, TargetX);
            ChibiSimState.Mix(ref h, MoveHit ? 1 : 0);
            ChibiSimState.Mix(ref h, ComboIdx); ChibiSimState.Mix(ref h, ComboWindow);
            ChibiSimState.Mix(ref h, ComboHits); ChibiSimState.Mix(ref h, ComboTimer);
            ChibiSimState.Mix(ref h, Shielding ? 1 : 0); ChibiSimState.Mix(ref h, ShieldTick);
            ChibiSimState.Mix(ref h, ParryTicks); ChibiSimState.Mix(ref h, GuardTicks);
            ChibiSimState.Mix(ref h, Cd[0]); ChibiSimState.Mix(ref h, Cd[1]); ChibiSimState.Mix(ref h, Cd[2]);
            ChibiSimState.Mix(ref h, SlowTicks); ChibiSimState.Mix(ref h, WeakenTicks);
            ChibiSimState.Mix(ref h, SluggishTicks); ChibiSimState.Mix(ref h, ArmorBreakTicks);
            ChibiSimState.Mix(ref h, TailwindTicks); ChibiSimState.Mix(ref h, TailwindCd);
            ChibiSimState.Mix(ref h, AuraTicks); ChibiSimState.Mix(ref h, RingTicks); ChibiSimState.Mix(ref h, IdleTicks);
            ChibiSimState.Mix(ref h, SetCd); ChibiSimState.Mix(ref h, GuardHp10); ChibiSimState.Mix(ref h, GuardBrokenTicks);
            ChibiSimState.Mix(ref h, PrevInput); ChibiSimState.Mix(ref h, BufBtn); ChibiSimState.Mix(ref h, BufAge);
            ChibiSimState.Mix(ref h, HitsLanded); ChibiSimState.Mix(ref h, CombosLanded);
            ChibiSimState.Mix(ref h, UltsUsed); ChibiSimState.Mix(ref h, Parries);
            ChibiSimState.Mix(ref h, DashTicks); ChibiSimState.Mix(ref h, DashCd); ChibiSimState.Mix(ref h, DashDir);
            ChibiSimState.Mix(ref h, DropTicks); ChibiSimState.Mix(ref h, DropPlatform);
        }
    }

    public sealed class ChibiProjectileSim
    {
        public bool Active;
        /// <summary>X/Y in milli; Vx/Vy in velocity sub-units (1/VelSub milli per tick); KbX/KbY knockback in milli/tick.</summary>
        public int Owner, X, Y, Vx, Vy, Life, Mult, KbX, KbY, Stun;
        /// <summary>Sub-milli position remainders in [0, VelSub) (rollback state).</summary>
        public int XRem, YRem;
        public bool Homing;

        public void CopyFrom(ChibiProjectileSim o)
        {
            Active = o.Active; Owner = o.Owner; X = o.X; Y = o.Y; Vx = o.Vx; Vy = o.Vy; XRem = o.XRem; YRem = o.YRem; Life = o.Life;
            Mult = o.Mult; KbX = o.KbX; KbY = o.KbY; Stun = o.Stun; Homing = o.Homing;
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
        public readonly ChibiFighterSim[] F = { new ChibiFighterSim(), new ChibiFighterSim() };
        public readonly ChibiProjectileSim[] Proj = new ChibiProjectileSim[ChibiSimConst.MaxProjectiles];

        public ChibiSimState()
        {
            for (int i = 0; i < Proj.Length; i++) Proj[i] = new ChibiProjectileSim();
        }

        public void CopyFrom(ChibiSimState o)
        {
            Frame = o.Frame; Rng = o.Rng; MatchOver = o.MatchOver; Winner = o.Winner; Stage = o.Stage;
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
            F[0].Hash(ref h); F[1].Hash(ref h);
            for (int i = 0; i < Proj.Length; i++)
            {
                var p = Proj[i];
                Mix(ref h, p.Active ? 1 : 0);
                if (!p.Active) continue;
                Mix(ref h, p.Owner); Mix(ref h, p.X); Mix(ref h, p.Y); Mix(ref h, p.Vx); Mix(ref h, p.Vy);
                Mix(ref h, p.XRem); Mix(ref h, p.YRem);
                Mix(ref h, p.Life); Mix(ref h, p.Mult); Mix(ref h, p.KbX); Mix(ref h, p.KbY); Mix(ref h, p.Stun);
                Mix(ref h, p.Homing ? 1 : 0);
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
        const int ActionMask = (ushort)(ChibiButtons.Light | ChibiButtons.Heavy | ChibiButtons.Skill1 |
                                        ChibiButtons.Skill2 | ChibiButtons.Skill3 | ChibiButtons.Ultimate);

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
            f.Hp = f.Stats.MaxHp; f.Ult = 0; f.Stun = 0; f.Invuln = 0;
            f.MoveSlot = -1; f.MoveTick = 0; f.MoveStartup = 0; f.MoveActive = 0; f.MoveRecovery = 0;
            f.MoveMult = 0; f.MoveKbScale = 1000; f.ShotIdx = 0; f.TargetX = 0; f.MoveHit = false;
            f.ComboIdx = 0; f.ComboWindow = 0; f.ComboHits = 0; f.ComboTimer = 0;
            f.Shielding = false; f.ShieldTick = 0; f.ParryTicks = 0; f.GuardTicks = 0;
            f.Cd[0] = f.Cd[1] = f.Cd[2] = 0;
            f.SlowTicks = f.WeakenTicks = f.SluggishTicks = f.ArmorBreakTicks = 0;
            f.TailwindTicks = 0; f.TailwindCd = 0; f.AuraTicks = 0; f.RingTicks = 0; f.IdleTicks = 0; f.SetCd = 0;
            f.GuardHp10 = ChibiCombatStats.GuardMax10(f.Stats.Defense); f.GuardBrokenTicks = 0;
            f.PrevInput = 0; f.BufBtn = 0; f.BufAge = 0;
            f.HitsLanded = f.CombosLanded = f.UltsUsed = f.Parries = 0;
            f.DashTicks = 0; f.DashCd = 0; f.DashDir = 0;
            f.DropTicks = 0; f.DropPlatform = -1;
        }

        public static void Step(ChibiSimState s, ChibiInputPacket in0, ChibiInputPacket in1)
        {
            if (s.MatchOver) { s.Frame++; return; }

            UpdateFighter(s, 0, in0.Sanitized());
            UpdateFighter(s, 1, in1.Sanitized());

            // Probe both melee hitboxes first, then apply: simultaneous swings trade.
            PendingHit h0 = MeleeProbe(s, 0);
            PendingHit h1 = MeleeProbe(s, 1);
            if (h0.Valid) ApplyHit(s, 0, 1, h0);
            if (h1.Valid) ApplyHit(s, 1, 0, h1);

            TickFireRings(s);
            UpdateProjectiles(s);
            ResolveBoundsAndMatchEnd(s);
            s.Frame++;
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

        static void UpdateFighter(ChibiSimState s, int i, ChibiInputPacket input)
        {
            var f = s.F[i];
            var st = ChibiStageTable.Get(s.Stage);
            int raw = input.Raw;
            int edges = raw & ~f.PrevInput;               // buttons pressed this frame
            f.PrevInput = raw;

            // ---- timers
            Dec(ref f.Stun); Dec(ref f.Invuln); Dec(ref f.ComboWindow);
            Dec(ref f.DashTicks); Dec(ref f.DashCd);       // dash timers tick unconditionally (Fighter.cs:205-207)
            // Drop-through ends at the start of tick T+21, before this tick's probe and physics (stage-conversion.md section 3).
            if (f.DropTicks > 0 && --f.DropTicks == 0) f.DropPlatform = -1;
            Dec(ref f.ParryTicks); Dec(ref f.GuardTicks);
            Dec(ref f.SlowTicks); Dec(ref f.WeakenTicks); Dec(ref f.SluggishTicks); Dec(ref f.ArmorBreakTicks);
            Dec(ref f.TailwindTicks); Dec(ref f.AuraTicks); Dec(ref f.RingTicks); Dec(ref f.SetCd); Dec(ref f.GuardBrokenTicks);
            int guardMax = ChibiCombatStats.GuardMax10(f.Stats.Defense);
            if (!f.Shielding && f.GuardBrokenTicks == 0 && f.GuardHp10 < guardMax)
                f.GuardHp10 = Math.Min(guardMax, f.GuardHp10 + ChibiCombatStats.GuardRegenPerTick);
            Dec(ref f.Cd[0]); Dec(ref f.Cd[1]); Dec(ref f.Cd[2]);
            if (f.ComboTimer > 0 && --f.ComboTimer == 0) f.ComboHits = 0;
            if (f.Stats.TailwindCooldown > 0)
            {
                if (f.TailwindCd > 0) f.TailwindCd--;
                else { f.TailwindTicks = f.Stats.TailwindDuration; f.TailwindCd = f.Stats.TailwindCooldown; }
            }

            // ---- input buffer: an action pressed during recovery/stun still fires for InputBufferTicks.
            // Dash is deliberately not in ActionMask: a press during cooldown/dash/stun is lost, not buffered.
            int act = edges & ActionMask;
            if (act != 0) { f.BufBtn |= act; f.BufAge = 0; }
            else if (f.BufBtn != 0 && ++f.BufAge > ChibiSimConst.InputBufferTicks) f.BufBtn = 0;

            // Doc 5.1 / Fighter.cs:179-189: no x-velocity change at all during hitstun (knockback keeps its speed).
            // The 30 u/s² DisabledFriction branch (grounded, not stunned, cannot act) has no reachable state here:
            // the sim has no intro/pause, and a KO ends the match before the next Step.
            bool level = false;                          // original forces vy = 0 this tick (doubled 50 Hz arc term)
            bool dashing = f.Stun == 0 && f.DashTicks > 0;
            if (f.Stun > 0)
            {
                f.MoveSlot = -1; f.Shielding = false; f.ShieldTick = 0; f.ParryTicks = 0; f.GuardTicks = 0;
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
                if (f.IdleTicks < (1 << 20)) f.IdleTicks++;
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

            // Priority: ultimate > skills > heavy > light
            int b = f.BufBtn;
            if ((b & (int)ChibiButtons.Ultimate) != 0 && f.Ult >= ChibiSimConst.UltMax) { StartMove(s, i, 5); return; }
            for (int k = 0; k < 3; k++)
            {
                int bit = (int)ChibiButtons.Skill1 << k;
                if ((b & bit) != 0 && f.Cd[k] == 0 && ChibiMoveTable.Get(f.Class, 2 + k).Exists) { StartMove(s, i, 2 + k); return; }
            }
            bool reloading = f.SetCd > 0 && (f.Class == (int)FighterClass.Ranged || f.Class == (int)FighterClass.Magic);
            if (reloading) return;
            if ((b & (int)ChibiButtons.Heavy) != 0) StartMove(s, i, 1);
            else if ((b & (int)ChibiButtons.Light) != 0) StartMove(s, i, 0);
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
            int sp = ChibiCombatStats.AttackSpeedPermille(f.Stats, f.SluggishTicks > 0);

            f.MoveSlot = slot; f.MoveTick = 0; f.MoveHit = false; f.ShotIdx = 0;
            f.MoveStartup = ChibiCombatStats.ScaleFrames(mv.Startup, sp);
            f.MoveActive = mv.Active;
            f.MoveRecovery = ChibiCombatStats.ScaleFrames(mv.Recovery, sp);

            int mult = mv.Mult, kbScale = 1000;
            if (slot == 0)
            {
                if (f.ComboWindow <= 0) f.ComboIdx = 0;
                int idx = f.ComboIdx;
                if (idx == f.Stats.PatternLength - 1)                                              // finisher
                {
                    mult = mult * 1500 / 1000; kbScale = 1500;
                    if (f.Class == (int)FighterClass.Ranged || f.Class == (int)FighterClass.Magic)
                        f.SetCd = f.MoveStartup + ChibiSimConst.SetCooldownTicks;                   // reload after the 4th shot
                }
                f.ComboIdx = (idx + 1) % f.Stats.PatternLength;
                f.ComboWindow = ChibiSimConst.ComboWindowTicks;
            }
            else if (slot == 1) { f.ComboIdx = 0; f.ComboWindow = 0; }

            if (f.Stats.PatienceTicks > 0 && f.IdleTicks >= f.Stats.PatienceTicks && mult > 0)
                mult = mult * (1000 + ChibiCombatStats.PatienceBonusPermille) / 1000;
            f.IdleTicks = 0;

            f.MoveMult = mult; f.MoveKbScale = kbScale;
            if (slot >= 2 && slot <= 4) f.Cd[slot - 2] = mv.Cooldown;
            if (slot == 5)
            {
                f.Ult = 0; f.UltsUsed++;
                if (f.Invuln < 36) f.Invuln = 36;   // ultimate wind-up is protected
            }
            f.BufBtn = 0;
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
                        SpawnProjectile(s, i, f.X + f.Facing * 450, f.Y + ChibiSimConst.ProjectileY + yOff, f.Facing * mv.ProjSpeed,
                                        mv.ProjLife, f.MoveMult, mv.KbX * f.MoveKbScale / 1000, mv.KbY * f.MoveKbScale / 1000,
                                        mv.Stun, (mv.Flags & ChibiMove.FHoming) != 0);
                        f.ShotIdx++;
                    }
                    if ((mv.Flags & ChibiMove.FAura) != 0) f.AuraTicks = ChibiSimConst.AuraDuration;
                    if ((mv.Flags & ChibiMove.FRing) != 0) f.RingTicks = ChibiSimConst.RingDuration;
                    // Sword Aura: light attacks also throw a horizontal wave
                    if (f.MoveSlot == 0 && f.Class == (int)FighterClass.Melee && f.AuraTicks > 0)
                        SpawnProjectile(s, i, f.X + f.Facing * 600, f.Y + 600, f.Facing * 280, 40, 800, 120, 80, 12, false);
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
            return new PendingHit
            {
                Valid = true, Mult = f.MoveMult, KbX = mv.KbX * f.MoveKbScale / 1000, KbY = mv.KbY * f.MoveKbScale / 1000,
                Stun = mv.Stun, Dir = dir, Unblockable = (mv.Flags & ChibiMove.FUnblockable) != 0
            };
        }

        static bool Overlaps(ChibiFighterSim target, int x0, int x1, int y0, int y1)
        {
            return x0 < target.X + ChibiSimConst.HurtHalfW && x1 > target.X - ChibiSimConst.HurtHalfW &&
                   y0 < target.Y + ChibiSimConst.HurtH && y1 > target.Y;
        }

        /// <summary>Spawns a projectile. <paramref name="vx"/> is in milli/tick (move-table units) and is stored x VelSub.</summary>
        static void SpawnProjectile(ChibiSimState s, int owner, int x, int y, int vx, int life, int mult, int kbX, int kbY, int stun, bool homing)
        {
            for (int n = 0; n < s.Proj.Length; n++)
            {
                var p = s.Proj[n];
                if (p.Active) continue;
                p.Active = true; p.Owner = owner; p.X = x; p.Y = y; p.Vx = vx * ChibiSimConst.VelSub; p.Vy = 0;
                p.XRem = 0; p.YRem = 0; p.Life = life;
                p.Mult = mult; p.KbX = kbX; p.KbY = kbY; p.Stun = stun; p.Homing = homing;
                return;
            }
            // Pool exhausted: drop the shot. Deterministic because the pool state is part of ChibiSimState.
        }

        static void UpdateProjectiles(ChibiSimState s)
        {
            var stProj = ChibiStageTable.Get(s.Stage);   // per-stage X bound (stage-conversion.md section 7)
            for (int n = 0; n < s.Proj.Length; n++)
            {
                var p = s.Proj[n];
                if (!p.Active) continue;
                var owner = s.F[p.Owner];
                var target = s.F[1 - p.Owner];

                if (p.Homing)
                {
                    int dy = target.Y + 650 - p.Y;
                    const int steer = 8 * ChibiSimConst.VelSub, maxVy = 100 * ChibiSimConst.VelSub;   // 8 / 100 milli/tick
                    p.Vy += Sign(dy) * steer;
                    if (p.Vy > maxVy) p.Vy = maxVy; else if (p.Vy < -maxVy) p.Vy = -maxVy;
                }
                Integrate(ref p.X, ref p.XRem, p.Vx);
                Integrate(ref p.Y, ref p.YRem, p.Vy);
                p.Life--;

                if (p.Life <= 0 || Math.Abs(p.X) > stProj.BlastX || p.Y < ChibiSimConst.ProjBlastBottom || p.Y > ChibiSimConst.ProjBlastTop)
                { p.Active = false; continue; }

                // Fire ring and spin guard eat projectiles that come close to the defender
                long dx = p.X - target.X, dyc = p.Y - (target.Y + 650);
                long d2 = dx * dx + dyc * dyc;
                if (target.RingTicks > 0 && d2 <= (long)ChibiSimConst.RingRadius * ChibiSimConst.RingRadius) { p.Active = false; continue; }
                if (target.GuardTicks > 0 && d2 <= (long)ChibiSimConst.GuardRadius * ChibiSimConst.GuardRadius) { p.Active = false; continue; }

                if (Overlaps(target, p.X - ChibiSimConst.ProjHalfW, p.X + ChibiSimConst.ProjHalfW,
                             p.Y - ChibiSimConst.ProjHalfH, p.Y + ChibiSimConst.ProjHalfH))
                {
                    var hit = new PendingHit { Valid = true, Mult = p.Mult, KbX = p.KbX, KbY = p.KbY, Stun = p.Stun, Dir = Sign(p.Vx) };
                    p.Active = false;
                    ApplyHit(s, p.Owner, 1 - p.Owner, hit);
                }
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
                ApplyHit(s, i, 1 - i, new PendingHit { Valid = true, Mult = 400, KbX = 60, KbY = 45, Stun = 12, Dir = Sign(dx) != 0 ? Sign(dx) : f.Facing });
            }
        }

        // ------------------------------------------------------------------ damage

        /// <summary>
        /// Resolution order (fixed, so the RNG stream matches on both peers):
        ///   invulnerable -> parry stance -> perfect shield parry -> Ward roll -> crit roll -> block scaling ->
        ///   defense mitigation -> apply HP/stun/knockback -> attacker rewards (life steal, ult, on-hit procs).
        /// </summary>
        static void ApplyHit(ChibiSimState s, int a, int d, PendingHit h)
        {
            var A = s.F[a];
            var D = s.F[d];
            if (D.Invuln > 0) return;
            if (ChibiCombatStats.HalfKnockback(A.Class)) { h.KbX /= 2; h.KbY /= 2; }

            // Parry skill: the attack is turned around on the attacker
            if (D.ParryTicks > 0 && !h.Unblockable)
            {
                D.ParryTicks = 0;
                int reflected = ChibiCombatStats.RollDamage(A.Stats, h.Mult, A.WeakenTicks > 0, ref s.Rng, out _);
                reflected = ChibiCombatStats.MitigateDefense(reflected, ChibiCombatStats.EffectiveDefense(A.Stats, A.ArmorBreakTicks > 0));
                A.Hp = Math.Max(0, A.Hp - reflected);
                A.Stun = Math.Max(A.Stun, 30);
                A.DashTicks = 0;                                                                // Interrupt()
                A.Vx = -h.Dir * 120 * ChibiSimConst.VelSub; A.Vy = 80 * ChibiSimConst.VelSub;   // 120 / 80 milli/tick
                GainUlt(D, ChibiSimConst.UltPerParry);
                D.Parries++;
                return;
            }

            bool blocking = D.Shielding && !h.Unblockable;
            if (blocking && D.ShieldTick < ChibiSimConst.ParryWindow)
            {
                A.Stun = Math.Max(A.Stun, 27);
                A.DashTicks = 0;                                                                // Interrupt()
                GainUlt(D, ChibiSimConst.UltPerParry);
                D.Parries++;
                return;
            }

            if (D.Stats.WardPermille > 0 && s.Rng.Roll(D.Stats.WardPermille)) return;

            int dmg = ChibiCombatStats.RollDamage(A.Stats, h.Mult, A.WeakenTicks > 0, ref s.Rng, out _);
            int stun = h.Stun;
            if (blocking)
            {
                D.GuardHp10 -= dmg * 10;                    // the guard soaks the full raw hit
                if (D.GuardHp10 <= 0)
                {
                    D.GuardHp10 = 0;                        // guard break: full damage, long stun, no blocking for 3 s
                    D.GuardBrokenTicks = ChibiCombatStats.GuardBrokenTicks;
                    D.Shielding = false;
                    blocking = false;
                    if (stun < ChibiCombatStats.GuardBreakStunTicks) stun = ChibiCombatStats.GuardBreakStunTicks;
                }
                else dmg = dmg * ChibiCombatStats.BlockedPermille(ChibiCombatStats.EffectiveDefense(D.Stats, D.ArmorBreakTicks > 0)) / 1000;
            }
            dmg = ChibiCombatStats.MitigateDefense(dmg, ChibiCombatStats.EffectiveDefense(D.Stats, D.ArmorBreakTicks > 0));
            D.Hp = Math.Max(0, D.Hp - dmg);
            GainUlt(D, ChibiSimConst.UltWhenHit);

            if (blocking)
            {
                D.Vx = h.Dir * h.KbX * ChibiSimConst.VelSub / 4;     // PendingHit knockback is milli/tick
                GainUlt(A, 10);
                return;
            }

            D.Stun = Math.Max(D.Stun, stun);
            if (stun > 0) D.DashTicks = 0;                  // Interrupt() only when stun > 0 (Fighter.cs:614-618)
            D.Vx = h.Dir * h.KbX * ChibiSimConst.VelSub;
            D.Vy = h.KbY * ChibiSimConst.VelSub;
            D.MoveSlot = -1; D.Shielding = false; D.ParryTicks = 0; D.GuardTicks = 0;

            // attacker rewards
            A.HitsLanded++;
            A.ComboHits++;
            A.ComboTimer = 66;
            if (A.ComboHits % A.Stats.PatternLength == 0) A.CombosLanded++;
            int gain = ChibiSimConst.UltPerHit + (A.ComboHits >= 3 ? ChibiSimConst.UltPerComboHit - ChibiSimConst.UltPerHit : 0);
            if (A.Hp * 10 < A.Stats.MaxHp * 3) gain *= 2;           // calculated risk: fighting under 30% HP
            GainUlt(A, gain);
            if (A.Stats.LifestealPermille > 0) A.Hp = Math.Min(A.Stats.MaxHp, A.Hp + dmg * A.Stats.LifestealPermille / 1000);
            if (A.Stats.OnHitHexPermille > 0 && s.Rng.Roll(A.Stats.OnHitHexPermille))
            { D.SlowTicks = D.WeakenTicks = D.SluggishTicks = ChibiCombatStats.HexDuration; }
            if (A.Stats.OnHitArmorBreakPermille > 0 && s.Rng.Roll(A.Stats.OnHitArmorBreakPermille))
                D.ArmorBreakTicks = ChibiCombatStats.ArmorBreakDuration;
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
                    f.Hp = Math.Max(0, f.Hp - f.Stats.MaxHp * ChibiSimConst.RingOutPenaltyPermille / 1000);
                    f.X = st.RespawnX; f.Y = st.RespawnY; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Stun = 0;
                    f.Grounded = false;
                    f.Invuln = ChibiSimConst.RespawnInvuln;
                    f.MoveSlot = -1; f.Shielding = false; f.ParryTicks = 0; f.GuardTicks = 0;
                    f.DashTicks = 0;                        // RingOut -> Interrupt(); DashCd is kept (Fighter.cs:692-708)
                }
            }

            bool ko0 = s.F[0].Hp <= 0, ko1 = s.F[1].Hp <= 0;
            if (ko0 || ko1)
            {
                s.MatchOver = true;
                s.Winner = ko0 && ko1 ? -1 : (ko0 ? 1 : 0);
            }
            else if (s.Frame + 1 >= ChibiSimConst.MatchTicks)
            {
                s.MatchOver = true;
                long r0 = (long)s.F[0].Hp * 1000 / s.F[0].Stats.MaxHp, r1 = (long)s.F[1].Hp * 1000 / s.F[1].Stats.MaxHp;
                s.Winner = r0 == r1 ? -1 : (r0 > r1 ? 0 : 1);
            }
        }
    }
}
