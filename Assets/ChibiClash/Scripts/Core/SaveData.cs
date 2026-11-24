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

        [NonSerialized] string descKey;
        /// <summary>The description in the current language (key quest.&lt;id&gt;); the saved English desc if there is no key.</summary>
        public string DescText
        {
            get
            {
                if (descKey == null) descKey = "quest." + id;
                return Loc.Has(descKey) ? Loc.T(descKey) : desc;
            }
        }
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

    /// <summary>Key/value storage behind SaveData. The game uses PlayerPrefs; tests pass an in-memory store.</summary>
    public interface ISaveStore
    {
        /// <summary>The stored string for key, or "" if there is none.</summary>
        string Get(string key);
        /// <summary>Store value under key (may stay in memory until Flush).</summary>
        void Set(string key, string value);
        /// <summary>Write pending changes to disk.</summary>
        void Flush();
    }

    /// <summary>ISaveStore backed by PlayerPrefs, used by Load() and Save().</summary>
    public sealed class PlayerPrefsSaveStore : ISaveStore
    {
        public static readonly PlayerPrefsSaveStore Instance = new PlayerPrefsSaveStore();
        public string Get(string key) => PlayerPrefs.GetString(key, "");
        public void Set(string key, string value) => PlayerPrefs.SetString(key, value);
        public void Flush() => PlayerPrefs.Save();
    }

    /// <summary>One account's identity on this computer (the active one is mirrored in SaveData.playerName / ageBand).</summary>
    [Serializable]
    public class AccountProfile
    {
        public string key = "";
        public string name = "";
        public int ageBand;
        public int age;
        public bool ageRecorded;
    }

    /// <summary>Persistent player profile: currency, owned cosmetics, quests and settings.</summary>
    [Serializable]
    public class SaveData
    {
        /// <summary>Store key of the profile JSON.</summary>
        public const string Key = "ChibiClash.Save.v1";
        /// <summary>Store key where an unreadable profile is kept instead of being overwritten.</summary>
        public const string BackupKey = Key + ".corrupt";
        public const int PullCost = 100;
        public const int TenPullCost = 900;
        /// <summary>Layout version of this class; <see cref="Migrate"/> upgrades older saves to it.</summary>
        public const int SchemaVersion = 1;
        /// <summary>Coins are clamped to 0..MaxCoins on load (an edited save cannot overflow the shop).</summary>
        public const int MaxCoins = 9_999_999;

        /// <summary>Layout version this profile was written with (0 = before versioning, 2026-11-17).</summary>
        public int schemaVersion;
        public int coins = 300;
        public List<string> owned = new List<string>();
        public List<string> equipped = new List<string>();
        public List<Quest> quests = new List<Quest>();
        public List<string> keybinds = new List<string>();
        public int keybindVersion;
        public Settings settings = new Settings();
        public int wins, losses, matches;
        /// <summary>
        /// Player name chosen on first launch (3-16 letters, digits or _; no tag). Shown to opponents and pushed to
        /// Unity Authentication for friends. Read it through ChibiSocialRules.CleanSavedName (the file can be edited).
        /// </summary>
        public string playerName = "";
        /// <summary>AgeBand from the first-launch age question (0 = not asked).</summary>
        public int ageBand;
        /// <summary>Age the player gave (0 = not asked); written to the player age record (PlayerAgeLedger).</summary>
        public int age;
        /// <summary>This identity already has its row in the player age record (one row per new player).</summary>
        public bool ageRecorded;
        /// <summary>Signed-in account (Unity player id bound to the Google sign-in); "" = no account (guest).</summary>
        public string accountId = "";
        /// <summary>The "sign in to keep your name" step was answered (signed in or chose to play without an account).</summary>
        public bool accountAsked;
        /// <summary>Name and age group of every account used on this computer (+ "guest"); see PlayerIdentity.SwitchProfile.</summary>
        public List<AccountProfile> profiles = new List<AccountProfile>();
        /// <summary>
        /// UI language of this device (a Resources/Loc/languages.json code, see Loc); "" = not chosen yet, so the
        /// first-launch language window opens after the age question. Per device, never sent online.
        /// </summary>
        public string language = "";

        /// <summary>Load the profile from PlayerPrefs.</summary>
        public static SaveData Load() => Load(PlayerPrefsSaveStore.Instance);

        /// <summary>
        /// Load the profile from store: parse, <see cref="Migrate"/>, <see cref="Repair"/>. A profile that fails any of
        /// those (unreadable JSON, or a Repair that throws on an edited save) is copied to BackupKey and a fresh profile
        /// is returned instead (review 2026-11-16 item 23: Repair used to run outside the try).
        /// </summary>
        public static SaveData Load(ISaveStore store)
        {
            SaveData d = null;
            string json = "", error = null;
            try
            {
                json = store.Get(Key) ?? "";
                if (json.Length > 0)
                {
                    d = JsonUtility.FromJson<SaveData>(json);
                    if (d == null) error = "save parsed to null";
                    else { d.Migrate(); d.Repair(); }
                }
            }
            catch (Exception e) { d = null; error = e.Message; }
            if (error != null) BackupCorrupt(store, json, error);
            if (d == null)
            {
                d = new SaveData();
                d.Repair();
            }
            // Never stamp a save written by a newer build down (its Migrate steps would run again on that build).
            if (d.schemaVersion < SchemaVersion) d.schemaVersion = SchemaVersion;
            return d;
        }

        // Keeps the unreadable save so the fresh profile's next Save() cannot destroy it. There is one backup slot:
        // an identical string is not rewritten, a different corrupt save replaces it (newest wins).
        static void BackupCorrupt(ISaveStore store, string json, string error)
        {
            if (string.IsNullOrEmpty(json)) { Debug.LogWarning("ChibiClash: save reset (" + error + ")"); return; }
            try
            {
                if (store.Get(BackupKey) != json) { store.Set(BackupKey, json); store.Flush(); }
                Debug.LogWarning("ChibiClash: save unreadable (" + error + "); original kept under key '" + BackupKey + "', starting a fresh profile");
            }
            catch (Exception e) { Debug.LogWarning("ChibiClash: save unreadable (" + error + ") and backup to '" + BackupKey + "' failed (" + e.Message + ")"); }
        }

        /// <summary>Write the profile to PlayerPrefs.</summary>
        public void Save() => Save(PlayerPrefsSaveStore.Instance);

        /// <summary>Write the profile to store.</summary>
        /// <summary>
        /// DEV ONLY (-devName, ChibiDevFlags): the name that is on disk while <see cref="playerName"/> holds a run-only dev
        /// name. When set, Save writes this instead, so the dev name never reaches PlayerPrefs. Null = normal.
        /// </summary>
        [NonSerialized] public string diskPlayerName;

        public void Save(ISaveStore store)
        {
            if (diskPlayerName != null)
            {
                string live = playerName;
                playerName = diskPlayerName;
                try { store.Set(Key, JsonUtility.ToJson(this)); }
                finally { playerName = live; }
                store.Flush();
                return;
            }
            store.Set(Key, JsonUtility.ToJson(this));
            store.Flush();
        }

        /// <summary>
        /// Upgrade an older layout to <see cref="SchemaVersion"/>. Version 1 (2026-11-17) only adds the field itself, so a
        /// version-0 save needs no change; add a step here for each later layout change, oldest first.
        /// </summary>
        /// <example><code>if (schemaVersion &lt; 2) { /* move field X to Y */ }</code></example>
        public void Migrate()
        {
            // 0 -> 1: nothing to move.
        }

        /// <summary>
        /// RESET PROFILE (review 2026-11-16 item 22, decision: progress only). A fresh profile that keeps this one's
        /// identity (name, age, sign-in, the age-record flag, per-account profiles) so there is no second onboarding and
        /// no second row in the age record, and drops coins, cosmetics, quests, stats, settings and keys. It is written
        /// to <paramref name="store"/> (PlayerPrefs in the game) and returned.
        /// </summary>
        /// <example><code>Save = Save.ResetProgress(PlayerPrefsSaveStore.Instance); Bindings.Data = Save;</code></example>
        public SaveData ResetProgress(ISaveStore store)
        {
            var d = new SaveData
            {
                playerName = playerName ?? "",
                ageBand = ageBand,
                age = age,
                ageRecorded = ageRecorded,
                accountId = accountId ?? "",
                accountAsked = accountAsked,
                profiles = profiles != null ? new List<AccountProfile>(profiles) : new List<AccountProfile>(),
                language = language ?? "",
            };
            // Privacy: a player who turned the camera off keeps it off (a fresh Settings would switch it on by default).
            if (settings != null) { d.settings.useWebcam = settings.useWebcam; d.settings.camInit = settings.camInit; }
            d.Repair();
            d.schemaVersion = SchemaVersion;
            d.Save(store);
            return d;
        }

        /// <summary>
        /// Fill missing parts, clamp edited values, restore built-ins. Safe on any deserialized profile (null lists,
        /// null entries, out-of-range coins).
        /// </summary>
        public void Repair()
        {
            if (settings == null) settings = new Settings();
            if (language == null) language = "";
            if (keybinds == null) keybinds = new List<string>();
            if (owned == null) owned = new List<string>();
            if (equipped == null) equipped = new List<string>();
            if (quests == null) quests = new List<Quest>();
            if (profiles == null) profiles = new List<AccountProfile>();
            quests.RemoveAll(q => q == null);
            coins = Mathf.Clamp(coins, 0, MaxCoins);
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
            var q = quests.FirstOrDefault(x => x != null && x.id == id);
            if (q == null) { quests.Add(new Quest { id = id, desc = desc, target = target, reward = reward }); return; }
            // A saved target <= 0 would hang Progress: restore the built-in target. Text and valid rewards are kept.
            if (q.target <= 0) { q.target = target; q.progress = Mathf.Clamp(q.progress, 0, target - 1); }
            if (q.reward < 0) q.reward = reward;
        }

        /// <summary>The skin equipped on <paramref name="c"/>; the class default when the entry is missing, unknown or not owned.</summary>
        public Skin Equipped(FighterClass c)
        {
            int i = (int)c;
            string id = equipped != null && i >= 0 && i < equipped.Count ? equipped[i] : null;
            var s = id != null && owned != null && owned.Contains(id) ? Skin.Find(id) : null;
            return s ?? Skin.Default(c);
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
            var q = quests?.FirstOrDefault(x => x != null && x.id == id);
            if (q == null || amount <= 0 || q.target <= 0) return 0;   // target <= 0 would never leave the loop below
            q.progress += amount;
            int earned = 0;
            while (q.progress >= q.target)
            {
                q.progress -= q.target;
                q.completions++;
                earned += q.reward;
                log?.Add(Loc.T("rewards.quest", q.DescText, q.reward));
            }
            coins = (int)Math.Min(MaxCoins, (long)coins + earned);
            return earned;
        }

        public struct PullResult { public Skin skin; public bool duplicate; }

        /// <summary>Cosmetics-only gacha. Rates: Common 60%, Rare 28%, Epic 10%, Legendary 2%.</summary>
        public List<PullResult> Pull(int count, FighterClass? banner, ISaveStore store = null)
        {
            var results = new List<PullResult>();
            if (count <= 0) return results;
            int cost = count >= 10 ? TenPullCost : PullCost * count;
            if (coins < cost) return results;
            // A banner with no pullable skins (unknown class) would index an empty pool: no pull, no charge.
            if (!Skin.All.Any(s => !s.id.EndsWith("_default") && (banner == null || s.cls == banner))) return results;
            coins -= cost;
            for (int i = 0; i < count; i++)
            {
                float r = UnityEngine.Random.value;
                Rarity rarity = r < 0.02f ? Rarity.Legendary : r < 0.12f ? Rarity.Epic : r < 0.40f ? Rarity.Rare : Rarity.Common;
                var pool = Skin.All.Where(s => s.rarity == rarity && !s.id.EndsWith("_default") && (banner == null || s.cls == banner)).ToList();
                if (pool.Count == 0)
                    pool = Skin.All.Where(s => !s.id.EndsWith("_default") && (banner == null || s.cls == banner)).ToList();
                var skin = pool[UnityEngine.Random.Range(0, pool.Count)];
                bool dup = owned.Contains(skin.id);
                if (dup) coins = Math.Min(MaxCoins, coins + 20);
                else owned.Add(skin.id);
                results.Add(new PullResult { skin = skin, duplicate = dup });
            }
            Save(store ?? PlayerPrefsSaveStore.Instance);
            return results;
        }
    }
}
