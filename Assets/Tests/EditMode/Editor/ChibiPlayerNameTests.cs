// Story type: Logic (player names over the wire) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Spec: docs/architecture/netcode/online-social.md ("Player names in the match"). A name is chosen by each player, sent in
// the handshake and drawn on the opponent's HUD, so everything here treats the peer's name as hostile input.
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Handshake name trailer (codec + agreement) and the saved-name rules.</summary>
    public class ChibiPlayerNameTests
    {
        static ChibiControlMessage Hello(string name) => new ChibiControlMessage
        {
            Type = ChibiControlType.Hello, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = ChibiMatchConfig.RulesVersion,
            Class = FighterClass.Magic, Passive = 1, Name = name,
        };

        static ChibiControlMessage RoundTrip(ChibiControlMessage m, out bool ok)
        {
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            ChibiControlCodec.Write(b, m);
            var rx = new BitBuffer(ChibiControlCodec.MaxBytes);
            rx.LoadFrom(b.Bytes, b.ByteLength);
            var o = new ChibiControlMessage();
            ok = ChibiControlCodec.TryRead(rx, o);
            return o;
        }

        // ================================================================== codec

        [Test]
        public void Hello_NameRoundTrips()
        {
            var o = RoundTrip(Hello("Kira#4821"), out bool ok);
            Assert.IsTrue(ok);
            Assert.AreEqual("Kira#4821", o.ReceivedName());
            Assert.AreEqual(FighterClass.Magic, o.Class);
            Assert.AreEqual(1, o.Passive);
        }

        [Test]
        public void Name_RichTextAndControlChars_NeverReachTheWire()
        {
            var o = RoundTrip(Hello("<size=500><color=red>Ki ra</color>\n\t"), out bool ok);
            Assert.IsTrue(ok);
            string n = o.ReceivedName();
            foreach (char c in n) StringAssert.Contains(c.ToString(), ChibiControlCodec.NameAlphabet);
            Assert.IsFalse(n.Contains("<") || n.Contains(">") || n.Contains("=") || n.Contains(" ") || n.Contains("\n"), n);
            StringAssert.Contains("Kira", n);
        }

        [Test]
        public void Name_LongerThanMax_IsCut()
        {
            var o = RoundTrip(Hello(new string('A', 200)), out bool ok);
            Assert.IsTrue(ok);
            Assert.AreEqual(ChibiControlCodec.NameMaxChars, o.NameLength);
        }

        [Test]
        public void Start_WithLongestName_FitsTheControlBuffer()
        {
            var m = new ChibiControlMessage
            {
                Type = ChibiControlType.Start, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = ChibiMatchConfig.RulesVersion,
                Config = new ChibiMatchConfig { Seed = 0xFFFFFFFF, Class0 = FighterClass.Polearm, Class1 = FighterClass.Polearm, Stage = 2 },
                Settings = ChibiRollbackSettings.Default, MatchHash = 0xFFFFFFFF, Name = "ABCDEFGHIJKLMNOP#1234567",
            };
            var o = RoundTrip(m, out bool ok);
            Assert.IsTrue(ok);
            Assert.AreEqual("ABCDEFGHIJKLMNOP#1234567", o.ReceivedName());
            Assert.AreEqual(0xFFFFFFFFu, o.MatchHash);
        }

        [Test]
        public void Hello_FromOlderBuildWithoutName_StillAccepted()
        {
            // The pre-name wire format: exactly the old fields, nothing after them.
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            b.Write(ChibiControlCodec.Magic, 8);
            b.Write((uint)ChibiControlType.Hello, 4);
            b.Write((uint)ChibiPacketCodec.ProtocolVersion, 8);
            b.Write((uint)ChibiMatchConfig.RulesVersion, 16);
            b.Write((uint)FighterClass.Heavy, 4);
            b.Write(0u, 1);
            var rx = new BitBuffer(ChibiControlCodec.MaxBytes);
            rx.LoadFrom(b.Bytes, b.ByteLength);
            var o = new ChibiControlMessage();

            Assert.IsTrue(ChibiControlCodec.TryRead(rx, o));
            Assert.AreEqual(0, o.NameLength);
            Assert.AreEqual("", o.ReceivedName());
            Assert.AreEqual(FighterClass.Heavy, o.Class);
        }

        [Test]
        public void Hello_NameLengthAboveMax_Malformed()
        {
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            b.Write(ChibiControlCodec.Magic, 8);
            b.Write((uint)ChibiControlType.Hello, 4);
            b.Write((uint)ChibiPacketCodec.ProtocolVersion, 8);
            b.Write((uint)ChibiMatchConfig.RulesVersion, 16);
            b.Write((uint)FighterClass.Heavy, 4);
            b.Write(0u, 1);
            b.Write(31u, 5);   // claims 31 characters (max is 24)
            for (int i = 0; i < 31; i++) b.Write(0u, 6);
            var rx = new BitBuffer(ChibiControlCodec.MaxBytes);
            rx.LoadFrom(b.Bytes, b.ByteLength);

            Assert.IsFalse(ChibiControlCodec.TryRead(rx, new ChibiControlMessage()));
        }

        [Test]
        public void Hello_NameTruncatedMidway_Malformed()
        {
            var full = new BitBuffer(ChibiControlCodec.MaxBytes);
            ChibiControlCodec.Write(full, Hello("KiraKiraKira"));
            var rx = new BitBuffer(ChibiControlCodec.MaxBytes);
            rx.LoadFrom(full.Bytes, full.ByteLength - 3);   // cut inside the name characters

            Assert.IsFalse(ChibiControlCodec.TryRead(rx, new ChibiControlMessage()));
        }

        // ================================================================== handshake

        static ChibiInputPacket Idle(int n) => ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(0, 0), ChibiButtons.None);

        static (ChibiOnlineMatch host, ChibiOnlineMatch join) Duel(string hostName, string joinName, int loss = 20)
        {
            var link = new ChibiLoopbackLink(4, 1, loss);
            var h = ChibiOnlineMatch.Host(link.A, new ChibiScriptedInputSource(Idle), FighterClass.Melee, 0, 1, 0xC0FFEEu,
                                          ChibiRollbackSettings.Default, ChibiHandshake.DefaultTimeoutTicks, hostName);
            var j = ChibiOnlineMatch.Join(link.B, new ChibiScriptedInputSource(Idle), FighterClass.Ranged, 1,
                                          ChibiHandshake.DefaultTimeoutTicks, joinName);
            for (int t = 0; t < 600 && !(h.Phase == ChibiOnlinePhase.Playing && j.Phase == ChibiOnlinePhase.Playing); t++)
            {
                link.Advance();
                h.Tick();
                j.Tick();
            }
            return (h, j);
        }

        [Test]
        public void Handshake_LossyLink_EachSideLearnsTheOthersName()
        {
            var (h, j) = Duel("Kira#4821", "Pip_99#17");

            Assert.AreEqual(ChibiOnlinePhase.Playing, h.Phase);
            Assert.AreEqual(ChibiOnlinePhase.Playing, j.Phase);
            Assert.AreEqual("Pip_99#17", h.Handshake.PeerName);
            Assert.AreEqual("Kira#4821", j.Handshake.PeerName);
            Assert.AreEqual("Kira#4821", h.Handshake.LocalName);
        }

        [Test]
        public void Handshake_NamesAreCosmetic_MatchHashUnchanged()
        {
            var (h1, _) = Duel("Kira#4821", "Pip#1", loss: 0);
            var (h2, _) = Duel("", "", loss: 0);

            Assert.AreEqual(h2.Handshake.MatchHash, h1.Handshake.MatchHash);
            Assert.AreEqual("", h2.Handshake.PeerName);
        }

        // ================================================================== impersonation + blocked words (security review SEC-002)

        [Test]
        public void PeerDisplayName_Unverified_DropsTheTag_SoNobodyCanPoseAsAFriend()
        {
            Assert.AreEqual("Kira", ChibiSocialRules.PeerDisplayName("Kira#4821", verified: false));
        }

        [Test]
        public void PeerDisplayName_VerifiedFriend_ShownInFull()
        {
            Assert.AreEqual("Kira#4821", ChibiSocialRules.PeerDisplayName("Kira#4821", verified: true));
        }

        [TestCase("Sh1tLord")] [TestCase("F_U_C_K")] [TestCase("xXNaziXx#12")]
        public void PeerDisplayName_BlockedWord_ShownAsPlayer(string raw)
        {
            Assert.AreEqual("Player", ChibiSocialRules.PeerDisplayName(raw, verified: false));
        }

        [Test]
        public void PeerDisplayName_NoNameOrOnlyTag_Handled()
        {
            Assert.AreEqual("", ChibiSocialRules.PeerDisplayName("", verified: false));
            Assert.AreEqual("Player", ChibiSocialRules.PeerDisplayName("#1234", verified: false));
        }

        [TestCase("Sh1t")] [TestCase("admin_kira")] [TestCase("OFFICIAL")]
        public void ValidateName_BlockedWord_Rejected(string name) => Assert.IsNotNull(ChibiSocialRules.ValidateName(name));

        [TestCase("Skillz")] [TestCase("Kira")] [TestCase("Assassin_7")] [TestCase("Scunthorpe_ok")] [TestCase("Grape Escape")]
        [TestCase("Peacock")] [TestCase("Cocktail Bar")]
        public void ContainsBlockedWord_InnocentNames_Allowed(string name) =>
            Assert.IsFalse(ChibiSocialRules.ContainsBlockedWord(name), name);

        [TestCase("Grape rape")] [TestCase("peacock dick")]
        public void ContainsBlockedWord_AllowListCannotHideARealBlockedWord(string name) =>
            Assert.IsTrue(ChibiSocialRules.ContainsBlockedWord(name), name);

        // ================================================================== saved name rules

        [TestCase("Kira#4821", "Kira")] [TestCase("Kira", "Kira")] [TestCase("", "")] [TestCase(null, "")]
        public void BaseName_DropsTheTag(string input, string expected) => Assert.AreEqual(expected, ChibiSocialRules.BaseName(input));

        [TestCase("Kira", "Kira")] [TestCase("  Pip_99 ", "Pip_99")]
        public void CleanSavedName_Valid_Kept(string saved, string expected) =>
            Assert.AreEqual(expected, ChibiSocialRules.CleanSavedName(saved));

        [TestCase(null)] [TestCase("")] [TestCase("ab")] [TestCase("<b>Kira</b>")] [TestCase("Kira#1234")] [TestCase("ABCDEFGHIJKLMNOPQ")]
        public void CleanSavedName_TamperedOrInvalid_Empty(string saved) => Assert.AreEqual("", ChibiSocialRules.CleanSavedName(saved));
    }
}
