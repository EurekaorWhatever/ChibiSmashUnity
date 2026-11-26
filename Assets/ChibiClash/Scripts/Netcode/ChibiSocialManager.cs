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

    /// <summary>
    /// Player name, friends list and presence (docs/architecture/netcode/online-social.md), built on Unity
    /// Authentication (player name "Name#1234") and Unity Friends (requests, presence). There are NO invites through the
    /// friends list (user request 2026-11-26: the old friend-message invites were removed). Room invites go by player
    /// name through <see cref="ChibiNameInvites"/> on the CREATE ROOM rooms page. Names are sanitised before drawing.
    /// Main thread only.
    /// </summary>
    /// <example><code>
    /// var social = gameObject.AddComponent&lt;ChibiSocialManager&gt;();
    /// social.Connect();                 // sign in + load friends
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
        /// <summary>
        /// The name the player chose in this game (saved profile, no tag). On sign-in, if Unity's name differs (first
        /// sign-in, or the player renamed while offline) it is pushed to Unity so friends always see the current name.
        /// </summary>
        public Func<string> PreferredName = () => "";
        /// <summary>
        /// Set by the game: true only when the name was claimed worldwide (ChibiNameRegistry) this session. An unclaimed
        /// or taken name is never pushed to Unity's public player name (friends see that name).
        /// </summary>
        public Func<string, bool> NameVerified = _ => true;

        /// <summary>Set by the game: false (e.g. a player under 13) keeps the friends service off.</summary>
        public Func<bool> OnlineAllowed = () => true;

        bool _friendsInitialised, _subscribed;

        // ================================================================== connect

        /// <summary>Sign in, read our name, start the Friends service and load the lists. Safe to call again.</summary>
        public async void Connect()
        {
            if (Working) return;
            if (OnlineAllowed != null && !OnlineAllowed()) { SetStatus(NetworkRoomManager.NotAllowedMessage, true); return; }
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
                // Keep Unity's public name equal to the name chosen in the game (first sign-in or renamed offline).
                string wanted = PreferredName?.Invoke() ?? "";
                if (ChibiSocialRules.ValidateName(wanted) == null && NameVerified(wanted) &&
                    !string.Equals(ChibiSocialRules.BaseName(MyName), wanted, StringComparison.Ordinal))
                {
                    try
                    {
                        string full = await AuthenticationService.Instance.UpdatePlayerNameAsync(wanted);
                        MyName = ChibiSocialRules.SafeDisplayName(full);
                        NeedsName = false;
                    }
                    catch (Exception e) { Debug.LogWarning("ChibiClash name sync: " + NetworkRoomManager.Explain(e)); }
                }
                SetStatus(NeedsName ? Loc.T("friends.choose_name") : Loc.T("account.signed_in_as", MyName), false);
            }
            catch (Exception e) { SetStatus(Loc.T("friends.unavailable", NetworkRoomManager.Explain(e)), true); }
            finally { Working = false; }
        }

        /// <summary>
        /// Set or change our public player name (validated). Unity appends a tag such as #1234 to keep names unique;
        /// the tag can change with the name, so friends see the new name on their list automatically (they are linked
        /// by player id, not by name).
        /// </summary>
        public async void SetName(string name)
        {
            if (Working || !Ready) return;
            name = (name ?? "").Trim();
            string err = ChibiSocialRules.ValidateName(name);
            if (err != null) { SetStatus(err, true); return; }
            if (!NameVerified(name)) return;   // unclaimed name: pushed later, once the name gate confirmed it
            Working = true;
            try
            {
                string full = await AuthenticationService.Instance.UpdatePlayerNameAsync(name);
                MyName = ChibiSocialRules.SafeDisplayName(full);
                NeedsName = false;
                SetStatus(Loc.T("friends.your_name_is", MyName), false);
            }
            catch (Exception e) { SetStatus(Loc.T("friends.set_name_failed", NetworkRoomManager.Explain(e)), true); }
            finally { Working = false; }
        }

        // ================================================================== friends

        /// <summary>Send a friend request by name ("Kira#4821").</summary>
        public async void AddFriend(string name)
        {
            if (Working || !Ready) return;
            string n = ChibiSocialRules.NormalizeFriendName(name);
            if (n == null) { SetStatus(Loc.T("friends.type_tag"), true); return; }
            Working = true;
            try
            {
                await FriendsService.Instance.AddFriendByNameAsync(n);
                RebuildLists();
                SetStatus(Loc.T("friends.request_sent", ChibiSocialRules.SafeDisplayName(n)), false);
            }
            catch (Exception e) { SetStatus(Loc.T("friends.add_failed", ChibiSocialRules.SafeDisplayName(n), NetworkRoomManager.Explain(e)), true); }
            finally { Working = false; }
        }

        /// <summary>Accept a friend request (adding back makes it mutual).</summary>
        public async void AcceptRequest(string memberId) => await Run(() => FriendsService.Instance.AddFriendAsync(memberId), Loc.T("friends.added"));

        /// <summary>Decline a friend request.</summary>
        public async void DeclineRequest(string memberId) =>
            await Run(() => FriendsService.Instance.DeleteIncomingFriendRequestAsync(memberId), Loc.T("friends.declined_request"));

        /// <summary>Remove a friend.</summary>
        public async void RemoveFriend(string memberId) => await Run(() => FriendsService.Instance.DeleteFriendAsync(memberId), Loc.T("friends.removed"));

        /// <summary>Block a player: no friend requests from them any more.</summary>
        public async void Block(string memberId) => await Run(() => FriendsService.Instance.AddBlockAsync(memberId), Loc.T("friends.blocked"));

        async Task Run(Func<Task> call, string done)
        {
            if (Working || !Ready) return;
            Working = true;
            try { await call(); RebuildLists(); SetStatus(done, false); }
            catch (Exception e) { SetStatus(NetworkRoomManager.Explain(e), true); }
            finally { Working = false; }
        }

        // Friends SDK events can arrive from its notification socket, so handlers only set a flag; Update rebuilds the
        // lists on the main thread. Friends messages are not subscribed: nothing is ever received through them.
        volatile bool _listsDirty;
        Action<IRelationshipAddedEvent> _onAdded;
        Action<IRelationshipDeletedEvent> _onDeleted;
        Action<IPresenceUpdatedEvent> _onPresence;

        void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            _onAdded = _ => _listsDirty = true;
            _onDeleted = _ => _listsDirty = true;
            _onPresence = _ => _listsDirty = true;
            FriendsService.Instance.RelationshipAdded += _onAdded;
            FriendsService.Instance.RelationshipDeleted += _onDeleted;
            FriendsService.Instance.PresenceUpdated += _onPresence;
        }

        void OnDestroy()
        {
            if (!_subscribed) return;
            try
            {
                FriendsService.Instance.RelationshipAdded -= _onAdded;
                FriendsService.Instance.RelationshipDeleted -= _onDeleted;
                FriendsService.Instance.PresenceUpdated -= _onPresence;
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

        void Update()
        {
            if (_listsDirty) { _listsDirty = false; RebuildLists(); }
        }

        /// <summary>The signed-in account changed: forget the old account's name, friends and requests (reconnect to load the new ones).</summary>
        public void ResetForAccountChange()
        {
            Ready = false;
            MyName = "";
            NeedsName = false;
            Friends.Clear();
            Requests.Clear();
            SetStatus("", false);
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
