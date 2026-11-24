using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Onboarding (age, player name with SUGGEST, optional Google account), the MATCHMAKING pages (Page.Rooms: the room
    /// blocks with CREATE ROOMS / JOIN ROOM pop-ups; Page.Room: 2-4 seats, room code box with COPY, invite a friend by
    /// name, start when exactly 2 are seated and ready) and the Vs Online QUICK MATCH page (Page.QuickMatch: searching
    /// for a player near you). Rooms are ChibiRoomService rooms, quick match is NetworkRoomManager.QuickMatch and name
    /// invites are ChibiNameInvites; online services are only called, never re-implemented here.
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
        bool roomDialog;          // CREATE ROOMS pop-up: ENTER THE ROOM NAME (MATCHMAKING rooms screen)
        bool joinDialog;          // JOIN ROOM pop-up: ENTER THE ROOM CODE (MATCHMAKING rooms screen)
        /// <summary>A CREATE ROOMS request is running: once it ends, the new room shows as a block (FinishRoomCreate).</summary>
        bool roomCreatePending;
        /// <summary>The room just created from the pop-up, and until when its block glows.</summary>
        string newRoomId = "";
        float newRoomUntil;
        string joinError = "";    // JOIN ROOM window: a code that is not a valid room code
        // Invite a friend by name into our room (host, Room page), and room invites to us (pop-up on online menu pages).
        Netcode.ChibiNameInvites nameInvites;
        string inviteNameInput = "";
        /// <summary>When the current QUICK MATCH search was started (unscaled time), for the elapsed clock.</summary>
        float quickMatchSince;
        Netcode.ChibiAccount account;   // Unity Player Accounts (Google); no UI opens it since 2026-11-23 (user request)
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
        /// CREATE ROOMS pop-up (MATCHMAKING rooms screen): "ENTER THE ROOM NAME", then CREATE. The room is created at once
        /// and appears as a new block on the rooms screen (<see cref="FinishRoomCreate"/>); the host picks the stage inside
        /// the room. Validation: ChibiSocialRules.ValidateRoomName and the 13+ age gate.
        /// </summary>
        void RoomDialog()
        {
            var r = DialogRect(640f, 300f);
            Gui.Box(r, Gui.P1);
            Gui.Display(new Rect(r.x, r.y + 14f, r.width, 44f), "ENTER THE ROOM NAME", 36, Gui.Paper, TextAnchor.MiddleCenter);
            Gui.Text(new Rect(r.x + 30f, r.y + 60f, r.width - 60f, 22f),
                     "3-24 letters, numbers or spaces. Friends see it when they join. Rooms hold 2 to 4 players.",
                     13, Gui.Muted, TextAnchor.UpperCenter, false);

            bool enter = EnterPressed();
            var field = new Rect(r.x + 60f, r.y + 96f, r.width - 120f, 52f);
            GUI.SetNextControlName("RoomName");
            roomNameInput = GUI.TextField(field, roomNameInput ?? "", Netcode.ChibiSocialRules.RoomNameMax, Field(24));
            if (GUI.GetNameOfFocusedControl() != "RoomName") GUI.FocusControl("RoomName");
            Gui.Text(new Rect(r.x + 30f, r.y + 156f, r.width - 60f, 40f), roomError, 14, Gui.Red, TextAnchor.UpperCenter, false);

            // While the room service is busy (the room list refresh) a create would be ignored: wait for it.
            bool canCreate = !roomsSvc.Working;
            Gui.PreferNext();
            if ((Gui.Button(new Rect(r.x + r.width * 0.5f - 230f, r.y + 212f, 220f, 56f), canCreate ? "CREATE" : "ONE MOMENT...", Gui.P1, 20, canCreate) || enter) && canCreate)
            {
                string n = (roomNameInput ?? "").Trim();
                string err = Netcode.ChibiSocialRules.ValidateRoomName(n);
                if (err != null) { roomError = err; UiSound.Error(); }
                else if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { roomError = OnlineLockedMsg; UiSound.Error(); }
                else
                {
                    roomDialog = false;
                    roomNameInput = null;   // the next room starts from the default name again
                    pending.cpu[0] = pending.cpu[1] = false;
                    pending.training = false;
                    onlineNote = "";
                    social.ClearMatchFriend();
                    // ST-5: the room carries the host's hazards pick (key "hz") next to the stage; both change in the room.
                    roomsSvc.Create(n, pending.stage, pending.hazards);
                    roomCreatePending = true;   // stays on the rooms screen: the new room shows as a block
                    UiSound.Confirm();
                }
                if (enter) Event.current.Use();
            }
            if (Gui.Button(new Rect(r.x + r.width * 0.5f + 10f, r.y + 212f, 220f, 56f), "CANCEL", Gui.Steel, 18))
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
            if (nameInput.Length == 0) SuggestName();
            nameError = "";
        }

        /// <summary>Random source for name suggestions (seeded once per session; the generator itself is deterministic).</summary>
        readonly System.Random nameRandom = new System.Random(System.Environment.TickCount);

        /// <summary>SUGGEST (and gamepad Y): a random fun fighter name that passes the name rules, never the one shown now.</summary>
        void SuggestName()
        {
            nameInput = Netcode.ChibiNameSuggestions.Suggest(nameRandom.Next, nameInput);
            nameError = "";
        }

        /// <summary>Worldwide-unique names (Cloud Code script "Chibi_Clash" = Assets/ChibiClash/CloudCode/Chibi_Clash.js, + Cloud Save): claim, check, launch count.</summary>
        readonly Netcode.ChibiNameRegistry nameRegistry = new Netcode.ChibiNameRegistry();
        /// <summary>A SAVE in the name window is waiting for the server's claim answer (CHECKING...).</summary>
        bool nameClaiming;
        /// <summary>The running online-name claim, shared by concurrent online flows (null when none).</summary>
        System.Threading.Tasks.Task<string> onlineNameTask;
        /// <summary>The name <see cref="onlineNameTask"/> claims: a different saved name starts a new claim.</summary>
        string onlineNameTaskFor = "";

        /// <summary>Push a name the server confirmed as ours to Unity's public player name (friends) or the account.</summary>
        void PushClaimedName(string n)
        {
            if (social.Ready && !string.Equals(Netcode.ChibiSocialRules.BaseName(social.MyName), n, System.StringComparison.Ordinal))
                social.SetName(n);
            else if (!social.Ready && account.SignedIn) account.PushName(n);   // the account keeps the name everywhere
        }

        /// <summary>
        /// SAVE / LET'S FIGHT in the name window: the name passed ChibiSocialRules.ValidateName; now claim it worldwide.
        /// Taken: "This name is already taken", the window stays open. Offline, under 13 or service down: the name is
        /// saved locally as unverified and claimed before the first online flow (<see cref="EnsureOnlineNameAsync"/>).
        /// The claim always ends (ChibiNameRegistry's deadline covers sign-in too), so the window never stays stuck.
        /// </summary>
        async void ClaimAndSaveName(string n)
        {
            bool canClaim = PlayerIdentity.OnlineAllowed(Save.ageBand) && InternetUp &&
                            !Netcode.ChibiNameRegistryRules.MaySkipClaim(Application.isEditor, Debug.isDebugBuild, n);
            if (!canClaim) { CommitName(n, verified: false); return; }
            nameClaiming = true;
            nameError = "";
            Netcode.ChibiNameReply r;
            try { r = await nameRegistry.ClaimAsync(n); }
            catch (System.Exception) { r = Netcode.ChibiNameReply.Unavailable; }   // never thrown today; belt and braces
            finally { nameClaiming = false; }
            if (this == null) return;   // left Play Mode while waiting
            string err = Netcode.ChibiNameRegistryRules.DialogError(r.Status);
            if (err != null) { nameError = err; UiSound.Error(); return; }
            CommitName(n, verified: r.Status != Netcode.ChibiNameStatus.ServiceUnavailable);
        }

        /// <summary>Save the chosen name (the old SAVE body) and push it to Unity.</summary>
        void CommitName(string n, bool verified)
        {
            bool changed = n != Save.playerName;
            Save.playerName = n;
            RecordNewPlayer();   // age given + identity name created: this player's row in the age record
            Save.Save();
            nameDialog = false;
            // Unity's public name / the account name only change for a name claimed worldwide (unclaimed: pushed by
            // PushClaimedName once the online name gate confirms it).
            if (changed && verified) PushClaimedName(n);
            Toast(verified ? "You are " + n + "!" : "You are " + n + "! We will check the name when you play online.");
        }

        /// <summary>
        /// The name gate of every online flow. Wired into NetworkRoomManager.NameGate (quick match, private rooms, room
        /// links: checked in its sign-in step) and ChibiRoomService.NameGate (create / join a room), so no screen, deep
        /// link or invite path can go online with an unclaimed name. Returns null when our saved name is ours worldwide
        /// (claimed once per session), else the reason to show. Taken: the name window also opens with "This name is
        /// already taken". Service down: refused in release builds; editor / development builds go on (dev automation).
        /// </summary>
        System.Threading.Tasks.Task<string> EnsureOnlineNameAsync()
        {
            string n = Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);
            if (n.Length > 0 && (nameRegistry.IsVerified(n) ||
                                 Netcode.ChibiNameRegistryRules.MaySkipClaim(Application.isEditor, Debug.isDebugBuild, n)))
                return System.Threading.Tasks.Task.FromResult<string>(null);
            if (onlineNameTask == null || onlineNameTask.IsCompleted || onlineNameTaskFor != n)
            {
                onlineNameTaskFor = n;
                onlineNameTask = ClaimForOnline(n);
            }
            return onlineNameTask;
        }

        async System.Threading.Tasks.Task<string> ClaimForOnline(string n)
        {
            if (n.Length == 0) { OpenNameDialog(); return "Choose your player name first."; }
            Netcode.ChibiNameReply r;
            try { r = await nameRegistry.ClaimAsync(n); }
            catch (System.Exception) { r = Netcode.ChibiNameReply.Unavailable; }
            if (this == null) return "Cancelled.";
            // SEC-003: the saved name changed while we waited: this answer is about the old name, check the new one.
            if (Netcode.ChibiSocialRules.CleanSavedName(Save.playerName) != n) return await EnsureOnlineNameAsync();
            if (Netcode.ChibiNameRegistryRules.IsOwned(r.Status)) { PushClaimedName(n); return null; }
            if (r.Status == Netcode.ChibiNameStatus.ServiceUnavailable)
                return Netcode.ChibiNameRegistryRules.MaySkipClaim(Application.isEditor, Debug.isDebugBuild, n, r.Status)
                    ? null : Netcode.ChibiNameRegistryRules.UnavailableMessage;
            string err = Netcode.ChibiNameRegistryRules.DialogError(r.Status) ?? Netcode.ChibiNameRegistryRules.TakenMessage;
            if (this != null)
            {
                // Taken (or refused): choose another name. Never reopen a window that is already open: the invite poll
                // retries every 30 s and must not wipe what the player is typing (review 2026-11-23).
                bool wasOpen = nameDialog;
                if (!wasOpen) OpenNameDialog();
                nameError = err;
                if (!wasOpen) UiSound.Error();
            }
            return err;
        }

        /// <summary>
        /// Choose or change the player name (validated by ChibiSocialRules.ValidateName, then a worldwide claim via ClaimAndSaveName; saved;
        /// pushed to Unity). The box opens by itself only on first launch; later it opens from CHANGE NAME (SETTINGS) or
        /// the profile chip. SUGGEST fills a random valid name.
        /// </summary>
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
            var field = new Rect(r.x + 60f, r.y + 112f, r.width - 120f - 140f, 50f);
            GUI.SetNextControlName("PlayerName");
            nameInput = GUI.TextField(field, nameInput ?? "", Netcode.ChibiSocialRules.NameMax, Field(26));
            if (GUI.GetNameOfFocusedControl() != "PlayerName") GUI.FocusControl("PlayerName");
            if (Gui.Button(new Rect(field.xMax + 10f, field.y + 3f, 130f, 44f), "SUGGEST", Gui.Gold, 16)) { SuggestName(); UiSound.Step(); }
            if (nameClaiming) enter = false;   // one claim at a time
            Gui.Text(new Rect(r.x + 30f, r.y + 168f, r.width - 60f, 24f), nameError, 14, Gui.Red);

            float bw = first ? 260f : 200f;
            float bx = first ? r.x + (r.width - bw) * 0.5f : r.x + r.width * 0.5f - bw - 10f;
            string saveLabel = nameClaiming ? "CHECKING..." : first ? "LET'S FIGHT!" : "SAVE";
            if (Gui.Button(new Rect(bx, r.y + 210f, bw, 54f), saveLabel, Gui.P1, 20, !nameClaiming) || enter)
            {
                string n = (nameInput ?? "").Trim();
                string err = Netcode.ChibiSocialRules.ValidateName(n);
                if (err != null) { nameError = err; UiSound.Error(); }
                else if (!nameClaiming) ClaimAndSaveName(n);   // worldwide-unique claim, then save (or "already taken")
                if (enter && Event.current.type == EventType.KeyDown) Event.current.Use();
            }
            Gui.Text(new Rect(r.x, r.yMax - 30f, r.width, 22f), first ? "Gamepad: A = let's fight · Y = suggest a name" : "Gamepad: A = save · Y = suggest a name", 12, Gui.Muted);
            // No SIGN IN WITH GOOGLE row (user request 2026-11-23): the name stays on this device, tied to its
            // anonymous player id. ChibiAccount remains in the code but nothing in the UI opens it.
            if (!first && Gui.Button(new Rect(r.x + r.width * 0.5f + 10f, r.y + 210f, 200f, 54f), "CANCEL", Gui.Steel, 18, !nameClaiming))
            {
                nameDialog = false;
                UiSound.Back();
            }
        }

        // ---------------------------------------------------------------- online match start

        /// <summary>The room manager connected us: start the online match (handshake first; the view appears once agreed).</summary>
        void OnRoomConnected(Netcode.ChibiUtpTransport transport, bool isHost) => StartOnline(transport, isHost, null, 2);

        /// <summary>
        /// The room manager connected a star of 2-4 players (protocol 6): the host gets one link per guest
        /// (<paramref name="guests"/>[slot]); a guest gets <c>OnRoomConnected</c> as before and learns its seat from Start.
        /// </summary>
        void OnRoomGroupConnected(Netcode.ChibiUtpTransport transport, Netcode.IChibiTransport[] guests, int players)
            => StartOnline(transport, true, guests, players);

        void StartOnline(Netcode.ChibiUtpTransport transport, bool isHost, Netcode.IChibiTransport[] guests, int players)
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
            uint seed = (uint)System.Environment.TickCount ^ 0xC1B1u;
            // A guest of a group may wait while the host fills seats (quick match: up to QuickFillSeconds), so it waits longer.
            int joinTimeout = Netcode.ChibiHandshake.DefaultTimeoutTicks + (int)(Netcode.NetworkRoomManager.QuickFillSeconds + 15f) * 60;
            int roomSeats = roomsSvc.OwnsLink && roomsSvc.Current != null ? roomsSvc.Current.Slots.Count : -1;   // SEC-001
            // SEC-003 / L2: in a room every Relay connection must prove its seat (secret in Hello, key in the lobby).
            string[] seatKeys = roomsSvc.OwnsLink ? roomsSvc.SeatKeys() : null;
            ulong seatSecret = roomsSvc.OwnsLink ? roomsSvc.MySecret : 0UL;
            var online = guests != null
                ? Netcode.ChibiOnlineMatch.HostGroup(guests, players, input, pending.cls[0], pending.passive[0], pending.stage, seed,
                                                     Netcode.ChibiOnlineMatch.SettingsFor(players), Netcode.ChibiMatchMode.FreeForAll, 0,
                                                     Netcode.ChibiHandshake.DefaultTimeoutTicks, me, !pending.hazards,
                                                     transport, transport, seatKeys)
                : isHost
                ? Netcode.ChibiOnlineMatch.Host(transport, input, pending.cls[0], pending.passive[0], pending.stage,
                                                seed, settings, Netcode.ChibiHandshake.DefaultTimeoutTicks, me, !pending.hazards,
                                                seatKeys != null ? seatKeys[1] : null)
                : Netcode.ChibiOnlineMatch.Join(transport, input, pending.cls[0], pending.passive[0], joinTimeout, me,
                                                roomsSvc.OwnsLink ? pending.stage : -1,                  // SEC-001: the room's
                                                roomsSvc.OwnsLink ? !pending.hazards : (bool?)null,      // agreed settings
                                                roomSeats, seatSecret);
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
            // A 1v1 room fight is seat 0 (host) against seat 1; 3-4 seats load the others on demand (hint only).
            if (room.Slots.Count != 2) return "";
            string me = roomsSvc.MyId;
            var other = room.Slots[0].PlayerId == me ? room.Slots[1] : room.Slots[1].PlayerId == me ? room.Slots[0] : default;
            return string.IsNullOrEmpty(other.PlayerId) ? "" : CharacterRoster.Of(other.Class).Id;
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
                m.LocalIndex == 0 && m.MatchHash != 0)
            {
                onlinePeerSeen = true;
                var cfg = m.Config;
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
                var agreed = m.Config;
                ChibiMatch.ReleaseUnusedSheets(agreed.Class0, agreed.Class1);   // a wrong room guess is dropped here
                onlineLoad = new MatchAssetLoader(agreed.Class0, agreed.Class1);   // the rest: READY intro slices
                match = ChibiMatch.CreateOnline(this, pendingOnline, pendingOnlineInput, pending.weapon[0]);
                // SEC-004: in a room the names come from the lobby (each player's own claimed name), not from the handshake.
                if (roomsSvc.OwnsLink) match.SetRoomNames(roomsSvc.SeatNames());
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
                onlineNote = r.RejectReason == Netcode.ChibiRejectReason.InvalidConfig && roomsSvc.OwnsLink && !m.RejectedByPeer
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

        // ---------------------------------------------------------------- MATCHMAKING: the rooms screen (ChibiRoomService rooms)

        /// <summary>Seat colour for seats 1-4 (P1 red, P2 blue, then gold and green).</summary>
        static Color SeatColor(int i) => i == 0 ? Gui.P1 : i == 1 ? Gui.P2 : i == 2 ? Gui.Gold : Gui.Green;

        /// <summary>Opens the MATCHMAKING rooms screen (PLAY > MATCHMAKING) and reloads the rooms we are in.</summary>
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
        /// MATCHMAKING rooms screen (rooms of 2-4 players). Top right: CREATE ROOMS (pop-up "Enter the room name"; the room
        /// is created and appears here as a block) and JOIN ROOM (pop-up "Enter the room code"). Middle: one block per room
        /// we are in (click it for its code with COPY and INVITE BY NAME), or "No room create yet!" when there is none.
        /// Bottom: the room service status (errors in red), REFRESH and BACK. 13+ and an internet connection.
        /// </summary>
        void RoomsView()
        {
            bool onlineOk = PlayerIdentity.OnlineAllowed(Save.ageBand);
            if (!onlineOk) { Enter(Page.Play); return; }   // age gate (13+), defence in depth
            FinishRoomCreate();
            Backdrop();
            NavBar();
            bool net = InternetUp;
            bool working = roomsSvc.Working;
            var a = BeginPage("02", "MATCHMAKING", "ROOMS  ·  2-4 PLAYERS  ·  CREATE · JOIN · INVITE BY NAME");
            int count = roomsSvc.MyRooms.Count;

            // Top row: YOUR ROOMS on the left, CREATE ROOMS and JOIN ROOM in the top-right corner.
            Gui.Display(new Rect(a.x, a.y + 4f, 400f, 40f), "YOUR ROOMS", 30, Gui.Gold, TextAnchor.MiddleLeft, 0.18f);
            if (count > 0)
                Gui.Text(new Rect(a.x + Gui.Measure("YOUR ROOMS", 30, true) + 24f, a.y + 4f, 300f, 40f), count + "  ·  click a room to open it",
                         13, Gui.Muted, TextAnchor.MiddleLeft, true, false);
            const float bw = 200f, bh = 48f;
            var joinR = new Rect(a.xMax - bw, a.y, bw, bh);
            var createR = new Rect(joinR.x - bw - 12f, a.y, bw, bh);
            if (count == 0) Gui.PreferNext();
            if (Gui.Button(createR, "CREATE ROOMS", Gui.P1, 18))
            {
                if (!net) Deny(NoInternetMsg);
                else OpenRoomDialog();
            }
            if (Gui.Button(joinR, "JOIN ROOM", Gui.P2, 18))
            {
                if (!net) Deny(NoInternetMsg);
                else OpenJoinDialog();
            }

            // Middle: the room blocks, or the empty / loading / offline message.
            var area = new Rect(a.x, a.y + bh + 18f, a.width, a.height - bh - 18f - 72f);
            if (count == 0)
            {
                bool loading = (working || roomCreatePending) && !roomsSvc.ListLoaded;
                string head = !net ? "NO INTERNET" : roomCreatePending ? "CREATING YOUR ROOM..." : loading ? "LOADING YOUR ROOMS..." : "No room create yet!";
                string sub = !net ? NoInternetMsg
                           : roomCreatePending || loading ? "One moment."
                           : "Press CREATE ROOMS to make one, or JOIN ROOM with a friend's room code.";
                float cy = area.y + area.height * 0.5f;
                Gui.Glow(new Rect(area.center.x - 260f, cy - 90f, 520f, 160f), WithA(Gui.P2, 0.12f));
                Gui.Display(new Rect(area.x, cy - 54f, area.width, 64f), head, 50, Gui.Paper, TextAnchor.MiddleCenter, 0.15f);
                Gui.Text(new Rect(area.x, cy + 14f, area.width, 26f), sub, 15, Gui.Muted, TextAnchor.MiddleCenter, false);
                Gui.Text(new Rect(area.x, cy + 42f, area.width, 22f), "Rooms hold 2 to 4 players. Invite friends by their player name.", 12,
                         WithA(Gui.Muted, 0.8f), TextAnchor.MiddleCenter, false, false);
            }
            else RoomBlocks(area);

            // Bottom: status, REFRESH and BACK.
            string status = roomsSvc.Status;
            bool statusErr = roomsSvc.StatusIsError;
            if (status.Length == 0 && !net) { status = NoInternetMsg; statusErr = true; }
            float by = a.yMax - 52f;
            if (nameInvites != null && nameInvites.Paused)
                Gui.Text(new Rect(a.x, by - 20f, 600f, 18f), "Room invites paused for a few minutes", 11, Gui.Muted, TextAnchor.MiddleLeft, false, false);
            Gui.Text(new Rect(a.x, by, a.width - 420f, 50f), status, 13, statusErr ? Gui.Red : Gui.Paper, TextAnchor.MiddleLeft, false);
            if (Gui.Button(new Rect(a.xMax - 400f, by + 2f, 150f, 46f), "REFRESH", Gui.P2, 15, !working && net)) roomsSvc.Refresh(force: true);
            if (Gui.Button(new Rect(a.xMax - 240f, by + 2f, 240f, 46f), "BACK", Gui.Steel, 18)) { UiSound.Back(); Go(Page.Play); }
            EndPage();
            NavHints("MATCHMAKING  ·  ROOMS");
        }

        /// <summary>
        /// One block per room we are in, in a grid: accent bar (gold = we host), room name, code, the 4 seats as pips (filled
        /// = taken) and "2 / 4 PLAYERS". A just-created room glows for a moment. Click opens the room.
        /// </summary>
        void RoomBlocks(Rect area)
        {
            const float gap = 18f, bh = 128f;
            int cols = Mathf.Clamp(Mathf.FloorToInt((area.width + gap) / (270f + gap)), 2, 5);
            float bw = (area.width - gap * (cols - 1)) / cols;
            int rows = Mathf.Max(1, Mathf.FloorToInt((area.height + gap) / (bh + gap)));
            int count = roomsSvc.MyRooms.Count, shown = Mathf.Min(count, cols * rows);
            if (shown < count) shown = Mathf.Max(0, shown - 1);   // the last cell says "+ N more"
            string me = roomsSvc.MyId;
            float t = Time.unscaledTime;
            var baseFill = new Color(0.07f, 0.11f, 0.2f, 0.97f);
            for (int k = 0; k < shown; k++)
            {
                var room = roomsSvc.MyRooms[k];
                float ap = Gui.EaseOut((Since - 0.08f - k * 0.04f) / 0.3f);
                var r = new Rect(area.x + (k % cols) * (bw + gap), area.y + (k / cols) * (bh + gap) + (1f - ap) * 16f, bw, bh);
                bool host = room.HostId.Length > 0 && room.HostId == me;
                var accent = host ? Gui.Gold : Gui.P2;
                if (k == 0) Gui.PreferNext();
                bool click = Gui.Hit(r, out bool hot);
                bool fresh = room.Id == newRoomId && t < newRoomUntil;
                Gui.Alpha = ap;
                if (fresh) Gui.Glow(new Rect(r.x - 24f, r.y - 24f, r.width + 48f, r.height + 48f), WithA(Gui.Gold, 0.3f + 0.15f * Mathf.Sin(t * 6f)));
                Gui.Skew(new Rect(r.x + 5f, r.y + 5f, r.width, r.height), new Color(0f, 0f, 0f, 0.4f), 0.06f);
                Gui.Skew(r, hot ? Color.Lerp(baseFill, accent, 0.18f) : baseFill, 0.06f);
                Gui.Skew(new Rect(r.x, r.y, r.width, 5f), accent, 0.06f);
                Gui.HalftoneFade(new Rect(r.x + 10f, r.y + 8f, r.width - 20f, r.height * 0.5f), WithA(accent, hot ? 0.22f : 0.1f), true);
                float x = r.x + 20f, w = r.width - 40f;
                Gui.Display(new Rect(x, r.y + 14f, w, 34f), room.Name, 26, Gui.Paper, TextAnchor.MiddleLeft, 0.15f);
                Gui.Text(new Rect(x, r.y + 50f, w, 20f), "CODE  " + room.Code, 13, Gui.Gold, TextAnchor.MiddleLeft, true, false);
                if (host || fresh)
                {
                    string tag = fresh ? "NEW" : "YOU HOST";
                    float tw = Gui.Measure(tag, 11) + 18f;
                    var chip = new Rect(r.xMax - 20f - tw, r.y + 52f, tw, 18f);
                    Gui.Skew(chip, fresh ? Gui.Green : Gui.Gold, 0.3f);
                    Gui.Text(chip, tag, 11, Gui.Ink, TextAnchor.MiddleCenter, true, false);
                }
                int taken = Mathf.Min(room.Slots.Count, Netcode.ChibiRoomRules.MaxSeats);
                for (int s = 0; s < Netcode.ChibiRoomRules.MaxSeats; s++)
                    Gui.Skew(new Rect(x + s * 26f, r.y + 82f, 20f, 14f), s < taken ? SeatColor(s) : WithA(Gui.Steel, 0.9f), 0.3f);
                Gui.Text(new Rect(x + Netcode.ChibiRoomRules.MaxSeats * 26f + 8f, r.y + 78f, w, 22f),
                         taken + " / " + Netcode.ChibiRoomRules.MaxSeats + " PLAYERS", 12, Gui.Muted, TextAnchor.MiddleLeft, true, false);
                if (hot)
                {
                    Gui.Text(new Rect(x, r.yMax - 28f, w - 12f, 22f), "OPEN", 12, Gui.Paper, TextAnchor.MiddleRight, true, false);
                    Gui.Chevron(new Rect(r.xMax - 26f, r.yMax - 23f, 8f, 12f), Gui.Paper);
                    Gui.SkewFrame(r, Gui.Paper, 2f, 0.06f);
                }
                Gui.Alpha = 1f;
                if (click)
                {
                    roomsSvc.Enter(room.Id);
                    Enter(Page.Room);
                }
            }
            if (shown < count)
            {
                var r = new Rect(area.x + (shown % cols) * (bw + gap), area.y + (shown / cols) * (bh + gap), bw, bh);
                Gui.Text(r, "+ " + (count - shown) + " more rooms", 16, Gui.Muted, TextAnchor.MiddleCenter);
            }
        }

        /// <summary>
        /// A room made from the CREATE ROOMS pop-up opens in the service; once it exists we go back to the list (the room
        /// shows as a new block that glows) instead of entering it. A failed create leaves its reason in the status line.
        /// </summary>
        void FinishRoomCreate()
        {
            if (!roomCreatePending || roomsSvc.Working) return;
            roomCreatePending = false;
            var made = roomsSvc.Current;
            if (made == null) return;
            newRoomId = made.Id;
            newRoomUntil = Time.unscaledTime + 4f;
            roomsSvc.ExitToList();
            Toast("Room \"" + made.Name + "\" created. Click it to share the code or invite friends.");
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
        /// JOIN ROOM pop-up (MATCHMAKING rooms screen): type or paste (Ctrl+V, or PASTE) the code a friend sent, then JOIN puts us straight into
        /// their room (the same path as before: JoinByCode, then the room page). A code that cannot be a room code is
        /// refused here; a failed join (no such room, full) shows in red on the rooms screen, and here when reopened.
        /// </summary>
        void JoinDialog()
        {
            var r = DialogRect(600f, 320f);
            Gui.Box(r, Gui.P2);
            Gui.Display(new Rect(r.x, r.y + 14f, r.width, 44f), "ENTER THE ROOM CODE", 36, Gui.Paper, TextAnchor.MiddleCenter);
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

        // ---------------------------------------------------------------- VS ONLINE: QUICK MATCH (searching page, no room)

        /// <summary>
        /// QUICK MATCH (Vs Online, Solo): open the searching page and start a search (NetworkRoomManager.QuickMatch: nearest
        /// country / area first, widening with time). When it connects, OnRoomConnected / UpdatePendingOnline start the
        /// match as before. Refused (with the reason) under 13, without internet, or while a room fight is starting.
        /// </summary>
        void StartQuickMatch()
        {
            if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { Deny(OnlineLockedMsg); return; }
            if (!InternetUp) { Deny(NoInternetMsg); return; }
            if (roomsSvc.Launching || roomsSvc.OwnsLink) { Deny("A room fight is starting. Wait for it, or leave the room first."); return; }
            if (roomsSvc.Current != null) roomsSvc.ExitToList();   // not "in" a room during a quick match (the room stays ours)
            pending.cpu[0] = pending.cpu[1] = false;
            pending.training = false;
            onlineNote = "";
            social.ClearMatchFriend();
            if (page != Page.QuickMatch) Enter(Page.QuickMatch);
            bool busy = rooms.State != Netcode.ChibiRoomState.Idle && rooms.State != Netcode.ChibiRoomState.Failed || pendingOnline != null;
            if (!busy)
            {
                quickMatchSince = Time.unscaledTime;
                rooms.QuickMatch();
            }
        }

        /// <summary>Stop a QUICK MATCH search (and a handshake it started). Never touches a room fight's link.</summary>
        void StopQuickMatch()
        {
            if (roomsSvc.OwnsLink || roomsSvc.Launching) return;
            if (pendingOnline != null) { Destroy(pendingOnline.gameObject); pendingOnline = null; pendingOnlineInput = null; }
            rooms.Cancel();
            onlineNote = "";
        }

        /// <summary>
        /// QUICK MATCH page. Left: our fighter (pick while searching; locked once a player is found). Right: SEARCHING
        /// with an animated line, where the search looks now, the elapsed time and the status (errors in red), then
        /// CANCEL (while searching) or SEARCH AGAIN, and BACK to the Vs Online play styles (which stops the search).
        /// </summary>
        void QuickMatchView()
        {
            if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { Enter(Page.Play); return; }   // age gate (13+), defence in depth
            Backdrop();
            NavBar();
            var st = rooms.State;
            bool connecting = pendingOnline != null || st == Netcode.ChibiRoomState.Connecting || st == Netcode.ChibiRoomState.Connected;
            bool failed = !connecting && (st == Netcode.ChibiRoomState.Failed || onlineNote.Length > 0);
            bool searching = !connecting && st != Netcode.ChibiRoomState.Idle && st != Netcode.ChibiRoomState.Failed;
            bool net = InternetUp;
            var a = BeginPage("01", "QUICK MATCH", "VS ONLINE  ·  SOLO 1V1  ·  A PLAYER NEAR YOU");
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
                int secs = Mathf.Max(0, Mathf.FloorToInt(t - quickMatchSince));
                Gui.Text(new Rect(x, y, w, 22f), (secs / 60) + ":" + (secs % 60).ToString("00"), 13, Gui.Muted, TextAnchor.MiddleRight);
            }
            y += 32f;

            // Status: a failed handshake note, the room manager's status (red when it failed), or no internet.
            string status = onlineNote.Length > 0 ? onlineNote : rooms.Status;
            bool err = failed;
            if (!searching && !connecting && !net) { status = NoInternetMsg; err = true; }
            Gui.Text(new Rect(x, y, w, 64f), status, 14, err ? Gui.Red : Gui.Paper, TextAnchor.UpperLeft, false);
            y += 70f;
            // Privacy notice: what quick match shares.
            Gui.Text(new Rect(x, y, w, 36f), "Quick match shares your country (from device settings), never your location.", 11, Gui.Muted, TextAnchor.UpperLeft, false);

            var actionR = new Rect(x, r.yMax - 74f, w, 54f);
            if (searching || connecting)
            {
                if (Gui.Button(actionR, "CANCEL", Gui.Red, 20)) { UiSound.Back(); StopQuickMatch(); }
            }
            else
            {
                Gui.PreferNext();
                if (Gui.Button(actionR, "SEARCH AGAIN", Gui.P2, 20)) StartQuickMatch();
            }

            float by = a.yMax - 48f;
            if (Gui.Button(new Rect(a.xMax - 240f, by, 240f, 46f), "BACK", Gui.Steel, 18)) { UiSound.Back(); OpenPlayStyle(); }   // stops the search
            EndPage();
            NavHints("VS ONLINE  ·  QUICK MATCH");
        }

        // ---------------------------------------------------------------- ROOM (waiting for players)

        /// <summary>
        /// Inside a room (MATCHMAKING, 2-4 seats): choose a fighter (left), press OK to be "in". When exactly 2 players are
        /// seated and both pressed OK the fight starts by itself (3-4 seated: a "coming soon" note, no start) (the host presses START FIGHT once, see <see cref="AutoStartWhenFull"/>);
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
                if (!roomsSvc.Working) { Enter(Page.Rooms); return; }   // join / create failed: the rooms screen shows why
                var w0 = BeginPage("02", "MATCHMAKING", "ROOM  ·  OPENING THE ROOM...");
                Gui.Text(new Rect(w0.x, w0.y + w0.height * 0.35f, w0.width, 40f), roomsSvc.Status.Length > 0 ? roomsSvc.Status : "Opening the room...", 18, Gui.Paper);
                if (Gui.Button(new Rect(w0.xMax - 240f, w0.yMax - 48f, 240f, 46f), "BACK", Gui.Steel, 18)) { UiSound.Back(); Enter(Page.Rooms); }
                EndPage();
                return;
            }
            var a = BeginPage("02", "MATCHMAKING", "ROOM  ·  2-4 PLAYERS  ·  " + cur.Name.ToUpperInvariant());
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
            // 2-4 seats. WAITING FOR PLAYERS under 2, READY TO FIGHT with 2-4 (protocol 6: 3-4 players fight a Battle
            // Royale; the fight starts once everyone pressed OK).
            int joined = Mathf.Min(cur.Slots.Count, Netcode.ChibiRoomRules.MaxSeats);
            bool full = joined >= Netcode.ChibiRoomRules.MaxSeats;
            bool fightable = joined >= Netcode.ChibiRoomRules.MinSeats;
            string head = fightable ? (joined > 2 ? "BATTLE ROYALE" : "READY TO FIGHT") + (full ? " · ROOM FULL" : "") : "WAITING FOR PLAYERS";
            Gui.Text(new Rect(x, y, w, 18f), head + "  ·  " + joined + " / " + Netcode.ChibiRoomRules.MaxSeats + " JOINED",
                     12, fightable ? Gui.Green : Gui.Gold, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(x, y, w, 18f), fightable ? "Everyone presses OK: the fight starts by itself." : "Share the code, or invite friends by name.",
                     12, Gui.Muted, TextAnchor.MiddleRight);
            y += 24f;

            // Seats as a 2 x 2 grid (host top-left). Names come from the room snapshot, already sanitised there
            // (ChibiSocialRules.PeerDisplayName in ChibiRoomRules.Snapshot).
            const float seatH = 62f, seatGap = 8f;
            float seatW = (w - seatGap) * 0.5f;
            for (int i = 0; i < Netcode.ChibiRoomRules.MaxSeats; i++)
            {
                var row = new Rect(x + (i % 2) * (seatW + seatGap), y + (i / 2) * (seatH + seatGap), seatW, seatH);
                Gui.Rect(row, i < cur.Slots.Count ? Gui.PanelLight : WithA(Gui.PanelLight, 0.5f));
                Gui.Rect(new Rect(row.x, row.y, 4f, row.height), SeatColor(i));
                if (i < cur.Slots.Count)
                {
                    var slot = cur.Slots[i];
                    var ch = CharacterRoster.Of(slot.Class);
                    var port = new Rect(row.x + 10f, row.y + 6f, 50f, 50f);
                    CharacterArt.DrawPortrait(port, ch);
                    Gui.Frame(port, ch.Primary, 1f);
                    float tx = row.x + 68f, tw = row.width - 76f;
                    string tag = (i == 0 ? "HOST" : "PLAYER " + (i + 1)) + (slot.PlayerId == me ? " · YOU" : "");
                    Gui.Text(new Rect(tx, row.y + 4f, tw, 22f), slot.Name, 16, Gui.Paper, TextAnchor.MiddleLeft);
                    Gui.Text(new Rect(tx, row.y + 24f, tw, 18f), tag, 11, SeatColor(i), TextAnchor.MiddleLeft, true, false);
                    Gui.Text(new Rect(tx, row.y + 41f, tw, 18f), Hud.CharNameUpper(ch.Class), 11, Gui.Muted, TextAnchor.MiddleLeft, true, false);
                    Gui.Display(new Rect(tx, row.y + 34f, tw - 4f, 26f), slot.Ready ? "READY" : "choosing...", slot.Ready ? 18 : 12,
                                slot.Ready ? Gui.Green : Gui.Muted, TextAnchor.MiddleRight, slot.Ready ? 0.15f : 0f);
                }
                else Gui.Text(new Rect(row.x + 14f, row.y, row.width - 20f, seatH), "Empty seat", 14, Gui.Muted, TextAnchor.MiddleLeft);
            }
            y += 2f * (seatH + seatGap) + 2f;

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
            Gui.Text(new Rect(x, y, w, 50f), status, 14, roomsSvc.StatusIsError || onlineNote.Length > 0 ? Gui.Red : Gui.Paper,
                     TextAnchor.UpperCenter, false);

            // Bottom left (host): INVITE A FRIEND BY NAME (the in-game name they chose at first launch).
            float by = a.yMax - 48f;
            InviteByNameRow(new Rect(a.x, by, a.xMax - 460f - a.x, 46f), cur, host, full, launching);

            // Bottom right: leave for good, or back to the rooms screen (we stay a member of the room).
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
            NavHints("MATCHMAKING  ·  ROOM");
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
                nameInvites.Send(name, cur.Code, cur.Name);   // host of a room: our name was claimed to create it
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
        /// also the Vs CPU menu, so opening it must not sign in or poll Lobby (SEC-007). Not the QUICK MATCH
        /// page either: its search queries Lobby too, and both share one 1-per-second Query limit (network review
        /// 2026-11-22), so polling there would make the search hit "too many requests" and back off.
        /// </summary>
        bool OnInviteMenuPage =>
            page == Page.PlayStyle || page == Page.Rooms || page == Page.Room;

        /// <summary>ChibiNameInvites.Enabled: online allowed (13+), on an online menu page, no online match starting.</summary>
        bool NameInvitesWanted => PlayerIdentity.OnlineAllowed(Save.ageBand) && OnInviteMenuPage && pendingOnline == null && wipeStart < 0f;

        /// <summary>
        /// The pop-up is drawn this frame: an invite is waiting, no window covers the page, and we are free to answer it
        /// (SEC-003): not seated in a room (Room page), no room join / start in progress, no online match starting.
        /// Invites stay pending meanwhile and show once we are back on the rooms screen.
        /// </summary>
        bool NameInviteShown => nameInvites != null && nameInvites.Pending.Count > 0 && NameInvitesWanted && !DialogOpen &&
                                !(page == Page.Room && roomsSvc.Current != null) && !roomsSvc.Working && !roomsSvc.Launching &&
                                !OnlineMatchStarting;

        /// <summary>A quick-match or room link is connecting, or a handshake runs: an online match is about to start.</summary>
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
                    Enter(Page.Room);   // leaving QUICK MATCH this way stops its search (Enter)
                }
            }
            if (Gui.Button(new Rect(r.xMax - 270f, r.y + 102f, 200f, 46f), "DECLINE", Gui.Red, 17)) nameInvites.Decline(0);
        }

        /// <summary>
        /// "Start when ready": once exactly 2 players are seated and both pressed OK, the host runs the START FIGHT action once
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

        // ---------------------------------------------------------------- compact fighter picker (room / quick match)

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
