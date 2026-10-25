using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChibiClash
{
    public enum Rarity { Common, Rare, Epic, Legendary }

    public enum Hat { None, Crown, Bandana, Horns, Halo, Cat }

    /// <summary>A purely cosmetic skin. Skins never touch stats (GDD 4.3).</summary>
    public class Skin
    {
        public string id;
        public FighterClass cls;
        public string name;
        public Rarity rarity;
        public Color body, accent;
        public Hat hat;
        public Color ultFx;
        public bool goldCinematic;

        public static readonly List<Skin> All = Build();

        public static Skin Default(FighterClass c) => All.First(s => s.cls == c && s.id.EndsWith("_default"));
        public static Skin Find(string id) => All.FirstOrDefault(s => s.id == id);

        public static Color RarityColor(Rarity r)
        {
            switch (r)
            {
                case Rarity.Rare: return new Color(0.3f, 0.6f, 1f);
                case Rarity.Epic: return new Color(0.75f, 0.4f, 1f);
                case Rarity.Legendary: return new Color(1f, 0.78f, 0.2f);
                default: return new Color(0.75f, 0.75f, 0.78f);
            }
        }

        static List<Skin> Build()
        {
            var list = new List<Skin>();
            void Add(FighterClass c, string key, string name, Rarity r, Color body, Color accent, Hat hat, Color fx, bool gold = false)
            {
                list.Add(new Skin { id = c + "_" + key, cls = c, name = name, rarity = r, body = body, accent = accent, hat = hat, ultFx = fx, goldCinematic = gold });
            }
            foreach (var def in ClassDef.All)
            {
                var c = def.type;
                Color b = def.color;
                Add(c, "default", "Classic", Rarity.Common, b, Color.white, Hat.None, b);
                Add(c, "mint", "Mint Pop", Rarity.Common, new Color(0.5f, 0.92f, 0.78f), new Color(0.2f, 0.4f, 0.35f), Hat.Bandana, new Color(0.4f, 1f, 0.8f));
                Add(c, "shadow", "Shadow", Rarity.Common, new Color(0.28f, 0.28f, 0.34f), new Color(0.9f, 0.2f, 0.3f), Hat.Bandana, new Color(0.9f, 0.2f, 0.3f));
                Add(c, "sakura", "Sakura", Rarity.Rare, new Color(1f, 0.7f, 0.82f), new Color(1f, 1f, 1f), Hat.Cat, new Color(1f, 0.55f, 0.75f));
                Add(c, "inferno", "Inferno", Rarity.Epic, new Color(0.9f, 0.2f, 0.15f), new Color(1f, 0.8f, 0.2f), Hat.Horns, new Color(1f, 0.4f, 0.05f));
                Add(c, "celestial", "Celestial", Rarity.Legendary, new Color(0.98f, 0.95f, 0.85f), new Color(1f, 0.8f, 0.25f), Hat.Halo, new Color(1f, 0.85f, 0.3f), true);
            }
            return list;
        }
    }

    [Serializable]
    public class Quest
    {
        public string id;
        public string desc;
        public int target;
        public int progress;
        public int reward;
        public int completions;
    }

    [Serializable]
    public class Settings
    {
        public int matchTime = MatchController.MatchSeconds;   // locked: every match is 180 s
        public int cpuDifficulty = 2;
        public bool useWebcam;
        public bool camInit;            // set once the camera has been switched on by default
        public bool showCvOverlay = true;
        public bool screenShake = true;
        public bool preMatchCheck = true;
    }

    /// <summary>Persistent player profile: currency, owned cosmetics, quests and settings.</summary>
    [Serializable]
    public class SaveData
    {
        const string Key = "ChibiClash.Save.v1";
        public const int PullCost = 100;
        public const int TenPullCost = 900;

        public int coins = 300;
        public List<string> owned = new List<string>();
        public List<string> equipped = new List<string>();
        public List<Quest> quests = new List<Quest>();
        public List<string> keybinds = new List<string>();
        public int keybindVersion;
        public Settings settings = new Settings();
        public int wins, losses, matches;

        public static SaveData Load()
        {
            SaveData d = null;
            try
            {
                var json = PlayerPrefs.GetString(Key, "");
                if (!string.IsNullOrEmpty(json)) d = JsonUtility.FromJson<SaveData>(json);
            }
            catch (Exception e) { Debug.LogWarning("ChibiClash: save reset (" + e.Message + ")"); }
            if (d == null) d = new SaveData();
            d.Repair();
            return d;
        }

        public void Save()
        {
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(this));
            PlayerPrefs.Save();
        }

        void Repair()
        {
            if (settings == null) settings = new Settings();
            if (keybinds == null) keybinds = new List<string>();
            Bindings.Normalize(this);
            settings.matchTime = MatchController.MatchSeconds;   // match length is locked
            if (!settings.camInit) { settings.useWebcam = true; settings.camInit = true; }   // camera is on by default
            foreach (var def in ClassDef.All)
            {
                var id = Skin.Default(def.type).id;
                if (!owned.Contains(id)) owned.Add(id);
            }
            while (equipped.Count < ClassDef.All.Length) equipped.Add(Skin.Default((FighterClass)equipped.Count).id);
            AddQuest("win", "Win 3 matches", 3, 80);
            AddQuest("pve", "Beat the CPU twice (PvE)", 2, 60);
            AddQuest("combo", "Land 10 full combos", 10, 60);
            AddQuest("ult", "Trigger 3 gesture ultimates", 3, 70);
            AddQuest("parry", "Parry 5 attacks", 5, 50);
            AddQuest("posture", "Finish a match with 80%+ good posture", 1, 50);
        }

        void AddQuest(string id, string desc, int target, int reward)
        {
            if (quests.Any(q => q.id == id)) return;
            quests.Add(new Quest { id = id, desc = desc, target = target, reward = reward });
        }

        public Skin Equipped(FighterClass c)
        {
            return Skin.Find(equipped[(int)c]) ?? Skin.Default(c);
        }

        public void Equip(Skin s)
        {
            if (!owned.Contains(s.id)) return;
            equipped[(int)s.cls] = s.id;
            Save();
        }

        /// <summary>Progress a quest; returns coins awarded if it completed (quests repeat).</summary>
        public int Progress(string id, int amount, List<string> log)
        {
            var q = quests.FirstOrDefault(x => x.id == id);
            if (q == null || amount <= 0) return 0;
            q.progress += amount;
            int earned = 0;
            while (q.progress >= q.target)
            {
                q.progress -= q.target;
                q.completions++;
                earned += q.reward;
                log?.Add("Quest: " + q.desc + "  +" + q.reward);
            }
            coins += earned;
            return earned;
        }

        public struct PullResult { public Skin skin; public bool duplicate; }

        /// <summary>Cosmetics-only gacha. Rates: Common 60%, Rare 28%, Epic 10%, Legendary 2%.</summary>
        public List<PullResult> Pull(int count, FighterClass? banner)
        {
            var results = new List<PullResult>();
            int cost = count >= 10 ? TenPullCost : PullCost * count;
            if (coins < cost) return results;
            coins -= cost;
            for (int i = 0; i < count; i++)
            {
                float r = UnityEngine.Random.value;
                Rarity rarity = r < 0.02f ? Rarity.Legendary : r < 0.12f ? Rarity.Epic : r < 0.40f ? Rarity.Rare : Rarity.Common;
                var pool = Skin.All.Where(s => s.rarity == rarity && !s.id.EndsWith("_default") && (banner == null || s.cls == banner)).ToList();
                var skin = pool[UnityEngine.Random.Range(0, pool.Count)];
                bool dup = owned.Contains(skin.id);
                if (dup) coins += 20;
                else owned.Add(skin.id);
                results.Add(new PullResult { skin = skin, duplicate = dup });
            }
            Save();
            return results;
        }
    }
}
