// Story type: Logic + Integration (rules v20 stages 3..7, ST-1..ST-5, GL-2 / GL-3) -- evidence: automated EditMode test -- gate: BLOCKING
// Spec: design/gdd/stages/README.md (3.1 edges, 3.2 F1 moving platforms, 3.3 events, 8 acceptance) and 01..05 stage files;
// geometry: design/gdd/stages/stage-geometry.json via ChibiStageTable.
using System;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>KO edges vs walls / floors / sky caps, moving platforms and riders, the three stage events, the hazard RNG
    /// stream, determinism, rollback and fuzz on the new stages.</summary>
    public class ChibiStagesV20Tests
    {
        static readonly ChibiInputPacket N = default;
        const uint Seed = 20_2611;

        static ChibiSimState New(int stage, bool hazardsOff = false, uint seed = Seed, bool training = false)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig
            {
                Seed = seed, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy, Stage = stage, HazardsOff = hazardsOff,
                Training = training,
            });
            return s;
        }

        static void Step(ChibiSimState s, ChibiInputPacket a = default, ChibiInputPacket b = default) => ChibiTestTime.StepGame(s, a, b);

        static void Put(ChibiFighterSim f, int x, int y)
        {
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = false; f.Stun = 0; f.Invuln = 0;
            f.HoverTicks = 0; f.OutTicks = 0;
        }

        /// <summary>One step with F[0] placed at (x, y) at rest just before it; true when that step KO'd F[0].</summary>
        static bool KoAt(int stage, int x, int y)
        {
            var s = New(stage, hazardsOff: true);
            Put(s.F[0], x, y);
            s.F[0].Vy = ChibiSimConst.Gravity;    // cancels this step's gravity so the feet stay at y (no arc while > 0)
            Step(s);
            return s.F[0].KosTaken > 0;
        }

        // ================================================================== edges (README 3.1, AC 1)

        [TestCase(ChibiStageTable.Mountain, false, true, true)]
        [TestCase(ChibiStageTable.Plain, true, false, true)]       // rules v21: ground ends, KO bottom below its ends
        [TestCase(ChibiStageTable.Volcano, true, true, true)]
        [TestCase(ChibiStageTable.BirdLair, true, false, true)]    // rules v21: finite cloud floor, KO bottom beyond it
        [TestCase(ChibiStageTable.Ocean, true, true, true)]
        public void KoEdges_MatchTheSheetMatrix(int stage, bool sides, bool top, bool bottom)
        {
            var st = ChibiStageTable.Get(stage);
            Assert.AreEqual(sides, st.KoSides, "side KO flag");
            Assert.AreEqual(top, st.KoTop, "top KO flag");
            Assert.AreEqual(bottom, st.KoBottom, "bottom KO flag");
            int midY = 4000;
            Assert.AreEqual(sides, KoAt(stage, st.BlastX + 1000, midY), "past the right edge");
            Assert.AreEqual(sides, KoAt(stage, -st.BlastX - 1000, midY), "past the left edge");
            Assert.AreEqual(top, KoAt(stage, 0, st.CeilingY + 1000), "past the top edge");
            if (bottom) Assert.IsTrue(KoAt(stage, st.HalfWidth + 3000, st.KillY - 1000), "past the bottom edge");
        }

        [Test]
        public void Mountain_Wall_StopsAnySpeed_NeverKOs()
        {
            // 01-mountain.md AC 1 (wall at the GL-2 / json value 16000, centre clamp 15625).
            var st = ChibiStageTable.Get(ChibiStageTable.Mountain);
            var s = New(ChibiStageTable.Mountain, hazardsOff: true);
            Put(s.F[0], st.WallClampX - 2000, 6000);
            for (int t = 0; t < 30; t++)
            {
                s.F[0].Vx = 300_000;
                Step(s);
                Assert.LessOrEqual(s.F[0].X, st.WallClampX, $"clamped at the wall (tick {t})");
            }
            Assert.AreEqual(st.WallClampX, s.F[0].X, "pressed against the wall");
            Assert.AreEqual(0, s.F[0].Vx, "Vx into the wall zeroed");
            Assert.AreEqual(0, s.F[0].KosTaken, "a wall never KOs");
            Assert.AreEqual(st.BlastX - ChibiSimConst.HurtHalfW, st.WallClampX);
        }

        [TestCase(ChibiStageTable.Plain)]
        [TestCase(ChibiStageTable.BirdLair)]
        public void NoTopStages_HugeLaunchUp_CappedAndFallsBackToTheFloor(int stage)
        {
            // 02-plain AC 2-3 / 04-bird-lair AC 6.
            var st = ChibiStageTable.Get(stage);
            var s = New(stage, hazardsOff: true);
            Put(s.F[0], 0, 0);
            s.F[0].Vy = 900_000;
            int maxY = int.MinValue, minY = int.MaxValue;
            for (int t = 0; t < 900; t++)
            {
                Step(s);
                maxY = Math.Max(maxY, s.F[0].Y); minY = Math.Min(minY, s.F[0].Y);
            }
            Assert.LessOrEqual(maxY, ChibiStageTable.SkyCapY, "never above the sky cap");
            Assert.AreEqual(ChibiStageTable.SkyCapY, maxY, "reached the cap");
            Assert.GreaterOrEqual(minY, 0, "lands back on MAIN, never below it");
            Assert.IsTrue(s.F[0].Grounded, "landed again");
            Assert.AreEqual(0, s.F[0].KosTaken, "no KO");
        }

        [Test]
        public void BirdLair_KnockedOffThePlateau_LandsOnTheCloudFloor()
        {
            var st = ChibiStageTable.Get(ChibiStageTable.BirdLair);
            var s = New(ChibiStageTable.BirdLair, hazardsOff: true);
            Put(s.F[0], st.HalfWidth + 2000, 500);
            for (int t = 0; t < 120 && !s.F[0].Grounded; t++) Step(s);
            Assert.IsTrue(s.F[0].Grounded);
            Assert.IsTrue(st.HasFloor, "Bird Lair has the cloud floor");
            Assert.AreEqual(st.FloorY, s.F[0].Y, "stands on the floor at -3000");
            Step(s, ChibiInputPacket.Make(ChibiDir.Neutral, ChibiButtons.Jump));
            Assert.Greater(s.F[0].Vy, 0, "a ground jump from the floor");
        }

        [Test]
        public void Plain_WalkingOffTheGroundEnd_FallsAndKOsAtTheBottom_SideLine8000Beyond()
        {
            // Rules v21 (lead, GL-2 consistently): the ground ends at +-16991 and the side KO line is 8000 beyond it.
            var st = ChibiStageTable.Get(ChibiStageTable.Plain);
            Assert.GreaterOrEqual(st.BlastX - st.HalfWidth, 8000, "side line >= 8000 beyond the ground");
            var s = New(ChibiStageTable.Plain, hazardsOff: true);
            Put(s.F[0], st.HalfWidth - 300, 0);
            s.F[0].Grounded = true;
            Put(s.F[1], -10000, 0); s.F[1].Grounded = true;
            for (int t = 0; t < 300 && s.F[0].KosTaken == 0; t++)
            {
                Step(s, ChibiInputPacket.Make(ChibiDir.E, ChibiButtons.None));
                if (s.F[0].KosTaken == 0) Assert.LessOrEqual(s.F[0].X, st.BlastX, "inside the side line");
            }
            Assert.AreEqual(1, s.F[0].KosTaken, "falls past the ground's end and is KO'd");
            Assert.Less(s.F[0].Y, st.KillY, "by the bottom line, not the side line");
        }

        [Test]
        public void EverySideKoLine_AtLeast8000BeyondTheOutermostWalkableSurface()
        {
            for (int stage = ChibiStageTable.Mountain; stage < ChibiStageTable.Count; stage++)
            {
                var st = ChibiStageTable.Get(stage);
                if (!st.KoSides) continue;
                Assert.GreaterOrEqual(st.BlastX - OutermostWalkableX(st), 8000, $"stage {stage}");
            }
        }

        /// <summary>Largest |X| of any walkable surface edge (MAIN, floor, platforms over their whole travel).</summary>
        public static int OutermostWalkableX(ChibiStageDef st)
        {
            int m = st.HalfWidth;
            if (st.HasFloor) m = Math.Max(m, st.FloorHalfW);
            foreach (var p in st.Platforms)
            {
                int cx = p.Axis == ChibiPlatform.AxisX ? Math.Max(Math.Abs(p.Min), Math.Abs(p.Max)) : Math.Abs(p.Cx);
                m = Math.Max(m, cx + p.HalfW);
            }
            return m;
        }

        // ================================================================== moving platforms (README 3.2 / F1, AC 5)

        [Test]
        public void MovingPlatforms_FollowF1()
        {
            var plats = ChibiStageTable.Get(ChibiStageTable.Mountain).Platforms;
            var a = plats[0]; var d = plats[3];
            Assert.AreEqual(2208, a.TopAt(0), "A at t 0: middle, rising (Ph 90)");
            Assert.AreEqual(3208, a.TopAt(90), "A at t 90: top");
            Assert.AreEqual(1208, a.TopAt(270), "A at t 270: bottom");
            Assert.AreEqual(-7349, d.CxAt(0), "D at t 0: art home (left end)");
            Assert.AreEqual(7349, d.CxAt(450), "D at t 450: right end");
            Assert.AreEqual(0, d.CxAt(225), "D at t 225: centre (README worked example shape)");
            Assert.AreEqual(d.CxAt(0), d.CxAt(900), "period 900");
            int peak = 0;
            for (int t = 1; t <= 900; t++) peak = Math.Max(peak, Math.Abs(d.CxAt(t) - d.CxAt(t - 1)));
            Assert.LessOrEqual(peak, 1 + 3 * 14698 / 900, "peak speed = 1.5 x travel / H");
        }

        [TestCase(0)]   // A (vertical)
        [TestCase(3)]   // D (horizontal)
        public void Rider_IsCarried_NoDrift_OverAFullPeriod(int p)
        {
            var plat = ChibiStageTable.Get(ChibiStageTable.Mountain).Platforms[p];
            var s = New(ChibiStageTable.Mountain, hazardsOff: true);
            for (int t = 0; t < 5; t++) Step(s);
            int clock = s.GameTicks - 1;
            Put(s.F[0], plat.CxAt(clock) + 300, plat.TopAt(clock));
            s.F[0].Grounded = true;
            Put(s.F[1], -14000, 12000);
            for (int t = 0; t < plat.Period; t++)
            {
                Step(s);
                int c = ChibiSimulation.PlatformClock(s);
                Assert.AreEqual(plat.TopAt(c), s.F[0].Y, $"rider on the top (tick {t})");
                Assert.AreEqual(plat.CxAt(c) + 300, s.F[0].X, $"rider keeps its offset (tick {t})");
            }
            Assert.IsTrue(s.F[0].Grounded, "grounded on the platform");
        }

        [Test]
        public void MovingPlatform_RisingTop_CatchesAFallingFighter()
        {
            var plat = ChibiStageTable.Get(ChibiStageTable.Mountain).Platforms[0];
            var s = New(ChibiStageTable.Mountain, hazardsOff: true);
            Put(s.F[1], 14000, 12000);
            Put(s.F[0], plat.Cx, 3600);
            bool landed = false;
            for (int t = 0; t < 200 && !landed; t++)
            {
                Step(s);
                landed = s.F[0].Grounded;
            }
            Assert.IsTrue(landed, "landed on A");
            Assert.AreEqual(ChibiSimulation.PlatformTop(s, 0), s.F[0].Y, "on A's current top");
        }

        // ================================================================== hazards (README 3.3, AC 6-8)

        static int ReplicaFirstDelay(uint seed, int baseT, int span) => baseT + new ChibiRng(seed ^ ChibiHazardConst.RngSalt).Range(span);

        /// <summary>Steps idle until HazPhase becomes <paramref name="phase"/>; returns the GameTicks value of the first step
        /// on which it is current (the advance runs at the end of a step, GameTicks counts after it).</summary>
        static int RunUntilPhase(ChibiSimState s, int phase, int max, Action<ChibiSimState> each = null)
        {
            for (int t = 0; t < max; t++)
            {
                each?.Invoke(s);
                Step(s);
                if (s.HazPhase == phase) return s.GameTicks;
            }
            return -1;
        }

        [Test]
        public void Volcano_FirstTelegraphAtDrawnDelay_FirstDamage75Later_10PercentStraightUp()
        {
            var s = New(ChibiStageTable.Volcano);
            int delay = ReplicaFirstDelay(Seed, ChibiHazardConst.FireFirstBase, ChibiHazardConst.FireFirstSpan);
            Assert.AreEqual(delay, s.HazTimer, "first delay from the hazard stream");
            int tele = RunUntilPhase(s, ChibiHazardConst.Telegraph, 2000);
            Assert.AreEqual(delay, tele, "Telegraph is current from the step with GameTicks = delay (Idle ran steps 0..delay-1)");
            int lane = s.HazArg == 1 ? ChibiHazardConst.FireLaneRightX : ChibiHazardConst.FireLaneLeftX;
            int hitTick = -1;
            for (int t = 0; t < 200 && hitTick < 0; t++)
            {
                Put(s.F[1], lane, -3000);                  // pinned in the lane below the floor line (03-volcano AC 3)
                Put(s.F[0], -2000, 0); s.F[0].Grounded = true;
                Step(s);
                if (s.F[1].PctM > 0) hitTick = s.GameTicks - 1;   // GameTicks of the hit step
            }
            Assert.AreEqual(tele + ChibiHazardConst.FireTelegraph, hitTick, "first damage step = Telegraph start + 75");
            Assert.AreEqual(ChibiHazardConst.FirePctM, s.F[1].PctM, "10 %");
            Assert.AreEqual(0, s.F[1].Vx, "straight up");
            Assert.Greater(s.F[1].Vy, 0, "launched up");
            int pct = s.F[1].PctM;
            for (int t = 0; t < 20; t++) { Put(s.F[1], lane, -3000); Step(s); }
            Assert.AreEqual(pct, s.F[1].PctM, "once per fighter per burst");
        }

        [Test]
        public void Volcano_FightersOnMain_NeverHitByBursts()
        {
            var s = New(ChibiStageTable.Volcano);
            for (int t = 0; t < 4000; t++) Step(s);
            Assert.Greater(s.HazCount, 4, "fixture: several bursts happened");
            Assert.AreEqual(0, s.F[0].PctM); Assert.AreEqual(0, s.F[1].PctM);
        }

        [Test]
        public void BirdLair_GustAt90_BodyHitsOnPlatformOnly_DirectionAlternates()
        {
            var s = New(ChibiStageTable.BirdLair);
            var st = ChibiStageTable.Get(ChibiStageTable.BirdLair);
            int tele = RunUntilPhase(s, ChibiHazardConst.Telegraph, 3000);
            Assert.Greater(tele, 0);
            int dir = s.HazArg;
            int gust = RunUntilPhase(s, ChibiHazardConst.PhaseBirdSwipe, 200);
            Assert.AreEqual(tele + ChibiHazardConst.BirdTelegraph, gust, "gust / first push 90 after the Telegraph start");
            // Fighter on the L / R mesa top (head inside the body band) and one on MAIN.
            var mesa = st.Platforms[dir > 0 ? 1 : 0];
            for (int t = 0; t < ChibiHazardConst.BirdSwipe; t++)
            {
                Put(s.F[0], mesa.Cx, mesa.Top); s.F[0].Grounded = true;
                Put(s.F[1], -dir * 2000, 0); s.F[1].Grounded = true;
                Step(s);
            }
            Assert.AreEqual(ChibiHazardConst.BirdPctM, s.F[0].PctM, "body hit once on the mesa (14 %)");
            Assert.AreEqual(0, s.F[1].PctM, "standing on MAIN is below the body band");
            Assert.AreEqual(ChibiHazardConst.Idle, s.HazPhase, "event over after 210 steps");
            RunUntilPhase(s, ChibiHazardConst.Telegraph, 3000);
            Assert.AreEqual(-dir, s.HazArg, "the second event blows the other way");
        }

        [Test]
        public void BirdLair_Gust_GroundedNeutralDriftsLittle_AirborneNeutralIsCarried()
        {
            var s = New(ChibiStageTable.BirdLair);
            RunUntilPhase(s, ChibiHazardConst.PhaseBirdSwipe - 0, 3000);
            int dir = s.HazArg;
            var g = new ChibiSimState(); g.CopyFrom(s);
            Put(g.F[0], 0, 0); g.F[0].Grounded = true; Put(g.F[1], -dir * 5000, 0); g.F[1].Grounded = true;
            int x0 = g.F[0].X;
            for (int t = 1; t < ChibiHazardConst.BirdSwipe; t++) Step(g);
            Assert.LessOrEqual(Math.Abs(g.F[0].X - x0), 2000, "grounded neutral drift <= 2000 (AC 2)");
            Assert.Greater((g.F[0].X - x0) * dir, 0, "drifts downwind");

            var a = new ChibiSimState(); a.CopyFrom(s);
            Put(a.F[0], 0, 5000); Put(a.F[1], -dir * 5000, 0); a.F[1].Grounded = true;
            int ax = a.F[0].X;
            for (int t = 1; t < ChibiHazardConst.BirdSwipe; t++) { a.F[0].Y = 5000; a.F[0].Vy = 0; Step(a); }
            Assert.GreaterOrEqual((a.F[0].X - ax) * dir, 6000 - 1, "airborne neutral carried >= 6000 (AC 3)");
        }

        [Test]
        public void Ocean_FirstContactOnMain_AtLeast120AfterTelegraph_NotStuckInsideTheWhale()
        {
            var s = New(ChibiStageTable.Ocean);
            int tele = RunUntilPhase(s, ChibiHazardConst.Telegraph, 4000);
            Assert.Greater(tele, 0);
            int hit = -1;
            for (int t = 0; t < 900 && s.HazPhase != ChibiHazardConst.Idle; t++)
            {
                Step(s);
                if (hit < 0 && s.F[0].PctM > 0) hit = s.GameTicks - 1;
            }
            Assert.GreaterOrEqual(hit - tele, ChibiHazardConst.WhaleTelegraph, "first contact >= 120 after the Telegraph start");
            Assert.AreEqual(ChibiHazardConst.WhalePctM, s.F[0].PctM, "12 % once");
            Assert.Less(s.F[0].Y, ChibiHazardConst.WhaleHoldY, "squeezed off MAIN, below the whale");
        }

        [Test]
        public void Ocean_RespawnDuringTheEvent_UnderMain()
        {
            var s = New(ChibiStageTable.Ocean);
            RunUntilPhase(s, ChibiHazardConst.PhaseWhaleDescend, 4000);
            s.F[1].X = ChibiStageTable.Get(ChibiStageTable.Ocean).BlastX + 1;
            for (int t = 0; t <= ChibiPercentConst.RespawnDelay; t++) Step(s);
            Assert.AreEqual(ChibiHazardConst.WhaleRespawnX, s.F[1].X);
            Assert.AreEqual(ChibiHazardConst.WhaleRespawnY, s.F[1].Y, "respawn at (0, -4300) during the whale");
        }

        [TestCase(ChibiStageTable.Volcano)]
        [TestCase(ChibiStageTable.BirdLair)]
        [TestCase(ChibiStageTable.Ocean)]
        public void HazardsOff_RngUntouched_NoHazardDamage(int stage)
        {
            // README AC 8: 10 800 steps with hazards off: HazRng unchanged, phase Idle, no hazard damage.
            var s = New(stage, hazardsOff: true);
            uint rng0 = s.HazRng.State;
            for (int t = 0; t < 10_800 && !s.MatchOver; t++) Step(s);
            Assert.AreEqual(rng0, s.HazRng.State);
            Assert.AreEqual(ChibiHazardConst.Idle, s.HazPhase);
            Assert.AreEqual(0, s.F[0].PctM); Assert.AreEqual(0, s.F[1].PctM);
        }

        [Test]
        public void HazardStream_IsSeparate_CombatRngIdenticalWithHazardsOnOrOff()
        {
            // ST-5: toggling hazards never shifts crits. Fighters jab each other on MAIN (out of every hazard's reach).
            var on = New(ChibiStageTable.Volcano);
            var off = New(ChibiStageTable.Volcano, hazardsOff: true);
            for (int t = 0; t < 3000; t++)
            {
                var a = ChibiInputPacket.Make(ChibiDir.Neutral, t % 20 == 0 ? ChibiButtons.Light : ChibiButtons.None);
                Step(on, a, a); Step(off, a, a);
                Assert.AreEqual(off.Rng.State, on.Rng.State, $"combat RNG diverged at step {t}");
            }
            Assert.AreNotEqual(off.HazRng.State, on.HazRng.State, "the hazard stream did advance with hazards on");
        }

        [Test]
        public void HazardsOff_IsInTheConfigHash()
        {
            var a = new ChibiMatchConfig { Seed = 1, Stage = ChibiStageTable.Volcano };
            var b = a; b.HazardsOff = true;
            Assert.AreNotEqual(a.ComputeHash(), b.ComputeHash());
        }

        // ================================================================== determinism / rollback / fuzz

        static ChibiInputPacket Bot(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)(n / 3) * 2654435761u + (uint)seed * 40503u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                return new ChibiInputPacket { Raw = (ushort)(h & 0x9FFF) };
            }
        }

        [TestCase(ChibiStageTable.Mountain)]
        [TestCase(ChibiStageTable.Plain)]
        [TestCase(ChibiStageTable.Volcano)]
        [TestCase(ChibiStageTable.BirdLair)]
        [TestCase(ChibiStageTable.Ocean)]
        public void NewStage_HazardsOn_SameInputsTwice_AndRollbackAtAnyDepth_Identical(int stage)
        {
            var straight = New(stage, training: true);
            var twice = New(stage, training: true);
            var rolled = New(stage, training: true);
            var snap = new ChibiSimState();
            for (int f = 0; f < 6000; f++)
            {
                var p0 = Bot(1, f); var p1 = Bot(2, f);
                ChibiSimulation.Step(straight, p0, p1);
                ChibiSimulation.Step(twice, p0, p1);
                if (f % 131 == 0)
                {
                    snap.CopyFrom(rolled);
                    for (int k = 0; k < 1 + f % 9; k++) ChibiSimulation.Step(rolled, Bot(9, f + k), Bot(8, f + k));
                    rolled.CopyFrom(snap);
                }
                ChibiSimulation.Step(rolled, p0, p1);
                if (straight.ComputeChecksum() != twice.ComputeChecksum()) Assert.Fail($"stage {stage}: nondeterministic at {f}");
                if (straight.ComputeChecksum() != rolled.ComputeChecksum()) Assert.Fail($"stage {stage}: rollback diverged at {f}");
            }
            Assert.Greater(straight.HazCount + (ChibiStageTable.Get(stage).Hazard == 0 ? 1 : 0), 0, "events ran (hazard stages)");
        }

        [Test]
        public void EveryInputValue_Volcano_HazardsOn_TwoSims_NoExceptionEqualChecksums()
        {
            var cfg = new ChibiMatchConfig { Seed = 65536, Class0 = FighterClass.Magic, Class1 = FighterClass.Heavy,
                                             Stage = ChibiStageTable.Volcano, Training = true };
            var a = new ChibiSimState(); ChibiSimulation.Init(a, cfg);
            var b = new ChibiSimState(); ChibiSimulation.Init(b, cfg);
            for (int raw = 0; raw < 65536; raw++)
            {
                var p0 = new ChibiInputPacket { Raw = (ushort)raw };
                var p1 = new ChibiInputPacket { Raw = (ushort)((raw * 40503 + 12345) & 0xFFFF) };
                ChibiSimulation.Step(a, p0, p1);
                ChibiSimulation.Step(b, p0, p1);
                if ((raw & 4095) == 4095) Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum(), "diverged at input " + raw);
            }
            Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum());
            Assert.Greater(a.HazCount, 10, "fire bursts ran during the fuzz");
        }
    }
}
