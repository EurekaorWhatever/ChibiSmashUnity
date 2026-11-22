using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Onboarding (age, player name, optional Google account) and the Vs Online Solo pages: Page.Rooms (the Solo hub:
    /// CREATE ROOM, JOIN ROOM by code, MATCHMAKING, and the rooms we are in), Page.Room (waiting for players, room code
    /// box with COPY, invite a friend by name, start when full and ready) and Page.Matchmaking (searching for a player
    /// near you). Rooms are ChibiRoomService rooms, matchmaking is NetworkRoomManager.QuickMatch and name invites are
    /// ChibiNameInvites; online services are only called, never re-implemented here.
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
        string onlineNote = "";

        // Friends (player names, friend list, invites): Unity Authentication + Unity Friends on top of the private rooms.
        Netcode.ChibiSocialManager social;
        string nameInput = "";
        // Player name dialog: forced on first launch (no saved name), opened from the profile chip in the nav bar.
        bool nameDialog;
        bool roomDialog;          // CREATE ROOM window (Vs Online Solo hub)
        bool joinDialog;          // JOIN ROOM window: ENTER ROOM CODE (Vs Online Solo hub)
        string joinError = "";    // JOIN ROOM window: a code that is not a valid room code
        // Invite a friend by name into our room (host, Room page), and room invites to us (pop-up on online menu pages).
        Netcode.ChibiNameInvites nameInvites;
        string inviteNameInput = "";
        /// <summary>When the current matchmaking search was started (unscaled time), for the elapsed clock.</summary>
        float matchmakingSince;
        Netcode.ChibiAccount account;   // optional SIGN IN WITH GOOGLE (Unity Player Accounts): name on other computers
        /// <summary>Product decision 2026-11-09: no sign-in step on first launch (identity is automatic per computer user).</summary>
        const bool ShowAccountStepOnFirstLaunch = false;
        Netcode.ChibiRoomService roomsSvc;   // CREATE ROOM mode: named rooms, OK / START FIGHT
        string leaveArmedId;      // two-click LEAVE ROOM
        float leaveArmedUntil;
        string roomError = "";
        string nameError = "", ageInput = "";
        /// <summary>Id of the lobby whose full-and-ready state already auto-pressed START FIGHT (null: armed again).</summary>
        string autoStartedFor;

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

            bool enter = EnterPressed() || TakePadSubmit();
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
                if (band == AgeBand.Unknown) { nameError = "Type your age, or set it with the D-pad."; UiSound.Error(); }
                else
                {
                    Save.ageBand = (int)band;
                    Save.age = PlayerIdentity.ParseAge(ageInput);
                    Save.Save();
                    nameError = "";
                    ageInput = "";
                }
                if (enter && Event.current.type == EventType.KeyDown) Event.current.Use();
            }
            Gui.Text(new Rect(r.x, r.yMax - 30f, r.width, 22f), "Gamepad: D-pad up/down changes it by 1, left/right by 10 · A = next", 12, Gui.Muted);
        }

        void OpenRoomDialog()
        {
            roomDialog = true;
            roomError = "";
            if (string.IsNullOrEmpty(roomNameInput)) roomNameInput = Netcode.ChibiSocialRules.DefaultRoomName(DisplayedName());
        }

        /// <summary>
        /// CREATE ROOM window (Vs Online Solo hub): name the room and pick the stage; it is created and opened at once
        /// (choose your fighter there, press OK, and share the code or invite a friend by name).
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

            // While the room service is busy (the room list refresh) a create would be ignored: wait for it.
            bool canCreate = !roomsSvc.Working;
            Gui.PreferNext();
            if ((Gui.Button(new Rect(r.x + r.width * 0.5f - 230f, r.y + 300f, 220f, 56f), canCreate ? "CREATE ROOM" : "ONE MOMENT...", Gui.P1, 20, canCreate) || enter) && canCreate)
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
            // First launch: start from a valid fighter name so a gamepad player (no keyboard) can just press A (item 18).
            if (nameInput.Length == 0) nameInput = Netcode.ChibiSocialRules.DefaultFighterName((uint)System.Environment.TickCount);
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

            bool enter = EnterPressed() || TakePadSubmit();
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
                if (enter && Event.current.type == EventType.KeyDown) Event.current.Use();
            }
            Gui.Text(new Rect(r.x, r.yMax - 30f, r.width, 22f), first ? "Gamepad: A = let's fight · Y = another name" : "Gamepad: A = save · Y = another name", 12, Gui.Muted);
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

        // ---------------------------------------------------------------- VS ONLINE · SOLO 1V1 hub (ChibiRoomService rooms)

        /// <summary>Vs Online, Solo: the hub page (CREATE ROOM, JOIN ROOM by code, MATCHMAKING, and the rooms we are in).</summary>
        void OpenRoomsPage()
        {
            pending.cpu[0] = pending.cpu[1] = false;
            pending.training = false;
            onlineNote = "";
            roomDialog = false;
            joinDialog = false;
            Enter(Page.Rooms);
            roomsSvc.Refresh(force: true);
        }

        /// <summary>
        /// Vs Online Solo hub. Three mode cards: CREATE ROOM (name dialog, then the room), JOIN ROOM (ENTER ROOM CODE
        /// dialog, then the friend's room) and MATCHMAKING (searching page, no room). Right panel: YOUR ROOMS (rooms we
        /// are a member of; click one to go in), the room service status (errors in red), REFRESH and BACK.
        /// </summary>
        void RoomsView()
        {
            bool onlineOk = PlayerIdentity.OnlineAllowed(Save.ageBand);
            if (!onlineOk) { Enter(Page.Play); return; }   // age gate (13+), defence in depth
            Backdrop();
            NavBar();
            bool net = InternetUp;
            bool working = roomsSvc.Working;
            var a = BeginPage("01", "VS ONLINE", "SOLO 1V1  ·  CREATE ROOM · JOIN ROOM · MATCHMAKING");
            float qw = 380f;
            float cw = (a.width - qw - 30f - 44f) / 3f;
            float ch = Mathf.Min(a.height - 16f, 430f);
            // Same gates as the other online cards: 13+ (the page is already gated) and an internet connection.
            bool locked = !onlineOk || !net;
            string lockTag = !onlineOk ? "13+ ONLY" : "NO INTERNET";

            if (ModeCard(0, new Rect(a.x, a.y + 8f, cw, ch), "CREATE\nROOM", locked ? lockTag : "HOST · 2 PLAYERS",
                         "Host a room, then invite a friend to it.", Gui.P1, locked ? "LOCKED" : "NAME YOUR ROOM", locked))
            {
                if (locked) DenyOnline(onlineOk);
                else OpenRoomDialog();
            }
            if (ModeCard(1, new Rect(a.x + cw + 22f, a.y + 8f, cw, ch), "JOIN\nROOM", locked ? lockTag : "ROOM CODE",
                         "Type or paste your friend's room code.", Gui.P2, locked ? "LOCKED" : "ENTER ROOM CODE", locked))
            {
                if (locked) DenyOnline(onlineOk);
                else OpenJoinDialog();
            }
            if (ModeCard(2, new Rect(a.x + 2f * (cw + 22f), a.y + 8f, cw, ch), "MATCH\nMAKING", locked ? lockTag : "NO ROOM · AUTO",
                         "Fight a player searching now, nearest first.", Gui.Gold, locked ? "LOCKED" : "FIND A PLAYER NEAR YOU", locked))
            {
                if (locked) DenyOnline(onlineOk);
                else StartMatchmaking();
            }

            // Right: YOUR ROOMS, status, REFRESH and BACK.
            var info = new Rect(a.xMax - qw, a.y + 8f, qw, a.height - 8f);
            Gui.Box(info, Gui.Gold);
            float x = info.x + 22f, w = info.width - 44f, y = info.y + 16f;
            int count = roomsSvc.MyRooms.Count;
            Gui.Display(new Rect(x, y, w, 30f), "YOUR ROOMS", 26, Gui.Gold, TextAnchor.MiddleLeft, 0.18f);
            if (count > 0) Gui.Text(new Rect(x, y, w, 30f), count + "  ·  click to go in", 12, Gui.Muted, TextAnchor.MiddleRight, true, false);
            y += 42f;
            float listEnd = info.yMax - 170f;
            if (count == 0)
            {
                string empty = working && !roomsSvc.ListLoaded ? "Loading your rooms..." : "No rooms yet. Create one, or join a friend's room with its code.";
                Gui.Text(new Rect(x, y, w, 60f), empty, 14, Gui.Muted, TextAnchor.UpperLeft, false);
            }
            for (int k = 0; k < count; k++)
            {
                var room = roomsSvc.MyRooms[k];
                var b = new Rect(x, y, w, 64f);
                if (b.yMax > listEnd)
                {
                    Gui.Text(new Rect(x, y, w, 20f), "+ " + (count - k) + " more", 12, Gui.Muted, TextAnchor.MiddleLeft);
                    break;
                }
                bool click = Gui.Hit(b, out bool hot);
                Gui.Rect(b, hot ? Color.Lerp(Gui.PanelLight, Gui.Gold, 0.15f) : Gui.PanelLight);
                Gui.Rect(new Rect(b.x, b.y, 4f, b.height), Gui.Gold);
                if (hot) Gui.Frame(b, Gui.Paper, 2f);
                Gui.Text(new Rect(b.x + 14f, b.y + 6f, b.width - 28f, 26f), room.Name, 17, Gui.Paper, TextAnchor.MiddleLeft);
                string seats = Mathf.Min(room.Slots.Count, Netcode.ChibiRoomRules.Seats) + " / " + Netcode.ChibiRoomRules.Seats;
                Gui.Text(new Rect(b.x + 14f, b.y + 34f, b.width - 28f, 22f),
                         "CODE  " + room.Code + "   ·   " + seats + (room.HostId == roomsSvc.MyId ? "   ·   YOU HOST" : ""), 12, Gui.Muted, TextAnchor.MiddleLeft);
                if (hot) Gui.Chevron(new Rect(b.xMax - 22f, b.y + 26f, 8f, 12f), Gui.Paper);
                if (click) { roomsSvc.Enter(room.Id); Enter(Page.Room); }
                y += 72f;
            }

            // Status / errors from the room service (a failed join lands here); with no status, a lost connection.
            string status = roomsSvc.Status;
            bool statusErr = roomsSvc.StatusIsError;
            if (status.Length == 0 && !net) { status = NoInternetMsg; statusErr = true; }
            // SEC-003: after 3 declines, room invites pause for a while (ChibiNameInvites.Paused).
            if (nameInvites != null && nameInvites.Paused)
                Gui.Text(new Rect(x, info.yMax - 164f, w, 18f), "Room invites paused for a few minutes", 11, Gui.Muted, TextAnchor.MiddleLeft, false, false);
            Gui.Text(new Rect(x, info.yMax - 140f, w, 62f), status, 13, statusErr ? Gui.Red : Gui.Paper, TextAnchor.UpperLeft, false);
            if (Gui.Button(new Rect(x, info.yMax - 70f, 130f, 50f), "REFRESH", Gui.P2, 15, !working)) roomsSvc.Refresh(force: true);
            if (Gui.Button(new Rect(x + 140f, info.yMax - 70f, w - 140f, 50f), "BACK", Gui.Steel, 18)) { UiSound.Back(); OpenPlayStyle(true); }
            EndPage();
            NavHints("VS ONLINE  ·  SOLO 1V1");
        }

        /// <summary>A locked online card was clicked: say why (age first, then the connection).</summary>
        void DenyOnline(bool onlineOk) => Deny(!onlineOk ? OnlineLockedMsg : NoInternetMsg);

        // ---------------------------------------------------------------- JOIN ROOM (ENTER ROOM CODE window)

        void OpenJoinDialog()
        {
            joinDialog = true;
            joinError = "";
            joinCode = "";
        }

        /// <summary>
        /// JOIN ROOM window: type or paste (Ctrl+V, or PASTE) the code a friend sent, then JOIN puts us straight into
        /// their room (the same path as before: JoinByCode, then the room page). A code that cannot be a room code is
        /// refused here; a failed join (no such room, full) shows in red on the hub, and here when reopened.
        /// </summary>
        void JoinDialog()
        {
            var r = DialogRect(600f, 320f);
            Gui.Box(r, Gui.P2);
            Gui.Display(new Rect(r.x, r.y + 14f, r.width, 44f), "ENTER ROOM CODE", 36, Gui.Paper, TextAnchor.MiddleCenter);
            Gui.Text(new Rect(r.x + 30f, r.y + 60f, r.width - 60f, 22f),
                     "Type or paste the room code your friend sent you (Discord, chat...).", 13, Gui.Muted, TextAnchor.UpperCenter, false);

            bool enter = EnterPressed();
            var field = new Rect(r.x + 70f, r.y + 96f, r.width - 140f - 120f, 56f);
            GUI.SetNextControlName("JoinCode");
            string typed = GUI.TextField(field, joinCode ?? "", 12, Field(30));
            if (typed != joinCode) { joinCode = typed.Trim().ToUpperInvariant(); joinError = ""; }
            if (GUI.GetNameOfFocusedControl() != "JoinCode") GUI.FocusControl("JoinCode");
            if (Gui.Button(new Rect(field.xMax + 10f, field.y + 6f, 110f, 44f), "PASTE", Gui.Steel, 15))
            {
                string clip = (GUIUtility.systemCopyBuffer ?? "").Trim().ToUpperInvariant();
                joinCode = clip.Length > 12 ? clip.Substring(0, 12) : clip;
                joinError = "";
            }

            string err = joinError.Length > 0 ? joinError : roomsSvc.StatusIsError ? roomsSvc.Status : "";
            Gui.Text(new Rect(r.x + 30f, r.y + 160f, r.width - 60f, 44f), err, 14, Gui.Red, TextAnchor.UpperCenter, false);

            bool canJoin = !roomsSvc.Working;
            Gui.PreferNext();
            if ((Gui.Button(new Rect(r.x + r.width * 0.5f - 230f, r.y + 222f, 220f, 56f), canJoin ? "JOIN" : "ONE MOMENT...", Gui.P2, 20, canJoin) || enter) && canJoin)
            {
                string code = Netcode.ChibiSocialRules.NormalizeCode(joinCode);
                if (code == null) { joinError = "That is not a room code. Check it and try again (letters and numbers only)."; UiSound.Error(); }
                else if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { joinError = OnlineLockedMsg; UiSound.Error(); }
                else
                {
                    joinDialog = false;
                    pending.cpu[0] = pending.cpu[1] = false;
                    pending.training = false;
                    onlineNote = "";
                    social.ClearMatchFriend();
                    roomsSvc.JoinByCode(code);
                    Enter(Page.Room);
                }
                if (enter) Event.current.Use();
            }
            if (Gui.Button(new Rect(r.x + r.width * 0.5f + 10f, r.y + 222f, 220f, 56f), "CANCEL", Gui.Steel, 18))
            {
                joinDialog = false;
                UiSound.Back();
            }
        }

        // ---------------------------------------------------------------- MATCHMAKING (searching page, no room)

        /// <summary>
        /// MATCHMAKING card: open the searching page and start a search (NetworkRoomManager.QuickMatch: nearest
        /// country / area first, widening with time). When it connects, OnRoomConnected / UpdatePendingOnline start the
        /// match as before. Refused (with the reason) under 13, without internet, or while a room fight is starting.
        /// </summary>
        void StartMatchmaking()
        {
            if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { Deny(OnlineLockedMsg); return; }
            if (!InternetUp) { Deny(NoInternetMsg); return; }
            if (roomsSvc.Launching || roomsSvc.OwnsLink) { Deny("A room fight is starting. Wait for it, or leave the room first."); return; }
            if (roomsSvc.Current != null) roomsSvc.ExitToList();   // not "in" a room while matchmaking (the room stays ours)
            pending.cpu[0] = pending.cpu[1] = false;
            pending.training = false;
            onlineNote = "";
            social.ClearMatchFriend();
            if (page != Page.Matchmaking) Enter(Page.Matchmaking);
            bool busy = rooms.State != Netcode.ChibiRoomState.Idle && rooms.State != Netcode.ChibiRoomState.Failed || pendingOnline != null;
            if (!busy)
            {
                matchmakingSince = Time.unscaledTime;
                rooms.QuickMatch();
            }
        }

        /// <summary>Stop a matchmaking search (and a handshake it started). Never touches a room fight's link.</summary>
        void StopMatchmaking()
        {
            if (roomsSvc.OwnsLink || roomsSvc.Launching) return;
            if (pendingOnline != null) { Destroy(pendingOnline.gameObject); pendingOnline = null; pendingOnlineInput = null; }
            rooms.Cancel();
            onlineNote = "";
        }

        /// <summary>
        /// MATCHMAKING page. Left: our fighter (pick while searching; locked once a player is found). Right: SEARCHING
        /// with an animated line, where the search looks now, the elapsed time and the status (errors in red), then
        /// CANCEL (while searching) or SEARCH AGAIN, and BACK to the Solo hub (which stops the search).
        /// </summary>
        void MatchmakingView()
        {
            if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { Enter(Page.Play); return; }   // age gate (13+), defence in depth
            Backdrop();
            NavBar();
            var st = rooms.State;
            bool connecting = pendingOnline != null || st == Netcode.ChibiRoomState.Connecting || st == Netcode.ChibiRoomState.Connected;
            bool failed = !connecting && (st == Netcode.ChibiRoomState.Failed || onlineNote.Length > 0);
            bool searching = !connecting && st != Netcode.ChibiRoomState.Idle && st != Netcode.ChibiRoomState.Failed;
            bool net = InternetUp;
            var a = BeginPage("01", "MATCHMAKING", "SOLO 1V1  ·  NO ROOM  ·  A PLAYER NEAR YOU");
            float ph = a.height - 62f;

            var left = new Rect(a.x, a.y, a.width * 0.5f - 10f, ph);
            FighterPicker(left, !connecting);

            var r = new Rect(left.xMax + 20f, a.y, a.width - left.width - 20f, ph);
            var sc = connecting ? Gui.Green : failed ? Gui.P1 : searching ? Gui.P2 : Gui.Steel;
            Gui.Box(r, sc);
            float x = r.x + 24f, w = r.width - 48f, y = r.y + 18f;
            float t = Time.unscaledTime;
            int dots = 1 + (int)(t * 2.5f) % 3;
            string head = connecting ? "PLAYER FOUND!" : failed ? "SEARCH FAILED" : searching ? "SEARCHING" + new string('.', dots) : "NOT SEARCHING";
            float blink = 0.5f + 0.5f * Mathf.Sin(t * 4f);
            Gui.Skew(new Rect(x, y + 10f, 14f, 30f), WithA(sc, searching || connecting ? 0.5f + 0.5f * blink : 1f), 0.3f);
            Gui.Display(new Rect(x + 26f, y, w - 26f, 50f), head, 40, connecting ? Gui.Green : failed ? Gui.P1 : Gui.Paper);
            y += 60f;

            // The animated search line and a sweeping bar.
            string line = connecting ? "Connecting to your opponent..." : searching ? "Searching for a player near you" + new string('.', dots)
                        : failed ? "The search stopped. Try again." : "Press SEARCH AGAIN to look for a player.";
            Gui.Text(new Rect(x, y, w, 26f), line, 16, Gui.Paper, TextAnchor.MiddleLeft);
            y += 32f;
            var bar = new Rect(x, y, w, 12f);
            Gui.Skew(bar, WithA(Gui.Steel, 0.8f), 0.6f);
            if (searching || connecting)
            {
                float seg = w * 0.22f;
                float px = connecting ? 0f : Mathf.Repeat(t * 0.55f, 1f) * (w + seg) - seg;
                float sx = Mathf.Max(bar.x, bar.x + px), ex = Mathf.Min(bar.xMax, bar.x + px + (connecting ? w : seg));
                if (ex > sx) Gui.Skew(new Rect(sx, bar.y, ex - sx, bar.height), sc, 0.6f);
            }
            y += 26f;

            string area = rooms.SearchArea;
            if (searching && area.Length > 0)
                Gui.Text(new Rect(x, y, w, 22f), "LOOKING IN:  " + area.ToUpperInvariant(), 13, Gui.Gold, TextAnchor.MiddleLeft);
            if (searching || connecting)
            {
                int secs = Mathf.Max(0, Mathf.FloorToInt(t - matchmakingSince));
                Gui.Text(new Rect(x, y, w, 22f), (secs / 60) + ":" + (secs % 60).ToString("00"), 13, Gui.Muted, TextAnchor.MiddleRight);
            }
            y += 32f;

            // Status: a failed handshake note, the room manager's status (red when it failed), or no internet.
            string status = onlineNote.Length > 0 ? onlineNote : rooms.Status;
            bool err = failed;
            if (!searching && !connecting && !net) { status = NoInternetMsg; err = true; }
            Gui.Text(new Rect(x, y, w, 64f), status, 14, err ? Gui.Red : Gui.Paper, TextAnchor.UpperLeft, false);
            y += 70f;
            // Privacy notice: what matchmaking shares.
            Gui.Text(new Rect(x, y, w, 36f), "Matchmaking shares your country (from device settings), never your location.", 11, Gui.Muted, TextAnchor.UpperLeft, false);

            var actionR = new Rect(x, r.yMax - 74f, w, 54f);
            if (searching || connecting)
            {
                if (Gui.Button(actionR, "CANCEL", Gui.Red, 20)) { UiSound.Back(); StopMatchmaking(); }
            }
            else
            {
                Gui.PreferNext();
                if (Gui.Button(actionR, "SEARCH AGAIN", Gui.P2, 20)) StartMatchmaking();
            }

            float by = a.yMax - 48f;
            if (Gui.Button(new Rect(a.xMax - 240f, by, 240f, 46f), "BACK", Gui.Steel, 18)) { UiSound.Back(); OpenRoomsPage(); }   // stops the search
            EndPage();
            NavHints("VS ONLINE  ·  MATCHMAKING");
        }

        // ---------------------------------------------------------------- ROOM (waiting for players)

        /// <summary>
        /// Inside a room (WAITING FOR PLAYERS): choose a fighter (left), press OK to be "in". When the room is full and
        /// both pressed OK the fight starts by itself (the host presses START FIGHT once, see <see cref="AutoStartWhenFull"/>);
        /// START FIGHT stays for a manual start. After the fight both come back here. Top right: the room code box with
        /// COPY. Bottom left (host): INVITE A FRIEND BY NAME.
        /// </summary>
        void RoomView()
        {
            var cur = roomsSvc.Current;
            Backdrop();
            NavBar();
            if (cur == null)
            {
                if (!roomsSvc.Working) { Enter(Page.Rooms); return; }   // join / create failed: the hub status shows why
                var w0 = BeginPage("01", "VS ONLINE", "SOLO 1V1  ·  OPENING THE ROOM...");
                Gui.Text(new Rect(w0.x, w0.y + w0.height * 0.35f, w0.width, 40f), roomsSvc.Status.Length > 0 ? roomsSvc.Status : "Opening the room...", 18, Gui.Paper);
                if (Gui.Button(new Rect(w0.xMax - 240f, w0.yMax - 48f, 240f, 46f), "BACK", Gui.Steel, 18)) { UiSound.Back(); Enter(Page.Rooms); }
                EndPage();
                return;
            }
            var a = BeginPage("01", "VS ONLINE", "SOLO 1V1  ·  ROOM  ·  " + cur.Name.ToUpperInvariant());
            string me = roomsSvc.MyId;
            bool host = roomsSvc.IsHost, ready = roomsSvc.LocalReady, launching = roomsSvc.Launching || pendingOnline != null;
            float ph = a.height - 62f;

            // Left: our fighter. Locked once OK is pressed (CHANGE unlocks).
            var left = new Rect(a.x, a.y, a.width * 0.5f - 10f, ph);
            var keepCls = pending.cls[0]; int keepPassive = pending.passive[0], keepWeapon = pending.weapon[0];
            FighterPicker(left, !ready && !launching);
            if (ready || launching) { pending.cls[0] = keepCls; pending.passive[0] = keepPassive; pending.weapon[0] = keepWeapon; }
            if (ready)
            {
                // LOCKED IN chip right after the YOUR FIGHTER title (the player name keeps the top-right corner).
                float tx = left.x + 20f + Gui.Measure("YOUR FIGHTER", 24, true) + 18f;
                var chip = new Rect(tx, left.y + 14f, Gui.Measure("LOCKED IN", 12) + 22f, 20f);
                Gui.Skew(chip, Gui.Green, 0.3f);
                Gui.Text(chip, "LOCKED IN", 12, Gui.Ink, TextAnchor.MiddleCenter, true, false);
            }
            roomsSvc.SetPick(pending.cls[0], pending.passive[0]);
            if (host) pending.stage = cur.Stage;   // the handshake uses the host's stage

            // Right: the room. Name on the left, the room code box (with COPY) in the top-right corner.
            var r = new Rect(left.xMax + 20f, a.y, a.width - left.width - 20f, ph);
            Gui.Box(r, Gui.Gold);
            float x = r.x + 24f, w = r.width - 48f, y = r.y + 12f;
            float codeW = RoomCodeBox(r.xMax - 24f, y, cur.Code);
            Gui.Display(new Rect(x, y, w - codeW - 12f, 36f), cur.Name, 30, Gui.Paper, TextAnchor.MiddleLeft, 0.15f);
            y += 44f;
            // WAITING FOR PLAYERS until every seat is taken, then ROOM FULL (the fight starts once both pressed OK).
            int joined = Mathf.Min(cur.Slots.Count, Netcode.ChibiRoomRules.Seats);
            bool full = joined >= Netcode.ChibiRoomRules.Seats;
            Gui.Text(new Rect(x, y, w, 18f), (full ? "ROOM FULL" : "WAITING FOR PLAYERS") + "  ·  " + joined + " / " + Netcode.ChibiRoomRules.Seats + " JOINED",
                     12, full ? Gui.Green : Gui.Gold, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(x, y, w, 18f), full ? "Both press OK: the fight starts by itself." : "Share the code, or invite a friend by name.", 12, Gui.Muted, TextAnchor.MiddleRight);
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
                else Gui.Text(new Rect(row.x + 14f, row.y, w - 28f, 62f), "Empty seat · waiting for a player to join", 15, Gui.Muted, TextAnchor.MiddleLeft);
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
            AutoStartWhenFull(cur, host, ready, launching);
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

            // Bottom left (host): INVITE A FRIEND BY NAME (the in-game name they chose at first launch).
            float by = a.yMax - 48f;
            InviteByNameRow(new Rect(a.x, by, a.xMax - 460f - a.x, 46f), cur, host, full, launching);

            // Bottom right: leave for good, or back to the Solo hub (we stay a member of the room).
            bool armed = leaveArmedId == cur.Id && Time.unscaledTime < leaveArmedUntil;
            if (Gui.Button(new Rect(a.xMax - 440f, by, 180f, 46f), armed ? "SURE?" : host ? "CLOSE ROOM" : "LEAVE ROOM", Gui.Red, 15, !launching))
            {
                if (armed)
                {
                    if (nameInvites != null) nameInvites.CancelOutgoing();   // the room is gone: our invite would lead nowhere
                    roomsSvc.Leave(cur.Id);
                    leaveArmedId = null;
                    Enter(Page.Rooms);
                }
                else { leaveArmedId = cur.Id; leaveArmedUntil = Time.unscaledTime + 3f; }
            }
            if (Gui.Button(new Rect(a.xMax - 240f, by, 240f, 46f), "BACK", Gui.Steel, 18, !launching))
            {
                UiSound.Back();
                roomsSvc.ExitToList();
                Enter(Page.Rooms);
            }
            EndPage();
            NavHints("VS ONLINE  ·  ROOM");
        }

        /// <summary>
        /// The room code box (top-right corner of the room panel, right-aligned at <paramref name="right"/>): "CODE" and
        /// the code, and COPY, which puts the code on the clipboard to paste into Discord or a chat. Returns its width.
        /// </summary>
        float RoomCodeBox(float right, float y, string code)
        {
            const float h = 36f, copyW = 64f;
            float cw = Gui.Measure(code, 20, true) + 14f;
            float bw = 12f + 40f + cw + copyW + 8f;
            var box = new Rect(right - bw, y, bw, h);
            Gui.Rect(box, Gui.PanelLight);
            Gui.Frame(box, WithA(Gui.Gold, 0.85f), 1f);
            Gui.Rect(new Rect(box.x, box.y, 4f, box.height), Gui.Gold);
            Gui.Text(new Rect(box.x + 12f, box.y, 40f, h), "CODE", 11, Gui.Muted, TextAnchor.MiddleLeft, true, false);
            Gui.Display(new Rect(box.x + 52f, box.y, cw, h), code, 20, Gui.Gold, TextAnchor.MiddleLeft, 0.12f);
            if (Gui.Button(new Rect(box.xMax - copyW - 4f, box.y + 4f, copyW, h - 8f), "COPY", Gui.Gold, 12, code.Length > 0))
            {
                GUIUtility.systemCopyBuffer = code;
                Toast("Room code " + code + " copied. Paste it to your friend.");
            }
            return bw;
        }

        /// <summary>
        /// INVITE A FRIEND BY NAME (host only): a name field and INVITE (Enter sends too), then the invite service's
        /// status. Disabled while the room is full, a fight is starting, or invites are not available.
        /// </summary>
        void InviteByNameRow(Rect r, Netcode.ChibiRoomSnapshot cur, bool host, bool full, bool launching)
        {
            if (!host)
            {
                Gui.Text(new Rect(r.x, r.y, r.width, r.height), "The host can invite a friend by name.", 12, Gui.Muted, TextAnchor.MiddleLeft, false);
                return;
            }
            bool can = nameInvites != null && nameInvites.Enabled && !nameInvites.Sending && !full && !launching;
            var field = new Rect(r.x, r.y + 3f, 250f, 40f);
            GUI.SetNextControlName("InviteName");
            if (can) inviteNameInput = GUI.TextField(field, inviteNameInput ?? "", Netcode.ChibiSocialRules.DisplayMax, Field(17));
            else GUI.Label(field, inviteNameInput ?? "", Field(17));
            bool focused = GUI.GetNameOfFocusedControl() == "InviteName";
            if (string.IsNullOrEmpty(inviteNameInput) && !focused)
                Gui.Text(field, "INVITE A FRIEND BY NAME", 12, WithA(Gui.Muted, can ? 0.9f : 0.5f), TextAnchor.MiddleCenter, true, false);
            string name = (inviteNameInput ?? "").Trim();
            bool send = Gui.Button(new Rect(field.xMax + 8f, r.y + 3f, 120f, 40f), "INVITE", Gui.P2, 15, can && name.Length > 0);
            if (can && focused && name.Length > 0 && EnterPressed()) { send = true; Event.current.Use(); }
            if (send && can && name.Length > 0)
            {
                nameInvites.Send(name, cur.Code, cur.Name);
                GUIUtility.keyboardControl = 0;
            }
            string status = full ? "The room is full." : nameInvites != null ? nameInvites.Status : "";
            bool err = !full && nameInvites != null && nameInvites.StatusIsError;
            float sx = field.xMax + 138f;
            Gui.Text(new Rect(sx, r.y, Mathf.Max(0f, r.xMax - sx), r.height), status, 12, err ? Gui.Red : Gui.Muted, TextAnchor.MiddleLeft, false);
        }

        // ---------------------------------------------------------------- ROOM INVITE pop-up (invites by name to us)

        /// <summary>
        /// Online menu pages where room invites are received (never a match or another menu). Not the PLAY page: it is
        /// also the Vs Local / Vs CPU menu, so opening it must not sign in or poll Lobby (SEC-007). Not the MATCHMAKING
        /// page either: its search queries Lobby too, and both share one 1-per-second Query limit (network review
        /// 2026-11-22), so polling there would make the search hit "too many requests" and back off.
        /// </summary>
        bool OnInviteMenuPage =>
            (page == Page.PlayStyle && playStyleOnline) || page == Page.Rooms || page == Page.Room;

        /// <summary>ChibiNameInvites.Enabled: online allowed (13+), on an online menu page, no online match starting.</summary>
        bool NameInvitesWanted => PlayerIdentity.OnlineAllowed(Save.ageBand) && OnInviteMenuPage && pendingOnline == null && wipeStart < 0f;

        /// <summary>
        /// The pop-up is drawn this frame: an invite is waiting, no window covers the page, and we are free to answer it
        /// (SEC-003): not seated in a room (Room page), no room join / start in progress, no online match starting.
        /// Invites stay pending meanwhile and show once we are back on the hub.
        /// </summary>
        bool NameInviteShown => nameInvites != null && nameInvites.Pending.Count > 0 && NameInvitesWanted && !DialogOpen &&
                                !(page == Page.Room && roomsSvc.Current != null) && !roomsSvc.Working && !roomsSvc.Launching &&
                                !OnlineMatchStarting;

        /// <summary>A matchmaking or room link is connecting, or a handshake runs: an online match is about to start.</summary>
        bool OnlineMatchStarting => pendingOnline != null || rooms.State == Netcode.ChibiRoomState.Connecting ||
                                    rooms.State == Netcode.ChibiRoomState.Connected;

        Rect InvitePopupRect => new Rect(W * 0.5f - 290f, 120f, 580f, 170f);

        /// <summary>
        /// ROOM INVITE pop-up (style of the old match invite): who invites us to which room, the seconds left, ACCEPT
        /// (join their room by its code and open it) and DECLINE.
        /// </summary>
        void NameInvitePopup()
        {
            var inv = nameInvites.Pending[0];
            var r = InvitePopupRect;
            Gui.Glow(new Rect(r.x - 30f, r.y - 30f, r.width + 60f, r.height + 60f), new Color(1f, 0.8f, 0.2f, 0.35f));
            Gui.Box(r, Gui.Gold);
            Gui.Display(new Rect(r.x, r.y + 10f, r.width, 40f), "ROOM INVITE", 32, Gui.Gold, TextAnchor.MiddleCenter);
            int left = Mathf.Max(0, Mathf.CeilToInt(inv.ExpiresAt - Time.unscaledTime));
            string from = Netcode.ChibiSocialRules.SafeDisplayName(inv.FromName);     // names come from the network:
            string room = Netcode.ChibiSocialRules.SafeRoomName(inv.RoomName);        // drawn only through the sanitisers
            Gui.Text(new Rect(r.x + 20f, r.y + 50f, r.width - 40f, 26f), from + " invites you to " + room + "   (" + left + "s)", 16, Gui.Paper);
            // SEC-005: names are self-reported and not unique.
            Gui.Text(new Rect(r.x + 20f, r.y + 76f, r.width - 40f, 18f), "Names are not checked: only accept invites from people you know.", 11, Gui.Muted, TextAnchor.MiddleCenter, false, false);
            bool canAccept = !roomsSvc.Working && !roomsSvc.Launching && pendingOnline == null;
            Gui.PreferNext();
            if (Gui.Button(new Rect(r.x + 70f, r.y + 102f, 200f, 46f), canAccept ? "ACCEPT" : "ONE MOMENT...", Gui.Green, 17, canAccept) && canAccept)
            {
                string code = nameInvites.Accept(0);
                if (!string.IsNullOrEmpty(code))
                {
                    pending.cpu[0] = pending.cpu[1] = false;
                    pending.training = false;
                    onlineNote = "";
                    social.ClearMatchFriend();
                    roomsSvc.JoinByCode(code);
                    Enter(Page.Room);   // leaving MATCHMAKING this way stops its search (Enter)
                }
            }
            if (Gui.Button(new Rect(r.xMax - 270f, r.y + 102f, 200f, 46f), "DECLINE", Gui.Red, 17)) nameInvites.Decline(0);
        }

        /// <summary>
        /// "Start when full": once the lobby is full and both players pressed OK, the host runs the START FIGHT action once
        /// by itself. Only the host presses (its press launches directly; a guest press would only ask the host, one more
        /// Lobby write). It fires once per ready state: it re-arms only after the state drops (CHANGE, a failed start, or
        /// the fight ending, all of which clear OK), so a failed start never repeats by itself.
        /// </summary>
        void AutoStartWhenFull(Netcode.ChibiRoomSnapshot cur, bool host, bool ready, bool launching)
        {
            bool fullAndReady = ready && Netcode.ChibiRoomRules.CanStart(cur);
            if (!fullAndReady) { autoStartedFor = null; return; }
            if (autoStartedFor == cur.Id || !host || launching || roomsSvc.Working || !roomsSvc.Synced || wipeStart >= 0f) return;
            autoStartedFor = cur.Id;
            onlineNote = "";
            social.ClearMatchFriend();
            roomsSvc.PressStart();
        }

        // ---------------------------------------------------------------- shared stage grid (create room dialog / room)

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

        // ---------------------------------------------------------------- compact fighter picker (room / matchmaking)

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
