using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Moveset mechanics of one CSV row (slots &gt;= <see cref="ChibiMovesetData.FirstNewSlot"/>), rules v18. Constant
    /// data generated from design/gdd/movesets/movesets.csv by tools/gameplay/gen_movesets.py; every field is mixed into
    /// the config hash by <see cref="ChibiMovesetData.MixHash"/>. Semantics: design/gdd/movesets/README.md section 5.
    /// All values are integers (ticks, milli, velocity sub-units, permille).
    /// </summary>
    public sealed class ChibiMoveExt
    {
        /// <summary><see cref="Flags"/> bits (README section 5 flag names).</summary>
        public const int FSpike = 1 << 0, FTrip = 1 << 1, FBack = 1 << 2, FPull = 1 << 3, FCounter = 1 << 4,
                         FCounterProj = 1 << 5, FReflect = 1 << 6, FDive = 1 << 7, FTeleport = 1 << 8, FAirOnce = 1 << 9,
                         FRestoreJump = 1 << 10, FRoll = 1 << 11, FPierce = 1 << 12, FTetherSelf = 1 << 13,
                         FTetherFoe = 1 << 14, FBounce = 1 << 15, FExplicitPv = 1 << 16, FArmor = 1 << 17,
                         FRecoil = 1 << 18, FInvulnWindow = 1 << 19, FLandHit = 1 << 20, FZone = 1 << 21,
                         FCharge = 1 << 22, FChargeLevel = 1 << 23, FHold = 1 << 24, FMulti = 1 << 25, FFan = 1 << 26,
                         FCircle = 1 << 27, FEstimated = 1 << 28;

        /// <summary>Input of the row: button 0 L, 1 H, 2 B, 3-5 S1-S3; dir 0 neutral, 1 F, 2 BK, 3 U, 4 D.</summary>
        public int Button, Dir;
        /// <summary>1 = ground, 2 = air, 3 = both (CSV ground_air).</summary>
        public int AirMask;
        /// <summary>Dash-window variant (L@dash / L+D@dash).</summary>
        public bool Dash;
        /// <summary>The button bit that started the move; charge / hold read its held level.</summary>
        public int Trigger;
        /// <summary>Technique cooldown index into <see cref="ChibiFighterSim.TechCd"/> (k*2 + (U ? 0 : 1)), -1 = none.</summary>
        public int TechIdx;
        public int Flags;
        /// <summary>Circle hitbox radius (milli), 0 = box (README 4.2).</summary>
        public int Radius;
        public int ChargeMax, ChargeBonus, ChargeLevel, ChargeLevelMin, ChargeGroupBase, ChargeGroupCount;
        public int HoldMax, MultiHits;
        /// <summary>Armor move ticks ArmorFrom..ArmorTo inclusive (-1 = last active tick incl. hold), knockback permille.</summary>
        public int ArmorFrom, ArmorTo, ArmorKbPermille;
        public int ReflectPermille, ReflectMaxMult;
        /// <summary>Zone kind 1 contact (A arm, B life), 2 timed (A delay, B burst), 3 periodic (A period, B life).</summary>
        public int ZoneKind, ZoneA, ZoneB, ZoneSlow;
        public int DiveMax, LandMult, LandKbX, LandKbY, LandStun, LandW, LandH;
        public int TeleportDist, InvulnFrom, InvulnTo, RecoilVx, RecoilVy;
        public int TetherSpeed, TetherMax, BounceMax, BounceMult;
        /// <summary>Explicit projectile velocity (sub/tick, X toward Facing).</summary>
        public int ProjVx, ProjVy;
        /// <summary>Per-shot projectile velocities (sub/tick, X toward Facing), shot i = ShotIdx; null = none.</summary>
        public int[] FanVx, FanVy;

        public bool Has(int flag) => (Flags & flag) != 0;
    }

    /// <summary>Moveset constants (README section 5 "New constants"), all mixed into the config hash.</summary>
    public static class ChibiMovesetConst
    {
        /// <summary>Dash window: Grounded and DashCd &gt;= DashCooldown - DashAttackWindow (dash start .. 16 ticks after).</summary>
        public const int DashAttackWindow = 16;
        /// <summary>Non-final hits of a multi-hit: kb (13500, 9000), stun = rehit + 4.</summary>
        public const int LinkKbX = 13500, LinkKbY = 9000, LinkStunPad = 4;
        /// <summary>
        /// Trip vs a grounded victim (22500, 0); vs an airborne victim the L horizontal tier. The grounded stun comes from each
        /// row's own CSV <c>stun</c> since rules v23 (Bruna down tilt 22, Ren 26, Kaia low sweep 28); TripStun = 34 is the
        /// original sheet value, no longer read by the sim but kept in the config hash.
        /// </summary>
        public const int TripKbX = 22500, TripStun = 34, TripAirKbX = 67500, TripAirKbY = 52500, TripAirStun = 22;
        /// <summary>Counter: lock ends 20 ticks after the trigger, invuln 20.</summary>
        public const int CounterLock = 20, CounterInvuln = 20;
        /// <summary>Tether pull arrives at Chebyshev distance &lt;= 300 milli.</summary>
        public const int PullArrive = 300;
        public const int ZonePool = 4;
        public const int TeleportMargin = 2000;
        /// <summary>Teleport diagonal axis factor (permille of the distance).</summary>
        public const int TeleportDiag = 707;
        /// <summary>Dive that hits: Vy = +60 milli/tick, then recovery in the air.</summary>
        public const int DivePopVy = 60;
        /// <summary>Bouncing projectile re-arms 10 ticks after hitting the enemy.</summary>
        public const int ProjRearm = 10;
        /// <summary>Reflected projectile life floor (ticks).</summary>
        public const int ReflectLifeFloor = 30;
        /// <summary>
        /// SEC-104 (rules v19): only a shot's first two reflections multiply its Mult (x P / 1000 each); later reflections
        /// keep it, so a shot bounced between two reflectors is capped at P^2 (1.5625 for Prism) of its spawn Mult.
        /// </summary>
        public const int MaxScaledReflects = 2;
        /// <summary>TechCd length (technique cooldown slots, README 1.3); the generator asserts TechIdx stays below it.</summary>
        public const int TechCdSlots = 6;
        /// <summary>Most rows per class: AirUsedMask is an int and uses bit (slot - 7).</summary>
        public const int MaxRowsPerClass = 31;

        /// <summary>Every constant above, mixed into the config hash.</summary>
        public static readonly int[] HashConstants =
        {
            DashAttackWindow, LinkKbX, LinkKbY, LinkStunPad, TripKbX, TripStun, TripAirKbX, TripAirKbY, TripAirStun,
            CounterLock, CounterInvuln, PullArrive, ZonePool, TeleportMargin, TeleportDiag, DivePopVy, ProjRearm,
            ReflectLifeFloor, MaxScaledReflects, TechCdSlots, MaxRowsPerClass,
        };
    }

    public static partial class ChibiMovesetData
    {
        /// <summary>Resolution key: (button * 5 + dir) * 2 + air, 0..59.</summary>
        public static int Key(int button, int dir, int air) => (button * 5 + dir) * 2 + air;

        /// <summary>Slot of an input pattern for class <paramref name="cls"/>, or -1.</summary>
        public static int SlotFor(int cls, int button, int dir, bool air) => Resolve[cls][Key(button, dir, air ? 1 : 0)];

        /// <summary>Appends the CSV rows to the existing 7-slot class tables (called once by ChibiMoveTable.Build).</summary>
        public static ChibiMove[][] Extend(ChibiMove[][] t)
        {
            var add = NewRows();
            var res = new ChibiMove[t.Length][];
            for (int c = 0; c < t.Length; c++)
            {
                if (t[c].Length != FirstNewSlot)
                    throw new InvalidOperationException("ChibiMovesetData: class " + c + " has " + t[c].Length + " base slots, expected " + FirstNewSlot);
                var extra = c < add.Length ? add[c] : Array.Empty<ChibiMove>();
                res[c] = new ChibiMove[t[c].Length + extra.Length];
                Array.Copy(t[c], res[c], t[c].Length);
                Array.Copy(extra, 0, res[c], t[c].Length, extra.Length);
            }
            return res;
        }

        /// <summary>Mixes every moveset value (rows, ext, resolution tables, constants) into a config hash.</summary>
        public static void MixHash(ref uint h)
        {
            var kc = ChibiMovesetConst.HashConstants;
            for (int k = 0; k < kc.Length; k++) ChibiSimState.Mix(ref h, kc[k]);
            for (int c = 0; c < Resolve.Length; c++)
            {
                var r = Resolve[c];
                for (int k = 0; k < r.Length; k++) ChibiSimState.Mix(ref h, r[k]);
                ChibiSimState.Mix(ref h, DashSlot[c]);
                ChibiSimState.Mix(ref h, DashDownSlot[c]);
                int n = ChibiMoveTable.Count(c);
                ChibiSimState.Mix(ref h, n);
                for (int slot = FirstNewSlot; slot < n; slot++) MixMove(ref h, ChibiMoveTable.Get(c, slot));
            }
        }

        static void MixMove(ref uint h, in ChibiMove m)
        {
            int[] v =
            {
                m.Startup, m.Active, m.Recovery, m.Mult, m.BoxX, m.BoxW, m.BoxY, m.BoxH, m.KbX, m.KbY, m.Stun,
                m.SelfVx, m.SelfVy, m.ProjSpeed, m.ProjLife, (int)m.ProjKind, m.ProjR, m.ProjFlags, m.ProjOffX, m.ProjOffY,
                m.Rehit, m.Cooldown, m.Flags,
            };
            for (int k = 0; k < v.Length; k++) ChibiSimState.Mix(ref h, v[k]);
            var e = m.Ext;
            if (e == null) { ChibiSimState.Mix(ref h, -1); return; }
            int[] x =
            {
                e.Button, e.Dir, e.AirMask, e.Dash ? 1 : 0, e.Trigger, e.TechIdx, e.Flags, e.Radius, e.ChargeMax,
                e.ChargeBonus, e.ChargeLevel, e.ChargeLevelMin, e.ChargeGroupBase, e.ChargeGroupCount, e.HoldMax,
                e.MultiHits, e.ArmorFrom, e.ArmorTo, e.ArmorKbPermille, e.ReflectPermille, e.ReflectMaxMult, e.ZoneKind,
                e.ZoneA, e.ZoneB, e.ZoneSlow, e.DiveMax, e.LandMult, e.LandKbX, e.LandKbY, e.LandStun, e.LandW, e.LandH,
                e.TeleportDist, e.InvulnFrom, e.InvulnTo, e.RecoilVx, e.RecoilVy, e.TetherSpeed, e.TetherMax,
                e.BounceMax, e.BounceMult, e.ProjVx, e.ProjVy,
            };
            for (int k = 0; k < x.Length; k++) ChibiSimState.Mix(ref h, x[k]);
            int fan = e.FanVx != null ? e.FanVx.Length : 0;
            ChibiSimState.Mix(ref h, fan);
            for (int k = 0; k < fan; k++) { ChibiSimState.Mix(ref h, e.FanVx[k]); ChibiSimState.Mix(ref h, e.FanVy[k]); }
        }
    }

    /// <summary>Presentation metadata of the moveset rows. Never read by the simulation (not in any hash).</summary>
    public static partial class ChibiMoveInfo
    {
        /// <summary>One row's view data: CSV id, display name, animation kind, VFX note, onomatopoeia, start / hit sound ids.</summary>
        public readonly struct Entry
        {
            public readonly string Id, Name, AnimKind, Vfx, Onomatopoeia, SfxStart, SfxHit;

            public Entry(string id, string name, string animKind, string vfx, string onomatopoeia, string sfxStart, string sfxHit)
            {
                Id = id; Name = name; AnimKind = animKind; Vfx = vfx; Onomatopoeia = onomatopoeia; SfxStart = sfxStart; SfxHit = sfxHit;
            }

            /// <summary>False for slots without metadata (slots 0-6, out of range).</summary>
            public bool Valid => Id != null;
        }

        /// <summary>Metadata of class <paramref name="cls"/> slot <paramref name="slot"/>; default (Valid false) for slots 0-6.</summary>
        public static Entry Get(int cls, int slot)
        {
            if (cls < 0 || cls >= Rows.Length) return default;
            int k = slot - ChibiMovesetData.FirstNewSlot;
            return k >= 0 && k < Rows[cls].Length ? Rows[cls][k] : default;
        }

        /// <summary>Number of moveset rows of class <paramref name="cls"/> (slots FirstNewSlot .. FirstNewSlot + Count - 1).</summary>
        public static int Count(int cls) => cls >= 0 && cls < Rows.Length ? Rows[cls].Length : 0;
    }
}
