using System;
using System.Text;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// The match invite a player sends to a friend through Unity Friends messaging. Plain public fields (serialised by the
    /// Friends SDK). Everything in it comes from another player's machine, so it is UNTRUSTED: check it with
    /// <see cref="ChibiSocialRules.ValidateInvite"/> before showing or acting on it.
    /// </summary>
    [Serializable]
    public sealed class ChibiInviteMessage
    {
        /// <summary>Message kind: <see cref="ChibiSocialRules.KindInvite"/>, <see cref="ChibiSocialRules.KindDecline"/> or <see cref="ChibiSocialRules.KindCancel"/>.</summary>
        public string Kind = "";
        /// <summary>Lobby code of the private room (invite only).</summary>
        public string Code = "";
        /// <summary>Sender build tag (<see cref="NetworkRoomManager.ProtocolTag"/>): different builds cannot play each other.</summary>
        public string Proto = "";
        /// <summary>Message format version.</summary>
        public int V;
    }

    /// <summary>Why an incoming invite was ignored (for logs / tests; never shown with the attacker's text).</summary>
    public enum ChibiInviteVerdict : byte
    {
        Ok, NotAFriend, Malformed, WrongKind, BadCode, OtherBuild, Flooding, Busy,
    }

    /// <summary>
    /// Pure, unit-tested rules for the social layer (player names, friend invites). Security model: Unity Friends and
    /// Lobby are the authorities for who is a friend and which rooms exist; this class only refuses malformed or abusive
    /// input from other clients and makes names safe to draw. It never trusts anything a peer sends.
    /// </summary>
    public static class ChibiSocialRules
    {
        public const string KindInvite = "invite", KindDecline = "decline", KindCancel = "cancel";
        /// <summary>Invite message format version.</summary>
        public const int InviteVersion = 1;
        /// <summary>Player names: 3..16 characters, letters, digits and underscore (Unity adds "#1234" itself).</summary>
        public const int NameMin = 3, NameMax = 16;
        /// <summary>Longest name we ever draw (name + "#" + 4-digit tag + slack); longer input is cut.</summary>
        public const int DisplayMax = 24;
        /// <summary>INVITE by name to a non-friend: how long we wait for them to accept the friend request (seconds).</summary>
        public const float InviteByNameWaitSeconds = 600f;

        /// <summary>The chosen part of a Unity name: "Kira#4821" -> "Kira" (null -> "").</summary>
        public static string BaseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int h = name.IndexOf('#');
            return h < 0 ? name : name.Substring(0, h);
        }

        // Words a name may not contain (checked case-insensitively, with common digit swaps undone: 0->o 1->i 3->e 4->a 5->s 7->t).
        // A short baseline list; a live game would add the platform's text filter on top.
        static readonly string[] BlockedWords =
        {
            "fuck", "shit", "bitch", "cunt", "dick", "cock", "pussy", "whore", "slut", "porn", "rape", "nazi", "hitler",
            "nigg", "fag", "retard", "admin", "moderator", "official",
        };

        /// <summary>True when <paramref name="name"/> contains a blocked word (also catches "Sh1t", "F_U_C_K").</summary>
        public static bool ContainsBlockedWord(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name)
            {
                char c = char.ToLowerInvariant(ch);
                switch (c)
                {
                    case '0': c = 'o'; break;
                    case '1': c = 'i'; break;
                    case '3': c = 'e'; break;
                    case '4': c = 'a'; break;
                    case '5': c = 's'; break;
                    case '7': c = 't'; break;
                }
                if (c >= 'a' && c <= 'z') sb.Append(c);   // drop separators: "f_u_c_k" -> "fuck"
            }
            string flat = sb.ToString();
            foreach (var w in BlockedWords)
                if (flat.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// How an opponent's name from the match handshake is shown. The handshake name is not bound to an account, so an
        /// UNVERIFIED name shows without its #tag (a stranger cannot pose as a friend's exact "Kira#4821"). A VERIFIED
        /// name (taken from OUR friends list for an invite match) shows in full. Blocked words show as "Player".
        /// </summary>
        public static string PeerDisplayName(string raw, bool verified)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string safe = SafeDisplayName(raw);
            if (ContainsBlockedWord(safe)) return "Player";
            if (verified) return safe;
            string b = BaseName(safe);
            return b.Length == 0 ? "Player" : b;
        }

        /// <summary>A saved player name if it is still valid, else "" (a tampered profile cannot inject anything).</summary>
        public static string CleanSavedName(string saved)
        {
            string s = (saved ?? "").Trim();
            return ValidateName(s) == null ? s : "";
        }
        /// <summary>Lobby codes are short upper-case alphanumerics; accept 4..12 to stay robust to format changes.</summary>
        public const int CodeMin = 4, CodeMax = 12;
        /// <summary>An invite is shown for this long, then dropped (seconds, local clock: a peer's clock is not trusted).</summary>
        public const float InviteLifetime = 60f;
        /// <summary>One invite per friend per this many seconds is accepted (and sent): stops pop-up spam.</summary>
        public const float InviteCooldown = 5f;
        /// <summary>At most this many invites are pending at once.</summary>
        public const int MaxPendingInvites = 3;

        /// <summary>
        /// Check a name the local player wants. Returns null when valid, else a short reason to show.
        /// </summary>
        /// <example><code>string err = ChibiSocialRules.ValidateName(input); if (err == null) await SetName(input);</code></example>
        public static string ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Enter a name.";
            if (name.Length < NameMin) return "Name is too short (at least " + NameMin + " characters).";
            if (name.Length > NameMax) return "Name is too long (at most " + NameMax + " characters).";
            if (ContainsBlockedWord(name)) return "Please choose a different name.";
            foreach (char c in name)
                if (!IsNameChar(c)) return "Use only letters, numbers and _ (no spaces or symbols).";
            return null;
        }

        /// <summary>
        /// Check a friend name typed to send a request: a valid name, optionally followed by "#" and the 4-digit tag Unity
        /// shows (e.g. "Kira#4821"). Returns the trimmed name or null when malformed.
        /// </summary>
        public static string NormalizeFriendName(string input)
        {
            if (input == null) return null;
            string s = input.Trim();
            int hash = s.IndexOf('#');
            string baseName = hash >= 0 ? s.Substring(0, hash) : s;
            if (ValidateName(baseName) != null) return null;
            if (hash >= 0)
            {
                string tag = s.Substring(hash + 1);
                if (tag.Length < 1 || tag.Length > 6) return null;
                foreach (char c in tag) if (c < '0' || c > '9') return null;
            }
            return s;
        }

        /// <summary>
        /// Make any name (ours or a stranger's) safe to draw: keep letters, digits, '_' and '#', drop everything else
        /// (this removes rich-text tags like &lt;size=500&gt; and control characters), cut to <see cref="DisplayMax"/>.
        /// Never returns null; an empty result becomes "Player".
        /// </summary>
        public static string SafeDisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Player";
            var sb = new StringBuilder(Math.Min(name.Length, DisplayMax));
            foreach (char c in name)
            {
                if (sb.Length >= DisplayMax) break;
                if (IsNameChar(c) || c == '#') sb.Append(c);
            }
            return sb.Length == 0 ? "Player" : sb.ToString();
        }

        /// <summary>Upper-case and check a lobby code; returns null when it is not a plausible code.</summary>
        public static string NormalizeCode(string code)
        {
            if (code == null) return null;
            string s = code.Trim().ToUpperInvariant();
            if (s.Length < CodeMin || s.Length > CodeMax) return null;
            foreach (char c in s)
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return null;
            return s;
        }

        /// <summary>
        /// Decide whether an incoming message may be shown. <paramref name="senderIsFriend"/> must come from OUR friends
        /// list (Unity Friends), never from the message. Busy (in a match / room flow) invites are ignored.
        /// </summary>
        public static ChibiInviteVerdict ValidateInvite(ChibiInviteMessage m, bool senderIsFriend, string localProtoTag,
                                                        float now, float lastFromSender, int pendingCount, bool busy)
        {
            if (!senderIsFriend) return ChibiInviteVerdict.NotAFriend;
            if (m == null || m.Kind == null || m.Kind.Length > 16 || (m.Proto != null && m.Proto.Length > 32))
                return ChibiInviteVerdict.Malformed;
            if (m.Kind != KindInvite) return ChibiInviteVerdict.WrongKind;
            if (NormalizeCode(m.Code) == null) return ChibiInviteVerdict.BadCode;
            if (m.Proto != localProtoTag) return ChibiInviteVerdict.OtherBuild;
            if (now - lastFromSender < InviteCooldown || pendingCount >= MaxPendingInvites) return ChibiInviteVerdict.Flooding;
            if (busy) return ChibiInviteVerdict.Busy;
            return ChibiInviteVerdict.Ok;
        }

        static bool IsNameChar(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
    }
}
