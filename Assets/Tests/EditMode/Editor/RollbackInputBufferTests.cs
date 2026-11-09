using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Story type: Logic (rollback input history). Evidence: automated EditMode unit test. Gate: BLOCKING.
    /// Contract of <see cref="RollbackInputBuffer"/> and <see cref="RollbackStateRing"/>: frame-keyed history,
    /// "repeat last confirmed" prediction, misprediction detection, contiguous confirmation, snapshot restore.
    /// </summary>
    public class RollbackInputBufferTests
    {
        const int Local = 0, Remote = 1, Delay = 2;
        const ushort RunRight = (ushort)ChibiDir.E;
        const ushort RunLeft = (ushort)ChibiDir.W;
        const ushort Punch = (ushort)ChibiButtons.Light;

        [Test]
        public void NewBuffer_DelayFramesAreNeutralAndConfirmed()
        {
            // Arrange + Act
            var b = new RollbackInputBuffer(Local, Delay);

            // Assert
            Assert.AreEqual(Delay - 1, b.RemoteContiguous);
            Assert.AreEqual(Delay - 1, b.LocalLast);
            for (int f = 0; f < Delay; f++)
            {
                Assert.IsTrue(b.IsActual(Remote, f));
                Assert.AreEqual(0, b.Get(Remote, f));
            }
        }

        [Test]
        public void AddLocal_StoresAtNextFrame()
        {
            var b = new RollbackInputBuffer(Local, Delay);

            int f = b.AddLocal(Punch);

            Assert.AreEqual(Delay, f);
            Assert.AreEqual(Punch, b.LocalInput(f));
            Assert.AreEqual(Punch, b.Get(Local, f));
        }

        [Test]
        public void MissingRemote_PredictsNewestContiguousInput()
        {
            // Arrange: remote frames 2 and 3 known (run right), frame 4 missing.
            var b = new RollbackInputBuffer(Local, Delay);
            b.AddRemote(2, RunRight, simulatedBelow: 0);
            b.AddRemote(3, RunRight, simulatedBelow: 0);

            // Act
            ushort predicted = b.Get(Remote, 4);

            // Assert
            Assert.AreEqual(RunRight, predicted);
            Assert.IsFalse(b.IsActual(Remote, 4), "a prediction must not count as confirmed");
            Assert.AreEqual(3, b.RemoteContiguous);
        }

        [Test]
        public void ActualDiffersFromSimulatedPrediction_FlagsEarliestFrame()
        {
            // Arrange: frames 2..5 simulated with the "run right" prediction.
            var b = new RollbackInputBuffer(Local, Delay);
            b.AddRemote(2, RunRight, 0);
            for (int f = 3; f <= 5; f++) b.Get(Remote, f);

            // Act: the truth for 4 and 3 arrives out of order; both differ.
            b.AddRemote(4, RunLeft, simulatedBelow: 6);
            b.AddRemote(3, RunLeft, simulatedBelow: 6);

            // Assert
            Assert.AreEqual(3, b.TakeFirstMisprediction());
            Assert.AreEqual(-1, b.TakeFirstMisprediction(), "taking clears it");
            Assert.AreEqual(4, b.RemoteContiguous);
            Assert.AreEqual(RunLeft, b.PredictionBase);
        }

        [Test]
        public void ActualEqualsPrediction_NoRollback()
        {
            var b = new RollbackInputBuffer(Local, Delay);
            b.AddRemote(2, RunRight, 0);
            b.Get(Remote, 3);

            b.AddRemote(3, RunRight, simulatedBelow: 4);

            Assert.AreEqual(-1, b.FirstMisprediction);
        }

        [Test]
        public void FrameNotYetSimulated_NeverFlagsMisprediction()
        {
            var b = new RollbackInputBuffer(Local, Delay);
            b.AddRemote(2, RunRight, 0);
            b.Get(Remote, 3);                       // predicted but...

            b.AddRemote(3, RunLeft, simulatedBelow: 3);   // ...frame 3 is not simulated yet

            Assert.AreEqual(-1, b.FirstMisprediction);
        }

        [Test]
        public void DuplicateAndStaleFrames_AreIgnored()
        {
            var b = new RollbackInputBuffer(Local, Delay);
            Assert.IsTrue(b.AddRemote(2, Punch, 0));

            Assert.IsFalse(b.AddRemote(2, RunLeft, 0), "already confirmed");
            Assert.IsFalse(b.AddRemote(0, RunLeft, 0), "pre-seeded delay frame");
            Assert.AreEqual(Punch, b.Get(Remote, 2));
        }

        [Test]
        public void FrameFarAhead_IsRejected()
        {
            var b = new RollbackInputBuffer(Local, Delay);

            bool stored = b.AddRemote(b.RemoteContiguous + RollbackInputBuffer.RingSize / 2 + 1, Punch, 0);

            Assert.IsFalse(stored);
        }

        [Test]
        public void ContiguousCursor_WaitsForHoleThenJumps()
        {
            var b = new RollbackInputBuffer(Local, Delay);
            b.AddRemote(3, Punch, 0);
            b.AddRemote(4, Punch, 0);
            Assert.AreEqual(1, b.RemoteContiguous, "frame 2 missing: cursor must not pass the hole");

            b.AddRemote(2, Punch, 0);

            Assert.AreEqual(4, b.RemoteContiguous);
        }

        [Test]
        public void RingWrap_OldFrameSlotIsNotMistakenForNewFrame()
        {
            // Arrange: this peer controls player 1, so player 0 is the remote. Confirm a long run so slots get reused.
            const int Me = 1, Them = 0;
            var b = new RollbackInputBuffer(Me, Delay);
            int last = RollbackInputBuffer.RingSize * 3;

            // Act
            for (int f = Delay; f <= last; f++) b.AddRemote(f, (ushort)(f & 0xF0), 0);

            // Assert: the frame one ring ago shares a slot with `last` but must not read as present.
            Assert.AreEqual(last, b.RemoteContiguous);
            Assert.IsTrue(b.IsActual(Them, last));
            Assert.IsFalse(b.IsActual(Them, last - RollbackInputBuffer.RingSize), "stale slot reported as actual");
        }

        [Test]
        public void StateRing_RestoresExactSnapshotUntilOverwritten()
        {
            // Arrange
            var cfg = new ChibiMatchConfig { Seed = 7, Class0 = FighterClass.Heavy, Class1 = FighterClass.Ranged };
            var live = new ChibiSimState();
            ChibiSimulation.Init(live, cfg);
            var ring = new RollbackStateRing(8);
            for (int f = 0; f < 5; f++)
            {
                ring.Save(f, live);
                ChibiSimulation.Step(live, ChibiInputPacket.Make(ChibiDir.E, ChibiButtons.None), default);
            }
            var expected = new ChibiSimState();
            Assert.IsTrue(ring.TryLoad(2, expected));
            uint hashAt2 = expected.ComputeChecksum();

            // Act: re-step from the restored state and compare with a fresh replay of the same frames.
            var replay = new ChibiSimState();
            ChibiSimulation.Init(replay, cfg);
            for (int f = 0; f < 2; f++) ChibiSimulation.Step(replay, ChibiInputPacket.Make(ChibiDir.E, ChibiButtons.None), default);

            // Assert
            Assert.AreEqual(replay.ComputeChecksum(), hashAt2);
            for (int f = 5; f < 13; f++) ring.Save(f, live);   // frames 5..12 overwrite slots 5..7 and 0..4
            Assert.IsFalse(ring.Has(2), "frame 2 must be gone after the ring wrapped");
            Assert.IsNull(ring.Peek(2));
        }
    }
}
