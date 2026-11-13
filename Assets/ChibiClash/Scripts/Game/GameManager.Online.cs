using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Onboarding (age, player name, optional Google account), the online lobby (matchmaking, LAN, friends, invites),
    /// the ROOMS pages and the invite pop-up. Logic unchanged from the pre-UI-1 GameManager; presentation in the fork's
    /// arcade style. Online services (rooms, social, account) are only called, never re-implemented here.
    /// </summary>
    public partial class GameManager
    {
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
        // Player name dialog: forced on first launch (no saved name), opened from the profile chip in the nav bar.
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

        readonly Dictionary<int, GUIStyle> fieldStyles = new Dictionary<int, GUIStyle>();

        /// <summary>Centred text-field style of a font size, created once (OnGUI only).</summary>
        GUIStyle Field(int size)
        {
            if (!fieldStyles.TryGetValue(size, out var st))
            {
                st = new GUIStyle(GUI.skin.textField) { fontSize = size, alignment = TextAnchor.MiddleCenter };
                fieldStyles[size] = st;
            }
            return st;
        }

        static bool EnterPressed()
        {
            var e = Event.current;
            return e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
        }

        // ---------------------------------------------------------------- onboarding / PLAYER NAME

        bool ShowAccountStep => ShowAccountStepOnFirstLaunch && !Save.accountAsked && PlayerIdentity.OnlineAllowed(Save.ageBand) && Save.accountId.Length == 0;

        /// <summary>First-launch steps still to do (age; the account step when enabled).</summary>
        bool NeedsOnboarding => PlayerIdentity.NeedsAge(Save.ageBand) || Save.age <= 0 || ShowAccountStep;

        /// <summary>No valid saved name yet: the first-launch name dialog is shown.</summary>
        bool NeedsFirstName => Netcode.ChibiSocialRules.CleanSavedName(Save.playerName).Length == 0;

        /// <summary>The name to show for us: Unity's tagged name once signed in ("Kira#4821"), else the saved one.</summary>
        string DisplayedName() =>
            social != null && social.Ready && !social.NeedsName ? social.MyName : Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);

        Rect DialogRect(float w, float h) => new Rect(W * 0.5f - w * 0.5f, Mathf.Max(130f, H * 0.5f - h * 0.5f - 30f), w, h);

        /// <summary>
        /// Onboarding step 1: a neutral age question (no hint of a "right" answer). Only the age GROUP is saved. Under 13,
        /// online friends / invites / matchmaking stay locked; everything offline is available.
        /// </summary>
        void AgeDialog()
        {
            var r = DialogRect(660f, 300f);
            Gui.Box(r, Gui.P2);
            Gui.Display(new Rect(r.x, r.y + 14f, r.width, 44f), "HOW OLD ARE YOU?", 36, Gui.Paper, TextAnchor.MiddleCenter);
            Gui.Text(new Rect(r.x + 30f, r.y + 62f, r.width - 60f, 40f),
                     "Every age can play every mode. Your age is recorded with your player name for game statistics.", 13, Gui.Muted, TextAnchor.UpperCenter, false);

            bool enter = EnterPressed();
            var field = new Rect(r.x + r.width * 0.5f - 90f, r.y + 106f, 180f, 56f);
            GUI.SetNextControlName("Age");
            string typed = GUI.TextField(field, ageInput ?? "", 3, Field(30));
            if (typed != ageInput)
            {
                var sb = new System.Text.StringBuilder(3);
                foreach (char ch in typed) if (ch >= '0' && ch <= '9') sb.Append(ch);   // digits only
                ageInput = sb.ToString();
            }
            if (GUI.GetNameOfFocusedControl() != "Age") GUI.FocusControl("Age");
            Gui.Text(new Rect(r.x + 30f, r.y + 168f, r.width - 60f, 24f), nameError, 14, Gui.Red);

            if (Gui.Button(new Rect(r.x + r.width * 0.5f - 130f, r.y + 210f, 260f, 54f), "NEXT", Gui.P1, 20) || enter)
            {
                var band = PlayerIdentity.BandForAnswer(ageInput);
                if (band == AgeBand.Unknown) { nameError = "Please type your age as a number."; UiSound.Error(); }
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
            var r = DialogRect(680f, 392f);
            Gui.Box(r, Gui.P1);
            Gui.Display(new Rect(r.x, r.y + 14f, r.width, 44f), "CREATE A ROOM", 36, Gui.Paper, TextAnchor.MiddleCenter);
            Gui.Text(new Rect(r.x + 30f, r.y + 60f, r.width - 60f, 22f),
                     "Name your room (3-24 letters, numbers, spaces). Your friend sees it when they join with the code.",
                     13, Gui.Muted, TextAnchor.UpperCenter, false);

            bool enter = EnterPressed();
            var field = new Rect(r.x + 60f, r.y + 92f, r.width - 120f, 50f);
            GUI.SetNextControlName("RoomName");
            roomNameInput = GUI.TextField(field, roomNameInput ?? "", Netcode.ChibiSocialRules.RoomNameMax, Field(24));
            if (GUI.GetNameOfFocusedControl() != "RoomName") GUI.FocusControl("RoomName");

            Gui.Text(new Rect(r.x + 60f, r.y + 152f, 200f, 24f), "STAGE", 14, Gui.Gold, TextAnchor.MiddleLeft);
            if (HazardsButton(new Rect(r.xMax - 60f - 132f, r.y + 151f, 132f, 26f), pending.hazards, true)) pending.hazards = !pending.hazards;
            int pick = StageGrid(new Rect(r.x + 60f, r.y + 184f, r.width - 120f, StageGridHeight(34f)), pending.stage, true, 14);
            if (pick >= 0) pending.stage = pick;

            Gui.Text(new Rect(r.x + 30f, r.y + 264f, r.width - 60f, 24f), roomError, 14, Gui.Red);

            if (Gui.Button(new Rect(r.x + r.width * 0.5f - 230f, r.y + 300f, 220f, 56f), "CREATE ROOM", Gui.P1, 20) || enter)
            {
                string n = (roomNameInput ?? "").Trim();
                string err = Netcode.ChibiSocialRules.ValidateRoomName(n);
                if (err != null) { roomError = err; UiSound.Error(); }
                else if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { roomError = "Online play is for players 13 and older."; UiSound.Error(); }
                else
                {
                    roomDialog = false;
                    pending.cpu[0] = pending.cpu[1] = false;
                    pending.training = false;
                    onlineNote = "";
                    social.ClearMatchFriend();
                    // ST-5: the room carries the host's hazards pick (key "hz") next to the stage.
                    roomsSvc.Create(n, pending.stage, pending.hazards);
                    Enter(Page.Room);
                }
                if (enter) Event.current.Use();
            }
            if (Gui.Button(new Rect(r.x + r.width * 0.5f + 10f, r.y + 300f, 220f, 56f), "CANCEL", Gui.Steel, 18))
            {
                roomDialog = false;
                UiSound.Back();
            }
        }

        /// <summary>
        /// Onboarding: SIGN IN WITH GOOGLE (the name and info stay with the account; the same account on this computer
        /// signs in by itself next time) or PLAY WITHOUT ACCOUNT (kept on this computer only). Changeable later from the
        /// name window.
        /// </summary>
        void AccountDialog()
        {
            var r = DialogRect(680f, 330f);
            Gui.Box(r, Gui.P2);
            Gui.Display(new Rect(r.x, r.y + 14f, r.width, 44f), "KEEP YOUR FIGHTER NAME", 34, Gui.Paper, TextAnchor.MiddleCenter);
            Gui.Text(new Rect(r.x + 40f, r.y + 62f, r.width - 80f, 70f),
                     "Sign in with your Google account and your name and info stay with you: every time you open the game, " +
                     "and on any computer. Another Google account gets its own new name.", 14, Gui.Paper, TextAnchor.UpperCenter, false);
            bool working = account.Working;
            if (Gui.Button(new Rect(r.x + r.width * 0.5f - 200f, r.y + 146f, 400f, 58f), working ? "FINISH IN YOUR BROWSER..." : "SIGN IN WITH GOOGLE",
                           Gui.P1, 20, !working))
                account.SignInWithGoogle();
            if (Gui.Button(new Rect(r.x + r.width * 0.5f - 200f, r.y + 216f, 400f, 46f), "PLAY WITHOUT ACCOUNT", Gui.Steel, 16, !working))
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
            if (NeedsOnboarding) return;
            nameDialog = true;
            nameInput = Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);
            nameError = "";
        }

        /// <summary>Choose or change the player name (validated by ChibiSocialRules.ValidateName; saved; pushed to Unity).</summary>
        void NameDialog()
        {
            bool first = NeedsFirstName;
            var r = DialogRect(660f, 300f);
            Gui.Box(r, Gui.Gold);
            Gui.Display(new Rect(r.x, r.y + 14f, r.width, 44f), first ? "WELCOME, FIGHTER! WHAT IS YOUR NAME?" : "CHANGE YOUR NAME", first ? 30 : 36, Gui.Paper, TextAnchor.MiddleCenter);
            Gui.Text(new Rect(r.x + 30f, r.y + 60f, r.width - 60f, 40f),
                     "3-16 letters, numbers or _. Pick a fun fighter name, not your real name. It is how friends find and invite you, and what everyone sees.",
                     13, Gui.Muted, TextAnchor.UpperCenter, false);

            bool enter = EnterPressed();
            var field = new Rect(r.x + 60f, r.y + 112f, r.width - 120f, 50f);
            GUI.SetNextControlName("PlayerName");
            nameInput = GUI.TextField(field, nameInput ?? "", Netcode.ChibiSocialRules.NameMax, Field(26));
            if (GUI.GetNameOfFocusedControl() != "PlayerName") GUI.FocusControl("PlayerName");
            Gui.Text(new Rect(r.x + 30f, r.y + 168f, r.width - 60f, 24f), nameError, 14, Gui.Red);

            float bw = first ? 260f : 200f;
            float bx = first ? r.x + (r.width - bw) * 0.5f : r.x + r.width * 0.5f - bw - 10f;
            if (Gui.Button(new Rect(bx, r.y + 210f, bw, 54f), first ? "LET'S FIGHT!" : "SAVE", Gui.P1, 20) || enter)
            {
                string n = (nameInput ?? "").Trim();
                string err = Netcode.ChibiSocialRules.ValidateName(n);
                if (err != null) { nameError = err; UiSound.Error(); }
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
                Gui.Rect(ar, Gui.Panel);
                Gui.Rect(new Rect(ar.x, ar.y, 4f, ar.height), Gui.P2);
                Gui.Text(new Rect(ar.x + 18f, ar.y, ar.width - 260f, 50f),
                         account.SignedIn ? "Signed in with your account: this name stays with it on every computer."
                                          : "Saved on this computer automatically. Sign in only to use it on other computers.", 13, Gui.Muted, TextAnchor.MiddleLeft, false);
                string label = account.SignedIn ? "SIGN OUT / SWITCH" : "SIGN IN WITH GOOGLE";
                if (Gui.Button(new Rect(ar.xMax - 236f, ar.y + 7f, 222f, 36f), account.Working ? "SIGNING IN..." : label, Gui.P2, 14, !account.Working))
                {
                    if (account.SignedIn) account.SignOut();
                    else account.SignInWithGoogle();
                }
                if (account.Status.Length > 0)
                    Gui.Text(new Rect(ar.x, ar.yMax + 4f, ar.width, 40f), account.Status, 12, account.StatusIsError ? Gui.Red : Gui.Muted, TextAnchor.UpperCenter, false);
            }
            if (!first && Gui.Button(new Rect(r.x + r.width * 0.5f + 10f, r.y + 210f, 200f, 54f), "CANCEL", Gui.Steel, 18))
            {
                nameDialog = false;
                UiSound.Back();
            }
        }

        // ---------------------------------------------------------------- online match start

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
                pending.hazards = roomsSvc.HazardsOn;   // ST-5: the room's hazards pick
            }
            // Perf: start loading the fight sheets now, before the handshake and its rollback clock start: our own fighter,
            // plus the opponent's when the room already shows it (cosmetic hint only; the handshake's agreed config is
            // what the match uses, and a wrong guess just loads a sheet set that is released later). Stepped in small
            // slices every frame while handshaking (UpdatePendingOnline).
            string mine = CharacterRoster.Of(pending.cls[0]).Id;
            string theirs = RoomOpponentId();
            if (match == null) SpriteAnimLibrary.ReleaseAllExcept(mine, theirs);   // nothing on screen shows the others
            onlineLoad = new MatchAssetLoader(mine, theirs);
            onlinePeerSeen = false;
            // ST-5: the host's hazards pick travels in the handshake Start message (protocol 4) and becomes HazardsOff on
            // both peers (the joiner takes it from Start).
            var online = isHost
                ? Netcode.ChibiOnlineMatch.Host(transport, input, pending.cls[0], pending.passive[0], pending.stage,
                                                (uint)System.Environment.TickCount ^ 0xC1B1u, settings,
                                                Netcode.ChibiHandshake.DefaultTimeoutTicks, me, !pending.hazards)
                : Netcode.ChibiOnlineMatch.Join(transport, input, pending.cls[0], pending.passive[0],
                                                Netcode.ChibiHandshake.DefaultTimeoutTicks, me,
                                                roomsSvc.OwnsLink ? pending.stage : -1,                  // SEC-001: the room's
                                                roomsSvc.OwnsLink ? !pending.hazards : (bool?)null);      // agreed settings
            var go = new GameObject("OnlineMatch");
            var runner = go.AddComponent<Netcode.ChibiOnlineMatchRunner>();
            runner.Begin(online, input);
            pendingOnline = runner;
            pendingOnlineInput = input;
            onlineNote = "Connected. Agreeing on the match...";
            UiSound.LockIn();   // match found
        }

        /// <summary>Online fight-sheet preload (handshake, then the READY intro); null when none is running.</summary>
        MatchAssetLoader onlineLoad;
        /// <summary>The host already saw the joiner's Hello (its class is known) and queued its sheets.</summary>
        bool onlinePeerSeen;

        /// <summary>
        /// Character id of the other player's pick in the room we fight from ("" outside a room or when not shown yet).
        /// A view-side hint for preloading only: the handshake decides the classes.
        /// </summary>
        string RoomOpponentId()
        {
            var room = roomsSvc != null && roomsSvc.OwnsLink ? roomsSvc.Current : null;
            if (room == null) return "";
            string me = roomsSvc.MyId;
            foreach (var slot in room.Slots)
                if (!string.IsNullOrEmpty(slot.PlayerId) && slot.PlayerId != me) return CharacterRoster.Of(slot.Class).Id;
            return "";
        }

        /// <summary>
        /// One budgeted slice of the online preload per frame. While handshaking, the host learns the joiner's class from
        /// its Hello one round trip before the match starts (the handshake's config, and its match hash, are filled in
        /// then; both are read only), so the opponent's sheets load before Playing. The joiner only learns the host's
        /// class when the match starts; what is left then is finished in small slices during the READY intro (input off)
        /// instead of one long stall, and anything shown before it is loaded is loaded on demand.
        /// </summary>
        void StepOnlineLoad(Netcode.ChibiOnlineMatch m)
        {
            if (m != null && m.Phase == Netcode.ChibiOnlinePhase.Handshaking && !onlinePeerSeen &&
                m.Handshake.LocalIndex == 0 && m.Handshake.MatchHash != 0)
            {
                onlinePeerSeen = true;
                var cfg = m.Handshake.Config;
                onlineLoad = new MatchAssetLoader(cfg.Class0, cfg.Class1);   // resumes: progress lives in SpriteAnimLibrary
            }
            if (onlineLoad == null) return;
            float budget = m != null && m.Phase == Netcode.ChibiOnlinePhase.Handshaking
                ? MatchAssetLoader.WipeFrameBudget : MatchAssetLoader.RunningFrameBudget;
            if (onlineLoad.Step(budget)) onlineLoad = null;
        }

        /// <summary>Watch the handshake of a just-connected online match: show the match once it plays, or explain why not.</summary>
        void UpdatePendingOnline()
        {
            if (pendingOnline == null)
            {
                if (page == Page.Match && match != null && match.Online) StepOnlineLoad(null);   // READY intro slices
                else onlineLoad = null;
                return;
            }
            var m = pendingOnline.Match;
            if (m.Phase == Netcode.ChibiOnlinePhase.Handshaking) StepOnlineLoad(m);
            if (m.Phase == Netcode.ChibiOnlinePhase.Playing || m.Phase == Netcode.ChibiOnlinePhase.Lingering)
            {
                TearDownMatch();
                CancelWipe();
                exitPrompt = false;
                roomDialog = false;
                var agreed = m.Handshake.Config;
                ChibiMatch.ReleaseUnusedSheets(agreed.Class0, agreed.Class1);   // a wrong room guess is dropped here
                onlineLoad = new MatchAssetLoader(agreed.Class0, agreed.Class1);   // the rest: READY intro slices
                match = ChibiMatch.CreateOnline(this, pendingOnline, pendingOnlineInput, pending.weapon[0]);
                match.SetVerifiedOpponentName(social.MatchFriendName);   // invite match: the friends-list name
                social.ClearMatchFriend();
                pendingOnline = null;
                pendingOnlineInput = null;
                page = Page.Match;
                pageSince = Time.unscaledTime;
                resultsSeen = false;
                Gui.ResetFocus();
                social.SetInMatch(true);
            }
            else if (m.Phase == Netcode.ChibiOnlinePhase.Closed)
            {
                var r = m.Result;
                onlineNote = r.RejectReason == Netcode.ChibiRejectReason.InvalidConfig && roomsSvc.OwnsLink && !m.Handshake.RejectedByPeer
                    ? "The host's settings changed \u2014 rejoin the room."                                                       // SEC-001
                    : r.RejectReason == Netcode.ChibiRejectReason.RulesMismatch || r.RejectReason == Netcode.ChibiRejectReason.ProtocolMismatch ||
                      r.RejectReason == Netcode.ChibiRejectReason.ContentMismatch
                    ? "Your opponent runs a different version of the game. Both players need the same build."
                    : "The match could not start (" + r.HandshakeState + ").";
                Destroy(pendingOnline.gameObject);
                pendingOnline = null;
                pendingOnlineInput = null;
                rooms.Cancel();
                UiSound.Error();
                if (roomsSvc.OwnsLink) roomsSvc.OnMatchEnded();   // room fight that never started: back to OK
            }
        }

        // ---------------------------------------------------------------- LOBBY (matchmaking, LAN, friends)

        bool OnlineBusy => rooms.State != Netcode.ChibiRoomState.Idle && rooms.State != Netcode.ChibiRoomState.Failed || pendingOnline != null;

        void OpenLobby(bool friends)
        {
            pending.cpu[0] = pending.cpu[1] = false;
            pending.training = false;
            onlineNote = "";
            showFriends = friends;
            Enter(Page.Lobby);
            if (friends && !social.Ready && !social.Working) social.Connect();
        }

        /// <summary>MATCHMAKING card: open the lobby and start searching at once (pick a fighter while it searches).</summary>
        void StartMatchmaking()
        {
            OpenLobby(false);
            social.ClearMatchFriend();
            if (!OnlineBusy) rooms.QuickMatch();
        }

        void LobbyView()
        {
            if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { Enter(Page.Home); return; }   // age gate (13+), defence in depth
            bool noInternet = Application.internetReachability == NetworkReachability.NotReachable;
            Backdrop();
            NavBar();
            var a = BeginPage("02", "LOBBY", noInternet ? "NO INTERNET  ·  CONNECT WI-FI OR MOBILE DATA (LAN STILL WORKS)"
                                                        : "MATCHMAKING · LAN · FRIENDS & INVITES");
            float ph = a.height - 62f;
            var left = new Rect(a.x, a.y, a.width * 0.5f - 10f, ph);
            FighterPicker(left, true);

            var r = new Rect(left.xMax + 20f, a.y, a.width - left.width - 20f, ph);
            bool busy = OnlineBusy;
            Gui.Box(r, showFriends ? Gui.P1 : Gui.P2);
            // Tabs: ROOMS (stage, quick match, LAN) and FRIENDS (name, friend list, invites).
            float tw = (r.width - 48f - 10f) * 0.5f;
            if (Gui.Button(new Rect(r.x + 24f, r.y + 12f, tw, 32f), "MATCH & LAN", Gui.P2, 14, true, !showFriends)) showFriends = false;
            if (Gui.Button(new Rect(r.x + 34f + tw, r.y + 12f, tw, 32f), social.Requests.Count > 0 ? "FRIENDS (" + social.Requests.Count + ")" : "FRIENDS",
                           Gui.P1, 14, true, showFriends))
            {
                showFriends = true;
                if (!social.Ready && !social.Working) social.Connect();
            }
            var body = new Rect(r.x, r.y + 50f, r.width, r.height - 50f);
            if (showFriends) FriendsPanel(body, busy);
            else MatchPanel(body, busy);

            LobbyBottomButtons(busy, a);
            EndPage();
            NavHints("ONLINE  ·  LOBBY");
        }

        void MatchPanel(Rect r, bool busy)
        {
            float x = r.x + 24f, w = r.width - 48f, y = r.y + 8f;
            Gui.Text(new Rect(x, y, w, 26f), "STAGE (the room host's pick is used)", 13, Gui.Gold, TextAnchor.MiddleLeft);
            // F2: in quick play the host's hazards switch is used too; the toggle sets only what we use when we host.
            if (HazardsButton(new Rect(x + w - 128f, y + 1f, 128f, 24f), pending.hazards, !busy) && !busy) pending.hazards = !pending.hazards;
            Gui.Text(new Rect(x + w - 330f, y + 1f, 196f, 24f), "host's pick is used", 11, Gui.Muted, TextAnchor.MiddleRight, true, false);
            y += 30f;
            int pick = StageGrid(new Rect(x, y, w, StageGridHeight(32f)), pending.stage, !busy);
            if (pick >= 0) pending.stage = pick;
            y += StageGridHeight(32f) + 14f;

            // Private rooms live on the ROOMS page (named rooms, OK / START FIGHT).
            if (Gui.Button(new Rect(x, y, w, 48f), "MY ROOMS  (create / join a private room)", Gui.Gold, 16, !busy) && !busy)
                OpenRoomsPage();
            y += 60f;

            Gui.PreferNext();
            if (Gui.Button(new Rect(x, y, w, 44f), "MATCHMAKING (closest players)", Gui.P2, 17, !busy) && !busy)
            { onlineNote = ""; social.ClearMatchFriend(); rooms.QuickMatch(); }
            // Privacy notice: what matchmaking shares.
            Gui.Text(new Rect(x, y + 46f, w, 18f), "Finds the closest players. Shares your country (from device settings), never your location.",
                     11, Gui.Muted);
            y += 74f;

            // LAN direct play: no Unity services needed (same network). Same transport, handshake and rollback as Relay.
            Gui.Text(new Rect(x, y - 4f, w, 18f), "SAME NETWORK (LAN)", 12, Gui.Gold, TextAnchor.MiddleLeft);
            y += 16f;
            if (Gui.Button(new Rect(x, y, 140f, 40f), "LAN HOST", Gui.Gold, 14, !busy) && !busy)
            { onlineNote = ""; social.ClearMatchFriend(); rooms.HostLan(); }
            var ipField = new Rect(x + 150f, y, w - 150f - 150f, 40f);
            if (!busy) lanAddress = GUI.TextField(ipField, lanAddress ?? "", 40, Field(18));
            else GUI.Label(ipField, lanAddress, Field(18));
            if (Gui.Button(new Rect(ipField.xMax + 10f, y, 140f, 40f), "LAN JOIN", Gui.Gold, 14, !busy) && !busy)
            { onlineNote = ""; social.ClearMatchFriend(); rooms.JoinLan(lanAddress); }
            y += 56f;

            bool failed = rooms.State == Netcode.ChibiRoomState.Failed;
            string status = onlineNote.Length > 0 ? onlineNote : rooms.Status;
            if (busy)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5f);
                Gui.Skew(new Rect(x, y + 4f, 10f, 22f), WithA(Gui.P2, 0.4f + 0.6f * pulse), 0.3f);
            }
            Gui.Text(new Rect(x + (busy ? 20f : 0f), y, w - (busy ? 20f : 0f), 60f), status, 15, failed ? Gui.Red : Gui.Paper, TextAnchor.UpperCenter, false);
        }

        /// <summary>FRIENDS tab: our player name, add a friend by name, incoming requests, friends with online dot and INVITE.</summary>
        void FriendsPanel(Rect r, bool busy)
        {
            float x = r.x + 24f, w = r.width - 48f, y = r.y + 8f;

            if (!social.Ready)
            {
                Gui.Text(new Rect(x, y + 20f, w, 80f), social.Working ? "Connecting to friends..." : social.Status, 15,
                         social.StatusIsError ? Gui.Red : Gui.Paper, TextAnchor.UpperCenter, false);
                Gui.PreferNext();
                if (!social.Working && Gui.Button(new Rect(x + w * 0.5f - 110f, y + 110f, 220f, 46f), "CONNECT", Gui.P2, 16))
                    social.Connect();
                return;
            }

            // Our name ("Kira#4821"): what friends type to add / invite us. Changed with the profile chip at the top.
            Gui.Display(new Rect(x, y, w, 30f), "YOU: " + (social.NeedsName ? "(setting your name...)" : social.MyName), 24, Gui.Gold, TextAnchor.MiddleLeft, 0.15f);
            Gui.Text(new Rect(x, y + 28f, w, 20f), "Friends type this name to add or invite you. Change it with your name at the top right.",
                     12, Gui.Muted, TextAnchor.MiddleLeft);
            y += 54f;

            // Type a player's name: ADD FRIEND, or INVITE straight into our room (a friend request goes first if needed).
            var af = new Rect(x, y, w - 290f, 40f);
            friendInput = GUI.TextField(af, friendInput ?? "", 24, Field(18));
            if (Gui.Button(new Rect(af.xMax + 10f, y, 140f, 40f), "ADD FRIEND", Gui.P2, 13, !social.Working))
            { social.AddFriend(friendInput); friendInput = ""; }
            if (Gui.Button(new Rect(af.xMax + 155f, y, 135f, 40f), "INVITE", Gui.P1, 14, !social.Working && !busy) && !busy)
            { onlineNote = ""; social.InviteByName(friendInput); friendInput = ""; }
            y += 44f;
            if (social.WaitingToInviteName.Length > 0)
            {
                Gui.Text(new Rect(x, y, w - 100f, 24f), "Waiting for " + social.WaitingToInviteName + " to accept, then the invite goes out.",
                         12, Gui.Gold, TextAnchor.MiddleLeft, false);
                if (Gui.Button(new Rect(x + w - 90f, y, 90f, 24f), "STOP", Gui.Steel, 11)) social.ForgetWaitingInvite();
            }
            else Gui.Text(new Rect(x, y, w, 24f), "Type a name like Kira#4821.", 12, Gui.Muted, TextAnchor.MiddleLeft);
            y += 30f;

            // Incoming requests.
            foreach (var q in social.Requests)
            {
                if (y > r.yMax - 120f) break;
                Gui.Rect(new Rect(x, y, w, 40f), Gui.PanelLight);
                Gui.Rect(new Rect(x, y, 3f, 40f), Gui.Gold);
                Gui.Text(new Rect(x + 12f, y, w - 300f, 40f), q.Name + "  wants to be friends", 14, Gui.Paper, TextAnchor.MiddleLeft, false);
                if (Gui.Button(new Rect(x + w - 282f, y + 4f, 90f, 32f), "ACCEPT", Gui.Green, 12, !social.Working)) social.AcceptRequest(q.Id);
                if (Gui.Button(new Rect(x + w - 186f, y + 4f, 90f, 32f), "DECLINE", Gui.Red, 12, !social.Working)) social.DeclineRequest(q.Id);
                // Block stops further requests, messages and invites from a spammer.
                if (Gui.Button(new Rect(x + w - 90f, y + 4f, 80f, 32f), "BLOCK", Gui.Steel, 11, !social.Working)) social.Block(q.Id);
                y += 46f;
            }

            // Friends.
            Gui.Text(new Rect(x, y, w, 24f), "FRIENDS (" + social.Friends.Count + ")", 13, Gui.Gold, TextAnchor.MiddleLeft);
            y += 26f;
            if (social.Friends.Count == 0)
                Gui.Text(new Rect(x, y, w, 40f), "No friends yet. Add one by name above.", 13, Gui.Muted, TextAnchor.UpperLeft, false);
            foreach (var f in social.Friends)
            {
                if (y > r.yMax - 110f) { Gui.Text(new Rect(x, y, w, 20f), "...", 13, Gui.Muted); break; }
                Gui.Rect(new Rect(x, y, w, 40f), Gui.PanelLight);
                Gui.Skew(new Rect(x + 12f, y + 15f, 10f, 10f), f.Online ? Gui.Green : Gui.Muted, 0.3f);
                Gui.Text(new Rect(x + 30f, y, w - 280f, 40f), f.Name + (f.Online ? "" : "  (offline)"), 14, Gui.Paper, TextAnchor.MiddleLeft, false);
                if (Gui.Button(new Rect(x + w - 240f, y + 4f, 130f, 32f), "INVITE", Gui.P1, 13, f.Online && !busy && !social.Working)
                    && f.Online && !busy)
                { onlineNote = ""; social.SendInvite(f.Id); }
                // Two-click remove: the first click arms "SURE?" for 3 seconds.
                bool armed = removeArmedId == f.Id && Time.unscaledTime < removeArmedUntil;
                if (Gui.Button(new Rect(x + w - 102f, y + 4f, 90f, 32f), armed ? "SURE?" : "REMOVE", armed ? Gui.Red : Gui.Steel, 11, !social.Working))
                {
                    if (armed) { social.RemoveFriend(f.Id); removeArmedId = null; }
                    else { removeArmedId = f.Id; removeArmedUntil = Time.unscaledTime + 3f; }
                }
                y += 46f;
            }

            if (roomsSvc.Current != null)
                Gui.Text(new Rect(x, r.yMax - 92f, w, 24f), "INVITE puts friends into your room \"" + roomsSvc.Current.Name + "\" (" + roomsSvc.Current.Code + ").", 13, Gui.Gold);
            string status = rooms.State == Netcode.ChibiRoomState.Failed ? rooms.Status : social.Status;
            bool err = rooms.State == Netcode.ChibiRoomState.Failed || social.StatusIsError;
            Gui.Text(new Rect(x, r.yMax - 66f, w, 60f), status, 14, err ? Gui.Red : Gui.Paper, TextAnchor.UpperCenter, false);
        }

        void LobbyBottomButtons(bool busy, Rect a)
        {
            float by = a.yMax - 48f;
            if (busy && Gui.Button(new Rect(a.xMax - 440f, by, 180f, 46f), "CANCEL", Gui.Red, 17))
            {
                if (pendingOnline != null) { Destroy(pendingOnline.gameObject); pendingOnline = null; pendingOnlineInput = null; }
                social.WithdrawInvite();   // the invited friend's pop-up disappears instead of pointing at a dead room
                rooms.Cancel();
                onlineNote = "";
            }
            bool fromRoom = roomsSvc.Current != null && !busy;
            if (Gui.Button(new Rect(a.xMax - 240f, by, 240f, 46f), fromRoom ? "BACK TO ROOM" : "BACK", Gui.Steel, 18)) LobbyBack();
        }

        void LobbyBack()
        {
            UiSound.Back();
            bool fromRoom = roomsSvc.Current != null && !OnlineBusy;
            if (fromRoom) { Go(Page.Room); return; }   // the room and its invites stay
            if (pendingOnline != null) { Destroy(pendingOnline.gameObject); pendingOnline = null; pendingOnlineInput = null; }
            social.WithdrawInvite();
            rooms.Cancel();
            Go(Page.Online);
        }

        /// <summary>Invite pop-up on every menu screen: ACCEPT joins the friend's room, DECLINE tells them. Expires after 60 s.</summary>
        void InvitePopup()
        {
            if (social == null || social.Invites.Count == 0 || !PlayerIdentity.OnlineAllowed(Save.ageBand)) return;
            var inv = social.Invites[0];
            var r = new Rect(W * 0.5f - 270f, 120f, 540f, 160f);
            Gui.Glow(new Rect(r.x - 30f, r.y - 30f, r.width + 60f, r.height + 60f), new Color(1f, 0.8f, 0.2f, 0.35f));
            Gui.Box(r, Gui.Gold);
            Gui.Display(new Rect(r.x, r.y + 10f, r.width, 40f), "MATCH INVITE", 32, Gui.Gold, TextAnchor.MiddleCenter);
            int left = Mathf.Max(0, Mathf.CeilToInt(inv.ExpiresAt - Time.unscaledTime));
            Gui.Text(new Rect(r.x, r.y + 52f, r.width, 28f), inv.FromName + " wants to fight you  (" + left + "s)", 16, Gui.Paper);
            Gui.PreferNext();
            if (Gui.Button(new Rect(r.x + 60f, r.y + 96f, 190f, 46f), "ACCEPT", Gui.Green, 17))
            {
                pending.cpu[0] = pending.cpu[1] = false;
                pending.training = false;
                onlineNote = "";
                social.AcceptInvite(0);   // joins the friend's room
                Enter(Page.Room);
            }
            if (Gui.Button(new Rect(r.xMax - 250f, r.y + 96f, 190f, 46f), "DECLINE", Gui.Red, 17)) social.DeclineInvite(0);
        }

        // ---------------------------------------------------------------- ROOMS (create room mode)

        void OpenRoomsPage()
        {
            pending.cpu[0] = pending.cpu[1] = false;
            pending.training = false;
            onlineNote = "";
            roomDialog = false;
            Enter(Page.Rooms);
            roomsSvc.Refresh(force: true);
        }

        /// <summary>
        /// ROOMS page. No rooms yet: one big CREATE ROOM in the middle (and join by code). With rooms: CREATE ROOM moves
        /// to the top-right corner and every room we are in is a block; click a block to go in.
        /// </summary>
        void RoomsView()
        {
            Backdrop();
            NavBar();
            var a = BeginPage("02", "ROOMS", "YOUR OWN ROOMS WITH FRIENDS");
            bool working = roomsSvc.Working;
            bool any = roomsSvc.MyRooms.Count > 0;

            if (!any)
            {
                if (working && !roomsSvc.ListLoaded)
                    Gui.Text(new Rect(a.x, a.y + a.height * 0.35f, a.width, 40f), "Loading your rooms...", 18, Gui.Muted);
                else
                {
                    var big = new Rect(a.center.x - 230f, a.y + a.height * 0.22f, 460f, 100f);
                    Gui.Glow(new Rect(big.x - 30f, big.y - 30f, big.width + 60f, big.height + 60f), new Color(1f, 0.3f, 0.4f, 0.3f));
                    Gui.PreferNext();
                    if (Gui.Button(big, "CREATE ROOM", Gui.P1, 34, !working)) OpenRoomDialog();
                    Gui.Text(new Rect(a.x, big.yMax + 14f, a.width, 24f), "Name your room, invite a friend, both press OK, then START FIGHT.", 14, Gui.Muted);
                    var cf = new Rect(a.center.x - 230f, big.yMax + 70f, 300f, 44f);
                    Gui.Text(new Rect(cf.x, cf.y - 22f, 300f, 20f), "Or join a friend's room with its code:", 12, Gui.Muted, TextAnchor.MiddleLeft);
                    joinCode = GUI.TextField(cf, joinCode ?? "", 12, Field(20)).ToUpperInvariant();
                    if (Gui.Button(new Rect(cf.xMax + 10f, cf.y, 150f, 44f), "JOIN", Gui.P2, 18, !working))
                    { social.ClearMatchFriend(); roomsSvc.JoinByCode(joinCode); Enter(Page.Room); }
                }
            }
            else
            {
                // Top-right: CREATE ROOM, and join by code next to it.
                Gui.PreferNext();
                if (Gui.Button(new Rect(a.xMax - 240f, a.y, 240f, 46f), "+ CREATE ROOM", Gui.P1, 18, !working)) OpenRoomDialog();
                var cf = new Rect(a.xMax - 580f, a.y + 3f, 200f, 40f);
                joinCode = GUI.TextField(cf, joinCode ?? "", 12, Field(20)).ToUpperInvariant();
                if (Gui.Button(new Rect(cf.xMax + 8f, a.y + 3f, 110f, 40f), "JOIN", Gui.P2, 15, !working))
                { social.ClearMatchFriend(); roomsSvc.JoinByCode(joinCode); Enter(Page.Room); }
                Gui.Text(new Rect(a.x, a.y, 520f, 46f), "YOUR ROOMS (" + roomsSvc.MyRooms.Count + ")  ·  click a room to go in", 15, Gui.Gold, TextAnchor.MiddleLeft);

                // Room blocks, 3 per row.
                float bw = (a.width - 40f) / 3f, bh = 140f;
                for (int k = 0; k < roomsSvc.MyRooms.Count; k++)
                {
                    var room = roomsSvc.MyRooms[k];
                    var b = new Rect(a.x + (k % 3) * (bw + 20f), a.y + 62f + (k / 3) * (bh + 16f), bw, bh);
                    if (b.yMax > a.yMax - 110f) break;
                    bool click = Gui.Hit(b, out bool hot);
                    if (hot) Gui.Glow(new Rect(b.x - 16f, b.y - 16f, b.width + 32f, b.height + 32f), new Color(1f, 0.8f, 0.3f, 0.3f));
                    Gui.Box(b, hot ? Gui.Paper : Gui.Gold);
                    if (hot) Gui.Frame(b, Gui.Paper, 2f);
                    Gui.Display(new Rect(b.x + 18f, b.y + 12f, b.width - 36f, 34f), room.Name, 26, Gui.Paper, TextAnchor.MiddleLeft, 0.15f);
                    Gui.Text(new Rect(b.x + 18f, b.y + 46f, b.width - 36f, 22f), "CODE  " + room.Code, 14, Gui.Gold, TextAnchor.MiddleLeft);
                    string who = room.Slots.Count == 0 ? "empty" : room.Slots[0].Name + (room.Slots.Count > 1 ? "  vs  " + room.Slots[1].Name : "  ·  waiting for a friend");
                    Gui.Text(new Rect(b.x + 18f, b.y + 72f, b.width - 36f, 24f), who, 15, Gui.Paper, TextAnchor.MiddleLeft);
                    Gui.Text(new Rect(b.x + 18f, b.y + 100f, b.width - 36f, 22f),
                             room.Slots.Count + " / 2 players" + (room.HostId == roomsSvc.MyId ? "  ·  YOU HOST" : ""), 13, Gui.Muted, TextAnchor.MiddleLeft);
                    if (click) { roomsSvc.Enter(room.Id); Enter(Page.Room); }
                }
            }

            Gui.Text(new Rect(a.x, a.yMax - 100f, a.width, 40f), roomsSvc.Status, 15, roomsSvc.StatusIsError ? Gui.Red : Gui.Paper,
                     TextAnchor.UpperCenter, false);
            float by = a.yMax - 48f;
            if (Gui.Button(new Rect(a.x, by, 180f, 46f), "REFRESH", Gui.P2, 17, !working)) roomsSvc.Refresh(force: true);
            if (Gui.Button(new Rect(a.xMax - 240f, by, 240f, 46f), "BACK", Gui.Steel, 18)) { UiSound.Back(); Go(Page.Online); }
            EndPage();
            NavHints("ONLINE  ·  ROOMS");
        }

        /// <summary>
        /// Inside a room: choose a fighter (left), press OK to be "in". START FIGHT works only when both players are in;
        /// either one may press it and the fight starts for both. After the fight both come back here.
        /// </summary>
        void RoomView()
        {
            var cur = roomsSvc.Current;
            Backdrop();
            NavBar();
            if (cur == null)
            {
                if (!roomsSvc.Working) { Enter(Page.Rooms); return; }   // join / create failed: the status shows why
                var w0 = BeginPage("02", "ROOM", "OPENING...");
                Gui.Text(new Rect(w0.x, w0.y + w0.height * 0.35f, w0.width, 40f), roomsSvc.Status.Length > 0 ? roomsSvc.Status : "Opening the room...", 18, Gui.Paper);
                EndPage();
                return;
            }
            var a = BeginPage("02", "ROOM", cur.Name.ToUpperInvariant() + "  ·  CODE " + cur.Code);
            string me = roomsSvc.MyId;
            bool host = roomsSvc.IsHost, ready = roomsSvc.LocalReady, launching = roomsSvc.Launching || pendingOnline != null;
            float ph = a.height - 62f;

            // Left: our fighter. Locked once OK is pressed (CHANGE unlocks).
            var left = new Rect(a.x, a.y, a.width * 0.5f - 10f, ph);
            var keepCls = pending.cls[0]; int keepPassive = pending.passive[0], keepWeapon = pending.weapon[0];
            FighterPicker(left, !ready && !launching);
            if (ready || launching) { pending.cls[0] = keepCls; pending.passive[0] = keepPassive; pending.weapon[0] = keepWeapon; }
            if (ready) Gui.Text(new Rect(left.x, left.y + 8f, left.width - 20f, 24f), "LOCKED IN", 15, Gui.Green, TextAnchor.MiddleRight);
            roomsSvc.SetPick(pending.cls[0], pending.passive[0]);
            if (host) pending.stage = cur.Stage;   // the handshake uses the host's stage

            // Right: the room.
            var r = new Rect(left.xMax + 20f, a.y, a.width - left.width - 20f, ph);
            Gui.Box(r, Gui.Gold);
            float x = r.x + 24f, w = r.width - 48f, y = r.y + 12f;
            Gui.Display(new Rect(x, y, w, 36f), cur.Name, 30, Gui.Paper, TextAnchor.MiddleLeft, 0.15f);
            Gui.Text(new Rect(x, y, w, 36f), "CODE  " + cur.Code, 18, Gui.Gold, TextAnchor.MiddleRight);
            y += 40f;
            Gui.Text(new Rect(x, y, w, 18f), "Share the code, or INVITE a friend by name.", 12, Gui.Muted, TextAnchor.MiddleLeft);
            y += 24f;

            for (int i = 0; i < Netcode.ChibiRoomRules.Seats; i++)
            {
                var row = new Rect(x, y, w, 62f);
                Gui.Rect(row, Gui.PanelLight);
                Gui.Rect(new Rect(row.x, row.y, 4f, row.height), Gui.PlayerColor(i));
                if (i < cur.Slots.Count)
                {
                    var slot = cur.Slots[i];
                    var ch = CharacterRoster.Of(slot.Class);
                    var port = new Rect(row.x + 10f, row.y + 6f, 50f, 50f);
                    CharacterArt.DrawPortrait(port, ch);
                    Gui.Frame(port, ch.Primary, 1f);
                    string tag = (i == 0 ? "HOST" : "GUEST") + (slot.PlayerId == me ? " · YOU" : "");
                    Gui.Text(new Rect(row.x + 70f, row.y + 6f, w - 240f, 26f), slot.Name, 19, Gui.Paper, TextAnchor.MiddleLeft);
                    Gui.Text(new Rect(row.x + 70f, row.y + 32f, w - 240f, 22f), tag + "  ·  " + ch.Name + " (" + ch.Role + ")", 13, Gui.Muted, TextAnchor.MiddleLeft);
                    Gui.Display(new Rect(row.xMax - 170f, row.y, 156f, 62f), slot.Ready ? "READY" : "choosing...", slot.Ready ? 24 : 15,
                                slot.Ready ? Gui.Green : Gui.Muted, TextAnchor.MiddleRight, slot.Ready ? 0.15f : 0f);
                }
                else Gui.Text(new Rect(row.x + 14f, row.y, w - 28f, 62f), "Empty seat · waiting for a friend to join", 15, Gui.Muted, TextAnchor.MiddleLeft);
                y += 70f;
            }

            // Stage (host chooses; the guest sees it).
            Gui.Text(new Rect(x, y, 220f, 22f), host ? "STAGE (you choose)" : "STAGE (host chooses)", 13, Gui.Gold, TextAnchor.MiddleLeft);
            bool canPick = host && !ready && !launching;
            // Host option (ST-5), stored in the room next to the stage (key "hz"); the guest reads the host's value.
            if (host)
            {
                pending.hazards = roomsSvc.HazardsOn;
                if (HazardsButton(new Rect(x + w - 128f, y - 1f, 128f, 24f), pending.hazards, canPick) && canPick)
                {
                    pending.hazards = !pending.hazards;
                    roomsSvc.SetHazards(pending.hazards);
                }
            }
            else Gui.Text(new Rect(x + w - 200f, y, 200f, 22f), (roomsSvc.HazardsOn ? "HAZARDS ON" : "HAZARDS OFF") + " · HOST'S PICK",
                          12, Gui.Muted, TextAnchor.MiddleRight, true, false);
            y += 26f;
            int pick = StageGrid(new Rect(x, y, w, StageGridHeight(30f)), cur.Stage, canPick);
            if (pick >= 0 && canPick) roomsSvc.SetStage(pick);
            y += StageGridHeight(30f) + 14f;

            // OK / CHANGE, then START FIGHT (only when both are ready; either player may press it).
            if (!ready)
            {
                bool canOk = !roomsSvc.Working && !launching && roomsSvc.Synced;
                Gui.PreferNext();
                if (Gui.Button(new Rect(x, y, w * 0.4f, 54f), roomsSvc.Synced ? "OK" : "LOADING...", Gui.Green, 22, canOk) && canOk) roomsSvc.SetReady(true);
            }
            else if (Gui.Button(new Rect(x, y, w * 0.4f, 54f), "CHANGE", Gui.Steel, 18, !roomsSvc.Working && !launching)) roomsSvc.SetReady(false);
            bool canStart = Netcode.ChibiRoomRules.CanStart(cur) && !launching;
            if (canStart) Gui.Glow(new Rect(x + w * 0.42f - 20f, y - 20f, w * 0.58f + 40f, 94f), new Color(1f, 0.3f, 0.4f, 0.35f));
            if (canStart && ready) Gui.PreferNext();
            if (Gui.Button(new Rect(x + w * 0.42f, y, w * 0.58f, 54f), launching ? "STARTING..." : "START FIGHT", Gui.P1, 22, canStart) && canStart)
            {
                onlineNote = "";
                social.ClearMatchFriend();
                roomsSvc.PressStart();
            }
            y += 62f;
            string hint = Netcode.ChibiRoomRules.WaitingFor(cur, me);
            string status = onlineNote.Length > 0 ? onlineNote : roomsSvc.Status;
            if (!launching && hint.Length > 0 && !roomsSvc.StatusIsError && onlineNote.Length == 0) status = hint;
            Gui.Text(new Rect(x, y, w, 50f), status, 14, roomsSvc.StatusIsError || onlineNote.Length > 0 ? Gui.Red : Gui.Paper, TextAnchor.UpperCenter, false);

            // Bottom: invite friends (FRIENDS tab keeps this room), leave for good, or back to the room list.
            float by = a.yMax - 48f;
            if (Gui.Button(new Rect(a.x, by, 230f, 46f), "INVITE FRIENDS", Gui.P2, 17, !launching)) OpenLobby(true);
            bool armed = leaveArmedId == cur.Id && Time.unscaledTime < leaveArmedUntil;
            if (Gui.Button(new Rect(a.xMax - 440f, by, 180f, 46f), armed ? "SURE?" : host ? "CLOSE ROOM" : "LEAVE ROOM", Gui.Red, 15, !launching))
            {
                if (armed) { roomsSvc.Leave(cur.Id); leaveArmedId = null; Enter(Page.Rooms); }
                else { leaveArmedId = cur.Id; leaveArmedUntil = Time.unscaledTime + 3f; }
            }
            if (Gui.Button(new Rect(a.xMax - 240f, by, 240f, 46f), "BACK", Gui.Steel, 18, !launching))
            {
                UiSound.Back();
                roomsSvc.ExitToList();
                Enter(Page.Rooms);
            }
            EndPage();
            NavHints("ONLINE  ·  ROOM");
        }

        // ---------------------------------------------------------------- shared stage grid (create room / lobby / room)

        const int StageGridCols = 4;
        const float StageGridGap = 8f;

        /// <summary>Height of the stage grid for a given button row height (StageCount buttons, 4 per row).</summary>
        static float StageGridHeight(float rowH)
        {
            int rows = (StageCount + StageGridCols - 1) / StageGridCols;
            return rows * rowH + (rows - 1) * StageGridGap;
        }

        /// <summary>
        /// Stage picker used by every online screen: <see cref="StageBuilder.ShortNames"/> as a 4-column grid in
        /// <see cref="StageBuilder.MenuOrder"/> (design stages first). Returns the clicked SIM stage index, or -1.
        /// </summary>
        int StageGrid(Rect area, int selected, bool enabled, int size = 13)
        {
            int n = StageCount, rows = (n + StageGridCols - 1) / StageGridCols;
            float bw = (area.width - StageGridGap * (StageGridCols - 1)) / StageGridCols;
            float bh = (area.height - StageGridGap * (rows - 1)) / rows;
            int picked = -1;
            for (int k = 0; k < n; k++)
            {
                int st = StageAt(k);
                var b = new Rect(area.x + (k % StageGridCols) * (bw + StageGridGap), area.y + (k / StageGridCols) * (bh + StageGridGap), bw, bh);
                if (Gui.Button(b, StageBuilder.ShortNames[st].ToUpperInvariant(), Gui.Gold, size, enabled, selected == st) && enabled) picked = st;
            }
            return picked;
        }

        /// <summary>HAZARDS ON / OFF toggle button (ST-5). Returns true when pressed; the caller flips its own value.</summary>
        static bool HazardsButton(Rect r, bool on, bool enabled) =>
            Gui.Button(r, on ? "HAZARDS  ON" : "HAZARDS  OFF", on ? Gui.P1 : Gui.Steel, 12, enabled, on);

        // ---------------------------------------------------------------- compact fighter picker (lobby / room)

        /// <summary>
        /// Our fighter for online play: the five characters as portrait tiles, the chosen one's sheet (role, weapon,
        /// stat bars), passive and cosmetic weapon. Writes only the menu's pending pick (P1).
        /// </summary>
        void FighterPicker(Rect r, bool enabled)
        {
            Gui.Box(r, Gui.P1);
            float x = r.x + 20f, w = r.width - 40f, y = r.y + 10f;
            string myName = DisplayedName();
            Gui.Display(new Rect(x, y, w, 28f), "YOUR FIGHTER", 24, Gui.Paper, TextAnchor.MiddleLeft, 0.15f);
            if (myName.Length > 0) Gui.Text(new Rect(x, y, w, 28f), myName, 13, Gui.Gold, TextAnchor.MiddleRight);
            y += 36f;

            int n = CharacterRoster.All.Length;
            float tw = (w - (n - 1) * 8f) / n, th = Mathf.Min(tw, 104f);
            for (int i = 0; i < n; i++)
            {
                var c = CharacterRoster.All[i];
                var t = new Rect(x + i * (tw + 8f), y, tw, th + 22f);
                bool sel = pending.cls[0] == c.Class;
                if (sel) Gui.PreferNext();
                bool click = Gui.Hit(t, out bool hot, enabled);
                Gui.Rect(t, Color.Lerp(new Color(0.08f, 0.12f, 0.22f), c.Primary, sel ? 0.45f : 0.2f));
                CharacterArt.DrawPortrait(new Rect(t.x + (tw - th) * 0.5f, t.y, th, th), c, enabled || sel ? 1f : 0.45f);
                var bar = new Rect(t.x, t.yMax - 22f, t.width, 22f);
                Gui.Rect(bar, new Color(0.03f, 0.05f, 0.1f, 0.92f));
                Gui.Rect(new Rect(bar.x, bar.y, bar.width, 2f), c.Primary);
                Gui.Text(bar, Hud.CharNameUpper(c.Class), 12, sel ? Gui.Gold : Gui.Paper, TextAnchor.MiddleCenter, true, false);
                Gui.Frame(t, sel ? Gui.P1 : hot ? Gui.Paper : Gui.Steel, sel ? 3f : hot ? 2f : 1f);
                if (click && enabled && !sel)
                {
                    pending.cls[0] = c.Class;
                    pending.passive[0] = 0;
                    pending.weapon[0] = 0;
                }
            }
            y += th + 32f;

            var ch = CharacterRoster.Of(pending.cls[0]);
            var def = ClassDef.Get(pending.cls[0]);
            Gui.Display(new Rect(x, y, w, 40f), Hud.CharNameUpper(ch.Class), 36, Gui.Paper, TextAnchor.MiddleLeft, 0.2f, ch.Primary, 3f);
            float nw = Gui.Measure(Hud.CharNameUpper(ch.Class), 36, true) + 20f;
            Gui.Display(new Rect(x + nw, y + 4f, w - nw, 22f), ch.Role.ToUpper(), 18, Gui.Gold, TextAnchor.MiddleLeft, 0.15f);
            Gui.Text(new Rect(x + nw, y + 24f, w - nw, 16f), ch.Weapon.ToUpper(), 11, Gui.Muted, TextAnchor.MiddleLeft, true, false);
            y += 44f;
            Gui.Text(new Rect(x, y, w, 30f), ch.Style, 11, Gui.Paper, TextAnchor.UpperLeft, false, false);
            y += 30f;
            for (int i = 0; i < 5; i++) StatRow(x, y + i * 12f, w * 0.6f, StatNames[i], Stat(ch, i), ch.Primary);
            for (int p = 0; p < 3; p++)
                Gui.Skew(new Rect(x + w - 66f + p * 22f, y + 4f, 16f, 10f), p == 0 ? ch.Primary : p == 1 ? ch.Accent : ch.Fx, 0.3f);
            Gui.Text(new Rect(x + w * 0.62f, y + 18f, w * 0.38f, 40f), "ULT  " + def.ultName, 11, Gui.Gold, TextAnchor.UpperRight, true, false);
            y += 66f;

            // passive
            Gui.Text(new Rect(x, y, w, 16f), "PASSIVE", 11, Gui.Gold, TextAnchor.MiddleLeft, true, false);
            float pw = (w - 8f) * 0.5f;
            for (int p = 0; p < 2; p++)
                if (Gui.Button(new Rect(x + p * (pw + 8f), y + 17f, pw, 26f), def.passiveNames[p].ToUpper(), Gui.P1, 12, enabled, pending.passive[0] == p) && enabled)
                    pending.passive[0] = p;
            Gui.Text(new Rect(x, y + 44f, w, 16f), def.passiveDescs[Mathf.Clamp(pending.passive[0], 0, 1)], 11, Gui.Muted, TextAnchor.MiddleLeft, false, false);
            y += 64f;

            // Secondary: the cosmetic weapon look from the armory (signature weapon shown under the name).
            var variants = WeaponDef.ForClass(def.type);
            pending.weapon[0] = Mathf.Clamp(pending.weapon[0], 0, variants.Count - 1);
            var weapon = WeaponDef.Get(def.type, pending.weapon[0]);
            if (variants.Count > 1 && y + 24f <= r.yMax)
            {
                Gui.Text(new Rect(x, y, 70f, 24f), "LOOK", 11, Gui.Gold, TextAnchor.MiddleLeft, true, false);
                if (Gui.Hit(new Rect(x + 64f, y, 26f, 24f), out bool hl, enabled) && enabled) { pending.weapon[0] = Mod(pending.weapon[0] - 1, variants.Count); UiSound.Step(); }
                Gui.Chevron(new Rect(x + 72f, y + 5f, 9f, 14f), hl ? Gui.Paper : Gui.P1, false);
                Gui.Text(new Rect(x + 92f, y, w - 184f, 24f), weapon.name.ToUpper() + "  <color=#9AA6BC>" + (pending.weapon[0] + 1) + "/" + variants.Count + "</color>", 13, Gui.Paper, TextAnchor.MiddleCenter, true, false);
                if (Gui.Hit(new Rect(x + w - 90f, y, 26f, 24f), out bool hr, enabled) && enabled) { pending.weapon[0] = Mod(pending.weapon[0] + 1, variants.Count); UiSound.Step(); }
                Gui.Chevron(new Rect(x + w - 82f, y + 5f, 9f, 14f), hr ? Gui.Paper : Gui.P1);
                Gui.Text(new Rect(x, y, w, 24f), "COSMETIC", 10, Gui.Muted, TextAnchor.MiddleRight, true, false);
            }
        }
    }
}
