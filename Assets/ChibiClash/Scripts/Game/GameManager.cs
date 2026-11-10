using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChibiClash
{
    /// <summary>
    /// Entry point: owns the save profile, the loading screen, the menu flow (GDD 5) and the active match. Every mode runs
    /// on the deterministic simulation through <see cref="ChibiMatch"/>; the menus only READ it.
    /// Presentation is the copy-ghibli-smash fork's arcade UI (decision UI-1) with our five characters:
    ///   GameManager.cs         flow, pages, input routing, match start / end, loading steps, pre-match check
    ///   GameManager.Menus.cs   loading, home, nav bar, Play / Online hub / Arcade / Training / Collection / Settings
    ///   GameManager.Select.cs  character select (roster grid, side panels, loadout) and the stage carousel
    ///   GameManager.Online.cs  onboarding (age, name, account), online lobby, friends, rooms, invites
    /// </summary>
    public partial class GameManager : MonoBehaviour
    {
        enum Page { Loading, Home, Play, Online, Training, Arcade, Collection, Settings, Select, PreCheck, Match, Lobby, Rooms, Room }

        public SaveData Save { get; private set; }

        Page page = Page.Loading;
        float pageSince;
        ChibiMatch match;   // every mode runs on the deterministic sim (port step 18)
        MatchConfig pending = new MatchConfig();

        HumanInput menuInput;
        GestureTracker menuTracker;
        AutoPoseSource menuPose;

        // key rebinding (Settings)
        bool listening;
        float listenSince;
        int listenPlayer;
        BindAction listenAction;

        float preCheckT;
        bool lightOk, frameOk;
        string lightMsg = "", frameMsg = "";

        FighterClass armoryClass;
        bool bannerAll = true;
        List<SaveData.PullResult> lastPull = new List<SaveData.PullResult>();
        string toast;
        float toastUntil;

        bool exitPrompt;
        bool resultsSeen;

        // screen wipe transition
        const float WipeTime = 0.6f;
        float wipeStart = -1f;
        Action wipeAction;
        bool wipeSwapped;
        string wipeCaption;

        // arcade ladder: all five characters, one each, in a random order
        const int ArcadeRounds = 5;
        bool arcade;
        int arcadeRound;
        readonly FighterClass[] arcadeLadder = new FighterClass[ArcadeRounds];

        static readonly string[] Difficulty = { "Dummy", "Easy", "Normal", "Hard" };
        static readonly string[] DifficultyUpper = { "DUMMY", "EASY", "NORMAL", "HARD" };

        static float W => Gui.W;
        static float H => Gui.H;
        float Since => Time.unscaledTime - pageSince;

        void Awake()
        {
            Save = SaveData.Load();
            Bindings.Data = Save;
            Application.targetFrameRate = 120;
            menuInput = new HumanInput(0);
            var sim = new SimulatedPoseSource("P1 simulated", () => menuInput.SimulatedGesture, () => menuInput.Slouching);
            menuPose = new AutoPoseSource(0, sim, () => Save.settings.useWebcam);
            menuTracker = new GestureTracker(menuPose);
            rooms = gameObject.AddComponent<Netcode.NetworkRoomManager>();
            rooms.OnConnected += OnRoomConnected;
            social = gameObject.AddComponent<Netcode.ChibiSocialManager>();
            account = gameObject.AddComponent<Netcode.ChibiAccount>();
            account.OnlineAllowed = () => PlayerIdentity.OnlineAllowed(Save.ageBand);
            account.AccountChanged += OnAccountChanged;
            roomsSvc = gameObject.AddComponent<Netcode.ChibiRoomService>();
            roomsSvc.Bind(rooms);
            roomsSvc.MyName = DisplayedName;
            roomsSvc.OnlineAllowed = () => PlayerIdentity.OnlineAllowed(Save.ageBand);
            roomsSvc.InMatch = () => page == Page.Match || pendingOnline != null;
            roomsSvc.ScreenOpen = () => page == Page.Room;   // room starts only happen while the room screen is shown
            social.Bind(roomsSvc);   // friend invites go into named rooms
            social.PreferredName = () => Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);
            // Age gate enforced inside both services too, not only by the menu (no side path can start online play).
            rooms.OnlineAllowed = () => PlayerIdentity.OnlineAllowed(Save.ageBand);
            social.OnlineAllowed = () => PlayerIdentity.OnlineAllowed(Save.ageBand);
            // Invites are ignored while playing or while another room flow is running.
            social.IsBusy = () => page == Page.Match || pendingOnline != null || roomsSvc.Launching ||
                                  rooms.State == Netcode.ChibiRoomState.Connecting || rooms.State == Netcode.ChibiRoomState.Joining;
            WebcamFeed.SetEnabled(Save.settings.useWebcam);
            ResetCamera();
            account.RestoreOnLaunch(Save.accountId);   // same account on this computer: signed in again silently
            BuildLoadSteps();
            Enter(Page.Loading);
        }

        /// <summary>
        /// Signed in, signed out or switched Google account: load that account's identity (its name follows the account
        /// on every computer; a different account is named again) and drop the old account's friends and rooms.
        /// </summary>
        void OnAccountChanged(string accountId, string serverName)
        {
            accountId = accountId ?? "";
            bool switched = accountId != (Save.accountId ?? "");
            PlayerIdentity.SwitchProfile(Save, accountId, serverName);
            Save.accountAsked = true;
            RecordNewPlayer();   // an account that brought its own name is a new player too (once age is known)
            Save.Save();
            if (switched)
            {
                if (pendingOnline != null) { Destroy(pendingOnline.gameObject); pendingOnline = null; pendingOnlineInput = null; }
                rooms.Cancel();
                social.ResetForAccountChange();
                roomsSvc.ResetForAccountChange();
                roomNameInput = null;
                if (page == Page.Lobby || page == Page.Rooms || page == Page.Room) Enter(Page.Home);
            }
            // The account has no public name yet but we have one: give it to the account now.
            string local = Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);
            if (accountId.Length > 0 && string.IsNullOrEmpty(serverName) && local.Length > 0) account.PushName(local);
            nameDialog = false;   // re-evaluated: NeedsFirstName opens it again when this account has no name
            Toast(accountId.Length > 0 ? "Signed in" + (local.Length > 0 ? " as " + local : "") + "." : "Signed out.");
        }

        void ResetCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera));
                go.tag = "MainCamera";
                cam = go.GetComponent<Camera>();
            }
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Gui.Navy;
            var cc = cam.GetComponent<CameraController>();
            if (cc != null) cc.enabled = false;
        }

        void Update()
        {
            MenuInput.Poll();
            UpdateWipe();
            UpdatePendingOnline();
            if (page == Page.Match)
            {
                MatchInput();
                return;
            }

            bool wasListening = listening;
            if (listening) CaptureRebind();
            menuInput.Tick(null, Time.unscaledDeltaTime);
            menuTracker.Tick(Time.unscaledDeltaTime);
            if (page == Page.Loading) UpdateLoading();
            if (page == Page.PreCheck) UpdatePreCheck();
            homeSelAnim = Mathf.Lerp(homeSelAnim, homeSel, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 18f));
            stageAnim = Mathf.Lerp(stageAnim, stageTarget, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f));

            if (wipeStart >= 0f || wasListening || page == Page.Loading) return;
            if (DialogOpen)
            {
                // Text-entry dialogs own the keyboard; only Back closes the ones that may be closed.
                if (MenuInput.Any(NavCmd.Back))
                {
                    if (roomDialog) roomDialog = false;
                    else if (nameDialog && !NeedsFirstName) nameDialog = false;
                }
                return;
            }
            if (GUIUtility.keyboardControl != 0)
            {
                // Typing into a text field (room code, friend name, LAN address): keys type, Esc leaves the field.
                if (MenuInput.Any(NavCmd.Back)) GUIUtility.keyboardControl = 0;
                return;
            }
            if (page == Page.Select) UpdateSelect();
            else MenuNav();
        }

        /// <summary>A modal onboarding / text-entry dialog is on screen (keyboard menu navigation is off).</summary>
        bool DialogOpen => NeedsOnboarding || nameDialog || roomDialog || NeedsFirstName;

        /// <summary>Keyboard / gamepad driving of every non-select screen through Gui's spatial focus.</summary>
        void MenuNav()
        {
            if (MenuInput.Any(NavCmd.Up)) Gui.Navigate(0, -1);
            if (MenuInput.Any(NavCmd.Down)) Gui.Navigate(0, 1);
            if (MenuInput.Any(NavCmd.Left)) Gui.Navigate(-1, 0);
            if (MenuInput.Any(NavCmd.Right)) Gui.Navigate(1, 0);
            if (MenuInput.Any(NavCmd.Confirm)) Gui.Activate();
            if (exitPrompt)
            {
                if (MenuInput.Any(NavCmd.Back)) CloseExitPrompt();
                return;
            }
            if (IsNavPage(page))
            {
                if (MenuInput.Any(NavCmd.PrevTab)) CycleTab(-1);
                else if (MenuInput.Any(NavCmd.NextTab)) CycleTab(1);
            }
            if (MenuInput.Any(NavCmd.Back)) Back();
        }

        void Back()
        {
            switch (page)
            {
                case Page.Home: OpenExitPrompt(); return;
                case Page.PreCheck: Go(Page.Select); return;
                case Page.Lobby: LobbyBack(); return;
                case Page.Rooms: Go(Page.Online); return;
                case Page.Room:
                    if (roomsSvc.Launching || pendingOnline != null) return;
                    roomsSvc.ExitToList();
                    Go(Page.Rooms);
                    return;
            }
            if (IsNavPage(page)) Go(Page.Home);
        }

        void MatchInput()
        {
            if (match == null || wipeStart >= 0f) return;
            // Gamepad Start pauses offline matches (Esc is handled by the local loop itself). Online never pauses.
            if (!match.Online && !match.Finished && MenuInput.PadStartPressed())
            {
                match.SetPaused(!match.Paused);
                Gui.ResetFocus();
            }
            float sinceEnd = Time.unscaledTime - match.FinishedTime;
            bool results = match.Finished && sinceEnd > 1.3f;
            if (results && !resultsSeen) resultsSeen = true;
            if (!match.Paused && !(results && sinceEnd > 1.9f)) return;
            if (MenuInput.Any(NavCmd.Up)) Gui.Navigate(0, -1);
            if (MenuInput.Any(NavCmd.Down)) Gui.Navigate(0, 1);
            if (MenuInput.Any(NavCmd.Left)) Gui.Navigate(-1, 0);
            if (MenuInput.Any(NavCmd.Right)) Gui.Navigate(1, 0);
            if (MenuInput.Any(NavCmd.Confirm)) Gui.Activate();
        }

        /// <summary>While a binding is selected in Settings, the next key press becomes the new binding (Esc cancels).</summary>
        void CaptureRebind()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) { listening = false; return; }
            string bound = null;
            var mouse = Mouse.current;
            if (mouse != null && Time.unscaledTime - listenSince > 0.25f)
            {
                if (mouse.leftButton.wasPressedThisFrame) bound = Bindings.MouseLeft;
                else if (mouse.rightButton.wasPressedThisFrame) bound = Bindings.MouseRight;
                else if (mouse.middleButton.wasPressedThisFrame) bound = Bindings.MouseMiddle;
            }
            if (bound == null && kb != null)
                foreach (var key in kb.allKeys)
                    if (key.wasPressedThisFrame && key.keyCode != Key.None) { bound = key.keyCode.ToString(); break; }
            if (bound == null) return;
            Bindings.Set(listenPlayer, listenAction, bound);
            Toast("P" + (listenPlayer + 1) + " " + Bindings.Names[(int)listenAction] + " = " + Bindings.BindingLabel(bound));
            listening = false;
        }

        void Toast(string s)
        {
            toast = s;
            toastUntil = Time.unscaledTime + 2.5f;
        }

        // ---------------------------------------------------------------- navigation

        static readonly string[] NavNames = { "HOME", "PLAY", "ONLINE", "TRAINING", "ARCADE", "COLLECTION", "SETTINGS" };
        static readonly Page[] NavPages = { Page.Home, Page.Play, Page.Online, Page.Training, Page.Arcade, Page.Collection, Page.Settings };

        static bool IsNavPage(Page p) => Array.IndexOf(NavPages, p) >= 0;

        /// <summary>The nav tab a page belongs to (the online sub-pages light up ONLINE).</summary>
        static Page NavOwner(Page p) => p == Page.Lobby || p == Page.Rooms || p == Page.Room ? Page.Online : p;

        void CycleTab(int d)
        {
            int i = Array.IndexOf(NavPages, page);
            Go(NavPages[(i + d + NavPages.Length) % NavPages.Length]);
        }

        void Go(Page p)
        {
            if (p != page) Enter(p);
        }

        void Enter(Page p)
        {
            page = p;
            pageSince = Time.unscaledTime;
            exitPrompt = false;
            Gui.ResetFocus();
            GUIUtility.keyboardControl = 0;   // a text field of the previous page must not keep the keyboard
            if (p != Page.Settings) listening = false;
            if (p == Page.Select) EnterSelect();
        }

        void OpenExitPrompt()
        {
            exitPrompt = true;
            Gui.ResetFocus();
        }

        void CloseExitPrompt()
        {
            exitPrompt = false;
            Gui.ResetFocus();
        }

        /// <summary>Diagonal red / navy / blue wipe; the action runs while the screen is fully covered.</summary>
        void Wipe(string caption, Action mid)
        {
            if (wipeStart >= 0f) return;
            wipeStart = Time.unscaledTime;
            wipeAction = mid;
            wipeSwapped = false;
            wipeCaption = caption;
        }

        void UpdateWipe()
        {
            if (wipeStart < 0f) return;
            float t = (Time.unscaledTime - wipeStart) / WipeTime;
            if (!wipeSwapped && t >= 0.5f)
            {
                wipeSwapped = true;
                var a = wipeAction;
                wipeAction = null;
                a?.Invoke();
            }
            if (t >= 1f) wipeStart = -1f;
        }

        void CancelWipe()
        {
            wipeStart = -1f;
            wipeAction = null;
        }

        // ---------------------------------------------------------------- flow

        void StartMatch()
        {
            pending.timeLimit = MatchController.MatchSeconds;
            pending.difficulty = Save.settings.cpuDifficulty;
            if (match != null) Destroy(match.gameObject);
            match = ChibiMatch.Create(this, pending);
            page = Page.Match;
            pageSince = Time.unscaledTime;
            resultsSeen = false;
            exitPrompt = false;
            Gui.ResetFocus();
        }

        string VersusCaption() => Hud.CharNameUpper(pending.cls[0]) + "  VS  " + Hud.CharNameUpper(pending.cls[1]);

        /// <summary>VS AI picks its opponent here (so the wipe caption shows it), then the wipe starts the match.</summary>
        void LaunchMatch()
        {
            PickAiFighter();   // VS AI: a new random opponent every fight (rematches too)
            Wipe(VersusCaption(), StartMatch);
        }

        /// <summary>Skip the menus and start a match directly (editor testing / automation).</summary>
        public ChibiMatch QuickMatch(FighterClass p1, FighterClass p2, bool vsCpu, int stage = 0, bool training = false)
        {
            FinishLoadingNow();
            CancelWipe();
            arcade = false;
            pending.cls[0] = p1;
            pending.cls[1] = p2;
            pending.cpu[0] = false;
            pending.cpu[1] = vsCpu;
            pending.stage = stage;
            pending.training = training;
            aiPicksItself = false;   // automation: keep the exact opponent
            StartMatch();
            return match;
        }

        /// <summary>
        /// Jump straight to a screen (editor testing / automation): home, play (or brawl), online, training, arcade,
        /// collection (or armory), settings, select, precheck, matchmaking, rooms, createroom, lobby, friends.
        /// </summary>
        public void ShowScreen(string screen)
        {
            FinishLoadingNow();
            CancelWipe();
            if (match != null) LeaveMatch(Page.Home);
            const string Locked = "Online play is for players 13 and older.";
            bool onlineOk = PlayerIdentity.OnlineAllowed(Save.ageBand);
            switch (screen)
            {
                case "select": SetupSelect(true, false, false); Enter(Page.Select); return;
                case "precheck": SetupSelect(true, false, false); Enter(Page.PreCheck); preCheckT = 0f; return;
                case "brawl":
                case "play": Enter(Page.Play); return;
                case "online": Enter(Page.Online); return;
                case "training": Enter(Page.Training); return;
                case "arcade": Enter(Page.Arcade); return;
                case "armory":
                case "collection": Enter(Page.Collection); return;
                case "settings": Enter(Page.Settings); return;
                case "matchmaking":
                    if (!onlineOk) { Enter(Page.Home); Toast(Locked); return; }
                    StartMatchmaking();
                    return;
                case "createroom":
                case "rooms":
                    if (!onlineOk) { Enter(Page.Home); Toast(Locked); return; }
                    OpenRoomsPage();
                    if (screen == "createroom") OpenRoomDialog();
                    return;
                case "lobby":
                case "friends":
                    if (!onlineOk) { Enter(Page.Home); Toast(Locked); return; }
                    OpenLobby(screen == "friends");
                    return;
                default: Enter(Page.Home); return;
            }
        }

        void LeaveMatch(Page to)
        {
            if (match != null) Destroy(match.gameObject);   // online: destroying the runner forfeits a running match
            match = null;
            if (rooms != null) rooms.Cancel();
            if (social != null) social.SetInMatch(false);
            Time.timeScale = 1f;
            ResetCamera();
            Enter(to);
        }

        /// <summary>The player came through the VS AI screen (automation's QuickMatch passes an exact opponent instead).</summary>
        bool aiPicksItself;

        /// <summary>VS AI: you against the computer (not training, not local versus).</summary>
        bool VsAi => pending.cpu[1] && !pending.cpu[0] && !pending.training;

        /// <summary>VS AI: the computer picks its own fighter, passive and weapon at random for every fight (rematches too).</summary>
        void PickAiFighter()
        {
            if (!VsAi || !aiPicksItself || arcade) return;
            pending.cls[1] = (FighterClass)UnityEngine.Random.Range(0, CharacterRoster.All.Length);
            pending.passive[1] = UnityEngine.Random.Range(0, 2);
            pending.weapon[1] = UnityEngine.Random.Range(0, WeaponDef.ForClass(pending.cls[1]).Count);
        }

        void SetupSelect(bool cpu, bool training, bool arcadeMode)
        {
            arcade = arcadeMode;
            pending.cpu[0] = false;
            pending.cpu[1] = cpu;
            pending.training = training;
            aiPicksItself = cpu && !training && !arcadeMode;
        }

        /// <summary>Opens character select for a mode (diagonal wipe).</summary>
        void BeginSelect(bool cpu, bool training)
        {
            Wipe("CHARACTER SELECT", () =>
            {
                SetupSelect(cpu, training, false);
                Enter(Page.Select);
            });
        }

        void StartArcade()
        {
            var pool = new List<FighterClass>(CharacterRoster.All.Length);
            foreach (var c in CharacterRoster.All) pool.Add(c.Class);
            for (int i = 0; i < ArcadeRounds; i++)
            {
                if (pool.Count == 0) foreach (var c in CharacterRoster.All) pool.Add(c.Class);
                int k = UnityEngine.Random.Range(0, pool.Count);
                arcadeLadder[i] = pool[k];
                pool.RemoveAt(k);
            }
            arcadeRound = 0;
            Wipe("ARCADE", () =>
            {
                SetupSelect(true, false, true);
                Enter(Page.Select);
            });
        }

        void PrepareArcadeRound()
        {
            int r = Mathf.Clamp(arcadeRound, 0, ArcadeRounds - 1);
            var cls = arcadeLadder[r];
            pending.cpu[1] = true;
            pending.training = false;
            pending.cls[1] = cls;
            pending.passive[1] = UnityEngine.Random.Range(0, 2);
            pending.weapon[1] = UnityEngine.Random.Range(0, WeaponDef.ForClass(cls).Count);
            pending.stage = ArcadeStage(r);
        }

        static int ArcadeStage(int round) => round % StageBuilder.Names.Length;

        /// <summary>Label of the results screen's first button (arcade continues the ladder instead of a rematch).</summary>
        public string RematchLabel
        {
            get
            {
                if (!arcade || match == null) return "REMATCH";
                if (match.Winner != 0) return "CONTINUE";
                return arcadeRound + 1 >= ArcadeRounds ? "FINISH" : "NEXT BATTLE";
            }
        }

        void Rematch()
        {
            if (arcade && match != null)
            {
                if (match.Winner == 0)
                {
                    arcadeRound++;
                    if (arcadeRound >= ArcadeRounds)
                    {
                        arcade = false;
                        Wipe("ARCADE CLEAR", () =>
                        {
                            LeaveMatch(Page.Arcade);
                            Toast("ARCADE CLEAR! All " + ArcadeRounds + " rivals defeated");
                        });
                        return;
                    }
                }
                PrepareArcadeRound();
            }
            LaunchMatch();
        }

        void Confirm()
        {
            if (arcade) PrepareArcadeRound();
            if (Save.settings.preMatchCheck && !pending.training)
                Wipe("CAMERA CHECK", () =>
                {
                    Enter(Page.PreCheck);
                    preCheckT = 0f;
                });
            else LaunchMatch();
        }

        /// <summary>5-second lighting and framing check before queueing (GDD 5).</summary>
        void UpdatePreCheck()
        {
            preCheckT += Time.unscaledDeltaTime;
            if (Save.settings.useWebcam)
            {
                float b = WebcamFeed.Brightness();
                if (b < 0f) { lightOk = false; lightMsg = "Camera not available"; }
                else if (b < 0.18f) { lightOk = false; lightMsg = "Too dark (" + Mathf.RoundToInt(b * 100f) + "%) - add light"; }
                else if (b > 0.92f) { lightOk = false; lightMsg = "Too bright (" + Mathf.RoundToInt(b * 100f) + "%) - reduce glare"; }
                else { lightOk = true; lightMsg = "Good (" + Mathf.RoundToInt(b * 100f) + "%)"; }
            }
            else { lightOk = true; lightMsg = "Simulated feed (webcam off)"; }

            var f = menuTracker.Source.Current;
            bool inFrame = f.confidence >= 0.6f;
            foreach (var p in f.p) inFrame &= p.x > 0.03f && p.x < 0.97f && p.y > 0.03f && p.y < 0.97f;
            frameOk = inFrame;
            frameMsg = inFrame ? "Upper body in frame" : "Step back - shoulders and hips must be visible";

            if (preCheckT >= 5.6f && lightOk && frameOk && wipeStart < 0f) LaunchMatch();
        }

        void QuitGame()
        {
            Save.Save();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---------------------------------------------------------------- loading (real work, real progress)

        class LoadStep
        {
            public string label;
            public Func<float> run;     // called once per frame until it returns 1
            public Action now;          // synchronous completion (automation skipping the loading screen)
            public bool shown;
        }

        readonly List<LoadStep> loadSteps = new List<LoadStep>();
        int loadIndex;
        float loadFrac, loadShown, loadDoneAt = -1f;
        int loadFeatured;

        void BuildLoadSteps()
        {
            loadSteps.Clear();
            loadSteps.Add(new LoadStep { label = "Interface graphics", run = () => { GuiArt.WarmUp(); return 1f; } });
            loadSteps.Add(new LoadStep { label = "Typefaces", run = () => { Gui.WarmUpFonts(); return 1f; } });
            foreach (var w in WeaponDef.All)
            {
                var weapon = w;
                ResourceRequest req = null;
                loadSteps.Add(new LoadStep
                {
                    label = "Weapon art: " + weapon.name,
                    run = () =>
                    {
                        if (req == null) req = Resources.LoadAsync<Sprite>("Weapons/" + weapon.resource);
                        if (!req.isDone) return req.progress * 0.95f;
                        var _ = weapon.Sprite;      // served from memory now
                        return 1f;
                    },
                    now = () => { var _ = weapon.Sprite; }
                });
            }
            foreach (var ch in CharacterRoster.All)
            {
                var c = ch;
                loadSteps.Add(new LoadStep { label = "Fighter: " + c.Name, run = () => { CharacterArt.Preload(c); return 1f; } });
            }
            if (Save.settings.useWebcam)
                loadSteps.Add(new LoadStep { label = "Pose tracking model", run = () => { MoveNetPoseSource.Shared.EnsureLoaded(); return 1f; } });
            loadIndex = 0;
            loadFrac = 0f;
            loadShown = 0f;
            loadDoneAt = -1f;
            loadFeatured = UnityEngine.Random.Range(0, CharacterRoster.All.Length);
            featured = loadFeatured;
        }

        float LoadProgress => loadSteps.Count == 0 ? 1f : Mathf.Clamp01((loadIndex + loadFrac) / loadSteps.Count);
        string LoadLabel => loadIndex < loadSteps.Count ? loadSteps[loadIndex].label : "Ready";

        void UpdateLoading()
        {
            if (loadIndex < loadSteps.Count)
            {
                var step = loadSteps[loadIndex];
                if (!step.shown) step.shown = true;     // show the label for a frame before blocking work runs
                else
                {
                    loadFrac = Mathf.Clamp01(step.run());
                    if (loadFrac >= 1f)
                    {
                        loadIndex++;
                        loadFrac = 0f;
                    }
                }
            }
            // The bar eases toward the real progress but never runs ahead of it.
            float real = LoadProgress;
            loadShown = Mathf.Min(real, Mathf.MoveTowards(loadShown, real, Time.unscaledDeltaTime * 1.8f));
            if (loadIndex >= loadSteps.Count && loadShown >= 0.999f)
            {
                if (loadDoneAt < 0f) loadDoneAt = Time.unscaledTime;
                else if (Time.unscaledTime - loadDoneAt > 0.45f) Wipe(null, () => { if (page == Page.Loading) Enter(Page.Home); });
            }
        }

        void FinishLoadingNow()
        {
            for (; loadIndex < loadSteps.Count; loadIndex++)
            {
                var s = loadSteps[loadIndex];
                if (s.now != null) s.now();
                else s.run();
            }
            loadFrac = 0f;
            loadShown = 1f;
            if (page == Page.Loading) Enter(Page.Home);
        }

        // ---------------------------------------------------------------- GUI

        void OnGUI()
        {
            Gui.Begin();
            if (page == Page.Match && match != null)
            {
                Gui.Interactive = wipeStart < 0f;
                var choice = Hud.Draw(match, this);
                if (wipeStart < 0f) HandleMatchChoice(choice);
            }
            else
            {
                Gui.Interactive = wipeStart < 0f && !exitPrompt;
                DrawMenus();
                Gui.Alpha = 1f;
                if (exitPrompt)
                {
                    Gui.Interactive = wipeStart < 0f;
                    ExitModal();
                }
                if (page != Page.Loading && !DialogOpen) InvitePopup();
                DrawToast();
            }
            Gui.Interactive = true;
            DrawWipe();
            Gui.End();
        }

        void HandleMatchChoice(Hud.Action choice)
        {
            switch (choice)
            {
                case Hud.Action.Resume: match.SetPaused(false); break;
                case Hud.Action.Rematch: Rematch(); break;
                case Hud.Action.CharacterSelect:
                case Hud.Action.Menu:
                    if (match.Online)
                    {
                        if (roomsSvc.CurrentId.Length > 0)
                        {
                            LeaveMatch(Page.Room);   // room fight: back to the room for a rematch
                            roomsSvc.OnMatchEnded();
                        }
                        else LeaveMatch(choice == Hud.Action.Menu ? Page.Home : Page.Online);
                        break;
                    }
                    bool toMenu = choice == Hud.Action.Menu;
                    Wipe(toMenu ? null : "CHARACTER SELECT", () =>
                    {
                        if (toMenu) arcade = false;
                        LeaveMatch(toMenu ? Page.Home : Page.Select);
                    });
                    break;
            }
        }

        /// <summary>Every screen outside a match: loading, onboarding dialogs, then the current page.</summary>
        void DrawMenus()
        {
            if (page == Page.Loading)
            {
                LoadingView();
                return;
            }
            // First launch onboarding: age group, then the player name (their identity everywhere in the game).
            if (PlayerIdentity.NeedsAge(Save.ageBand) || Save.age <= 0)
            {
                DialogBackdrop("WELCOME!");
                AgeDialog();
                return;
            }
            // No sign-in step: the identity is kept automatically for this computer's user profile (every launch, no
            // button). SIGN IN WITH GOOGLE stays optional in the name window, only to carry the name to other computers.
            if (ShowAccountStep)
            {
                DialogBackdrop("YOUR ACCOUNT");
                AccountDialog();
                return;
            }
            if (NeedsFirstName && !nameDialog) OpenNameDialog();
            if (nameDialog)
            {
                DialogBackdrop("YOUR PLAYER NAME");
                NameDialog();
                return;
            }
            if (roomDialog)
            {
                DialogBackdrop("CREATE A ROOM");
                RoomDialog();
                return;
            }
            switch (page)
            {
                case Page.Home: HomeView(); break;
                case Page.Play: PlayView(); break;
                case Page.Online: OnlineView(); break;
                case Page.Training: TrainingView(); break;
                case Page.Arcade: ArcadeView(); break;
                case Page.Collection: CollectionView(); break;
                case Page.Settings: SettingsView(); break;
                case Page.Select: SelectView(); break;
                case Page.PreCheck: PreCheckView(); break;
                case Page.Lobby: LobbyView(); break;
                case Page.Rooms: RoomsView(); break;
                case Page.Room: RoomView(); break;
                default: HomeView(); break;
            }
        }

        void DrawToast()
        {
            if (toast == null || Time.unscaledTime >= toastUntil) return;
            float k = Gui.EaseOut((toastUntil - Time.unscaledTime) / 0.2f) * Gui.EaseOut((Time.unscaledTime - (toastUntil - 2.5f)) / 0.2f);
            var r = new Rect(W * 0.5f - 290f, H - 92f + (1f - k) * 20f, 580f, 46f);
            Gui.Alpha = k;
            Gui.Skew(new Rect(r.x + 5f, r.y + 5f, r.width, r.height), new Color(0f, 0f, 0f, 0.5f));
            Gui.Skew(r, Gui.Navy);
            Gui.SkewFrame(r, Gui.Gold, 2f);
            Gui.Skew(new Rect(r.x + 6f, r.y, 12f, r.height), Gui.Gold);
            Gui.Text(r, toast, 16, Gui.Paper);
            Gui.Alpha = 1f;
        }

        // ---------------------------------------------------------------- PRE-MATCH CHECK

        void PreCheckView()
        {
            Backdrop();
            float k = Gui.EaseOut(Since / 0.35f);
            Gui.Display(new Rect(40f, 24f, 700f, 70f), "CAMERA CHECK", 56, Gui.Paper, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(44f, 88f, 700f, 22f), VersusCaption() + "  ·  " + StageBuilder.Names[Mathf.Clamp(pending.stage, 0, StageBuilder.Names.Length - 1)].ToUpper(), 14, Gui.Gold, TextAnchor.MiddleLeft);

            Gui.Alpha = k;
            var feed = new Rect(W * 0.5f - 420f, 130f, 440f, 340f);
            bool ok = lightOk && frameOk;
            var state = preCheckT < 5f ? Gui.Gold : ok ? Gui.Green : Gui.Red;
            Gui.CvPanel(feed, menuTracker, state, Save.settings.useWebcam, "CAMERA CHECK");

            var p = new Rect(feed.xMax + 20f, 130f, 380f, 340f);
            Gui.Box(p, state);
            float x = p.x + 20f, w = p.width - 40f;
            Gui.Display(new Rect(x, p.y + 14f, w, 34f), preCheckT < 5f ? "CHECKING... " + Mathf.CeilToInt(5f - preCheckT) : ok ? "ALL CHECKS PASSED" : "CHECK FAILED", 28, state, TextAnchor.MiddleLeft, 0.18f);
            Gui.Meter(new Rect(x, p.y + 56f, w, 10f), preCheckT / 5f, state, 20);
            Check(x, p.y + 80f, w, "LIGHTING", lightOk, lightMsg);
            Check(x, p.y + 140f, w, "FRAMING", frameOk, frameMsg);
            Check(x, p.y + 200f, w, "POSTURE", menuTracker.Posture >= 0.6f, menuTracker.Posture >= 0.6f ? "Nice and upright" : "Sit up straight for bonus coins");

            if (preCheckT >= 5f && !ok)
            {
                Gui.PreferNext();
                if (Gui.Button(new Rect(x, p.yMax - 60f, 160f, 44f), "RETRY", Gui.Gold, 16)) preCheckT = 0f;
                if (Gui.Button(new Rect(x + 180f, p.yMax - 60f, 160f, 44f), "PLAY ANYWAY", Gui.P1, 14)) LaunchMatch();
            }
            if (Gui.Button(new Rect(W * 0.5f - 90f, 500f, 180f, 48f), "BACK", Gui.Steel, 16)) Go(Page.Select);
            Gui.Text(new Rect(0f, 560f, W, 40f), "Ranked queues require this check to pass. Casual matches may skip it.", 13, Gui.Muted, TextAnchor.MiddleCenter, false);
            Gui.Alpha = 1f;
            Gui.HintBar("PRE-MATCH CAMERA CHECK", Gui.Key("SPACE", "A"), "SELECT", Gui.Key("ESC", "B"), "BACK");
        }

        void Check(float x, float y, float w, string label, bool ok, string msg)
        {
            Gui.Skew(new Rect(x, y + 5f, 16f, 16f), preCheckT < 5f && !ok ? Gui.Gold : ok ? Gui.Green : Gui.Red, 0.3f);
            Gui.Text(new Rect(x + 28f, y, w - 28f, 26f), label, 17, Gui.Paper, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(x + 28f, y + 26f, w - 28f, 22f), msg, 12, Gui.Muted, TextAnchor.MiddleLeft, false);
        }

        void OnDestroy()
        {
            MoveNetPoseSource.Shared.Dispose();
            WebcamFeed.SetEnabled(false);
            Time.timeScale = 1f;
        }
    }
}
