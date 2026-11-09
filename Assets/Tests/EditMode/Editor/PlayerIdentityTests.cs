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
        public void OnlineAllowed_EveryAge_OnceTheAgeIsAnswered()
        {
            // Product decision 2026-11-09: all modes for all ages; the age is for market analysis.
            Assert.IsFalse(PlayerIdentity.RequireTeenForOnline);
            Assert.IsFalse(PlayerIdentity.OnlineAllowed((int)AgeBand.Unknown), "the age question comes first");
            Assert.IsTrue(PlayerIdentity.OnlineAllowed((int)AgeBand.Child));
            Assert.IsTrue(PlayerIdentity.OnlineAllowed((int)AgeBand.Teen));
            Assert.IsTrue(PlayerIdentity.OnlineAllowed((int)AgeBand.Adult));
        }

        [TestCase("9", 9)] [TestCase(" 27 ", 27)] [TestCase("030", 30)] [TestCase("abc", 0)] [TestCase("", 0)] [TestCase("200", 0)] [TestCase(null, 0)]
        public void ParseAge_DigitsOnly(string typed, int expected) => Assert.AreEqual(expected, PlayerIdentity.ParseAge(typed));

        // ================================================================== player age record (market analysis file)

        [Test]
        public void AgeRecord_Row_HasAllColumns()
        {
            string row = PlayerAgeLedger.Row("Kira", 27, "TH", "WindowsPlayer", "1.0", new System.DateTime(2026, 11, 9, 8, 30, 0, System.DateTimeKind.Utc));

            Assert.AreEqual("2026-11-09T08:30:00Z,Kira,27,25-34,TH,WindowsPlayer,1.0", row);
            Assert.AreEqual(PlayerAgeLedger.Header.Split(',').Length, row.Split(',').Length);
        }

        [Test]
        public void AgeRecord_Row_HostileValuesCannotBreakTheFile()
        {
            string row = PlayerAgeLedger.Row("=HYPERLINK(\"x\"),\nEvil", 15, "T,H", "Win\"dows", "1,0", System.DateTime.UtcNow);

            Assert.AreEqual(PlayerAgeLedger.Header.Split(',').Length, row.Split(',').Length, "no extra columns");
            Assert.IsFalse(row.Contains("\n") || row.Contains("\"") || row.Contains("="), row);
        }

        [TestCase(8, "under13")] [TestCase(13, "13-17")] [TestCase(18, "18-24")] [TestCase(30, "25-34")] [TestCase(40, "35-44")]
        [TestCase(60, "45+")] [TestCase(0, "unknown")]
        public void AgeRecord_Group(int age, string group) => Assert.AreEqual(group, PlayerAgeLedger.Group(age));

        [Test]
        public void SwitchProfile_DifferentAccount_IsANewPlayerForTheAgeRecord()
        {
            var s = new SaveData { playerName = "Kira", ageBand = (int)AgeBand.Adult, age = 27, ageRecorded = true };
            PlayerIdentity.SwitchProfile(s, "acc-A", "");
            Assert.IsTrue(s.ageRecorded, "first account from the guest is the same person: not counted twice");
            Assert.AreEqual(27, s.age);

            PlayerIdentity.SwitchProfile(s, "acc-B", "");
            Assert.IsFalse(s.ageRecorded, "another account is a new player");
            Assert.AreEqual(0, s.age, "and is asked its age");
        }

        [TestCase(0)] [TestCase(4)] [TestCase(-1)] [TestCase(77)]
        public void NeedsAge_UnknownOrEditedValue_AsksAgain(int stored) => Assert.IsTrue(PlayerIdentity.NeedsAge(stored));

        [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void NeedsAge_KnownGroup_NotAsked(int stored) => Assert.IsFalse(PlayerIdentity.NeedsAge(stored));

        // ================================================================== accounts (Google sign-in): identity per account

        static SaveData Guest(string name = "Kira", AgeBand band = AgeBand.Adult) =>
            new SaveData { playerName = name, ageBand = (int)band };

        [Test]
        public void SwitchProfile_FirstSignInFromGuest_KeepsNameAndAge()
        {
            var s = Guest();

            PlayerIdentity.SwitchProfile(s, "acc-A", serverName: "");

            Assert.AreEqual("acc-A", s.accountId);
            Assert.AreEqual("Kira", s.playerName, "the first account on this computer adopts the guest identity");
            Assert.AreEqual((int)AgeBand.Adult, s.ageBand);
        }

        [Test]
        public void SwitchProfile_AccountServerName_Wins()
        {
            var s = Guest();

            PlayerIdentity.SwitchProfile(s, "acc-A", serverName: "Shadow#4821");

            Assert.AreEqual("Shadow", s.playerName, "the same account shows the same name on every computer");
        }

        [Test]
        public void SwitchProfile_DifferentAccount_MustBeNamedAgain_ThenSwitchingBackRestores()
        {
            var s = Guest();
            PlayerIdentity.SwitchProfile(s, "acc-A", "");          // Kira on account A

            PlayerIdentity.SwitchProfile(s, "acc-B", "");          // another Google account
            Assert.AreEqual("", s.playerName, "a different account starts with no name (rename)");
            Assert.IsTrue(PlayerIdentity.NeedsAge(s.ageBand), "and its own age question");

            s.playerName = "Pip"; s.ageBand = (int)AgeBand.Teen;   // named on account B
            PlayerIdentity.SwitchProfile(s, "acc-A", "");          // back to account A on this computer

            Assert.AreEqual("Kira", s.playerName, "account A's identity is kept");
            Assert.AreEqual((int)AgeBand.Adult, s.ageBand);
            PlayerIdentity.SwitchProfile(s, "acc-B", "");
            Assert.AreEqual("Pip", s.playerName, "account B's identity is kept too");
        }

        [Test]
        public void SwitchProfile_SignOut_ReturnsToGuestIdentity()
        {
            var s = Guest("Kira");
            PlayerIdentity.SwitchProfile(s, "acc-A", "Shadow#1");

            PlayerIdentity.SwitchProfile(s, "", "");

            Assert.AreEqual("", s.accountId);
            Assert.AreEqual("Kira", s.playerName, "the guest profile on this computer is untouched");
        }

        [Test]
        public void SwitchProfile_HostileServerName_Ignored_AndProfilesCapped()
        {
            var s = Guest();
            PlayerIdentity.SwitchProfile(s, "acc-A", "<b>Evil</b>#1");
            Assert.AreEqual("Kira", s.playerName, "an invalid server name never replaces the saved one");

            for (int i = 0; i < 20; i++) PlayerIdentity.SwitchProfile(s, "acc-" + i, "");
            Assert.LessOrEqual(s.profiles.Count, PlayerIdentity.MaxProfiles);
        }

        [Test]
        public void SaveData_NewProfile_StartsUnasked()
        {
            var s = new SaveData();
            Assert.IsTrue(PlayerIdentity.NeedsAge(s.ageBand));
            Assert.AreEqual("", s.playerName);
        }
    }
}
