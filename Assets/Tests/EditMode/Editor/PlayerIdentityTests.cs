// Story type: Logic (first-launch onboarding: age group gate) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Spec: docs/architecture/netcode/online-social.md ("Onboarding: age, then name").
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>PlayerIdentity: age answer parsing, age groups and the online gate.</summary>
    public class PlayerIdentityTests
    {
        [TestCase(1, AgeBand.Child)] [TestCase(12, AgeBand.Child)]
        [TestCase(13, AgeBand.Teen)] [TestCase(17, AgeBand.Teen)]
        [TestCase(18, AgeBand.Adult)] [TestCase(120, AgeBand.Adult)]
        public void BandForAge_Boundaries(int age, AgeBand expected) => Assert.AreEqual(expected, PlayerIdentity.BandForAge(age));

        [TestCase(0)] [TestCase(-5)] [TestCase(121)] [TestCase(999)]
        public void BandForAge_Implausible_Unknown(int age) => Assert.AreEqual(AgeBand.Unknown, PlayerIdentity.BandForAge(age));

        [TestCase("12", AgeBand.Child)] [TestCase(" 15 ", AgeBand.Teen)] [TestCase("030", AgeBand.Adult)]
        public void BandForAnswer_Digits_Parsed(string typed, AgeBand expected) =>
            Assert.AreEqual(expected, PlayerIdentity.BandForAnswer(typed));

        [TestCase(null)] [TestCase("")] [TestCase("abc")] [TestCase("1 5")] [TestCase("-3")] [TestCase("12.5")] [TestCase("9999")]
        [TestCase("<b>20</b>")]
        public void BandForAnswer_NotAnAge_Unknown(string typed) => Assert.AreEqual(AgeBand.Unknown, PlayerIdentity.BandForAnswer(typed));

        [Test]
        public void OnlineAllowed_OnlyThirteenAndOver()
        {
            Assert.IsFalse(PlayerIdentity.OnlineAllowed((int)AgeBand.Unknown));
            Assert.IsFalse(PlayerIdentity.OnlineAllowed((int)AgeBand.Child));
            Assert.IsTrue(PlayerIdentity.OnlineAllowed((int)AgeBand.Teen));
            Assert.IsTrue(PlayerIdentity.OnlineAllowed((int)AgeBand.Adult));
        }

        [TestCase(0)] [TestCase(4)] [TestCase(-1)] [TestCase(77)]
        public void NeedsAge_UnknownOrEditedValue_AsksAgain(int stored) => Assert.IsTrue(PlayerIdentity.NeedsAge(stored));

        [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void NeedsAge_KnownGroup_NotAsked(int stored) => Assert.IsFalse(PlayerIdentity.NeedsAge(stored));

        [Test]
        public void SaveData_NewProfile_StartsUnasked()
        {
            var s = new SaveData();
            Assert.IsTrue(PlayerIdentity.NeedsAge(s.ageBand));
            Assert.AreEqual("", s.playerName);
        }
    }
}
