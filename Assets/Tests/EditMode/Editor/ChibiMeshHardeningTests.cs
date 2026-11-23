using System;
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Protocol 6 hardening (network + security review): per-link flood cap and mux pump cap, a leaver that never played
    /// (drop at or below InputDelay) is a no contest for everyone with the culprit recorded, 2v2 scoring by team with a
    /// leaver, and NET-03 per peer.
    /// </summary>
    public class ChibiMeshHardeningTests
    {
        const uint Seed = 0x5EEDu;

        static ChibiMatchConfig Ffa3() => new ChibiMatchConfig
        {
            Seed = Seed, Format = ChibiMatchConfig.FormatFfa, Count = 3,
            Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy, Class2 = FighterClass.Ranged, Stage = 0, HazardsOff = true,
        };

        static ChibiInputPacket Bot(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)(n / 5) * 2654435761u + (uint)seed * 40503u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                int x = (int)(h % 3) - 1;
                ChibiButtons b = ChibiButtons.None;
                if (h % 5 == 0) b |= ChibiButtons.Light;
                if (h % 11 == 1) b |= ChibiButtons.Jump;
                if (h % 9 == 2) b |= ChibiButtons.Heavy;
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, 0), b);
            }
        }

        sealed class Probe : IChibiTransport
        {
            public readonly Queue<byte[]> Inbox = new Queue<byte[]>();
            public void Send(byte[] data, int length) { }
            public int Receive(byte[] buffer)
            {
                if (Inbox.Count == 0) return 0;
                var d = Inbox.Dequeue();
                Buffer.BlockCopy(d, 0, buffer, 0, d.Length);
                return d.Length;
            }
        }

        /// <summary>3-slot probe encode; fills the hold block protocol 6 requires (values -1: holds nothing).</summary>
        static byte[] Encode(ChibiNetMessage m)
        {
            for (int k = 0; k < 3; k++) if (k != m.Sender) { m.HoldMask |= 1 << k; m.Hold[k] = -1; }
            var b = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(b, m);
            var bytes = new byte[b.ByteLength];
            Buffer.BlockCopy(b.Bytes, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        // ================================================================== flood caps

        [Test]
        public void FloodingPeer_OnlyFillsItsOwnLink_TheOtherLinkKeepsDelivering()
        {
            // Arrange: slot 0 of 3; slot 1 is honest, slot 2 floods 500 junk datagrams per tick.
            var p1 = new Probe();
            var p2 = new Probe();
            var s = new ChibiRollbackSession(0, Ffa3(), new IChibiTransport[] { null, p1, p2 },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);
            var junk = new byte[] { ChibiPacketCodec.Magic, 0xFF, 0xFF };
            int f = s.Settings.InputDelay;

            // Act
            for (int t = 0; t < 20; t++, f++)
            {
                for (int k = 0; k < 500; k++) p2.Inbox.Enqueue(junk);
                p1.Inbox.Enqueue(Encode(new ChibiNetMessage { Sender = 1, LastFrame = f, AckFrame = -1, Count = 1 }));
                s.Tick();
            }

            // Assert: every honest datagram was read, and the flood was read at most 16 per tick.
            Assert.AreEqual(f - 1, s.RemoteContiguousOf(1), "the honest link was starved by the flood");
            Assert.AreEqual(20 * 500 - 20 * ChibiRollbackSession.MaxDatagramsPerLinkPerTick, p2.Inbox.Count,
                            "the flooding link was read past its per-tick cap");
        }

        [Test]
        public void MuxPump_ReadsAtMostTheCapPerCall()
        {
            var raw = new Probe();
            for (int i = 0; i < 1000; i++) raw.Inbox.Enqueue(new byte[] { ChibiPacketCodec.Magic, 1, 2, 3 });
            var mux = new ChibiChannelMux(raw);

            mux.Pump();

            Assert.AreEqual(1000 - ChibiChannelMux.MaxPumpPerCall, raw.Inbox.Count);
        }

        // ================================================================== NET-03 per peer

        [Test]
        public void Net03_PerPeer_OnlyRealInputFramesCount()
        {
            var p1 = new Probe();
            var p2 = new Probe();
            var s = new ChibiRollbackSession(0, Ffa3(), new IChibiTransport[] { null, p1, p2 },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);
            int delay = s.Settings.InputDelay;

            // slot 1 sends a real frame; slot 2 only repeats a pre-seeded frame (no real input).
            p1.Inbox.Enqueue(Encode(new ChibiNetMessage { Sender = 1, LastFrame = delay, AckFrame = -1, Count = 1 }));
            p2.Inbox.Enqueue(Encode(new ChibiNetMessage { Sender = 2, LastFrame = delay - 1, AckFrame = -1, Count = 1 }));
            s.Tick();

            Assert.IsTrue(s.HasInputFrom(1));
            Assert.IsFalse(s.HasInputFrom(2), "a stale frame counted as play");
            Assert.IsFalse(s.HasRemoteInput, "not every peer has played");
        }

        // ================================================================== online mesh

        sealed class Mesh
        {
            public ChibiLoopbackLink[,] L;
            public ChibiOnlineMatch[] M;
            public bool[] Frozen;
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
                for (int i = 0; i < N; i++) if (!Frozen[i] && M[i].Phase != ChibiOnlinePhase.Closed) M[i].Tick();
                foreach (var l in L) l?.Advance();
            }
        }

        static Mesh NewMesh(int format, int count)
        {
            var m = new Mesh { N = count, L = new ChibiLoopbackLink[count, count], M = new ChibiOnlineMatch[count], Frozen = new bool[count] };
            for (int i = 0; i < count; i++) for (int j = i + 1; j < count; j++) m.L[i, j] = new ChibiLoopbackLink(2, 0, 0);
            var hashes = new uint[count];
            for (int i = 0; i < count; i++) hashes[i] = (uint)(100 + i);
            var classes = new[] { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Polearm };
            m.M[0] = ChibiOnlineMatch.MeshHost(m.RawFor(0), new ChibiScriptedInputSource(n => Bot(1, n)), format, hashes,
                                               classes[0], 0, 0, Seed, ChibiRollbackSettings.Default, hazardsOff: true);
            for (int i = 1; i < count; i++)
            {
                int seed = i + 1;
                m.M[i] = ChibiOnlineMatch.MeshJoin(m.RawFor(i), i, new ChibiScriptedInputSource(n => Bot(seed, n)), format,
                                                   hashes, classes[i], 0);
            }
            return m;
        }

        [Test]
        public void LeaverThatNeverPlayed_DropAtOrBelowInputDelay_IsNoContestForEveryone_WithTheCulprit()
        {
            // Arrange: slot 2 finishes the handshake, then never sends a single gameplay datagram.
            var m = NewMesh(ChibiMatchConfig.FormatFfa, 3);
            for (int t = 0; t < 900 && !(m.M[0].Phase == ChibiOnlinePhase.Playing && m.M[1].Phase == ChibiOnlinePhase.Playing); t++)
            {
                if (m.M[2].Phase == ChibiOnlinePhase.Playing) m.Frozen[2] = true;   // agreed: from here on it is silent
                m.Tick();
            }
            Assert.IsTrue(m.Frozen[2], "precondition: slot 2 agreed");

            // Act: past the first-datagram grace and the timeout.
            for (int t = 0; t < 2500 && !(m.M[0].HasResult && m.M[1].HasResult); t++) m.Tick();

            // Assert
            for (int i = 0; i < 2; i++)
            {
                var r = m.M[i].Result;
                Assert.AreEqual(ChibiMatchOutcome.NoContest, r.Outcome, "survivor " + i + ": " + r);
                Assert.AreEqual(ChibiEndCause.PeerNeverStarted, r.Cause);
                Assert.AreEqual(2, r.CulpritSlot);
            }
        }

        [Test]
        public void TwoVsTwo_WithALeaver_LeaverLoses_EveryoneElseIsScoredByTeam()
        {
            // Arrange: 2v2 (slots 0,1 vs 2,3) in play.
            var m = NewMesh(ChibiMatchConfig.Format2v2, 4);
            for (int t = 0; t < 900 && Array.Exists(m.M, x => x.Phase != ChibiOnlinePhase.Playing); t++) m.Tick();
            Assert.IsTrue(Array.TrueForAll(m.M, x => x.Phase == ChibiOnlinePhase.Playing), "precondition: started");
            for (int t = 0; t < 300; t++) m.Tick();

            // Act: slot 3 quits; the rest play to the end (KOs or the timer).
            m.M[3].Forfeit();
            for (int t = 0; t < 12000 && !(m.M[0].HasResult && m.M[1].HasResult && m.M[2].HasResult); t++) m.Tick();

            // Assert
            Assert.AreEqual(ChibiMatchOutcome.Loss, m.M[3].Result.Outcome, "the leaver must lose");
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(ChibiEndCause.MatchOver, m.M[i].Result.Cause, "slot " + i + ": " + m.M[i].Result);
                Assert.AreEqual(1 << 3, m.M[i].Session.DroppedMask);
            }
            int w = m.M[0].Result.Winner;
            Assert.AreEqual(w, m.M[1].Result.Winner);
            Assert.AreEqual(w, m.M[2].Result.Winner);
            ChibiMatchOutcome For(int team) => w < 0 ? ChibiMatchOutcome.Draw : w == team ? ChibiMatchOutcome.Win : ChibiMatchOutcome.Loss;
            Assert.AreEqual(For(0), m.M[0].Result.Outcome, "slot 0 is team 0");
            Assert.AreEqual(For(0), m.M[1].Result.Outcome, "slot 1 is team 0");
            Assert.AreEqual(For(1), m.M[2].Result.Outcome, "slot 2 (the leaver's teammate) is team 1");
        }
    }
}
