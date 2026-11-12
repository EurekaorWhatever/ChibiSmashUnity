// Story type: Logic (CREATE ROOM mode: who is in, when START FIGHT works) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Spec: docs/architecture/netcode/online-rooms.md ("Rooms"). Lobby data comes from the other player, so the rules
// validate it; START FIGHT must never work unless BOTH players chose a fighter and pressed OK.
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiRoomRules: snapshot building, ready / start gating, host launch and guest connect decisions.</summary>
    public class ChibiRoomRulesTests
    {
        const string Host = "host-id", Guest = "guest-id";

        static ChibiRoomRawPlayer P(string id, string name, string cls = "1", bool ready = false, string start = "") =>
            new ChibiRoomRawPlayer { Id = id, Name = name, Class = cls, Passive = "0", Ready = ready ? "1" : "0", Start = start };

        static ChibiRoomSnapshot Room(string go = "", string relay = "", params ChibiRoomRawPlayer[] players) =>
            ChibiRoomRules.Snapshot("lobby1", "Kira's Dojo", "k7q2px", Host, "2", go, relay, new List<ChibiRoomRawPlayer>(players));

        // ================================================================== snapshot

        [Test]
        public void Snapshot_HostSeatFirst_FieldsValidated()
        {
            var s = Room("", "", P(Guest, "Pip#77", "3"), P(Host, "Kira#4821", "0"));

            Assert.AreEqual(2, s.Slots.Count);
            Assert.AreEqual(Host, s.Slots[0].PlayerId, "host is seat 0 whatever the lobby order");
            Assert.AreEqual("Kira", s.Slots[0].Name, "self-reported names show without the #tag");
            Assert.AreEqual(FighterClass.Magic, s.Slots[1].Class);
            Assert.AreEqual("K7Q2PX", s.Code);
            Assert.AreEqual(2, s.Stage);
            Assert.AreEqual("Kira's Dojo", s.Name);
        }

        [Test]
        public void Snapshot_HostileData_Sanitised()
        {
            var raw = new List<ChibiRoomRawPlayer>
            {
                P(Host, "<size=500>Kira</size>", "99", ready: true, start: new string('x', 500)),
                P(Guest, "Pip"), P("third", "Extra"),
            };
            var s = ChibiRoomRules.Snapshot("id", "<color=red>Evil</color> Room", "../x", Host, "9", new string('g', 200), "r", raw);   // 9: out of 0..7

            Assert.AreEqual(2, s.Slots.Count, "never more than 2 seats");
            Assert.IsFalse(s.Slots[0].Name.Contains("<"), s.Slots[0].Name);
            Assert.IsFalse(s.Slots[0].Ready, "a player with an invalid fighter is never ready");
            Assert.AreEqual("", s.Slots[0].StartNonce, "oversized nonce dropped");
            Assert.AreEqual(0, s.Stage, "out-of-range stage -> 0");
            Assert.AreEqual("", s.Go, "oversized go dropped");
            Assert.AreEqual("", s.Code, "invalid code dropped");
            Assert.IsFalse(s.Name.Contains("<"), s.Name);
        }

        // ================================================================== START FIGHT gating

        [Test]
        public void CanStart_OnlyWhenBothSeatsTakenAndBothReady()
        {
            Assert.IsFalse(ChibiRoomRules.CanStart(Room("", "", P(Host, "Kira", ready: true))), "alone in the room");
            Assert.IsFalse(ChibiRoomRules.CanStart(Room("", "", P(Host, "Kira", ready: true), P(Guest, "Pip"))), "guest not OK yet");
            Assert.IsFalse(ChibiRoomRules.CanStart(Room("", "", P(Host, "Kira"), P(Guest, "Pip", ready: true))), "host not OK yet");
            Assert.IsTrue(ChibiRoomRules.CanStart(Room("", "", P(Host, "Kira", ready: true), P(Guest, "Pip", ready: true))));
            Assert.IsFalse(ChibiRoomRules.CanStart(null));
        }

        [Test]
        public void WaitingFor_ExplainsWhatIsMissing()
        {
            StringAssert.Contains("friend to join", ChibiRoomRules.WaitingFor(Room("", "", P(Host, "Kira")), Host));
            StringAssert.Contains("press OK", ChibiRoomRules.WaitingFor(Room("", "", P(Host, "Kira"), P(Guest, "Pip", ready: true)), Host));
            StringAssert.Contains("Pip", ChibiRoomRules.WaitingFor(Room("", "", P(Host, "Kira", ready: true), P(Guest, "Pip")), Host));
            Assert.AreEqual("", ChibiRoomRules.WaitingFor(Room("", "", P(Host, "Kira", ready: true), P(Guest, "Pip", ready: true)), Host));
        }

        // ================================================================== launch / connect

        [Test]
        public void HostShouldLaunch_OnNewGuestStart_WhenBothReady_Once()
        {
            var both = Room("", "", P(Host, "Kira", ready: true), P(Guest, "Pip", ready: true, start: "n1"));
            Assert.IsTrue(ChibiRoomRules.HostShouldLaunch(both, ""), "guest pressed START");
            Assert.IsFalse(ChibiRoomRules.HostShouldLaunch(both, "n1"), "the same press is handled once");

            var notReady = Room("", "", P(Host, "Kira"), P(Guest, "Pip", ready: true, start: "n2"));
            Assert.IsFalse(ChibiRoomRules.HostShouldLaunch(notReady, ""), "START ignored until both pressed OK");
        }

        [Test]
        public void GuestShouldConnect_OnlyOnANewLaunchWithRelayCode()
        {
            var launched = Room("go2", "RELAY1", P(Host, "Kira", ready: true), P(Guest, "Pip", ready: true));
            Assert.IsTrue(ChibiRoomRules.GuestShouldConnect(launched, "go1", Guest));
            Assert.IsFalse(ChibiRoomRules.GuestShouldConnect(launched, "go2", Guest), "an old launch (seen on entering) is ignored");
            Assert.IsFalse(ChibiRoomRules.GuestShouldConnect(launched, "go1", Host), "the host never joins its own link");
            Assert.IsFalse(ChibiRoomRules.GuestShouldConnect(Room("go2", "", P(Host, "Kira"), P(Guest, "Pip")), "go1", Guest), "no relay code");
            Assert.IsFalse(ChibiRoomRules.GuestShouldConnect(launched, "go1", "stranger"), "not a member");
        }

        [TestCase("ABC123", true)] [TestCase("abcdef12", true)] [TestCase("ABC12", false)] [TestCase("ABCDEFGHIJKLM", false)]
        [TestCase("AB C12", false)] [TestCase("<b>X</b>", false)] [TestCase(null, false)]
        public void ValidRelayCode_OnlyPlainCodes(string code, bool expected) => Assert.AreEqual(expected, ChibiRoomRules.ValidRelayCode(code));

        [Test]
        public void Snapshot_InvalidRelayCode_Dropped_SoTheGuestNeverConnects()
        {
            var s = Room("go2", "<color=red>x</color>", P(Host, "Kira", ready: true), P(Guest, "Pip", ready: true));
            Assert.AreEqual("", s.RelayCode);
            Assert.IsFalse(ChibiRoomRules.GuestShouldConnect(s, "go1", Guest));
        }

        [Test]
        public void Snapshot_GuestAck_Read()
        {
            var raw = new List<ChibiRoomRawPlayer> { P(Host, "Kira"), new ChibiRoomRawPlayer { Id = Guest, Name = "Pip", Class = "1", Ack = "go7" } };
            var s = ChibiRoomRules.Snapshot("id", "Room", "ABCD", Host, "0", "go7", "RELAY1", raw);
            Assert.AreEqual("go7", s.Slots[1].Ack);
        }

        [TestCase(null, 0)] [TestCase("1", 1)] [TestCase("2", 2)] [TestCase("-1", 0)] [TestCase("abc", 0)]
        [TestCase("3", 3)] [TestCase("7", 7)] [TestCase("8", 0)]   // rules v20: stages 0..7 (ST-1)
        [TestCase(" 3", 0)] [TestCase("99999999999", 0)] [TestCase("+3", 0)] [TestCase("03", 3)]   // SEC-001: strict digits
        public void ParseStage_Range(string raw, int expected) => Assert.AreEqual(expected, ChibiRoomRules.ParseStage(raw));

        [TestCase(null, false)] [TestCase("1", false)] [TestCase("0", true)] [TestCase("off", false)] [TestCase("", false)]
        public void Snapshot_HazardsKey_OnlyZeroTurnsThemOff(string hz, bool expectedOff)
        {
            var raw = new List<ChibiRoomRawPlayer> { P(Host, "Kira") };
            var s = ChibiRoomRules.Snapshot("id", "Room", "ABCD", Host, "5", "", "", raw, hz);
            Assert.AreEqual(expectedOff, s.HazardsOff, "ST-5: absent / garbage = hazards on (the default)");
            Assert.AreEqual(5, s.Stage);
            Assert.AreEqual("0", ChibiRoomRules.HazardsValue(false));
            Assert.AreEqual("1", ChibiRoomRules.HazardsValue(true));
        }

        [Test]
        public void NewNonce_ShortAndDifferent()
        {
            string a = ChibiRoomRules.NewNonce(), b = ChibiRoomRules.NewNonce();
            Assert.AreNotEqual(a, b);
            Assert.LessOrEqual(a.Length, ChibiRoomRules.MaxNonce);
        }

        // ------------------------------------------------------------------ version gate at join (network review finding 1)

        [TestCase("p3r18", "p3r18", true)]
        [TestCase("p2r17", "p3r18", false)]   // old build's room
        [TestCase("p3r17", "p3r18", false)]   // same wire, other rules
        [TestCase("P3R18", "p3r18", false)]   // exact match only
        [TestCase(null, "p3r18", false)]      // no tag published
        [TestCase("", "p3r18", false)]
        public void SameBuild_OnlyExactProtocolTagMatches(string room, string mine, bool expected)
        {
            // Arrange + Act
            bool same = ChibiRoomRules.SameBuild(room, mine);

            // Assert
            Assert.AreEqual(expected, same);
        }
    }
}
