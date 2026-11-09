// Story type: Logic (social layer security rules) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Spec: docs/architecture/netcode/online-social.md. Everything another player sends is untrusted; these rules decide what
// may be shown or acted on. The Unity Friends / Lobby services themselves need a linked cloud project (not unit-testable).
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiSocialRules: player names, safe display, room codes, invite validation, rate limits.</summary>
    public class ChibiSocialRulesTests
    {
        const string Proto = "p2r17";

        static ChibiInviteMessage Invite(string code = "K7Q2PX", string proto = Proto, string kind = ChibiSocialRules.KindInvite) =>
            new ChibiInviteMessage { Kind = kind, Code = code, Proto = proto, V = ChibiSocialRules.InviteVersion };

        static ChibiInviteVerdict Check(ChibiInviteMessage m, bool friend = true, float now = 100f, float last = float.NegativeInfinity,
                                        int pending = 0, bool busy = false) =>
            ChibiSocialRules.ValidateInvite(m, friend, Proto, now, last, pending, busy);

        // ================================================================== names

        [TestCase("Kira")] [TestCase("Pip_99")] [TestCase("abc")] [TestCase("ABCDEFGHIJKLMNOP")]
        public void ValidateName_GoodNames_Accepted(string name) => Assert.IsNull(ChibiSocialRules.ValidateName(name));

        [TestCase(null)] [TestCase("")] [TestCase("ab")] [TestCase("ABCDEFGHIJKLMNOPQ")]
        [TestCase("Bad Name")] [TestCase("<b>x</b>")] [TestCase("Kira#1234")] [TestCase("ข้าว")] [TestCase("a\nb\nc")]
        public void ValidateName_BadNames_Rejected(string name) => Assert.IsNotNull(ChibiSocialRules.ValidateName(name));

        [TestCase("Kira#4821", "Kira#4821")]
        [TestCase("  Kira#4821 ", "Kira#4821")]
        [TestCase("Kira", "Kira")]
        public void NormalizeFriendName_Valid(string input, string expected) =>
            Assert.AreEqual(expected, ChibiSocialRules.NormalizeFriendName(input));

        [TestCase("Ki#1")] [TestCase("Kira#")] [TestCase("Kira#12ab")] [TestCase("Kira#1234567")] [TestCase("<size=99>K#1")] [TestCase(null)]
        public void NormalizeFriendName_Malformed_Null(string input) => Assert.IsNull(ChibiSocialRules.NormalizeFriendName(input));

        // ================================================================== safe display (rich-text injection)

        [Test]
        public void SafeDisplayName_StripsRichTextAndControlChars()
        {
            string evil = "<size=500><color=red>Kira</color></size>\n\t#4821";
            string safe = ChibiSocialRules.SafeDisplayName(evil);
            Assert.IsFalse(safe.Contains("<") || safe.Contains(">") || safe.Contains("=") || safe.Contains("\n"), safe);
            StringAssert.Contains("Kira", safe);
        }

        [Test]
        public void SafeDisplayName_CapsLength_AndNeverEmpty()
        {
            Assert.LessOrEqual(ChibiSocialRules.SafeDisplayName(new string('A', 500)).Length, ChibiSocialRules.DisplayMax);
            Assert.AreEqual("Player", ChibiSocialRules.SafeDisplayName("<<>>"));
            Assert.AreEqual("Player", ChibiSocialRules.SafeDisplayName(null));
        }

        // ================================================================== room names (host-chosen, shown to the joiner)

        [TestCase("Kira's Dojo")] [TestCase("Bangkok Brawl!")] [TestCase("Room-1")] [TestCase("abc")] [TestCase("ABCDEFGHIJKLMNOPQRSTUVWX")]
        public void ValidateRoomName_GoodNames_Accepted(string name) => Assert.IsNull(ChibiSocialRules.ValidateRoomName(name));

        [TestCase(null)] [TestCase("")] [TestCase("ab")] [TestCase("ABCDEFGHIJKLMNOPQRSTUVWXY")] [TestCase("<b>Room</b>")]
        [TestCase("Two  spaces")] [TestCase("Line\nBreak")] [TestCase("Sh1t Room")] [TestCase("Room=1")]
        public void ValidateRoomName_BadNames_Rejected(string name) => Assert.IsNotNull(ChibiSocialRules.ValidateRoomName(name));

        [Test]
        public void SafeRoomName_HostileLobbyName_Sanitised()
        {
            string safe = ChibiSocialRules.SafeRoomName("<size=500><color=red>Kira's   Dojo</color></size>\n");
            Assert.IsFalse(safe.Contains("<") || safe.Contains(">") || safe.Contains("=") || safe.Contains("\n") || safe.Contains("  "), safe);
            StringAssert.Contains("Kira's Dojo", safe);
            Assert.LessOrEqual(ChibiSocialRules.SafeRoomName(new string('A', 300)).Length, ChibiSocialRules.RoomNameMax);
        }

        [TestCase(null)] [TestCase("")] [TestCase("<>")] [TestCase("F_U_C_K room")]
        public void SafeRoomName_EmptyOrBlocked_Fallback(string raw) =>
            Assert.AreEqual(ChibiSocialRules.FallbackRoomName, ChibiSocialRules.SafeRoomName(raw));

        [TestCase("Kira#4821", "Kira's room")] [TestCase("", "My room")] [TestCase("Player", "My room")]
        [TestCase("ABCDEFGHIJKLMNOP#1", "ABCDEFGHIJKLMNOP's room")]
        public void DefaultRoomName_FromPlayerName(string player, string expected)
        {
            Assert.AreEqual(expected, ChibiSocialRules.DefaultRoomName(player));
            Assert.IsNull(ChibiSocialRules.ValidateRoomName(ChibiSocialRules.DefaultRoomName(player)), "the default is always valid");
        }

        // ================================================================== room codes

        [TestCase("k7q2px", "K7Q2PX")] [TestCase(" ABCD ", "ABCD")]
        public void NormalizeCode_Valid(string input, string expected) => Assert.AreEqual(expected, ChibiSocialRules.NormalizeCode(input));

        [TestCase("ABC")] [TestCase("ABCDEFGHIJKLM")] [TestCase("AB-CD")] [TestCase("AB CD")] [TestCase("../x")] [TestCase(null)]
        public void NormalizeCode_Invalid_Null(string input) => Assert.IsNull(ChibiSocialRules.NormalizeCode(input));

        // ================================================================== invites

        [Test]
        public void Invite_FromFriend_SameBuild_Accepted() => Assert.AreEqual(ChibiInviteVerdict.Ok, Check(Invite()));

        [Test]
        public void Invite_FromStranger_Rejected() =>
            Assert.AreEqual(ChibiInviteVerdict.NotAFriend, Check(Invite(), friend: false));

        [Test]
        public void Invite_NullOrOversizedFields_Malformed()
        {
            Assert.AreEqual(ChibiInviteVerdict.Malformed, Check(null));
            Assert.AreEqual(ChibiInviteVerdict.Malformed, Check(new ChibiInviteMessage { Kind = null }));
            Assert.AreEqual(ChibiInviteVerdict.Malformed, Check(Invite(kind: new string('x', 200))));
            Assert.AreEqual(ChibiInviteVerdict.Malformed, Check(Invite(proto: new string('p', 500))));
        }

        [Test]
        public void Invite_WrongKind_Rejected() =>
            Assert.AreEqual(ChibiInviteVerdict.WrongKind, Check(Invite(kind: ChibiSocialRules.KindDecline)));

        [TestCase("")] [TestCase("AB")] [TestCase("<b>HACK</b>")] [TestCase("ABCDEFGHIJKLMNOP")]
        public void Invite_BadCode_Rejected(string code) => Assert.AreEqual(ChibiInviteVerdict.BadCode, Check(Invite(code)));

        [Test]
        public void Invite_OtherBuild_Rejected() =>
            Assert.AreEqual(ChibiInviteVerdict.OtherBuild, Check(Invite(proto: "p1r9")));

        [Test]
        public void Invite_WithinCooldownOfSameSender_Flooding()
        {
            Assert.AreEqual(ChibiInviteVerdict.Flooding, Check(Invite(), now: 100f, last: 100f - ChibiSocialRules.InviteCooldown + 0.1f));
            Assert.AreEqual(ChibiInviteVerdict.Ok, Check(Invite(), now: 100f, last: 100f - ChibiSocialRules.InviteCooldown));
        }

        [Test]
        public void Invite_TooManyPending_Flooding() =>
            Assert.AreEqual(ChibiInviteVerdict.Flooding, Check(Invite(), pending: ChibiSocialRules.MaxPendingInvites));

        [Test]
        public void Invite_WhileBusy_Ignored() => Assert.AreEqual(ChibiInviteVerdict.Busy, Check(Invite(), busy: true));
    }
}
