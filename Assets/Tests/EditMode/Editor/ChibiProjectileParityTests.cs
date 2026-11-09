// Story type: Logic (projectile kinds: circle hit, 2D homing, spear return, growth, pierce, harmless, volley split, pool)
// -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/projectile-conversion.md (port step 11, RulesVersion 11): section 2 per-update
// order, section 3 integer formulas + reference values H1-H3 / R1-R2, section 4 split, section 5 pool, section 6 contract,
// section 8 test table P1-P21, and the "Technical-director review" amendments A1-A8 (binding; P22 comes from A7).
// Original: Combat/CombatObjects.cs (CO:), Fighter/Fighter.cs (F:).
//
// User decisions applied: D-P1 = A (rejection sampling in the 0.6 circle, max 16 tries), D-P2 = A (MaxProjectiles 64,
// dropped-shot counter in state, A4), D-P3 = A (seeker orbs use the 2D homing; not exercised here, step 13 removes them).
//
// COMPILES TODAY, FAILS TODAY: the v11 members (enum ChibiProjKind; ChibiProjectileSim.Kind / Flags / Radius /
// GrowPerTick / Speed / ReturnTicks / ReturnMult; ChibiFighterSim.SpearOut; ChibiSimState.DroppedShots; the static helper
// SteerHoming) do not exist yet. They are reached ONLY through Need / GetInt / SetInt / GetBool / SetBool / KindOf /
// FindStatic below, which Assert.Fail("... missing") when absent. Nothing here names a v11 member at compile time.
//
// FIXTURE INJECTION (documented): machinery tests (circle hit, destroy radii, pierce, growth, harmless, split, kb sign)
// write a projectile straight into a pool slot (Place) with SpawnFrame = GameTicks - 1, so the next game step is its
// update 1. Spawn-table tests (P2, P3, P5-P9, P17, P21, P22) fire through the real input path instead.
//
// Tick convention: "update n" = the n-th game step (ChibiTestTime.StepGame) after the step that spawned the projectile;
// values are read at the END of that step. A despawned slot keeps its last field values (Despawn only clears Active).
//
// Arena: Sky Dojo (stage 0), main stage top Y 0, |X| <= 8000. Victim F[1] = Melee P1 (no ward / dodge / armour).
using System;
using System.Collections.Generic;
using System.Reflection;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Projectile parity (port step 11) between the deterministic 60 Hz simulation and CombatObjects.Projectile.
    /// Reference values: projectile-conversion.md sections 3-4 and 8; contract: section 6 + technical-director amendments.
    /// </summary>
    public class ChibiProjectileParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;
        const int SettleAfterPut = 3;
        const int SkyDojo = 0;
        const int MaxSpawnSearch = 60;                // game steps searched for the spawn of a fired projectile
        const int LongRun = 300;                      // upper bound on updates while waiting for a despawn

        // ---- passives (ChibiCombatStats.Create)
        const int RangedBase = 0, MagicBase = 0, HeavyBase = 1;   // no attack-speed effect (normals tests)
        const int PolearmArmorBreak = 0;              // b = 1 (no Patience)
        const int PolearmPatience = 1;                // b = 1.5 once SinceAttackTicks >= 180 (F:443-451)
        const int VictimPassive = 1;                  // Melee P1

        // ---- section 6 flags bitmask
        const int FHoming = 1, FPierce = 2, FHarmless = 4, FDestructible = 8;

        // ---- section 0 / SIM constants
        const int CenterY = 675;                      // Center = feet + 0.675 (F:32)
        const int HurtHalfW = 375;                    // SIM:52
        const int GuardRadius = 1600, RingRadius = 2100;   // F:525-526
        const int GuardHold = 100;                    // GuardTicks kept > 0 for the whole test
        const int RingHold = 101;                     // RingTicks after Dec = 100, 100 % 30 != 0: no ring damage pulse
        const int DashInvuln = 6;                     // dash i-frames (dash-conversion.md)
        const int MaxProjectiles = 64;                // D-P2 = A
        const int RulesVersion11 = 11;                // A8

        // ---- rows 1-9 / 11-13 of the section 1 table (sim units)
        const int ArrowR = 250, BoltBigR = 440, FireballR = 380, SpearR = 300, WaveR = 600;
        const int ArrowLightSpeed = 270000, ArrowBigSpeed = 315000, FireballSpeed = 135000, SpearSpeed = 240000;
        const int WaveSpeed = 195000;
        const int ArrowLife = 78, FireballLife = 180, SpearLife = 180, WaveLife = 66, LeadLife = 27, VolleyLife = 90;
        const int ArrowOffX = 500, ArrowOffY = 725;   // row 1/2/6
        const int BarrageBaseY = 675;                 // row 7: 675 + yOff, yOff in [-250, 300] (Range(-0.25, 0.3))
        const int BarrageYOffMin = -250, BarrageYOffMax = 300;
        const int FireballOffX = 600, FireballOffY = 975;  // row 8
        const int SpearOffX = 600, SpearOffY = 775;   // row 9
        const int WaveOffX = 600, WaveOffY = 550;     // row 11
        const int LeadOffX = 500, LeadOffY = 975, LeadVx = 135000, LeadVy = 180000;   // row 12
        const int WaveGrowPerTick = 14;               // section 3c: 600 * 1.4 / 60
        const int WaveRadiusAtEnd = 1524;             // r(66) = 600 + 14 * 66
        const int SpearReturnTicks = 27, SpearReturnSpeed = 270000, SpearArriveR = 500;
        const int VolleyCount = 18, VolleyDiscR = 600;
        const int VolleySpeedMin = 210000, VolleySpeedSpan = 75001;      // S = 210000 + Range(75001), 14..19 u/s
        const int VolleyOxSpan = 5001, VolleyOxHalf = 2500;              // Range(-2.5, 2.5)
        const int VolleyOySpan = 1001, VolleyOyHalf = 500;               // Range(-0.5, 0.5)
        const int VolleyCSpan = 1201, VolleyCHalf = 600, VolleyMaxTries = 16;   // D-P1 = A
        const int VolleyMult = 450, VolleyKbX = 22500, VolleyKbY = 15000, VolleyStun = 9;

        // ---- formula tolerances (section 3b: +-1 sub per component; section 8 P15: |V| within +-2 of S)
        const int VelTol = 1;
        const int SpeedTol = 2;
        const long PreScaleLimit = 1L << 20;          // A3: pre-scale x1024 when max(|dx|, |dy|) < 2^20
        const int PreScale = 1024;

        static readonly ChibiInputPacket Neutral = default;

        // ================================================================== reflection access to v11 members

        const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags Stat = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        static FieldInfo Need(Type t, string name)
        {
            var fi = t.GetField(name, Inst);
            if (fi == null) Assert.Fail($"field {t.Name}.{name} missing (projectile-conversion.md section 6 / TD review, RulesVersion 11)");
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

        static int ConstOf(Type t, string name)
        {
            var fi = t.GetField(name, Stat);
            if (fi == null) Assert.Fail($"constant {t.Name}.{name} missing");
            return Convert.ToInt32(fi.GetValue(null));
        }

        static Type KindEnum()
        {
            var t = typeof(ChibiSimulation).Assembly.GetType("ChibiClash.Netcode.ChibiProjKind");
            if (t == null) Assert.Fail("enum ChibiClash.Netcode.ChibiProjKind missing (projectile-conversion.md section 6)");
            return t;
        }

        static int KindOf(string name)
        {
            var t = KindEnum();
            if (!Enum.IsDefined(t, name)) Assert.Fail($"ChibiProjKind.{name} missing (section 6 enum list)");
            return Convert.ToInt32(Enum.Parse(t, name));
        }

        static MethodInfo FindStatic(string name)
        {
            foreach (var t in typeof(ChibiSimulation).Assembly.GetTypes())
            {
                if (t.Namespace != "ChibiClash.Netcode") continue;
                var m = t.GetMethod(name, Stat);
                if (m != null) return m;
            }
            Assert.Fail($"static helper {name} missing (projectile-conversion.md section 3, 'pure static, unit-testable')");
            return null;
        }

        static int Dropped(ChibiSimState s) => GetInt(s, "DroppedShots");          // A4
        static bool SpearOut(ChibiFighterSim f) => GetBool(f, "SpearOut");           // section 6

        // ================================================================== reference formulas (section 3 + A3), test-side

        static int DivRoundL(long n, long d) => (int)(n >= 0 ? (n + d / 2) / d : -((-n + d / 2) / d));

        /// <summary>Section 3 ScaleTo with the A3 pre-scale: (DivRound(dx*S, L), DivRound(dy*S, L)), L = ISqrt; L == 0 -> 0.</summary>
        static void ScaleToRef(long dx, long dy, int speed, out int rx, out int ry)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < PreScaleLimit) { dx *= PreScale; dy *= PreScale; }
            long l = ChibiFixed.ISqrt(dx * dx + dy * dy);
            if (l == 0) { rx = 0; ry = 0; return; }
            rx = DivRoundL(dx * speed, l);
            ry = DivRoundL(dy * speed, l);
        }

        /// <summary>Section 3b: D = ScaleTo(to target, S); B = 113 V + 7 D; V = ScaleTo(B, S).</summary>
        static void SteerRef(ref int vx, ref int vy, int speed, int dx, int dy)
        {
            ScaleToRef(dx, dy, speed, out int ddx, out int ddy);
            long bx = 113L * vx + 7L * ddx, by = 113L * vy + 7L * ddy;
            ScaleToRef(bx, by, speed, out vx, out vy);
        }

        // ================================================================== match fixture

        static ChibiSimState NewMatch(FighterClass c0, int p0)
        {
            var cfg = new ChibiMatchConfig
            {
                Seed = Seed, Class0 = c0, Passive0 = p0, Class1 = FighterClass.Melee, Passive1 = VictimPassive, Stage = SkyDojo,
            };
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            for (int t = 0; t < SettleTicks; t++) ChibiSimulation.Step(s, Neutral, Neutral);
            return s;
        }

        /// <summary>Fighter at rest, grounded on a surface with top <paramref name="y"/>, free to act.</summary>
        static void Put(ChibiFighterSim f, int x, int y, int facing)
        {
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0;
            f.Grounded = true; f.AirJumps = 1; f.Facing = facing; f.Stun = 0; f.Invuln = 0;
        }

        /// <summary>Attacker F[0] at (ax, 0) facing <paramref name="facing"/>; victim F[1] at (tx, ty) facing the other way.</summary>
        static ChibiSimState Arena(FighterClass c, int passive, int ax, int tx, int ty = 0, int facing = 1)
        {
            var s = NewMatch(c, passive);
            Put(s.F[0], ax, 0, facing);
            Put(s.F[1], tx, ty, -facing);
            for (int t = 0; t < SettleAfterPut; t++) ChibiSimulation.Step(s, Neutral, Neutral);
            return s;
        }

        static Action Hold(ChibiSimState s, int x, int y) => () => Put(s.F[1], x, y, -1);

        static void Update(ChibiSimState s, Action before = null)
        {
            before?.Invoke();
            ChibiTestTime.StepGame(s, Neutral, Neutral);
        }

        static ChibiInputPacket Press(ChibiButtons b) => ChibiInputPacket.Make(ChibiDir.Neutral, b);

        /// <summary>
        /// Presses <paramref name="b"/> on F[0] on the first game step, then steps neutrally until an F[0] projectile appears
        /// with SpawnFrame == the step just run. Returns its slot; <paramref name="feetX"/>/<paramref name="feetY"/> = F[0]'s
        /// feet at the start of the spawn step (spawn happens in AdvanceMove, before that step's Physics).
        /// </summary>
        static int Fire(ChibiSimState s, ChibiButtons b, Action before, out int feetX, out int feetY)
        {
            for (int t = 0; t < MaxSpawnSearch; t++)
            {
                before?.Invoke();
                feetX = s.F[0].X; feetY = s.F[0].Y;
                ChibiTestTime.StepGame(s, t == 0 ? Press(b) : Neutral, Neutral);
                for (int n = 0; n < s.Proj.Length; n++)
                {
                    var p = s.Proj[n];
                    if (p.Active && p.Owner == 0 && p.SpawnFrame == s.GameTicks - 1) return n;
                }
            }
            Assert.Fail($"precondition: {b} spawned no projectile within {MaxSpawnSearch} game steps");
            feetX = feetY = 0;
            return -1;
        }

        static int Fire(ChibiSimState s, ChibiButtons b, Action before = null) => Fire(s, b, before, out _, out _);

        sealed class Spec
        {
            public string Kind = "Arrow";
            public int Owner, X, Y, Vx, Vy, Radius = ArrowR, Life = ArrowLife, Mult = 1000, KbX = 22500, KbY = 18000, Stun = 17;
            public int Flags = FDestructible, Grow, ReturnTicks, ReturnMult;
        }

        /// <summary>Writes a projectile into slot <paramref name="slot"/> as if spawned on the previous game step (fixture injection).</summary>
        static ChibiProjectileSim Place(ChibiSimState s, int slot, Spec sp)
        {
            var p = s.Proj[slot];
            SetInt(p, "Kind", KindOf(sp.Kind));
            SetInt(p, "Flags", sp.Flags);
            SetInt(p, "Radius", sp.Radius);
            SetInt(p, "GrowPerTick", sp.Grow);
            SetInt(p, "Speed", (int)ChibiFixed.ISqrt((long)sp.Vx * sp.Vx + (long)sp.Vy * sp.Vy));
            SetInt(p, "ReturnTicks", sp.ReturnTicks);
            SetInt(p, "ReturnMult", sp.ReturnMult);
            p.Active = true; p.Owner = sp.Owner; p.X = sp.X; p.Y = sp.Y; p.Vx = sp.Vx; p.Vy = sp.Vy; p.XRem = 0; p.YRem = 0;
            p.Life = sp.Life; p.Mult = sp.Mult; p.KbX = sp.KbX; p.KbY = sp.KbY; p.Stun = sp.Stun;
            p.SpawnFrame = s.GameTicks - 1;
            return p;
        }

        static int FreeSlot(ChibiSimState s)
        {
            for (int n = 0; n < s.Proj.Length; n++) if (!s.Proj[n].Active) return n;
            Assert.Fail("precondition: no free projectile slot");
            return -1;
        }

        static ChibiSimState Clone(ChibiSimState s)
        {
            var c = new ChibiSimState();
            c.CopyFrom(s);
            return c;
        }

        // ================================================================== P1 circle vs fighter AABB

        // P1 -- section 3a worked example; F:514-519 (inclusive <=).
        [TestCase(2375, true)]
        [TestCase(2374, false)]
        public void P01_CircleVsBox_InclusiveAtRadius(int px, bool expectHit)
        {
            const int tx = 3000, py = 725;                     // target feet (3000, 0); point y 725 inside the box height
            var s = Arena(FighterClass.Ranged, RangedBase, -7500, tx);
            var p = Place(s, 0, new Spec { X = px, Y = py, Vx = 0, Vy = 0 });   // stationary probe, r 250
            int hp = s.F[1].HpU;
            Update(s, Hold(s, tx, 0));
            Assert.AreEqual(expectHit, s.F[1].HpU < hp, $"P1: r 250 at ({px}, {py}) vs box edge {tx - HurtHalfW}: hit");
            Assert.AreEqual(!expectHit, p.Active, "P1: a hitting non-pierce arrow is despawned, a miss stays");
        }

        // ================================================================== P2 / P3 hit tick of normals

        static int HitUpdate(ChibiSimState s, int slot, int tx, out int xAtHit)
        {
            for (int n = 1; n <= ArrowLife; n++)
            {
                int hp = s.F[1].HpU;
                Update(s, Hold(s, tx, 0));
                if (s.F[1].HpU < hp) { xAtHit = s.Proj[slot].X; return n; }
            }
            xAtHit = 0;
            return -1;
        }

        // P2 -- section 8 P2, F:482-484: heavy bolt r 0.55 x 0.8 = 440 hits on update 7 at X 2233 (old AABB: 9).
        [Test]
        public void P02_HeavyBolt_HitsOnUpdate7()
        {
            const int tx = 3000, expectUpdate = 7, expectX = 2233;
            var s = Arena(FighterClass.Magic, MagicBase, 0, tx);
            int slot = Fire(s, ChibiButtons.Heavy, Hold(s, tx, 0));
            int n = HitUpdate(s, slot, tx, out int x);
            Assert.AreEqual(expectUpdate, n, "P2: heavy bolt hit update (r 440 circle; 9 = old 150 x 100 AABB)");
            Assert.AreEqual(expectX, x, "P2: bolt X on the hit update");
            Assert.AreEqual(BoltBigR, GetInt(s.Proj[slot], "Radius"), "P2: heavy bolt Radius (row 4)");
        }

        // P3 -- section 8 P3, F:538-539: light arrow r 250 hits on update 7 at X 2600.
        [Test]
        public void P03_LightArrow_HitsOnUpdate7()
        {
            const int tx = 3000, expectUpdate = 7, expectX = 2600;
            var s = Arena(FighterClass.Ranged, RangedBase, 0, tx);
            int slot = Fire(s, ChibiButtons.Light, Hold(s, tx, 0));
            int n = HitUpdate(s, slot, tx, out int x);
            Assert.AreEqual(expectUpdate, n, "P3: light arrow hit update");
            Assert.AreEqual(expectX, x, "P3: arrow X on the hit update");
            Assert.AreEqual(ArrowR, GetInt(s.Proj[slot], "Radius"), "P3: light arrow Radius (row 1)");
        }

        // ================================================================== P4 / P5 / P6 homing

        // P4 -- section 3b H1, CO:54: SteerHoming(135000, 0, S 135000, dx 0, dy +1000) -> (134742, 8347).
        [Test]
        public void P04_SteerHoming_H1()
        {
            var m = FindStatic("SteerHoming");
            var ps = m.GetParameters();
            if (ps.Length != 5 || !ps[0].ParameterType.IsByRef || !ps[1].ParameterType.IsByRef)
                Assert.Fail($"P4: SteerHoming signature {m} -- expected (ref vx, ref vy, speed, dx, dy) per section 2 line 86");
            int[] raw = { 135000, 0, 135000, 0, 1000 };
            var args = new object[5];
            for (int i = 0; i < 5; i++)
            {
                var pt = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType;
                args[i] = Convert.ChangeType(raw[i], pt);
            }
            m.Invoke(null, args);
            Assert.AreEqual(134742, Convert.ToInt32(args[0]), "P4/H1: Vx");
            Assert.AreEqual(8347, Convert.ToInt32(args[1]), "P4/H1: Vy");
        }

        // P5 -- section 3b H2, CO:51-55, F:880: Mage feet (0,0) F+1, target (3000,0): spawn (600, 975) V (135000, 0);
        // update 1 -> V (134996, -977), pos (749, 973), XRem 896, YRem 823.
        [Test]
        public void P05_Fireball_H2_FirstUpdate()
        {
            const int tx = 3000;
            var s = Arena(FighterClass.Magic, MagicBase, 0, tx);
            int slot = Fire(s, ChibiButtons.Skill1, Hold(s, tx, 0));
            var p = s.Proj[slot];
            Assert.AreEqual(FireballOffX, p.X, "P5: fireball spawn X (row 8: 600)");
            Assert.AreEqual(FireballOffY, p.Y, "P5: fireball spawn Y (row 8: 975)");
            Assert.AreEqual(FireballSpeed, p.Vx, "P5: fireball spawn Vx (9 u/s)");
            Update(s, Hold(s, tx, 0));
            Assert.AreEqual(134996, p.Vx, "P5/H2: Vx after update 1");
            Assert.AreEqual(-977, p.Vy, "P5/H2: Vy after update 1");
            Assert.AreEqual(749, p.X, "P5/H2: X after update 1");
            Assert.AreEqual(973, p.Y, "P5/H2: Y after update 1");
            Assert.AreEqual(896, p.XRem, "P5/H2: XRem");
            Assert.AreEqual(823, p.YRem, "P5/H2: YRem");
        }

        // P6 -- section 3b H3, CO:47, CO:108, F:881: |Vx^2 + Vy^2 - S^2| <= 2S + 2 every update; Life 180 -> despawn on
        // update 180. Target held at X -16000 behind a Mage at +7500: the fireball cannot reach it in 180 updates.
        [Test]
        public void P06_Fireball_SpeedInvariant_DespawnOn180()
        {
            const int ax = 7500, tx = -16000;
            var s = Arena(FighterClass.Magic, MagicBase, ax, tx);
            var hold = Hold(s, tx, 0);
            int slot = Fire(s, ChibiButtons.Skill1, hold);
            var p = s.Proj[slot];
            long s2 = (long)FireballSpeed * FireballSpeed, bound = 2L * FireballSpeed + 2;
            for (int n = 1; n <= FireballLife; n++)
            {
                Update(s, hold);
                long err = Math.Abs((long)p.Vx * p.Vx + (long)p.Vy * p.Vy - s2);
                Assert.LessOrEqual(err, bound, $"P6/H3: speed invariant broken on update {n}, V ({p.Vx}, {p.Vy})");
                if (n < FireballLife) Assert.IsTrue(p.Active, $"P6: fireball despawned early on update {n} (expected 180)");
            }
            Assert.IsFalse(p.Active, "P6: fireball still active after update 180 (Life 180)");
        }

        // ================================================================== P7 / P8 / P9 spear

        // P7 -- section 3c R1, CO:57-72, F:455, F:938-949. Polearm (0,0) F+1 still; opponent at X -5000.
        // X 7533 / XRem 300 after update 26; return V on update 27; despawn on update 51 with Life 129; SpearOut false after
        // 51; a light press on update 51 is ignored, on 52 accepted.
        // NOTE: doc R1 says V = (-270000, -3584) on update 27. That value is without the A3 pre-scale (L = ISqrt(56756089)
        // = 7533 exactly matches dx). With A3 (binding) L' = ISqrt(1024^2 * 56756089) = 7714472 and Vx = -269976. The test
        // uses the A3 value; see the report.
        [Test]
        public void P07_SpearReturn_R1()
        {
            const int ox = 0, enemyX = -5000;
            const int x26 = 7533, xRem26 = 300, y26 = 775;
            const int vx27 = -269976, vy27 = -3584;          // A3 (doc R1 without A3: -270000, -3584)
            const int x27 = 7233, y27 = 771;
            const int despawnUpdate = 51, lifeAtDespawn = 129;
            var s = Arena(FighterClass.Polearm, PolearmArmorBreak, ox, enemyX);
            int slot = Fire(s, ChibiButtons.Skill3);
            var p = s.Proj[slot];
            Assert.AreEqual(SpearOffX, p.X, "P7: spear spawn X (row 9: 600)");
            Assert.AreEqual(SpearOffY, p.Y, "P7: spear spawn Y (row 9: 775)");
            Assert.AreEqual(SpearSpeed, p.Vx, "P7: spear spawn Vx (16 u/s)");
            Assert.IsTrue(SpearOut(s.F[0]), "P7: SpearOut set by the spawn");
            for (int n = 1; n <= 26; n++) Update(s);
            Assert.AreEqual(x26, p.X, "P7/R1: X after update 26");
            Assert.AreEqual(xRem26, p.XRem, "P7/R1: XRem after update 26");
            Assert.AreEqual(y26, p.Y, "P7/R1: Y after update 26");
            Update(s);
            Assert.AreEqual(vx27, p.Vx, "P7/R1: return Vx on update 27 (A3 ScaleTo)");
            Assert.AreEqual(vy27, p.Vy, "P7/R1: return Vy on update 27");
            Assert.AreEqual(x27, p.X, "P7/R1: X after update 27");
            Assert.AreEqual(y27, p.Y, "P7/R1: Y after update 27");
            for (int n = 28; n < despawnUpdate; n++)
            {
                Update(s);
                Assert.IsTrue(p.Active, $"P7: spear gone on update {n} (expected {despawnUpdate})");
                Assert.IsTrue(SpearOut(s.F[0]), $"P7: SpearOut cleared early on update {n}");
            }
            var at50 = Clone(s);

            // update 51 with a Light press: spear arrives (pre-move distance < 500), press ignored (F:455)
            ChibiTestTime.StepGame(s, Press(ChibiButtons.Light), Neutral);
            Assert.IsFalse(p.Active, $"P7: spear still active after update {despawnUpdate}");
            Assert.AreEqual(lifeAtDespawn, p.Life, "P7/R1: Life at despawn (180 - 51)");
            Assert.IsFalse(SpearOut(s.F[0]), "P7: SpearOut still set after the spear despawned");
            Assert.AreEqual(-1, s.F[0].MoveSlot, "P7: light pressed on update 51 must be ignored (SpearOut still set during UpdateFighter)");

            // update 51 neutral, Light on 52: accepted
            ChibiTestTime.StepGame(at50, Neutral, Neutral);
            ChibiTestTime.StepGame(at50, Press(ChibiButtons.Light), Neutral);
            Assert.AreEqual(0, at50.F[0].MoveSlot, "P7: light pressed on update 52 must start");
        }

        // P8 -- section 8 P8, CO:63-64, CO:101-104: target held at (2000, 0). Polearm P1 with Patience (b = 1.5): one
        // outbound hit at 1400 b = 2100, keeps flying, no second outbound hit; one hit after update 27 at 800 b = 1200
        // with knockback X toward the owner (negative).
        [Test]
        public void P08_SpearHitsOutAndBack()
        {
            const int tx = 2000, outMult = 2100, retMult = 1200;
            var s = Arena(FighterClass.Polearm, PolearmPatience, 0, tx);
            var hold = Hold(s, tx, 0);
            int slot = Fire(s, ChibiButtons.Skill3, hold);
            var p = s.Proj[slot];
            Assert.AreEqual(outMult, p.Mult, "P8: spear Mult = 1400 x b (b = 1.5, Patience)");
            int outHits = 0, backHits = 0;
            for (int n = 1; n <= LongRun && p.Active; n++)
            {
                int hp = s.F[1].HpU;
                Update(s, hold);
                if (s.F[1].HpU >= hp) continue;
                if (n < SpearReturnTicks)
                {
                    outHits++;
                    Assert.AreEqual(outMult, p.Mult, $"P8: outbound hit on update {n} Mult");
                    Assert.IsTrue(p.Active, $"P8: spear despawned by its outbound hit on update {n} (ReturnTicks > 0 keeps it)");
                }
                else
                {
                    backHits++;
                    Assert.AreEqual(retMult, p.Mult, $"P8: return hit on update {n} Mult = 800 x b");
                    Assert.Less(s.F[1].Vx, 0, $"P8: return hit on update {n} knockback X must point toward the owner");
                }
            }
            Assert.AreEqual(1, outHits, "P8: outbound hits");
            Assert.AreEqual(1, backHits, "P8: return hits");
            Assert.AreEqual(retMult, GetInt(p, "ReturnMult"), "P8: ReturnMult = 800 x b");
        }

        // P9 -- section 8 P9, F:942 (!Destructible), F:524: the spear is never eaten by the target's Spin Guard or Fire
        // Ring; its life is exactly the R1 path (despawn at the owner on update 51).
        [TestCase(true)]
        [TestCase(false)]
        public void P09_Spear_NotDestroyedByGuardOrRing(bool guard)
        {
            const int tx = 4000, despawnUpdate = 51;
            var s = Arena(FighterClass.Polearm, PolearmArmorBreak, 0, tx);
            Action before = () =>
            {
                Put(s.F[1], tx, 0, -1);
                if (guard) s.F[1].GuardTicks = GuardHold; else s.F[1].RingTicks = RingHold;
            };
            int slot = Fire(s, ChibiButtons.Skill3, before);
            var p = s.Proj[slot];
            string what = guard ? "Spin Guard" : "Fire Ring";
            for (int n = 1; n < despawnUpdate; n++)
            {
                Update(s, before);
                Assert.IsTrue(p.Active, $"P9: spear destroyed on update {n} by the target's {what}");
            }
            Update(s, before);
            Assert.IsFalse(p.Active, $"P9: spear must reach the owner on update {despawnUpdate}");
        }

        // ================================================================== P10 / P11 destroy radii

        // P10 -- F:525: Spin Guard eats a Destructible shot when |pos - (T.X, T.Y + 675)| < 1600 (strict), after the move.
        // The probe sits straight above the centre, which also separates Y+675 from today's Y+650.
        [TestCase(GuardRadius, true)]
        [TestCase(GuardRadius - 1, false)]
        public void P10_SpinGuard_StrictRadiusFromCenter(int dist, bool survives) => DestroyRadius(true, dist, survives);

        // P11 -- F:526: Fire Ring, radius 2100, same rule.
        [TestCase(RingRadius, true)]
        [TestCase(RingRadius - 1, false)]
        public void P11_FireRing_StrictRadiusFromCenter(int dist, bool survives) => DestroyRadius(false, dist, survives);

        static void DestroyRadius(bool guard, int dist, bool survives)
        {
            const int tx = 3000;
            var s = Arena(FighterClass.Ranged, RangedBase, -7500, tx);
            var p = Place(s, 0, new Spec { X = tx, Y = CenterY + dist });
            int hp = s.F[1].HpU;
            Update(s, () =>
            {
                Put(s.F[1], tx, 0, -1);
                if (guard) s.F[1].GuardTicks = GuardHold; else s.F[1].RingTicks = RingHold;
            });
            Assert.AreEqual(survives, p.Active, $"{(guard ? "P10" : "P11")}: arrow {dist} above the target centre: survives");
            Assert.AreEqual(hp, s.F[1].HpU, "probe must not hit (it is outside the 250 circle)");
        }

        // ================================================================== P12 / P13 growth + pierce

        static Spec Wave(int dir) => new Spec
        {
            Kind = "SmashWave", X = dir * WaveOffX, Y = WaveOffY, Vx = dir * WaveSpeed, Radius = WaveR, Life = WaveLife,
            Mult = 3000, KbX = 90000, KbY = 180000, Stun = 48, Flags = FPierce, Grow = WaveGrowPerTick,
        };

        // P12 -- section 3c R2, CO:77, CO:97, CO:104, F:1000-1003: Heavy (0,0), target held at (4000,0). Hit on update 11
        // only, wave stays active after it; Radius = 600 + 14 n each update; 1524 on update 66, gone after it.
        [Test]
        public void P12_SmashWave_GrowsPiercesOnceExpires()
        {
            const int tx = 4000, hitUpdate = 11;
            var s = Arena(FighterClass.Heavy, HeavyBase, 0, tx);
            var p = Place(s, 0, Wave(1));
            var hits = new List<int>();
            for (int n = 1; n <= WaveLife; n++)
            {
                int hp = s.F[1].HpU;
                Update(s, Hold(s, tx, 0));
                if (s.F[1].HpU < hp) hits.Add(n);
                Assert.AreEqual(WaveR + WaveGrowPerTick * n, GetInt(p, "Radius"), $"P12: Radius after update {n}");
                if (n < WaveLife) Assert.IsTrue(p.Active, $"P12: wave gone on update {n} (Pierce, Life 66)");
            }
            CollectionAssert.AreEqual(new[] { hitUpdate }, hits, "P12/R2: hit updates (first at 11 thanks to growth, never again)");
            Assert.AreEqual(WaveRadiusAtEnd, GetInt(p, "Radius"), "P12: Radius on update 66");
            Assert.IsFalse(p.Active, "P12: wave active after update 66");
        }

        // P13 -- CO:89, F:997: two waves (dir -1 / +1) and the target standing on the Heavy: each wave hits once.
        [Test]
        public void P13_TwoSmashWaves_EachHitsOnce()
        {
            var s = Arena(FighterClass.Heavy, HeavyBase, 0, 0);
            var a = Place(s, 0, Wave(-1));
            var b = Place(s, 1, Wave(1));
            int landed0 = s.F[0].HitsLanded;
            Update(s, Hold(s, 0, 0));
            Assert.AreEqual(2, s.F[0].HitsLanded - landed0, "P13: hits after update 1 (one per wave)");
            Assert.IsTrue(a.Active && b.Active, "P13: piercing waves survive their hit");
            for (int n = 2; n <= WaveLife; n++) Update(s, Hold(s, 0, 0));
            Assert.AreEqual(2, s.F[0].HitsLanded - landed0, "P13: total hits over the waves' life");
        }

        // ================================================================== P14 / P15 / P16 Thousand Arrows split

        static Spec Lead() => new Spec
        {
            Kind = "VolleyLead", X = LeadOffX, Y = LeadOffY, Vx = LeadVx, Vy = LeadVy, Radius = 0, Life = LeadLife,
            Mult = 0, KbX = 0, KbY = 0, Stun = 0, Flags = FHarmless,
        };

        // V1 (section 4): (500, 975) + (135000, 180000) x 27 / 900 = (4550, 6375) exactly.
        const int SplitX = LeadOffX + LeadVx / 900 * LeadLife, SplitY = LeadOffY + LeadVy / 900 * LeadLife;

        static List<ChibiProjectileSim> SpawnedThisStep(ChibiSimState s)
        {
            var list = new List<ChibiProjectileSim>();
            foreach (var p in s.Proj) if (p.Active && p.SpawnFrame == s.GameTicks - 1) list.Add(p);
            return list;
        }

        // P14 -- F:1015-1019, CO:110: lead from feet (0,0) F+1 splits on update 27 at (4550, 6375) into 18 VolleyArrows,
        // none of which moves on that step (SpawnFrame = that step, XRem = YRem = 0, inside the 0.6 disc).
        [Test]
        public void P14_Lead_SplitsOnUpdate27()
        {
            const int tx = 3000;
            var s = Arena(FighterClass.Ranged, RangedBase, 0, tx);
            var lead = Place(s, 0, Lead());
            int leadKind = KindOf("VolleyLead"), arrowKind = KindOf("VolleyArrow");
            for (int n = 1; n < LeadLife; n++)
            {
                Update(s, Hold(s, tx, 0));
                Assert.IsTrue(lead.Active && GetInt(lead, "Kind") == leadKind, $"P14: lead gone on update {n}");
            }
            Update(s, Hold(s, tx, 0));
            var arrows = SpawnedThisStep(s);
            Assert.AreEqual(VolleyCount, arrows.Count, "P14: arrows spawned on update 27");
            foreach (var p in s.Proj)
                if (p.Active) Assert.AreNotEqual(leadKind, GetInt(p, "Kind"), "P14: lead still active after update 27");
            foreach (var p in arrows)
            {
                Assert.AreEqual(arrowKind, GetInt(p, "Kind"), "P14: split arrow Kind");
                long dx = p.X - SplitX, dy = p.Y - SplitY;
                Assert.LessOrEqual(dx * dx + dy * dy, (long)VolleyDiscR * VolleyDiscR,
                    $"P14: arrow at ({p.X}, {p.Y}) outside the 0.6 disc around the split point ({SplitX}, {SplitY})");
                Assert.AreEqual(0, p.XRem, "P14: arrow moved on its spawn step (XRem)");
                Assert.AreEqual(0, p.YRem, "P14: arrow moved on its spawn step (YRem)");
                Assert.AreEqual(ArrowR, VolleyR(p), "P14: arrow Radius 250");
                Assert.AreEqual(VolleyLife, p.Life, "P14: arrow Life 90");
            }
        }

        static int VolleyR(ChibiProjectileSim p) => GetInt(p, "Radius");

        sealed class ExpectedArrow { public int X, Y, Vx, Vy, S; }

        /// <summary>Section 4 draw order (A5: 5 + 2 extra draws per arrow): ox, oy, (cx, cy)+, S.</summary>
        static List<ExpectedArrow> ReplaySplit(uint rngState, int lx, int ly, int tX, int tY, out uint endState)
        {
            var rng = new ChibiRng { State = rngState };
            var list = new List<ExpectedArrow>();
            for (int k = 0; k < VolleyCount; k++)
            {
                int ox = rng.Range(VolleyOxSpan) - VolleyOxHalf;
                int oy = rng.Range(VolleyOySpan) - VolleyOyHalf;
                int cx = 0, cy = 0;
                bool ok = false;
                for (int tries = 0; tries < VolleyMaxTries && !ok; tries++)
                {
                    cx = rng.Range(VolleyCSpan) - VolleyCHalf;
                    cy = rng.Range(VolleyCSpan) - VolleyCHalf;
                    ok = (long)cx * cx + (long)cy * cy <= (long)VolleyCHalf * VolleyCHalf;
                }
                if (!ok) { cx = 0; cy = 0; }
                int S = VolleySpeedMin + rng.Range(VolleySpeedSpan);
                int fx = lx + cx, fy = ly + cy;
                ScaleToRef(tX + ox - fx, tY + CenterY + oy - fy, S, out int vx, out int vy);
                list.Add(new ExpectedArrow { X = fx, Y = fy, Vx = vx, Vy = vy, S = S });
            }
            endState = rng.State;
            return list;
        }

        // P15 -- F:1024-1027, section 4, A3, A5: with a fixed seed the 18 arrows follow the draw order ox, oy, (cx, cy)+, S
        // from s.Rng (RNG state after the step = the replay's; no hard-coded state), positions exact, V = ScaleTo +-1,
        // |V| within +-2 of S, S in [210000, 285000]; a second run from the same snapshot gives identical arrows.
        [Test]
        public void P15_Split_FixedSeed_DrawOrderAndReplay()
        {
            const int tx = 3000;
            var s = Arena(FighterClass.Ranged, RangedBase, 0, tx);
            Place(s, 0, Lead());
            for (int n = 1; n < LeadLife; n++) Update(s, Hold(s, tx, 0));
            var pre = Clone(s);
            uint rng0 = s.Rng.State;
            Update(s, Hold(s, tx, 0));
            var got = SpawnedThisStep(s);
            Assert.AreEqual(VolleyCount, got.Count, "P15: arrows spawned on update 27");
            var want = ReplaySplit(rng0, SplitX, SplitY, s.F[1].X, s.F[1].Y, out uint rngEnd);
            Assert.AreEqual(rngEnd, s.Rng.State, "P15/A5: RNG state after the split != replay of ox, oy, (cx, cy)+, S x 18");
            for (int k = 0; k < VolleyCount; k++)
            {
                var p = got[k]; var e = want[k];
                Assert.AreEqual(e.X, p.X, $"P15: arrow {k} X");
                Assert.AreEqual(e.Y, p.Y, $"P15: arrow {k} Y");
                Assert.That(p.Vx, Is.InRange(e.Vx - VelTol, e.Vx + VelTol), $"P15: arrow {k} Vx (ScaleTo, A3)");
                Assert.That(p.Vy, Is.InRange(e.Vy - VelTol, e.Vy + VelTol), $"P15: arrow {k} Vy (ScaleTo, A3)");
                long mag = ChibiFixed.ISqrt((long)p.Vx * p.Vx + (long)p.Vy * p.Vy);
                Assert.LessOrEqual(Math.Abs(mag - e.S), SpeedTol, $"P15: arrow {k} |V| {mag} vs S {e.S}");
                Assert.That(e.S, Is.InRange(VolleySpeedMin, VolleySpeedMin + VolleySpeedSpan - 1), "P15: S range");
                Assert.AreEqual(VolleyMult, p.Mult, $"P15: arrow {k} Mult");
                Assert.AreEqual(VolleyKbX, p.KbX, $"P15: arrow {k} KbX");
                Assert.AreEqual(VolleyKbY, p.KbY, $"P15: arrow {k} KbY");
                Assert.AreEqual(VolleyStun, p.Stun, $"P15: arrow {k} Stun");
                Assert.AreEqual(FDestructible, GetInt(p, "Flags"), $"P15: arrow {k} Flags = Destructible");
            }
            Update(pre, Hold(pre, tx, 0));
            Assert.AreEqual(s.ComputeChecksum(), pre.ComputeChecksum(), "P15: replay of the same snapshot differs");
        }

        // P16 -- CO:89, F:1017-1018: the Harmless lead passes through a standing target with an active Fire Ring: no hit,
        // no damage, not destroyed (updates 1..26, before it splits).
        [Test]
        public void P16_Lead_HarmlessAndNotDestroyed()
        {
            const int tx = 1000;                               // lead (650, 1175) on update 1 is inside the target box
            var s = Arena(FighterClass.Ranged, RangedBase, -7000, tx);
            var lead = Place(s, 0, Lead());
            int hp = s.F[1].HpU;
            Action before = () => { Put(s.F[1], tx, 0, -1); s.F[1].RingTicks = RingHold; };
            for (int n = 1; n < LeadLife; n++)
            {
                Update(s, before);
                Assert.IsTrue(lead.Active, $"P16: lead removed on update {n} (Harmless: never hit-tested or destroyed)");
                Assert.AreEqual(hp, s.F[1].HpU, $"P16: lead damaged the target on update {n}");
            }
        }

        // ================================================================== P17 / P18 hit details

        // P17 -- CO:102-104: an arrow reaching an invulnerable target (dash i-frames) deals nothing and is consumed.
        [Test]
        public void P17_ArrowVsInvulnerable_NoDamageArrowConsumed()
        {
            const int tx = 3000, contactUpdate = 7;           // same geometry as P3
            var s = Arena(FighterClass.Ranged, RangedBase, 0, tx);
            Action before = () => { Put(s.F[1], tx, 0, -1); s.F[1].Invuln = DashInvuln; };
            int slot = Fire(s, ChibiButtons.Light, before);
            var p = s.Proj[slot];
            int hp = s.F[1].HpU;
            for (int n = 1; n < contactUpdate; n++) { Update(s, before); Assert.IsTrue(p.Active, $"P17: arrow gone on update {n}"); }
            Update(s, before);
            Assert.IsFalse(p.Active, "P17: arrow must be consumed on contact even though the hit misses (HasHit + despawn)");
            Assert.AreEqual(hp, s.F[1].HpU, "P17: invulnerable target took damage");
        }

        // P18 -- CO:101: kb direction = Vel.x >= 0 ? +1 : -1, so a vertical shot (Vx = 0) knocks toward +X.
        [Test]
        public void P18_VerticalShot_KnockbackDirPlus()
        {
            const int tx = 3000, contactUpdate = 3;           // (3000, -1000) + 300 per update; circle reaches y 0 at -100
            const int kbX = 22500;
            var s = Arena(FighterClass.Ranged, RangedBase, -7500, tx);
            Place(s, 0, new Spec { X = tx, Y = -1000, Vx = 0, Vy = ArrowLightSpeed, KbX = kbX });
            int hp = s.F[1].HpU;
            for (int n = 1; n < contactUpdate; n++) Update(s, Hold(s, tx, 0));
            Update(s, Hold(s, tx, 0));
            Assert.Less(s.F[1].HpU, hp, "P18: precondition, the vertical arrow hit on update 3");
            Assert.AreEqual(kbX / 2, s.F[1].Vx, "P18: knockback Vx = +KbX x 0.5 (Ranged) -- Dir must be +1 for Vx = 0");
        }

        // ================================================================== P19 pool + A1 / A4

        // P19 -- section 5, D-P2 = A, A4: MaxProjectiles 64; a Ranged mirror firing both ults, Arrow Barrage and normals never
        // drops a shot (DroppedShots stays 0, in state).
        [Test]
        public void P19_RangedMirror_NoDroppedShots()
        {
            const int steps = 400;
            Assert.AreEqual(MaxProjectiles, ConstOf(typeof(ChibiSimConst), "MaxProjectiles"), "P19: ChibiSimConst.MaxProjectiles");
            var cfg = new ChibiMatchConfig
            {
                Seed = Seed, Class0 = FighterClass.Ranged, Passive0 = RangedBase, Class1 = FighterClass.Ranged,
                Passive1 = RangedBase, Stage = SkyDojo,
            };
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            for (int t = 0; t < SettleTicks; t++) ChibiSimulation.Step(s, Neutral, Neutral);
            Assert.AreEqual(MaxProjectiles, s.Proj.Length, "P19: pool length");
            Put(s.F[0], -3000, 0, 1);
            Put(s.F[1], 3000, 0, -1);
            ChibiTestTime.ArmUltFire(s, 0); ChibiTestTime.ArmUltFire(s, 1);   // rules v15: both ults fire on step 0
            var cycle = new[] { ChibiButtons.Skill3, ChibiButtons.Light, ChibiButtons.Heavy, ChibiButtons.Skill1 };
            int peak = 0;
            for (int t = 0; t < steps && !s.MatchOver; t++)
            {
                ChibiButtons b = t == 0 ? ChibiButtons.None : (t % 2 == 0 ? cycle[(t / 2) % cycle.Length] : ChibiButtons.None);
                ChibiTestTime.StepGame(s, Press(b), Press(b));
                int live = 0;
                foreach (var p in s.Proj) if (p.Active) live++;
                peak = Math.Max(peak, live);
                Assert.AreEqual(0, Dropped(s), $"P19: shot dropped on step {t} (peak {peak} live)");
            }
        }

        // A1 + A4 -- TD review: with the pool full the spear spawn fails, SpearOut is NOT set (the Polearm keeps its
        // normals), and the drop is counted in state.
        [Test]
        public void A1_SpearDroppedByFullPool_SpearOutStaysFalse()
        {
            const int lockSteps = 40;                          // > throw spear startup + active + recovery (10 + 1 + 14)
            var s = Arena(FighterClass.Polearm, PolearmArmorBreak, 0, -5000);
            foreach (var p in s.Proj)
            {
                p.Active = true; p.Owner = 1; p.X = 0; p.Y = 40000; p.Vx = 0; p.Vy = 0; p.Life = 100000;
                p.SpawnFrame = s.GameTicks - 1;
            }
            ChibiTestTime.StepGame(s, Press(ChibiButtons.Skill3), Neutral);
            for (int t = 1; t < lockSteps; t++) ChibiTestTime.StepGame(s, Neutral, Neutral);
            foreach (var p in s.Proj) Assert.AreEqual(1, p.Owner, "A1: precondition, the spear must not find a slot");
            Assert.AreEqual(1, Dropped(s), "A4: DroppedShots after one dropped spear");
            Assert.IsFalse(SpearOut(s.F[0]), "A1: SpearOut set although the spear was dropped");
            ChibiTestTime.StepGame(s, Press(ChibiButtons.Light), Neutral);
            Assert.AreEqual(0, s.F[0].MoveSlot, "A1: Polearm normals locked after a dropped spear");
        }

        // A2 -- TD review: SpawnProjectile writes every new field (slot reuse) and InitFighter clears SpearOut.
        [Test]
        public void A2_SlotReuse_SpawnResetsEveryNewField()
        {
            const int dirty = 4321;
            var s = Arena(FighterClass.Ranged, RangedBase, 0, 3000);
            var slot0 = s.Proj[0];
            SetInt(slot0, "Kind", KindOf("VolleyLead"));
            SetInt(slot0, "Flags", FHoming | FPierce | FHarmless);
            SetInt(slot0, "Radius", dirty); SetInt(slot0, "GrowPerTick", dirty); SetInt(slot0, "Speed", dirty);
            SetInt(slot0, "ReturnTicks", dirty); SetInt(slot0, "ReturnMult", dirty);
            int slot = Fire(s, ChibiButtons.Light, Hold(s, 3000, 0));
            Assert.AreEqual(0, slot, "A2: precondition, the arrow reuses slot 0");
            Assert.AreEqual(KindOf("Arrow"), GetInt(slot0, "Kind"), "A2: Kind");
            Assert.AreEqual(FDestructible, GetInt(slot0, "Flags"), "A2: Flags");
            Assert.AreEqual(ArrowR, GetInt(slot0, "Radius"), "A2: Radius");
            Assert.AreEqual(0, GetInt(slot0, "GrowPerTick"), "A2: GrowPerTick");
            Assert.AreEqual(ArrowLightSpeed, GetInt(slot0, "Speed"), "A2: Speed = ISqrt(Vx^2 + Vy^2)");
            Assert.AreEqual(0, GetInt(slot0, "ReturnTicks"), "A2: ReturnTicks");
            Assert.AreNotEqual(dirty, GetInt(slot0, "ReturnMult"), "A2: ReturnMult left over from the previous occupant");
        }

        [Test]
        public void A2_Init_ClearsSpearOut()
        {
            var s = Arena(FighterClass.Polearm, PolearmArmorBreak, 0, -5000);
            SetBool(s.F[0], "SpearOut", true);
            SetBool(s.F[1], "SpearOut", true);
            ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = Seed, Class0 = FighterClass.Polearm, Class1 = FighterClass.Polearm });
            Assert.IsFalse(SpearOut(s.F[0]) || SpearOut(s.F[1]), "A2: InitFighter must clear SpearOut");
        }

        // A8 -- TD review: RulesVersion 11. (MaxProjectiles in ComputeHash cannot be varied from a test: it is a const.)
        [Test]
        public void A8_RulesVersion_Is11()
        {
            Assert.GreaterOrEqual(ConstOf(typeof(ChibiMatchConfig), "RulesVersion"), RulesVersion11, "A8: RulesVersion at least 11");
        }

        // ================================================================== P20 rollback

        static readonly string[] NewProjFields = { "Kind", "Flags", "Radius", "GrowPerTick", "Speed", "ReturnTicks", "ReturnMult" };

        /// <summary>Spear mid-return (update 35, real throw), a wave mid-growth and a pending lead (placed).</summary>
        static ChibiSimState RollbackScene(out int spearSlot)
        {
            const int midReturn = 35, waveAge = 20, leadAge = 17;
            var s = Arena(FighterClass.Polearm, PolearmArmorBreak, 0, -5000);
            spearSlot = Fire(s, ChibiButtons.Skill3);
            for (int n = 1; n <= midReturn; n++) Update(s);
            var w = Wave(1);
            w.X = -3000; w.Radius = WaveR + WaveGrowPerTick * waveAge; w.Life = WaveLife - waveAge;
            Place(s, FreeSlot(s), w);
            var l = Lead();
            l.Life = LeadLife - leadAge;
            Place(s, FreeSlot(s), l);
            return s;
        }

        // P20 -- section 6: restore a snapshot into a state whose every new field holds garbage, step 60: identical to the
        // uninterrupted run (CopyFrom covers every new field).
        [Test]
        public void P20_Rollback_RestoreThenStep60_ChecksumMatches()
        {
            const int steps = 60, garbage = 7;
            var s = RollbackScene(out _);
            var snap = Clone(s);
            var dirty = Clone(s);
            foreach (var p in dirty.Proj)
                foreach (var f in NewProjFields) SetInt(p, f, GetInt(p, f) + garbage);
            SetBool(dirty.F[0], "SpearOut", !SpearOut(dirty.F[0]));
            SetBool(dirty.F[1], "SpearOut", !SpearOut(dirty.F[1]));
            SetInt(dirty, "DroppedShots", Dropped(dirty) + garbage);
            dirty.CopyFrom(snap);
            for (int n = 0; n < snap.Proj.Length; n++)
                foreach (var f in NewProjFields)
                    Assert.AreEqual(GetInt(snap.Proj[n], f), GetInt(dirty.Proj[n], f), $"P20: CopyFrom misses Proj[{n}].{f}");
            Assert.AreEqual(SpearOut(snap.F[0]), SpearOut(dirty.F[0]), "P20: CopyFrom misses SpearOut");
            Assert.AreEqual(Dropped(snap), Dropped(dirty), "P20: CopyFrom misses DroppedShots");
            for (int t = 0; t < steps; t++) { Update(s); Update(dirty); }
            Assert.AreEqual(s.ComputeChecksum(), dirty.ComputeChecksum(), "P20: restored run diverged from the uninterrupted run");
        }

        // P20 -- section 6 / A4: every new field (and SpearOut, DroppedShots) changes the checksum.
        [Test]
        public void P20_EveryNewField_ChangesChecksum()
        {
            var s = RollbackScene(out int spear);
            uint baseSum = s.ComputeChecksum();
            foreach (var f in NewProjFields)
            {
                var c = Clone(s);
                SetInt(c.Proj[spear], f, GetInt(c.Proj[spear], f) + 1);
                Assert.AreNotEqual(baseSum, c.ComputeChecksum(), $"P20: ChibiProjectileSim.{f} not mixed into the checksum");
            }
            var c1 = Clone(s);
            SetBool(c1.F[0], "SpearOut", !SpearOut(c1.F[0]));
            Assert.AreNotEqual(baseSum, c1.ComputeChecksum(), "P20: ChibiFighterSim.SpearOut not mixed into the checksum");
            var c2 = Clone(s);
            SetInt(c2, "DroppedShots", Dropped(c2) + 1);
            Assert.AreNotEqual(baseSum, c2.ComputeChecksum(), "P20/A4: ChibiSimState.DroppedShots not mixed into the checksum");
        }

        // ================================================================== P21 spawn tables (real input)

        static void AssertSpawn(string id, ChibiProjectileSim p, int feetX, int feetY, int facing, int offX, int offY,
                                int speed, int life, int mult, int kbX, int kbY, int stun, int r, string kind, int flags)
        {
            Assert.AreEqual(feetX + facing * offX, p.X, $"{id}: spawn X (feet {feetX} + {facing} x {offX})");
            Assert.AreEqual(feetY + offY, p.Y, $"{id}: spawn Y");
            Assert.AreEqual(facing * speed, p.Vx, $"{id}: Vx");
            Assert.AreEqual(0, p.Vy, $"{id}: Vy");
            Assert.AreEqual(life, p.Life, $"{id}: Life");
            Assert.AreEqual(mult, p.Mult, $"{id}: Mult");
            Assert.AreEqual(kbX, p.KbX, $"{id}: KbX (sub, before the x0.5 of HalfKnockback)");
            Assert.AreEqual(kbY, p.KbY, $"{id}: KbY");
            Assert.AreEqual(stun, p.Stun, $"{id}: Stun");
            Assert.AreEqual(r, GetInt(p, "Radius"), $"{id}: Radius");
            Assert.AreEqual(KindOf(kind), GetInt(p, "Kind"), $"{id}: Kind");
            Assert.AreEqual(flags, GetInt(p, "Flags"), $"{id}: Flags");
            Assert.AreEqual(speed, GetInt(p, "Speed"), $"{id}: Speed");
            Assert.AreEqual(0, GetInt(p, "GrowPerTick"), $"{id}: GrowPerTick");
        }

        const int SideX = 3000;                                // attacker at -3000 facing +1 or +3000 facing -1
        const int PlatC_X = 0, PlatC_Top = 5550;               // victim parked above every shot line

        static ChibiSimState SpawnArena(FighterClass c, int passive, int facing) =>
            Arena(c, passive, -facing * SideX, PlatC_X, PlatC_Top, facing);

        // P21 row 6 -- F:854, F:538: Backstep Shot = big arrow (500F, 725), 21 u/s, r 250, Life 78, 1500 b, kb 75000/45000, stun 24.
        [TestCase(1)]
        [TestCase(-1)]
        public void P21_BackstepShot_SpawnValues(int facing)
        {
            var s = SpawnArena(FighterClass.Ranged, RangedBase, facing);
            var p = s.Proj[Fire(s, ChibiButtons.Skill1, null, out int fx, out int fy)];
            AssertSpawn("P21 backstep", p, fx, fy, facing, ArrowOffX, ArrowOffY, ArrowBigSpeed, ArrowLife, 1500, 75000, 45000, 24,
                        ArrowR, "ArrowBig", FDestructible);
        }

        // P21 row 7 -- F:871: Barrage arrow (500F, 675 + yOff), 18 u/s, r 250, Life 78, 600 b, kb 18000/12000, stun 11.
        // yOff (random in step 12) is checked only against the original's range [-0.25, 0.3].
        [TestCase(1)]
        [TestCase(-1)]
        public void P21_BarrageArrow_SpawnValues(int facing)
        {
            var s = SpawnArena(FighterClass.Ranged, RangedBase, facing);
            var p = s.Proj[Fire(s, ChibiButtons.Skill3, null, out int fx, out int fy)];
            Assert.That(p.Y - fy, Is.InRange(BarrageBaseY + BarrageYOffMin, BarrageBaseY + BarrageYOffMax), "P21 barrage: spawn Y offset");
            AssertSpawn("P21 barrage", p, fx, fy, facing, ArrowOffX, p.Y - fy,
                        ArrowLightSpeed, ArrowLife, 600, 18000, 12000, 11, ArrowR, "Arrow", FDestructible);
        }

        // P21 row 8 -- F:876-884: Fireball (600F, 975), 9 u/s, r 380, Life 180, 1600 b, kb 75000/60000, stun 24, Homing.
        [TestCase(1)]
        [TestCase(-1)]
        public void P21_Fireball_SpawnValues(int facing)
        {
            var s = SpawnArena(FighterClass.Magic, MagicBase, facing);
            var p = s.Proj[Fire(s, ChibiButtons.Skill1, null, out int fx, out int fy)];
            AssertSpawn("P21 fireball", p, fx, fy, facing, FireballOffX, FireballOffY, FireballSpeed, FireballLife, 1600, 75000, 60000, 24,
                        FireballR, "Fireball", FHoming | FDestructible);
        }

        // P21 row 9 -- F:935-950: Spear (600F, 775), 16 u/s, r 300, Life 180, 1400 b, kb 75000/45000, stun 21, !Destructible,
        // ReturnTicks 27, ReturnMult 800 b (Polearm P0: b = 1), SpearOut set.
        [TestCase(1)]
        [TestCase(-1)]
        public void P21_Spear_SpawnValues(int facing)
        {
            var s = SpawnArena(FighterClass.Polearm, PolearmArmorBreak, facing);
            var p = s.Proj[Fire(s, ChibiButtons.Skill3, null, out int fx, out int fy)];
            AssertSpawn("P21 spear", p, fx, fy, facing, SpearOffX, SpearOffY, SpearSpeed, SpearLife, 1400, 75000, 45000, 21,
                        SpearR, "Spear", 0);
            Assert.AreEqual(SpearReturnTicks, GetInt(p, "ReturnTicks"), "P21 spear: ReturnTicks");
            Assert.AreEqual(800, GetInt(p, "ReturnMult"), "P21 spear: ReturnMult = 800 x b");
            Assert.IsTrue(SpearOut(s.F[0]), "P21 spear: SpearOut");
        }

        // ================================================================== P22 ring-out (A7)

        // P22 -- A7, F:670-710: the owner's ring-out keeps the returning spear and SpearOut; the next update the spear
        // retargets the owner's respawned Center; it still despawns at the owner and clears SpearOut.
        [Test]
        public void P22_OwnerRingOut_ReturningSpearRetargetsRespawn()
        {
            const int returningAt = 30;
            var s = Arena(FighterClass.Polearm, PolearmArmorBreak, 0, -5000);
            var st = ChibiStageTable.Get(SkyDojo);
            int slot = Fire(s, ChibiButtons.Skill3);
            var p = s.Proj[slot];
            for (int n = 1; n <= returningAt; n++) Update(s);
            Update(s, () => Put(s.F[0], st.BlastX + 500, 0, 1));          // ring-out at the end of this game step
            Assert.AreEqual(st.RespawnX, s.F[0].X, "P22: precondition, the owner rang out and respawned");
            Assert.IsTrue(p.Active, "P22: ring-out removed the returning spear");
            Assert.IsTrue(SpearOut(s.F[0]), "P22: ring-out cleared SpearOut (Interrupt / RingOut never reset it)");
            int px = p.X, py = p.Y;
            Update(s);
            ScaleToRef(s.F[0].X - px, s.F[0].Y + CenterY - py, SpearReturnSpeed, out int evx, out int evy);
            Assert.That(p.Vx, Is.InRange(evx - VelTol, evx + VelTol), "P22: return Vx toward the respawned owner");
            Assert.That(p.Vy, Is.InRange(evy - VelTol, evy + VelTol), "P22: return Vy toward the respawned owner");
            for (int n = 0; n < LongRun && p.Active; n++) Update(s);
            Assert.IsFalse(p.Active, "P22: spear never reached the respawned owner");
            Assert.IsFalse(SpearOut(s.F[0]), "P22: SpearOut still set after the spear despawned");
        }

        // P22 -- A7: homing retargets the opponent after its ring-out (next update steers toward the respawned Center).
        [Test]
        public void P22_TargetRingOut_FireballRetargetsRespawn()
        {
            const int tx = 3000, ringOutAt = 3;
            var s = Arena(FighterClass.Magic, MagicBase, 0, tx);
            var st = ChibiStageTable.Get(SkyDojo);
            int slot = Fire(s, ChibiButtons.Skill1, Hold(s, tx, 0));
            var p = s.Proj[slot];
            for (int n = 1; n < ringOutAt; n++) Update(s, Hold(s, tx, 0));
            Update(s, () => Put(s.F[1], -(st.BlastX + 500), 0, 1));
            Assert.AreEqual(st.RespawnX, s.F[1].X, "P22: precondition, the target rang out and respawned");
            Assert.IsTrue(p.Active, "P22: fireball removed by the target's ring-out");
            int px = p.X, py = p.Y, vx = p.Vx, vy = p.Vy;
            Update(s);
            SteerRef(ref vx, ref vy, FireballSpeed, s.F[1].X - px, s.F[1].Y + CenterY - py);
            Assert.That(p.Vx, Is.InRange(vx - VelTol, vx + VelTol), "P22: homing Vx toward the respawned target");
            Assert.That(p.Vy, Is.InRange(vy - VelTol, vy + VelTol), "P22: homing Vy toward the respawned target");
        }
    }
}
