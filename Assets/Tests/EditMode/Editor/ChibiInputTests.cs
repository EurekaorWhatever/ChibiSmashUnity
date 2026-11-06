// Story type: Logic (input sampling / packing). Evidence: automated unit test, BLOCKING.
// Location: Assets/Tests/EditMode/Editor/ (Unity EditMode test root).
using System;
using System.Collections.Generic;
using ChibiClash;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// ChibiPlayerInputSource: render-frame polling, one-shot latching, held state, gesture and PoseVerified bits.
    /// Uses a scripted FighterInput so no Unity input or time is involved.
    /// </summary>
    public class ChibiInputTests
    {
        const float FrameDt = 1f / 60f;
        const float Left = -1f;
        const float Right = 1f;
        const float Neutral = 0f;

        /// <summary>
        /// Scripted device. Each Tick clears every field (as HumanInput/CpuInput do) and then applies the next
        /// queued frame, if any. An empty queue means "nothing pressed or held this frame".
        /// </summary>
        public sealed class FakeDevice : FighterInput
        {
            readonly Queue<Action<FakeDevice>> _frames = new Queue<Action<FakeDevice>>();

            public int TickCount { get; private set; }

            public void Next(Action<FakeDevice> frame) => _frames.Enqueue(frame);
            public void NextIdle() => _frames.Enqueue(_ => { });

            public override void Tick(Fighter self, float dt)
            {
                TickCount++;
                Clear();
                if (_frames.Count > 0) _frames.Dequeue()(this);
            }

            public override Gesture SimulatedGesture => Gesture.None;
        }

        /// <summary>Source plus the knobs a test can change between calls.</summary>
        sealed class Rig
        {
            public readonly FakeDevice Device = new FakeDevice();
            public Gesture CurrentGesture = Gesture.None;
            public bool Verified;
            public readonly ChibiPlayerInputSource Source;

            public Rig()
            {
                Source = new ChibiPlayerInputSource(Device, () => CurrentGesture, () => Verified, () => FrameDt);
            }

            /// <summary>Queue one frame, poll it, then sample.</summary>
            public ChibiInputPacket PollThenSample(Action<FakeDevice> frame)
            {
                Device.Next(frame);
                Source.PollRenderFrame();
                return Source.SampleTick();
            }
        }

        const ChibiButtons AllOneShots =
            ChibiButtons.Jump | ChibiButtons.Light | ChibiButtons.Heavy | ChibiButtons.Dash |
            ChibiButtons.Skill1 | ChibiButtons.Skill2 | ChibiButtons.Skill3 | ChibiButtons.Ultimate;

        const ChibiButtons AllButtonBits = AllOneShots | ChibiButtons.Shield | ChibiButtons.PoseVerified;

        static ChibiButtons ButtonsOf(ChibiInputPacket p) => (ChibiButtons)p.Raw & AllButtonBits;

        // ---------------------------------------------------------------- button mapping

        static IEnumerable<TestCaseData> ButtonCases()
        {
            yield return new TestCaseData((Action<FakeDevice>)(d => d.Jump = true), ChibiButtons.Jump).SetName("EachButton_MapsToItsOwnBit(Jump)");
            yield return new TestCaseData((Action<FakeDevice>)(d => d.Light = true), ChibiButtons.Light).SetName("EachButton_MapsToItsOwnBit(Light)");
            yield return new TestCaseData((Action<FakeDevice>)(d => d.Heavy = true), ChibiButtons.Heavy).SetName("EachButton_MapsToItsOwnBit(Heavy)");
            yield return new TestCaseData((Action<FakeDevice>)(d => d.Block = true), ChibiButtons.Shield).SetName("EachButton_MapsToItsOwnBit(Block)");
            yield return new TestCaseData((Action<FakeDevice>)(d => d.Skill[0] = true), ChibiButtons.Skill1).SetName("EachButton_MapsToItsOwnBit(Skill1)");
            yield return new TestCaseData((Action<FakeDevice>)(d => d.Skill[1] = true), ChibiButtons.Skill2).SetName("EachButton_MapsToItsOwnBit(Skill2)");
            yield return new TestCaseData((Action<FakeDevice>)(d => d.Skill[2] = true), ChibiButtons.Skill3).SetName("EachButton_MapsToItsOwnBit(Skill3)");
            yield return new TestCaseData((Action<FakeDevice>)(d => d.Ult = true), ChibiButtons.Ultimate).SetName("EachButton_MapsToItsOwnBit(Ult)");
            yield return new TestCaseData((Action<FakeDevice>)(d => d.Dash = true), ChibiButtons.Dash).SetName("EachButton_MapsToItsOwnBit(Dash)");
        }

        [TestCaseSource(nameof(ButtonCases))]
        public void EachButton_MapsToItsOwnBit(Action<FakeDevice> press, ChibiButtons expected)
        {
            // Arrange
            var rig = new Rig();

            // Act
            var p = rig.PollThenSample(press);

            // Assert
            Assert.AreEqual(expected, ButtonsOf(p));
            Assert.AreEqual(ChibiDir.Neutral, p.Direction);
            Assert.AreEqual(Gesture.None, p.Gesture);
        }

        // ---------------------------------------------------------------- direction

        [TestCase(Neutral, false, ChibiDir.Neutral)]
        [TestCase(Left, false, ChibiDir.W)]
        [TestCase(Right, false, ChibiDir.E)]
        [TestCase(Neutral, true, ChibiDir.S)]
        [TestCase(Left, true, ChibiDir.SW)]
        [TestCase(Right, true, ChibiDir.SE)]
        public void Direction_MoveAndDown_MapToEightWay(float moveX, bool down, ChibiDir expected)
        {
            // Arrange
            var rig = new Rig();

            // Act
            var p = rig.PollThenSample(d => { d.MoveX = moveX; d.Down = down; });

            // Assert
            Assert.AreEqual(expected, p.Direction);
            Assert.AreEqual(ChibiButtons.None, ButtonsOf(p));
        }

        // ---------------------------------------------------------------- latching

        [Test]
        public void QuickTap_BetweenTicks_IsNotLost()
        {
            // Arrange: tap in frame 1, frames 2-3 show nothing, all before one sim tick.
            var rig = new Rig();
            rig.Device.Next(d => d.Light = true);
            rig.Device.NextIdle();
            rig.Device.NextIdle();

            // Act
            rig.Source.PollRenderFrame();
            rig.Source.PollRenderFrame();
            rig.Source.PollRenderFrame();
            var p = rig.Source.SampleTick();

            // Assert
            Assert.IsTrue(p.Has(ChibiButtons.Light));
            Assert.AreEqual(ChibiButtons.Light, ButtonsOf(p));
        }

        [Test]
        public void Latch_ClearsAfterSample()
        {
            // Arrange
            var rig = new Rig();
            rig.PollThenSample(d => d.Light = true);

            // Act
            var p = rig.PollThenSample(_ => { });

            // Assert
            Assert.IsFalse(p.Has(ChibiButtons.Light));
            Assert.AreEqual(ChibiButtons.None, ButtonsOf(p));
        }

        [Test]
        public void HeldShield_UsesNewestValue()
        {
            // Arrange: Block held in frame 1, released in frame 2, one sample after both.
            var rig = new Rig();
            rig.Device.Next(d => d.Block = true);
            rig.Device.Next(d => d.Block = false);

            // Act
            rig.Source.PollRenderFrame();
            rig.Source.PollRenderFrame();
            var p = rig.Source.SampleTick();

            // Assert
            Assert.IsFalse(p.Has(ChibiButtons.Shield));
        }

        [Test]
        public void SampleWithoutPoll_ReusesHeldState()
        {
            // Arrange
            var rig = new Rig();
            var first = rig.PollThenSample(d => { d.MoveX = Right; d.Jump = true; });
            int ticksAfterFirst = rig.Device.TickCount;

            // Act: catch-up tick in the same render frame.
            var second = rig.Source.SampleTick();

            // Assert
            Assert.IsTrue(first.Has(ChibiButtons.Jump));
            Assert.AreEqual(ChibiDir.E, second.Direction);
            Assert.AreEqual(ChibiButtons.None, (ChibiButtons)second.Raw & AllOneShots);
            Assert.AreEqual(ticksAfterFirst, rig.Device.TickCount, "SampleTick must not re-poll the device");
        }

        // ---------------------------------------------------------------- gesture / pose verified

        [TestCase(Gesture.None)]
        [TestCase(Gesture.CrossArms)]
        [TestCase(Gesture.PunchForward)]
        [TestCase(Gesture.RaiseHands)]
        public void Gesture_IsCarriedInGestureBits(Gesture gesture)
        {
            // Arrange
            var rig = new Rig { CurrentGesture = gesture };

            // Act
            var p = rig.PollThenSample(_ => { });

            // Assert
            Assert.AreEqual(gesture, p.Gesture);
            Assert.AreEqual(ChibiDir.Neutral, p.Direction);
        }

        [TestCase(Gesture.None, true, false)]
        [TestCase(Gesture.CrossArms, false, false)]
        [TestCase(Gesture.CrossArms, true, true)]
        public void PoseVerified_OnlyWithGestureAndVerified(Gesture gesture, bool verified, bool expectBit)
        {
            // Arrange
            var rig = new Rig { CurrentGesture = gesture, Verified = verified };

            // Act
            var p = rig.PollThenSample(_ => { });

            // Assert
            Assert.AreEqual(expectBit, p.Has(ChibiButtons.PoseVerified));
        }

        // ---------------------------------------------------------------- wire

        [Test]
        public void Packet_SurvivesWireMask()
        {
            // Arrange: everything pressed at once, plus a verified gesture and a diagonal.
            var rig = new Rig { CurrentGesture = Gesture.RaiseHands, Verified = true };

            // Act
            var p = rig.PollThenSample(d =>
            {
                d.MoveX = Left; d.Down = true;
                d.Jump = d.Light = d.Heavy = d.Block = d.Dash = d.Ult = true;
                d.Skill[0] = d.Skill[1] = d.Skill[2] = true;
            });

            // Assert
            Assert.AreEqual(p.Raw, (ushort)(p.Raw & ChibiInputPacket.WireMask));
            Assert.AreEqual(p, p.Sanitized());
            Assert.AreEqual(AllButtonBits, ButtonsOf(p));
            Assert.AreEqual(ChibiDir.SW, p.Direction);
            Assert.AreEqual(Gesture.RaiseHands, p.Gesture);
        }
    }
}
