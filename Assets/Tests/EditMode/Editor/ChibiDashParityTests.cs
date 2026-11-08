// Story type: Logic (dash state machine + formulas) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/dash-conversion.md (section 1 rules table, section 2 edge cases,
// section 3 tick order, section 4 Dash & Thrust, section 5 reference test values + tolerances).
// User decision D2 = A: the reference is the original at 60 fps (motion ticks 1..7, ground dash 1983.3 milli).
//
// These tests compile against TODAY's API: they never touch DashTicks / DashCd / DashDir (not added yet) and never
// read or assert Vx / Vy. They measure positions (X, Y in milli), tick counts, Facing, Grounded, Hp, Shielding,
// ShieldTick and AirJumps. Dash is a press edge (doc section 0, FighterInput.cs:63): pressed for 1 tick, then released.
// Tick numbering follows doc section 5: tick 0 = the tick the Dash edge is pressed; X(k) = position after tick k.
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Dash parity between the deterministic 60 Hz simulation and the original Fighter.cs dash (model A, 60 fps).
    /// Expected values and tolerances come from dash-conversion.md section 5.
    /// </summary>
    public class ChibiDashParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;               // spawn invulnerability wears off (same as ChibiMovementParityTests)

        // ---- placement
        const int GroundStartX = -6000;            // a full dash + slide (3908 milli) stays on stage (StageHalfWidth 8000)
        const int DummyX = -7900;                  // P2 parked behind P1, never in the dash path
        const int AirStartY = 15000;               // below BlastTop 18000; > 40 ticks of airtime before landing
        const int OverlapOffset = 1700;            // attacker centre = victim start + 6 dash ticks (6 x 283.33)

        // ---- doc section 1 rules table (Fighter.cs lines cited there)
        const double DashStepMilli = 850.0 / 3.0;  // row 1: 17 u/s x 50/3 = 283.333 milli/tick (Fighter.cs:270)
        const int DashMotionTicks = 7;             // row 2: motion on ticks 1..7 (0.13 s timer, Fighter.cs:312)
        const int DashCooldownTicks = 33;          // row 3: 0.55 s x 60 (Fighter.cs:313)
        const int DashInvulnTicks = 6;             // row 4: 0.1 s x 60, immune on ticks 0..5 (Fighter.cs:314)

        // ---- doc section 5 reference values
        const double GroundDashDistance = 1983.33;      // 7 x 255000 / 900
        const double GroundDashTotal = 3908;            // 1983 + slide 1925 (sum k=1..14 of 255000 - 17500k, /900)
        const int GroundDashRestTick = 22;              // "at rest on tick 22"
        const double AirDashSlide = 3472.2;             // sum k=1..25 of (255000 - 10000k) / 900
        const int AirDashSlideTicks = 25;
        const double AirDashDrop = -82.41;              // Y(7) - Y(0) = -7 x 10595 / 900 (row 6, doubled ArcC50 term)
        const double HitBeforeKnockback = 1700;         // 6 x 283.33: tick 6 integrates before hits resolve
        const double DashThrustTotal = 4105.6;          // 3900 + 50 + sum k=1..7 of (45000 - 6250k) / 900 (section 4)
        const int SettleWatchTicks = 80;                // longer than any slide in this file

        // ---- doc section 5 tolerances
        const double PosTol = 1;                        // milli: sub-milli remainder ("+-1 milli")

        // ------------------------------------------------------------------ helpers

        static readonly ChibiInputPacket Neutral = default;
        static ChibiInputPacket Hold(ChibiDir d, ChibiButtons b = ChibiButtons.None) => ChibiInputPacket.Make(d, b);
        static ChibiInputPacket DashPress(ChibiDir d = ChibiDir.Neutral) => Hold(d, ChibiButtons.Dash);

        /// <summary>Fresh settled match; P1 at <paramref name="x1"/> facing <paramref name="facing1"/> on the floor.</summary>
        static ChibiSimState NewMatch(int x1 = GroundStartX, int facing1 = 1, FighterClass p2 = FighterClass.Heavy,
                                      int x2 = DummyX, int facing2 = 1)
        {
            var cfg = new ChibiMatchConfig { Seed = Seed, Class0 = FighterClass.Melee, Passive0 = 0, Class1 = p2 };
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            for (int t = 0; t < SettleTicks; t++) ChibiSimulation.Step(s, default, default);
            s.F[0].X = x1; s.F[0].Facing = facing1;
            s.F[1].X = x2; s.F[1].Facing = facing2;
            return s;
        }

        /// <summary>Puts P1 in the air at rest (Vy = 0 is unit-independent) with its air jump available.</summary>
        static void PlaceInAir(ChibiSimState s, int x, int y)
        {
            var f = s.F[0];
            f.X = x; f.Y = y; f.Vy = 0; f.Grounded = false; f.AirJumps = 1;
        }

        // counts game steps (D10): one Tick = one game step; frozen hitstop / cinematic real ticks are consumed inside.
        static void Tick(ChibiSimState s, ChibiInputPacket p1) => ChibiTestTime.StepGame(s, p1, default);
        static void Tick(ChibiSimState s, ChibiInputPacket p1, ChibiInputPacket p2) => ChibiTestTime.StepGame(s, p1, p2);

        static void Idle(ChibiSimState s, int ticks)
        {
            for (int t = 0; t < ticks; t++) Tick(s, Neutral);
        }

        /// <summary>Runs neutral ticks until X stops changing; returns the final X.</summary>
        static int SettleX(ChibiSimState s)
        {
            for (int t = 0; t < SettleWatchTicks; t++)
            {
                int x0 = s.F[0].X;
                Tick(s, Neutral);
                if (s.F[0].X == x0) return x0;
            }
            return s.F[0].X;
        }

        /// <summary>
        /// Measures the Melee light's first hitting tick (0 = press tick) against an idle, overlapping victim, with the
        /// same class / passive / placement the timed tests use. Keeps the tests independent of the startup value and
        /// of attack-speed scaling (ChibiSimulation.cs:513).
        /// </summary>
        static int MeleeLightFirstHitTick()
        {
            var s = NewMatch(x1: 0, p2: FighterClass.Melee, x2: OverlapOffset, facing2: -1);
            int hp0 = s.F[0].Hp;
            for (int n = 0; n < 30; n++)
            {
                Tick(s, Neutral, n == 0 ? Hold(ChibiDir.Neutral, ChibiButtons.Light) : Neutral);
                if (s.F[0].Hp < hp0) return n;
            }
            return -1;
        }

        // ------------------------------------------------------------------ distance (rows 1-2, 10; section 5)

        [Test]
        public void GroundDash_Distance_MatchesOriginal60Hz()
        {
            // Arrange
            var s = NewMatch();
            int y0 = s.F[0].Y;

            // Act: tick 0 = press (normal velocity), ticks 1..7 = dash motion
            Tick(s, DashPress());
            int x0 = s.F[0].X;
            bool stayedGrounded = s.F[0].Grounded;
            int maxYDev = 0;
            for (int k = 1; k <= DashMotionTicks; k++)
            {
                Tick(s, Neutral);
                stayedGrounded &= s.F[0].Grounded;
                maxYDev = System.Math.Max(maxYDev, System.Math.Abs(s.F[0].Y - y0));
            }
            int x7 = s.F[0].X;
            int[] xs = new int[GroundDashRestTick + 1];
            xs[DashMotionTicks] = x7;
            for (int k = DashMotionTicks + 1; k <= GroundDashRestTick; k++) { Tick(s, Neutral); xs[k] = s.F[0].X; }

            // Assert
            Assert.AreEqual(GroundDashDistance, x7 - x0, PosTol, "X(7) - X(0): 7 motion ticks at 17 u/s (milli)");
            Assert.IsTrue(stayedGrounded, "ground dash: Grounded stays true");
            Assert.AreEqual(0, maxYDev, "ground dash: no height change");
            Assert.AreEqual(GroundDashTotal, xs[GroundDashRestTick - 1] - x0, PosTol,
                            "dash + post-dash slide (no dash-end reset; doc section 6.3) (milli)");
            Assert.Greater(xs[GroundDashRestTick - 1], xs[GroundDashRestTick - 2], "still sliding on tick 21");
            Assert.AreEqual(xs[GroundDashRestTick - 1], xs[GroundDashRestTick], "at rest on tick 22 (exact tick)");
        }

        // ------------------------------------------------------------------ direction (row 8; section 2 "Facing")

        [Test]
        public void Dash_DirectionFromHeldInput_ElseFacing()
        {
            // Arrange: (a) holding W while facing right; (b) neutral while facing left
            var held = NewMatch(x1: 0, facing1: 1);
            var neutral = NewMatch(x1: 0, facing1: -1);

            // Act
            Tick(held, DashPress(ChibiDir.W));
            int hx0 = held.F[0].X;
            int facingAfterPress = held.F[0].Facing;
            Idle(held, DashMotionTicks);

            Tick(neutral, DashPress());
            int nx0 = neutral.F[0].X;
            Idle(neutral, DashMotionTicks);

            // Assert
            Assert.AreEqual(-GroundDashDistance, held.F[0].X - hx0, PosTol, "MoveX = -1 overrides Facing = +1: dash left");
            Assert.AreEqual(-1, facingAfterPress, "free-state back-dash turns the fighter on tick 0 (Fighter.cs:284)");
            Assert.AreEqual(-GroundDashDistance, neutral.F[0].X - nx0, PosTol, "neutral input dashes toward Facing = -1");
        }

        [Test]
        public void Dash_BackdashDuringAttack_KeepsFacing()
        {
            // Arrange: Light at tick 0 (Facing +1); the move is still running on tick 1
            var s = NewMatch(x1: 0, facing1: 1);
            Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Light));
            Assert.GreaterOrEqual(s.F[0].MoveSlot, 0, "fixture: the light attack is running");

            // Act: Dash + W on tick 1 (inside the attack)
            Tick(s, DashPress(ChibiDir.W));
            int x0 = s.F[0].X;
            Idle(s, DashMotionTicks);

            // Assert (doc section 5 "Backdash during attack")
            Assert.AreEqual(-GroundDashDistance, s.F[0].X - x0, PosTol, "back-dash during an attack goes opposite Facing");
            Assert.AreEqual(1, s.F[0].Facing, "facing is not updated during an attack: a true backdash");
        }

        // ------------------------------------------------------------------ air (rows 5-7; section 2 "Air dash limit")

        [Test]
        public void AirDash_HeightDrop_MatchesOriginal()
        {
            // Arrange
            var s = NewMatch();
            PlaceInAir(s, GroundStartX, AirStartY);

            // Act
            Tick(s, DashPress());
            int x0 = s.F[0].X, y0 = s.F[0].Y;
            Idle(s, DashMotionTicks);
            int x7 = s.F[0].X, y7 = s.F[0].Y;
            Idle(s, AirDashSlideTicks);

            // Assert
            Assert.IsFalse(s.F[0].Grounded, "fixture: still airborne");
            Assert.AreEqual(AirDashDrop, y7 - y0, PosTol, "Y(7) - Y(0): air sag with the doubled 50 Hz arc term (milli)");
            Assert.AreEqual(GroundDashDistance, x7 - x0, PosTol, "air dash distance (milli)");
            Assert.AreEqual(AirDashSlide, s.F[0].X - x7, PosTol, "air slide after the dash at 40 u/s^2 (milli)");
        }

        [Test]
        public void AirDash_Unlimited_OnlyCooldownLimits()
        {
            // Arrange
            var s = NewMatch();
            PlaceInAir(s, GroundStartX, AirStartY);
            int airJumps0 = s.F[0].AirJumps;

            // Act: air dash at tick 0 and again at tick 33 (cooldown expired, Vx back to 0 by tick 33)
            Tick(s, DashPress());
            int x0 = s.F[0].X;
            Idle(s, DashMotionTicks);
            int first = s.F[0].X - x0;
            Idle(s, DashCooldownTicks - 1 - DashMotionTicks);
            Tick(s, DashPress());
            int x33 = s.F[0].X;
            Idle(s, DashMotionTicks);
            int second = s.F[0].X - x33;

            // Assert
            Assert.IsFalse(s.F[0].Grounded, "fixture: both dashes in the air");
            Assert.AreEqual(GroundDashDistance, first, PosTol, "first air dash starts");
            Assert.AreEqual(GroundDashDistance, second, PosTol, "second air dash at tick 33 starts: no air counter");
            Assert.AreEqual(airJumps0, s.F[0].AirJumps, "air dashes do not spend airJumps");
        }

        // ------------------------------------------------------------------ cooldown (row 3)

        [TestCase(DashCooldownTicks - 1, false)]
        [TestCase(DashCooldownTicks, true)]
        public void Dash_Cooldown_BlockedUntilTick33(int secondPressTick, bool expectDash)
        {
            // Arrange: first dash at tick 0; at rest from tick 22
            var s = NewMatch();
            Tick(s, DashPress());
            int x0 = s.F[0].X;
            Idle(s, DashMotionTicks);
            Assert.AreEqual(GroundDashDistance, s.F[0].X - x0, PosTol, "fixture: the first dash starts");
            Idle(s, secondPressTick - 1 - DashMotionTicks);

            // Act
            Tick(s, DashPress());
            int xN = s.F[0].X;
            Idle(s, DashMotionTicks);
            int moved = s.F[0].X - xN;
            Idle(s, 3);                                     // a buffered replay of the tick-32 press would show here

            // Assert (doc section 5 "Cooldown": tick 32 blocked, tick 33 starts)
            if (expectDash)
                Assert.AreEqual(GroundDashDistance, moved, PosTol, "Dash edge on tick 33 starts a dash");
            else
                Assert.AreEqual(xN, s.F[0].X, "Dash edge on tick 32 is ignored (cooldown) and not buffered");
        }

        // ------------------------------------------------------------------ invulnerability (row 4)

        [TestCase(0, false)]
        [TestCase(3, false)]
        [TestCase(DashInvulnTicks - 1, false)]
        [TestCase(DashInvulnTicks, true)]
        public void Dash_Invuln_HitsMissEarlyLandLater(int hitTick, bool expectLand)
        {
            // Arrange: P2 (Melee) faces P1 with its centre at P1's tick-6 position, so P1 overlaps its light box on
            // every dash tick 0..7 (and when idle). P2's light is timed so its first active tick is dash tick k.
            int startup = MeleeLightFirstHitTick();
            Assert.GreaterOrEqual(startup, 0, "fixture: the Melee light must hit an idle overlapping victim");
            var s = NewMatch(x1: 0, p2: FighterClass.Melee, x2: OverlapOffset, facing2: -1);
            int dashTick = startup;                         // so the P2 press tick (dashTick + k - startup) is >= 0
            int p2Press = dashTick + hitTick - startup;
            int hp0 = s.F[0].Hp;

            // Act
            int hpBefore = hp0;
            for (int t = 0; t <= dashTick + hitTick; t++)
            {
                if (t == dashTick + hitTick) hpBefore = s.F[0].Hp;
                Tick(s, t == dashTick ? DashPress() : Neutral,
                        t == p2Press ? Hold(ChibiDir.Neutral, ChibiButtons.Light) : Neutral);
            }

            // Assert
            Assert.AreEqual(hp0, hpBefore, "fixture: no hit before dash tick " + hitTick);
            if (expectLand)
                Assert.Less(s.F[0].Hp, hpBefore, "dash tick 6: invulnerability (6 ticks) has expired, the hit lands");
            else
                Assert.AreEqual(hpBefore, s.F[0].Hp, "dash tick " + hitTick + ": invulnerable, the hit misses");
        }

        // ------------------------------------------------------------------ shield (section 2 shield rows)

        [Test]
        public void Dash_CannotStartWhileShielding()
        {
            // Arrange: shield raised
            var s = NewMatch();
            for (int t = 0; t < 5; t++) Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Shield));
            Assert.IsTrue(s.F[0].Shielding, "fixture: shield is up");

            // Act 1: Dash edge while holding Shield, keep holding
            int x0 = s.F[0].X;
            Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Shield | ChibiButtons.Dash));
            for (int t = 0; t < DashMotionTicks; t++) Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Shield));
            int xShield = s.F[0].X;

            // Act 2: release Shield and dash on the next free tick (cooldown was not spent)
            Tick(s, DashPress());
            int x1 = s.F[0].X;
            Idle(s, DashMotionTicks);

            // Assert
            Assert.AreEqual(x0, xShield, "no dash while shielding (Fighter.cs:277 returns before the dash start)");
            Assert.AreEqual(GroundDashDistance, s.F[0].X - x1, PosTol,
                            "the blocked press did not spend the cooldown: the next free press dashes");
        }

        [Test]
        public void Shield_HeldDuringDash_RaisesOnlyAfterDashEnds()
        {
            // Arrange
            var s = NewMatch();
            Tick(s, DashPress());
            bool shieldedDuringDash = false;

            // Act: Shield held from tick 1
            for (int k = 1; k <= DashMotionTicks; k++)
            {
                Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Shield));
                shieldedDuringDash |= s.F[0].Shielding;
            }
            Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Shield));   // tick 8

            // Assert (doc section 5 "Shield during dash")
            Assert.IsFalse(shieldedDuringDash, "Shielding false on ticks 1..7 (wantBlock needs dashTimer <= 0, Fighter.cs:262)");
            Assert.IsTrue(s.F[0].Shielding, "tick 8: the held shield rises when the dash ends");
            Assert.AreEqual(0, s.F[0].ShieldTick, "tick 8: ShieldTick 0 = fresh perfect-parry window");
        }

        // ------------------------------------------------------------------ attacks (section 2 attack rows)

        [Test]
        public void Dash_DuringAttack_AllowedAndAttackStillHits()
        {
            // counts game steps (D10)
            // Arrange: P2 stands where P1's light box (centre .. 1650 ahead) reaches during the dash
            int startup = MeleeLightFirstHitTick();
            Assert.GreaterOrEqual(startup, 0, "fixture: the Melee light must hit an idle overlapping victim");
            int victimX = (int)(startup * DashStepMilli) + 1000;
            var s = NewMatch(x1: 0, facing1: 1, p2: FighterClass.Heavy, x2: victimX, facing2: -1);
            int p2Hp0 = s.F[1].Hp;

            // Act: Light + Dash on the same tick (dash start runs before attack selection, Fighter.cs:309-318)
            Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Light | ChibiButtons.Dash));
            int x0 = s.F[0].X;
            bool moveRunning = s.F[0].MoveSlot >= 0;
            Idle(s, DashMotionTicks);
            int x7 = s.F[0].X;
            Idle(s, 3);

            // Assert
            Assert.IsTrue(moveRunning, "the light attack starts on the dash tick");
            Assert.AreEqual(GroundDashDistance, x7 - x0, PosTol, "the dash moves the fighter while the swing continues");
            Assert.Less(s.F[1].Hp, p2Hp0, "the attack is not interrupted: it still arms and hits");
        }

        // ------------------------------------------------------------------ hit cancels (section 2 "Hit during the dash")

        [Test]
        public void HitDuringDash_StopsDash()
        {
            // Arrange: P2's Melee light (stun 16 > 0) first active on dash tick 6 (see Dash_Invuln test geometry)
            int startup = MeleeLightFirstHitTick();
            Assert.GreaterOrEqual(startup, 0, "fixture: the Melee light must hit an idle overlapping victim");
            var s = NewMatch(x1: 0, p2: FighterClass.Melee, x2: OverlapOffset, facing2: -1);
            int dashTick = startup;
            int p2Press = dashTick + DashInvulnTicks - startup;
            int hp0 = s.F[0].Hp;
            int x0 = 0;

            // Act: run through dash tick 6
            for (int t = 0; t <= dashTick + DashInvulnTicks; t++)
            {
                Tick(s, t == dashTick ? DashPress() : Neutral,
                        t == p2Press ? Hold(ChibiDir.Neutral, ChibiButtons.Light) : Neutral);
                if (t == dashTick) x0 = s.F[0].X;
            }
            int x6 = s.F[0].X;
            bool hit = s.F[0].Hp < hp0;
            int maxStepAfter = int.MinValue;
            for (int k = 0; k < 4; k++)
            {
                int xp = s.F[0].X;
                Tick(s, Neutral);
                maxStepAfter = System.Math.Max(maxStepAfter, s.F[0].X - xp);
            }

            // Assert
            Assert.IsTrue(hit, "fixture: the stun hit lands on dash tick 6");
            Assert.AreEqual(HitBeforeKnockback, x6 - x0, PosTol, "displacement before the hit (milli)");
            Assert.Less(maxStepAfter, DashStepMilli - PosTol, "stun > 0 hit -> Interrupt: no dash motion afterwards");
        }

        // ------------------------------------------------------------------ Dash & Thrust (section 4)

        [Test]
        public void DashThrust_TotalSlide_MatchesOriginal()
        {
            // Arrange: Melee skill 1, grounded, P2 parked behind
            var s = NewMatch();

            // Act
            Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Skill1));
            Assert.GreaterOrEqual(s.F[0].MoveSlot, 0, "fixture: Dash & Thrust started");
            int x0 = s.F[0].X;
            int xEnd = SettleX(s);

            // Assert: 18 u/s on move ticks 1..13, 3 u/s once on tick 14, then MoveFriction 6250/tick
            Assert.AreEqual(DashThrustTotal, xEnd - x0, PosTol, "Dash & Thrust total travel incl. end slide (milli)");
        }
    }
}
