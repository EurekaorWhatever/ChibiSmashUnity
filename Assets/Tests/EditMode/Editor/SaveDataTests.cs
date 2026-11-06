using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ChibiClash.Tests
{
    /// <summary>Regression tests for the quest-target hang and the corrupt-save overwrite. No PlayerPrefs, no file I/O.</summary>
    public class SaveDataTests
    {
        /// <summary>In-memory ISaveStore that records every Set, so tests can check what Load wrote.</summary>
        sealed class MemoryStore : ISaveStore
        {
            public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
            public readonly List<string> Writes = new List<string>();
            public string Get(string key) => Data.TryGetValue(key, out var v) ? v : "";
            public void Set(string key, string value) { Data[key] = value; Writes.Add(key); }
            public void Flush() { }
        }

        const string CorruptJson = "{not json";
        const string OtherCorruptJson = "{still not json";
        const string BuiltInQuest = "win";
        const string BrokenQuest = "broken";

        static Regex BackupWarning => new Regex(Regex.Escape(SaveData.BackupKey));

        static int ProgressOnTarget(int target, out SaveData d, out List<string> log)
        {
            d = new SaveData();
            d.quests.Add(new Quest { id = BrokenQuest, desc = "Broken", target = target, reward = 50 });
            log = new List<string>();
            return d.Progress(BrokenQuest, 1, log);
        }

        // If either Progress test hangs, the target <= 0 guard has regressed (the loop cannot be interrupted from a test).
        [Test]
        public void Progress_TargetZero_ReturnsZeroAndTerminates()
        {
            int coinsBefore = new SaveData().coins;
            Assert.AreEqual(0, ProgressOnTarget(0, out var d, out var log));
            Assert.AreEqual(coinsBefore, d.coins);
            Assert.IsEmpty(log);
        }

        [Test]
        public void Progress_NegativeTarget_ReturnsZeroAndTerminates()
        {
            int coinsBefore = new SaveData().coins;
            Assert.AreEqual(0, ProgressOnTarget(-5, out var d, out var log));
            Assert.AreEqual(coinsBefore, d.coins);
            Assert.IsEmpty(log);
        }

        [Test]
        public void Load_SavedQuestTargetZero_RestoresCanonicalTargetAndProgressWorks()
        {
            int canonical = SaveData.Load(new MemoryStore()).quests.First(q => q.id == BuiltInQuest).target;
            var store = new MemoryStore();
            var bad = SaveData.Load(store);
            bad.quests.First(q => q.id == BuiltInQuest).target = 0;
            bad.Save(store);
            StringAssert.Contains("\"target\":0", store.Data[SaveData.Key]);   // the fixture really holds a bad target

            var d = SaveData.Load(store);
            var quest = d.quests.First(q => q.id == BuiltInQuest);

            Assert.Greater(canonical, 0);
            Assert.AreEqual(canonical, quest.target);
            int coinsBefore = d.coins;
            Assert.AreEqual(quest.reward, d.Progress(BuiltInQuest, quest.target, null));
            Assert.AreEqual(coinsBefore + quest.reward, d.coins);
            Assert.AreEqual(1, quest.completions);
            Assert.AreEqual(0, quest.progress);
        }

        [Test]
        public void Load_CorruptJson_ReturnsFreshProfileAndBacksUpOriginal()
        {
            var store = new MemoryStore();
            store.Data[SaveData.Key] = CorruptJson;
            LogAssert.Expect(LogType.Warning, BackupWarning);

            var d = SaveData.Load(store);

            Assert.IsNotNull(d);
            Assert.AreEqual(new SaveData().coins, d.coins);
            Assert.AreEqual(ClassDef.All.Length, d.equipped.Count);
            Assert.IsNotEmpty(d.quests);
            Assert.AreEqual(CorruptJson, store.Data[SaveData.BackupKey]);
            Assert.AreEqual(CorruptJson, store.Data[SaveData.Key]);
            CollectionAssert.DoesNotContain(store.Writes, SaveData.Key);
        }

        [Test]
        public void Save_AfterCorruptLoad_KeepsBackupIntact()
        {
            var store = new MemoryStore();
            store.Data[SaveData.Key] = CorruptJson;
            LogAssert.Expect(LogType.Warning, BackupWarning);
            var d = SaveData.Load(store);

            d.Save(store);

            Assert.AreEqual(CorruptJson, store.Data[SaveData.BackupKey]);
            Assert.AreNotEqual(CorruptJson, store.Data[SaveData.Key]);
            SaveData.Load(store);   // the new main save is valid, so this load must leave the backup alone
            Assert.AreEqual(CorruptJson, store.Data[SaveData.BackupKey]);
            Assert.AreEqual(1, store.Writes.Count(k => k == SaveData.BackupKey));
        }

        [Test]
        public void Load_ValidJson_WritesNoBackup()
        {
            var store = new MemoryStore();
            SaveData.Load(store).Save(store);
            store.Writes.Clear();

            SaveData.Load(store);

            Assert.IsFalse(store.Data.ContainsKey(SaveData.BackupKey));
            Assert.IsEmpty(store.Writes);
        }

        [Test]
        public void Load_SameCorruptJsonTwice_WritesBackupOnce()
        {
            var store = new MemoryStore();
            store.Data[SaveData.Key] = CorruptJson;
            LogAssert.Expect(LogType.Warning, BackupWarning);
            LogAssert.Expect(LogType.Warning, BackupWarning);

            SaveData.Load(store);
            SaveData.Load(store);

            Assert.AreEqual(1, store.Writes.Count(k => k == SaveData.BackupKey));
        }

        [Test]
        public void Load_DifferentCorruptJson_KeepsNewestBackup()
        {
            var store = new MemoryStore();
            store.Data[SaveData.BackupKey] = CorruptJson;
            store.Data[SaveData.Key] = OtherCorruptJson;
            LogAssert.Expect(LogType.Warning, BackupWarning);

            SaveData.Load(store);

            Assert.AreEqual(OtherCorruptJson, store.Data[SaveData.BackupKey]);
        }
    }
}
