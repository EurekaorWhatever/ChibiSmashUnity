// Story type: Logic (social layer security rules) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Spec: docs/architecture/netcode/online-social.md. Everything another player sends is untrusted; these rules decide what
// may be shown or acted on. The Unity Friends / Lobby services themselves need a linked cloud project (not unit-testable).
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiSocialRules: player names, safe display, room codes, and no friend-list invites.</summary>
    public class ChibiSocialRulesTests
    {
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

        // ================================================================== no friend-list invites (2026-11-26)

        [Test]
        public void SocialManager_HasNoFriendListInviteApi()
        {
            // User request 2026-11-26: room invites go by player name only (ChibiNameInvites on VS LOCAL), never through
            // the friends list. Guard against the old friend-message invite path coming back.
            foreach (var m in typeof(ChibiSocialManager).GetMembers())
                StringAssert.DoesNotContain("Invite", m.Name, "friend-list invite member is back: " + m.Name);
        }
    }
}
