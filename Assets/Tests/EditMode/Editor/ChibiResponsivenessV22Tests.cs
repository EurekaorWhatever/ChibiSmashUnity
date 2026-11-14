// Story type: Logic (rules v22 responsiveness pass: attack buffer, walk / turn cancel, buffered turnaround) -- evidence:
// automated EditMode unit test -- gate: BLOCKING. User decisions UL-1 / EL-1 / TN-1 (2026-11-14, vault Decisions Log).
//
// Tick convention: one ChibiSimulation.Step = one game step (no hitstop pending). Tick 0 is the first press. A move started
// on tick 0 with lock L (= startup + active + recovery) is over at the end of tick L and actions are free on tick L + 1.
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Rules v22: attack buffer, grounded walk / turn cancel, and turning toward the held stick on a buffered attack.</summary>
    public class ChibiResponsivenessV22Tests
    {
        const uint Seed = 4242;
        const int Settle = 90;
        static readonly ChibiInputPacket N = default;

        /// <summary>Both fighters landed on Sky Dojo far apart (no hits), F[0] facing +1.</summary>
        static ChibiSimState FarArena(FighterClass c0)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = Seed, Class0 = c0, Class1 = FighterClass.Melee, Stage = 0 });
            for (int t = 0; t < Settle; t++) ChibiSimulation.Step(s, N, N);
            Place(s.F[0], -6000, 1);
            Place(s.F[1], 9000, -1);
            for (int t = 0; t < 3; t++) ChibiSimulation.Step(s, N, N);
            return s;
        }

        static void Place(ChibiFighterSim f, int x, int facing)
        {
            f.X = x; f.Y = 0; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = true; f.Facing = facing;
            f.Stun = 0; f.Invuln = 0;
        }

        static ChibiInputPacket In(ChibiDir d, ChibiButtons b = ChibiButtons.None) => ChibiInputPacket.Make(d, b);

        static void Step(ChibiSimState s, ChibiInputPacket a) => ChibiSimulation.Step(s, a, N);

        [Test]
        public void Constants_AreSixTicks()
        {
            Assert.AreEqual(6, ChibiSimConst.AttackBufferTicks);
            Assert.AreEqual(6, ChibiSimConst.MoveCancelTicks);
            Assert.GreaterOrEqual(ChibiMatchConfig.RulesVersion, 22);
        }

        [Test]
        public void TurnCancel_LastRecoveryTicks_EndsTheLightAndTurns_AttackWaitsForTheOldLockEnd()
        {
            var s = FarArena(FighterClass.Melee);
            Step(s, In(ChibiDir.Neutral, ChibiButtons.Light));                 // tick 0: neutral light (slot 0)
            var f = s.F[0];
            Assert.AreEqual(0, f.MoveSlot);
            int lockEnd = f.MoveLockEnd;
            int cancelTick = lockEnd - 2;                                     // 3 lock ticks left: recovery, inside the 6-tick window
            for (int t = 1; t < cancelTick; t++) Step(s, N);
            Assert.AreEqual(0, f.MoveSlot, "still in the light before the turn");

            Step(s, In(ChibiDir.W));                                          // hold back: turn cancel
            Assert.AreEqual(-1, f.MoveSlot, $"the light is cancelled on tick {cancelTick}");
            Assert.AreEqual(-1, f.Facing, "the fighter turned on the cancel tick");
            Assert.Greater(f.AttackLockTicks, 0, "attacks stay locked until the old lock end");

            Step(s, In(ChibiDir.W, ChibiButtons.Light));                      // press during the lock: buffered
            for (int t = cancelTick + 2; t <= lockEnd; t++)
            {
                Step(s, In(ChibiDir.W));
                Assert.AreEqual(-1, f.MoveSlot, $"no attack before the old lock end (tick {t})");
            }
            Step(s, In(ChibiDir.W));                                          // tick lockEnd + 1: free
            Assert.AreNotEqual(-1, f.MoveSlot, "the buffered light starts on the first free tick");
            Assert.AreEqual(-1, f.Facing);
        }

        [Test]
        public void TurnCancel_TooEarly_DoesNotCancel()
        {
            var s = FarArena(FighterClass.Melee);
            Step(s, In(ChibiDir.Neutral, ChibiButtons.Light));
            var f = s.F[0];
            int lockEnd = f.MoveLockEnd;
            for (int t = 1; t < lockEnd - 6; t++) Step(s, N);
            Step(s, In(ChibiDir.W));                                          // 7 lock ticks left: outside the window
            Assert.AreEqual(0, f.MoveSlot, "a turn 7 ticks before the end does not cancel");
            Assert.AreEqual(1, f.Facing, "no turn while the move runs");
        }

        [Test]
        public void HeavyWhiff_IsNotCancelled_OnlyHitsCancel()
        {
            var s = FarArena(FighterClass.Melee);
            Step(s, In(ChibiDir.Neutral, ChibiButtons.Heavy));                 // tick 0: neutral heavy (slot 1), whiffs
            var f = s.F[0];
            Assert.AreEqual(1, f.MoveSlot);
            int lockEnd = f.MoveLockEnd;
            for (int t = 1; t < lockEnd; t++)
            {
                Step(s, t >= lockEnd - 5 ? In(ChibiDir.W) : N);
                Assert.AreEqual(1, f.MoveSlot, $"a whiffed heavy keeps its full recovery (tick {t})");
            }
        }

        [Test]
        public void AirborneMove_IsNotCancelled()
        {
            var s = FarArena(FighterClass.Melee);
            var f = s.F[0];
            Step(s, In(ChibiDir.Neutral, ChibiButtons.Jump));
            for (int t = 0; t < 4; t++) Step(s, N);
            Assert.IsFalse(f.Grounded, "precondition: airborne");
            Step(s, In(ChibiDir.Neutral, ChibiButtons.Light));                 // neutral air
            Assert.AreNotEqual(-1, f.MoveSlot);
            int slot = f.MoveSlot, lockEnd = f.MoveLockEnd;
            for (int t = 1; t < lockEnd && !f.Grounded; t++)
            {
                Step(s, In(ChibiDir.W));
                if (!f.Grounded) Assert.AreEqual(slot, f.MoveSlot, $"no walk cancel in the air (tick {t})");
            }
        }

        [Test]
        public void BufferedLight_WithStickBack_TurnsAndAttacksTheOtherWay()
        {
            // A whiffed neutral heavy cannot be walk-cancelled, so this isolates the buffer: the Light pressed with the stick
            // back near the end is replayed on the first free tick and FreeActions turns the fighter before resolving it.
            var s = FarArena(FighterClass.Melee);
            Step(s, In(ChibiDir.Neutral, ChibiButtons.Heavy));
            var f = s.F[0];
            int lockEnd = f.MoveLockEnd;
            for (int t = 1; t < lockEnd - 1; t++) Step(s, N);
            Step(s, In(ChibiDir.W, ChibiButtons.Light));                      // 2 lock ticks left: buffered
            Assert.AreEqual(1, f.MoveSlot, "still in the heavy (a whiff is not cancelled)");
            Assert.AreEqual(1, f.Facing, "no turn while the heavy runs");
            // The heavy ends at the end of tick lockEnd (slot -1 there); the buffered light replays on tick lockEnd + 1.
            for (int t = 0; t < 4 && (f.MoveSlot == 1 || f.MoveSlot == -1); t++) Step(s, In(ChibiDir.W));
            Assert.AreNotEqual(-1, f.MoveSlot, "the buffered light started on the first free tick");
            Assert.AreNotEqual(1, f.MoveSlot, "a new move, not the heavy");
            Assert.AreEqual(-1, f.Facing, "it turned toward the held stick before attacking");
        }

        [Test]
        public void Buffer_IsInTheStateCopyAndChecksum()
        {
            var a = FarArena(FighterClass.Melee);
            a.F[0].BufferedEdges = (int)ChibiButtons.Light; a.F[0].BufferTicks = 3; a.F[0].AttackLockTicks = 2;
            var b = new ChibiSimState(); b.CopyFrom(a);
            Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum(), "CopyFrom carries the v22 fields");
            b.F[0].BufferTicks = 4;
            Assert.AreNotEqual(a.ComputeChecksum(), b.ComputeChecksum(), "BufferTicks is hashed");
            b.F[0].BufferTicks = 3; b.F[0].AttackLockTicks = 1;
            Assert.AreNotEqual(a.ComputeChecksum(), b.ComputeChecksum(), "AttackLockTicks is hashed");
            b.F[0].AttackLockTicks = 2; b.F[0].BufferedEdges = (int)ChibiButtons.Heavy;
            Assert.AreNotEqual(a.ComputeChecksum(), b.ComputeChecksum(), "BufferedEdges is hashed");
        }
    }
}
