// Story type: Logic (movement formulas) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/movement-conversion.md (section 1 table, section 2 order + C50 / D1, section 4 values).
// User decision D1 = YES: the sim adds the 50 Hz arc-correction term, so arcs are asserted against the ORIGINAL 50 Hz values.
//
// These tests measure ONLY positions (X, Y in milli-units), tick counts and booleans. They never read or assert
// Vx / Vy, because velocity is moving to 1/900-milli sub-units (doc section 3). The only velocity writes are
// "Vy = 0" (unit-independent) to start a drop from rest.
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Movement parity between the deterministic 60 Hz simulation and the original Rigidbody2D Fighter (50 Hz Box2D).
    /// Reference values and tolerances come from movement-conversion.md section 4.
    /// </summary>
    public class ChibiMovementParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;            // let spawn invulnerability wear off (same as ChibiMoveParityTests)
        const int TicksPerSecond = 60;

        // Placement: P1 starts near the left edge so a long run stays on stage (StageHalfWidth 8000);
        // P2 is parked behind P1 so it never overlaps a swing or the run path.
        const int RunStartX = -7500;
        const int DummyX = -7900;
        const int OffStageX = -10000;           // beyond StageHalfWidth 8000, inside BlastX 17000: no floor to land on

        // ---- conversions (doc "Unit conversions"): speed u/s x 50/3 = milli/tick; accel u/s^2 x 5/18 = milli/tick^2
        static double SpeedMilliPerTick(double unitsPerSecond) => unitsPerSecond * 50.0 / 3.0;
        static double AccelMilliPerTick2(double unitsPerSecond2) => unitsPerSecond2 * 5.0 / 18.0;

        // ---- original values
        const double GravityMilli = 9.81;               // doc row 1: 9.81 x 3.6 u/s^2 (Physics2DSettings.asset:7, Fighter.cs:93)
        const double C50Milli = 0.981;                  // doc section 2: 0.1 x g, 50 Hz arc correction (D1 = YES)
        const double GroundAccelUps2 = 70;              // doc row 12, Fighter.cs:283
        const double AttackDecelUps2 = 25;              // doc row 14, Fighter.cs:286-289
        const double ShieldDecelUps2 = 60;              // doc row 15, Fighter.cs:276
        const double HeavyRunUps = 6.3;                 // doc row 3, ClassData.cs:64 (exact 105 milli/tick)

        // ---- tolerances (doc section 4 header)
        const double HeightTol = 3;                     // milli: integer-tick peak sampling (g/8) + 1 rounding
        const double SpeedTol = 1;                      // milli/tick
        const double SlideTol = 10;                     // milli (doc rows "slide": original varies with Update rate)

        // ------------------------------------------------------------------ helpers

        static readonly ChibiInputPacket Neutral = default;
        static ChibiInputPacket Hold(ChibiDir d, ChibiButtons b = ChibiButtons.None) => ChibiInputPacket.Make(d, b);

        /// <summary>Fresh match, settled; P1 at <paramref name="x"/> facing right on the floor, P2 parked at DummyX.</summary>
        static ChibiSimState NewMatch(FighterClass p1, int passive1 = 0, int x = 0)
        {
            var cfg = new ChibiMatchConfig { Seed = Seed, Class0 = p1, Passive0 = passive1, Class1 = FighterClass.Heavy };
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            for (int t = 0; t < SettleTicks; t++) ChibiSimulation.Step(s, default, default);
            s.F[0].X = x; s.F[0].Facing = 1;
            s.F[1].X = DummyX; s.F[1].Facing = 1;
            return s;
        }

        /// <summary>Puts P1 in the air at rest (Vy = 0 is unit-independent) with its air jump available.</summary>
        static void PlaceInAir(ChibiSimState s, int x, int y)
        {
            var f = s.F[0];
            f.X = x; f.Y = y; f.Vy = 0; f.Grounded = false; f.AirJumps = 1;
        }

        static void Tick(ChibiSimState s, ChibiInputPacket p1) => ChibiSimulation.Step(s, p1, default);

        /// <summary>Holds E for <paramref name="ticks"/> ticks (long enough to reach top run speed).</summary>
        static void RunRight(ChibiSimState s, int ticks)
        {
            for (int t = 0; t < ticks; t++) Tick(s, Hold(ChibiDir.E));
        }

        /// <summary>Discrete MoveTowards slide: sum over k >= 1 of max(0, v0 - k*decel) (doc section 4 convention).</summary>
        static double DiscreteSlide(double v0, double decel)
        {
            double sum = 0;
            for (int k = 1; v0 - k * decel > 0; k++) sum += v0 - k * decel;
            return sum;
        }

        /// <summary>Jumps from rest and returns (apex height above takeoff, n of the first peak sample, jump tick n = 1).</summary>
        static void MeasureApex(ChibiSimState s, int watchTicks, out int apex, out int apexTick)
        {
            int y0 = s.F[0].Y;
            apex = int.MinValue; apexTick = -1;
            for (int n = 1; n <= watchTicks; n++)
            {
                Tick(s, n == 1 ? Hold(ChibiDir.Neutral, ChibiButtons.Jump) : Neutral);
                int h = s.F[0].Y - y0;
                if (h > apex) { apex = h; apexTick = n; }
            }
        }

        // ------------------------------------------------------------------ jumps (doc rows 16-17, section 4)

        // Ground jump: v0' = 13 - 0.353 = 12.647 u/s, h = v0'^2/(2g) = 2264 milli at n = 21 or 22 (50 Hz original).
        const int GroundJumpApex = 2264;
        const int GroundJumpApexTickMin = 21, GroundJumpApexTickMax = 22;

        [Test]
        public void GroundJump_ApexHeightAndTime_MatchOriginal()
        {
            var s = NewMatch(FighterClass.Melee);

            MeasureApex(s, 40, out int apex, out int apexTick);

            Assert.AreEqual(GroundJumpApex, apex, HeightTol, "ground-jump apex (milli) vs the original 50 Hz arc; 2284.7 means C50 is missing");
            Assert.That(apexTick, Is.InRange(GroundJumpApexTickMin, GroundJumpApexTickMax), "ticks to apex (jump tick = 1)");
        }

        // Air jump from vy = 0: v0' = 11.5 - 0.353 = 11.147 u/s, h = 1759 milli at n = 19 (+-1).
        const int AirJumpApex = 1759;
        const int AirJumpApexTick = 19;
        const int AirStartY = 5000;

        [Test]
        public void AirJump_ApexHeightAboveTakeoff_MatchesOriginal()
        {
            var s = NewMatch(FighterClass.Melee);
            PlaceInAir(s, 0, AirStartY);

            MeasureApex(s, 40, out int apex, out int apexTick);

            Assert.AreEqual(AirJumpApex, apex, HeightTol, "air-jump apex above takeoff (milli)");
            Assert.AreEqual(AirJumpApexTick, apexTick, 1, "ticks to air-jump apex (jump tick = 1)");
        }

        // ------------------------------------------------------------------ running (doc rows 2-6, 12-13, section 4)

        const int AccelTicks = 30;     // longer than any time-to-top-speed in the table (max 12)

        // Expected: moveSpeed (u/s) -> exactly that many x1000 milli in 60 ticks (1 s). Doc: exact in sub-units;
        // +-1 milli covers the sub-milli position remainder.
        [TestCase(FighterClass.Melee, 7.6)]     // ClassData.cs:52
        [TestCase(FighterClass.Heavy, 6.3)]     // ClassData.cs:64
        [TestCase(FighterClass.Ranged, 7.3)]    // ClassData.cs:76
        [TestCase(FighterClass.Magic, 6.9)]     // ClassData.cs:88
        [TestCase(FighterClass.Polearm, 7.0)]   // ClassData.cs:100
        public void Run_TopSpeedPerClass_MatchesOriginal(FighterClass cls, double unitsPerSecond)
        {
            var s = NewMatch(cls, x: RunStartX);
            RunRight(s, AccelTicks);
            int x0 = s.F[0].X;

            RunRight(s, TicksPerSecond);

            double expected = unitsPerSecond * 1000.0;
            Assert.AreEqual(expected, s.F[0].X - x0, 1.0, cls + ": distance run in 1 s at top speed (milli)");
        }

        // Time to top speed from rest: speed / (175/9) on ground, speed / (100/9) in air; exact tick (doc section 4).
        [TestCase(FighterClass.Melee, 7.6, 7, 12)]
        [TestCase(FighterClass.Heavy, 6.3, 6, 10)]
        [TestCase(FighterClass.Ranged, 7.3, 7, 11)]
        [TestCase(FighterClass.Magic, 6.9, 6, 11)]
        [TestCase(FighterClass.Polearm, 7.0, 6, 11)]
        public void Run_TimeToTopSpeed_MatchesOriginal(FighterClass cls, double unitsPerSecond, int groundTicks, int airTicks)
        {
            double top = SpeedMilliPerTick(unitsPerSecond);

            var ground = NewMatch(cls, x: RunStartX);
            int groundActual = TicksUntilStepReaches(ground, top);

            var air = NewMatch(cls);
            PlaceInAir(air, RunStartX, AirStartY);   // falls ~32 ticks before landing: enough for 12 ticks of air control
            int airActual = TicksUntilStepReaches(air, top);

            Assert.AreEqual(groundTicks, groundActual, cls + ": ground ticks to top speed (70 u/s^2)");
            Assert.AreEqual(airTicks, airActual, cls + ": air ticks to top speed (40 u/s^2)");
        }

        /// <summary>Holds E; returns the first tick (1-based) whose X step is within 1 milli of <paramref name="top"/>.</summary>
        static int TicksUntilStepReaches(ChibiSimState s, double top)
        {
            for (int k = 1; k <= AccelTicks; k++)
            {
                int x0 = s.F[0].X;
                Tick(s, Hold(ChibiDir.E));
                if (s.F[0].X - x0 >= top - SpeedTol) return k;
            }
            return -1;
        }

        // ------------------------------------------------------------------ falling (doc rows 1, 21, section 4)

        // Free-fall distance after 1 s from rest: 0.02 x 0.70632 x 1275 = 18.011 u (50 Hz Box2D) = 18011 milli, +-5.
        const int FreeFallDistance1s = 18011;
        const double FreeFallDistanceTol = 5;
        const int HighY = 17000;   // below BlastTop 18000; after 18 u of fall it is at -1011, above BlastBottom -9000

        [Test]
        public void Fall_DistanceAfterOneSecond_NoSpeedCap()
        {
            var s = NewMatch(FighterClass.Melee);
            PlaceInAir(s, OffStageX, HighY);
            int lastStep = 0;

            for (int t = 0; t < TicksPerSecond; t++)
            {
                int y0 = s.F[0].Y;
                Tick(s, Neutral);
                lastStep = s.F[0].Y - y0;
            }

            Assert.AreEqual(FreeFallDistance1s, HighY - s.F[0].Y, FreeFallDistanceTol, "free-fall distance after 1 s (milli)");
            // Doc: free-fall speed after 1 s = -588.6 milli/tick (no cap); the tick-60 position step adds -C50 (section 2 step 3).
            Assert.AreEqual(-(GravityMilli * TicksPerSecond + C50Milli), lastStep, SpeedTol,
                            "fall step at t = 1 s must not be capped (old MaxFall 330)");
        }

        // Fast-fall (doc rows 18-20, Fighter.cs:307): trigger vy < 3 u/s, +45 u/s^2, clamp -24 u/s.
        const int FastFallEngageTick = 17;          // k after the jump tick (k = 0); doc: +-1 tick
        const double FastFallTerminalStep = -410.8; // milli/tick incl. C50; doc tolerance +-2
        const double FastFallTerminalTol = 2;
        const double FastFallDetect = -16;          // second difference: -9.81 normal vs -22.31 with fast-fall; midpoint
        const int FastFallStartY = 15000;
        // Terminal drop column: X = 7000 is outside every Sky Dojo platform's support band (stage-conversion.md section 1/2:
        // right platform cx 4200 + halfW 1700 + fighter 375 = 6275; centre |X| < 1875) and inside the main stage (8000).
        // At X = 0 the 36-tick drop from 15000 (~11 000 milli) crossed the centre platform top 5550 and landed mid-sample.
        const int FastFallColumnX = 7000;
        const int FastFallSettleTicks = 30;         // cap reached ~18 ticks after engaging from rest
        const int FastFallSampleTicks = 6;

        [Test]
        public void FastFall_EngagesNearApexAndReachesOriginalTerminalSpeed()
        {
            // Engage: jump, then hold Down from the next tick; find the first tick whose Y second difference shows the extra accel.
            var s = NewMatch(FighterClass.Melee);
            int yPrev = s.F[0].Y;
            Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Jump));       // k = 0
            int dPrev = s.F[0].Y - yPrev; yPrev = s.F[0].Y;
            int engage = -1;
            for (int k = 1; k <= 40 && !s.F[0].Grounded; k++)
            {
                Tick(s, Hold(ChibiDir.S));
                int d = s.F[0].Y - yPrev; yPrev = s.F[0].Y;
                if (d - dPrev < FastFallDetect) { engage = k; break; }
                dPrev = d;
            }

            // Terminal: drop from rest high on stage holding Down; sample the per-tick fall once the cap holds.
            var t = NewMatch(FighterClass.Melee);
            PlaceInAir(t, FastFallColumnX, FastFallStartY);
            for (int i = 0; i < FastFallSettleTicks; i++) Tick(t, Hold(ChibiDir.S));
            int yStart = t.F[0].Y;
            for (int i = 0; i < FastFallSampleTicks; i++) Tick(t, Hold(ChibiDir.S));
            double step = (t.F[0].Y - yStart) / (double)FastFallSampleTicks;

            Assert.AreEqual(FastFallEngageTick, engage, 1, "fast-fall must engage when vy < 3 u/s (not at vy <= 0)");
            Assert.IsFalse(t.F[0].Grounded, "fixture: still airborne while sampling");
            Assert.AreEqual(FastFallTerminalStep, step, FastFallTerminalTol, "fast-fall terminal fall per tick (milli)");
        }

        // ------------------------------------------------------------------ decelerations (doc rows 14-15, 23-25)

        [Test]
        public void AttackDecel_SlideDistance_MatchesOriginal()
        {
            // Heavy light: 27-tick lock (passive 0) > the 16 ticks the 25 u/s^2 slide needs, so the move never ends mid-slide.
            var s = NewMatch(FighterClass.Heavy, x: RunStartX);
            RunRight(s, AccelTicks);
            Tick(s, Hold(ChibiDir.E, ChibiButtons.Light));   // press frame: original still runs (actionLock 0 until the attack starts)
            int x0 = s.F[0].X;

            for (int t = 0; t < 40; t++) Tick(s, Neutral);

            double expected = DiscreteSlide(SpeedMilliPerTick(HeavyRunUps), AccelMilliPerTick2(AttackDecelUps2)); // 741.7
            Assert.AreEqual(expected, s.F[0].X - x0, SlideTol, "grounded slide during an attack (milli)");
        }

        [Test]
        public void ShieldDecel_SlideDistance_MatchesOriginal()
        {
            // Doc row 15 / Fighter.cs:276: grounded block applies MoveTowards(0, 60 u/s^2) from the frame it is raised.
            var s = NewMatch(FighterClass.Heavy, x: RunStartX);
            RunRight(s, AccelTicks);
            int x0 = s.F[0].X;

            for (int t = 0; t < 20; t++) Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Shield));

            double expected = DiscreteSlide(SpeedMilliPerTick(HeavyRunUps), AccelMilliPerTick2(ShieldDecelUps2)); // 280
            Assert.IsTrue(s.F[0].Shielding, "fixture: shield is up");
            Assert.AreEqual(expected, s.F[0].X - x0, SlideTol, "grounded slide while shielding (milli)");
        }

        const int StunTicks = 30;

        [Test]
        public void KnockbackWhileStunned_NoFriction_SlideMatchesOriginal()
        {
            // Velocity comes from the sim's own run (no Vx write); Stun is a tick count, so it is unit-safe.
            // Doc rows 23/25 (Fighter.cs:179-189): no x change at all while hitstun > 0. After stun the fighter can act,
            // so neutral input decelerates with MoveTowards(0, 70 u/s^2) (doc section 4 "Slide after stun ends").
            var s = NewMatch(FighterClass.Heavy, x: RunStartX);
            RunRight(s, AccelTicks);
            double v = SpeedMilliPerTick(HeavyRunUps);
            s.F[0].Stun = StunTicks;

            int stunned = 0, slide = 0;
            int minStep = int.MaxValue, maxStep = int.MinValue;
            bool stunOver = false;
            for (int t = 0; t < StunTicks + 25; t++)
            {
                int x0 = s.F[0].X;
                bool wasStunned = !stunOver;
                Tick(s, Neutral);
                int step = s.F[0].X - x0;
                if (wasStunned && s.F[0].Stun > 0)
                {
                    stunned++;
                    if (step < minStep) minStep = step;
                    if (step > maxStep) maxStep = step;
                }
                else { stunOver = true; slide += step; }   // the first free tick starts the post-stun slide
            }

            Assert.Greater(stunned, 0, "fixture: stun must last at least one tick");
            Assert.AreEqual(v, minStep, SpeedTol, "slowest step while stunned: no friction during hitstun");
            Assert.AreEqual(v, maxStep, SpeedTol, "fastest step while stunned");
            Assert.AreEqual(DiscreteSlide(v, AccelMilliPerTick2(GroundAccelUps2)), slide, SlideTol,
                            "slide after stun ends with no input (milli)"); // 233.3 from 105 milli/tick
        }

        // ------------------------------------------------------------------ rules (doc section 5 items 5, 7, 9)

        [Test]
        public void AirBlock_ShieldRaisesInAir_LikeOriginal()
        {
            // Doc 5.5 / Fighter.cs:261-262: wantBlock has no Grounded test; only the 60 u/s^2 slowdown is ground-gated.
            var s = NewMatch(FighterClass.Melee);
            Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Jump));
            for (int t = 0; t < 5; t++) Tick(s, Neutral);

            Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Shield));

            Assert.IsFalse(s.F[0].Grounded, "fixture: mid-air");
            Assert.IsTrue(s.F[0].Shielding, "holding Shield in the air must raise the block");
        }

        [Test]
        public void JumpDuringAttack_AllowedLikeOriginal()
        {
            // Doc 5.9 / Fighter.cs:291-304: the jump branch sits outside the actionLock gate, so it runs mid-attack.
            // Heavy light locks for 27 ticks; Jump is pressed 3 ticks in.
            var s = NewMatch(FighterClass.Heavy);
            Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Light));
            Tick(s, Neutral);
            Tick(s, Neutral);
            Assert.GreaterOrEqual(s.F[0].MoveSlot, 0, "fixture: the attack is still running");

            Tick(s, Hold(ChibiDir.Neutral, ChibiButtons.Jump));
            for (int t = 0; t < 3; t++) Tick(s, Neutral);

            Assert.IsFalse(s.F[0].Grounded, "jump pressed during an attack must leave the ground");
            Assert.Greater(s.F[0].Y, 0, "fighter must be above the floor");
        }

        const int HexTicks = 10000;   // SlowTicks (Hex slow, Fighter.cs:141) held for the whole run

        // Doc row 7/9 + 5.7 (Fighter.cs:139-143): Heavy passive 1 = Juggernaut x0.85; Hex x0.85; they multiply.
        // Expected milli/tick: 89.25 and 105 x 0.7225 = 75.8625 (doc +-0.01 milli/tick -> +-0.6 over 60 ticks, +1 remainder).
        [TestCase(false, 89.25)]
        [TestCase(true, 75.8625)]
        public void SpeedModifiers_Multiply_NotAdd(bool hexed, double expectedMilliPerTick)
        {
            var s = NewMatch(FighterClass.Heavy, passive1: 1, x: RunStartX);
            if (hexed) s.F[0].SlowTicks = HexTicks;
            RunRight(s, AccelTicks);
            int x0 = s.F[0].X;

            RunRight(s, TicksPerSecond);

            Assert.AreEqual(expectedMilliPerTick * TicksPerSecond, s.F[0].X - x0, 2.0,
                            "1 s of running with Juggernaut" + (hexed ? " + Hex" : "") + " (milli)");
        }
    }
}
