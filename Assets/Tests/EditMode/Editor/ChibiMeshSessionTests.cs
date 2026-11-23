using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Team play phase 2 (protocol 6): the rollback session over a full mesh of 3..4 peers. Every pair of peers shares one
    /// in-memory <see cref="ChibiLoopbackLink"/>; peer i's link to slot j is links[j]. Covers convergence under loss and
    /// reorder, link-to-slot binding, the drop vote (agreement on the drop frame when peers disagree on the leaver's last
    /// frame), the majority rule for partitions, and per-peer desync attribution.
    /// </summary>
    public class ChibiMeshSessionTests
    {
        const uint Seed = 0x7EA4u;
        const int LongRun = 1500;

        // ================================================================== fixture

        static ChibiMatchConfig Cfg(int format, int count) => new ChibiMatchConfig
        {
            Seed = Seed, Format = format, Count = count,
            Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy, Class2 = FighterClass.Ranged, Class3 = FighterClass.Magic,
            Passive0 = 0, Passive1 = 1, Passive2 = 2, Passive3 = 0,
            Team0 = 0, Team1 = 1, Team2 = format == ChibiMatchConfig.Format2v2 ? 0 : 2, Team3 = format == ChibiMatchConfig.Format2v2 ? 1 : 3,
            Stage = 0, HazardsOff = true,
        };

        /// <summary>Busy deterministic player (pure function of seed and sample index).</summary>
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
                if (h % 13 == 2) b |= ChibiButtons.Heavy;
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, 0), b);
            }
        }

        sealed class Mesh
        {
            public ChibiRollbackSession[] Peers;
            public ChibiLoopbackLink[,] Links;
            public bool[] Running;
            public readonly List<int>[] DropFrames = new List<int>[4];

            public ChibiLoopbackLink Link(int i, int j) => i < j ? Links[i, j] : Links[j, i];

            public void Tick()
            {
                for (int i = 0; i < Peers.Length; i++) if (Running[i]) Peers[i].Tick();
                foreach (var l in Links) l?.Advance();
            }

            /// <summary>Make peer <paramref name="slot"/> vanish: it stops ticking and every link to it loses everything.</summary>
            public void Kill(int slot)
            {
                Running[slot] = false;
                for (int j = 0; j < Peers.Length; j++) if (j != slot) Link(slot, j).LossPercent = 100;
            }
        }

        static Mesh NewMesh(int format, int count, int latency, int jitter, int loss)
        {
            var cfg = Cfg(format, count);
            var m = new Mesh { Peers = new ChibiRollbackSession[count], Links = new ChibiLoopbackLink[count, count], Running = new bool[count] };
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                    m.Links[i, j] = new ChibiLoopbackLink(latency, jitter, loss);
            for (int i = 0; i < count; i++)
            {
                var links = new IChibiTransport[count];
                for (int j = 0; j < count; j++)
                    if (j != i) links[j] = i < j ? m.Links[i, j].A : m.Links[j, i].B;
                int seed = 11 * (i + 1);
                m.Peers[i] = new ChibiRollbackSession(i, cfg, links, new ChibiScriptedInputSource(n => Bot(seed, n)), ChibiRollbackSettings.Default);
                m.Running[i] = true;
                var frames = m.DropFrames[i] = new List<int>();
                m.Peers[i].OnPeerDropped += (slot, frame) => frames.Add(slot * 100000 + frame);
            }
            return m;
        }

        /// <summary>Queue transport for hand-crafted datagrams.</summary>
        sealed class ProbeTransport : IChibiTransport
        {
            public readonly Queue<byte[]> Inbox = new Queue<byte[]>();
            public void Send(byte[] data, int length) { }
            public int Receive(byte[] buffer)
            {
                if (Inbox.Count == 0) return 0;
                var d = Inbox.Dequeue();
                System.Buffer.BlockCopy(d, 0, buffer, 0, d.Length);
                return d.Length;
            }
        }

        /// <summary>
        /// Encode for the 3-slot probe sessions. Protocol 6 team play requires a hold for every slot but the sender's
        /// (second review, BLOCKER 1a): slots the test did not set get LastFrame - 2 (a plausible honest value).
        /// </summary>
        static byte[] Encode(ChibiNetMessage m, int count = 3)
        {
            for (int k = 0; k < count; k++)
                if (k != m.Sender && (m.HoldMask & (1 << k)) == 0) { m.HoldMask |= 1 << k; m.Hold[k] = System.Math.Max(-1, m.LastFrame - 2); }
            var b = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(b, m);
            var bytes = new byte[b.ByteLength];
            System.Buffer.BlockCopy(b.Bytes, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        // ================================================================== convergence

        [TestCase(ChibiMatchConfig.FormatFfa, 3)]
        [TestCase(ChibiMatchConfig.FormatFfa, 4)]
        [TestCase(ChibiMatchConfig.Format2v2, 4)]
        public void Mesh_WithLossAndReorder_ChecksumsAgreeWithEveryPeer(int format, int count)
        {
            // Arrange: 50 ms one way, 0..33 ms jitter (reorders), 10 % loss on every pair.
            var m = NewMesh(format, count, latency: 3, jitter: 2, loss: 10);

            // Act
            for (int t = 0; t < LongRun; t++) m.Tick();

            // Assert: every peer compared checksums with every other peer and never disagreed.
            foreach (var p in m.Peers)
            {
                Assert.IsFalse(p.Desynced, "peer " + p.LocalIndex + " desynced with slot " + p.DesyncPeer);
                Assert.IsFalse(p.Disconnected, "peer " + p.LocalIndex + " disconnected");
                Assert.AreEqual(0, p.DroppedMask, "nobody left");
                Assert.GreaterOrEqual(p.ChecksumsCompared, (count - 1) * 10, "too few checksum comparisons");
                Assert.Greater(p.Frame, LongRun / 2, "the mesh barely advanced");
            }
        }

        // ================================================================== link binding (security BLOCKER 1)

        [Test]
        public void InputOnALinkClaimingAnotherSlot_IsRejected()
        {
            // Arrange: slot 0 in a 3-peer FFA; the link bound to slot 2 is a probe.
            var probe1 = new ProbeTransport();
            var probe2 = new ProbeTransport();
            var s = new ChibiRollbackSession(0, Cfg(ChibiMatchConfig.FormatFfa, 3), new IChibiTransport[] { null, probe1, probe2 },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);
            s.Tick();

            // Act: slot 2's link carries a datagram claiming to come from slot 1.
            probe2.Inbox.Enqueue(Encode(new ChibiNetMessage { Sender = 1, LastFrame = 2, AckFrame = -1, Count = 1 }));
            s.Tick();

            // Assert
            Assert.AreEqual(1, s.RejectedPackets, "a datagram for another slot was accepted");
            Assert.AreEqual(0, s.AcceptedPackets);
            Assert.IsFalse(s.HasInputFrom(1), "the spoofed input was stored for slot 1");
            Assert.IsFalse(s.HasInputFrom(2), "the spoofed input was stored for the link's own slot");
        }

        [Test]
        public void VoteNamingTheSenderOrANonexistentSlot_IsRejected()
        {
            var probe1 = new ProbeTransport();
            var probe2 = new ProbeTransport();
            var s = new ChibiRollbackSession(0, Cfg(ChibiMatchConfig.FormatFfa, 3), new IChibiTransport[] { null, probe1, probe2 },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);
            var self = new ChibiNetMessage { Sender = 2, LastFrame = 2, AckFrame = -1, Count = 1, VoteMask = 1 << 2 };
            var ghost = new ChibiNetMessage { Sender = 2, LastFrame = 2, AckFrame = -1, Count = 1, VoteMask = 1 << 3 };
            ghost.VoteFrame[3] = 5;

            probe2.Inbox.Enqueue(Encode(self));
            probe2.Inbox.Enqueue(Encode(ghost));
            s.Tick();

            Assert.AreEqual(2, s.RejectedPackets);
            Assert.IsFalse(s.VotePending);
        }

        [Test]
        public void HoldBlockMissingASlot_IsRejected()
        {
            var probe1 = new ProbeTransport();
            var probe2 = new ProbeTransport();
            var s = new ChibiRollbackSession(0, Cfg(ChibiMatchConfig.FormatFfa, 3), new IChibiTransport[] { null, probe1, probe2 },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);
            var m = new ChibiNetMessage { Sender = 2, LastFrame = 2, AckFrame = -1, Count = 1, HoldMask = 1 << 0 };   // slot 1 missing
            var b = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(b, m);
            var bytes = new byte[b.ByteLength];
            System.Buffer.BlockCopy(b.Bytes, 0, bytes, 0, bytes.Length);
            probe2.Inbox.Enqueue(bytes);

            s.Tick();

            Assert.AreEqual(1, s.RejectedPackets, "a hold block hiding a slot was accepted");
            Assert.IsFalse(s.HasInputFrom(2));
        }

        [Test]
        public void FrozenHolds_TheFreezerIsEjected_ConfirmationNeverRunsOutOfTheRing()
        {
            // Arrange: slot 0 of 3. Both remotes send inputs (never more than 4 frames ahead of us); slot 1 reports honest
            // holds, slot 2 keeps reporting it holds nothing.
            var probe1 = new ProbeTransport();
            var probe2 = new ProbeTransport();
            var s = new ChibiRollbackSession(0, Cfg(ChibiMatchConfig.FormatFfa, 3), new IChibiTransport[] { null, probe1, probe2 },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);
            var lost = new System.Collections.Generic.List<int>();
            s.OnPeerLost += (slot, reason) => lost.Add(slot);

            // Act: every tick both remotes send their frame Frame + 2 (contiguous while we advance, a keep-alive while we
            // stall), like honest peers that are never more than the input delay ahead.
            for (int t = 0; t < 2000 && !lost.Contains(2); t++)
            {
                int f = s.Frame + 2;
                probe1.Inbox.Enqueue(Encode(new ChibiNetMessage { Sender = 1, LastFrame = f, AckFrame = -1, Count = 1 }));
                var frozen = new ChibiNetMessage { Sender = 2, LastFrame = f, AckFrame = -1, Count = 1, HoldMask = 0b011 };
                frozen.Hold[0] = 1; frozen.Hold[1] = 1;
                probe2.Inbox.Enqueue(Encode(frozen));
                s.Tick();
                Assert.IsNotNull(s.ConfirmedState, "confirmation fell out of the ring at tick " + t);
                Assert.LessOrEqual(s.Frame - s.ConfirmedFrame, RollbackInputBuffer.RingSize / 2 + 1);
            }

            // Assert: the freezer, and only the freezer, was voted out.
            Assert.Contains(2, lost, "the peer freezing confirmation was never ejected");
            Assert.IsFalse(lost.Contains(1), "the honest peer was blamed");
        }

        [TestCase(2, true)]
        [TestCase(5, false)]
        public void DropAgreement_AllSurvivorsMustReportTheSameDropFrame(int slot2Agrees, bool dropped)
        {
            // Arrange: slot 0 of 4. Slots 1 and 2 vote slot 3 out at frame 1 (it never played); we do too.
            var p = new[] { (ProbeTransport)null, new ProbeTransport(), new ProbeTransport(), new ProbeTransport() };
            var s = new ChibiRollbackSession(0, Cfg(ChibiMatchConfig.FormatFfa, 4), new IChibiTransport[] { null, p[1], p[2], p[3] },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);
            s.NotifyPeerDisconnected(3, ChibiDisconnectReason.RemoteLeft);
            int f = s.Settings.InputDelay;

            // Act: slot 1 agrees on frame 2 (= the correct min + 1); slot 2 reports slot2Agrees.
            for (int t = 0; t < 30 && !s.Disconnected && s.DroppedMask == 0; t++, f++)
            {
                for (int v = 1; v <= 2; v++)
                {
                    var m = new ChibiNetMessage { Sender = v, LastFrame = System.Math.Min(f, s.Frame + 4), AckFrame = -1, Count = 1,
                                                  VoteMask = 1 << 3, AgreeMask = 1 << 3, HoldMask = 0 };
                    m.VoteFrame[3] = 1;
                    m.AgreeFrame[3] = v == 1 ? 2 : slot2Agrees;
                    for (int k = 0; k < 4; k++) if (k != v) { m.HoldMask |= 1 << k; m.Hold[k] = 1; }
                    p[v].Inbox.Enqueue(Encode(m, 4));
                }
                s.Tick();
            }

            // Assert
            if (dropped)
            {
                Assert.AreEqual(1 << 3, s.DroppedMask);
                Assert.IsFalse(s.Disconnected);
            }
            else
            {
                Assert.AreEqual(0, s.DroppedMask, "a drop was applied although the survivors disagreed");
                Assert.AreEqual(ChibiDisconnectReason.VoteFailed, s.DisconnectReason);
                Assert.AreEqual(-1, s.CulpritSlot, "nobody can be blamed for an equivocation");
            }
        }

        [Test]
        public void VoteAgainstUs_IsIgnored_AndAnUnconfirmedVoteEndsAsVoteFailed()
        {
            // Arrange: slot 0 of 3. Slot 2 (a liar) votes slot 0 out and slot 1 out, but slot 1 is healthy for us and never votes.
            var probe1 = new ProbeTransport();
            var probe2 = new ProbeTransport();
            var s = new ChibiRollbackSession(0, Cfg(ChibiMatchConfig.FormatFfa, 3), new IChibiTransport[] { null, probe1, probe2 },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);
            var lie = new ChibiNetMessage { Sender = 2, LastFrame = 2, AckFrame = -1, Count = 1, VoteMask = (1 << 0) | (1 << 1) };
            lie.VoteFrame[0] = 1; lie.VoteFrame[1] = 1;

            // Act: feed the lie and slot 1 keep-alives every tick until the session gives up.
            int frame1 = 2;
            for (int t = 0; t < ChibiRollbackSession.MaxDisconnectTicks + 10 && !s.Disconnected; t++)
            {
                probe2.Inbox.Enqueue(Encode(lie));
                probe1.Inbox.Enqueue(Encode(new ChibiNetMessage { Sender = 1, LastFrame = frame1++, AckFrame = -1, Count = 1 }));
                s.Tick();
            }

            // Assert: nobody was dropped on the liar's word; the match ends as a failed vote (no contest), not a kick.
            Assert.AreEqual(0, s.DroppedMask, "a single peer's vote dropped someone");
            Assert.IsTrue(s.Disconnected);
            Assert.AreEqual(ChibiDisconnectReason.VoteFailed, s.DisconnectReason);
        }

        // ================================================================== drop vote

        [Test]
        public void Leaver_SurvivorsDisagreeOnItsLastFrame_AgreeOnDropFrameAndStayInSync()
        {
            // Arrange: 4-peer FFA; slot 3's link to slot 0 is much slower than to slots 1 and 2, so when it vanishes the
            // survivors hold different last frames of it.
            var m = NewMesh(ChibiMatchConfig.FormatFfa, 4, latency: 2, jitter: 1, loss: 5);
            m.Link(0, 3).LatencyTicks = 9;
            for (int t = 0; t < 400; t++) m.Tick();
            int[] comparedBefore = { m.Peers[0].ChecksumsCompared, m.Peers[1].ChecksumsCompared, m.Peers[2].ChecksumsCompared };

            // Act
            m.Kill(3);
            for (int t = 0; t < LongRun; t++) m.Tick();

            // Assert: same drop frame everywhere, still in sync afterwards, still comparing checksums.
            for (int i = 0; i < 3; i++)
            {
                var p = m.Peers[i];
                Assert.IsFalse(p.Desynced, "survivor " + i + " desynced with slot " + p.DesyncPeer);
                Assert.IsFalse(p.Disconnected, "survivor " + i + " ended: " + p.DisconnectReason);
                Assert.AreEqual(1 << 3, p.DroppedMask, "survivor " + i + " did not drop slot 3");
                Assert.AreEqual(1, m.DropFrames[i].Count);
                Assert.AreEqual(m.DropFrames[0][0], m.DropFrames[i][0], "survivors disagree on the drop frame");
                Assert.Greater(p.ChecksumsCompared, comparedBefore[i] + 20, "checksums stopped after the drop");
                Assert.IsTrue(p.State.MatchOver || p.Frame > 1000, "survivor " + i + " did not play on");
            }
        }

        [Test]
        public void MinorityOfAPartition_EndsWithVoteFailed_MajorityPlaysOn()
        {
            // Arrange: 3-peer FFA.
            var m = NewMesh(ChibiMatchConfig.FormatFfa, 3, latency: 2, jitter: 0, loss: 0);
            for (int t = 0; t < 300; t++) m.Tick();

            // Act: slot 0 loses both links but keeps running.
            m.Link(0, 1).LossPercent = 100;
            m.Link(0, 2).LossPercent = 100;
            for (int t = 0; t < LongRun; t++) m.Tick();

            // Assert
            Assert.IsTrue(m.Peers[0].Disconnected, "the isolated peer played on alone");
            Assert.AreEqual(ChibiDisconnectReason.VoteFailed, m.Peers[0].DisconnectReason);
            Assert.AreEqual(0, m.Peers[0].DroppedMask, "the isolated peer dropped the majority");
            for (int i = 1; i < 3; i++)
            {
                Assert.IsFalse(m.Peers[i].Disconnected, "majority peer " + i + " ended");
                Assert.AreEqual(1 << 0, m.Peers[i].DroppedMask);
                Assert.IsFalse(m.Peers[i].Desynced);
            }
            Assert.AreEqual(m.DropFrames[1][0], m.DropFrames[2][0]);
        }

        // ================================================================== desync attribution

        [Test]
        public void ForgedChecksumFromOnePeer_RaisesDesyncAttributedToThatPeer()
        {
            var probe1 = new ProbeTransport();
            var probe2 = new ProbeTransport();
            var s = new ChibiRollbackSession(0, Cfg(ChibiMatchConfig.FormatFfa, 3), new IChibiTransport[] { null, probe1, probe2 },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);
            int f = s.Settings.InputDelay;
            // Both remotes play neutral; slot 2 also claims a wrong hash for frame 30.
            for (int t = 0; t < 60 && !s.Desynced; t++, f++)
            {
                probe1.Inbox.Enqueue(Encode(new ChibiNetMessage { Sender = 1, LastFrame = f, AckFrame = -1, Count = 1 }));
                probe2.Inbox.Enqueue(Encode(new ChibiNetMessage
                {
                    Sender = 2, LastFrame = f, AckFrame = -1, Count = 1,
                    HasChecksum = true, ChecksumFrame = ChibiRollbackSession.ChecksumInterval, Checksum = 0x12345678u,
                }));
                s.Tick();
            }

            Assert.IsTrue(s.Desynced);
            Assert.AreEqual(2, s.DesyncPeer);
        }

        // ================================================================== review B1/N2, N3 (protocol 6 hardening)

        [Test]
        public void Leaver_GapBetweenSurvivorsSpansAChecksumFrame_ConfirmedStreamsAndReplaysAgree()
        {
            // Arrange: slot 3's link to slot 0 is 12 ticks slower than to slots 1 and 2, and it vanishes at a moment when
            // a checksum frame lies between what slot 0 and slot 1 hold of it. Before the fix, slots 1 and 2 confirmed (and
            // hashed / emitted for replay) frames that the agreed drop then rewrote.
            var m = NewMesh(ChibiMatchConfig.FormatFfa, 4, latency: 2, jitter: 0, loss: 0);
            m.Link(0, 3).LatencyTicks = 14;
            var streams = new List<ushort[]>[3];
            for (int i = 0; i < 3; i++)
            {
                var list = streams[i] = new List<ushort[]>();
                m.Peers[i].OnFrameConfirmedAll += (f, ins) =>
                {
                    Assert.AreEqual(list.Count, f, "confirmed frames must be emitted once, in order");
                    var row = new ushort[4];
                    for (int k = 0; k < 4; k++) row[k] = ins[k].Raw;
                    list.Add(row);
                };
            }
            for (int t = 0; t < 600; t++) m.Tick();
            // Kill slot 3 at a moment when a checksum frame lies between what slot 0 and slot 1 hold of it.
            bool spans = false;
            for (int t = 0; t < 3000 && !spans; t++)
            {
                m.Tick();
                int c0 = m.Peers[0].RemoteContiguousOf(3), c1 = m.Peers[1].RemoteContiguousOf(3);
                spans = (c1 + 1) / ChibiRollbackSession.ChecksumInterval > (c0 + 1) / ChibiRollbackSession.ChecksumInterval + 0
                        && c1 - c0 >= 4;
            }
            Assert.IsTrue(spans, "precondition: survivors' holdings of the leaver straddle a checksum frame");

            // Act
            m.Kill(3);
            for (int t = 0; t < LongRun; t++) m.Tick();

            // Assert: in sync, and every survivor's confirmed input stream (its replay) is the same and replays to the
            // same state.
            int common = int.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                Assert.IsFalse(m.Peers[i].Desynced, "survivor " + i + " desynced with " + m.Peers[i].DesyncPeer);
                Assert.IsFalse(m.Peers[i].Disconnected, "survivor " + i + " ended: " + m.Peers[i].DisconnectReason);
                Assert.AreEqual(1 << 3, m.Peers[i].DroppedMask);
                common = System.Math.Min(common, streams[i].Count);
            }
            Assert.Greater(common, 600, "too little confirmed after the drop");
            uint expected = 0;
            for (int i = 0; i < 3; i++)
            {
                var st = new ChibiSimState();
                ChibiSimulation.Init(st, Cfg(ChibiMatchConfig.FormatFfa, 4));
                var ins = new ChibiInputPacket[4];
                for (int f = 0; f < common; f++)
                {
                    for (int k = 0; k < 4; k++) ins[k] = new ChibiInputPacket { Raw = streams[i][f][k] };
                    ChibiSimulation.Step(st, ins);
                }
                uint h = st.ComputeChecksum();
                if (i == 0) expected = h;
                else Assert.AreEqual(expected, h, "survivor " + i + "'s replay diverges from survivor 0's");
            }
        }

        [Test]
        public void VoteBelowTheFrameTheVoterAlreadyReportedHolding_IsAProtocolViolationBlamedOnTheVoter()
        {
            // Arrange: slot 0 of 3; slot 1 reports holding slot 2 up to frame 40.
            var probe1 = new ProbeTransport();
            var probe2 = new ProbeTransport();
            var s = new ChibiRollbackSession(0, Cfg(ChibiMatchConfig.FormatFfa, 3), new IChibiTransport[] { null, probe1, probe2 },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);
            int f = s.Settings.InputDelay;
            for (int t = 0; t < 10; t++, f++)
            {
                var m1 = new ChibiNetMessage { Sender = 1, LastFrame = f, AckFrame = -1, Count = 1, HoldMask = 1 << 2 };
                m1.Hold[2] = 40;
                probe1.Inbox.Enqueue(Encode(m1));
                probe2.Inbox.Enqueue(Encode(new ChibiNetMessage { Sender = 2, LastFrame = f, AckFrame = -1, Count = 1 }));
                s.Tick();
            }

            // Act: slot 1 now votes slot 2 out at frame 5 (below the 40 it reported): a forged low drop frame.
            var lie = new ChibiNetMessage { Sender = 1, LastFrame = f, AckFrame = -1, Count = 1, VoteMask = 1 << 2 };
            lie.VoteFrame[2] = 5;
            probe1.Inbox.Enqueue(Encode(lie));
            s.Tick();

            // Assert
            Assert.IsTrue(s.Disconnected);
            Assert.AreEqual(ChibiDisconnectReason.ProtocolViolation, s.DisconnectReason);
            Assert.AreEqual(1, s.CulpritSlot);
            Assert.IsFalse(s.Desynced, "an honest peer must not see a desync");
            Assert.AreEqual(0, s.DroppedMask);
        }

        [Test]
        public void LiarRace_ASlotThatCutsAnotherAndVotesItOut_DoesNotGetItDropped()
        {
            // Arrange: 4-peer 2v2 in play.
            var m = NewMesh(ChibiMatchConfig.Format2v2, 4, latency: 2, jitter: 1, loss: 0);
            for (int t = 0; t < 300; t++) m.Tick();

            // Act: slot 1 (the liar) claims slot 2 left: it votes 2 out and stops talking to it. Slot 2 keeps playing.
            m.Peers[1].NotifyPeerDisconnected(2, ChibiDisconnectReason.RemoteLeft);
            for (int t = 0; t < LongRun; t++) m.Tick();

            // Assert: nobody drops slot 2 on the liar's word; the honest peers end as a no contest instead.
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(0, m.Peers[i].DroppedMask & (1 << 2), "peer " + i + " dropped the accused slot");
            Assert.IsTrue(m.Peers[0].Disconnected && m.Peers[3].Disconnected, "the honest bystanders kept a split match running");
            Assert.AreEqual(ChibiDisconnectReason.VoteFailed, m.Peers[0].DisconnectReason);
            Assert.AreEqual(-1, m.Peers[0].CulpritSlot, "a mutual accusation must not frame either side");
        }

        [Test]
        public void NoTimeoutCountsAgainstARemoteBeforeItsFirstDatagram_WithinTheGrace()
        {
            // Arrange: slot 0 of 3; slot 2 talks, slot 1 is silent (slow to finish its PeerHello).
            var probe1 = new ProbeTransport();
            var probe2 = new ProbeTransport();
            var s = new ChibiRollbackSession(0, Cfg(ChibiMatchConfig.FormatFfa, 3), new IChibiTransport[] { null, probe1, probe2 },
                                             new ChibiScriptedInputSource(n => default), ChibiRollbackSettings.Default);

            // Act: just past DisconnectTicks of nothing from slot 1.
            for (int t = 0; t < s.Settings.DisconnectTicks + 20; t++)
            {
                probe2.Inbox.Enqueue(Encode(new ChibiNetMessage { Sender = 2, LastFrame = s.Settings.InputDelay, AckFrame = -1, Count = 1 }));
                s.Tick();
            }

            // Assert
            Assert.IsFalse(s.IsPeerLost(1), "a peer that has not sent its first datagram yet was timed out");
            Assert.IsFalse(s.VotePending);
        }

        // ================================================================== codec

        [Test]
        public void Codec_SenderAndVotes_RoundTrip()
        {
            var m = new ChibiNetMessage { Sender = 3, LastFrame = 500, AckFrame = 480, Advantage = -2, Count = 1, VoteMask = (1 << 0) | (1 << 2) };
            m.VoteFrame[0] = 400; m.VoteFrame[2] = -1;
            var bytes = Encode(m);
            var b = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            b.LoadFrom(bytes, bytes.Length);
            var r = new ChibiNetMessage();

            Assert.IsTrue(ChibiPacketCodec.TryRead(b, r));
            Assert.AreEqual(3, r.Sender);
            Assert.AreEqual(m.VoteMask, r.VoteMask);
            Assert.AreEqual(400, r.VoteFrame[0]);
            Assert.AreEqual(-1, r.VoteFrame[2]);
            Assert.AreEqual(-1, r.VoteFrame[1]);
        }

        [Test]
        public void Codec_RandomBytes_NeverThrow()
        {
            var rng = new ChibiRng(99);
            var raw = new byte[ChibiPacketCodec.MaxPacketBytes];
            var b = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            var r = new ChibiNetMessage();
            for (int i = 0; i < 20000; i++)
            {
                int n = 1 + rng.Range(raw.Length);
                for (int k = 0; k < n; k++) raw[k] = (byte)rng.Range(256);
                raw[0] = ChibiPacketCodec.Magic;
                b.LoadFrom(raw, n);
                Assert.DoesNotThrow(() => ChibiPacketCodec.TryRead(b, r));
                Assert.That(r.Sender, Is.InRange(0, 3));
                Assert.That(r.VoteMask, Is.InRange(0, 15));
            }
        }
    }
}
