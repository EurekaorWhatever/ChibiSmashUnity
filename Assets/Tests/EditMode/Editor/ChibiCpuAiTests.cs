// Story type: Logic (CPU opponent as a deterministic input source, port step 17) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/cpu-ai-conversion.md. Original: Fighter/FighterInput.cs CpuInput (FI:138-228).
using System;
using System.Collections.Generic;
using System.IO;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiCpuInputSource: dummy, determinism, behaviour (approach, space, attack, recover, ult gesture), replays.</summary>
    public class ChibiCpuAiTests
    {
        const uint Seed = 777;

        static ChibiMatchConfig Cfg(FighterClass c0, FighterClass c1, uint seed = Seed, int stage = 0, bool intro = false) =>
            new ChibiMatchConfig { Seed = seed, Class0 = c0, Class1 = c1, Stage = stage, Intro = intro };

        static readonly ChibiScriptedInputSource Idle = new ChibiScriptedInputSource(_ => default);

        /// <summary>Human-less session: P0 idle (or scripted), P1 the CPU. Records every packet the CPU sent.</summary>
        sealed class Rig
        {
            public ChibiLocalSession Session;
            public ChibiCpuInputSource Cpu;
            public readonly List<ChibiInputPacket> Sent = new List<ChibiInputPacket>();

            public Rig(ChibiMatchConfig cfg, int difficulty, IChibiInputSource p0 = null)
            {
                Cpu = new ChibiCpuInputSource(1, difficulty, cfg.Seed);
                Session = new ChibiLocalSession(cfg, p0 ?? new ChibiScriptedInputSource(_ => default), Cpu);
                Session.OnFrame += (f, a, b) => Sent.Add(b);
            }

            public ChibiSimState S => Session.State;
            public void Run(int ticks) { for (int t = 0; t < ticks && Session.Tick(); t++) { } }
        }

        const ChibiButtons AttackButtons = ChibiButtons.Light | ChibiButtons.Heavy | ChibiButtons.Special |
                                           ChibiButtons.Skill1 | ChibiButtons.Skill2 | ChibiButtons.Skill3;

        static void Place(ChibiFighterSim f, int x, int facing)
        {
            f.X = x; f.Y = 0; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = true; f.Facing = facing;
        }

        // ================================================================== difficulty 0

        [Test]
        public void Difficulty0_IsStillDummy_AlwaysNeutral()
        {
            var r = new Rig(Cfg(FighterClass.Melee, FighterClass.Heavy), 0);
            r.S.F[1].Ult = ChibiSimConst.UltMax;
            r.Run(1200);
            foreach (var p in r.Sent) Assert.AreEqual(0, p.Raw, "difficulty 0 never presses anything (FI:156, MC:92)");
        }

        // ================================================================== determinism

        [Test]
        public void SameSeed_SameInputStreamAndFinalState()
        {
            var a = new Rig(Cfg(FighterClass.Polearm, FighterClass.Magic), 3, new ChibiCpuInputSource(0, 2, Seed));
            var b = new Rig(Cfg(FighterClass.Polearm, FighterClass.Magic), 3, new ChibiCpuInputSource(0, 2, Seed));
            a.Run(3000); b.Run(3000);
            CollectionAssert.AreEqual(a.Sent, b.Sent);
            Assert.AreEqual(a.S.ComputeChecksum(), b.S.ComputeChecksum());
        }

        [Test]
        public void DifferentSeed_DifferentInputStream()
        {
            var a = new Rig(Cfg(FighterClass.Melee, FighterClass.Melee, 1), 3);
            var b = new Rig(Cfg(FighterClass.Melee, FighterClass.Melee, 2), 3);
            a.Run(1500); b.Run(1500);
            CollectionAssert.AreNotEqual(a.Sent, b.Sent);
        }

        [Test]
        public void Sampling_NeverTouchesSimStateOrSimRng()
        {
            var cfg = Cfg(FighterClass.Ranged, FighterClass.Ranged);
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            var cpu = new ChibiCpuInputSource(1, 3, cfg.Seed);
            cpu.Bind(s);
            uint rng = s.Rng.State, sum = s.ComputeChecksum(), cpuRng0 = cpu.RngState;
            for (int t = 0; t < 500; t++) cpu.SampleTick();
            Assert.AreEqual(rng, s.Rng.State, "the CPU owns its RNG");
            Assert.AreEqual(sum, s.ComputeChecksum(), "the CPU never writes the state");
            Assert.AreNotEqual(cpuRng0, cpu.RngState, "precondition: the CPU did draw numbers");
        }

        [Test]
        public void CpuVsCpu_FullMatch_ReplayReproducesFinalState()
        {
            var cfg = Cfg(FighterClass.Heavy, FighterClass.Polearm, 99, stage: 1, intro: true);
            var rec = new ChibiReplayRecorder(cfg);
            var s = new ChibiLocalSession(cfg, new ChibiCpuInputSource(0, 3, cfg.Seed), new ChibiCpuInputSource(1, 3, cfg.Seed));
            s.OnFrame += rec.Add;
            int ticks = 0;
            while (s.Tick() && ticks < ChibiSimConst.MatchTicks * 3) ticks++;
            Assert.IsTrue(s.State.MatchOver, "a CPU vs CPU match ends (KO or time-out)");

            var ms = new MemoryStream();
            ChibiReplay.Write(ms, cfg, rec.P0, rec.P1);
            ms.Position = 0;
            Assert.IsTrue(ChibiReplay.Verify(ms), "replay v3 (with Intro) re-simulates to the recorded final state");
            Assert.AreEqual(s.State.ComputeChecksum(), ChibiReplay.Simulate(cfg, rec.P0, rec.P1).ComputeChecksum());
        }

        // ================================================================== behaviour (FI:173-208)

        [Test]
        public void Melee_Far_ApproachesOpponent()
        {
            var r = new Rig(Cfg(FighterClass.Melee, FighterClass.Melee), 2);
            r.Run(90);
            Place(r.S.F[0], -6000, 1); Place(r.S.F[1], 6000, -1);
            r.Sent.Clear();
            r.Run(30);
            int toward = 0, away = 0;
            foreach (var p in r.Sent) { if (p.MoveX == -1) toward++; else if (p.MoveX == 1) away++; }
            Assert.Greater(toward, 15, "dist > reach x 0.8 + 0.4: walk toward the opponent (after its next think)");
            Assert.AreEqual(0, away, "never walks away from a far opponent");
        }

        [Test]
        public void Ranged_TooClose_BacksOff()
        {
            var r = new Rig(Cfg(FighterClass.Melee, FighterClass.Ranged), 2);
            r.Run(90);
            Place(r.S.F[0], 0, 1); Place(r.S.F[1], 1000, -1);
            r.Sent.Clear();
            r.Run(20);
            int away = 0;
            foreach (var p in r.Sent) if (p.MoveX == 1) away++;
            Assert.Greater(away, 8, "ranged: dist < 4.5 - 1.5 = 3: step away");
        }

        [Test]
        public void Melee_InRange_AttacksWithLightOrHeavy()
        {
            var r = new Rig(Cfg(FighterClass.Melee, FighterClass.Melee), 3);
            r.Run(90);
            int attacks = 0;
            for (int t = 0; t < 300; t++)
            {
                Place(r.S.F[0], 0, 1);
                r.S.F[1].X = 1000; r.S.F[1].Facing = -1;
                r.Run(1);
                var p = r.Sent[r.Sent.Count - 1];
                // Rules v18: close range also picks specials and Skill+Up/Down techniques (ChibiCpuInputSource.Options).
                if ((p.Raw & (ushort)AttackButtons) != 0) attacks++;
            }
            Assert.Greater(attacks, 5, "inMelee and facing: attack chance (6% + 12%·skill) per tick");
        }

        [Test]
        public void OffStage_RecoversTowardCentreAndJumps()
        {
            var r = new Rig(Cfg(FighterClass.Melee, FighterClass.Melee), 2);
            r.Run(90);
            var f = r.S.F[1];
            // Facing the stage: in the original the stick toward the centre turned the fighter, so FI:168's thrust always
            // went back; rules v18 lock facing in the air (OD-1), and the port thrusts only when it faces the stage.
            f.X = 9500; f.Y = -2000; f.Vy = -60000; f.Grounded = false; f.Facing = -1;
            r.S.HitstopRealTicks = 0;
            r.Run(1);
            var p = r.Sent[r.Sent.Count - 1];
            Assert.AreEqual(-1, p.MoveX, "move back toward the centre (FI:166)");
            Assert.IsTrue(p.Has(ChibiButtons.Jump), "jump while falling (FI:167)");
            Assert.IsTrue(p.Has(ChibiButtons.Skill1), "Melee uses Dash & Thrust to recover when falling fast (FI:168)");
        }

        [Test]
        public void OffStage_FacingAway_NoDashAndThrustOffTheStage()
        {
            var r = new Rig(Cfg(FighterClass.Melee, FighterClass.Melee), 2);
            r.Run(90);
            var f = r.S.F[1];
            f.X = 9500; f.Y = -2000; f.Vy = -60000; f.Grounded = false; f.Facing = 1;   // air facing locked outward (OD-1)
            r.S.HitstopRealTicks = 0;
            r.Run(1);
            var p = r.Sent[r.Sent.Count - 1];
            Assert.AreEqual(-1, p.MoveX, "still drifts toward the centre");
            Assert.IsFalse(p.Has(ChibiButtons.Skill1), "Dash & Thrust goes along Facing: pressed now it would carry the CPU away");
        }

        [Test]
        public void Presses_AlwaysReleaseBetweenTicks()
        {
            var r = new Rig(Cfg(FighterClass.Magic, FighterClass.Melee), 3, new ChibiCpuInputSource(0, 3, Seed));
            r.Run(4000);
            // Tap buttons are never held two ticks (two presses would merge into one). Heavy / Special / Skill3 may be held
            // on purpose: that is ONE press charging a smash / Focus Shot / Charged Cross Cut or holding a stance
            // (rules v18 charge / hold read the level), bounded by MaxHoldTicks. See ChibiCpuMovesetTests for every class.
            const ushort pressMask = (ushort)(ChibiButtons.Light | ChibiButtons.Jump | ChibiButtons.Skill1 |
                                              ChibiButtons.Skill2 | ChibiButtons.Ultimate | ChibiButtons.Dash);
            for (int i = 1; i < r.Sent.Count; i++)
                Assert.AreEqual(0, r.Sent[i - 1].Raw & r.Sent[i].Raw & pressMask, "a press bit held two ticks would be one press");
            int run = 0;
            foreach (var p in r.Sent)
            {
                run = (p.Raw & (ushort)(ChibiButtons.Heavy | ChibiButtons.Special | ChibiButtons.Skill3)) != 0 ? run + 1 : 0;
                Assert.LessOrEqual(run, ChibiCpuInputSource.MaxHoldTicks + 1, "a deliberate charge hold is bounded");
            }
        }

        // ================================================================== ult gesture (FI:207, FI:210-227)

        [TestCase(FighterClass.Melee)] [TestCase(FighterClass.Heavy)] [TestCase(FighterClass.Ranged)]
        [TestCase(FighterClass.Magic)] [TestCase(FighterClass.Polearm)]
        public void FullMeter_PressesUltKey_FiresMostOfTheTime(FighterClass c)
        {
            int fired = 0;
            const int runs = 10;
            for (int k = 0; k < runs; k++)
            {
                var r = new Rig(Cfg(FighterClass.Melee, c, (uint)(100 + k)), 3);
                r.Run(60);
                r.S.F[0].Invuln = 1 << 20;   // keep the CPU from being interrupted by nothing; P0 is idle anyway
                r.S.F[1].Ult = ChibiSimConst.UltMax;
                for (int t = 0; t < 1200 && r.S.F[1].UltsUsed == 0; t++)
                {
                    r.Run(1);
                    if (r.S.F[1].Ult < ChibiSimConst.UltMax && r.S.F[1].UltsUsed == 0) r.S.F[1].Ult = ChibiSimConst.UltMax;
                }
                if (r.S.F[1].UltsUsed > 0)
                {
                    fired++;
                    bool sawKey = false;
                    foreach (var p in r.Sent) if (p.Has(ChibiButtons.Ultimate)) sawKey = true;
                    Assert.IsTrue(sawKey, "rules v22: the ult fired through the Ult key (no gesture needed)");
                }
            }
            // Difficulty 3: the per-tick press chance is scaled by 96 %, so nearly every run fires within 20 s.
            Assert.GreaterOrEqual(fired, 8, $"{c}: CPU fired its ult in {fired}/{runs} runs");
        }
    }
}
