using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Story type: Integration (handshake + rollback session + result rules over a simulated network).
    /// Evidence: automated EditMode test. Gate: BLOCKING.
    /// Covers: channel mux, control codec, handshake agreement / rejection, disconnect = opponent wins (Q4),
    /// desync = no contest (2026-11-09), confirmed-only KO results, both peers report mirrored results.
    /// </summary>
    public class ChibiOnlineMatchTests
    {
        const uint Seed = 0xBADC0DE;
        const int Stage = 1;
        const int MaxTicks = 20000;

        static ChibiInputPacket Bot(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)(n / 4) * 2654435761u + (uint)seed * 40503u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                int x = (int)(h % 3) - 1;
                ChibiButtons b = ChibiButtons.None;
                if (n % 2 == 0)
                {
                    if (h % 3 == 0) b |= ChibiButtons.Light;
                    if (h % 5 == 1) b |= ChibiButtons.Heavy;
                    if (h % 17 == 2) b |= ChibiButtons.Skill1;
                    if (h % 19 == 3) b |= ChibiButtons.Jump;
                }
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, 0), b);
            }
        }

        sealed class Duel
        {
            public ChibiLoopbackLink Link;
            public ChibiOnlineMatch Host, Joiner;

            public void Step()
            {
                Link.Advance();
                Host.Tick();
                Joiner.Tick();
            }

            public int RunUntil(System.Func<bool> done, int max = MaxTicks)
            {
                int t = 0;
                while (!done() && t < max) { Step(); t++; }
                return t;
            }
        }

        static Duel NewDuel(int latency = 3, int jitter = 1, int loss = 5, FighterClass hostClass = FighterClass.Melee,
                            FighterClass joinClass = FighterClass.Heavy)
        {
            var link = new ChibiLoopbackLink(latency, jitter, loss);
            return new Duel
            {
                Link = link,
                Host = ChibiOnlineMatch.Host(link.A, new ChibiScriptedInputSource(n => Bot(1, n)), hostClass, 0, Stage, Seed,
                                             ChibiRollbackSettings.Default),
                Joiner = ChibiOnlineMatch.Join(link.B, new ChibiScriptedInputSource(n => Bot(2, n)), joinClass, 1),
            };
        }

        // ------------------------------------------------------------------ handshake

        [Test]
        public void Handshake_LossyLink_BothAgreeOnHostConfig()
        {
            // Arrange
            var d = NewDuel(latency: 5, jitter: 2, loss: 30);

            // Act
            d.RunUntil(() => d.Host.Phase == ChibiOnlinePhase.Playing && d.Joiner.Phase == ChibiOnlinePhase.Playing, 600);

            // Assert
            Assert.AreEqual(ChibiOnlinePhase.Playing, d.Host.Phase);
            Assert.AreEqual(ChibiOnlinePhase.Playing, d.Joiner.Phase);
            var cfg = d.Joiner.Handshake.Config;
            Assert.AreEqual(Seed, cfg.Seed);
            Assert.AreEqual(Stage, cfg.Stage);
            Assert.AreEqual(FighterClass.Melee, cfg.Class0);
            Assert.AreEqual(FighterClass.Heavy, cfg.Class1);
            Assert.AreEqual(1, cfg.Passive1);
            Assert.AreEqual(d.Host.Handshake.MatchHash, d.Joiner.Handshake.MatchHash);
            Assert.AreEqual(0, d.Host.LocalIndex);
            Assert.AreEqual(1, d.Joiner.LocalIndex);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Handshake_HostHazardsChoice_ReachesTheJoinerAndTheHash(bool hazardsOff)
        {
            // Arrange: rules v20 / protocol 4 (ST-5): the host's switch travels in Start; both peers hash it.
            var link = new ChibiLoopbackLink(3, 1, 5);
            var host = ChibiOnlineMatch.Host(link.A, new ChibiScriptedInputSource(n => Bot(1, n)), FighterClass.Melee, 0,
                                             ChibiStageTable.Volcano, Seed, ChibiRollbackSettings.Default,
                                             ChibiHandshake.DefaultTimeoutTicks, "", hazardsOff);
            var join = ChibiOnlineMatch.Join(link.B, new ChibiScriptedInputSource(n => Bot(2, n)), FighterClass.Heavy, 1);

            // Act
            for (int t = 0; t < 600 && !(host.Phase == ChibiOnlinePhase.Playing && join.Phase == ChibiOnlinePhase.Playing); t++)
            {
                link.Advance(); host.Tick(); join.Tick();
            }

            // Assert
            Assert.AreEqual(ChibiOnlinePhase.Playing, join.Phase);
            Assert.AreEqual(hazardsOff, join.Handshake.Config.HazardsOff, "joiner took the host's switch from Start");
            Assert.AreEqual(ChibiStageTable.Volcano, join.Handshake.Config.Stage, "stage 5 accepted (0..7)");
            Assert.AreEqual(host.Handshake.MatchHash, join.Handshake.MatchHash);
        }

        [Test]
        public void StartCodec_RoundTripsHazardsOffAndNewStages()
        {
            var m = new ChibiControlMessage { Type = ChibiControlType.Start, Protocol = ChibiPacketCodec.ProtocolVersion,
                                              Rules = ChibiMatchConfig.RulesVersion };
            m.Config.Stage = ChibiStageTable.Ocean; m.Config.HazardsOff = true; m.Config.Class0 = FighterClass.Melee;
            m.Settings = ChibiRollbackSettings.Default;
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            ChibiControlCodec.Write(b, m);
            var r = new BitBuffer(ChibiControlCodec.MaxBytes);
            r.LoadFrom(b.Bytes, b.ByteLength);
            var o = new ChibiControlMessage();
            Assert.IsTrue(ChibiControlCodec.TryRead(r, o));
            Assert.AreEqual(ChibiStageTable.Ocean, o.Config.Stage);
            Assert.IsTrue(o.Config.HazardsOff);
        }

        /// <summary>Raw control-message sender, playing a peer from another build.</summary>
        static void SendControl(IChibiTransport t, ChibiControlMessage m)
        {
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            ChibiControlCodec.Write(b, m);
            t.Send(b.Bytes, b.ByteLength);
        }

        [Test]
        public void Handshake_JoinerWithOtherRulesVersion_IsRejectedOnBothSides()
        {
            // Arrange: a host, and an "old build" joiner speaking raw control messages.
            var link = new ChibiLoopbackLink(1, 0, 0);
            var host = ChibiOnlineMatch.Host(link.A, new ChibiScriptedInputSource(n => default), FighterClass.Melee, 0, 0, Seed,
                                             ChibiRollbackSettings.Default);
            var oldBuild = new ChibiChannelMux(link.B);

            // Act
            SendControl(oldBuild.Control, new ChibiControlMessage
            {
                Type = ChibiControlType.Hello, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = ChibiMatchConfig.RulesVersion - 1,
                Class = FighterClass.Magic,
            });
            for (int i = 0; i < 10; i++) { link.Advance(); host.Tick(); }

            // Assert: host refused, and told the peer why.
            Assert.AreEqual(ChibiMatchOutcome.Aborted, host.Result.Outcome);
            Assert.AreEqual(ChibiRejectReason.RulesMismatch, host.Result.RejectReason);
            var buf = new byte[256];
            var msg = new ChibiControlMessage();
            int n = oldBuild.Control.Receive(buf);
            Assert.Greater(n, 0, "no Reject reached the peer");
            var bb = new BitBuffer(ChibiControlCodec.MaxBytes);
            bb.LoadFrom(buf, n);
            Assert.IsTrue(ChibiControlCodec.TryRead(bb, msg));
            Assert.AreEqual(ChibiControlType.Reject, msg.Type);
            Assert.AreEqual((byte)ChibiRejectReason.RulesMismatch, msg.Reason);
        }

        [Test]
        public void Handshake_StartWithForeignContentHash_JoinerRejects()
        {
            // Arrange: a "host" from a build with different balance constants sends a hash we cannot reproduce.
            var link = new ChibiLoopbackLink(1, 0, 0);
            var joiner = ChibiOnlineMatch.Join(link.B, new ChibiScriptedInputSource(n => default), FighterClass.Ranged, 0);
            var otherHost = new ChibiChannelMux(link.A);
            var cfg = new ChibiMatchConfig { Seed = 1, Class0 = FighterClass.Melee, Class1 = FighterClass.Ranged, Stage = 0 };

            // Act
            SendControl(otherHost.Control, new ChibiControlMessage
            {
                Type = ChibiControlType.Start, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = ChibiMatchConfig.RulesVersion,
                Config = cfg, Settings = ChibiRollbackSettings.Default,
                MatchHash = ChibiControlCodec.MatchHash(cfg, ChibiRollbackSettings.Default) ^ 0x1u,
            });
            for (int i = 0; i < 10; i++) { link.Advance(); joiner.Tick(); }

            // Assert
            Assert.AreEqual(ChibiMatchOutcome.Aborted, joiner.Result.Outcome);
            Assert.AreEqual(ChibiRejectReason.ContentMismatch, joiner.Result.RejectReason);
        }

        // ------------------------------------------------------------------ SEC-001 (rules v21 / protocol 4)

        static ChibiOnlineMatch RawStartToJoiner(ChibiMatchConfig cfg, int expectStage = -1, bool? expectHazardsOff = null)
        {
            var link = new ChibiLoopbackLink(1, 0, 0);
            var joiner = ChibiOnlineMatch.Join(link.B, new ChibiScriptedInputSource(n => default), FighterClass.Ranged, 0,
                                               ChibiHandshake.DefaultTimeoutTicks, "", expectStage, expectHazardsOff);
            var host = new ChibiChannelMux(link.A);
            SendControl(host.Control, new ChibiControlMessage
            {
                Type = ChibiControlType.Start, Protocol = ChibiPacketCodec.ProtocolVersion, Rules = ChibiMatchConfig.RulesVersion,
                Config = cfg, Settings = ChibiRollbackSettings.Default,
                MatchHash = ChibiControlCodec.MatchHash(cfg, ChibiRollbackSettings.Default),   // a correct hash: only the field is wrong
            });
            for (int i = 0; i < 10; i++) { link.Advance(); joiner.Tick(); }
            return joiner;
        }

        [TestCase(8)]
        [TestCase(15)]
        public void Handshake_StartWithStageOutOfRange_InvalidConfig_NoThrow(int stage)
        {
            var cfg = new ChibiMatchConfig { Seed = 1, Class0 = FighterClass.Melee, Class1 = FighterClass.Ranged, Stage = stage };
            ChibiOnlineMatch joiner = null;
            Assert.DoesNotThrow(() => joiner = RawStartToJoiner(cfg));
            Assert.AreEqual(ChibiMatchOutcome.Aborted, joiner.Result.Outcome);
            Assert.AreEqual(ChibiRejectReason.InvalidConfig, joiner.Result.RejectReason);
        }

        [Test]
        public void Handshake_RoomJoinerExpectingHazardsOff_RejectsStartWithHazardsOn()
        {
            var cfg = new ChibiMatchConfig { Seed = 1, Class0 = FighterClass.Melee, Class1 = FighterClass.Ranged,
                                             Stage = ChibiStageTable.Volcano, HazardsOff = false };
            var joiner = RawStartToJoiner(cfg, ChibiStageTable.Volcano, expectHazardsOff: true);
            Assert.AreEqual(ChibiRejectReason.InvalidConfig, joiner.Result.RejectReason, "SEC-001: host flipped the hazards switch");
        }

        [Test]
        public void Handshake_RoomJoiner_RejectsAStartWithAnotherStage_AcceptsTheAgreedOne()
        {
            var cfg = new ChibiMatchConfig { Seed = 1, Class0 = FighterClass.Melee, Class1 = FighterClass.Ranged,
                                             Stage = ChibiStageTable.Ocean, HazardsOff = true, Intro = true };   // online: intro on
            Assert.AreEqual(ChibiRejectReason.InvalidConfig, RawStartToJoiner(cfg, ChibiStageTable.Volcano, true).Result.RejectReason,
                            "SEC-001: host swapped the stage");
            var ok = RawStartToJoiner(cfg, ChibiStageTable.Ocean, true);
            Assert.AreEqual(ChibiHandshakeState.Agreed, ok.Handshake.State, "the agreed stage and switch are accepted");
            Assert.IsTrue(ok.Handshake.Config.HazardsOff);
        }

        [Test]
        public void StartCodec_TruncatedBeforeTheHazardsBit_TryReadFalse()
        {
            // Arrange: a Start written by hand up to and including the stage, then cut (an old or hostile sender).
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);
            b.Write(ChibiControlCodec.Magic, 8);
            b.Write((uint)ChibiControlType.Start, 4);
            b.Write((uint)ChibiPacketCodec.ProtocolVersion, 8);
            b.Write((uint)ChibiMatchConfig.RulesVersion, 16);
            b.Write(1u, 32);
            b.Write(0u, 4); b.Write(0u, 1); b.Write(2u, 4); b.Write(0u, 1);
            b.Write(5u, 4);                                    // stage, then nothing
            var r = new BitBuffer(ChibiControlCodec.MaxBytes);
            r.LoadFrom(b.Bytes, b.ByteLength);

            // Act / Assert
            bool ok = true;
            Assert.DoesNotThrow(() => ok = ChibiControlCodec.TryRead(r, new ChibiControlMessage()));
            Assert.IsFalse(ok, "a truncated Start is malformed");
        }

        [Test]
        public void Handshake_NoPeer_TimesOut()
        {
            var link = new ChibiLoopbackLink(1, 0, 0);
            var joiner = ChibiOnlineMatch.Join(link.B, new ChibiScriptedInputSource(n => default), FighterClass.Ranged, 0,
                                               handshakeTimeoutTicks: 120);

            for (int i = 0; i < 200; i++) { link.Advance(); joiner.Tick(); }

            Assert.AreEqual(ChibiMatchOutcome.Aborted, joiner.Result.Outcome);
            Assert.AreEqual(ChibiHandshakeState.TimedOut, joiner.Result.HandshakeState);
        }

        [Test]
        public void ControlCodec_StartRoundTrip_AllFieldsPreserved()
        {
            var cfg = new ChibiMatchConfig
            {
                Seed = 0xFFFFFFFF, Class0 = FighterClass.Polearm, Passive0 = 1, Class1 = FighterClass.Magic, Passive1 = 0, Stage = 2,
            };
            var sent = new ChibiControlMessage
            {
                Type = ChibiControlType.Start, Protocol = 255, Rules = 65535, Config = cfg,
                Settings = new ChibiRollbackSettings { InputDelay = 8, MaxPrediction = 15, DisconnectTicks = 65535 },
                MatchHash = 0xDEADBEEF,
            };
            var b = new BitBuffer(ChibiControlCodec.MaxBytes);

            ChibiControlCodec.Write(b, sent);
            b.BeginRead();
            var got = new ChibiControlMessage();
            bool ok = ChibiControlCodec.TryRead(b, got);

            Assert.IsTrue(ok);
            Assert.AreEqual(ChibiControlType.Start, got.Type);
            Assert.AreEqual(255, got.Protocol);
            Assert.AreEqual(65535, got.Rules);
            Assert.AreEqual(cfg.ComputeHash(), got.Config.ComputeHash());
            Assert.AreEqual(8, got.Settings.InputDelay);
            Assert.AreEqual(15, got.Settings.MaxPrediction);
            Assert.AreEqual(65535, got.Settings.DisconnectTicks);
            Assert.AreEqual(0xDEADBEEF, got.MatchHash);
        }

        [Test]
        public void Mux_RoutesByMagicAndDropsForeignDatagrams()
        {
            var link = new ChibiLoopbackLink(0, 0, 0);
            var mux = new ChibiChannelMux(link.B);
            link.A.Send(new byte[] { ChibiControlCodec.Magic, 1 }, 2);
            link.A.Send(new byte[] { 0x42, 1, 2 }, 3);
            link.A.Send(new byte[] { ChibiPacketCodec.Magic, 9 }, 2);
            var buf = new byte[256];

            int s = mux.Session.Receive(buf);
            byte sessionFirst = buf[0];
            int c = mux.Control.Receive(buf);

            Assert.AreEqual(2, s);
            Assert.AreEqual(ChibiPacketCodec.Magic, sessionFirst);
            Assert.AreEqual(2, c);
            Assert.AreEqual(ChibiControlCodec.Magic, buf[0]);
            Assert.AreEqual(0, mux.Session.Receive(buf));
            Assert.AreEqual(1, mux.Dropped, "unknown magic must be dropped");
        }

        // ------------------------------------------------------------------ results

        [Test]
        public void FullMatch_BothPeersReportMirroredConfirmedResult()
        {
            // Arrange
            var d = NewDuel(latency: 4, jitter: 2, loss: 10);

            // Act: play to the end (KO or the 180 s timer) and through the goodbye.
            d.RunUntil(() => d.Host.Phase == ChibiOnlinePhase.Closed && d.Joiner.Phase == ChibiOnlinePhase.Closed);

            // Assert
            var h = d.Host.Result;
            var j = d.Joiner.Result;
            Assert.AreEqual(ChibiEndCause.MatchOver, h.Cause, "host: " + h);
            Assert.AreEqual(ChibiEndCause.MatchOver, j.Cause, "joiner: " + j);
            Assert.AreEqual(h.Winner, j.Winner, "peers disagree on the winner");
            if (h.Winner < 0) { Assert.AreEqual(ChibiMatchOutcome.Draw, h.Outcome); Assert.AreEqual(ChibiMatchOutcome.Draw, j.Outcome); }
            else
            {
                Assert.AreEqual(h.Winner == 0 ? ChibiMatchOutcome.Win : ChibiMatchOutcome.Loss, h.Outcome);
                Assert.AreEqual(h.Winner == 1 ? ChibiMatchOutcome.Win : ChibiMatchOutcome.Loss, j.Outcome);
            }
            Assert.IsFalse(d.Host.Session.Desynced || d.Joiner.Session.Desynced);
            // Both replays re-simulate to the same final state.
            uint ha = ChibiReplay.Simulate(d.Host.Replay.Config, d.Host.Replay.P0, d.Host.Replay.P1).ComputeChecksum();
            int common = System.Math.Min(d.Host.Replay.FrameCount, d.Joiner.Replay.FrameCount);
            Assert.Greater(common, 0);
            uint hb = ChibiReplay.Simulate(d.Joiner.Replay.Config, d.Joiner.Replay.P0.GetRange(0, common),
                                           d.Joiner.Replay.P1.GetRange(0, common)).ComputeChecksum();
            uint ha2 = ChibiReplay.Simulate(d.Host.Replay.Config, d.Host.Replay.P0.GetRange(0, common),
                                            d.Host.Replay.P1.GetRange(0, common)).ComputeChecksum();
            Assert.AreEqual(ha2, hb);
            Assert.AreNotEqual(0u, ha);
        }

        [Test]
        public void OpponentForfeits_LocalPlayerWinsAtOnce()
        {
            // Arrange
            var d = NewDuel(loss: 0);
            d.RunUntil(() => d.Host.Phase == ChibiOnlinePhase.Playing && d.Joiner.Phase == ChibiOnlinePhase.Playing);
            d.RunUntil(() => false, 120);

            // Act
            d.Joiner.Forfeit();
            int ticks = d.RunUntil(() => d.Host.HasResult, 60);

            // Assert: the goodbye ends it immediately, not after the 5 s silence timeout.
            Assert.AreEqual(ChibiMatchOutcome.Win, d.Host.Result.Outcome);
            Assert.AreEqual(ChibiEndCause.OpponentLeft, d.Host.Result.Cause);
            Assert.AreEqual(ChibiMatchOutcome.Loss, d.Joiner.Result.Outcome);
            Assert.AreEqual(ChibiEndCause.LocalForfeit, d.Joiner.Result.Cause);
            Assert.Less(ticks, 30);
        }

        [Test]
        public void OpponentVanishes_LocalPlayerWinsAfterTimeout()
        {
            // Arrange
            var d = NewDuel(loss: 0);
            d.RunUntil(() => d.Host.Phase == ChibiOnlinePhase.Playing && d.Joiner.Phase == ChibiOnlinePhase.Playing);
            d.RunUntil(() => false, 120);

            // Act: the joiner's machine dies (no goodbye): only the host keeps ticking.
            int t = 0;
            while (!d.Host.HasResult && t++ < 2000) { d.Link.Advance(); d.Host.Tick(); }

            // Assert
            Assert.AreEqual(ChibiMatchOutcome.Win, d.Host.Result.Outcome);
            Assert.AreEqual(ChibiEndCause.OpponentTimedOut, d.Host.Result.Cause);
            Assert.AreEqual(0, d.Host.Result.Winner);
            Assert.GreaterOrEqual(t, ChibiRollbackSettings.Default.DisconnectTicks, "ended before the timeout");
        }

        [Test]
        public void ForgedChecksum_EndsAsNoContestForBothPlayers()
        {
            // Arrange: both playing, before the first checksum (frame 30) is compared.
            var d2 = NewDuel(loss: 0);
            d2.RunUntil(() => d2.Host.Phase == ChibiOnlinePhase.Playing && d2.Joiner.Phase == ChibiOnlinePhase.Playing);
            Assert.Less(d2.Host.Session.Frame, ChibiRollbackSession.ChecksumInterval, "precondition");
            var lie = new ChibiNetMessage
            {
                LastFrame = 0, AckFrame = -1, Count = 1, HasChecksum = true,
                ChecksumFrame = ChibiRollbackSession.ChecksumInterval, Checksum = 0x12345678u,
            };
            var bb = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(bb, lie);

            // Act: a cheater (or a corrupt link) claims a frame-30 state hash that the host's sim will not produce.
            d2.Link.B.Send(bb.Bytes, bb.ByteLength);
            d2.RunUntil(() => d2.Host.HasResult && d2.Joiner.HasResult, 600);

            // Assert: no winner for anybody.
            Assert.AreEqual(ChibiMatchOutcome.NoContest, d2.Host.Result.Outcome);
            Assert.AreEqual(ChibiEndCause.Desync, d2.Host.Result.Cause);
            Assert.AreEqual(ChibiMatchOutcome.NoContest, d2.Joiner.Result.Outcome, "the peer must end as no contest too");
            Assert.AreEqual(-1, d2.Host.Result.Winner);
        }

        [Test]
        public void LocalForfeitDuringHandshake_Aborts()
        {
            var d = NewDuel();
            d.Step();

            d.Joiner.Forfeit();
            d.RunUntil(() => d.Host.HasResult, 60);

            Assert.AreEqual(ChibiMatchOutcome.Aborted, d.Joiner.Result.Outcome);
            Assert.AreEqual(ChibiMatchOutcome.Aborted, d.Host.Result.Outcome);
            Assert.AreEqual(ChibiHandshakeState.PeerLeft, d.Host.Result.HandshakeState);
        }
    }
}
