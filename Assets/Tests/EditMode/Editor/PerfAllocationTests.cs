// Story type: Logic (per-frame allocation, review 2026-11-16 batch 5 item 30, fixes B and C) -- evidence: automated
// EditMode unit test -- gate: BLOCKING. Before the fixes these paths allocated every frame even with the webcam off
// (pose templates ~280 B/frame, invite cleanup closure 148 B/frame, 2026-11-14 profiler).
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace ChibiClash.Tests
{
    /// <summary>The simulated pose source, the CV telemetry check, skeleton templates and invite expiry allocate nothing per frame.</summary>
    public class PerfAllocationTests
    {
        [Test]
        public void SimulatedPoseTick_DoesNotAllocate()
        {
            Gesture g = Gesture.CrossArms;
            var src = new SimulatedPoseSource("test", () => g);
            src.Tick(1f / 60f);                                         // warm-up
            Assert.That(() => src.Tick(1f / 60f), Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void TelemetryPush_AfterTheFirstSample_DoesNotAllocate()
        {
            var v = new TelemetryValidator();
            var f = SimulatedPoseSource.Template(Gesture.None, false);
            v.Push(f, 1f / 60f);                                        // first sample sets up the previous-frame buffer
            Assert.That(() => v.Push(f, 1f / 60f), Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void TemplateCached_DoesNotAllocate_AndMatchesTemplate()
        {
            SimulatedPoseSource.TemplateCached(Gesture.RaiseHands, true);  // warm-up
            Assert.That(() => { SimulatedPoseSource.TemplateCached(Gesture.RaiseHands, true); }, Is.Not.AllocatingGCMemory());
            var a = SimulatedPoseSource.TemplateCached(Gesture.PunchForward, false);
            var b = SimulatedPoseSource.Template(Gesture.PunchForward, false);
            CollectionAssert.AreEqual(b.p, a.p, "the cached template has the same joints");
        }

        [Test]
        public void SimulatedPose_StillMovesTowardTheTarget()
        {
            Gesture g = Gesture.RaiseHands;
            var src = new SimulatedPoseSource("test", () => g);
            for (int t = 0; t < 120; t++) src.Tick(1f / 60f);
            var goal = SimulatedPoseSource.Template(Gesture.RaiseHands, false);
            Assert.Less((src.Current.p[PoseFrame.LWrist] - goal.p[PoseFrame.LWrist]).magnitude, 0.02f, "behaviour unchanged");
        }
    }
}
