// Story type: Logic (rules v23 balance pass, review 2026-11-16 batch 4: items 25 T1/T2, 27, 28) -- evidence: automated
// EditMode unit test -- gate: BLOCKING. User decisions B4-1..B4-3 (2026-11-17, vault Decisions Log).
//
// Ticks are game steps (ChibiSimState.GameTicks); hitstop real ticks do not count.
using System;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Rules v23: no trip loops, Bruna's down smash sends sideways, walk cancel only on a connected hit, dash attack buffer.</summary>
    public class ChibiBalanceV23Tests
    {
        const uint Seed = 2323;
        const int Settle = 90;
        static readonly ChibiInputPacket N = default;

        static ChibiInputPacket In(ChibiDir d, ChibiButtons b = ChibiButtons.None) => ChibiInputPacket.Make(d, b);

        static void Place(ChibiFighterSim f, int x, int facing)
        {
            f.X = x; f.Y = 0; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = true; f.Facing = facing;
            f.Stun = 0; f.Invuln = 0; f.MoveSlot = -1; f.MoveLockEnd = 0; f.DashTicks = 0; f.PctM = 0;
        }

        /// <summary>Sky Dojo, both fighters settled; F[0] at <paramref name="x0"/> facing +1, F[1] at <paramref name="x1"/> facing -1.</summary>
        static ChibiSimState Arena(FighterClass c0, FighterClass c1, int x0, int x1)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = Seed, Class0 = c0, Class1 = c1, Stage = ChibiStageTable.SkyDojo, HazardsOff = true });
            for (int t = 0; t < Settle; t++) ChibiSimulation.Step(s, N, N);
            Place(s.F[0], x0, 1);
            Place(s.F[1], x1, -1);
            for (int t = 0; t < 3; t++) ChibiSimulation.Step(s, N, N);
            return s;
        }

        /// <summary>Steps until <paramref name="until"/> holds or <paramref name="max"/> steps; returns whether it held.</summary>
        static bool StepUntil(ChibiSimState s, Func<ChibiSimState, bool> until, int max, ChibiInputPacket a = default, ChibiInputPacket b = default)
        {
            for (int t = 0; t < max; t++)
            {
                if (until(s)) return true;
                ChibiSimulation.Step(s, a, b);
            }
            return until(s);
        }

        // ================================================================== T1: trips leave no loop

        /// <summary>
        /// One trip on a grounded victim. Returns the game tick the victim can act again (stun over) and the tick the attacker
        /// can start the same trip again (move, dash and cancel lock over).
        /// </summary>
        static void Trip(FighterClass c, bool fromDash, out int victimFree, out int attackerFree, out int startup)
        {
            var s = Arena(c, FighterClass.Melee, 0, fromDash ? 2600 : 900);
            var a = s.F[0];
            var v = s.F[1];
            int slot;
            if (fromDash)
            {
                ChibiSimulation.Step(s, In(ChibiDir.E, ChibiButtons.Dash), N);
                ChibiSimulation.Step(s, N, N);
                ChibiSimulation.Step(s, In(ChibiDir.S, ChibiButtons.Light), N);
                slot = ChibiMovesetData.DashDownSlot[(int)c];
            }
            else
            {
                ChibiSimulation.Step(s, In(ChibiDir.S, ChibiButtons.Light), N);
                slot = ChibiMovesetData.SlotFor((int)c, 0, 4, false);
            }
            Assert.AreEqual(slot, a.MoveSlot, $"{c}: precondition, the trip row started");
            startup = ChibiMoveTable.Get((int)c, slot).Startup;
            Assert.IsTrue(StepUntil(s, x => x.F[1].Stun > 0, 60), $"{c}: precondition, the trip connected");
            Assert.IsTrue(v.Grounded || v.Y < 400, $"{c}: precondition, a grounded trip");
            victimFree = attackerFree = -1;
            for (int t = 0; t < 120 && (victimFree < 0 || attackerFree < 0); t++)
            {
                ChibiSimulation.Step(s, N, N);
                if (victimFree < 0 && v.Stun == 0) victimFree = s.GameTicks;
                if (attackerFree < 0 && a.MoveSlot < 0 && a.AttackLockTicks == 0 && a.DashTicks == 0) attackerFree = s.GameTicks;
            }
        }

        [TestCase(FighterClass.Heavy)] [TestCase(FighterClass.Ranged)]
        public void DownTiltTrip_VictimActsBeforeTheSameTripCanHitAgain(FighterClass c)
        {
            Trip(c, false, out int victimFree, out int attackerFree, out int startup);
            int nextHit = attackerFree + startup - 1;                 // first active tick of an immediate second down tilt
            Assert.LessOrEqual(victimFree, nextHit, $"{c}: victim free at {victimFree}, a second trip lands at {nextHit}: a loop");
        }

        [Test]
        public void KaiaLowSweep_VictimActsBeforeADashSweepCanHitAgain()
        {
            Trip(FighterClass.Melee, true, out int victimFree, out int attackerFree, out int startup);
            int nextHit = attackerFree + 1 + startup - 1;             // one dash tick, then the sweep
            Assert.LessOrEqual(victimFree, nextHit, $"victim free at {victimFree}, a dash sweep lands at {nextHit}: a loop");
        }

        // ================================================================== T2: Bruna's down smash sends sideways

        [Test]
        public void BrunaDownSmash_LaunchesSideways_NotStraightUp()
        {
            var s = Arena(FighterClass.Heavy, FighterClass.Magic, 0, 1200);
            s.F[1].PctM = 100_000;
            ChibiSimulation.Step(s, In(ChibiDir.S, ChibiButtons.Heavy), N);
            Assert.AreEqual(ChibiMovesetData.SlotFor((int)FighterClass.Heavy, 1, 4, false), s.F[0].MoveSlot, "precondition: down smash");
            Assert.IsTrue(StepUntil(s, x => x.F[1].Stun > 0, 80), "precondition: it connected");
            var v = s.F[1];
            Assert.Greater(Math.Abs((long)v.Vx) * 4, Math.Abs((long)v.Vy) * 3,
                $"launch ({v.Vx}, {v.Vy}) should be at most ~53 degrees up, like the other down smashes");
        }

        // ================================================================== item 27: "on hit" = landed or blocked

        static ChibiSimState HeavyInto(bool victimInvulnerable, out int lockEnd, bool victimShields = false)
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee, 0, 900);
            if (victimInvulnerable) s.F[1].Invuln = 300;
            // A shield raised well before the attack is past the perfect-parry window (a parry would also end the move).
            if (victimShields)
                for (int t = 0; t < ChibiSimConst.ParryWindow + 10; t++) ChibiSimulation.Step(s, N, In(ChibiDir.Neutral, ChibiButtons.Shield));
            ChibiSimulation.Step(s, In(ChibiDir.Neutral, ChibiButtons.Heavy), victimShields ? In(ChibiDir.Neutral, ChibiButtons.Shield) : N);
            Assert.AreEqual(1, s.F[0].MoveSlot, "precondition: neutral heavy");
            lockEnd = s.F[0].MoveLockEnd;
            return s;
        }

        [Test]
        public void HeavyThatOnlyTouchedAnInvulnerableTarget_IsNotWalkCancelled()
        {
            var s = HeavyInto(true, out int lockEnd);
            var f = s.F[0];
            bool touched = false;
            for (int t = 1; t < lockEnd; t++)
            {
                ChibiSimulation.Step(s, t >= lockEnd - 5 ? In(ChibiDir.W) : N, N);
                touched |= f.MoveHit;
                Assert.AreEqual(1, f.MoveSlot, $"a heavy that only met invulnerability keeps its full recovery (tick {t})");
            }
            Assert.IsTrue(touched, "precondition: the hitbox overlapped the invulnerable target");
            Assert.AreEqual(0, s.F[1].PctM, "precondition: no damage was dealt");
        }

        [Test]
        public void HeavyBlockedByShield_IsStillWalkCancelled()
        {
            var s = HeavyInto(false, out int lockEnd, victimShields: true);
            var f = s.F[0];
            var shield = In(ChibiDir.Neutral, ChibiButtons.Shield);
            Assert.IsTrue(StepUntil(s, x => x.F[0].MoveConnected, 30, N, shield), "precondition: the heavy met the shield");
            Assert.IsTrue(s.F[1].Shielding && s.F[0].Stun == 0, "precondition: a block, not a perfect parry");
            bool cancelled = false;
            for (int t = 0; t < 60 && f.MoveSlot == 1; t++)
            {
                bool late = f.MoveLockEnd - f.MoveTick <= ChibiSimConst.MoveCancelTicks - 1;
                ChibiSimulation.Step(s, late ? In(ChibiDir.W) : N, shield);
                if (f.MoveSlot == -1) cancelled = f.AttackLockTicks > 0;   // a cancel leaves the old lock as AttackLockTicks
            }
            Assert.IsTrue(cancelled, "a blocked heavy counts as a hit for the walk cancel (decision B4-2)");
        }

        // ================================================================== item 28: attacks buffered during a dash

        [Test]
        public void HeavyPressedLateInADash_ComesOutWhenTheDashEnds()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee, -6000, 9000);
            var f = s.F[0];
            ChibiSimulation.Step(s, In(ChibiDir.E, ChibiButtons.Dash), N);
            Assert.Greater(f.DashTicks, 0, "precondition: dashing");
            Assert.IsTrue(StepUntil(s, x => x.F[0].DashTicks <= ChibiSimConst.AttackBufferTicks, 10), "precondition: late in the dash");
            Assert.AreEqual(ChibiSimConst.AttackBufferTicks, f.DashTicks, "precondition: exactly at the buffer window's edge");
            ChibiSimulation.Step(s, In(ChibiDir.Neutral, ChibiButtons.Heavy), N);
            Assert.AreEqual(-1, f.MoveSlot, "no attack during the dash itself");
            bool started = StepUntil(s, x => x.F[0].MoveSlot >= 0, 10);
            Assert.IsTrue(started, "the heavy pressed during the dash starts once the dash ends");
            Assert.AreEqual(1, f.MoveSlot, "it is the neutral heavy");
        }

        [Test]
        public void HeavyPressedEarlyInADash_IsStillDropped()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee, -6000, 9000);
            var f = s.F[0];
            ChibiSimulation.Step(s, In(ChibiDir.E, ChibiButtons.Dash), N);
            Assert.Greater(f.DashTicks, ChibiSimConst.AttackBufferTicks, "precondition: early in the dash");
            ChibiSimulation.Step(s, In(ChibiDir.Neutral, ChibiButtons.Heavy), N);
            Assert.IsFalse(StepUntil(s, x => x.F[0].MoveSlot >= 0, 14), "a press older than the buffer window is not replayed");
        }
    }
}
