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
    ///   GameManager.Menus.cs   loading, home, nav bar, Play (Vs Online / Matchmaking / Vs CPU), the Vs Online
    ///                          play-style screen, Arcade / Training / Collection / Settings
    ///   GameManager.Select.cs  character select (roster grid, side panels, loadout) and the stage carousel
    ///   GameManager.Online.cs  onboarding (age, name, account), the Matchmaking rooms pages (rooms, room, create / join
    ///                          pop-ups) and the Vs Online quick match page
    /// </summary>
    public partial class GameManager : MonoBehaviour
    {
        enum Page { Loading, Home, Play, PlayStyle, Training, Arcade, Collection, Settings, Select, PreCheck, Match, Rooms, Room, QuickMatch }

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
        float listenEndedAt = -10f;        // the click that ended listening must not re-open it on release
        bool listenPointerOnRow;           // mouse is over the row being rebound (set while drawing Settings)
        float resetProfileArmedUntil, resetKeysArmedUntil;   // two-click RESET PROFILE / RESET KEYS (item 22)
        /// <summary>The second click arms for this long; it is ignored for the first ResetGraceSeconds (a double-click is not a yes).</summary>
        const float ResetArmSeconds = 3f, ResetGraceSeconds = 0.4f;
        bool listenClickLatch;             // a mouse click ended listening: no row click until every button is up again
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
        /// <summary>Optional work run once per frame while the wipe covers the screen; the wipe holds until it returns true.</summary>
        Func<bool> wipePrepare;
        bool wipeSwapped;
        string wipeCaption;
        /// <summary>Behind-the-wipe preload of the next offline match (null when none is running).</summary>
        MatchAssetLoader matchLoad;

        // arcade ladder: all five characters, one each, in a random order
        const int ArcadeRounds = 5;
        bool arcade;
        int arcadeRound;
        readonly FighterClass[] arcadeLadder = new FighterClass[ArcadeRounds];

        // Vs CPU levels 1-3 are easy / medium / hard (the user's words); 0 is the training dummy.
        static readonly string[] Difficulty = { "difficulty.dummy", "difficulty.easy", "difficulty.medium", "difficulty.hard" };   // Loc keys
        static readonly string[] DifficultyUpper = { "difficulty.dummy_upper", "difficulty.easy_upper", "difficulty.medium_upper", "difficulty.hard_upper" };   // Loc keys

        static float W => Gui.W;
        static float H => Gui.H;
        float Since => Time.unscaledTime - pageSince;

        void Awake()
        {
            Save = SaveData.Load();
            // DEV ONLY (-devName, ChibiDevFlags; inert in release): a name for this run that is never saved (exes share PlayerPrefs).
            string devName = Netcode.ChibiDevFlags.DevName;
            if (devName != null) { Save.diskPlayerName = Save.playerName ?? ""; Save.playerName = devName; }
            Bindings.Data = Save;
            // UI language (per device): the saved choice, else the device's own language until the player picks one.
            Loc.Use(Save.language.Length > 0 ? Save.language : Loc.DeviceLanguage());
            // Perf audit P4 #6: every widget is GUI.* (no GUILayout / GUI.Window anywhere), so skip the Layout event pass;
            // halves OnGUI calls per frame. GUI.TextField / FocusControl do not need it (verify text fields in Play Mode).
            useGUILayout = false;
            // Frame pacing (perf audit 2026-11-11, docs/performance/2026-11-11-smoothness-audit.md F1): present on every
            // display refresh (60/120/144/165 Hz) whatever quality level is active (Very Low / Low ship vSyncCount 0).
            // targetFrameRate is ignored while vSync is on; it only caps the frame rate at the refresh rate when a GPU
            // driver forces vSync off (the old fixed 120 tore on 60 Hz and judders 120-on-144 Hz).
            QualitySettings.vSyncCount = 1;
            Application.targetFrameRate = Mathf.Max(60, Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value));
            menuInput = new HumanInput(0);
            var sim = new SimulatedPoseSource("P1 simulated", () => menuInput.SimulatedGesture, () => menuInput.Slouching);
            menuPose = new AutoPoseSource(0, sim, () => Save.settings.useWebcam);
            menuTracker = new GestureTracker(menuPose);
            rooms = gameObject.AddComponent<Netcode.NetworkRoomManager>();
            rooms.OnConnected += OnRoomConnected;
            rooms.OnGroupConnected += OnRoomGroupConnected;
            social = gameObject.AddComponent<Netcode.ChibiSocialManager>();
            account = gameObject.AddComponent<Netcode.ChibiAccount>();
            account.OnlineAllowed = () => PlayerIdentity.OnlineAllowed(Save.ageBand);
            account.AccountChanged += OnAccountChanged;
            roomsSvc = gameObject.AddComponent<Netcode.ChibiRoomService>();
            roomsSvc.Bind(rooms);
            // Worldwide-unique names: every online flow claims our name first, at the service level (one gate).
            rooms.NameGate = EnsureOnlineNameAsync;
            roomsSvc.NameGate = EnsureOnlineNameAsync;
            roomsSvc.MyName = DisplayedName;
            roomsSvc.OnlineAllowed = () => PlayerIdentity.OnlineAllowed(Save.ageBand);
            roomsSvc.InMatch = () => page == Page.Match || pendingOnline != null;
            roomsSvc.ScreenOpen = () => page == Page.Room;   // room starts only happen while the room screen is shown
            nameInvites = gameObject.AddComponent<Netcode.ChibiNameInvites>();   // INVITE A PLAYER BY NAME (CREATE ROOM rooms)
            nameInvites.Bind(roomsSvc);   // name, age gate and in-match come from the room service
            social.PreferredName = () => Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);
            // Only a name claimed worldwide becomes Unity's public name (dev names skip the claim in editor / dev builds).
            social.NameVerified = n => nameRegistry.IsVerified(n) ||
                                       Netcode.ChibiNameRegistryRules.MaySkipClaim(Application.isEditor, Debug.isDebugBuild, n);
            // Age gate enforced inside both services too, not only by the menu (no side path can start online play).
            rooms.OnlineAllowed = () => PlayerIdentity.OnlineAllowed(Save.ageBand);
            social.OnlineAllowed = () => PlayerIdentity.OnlineAllowed(Save.ageBand);
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
            nameRegistry.ResetVerified();   // another player id: its name must be claimed again before online play
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
                if (page == Page.Rooms || page == Page.Room || page == Page.QuickMatch) Enter(Page.Home);
            }
            // The account has no public name yet but we have one: give it to the account only once the name is claimed
            // worldwide for THIS player id (review 2026-11-23: a new account is a new player id, so the old claim does not
            // count). Otherwise PushClaimedName does it after the name gate's claim succeeds.
            string local = Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);
            if (accountId.Length > 0 && string.IsNullOrEmpty(serverName) && local.Length > 0 && nameRegistry.IsVerified(local))
                account.PushName(local);
            nameDialog = false;   // re-evaluated: NeedsFirstName opens it again when this account has no name
            Toast(accountId.Length > 0 ? (local.Length > 0 ? Loc.T("account.signed_in_as", local) : Loc.T("account.signed_in")) : Loc.T("account.signed_out"));
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
            // One "join" (launch count) per run, once the friends layer has signed in (never races the account restore).
            if (social != null && social.Ready && PlayerIdentity.OnlineAllowed(Save.ageBand)) nameRegistry.JoinOncePerLaunch();
            if (nameInvites != null) nameInvites.Enabled = NameInvitesWanted;   // only on the online menu pages, never in a match
            WatchOnlineSounds();
            if (page == Page.Match)
            {
                MatchInput();
                return;
            }

            if (listenClickLatch && !AnyMouseHeld()) listenClickLatch = false;
            bool wasListening = listening;
            if (listening) CaptureRebind();
            menuInput.Tick(null, Time.unscaledDeltaTime);
            menuTracker.Tick(Time.unscaledDeltaTime);
            if (page == Page.Loading) UpdateLoading();
            if (page == Page.PreCheck) UpdatePreCheck();
            homeSelAnim = Mathf.Lerp(homeSelAnim, homeSel, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 18f));
            stageAnim = Mathf.Lerp(stageAnim, stageTarget, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f));

            if (!DialogOpen) padSubmit = false;
            if (wipeStart >= 0f || wasListening || page == Page.Loading) return;
            if (DialogOpen)
            {
                if (LanguageShown && !PlayerIdentity.NeedsAge(Save.ageBand) && Save.age > 0) PadLanguageInput();
                else PadDialogInput();
                // CREATE ROOM / JOIN ROOM windows: the gamepad moves between their buttons (keys type into the field).
                if (roomDialog || joinDialog) PadDialogNav();
                // Text-entry dialogs own the keyboard; only Back closes the ones that may be closed.
                if (MenuInput.Any(NavCmd.Back))
                {
                    if (langDialog && !NeedsLanguage) { langDialog = false; UiSound.Back(); }
                    else if (roomDialog) { roomDialog = false; UiSound.Back(); }
                    else if (joinDialog) { joinDialog = false; UiSound.Back(); }
                    else if (nameDialog && !NeedsFirstName) { nameDialog = false; UiSound.Back(); }
                }
                return;
            }
            if (GUIUtility.keyboardControl != 0)
            {
                // Typing into a text field (room code, invite name): keys type, Esc leaves the field.
                if (MenuInput.Any(NavCmd.Back)) { GUIUtility.keyboardControl = 0; UiSound.Back(); }
                return;
            }
            if (page == Page.Select) UpdateSelect();
            else MenuNav();
        }

        bool padSubmit;   // a gamepad A / Start asked the open dialog to submit (taken by the dialog in OnGUI)

        /// <summary>
        /// Gamepad driving of the first-launch dialogs (review 2026-11-16 item 18): the age is stepped with the D-pad /
        /// stick (up/down 1, left/right 10), the name is prefilled and Y rerolls it, A submits either.
        /// </summary>
        void PadDialogInput()
        {
            bool ageStep = PlayerIdentity.NeedsAge(Save.ageBand) || Save.age <= 0;
            if (ageStep)
            {
                int d = MenuInput.PadFired(NavCmd.Up) ? 1 : MenuInput.PadFired(NavCmd.Down) ? -1 :
                        MenuInput.PadFired(NavCmd.Right) ? 10 : MenuInput.PadFired(NavCmd.Left) ? -10 : 0;
                if (d != 0) ageInput = PlayerIdentity.StepAge(ageInput, d);
            }
            else if (nameDialog && MenuInput.PadFired(NavCmd.Alt))
                SuggestName();
            if (MenuInput.PadFired(NavCmd.Confirm) && (ageStep || nameDialog)) padSubmit = true;
        }

        static bool AnyMouseHeld()
        {
            var m = Mouse.current;
            return m != null && (m.leftButton.isPressed || m.rightButton.isPressed || m.middleButton.isPressed ||
                                 m.leftButton.wasReleasedThisFrame || m.rightButton.wasReleasedThisFrame || m.middleButton.wasReleasedThisFrame);
        }

        /// <summary>The dialog's submit for a gamepad A press made in Update (consumed once).</summary>
        bool TakePadSubmit()
        {
            if (!padSubmit) return false;
            padSubmit = false;
            return true;
        }

        /// <summary>A modal onboarding / text-entry dialog is on screen (keyboard menu navigation is off).</summary>
        bool DialogOpen => NeedsOnboarding || LanguageShown || nameDialog || roomDialog || joinDialog || NeedsFirstName;

        /// <summary>Gamepad only (the keyboard is typing): D-pad moves the focus, A presses the focused button.</summary>
        static void PadDialogNav()
        {
            if (MenuInput.PadFired(NavCmd.Up)) Gui.Navigate(0, -1);
            if (MenuInput.PadFired(NavCmd.Down)) Gui.Navigate(0, 1);
            if (MenuInput.PadFired(NavCmd.Left)) Gui.Navigate(-1, 0);
            if (MenuInput.PadFired(NavCmd.Right)) Gui.Navigate(1, 0);
            if (MenuInput.PadFired(NavCmd.Confirm)) Gui.Activate();
        }

        /// <summary>Keyboard / gamepad driving of every non-select screen through Gui's spatial focus.</summary>
        void MenuNav()
        {
            if (CollectionNav()) return;   // roster screen: up / down / prev / next step the roster
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
                case Page.PreCheck: UiSound.Back(); Go(Page.Select); return;
                case Page.PlayStyle: UiSound.Back(); Go(Page.Play); return;
                case Page.Rooms: UiSound.Back(); Go(Page.Play); return;            // MATCHMAKING rooms: back to PLAY
                case Page.QuickMatch: UiSound.Back(); OpenPlayStyle(); return;     // stops the search (Enter)
                case Page.Room:
                    if (roomsSvc.Launching || pendingOnline != null) { UiSound.Error(); return; }
                    UiSound.Back();
                    roomsSvc.ExitToList();
                    Go(Page.Rooms);
                    return;
            }
            if (IsNavPage(page))
            {
                UiSound.Back();
                Go(Page.Home);
            }
        }

        // ---------------------------------------------------------------- menu sounds driven by state changes

        bool soundPaused, soundRoomFailed, soundRoomsErr, soundSocialErr, soundInRoom;

        /// <summary>
        /// Sounds for things that happen inside the online services rather than on a click: a failed join or search
        /// (error), entering a room (lock-in), and the pause menu opening / closing (confirm / back).
        /// </summary>
        void WatchOnlineSounds()
        {
            bool paused = page == Page.Match && match != null && match.Paused;
            if (paused != soundPaused) { if (paused) UiSound.Confirm(); else UiSound.Back(); }
            soundPaused = paused;
            if (rooms == null || roomsSvc == null || social == null) return;
            bool failed = rooms.State == Netcode.ChibiRoomState.Failed;
            if (failed && !soundRoomFailed) UiSound.Error();
            soundRoomFailed = failed;
            bool roomsErr = roomsSvc.StatusIsError;
            if (roomsErr && !soundRoomsErr) UiSound.Error();
            soundRoomsErr = roomsErr;
            bool socialErr = social.StatusIsError;
            if (socialErr && !soundSocialErr) UiSound.Error();
            soundSocialErr = socialErr;
            bool inRoom = roomsSvc.Current != null;
            if (inRoom && !soundInRoom) UiSound.LockIn();
            soundInRoom = inRoom;
        }

        /// <summary>A refused action: explain it and play the error sound.</summary>
        void Deny(string message)
        {
            Toast(message);
            UiSound.Error();
        }

        void MatchInput()
        {
            if (match == null || wipeStart >= 0f) return;
            var kb = Keyboard.current;
            bool backspace = kb != null && kb.backspaceKey.wasPressedThisFrame;
            if (match.Online)
            {
                // Online never pauses: Esc / Start open a "LEAVE MATCH?" confirm over the running match (item 21).
                if (match.Finished) match.LeavePrompt = false;
                else if (MenuInput.PadStartPressed() || (kb != null && kb.escapeKey.wasPressedThisFrame))
                {
                    match.LeavePrompt = !match.LeavePrompt;
                    Gui.ResetFocus();
                }
                else if (match.LeavePrompt && (MenuInput.PadBackPressed() || backspace)) match.LeavePrompt = false;
            }
            else if (!match.Finished)
            {
                // Gamepad Start pauses offline matches (Esc is handled by the local loop itself). B / Back / Backspace
                // resume (item 20); Esc is not read here or it would toggle twice.
                if (MenuInput.PadStartPressed())
                {
                    match.SetPaused(!match.Paused);
                    Gui.ResetFocus();
                }
                else if (match.Paused && (MenuInput.PadBackPressed() || backspace)) match.SetPaused(false);
            }
            float sinceEnd = Time.unscaledTime - match.FinishedTime;
            bool results = match.Finished && sinceEnd > 1.3f;
            if (results && !resultsSeen) resultsSeen = true;
            if (!match.Paused && !match.LeavePrompt && !(results && sinceEnd > 1.9f)) return;
            if (MenuInput.Any(NavCmd.Up)) Gui.Navigate(0, -1);
            if (MenuInput.Any(NavCmd.Down)) Gui.Navigate(0, 1);
            if (MenuInput.Any(NavCmd.Left)) Gui.Navigate(-1, 0);
            if (MenuInput.Any(NavCmd.Right)) Gui.Navigate(1, 0);
            if (MenuInput.Any(NavCmd.Confirm)) Gui.Activate();
        }

        /// <summary>
        /// While a binding is selected in Settings, the next key press (or a mouse click on that row) becomes the new
        /// binding. Esc, gamepad B / Back, a click elsewhere or <see cref="RebindRules.TimeoutSeconds"/> cancel (item 16).
        /// </summary>
        void CaptureRebind()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            var f = new RebindFrame
            {
                Elapsed = Time.unscaledTime - listenSince,
                Escape = kb != null && kb.escapeKey.wasPressedThisFrame,
                PadCancel = MenuInput.PadBackPressed(),
                PointerOnRow = listenPointerOnRow,
            };
            if (mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame) f.MouseButton = Bindings.MouseLeft;
                else if (mouse.rightButton.wasPressedThisFrame) f.MouseButton = Bindings.MouseRight;
                else if (mouse.middleButton.wasPressedThisFrame) f.MouseButton = Bindings.MouseMiddle;
            }
            if (kb != null && !f.Escape)
                foreach (var key in kb.allKeys)
                    if (key.wasPressedThisFrame && key.keyCode != Key.None) { f.Key = key.keyCode.ToString(); break; }

            var step = RebindRules.Decide(f, out string bound);
            if (step == RebindStep.Wait) return;
            listening = false;
            listenEndedAt = Time.unscaledTime;
            if (f.MouseButton != null) listenClickLatch = true;
            if (step == RebindStep.Cancel) { UiSound.Back(); return; }
            string who = Loc.T("keys.who", listenPlayer + 1, Loc.T(Bindings.Names[(int)listenAction]));
            string oldKey = Bindings.Get(listenPlayer, listenAction);
            string otherName = "";
            for (int k = 0; k < Bindings.Count; k++)
                if (Bindings.Get(1 - listenPlayer, (BindAction)k) == bound) { otherName = Loc.T(Bindings.Names[k]); break; }
            switch (Bindings.Set(listenPlayer, listenAction, bound))
            {
                case BindResult.Reserved:
                    Deny(Loc.T("keys.reserved", Bindings.BindingLabel(bound)));
                    break;
                case BindResult.SwappedOtherPlayer:
                    Toast(Loc.T("keys.swapped", who + " = " + Bindings.BindingLabel(bound), Loc.T("keys.who", 2 - listenPlayer, otherName),
                          Bindings.BindingLabel(oldKey)));
                    break;
                default:
                    Toast(who + " = " + Bindings.BindingLabel(bound));
                    break;
            }
        }

        void Toast(string s)
        {
            toast = s;
            toastUntil = Time.unscaledTime + 2.5f;
        }

        // ---------------------------------------------------------------- navigation

        static readonly string[] NavNames = { "nav.home", "nav.play", "nav.training", "nav.arcade", "nav.collection", "nav.settings" };   // Loc keys
        static readonly Page[] NavPages = { Page.Home, Page.Play, Page.Training, Page.Arcade, Page.Collection, Page.Settings };

        static bool IsNavPage(Page p) => Array.IndexOf(NavPages, p) >= 0;

        /// <summary>The nav tab a page belongs to (the play-style, quick match and Matchmaking rooms pages light up PLAY).</summary>
        static Page NavOwner(Page p) => p == Page.PlayStyle || p == Page.Rooms || p == Page.Room || p == Page.QuickMatch ? Page.Play : p;

        void CycleTab(int d)
        {
            UiSound.Navigate();
            int i = Array.IndexOf(NavPages, page);
            Go(NavPages[(i + d + NavPages.Length) % NavPages.Length]);
        }

        void Go(Page p)
        {
            if (p != page) Enter(p);
        }

        void Enter(Page p)
        {
            // Leaving the QUICK MATCH page any way (BACK, a nav tab, an accepted invite) stops the search, so a match
            // never starts on another screen. A found match switches to Page.Match directly, not through here.
            if (page == Page.QuickMatch && p != Page.QuickMatch) StopQuickMatch();
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
            UiSound.Confirm();
            exitPrompt = true;
            Gui.ResetFocus();
        }

        void CloseExitPrompt()
        {
            UiSound.Back();
            exitPrompt = false;
            Gui.ResetFocus();
        }

        /// <summary>
        /// Diagonal red / navy / blue wipe; the action runs while the screen is fully covered. With
        /// <paramref name="prepare"/>, the covered wipe first holds (one call per frame) until it returns true, then the
        /// action runs on the next frame, so long loads are spread over covered frames instead of freezing one.
        /// </summary>
        void Wipe(string caption, Action mid, Func<bool> prepare = null)
        {
            if (wipeStart >= 0f) return;
            wipeStart = Time.unscaledTime;
            wipeAction = mid;
            wipePrepare = prepare;
            wipeSwapped = false;
            wipeCaption = caption;
        }

        void UpdateWipe()
        {
            if (wipeStart < 0f) return;
            float t = (Time.unscaledTime - wipeStart) / WipeTime;
            if (!wipeSwapped && t >= 0.5f && wipePrepare != null)
            {
                // Hold the wipe fully covered (t = 0.5) while the preparation runs, one budgeted slice per frame. The
                // action waits for the next frame so that frame does not pay the last slice and the match build together.
                if (wipePrepare()) wipePrepare = null;
                wipeStart = Time.unscaledTime - 0.5f * WipeTime;
                return;
            }
            if (!wipeSwapped && t >= 0.5f)
            {
                wipeSwapped = true;
                var a = wipeAction;
                wipeAction = null;
                float t0 = Time.realtimeSinceStartup;
                a?.Invoke();
                // Perf: the action may block (match build + asset preload, ~0.2-0.6 s). Push the wipe's clock forward by
                // that time so the reveal half still plays in full instead of the wipe vanishing on the next frame.
                if (wipeStart >= 0f) wipeStart += Time.realtimeSinceStartup - t0;
            }
            if (t >= 1f) wipeStart = -1f;
        }

        void CancelWipe()
        {
            wipeStart = -1f;
            wipeAction = null;
            wipePrepare = null;
            matchLoad = null;
        }

        // ---------------------------------------------------------------- flow

        void StartMatch()
        {
            pending.timeLimit = MatchController.MatchSeconds;
            pending.difficulty = Save.settings.cpuDifficulty;
            // Old match first, then its unused sheets, then the new pair: never more than two characters resident.
            // After PrepareMatch (the wipe path) all three are already done and Create's preload is a no-op.
            TearDownMatch();
            ChibiMatch.ReleaseUnusedSheets(pending.cls[0], pending.cls[1]);
            matchLoad = null;
            match = ChibiMatch.Create(this, pending);
            page = Page.Match;
            pageSince = Time.unscaledTime;
            resultsSeen = false;
            exitPrompt = false;
            Gui.ResetFocus();
        }

        string VersusCaption() => Loc.T("match.versus", Hud.CharNameUpper(pending.cls[0]), Hud.CharNameUpper(pending.cls[1]));

        /// <summary>VS AI picks its opponent here (so the wipe caption shows it), then the wipe starts the match.</summary>
        void LaunchMatch()
        {
            PickAiFighter();   // VS AI: a new random opponent every fight (rematches too)
            Wipe(VersusCaption(), StartMatch, PrepareMatch);
        }

        /// <summary>
        /// Runs once per covered wipe frame before <see cref="StartMatch"/>. First frame: destroy the old match now (not
        /// deferred) and release every character not in the next match, so the old pair's sheets are gone before the new
        /// pair loads. Next frames: load the new pair's fight sheets and FX in <see cref="MatchAssetLoader.WipeFrameBudget"/>
        /// slices. Returns true when everything is loaded (the match never starts before that).
        /// </summary>
        bool PrepareMatch()
        {
            if (matchLoad == null)
            {
                TearDownMatch();
                ChibiMatch.ReleaseUnusedSheets(pending.cls[0], pending.cls[1]);
                matchLoad = new MatchAssetLoader(pending.cls[0], pending.cls[1]);
                return false;   // this frame paid the teardown; loading starts next frame
            }
            if (!matchLoad.Step(MatchAssetLoader.WipeFrameBudget)) return false;
            matchLoad = null;
            return true;
        }

        /// <summary>
        /// Destroys the current match immediately (its view, fighters and stage go now, not at the end of the frame), so
        /// the sheets it showed can be released right after. Only call it while the screen is covered or being replaced.
        /// </summary>
        void TearDownMatch()
        {
            if (match == null) return;
            var go = match.gameObject;
            match = null;
            DestroyImmediate(go);
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
        /// DEV ONLY (editor or development build; null elsewhere): start a local match of <paramref name="players"/> (2..4)
        /// fighters straight away, player 0 human (keyboard / pad 1) and every other slot a CPU, each a different character.
        /// <paramref name="team"/> = 2v2 (fighters 0 + 2 vs 1 + 3, join order); else free-for-all (Battle Royale). Not in
        /// any menu: Vs CPU stays 1v1 (user PDF). Resets <c>pending</c> to a 1v1 afterwards so menus are unchanged.
        /// </summary>
        /// <example><code>FindAnyObjectByType&lt;GameManager&gt;().DevQuickMatch4(4, team: false, stage: 3);</code></example>
        public ChibiMatch DevQuickMatch4(int players = 4, bool team = false, int stage = 0)
        {
            if (!(Application.isEditor || Debug.isDebugBuild)) return null;
            FinishLoadingNow();
            CancelWipe();
            arcade = false;
            players = Mathf.Clamp(players, 2, 4);
            var roster = CharacterRoster.All;
            for (int i = 0; i < 4; i++)
            {
                pending.cls[i] = roster[i % roster.Length].Class;
                pending.passive[i] = 0;
                pending.weapon[i] = 0;
                pending.cpu[i] = i != 0;
            }
            pending.players = players;
            pending.team = team && players == 4;
            pending.teamOf = pending.team ? 0b1010 : 0;   // fighters 1 and 3 on team 1
            pending.stage = stage;
            pending.training = false;
            aiPicksItself = false;
            StartMatch();
            // The next menu match is a 1v1 again (only the match above reads these fields).
            pending.players = 2; pending.team = false; pending.teamOf = 0;
            pending.cpu[2] = pending.cpu[3] = true;
            return match;
        }

        // Inspector right-click on the GameManager in Play Mode (DEV ONLY, same guard as DevQuickMatch4).
        [ContextMenu("DEV: 4-player Battle Royale (Mountain)")] void DevBattleRoyale4() => DevQuickMatch4(4, false, 3);
        [ContextMenu("DEV: 3-player Battle Royale (Mountain)")] void DevBattleRoyale3() => DevQuickMatch4(3, false, 3);
        [ContextMenu("DEV: 2v2 Team (Mountain)")] void DevTeam2v2() => DevQuickMatch4(4, true, 3);

        /// <summary>
        /// Jump straight to a screen (editor testing / automation): home, play (or brawl), online (the Vs Online play
        /// styles), matchmaking / rooms / local (the MATCHMAKING rooms screen), createroom / joinroom (that screen with its
        /// pop-up open), quickmatch (Vs Online QUICK MATCH, starts a real regional search), cpu (Vs CPU character select
        /// with the difficulty), select, precheck, training, arcade, collection (or armory), settings, changename (the
        /// change-name window), language (the LANGUAGE window over SETTINGS).
        /// </summary>
        public void ShowScreen(string screen)
        {
            FinishLoadingNow();
            CancelWipe();
            if (match != null) LeaveMatch(Page.Home);
            string Locked = Loc.T("online.locked_age");
            bool onlineOk = PlayerIdentity.OnlineAllowed(Save.ageBand);
            switch (screen)
            {
                case "select": SetupSelect(true, false, false); Enter(Page.Select); return;
                case "precheck": SetupSelect(true, false, false); Enter(Page.PreCheck); preCheckT = 0f; return;
                case "brawl":
                case "play": Enter(Page.Play); return;
                case "online":
                    if (!onlineOk) { Enter(Page.Home); Toast(Locked); return; }
                    OpenPlayStyle();
                    return;
                case "cpu":
                case "vscpu": SetupSelect(true, false, false); Enter(Page.Select); return;
                case "changename": Enter(Page.Settings); OpenNameDialog(); return;
                case "training": Enter(Page.Training); return;
                case "arcade": Enter(Page.Arcade); return;
                case "armory":
                case "collection": Enter(Page.Collection); return;
                case "settings": Enter(Page.Settings); return;
                case "language": Enter(Page.Settings); OpenLanguageDialog(); return;
                case "quickmatch":
                    if (!onlineOk) { Enter(Page.Home); Toast(Locked); return; }
                    StartQuickMatch();
                    return;
                case "matchmaking":
                case "local":
                case "rooms":
                case "createroom":
                case "joinroom":
                    if (!onlineOk) { Enter(Page.Home); Toast(Locked); return; }
                    OpenRoomsPage();
                    if (screen == "createroom") OpenRoomDialog();
                    else if (screen == "joinroom") OpenJoinDialog();
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
            Wipe(Loc.T("wipe.character_select"), () =>
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
            Wipe(Loc.T("wipe.arcade"), () =>
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

        /// <summary>Arcade round stage: the menu order (design stages first, ST-1), so a 5-round run visits all five.</summary>
        static int ArcadeStage(int round) => StageAt(round);

        /// <summary>Number of stages offered in menus.</summary>
        static int StageCount => StageBuilder.MenuOrder.Length;

        /// <summary>Sim stage index shown at menu position <paramref name="pos"/> (wraps), per <see cref="StageBuilder.MenuOrder"/>.</summary>
        static int StageAt(int pos)
        {
            int n = StageBuilder.MenuOrder.Length;
            return StageBuilder.MenuOrder[((pos % n) + n) % n];
        }

        /// <summary>Menu position of sim stage index <paramref name="stage"/> (0 when it is not offered).</summary>
        static int StagePos(int stage)
        {
            for (int k = 0; k < StageBuilder.MenuOrder.Length; k++)
                if (StageBuilder.MenuOrder[k] == stage) return k;
            return 0;
        }

        /// <summary>Stage name that fits <paramref name="width"/> at <paramref name="size"/>: full name, else the short one.</summary>
        static string StageLabel(int stage, float width, int size, bool display = false)
        {
            string full = StageBuilder.NameText(stage).ToUpperInvariant();
            return Gui.Measure(full, size, display) <= width ? full : StageBuilder.ShortNameText(stage).ToUpperInvariant();
        }

        /// <summary>Label of the results screen's first button (arcade continues the ladder instead of a rematch).</summary>
        public string RematchLabel
        {
            get
            {
                if (!arcade || match == null) return Loc.T("results.rematch");
                if (match.Winner != 0) return Loc.T("results.continue");
                return arcadeRound + 1 >= ArcadeRounds ? Loc.T("results.finish") : Loc.T("results.next_battle");
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
                        Wipe(Loc.T("wipe.arcade_clear"), () =>
                        {
                            LeaveMatch(Page.Arcade);
                            Toast(Loc.T("arcade.clear_toast", ArcadeRounds));
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
            UiSound.Confirm();
            if (arcade) PrepareArcadeRound();
            if (Save.settings.preMatchCheck && !pending.training)
                Wipe(Loc.T("precheck.title"), () =>
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
                if (b < 0f) { lightOk = false; lightMsg = Loc.T("precheck.camera_unavailable"); }
                else if (b < 0.18f) { lightOk = false; lightMsg = Loc.T("precheck.too_dark", Mathf.RoundToInt(b * 100f)); }
                else if (b > 0.92f) { lightOk = false; lightMsg = Loc.T("precheck.too_bright", Mathf.RoundToInt(b * 100f)); }
                else { lightOk = true; lightMsg = Loc.T("precheck.good", Mathf.RoundToInt(b * 100f)); }
            }
            else { lightOk = true; lightMsg = Loc.T("precheck.simulated_feed"); }

            var f = menuTracker.Source.Current;
            bool inFrame = f.confidence >= 0.6f;
            foreach (var p in f.p) inFrame &= p.x > 0.03f && p.x < 0.97f && p.y > 0.03f && p.y < 0.97f;
            frameOk = inFrame;
            frameMsg = inFrame ? Loc.T("precheck.upper_body_ok") : Loc.T("precheck.step_back");

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
            loadSteps.Add(new LoadStep { label = Loc.T("loading.interface"), run = () => { GuiArt.WarmUp(); return 1f; } });
            loadSteps.Add(new LoadStep { label = Loc.T("loading.typefaces"), run = () => { Gui.WarmUpFonts(); return 1f; } });
            foreach (var w in WeaponDef.All)
            {
                var weapon = w;
                ResourceRequest req = null;
                loadSteps.Add(new LoadStep
                {
                    label = Loc.T("loading.weapon_art", weapon.name),
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
                loadSteps.Add(new LoadStep { label = Loc.T("loading.fighter", c.Name), run = () => { CharacterArt.Preload(c); return 1f; } });
            }
            if (Save.settings.useWebcam)
                loadSteps.Add(new LoadStep { label = Loc.T("loading.pose_model"), run = () => { MoveNetPoseSource.Shared.EnsureLoaded(); return 1f; } });
            loadIndex = 0;
            loadFrac = 0f;
            loadShown = 0f;
            loadDoneAt = -1f;
            loadFeatured = UnityEngine.Random.Range(0, CharacterRoster.All.Length);
            featured = loadFeatured;
        }

        float LoadProgress => loadSteps.Count == 0 ? 1f : Mathf.Clamp01((loadIndex + loadFrac) / loadSteps.Count);
        string LoadLabel => loadIndex < loadSteps.Count ? loadSteps[loadIndex].label : Loc.T("loading.ready");

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
                // A room invite pop-up sits on top of the page: the page under the mouse pointer must not take its clicks.
                bool invite = !exitPrompt && NameInviteShown;
                Gui.Interactive = wipeStart < 0f && !exitPrompt && !(invite && InvitePopupRect.Contains(Event.current.mousePosition));
                DrawMenus();
                Gui.Alpha = 1f;
                if (exitPrompt)
                {
                    Gui.Interactive = wipeStart < 0f;
                    ExitModal();
                }
                else if (invite)
                {
                    Gui.Interactive = wipeStart < 0f;
                    NameInvitePopup();
                }
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
                case Hud.Action.Resume:
                    if (match.Online) match.LeavePrompt = false;
                    else match.SetPaused(false);
                    break;
                case Hud.Action.Forfeit: match.Forfeit(); break;
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
                        else
                        {
                            // A quick match fight (no room): BACK returns to the Vs Online play styles (SOLO searches
                            // again), MENU to the home menu.
                            LeaveMatch(choice == Hud.Action.Menu ? Page.Home : Page.PlayStyle);
                        }
                        break;
                    }
                    bool toMenu = choice == Hud.Action.Menu;
                    Wipe(toMenu ? null : Loc.T("wipe.character_select"), () =>
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
                DialogBackdrop(Loc.T("onboarding.welcome"));
                AgeDialog();
                return;
            }
            // First launch step 2 (user order 2026-11-24: age, then language, then name); also SETTINGS > LANGUAGE and
            // the globe button at the top right of the menu pages.
            if (LanguageShown)
            {
                DialogBackdrop(Loc.T("lang.backdrop"));
                LanguageDialog();
                return;
            }
            // No sign-in step: the identity is kept automatically for this computer's user profile (every launch, no
            // button). The SIGN IN WITH GOOGLE button was removed from the name window (user request 2026-11-23).
            if (ShowAccountStep)
            {
                DialogBackdrop(Loc.T("onboarding.your_account"));
                AccountDialog();
                return;
            }
            if (NeedsFirstName && !nameDialog) OpenNameDialog();
            if (nameDialog)
            {
                DialogBackdrop(Loc.T("onboarding.your_name"));
                NameDialog();
                return;
            }
            if (roomDialog)
            {
                DialogBackdrop(Loc.T("rooms.backdrop_create"));
                RoomDialog();
                return;
            }
            if (joinDialog)
            {
                DialogBackdrop(Loc.T("rooms.backdrop_join"));
                JoinDialog();
                return;
            }
            switch (page)
            {
                case Page.Home: HomeView(); break;
                case Page.Play: PlayView(); break;
                case Page.PlayStyle: PlayStyleView(); break;
                case Page.Training: TrainingView(); break;
                case Page.Arcade: ArcadeView(); break;
                case Page.Collection: CollectionView(); break;
                case Page.Settings: SettingsView(); break;
                case Page.Select: SelectView(); break;
                case Page.PreCheck: PreCheckView(); break;
                case Page.Rooms: RoomsView(); break;
                case Page.Room: RoomView(); break;
                case Page.QuickMatch: QuickMatchView(); break;
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
            Gui.Display(new Rect(40f, 24f, 700f, 70f), Loc.T("precheck.title"), 56, Gui.Paper, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(44f, 88f, 700f, 22f), VersusCaption() + "  ·  " + StageBuilder.NameText(Mathf.Clamp(pending.stage, 0, StageBuilder.Names.Length - 1)).ToUpper(), 14, Gui.Gold, TextAnchor.MiddleLeft);

            Gui.Alpha = k;
            var feed = new Rect(W * 0.5f - 420f, 130f, 440f, 340f);
            bool ok = lightOk && frameOk;
            var state = preCheckT < 5f ? Gui.Gold : ok ? Gui.Green : Gui.Red;
            Gui.CvPanel(feed, menuTracker, state, Save.settings.useWebcam, Loc.T("precheck.title"));

            var p = new Rect(feed.xMax + 20f, 130f, 380f, 340f);
            Gui.Box(p, state);
            float x = p.x + 20f, w = p.width - 40f;
            Gui.Display(new Rect(x, p.y + 14f, w, 34f), preCheckT < 5f ? Loc.T("precheck.checking", Mathf.CeilToInt(5f - preCheckT)) : ok ? Loc.T("precheck.all_passed") : Loc.T("precheck.failed"), 28, state, TextAnchor.MiddleLeft, 0.18f);
            Gui.Meter(new Rect(x, p.y + 56f, w, 10f), preCheckT / 5f, state, 20);
            Check(x, p.y + 80f, w, Loc.T("precheck.lighting"), lightOk, lightMsg);
            Check(x, p.y + 140f, w, Loc.T("precheck.framing"), frameOk, frameMsg);
            bool postureOk = menuTracker.PostureLive && menuTracker.Posture >= 0.6f;   // camera only: the simulator is always upright
            Check(x, p.y + 200f, w, Loc.T("precheck.posture"), postureOk,
                  !menuTracker.PostureLive ? Loc.T("precheck.posture_no_camera") : postureOk ? Loc.T("precheck.upright") : Loc.T("precheck.sit_up"));

            if (preCheckT >= 5f && !ok)
            {
                Gui.PreferNext();
                if (Gui.Button(new Rect(x, p.yMax - 60f, 160f, 44f), Loc.T("common.retry"), Gui.Gold, 16)) preCheckT = 0f;
                if (Gui.Button(new Rect(x + 180f, p.yMax - 60f, 160f, 44f), Loc.T("precheck.play_anyway"), Gui.P1, 14)) LaunchMatch();
            }
            if (Gui.Button(new Rect(W * 0.5f - 90f, 500f, 180f, 48f), Loc.T("common.back"), Gui.Steel, 16)) { UiSound.Back(); Go(Page.Select); }
            Gui.Text(new Rect(0f, 560f, W, 40f), Loc.T("precheck.ranked_note"), 13, Gui.Muted, TextAnchor.MiddleCenter, false);
            Gui.Alpha = 1f;
            Gui.HintBar(Loc.T("precheck.hint"), Gui.Key("SPACE", "A"), Loc.T("hint.select"), Gui.Key("ESC", "B"), Loc.T("common.back"));
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
