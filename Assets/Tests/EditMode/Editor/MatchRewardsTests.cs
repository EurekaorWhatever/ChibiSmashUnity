// Story type: Logic (rewards and progress from the sim match, port step 18) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source: Game/MatchController.cs AwardRewards (MC:195-225), now Core/MatchRewards.cs; MaxCombo (F:636) now in the sim (rules v17).
using System.Collections.Generic;
using System.Linq;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>MatchRewards (coins, quests, profile stats) and the sim's MaxCombo counter.</summary>
    public class MatchRewardsTests
    {
        sealed class MemoryStore : ISaveStore
        {
            readonly Dictionary<string, string> _d = new Dictionary<string, string>();
            public string Get(string key) => _d.TryGetValue(key, out var v) ? v : "";
            public void Set(string key, string value) => _d[key] = value;
            public void Flush() { }
        }

        static SaveData Fresh() => SaveData.Load(new MemoryStore());

        static MatchStatsForRewards Stats(bool won = false, bool lost = false, bool pve = false, float posture = 0f,
                                          int combos = 0, int ults = 0, int parries = 0, bool training = false) =>
            new MatchStatsForRewards
            {
                Won = won, Lost = lost, Pve = pve, PostureAverage = posture, CombosLanded = combos, UltsUsed = ults,
                Parries = parries, Training = training,
            };

        [TestCase(true, true, 50)]
        [TestCase(true, false, 15)]
        [TestCase(false, true, 40)]
        [TestCase(false, false, 15)]
        public void MatchCoins_PvEAndPvP_WinOrNot_MatchOriginal(bool pve, bool won, int expected)
        {
            var save = Fresh();
            int before = save.coins;
            var log = new List<string>();
            int earned = MatchRewards.Award(save, Stats(won: won, lost: !won, pve: pve), log);
            Assert.AreEqual(expected, earned - QuestCoins(log), "match coins (MC:204)");
            Assert.AreEqual(before + earned, save.coins);
        }

        [Test]
        public void PostureCoins_RoundPostureTimes30()
        {
            var save = Fresh();
            var log = new List<string>();
            int earned = MatchRewards.Award(save, Stats(lost: true, posture: 0.5f), log);
            Assert.AreEqual(15 + 15, earned - QuestCoins(log), "15 match + round(0.5 x 30) = 15 posture (MC:208-211)");
        }

        [Test]
        public void Stats_WinLossDraw_CountedLikeOriginal()
        {
            var save = Fresh();
            var log = new List<string>();
            MatchRewards.Award(save, Stats(won: true), log);
            MatchRewards.Award(save, Stats(lost: true), log);
            MatchRewards.Award(save, Stats(), log);   // draw
            Assert.AreEqual(3, save.matches);
            Assert.AreEqual(1, save.wins);
            Assert.AreEqual(1, save.losses, "a draw is neither a win nor a loss (MC:221-222)");
        }

        [Test]
        public void Training_GivesNothing()
        {
            var save = Fresh();
            int coins = save.coins, matches = save.matches;
            var log = new List<string>();
            Assert.AreEqual(0, MatchRewards.Award(save, Stats(won: true, training: true), log));
            Assert.AreEqual(coins, save.coins);
            Assert.AreEqual(matches, save.matches);
            Assert.IsEmpty(log);
        }

        [Test]
        public void Quests_ProgressFromSimCounters()
        {
            var save = Fresh();
            int Total(Quest q) => q.completions * q.target + q.progress;   // progress wraps on completion (SaveData.Progress)
            var before = save.quests.ToDictionary(q => q.id, Total);
            MatchRewards.Award(save, Stats(won: true, pve: true, posture: 0.9f, combos: 2, ults: 1, parries: 3), new List<string>());
            var expected = new Dictionary<string, int> { { "win", 1 }, { "pve", 1 }, { "posture", 1 }, { "combo", 2 }, { "ult", 1 }, { "parry", 3 } };
            int checkedCount = 0;
            foreach (var q in save.quests)
            {
                if (!expected.TryGetValue(q.id, out int gain)) continue;
                Assert.AreEqual(gain, Total(q) - before[q.id], "quest " + q.id + " (MC:213-218)");
                checkedCount++;
            }
            Assert.Greater(checkedCount, 0, "precondition: the profile has reward quests");
        }

        [Test]
        public void SimMaxCombo_TracksHighestComboAcrossResets()
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = 3, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy });
            var idle = default(ChibiInputPacket);
            for (int t = 0; t < 90; t++) ChibiSimulation.Step(s, idle, idle);
            var a = s.F[0]; var d = s.F[1];
            a.X = 0; d.X = 900; a.Facing = 1; d.Facing = -1;
            var light = ChibiInputPacket.Make(ChibiDir.Neutral, ChibiButtons.Light);
            int peak = 0;
            for (int t = 0; t < 400; t++)
            {
                d.X = 900; d.Y = a.Y; d.Invuln = 0;
                ChibiSimulation.Step(s, t % 2 == 0 ? light : idle, idle);
                peak = System.Math.Max(peak, a.ComboHits);
                if (s.MatchOver) break;
            }
            Assert.Greater(peak, 1, "precondition: a combo of 2+ hits");
            Assert.AreEqual(peak, a.MaxCombo, "MaxCombo = highest ComboHits seen");
        }

        /// <summary>Coins from completed quests in the log (lines ending in "+N coins" written by SaveData.Progress), 0 when none.</summary>
        static int QuestCoins(List<string> log)
        {
            int sum = 0;
            foreach (var l in log)
            {
                if (!l.StartsWith("Quest")) continue;
                int plus = l.LastIndexOf('+');
                if (plus < 0) continue;
                var digits = new string(l.Substring(plus + 1).TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out int n)) sum += n;
            }
            return sum;
        }
    }
}
