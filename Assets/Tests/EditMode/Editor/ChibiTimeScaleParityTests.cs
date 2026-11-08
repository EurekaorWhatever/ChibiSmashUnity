// Story type: Logic (time-scale state machine: hitstop, ult cinematic, input latch, game-step accumulator) -- evidence:
// automated EditMode unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/timescale-conversion.md, RulesVersion 10. Section 1 per-source table, section 5
// code contract, section 6 reference values T1-T17 (tolerances: the v10 column is exact), section 8 TD amendments
// (override section 5: LatchedHeld, replay v2 + config hash, D-N1 "next game step only", timer/SpawnFrame on GameTicks).
// Original: Core/MatchController.cs (MC:), Fighter/Fighter.cs (F:).
//
// User decision applied: D10 = Option A-held with the section 8.3 contract changes.
//
// COMPILES TODAY, FAILS TODAY: the v10 fields (ChibiSimState.HitstopRealTicks, CinematicRealTicks, CinematicFighter,
// TimeAccum, GameTicks; ChibiFighterSim.LatchedEdges, LatchedHeld; ChibiSimConst.Scale*/Hitstop*/CinematicTicks) do not
// exist yet. Every test reads or writes them only through Get/Set/FGet/FSet/Const below, which Assert.Fail("field X
// missing") when absent. Nothing here references a v10 member by name at compile time.
//
// FIXTURE INJECTION (documented): where a scenario needs "the state right after a hitstop/cinematic started" and the
// source of the slow-mo does not matter to the rule under test (latch, D-N1, SpawnFrame), the test writes
// HitstopRealTicks / CinematicRealTicks / CinematicFighter / TimeAccum through reflection instead of landing a real hit
// or ult. The tests that check the TRIGGERS (Hitstop_*_RealTickCount, Cinematic_UltStart_*) use real hits and a real
// ult: an ult can fire today (FreeActions SIM:663, StartMove slot 5 SIM:743) by setting F.Ult = UltMax.
//
// RNG: every tick is pinned to the "Quiet" state (no crit/proc) as in ChibiDamageParityTests; a crit is forced by
// writing a searched RNG state right before the hit tick.
// Arena: Sky Dojo, melee columns X 6500 / 7300, shot columns 6300 / 7500 (ChibiNormalsParityTests).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Time-scale parity (hitstop 0.05, ult cinematic 0.2) between the deterministic sim and the original MatchController
    /// Time.timeScale handling. Reference values: timescale-conversion.md section 6; contract: sections 5 and 8.3.
    /// </summary>
    public class ChibiTimeScaleParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;
        const int SkyDojo = 0, LavaForge = 2;
        const int MaxHitSearch = 150;

        // ---- section 5 constants / section 1 table (doc values; the code values are checked in Constants_MatchDoc)
        const int ScaleHitstop = 50;              // MC:112 min(hitstop ? 0.05 : 1, ...)
        const int ScaleCinematic = 200;           // MC:113 cinematic ? 0.2 : 1
        const int ScaleNormal = 1000;
        const int StepCost = 1000;                // one game step per 1000 permille (section 3 Option A, section 8)
        const int HitstopNormal = 3;              // H1 / T1: ceil(0.045 * 60), F:625 HitStop(0.045)
        const int HitstopCrit = 5;                // H2 / T2: ceil(0.08 * 60),  F:625 HitStop(0.08)
        const int HitstopParry = 6;               // H3 / T3: ceil(0.1 * 60),   F:570 HitStop(0.1)
        const int HitstopReflect = 8;             // H4 / T4: ceil(0.12 * 60),  F:559 HitStop(0.12)
        const int CinematicTicks = 48;            // C / T5: 0.8 * 60, MC:188 cinematicUntil = now + 0.8
        const int NoCinematicFighter = -1;        // section 5 state: CinematicFighter -1 = none
        const int RulesVersion10 = 10;            // section 5, SIM:112
        const byte ReplayVersion2 = 2;            // section 8.3 item 3

        // ---- section 6 reference values
        const int T5Steps = 9, T5Carry = 600;     // A0 = 0: steps at c+5 .. c+45, accum 600 after c+48
        const int T6A0 = 400, T6Steps = 10;       // A0 >= 400: +10 game ticks over the cinematic
        const int T6Carry = 0;                    // 400 + 48 * 200 - 10 * 1000
        const int T9PressAt = 2;                  // press on real tick k+2 of a hitstop
        const int T10PressAt = 1, T10FirstStep = 5; // press at c+1 of a cinematic, starts on the step at c+5
        const int T10StepSpacing = 5;             // 1000 / 200 real ticks between game steps in a cinematic
        const int T12CritAt = 20, T12Steps = 8;   // crit at c+20 of a cinematic: 8 game steps in c+1..c+48
        const int T14Ticks = 600;                 // whole-run regression without slow-mo
        const int TimerLeadSteps = 3;             // match timer test: GameTicks = MatchTicks - 3 at c
        const int TimerEndsAt = 15;               // third cinematic game step (c+5, c+10, c+15) ends the match
        const int RollbackSnapAt = 17;            // c+17: TimeAccum 17 * 200 % 1000 = 400, latch pending
        const int RollbackPressAt = 16;           // F1 Light at c+16, consumed on the step at c+20
        const int RollbackRunTo = 60;             // past the cinematic end (c+48)
        const int SpawnShiftTicks = HitstopReflect; // projectile test: GameTicks lags Frame by 8 real ticks

        // ---- arena / class constants (ChibiDamageParityTests, ChibiCombatStats.Create)
        const int MeleeAX = 6500, MeleeVX = 7300;
        const int ShotAX = 6300, ShotVX = 7500;
        const int BloodEdge = 1, Juggernaut = 1, EagleEye = 0;
        const int ParryWindow = 11;               // ShieldTick 0..10 parries (damage-conversion 3a)
        const int ParryAttackDelay = 20;          // light pressed late so the parry raise tick is inside the scenario
        const int ReflectStun = 30;               // F:550-561
        const int BusyStunTicks = 3;              // > 1 game step of stun at c+5 (Dec then busy), free by c+15
        const int LongTimer = 600;                // a timer that cannot run out inside one test

        // ---- v10 field names (section 5 + 8.3 item 1)
        const string HitstopF = "HitstopRealTicks", CinematicF = "CinematicRealTicks", CinFighterF = "CinematicFighter";
        const string AccumF = "TimeAccum", GameTicksF = "GameTicks";
        const string LatchedEdgesF = "LatchedEdges", LatchedHeldF = "LatchedHeld";

        // ------------------------------------------------------------------ reflection (fails while v10 is absent)

        const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance;

        static FieldInfo Need(Type t, string name)
        {
            var fi = t.GetField(name, Inst);
            if (fi == null) Assert.Fail($"field {t.Name}.{name} missing (timescale-conversion.md section 5 / 8.3, RulesVersion 10)");
            return fi;
        }

        static int Get(ChibiSimState s, string n) => Convert.ToInt32(Need(typeof(ChibiSimState), n).GetValue(s));
        static void Set(ChibiSimState s, string n, int v) => Need(typeof(ChibiSimState), n).SetValue(s, v);
        static int FGet(ChibiFighterSim f, string n) => Convert.ToInt32(Need(typeof(ChibiFighterSim), n).GetValue(f));
        static void FSet(ChibiFighterSim f, string n, int v) => Need(typeof(ChibiFighterSim), n).SetValue(f, v);

        static int Const(string name)
        {
            var fi = typeof(ChibiSimConst).GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (fi == null) Assert.Fail($"constant ChibiSimConst.{name} missing (timescale-conversion.md section 5)");
            return Convert.ToInt32(fi.IsLiteral ? fi.GetRawConstantValue() : fi.GetValue(null));
        }

        // ------------------------------------------------------------------ RNG forcing (as ChibiDamageParityTests)

        static bool NoProc(int v) => v >= 250;
        static bool CritOn(int v) => v < 50;
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

        static readonly uint Quiet = RngFind(SearchStart, NoProc, NoProc, NoProc, NoProc);
        static readonly uint CritFirst = RngFind(SearchStart, CritOn, NoProc, NoProc, NoProc, NoProc, NoProc);

        // ------------------------------------------------------------------ match helpers

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

        static bool IsShooter(FighterClass c) => c == FighterClass.Ranged || c == FighterClass.Magic;

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

        static ChibiSimState MeleeArena() => Arena(FighterClass.Melee, BloodEdge, FighterClass.Melee, BloodEdge);

        static ChibiInputPacket P(ChibiButtons b) => ChibiInputPacket.Make(ChibiDir.Neutral, b);

        static void Tick(ChibiSimState s, ChibiButtons a, ChibiButtons d)
        {
            s.Rng.State = Quiet;
            ChibiSimulation.Step(s, P(a), P(d));
        }

        static Func<int, ChibiButtons> On(ChibiButtons b, params int[] ticks) =>
            t => Array.IndexOf(ticks, t) >= 0 ? b : ChibiButtons.None;

        static readonly Func<int, ChibiButtons> Idle = t => ChibiButtons.None;

        static ChibiSimState Clone(ChibiSimState s)
        {
            var c = new ChibiSimState();
            c.CopyFrom(s);
            return c;
        }

        static int FindHitTick(ChibiSimState s, Func<int, ChibiButtons> a, Func<int, ChibiButtons> d)
        {
            var c = Clone(s);
            for (int t = 0; t < MaxHitSearch; t++)
            {
                int hp = c.F[1].HpU, parries = c.F[1].Parries;
                Tick(c, a(t), d(t));
                if (c.F[1].HpU < hp || c.F[1].Parries > parries) return t;
            }
            Assert.Fail($"precondition: no hit resolved within {MaxHitSearch} ticks");
            return -1;
        }

        static int Prime(ChibiSimState s, Func<int, ChibiButtons> a, Func<int, ChibiButtons> d)
        {
            int h = FindHitTick(s, a, d);
            for (int t = 0; t < h; t++) Tick(s, a(t), d(t));
            return h;
        }

        static void Resolve(ChibiSimState s, Func<int, ChibiButtons> a, Func<int, ChibiButtons> d, int h, uint rng)
        {
            s.Rng.State = rng;
            ChibiSimulation.Step(s, P(a(h)), P(d(h)));
        }

        /// <summary>Everything a game step could change, for "nothing moved" comparisons (T7).</summary>
        static string Snap(ChibiSimState s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in s.F)
                sb.Append($"{f.X},{f.Y},{f.Vx},{f.Vy},{f.XRem},{f.YRem},{f.HpU},{f.GuardU},{f.Stun},{f.Invuln},{f.MoveSlot}," +
                          $"{f.MoveTick},{f.Shielding},{f.ShieldTick},{f.ParryTicks},{f.ComboTimer},{f.DashCd},{f.Ult}|");
            foreach (var p in s.Proj)
                if (p != null && p.Active) sb.Append($"P{p.X},{p.Y},{p.Life}|");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ hit scenarios (section 1 H1-H4)

        public enum HitKind { Normal, Crit, PerfectParry, Reflect }

        sealed class Landed
        {
            public ChibiSimState S;
            public Func<int, ChibiButtons> A, D;
            public int H;
        }

        /// <summary>Lands a real hit of the given kind on the game step at real tick k = H, from TimeAccum = 0.</summary>
        static Landed LandHit(HitKind kind)
        {
            ChibiSimState s;
            Func<int, ChibiButtons> a, d = Idle;
            uint rng = Quiet;
            if (kind == HitKind.Reflect)
            {
                s = Arena(FighterClass.Heavy, Juggernaut, FighterClass.Melee, BloodEdge);
                s.F[1].ParryTicks = LongTimer;           // Parry-skill stance (F:559)
                a = On(ChibiButtons.Light, 0);
            }
            else if (kind == HitKind.PerfectParry)
            {
                s = MeleeArena();
                a = On(ChibiButtons.Light, ParryAttackDelay);
                int hp = FindHitTick(s, a, Idle);
                int raise = hp - (ParryWindow - 1);      // ShieldTick 10 at the hit -> perfect parry (F:570)
                Assert.GreaterOrEqual(raise, 0, "fixture: shield raise inside the scenario");
                d = t => t >= raise ? ChibiButtons.Shield : ChibiButtons.None;
            }
            else
            {
                s = MeleeArena();
                a = On(ChibiButtons.Light, 0);
                if (kind == HitKind.Crit) rng = CritFirst;
            }

            int h = Prime(s, a, d);
            Assert.AreEqual(0, Get(s, AccumF), "fixture: section 6 cases start from TimeAccum = 0");
            int vHp = s.F[1].HpU, parries = s.F[1].Parries;
            ChibiSimState normal = kind == HitKind.Crit ? Clone(s) : null;
            Resolve(s, a, d, h, rng);

            switch (kind)
            {
                case HitKind.Normal:
                    Assert.Less(s.F[1].HpU, vHp, "precondition: normal hit landed"); break;
                case HitKind.Crit:
                    Resolve(normal, a, d, h, Quiet);
                    Assert.Less(s.F[1].HpU, normal.F[1].HpU, "precondition: forced crit deals more than the quiet hit"); break;
                case HitKind.PerfectParry:
                    Assert.AreEqual(parries + 1, s.F[1].Parries, "precondition: perfect parry");
                    Assert.AreEqual(vHp, s.F[1].HpU, "precondition: parry, no damage"); break;
                case HitKind.Reflect:
                    Assert.AreEqual(parries + 1, s.F[1].Parries, "precondition: parry stance consumed");
                    Assert.AreEqual(ReflectStun, s.F[0].Stun, "precondition: reflect stunned the attacker"); break;
            }
            return new Landed { S = s, A = a, D = d, H = h };
        }

        /// <summary>T1-T4: N real ticks at scale 50, 0 game steps, accum N * 50, next game step at k+N+1.</summary>
        static void AssertHitstopWindow(Landed hit, int n)
        {
            var s = hit.S;
            Assert.AreEqual(n, Get(s, HitstopF), "HitstopRealTicks right after the hit tick k");
            int g0 = Get(s, GameTicksF);
            for (int j = 1; j <= n; j++)
            {
                Tick(s, hit.A(hit.H + j), hit.D(hit.H + j));
                Assert.AreEqual(g0, Get(s, GameTicksF), $"no game step on real tick k+{j}");
                Assert.AreEqual(n - j, Get(s, HitstopF), $"hitstop counts down in real ticks (k+{j})");
                Assert.AreEqual(j * ScaleHitstop, Get(s, AccumF), $"real tick k+{j} runs at scale {ScaleHitstop}");
            }
            Tick(s, hit.A(hit.H + n + 1), hit.D(hit.H + n + 1));
            Assert.AreEqual(g0 + 1, Get(s, GameTicksF), $"next game step on real tick k+{n + 1}");
            Assert.AreEqual(n * ScaleHitstop, Get(s, AccumF), "carry kept after the window");
        }

        // ------------------------------------------------------------------ fixture injection (documented in header)

        /// <summary>State right after a real tick k that started a hitstop of n real ticks (T9 source-independent).</summary>
        static void InjectHitstop(ChibiSimState s, int n)
        {
            Assert.AreEqual(0, Get(s, AccumF), "fixture: A0 = 0");
            Set(s, HitstopF, n);
        }

        /// <summary>State right after the ult-fire tick c (T10, 8.3 item 1): 48 real ticks of cinematic, A0 = 0.</summary>
        static void InjectCinematic(ChibiSimState s, int fighter)
        {
            Assert.AreEqual(0, Get(s, AccumF), "fixture: A0 = 0");
            Set(s, CinematicF, CinematicTicks);
            Set(s, CinFighterF, fighter);
        }

        /// <summary>Real ult by F[0] on tick c (SIM:663/743). F[1] invulnerable so no hitstop enters the window.</summary>
        static ChibiSimState FireUlt(int a0)
        {
            var s = MeleeArena();
            s.F[1].Invuln = LongTimer;
            s.F[0].Ult = ChibiSimConst.UltMax;
            if (a0 != 0) Set(s, AccumF, a0);
            int used = s.F[0].UltsUsed;
            Tick(s, ChibiButtons.Ultimate, ChibiButtons.None);
            Assert.AreEqual(used + 1, s.F[0].UltsUsed, "precondition: ult fired on tick c");
            return s;
        }

        // ================================================================== constants

        [Test]
        public void Constants_MatchDocSection5()
        {
            // Arrange / Act / Assert: code constants equal the section 5 table
            Assert.AreEqual(ScaleHitstop, Const("ScaleHitstop"));
            Assert.AreEqual(ScaleCinematic, Const("ScaleCinematic"));
            Assert.AreEqual(ScaleNormal, Const("ScaleNormal"));
            Assert.AreEqual(HitstopNormal, Const("HitstopNormal"));
            Assert.AreEqual(HitstopCrit, Const("HitstopCrit"));
            Assert.AreEqual(HitstopParry, Const("HitstopParry"));
            Assert.AreEqual(HitstopReflect, Const("HitstopReflect"));
            Assert.AreEqual(CinematicTicks, Const("CinematicTicks"));
            Assert.GreaterOrEqual(ChibiMatchConfig.RulesVersion, RulesVersion10, "RulesVersion bumped to at least 10");
        }

        // ================================================================== hitstop (T1-T4, T7, T13)

        [Test]
        public void Hitstop_Normal_RealTickCount()
        {
            // Arrange + Act: a normal light lands on the game step at real tick k (T1)
            var hit = LandHit(HitKind.Normal);
            // Assert: 3 real ticks, 0 steps, accum 150, next step at k+4
            AssertHitstopWindow(hit, HitstopNormal);
        }

        [Test]
        public void Hitstop_Crit_RealTickCount()
        {
            var hit = LandHit(HitKind.Crit);           // T2
            AssertHitstopWindow(hit, HitstopCrit);
        }

        [Test]
        public void Hitstop_PerfectParry_RealTickCount()
        {
            var hit = LandHit(HitKind.PerfectParry);   // T3
            AssertHitstopWindow(hit, HitstopParry);
        }

        [Test]
        public void Hitstop_Reflect_RealTickCount()
        {
            // T4: the reflect's own ApplyDamage 0.045 is absorbed by the max (section 1 H4)
            var hit = LandHit(HitKind.Reflect);
            AssertHitstopWindow(hit, HitstopReflect);
        }

        [TestCase(HitKind.Normal, HitstopNormal)]
        [TestCase(HitKind.Crit, HitstopCrit)]
        [TestCase(HitKind.PerfectParry, HitstopParry)]
        [TestCase(HitKind.Reflect, HitstopReflect)]
        public void Hitstop_NoGameStepInside_WorldFrozen(HitKind kind, int n)
        {
            // Arrange: hit applied at k (HP / Stun already applied, T7)
            var hit = LandHit(kind);
            var s = hit.S;
            string atK = Snap(s);
            int g0 = Get(s, GameTicksF);

            // Act + Assert: positions, velocities, HP, stun, move/shield/combo timers identical on k+1..k+N
            for (int j = 1; j <= n; j++)
            {
                Tick(s, hit.A(hit.H + j), hit.D(hit.H + j));
                Assert.AreEqual(atK, Snap(s), $"world frozen on real tick k+{j}");
                Assert.AreEqual(g0, Get(s, GameTicksF), $"GameTicks unchanged on k+{j}");
            }
            // trajectory resumes at k+N+1
            Tick(s, hit.A(hit.H + n + 1), hit.D(hit.H + n + 1));
            Assert.AreNotEqual(atK, Snap(s), "game step at k+N+1 advances the world");
        }

        [Test]
        public void Hitstop_Stacking_TakesMax()
        {
            // Arrange: symmetric lights trade on one game step; the first resolved hit crits, the second does not (T13)
            var s = MeleeArena();
            var a = On(ChibiButtons.Light, 0);
            var d = On(ChibiButtons.Light, 0);
            var c = Clone(s);
            int h = -1;
            for (int t = 0; t < MaxHitSearch && h < 0; t++)
            {
                int h0 = c.F[0].HpU, h1 = c.F[1].HpU;
                Tick(c, a(t), d(t));
                bool l0 = c.F[0].HpU < h0, l1 = c.F[1].HpU < h1;
                if (l0 || l1) { Assert.IsTrue(l0 && l1, "precondition: the lights trade on one tick"); h = t; }
            }
            Assert.GreaterOrEqual(h, 0, "precondition: a trade happened");
            for (int t = 0; t < h; t++) Tick(s, a(t), d(t));

            // Act
            Resolve(s, a, d, h, CritFirst);

            // Assert: HitstopRealTicks = max(3, 5) (MC:177 hitstopUntil = max(...))
            Assert.AreEqual(Math.Max(HitstopNormal, HitstopCrit), Get(s, HitstopF));
        }

        // ================================================================== cinematic + match timer (T5, T6, T12)

        [TestCase(0, T5Steps, T5Carry)]
        [TestCase(T6A0, T6Steps, T6Carry)]
        public void Cinematic_UltStart_48RealTicks_RunsGameStepsPerDoc(int a0, int expectedSteps, int expectedCarry)
        {
            // Arrange: real ult on tick c (game step at scale 1000 keeps A0)
            var s = FireUlt(a0);
            Assert.AreEqual(CinematicTicks, Get(s, CinematicF), "CinematicRealTicks = 48 after c (MC:188)");
            Assert.AreEqual(0, Get(s, CinFighterF), "CinematicFighter = ult user");
            int hp1 = s.F[1].HpU;

            // expected step ticks from the section 3 formula steps(n) = floor((A0 + sum scale) / 1000)
            var expected = new List<int>();
            int acc = a0;
            for (int j = 1; j <= CinematicTicks; j++)
            {
                acc += ScaleCinematic;
                if (acc >= StepCost) { acc -= StepCost; expected.Add(j); }
            }
            Assert.AreEqual(expectedSteps, expected.Count, "fixture: doc formula reproduces section 6");

            // Act
            var actual = new List<int>();
            int g = Get(s, GameTicksF);
            for (int j = 1; j <= CinematicTicks; j++)
            {
                Tick(s, ChibiButtons.None, ChibiButtons.None);
                Assert.AreEqual(0, Get(s, HitstopF), "precondition: no hitstop inside the cinematic");
                int g2 = Get(s, GameTicksF);
                if (g2 != g) actual.Add(j);
                g = g2;
            }

            // Assert
            Assert.AreEqual(hp1, s.F[1].HpU, "precondition: ult did not hit the invulnerable opponent");
            CollectionAssert.AreEqual(expected, actual, "game steps on c+5, c+10, ... (T5) / A0 carry (T6)");
            Assert.AreEqual(expectedCarry, Get(s, AccumF), "TimeAccum after c+48");
            Assert.AreEqual(0, Get(s, CinematicF), "cinematic over after 48 real ticks");
            Assert.AreEqual(NoCinematicFighter, Get(s, CinFighterF), "CinematicFighter reset to -1");
        }

        [Test]
        public void MatchTimer_CountsGameTicksNotRealTicks()
        {
            // Arrange: ult at c, then the clock is 3 game steps from time-out (section 4: GameTicks + 1 >= MatchTicks)
            var s = FireUlt(0);
            Set(s, GameTicksF, ChibiSimConst.MatchTicks - TimerLeadSteps);
            s.Frame = ChibiSimConst.MatchTicks - TimerLeadSteps;

            // Act: real ticks until the match ends
            int endedAt = -1;
            for (int j = 1; j <= CinematicTicks && endedAt < 0; j++)
            {
                Tick(s, ChibiButtons.None, ChibiButtons.None);
                if (s.MatchOver) endedAt = j;
            }

            // Assert: ends on the third cinematic game step (c+15), not after 3 real ticks
            Assert.AreEqual(TimerEndsAt, endedAt, "time-out counted in game steps (replaces s.Frame + 1, SIM:1189)");
            Assert.Greater(s.Frame, ChibiSimConst.MatchTicks, "real ticks ran past MatchTicks without ending the match");
        }

        [Test]
        public void Crit_DuringCinematic_SlowerFactorWins()
        {
            // Arrange: a light is one game step from landing; inject the state at the start of real tick c+20 of a
            // cinematic started at c with A0 = 0: 29 real ticks left, accum (19 * 200) % 1000 = 800, 3 steps already run
            var s = MeleeArena();
            var a = On(ChibiButtons.Light, 0);
            int h = Prime(s, a, Idle);
            Assert.AreEqual(0, Get(s, AccumF), "fixture: A0 = 0");
            int ticksLeft = CinematicTicks - (T12CritAt - 1);
            int stepsBefore = (T12CritAt - 1) * ScaleCinematic / StepCost;
            Set(s, CinematicF, ticksLeft);
            Set(s, CinFighterF, 0);
            Set(s, AccumF, (T12CritAt - 1) * ScaleCinematic % StepCost);
            int g0 = Get(s, GameTicksF);
            int hp = s.F[1].HpU;

            // Act: the crit lands on the game step at c+20
            Resolve(s, a, Idle, h, CritFirst);

            // Assert
            Assert.Less(s.F[1].HpU, hp, "precondition: hit landed on the step at c+20");
            Assert.AreEqual(g0 + 1, Get(s, GameTicksF), "c+20 is a game step");
            Assert.AreEqual(HitstopCrit, Get(s, HitstopF));
            for (int j = 1; j <= HitstopCrit; j++)
            {
                Tick(s, ChibiButtons.None, ChibiButtons.None);
                Assert.AreEqual(j * ScaleHitstop, Get(s, AccumF), $"c+{T12CritAt + j}: scale 50 beats 200 (min factor)");
            }
            int remaining = ticksLeft - 1 - HitstopCrit;
            for (int j = 1; j <= remaining; j++)
            {
                Assert.Greater(Get(s, CinematicF), 0, "cinematic not extended or shortened by the hitstop");
                Tick(s, ChibiButtons.None, ChibiButtons.None);
            }
            Assert.AreEqual(0, Get(s, CinematicF), "cinematic still ends at c+48 (counted in real ticks)");
            Assert.AreEqual(T12Steps, stepsBefore + (Get(s, GameTicksF) - g0), "T12: 8 game steps in c+1..c+48");
        }

        // ================================================================== input latch (T8-T11, 8.2, 8.3 item 1)

        [Test]
        public void PressDuringHitstop_FreeFighter_StartsOnNextGameStep()
        {
            // Arrange: free idle F[0]; hitstop of 3 starts after tick k (T9; source injected, see header)
            var s = MeleeArena();
            InjectHitstop(s, HitstopNormal);
            int g0 = Get(s, GameTicksF);

            // Act + Assert: Light pressed only on k+2
            for (int j = 1; j <= HitstopNormal; j++)
            {
                Tick(s, j == T9PressAt ? ChibiButtons.Light : ChibiButtons.None, ChibiButtons.None);
                Assert.AreEqual(-1, s.F[0].MoveSlot, $"no move starts inside the hitstop (k+{j})");
                if (j >= T9PressAt)
                    Assert.AreNotEqual(0, FGet(s.F[0], LatchedEdgesF) & (int)ChibiButtons.Light, "press latched");
            }
            Tick(s, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(g0 + 1, Get(s, GameTicksF), "k+4 is the next game step");
            Assert.AreNotEqual(-1, s.F[0].MoveSlot, "latched light starts on the game step at k+4");
            Assert.AreEqual(0, FGet(s.F[0], LatchedEdgesF), "latch cleared by the step");
        }

        [Test]
        public void PressDuringHitstop_BusyAtNextStep_IsLost()
        {
            // Arrange: real normal hit; the victim F[1] is in hitstun at k+4 (T8, D-N1)
            var hit = LandHit(HitKind.Normal);
            var s = hit.S;
            Assert.AreEqual(HitstopNormal, Get(s, HitstopF), "precondition: hitstop running");

            // Act: victim presses Light on k+2 only, then idles until well after the stun
            for (int j = 1; j <= HitstopNormal + 1; j++)
                Tick(s, hit.A(hit.H + j), j == T9PressAt ? ChibiButtons.Light : ChibiButtons.None);
            Assert.Greater(s.F[1].Stun, 0, "precondition: victim busy at the step k+4");
            Assert.AreEqual(0, FGet(s.F[1], LatchedEdgesF), "latch cleared at k+4 although unused");

            // Assert: the press is lost, never replayed when the stun ends (no input buffer)
            for (int j = 0; j < LongTimer / 4; j++)
            {
                Assert.AreEqual(-1, s.F[1].MoveSlot, $"lost press must not start a move ({j} ticks after k+4)");
                Tick(s, hit.A(hit.H + HitstopNormal + 2 + j), ChibiButtons.None);
            }
        }

        [Test]
        public void PressHeldAcrossTwoSteps_NotCarried()
        {
            // Arrange: cinematic from c; F[1] stunned through the step at c+5, free at a later step (8.2)
            var s = MeleeArena();
            InjectCinematic(s, 0);
            s.F[1].Stun = BusyStunTicks;

            // Act: F[1] presses Light on c+1 only
            for (int j = 1; j <= T10FirstStep; j++)
                Tick(s, ChibiButtons.None, j == T10PressAt ? ChibiButtons.Light : ChibiButtons.None);
            Assert.Greater(s.F[1].Stun, 0, "precondition: F[1] busy at the step c+5");
            Assert.AreEqual(0, FGet(s.F[1], LatchedEdgesF), "latch cleared by the first game step, used or not");
            for (int j = T10FirstStep + 1; j <= CinematicTicks; j++)
            {
                Tick(s, ChibiButtons.None, ChibiButtons.None);
                // Assert: never carried to the second (or any later) step
                Assert.AreEqual(-1, s.F[1].MoveSlot, $"press from c+1 must not start a move at c+{j}");
            }
            Assert.AreEqual(0, s.F[1].Stun, "precondition: F[1] became free inside the window");
        }

        [Test]
        public void PressDuringCinematic_FreeOpponent_StartsOnNextGameStep()
        {
            // Arrange: T10 / T11 (A-lost regression guard)
            var s = MeleeArena();
            InjectCinematic(s, 0);

            // Act: F[1] presses Light on c+1, releases on c+2
            for (int j = 1; j < T10FirstStep; j++)
            {
                Tick(s, ChibiButtons.None, j == T10PressAt ? ChibiButtons.Light : ChibiButtons.None);
                Assert.AreEqual(-1, s.F[1].MoveSlot, $"no game step before c+5 (c+{j})");
            }
            Tick(s, ChibiButtons.None, ChibiButtons.None);

            // Assert: starts on the step at c+5, then advances one move tick per game step (5 real ticks apart)
            Assert.AreNotEqual(-1, s.F[1].MoveSlot, "press in a cinematic must NOT be lost (T11)");
            int mt = s.F[1].MoveTick;
            for (int j = 1; j < T10StepSpacing; j++)
            {
                Tick(s, ChibiButtons.None, ChibiButtons.None);
                Assert.AreEqual(mt, s.F[1].MoveTick, "move frozen between game steps");
            }
            Tick(s, ChibiButtons.None, ChibiButtons.None);
            Assert.AreEqual(mt + 1, s.F[1].MoveTick, "next move tick on the step at c+10");
        }

        [Test]
        public void ShieldTapDuringCinematic_LatchedAsHeld()
        {
            // Arrange: 8.3 item 1 QA case
            var s = MeleeArena();
            InjectCinematic(s, 0);

            // Act: F[1] holds Block on c+1..c+2 only
            for (int j = 1; j < T10FirstStep; j++)
            {
                Tick(s, ChibiButtons.None, j <= 2 ? ChibiButtons.Shield : ChibiButtons.None);
                Assert.IsFalse(s.F[1].Shielding, $"no game step at c+{j}");
                Assert.AreNotEqual(0, FGet(s.F[1], LatchedHeldF) & (int)ChibiButtons.Shield, "Block bit OR-ed into LatchedHeld");
            }
            Tick(s, ChibiButtons.None, ChibiButtons.None);

            // Assert: shield active on the step at c+5 although Block is released at that tick
            Assert.IsTrue(s.F[1].Shielding, "tap between two game steps is not lost");
            Assert.AreEqual(0, s.F[1].ShieldTick, "first shield tick");
            Assert.AreEqual(0, FGet(s.F[1], LatchedHeldF), "LatchedHeld cleared by the step");
            for (int j = 0; j < T10StepSpacing; j++) Tick(s, ChibiButtons.None, ChibiButtons.None);
            Assert.IsFalse(s.F[1].Shielding, "released: no carry to the step at c+10");
        }

        // ================================================================== regression / determinism (T14, T17, 8.1)

        [Test]
        public void NoSlowMo_BehavesExactlyAsBefore()
        {
            // Arrange: no contact (jumps and shields only, no attack or dash), so no hitstop or cinematic can start
            var cfg = new ChibiMatchConfig { Seed = Seed, Class0 = FighterClass.Melee, Passive0 = BloodEdge,
                                             Class1 = FighterClass.Heavy, Passive1 = Juggernaut, Stage = SkyDojo };
            var p0 = new List<ushort>();
            var p1 = new List<ushort>();
            for (int t = 0; t < T14Ticks; t++)
            {
                var b0 = (t % 37 == 0 ? ChibiButtons.Jump : 0) | (t % 50 >= 10 && t % 50 < 25 ? ChibiButtons.Shield : 0);
                var b1 = (t % 41 == 3 ? ChibiButtons.Jump : 0) | (t % 60 >= 30 && t % 60 < 40 ? ChibiButtons.Shield : 0);
                p0.Add(P(b0).Raw);
                p1.Add(P(b1).Raw);
            }
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            int hp0 = s.F[0].HpU, hp1 = s.F[1].HpU;

            // Act
            for (int t = 0; t < T14Ticks; t++)
            {
                ChibiSimulation.Step(s, new ChibiInputPacket { Raw = p0[t] }, new ChibiInputPacket { Raw = p1[t] });

                // Assert per tick: every real tick is a game step; latch consumed on the same tick (bit-identical to v9)
                Assert.AreEqual(t + 1, Get(s, GameTicksF), $"GameTicks == real ticks (tick {t})");
                Assert.AreEqual(0, Get(s, AccumF), "TimeAccum stays 0 at scale 1000");
                Assert.AreEqual(0, Get(s, HitstopF));
                Assert.AreEqual(0, Get(s, CinematicF));
                for (int i = 0; i < 2; i++)
                {
                    Assert.AreEqual(0, FGet(s.F[i], LatchedEdgesF), "edges consumed on the tick they arrive");
                    Assert.AreEqual(0, FGet(s.F[i], LatchedHeldF), "held latch consumed on the same tick");
                }
            }
            Assert.AreEqual(hp0, s.F[0].HpU, "precondition: no contact");
            Assert.AreEqual(hp1, s.F[1].HpU, "precondition: no contact");

            // Assert: identical final checksum to the plain replay path (derived, not hard-coded)
            Assert.AreEqual(ChibiReplay.Simulate(cfg, p0, p1).ComputeChecksum(), s.ComputeChecksum());
        }

        [TestCase(HitstopF)]
        [TestCase(CinematicF)]
        [TestCase(CinFighterF)]
        [TestCase(AccumF)]
        [TestCase(GameTicksF)]
        public void StateHash_ChangesWithEachTimeScaleField(string field)
        {
            // Arrange (T17)
            var s = MeleeArena();
            var c = Clone(s);
            // Act
            Set(c, field, Get(c, field) + 1);
            // Assert
            Assert.AreNotEqual(s.ComputeChecksum(), c.ComputeChecksum(), $"{field} is mixed into the state hash");
        }

        [TestCase(LatchedEdgesF, ChibiButtons.Light)]
        [TestCase(LatchedHeldF, ChibiButtons.Shield)]
        public void StateHash_ChangesWithEachFighterLatch(string field, ChibiButtons bit)
        {
            var s = MeleeArena();
            var c = Clone(s);
            FSet(c.F[1], field, (int)bit);
            Assert.AreNotEqual(s.ComputeChecksum(), c.ComputeChecksum(), $"{field} is mixed into the state hash");
            var copy = Clone(c);
            Assert.AreEqual((int)bit, FGet(copy.F[1], field), $"{field} copied by CopyFrom (8.1)");
        }

        [Test]
        public void Rollback_SnapshotRestore_DuringCinematic_Identical()
        {
            // Arrange: real ult at c; F[1] (invulnerable) presses Light at c+16 -> pending in the latch at c+17
            var s = FireUlt(0);
            Func<int, ChibiButtons> d = t => t == RollbackPressAt ? ChibiButtons.Light
                : (t >= 20 && t <= 22 ? ChibiButtons.Shield : (t == 30 ? ChibiButtons.Jump : ChibiButtons.None));
            for (int j = 1; j <= RollbackSnapAt; j++) Tick(s, ChibiButtons.None, d(j));
            Assert.Greater(Get(s, CinematicF), 0, "precondition: snapshot is mid-cinematic");
            Assert.AreEqual(RollbackSnapAt * ScaleCinematic % StepCost, Get(s, AccumF), "precondition: non-zero carry");
            Assert.AreNotEqual(0, FGet(s.F[1], LatchedEdgesF), "precondition: press pending in the latch");

            var snap = Clone(s);
            foreach (var f in new[] { HitstopF, CinematicF, CinFighterF, AccumF, GameTicksF })
                Assert.AreEqual(Get(s, f), Get(snap, f), $"{f} copied by CopyFrom");
            Assert.AreEqual(FGet(s.F[1], LatchedEdgesF), FGet(snap.F[1], LatchedEdgesF), "LatchedEdges copied");
            Assert.AreEqual(s.ComputeChecksum(), snap.ComputeChecksum());

            var truth = new List<uint>();
            for (int j = RollbackSnapAt + 1; j <= RollbackRunTo; j++) { Tick(s, ChibiButtons.None, d(j)); truth.Add(s.ComputeChecksum()); }

            // Act: mispredict 3 ticks, roll back into the dirty state, re-simulate the true inputs
            var r = Clone(snap);
            for (int j = RollbackSnapAt + 1; j <= RollbackSnapAt + 3; j++) Tick(r, ChibiButtons.None, ChibiButtons.Heavy);
            r.CopyFrom(snap);
            var resim = new List<uint>();
            for (int j = RollbackSnapAt + 1; j <= RollbackRunTo; j++) { Tick(r, ChibiButtons.None, d(j)); resim.Add(r.ComputeChecksum()); }

            // Assert
            CollectionAssert.AreEqual(truth, resim, "re-simulation from a mid-cinematic snapshot is identical");
        }

        // ================================================================== SpawnFrame / replay (section 5, 8.3 item 3)

        [Test]
        public void ProjectileSpawnFrame_UsesGameTicks()
        {
            // Arrange: an injected 8-tick hitstop makes GameTicks lag Frame by 8, then normal speed resumes
            var s = Arena(FighterClass.Ranged, EagleEye, FighterClass.Melee, BloodEdge);
            InjectHitstop(s, SpawnShiftTicks);
            for (int j = 0; j < SpawnShiftTicks; j++) Tick(s, ChibiButtons.None, ChibiButtons.None);
            Assert.AreNotEqual(s.Frame, Get(s, GameTicksF), "precondition: GameTicks differs from Frame");

            // Act: Ranged light until its projectile exists
            ChibiProjectileSim shot = null;
            var a = On(ChibiButtons.Light, 0);
            for (int t = 0; t < MaxHitSearch && shot == null; t++)
            {
                Tick(s, a(t), ChibiButtons.None);
                foreach (var p in s.Proj) if (p != null && p.Active && p.Owner == 0) { shot = p; break; }
            }
            Assert.IsNotNull(shot, "precondition: Ranged light spawned a projectile");

            // Assert: stored in the spawning game step, before GameTicks++ (section 5 "SpawnFrame stores GameTicks")
            Assert.AreEqual(Get(s, GameTicksF) - 1, shot.SpawnFrame, "SpawnFrame counts game ticks, not real ticks");
        }

        [Test]
        public void Replay_V2_ContainsConfigHash()
        {
            // Arrange: ChibiReplay.Write / TryRead are readable today
            var cfg = new ChibiMatchConfig { Seed = Seed, Class0 = FighterClass.Melee, Passive0 = BloodEdge,
                                             Class1 = FighterClass.Ranged, Passive1 = EagleEye, Stage = LavaForge };
            var p0 = new List<ushort>();
            var p1 = new List<ushort>();
            for (int t = 0; t < SettleTicks; t++) { p0.Add(P(t == 10 ? ChibiButtons.Jump : 0).Raw); p1.Add(P(0).Raw); }

            // Act
            byte[] bytes;
            using (var ms = new MemoryStream()) { ChibiReplay.Write(ms, cfg, p0, p1); bytes = ms.ToArray(); }

            // Assert: version 2, config hash stored (little-endian, BinaryWriter)
            Assert.AreEqual(ReplayVersion2, bytes[4], "replay Version bumped to 2");
            uint hash = cfg.ComputeHash();
            var key = new[] { (byte)hash, (byte)(hash >> 8), (byte)(hash >> 16), (byte)(hash >> 24) };
            int at = -1;
            for (int i = 5; i + 4 <= bytes.Length && at < 0; i++)
                if (bytes[i] == key[0] && bytes[i + 1] == key[1] && bytes[i + 2] == key[2] && bytes[i + 3] == key[3]) at = i;
            Assert.GreaterOrEqual(at, 0, "ChibiMatchConfig.ComputeHash() stored in the replay");

            using (var ms = new MemoryStream(bytes))
                Assert.IsTrue(ChibiReplay.TryRead(ms, out var read, out _, out _, out _), "own v2 replay reads back");
            using (var ms = new MemoryStream(bytes))
            {
                ChibiReplay.TryRead(ms, out var read2, out _, out _, out _);
                Assert.AreEqual(hash, read2.ComputeHash(), "read config reproduces the hash (all hashed fields stored)");
            }

            var bad = (byte[])bytes.Clone();
            bad[at] ^= 0xFF;
            using (var ms = new MemoryStream(bad))
                Assert.IsFalse(ChibiReplay.TryRead(ms, out _, out _, out _, out _), "config hash mismatch rejected");

            var v1 = (byte[])bytes.Clone();
            v1[4] = 1;
            using (var ms = new MemoryStream(v1))
                Assert.IsFalse(ChibiReplay.TryRead(ms, out _, out _, out _, out _), "v1 replay rejected under v10");
        }
    }
}
