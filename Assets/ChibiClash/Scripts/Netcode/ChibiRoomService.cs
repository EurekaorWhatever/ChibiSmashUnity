using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>What the friends layer needs from rooms: a room to invite into, and joining an accepted invite.</summary>
    public interface IChibiInviteRooms
    {
        /// <summary>Code of the room we are in ("" when none yet).</summary>
        string RoomCode { get; }
        /// <summary>The last room request failed (see <see cref="RoomStatus"/>).</summary>
        bool RoomFailed { get; }
        string RoomStatus { get; }
        /// <summary>Make a room to invite a friend into (no-op when we are already in one).</summary>
        void CreateRoomForInvite(string roomName);
        /// <summary>Join the room of an accepted invite.</summary>
        void JoinRoomForInvite(string code);
    }

    /// <summary>
    /// CREATE ROOM mode (docs/architecture/netcode/online-rooms.md "Rooms"): named private rooms that stay open between
    /// fights. Each room is a private Unity Lobby of 2 to 4 seats (<see cref="ChibiRoomRules.MaxSeats"/>). Inside it
    /// the players choose a fighter and press OK (Ready); START FIGHT works (protocol 6) when 2-4 seated players are all ready; it used to need exactly
    /// 2 players are seated and both are ready (3-4 seated players wait for the 4-player engine). Either player may press it:
    ///   host presses  -> host un-readies, opens the match link (Relay) and publishes {relay, go} in the lobby
    ///   guest presses -> guest publishes a start nonce; the host sees it on its next poll and does the above
    ///   guest sees a NEW go -> acknowledges it (ack = go, ready off) -> connects -> handshake -> match (unchanged core)
    ///   no acknowledgement within <see cref="AckTimeoutSeconds"/> -> the host cancels quickly
    /// After the fight both return to the room and press OK again. Room actions only happen while the room screen is
    /// open (<see cref="ScreenOpen"/>); leaving the screen un-readies. Rules live in <see cref="ChibiRoomRules"/> (pure,
    /// tested); this class only talks to the Lobby service. Reviewed by the network-programmer and security-engineer
    /// agents (races, relaunch spam, stale launches, rate limits). Main thread only.
    /// </summary>
    /// <example><code>
    /// var svc = gameObject.AddComponent&lt;ChibiRoomService&gt;();
    /// svc.Bind(roomManager);  svc.Create("Kira's Dojo", stage: 0);  svc.SetReady(true);  svc.PressStart();
    /// </code></example>
    public sealed class ChibiRoomService : MonoBehaviour, IChibiInviteRooms
    {
        const string KeyStage = "stage", KeyGo = "go", KeyRelay = "relay", KeyProto = "proto";
        /// <summary>Rules v20 (ST-5): the host's stage hazards switch, member-visible ("1" on, "0" off).</summary>
        const string KeyHazards = "hz";
        const string PName = "n", PClass = "c", PPassive = "p", PReady = "r", PStart = "s", PAck = "a", PKey = "k";

        /// <summary>
        /// SEC-003: this device's room seat secret. Only its key (<see cref="ChibiRoomRules.SeatKey"/>) goes into the lobby
        /// player data (members only); the secret itself travels only in the match Hello to the host. SEC-005: a fresh
        /// secret for every seat (entering a room) and after every launch (fight over or aborted), so a host that saw it
        /// once cannot reuse it later to take this player's seat in another room or fight.
        /// </summary>
        public ulong MySecret => _mySecret;
        ulong _mySecret = ChibiRoomRules.NewSeatSecret();
        string _myKey;
        string MyKey => _myKey ?? (_myKey = ChibiRoomRules.SeatKey(_mySecret));

        void RenewSeatSecret()
        {
            _mySecret = ChibiRoomRules.NewSeatSecret();
            _myKey = null;
        }

        /// <summary>SEC-003 / L2: the published seat keys of the current room by seat (host = 0).</summary>
        public string[] SeatKeys() => ChibiRoomRules.SeatKeys(Current);

        /// <summary>SEC-004: the names to draw for a room fight, by seat = sim slot (lobby data, already sanitised).</summary>
        public string[] SeatNames()
        {
            var names = new string[ChibiRoomRules.MaxSeats];
            for (int i = 0; i < names.Length; i++) names[i] = Current != null && i < Current.Slots.Count ? Current.Slots[i].Name : "";
            return names;
        }
        const float PollSeconds = 2f, HeartbeatSeconds = 15f, ListRefreshMinSeconds = 10f;
        /// <summary>A launch that has not connected after this long is cancelled.</summary>
        public const float LaunchTimeoutSeconds = 25f;
        /// <summary>The host cancels a launch the guest has not acknowledged within this long (guest gone or not ready).</summary>
        public const float AckTimeoutSeconds = 8f;
        /// <summary>At most this many fights started by the guest per <see cref="GuestLaunchWindowSeconds"/> (anti-spam).</summary>
        public const int MaxGuestLaunches = 3;
        public const float GuestLaunchWindowSeconds = 120f;
        /// <summary>Rooms one player may host at once (each needs a heartbeat; Lobby limits heartbeats per player).</summary>
        public const int MaxHostedRooms = 2;
        const int MaxListedRooms = 10;

        /// <summary>Rooms we are in (hosted or joined), for the room blocks on the ROOMS page.</summary>
        public readonly List<ChibiRoomSnapshot> MyRooms = new List<ChibiRoomSnapshot>();
        /// <summary>The room whose screen is open (null on the room list).</summary>
        public ChibiRoomSnapshot Current { get; private set; }
        /// <summary>Id of the room whose screen is open ("" none).</summary>
        public string CurrentId { get; private set; } = "";
        /// <summary>We pressed OK in the open room.</summary>
        public bool LocalReady { get; private set; }
        /// <summary>The open room was read fresh after entering (OK is allowed only then).</summary>
        public bool Synced { get; private set; }
        /// <summary>A request is in flight (buttons disabled).</summary>
        public bool Working { get; private set; }
        /// <summary>START FIGHT was triggered and the match link is being set up.</summary>
        public bool Launching { get; private set; }
        /// <summary>The current match link was started by this room (the game takes fighter and stage from here).</summary>
        public bool OwnsLink { get; private set; }
        public string Status { get; private set; } = "";
        public bool StatusIsError { get; private set; }
        /// <summary>The room list was loaded at least once (so an empty list really means "no rooms").</summary>
        public bool ListLoaded { get; private set; }

        /// <summary>Our player name to show in rooms (set by the game).</summary>
        public Func<string> MyName = () => "";
        /// <summary>Age gate (set by the game): false refuses every room action.</summary>
        public Func<bool> OnlineAllowed = () => true;
        /// <summary>
        /// Name gate (set by the game): claims our player name worldwide before a room is created or joined. Returns
        /// null when the name is ours, else the reason to show. Every create / join passes here, whatever screen calls it.
        /// </summary>
        public Func<Task<string>> NameGate = () => Task.FromResult<string>(null);
        /// <summary>True while a match is running (polling pauses; heartbeats continue).</summary>
        public Func<bool> InMatch = () => false;
        /// <summary>True while the room screen is on show: launches and starts are only acted on then.</summary>
        public Func<bool> ScreenOpen = () => true;

        /// <summary>Our player id ("" before sign-in).</summary>
        /// <remarks>
        /// AuthenticationService.Instance THROWS (ServicesInitializationException) until UnityServices.InitializeAsync has
        /// completed, which never happens in a project not linked to Unity Gaming Services; Update reads this every
        /// frame, so check the services state first (found in Play Mode 2026-11-10: exception spam every frame).
        /// </remarks>
        public string MyId => Unity.Services.Core.UnityServices.State == Unity.Services.Core.ServicesInitializationState.Initialized &&
                              AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn
            ? AuthenticationService.Instance.PlayerId : "";
        /// <summary>We host the open room.</summary>
        public bool IsHost => Current != null && Current.HostId.Length > 0 && Current.HostId == MyId;
        /// <summary>The fighter we published with OK (the match uses exactly this).</summary>
        public FighterClass PickClass => _class;
        public int PickPassive => _passive;
        /// <summary>The host's stage choice for the next fight (0..7).</summary>
        public int Stage => Current != null ? Current.Stage : 0;
        /// <summary>The host's hazards choice for the next fight (ST-5; true = on, the default).</summary>
        public bool HazardsOn => Current == null || !Current.HazardsOff;

        NetworkRoomManager _link;
        FighterClass _class;
        int _passive;
        float _pollAt, _heartbeatAt, _listAt = -999f, _launchStarted, _goPublishedAt;
        bool _polling, _guestJoining;
        string _baselineGo = "", _lastGuestNonce = "", _myStartNonce = "", _myAck = "", _publishedGo = "";
        /// <summary>PlayerId whose start press <see cref="_lastGuestNonce"/> belongs to (presses are keyed by player, not seat).</summary>
        string _lastGuestId = "";
        /// <summary>SEC-001: the guest a launch began with ("" when no launch). The launch aborts if the seats change.</summary>
        string _launchGuestId = "";
        /// <summary>A stale lock (a launch that never ended cleanly) was already lifted for this room id.</summary>
        string _unlockSentFor = "";
        readonly Queue<float> _guestLaunchTimes = new Queue<float>();

        /// <summary>The room manager that opens the actual match link.</summary>
        public void Bind(NetworkRoomManager link) => _link = link;

        // ================================================================== IChibiInviteRooms

        public string RoomCode => Current != null ? Current.Code : "";
        public bool RoomFailed => StatusIsError;
        public string RoomStatus => Status;
        public void CreateRoomForInvite(string roomName) { if (Current == null) Create(roomName, 0); }
        public void JoinRoomForInvite(string code) => JoinByCode(code);

        // ================================================================== list

        /// <summary>Reload the rooms we are in (throttled and spaced: Lobby allows about one read per second).</summary>
        public async void Refresh(bool force = false)
        {
            if (Working || !Allowed()) return;
            if (!force && Time.unscaledTime - _listAt < ListRefreshMinSeconds) return;
            _listAt = Time.unscaledTime;
            Working = true;
            try
            {
                string err = await NetworkRoomManager.EnsureSignedInAsync();
                if (err != null) { SetStatus(err, true); return; }
                var ids = await LobbyService.Instance.GetJoinedLobbiesAsync();
                var fresh = new List<ChibiRoomSnapshot>();
                if (ids != null)
                    for (int k = 0; k < ids.Count && fresh.Count < MaxListedRooms; k++)
                    {
                        if (k > 0) await Task.Delay(1100);   // spaced reads
                        try
                        {
                            var l = await LobbyService.Instance.GetLobbyAsync(ids[k]);
                            // Our invite-by-name beacon (ChibiNameInvites) is a joined lobby too, but not a room. Known
                            // by its index data key, not its name: a player may name a real room "ChibiInvite".
                            if (l != null && !(l.Data != null && l.Data.ContainsKey(ChibiNameInviteRules.KeyIndex)))
                                fresh.Add(ToSnapshot(l));
                        }
                        catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyNotFound || e.Reason == LobbyExceptionReason.Forbidden)
                        { /* closed or we were removed */ }
                        catch (Exception)
                        {
                            var old = Find(ids[k]);   // busy service: keep what we knew (never drop a live room)
                            if (old != null) fresh.Add(old);
                        }
                    }
                MyRooms.Clear();
                MyRooms.AddRange(fresh);
                ListLoaded = true;
                SetStatus("", false);
            }
            catch (Exception e) { SetStatus("Could not load your rooms: " + NetworkRoomManager.Explain(e), true); }
            finally { Working = false; }
        }

        /// <summary>The signed-in account changed: the old account's rooms are not ours any more.</summary>
        public void ResetForAccountChange()
        {
            if (Launching) _link?.Cancel();
            MyRooms.Clear();
            Current = null;
            CurrentId = "";
            ListLoaded = false;
            _listAt = -999f;
            ResetSeat(true);
            SetStatus("", false);
        }

        // ================================================================== create / join / enter / leave

        /// <summary>Create a named room (validated) and open it.</summary>
        public async void Create(string roomName, int stage, bool hazardsOn = true)
        {
            if (Working || !Allowed()) return;
            string name = (roomName ?? "").Trim();
            string bad = ChibiSocialRules.ValidateRoomName(name);
            if (bad != null) { SetStatus(bad, true); return; }
            int hosted = 0;
            foreach (var r in MyRooms) if (r.HostId == MyId && MyId.Length > 0) hosted++;
            if (hosted >= MaxHostedRooms) { SetStatus("You already host " + MaxHostedRooms + " rooms. Close one first.", true); return; }
            Working = true;
            SetStatus("Creating \"" + name + "\"...", false);
            try
            {
                string err = await NetworkRoomManager.EnsureSignedInAsync() ?? await NameGate();
                if (err != null) { SetStatus(err, true); return; }
                await LeaveCurrentSeat();
                var options = new CreateLobbyOptions
                {
                    IsPrivate = true,
                    Player = MyPlayer(),
                    Data = new Dictionary<string, DataObject>
                    {
                        [KeyProto] = new DataObject(DataObject.VisibilityOptions.Member, NetworkRoomManager.ProtocolTag),
                        [KeyStage] = new DataObject(DataObject.VisibilityOptions.Member, ChibiRoomRules.ParseStage(stage.ToString()).ToString()),
                        [KeyHazards] = new DataObject(DataObject.VisibilityOptions.Member, ChibiRoomRules.HazardsValue(hazardsOn)),
                        [KeyGo] = new DataObject(DataObject.VisibilityOptions.Member, ""),
                        [KeyRelay] = new DataObject(DataObject.VisibilityOptions.Member, ""),
                    },
                };
                var lobby = await LobbyService.Instance.CreateLobbyAsync(ChibiSocialRules.SafeRoomName(name), ChibiRoomRules.MaxSeats, options);
                Open(lobby, synced: true);   // we just created it: nothing stale
                SetStatus("Room \"" + Current.Name + "\" is ready. Share the code " + Current.Code + " or invite a friend.", false);
            }
            catch (Exception e) { SetStatus("Could not create the room: " + NetworkRoomManager.Explain(e), true); }
            finally { Working = false; }
        }

        /// <summary>Join a friend's room by its code and open it.</summary>
        public async void JoinByCode(string code)
        {
            if (Working || !Allowed()) return;
            string c = ChibiSocialRules.NormalizeCode(code);
            if (c == null) { SetStatus("That is not a valid room code (letters and numbers only).", true); return; }
            foreach (var r in MyRooms) if (r.Code == c) { Enter(r.Id); return; }   // already a member
            Working = true;
            SetStatus("Joining room " + c + "...", false);
            try
            {
                string err = await NetworkRoomManager.EnsureSignedInAsync() ?? await NameGate();
                if (err != null) { SetStatus(err, true); return; }
                await LeaveCurrentSeat();
                var lobby = await LobbyService.Instance.JoinLobbyByCodeAsync(c, new JoinLobbyByCodeOptions { Player = MyPlayer() });
                // Version gate at join time (network review 2026-11-10): the room's protocol tag is member data, readable now.
                string roomTag = lobby.Data != null && lobby.Data.TryGetValue(KeyProto, out var proto) ? proto?.Value : null;
                if (!ChibiRoomRules.SameBuild(roomTag, NetworkRoomManager.ProtocolTag))
                {
                    try { await LobbyService.Instance.RemovePlayerAsync(lobby.Id, MyId); } catch (Exception) { /* best effort */ }
                    SetStatus("That room was made with a different version of Chibi Clash. Both players need the same version.", true);
                    return;
                }
                Open(lobby, synced: false);   // an older launch may be in the lobby: baseline from the first poll
                SetStatus("You joined \"" + Current.Name + "\". Choose your fighter and press OK.", false);
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyNotFound || e.Reason == LobbyExceptionReason.InvalidJoinCode)
            { SetStatus("No room with code " + c + ". Check the code and try again.", true); }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyFull)
            { SetStatus("That room is full (" + ChibiRoomRules.MaxSeats + " players).", true); }
            catch (Exception e) { SetStatus("Could not join: " + NetworkRoomManager.Explain(e), true); }
            finally { Working = false; }
        }

        /// <summary>Open a room we are already in (a room block was clicked). OK unlocks after the first fresh read.</summary>
        public void Enter(string roomId)
        {
            if (!Allowed()) return;
            var r = Find(roomId);
            if (r == null) return;
            if (CurrentId.Length > 0 && CurrentId != roomId) _ = LeaveCurrentSeat();
            Current = r;
            CurrentId = r.Id;
            ResetSeat(synced: false);
            _pollAt = 0f;   // read it now
            SetStatus("Loading the room...", false);
        }

        /// <summary>Back to the room list (we stay a member; the room stays open; our OK is withdrawn).</summary>
        public void ExitToList()
        {
            _ = LeaveCurrentSeat();
            Current = null;
            CurrentId = "";
        }

        /// <summary>Leave a room for good (the host closes it for both).</summary>
        public async void Leave(string roomId)
        {
            if (Working || string.IsNullOrEmpty(roomId)) return;
            Working = true;
            var room = Find(roomId);
            bool host = room != null && room.HostId == MyId;
            try
            {
                if (host) await LobbyService.Instance.DeleteLobbyAsync(roomId);
                else await LobbyService.Instance.RemovePlayerAsync(roomId, MyId);
                SetStatus(host ? "Room closed." : "You left the room.", false);
            }
            catch (Exception) { /* already gone */ }
            finally
            {
                MyRooms.RemoveAll(r => r.Id == roomId);
                if (CurrentId == roomId) { Current = null; CurrentId = ""; ResetSeat(true); }
                Working = false;
            }
        }

        // ================================================================== in the room

        /// <summary>Our fighter choice (published with OK; changing it is only possible while not ready).</summary>
        public void SetPick(FighterClass cls, int passive)
        {
            if (LocalReady || Launching) return;
            _class = cls;
            _passive = passive == 0 ? 0 : 1;
        }

        /// <summary>OK (ready = "I'm in with this fighter") or CHANGE (not ready). OK needs a fresh read of the room.</summary>
        public async void SetReady(bool ready)
        {
            if (Current == null || Working || !Allowed()) return;
            if (ready && !Synced) return;
            if (LocalReady == ready) return;
            LocalReady = ready;
            if (!ready) _myStartNonce = "";
            await PushPlayer();
        }

        /// <summary>Host: choose the stage of the next fight.</summary>
        public async void SetStage(int stage)
        {
            if (!IsHost || Working || Current == null || !Allowed()) return;
            stage = ChibiRoomRules.ParseStage(stage.ToString());
            if (stage == Current.Stage) return;
            // SEC-001b: no optimistic local write; Current changes only from the lobby the service returns (Apply on success).
            await UpdateLobbyData(new Dictionary<string, DataObject> { [KeyStage] = Member(stage.ToString()) }, "change the stage");
        }

        /// <summary>Host: turn the stage hazards of the next fight on or off (ST-5).</summary>
        public async void SetHazards(bool on)
        {
            if (!IsHost || Working || Current == null || !Allowed()) return;
            if (on == !Current.HazardsOff) return;
            // SEC-001b: no optimistic local write; Current changes only from the lobby the service returns (Apply on success).
            await UpdateLobbyData(new Dictionary<string, DataObject> { [KeyHazards] = Member(ChibiRoomRules.HazardsValue(on)) },
                                  "change the hazards");
        }

        /// <summary>START FIGHT: only when both players are ready (else the reason is shown). Either player may press it.</summary>
        public async void PressStart()
        {
            if (Current == null || Launching || Working || !Allowed()) return;
            if (!ChibiRoomRules.CanStart(Current)) { SetStatus(ChibiRoomRules.WaitingFor(Current, MyId), true); return; }
            if (IsHost) { await Launch(); return; }
            _myStartNonce = ChibiRoomRules.NewNonce();
            SetStatus("Starting... waiting for the host's game.", false);
            await PushPlayer();
            _pollAt = Time.unscaledTime + 0.5f;
        }

        /// <summary>The fight ended (or never started): back in the room, both press OK again for the next fight.</summary>
        public void OnMatchEnded()
        {
            bool wasHost = IsHost;
            ResetSeat(synced: Synced);
            if (Current == null) return;
            _ = PushPlayer();
            // SEC-001: the room was locked for the fight; open it again for new players.
            if (wasHost) _ = UpdateLobbyData(new Dictionary<string, DataObject> { [KeyGo] = Member(""), [KeyRelay] = Member("") }, null, locked: false);
            SetStatus("Choose your fighter and press OK for the next fight.", false);
        }

        // ================================================================== internals

        bool Allowed()
        {
            if (OnlineAllowed == null || OnlineAllowed()) return true;
            SetStatus(NetworkRoomManager.NotAllowedMessage, true);
            return false;
        }

        ChibiRoomSnapshot Find(string id)
        {
            foreach (var r in MyRooms) if (r.Id == id) return r;
            return null;
        }

        void ResetSeat(bool synced)
        {
            RenewSeatSecret();   // SEC-005: every seat / launch gets its own secret (pushed with the next player data)
            LocalReady = false;
            Launching = false;
            OwnsLink = false;
            _guestJoining = false;
            _myStartNonce = "";
            _publishedGo = "";
            _launchGuestId = "";
            Synced = synced;
        }

        /// <summary>Withdraw OK / start in the room we are leaving (so nobody launches against an empty seat).</summary>
        async Task LeaveCurrentSeat()
        {
            if (Current == null || (!LocalReady && _myStartNonce.Length == 0)) { ResetSeat(Synced); return; }
            ResetSeat(Synced);
            await PushPlayer();
        }

        static DataObject Member(string v) => new DataObject(DataObject.VisibilityOptions.Member, v);
        static PlayerDataObject PMember(string v) => new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, v);

        Player MyPlayer() => new Player(id: MyId, data: PlayerData());

        Dictionary<string, PlayerDataObject> PlayerData()
        {
            // Only the chosen part of the name is shared (no #tag: a stranger with the code cannot harvest friend tags).
            string name = ChibiSocialRules.BaseName(ChibiSocialRules.SafeDisplayName(MyName?.Invoke()));
            return new Dictionary<string, PlayerDataObject>
            {
                [PName] = PMember(name),
                [PClass] = PMember(((int)_class).ToString()),
                [PPassive] = PMember(_passive.ToString()),
                [PReady] = PMember(LocalReady ? "1" : "0"),
                [PStart] = PMember(_myStartNonce),
                [PAck] = PMember(_myAck),
                [PKey] = PMember(MyKey),
            };
        }

        async Task PushPlayer()
        {
            string id = CurrentId;
            if (id.Length == 0 || MyId.Length == 0) return;
            try
            {
                var lobby = await LobbyService.Instance.UpdatePlayerAsync(id, MyId, new UpdatePlayerOptions { Data = PlayerData() });
                Apply(lobby);
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyNotFound) { if (id == CurrentId) RoomGone(); }
            catch (Exception e) { SetStatus("Could not update the room: " + NetworkRoomManager.Explain(e), true); }
        }

        /// <param name="locked">Also lock (true) or unlock (false) the Lobby; null leaves it as it is (SEC-001).</param>
        async Task<bool> UpdateLobbyData(Dictionary<string, DataObject> data, string what, bool? locked = null)
        {
            try
            {
                var options = new UpdateLobbyOptions { Data = data };
                if (locked.HasValue) options.IsLocked = locked.Value;
                Apply(await LobbyService.Instance.UpdateLobbyAsync(CurrentId, options));
                return true;
            }
            catch (Exception e)
            {
                if (what != null) SetStatus("Could not " + what + ": " + NetworkRoomManager.Explain(e), true);
                return false;
            }
        }

        void Open(Lobby lobby, bool synced)
        {
            var snap = ToSnapshot(lobby);
            MyRooms.RemoveAll(r => r.Id == snap.Id);
            MyRooms.Insert(0, snap);
            ListLoaded = true;
            Current = snap;
            CurrentId = snap.Id;
            ResetSeat(synced);
            _baselineGo = snap.Go;
            RememberGuestPress(snap);
            _heartbeatAt = Mathf.Min(_heartbeatAt, Time.unscaledTime + HeartbeatSeconds);
            _pollAt = Time.unscaledTime + PollSeconds;
        }

        void Apply(Lobby lobby)
        {
            if (lobby == null) return;
            var snap = ToSnapshot(lobby);
            for (int i = 0; i < MyRooms.Count; i++) if (MyRooms[i].Id == snap.Id) MyRooms[i] = snap;
            if (snap.Id != CurrentId) return;
            // F4: a guest that already pressed OK agreed to the old stage / hazards: a host change clears its ready flag.
            bool changed = Current != null && (Current.Stage != snap.Stage || Current.HazardsOff != snap.HazardsOff);
            Current = snap;
            if (changed && !IsHost && LocalReady && !Launching)
            {
                LocalReady = false;
                _myStartNonce = "";
                _ = PushPlayer();
                SetStatus("The host changed the stage settings. Press OK again.", false);
            }
        }

        void RoomGone()
        {
            MyRooms.RemoveAll(r => r.Id == CurrentId);
            Current = null;
            CurrentId = "";
            if (Launching) _link?.Cancel();
            ResetSeat(true);
            SetStatus("The room was closed (the host left).", true);
        }

        static string Val(Dictionary<string, DataObject> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? v.Value : "";
        static string PVal(Dictionary<string, PlayerDataObject> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? v.Value : "";

        static ChibiRoomSnapshot ToSnapshot(Lobby l)
        {
            var raw = new List<ChibiRoomRawPlayer>();
            if (l.Players != null)
                foreach (var p in l.Players)
                    raw.Add(new ChibiRoomRawPlayer
                    {
                        Id = p?.Id, Name = PVal(p?.Data, PName), Class = PVal(p?.Data, PClass), Passive = PVal(p?.Data, PPassive),
                        Ready = PVal(p?.Data, PReady), Start = PVal(p?.Data, PStart), Ack = PVal(p?.Data, PAck),
                        Key = PVal(p?.Data, PKey),
                    });
            var snap = ChibiRoomRules.Snapshot(l.Id, l.Name, l.LobbyCode, l.HostId, Val(l.Data, KeyStage), Val(l.Data, KeyGo),
                                               Val(l.Data, KeyRelay), raw, Val(l.Data, KeyHazards));
            snap.Locked = l.IsLocked;
            return snap;
        }

        /// <summary>The guest's current start press (by PlayerId) counts as handled from now on: every press is used once.</summary>
        void RememberGuestPress(ChibiRoomSnapshot s)
        {
            ChibiRoomRules.PressKey(s, out _lastGuestId, out _lastGuestNonce);   // every guest's press (2-4 seats)
        }

        /// <summary>
        /// Host: start the fight (exactly 2 seated, both ready). Un-readies first, so a failed start never repeats by itself.
        /// SEC-001: records the guest's PlayerId and locks the Lobby (no new joins) before opening the Relay link, then
        /// re-checks that the same two players are still seated and the guest still ready before publishing go + the
        /// Relay code. The lock is lifted on abort and when the fight ends.
        /// </summary>
        async Task Launch()
        {
            if (Launching || Current == null || !ChibiRoomRules.CanStart(Current)) return;
            Launching = true;
            OwnsLink = true;
            _launchStarted = Time.unscaledTime;
            _goPublishedAt = float.MaxValue;
            RememberGuestPress(Current);   // every start press is used once
            _launchGuestId = ChibiRoomRules.GuestIds(Current);   // SEC-001: every seated guest, by PlayerId in seat order
            int guests = Current.Slots.Count - 1;
            LocalReady = false;
            SetStatus("Starting the fight...", false);
            await PushPlayer();
            bool locked = Launching && await UpdateLobbyData(new Dictionary<string, DataObject>(), "start", locked: true);
            if (!Launching) return;   // cancelled while we waited (AbortLaunch already cleaned up)
            if (!locked) { AbortLaunch(Status); return; }
            if (!ChibiRoomRules.LaunchStillValid(Current, MyId, _launchGuestId)) { AbortLaunch("A player left or joined."); return; }

            string relay = Launching && _link != null ? await _link.HostLinkAsync(guests) : null;
            if (!Launching) { _link?.Cancel(); return; }   // cancelled (timeout / room gone) while we waited
            if (relay == null) { AbortLaunch(_link != null ? _link.Status : "No connection."); return; }
            // SEC-001: nobody may have left, joined or moved seats while the link was opening.
            if (!ChibiRoomRules.LaunchStillValid(Current, MyId, _launchGuestId)) { AbortLaunch("A player left or joined."); return; }
            _publishedGo = ChibiRoomRules.NewNonce();
            bool ok = await UpdateLobbyData(new Dictionary<string, DataObject> { [KeyRelay] = Member(relay), [KeyGo] = Member(_publishedGo) }, "start");
            if (!Launching || _link == null || _link.State != ChibiRoomState.WaitingForOpponent) { if (Launching) AbortLaunch("The fight could not start."); return; }
            if (!ok) { AbortLaunch(Status); return; }
            _goPublishedAt = Time.unscaledTime;
            SetStatus("Fight starting! Waiting for your friend to connect...", false);
        }

        void AbortLaunch(string why)
        {
            _link?.Cancel();
            bool host = IsHost;
            ResetSeat(Synced);
            if (host) _ = UpdateLobbyData(new Dictionary<string, DataObject> { [KeyGo] = Member(""), [KeyRelay] = Member("") }, null, locked: false);
            _ = PushPlayer();
            SetStatus(why + " Both press OK and START FIGHT again.", true);
        }

        bool GuestLaunchAllowed()
        {
            float now = Time.unscaledTime;
            while (_guestLaunchTimes.Count > 0 && now - _guestLaunchTimes.Peek() > GuestLaunchWindowSeconds) _guestLaunchTimes.Dequeue();
            if (_guestLaunchTimes.Count >= MaxGuestLaunches) return false;
            _guestLaunchTimes.Enqueue(now);
            return true;
        }

        async Task Poll()
        {
            if (_polling || CurrentId.Length == 0) return;
            _polling = true;
            try
            {
                var lobby = await LobbyService.Instance.GetLobbyAsync(CurrentId);
                Apply(lobby);
                if (Current == null) return;
                if (!Synced)
                {
                    // First fresh read after entering: whatever launch / start is in there is history, not a request.
                    _baselineGo = Current.Go;
                    RememberGuestPress(Current);
                    Synced = true;
                    SetStatus("Choose your fighter and press OK.", false);
                    return;
                }
                if (!(ScreenOpen?.Invoke() ?? true)) return;   // act only while the player is looking at the room

                // SEC-001: a launch is for exactly the two players it began with; any seat change cancels it at once.
                if (IsHost && Launching && _launchGuestId.Length > 0 && !ChibiRoomRules.SameFightSeats(Current, MyId, _launchGuestId))
                {
                    AbortLaunch("A player left or joined.");
                }
                else if (IsHost && !Launching && ChibiRoomRules.HostShouldLaunch(Current, _lastGuestId, _lastGuestNonce))
                {
                    RememberGuestPress(Current);
                    if (GuestLaunchAllowed()) await Launch();
                    else SetStatus("Too many starts. Wait a minute, then press START FIGHT.", true);
                }
                else if (IsHost && Launching && ChibiRoomRules.GuestAcked(Current, _launchGuestId, _publishedGo))
                {
                    _goPublishedAt = float.MaxValue;   // the guest accepted this launch: wait for the link
                }
                else if (IsHost && !Launching && Current.Locked && _unlockSentFor != CurrentId)
                {
                    // A lock left by a launch that never ended cleanly (game closed mid-fight): open the room again.
                    _unlockSentFor = CurrentId;
                    await UpdateLobbyData(new Dictionary<string, DataObject>(), null, locked: false);
                }
                else if (!IsHost && !Launching && ChibiRoomRules.GuestShouldConnect(Current, _baselineGo, MyId))
                {
                    _baselineGo = Current.Go;   // each launch is answered once
                    if (LocalReady && _link != null)
                    {
                        // Acknowledge (ready off, start nonce spent) BEFORE connecting: no phantom ready if anything fails.
                        _myAck = Current.Go;
                        LocalReady = false;
                        _myStartNonce = "";
                        await PushPlayer();
                        Launching = true;
                        OwnsLink = true;
                        _guestJoining = true;
                        _launchStarted = Time.unscaledTime;
                        SetStatus("Fight starting! Connecting...", false);
                        if (!_link.JoinLink(Current?.RelayCode)) AbortLaunch(_link.Status.Length > 0 ? _link.Status : "Could not connect.");
                    }
                }
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyNotFound) { RoomGone(); }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.RateLimited) { _pollAt = Time.unscaledTime + 6f; }
            catch (Exception e) { Debug.LogWarning("ChibiClash room poll: " + NetworkRoomManager.Explain(e)); }
            finally { _polling = false; }
        }

        void Update()
        {
            float now = Time.unscaledTime;
            bool allowed = OnlineAllowed == null || OnlineAllowed();

            // Keep every room we host alive (also during a fight, so the room is still there afterwards).
            if (allowed && now >= _heartbeatAt)
            {
                _heartbeatAt = now + HeartbeatSeconds;
                string me = MyId;
                foreach (var r in MyRooms)
                    if (r.HostId == me && me.Length > 0) _ = Heartbeat(r.Id);
            }

            bool inMatch = InMatch?.Invoke() ?? false;
            bool screen = ScreenOpen?.Invoke() ?? true;

            if (Launching && !inMatch)
            {
                var st = _link != null ? _link.State : ChibiRoomState.Failed;
                if (st == ChibiRoomState.Connected) Launching = false;   // the match took over
                else if (_guestJoining && st == ChibiRoomState.Failed) AbortLaunch(_link.Status);
                else if (IsHost && now - _goPublishedAt > AckTimeoutSeconds) AbortLaunch("Your friend did not answer.");
                else if (IsHost && Current != null && !screen) AbortLaunch("Start cancelled.");
                else if (now - _launchStarted > LaunchTimeoutSeconds) AbortLaunch("Your friend did not connect.");
            }

            // Left the room screen while ready: withdraw OK so nothing starts behind the player's back.
            if (LocalReady && !screen && !inMatch && !Launching) _ = LeaveCurrentSeat();

            if (allowed && CurrentId.Length > 0 && !inMatch && now >= _pollAt)
            {
                _pollAt = now + PollSeconds;
                _ = Poll();
            }
        }

        static async Task Heartbeat(string id)
        {
            try { await LobbyService.Instance.SendHeartbeatPingAsync(id); }
            catch (Exception) { /* closed: the next poll / refresh notices */ }
        }

        void SetStatus(string s, bool error)
        {
            Status = s ?? "";
            StatusIsError = error;
        }
    }
}
