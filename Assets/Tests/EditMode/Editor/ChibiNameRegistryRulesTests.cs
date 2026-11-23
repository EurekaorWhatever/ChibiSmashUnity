// Story type: Logic (worldwide-unique player names: reply parsing, messages, dev skip) -- evidence: automated EditMode
// unit test -- gate: BLOCKING. Spec: production/HANDOFF-2026-11-22.md "NEXT" item 4 (unique names, "this name is already
// taken"). The server script (Assets/ChibiClash/CloudCode/Chibi_Clash.js) is the authority; these rules shape and read its replies.
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiNameRegistryRules: never trusts a malformed reply, shows the exact taken text, dev skip only in dev.</summary>
    public class ChibiNameRegistryRulesTests
    {
        [Test]
        public void ParseReply_ClaimOk_MatchingName_IsOk()
        {
            var r = ChibiNameRegistryRules.ParseReply("{\"status\":\"ok\",\"name\":\"Kira\"}", "kira");
            Assert.AreEqual(ChibiNameStatus.Ok, r.Status);
            Assert.AreEqual("Kira", r.Name);
        }

        [Test]
        public void ParseReply_ClaimOk_OtherName_IsUnavailable()
        {
            var r = ChibiNameRegistryRules.ParseReply("{\"status\":\"ok\",\"name\":\"Other\"}", "Kira");
            Assert.AreEqual(ChibiNameStatus.ServiceUnavailable, r.Status);
        }

        [TestCase("{\"status\":\"taken\"}", ChibiNameStatus.Taken)]
        [TestCase("{\"status\":\"available\"}", ChibiNameStatus.Available)]
        [TestCase("{\"status\":\"yours\"}", ChibiNameStatus.Yours)]
        [TestCase("{\"status\":\"invalid\",\"reason\":\"x\"}", ChibiNameStatus.Invalid)]
        [TestCase("{\"status\":\"wait\"}", ChibiNameStatus.TooSoon)]
        [TestCase("{\"status\":\"error\"}", ChibiNameStatus.ServiceUnavailable)]
        [TestCase("{\"status\":\"OK\"}", ChibiNameStatus.ServiceUnavailable)]
        [TestCase("{}", ChibiNameStatus.ServiceUnavailable)]
        [TestCase("not json", ChibiNameStatus.ServiceUnavailable)]
        [TestCase("", ChibiNameStatus.ServiceUnavailable)]
        [TestCase(null, ChibiNameStatus.ServiceUnavailable)]
        public void ParseReply_Statuses(string json, ChibiNameStatus expected)
        {
            Assert.AreEqual(expected, ChibiNameRegistryRules.ParseReply(json).Status);
        }

        [Test]
        public void ParseReply_Stats_ReadsCounts_NegativeClampedToZero()
        {
            var r = ChibiNameRegistryRules.ParseReply("{\"status\":\"ok\",\"players\":12,\"joins\":340}");
            Assert.AreEqual(12, r.Players);
            Assert.AreEqual(340, r.Joins);
            Assert.AreEqual(0, ChibiNameRegistryRules.ParseReply("{\"status\":\"ok\",\"players\":-5}").Players);
        }

        [Test]
        public void ParseReply_PrettyPrintedJson_Works()
        {
            // The non-generic CallEndpointAsync returns an indented JSON object text.
            var r = ChibiNameRegistryRules.ParseReply("{\n  \"status\": \"taken\"\n}");
            Assert.AreEqual(ChibiNameStatus.Taken, r.Status);
        }

        [Test]
        public void ParseReply_HugeReply_Refused()
        {
            string huge = "{\"status\":\"ok\",\"name\":\"" + new string('a', 5000) + "\"}";
            Assert.AreEqual(ChibiNameStatus.ServiceUnavailable, ChibiNameRegistryRules.ParseReply(huge).Status);
        }

        [Test]
        public void DialogError_Taken_IsExactSpecText()
        {
            Assert.AreEqual("This name is already taken", ChibiNameRegistryRules.DialogError(ChibiNameStatus.Taken));
        }

        [Test]
        public void DialogError_TooSoon_KeepsWindowOpenWithCooldownText()
        {
            Assert.AreEqual(ChibiNameRegistryRules.TooSoonMessage, ChibiNameRegistryRules.DialogError(ChibiNameStatus.TooSoon));
            Assert.IsFalse(ChibiNameRegistryRules.IsOwned(ChibiNameStatus.TooSoon));
        }

        [TestCase(ChibiNameStatus.Ok)]
        [TestCase(ChibiNameStatus.Yours)]
        [TestCase(ChibiNameStatus.ServiceUnavailable)]   // offline: saved locally, claimed before online play
        public void DialogError_SavableStatuses_AreNull(ChibiNameStatus s)
        {
            Assert.IsNull(ChibiNameRegistryRules.DialogError(s));
        }

        [Test]
        public void Normalize_CaseAndSpaces_SameKey()
        {
            Assert.AreEqual(ChibiNameRegistryRules.Normalize("  KiRa_9 "), ChibiNameRegistryRules.Normalize("kira_9"));
            Assert.AreEqual("", ChibiNameRegistryRules.Normalize(null));
        }

        [Test]
        public void MaySkipClaim_ReleaseBuild_NeverSkips()
        {
            Assert.IsFalse(ChibiNameRegistryRules.MaySkipClaim(false, false, "DevHost"));
            Assert.IsFalse(ChibiNameRegistryRules.MaySkipClaim(false, false, "Kira", ChibiNameStatus.ServiceUnavailable));
        }

        [Test]
        public void MaySkipClaim_Dev_OnlyDevNamesOrServiceDown()
        {
            Assert.IsTrue(ChibiNameRegistryRules.MaySkipClaim(true, false, "DevGuest"));
            Assert.IsTrue(ChibiNameRegistryRules.MaySkipClaim(false, true, "DevPlayer"));
            Assert.IsFalse(ChibiNameRegistryRules.MaySkipClaim(true, false, "Kira"));
            Assert.IsFalse(ChibiNameRegistryRules.MaySkipClaim(true, false, "Kira", ChibiNameStatus.Taken));
            Assert.IsTrue(ChibiNameRegistryRules.MaySkipClaim(true, false, "Kira", ChibiNameStatus.ServiceUnavailable));
            Assert.IsFalse(ChibiNameRegistryRules.MaySkipClaim(true, false, "devhost"));   // exact names only
        }
    }
}
