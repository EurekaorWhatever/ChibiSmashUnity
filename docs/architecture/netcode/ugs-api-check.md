# UGS + Unity Transport API check (Chibi Clash, Unity 6000.6.7f1)

Verified by grep over Library/PackageCache (paths relative to it). Resolved versions (packages-lock.json):
services.multiplayer 2.4.1, services.core 1.18.0, services.authentication 3.8.0, services.qos 1.5.1, services.wire 1.6.0,
transport 6.6.0, collections 6.6.0, burst 2.0.0, mathematics 1.4.0. NO Netcode for GameObjects / Netcode for Entities installed.
Public surface file: com.unity.services.multiplayer@cf2613f41536/Runtime/Unity.Services.Multiplayer.api (line numbers below = that file unless noted).

## Core / Auth
| API | Namespace / type | Signature | File |
|---|---|---|---|
| Init | Unity.Services.Core.UnityServices | `public static Task InitializeAsync()` / `InitializeAsync(InitializationOptions options)` | services.core@3464cb68d709/Runtime/Core/UnityServices.cs:93,120 |
| Auth instance | Unity.Services.Authentication.AuthenticationService | `public static IAuthenticationService Instance` | services.authentication@6613d5ec6ae8/Runtime/AuthenticationService.cs:20 |
| Sign in | IAuthenticationService | `Task SignInAnonymouslyAsync(SignInOptions options = null)` | .../Runtime/IAuthenticationService.cs:158 |
| State | IAuthenticationService | `bool IsSignedIn { get; }`, `string PlayerId { get; }` | .../IAuthenticationService.cs:71,92 |

## Relay (namespace Unity.Services.Relay; NOT obsolete)
| API | Signature | Where |
|---|---|---|
| `RelayService.Instance` | `public static IRelayService Instance { get; }` | .api:1829 (src Runtime/Relay/SDK/RelayService.cs) |
| Create | `Task<Models.Allocation> CreateAllocationAsync(int maxConnections, string region = default)` | .api:1757 |
| Join code | `Task<string> GetJoinCodeAsync(System.Guid allocationId)` | .api:1758 |
| Join | `Task<Models.JoinAllocation> JoinAllocationAsync(string joinCode)` | .api:1759 |
| Regions | `Task<List<Models.Region>> ListRegionsAsync()` | .api:1760 |

RelayServerData (type lives in Transport: Unity.Networking.Transport.Relay.RelayServerData):
- Preferred: extension `AllocationUtils.ToRelayServerData(this Allocation|JoinAllocation, string connectionType)` and
  overload taking `Unity.Services.Multiplayer.RelayProtocol`. **Namespace is Unity.Services.Relay.Models** (need that using).
  Source: services.multiplayer/Runtime/Relay/Models/AllocationUtils.cs:45,65,86,125. Valid strings: "udp", "dtls", "wss"; else throws ArgumentException.
- `enum RelayProtocol : byte { Unknown=0, UDP=1, DTLS=2, Default=2, WSS=3 }` (.api:1467, namespace Unity.Services.Multiplayer).
- Manual ctors still exist: `RelayServerData(string host, ushort port, byte[] allocationId, byte[] connectionData, byte[] hostConnectionData, byte[] key, bool isSecure, bool isWebSocket)` (+7-arg no-websocket overload, + ref-struct overloads). transport@1b9b1e9062a8/Runtime/Relay/RelayServerData.cs:90,102,116,140.
- Old `Unity.Services.Relay.Models.RelayServerEndpoint` helper + 2022-era sample helper `RelayUtilities` are not needed.

## Lobby (namespace Unity.Services.Lobbies; models in Unity.Services.Lobbies.Models; NOT obsolete)
`LobbyService.Instance` -> `ILobbyService` (.api:228; src Runtime/Lobbies/SDK/LobbyService.cs). ILobbyService (.api ~84-107):
- `Task<Lobby> CreateLobbyAsync(string lobbyName, int maxPlayers, CreateLobbyOptions options = default)`
- `Task<Lobby> CreateOrJoinLobbyAsync(string lobbyId, string lobbyName, int maxPlayers, CreateLobbyOptions options = default)`
- `Task<Lobby> JoinLobbyByCodeAsync(string lobbyCode, JoinLobbyByCodeOptions options = default)`; `JoinLobbyByIdAsync(string, JoinLobbyByIdOptions = default)`
- `Task<Lobby> QuickJoinLobbyAsync(QuickJoinLobbyOptions options = default)`
- `Task<Lobby> GetLobbyAsync(string lobbyId)` and `GetLobbyAsync(string lobbyId, string ifNoneMatchVersion)`
- `Task SendHeartbeatPingAsync(string lobbyId)`; `Task<Lobby> UpdateLobbyAsync(string lobbyId, UpdateLobbyOptions options)`
- `Task<Lobby> UpdatePlayerAsync(string lobbyId, string playerId, UpdatePlayerOptions options)`
- `Task RemovePlayerAsync(string lobbyId, string playerId)`; `Task DeleteLobbyAsync(string lobbyId)`
- `Task<ILobbyEvents> SubscribeToLobbyEventsAsync(string lobbyId, LobbyEventCallbacks callbacks)`; `ReconnectToLobbyAsync`, `QueryLobbiesAsync`, `GetJoinedLobbiesAsync`
- `bool ConcurrencyControlEnabled { get; set; }` (new)
- `CreateLobbyOptions { Dictionary<string, DataObject> Data; bool? IsLocked; bool? IsPrivate; string Password; Player Player; }` (.api:53-61). IsPrivate is `bool?`.

## Obsolete attributes in the whole multiplayer public API (only these 6)
- `ISession.PlayerLeft`: "PlayerLeft has been deprecated. Use PlayerLeaving instead (UnityUpgradable) -> PlayerLeaving" (.api:1226)
- `RelayNetworkOptions.Protocol`: "This value was moved to the SessionOptions.WithNetworkOptions() API." (.api:1459)
- `RelayNetworkOptions(RelayProtocol)`, `(RelayProtocol, string region)`, `(RelayProtocol, string, bool)`: "The RelayProtocol value should be se[t] through SessionOptions.WithNetworkOptions() API." (.api:1461-1464)
Use `new RelayNetworkOptions(string region = default, bool preserveRegion = false)` + `WithNetworkOptions(new NetworkOptions { RelayProtocol = RelayProtocol.DTLS })`.

## Multiplayer Sessions (namespace Unity.Services.Multiplayer)
`MultiplayerService.Instance` -> `IMultiplayerService` (.api:1331, 1162-1190):
- `Task<IHostSession> CreateSessionAsync(SessionOptions)` (+CancellationToken)
- `Task<ISession> JoinSessionByCodeAsync(string sessionCode[, JoinSessionOptions[, CancellationToken]])`; `JoinSessionByIdAsync(...)`
- `Task<ISession> MatchmakeSessionAsync(QuickJoinOptions, SessionOptions)` and `(MatchmakerOptions, SessionOptions)` (+CT)
- `CreateOrJoinSessionAsync(string sessionId, SessionOptions)`, `QuerySessionsAsync(QuerySessionsOptions)`, `ReconnectToSessionAsync(...)`
- `SessionOptions { bool IsLocked; bool IsPrivate; int MaxPlayers; string Name; string Password; Dictionary<string, SessionProperty> SessionProperties; }` (.api:1563)
- Extensions (.api:1574-1586): `WithRelayNetwork<T>(string region = default)`, `WithRelayNetwork<T>(RelayNetworkOptions)`, `WithDirectNetwork<T>(...)`, `WithNetworkOptions<T>(NetworkOptions)`, `WithNetworkHandler<T>(INetworkHandler)`, `WithPlayerName<T>(...)`, `WithHostMigration<T>(...)`
- Sessions auto-heartbeat the backing lobby: `LobbySettings.HeartbeatSeconds => 10` (Runtime/Multiplayer/Lobby/LobbySettings.cs:10).

**Critical for this project (no NGO/N4E installed):** the default network handler builder throws
`SessionException("Netcode for GameObjects package is not installed", SessionError.MissingAssembly)` (or the Entities equivalent)
(Runtime/Multiplayer/Modules/Network/Handlers/NetworkHandler/NetworkBuilder.cs:19-36). With raw UTP you MUST pass
`.WithNetworkHandler(myHandler)` implementing:
```
public interface INetworkHandler {
  Task StartAsync(NetworkConfiguration configuration);
  Task StartAsync(NetworkConfiguration configuration, CancellationToken cancellationToken);
  Task StopAsync();
}
```
`NetworkConfiguration` gives `RelayServerData RelayServerData`, `RelayServerData RelayClientData`, `NetworkRole Role` (Client/Server/Host), `NetworkType Type`, `DirectNetworkListenAddress/PublishAddress`, `UpdatePublishPort(ushort)` (.api:1334-1343). The SDK does the Relay alloc/join-code/join itself (NetworkModule.cs:288-321) and hands both host-side server data and a host-as-client data to the handler.

## Unity Transport 6.6.0 (namespace Unity.Networking.Transport; src transport@1b9b1e9062a8/Runtime/NetworkDriver.cs)
- `static NetworkDriver Create(NetworkSettings settings)` :461; `Create()` :474; `Create<N>(N iface[, NetworkSettings])` :482-500
- `ref NetworkSettings WithRelayParameters(ref this NetworkSettings settings, ref RelayServerData serverData, int relayConnectionTimeMS = default 3000)` (Relay/RelayNetworkParameter.cs:21) -> `serverData` must be a local variable (ref). Doc: keep-alive must be < 10 s or the relay severs the connection.
- `int Bind(NetworkEndpoint)` :829; `int Listen()` :870; `NetworkConnection Accept()` :940
- `NetworkConnection Connect(NetworkEndpoint endpoint[, NativeArray<byte> payload])` :1053,1061; NEW hostname overloads `Connect(FixedString512Bytes address, ushort port[, NativeArray<byte> payload])` :1106,1114. For Relay, connect to `relayServerData.Endpoint` (UNVERIFIED property name beyond `Endpoint` field assigned in RelayServerData.cs:92).
- `int BeginSend(NetworkPipeline pipe, NetworkConnection connection, out DataStreamWriter writer, int requiredPayloadSize = 0)` :1434; `BeginSend(NetworkConnection, out DataStreamWriter, int = 0)` :1448 (uses NetworkPipeline.Null)
- `int EndSend(DataStreamWriter writer)` :1464; `void AbortSend(DataStreamWriter writer)` :1471
- `NetworkEvent.Type PopEvent(out NetworkConnection, out DataStreamReader[, out NetworkPipeline])` :1492,1514
- `NetworkEvent.Type PopEventForConnection(NetworkConnection, out DataStreamReader[, out NetworkPipeline])` :1568,1590
- `JobHandle ScheduleUpdate(JobHandle dependency = default)` :654; `ScheduleFlushSend(JobHandle = default)` :685
- `NetworkPipeline CreatePipeline(params Type[] stages)` :756; `CreatePipeline(NativeArray<NetworkPipelineStageId>)` :789
- `int Disconnect(NetworkConnection)` :1275; `NetworkConnection.State GetConnectionState(NetworkConnection)` :1328; `bool IsCreated` :581; `bool Listening` :438
- Default unreliable pipeline = `NetworkPipeline.Null` (`public static NetworkPipeline Null => default;` NetworkPipeline.cs:307). Stages: UnreliableSequencedPipelineStage, ReliableSequencedPipelineStage, FragmentationPipelineStage.
- Max payload: `NetworkParameterConstants.MaxMessageSize = 1400` (and `MTU = 1400`, same) (NetworkParams.cs:82,86); usable payload is that minus `NetworkDriver.MaxHeaderSize(pipe)` (Relay/DTLS add overhead). Larger needs FragmentationPipelineStage.

## DataStreamReader/Writer — namespace Unity.Collections (com.unity.collections@4920e8897b5a 6.6.0)
- Writer: `bool WriteBytes(NativeArray<byte> value)` :307, `bool WriteBytes(Span<byte> value)` :317; `bool HasFailedWrites` :198; `int Length` :221
- Reader: `void ReadBytes(NativeArray<byte> array)` :153, `void ReadBytes(Span<byte> span)` :164; `bool HasFailedReads` :106; `int GetBytesRead()` :176
- No `byte[]` overloads seen. (ReadOnlySpan overload for WriteBytes: UNVERIFIED / not found in grep.)

## Rate limits
Not in package source. Lobby/Relay/Matchmaker API clients map HTTP 429 (Lobbies/Apis/LobbyApi.cs); MatchmakerExceptionReason.RateLimited = 21429 links docs.unity.com/matchmaker/Content/rate-limits.htm. Concrete Lobby/Relay numbers: UNVERIFIED (check UGS docs).

## NAT punch-through / P2P
Not in package source. General knowledge (UNVERIFIED here): Relay always relays all traffic through the Relay server; it does not do NAT punch-through or upgrade to direct P2P. "Peer-to-peer" in the CHANGELOG means client-hosted (host player) topology over Relay. `WithDirectNetwork(listenIp, publishIp, port)` is direct IP with no NAT traversal (LAN / port-forwarded only).
