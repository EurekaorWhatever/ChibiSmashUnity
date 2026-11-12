// Story type: Logic (match flow in the deterministic sim, port step 16) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/match-flow-conversion.md. Original: Game/MatchController.cs (MC), Fighter.cs RingOut /
// Revive (F:692-714), Hud.cs:47-53 (intro banner).
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Intro countdown, match clock, KO / timeout winners, training revive and ult fill (rules v16).</summary>
    public class ChibiMatchFlowTests
    {
        const uint Seed = 4242;
        static readonly ChibiInputPacket N = default;

        static ChibiSimState NewMatch(bool intro = false, bool training = false,
                                      FighterClass c0 = FighterClass.Melee, FighterClass c1 = FighterClass.Melee)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig
            {
                Seed = Seed, Class0 = c0, Class1 = c1, Stage = 0, Intro = intro, Training = training,
            });
            return s;
        }

        static ChibiInputPacket B(ChibiButtons b) => ChibiInputPacket.Make(ChibiDir.Neutral, b);

        static void Run(ChibiSimState s, int ticks)
        {
            for (int t = 0; t < ticks; t++) ChibiSimulation.Step(s, N, N);
        }

        // ================================================================== intro (MC:26, MC:121-127)

        [Test]
        public void Intro_LightOnRealTick131Ignored_LightOnTick132Starts()
        {
            var s = NewMatch(intro: true);
            Run(s, ChibiSimConst.IntroTicks - 1);
            var early = new ChibiSimState(); early.CopyFrom(s);
            ChibiSimulation.Step(early, B(ChibiButtons.Light), N);             // real tick 131
            Assert.AreEqual(-1, early.F[0].MoveSlot, "input is off for the whole 2.2 s intro");

            ChibiSimulation.Step(s, N, N);                                     // real tick 131 (idle)
            Assert.IsTrue(s.Running, "running after 132 real ticks");
            ChibiSimulation.Step(s, B(ChibiButtons.Light), N);                 // real tick 132
            Assert.AreEqual(0, s.F[0].MoveSlot, "first input tick");
        }

        [Test]
        public void Intro_UltPressDoesNotOpenWindow()
        {
            var s = NewMatch(intro: true);
            s.F[0].Ult = ChibiSimConst.UltMax;
            ChibiSimulation.Step(s, B(ChibiButtons.Ultimate), N);
            Assert.AreEqual(0, s.F[0].UltWindowTicks);
        }

        [Test]
        public void Intro_NoMatchClockAndNoKo()
        {
            // Rules v20: crossing a KO edge costs a stock, but only while running (MC:121-127 returns before the bounds check).
            var s = NewMatch(intro: true);
            s.F[1].X = 40000;                       // past the blast line during the intro (fixture injection)
            Run(s, 10);
            Assert.AreEqual(0, s.MatchClock, "the timer does not run in the intro");
            Assert.AreEqual(ChibiPercentConst.StocksStart, s.F[1].Stocks, "no KO while not running");
            Assert.AreEqual(0, s.F[1].OutTicks);
            Run(s, ChibiSimConst.IntroTicks);
            Assert.AreEqual(ChibiPercentConst.StocksStart - 1, s.F[1].Stocks, "the KO resolves once running");
            Assert.AreEqual(1, s.F[1].KosTaken);
        }

        [Test]
        public void MatchClock_CountsOnlyRunningGameSteps()
        {
            var s = NewMatch(intro: true);
            Run(s, ChibiSimConst.IntroTicks + 300);
            Assert.AreEqual(300, s.MatchClock);
            Assert.AreEqual(ChibiSimConst.MatchTicks - 300, s.TimeLeftTicks);
        }

        // ================================================================== timeout (rules v20: stocks, then percent, then draw)

        [Test]
        public void Timeout_EndsOnTheLastClockStep()
        {
            var s = NewMatch();
            s.MatchClock = ChibiSimConst.MatchTicks - 2;
            ChibiSimulation.Step(s, N, N);
            Assert.IsFalse(s.MatchOver, "one step left");
            ChibiSimulation.Step(s, N, N);
            Assert.IsTrue(s.MatchOver, "ends on the last clock step (300 s)");
            Assert.AreEqual(-1, s.Winner, "equal stocks and percent: draw");
        }

        [Test]
        public void Timeout_LowerPercentWins_ByOneMilliPercent()
        {
            var s = NewMatch();
            s.F[0].PctM = 40_000;
            s.F[1].PctM = 40_001;
            s.MatchClock = ChibiSimConst.MatchTicks - 1;
            ChibiSimulation.Step(s, N, N);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(0, s.Winner, "equal stocks: the lower percent wins");
        }

        [Test]
        public void Timeout_StocksBeatPercent_EqualIsDraw()
        {
            // percent-knockback.md AC 12.
            var s = NewMatch(c0: FighterClass.Heavy, c1: FighterClass.Ranged);
            s.F[0].Stocks = 2; s.F[0].PctM = 150_000;
            s.F[1].Stocks = 1; s.F[1].PctM = 0;
            Assert.AreEqual(0, ChibiSimulation.TimeoutWinner(s.F[0], s.F[1]), "(2, 150 %) beats (1, 0 %)");
            s.F[0].Stocks = 1; s.F[0].PctM = 40_000; s.F[1].PctM = 41_000;
            Assert.AreEqual(0, ChibiSimulation.TimeoutWinner(s.F[0], s.F[1]), "(1, 40 %) beats (1, 41 %)");
            s.F[1].PctM = 40_000;
            Assert.AreEqual(-1, ChibiSimulation.TimeoutWinner(s.F[0], s.F[1]), "(1, 40 %) vs (1, 40 %): draw");
        }

        // ================================================================== KO (rules v20 stocks, D-F1)

        [Test]
        public void KO_LastStock_OpponentWins()
        {
            var s = NewMatch();
            s.F[1].Stocks = 1;
            s.F[1].X = 40000;
            ChibiSimulation.Step(s, N, N);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(0, s.Winner);
            Assert.AreEqual(0, s.F[1].Stocks);
        }

        [Test]
        public void KO_BothLastStocksSameTick_Draw()
        {
            var s = NewMatch();
            s.F[0].Stocks = 1; s.F[0].X = -40000;
            s.F[1].Stocks = 1; s.F[1].X = 40000;
            ChibiSimulation.Step(s, N, N);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(-1, s.Winner, "D-F1: a same-tick double last-stock KO is a draw");
        }

        [Test]
        public void KO_MatchOver_StopsSimulating()
        {
            var s = NewMatch();
            s.F[1].Stocks = 1;
            s.F[1].X = 40000;
            ChibiSimulation.Step(s, N, N);
            uint h = s.ComputeChecksum();
            int g = s.GameTicks;
            ChibiSimulation.Step(s, B(ChibiButtons.Light), N);
            Assert.AreEqual(g, s.GameTicks, "no game steps after the end");
            Assert.AreEqual(-1, s.F[0].MoveSlot);
            Assert.AreNotEqual(h, s.ComputeChecksum(), "only Frame advances");
        }

        // ================================================================== training (MC:68, MC:129-132, rules v20)

        [Test]
        public void Training_KO_KeepsStocks_RespawnsAtZeroPercent()
        {
            var s = NewMatch(training: true);
            s.F[1].PctM = 120_000;
            s.F[1].X = 40000;
            ChibiSimulation.Step(s, N, N);
            Assert.IsFalse(s.MatchOver);
            Assert.AreEqual(ChibiPercentConst.StocksStart, s.F[1].Stocks, "training: no stock loss");
            Assert.AreEqual(1, s.F[1].KosTaken);
            Run(s, ChibiPercentConst.RespawnDelay);
            Assert.AreEqual(0, s.F[1].OutTicks, "respawned");
            Assert.AreEqual(0, s.F[1].PctM, "percent reset");
            Assert.AreEqual(ChibiStageTable.Get(0).RespawnY, s.F[1].Y, "on the respawn point");
        }

        [Test]
        public void Training_NoTimer_NeverTimesOut()
        {
            var s = NewMatch(training: true);
            Run(s, ChibiSimConst.MatchTicks + 60);
            Assert.IsFalse(s.MatchOver);
            Assert.AreEqual(0, s.MatchClock);
            Assert.AreEqual(0, s.TimeLeftTicks);
        }

        [Test]
        public void TrainingFillUlt_OnlyInRunningTraining()
        {
            var normal = NewMatch();
            Assert.IsFalse(ChibiSimulation.TrainingFillUlt(normal, 0), "not outside training");
            Assert.AreEqual(0, normal.F[0].Ult);

            var intro = NewMatch(intro: true, training: true);
            Assert.IsFalse(ChibiSimulation.TrainingFillUlt(intro, 0), "not during the intro (MC:129 needs Running)");

            var t = NewMatch(training: true);
            Assert.IsTrue(ChibiSimulation.TrainingFillUlt(t, 0));
            Assert.AreEqual(ChibiSimConst.UltMax, t.F[0].Ult);
        }

        // ================================================================== config / determinism

        [Test]
        public void ConfigHash_DiffersByIntroAndTraining()
        {
            var a = new ChibiMatchConfig { Seed = Seed };
            var b = a; b.Intro = true;
            var c = a; c.Training = true;
            Assert.AreNotEqual(a.ComputeHash(), b.ComputeHash());
            Assert.AreNotEqual(a.ComputeHash(), c.ComputeHash());
            Assert.GreaterOrEqual(ChibiMatchConfig.RulesVersion, 16);
        }

        [Test]
        public void IntroMatch_TwoRunsSameInputs_SameChecksumEveryTick()
        {
            var a = NewMatch(intro: true);
            var b = NewMatch(intro: true);
            var rng = new System.Random(5);
            for (int t = 0; t < ChibiSimConst.IntroTicks + 600; t++)
            {
                var p0 = new ChibiInputPacket { Raw = (ushort)rng.Next(0, 0x2000) }.Sanitized();
                var p1 = new ChibiInputPacket { Raw = (ushort)rng.Next(0, 0x2000) }.Sanitized();
                ChibiSimulation.Step(a, p0, p1);
                ChibiSimulation.Step(b, p0, p1);
                Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum(), "desync at tick " + t);
            }
        }
    }
}
