using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Friends;
using Unity.Services.Friends.Models;
using Unity.Services.Friends.Notifications;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>A friend as the menu shows it (snapshot; names already made safe to draw).</summary>
    public struct ChibiFriendView
    {
        public string Id, Name;
        public bool Online;
    }

    /// <summary>An invite waiting for ACCEPT / DECLINE.</summary>
    public struct ChibiPendingInvite
    {
        public string FromId, FromName, Code;
        /// <summary>Local unscaled time the invite expires.</summary>
        public float ExpiresAt;
    }

    /// <summary>
    /// Player name, friends list and match invites, like a normal online fighting game (docs/architecture/netcode/
    /// online-social.md). Built on Unity Authentication (player name "Name#1234"), Unity Friends (requests, presence,
    /// messages) and the existing private rooms of <see cref="NetworkRoomManager"/>:
    ///   INVITE friend  -> host creates a private room -> sends the room code to that friend as a Friends message
    ///   friend ACCEPT  -> joins the room by code -> Relay -> handshake -> match
    /// Security: only friends (per Unity Friends, never per message) can invite; every message is validated by
    /// <see cref="ChibiSocialRules"/>; invites are rate limited and expire; names are sanitised before drawing.
    /// Main thread only.
    /// </summary>
    /// <example><code>
    /// var social = gameObject.AddComponent&lt;ChibiSocialManager&gt;();
    /// social.Bind(rooms);
    /// social.Connect();                 // sign in + load friends
    /// social.SendInvite(friend.Id);     // creates a private room and invites
    /// </code></example>
    public sealed class ChibiSocialManager : MonoBehaviour
    {
        /// <summary>Signed in and the Friends service is ready.</summary>
        public bool Ready { get; private set; }
        /// <summary>True while a request is in flight (buttons disabled).</summary>
        public bool Working { get; private set; }
        /// <summary>Our player name as Unity shows it ("Name#1234"), safe to draw; empty before sign-in.</summary>
        public string MyName { get; private set; } = "";
        /// <summary>True when the player never chose a name (Unity generated one): the menu asks for one.</summary>
        public bool NeedsName { get; private set; }
        /// <summary>Last message for the player (errors in <see cref="StatusIsError"/>).</summary>
        public string Status { get; private set; } = "";
        public bool StatusIsError { get; private set; }

        /// <summary>Friends, online first then by name.</summary>
        public readonly List<ChibiFriendView> Friends = new List<ChibiFriendView>();
        /// <summary>People who asked to be our friend.</summary>
        public readonly List<ChibiFriendView> Requests = new List<ChibiFriendView>();
        /// <summary>Invites waiting for an answer (newest last).</summary>
        public readonly List<ChibiPendingInvite> Invites = new List<ChibiPendingInvite>();

        /// <summary>When true, incoming invites are ignored (in a match or already in a room flow).</summary>
        public Func<bool> IsBusy = () => false;

        NetworkRoomManager _rooms;
        bool _friendsInitialised, _subscribed;
        string _inviteTargetId;               // friend we are creating a room for
        float _inviteTargetSince;
        readonly Dictionary<string, float> _lastInviteFrom = new Dictionary<string, float>();
        readonly Dictionary<string, float> _lastInviteTo = new Dictionary<string, float>();

        /// <summary>Use these rooms to host invited matches and to join accepted invites.</summary>
        public void Bind(NetworkRoomManager rooms) => _rooms = rooms;

        // ================================================================== connect

        /// <summary>Sign in, read our name, start the Friends service and load the lists. Safe to call again.</summary>
        public async void Connect()
        {
            if (Working) return;
            Working = true;
            try
            {
                string err = await NetworkRoomManager.EnsureSignedInAsync();
                if (err != null) { SetStatus(err, true); return; }
                string name = await AuthenticationService.Instance.GetPlayerNameAsync(autoGenerate: false);
                NeedsName = string.IsNullOrEmpty(name);
                MyName = NeedsName ? "" : ChibiSocialRules.SafeDisplayName(name);
                if (!_friendsInitialised)
                {
                    await FriendsService.Instance.InitializeAsync();
                    _friendsInitialised = true;
                }
                Subscribe();
                await FriendsService.Instance.SetPresenceAvailabilityAsync(Availability.Online);
                Ready = true;
                RebuildLists();
                SetStatus(NeedsName ? "Choose a player name so friends can find you." : "Signed in as " + MyName + ".", false);
            }
            catch (Exception e) { SetStatus("Friends unavailable: " + NetworkRoomManager.Explain(e), true); }
            finally { Working = false; }
        }

        /// <summary>Set our public player name (validated). Unity appends a tag such as #1234 to keep names unique.</summary>
        public async void SetName(string name)
        {
            if (Working || !Ready) return;
            string err = ChibiSocialRules.ValidateName(name);
            if (err != null) { SetStatus(err, true); return; }
            Working = true;
            try
            {
                string full = await AuthenticationService.Instance.UpdatePlayerNameAsync(name);
                MyName = ChibiSocialRules.SafeDisplayName(full);
                NeedsName = false;
                SetStatus("Your name is " + MyName + ". Give it to friends so they can add you.", false);
            }
            catch (Exception e) { SetStatus("Could not set the name: " + NetworkRoomManager.Explain(e), true); }
            finally { Working = false; }
        }

        // ================================================================== friends

        /// <summary>Send a friend request by name ("Kira#4821").</summary>
        public async void AddFriend(string name)
        {
            if (Working || !Ready) return;
            string n = ChibiSocialRules.NormalizeFriendName(name);
            if (n == null) { SetStatus("Type your friend's name like Kira#4821.", true); return; }
            Working = true;
            try
            {
                await FriendsService.Instance.AddFriendByNameAsync(n);
                RebuildLists();
                SetStatus("Friend request sent to " + ChibiSocialRules.SafeDisplayName(n) + ".", false);
            }
            catch (Exception e) { SetStatus("Could not add " + ChibiSocialRules.SafeDisplayName(n) + ": " + NetworkRoomManager.Explain(e), true); }
            finally { Working = false; }
        }

        /// <summary>Accept a friend request (adding back makes it mutual).</summary>
        public async void AcceptRequest(string memberId) => await Run(() => FriendsService.Instance.AddFriendAsync(memberId), "Friend added.");

        /// <summary>Decline a friend request.</summary>
        public async void DeclineRequest(string memberId) =>
            await Run(() => FriendsService.Instance.DeleteIncomingFriendRequestAsync(memberId), "Request declined.");

        /// <summary>Remove a friend.</summary>
        public async void RemoveFriend(string memberId) => await Run(() => FriendsService.Instance.DeleteFriendAsync(memberId), "Friend removed.");

        /// <summary>Block a player: no requests, messages or invites from them any more.</summary>
        public async void Block(string memberId) => await Run(() => FriendsService.Instance.AddBlockAsync(memberId), "Player blocked.");

        async Task Run(Func<Task> call, string done)
        {
            if (Working || !Ready) return;
            Working = true;
            try { await call(); RebuildLists(); SetStatus(done, false); }
            catch (Exception e) { SetStatus(NetworkRoomManager.Explain(e), true); }
            finally { Working = false; }
        }

        // Friends SDK events can arrive from its notification socket, so handlers only enqueue; Update applies them on the
        // main thread (lists and UI state are never touched off the main thread).
        readonly System.Collections.Concurrent.ConcurrentQueue<IMessageReceivedEvent> _messages =
            new System.Collections.Concurrent.ConcurrentQueue<IMessageReceivedEvent>();
        volatile bool _listsDirty;
        Action<IRelationshipAddedEvent> _onAdded;
        Action<IRelationshipDeletedEvent> _onDeleted;
        Action<IPresenceUpdatedEvent> _onPresence;
        Action<IMessageReceivedEvent> _onMessage;

        void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            _onAdded = _ => _listsDirty = true;
            _onDeleted = _ => _listsDirty = true;
            _onPresence = _ => _listsDirty = true;
            _onMessage = e => { if (_messages.Count < 64) _messages.Enqueue(e); };   // bounded: a flood cannot grow memory
            FriendsService.Instance.RelationshipAdded += _onAdded;
            FriendsService.Instance.RelationshipDeleted += _onDeleted;
            FriendsService.Instance.PresenceUpdated += _onPresence;
            FriendsService.Instance.MessageReceived += _onMessage;
        }

        void OnDestroy()
        {
            if (!_subscribed) return;
            try
            {
                FriendsService.Instance.RelationshipAdded -= _onAdded;
                FriendsService.Instance.RelationshipDeleted -= _onDeleted;
                FriendsService.Instance.PresenceUpdated -= _onPresence;
                FriendsService.Instance.MessageReceived -= _onMessage;
            }
            catch (Exception) { /* services already shut down */ }
            _subscribed = false;
        }

        void RebuildLists()
        {
            Friends.Clear();
            Requests.Clear();
            if (!_friendsInitialised) return;
            foreach (var r in FriendsService.Instance.Friends) Friends.Add(View(r));
            foreach (var r in FriendsService.Instance.IncomingFriendRequests) Requests.Add(View(r));
            Friends.Sort((a, b) => a.Online != b.Online ? (a.Online ? -1 : 1) : string.CompareOrdinal(a.Name, b.Name));
        }

        static ChibiFriendView View(Relationship r)
        {
            var m = r.Member;
            var a = m?.Presence?.Availability ?? Availability.Unknown;
            return new ChibiFriendView
            {
                Id = m?.Id ?? "",
                Name = ChibiSocialRules.SafeDisplayName(m?.Profile?.Name),
                Online = a == Availability.Online || a == Availability.Busy || a == Availability.Away,
            };
        }

        bool IsFriend(string memberId)
        {
            if (!_friendsInitialised || string.IsNullOrEmpty(memberId)) return false;
            foreach (var r in FriendsService.Instance.Friends)
                if (r.Member != null && r.Member.Id == memberId) return true;
            return false;
        }

        string FriendName(string memberId)
        {
            foreach (var f in Friends) if (f.Id == memberId) return f.Name;
            return "A friend";
        }

        // ================================================================== invites

        /// <summary>Invite a friend: create a private room, then send them its code (rate limited per friend).</summary>
        public void SendInvite(string friendId)
        {
            if (!Ready || _rooms == null || !IsFriend(friendId)) return;
            float now = Time.unscaledTime;
            if (_lastInviteTo.TryGetValue(friendId, out float t) && now - t < ChibiSocialRules.InviteCooldown)
            { SetStatus("Wait a moment before inviting again.", true); return; }
            _lastInviteTo[friendId] = now;
            _inviteTargetId = friendId;
            _inviteTargetSince = now;
            if (string.IsNullOrEmpty(_rooms.RoomCode)) _rooms.CreatePrivateRoom();
            SetStatus("Creating a room for " + FriendName(friendId) + "...", false);
        }

        /// <summary>Accept an invite: join its room by code (the room flow and the handshake do the rest).</summary>
        public void AcceptInvite(int index)
        {
            if (index < 0 || index >= Invites.Count || _rooms == null) return;
            var inv = Invites[index];
            Invites.Clear();   // one match at a time: other invites are dropped
            _rooms.JoinPrivateRoom(inv.Code);
            SetStatus("Joining " + inv.FromName + "...", false);
        }

        /// <summary>Decline an invite and tell the sender.</summary>
        public async void DeclineInvite(int index)
        {
            if (index < 0 || index >= Invites.Count) return;
            var inv = Invites[index];
            Invites.RemoveAt(index);
            try
            {
                await FriendsService.Instance.MessageAsync(inv.FromId, new ChibiInviteMessage
                    { Kind = ChibiSocialRules.KindDecline, Proto = NetworkRoomManager.ProtocolTag, V = ChibiSocialRules.InviteVersion });
            }
            catch (Exception) { /* best effort: the sender's room simply times out */ }
        }

        void OnMessage(IMessageReceivedEvent e)
        {
            ChibiInviteMessage m;
            try { m = e.GetAs<ChibiInviteMessage>(); }
            catch (Exception) { return; }   // not our format: ignore silently
            if (m == null) return;
            bool friend = IsFriend(e.UserId);

            if (friend && m.Kind == ChibiSocialRules.KindDecline && e.UserId == _inviteTargetId)
            {
                SetStatus(FriendName(e.UserId) + " declined the invite.", true);
                _inviteTargetId = null;
                _rooms?.Cancel();
                return;
            }
            if (friend && m.Kind == ChibiSocialRules.KindCancel)
            {
                Invites.RemoveAll(i => i.FromId == e.UserId);
                return;
            }

            float now = Time.unscaledTime;
            float last = _lastInviteFrom.TryGetValue(e.UserId ?? "", out float l) ? l : float.NegativeInfinity;
            var verdict = ChibiSocialRules.ValidateInvite(m, friend, NetworkRoomManager.ProtocolTag, now, last, Invites.Count, IsBusy());
            if (verdict == ChibiInviteVerdict.OtherBuild && friend)
            {
                SetStatus(FriendName(e.UserId) + " invited you, but runs a different version of the game.", true);
                return;
            }
            if (verdict != ChibiInviteVerdict.Ok) return;   // never echo untrusted content
            _lastInviteFrom[e.UserId] = now;
            Invites.RemoveAll(i => i.FromId == e.UserId);
            Invites.Add(new ChibiPendingInvite
            {
                FromId = e.UserId, FromName = FriendName(e.UserId), Code = ChibiSocialRules.NormalizeCode(m.Code),
                ExpiresAt = now + ChibiSocialRules.InviteLifetime,
            });
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (_listsDirty) { _listsDirty = false; RebuildLists(); }
            for (int n = 0; n < 8 && _messages.TryDequeue(out var msg); n++) OnMessage(msg);   // a few per frame
            Invites.RemoveAll(i => i.ExpiresAt <= now);

            // Our room for an invite is ready: send the code to the friend (once).
            if (_inviteTargetId != null && _rooms != null)
            {
                if (!string.IsNullOrEmpty(_rooms.RoomCode))
                {
                    string target = _inviteTargetId, code = _rooms.RoomCode;
                    _inviteTargetId = null;
                    _ = SendInviteMessage(target, code);
                }
                else if (_rooms.State == ChibiRoomState.Failed || now - _inviteTargetSince > 30f)
                {
                    _inviteTargetId = null;
                    if (_rooms.State == ChibiRoomState.Failed) SetStatus(_rooms.Status, true);
                }
            }
        }

        async Task SendInviteMessage(string friendId, string code)
        {
            try
            {
                await FriendsService.Instance.MessageAsync(friendId, new ChibiInviteMessage
                {
                    Kind = ChibiSocialRules.KindInvite, Code = code, Proto = NetworkRoomManager.ProtocolTag,
                    V = ChibiSocialRules.InviteVersion,
                });
                SetStatus("Invite sent to " + FriendName(friendId) + ". Waiting for them to join...", false);
            }
            catch (Exception e) { SetStatus("Could not send the invite: " + NetworkRoomManager.Explain(e), true); }
        }

        /// <summary>Tell a friend we withdrew our invite (when we cancel the room).</summary>
        public async void CancelOutgoingInvite(string friendId)
        {
            if (!Ready || string.IsNullOrEmpty(friendId)) return;
            try
            {
                await FriendsService.Instance.MessageAsync(friendId, new ChibiInviteMessage
                    { Kind = ChibiSocialRules.KindCancel, Proto = NetworkRoomManager.ProtocolTag, V = ChibiSocialRules.InviteVersion });
            }
            catch (Exception) { /* best effort */ }
        }

        /// <summary>Show "in a match" to friends (Busy) or available (Online).</summary>
        public async void SetInMatch(bool inMatch)
        {
            if (!Ready) return;
            try { await FriendsService.Instance.SetPresenceAvailabilityAsync(inMatch ? Availability.Busy : Availability.Online); }
            catch (Exception) { /* presence is cosmetic */ }
        }

        void SetStatus(string s, bool error)
        {
            Status = s;
            StatusIsError = error;
        }
    }
}
