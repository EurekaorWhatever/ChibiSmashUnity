using System.Collections.Generic;
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

        // ---- clips.txt-only preload (review 2026-11-13 item 1): fight mode never loads the legacy strip cutouts

        static bool Listed(List<string> listed, string name)
        {
            foreach (var l in listed) if (string.Equals(l, name, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [Test]
        public void Preload_RealCharacter_LoadsOnlyTheClipsTxtSheets()
        {
            // Arrange
            string id = CharacterRoster.Of(FighterClass.Magic).Id;
            SpriteAnimLibrary.Release(id);
            int folderLoads = SpriteAnimLibrary.FolderLoadCount;
            var loaded = new List<string>();
            var listed = new List<string>();
            try
            {
                // Act
                SpriteAnimLibrary.Preload(id, warmGpu: false);
                SpriteAnimLibrary.LoadedTextureNames(id, loaded);
                SpriteAnimLibrary.ListedSheetClips(id, listed);

                // Assert
                Assert.Greater(listed.Count, 0, "precondition: " + id + " has clips.txt");
                Assert.Greater(loaded.Count, 0);
                Assert.AreEqual(folderLoads, SpriteAnimLibrary.FolderLoadCount, "preload loaded the whole folder");
                Assert.IsTrue(SpriteAnimLibrary.IsPreloaded(id));
                foreach (var name in loaded)
                {
                    Assert.IsTrue(FightClips.IsSheetClip(name), "strip texture loaded by the fight preload: " + name);
                    Assert.IsTrue(Listed(listed, name), name + " is not listed in clips.txt");
                }
            }
            finally
            {
                SpriteAnimLibrary.Release(id);
            }
        }

        [Test]
        public void GetCutout_AfterSheetPreload_LoadsTheStripsOnceOnDemand()
        {
            // Arrange
            string id = CharacterRoster.Of(FighterClass.Magic).Id;
            SpriteAnimLibrary.Release(id);
            try
            {
                SpriteAnimLibrary.Preload(id, warmGpu: false);
                int folderLoads = SpriteAnimLibrary.FolderLoadCount;
                int tables = SpriteAnimLibrary.LoadCount;

                // Act
                var sheetRun = SpriteAnimLibrary.GetSheet(id, "Run");
                int afterSheetLookup = SpriteAnimLibrary.FolderLoadCount;
                var run = SpriteAnimLibrary.Get(id, "Run");
                var cast = SpriteAnimLibrary.Get(id, "Cast");
                var missing = SpriteAnimLibrary.Get(id, "Skill9");

                // Assert
                Assert.IsNull(sheetRun, "GetSheet must never return a cutout");
                Assert.AreEqual(folderLoads, afterSheetLookup, "a sheet-only lookup loaded the strips");
                Assert.IsNotNull(run, "menu / legacy callers still get the Run strip");
                Assert.IsNotNull(cast);
                Assert.IsNull(missing);
                Assert.AreEqual(folderLoads + 1, SpriteAnimLibrary.FolderLoadCount, "strips must load exactly once");
                Assert.AreEqual(tables, SpriteAnimLibrary.LoadCount);
            }
            finally
            {
                SpriteAnimLibrary.Release(id);
            }
        }

        [Test]
        public void PreloadStep_ZeroBudget_LoadsIdleSheetFirstThenOneClipPerCall()
        {
            // Arrange
            string id = CharacterRoster.Of(FighterClass.Magic).Id;
            SpriteAnimLibrary.Release(id);
            var loaded = new List<string>();
            var listed = new List<string>();
            try
            {
                // Act
                bool doneAfterFirst = SpriteAnimLibrary.PreloadStep(id, 0f, warmGpu: false);
                SpriteAnimLibrary.LoadedTextureNames(id, loaded);
                SpriteAnimLibrary.ListedSheetClips(id, listed);
                int calls = 1;
                while (!SpriteAnimLibrary.PreloadStep(id, 0f, warmGpu: false) && calls <= listed.Count) calls++;

                // Assert
                Assert.Greater(listed.Count, 1, "precondition: several clips in clips.txt");
                Assert.IsFalse(doneAfterFirst, "a zero budget still stops after one clip");
                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual(SpriteAnimLibrary.IdleSheet, loaded[0], "the READY intro's clip loads first");
                Assert.IsTrue(SpriteAnimLibrary.IsPreloaded(id));
                Assert.LessOrEqual(calls, listed.Count);
            }
            finally
            {
                SpriteAnimLibrary.Release(id);
            }
        }

        [Test]
        public void ReleaseAllExcept_TwoRealCharactersKept_TheirClipsStayUsable()
        {
            // Arrange
            string a = CharacterRoster.Of(FighterClass.Melee).Id, b = CharacterRoster.Of(FighterClass.Magic).Id;
            try
            {
                SpriteAnimLibrary.Preload(a, warmGpu: false);
                SpriteAnimLibrary.Preload(b, warmGpu: false);
                SpriteAnimLibrary.Preload(FakeC, warmGpu: false);
                var idleA = SpriteAnimLibrary.GetSheet(a, SpriteAnimLibrary.IdleSheet);
                var idleB = SpriteAnimLibrary.GetSheet(b, SpriteAnimLibrary.IdleSheet);
                int tables = SpriteAnimLibrary.LoadCount;

                // Act
                SpriteAnimLibrary.ReleaseAllExcept(a, b);

                // Assert: same clip objects, sprites and textures alive, no reload
                Assert.IsFalse(SpriteAnimLibrary.IsLoaded(FakeC));
                Assert.AreSame(idleA, SpriteAnimLibrary.GetSheet(a, SpriteAnimLibrary.IdleSheet));
                Assert.AreSame(idleB, SpriteAnimLibrary.GetSheet(b, SpriteAnimLibrary.IdleSheet));
                Assert.IsTrue(idleA.frames[0] != null && idleA.frames[0].texture != null, a + " sprite destroyed");
                Assert.IsTrue(idleB.frames[0] != null && idleB.frames[0].texture != null, b + " sprite destroyed");
                Assert.IsTrue(SpriteAnimLibrary.IsPreloaded(a) && SpriteAnimLibrary.IsPreloaded(b));
                Assert.AreEqual(tables, SpriteAnimLibrary.LoadCount);
            }
            finally
            {
                SpriteAnimLibrary.Release(a);
                SpriteAnimLibrary.Release(b);
            }
        }

        [Test]
        public void MatchAssetLoader_SameCharacterTwice_FinishesWithItPreloaded()
        {
            // Arrange
            string id = CharacterRoster.Of(FighterClass.Melee).Id;
            SpriteAnimLibrary.Release(id);
            var load = new MatchAssetLoader(FighterClass.Melee, FighterClass.Melee, warmFx: false);
            try
            {
                // Act
                bool firstSlice = load.Step(0f);
                load.Finish();

                // Assert
                Assert.IsFalse(firstSlice, "one zero-budget slice cannot load a whole character");
                Assert.IsTrue(load.Done);
                Assert.IsTrue(SpriteAnimLibrary.IsPreloaded(id));
            }
            finally
            {
                SpriteAnimLibrary.Release(id);
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
