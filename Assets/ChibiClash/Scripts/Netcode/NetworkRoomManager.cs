using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>Where the room flow is (drives the online menu).</summary>
    public enum ChibiRoomState : byte
    {
        Idle, SigningIn, CreatingRoom, WaitingForOpponent, Joining, Searching, Connecting, Connected, Failed,
    }

    /// <summary>
    /// Online rooms for Chibi Clash (docs/architecture/netcode/online-rooms.md): Unity Authentication (anonymous player id),
    /// Unity Lobby (private room code, matchmaking) and Unity Relay (connects the two players without exposing IPs).
    /// The output is a connected <see cref="ChibiUtpTransport"/>; the game then runs <see cref="ChibiOnlineMatch"/> on it
    /// (handshake, rollback, results). No game server: the host's machine is player 0 and the config authority.
    ///
    /// Flows
    ///   Private room (decision Q2): host allocates Relay (1 peer), creates a PRIVATE 2-player lobby holding the Relay join
    ///   code (member-only) and shows the LOBBY code; the friend joins the lobby by that code, reads the Relay code, joins
    ///   the allocation and connects. Matchmaking: quick-join a public lobby whose protocol tag (S1 index) matches this
    ///   build; if none, host a public one and keep retrying quick-join every few seconds so two searching players
    ///   always meet. Once the UDP link is up the host deletes the lobby (nobody else can join). Lobby heartbeat every 15 s.
    ///
    /// Main thread only. Requires the project to be linked to a Unity Cloud project with Relay and Lobby enabled.
    /// </summary>
    /// <example><code>
    /// var rooms = gameObject.AddComponent&lt;NetworkRoomManager&gt;();
    /// rooms.OnConnected += (transport, isHost) =&gt; StartOnlineMatch(transport, isHost);
    /// rooms.CreatePrivateRoom();       // shows rooms.RoomCode
    /// // friend: rooms.JoinPrivateRoom("ABC123");   or both: rooms.QuickMatch();
    /// </code></example>
    public sealed class NetworkRoomManager : MonoBehaviour
    {
        /// <summary>Lobby heartbeat interval; lobbies without a heartbeat for 30 s go inactive.</summary>
        const float HeartbeatSeconds = 15f;
        /// <summary>While hosting a public matchmaking lobby, try to quick-join someone else's this often (two hosts meet).</summary>
        const float RetryQuickJoinSeconds = 6f;
        /// <summary>Give up waiting for the UDP link after Relay join (seconds).</summary>
        const float ConnectTimeoutSeconds = 15f;
        const string KeyRelay = "relay", KeyProto = "proto", KeyCountry = "cc", KeySubregion = "sub", KeyContinent = "cont";
        /// <summary>Public lobbies read per matchmaking search (oldest first); enough to find the closest waiting player.</summary>
        const int SearchPageSize = 50;
        /// <summary>Candidates tried per search before hosting / waiting again (Lobby allows only a few joins per player per few seconds).</summary>
        const int JoinAttemptsPerSearch = 2;
        /// <summary>Quick match: give up on an opponent's Relay link after this long and search again (it may have left).</summary>
        const float QuickConnectTimeoutSeconds = 8f;
        /// <summary>Quick match: how many times a vanished opponent sends us back into the search before we give up.</summary>
        const int MaxRequeues = 5;
        /// <summary>Among equally close waiting players, pick randomly from this many so searchers do not all pile on one lobby.</summary>
        const int SpreadAcross = 3;
        /// <summary>When the Lobby service says "too many requests" (peak traffic), wait this long (+ random jitter) before searching again.</summary>
        const float RateLimitBackoffSeconds = 15f;
        /// <summary>
        /// Relay connection type: "dtls" (encrypted UDP) on PC; "wss" (secure WebSockets over TCP, Relay ports
        /// 37000-37100) in the browser build, where UDP is not available. Must match the driver ChibiUtpTransport creates
        /// for the platform. Web and PC players can share a match: Unity Transport's cross-play doc
        /// (com.unity.transport Documentation~/cross-play.md) says Relay connects a DTLS host and a WebSocket browser client
        /// automatically, so both platforms stay in one matchmaking pool (network review 2026-11-22).
        /// </summary>
        public const string RelayConnectionType =
#if UNITY_WEBGL && !UNITY_EDITOR
            "wss";
#else
            "dtls";
#endif

        /// <summary>Matchmaking tag: only builds with the same wire protocol and rules version meet.</summary>
        public static string ProtocolTag => "p" + ChibiPacketCodec.ProtocolVersion + "r" + ChibiMatchConfig.RulesVersion;
        /// <summary>
        /// Quick match lobbies (VS ONLINE): Battle Royale for 2-4 players. The play style is part of the tag
        /// (four-player-design.md 3), so modes and builds never mix in one search.
        /// </summary>
        public static string QuickTag => ProtocolTag + "-R";
        /// <summary>Quick match seats (host + up to 3 guests, one Relay allocation).</summary>
        public const int QuickSeats = ChibiSimState.MaxFighters;
        /// <summary>Quick match: once a first opponent is connected, wait this long for more (start at once when full).</summary>
        public const float QuickFillSeconds = 20f;

        /// <summary>Current step of the flow.</summary>
        public ChibiRoomState State { get; private set; } = ChibiRoomState.Idle;
        /// <summary>Short human-readable status for the menu.</summary>
        public string Status { get; private set; } = "";
        /// <summary>The private room's Lobby code to share (host of a private room only).</summary>
        public string RoomCode { get; private set; } = "";
        /// <summary>
        /// Name of the private room we host or joined ("Kira's Dojo"), safe to draw; "" outside private rooms. The joiner
        /// reads it from the Lobby (another player's text, sanitised by ChibiSocialRules.SafeRoomName).
        /// </summary>
        public string RoomName { get; private set; } = "";
        /// <summary>The anonymous player id once signed in.</summary>
        /// <remarks>Guarded on the services state: AuthenticationService.Instance throws before UnityServices is initialized.</remarks>
        public string PlayerId => Unity.Services.Core.UnityServices.State == Unity.Services.Core.ServicesInitializationState.Initialized &&
                                  AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn
            ? AuthenticationService.Instance.PlayerId : "";

        /// <summary>Raised once when the UDP link to the opponent is up. The receiver owns the transport from then on.</summary>
        public event Action<ChibiUtpTransport, bool> OnConnected;
        /// <summary>
        /// Host of a star (quick match, or a room of 2-4 seats): raised once when the seats are filled. Arguments: the
        /// transport (owned by the receiver), the link per guest slot ([0] null), and the player count (2..4).
        /// </summary>
        public event Action<ChibiUtpTransport, IChibiTransport[], int> OnGroupConnected;

        /// <summary>Host of a star: guests the room expects (rooms), 0 = fill up to 3 (quick match).</summary>
        int _expectGuests;
        float _firstGuestAt = -1f;
        /// <summary>Host of a star: guests connected right now (for the waiting text).</summary>
        public int ConnectedGuests => _transport != null && _isHost ? _transport.ConnectedPeers : 0;

        ChibiUtpTransport _transport;
        bool _isHost, _quickMode;
        Lobby _lobby;
        float _heartbeatAt, _retryAt, _connectDeadline;
        int _flow;   // incremented by Cancel / a new flow so stale async continuations stop
        string _myCountry = "";   // matchmaking: this device's country code ("" = unknown -> worldwide)
        bool _searching;          // a search is in flight (never two at once: a second join would orphan a lobby)
        int _requeues;            // quick match: opponents that vanished during connect

        /// <summary>
        /// Set by the game: false (e.g. a player under 13) makes every online flow refuse to start. Checked here as well
        /// as in the menu so no screen or side path can bypass it.
        /// </summary>
        public Func<bool> OnlineAllowed = () => true;
        /// <summary>
        /// Name gate (set by the game): claims our player name worldwide before any online flow (quick match, private
        /// rooms, room links). Null = OK, else the reason to show. Checked in <c>SignIn</c>, the step every flow passes.
        /// </summary>
        public Func<Task<string>> NameGate = () => Task.FromResult<string>(null);
        /// <summary>Shown when <see cref="OnlineAllowed"/> refuses.</summary>
        public static string NotAllowedMessage => Loc.T("online.locked_age");

        bool Gate()
        {
            if (OnlineAllowed == null || OnlineAllowed()) return true;
            Fail(NotAllowedMessage);
            return false;
        }
        float _searchStart;       // matchmaking: when QUICK MATCH was pressed (the search widens with waiting time)

        /// <summary>
        /// Matchmaking: where the search currently looks ("your country and South-eastern Asia", "Asia", "the whole world").
        /// Empty outside QUICK MATCH.
        /// </summary>
        public string SearchArea => _quickMode && Busy ? AreaLabel(CurrentMaxTier()) : "";

        bool Busy => State != ChibiRoomState.Idle && State != ChibiRoomState.Failed && State != ChibiRoomState.Connected;

        // ================================================================== public flows

        /// <summary>Host a private room; on success <see cref="RoomCode"/> holds the Lobby code to share.</summary>
        public void CreatePrivateRoom(string roomName = null)
        {
            if (Busy || !Gate()) return;
            string name = (roomName ?? "").Trim();
            if (name.Length == 0) name = ChibiSocialRules.FallbackRoomName;
            string err = ChibiSocialRules.ValidateRoomName(name);
            if (err != null) { Fail(err); return; }
            _ = RunHost(isPrivate: true, ++_flow, name);
        }

        /// <summary>Join a friend's private room by its Lobby code (decision Q2).</summary>
        public void JoinPrivateRoom(string lobbyCode)
        {
            if (Busy || !Gate()) return;
            if (string.IsNullOrWhiteSpace(lobbyCode)) { Fail(Loc.T("net.enter_code")); return; }
            lobbyCode = ChibiSocialRules.NormalizeCode(lobbyCode);
            if (lobbyCode == null) { Fail(Loc.T("net.bad_code")); return; }
            _ = RunJoinByCode(lobbyCode, ++_flow);
        }

        /// <summary>Find any opponent on the same build (quick-join, else host and keep looking).</summary>
        public void QuickMatch() { if (!Busy && Gate()) _ = RunQuickMatch(++_flow); }

        /// <summary>Default UDP port for LAN direct play.</summary>
        public const ushort LanPort = 7777;

        /// <summary>
        /// Host a LAN match without any Unity service (same Wi-Fi / network, or a forwarded port): listen on
        /// <paramref name="port"/> and wait for the friend to join by this machine's IP (shown in <see cref="Status"/>).
        /// Same transport, handshake and rollback as Relay play; only the Relay hop is skipped.
        /// </summary>
        public void HostLan(ushort port = LanPort)
        {
            if (Busy || !Gate()) return;
            if (LanUnavailable()) return;
            ++_flow;
            _quickMode = false;
            _isHost = true;
            _transport = ChibiUtpTransport.HostDirect(port);
            if (_transport.LinkState == ChibiLinkState.Lost) { Fail(Loc.T("net.udp_port", port)); return; }
            RoomCode = "";
            RoomName = "";
            State = ChibiRoomState.WaitingForOpponent;
            Status = Loc.T("net.lan_ready", LocalIpv4(), port);
            _connectDeadline = float.MaxValue;
        }

        /// <summary>
        /// True (and the flow fails with a clear message) in the browser build: LAN is direct UDP by IP, which browsers
        /// do not allow. Online play there goes through Relay (rooms / matchmaking) instead.
        /// </summary>
        bool LanUnavailable()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Fail(Loc.T("net.lan_browser"));
            return true;
#else
            return false;
#endif
        }

        /// <summary>Join a LAN host by IP address (see <see cref="HostLan"/>).</summary>
        public void JoinLan(string address, ushort port = LanPort)
        {
            if (Busy || !Gate()) return;
            if (LanUnavailable()) return;
            ++_flow;
            address = (address ?? "").Trim();
            if (address.Length == 0) { Fail(Loc.T("net.enter_ip")); return; }
            _quickMode = false;
            _isHost = false;
            _transport = ChibiUtpTransport.JoinDirect(address, port);
            if (_transport.LinkState == ChibiLinkState.Lost) { Fail(Loc.T("net.bad_ip", address)); return; }
            State = ChibiRoomState.Connecting;
            Status = Loc.T("net.connecting_to", address);
            _connectDeadline = Time.unscaledTime + ConnectTimeoutSeconds;
        }

        /// <summary>This machine's first non-loopback IPv4 address (for the LAN host to read out), or "this PC's IP".</summary>
        public static string LocalIpv4()
        {
            try
            {
                foreach (var a in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
                        return a.ToString();
            }
            catch (Exception) { /* no network info: fall through */ }
            return Loc.T("net.this_pc_ip");
        }

        /// <summary>Stop whatever is in progress, leave / delete the lobby and close the link.</summary>
        public void Cancel()
        {
            _flow++;
            _ = LeaveLobby(_lobby, _isHost);
            _lobby = null;
            _transport?.Dispose();
            _transport = null;
            RoomCode = "";
            RoomName = "";
            State = ChibiRoomState.Idle;
            Status = "";
        }

        // ================================================================== sign-in

        async Task<bool> SignIn(int flow)
        {
            State = ChibiRoomState.SigningIn;
            Status = Loc.T("net.signing_in");
            string error = await EnsureSignedInAsync() ?? await NameGate();
            if (flow != _flow) return false;
            if (error != null) { Fail(error); return false; }
            return true;
        }

        static Task<string> _signIn;

        /// <summary>
        /// The project uses Enter Play Mode Options without a domain reload (EditorSettings), so statics survive between
        /// Play sessions while Unity Services are shut down at exit. Forget the cached sign-in at each Play start.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _signIn = null;
            ChibiRegions.ResetCapturedCountry();   // captured again in UseInvariantCulture
        }

        /// <summary>
        /// The OS culture seen when this process first started ("th-TH"), before the switch below. Deliberately NOT reset
        /// per Play session: without a domain reload the thread culture is already invariant when the next session starts,
        /// so this is the only place the real culture survives. It describes the machine, not a session, and never changes.
        /// </summary>
        static System.Globalization.CultureInfo _osCulture;
        /// <summary>The country captured under <see cref="_osCulture"/> (same lifetime and reason: kept across Play sessions).</summary>
        static string _osCountry;

        /// <summary>
        /// Online services need a neutral culture for formatting. On a PC whose Windows region uses the Thai Buddhist
        /// calendar (dates like 22/11/2569), the PC player's secure WebSocket to Unity's "Wire" service failed every TLS
        /// handshake (2026-11-22, player log), while the editor, which runs invariant, was fine. Text in any language still
        /// displays normally; only number and date formatting becomes culture-neutral, which the game already assumes.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void UseInvariantCulture()
        {
            // Regional matchmaking reads the device country from the culture: capture it BEFORE the switch (review 2026-11-22).
            var current = System.Globalization.CultureInfo.CurrentCulture;
            bool osCultureLive = !string.IsNullOrEmpty(current.Name);   // the invariant culture has Name ""
            if (_osCulture == null && osCultureLive) _osCulture = current;
            // Live OS culture: the region setting wins. Already invariant (a later Play session without a domain reload):
            // the region API would describe the invariant culture (Mono may answer "US"), so use the saved culture's name.
            if (osCultureLive)
            {
                ChibiRegions.CaptureDeviceCountry(current.Name);
                if (_osCountry == null) _osCountry = ChibiRegions.DeviceCountry();
            }
            else ChibiRegions.CaptureDeviceCountry(_osCountry ?? "", _osCulture != null ? _osCulture.Name : "");
            var c = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = c;
            System.Globalization.CultureInfo.CurrentCulture = c;
        }

        /// <summary>
        /// Initialise Unity Services and sign in anonymously once per run (shared by rooms and friends; concurrent callers
        /// await the same attempt). Returns null on success, else a player-facing reason. A cached success is only trusted
        /// while the services really are initialised and signed in: otherwise the player id would be empty and every
        /// Lobby call would fail with "400 Bad Request" (found 2026-11-22 after a Play Mode restart).
        /// </summary>
        public static Task<string> EnsureSignedInAsync()
        {
            if (string.IsNullOrEmpty(Application.cloudProjectId)) return Task.FromResult(NotLinkedMessage);
            if (Application.internetReachability == NetworkReachability.NotReachable) return Task.FromResult(NoInternetMessage);
            bool stale = _signIn != null && _signIn.IsCompleted &&
                         (_signIn.Result != null || UnityServices.State != ServicesInitializationState.Initialized ||
                          !AuthenticationService.Instance.IsSignedIn);
            if (_signIn == null || stale) _signIn = SignInOnce();
            return _signIn;
        }

        static async Task<string> SignInOnce()
        {
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized) await UnityServices.InitializeAsync();
                ChibiDevFlags.ApplyAuthProfile();   // dev only: one player per window (-authProfile); inert in release
                if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();
                return null;
            }
            catch (Exception e)
            {
                return Loc.T("net.sign_in_failed", Explain(e));
            }
        }

        // ================================================================== host

        async Task RunHost(bool isPrivate, int flow, string roomName = null)
        {
            _quickMode = !isPrivate;
            if (isPrivate) _myCountry = "";
            if (!await SignIn(flow)) return;
            State = ChibiRoomState.CreatingRoom;
            Status = isPrivate ? Loc.T("net.creating_private") : Loc.T("net.hosting_one");
            try
            {
                // Quick match is a star of up to 4 (one allocation, the same join code for every guest); a private room
                // made here is the 1v1 room by code.
                int guests = isPrivate ? 1 : QuickSeats - 1;
                Allocation alloc = await RelayService.Instance.CreateAllocationAsync(guests);
                string relayCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
                if (flow != _flow) return;
                _transport = ChibiUtpTransport.HostRelay(alloc.ToRelayServerData(RelayConnectionType), guests);
                _isHost = true;
                _expectGuests = 0;
                _firstGuestAt = -1f;

                var options = new CreateLobbyOptions
                {
                    IsPrivate = isPrivate,
                    Data = new Dictionary<string, DataObject>
                    {
                        [KeyRelay] = new DataObject(DataObject.VisibilityOptions.Member, relayCode),
                        [KeyProto] = new DataObject(DataObject.VisibilityOptions.Public, isPrivate ? ProtocolTag : QuickTag, DataObject.IndexOptions.S1),
                    },
                };
                // Matchmaking lobbies show only a 2-letter country code (from the device region setting) so the closest
                // players find each other. Private rooms share nothing.
                if (!isPrivate && _myCountry.Length > 0)
                {
                    // Indexed so searchers ask the SERVER for exactly their radius (country / sub-region / continent).
                    options.Data[KeyCountry] = new DataObject(DataObject.VisibilityOptions.Public, _myCountry, DataObject.IndexOptions.S2);
                    options.Data[KeySubregion] = new DataObject(DataObject.VisibilityOptions.Public, ChibiRegions.Subregion(_myCountry), DataObject.IndexOptions.S3);
                    options.Data[KeyContinent] = new DataObject(DataObject.VisibilityOptions.Public, ChibiRegions.Continent(_myCountry), DataObject.IndexOptions.S4);
                }
                // A private room carries the host's chosen name (seen only by members: private lobbies are never listed).
                string lobbyName = isPrivate ? ChibiSocialRules.SafeRoomName(roomName) : "Chibi Clash " + QuickTag;
                var lobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, isPrivate ? 2 : QuickSeats, options);
                if (flow != _flow) { _ = LeaveLobby(lobby, true); return; }
                _lobby = lobby;
                RoomCode = isPrivate ? lobby.LobbyCode : "";
                RoomName = isPrivate ? lobbyName : "";
                State = ChibiRoomState.WaitingForOpponent;
                Status = isPrivate ? Loc.T("net.room_ready", RoomName)
                                  : Loc.T("net.searching_in", SearchArea);
                _heartbeatAt = Time.unscaledTime + HeartbeatSeconds;
                _retryAt = Mathf.Max(_retryAt, Time.unscaledTime + RetryQuickJoinSeconds);   // keep a rate-limit back-off
            }
            catch (Exception e)
            {
                if (flow == _flow) Fail(Loc.T("net.create_failed", Explain(e)));
            }
        }

        // ================================================================== room links (ChibiRoomService)

        /// <summary>
        /// Rooms: open the HOST end of the match link (a fresh Relay allocation, no lobby; the room's own lobby holds the
        /// code). Returns the Relay join code to publish to the guest, or null on failure (see <see cref="Status"/>).
        /// The connected link is handed over through <see cref="OnConnected"/> exactly like every other mode.
        /// </summary>
        /// <param name="guests">Seated guests the fight is for (1..3). The link is handed over once all of them connected:
        /// 1 through <see cref="OnConnected"/> (the 1v1 path), 2-3 through <see cref="OnGroupConnected"/>.</param>
        public async Task<string> HostLinkAsync(int guests = 1)
        {
            if (Busy || !Gate()) return null;
            if (guests < 1 || guests > ChibiUtpTransport.MaxGuests) { Fail(Loc.T("net.needs_2_to_4")); return null; }
            int flow = ++_flow;
            _quickMode = false;
            RoomCode = "";
            if (!await SignIn(flow)) return null;
            State = ChibiRoomState.CreatingRoom;
            Status = Loc.T("net.starting_fight");
            try
            {
                Allocation alloc = await RelayService.Instance.CreateAllocationAsync(guests);
                string relayCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
                if (flow != _flow) return null;
                _transport = ChibiUtpTransport.HostRelay(alloc.ToRelayServerData(RelayConnectionType), guests);
                _isHost = true;
                _expectGuests = guests;
                _lobby = null;
                State = ChibiRoomState.WaitingForOpponent;
                Status = guests == 1 ? Loc.T("net.waiting_friend") : Loc.T("net.waiting_friends");
                return relayCode;
            }
            catch (Exception e)
            {
                if (flow == _flow) Fail(Loc.T("net.start_failed", Explain(e)));
                return null;
            }
        }

        /// <summary>
        /// Rooms: open the GUEST end of the match link with the host's Relay join code (from the room lobby).
        /// Returns false at once when it cannot start (busy, age gate, bad code) so the caller never waits for nothing.
        /// </summary>
        public bool JoinLink(string relayCode)
        {
            if (Busy) { Status = Loc.T("net.connection_running"); return false; }
            if (!Gate()) return false;
            if (!ChibiRoomRules.ValidRelayCode(relayCode)) { Fail(Loc.T("net.no_valid_info")); return false; }
            _quickMode = false;
            _ = RunJoinLink(relayCode, ++_flow);
            return true;
        }

        async Task RunJoinLink(string relayCode, int flow)
        {
            if (!await SignIn(flow)) return;
            State = ChibiRoomState.Connecting;
            Status = Loc.T("net.connecting_host");
            try
            {
                JoinAllocation join = await RelayService.Instance.JoinAllocationAsync(relayCode);
                if (flow != _flow) return;
                _lobby = null;
                _isHost = false;
                _transport = ChibiUtpTransport.JoinRelay(join.ToRelayServerData(RelayConnectionType));
                _connectDeadline = Time.unscaledTime + ConnectTimeoutSeconds;
            }
            catch (Exception e)
            {
                if (flow == _flow) Fail(Loc.T("net.host_unreachable", Explain(e)));
            }
        }

        // ================================================================== joiner

        async Task RunJoinByCode(string code, int flow)
        {
            _quickMode = false;
            if (!await SignIn(flow)) return;
            State = ChibiRoomState.Joining;
            Status = Loc.T("net.joining_room", code);
            try
            {
                var lobby = await LobbyService.Instance.JoinLobbyByCodeAsync(code);
                if (flow != _flow) { _ = LeaveLobby(lobby, false); return; }
                // Version gate at join time (network review 2026-11-10): refuse a room from another build at once.
                string roomTag = lobby?.Data != null && lobby.Data.TryGetValue(KeyProto, out var proto) ? proto?.Value : null;
                if (!ChibiRoomRules.SameBuild(roomTag, ProtocolTag))
                {
                    _ = LeaveLobby(lobby, false);
                    Fail(Loc.T("net.version_mismatch"));
                    return;
                }
                RoomName = ChibiSocialRules.SafeRoomName(lobby?.Name);   // the host's text: sanitised
                await ConnectAsJoiner(lobby, flow);
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyNotFound || e.Reason == LobbyExceptionReason.InvalidJoinCode)
            {
                if (flow == _flow) Fail(Loc.T("net.no_room_code", code));
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyFull)
            {
                if (flow == _flow) Fail(Loc.T("net.already_full"));
            }
            catch (Exception e)
            {
                if (flow == _flow) Fail(Loc.T("net.join_failed", Explain(e)));
            }
        }

        async Task RunQuickMatch(int flow, bool fresh = true)
        {
            _quickMode = true;
            RoomName = "";
            if (fresh)
            {
                _myCountry = ChibiRegions.DeviceCountry();
                _searchStart = Time.unscaledTime;
                _requeues = 0;
                _retryAt = 0f;
            }
            if (!await SignIn(flow)) return;
            State = ChibiRoomState.Searching;
            Status = Loc.T("net.searching_in", SearchArea);
            var found = await Search(flow, null);
            if (flow != _flow) { if (found.Lobby != null) _ = LeaveLobby(found.Lobby, false); return; }
            if (found.Lobby != null) { await ConnectAsJoiner(found.Lobby, flow); return; }
            await RunHost(isPrivate: false, flow);   // nobody close enough waits (or we are throttled): let them find us
        }

        int CurrentMaxTier() => ChibiRegions.MaxTierAfter(Time.unscaledTime - _searchStart, _myCountry.Length > 0);

        /// <summary>A ChibiRegions continent / subregion name ("Eastern Asia") in the current language (key region.eastern_asia).</summary>
        static string RegionText(string english) =>
            string.IsNullOrEmpty(english) ? "" : Loc.T("region." + english.ToLowerInvariant().Replace(' ', '_').Replace('-', '_'));

        string AreaLabel(int maxTier)
        {
            if (_myCountry.Length == 0 || maxTier >= ChibiRegions.TierWorld) return Loc.T("net.area_world");
            if (maxTier <= ChibiRegions.TierSubregion) return Loc.T("net.area_country_and", RegionText(ChibiRegions.SubregionName(_myCountry)));
            return RegionText(ChibiRegions.Continent(_myCountry));
        }

        struct SearchResult
        {
            public Lobby Lobby;
            public bool Throttled;
        }

        /// <summary>
        /// One regional search (never two at once). <paramref name="mine"/> is our own waiting lobby when we host: two
        /// waiting hosts would otherwise join each other at the same moment, so only the host with the higher lobby id
        /// moves; the other stays and is joined.
        /// </summary>
        async Task<SearchResult> Search(int flow, Lobby mine)
        {
            if (_searching) return default;
            _searching = true;
            try { return await SearchOnce(flow, mine); }
            finally { _searching = false; }
        }

        /// <summary>
        /// ONE Lobby query per search, for exactly the current radius, filtered by the SERVER through indexes (so it
        /// scales with the number of waiting players): sub-region (S3) at first, then continent (S4), then the whole
        /// world. Results come oldest-waiting first; ChibiRegions ranks them (same country first) and we try up to
        /// <see cref="JoinAttemptsPerSearch"/>, picking randomly among the equally close front-runners to spread load.
        /// </summary>
        async Task<SearchResult> SearchOnce(int flow, Lobby mine)
        {
            int maxTier = CurrentMaxTier();
            List<Lobby> found;
            try
            {
                var filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT),
                    new QueryFilter(QueryFilter.FieldOptions.S1, QuickTag, QueryFilter.OpOptions.EQ),
                };
                if (_myCountry.Length > 0 && maxTier <= ChibiRegions.TierSubregion)
                    filters.Add(new QueryFilter(QueryFilter.FieldOptions.S3, ChibiRegions.Subregion(_myCountry), QueryFilter.OpOptions.EQ));
                else if (_myCountry.Length > 0 && maxTier <= ChibiRegions.TierContinent)
                    filters.Add(new QueryFilter(QueryFilter.FieldOptions.S4, ChibiRegions.Continent(_myCountry), QueryFilter.OpOptions.EQ));
                var query = new QueryLobbiesOptions
                {
                    Count = SearchPageSize,
                    Filters = filters,
                    Order = new List<QueryOrder> { new QueryOrder(true, QueryOrder.FieldOptions.Created) },   // longest wait first
                };
                found = (await LobbyService.Instance.QueryLobbiesAsync(query))?.Results;
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.RateLimited)
            {
                BackOff();
                return new SearchResult { Throttled = true };
            }
            catch (Exception e)
            {
                if (flow == _flow) Debug.LogWarning("ChibiClash matchmaking search: " + Explain(e));
                return default;
            }
            if (flow != _flow || found == null || found.Count == 0) return default;

            var lobbies = new List<Lobby>(found.Count);
            var countries = new List<string>(found.Count);
            foreach (var l in found)
            {
                if (l == null || string.IsNullOrEmpty(l.Id)) continue;
                if (mine != null && string.CompareOrdinal(l.Id, mine.Id) >= 0) continue;   // ourselves, or the host who moves to us
                lobbies.Add(l);
                // Untrusted: only the country code is used, and ChibiRegions treats anything unknown as "far away".
                countries.Add(l.Data != null && l.Data.TryGetValue(KeyCountry, out var cc) ? cc?.Value : "");
            }

            var order = ChibiRegions.RankCandidates(_myCountry, countries, maxTier);
            if (order.Count > 1)
            {
                int best = ChibiRegions.Tier(_myCountry, countries[order[0]]), same = 1;
                while (same < order.Count && same < SpreadAcross && ChibiRegions.Tier(_myCountry, countries[order[same]]) == best) same++;
                int pick = UnityEngine.Random.Range(0, same);
                (order[0], order[pick]) = (order[pick], order[0]);
            }
            for (int k = 0; k < order.Count && k < JoinAttemptsPerSearch; k++)
            {
                try
                {
                    var joined = await LobbyService.Instance.JoinLobbyByIdAsync(lobbies[order[k]].Id);
                    if (flow != _flow) { _ = LeaveLobby(joined, false); return default; }
                    return new SearchResult { Lobby = joined };
                }
                catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyFull || e.Reason == LobbyExceptionReason.LobbyNotFound)
                {
                    // Someone else got there first: try the next closest.
                }
                catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.RateLimited)
                {
                    BackOff();
                    return new SearchResult { Throttled = true };
                }
                catch (Exception e)
                {
                    if (flow == _flow) Debug.LogWarning("ChibiClash matchmaking join: " + Explain(e));
                    return default;
                }
                if (flow != _flow) return default;
            }
            return default;
        }

        /// <summary>Service is throttling us: stop searching for a while (jittered so many clients do not retry in lock-step).</summary>
        void BackOff()
        {
            _retryAt = Mathf.Max(_retryAt, Time.unscaledTime + RateLimitBackoffSeconds + UnityEngine.Random.Range(0f, 5f));
        }

        async Task ConnectAsJoiner(Lobby lobby, int flow)
        {
            DataObject relay = null;
            if (lobby?.Data == null || !lobby.Data.TryGetValue(KeyRelay, out relay) || string.IsNullOrEmpty(relay?.Value))
            {
                _ = LeaveLobby(lobby, false);
                RetryOrFail(Loc.T("net.no_info"));
                return;
            }
            State = ChibiRoomState.Connecting;
            Status = RoomName.Length > 0 ? Loc.T("net.joined_connecting", RoomName) : Loc.T("quick.connecting");
            JoinAllocation join;
            try { join = await RelayService.Instance.JoinAllocationAsync(relay.Value); }
            catch (Exception e)
            {
                if (flow != _flow) return;
                _ = LeaveLobby(lobby, false);
                RetryOrFail(Loc.T("net.opponent_unreachable", Explain(e)));
                return;
            }
            if (flow != _flow) { _ = LeaveLobby(lobby, false); return; }
            _lobby = lobby;
            _isHost = false;
            _transport = ChibiUtpTransport.JoinRelay(join.ToRelayServerData(RelayConnectionType));
            _connectDeadline = Time.unscaledTime + (_quickMode ? QuickConnectTimeoutSeconds : ConnectTimeoutSeconds);
        }

        /// <summary>
        /// Quick match: the opponent vanished while connecting (left, crashed, or switched lobbies) -> back into the search
        /// with the radius already reached. Rooms by code and LAN fail with the reason instead.
        /// </summary>
        void RetryOrFail(string message)
        {
            if (!_quickMode || _requeues >= MaxRequeues) { Fail(message); return; }
            _requeues++;
            _transport?.Dispose();
            _transport = null;
            _ = LeaveLobby(_lobby, _isHost);
            _lobby = null;
            State = ChibiRoomState.Searching;
            Status = Loc.T("net.player_left_searching");
            _ = RunQuickMatch(++_flow, fresh: false);
        }

        // ================================================================== per frame: pump the link, heartbeat, hand off

        void Update()
        {
            if (_transport == null || State == ChibiRoomState.Connected) return;
            _transport.Pump();
            _transport.Flush();

            if (_isHost && _transport.MaxPeers > 1)
            {
                UpdateStarHost();
                return;
            }

            if (_transport.LinkState == ChibiLinkState.Connected)
            {
                // Hand the link to the match. The host removes the lobby so no third player can find it.
                var t = _transport;
                bool host = _isHost;
                _transport = null;
                _ = LeaveLobby(_lobby, host);
                _lobby = null;
                State = ChibiRoomState.Connected;
                Status = Loc.T("net.connected");
                OnConnected?.Invoke(t, host);
                return;
            }
            if (_transport.LinkState == ChibiLinkState.Lost) { RetryOrFail(Loc.T("net.relay_failed")); return; }

            if (_isHost && _lobby != null)
            {
                if (Time.unscaledTime >= _heartbeatAt)
                {
                    _heartbeatAt = Time.unscaledTime + HeartbeatSeconds;
                    _ = Heartbeat(_lobby.Id);
                }
                if (_quickMode) Status = Loc.T("net.searching_in", SearchArea);
                if (_quickMode && !_searching && Time.unscaledTime >= _retryAt)
                {
                    _retryAt = Time.unscaledTime + RetryQuickJoinSeconds + UnityEngine.Random.Range(0f, 2f);
                    _ = SwitchToJoinerIfSomeoneIsWaiting(_flow);
                }
            }
            else if (!_isHost && Time.unscaledTime > _connectDeadline) RetryOrFail(Loc.T("net.opponent_timeout"));
        }

        /// <summary>
        /// Host of a star (quick match up to 4, or a room of 3-4): hand the guests to the match when the seats are filled.
        /// Quick match starts when 3 guests are connected, or with 1-2 once <see cref="QuickFillSeconds"/> passed since the
        /// first one arrived. A room starts when every seated guest is connected (the room service times the launch out).
        /// </summary>
        void UpdateStarHost()
        {
            if (_transport.LinkState == ChibiLinkState.Lost) { Fail(Loc.T("net.relay_failed")); return; }
            int n = _transport.ConnectedPeers;
            float now = Time.unscaledTime;
            bool start;
            if (_expectGuests > 0) start = n >= _expectGuests;
            else
            {
                if (n == 0) _firstGuestAt = -1f;
                else if (_firstGuestAt < 0f) _firstGuestAt = now;
                start = n >= _transport.MaxPeers || (n >= 1 && now - _firstGuestAt >= QuickFillSeconds);
                Status = n == 0 ? Loc.T("net.searching_in", SearchArea)
                                : Loc.T("net.quick_filling", n + 1, QuickSeats, Mathf.CeilToInt(QuickFillSeconds - (now - _firstGuestAt)));
            }
            if (start) { HandOffStar(); return; }

            if (_lobby != null)
            {
                if (now >= _heartbeatAt)
                {
                    _heartbeatAt = now + HeartbeatSeconds;
                    _ = Heartbeat(_lobby.Id);
                }
                // Two waiting hosts would never meet: the one with nobody connected may move to the other's lobby.
                if (_quickMode && n == 0 && !_searching && now >= _retryAt)
                {
                    _retryAt = now + RetryQuickJoinSeconds + UnityEngine.Random.Range(0f, 2f);
                    _ = SwitchToJoinerIfSomeoneIsWaiting(_flow);
                }
            }
        }

        /// <summary>Seal the transport (no stranger can take a seat later), drop half-connected guests, close the lobby.</summary>
        void HandOffStar()
        {
            var t = _transport;
            t.Sealed = true;
            var guests = new IChibiTransport[ChibiSimState.MaxFighters];
            int players = 1;
            for (int i = 0; i < t.MaxPeers; i++)
            {
                if (t.PeerState(i) == ChibiLinkState.Connected) guests[players++] = t.Peer(i);
                else t.DropPeer(i);
            }
            _transport = null;
            _ = LeaveLobby(_lobby, true);
            _lobby = null;
            State = ChibiRoomState.Connected;
            Status = Loc.T("net.connected");
            OnGroupConnected?.Invoke(t, guests, players);
        }

        /// <summary>
        /// Two players who both hosted a public lobby would wait forever; the one who finds the other's lobby joins it.
        /// Never abandons a player who already joined OUR lobby (their Relay link takes a few seconds to come up).
        /// </summary>
        async Task SwitchToJoinerIfSomeoneIsWaiting(int flow)
        {
            var mine = _lobby;
            var other = (await Search(flow, mine)).Lobby;
            if (other == null) return;
            if (flow != _flow || mine == null || other.Id == mine.Id) { _ = LeaveLobby(other, false); return; }

            bool someoneJoinedUs = _transport != null && _transport.LinkState == ChibiLinkState.Connected;
            if (!someoneJoinedUs)
            {
                try
                {
                    var fresh = await LobbyService.Instance.GetLobbyAsync(mine.Id);
                    someoneJoinedUs = fresh?.Players != null && fresh.Players.Count > 1;
                }
                catch (Exception) { someoneJoinedUs = true; }   // cannot tell: stay host (the safe side)
            }
            if (flow != _flow || someoneJoinedUs || (_transport != null && _transport.LinkState == ChibiLinkState.Connected))
            {
                _ = LeaveLobby(other, false);
                return;
            }
            _ = LeaveLobby(mine, true);
            _transport?.Dispose();
            _transport = null;
            _lobby = null;
            await ConnectAsJoiner(other, flow);
        }

        async Task Heartbeat(string lobbyId)
        {
            try { await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId); }
            catch (Exception e) { Debug.LogWarning("ChibiClash lobby heartbeat: " + Explain(e)); }
        }

        static async Task LeaveLobby(Lobby lobby, bool isHost)
        {
            if (lobby == null) return;
            try
            {
                if (isHost) await LobbyService.Instance.DeleteLobbyAsync(lobby.Id);
                else if (AuthenticationService.Instance.IsSignedIn)
                    await LobbyService.Instance.RemovePlayerAsync(lobby.Id, AuthenticationService.Instance.PlayerId);
            }
            catch (Exception) { /* lobby already gone: nothing to clean up */ }
        }

        void Fail(string message)
        {
            _transport?.Dispose();
            _transport = null;
            _ = LeaveLobby(_lobby, _isHost);
            _lobby = null;
            RoomCode = "";
            RoomName = "";
            State = ChibiRoomState.Failed;
            Status = message;
        }

        /// <summary>
        /// Shown when the device has no internet. Rooms, invites and quick match work from ANY place over the internet
        /// (Wi-Fi or mobile data, different cities or the same room): Unity Relay connects the two players.
        /// </summary>
        public static string NoInternetMessage =>
            Loc.T("net.no_internet");

        /// <summary>Shown when the build has no Unity Cloud project id (Relay / Lobby cannot work without one).</summary>
        public const string NotLinkedMessage =
            "Online is not set up in this build: link the project to a Unity Cloud project (Edit > Project Settings > " +
            "Services), then turn on Relay, Lobby and Friends for it in the Unity Dashboard. (LAN play works without this.)";

        /// <summary>Player-facing reason: the innermost exception's message, with the one setup step this project needs called out.</summary>
        public static string Explain(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            if (e is AggregateException ae && ae.InnerExceptions.Count > 0) e = ae.InnerExceptions[0];
            string m = e.Message ?? e.GetType().Name;
            if (m.IndexOf("project", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (m.IndexOf("link", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("cloud", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 m.IndexOf("id", StringComparison.OrdinalIgnoreCase) >= 0))
                return "this build is not linked to a Unity Cloud project (Edit > Project Settings > Services). " + m;
            return m;
        }

        void OnDestroy() => Cancel();
    }
}
