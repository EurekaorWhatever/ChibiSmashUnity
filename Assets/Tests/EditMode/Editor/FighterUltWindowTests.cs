using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Regression tests for the ult window counting down while the match is paused.</summary>
    public class FighterUltWindowTests
    {
        const float Left = Fighter.UltWindowDuration;
        const float FrameDt = 0.25f;     // exact in binary, so the subtraction is exact
        const float BigDt = 10f;         // longer than the whole window

        [Test]
        public void TickUltWindow_Paused_Unchanged()
        {
            Assert.AreEqual(Left, Fighter.TickUltWindow(Left, FrameDt, true));
        }

        [Test]
        public void TickUltWindow_PausedBigDt_Unchanged()
        {
            Assert.AreEqual(Left, Fighter.TickUltWindow(Left, BigDt, true));
        }

        [Test]
        public void TickUltWindow_Unpaused_DecreasesByDt()
        {
            Assert.AreEqual(Left - FrameDt, Fighter.TickUltWindow(Left, FrameDt, false));
        }

        // no clamp: the original `UltWindowLeft -= dt` could go negative and UpdateUltWindow closes on <= 0
        [Test]
        public void TickUltWindow_UnpausedBigDt_GoesNegativeUnclamped()
        {
            Assert.AreEqual(Left - BigDt, Fighter.TickUltWindow(Left, BigDt, false));
        }
    }
}
