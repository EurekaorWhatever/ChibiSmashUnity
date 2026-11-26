using System;
using System.Collections;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Development-only automation for testing online play with two game instances on one PC (editor + a development
    /// Windows build). Every entry point checks <see cref="DevAutomationAllowed"/> (editor or a development build) at
    /// runtime, so in a release or the web build nothing here ever runs. (Unity 6.6 deprecates the DEVELOPMENT_BUILD
    /// define and no longer sets it for development players, so a compile-time #if left the hook out entirely.)
    /// It drives exactly the UI paths a player uses: create/join lobby → waiting screen → pick → OK → the host auto-starts.
    /// </summary>
    /// <remarks>
    /// Development build command line: <c>ChibiClash.exe -autoJoinLobby ABC123</c> (joins that lobby and presses OK).
    /// In the editor (Play Mode, e.g. through Unity MCP): <see cref="DevCreateLobby"/>, then <see cref="DevLobbyInfo"/>.
    /// </remarks>
    public partial class GameManager
    {
        const string DevJoinArg = "-autoJoinLobby";

        /// <summary>The automation only works in the editor or a development build (Debug.isDebugBuild).</summary>
        static bool DevAutomationAllowed => Application.isEditor || Debug.isDebugBuild;

        /// <summary>
        /// Skip first-launch onboarding for a FRESH test player only: if no age was ever given, an adult test age and a
        /// name. A profile that already has an age keeps it (its age group is re-derived from that age, never forced).
        /// </summary>
        public void DevPrepareOnlinePlayer(string name)
        {
            if (!DevAutomationAllowed) return;
            if (Save.age <= 0) Save.age = 20;
            Save.ageBand = (int)PlayerIdentity.BandForAge(Save.age);
            if (string.IsNullOrEmpty(Save.playerName)) Save.playerName = name;
            nameDialog = false;
            Save.Save();
        }

        /// <summary>Create a room (MATCHMAKING) and open its screen, like CREATE ROOMS then clicking the new room block.</summary>
        public void DevCreateLobby(string lobbyName)
        {
            if (!DevAutomationAllowed) return;
            DevPrepareOnlinePlayer("DevHost");
            FinishLoadingNow();
            OpenRoomsPage();
            StartCoroutine(DevWhenIdle(() => roomsSvc.Create(lobbyName, pending.stage, pending.hazards)));
        }

        /// <summary>Join a room by code and open its screen, like JOIN ROOM in the MATCHMAKING rooms screen.</summary>
        public void DevJoinLobby(string code)
        {
            if (!DevAutomationAllowed) return;
            DevPrepareOnlinePlayer("DevGuest");
            FinishLoadingNow();
            OpenRoomsPage();
            StartCoroutine(DevWhenIdle(() => roomsSvc.JoinByCode(code)));
        }

        /// <summary>
        /// Opening the lobby page refreshes the lobby list, and the service ignores requests while it is busy. A player
        /// clicks after that finishes, so the automation waits the same way, then acts and opens the waiting screen.
        /// </summary>
        IEnumerator DevWhenIdle(Action act)
        {
            float until = Time.realtimeSinceStartup + 30f;
            while (roomsSvc.Working && Time.realtimeSinceStartup < until) yield return null;
            act();
            Enter(Page.Room);
            yield return null;
            yield return DevPressOkWhenSynced();
        }

        /// <summary>Start a Vs Online QUICK MATCH exactly like the SOLO card (the search page starts a real regional search).</summary>
        public void DevMatchmake()
        {
            if (!DevAutomationAllowed) return;
            DevPrepareOnlinePlayer("DevPlayer");
            FinishLoadingNow();
            ShowScreen("quickmatch");
        }

#if UNITY_EDITOR
        /// <summary>Editor only: open CREATE ROOM with a sample room invite waiting (screenshot of the bottom-right card).</summary>
        public void DevShowInviteCard()
        {
            DevScreenshotPrep();
            nameInvites.DevInjectSample("Kira", "Kira's room");
            Enter(Page.Rooms);
        }

        /// <summary>Editor only: open the CHARACTER COLLECTION roster screen.</summary>
        public void DevShowCollection() { DevScreenshotPrep(); Enter(Page.Collection); }

        /// <summary>
        /// Editor only, IN MEMORY (never saved): English, no first-launch windows, an adult test age and a name, so a
        /// screenshot shows the page itself. A later save of this session could still write them, so use a test profile.
        /// </summary>
        void DevScreenshotPrep()
        {
            if (string.IsNullOrEmpty(Save.language)) Save.language = "en";
            Loc.Use("en");
            langDialog = false;
            if (Save.age <= 0) { Save.age = 20; Save.ageBand = (int)PlayerIdentity.BandForAge(Save.age); }
            if (string.IsNullOrEmpty(Save.playerName)) Save.playerName = "Pluto";
            nameDialog = false;
        }
#endif

        /// <summary>From inside our room: invite a player by in-game name, like the INVITE button.</summary>
        public void DevInvite(string friendName)
        {
            if (!DevAutomationAllowed || roomsSvc.Current == null) return;
            nameInvites.Send(friendName, roomsSvc.Current.Code, roomsSvc.Current.Name);
        }

        /// <summary>Wait on the MATCHMAKING rooms screen for a ROOM INVITE, then ACCEPT it (join by its code) and press OK.</summary>
        IEnumerator DevAcceptFirstInvite()
        {
            DevPrepareOnlinePlayer("DevGuest");
            FinishLoadingNow();
            OpenRoomsPage();   // an online menu page: invite receiving is on here
            float until = Time.realtimeSinceStartup + 180f;
            while (Time.realtimeSinceStartup < until && nameInvites.Pending.Count == 0) yield return null;
            if (nameInvites.Pending.Count == 0) yield break;
            string code = nameInvites.Accept(0);
            if (code.Length > 0) yield return DevWhenIdle(() => roomsSvc.JoinByCode(code));
        }

        /// <summary>One-line state for test scripts: page, lobby code, seats, ready flags, status, online match state.</summary>
        public string DevLobbyInfo()
        {
            if (!DevAutomationAllowed) return "";
            var cur = roomsSvc != null ? roomsSvc.Current : null;
            string seats = "";
            if (cur != null)
                foreach (var s in cur.Slots) seats += (seats.Length > 0 ? ", " : "") + s.Name + (s.Ready ? " (OK)" : "");
            var st = match != null ? match.State : null;
            return "page=" + page + " code=" + (cur != null ? cur.Code : "-") + " seats=[" + seats + "]" +
                   " status=\"" + (roomsSvc != null ? roomsSvc.Status : "") + "\" note=\"" + onlineNote + "\"" +
                   " match=" + (match != null) + (st != null ? " ticks=" + st.GameTicks : "") +
                   " | mm=" + (rooms != null ? rooms.State + " \"" + rooms.Status + "\"" : "-") +
                   " | invites pending=" + (nameInvites != null ? nameInvites.Pending.Count + " \"" + nameInvites.Status + "\"" : "-");
        }

        /// <summary>
        /// Ask the name server (Cloud Code script "Chibi_Clash", code Assets/ChibiClash/CloudCode/Chibi_Clash.js) about <paramref name="name"/> as THIS player: check, then claim.
        /// Logs one "[DevNameTest]" line per call, so a second instance can prove "This name is already taken".
        /// </summary>
        public async void DevNameTest(string name)
        {
            if (!DevAutomationAllowed) return;
            var check = await nameRegistry.CheckAsync(name);
            var claim = await nameRegistry.ClaimAsync(name);
            var stats = await nameRegistry.StatsAsync();
            Debug.Log("[DevNameTest] name=" + name + " check=" + check.Status + " claim=" + claim.Status +
                      " stats=" + stats.Status + " players=" + stats.Players);
        }

        /// <summary>
        /// Open CHANGE NAME, type <paramref name="name"/> and press SAVE, exactly like a player (validation, worldwide
        /// claim, "This name is already taken" when another player owns it). Logs "[DevTypeName]" with the outcome.
        /// </summary>
        public async void DevTypeNameAndSave(string name)
        {
            if (!DevAutomationAllowed) return;
            FinishLoadingNow();
            OpenNameDialog();
            nameInput = name;
            string err = Netcode.ChibiSocialRules.ValidateName(name);
            if (err != null) { nameError = err; Debug.Log("[DevTypeName] " + name + " invalid: " + err); return; }
            ClaimAndSaveName(name);
            float until = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < until && nameClaiming) await System.Threading.Tasks.Task.Yield();
            await System.Threading.Tasks.Task.Delay(200);
            Debug.Log("[DevTypeName] " + name + " -> dialogOpen=" + nameDialog + " error=\"" + nameError + "\" saved=\"" + Save.playerName + "\"");
        }

        /// <summary>Press OK once the lobby has synced, like a player would after picking a fighter.</summary>
        IEnumerator DevPressOkWhenSynced()
        {
            float until = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < until && (roomsSvc.Current == null || !roomsSvc.Synced || roomsSvc.Working))
                yield return null;
            if (roomsSvc.Current != null) roomsSvc.SetReady(true);
        }

        /// <summary>
        /// Development build command line, after loading:
        /// <c>-autoJoinLobby CODE</c> joins that room; <c>-autoMatchmake</c> starts a QUICK MATCH;
        /// <c>-autoAcceptInvites</c> waits on the MATCHMAKING rooms screen and accepts the first ROOM INVITE.
        /// </summary>
        IEnumerator DevAutomationFromCommandLine()
        {
            if (!DevAutomationAllowed) yield break;
            var args = Environment.GetCommandLineArgs();
            string code = null;
            bool matchmake = false, acceptInvites = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (i < args.Length - 1 && string.Equals(args[i], DevJoinArg, StringComparison.OrdinalIgnoreCase)) code = args[i + 1];
                if (string.Equals(args[i], "-autoMatchmake", StringComparison.OrdinalIgnoreCase)) matchmake = true;
                if (string.Equals(args[i], "-autoAcceptInvites", StringComparison.OrdinalIgnoreCase)) acceptInvites = true;
                if (i < args.Length - 1 && string.Equals(args[i], "-devTypeName", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new WaitForSecondsRealtime(5f);
                    DevTypeNameAndSave(args[i + 1]);
                    yield break;
                }
                if (i < args.Length - 1 && string.Equals(args[i], "-devNameTest", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new WaitForSecondsRealtime(3f);
                    DevNameTest(args[i + 1]);
                    yield break;
                }
            }
            if (string.IsNullOrEmpty(code) && !matchmake && !acceptInvites) yield break;
            yield return new WaitForSecondsRealtime(3f);   // let the game finish loading
            if (!string.IsNullOrEmpty(code)) DevJoinLobby(code);
            else if (matchmake) DevMatchmake();
            else yield return DevAcceptFirstInvite();
        }

        void Start() => StartCoroutine(DevAutomationFromCommandLine());
    }
}
