using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Determinism, snapshot/restore and integer-math tests for the netcode simulation.</summary>
    public class ChibiDeterminismTests
    {
        const uint Seed = 0xC0FFEE;
        const int BotSeed0 = 1, BotSeed1 = 2;
        const int RunTicks = 3600;
        const int CheckInterval = 60;
        const int SnapFrame = 500, EndFrame = 1000;

        static ChibiMatchConfig Cfg()
        {
            return new ChibiMatchConfig
            {
                Seed = Seed, Class0 = FighterClass.Melee, Class1 = FighterClass.Magic, Passive0 = 0, Passive1 = 1
            };
        }

        /// <summary>Pure function of (seed, frame): a busy pseudo-random player. Never touches wall time.</summary>
        static ChibiInputPacket Bot(int seed, int frame)
        {
            unchecked
            {
                uint h = (uint)(frame / 7) * 2654435761u + (uint)seed * 40503u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                int x = (int)(h % 3) - 1;
                int y = (h >> 3) % 8 == 0 ? -1 : 0;
                uint g = (uint)frame * 2246822519u + (uint)seed * 3266489917u;
                g ^= g >> 15; g *= 2654435761u; g ^= g >> 13;
                ChibiButtons b = ChibiButtons.None;
                if (g % 9 == 0) b |= ChibiButtons.Light;
                if (g % 23 == 1) b |= ChibiButtons.Heavy;
                if (g % 31 == 2) b |= ChibiButtons.Jump;
                if (g % 41 == 3) b |= ChibiButtons.Skill1;
                if (g % 47 == 4) b |= ChibiButtons.Skill2;
                if (g % 53 == 5) b |= ChibiButtons.Skill3;
                if (g % 61 == 6) b |= ChibiButtons.Shield;
                if (g % 67 == 7) b |= ChibiButtons.Ultimate;
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, y), b);
            }
        }

        static ChibiLocalSession NewSession()
        {
            return new ChibiLocalSession(Cfg(),
                new ChibiScriptedInputSource(f => Bot(BotSeed0, f)),
                new ChibiScriptedInputSource(f => Bot(BotSeed1, f)));
        }

        // ------------------------------------------------------------------ simulation determinism

        [Test]
        public void SameInputsTwice_SameChecksum()
        {
            var a = NewSession();
            var b = NewSession();
            for (int t = 0; t < RunTicks; t++)
            {
                bool ra = a.Tick();
                bool rb = b.Tick();
                Assert.AreEqual(ra, rb, "MatchOver diverged at tick " + t);
                if (a.Frame % CheckInterval == 0)
                    Assert.AreEqual(a.State.ComputeChecksum(), b.State.ComputeChecksum(), "checksum diverged at frame " + a.Frame);
                if (!ra) break;
            }
            Assert.AreEqual(a.Frame, b.Frame);
            Assert.AreEqual(a.State.ComputeChecksum(), b.State.ComputeChecksum());
        }

        [Test]
        public void SnapshotRestore_ResimulatesIdentically()
        {
            var live = NewSession();
            while (live.Frame < SnapFrame) live.Tick();
            Assert.AreEqual(SnapFrame, live.Frame);

            var snapshot = new ChibiSimState();
            snapshot.CopyFrom(live.State);
            Assert.AreEqual(live.State.ComputeChecksum(), snapshot.ComputeChecksum(), "snapshot differs at copy time");

            while (live.Frame < EndFrame) live.Tick();
            uint expected = live.State.ComputeChecksum();

            var restored = new ChibiSimState();
            restored.CopyFrom(snapshot);
            for (int f = SnapFrame; f < EndFrame; f++)
                ChibiSimulation.Step(restored, Bot(BotSeed0, f), Bot(BotSeed1, f));

            Assert.AreEqual(EndFrame, restored.Frame);
            Assert.AreEqual(expected, restored.ComputeChecksum());
        }

        // ------------------------------------------------------------------ RNG

        [Test]
        public void Rng_SameSeed_SameSequence()
        {
            var a = new ChibiRng(12345u);
            var b = new ChibiRng(12345u);
            for (int i = 0; i < 1000; i++) Assert.AreEqual(a.NextU32(), b.NextU32(), "draw " + i);
        }

        [Test]
        public void Rng_ZeroSeed_NotStuck()
        {
            var r = new ChibiRng(0u);
            Assert.AreNotEqual(0u, r.State);
            uint prev = r.State;
            for (int i = 0; i < 100; i++)
            {
                uint v = r.NextU32();
                Assert.AreNotEqual(0u, v);
                Assert.AreNotEqual(prev, v);
                prev = v;
            }
        }

        // ------------------------------------------------------------------ ChibiFixed

        [Test]
        public void MulPermille_RoundsHalfAwayFromZero()
        {
            Assert.AreEqual(1500, ChibiFixed.MulPermille(1000, 1500));
            Assert.AreEqual(1, ChibiFixed.MulPermille(1, 500));     //  0.5 ->  1
            Assert.AreEqual(-1, ChibiFixed.MulPermille(-1, 500));   // -0.5 -> -1
            Assert.AreEqual(2, ChibiFixed.MulPermille(3, 500));     //  1.5 ->  2
            Assert.AreEqual(-2, ChibiFixed.MulPermille(-3, 500));   // -1.5 -> -2
            Assert.AreEqual(0, ChibiFixed.MulPermille(1, 499));     //  0.499 -> 0
            Assert.AreEqual(0, ChibiFixed.MulPermille(-1, 499));
            Assert.AreEqual(-2, ChibiFixed.MulPermille(3, -500));
            Assert.AreEqual(0, ChibiFixed.MulPermille(7, 0));
        }

        [Test]
        public void ISqrt_Boundaries()
        {
            Assert.AreEqual(0L, ChibiFixed.ISqrt(0));
            Assert.AreEqual(1L, ChibiFixed.ISqrt(1));
            Assert.AreEqual(1L, ChibiFixed.ISqrt(2));
            Assert.AreEqual(1L, ChibiFixed.ISqrt(3));
            Assert.AreEqual(2L, ChibiFixed.ISqrt(4));
            Assert.AreEqual(3L, ChibiFixed.ISqrt(15));
            Assert.AreEqual(4L, ChibiFixed.ISqrt(16));
            Assert.AreEqual(999L, ChibiFixed.ISqrt(999999));
            Assert.AreEqual(1000L, ChibiFixed.ISqrt(1000000));
            const long big = 3037000499L;   // floor(sqrt(long.MaxValue))
            Assert.AreEqual(big, ChibiFixed.ISqrt(big * big));
            Assert.AreEqual(big - 1, ChibiFixed.ISqrt(big * big - 1));
            Assert.AreEqual(big, ChibiFixed.ISqrt(long.MaxValue));
        }

        [Test]
        public void DivRound_HandlesSigns()
        {
            Assert.AreEqual(4, ChibiFixed.DivRound(7, 2));
            Assert.AreEqual(-4, ChibiFixed.DivRound(-7, 2));
            Assert.AreEqual(-4, ChibiFixed.DivRound(7, -2));
            Assert.AreEqual(4, ChibiFixed.DivRound(-7, -2));
            Assert.AreEqual(2, ChibiFixed.DivRound(5, 3));
            Assert.AreEqual(-2, ChibiFixed.DivRound(-5, 3));
            Assert.AreEqual(1, ChibiFixed.DivRound(4, 3));
            Assert.AreEqual(-1, ChibiFixed.DivRound(-4, 3));
            Assert.AreEqual(0, ChibiFixed.DivRound(0, 5));
        }

        [Test]
        public void ClampSignAbs_Basic()
        {
            Assert.AreEqual(10, ChibiFixed.Clamp(12, 0, 10));
            Assert.AreEqual(0, ChibiFixed.Clamp(-3, 0, 10));
            Assert.AreEqual(5, ChibiFixed.Clamp(5, 0, 10));
            Assert.AreEqual(-1, ChibiFixed.Sign(-5));
            Assert.AreEqual(0, ChibiFixed.Sign(0));
            Assert.AreEqual(1, ChibiFixed.Sign(9));
            Assert.AreEqual(5, ChibiFixed.Abs(-5));
            Assert.AreEqual(25000000L, ChibiFixed.DistSq(3000, 4000));
        }

        [Test]
        public void CircleOverlapsAabb_InsideTouchingOutside()
        {
            Assert.IsTrue(ChibiFixed.CircleOverlapsAabb(0, 0, 100, -50, -50, 50, 50));     // centre inside
            Assert.IsTrue(ChibiFixed.CircleOverlapsAabb(0, 0, 1000, 1000, -500, 2000, 500)); // touching edge
            Assert.IsFalse(ChibiFixed.CircleOverlapsAabb(0, 0, 999, 1000, -500, 2000, 500));
            // corner: nearest point (3000, 4000) is exactly 5000 away
            Assert.IsTrue(ChibiFixed.CircleOverlapsAabb(0, 0, 5000, 3000, 4000, 6000, 8000));
            Assert.IsFalse(ChibiFixed.CircleOverlapsAabb(0, 0, 4999, 3000, 4000, 6000, 8000));
        }

        // ------------------------------------------------------------------ match config hash

        [Test]
        public void MatchConfig_SameConfig_SameHash()
        {
            Assert.AreEqual(Cfg().ComputeHash(), Cfg().ComputeHash());
        }

        [Test]
        public void MatchConfig_AnyFieldChange_ChangesHash()
        {
            uint baseHash = Cfg().ComputeHash();
            var c = Cfg(); c.Seed = Seed + 1;
            Assert.AreNotEqual(baseHash, c.ComputeHash(), "Seed");
            c = Cfg(); c.Class0 = FighterClass.Heavy;
            Assert.AreNotEqual(baseHash, c.ComputeHash(), "Class0");
            c = Cfg(); c.Class1 = FighterClass.Ranged;
            Assert.AreNotEqual(baseHash, c.ComputeHash(), "Class1");
            c = Cfg(); c.Passive0 = 1;
            Assert.AreNotEqual(baseHash, c.ComputeHash(), "Passive0");
            c = Cfg(); c.Passive1 = 0;
            Assert.AreNotEqual(baseHash, c.ComputeHash(), "Passive1");
        }

        // ------------------------------------------------------------------ tick clock

        [Test]
        public void TickClock_OneTickPerTickLengthFrame()
        {
            var clock = new ChibiTickClock();
            for (int i = 0; i < 10; i++)
                Assert.AreEqual(1, clock.Advance(ChibiTickClock.TickSeconds, 1f), "frame " + i);
        }

        [Test]
        public void TickClock_HugeDelta_CapsAndDropsBacklog()
        {
            var clock = new ChibiTickClock();
            Assert.AreEqual(ChibiTickClock.MaxTicksPerRenderFrame, clock.Advance(10f, 1f));
            Assert.AreEqual(0f, clock.RenderAlpha);
            Assert.AreEqual(0, clock.Advance(0f, 1f));
        }

        [Test]
        public void TickClock_Run_MatchesAdvance_AndRunsCallbacks()
        {
            var clock = new ChibiTickClock();
            int ticks = 0;
            int ran = clock.Run(10f, () => 1f, () => ticks++);
            Assert.AreEqual(ChibiTickClock.MaxTicksPerRenderFrame, ran);
            Assert.AreEqual(ran, ticks);
            Assert.AreEqual(0f, clock.RenderAlpha);
        }

        [Test]
        public void TickClock_Reset_ClearsAccumulator()
        {
            var clock = new ChibiTickClock();
            clock.Advance(ChibiTickClock.TickSeconds * 0.5f, 1f);
            Assert.Greater(clock.RenderAlpha, 0f);
            clock.Reset();
            Assert.AreEqual(0f, clock.RenderAlpha);
        }
    }
}
