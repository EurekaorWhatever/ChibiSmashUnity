// Story type: Integration (final verification, port step 19) -- evidence: automated EditMode test -- gate: BLOCKING
// Proves both players always see the same game: two-peer rollback sync over a hostile link for every class and stage with
// every input bit in use, replay reproduction of the confirmed state, reflection checks that every state field is copied
// and hashed (the classic hidden-rollback-state bug), resimulation from snapshots, and CPU vs CPU soak for all 25 pairings.
// Spec: docs/architecture/netcode/verification-report.md.
using System;
using System.Collections.Generic;
using System.Reflection;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Port step 19: sync, replay and state-coverage verification of the deterministic simulation.</summary>
    public class ChibiSyncVerificationTests
    {
        static readonly FighterClass[] Classes =
            { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic, FighterClass.Polearm };

        /// <summary>
        /// Deterministic "everything" bot: direction incl. Down, every button incl. Dash / Shield / Skill1-3 / Ultimate,
        /// and all three gestures held for stretches (so ult windows match, CrossArms blocks, poses fire). Pure function
        /// of (seed, sample index).
        /// </summary>
        static ChibiInputPacket RichBot(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)(n / 4) * 2654435761u + (uint)seed * 40503u + 0x9E37u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                int x = (int)(h % 3) - 1, y = (int)((h >> 3) % 5) == 0 ? -1 : 0;
                ChibiButtons b = ChibiButtons.None;
                if (h % 5 == 0) b |= ChibiButtons.Light;
                if (h % 11 == 1) b |= ChibiButtons.Jump;
                if (h % 13 == 2) b |= ChibiButtons.Heavy;
                if (h % 17 == 3) b |= ChibiButtons.Dash;
                if (h % 19 == 4) b |= ChibiButtons.Shield;
                if (h % 23 == 5) b |= ChibiButtons.Skill1;
                if (h % 29 == 6) b |= ChibiButtons.Skill2;
                if (h % 31 == 7) b |= ChibiButtons.Skill3;
                if (h % 9 == 8) b |= ChibiButtons.Ultimate;
                // Gestures change every 40 samples: held long enough to pass the 9-tick ult hold.
                uint g = ((uint)(n / 40) * 2246822519u + (uint)seed) >> 7;
                var gesture = (Gesture)(g % 4);
                // Protocol 3 / rules v18 (intended): bit 15 is the Special button now, a real gameplay input, so the
                // fuzz presses it like any other button instead of flipping the retired PoseVerified hint.
                if (h % 7 == 6) b |= ChibiButtons.Special;
                if ((h >> 9) % 4 == 0 && y == 0) y = 1;   // aim up (up tilts / smashes / specials)
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, y), b, gesture);
            }
        }

        // ================================================================== two-peer sync matrix

        static IEnumerable<TestCaseData> SyncMatrix()
        {
            for (int c = 0; c < Classes.Length; c++)
                for (int stage = 0; stage < ChibiStageTable.Count; stage++)   // rules v20: stages 3..7 with hazards on
                    yield return new TestCaseData(Classes[c], Classes[(c + 2) % Classes.Length], stage)
                        .SetName($"TwoPeers_HostileLink_{Classes[c]}_vs_{Classes[(c + 2) % Classes.Length]}_Stage{stage}_StayInSync");
        }

        [TestCaseSource(nameof(SyncMatrix))]
        public void TwoPeers_HostileLink_ConfirmedStatesIdentical_ReplayReproduces(FighterClass c0, FighterClass c1, int stage)
        {
            // Arrange: ~200 ms RTT, jitter, 5% loss; production intro; meters fill through play, gestures held by the bots.
            var cfg = new ChibiMatchConfig
            {
                Seed = (uint)(1000 + stage * 17 + (int)c0 * 5 + (int)c1), Class0 = c0, Class1 = c1, Passive0 = stage % 2,
                Passive1 = (stage + 1) % 2, Stage = stage, Intro = true,
            };
            var link = new ChibiLoopbackLink(6, 2, 5);
            var a = new ChibiRollbackSession(0, cfg, link.A, new ChibiScriptedInputSource(n => RichBot(11, n)));
            var b = new ChibiRollbackSession(1, cfg, link.B, new ChibiScriptedInputSource(n => RichBot(22, n)));
            var recA = new ChibiReplayRecorder(cfg);
            var recB = new ChibiReplayRecorder(cfg);
            a.OnFrameConfirmed += recA.Add;
            b.OnFrameConfirmed += recB.Add;
            bool desync = false, disconnect = false;
            a.OnDesync += (f, l, r) => desync = true; b.OnDesync += (f, l, r) => desync = true;
            a.OnDisconnect += _ => disconnect = true; b.OnDisconnect += _ => disconnect = true;

            // Act
            const int ticks = 2400;
            for (int t = 0; t < ticks; t++) { link.Advance(); a.Tick(); b.Tick(); }

            // Assert: no desync, no disconnect, real checksum comparisons, identical confirmed inputs.
            Assert.IsFalse(desync, "checksum desync between peers");
            Assert.IsFalse(disconnect, "peers disconnected");
            Assert.Greater(a.ChecksumsCompared, 20, "checksums were compared");
            int common = Math.Min(recA.FrameCount, recB.FrameCount);
            Assert.Greater(common, ticks / 2, "most frames confirmed");
            for (int i = 0; i < common; i++)
            {
                Assert.AreEqual(recA.P0[i], recB.P0[i], "P0 input differs at frame " + i);
                Assert.AreEqual(recA.P1[i], recB.P1[i], "P1 input differs at frame " + i);
            }

            // The confirmed state of each peer equals an offline re-simulation of the confirmed inputs (replay = truth).
            var confA = a.ConfirmedState;
            Assert.IsNotNull(confA);
            Assert.AreEqual(a.ConfirmedFrame, recA.FrameCount, "recorder holds exactly the confirmed frames");
            var replayed = ChibiReplay.Simulate(cfg, recA.P0, recA.P1);
            Assert.AreEqual(replayed.ComputeChecksum(), confA.ComputeChecksum(), "replay of confirmed inputs != confirmed state");
            Assert.AreEqual(0, replayed.DroppedShots, "projectile pool overflow");
            Assert.AreEqual(0, replayed.DroppedSummons, "summon pool overflow");
        }

        [TestCase(FighterClass.Melee, FighterClass.Ranged)]
        [TestCase(FighterClass.Heavy, FighterClass.Magic)]
        [TestCase(FighterClass.Ranged, FighterClass.Polearm)]
        [TestCase(FighterClass.Magic, FighterClass.Melee)]
        [TestCase(FighterClass.Polearm, FighterClass.Heavy)]
        public void TwoPeers_UltimatesUnderRollback_StayInSync(FighterClass c0, FighterClass c1)
        {
            // Arrange: training (no KO end), both meters full on BOTH peers before the first tick (the first snapshot is
            // taken at the first Advance, so this is the frame-0 state on both sides). Ult windows, gesture holds, the
            // cinematic slow-mo and hitstop all run through mispredictions and rollbacks.
            var cfg = new ChibiMatchConfig
            {
                Seed = (uint)(4000 + (int)c0 * 5 + (int)c1), Class0 = c0, Class1 = c1, Stage = (int)c0 % 3, Training = true,
            };
            var link = new ChibiLoopbackLink(6, 2, 5);
            var a = new ChibiRollbackSession(0, cfg, link.A, new ChibiScriptedInputSource(n => RichBot(31, n)));
            var b = new ChibiRollbackSession(1, cfg, link.B, new ChibiScriptedInputSource(n => RichBot(32, n)));
            foreach (var st in new[] { a.State, b.State })
                for (int i = 0; i < 2; i++) st.F[i].Ult = ChibiSimConst.UltMax;
            var start = new ChibiSimState(); start.CopyFrom(a.State);
            var recA = new ChibiReplayRecorder(cfg);
            a.OnFrameConfirmed += recA.Add;
            bool desync = false;
            a.OnDesync += (f, l, r) => desync = true; b.OnDesync += (f, l, r) => desync = true;

            // Act
            for (int t = 0; t < 1800; t++) { link.Advance(); a.Tick(); b.Tick(); }

            // Assert
            Assert.IsFalse(desync, "desync during ultimates");
            var conf = a.ConfirmedState;
            Assert.GreaterOrEqual(conf.F[0].UltsUsed + conf.F[1].UltsUsed, 1, "precondition: an ult fired under rollback");
            var re = new ChibiSimState(); re.CopyFrom(start);
            for (int i = 0; i < recA.FrameCount; i++)
                ChibiSimulation.Step(re, new ChibiInputPacket { Raw = recA.P0[i] }, new ChibiInputPacket { Raw = recA.P1[i] });
            Assert.AreEqual(re.ComputeChecksum(), conf.ComputeChecksum(), "re-simulation of the confirmed inputs != confirmed state");
        }

        // ================================================================== snapshot / restore resimulation

        [TestCase(1)] [TestCase(3)] [TestCase(8)] [TestCase(15)]
        public void SnapshotRestoreResimulate_AnyDepth_IdenticalToStraightRun(int depth)
        {
            foreach (var c in Classes)
            {
                var cfg = new ChibiMatchConfig { Seed = (uint)(50 + (int)c), Class0 = c, Class1 = Classes[((int)c + 1) % 5], Stage = (int)c % 3 };
                var straight = new ChibiSimState(); ChibiSimulation.Init(straight, cfg);
                var rolled = new ChibiSimState(); ChibiSimulation.Init(rolled, cfg);
                for (int i = 0; i < 2; i++) straight.F[i].Ult = rolled.F[i].Ult = ChibiSimConst.UltMax;   // ults in the drill
                var snap = new ChibiSimState();
                for (int f = 0; f < 1500; f++)
                {
                    var p0 = RichBot(3, f); var p1 = RichBot(4, f);
                    ChibiSimulation.Step(straight, p0, p1);
                    if (f % 97 == 0)
                    {
                        // Rollback drill: snapshot, run `depth` frames with WRONG inputs, restore, re-run with the right ones.
                        snap.CopyFrom(rolled);
                        for (int k = 0; k < depth; k++) ChibiSimulation.Step(rolled, RichBot(99, f + k), RichBot(98, f + k));
                        rolled.CopyFrom(snap);
                    }
                    ChibiSimulation.Step(rolled, p0, p1);
                    if (straight.ComputeChecksum() != rolled.ComputeChecksum())
                        Assert.Fail($"{c}: resimulated state diverged at frame {f} (rollback depth {depth}) - a field is not restored by CopyFrom");
                }
            }
        }

        // ================================================================== field coverage by reflection

        static readonly Random Fuzz = new Random(1234);

        static void Randomize(object o)
        {
            foreach (var fi in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (fi.IsInitOnly && fi.FieldType == typeof(int[]))
                {
                    var arr = (int[])fi.GetValue(o);
                    for (int i = 0; i < arr.Length; i++) arr[i] = Fuzz.Next(1, 1 << 20);
                    continue;
                }
                if (fi.IsInitOnly || fi.IsLiteral) continue;
                if (fi.FieldType == typeof(int)) fi.SetValue(o, Fuzz.Next(1, 1 << 20));
                else if (fi.FieldType == typeof(bool)) fi.SetValue(o, true);
                else if (fi.FieldType.IsEnum) fi.SetValue(o, Enum.ToObject(fi.FieldType, 1 + Fuzz.Next(0, 3)));
                else if (fi.FieldType == typeof(ChibiRng)) fi.SetValue(o, new ChibiRng((uint)Fuzz.Next(1, int.MaxValue)));
            }
        }

        static void AssertFieldsEqual(object expected, object actual, string where)
        {
            foreach (var fi in expected.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (fi.IsLiteral) continue;
                var e = fi.GetValue(expected);
                var a = fi.GetValue(actual);
                if (fi.FieldType == typeof(int[]))
                    CollectionAssert.AreEqual((int[])e, (int[])a, where + "." + fi.Name + " not copied");
                else if (fi.FieldType.IsArray || fi.FieldType.IsClass) continue;   // pools / fighters checked separately
                else Assert.AreEqual(e, a, where + "." + fi.Name + " not copied by CopyFrom");
            }
        }

        [Test]
        public void CopyFrom_CopiesEveryField_OfEveryStateClass()
        {
            var f = new ChibiFighterSim(); Randomize(f);
            var f2 = new ChibiFighterSim(); f2.CopyFrom(f);
            AssertFieldsEqual(f, f2, "ChibiFighterSim");

            var p = new ChibiProjectileSim(); Randomize(p);
            var p2 = new ChibiProjectileSim(); p2.CopyFrom(p);
            AssertFieldsEqual(p, p2, "ChibiProjectileSim");

            var m = new ChibiMinionSim(); Randomize(m);
            var m2 = new ChibiMinionSim(); m2.CopyFrom(m);
            AssertFieldsEqual(m, m2, "ChibiMinionSim");

            var c = new ChibiCloudSim(); Randomize(c);
            var c2 = new ChibiCloudSim(); c2.CopyFrom(c);
            AssertFieldsEqual(c, c2, "ChibiCloudSim");

            var s = new ChibiSimState(); Randomize(s);
            var s2 = new ChibiSimState(); s2.CopyFrom(s);
            AssertFieldsEqual(s, s2, "ChibiSimState");
        }

        /// <summary>Fields deliberately outside the fighter hash: Stats is derived from Class + Passive at Init.</summary>
        static readonly HashSet<string> FighterHashExempt = new HashSet<string> { "Stats" };

        [Test]
        public void Checksum_ChangesWhenAnyFighterFieldChanges()
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = 9, Class0 = FighterClass.Magic, Class1 = FighterClass.Heavy });
            uint baseSum = s.ComputeChecksum();
            var missing = new List<string>();
            foreach (var fi in typeof(ChibiFighterSim).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (fi.IsLiteral || FighterHashExempt.Contains(fi.Name)) continue;
                var probe = new ChibiSimState(); probe.CopyFrom(s);
                var f = probe.F[1];
                if (fi.FieldType == typeof(int)) fi.SetValue(f, (int)fi.GetValue(f) + 7);
                else if (fi.FieldType == typeof(bool)) fi.SetValue(f, !(bool)fi.GetValue(f));
                else if (fi.FieldType == typeof(int[])) ((int[])fi.GetValue(f))[0] += 7;
                else continue;
                if (probe.ComputeChecksum() == baseSum) missing.Add(fi.Name);
            }
            CollectionAssert.IsEmpty(missing, "fighter fields not in the checksum (a desync there would go unseen)");
        }

        [Test]
        public void Checksum_ChangesWhenAnyMatchFieldOrLivePoolFieldChanges()
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = 9, Class0 = FighterClass.Magic, Class1 = FighterClass.Heavy });
            s.Proj[3].Active = true; s.Minions[2].Active = true; s.Clouds[1].Active = true;
            uint baseSum = s.ComputeChecksum();
            var missing = new List<string>();
            void Probe(object target, Func<ChibiSimState, object> pick, string label)
            {
                foreach (var fi in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (fi.IsLiteral || fi.IsInitOnly) continue;
                    var probe = new ChibiSimState(); probe.CopyFrom(s);
                    var o = pick(probe);
                    if (fi.FieldType == typeof(int)) fi.SetValue(o, (int)fi.GetValue(o) + 7);
                    else if (fi.FieldType == typeof(bool)) fi.SetValue(o, !(bool)fi.GetValue(o));
                    else if (fi.FieldType.IsEnum) fi.SetValue(o, Enum.ToObject(fi.FieldType, Convert.ToInt32(fi.GetValue(o)) + 1));
                    else if (fi.FieldType == typeof(ChibiRng)) fi.SetValue(o, new ChibiRng(((ChibiRng)fi.GetValue(o)).State + 7));
                    else continue;
                    if (probe.ComputeChecksum() == baseSum) missing.Add(label + "." + fi.Name);
                }
            }
            Probe(s, st => st, "ChibiSimState");
            Probe(s.Proj[3], st => st.Proj[3], "Proj");
            Probe(s.Minions[2], st => st.Minions[2], "Minion");
            Probe(s.Clouds[1], st => st.Clouds[1], "Cloud");
            CollectionAssert.IsEmpty(missing, "state fields not in the checksum");
        }

        // ================================================================== CPU vs CPU soak, every pairing

        static IEnumerable<TestCaseData> AllPairings()
        {
            foreach (var a in Classes)
                foreach (var b in Classes)
                    yield return new TestCaseData(a, b).SetName($"CpuVsCpu_{a}_vs_{b}_FullMatchEndsCleanly");
        }

        [TestCaseSource(nameof(AllPairings))]
        public void CpuVsCpu_FullMatch_EndsCleanly_NoPoolOverflow_ReplayMatches(FighterClass a, FighterClass b)
        {
            var cfg = new ChibiMatchConfig
            {
                Seed = (uint)(7000 + (int)a * 10 + (int)b), Class0 = a, Class1 = b, Passive0 = (int)a % 2, Passive1 = (int)b % 2,
                Stage = ((int)a + (int)b) % 3, Intro = true,
            };
            var rec = new ChibiReplayRecorder(cfg);
            var session = new ChibiLocalSession(cfg, new ChibiCpuInputSource(0, 3, cfg.Seed), new ChibiCpuInputSource(1, 3, cfg.Seed));
            session.OnFrame += rec.Add;
            int guard = 0;
            while (session.Tick() && guard++ < ChibiSimConst.MatchTicks * 4) { }
            var s = session.State;
            Assert.IsTrue(s.MatchOver, "the match ends by KO or time-out");
            Assert.AreEqual(0, s.DroppedShots, "projectile pool overflow");
            Assert.AreEqual(0, s.DroppedSummons, "summon pool overflow");
            for (int i = 0; i < 2; i++)
            {
                Assert.That(s.F[i].PctM >= 0 && s.F[i].PctM <= ChibiPercentConst.PctCapM, "percent in range (rules v20)");
                Assert.That(s.F[i].Stocks >= 0 && s.F[i].Stocks <= ChibiPercentConst.StocksStart, "stocks in range (rules v20)");
                Assert.That(s.F[i].Ult >= 0 && s.F[i].Ult <= ChibiSimConst.UltMax);
                Assert.IsFalse(s.F[i].Held && !s.F[1 - i].Holding, "held invariant");
            }
            Assert.Greater(s.F[0].HitsLanded + s.F[1].HitsLanded, 0, "the CPUs actually fought");
            Assert.AreEqual(s.ComputeChecksum(), ChibiReplay.Simulate(cfg, rec.P0, rec.P1).ComputeChecksum(), "replay reproduces the match");
        }
    }
}
