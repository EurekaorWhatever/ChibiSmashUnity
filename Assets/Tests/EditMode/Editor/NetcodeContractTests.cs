// Story type: Logic (netcode core contract / tripwire) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Spec: docs/architecture/netcode/DEVELOPER-GUIDE.md. These tests do not test a feature: they pin the things two
// players' builds must agree on. When one fails, read its message: it says whether the change is fine (bump a version
// and update the golden value) or a mistake (something non-deterministic leaked into the simulation).
using System.Collections.Generic;
using System.Text;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Tripwires for the online core: rules version, wire format, determinism fingerprints.</summary>
    public class NetcodeContractTests
    {
        // ------------------------------------------------------------------ pinned values (update ONLY with a version bump)

        /// <summary>The rules version these golden values were recorded with.</summary>
        /// <remarks>
        /// 18: re-recorded ON PURPOSE with the character movesets (design/gdd/movesets, CH-3/CH-4). Causes, all intended:
        /// the config hash now mixes RulesVersion 18 and every moveset row / resolution table / constant; the fixture
        /// scripts press Light / Heavy with a horizontal direction, which rules v18 resolves to forward tilts and forward
        /// smashes (and aerials in the air) instead of the jab / heavy; new rollback fields (TechCd, charge, air mask, pull,
        /// zones, projectile SrcSlot / Bounces / Rearm) are in the checksum. Neutral inputs are proven unchanged against
        /// the v17 build by ChibiMovesetRegressionTests.
        /// 19: re-recorded ON PURPOSE with the security fixes SEC-101..106 (Jade Stance cooldown, reflect growth cap with
        /// the new hashed projectile field Reflects, teleport clamp, stale move flags cleared). Fixture A is unchanged
        /// (no live projectile at its end, no affected move); fixture B (Magic vs Polearm) moves because its live bolts now
        /// hash Reflects. The config hash moves with RulesVersion, Jade Stance's cd / slot and the three new constants.
        /// 20: re-recorded ON PURPOSE with damage percent + stocks and the five new stages (percent-knockback.md, stages
        /// README, GL-1..GL-3, ST-1..ST-5). Causes, all intended: HpU left the fighter state and PctM / Stocks / OutTicks /
        /// HoverTicks / LaunchDecX / LaunchDecY / LaunchGravP / KosTaken joined it, the state hashes the hazard machine and
        /// its own RNG stream; every landed hit now launches by the knockback formula (decay, gravity fade, KB-based
        /// hitstun), so both fixtures play differently; the config hash mixes RulesVersion 20, the percent / knockback /
        /// stock constants, HazardsOff, the selected stage's geometry and the hazard constants. Fixtures stay on classic
        /// stages 0 and 2 (unchanged geometry); determinism on stages 3..7 is covered by ChibiStagesV20Tests and the
        /// 8-stage hostile-link matrix in ChibiSyncVerificationTests.
        /// 21: re-recorded ON PURPOSE (lead: GL-2 applied consistently, Sword Aura waves are not ult hits). Only the config
        /// hash moves (RulesVersion 21 and the stage hash now mixing the floor fields); both fixtures run on classic stages
        /// and never fire a Sword Aura wave, so their checksums are unchanged, as expected.
        /// 22: re-recorded ON PURPOSE (UL-1 / EL-1 / TN-1, 2026-11-14): the Ult press fires the ult, recovery trimmed, 6-tick
        /// attack buffer and walk cancel with three new hashed fighter fields and FeelConstants in the config hash, so the
        /// config hash and both fixture checksums move.
        /// </remarks>
        const int PinnedRulesVersion = 22;
        /// <summary>The wire protocol version the wire golden values were recorded with.</summary>
        /// <remarks>3: re-recorded ON PURPOSE (CH-3) - input bit 15 changed meaning from PoseVerified to Special.
        /// 4: re-recorded ON PURPOSE (rules v20, ST-5) - the Start message carries a 1-bit HazardsOff after the stage and the
        /// stage range is 0..7; Hello keeps its layout, only its protocol byte (and the rules field) change.</remarks>
        const int PinnedProtocolVersion = 4;
        /// <summary>Hash of every gameplay constant table + the fixture config (ChibiMatchConfig.ComputeHash).</summary>
        const uint GoldenConfigHash = 0x26C1875Bu;
        /// <summary>Final state checksum of fixture match A after <see cref="Ticks"/> ticks.</summary>
        const uint GoldenChecksumA = 0xE72052CEu;
        /// <summary>Final state checksum of fixture match B (other fighters, stage, intro off).</summary>
        const uint GoldenChecksumB = 0x1D0DA211u;
        /// <summary>Bytes of a Hello control datagram (protocol, rules, class, passive, name "Kira").</summary>
        /// <remarks>Re-recorded on purpose: protocol 4 and rules 22 (same Hello layout; only the rules field moved, 21 -> 22,
        /// so ProtocolVersion stays 4).</remarks>
        const string GoldenHelloHex = "c9416001208922ae1a";

        const int Ticks = 1800;   // 30 s of play: movement, attacks, skills, jumps, hits, KOs

        static readonly ChibiMatchConfig FixtureA = new ChibiMatchConfig
            { Seed = 0x12345678u, Class0 = FighterClass.Melee, Passive0 = 0, Class1 = FighterClass.Heavy, Passive1 = 1, Stage = 0, Intro = true };
        static readonly ChibiMatchConfig FixtureB = new ChibiMatchConfig
            { Seed = 0xCAFEF00Du, Class0 = FighterClass.Magic, Passive0 = 1, Class1 = FighterClass.Polearm, Passive1 = 0, Stage = 2, Intro = false };

        /// <summary>A fixed, busy input script (same every run, no randomness, no clock).</summary>
        static List<ushort> Script(int seed)
        {
            var list = new List<ushort>(Ticks);
            for (int n = 0; n < Ticks; n++)
            {
                unchecked
                {
                    uint h = (uint)(n / 4) * 2654435761u + (uint)seed * 40503u;
                    h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                    ChibiButtons b = ChibiButtons.None;
                    if (n % 2 == 0)
                    {
                        if (h % 3 == 0) b |= ChibiButtons.Light;
                        if (h % 5 == 1) b |= ChibiButtons.Heavy;
                        if (h % 17 == 2) b |= ChibiButtons.Skill1;
                        if (h % 19 == 3) b |= ChibiButtons.Jump;
                    }
                    list.Add(ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes((int)(h % 3) - 1, 0), b).Raw);
                }
            }
            return list;
        }

        static string Hex(byte[] bytes, int length)
        {
            var sb = new StringBuilder(length * 2);
            for (int i = 0; i < length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        const string HowToFixGameplay =
            "\nGameplay or balance changed. That is fine IF intended: bump ChibiMatchConfig.RulesVersion (ChibiSimulation.cs), " +
            "then set PinnedRulesVersion and the golden values in NetcodeContractTests to the new values printed here. " +
            "Old and new builds then refuse to play each other cleanly instead of desyncing. If you did NOT mean to change " +
            "gameplay, something non-deterministic reached the simulation (float math, UnityEngine.Random, Time, Dictionary " +
            "order, a static field): see docs/architecture/netcode/DEVELOPER-GUIDE.md.";

        const string HowToFixWire =
            "\nThe network wire format changed. Bump ChibiPacketCodec.ProtocolVersion (ChibiInputPacket.cs), then update " +
            "PinnedProtocolVersion and GoldenHelloHex here. Never change the format without the bump: two builds would " +
            "misread each other's packets.";

        // ------------------------------------------------------------------ versions

        [Test]
        public void RulesVersion_MatchesThePinnedGoldenValues()
        {
            Assert.AreEqual(PinnedRulesVersion, ChibiMatchConfig.RulesVersion,
                "RulesVersion was bumped: re-record the golden values below (run these tests, copy the printed values)." + HowToFixGameplay);
        }

        [Test]
        public void ProtocolVersion_MatchesThePinnedWireValues()
        {
            Assert.AreEqual(PinnedProtocolVersion, ChibiPacketCodec.ProtocolVersion,
                "ProtocolVersion was bumped: re-record GoldenHelloHex." + HowToFixWire);
        }

        // ------------------------------------------------------------------ determinism fingerprints

        [Test]
        public void ConfigHash_ConstantTablesUnchanged()
        {
            uint h = FixtureA.ComputeHash();
            Assert.AreEqual(GoldenConfigHash, h, "config hash is now 0x" + h.ToString("X8") + "u." + HowToFixGameplay);
        }

        [Test]
        public void FixtureMatchA_FinalChecksumUnchanged()
        {
            uint c = ChibiReplay.Simulate(FixtureA, Script(1), Script(2)).ComputeChecksum();
            Assert.AreEqual(GoldenChecksumA, c, "match A checksum is now 0x" + c.ToString("X8") + "u." + HowToFixGameplay);
        }

        [Test]
        public void FixtureMatchB_FinalChecksumUnchanged()
        {
            uint c = ChibiReplay.Simulate(FixtureB, Script(3), Script(4)).ComputeChecksum();
            Assert.AreEqual(GoldenChecksumB, c, "match B checksum is now 0x" + c.ToString("X8") + "u." + HowToFixGameplay);
        }

        [Test]
        public void FixtureMatch_RunTwice_Identical()
        {
            // Determinism inside one build: the same inputs always give the same state (no hidden global state).
            uint first = ChibiReplay.Simulate(FixtureA, Script(1), Script(2)).ComputeChecksum();
            uint second = ChibiReplay.Simulate(FixtureA, Script(1), Script(2)).ComputeChecksum();
            Assert.AreEqual(first, second, "two runs of the same match differ: hidden state leaked into the simulation." + HowToFixGameplay);
        }

        // ------------------------------------------------------------------ wire format

        [Test]
        public void HelloDatagram_BytesUnchanged()
        {
            var m = new ChibiControlMessage
            {
                Type = ChibiControlType.Hello, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = PinnedRulesVersion,
                Class = FighterClass.Ranged, Passive = 1, Name = "Kira",
            };
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            ChibiControlCodec.Write(b, m);
            string hex = Hex(b.Bytes, b.ByteLength);
            Assert.AreEqual(GoldenHelloHex, hex, "Hello bytes are now \"" + hex + "\"." + HowToFixWire);
        }

        [Test]
        public void WireLimits_Unchanged()
        {
            Assert.AreEqual(256, ChibiChannelMux.MaxDatagram, "datagram cap" + HowToFixWire);
            Assert.AreEqual(0xC9, ChibiControlCodec.Magic, "control magic" + HowToFixWire);
            Assert.AreEqual(64, ChibiControlCodec.NameAlphabet.Length, "name alphabet must stay exactly 64 characters (6 bits)" + HowToFixWire);
            Assert.AreEqual(ChibiSocialRules.DisplayMax, ChibiControlCodec.NameMaxChars, "wire name length = display length");
        }
    }
}
