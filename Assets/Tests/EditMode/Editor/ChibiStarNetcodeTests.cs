using System;
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Phase 3 (four-player-design.md 2, 3): 2-4 peers over the star (host forwards inputs), protocol 6 codec, leavers at an
    /// exact frame, roster / Start validation. In-memory links (ChibiLoopbackLink) with latency, jitter (reordering) and loss.
    /// </summary>
    public class ChibiStarNetcodeTests
    {
        // ------------------------------------------------------------------ fixtures

        static ChibiMatchConfig Cfg(int players, ChibiMatchMode mode = ChibiMatchMode.FreeForAll, int teamOf = 0) => new ChibiMatchConfig
        {
            Seed = 0xC0FFEE, Stage = 3, PlayerCount = players, Mode = mode, TeamOf = teamOf,
            Class0 = FighterClass.Melee, Class1 = FighterClass.Ranged, Class2 = FighterClass.Heavy, Class3 = FighterClass.Magic,
            Passive1 = 1, Passive3 = 1,
        };

        /// <summary>Deterministic pseudo-random player: holds a direction for a while and presses buttons.</summary>
        static ChibiInputPacket Bot(int seed, int frame)
        {
            uint h = (uint)(frame / 7) * 2654435761u + (uint)seed * 40503u;
            h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
            int x = (int)(h % 3) - 1;
            int y = (h >> 3) % 8 == 0 ? -1 : ((h >> 3) % 8 == 1 ? 1 : 0);
            uint g = (uint)frame * 2246822519u + (uint)seed * 3266489917u;
            g ^= g >> 15; g *= 2654435761u; g ^= g >> 13;
            ChibiButtons b = ChibiButtons.None;
            if (g % 9 == 0) b |= ChibiButtons.Light;
            if (g % 23 == 1) b |= ChibiButtons.Heavy;
            if (g % 31 == 2) b |= ChibiButtons.Jump;
            if (g % 41 == 3) b |= ChibiButtons.Skill1;
            if (g % 61 == 6) b |= ChibiButtons.Shield;
            return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, y), b);
        }

        /// <summary>A star of N sessions: one loopback link per guest (each with its own latency / jitter / loss).</summary>
        sealed class Star
        {
            public readonly ChibiRollbackSession[] Peers;
            public readonly ChibiLoopbackLink[] Links;
            public readonly List<ushort[]>[] Confirmed;
            public readonly bool[] Dead;
            public string Desync;

            public Star(int players, int latency, int jitter, int loss, ChibiRollbackSettings settings, ChibiMatchConfig? cfgOverride = null)
            {
                var cfg = cfgOverride ?? Cfg(players);
                Peers = new ChibiRollbackSession[players];
                Links = new ChibiLoopbackLink[players];
                Confirmed = new List<ushort[]>[players];
                Dead = new bool[players];
                var hostLinks = new IChibiTransport[ChibiSimState.MaxFighters];
                for (int g = 1; g < players; g++)
                {
                    Links[g] = new ChibiLoopbackLink(latency + g - 1, jitter, loss);   // different routes per guest
                    hostLinks[g] = Links[g].A;
                }
                for (int p = 0; p < players; p++)
                {
                    int seed = 11 * (p + 1);
                    var src = new ChibiScriptedInputSource(n => Bot(seed, n));
                    Peers[p] = p == 0 ? new ChibiRollbackSession(0, cfg, hostLinks, src, settings)
                                      : new ChibiRollbackSession(p, cfg, Links[p].B, src, settings);
                    var list = Confirmed[p] = new List<ushort[]>();
                    int me = p;
                    Peers[p].OnFrameConfirmedAll += (f, inputs) =>
                    {
                        Assert.AreEqual(list.Count, f, "confirmed frames arrive in order, once");
                        var copy = new ushort[players];
                        for (int i = 0; i < players; i++) copy[i] = inputs[i].Raw;
                        list.Add(copy);
                    };
                    Peers[p].OnDesync += (f, l, r) => Desync = "peer " + me + " desync at frame " + f;
                }
            }

            public void Run(int ticks)
            {
                for (int t = 0; t < ticks; t++)
                {
                    for (int g = 1; g < Links.Length; g++) Links[g].Advance();
                    for (int p = 0; p < Peers.Length; p++) if (!Dead[p]) Peers[p].Tick();
                }
            }

            public void Heal()
            {
                for (int g = 1; g < Links.Length; g++) { Links[g].LossPercent = 0; Links[g].JitterTicks = 0; }
            }
        }

        /// <summary>Re-simulate confirmed inputs with the leave frames: the state every honest peer must have.</summary>
        static uint Resimulate(ChibiMatchConfig cfg, List<ushort[]> inputs, int frames, ChibiRollbackSession leaves)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            int n = cfg.Players;
            var step = new ChibiInputPacket[n];
            for (int f = 0; f < frames; f++)
            {
                int mask = 0;
                for (int p = 0; p < n; p++)
                {
                    step[p] = new ChibiInputPacket { Raw = inputs[f][p] };
                    if (p == leaves.LocalIndex || f < leaves.LeftAt(p)) mask |= 1 << p;
                }
                if (n == 2 && mask == 3) ChibiSimulation.Step(s, step[0], step[1]);
                else ChibiSimulation.StepPresent(s, step, mask);
            }
            return s.ComputeChecksum();
        }

        static void AssertAllAgree(Star star, ChibiMatchConfig cfg)
        {
            Assert.IsNull(star.Desync, star.Desync);
            int common = int.MaxValue;
            for (int p = 0; p < star.Peers.Length; p++) if (!star.Dead[p]) common = Math.Min(common, star.Confirmed[p].Count);
            Assert.Greater(common, 600, "the match must progress under loss and reordering");
            uint expected = 0;
            int first = -1;
            for (int p = 0; p < star.Peers.Length; p++)
            {
                if (star.Dead[p]) continue;
                Assert.Greater(star.Peers[p].ChecksumsCompared, 0, "peer " + p + " compared checksums");
                uint h = Resimulate(cfg, star.Confirmed[p], common, star.Peers[p]);
                if (first < 0) { first = p; expected = h; }
                else Assert.AreEqual(expected, h, "peer " + p + " confirmed a different match than peer " + first);
                for (int f = 0; f < common; f++)
                    CollectionAssert.AreEqual(star.Confirmed[first][f], star.Confirmed[p][f], "inputs of frame " + f);
            }
        }

        // ------------------------------------------------------------------ 3 and 4 peers, lossy links

        [TestCase(3, 2, 3, 10)]
        [TestCase(4, 2, 3, 10)]
        [TestCase(4, 5, 4, 20)]
        [TestCase(2, 3, 3, 15)]
        public void Star_LossyReorderingLinks_EveryPeerConfirmsTheSameMatch(int players, int latency, int jitter, int loss)
        {
            var settings = ChibiOnlineMatch.SettingsFor(players);
            var star = new Star(players, latency, jitter, loss, settings);
            star.Run(1500);
            star.Heal();
            star.Run(120);
            AssertAllAgree(star, Cfg(players));
            for (int p = 0; p < players; p++) Assert.IsFalse(star.Peers[p].Disconnected, "peer " + p);
        }

        [Test]
        public void Star_BurstLossOnOneGuest_HealsWithoutDesync()
        {
            var star = new Star(4, 2, 2, 5, ChibiOnlineMatch.SettingsFor(4));
            star.Run(400);
            star.Links[2].DropAllFor(30);   // 500 ms outage on one spoke only
            star.Run(800);
            star.Heal();
            star.Run(120);
            AssertAllAgree(star, Cfg(4));
        }

        // ------------------------------------------------------------------ leavers

        [Test]
        public void Leave_DeclaredByHost_AppliesAtTheSameFrameOnEveryPeer()
        {
            var star = new Star(4, 3, 2, 10, ChibiOnlineMatch.SettingsFor(4));
            int reported = -1, reportedSlot = -1;
            star.Peers[0].OnPlayerLeft += (s, f) => { reportedSlot = s; reported = f; };
            star.Run(500);
            star.Dead[2] = true;                          // player 2 closes the game
            int f = star.Peers[0].DeclareLeft(2);         // the host's control channel saw its Leave(Forfeit)
            Assert.Greater(f, 0);
            Assert.AreEqual(2, reportedSlot);
            Assert.AreEqual(f, reported);
            star.Run(900);
            star.Heal();
            star.Run(120);
            for (int p = 0; p < 4; p++)
            {
                if (p == 2) continue;
                Assert.AreEqual(f, star.Peers[p].LeftAt(2), "peer " + p + " applies the leave at the host's frame");
                Assert.AreEqual(0, star.Peers[p].State.PresentMask & (1 << 2), "fighter 2 is out on peer " + p);
            }
            AssertAllAgree(star, Cfg(4));
        }

        [Test]
        public void Leave_SilentGuest_TimesOutAndTheOthersPlayOn()
        {
            var settings = ChibiOnlineMatch.SettingsFor(3);
            settings.DisconnectTicks = 90;
            var star = new Star(3, 2, 1, 5, settings);
            star.Run(300);
            star.Dead[1] = true;                          // power cut: no goodbye
            star.Run(1000);
            star.Heal();
            star.Run(120);
            Assert.AreNotEqual(RollbackInputBuffer.Never, star.Peers[0].LeftAt(1), "the host declared the silent guest gone");
            Assert.AreEqual(star.Peers[0].LeftAt(1), star.Peers[2].LeftAt(1));
            Assert.IsFalse(star.Peers[2].Disconnected, "the other guest is not dropped");
            Assert.Greater(star.Peers[2].Frame, 900, "the match goes on after the leave");
            AssertAllAgree(star, Cfg(3));
        }

        [TestCase(8, 0, 90)]
        [TestCase(8, 2, 300)]
        [TestCase(10, 2, 60)]
        [TestCase(12, 3, 300)]
        public void Leave_SilentGuestAtHighRtt_OnlyTheSilentGuestLeaves(int latency, int jitter, int disconnect)
        {
            // H1 (review 2026-11-24): at about 250 ms RTT the innocent guests stall right after the silent one stops, so a
            // silence count per guest dropped them too (cascade). Only the guest holding everyone back may time out.
            var settings = ChibiOnlineMatch.SettingsFor(4);
            settings.DisconnectTicks = disconnect;
            var star = new Star(4, latency, jitter, 0, settings);
            star.Run(400);

            star.Dead[1] = true;
            star.Run(disconnect + 600);

            Assert.AreNotEqual(RollbackInputBuffer.Never, star.Peers[0].LeftAt(1), "the silent guest is declared gone");
            Assert.AreEqual(RollbackInputBuffer.Never, star.Peers[0].LeftAt(2), "guest 2 only waited for guest 1");
            Assert.AreEqual(RollbackInputBuffer.Never, star.Peers[0].LeftAt(3), "guest 3 only waited for guest 1");
            Assert.IsFalse(star.Peers[2].LocallyDropped);
            Assert.IsFalse(star.Peers[3].LocallyDropped);
        }

        [Test]
        public void Checksum_OfALeftPlayerAtOrAfterItsLeaveFrame_IsIgnored()
        {
            // M1: a dropped guest keeps simulating without its own leave, so its checksums from the leave frame on differ.
            var net = new Probe();
            var settings = ChibiOnlineMatch.SettingsFor(3);
            var guest = new ChibiRollbackSession(1, Cfg(3), net, new ChibiScriptedInputSource(n => default), settings);
            const int leave = 10;
            for (int t = 0; t < 90; t++)
            {
                var m = new ChibiNetMessage { AckFrame = -1, BlockCount = 2, LeaveMask = 1 << 2 };
                m.LeaveFrame[2] = leave;
                int last = t + settings.InputDelay;
                m.Blocks[0].Slot = 0; m.Blocks[0].LastFrame = last; m.Blocks[0].Count = Math.Min(16, last + 1);
                var b = m.Blocks[1];
                b.Slot = 2; b.LastFrame = leave - 1; b.Count = leave;
                b.HasChecksum = true; b.ChecksumFrame = ChibiRollbackSession.ChecksumInterval; b.Checksum = 0xBADBADu;
                net.Inbox.Enqueue(Encode(m));
                guest.Tick();
            }
            Assert.Greater(guest.Frame, ChibiRollbackSession.ChecksumInterval * 2, "precondition: the guest confirmed past the checksum frame");
            Assert.IsFalse(guest.Desynced, "a left player's checksum after its leave frame is not compared");
        }

        [Test]
        public void Leave_GuestSeesItsOwnSlotGone_IsDropped()
        {
            var star = new Star(3, 2, 0, 0, ChibiOnlineMatch.SettingsFor(3));
            star.Run(200);
            star.Peers[0].DeclareLeft(2);   // the host thinks guest 2 is gone, but it is still listening
            star.Run(30);
            Assert.IsTrue(star.Peers[2].LocallyDropped);
            Assert.IsTrue(star.Peers[2].Disconnected);
            Assert.IsFalse(star.Peers[1].LocallyDropped);
        }

        [Test]
        public void DeclareLeft_OnlyTheHostOfAGroupMatch()
        {
            var two = new Star(2, 1, 0, 0, ChibiRollbackSettings.Default);
            Assert.AreEqual(-1, two.Peers[0].DeclareLeft(1), "a 1v1 keeps its own rules (opponent leaves = win)");
            var three = new Star(3, 1, 0, 0, ChibiOnlineMatch.SettingsFor(3));
            Assert.AreEqual(-1, three.Peers[1].DeclareLeft(2), "a guest cannot declare a leave");
            Assert.AreEqual(-1, three.Peers[0].DeclareLeft(0), "the host cannot leave itself");
            Assert.AreEqual(-1, three.Peers[0].DeclareLeft(7));
        }

        // ------------------------------------------------------------------ untrusted input on the host

        sealed class Probe : IChibiTransport
        {
            public readonly Queue<byte[]> Inbox = new Queue<byte[]>();
            public int Sent;
            public void Send(byte[] data, int length) => Sent++;
            public int Receive(byte[] buffer)
            {
                if (Inbox.Count == 0) return 0;
                var d = Inbox.Dequeue();
                Buffer.BlockCopy(d, 0, buffer, 0, d.Length);
                return d.Length;
            }
        }

        static byte[] Encode(ChibiNetMessage m)
        {
            var b = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(b, m);
            var bytes = new byte[b.ByteLength];
            Buffer.BlockCopy(b.Bytes, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        [Test]
        public void Host_RejectsAGuestSpeakingForAnotherSlot()
        {
            var p1 = new Probe();
            var p2 = new Probe();
            var host = new ChibiRollbackSession(0, Cfg(3), new IChibiTransport[] { null, p1, p2, null },
                                                new ChibiScriptedInputSource(n => default), ChibiOnlineMatch.SettingsFor(3));
            p1.Inbox.Enqueue(Encode(new ChibiNetMessage { Slot = 2, LastFrame = 5, Count = 1, AckFrame = -1 }));   // forged seat
            var leave = new ChibiNetMessage { Slot = 1, LastFrame = 5, Count = 1, AckFrame = -1, LeaveMask = 4 };
            leave.LeaveFrame[2] = 10;
            p1.Inbox.Enqueue(Encode(leave));                                                                         // guests never announce leaves
            var ack = new ChibiNetMessage { Slot = 1, LastFrame = 5, Count = 1, AckFrame = -1, ExtraAckMask = 4 };
            ack.ExtraAck[2] = 500;
            p1.Inbox.Enqueue(Encode(ack));                                                                           // acks a frame never forwarded
            host.Tick();
            Assert.AreEqual(3, host.RejectedPackets);
            Assert.AreEqual(RollbackInputBuffer.Never, host.LeftAt(2), "a guest's datagram can never make the host drop a player");
        }

        [Test]
        public void Guest_RejectsABlockForItsOwnSlotAndALeaveForFramesItHolds()
        {
            var net = new Probe();
            var guest = new ChibiRollbackSession(1, Cfg(3), net, new ChibiScriptedInputSource(n => default), ChibiOnlineMatch.SettingsFor(3));
            net.Inbox.Enqueue(Encode(new ChibiNetMessage { Slot = 1, LastFrame = 5, Count = 1, AckFrame = -1 }));
            var m = new ChibiNetMessage { Slot = 0, LastFrame = 8, Count = 6, AckFrame = -1, BlockCount = 2 };
            m.Blocks[1].Slot = 2; m.Blocks[1].LastFrame = 8; m.Blocks[1].Count = 6;
            net.Inbox.Enqueue(Encode(m));                                                       // honest: frames 3..8 of players 0 and 2
            var late = new ChibiNetMessage { Slot = 0, LastFrame = 8, Count = 1, AckFrame = -1, LeaveMask = 4 };
            late.LeaveFrame[2] = 5;                                                             // player 2's frame 5 is already actual here
            net.Inbox.Enqueue(Encode(late));
            guest.Tick();
            Assert.AreEqual(2, guest.RejectedPackets);
            Assert.AreEqual(RollbackInputBuffer.Never, guest.LeftAt(2));
        }

        // ------------------------------------------------------------------ protocol 6 codec

        [Test]
        public void Codec_FourBlocksAcksAndLeaves_RoundTrip()
        {
            var m = new ChibiNetMessage { AckFrame = 77, Advantage = -3, BlockCount = 4, ExtraAckMask = 0b1100, LeaveMask = 0b1010 };
            for (int k = 0; k < 4; k++)
            {
                var b = m.Blocks[k];
                b.Slot = 3 - k; b.LastFrame = 1000 + k; b.Count = ChibiNetMessage.MaxInputs;
                b.HasChecksum = k % 2 == 0; b.ChecksumFrame = 990; b.Checksum = 0xDEADBEEFu + (uint)k;
                for (int i = 0; i < b.Count; i++) b.Inputs[i] = new ChibiInputPacket { Raw = (ushort)(0xFFF0 | ((i + k) % 9)) };
            }
            m.ExtraAck[2] = 500; m.ExtraAck[3] = -1;
            m.LeaveFrame[1] = 1234; m.LeaveFrame[3] = 99;
            var buf = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(buf, m);
            Assert.IsFalse(buf.Overrun);
            Assert.LessOrEqual(buf.ByteLength, ChibiPacketCodec.MaxPacketBytes);
            var rx = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            rx.LoadFrom(buf.Bytes, buf.ByteLength);
            var o = new ChibiNetMessage();
            Assert.IsTrue(ChibiPacketCodec.TryRead(rx, o));
            Assert.AreEqual(4, o.BlockCount);
            Assert.AreEqual(77, o.AckFrame);
            Assert.AreEqual(-3, o.Advantage);
            Assert.AreEqual(0b1100, o.ExtraAckMask);
            Assert.AreEqual(500, o.ExtraAck[2]);
            Assert.AreEqual(-1, o.ExtraAck[3]);
            Assert.AreEqual(0b1010, o.LeaveMask);
            Assert.AreEqual(1234, o.LeaveFrame[1]);
            Assert.AreEqual(99, o.LeaveFrame[3]);
            for (int k = 0; k < 4; k++)
            {
                Assert.AreEqual(m.Blocks[k].Slot, o.Blocks[k].Slot);
                Assert.AreEqual(m.Blocks[k].LastFrame, o.Blocks[k].LastFrame);
                Assert.AreEqual(m.Blocks[k].HasChecksum, o.Blocks[k].HasChecksum);
                if (m.Blocks[k].HasChecksum) Assert.AreEqual(m.Blocks[k].Checksum, o.Blocks[k].Checksum);
                for (int i = 0; i < m.Blocks[k].Count; i++) Assert.AreEqual(m.Blocks[k].Inputs[i], o.Blocks[k].Inputs[i]);
            }
        }

        [Test]
        public void Codec_TwoBlocksForOneSlot_Rejected()
        {
            var m = new ChibiNetMessage { BlockCount = 2, Slot = 1, LastFrame = 3, Count = 1 };
            m.Blocks[1].Slot = 1; m.Blocks[1].LastFrame = 3; m.Blocks[1].Count = 1;
            var buf = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(buf, m);
            var rx = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            rx.LoadFrom(buf.Bytes, buf.ByteLength);
            Assert.IsFalse(ChibiPacketCodec.TryRead(rx, new ChibiNetMessage()));
        }

        [Test]
        public void Codec_Fuzz_RandomAndTruncatedDatagrams_NeverThrow()
        {
            var rng = new ChibiRng(4242);
            var data = new byte[ChibiChannelMux.MaxDatagram];
            var rx = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            var ctl = new BitBuffer(ChibiControlCodec.MaxBytes);
            var m = new ChibiNetMessage();
            var c = new ChibiControlMessage();
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 20000; i++)
                {
                    int n = 1 + rng.Range(ChibiChannelMux.MaxDatagram);
                    for (int k = 0; k < n; k++) data[k] = (byte)rng.Range(256);
                    data[0] = i % 2 == 0 ? ChibiPacketCodec.Magic : ChibiControlCodec.Magic;
                    rx.LoadFrom(data, n);
                    if (ChibiPacketCodec.TryRead(rx, m))
                    {
                        Assert.That(m.BlockCount, Is.InRange(1, ChibiNetMessage.MaxBlocks));
                        for (int k = 0; k < m.BlockCount; k++)
                        {
                            Assert.That(m.Blocks[k].Count, Is.InRange(1, ChibiNetMessage.MaxInputs));
                            Assert.That(m.Blocks[k].Slot, Is.InRange(0, 3));
                        }
                    }
                    ctl.LoadFrom(data, n);
                    if (ChibiControlCodec.TryRead(ctl, c) && c.Type == ChibiControlType.Roster)
                        for (int k = 0; k < c.RosterCount; k++) Assert.That(c.RosterLength[k], Is.InRange(0, ChibiControlCodec.NameMaxChars));
                }
            });
        }

        [Test]
        public void Codec_V6_Session_ProbeSeesItsSlotAndAckPerStream()
        {
            byte[] sent = null;
            var cap = new CaptureTransport(d => sent = d);
            var guest = new ChibiRollbackSession(2, Cfg(4), cap, new ChibiScriptedInputSource(n => default), ChibiOnlineMatch.SettingsFor(4));
            guest.Tick();
            var rx = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            rx.LoadFrom(sent, sent.Length);
            var m = new ChibiNetMessage();
            Assert.IsTrue(ChibiPacketCodec.TryRead(rx, m));
            Assert.AreEqual(1, m.BlockCount);
            Assert.AreEqual(2, m.Slot, "a guest's block carries its own slot");
            Assert.AreEqual(0b1010, m.ExtraAckMask, "acks for the forwarded streams 1 and 3");
            Assert.AreEqual(0, m.LeaveMask);
        }

        sealed class CaptureTransport : IChibiTransport
        {
            readonly Action<byte[]> _onSend;
            public CaptureTransport(Action<byte[]> onSend) { _onSend = onSend; }
            public void Send(byte[] data, int length) { var c = new byte[length]; Buffer.BlockCopy(data, 0, c, 0, length); _onSend(c); }
            public int Receive(byte[] buffer) => 0;
        }

        // ------------------------------------------------------------------ handshake, Start and Roster validation

        static ChibiControlMessage RoundTripControl(ChibiControlMessage m)
        {
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            ChibiControlCodec.Write(b, m);
            var r = new BitBuffer(ChibiControlCodec.MaxBytes);
            r.LoadFrom(b.Bytes, b.ByteLength);
            var o = new ChibiControlMessage();
            Assert.IsTrue(ChibiControlCodec.TryRead(r, o));
            return o;
        }

        [Test]
        public void Roster_FourLongestNames_FitAndRoundTrip()
        {
            var m = new ChibiControlMessage { Type = ChibiControlType.Roster, MatchHash = 0xABCDEF01u, RosterCount = 4 };
            m.RosterOut[0] = "ABCDEFGHIJKLMNOP#1234567";
            m.RosterOut[1] = "Kira#4821";
            m.RosterOut[2] = "<b>bad</b> name";
            m.RosterOut[3] = "";
            var o = RoundTripControl(m);
            Assert.AreEqual(4, o.RosterCount);
            Assert.AreEqual(0xABCDEF01u, o.MatchHash);
            Assert.AreEqual("ABCDEFGHIJKLMNOP#1234567", o.RosterName(0));
            Assert.AreEqual("Kira#4821", o.RosterName(1));
            Assert.AreEqual("bbadbname", o.RosterName(2), "only alphabet characters travel");
            Assert.AreEqual("", o.RosterName(3));
        }

        [Test]
        public void Start_V6_CarriesSeatsModeTeamsAndYourSlot()
        {
            var cfg = Cfg(4, ChibiMatchMode.Team, 0b1010);
            var o = RoundTripControl(new ChibiControlMessage
            {
                Type = ChibiControlType.Start, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = ChibiMatchConfig.RulesVersion,
                Config = cfg, Settings = ChibiOnlineMatch.SettingsFor(4), Slot = 3, MatchHash = 7, Name = "Host#1",
            });
            Assert.AreEqual(4, o.Config.PlayerCount);
            Assert.AreEqual(ChibiMatchMode.Team, o.Config.Mode);
            Assert.AreEqual(0b1010, o.Config.TeamOf);
            Assert.AreEqual(3, o.Slot);
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(cfg.ClassOf(i), o.Config.ClassOf(i));
                Assert.AreEqual(cfg.PassiveOf(i), o.Config.PassiveOf(i));
            }
            Assert.AreEqual(cfg.ComputeHash(), o.Config.ComputeHash());
        }

        /// <summary>Start sent to a joiner that plays Ranged / passive 1 in some seat.</summary>
        static ChibiOnlineMatch StartToJoiner(ChibiMatchConfig cfg, int slot, int expectPlayers = -1)
        {
            var link = new ChibiLoopbackLink(1, 0, 0);
            var joiner = ChibiOnlineMatch.Join(link.B, new ChibiScriptedInputSource(n => default), FighterClass.Ranged, 1,
                                               expectPlayers: expectPlayers);
            var host = new ChibiChannelMux(link.A);
            var settings = ChibiOnlineMatch.SettingsFor(cfg.Players);
            var m = new ChibiControlMessage
            {
                Type = ChibiControlType.Start, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = ChibiMatchConfig.RulesVersion,
                Config = cfg, Settings = settings, Slot = slot, MatchHash = ChibiControlCodec.MatchHash(cfg, settings),
            };
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            ChibiControlCodec.Write(b, m);
            host.Control.Send(b.Bytes, b.ByteLength);
            for (int i = 0; i < 10; i++) { link.Advance(); joiner.Tick(); }
            return joiner;
        }

        [Test]
        public void Joiner_AcceptsItsOwnSeat()
        {
            var cfg = Cfg(4);
            cfg.Intro = true;   // online matches always play the intro: the joiner hashes with it on
            var joiner = StartToJoiner(cfg, 1);
            Assert.AreEqual(ChibiOnlinePhase.Playing, joiner.Phase);
            Assert.AreEqual(1, joiner.LocalIndex);
            Assert.AreEqual(4, joiner.Session.Players);
        }

        [TestCase(0, -1, Description = "slot 0 is the host's")]
        [TestCase(2, -1, Description = "seat 2 holds another fighter (Heavy)")]
        [TestCase(1, 3, Description = "the room had 3 seats, the Start says 4")]
        public void Joiner_RejectsAStartThatDoesNotSeatIt(int slot, int expectPlayers)
        {
            var cfg = Cfg(4);
            cfg.Intro = true;
            var joiner = StartToJoiner(cfg, slot, expectPlayers);
            Assert.AreEqual(ChibiMatchOutcome.Aborted, joiner.Result.Outcome);
            Assert.AreEqual(ChibiRejectReason.InvalidConfig, joiner.Result.RejectReason);
        }

        [Test]
        public void Joiner_RejectsTeamBitsInFreeForAllAndBadSeatClasses()
        {
            var cfg = Cfg(3);
            cfg.Intro = true;
            cfg.TeamOf = 0b10;   // FFA with a team map
            Assert.AreEqual(ChibiRejectReason.InvalidConfig, StartToJoiner(cfg, 1).Result.RejectReason);
            cfg = Cfg(3);
            cfg.Intro = true;
            cfg.Class2 = (FighterClass)12;
            Assert.AreEqual(ChibiRejectReason.InvalidConfig, StartToJoiner(cfg, 1).Result.RejectReason);
        }

        // ------------------------------------------------------------------ full online match, 3 and 4 players

        sealed class GroupMatch
        {
            public readonly ChibiOnlineMatch Host;
            public readonly ChibiOnlineMatch[] Guests;
            public readonly ChibiLoopbackLink[] Links;

            public GroupMatch(int players, int latency, int jitter, int loss, string[] names, string[] seatKeys = null, ulong[] secrets = null)
            {
                Links = new ChibiLoopbackLink[players];
                var hostEnds = new IChibiTransport[ChibiSimState.MaxFighters];
                for (int g = 1; g < players; g++) { Links[g] = new ChibiLoopbackLink(latency, jitter, loss); hostEnds[g] = Links[g].A; }
                Host = ChibiOnlineMatch.HostGroup(hostEnds, players, new ChibiScriptedInputSource(n => Bot(1, n)), FighterClass.Melee, 0,
                                                  stage: 3, seed: 0xBEEFu, ChibiOnlineMatch.SettingsFor(players), localName: names[0],
                                                  seatKeys: seatKeys);
                Guests = new ChibiOnlineMatch[players];
                var classes = new[] { FighterClass.Melee, FighterClass.Ranged, FighterClass.Heavy, FighterClass.Magic };
                for (int g = 1; g < players; g++)
                {
                    int seed = g + 1;
                    Guests[g] = ChibiOnlineMatch.Join(Links[g].B, new ChibiScriptedInputSource(n => Bot(seed, n)), classes[g], g % 2,
                                                      localName: names[g], expectPlayers: players,
                                                      secret: secrets != null ? secrets[g] : 0UL);
                }
            }

            public void Run(int ticks)
            {
                for (int t = 0; t < ticks; t++)
                {
                    for (int g = 1; g < Links.Length; g++) Links[g].Advance();
                    Host.Tick();
                    for (int g = 1; g < Guests.Length; g++) Guests[g].Tick();
                }
            }
        }

        [Test]
        public void GroupMatch_FourPlayers_AgreeSeatsNamesAndPlayInSync()
        {
            var names = new[] { "Kira#4821", "Pip_99#17", "Zed#1", "Mo#22" };
            var gm = new GroupMatch(4, 2, 2, 10, names);
            gm.Run(900);
            Assert.AreEqual(ChibiOnlinePhase.Playing, gm.Host.Phase, gm.Host.Result.ToString());
            Assert.AreEqual(4, gm.Host.Session.Players);
            for (int g = 1; g < 4; g++)
            {
                Assert.AreEqual(ChibiOnlinePhase.Playing, gm.Guests[g].Phase, "guest " + g + ": " + gm.Guests[g].Result);
                Assert.AreEqual(g, gm.Guests[g].LocalIndex);
                Assert.AreEqual(gm.Host.Group.MatchHash, gm.Guests[g].Handshake.MatchHash);
                for (int s = 0; s < 4; s++) Assert.AreEqual(names[s], gm.Guests[g].PlayerName(s), "guest " + g + " sees seat " + s);
                Assert.IsFalse(gm.Guests[g].Session.Desynced);
                Assert.Greater(gm.Guests[g].Session.ChecksumsCompared, 0);
            }
            for (int s = 0; s < 4; s++) Assert.AreEqual(names[s], gm.Host.PlayerName(s));
            Assert.IsNull(gm.Host.Replay, "the 2-stream replay file is not recorded for 4 players");
        }

        [Test]
        public void GroupMatch_GuestForfeits_IsEliminatedAndTheOthersPlayOn()
        {
            var gm = new GroupMatch(3, 2, 1, 5, new[] { "A", "B", "C" });
            int leftSlot = -1;
            gm.Host.OnPlayerLeft += (s, f) => leftSlot = s;
            gm.Run(400);
            gm.Guests[2].Forfeit();
            Assert.AreEqual(ChibiMatchOutcome.Loss, gm.Guests[2].Result.Outcome);
            gm.Run(300);
            Assert.AreEqual(2, leftSlot);
            Assert.AreEqual(ChibiOnlinePhase.Playing, gm.Host.Phase);
            Assert.AreEqual(ChibiOnlinePhase.Playing, gm.Guests[1].Phase);
            Assert.AreEqual(gm.Host.Session.LeftAt(2), gm.Guests[1].Session.LeftAt(2));
        }

        [Test]
        public void GroupMatch_GuestSaysMatchComplete_IsNotDropped()
        {
            // M2: Leave(MatchComplete) is a claim that decides nothing; the host keeps that guest in the match.
            var gm = new GroupMatch(3, 2, 0, 0, new[] { "A", "B", "C" });
            gm.Run(400);
            gm.Guests[2].Handshake.SendLeave(ChibiLeaveReason.MatchComplete);
            gm.Run(60);
            Assert.AreEqual(RollbackInputBuffer.Never, gm.Host.Session.LeftAt(2));
            Assert.AreEqual(ChibiOnlinePhase.Playing, gm.Host.Phase);
        }

        [Test]
        public void GroupMatch_GuestClaimsDesync_OnlyThatGuestIsDropped()
        {
            // M3 / SEC-002: the host cannot check a guest's desync claim, so it costs that guest the match, nobody else.
            var gm = new GroupMatch(3, 2, 0, 0, new[] { "A", "B", "C" });
            gm.Run(400);
            gm.Guests[2].Handshake.SendLeave(ChibiLeaveReason.Desync);
            gm.Run(120);
            Assert.AreNotEqual(RollbackInputBuffer.Never, gm.Host.Session.LeftAt(2), "the claiming guest is out");
            Assert.AreEqual(ChibiOnlinePhase.Playing, gm.Host.Phase, "the match goes on for the host");
            Assert.AreEqual(ChibiOnlinePhase.Playing, gm.Guests[1].Phase, "and for the other guest");
        }

        /// <summary>Host of 3 fed by two probe guests with neutral inputs; each guest reports a checksum for frame 30.</summary>
        static ChibiRollbackSession HostWithReportedChecksums(bool guest1Honest, bool guest2Honest, bool guest2Reports = true,
                                                              int dropGuest2At = -1)
        {
            var cfg = Cfg(3);
            var settings = ChibiOnlineMatch.SettingsFor(3);
            var reference = new ChibiSimState();
            ChibiSimulation.Init(reference, cfg);
            var neutral = new ChibiInputPacket[3];
            for (int f = 0; f < ChibiRollbackSession.ChecksumInterval; f++) ChibiSimulation.StepPresent(reference, neutral, 0b111);
            uint truth = reference.ComputeChecksum();

            var p1 = new Probe();
            var p2 = new Probe();
            var host = new ChibiRollbackSession(0, cfg, new IChibiTransport[] { null, p1, p2, null },
                                                new ChibiScriptedInputSource(n => default), settings);
            for (int t = 0; t < 120; t++)
            {
                if (t == dropGuest2At) host.DeclareLeft(2);
                for (int g = 1; g <= 2; g++)
                {
                    if (g == 2 && dropGuest2At >= 0 && t >= dropGuest2At) continue;
                    int last = t + settings.InputDelay;
                    var m = new ChibiNetMessage { Slot = g, LastFrame = last, Count = Math.Min(16, last + 1), AckFrame = -1 };
                    if (t >= ChibiRollbackSession.ChecksumInterval && (g == 1 || guest2Reports))
                    {
                        bool honest = g == 1 ? guest1Honest : guest2Honest;
                        m.HasChecksum = true;
                        m.ChecksumFrame = ChibiRollbackSession.ChecksumInterval;
                        m.Checksum = honest ? truth : truth ^ 0x5A5A5A5Au;
                    }
                    (g == 1 ? p1 : p2).Inbox.Enqueue(Encode(m));
                }
                host.Tick();
            }
            return host;
        }

        [Test]
        public void Checksum_OneGuestDisagrees_HostDropsOnlyThatGuest()
        {
            var host = HostWithReportedChecksums(guest1Honest: true, guest2Honest: false);
            Assert.IsFalse(host.Desynced, "one forged or broken checksum does not void the match");
            Assert.AreNotEqual(RollbackInputBuffer.Never, host.LeftAt(2));
            Assert.AreEqual(RollbackInputBuffer.Never, host.LeftAt(1));
            Assert.AreEqual(1, host.ChecksumDrops);
        }

        [Test]
        public void Checksum_EveryGuestDisagrees_TheHostIsOutOfSync_MatchEnds()
        {
            var host = HostWithReportedChecksums(guest1Honest: false, guest2Honest: false);
            Assert.IsTrue(host.Desynced, "the host is the odd one out: a real desync, no contest");
            Assert.AreEqual(0, host.ChecksumDrops);
        }

        [Test]
        public void Checksum_AwaitedGuestLeaves_TheCheckDecidesInsteadOfHanging()
        {
            // SEC-007: guest 2 never sends its checksum, so the frame-30 check waits for it. When guest 2 leaves, the check
            // must decide with the guests still there (here: guest 1 disagrees with the host alone = desync).
            var host = HostWithReportedChecksums(guest1Honest: false, guest2Honest: true, guest2Reports: false, dropGuest2At: 80);
            Assert.AreNotEqual(RollbackInputBuffer.Never, host.LeftAt(2), "precondition: guest 2 left");
            Assert.IsTrue(host.Desynced, "the pending check was decided after the leave");
        }

        [Test]
        public void Checksum_AwaitedGuestLeaves_HonestRestIsFine()
        {
            var host = HostWithReportedChecksums(guest1Honest: true, guest2Honest: true, guest2Reports: false, dropGuest2At: 80);
            Assert.IsFalse(host.Desynced);
            Assert.AreEqual(RollbackInputBuffer.Never, host.LeftAt(1));
        }

        [Test]
        public void Guest_LeaveAnnounced_ResetsItsStallWindow()
        {
            // SEC-008: the guest's stalls were spent waiting for the player who left; they must not count towards Stalled.
            var net = new Probe();
            var settings = ChibiOnlineMatch.SettingsFor(3);
            var guest = new ChibiRollbackSession(1, Cfg(3), net, new ChibiScriptedInputSource(n => default), settings);
            for (int t = 0; t < 100; t++)
            {
                int last = t + settings.InputDelay;
                net.Inbox.Enqueue(Encode(new ChibiNetMessage { Slot = 0, LastFrame = last, Count = Math.Min(16, last + 1), AckFrame = -1 }));
                guest.Tick();   // player 2 never arrives: the guest stalls
            }
            Assert.Greater(guest.StalledInWindow, 50, "precondition: stalled waiting for player 2");

            var leave = new ChibiNetMessage { Slot = 0, LastFrame = 100 + settings.InputDelay, Count = 16, AckFrame = -1, LeaveMask = 1 << 2 };
            leave.LeaveFrame[2] = settings.InputDelay;
            net.Inbox.Enqueue(Encode(leave));
            guest.Tick();

            Assert.AreEqual(settings.InputDelay, guest.LeftAt(2));
            Assert.LessOrEqual(guest.StalledInWindow, 1, "the stalls caused by the leaver are forgotten");
        }

        [Test]
        public void OneVsOneRoomHost_GuestPublishedNoKey_RefusesEveryHello()
        {
            // SEC-006: in a room fight the seat is always bound; a missing / malformed key refuses (like the group host).
            foreach (string key in new[] { "", "not-a-key" })
            {
                var link = new ChibiLoopbackLink(1, 0, 0);
                var host = ChibiOnlineMatch.Host(link.A, new ChibiScriptedInputSource(n => default), FighterClass.Melee, 0, 0, 7u,
                                                 ChibiRollbackSettings.Default, expectKey: key);
                var join = ChibiOnlineMatch.Join(link.B, new ChibiScriptedInputSource(n => default), FighterClass.Ranged, 0, secret: 42UL);
                for (int i = 0; i < 60; i++) { link.Advance(); host.Tick(); join.Tick(); }
                Assert.AreEqual(ChibiMatchOutcome.Aborted, host.Result.Outcome, "key \"" + key + "\"");
            }
        }

        [Test]
        public void DevFlags_ParseAndValidate()
        {
            var args = new[] { "game.exe", "-authProfile", "p2", "-devName", "Tester2" };
            Assert.AreEqual("p2", ChibiDevFlags.ArgValue(args, "-authProfile"));
            Assert.AreEqual("Tester2", ChibiDevFlags.ArgValue(args, "-devname"), "flags are case-insensitive");
            Assert.IsNull(ChibiDevFlags.ArgValue(args, "-missing"));
            Assert.IsNull(ChibiDevFlags.ArgValue(new[] { "-authProfile" }, "-authProfile"), "a flag without a value");
            Assert.AreEqual("p-2_x", ChibiDevFlags.CleanProfile("p-2_x"));
            Assert.IsNull(ChibiDevFlags.CleanProfile("bad profile!"));
            Assert.IsNull(ChibiDevFlags.CleanProfile(new string('a', 31)));
        }

        sealed class MemoryStore : ISaveStore
        {
            public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
            public string Get(string key) => Data.TryGetValue(key, out var v) ? v : "";
            public void Set(string key, string value) => Data[key] = value;
            public void Flush() { }
        }

        [Test]
        public void DevName_IsNeverWrittenToTheSave()
        {
            var store = new MemoryStore();
            var save = SaveData.Load(store);
            save.playerName = "RealName";
            save.Save(store);
            save.diskPlayerName = save.playerName;   // what GameManager does for -devName
            save.playerName = "DevTester";
            save.Save(store);
            Assert.AreEqual("DevTester", save.playerName, "the run keeps the dev name");
            Assert.AreEqual("RealName", SaveData.Load(store).playerName, "the disk keeps the real name");
        }

        [Test]
        public void Checksum_EveryGuestAgrees_NoDrop()
        {
            var host = HostWithReportedChecksums(guest1Honest: true, guest2Honest: true);
            Assert.IsFalse(host.Desynced);
            Assert.AreEqual(0, host.ChecksumDrops);
            Assert.Greater(host.ChecksumsCompared, 1);
        }

        // ------------------------------------------------------------------ SEC-003 / L2: Relay connection bound to a seat

        [Test]
        public void SeatBinding_SlotsFollowRoomSeatsNotConnectionOrder()
        {
            ulong a = 0x1111222233334444UL, b = 0x5555666677778888UL;
            // Connection 1 holds secret a, connection 2 secret b; the room seated b in seat 1 and a in seat 2.
            var keys = new[] { "", ChibiRoomRules.SeatKey(b), ChibiRoomRules.SeatKey(a) };
            var gm = new GroupMatch(3, 2, 0, 0, new[] { "H", "A", "B" }, keys, new[] { 0UL, a, b });
            gm.Run(300);
            Assert.AreEqual(ChibiOnlinePhase.Playing, gm.Host.Phase, gm.Host.Result.ToString());
            Assert.AreEqual(2, gm.Guests[1].LocalIndex, "secret a sits in seat 2");
            Assert.AreEqual(1, gm.Guests[2].LocalIndex, "secret b sits in seat 1");
            Assert.AreEqual("A", gm.Host.PlayerName(2));
            Assert.IsFalse(gm.Host.Session.Desynced);
        }

        [Test]
        public void SeatBinding_UnknownSecret_Refused()
        {
            ulong a = 0x1111222233334444UL, b = 0x5555666677778888UL, stranger = 0x0BADC0DE0BADC0DEUL;
            var keys = new[] { "", ChibiRoomRules.SeatKey(a), ChibiRoomRules.SeatKey(b) };
            var gm = new GroupMatch(3, 2, 0, 0, new[] { "H", "A", "X" }, keys, new[] { 0UL, a, stranger });
            gm.Run(120);
            Assert.AreEqual(ChibiMatchOutcome.Aborted, gm.Host.Result.Outcome);
            Assert.AreEqual(ChibiRejectReason.InvalidConfig, gm.Host.Result.RejectReason);
        }

        [Test]
        public void SeatBinding_OnePlayerTwoConnections_SecondRefused()
        {
            ulong a = 0x1111222233334444UL, b = 0x5555666677778888UL;
            var keys = new[] { "", ChibiRoomRules.SeatKey(a), ChibiRoomRules.SeatKey(b) };
            var gm = new GroupMatch(3, 2, 0, 0, new[] { "H", "A", "A2" }, keys, new[] { 0UL, a, a });   // a takes two seats
            gm.Run(120);
            Assert.AreEqual(ChibiMatchOutcome.Aborted, gm.Host.Result.Outcome);
        }

        [Test]
        public void SeatBinding_OneVsOneRoomHost_RefusesAWrongSecret()
        {
            var link = new ChibiLoopbackLink(1, 0, 0);
            var host = ChibiOnlineMatch.Host(link.A, new ChibiScriptedInputSource(n => default), FighterClass.Melee, 0, 0, 7u,
                                             ChibiRollbackSettings.Default, expectKey: ChibiRoomRules.SeatKey(42UL));
            var join = ChibiOnlineMatch.Join(link.B, new ChibiScriptedInputSource(n => default), FighterClass.Ranged, 0, secret: 43UL);
            for (int i = 0; i < 60; i++) { link.Advance(); host.Tick(); join.Tick(); }
            Assert.AreEqual(ChibiMatchOutcome.Aborted, host.Result.Outcome);

            link = new ChibiLoopbackLink(1, 0, 0);
            host = ChibiOnlineMatch.Host(link.A, new ChibiScriptedInputSource(n => default), FighterClass.Melee, 0, 0, 7u,
                                         ChibiRollbackSettings.Default, expectKey: ChibiRoomRules.SeatKey(42UL));
            join = ChibiOnlineMatch.Join(link.B, new ChibiScriptedInputSource(n => default), FighterClass.Ranged, 0, secret: 42UL);
            for (int i = 0; i < 60; i++) { link.Advance(); host.Tick(); join.Tick(); }
            Assert.AreEqual(ChibiOnlinePhase.Playing, host.Phase, "the seated guest's secret is accepted");
        }

        [Test]
        public void Hello_SecretTrailer_RoundTripsAndIsOptional()
        {
            var o = RoundTripControl(new ChibiControlMessage
            {
                Type = ChibiControlType.Hello, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = ChibiMatchConfig.RulesVersion,
                Class = FighterClass.Heavy, Name = "Kira", Secret = 0xFEDCBA9876543210UL,
            });
            Assert.AreEqual(0xFEDCBA9876543210UL, o.Secret);
            Assert.AreEqual("Kira", o.ReceivedName());
            var none = RoundTripControl(new ChibiControlMessage
            {
                Type = ChibiControlType.Hello, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = ChibiMatchConfig.RulesVersion,
                Class = FighterClass.Heavy, Name = "Kira",
            });
            Assert.AreEqual(0UL, none.Secret);
            Assert.AreEqual(16, ChibiRoomRules.SeatKey(1UL).Length);
            Assert.AreEqual("", ChibiRoomRules.CleanSeatKey("<b>not-hex</b>"));
        }

        [Test]
        public void GroupMatch_HostForfeits_GuestsGetNoContest()
        {
            var gm = new GroupMatch(3, 2, 0, 0, new[] { "A", "B", "C" });
            gm.Run(400);
            gm.Host.Forfeit();
            gm.Run(30);
            for (int g = 1; g < 3; g++)
            {
                Assert.AreEqual(ChibiMatchOutcome.NoContest, gm.Guests[g].Result.Outcome, "guest " + g);
                Assert.AreEqual(ChibiEndCause.HostLeft, gm.Guests[g].Result.Cause);
            }
        }
    }
}
