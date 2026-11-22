using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// An invite by in-game name that is waiting for the player's answer. Built only by
    /// <see cref="ChibiNameInviteRules.Validate"/>, so every text field is already safe to draw.
    /// </summary>
    public sealed class PendingNameInvite
    {
        /// <summary>Inviter's in-game name (self-reported, not unique, no #tag, blocked words shown as "Player").</summary>
        public readonly string FromName;
        /// <summary>Room code to pass to <c>ChibiRoomService.JoinByCode</c> (normalised, 4..12 upper-case letters / digits).</summary>
        public readonly string RoomCode;
        /// <summary>Room name, sanitised by <see cref="ChibiSocialRules.SafeRoomName"/>.</summary>
        public readonly string RoomName;
        /// <summary>Local <c>Time.unscaledTime</c> when the invite is dropped (a peer can never push it past the lifetime).</summary>
        public readonly float ExpiresAt;
        /// <summary>Inviter's player id (set by the Lobby service, not by the inviter): duplicates per inviter collapse on it.</summary>
        public readonly string InviterId;
        /// <summary>Id of the beacon lobby this invite came from.</summary>
        public readonly string BeaconId;

        /// <summary>Make an invite (normally only <see cref="ChibiNameInviteRules.Validate"/> does this).</summary>
        public PendingNameInvite(string fromName, string roomCode, string roomName, float expiresAt, string inviterId, string beaconId)
        {
            FromName = fromName ?? "";
            RoomCode = roomCode ?? "";
            RoomName = roomName ?? "";
            ExpiresAt = expiresAt;
            InviterId = inviterId ?? "";
            BeaconId = beaconId ?? "";
        }
    }

    /// <summary>
    /// The raw fields of an invite beacon lobby (or the fields we are about to publish). Plain strings so the rules stay
    /// testable without the Unity Lobby SDK. Everything read from the network is UNTRUSTED.
    /// </summary>
    public struct ChibiNameBeacon
    {
        public string LobbyId, LobbyName, HostId;
        /// <summary>Indexed (S5) value: <see cref="ChibiNameInviteRules.IndexValue"/> of the invitee.</summary>
        public string Index;
        public string Proto, Version;
        /// <summary>
        /// "code|inviter name|room name", XOR a keystream derived from the invitee's name and <see cref="Salt"/>, as hex
        /// (see <see cref="ChibiNameInviteRules.MaskPayload"/>). Nothing readable without the name.
        /// </summary>
        public string Payload;
        public string Salt;
        /// <summary>Unix seconds (UTC) when the inviter deletes the beacon.</summary>
        public string Expiry;
    }

    /// <summary>Why a beacon or an invite was not added (for logs and tests; never shown with the stranger's text).</summary>
    public enum ChibiNameInviteVerdict : byte
    {
        Ok, NotABeacon, Malformed, OtherBuild, NotForMe, Own, BadPayload, Expired, Dismissed, Full, Known, Duplicate,
    }

    /// <summary>Why our outgoing beacon should be deleted now.</summary>
    public enum ChibiNameInviteEnd : byte
    {
        None, Expired, RoomFull, RoomClosed,
    }

    /// <summary>A beacon we answered or let expire (ignored from now on), and how long its inviter stays muted.</summary>
    public struct ChibiDismissedInvite
    {
        public string BeaconId, InviterId;
        /// <summary>Local unscaled time until which NEW beacons from this inviter are ignored (decline only).</summary>
        public float MuteUntil;
    }

    /// <summary>
    /// Pure, unit-tested rules for inviting a player into a private room by their in-game name
    /// (docs/architecture/netcode/online-rooms.md "Invite by name"; service: <see cref="ChibiNameInvites"/>).
    /// <para>
    /// Design: the inviter publishes a short-lived PUBLIC "beacon" lobby. Its indexed field S5 holds a hash of the
    /// invitee's lower-case name and the build's protocol tag; the invitee queries S5 == hash(my name). The room code,
    /// the inviter's name and the room name travel in one payload masked with a key derived from the same name, so a
    /// client that lists every public lobby reads nothing without guessing the name. This is NOT secrecy: names are
    /// self-reported, not unique and low-entropy (a dictionary of common names breaks the hash). Two players named
    /// "Kira" both see the invite (the inviter's name is shown), and anyone who knows the invitee's name can read the
    /// code during the 60 s window, exactly like the invitee. The worst case is a stranger taking the second seat of the
    /// 2-seat room, which the host sees and can close.
    /// </para>
    /// </summary>
    public static class ChibiNameInviteRules
    {
        /// <summary>Lobby name of every beacon. Matchmaking and room lobbies never use it.</summary>
        public const string Marker = "ChibiInvite";
        /// <summary>Lobby data keys (all Public visibility). Only <see cref="KeyIndex"/> is indexed (S5).</summary>
        public const string KeyIndex = "inv", KeyProto = "proto", KeyVersion = "v", KeyPayload = "pl", KeySalt = "s",
                            KeyExpiry = "exp";
        /// <summary>Beacon format version (bump when the fields change; other versions are ignored). 2: masked payload.</summary>
        public const int FormatVersion = 2;
        /// <summary>
        /// Beacon seats. 2, not 1: the inviter holds one and the other is closed by a random password nobody knows. A
        /// lobby with a free seat is never "full", so it shows in queries whatever the service's default for full lobbies.
        /// </summary>
        public const int BeaconSeats = 2;
        /// <summary>The inviter deletes the beacon after this long; the invitee never shows it longer (seconds).</summary>
        public const float LifetimeSeconds = 60f;
        /// <summary>
        /// Beacon heartbeats (a lobby with no heartbeat for 30 s goes inactive and leaves query results): the first at
        /// <see cref="FirstHeartbeatSeconds"/>, then every <see cref="HeartbeatSeconds"/>. More than 30 s apart, so the
        /// beacon adds at most one heartbeat to any 30 s window next to the host's room heartbeats (Lobby allows 5 per
        /// 30 s; two hosted rooms already use 4). The 0.5 s gap near the end only matters to a poll landing in it.
        /// </summary>
        public const float FirstHeartbeatSeconds = 25f, HeartbeatSeconds = 30.5f;
        /// <summary>Receiving: one query per <see cref="PollSeconds"/> + up to <see cref="PollJitterSeconds"/> random.</summary>
        public const float PollSeconds = 5f, PollJitterSeconds = 1f;
        /// <summary>After "too many requests": wait this long plus up to 5 s random (same as NetworkRoomManager).</summary>
        public const float RateLimitBackoffSeconds = 15f;
        /// <summary>At most one Send per this many seconds (stops spam and keeps Create well under its rate limit).</summary>
        public const float SendCooldownSeconds = 5f;
        /// <summary>Declining an invite ignores new invites from the same player id for this long.</summary>
        public const float DeclineMuteSeconds = 60f;
        /// <summary>
        /// SEC-003: this many declines within <see cref="DeclineWindowSeconds"/> pause ALL incoming invites for
        /// <see cref="PauseSeconds"/>, whatever the sender's player id (throwaway accounts make per-id mutes useless).
        /// </summary>
        public const int DeclinesBeforePause = 3;
        public const float DeclineWindowSeconds = 600f, PauseSeconds = 600f;
        /// <summary>A just-seen invite is shown at least this long even if the clocks disagree (the beacon's removal still ends it).</summary>
        public const float MinShowSeconds = 10f;
        /// <summary>Allowed disagreement between two players' clocks (seconds): older expiries are stale, later ones malformed.</summary>
        public const long ClockSkewSeconds = 90;
        /// <summary>At most this many invites wait for an answer.</summary>
        public const int MaxPending = ChibiSocialRules.MaxPendingInvites;
        /// <summary>Any beacon field except the payload longer than this is malformed.</summary>
        public const int MaxFieldChars = 64;
        /// <summary>Longest payload before masking: code + 2 separators + name + room name (hex doubles it).</summary>
        public const int MaxPayloadBytes = ChibiSocialRules.CodeMax + 2 + ChibiSocialRules.DisplayMax + ChibiSocialRules.RoomNameMax;
        /// <summary>Answered / expired beacons remembered (oldest forgotten first).</summary>
        public const int MaxDismissed = 16;
        /// <summary>Beacons read per query, newest first (more than <see cref="MaxPending"/>).</summary>
        public const int QueryCount = 10;

        const string HexDigits = "0123456789abcdef";
        const char Sep = '|';

        /// <summary>
        /// The name a player is invited by: the in-game name without any "#tag", trimmed and lower-case. Null when it is
        /// not a valid in-game name (3..16 letters, digits, _; no blocked words).
        /// </summary>
        /// <example><code>ChibiNameInviteRules.NormalizeName(" Pangya123 ")   // "pangya123"</code></example>
        public static string NormalizeName(string input)
        {
            if (input == null) return null;
            string s = ChibiSocialRules.BaseName(input.Trim()).Trim();
            if (ChibiSocialRules.ValidateName(s) != null) return null;
            return s.ToLowerInvariant();
        }

        /// <summary>
        /// Indexed S5 value for invites to <paramref name="normName"/> in build <paramref name="proto"/>: "ci" + version +
        /// ":" + 32 hex characters of SHA-256. The plain name is not published. Null for empty input.
        /// </summary>
        public static string IndexValue(string normName, string proto)
        {
            if (string.IsNullOrEmpty(normName) || string.IsNullOrEmpty(proto)) return null;
            byte[] h = Sha("ChibiClash.invite.name|" + proto + "|" + normName);
            var sb = new StringBuilder(4 + 32);
            sb.Append("ci").Append(FormatVersion).Append(':');
            for (int i = 0; i < 16; i++) sb.Append(HexDigits[h[i] >> 4]).Append(HexDigits[h[i] & 15]);
            return sb.ToString();
        }

        /// <summary>
        /// Mask "code|from|room" for <paramref name="normName"/>: our name and the room name are sanitised first, then
        /// XOR a SHA-256 keystream of (protocol tag, name, salt), as lower-case hex. Null when the code is invalid.
        /// </summary>
        public static string MaskPayload(string code, string fromName, string roomName, string salt, string normName, string proto)
        {
            string c = ChibiSocialRules.NormalizeCode(code);
            if (c == null || string.IsNullOrEmpty(normName) || !IsSalt(salt)) return null;
            string plain = c + Sep + ChibiSocialRules.BaseName(ChibiSocialRules.SafeDisplayName(fromName)) + Sep +
                           ChibiSocialRules.SafeRoomName(roomName);
            return MaskText(plain, salt, normName, proto);
        }

        /// <summary>
        /// Mask any text as-is (no sanitising; <see cref="MaskPayload"/> sanitises first). Tests use it to play a hostile
        /// peer. Null for invalid input or text longer than <see cref="MaxPayloadBytes"/>.
        /// </summary>
        public static string MaskText(string plain, string salt, string normName, string proto)
        {
            if (plain == null || plain.Length > MaxPayloadBytes || string.IsNullOrEmpty(normName) || !IsSalt(salt)) return null;
            byte[] key = KeyStream(salt, normName, proto, plain.Length);
            var sb = new StringBuilder(plain.Length * 2);
            for (int i = 0; i < plain.Length; i++)
            {
                int v = (plain[i] ^ key[i]) & 0xFF;
                sb.Append(HexDigits[v >> 4]).Append(HexDigits[v & 15]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Undo <see cref="MaskPayload"/> and sanitise the parts. False unless the result is printable ASCII with exactly
        /// two separators and a plausible room code (a wrong name gives garbage, which fails).
        /// </summary>
        public static bool TryUnmaskPayload(string masked, string salt, string normName, string proto,
                                            out string code, out string fromName, out string roomName)
        {
            code = fromName = roomName = null;
            if (masked == null || !IsSalt(salt) || string.IsNullOrEmpty(normName)) return false;
            int n = masked.Length / 2;
            if ((masked.Length & 1) != 0 || n < ChibiSocialRules.CodeMin + 2 || n > MaxPayloadBytes) return false;
            byte[] key = KeyStream(salt, normName, proto, n);
            var chars = new char[n];
            int sep1 = -1, sep2 = -1;
            for (int i = 0; i < n; i++)
            {
                int hi = Nibble(masked[2 * i]), lo = Nibble(masked[2 * i + 1]);
                if (hi < 0 || lo < 0) return false;
                int ch = ((hi << 4) | lo) ^ key[i];
                if (ch < 0x20 || ch > 0x7E) return false;
                chars[i] = (char)ch;
                if (ch != Sep) continue;
                if (sep1 < 0) sep1 = i;
                else if (sep2 < 0) sep2 = i;
                else return false;
            }
            if (sep2 < 0) return false;
            string s = new string(chars);
            code = ChibiSocialRules.NormalizeCode(s.Substring(0, sep1));
            if (code == null) return false;
            string from = s.Substring(sep1 + 1, sep2 - sep1 - 1);
            fromName = from.Length == 0 ? "Player" : ChibiSocialRules.PeerDisplayName(from, verified: false);
            roomName = ChibiSocialRules.SafeRoomName(s.Substring(sep2 + 1));
            return true;
        }

        /// <summary>
        /// The fields to publish for an invite to <paramref name="friendName"/>. False when the friend name, room code or
        /// salt is invalid. Our own name and the room name are sanitised and masked before they leave this machine.
        /// </summary>
        public static bool TryBuildBeacon(string friendName, string proto, string roomCode, string salt, string myName,
                                          string roomName, long nowUnix, out ChibiNameBeacon beacon)
        {
            beacon = default;
            string norm = NormalizeName(friendName);
            if (norm == null || string.IsNullOrEmpty(proto) || proto.Length > MaxFieldChars || !IsSalt(salt)) return false;
            string masked = MaskPayload(roomCode, myName, roomName, salt, norm, proto);
            if (masked == null) return false;
            beacon = new ChibiNameBeacon
            {
                LobbyName = Marker,
                Index = IndexValue(norm, proto),
                Proto = proto,
                Version = FormatVersion.ToString(CultureInfo.InvariantCulture),
                Payload = masked,
                Salt = salt,
                Expiry = (nowUnix + (long)LifetimeSeconds).ToString(CultureInfo.InvariantCulture),
            };
            return true;
        }

        /// <summary>
        /// Check a beacon from the Lobby query and, when it is a live invite for us, build the invite to show.
        /// <paramref name="myIndex"/> = <see cref="IndexValue"/> of <paramref name="myNormName"/>; <paramref name="myId"/>
        /// is our player id (our own beacons are ignored). <paramref name="nowUnix"/> is the local UTC clock: only used
        /// with a skew allowance, and a peer can never make an invite last longer than <see cref="LifetimeSeconds"/>.
        /// </summary>
        public static ChibiNameInviteVerdict Validate(in ChibiNameBeacon b, string myIndex, string myNormName, string proto,
                                                      string myId, float nowUnscaled, long nowUnix, out PendingNameInvite invite)
        {
            invite = null;
            if (b.LobbyName != Marker) return ChibiNameInviteVerdict.NotABeacon;
            if (string.IsNullOrEmpty(b.LobbyId) || string.IsNullOrEmpty(b.HostId) || !Fits(b.LobbyId) || !Fits(b.HostId) ||
                !Fits(b.Index) || !Fits(b.Proto) || !Fits(b.Version) || !Fits(b.Salt) || !Fits(b.Expiry) ||
                b.Payload == null || b.Payload.Length > 2 * MaxPayloadBytes)
                return ChibiNameInviteVerdict.Malformed;
            if (b.Version != FormatVersion.ToString(CultureInfo.InvariantCulture) || string.IsNullOrEmpty(proto) ||
                !string.Equals(b.Proto, proto, StringComparison.Ordinal))
                return ChibiNameInviteVerdict.OtherBuild;
            if (string.IsNullOrEmpty(myIndex) || !string.Equals(b.Index, myIndex, StringComparison.Ordinal))
                return ChibiNameInviteVerdict.NotForMe;
            if (!string.IsNullOrEmpty(myId) && b.HostId == myId) return ChibiNameInviteVerdict.Own;
            if (!IsSalt(b.Salt)) return ChibiNameInviteVerdict.Malformed;
            if (b.Expiry.Length > 12 || !long.TryParse(b.Expiry, NumberStyles.None, CultureInfo.InvariantCulture, out long exp))
                return ChibiNameInviteVerdict.Malformed;
            long left = exp - nowUnix;
            // SEC-001: an expiry further ahead than any honest beacon (lifetime + clock skew) is forged.
            if (left > (long)LifetimeSeconds + ClockSkewSeconds) return ChibiNameInviteVerdict.Malformed;
            if (left < -ClockSkewSeconds) return ChibiNameInviteVerdict.Expired;
            if (!TryUnmaskPayload(b.Payload, b.Salt, myNormName, proto, out string code, out string from, out string room))
                return ChibiNameInviteVerdict.BadPayload;
            float show = left < MinShowSeconds ? MinShowSeconds : left > LifetimeSeconds ? LifetimeSeconds : left;
            invite = new PendingNameInvite(from, code, room, nowUnscaled + show, b.HostId, b.LobbyId);
            return ChibiNameInviteVerdict.Ok;
        }

        /// <summary>
        /// Add a validated invite to the pending list. Results come NEWEST first, so within one query only the first
        /// beacon per inviter counts (<paramref name="hostsThisQuery"/>, cleared by the caller before each query; null =
        /// no per-query check). The same beacon again keeps its first expiry (<see cref="ChibiNameInviteVerdict.Known"/>);
        /// a newer beacon from the same inviter replaces the old one; answered / expired beacons and muted inviters are
        /// ignored; at most <see cref="MaxPending"/>. Returns Ok when it is newly shown.
        /// </summary>
        public static ChibiNameInviteVerdict Offer(List<PendingNameInvite> pending, PendingNameInvite incoming,
                                                   List<ChibiDismissedInvite> dismissed, float now,
                                                   HashSet<string> hostsThisQuery = null)
        {
            if (pending == null || incoming == null) return ChibiNameInviteVerdict.Malformed;
            if (hostsThisQuery != null && !hostsThisQuery.Add(incoming.InviterId)) return ChibiNameInviteVerdict.Duplicate;
            if (IsDismissed(dismissed, incoming.BeaconId, incoming.InviterId, now)) return ChibiNameInviteVerdict.Dismissed;
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].BeaconId == incoming.BeaconId) return ChibiNameInviteVerdict.Known;
                if (pending[i].InviterId == incoming.InviterId) { pending[i] = incoming; return ChibiNameInviteVerdict.Ok; }
            }
            if (pending.Count >= MaxPending) return ChibiNameInviteVerdict.Full;
            pending.Add(incoming);
            return ChibiNameInviteVerdict.Ok;
        }

        /// <summary>True when the beacon was answered / expired already, or its inviter is muted until after <paramref name="now"/>.</summary>
        public static bool IsDismissed(List<ChibiDismissedInvite> dismissed, string beaconId, string inviterId, float now)
        {
            if (dismissed == null) return false;
            for (int i = 0; i < dismissed.Count; i++)
            {
                if (dismissed[i].BeaconId == beaconId) return true;
                if (dismissed[i].InviterId == inviterId && now < dismissed[i].MuteUntil) return true;
            }
            return false;
        }

        /// <summary>Remember an answered or expired invite (decline: pass a mute end; otherwise 0). Keeps at most <see cref="MaxDismissed"/>.</summary>
        public static void Dismiss(List<ChibiDismissedInvite> dismissed, PendingNameInvite invite, float muteUntil)
        {
            if (dismissed == null || invite == null) return;
            dismissed.Add(new ChibiDismissedInvite { BeaconId = invite.BeaconId, InviterId = invite.InviterId, MuteUntil = muteUntil });
            while (dismissed.Count > MaxDismissed) dismissed.RemoveAt(0);
        }

        /// <summary>
        /// Drop expired invites (SEC-001: they are added to <paramref name="dismissed"/>, so the same live beacon can never
        /// come back with a fresh lifetime), and, when <paramref name="seenBeacons"/> is the result of a SUCCESSFUL query,
        /// invites whose beacon is gone (cancelled, room full or closed). Pass null when no fresh query result exists.
        /// </summary>
        public static int Prune(List<PendingNameInvite> pending, float now, HashSet<string> seenBeacons,
                                List<ChibiDismissedInvite> dismissed)
        {
            if (pending == null) return 0;
            int removed = 0;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                bool expired = pending[i].ExpiresAt <= now;
                if (!expired && (seenBeacons == null || seenBeacons.Contains(pending[i].BeaconId))) continue;
                if (expired) Dismiss(dismissed, pending[i], 0f);
                pending.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// SEC-003: record a decline at <paramref name="now"/> and return the new "paused until" time: after
        /// <see cref="DeclinesBeforePause"/> declines within <see cref="DeclineWindowSeconds"/>, all incoming invites
        /// pause for <see cref="PauseSeconds"/> (the decline history then starts again).
        /// </summary>
        public static float RecordDecline(List<float> declineTimes, float now, float pausedUntil)
        {
            if (declineTimes == null) return pausedUntil;
            for (int i = declineTimes.Count - 1; i >= 0; i--)
                if (now - declineTimes[i] > DeclineWindowSeconds) declineTimes.RemoveAt(i);
            declineTimes.Add(now);
            if (declineTimes.Count < DeclinesBeforePause) return pausedUntil;
            declineTimes.Clear();
            return Math.Max(pausedUntil, now + PauseSeconds);
        }

        /// <summary>
        /// Should our outgoing beacon end now? <paramref name="roomsKnown"/> = the room list is loaded (else the room
        /// state is unknown and only the expiry counts); <paramref name="roomFound"/> / <paramref name="roomPlayers"/>
        /// describe the invited room in it.
        /// </summary>
        public static ChibiNameInviteEnd OutgoingEnd(float now, float expiresAt, bool roomsKnown, bool roomFound, int roomPlayers)
        {
            if (now >= expiresAt) return ChibiNameInviteEnd.Expired;
            if (!roomsKnown) return ChibiNameInviteEnd.None;
            if (!roomFound) return ChibiNameInviteEnd.RoomClosed;
            if (roomPlayers >= ChibiRoomRules.Seats) return ChibiNameInviteEnd.RoomFull;
            return ChibiNameInviteEnd.None;
        }

        /// <summary>Seconds to the next query for a random value <paramref name="random01"/> in [0,1].</summary>
        public static float PollDelay(float random01)
        {
            if (!(random01 >= 0f)) random01 = 0f;
            if (random01 > 1f) random01 = 1f;
            return PollSeconds + PollJitterSeconds * random01;
        }

        /// <summary>A salt is 4..16 lower-case hex characters.</summary>
        public static bool IsSalt(string s)
        {
            if (s == null || s.Length < 4 || s.Length > 16) return false;
            foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        /// <summary>A fresh random salt (8 hex characters).</summary>
        public static string NewSalt() => Guid.NewGuid().ToString("N").Substring(0, 8);

        static bool Fits(string s) => s != null && s.Length <= MaxFieldChars;

        static int Nibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary><paramref name="n"/> key bytes: SHA-256 of (tag, name, salt, block number), block after block.</summary>
        static byte[] KeyStream(string salt, string normName, string proto, int n)
        {
            var key = new byte[n];
            using (var sha = SHA256.Create())
                for (int block = 0, filled = 0; filled < n; block++)
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(
                        "ChibiClash.invite.payload|" + proto + "|" + normName + "|" + salt + "|" + block.ToString(CultureInfo.InvariantCulture)));
                    int take = Math.Min(h.Length, n - filled);
                    Array.Copy(h, 0, key, filled, take);
                    filled += take;
                }
            return key;
        }

        static byte[] Sha(string s)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(s));
        }
    }
}
