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
        const string KeyRelay = "relay", KeyProto = "proto";
        /// <summary>Relay connection type: DTLS = encrypted UDP.</summary>
        const string RelayConnectionType = "dtls";

        /// <summary>Matchmaking tag: only builds with the same wire protocol and rules version meet.</summary>
        public static string ProtocolTag => "p" + ChibiPacketCodec.ProtocolVersion + "r" + ChibiMatchConfig.RulesVersion;

        /// <summary>Current step of the flow.</summary>
        public ChibiRoomState State { get; private set; } = ChibiRoomState.Idle;
        /// <summary>Short human-readable status for the menu.</summary>
        public string Status { get; private set; } = "";
        /// <summary>The private room's Lobby code to share (host of a private room only).</summary>
        public string RoomCode { get; private set; } = "";
        /// <summary>The anonymous player id once signed in.</summary>
        public string PlayerId => AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn ? AuthenticationService.Instance.PlayerId : "";

        /// <summary>Raised once when the UDP link to the opponent is up. The receiver owns the transport from then on.</summary>
        public event Action<ChibiUtpTransport, bool> OnConnected;

        ChibiUtpTransport _transport;
        bool _isHost, _quickMode;
        Lobby _lobby;
        float _heartbeatAt, _retryAt, _connectDeadline;
        int _flow;   // incremented by Cancel / a new flow so stale async continuations stop

        bool Busy => State != ChibiRoomState.Idle && State != ChibiRoomState.Failed && State != ChibiRoomState.Connected;

        // ================================================================== public flows

        /// <summary>Host a private room; on success <see cref="RoomCode"/> holds the Lobby code to share.</summary>
        public void CreatePrivateRoom() { if (!Busy) _ = RunHost(isPrivate: true, ++_flow); }

        /// <summary>Join a friend's private room by its Lobby code (decision Q2).</summary>
        public void JoinPrivateRoom(string lobbyCode)
        {
            if (Busy) return;
            lobbyCode = (lobbyCode ?? "").Trim().ToUpperInvariant();
            if (lobbyCode.Length == 0) { Fail("Enter the room code your friend shows."); return; }
            _ = RunJoinByCode(lobbyCode, ++_flow);
        }

        /// <summary>Find any opponent on the same build (quick-join, else host and keep looking).</summary>
        public void QuickMatch() { if (!Busy) _ = RunQuickMatch(++_flow); }

        /// <summary>Default UDP port for LAN direct play.</summary>
        public const ushort LanPort = 7777;

        /// <summary>
        /// Host a LAN match without any Unity service (same Wi-Fi / network, or a forwarded port): listen on
        /// <paramref name="port"/> and wait for the friend to join by this machine's IP (shown in <see cref="Status"/>).
        /// Same transport, handshake and rollback as Relay play; only the Relay hop is skipped.
        /// </summary>
        public void HostLan(ushort port = LanPort)
        {
            if (Busy) return;
            ++_flow;
            _quickMode = false;
            _isHost = true;
            _transport = ChibiUtpTransport.HostDirect(port);
            if (_transport.LinkState == ChibiLinkState.Lost) { Fail("Could not open UDP port " + port + " (already in use?)."); return; }
            RoomCode = "";
            State = ChibiRoomState.WaitingForOpponent;
            Status = "LAN host ready. Your friend joins: " + LocalIpv4() + "  (port " + port + ")";
            _connectDeadline = float.MaxValue;
        }

        /// <summary>Join a LAN host by IP address (see <see cref="HostLan"/>).</summary>
        public void JoinLan(string address, ushort port = LanPort)
        {
            if (Busy) return;
            ++_flow;
            address = (address ?? "").Trim();
            if (address.Length == 0) { Fail("Enter the host's IP address."); return; }
            _quickMode = false;
            _isHost = false;
            _transport = ChibiUtpTransport.JoinDirect(address, port);
            if (_transport.LinkState == ChibiLinkState.Lost) { Fail("\"" + address + "\" is not a valid IP address."); return; }
            State = ChibiRoomState.Connecting;
            Status = "Connecting to " + address + "...";
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
            return "this PC's IP";
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
            State = ChibiRoomState.Idle;
            Status = "";
        }

        // ================================================================== sign-in

        async Task<bool> SignIn(int flow)
        {
            State = ChibiRoomState.SigningIn;
            Status = "Signing in...";
            if (string.IsNullOrEmpty(Application.cloudProjectId))
            {
                Fail(NotLinkedMessage);
                return false;
            }
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized) await UnityServices.InitializeAsync();
                if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();
                return flow == _flow;
            }
            catch (Exception e)
            {
                if (flow == _flow) Fail("Sign-in failed: " + Explain(e));
                return false;
            }
        }

        // ================================================================== host

        async Task RunHost(bool isPrivate, int flow)
        {
            _quickMode = !isPrivate;
            if (!await SignIn(flow)) return;
            State = ChibiRoomState.CreatingRoom;
            Status = isPrivate ? "Creating a private room..." : "No open match found, hosting one...";
            try
            {
                Allocation alloc = await RelayService.Instance.CreateAllocationAsync(1);
                string relayCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
                if (flow != _flow) return;
                _transport = ChibiUtpTransport.HostRelay(alloc.ToRelayServerData(RelayConnectionType));
                _isHost = true;

                var options = new CreateLobbyOptions
                {
                    IsPrivate = isPrivate,
                    Data = new Dictionary<string, DataObject>
                    {
                        [KeyRelay] = new DataObject(DataObject.VisibilityOptions.Member, relayCode),
                        [KeyProto] = new DataObject(DataObject.VisibilityOptions.Public, ProtocolTag, DataObject.IndexOptions.S1),
                    },
                };
                var lobby = await LobbyService.Instance.CreateLobbyAsync("Chibi Clash " + ProtocolTag, 2, options);
                if (flow != _flow) { _ = LeaveLobby(lobby, true); return; }
                _lobby = lobby;
                RoomCode = isPrivate ? lobby.LobbyCode : "";
                State = ChibiRoomState.WaitingForOpponent;
                Status = isPrivate ? "Room ready. Share the code with your friend." : "Searching for an opponent...";
                _heartbeatAt = Time.unscaledTime + HeartbeatSeconds;
                _retryAt = Time.unscaledTime + RetryQuickJoinSeconds;
            }
            catch (Exception e)
            {
                if (flow == _flow) Fail("Could not create the room: " + Explain(e));
            }
        }

        // ================================================================== joiner

        async Task RunJoinByCode(string code, int flow)
        {
            _quickMode = false;
            if (!await SignIn(flow)) return;
            State = ChibiRoomState.Joining;
            Status = "Joining room " + code + "...";
            try
            {
                var lobby = await LobbyService.Instance.JoinLobbyByCodeAsync(code);
                if (flow != _flow) { _ = LeaveLobby(lobby, false); return; }
                await ConnectAsJoiner(lobby, flow);
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyNotFound || e.Reason == LobbyExceptionReason.InvalidJoinCode)
            {
                if (flow == _flow) Fail("No room with code " + code + ". Check the code and try again.");
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyFull)
            {
                if (flow == _flow) Fail("That room is already full.");
            }
            catch (Exception e)
            {
                if (flow == _flow) Fail("Could not join: " + Explain(e));
            }
        }

        async Task RunQuickMatch(int flow)
        {
            if (!await SignIn(flow)) return;
            State = ChibiRoomState.Searching;
            Status = "Searching for an opponent...";
            var lobby = await TryQuickJoin(flow);
            if (flow != _flow) return;
            if (lobby != null) { await ConnectAsJoiner(lobby, flow); return; }
            await RunHost(isPrivate: false, flow);
        }

        /// <summary>Quick-join a public lobby of this build with a free slot, or null when none exists.</summary>
        async Task<Lobby> TryQuickJoin(int flow)
        {
            try
            {
                var options = new QuickJoinLobbyOptions
                {
                    Filter = new List<QueryFilter>
                    {
                        new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT),
                        new QueryFilter(QueryFilter.FieldOptions.S1, ProtocolTag, QueryFilter.OpOptions.EQ),
                    },
                };
                return await LobbyService.Instance.QuickJoinLobbyAsync(options);
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.NoOpenLobbies)
            {
                return null;
            }
            catch (Exception e)
            {
                if (flow == _flow) Debug.LogWarning("ChibiClash quick join: " + Explain(e));
                return null;
            }
        }

        async Task ConnectAsJoiner(Lobby lobby, int flow)
        {
            if (!lobby.Data.TryGetValue(KeyRelay, out var relay) || string.IsNullOrEmpty(relay.Value))
            {
                _ = LeaveLobby(lobby, false);
                Fail("The room has no connection info (host left?).");
                return;
            }
            State = ChibiRoomState.Connecting;
            Status = "Connecting to your opponent...";
            JoinAllocation join = await RelayService.Instance.JoinAllocationAsync(relay.Value);
            if (flow != _flow) { _ = LeaveLobby(lobby, false); return; }
            _lobby = lobby;
            _isHost = false;
            _transport = ChibiUtpTransport.JoinRelay(join.ToRelayServerData(RelayConnectionType));
            _connectDeadline = Time.unscaledTime + ConnectTimeoutSeconds;
        }

        // ================================================================== per frame: pump the link, heartbeat, hand off

        void Update()
        {
            if (_transport == null || State == ChibiRoomState.Connected) return;
            _transport.Pump();
            _transport.Flush();

            if (_transport.LinkState == ChibiLinkState.Connected)
            {
                // Hand the link to the match. The host removes the lobby so no third player can find it.
                var t = _transport;
                bool host = _isHost;
                _transport = null;
                _ = LeaveLobby(_lobby, host);
                _lobby = null;
                State = ChibiRoomState.Connected;
                Status = "Connected!";
                OnConnected?.Invoke(t, host);
                return;
            }
            if (_transport.LinkState == ChibiLinkState.Lost) { Fail("The connection to the Relay server failed."); return; }

            if (_isHost && _lobby != null)
            {
                if (Time.unscaledTime >= _heartbeatAt)
                {
                    _heartbeatAt = Time.unscaledTime + HeartbeatSeconds;
                    _ = Heartbeat(_lobby.Id);
                }
                if (_quickMode && Time.unscaledTime >= _retryAt)
                {
                    _retryAt = Time.unscaledTime + RetryQuickJoinSeconds + UnityEngine.Random.Range(0f, 2f);
                    _ = SwitchToJoinerIfSomeoneIsWaiting(_flow);
                }
            }
            else if (!_isHost && Time.unscaledTime > _connectDeadline) Fail("Could not reach your opponent (timed out).");
        }

        /// <summary>Two players who both hosted a public lobby would wait forever; the one who finds the other's lobby joins it.</summary>
        async Task SwitchToJoinerIfSomeoneIsWaiting(int flow)
        {
            var mine = _lobby;
            var other = await TryQuickJoin(flow);
            if (flow != _flow || other == null) return;
            if (mine != null && other.Id == mine.Id) return;
            if (_transport != null && _transport.LinkState == ChibiLinkState.Connected)
            {
                _ = LeaveLobby(other, false);   // someone connected to us meanwhile: stay host
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
            State = ChibiRoomState.Failed;
            Status = message;
        }

        /// <summary>Shown when the build has no Unity Cloud project id (Relay / Lobby cannot work without one).</summary>
        public const string NotLinkedMessage =
            "Online is not set up in this build: link the project to a Unity Cloud project (Edit > Project Settings > " +
            "Services), then turn on Relay and Lobby for it in the Unity Dashboard.";

        /// <summary>Player-facing reason: the innermost exception's message, with the one setup step this project needs called out.</summary>
        static string Explain(Exception e)
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
