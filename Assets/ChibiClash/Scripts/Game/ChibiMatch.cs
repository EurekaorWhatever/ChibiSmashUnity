using System;
using System.Collections.Generic;
using ChibiClash.Netcode;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// One match of the shipped game, run by the deterministic simulation (decision Q1: one version of the game). It replaces
    /// MatchController in the menu flow for all three modes: local versus / vs CPU / training run a <see cref="ChibiLocalLoop"/>
    /// (CPU slots are <see cref="ChibiCpuInputSource"/>s), online runs a <see cref="ChibiOnlineMatchRunner"/>. Drawing is
    /// <see cref="ChibiSimView"/> + <see cref="Hud"/>; this component only wires them, samples posture (local, never in the
    /// sim) and awards rewards once when the match ends (MC:195-225 via <see cref="MatchRewards"/>).
    /// </summary>
    public sealed class ChibiMatch : MonoBehaviour
    {
        /// <summary>The game manager that owns the save profile.</summary>
        public GameManager GM { get; private set; }
        /// <summary>The menu's match settings (classes, passives, weapons, CPU slots, stage, training).</summary>
        public MatchConfig Config { get; private set; }
        /// <summary>The simulation config both peers / replays use.</summary>
        public ChibiMatchConfig SimConfig { get; private set; }
        /// <summary>What drives the simulation (offline loop or online runner).</summary>
        public IChibiSimFeed Feed { get; private set; }
        /// <summary>The presenter (stage, fighters, effects, camera).</summary>
        public ChibiSimView View { get; private set; }
        /// <summary>Index of the player whose profile earns the rewards (P1 offline; the local peer online).</summary>
        public int LocalPlayer { get; private set; }
        /// <summary>True for an online match (no pause, opponent is remote).</summary>
        public bool Online { get; private set; }

        /// <summary>Lines of the reward breakdown, filled when the match ends.</summary>
        public readonly List<string> RewardLog = new List<string>();
        /// <summary>Coins the local profile earned this match.</summary>
        public int CoinsEarned { get; private set; }
        /// <summary>Unscaled time the end of the match was first seen.</summary>
        public float FinishedTime { get; private set; }

        readonly ClassDef[] _def = new ClassDef[2];
        readonly Skin[] _skin = new Skin[2];
        readonly WeaponDef[] _weapon = new WeaponDef[2];
        readonly ChibiPlayerInputSource[] _human = new ChibiPlayerInputSource[2];
        readonly bool[] _cpu = new bool[2];
        ChibiLocalLoop _local;
        bool _awarded;

        /// <summary>Live simulation state (may be a predicted frame online). Read only.</summary>
        public ChibiSimState State => Feed?.State;
        /// <summary>The match ended (KO, time-out, or a disconnect online).</summary>
        public bool Finished => State != null && State.MatchOver;
        /// <summary>Offline pause (Esc); always false online.</summary>
        public bool Paused => _local != null && _local.Paused;
        /// <summary>Winner 0 / 1, or -1 for a draw (valid once finished).</summary>
        public int Winner => State != null ? State.Winner : -1;
        /// <summary>Stage display name.</summary>
        public string StageName => View != null && View.Stage != null ? View.Stage.name : "";

        /// <summary>Static data of fighter <paramref name="i"/>.</summary>
        public ClassDef Def(int i) => _def[i];
        /// <summary>Equipped skin of fighter <paramref name="i"/>.</summary>
        public Skin SkinOf(int i) => _skin[i];
        /// <summary>Weapon variant of fighter <paramref name="i"/>.</summary>
        public WeaponDef WeaponOf(int i) => _weapon[i];
        /// <summary>Fighter <paramref name="i"/> is played by the CPU.</summary>
        public bool IsCpu(int i) => _cpu[i];
        /// <summary>Gesture tracker of a local human fighter (camera / simulated poses), else null.</summary>
        public GestureTracker Tracker(int i) => _human[i] != null ? _human[i].Tracker : null;
        /// <summary>World position of fighter <paramref name="i"/>'s body centre.</summary>
        public Vector2 Center(int i) => View != null ? View.FighterCenter(i) : Vector2.zero;

        /// <summary>Create and start an offline match (local versus, vs CPU or training) from the menu settings.</summary>
        /// <example><code>var m = ChibiMatch.Create(gameManager, pendingConfig);</code></example>
        public static ChibiMatch Create(GameManager gm, MatchConfig cfg)
        {
            var go = new GameObject("Match");
            var m = go.AddComponent<ChibiMatch>();
            m.GM = gm;
            m.Config = cfg;
            m.SetupOffline();
            return m;
        }

        void ResolveCosmetics(SaveData save)
        {
            for (int i = 0; i < 2; i++)
            {
                _def[i] = ClassDef.Get(Config.cls[i]);
                _skin[i] = save != null ? save.Equipped(_def[i].type) : Skin.Default(_def[i].type);
                _weapon[i] = WeaponDef.Get(_def[i].type, Config.weapon[i]);
            }
        }

        void SetupOffline()
        {
            ResolveCosmetics(GM != null ? GM.Save : null);
            SimConfig = new ChibiMatchConfig
            {
                Seed = (uint)Environment.TickCount ^ 0x5EEDC0DEu,   // offline: any seed; the match is not shared
                Class0 = Config.cls[0], Class1 = Config.cls[1], Passive0 = Config.passive[0], Passive1 = Config.passive[1],
                Stage = Config.stage, Intro = true, Training = Config.training,
            };
            var src = new IChibiInputSource[2];
            for (int i = 0; i < 2; i++)
            {
                _cpu[i] = Config.cpu[i];
                if (_cpu[i]) src[i] = new ChibiCpuInputSource(i, Config.training ? 0 : Config.difficulty, SimConfig.Seed);
                else src[i] = _human[i] = ChibiPlayerInputSource.ForPlayer(i);
            }
            _local = gameObject.AddComponent<ChibiLocalLoop>();
            _local.Begin(SimConfig, src[0], src[1], recordReplay: true);
            Feed = _local;
            LocalPlayer = 0;
            BuildView();
        }

        void BuildView()
        {
            View = gameObject.AddComponent<ChibiSimView>();
            View.Begin(Feed, SimConfig, _skin, _weapon);
        }

        /// <summary>Pause or resume (offline only; the HUD's Resume button).</summary>
        public void SetPaused(bool paused) { if (_local != null) _local.SetPaused(paused); }

        /// <summary>Save the offline replay to <paramref name="path"/> (no-op online or when not recorded).</summary>
        public void SaveReplay(string path) { if (_local != null && _local.Replay != null) _local.Replay.Save(path); }

        void Update()
        {
            var s = State;
            if (s == null) return;
            // Posture is sampled for local human fighters while running, scaled dt (MC:150); never part of the sim.
            if (s.Running && !Paused)
                for (int i = 0; i < 2; i++) Tracker(i)?.SamplePosture(Time.deltaTime);

            if (s.MatchOver && !_awarded)
            {
                _awarded = true;
                FinishedTime = Time.unscaledTime;
                AwardRewards();
            }
        }

        /// <summary>The local player's statuses for the HUD (Fighter.Statuses, F:151-165), from sim state.</summary>
        public List<string> Statuses(int i, List<string> into)
        {
            into.Clear();
            var f = State.F[i];
            var def = _def[i];
            const float tr = ChibiSimConst.TickRate;
            if (f.AuraTicks > 0) into.Add("AURA " + Mathf.CeilToInt(f.AuraTicks / tr));
            if (f.RingTicks > 0) into.Add("FIRE RING " + Mathf.CeilToInt(f.RingTicks / tr));
            if (f.TailwindTicks > 0) into.Add("TAILWIND");
            int guardMax = ChibiCombatStats.GuardMaxU(f.Stats.Defense);
            if (f.GuardBrokenTicks > 0) into.Add("GUARD BROKEN " + (f.GuardBrokenTicks / tr).ToString("0.0") + "s");
            else if (f.GuardU < guardMax - ChibiCombatStats.HpScale / 2) into.Add("GUARD " + Mathf.RoundToInt(100f * f.GuardU / guardMax) + "%");
            if (def.rangedNormals && f.SetCd > 0)
                into.Add((def.type == FighterClass.Ranged ? "RELOADING " : "RECHARGING ") + (f.SetCd / tr).ToString("0.0") + "s");
            if (f.SlowTicks > 0) into.Add("HEXED");
            if (f.ArmorBreakTicks > 0) into.Add("ARMOR -15%");
            return into;
        }

        /// <summary>Prompt text under the gesture panel (Fighter.PromptMessage, F:402, F:421, F:430).</summary>
        public string PromptMessage(int i)
        {
            var f = State.F[i];
            if (f.UltWindowTicks > 0) return "Perform: " + ClassDef.GestureName(_def[i].ultGesture);
            if (f.UltPrompt == (int)ChibiUltPrompt.Detected) return _def[i].ultName + "!";
            if (f.UltPrompt == (int)ChibiUltPrompt.Failed) return "Time's up!";
            return "";
        }

        void AwardRewards()
        {
            if (GM == null || GM.Save == null || Config.training) return;
            var me = State.F[LocalPlayer];
            var tracker = Tracker(LocalPlayer);
            var stats = new MatchStatsForRewards
            {
                Won = Winner == LocalPlayer,
                Lost = Winner == 1 - LocalPlayer,
                Pve = Config.pve && !Online,
                Training = Config.training,
                CombosLanded = me.CombosLanded, UltsUsed = me.UltsUsed, Parries = me.Parries,
                PostureAverage = tracker != null ? tracker.PostureAverage : 1f,
            };
            CoinsEarned = MatchRewards.Award(GM.Save, stats, RewardLog);
            GM.Save.Save();
        }

        void OnDestroy()
        {
            Time.timeScale = 1f;
            Fx.Popups.Clear();
        }
    }
}
