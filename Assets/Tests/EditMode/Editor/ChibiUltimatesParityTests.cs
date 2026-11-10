// Story type: Logic (ultimate programs, common ult start, Kick hold state machine) -- evidence: automated EditMode unit test
// -- gate: BLOCKING
// Source spec: docs/architecture/netcode/ultimates-conversion.md (port step 14, RulesVersion 14): section 1 common start,
// section 2 per-ult tables, section 4 contract, section 5 reference values R1-R9, section 6 test list 1-25 and the
// "Technical-director review" amendments 1-8 plus its missing-tests list (binding). Original: Fighter/Fighter.cs (F:).
//
// User decisions applied: D-U1 = A (stand-in button fires only on the free path until step 15; full common start now:
// Interrupt, Stun = 0, invuln; a same-tick jump keeps its vy, amendment 2), D-U2 = A (Sword Aura / Fire Ring start on the
// press tick, no lock), D-U3 = A (KickFromX / KickFromY / KickToX / KickToY), D-U4 = A (Kick on an invulnerable victim in
// range still holds and stuns, no damage).
//
// COMPILES TODAY, FAILS TODAY: the v14 members (ChibiFighterSim.KickFromX / KickFromY / KickToX / KickToY, the new
// ChibiSimConst constants) do not exist yet. They are reached ONLY through Need / GetInt / SetInt / ConstOf below, which
// Assert.Fail ("... missing") when absent. Every other member used here exists in v13.
//
// Tick convention: "tick f" = the f-th GAME step (ChibiTestTime.StepGame) counted from the press step (f = 0); values are
// read at the END of that step. Amendment 6: the ult cinematic and the Kick's first hit (hitstop) stretch real time, so
// every count here is in game steps, never in ChibiSimulation.Step calls. A hit "on tick f" = the victim's HpU dropped
// during step f.
//
// Real input path: the meter is filled by the fixture (f.Ult = UltMax) and the Ultimate BUTTON is pressed (the step-14
// stand-in). FIXTURE INJECTION (documented): ring-out tests teleport a fighter past the blast line; the Kick out-of-range
// box test moves the victim into the box on tick 15; range-edge tests pin the victim's Y.
//
// Values marked "TBD-verify" are pinned from the doc but depend on an ordering the doc leaves to the implementer
// (timer decrement vs end-of-step read, gravity after a frame-0 velocity write, projectile bounds); confirm on first run.
//
// Arena: Sky Dojo (stage 0), main stage top Y 0, |X| <= 8000, BlastX 17000, KillY -9725.
using System;
using System.Collections.Generic;
using System.Reflection;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Ultimate parity (port step 14) between the deterministic 60 Hz simulation and Fighter.cs FireUltimate + ult coroutines.
    /// Reference values: ultimates-conversion.md section 5; test list: section 6 + technical-director review.
    /// </summary>
    public class ChibiUltimatesParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;
        const int SettleAfterPut = 3;
        const int SkyDojo = 0;

        // ---- passives (ChibiCombatStats.Create)
        const int SwiftBlade = 0, BloodEdge = 1, HeavyP1 = 1, RangedP0 = 0, MagicP0 = 0, PolearmP0 = 0;

        // ---- section 1 common start
        const int UltCommonInvuln = 36;
        const int CinematicTicks = 48;

        // ---- 2.1 / 2.4
        const int AuraDuration = 600, RingDuration = 600;

        // ---- 2.2 Smash
        const int SmashJumpVel = 225000, SmashMinFrame = 10, SmashTimeout = 66, SmashAfterSlam = 18, SmashInvuln = 84;
        const int SmashWaveDx = 600, SmashWaveDy = 550, SmashWaveVel = 195000, SmashWaveR = 600, SmashWaveGrow = 14,
                  SmashWaveLife = 66, SmashWaveMult = 3000;

        // ---- 2.3 Thousand Arrows
        const int ArrowsLock = 48, ArrowsInvuln = 48, LeadDx = 500, LeadDy = 975, LeadVx = 135000, LeadVy = 180000,
                  LeadLife = 27, VolleyCount = 18, RespawnInvuln90 = 90;

        // ---- 2.5 Kick
        const int KickLock = 114, KickInvuln = 120, KickRangeX = 8000, KickRangeY = 3000, KickDashVel = 300000,
                  KickDashFrames = 15, KickLerpFrames = 9, KickBehind = 1300, KickStun = 96, KickHit1Stun = 72,
                  KickRiseFrame = 30, KickRiseVel = 180000, KickReleaseFrame = 50, KickHit2KbX = 180000,
                  KickHit2KbY = 135000, KickBoxBw = 2500;
        const int MoveFriction = 6250;   // SIM:28, grounded decel with a lock (fact 4)

        const int RulesVersion14 = 14;

        static readonly ChibiInputPacket N = default;

        // ================================================================== reflection access to v14 members

        const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static FieldInfo Need(Type t, string name)
        {
            var fi = t.GetField(name, Inst);
            if (fi == null) Assert.Fail($"field {t.Name}.{name} missing (ultimates-conversion.md section 4, RulesVersion 14)");
            return fi;
        }

        static int GetInt(object o, string name) => Convert.ToInt32(Need(o.GetType(), name).GetValue(o));
        static bool GetBool(object o, string name) => Convert.ToBoolean(Need(o.GetType(), name).GetValue(o));

        static void SetInt(object o, string name, int v)
        {
            var fi = Need(o.GetType(), name);
            fi.SetValue(o, fi.FieldType.IsEnum ? Enum.ToObject(fi.FieldType, v) : Convert.ChangeType(v, fi.FieldType));
        }

        /// <summary>A public const / static int on <paramref name="t"/>; Assert.Fail when absent.</summary>
        static int ConstOf(Type t, string name)
        {
            var fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (fi == null) Assert.Fail($"constant {t.Name}.{name} missing (ultimates-conversion.md section 4, RulesVersion 14)");
            return Convert.ToInt32(fi.GetValue(null));
        }

        static void HeldInvariant(ChibiSimState s, int tick)
        {
            for (int i = 0; i < 2; i++)
                Assert.AreEqual(s.F[i].Holding, s.F[1 - i].Held, $"step-12 A6: F[{i}].Holding != F[{1 - i}].Held after tick {tick}");
        }

        // ================================================================== match fixture

        static ChibiSimState NewMatch(FighterClass c0, int p0, FighterClass c1, int p1, uint seed = Seed)
        {
            var cfg = new ChibiMatchConfig { Seed = seed, Class0 = c0, Passive0 = p0, Class1 = c1, Passive1 = p1, Stage = SkyDojo };
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            for (int t = 0; t < SettleTicks; t++) ChibiSimulation.Step(s, N, N);
            return s;
        }

        /// <summary>Fighter at rest at (x, y), free to act. Grounded only on the main stage top (y == 0).</summary>
        static void Put(ChibiFighterSim f, int x, int y, int facing, int vx = 0)
        {
            f.X = x; f.Y = y; f.Vx = vx; f.Vy = 0; f.XRem = 0; f.YRem = 0;
            f.Grounded = y == 0; f.AirJumps = 1; f.Facing = facing; f.Stun = 0; f.Invuln = 0;
        }

        /// <summary>F[0] at (x0, 0) facing <paramref name="facing"/>, F[1] at (x1, 0) facing the other way; settled 3 ticks.</summary>
        static ChibiSimState Arena(FighterClass c0, int p0, int x0, FighterClass c1, int p1, int x1, int facing = 1)
        {
            var s = NewMatch(c0, p0, c1, p1);
            Put(s.F[0], x0, 0, facing);
            Put(s.F[1], x1, 0, -facing);
            for (int t = 0; t < SettleAfterPut; t++) ChibiSimulation.Step(s, N, N);
            return s;
        }

        static void Pin(ChibiFighterSim f, int x, int y)
        {
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = y == 0;
        }

        static ChibiInputPacket In(ChibiButtons b) => ChibiInputPacket.Make(ChibiDir.Neutral, b);

        /// <summary>
        /// Lock-end check in behaviour form (step-12 convention, D-N1): <paramref name="s"/> is the state after tick L-2.
        /// A Light pressed on tick L-1 is lost; a Light pressed on tick L starts. Original: TickTimers (F:174) runs before
        /// the ult sets actionLock (F:176), so an N-tick lock frees on tick N, the same as skills.
        /// </summary>
        static void AssertLockEndsOn(ChibiSimState s, int i, string label)
        {
            var light = In(ChibiButtons.Light);
            var c = Clone(s);
            if (i == 0) Game(c, light, N); else Game(c, N, light);
            Assert.AreNotEqual(LightSlotFor(c.F[i]), c.F[i].MoveSlot, label + ": Light on tick L-1 is lost (still locked)");
            Game(s, N, N);
            int expect = LightSlotFor(s.F[i]);
            if (i == 0) Game(s, light, N); else Game(s, N, light);
            Assert.AreEqual(expect, s.F[i].MoveSlot, label + ": Light on tick L starts (free)");
        }

        /// <summary>
        /// Slot a neutral Light starts from this state: the light combo (0) on the ground, the class neutral air in the air
        /// (rules v18, OD-2 = A: aerials replace the air light; intended change, the lock timing under test is unchanged).
        /// </summary>
        static int LightSlotFor(ChibiFighterSim f) => f.Grounded ? 0 : ChibiMovesetData.SlotFor(f.Class, 0, 0, true);

        static void Game(ChibiSimState s, ChibiInputPacket a, ChibiInputPacket b) => ChibiTestTime.StepGame(s, a, b);

        /// <summary>One game step with <paramref name="mine"/> for F[who] and <paramref name="other"/> for the opponent.</summary>
        static void GameFor(ChibiSimState s, int who, ChibiInputPacket mine, ChibiInputPacket other = default)
        {
            if (who == 0) Game(s, mine, other); else Game(s, other, mine);
        }

        /// <summary>Tick 0: fill the meter and press Ultimate (plus <paramref name="extra"/>) for F[who].</summary>
        static void PressUlt(ChibiSimState s, int who = 0, ChibiButtons extra = ChibiButtons.None)
        {
            ChibiTestTime.ArmUltFire(s, who);   // rules v15: the gesture fire point, top of tick 0
            GameFor(s, who, In(extra));
        }

        static ChibiSimState Clone(ChibiSimState s)
        {
            var c = new ChibiSimState();
            c.CopyFrom(s);
            return c;
        }

        static List<ChibiProjectileSim> OfKind(ChibiSimState s, ChibiProjKind k)
        {
            var l = new List<ChibiProjectileSim>();
            foreach (var p in s.Proj) if (p.Active && p.Kind == k) l.Add(p);
            return l;
        }

        /// <summary>End-of-tick-0 read of a value set to <paramref name="n"/> on tick 0 and decremented by TickTimers (TBD-verify order).</summary>
        static void AssertSetOnPress(int n, int actual, string what) =>
            Assert.That(actual == n || actual == n - 1, $"{what}: expected {n} (or {n - 1} if TickTimers ran after the press) after tick 0, got {actual}");   // TBD-verify

        /// <summary>A velocity written before Physics: end-of-tick value is v or v - Gravity (TBD-verify gravity order).</summary>
        static void AssertVyWritten(int v, int actual, string what) =>
            Assert.That(actual <= v && actual >= v - ChibiSimConst.Gravity, $"{what}: expected Vy in [{v - ChibiSimConst.Gravity}, {v}], got {actual}");   // TBD-verify

        // ================================================================== 1-5 common start (R1)

        // Test 1 / R1 -- F:956-961: Ult 0, UltsUsed 1, cinematic 48 for the presser, Stun 0, for every class and both slots.
        [TestCase(FighterClass.Melee, 0)] [TestCase(FighterClass.Heavy, 0)] [TestCase(FighterClass.Ranged, 0)]
        [TestCase(FighterClass.Magic, 0)] [TestCase(FighterClass.Polearm, 0)] [TestCase(FighterClass.Polearm, 1)]
        public void T01_CommonStart_EachClass_MeterSpentCinematicStarted(FighterClass c, int who)
        {
            var s = who == 0 ? Arena(c, 0, 0, FighterClass.Melee, BloodEdge, 12000)
                             : Arena(FighterClass.Melee, BloodEdge, 12000, c, 0, 0, -1);
            PressUlt(s, who);
            var f = s.F[who];
            Assert.AreEqual(0, f.Ult, "R1: Ult 0");
            Assert.AreEqual(1, f.UltsUsed, "R1: UltsUsed 1");
            Assert.AreEqual(who, s.CinematicFighter, "R1: CinematicFighter = presser");
            Assert.That(s.CinematicRealTicks <= CinematicTicks && s.CinematicRealTicks >= CinematicTicks - 1,
                $"R1: CinematicRealTicks 48 at the press (got {s.CinematicRealTicks})");   // TBD-verify decrement order
            Assert.AreEqual(0, f.Stun, "R1: Stun 0");
        }

        // Test 2 -- F:960, 987, 1013, 1039: Invuln after tick 0 per class (assignment for Heavy / Ranged / Polearm).
        [TestCase(FighterClass.Melee, UltCommonInvuln)]
        [TestCase(FighterClass.Heavy, SmashInvuln)]
        [TestCase(FighterClass.Ranged, ArrowsInvuln)]
        [TestCase(FighterClass.Magic, UltCommonInvuln)]
        [TestCase(FighterClass.Polearm, KickInvuln)]
        public void T02_Invuln_PerClass_MatchesOriginal(FighterClass c, int expected)
        {
            var s = Arena(c, 0, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(s);
            AssertSetOnPress(expected, s.F[0].Invuln, $"test 2 {c} Invuln");
        }

        // Test 3 -- F:1013: Thousand Arrows ASSIGNS invuln 48, lowering a respawn invuln of 90.
        [Test]
        public void T03_Arrows_RespawnInvuln90_LoweredTo48()
        {
            var s = Arena(FighterClass.Ranged, RangedP0, 0, FighterClass.Melee, BloodEdge, 12000);
            s.F[0].Invuln = RespawnInvuln90;
            PressUlt(s);
            AssertSetOnPress(ArrowsInvuln, s.F[0].Invuln, "test 3: Arrows assigns 48");
        }

        // Test 4 -- SIM stand-in: meter 999 -> no ult, press falls through.
        [Test]
        public void T04_Meter999_NoUlt()
        {
            var s = Arena(FighterClass.Melee, SwiftBlade, 0, FighterClass.Melee, BloodEdge, 12000);
            s.F[0].Ult = ChibiSimConst.UltMax - 1;
            Game(s, In(ChibiButtons.Ultimate), N);
            Assert.AreEqual(0, s.F[0].UltsUsed, "test 4: no ult below a full meter");
            Assert.AreEqual(ChibiSimConst.UltMax - 1, s.F[0].Ult, "test 4: meter untouched");
            Assert.AreEqual(-1, s.CinematicFighter, "test 4: no cinematic");
        }

        // Test 5 -- D-U1 = A: a stunned fighter cannot ult in step 14 (documented step-15 gap, F:417 vs SIM:1129).
        [Test]
        public void T05_Stunned_GestureUltFiresAndClearsStun()
        {
            // Rules v15 (step 15) replaces D-U1 A: the gesture ult fires at F:176, before the hitstun gate (F:179).
            var s = Arena(FighterClass.Polearm, PolearmP0, 0, FighterClass.Melee, BloodEdge, 12000);
            s.F[0].Stun = 30;
            PressUlt(s);
            Assert.AreEqual(1, s.F[0].UltsUsed, "step 15: the ult fires while stunned");
            Assert.AreEqual(0, s.F[0].Stun, "FireUltimate clears hitstun (F:958)");
        }

        // Test 25 -- F:674: Dash + Ult on the same tick -> the ult's Interrupt clears DashTicks.
        [Test]
        public void T06_DashAndUltSameTick_InterruptClearsDash()
        {
            // Rules v15 order (F:176 ult, F:309 dash press): a dash already running is cleared by the ult's Interrupt ...
            var s = Arena(FighterClass.Melee, SwiftBlade, 0, FighterClass.Melee, BloodEdge, 12000);
            Game(s, In(ChibiButtons.Dash), N);
            Assert.Greater(s.F[0].DashTicks, 0, "precondition: dashing");
            PressUlt(s);
            Assert.AreEqual(1, s.F[0].UltsUsed, "precondition: ult fired");
            Assert.AreEqual(0, s.F[0].DashTicks, "test 25: Interrupt cleared the running dash");

            // ... while a Dash pressed on the fire tick starts after it (HandleActions runs after FireUltimate).
            var t = Arena(FighterClass.Melee, SwiftBlade, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(t, 0, ChibiButtons.Dash);
            Assert.AreEqual(1, t.F[0].UltsUsed);
            Assert.AreEqual(ChibiSimConst.DashDuration, t.F[0].DashTicks, "same-tick Dash press starts after the ult");
        }

        // ================================================================== Sword Aura / Fire Ring (D-U2 = A)

        // Test 6 / R2 -- F:966, F:490-494: aura on tick 0, free on tick 1; a Light / Heavy on tick 1 spawns an AuraWave.
        [TestCase(ChibiButtons.Light)]
        [TestCase(ChibiButtons.Heavy)]
        public void T07_SwordAura_InstantNoLock_NormalSpawnsWave(ChibiButtons normal)
        {
            var s = Arena(FighterClass.Melee, SwiftBlade, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(s);
            AssertSetOnPress(AuraDuration, s.F[0].AuraTicks, "R2: AuraTicks on tick 0");
            Assert.AreEqual(-1, s.F[0].MoveSlot, "D-U2 A: no lock, free after the press tick");
            Game(s, In(normal), N);
            Assert.AreEqual(normal == ChibiButtons.Light ? 0 : 1, s.F[0].MoveSlot, "R2: normal starts on tick 1");
            bool wave = false;
            for (int t = 2; t <= 60 && !wave; t++) { Game(s, N, N); wave = OfKind(s, ChibiProjKind.AuraWave).Count > 0; }
            Assert.IsTrue(wave, "R2: the normal's strike spawns an AuraWave");
        }

        // Test 8 / R3 -- F:975: Fire Ring on tick 0, free on tick 1.
        [Test]
        public void T08_FireRing_InstantNoLock()
        {
            var s = Arena(FighterClass.Magic, MagicP0, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(s);
            AssertSetOnPress(RingDuration, s.F[0].RingTicks, "R3: RingTicks on tick 0");
            Assert.AreEqual(-1, s.F[0].MoveSlot, "D-U2 A: no lock");
        }

        // TD item 5 -- Aura and Fire Ring survive the owner's ring-out (RingOut F:692-708 does not clear them).
        [TestCase(FighterClass.Melee)]
        [TestCase(FighterClass.Magic)]
        public void T09_AuraAndRing_SurviveOwnerRingOut(FighterClass c)
        {
            var s = Arena(c, 0, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(s);
            for (int t = 1; t <= 4; t++) Game(s, N, N);
            int before = c == FighterClass.Melee ? s.F[0].AuraTicks : s.F[0].RingTicks;
            Assert.Greater(before, 500, "precondition: buff running on tick 4");
            s.F[0].X = 20000;                                       // fixture: past BlastX 17000
            Game(s, N, N);
            Assert.AreEqual(ChibiStageTable.Get(SkyDojo).RespawnX, s.F[0].X, "precondition: ring-out on tick 5");
            int after = c == FighterClass.Melee ? s.F[0].AuraTicks : s.F[0].RingTicks;
            Assert.AreEqual(before - 1, after, "TD item 5: buff keeps counting down through the ring-out");
        }

        // ================================================================== Smash

        /// <summary>Runs ticks 1..last after the press; returns the slam tick (first tick with SmashWaves) and start-of-tick Grounded.</summary>
        static int SmashRun(ChibiSimState s, int last, List<bool> groundedAtStart, Func<int, ChibiInputPacket> extra = null)
        {
            int slam = -1;
            for (int t = 1; t <= last; t++)
            {
                groundedAtStart.Add(s.F[0].Grounded);
                Game(s, extra != null ? extra(t) : N, N);
                if (slam < 0 && OfKind(s, ChibiProjKind.SmashWave).Count > 0) slam = t;
            }
            return slam;
        }

        // Test 9 / R4 -- F:986-1007: Vy 225000 on tick 0, slam on the first start-grounded tick >= 10, waves ±195000 at
        // (X ± 600, Y + 550), R 600, Mult 3000, Pierce, indestructible, free on S + 18.
        [Test]
        public void T10_Smash_Grounded_JumpThenSlamWavesThenFree()
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(s);
            AssertVyWritten(SmashJumpVel, s.F[0].Vy, "R4 tick 0 (frame-0 write applied, fact 3)");
            Assert.AreEqual(0, s.F[0].Vx, "R4 tick 0 Vx 0");
            Assert.AreEqual(5, s.F[0].MoveSlot, "R4: locked");
            var g = new List<bool> { false };                       // index = tick; tick 0 unused
            int slam = SmashRun(s, 100, g);
            int expected = -1;
            for (int k = SmashMinFrame; k < g.Count && k < SmashTimeout; k++) if (g[k]) { expected = k; break; }
            if (expected < 0) expected = SmashTimeout;
            Assert.AreEqual(expected, slam, "R4: slam on the first start-of-tick-grounded tick >= 10");

            var s2 = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(s2);
            for (int t = 1; t < slam; t++) Game(s2, N, N);
            Assert.AreEqual(0, OfKind(s2, ChibiProjKind.SmashWave).Count, "R4: no waves before the slam");
            Game(s2, N, N);
            var w = OfKind(s2, ChibiProjKind.SmashWave);
            Assert.AreEqual(2, w.Count, "R4: two waves");
            int fx = s2.F[0].X, fy = s2.F[0].Y;
            w.Sort((a, b) => a.Vx.CompareTo(b.Vx));
            for (int n = 0; n < 2; n++)
            {
                int dir = n == 0 ? -1 : 1;
                Assert.AreEqual(fx + dir * SmashWaveDx, w[n].X, $"R4 wave {dir} X");
                Assert.AreEqual(fy + SmashWaveDy, w[n].Y, $"R4 wave {dir} Y");
                Assert.AreEqual(dir * SmashWaveVel, w[n].Vx, $"R4 wave {dir} Vx");
                Assert.AreEqual(SmashWaveR, w[n].Radius, $"R4 wave {dir} R");
                Assert.AreEqual(SmashWaveGrow, w[n].GrowPerTick, $"R4 wave {dir} GrowPerTick");
                Assert.AreEqual(SmashWaveLife, w[n].Life, $"R4 wave {dir} Life");   // TBD-verify (66 at spawn)
                Assert.AreEqual(SmashWaveMult, w[n].Mult, $"R4 wave {dir} Mult");
                Assert.AreNotEqual(0, w[n].Flags & ChibiProjectileSim.FPierce, "R4: Pierce");
                Assert.AreEqual(0, w[n].Flags & ChibiProjectileSim.FDestructible, "R4: indestructible");
            }
            Assert.Less(Array.IndexOf(s2.Proj, w[0]), Array.IndexOf(s2.Proj, w[1]), "R4: spawn order dir -1 then +1");
            for (int t = slam + 1; t < slam + SmashAfterSlam - 1; t++) Game(s2, N, N);
            AssertLockEndsOn(s2, 0, $"R4: lock ends on tick S + 18 = {slam + 18}");
        }

        // Test 10 -- CO:77/97: R = 600 + 14 n after n updates (first update the tick after spawn).
        [Test]
        public void T11_SmashWave_GrowsFourteenPerUpdate()
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(s);
            int slam = SmashRun(s, 100, new List<bool>());
            Assert.Greater(slam, 0, "precondition: slam happened");
            // re-run to the slam tick, then watch growth
            var s2 = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(s2);
            for (int t = 1; t <= slam; t++) Game(s2, N, N);
            var w = OfKind(s2, ChibiProjKind.SmashWave);
            for (int n = 1; n <= 5; n++)
            {
                Game(s2, N, N);
                foreach (var p in w) Assert.AreEqual(SmashWaveR + SmashWaveGrow * n, p.Radius, $"test 10: R after {n} updates");
            }
        }

        // Amendment 5 -- a Pierce wave hits a fighter exactly once (hasHit never reset, CO:89/100) and keeps flying.
        [TestCase(3000)]
        [TestCase(-3000)]
        public void T12_SmashWave_HitsVictimOnceAndContinues(int victimX)
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, victimX);
            PressUlt(s);
            var v = s.F[1];
            int hits = 0, firstHit = -1;
            ChibiProjectileSim wave = null;
            for (int t = 1; t <= 160; t++)
            {
                int hp = v.HpU;
                Game(s, N, N);
                if (wave == null)
                    foreach (var p in OfKind(s, ChibiProjKind.SmashWave)) if (Math.Sign(p.Vx) == Math.Sign(victimX)) wave = p;
                if (v.HpU < hp) { hits++; if (firstHit < 0) firstHit = t; }
                if (firstHit > 0 && t == firstHit + 1)
                    Assert.IsTrue(wave != null && wave.Active && wave.Kind == ChibiProjKind.SmashWave, "A5: wave still flying after its hit (Pierce)");
            }
            Assert.AreEqual(1, hits, "A5: exactly one hit from the wave travelling toward the victim");
        }

        // Test 11 / R5 -- F:990: Smash with no ground -> slam on tick 66 in the air, free on 84. Off-stage start Y 5000.
        [Test]
        public void T13_Smash_OffStage_SlamOnTimeoutThenFree84()
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, -3000);
            Put(s.F[0], 12000, 5000, 1);                            // off the main stage, airborne (TBD-verify no platform here)
            PressUlt(s);
            int slam = -1;
            for (int t = 1; t <= SmashTimeout + SmashAfterSlam - 2; t++)
            {
                Game(s, N, N);
                if (slam < 0 && OfKind(s, ChibiProjKind.SmashWave).Count > 0) slam = t;
            }
            Assert.AreEqual(SmashTimeout, slam, "R5: slam on tick 66 in the air");
            AssertLockEndsOn(s, 0, "R5: lock ends on tick 84");
        }

        // Test 12 -- F:291-305: an air jump on tick 20 during Smash sets AirJumpVel (actionLock does not gate jump); slam later.
        [Test]
        public void T14_Smash_AirJumpTick20_SetsAirJumpVelAndDelaysSlam()
        {
            var a = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(a);
            int baseSlam = SmashRun(a, 120, new List<bool>());
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(s);
            for (int t = 1; t < 20; t++) Game(s, N, N);
            Game(s, In(ChibiButtons.Jump), N);
            AssertVyWritten(ChibiSimConst.AirJumpVel, s.F[0].Vy, "test 12: air jump during Smash");
            int slam = 20 + SmashRun(s, 120, new List<bool>());
            Assert.Greater(slam, baseSlam, "test 12: the air jump delays the slam");
        }

        // Amendment 2 -- same-tick Jump + Ult (Smash): the jump's vy wins (original Smash ends at 13 u/s, not 15).
        [Test]
        public void T15_Smash_JumpAndUltSameTick_VyIsJumpValue()
        {
            var j = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 12000);
            Game(j, In(ChibiButtons.Jump), N);
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 12000);
            PressUlt(s, 0, ChibiButtons.Jump);
            Assert.AreEqual(1, s.F[0].UltsUsed, "precondition: ult fired");
            Assert.AreEqual(5, s.F[0].MoveSlot, "precondition: Smash running");
            Assert.AreEqual(j.F[0].Vy, s.F[0].Vy, "A2: Vy equals a plain grounded jump's Vy, not SmashJumpVel");
        }

        // ================================================================== Thousand Arrows

        // Test 13 / R6 + TD item 4 -- lead (X+500, Y+975), V (135000, 180000), R 0, Life 27, harmless, indestructible;
        // split into 18 VolleyArrows on tick 27; free on 48.
        [Test]
        public void T16_Arrows_LeadThenSplitOnTick27_Free48()
        {
            var s = Arena(FighterClass.Ranged, RangedP0, 0, FighterClass.Melee, BloodEdge, 3000);
            int fx = s.F[0].X, fy = s.F[0].Y;
            PressUlt(s);
            var lead = OfKind(s, ChibiProjKind.VolleyLead);
            Assert.AreEqual(1, lead.Count, "R6: one lead on tick 0");
            var l = lead[0];
            Assert.AreEqual(fx + LeadDx, l.X, "R6 lead X");
            Assert.AreEqual(fy + LeadDy, l.Y, "R6 lead Y");
            Assert.AreEqual(LeadVx, l.Vx, "R6 lead Vx");
            Assert.AreEqual(LeadVy, l.Vy, "R6 lead Vy");
            Assert.AreEqual(0, l.Radius, "R6 lead R 0");
            Assert.AreEqual(LeadLife, l.Life, "R6 lead Life 27");
            Assert.AreNotEqual(0, l.Flags & ChibiProjectileSim.FHarmless, "R6: harmless");
            Assert.AreEqual(0, l.Flags & ChibiProjectileSim.FDestructible, "R6: indestructible");
            Assert.AreEqual(0, OfKind(s, ChibiProjKind.VolleyArrow).Count, "R6: no stream arrows (stand-in removed)");
            int hp = s.F[1].HpU;
            for (int t = 1; t < LeadLife; t++) Game(s, N, N);
            Assert.AreEqual(0, OfKind(s, ChibiProjKind.VolleyArrow).Count, "TD item 4: no split before tick 27");
            Assert.AreEqual(hp, s.F[1].HpU, "R6: the lead does no damage");
            Game(s, N, N);
            Assert.AreEqual(VolleyCount, OfKind(s, ChibiProjKind.VolleyArrow).Count, "TD item 4: 18 arrows on tick 27");
            Assert.AreEqual(0, OfKind(s, ChibiProjKind.VolleyLead).Count, "TD item 4: lead gone on tick 27");
            for (int t = LeadLife + 1; t < ArrowsLock - 1; t++) Game(s, N, N);
            AssertLockEndsOn(s, 0, "R6: lock ends on tick 48");
        }

        // TD missing test -- the lead leaving the bounds splits early (CO:108, SIM:1891).
        [Test]
        public void T17_Arrows_LeadOutOfBounds_SplitsBeforeTick27()
        {
            var s = Arena(FighterClass.Ranged, RangedP0, 0, FighterClass.Melee, BloodEdge, 3000);
            // Projectile bounds are |x| > 40 world units (CO:108, ChibiSimConst.ProjBoundX), not the fighter blast line, so the
            // lead is moved next to the bound right after the press; moving +150 milli / tick it leaves on its first update.
            PressUlt(s);
            var lead = OfKind(s, ChibiProjKind.VolleyLead);
            Assert.AreEqual(1, lead.Count, "fixture: lead spawned on the press tick");
            lead[0].X = ChibiSimConst.ProjBoundX - 100;
            int split = -1;
            for (int t = 1; t <= LeadLife && split < 0; t++)
            {
                Game(s, N, N);
                if (OfKind(s, ChibiProjKind.VolleyArrow).Count > 0) split = t;
            }
            Assert.That(split > 0 && split < LeadLife, $"TD: bounds expiry splits before tick 27 (split on {split})");
            Assert.AreEqual(VolleyCount, OfKind(s, ChibiProjKind.VolleyArrow).Count, "TD: 18 arrows on a bounds split");
        }

        // Test 14 -- D-P1: same seed + inputs -> identical arrows; rollback replay from tick 20 gives the same hash.
        [Test]
        public void T18_Arrows_Rollback_SameHash()
        {
            var a = Arena(FighterClass.Ranged, RangedP0, 0, FighterClass.Melee, BloodEdge, 3000);
            var b = Clone(a);
            PressUlt(a); PressUlt(b);
            ChibiSimState snap = null;
            for (int t = 1; t <= 40; t++) { if (t == 20) snap = Clone(a); Game(a, N, N); }
            for (int t = 1; t < 20; t++) Game(b, N, N);
            for (int t = 20; t <= 40; t++) Game(b, N, In(ChibiButtons.Jump));   // wrong prediction
            b.CopyFrom(snap);
            for (int t = 20; t <= 40; t++) Game(b, N, N);
            Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum(), "test 14: resim from tick 20 equals the straight run");
            Assert.Greater(OfKind(a, ChibiProjKind.VolleyArrow).Count + a.F[1].HitsLanded + (3000 - 0), 0, "sanity");
        }

        // ================================================================== Kick

        /// <summary>Polearm F[who] at X 0 facing +1, Melee (Blood Edge) victim at (victimX, 0).</summary>
        static ChibiSimState KickArena(int victimX, int who = 0) =>
            who == 0 ? Arena(FighterClass.Polearm, PolearmP0, 0, FighterClass.Melee, BloodEdge, victimX, 1)
                     : Arena(FighterClass.Melee, BloodEdge, victimX, FighterClass.Polearm, PolearmP0, 0, -1);

        static int LerpX(int from, int to, int k) => from + (to - from) * k / KickLerpFrames;

        // Test 15 / R7 + amendment 4 -- lerp X on ticks 1..8, tick 9 Held/Holding + stun + 1.5x hit, as fighter 0 and 1.
        [TestCase(0)]
        [TestCase(1)]
        public void T19_Kick_InRange_LerpThenHoldAndFirstHitOnTick9(int who)
        {
            var s = KickArena(5000, who);
            var k = s.F[who]; var v = s.F[1 - who];
            Assert.AreEqual(1, k.Facing, "precondition: Polearm faces +1");
            PressUlt(s, who);
            int toX = 5000 - KickBehind;
            Assert.AreEqual(0, GetInt(k, "KickFromX"), "R7 KickFromX");
            Assert.AreEqual(0, GetInt(k, "KickFromY"), "R7 KickFromY");
            Assert.AreEqual(toX, GetInt(k, "KickToX"), "R7 KickToX 3700");
            Assert.AreEqual(0, GetInt(k, "KickToY"), "R7 KickToY");
            Assert.AreEqual(0, k.Vx, "R7 tick 0: V 0");
            int hits = 0;
            for (int t = 1; t <= 9; t++)
            {
                int hp = v.HpU;
                GameFor(s, who, N);
                if (v.HpU < hp) { hits++; Assert.AreEqual(9, t, "R7: first hit on tick 9"); }
                if (t <= 8) Assert.AreEqual(LerpX(0, toX, t), k.X, $"R7 lerp X on tick {t}");
                HeldInvariant(s, t);
            }
            Assert.AreEqual(1, hits, "R7: one hit by tick 9");
            Assert.IsTrue(v.Held && k.Holding, "R7 tick 9: victim Held, Polearm Holding");
            Assert.That(v.Stun >= KickHit1Stun && v.Stun <= KickStun, $"R7 tick 9: victim stun from Stun(96) (got {v.Stun})");   // TBD-verify 96 vs 95
        }

        // Test 15 (unblockable) -- the first hit lands through a shield.
        [Test]
        public void T20_Kick_FirstHitUnblockable_ThroughShield()
        {
            var s = KickArena(5000);
            var v = s.F[1];
            var shield = In(ChibiButtons.Shield);
            ChibiTestTime.ArmUltFire(s, 0);
            Game(s, N, shield);
            int hp = v.HpU;
            for (int t = 1; t <= 9; t++) Game(s, N, shield);
            Assert.Less(v.HpU, hp, "test 15: Kick hit 1 is unblockable");
        }

        // Test 16 -- F:1065: victim held in place ticks 9..49 (V 0, no gravity), its inputs ignored.
        [Test]
        public void T21_Kick_Hold_VictimFixedInputsIgnored()
        {
            var s = KickArena(5000);
            var v = s.F[1];
            PressUlt(s);
            for (int t = 1; t <= 9; t++) Game(s, N, N);
            int hx = v.X, hy = v.Y;
            for (int t = 10; t < KickReleaseFrame; t++)
            {
                Game(s, N, In(t % 2 == 0 ? ChibiButtons.Jump : ChibiButtons.Light));
                Assert.IsTrue(v.Held, $"test 16: still held on tick {t}");
                Assert.AreEqual(hx, v.X, $"test 16: victim X fixed on tick {t}");
                Assert.AreEqual(hy, v.Y, $"test 16: victim Y fixed on tick {t}");
                Assert.AreEqual(0, v.Vy, $"test 16: victim Vy 0 on tick {t}");
                Assert.AreEqual(-1, v.MoveSlot, $"test 16: victim inputs ignored on tick {t}");
                HeldInvariant(s, t);
            }
        }

        // Test 17 -- F:1071-1076: rise Vy 180000 on tick 30; tick 50 release then 2.5x hit kb (F·180000, 135000); free 114.
        [Test]
        public void T22_Kick_RiseTick30_ReleaseAndSecondHitTick50_Free114()
        {
            var s = KickArena(5000);
            var k = s.F[0]; var v = s.F[1];
            PressUlt(s);
            for (int t = 1; t < KickRiseFrame; t++) Game(s, N, N);
            Game(s, N, N);
            AssertVyWritten(KickRiseVel, k.Vy, "test 17: rise on tick 30");
            Assert.AreEqual(0, k.Vx, "test 17: rise Vx 0");
            for (int t = KickRiseFrame + 1; t < KickReleaseFrame; t++) Game(s, N, N);
            Assert.IsTrue(v.Held, "test 17: still held on tick 49");
            int hp = v.HpU;
            Game(s, N, N);
            Assert.IsFalse(v.Held, "test 17: released on tick 50");
            Assert.IsFalse(k.Holding, "test 17: not holding on tick 50");
            Assert.Less(v.HpU, hp, "test 17: second hit on tick 50");
            Assert.AreEqual(KickHit2KbX, v.Vx, "test 17: kb X F·180000");   // TBD-verify (kb scaling / friction on the hit tick)
            AssertVyWritten(KickHit2KbY, v.Vy, "test 17: kb Y 135000");
            Assert.That(v.Stun > 0, "TD item 6: victim stun kept by max");
            for (int t = KickReleaseFrame + 1; t < KickLock - 1; t++) Game(s, N, N);
            AssertLockEndsOn(s, k == s.F[0] ? 0 : 1, "test 17: lock ends on tick 114");
        }

        // Test 18 -- F:1054-1056: opponent behind -> Facing -1, to = -5000 + 1300.
        [Test]
        public void T23_Kick_OpponentBehind_FacesAndTargetsBehindIt()
        {
            var s = KickArena(-5000);
            Assert.AreEqual(1, s.F[0].Facing, "precondition: Polearm faces away");
            PressUlt(s);
            Assert.AreEqual(-1, s.F[0].Facing, "test 18: SetFacing toward the opponent");
            Assert.AreEqual(-5000 + KickBehind, GetInt(s.F[0], "KickToX"), "test 18: to = -3700");
            Game(s, N, N);
            Assert.AreEqual(LerpX(0, -5000 + KickBehind, 1), s.F[0].X, "test 18: lerp tick 1");
        }

        // Test 19 / R8 + amendment 1 -- out of range: tick 0 Vx 293750, ticks 1..14 Vx 300000, box on tick 15 (bx 0, bw 2500),
        // stun 30; free 114. Victim moved into the box before tick 15 (fixture).
        [Test]
        public void T24_Kick_OutOfRange_DashThenBoxOnTick15_Free114()
        {
            // Start at X -7000 (gap 9000, out of range): the dash (~5 u) plus the locked grounded slide (F:286-289, ~8 u) keeps
            // the Polearm on the stage for the whole 114-tick lock. From X 0 it slides off and rings out on tick 61, and
            // ring-out Interrupts the move (F:692-697) -- correct original behaviour, but it would hide the lock end.
            const int KickStartX = -7000;
            var s = Arena(FighterClass.Polearm, PolearmP0, KickStartX, FighterClass.Melee, BloodEdge, KickStartX + 9000, 1);
            var k = s.F[0]; var v = s.F[1];
            PressUlt(s);
            Assert.AreEqual(KickDashVel - MoveFriction, k.Vx, "R8 tick 0: Vx 293750 (grounded decel after the frame-0 write)");
            Assert.AreEqual(0, k.Vy, "R8 tick 0: Vy 0");
            for (int t = 1; t < KickDashFrames; t++)
            {
                Game(s, N, N);
                Assert.AreEqual(KickDashVel, k.Vx, $"R8: Vx 300000 on tick {t}");   // TBD-verify (end-of-tick read after Physics)
            }
            Assert.AreEqual(4993, k.X - KickStartX, "R8: dash distance after tick 14 (pinned by the step-14 sim run)");
            Pin(v, k.X + KickBoxBw / 2, 0);                        // fixture: victim inside the box
            int hp = v.HpU;
            Game(s, N, N);
            Assert.Less(v.HpU, hp, "R8: box hit on tick 15");
            Assert.That(v.Stun >= 29 && v.Stun <= 30, $"R8: box stun 30 (got {v.Stun})");   // TBD-verify decrement order
            Assert.IsFalse(v.Held, "R8: out-of-range branch does not hold");
            for (int t = KickDashFrames + 1; t < KickLock - 1; t++) Game(s, N, N);
            Assert.AreEqual(0, k.Y, "fixture: Polearm stayed on the stage for the whole lock");
            AssertLockEndsOn(s, k == s.F[0] ? 0 : 1, "R8: lock ends on tick 114");
        }

        // Test 20 / R9 -- strict < on both axes: dx 7999 in, 8000 out; dy 2999 in, 3000 out (victim pinned for the press tick).
        [TestCase(7999, 0, true)]
        [TestCase(8000, 0, false)]
        [TestCase(2000, 2999, true)]
        [TestCase(2000, 3000, false)]
        public void T25_Kick_RangeEdges_Strict(int dx, int dy, bool inRange)
        {
            var s = KickArena(dx);
            Pin(s.F[1], dx, dy);
            PressUlt(s);
            Assert.AreEqual(1, s.F[0].UltsUsed, "precondition: ult fired");
            if (inRange)
            {
                Assert.AreEqual(0, s.F[0].Vx, "R9: in range -> lerp branch, V 0");
                Assert.AreEqual(dx - KickBehind, GetInt(s.F[0], "KickToX"), "R9: in range -> to set");
            }
            else Assert.AreEqual(KickDashVel - MoveFriction, s.F[0].Vx, "R9: out of range -> dash branch");
        }

        // Test 21 -- F:1041: a dead opponent in range -> out-of-range branch.
        [Test]
        public void T26_Kick_DeadOpponentInRange_DashBranch()
        {
            var s = KickArena(5000);
            s.F[1].HpU = 0;
            PressUlt(s);
            Assert.AreEqual(KickDashVel - MoveFriction, s.F[0].Vx, "test 21: dead opponent -> dash branch");
            Assert.IsFalse(s.F[1].Held, "test 21: no hold");
        }

        // Test 22 / D-U4 = A -- invulnerable victim in range: held + stun 96, no damage from either hit.
        [Test]
        public void T27_Kick_InvulnerableVictim_HeldAndStunnedNoDamage()
        {
            var s = KickArena(5000);
            var v = s.F[1];
            v.Invuln = 200;
            int hp = v.HpU;
            PressUlt(s);
            for (int t = 1; t <= 9; t++) Game(s, N, N);
            Assert.IsTrue(v.Held, "D-U4 A: held despite invuln");
            Assert.That(v.Stun > KickHit1Stun, $"D-U4 A: Stun(96) applied despite invuln (got {v.Stun})");
            for (int t = 10; t <= 60; t++) Game(s, N, N);
            Assert.AreEqual(hp, v.HpU, "D-U4 A: no damage from hit 1 or hit 2");
        }

        // Test 23 + amendment 7 -- victim rings out mid-hold: Polearm free at once (MoveSlot -1, lock 0), no hit 2.
        [Test]
        public void T28_Kick_VictimRingOutMidHold_FreesPolearm()
        {
            var s = KickArena(5000);
            var k = s.F[0]; var v = s.F[1];
            PressUlt(s);
            for (int t = 1; t < 20; t++) Game(s, N, N);
            Assert.IsTrue(v.Held, "precondition: held on tick 19");
            v.X = 20000;                                            // fixture: past BlastX (TBD-verify hold does not re-pin X)
            Game(s, N, N);
            Assert.IsFalse(v.Held, "test 23: victim not held");
            Assert.IsFalse(k.Holding, "test 23: Polearm not holding");
            Assert.AreEqual(-1, k.MoveSlot, "A7: Polearm free on the ring-out tick");
            Assert.AreEqual(0, k.MoveLockEnd, "A7: lock 0");
            int hp = v.HpU;
            for (int t = 21; t <= 60; t++) Game(s, N, N);
            Assert.AreEqual(hp, v.HpU, "test 23: no hit 2 after the ring-out");
        }

        // Test 23 -- attacker rings out mid-hold: victim released, no hit 2.
        [Test]
        public void T29_Kick_AttackerRingOutMidHold_ReleasesVictim()
        {
            var s = KickArena(5000);
            var k = s.F[0]; var v = s.F[1];
            PressUlt(s);
            for (int t = 1; t < 20; t++) Game(s, N, N);
            Assert.IsTrue(v.Held, "precondition: held on tick 19");
            k.X = 20000;
            Game(s, N, N);
            Assert.IsFalse(v.Held, "test 23: victim released");
            Assert.IsFalse(k.Holding, "test 23: not holding");
            int hp = v.HpU;
            for (int t = 21; t <= 60; t++) Game(s, N, N);
            Assert.AreEqual(hp, v.HpU, "test 23: no hit 2");
        }

        // Amendment 2 -- same-tick Jump + Ult, Kick out of range: the jump's vy wins over the dash's vy 0.
        [Test]
        public void T30_KickA_JumpAndUltSameTick_VyIsJumpValue()
        {
            var j = KickArena(9000);
            Game(j, In(ChibiButtons.Jump), N);
            var s = KickArena(9000);
            PressUlt(s, 0, ChibiButtons.Jump);
            Assert.AreEqual(1, s.F[0].UltsUsed, "precondition: ult fired");
            Assert.AreEqual(j.F[0].Vy, s.F[0].Vy, "A2: Kick A keeps the jump's Vy");
            Assert.That(s.F[0].Vx > 0, "A2: the dash vx write still applies");
        }

        // Amendment 2 -- same rule for Kick B (V 0 -> jump vy).
        [Test]
        public void T31_KickB_JumpAndUltSameTick_VyIsJumpValue()
        {
            var j = KickArena(5000);
            Game(j, In(ChibiButtons.Jump), N);
            var s = KickArena(5000);
            PressUlt(s, 0, ChibiButtons.Jump);
            Assert.AreEqual(j.F[0].Vy, s.F[0].Vy, "A2: Kick B keeps the jump's Vy");
        }

        // Amendment 3 / test 24 -- nonzero XRem before the press: lerp values exact (remainders cleared); save on tick 4, 20
        // and 40, mispredict, resim -> same hash; the Kick fields are copied and hashed.
        [TestCase(4)]
        [TestCase(20)]
        [TestCase(40)]
        public void T32_Kick_RollbackMidKick_WithRemainders_SameHash(int saveAt)
        {
            var a = KickArena(5000);
            a.F[0].XRem = 450; a.F[0].YRem = 300;
            var b = Clone(a);
            PressUlt(a);
            ChibiSimState snap = null;
            for (int t = 1; t <= 60; t++)
            {
                if (t == saveAt) snap = Clone(a);
                Game(a, N, N);
                if (t <= 8) Assert.AreEqual(LerpX(0, 5000 - KickBehind, t), a.F[0].X, $"A3: lerp X on tick {t} despite XRem");
            }
            PressUlt(b);
            for (int t = 1; t < saveAt; t++) Game(b, N, N);
            Assert.AreEqual(GetInt(b.F[0], "KickToX"), GetInt(snap.F[0], "KickToX"), "section 4: KickToX copied");
            Assert.AreEqual(GetInt(b.F[0], "KickFromX"), GetInt(snap.F[0], "KickFromX"), "section 4: KickFromX copied");
            for (int t = saveAt; t < saveAt + 6; t++) Game(b, N, In(ChibiButtons.Jump));   // wrong prediction
            b.CopyFrom(snap);
            for (int t = saveAt; t <= 60; t++) Game(b, N, N);
            Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum(), $"test 24: resim from tick {saveAt} equals the straight run");
            var c = Clone(a);
            SetInt(c.F[0], "KickToX", GetInt(c.F[0], "KickToX") + 1);
            Assert.AreNotEqual(a.ComputeChecksum(), c.ComputeChecksum(), "section 4: KickToX is hashed");
        }

        // ================================================================== RulesVersion / config hash (amendment 8)

        /// <summary>Today's (v13) ChibiMatchConfig.ComputeHash, reproduced test-side with RulesVersion 13.</summary>
        static uint HashV13(in ChibiMatchConfig c)
        {
            uint h = 2166136261u;
            ChibiSimState.Mix(ref h, 13);
            ChibiSimState.Mix(ref h, unchecked((int)c.Seed));
            ChibiSimState.Mix(ref h, (int)c.Class0); ChibiSimState.Mix(ref h, (int)c.Class1);
            ChibiSimState.Mix(ref h, c.Passive0); ChibiSimState.Mix(ref h, c.Passive1); ChibiSimState.Mix(ref h, c.Stage);
            ChibiSimState.Mix(ref h, ChibiSimConst.ScaleHitstop); ChibiSimState.Mix(ref h, ChibiSimConst.ScaleCinematic);
            ChibiSimState.Mix(ref h, ChibiSimConst.HitstopNormal); ChibiSimState.Mix(ref h, ChibiSimConst.HitstopCrit);
            ChibiSimState.Mix(ref h, ChibiSimConst.HitstopParry); ChibiSimState.Mix(ref h, ChibiSimConst.HitstopReflect);
            ChibiSimState.Mix(ref h, ChibiSimConst.CinematicTicks); ChibiSimState.Mix(ref h, ChibiSimConst.MaxProjectiles);
            var pc = ChibiSimConst.ProgramConstants;
            for (int k = 0; k < pc.Length; k++) ChibiSimState.Mix(ref h, pc[k]);
            var sc = ChibiSimConst.SummonConstants;
            for (int k = 0; k < sc.Length; k++) ChibiSimState.Mix(ref h, sc[k]);
            return h;
        }

        [Test]
        public void T33_RulesVersion14()
        {
            Assert.GreaterOrEqual(ConstOf(typeof(ChibiMatchConfig), "RulesVersion"), RulesVersion14, "A8: RulesVersion at least 14");
        }

        // A8 -- v13 hash != v14 hash; the step-14 constants exist with the doc's values (names per section 4, TBD-verify).
        [Test]
        public void T34_ConfigHash_V14_DiffersFromV13_AndNewConstantsExist()
        {
            var c = new ChibiMatchConfig { Seed = Seed, Class0 = FighterClass.Polearm, Class1 = FighterClass.Heavy, Stage = SkyDojo };
            Assert.AreNotEqual(HashV13(c), c.ComputeHash(), "A8: v13 config hash must not match v14");
            var t = typeof(ChibiSimConst);
            Assert.AreEqual(SmashJumpVel, ConstOf(t, "SmashJumpVel"), "section 4 SmashJumpVel");
            Assert.AreEqual(SmashInvuln, ConstOf(t, "SmashInvuln"), "section 4 SmashInvuln");
            Assert.AreEqual(ArrowsLock, ConstOf(t, "ArrowsLock"), "section 4 ArrowsLock");
            Assert.AreEqual(LeadLife, ConstOf(t, "LeadLife"), "section 4 LeadLife");
            Assert.AreEqual(KickLock, ConstOf(t, "KickLock"), "section 4 KickLock");
            Assert.AreEqual(KickInvuln, ConstOf(t, "KickInvuln"), "section 4 KickInvuln");
            Assert.AreEqual(KickRangeX, ConstOf(t, "KickRangeX"), "section 4 KickRangeX");
            Assert.AreEqual(KickRangeY, ConstOf(t, "KickRangeY"), "section 4 KickRangeY");
            Assert.AreEqual(KickBoxBw, ConstOf(t, "KickBoxBw"), "A1 KickBoxBw 2500");
        }
    }
}
