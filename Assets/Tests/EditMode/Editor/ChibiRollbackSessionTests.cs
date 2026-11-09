using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Story type: Logic (rollback session liveness + hostile-peer hardening). Evidence: automated EditMode test.
    /// Gate: BLOCKING.
    ///
    /// Regression tests for rollback-gap-report.md:
    ///   2.1 HIGH  the resend window held only the newest 8 inputs, so a burst of loss longer than that window left a
    ///             permanent hole in the remote's input stream and stalled both peers forever.
    ///   2.8       a forged AckFrame beyond anything we sent turned our redundancy off (Count fell to 1).
    /// </summary>
    public class ChibiRollbackSessionTests
    {
        const uint Seed = 0xC0FFEE;
        const int LatencyTicks = 4;          // ~67 ms one way, ~133 ms RTT
        const int BlackoutStartTick = 300;
        const int BlackoutTicks = 24;        // 400 ms Wi-Fi dropout: longer than the old 8-input window
        const int TotalTicks = 1200;
        /// <summary>After recovery the confirmed frame must trail the live frame by no more than a normal RTT + delay.</summary>
        const int MaxConfirmLagAtEnd = 30;

        static ChibiMatchConfig Cfg() => new ChibiMatchConfig
        {
            Seed = Seed, Class0 = FighterClass.Melee, Class1 = FighterClass.Polearm, Passive0 = 0, Passive1 = 1, Stage = 0,
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

        sealed class Pair
        {
            public ChibiLoopbackLink Link;
            public ChibiRollbackSession A, B;
            public ChibiReplayRecorder RecA, RecB;
            public bool Disconnected, Desynced;

            public void Run(int ticks)
            {
                for (int t = 0; t < ticks; t++) { Link.Advance(); A.Tick(); B.Tick(); }
            }
        }

        static Pair NewPair(int latency, int jitter, int loss)
        {
            var cfg = Cfg();
            var p = new Pair { Link = new ChibiLoopbackLink(latency, jitter, loss) };
            p.A = new ChibiRollbackSession(0, cfg, p.Link.A, new ChibiScriptedInputSource(n => Bot(1, n)));
            p.B = new ChibiRollbackSession(1, cfg, p.Link.B, new ChibiScriptedInputSource(n => Bot(2, n)));
            p.RecA = new ChibiReplayRecorder(cfg);
            p.RecB = new ChibiReplayRecorder(cfg);
            p.A.OnFrameConfirmed += p.RecA.Add;
            p.B.OnFrameConfirmed += p.RecB.Add;
            p.A.OnDisconnect += _ => p.Disconnected = true;
            p.B.OnDisconnect += _ => p.Disconnected = true;
            p.A.OnDesync += (f, l, r) => p.Desynced = true;
            p.B.OnDesync += (f, l, r) => p.Desynced = true;
            return p;
        }

        // ------------------------------------------------------------------ 2.1 burst loss

        [Test]
        public void BurstLossLongerThanResendWindow_BothPeersRecover()
        {
            // Arrange
            var p = NewPair(LatencyTicks, 0, 0);
            p.Run(BlackoutStartTick);

            // Act: 400 ms of total loss both ways, then a clean link again.
            p.Link.DropAllFor(BlackoutTicks);
            p.Run(TotalTicks - BlackoutStartTick);

            // Assert: both sides confirmed (nearly) everything they simulated, and agree on it.
            Assert.IsFalse(p.Disconnected, "a 400 ms outage must not end the match");
            Assert.IsFalse(p.Desynced, "desync after recovery");
            Assert.LessOrEqual(p.A.Frame - p.A.ConfirmedFrame, MaxConfirmLagAtEnd,
                $"A stuck: frame {p.A.Frame}, confirmed {p.A.ConfirmedFrame} (input hole never repaired)");
            Assert.LessOrEqual(p.B.Frame - p.B.ConfirmedFrame, MaxConfirmLagAtEnd,
                $"B stuck: frame {p.B.Frame}, confirmed {p.B.ConfirmedFrame} (input hole never repaired)");
            Assert.Greater(p.A.Frame, TotalTicks - 200, "A spent most of the match stalled");
            int common = System.Math.Min(p.RecA.FrameCount, p.RecB.FrameCount);
            for (int i = 0; i < common; i++)
            {
                Assert.AreEqual(p.RecA.P0[i], p.RecB.P0[i], "P0 input differs at frame " + i);
                Assert.AreEqual(p.RecA.P1[i], p.RecB.P1[i], "P1 input differs at frame " + i);
            }
        }

        // ------------------------------------------------------------------ 2.8 forged ack

        /// <summary>Captures what the session sends and lets the test inject datagrams.</summary>
        sealed class ProbeTransport : IChibiTransport
        {
            public readonly Queue<byte[]> Inbox = new Queue<byte[]>();
            public byte[] LastSent;
            public int LastSentLength;

            public void Send(byte[] data, int length)
            {
                LastSent = (byte[])data.Clone();
                LastSentLength = length;
            }

            public int Receive(byte[] buffer)
            {
                if (Inbox.Count == 0) return 0;
                var d = Inbox.Dequeue();
                System.Buffer.BlockCopy(d, 0, buffer, 0, d.Length);
                return d.Length;
            }
        }

        static byte[] Encode(ChibiNetMessage m)
        {
            var b = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(b, m);
            var bytes = new byte[b.ByteLength];
            System.Buffer.BlockCopy(b.Bytes, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        static ChibiNetMessage Decode(ProbeTransport t)
        {
            var b = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            b.LoadFrom(t.LastSent, t.LastSentLength);
            var m = new ChibiNetMessage();
            Assert.IsTrue(ChibiPacketCodec.TryRead(b, m), "session sent an unreadable datagram");
            return m;
        }

        [Test]
        public void ForgedAckBeyondLocalFrames_DoesNotDisableResend()
        {
            // Arrange: a session whose peer has acknowledged nothing, so it is re-sending several inputs.
            const int Ticks = 6;
            var net = new ProbeTransport();
            var s = new ChibiRollbackSession(0, Cfg(), net, new ChibiScriptedInputSource(n => Bot(1, n)));
            for (int i = 0; i < Ticks; i++) s.Tick();
            int honestCount = Decode(net).Count;
            Assert.Greater(honestCount, 1, "precondition: several unacked inputs in flight");

            // Act: the peer claims to hold frames we have never produced.
            var forged = new ChibiNetMessage { LastFrame = 1, AckFrame = 1_000_000, Advantage = 0, Count = 1 };
            net.Inbox.Enqueue(Encode(forged));
            s.Tick();

            // Assert: we still re-send everything that was really unacknowledged (plus the new frame).
            Assert.GreaterOrEqual(Decode(net).Count, honestCount, "a forged ack switched redundancy off");
            Assert.AreEqual(1, s.RejectedPackets, "the forged datagram must be counted as rejected");
        }

        // ------------------------------------------------------------------ resend window shape

        [Test]
        public void UnackedSpanBeyondWindow_SendsOldestFramesFirst()
        {
            // Arrange: no peer at all, so nothing is ever acked; run until more than 16 frames are unacked would be
            // possible (the session stalls at MaxPrediction, so use a large prediction window to get past 16).
            var settings = ChibiRollbackSettings.Default;
            settings.MaxPrediction = 15;
            settings.InputDelay = 4;
            var net = new ProbeTransport();
            var s = new ChibiRollbackSession(0, Cfg(), net, new ChibiScriptedInputSource(n => Bot(1, n)), settings);

            // Act
            for (int i = 0; i < 40; i++) s.Tick();
            var m = Decode(net);

            // Assert: the window starts at frame 0 (oldest unacked), not at the newest frame minus 15.
            Assert.AreEqual(ChibiNetMessage.MaxInputs, m.Count);
            Assert.AreEqual(0, m.LastFrame - m.Count + 1, "window must start at the oldest unacknowledged frame");
        }

        // ------------------------------------------------------------------ disconnect

        [Test]
        public void Silence_RaisesTimeoutExactlyOnce()
        {
            // Arrange
            var settings = ChibiRollbackSettings.Default;
            var s = new ChibiRollbackSession(0, Cfg(), new ProbeTransport(), new ChibiScriptedInputSource(n => default), settings);
            int raised = 0;
            ChibiDisconnectReason reason = ChibiDisconnectReason.RemoteLeft;
            s.OnDisconnect += r => { raised++; reason = r; };

            // Act: well past the timeout.
            for (int i = 0; i < settings.DisconnectTicks * 3; i++) s.Tick();

            // Assert
            Assert.AreEqual(1, raised);
            Assert.AreEqual(ChibiDisconnectReason.Timeout, reason);
            Assert.IsTrue(s.Disconnected);
        }

        [Test]
        public void Silence_BeforeTimeout_DoesNotDisconnect()
        {
            // Arrange
            var settings = ChibiRollbackSettings.Default;
            var s = new ChibiRollbackSession(0, Cfg(), new ProbeTransport(), new ChibiScriptedInputSource(n => default), settings);
            bool raised = false;
            s.OnDisconnect += _ => raised = true;

            // Act
            for (int i = 0; i < settings.DisconnectTicks; i++) s.Tick();

            // Assert
            Assert.IsFalse(raised);
            Assert.AreEqual(settings.DisconnectTicks, s.SilentTicks);
        }

        [Test]
        public void NotifyDisconnected_RaisesOnceWithGivenReason()
        {
            // Arrange
            var s = new ChibiRollbackSession(0, Cfg(), new ProbeTransport(), new ChibiScriptedInputSource(n => default));
            int raised = 0;
            ChibiDisconnectReason reason = ChibiDisconnectReason.Timeout;
            s.OnDisconnect += r => { raised++; reason = r; };

            // Act
            s.NotifyDisconnected(ChibiDisconnectReason.RemoteLeft);
            s.NotifyDisconnected(ChibiDisconnectReason.TransportLost);

            // Assert
            Assert.AreEqual(1, raised);
            Assert.AreEqual(ChibiDisconnectReason.RemoteLeft, reason);
        }

        // ------------------------------------------------------------------ bad networks stay in sync

        [TestCase(2, 0, 0)]       // LAN
        [TestCase(6, 2, 5)]       // ~200 ms RTT, jitter, 5% loss
        [TestCase(9, 3, 15)]      // ~300 ms RTT, heavy jitter, 15% loss
        public void HostileLink_NoDesyncAndInputsAgree(int latency, int jitter, int loss)
        {
            // Arrange
            var p = NewPair(latency, jitter, loss);
            int rollbackEvents = 0;
            p.A.OnRollback += (from, to) => { Assert.Less(from, to); rollbackEvents++; };

            // Act
            p.Run(TotalTicks);

            // Assert
            Assert.IsFalse(p.Desynced, "desync");
            Assert.IsFalse(p.Disconnected, "disconnect");
            Assert.Greater(p.A.ChecksumsCompared, 10, "checksums were never compared");
            Assert.AreEqual(p.A.RollbackCount, rollbackEvents, "OnRollback must fire once per rollback");
            Assert.LessOrEqual(p.A.MaxRollbackDepth, p.A.Settings.MaxPrediction + 1);
            int common = System.Math.Min(p.RecA.FrameCount, p.RecB.FrameCount);
            Assert.Greater(common, TotalTicks / 2);
            for (int i = 0; i < common; i++)
            {
                Assert.AreEqual(p.RecA.P0[i], p.RecB.P0[i], "P0 input differs at frame " + i);
                Assert.AreEqual(p.RecA.P1[i], p.RecB.P1[i], "P1 input differs at frame " + i);
            }
            Assert.Greater(p.A.RttTicksX8, 0, "RTT was never sampled");
        }

        // ------------------------------------------------------------------ settings

        [Test]
        public void Settings_OutOfRange_Throw()
        {
            var bad = ChibiRollbackSettings.Default;
            bad.MaxPrediction = 0;
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                new ChibiRollbackSession(0, Cfg(), new ProbeTransport(), new ChibiScriptedInputSource(n => default), bad));
        }
    }
}
