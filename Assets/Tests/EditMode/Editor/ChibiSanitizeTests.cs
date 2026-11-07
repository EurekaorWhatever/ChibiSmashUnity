using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Regression tests: locally sampled input must be sanitised exactly like the remote peer sanitises it on
    /// receipt, otherwise the two peers' confirmed inputs (and replays) disagree for the same frame.
    /// </summary>
    public class ChibiSanitizeTests
    {
        const uint Seed = 0xC0FFEE;
        const int RunTicks = 600;
        const int MinConfirmedFrames = 500;
        const ushort IllegalDir = 0xF;   // direction nibble 15: outside ChibiDir 0..8

        static ChibiMatchConfig Cfg() => new ChibiMatchConfig
        {
            Seed = Seed, Class0 = FighterClass.Melee, Class1 = FighterClass.Magic, Passive0 = 0, Passive1 = 1
        };

        /// <summary>Peer A: illegal direction nibble (with Light held) every 3rd frame, walking east otherwise.</summary>
        static ChibiInputPacket IllegalScript(int f)
        {
            if (f % 3 == 0) return new ChibiInputPacket { Raw = (ushort)(IllegalDir | (ushort)ChibiButtons.Light) };
            return ChibiInputPacket.Make(ChibiDir.E, f % 10 == 1 ? ChibiButtons.Jump : ChibiButtons.None);
        }

        static ChibiInputPacket WestScript(int f) =>
            ChibiInputPacket.Make(ChibiDir.W, f % 20 == 5 ? ChibiButtons.Heavy : ChibiButtons.None);

        [Test]
        public void IllegalLocalDirection_NoDesyncBetweenPeers()
        {
            // Arrange
            var cfg = Cfg();
            var link = new ChibiLoopbackLink(3, 0, 0);
            var a = new ChibiRollbackSession(0, cfg, link.A, new ChibiScriptedInputSource(IllegalScript));
            var b = new ChibiRollbackSession(1, cfg, link.B, new ChibiScriptedInputSource(WestScript));
            var recA = new ChibiReplayRecorder(cfg);
            var recB = new ChibiReplayRecorder(cfg);
            a.OnFrameConfirmed += recA.Add;
            b.OnFrameConfirmed += recB.Add;
            bool desync = false;
            a.OnDesync += (f, l, r) => desync = true;
            b.OnDesync += (f, l, r) => desync = true;

            // Act
            for (int t = 0; t < RunTicks; t++)
            {
                link.Advance();
                a.Tick();
                b.Tick();
            }

            // Assert
            Assert.IsFalse(desync, "OnDesync fired");
            int common = System.Math.Min(recA.FrameCount, recB.FrameCount);
            Assert.GreaterOrEqual(common, MinConfirmedFrames, "too few confirmed frames to compare");
            for (int i = 0; i < common; i++)
            {
                Assert.AreEqual(recB.P0[i], recA.P0[i], "peers disagree on P0's confirmed input at frame " + i);
                Assert.AreEqual(recB.P1[i], recA.P1[i], "peers disagree on P1's confirmed input at frame " + i);
                Assert.AreEqual(new ChibiInputPacket { Raw = recA.P0[i] }.Sanitized().Raw, recA.P0[i], "unsanitised local input stored at frame " + i);
            }
            uint ha = ChibiReplay.Simulate(cfg, recA.P0.GetRange(0, common), recA.P1.GetRange(0, common)).ComputeChecksum();
            uint hb = ChibiReplay.Simulate(cfg, recB.P0.GetRange(0, common), recB.P1.GetRange(0, common)).ComputeChecksum();
            Assert.AreEqual(hb, ha, "replaying each peer's confirmed inputs lands on different states");
        }

        [Test]
        public void LocalSession_SanitizesIllegalDirection()
        {
            // Arrange
            var session = new ChibiLocalSession(Cfg(), new ChibiScriptedInputSource(IllegalScript),
                new ChibiScriptedInputSource(f => new ChibiInputPacket { Raw = (ushort)(IllegalDir | (ushort)ChibiButtons.Jump) }));
            var bad = new List<string>();
            int frames = 0;
            session.OnFrame += (f, in0, in1) =>
            {
                frames++;
                if (!in0.Equals(in0.Sanitized())) bad.Add("P0@" + f);
                if (!in1.Equals(in1.Sanitized())) bad.Add("P1@" + f);
            };

            // Act
            for (int t = 0; t < RunTicks && session.Tick(); t++) { }

            // Assert
            Assert.Greater(frames, 0, "no frames simulated");
            Assert.IsEmpty(bad, "unsanitised inputs: " + string.Join(", ", bad));
        }
    }
}
