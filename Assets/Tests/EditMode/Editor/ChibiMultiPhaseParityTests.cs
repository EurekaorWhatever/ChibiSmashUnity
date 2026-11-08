// Story type: Logic (multi-phase skill programs, held state, dispatch priority) -- evidence: automated EditMode unit test
// -- gate: BLOCKING
// Source spec: docs/architecture/netcode/multiphase-conversion.md (port step 12, RulesVersion 12): section 1 timing contract,
// section 2 per-move tables, section 3 interrupt semantics, section 4 priority, section 6 contract, section 7 reference
// values R1-R13, section 8 test list 1-32, and the "Technical-director review" amendments 1-9 (binding; 8a-8i added here).
// Original: Fighter/Fighter.cs (F:).
//
// User decisions applied: D-M1 = A (Light > Heavy > skills; ult stand-in checked first only when free and meter full),
// D-M2 = B (Leap Slam leaps on tick 1, slams on the first tick >= 10 whose start-of-tick Grounded is true, timeout 78),
// D-M3 = B (both same-tick grabs whiff, 21-tick lock).
//
// COMPILES TODAY, FAILS TODAY: the v12 members (ChibiFighterSim.MoveLockEnd / MovePhase / Held / Holding / GrabSX..GrabLY)
// do not exist yet. They are reached ONLY through Need / GetInt / SetInt / GetBool / SetBool below, which Assert.Fail
// ("... missing") when absent. Nothing here names a v12 member at compile time.
//
// Tick convention: "tick f" = the f-th game step (ChibiTestTime.StepGame) counted from the press step (f = 0); values are
// read at the END of that step. A hit "on tick f" = the victim's HpU dropped during step f.
//
// FIXTURE INJECTION (documented): interrupts of a running move (tests 2, 10, 26) and aura probes (25, 26) write a
// projectile straight into a pool slot (Shot) with SpawnFrame = GameTicks - 1, so it updates on the next step. Pool-full
// (21) fills all 64 slots with harmless shots. Everything else goes through the real input path.
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
    /// Multi-phase move parity (port step 12) between the deterministic 60 Hz simulation and Fighter.cs skill coroutines.
    /// Reference values: multiphase-conversion.md section 7; test list: section 8 + technical-director amendment 8.
    /// </summary>
    public class ChibiMultiPhaseParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;
        const int SettleAfterPut = 3;
        const int SkyDojo = 0;

        // ---- passives (ChibiCombatStats.Create)
        const int SwiftBlade = 0, BloodEdge = 1;           // Melee: +15 % attack speed / life steal
        const int HeavyP1 = 1;                             // Heavy: -15 % move speed (no attack-speed change)
        const int RangedP0 = 0;                            // +20 % crit (no attack-speed change)
        const int MagicWard = 1;                           // Ward 20 %
        const int PolearmP0 = 0;                           // Armor Break (no Patience)
        const int WardPermille = 200;

        // ---- section 0 / SIM
        const int CenterY = 675;
        const int BlastX = 17000;
        const int RespawnX = 0, RespawnY = 7775;
        const int UltMax = 1000;
        const int HexTicks = 200;                          // SluggishTicks kept > 0 for the whole move

        // ---- section 1 / 2 lock ends (MoveLockEnd)
        const int LockThrust = 48, LockGrabWhiff = 21, LockGrabHit = 66, LockTornado = 65, LockBackstep = 24,
                  LockBarrage = 36, LockSpinGuard = 72, LeapLockAfterSlam = 12;

        // ---- 2.1 Thrust Barrage
        static readonly int[] ThrustHits = { 0, 7, 14, 21, 28, 35 };
        const int ThrustKbX = 12000, ThrustKbY = 7500, ThrustLastKbX = 90000, ThrustLastKbY = 60000, ThrustStun = 15;

        // ---- 2.2 Grab
        const int GrabStun = 72, GrabSlamKbX = 60000, GrabSlamKbY = 120000, GrabStunAt26 = 46;
        const int GrabTopDx = 200, GrabTopDy = 1500, GrabSlamDx = 1000;
        const int GrabReleaseFrame = 26, GrabLiftN = 18, GrabSlamDen = 36;

        // ---- 2.3 Tornado
        static readonly int[] TornadoHits = { 0, 12, 24, 36, 48 };
        const int TornadoKb = 45000, TornadoStun = 15, TornadoSteer = 52500, MoveFriction = 6250;

        // ---- 2.4 Backstep
        const int BackVx = -240000, BackVy = 30000, BackExitVx = -30000, BackShotFrame = 9;
        const int ArrowOffX = 500, ArrowOffY = 725;

        // ---- 2.5 Arrow Barrage
        static readonly int[] BarrageShots = { 0, 6, 12, 18, 24 };
        const int BarrageY = 675, BarrageYOffMin = -250, BarrageYOffSpan = 551;

        // ---- 2.6 Leap Slam (D-M2 = B)
        const int LeapVx = 90000, LeapVy = 210000, LeapEarliest = 10, LeapTimeout = 78;

        // ---- 2.7 Spin Guard
        static readonly int[] SpinHits = { 0, 18, 36, 54 };
        const int SpinGuardTicks = 72, SpinAuraLastTick = 71;

        const int RulesVersion12 = 12;

        static readonly ChibiInputPacket N = default;

        // ================================================================== reflection access to v12 members

        const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static FieldInfo Need(Type t, string name)
        {
            var fi = t.GetField(name, Inst);
            if (fi == null) Assert.Fail($"field {t.Name}.{name} missing (multiphase-conversion.md section 6, RulesVersion 12)");
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

        static bool Held(ChibiFighterSim f) => GetBool(f, "Held");
        static bool Holding(ChibiFighterSim f) => GetBool(f, "Holding");

        /// <summary>Amendment 6 / 8h: F[i].Holding == F[1-i].Held, both directions, every tick.</summary>
        static void HeldInvariant(ChibiSimState s, int tick)
        {
            for (int i = 0; i < 2; i++)
                Assert.AreEqual(Holding(s.F[i]), Held(s.F[1 - i]),
                    $"A6/8h: F[{i}].Holding != F[{1 - i}].Held after tick {tick}");
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
        static ChibiSimState Arena(FighterClass c0, int p0, int x0, FighterClass c1, int p1, int x1, int facing = 1,
                                   uint seed = Seed)
        {
            var s = NewMatch(c0, p0, c1, p1, seed);
            Put(s.F[0], x0, 0, facing);
            Put(s.F[1], x1, 0, -facing);
            for (int t = 0; t < SettleAfterPut; t++) ChibiSimulation.Step(s, N, N);
            return s;
        }

        /// <summary>Holds a fighter in place (position, velocity, remainders) without touching Stun / Invuln / Facing.</summary>
        static void Pin(ChibiFighterSim f, int x, int y)
        {
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = y == 0;
        }

        static ChibiInputPacket In(ChibiButtons b, ChibiDir d = ChibiDir.Neutral) => ChibiInputPacket.Make(d, b);

        static void Game(ChibiSimState s, ChibiInputPacket a, ChibiInputPacket b) => ChibiTestTime.StepGame(s, a, b);

        static ChibiSimState Clone(ChibiSimState s)
        {
            var c = new ChibiSimState();
            c.CopyFrom(s);
            return c;
        }

        /// <summary>
        /// Presses <paramref name="b"/> for F[<paramref name="who"/>] on tick 0, runs ticks 0..<paramref name="last"/> and
        /// returns the ticks on which the other fighter lost HP. <paramref name="before"/> runs before each step (pins);
        /// <paramref name="onHit"/> sees each hit tick (velocity / stun read at the end of that step).
        /// </summary>
        static List<int> HitTicks(ChibiSimState s, ChibiButtons b, int last, Action<int> before = null,
                                  Action<int> onHit = null, int who = 0, Func<int, ChibiInputPacket> extra = null)
        {
            var hits = new List<int>();
            var v = s.F[1 - who];
            for (int t = 0; t <= last; t++)
            {
                before?.Invoke(t);
                int hp = v.HpU;
                var mine = t == 0 ? In(b) : (extra != null ? extra(t) : N);
                if (who == 0) Game(s, mine, N); else Game(s, N, mine);
                if (v.HpU < hp) { hits.Add(t); onHit?.Invoke(t); }
            }
            return hits;
        }

        /// <summary>Writes a shot from <paramref name="owner"/> into a free slot as if spawned on the previous game step.</summary>
        static ChibiProjectileSim Shot(ChibiSimState s, int owner, int x, int y, int flags, int stun = 17, int life = 10)
        {
            for (int n = 0; n < s.Proj.Length; n++)
            {
                var p = s.Proj[n];
                if (p.Active) continue;
                p.Active = true; p.Owner = owner; p.X = x; p.Y = y; p.Vx = 0; p.Vy = 0; p.XRem = 0; p.YRem = 0;
                p.Life = life; p.Mult = 1000; p.KbX = 22500; p.KbY = 18000; p.Stun = stun;
                p.Kind = ChibiProjKind.Arrow; p.Flags = flags; p.Radius = 250; p.GrowPerTick = 0; p.Speed = 0;
                p.ReturnTicks = 0; p.ReturnMult = 0; p.SpawnFrame = s.GameTicks - 1;
                return p;
            }
            Assert.Fail("precondition: no free projectile slot");
            return null;
        }

        /// <summary>A stationary stun shot on the Center of F[target] (interrupts it on the next step's projectile update).</summary>
        static void InterruptShot(ChibiSimState s, int target) =>
            Shot(s, 1 - target, s.F[target].X, s.F[target].Y + CenterY, 0);

        static int DivRound(long n, long d) => (int)(n >= 0 ? (n + d / 2) / d : -((-n + d / 2) / d));

        /// <summary>Section 2.2 victim feet on grab frame f (1..25) from S, T (frame 0) and L (frame 18).</summary>
        static void GrabPath(int f, int sx, int sy, int tx, int ty, int lx, int ly, out int x, out int y)
        {
            if (f <= 0) { x = sx; y = sy; return; }
            if (f < GrabLiftN)
            {
                x = sx + DivRound((long)(tx - sx) * f, GrabLiftN);
                y = sy + DivRound((long)(ty - sy) * f, GrabLiftN);
                return;
            }
            int j5 = 5 * (f - GrabLiftN);
            x = tx + DivRound((long)(lx - tx) * j5, GrabSlamDen);
            y = ty + DivRound((long)(ly - ty) * j5, GrabSlamDen);
        }

        /// <summary>Grab arena: Heavy F[0] at X 0 facing +1, victim F[1] (Melee Blood Edge unless given) at (victimX, 0).</summary>
        static ChibiSimState GrabArena(int victimX, FighterClass vc = FighterClass.Melee, int vp = BloodEdge, uint seed = Seed) =>
            Arena(FighterClass.Heavy, HeavyP1, 0, vc, vp, victimX, 1, seed);

        // ================================================================== 1-3 Thrust Barrage

        // Test 1 / R1 -- F:775-786: hits on 0, 7, 14, 21, 28, 35; kb 12000/7500 (last 90000/60000); stun 15; 6 HP drops.
        [Test]
        public void ThrustBarrage_VictimAt1000_HitsOnR1TicksWithR1KbAndStun()
        {
            var s = Arena(FighterClass.Melee, BloodEdge, 0, FighterClass.Melee, BloodEdge, 1000);
            var kb = new List<int[]>();
            var hits = HitTicks(s, ChibiButtons.Skill3, LockThrust, t => Pin(s.F[1], 1000, 0),
                                t => kb.Add(new[] { s.F[1].Vx, s.F[1].Vy, s.F[1].Stun }));
            CollectionAssert.AreEqual(ThrustHits, hits, "Test 1/R1: Thrust Barrage hit ticks (sim today: 5, 11, 17, 23, 29, 35)");
            for (int n = 0; n < kb.Count; n++)
            {
                bool last = n == ThrustHits.Length - 1;
                Assert.AreEqual(last ? ThrustLastKbX : ThrustKbX, kb[n][0], $"R1: hit {n} kb X");
                Assert.AreEqual(last ? ThrustLastKbY : ThrustKbY, kb[n][1], $"R1: hit {n} kb Y");
                Assert.AreEqual(ThrustStun, kb[n][2], $"R1: hit {n} victim Stun");
            }
        }

        // Test 2 -- F:670: attacker hit (stun > 0) on tick 20: hits 0, 7, 14 only; no big last hit; MoveSlot -1.
        [Test]
        public void ThrustBarrage_AttackerInterruptedOnTick20_OnlyHits0To14()
        {
            var s = Arena(FighterClass.Melee, BloodEdge, 0, FighterClass.Melee, BloodEdge, 1000);
            int slotAfter20 = 99;
            var hits = HitTicks(s, ChibiButtons.Skill3, LockThrust, t =>
            {
                Pin(s.F[1], 1000, 0);
                if (t == 20) InterruptShot(s, 0);
            }, null, 0, null);
            // re-run to read MoveSlot on tick 20 (HitTicks does not expose per-tick state)
            var r = Arena(FighterClass.Melee, BloodEdge, 0, FighterClass.Melee, BloodEdge, 1000);
            for (int t = 0; t <= 20; t++)
            {
                Pin(r.F[1], 1000, 0);
                if (t == 20) InterruptShot(r, 0);
                Game(r, t == 0 ? In(ChibiButtons.Skill3) : N, N);
            }
            slotAfter20 = r.F[0].MoveSlot;
            CollectionAssert.AreEqual(new[] { 0, 7, 14 }, hits, "Test 2: hits after an interrupt on tick 20");
            Assert.AreEqual(-1, slotAfter20, "Test 2: attacker MoveSlot after the interrupt");
        }

        // Test 3 -- F:456 vs F:775 (fact 7): Swift Blade + Hexed does not change any skill timing.
        [Test]
        public void ThrustBarrage_SwiftBladeHexed_SameHitTicksAsTest1()
        {
            var s = Arena(FighterClass.Melee, SwiftBlade, 0, FighterClass.Melee, BloodEdge, 1000);
            s.F[0].SluggishTicks = HexTicks;
            var hits = HitTicks(s, ChibiButtons.Skill3, LockThrust, t => Pin(s.F[1], 1000, 0));
            CollectionAssert.AreEqual(ThrustHits, hits, "Test 3: Swift Blade + Hex must not scale skill frames (fact 7)");
        }

        // Amendment 7 -- global ScaleFrames removal: a skill outside the seven (Ranged Bowstring Choke) hits on the same tick
        // hexed and not hexed.
        [Test]
        public void Choke_Hexed_SameFirstHitTickAsUnhexed()
        {
            var a = Arena(FighterClass.Ranged, RangedP0, 0, FighterClass.Melee, BloodEdge, 1000);
            var b = Arena(FighterClass.Ranged, RangedP0, 0, FighterClass.Melee, BloodEdge, 1000);
            b.F[0].SluggishTicks = HexTicks;
            var ha = HitTicks(a, ChibiButtons.Skill2, 40, t => Pin(a.F[1], 1000, 0));
            var hb = HitTicks(b, ChibiButtons.Skill2, 40, t => Pin(b.F[1], 1000, 0));
            Assert.IsNotEmpty(ha, "precondition: Choke hits a victim at X 1000");
            Assert.IsNotEmpty(hb, "A7: hexed Choke never hit");
            Assert.AreEqual(ha[0], hb[0], "A7: hexed Choke first hit tick (no ScaleFrames on skills, SIM:949-954 removed)");
        }

        // ================================================================== 4-14 Grab and the held state

        // Test 4 / R3-R5 -- F:788-818: lift and slam path tick by tick, stun 72 -> 46 at 26, slam kb, victim acts on 72.
        [Test]
        public void Grab_VictimAt1000_PathStunAndReleaseMatchR3ToR5()
        {
            var s = GrabArena(1000);
            var v = s.F[1];
            int hp = v.HpU;
            for (int t = 0; t <= GrabReleaseFrame; t++)
            {
                Game(s, t == 0 ? In(ChibiButtons.Skill1) : N, N);
                if (t == 0) Assert.AreEqual(GrabStun, v.Stun, "R5: victim Stun on tick 0 (grab Stun(1.2))");
                if (t < GrabReleaseFrame)
                {
                    GrabPath(t, 1000, 0, GrabTopDx, GrabTopDy, GrabSlamDx, 0, out int ex, out int ey);
                    Assert.AreEqual(ex, v.X, $"R3/R4: victim X on tick {t}");
                    Assert.AreEqual(ey, v.Y, $"R3/R4: victim Y on tick {t}");
                    Assert.AreEqual(0, v.Vx, $"held victim Vx on tick {t}");
                    Assert.AreEqual(0, v.Vy, $"held victim Vy on tick {t}");
                    Assert.AreEqual(hp, v.HpU, $"no damage before the slam (tick {t})");
                }
                if (t == 9) { Assert.AreEqual(600, v.X, "R3 f9 X"); Assert.AreEqual(750, v.Y, "R3 f9 Y"); }
                if (t == 17) { Assert.AreEqual(244, v.X, "R3 f17 X"); Assert.AreEqual(1417, v.Y, "R3 f17 Y"); }
                if (t == 18) { Assert.AreEqual(200, v.X, "R3 f18 X"); Assert.AreEqual(1500, v.Y, "R3 f18 Y"); }
                if (t == 19) { Assert.AreEqual(311, v.X, "R4 f19 X"); Assert.AreEqual(1292, v.Y, "R4 f19 Y"); }
                if (t == 25) { Assert.AreEqual(978, v.X, "R4 f25 X"); Assert.AreEqual(42, v.Y, "R4 f25 Y"); }
                HeldInvariant(s, t);
            }
            Assert.AreEqual(978, v.X, "R4: release X on tick 26 (no Physics on the release tick)");
            Assert.AreEqual(42, v.Y, "R4: release Y on tick 26");
            Assert.AreEqual(GrabSlamKbX, v.Vx, "R4: slam kb X");
            Assert.AreEqual(GrabSlamKbY, v.Vy, "R4: slam kb Y");
            Assert.Less(v.HpU, hp, "R4: slam damages on tick 26");
            Assert.AreEqual(GrabStunAt26, v.Stun, "R5: victim Stun after the slam (0.7 s absorbed)");
            Assert.IsFalse(Held(v), "victim released on tick 26");
            for (int t = GrabReleaseFrame + 1; t < GrabStun; t++) { Game(s, N, N); HeldInvariant(s, t); }
            Assert.AreEqual(1, v.Stun, "R5: victim Stun after tick 71");
            Game(s, N, In(ChibiButtons.Light));
            Assert.AreEqual(0, v.MoveSlot, "R5: victim acts (Light starts) on tick 72");
        }

        // Test 5 -- F:507-511: inclusive grab box; victim at 1825 (edge 1450 touches) connects, 1826 whiffs.
        [TestCase(1825, true)]
        [TestCase(1826, false)]
        public void Grab_VictimAtBoxEdge_InclusiveConnect(int victimX, bool connects)
        {
            var s = GrabArena(victimX);
            Game(s, In(ChibiButtons.Skill1), N);
            Assert.AreEqual(connects ? GrabStun : 0, s.F[1].Stun, $"Test 5: grab vs victim X {victimX} (A5 inclusive)");
            Assert.AreEqual(connects, Held(s.F[1]), "Test 5: victim Held");
            HeldInvariant(s, 0);
        }

        // Test 6 -- F:793: victim dashing (dash i-frames from a same-tick Dash press) -> whiff, lock 21.
        [Test]
        public void Grab_VictimDashInvuln_WhiffsWithLock21()
        {
            var s = GrabArena(1000);
            Game(s, In(ChibiButtons.Skill1), In(ChibiButtons.Dash));
            Assert.Greater(s.F[1].Invuln, 0, "precondition: victim dash invuln");
            Assert.AreEqual(0, s.F[1].Stun, "Test 6: grab on an invulnerable victim whiffs");
            HeldInvariant(s, 0);
            for (int t = 1; t < LockGrabWhiff - 1; t++) { Game(s, N, N); HeldInvariant(s, t); }
            var lost = Clone(s);
            Game(lost, In(ChibiButtons.Light), N);
            Assert.AreEqual(-1, lost.F[0].MoveSlot, "Test 6: Light on tick 20 lost (whiff lock 21)");
            Game(s, N, N);
            Game(s, In(ChibiButtons.Light), N);
            Assert.AreEqual(0, s.F[0].MoveSlot, "Test 6: Light on tick 21 starts");
        }

        // Test 7 -- F:793-798: the grab ignores shield, Parry stance and Spin Guard; the shield drops.
        [TestCase("shield")]
        [TestCase("parry")]
        [TestCase("spinguard")]
        public void Grab_VictimDefending_ConnectsAndDropsShield(string defence)
        {
            var s = GrabArena(1000);
            var hold = defence == "shield" ? In(ChibiButtons.Shield) : N;
            if (defence == "shield") for (int t = 0; t < 20; t++) Game(s, N, hold);   // shield up, past the parry window
            if (defence == "parry") s.F[1].ParryTicks = 30;
            if (defence == "spinguard") s.F[1].GuardTicks = 30;
            Game(s, In(ChibiButtons.Skill1), hold);
            Assert.AreEqual(GrabStun, s.F[1].Stun, $"Test 7: grab vs {defence} connects");
            Assert.IsFalse(s.F[1].Shielding, "Test 7: shield dropped on tick 0");
            HeldInvariant(s, 0);
            Game(s, N, hold);
            Assert.IsFalse(s.F[1].Shielding, "Test 7: held victim cannot shield on tick 1");
            HeldInvariant(s, 1);
        }

        // Test 8 -- GrabPhase symmetry: victim index 0 vs index 1 gives the identical trajectory and release.
        [Test]
        public void Grab_SwapSlots_IdenticalVictimTrajectory()
        {
            var a = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 1000);
            var b = NewMatch(FighterClass.Melee, BloodEdge, FighterClass.Heavy, HeavyP1);
            Put(b.F[1], 0, 0, 1);
            Put(b.F[0], 1000, 0, -1);
            for (int t = 0; t < SettleAfterPut; t++) ChibiSimulation.Step(b, N, N);
            for (int t = 0; t <= GrabReleaseFrame + 4; t++)
            {
                var press = t == 0 ? In(ChibiButtons.Skill1) : N;
                Game(a, press, N);
                Game(b, N, press);
                Assert.AreEqual(a.F[1].X, b.F[0].X, $"Test 8: victim X on tick {t}");
                Assert.AreEqual(a.F[1].Y, b.F[0].Y, $"Test 8: victim Y on tick {t}");
                Assert.AreEqual(a.F[1].Vx, b.F[0].Vx, $"Test 8: victim Vx on tick {t}");
                Assert.AreEqual(a.F[1].Vy, b.F[0].Vy, $"Test 8: victim Vy on tick {t}");
                if (t == 0) Assert.AreEqual(GrabStun, a.F[1].Stun, "precondition: grab connects");
                HeldInvariant(a, t);
                HeldInvariant(b, t);
            }
        }

        // Test 9 -- F:179, F:190: held victim ignores Jump / Dash / Light / Shield; V stays 0; path = R3/R4 only.
        [Test]
        public void Grab_HeldVictimPresses_DoNothing()
        {
            var s = GrabArena(1000);
            var v = s.F[1];
            ChibiButtons[] cycle = { ChibiButtons.Jump, ChibiButtons.Dash, ChibiButtons.Light, ChibiButtons.Shield };
            for (int t = 0; t < GrabReleaseFrame; t++)
            {
                var vin = t == 0 ? N : In(cycle[t % cycle.Length]);
                Game(s, t == 0 ? In(ChibiButtons.Skill1) : N, vin);
                if (t == 0) { Assert.AreEqual(GrabStun, v.Stun, "precondition: grab connects"); continue; }
                GrabPath(t, 1000, 0, GrabTopDx, GrabTopDy, GrabSlamDx, 0, out int ex, out int ey);
                Assert.AreEqual(ex, v.X, $"Test 9: victim X on tick {t}");
                Assert.AreEqual(ey, v.Y, $"Test 9: victim Y on tick {t}");
                Assert.AreEqual(0, v.Vx, $"Test 9: Vx on tick {t}");
                Assert.AreEqual(0, v.Vy, $"Test 9: Vy on tick {t}");
                Assert.AreEqual(-1, v.MoveSlot, $"Test 9: no move on tick {t}");
                Assert.AreEqual(0, v.DashTicks, $"Test 9: no dash on tick {t}");
                Assert.IsFalse(v.Shielding, $"Test 9: no shield on tick {t}");
                HeldInvariant(s, t);
            }
        }

        // Test 10 -- F:670-683: holder hit (stun > 0) on tick 10 releases the victim at its tick-10 position, V 0, Stun kept.
        [Test]
        public void Grab_HolderInterruptedOnTick10_VictimReleasedInPlace()
        {
            var s = GrabArena(1000);
            var v = s.F[1];
            for (int t = 0; t <= 10; t++)
            {
                if (t == 10) InterruptShot(s, 0);
                Game(s, t == 0 ? In(ChibiButtons.Skill1) : N, N);
                if (t == 0) Assert.AreEqual(GrabStun, v.Stun, "precondition: grab connects");
                HeldInvariant(s, t);
            }
            Assert.AreEqual(-1, s.F[0].MoveSlot, "Test 10: holder MoveSlot after the hit");
            Assert.IsFalse(Held(v), "Test 10: victim released");
            Assert.AreEqual(556, v.X, "Test 10: victim X = tick-10 path point (1000 - 444)");
            Assert.AreEqual(833, v.Y, "Test 10: victim Y = tick-10 path point");
            Assert.AreEqual(0, v.Vx, "Test 10: victim Vx");
            Assert.AreEqual(0, v.Vy, "Test 10: victim Vy");
            Assert.AreEqual(GrabStun - 10, v.Stun, "Test 10: victim keeps its remaining Stun");
            Game(s, N, N);
            Assert.Less(v.Y, 833, "Test 10: released victim falls on tick 11");
            Assert.AreEqual(556, v.X, "Test 10: no horizontal motion after release");
            HeldInvariant(s, 11);
        }

        // Test 11 -- F:692-697: held victim carried past BlastX rings out; holder Interrupted and free next tick.
        // Computed with section 2.2: S (17000, ~4990), T x 16400, L x 17200 -> j = 6 (x 17067) on tick 24.
        [Test]
        public void Grab_HeldVictimPastBlastX_RingsOutAndFreesHolder()
        {
            const int expectRingOut = 24;   // computed from the 2.2 formula, see comment above
            var s = NewMatch(FighterClass.Heavy, HeavyP1, FighterClass.Melee, BloodEdge);
            Put(s.F[0], 16200, 5000, 1);
            Put(s.F[1], BlastX, 5000, -1);
            int ringTick = -1;
            for (int t = 0; t <= GrabReleaseFrame; t++)
            {
                Game(s, t == 0 ? In(ChibiButtons.Skill1) : N, N);
                if (t == 0) Assert.AreEqual(GrabStun, s.F[1].Stun, "precondition: airborne grab at the edge connects");
                HeldInvariant(s, t);
                if (s.F[1].X == RespawnX && s.F[1].Y == RespawnY) { ringTick = t; break; }
            }
            Assert.AreEqual(expectRingOut, ringTick, "Test 11: victim ring-out tick");
            Assert.IsFalse(Held(s.F[1]), "Test 11: ringed-out victim not held");
            Assert.AreEqual(-1, s.F[0].MoveSlot, "Test 11: holder Interrupted at once");
            Game(s, In(ChibiButtons.Light), N);
            Assert.AreEqual(0, s.F[0].MoveSlot, "Test 11: holder free on the next tick");
        }

        // Test 12 -- F:695, F:681: holder rings out while holding: victim released in place, V 0, Stun kept.
        [Test]
        public void Grab_HolderRingsOut_VictimReleasedWithStun()
        {
            var s = NewMatch(FighterClass.Heavy, HeavyP1, FighterClass.Melee, BloodEdge);
            Put(s.F[0], 16700, 5000, 1, 180000);    // tick 0 -> ~16888, tick 1 -> ~17077 > BlastX
            Put(s.F[1], 16950, 5000, -1);
            Game(s, In(ChibiButtons.Skill1), N);
            Assert.AreEqual(GrabStun, s.F[1].Stun, "precondition: grab connects on tick 0");
            HeldInvariant(s, 0);
            Game(s, N, N);
            Assert.AreEqual(RespawnX, s.F[0].X, "precondition: holder rang out on tick 1");
            Assert.IsFalse(Held(s.F[1]), "Test 12: victim released");
            Assert.AreNotEqual(RespawnY, s.F[1].Y, "Test 12: victim not respawned");
            Assert.AreEqual(0, s.F[1].Vx, "Test 12: victim Vx 0");
            Assert.AreEqual(0, s.F[1].Vy, "Test 12: victim Vy 0");
            Assert.AreEqual(GrabStun - 1, s.F[1].Stun, "Test 12: victim keeps Stun");
            HeldInvariant(s, 1);
            int y = s.F[1].Y;
            Game(s, N, N);
            Assert.Less(s.F[1].Y, y, "Test 12: released victim falls");
        }

        // Test 13 -- F:574-579: slam vs Ward: 1 draw; on success no damage, no kb, victim still released. Without Ward
        // success: ward + crit = 2 draws and damage.
        [Test]
        public void Grab_SlamVsWard_OneDrawAndStillReleased()
        {
            bool sawWard = false, sawHit = false;
            for (uint seed = 1; seed <= 200 && !(sawWard && sawHit); seed++)
            {
                var s = GrabArena(1000, FighterClass.Magic, MagicWard, seed);
                for (int t = 0; t < GrabReleaseFrame; t++)
                {
                    Game(s, t == 0 ? In(ChibiButtons.Skill1) : N, N);
                    if (t == 0) Assert.AreEqual(GrabStun, s.F[1].Stun, "precondition: grab connects");
                }
                var r = s.Rng;
                bool ward = r.Roll(WardPermille);
                if (ward ? sawWard : sawHit) continue;
                int hp = s.F[1].HpU;
                Game(s, N, N);
                Assert.IsFalse(Held(s.F[1]), $"Test 13: victim released on tick 26 (seed {seed}, ward {ward})");
                if (ward)
                {
                    sawWard = true;
                    Assert.AreEqual(hp, s.F[1].HpU, "Test 13: Ward negates the slam");
                    Assert.AreEqual(r.State, s.Rng.State, "Test 13: exactly one draw (Ward) on a negated slam");
                    Assert.AreEqual(0, s.F[1].Vx, "Test 13: no kb on a negated slam");
                }
                else
                {
                    sawHit = true;
                    r.NextU32();                              // crit draw
                    Assert.Less(s.F[1].HpU, hp, "Test 13: slam damages without Ward");
                    Assert.AreEqual(r.State, s.Rng.State, "Test 13: Ward + crit = 2 draws");
                }
            }
            Assert.IsTrue(sawWard && sawHit, "precondition: seeds 1..200 cover Ward success and failure");
        }

        // Test 14 -- D-M3 = B: both Heavies grab on the same tick -> both whiff, lock 21.
        [Test]
        public void Grab_BothSameTick_BothWhiffLock21()
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Heavy, HeavyP1, 1000);
            var g = In(ChibiButtons.Skill1);
            Game(s, g, g);
            Assert.AreEqual(0, s.F[0].Stun, "D-M3 B: F0 not grabbed");
            Assert.AreEqual(0, s.F[1].Stun, "D-M3 B: F1 not grabbed");
            HeldInvariant(s, 0);
            Assert.IsFalse(Held(s.F[0]) || Held(s.F[1]), "D-M3 B: nobody held");
            for (int t = 1; t < LockGrabWhiff - 1; t++) { Game(s, N, N); HeldInvariant(s, t); }
            var lost = Clone(s);
            Game(lost, In(ChibiButtons.Light), In(ChibiButtons.Light));
            Assert.AreEqual(-1, lost.F[0].MoveSlot, "D-M3 B: F0 Light on 20 lost");
            Assert.AreEqual(-1, lost.F[1].MoveSlot, "D-M3 B: F1 Light on 20 lost");
            Game(s, N, N);
            Game(s, In(ChibiButtons.Light), In(ChibiButtons.Light));
            Assert.AreEqual(0, s.F[0].MoveSlot, "D-M3 B: F0 Light on 21 starts");
            Assert.AreEqual(0, s.F[1].MoveSlot, "D-M3 B: F1 Light on 21 starts");
        }

        // Amendment 8a / 1 -- grab vs the victim's Light strike on the same tick: grab wins, the strike does not land.
        [Test]
        public void Grab_VsSameTickLightStrike_GrabWinsStrikeCancelled()
        {
            var s = GrabArena(1000);
            var dry = Clone(s);
            int strike = -1;
            for (int t = 0; t < 30 && strike < 0; t++)
            {
                int hp = dry.F[0].HpU;
                Game(dry, N, t == 0 ? In(ChibiButtons.Light) : N);
                if (dry.F[0].HpU < hp) strike = t;
            }
            Assert.GreaterOrEqual(strike, 1, "precondition: victim Light strikes the Heavy");
            int hp0 = s.F[0].HpU;
            for (int t = 0; t <= strike; t++)
                Game(s, t == strike ? In(ChibiButtons.Skill1) : N, t == 0 ? In(ChibiButtons.Light) : N);
            Assert.AreEqual(GrabStun, s.F[1].Stun, "A8a: grab started on the strike tick connects");
            Assert.AreEqual(hp0, s.F[0].HpU, "A8a: the victim's same-tick strike does not land (grab before MeleeProbe)");
            HeldInvariant(s, strike);
        }

        // Amendment 8b -- grab vs a victim Tornado frame-0 hit on the same tick: grab wins.
        [Test]
        public void Grab_VsSameTickTornadoFrame0_GrabWins()
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Heavy, HeavyP1, 1000);
            int hp0 = s.F[0].HpU;
            Game(s, In(ChibiButtons.Skill1), In(ChibiButtons.Skill2));
            Assert.AreEqual(GrabStun, s.F[1].Stun, "A8b: grab connects");
            Assert.AreEqual(hp0, s.F[0].HpU, "A8b: Tornado frame-0 hit cancelled by the grab");
            HeldInvariant(s, 0);
        }

        // Amendment 8d -- forced hitstop + ult slow-mo at the press: victim positions per GAME STEP equal R3/R4.
        [Test]
        public void Grab_UnderHitstopAndSlowMo_PathPerGameStepMatchesR3R4()
        {
            var s = GrabArena(1000);
            s.HitstopRealTicks = 5;
            s.CinematicRealTicks = 48;
            var v = s.F[1];
            for (int t = 0; t <= GrabReleaseFrame; t++)
            {
                Game(s, t == 0 ? In(ChibiButtons.Skill1) : N, N);
                if (t == 0) Assert.AreEqual(GrabStun, v.Stun, "A8d: grab connects under slow-mo");
                if (t < GrabReleaseFrame)
                {
                    GrabPath(t, 1000, 0, GrabTopDx, GrabTopDy, GrabSlamDx, 0, out int ex, out int ey);
                    Assert.AreEqual(ex, v.X, $"A8d: victim X on game step {t}");
                    Assert.AreEqual(ey, v.Y, $"A8d: victim Y on game step {t}");
                }
                HeldInvariant(s, t);
            }
            Assert.AreEqual(978, v.X, "A8d: release X");
            Assert.AreEqual(42, v.Y, "A8d: release Y");
            Assert.AreEqual(GrabSlamKbX, v.Vx, "A8d: slam kb X");
        }

        // Amendment 8f / 4 -- holder and victim ring out on the same tick: idempotent release, both respawn, nobody held.
        // FIXTURE: on tick 5 the holder is moved to X 16999 with Vx 200 milli/tick and its stored S/T are moved past BlastX.
        [Test]
        public void Grab_HolderAndVictimRingOutSameTick_BothRespawnNobodyHeld()
        {
            var s = GrabArena(1000);
            for (int t = 0; t < 5; t++)
            {
                Game(s, t == 0 ? In(ChibiButtons.Skill1) : N, N);
                if (t == 0) Assert.AreEqual(GrabStun, s.F[1].Stun, "precondition: grab connects");
            }
            var h = s.F[0];
            h.X = 16999; h.Y = 5000; h.Vx = 180000; h.Vy = 0; h.Grounded = false;
            SetInt(h, "GrabSX", 17500); SetInt(h, "GrabTX", 17500);
            int hp0 = s.F[0].HpU, hp1 = s.F[1].HpU;
            Game(s, N, N);
            Assert.AreEqual(RespawnY, s.F[0].Y, "A8f: holder respawned");
            Assert.AreEqual(RespawnY, s.F[1].Y, "A8f: victim respawned");
            Assert.Less(s.F[0].HpU, hp0, "A8f: holder ring-out penalty");
            Assert.Less(s.F[1].HpU, hp1, "A8f: victim ring-out penalty");
            Assert.IsFalse(Held(s.F[1]) || Holding(s.F[0]), "A8f: nobody held after a double ring-out");
            Assert.AreEqual(-1, s.F[0].MoveSlot, "A8f: holder move cancelled");
            HeldInvariant(s, 5);
            int y = s.F[1].Y;
            Game(s, N, N);
            Assert.AreNotEqual(y, s.F[1].Y, "A8f: victim moves under its own Physics after respawn");
        }

        // Amendment 8g -- slam KO: match ends, Held cleared.
        [Test]
        public void Grab_SlamKo_MatchEndsHeldCleared()
        {
            var s = GrabArena(1000);
            for (int t = 0; t < GrabReleaseFrame; t++)
            {
                Game(s, t == 0 ? In(ChibiButtons.Skill1) : N, N);
                if (t == 0) Assert.AreEqual(GrabStun, s.F[1].Stun, "precondition: grab connects");
            }
            s.F[1].HpU = 1;
            Game(s, N, N);
            Assert.IsTrue(s.MatchOver, "A8g: slam KO ends the match");
            Assert.AreEqual(0, s.Winner, "A8g: holder wins");
            Assert.IsFalse(Held(s.F[1]), "A8g: Held cleared");
            Assert.IsFalse(Holding(s.F[0]), "A8g: Holding cleared");
        }

        // ================================================================== 15-18 Tornado Spin

        // Test 15 / R6 -- F:820-842: hits 0, 12, 24, 36, 48; kb (dir 45000, 45000), stun 15.
        [Test]
        public void Tornado_VictimInsideR1600_HitsOnR6Ticks()
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 1000);
            var kb = new List<int[]>();
            var hits = HitTicks(s, ChibiButtons.Skill2, LockTornado, t => Pin(s.F[1], s.F[0].X + 1000, 0),
                                t => kb.Add(new[] { s.F[1].Vx, s.F[1].Vy, s.F[1].Stun }));
            CollectionAssert.AreEqual(TornadoHits, hits, "Test 15/R6: Tornado hit ticks (sim today: 7, 19, 31, 43, 55)");
            foreach (var k in kb)
            {
                Assert.AreEqual(TornadoKb, k[0], "R6: kb X");
                Assert.AreEqual(TornadoKb, k[1], "R6: kb Y");
                Assert.AreEqual(TornadoStun, k[2], "R6: stun");
            }
        }

        // Test 16 / R7 -- F:828: MoveX 0 on tick 0, then +1: dX(59 - 0) in {3441, 3442}; Vx 46250 on 60, 21250 on 64.
        // Second case: MoveX opposite to Facing steers backward and Facing stays +1.
        [TestCase(ChibiDir.E, 1)]
        [TestCase(ChibiDir.W, -1)]
        public void Tornado_Steering_MatchesR7AndKeepsFacing(ChibiDir dir, int sign)
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, -7000);
            int x0 = 0;
            for (int t = 0; t <= 64; t++)
            {
                Game(s, t == 0 ? In(ChibiButtons.Skill2) : In(ChibiButtons.None, dir), N);
                if (t == 0) x0 = s.F[0].X;
                if (t >= 1 && t <= 59) Assert.AreEqual(sign * TornadoSteer, s.F[0].Vx, $"R7: steering Vx on tick {t}");
                if (t == 59)
                    Assert.That(sign * (s.F[0].X - x0), Is.InRange(3441, 3442), "R7: X(after 59) - X(after 0)");
                if (t == 60) Assert.AreEqual(sign * (TornadoSteer - MoveFriction), s.F[0].Vx, "R7: Vx on tick 60 (friction)");
                if (t == 64) Assert.AreEqual(sign * (TornadoSteer - 5 * MoveFriction), s.F[0].Vx, "R7: Vx on tick 64");
                Assert.AreEqual(1, s.F[0].Facing, $"Test 16: Facing unchanged on tick {t}");
            }
        }

        // Test 17 -- F:836: Mathf.Sign(0) = +1: equal X -> victim kb Vx +45000 even when the spinner faces -1.
        [Test]
        public void Tornado_EqualX_KbDirectionPlus()
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 0, -1);
            Pin(s.F[1], s.F[0].X, 0);
            int hp = s.F[1].HpU;
            Game(s, In(ChibiButtons.Skill2), N);
            Assert.Less(s.F[1].HpU, hp, "precondition: Tornado frame-0 hit at equal X");
            Assert.AreEqual(TornadoKb, s.F[1].Vx, "Test 17: kb Vx sign at equal X (+1)");
        }

        // Test 18 -- F:291, F:828: Jump on tick 20 during the spin sets Vy; steering continues.
        [Test]
        public void Tornado_JumpOnTick20_VyJumpSteeringContinues()
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, -7000);
            for (int t = 0; t <= 21; t++)
            {
                var b = t == 0 ? ChibiButtons.Skill2 : (t == 20 ? ChibiButtons.Jump : ChibiButtons.None);
                Game(s, In(b, t == 0 ? ChibiDir.Neutral : ChibiDir.E), N);
                if (t == 20)
                {
                    Assert.AreEqual(ChibiSimConst.JumpVel - ChibiSimConst.Gravity, s.F[0].Vy, "Test 18: jump Vy on tick 20");
                    Assert.AreEqual(TornadoSteer, s.F[0].Vx, "Test 18: steering on tick 20");
                }
            }
            Assert.AreEqual(TornadoSteer, s.F[0].Vx, "Test 18: steering on tick 21");
        }

        // ================================================================== 19 Backstep Shot

        // Test 19 / R8 -- F:844-855 + A2: V (-240000, 30000) written on 1-8 (Vy read after gravity), Jump on tick 3 overridden;
        // tick 9: Vx -30000, big arrow from the position before tick-9 Physics.
        [Test]
        public void Backstep_R8VelocitiesShotOnTick9_JumpOverridden()
        {
            var s = Arena(FighterClass.Ranged, RangedP0, 0, FighterClass.Melee, BloodEdge, -7000);
            int x8 = 0, y8 = 0;
            for (int t = 0; t <= BackShotFrame; t++)
            {
                var b = t == 0 ? ChibiButtons.Skill1 : (t == 3 ? ChibiButtons.Jump : ChibiButtons.None);
                Game(s, In(b), N);
                if (t >= 1 && t <= 8)
                {
                    Assert.AreEqual(BackVx, s.F[0].Vx, $"R8: Vx on tick {t}");
                    Assert.AreEqual(BackVy - ChibiSimConst.Gravity, s.F[0].Vy, $"R8/A2: Vy on tick {t} (jump on 3 overridden)");
                }
                if (t < BackShotFrame)
                    foreach (var p in s.Proj)
                        Assert.IsFalse(p.Active && p.Owner == 0, $"R8: no arrow before tick 9 (tick {t})");
                if (t == 8) { x8 = s.F[0].X; y8 = s.F[0].Y; }
            }
            Assert.AreEqual(BackExitVx, s.F[0].Vx, "R8: exit Vx on tick 9");
            ChibiProjectileSim shot = null;
            foreach (var p in s.Proj) if (p.Active && p.Owner == 0 && p.SpawnFrame == s.GameTicks - 1) shot = p;
            Assert.IsNotNull(shot, "R8: big arrow spawned on tick 9");
            Assert.AreEqual(x8 + ArrowOffX, shot.X, "R8: arrow X = feet before tick-9 Physics + Facing*500");
            Assert.AreEqual(y8 + ArrowOffY, shot.Y, "R8: arrow Y = feet + 725");
        }

        // ================================================================== 20-21 Arrow Barrage

        // Test 20 / R9 -- F:865-874: arrows on 0, 6, 12, 18, 24; yOff = -250 + NextU32 % 551, one draw per arrow.
        [Test]
        public void ArrowBarrage_FixedSeed_R9TicksAndRandomYOff()
        {
            var s = Arena(FighterClass.Ranged, RangedP0, 0, FighterClass.Melee, BloodEdge, -7000);
            var spawns = new List<int>();
            for (int t = 0; t <= LockBarrage; t++)
            {
                var r = s.Rng;
                int fy = s.F[0].Y;
                Game(s, t == 0 ? In(ChibiButtons.Skill3) : N, N);
                ChibiProjectileSim shot = null;
                foreach (var p in s.Proj) if (p.Active && p.Owner == 0 && p.SpawnFrame == s.GameTicks - 1) shot = p;
                if (shot == null) { Assert.AreEqual(r.State, s.Rng.State, $"R9: no draw on tick {t} without an arrow"); continue; }
                spawns.Add(t);
                int yOff = BarrageYOffMin + (int)(r.NextU32() % (uint)BarrageYOffSpan);
                Assert.AreEqual(fy + BarrageY + yOff, shot.Y, $"R9: arrow Y on tick {t} (yOff {yOff})");
                Assert.AreEqual(r.State, s.Rng.State, $"R9: exactly one draw on spawn tick {t}");
            }
            CollectionAssert.AreEqual(BarrageShots, spawns, "Test 20/R9: Arrow Barrage spawn ticks (sim today: 7, 12, 17, 22, 27)");
        }

        // Test 21 -- F:871: pool full: the yOff draw is still consumed, DroppedShots +1.
        [Test]
        public void ArrowBarrage_PoolFull_DrawConsumedAndDropped()
        {
            var s = Arena(FighterClass.Ranged, RangedP0, 0, FighterClass.Melee, BloodEdge, -7000);
            for (int n = 0; n < s.Proj.Length; n++)
                Shot(s, 1, 30000, 20000, ChibiProjectileSim.FHarmless, 0, 1000);
            var r = s.Rng;
            int dropped = s.DroppedShots;
            Game(s, In(ChibiButtons.Skill3), N);
            Assert.AreEqual(dropped + 1, s.DroppedShots, "Test 21: first arrow dropped on tick 0");
            r.NextU32();
            Assert.AreEqual(r.State, s.Rng.State, "Test 21: yOff draw consumed although the pool is full");
        }

        // ================================================================== 22-24 Leap Slam (D-M2 = B)

        /// <summary>
        /// Runs Leap Slam with the victim pinned 1000 ahead at the attacker's height (so the slam box always reaches it).
        /// Returns the slam tick (first victim HP drop) or -1; <paramref name="groundedAtStart"/>[t] = attacker Grounded
        /// before step t (amendment 3 snapshot). Stops at <paramref name="last"/>.
        /// </summary>
        static int LeapRun(ChibiSimState s, int last, List<bool> groundedAtStart, Func<int, ChibiInputPacket> extra = null,
                           Action<int> after = null)
        {
            var a = s.F[0];
            int slam = -1;
            for (int t = 0; t <= last; t++)
            {
                Pin(s.F[1], a.X + a.Facing * 1000, a.Y);
                groundedAtStart.Add(a.Grounded);
                int hp = s.F[1].HpU;
                Game(s, t == 0 ? In(ChibiButtons.Skill1) : (extra != null ? extra(t) : N), N);
                if (slam < 0 && s.F[1].HpU < hp) slam = t;
                after?.Invoke(t);
            }
            return slam;
        }

        static int ExpectedSlam(List<bool> g)
        {
            for (int t = LeapEarliest; t < LeapTimeout && t < g.Count; t++) if (g[t]) return t;
            return LeapTimeout;
        }

        static void AssertLeapStructure(ChibiSimState s, int slam, List<bool> g, string ctx)
        {
            Assert.GreaterOrEqual(slam, LeapEarliest, $"{ctx}: slam not before tick 10 (got {slam})");
            Assert.LessOrEqual(slam, LeapTimeout, $"{ctx}: slam at the latest on tick 78");
            Assert.AreEqual(ExpectedSlam(g), slam, $"{ctx}: slam on the first tick >= 10 with start-of-tick Grounded (A3)");
        }

        // Test 22 / R12 -- F:901-917, D-M2 = B: no leap on tick 0, leap V (90000, 210000) on tick 1, slam on landing,
        // free slam + 12.
        // Amendment 9: landing tick pinned from the sim run (multiphase-conversion.md R12 / test 22): slam on tick 48.
        const int LeapPinnedSlamTick = 48;   // R12, pinned by the step-12 sim run (amendment 9)

        [Test]
        public void LeapSlam_Grounded_LeapsOnTick1SlamsOnLanding()
        {
            var s = Arena(FighterClass.Polearm, PolearmP0, 0, FighterClass.Melee, BloodEdge, 1000);
            var g = new List<bool>();
            int slam = LeapRun(s, 1, g, null, t =>
            {
                if (t == 0) Assert.AreEqual(0, s.F[0].Y, "R12: no leap on tick 0 (frame-0 write skipped)");
                if (t == 1)
                {
                    Assert.IsFalse(s.F[0].Grounded, "R12: airborne after tick 1");
                    Assert.Greater(s.F[0].Y, 0, "R12: left the ground on tick 1");
                    Assert.AreEqual(LeapVx, s.F[0].Vx, "R12: leap Vx on tick 1");
                    Assert.AreEqual(LeapVy - ChibiSimConst.Gravity, s.F[0].Vy, "R12: leap Vy on tick 1 (after gravity)");
                }
            });
            Assert.AreEqual(-1, slam, "R12: no slam on ticks 0-1");
            var rest = new List<bool>();
            int slam2 = -1;
            for (int t = 2; t <= LeapTimeout && slam2 < 0; t++)
            {
                var a = s.F[0];
                Pin(s.F[1], a.X + 1000, a.Y);
                g.Add(a.Grounded);
                int hp = s.F[1].HpU;
                Game(s, N, N);
                if (s.F[1].HpU < hp) slam2 = t;
            }
            AssertLeapStructure(s, slam2, g, "Test 22/R12");
            Assert.AreEqual(LeapPinnedSlamTick, slam2, "R12 (amendment 9): pinned slam tick");
            Assert.IsTrue(g.GetRange(2, slam2 - 2).Contains(false), "D-M2 B: the attacker was airborne before the slam");
            for (int t = slam2 + 1; t <= slam2 + LeapLockAfterSlam - 2; t++) Game(s, N, N);
            var lost = Clone(s);
            Game(lost, In(ChibiButtons.Light), N);
            Assert.AreEqual(-1, lost.F[0].MoveSlot, "R12: Light on slam + 11 lost");
            Game(s, N, N);
            Game(s, In(ChibiButtons.Light), N);
            Assert.AreEqual(0, s.F[0].MoveSlot, "R12: Light on slam + 12 starts");
        }

        // Amendment 8c / 3 -- Jump on the landing tick: Grounded snapshot taken at tick start, so it still slams that tick.
        [Test]
        public void LeapSlam_JumpOnLandingTick_StillSlams()
        {
            var a = Arena(FighterClass.Polearm, PolearmP0, 0, FighterClass.Melee, BloodEdge, 1000);
            var b = Clone(a);
            int slam = LeapRun(a, LeapTimeout, new List<bool>());
            Assert.That(slam, Is.InRange(LeapEarliest + 1, LeapTimeout - 1), "precondition: grounded leap lands and slams");
            int slamJ = LeapRun(b, LeapTimeout, new List<bool>(), t => t == slam ? In(ChibiButtons.Jump) : N);
            Assert.AreEqual(slam, slamJ, "A8c: Jump on the landing tick still slams on that tick");
        }

        // Test 23 -- F:909: started in the air over the stage: slams on the first start-of-tick Grounded tick >= 10.
        // NOTE: doc test 23 "landing on tick 30, free 42" is an A-world number; under D-M2 = B the tick-1 leap also fires in
        // the air, so the landing tick is again a sim-run value (TBD-verify, amendment 9). Asserted structurally.
        [Test]
        public void LeapSlam_StartedInAir_SlamsOnFirstGroundedTickFrom10()
        {
            var s = NewMatch(FighterClass.Polearm, PolearmP0, FighterClass.Melee, BloodEdge);
            Put(s.F[0], 0, 3000, 1);
            Put(s.F[1], 1000, 3000, -1);
            var g = new List<bool>();
            int slam = -1;
            var a = s.F[0];
            for (int t = 0; t <= LeapTimeout && slam < 0; t++)
            {
                Pin(s.F[1], a.X + 1000, a.Y);
                g.Add(a.Grounded);
                int hp = s.F[1].HpU;
                Game(s, t == 0 ? In(ChibiButtons.Skill1) : N, N);
                if (s.F[1].HpU < hp) slam = t;
            }
            AssertLeapStructure(s, slam, g, "Test 23");
        }

        // Test 24 -- F:906: off-stage, never lands: slam on tick 78 in the air; free on 90.
        [Test]
        public void LeapSlam_OffStageNeverLands_SlamsOnTick78InAir()
        {
            // Facing -1 from X 16500: X 16500 -> ~8700 by tick 78 (never over the main stage, |X| > 8375, no platform);
            // Y 9000 -> about -2600 (> KillY -9725), peak about 11800 (< CeilingY 17775).   // TBD-verify by sim run
            var s = NewMatch(FighterClass.Polearm, PolearmP0, FighterClass.Melee, BloodEdge);
            Put(s.F[0], 16500, 9000, -1);
            Put(s.F[1], 15500, 9000, 1);
            var g = new List<bool>();
            int slam = -1;
            var a = s.F[0];
            for (int t = 0; t <= LeapTimeout && slam < 0; t++)
            {
                Pin(s.F[1], a.X - 1000, a.Y);
                g.Add(a.Grounded);
                int hp = s.F[1].HpU;
                Game(s, t == 0 ? In(ChibiButtons.Skill1) : N, N);
                if (s.F[1].HpU < hp) slam = t;
            }
            Assert.AreNotEqual(RespawnY, a.Y, "precondition: attacker did not ring out");
            Assert.AreEqual(LeapTimeout, slam, "Test 24: timeout slam on tick 78");
            Assert.IsFalse(a.Grounded, "Test 24: slam happened in the air");
        }

        // ================================================================== 25-26 Spin Guard

        // Test 25 / R13 -- F:919-933: hits 0, 18, 36, 54; GuardTicks 72 on tick 0; a destructible arrow inside 1.6 is
        // destroyed on tick 71, not on 72.
        [Test]
        public void SpinGuard_R13HitsAndAuraEndsAfterTick71()
        {
            var s = Arena(FighterClass.Polearm, PolearmP0, 0, FighterClass.Melee, BloodEdge, 1000);
            var hits = new List<int>();
            ChibiSimState at71 = null;
            for (int t = 0; t < SpinAuraLastTick; t++)
            {
                Pin(s.F[1], 1000, 0);
                int hp = s.F[1].HpU;
                Game(s, t == 0 ? In(ChibiButtons.Skill2) : N, N);
                if (s.F[1].HpU < hp) hits.Add(t);
                if (t == 0) Assert.AreEqual(SpinGuardTicks, s.F[0].GuardTicks, "R13: GuardTicks 72 on tick 0");
            }
            at71 = Clone(s);
            CollectionAssert.AreEqual(SpinHits, hits, "Test 25/R13: Spin Guard hit ticks (sim today: 3, 21, 39, 57)");
            var p71 = Shot(at71, 1, at71.F[0].X + 1000, at71.F[0].Y + CenterY, ChibiProjectileSim.FDestructible);
            Pin(at71.F[1], 1000, 0);
            Game(at71, N, N);
            Assert.IsFalse(p71.Active, "R13: aura destroys a destructible arrow on tick 71");
            Pin(s.F[1], 1000, 0);
            Game(s, N, N);
            var p72 = Shot(s, 1, s.F[0].X + 1000, s.F[0].Y + CenterY, ChibiProjectileSim.FDestructible);
            Pin(s.F[1], 1000, 0);
            Game(s, N, N);
            Assert.IsTrue(p72.Active, "R13: aura gone on tick 72");
        }

        // Test 26 -- F:670, F:525: interrupted on tick 10: no more hits; the aura (set once on tick 0) survives the interrupt
        // and still destroys a projectile on tick 60.
        [Test]
        public void SpinGuard_InterruptedOnTick10_NoMoreHitsAuraPersists()
        {
            var s = Arena(FighterClass.Polearm, PolearmP0, 0, FighterClass.Melee, BloodEdge, 1000);
            var hits = new List<int>();
            for (int t = 0; t < 60; t++)
            {
                Pin(s.F[1], 1000, 0);
                if (t == 10) InterruptShot(s, 0);   // Flags 0: not Destructible, the aura cannot eat it
                int hp = s.F[1].HpU;
                Game(s, t == 0 ? In(ChibiButtons.Skill2) : N, N);
                if (s.F[1].HpU < hp) hits.Add(t);
                if (t == 10) Assert.AreEqual(-1, s.F[0].MoveSlot, "precondition: Spin Guard interrupted on tick 10");
            }
            CollectionAssert.AreEqual(new[] { 0 }, hits, "Test 26: only hit 1 before the interrupt");
            var p = Shot(s, 1, s.F[0].X + 1000, s.F[0].Y + CenterY, ChibiProjectileSim.FDestructible);
            Pin(s.F[1], 1000, 0);
            Game(s, N, N);
            Assert.IsFalse(p.Active, "Test 26: aura still destroys a projectile on tick 60 (survives Interrupt, fact 9)");
        }

        // ================================================================== 27-29 Dispatch priority (D-M1 = A)

        // Test 27 -- F:320-330: Light beats a same-tick skill; Heavy beats a same-tick skill.
        [TestCase(ChibiButtons.Light | ChibiButtons.Skill1, 0)]
        [TestCase(ChibiButtons.Light | ChibiButtons.Skill3, 0)]
        [TestCase(ChibiButtons.Heavy | ChibiButtons.Skill2, 1)]
        [TestCase(ChibiButtons.Light | ChibiButtons.Heavy | ChibiButtons.Skill1, 0)]
        public void Priority_NormalAndSkillSameTick_NormalStarts(ChibiButtons b, int expectSlot)
        {
            var s = Arena(FighterClass.Melee, BloodEdge, 0, FighterClass.Melee, BloodEdge, 6000);
            Game(s, In(b), N);
            Assert.AreEqual(expectSlot, s.F[0].MoveSlot, $"D-M1 A: {b} starts slot {expectSlot}");
            Assert.AreEqual(0, s.F[0].Cd[0] + s.F[0].Cd[1] + s.F[0].Cd[2], "D-M1 A: no skill cooldown spent");
        }

        // Test 28 -- F:455: SpearOut + Light + Skill2 on the same tick: nothing starts this tick.
        [Test]
        public void Priority_SpearOutLightAndSkill2_NothingStarts()
        {
            var s = Arena(FighterClass.Polearm, PolearmP0, 0, FighterClass.Melee, BloodEdge, 6000);
            s.F[0].SpearOut = true;
            Game(s, In(ChibiButtons.Light | ChibiButtons.Skill2), N);
            Assert.AreEqual(-1, s.F[0].MoveSlot, "Test 28: spear-out normal press eats the tick");
            Assert.AreEqual(0, s.F[0].Cd[1], "Test 28: Spin Guard cooldown not spent");
        }

        // Test 29 -- F:320-326: during reload a Light press falls through to the skill.
        [Test]
        public void Priority_ReloadLightAndSkill1_SkillStarts()
        {
            var s = Arena(FighterClass.Ranged, RangedP0, 0, FighterClass.Melee, BloodEdge, 6000);
            s.F[0].SetCd = 50;
            Game(s, In(ChibiButtons.Light | ChibiButtons.Skill1), N);
            Assert.AreEqual(2, s.F[0].MoveSlot, "Test 29: reload falls through to Skill1");
        }

        // TD review "Ult in step 12": stand-in ult first only with a full meter; otherwise the press falls through.
        [TestCase(UltMax, 5)]
        [TestCase(UltMax - 1, 0)]
        public void Priority_UltAndLightSameTick_UltOnlyWhenMeterFull(int meter, int expectSlot)
        {
            // Heavy (Smash locks as slot 5). Not Melee: since step 14 (D-U2 = A) Sword Aura starts instantly with no lock,
            // so a Melee ult leaves MoveSlot -1 and would not show which press won.
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 6000);
            s.F[0].Ult = meter;
            Game(s, In(ChibiButtons.Ultimate | ChibiButtons.Light), N);
            Assert.AreEqual(expectSlot, s.F[0].MoveSlot, $"TD ult rule: meter {meter} -> slot {expectSlot}");
        }

        // ================================================================== 30 Locks (MoveLockEnd)

        // Test 30 -- sections 2.1-2.7, D-N1: a Light one tick before MoveLockEnd is lost, at MoveLockEnd it starts.
        // Leap Slam is covered by test 22 (D-M2 = B: slam + 12).
        [TestCase(FighterClass.Melee, BloodEdge, ChibiButtons.Skill3, 6000, LockThrust)]        // Thrust Barrage
        [TestCase(FighterClass.Heavy, HeavyP1, ChibiButtons.Skill1, 3000, LockGrabWhiff)]       // Grab whiff (R2)
        [TestCase(FighterClass.Heavy, HeavyP1, ChibiButtons.Skill1, 1000, LockGrabHit)]         // Grab hit (R5)
        [TestCase(FighterClass.Heavy, HeavyP1, ChibiButtons.Skill2, 6000, LockTornado)]         // Tornado (R6)
        [TestCase(FighterClass.Ranged, RangedP0, ChibiButtons.Skill1, -7000, LockBackstep)]     // Backstep (R8)
        [TestCase(FighterClass.Ranged, RangedP0, ChibiButtons.Skill3, -7000, LockBarrage)]      // Arrow Barrage (R9)
        [TestCase(FighterClass.Polearm, PolearmP0, ChibiButtons.Skill2, 6000, LockSpinGuard)]   // Spin Guard (R13)
        public void Lock_LightBeforeAndAtMoveLockEnd_LostThenStarts(FighterClass c, int passive, ChibiButtons skill,
                                                                    int victimX, int lockEnd)
        {
            var s = Arena(c, passive, 0, FighterClass.Melee, BloodEdge, victimX);
            ChibiSimState after0 = null;
            for (int t = 0; t <= lockEnd - 2; t++)
            {
                Game(s, t == 0 ? In(skill) : N, N);
                if (t == 0) after0 = Clone(s);
            }
            Assert.GreaterOrEqual(s.F[0].MoveSlot, 2, $"Test 30: {c} {skill} still locked after tick {lockEnd - 2}");
            var lost = Clone(s);
            Game(lost, In(ChibiButtons.Light), N);
            Assert.AreEqual(-1, lost.F[0].MoveSlot, $"Test 30: {c} {skill}: Light on tick {lockEnd - 1} lost, move ended");
            Game(s, N, N);
            Game(s, In(ChibiButtons.Light), N);
            Assert.AreEqual(0, s.F[0].MoveSlot, $"Test 30: {c} {skill}: Light on tick {lockEnd} starts");
            Assert.AreEqual(lockEnd, GetInt(after0.F[0], "MoveLockEnd"), "section 6: MoveLockEnd value");
        }

        // R2 -- grab whiff spends the cooldown.
        [Test]
        public void Grab_Whiff_SpendsCooldown()
        {
            var s = GrabArena(3000);
            Game(s, In(ChibiButtons.Skill1), N);
            Assert.AreEqual(0, s.F[1].Stun, "R2: whiff");
            Assert.Greater(s.F[0].Cd[0], 0, "R2: cooldown spent on a whiff");
            Assert.AreEqual(LockGrabWhiff, GetInt(s.F[0], "MoveLockEnd"), "R2: whiff MoveLockEnd 21");
        }

        // ================================================================== 31-32 Hash / copy / rollback

        // Test 31 -- section 6: each new field changes the checksum and survives CopyFrom.
        [TestCase("MoveLockEnd")]
        [TestCase("MovePhase")]
        [TestCase("Held")]
        [TestCase("Holding")]
        [TestCase("GrabSX")]
        [TestCase("GrabSY")]
        [TestCase("GrabTX")]
        [TestCase("GrabTY")]
        [TestCase("GrabLX")]
        [TestCase("GrabLY")]
        public void HashCopy_NewField_ChangesHashAndRoundTrips(string field)
        {
            var s = Arena(FighterClass.Heavy, HeavyP1, 0, FighterClass.Melee, BloodEdge, 1000);
            for (int i = 0; i < 2; i++)
            {
                var c = Clone(s);
                uint h0 = c.ComputeChecksum();
                var fi = Need(typeof(ChibiFighterSim), field);
                if (fi.FieldType == typeof(bool)) SetBool(c.F[i], field, !GetBool(c.F[i], field));
                else SetInt(c.F[i], field, GetInt(c.F[i], field) + 7);
                Assert.AreNotEqual(h0, c.ComputeChecksum(), $"Test 31: F[{i}].{field} not hashed");
                var d = Clone(c);
                Assert.AreEqual(fi.GetValue(c.F[i]), fi.GetValue(d.F[i]), $"Test 31: F[{i}].{field} not copied by CopyFrom");
                Assert.AreEqual(c.ComputeChecksum(), d.ComputeChecksum(), $"Test 31: hash after CopyFrom ({field})");
            }
        }

        static ChibiInputPacket GrabInput(int realTick, int pressTick) => realTick == pressTick ? In(ChibiButtons.Skill1) : N;

        // Test 32 -- section 6: save at game tick 10 of a grab, resimulate to 30: identical hash to a straight run.
        [Test]
        public void Rollback_SaveAtTick10ResimTo30_SameHash()
        {
            var a = GrabArena(1000);
            var b = Clone(a);
            ChibiSimState snap = null;
            for (int t = 0; t <= 30; t++)
            {
                if (t == 10) snap = Clone(a);
                ChibiSimulation.Step(a, GrabInput(t, 0), N);
            }
            for (int t = 0; t < 10; t++) ChibiSimulation.Step(b, GrabInput(t, 0), N);
            Assert.IsTrue(Held(b.F[1]), "precondition: victim held at tick 10");
            for (int t = 10; t <= 30; t++) ChibiSimulation.Step(b, N, In(ChibiButtons.Jump));   // wrong prediction
            b.CopyFrom(snap);
            for (int t = 10; t <= 30; t++) ChibiSimulation.Step(b, N, N);
            Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum(), "Test 32: resim from tick 10 equals the straight run");
        }

        // Amendment 8e -- remote grab press arrives late: mispredict across tick 0 and across tick 26 (holder Jump predicted
        // on ticks 24..28); the re-simulated hash equals a straight run. Real ticks (no hitstop before the slam).
        [TestCase(0)]
        [TestCase(24)]
        public void Rollback_LateRemoteGrabPress_ResimEqualsStraightRun(int mispredictFrom)
        {
            const int end = 60, pressTick = 2, lateBy = 4;
            var straight = Arena(FighterClass.Melee, BloodEdge, 1000, FighterClass.Heavy, HeavyP1, 0, -1);
            var live = Clone(straight);
            for (int t = 0; t <= end; t++)
            {
                ChibiSimulation.Step(straight, N, GrabInput(t, pressTick));
                if (t == 12) Assert.IsTrue(Held(straight.F[0]), "precondition: remote F1 grab holds F0 at tick 12");
            }
            ChibiSimState snap = null;
            int from = mispredictFrom == 0 ? 0 : mispredictFrom;
            for (int t = 0; t < from; t++) ChibiSimulation.Step(live, N, GrabInput(t, pressTick));
            snap = Clone(live);
            for (int t = from; t < from + lateBy + (mispredictFrom == 0 ? pressTick : 4); t++)
            {
                var predicted = mispredictFrom == 0 ? N : In(t % 2 == 0 ? ChibiButtons.Jump : ChibiButtons.None);
                ChibiSimulation.Step(live, N, predicted);
            }
            live.CopyFrom(snap);
            for (int t = from; t <= end; t++) ChibiSimulation.Step(live, N, GrabInput(t, pressTick));
            Assert.AreEqual(straight.ComputeChecksum(), live.ComputeChecksum(), $"A8e: resim from tick {from} equals straight run");
        }

        // ================================================================== config hash (A6 / 8i)

        /// <summary>Today's (v11) ChibiMatchConfig.ComputeHash, reproduced test-side.</summary>
        static uint HashV11(in ChibiMatchConfig c)
        {
            uint h = 2166136261u;
            ChibiSimState.Mix(ref h, 11);
            ChibiSimState.Mix(ref h, unchecked((int)c.Seed));
            ChibiSimState.Mix(ref h, (int)c.Class0); ChibiSimState.Mix(ref h, (int)c.Class1);
            ChibiSimState.Mix(ref h, c.Passive0); ChibiSimState.Mix(ref h, c.Passive1); ChibiSimState.Mix(ref h, c.Stage);
            ChibiSimState.Mix(ref h, ChibiSimConst.ScaleHitstop); ChibiSimState.Mix(ref h, ChibiSimConst.ScaleCinematic);
            ChibiSimState.Mix(ref h, ChibiSimConst.HitstopNormal); ChibiSimState.Mix(ref h, ChibiSimConst.HitstopCrit);
            ChibiSimState.Mix(ref h, ChibiSimConst.HitstopParry); ChibiSimState.Mix(ref h, ChibiSimConst.HitstopReflect);
            ChibiSimState.Mix(ref h, ChibiSimConst.CinematicTicks); ChibiSimState.Mix(ref h, ChibiSimConst.MaxProjectiles);
            return h;
        }

        // Amendment 6 / 8i -- RulesVersion 12 and the step-12 constants in the config hash: v11 != v12.
        [Test]
        public void ConfigHash_V12_DiffersFromV11()
        {
            var c = new ChibiMatchConfig { Seed = Seed, Class0 = FighterClass.Heavy, Class1 = FighterClass.Melee, Stage = SkyDojo };
            Assert.GreaterOrEqual(ChibiMatchConfig.RulesVersion, RulesVersion12, "section 6 item 1: RulesVersion at least 12");
            Assert.AreNotEqual(HashV11(c), c.ComputeHash(), "A8i: v11 config hash must not match v12");
        }
    }
}
