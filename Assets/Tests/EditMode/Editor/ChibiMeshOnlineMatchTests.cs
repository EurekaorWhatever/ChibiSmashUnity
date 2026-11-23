using System;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Team play phase 2 (protocol 6): <see cref="ChibiOnlineMatch"/> over a full mesh of in-memory links. Covers the
    /// roster handshake (Start roster, PeerHello on guest links, slot binding, lobby-roster checks), leavers in team play
    /// (Loss for the leaver, the rest play on) and "one desync = no contest for everyone".
    /// </summary>
    public class ChibiMeshOnlineMatchTests
    {
        const uint Seed = 0xBEEFu;
        const int HandshakeBudget = 900;
        static readonly uint[] Hashes = { 0x1001u, 0x2002u, 0x3003u, 0x4004u };
        static readonly FighterClass[] Classes = { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic };

        static ChibiInputPacket Bot(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)(n / 5) * 2654435761u + (uint)seed * 40503u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                int x = (int)(h % 3) - 1;
                ChibiButtons b = ChibiButtons.None;
                if (h % 7 == 0) b |= ChibiButtons.Light;
                if (h % 11 == 1) b |= ChibiButtons.Jump;
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, 0), b);
            }
        }

        sealed class Mesh
        {
            public ChibiLoopbackLink[,] L;
            public ChibiOnlineMatch[] M;
            public int N;

            public ChibiLoopbackLink Link(int i, int j) => i < j ? L[i, j] : L[j, i];

            public IChibiTransport[] RawFor(int i)
            {
                var r = new IChibiTransport[N];
                for (int j = 0; j < N; j++) if (j != i) r[j] = i < j ? L[i, j].A : L[j, i].B;
                return r;
            }

            public void Tick()
            {
                foreach (var m in M) if (m != null && m.Phase != ChibiOnlinePhase.Closed) m.Tick();
                foreach (var l in L) l?.Advance();
            }

            public bool RunUntil(Func<bool> done, int max)
            {
                for (int t = 0; t < max; t++) { if (done()) return true; Tick(); }
                return done();
            }
        }

        /// <summary>Mesh with every peer built by the default rules; <paramref name="joinerHashes"/> overrides what joiners expect.</summary>
        static Mesh NewMesh(int format, int count, uint[] joinerHashes = null, int loss = 0)
        {
            var m = new Mesh { N = count, L = new ChibiLoopbackLink[count, count], M = new ChibiOnlineMatch[count] };
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                    m.L[i, j] = new ChibiLoopbackLink(2, 1, loss);
            var hashes = new uint[count];
            Array.Copy(Hashes, hashes, count);
            m.M[0] = ChibiOnlineMatch.MeshHost(m.RawFor(0), new ChibiScriptedInputSource(n => Bot(1, n)), format, hashes,
                                               Classes[0], 0, stage: 0, Seed, ChibiRollbackSettings.Default);
            for (int i = 1; i < count; i++)
            {
                int seed = i + 1;
                m.M[i] = ChibiOnlineMatch.MeshJoin(m.RawFor(i), i, new ChibiScriptedInputSource(n => Bot(seed, n)), format,
                                                   joinerHashes ?? hashes, Classes[i], i & 1);
            }
            return m;
        }

        static bool AllPlaying(Mesh m)
        {
            foreach (var x in m.M) if (x.Phase != ChibiOnlinePhase.Playing) return false;
            return true;
        }

        // ================================================================== handshake

        [TestCase(ChibiMatchConfig.Format2v2, 4)]
        [TestCase(ChibiMatchConfig.FormatFfa, 3)]
        [TestCase(ChibiMatchConfig.FormatFfa, 4)]
        public void MeshHandshake_AllPeersAgreeOnTheRoster_AndPlay(int format, int count)
        {
            // Arrange
            var m = NewMesh(format, count, loss: 10);

            // Act
            bool started = m.RunUntil(() => AllPlaying(m), HandshakeBudget);
            for (int t = 0; t < 600; t++) m.Tick();

            // Assert
            Assert.IsTrue(started, "not every peer started");
            uint hash = m.M[0].Handshake.MatchHash;
            for (int i = 0; i < count; i++)
            {
                var hs = m.M[i].Handshake;
                Assert.AreEqual(i, m.M[i].LocalIndex);
                Assert.AreEqual(hash, hs.MatchHash, "peer " + i + " agreed to another match");
                for (int s = 0; s < count; s++)
                {
                    Assert.AreEqual(Classes[s], hs.Config.ClassOf(s));
                    Assert.AreEqual(ChibiControlCodec.TeamFor(format, s), hs.Config.RawTeam(s));
                    Assert.AreEqual(Hashes[s], hs.PlayerHashOf(s));
                }
                Assert.IsFalse(m.M[i].Session.Desynced, "peer " + i + " desynced");
                Assert.IsFalse(m.M[i].HasResult, "peer " + i + " ended early: " + m.M[i].Result);
            }
        }

        [Test]
        public void StartWithAnotherPlayerInARosterSlot_IsRefusedAsInvalidConfig()
        {
            // Arrange: joiners believe the lobby put a different player in slot 2 than the one who actually connected.
            var lobby = new uint[] { Hashes[0], Hashes[1], 0xBADu };
            var m = NewMesh(ChibiMatchConfig.FormatFfa, 3);
            // Slot 1 is rebuilt with the stale roster.
            m.M[1] = ChibiOnlineMatch.MeshJoin(m.RawFor(1), 1, new ChibiScriptedInputSource(n => default),
                                               ChibiMatchConfig.FormatFfa, lobby, Classes[1], 1);

            // Act
            m.RunUntil(() => m.M[1].HasResult, HandshakeBudget);

            // Assert
            Assert.AreEqual(ChibiMatchOutcome.Aborted, m.M[1].Result.Outcome);
            Assert.AreEqual(ChibiRejectReason.InvalidConfig, m.M[1].Result.RejectReason);
        }

        [Test]
        public void HelloClaimingAnotherSlotOnABoundLink_IsRefused()
        {
            // Arrange: 3-peer FFA. The peer on the host's slot-1 link claims to be slot 2.
            var m = NewMesh(ChibiMatchConfig.FormatFfa, 3);
            var raw = m.RawFor(1);
            var liar = new IChibiTransport[] { raw[0], raw[2], null };   // host link first; claims slot 2
            m.M[1] = ChibiOnlineMatch.MeshJoin(liar, 2, new ChibiScriptedInputSource(n => default), ChibiMatchConfig.FormatFfa,
                                               new uint[] { Hashes[0], Hashes[1], Hashes[2] }, Classes[1], 0);

            // Act
            m.RunUntil(() => m.M[0].HasResult, HandshakeBudget);

            // Assert
            Assert.AreEqual(ChibiMatchOutcome.Aborted, m.M[0].Result.Outcome);
            Assert.AreEqual(ChibiRejectReason.InvalidConfig, m.M[0].Result.RejectReason);
            Assert.AreEqual(1, m.M[0].Handshake.ProblemSlot);
        }

        // ================================================================== leavers and desync

        [Test]
        public void Forfeit_InFfa_LeaverLoses_OthersPlayOnWithoutIt()
        {
            // Arrange
            var m = NewMesh(ChibiMatchConfig.FormatFfa, 3);
            Assert.IsTrue(m.RunUntil(() => AllPlaying(m), HandshakeBudget), "precondition: started");
            for (int t = 0; t < 300; t++) m.Tick();

            // Act
            m.M[2].Forfeit();
            for (int t = 0; t < 400; t++) m.Tick();

            // Assert
            Assert.AreEqual(ChibiMatchOutcome.Loss, m.M[2].Result.Outcome);
            Assert.AreEqual(ChibiEndCause.LocalForfeit, m.M[2].Result.Cause);
            for (int i = 0; i < 2; i++)
            {
                var s = m.M[i].Session;
                Assert.AreEqual(1 << 2, s.DroppedMask, "survivor " + i + " did not drop the leaver");
                Assert.IsFalse(s.Desynced);
                Assert.IsTrue(m.M[i].Phase == ChibiOnlinePhase.Playing || m.M[i].Result.Cause == ChibiEndCause.MatchOver,
                              "survivor " + i + " ended for the wrong reason: " + m.M[i].Result);
            }
        }

        [Test]
        public void ForgedChecksumFromOnePeer_EndsAsNoContestForEveryone()
        {
            // Arrange
            var m = NewMesh(ChibiMatchConfig.Format2v2, 4);
            Assert.IsTrue(m.RunUntil(() => AllPlaying(m), HandshakeBudget), "precondition: started");
            Assert.Less(m.M[0].Session.Frame, ChibiRollbackSession.ChecksumInterval, "precondition");
            var lie = new ChibiNetMessage
            {
                Sender = 3, LastFrame = 0, AckFrame = -1, Count = 1, HasChecksum = true,
                ChecksumFrame = ChibiRollbackSession.ChecksumInterval, Checksum = 0x12345678u,
                HoldMask = 0b0111,   // protocol 6: a hold for every other slot (values: holds nothing yet)
            };
            lie.Hold[0] = lie.Hold[1] = lie.Hold[2] = -1;
            var bb = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(bb, lie);

            // Act: slot 3's link to the host carries a wrong frame-30 hash.
            m.Link(0, 3).B.Send(bb.Bytes, bb.ByteLength);
            m.RunUntil(() => Array.TrueForAll(m.M, x => x.HasResult), 900);

            // Assert
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(ChibiMatchOutcome.NoContest, m.M[i].Result.Outcome, "peer " + i + ": " + m.M[i].Result);
        }

        // ================================================================== codec

        [Test]
        public void StartRoster_RoundTrips_AndHashesTheRoster()
        {
            var cfg = new ChibiMatchConfig { Seed = Seed, Format = ChibiMatchConfig.Format2v2, Count = 4, Stage = 3, Intro = true };
            for (int i = 0; i < 4; i++) ChibiControlCodec.SetSlot(ref cfg, i, Classes[i], i & 1, ChibiControlCodec.TeamFor(cfg.Format, i));
            var w = new ChibiControlMessage
            {
                Type = ChibiControlType.Start, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = ChibiMatchConfig.RulesVersion,
                Config = cfg, Settings = ChibiRollbackSettings.Default, Name = "Host",
            };
            Array.Copy(Hashes, w.PlayerHashes, 4);
            w.MatchHash = ChibiControlCodec.MatchHash(cfg, w.Settings, w.PlayerHashes);
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            ChibiControlCodec.Write(b, w);
            var bytes = new byte[b.ByteLength];
            Buffer.BlockCopy(b.Bytes, 0, bytes, 0, bytes.Length);
            var r = new BitBuffer(ChibiControlCodec.MaxBytes);
            r.LoadFrom(bytes, bytes.Length);
            var m = new ChibiControlMessage();

            Assert.IsTrue(ChibiControlCodec.TryRead(r, m));
            var got = m.Config; got.Intro = true;
            Assert.AreEqual(w.MatchHash, ChibiControlCodec.MatchHash(got, m.Settings, m.PlayerHashes));
            for (int i = 0; i < 4; i++) Assert.AreEqual(Hashes[i], m.PlayerHashes[i]);
            Assert.AreEqual("Host", m.ReceivedName());
            uint swapped = ChibiControlCodec.MatchHash(got, m.Settings, new[] { Hashes[1], Hashes[0], Hashes[2], Hashes[3] });
            Assert.AreNotEqual(w.MatchHash, swapped, "swapping two players must change the match hash");
        }

        [Test]
        public void ControlCodec_RandomBytes_NeverThrow()
        {
            var rng = new ChibiRng(7);
            var raw = new byte[ChibiControlCodec.MaxBytes];
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            var m = new ChibiControlMessage();
            for (int i = 0; i < 20000; i++)
            {
                int n = 1 + rng.Range(raw.Length);
                for (int k = 0; k < n; k++) raw[k] = (byte)rng.Range(256);
                raw[0] = ChibiControlCodec.Magic;
                b.LoadFrom(raw, n);
                Assert.DoesNotThrow(() => ChibiControlCodec.TryRead(b, m));
            }
        }
    }
}
