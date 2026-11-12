using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Story type: Logic (asset loading / caching). Evidence: automated EditMode unit test. Gate: BLOCKING.
    /// SpriteAnimLibrary preload and cache contract (docs/performance/2026-11-11-smoothness-audit.md, 2026-11-12 section):
    /// a character folder is loaded from Resources exactly once until released, Get never reloads a preloaded character,
    /// and Release / ReleaseAllExcept drop only what they should. Ids that have no Resources folder keep these tests fast;
    /// one test loads a real character's sheets to prove Preload builds playable clips.
    /// </summary>
    public class SpriteAnimLibraryTests
    {
        const string FakeA = "__SpriteAnimTest_A__";
        const string FakeB = "__SpriteAnimTest_B__";
        const string FakeC = "__SpriteAnimTest_C__";

        [SetUp]
        public void SetUp()
        {
            ReleaseFakes();
        }

        [TearDown]
        public void TearDown()
        {
            ReleaseFakes();
        }

        static void ReleaseFakes()
        {
            SpriteAnimLibrary.Release(FakeA);
            SpriteAnimLibrary.Release(FakeB);
            SpriteAnimLibrary.Release(FakeC);
        }

        [Test]
        public void Preload_CalledTwice_LoadsFolderOnce()
        {
            // Arrange
            int before = SpriteAnimLibrary.LoadCount;

            // Act
            SpriteAnimLibrary.Preload(FakeA, warmGpu: false);
            SpriteAnimLibrary.Preload(FakeA, warmGpu: false);

            // Assert
            Assert.AreEqual(before + 1, SpriteAnimLibrary.LoadCount);
            Assert.IsTrue(SpriteAnimLibrary.IsLoaded(FakeA));
        }

        [Test]
        public void Get_AfterPreload_DoesNotReload()
        {
            // Arrange
            SpriteAnimLibrary.Preload(FakeA, warmGpu: false);
            int afterPreload = SpriteAnimLibrary.LoadCount;

            // Act
            var idle = SpriteAnimLibrary.Get(FakeA, "Idle");
            var move = SpriteAnimLibrary.Get(FakeA, SpriteAnimLibrary.MoveClipName("kaia_ftilt"));
            bool any = SpriteAnimLibrary.HasAny(FakeA);

            // Assert
            Assert.IsNull(idle);
            Assert.IsNull(move);
            Assert.IsFalse(any);
            Assert.AreEqual(afterPreload, SpriteAnimLibrary.LoadCount);
        }

        [Test]
        public void Release_ThenGet_ReloadsOnce()
        {
            // Arrange
            SpriteAnimLibrary.Preload(FakeA, warmGpu: false);

            // Act
            SpriteAnimLibrary.Release(FakeA);
            bool loadedAfterRelease = SpriteAnimLibrary.IsLoaded(FakeA);
            int beforeGet = SpriteAnimLibrary.LoadCount;
            SpriteAnimLibrary.Get(FakeA, "Idle");
            SpriteAnimLibrary.Get(FakeA, "Run");

            // Assert
            Assert.IsFalse(loadedAfterRelease);
            Assert.AreEqual(beforeGet + 1, SpriteAnimLibrary.LoadCount);
            Assert.IsTrue(SpriteAnimLibrary.IsLoaded(FakeA));
        }

        [Test]
        public void ReleaseAllExcept_ThreeLoaded_KeepsOnlyTheTwoNamed()
        {
            // Arrange
            SpriteAnimLibrary.Preload(FakeA, warmGpu: false);
            SpriteAnimLibrary.Preload(FakeB, warmGpu: false);
            SpriteAnimLibrary.Preload(FakeC, warmGpu: false);

            // Act (case-insensitive ids, like the cache)
            int released = SpriteAnimLibrary.ReleaseAllExcept(FakeA.ToLowerInvariant(), FakeB);

            // Assert
            Assert.GreaterOrEqual(released, 1);
            Assert.IsTrue(SpriteAnimLibrary.IsLoaded(FakeA));
            Assert.IsTrue(SpriteAnimLibrary.IsLoaded(FakeB));
            Assert.IsFalse(SpriteAnimLibrary.IsLoaded(FakeC));
        }

        [Test]
        public void Preload_EmptyId_ReturnsZeroAndLoadsNothing()
        {
            // Arrange
            int before = SpriteAnimLibrary.LoadCount;

            // Act
            int clips = SpriteAnimLibrary.Preload(null, warmGpu: false) + SpriteAnimLibrary.Preload("", warmGpu: false);

            // Assert
            Assert.AreEqual(0, clips);
            Assert.AreEqual(before, SpriteAnimLibrary.LoadCount);
        }

        [Test]
        public void Preload_RealCharacter_BuildsPlayableIdleWithoutReloadOnGet()
        {
            // Arrange
            string id = CharacterRoster.Of(FighterClass.Melee).Id;
            bool wasLoaded = SpriteAnimLibrary.IsLoaded(id);
            try
            {
                // Act
                int clips = SpriteAnimLibrary.Preload(id, warmGpu: false);
                int afterPreload = SpriteAnimLibrary.LoadCount;
                var idle = SpriteAnimLibrary.Get(id, "Idle");
                var run = SpriteAnimLibrary.Get(id, "Run");

                // Assert
                Assert.Greater(clips, 0, "no sheets found under Resources/Animations/" + id);
                Assert.IsNotNull(idle);
                Assert.Greater(idle.frames.Length, 0);
                Assert.IsNotNull(idle.frames[0]);
                Assert.IsTrue(idle.loop);
                Assert.IsNotNull(run);
                Assert.AreEqual(afterPreload, SpriteAnimLibrary.LoadCount);
            }
            finally
            {
                if (!wasLoaded) SpriteAnimLibrary.Release(id);
            }
        }

        [Test]
        public void DamageText_InRange_MatchesToStringAndIsReused()
        {
            // Arrange / Act
            string first = ChibiSimView.DamageText(37);
            string second = ChibiSimView.DamageText(37);

            // Assert
            Assert.AreEqual("37", first);
            Assert.AreSame(first, second);
            Assert.AreEqual("0", ChibiSimView.DamageText(0));
            Assert.AreEqual("999", ChibiSimView.DamageText(999));
        }

        [Test]
        public void DamageText_OutOfRange_FallsBackToToString()
        {
            // Act / Assert (boundary values are the point here)
            Assert.AreEqual("-5", ChibiSimView.DamageText(-5));
            Assert.AreEqual("1000", ChibiSimView.DamageText(1000));
        }
    }
}
