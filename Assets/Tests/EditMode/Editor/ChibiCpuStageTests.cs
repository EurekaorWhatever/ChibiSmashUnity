// Story type: Logic (CPU plays the rules v20 loop: percent kill reads, stage and hazard awareness, respawn) -- evidence:
// automated EditMode unit test -- gate: BLOCKING
// Source specs: design/gdd/percent-knockback.md (3.4 respawn / hover, 3.5 CPU kill rule, 4.2 knockback, 4.8 KO calibration),
// design/gdd/stages/README.md + 01..05 stage files (KO edges, hazards), docs/architecture/netcode/cpu-ai-conversion.md.
using System;
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiCpuInputSource on the five design stages: determinism, fire lanes, whale, walk-off, kill reads, respawn.</summary>
    public class ChibiCpuStageTests
    {
        static readonly FighterClass[] Classes =
            { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic, FighterClass.Polearm };

        static ChibiMatchConfig Cfg(FighterClass c0, FighterClass c1, uint seed, int stage, bool hazardsOff = false, bool training = false) =>
            new ChibiMatchConfig { Seed = seed, Class0 = c0, Class1 = c1, Stage = stage, HazardsOff = hazardsOff, Training = training };

        /// <summary>P0 = idle human (or a CPU), P1 = the CPU under test; records the CPU's packets.</summary>
        sealed class Rig
        {
            public readonly ChibiLocalSession Session;
            public readonly List<ChibiInputPacket> Sent = new List<ChibiInputPacket>();
            public readonly List<ChibiInputPacket> Sent0 = new List<ChibiInputPacket>();

            public Rig(ChibiMatchConfig cfg, int difficulty, IChibiInputSource p0 = null)
            {
                Session = new ChibiLocalSession(cfg, p0 ?? new ChibiScriptedInputSource(_ => default),
                                                new ChibiCpuInputSource(1, difficulty, cfg.Seed));
                Session.OnFrame += (f, a, b) => { Sent0.Add(a); Sent.Add(b); };
            }

            public ChibiSimState S => Session.State;
            public ChibiFighterSim Cpu => S.F[1];
            public bool Tick() => Session.Tick();
            public void Run(int ticks) { for (int t = 0; t < ticks && Session.Tick(); t++) { } }

            /// <summary>Ticks until <paramref name="until"/> holds (true) or <paramref name="max"/> ticks / match end (false).</summary>
            public bool RunUntil(Func<ChibiSimState, bool> until, int max)
            {
                for (int t = 0; t < max; t++)
                {
                    if (until(S)) return true;
                    if (!Session.Tick()) return false;
                }
                return until(S);
            }
        }

        static bool HurtOverlaps(ChibiFighterSim f, int x0, int x1, int y0, int y1) =>
            x0 < f.X + ChibiSimConst.HurtHalfW && x1 > f.X - ChibiSimConst.HurtHalfW && y0 < f.Y + ChibiSimConst.HurtH && y1 > f.Y;

        /// <summary>Puts a fighter at rest, free to act (no move, stun, dash, invulnerability).</summary>
        static void Free(ChibiFighterSim f, int x, int y, bool grounded, int facing)
        {
            f.X = x; f.Y = y; f.XRem = 0; f.YRem = 0; f.Vx = 0; f.Vy = 0; f.Grounded = grounded; f.Facing = facing;
            f.Stun = 0; f.MoveSlot = -1; f.MoveLockEnd = 0; f.DashTicks = 0; f.Invuln = 0; f.AirJumps = 1; f.AirUsedMask = 0;
            f.ClearLaunch();
        }

        static int[] SmashSlots(FighterClass c, params int[] dirs)
        {
            var l = new List<int>();
            foreach (int d in dirs)
            {
                int s = ChibiMovesetData.SlotFor((int)c, 1, d, false);
                if (s >= 0) l.Add(s);
            }
            return l.ToArray();
        }

        // ================================================================== determinism per stage

        [TestCase(ChibiStageTable.Mountain)] [TestCase(ChibiStageTable.Plain)] [TestCase(ChibiStageTable.Volcano)]
        [TestCase(ChibiStageTable.BirdLair)] [TestCase(ChibiStageTable.Ocean)]
        public void CpuVsCpu_SameSeed_SameStreamAndState_PerStage(int stage)
        {
            const int ticks = 3000;
            var c0 = Classes[stage % Classes.Length]; var c1 = Classes[(stage + 2) % Classes.Length];
            var cfg = Cfg(c0, c1, 8800u + (uint)stage, stage);
            var a = new Rig(cfg, 3, new ChibiCpuInputSource(0, 3, cfg.Seed));
            var b = new Rig(cfg, 3, new ChibiCpuInputSource(0, 3, cfg.Seed));
            for (int t = 0; t < ticks; t++) { a.Tick(); b.Tick(); }
            CollectionAssert.AreEqual(a.Sent0, b.Sent0, $"stage {stage}: P0 CPU stream");
            CollectionAssert.AreEqual(a.Sent, b.Sent, $"stage {stage}: P1 CPU stream");
            Assert.AreEqual(a.S.ComputeChecksum(), b.S.ComputeChecksum(), "same inputs, same final state");
            if (ChibiStageTable.Get(stage).Hazard != ChibiStageTable.HazardNone)
                Assert.Greater(a.S.HazCount, 0, "precondition: a stage event ran during the sample");
        }

        // ================================================================== Volcano fire lanes

        [Test]
        public void Volcano_CpuNeverInsideAnEruptingFireLane()
        {
            const int events = 9;
            // Training: a KO (the late variant may not make it home) respawns without ending the match.
            var cfg = Cfg(FighterClass.Melee, FighterClass.Melee, 4242u, ChibiStageTable.Volcano, training: true);
            var r = new Rig(cfg, 3);
            int checkedTicks = 0;
            for (int k = 0; k < events; k++)
            {
                Assert.IsTrue(r.RunUntil(s => s.HazPhase == ChibiHazardConst.Telegraph, 2000), $"event {k}: a telegraph starts");
                int lane = r.S.HazArg == 2 ? k % 2 : r.S.HazArg;
                int lx = lane == 0 ? ChibiHazardConst.FireLaneLeftX : ChibiHazardConst.FireLaneRightX;
                int outward = lx > 0 ? 1 : -1;
                // Variant 0: in the middle of the lane (recovering). 1: beyond it at the telegraph start, time to cross.
                // 2: beyond it with 2 telegraph ticks left, too late to cross (even with an air dash): the way home runs
                // through the eruption, so the CPU has to wait outside.
                int variant = k % 3;
                if (variant == 2)
                    Assert.IsTrue(r.RunUntil(s => s.HazPhase == ChibiHazardConst.Telegraph && s.HazTimer <= 2, 200), "late in the telegraph");
                if (variant == 0) Free(r.Cpu, lx, 1500, false, -outward);
                else if (variant == 1) Free(r.Cpu, lx + outward * 2600, 3000, false, -outward);
                else Free(r.Cpu, lx + outward * 2000, 4000, false, -outward);
                r.S.HitstopRealTicks = 0;

                bool erupted = false;
                for (int t = 0; t < 400; t++)
                {
                    if (!r.Tick()) break;
                    var s = r.S;
                    if (s.HazPhase == ChibiHazardConst.PhaseFireErupt)
                    {
                        erupted = true;
                        int top = ChibiSimulation.FireColumnTop(s);
                        var f = r.Cpu;
                        if (!f.InPlay) continue;
                        for (int ln = 0; ln < 2; ln++)
                        {
                            if (!ChibiSimulation.FireLaneOn(s, ln)) continue;
                            int x = ln == 0 ? ChibiHazardConst.FireLaneLeftX : ChibiHazardConst.FireLaneRightX;
                            Assert.IsFalse(HurtOverlaps(f, x - ChibiHazardConst.FireLaneHalfW, x + ChibiHazardConst.FireLaneHalfW,
                                                        ChibiHazardConst.FireBottom, top),
                                $"event {k} (variant {variant}, lane {lane}): CPU at ({f.X}, {f.Y}) inside erupting lane {ln}");
                        }
                        checkedTicks++;
                    }
                    else if (erupted) break;
                }
                Assert.IsTrue(erupted, $"event {k}: the column erupted");
                Assert.AreEqual(0, r.S.HazHitMask & 2, $"event {k}: the CPU was never burned");
            }
            Assert.Greater(checkedTicks, events * 30, "precondition: eruption ticks were checked");
        }

        // ================================================================== Ocean whale

        [TestCase(FighterClass.Melee, 11u)] [TestCase(FighterClass.Heavy, 12u)] [TestCase(FighterClass.Ranged, 13u)]
        [TestCase(FighterClass.Magic, 14u)] [TestCase(FighterClass.Polearm, 15u)]
        public void Ocean_CpuIsBelowTheWhaleDuringHold(FighterClass c, uint seed)
        {
            var r = new Rig(Cfg(FighterClass.Heavy, c, seed, ChibiStageTable.Ocean), 3);
            Assert.IsTrue(r.RunUntil(s => s.HazPhase == ChibiHazardConst.PhaseWhaleHold, 4000), "the whale reaches its hold");
            int held = 0;
            while (r.S.HazPhase == ChibiHazardConst.PhaseWhaleHold && r.Tick())
            {
                var f = r.Cpu;
                int bottom = ChibiSimulation.WhaleBottom(r.S);
                if (f.InPlay)
                    Assert.IsFalse(HurtOverlaps(f, -ChibiHazardConst.WhaleHalfW, ChibiHazardConst.WhaleHalfW, bottom, bottom + ChibiHazardConst.WhaleH),
                        $"{c}: CPU at ({f.X}, {f.Y}) inside the holding whale");
                held++;
            }
            Assert.GreaterOrEqual(held, ChibiHazardConst.WhaleHold - 2, "precondition: the whole hold was checked");
            Assert.AreEqual(0, r.S.HazHitMask & 2, $"{c}: the CPU was never crushed this event");
        }

        // ================================================================== Bird Lair

        [Test]
        public void BirdLair_CpuIsNeverHitByTheBirdBody_TwoEvents()
        {
            var r = new Rig(Cfg(FighterClass.Melee, FighterClass.Polearm, 606u, ChibiStageTable.BirdLair), 3);
            for (int k = 0; k < 2; k++)
            {
                Assert.IsTrue(r.RunUntil(s => s.HazPhase == ChibiHazardConst.Telegraph, 3000), $"event {k}: the telegraph starts");
                // Both fighters stand on a mesa (inside the body band) when the bird screeches: the CPU must get down.
                int m = k % 2, cx = ChibiSimulation.PlatformCx(r.S, m), top = ChibiSimulation.PlatformTop(r.S, m);
                Free(r.Cpu, cx - 600, top, true, 1);
                Free(r.S.F[0], cx + 600, top, true, -1);
                r.S.HitstopRealTicks = 0;
                Assert.IsTrue(r.RunUntil(s => s.HazPhase == ChibiHazardConst.PhaseBirdSwipe, 200), $"event {k}: the swipe starts");
                Assert.IsTrue(r.RunUntil(s => s.HazPhase != ChibiHazardConst.PhaseBirdSwipe, 400), $"event {k}: the swipe ends");
                Assert.AreEqual(0, r.S.HazHitMask & 2, $"event {k}: the CPU stayed out of the body band");
            }
            Assert.AreEqual(0, r.Cpu.KosTaken, "and was never blown off the stage");
        }

        // ================================================================== Plain: stay on the ground

        [TestCaseSource(nameof(Classes))]
        public void Plain_CpuDoesNotFallOff_60sVsDummy(FighterClass c)
        {
            // Rules v21: Plain's ground ends at its ledges (ChibiStageDef.HalfWidth) with KO lines beyond and below; the CPU
            // knocks the dummy around and off without ever leaving the ground itself (no KO of any kind).
            var r = new Rig(Cfg(FighterClass.Melee, c, 2024u + (uint)c, ChibiStageTable.Plain), 3, new ChibiCpuInputSource(0, 0, 2024u));
            int maxX = 0;
            for (int t = 0; t < 60 * ChibiSimConst.TickRate && r.Tick(); t++)
                if (r.Cpu.InPlay) maxX = Math.Max(maxX, Math.Abs(r.Cpu.X));
            Assert.AreEqual(0, r.Cpu.KosTaken, $"{c}: the CPU never fell off Plain (furthest |X| {maxX})");
            Assert.Greater(r.S.F[0].PctM + r.S.F[0].KosTaken, 0, "precondition: the CPU fought the dummy");
        }

        // ================================================================== kill reads by percent

        /// <summary>Trials in which the CPU's first move (within 150 ticks) is one of <paramref name="slots"/>.</summary>
        static int FirstMoveCount(int stage, FighterClass c, int oppX, int cpuX, int pctM, int[] slots, int trials, out int anyMove)
        {
            int hits = 0;
            anyMove = 0;
            for (int k = 0; k < trials; k++)
            {
                var r = new Rig(Cfg(c, c, 3000u + (uint)k, stage, hazardsOff: true), 3);
                r.Run(90);
                int face = oppX > cpuX ? 1 : -1;
                Free(r.S.F[0], oppX, 0, true, -face);
                Free(r.Cpu, cpuX, 0, true, face);
                r.S.F[0].PctM = pctM;
                r.S.HitstopRealTicks = 0;
                for (int t = 0; t < 150 && r.Tick(); t++)
                {
                    int slot = r.Cpu.MoveSlot;
                    if (slot < 0) continue;
                    anyMove++;
                    if (Array.IndexOf(slots, slot) >= 0) hits++;
                    break;
                }
            }
            return hits;
        }

        [Test]
        public void Smash_MoreOftenAtKillPercentNearEdge_ThanAtZeroPercentCentre()
        {
            const int trials = 30;
            var c = FighterClass.Melee;
            var smashes = SmashSlots(c, 1, 3, 4);
            var st = ChibiStageTable.Get(ChibiStageTable.SkyDojo);
            int high = FirstMoveCount(ChibiStageTable.SkyDojo, c, st.HalfWidth - 1500, st.HalfWidth - 2600, 150_000, smashes, trials, out int movesHigh);
            int low = FirstMoveCount(ChibiStageTable.SkyDojo, c, 550, -550, 0, smashes, trials, out int movesLow);
            // Same spot by the ledge at 0 %: the edge alone is no kill read any more (the v19 rule smashed there).
            int edgeLow = FirstMoveCount(ChibiStageTable.SkyDojo, c, st.HalfWidth - 1500, st.HalfWidth - 2600, 0, smashes, trials, out int movesEdgeLow);
            Assert.Greater(movesHigh, trials / 2, "precondition: the CPU attacks near the edge");
            Assert.Greater(movesLow, trials / 2, "precondition: the CPU attacks at the centre");
            Assert.Greater(movesEdgeLow, trials / 2, "precondition: the CPU attacks near the edge at 0 %");
            Assert.GreaterOrEqual(high * 2, movesHigh, $"at 150 % by the ledge most first moves are smashes ({high}/{movesHigh})");
            Assert.Greater(high, low * 3, $"smashes: {high}/{movesHigh} at kill percent by the edge vs {low}/{movesLow} at 0 % centre");
            Assert.Greater(high, edgeLow * 2, $"smashes: {high}/{movesHigh} at 150 % vs {edgeLow}/{movesEdgeLow} at 0 %, same spot by the edge");
        }

        [Test]
        public void Mountain_KillPercent_PrefersUpSmashOverForwardSmash()
        {
            // Mountain's sides are walls (no side KO): only a vertical launch can finish (01-mountain.md).
            const int trials = 30;
            var c = FighterClass.Melee;
            int up = FirstMoveCount(ChibiStageTable.Mountain, c, 1100, 0, 170_000, SmashSlots(c, 3), trials, out int moves);
            int fwd = FirstMoveCount(ChibiStageTable.Mountain, c, 1100, 0, 170_000, SmashSlots(c, 1), trials, out _);
            Assert.Greater(moves, trials / 2, "precondition: the CPU attacks");
            Assert.Greater(up, fwd * 2, $"up smash {up} vs forward smash {fwd} of {moves} first moves at 170 % on Mountain");
        }

        // ================================================================== respawn

        [Test]
        public void Respawn_NoInputWhileOut_ThenLeavesHoverOnceSafe()
        {
            var r = new Rig(Cfg(FighterClass.Melee, FighterClass.Melee, 5150u, ChibiStageTable.Plain), 3);
            r.Run(90);
            var st = ChibiStageTable.Get(ChibiStageTable.Plain);
            Free(r.Cpu, st.BlastX + 500, 0, false, 1);                  // past the walk-off line: KO this step
            r.S.HitstopRealTicks = 0;
            r.Run(1);
            Assert.Greater(r.Cpu.OutTicks, 0, "precondition: the CPU is KO'd");
            int sent = r.Sent.Count;
            Assert.IsTrue(r.RunUntil(s => s.F[1].HoverTicks > 0, 200), "it respawns onto the hover point");
            for (int i = sent; i < r.Sent.Count; i++) Assert.AreEqual(0, r.Sent[i].Raw, "no input while KO'd");

            int hoverStart = r.Sent.Count, hovered = 0;
            while (r.Cpu.HoverTicks > 0 && r.Tick()) hovered++;
            int nonZero = 0;
            for (int i = hoverStart; i < r.Sent.Count; i++) if (r.Sent[i].Raw != 0) nonZero++;
            Assert.AreEqual(1, nonZero, "exactly one stick tick ends the hover");
            Assert.AreEqual(0, r.Sent[r.Sent.Count - 1].Raw & ~ChibiInputPacket.DirMask, "it is a direction, no button or gesture");
            Assert.Less(hovered, ChibiPercentConst.RespawnHoverMax - 1, "the CPU leaves on its own, the dummy is far away");
        }
    }
}
