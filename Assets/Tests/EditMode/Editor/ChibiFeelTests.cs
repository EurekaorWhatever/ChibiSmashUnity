// Story type: Logic (fight-view game feel curves) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Covers the pure parts of the feel pass: hit strength, sim-derived tumble, hitstop vibration decay, flash length,
// punch / kick envelopes, trauma shake decay and cap, final-KO slow-mo and zoom curves, and FX slot pool reuse.
using ChibiClash.Netcode;
using NUnit.Framework;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>ChibiFeel / ChibiCameraFeel / ChibiFxRing: presentation curves are bounded, decay to rest and pools never grow.</summary>
    public class ChibiFeelTests
    {
        // Sim velocity sub-units per tick for a given world speed (inverse of ChibiFeel.SubToUps).
        static int SubFor(float ups) => Mathf.RoundToInt(ups / ChibiFeel.SubToUps);

        // ------------------------------------------------------------------ hit strength

        [Test]
        public void HitStrength_NoPercentNoLaunch_IsZero()
        {
            // Act
            float s = ChibiFeel.HitStrength(0, 0f);

            // Assert
            Assert.AreEqual(0f, s, 1e-5f);
        }

        [Test]
        public void HitStrength_FullPercentOrStrongLaunch_IsOne()
        {
            // Act
            float byPct = ChibiFeel.HitStrength(Mathf.CeilToInt(ChibiFeel.PctFullHit), 0f);
            float byLaunch = ChibiFeel.HitStrength(0, ChibiFeel.LaunchStrongUps);

            // Assert
            Assert.AreEqual(1f, byPct, 1e-5f);
            Assert.AreEqual(1f, byLaunch, 1e-5f);
        }

        [Test]
        public void HitStrength_HugeInputs_ClampedToOne()
        {
            // Act
            float s = ChibiFeel.HitStrength(999, 500f);

            // Assert
            Assert.AreEqual(1f, s, 1e-5f);
        }

        // ------------------------------------------------------------------ tumble from sim data

        [Test]
        public void IsTumble_AirborneHitstunWithReducedLaunchGravity_IsTrueEvenWhenSlow()
        {
            // Act
            bool t = ChibiFeel.IsTumble(stun: 20, grounded: false, inPlay: true, launchGravP: 400, vx: 0, vy: 0);

            // Assert
            Assert.IsTrue(t);
        }

        [Test]
        public void IsTumble_AirborneHitstunFastLaunch_IsTrue()
        {
            // Arrange
            int vx = SubFor(ChibiFeel.TumbleMinUps + 1f);

            // Act
            bool t = ChibiFeel.IsTumble(20, false, true, 1000, vx, 0);

            // Assert
            Assert.IsTrue(t);
        }

        [Test]
        public void IsTumble_WeakJabFullGravitySlow_IsFalse()
        {
            // Arrange
            int vx = SubFor(ChibiFeel.TumbleMinUps * 0.5f);

            // Act
            bool t = ChibiFeel.IsTumble(12, false, true, 1000, vx, 0);

            // Assert
            Assert.IsFalse(t);
        }

        [Test]
        public void IsTumble_GroundedOrNoStunOrOut_IsFalse()
        {
            // Arrange
            int fast = SubFor(20f);

            // Act / Assert
            Assert.IsFalse(ChibiFeel.IsTumble(20, true, true, 0, fast, fast), "grounded");
            Assert.IsFalse(ChibiFeel.IsTumble(0, false, true, 0, fast, fast), "no hitstun");
            Assert.IsFalse(ChibiFeel.IsTumble(20, false, false, 0, fast, fast), "KO'd / hovering");
        }

        // ------------------------------------------------------------------ hitstop decoration / flash

        [Test]
        public void HitShake_StrongerHit_LargerAmplitude()
        {
            // Act
            float light = Mathf.Abs(ChibiFeel.HitShake(0f, 1f, 0f));
            float heavy = Mathf.Abs(ChibiFeel.HitShake(1f, 1f, 0f));

            // Assert
            Assert.AreEqual(ChibiFeel.HitShakeMin, light, 1e-5f);
            Assert.AreEqual(ChibiFeel.HitShakeMax, heavy, 1e-5f);
        }

        [Test]
        public void HitShake_HitstopOver_IsZero()
        {
            // Act
            float x = ChibiFeel.HitShake(1f, 0f, 0.123f);

            // Assert
            Assert.AreEqual(0f, x, 1e-6f);
        }

        [Test]
        public void HitShake_HalfPeriodApart_FlipsSide()
        {
            // Arrange
            float half = 0.5f / ChibiFeel.HitShakeHz;

            // Act
            float a = ChibiFeel.HitShake(1f, 1f, 0.1f * half);
            float b = ChibiFeel.HitShake(1f, 1f, 1.1f * half);

            // Assert
            Assert.Less(a * b, 0f);
        }

        [Test]
        public void FlashSeconds_LightOneFrame_HeavyTwoFrames()
        {
            // Act
            float light = ChibiFeel.FlashSeconds(0.1f);
            float heavy = ChibiFeel.FlashSeconds(ChibiFeel.HeavyHit);

            // Assert
            Assert.AreEqual(ChibiFeel.FlashFramesLight * ChibiFeel.FlashFrameSec, light, 1e-6f);
            Assert.AreEqual(ChibiFeel.FlashFramesHeavy * ChibiFeel.FlashFrameSec, heavy, 1e-6f);
        }

        // ------------------------------------------------------------------ camera curves

        [Test]
        public void PunchZoom_LightHit_NoPunch_HeavyCappedAtMax()
        {
            // Act
            float light = ChibiFeel.PunchZoom(ChibiFeel.HeavyHit - 0.01f);
            float heavy = ChibiFeel.PunchZoom(1f);

            // Assert
            Assert.AreEqual(0f, light, 1e-6f);
            Assert.AreEqual(ChibiFeel.PunchZoomMax, heavy, 1e-6f);
        }

        [Test]
        public void PunchEnvelope_RisesToOneThenEasesBackToZero()
        {
            // Act
            float start = ChibiFeel.PunchEnvelope(0f);
            float peak = ChibiFeel.PunchEnvelope(ChibiFeel.PunchAttackSec);
            float mid = ChibiFeel.PunchEnvelope(ChibiFeel.PunchAttackSec + ChibiFeel.PunchReleaseSec * 0.5f);
            float end = ChibiFeel.PunchEnvelope(ChibiFeel.PunchAttackSec + ChibiFeel.PunchReleaseSec);

            // Assert
            Assert.AreEqual(0f, start, 1e-6f);
            Assert.AreEqual(1f, peak, 1e-4f);
            Assert.That(mid, Is.GreaterThan(0f).And.LessThan(1f));
            Assert.AreEqual(0f, end, 1e-6f);
        }

        [Test]
        public void PunchEnvelope_ReleaseIsMonotonicallyDecreasing()
        {
            // Arrange
            float prev = ChibiFeel.PunchEnvelope(ChibiFeel.PunchAttackSec);

            // Act / Assert
            for (int k = 1; k <= 20; k++)
            {
                float v = ChibiFeel.PunchEnvelope(ChibiFeel.PunchAttackSec + ChibiFeel.PunchReleaseSec * k / 20f);
                Assert.LessOrEqual(v, prev + 1e-6f, "step " + k);
                prev = v;
            }
        }

        [Test]
        public void KickEnvelope_OneAtHit_ZeroAfterKickSec()
        {
            // Act / Assert
            Assert.AreEqual(1f, ChibiFeel.KickEnvelope(0f), 1e-6f);
            Assert.AreEqual(0f, ChibiFeel.KickEnvelope(ChibiFeel.KickSec), 1e-6f);
        }

        [Test]
        public void DecayTrauma_DecaysLinearlyAndStopsAtZero()
        {
            // Act
            float half = ChibiFeel.DecayTrauma(1f, 0.5f / ChibiFeel.TraumaDecay);
            float gone = ChibiFeel.DecayTrauma(0.3f, 10f);

            // Assert
            Assert.AreEqual(0.5f, half, 1e-5f);
            Assert.AreEqual(0f, gone, 1e-6f);
        }

        [Test]
        public void ShakeOffset_NeverExceedsMaxTimesTraumaSquared()
        {
            // Act / Assert
            for (int k = 0; k < 200; k++)
            {
                float t = k * 0.0137f;
                float tr = (k % 11) / 10f;
                float m = ChibiFeel.ShakeOffset(tr, t).magnitude;
                Assert.LessOrEqual(m, ChibiFeel.ShakeMaxOffset * tr * tr + 1e-5f, "t " + t);
            }
            Assert.AreEqual(Vector2.zero, ChibiFeel.ShakeOffset(0f, 1f));
        }

        [Test]
        public void CameraFeel_MaxTraumaAndKick_OffsetCappedAndDecaysToRest()
        {
            // Arrange
            var cam = new ChibiCameraFeel();
            cam.AddTrauma(1f);
            cam.Kick(Vector2.right, 1f);
            cam.Punch(ChibiFeel.PunchZoomMax, new Vector2(3f, 1f));

            // Act
            float maxOff = 0f, minZoom = 1f;
            for (int k = 0; k < 120; k++)   // 2 s at 60 fps
            {
                cam.Tick(1f / 60f);
                maxOff = Mathf.Max(maxOff, cam.Offset(k / 60f).magnitude);
                minZoom = Mathf.Min(minZoom, cam.ZoomFactor);
            }

            // Assert
            Assert.LessOrEqual(maxOff, ChibiFeel.CamMaxOffset + 1e-5f);
            Assert.GreaterOrEqual(minZoom, 1f - ChibiFeel.PunchZoomMax - 1e-5f);
            Assert.AreEqual(0f, cam.Trauma, 1e-6f);
            Assert.AreEqual(Vector2.zero, cam.Offset(5f));
            Assert.AreEqual(1f, cam.ZoomFactor, 1e-6f);
        }

        [Test]
        public void CameraFeel_WeakPunchDuringStrongOne_DoesNotCutIt()
        {
            // Arrange
            var cam = new ChibiCameraFeel();
            cam.Punch(ChibiFeel.PunchZoomMax, Vector2.one);
            cam.Tick(ChibiFeel.PunchAttackSec);
            float before = cam.ZoomFactor;

            // Act
            cam.Punch(ChibiFeel.PunchZoomMax * 0.2f, Vector2.zero);

            // Assert
            Assert.AreEqual(before, cam.ZoomFactor, 1e-6f);
            Assert.AreEqual(Vector2.one, cam.PunchAt);
        }

        // ------------------------------------------------------------------ final KO

        [Test]
        public void FinishTimeScale_HoldsSlowThenRampsBackToOne()
        {
            // Act / Assert
            Assert.AreEqual(ChibiFeel.FinishMinScale, ChibiFeel.FinishTimeScale(0f), 1e-6f);
            Assert.AreEqual(ChibiFeel.FinishMinScale, ChibiFeel.FinishTimeScale(ChibiFeel.FinishHoldSec * 0.9f), 1e-6f);
            Assert.AreEqual(1f, ChibiFeel.FinishTimeScale(ChibiFeel.FinishSec), 1e-6f);
            float prev = 0f;
            for (int k = 0; k <= 30; k++)
            {
                float v = ChibiFeel.FinishTimeScale(ChibiFeel.FinishSec * k / 30f);
                Assert.GreaterOrEqual(v, prev - 1e-6f, "step " + k);
                prev = v;
            }
        }

        [Test]
        public void FinishZoom01_ZeroAtEndsFullDuringHold()
        {
            // Act / Assert
            Assert.AreEqual(0f, ChibiFeel.FinishZoom01(0f), 1e-6f);
            Assert.AreEqual(1f, ChibiFeel.FinishZoom01((ChibiFeel.FinishZoomInSec + ChibiFeel.FinishHoldSec) * 0.5f), 1e-6f);
            Assert.AreEqual(0f, ChibiFeel.FinishZoom01(ChibiFeel.FinishSec), 1e-6f);
        }

        // ------------------------------------------------------------------ pool

        [Test]
        public void FxRing_ReleasedSlot_IsReusedWithoutRecycling()
        {
            // Arrange
            var ring = new ChibiFxRing(4);
            int a = ring.Take(out _);
            ring.Take(out _);
            ring.Release(a);

            // Act
            int c = ring.Take(out bool r1);
            int d = ring.Take(out bool r2);
            int e = ring.Take(out bool r3);

            // Assert
            Assert.IsFalse(r1 || r2 || r3);
            Assert.AreEqual(4, ring.LiveCount);
            CollectionAssert.AllItemsAreUnique(new[] { c, d, e });
        }

        [Test]
        public void FxRing_Full_RecyclesRoundRobinAndNeverGrows()
        {
            // Arrange
            var ring = new ChibiFxRing(3);
            for (int k = 0; k < 3; k++) ring.Take(out _);

            // Act
            int x = ring.Take(out bool rx);
            int y = ring.Take(out bool ry);

            // Assert
            Assert.IsTrue(rx && ry);
            Assert.AreNotEqual(x, y);
            Assert.AreEqual(3, ring.LiveCount);
            Assert.AreEqual(3, ring.Capacity);
        }

        [Test]
        public void FxRing_Clear_FreesEverySlot()
        {
            // Arrange
            var ring = new ChibiFxRing(5);
            for (int k = 0; k < 5; k++) ring.Take(out _);

            // Act
            ring.Clear();

            // Assert
            Assert.AreEqual(0, ring.LiveCount);
            for (int k = 0; k < 5; k++) Assert.IsFalse(ring.IsLive(k));
        }
    }
}
