// Story type: Logic (four players, Phase 1: engine for N fighters, 1v1 bit-identical) -- evidence: automated EditMode
// unit test -- gate: BLOCKING
// Spec: docs/architecture/netcode/four-player-design.md sections 1.1-1.5 and 6 (tests 1-2), decisions in section 9.
// The 1v1 guard is NetcodeContractTests (goldens unchanged) plus ChibiSyncVerificationTests (1v1 reflection checks);
// these tests cover the 3..4 fighter engine: determinism, snapshot / restore, the nearest-enemy rule and field coverage.
using System;
using System.Collections.Generic;
using System.Reflection;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Four-player engine (Phase 1): determinism, rollback state coverage and the enemy rules.</summary>
    public class ChibiFourPlayerTests
    {
        const int SmokeTicks = 3600;   // 60 s of play: plenty of hits, KOs, grabs, ults and summons

        static ChibiMatchConfig Cfg(int players, int stage, ChibiMatchMode mode = ChibiMatchMode.FreeForAll, int teamOf = 0) =>
            new ChibiMatchConfig
            {
                Seed = 0x4F0Fu + (uint)stage, PlayerCount = players, Mode = mode, TeamOf = teamOf, Stage = stage,
                Class0 = FighterClass.Melee, Passive0 = 0, Class1 = FighterClass.Magic, Passive1 = 1,
                Class2 = FighterClass.Polearm, Passive2 = 1, Class3 = FighterClass.Heavy, Passive3 = 0,
            };

        /// <summary>Deterministic busy input for fighter <paramref name="fighter"/> on tick <paramref name="n"/> (no RNG, no clock).</summary>
        static ChibiInputPacket Input(int fighter, int n)
        {
            unchecked
            {
                uint h = (uint)(n / 4) * 2654435761u + (uint)(fighter + 1) * 40503u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                var b = ChibiButtons.None;
                if (n % 2 == 0)
                {
                    if (h % 3 == 0) b |= ChibiButtons.Light;
                    if (h % 5 == 1) b |= ChibiButtons.Heavy;
                    if (h % 11 == 2) b |= ChibiButtons.Skill1;
                    if (h % 13 == 3) b |= ChibiButtons.Skill2;
                    if (h % 17 == 4) b |= ChibiButtons.Skill3;
                    if (h % 19 == 5) b |= ChibiButtons.Jump;
                    if (h % 23 == 6) b |= ChibiButtons.Shield;
                    if (h % 29 == 7) b |= ChibiButtons.Dash;
                    if (h % 31 == 8) b |= ChibiButtons.Ultimate;
                    if (h % 37 == 9) b |= ChibiButtons.Special;
                }
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes((int)(h % 3) - 1, (int)((h >> 5) % 3) - 1), b);
            }
        }

        static void StepScripted(ChibiSimState s, ChibiInputPacket[] buf, int n)
        {
            for (int i = 0; i < buf.Length; i++) buf[i] = Input(i, n);
            ChibiSimulation.Step(s, buf);
        }

        static void AssertHoldInvariant(ChibiSimState s, int tick)
        {
            for (int i = 0; i < s.PlayerCount; i++)
            {
                var f = s.F[i];
                if (f.HeldBy >= 0) Assert.AreEqual(i, s.F[f.HeldBy].HoldTarget, $"tick {tick}: F[{i}].HeldBy has no matching HoldTarget");
                if (f.HoldTarget >= 0) Assert.AreEqual(i, s.F[f.HoldTarget].HeldBy, $"tick {tick}: F[{i}].HoldTarget has no matching HeldBy");
            }
        }

        static IEnumerable<TestCaseData> SmokeCases()
        {
            for (int players = 3; players <= 4; players++)
                foreach (int stage in new[] { 0, 2, 4, 6 })
                    yield return new TestCaseData(players, stage).SetName($"FourPlayer_Smoke_{players}P_Stage{stage}_DeterministicNoExceptions");
            yield return new TestCaseData(4, 1).SetName("FourPlayer_Smoke_4P_Stage1_DeterministicNoExceptions");
        }

        [TestCaseSource(nameof(SmokeCases))]
        public void FourPlayer_Smoke_TwoRunsAgree_HoldInvariant_NoExceptions(int players, int stage)
        {
            var cfg = Cfg(players, stage);
            var a = new ChibiSimState(); ChibiSimulation.Init(a, cfg);
            var b = new ChibiSimState(); ChibiSimulation.Init(b, cfg);
            Assert.AreEqual(players, a.PlayerCount);
            var bufA = new ChibiInputPacket[players];
            var bufB = new ChibiInputPacket[players];
            int hitsLanded = 0;
            for (int n = 0; n < SmokeTicks; n++)
            {
                StepScripted(a, bufA, n);
                StepScripted(b, bufB, n);
                Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum(), $"two runs with the same inputs diverged at tick {n}");
                AssertHoldInvariant(a, n);
                if (a.MatchOver) break;
            }
            for (int i = 0; i < players; i++)
            {
                hitsLanded += a.F[i].HitsLanded;
                Assert.That(a.F[i].Stocks >= 0 && a.F[i].Stocks <= ChibiPercentConst.StocksStart, "stocks in range");
                Assert.That(a.F[i].LastHitBy >= -1 && a.F[i].LastHitBy < players && a.F[i].LastHitBy != i, "LastHitBy is another fighter");
            }
            Assert.Greater(hitsLanded, 0, "the fighters actually fought");
            if (a.MatchOver)
            {
                if (a.Winner >= 0) Assert.AreNotEqual(0, a.WinnerMask & (1 << a.Winner), "Winner is in WinnerMask");
                for (int i = 0; i < players; i++) Assert.That(a.Place[i] >= 1 && a.Place[i] <= players, $"Place[{i}] set at match end");
            }
        }

        [Test]
        public void FourCpuLocalSession_RunsTwiceIdentically_NoExceptions()
        {
            var cfg = Cfg(4, 0);
            ChibiLocalSession Make() => new ChibiLocalSession(cfg, new IChibiInputSource[]
            {
                new ChibiCpuInputSource(0, 3, cfg.Seed), new ChibiCpuInputSource(1, 3, cfg.Seed),
                new ChibiCpuInputSource(2, 3, cfg.Seed), new ChibiCpuInputSource(3, 3, cfg.Seed),
            });
            var a = Make();
            var b = Make();
            int frames = 0;
            for (int n = 0; n < SmokeTicks && a.Tick() & b.Tick(); n++)
            {
                frames++;
                Assert.AreEqual(a.State.ComputeChecksum(), b.State.ComputeChecksum(), $"4-CPU sessions diverged at frame {n}");
                AssertHoldInvariant(a.State, n);
            }
            Assert.Greater(frames, 0);
            Assert.Throws<ArgumentException>(() => new ChibiLocalSession(cfg, new IChibiInputSource[]
                { new ChibiCpuInputSource(0, 3, 1), new ChibiCpuInputSource(1, 3, 1) }), "source count must match PlayerCount");
        }

        [Test]
        public void FourPlayer_SnapshotRestore_ResimulatesIdentically_AndCopyRoundTrips()
        {
            var cfg = Cfg(4, 0);
            var s = new ChibiSimState(); ChibiSimulation.Init(s, cfg);
            var buf = new ChibiInputPacket[4];
            var snap = new ChibiSimState();
            const int depth = 9;
            for (int n = 0; n < 2400 && !s.MatchOver; n++)
            {
                if (n % 37 == 0)
                {
                    snap.CopyFrom(s);
                    Assert.AreEqual(s.ComputeChecksum(), snap.ComputeChecksum(), $"CopyFrom round trip differs at tick {n}");
                    var straight = new ChibiSimState(); straight.CopyFrom(s);
                    var resim = new ChibiSimState();
                    for (int k = 0; k < depth; k++) StepScripted(straight, buf, n + k);
                    resim.CopyFrom(snap);
                    for (int k = 0; k < depth; k++) StepScripted(resim, buf, n + k);
                    Assert.AreEqual(straight.ComputeChecksum(), resim.ComputeChecksum(), $"restore + resimulate diverged from tick {n}");
                }
                StepScripted(s, buf, n);
            }
        }

        [Test]
        public void NearestEnemy_SmallestDistance_TieGoesToLowestIndex()
        {
            var s = new ChibiSimState(); ChibiSimulation.Init(s, Cfg(4, 0));
            Place(s, 0, 0, 0); Place(s, 1, 1000, 0); Place(s, 2, -1000, 0); Place(s, 3, 0, 3000);
            Assert.AreEqual(1, ChibiSimulation.NearestEnemy(s, 0), "1 and 2 tie at 1000: lowest index wins");
            Place(s, 1, 1001, 0);
            Assert.AreEqual(2, ChibiSimulation.NearestEnemy(s, 0), "2 is now strictly nearer");
            Place(s, 3, 600, 700);   // 600² + 700² = 850 000 < 1000² = 1 000 000
            Assert.AreEqual(3, ChibiSimulation.NearestEnemy(s, 0), "squared distance uses both axes");
            // From F[3] (600, 700): F[1] (1001, 0) is 401² + 700² = 650 801, F[0] is 850 000, F[2] (-1000, 0) is 3 050 000.
            Assert.AreEqual(1, ChibiSimulation.NearestEnemy(s, 3), "fighter 3's nearest is 1");
            // Exact tie for fighter 3: F[1] and F[2] mirrored around it, both 2000² away; F[0] farther.
            Place(s, 3, 0, 5000); Place(s, 1, 2000, 5000); Place(s, 2, -2000, 5000); Place(s, 0, 0, 0);
            Assert.AreEqual(1, ChibiSimulation.NearestEnemy(s, 3), "tie between 1 and 2: lowest index");
        }

        [Test]
        public void NearestEnemy_SkipsOutFighters_AndTeammates_FoeKeeps1v1Lookup()
        {
            var s = new ChibiSimState(); ChibiSimulation.Init(s, Cfg(4, 0));
            Place(s, 0, 0, 0); Place(s, 1, 1000, 0); Place(s, 2, 2000, 0); Place(s, 3, 3000, 0);
            s.F[1].OutTicks = 10;
            Assert.AreEqual(2, ChibiSimulation.NearestEnemy(s, 0), "an Out fighter is not an enemy");
            s.F[2].HoverTicks = 10;
            Assert.AreEqual(3, ChibiSimulation.NearestEnemy(s, 0), "a hovering (respawning) fighter is not an enemy");
            s.F[3].OutTicks = 10;
            Assert.AreEqual(-1, ChibiSimulation.NearestEnemy(s, 0), "no enemy in play");
            Assert.AreEqual(1, ChibiSimulation.Foe(s, 0), "Foe falls back to the nearest foe out of play");

            s.F[1].OutTicks = 0; s.F[2].HoverTicks = 0; s.F[3].OutTicks = 0;
            s.Mode = ChibiMatchMode.Team; s.TeamOf = 0b0010 | 0b0100;   // teams {0, 3} and {1, 2}
            Place(s, 3, 10, 0);                                         // teammate right next to 0
            Assert.IsFalse(ChibiSimulation.IsEnemy(s, 0, 3), "a teammate is never an enemy");
            Assert.IsFalse(ChibiSimulation.IsEnemy(s, 0, 0), "nobody is their own enemy");
            Assert.AreEqual(1, ChibiSimulation.NearestEnemy(s, 0), "the teammate is skipped");

            var d = new ChibiSimState();
            ChibiSimulation.Init(d, new ChibiMatchConfig { Seed = 3, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy });
            Assert.AreEqual(2, d.PlayerCount, "an unset PlayerCount plays as 1v1");
            d.F[1].OutTicks = 30;
            Assert.AreEqual(1, ChibiSimulation.Foe(d, 0), "1v1: the opponent even while Out (rules v24 lookup)");
            Assert.AreEqual(0, ChibiSimulation.Foe(d, 1));
            Assert.AreEqual(-1, ChibiSimulation.NearestEnemy(d, 0), "but it is not an enemy in play");
        }

        static void Place(ChibiSimState s, int i, int x, int y) { s.F[i].X = x; s.F[i].Y = y; }

        // ================================================================== field coverage (ChibiSyncVerificationTests style)

        /// <summary>Same exemption as the 1v1 test: Stats is derived from Class + Passive at Init.</summary>
        static readonly HashSet<string> FighterHashExempt = new HashSet<string> { "Stats" };

        static ChibiSimState FourPlayerState()
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, Cfg(4, 0));
            return s;
        }

        [Test]
        public void FourPlayer_Checksum_ChangesWhenAnyFieldOfFighter3Changes()
        {
            var s = FourPlayerState();
            uint baseSum = s.ComputeChecksum();
            var missing = new List<string>();
            foreach (var fi in typeof(ChibiFighterSim).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (fi.IsLiteral || FighterHashExempt.Contains(fi.Name)) continue;
                var probe = new ChibiSimState(); probe.CopyFrom(s);
                var f = probe.F[3];
                if (fi.FieldType == typeof(int)) fi.SetValue(f, (int)fi.GetValue(f) + 7);
                else if (fi.FieldType == typeof(bool)) fi.SetValue(f, !(bool)fi.GetValue(f));
                else if (fi.FieldType == typeof(int[])) ((int[])fi.GetValue(f))[0] += 7;
                else continue;
                if (probe.ComputeChecksum() == baseSum) missing.Add(fi.Name);
            }
            CollectionAssert.IsEmpty(missing, "fighter 3 fields not in the 4-player checksum");
        }

        [Test]
        public void FourPlayer_Checksum_ChangesWhenAnyMatchFieldOrProjectileFieldChanges()
        {
            var s = FourPlayerState();
            s.Proj[3].Active = true; s.Proj[3].Owner = 2;
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
            for (int i = 0; i < ChibiSimState.MaxFighters; i++)
            {
                var probe = new ChibiSimState(); probe.CopyFrom(s);
                probe.Place[i] += 1;
                if (probe.ComputeChecksum() == baseSum) missing.Add("ChibiSimState.Place[" + i + "]");
            }
            CollectionAssert.IsEmpty(missing, "4-player state fields not in the checksum");
        }

        [Test]
        public void NewFields_AreCopied_ByCopyFrom()
        {
            var s = FourPlayerState();
            s.PlayerCount = 3; s.Mode = ChibiMatchMode.Team; s.TeamOf = 0b0101; s.WinnerMask = 0b0110;
            for (int i = 0; i < ChibiSimState.MaxFighters; i++)
            {
                s.Place[i] = i + 1;
                var f = s.F[i];
                f.HeldBy = (i + 1) % 4; f.HoldTarget = (i + 2) % 4; f.MoveHitMask = 1 << i; f.LastHitBy = (i + 3) % 4; f.ProgTarget = i;
                f.X = 1000 * i + 17;
            }
            s.Proj[5].Active = true; s.Proj[5].HitMask = 0b1010;
            var c = new ChibiSimState(); c.CopyFrom(s);
            Assert.AreEqual(3, c.PlayerCount); Assert.AreEqual(ChibiMatchMode.Team, c.Mode);
            Assert.AreEqual(0b0101, c.TeamOf); Assert.AreEqual(0b0110, c.WinnerMask);
            CollectionAssert.AreEqual(s.Place, c.Place, "Place copied");
            for (int i = 0; i < ChibiSimState.MaxFighters; i++)
            {
                Assert.AreEqual(s.F[i].HeldBy, c.F[i].HeldBy, $"F[{i}].HeldBy");
                Assert.AreEqual(s.F[i].HoldTarget, c.F[i].HoldTarget, $"F[{i}].HoldTarget");
                Assert.AreEqual(s.F[i].MoveHitMask, c.F[i].MoveHitMask, $"F[{i}].MoveHitMask");
                Assert.AreEqual(s.F[i].LastHitBy, c.F[i].LastHitBy, $"F[{i}].LastHitBy");
                Assert.AreEqual(s.F[i].ProgTarget, c.F[i].ProgTarget, $"F[{i}].ProgTarget");
                Assert.AreEqual(s.F[i].X, c.F[i].X, $"F[{i}] (slot 2..3 copied too)");
            }
            Assert.AreEqual(0b1010, c.Proj[5].HitMask, "projectile HitMask copied");
            Assert.AreEqual(s.ComputeChecksum(), c.ComputeChecksum());
        }

        [Test]
        public void ConfigHash_1v1Unchanged_ByUnusedSeats_MultiplayerFieldsHashed()
        {
            var one = new ChibiMatchConfig { Seed = 5, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy };
            var oneSeats = one; oneSeats.Class2 = FighterClass.Ranged; oneSeats.Passive3 = 1; oneSeats.PlayerCount = 2;
            Assert.AreEqual(one.ComputeHash(), oneSeats.ComputeHash(), "a 1v1 config ignores seats 2..3 (keeps the v24 hash)");

            var four = one; four.PlayerCount = 4;
            Assert.AreNotEqual(one.ComputeHash(), four.ComputeHash(), "PlayerCount is in the hash once above 2");
            var fourB = four; fourB.Class3 = FighterClass.Magic;
            Assert.AreNotEqual(four.ComputeHash(), fourB.ComputeHash(), "seat classes are hashed in a 4-player config");
            var team = four; team.Mode = ChibiMatchMode.Team; team.TeamOf = 0b0110;
            Assert.AreNotEqual(four.ComputeHash(), team.ComputeHash(), "mode and teams are hashed");
        }
    }
}
