// Story type: Logic (stage geometry, one-way platforms, ring-out rules) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/stage-conversion.md (section 1 stage table, sections 2-6 rules, section 8 code
// contract, section 9 reference values + tolerances). Original: Stage.cs, Fighter.cs, MatchController.cs.
//
// User decisions applied on top of the doc:
//   D-S1 = fix the "grounded inside a platform slab" glitch: Grounded ONLY when truly resting on a surface. A fighter
//          whose jump apex leaves its feet inside a slab is NOT grounded and does NOT get its air jump back.
//          (This overrides doc section 5 "Mid-air grounding inside a slab" and section 9 row 7 "Grounded true for 7-8 ticks".)
//   D-S2 = clean rule: falling onto a platform top from above ALWAYS lands (no Box2D fat-AABB slip-through).
//
// Compiles against today's sim: ChibiMatchConfig.Stage is set through reflection (Cfg) and every test FAILS with
// "Stage field missing" until section 8 is implemented. No stage-table type, DropTicks or DropPlatform is referenced.
// The tests measure ONLY positions (milli), tick counts, Grounded, AirJumps and Hp. They never read or assert Vx / Vy;
// the only velocity writes are "Vx = Vy = 0" (unit-independent) to start from rest. Upward motion comes from real jumps.
using System.Reflection;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Stage parity between the deterministic 60 Hz simulation and the original Stage.cs / Fighter.cs (Box2D).
    /// Reference values come from stage-conversion.md section 9; tolerances from its header line.
    /// </summary>
    public class ChibiStageParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;            // spawn drop (~12 ticks, row 17) is long finished

        // ---- tolerances (doc section 9 header): positions +-1 milli where exact, tick counts +-1 (variable-dt original)
        const double PosTol = 1;
        const int TickTol = 1;
        const double HeightTol = 3;             // movement-conversion.md section 4: integer-tick apex sampling

        // ---- stage indices (doc section 0, Stage.cs:19-51)
        const int SkyDojo = 0, NeonCity = 1, LavaForge = 2;
        const int OutOfRangeStage = 5;          // Stage.cs `default:` builds Sky Dojo (doc section 8, row 18)

        // ---- geometry (doc section 1 table, sim milli; feet-relative)
        const int MainTop = 0;
        const int MainBottom = -1200;           // Stage.cs:74-80
        const int HurtH = 1350;                 // Fighter.cs:97-99
        const int PlatThick = 300;              // Stage.cs:86-96
        const int SpawnY = 775;                 // groundY + 1.5 transform -> +0.775 feet (doc section 1)
        const int RespawnX = 0, RespawnY = 7775;// (0, 6) transform (MatchController.cs:86)
        const int KillY = -9725;                // groundY - 9 transform (Stage.cs:65-67)
        const int CeilingY = 17775;             // 16 transform (Stage.cs:65-67)
        const int SkyA_Cx = -4200, SkyA_Top = 3050, SkyA_HalfW = 1700;
        const int NeonB_Cx = 5500, NeonB_Top = 2450;

        // ---- jump heights (movement-conversion.md section 4, D1 = YES: C50 term included)
        const int GroundJumpApex = 2264;
        const int AirJumpApex = 1759;

        static readonly ChibiInputPacket Neutral = default;
        static ChibiInputPacket Hold(ChibiDir d, ChibiButtons b = ChibiButtons.None) => ChibiInputPacket.Make(d, b);
        static readonly ChibiInputPacket JumpPress = Hold(ChibiDir.Neutral, ChibiButtons.Jump);
        static readonly ChibiInputPacket DownJump = Hold(ChibiDir.S, ChibiButtons.Jump);

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Config with <c>Stage</c> set by reflection (doc section 8: <c>ChibiMatchConfig.Stage</c>, int). Fails the test
        /// while the field does not exist. ChibiMatchConfig is a struct: box, SetValue on the box, unbox.
        /// </summary>
        static ChibiMatchConfig Cfg(int stage)
        {
            var cfg = new ChibiMatchConfig { Seed = Seed, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy };
            FieldInfo field = typeof(ChibiMatchConfig).GetField("Stage", BindingFlags.Public | BindingFlags.Instance);
            if (field == null) Assert.Fail("Stage field missing: ChibiMatchConfig.Stage (stage-conversion.md section 8) is not implemented");
            object boxed = cfg;
            field.SetValue(boxed, stage);
            return (ChibiMatchConfig)boxed;
        }

        static ChibiSimState Fresh(int stage)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, Cfg(stage));
            return s;
        }

        /// <summary>Fresh match on <paramref name="stage"/>, settled for SettleTicks with neutral input.</summary>
        static ChibiSimState NewMatch(int stage)
        {
            var s = Fresh(stage);
            for (int t = 0; t < SettleTicks; t++) ChibiSimulation.Step(s, default, default);
            return s;
        }

        static void Tick(ChibiSimState s, ChibiInputPacket p1) => ChibiSimulation.Step(s, p1, default);

        /// <summary>P1 at rest in the air with its air jump available (or not).</summary>
        static void PlaceInAir(ChibiSimState s, int x, int y, int airJumps = 1)
        {
            var f = s.F[0];
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = false; f.AirJumps = airJumps;
        }

        /// <summary>P1 standing on a surface whose top is <paramref name="y"/>, then 3 neutral ticks to settle.</summary>
        static void PlaceOn(ChibiSimState s, int x, int y)
        {
            var f = s.F[0];
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = true; f.AirJumps = 1;
            for (int t = 0; t < 3; t++) Tick(s, Neutral);
        }

        /// <summary>Ticks neutral until P1 is Grounded; returns the 1-based tick or -1 after <paramref name="max"/>.</summary>
        static int TicksUntilGrounded(ChibiSimState s, int max)
        {
            for (int n = 1; n <= max; n++)
            {
                Tick(s, Neutral);
                if (s.F[0].Grounded) return n;
            }
            return -1;
        }

        // ------------------------------------------------------------------ spawn (doc section 1, row 17)

        // Spawn tick count is "about tick 12" for a 775-milli drop from rest.
        const int SpawnLandTick = 12;

        [TestCase(SkyDojo, 3600)]      // Stage.cs:29 halfWidth 8 x 0.45
        [TestCase(NeonCity, 4050)]     // Stage.cs:37 halfWidth 9 x 0.45
        [TestCase(LavaForge, 3150)]    // Stage.cs:45 halfWidth 7 x 0.45
        public void Spawn_PositionsPerStage_MatchOriginal(int stage, int spawnX)
        {
            // Arrange + Act (Init)
            var s = Fresh(stage);

            // Assert: spawn transform (+-halfWidth*0.45, groundY+1.5) -> feet 775, airborne
            Assert.AreEqual(-spawnX, s.F[0].X, "P0 spawn X");
            Assert.AreEqual(spawnX, s.F[1].X, "P1 spawn X");
            Assert.AreEqual(SpawnY, s.F[0].Y, "P0 spawn Y (feet; sim used 0)");
            Assert.AreEqual(SpawnY, s.F[1].Y, "P1 spawn Y (feet)");
            Assert.IsFalse(s.F[0].Grounded, "P0 spawns airborne");
            Assert.IsFalse(s.F[1].Grounded, "P1 spawns airborne");

            // Act: drop onto the main stage
            int landTick = TicksUntilGrounded(s, 60);

            Assert.AreEqual(SpawnLandTick, landTick, TickTol, "ticks to land from the spawn drop");
            Assert.AreEqual(MainTop, s.F[0].Y, PosTol, "lands on the main-stage top");
            Assert.AreEqual(-spawnX, s.F[0].X, "spawn drop is vertical");
        }

        // ------------------------------------------------------------------ one-way platforms: landing (doc section 2, rows 1-3)

        // expectedTicks -1 = the doc gives no tick count for this setup.
        [TestCase(SkyDojo, -4200, 3050, 4000, 14)]   // row 1 (platform A)
        [TestCase(SkyDojo, 4200, 3050, 4000, 14)]    // platform B, mirror of row 1
        [TestCase(SkyDojo, 0, 5550, 6500, -1)]       // platform C
        [TestCase(NeonCity, -5500, 2450, 7000, -1)]  // platform A, mirror of row 2
        [TestCase(NeonCity, 5500, 2450, 7000, -1)]   // row 2 (platform B)
        [TestCase(NeonCity, 0, 4650, 6500, -1)]      // platform C
        [TestCase(LavaForge, -3600, 3250, 5000, -1)] // platform A, mirror of row 3
        [TestCase(LavaForge, 3600, 3250, 5000, -1)]  // row 3 (platform B)
        public void Platform_LandFromAbove_EachPlatformEachStage(int stage, int cx, int top, int startY, int expectedTicks)
        {
            // Arrange: at rest above the platform centre, air jump already spent (landing must restore it)
            var s = NewMatch(stage);
            PlaceInAir(s, cx, startY, airJumps: 0);

            // Act
            int landTick = TicksUntilGrounded(s, 90);
            for (int t = 0; t < 10; t++) Tick(s, Neutral);   // stays put

            // Assert
            Assert.AreNotEqual(-1, landTick, "never grounded: fell through the platform");
            if (expectedTicks > 0) Assert.AreEqual(expectedTicks, landTick, TickTol, "ticks to land");
            Assert.AreEqual(top, s.F[0].Y, PosTol, "feet Y == platform top");
            Assert.IsTrue(s.F[0].Grounded, "Grounded on the platform");
            Assert.AreEqual(1, s.F[0].AirJumps, "landing on a platform restores the air jump");
        }

        // Rows 5-6: Sky Dojo, standing at X = -4200 under platform A (underside 2750, top 3050).
        [Test]
        public void Platform_JumpUpThroughFromBelow_NoSnap()
        {
            // Arrange
            var s = NewMatch(SkyDojo);
            PlaceOn(s, SkyA_Cx, MainTop);

            // Act (row 5): plain ground jump, the head passes the underside, no collision
            int maxY = int.MinValue, prevY = s.F[0].Y, apexTick = -1;
            for (int n = 1; n <= 40 && apexTick < 0; n++)
            {
                Tick(s, n == 1 ? JumpPress : Neutral);
                if (s.F[0].Y < prevY) apexTick = n;
                prevY = s.F[0].Y;
                if (s.F[0].Y > maxY) maxY = s.F[0].Y;
            }

            // Assert (row 5): unobstructed apex, nowhere near the platform top
            Assert.AreEqual(GroundJumpApex, maxY, HeightTol, "ground jump under a platform is not bonked by its underside");
            Assert.Less(maxY, SkyA_Top - PlatThick, "fixture: apex stays below the slab");

            // Act (row 6): air jump at the apex; feet rise through the whole slab
            bool snappedRising = false;
            int maxY2 = int.MinValue;
            // The loop above stops on the first falling tick (one tick past the apex), so the air jump takes off
            // from here, not from maxY. AirJumpApex is "apex above take-off" (movement-conversion.md).
            int takeoffY = s.F[0].Y;
            prevY = takeoffY;
            int landTick = -1;
            for (int n = 1; n <= 90; n++)
            {
                Tick(s, n == 1 ? JumpPress : Neutral);
                int y = s.F[0].Y;
                if (y > prevY && s.F[0].Grounded) snappedRising = true;
                if (y > maxY2) maxY2 = y;
                prevY = y;
                if (n > 1 && s.F[0].Grounded) { landTick = n; break; }
            }

            // Assert (row 6)
            Assert.IsFalse(snappedRising, "no snap / ground state while rising through the platform");
            Assert.Greater(maxY2, SkyA_Top, "feet pass the platform top going up");
            Assert.AreEqual(takeoffY + AirJumpApex, maxY2, HeightTol, "air jump through the slab is unobstructed");
            Assert.AreNotEqual(-1, landTick, "never landed");
            Assert.AreEqual(SkyA_Top, s.F[0].Y, PosTol, "lands on platform A on the way down");
        }

        // D-S2: an apex only a few milli above the top still lands (no fat-AABB slip-through).
        [Test]
        public void Platform_ApexJustAboveTop_LandsOnFall()
        {
            // Arrange: air jump from below so the apex is ~5 milli above Sky Dojo A's top
            const int ClearanceAboveTop = 5;
            var s = NewMatch(SkyDojo);
            PlaceInAir(s, SkyA_Cx, SkyA_Top - AirJumpApex + ClearanceAboveTop);

            // Act
            int maxY = int.MinValue, landTick = -1;
            for (int n = 1; n <= 90; n++)
            {
                Tick(s, n == 1 ? JumpPress : Neutral);
                if (s.F[0].Y > maxY) maxY = s.F[0].Y;
                if (n > 1 && s.F[0].Grounded) { landTick = n; break; }
            }

            // Assert
            Assert.Greater(maxY, SkyA_Top, "fixture: apex must clear the top");
            Assert.LessOrEqual(maxY - SkyA_Top, ClearanceAboveTop + (int)HeightTol, "fixture: apex only just clears the top");
            Assert.AreNotEqual(-1, landTick, "never landed");
            Assert.AreEqual(SkyA_Top, s.F[0].Y, PosTol, "D-S2: falling onto the top from above always lands");
        }

        // Row 8: Sky Dojo A left edge. Grounded while 2|dx| < 2*1700 + 675, supported while |dx| < 1700 + 375.
        const int SkyA_LastGroundedX = -6237, SkyA_LastSupportedX = -6274, SkyA_FallX = -6275;

        [Test]
        public void Platform_WalkOffEdge_Falls()
        {
            // Arrange: standing on A
            var s = NewMatch(SkyDojo);
            PlaceOn(s, SkyA_Cx, SkyA_Top);
            Assert.AreEqual(SkyA_Top, s.F[0].Y, PosTol, "precondition: resting on platform A");

            // Act: walk left off the edge, check every tick
            int landTick = -1;
            bool leftPlatform = false;
            for (int n = 1; n <= 120; n++)
            {
                // Release left once off the platform: holding it for the whole fall (~25 ticks at run speed) would carry
                // the fighter past the main stage's left edge (-8375) and ring it out, in the original as well.
                Tick(s, leftPlatform ? Neutral : Hold(ChibiDir.W));
                var f = s.F[0];
                if (f.Y == SkyA_Top)
                {
                    Assert.Greater(f.X, SkyA_FallX, "tick " + n + ": still on platform A beyond the support width");
                    if (f.Grounded) Assert.GreaterOrEqual(f.X, SkyA_LastGroundedX, "tick " + n + ": Grounded beyond the probe width");
                }
                else if (f.Y < SkyA_Top - PlatThick) leftPlatform = true;
                if (leftPlatform && f.Grounded && f.Y <= MainTop + PosTol) { landTick = n; break; }
            }

            // Assert: fell off and landed on the main stage below
            Assert.IsTrue(leftPlatform, "never walked off platform A");
            Assert.AreNotEqual(-1, landTick, "never landed on the main stage after walking off");
            Assert.AreEqual(MainTop, s.F[0].Y, PosTol, "lands on the main-stage top");
        }

        // ------------------------------------------------------------------ ledge band (doc section 5, rows 8-9)

        // probe half-width 337.5 vs support half-width 375: supported but not Grounded in between.
        [TestCase(SkyA_Cx, SkyA_Top, SkyA_LastGroundedX, true, true)]        // row 8
        [TestCase(SkyA_Cx, SkyA_Top, -6238, false, true)]                    // row 8: ledge band starts
        [TestCase(SkyA_Cx, SkyA_Top, SkyA_LastSupportedX, false, true)]      // row 8
        [TestCase(SkyA_Cx, SkyA_Top, SkyA_FallX, false, false)]              // row 8: falls
        [TestCase(0, MainTop, 8337, true, true)]                             // row 9: main stage HW 8000
        [TestCase(0, MainTop, 8338, false, true)]                            // row 9
        [TestCase(0, MainTop, 8374, false, true)]                            // row 9
        [TestCase(0, MainTop, 8375, false, false)]                           // row 9: falls
        public void LedgeBand_SupportedButNotGroundedWidth(int cx, int top, int x, bool expectGrounded, bool expectSupported)
        {
            // Arrange: standing exactly at X on the surface top, at rest
            var s = NewMatch(SkyDojo);
            var f = s.F[0];
            f.X = x; f.Y = top; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = true; f.AirJumps = 1;

            // Act
            const int Hold5 = 5;
            bool everGrounded = false, allGrounded = true;
            for (int t = 0; t < Hold5; t++)
            {
                Tick(s, Neutral);
                everGrounded |= f.Grounded;
                allGrounded &= f.Grounded;
            }

            // Assert
            Assert.AreEqual(x, f.X, "fixture: X did not drift");
            if (expectSupported) Assert.AreEqual(top, f.Y, PosTol, "supported: feet stay on the top");
            else Assert.Less(f.Y, top - PosTol, "unsupported: falls");
            if (expectGrounded) Assert.IsTrue(allGrounded, "Grounded every tick inside the probe width");
            else Assert.IsFalse(everGrounded, "not Grounded outside the probe width");
        }

        // ------------------------------------------------------------------ drop-through (doc section 3, rows 10-11)

        const int DropTicks = 21;               // 0.35 s x 60 (Fighter.cs:291-297, 347-361)
        const int DropLandAfterTicks = 24;      // row 10: lands Y = 0 about tick T+24 (no fast-fall)

        [Test]
        public void Platform_DownJump_DropsThroughFor21Ticks_ThenLandsBelow()
        {
            // Arrange: resting on Sky Dojo A
            var s = NewMatch(SkyDojo);
            PlaceOn(s, SkyA_Cx, SkyA_Top);
            Assert.AreEqual(SkyA_Top, s.F[0].Y, PosTol, "precondition: resting on platform A");
            Assert.IsTrue(s.F[0].Grounded, "precondition: Grounded on platform A");

            // Act: Down + Jump on tick T
            Tick(s, DownJump);

            // Assert (tick T): jump consumed, no rise, air jump not spent, feet already below the top
            Assert.Less(s.F[0].Y, SkyA_Top, "feet below 3050 from tick T (platform ignored on T)");
            Assert.AreEqual(1, s.F[0].AirJumps, "drop-through does not spend the air jump");

            // Act: ticks T+1 .. landing, neutral
            int prevY = s.F[0].Y, landAfter = -1;
            for (int n = 1; n <= 60; n++)
            {
                Tick(s, Neutral);
                int y = s.F[0].Y;
                Assert.LessOrEqual(y, prevY, "tick T+" + n + ": no upward motion (the jump was consumed)");
                if (n >= DropTicks) Assert.Less(y, SkyA_Top, "tick T+" + n + ": no re-land on A after the ignore ends");
                prevY = y;
                if (s.F[0].Grounded) { landAfter = n; break; }
            }

            // Assert: lands on the main stage
            Assert.AreNotEqual(-1, landAfter, "never landed after dropping through");
            Assert.AreEqual(DropLandAfterTicks, landAfter, TickTol, "ticks after T to land on the main stage");
            Assert.AreEqual(MainTop, s.F[0].Y, PosTol, "lands on the main stage below");
        }

        [Test]
        public void MainStage_DownJump_DoesNotDropThrough()
        {
            // Arrange: standing on the main stage, clear of platforms A/B (X = 0, C is 5550 up)
            var s = NewMatch(SkyDojo);
            PlaceOn(s, 0, MainTop);

            // Act (row 11): Down + Jump = normal ground jump
            int maxY = int.MinValue, minY = int.MaxValue, landTick = -1;
            for (int n = 1; n <= 90; n++)
            {
                Tick(s, n == 1 ? DownJump : Neutral);
                if (s.F[0].Y > maxY) maxY = s.F[0].Y;
                if (s.F[0].Y < minY) minY = s.F[0].Y;
                if (n > 1 && s.F[0].Grounded) { landTick = n; break; }
            }

            // Assert
            Assert.GreaterOrEqual(minY, MainTop, "never drops below the main-stage top");
            Assert.AreEqual(GroundJumpApex, maxY, HeightTol, "Down+Jump on the main stage is a full ground jump");
            Assert.AreNotEqual(-1, landTick, "never landed");
            Assert.AreEqual(MainTop, s.F[0].Y, PosTol, "lands back on the main stage");
        }

        // ------------------------------------------------------------------ solid main stage (doc section 4, rows 13-14)

        const int SkyWallX = -(8000 + 375);     // -(HW + HurtHalfW)

        [Test]
        public void MainStage_SideWall_PushesOut()
        {
            // Arrange (row 13): beside the stage body, holding toward it
            var s = NewMatch(SkyDojo);
            PlaceInAir(s, -9000, -600);

            // Act + Assert: every tick while the boxes overlap vertically, X never enters the wall
            bool pinned = false;
            for (int n = 1; n <= 60; n++)
            {
                Tick(s, Hold(ChibiDir.E));
                var f = s.F[0];
                bool besideBody = f.Y < MainTop && f.Y + HurtH > MainBottom;
                if (!besideBody) break;
                Assert.LessOrEqual(f.X, SkyWallX, "tick " + n + ": fighter box entered the main-stage side wall");
                if (f.X == SkyWallX) pinned = true;
            }

            Assert.IsTrue(pinned, "never pinned at X = -8375 against the wall");
        }

        const int BonkFeetY = MainBottom - HurtH;   // -2550: head at the stage underside

        [Test]
        public void MainStage_Underside_StopsUpwardMotion()
        {
            // Arrange (row 14): under the stage at X = 0; the air jump gives Vy = AirJumpVel 172500 as in the row
            const int StartY = -3000;
            var s = NewMatch(SkyDojo);
            PlaceInAir(s, 0, StartY);

            // Act
            int maxY = int.MinValue, bonkTick = -1;
            for (int n = 1; n <= 40; n++)
            {
                Tick(s, n == 1 ? JumpPress : Neutral);
                if (s.F[0].Y > maxY) { maxY = s.F[0].Y; bonkTick = n; }
            }

            // Assert
            Assert.Greater(StartY + AirJumpApex + HurtH, MainBottom, "fixture: unobstructed head would pass the underside");
            Assert.AreEqual(BonkFeetY, maxY, PosTol, "head stops at the underside: feet Y = -2550");
            Assert.Less(s.F[0].Y, StartY, "falls after the bonk");
            Assert.Less(bonkTick, 40, "fixture: apex found");
        }

        // ------------------------------------------------------------------ D-S1: no mid-air grounding inside a slab

        [Test]
        public void JumpUnderPlatform_NotGroundedMidAir()
        {
            // Arrange (row 7, D-S1 overrides its "Grounded 7-8 ticks"): Neon City, under platform B
            const int MainProbeBand = 100;      // main-stage probe band [0, 100] is real ground contact, excluded
            var s = NewMatch(NeonCity);
            PlaceOn(s, NeonB_Cx, MainTop);

            // Act: ground jump, then remove the air jump so a slab "reset" would be visible
            Tick(s, JumpPress);
            s.F[0].AirJumps = 0;
            int maxY = int.MinValue, landTick = -1;
            for (int n = 1; n <= 90; n++)
            {
                Tick(s, Neutral);
                var f = s.F[0];
                if (f.Y > maxY) maxY = f.Y;
                if (f.Y > MainProbeBand)
                {
                    Assert.IsFalse(f.Grounded, "tick " + n + ": Grounded mid-air with feet at " + f.Y + " (D-S1)");
                    Assert.AreEqual(0, f.AirJumps, "tick " + n + ": air jump restored mid-air inside the slab (D-S1)");
                }
                if (f.Grounded && f.Y <= MainTop + PosTol) { landTick = n; break; }
            }

            // Assert
            Assert.Greater(maxY, NeonB_Top - PlatThick, "fixture: apex must reach inside the slab (2150, 2450]");
            Assert.LessOrEqual(maxY, NeonB_Top, "fixture: no snap onto the platform top");
            Assert.AreNotEqual(-1, landTick, "never landed back on the main stage");
            Assert.AreEqual(MainTop, s.F[0].Y, PosTol, "lands Y = 0");
            Assert.AreEqual(1, s.F[0].AirJumps, "air jump restored only by the real landing");
        }

        // ------------------------------------------------------------------ ring-out (doc section 6, rows 4, 15-16)

        const int RingOutPenaltyPermille = 150;     // Fighter.cs:692-708, -15% max HP
        const int ProbeHighY = 12000;               // above every surface, below the ceiling

        /// <summary>Feet drop of one neutral tick from rest (measured, so velocity units stay out of the test).</summary>
        static int OneTickDropFromRest(int stage)
        {
            var s = NewMatch(stage);
            PlaceInAir(s, 0, ProbeHighY);
            Tick(s, Neutral);
            return ProbeHighY - s.F[0].Y;
        }

        /// <summary>Places P1 at rest so its feet are at (x, y) after one tick, ticks, and reports a ring-out.</summary>
        static bool RingsOutAt(int stage, int x, int y, int drop, out ChibiSimState s, out int hp0)
        {
            s = NewMatch(stage);
            hp0 = s.F[0].Hp;
            PlaceInAir(s, x, y + drop);
            Tick(s, Neutral);
            return s.F[0].Hp < hp0;
        }

        static void AssertRingOut(ChibiSimState s, int hp0, string what)
        {
            var f = s.F[0];
            Assert.AreEqual(hp0 - f.Stats.MaxHp * RingOutPenaltyPermille / 1000, f.Hp, what + ": -15% max HP");
            Assert.AreEqual(RespawnX, f.X, what + ": respawn X");
            Assert.AreEqual(RespawnY, f.Y, PosTol, what + ": respawn Y 7775 (feet)");
            Assert.IsFalse(f.Grounded, what + ": respawns airborne");
            Assert.IsFalse(s.MatchOver, what + ": ring-out is not a KO");
        }

        [TestCase(SkyDojo, 17000)]     // row 15, halfWidth 8 + 9
        [TestCase(NeonCity, 18000)]    // row 16
        [TestCase(LavaForge, 16000)]   // row 16
        public void RingOut_BoundariesPerStage(int stage, int blastX)
        {
            // Arrange
            int d = OneTickDropFromRest(stage);
            Assert.Greater(d, 0, "fixture: measured one-tick drop");

            // Act + Assert: strict > / < on feet coordinates (MatchController.cs:148)
            Assert.IsFalse(RingsOutAt(stage, blastX, ProbeHighY - d, d, out _, out _), "X = +blastX is not a ring-out");
            Assert.IsFalse(RingsOutAt(stage, -blastX, ProbeHighY - d, d, out _, out _), "X = -blastX is not a ring-out");
            Assert.IsTrue(RingsOutAt(stage, blastX + 1, ProbeHighY - d, d, out var sx, out int hx), "X = +blastX+1 rings out");
            AssertRingOut(sx, hx, "right edge");
            Assert.IsTrue(RingsOutAt(stage, -blastX - 1, ProbeHighY - d, d, out var sxl, out int hxl), "X = -blastX-1 rings out");
            AssertRingOut(sxl, hxl, "left edge");

            Assert.IsFalse(RingsOutAt(stage, 0, KillY, d, out _, out _), "Y = -9725 is not a ring-out");
            Assert.IsTrue(RingsOutAt(stage, 0, KillY - 1, d, out var sb, out int hb), "Y = -9726 rings out");
            AssertRingOut(sb, hb, "bottom");

            Assert.IsFalse(RingsOutAt(stage, 0, CeilingY, d, out _, out _), "Y = 17775 is not a ring-out");
            Assert.IsTrue(RingsOutAt(stage, 0, CeilingY + 1, d, out var st, out int ht), "Y = 17776 rings out");
            AssertRingOut(st, ht, "ceiling (ring-out, not KO)");
        }

        // Row 4: respawn (0, 7775) lands on platform C on Sky Dojo / Neon City, on the main stage on Lava Forge.
        [TestCase(SkyDojo, 17000, 5550, 21)]
        [TestCase(NeonCity, 18000, 4650, 25)]
        [TestCase(LavaForge, 16000, MainTop, 40)]
        public void RingOut_Respawn_LandsOnTopSurface(int stage, int blastX, int expectedTop, int expectedTicks)
        {
            // Arrange: ring out off the right edge
            var s = NewMatch(stage);
            int hp0 = s.F[0].Hp;
            PlaceInAir(s, blastX + 1000, ProbeHighY);
            Tick(s, Neutral);
            AssertRingOut(s, hp0, "precondition");

            // Act
            int landTick = TicksUntilGrounded(s, 90);

            // Assert
            Assert.AreNotEqual(-1, landTick, "respawned fighter never landed");
            Assert.AreEqual(expectedTicks, landTick, TickTol, "ticks from respawn to landing");
            Assert.AreEqual(expectedTop, s.F[0].Y, PosTol, "respawn lands on the highest surface under X = 0");
        }

        // ------------------------------------------------------------------ config (doc section 8, row 18)

        const int StageRulesVersion = 7;    // section 8: bump RulesVersion to 7

        [Test]
        public void StageInConfigHash_DifferentStage_DifferentHash()
        {
            // Arrange
            var sky = Cfg(SkyDojo);
            var neon = Cfg(NeonCity);
            var lava = Cfg(LavaForge);

            // Act
            uint h0 = sky.ComputeHash(), h1 = neon.ComputeHash(), h2 = lava.ComputeHash();

            // Assert
            Assert.AreNotEqual(h0, h1, "Stage 0 vs 1 must differ in ComputeHash");
            Assert.AreNotEqual(h0, h2, "Stage 0 vs 2 must differ in ComputeHash");
            Assert.AreNotEqual(h1, h2, "Stage 1 vs 2 must differ in ComputeHash");
            Assert.GreaterOrEqual(ChibiMatchConfig.RulesVersion, StageRulesVersion, "RulesVersion bumped for stages");

            // Out-of-range stage plays as Sky Dojo (Stage.cs `default:`)
            var odd = Fresh(OutOfRangeStage);
            Assert.AreEqual(-3600, odd.F[0].X, "Stage 5 spawns like Sky Dojo (P0)");
            Assert.AreEqual(3600, odd.F[1].X, "Stage 5 spawns like Sky Dojo (P1)");
        }
    }
}
