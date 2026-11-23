using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Team play phase 2: the pure queue / roster / mesh-allocation rules behind ChibiTeamMatchmaker.</summary>
    public class ChibiTeamRoomRulesTests
    {
        [Test]
        public void QueueTags_ArePerFormat()
        {
            Assert.AreEqual("p6r25-1v1", ChibiRoomRules.QueueTag("p6r25", ChibiMatchConfig.Format1v1));
            Assert.AreEqual("p6r25-2v2", ChibiRoomRules.QueueTag("p6r25", ChibiMatchConfig.Format2v2));
            Assert.AreEqual("p6r25-ffa", ChibiRoomRules.QueueTag("p6r25", ChibiMatchConfig.FormatFfa));
        }

        [Test]
        public void Seats_Are2_4_4()
        {
            Assert.AreEqual(2, ChibiRoomRules.SeatsFor(ChibiMatchConfig.Format1v1));
            Assert.AreEqual(4, ChibiRoomRules.SeatsFor(ChibiMatchConfig.Format2v2));
            Assert.AreEqual(4, ChibiRoomRules.SeatsFor(ChibiMatchConfig.FormatFfa));
        }

        [TestCase(ChibiMatchConfig.Format2v2, 3, 999f, false)]
        [TestCase(ChibiMatchConfig.Format2v2, 4, 0f, true)]
        [TestCase(ChibiMatchConfig.FormatFfa, 4, 0f, true)]
        [TestCase(ChibiMatchConfig.FormatFfa, 3, 44.9f, false)]
        [TestCase(ChibiMatchConfig.FormatFfa, 3, 45f, true)]
        [TestCase(ChibiMatchConfig.FormatFfa, 2, 999f, false)]
        [TestCase(ChibiMatchConfig.Format1v1, 2, 0f, true)]
        public void ShouldStart_FullOrFfaThreeAfterTheWait(int format, int players, float waited, bool expected)
        {
            Assert.AreEqual(expected, ChibiRoomRules.ShouldStart(format, players, waited));
        }

        [Test]
        public void LowerSlotAllocatesEveryPair()
        {
            int allocations = 0;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    if (i != j)
                    {
                        Assert.AreNotEqual(ChibiRoomRules.AllocatesFor(i, j), ChibiRoomRules.AllocatesFor(j, i), "exactly one side allocates");
                        if (ChibiRoomRules.AllocatesFor(i, j)) allocations++;
                    }
            Assert.AreEqual(6, allocations, "one allocation per link of a 4-peer mesh");
        }

        [Test]
        public void ParseRoster_RejectsWrongSizeDuplicatesAndEmpties()
        {
            Assert.IsNotNull(ChibiRoomRules.ParseRoster("a,b,c,d", ChibiMatchConfig.Format2v2));
            Assert.IsNull(ChibiRoomRules.ParseRoster("a,b,c", ChibiMatchConfig.Format2v2));
            Assert.IsNotNull(ChibiRoomRules.ParseRoster("a,b,c", ChibiMatchConfig.FormatFfa));
            Assert.IsNull(ChibiRoomRules.ParseRoster("a,b,a", ChibiMatchConfig.FormatFfa));
            Assert.IsNull(ChibiRoomRules.ParseRoster("a,,c", ChibiMatchConfig.FormatFfa));
            Assert.IsNull(ChibiRoomRules.ParseRoster("", ChibiMatchConfig.FormatFfa));
        }

        [Test]
        public void Teams_2v2IsSlots01Vs23_FfaEachOwn()
        {
            Assert.AreEqual(new[] { 0, 0, 1, 1 }, new[] { 0, 1, 2, 3 }.Select4(s => ChibiControlCodec.TeamFor(ChibiMatchConfig.Format2v2, s)));
            Assert.AreEqual(new[] { 0, 1, 2, 3 }, new[] { 0, 1, 2, 3 }.Select4(s => ChibiControlCodec.TeamFor(ChibiMatchConfig.FormatFfa, s)));
        }

        [Test]
        public void PlayerHash_IsStableAndNeverZeroForAnId()
        {
            Assert.AreEqual(ChibiControlCodec.PlayerHash("abc"), ChibiControlCodec.PlayerHash("abc"));
            Assert.AreNotEqual(ChibiControlCodec.PlayerHash("abc"), ChibiControlCodec.PlayerHash("abd"));
            Assert.AreEqual(0u, ChibiControlCodec.PlayerHash(""));
            Assert.AreNotEqual(0u, ChibiControlCodec.PlayerHash("x"));
        }

        // ================================================================== matchmaker decisions (review B2 / S2 / N1)

        [Test]
        public void IsHost_FollowsTheLobbyHostId()
        {
            Assert.IsTrue(ChibiRoomRules.IsHost("me", "me"));
            Assert.IsFalse(ChibiRoomRules.IsHost("other", "me"));
            Assert.IsFalse(ChibiRoomRules.IsHost("", ""), "not signed in is never the host");
        }

        [Test]
        public void RateLimitBackoff_Grows_ThenCaps()
        {
            Assert.AreEqual(5f, ChibiRoomRules.RateLimitBackoffSeconds(1));
            Assert.AreEqual(10f, ChibiRoomRules.RateLimitBackoffSeconds(2));
            Assert.AreEqual(20f, ChibiRoomRules.RateLimitBackoffSeconds(3));
            Assert.AreEqual(30f, ChibiRoomRules.RateLimitBackoffSeconds(9));
            Assert.AreEqual(3, ChibiRoomRules.JoinAttemptsPerSearch);
        }

        [Test]
        public void ShouldRequeue_OnlyAGuestThatWaitedTooLong()
        {
            Assert.IsFalse(ChibiRoomRules.ShouldRequeue(false, ChibiRoomRules.QueueRequeueSeconds - 1f));
            Assert.IsTrue(ChibiRoomRules.ShouldRequeue(false, ChibiRoomRules.QueueRequeueSeconds));
            Assert.IsFalse(ChibiRoomRules.ShouldRequeue(true, 9999f), "a host keeps its lobby");
        }

        [Test]
        public void VerifyRoster_AcceptsJoinOrderAndLateJoiners_RejectsStrangersAndReordering()
        {
            var lobby = new System.Collections.Generic.List<string> { "h", "a", "b", "late" };
            Assert.IsNull(ChibiRoomRules.VerifyRoster(new[] { "h", "a", "b" }, lobby), "a late joiner after the lock is fine");
            Assert.IsNotNull(ChibiRoomRules.VerifyRoster(new[] { "h", "b", "a" }, lobby), "reordered slots");
            Assert.IsNotNull(ChibiRoomRules.VerifyRoster(new[] { "h", "a", "x" }, lobby), "a stranger in the roster");
            Assert.IsNotNull(ChibiRoomRules.VerifyRoster(new[] { "h", "a", "b" }, new System.Collections.Generic.List<string> { "h", "b" }),
                             "a roster member who left");
            Assert.IsNotNull(ChibiRoomRules.VerifyRoster(null, lobby));
        }

        [Test]
        public void ParsePublicKey_AcceptsOnly32Bytes_NeverThrows()
        {
            var key = new byte[32]; key[0] = 9;
            Assert.IsNotNull(ChibiRoomRules.ParsePublicKey(System.Convert.ToBase64String(key)));
            Assert.IsNull(ChibiRoomRules.ParsePublicKey(System.Convert.ToBase64String(new byte[31])));
            Assert.IsNull(ChibiRoomRules.ParsePublicKey("not base64 !!"));
            Assert.IsNull(ChibiRoomRules.ParsePublicKey(null));
        }

        [Test]
        public void DerivePairKeys_BothEndsOfEveryPairAgree_MissingOrLowOrderKeysRefuse()
        {
            // Arrange: three players with fixed private keys.
            var priv = new byte[3][];
            var pub = new byte[3][];
            for (int i = 0; i < 3; i++)
            {
                priv[i] = new byte[32];
                for (int k = 0; k < 32; k++) priv[i][k] = (byte)(i * 50 + k + 1);
                pub[i] = ChibiX25519.PublicKey(priv[i]);
            }

            // Act
            var keys = new byte[3][][];
            for (int i = 0; i < 3; i++) keys[i] = ChibiRoomRules.DerivePairKeys(priv[i], pub, "lobby1", i);

            // Assert
            for (int i = 0; i < 3; i++)
            {
                Assert.IsNull(keys[i][i]);
                for (int j = 0; j < 3; j++)
                    if (i != j) Assert.AreEqual(keys[i][j], keys[j][i], "pair " + i + "-" + j + " derived different keys");
            }
            Assert.AreNotEqual(keys[0][1], keys[0][2]);
            var missing = new[] { pub[0], null, pub[2] };
            Assert.IsNull(ChibiRoomRules.DerivePairKeys(priv[0], missing, "lobby1", 0));
            var lowOrder = new[] { pub[0], new byte[32], pub[2] };
            Assert.IsNull(ChibiRoomRules.DerivePairKeys(priv[0], lowOrder, "lobby1", 0));
        }

        [Test]
        public void DerivePairKeys_RefusesDuplicateKeysAndOurOwnKey()
        {
            var priv = new byte[3][];
            var pub = new byte[3][];
            for (int i = 0; i < 3; i++)
            {
                priv[i] = new byte[32];
                for (int k = 0; k < 32; k++) priv[i][k] = (byte)(i * 50 + k + 1);
                pub[i] = ChibiX25519.PublicKey(priv[i]);
            }
            Assert.IsNull(ChibiRoomRules.DerivePairKeys(priv[0], new[] { pub[0], pub[1], pub[1] }, "L", 0), "two slots share a key");
            Assert.IsNull(ChibiRoomRules.DerivePairKeys(priv[0], new[] { pub[0], pub[0], pub[2] }, "L", 0), "a peer replays our key");
            Assert.IsTrue(ChibiRoomRules.HostShouldGiveUp(ChibiRoomRules.HostQueueTimeoutSeconds));
            Assert.IsFalse(ChibiRoomRules.HostShouldGiveUp(ChibiRoomRules.HostQueueTimeoutSeconds - 1f));
        }
    }

    static class TeamRulesTestExt
    {
        public static int[] Select4(this int[] a, System.Func<int, int> f)
        {
            var r = new int[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = f(a[i]);
            return r;
        }
    }
}
