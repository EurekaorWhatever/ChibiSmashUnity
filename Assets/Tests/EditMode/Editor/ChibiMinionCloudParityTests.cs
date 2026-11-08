// Story type: Logic (Mage minions + Thunder Cloud summons, port step 13) -- evidence: automated EditMode unit test
// -- output: Assets/Tests/EditMode/Editor/ -- gate: BLOCKING
// Source spec: docs/architecture/netcode/minion-cloud-conversion.md (RulesVersion 13): section 1 minion, section 2 cloud,
// section 3 pools/state, section 4 reference values R1-R12, section 6 test list 1-30, and the "Technical-director review"
// amendments 1-9 + "Missing tests to add" (binding). Original: CombatObjects.cs (CO:), Fighter.cs (F:).
//
// User decisions applied: D-C1 = A (ground friction 48968 sub/tick walk, 50 Hz basis; wall cling 17500), D-C2 = A (centre
// over the surface), D-C3 = A (fighters -> projectiles -> minions -> clouds, by slot), D-C4 = A (parry hurts the Mage),
// D-C5 = A (cloud position in micro = 1/1000 milli, RoundHalfAway). Pools MaxMinions 12, MaxClouds 4, DroppedSummons.
//
// BUTTONS: the doc's "skill 1 / skill 2" are Magic skill INDEX 1 / 2 (TD review: "Magic skill index 1 = minions,
// 2 = cloud"), i.e. move slots 3 / 4 = ChibiButtons.Skill2 / Skill3. ChibiButtons.Skill1 stays the Homing Fireball.
//
// COMPILES TODAY, FAILS TODAY: ChibiMinionSim, ChibiCloudSim, ChibiSimState.Minions / Clouds / DroppedSummons and the new
// ChibiSimConst members do not exist yet. They are reached ONLY through Pool / Need / GetInt / SetInt / GetBool / Const,
// which Assert.Fail("... missing") when absent. Nothing here names a v13 member at compile time.
//
// Tick convention: "tick n" = the n-th game step (ChibiTestTime.StepGame) after the press / injection step (tick 0);
// values are read at the END of that step. A bonk "on tick n" = the minion's AttackCd reads MinionCd (60) after step n.
//
// FIXTURE INJECTION (documented): physics-shape tests (walk speeds, stop distance, platform, side, underside, wall cling,
// fall despawn, overlap, strike window, parry/shield, same-tick order) write one minion / cloud straight into a pool slot
// with SpawnTick = GameTicks - 1 so it updates on the next step. Spawn, timing, life, schedule, follow, max-load, rollback
// and stand-in tests use the real input path (Magic class, skill buttons).
//
// "// TBD-verify" marks values the doc derived analytically and that no run has confirmed yet (doc section 4 and the TD
// amendments). If one of these is the only red, re-derive against the doc before changing the sim.
//
// Arena: Sky Dojo (stage 0), main stage top Y 0, |X| <= 8000, MainBottom -1200, platforms (+-4200, 3050) / (0, 5550).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Mage minion and Thunder Cloud parity (port step 13) between the deterministic 60 Hz simulation and
    /// CombatObjects.Minion / ThunderCloud. Reference values: minion-cloud-conversion.md section 4; tests: section 6 + TD review.
    /// </summary>
    public class ChibiMinionCloudParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;
        const int SettleAfterPut = 3;
        const int SkyDojo = 0;

        const int MagicHex = 0, MagicWard = 1, MeleeBase = 0, PolearmBase = 0;

        // ---- section 0 / 3.2 constants (expected values of the new ChibiSimConst members)
        const int MinionGravity = 7358, MinionArc = 736, MinionR = 220, MinionWalkSub = 52500;
        const int MinionGroundWalkSub = 48968;            // D-C1 = A, amendment 1 (50 Hz basis)        // TBD-verify
        const int MinionWallFrictionSub = 17500;          // amendment 2                                // TBD-verify
        const int MinionStopDx = 400, MinionBonkDx = 700, MinionBonkDy = 900;
        const int MinionLife = 360, MinionFirstCd = 24, MinionCd = 60;
        const int MinionHopVx = 30000, MinionHopVy = 75000, MinionKillY = -20000;
        const int MinionSpawnOx = 600, MinionSpawnStep = 500, MinionSpawnY = 975;
        const int MinionLock = 24, CloudLock = 18;
        const int MinionSkillCd = 720, CloudSkillCd = 600;
        const int MaxMinions = 12, MaxClouds = 4;

        const int CloudSpawnOy = 3675, CloudGoalOy = 3875, CloudLerpDiv = 24;
        const int CloudLife = 276, CloudFirstStrike = 48, CloudPeriod = 66, CloudBolt = 9, CloudStrikeDx = 750;
        static readonly int[] StrikeTicks = { 48, 114, 180, 246 };

        // ---- hit specs after HalfKnockback(Magic) (R11 / R12)
        const int BonkKbHalf = 15000, BonkStun = 9;
        const int CloudKbYHalf = -22500, CloudStun = 21;

        const int StageHW = 8000, MainBottom = -1200;
        const int RulesVersion13 = 13;

        static readonly string[] MinionFields =
            { "Owner", "X", "Y", "XRem", "YRem", "Vx", "Vy", "Life", "AttackCd", "Facing", "SpawnTick" };
        static readonly string[] CloudFields = { "Owner", "CX", "CY", "Life", "StrikeTicks", "BoltTicks", "SpawnTick" };

        static readonly ChibiInputPacket N = default;

        // ================================================================== reflection access to v13 members

        const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags Stat = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        static FieldInfo Need(Type t, string name)
        {
            var fi = t.GetField(name, Inst);
            if (fi == null) Assert.Fail($"field {t.Name}.{name} missing (minion-cloud-conversion.md section 3.2, RulesVersion 13)");
            return fi;
        }

        static int GetInt(object o, string name) => Convert.ToInt32(Need(o.GetType(), name).GetValue(o));
        static bool GetBool(object o, string name) => Convert.ToBoolean(Need(o.GetType(), name).GetValue(o));

        static void SetInt(object o, string name, int v)
        {
            var fi = Need(o.GetType(), name);
            fi.SetValue(o, fi.FieldType.IsEnum ? Enum.ToObject(fi.FieldType, v) : Convert.ChangeType(v, fi.FieldType));
        }

        static void SetBool(object o, string name, bool v) => Need(o.GetType(), name).SetValue(o, v);

        /// <summary>ChibiSimConst.<paramref name="name"/> (const or static readonly), Assert.Fail when missing.</summary>
        static int Const(string name)
        {
            var fi = typeof(ChibiSimConst).GetField(name, Stat);
            if (fi == null) Assert.Fail($"ChibiSimConst.{name} missing (minion-cloud-conversion.md section 3.2)");
            return Convert.ToInt32(fi.IsLiteral ? fi.GetRawConstantValue() : fi.GetValue(null));
        }

        static Array Pool(ChibiSimState s, string name)
        {
            var fi = typeof(ChibiSimState).GetField(name, Inst);
            if (fi == null) Assert.Fail($"ChibiSimState.{name} missing (section 3.2: Minions[MaxMinions] / Clouds[MaxClouds])");
            var a = fi.GetValue(s) as Array;
            if (a == null) Assert.Fail($"ChibiSimState.{name} is not an array");
            return a;
        }

        static object Minion(ChibiSimState s, int i) => Pool(s, "Minions").GetValue(i);
        static object Cloud(ChibiSimState s, int i) => Pool(s, "Clouds").GetValue(i);
        static bool Active(object o) => GetBool(o, "Active");

        static int ActiveCount(ChibiSimState s, string pool)
        {
            int n = 0;
            foreach (var o in Pool(s, pool)) if (Active(o)) n++;
            return n;
        }

        static string Dump(object o, string[] fields) =>
            (Active(o) ? "A" : "-") + ":" + string.Join(",", fields.Select(f => GetInt(o, f).ToString()));

        static long RoundHalfAway(long n, long d) => Math.Sign(n) * ((Math.Abs(n) + d / 2) / d);
        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        // ================================================================== match fixture

        static ChibiSimState NewMatch(FighterClass c0, int p0, FighterClass c1, int p1, uint seed = Seed)
        {
            var cfg = Cfg(c0, p0, c1, p1, seed);
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            for (int t = 0; t < SettleTicks; t++) ChibiSimulation.Step(s, N, N);
            return s;
        }

        static ChibiMatchConfig Cfg(FighterClass c0, int p0, FighterClass c1, int p1, uint seed = Seed) =>
            new ChibiMatchConfig { Seed = seed, Class0 = c0, Passive0 = p0, Class1 = c1, Passive1 = p1, Stage = SkyDojo };

        static void Put(ChibiFighterSim f, int x, int y, int facing)
        {
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0;
            f.Grounded = y == 0; f.AirJumps = 1; f.Facing = facing; f.Stun = 0; f.Invuln = 0;
        }

        /// <summary>Holds a fighter in place without touching Stun / Invuln / Facing / Shielding.</summary>
        static void Pin(ChibiFighterSim f, int x, int y)
        {
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = y == 0;
        }

        /// <summary>Mage F[0] at (x0, 0) facing <paramref name="facing"/>, opponent F[1] at (x1, 0); settled 3 ticks.</summary>
        static ChibiSimState Arena(int x0, int x1, int facing = 1, FighterClass c1 = FighterClass.Heavy, int p1 = 1,
                                   int p0 = MagicHex, FighterClass c0 = FighterClass.Magic)
        {
            var s = NewMatch(c0, p0, c1, p1);
            Put(s.F[0], x0, 0, facing);
            Put(s.F[1], x1, 0, -facing);
            for (int t = 0; t < SettleAfterPut; t++) ChibiSimulation.Step(s, N, N);
            return s;
        }

        static ChibiInputPacket In(ChibiButtons b, ChibiDir d = ChibiDir.Neutral) => ChibiInputPacket.Make(d, b);
        static void Game(ChibiSimState s, ChibiInputPacket a, ChibiInputPacket b) => ChibiTestTime.StepGame(s, a, b);

        static ChibiSimState Clone(ChibiSimState s)
        {
            var c = new ChibiSimState();
            c.CopyFrom(s);
            return c;
        }

        /// <summary>Tick 0: F[0] presses <paramref name="b"/>; both fighters pinned at (x0, y0) / (x1, y1) first.</summary>
        static void PressPinned(ChibiSimState s, ChibiButtons b, int x0, int x1, int y0 = 0, int y1 = 0)
        {
            Pin(s.F[0], x0, y0); Pin(s.F[1], x1, y1);
            Game(s, In(b), N);
        }

        /// <summary>Runs ticks 1..n, pinning both fighters before every step; <paramref name="after"/> sees tick t.</summary>
        static void RunPinned(ChibiSimState s, int n, int x0, int x1, Action<int> after = null, int y0 = 0, int y1 = 0,
                              Action<int> before = null, Func<int, ChibiInputPacket> in1 = null)
        {
            for (int t = 1; t <= n; t++)
            {
                Pin(s.F[0], x0, y0); Pin(s.F[1], x1, y1);
                before?.Invoke(t);
                Game(s, N, in1 != null ? in1(t) : N);
                after?.Invoke(t);
            }
        }

        /// <summary>Writes a minion into <paramref name="slot"/> as if spawned on the previous game step (every field).</summary>
        static object PutMinion(ChibiSimState s, int slot, int owner, int x, int y, int vx = 0, int vy = 0,
                                int life = 300, int attackCd = MinionCd, int facing = 1)
        {
            var m = Minion(s, slot);
            SetBool(m, "Active", true);
            SetInt(m, "Owner", owner); SetInt(m, "X", x); SetInt(m, "Y", y); SetInt(m, "XRem", 0); SetInt(m, "YRem", 0);
            SetInt(m, "Vx", vx); SetInt(m, "Vy", vy); SetInt(m, "Life", life); SetInt(m, "AttackCd", attackCd);
            SetInt(m, "Facing", facing); SetInt(m, "SpawnTick", s.GameTicks - 1);
            return m;
        }

        /// <summary>Writes a cloud into <paramref name="slot"/> at micro position (cx, cy), spawned on the previous step.</summary>
        static object PutCloud(ChibiSimState s, int slot, int owner, long cx, long cy, int strike, int life = CloudLife)
        {
            var c = Cloud(s, slot);
            SetBool(c, "Active", true);
            SetInt(c, "Owner", owner); SetInt(c, "CX", checked((int)cx)); SetInt(c, "CY", checked((int)cy));
            SetInt(c, "Life", life); SetInt(c, "StrikeTicks", strike); SetInt(c, "BoltTicks", 0);
            SetInt(c, "SpawnTick", s.GameTicks - 1);
            return c;
        }

        static int CloudX(object c) => (int)FloorDiv(GetInt(c, "CX"), 1000);
        static int CloudY(object c) => (int)FloorDiv(GetInt(c, "CY"), 1000);

        // ================================================================== 1. minion spawn

        // Test 1 -- F:888-891: 3 minions at (f.X + F(600 + 500k), f.Y + 975), slots 0, 1, 2 in k order; lock 24 ticks.
        [TestCase(1)]
        [TestCase(-1)]
        public void T01_MinionSpawn_PositionsAndLock(int facing)
        {
            var s = Arena(0, facing * 3000, facing);
            PressPinned(s, ChibiButtons.Skill2, 0, facing * 3000);
            Assert.AreEqual(3, ActiveCount(s, "Minions"), "T1: 3 minions on the press tick");
            for (int k = 0; k < 3; k++)
            {
                var m = Minion(s, k);
                Assert.IsTrue(Active(m), $"T1: slot {k} active (slot order = k order)");
                Assert.AreEqual(facing * (MinionSpawnOx + MinionSpawnStep * k), GetInt(m, "X"), $"T1: minion {k} X");
                Assert.AreEqual(MinionSpawnY, GetInt(m, "Y"), $"T1: minion {k} Y = feet + 975");
                Assert.AreEqual(0, GetInt(m, "Owner"), $"T1: minion {k} owner");
            }
            Assert.AreEqual(0, s.Proj.Count(p => p.Active), "T1/T29: no projectile from the minion skill");
            Assert.AreEqual(MinionLock, GetInt(s.F[0], "MoveLockEnd"), "T1: SummonMinions lock 0.4 s = 24 ticks");
            Assert.AreEqual(MinionSkillCd, s.F[0].Cd[1], "3.1: minion skill cd 720 (TD confirmed)");
        }

        // Amendment 7: spawn writes every field, so stale values in a reused slot cannot survive.
        [Test]
        public void A7_MinionSpawn_WritesEveryField()
        {
            var s = Arena(0, 3000);
            var m0 = Minion(s, 0);
            SetBool(m0, "Active", false);
            SetInt(m0, "XRem", 777); SetInt(m0, "YRem", 555); SetInt(m0, "Vx", 12345); SetInt(m0, "Vy", -999);
            SetInt(m0, "Facing", -1); SetInt(m0, "Owner", 1); SetInt(m0, "AttackCd", -40); SetInt(m0, "Life", 5);
            int g0 = s.GameTicks;
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            Assert.IsTrue(Active(m0), "A7: slot 0 reused");
            Assert.AreEqual(0, GetInt(m0, "XRem"), "A7: XRem"); Assert.AreEqual(0, GetInt(m0, "YRem"), "A7: YRem");
            Assert.AreEqual(0, GetInt(m0, "Vx"), "A7: Vx"); Assert.AreEqual(0, GetInt(m0, "Vy"), "A7: Vy");
            Assert.AreEqual(1, GetInt(m0, "Facing"), "A7: Facing +1 until the first update");
            Assert.AreEqual(0, GetInt(m0, "Owner"), "A7: Owner");
            Assert.AreEqual(MinionFirstCd, GetInt(m0, "AttackCd"), "A7: AttackCd 24");
            Assert.AreEqual(MinionLife, GetInt(m0, "Life"), "A7: Life 360");
            // Same convention as ChibiProjectileSim.SpawnFrame: the GameTicks value of the spawning step.  // TBD-verify
            Assert.That(GetInt(m0, "SpawnTick"), Is.EqualTo(g0).Or.EqualTo(g0 + 1), "A7: SpawnTick = spawning game step");
        }

        // Test 2 -- CO:126 + SpawnTick: no update on the spawn tick; tick 1 applies gravity once.
        [Test]
        public void T02_Minion_FirstUpdateNextStep()
        {
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            var m = Minion(s, 0);
            Assert.AreEqual(MinionSpawnY, GetInt(m, "Y"), "T2: spawn tick: not moved");
            Assert.AreEqual(0, GetInt(m, "Vy"), "T2: spawn tick: Vy 0");
            Assert.AreEqual(MinionLife, GetInt(m, "Life"), "T2: spawn tick: Life not decremented");
            RunPinned(s, 1, 0, 3000);
            Assert.AreEqual(-MinionGravity, GetInt(m, "Vy"), "T2: tick 1 Vy = -7358");
            Assert.AreEqual(MinionLife - 1, GetInt(m, "Life"), "T2: tick 1 Life 359");
            Assert.AreEqual(MinionWalkSub, GetInt(m, "Vx"), "T2: tick 1 airborne walk 52500 toward the opponent");
        }

        // Test 3 / R1 -- CO:138: k = 0 falls 755 milli, lands on tick 14 at Y 220, Vy 0; airborne after tick 13.
        [Test]
        public void T03_Minion_LandsOnTick14()
        {
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            var m = Minion(s, 0);
            RunPinned(s, 14, 0, 3000, t =>
            {
                if (t == 13)
                {
                    // Exact height = Y + YRem/900 (integer Y floors, so compare including the remainder).
                    Assert.Greater((long)GetInt(m, "Y") * 900 + GetInt(m, "YRem"), (long)MinionR * 900, "R1: still airborne after tick 13");
                    Assert.Less(GetInt(m, "Vy"), 0, "R1: falling after tick 13");
                }
            });
            Assert.AreEqual(MinionR, GetInt(m, "Y"), "R1: lands tick 14 at Y = 220");                   // TBD-verify
            Assert.AreEqual(0, GetInt(m, "Vy"), "R1: Vy 0 on landing");
            Assert.AreEqual(0, GetInt(m, "YRem"), "1.3: landing YRem 0");
        }

        // Test 4 / R2 -- CO:164: airborne walk 52500 sub/tick: 60 ticks = exactly 3500 milli (offstage, falling).
        [Test]
        public void T04_R2_AirborneWalk_3500In60Ticks()
        {
            var s = Arena(-6000, 0);
            PutMinion(s, 0, 0, 20000, 20000, attackCd: 999, life: 999);
            var m = Minion(s, 0);
            RunPinned(s, 60, -6000, 0);
            Assert.AreEqual(20000 - 3500, GetInt(m, "X"), "R2: 60 airborne ticks x 52500 sub = 3500 milli");
            Assert.AreEqual(0, GetInt(m, "XRem"), "R2: no remainder");
            Assert.AreEqual(-MinionWalkSub, GetInt(m, "Vx"), "R2: airborne Vx = -52500 (no friction)");
        }

        // Test 4 / R3 -- D-C1 = A, amendment 1: grounded walk 48968 sub/tick: 60 ticks = 3264 milli + 480 rem.
        [Test]
        public void T04_R3_GroundedWalk_Friction()
        {
            var s = Arena(-7500, 6000);
            PutMinion(s, 0, 0, -7000, MinionR, attackCd: 999, life: 999);
            var m = Minion(s, 0);
            RunPinned(s, 60, -7500, 6000);
            Assert.AreEqual(-7000 + 3264, GetInt(m, "X"), "R3 (D-C1 A): 60 grounded ticks = 3264 milli");      // TBD-verify
            Assert.AreEqual(480, GetInt(m, "XRem"), "R3 (D-C1 A): remainder 480 sub");                        // TBD-verify
            Assert.AreEqual(MinionGroundWalkSub, GetInt(m, "Vx"), "R3: stored Vx after friction = 48968");     // TBD-verify
            Assert.AreEqual(MinionR, GetInt(m, "Y"), "R3: stayed on the ground");
        }

        // Test 5 -- CO:162-164: |dx| <= 400 -> Vx 0; 401 -> walk; dx == 0 -> dir +1 (Mathf.Sign(0)).
        [TestCase(400, 0, 1)]
        [TestCase(-400, 0, -1)]
        [TestCase(401, MinionGroundWalkSub, 1)]      // grounded: walk after friction  // TBD-verify
        [TestCase(-401, -MinionGroundWalkSub, -1)]   // TBD-verify
        [TestCase(0, 0, 1)]
        public void T05_StopDistance_AndSignZero(int dx, int expectVx, int expectFacing)
        {
            const int tx = 0;
            var s = Arena(-6000, tx);
            PutMinion(s, 0, 0, tx - dx, MinionR, attackCd: 999, facing: -expectFacing);
            var m = Minion(s, 0);
            RunPinned(s, 1, -6000, tx);
            Assert.AreEqual(expectVx, GetInt(m, "Vx"), $"T5: dx {dx} -> Vx");
            Assert.AreEqual(expectFacing, GetInt(m, "Facing"), $"T5: dx {dx} -> Facing");
        }

        // ================================================================== bonk

        /// <summary>Ticks 1..last on which minion <paramref name="slot"/> bonked (AttackCd reads MinionCd at the end).</summary>
        static List<int> BonkTicks(ChibiSimState s, int slot, int last, int x0, int x1, int y1 = 0,
                                   Action<int> before = null, Action<int> after = null)
        {
            var m = Minion(s, slot);
            var ticks = new List<int>();
            RunPinned(s, last, x0, x1, t =>
            {
                if (Active(m) && GetInt(m, "AttackCd") == MinionCd) ticks.Add(t);
                after?.Invoke(t);
            }, 0, y1, before);
            return ticks;
        }

        // Test 6 / R4 -- CO:165-168: Mage X 0 facing right, opponent feet X 3000: minion k = 0 first bonks on tick 32.
        [Test]
        public void T06_R4_FirstBonkTick32()
        {
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            var b = BonkTicks(s, 0, 40, 0, 3000);
            Assert.IsNotEmpty(b, "R4: minion 0 bonked within 40 ticks");
            // 33, not the doc's analytic 32: the user-chosen landing-step friction (D-C1 A, TD amendment 3) costs 46 milli on
            // the landing tick 14 (Vx 52500 -> 11296), so dx after tick 32 is 650 < 700. With that friction off it is 32.
            Assert.AreEqual(33, b[0], "R4 (D-C1 A incl. landing friction): first bonk of minion k = 0 on tick 33");
        }

        // Test 7 / R5 -- CO:124, :167: spawned already in reach -> first bonk tick 24 (initial cd), next tick 84, none between.
        [Test]
        public void T07_R5_InitialAndRepeatCooldown()
        {
            var s = Arena(0, 1000);
            PressPinned(s, ChibiButtons.Skill2, 0, 1000);
            var b = BonkTicks(s, 0, 100, 0, 1000);
            CollectionAssert.AreEqual(new[] { 24, 84 }, b, "R5: minion 0 bonks on 24 and 84 only");
        }

        // Test 8 -- CO:165: opponent on a platform, |dy| >= 900 -> no bonk while the minion stands under it.
        [Test]
        public void T08_BonkDyLimit_OpponentOnPlatform()
        {
            var s = Arena(-6000, 4200);
            PutMinion(s, 0, 0, 4200, MinionR, attackCd: 1, life: 300);
            int hp = s.F[1].HpU;
            var b = BonkTicks(s, 0, 120, -6000, 4200, 3050);
            Assert.IsEmpty(b, "T8: dy = 3050 + 675 - 220 >= 900 -> never bonks");
            Assert.AreEqual(hp, s.F[1].HpU, "T8: opponent HP unchanged");
        }

        // Test 9 / R6 -- CO:169: bonk tick Vx = -dir 30000 and the hop; next tick Vx back to walk; lands bonk + 19.
        [Test]
        public void T09_HopBack_LandsBonkPlus19()
        {
            var s = Arena(-6000, 1000);
            var m = PutMinion(s, 0, 0, 600, MinionR, attackCd: 1);
            int landed = -1;
            RunPinned(s, 30, -6000, 1000, t =>
            {
                if (t == 1)
                {
                    Assert.AreEqual(MinionCd, GetInt(m, "AttackCd"), "R6: bonk on tick 1");
                    Assert.AreEqual(-MinionHopVx, GetInt(m, "Vx"), "T9: bonk tick Vx = -dir x 30000 (dir +1)");
                    Assert.AreEqual(MinionHopVy - MinionGravity, GetInt(m, "Vy"), "T9: bonk tick Vy = 75000 - g after Physics"); // TBD-verify
                }
                else if (t == 2)
                    Assert.AreEqual(MinionWalkSub, GetInt(m, "Vx"), "T9: next tick: dx 433 > 400 -> airborne walk 52500");
                if (landed < 0 && t > 1 && GetInt(m, "Y") == MinionR && GetInt(m, "Vy") == 0) landed = t;
            });
            Assert.AreEqual(1 + 19, landed, "R6: lands on bonk tick + 19");                                     // TBD-verify
        }

        // Test 10 / R12 -- CO:168, F:548: defenceless target: Mult 350 damage, kb (dir 15000, 15000) after halving, stun 9.
        [Test]
        public void T10_BonkHitSpec()
        {
            var s = Arena(-6000, 1000);
            PutMinion(s, 0, 0, 600, MinionR, attackCd: 1);
            int hp = s.F[1].HpU;
            Pin(s.F[0], -6000, 0); Pin(s.F[1], 1000, 0);
            Game(s, N, N);
            Assert.Less(s.F[1].HpU, hp, "R12: bonk damaged the opponent");
            Assert.AreEqual(BonkKbHalf, s.F[1].Vx, "R12: kb x = dir x 30000 / 2");                             // TBD-verify
            Assert.AreEqual(BonkKbHalf, s.F[1].Vy, "R12: kb y = 30000 / 2");                                   // TBD-verify
            Assert.AreEqual(BonkStun, s.F[1].Stun, "R12: stun 0.15 s = 9");
        }

        // Test 10 (ratio) -- Mult 350 vs the cloud's 800 on identical seeds: damage ratio 0.35 / 0.8 within the roll's rounding.
        [Test]
        public void T10_BonkDamage_Is035xRoll()
        {
            var a = Arena(-6000, 1000);
            PutMinion(a, 0, 0, 600, MinionR, attackCd: 1);
            int hpA = a.F[1].HpU;
            Pin(a.F[0], -6000, 0); Pin(a.F[1], 1000, 0);
            Game(a, N, N);
            var b = Arena(-6000, 1000);
            PutCloud(b, 0, 0, 1000 * 1000L, 5000 * 1000L, strike: 1);
            int hpB = b.F[1].HpU;
            Pin(b.F[0], -6000, 0); Pin(b.F[1], 1000, 0);
            Game(b, N, N);
            int dA = hpA - a.F[1].HpU, dB = hpB - b.F[1].HpU;
            Assert.Greater(dA, 0, "T10: bonk dealt damage"); Assert.Greater(dB, 0, "T23: strike dealt damage");
            // CA:52-54: damage after defence = raw - floor(DEF/10) HP, so add the flat part back before comparing the multipliers.
            // Defender is Heavy (DEF 30 -> 3 HP = 3 000 000 micro-HP).
            const int HeavyFlatDefenseMicro = 3 * 1000000;
            Assert.AreEqual(350.0 / 800.0, (double)(dA + HeavyFlatDefenseMicro) / (dB + HeavyFlatDefenseMicro), 0.01,
                "T10/T23: raw bonk / strike damage = 350 / 800 (same seed, same roll)");
        }

        // Test 11 -- F:547: respawn-invulnerable target: no damage, cd still 60, hop still happens.
        [Test]
        public void T11_BonkOnInvulnerableTarget()
        {
            var s = Arena(-6000, 1000);
            var m = PutMinion(s, 0, 0, 600, MinionR, attackCd: 1);
            int hp = s.F[1].HpU;
            s.F[1].Invuln = 90;
            RunPinned(s, 1, -6000, 1000);
            Assert.AreEqual(hp, s.F[1].HpU, "T11: no damage while invulnerable");
            Assert.AreEqual(MinionCd, GetInt(m, "AttackCd"), "T11: cd still 60");
            Assert.AreEqual(-MinionHopVx, GetInt(m, "Vx"), "T11: hop back still happens");
            Assert.Greater(GetInt(m, "Vy"), 0, "T11: hop up still happens");
        }

        // Test 12 -- F:550-560, D-C4 = A: Parry stance against a minion reflects onto the far-away Mage; minion stays.
        [Test]
        public void T12_ParryStanceVsMinion_PunishesMage()
        {
            var s = Arena(-6000, 1000, 1, FighterClass.Melee, MeleeBase);
            var m = PutMinion(s, 0, 0, 600, MinionR, attackCd: 1);
            int hpMage = s.F[0].HpU, hpOpp = s.F[1].HpU;
            RunPinned(s, 1, -6000, 1000, before: _ => s.F[1].ParryTicks = 30);
            Assert.AreEqual(hpOpp, s.F[1].HpU, "T12: parrying opponent takes no damage");
            Assert.Less(s.F[0].HpU, hpMage, "T12 (D-C4 A): reflect damages the Mage across the stage");
            Assert.AreEqual(ChibiSimConst.ReflectStun, s.F[0].Stun, "T12: Mage gets ReflectStun 30");          // TBD-verify
            Assert.IsTrue(Active(m), "T12: minion stays");
            Assert.AreEqual(MinionCd, GetInt(m, "AttackCd"), "T12: cd set even when parried");
        }

        // Test 13 -- F:563-571, D-C4 = A: perfect shield (raised within 11 ticks) vs a strike stuns the Mage 27, no damage.
        [Test]
        public void T13_PerfectShieldVsCloud_StunsMage()
        {
            var s = Arena(-6000, 1000);
            PutCloud(s, 0, 0, 1000 * 1000L, 5000 * 1000L, strike: 5);
            int hpOpp = s.F[1].HpU;
            var shield = In(ChibiButtons.Shield);
            RunPinned(s, 5, -6000, 1000, in1: _ => shield);
            Assert.AreEqual(hpOpp, s.F[1].HpU, "T13: perfect parry: no damage to the opponent");
            Assert.AreEqual(ChibiSimConst.PerfectParryStun, s.F[0].Stun, "T13 (D-C4 A): Mage stunned 27 by a perfect parry"); // TBD-verify
        }

        // Test 14 / R7 -- CO:123, :155: active on tick 359, inactive after tick 360.
        [Test]
        public void T14_R7_MinionLife()
        {
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            var m = Minion(s, 0);
            RunPinned(s, 360, 0, 3000, t =>
            {
                if (t == 359) Assert.IsTrue(Active(m), "R7: active after tick 359");
                if (t == 360) Assert.IsFalse(Active(m), "R7: inactive after tick 360");
            });
        }

        // Test 15 -- CO:155: despawns on the first update whose start-of-update centre is below -20 u; no x bound.
        [Test]
        public void T15_FallDespawn_OnlyByKillY()
        {
            var s = Arena(-6000, 0);
            var m = PutMinion(s, 0, 0, 20000, 0, attackCd: 999, life: 999);
            int belowAt = -1, goneAt = -1;
            RunPinned(s, 200, -6000, 0, t =>
            {
                if (goneAt >= 0) return;
                if (!Active(m)) { goneAt = t; return; }
                if (belowAt < 0 && GetInt(m, "Y") < MinionKillY) belowAt = t;
            });
            Assert.Greater(belowAt, 0, "T15: minion fell below -20000 while active");
            Assert.AreEqual(belowAt + 1, goneAt, "T15: inactive on the update after Y < -20000 (step 2 runs before Physics)");
        }

        // Test 16 -- CO:142-145: minions ignore fighters and each other (no push).
        [Test]
        public void T16_MinionsIgnoreFightersAndEachOther()
        {
            // Both fighters within 400 of the minions so every minion's walk is 0 (CO:164) whichever fighter it chases;
            // any X change can then only come from a push.
            var s = Arena(300, 0);
            var a = PutMinion(s, 0, 0, 100, MinionR, attackCd: 200);
            var b = PutMinion(s, 1, 0, 100, MinionR, attackCd: 200);
            var c = PutMinion(s, 2, 1, 100, MinionR, attackCd: 200);   // opponent's own minion overlapping too
            RunPinned(s, 20, 300, 0, t =>
            {
                foreach (var m in new[] { a, b, c })
                {
                    Assert.AreEqual(100, GetInt(m, "X"), $"T16: overlapping minion not pushed (tick {t})");
                    Assert.AreEqual(MinionR, GetInt(m, "Y"), $"T16: minion stays grounded (tick {t})");
                }
            });
        }

        // Test 17 -- Stage.cs:94, D-S2: rising through a one-way platform is not stopped; falling onto it lands at top + 220.
        [Test]
        public void T17_OneWayPlatform()
        {
            var s = Arena(-6000, 4200);
            var m = PutMinion(s, 0, 0, 4200, 2500, vy: 150000, attackCd: 999, life: 999);
            int maxY = int.MinValue, landedY = int.MinValue;
            RunPinned(s, 60, -6000, 4200, t =>
            {
                maxY = Math.Max(maxY, GetInt(m, "Y"));
                if (landedY == int.MinValue && GetInt(m, "Vy") == 0 && t > 1) landedY = GetInt(m, "Y");
            });
            Assert.Greater(maxY, 3050 + MinionR, "T17: rose through the platform from below");
            Assert.AreEqual(3050 + MinionR, landedY, "T17: landed on the platform top + 220");
        }

        // Test 18 -- D-C2: below the top, pushed out of the main stage side to X = HW + 220.
        [Test]
        public void T18_MainStageSide_PushOut()
        {
            var s = Arena(-6000, 15000);
            var m = PutMinion(s, 0, 0, StageHW + 100, -600, attackCd: 999, life: 999);
            RunPinned(s, 1, -6000, 15000, y1: 100);
            Assert.AreEqual(StageHW + MinionR, GetInt(m, "X"), "T18: pushed out to HW + 220");                  // TBD-verify
        }

        // Test 18 -- rising into the main stage underside stops at MainBottom - 220 with Vy <= 0.
        [Test]
        public void T18_MainStageUnderside_Stops()
        {
            var s = Arena(-6000, 0);
            var m = PutMinion(s, 0, 0, 0, -1440, vy: MinionHopVy, attackCd: 999, life: 999);
            RunPinned(s, 1, -6000, 0);
            Assert.AreEqual(MainBottom - MinionR, GetInt(m, "Y"), "T18: underside: Y = MainBottom - 220");
            Assert.LessOrEqual(GetInt(m, "Vy"), 0, "T18: underside: Vy = min(Vy, 0)");
        }

        // TD amendment 2 -- wall cling (D-C1 = A): walking into the main stage side below the top, Vy is held at 0.
        [Test]
        public void A2_WallCling_StopsSliding()
        {
            var s = Arena(-6000, 0);
            var m = PutMinion(s, 0, 0, StageHW + MinionR, -500, attackCd: 999, life: 999);
            RunPinned(s, 30, -6000, 0, t =>
            {
                Assert.AreEqual(StageHW + MinionR, GetInt(m, "X"), $"A2: held against the wall (tick {t})");
                Assert.AreEqual(-500, GetInt(m, "Y"), $"A2: wall cling: no sliding (towards0(Vy - g, 17500)) (tick {t})"); // TBD-verify
                Assert.AreEqual(0, GetInt(m, "Vy"), $"A2: Vy 0 while clinging (tick {t})");                               // TBD-verify
            });
        }

        // TD missing test -- a minion follows the opponent off the ledge, then the opponent returns onstage: the minion
        // turns, walks into the side wall below the top and clings there (it cannot climb back).
        [Test]
        public void A2_WalkOffLedge_ThenOpponentReturns_Clings()
        {
            var s = Arena(-6000, 11000);
            var m = PutMinion(s, 0, 0, StageHW - 100, MinionR, attackCd: 999, life: 999);
            int x1 = 11000, y1 = 100, clingY = int.MinValue, turnedAt = -1;
            for (int t = 1; t <= 120; t++)
            {
                Pin(s.F[0], -6000, 0); Pin(s.F[1], x1, y1);
                Game(s, N, N);
                Assert.IsTrue(Active(m), $"A2 ledge: minion alive (tick {t})");
                if (turnedAt < 0 && GetInt(m, "X") > StageHW + MinionR && GetInt(m, "Y") < 0)
                { x1 = 0; y1 = 0; turnedAt = t; }
                if (turnedAt > 0 && t >= turnedAt + 20)
                {
                    Assert.AreEqual(StageHW + MinionR, GetInt(m, "X"), $"A2 ledge: clinging at HW + 220 (tick {t})");  // TBD-verify
                    if (clingY == int.MinValue) clingY = GetInt(m, "Y");
                    Assert.AreEqual(clingY, GetInt(m, "Y"), $"A2 ledge: not sliding (tick {t})");                    // TBD-verify
                }
            }
            Assert.Greater(turnedAt, 0, "A2 ledge: the minion walked off the ledge");
            Assert.Greater(clingY, MainBottom - MinionR, "A2 ledge: clings above the underside, below the top");
            Assert.Less(clingY, MinionR, "A2 ledge: clings below the stage top");
        }

        // TD missing test / D-C3 = A -- two minions bonk on the same tick: slot order decides. Both fighters at 1 micro-HP:
        // the lower slot's hit KOs its target first, the second hit's attacker is then KO'd (ApplyHit returns), so the
        // owner of the lower slot wins.
        [TestCase(0)]
        [TestCase(1)]
        public void TD_TwoMinionsSameTick_SlotOrderDecides(int lowSlotOwner)
        {
            var s = Arena(-3000, 3000, 1, FighterClass.Magic, MagicHex);
            s.F[0].HpU = 1; s.F[1].HpU = 1;
            int victimLow = 1 - lowSlotOwner;
            PutMinion(s, 0, lowSlotOwner, s.F[victimLow].X + 300, MinionR, attackCd: 1);
            PutMinion(s, 1, victimLow, s.F[lowSlotOwner].X + 300, MinionR, attackCd: 1);
            Pin(s.F[0], -3000, 0); Pin(s.F[1], 3000, 0);
            Game(s, N, N);
            Assert.IsTrue(s.MatchOver, "D-C3: a KO ended the match");
            Assert.AreEqual(lowSlotOwner, s.Winner, "D-C3 A: slot 0's bonk lands first; its owner wins");
        }

        // TD missing test -- owner rings out mid-life: minions and cloud keep acting, attacker is still the owner.
        [Test]
        public void TD_OwnerRingOutMidLife_SummonsKeepActing()
        {
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            RunPinned(s, 25, 0, 3000);
            Pin(s.F[0], 0, 0); Pin(s.F[1], 3000, 0);
            Game(s, In(ChibiButtons.Skill3), N);                 // cloud on tick 26 (minion lock 24 is over)
            Assert.AreEqual(1, ActiveCount(s, "Clouds"), "TD ring-out: cloud cast");
            s.F[0].X = BlastX() + 1000;                           // owner leaves the blast zone
            Game(s, N, N);
            int hits0 = s.F[0].HitsLanded, hp1 = s.F[1].HpU;
            for (int t = 0; t < 120; t++)
            {
                Pin(s.F[1], 3000, 0);
                Game(s, N, N);
            }
            Assert.AreEqual(3, ActiveCount(s, "Minions"), "TD ring-out: minions live on after the owner's ring-out");
            for (int k = 0; k < 3; k++) Assert.AreEqual(0, GetInt(Minion(s, k), "Owner"), "TD ring-out: still owned by F[0]");
            Assert.AreEqual(0, GetInt(Cloud(s, 0), "Owner"), "TD ring-out: cloud still owned by F[0]");
            Assert.Less(s.F[1].HpU, hp1, "TD ring-out: summons still damage the opponent");
            Assert.Greater(s.F[0].HitsLanded, hits0, "TD ring-out: hits credited to the owner");               // TBD-verify
        }

        static int BlastX() => ChibiStageTable.Get(SkyDojo).BlastX;

        // ================================================================== 2. cloud

        // Test 19 -- F:896-898, CO:188: one cloud at (f.X, f.Y + 3675), Life 276, first strike 48; lock 18; cd 600.
        [Test]
        public void T19_CloudSpawn()
        {
            var s = Arena(500, 3000);
            PressPinned(s, ChibiButtons.Skill3, 500, 3000);
            Assert.AreEqual(1, ActiveCount(s, "Clouds"), "T19: one cloud");
            var c = Cloud(s, 0);
            Assert.AreEqual(500 * 1000, GetInt(c, "CX"), "T19: CX = f.X in micro");
            Assert.AreEqual(CloudSpawnOy * 1000, GetInt(c, "CY"), "T19: CY = (f.Y + 3675) in micro");
            Assert.AreEqual(CloudLife, GetInt(c, "Life"), "T19: Life 276");
            Assert.AreEqual(CloudFirstStrike, GetInt(c, "StrikeTicks"), "T19: first strike 48");
            Assert.AreEqual(0, GetInt(c, "BoltTicks"), "T19: BoltTicks 0");
            Assert.AreEqual(0, GetInt(c, "Owner"), "T19: owner");
            Assert.AreEqual(CloudLock, GetInt(s.F[0], "MoveLockEnd"), "T19: SummonCloud lock 0.3 s = 18");
            Assert.AreEqual(CloudSkillCd, s.F[0].Cd[2], "3.1: cloud cd 600 (TD confirmed)");
            Assert.AreEqual(0, s.Proj.Count(p => p.Active), "2.1: the cloud is not a projectile");
        }

        // Test 20 / R8 / R9 -- CO:206-207, D-C5 = A: exact integers of the 2.2 algorithm; within +-1 milli of the float lerp.
        [Test]
        public void T20_R8R9_CloudFollow()
        {
            const int tx = 3000;
            var s = Arena(0, tx);
            PressPinned(s, ChibiButtons.Skill3, 0, tx);
            var c = Cloud(s, 0);
            long cx = 0, cy = CloudSpawnOy * 1000L;
            long gx = tx * 1000L, gy = CloudGoalOy * 1000L;
            RunPinned(s, 60, 0, tx, n =>
            {
                cx += RoundHalfAway(gx - cx, CloudLerpDiv);
                cy += RoundHalfAway(gy - cy, CloudLerpDiv);
                Assert.AreEqual(cx, GetInt(c, "CX"), $"R8: CX tick {n} (2.2 algorithm)");
                Assert.AreEqual(cy, GetInt(c, "CY"), $"R9: CY tick {n} (2.2 algorithm)");
                double fx = tx * (1 - Math.Pow(23.0 / 24.0, n));
                double fy = CloudGoalOy - 200 * Math.Pow(23.0 / 24.0, n);
                Assert.AreEqual(fx, CloudX(c), 1.0, $"R8: X tick {n} within 1 milli of 3000(1 - (23/24)^n)");
                Assert.AreEqual(fy, CloudY(c), 1.0, $"R9: Y tick {n} within 1 milli of 3875 - 200(23/24)^n");
            });
            Assert.That(CloudX(c), Is.InRange(2766, 2767), "R8: n = 60 -> 2766 or 2767");
        }

        // Test 21 / R10 -- CO:178-179, :211-216: strikes on 48, 114, 180, 246 only (BoltTicks = 9); inactive after 276.
        [Test]
        public void T21_R10_StrikeSchedule()
        {
            var s = Arena(0, 500);
            PressPinned(s, ChibiButtons.Skill3, 0, 500);
            var c = Cloud(s, 0);
            var strikes = new List<int>();
            RunPinned(s, 280, 0, 500, t =>
            {
                if (t == 275) Assert.IsTrue(Active(c), "R10: active after tick 275");
                if (t == 276) Assert.IsFalse(Active(c), "R10: inactive after tick 276 (no 5th strike)");
                if (Active(c) && GetInt(c, "BoltTicks") == CloudBolt) strikes.Add(t);
            });
            CollectionAssert.AreEqual(StrikeTicks, strikes, "R10: strike ticks");
        }

        // Test 21 (hits) -- the opponent within the window loses HP on exactly the strike ticks.
        [Test]
        public void T21_StrikeHitsOnScheduleOnly()
        {
            var s = Arena(0, 500);
            PressPinned(s, ChibiButtons.Skill3, 0, 500);
            var hits = new List<int>();
            int before = 0;
            RunPinned(s, 280, 0, 500, t => { if (s.F[1].HpU < before) hits.Add(t); },
                      before: t => before = s.F[1].HpU);
            CollectionAssert.AreEqual(StrikeTicks, hits, "R10: hits only on 48, 114, 180, 246");
        }

        /// <summary>A micro offset e so that one follow step toward a goal at 0 leaves FloorDiv(e', 1000) == target milli.</summary>
        static long OffsetForMilliAfterStep(int targetMilli)
        {
            long guess = (long)targetMilli * 1000 * 24 / 23;
            for (long d = 0; d < 4000; d++)
                foreach (long e in new[] { guess + d, guess - d })
                {
                    long after = e - RoundHalfAway(e, CloudLerpDiv);
                    if (FloorDiv(after, 1000) == targetMilli) return e;
                }
            Assert.Fail("fixture: no offset found");
            return 0;
        }

        // Test 22 -- CO:217: |T.X - X| < 750 after this tick's follow; x only (opponent high above is still hit).
        [TestCase(749, 0, true)]
        [TestCase(750, 0, false)]
        [TestCase(-749, 0, true)]
        [TestCase(-750, 0, false)]
        [TestCase(749, 5550, true)]       // opponent on the top platform, cloud far below: no y condition
        public void T22_StrikeXWindow(int dxMilli, int oppY, bool hit)
        {
            var s = Arena(-6000, 0);
            long e = OffsetForMilliAfterStep(dxMilli);
            long cy = oppY == 0 ? 3875_000L : -500_000L;
            var c = PutCloud(s, 0, 0, e, cy, strike: 1);
            int hp1 = s.F[1].HpU;
            RunPinned(s, 1, -6000, 0, y1: oppY);
            Assert.AreEqual(dxMilli, CloudX(c), "fixture: cloud X after the follow");
            Assert.AreEqual(CloudBolt, GetInt(c, "BoltTicks"), "T22: strike check ran");
            Assert.AreEqual(hit, s.F[1].HpU < hp1, $"T22: |dx| = {Math.Abs(dxMilli)} -> hit = {hit}");
        }

        // Test 23 / R11 -- CO:218: Mult 800, KbY -45000 halved -> -22500, KbX 0, stun 21.
        [Test]
        public void T23_R11_StrikeHitSpec()
        {
            var s = Arena(-6000, 1000);
            PutCloud(s, 0, 0, 1000 * 1000L, 5000 * 1000L, strike: 1);
            int hp1 = s.F[1].HpU;
            RunPinned(s, 1, -6000, 1000);
            Assert.Less(s.F[1].HpU, hp1, "R11: strike damaged the opponent");
            Assert.AreEqual(0, s.F[1].Vx, "R11: kb x 0");                                                     // TBD-verify
            Assert.AreEqual(CloudKbYHalf, s.F[1].Vy, "R11: kb y -45000 / 2 = -22500");                          // TBD-verify
            Assert.AreEqual(CloudStun, s.F[1].Stun, "R11: stun 0.35 s = 21");
        }

        // Test 23 -- blockable: shield held past the parry window -> chip + guard damage, Mage not stunned.
        [Test]
        public void T23_StrikeBlocked_ChipAndGuard()
        {
            var s = Arena(-6000, 1000);
            PutCloud(s, 0, 0, 1000 * 1000L, 5000 * 1000L, strike: 40);
            var shield = In(ChibiButtons.Shield);
            int hp1 = 0, guard = 0;
            RunPinned(s, 40, -6000, 1000, in1: _ => shield, before: t => { if (t == 40) { hp1 = s.F[1].HpU; guard = s.F[1].GuardU; } });
            Assert.IsTrue(s.F[1].Shielding, "fixture: opponent shielding");
            Assert.Less(s.F[1].GuardU, guard, "T23: blocked strike drains guard");
            Assert.Less(s.F[1].HpU, hp1, "T23: blocked strike deals chip damage");
            Assert.AreEqual(0, s.F[0].Stun, "T23: no parry (outside the 11-tick window)");
        }

        // Test 24 -- CO:175-220, F:521: Spin Guard / Fire Ring on the opponent destroy projectiles, not summons.
        [Test]
        public void T24_NotAProjectile_SpinGuardAndRingIgnoreSummons()
        {
            var s = Arena(0, 1500, 1, FighterClass.Polearm, PolearmBase);
            PressPinned(s, ChibiButtons.Skill2, 0, 1500);
            RunPinned(s, 25, 0, 1500, before: _ => { s.F[1].GuardTicks = 50; s.F[1].RingTicks = 50; });
            Pin(s.F[0], 0, 0); Pin(s.F[1], 1500, 0);
            Game(s, In(ChibiButtons.Skill3), N);
            RunPinned(s, 30, 0, 1500, before: _ => { s.F[1].GuardTicks = 50; s.F[1].RingTicks = 50; });
            Assert.AreEqual(3, ActiveCount(s, "Minions"), "T24: minions not destroyed by Spin Guard / Fire Ring");
            Assert.AreEqual(1, ActiveCount(s, "Clouds"), "T24: cloud not destroyed");
            Assert.AreEqual(0, s.Proj.Count(p => p.Active), "T24: projectile pool untouched");
        }

        // Test 25 -- section 3.1: both Mages cast whenever possible for 1800 ticks: <= 6 minions, <= 2 clouds, no drops.
        [Test]
        public void T25_BothMagesMaxLoad_NoDrops()
        {
            var s = Arena(-2000, 2000, 1, FighterClass.Magic, MagicHex);
            var press = In(ChibiButtons.Skill2 | ChibiButtons.Skill3);
            int maxM = 0, maxC = 0;
            for (int t = 0; t < 1800 && !s.MatchOver; t++)
            {
                var p = (t & 1) == 0 ? press : N;
                Game(s, p, p);
                maxM = Math.Max(maxM, ActiveCount(s, "Minions"));
                maxC = Math.Max(maxC, ActiveCount(s, "Clouds"));
                Assert.LessOrEqual(ActiveCount(s, "Minions"), 6, $"3.1: at most 6 minions (tick {t})");
                Assert.LessOrEqual(ActiveCount(s, "Clouds"), 2, $"3.1: at most 2 clouds (tick {t})");
            }
            Assert.AreEqual(6, maxM, "3.1: both Mages reach 6 minions");
            Assert.AreEqual(2, maxC, "3.1: both Mages reach 2 clouds");
            Assert.AreEqual(0, GetInt(s, "DroppedSummons"), "3.1: DroppedSummons == 0");
        }

        // ================================================================== 3. state, rollback, hashing

        // Test 26 -- snapshot mid-life (minions airborne + cloud live), run 120, restore, rerun: identical checksums.
        [Test]
        public void T26_Rollback_IdenticalChecksums()
        {
            var s = Arena(0, 3000, 1, FighterClass.Magic, MagicHex);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            RunPinned(s, 25, 0, 3000);
            Game(s, In(ChibiButtons.Skill3), In(ChibiButtons.Skill2));
            RunPinned(s, 5, 0, 3000);
            Assert.Greater(ActiveCount(s, "Minions"), 0, "fixture: minions alive at the snapshot");
            Assert.Greater(ActiveCount(s, "Clouds"), 0, "fixture: cloud alive at the snapshot");
            var snap = Clone(s);
            var sums = new List<uint>();
            RunScript(s, 120, sums);
            s.CopyFrom(snap);
            var again = new List<uint>();
            RunScript(s, 120, again);
            CollectionAssert.AreEqual(sums, again, "T26: same inputs after restore -> identical checksums every tick");
        }

        /// <summary>Deterministic script: no pins, scripted presses by tick index.</summary>
        static void RunScript(ChibiSimState s, int n, List<uint> sums)
        {
            for (int t = 0; t < n; t++)
            {
                var a = t % 30 == 3 ? In(ChibiButtons.Light) : N;
                var b = t % 40 == 7 ? In(ChibiButtons.Jump) : N;
                Game(s, a, b);
                sums.Add(s.ComputeChecksum());
            }
        }

        // TD missing test -- rollback across a slot reuse: despawn, respawn into the same slot, restore earlier.
        [Test]
        public void TD_RollbackAcrossSlotReuse()
        {
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            int firstSpawn = GetInt(Minion(s, 0), "SpawnTick");
            RunPinned(s, 100, 0, 3000);
            var snap = Clone(s);
            var sums = new List<uint>();
            bool sawFree = false;
            void Script(List<uint> into, bool check)
            {
                for (int t = 101; t <= 760; t++)
                {
                    Pin(s.F[0], 0, 0); Pin(s.F[1], 3000, 0);
                    Game(s, t == MinionSkillCd + 10 ? In(ChibiButtons.Skill2) : N, N);
                    into.Add(s.ComputeChecksum());
                    if (check && !Active(Minion(s, 0))) sawFree = true;
                }
            }
            Script(sums, true);
            Assert.IsTrue(sawFree, "fixture: slot 0 was freed (life 360)");
            Assert.IsTrue(Active(Minion(s, 0)), "fixture: slot 0 reused by the second cast");
            Assert.AreNotEqual(firstSpawn, GetInt(Minion(s, 0), "SpawnTick"), "fixture: new life in slot 0");
            s.CopyFrom(snap);
            var again = new List<uint>();
            Script(again, false);
            CollectionAssert.AreEqual(sums, again, "TD: checksums identical across the slot reuse after restore");
        }

        // Test 26 (b) / section 3.2 -- every active-slot field is hashed; inactive slots are not; CopyFrom copies every field.
        [Test]
        public void T26b_HashCoversActiveFields_CopyFromCopiesAll()
        {
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            RunPinned(s, 25, 0, 3000);
            Pin(s.F[0], 0, 0); Pin(s.F[1], 3000, 0);
            Game(s, In(ChibiButtons.Skill3), N);
            uint h = s.ComputeChecksum();
            foreach (var f in MinionFields)
            {
                var c = Clone(s);
                var m = Minion(c, 0);
                SetInt(m, f, GetInt(m, f) + 1);
                Assert.AreNotEqual(h, c.ComputeChecksum(), $"3.2: minion field {f} hashed");
            }
            foreach (var f in CloudFields)
            {
                var c = Clone(s);
                var cl = Cloud(c, 0);
                SetInt(cl, f, GetInt(cl, f) + 1);
                Assert.AreNotEqual(h, c.ComputeChecksum(), $"3.2: cloud field {f} hashed");
            }
            {
                var c = Clone(s);
                SetInt(c, "DroppedSummons", GetInt(c, "DroppedSummons") + 1);
                Assert.AreNotEqual(h, c.ComputeChecksum(), "3.2: DroppedSummons hashed");
            }
            {
                var c = Clone(s);
                SetBool(Minion(c, 0), "Active", false);
                Assert.AreNotEqual(h, c.ComputeChecksum(), "3.2: minion Active hashed");
                var idle = Minion(c, MaxMinions - 1);
                uint h2 = c.ComputeChecksum();
                SetInt(idle, "X", GetInt(idle, "X") + 5);
                Assert.AreEqual(h2, c.ComputeChecksum(), "3.2: inactive slot fields not hashed (same as projectiles)");
            }
            var copy = Clone(s);
            for (int i = 0; i < MaxMinions; i++)
                Assert.AreEqual(Dump(Minion(s, i), MinionFields), Dump(Minion(copy, i), MinionFields), $"3.2: CopyFrom minion {i}");
            for (int i = 0; i < MaxClouds; i++)
                Assert.AreEqual(Dump(Cloud(s, i), CloudFields), Dump(Cloud(copy, i), CloudFields), $"3.2: CopyFrom cloud {i}");
            Assert.AreEqual(GetInt(s, "DroppedSummons"), GetInt(copy, "DroppedSummons"), "3.2: CopyFrom DroppedSummons");
        }

        // Test 27 -- step 10: during hitstop the summons change only on game steps.
        [Test]
        public void T27_HitstopSlowsSummons()
        {
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            Game(s, N, N);
            Pin(s.F[0], 0, 0); Pin(s.F[1], 3000, 0);
            Game(s, In(ChibiButtons.Skill3), N);   // ignored by the lock, harmless
            s.HitstopRealTicks = 8;
            int frozen = 0;
            for (int n = 0; n < 40; n++)
            {
                string m0 = Dump(Minion(s, 0), MinionFields);
                int g = s.GameTicks;
                ChibiSimulation.Step(s, N, N);
                if (s.GameTicks == g)
                {
                    frozen++;
                    Assert.AreEqual(m0, Dump(Minion(s, 0), MinionFields), $"T27: minion unchanged on a frozen real tick ({n})");
                }
                else Assert.AreNotEqual(m0, Dump(Minion(s, 0), MinionFields), "T27: minion updated on the game step");
            }
            Assert.Greater(frozen, 0, "fixture: hitstop produced frozen real ticks");
        }

        // Test 28 -- F:547: after MatchOver no minion / cloud field changes.
        [Test]
        public void T28_MatchEndFreeze()
        {
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            RunPinned(s, 25, 0, 3000);
            Game(s, In(ChibiButtons.Skill3), N);
            s.MatchOver = true; s.Winner = 0;
            string before = string.Join("|", Enumerable.Range(0, MaxMinions).Select(i => Dump(Minion(s, i), MinionFields)))
                          + string.Join("|", Enumerable.Range(0, MaxClouds).Select(i => Dump(Cloud(s, i), CloudFields)));
            for (int t = 0; t < 60; t++) ChibiSimulation.Step(s, N, N);
            string after = string.Join("|", Enumerable.Range(0, MaxMinions).Select(i => Dump(Minion(s, i), MinionFields)))
                         + string.Join("|", Enumerable.Range(0, MaxClouds).Select(i => Dump(Cloud(s, i), CloudFields)));
            Assert.AreEqual(before, after, "T28: frozen after MatchOver");
        }

        // TD missing test -- Init / rematch clears every slot and DroppedSummons.
        [Test]
        public void TD_InitClearsSlotsAndDroppedSummons()
        {
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            RunPinned(s, 25, 0, 3000);
            Game(s, In(ChibiButtons.Skill3), N);
            SetInt(s, "DroppedSummons", 3);
            Assert.Greater(ActiveCount(s, "Minions") + ActiveCount(s, "Clouds"), 0, "fixture: summons alive before Init");
            ChibiSimulation.Init(s, Cfg(FighterClass.Magic, MagicHex, FighterClass.Heavy, 1));
            Assert.AreEqual(0, ActiveCount(s, "Minions"), "3.2: Init clears all minion slots");
            Assert.AreEqual(0, ActiveCount(s, "Clouds"), "3.2: Init clears all cloud slots");
            Assert.AreEqual(0, GetInt(s, "DroppedSummons"), "3.2: Init clears DroppedSummons");
        }

        // Section 3.2 -- pool sizes and the v13 constants.
        [Test]
        public void S32_PoolsAndConstants()
        {
            var s = new ChibiSimState();
            Assert.AreEqual(MaxMinions, Const("MaxMinions"), "3.1: MaxMinions 12");
            Assert.AreEqual(MaxClouds, Const("MaxClouds"), "3.1: MaxClouds 4");
            Assert.AreEqual(MaxMinions, Pool(s, "Minions").Length, "3.2: Minions[MaxMinions]");
            Assert.AreEqual(MaxClouds, Pool(s, "Clouds").Length, "3.2: Clouds[MaxClouds]");
            foreach (var o in Pool(s, "Minions")) Assert.IsNotNull(o, "3.2: minion slots constructed");
            foreach (var o in Pool(s, "Clouds")) Assert.IsNotNull(o, "3.2: cloud slots constructed");
            Assert.AreEqual(MinionGravity, Const("MinionGravity")); Assert.AreEqual(MinionArc, Const("MinionArc"));
            Assert.AreEqual(MinionR, Const("MinionR")); Assert.AreEqual(MinionWalkSub, Const("MinionWalkSub"));
            Assert.AreEqual(MinionWalkSub - MinionGroundWalkSub, Const("MinionFrictionSub"), "D-C1 A: friction 3532 sub/tick"); // TBD-verify
            Assert.AreEqual(MinionWallFrictionSub, Const("MinionWallFrictionSub"), "A2: wall friction 17500");                // TBD-verify
            Assert.AreEqual(MinionLife, Const("MinionLife")); Assert.AreEqual(MinionCd, Const("MinionCd"));
            Assert.AreEqual(MinionFirstCd, Const("MinionFirstCd")); Assert.AreEqual(MinionKillY, Const("MinionKillY"));
            Assert.AreEqual(CloudLife, Const("CloudLife")); Assert.AreEqual(CloudPeriod, Const("CloudPeriod"));
            Assert.AreEqual(CloudFirstStrike, Const("CloudFirstStrike")); Assert.AreEqual(CloudStrikeDx, Const("CloudStrikeDx"));
            Assert.AreEqual(CloudLerpDiv, Const("CloudLerpDiv")); Assert.AreEqual(CloudBolt, Const("CloudBolt"));
        }

        // Test 30 / section 3.1 -- RulesVersion 13 (pool sizes are consts: their presence in ComputeHash is a review item).
        [Test]
        public void T30_RulesVersion13()
        {
            Assert.GreaterOrEqual(ChibiMatchConfig.RulesVersion, RulesVersion13, "3.2: RulesVersion at least 13");
            var a = Cfg(FighterClass.Magic, 0, FighterClass.Magic, 1);
            Assert.AreEqual(a.ComputeHash(), Cfg(FighterClass.Magic, 0, FighterClass.Magic, 1).ComputeHash(), "config hash stable");
        }

        // Test 29 / section 3.3 -- SeekerOrb stand-in removed; Magic minion skill spawns no projectile.
        [Test]
        public void T29_SeekerOrbStandInRemoved()
        {
            CollectionAssert.DoesNotContain(Enum.GetNames(typeof(ChibiProjKind)), "SeekerOrb", "3.3: ChibiProjKind.SeekerOrb deleted");
            var s = Arena(0, 3000);
            PressPinned(s, ChibiButtons.Skill2, 0, 3000);
            int maxProj = 0;
            RunPinned(s, 40, 0, 3000, _ => maxProj = Math.Max(maxProj, s.Proj.Count(p => p.Active)));
            Assert.AreEqual(0, maxProj, "3.3: Magic Skill2 spawns no projectile");
            Assert.AreEqual(0, s.DroppedShots, "3.3: no shot drops");
        }

        // Section 3.3 -- the thunder strike stand-in (melee row) is gone: Skill3 deals no hit before the cloud's tick 48.
        [Test]
        public void S33_ThunderStandInRemoved_NoEarlyHit()
        {
            var s = Arena(0, 600);
            PressPinned(s, ChibiButtons.Skill3, 0, 600);
            int hp1 = s.F[1].HpU;
            RunPinned(s, 47, 0, 600);
            Assert.AreEqual(hp1, s.F[1].HpU, "3.3: no hit before the first cloud strike (old stand-in hit on tick 40)");
            Assert.AreEqual(1, ActiveCount(s, "Clouds"), "3.3: cloud present");
        }
    }
}
