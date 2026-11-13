// Story type: UI (HUD label layout) -- evidence: automated EditMode unit test for the pure helper + Play Mode screenshots
// Bug 2026-11-12 (art review pass 2, item 4): name tags and move callouts sat behind the HUD plates (Mountain, Plain,
// Ocean) and a "Slash!" callout covered a name tag (Volcano). HudLabelLayout now places every world label.
using NUnit.Framework;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>HudLabelLayout: world labels stay on screen, out of the HUD plates and off each other.</summary>
    public class HudLabelLayoutTests
    {
        // A 1280x720 Gui canvas (1920x1080 at Gui.Scale 1.5) with the HUD's plates as Hud draws them.
        const float W = 1280f, H = 720f, Margin = 16f, Gap = 6f;
        static readonly Rect P1Panel = HudLabelLayout.SkewedPlate(new Rect(16f, 14f, 440f, 130f), 0.12f, 5f);
        static readonly Rect P2Panel = HudLabelLayout.SkewedPlate(new Rect(W - 456f, 14f, 440f, 130f), 0.12f, 5f);
        static readonly Rect Timer = HudLabelLayout.SkewedPlate(new Rect(W * 0.5f - 62f, 12f, 124f, 72f), 0.15f, 4f);
        static readonly Rect CvP1 = new Rect(16f, H - 196f, 230f, 172f);

        static Rect[] Plates() => new[] { P1Panel, P2Panel, Timer, CvP1, default, default };
        const int PlateCount = 4;

        static Rect Area(Rect[] plates, int count) =>
            HudLabelLayout.SafeArea(W, H, Margin, HudLabelLayout.BandBottom(plates, count, H), Gap);

        static bool Overlaps(Rect a, Rect b) => a.xMax > b.xMin && a.xMin < b.xMax && a.yMax > b.yMin && a.yMin < b.yMax;

        [Test]
        public void BandBottom_TopPlatesAndBottomCameraPanel_ReturnsLowestTopPlateEdge()
        {
            // Arrange
            var plates = Plates();

            // Act
            float band = HudLabelLayout.BandBottom(plates, PlateCount, H);

            // Assert: the panels (with shadow) are the lowest top plates; the bottom camera panel is not in the band
            Assert.AreEqual(P1Panel.yMax, band, 1e-4f);
        }

        [Test]
        public void SafeArea_AtFullHd_StartsBelowTheReviewLimitOf230Px()
        {
            // Arrange
            var plates = Plates();
            const float gui1080 = 1.5f;   // Gui.Scale at 1920x1080

            // Act
            Rect area = Area(plates, PlateCount);

            // Assert: art review pass 2 #4, "no label may sit above y 230 at 1080p"
            Assert.GreaterOrEqual(area.yMin * gui1080, 230f);
            Assert.AreEqual(Margin, area.xMin, 1e-4f);
            Assert.AreEqual(W - Margin, area.xMax, 1e-4f);
            Assert.AreEqual(H - Margin, area.yMax, 1e-4f);
        }

        [Test]
        public void Place_TagBehindTimer_DropsJustBelowTheTopBand()
        {
            // Arrange: Mountain capture, "Pangya123" tag at about (1000, 117) px = (667, 78) Gui units, behind the timer
            var plates = Plates();
            Rect area = Area(plates, PlateCount);
            var tag = new Rect(620f, 70f, 94f, 38f);

            // Act
            Rect r = HudLabelLayout.Place(tag, area, plates, PlateCount, Gap, false);

            // Assert
            Assert.AreEqual(area.yMin, r.y, 1e-4f, "just below the band");
            Assert.AreEqual(tag.x, r.x, 1e-4f, "stays over its fighter");
            for (int i = 0; i < PlateCount; i++) Assert.IsFalse(Overlaps(r, plates[i]), "plate " + i);
        }

        [Test]
        public void Place_FreeSpot_ReturnsTheLabelUnchanged()
        {
            // Arrange
            var plates = Plates();
            Rect area = Area(plates, PlateCount);
            var tag = new Rect(600f, 400f, 90f, 38f);

            // Act
            Rect r = HudLabelLayout.Place(tag, area, plates, PlateCount, Gap, false);

            // Assert
            Assert.AreEqual(tag, r);
        }

        [Test]
        public void Place_CalloutPastTheRightEdge_ClampsInsideTheScreen()
        {
            // Arrange: a long move callout centred on a fighter at the right ledge
            var plates = Plates();
            Rect area = Area(plates, PlateCount);
            var callout = new Rect(W - 60f, 400f, 180f, 30f);

            // Act
            Rect r = HudLabelLayout.Place(callout, area, plates, PlateCount, Gap, true);

            // Assert
            Assert.AreEqual(W - Margin, r.xMax, 1e-4f);
            Assert.AreEqual(callout.y, r.y, 1e-4f);
        }

        [Test]
        public void Place_CalloutOverItsNameTag_StacksAboveTheTag()
        {
            // Arrange: Volcano capture, "Slash!" over the tag in mid-screen
            var blockers = Plates();
            Rect area = Area(blockers, PlateCount);
            var tag = new Rect(600f, 400f, 90f, 38f);
            blockers[PlateCount] = tag;
            var callout = new Rect(580f, 410f, 120f, 30f);

            // Act
            Rect r = HudLabelLayout.Place(callout, area, blockers, PlateCount + 1, Gap, true);

            // Assert
            Assert.IsFalse(Overlaps(r, tag));
            Assert.AreEqual(tag.yMin - Gap - callout.height, r.y, 1e-4f, "directly above the tag");
        }

        [Test]
        public void Place_CalloutOverTagUnderTheTopBand_GoesBelowTheTag()
        {
            // Arrange: the tag already sits at the top of the safe area, so there is no room above it
            var blockers = Plates();
            Rect area = Area(blockers, PlateCount);
            var tag = new Rect(620f, area.yMin, 94f, 38f);
            blockers[PlateCount] = tag;
            var callout = new Rect(600f, area.yMin, 130f, 30f);

            // Act
            Rect r = HudLabelLayout.Place(callout, area, blockers, PlateCount + 1, Gap, true);

            // Assert
            Assert.AreEqual(tag.yMax + Gap, r.y, 1e-4f);
            for (int i = 0; i < PlateCount + 1; i++) Assert.IsFalse(Overlaps(r, blockers[i]), "blocker " + i);
        }

        [Test]
        public void Place_SecondTagOnTheFirst_DoesNotCollide()
        {
            // Arrange: both fighters in the same spot
            var blockers = Plates();
            Rect area = Area(blockers, PlateCount);
            var first = HudLabelLayout.Place(new Rect(600f, 400f, 90f, 38f), area, blockers, PlateCount, Gap, false);
            blockers[PlateCount] = first;

            // Act
            Rect second = HudLabelLayout.Place(new Rect(610f, 395f, 60f, 38f), area, blockers, PlateCount + 1, Gap, false);

            // Assert
            Assert.IsFalse(Overlaps(second, first));
            Assert.Less(second.y, first.y, "nearest free spot wins: above (39 units) beats below (49)");
        }

        [Test]
        public void Place_TagOnTheCameraPanel_MovesAboveThePanel()
        {
            // Arrange: fighter at the bottom-left, its tag over the camera panel
            var plates = Plates();
            Rect area = Area(plates, PlateCount);
            var tag = new Rect(100f, CvP1.y + 30f, 90f, 38f);

            // Act
            Rect r = HudLabelLayout.Place(tag, area, plates, PlateCount, Gap, false);

            // Assert
            Assert.AreEqual(CvP1.yMin - Gap - tag.height, r.y, 1e-4f);
        }

        [Test]
        public void Pointer_FighterBehindTheTopPlate_PointsUpAtTheFighter()
        {
            // Arrange: tag dropped below the band, fighter above it and to the right of the tag's centre
            var tag = new Rect(600f, 170f, 90f, 18f);
            var fighter = new Vector2(670f, 60f);

            // Act
            Rect ptr = HudLabelLayout.Pointer(tag, fighter, 12f, 8f, 2f, out bool down);

            // Assert
            Assert.IsFalse(down);
            Assert.AreEqual(tag.yMin - 2f, ptr.yMax, 1e-4f, "on top of the tag");
            Assert.AreEqual(fighter.x, ptr.center.x, 1e-4f, "lined up with the fighter");
        }

        [Test]
        public void Pointer_FighterBelowAndPastTheTagEdge_PointsDownFromTheTagEdge()
        {
            // Arrange: tag clamped to the left screen edge, fighter further left and below it
            var tag = new Rect(16f, 300f, 90f, 18f);
            var fighter = new Vector2(4f, 330f);

            // Act
            Rect ptr = HudLabelLayout.Pointer(tag, fighter, 12f, 8f, 2f, out bool down);

            // Assert
            Assert.IsTrue(down);
            Assert.AreEqual(tag.yMax + 2f, ptr.yMin, 1e-4f);
            Assert.AreEqual(tag.xMin, ptr.xMin, 1e-4f, "kept within the tag");
        }

        [Test]
        public void SkewedPlate_ShearAndShadow_GrowsTheRect()
        {
            // Arrange
            var r = new Rect(100f, 10f, 200f, 100f);

            // Act
            Rect p = HudLabelLayout.SkewedPlate(r, 0.2f, 5f);

            // Assert: shear leans each side out by 0.2 * 100 / 2 = 10; the shadow adds 5 right and down
            Assert.AreEqual(90f, p.xMin, 1e-4f);
            Assert.AreEqual(315f, p.xMax, 1e-4f);
            Assert.AreEqual(10f, p.yMin, 1e-4f);
            Assert.AreEqual(115f, p.yMax, 1e-4f);
        }

        [Test]
        public void RotatedSize_QuarterTurn_SwapsWidthAndHeight()
        {
            // Act
            Vector2 s = HudLabelLayout.RotatedSize(120f, 30f, 90f);

            // Assert
            Assert.AreEqual(30f, s.x, 1e-3f);
            Assert.AreEqual(120f, s.y, 1e-3f);
        }

        // ---- popups (review 2026-11-13 item 5): a "14" damage number covered a "Woom!" callout

        [Test]
        public void PlaceAndBlock_CalloutThenDamageNumberAtSameSpot_DoNotOverlap()
        {
            // Arrange: plates, then three popups spawned on the same fighter (callout, damage, comic SFX)
            var blockers = new Rect[PlateCount + 3];
            System.Array.Copy(Plates(), blockers, PlateCount);
            int count = PlateCount;
            var area = Area(blockers, count);
            var woom = new Rect(600f, 360f, 90f, 30f);
            var dmg = new Rect(620f, 362f, 40f, 30f);
            var sfx = new Rect(590f, 355f, 120f, 40f);

            // Act
            var a = HudLabelLayout.PlaceAndBlock(woom, area, blockers, ref count, Gap, true);
            var b = HudLabelLayout.PlaceAndBlock(dmg, area, blockers, ref count, Gap, true);
            var c = HudLabelLayout.PlaceAndBlock(sfx, area, blockers, ref count, Gap, true);

            // Assert
            Assert.AreEqual(PlateCount + 3, count, "each placed popup becomes a blocker");
            Assert.AreEqual(woom.position, a.position, "the first popup keeps its spot");
            Assert.IsFalse(Overlaps(a, b), "damage number covers the callout");
            Assert.IsFalse(Overlaps(a, c));
            Assert.IsFalse(Overlaps(b, c));
            Assert.AreEqual(dmg.size, b.size);
        }

        [Test]
        public void PlaceAndBlock_BlockerArrayFull_PlacesWithoutAppending()
        {
            // Arrange
            var blockers = Plates();
            int count = blockers.Length;
            var area = Area(blockers, PlateCount);

            // Act
            var r = HudLabelLayout.PlaceAndBlock(new Rect(600f, 360f, 40f, 20f), area, blockers, ref count, Gap, true);

            // Assert
            Assert.AreEqual(blockers.Length, count);
            Assert.IsTrue(area.Contains(r.min) && area.Contains(r.max));
        }

        [Test]
        public void Follow_OneFrame_MovesPartWayWithoutSnappingOrOvershooting()
        {
            // Arrange
            var from = new Vector2(100f, 100f);
            var to = new Vector2(100f, 160f);

            // Act
            var still = HudLabelLayout.Follow(from, to, 0f, 14f);
            var one = HudLabelLayout.Follow(from, to, 1f / 60f, 14f);
            var p = from;
            float prevDist = Vector2.Distance(p, to);
            bool monotonic = true;
            for (int i = 0; i < 120; i++)
            {
                p = HudLabelLayout.Follow(p, to, 1f / 60f, 14f);
                float d = Vector2.Distance(p, to);
                if (d > prevDist || p.y > to.y) monotonic = false;
                prevDist = d;
            }

            // Assert
            Assert.AreEqual(from, still, "dt 0 must not move the label");
            Assert.Greater(one.y, from.y);
            Assert.Less(one.y, to.y - 30f, "one frame must not snap most of the way");
            Assert.IsTrue(monotonic, "easing overshot or moved away");
            Assert.Less(Vector2.Distance(p, to), 0.5f, "two seconds of easing should settle on the spot");
        }
    }
}
