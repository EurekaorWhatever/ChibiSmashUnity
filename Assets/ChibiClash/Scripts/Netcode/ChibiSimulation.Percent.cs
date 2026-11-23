using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Rules v20: damage percent, Smash-style knockback and stocks (design/gdd/percent-knockback.md, decisions GL-1 / GL-2
    /// in production/session-state/active.md). Every knob of the spec's section 7 (sim side) lives here and is mixed into
    /// the config hash through <see cref="Constants"/>. Integer only; intermediates in long.
    /// </summary>
    public static class ChibiPercentConst
    {
        /// <summary>Milli-percent per HP (spec 4.1): 200 = 0.2 % per HP, 5 HP = 1 %. Pct(u) = (u x 200 + 500 000) / 1 000 000.</summary>
        public const int PctPerHpM = 200;
        /// <summary>Percent saturation, milli-percent (999.9 %, spec 5).</summary>
        public const int PctCapM = 999_900;
        /// <summary>Cap of the hit's percent d inside the knockback formula only (GL-2: 15 %).</summary>
        public const int DmgTermCapM = 15_000;
        /// <summary>Growth sP = KbgBase + mag / KbgDiv (permille, spec 4.4).</summary>
        public const int KbgBase = 250, KbgDiv = 250;
        /// <summary>Base bM = BkbBase + mag x BkbNum / BkbDen (milli-KB, spec 4.4).</summary>
        public const int BkbBase = 10_000, BkbNum = 2, BkbDen = 15;
        /// <summary>Clamp of the KbX/KbY magnitude used for growth and base (spec 4.4).</summary>
        public const int MagCap = 250_000;
        /// <summary>Base bonus of an ult hit (slot 5, its programs, projectiles and summons; spec 4.4).</summary>
        public const int UltBkbBonusM = 80_000;
        /// <summary>Knockback multiplier of Ranged (Ren) and Magic (Lyra), permille (GL-2: x0.85, replaces HalfKnockback).</summary>
        public const int ZonerKbP = 850;
        /// <summary>Below this KbX/KbY magnitude a hit is set knockback (legacy velocity and stun, spec 4.5).</summary>
        public const int SetKbMag = 30_000;
        /// <summary>Weight term numerator 200 x 1.4 (spec 4.2) and the weight offset (w + 100).</summary>
        public const int WeightNum = 280, WeightOff = 100;
        /// <summary>Constant term of the growth bracket, milli-KB (Smash 18).</summary>
        public const int KbConstM = 18_000;
        /// <summary>Knockback cap, milli-KB (spec 4.2).</summary>
        public const int KbCapM = 400_000;
        /// <summary>Launch speed per KB in velocity sub-units per tick (spec 4.6: 1530 sub = 1.7 milli/tick).</summary>
        public const int LsSubPerKb = 1530;
        /// <summary>Launch decay along the launch, sub/tick (3 milli/tick^2, spec 4.6).</summary>
        public const int LaunchDecay = 2700;
        /// <summary>Gravity fades from 100 % at KB 60 to 0 % at KB 120 while in hitstun (spec 4.6).</summary>
        public const int GravFullKbM = 60_000, GravZeroKbM = 120_000;
        /// <summary>Hitstun = KB x 4 / 10 (KbStunNum / KbStunDen on milli-KB), capped at HitstunCap ticks (spec 4.6).</summary>
        public const int KbStunNum = 4, KbStunDen = 10_000, HitstunCap = 90;
        /// <summary>
        /// Lead decision GL-3 R2: the legacy stun floor applies only to weak hits (KbX/KbY magnitude below StrongKbMag:
        /// jabs, tilts keep their combo timing); strong launches use KB x 0.4 clamped to [HitstunMin, HitstunCap].
        /// </summary>
        public const int StrongKbMag = 100_000, HitstunMin = 10;
        /// <summary>Armor holds below this knockback (spec 3.2 step 7).</summary>
        public const int ArmorBreakKbM = 150_000;
        /// <summary>Stocks at match start (spec 3.4).</summary>
        public const int StocksStart = 3;
        /// <summary>Game ticks off-screen after a KO, max hover ticks on the respawn point, invuln after the hover (spec 7).</summary>
        public const int RespawnDelay = 60, RespawnHoverMax = 180, RespawnInvuln = 120;
        /// <summary>Ult gain is doubled at or above this percent (spec 3.2 step 8, was "under 30 % HP").</summary>
        public const int ComebackPctM = 100_000;
        /// <summary>CPU kill read: opponent percent &gt;= (Weight + offset) x 1000 (spec 3.5). Read by the CPU only.</summary>
        public const int CpuKillPctOffset = 10;
        /// <summary>
        /// Rules v24 escape dash (decision ED-1, 2026-11-17): a Dash press while launched (real knockback, not set
        /// knockback) once LaunchAge &gt; EscapeMinAge, with DashCd 0, scales the launch velocity by EscapeSpeedP permille,
        /// ends the hitstun and starts the dash cooldown. No invulnerability. LaunchAge saturates at LaunchAgeCap.
        /// </summary>
        public const int EscapeMinAge = 10, EscapeSpeedP = 500, LaunchAgeCap = 1000;

        /// <summary>Every constant above, mixed into the config hash (rules v20).</summary>
        public static readonly int[] Constants =
        {
            PctPerHpM, PctCapM, DmgTermCapM, KbgBase, KbgDiv, BkbBase, BkbNum, BkbDen, MagCap, UltBkbBonusM, ZonerKbP, SetKbMag,
            WeightNum, WeightOff, KbConstM, KbCapM, LsSubPerKb, LaunchDecay, GravFullKbM, GravZeroKbM, KbStunNum, KbStunDen,
            HitstunCap, StrongKbMag, HitstunMin, ArmorBreakKbM, StocksStart, RespawnDelay, RespawnHoverMax, RespawnInvuln, ComebackPctM, CpuKillPctOffset,
            EscapeMinAge, EscapeSpeedP, LaunchAgeCap,
        };
    }

    public sealed partial class ChibiFighterSim
    {
        // ---- rules v20 (percent-knockback.md 3.1). All rolled back and hashed (CopyPercent / HashPercent).
        /// <summary>Damage percent in milli-percent (1000 = 1.0 %), 0..<see cref="ChibiPercentConst.PctCapM"/>. Only goes up, except lifesteal and respawn.</summary>
        public int PctM;
        /// <summary>Stocks left (starts <see cref="ChibiPercentConst.StocksStart"/>). Unchanged by KOs in training.</summary>
        public int Stocks;
        /// <summary>&gt; 0: KO'd and off-screen, game ticks until the respawn. Position stays at the crossing point (view: blast effect).</summary>
        public int OutTicks;
        /// <summary>&gt; 0: on the respawn point, gravity off, untouchable; ends on any input or at 0, then Invuln 120.</summary>
        public int HoverTicks;
        /// <summary>Per-tick launch decay in velocity sub-units while in hitstun; DecY carries the sign of the launch's Y.</summary>
        public int LaunchDecX, LaunchDecY;
        /// <summary>Gravity permille while in hitstun (1000 = normal).</summary>
        public int LaunchGravP = 1000;
        /// <summary>
        /// Rules v24: game ticks since the current knockback launch started (1 on the hit tick), 0 when not launched
        /// (no hitstun, or set knockback). Gates the escape dash (<see cref="ChibiPercentConst.EscapeMinAge"/>).
        /// </summary>
        public int LaunchAge;
        /// <summary>True while a knockback launch is running and the escape dash may still be used.</summary>
        public bool CanEscapeDash => Stun > 0 && LaunchAge > ChibiPercentConst.EscapeMinAge && DashCd == 0 && !Held;
        /// <summary>KOs taken this match (stats; training counts too). Replaces the v16 Revives counter.</summary>
        public int KosTaken;

        /// <summary>Knockback weight (spec 4.3): MaxHp / 5 (Lyra 75, Ren 80, Kaia 100, Taro 100, Bruna 120).</summary>
        public int Weight => Stats.Weight;
        /// <summary>True while the fighter is on stage and hittable by the rules (not Out, not hovering).</summary>
        public bool InPlay => OutTicks == 0 && HoverTicks == 0;

        /// <summary>
        /// COMPATIBILITY ONLY (rules v20 removed HP from the state): MaxHp in micro-HP while the fighter has stocks, 0 once it
        /// has none. Kept so presentation / CPU code that still reads HpU compiles until it moves to PctM / Stocks; the sim
        /// never reads it. Remove once no reader is left.
        /// </summary>
        public int HpU => Stocks > 0 ? Stats.MaxHp * ChibiCombatStats.HpScale : 0;
        /// <summary>COMPATIBILITY ONLY: whole-HP form of <see cref="HpU"/>. The sim never reads it.</summary>
        public int Hp => Stocks > 0 ? Stats.MaxHp : 0;
        /// <summary>COMPATIBILITY ONLY: the v16 training revive counter, now <see cref="KosTaken"/>.</summary>
        public int Revives => KosTaken;

        /// <summary>Match-start values (InitFighter).</summary>
        public void ResetPercent()
        {
            PctM = 0; Stocks = ChibiPercentConst.StocksStart; OutTicks = 0; HoverTicks = 0;
            LaunchDecX = 0; LaunchDecY = 0; LaunchGravP = 1000; LaunchAge = 0; KosTaken = 0;
        }

        /// <summary>
        /// Rules v24: call right after a knockback writes the launch fields. Starts LaunchAge only for a strong launch
        /// (<paramref name="strong"/>: KbX/KbY magnitude &gt;= StrongKbMag, the same split as the hitstun rule), so weak hits
        /// (jabs, tilts) and set knockback keep their combo timing and cannot be escaped. A held fighter gets no launch (its
        /// velocity stays 0), so a hit while held cannot be escaped either.
        /// </summary>
        public void StartLaunchAge(bool strong) =>
            LaunchAge = strong && !Held && Stun > 0 && (LaunchDecX != 0 || LaunchDecY != 0 || LaunchGravP != 1000) ? 1 : 0;

        /// <summary>Clears the hitstun launch fields (end of hitstun, set knockback, StunF, KO).</summary>
        public void ClearLaunch() { LaunchDecX = 0; LaunchDecY = 0; LaunchGravP = 1000; LaunchAge = 0; }

        void CopyPercent(ChibiFighterSim o)
        {
            PctM = o.PctM; Stocks = o.Stocks; OutTicks = o.OutTicks; HoverTicks = o.HoverTicks;
            LaunchDecX = o.LaunchDecX; LaunchDecY = o.LaunchDecY; LaunchGravP = o.LaunchGravP; LaunchAge = o.LaunchAge; KosTaken = o.KosTaken;
        }

        void HashPercent(ref uint h)
        {
            ChibiSimState.Mix(ref h, PctM); ChibiSimState.Mix(ref h, Stocks); ChibiSimState.Mix(ref h, OutTicks);
            ChibiSimState.Mix(ref h, HoverTicks); ChibiSimState.Mix(ref h, LaunchDecX); ChibiSimState.Mix(ref h, LaunchDecY);
            ChibiSimState.Mix(ref h, LaunchGravP); ChibiSimState.Mix(ref h, LaunchAge); ChibiSimState.Mix(ref h, KosTaken);
        }
    }

    public static partial class ChibiSimulation
    {
        // ================================================================== formulas (percent-knockback.md section 4)

        /// <summary>Micro-HP to milli-percent, half-up (spec 4.1): Pct(18 000 000) = 3600 (18 HP = 3.6 %).</summary>
        public static int Pct(long microHp) =>
            microHp <= 0 ? 0 : (int)((microHp * ChibiPercentConst.PctPerHpM + 500_000) / 1_000_000);

        /// <summary>Attacker class knockback permille: Ranged / Magic 850, others 1000 (GL-2).</summary>
        public static int ClassKbP(int cls) =>
            cls == (int)FighterClass.Ranged || cls == (int)FighterClass.Magic ? ChibiPercentConst.ZonerKbP : 1000;

        /// <summary>Growth sP (permille) from the clamped KbX/KbY magnitude (spec 4.4): H smash 187 500 -> 1000.</summary>
        public static int GrowthP(int mag) => ChibiPercentConst.KbgBase + Math.Min(mag, ChibiPercentConst.MagCap) / ChibiPercentConst.KbgDiv;

        /// <summary>Base bM (milli-KB) from the clamped magnitude (spec 4.4): H smash -> 35 000; ult hits + 80 000.</summary>
        public static int BaseM(int mag, bool ult) =>
            ChibiPercentConst.BkbBase + Math.Min(mag, ChibiPercentConst.MagCap) * ChibiPercentConst.BkbNum / ChibiPercentConst.BkbDen +
            (ult ? ChibiPercentConst.UltBkbBonusM : 0);

        /// <summary>
        /// Knockback in milli-KB (spec 4.2, integer form): x = p/10 + p·min(d, 15 %)/20 000, y = x·280/(w + 100) + 18 000,
        /// KB = (y·sP/1000 + bM)·classKbP/1000, clamped to [0, KbCapM]. Worked example (4.7): Kaia fsmash on Kaia at 0 %
        /// (p = d = 10 920, w 100, mag 187 500) = 62 875.
        /// </summary>
        /// <param name="pctAfterM">Defender percent after this hit, milli.</param>
        /// <param name="hitPctM">This hit's percent, milli.</param>
        /// <param name="weight">Defender weight (75..120).</param>
        /// <param name="mag">ISqrt(KbX² + KbY²) of the pending hit.</param>
        /// <param name="ult">The hit belongs to an ultimate.</param>
        /// <param name="classKbP">Attacker <see cref="ClassKbP"/>.</param>
        public static int KnockbackM(int pctAfterM, int hitPctM, int weight, int mag, bool ult, int classKbP)
        {
            long p = pctAfterM, d = Math.Min(hitPctM, ChibiPercentConst.DmgTermCapM);
            long x = p / 10 + p * d / 20_000;
            long y = x * ChibiPercentConst.WeightNum / (weight + ChibiPercentConst.WeightOff) + ChibiPercentConst.KbConstM;
            long kb = y * GrowthP(mag) / 1000 + BaseM(mag, ult);
            kb = kb * classKbP / 1000;
            return (int)(kb < 0 ? 0 : (kb > ChibiPercentConst.KbCapM ? ChibiPercentConst.KbCapM : kb));
        }

        /// <summary>Hitstun from knockback (spec 4.6): min(90, max(legacy stun, KB x 0.4)).</summary>
        public static int HitstunFromKb(int kbM, int legacyStun) =>
            Math.Min(ChibiPercentConst.HitstunCap, Math.Max(legacyStun, kbM * ChibiPercentConst.KbStunNum / ChibiPercentConst.KbStunDen));

        /// <summary>
        /// Hitstun of a variable-knockback hit (spec 4.6 amended by GL-3 R2): weak hits (magnitude &lt; 100 000) keep
        /// <see cref="HitstunFromKb"/> with the legacy floor; strong hits use clamp(KB x 0.4, 10, 90).
        /// </summary>
        public static int HitstunFor(int kbM, int mag, int legacyStun)
        {
            if (mag < ChibiPercentConst.StrongKbMag) return HitstunFromKb(kbM, legacyStun);
            int t = kbM * ChibiPercentConst.KbStunNum / ChibiPercentConst.KbStunDen;
            return t < ChibiPercentConst.HitstunMin ? ChibiPercentConst.HitstunMin : (t > ChibiPercentConst.HitstunCap ? ChibiPercentConst.HitstunCap : t);
        }

        /// <summary>Hitstun gravity permille (spec 4.6): 1000 at KB &lt;= 60, 0 at KB &gt;= 120, linear between.</summary>
        public static int LaunchGravityP(int kbM)
        {
            int g = (int)((long)(ChibiPercentConst.GravZeroKbM - kbM) * 1000 /
                          (ChibiPercentConst.GravZeroKbM - ChibiPercentConst.GravFullKbM));
            return g < 0 ? 0 : (g > 1000 ? 1000 : g);
        }

        /// <summary>Result of <see cref="ComputeLaunch"/>: what an unblocked hit does to the defender's motion.</summary>
        struct Launch
        {
            public int KbM, Vx, Vy, Stun, DecX, DecY, GravP;
            /// <summary>Set knockback (spec 4.5): legacy velocity and stun, no decay, normal gravity.</summary>
            public bool Set;
        }

        /// <summary>
        /// Spec 3.2 step 6 + 4.4-4.6: knockback from the defender's percent (already including this hit), the hit's percent,
        /// weight, the KbX/KbY magnitude, ult bonus and attacker class; set knockback (mag &lt; 30 000, grounded trip, mag 0)
        /// keeps the legacy velocity (Dir·KbX, KbY) and stun. <paramref name="legacyStun"/> is h.Stun with the guard-break floor.
        /// </summary>
        static Launch ComputeLaunch(ChibiFighterSim A, ChibiFighterSim D, in PendingHit h, int hitPctM, int legacyStun)
        {
            long kx = h.KbX, ky = h.KbY;
            int mag = (int)ChibiFixed.ISqrt(kx * kx + ky * ky);
            var l = new Launch
            {
                KbM = KnockbackM(D.PctM, hitPctM, D.Weight, mag, h.Ult, ClassKbP(A.Class)),
                GravP = 1000,
            };
            if (mag < ChibiPercentConst.SetKbMag || h.Trip)
            {
                l.Set = true;
                l.Vx = h.Dir * h.KbX; l.Vy = h.KbY; l.Stun = legacyStun;
                return l;
            }
            l.Stun = HitstunFor(l.KbM, mag, legacyStun);
            long ls = (long)l.KbM * ChibiPercentConst.LsSubPerKb / 1000;
            l.Vx = h.Dir * (int)(ls * Math.Abs(kx) / mag);
            l.Vy = (int)(ls * ky / mag);
            l.DecX = (int)((long)ChibiPercentConst.LaunchDecay * Math.Abs(kx) / mag);
            l.DecY = (int)((long)ChibiPercentConst.LaunchDecay * ky / mag);
            l.GravP = LaunchGravityP(l.KbM);
            return l;
        }

        /// <summary>Rules v24: a strong launch (KbX/KbY magnitude &gt;= StrongKbMag) may be escaped (ED-1).</summary>
        static bool StrongKb(int kbX, int kbY)
        {
            long kx = kbX, ky = kbY;
            return kx * kx + ky * ky >= (long)ChibiPercentConst.StrongKbMag * ChibiPercentConst.StrongKbMag;
        }

        /// <summary>Adds <paramref name="addM"/> milli-percent, saturating at PctCapM.</summary>
        static void AddPct(ChibiFighterSim f, int addM)
        {
            f.PctM = (int)Math.Min(ChibiPercentConst.PctCapM, (long)f.PctM + addM);
        }

        // ================================================================== hitstun physics (spec 3.3)

        /// <summary>
        /// Stun branch of UpdateFighter, before Physics: Vx approaches 0 by DecX; Vy approaches 0 by |DecY| only while it
        /// still moves with the launch (same sign), so the decay brakes the launch and never the fall.
        /// </summary>
        static void LaunchDecayStep(ChibiFighterSim f)
        {
            if (f.LaunchDecX != 0) f.Vx = Approach(f.Vx, 0, f.LaunchDecX);
            if (f.LaunchDecY != 0 && f.Vy != 0 && Sign(f.Vy) == Sign(f.LaunchDecY))
                f.Vy = Approach(f.Vy, 0, Math.Abs(f.LaunchDecY));
        }

        /// <summary>
        /// Rules v24 escape dash (ED-1): on a Dash press edge while <see cref="ChibiFighterSim.CanEscapeDash"/>, scale the
        /// launch velocity by EscapeSpeedP permille, end the hitstun and the launch, start the dash cooldown. Returns true
        /// when it fired (the caller then skips this tick's launch decay). No invulnerability, no dash motion.
        /// </summary>
        static bool TryEscapeDash(ChibiSimState s, ChibiFighterSim f, int edges)
        {
            if ((edges & (int)ChibiButtons.Dash) == 0 || !f.CanEscapeDash || !InputEnabled(s)) return false;
            f.Vx = (int)((long)f.Vx * ChibiPercentConst.EscapeSpeedP / 1000);
            f.Vy = (int)((long)f.Vy * ChibiPercentConst.EscapeSpeedP / 1000);
            f.Stun = 0;
            f.ClearLaunch();
            f.DashCd = ChibiSimConst.DashCooldown;
            f.EscapeDashes++;
            return true;
        }

        /// <summary>Gravity this tick: Gravity x LaunchGravP / 1000 while in hitstun, else Gravity.</summary>
        static int GravityOf(ChibiFighterSim f) =>
            f.Stun > 0 && f.LaunchGravP != 1000 ? ChibiSimConst.Gravity * f.LaunchGravP / 1000 : ChibiSimConst.Gravity;

        // ================================================================== out / hover / respawn (spec 3.4)

        /// <summary>
        /// Top of UpdateFighter: a KO'd fighter waits off-screen (nothing else runs) and respawns when OutTicks reaches 0;
        /// a hovering fighter stays still on the respawn point until any direction / button or the hover limit, then gets
        /// RespawnInvuln and plays this tick normally. Returns true when the rest of UpdateFighter must be skipped.
        /// </summary>
        static bool OutAndHover(ChibiSimState s, int i, ChibiInputPacket input, int edges)
        {
            var f = s.F[i];
            if (f.OutTicks > 0)
            {
                if (Eliminated(s, i)) return true;   // rules v25: an eliminated / dropped fighter stays out (never in 1v1)
                if (--f.OutTicks == 0) Respawn(s, i);
                return true;
            }
            if (f.HoverTicks <= 0) return false;
            f.HoverTicks--;
            if ((input.Raw | edges) != 0 || f.HoverTicks == 0)
            {
                f.HoverTicks = 0;
                f.Invuln = Math.Max(f.Invuln, ChibiPercentConst.RespawnInvuln);
                return false;
            }
            f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = false;
            return true;
        }

        /// <summary>
        /// Respawn (spec 3.4): stage respawn point, percent 0, still, airborne, hovering; statuses cleared, guard refilled,
        /// air jump and aironce moves restored, parry / spin guard / pull ended. Cooldowns and Ult are kept.
        /// </summary>
        static void Respawn(ChibiSimState s, int i)
        {
            var f = s.F[i];
            RespawnPoint(s, out int rx, out int ry);
            f.X = rx; f.Y = ry; f.XRem = 0; f.YRem = 0; f.Vx = 0; f.Vy = 0;
            f.Grounded = false; f.Stun = 0; f.ClearLaunch();
            f.PctM = 0;
            f.HoverTicks = ChibiPercentConst.RespawnHoverMax;
            f.SlowTicks = f.WeakenTicks = f.SluggishTicks = f.ArmorBreakTicks = 0;
            f.GuardBrokenTicks = 0; f.GuardU = ChibiCombatStats.GuardMaxU(f.Stats.Defense);
            f.AirUsedMask = 0; f.AirJumps = 1;
            f.ParryTicks = 0; f.GuardTicks = 0; f.PullTicks = 0;
            f.DropTicks = 0; f.DropPlatform = -1;
        }

        /// <summary>KO of fighter <paramref name="i"/> (spec 3.4): release holds, lose a stock (not in training), go Out.</summary>
        static void KnockOut(ChibiSimState s, int i)
        {
            var f = s.F[i];
            // Existing ring-out release (F:692-697): Interrupt self (frees a held victim); a held fighter frees its holder.
            Interrupt(s, i);
            if (f.Held)
            {
                int hb = HolderOf(s, i);             // rules v25: the actual holder (1v1: the opponent)
                if (hb >= 0) Interrupt(s, hb);
                f.Held = false; f.HeldBy = -1;
            }
            if (!s.Training && f.Stocks > 0) f.Stocks--;
            f.KosTaken++;
            f.OutTicks = ChibiPercentConst.RespawnDelay;
            f.HoverTicks = 0;
            f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Stun = 0; f.ClearLaunch();
            f.Grounded = false;
        }

        /// <summary>
        /// Timeout winner (spec 3.4, GL-2): more stocks wins; equal -> lower percent wins; equal -> draw (-1).
        /// </summary>
        public static int TimeoutWinner(ChibiFighterSim a, ChibiFighterSim b)
        {
            if (a.Stocks != b.Stocks) return a.Stocks > b.Stocks ? 0 : 1;
            if (a.PctM != b.PctM) return a.PctM < b.PctM ? 0 : 1;
            return -1;
        }

        /// <summary>
        /// End of a game step (spec 3.4, only while running): KO on an active edge (inactive edges were clamped in Physics),
        /// then last-stock check (same-tick double last stock = draw, D-F1), then the match timer (not in training). A KO on
        /// the last timer tick counts because it runs first.
        /// </summary>
        static void ResolveBoundsAndMatchEnd(ChibiSimState s)
        {
            if (!s.Running) return;
            var st = ChibiStageTable.Get(s.Stage);
            for (int i = 0; i < s.Count; i++)
            {
                var f = s.F[i];
                if (f.OutTicks > 0) continue;
                if (st.CrossesKoEdge(f.X, f.Y)) KnockOut(s, i);
            }
            if (s.Training) return;   // no stock loss and no timer in training (MC:68)

            // Rules v25: a team is alive while any member has stocks; the match ends when at most one team is alive
            // (none = draw, D-F1). In 1v1 each fighter is its own team, which is the v24 rule.
            UpdatePlacements(s);
            int alive = AliveTeams(s, out int lastTeam);
            if (alive <= 1)
            {
                s.MatchOver = true;
                s.Winner = alive == 1 ? lastTeam : -1;
                FinishPlacements(s);
                return;
            }
            s.MatchClock++;
            if (s.MatchClock >= ChibiSimConst.MatchTicks)
            {
                s.MatchOver = true;
                s.Winner = TeamTimeoutWinner(s);
                FinishPlacements(s);
            }
        }
    }
}
