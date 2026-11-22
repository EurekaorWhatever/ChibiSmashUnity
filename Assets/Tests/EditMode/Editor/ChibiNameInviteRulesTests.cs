// Story type: Logic (invite by name, social security rules) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Spec: docs/architecture/netcode/online-rooms.md "Invite by name". Beacon lobbies come from strangers: these rules decide
// what may be shown. The Lobby service itself needs a linked cloud project (not unit-testable).
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>ChibiNameInviteRules: name normalisation, beacon build / validation, pending list, pause, outgoing end.</summary>
    public class ChibiNameInviteRulesTests
    {
        const string Proto = "p3r23";
        const string Salt = "a1b2c3d4";
        const string Code = "K7Q2PX";
        const long NowUnix = 1_800_000_000;
        const float Now = 100f;
        const string MyId = "player-me";

        static ChibiNameBeacon Beacon(string friend = "Kira", string from = "Pangya123", string room = "Pangya's room",
                                      string code = Code, long sentAt = NowUnix)
        {
            Assert.IsTrue(ChibiNameInviteRules.TryBuildBeacon(friend, Proto, code, Salt, from, room, sentAt, out var b));
            b.LobbyId = "lobby-1";
            b.HostId = "player-inviter";
            return b;
        }

        /// <summary>A beacon whose payload a hostile peer wrote by hand (no sanitising on its side).</summary>
        static ChibiNameBeacon RawBeacon(string plainPayload)
        {
            var b = Beacon();
            b.Payload = ChibiNameInviteRules.MaskText(plainPayload, Salt, "kira", Proto);
            return b;
        }

        static ChibiNameInviteVerdict Check(ChibiNameBeacon b, out PendingNameInvite inv, string me = "kira", string myId = MyId,
                                            long nowUnix = NowUnix) =>
            ChibiNameInviteRules.Validate(b, ChibiNameInviteRules.IndexValue(me, Proto), me, Proto, myId, Now, nowUnix, out inv);

        static PendingNameInvite Inv(string beacon, string inviter, float expires = Now + 60f) =>
            new PendingNameInvite("From" + inviter, Code, "Room", expires, inviter, beacon);

        // ================================================================== names and index

        [Test]
        public void NormalizeName_MixedCaseAndSpaces_LowerCaseTrimmed()
        {
            // Arrange / Act
            string n = ChibiNameInviteRules.NormalizeName("  Pangya123 ");
            // Assert
            Assert.AreEqual("pangya123", n);
        }

        [Test]
        public void NormalizeName_UnityTagSuffix_TagDropped() => Assert.AreEqual("kira", ChibiNameInviteRules.NormalizeName("Kira#4821"));

        [TestCase(null)] [TestCase("")] [TestCase("ab")] [TestCase("Bad Name")] [TestCase("<b>Kira</b>")]
        [TestCase("ABCDEFGHIJKLMNOPQ")] [TestCase("Sh1tLord")]
        public void NormalizeName_InvalidNames_Null(string name) => Assert.IsNull(ChibiNameInviteRules.NormalizeName(name));

        [Test]
        public void IndexValue_SameNameDifferentCase_Equal()
        {
            // Arrange
            string a = ChibiNameInviteRules.IndexValue(ChibiNameInviteRules.NormalizeName("KIRA"), Proto);
            string b = ChibiNameInviteRules.IndexValue(ChibiNameInviteRules.NormalizeName("kira"), Proto);
            // Assert
            Assert.AreEqual(a, b);
            StringAssert.StartsWith("ci" + ChibiNameInviteRules.FormatVersion + ":", a);
            Assert.LessOrEqual(a.Length, ChibiNameInviteRules.MaxFieldChars);
        }

        [Test]
        public void IndexValue_OtherBuild_Differs() =>
            Assert.AreNotEqual(ChibiNameInviteRules.IndexValue("kira", Proto), ChibiNameInviteRules.IndexValue("kira", "p3r24"));

        // ================================================================== build and validate

        [Test]
        public void BuildThenValidate_ForInvitee_OkWithSameCodeAndNames()
        {
            // Arrange
            var b = Beacon();
            // Act
            var v = Check(b, out var inv);
            // Assert
            Assert.AreEqual(ChibiNameInviteVerdict.Ok, v);
            Assert.AreEqual(Code, inv.RoomCode);
            Assert.AreEqual("Pangya123", inv.FromName);
            Assert.AreEqual("Pangya's room", inv.RoomName);
            Assert.AreEqual("player-inviter", inv.InviterId);
            Assert.AreEqual(Now + ChibiNameInviteRules.LifetimeSeconds, inv.ExpiresAt, 0.001f);
        }

        [Test]
        public void BeaconFields_ListedByAStranger_NoReadableNameCodeOrRoom()
        {
            // Arrange (SEC-004): every value a client that lists public lobbies can read
            var b = Beacon(friend: "Kira", from: "Pangya123", room: "Pangya's room");
            string[] published = { b.LobbyName, b.Index, b.Proto, b.Version, b.Payload, b.Salt, b.Expiry };
            // Act / Assert
            foreach (var field in published)
            {
                string f = field.ToLowerInvariant();
                Assert.AreEqual(-1, f.IndexOf("kira", System.StringComparison.Ordinal), field);
                Assert.AreEqual(-1, f.IndexOf("pangya", System.StringComparison.Ordinal), field);
                Assert.AreEqual(-1, f.IndexOf("k7q2px", System.StringComparison.Ordinal), field);
            }
        }

        [TestCase("ab", Code, Salt)] [TestCase("Kira", "no!", Salt)] [TestCase("Kira", Code, "zz")] [TestCase("Kira", Code, null)]
        [TestCase("Kira", Code, "A1B2C3D4")]
        public void TryBuildBeacon_InvalidInput_False(string friend, string code, string salt) =>
            Assert.IsFalse(ChibiNameInviteRules.TryBuildBeacon(friend, Proto, code, salt, "Me", "Room", NowUnix, out _));

        [Test]
        public void Validate_OtherPlayersName_NotForMe() =>
            Assert.AreEqual(ChibiNameInviteVerdict.NotForMe, Check(Beacon(friend: "Kira"), out _, me: "lyra"));

        [Test]
        public void Validate_OwnBeacon_Own() =>
            Assert.AreEqual(ChibiNameInviteVerdict.Own, Check(Beacon(), out _, myId: "player-inviter"));

        [Test]
        public void Validate_MatchmakingLobbyName_NotABeacon()
        {
            var b = Beacon();
            b.LobbyName = "Chibi Clash " + Proto;
            Assert.AreEqual(ChibiNameInviteVerdict.NotABeacon, Check(b, out _));
        }

        [Test]
        public void Validate_OtherProtocolTag_OtherBuild()
        {
            var b = Beacon();
            b.Proto = "p2r17";
            Assert.AreEqual(ChibiNameInviteVerdict.OtherBuild, Check(b, out _));
        }

        [Test]
        public void Validate_OldFormatVersion_OtherBuild()
        {
            var b = Beacon();
            b.Version = "1";
            Assert.AreEqual(ChibiNameInviteVerdict.OtherBuild, Check(b, out _));
        }

        [Test]
        public void Validate_TamperedPayload_RejectedOrNotTheCode()
        {
            // Arrange: change the first masked byte
            var b = Beacon();
            b.Payload = (b.Payload.StartsWith("ff") ? "00" : "ff") + b.Payload.Substring(2);
            // Act
            var v = Check(b, out var inv);
            // Assert
            Assert.IsTrue(v == ChibiNameInviteVerdict.BadPayload || (v == ChibiNameInviteVerdict.Ok && inv.RoomCode != Code));
        }

        [TestCase("zz")] [TestCase("abc")] [TestCase("")] [TestCase("0102030405060708090a0b0c0d0e0f1011121314151617g9")]
        public void Validate_NonHexOrShortPayload_BadPayload(string masked)
        {
            var b = Beacon();
            b.Payload = masked;
            Assert.AreEqual(ChibiNameInviteVerdict.BadPayload, Check(b, out _));
        }

        [Test]
        public void Validate_PayloadForAnotherName_BadPayload()
        {
            // Arrange: the index says "kira" but the payload was masked for "lyra"
            var b = Beacon();
            b.Payload = ChibiNameInviteRules.MaskPayload(Code, "Pangya123", "Room", Salt, "lyra", Proto);
            // Act / Assert
            Assert.AreEqual(ChibiNameInviteVerdict.BadPayload, Check(b, out _));
        }

        [TestCase("K7Q2PX|A|B|C")] [TestCase("K7Q2PX-Kira-Room")] [TestCase("K7Q2PX|Kira\u0001|Room")] [TestCase("bad!|Kira|Room")]
        public void Validate_HostilePayloadShape_BadPayload(string plain) =>
            Assert.AreEqual(ChibiNameInviteVerdict.BadPayload, Check(RawBeacon(plain), out _));

        [Test]
        public void Validate_OversizedField_Malformed()
        {
            var b = Beacon();
            b.Proto = new string('p', ChibiNameInviteRules.MaxFieldChars + 1);
            Assert.AreEqual(ChibiNameInviteVerdict.Malformed, Check(b, out _));
        }

        [Test]
        public void Validate_OversizedPayload_Malformed()
        {
            var b = Beacon();
            b.Payload = new string('a', 2 * ChibiNameInviteRules.MaxPayloadBytes + 2);
            Assert.AreEqual(ChibiNameInviteVerdict.Malformed, Check(b, out _));
        }

        [Test]
        public void Validate_MissingFields_Malformed()
        {
            var b = Beacon();
            b.Payload = null;
            b.Salt = null;
            Assert.AreEqual(ChibiNameInviteVerdict.Malformed, Check(b, out _));
        }

        [Test]
        public void Validate_MissingHostId_Malformed()
        {
            var b = Beacon();
            b.HostId = "";
            Assert.AreEqual(ChibiNameInviteVerdict.Malformed, Check(b, out _));
        }

        [Test]
        public void Validate_RichTextNamesFromHostilePeer_Sanitised()
        {
            // Arrange: a hostile peer masks markup straight into its payload
            var b = RawBeacon("K7Q2PX|<size=500>Kira</size>#1234|<color=red>Dojo</color>");
            // Act
            var v = Check(b, out var inv);
            // Assert
            Assert.AreEqual(ChibiNameInviteVerdict.Ok, v);
            Assert.AreEqual("size500Kirasize", inv.FromName);   // tags lose their brackets; the #tag is never shown
            Assert.AreEqual("Dojo", inv.RoomName);              // whole markup tags are dropped
        }

        [Test]
        public void Validate_BlockedInviterName_ShownAsPlayer()
        {
            Check(RawBeacon("K7Q2PX|Sh1tLord|Room"), out var inv);
            Assert.AreEqual("Player", inv.FromName);
        }

        [Test]
        public void Validate_EmptyNames_Defaults()
        {
            Check(RawBeacon("K7Q2PX||"), out var inv);
            Assert.AreEqual("Player", inv.FromName);
            Assert.AreEqual(ChibiSocialRules.FallbackRoomName, inv.RoomName);
        }

        [Test]
        public void Validate_LongExpiredBeacon_Expired() =>
            Assert.AreEqual(ChibiNameInviteVerdict.Expired, Check(Beacon(sentAt: NowUnix - 600), out _));

        [Test]
        public void Validate_ForgedFarFutureExpiry_Malformed() =>
            // SEC-001: a beacon claiming to live a day cannot come from an honest client
            Assert.AreEqual(ChibiNameInviteVerdict.Malformed, Check(Beacon(sentAt: NowUnix + 86_400), out _));

        [Test]
        public void Validate_ExpiryAheadWithinClockSkew_CappedAtLifetime()
        {
            // Arrange: the inviter's clock is 80 s ahead (expiry 140 s away, within lifetime + skew)
            var b = Beacon(sentAt: NowUnix + 80);
            // Act
            var v = Check(b, out var inv);
            // Assert
            Assert.AreEqual(ChibiNameInviteVerdict.Ok, v);
            Assert.AreEqual(Now + ChibiNameInviteRules.LifetimeSeconds, inv.ExpiresAt, 0.001f);
        }

        [Test]
        public void Validate_SmallClockSkew_ShownForMinimum()
        {
            // Arrange: our clock is 70 s ahead of the inviter's (expiry looks 10 s past, within the skew allowance)
            var b = Beacon(sentAt: NowUnix - 70);
            // Act
            var v = Check(b, out var inv);
            // Assert
            Assert.AreEqual(ChibiNameInviteVerdict.Ok, v);
            Assert.AreEqual(Now + ChibiNameInviteRules.MinShowSeconds, inv.ExpiresAt, 0.001f);
        }

        [TestCase("-5")] [TestCase("12x")] [TestCase("9999999999999")] [TestCase("")]
        public void Validate_UnparsableExpiry_Malformed(string exp)
        {
            var b = Beacon();
            b.Expiry = exp;
            Assert.AreEqual(ChibiNameInviteVerdict.Malformed, Check(b, out _));
        }

        // ================================================================== pending list

        [Test]
        public void Offer_FourthInviter_Full()
        {
            // Arrange
            var pending = new List<PendingNameInvite>();
            for (int i = 0; i < ChibiNameInviteRules.MaxPending; i++)
                ChibiNameInviteRules.Offer(pending, Inv("b" + i, "p" + i), null, Now);
            // Act
            var v = ChibiNameInviteRules.Offer(pending, Inv("b9", "p9"), null, Now);
            // Assert
            Assert.AreEqual(ChibiNameInviteVerdict.Full, v);
            Assert.AreEqual(ChibiNameInviteRules.MaxPending, pending.Count);
        }

        [Test]
        public void Offer_OlderBeaconSameInviterSameQuery_Duplicate()
        {
            // Arrange (SEC-002): results come newest first; the first beacon per inviter wins
            var pending = new List<PendingNameInvite>();
            var hosts = new HashSet<string>();
            ChibiNameInviteRules.Offer(pending, Inv("newest", "p1"), null, Now, hosts);
            // Act
            var v = ChibiNameInviteRules.Offer(pending, Inv("older", "p1"), null, Now, hosts);
            // Assert
            Assert.AreEqual(ChibiNameInviteVerdict.Duplicate, v);
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual("newest", pending[0].BeaconId);
        }

        [Test]
        public void Offer_SameInviterNewBeaconNextQuery_ReplacesOld()
        {
            var pending = new List<PendingNameInvite> { Inv("b1", "p1") };
            var v = ChibiNameInviteRules.Offer(pending, Inv("b2", "p1"), null, Now, new HashSet<string>());
            Assert.AreEqual(ChibiNameInviteVerdict.Ok, v);
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual("b2", pending[0].BeaconId);
        }

        [Test]
        public void Offer_SameBeaconAgain_KeepsOriginalExpiry()
        {
            var pending = new List<PendingNameInvite> { Inv("b1", "p1", expires: Now + 30f) };
            var v = ChibiNameInviteRules.Offer(pending, Inv("b1", "p1", expires: Now + 60f), null, Now);
            Assert.AreEqual(ChibiNameInviteVerdict.Known, v);
            Assert.AreEqual(Now + 30f, pending[0].ExpiresAt, 0.001f);
        }

        [Test]
        public void Offer_AcceptedBeacon_Ignored()
        {
            var pending = new List<PendingNameInvite>();
            var dismissed = new List<ChibiDismissedInvite>();
            ChibiNameInviteRules.Dismiss(dismissed, Inv("b1", "p1"), 0f);
            Assert.AreEqual(ChibiNameInviteVerdict.Dismissed, ChibiNameInviteRules.Offer(pending, Inv("b1", "p1"), dismissed, Now));
            Assert.AreEqual(ChibiNameInviteVerdict.Ok, ChibiNameInviteRules.Offer(pending, Inv("b2", "p1"), dismissed, Now));
        }

        [Test]
        public void Offer_DeclinedInviterNewBeacon_IgnoredUntilMuteEnds()
        {
            // Arrange
            var pending = new List<PendingNameInvite>();
            var dismissed = new List<ChibiDismissedInvite>();
            ChibiNameInviteRules.Dismiss(dismissed, Inv("b1", "p1"), Now + ChibiNameInviteRules.DeclineMuteSeconds);
            // Act
            var during = ChibiNameInviteRules.Offer(pending, Inv("b2", "p1"), dismissed, Now + 10f);
            var after = ChibiNameInviteRules.Offer(pending, Inv("b3", "p1"), dismissed, Now + ChibiNameInviteRules.DeclineMuteSeconds + 1f);
            // Assert
            Assert.AreEqual(ChibiNameInviteVerdict.Dismissed, during);
            Assert.AreEqual(ChibiNameInviteVerdict.Ok, after);
        }

        [Test]
        public void Dismiss_ManyEntries_CappedAtMax()
        {
            var dismissed = new List<ChibiDismissedInvite>();
            for (int i = 0; i < ChibiNameInviteRules.MaxDismissed + 5; i++) ChibiNameInviteRules.Dismiss(dismissed, Inv("b" + i, "p" + i), 0f);
            Assert.AreEqual(ChibiNameInviteRules.MaxDismissed, dismissed.Count);
            Assert.AreEqual("b5", dismissed[0].BeaconId);   // the oldest were forgotten
        }

        [Test]
        public void Prune_ExpiredInvite_RemovedAndNeverReoffered()
        {
            // Arrange (SEC-001): the same live beacon must not come back with a fresh 60 s
            var pending = new List<PendingNameInvite> { Inv("b1", "p1", expires: Now - 1f), Inv("b2", "p2") };
            var dismissed = new List<ChibiDismissedInvite>();
            // Act
            int removed = ChibiNameInviteRules.Prune(pending, Now, null, dismissed);
            var again = ChibiNameInviteRules.Offer(pending, Inv("b1", "p1", expires: Now + 60f), dismissed, Now);
            // Assert
            Assert.AreEqual(1, removed);
            Assert.AreEqual(ChibiNameInviteVerdict.Dismissed, again);
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual("b2", pending[0].BeaconId);
        }

        [Test]
        public void Prune_BeaconGoneFromQuery_RemovedNotDismissed()
        {
            var pending = new List<PendingNameInvite> { Inv("b1", "p1"), Inv("b2", "p2") };
            var dismissed = new List<ChibiDismissedInvite>();
            ChibiNameInviteRules.Prune(pending, Now, new HashSet<string> { "b2" }, dismissed);
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual("b2", pending[0].BeaconId);
            Assert.AreEqual(0, dismissed.Count);
        }

        [Test]
        public void Prune_NoFreshQuery_KeepsLiveInvites()
        {
            var pending = new List<PendingNameInvite> { Inv("b1", "p1") };
            Assert.AreEqual(0, ChibiNameInviteRules.Prune(pending, Now, null, null));
            Assert.AreEqual(1, pending.Count);
        }

        // ================================================================== decline pause (SEC-003)

        [Test]
        public void RecordDecline_ThirdWithinWindow_PausesAllInvites()
        {
            // Arrange: three different (throwaway) senders, declined within 10 minutes
            var times = new List<float>();
            float paused = -1f;
            // Act
            paused = ChibiNameInviteRules.RecordDecline(times, Now, paused);
            paused = ChibiNameInviteRules.RecordDecline(times, Now + 100f, paused);
            float beforeThird = paused;
            paused = ChibiNameInviteRules.RecordDecline(times, Now + 200f, paused);
            // Assert
            Assert.Less(beforeThird, Now + 100f);
            Assert.AreEqual(Now + 200f + ChibiNameInviteRules.PauseSeconds, paused, 0.001f);
        }

        [Test]
        public void RecordDecline_SpreadOverMoreThanWindow_NoPause()
        {
            var times = new List<float>();
            float paused = -1f;
            paused = ChibiNameInviteRules.RecordDecline(times, Now, paused);
            paused = ChibiNameInviteRules.RecordDecline(times, Now + 400f, paused);
            paused = ChibiNameInviteRules.RecordDecline(times, Now + 400f + ChibiNameInviteRules.DeclineWindowSeconds, paused);
            Assert.AreEqual(-1f, paused);
        }

        // ================================================================== outgoing beacon and polling

        [Test]
        public void OutgoingEnd_PastExpiry_Expired() =>
            Assert.AreEqual(ChibiNameInviteEnd.Expired, ChibiNameInviteRules.OutgoingEnd(Now, Now, true, true, 1));

        [Test]
        public void OutgoingEnd_RoomHasTwoPlayers_RoomFull() =>
            Assert.AreEqual(ChibiNameInviteEnd.RoomFull, ChibiNameInviteRules.OutgoingEnd(Now, Now + 30f, true, true, 2));

        [Test]
        public void OutgoingEnd_RoomMissingFromLoadedList_RoomClosed() =>
            Assert.AreEqual(ChibiNameInviteEnd.RoomClosed, ChibiNameInviteRules.OutgoingEnd(Now, Now + 30f, true, false, 0));

        [Test]
        public void OutgoingEnd_RoomListNotLoaded_None() =>
            Assert.AreEqual(ChibiNameInviteEnd.None, ChibiNameInviteRules.OutgoingEnd(Now, Now + 30f, false, false, 0));

        [Test]
        public void OutgoingEnd_OpenRoomOnePlayer_None() =>
            Assert.AreEqual(ChibiNameInviteEnd.None, ChibiNameInviteRules.OutgoingEnd(Now, Now + 30f, true, true, 1));

        [TestCase(0f, 5f)] [TestCase(1f, 6f)] [TestCase(-3f, 5f)] [TestCase(7f, 6f)] [TestCase(float.NaN, 5f)]
        public void PollDelay_RandomInput_BetweenFiveAndSixSeconds(float r, float expected) =>
            Assert.AreEqual(expected, ChibiNameInviteRules.PollDelay(r), 0.0001f);

        // ================================================================== age gate (SEC-006)

        [Test]
        public void Send_NoAgeGateWired_RefusedAsNotAllowed()
        {
            // Arrange: no OnlineAllowed delegate and no room service bound: the gate must fail closed
            var go = new GameObject("NameInvitesTest");
            try
            {
                var svc = go.AddComponent<ChibiNameInvites>();
                // Act (the refusal happens before any await)
                svc.Send("Kira", Code, "Room");
                // Assert
                Assert.AreEqual(NetworkRoomManager.NotAllowedMessage, svc.Status);
                Assert.IsTrue(svc.StatusIsError);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Send_AgeGateAllowsButBadName_NameErrorShown()
        {
            var go = new GameObject("NameInvitesTest");
            try
            {
                var svc = go.AddComponent<ChibiNameInvites>();
                svc.OnlineAllowed = () => true;
                svc.Send("a b", Code, "Room");
                Assert.AreNotEqual(NetworkRoomManager.NotAllowedMessage, svc.Status);
                Assert.IsTrue(svc.StatusIsError);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
