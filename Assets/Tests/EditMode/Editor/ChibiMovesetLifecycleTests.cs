// Story type: Integration (moveset rows end to end, rules v18) -- evidence: automated EditMode test -- gate: BLOCKING
// Source spec: design/gdd/movesets/README.md sections 1, 5, 7; docs/architecture/netcode/DEVELOPER-GUIDE.md sections 3-4.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using ChibiClash.Netcode;
using NUnit.Framework;
using static ChibiClash.Tests.MovesetKit;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Every moveset row starts and ends cleanly for all five classes; determinism, snapshot / rollback and two-peer sync
    /// with a bot that presses the new inputs; the Special bit on the wire; zone pool coverage; data acceptance; perf.
    /// </summary>
    public class ChibiMovesetLifecycleTests
    {
        // ================================================================== every row starts and ends

        static string InputOf(ChibiMoveExt e)
        {
            string b = e.Button == 0 ? "L" : e.Button == 1 ? "H" : e.Button == 2 ? "B" : "S" + (e.Button - 2);
            string d = e.Dir == 1 ? "+F" : e.Dir == 2 ? "+BK" : e.Dir == 3 ? "+U" : e.Dir == 4 ? "+D" : "";
            return b + d + (e.Dash ? "@dash" : "");
        }

        static IEnumerable<TestCaseData> AllRows()
        {
            foreach (var c in Classes)
                for (int k = 0; k < ChibiMoveInfo.Count((int)c); k++)
                {
                    int slot = ChibiMovesetData.FirstNewSlot + k;
                    var e = ChibiMoveTable.Get((int)c, slot).Ext;
                    if (e.Has(ChibiMoveExt.FChargeLevel) && e.ChargeGroupBase != slot) continue;   // reached by charging
                    string id = ChibiMoveInfo.Get((int)c, slot).Id;
                    yield return new TestCaseData(c, id).SetName($"EveryRow_{c}_{id}_StartsAndEndsCleanly");
                }
        }

        [TestCaseSource(nameof(AllRows))]
        public void EveryRow_StartsAndEndsCleanly(FighterClass c, string id)
        {
            // Arrange
            var s = Arena(c, FighterClass.Heavy, -3000, 7000);
            var row = Row(c, id);
            var e = row.Ext;
            bool air = (e.AirMask & 1) == 0;

            // Act
            StartRow(s, c, id, InputOf(e), air);
            int bound = row.Startup + row.Active + row.Recovery + ChibiMovesetConst.CounterLock + 2;
            int steps = 0;
            while (s.F[0].MoveSlot >= 0 && steps < bound) { Game(s, N, N); steps++; }
            Idle(s, 120);                                    // let shots, zones and pulls run out

            // Assert
            Assert.AreEqual(-1, s.F[0].MoveSlot, $"{id} still running after {bound} steps");
            Assert.LessOrEqual(steps, row.Startup + row.Active + row.Recovery, "no row outlasts its frames without a hold / counter");
            Assert.IsFalse(s.F[0].Charging, "charge released");
            Assert.AreEqual(0, s.F[0].PullTicks, "pull over");
            Assert.AreEqual(0, s.DroppedShots, "projectile pool");
            Assert.AreEqual(0, s.DroppedSummons, "zone / summon pool");
            Assert.Greater(s.F[0].HpU, 0);
        }

        // ================================================================== bot that presses the new inputs

        /// <summary>Deterministic moveset bot: every direction incl. up, Special, skills + up / down, held Heavy / Special (charge, hold), dash, jump.</summary>
        static ChibiInputPacket MovesetBot(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)(n / 5) * 2654435761u + (uint)seed * 40503u + 0x51EDu;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                int x = (int)(h % 3) - 1;
                int ySel = (int)((h >> 3) % 4);
                int y = ySel == 0 ? 1 : ySel == 1 ? -1 : 0;
                ChibiButtons b = ChibiButtons.None;
                if ((n / 24) % 3 == seed % 3) b |= ChibiButtons.Heavy;          // held level: charges smashes
                if ((n / 30) % 4 == seed % 4) b |= ChibiButtons.Special;        // held level: charge level / hold
                if (n % 2 == 0)
                {
                    if (h % 5 == 0) b |= ChibiButtons.Light;
                    if (h % 9 == 1) b |= ChibiButtons.Special;
                    if (h % 13 == 2) b |= ChibiButtons.Skill1;
                    if (h % 14 == 3) b |= ChibiButtons.Skill2;
                    if (h % 15 == 4) b |= ChibiButtons.Skill3;
                    if (h % 11 == 5) b |= ChibiButtons.Jump;
                    if (h % 17 == 6) b |= ChibiButtons.Dash;
                    if (h % 29 == 7) b |= ChibiButtons.Shield;
                }
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, y), b);
            }
        }

        static ChibiMatchConfig Cfg(FighterClass c0, FighterClass c1, int stage, bool training = true) => new ChibiMatchConfig
        {
            Seed = (uint)(9000 + (int)c0 * 7 + (int)c1 + stage * 31), Class0 = c0, Class1 = c1, Passive0 = stage % 2,
            Passive1 = (stage + 1) % 2, Stage = stage, Training = training,
        };

        static IEnumerable<TestCaseData> Pairs()
        {
            for (int c = 0; c < Classes.Length; c++)
                yield return new TestCaseData(Classes[c], Classes[(c + 1) % Classes.Length], c % 3)
                    .SetName($"MovesetBot_{Classes[c]}_vs_{Classes[(c + 1) % Classes.Length]}");
        }

        [TestCaseSource(nameof(Pairs))]
        public void TwoSims_MovesetBot_ChecksumsEqualEverySecond(FighterClass c0, FighterClass c1, int stage)
        {
            // Arrange
            var cfg = Cfg(c0, c1, stage);
            var a = new ChibiSimState(); ChibiSimulation.Init(a, cfg);
            var b = new ChibiSimState(); ChibiSimulation.Init(b, cfg);
            int movesetStarts = 0, prevSlot = -1;

            // Act / Assert
            for (int n = 0; n < 3600; n++)
            {
                var i0 = MovesetBot(1, n); var i1 = MovesetBot(2, n);
                ChibiSimulation.Step(a, i0, i1);
                ChibiSimulation.Step(b, i0, i1);
                if (a.F[0].MoveSlot >= ChibiMovesetData.FirstNewSlot && a.F[0].MoveSlot != prevSlot) movesetStarts++;
                prevSlot = a.F[0].MoveSlot;
                if (n % 60 == 59) Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum(), "checksums differ at tick " + n);
            }
            Assert.Greater(movesetStarts, 20, "precondition: the bot used the new moves");
            Assert.AreEqual(0, a.DroppedShots);
        }

        [TestCaseSource(nameof(Pairs))]
        public void Resimulation_FromSnapshots_MatchesStraightRun(FighterClass c0, FighterClass c1, int stage)
        {
            // Arrange: straight run vs a run that rolls back 1..8 ticks every 9 ticks and re-simulates.
            var cfg = Cfg(c0, c1, stage);
            var straight = new ChibiSimState(); ChibiSimulation.Init(straight, cfg);
            var rolled = new ChibiSimState(); ChibiSimulation.Init(rolled, cfg);
            var snaps = new ChibiSimState[9];
            for (int k = 0; k < snaps.Length; k++) snaps[k] = new ChibiSimState();

            // Act / Assert
            for (int n = 0; n < 1800; n++)
            {
                snaps[n % 9].CopyFrom(rolled);
                ChibiSimulation.Step(straight, MovesetBot(5, n), MovesetBot(6, n));
                ChibiSimulation.Step(rolled, MovesetBot(5, n), MovesetBot(6, n));
                if (n % 9 == 8)
                {
                    int depth = 1 + (n / 9) % 8;
                    int from = n - depth + 1;
                    rolled.CopyFrom(snaps[from % 9]);
                    for (int f = from; f <= n; f++) ChibiSimulation.Step(rolled, MovesetBot(5, f), MovesetBot(6, f));
                    Assert.AreEqual(straight.ComputeChecksum(), rolled.ComputeChecksum(), $"resim diverged at {n} (depth {depth})");
                }
            }
        }

        [TestCaseSource(nameof(Pairs))]
        public void TwoPeers_HostileLink_MovesetBot_StayInSync(FighterClass c0, FighterClass c1, int stage)
        {
            // Arrange: ~200 ms RTT, jitter, 5 % loss; real matches (no training) with the intro.
            var cfg = Cfg(c0, c1, stage, training: false);
            cfg.Intro = true;
            var link = new ChibiLoopbackLink(6, 2, 5);
            var a = new ChibiRollbackSession(0, cfg, link.A, new ChibiScriptedInputSource(n => MovesetBot(41, n)));
            var b = new ChibiRollbackSession(1, cfg, link.B, new ChibiScriptedInputSource(n => MovesetBot(42, n)));
            var recA = new ChibiReplayRecorder(cfg);
            a.OnFrameConfirmed += recA.Add;
            bool desync = false, disconnect = false;
            a.OnDesync += (f, l, r) => desync = true; b.OnDesync += (f, l, r) => desync = true;
            a.OnDisconnect += _ => disconnect = true; b.OnDisconnect += _ => disconnect = true;

            // Act
            for (int t = 0; t < 2400; t++) { link.Advance(); a.Tick(); b.Tick(); }

            // Assert
            Assert.IsFalse(desync, "checksum desync between peers");
            Assert.IsFalse(disconnect, "peers disconnected");
            Assert.Greater(a.ChecksumsCompared, 20, "checksums were compared");
            var replayed = ChibiReplay.Simulate(cfg, recA.P0, recA.P1);
            Assert.AreEqual(replayed.ComputeChecksum(), a.ConfirmedState.ComputeChecksum(), "replay of confirmed inputs != confirmed state");
        }

        // ================================================================== Special bit on the wire (protocol 3)

        [Test]
        public void SpecialBit_SurvivesSanitizeAndTheCodecRoundTrip()
        {
            // Arrange
            var m = new ChibiNetMessage { LastFrame = 100, AckFrame = 90, Advantage = 1, Count = 3, HasChecksum = false };
            m.Inputs[0] = ChibiInputPacket.Make(ChibiDir.N, ChibiButtons.Special);
            m.Inputs[1] = ChibiInputPacket.Make(ChibiDir.N, ChibiButtons.Special | ChibiButtons.Heavy);
            m.Inputs[2] = ChibiInputPacket.Make(ChibiDir.S, ChibiButtons.Special, Gesture.RaiseHands);
            var buffer = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);

            // Act
            ChibiPacketCodec.Write(buffer, m);
            buffer.BeginRead();
            var r = new ChibiNetMessage();
            bool ok = ChibiPacketCodec.TryRead(buffer, r);

            // Assert
            Assert.IsTrue(ok);
            for (int k = 0; k < 3; k++)
            {
                Assert.AreEqual(m.Inputs[k].Raw, r.Inputs[k].Raw, "input " + k);
                Assert.IsTrue(r.Inputs[k].Has(ChibiButtons.Special));
                Assert.AreEqual(m.Inputs[k], m.Inputs[k].Sanitized(), "Special is canonical (never cleared)");
            }
            Assert.AreEqual(3, ChibiPacketCodec.ProtocolVersion, "bit 15 changed meaning: protocol 3");
        }

        // ================================================================== zone pool state coverage

        [Test]
        public void ZonePool_CopiedAndHashed_EveryField()
        {
            // Arrange
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = 3, Class0 = FighterClass.Melee, Class1 = FighterClass.Magic });
            s.Zones[1].Active = true;
            uint baseSum = s.ComputeChecksum();
            var missing = new List<string>();

            // Act / Assert: CopyFrom copies, the checksum sees every field of a live zone.
            foreach (var fi in typeof(ChibiZoneSim).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var probe = new ChibiSimState(); probe.CopyFrom(s);
                var z = probe.Zones[1];
                if (fi.FieldType == typeof(int)) fi.SetValue(z, (int)fi.GetValue(z) + 7);
                else if (fi.FieldType == typeof(bool)) fi.SetValue(z, !(bool)fi.GetValue(z));
                var copy = new ChibiSimState(); copy.CopyFrom(probe);
                Assert.AreEqual(fi.GetValue(z), fi.GetValue(copy.Zones[1]), "ChibiZoneSim." + fi.Name + " not copied");
                if (probe.ComputeChecksum() == baseSum) missing.Add(fi.Name);
            }
            CollectionAssert.IsEmpty(missing, "zone fields not in the checksum");
        }

        // ================================================================== data acceptance (README 7) + config hash

        [Test]
        public void Data_MultCappedAndNoDpsCreep()
        {
            foreach (var c in Classes)
                for (int slot = ChibiMovesetData.FirstNewSlot; slot < ChibiMoveTable.Count((int)c); slot++)
                {
                    var m = ChibiMoveTable.Get((int)c, slot);
                    string id = ChibiMoveInfo.Get((int)c, slot).Id;
                    Assert.LessOrEqual(m.Mult, 3000, id + ": mult > 3000");
                    if (m.Ext.Has(ChibiMoveExt.FCounter) || m.Ext.Has(ChibiMoveExt.FArmor)) continue;
                    int frames = m.Startup + m.Active + m.Recovery;
                    Assert.Less(m.Mult / frames, 72, id + ": mult per tick >= 72 (README 7)");
                }
        }

        [Test]
        public void ConfigHash_SeesEveryMovesetExtField()
        {
            // Arrange: mutate one int field of one row's ext at a time (restored in finally) and watch the hash.
            var cfg = new ChibiMatchConfig { Seed = 1, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy };
            uint h0 = cfg.ComputeHash();
            var e = ChibiMoveTable.Get((int)FighterClass.Ranged, Slot(FighterClass.Ranged, "ren_volley")).Ext;
            var missing = new List<string>();

            // Act / Assert
            foreach (var fi in typeof(ChibiMoveExt).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object old = fi.GetValue(e);
                try
                {
                    if (fi.FieldType == typeof(int)) fi.SetValue(e, (int)old + 1);
                    else if (fi.FieldType == typeof(bool)) fi.SetValue(e, !(bool)old);
                    else if (fi.FieldType == typeof(int[])) ((int[])old)[0] += 1;
                    if (cfg.ComputeHash() == h0) missing.Add(fi.Name);
                }
                finally
                {
                    if (fi.FieldType == typeof(int[])) ((int[])old)[0] -= 1;
                    else fi.SetValue(e, old);
                }
            }
            Assert.AreEqual(h0, cfg.ComputeHash(), "fixture restored the data");
            CollectionAssert.IsEmpty(missing, "moveset fields outside the config hash");
        }

        // ================================================================== perf guard

        /// <summary>
        /// Budget: a rollback frame re-simulates up to 8 predicted ticks plus the new one (9 Steps) inside a 16.6 ms frame
        /// next to rendering; the sim gets 1 ms of it, so one Step must average under 100 microseconds (measured on the
        /// moveset bot, after a warm-up). Generous on purpose: the editor test runner is slower than a player build.
        /// </summary>
        /// <remarks>
        /// Wall-clock based, so it is tagged "Performance" (network review 2026-11-10, finding 4): CI can run it in a separate
        /// job or exclude it on loaded runners. The 100 us budget is ~35x the measured 2.9 us, so it only catches real
        /// regressions (an accidental O(n^2) or per-tick allocation), never machine noise.
        /// </remarks>
        [Test, Category("Performance")]
        public void Step_WithTheNewMoves_StaysUnderTheRollbackBudget()
        {
            // Arrange
            const double BudgetMicros = 100.0;
            var cfg = Cfg(FighterClass.Ranged, FighterClass.Magic, 1);
            var s = new ChibiSimState(); ChibiSimulation.Init(s, cfg);
            for (int n = 0; n < 600; n++) ChibiSimulation.Step(s, MovesetBot(7, n), MovesetBot(8, n));

            // Act
            const int Steps = 6000;
            var sw = Stopwatch.StartNew();
            for (int n = 600; n < 600 + Steps; n++) ChibiSimulation.Step(s, MovesetBot(7, n), MovesetBot(8, n));
            sw.Stop();
            double micros = sw.Elapsed.TotalMilliseconds * 1000.0 / Steps;

            // Assert
            System.Console.WriteLine($"average Step: {micros:F2} us");
            Assert.Less(micros, BudgetMicros, $"Step averages {micros:F1} us (budget {BudgetMicros} us)");
        }
    }
}
