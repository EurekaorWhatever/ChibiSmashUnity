using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// The links of one team-play match once every pair of peers is connected: <see cref="Links"/>[slot] is the Relay link
    /// bound to that roster slot (null at <see cref="MySlot"/>). Hand them to <see cref="ChibiOnlineMatch.MeshHost"/> /
    /// <see cref="ChibiOnlineMatch.MeshJoin"/> together with <see cref="PlayerHashes"/> and <see cref="PairKeys"/>.
    /// </summary>
    public sealed class ChibiMeshLinks
    {
        public int Format, Count, MySlot;
        public IChibiTransport[] Links;
        public string[] PlayerIds;
        public uint[] PlayerHashes;
        /// <summary>Review N1: per-pair HMAC keys from the X25519 exchange (null at <see cref="MySlot"/>).</summary>
        public byte[][] PairKeys;
        public bool IsHost => MySlot == 0;
    }

    /// <summary>
    /// Quick match for 2v2 and FFA (protocol 6, full-mesh rollback over Relay). Separate queue per format (lobby S1 =
    /// <see cref="ChibiRoomRules.QueueTag"/>), lobby seats from <see cref="ChibiRoomRules.SeatsFor"/>.
    ///
    /// Flow (every decision is a pure function in <see cref="ChibiRoomRules"/>, unit-tested there)
    ///   1. A fresh X25519 key pair per queue; the public key goes into our player data (<see cref="ChibiRoomRules.KeyPublicKey"/>).
    ///   2. Search our queue and try at most <see cref="ChibiRoomRules.JoinAttemptsPerSearch"/> lobbies; if none worked
    ///      create one. A rate-limited call backs off (<see cref="ChibiRoomRules.RateLimitBackoffSeconds"/>) and searches
    ///      again instead of creating. A guest whose lobby does not start within <see cref="ChibiRoomRules.QueueRequeueSeconds"/>
    ///      leaves and searches again.
    ///   3. The host is whoever <c>lobby.HostId</c> names (review B2: re-derived on every poll, so a migrated host takes
    ///      over). It locks the lobby when <see cref="ChibiRoomRules.ShouldStart"/> says so (full, or FFA with 3 after 45 s
    ///      as host; a host that waits <see cref="ChibiRoomRules.HostQueueTimeoutSeconds"/> gives up with a message) and
    ///      writes the roster: player ids in join order. Everyone (host included, after a re-read) checks the
    ///      roster against the lobby's players (<see cref="ChibiRoomRules.VerifyRoster"/>, review S2).
    ///   4. Pair keys from the roster's public keys (<see cref="ChibiRoomRules.DerivePairKeys"/>); no keys, no match.
    ///   5. Relay mesh: of each pair the lower slot allocates and publishes the join code (member-only player data); the
    ///      higher slot joins. When every link is Connected, <see cref="OnMeshReady"/> hands them over.
    /// All state of one attempt lives in a per-flow <see cref="Attempt"/>; fields are only touched after a flow check, so a
    /// cancelled flow can never leak into the next one (review: per-flow locals).
    /// </summary>
    public sealed class ChibiTeamMatchmaker : MonoBehaviour
    {
        const float PollSeconds = 1.5f;
        const float HeartbeatSeconds = 15f;
        const float MeshConnectTimeoutSeconds = 20f;
        const float HostDeleteDelaySeconds = 10f;
        const string KeyProto = "proto", KeyRoster = "roster";
        const string RelayConnectionType = "dtls";

        /// <summary>Raised once per match when every link of the mesh is connected.</summary>
        public event Action<ChibiMeshLinks> OnMeshReady;
        /// <summary>Raised when the flow gives up (message for the player).</summary>
        public event Action<string> OnFailed;

        public string Status { get; private set; } = "";
        public bool Busy { get; private set; }
        public int Format { get; private set; }

        /// <summary>Everything one queue attempt owns.</summary>
        sealed class Attempt
        {
            public int Flow;
            public string MyId = "";
            public Lobby Lobby;
            public bool IsHost;
            public byte[] Private;
            public ChibiUtpTransport[] Links;
            public int Allocations;        // Relay allocations we created (they expire on their own if never used)
            public bool HandedOff;
        }

        int _flow;
        Attempt _current;

        /// <summary>Queue for <paramref name="format"/> (<see cref="ChibiMatchConfig.Format2v2"/> or <see cref="ChibiMatchConfig.FormatFfa"/>).</summary>
        public void QuickMatch(int format)
        {
            if (Busy) return;
            if (format != ChibiMatchConfig.Format2v2 && format != ChibiMatchConfig.FormatFfa)
                throw new ArgumentOutOfRangeException(nameof(format), "1v1 uses NetworkRoomManager.QuickMatch");
            Format = format;
            Busy = true;
            var a = new Attempt { Flow = ++_flow };
            _current = a;
            _ = Run(a);
        }

        /// <summary>Leave the queue / abandon a half-built mesh.</summary>
        public void Cancel()
        {
            _flow++;
            if (_current != null) Cleanup(_current);
            _current = null;
            Busy = false;
            Status = "";
        }

        bool Live(Attempt a) => a.Flow == _flow;

        void Cleanup(Attempt a)
        {
            if (a.HandedOff) return;
            if (a.Links != null) foreach (var l in a.Links) l?.Dispose();
            a.Links = null;
            if (a.Allocations > 0)
                Debug.Log("ChibiClash team queue: abandoned " + a.Allocations + " Relay allocation(s); they expire unused.");
            if (a.Private != null) Array.Clear(a.Private, 0, a.Private.Length);
            _ = LeaveLobby(a, 0f);
            a.Lobby = null;
        }

        void Fail(Attempt a, string message)
        {
            if (!Live(a)) return;
            Cleanup(a);
            _current = null;
            Busy = false;
            Status = message;
            OnFailed?.Invoke(message);
        }

        static bool RateLimited(Exception e) =>
            e is LobbyServiceException l && l.Reason == LobbyExceptionReason.RateLimited;

        async Task Run(Attempt a)
        {
            int rateLimits = 0;
            try
            {
                string myId = await NetworkRoomManager.EnsureSignedInAsync();
                if (!Live(a)) return;
                a.MyId = myId;
                a.Private = ChibiX25519.NewPrivateKey();
                string pub = Convert.ToBase64String(ChibiX25519.PublicKey(a.Private));
                string tag = ChibiRoomRules.QueueTag(NetworkRoomManager.ProtocolTag, Format);
                int seats = ChibiRoomRules.SeatsFor(Format);

                while (Live(a))
                {
                    // ---- 1. find or create
                    Status = "Searching for players...";
                    Lobby lobby;
                    try { lobby = await FindOrCreate(a, tag, seats, pub); rateLimits = 0; }
                    catch (Exception e) when (RateLimited(e))
                    {
                        float wait = ChibiRoomRules.RateLimitBackoffSeconds(++rateLimits);
                        Status = "Matchmaking is busy, retrying in " + (int)wait + " s...";
                        await Task.Delay((int)(wait * 1000));
                        continue;
                    }
                    if (!Live(a)) { _ = LeaveLobby(new Attempt { Lobby = lobby, MyId = myId, IsHost = lobby != null && ChibiRoomRules.IsHost(lobby.HostId, myId) }, 0f); return; }
                    a.Lobby = lobby;
                    a.IsHost = ChibiRoomRules.IsHost(lobby.HostId, myId);

                    // ---- 2. wait for the roster (host migration and requeue handled per poll)
                    float joinedAt = Time.unscaledTime, hostSince = a.IsHost ? Time.unscaledTime : -1f, heartbeatAt = 0f;
                    string[] roster = null;
                    bool requeue = false;
                    while (roster == null)
                    {
                        await Task.Delay((int)(PollSeconds * 1000));
                        if (!Live(a)) return;
                        try { lobby = await LobbyService.Instance.GetLobbyAsync(lobby.Id); rateLimits = 0; }
                        catch (Exception e) when (RateLimited(e))
                        {
                            await Task.Delay((int)(ChibiRoomRules.RateLimitBackoffSeconds(++rateLimits) * 1000));
                            continue;
                        }
                        if (!Live(a)) return;
                        a.Lobby = lobby;
                        bool host = ChibiRoomRules.IsHost(lobby.HostId, myId);
                        if (host && !a.IsHost) hostSince = Time.unscaledTime;   // review B2: the host left, we took over
                        a.IsHost = host;
                        int n = lobby.Players?.Count ?? 0;
                        Status = "Waiting for players (" + n + "/" + seats + ")...";

                        if (a.IsHost)
                        {
                            if (Time.unscaledTime >= heartbeatAt)
                            {
                                heartbeatAt = Time.unscaledTime + HeartbeatSeconds;
                                await Heartbeat(lobby.Id);   // catching helper: a failed ping must not end the queue
                            }
                            if (!HasRoster(lobby) && ChibiRoomRules.HostShouldGiveUp(Time.unscaledTime - hostSince))
                            {
                                Fail(a, "No players found for this mode right now. Try again in a moment.");
                                return;
                            }
                            if (!HasRoster(lobby) && ChibiRoomRules.ShouldStart(Format, n, Time.unscaledTime - hostSince))
                            {
                                // Join order as the lobby lists it (the host is normally first; after a migration it may
                                // not be: slot 0 is then whoever joined first, and every client checks the same order).
                                var ids = new string[n];
                                for (int i = 0; i < n; i++) ids[i] = lobby.Players[i].Id;
                                string lobbyId = lobby.Id;
                                await WithBackoff(a, () => LobbyService.Instance.UpdateLobbyAsync(lobbyId, new UpdateLobbyOptions
                                {
                                    IsLocked = true,
                                    Data = new Dictionary<string, DataObject>
                                    {
                                        [KeyRoster] = new DataObject(DataObject.VisibilityOptions.Member, string.Join(",", ids)),
                                    },
                                }));
                                if (!Live(a)) return;
                                // Re-read after the lock: someone may have left in between; the check below reconciles.
                                lobby = await WithBackoff(a, () => LobbyService.Instance.GetLobbyAsync(lobbyId));
                                if (lobby == null) return;
                                if (!Live(a)) return;
                                a.Lobby = lobby;
                            }
                        }
                        else if (!HasRoster(lobby) && ChibiRoomRules.ShouldRequeue(false, Time.unscaledTime - joinedAt))
                        {
                            requeue = true;
                            break;
                        }

                        if (HasRoster(lobby))
                        {
                            roster = ChibiRoomRules.ParseRoster(lobby.Data[KeyRoster].Value, Format);
                            string err = roster == null ? "The match could not be set up (bad roster)."
                                                        : ChibiRoomRules.VerifyRoster(roster, PlayerIds(lobby));   // review S2
                            if (err != null) { Fail(a, err); return; }
                        }
                    }
                    if (requeue)
                    {
                        Status = "No match yet, searching again...";
                        _ = LeaveLobby(a, 0f);
                        a.Lobby = null;
                        a.IsHost = false;
                        continue;
                    }

                    int me = Array.IndexOf(roster, myId);
                    if (me < 0) { Fail(a, "The match started without you. Try again."); return; }
                    await Connect(a, lobby, roster, me);
                    return;
                }
            }
            catch (Exception e)
            {
                Fail(a, "Matchmaking failed: " + e.Message);
            }
        }

        async Task<Lobby> FindOrCreate(Attempt a, string tag, int seats, string pub)
        {
            var playerData = new Dictionary<string, PlayerDataObject>
            {
                [ChibiRoomRules.KeyPublicKey] = new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, pub),
            };
            var query = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
            {
                Count = 20,
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT),
                    new QueryFilter(QueryFilter.FieldOptions.S1, tag, QueryFilter.OpOptions.EQ),
                },
            });
            int attempts = 0;
            foreach (var l in query.Results)
            {
                if (!Live(a) || attempts++ >= ChibiRoomRules.JoinAttemptsPerSearch) break;
                try
                {
                    return await LobbyService.Instance.JoinLobbyByIdAsync(l.Id, new JoinLobbyByIdOptions
                    {
                        Player = new Player(id: a.MyId, data: playerData),
                    });
                }
                catch (LobbyServiceException e) when (e.Reason != LobbyExceptionReason.RateLimited) { /* full or gone: next */ }
            }
            return await LobbyService.Instance.CreateLobbyAsync("Chibi Clash " + tag, seats, new CreateLobbyOptions
            {
                IsPrivate = false,
                Player = new Player(id: a.MyId, data: playerData),
                Data = new Dictionary<string, DataObject>
                {
                    [KeyProto] = new DataObject(DataObject.VisibilityOptions.Public, tag, DataObject.IndexOptions.S1),
                },
            });
        }

        async Task Connect(Attempt a, Lobby lobby, string[] roster, int me)
        {
            int count = roster.Length;

            // ---- pair keys (review N1)
            var pubs = new byte[count][];
            for (int s = 0; s < count; s++) pubs[s] = ChibiRoomRules.ParsePublicKey(PlayerData(lobby, roster[s], ChibiRoomRules.KeyPublicKey));
            var keys = ChibiRoomRules.DerivePairKeys(a.Private, pubs, lobby.Id, me);
            Array.Clear(a.Private, 0, a.Private.Length);
            if (keys == null) { Fail(a, "Could not secure the connection to every player. Try again."); return; }

            // ---- Relay mesh
            a.Links = new ChibiUtpTransport[count];
            var codes = new Dictionary<string, PlayerDataObject>();
            for (int j = me + 1; j < count; j++)
            {
                Allocation alloc = await RelayService.Instance.CreateAllocationAsync(1);
                string code = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
                a.Allocations++;
                if (!Live(a)) return;
                a.Links[j] = ChibiUtpTransport.HostRelay(alloc.ToRelayServerData(RelayConnectionType));
                codes[ChibiRoomRules.MeshRelayKey(j)] = new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, code);
            }
            if (codes.Count > 0) await LobbyService.Instance.UpdatePlayerAsync(lobby.Id, a.MyId, new UpdatePlayerOptions { Data = codes });
            if (!Live(a)) return;
            Status = "Connecting to " + (count - 1) + " players...";

            float deadline = Time.unscaledTime + MeshConnectTimeoutSeconds, heartbeatAt = 0f, pollAt = 0f;
            while (true)
            {
                if (!Live(a)) return;
                if (a.IsHost && Time.unscaledTime >= heartbeatAt)
                {
                    heartbeatAt = Time.unscaledTime + HeartbeatSeconds;
                    _ = Heartbeat(lobby.Id);
                }
                bool pendingJoin = false;
                for (int i = 0; i < me; i++)
                {
                    if (a.Links[i] != null) continue;
                    pendingJoin = true;
                    string code = PlayerData(lobby, roster[i], ChibiRoomRules.MeshRelayKey(me));
                    if (code == null || !ChibiRoomRules.ValidRelayCode(code)) continue;
                    JoinAllocation join = await RelayService.Instance.JoinAllocationAsync(code);
                    if (!Live(a)) return;
                    a.Links[i] = ChibiUtpTransport.JoinRelay(join.ToRelayServerData(RelayConnectionType));
                }
                bool all = true;
                for (int s = 0; s < count; s++)
                {
                    if (s == me) continue;
                    if (a.Links[s] == null) { all = false; continue; }
                    a.Links[s].Pump();
                    a.Links[s].Flush();
                    if (a.Links[s].LinkState == ChibiLinkState.Lost) { Fail(a, "A player dropped while connecting. Try again."); return; }
                    if (a.Links[s].LinkState != ChibiLinkState.Connected) all = false;
                }
                if (all) break;
                if (Time.unscaledTime > deadline) { Fail(a, "Could not connect to every player. Try again."); return; }
                await Task.Delay(50);
                if (pendingJoin && Time.unscaledTime >= pollAt && Live(a))
                {
                    pollAt = Time.unscaledTime + PollSeconds;
                    string lobbyId = lobby.Id;
                    var fresh = await WithBackoff(a, () => LobbyService.Instance.GetLobbyAsync(lobbyId));
                    if (fresh == null) return;
                    lobby = fresh;
                    a.Lobby = lobby;
                }
            }

            // ---- hand over
            var hashes = new uint[count];
            for (int s = 0; s < count; s++) hashes[s] = ChibiControlCodec.PlayerHash(roster[s]);
            var links = new IChibiTransport[count];
            for (int s = 0; s < count; s++) links[s] = a.Links[s];
            var ready = new ChibiMeshLinks
            {
                Format = Format, Count = count, MySlot = me, Links = links, PlayerIds = roster, PlayerHashes = hashes, PairKeys = keys,
            };
            a.HandedOff = true;                                   // the match owns the links from here
            _ = LeaveLobby(a, a.IsHost ? HostDeleteDelaySeconds : 0f);   // the host keeps it so slower pairs can read codes
            _current = null;
            Busy = false;
            Status = "";
            OnMeshReady?.Invoke(ready);
        }

        /// <summary>
        /// Run a Lobby call, backing off (<see cref="ChibiRoomRules.RateLimitBackoffSeconds"/>) and retrying while it is
        /// rate limited. Returns default when the flow was cancelled meanwhile; other errors propagate.
        /// </summary>
        async Task<T> WithBackoff<T>(Attempt a, Func<Task<T>> call)
        {
            for (int n = 1; ; n++)
            {
                try { return await call(); }
                catch (Exception e) when (RateLimited(e))
                {
                    await Task.Delay((int)(ChibiRoomRules.RateLimitBackoffSeconds(n) * 1000));
                    if (!Live(a)) return default;
                }
            }
        }

        static bool HasRoster(Lobby l) => l?.Data != null && l.Data.TryGetValue(KeyRoster, out var d) && !string.IsNullOrEmpty(d?.Value);

        static List<string> PlayerIds(Lobby l)
        {
            var ids = new List<string>();
            if (l?.Players != null) foreach (var p in l.Players) ids.Add(p.Id);
            return ids;
        }

        static string PlayerData(Lobby l, string playerId, string key)
        {
            if (l?.Players == null) return null;
            foreach (var p in l.Players)
                if (p.Id == playerId && p.Data != null && p.Data.TryGetValue(key, out var d))
                    return d?.Value;
            return null;
        }

        static async Task Heartbeat(string lobbyId)
        {
            try { await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId); }
            catch (Exception e) { Debug.LogWarning("ChibiClash team lobby heartbeat: " + e.Message); }
        }

        static async Task LeaveLobby(Attempt a, float delaySeconds)
        {
            var lobby = a.Lobby;
            if (lobby == null) return;
            try
            {
                // The host keeps the lobby alive (heartbeat) through the delete delay.
                float until = Time.unscaledTime + delaySeconds;
                while (a.IsHost && Time.unscaledTime < until)
                {
                    await Heartbeat(lobby.Id);
                    await Task.Delay((int)(Math.Min(HeartbeatSeconds, Math.Max(0.1f, until - Time.unscaledTime)) * 1000));
                }
                if (a.IsHost) await LobbyService.Instance.DeleteLobbyAsync(lobby.Id);
                else await LobbyService.Instance.RemovePlayerAsync(lobby.Id, a.MyId);
            }
            catch (Exception) { /* lobby already gone */ }
        }

        void OnDestroy() => Cancel();
    }
}
