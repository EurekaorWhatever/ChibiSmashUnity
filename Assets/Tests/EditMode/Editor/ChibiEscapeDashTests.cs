// Story type: Logic (rules v24 escape dash, decision ED-1, 2026-11-17) -- evidence: automated EditMode unit test --
// gate: BLOCKING. A Dash press during a knockback launch, after 10 game ticks of it, with the dash cooldown ready,
// halves the launch velocity, ends the hitstun and starts the dash cooldown, so a launched player gets a chance to come
// back. Set knockback (weak hits) cannot be escaped. The CPU escapes a launch that would KO: Easy never, Normal on a
// 50 % roll per launch, Hard always.
//
// Ticks are game steps (ChibiSimState.GameTicks); hitstop real ticks do not count.
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    public class ChibiEscapeDashTests
    {
        static readonly ChibiInputPacket N = default;
        static ChibiInputPacket In(ChibiDir d, ChibiButtons b = ChibiButtons.None) => ChibiInputPacket.Make(d, b);
        static readonly ChibiInputPacket DashIn = In(ChibiDir.Neutral, ChibiButtons.Dash);

        static void Step(ChibiSimState s, ChibiInputPacket a, ChibiInputPacket b) => ChibiTestTime.StepGame(s, a, b);

        static uint NoCritSeed()
        {
            for (uint seed = 1; ; seed++) if (new ChibiRng(seed).Range(1000) >= 200) return seed;
        }

        /// <summary>Sky Dojo, Kaia (F[0]) at -1200 facing +1, Kaia (F[1]) at 0 facing -1, victim at <paramref name="pct"/> %.</summary>
        static ChibiSimState Duel(int pct, int stage = ChibiStageTable.SkyDojo)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig
            {
                Seed = NoCritSeed(), Class0 = FighterClass.Melee, Class1 = FighterClass.Melee, Stage = stage, HazardsOff = true,
            });
            for (int t = 0; t < 60; t++) ChibiSimulation.Step(s, N, N);
            MovesetKit.Place(s.F[0], -1200, 0, 1);
            MovesetKit.Place(s.F[1], 0, 0, -1);
            for (int t = 0; t < 3; t++) Step(s, N, N);
            s.Rng = new ChibiRng(NoCritSeed());
            s.F[1].PctM = pct * 1000;
            return s;
        }

        /// <summary>Uncharged Kaia forward smash (Heavy + E) or jab; steps until the victim's percent rises. Fails if it never lands.</summary>
        static void LandHit(ChibiSimState s, bool smash = true)
        {
            int pct = s.F[1].PctM;
            Step(s, smash ? In(ChibiDir.E, ChibiButtons.Heavy) : In(ChibiDir.Neutral, ChibiButtons.Light), N);
            for (int t = 0; t < 40 && s.F[1].PctM == pct; t++) Step(s, N, N);
            Assert.AreNotEqual(pct, s.F[1].PctM, "fixture: the hit lands");
        }

        /// <summary>Steps (victim idle) until the victim's LaunchAge reaches <paramref name="age"/>.</summary>
        static void AgeTo(ChibiSimState s, int age)
        {
            for (int t = 0; t < 60 && s.F[1].LaunchAge < age && s.F[1].Stun > 0; t++) Step(s, N, N);
            Assert.AreEqual(age, s.F[1].LaunchAge, "fixture: launch age reached");
        }

        /// <summary>Runs <paramref name="ticks"/> more steps with the victim holding <paramref name="hold"/>; true if it was KO'd.</summary>
        static bool KoWithin(ChibiSimState s, int ticks, ChibiInputPacket hold)
        {
            for (int t = 0; t < ticks && s.F[1].KosTaken == 0; t++) Step(s, N, hold);
            return s.F[1].KosTaken > 0;
        }

        // ================================================================== the rule

        [Test]
        public void SmashLaunch_StartsLaunchAge_AndCountsUp()
        {
            var s = Duel(200);
            LandHit(s);
            Assert.AreEqual(1, s.F[1].LaunchAge, "a knockback launch starts at age 1 on the hit step");
            Step(s, N, N);
            Assert.AreEqual(2, s.F[1].LaunchAge);
        }

        [Test]
        public void EscapeAfter10Ticks_HalvesVelocity_EndsStun_StartsDashCooldown()
        {
            var s = Duel(200);
            LandHit(s);
            AgeTo(s, ChibiPercentConst.EscapeMinAge);
            var before = new ChibiFighterSim();
            before.CopyFrom(s.F[1]);
            Step(s, N, DashIn);                          // age 11 this step: the escape fires
            var f = s.F[1];
            Assert.AreEqual(0, f.Stun, "hitstun ended");
            Assert.AreEqual(0, f.LaunchAge, "launch ended");
            Assert.AreEqual(1, f.EscapeDashes);
            Assert.AreEqual(ChibiSimConst.DashCooldown, f.DashCd, "dash cooldown started");
            Assert.AreEqual(0, f.DashTicks, "no dash motion");
            Assert.AreEqual(0, f.Invuln, "no invulnerability");
            Assert.Less(System.Math.Abs(f.Vx), System.Math.Abs(before.Vx) * 6 / 10, "launch speed X roughly halved");
            Assert.Greater(System.Math.Abs(f.Vx), System.Math.Abs(before.Vx) * 3 / 10, "launch speed X roughly halved, not stopped");
        }

        [Test]
        public void DashPress_AtAge10_IsIgnored()
        {
            var s = Duel(200);
            LandHit(s);
            AgeTo(s, ChibiPercentConst.EscapeMinAge - 1);
            Step(s, N, DashIn);                          // age 10 this step: too early
            Assert.Greater(s.F[1].Stun, 0, "still in hitstun");
            Assert.AreEqual(0, s.F[1].EscapeDashes);
            Assert.AreEqual(ChibiPercentConst.EscapeMinAge, s.F[1].LaunchAge);
        }

        [Test]
        public void DashPress_WhileDashCooldown_IsIgnored()
        {
            var s = Duel(200);
            LandHit(s);
            s.F[1].DashCd = 30;
            AgeTo(s, ChibiPercentConst.EscapeMinAge);
            Step(s, N, DashIn);
            Assert.Greater(s.F[1].Stun, 0, "the escape needs the dash cooldown ready");
            Assert.AreEqual(0, s.F[1].EscapeDashes);
        }

        [Test]
        public void WeakHit_Jab_CannotBeEscaped()
        {
            var s = Duel(0);
            LandHit(s, smash: false);
            Assert.Greater(s.F[1].Stun, 0, "fixture: the jab stuns");
            Assert.AreEqual(0, s.F[1].LaunchAge, "a weak hit (jab) is not an escapable launch");
            for (int t = 0; t < 12 && s.F[1].Stun > 0; t++) Step(s, N, N);
            if (s.F[1].Stun > 0)
            {
                Step(s, N, DashIn);
                Assert.AreEqual(0, s.F[1].EscapeDashes, "no escape out of a weak hit");
            }
            Assert.AreEqual(0, s.F[1].EscapeDashes);
        }

        // ================================================================== the point: a chance to come back

        [Test]
        public void Fsmash200_NoEscape_KOs()
        {
            var s = Duel(200);
            LandHit(s);
            Assert.IsTrue(KoWithin(s, 240, N), "baseline: an uncharged forward smash at 200 % KOs on Sky Dojo");
        }

        [Test]
        public void Fsmash200_EscapeAtAge11_Survives()
        {
            var s = Duel(200);
            LandHit(s);
            AgeTo(s, ChibiPercentConst.EscapeMinAge);
            Step(s, N, DashIn);
            Assert.AreEqual(1, s.F[1].EscapeDashes, "fixture: escaped");
            // No further input: the halved launch alone carries the fighter back down onto the stage.
            bool landed = false;
            for (int t = 0; t < 240 && s.F[1].KosTaken == 0; t++) { Step(s, N, N); landed |= s.F[1].Grounded; }
            Assert.AreEqual(0, s.F[1].KosTaken, "the escape dash saves the stock");
            Assert.IsTrue(landed, "back on the stage");
        }

        // ================================================================== state

        [Test]
        public void LaunchAge_And_EscapeDashes_AreCopied_AndHashed()
        {
            var s = Duel(200);
            LandHit(s);
            var c = new ChibiSimState();
            c.CopyFrom(s);
            Assert.AreEqual(s.F[1].LaunchAge, c.F[1].LaunchAge);
            Assert.AreEqual(s.ComputeChecksum(), c.ComputeChecksum());
            c.F[1].LaunchAge++;
            Assert.AreNotEqual(s.ComputeChecksum(), c.ComputeChecksum(), "LaunchAge is in the checksum");
            c.CopyFrom(s);
            c.F[1].EscapeDashes++;
            Assert.AreNotEqual(s.ComputeChecksum(), c.ComputeChecksum(), "EscapeDashes is in the checksum");
        }

        [Test]
        public void ConfigHash_HasRulesVersion25()
        {
            // Rules v24 introduced the escape dash; v25 (team play) keeps it. Pinned so a later bump revisits this file.
            Assert.AreEqual(25, ChibiMatchConfig.RulesVersion);
        }

        // ================================================================== CPU

        [Test]
        public void Cpu_LaunchWouldKo_TrueAt200_FalseAt50()
        {
            var st = ChibiStageTable.Get(ChibiStageTable.SkyDojo);
            var hi = Duel(200); LandHit(hi);
            Assert.IsTrue(ChibiCpuInputSource.LaunchWouldKo(hi.F[1], st), "200 % forward smash launch is a KO");
            var lo = Duel(50); LandHit(lo);
            Assert.IsFalse(ChibiCpuInputSource.LaunchWouldKo(lo.F[1], st), "50 % forward smash launch is not a KO");
        }

        /// <summary>Victim F[1] is a CPU of <paramref name="difficulty"/>; returns how many escape dashes it used on a 200 % launch.</summary>
        static int CpuEscapes(int difficulty, uint seed)
        {
            var s = Duel(200);
            var cpu = new ChibiCpuInputSource(1, difficulty, seed);
            cpu.Bind(s);
            LandHit(s);
            for (int t = 0; t < 40 && s.F[1].Stun > 0 && s.F[1].KosTaken == 0; t++) Step(s, N, cpu.SampleTick());
            return s.F[1].EscapeDashes;
        }

        [Test]
        public void Cpu_Hard_AlwaysEscapes_Easy_Never()
        {
            for (uint seed = 1; seed <= 8; seed++)
            {
                Assert.AreEqual(1, CpuEscapes(3, seed), $"Hard escapes (seed {seed})");
                Assert.AreEqual(0, CpuEscapes(1, seed), $"Easy never escapes (seed {seed})");
            }
        }

        [Test]
        public void Cpu_Normal_EscapesSometimes()
        {
            int used = 0;
            for (uint seed = 1; seed <= 40; seed++) used += CpuEscapes(2, seed);
            Assert.That(used, Is.InRange(8, 32), "Normal escapes about half of the KO launches");
        }
    }
}
