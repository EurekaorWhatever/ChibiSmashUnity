using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChibiClash
{
    /// <summary>Entry point: owns the save profile, menu flow (GDD 5) and the active match.</summary>
    public class GameManager : MonoBehaviour
    {
        enum View { Menu, Select, PreCheck, Match, Online, Rooms, Room }
        enum Tab { Brawl, Training, Armory, Settings }

        public SaveData Save { get; private set; }

        View view = View.Menu;
        Tab tab = Tab.Brawl;
        ChibiMatch match;   // every mode runs on the deterministic sim (port step 18)

        // Online (Lobby code rooms, matchmaking, Relay): the room manager hands over a connected transport, the runner
        // plays the handshake, and the match view appears once both peers agreed.
        Netcode.NetworkRoomManager rooms;
        Netcode.ChibiOnlineMatchRunner pendingOnline;
        Netcode.ChibiPlayerInputSource pendingOnlineInput;
        string joinCode = "";
        string roomNameInput;   // null until first shown, then defaults to "<name>'s room"
        string lanAddress = "";
        string onlineNote = "";

        // Friends (player names, friend list, invites): Unity Authentication + Unity Friends on top of the private rooms.
        Netcode.ChibiSocialManager social;
        bool showFriends;
        string nameInput = "", friendInput = "";
        // Player name dialog: forced on first launch (no saved name), opened from the name badge in the header.
        bool nameDialog;
        bool roomDialog;          // CREATE ROOM window (rooms page)
        Netcode.ChibiAccount account;   // optional SIGN IN WITH GOOGLE (Unity Player Accounts): name on other computers
        /// <summary>Product decision 2026-11-09: no sign-in step on first launch (identity is automatic per computer user).</summary>
        const bool ShowAccountStepOnFirstLaunch = false;
        Netcode.ChibiRoomService roomsSvc;   // CREATE ROOM mode: named rooms, OK / START FIGHT
        string leaveArmedId;      // two-click LEAVE ROOM
        float leaveArmedUntil;
        string roomError = "";
        string nameError = "", ageInput = "";
        string removeArmedId;
        float removeArmedUntil;
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

        static readonly string[] Difficulty = { "Dummy", "Easy", "Normal", "Hard" };

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
            roomsSvc.InMatch = () => view == View.Match || pendingOnline != null;
            roomsSvc.ScreenOpen = () => view == View.Room;   // room starts only happen while the room screen is shown
            social.Bind(roomsSvc);   // friend invites go into named rooms
            social.PreferredName = () => Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);
            // Age gate enforced inside both services too, not only by the menu (no side path can start online play).
            rooms.OnlineAllowed = () => PlayerIdentity.OnlineAllowed(Save.ageBand);
            social.OnlineAllowed = () => PlayerIdentity.OnlineAllowed(Save.ageBand);
            // Invites are ignored while playing or while another room flow is running.
            social.IsBusy = () => view == View.Match || pendingOnline != null || roomsSvc.Launching ||
                                  rooms.State == Netcode.ChibiRoomState.Connecting || rooms.State == Netcode.ChibiRoomState.Joining;
            WebcamFeed.SetEnabled(Save.settings.useWebcam);
            ResetCamera();
            account.RestoreOnLaunch(Save.accountId);   // same account on this computer: signed in again silently
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
                if (view == View.Online || view == View.Rooms || view == View.Room) view = View.Menu;
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
            cam.backgroundColor = new Color(0.1f, 0.07f, 0.2f);
            var cc = cam.GetComponent<CameraController>();
            if (cc != null) cc.enabled = false;
        }

        void Update()
        {
            UpdatePendingOnline();
            if (view == View.Match) return;
            if (listening) CaptureRebind();
            menuInput.Tick(null, Time.unscaledDeltaTime);
            menuTracker.Tick(Time.unscaledDeltaTime);
            if (view == View.PreCheck) UpdatePreCheck();
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

        // ---------------------------------------------------------------- flow

        void StartMatch()
        {
            pending.timeLimit = MatchController.MatchSeconds;
            pending.difficulty = Save.settings.cpuDifficulty;
            PickAiFighter();   // VS AI: a new random opponent every fight
            if (match != null) Destroy(match.gameObject);
            match = ChibiMatch.Create(this, pending);
            view = View.Match;
        }

        /// <summary>Skip the menus and start a match directly (editor testing / automation).</summary>
        public ChibiMatch QuickMatch(FighterClass p1, FighterClass p2, bool vsCpu, int stage = 0, bool training = false)
        {
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

        /// <summary>Jump straight to a screen: brawl, training, armory, settings, select, precheck (editor testing / automation).</summary>
        public void ShowScreen(string screen)
        {
            if (match != null) LeaveMatch(View.Menu);
            switch (screen)
            {
                case "select": BeginSelect(true, false); return;
                case "matchmaking":
                    if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { view = View.Menu; Toast("Online play is for players 13 and older."); return; }
                    StartMatchmaking();
                    return;
                case "createroom":
                case "rooms":
                    if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { view = View.Menu; Toast("Online play is for players 13 and older."); return; }
                    OpenRoomsPage();
                    if (screen == "createroom") OpenRoomDialog();
                    return;
                case "precheck": BeginSelect(true, false); preCheckT = 0f; view = View.PreCheck; return;
                case "online":
                case "friends":
                    if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { view = View.Menu; Toast("Online play is for players 13 and older."); return; }
                    pending.cpu[0] = pending.cpu[1] = false; pending.training = false; view = View.Online;
                    showFriends = screen == "friends";
                    if (showFriends && !social.Ready && !social.Working) social.Connect();
                    return;
            }
            view = View.Menu;
            tab = screen == "training" ? Tab.Training : screen == "armory" ? Tab.Armory : screen == "settings" ? Tab.Settings : Tab.Brawl;
        }

        void LeaveMatch(View to)
        {
            if (match != null) Destroy(match.gameObject);   // online: destroying the runner forfeits a running match
            match = null;
            if (rooms != null) rooms.Cancel();
            if (social != null) social.SetInMatch(false);
            Time.timeScale = 1f;
            ResetCamera();
            view = to;
        }

        void BeginSelect(bool cpu, bool training)
        {
            pending.cpu[0] = false;
            pending.cpu[1] = cpu;
            pending.training = training;
            aiPicksItself = cpu && !training;
            view = View.Select;
        }

        /// <summary>The player came through the VS AI screen (automation's QuickMatch passes an exact opponent instead).</summary>
        bool aiPicksItself;

        /// <summary>VS AI: you against the computer (not training, not local versus).</summary>
        bool VsAi => pending.cpu[1] && !pending.cpu[0] && !pending.training;

        /// <summary>VS AI: the computer picks its own fighter, passive and weapon at random for every fight (rematches too).</summary>
        void PickAiFighter()
        {
            if (!VsAi || !aiPicksItself) return;
            pending.cls[1] = (FighterClass)Random.Range(0, 5);
            pending.passive[1] = Random.Range(0, 2);
            pending.weapon[1] = Random.Range(0, WeaponDef.ForClass(pending.cls[1]).Count);
        }

        /// <summary>VS AI right column: the AI's fighter is a surprise (picked when the fight starts); choose its difficulty.</summary>
        void AiColumn(Rect r)
        {
            Gui.Rect(r, Gui.Panel, 16f);
            Gui.Frame(r, Gui.Accent2, 2f, 16f);
            Gui.Text(new Rect(r.x + 18f, r.y + 8f, r.width - 36f, 30f), "AI OPPONENT", 20, Gui.Accent2, TextAnchor.MiddleLeft);
            var q = new Rect(r.x + r.width * 0.5f - 80f, r.y + 70f, 160f, 160f);
            Gui.Glow(new Rect(q.x - 30f, q.y - 30f, q.width + 60f, q.height + 60f), new Color(0.4f, 0.8f, 1f, 0.25f));
            Gui.Rect(q, Gui.PanelLight, 80f);
            Gui.Text(q, "?", 96, Gui.Accent2);
            Gui.Text(new Rect(r.x + 30f, q.yMax + 18f, r.width - 60f, 50f),
                     "The AI picks its own fighter at random when the fight starts.", 16, Color.white, TextAnchor.UpperCenter, false);

            Gui.Text(new Rect(r.x + 18f, r.yMax - 120f, r.width - 36f, 24f), "AI LEVEL", 15, Gui.Gold, TextAnchor.MiddleLeft);
            float bw = (r.width - 36f - 20f) / 3f;
            for (int lvl = 1; lvl <= 3; lvl++)
                if (Gui.Button(new Rect(r.x + 18f + (lvl - 1) * (bw + 10f), r.yMax - 90f, bw, 48f), Difficulty[lvl], Gui.Accent2, 17, true,
                               Save.settings.cpuDifficulty == lvl))
                {
                    Save.settings.cpuDifficulty = lvl;
                    Save.Save();
                }
        }

        void Confirm()
        {
            if (Save.settings.preMatchCheck && !pending.training)
            {
                preCheckT = 0f;
                view = View.PreCheck;
            }
            else StartMatch();
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

            if (preCheckT >= 5.6f && lightOk && frameOk) StartMatch();
        }

        // ---------------------------------------------------------------- GUI

        void OnGUI()
        {
            Gui.Begin();
            if (view == View.Match && match != null)
            {
                var choice = Hud.Draw(match, this);
                switch (choice)
                {
                    case Hud.Action.Resume: match.SetPaused(false); break;
                    case Hud.Action.Rematch: StartMatch(); break;
                    case Hud.Action.CharacterSelect:
                    case Hud.Action.Menu:
                        if (match.Online && roomsSvc.CurrentId.Length > 0)
                        {
                            LeaveMatch(View.Room);   // room fight: back to the room for a rematch
                            roomsSvc.OnMatchEnded();
                        }
                        else LeaveMatch(choice == Hud.Action.Menu ? View.Menu : View.Select);
                        break;
                }
                return;
            }

            Background();
            // First launch onboarding: age group, then the player name (their identity everywhere in the game).
            if (PlayerIdentity.NeedsAge(Save.ageBand) || Save.age <= 0)
            {
                Header("Welcome!");
                AgeDialog();
                return;
            }
            // No sign-in step: the identity is kept automatically for this computer's user profile (every launch, no
            // button). SIGN IN WITH GOOGLE stays optional in the name window, only to carry the name to other computers.
            if (ShowAccountStepOnFirstLaunch && !Save.accountAsked && PlayerIdentity.OnlineAllowed(Save.ageBand) && Save.accountId.Length == 0)
            {
                Header("Your account");
                AccountDialog();
                return;
            }
            if (NeedsFirstName && !nameDialog) OpenNameDialog();
            if (nameDialog)
            {
                Header("Your player name");
                NameDialog();
                return;
            }
            if (roomDialog)
            {
                Header("Create a room");
                RoomDialog();
                return;
            }
            switch (view)
            {
                case View.Menu: MenuView(); break;
                case View.Select: SelectView(); break;
                case View.PreCheck: PreCheckView(); break;
                case View.Online: OnlineView(); break;
                case View.Rooms: RoomsView(); break;
                case View.Room: RoomView(); break;
            }
            InvitePopup();

            if (toast != null && Time.unscaledTime < toastUntil)
            {
                var r = new Rect(Gui.W * 0.5f - 260f, Gui.H - 70f, 520f, 44f);
                Gui.Rect(r, Gui.PanelLight, 12f);
                Gui.Frame(r, Gui.Gold, 2f, 12f);
                Gui.Text(r, toast, 15, Color.white);
            }
        }

        void Background()
        {
            Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(0.09f, 0.06f, 0.18f));
            float t = Time.unscaledTime;
            for (int i = 0; i < 8; i++)
            {
                float x = (Mathf.Sin(t * 0.13f + i * 1.7f) * 0.5f + 0.5f) * Gui.W;
                float y = (Mathf.Cos(t * 0.11f + i * 2.3f) * 0.5f + 0.5f) * Gui.H;
                float s = 260f + i * 40f;
                var c = i % 2 == 0 ? Gui.Accent : Gui.Accent2;
                Gui.Glow(new Rect(x - s * 0.5f, y - s * 0.5f, s, s), new Color(c.r, c.g, c.b, 0.12f));
            }
        }

        void Header(string subtitle)
        {
            Gui.Text(new Rect(30f, 14f, 500f, 64f), "<color=#ff6aa0>CHIBI</color> <color=#5fdcff>CLASH</color>", 50, Color.white, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(34f, 70f, 600f, 22f), subtitle, 14, Gui.Muted, TextAnchor.MiddleLeft, false);
            var coin = new Rect(Gui.W - 230f, 24f, 200f, 44f);
            Gui.Rect(coin, Gui.Panel, 22f);
            Gui.Frame(coin, Gui.Gold, 2f, 22f);
            Gui.Circle(new Rect(coin.x + 10f, coin.y + 9f, 26f, 26f), Gui.Gold);
            Gui.Text(new Rect(coin.x + 44f, coin.y, 150f, 44f), Save.coins + " coins", 18, Gui.Gold, TextAnchor.MiddleLeft);

            // Name badge: who we are; click to change the name.
            if (!nameDialog && !PlayerIdentity.NeedsAge(Save.ageBand) && Save.age > 0)
            {
                string shown = DisplayedName();
                if (Gui.Button(new Rect(Gui.W - 520f, 26f, 280f, 38f), (shown.Length > 0 ? shown : "SET NAME") + "  ·  EDIT", Gui.Accent2, 16))
                    OpenNameDialog();
            }
        }

        // ---------------------------------------------------------------- PLAYER NAME

        /// <summary>No valid saved name yet: the first-launch name dialog is shown.</summary>
        bool NeedsFirstName => Netcode.ChibiSocialRules.CleanSavedName(Save.playerName).Length == 0;

        /// <summary>The name to show for us: Unity's tagged name once signed in ("Kira#4821"), else the saved one.</summary>
        string DisplayedName() =>
            social != null && social.Ready && !social.NeedsName ? social.MyName : Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);

        /// <summary>
        /// Onboarding step 1: a neutral age question (no hint of a "right" answer). Only the age GROUP is saved. Under 13,
        /// online friends / invites / matchmaking stay locked; everything offline is available.
        /// </summary>
        void AgeDialog()
        {
            var r = new Rect(Gui.W * 0.5f - 330f, 170f, 660f, 300f);
            Gui.Rect(r, Gui.Panel, 18f);
            Gui.Frame(r, Gui.Accent2, 3f, 18f);
            Gui.Text(new Rect(r.x, r.y + 18f, r.width, 36f), "HOW OLD ARE YOU?", 26, Gui.Accent2);
            Gui.Text(new Rect(r.x + 30f, r.y + 58f, r.width - 60f, 40f),
                     "Every age can play every mode. Your age is recorded with your player name for game statistics.", 13, Gui.Muted, TextAnchor.UpperCenter, false);

            bool enter = Event.current.type == EventType.KeyDown &&
                         (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            var field = new Rect(r.x + r.width * 0.5f - 90f, r.y + 106f, 180f, 56f);
            GUI.SetNextControlName("Age");
            var style = new GUIStyle(GUI.skin.textField) { fontSize = 30, alignment = TextAnchor.MiddleCenter };
            string typed = GUI.TextField(field, ageInput ?? "", 3, style);
            ageInput = System.Text.RegularExpressions.Regex.Replace(typed, "[^0-9]", "");   // digits only
            if (GUI.GetNameOfFocusedControl() != "Age") GUI.FocusControl("Age");
            Gui.Text(new Rect(r.x + 30f, r.y + 168f, r.width - 60f, 24f), nameError, 14, Gui.Red);

            if (Gui.Button(new Rect(r.x + r.width * 0.5f - 130f, r.y + 210f, 260f, 54f), "NEXT", Gui.Accent, 20) || enter)
            {
                var band = PlayerIdentity.BandForAnswer(ageInput);
                if (band == AgeBand.Unknown) nameError = "Please type your age as a number.";
                else
                {
                    Save.ageBand = (int)band;
                    Save.age = PlayerIdentity.ParseAge(ageInput);
                    Save.Save();
                    nameError = "";
                    ageInput = "";
                }
                if (enter) Event.current.Use();
            }
        }

        void OpenRoomDialog()
        {
            roomDialog = true;
            roomError = "";
            if (string.IsNullOrEmpty(roomNameInput)) roomNameInput = Netcode.ChibiSocialRules.DefaultRoomName(DisplayedName());
        }

        /// <summary>
        /// CREATE ROOM window (rooms page): name the room and pick the stage; the room is created and opened at once
        /// (choose your fighter there, press OK, and share the code or invite a friend).
        /// </summary>
        void RoomDialog()
        {
            var r = new Rect(Gui.W * 0.5f - 340f, 150f, 680f, 360f);
            Gui.Rect(r, Gui.Panel, 18f);
            Gui.Frame(r, Gui.Accent, 3f, 18f);
            Gui.Text(new Rect(r.x, r.y + 18f, r.width, 36f), "CREATE A ROOM", 26, Gui.Accent);
            Gui.Text(new Rect(r.x + 30f, r.y + 58f, r.width - 60f, 22f),
                     "Name your room (3-24 letters, numbers, spaces). Your friend sees it when they join with the code.",
                     13, Gui.Muted, TextAnchor.UpperCenter, false);

            bool enter = Event.current.type == EventType.KeyDown &&
                         (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            var field = new Rect(r.x + 60f, r.y + 92f, r.width - 120f, 50f);
            GUI.SetNextControlName("RoomName");
            var style = new GUIStyle(GUI.skin.textField) { fontSize = 24, alignment = TextAnchor.MiddleCenter };
            roomNameInput = GUI.TextField(field, roomNameInput ?? "", Netcode.ChibiSocialRules.RoomNameMax, style);
            if (GUI.GetNameOfFocusedControl() != "RoomName") GUI.FocusControl("RoomName");

            Gui.Text(new Rect(r.x + 60f, r.y + 152f, 200f, 24f), "STAGE", 14, Gui.Gold, TextAnchor.MiddleLeft);
            float sw = (r.width - 120f - 20f) / 3f;
            for (int st = 0; st < StageBuilder.Names.Length; st++)
                if (Gui.Button(new Rect(r.x + 60f + st * (sw + 10f), r.y + 178f, sw, 40f), StageBuilder.Names[st], Gui.Gold, 14, true, pending.stage == st))
                    pending.stage = st;

            Gui.Text(new Rect(r.x + 30f, r.y + 228f, r.width - 60f, 24f), roomError, 14, Gui.Red);

            if (Gui.Button(new Rect(r.x + r.width * 0.5f - 230f, r.y + 270f, 220f, 56f), "CREATE ROOM", Gui.Accent, 20) || enter)
            {
                string n = (roomNameInput ?? "").Trim();
                string err = Netcode.ChibiSocialRules.ValidateRoomName(n);
                if (err != null) roomError = err;
                else if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) roomError = "Online play is for players 13 and older.";
                else
                {
                    roomDialog = false;
                    pending.cpu[0] = pending.cpu[1] = false;
                    pending.training = false;
                    onlineNote = "";
                    social.ClearMatchFriend();
                    roomsSvc.Create(n, pending.stage);
                    view = View.Room;
                }
                if (enter) Event.current.Use();
            }
            if (Gui.Button(new Rect(r.x + r.width * 0.5f + 10f, r.y + 270f, 220f, 56f), "CANCEL", Gui.Muted, 18))
                roomDialog = false;
        }

        /// <summary>
        /// Onboarding: SIGN IN WITH GOOGLE (the name and info stay with the account; the same account on this computer
        /// signs in by itself next time) or PLAY WITHOUT ACCOUNT (kept on this computer only). Changeable later from the
        /// name button.
        /// </summary>
        void AccountDialog()
        {
            var r = new Rect(Gui.W * 0.5f - 340f, 150f, 680f, 330f);
            Gui.Rect(r, Gui.Panel, 18f);
            Gui.Frame(r, Gui.Accent2, 3f, 18f);
            Gui.Text(new Rect(r.x, r.y + 18f, r.width, 36f), "KEEP YOUR FIGHTER NAME", 26, Gui.Accent2);
            Gui.Text(new Rect(r.x + 40f, r.y + 62f, r.width - 80f, 70f),
                     "Sign in with your Google account and your name and info stay with you: every time you open the game, " +
                     "and on any computer. Another Google account gets its own new name.", 14, Color.white, TextAnchor.UpperCenter, false);
            bool working = account.Working;
            if (Gui.Button(new Rect(r.x + r.width * 0.5f - 200f, r.y + 146f, 400f, 58f), working ? "FINISH IN YOUR BROWSER..." : "SIGN IN WITH GOOGLE",
                           Gui.Accent, 20, !working))
                account.SignInWithGoogle();
            if (Gui.Button(new Rect(r.x + r.width * 0.5f - 200f, r.y + 216f, 400f, 46f), "PLAY WITHOUT ACCOUNT", Gui.Muted, 16, !working))
            {
                Save.accountAsked = true;
                Save.Save();
            }
            Gui.Text(new Rect(r.x + 30f, r.y + 272f, r.width - 60f, 50f), account.Status, 12, account.StatusIsError ? Gui.Red : Gui.Muted,
                     TextAnchor.UpperCenter, false);
        }

        /// <summary>
        /// The player age record (market analysis): one row per new player, written once the player has given their
        /// age AND created their identity name. File: PlayerAgeLedger.FileName in the game's data folder.
        /// </summary>
        void RecordNewPlayer()
        {
            if (Save.ageRecorded || Save.age <= 0) return;
            string name = Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);
            if (name.Length == 0) return;
            var ledger = new PlayerAgeLedger(System.IO.Path.Combine(Application.persistentDataPath, PlayerAgeLedger.FileName));
            string row = PlayerAgeLedger.Row(name, Save.age, Netcode.ChibiRegions.DeviceCountry(), Application.platform.ToString(),
                                             Application.version, System.DateTime.UtcNow);
            if (ledger.Append(row)) Save.ageRecorded = true;
        }

        void OpenNameDialog()
        {
            nameDialog = true;
            nameInput = Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);
            nameError = "";
        }

        /// <summary>Choose or change the player name (validated by ChibiSocialRules.ValidateName; saved; pushed to Unity).</summary>
        void NameDialog()
        {
            bool first = NeedsFirstName;
            var r = new Rect(Gui.W * 0.5f - 330f, 170f, 660f, 300f);
            Gui.Rect(r, Gui.Panel, 18f);
            Gui.Frame(r, Gui.Gold, 3f, 18f);
            Gui.Text(new Rect(r.x, r.y + 18f, r.width, 36f), first ? "WELCOME, FIGHTER! WHAT IS YOUR NAME?" : "CHANGE YOUR NAME", 24, Gui.Gold);
            Gui.Text(new Rect(r.x + 30f, r.y + 58f, r.width - 60f, 40f),
                     "3-16 letters, numbers or _. Pick a fun fighter name, not your real name. It is how friends find and invite you, and what everyone sees.",
                     13, Gui.Muted, TextAnchor.UpperCenter, false);

            bool enter = Event.current.type == EventType.KeyDown &&
                         (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            var field = new Rect(r.x + 60f, r.y + 112f, r.width - 120f, 50f);
            GUI.SetNextControlName("PlayerName");
            var style = new GUIStyle(GUI.skin.textField) { fontSize = 26, alignment = TextAnchor.MiddleCenter };
            nameInput = GUI.TextField(field, nameInput ?? "", Netcode.ChibiSocialRules.NameMax, style);
            if (GUI.GetNameOfFocusedControl() != "PlayerName") GUI.FocusControl("PlayerName");
            Gui.Text(new Rect(r.x + 30f, r.y + 168f, r.width - 60f, 24f), nameError, 14, Gui.Red);

            float bw = first ? 260f : 200f;
            float bx = first ? r.x + (r.width - bw) * 0.5f : r.x + r.width * 0.5f - bw - 10f;
            if (Gui.Button(new Rect(bx, r.y + 210f, bw, 54f), first ? "LET'S FIGHT!" : "SAVE", Gui.Accent, 20) || enter)
            {
                string n = (nameInput ?? "").Trim();
                string err = Netcode.ChibiSocialRules.ValidateName(n);
                if (err != null) nameError = err;
                else
                {
                    bool changed = n != Save.playerName;
                    Save.playerName = n;
                    RecordNewPlayer();   // age given + identity name created: this player's row in the age record
                    Save.Save();
                    nameDialog = false;
                    if (changed && social.Ready) social.SetName(n);   // signed in: rename on Unity now (else synced on connect)
                    else if (changed && account.SignedIn) account.PushName(n);   // the account keeps the name everywhere
                    Toast("You are " + n + "!");
                }
                if (enter) Event.current.Use();
            }
            // Account row: who this identity belongs to, and switching Google account.
            if (!first && PlayerIdentity.OnlineAllowed(Save.ageBand))
            {
                var ar = new Rect(r.x, r.yMax + 16f, r.width, 50f);
                Gui.Rect(ar, Gui.Panel, 12f);
                Gui.Text(new Rect(ar.x + 18f, ar.y, ar.width - 260f, 50f),
                         account.SignedIn ? "Signed in with your account: this name stays with it on every computer."
                                          : "Saved on this computer automatically. Sign in only to use it on other computers.", 13, Gui.Muted, TextAnchor.MiddleLeft, false);
                string label = account.SignedIn ? "SIGN OUT / SWITCH" : "SIGN IN WITH GOOGLE";
                if (Gui.Button(new Rect(ar.xMax - 236f, ar.y + 7f, 222f, 36f), account.Working ? "SIGNING IN..." : label, Gui.Accent2, 14, !account.Working))
                {
                    if (account.SignedIn) account.SignOut();
                    else account.SignInWithGoogle();
                }
                if (account.Status.Length > 0)
                    Gui.Text(new Rect(ar.x, ar.yMax + 4f, ar.width, 40f), account.Status, 12, account.StatusIsError ? Gui.Red : Gui.Muted, TextAnchor.UpperCenter, false);
            }
            if (!first && Gui.Button(new Rect(r.x + r.width * 0.5f + 10f, r.y + 210f, 200f, 54f), "CANCEL", Gui.Muted, 18))
                nameDialog = false;
        }

        void MenuView()
        {
            Header("2D PvP arena fighter · body-gesture ultimates · cosmetics-only gacha");
            string[] names = { "BRAWL", "TRAINING / CV SETUP", "ARMORY", "SETTINGS" };
            float tw = 250f;
            float tx = Gui.W * 0.5f - (tw * 4f + 30f) * 0.5f;
            for (int i = 0; i < 4; i++)
                if (Gui.Button(new Rect(tx + i * (tw + 10f), 104f, tw, 44f), names[i], i % 2 == 0 ? Gui.Accent : Gui.Accent2, 17, true, (int)tab == i))
                    tab = (Tab)i;
            if (tab != Tab.Settings) listening = false;

            var area = new Rect(30f, 166f, Gui.W - 60f, Gui.H - 196f);
            switch (tab)
            {
                case Tab.Brawl: BrawlTab(area); break;
                case Tab.Training: TrainingTab(area); break;
                case Tab.Armory: ArmoryTab(area); break;
                case Tab.Settings: SettingsTab(area); break;
            }
        }

        // ---------------------------------------------------------------- BRAWL

        void BrawlTab(Rect a)
        {
            // Mode blocks. Top row = the three main modes; one click goes straight into that mode.
            float rowW = a.width - 420f - 15f;
            float bw3 = (rowW - 30f) / 3f;
            var bMatch = new Rect(a.x, a.y, bw3, 240f);
            var bRooms = new Rect(a.x + bw3 + 15f, a.y, bw3, 240f);
            var bAi = new Rect(a.x + (bw3 + 15f) * 2f, a.y, bw3, 240f);
            var sLocal = new Rect(a.x, a.y + 255f, bw3, 165f);
            var sTrain = new Rect(a.x + bw3 + 15f, a.y + 255f, bw3, 165f);
            var sFriends = new Rect(a.x + (bw3 + 15f) * 2f, a.y + 255f, bw3, 165f);
            bool onlineOk = PlayerIdentity.OnlineAllowed(Save.ageBand);
            const string Locked = "Online modes are for players 13 and older.";
            // MATCHMAKING and ROOMS need the internet (Wi-Fi or mobile data); VS AI, local versus and training never do.
            bool online = Application.internetReachability != NetworkReachability.NotReachable;
            const string NoNet = "No internet: connect Wi-Fi or mobile data. VS AI works offline.";

            if (ModeCard(bMatch, !onlineOk ? "MATCHMAKING 13+" : "MATCHMAKING",
                         !onlineOk ? Locked : !online ? "NO INTERNET\nConnect to Wi-Fi or mobile data." :
                         "Fight players online now.\nClosest first: your country,\nnearby countries, then the world.",
                         Gui.Accent2, true, 28))
            {
                if (!onlineOk) Toast(Locked);
                else if (!online) Toast(NoNet);
                else StartMatchmaking();
            }
            if (ModeCard(bRooms, !onlineOk ? "ROOMS  13+" : "ROOMS",
                         !onlineOk ? Locked : !online ? "NO INTERNET\nConnect to Wi-Fi or mobile data." :
                         "Your own named rooms.\nInvite a friend, both press OK,\nthen START FIGHT.",
                         Gui.Gold, true, 28))
            {
                if (!onlineOk) Toast(Locked);
                else if (!online) Toast(NoNet);
                else OpenRoomsPage();
            }
            if (ModeCard(bAi, "VS AI", "Fight the computer:\nEasy, Normal or Hard.\nNo internet needed.", Gui.Accent, true, 28))
                BeginSelect(true, false);

            if (ModeCard(sLocal, "LOCAL VERSUS", "2 players, one keyboard\nor two gamepads.", Gui.Muted, true, 22)) BeginSelect(false, false);
            if (ModeCard(sTrain, "TRAINING", "Combos and gestures\non a dummy.", Gui.Muted, true, 22)) BeginSelect(true, true);
            if (ModeCard(sFriends, onlineOk ? "FRIENDS & LAN" : "FRIENDS 13+", "Friends list, invites\nand same-network play.", Gui.Muted, true, 22))
            {
                if (!onlineOk) Toast(Locked);
                else
                {
                    pending.cpu[0] = pending.cpu[1] = false;
                    pending.training = false;
                    onlineNote = "";
                    showFriends = true;
                    view = View.Online;
                    if (!social.Ready && !social.Working) social.Connect();
                }
            }

            var q = new Rect(a.xMax - 420f, a.y, 420f, a.height);
            Gui.Rect(q, Gui.Panel, 16f);
            Gui.Text(new Rect(q.x + 18f, q.y + 10f, 380f, 30f), "QUESTS", 20, Gui.Gold, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(q.x + 18f, q.y + 10f, 384f, 30f), "W " + Save.wins + "  L " + Save.losses, 14, Gui.Muted, TextAnchor.MiddleRight);
            float y = q.y + 50f;
            foreach (var quest in Save.quests)
            {
                Gui.Text(new Rect(q.x + 18f, y, 300f, 22f), quest.desc, 14, Color.white, TextAnchor.MiddleLeft, false);
                Gui.Text(new Rect(q.x + 18f, y, 384f, 22f), "+" + quest.reward, 14, Gui.Gold, TextAnchor.MiddleRight);
                Gui.Bar(new Rect(q.x + 18f, y + 25f, 384f, 10f), (float)quest.progress / quest.target, Gui.Accent2, new Color(0f, 0f, 0f, 0.4f));
                Gui.Text(new Rect(q.x + 18f, y + 36f, 384f, 16f), quest.progress + " / " + quest.target + (quest.completions > 0 ? "   ·   completed x" + quest.completions : ""), 11, Gui.Muted, TextAnchor.MiddleLeft, false);
                y += 62f;
            }
            Gui.Text(new Rect(q.x + 18f, q.yMax - 50f, 384f, 44f), "Coins come only from playing: wins, PvE, quests and good posture tracked by the camera.", 12, Gui.Muted, TextAnchor.MiddleLeft, false);
        }

        bool ModeCard(Rect r, string title, string desc, Color c, bool enabled, int titleSize = 34)
        {
            bool hover = enabled && r.Contains(Event.current.mousePosition);
            if (hover) Gui.Glow(new Rect(r.x - 20f, r.y - 20f, r.width + 40f, r.height + 40f), new Color(c.r, c.g, c.b, 0.3f));
            Gui.Rect(r, hover ? Gui.PanelLight : Gui.Panel, 18f);
            Gui.Frame(r, c, hover ? 3f : 2f, 18f);
            Gui.Text(new Rect(r.x + 24f, r.y + 30f, r.width - 48f, 60f), title, titleSize, enabled ? Color.white : Gui.Muted, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(r.x + 24f, r.y + 96f, r.width - 48f, 70f), desc, 15, Gui.Muted, TextAnchor.UpperLeft, false);
            if (!enabled) Gui.Text(new Rect(r.x, r.y + 14f, r.width - 24f, 24f), "LOCKED", 13, Gui.Red, TextAnchor.MiddleRight);
            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // ---------------------------------------------------------------- TRAINING / CV SETUP

        void TrainingTab(Rect a)
        {
            var feed = new Rect(a.x, a.y, 520f, 400f);
            var st = menuTracker.Stable != Gesture.None ? Gui.Green : Gui.Yellow;
            Gui.CvPanel(feed, menuTracker, st, Save.settings.useWebcam, "CV SETUP · " + menuTracker.Source.Name);

            var p = new Rect(a.x + 545f, a.y, a.width - 545f, a.height);
            Gui.Rect(p, Gui.Panel, 16f);
            float x = p.x + 20f, w = p.width - 40f, y = p.y + 14f;
            Gui.Text(new Rect(x, y, w, 28f), "COMPUTER VISION", 20, Gui.Accent2, TextAnchor.MiddleLeft);
            y += 34f;
            string status = "Camera: " + (Save.settings.useWebcam ? WebcamFeed.Status : "off") +
                            "\nTracking: " + menuPose.Status() +
                            "\nDetected: " + (menuTracker.Stable == Gesture.None ? "none" : ClassDef.GestureName(menuTracker.Stable)) +
                            "   ·   Posture " + Mathf.RoundToInt(menuTracker.Posture * 100f) + "%" +
                            "   ·   Telemetry " + menuTracker.Validator.Reason;
            Gui.Text(new Rect(x, y, w, 70f), status, 13, Color.white, TextAnchor.UpperLeft, false);
            y += 80f;

            Gui.Text(new Rect(x, y, w, 24f), (menuTracker.IsLive ? "GESTURES  (do the pose in front of the camera - punch = one arm straight out to the side)" : "GESTURES  (hold the key to simulate the pose)"), 14, Gui.Gold, TextAnchor.MiddleLeft);
            y += 30f;
            var gestures = new[] { Gesture.CrossArms, Gesture.PunchForward, Gesture.RaiseHands };
            string[] uses = { "Block · Melee & Magic ultimate", "Fireball · Ranged & Polearm ultimate", "Super jump · Heavy ultimate" };
            float gw = (w - 20f) / 3f;
            for (int i = 0; i < 3; i++)
            {
                var g = new Rect(x + i * (gw + 10f), y, gw, 190f);
                bool active = menuTracker.Stable == gestures[i];
                Gui.Rect(g, active ? new Color(0.1f, 0.35f, 0.25f, 0.9f) : Gui.PanelLight, 12f);
                Gui.Frame(g, active ? Gui.Green : Gui.Muted * 0.6f, 2f, 12f);
                Gui.Skeleton(new Rect(g.x + gw * 0.5f - 50f, g.y + 8f, 100f, 110f), SimulatedPoseSource.Template(gestures[i], false), active ? Gui.Green : Gui.Accent2, 2.5f);
                Gui.Text(new Rect(g.x, g.y + 120f, gw, 22f), "[" + (i + 1) + "] " + ClassDef.GestureName(gestures[i]), 13, Color.white);
                Gui.Text(new Rect(g.x + 6f, g.y + 144f, gw - 12f, 40f), uses[i], 11, Gui.Muted, TextAnchor.UpperCenter, false);
            }
            y += 205f;
            Gui.Text(new Rect(x, y, w, 20f), "Press P to toggle a slouching posture and watch the posture score drop.", 12, Gui.Muted, TextAnchor.MiddleLeft, false);

            if (Gui.Button(new Rect(a.x, a.y + 415f, 250f, 48f), Save.settings.useWebcam ? "CAMERA TRACKING: ON" : "CAMERA TRACKING: OFF", Gui.Accent2, 16, true, Save.settings.useWebcam))
            {
                Save.settings.useWebcam = !Save.settings.useWebcam;
                WebcamFeed.SetEnabled(Save.settings.useWebcam);
                Save.Save();
            }
            if (Gui.Button(new Rect(a.x + 270f, a.y + 415f, 250f, 48f), "PRACTICE VS DUMMY", Gui.Gold, 16))
                BeginSelect(true, true);
        }

        // ---------------------------------------------------------------- ARMORY / GACHA

        void ArmoryTab(Rect a)
        {
            for (int i = 0; i < 5; i++)
            {
                var def = ClassDef.All[i];
                if (Gui.Button(new Rect(a.x, a.y + i * 56f, 180f, 48f), def.heroName + " · " + def.className, def.color, 15, true, armoryClass == def.type))
                    armoryClass = def.type;
            }

            var grid = new Rect(a.x + 200f, a.y, 600f, a.height);
            var skins = Skin.All.FindAll(s => s.cls == armoryClass);
            var equipped = Save.Equipped(armoryClass);
            var cdef = ClassDef.Get(armoryClass);
            for (int i = 0; i < skins.Count; i++)
            {
                var s = skins[i];
                var r = new Rect(grid.x + (i % 3) * 200f, grid.y + (i / 3) * 236f, 188f, 224f);
                bool owned = Save.owned.Contains(s.id);
                var rc = Skin.RarityColor(s.rarity);
                Gui.Rect(r, owned ? Gui.PanelLight : Gui.Panel, 14f);
                Gui.Frame(r, s == equipped ? Gui.Green : rc, s == equipped ? 3f : 2f, 14f);
                if (owned) Gui.Pfp(new Rect(r.x + 44f, r.y + 14f, 100f, 100f), cdef, s);
                else
                {
                    Gui.Circle(new Rect(r.x + 44f, r.y + 14f, 100f, 100f), new Color(0.05f, 0.05f, 0.1f));
                    Gui.Text(new Rect(r.x + 44f, r.y + 14f, 100f, 100f), "?", 50, Gui.Muted);
                }
                Gui.Text(new Rect(r.x, r.y + 120f, r.width, 24f), s.name, 16, Color.white);
                Gui.Text(new Rect(r.x, r.y + 142f, r.width, 20f), s.rarity.ToString().ToUpper() + (s.goldCinematic ? " · GOLD CINEMATIC" : ""), 11, rc);
                if (owned)
                {
                    if (s == equipped) Gui.Text(new Rect(r.x, r.y + 172f, r.width, 36f), "EQUIPPED", 14, Gui.Green);
                    else if (Gui.Button(new Rect(r.x + 34f, r.y + 172f, 120f, 36f), "EQUIP", Gui.Accent2, 14)) Save.Equip(s);
                }
                else Gui.Text(new Rect(r.x, r.y + 172f, r.width, 36f), "Pull to unlock", 12, Gui.Muted);
            }

            var g = new Rect(a.x + 820f, a.y, a.width - 820f, a.height);
            Gui.Rect(g, Gui.Panel, 16f);
            Gui.Frame(g, Gui.Gold, 2f, 16f);
            float x = g.x + 18f, w = g.width - 36f, y = g.y + 12f;
            Gui.Text(new Rect(x, y, w, 30f), "COSMETIC BANNER", 20, Gui.Gold);
            y += 32f;
            Gui.Text(new Rect(x, y, w, 50f), "Skins & ultimate cinematics only. Stats are identical on every skin - nothing is paywalled.", 12, Gui.Muted, TextAnchor.UpperCenter, false);
            y += 52f;
            Gui.Text(new Rect(x, y, w, 20f), "Common 60% · Rare 28% · Epic 10% · Legendary 2%", 11, Color.white);
            y += 28f;
            if (Gui.Button(new Rect(x, y, w * 0.5f - 5f, 34f), "ALL CLASSES", Gui.Accent2, 12, true, bannerAll)) bannerAll = true;
            if (Gui.Button(new Rect(x + w * 0.5f + 5f, y, w * 0.5f - 5f, 34f), ClassDef.Get(armoryClass).heroName.ToUpper() + " ONLY", Gui.Accent2, 12, true, !bannerAll)) bannerAll = false;
            y += 46f;
            FighterClass? banner = bannerAll ? (FighterClass?)null : armoryClass;
            if (Gui.Button(new Rect(x, y, w, 46f), "PULL x1  ·  " + SaveData.PullCost, Gui.Accent, 16, Save.coins >= SaveData.PullCost))
                DoPull(1, banner);
            y += 54f;
            if (Gui.Button(new Rect(x, y, w, 46f), "PULL x10  ·  " + SaveData.TenPullCost, Gui.Gold, 16, Save.coins >= SaveData.TenPullCost))
                DoPull(10, banner);
            y += 60f;
            foreach (var p in lastPull)
            {
                if (y > g.yMax - 20f) break;
                var cls = ClassDef.Get(p.skin.cls).heroName;
                Gui.Text(new Rect(x, y, w, 18f), cls + " · " + p.skin.name + (p.duplicate ? "  (dup +20)" : "  NEW!"), 12, Skin.RarityColor(p.skin.rarity), TextAnchor.MiddleLeft, false);
                y += 18f;
            }
        }

        void DoPull(int n, FighterClass? banner)
        {
            lastPull = Save.Pull(n, banner);
            foreach (var p in lastPull)
                if (p.skin.rarity >= Rarity.Epic && !p.duplicate)
                    Toast(p.skin.rarity.ToString().ToUpper() + "! " + ClassDef.Get(p.skin.cls).heroName + " · " + p.skin.name);
        }

        // ---------------------------------------------------------------- SETTINGS

        void SettingsTab(Rect a)
        {
            var s = Save.settings;
            var p = new Rect(a.x, a.y, 620f, a.height);
            Gui.Rect(p, Gui.Panel, 16f);
            float x = p.x + 24f, y = p.y + 20f;

            Gui.Text(new Rect(x, y, 200f, 40f), "Match time", 16, Color.white, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(x + 220f, y, 350f, 40f), MatchController.MatchSeconds + " seconds  <size=12><color=#b5aed0>(fixed for every match)</color></size>", 16, Color.white, TextAnchor.MiddleLeft);
            y += 56f;

            Gui.Text(new Rect(x, y, 200f, 40f), "CPU difficulty", 16, Color.white, TextAnchor.MiddleLeft);
            for (int i = 1; i <= 3; i++)
                if (Gui.Button(new Rect(x + 220f + (i - 1) * 120f, y, 110f, 40f), Difficulty[i], Gui.Accent2, 15, true, s.cpuDifficulty == i)) s.cpuDifficulty = i;
            y += 56f;

            if (Toggle(x, ref y, "Camera tracking (webcam)", ref s.useWebcam)) WebcamFeed.SetEnabled(s.useWebcam);
            Toggle(x, ref y, "CV overlay in match", ref s.showCvOverlay);
            Toggle(x, ref y, "Pre-match camera check", ref s.preMatchCheck);
            Toggle(x, ref y, "Screen shake", ref s.screenShake);

            y += 10f;
            if (Gui.Button(new Rect(x, y, 260f, 44f), "RESET PROFILE", Gui.Red, 15))
            {
                PlayerPrefs.DeleteKey("ChibiClash.Save.v1");
                Save = SaveData.Load();
                Bindings.Data = Save;
                WebcamFeed.SetEnabled(Save.settings.useWebcam);
                Toast("Profile reset");
            }
            if (GUI.changed) Save.Save();

            var c = new Rect(a.x + 650f, a.y, a.width - 650f, a.height);
            Gui.Rect(c, Gui.Panel, 16f);
            Gui.Text(new Rect(c.x + 20f, c.y + 10f, 300f, 30f), "KEY BINDINGS", 20, Gui.Gold, TextAnchor.MiddleLeft);
            if (Gui.Button(new Rect(c.xMax - 170f, c.y + 10f, 150f, 30f), "RESET KEYS", Gui.Red, 12))
            {
                Bindings.ResetDefaults(Save);
                Save.Save();
                listening = false;
                Toast("Keys reset to defaults");
            }
            float colW = (c.width - 60f) * 0.5f;
            for (int pn = 0; pn < 2; pn++)
            {
                float bx = c.x + 20f + pn * (colW + 20f), by = c.y + 50f;
                var pc = pn == 0 ? Gui.Accent : Gui.Accent2;
                Gui.Text(new Rect(bx, by, colW, 22f), pn == 0 ? "PLAYER 1" : "PLAYER 2", 15, pc, TextAnchor.MiddleLeft);
                by += 26f;
                for (int k = 0; k < Bindings.Count; k++)
                {
                    var act = (BindAction)k;
                    bool mine = listening && listenPlayer == pn && listenAction == act;
                    Gui.Text(new Rect(bx, by, colW - 106f, 26f), Bindings.Names[k], 13, Color.white, TextAnchor.MiddleLeft, false);
                    if (Gui.Button(new Rect(bx + colW - 104f, by, 104f, 26f), mine ? "key or click" : Bindings.Label(pn, act), mine ? Gui.Gold : pc, 12, true, mine))
                    {
                        listening = true; listenSince = Time.unscaledTime; listenPlayer = pn; listenAction = act;
                    }
                    by += 29f;
                }
            }
            Gui.Text(new Rect(c.x + 20f, c.yMax - 52f, c.width - 40f, 46f),
                "Click a binding, then press a key or click a mouse button (Esc cancels). If it's already used, the two actions swap.\n" +
                "Camera poses: cross arms = block · arm out to the side = fireball · both hands up = super jump (keyboard poses: 1/2/3 and 8/9/0).",
                11, Gui.Muted, TextAnchor.UpperLeft, false);
        }

        bool Toggle(float x, ref float y, string label, ref bool value)
        {
            Gui.Text(new Rect(x, y, 260f, 40f), label, 16, Color.white, TextAnchor.MiddleLeft);
            bool changed = false;
            if (Gui.Button(new Rect(x + 340f, y, 110f, 40f), "ON", Gui.Green, 15, true, value) && !value) { value = true; changed = true; }
            if (Gui.Button(new Rect(x + 460f, y, 110f, 40f), "OFF", Gui.Red, 15, true, !value) && value) { value = false; changed = true; }
            y += 52f;
            if (changed) Save.Save();
            return changed;
        }

        // ---------------------------------------------------------------- ONLINE

        /// <summary>The room manager connected us: start the online match (handshake first; the view appears once agreed).</summary>
        void OnRoomConnected(Netcode.ChibiUtpTransport transport, bool isHost)
        {
            if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { transport.Dispose(); rooms.Cancel(); return; }   // age gate, last line
            var input = Netcode.ChibiPlayerInputSource.ForPlayer(0);   // the local player always uses P1's devices / camera
            var settings = Netcode.ChibiRollbackSettings.Default;
            string me = DisplayedName();   // sent in the handshake so the opponent sees our name (cosmetic, not hashed)
            if (roomsSvc.OwnsLink)
            {
                // Room fight: exactly the fighter we locked in with OK, and the room's stage (not whatever another screen left).
                pending.cls[0] = roomsSvc.PickClass;
                pending.passive[0] = roomsSvc.PickPassive;
                pending.stage = roomsSvc.Stage;
            }
            var online = isHost
                ? Netcode.ChibiOnlineMatch.Host(transport, input, pending.cls[0], pending.passive[0], pending.stage,
                                                (uint)System.Environment.TickCount ^ 0xC1B1u, settings,
                                                Netcode.ChibiHandshake.DefaultTimeoutTicks, me)
                : Netcode.ChibiOnlineMatch.Join(transport, input, pending.cls[0], pending.passive[0],
                                                Netcode.ChibiHandshake.DefaultTimeoutTicks, me);
            var go = new GameObject("OnlineMatch");
            var runner = go.AddComponent<Netcode.ChibiOnlineMatchRunner>();
            runner.Begin(online, input);
            pendingOnline = runner;
            pendingOnlineInput = input;
            onlineNote = "Connected. Agreeing on the match...";
        }

        /// <summary>Watch the handshake of a just-connected online match: show the match once it plays, or explain why not.</summary>
        void UpdatePendingOnline()
        {
            if (pendingOnline == null) return;
            var m = pendingOnline.Match;
            if (m.Phase == Netcode.ChibiOnlinePhase.Playing || m.Phase == Netcode.ChibiOnlinePhase.Lingering)
            {
                if (match != null) Destroy(match.gameObject);
                match = ChibiMatch.CreateOnline(this, pendingOnline, pendingOnlineInput, pending.weapon[0]);
                match.SetVerifiedOpponentName(social.MatchFriendName);   // invite match: the friends-list name
                social.ClearMatchFriend();
                pendingOnline = null;
                pendingOnlineInput = null;
                view = View.Match;
                social.SetInMatch(true);
            }
            else if (m.Phase == Netcode.ChibiOnlinePhase.Closed)
            {
                var r = m.Result;
                onlineNote = r.RejectReason == Netcode.ChibiRejectReason.RulesMismatch || r.RejectReason == Netcode.ChibiRejectReason.ProtocolMismatch ||
                             r.RejectReason == Netcode.ChibiRejectReason.ContentMismatch
                    ? "Your opponent runs a different version of the game. Both players need the same build."
                    : "The match could not start (" + r.HandshakeState + ").";
                Destroy(pendingOnline.gameObject);
                pendingOnline = null;
                pendingOnlineInput = null;
                rooms.Cancel();
                if (roomsSvc.OwnsLink) roomsSvc.OnMatchEnded();   // room fight that never started: back to OK
            }
        }

        void OnlineView()
        {
            if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { view = View.Menu; return; }   // age gate (13+), defence in depth
            bool noInternet = Application.internetReachability == NetworkReachability.NotReachable;
            Header(noInternet ? "<color=#ff6b6b>No internet: connect to Wi-Fi or mobile data to play friends anywhere (LAN still works)</color>"
                              : "Online · play friends anywhere over the internet: invite by name, room code or matchmaking");
            var left = new Rect(30f, 104f, (Gui.W - 60f) * 0.5f - 10f, 500f);
            PlayerColumn(0, left);

            var r = new Rect(left.xMax + 20f, 104f, Gui.W - 60f - left.width - 20f, 500f);
            bool busy = rooms.State != Netcode.ChibiRoomState.Idle && rooms.State != Netcode.ChibiRoomState.Failed || pendingOnline != null;

            // Tabs: ROOMS (codes, quick match, LAN) and FRIENDS (name, friend list, invites).
            if (Gui.Button(new Rect(r.xMax - 330f, 62f, 160f, 34f), "ROOMS", Gui.Accent2, 15, true, !showFriends)) showFriends = false;
            if (Gui.Button(new Rect(r.xMax - 160f, 62f, 160f, 34f), "FRIENDS" + (social.Requests.Count > 0 ? " (" + social.Requests.Count + ")" : ""),
                           Gui.Accent, 15, true, showFriends))
            {
                showFriends = true;
                if (!social.Ready && !social.Working) social.Connect();
            }

            Gui.Rect(r, Gui.Panel, 16f);
            Gui.Frame(r, Gui.Accent2, 2f, 16f);
            if (showFriends) FriendsPanel(r, busy);
            else RoomsPanel(r, busy);
            OnlineBottomButtons(busy);
        }

        void RoomsPanel(Rect r, bool busy)
        {
            float x = r.x + 24f, w = r.width - 48f, y = r.y + 16f;
            Gui.Text(new Rect(x, y, w, 26f), "STAGE (the room host's pick is used)", 14, Gui.Gold, TextAnchor.MiddleLeft);
            y += 30f;
            float sw = (w - 20f) / 3f;
            for (int s = 0; s < StageBuilder.Names.Length; s++)
                if (Gui.Button(new Rect(x + s * (sw + 10f), y, sw, 40f), StageBuilder.Names[s], Gui.Gold, 14, !busy, pending.stage == s) && !busy)
                    pending.stage = s;
            y += 60f;

            // Private rooms live on the ROOMS page (named rooms, OK / START FIGHT).
            if (Gui.Button(new Rect(x, y, w, 52f), "MY ROOMS  (create / join a private room)", Gui.Gold, 17, !busy) && !busy)
                OpenRoomsPage();
            y += 64f;

            if (Gui.Button(new Rect(x, y, w, 40f), "MATCHMAKING (closest players)", Gui.Accent2, 18, !busy) && !busy)
            { onlineNote = ""; social.ClearMatchFriend(); rooms.QuickMatch(); }
            // Privacy notice: what matchmaking shares.
            Gui.Text(new Rect(x, y + 41f, w, 18f), "Finds the closest players. Shares your country (from device settings), never your location.",
                     11, Gui.Muted);
            y += 64f;

            // LAN direct play: no Unity services needed (same network). Same transport, handshake and rollback as Relay.
            if (Gui.Button(new Rect(x, y, 150f, 40f), "LAN HOST", Gui.Gold, 15, !busy) && !busy)
            { onlineNote = ""; social.ClearMatchFriend(); rooms.HostLan(); }
            var ipField = new Rect(x + 160f, y, w - 160f - 150f, 40f);
            var ipStyle = new GUIStyle(GUI.skin.textField) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
            if (!busy) lanAddress = GUI.TextField(ipField, lanAddress ?? "", 40, ipStyle);
            else GUI.Label(ipField, lanAddress, ipStyle);
            if (Gui.Button(new Rect(ipField.xMax + 10f, y, 140f, 40f), "LAN JOIN", Gui.Gold, 15, !busy) && !busy)
            { onlineNote = ""; social.ClearMatchFriend(); rooms.JoinLan(lanAddress); }
            y += 58f;

            bool failed = rooms.State == Netcode.ChibiRoomState.Failed;
            string status = onlineNote.Length > 0 ? onlineNote : rooms.Status;
            Gui.Text(new Rect(x, y, w, 60f), status, 15, failed ? Gui.Red : Color.white, TextAnchor.UpperCenter, false);
        }

        /// <summary>FRIENDS tab: our player name, add a friend by name, incoming requests, friends with online dot and INVITE.</summary>
        void FriendsPanel(Rect r, bool busy)
        {
            float x = r.x + 24f, w = r.width - 48f, y = r.y + 16f;
            var field = new GUIStyle(GUI.skin.textField) { fontSize = 18, alignment = TextAnchor.MiddleCenter };

            if (!social.Ready)
            {
                Gui.Text(new Rect(x, y, w, 80f), social.Working ? "Connecting to friends..." : social.Status, 15,
                         social.StatusIsError ? Gui.Red : Color.white, TextAnchor.UpperCenter, false);
                if (!social.Working && Gui.Button(new Rect(x + w * 0.5f - 110f, y + 90f, 220f, 46f), "CONNECT", Gui.Accent2, 16))
                    social.Connect();
                return;
            }

            // Our name ("Kira#4821"): what friends type to add / invite us. Changed with the name badge at the top.
            Gui.Text(new Rect(x, y, w, 30f), "YOU: " + (social.NeedsName ? "(setting your name...)" : social.MyName), 20, Gui.Gold, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(x, y + 28f, w, 20f), "Friends type this name to add or invite you. Change it with the name button at the top.",
                     12, Gui.Muted, TextAnchor.MiddleLeft);
            y += 56f;

            // Type a player's name: ADD FRIEND, or INVITE straight into our room (a friend request goes first if needed).
            var af = new Rect(x, y, w - 300f, 40f);
            friendInput = GUI.TextField(af, friendInput ?? "", 24, field);
            if (Gui.Button(new Rect(af.xMax + 10f, y, 140f, 40f), "ADD FRIEND", Gui.Accent2, 14, !social.Working))
            { social.AddFriend(friendInput); friendInput = ""; }
            if (Gui.Button(new Rect(af.xMax + 155f, y, 135f, 40f), "INVITE", Gui.Accent, 15, !social.Working && !busy) && !busy)
            { onlineNote = ""; social.InviteByName(friendInput); friendInput = ""; }
            y += 44f;
            if (social.WaitingToInviteName.Length > 0)
            {
                Gui.Text(new Rect(x, y, w - 100f, 24f), "Waiting for " + social.WaitingToInviteName + " to accept, then the invite goes out.",
                         12, Gui.Gold, TextAnchor.MiddleLeft, false);
                if (Gui.Button(new Rect(x + w - 90f, y, 90f, 24f), "STOP", Gui.Muted, 11)) social.ForgetWaitingInvite();
            }
            else Gui.Text(new Rect(x, y, w, 24f), "Type a name like Kira#4821.", 12, Gui.Muted, TextAnchor.MiddleLeft);
            y += 30f;

            // Incoming requests.
            foreach (var q in social.Requests)
            {
                if (y > r.yMax - 120f) break;
                Gui.Rect(new Rect(x, y, w, 40f), Gui.PanelLight, 8f);
                Gui.Text(new Rect(x + 12f, y, w - 300f, 40f), q.Name + "  wants to be friends", 14, Color.white, TextAnchor.MiddleLeft, false);
                if (Gui.Button(new Rect(x + w - 282f, y + 4f, 90f, 32f), "ACCEPT", Gui.Green, 13, !social.Working)) social.AcceptRequest(q.Id);
                if (Gui.Button(new Rect(x + w - 186f, y + 4f, 90f, 32f), "DECLINE", Gui.Red, 13, !social.Working)) social.DeclineRequest(q.Id);
                // Block stops further requests, messages and invites from a spammer.
                if (Gui.Button(new Rect(x + w - 90f, y + 4f, 80f, 32f), "BLOCK", Gui.Muted, 12, !social.Working)) social.Block(q.Id);
                y += 46f;
            }

            // Friends.
            Gui.Text(new Rect(x, y, w, 24f), "FRIENDS (" + social.Friends.Count + ")", 14, Gui.Gold, TextAnchor.MiddleLeft);
            y += 28f;
            if (social.Friends.Count == 0)
                Gui.Text(new Rect(x, y, w, 40f), "No friends yet. Add one by name above.", 13, Gui.Muted, TextAnchor.UpperLeft, false);
            foreach (var f in social.Friends)
            {
                if (y > r.yMax - 110f) { Gui.Text(new Rect(x, y, w, 20f), "...", 13, Gui.Muted); break; }
                Gui.Rect(new Rect(x, y, w, 40f), Gui.PanelLight, 8f);
                Gui.Rect(new Rect(x + 12f, y + 15f, 10f, 10f), f.Online ? Gui.Green : Gui.Muted, 5f);
                Gui.Text(new Rect(x + 30f, y, w - 280f, 40f), f.Name + (f.Online ? "" : "  (offline)"), 14, Color.white, TextAnchor.MiddleLeft, false);
                if (Gui.Button(new Rect(x + w - 240f, y + 4f, 130f, 32f), "INVITE", Gui.Accent, 13, f.Online && !busy && !social.Working)
                    && f.Online && !busy)
                { onlineNote = ""; social.SendInvite(f.Id); }
                // Two-click remove: the first click arms "SURE?" for 3 seconds.
                bool armed = removeArmedId == f.Id && Time.unscaledTime < removeArmedUntil;
                if (Gui.Button(new Rect(x + w - 102f, y + 4f, 90f, 32f), armed ? "SURE?" : "REMOVE", armed ? Gui.Red : Gui.Muted, 12, !social.Working))
                {
                    if (armed) { social.RemoveFriend(f.Id); removeArmedId = null; }
                    else { removeArmedId = f.Id; removeArmedUntil = Time.unscaledTime + 3f; }
                }
                y += 46f;
            }

            if (roomsSvc.Current != null)
                Gui.Text(new Rect(x, r.yMax - 96f, w, 24f), "INVITE puts friends into your room \"" + roomsSvc.Current.Name + "\" (" + roomsSvc.Current.Code + ").", 13, Gui.Gold);
            string status = rooms.State == Netcode.ChibiRoomState.Failed ? rooms.Status : social.Status;
            bool err = rooms.State == Netcode.ChibiRoomState.Failed || social.StatusIsError;
            Gui.Text(new Rect(x, r.yMax - 70f, w, 60f), status, 14, err ? Gui.Red : Color.white, TextAnchor.UpperCenter, false);
        }

        /// <summary>Invite pop-up on every menu screen: ACCEPT joins the friend's room, DECLINE tells them. Expires after 60 s.</summary>
        void InvitePopup()
        {
            if (social == null || social.Invites.Count == 0 || !PlayerIdentity.OnlineAllowed(Save.ageBand)) return;
            var inv = social.Invites[0];
            var r = new Rect(Gui.W * 0.5f - 260f, 120f, 520f, 150f);
            Gui.Glow(new Rect(r.x - 30f, r.y - 30f, r.width + 60f, r.height + 60f), new Color(1f, 0.8f, 0.2f, 0.35f));
            Gui.Rect(r, Gui.Panel, 16f);
            Gui.Frame(r, Gui.Gold, 3f, 16f);
            Gui.Text(new Rect(r.x, r.y + 14f, r.width, 34f), "MATCH INVITE", 22, Gui.Gold);
            int left = Mathf.Max(0, Mathf.CeilToInt(inv.ExpiresAt - Time.unscaledTime));
            Gui.Text(new Rect(r.x, r.y + 48f, r.width, 28f), inv.FromName + " wants to fight you  (" + left + "s)", 16, Color.white);
            if (Gui.Button(new Rect(r.x + 60f, r.y + 90f, 180f, 44f), "ACCEPT", Gui.Green, 17))
            {
                pending.cpu[0] = pending.cpu[1] = false;
                pending.training = false;
                onlineNote = "";
                social.AcceptInvite(0);   // joins the friend's room
                view = View.Room;
            }
            if (Gui.Button(new Rect(r.xMax - 240f, r.y + 90f, 180f, 44f), "DECLINE", Gui.Red, 17)) social.DeclineInvite(0);
        }

        // ---------------------------------------------------------------- ROOMS (create room mode)

        /// <summary>MATCHMAKING block: open the online screen and start searching at once (pick a fighter while it searches).</summary>
        void StartMatchmaking()
        {
            pending.cpu[0] = pending.cpu[1] = false;
            pending.training = false;
            onlineNote = "";
            showFriends = false;
            view = View.Online;
            social.ClearMatchFriend();
            bool busy = rooms.State != Netcode.ChibiRoomState.Idle && rooms.State != Netcode.ChibiRoomState.Failed || pendingOnline != null;
            if (!busy) rooms.QuickMatch();
        }

        void OpenRoomsPage()
        {
            pending.cpu[0] = pending.cpu[1] = false;
            pending.training = false;
            onlineNote = "";
            roomDialog = false;
            view = View.Rooms;
            roomsSvc.Refresh(force: true);
        }

        /// <summary>
        /// ROOMS page. No rooms yet: one big CREATE ROOM in the middle (and join by code). With rooms: CREATE ROOM moves
        /// to the top-right corner and every room we are in is a block; click a block to go in.
        /// </summary>
        void RoomsView()
        {
            Header("Rooms · your own rooms with friends");
            var codeStyle = new GUIStyle(GUI.skin.textField) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
            bool working = roomsSvc.Working;
            bool any = roomsSvc.MyRooms.Count > 0;

            if (!any)
            {
                if (working && !roomsSvc.ListLoaded)
                    Gui.Text(new Rect(0f, Gui.H * 0.5f - 60f, Gui.W, 40f), "Loading your rooms...", 18, Gui.Muted);
                else
                {
                    var big = new Rect(Gui.W * 0.5f - 230f, Gui.H * 0.5f - 110f, 460f, 100f);
                    Gui.Glow(new Rect(big.x - 30f, big.y - 30f, big.width + 60f, big.height + 60f), new Color(1f, 0.4f, 0.6f, 0.3f));
                    if (Gui.Button(big, "CREATE ROOM", Gui.Accent, 34, !working)) OpenRoomDialog();
                    Gui.Text(new Rect(0f, big.yMax + 14f, Gui.W, 24f), "Name your room, invite a friend, both press OK, then START FIGHT.", 14, Gui.Muted);
                    var cf = new Rect(Gui.W * 0.5f - 230f, big.yMax + 60f, 300f, 44f);
                    Gui.Text(new Rect(cf.x, cf.y - 22f, 300f, 20f), "Or join a friend's room with its code:", 12, Gui.Muted, TextAnchor.MiddleLeft);
                    joinCode = GUI.TextField(cf, joinCode ?? "", 12, codeStyle).ToUpperInvariant();
                    if (Gui.Button(new Rect(cf.xMax + 10f, cf.y, 150f, 44f), "JOIN", Gui.Accent2, 18, !working))
                    { social.ClearMatchFriend(); roomsSvc.JoinByCode(joinCode); view = View.Room; }
                }
            }
            else
            {
                // Top-right: CREATE ROOM, and join by code next to it.
                if (Gui.Button(new Rect(Gui.W - 270f, 104f, 240f, 52f), "+ CREATE ROOM", Gui.Accent, 20, !working)) OpenRoomDialog();
                var cf = new Rect(Gui.W - 600f, 110f, 200f, 40f);
                joinCode = GUI.TextField(cf, joinCode ?? "", 12, codeStyle).ToUpperInvariant();
                if (Gui.Button(new Rect(cf.xMax + 8f, 110f, 110f, 40f), "JOIN", Gui.Accent2, 16, !working))
                { social.ClearMatchFriend(); roomsSvc.JoinByCode(joinCode); view = View.Room; }
                Gui.Text(new Rect(30f, 110f, 500f, 40f), "YOUR ROOMS (" + roomsSvc.MyRooms.Count + ")  ·  click a room to go in", 16, Gui.Gold, TextAnchor.MiddleLeft);

                // Room blocks, 3 per row.
                float bw = (Gui.W - 60f - 40f) / 3f, bh = 150f;
                for (int k = 0; k < roomsSvc.MyRooms.Count; k++)
                {
                    var room = roomsSvc.MyRooms[k];
                    var b = new Rect(30f + (k % 3) * (bw + 20f), 170f + (k / 3) * (bh + 18f), bw, bh);
                    if (b.yMax > Gui.H - 130f) break;
                    bool hover = b.Contains(Event.current.mousePosition);
                    if (hover) Gui.Glow(new Rect(b.x - 16f, b.y - 16f, b.width + 32f, b.height + 32f), new Color(1f, 0.8f, 0.3f, 0.3f));
                    Gui.Rect(b, hover ? Gui.PanelLight : Gui.Panel, 16f);
                    Gui.Frame(b, Gui.Gold, hover ? 3f : 2f, 16f);
                    Gui.Text(new Rect(b.x + 18f, b.y + 12f, b.width - 36f, 34f), room.Name, 22, Color.white, TextAnchor.MiddleLeft);
                    Gui.Text(new Rect(b.x + 18f, b.y + 46f, b.width - 36f, 22f), "CODE  " + room.Code, 14, Gui.Gold, TextAnchor.MiddleLeft);
                    string who = room.Slots.Count == 0 ? "empty" : room.Slots[0].Name + (room.Slots.Count > 1 ? "  vs  " + room.Slots[1].Name : "  ·  waiting for a friend");
                    Gui.Text(new Rect(b.x + 18f, b.y + 72f, b.width - 36f, 24f), who, 15, Color.white, TextAnchor.MiddleLeft);
                    Gui.Text(new Rect(b.x + 18f, b.y + 100f, b.width - 36f, 22f),
                             room.Slots.Count + " / 2 players" + (room.HostId == roomsSvc.MyId ? "  ·  YOU HOST" : ""), 13, Gui.Muted, TextAnchor.MiddleLeft);
                    if (GUI.Button(b, GUIContent.none, GUIStyle.none)) { roomsSvc.Enter(room.Id); view = View.Room; }
                }
            }

            Gui.Text(new Rect(30f, Gui.H - 160f, Gui.W - 60f, 40f), roomsSvc.Status, 15, roomsSvc.StatusIsError ? Gui.Red : Color.white,
                     TextAnchor.UpperCenter, false);
            float by = Gui.H - 110f;
            if (Gui.Button(new Rect(30f, by - 6f, 180f, 56f), "REFRESH", Gui.Accent2, 18, !working)) roomsSvc.Refresh(force: true);
            if (Gui.Button(new Rect(Gui.W - 270f, by - 6f, 240f, 56f), "BACK", Gui.Muted, 20)) view = View.Menu;
        }

        /// <summary>
        /// Inside a room: choose a fighter (left), press OK to be "in". START FIGHT works only when both players are in;
        /// either one may press it and the fight starts for both. After the fight both come back here.
        /// </summary>
        void RoomView()
        {
            var cur = roomsSvc.Current;
            if (cur == null)
            {
                if (!roomsSvc.Working) { view = View.Rooms; return; }   // join / create failed: the status shows why
                Header("Rooms");
                Gui.Text(new Rect(0f, Gui.H * 0.5f - 40f, Gui.W, 40f), roomsSvc.Status.Length > 0 ? roomsSvc.Status : "Opening the room...", 18, Color.white);
                return;
            }
            Header("Room · " + cur.Name);
            string me = roomsSvc.MyId;
            bool host = roomsSvc.IsHost, ready = roomsSvc.LocalReady, launching = roomsSvc.Launching || pendingOnline != null;

            // Left: our fighter. Locked once OK is pressed (CHANGE unlocks).
            var left = new Rect(30f, 104f, (Gui.W - 60f) * 0.5f - 10f, 500f);
            var keepCls = pending.cls[0]; int keepPassive = pending.passive[0], keepWeapon = pending.weapon[0];
            PlayerColumn(0, left);
            if (ready || launching) { pending.cls[0] = keepCls; pending.passive[0] = keepPassive; pending.weapon[0] = keepWeapon; }
            if (ready) Gui.Text(new Rect(left.x, left.y + 8f, left.width - 20f, 30f), "LOCKED IN  ", 16, Gui.Green, TextAnchor.MiddleRight);
            roomsSvc.SetPick(pending.cls[0], pending.passive[0]);
            if (host) pending.stage = cur.Stage;   // the handshake uses the host's stage

            // Right: the room.
            var r = new Rect(left.xMax + 20f, 104f, Gui.W - 60f - left.width - 20f, 500f);
            Gui.Rect(r, Gui.Panel, 16f);
            Gui.Frame(r, Gui.Gold, 2f, 16f);
            float x = r.x + 24f, w = r.width - 48f, y = r.y + 14f;
            Gui.Text(new Rect(x, y, w, 34f), cur.Name, 26, Color.white, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(x, y, w, 34f), "CODE  " + cur.Code, 18, Gui.Gold, TextAnchor.MiddleRight);
            y += 40f;
            Gui.Text(new Rect(x, y, w, 18f), "Share the code, or INVITE a friend by name.", 12, Gui.Muted, TextAnchor.MiddleLeft);
            y += 26f;

            for (int i = 0; i < Netcode.ChibiRoomRules.Seats; i++)
            {
                var row = new Rect(x, y, w, 62f);
                Gui.Rect(row, Gui.PanelLight, 10f);
                if (i < cur.Slots.Count)
                {
                    var slot = cur.Slots[i];
                    var d = ClassDef.Get(slot.Class);
                    string tag = (i == 0 ? "HOST" : "GUEST") + (slot.PlayerId == me ? " · YOU" : "");
                    Gui.Text(new Rect(row.x + 14f, row.y + 6f, w - 180f, 26f), slot.Name, 20, Color.white, TextAnchor.MiddleLeft);
                    Gui.Text(new Rect(row.x + 14f, row.y + 32f, w - 180f, 22f), tag + "  ·  " + d.heroName + " (" + d.className + ")", 13, Gui.Muted, TextAnchor.MiddleLeft);
                    Gui.Text(new Rect(row.xMax - 170f, row.y, 156f, 62f), slot.Ready ? "READY" : "choosing...", slot.Ready ? 20 : 15,
                             slot.Ready ? Gui.Green : Gui.Muted, TextAnchor.MiddleRight);
                }
                else Gui.Text(new Rect(row.x + 14f, row.y, w - 28f, 62f), "Empty seat · waiting for a friend to join", 15, Gui.Muted, TextAnchor.MiddleLeft);
                y += 70f;
            }

            // Stage (host chooses; the guest sees it).
            Gui.Text(new Rect(x, y, 200f, 24f), host ? "STAGE (you choose)" : "STAGE (host chooses)", 13, Gui.Gold, TextAnchor.MiddleLeft);
            y += 26f;
            float sw = (w - 20f) / 3f;
            for (int st = 0; st < StageBuilder.Names.Length; st++)
                if (Gui.Button(new Rect(x + st * (sw + 10f), y, sw, 36f), StageBuilder.Names[st], Gui.Gold, 13, host && !ready && !launching, cur.Stage == st)
                    && host && !ready && !launching)
                    roomsSvc.SetStage(st);
            y += 50f;

            // OK / CHANGE, then START FIGHT (only when both are ready; either player may press it).
            if (!ready)
            {
                bool canOk = !roomsSvc.Working && !launching && roomsSvc.Synced;
                if (Gui.Button(new Rect(x, y, w * 0.4f, 56f), roomsSvc.Synced ? "OK" : "LOADING...", Gui.Green, 22, canOk) && canOk) roomsSvc.SetReady(true);
            }
            else if (Gui.Button(new Rect(x, y, w * 0.4f, 56f), "CHANGE", Gui.Muted, 18, !roomsSvc.Working && !launching)) roomsSvc.SetReady(false);
            bool canStart = Netcode.ChibiRoomRules.CanStart(cur) && !launching;
            if (canStart) Gui.Glow(new Rect(x + w * 0.42f - 20f, y - 20f, w * 0.58f + 40f, 96f), new Color(1f, 0.3f, 0.4f, 0.35f));
            if (Gui.Button(new Rect(x + w * 0.42f, y, w * 0.58f, 56f), launching ? "STARTING..." : "START FIGHT", Gui.Accent, 22, canStart) && canStart)
            {
                onlineNote = "";
                social.ClearMatchFriend();
                roomsSvc.PressStart();
            }
            y += 64f;
            string hint = Netcode.ChibiRoomRules.WaitingFor(cur, me);
            string status = onlineNote.Length > 0 ? onlineNote : roomsSvc.Status;
            if (!launching && hint.Length > 0 && !roomsSvc.StatusIsError && onlineNote.Length == 0) status = hint;
            Gui.Text(new Rect(x, y, w, 50f), status, 14, roomsSvc.StatusIsError || onlineNote.Length > 0 ? Gui.Red : Color.white, TextAnchor.UpperCenter, false);

            // Bottom: invite friends (FRIENDS tab keeps this room), leave for good, or back to the room list.
            float by = Gui.H - 110f;
            if (Gui.Button(new Rect(30f, by - 6f, 230f, 56f), "INVITE FRIENDS", Gui.Accent2, 18, !launching))
            {
                showFriends = true;
                view = View.Online;
                if (!social.Ready && !social.Working) social.Connect();
            }
            bool armed = leaveArmedId == cur.Id && Time.unscaledTime < leaveArmedUntil;
            if (Gui.Button(new Rect(Gui.W - 470f, by - 6f, 180f, 56f), armed ? "SURE?" : host ? "CLOSE ROOM" : "LEAVE ROOM", Gui.Red, 16, !launching))
            {
                if (armed) { roomsSvc.Leave(cur.Id); leaveArmedId = null; view = View.Rooms; }
                else { leaveArmedId = cur.Id; leaveArmedUntil = Time.unscaledTime + 3f; }
            }
            if (Gui.Button(new Rect(Gui.W - 270f, by - 6f, 240f, 56f), "BACK", Gui.Muted, 20, !launching))
            {
                roomsSvc.ExitToList();
                view = View.Rooms;
            }
        }

        void OnlineBottomButtons(bool busy)
        {
            float by = Gui.H - 110f;
            if (busy && Gui.Button(new Rect(Gui.W - 470f, by - 6f, 180f, 56f), "CANCEL", Gui.Red, 18))
            {
                if (pendingOnline != null) { Destroy(pendingOnline.gameObject); pendingOnline = null; pendingOnlineInput = null; }
                social.WithdrawInvite();   // the invited friend's pop-up disappears instead of pointing at a dead room
                rooms.Cancel();
                onlineNote = "";
            }
            bool fromRoom = roomsSvc.Current != null && !busy;
            if (Gui.Button(new Rect(Gui.W - 270f, by - 6f, 240f, 56f), fromRoom ? "BACK TO ROOM" : "BACK", Gui.Muted, 20))
            {
                if (fromRoom) { view = View.Room; return; }   // the room and its invites stay
                if (pendingOnline != null) { Destroy(pendingOnline.gameObject); pendingOnline = null; pendingOnlineInput = null; }
                social.WithdrawInvite();
                rooms.Cancel();
                view = View.Menu;
            }
        }

        // ---------------------------------------------------------------- CHARACTER SELECT

        void SelectView()
        {
            bool vsAi = VsAi;
            Header(pending.training ? "Training · pick your fighter" : vsAi ? "VS AI · pick your fighter (the AI picks its own)" : "Local Versus · pick your fighters");
            for (int i = 0; i < 2; i++)
            {
                var col = new Rect(30f + i * (Gui.W - 60f) * 0.5f + (i == 1 ? 10f : 0f), 104f, (Gui.W - 60f) * 0.5f - 10f, 500f);
                if (i == 1 && vsAi) AiColumn(col);
                else PlayerColumn(i, col);
            }

            float y = Gui.H - 110f;
            Gui.Text(new Rect(30f, y, 100f, 44f), "STAGE", 18, Gui.Gold, TextAnchor.MiddleLeft);
            for (int s = 0; s < StageBuilder.Names.Length; s++)
                if (Gui.Button(new Rect(120f + s * 190f, y, 180f, 44f), StageBuilder.Names[s], Gui.Gold, 15, true, pending.stage == s))
                    pending.stage = s;

            if (Gui.Button(new Rect(Gui.W - 470f, y - 6f, 180f, 56f), "BACK", Gui.Muted, 18)) view = View.Menu;
            // VS AI runs entirely on this device: no internet, no sign-in, nothing online is touched.
            if (Gui.Button(new Rect(Gui.W - 270f, y - 6f, 240f, 56f), pending.training ? "START TRAINING" : vsAi ? "START BATTLE" : "FIGHT!", Gui.Accent, 22))
                Confirm();
        }

        void PlayerColumn(int i, Rect r)
        {
            bool cpu = pending.cpu[i];
            Gui.Rect(r, Gui.Panel, 16f);
            var pc = i == 0 ? Gui.Accent : Gui.Accent2;
            Gui.Frame(r, pc, 2f, 16f);
            string myName = Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);
            string who = cpu ? (pending.training ? "TRAINING DUMMY" : "CPU · " + Difficulty[Save.settings.cpuDifficulty])
                       : i == 0 && myName.Length > 0 ? myName : "PLAYER " + (i + 1);   // P1 is the profile owner
            Gui.Text(new Rect(r.x + 18f, r.y + 8f, 300f, 30f), who, 20, pc, TextAnchor.MiddleLeft);
            if (cpu && Gui.Button(new Rect(r.xMax - 128f, r.y + 8f, 110f, 30f), "RANDOM", pc, 13))
            {
                pending.cls[i] = (FighterClass)Random.Range(0, 5);
                pending.passive[i] = Random.Range(0, 2);
                pending.weapon[i] = Random.Range(0, WeaponDef.ForClass(pending.cls[i]).Count);
            }

            float bw = (r.width - 36f - 16f) / 5f;
            for (int k = 0; k < 5; k++)
            {
                var d = ClassDef.All[k];
                if (Gui.Button(new Rect(r.x + 18f + k * (bw + 4f), r.y + 46f, bw, 40f), d.heroName, d.color, 14, true, pending.cls[i] == d.type))
                {
                    pending.cls[i] = d.type;
                    pending.passive[i] = 0;
                    pending.weapon[i] = 0;
                }
            }

            var def = ClassDef.Get(pending.cls[i]);
            var skin = Save.Equipped(def.type);
            var weapon = WeaponDef.Get(def.type, pending.weapon[i]);
            float x = r.x + 18f, w = r.width - 36f, y = r.y + 98f;
            Gui.Pfp(new Rect(x, y, 96f, 96f), def, skin);
            Gui.Text(new Rect(x + 110f, y, w - 110f, 30f), def.heroName.ToUpper() + "  <size=14><color=#b5aed0>" + def.className + " · " + weapon.name + " · skin: " + skin.name + "</color></size>", 22, Color.white, TextAnchor.MiddleLeft);
            StatBar(x + 110f, y + 34f, "ATK", def.attack, 40f, def.attack.ToString("0"));
            StatBar(x + 110f, y + 52f, "HP", def.health, 600f, def.health.ToString("0"));
            StatBar(x + 110f, y + 70f, "DEF", def.defense, 30f, def.defense.ToString("0") + "  (-" + Mathf.FloorToInt(def.defense / 10f) + " dmg/hit, blocks " + Mathf.RoundToInt((1f - CombatAuthority.BlockedFraction(def.defense)) * 100f) + "%)");
            Gui.Text(new Rect(x + 110f, y + 86f, w - 110f, 18f), "Crit " + Mathf.RoundToInt(def.critRate * 100f) + "% ×" + (1f + def.critDmg).ToString("0.0") + "  ·  " + def.patternLength + "-hit attack pattern  ·  speed " + def.moveSpeed.ToString("0.0"), 11, Gui.Muted, TextAnchor.MiddleLeft, false);
            if (weapon.Sprite != null)
                GUI.DrawTexture(new Rect(x + w - 58f, y - 2f, 54f, 106f), weapon.Sprite.texture, ScaleMode.ScaleToFit);
            y += 110f;

            var variants = WeaponDef.ForClass(def.type);
            if (variants.Count > 1)
            {
                Gui.Text(new Rect(x, y, 200f, 28f), "WEAPON (cosmetic)", 13, Gui.Gold, TextAnchor.MiddleLeft);
                float bw2 = (w - 210f) / variants.Count;
                for (int v = 0; v < variants.Count; v++)
                    if (Gui.Button(new Rect(x + 210f + v * bw2, y, bw2 - 4f, 28f), variants[v].name, def.color, 12, true, pending.weapon[i] == v))
                        pending.weapon[i] = v;
                y += 34f;
            }

            Gui.Text(new Rect(x, y, w, 20f), "PASSIVE (choose 1)", 13, Gui.Gold, TextAnchor.MiddleLeft);
            y += 22f;
            for (int p = 0; p < 2; p++)
            {
                var pr = new Rect(x + p * (w * 0.5f + 4f), y, w * 0.5f - 4f, 54f);
                bool sel = pending.passive[i] == p;
                if (Gui.Button(pr, "", def.color, 12, true, sel)) pending.passive[i] = p;
                var tc = sel ? new Color(0.08f, 0.06f, 0.12f) : Color.white;
                Gui.Text(new Rect(pr.x + 8f, pr.y + 4f, pr.width - 16f, 20f), def.passiveNames[p], 14, tc, TextAnchor.MiddleCenter, true, false);
                Gui.Text(new Rect(pr.x + 8f, pr.y + 24f, pr.width - 16f, 28f), def.passiveDescs[p], 11, tc, TextAnchor.UpperCenter, false, false);
            }
            y += 64f;

            Gui.Text(new Rect(x, y, w, 20f), "SKILLS", 13, Gui.Gold, TextAnchor.MiddleLeft);
            y += 22f;
            for (int s = 0; s < def.SkillCount; s++)
            {
                string key = cpu ? "•" : Bindings.Label(i, (BindAction)((int)BindAction.Skill1 + s));
                Gui.Text(new Rect(x, y, w, 20f), "<b>" + key + "  " + def.skillNames[s] + "</b>  <color=#b5aed0>" + def.skillDescs[s] + " · " + def.skillCooldowns[s] + "s</color>", 13, Color.white, TextAnchor.MiddleLeft, false);
                y += 22f;
            }
            y += 6f;
            Gui.Text(new Rect(x, y, w, 20f), "ULTIMATE", 13, Gui.Gold, TextAnchor.MiddleLeft);
            y += 22f;
            Gui.Text(new Rect(x, y, w, 40f), "<b>" + def.ultName + "</b> - " + def.ultDesc + "\n<color=#5fdcff>Gesture: " + ClassDef.GestureName(def.ultGesture) + "</color>", 13, Color.white, TextAnchor.UpperLeft, false);
        }

        void StatBar(float x, float y, string label, float v, float max, string text)
        {
            Gui.Text(new Rect(x, y, 40f, 16f), label, 11, Gui.Muted, TextAnchor.MiddleLeft);
            Gui.Bar(new Rect(x + 40f, y + 3f, 220f, 10f), v / max, Gui.Accent2, new Color(0f, 0f, 0f, 0.4f));
            Gui.Text(new Rect(x + 270f, y, 200f, 16f), text, 11, Color.white, TextAnchor.MiddleLeft, false);
        }

        // ---------------------------------------------------------------- PRE-MATCH CHECK

        void PreCheckView()
        {
            Header("Pre-match camera check");
            var feed = new Rect(Gui.W * 0.5f - 420f, 120f, 440f, 340f);
            bool ok = lightOk && frameOk;
            Gui.CvPanel(feed, menuTracker, preCheckT < 5f ? Gui.Yellow : ok ? Gui.Green : Gui.Red, Save.settings.useWebcam, "CAMERA CHECK");

            var p = new Rect(feed.xMax + 20f, 120f, 380f, 340f);
            Gui.Rect(p, Gui.Panel, 16f);
            float x = p.x + 20f, w = p.width - 40f;
            Gui.Text(new Rect(x, p.y + 14f, w, 30f), preCheckT < 5f ? "CHECKING... " + Mathf.CeilToInt(5f - preCheckT) : ok ? "ALL CHECKS PASSED" : "CHECK FAILED", 22, preCheckT < 5f ? Gui.Yellow : ok ? Gui.Green : Gui.Red, TextAnchor.MiddleLeft);
            Gui.Bar(new Rect(x, p.y + 52f, w, 10f), preCheckT / 5f, preCheckT < 5f ? Gui.Yellow : ok ? Gui.Green : Gui.Red, new Color(0f, 0f, 0f, 0.4f));
            Check(x, p.y + 80f, w, "Lighting", lightOk, lightMsg);
            Check(x, p.y + 140f, w, "Framing", frameOk, frameMsg);
            Check(x, p.y + 200f, w, "Posture", menuTracker.Posture >= 0.6f, menuTracker.Posture >= 0.6f ? "Nice and upright" : "Sit up straight for bonus coins");

            if (preCheckT >= 5f && !ok)
            {
                if (Gui.Button(new Rect(x, p.yMax - 60f, 160f, 44f), "RETRY", Gui.Yellow, 16)) preCheckT = 0f;
                if (Gui.Button(new Rect(x + 180f, p.yMax - 60f, 160f, 44f), "PLAY ANYWAY", Gui.Muted, 14)) StartMatch();
            }
            if (Gui.Button(new Rect(Gui.W * 0.5f - 90f, 490f, 180f, 48f), "BACK", Gui.Muted, 16)) view = View.Select;
            Gui.Text(new Rect(0f, 550f, Gui.W, 40f), "Ranked queues require this check to pass. Casual matches may skip it.", 13, Gui.Muted, TextAnchor.MiddleCenter, false);
        }

        void Check(float x, float y, float w, string label, bool ok, string msg)
        {
            Gui.Circle(new Rect(x, y + 4f, 18f, 18f), preCheckT < 5f && !ok ? Gui.Yellow : ok ? Gui.Green : Gui.Red);
            Gui.Text(new Rect(x + 28f, y, w - 28f, 26f), label, 17, Color.white, TextAnchor.MiddleLeft);
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
