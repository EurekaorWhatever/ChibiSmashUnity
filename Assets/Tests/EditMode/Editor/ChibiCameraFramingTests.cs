// Story type: Logic (camera framing) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Bug 2026-11-12 (Play Mode evidence, Bird Lair): the camera followed a fighter to the cloud floor and showed past the edge
// of the painted backdrop, because its rect came from the KO lines only. The view is now also kept inside the art rect.
using ChibiClash.Netcode;
using NUnit.Framework;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>ChibiCameraFraming: the view rect stays inside the KO lines AND the painted stage art.</summary>
    public class ChibiCameraFramingTests
    {
        const float Aspect = 16f / 9f;

        // Bird Lair as measured in the editor: KO-line rect x +-21, y -13.73..18; backdrop x -11.84..12.16, y -4.49..9.01.
        static readonly Rect BirdLairKo = Rect.MinMaxRect(-21f, -13.73f, 21f, 18f);
        static readonly Rect BirdLairArt = Rect.MinMaxRect(-11.84f, -4.49f, 12.16f, 9.01f);

        [Test]
        public void WithinArt_KoRectLargerThanArt_ReturnsTheArtRect()
        {
            // Act
            Rect r = ChibiCameraFraming.WithinArt(BirdLairKo, BirdLairArt);

            // Assert
            Assert.AreEqual(BirdLairArt.xMin, r.xMin, 1e-4f);
            Assert.AreEqual(BirdLairArt.xMax, r.xMax, 1e-4f);
            Assert.AreEqual(BirdLairArt.yMin, r.yMin, 1e-4f);
            Assert.AreEqual(BirdLairArt.yMax, r.yMax, 1e-4f);
        }

        [Test]
        public void WithinArt_ClampedView_NeverShowsPastTheArtEdge()
        {
            // Arrange: a fighter on the cloud floor far left, camera wants to centre on it at the widest framing
            Rect bounds = ChibiCameraFraming.WithinArt(BirdLairKo, BirdLairArt);
            float size = ChibiCameraFraming.MaxSize(bounds, Aspect, 9f);

            // Act
            Vector2 c = ChibiCameraFraming.ClampCentre(new Vector2(-15f, -3f), size, Aspect, bounds);

            // Assert
            Assert.GreaterOrEqual(c.x - size * Aspect, BirdLairArt.xMin - 1e-4f, "left edge of the view inside the art");
            Assert.GreaterOrEqual(c.y - size, BirdLairArt.yMin - 1e-4f, "bottom edge of the view inside the art");
            Assert.LessOrEqual(size, BirdLairArt.height * 0.5f + 1e-4f);
        }

        [Test]
        public void WithinArt_EmptyOrDisjointArt_KeepsTheKoRect()
        {
            // Act
            Rect empty = ChibiCameraFraming.WithinArt(BirdLairKo, Rect.zero);
            Rect disjoint = ChibiCameraFraming.WithinArt(BirdLairKo, Rect.MinMaxRect(100f, 100f, 110f, 110f));

            // Assert
            Assert.AreEqual(BirdLairKo, empty, "classic stages have no art rect");
            Assert.AreEqual(BirdLairKo, disjoint);
        }
    }
}
