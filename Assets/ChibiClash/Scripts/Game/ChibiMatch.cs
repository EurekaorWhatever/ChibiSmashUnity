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

        readonly ClassDef[] _def = new ClassDef[ChibiSimState.MaxFighters];
        readonly Skin[] _skin = new Skin[ChibiSimState.MaxFighters];
        readonly WeaponDef[] _weapon = new WeaponDef[ChibiSimState.MaxFighters];
        readonly ChibiPlayerInputSource[] _human = new ChibiPlayerInputSource[ChibiSimState.MaxFighters];
        readonly bool[] _cpu = new bool[ChibiSimState.MaxFighters];
        readonly string[] _names = { "", "", "", "" };
        ChibiLocalLoop _local;
        bool _awarded;

        /// <summary>Live simulation state (may be a predicted frame online). Read only.</summary>
        public ChibiSimState State => Feed?.State;
        /// <summary>
        /// The match ended. Offline: KO or time-out. Online: only once the result is FINAL (a confirmed KO / time-out,
        /// or the opponent leaving / dropping, which wins the match for the one who stays, decision Q4).
        /// </summary>
        public bool Finished => Online ? _runner != null && _runner.Match != null && _runner.Match.HasResult
                                       : State != null && State.MatchOver;
        /// <summary>Offline pause (Esc); always false online.</summary>
        public bool Paused => _local != null && _local.Paused;
        /// <summary>Winner 0 / 1, or -1 for a draw / no contest (valid once finished).</summary>
        public int Winner => Online ? OnlineResult.Winner : State != null ? State.Winner : -1;
        /// <summary>Stage display name.</summary>
        public string StageName => View != null && View.Stage != null ? View.Stage.name : "";

        /// <summary>Fighters in this match: 2 for 1v1, up to 4 (four-player Phase 2b).</summary>
        public int PlayerCount => State != null ? ChibiSimEvents.Fighters(State) : 2;
        /// <summary>Team mode (2v2): the HUD uses team colours and the results show the winning team.</summary>
        public bool TeamMode => State != null && State.Mode == ChibiMatchMode.Team;
        /// <summary>Team (0 / 1) of fighter <paramref name="i"/> in Team mode, else -1.</summary>
        public int TeamOf(int i) => TeamMode ? (State.TeamOf >> i) & 1 : -1;

        /// <summary>Static data of fighter <paramref name="i"/>.</summary>
        public ClassDef Def(int i) => _def[i];
        /// <summary>Equipped skin of fighter <paramref name="i"/>.</summary>
        public Skin SkinOf(int i) => _skin[i];
        /// <summary>Weapon variant of fighter <paramref name="i"/>.</summary>
        public WeaponDef WeaponOf(int i) => _weapon[i];
        /// <summary>Fighter <paramref name="i"/> is played by the CPU.</summary>
        public bool IsCpu(int i) => _cpu[i];
        /// <summary>
        /// Online: the chosen player name of fighter <paramref name="i"/> ("Kira#4821"), already safe to draw; "" offline
        /// or when that player sent none (older build / no name).
        /// </summary>
        public string NameOf(int i) => _names[i];

        /// <summary>Room fight: draw every other seat by its lobby name (SEC-004). Ignored offline.</summary>
        public void SetRoomNames(string[] seatNames)
        {
            // SEC-004: a room fight shows each seat's lobby name (already sanitised by ChibiRoomRules.Snapshot through
            // ChibiSocialRules.PeerDisplayName). Seat = sim slot (SEC-003 / L2 binds every connection to its seat).
            if (!Online || seatNames == null) return;
            for (int i = 0; i < PlayerCount && i < seatNames.Length; i++)
                if (i != LocalPlayer && !string.IsNullOrEmpty(seatNames[i])) _names[i] = seatNames[i];
        }

        /// <summary>
        /// Show the opponent by the name OUR friends list gives them (an invite match): verified, so shown in full with
        /// its #tag. 1v1 only. Ignored offline.
        /// </summary>
        public void SetVerifiedOpponentName(string friendName)
        {
            if (!Online || string.IsNullOrEmpty(friendName) || PlayerCount > 2) return;   // one opponent only (1v1 invite)
            _names[1 - LocalPlayer] = ChibiSocialRules.PeerDisplayName(friendName, verified: true);
        }
        /// <summary>Gesture tracker of a local human fighter (camera / simulated poses), else null.</summary>
        public GestureTracker Tracker(int i) => _human[i] != null ? _human[i].Tracker : null;
        /// <summary>World position of fighter <paramref name="i"/>'s body centre.</summary>
        public Vector2 Center(int i) => View != null ? View.FighterCenter(i) : Vector2.zero;

        /// <summary>
        /// Create and start an offline match (local versus, vs CPU or training) from the menu settings. The caller tears
        /// the previous match down and releases unused sheets first (<see cref="ReleaseUnusedSheets"/>); when the sheets
        /// were already loaded behind the wipe (<see cref="MatchAssetLoader"/>) the preload here is a no-op.
        /// </summary>
        /// <example><code>var m = ChibiMatch.Create(gameManager, pendingConfig);</code></example>
        public static ChibiMatch Create(GameManager gm, MatchConfig cfg)
        {
            PreloadAssets(cfg.cls[0], cfg.cls[1]);   // perf: whatever the wipe did not load yet, still behind the wipe
            for (int i = 2; i < Mathf.Clamp(cfg.players, 2, ChibiSimState.MaxFighters); i++)
                SpriteAnimLibrary.Preload(CharacterRoster.Of(cfg.cls[i]).Id);   // fighters 3-4 (cheap when already loaded)
            var go = new GameObject("Match");
            var m = go.AddComponent<ChibiMatch>();
            m.GM = gm;
            m.Config = cfg;
            m.SetupOffline();
            return m;
        }

        /// <summary>
        /// Loads, slices and GPU-warms both fighters' fight sheets (the clips.txt sheet clips only, never the legacy strip
        /// cutouts) and builds the shared procedural FX textures now, so none of it runs on a fight frame. Synchronous:
        /// call it where the match is built (behind the wipe, before the READY intro); <see cref="MatchAssetLoader"/> does
        /// the same work spread over frames. Cheap for a character already loaded. Returns the number of clips available
        /// for the two fighters.
        /// </summary>
        /// <example><code>ChibiMatch.PreloadAssets(FighterClass.Melee, FighterClass.Heavy);</code></example>
        public static int PreloadAssets(FighterClass c0, FighterClass c1)
        {
            // The ids FighterVisual.Build resolves by default (CharacterRoster.Of(def.type).Id).
            string a = CharacterRoster.Of(c0).Id, b = CharacterRoster.Of(c1).Id;
            int n = SpriteAnimLibrary.Preload(a);
            if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) n += SpriteAnimLibrary.Preload(b);
            WarmSharedFx();
            return n;
        }

        /// <summary>Builds the shared procedural FX textures every match uses (cached statics: cheap after the first match).</summary>
        public static void WarmSharedFx()
        {
            ChibiLaunchFx.WarmShared();                                     // launch trails / KO blasts
            _ = ChibiMoveVfx.Crescent;                                      // moveset slash crescents
            _ = ChibiFxTextures.Ray; _ = ChibiFxTextures.Glow;              // hit sparks
            _ = ChibiFxTextures.Ring; _ = ChibiFxTextures.Line;
        }

        /// <summary>
        /// Drops the sheets of every character not in a <paramref name="c0"/> vs <paramref name="c1"/> match (each ~105-120
        /// MB of GPU memory with its strips; after all five were played the cache held ~550 MB). Call it only after the
        /// previous match's visuals are destroyed and before the new pair is preloaded, so at most the two fighters of the
        /// next match are ever resident and the sprite teardown never lands on a fight frame. Returns how many were released.
        /// </summary>
        public static int ReleaseUnusedSheets(FighterClass c0, FighterClass c1) =>
            SpriteAnimLibrary.ReleaseAllExcept(CharacterRoster.Of(c0).Id, CharacterRoster.Of(c1).Id);

        void ResolveCosmetics(SaveData save)
        {
            int n = Mathf.Clamp(Config.players, 2, ChibiSimState.MaxFighters);
            for (int i = 0; i < n; i++)
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
                HazardsOff = !Config.hazards,   // ST-5: stage events toggle (moving platforms move either way)
            };
            int n = Mathf.Clamp(Config.players, 2, ChibiSimState.MaxFighters);
            if (n > 2)
            {
                // Four players (Phase 2b): fighters 3-4 and the mode. A 1v1 leaves every new field at its default, so its
                // config (and hash) is exactly the old one.
                SimConfig = WithPlayers(SimConfig, Config, n);
            }
            var src = new IChibiInputSource[n];
            for (int i = 0; i < n; i++)
            {
                _cpu[i] = Config.cpu[i];
                if (_cpu[i]) src[i] = new ChibiCpuInputSource(i, Config.training ? 0 : Config.difficulty, SimConfig.Seed);
                else src[i] = _human[i] = ChibiPlayerInputSource.ForPlayer(i);
            }
            // The profile owner (P1) is shown by their chosen name; P2 / CPU keep their labels.
            if (!_cpu[0] && GM != null && GM.Save != null) _names[0] = ChibiSocialRules.CleanSavedName(GM.Save.playerName);
            _local = gameObject.AddComponent<ChibiLocalLoop>();
            if (n == 2) _local.Begin(SimConfig, src[0], src[1], recordReplay: true);
            else _local.Begin(SimConfig, src, recordReplay: false);   // the replay format holds 2 streams (Phase 3)
            Feed = _local;
            LocalPlayer = 0;
            BuildView();
        }

        /// <summary>The sim config with fighters 2..n-1, the player count and the mode from <paramref name="mc"/>.</summary>
        static ChibiMatchConfig WithPlayers(ChibiMatchConfig c, MatchConfig mc, int n)
        {
            c.PlayerCount = n;
            c.Class2 = mc.cls[2]; c.Passive2 = mc.passive[2];
            if (n > 3) { c.Class3 = mc.cls[3]; c.Passive3 = mc.passive[3]; }
            c.Mode = mc.team ? ChibiMatchMode.Team : ChibiMatchMode.FreeForAll;
            c.TeamOf = mc.team ? mc.teamOf & ((1 << n) - 1) : 0;
            return c;
        }

        /// <summary>
        /// Wrap a running online match (its handshake agreed, so the state exists). The local fighter keeps the profile's
        /// equipped skin and weapon; the opponent's cosmetics are not exchanged and show as class defaults.
        /// </summary>
        public static ChibiMatch CreateOnline(GameManager gm, ChibiOnlineMatchRunner runner, ChibiPlayerInputSource localInput,
                                              int localWeapon)
        {
            var om = runner.Match;
            var cfg = om.Config;
            int n = cfg.Players;   // 2-4 (protocol 6 star)
            var mc = new MatchConfig { stage = cfg.Stage, training = false, hazards = !cfg.HazardsOff };   // F3: the agreed switch
            mc.players = n;
            mc.team = cfg.Mode == ChibiMatchMode.Team;
            mc.teamOf = cfg.TeamOf;
            for (int i = 0; i < n; i++) { mc.cls[i] = cfg.ClassOf(i); mc.passive[i] = cfg.PassiveOf(i); mc.cpu[i] = false; }
            int me = om.LocalIndex;
            mc.weapon[me] = localWeapon;

            var m = runner.gameObject.AddComponent<ChibiMatch>();
            m.GM = gm;
            m.Config = mc;
            m.Online = true;
            m._runner = runner;
            m.LocalPlayer = me;
            m.SimConfig = cfg;
            m.Feed = runner;
            m._human[me] = localInput;
            // Names travel in the handshake (cosmetic). Sanitised again here: never trust the wire for anything drawn.
            string local = om.PlayerName(me);
            for (int i = 0; i < n; i++)
                m._names[i] = i == me ? (local.Length > 0 ? ChibiSocialRules.SafeDisplayName(local) : "")
                                      : ChibiSocialRules.PeerDisplayName(om.PlayerName(i), verified: false);   // not bound to an account
            for (int i = 0; i < n; i++)
            {
                m._def[i] = ClassDef.Get(mc.cls[i]);
                m._skin[i] = i == me && gm != null && gm.Save != null ? gm.Save.Equipped(m._def[i].type) : Skin.Default(m._def[i].type);
                m._weapon[i] = WeaponDef.Get(m._def[i].type, i == me ? localWeapon : 0);
            }
            // Both fighters' sheets were preloaded while handshaking when they were known (GameManager.Online: our own at
            // connect, the opponent's from the room or the joiner's Hello). Whatever is left is NOT loaded here in one
            // blocking call: the view only needs Idle_Sheet for the READY intro (loaded on demand), and GameManager
            // finishes the rest in small per-frame slices during the intro (MatchAssetLoader), so the rollback clock never
            // runs through a long stall.
            WarmSharedFx();
            m.BuildView();
            return m;
        }

        ChibiOnlineMatchRunner _runner;

        /// <summary>The online match's final result (online only; Outcome None until decided).</summary>
        public ChibiOnlineResult OnlineResult => _runner != null && _runner.Match != null ? _runner.Match.Result : default;

        /// <summary>One line for the results screen explaining an online ending that was not a KO / time-out.</summary>
        public string OnlineEndText
        {
            get
            {
                if (!Online) return "";
                switch (OnlineResult.Cause)
                {
                    case ChibiEndCause.OpponentLeft: return Loc.T("end.opponent_left");
                    case ChibiEndCause.OpponentTimedOut: return Loc.T("end.opponent_timeout");
                    case ChibiEndCause.ConnectionLost: return Loc.T("end.connection_lost");
                    case ChibiEndCause.LocalForfeit: return Loc.T("end.you_left");
                    case ChibiEndCause.Desync: return Loc.T("end.desync");
                    case ChibiEndCause.UnconfirmedEnd: return Loc.T("end.unconfirmed");
                    case ChibiEndCause.HandshakeFailed: return Loc.T("end.handshake_failed");
                    case ChibiEndCause.PeerNeverStarted: return Loc.T("end.peer_never_started");
                    case ChibiEndCause.OpponentStalled: return Loc.T("end.too_slow");
                    case ChibiEndCause.HostLeft: return Loc.T("end.host_left");
                    case ChibiEndCause.Dropped: return Loc.T("end.dropped");
                    default: return "";
                }
            }
        }

        void BuildView()
        {
            View = gameObject.AddComponent<ChibiSimView>();
            View.Begin(Feed, SimConfig, _skin, _weapon);
        }

        /// <summary>
        /// Pause or resume (offline only; the HUD's Resume button). On resume the human inputs stay muted for
        /// <see cref="MenuInputGraceSeconds"/>, so the button that resumed (B is also Special, A is also Jump) does not
        /// fire in the match.
        /// </summary>
        public void SetPaused(bool paused)
        {
            if (_local == null) return;
            if (!paused && _local.Paused) MuteBriefly();
            _local.SetPaused(paused);
        }

        /// <summary>Human inputs stay neutral this long after a menu overlay closes (its keys are also fighter keys).</summary>
        public const float MenuInputGraceSeconds = 0.15f;

        bool _leavePrompt;
        float _muteUntil = -1f;

        /// <summary>
        /// Online: the "LEAVE MATCH?" confirm is showing (Esc / Start). The match keeps running underneath; online never
        /// pauses (review 2026-11-16 item 21). While it shows, and briefly after it closes, the local fighter gets
        /// neutral input so navigating the prompt never moves or attacks.
        /// </summary>
        public bool LeavePrompt
        {
            get => _leavePrompt;
            set
            {
                if (_leavePrompt && !value) MuteBriefly();
                _leavePrompt = value;
                ApplyMute();
            }
        }

        void MuteBriefly()
        {
            _muteUntil = Time.unscaledTime + MenuInputGraceSeconds;
            ApplyMute();
        }

        void ApplyMute()
        {
            bool mute = _leavePrompt || Time.unscaledTime < _muteUntil;
            for (int i = 0; i < _human.Length; i++)
                if (_human[i] != null) _human[i].Muted = mute;
        }

        /// <summary>Online: leave now. It tells the peer and counts as a loss (decision Q4); no-op offline.</summary>
        public void Forfeit()
        {
            LeavePrompt = false;
            if (_runner != null && _runner.Match != null) _runner.Match.Forfeit();
        }

        /// <summary>Save the offline replay to <paramref name="path"/> (no-op online or when not recorded).</summary>
        public void SaveReplay(string path) { if (_local != null && _local.Replay != null) _local.Replay.Save(path); }

        void Update()
        {
            if (_muteUntil >= 0f && Time.unscaledTime >= _muteUntil) { _muteUntil = -1f; ApplyMute(); }
            var s = State;
            if (s == null) return;
            // Posture is sampled for local human fighters while running, scaled dt (MC:150); never part of the sim.
            if (s.Running && !Paused)
                for (int i = 0; i < _human.Length; i++) Tracker(i)?.SamplePosture(Time.deltaTime);

            if (Finished && !_awarded)
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
            if (f.AuraTicks > 0) into.Add(Loc.T("status.aura", Mathf.CeilToInt(f.AuraTicks / tr)));
            if (f.RingTicks > 0) into.Add(Loc.T("status.fire_ring", Mathf.CeilToInt(f.RingTicks / tr)));
            if (f.TailwindTicks > 0) into.Add(Loc.T("status.tailwind"));
            int guardMax = ChibiCombatStats.GuardMaxU(f.Stats.Defense);
            if (f.GuardBrokenTicks > 0) into.Add(Loc.T("status.guard_broken", (f.GuardBrokenTicks / tr).ToString("0.0")));
            else if (f.GuardU < guardMax - ChibiCombatStats.HpScale / 2) into.Add(Loc.T("status.guard", Mathf.RoundToInt(100f * f.GuardU / guardMax)));
            if (def.rangedNormals && f.SetCd > 0)
                into.Add(Loc.T(def.type == FighterClass.Ranged ? "status.reloading" : "status.recharging", (f.SetCd / tr).ToString("0.0")));
            if (f.SlowTicks > 0) into.Add(Loc.T("status.hexed"));
            if (f.ArmorBreakTicks > 0) into.Add(Loc.T("status.armor_break"));
            return into;
        }

        /// <summary>Prompt text under the gesture panel (Fighter.PromptMessage, F:402, F:421, F:430).</summary>
        public string PromptMessage(int i)
        {
            var f = State.F[i];
            if (f.UltWindowTicks > 0) return Loc.T("hud.perform", ClassDef.GestureName(_def[i].ultGesture));
            if (f.UltPrompt == (int)ChibiUltPrompt.Detected) return _def[i].ultName + "!";
            if (f.UltPrompt == (int)ChibiUltPrompt.Failed) return Loc.T("hud.times_up");
            return "";
        }

        /// <summary>The local player won (1v1: Winner; more fighters: in WinnerMask, so a 2v2 team wins together).</summary>
        /// Online: the match's own outcome (protocol 6: 3-4 players win by WinnerMask, a dropped guest loses).
        bool WonLocal => Online ? OnlineResult.Outcome == ChibiMatchOutcome.Win
                       : PlayerCount <= 2 ? Winner == LocalPlayer
                       : State != null && (State.WinnerMask & (1 << LocalPlayer)) != 0;
        /// <summary>The local player lost (1v1: the other one won; more fighters: someone else won, not a draw).</summary>
        bool LostLocal => Online ? OnlineResult.Outcome == ChibiMatchOutcome.Loss
                        : PlayerCount <= 2 ? Winner == 1 - LocalPlayer
                        : State != null && State.WinnerMask != 0 && !WonLocal;

        void AwardRewards()
        {
            if (GM == null || GM.Save == null || Config.training) return;
            // Nobody human played the local slot (all-CPU / spectator match): no rewards and no save, so automation and
            // CPU-only matches never write to the player's profile (found 2026-11-24: an all-CPU run credited coins).
            if (!Online && !MatchRewardRules.LocalSlotIsHuman(Config.cpu, LocalPlayer))
            {
                RewardLog.Add("Spectator match: no rewards");
                return;
            }
            if (Online)
            {
                // No contest (desync / unconfirmed end) and aborted matches give nothing to anybody.
                var o = OnlineResult.Outcome;
                if (o != ChibiMatchOutcome.Win && o != ChibiMatchOutcome.Loss && o != ChibiMatchOutcome.Draw)
                {
                    RewardLog.Add(Loc.T("rewards.no_contest"));
                    return;
                }
            }
            var me = State.F[LocalPlayer];
            var tracker = Tracker(LocalPlayer);
            var stats = new MatchStatsForRewards
            {
                Won = WonLocal,
                Lost = LostLocal,
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
