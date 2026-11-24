using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// INVITE BY NAME (docs/architecture/netcode/online-rooms.md "Invite by name"): invite a player into your private room
    /// by typing their in-game name. No friendship and no #tag needed. Unity Lobby only (no Friends, no Cloud Save).
    /// <para>
    /// Send: we publish a short-lived public "beacon" lobby named <see cref="ChibiNameInviteRules.Marker"/> whose indexed
    /// field S5 holds a hash of the friend's lower-case name and our build tag, with the room code, our name and the room
    /// name masked by the same name. One beacon at a time; it is deleted after 60 s, on <see cref="CancelOutgoing"/>, when someone joins the
    /// room, or when the room is full or closed. Receive: while <see cref="Enabled"/> (online menu pages, never during a match) we query every 5-6 s for
    /// beacons addressed to our name, validate every field (<see cref="ChibiNameInviteRules.Validate"/>) and list them in
    /// <see cref="Pending"/>, newest per inviter. Three declines in 10 minutes pause all invites for 10 minutes
    /// (<see cref="Paused"/>). Nothing joins by itself: <see cref="Accept"/> only returns the room code, and the game
    /// passes it to <c>ChibiRoomService.JoinByCode</c>.
    /// </para>
    /// Names are self-reported and not unique: every player called "Kira" sees an invite sent to "Kira". Main thread only.
    /// No exception escapes; Update does not allocate.
    /// </summary>
    /// <example><code>
    /// nameInvites = gameObject.AddComponent&lt;Netcode.ChibiNameInvites&gt;();   // finds ChibiRoomService on the same object
    /// nameInvites.Enabled = onOnlineMenu &amp;&amp; !inMatch;
    /// nameInvites.Send("Kira", roomsSvc.RoomCode, roomsSvc.Current.Name);
    /// string code = nameInvites.Accept(0); if (code.Length &gt; 0) roomsSvc.JoinByCode(code);
    /// </code></example>
    public sealed class ChibiNameInvites : MonoBehaviour
    {
        /// <summary>Receive invites (query the Lobby). Set by the game on online menu pages only; off during a match.</summary>
        public bool Enabled { get; set; }
        /// <summary>Invites waiting for an answer (at most 3, newest per inviter, expired ones removed).</summary>
        public IReadOnlyList<PendingNameInvite> Pending => _pending;
        /// <summary>Short player-facing status of the last send / answer ("" none).</summary>
        public string Status { get; private set; } = "";
        /// <summary><see cref="Status"/> describes a failure.</summary>
        public bool StatusIsError { get; private set; }
        /// <summary>A send is in flight.</summary>
        public bool Sending { get; private set; }
        /// <summary>Our invite beacon is live (or being created).</summary>
        public bool HasOutgoing => _outId.Length > 0 || Sending;
        /// <summary>
        /// Incoming invites are paused (SEC-003: 3 declines within 10 minutes pause all invites for 10 minutes, whoever
        /// sends them). The UI can show "Invites paused".
        /// </summary>
        public bool Paused => Time.unscaledTime < _pausedUntil;
        /// <summary>Name we invited (as typed, safe to draw; "" when no invite is out).</summary>
        public string OutgoingTo { get; private set; } = "";

        /// <summary>Our in-game name. Null = the bound room service's <c>MyName</c> (GameManager already sets that).</summary>
        public Func<string> MyName;
        /// <summary>Age gate. Null = the bound room service's <c>OnlineAllowed</c>.</summary>
        public Func<bool> OnlineAllowed;
        /// <summary>True while a match runs (no queries). Null = the bound room service's <c>InMatch</c>.</summary>
        public Func<bool> InMatch;
        /// <summary>
        /// Worldwide name gate (claims our name; null = OK, else the reason). Null = the bound room service's
        /// <c>NameGate</c>. Invites are only sent, and only looked for, under a claimed name.
        /// </summary>
        public Func<Task<string>> NameGate;

        Task<string> GateName() => (NameGate ?? _rooms?.NameGate)?.Invoke() ?? Task.FromResult<string>(null);

        ChibiRoomService _rooms;
        readonly List<PendingNameInvite> _pending = new List<PendingNameInvite>(ChibiNameInviteRules.MaxPending);
        readonly List<ChibiDismissedInvite> _dismissed = new List<ChibiDismissedInvite>(ChibiNameInviteRules.MaxDismissed + 1);
        readonly HashSet<string> _seen = new HashSet<string>();
        readonly HashSet<string> _hostsThisQuery = new HashSet<string>();
        readonly List<float> _declineTimes = new List<float>(ChibiNameInviteRules.DeclinesBeforePause);
        float _pausedUntil = -1f;
        readonly List<QueryFilter> _filters = new List<QueryFilter>(1);
        QueryLobbiesOptions _query;
        string _myNorm = "", _myIndex = "", _myIdSeen = "";
        float _pollAt, _nextLogAt, _lastSendAt = -999f;
        bool _polling, _wasEnabled;

        // Outgoing beacon.
        string _outId = "", _outCode = "";
        int _outPlayers = -1;   // players in the invited room when the invite went out (-1 unknown)
        float _outExpires, _outHeartbeatAt;
        int _sendGen;

        /// <summary>The room service whose rooms we invite into (found on the same GameObject when not bound).</summary>
        public void Bind(ChibiRoomService rooms) => _rooms = rooms;

        // ================================================================== send

        /// <summary>
        /// Invite <paramref name="friendName"/> (their in-game name) into the room <paramref name="roomCode"/>. Replaces
        /// our previous invite. Result in <see cref="Status"/>.
        /// </summary>
        public async void Send(string friendName, string roomCode, string roomName)
        {
            if (Sending) return;
            if (!Allowed()) { SetStatus(NetworkRoomManager.NotAllowedMessage, true); return; }
            float now = Time.unscaledTime;
            if (now - _lastSendAt < ChibiNameInviteRules.SendCooldownSeconds)
            { SetStatus(Loc.T("invite.wait"), true); return; }
            string friend = ChibiNameInviteRules.NormalizeName(friendName);
            if (friend == null) { SetStatus(Loc.T("invite.type_name"), true); return; }
            if (friend == ChibiNameInviteRules.NormalizeName(MyNameNow())) { SetStatus(Loc.T("invite.own_name"), true); return; }
            string code = ChibiSocialRules.NormalizeCode(roomCode);
            if (code == null) { SetStatus(Loc.T("invite.open_room_first"), true); return; }
            var room = Rooms();
            if (room != null && room.ListLoaded)
            {
                var snap = FindRoom(room, code);
                if (snap == null) { SetStatus(Loc.T("invite.open_room_first"), true); return; }
                if (snap.Slots.Count >= ChibiRoomRules.MaxSeats) { SetStatus(Loc.T("invite.room_full"), true); return; }
            }
            string shown = ChibiSocialRules.BaseName(ChibiSocialRules.SafeDisplayName(friendName.Trim()));

            _lastSendAt = now;
            Sending = true;
            int gen = ++_sendGen;
            SetStatus(Loc.T("invite.inviting", shown), false);
            try
            {
                string err = await NetworkRoomManager.EnsureSignedInAsync() ?? await GateName();
                if (err != null) { SetStatus(err, true); return; }
                await DeleteOutgoing();   // at most one beacon per player
                if (gen != _sendGen) return;   // cancelled while we waited
                if (!ChibiNameInviteRules.TryBuildBeacon(friend, NetworkRoomManager.ProtocolTag, code, ChibiNameInviteRules.NewSalt(),
                                                         MyNameNow(), roomName, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var b))
                { SetStatus(Loc.T("invite.could_not_send"), true); return; }
                var options = new CreateLobbyOptions
                {
                    IsPrivate = false,
                    // Nobody may join a beacon (a griefer filling its seat would hide it): random password, never kept.
                    Password = Guid.NewGuid().ToString("N"),
                    Data = new Dictionary<string, DataObject>
                    {
                        [ChibiNameInviteRules.KeyIndex] = new DataObject(DataObject.VisibilityOptions.Public, b.Index, DataObject.IndexOptions.S5),
                        [ChibiNameInviteRules.KeyProto] = Pub(b.Proto),
                        [ChibiNameInviteRules.KeyVersion] = Pub(b.Version),
                        [ChibiNameInviteRules.KeyPayload] = Pub(b.Payload),   // code, our name, room name: masked
                        [ChibiNameInviteRules.KeySalt] = Pub(b.Salt),
                        [ChibiNameInviteRules.KeyExpiry] = Pub(b.Expiry),
                    },
                };
                var lobby = await LobbyService.Instance.CreateLobbyAsync(ChibiNameInviteRules.Marker, ChibiNameInviteRules.BeaconSeats, options);
                if (lobby == null || string.IsNullOrEmpty(lobby.Id)) { SetStatus(Loc.T("invite.could_not_send"), true); return; }
                if (gen != _sendGen || this == null) { _ = DeleteQuietly(lobby.Id); return; }   // cancelled while creating
                _outId = lobby.Id;
                _outCode = code;
                // The open room's snapshot is the freshest (polled every 2 s); the room list may be older.
                var sentRoom = room != null && room.Current != null && room.Current.Code == code ? room.Current
                             : room != null && room.ListLoaded ? FindRoom(room, code) : null;
                _outPlayers = sentRoom != null ? sentRoom.Slots.Count : -1;
                _outExpires = Time.unscaledTime + ChibiNameInviteRules.LifetimeSeconds;
                _outHeartbeatAt = Time.unscaledTime + ChibiNameInviteRules.FirstHeartbeatSeconds;
                OutgoingTo = shown;
                SetStatus(Loc.T("invite.sent_lasts", shown, (int)ChibiNameInviteRules.LifetimeSeconds), false);
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.RateLimited)
            { SetStatus(Loc.T("invite.too_many"), true); }
            catch (Exception e) { SetStatus(Loc.T("invite.send_failed", NetworkRoomManager.Explain(e)), true); }
            finally { Sending = false; }
        }

        /// <summary>Withdraw our invite (deletes the beacon; a send in flight is cancelled too).</summary>
        public void CancelOutgoing()
        {
            bool had = HasOutgoing;
            _sendGen++;
            _ = DeleteOutgoing();
            if (had) SetStatus(Loc.T("invite.cancelled"), false);
        }

        // ================================================================== answer

        /// <summary>
        /// Accept invite <paramref name="index"/>: returns its room code ("" when the index is wrong or it expired). The
        /// invite is removed. This does NOT join; the game calls <c>ChibiRoomService.JoinByCode</c> with the code.
        /// </summary>
        public string Accept(int index)
        {
            if (index < 0 || index >= _pending.Count) return "";
            var inv = _pending[index];
            _pending.RemoveAt(index);
            ChibiNameInviteRules.Dismiss(_dismissed, inv, 0f);
            if (inv.ExpiresAt <= Time.unscaledTime) { SetStatus(Loc.T("invite.expired"), true); return ""; }
            SetStatus(Loc.T("invite.joining_room_of", inv.FromName), false);
            return inv.RoomCode;
        }

        /// <summary>
        /// Decline invite <paramref name="index"/>: removed, and that player's new invites are ignored for 60 s. The third
        /// decline within 10 minutes pauses all incoming invites for 10 minutes (<see cref="Paused"/>).
        /// </summary>
        public void Decline(int index)
        {
            if (index < 0 || index >= _pending.Count) return;
            var inv = _pending[index];
            _pending.RemoveAt(index);
            float now = Time.unscaledTime;
            ChibiNameInviteRules.Dismiss(_dismissed, inv, now + ChibiNameInviteRules.DeclineMuteSeconds);
            bool wasPaused = Paused;
            _pausedUntil = ChibiNameInviteRules.RecordDecline(_declineTimes, now, _pausedUntil);
            if (Paused && !wasPaused)
            {
                // SEC-003: too many declines: stop listening for a while and drop what is waiting.
                for (int i = 0; i < _pending.Count; i++) ChibiNameInviteRules.Dismiss(_dismissed, _pending[i], 0f);
                _pending.Clear();
                SetStatus(Loc.T("invite.paused", (int)(ChibiNameInviteRules.PauseSeconds / 60f)), false);
            }
            else SetStatus("", false);
        }

        // ================================================================== loop

        void Start()
        {
            if (_rooms == null) _rooms = GetComponent<ChibiRoomService>();
        }

        void Update()
        {
            float now = Time.unscaledTime;
            TickOutgoing(now);
            if (_pending.Count > 0) ChibiNameInviteRules.Prune(_pending, now, null, _dismissed);

            bool on = Enabled && !IsInMatch() && now >= _pausedUntil;
            if (on && !_wasEnabled) _pollAt = Mathf.Max(_pollAt, now + UnityEngine.Random.Range(0.3f, 1.2f));   // no burst on page change
            _wasEnabled = on;
            if (!on || _polling || now < _pollAt || !Allowed()) return;
            _pollAt = now + ChibiNameInviteRules.PollDelay(UnityEngine.Random.value);
            _ = Poll();
        }

        async Task Poll()
        {
            _polling = true;
            try
            {
                string err = await NetworkRoomManager.EnsureSignedInAsync();
                if (err != null) { _pollAt = Time.unscaledTime + ChibiNameInviteRules.RateLimitBackoffSeconds; return; }
                string me = MyId;
                if (me != _myIdSeen)
                {
                    // Another account signed in: its invites and our old beacon (no longer ours to delete; it goes inactive) are dropped.
                    if (_myIdSeen.Length > 0) { _pending.Clear(); _dismissed.Clear(); _outId = ""; OutgoingTo = ""; }
                    _myIdSeen = me;
                }
                string norm = ChibiNameInviteRules.NormalizeName(MyNameNow());
                if (norm == null) return;   // no valid name yet: nobody can address us
                // Only a name claimed worldwide may receive invites (shared claim, once per session; slow retry on failure).
                if (await GateName() != null) { _pollAt = Time.unscaledTime + ChibiNameInviteRules.NameGateBackoffSeconds; return; }
                if (this == null) return;
                if (norm != _myNorm) PrepareQuery(norm);

                var resp = await LobbyService.Instance.QueryLobbiesAsync(_query);
                if (this == null || norm != _myNorm || Paused) return;   // destroyed, name changed, or paused meanwhile
                float now = Time.unscaledTime;
                long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string proto = NetworkRoomManager.ProtocolTag;
                _seen.Clear();
                _hostsThisQuery.Clear();
                var results = resp?.Results;
                if (results != null)
                    for (int i = 0; i < results.Count; i++)
                    {
                        var l = results[i];
                        if (l == null || string.IsNullOrEmpty(l.Id)) continue;
                        _seen.Add(l.Id);
                        var b = ToBeacon(l);
                        if (ChibiNameInviteRules.Validate(b, _myIndex, _myNorm, proto, me, now, nowUnix, out var inv) != ChibiNameInviteVerdict.Ok)
                            continue;
                        if (ChibiNameInviteRules.Offer(_pending, inv, _dismissed, now, _hostsThisQuery) == ChibiNameInviteVerdict.Ok && !IsInMatch())
                            SetStatus(Loc.T("invite.invited_you", inv.FromName, inv.RoomName), false);
                    }
                // beacon gone = invite withdrawn. A FULL page may have pushed a live beacon off it (junk beacons from
                // throwaway accounts), so then absence proves nothing: only expiry removes invites (review 2026-11-22).
                bool fullPage = results != null && results.Count >= ChibiNameInviteRules.QueryCount;
                ChibiNameInviteRules.Prune(_pending, now, fullPage ? null : _seen, _dismissed);
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.RateLimited)
            {
                _pollAt = Mathf.Max(_pollAt, Time.unscaledTime + ChibiNameInviteRules.RateLimitBackoffSeconds + UnityEngine.Random.Range(0f, 5f));
                LogOnce("ChibiClash name invites: Lobby rate limit, backing off.");
            }
            catch (Exception e) { LogOnce("ChibiClash name invites poll: " + NetworkRoomManager.Explain(e)); }
            finally { _polling = false; }
        }

        void PrepareQuery(string norm)
        {
            _myNorm = norm;
            _myIndex = ChibiNameInviteRules.IndexValue(norm, NetworkRoomManager.ProtocolTag);
            _pending.Clear();   // invites to our old name are not for us any more
            _filters.Clear();
            _filters.Add(new QueryFilter(QueryFilter.FieldOptions.S5, _myIndex, QueryFilter.OpOptions.EQ));
            _query = new QueryLobbiesOptions
            {
                Count = ChibiNameInviteRules.QueryCount,
                Filters = _filters,
                // SEC-002: newest first, so old junk beacons cannot fill the page and hide a real invite; within one
                // query only the first (newest) beacon per inviter counts.
                Order = new List<QueryOrder> { new QueryOrder(false, QueryOrder.FieldOptions.Created) },
            };
        }

        // ================================================================== outgoing beacon

        void TickOutgoing(float now)
        {
            if (_outId.Length == 0) return;
            var room = Rooms();
            bool known = room != null && room.ListLoaded;
            var snap = known ? FindRoom(room, _outCode) : null;
            var end = ChibiNameInviteRules.OutgoingEnd(now, _outExpires, known, snap != null, snap != null ? snap.Slots.Count : 0, _outPlayers);
            if (end != ChibiNameInviteEnd.None)
            {
                string who = OutgoingTo;
                _ = DeleteOutgoing();
                if (end == ChibiNameInviteEnd.Expired) SetStatus(Loc.T("invite.ran_out", who), false);
                else if (end == ChibiNameInviteEnd.RoomFull) SetStatus(Loc.T("invite.closed_full"), false);
                else if (end == ChibiNameInviteEnd.Joined) SetStatus(Loc.T("invite.closed_joined"), false);
                else SetStatus(Loc.T("invite.closed_room"), false);
                return;
            }
            if (now >= _outHeartbeatAt)
            {
                _outHeartbeatAt = now + ChibiNameInviteRules.HeartbeatSeconds;
                _ = Heartbeat(_outId);
            }
        }

        async Task Heartbeat(string id)
        {
            try { await LobbyService.Instance.SendHeartbeatPingAsync(id); }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.RateLimited)
            { if (this != null && id == _outId) _outHeartbeatAt = Time.unscaledTime + 3f; }
            catch (Exception) { /* gone: it drops out of queries by itself */ }
        }

        async Task DeleteOutgoing()
        {
            string id = _outId;
            _outId = "";
            _outCode = "";
            _outPlayers = -1;
            OutgoingTo = "";
            if (id.Length > 0) await DeleteQuietly(id);
        }

        static async Task DeleteQuietly(string id)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { await LobbyService.Instance.DeleteLobbyAsync(id); return; }
                catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.RateLimited) { await Task.Delay(1500); }
                catch (Exception) { return; }   // already gone; otherwise it goes inactive 30 s after its last heartbeat
            }
        }

        void OnDestroy()
        {
            _sendGen++;
            if (_outId.Length > 0) _ = DeleteQuietly(_outId);
            _outId = "";
        }

        // ================================================================== helpers

        static DataObject Pub(string v) => new DataObject(DataObject.VisibilityOptions.Public, v ?? "");

        static string Val(Dictionary<string, DataObject> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? v.Value : null;

        static ChibiNameBeacon ToBeacon(Lobby l) => new ChibiNameBeacon
        {
            LobbyId = l.Id, LobbyName = l.Name, HostId = l.HostId,
            Index = Val(l.Data, ChibiNameInviteRules.KeyIndex), Proto = Val(l.Data, ChibiNameInviteRules.KeyProto),
            Version = Val(l.Data, ChibiNameInviteRules.KeyVersion), Payload = Val(l.Data, ChibiNameInviteRules.KeyPayload),
            Salt = Val(l.Data, ChibiNameInviteRules.KeySalt), Expiry = Val(l.Data, ChibiNameInviteRules.KeyExpiry),
        };

        static ChibiRoomSnapshot FindRoom(ChibiRoomService rooms, string code)
        {
            var list = rooms.MyRooms;
            for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].Code == code) return list[i];
            return null;
        }

        ChibiRoomService Rooms() => _rooms;

        string MyNameNow()
        {
            try
            {
                if (MyName != null) return MyName() ?? "";
                return _rooms != null && _rooms.MyName != null ? _rooms.MyName() ?? "" : "";
            }
            catch (Exception) { return ""; }
        }

        /// <summary>Age gate; fails CLOSED (SEC-006): no gate wired and no room service bound means no online use.</summary>
        bool Allowed()
        {
            var f = OnlineAllowed ?? _rooms?.OnlineAllowed;
            try { return f != null && f(); }
            catch (Exception) { return false; }
        }

        bool IsInMatch()
        {
            var f = InMatch ?? _rooms?.InMatch;
            try { return f != null && f(); }
            catch (Exception) { return false; }   // no exception escapes Update
        }

        /// <summary>Our player id ("" before sign-in); guarded because AuthenticationService throws before services start.</summary>
        static string MyId => Unity.Services.Core.UnityServices.State == Unity.Services.Core.ServicesInitializationState.Initialized &&
                              AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn
            ? AuthenticationService.Instance.PlayerId : "";

        void LogOnce(string msg)
        {
            if (Time.unscaledTime < _nextLogAt) return;   // anomalies logged at most every 30 s
            _nextLogAt = Time.unscaledTime + 30f;
            Debug.LogWarning(msg);
        }

        void SetStatus(string s, bool error)
        {
            Status = s ?? "";
            StatusIsError = error;
        }
    }
}
