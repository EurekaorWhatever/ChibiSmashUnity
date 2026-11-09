// Story type: Logic (damage rules: formula, defense, block/guard, parries, passives, roll order) -- evidence: automated
// EditMode unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/damage-conversion.md (section 0 ground truth, 1c formulas, 1d per-class table,
// sections 2-6 rules + roll order, section 8 contract for RulesVersion 9, section 9 reference values + tolerances,
// section 10 known mismatches). Original: Core/CombatAuthority.cs (CA:), Fighter/Fighter.cs (F:), Core/ClassData.cs.
//
// User decision applied: D9-1 = (a). HP and guard are stored exactly in micro-HP (section 1b) and damage is computed
// exactly, matching the original's float HP. Tolerance vs the original float is +-100 micro-HP (section 9 header).
//
// UNITS: RulesVersion 9 renames Hp -> HpU (micro-HP) and GuardHp10 -> GuardU (micro-HP) (contract item 2). This file
// reads and writes HP and guard ONLY through HpOf/SetHp/GuardOf/SetGuard, which use reflection: the micro field when it
// exists, otherwise today's field (Hp in whole HP, GuardHp10 in tenths). So the file compiles today and after the change,
// and it FAILS today wherever today's truncating integer damage differs from the original (for example 75.6 vs 75).
//
// RNG forcing (section 9): there is no production hook. RngFind searches candidate states from a fixed start until the
// next k values of NextU32() % 1000 satisfy the predicates, then the test writes s.Rng.State right before the tick on
// which the hit resolves. Every other tick is pinned to the "Quiet" state (no crit, ward, hex or armor break), so the
// tick of the hit is found by a dry run on a copy of the state and the real run stays identical up to that tick.
// Draw counts: s.Rng.State after the hit is compared with a copy of the forced state advanced exactly N times.
//
// Arena: Sky Dojo (stage 0). Melee columns X 6500 / 7300 and shot columns 6300 / 7500 are platform-free
// (ChibiNormalsParityTests, stage-conversion.md section 1). Attacker is F[0] facing +1, defender is F[1] facing -1.
// Defender passives carry no defensive effect unless a test says so (Magic P1 Ward is used only by the Ward tests).
using System;
using System.Collections.Generic;
using System.Reflection;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Damage-rule parity between the deterministic 60 Hz simulation and the original CombatAuthority / Fighter.ReceiveHit.
    /// Reference values and tolerances: damage-conversion.md section 9.
    /// </summary>
    public class ChibiDamageParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;
        const int SkyDojo = 0;
        const int MaxHitSearch = 150;           // ticks searched for the tick on which the hit resolves

        // ---- tolerances (doc section 9 header: +-100 micro-HP vs the original float)
        const double HpTol = 0.0001;

        // ---- arena
        const int MeleeAX = 6500, MeleeVX = 7300;
        const int ShotAX = 6300, ShotVX = 7500;

        // ---- passives (ChibiCombatStats.Create, doc section 5)
        const int SwiftBlade = 0, BloodEdge = 1;          // Melee
        const int Crusher = 0, Juggernaut = 1;            // Heavy (both x1.2, F:112-121)
        const int EagleEye = 0, RangedOther = 1;          // Ranged
        const int HexPassive = 0, WardPassive = 1;        // Magic
        const int ArmorBreakPassive = 0, PatiencePassive = 1; // Polearm

        // ---- rules (doc sections 2-5)
        const int ParryWindow = 11;                       // ShieldTick 0..10 parries (section 3a)
        const int PerfectParryStun = 27;                  // 0.45 s (F:563-572)
        const int ReflectStun = 30;                       // 0.5 s (F:550-561)
        const int ReflectPushX = 75_000, ReflectPushY = 60_000; // 5 / 4 u/s in sub units (section 3b)
        const int GuardBreakStun = 60, GuardBrokenTicks = 180;  // 1.0 s / 3 s (section 2)
        const int HexTicks = 240, ArmorBreakTicks = 300;  // 4 s / 5 s (section 5)
        const int LightStun = 17;                         // ceil(0.28 * 60) (normals-conversion section 2)
        const int UltPerParry = 150, UltWhenHit = 15, UltBlockedAttacker = 10;
        const int UltPerHit = 30, UltPerComboHit = 50;    // 0..1000 scale (section 4 item 6)
        const int ShieldLead = 20;                        // ticks of shield before a hit: > ParryWindow, normal block
        const int ParryAttackDelay = 20;                  // light pressed on this scenario tick in the parry-window test
        const int LongTimer = 600;                        // a status timer that cannot run out inside one test
        const double RegenPerTick = 0.4;                  // 24 HP/s at 60 Hz (F:202-203)

        static readonly ChibiInputPacket Neutral = default;

        // ------------------------------------------------------------------ RNG forcing (doc section 9)

        static bool NoProc(int v) => v >= 250;            // misses crit (50 / 250 Eagle Eye), ward 200, hex/AB 100
        static bool CritOn(int v) => v < 50;
        static bool ProcOn(int v) => v < 100;             // Hex / Armor Break 10 %
        static bool WardOnNoCrit(int v) => v >= 50 && v < 200;
        static bool EagleEyeOnly(int v) => v >= 50 && v < 250;  // crits at 250 permille, not at 50
        const uint SearchStart = 1;

        static uint RngFind(uint start, params Func<int, bool>[] want)
        {
            for (uint c = start; c != 0; c++)
            {
                var r = new ChibiRng { State = c };
                bool ok = true;
                foreach (var w in want)
                    if (!w((int)(r.NextU32() % 1000))) { ok = false; break; }
                if (ok) return c;
            }
            throw new InvalidOperationException("RngFind: no state found");
        }

        static uint Advance(uint state, int n)
        {
            var r = new ChibiRng { State = state };
            for (int i = 0; i < n; i++) r.NextU32();
            return r.State;
        }

        static readonly uint Quiet = RngFind(SearchStart, NoProc, NoProc, NoProc, NoProc);

        // ------------------------------------------------------------------ unit-agnostic HP / guard (contract item 2)

        const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;
        static readonly FieldInfo HpUField = typeof(ChibiFighterSim).GetField("HpU", Pub);
        static readonly FieldInfo HpField = typeof(ChibiFighterSim).GetField("Hp", Pub);
        static readonly FieldInfo GuardUField = typeof(ChibiFighterSim).GetField("GuardU", Pub);
        static readonly FieldInfo Guard10Field = typeof(ChibiFighterSim).GetField("GuardHp10", Pub);
        const double MicroPerHp = 1_000_000.0;            // HpScale (contract item 3)
        const double TenthsPerHp = 10.0;                  // today's GuardHp10

        static double HpOf(ChibiFighterSim f) =>
            HpUField != null ? Convert.ToDouble(HpUField.GetValue(f)) / MicroPerHp : Convert.ToDouble(HpField.GetValue(f));

        static void SetHp(ChibiFighterSim f, double hp)
        {
            if (HpUField != null) HpUField.SetValue(f, (int)Math.Round(hp * MicroPerHp));
            else HpField.SetValue(f, (int)Math.Round(hp));
        }

        static double GuardOf(ChibiFighterSim f) =>
            GuardUField != null ? Convert.ToDouble(GuardUField.GetValue(f)) / MicroPerHp
                                : Convert.ToDouble(Guard10Field.GetValue(f)) / TenthsPerHp;

        static void SetGuard(ChibiFighterSim f, double g)
        {
            if (GuardUField != null) GuardUField.SetValue(f, (int)Math.Round(g * MicroPerHp));
            else Guard10Field.SetValue(f, (int)Math.Round(g * TenthsPerHp));
        }

        // ------------------------------------------------------------------ match helpers

        /// <summary>Defender passive with no defensive effect (Magic P0 Hex acts only when attacking).</summary>
        static int DefPassive(FighterClass c) =>
            c == FighterClass.Ranged ? EagleEye : c == FighterClass.Magic ? HexPassive : 1;

        static bool IsShooter(FighterClass c) => c == FighterClass.Ranged || c == FighterClass.Magic;

        static ChibiSimState NewMatch(FighterClass a, int ap, FighterClass d, int dp)
        {
            var cfg = new ChibiMatchConfig { Seed = Seed, Class0 = a, Passive0 = ap, Class1 = d, Passive1 = dp, Stage = SkyDojo };
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            for (int t = 0; t < SettleTicks; t++) Tick(s, ChibiButtons.None, ChibiButtons.None);
            return s;
        }

        static void Put(ChibiFighterSim f, int x, int y, int facing)
        {
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0;
            f.Grounded = true; f.AirJumps = 1; f.Facing = facing; f.Stun = 0; f.Invuln = 0;
        }

        /// <summary>Attacker F[0] facing +1, defender F[1] facing -1, melee or shot columns by attacker class.</summary>
        static ChibiSimState Arena(FighterClass a, int ap, FighterClass d, int dp)
        {
            var s = NewMatch(a, ap, d, dp);
            bool shot = IsShooter(a);
            Put(s.F[0], shot ? ShotAX : MeleeAX, 0, 1);
            Put(s.F[1], shot ? ShotVX : MeleeVX, 0, -1);
            for (int t = 0; t < 3; t++) Tick(s, ChibiButtons.None, ChibiButtons.None);
            s.F[0].Ult = 0; s.F[1].Ult = 0;
            return s;
        }

        static ChibiInputPacket P(ChibiButtons b) => ChibiInputPacket.Make(ChibiDir.Neutral, b);

        /// <summary>One GAME step (counts game steps (D10): frozen hitstop ticks are consumed) with the RNG pinned to <see cref="Quiet"/> (nothing procs).</summary>
        static void Tick(ChibiSimState s, ChibiButtons a, ChibiButtons d)
        {
            s.Rng.State = Quiet;
            ChibiTestTime.StepGame(s, P(a), P(d));
        }

        static Func<int, ChibiButtons> On(ChibiButtons b, params int[] ticks) =>
            t => Array.IndexOf(ticks, t) >= 0 ? b : ChibiButtons.None;

        static Func<int, ChibiButtons> Hold(ChibiButtons b) => t => b;
        static readonly Func<int, ChibiButtons> Idle = t => ChibiButtons.None;

        static ChibiSimState Clone(ChibiSimState s)
        {
            var c = new ChibiSimState();
            c.CopyFrom(s);
            return c;
        }

        static void RaiseShield(ChibiSimState s, int ticks = ShieldLead)
        {
            for (int t = 0; t < ticks; t++) Tick(s, ChibiButtons.None, ChibiButtons.Shield);
        }

        /// <summary>Dry run on a copy: first scenario tick on which the defender loses HP or parries.</summary>
        static int FindHitTick(ChibiSimState s, Func<int, ChibiButtons> a, Func<int, ChibiButtons> d)
        {
            var c = Clone(s);
            for (int t = 0; t < MaxHitSearch; t++)
            {
                double hp = HpOf(c.F[1]);
                int parries = c.F[1].Parries;
                Tick(c, a(t), d(t));
                if (HpOf(c.F[1]) < hp || c.F[1].Parries > parries) return t;
            }
            Assert.Fail($"precondition: no hit resolved within {MaxHitSearch} ticks");
            return -1;
        }

        /// <summary>Advances <paramref name="s"/> to the START of the hit tick (quiet RNG) and returns that tick.</summary>
        static int Prime(ChibiSimState s, Func<int, ChibiButtons> a, Func<int, ChibiButtons> d)
        {
            int h = FindHitTick(s, a, d);
            for (int t = 0; t < h; t++) Tick(s, a(t), d(t));
            return h;
        }

        /// <summary>Runs the hit tick with the RNG forced to <paramref name="rng"/>.</summary>
        static void Resolve(ChibiSimState s, Func<int, ChibiButtons> a, Func<int, ChibiButtons> d, int h, uint rng)
        {
            s.Rng.State = rng;
            ChibiTestTime.StepGame(s, P(a(h)), P(d(h)));   // counts game steps (D10)
        }

        /// <summary>Defender HP lost on the hit tick.</summary>
        static double StrikeDamage(ChibiSimState s, Func<int, ChibiButtons> a, Func<int, ChibiButtons> d, uint rng)
        {
            int h = Prime(s, a, d);
            double hp = HpOf(s.F[1]);
            Resolve(s, a, d, h, rng);
            return hp - HpOf(s.F[1]);
        }

        /// <summary>Quiet run; returns the defender HP lost per tick on which it dropped.</summary>
        static List<double> HitDamages(ChibiSimState s, int ticks, Func<int, ChibiButtons> a, Action<int> before = null)
        {
            var dmg = new List<double>();
            for (int t = 0; t < ticks; t++)
            {
                before?.Invoke(t);
                double hp = HpOf(s.F[1]);
                Tick(s, a(t), ChibiButtons.None);
                if (HpOf(s.F[1]) < hp) dmg.Add(hp - HpOf(s.F[1]));
            }
            return dmg;
        }

        // ------------------------------------------------------------------ section 9 unblocked table

        static readonly FighterClass[] Defenders =
            { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic, FighterClass.Polearm };

        sealed class AttackerRow
        {
            public FighterClass Cls; public int Passive; public double[] Light, Heavy, Crit;
        }

        // Doc section 9 "Unblocked damage, non-crit" table; defender order Melee, Heavy, Ranged, Magic, Polearm.
        static readonly AttackerRow[] Attackers =
        {
            new AttackerRow { Cls = FighterClass.Melee, Passive = SwiftBlade,
                Light = new[] { 18.0, 17, 19, 19, 18 }, Heavy = new[] { 34.0, 33, 35, 35, 34 }, Crit = new[] { 52.0, 51, 53, 53, 52 } },
            new AttackerRow { Cls = FighterClass.Magic, Passive = WardPassive,
                Light = new[] { 18.0, 17, 19, 19, 18 }, Heavy = new[] { 34.0, 33, 35, 35, 34 }, Crit = new[] { 52.0, 51, 53, 53, 52 } },
            new AttackerRow { Cls = FighterClass.Heavy, Passive = Crusher,
                Light = new[] { 40.0, 39, 41, 41, 40 }, Heavy = new[] { 73.6, 72.6, 74.6, 74.6, 73.6 }, Crit = new[] { 111.4, 110.4, 112.4, 112.4, 111.4 } },
            new AttackerRow { Cls = FighterClass.Heavy, Passive = Juggernaut,
                Light = new[] { 40.0, 39, 41, 41, 40 }, Heavy = new[] { 73.6, 72.6, 74.6, 74.6, 73.6 }, Crit = new[] { 111.4, 110.4, 112.4, 112.4, 111.4 } },
            new AttackerRow { Cls = FighterClass.Ranged, Passive = RangedOther,
                Light = new[] { 13.0, 12, 14, 14, 13 }, Heavy = new[] { 25.0, 24, 26, 26, 25 }, Crit = new[] { 38.5, 37.5, 39.5, 39.5, 38.5 } },
            new AttackerRow { Cls = FighterClass.Polearm, Passive = ArmorBreakPassive,
                Light = new[] { 23.0, 22, 24, 24, 23 }, Heavy = new[] { 43.0, 42, 44, 44, 43 }, Crit = new[] { 65.5, 64.5, 66.5, 66.5, 65.5 } },
        };

        static IEnumerable<TestCaseData> NoCritRows()
        {
            foreach (var r in Attackers)
                for (int i = 0; i < Defenders.Length; i++)
                {
                    yield return new TestCaseData(r.Cls, r.Passive, ChibiButtons.Light, Defenders[i], r.Light[i]);
                    yield return new TestCaseData(r.Cls, r.Passive, ChibiButtons.Heavy, Defenders[i], r.Heavy[i]);
                }
        }

        static IEnumerable<TestCaseData> CritRows()
        {
            foreach (var r in Attackers)
                for (int i = 0; i < Defenders.Length; i++)
                    yield return new TestCaseData(r.Cls, r.Passive, Defenders[i], r.Crit[i]);
        }

        [TestCaseSource(nameof(NoCritRows))]
        public void Damage_LightAndHeavy_PerAttackerClass_NoCrit(FighterClass a, int ap, ChibiButtons btn, FighterClass d, double expected)
        {
            // Arrange
            var s = Arena(a, ap, d, DefPassive(d));

            // Act
            double dmg = StrikeDamage(s, On(btn, 0), Idle, Quiet);

            // Assert: AttackPower * mult - floor(DEF/10), never rounded (doc section 0 rows 1-6)
            Assert.AreEqual(expected, dmg, HpTol, $"{a} P{ap} {btn} vs {d}: HP lost");
        }

        [TestCaseSource(nameof(CritRows))]
        public void Damage_Crit_PerClass(FighterClass a, int ap, FighterClass d, double expected)
        {
            var s = Arena(a, ap, d, DefPassive(d));

            double dmg = StrikeDamage(s, On(ChibiButtons.Heavy, 0), Idle, RngFind(SearchStart, CritOn, NoProc, NoProc));

            Assert.AreEqual(expected, dmg, HpTol, $"{a} P{ap} heavy crit (x1.5, CA:43-49) vs {d}: HP lost");
        }

        [TestCase(EagleEye, 38.5)]       // draw in [50, 250): crit at 250 permille (F:147)
        [TestCase(RangedOther, 25.0)]    // same draw: no crit at base 50 permille
        public void EagleEye_RaisesCritChance(int passive, double expected)
        {
            var s = Arena(FighterClass.Ranged, passive, FighterClass.Melee, DefPassive(FighterClass.Melee));

            double dmg = StrikeDamage(s, On(ChibiButtons.Heavy, 0), Idle, RngFind(SearchStart, EagleEyeOnly, NoProc, NoProc));

            Assert.AreEqual(expected, dmg, HpTol, $"Ranged P{passive} heavy vs Melee with a draw in [50, 250)");
        }

        // ------------------------------------------------------------------ defense (doc section 0 rows 5-6, section 9)

        [TestCase(FighterClass.Ranged, RangedOther, FighterClass.Heavy, true, 1.0)]   // 15 * 0.2 = 3 - 3 = 0 -> min 1 (CA:54)
        [TestCase(FighterClass.Melee, SwiftBlade, FighterClass.Heavy, false, 17.0)]   // 20 - floor(30/10)
        public void Defense_Mitigation_MinimumOne(FighterClass a, int ap, FighterClass d, bool blocked, double expected)
        {
            var s = Arena(a, ap, d, DefPassive(d));
            if (blocked) RaiseShield(s);

            double dmg = StrikeDamage(s, On(ChibiButtons.Light, 0), blocked ? Hold(ChibiButtons.Shield) : Idle, Quiet);

            Assert.AreEqual(expected, dmg, HpTol, $"{a} light vs {d} blocked={blocked}");
        }

        [Test]
        public void ArmorBreak_LowersDefense_ForFiveSeconds()
        {
            // counts game steps (D10)
            // Arrange: Polearm P0 light vs Heavy; crit off, Armor Break proc on (roll order section 6: crit, then AB)
            var s = Arena(FighterClass.Polearm, ArmorBreakPassive, FighterClass.Heavy, DefPassive(FighterClass.Heavy));
            uint procRng = RngFind(SearchStart, NoProc, ProcOn, NoProc);

            // Act 1: proc hit
            double first = StrikeDamage(s, On(ChibiButtons.Light, 0), Idle, procRng);

            // Assert 1: 22 before the proc, timer 300 at end of the proc tick (active T..T+299)
            Assert.AreEqual(22.0, first, HpTol, "proc hit uses unbroken DEF 30");
            Assert.AreEqual(ArmorBreakTicks, s.F[1].ArmorBreakTicks, "ArmorBreakTicks at end of the proc tick");
            Assert.AreEqual(Advance(procRng, 2), s.Rng.State, "crit + Armor Break = 2 draws");

            var expiry = Clone(s);
            for (int t = 0; t < ArmorBreakTicks - 1; t++) Tick(expiry, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(1, expiry.F[1].ArmorBreakTicks, "still broken on T+299");
            Tick(expiry, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(0, expiry.F[1].ArmorBreakTicks, "expired on T+300");

            // Act 2: next light (chained, non-finisher) on the broken Heavy
            for (int t = 0; t < 10; t++) Tick(s, ChibiButtons.None, ChibiButtons.None);
            Put(s.F[1], MeleeVX, 0, -1);
            double second = StrikeDamage(s, On(ChibiButtons.Light, 0), Idle, Quiet);

            // Assert 2: DEF 25.5 -> floor 2 -> 25 - 2 = 23 (doc section 9 "Armor Break")
            Assert.AreEqual(23.0, second, HpTol, "light on the armor-broken Heavy");
        }

        // a, ap, button, defender, blocked, expected. Defender has ArmorBreakTicks > 0 (DEF x0.85, fractional, F:148).
        [TestCase(FighterClass.Polearm, ArmorBreakPassive, ChibiButtons.Heavy, FighterClass.Heavy, true, 9.025)] // 45*0.245 - 2 (doc row)
        [TestCase(FighterClass.Melee, SwiftBlade, ChibiButtons.Light, FighterClass.Ranged, false, 20.0)]          // DEF 8.5 -> floor 0 (doc row)
        [TestCase(FighterClass.Melee, SwiftBlade, ChibiButtons.Light, FighterClass.Ranged, true, 8.3)]            // 20*0.415 - 0 (derived, section 1c/1d)
        public void ArmorBreak_ExactFractionalDefense(FighterClass a, int ap, ChibiButtons btn, FighterClass d, bool blocked, double expected)
        {
            var s = Arena(a, ap, d, DefPassive(d));
            s.F[1].ArmorBreakTicks = LongTimer;
            if (blocked) RaiseShield(s);

            double dmg = StrikeDamage(s, On(btn, 0), blocked ? Hold(ChibiButtons.Shield) : Idle, Quiet);

            Assert.AreEqual(expected, dmg, HpTol, $"{a} {btn} vs armor-broken {d} blocked={blocked}");
        }

        // ------------------------------------------------------------------ block / guard (doc section 2, section 9)

        // a, ap, button, defender, chip HP, guard after. Guard drain = raw hit; chip = ApplyDefense(raw * BlockedFraction).
        [TestCase(FighterClass.Melee, BloodEdge, ChibiButtons.Light, FighterClass.Melee, 4.0, 80.0)]     // 6 - 2 ; 100 - 20
        [TestCase(FighterClass.Melee, BloodEdge, ChibiButtons.Light, FighterClass.Heavy, 1.0, 110.0)]    // 4 - 3 ; 130 - 20
        [TestCase(FighterClass.Melee, BloodEdge, ChibiButtons.Light, FighterClass.Ranged, 7.0, 50.0)]    // 8 - 1 ; 70 - 20
        [TestCase(FighterClass.Melee, BloodEdge, ChibiButtons.Light, FighterClass.Magic, 7.0, 50.0)]
        [TestCase(FighterClass.Melee, BloodEdge, ChibiButtons.Light, FighterClass.Polearm, 4.0, 80.0)]
        [TestCase(FighterClass.Heavy, Juggernaut, ChibiButtons.Heavy, FighterClass.Melee, 20.68, 24.4)]  // doc row
        [TestCase(FighterClass.Heavy, Juggernaut, ChibiButtons.Heavy, FighterClass.Heavy, 12.12, 54.4)]  // doc row
        [TestCase(FighterClass.Ranged, RangedOther, ChibiButtons.Light, FighterClass.Heavy, 1.0, 115.0)] // doc row (min 1)
        public void Block_ChipDamageFraction_PerDefender(FighterClass a, int ap, ChibiButtons btn, FighterClass d, double chip, double guardAfter)
        {
            var s = Arena(a, ap, d, DefPassive(d));
            RaiseShield(s);

            double dmg = StrikeDamage(s, On(btn, 0), Hold(ChibiButtons.Shield), Quiet);

            Assert.AreEqual(chip, dmg, HpTol, $"{a} {btn} into {d} block: chip");
            Assert.AreEqual(guardAfter, GuardOf(s.F[1]), HpTol, "guard after the drain");
            Assert.IsTrue(s.F[1].Shielding, "a surviving guard keeps the shield up (no Interrupt)");
            Assert.AreEqual(0, s.F[1].Stun, "a blocked hit has no stun");
        }

        // a, ap, defender, expected Vx, Vy (sub units) = (Dir * KbX / 4, KbY / 4), C# truncation (section 2)
        [TestCase(FighterClass.Melee, BloodEdge, FighterClass.Melee, 9375, 7500)]
        [TestCase(FighterClass.Ranged, RangedOther, FighterClass.Heavy, 2812, 2250)]   // halved 11250 / 9000 first (CA:40)
        public void Block_ChipKnockback_XAndY_QuarterNoStun(FighterClass a, int ap, FighterClass d, int vx, int vy)
        {
            var s = Arena(a, ap, d, DefPassive(d));
            RaiseShield(s);
            s.F[0].Ult = 0; s.F[1].Ult = 0;

            StrikeDamage(s, On(ChibiButtons.Light, 0), Hold(ChibiButtons.Shield), Quiet);

            Assert.AreEqual(vx, s.F[1].Vx, "chip Vx = Dir * KbX / 4");
            Assert.AreEqual(vy, s.F[1].Vy, "chip Vy = KbY / 4 (the original sets X and Y)");
            Assert.AreEqual(0, s.F[1].Stun, "no stun on a blocked hit");
            Assert.IsTrue(s.F[1].Shielding, "still shielding");
            Assert.AreEqual(UltWhenHit, s.F[1].Ult, "defender +1.5 ult (ApplyDamage)");
            Assert.AreEqual(UltBlockedAttacker, s.F[0].Ult, "attacker +1 ult on a blocked hit (F:630-649)");
        }

        [Test]
        public void Guard_DrainsByRawDamage_RegensWhenNotBlocking()
        {
            // Arrange
            var s = Arena(FighterClass.Melee, BloodEdge, FighterClass.Melee, DefPassive(FighterClass.Melee));
            var D = s.F[1];
            RaiseShield(s);

            // Act 1: crit light into the block -- the guard soaks the raw hit, crit included (section 2)
            double chip = StrikeDamage(s, On(ChibiButtons.Light, 0), Hold(ChibiButtons.Shield), RngFind(SearchStart, CritOn, NoProc));
            Assert.AreEqual(70.0, GuardOf(D), HpTol, "guard 100 - raw 30 (crit)");
            Assert.AreEqual(7.0, chip, HpTol, "chip 30 * 0.3 - 2");

            // No regen while shielding
            SetGuard(D, 50);
            RaiseShield(s, 60);
            Assert.AreEqual(50.0, GuardOf(D), HpTol, "no regen while shielding");

            // Release tick: 'blocking' is last frame's value -> no regen yet (F:202-203)
            Tick(s, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(50.0, GuardOf(D), HpTol, "release tick: no regen");

            // +0.4 per tick, +24.0 per 60 ticks
            Tick(s, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(50.0 + RegenPerTick, GuardOf(D), HpTol, "one regen tick");
            for (int t = 0; t < 59; t++) Tick(s, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(74.0, GuardOf(D), HpTol, "+24.0 HP over 60 ticks");

            // Cap at GuardMax (40 + 3 * 20 = 100, F:90)
            for (int t = 0; t < 100; t++) Tick(s, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(100.0, GuardOf(D), HpTol, "capped at GuardMax");

            // No regen while broken
            SetGuard(D, 10);
            D.GuardBrokenTicks = 120;
            for (int t = 0; t < 60; t++) Tick(s, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(10.0, GuardOf(D), HpTol, "no regen while guard-broken");
        }

        [Test]
        public void Guard_BreakOnEmptyingHit_FullDamage_Stun_NoBlockForThreeSeconds()
        {
            // counts game steps (D10)
            // Arrange: Heavy heavy into a Melee block with guard 24.4 (after the first heavy, doc section 9)
            var s = Arena(FighterClass.Heavy, Juggernaut, FighterClass.Melee, DefPassive(FighterClass.Melee));
            var D = s.F[1];
            RaiseShield(s);
            SetGuard(D, 24.4);
            int landed = s.F[0].HitsLanded;

            // Act
            double dmg = StrikeDamage(s, On(ChibiButtons.Heavy, 0), Hold(ChibiButtons.Shield), Quiet);

            // Assert: break -> full damage, stun max(30, 60), broken 180, full kb, counts as landed
            Assert.AreEqual(73.6, dmg, HpTol, "guard break takes the full unblocked hit");
            Assert.AreEqual(0.0, GuardOf(D), HpTol, "guard 0");
            Assert.AreEqual(GuardBreakStun, D.Stun, "stun = max(spec 30, 60)");
            Assert.AreEqual(GuardBrokenTicks, D.GuardBrokenTicks, "GuardBrokenTicks at end of the break tick");
            Assert.IsFalse(D.Shielding, "shield dropped");
            Assert.AreEqual(120_000, D.Vx, "full kb X (8 u/s)");
            Assert.AreEqual(90_000, D.Vy, "full kb Y (6 u/s)");
            Assert.AreEqual(landed + 1, s.F[0].HitsLanded, "break counts as a landed hit");

            // Keep the defender on stage for the 3 s window (arrangement only)
            D.Vx = 0; D.Vy = 0;

            // Shield held from break tick T: false through T+179, true on T+180
            for (int k = 1; k <= GuardBrokenTicks; k++)
            {
                Tick(s, ChibiButtons.None, ChibiButtons.Shield);
                if (k < GuardBrokenTicks) Assert.IsFalse(D.Shielding, $"no block on T+{k}");
                else Assert.IsTrue(D.Shielding, $"block allowed again on T+{k}");
            }
        }

        // ------------------------------------------------------------------ parries (doc section 3)

        [Test]
        public void PerfectParry_WindowLastTickSucceeds_NextTickNormalBlock()
        {
            var s = Arena(FighterClass.Melee, BloodEdge, FighterClass.Melee, DefPassive(FighterClass.Melee));
            // The light is pressed late enough that both raise ticks below (h - 10 and h - 11) fall inside the
            // scenario; pressing on tick 0 would put them before tick 0, so the shield would really be up for only
            // ~h ticks and both cases would parry.
            var a = On(ChibiButtons.Light, ParryAttackDelay);
            int h = FindHitTick(s, a, Idle);
            Assert.GreaterOrEqual(h - ParryWindow, 0, "fixture: both raise ticks must be inside the scenario");

            // Raise on R = h - 10: the hit resolves with ShieldTick 10 -> perfect parry
            var p = Clone(s);
            int raise = h - (ParryWindow - 1);
            Func<int, ChibiButtons> dp = t => t >= raise ? ChibiButtons.Shield : ChibiButtons.None;
            Assert.AreEqual(h, Prime(p, a, dp), "hit tick independent of the shield");
            double hp = HpOf(p.F[1]);
            int parries = p.F[1].Parries;
            Resolve(p, a, dp, h, Quiet);

            Assert.AreEqual(hp, HpOf(p.F[1]), HpTol, "parry: no damage");
            Assert.AreEqual(parries + 1, p.F[1].Parries, "Parries++");
            Assert.AreEqual(UltPerParry, p.F[1].Ult, "defender +15 ult (150)");
            Assert.AreEqual(Quiet, p.Rng.State, "perfect parry: 0 draws");
            Assert.IsTrue(p.F[1].Shielding, "shield stays up");
            Assert.AreEqual(PerfectParryStun, p.F[0].Stun, "attacker Stun(0.45 s)");
            Assert.AreEqual(-1, p.F[0].MoveSlot, "attacker interrupted");
            Assert.LessOrEqual(p.F[0].Vy, 0, "Vy = min(0, Vy) (F:663-668)");
            int x0 = p.F[0].X;
            for (int k = 1; k < PerfectParryStun; k++)
            {
                Tick(p, ChibiButtons.None, ChibiButtons.Shield);
                Assert.AreEqual(x0, p.F[0].X, $"attacker Vx zeroed by Stun(): no X drift on stun tick {k}");
            }

            // Raise on R = h - 11: ShieldTick 11 -> normal block
            var b = Clone(s);
            int raiseLate = h - ParryWindow;
            Func<int, ChibiButtons> db = t => t >= raiseLate ? ChibiButtons.Shield : ChibiButtons.None;
            double dmg = StrikeDamage(b, a, db, Quiet);
            Assert.AreEqual(4.0, dmg, HpTol, "one tick later: normal block chip");
            Assert.AreEqual(parries, b.F[1].Parries, "not a parry");
            Assert.AreEqual(0, b.F[0].Stun, "attacker not stunned");
        }

        [TestCase(false, 39.0)]   // Heavy AttackPower 42 - floor(30/10)
        [TestCase(true, 60.0)]    // 63 - 3
        public void ParrySkill_Reflect_DamagePushStun_AndAttackerGetsWhenHitUlt(bool crit, double expected)
        {
            // Arrange: Melee in Parry stance (ParryTicks > 0), Heavy light into it
            var s = Arena(FighterClass.Heavy, Juggernaut, FighterClass.Melee, DefPassive(FighterClass.Melee));
            var A = s.F[0]; var D = s.F[1];
            D.ParryTicks = LongTimer;
            uint rng = crit ? RngFind(SearchStart, CritOn, NoProc) : Quiet;
            var a = On(ChibiButtons.Light, 0);
            int h = Prime(s, a, Idle);
            double aHp = HpOf(A), dHp = HpOf(D);
            int parries = D.Parries;

            // Act
            Resolve(s, a, Idle, h, rng);

            // Assert (doc section 3b)
            Assert.AreEqual(expected, aHp - HpOf(A), HpTol, "reflected damage on the attacker");
            Assert.AreEqual(dHp, HpOf(D), HpTol, "defender untouched");
            Assert.AreEqual(0, D.ParryTicks, "stance consumed");
            Assert.AreEqual(parries + 1, D.Parries, "Parries++");
            Assert.AreEqual(UltPerParry, D.Ult, "defender +15 ult (150)");
            Assert.AreEqual(UltWhenHit, A.Ult, "attacker gets the when-hit +1.5 (15)");
            Assert.AreEqual(ReflectStun, A.Stun, "attacker stun 0.5 s");
            Assert.AreEqual(-1, A.MoveSlot, "attacker interrupted");
            int sx = A.X >= D.X ? 1 : -1;      // positions, not hit Dir (Mathf.Sign(0) = +1)
            Assert.AreEqual(sx * ReflectPushX, A.Vx, "push X = sx * 5 u/s");
            Assert.AreEqual(ReflectPushY, A.Vy, "push Y = 4 u/s");
            Assert.AreEqual(Advance(rng, 1), s.Rng.State, "reflect: exactly 1 draw (crit)");
        }

        // ------------------------------------------------------------------ interrupt (doc section 0 row 12, section 4)

        [TestCase(true)]    // defender in a move
        [TestCase(false)]   // defender dashing
        public void Hit_InterruptsMoveAndDash_KeepsParryStanceTimers(bool inMove)
        {
            var s = Arena(FighterClass.Melee, BloodEdge, FighterClass.Melee, DefPassive(FighterClass.Melee));
            var D = s.F[1];
            var a = On(ChibiButtons.Light, 0);
            int h = FindHitTick(s, a, Idle);
            Func<int, ChibiButtons> d = inMove ? On(ChibiButtons.Light, h - 1) : Idle;
            D.GuardTicks = LongTimer;                          // spin-guard timer (spinGuardUntil)

            Assert.AreEqual(h, Prime(s, a, d), "hit tick unchanged");
            if (inMove) Assert.AreEqual(0, D.MoveSlot, "precondition: defender's light is running");
            else { D.DashTicks = 3; D.DashDir = -1; D.Invuln = 0; }
            double hp = HpOf(D);
            Resolve(s, a, d, h, Quiet);

            Assert.Less(HpOf(D), hp, "precondition: the hit landed");
            Assert.AreEqual(LightStun, D.Stun, "stun");
            Assert.AreEqual(-1, D.MoveSlot, "Interrupt: move stopped");
            Assert.AreEqual(0, D.DashTicks, "Interrupt: dash stopped");
            Assert.Greater(D.GuardTicks, 0, "Interrupt does not clear spinGuardUntil (F:670-677)");
            Tick(s, ChibiButtons.None, ChibiButtons.None);
            Assert.Greater(D.GuardTicks, 0, "stun tick does not clear it either (contract item 5)");
        }

        // ------------------------------------------------------------------ ult (doc section 4 items 4 + 6)

        [TestCase(0, 500.0, UltPerHit)]            // 3
        [TestCase(2, 500.0, UltPerComboHit)]       // ComboHits reaches 3: 5
        [TestCase(0, 149.0, 2 * UltPerHit)]        // HP < 30 % of 500: x2
        [TestCase(0, 150.0, UltPerHit)]            // exactly 30 %: no bonus
        [TestCase(2, 149.0, 2 * UltPerComboHit)]
        public void Ult_GainPerHit_AndWhenHit(int priorCombo, double attackerHp, int expectedGain)
        {
            var s = Arena(FighterClass.Melee, SwiftBlade, FighterClass.Melee, DefPassive(FighterClass.Melee));
            var A = s.F[0];
            A.ComboHits = priorCombo; A.ComboTimer = priorCombo > 0 ? LongTimer : 0;
            SetHp(A, attackerHp);

            StrikeDamage(s, On(ChibiButtons.Light, 0), Idle, Quiet);

            Assert.AreEqual(expectedGain, A.Ult, "attacker ult gain (0..1000)");
            Assert.AreEqual(UltWhenHit, s.F[1].Ult, "defender +15 when hit");
        }

        // ------------------------------------------------------------------ passives (doc section 5)

        [Test]
        public void Ward_NegatesHit()
        {
            // Ward fires (draw < 200, and >= 50 so a missing ward gate would not mask it as a crit)
            var s = Arena(FighterClass.Melee, SwiftBlade, FighterClass.Magic, WardPassive);
            var A = s.F[0]; var D = s.F[1];
            uint on = RngFind(SearchStart, WardOnNoCrit, NoProc, NoProc);
            var a = On(ChibiButtons.Light, 0);
            int h = Prime(s, a, Idle);
            double hp = HpOf(D), guard = GuardOf(D);
            int vx = D.Vx, vy = D.Vy, stun = D.Stun, dUlt = D.Ult, aUlt = A.Ult, landed = A.HitsLanded;

            Resolve(s, a, Idle, h, on);

            Assert.AreEqual(hp, HpOf(D), HpTol, "no damage");
            Assert.AreEqual(guard, GuardOf(D), HpTol, "no guard change");
            Assert.AreEqual(vx, D.Vx, "no kb X"); Assert.AreEqual(vy, D.Vy, "no kb Y");
            Assert.AreEqual(stun, D.Stun, "no stun");
            Assert.AreEqual(dUlt, D.Ult, "no defender ult"); Assert.AreEqual(aUlt, A.Ult, "no attacker ult");
            Assert.AreEqual(landed, A.HitsLanded, "not a landed hit");
            Assert.AreEqual(Advance(on, 1), s.Rng.State, "ward: exactly 1 draw");

            // Ward misses (>= 200) and crit off: Melee light vs Magic DEF 10 -> 19
            var s2 = Arena(FighterClass.Melee, SwiftBlade, FighterClass.Magic, WardPassive);
            uint off = RngFind(SearchStart, NoProc, NoProc, NoProc);
            double dmg = StrikeDamage(s2, On(ChibiButtons.Light, 0), Idle, off);
            Assert.AreEqual(19.0, dmg, HpTol, "ward miss: normal hit");
            Assert.AreEqual(Advance(off, 2), s2.Rng.State, "ward + crit = 2 draws");
        }

        [Test]
        public void Hex_AppliesSlowWeakenSluggish_FourSeconds()
        {
            // counts game steps (D10)
            // Magic P0 light vs Melee: 18 + proc (crit off, hex on)
            var s = Arena(FighterClass.Magic, HexPassive, FighterClass.Melee, DefPassive(FighterClass.Melee));
            var D = s.F[1];
            uint rng = RngFind(SearchStart, NoProc, ProcOn, NoProc);

            double dmg = StrikeDamage(s, On(ChibiButtons.Light, 0), Idle, rng);

            Assert.AreEqual(18.0, dmg, HpTol, "Magic light vs Melee");
            Assert.AreEqual(HexTicks, D.SlowTicks, "slow 4 s");
            Assert.AreEqual(HexTicks, D.WeakenTicks, "weaken 4 s");
            Assert.AreEqual(HexTicks, D.SluggishTicks, "sluggish 4 s");
            Assert.AreEqual(Advance(rng, 2), s.Rng.State, "crit + hex = 2 draws");
            var e = Clone(s);
            for (int t = 0; t < HexTicks - 1; t++) Tick(e, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(1, e.F[1].WeakenTicks, "active through T+239");
            Tick(e, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(0, e.F[1].WeakenTicks, "expired on T+240");

            // Hexed (weakened) Heavy heavy vs Melee: 75.6 * 0.9 - 2 = 66.04 (F:118)
            var w = Arena(FighterClass.Heavy, Juggernaut, FighterClass.Melee, DefPassive(FighterClass.Melee));
            w.F[0].WeakenTicks = LongTimer;
            double weakened = StrikeDamage(w, On(ChibiButtons.Heavy, 0), Idle, Quiet);
            Assert.AreEqual(66.04, weakened, HpTol, "weakened attacker x0.9");
        }

        [TestCase(false, false, 0.9)]    // 18 * 0.05
        [TestCase(true, false, 1.4)]     // crit 28 * 0.05
        [TestCase(false, true, 0.0)]     // blocked: no life steal
        public void BloodEdge_LifeSteal_ExactDecimal(bool crit, bool blocked, double heal)
        {
            var s = Arena(FighterClass.Melee, BloodEdge, FighterClass.Melee, DefPassive(FighterClass.Melee));
            SetHp(s.F[0], 300);                          // HpU 3 * 10^8 (doc row), above 30 %
            if (blocked) RaiseShield(s);
            var a = On(ChibiButtons.Light, 0);
            var d = blocked ? Hold(ChibiButtons.Shield) : Idle;
            uint rng = crit ? RngFind(SearchStart, CritOn, NoProc) : Quiet;
            int h = Prime(s, a, d);
            double before = HpOf(s.F[0]);

            Resolve(s, a, d, h, rng);

            Assert.AreEqual(heal, HpOf(s.F[0]) - before, HpTol, $"life steal crit={crit} blocked={blocked}");
        }

        // gap = ticks between the two light presses (start of the last attack -> start of the next)
        [TestCase(179, 23.0)]
        [TestCase(180, 35.5)]
        public void Patience_BonusAt180IdleTicks_Not179_FirstAttackQualifies_UltExcluded(int gap, double second)
        {
            // counts game steps (D10)
            var s = Arena(FighterClass.Polearm, PatiencePassive, FighterClass.Melee, DefPassive(FighterClass.Melee));

            var dmg = HitDamages(s, gap + 40, On(ChibiButtons.Light, 0, gap), t =>
            {
                if (t == gap) { Put(s.F[0], MeleeAX, 0, 1); Put(s.F[1], MeleeVX, 0, -1); }
            });

            Assert.AreEqual(2, dmg.Count, "two hits");
            Assert.AreEqual(35.5, dmg[0], HpTol, "first attack of the match qualifies: 25 * 1.5 - 2");
            Assert.AreEqual(second, dmg[1], HpTol, $"second light {gap} ticks later");
        }

        [Test]
        public void Patience_StunBetweenAttacks_DoesNotReset_HeavyGetsBonus()
        {
            // counts game steps (D10)
            var s = Arena(FighterClass.Polearm, PatiencePassive, FighterClass.Melee, DefPassive(FighterClass.Melee));
            const int gap = 180, stunAt = 30, stunTicks = 60;

            var dmg = HitDamages(s, gap + 40, On(ChibiButtons.Light, 0, gap), t =>
            {
                if (t == stunAt) s.F[0].Stun = stunTicks;
                if (t == gap) { Put(s.F[0], MeleeAX, 0, 1); Put(s.F[1], MeleeVX, 0, -1); }
            });
            Assert.AreEqual(2, dmg.Count, "two hits");
            Assert.AreEqual(35.5, dmg[1], HpTol, "a 60-tick stun in between does not reset the timer");

            var h = Arena(FighterClass.Polearm, PatiencePassive, FighterClass.Melee, DefPassive(FighterClass.Melee));
            Assert.AreEqual(65.5, StrikeDamage(h, On(ChibiButtons.Heavy, 0), Idle, Quiet), HpTol, "heavy with bonus: 67.5 - 2");
        }

        [Test]
        public void Patience_UltExcluded_SameDamageQualifiedOrNot()
        {
            // Qualified (fresh match) and not qualified (light 60 ticks before) must give the same first ult hit.
            const int ultAt = 60;
            var q = Arena(FighterClass.Polearm, PatiencePassive, FighterClass.Melee, DefPassive(FighterClass.Melee));
            ChibiTestTime.ArmUltFire(q, 0);   // rules v15: gesture fire at the top of tick 0
            var dq = HitDamages(q, 240, _ => ChibiButtons.None);

            var n = Arena(FighterClass.Polearm, PatiencePassive, FighterClass.Melee, DefPassive(FighterClass.Melee));
            var dn = HitDamages(n, ultAt + 240, t => t == 0 ? ChibiButtons.Light : ChibiButtons.None, t =>
            {
                if (t == ultAt) { Put(n.F[0], MeleeAX, 0, 1); Put(n.F[1], MeleeVX, 0, -1); ChibiTestTime.ArmUltFire(n, 0); }
            });

            Assert.GreaterOrEqual(dq.Count, 1, "precondition: the ult hits");
            Assert.GreaterOrEqual(dn.Count, 2, "precondition: light + ult hit");
            Assert.AreEqual(dq[0], dn[1], HpTol, "Patience does not apply to the ult");
        }

        [TestCase(Crusher)]
        [TestCase(Juggernaut)]
        public void HeavyClass_AttackPowerX1_2_BothPassives(int passive)
        {
            var s = Arena(FighterClass.Heavy, passive, FighterClass.Melee, DefPassive(FighterClass.Melee));
            Assert.AreEqual(200, s.F[0].Stats.DamageBonusPermille, "x1.2 for both passives (F:112-121, CS:105)");

            double dmg = StrikeDamage(s, On(ChibiButtons.Light, 0), Idle, Quiet);

            Assert.AreEqual(40.0, dmg, HpTol, $"Heavy P{passive} light vs Melee: 42 - 2");
        }

        // ------------------------------------------------------------------ roll order (doc section 6)

        // a, ap, d, dp, defender blocks, ward fires, expected draws
        [TestCase(FighterClass.Melee, SwiftBlade, FighterClass.Melee, 1, false, false, 1)]            // crit
        [TestCase(FighterClass.Magic, HexPassive, FighterClass.Melee, 1, false, false, 2)]            // crit + hex
        [TestCase(FighterClass.Polearm, ArmorBreakPassive, FighterClass.Melee, 1, false, false, 2)]   // crit + AB
        [TestCase(FighterClass.Melee, SwiftBlade, FighterClass.Magic, WardPassive, false, false, 2)]  // ward miss + crit
        [TestCase(FighterClass.Melee, SwiftBlade, FighterClass.Magic, WardPassive, false, true, 1)]   // ward fires: stop
        [TestCase(FighterClass.Polearm, ArmorBreakPassive, FighterClass.Melee, 1, true, false, 1)]    // blocked: no AB
        [TestCase(FighterClass.Magic, HexPassive, FighterClass.Melee, 1, true, false, 1)]             // blocked: no hex
        public void RollOrder_DrawCountPerHit_MatchesDoc(FighterClass a, int ap, FighterClass d, int dp, bool blocked, bool wardOn, int draws)
        {
            var s = Arena(a, ap, d, dp);
            if (blocked) RaiseShield(s);
            var ai = On(ChibiButtons.Light, 0);
            var di = blocked ? Hold(ChibiButtons.Shield) : Idle;
            uint rng = wardOn ? RngFind(SearchStart, WardOnNoCrit, NoProc, NoProc) : Quiet;
            int h = Prime(s, ai, di);

            Resolve(s, ai, di, h, rng);

            Assert.AreEqual(Advance(rng, draws), s.Rng.State, $"{a} P{ap} vs {d} P{dp} blocked={blocked} ward={wardOn}: {draws} draws");
        }

        // ------------------------------------------------------------------ match over (doc section 0 row 9, section 4 item 5)

        [Test]
        public void MatchOver_HitDoesNothing()
        {
            // Same-tick trade: F0's hit (h0) KOs F1; F1's hit (h1) that tick must be ignored (!Match.Running).
            var s = NewMatch(FighterClass.Melee, BloodEdge, FighterClass.Melee, BloodEdge);
            Put(s.F[0], MeleeAX, 0, 1);
            Put(s.F[1], MeleeVX, 0, -1);
            for (int t = 0; t < 3; t++) Tick(s, ChibiButtons.None, ChibiButtons.None);
            SetHp(s.F[1], 1);
            double hp0 = HpOf(s.F[0]);
            int stun0 = s.F[0].Stun;

            for (int t = 0; t < 30 && !s.MatchOver; t++)
            {
                var b = t == 0 ? ChibiButtons.Light : ChibiButtons.None;
                Tick(s, b, b);
            }

            Assert.IsTrue(s.MatchOver, "precondition: F1 was KO'd");
            Assert.AreEqual(0, s.Winner, "F0 wins");
            Assert.AreEqual(hp0, HpOf(s.F[0]), HpTol, "the trade hit after the KO does nothing");
            Assert.AreEqual(stun0, s.F[0].Stun, "no stun from the ignored hit");
        }
    }
}
