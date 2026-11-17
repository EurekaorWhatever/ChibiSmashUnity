// Story type: Integration (boot to results smoke, review 2026-11-16 batch 5 item 29) -- evidence: automated PlayMode
// test -- gate: BLOCKING. The game code has no assembly definition, so this file lives in the game assembly
// (Test Runner: "Enable playmode tests for all assemblies", decision B5-1) and is compiled only for tests.
#if UNITY_INCLUDE_TESTS
using System.Collections;
using ChibiClash.Netcode;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ChibiClash.Tests.PlayMode
{
    /// <summary>
    /// Boots the real game scene and walks Loading -> Home -> Select -> Match -> Results. Any error or exception logged on
    /// the way fails the test (Unity Test Framework default). The player's saved profile is backed up and restored.
    /// </summary>
    public class SmokePlayModeTests
    {
        string _save, _backup;

        [UnitySetUp]
        public IEnumerator BackUpProfile()
        {
            _save = PlayerPrefs.GetString(SaveData.Key, null);
            _backup = PlayerPrefs.GetString(SaveData.BackupKey, null);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator RestoreProfile()
        {
            // First remove the game: a live GameManager would write its in-memory (test) profile again on quit.
            foreach (var gm in Object.FindObjectsByType<GameManager>(FindObjectsSortMode.None)) Object.Destroy(gm.gameObject);
            yield return null;
            if (_save == null) PlayerPrefs.DeleteKey(SaveData.Key); else PlayerPrefs.SetString(SaveData.Key, _save);
            if (_backup == null) PlayerPrefs.DeleteKey(SaveData.BackupKey); else PlayerPrefs.SetString(SaveData.BackupKey, _backup);
            PlayerPrefs.Save();
            yield return null;
        }

        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        [UnityTest]
        public IEnumerator Boot_Home_Select_Match_Results_WithoutErrors()
        {
            // Arrange: the real boot scene.
            SceneManager.LoadScene(0);
            GameManager gm = null;
            for (int i = 0; i < 600 && gm == null; i++) { yield return null; gm = Object.FindAnyObjectByType<GameManager>(); }
            Assert.IsNotNull(gm, "the boot scene creates the GameManager");
            Application.runInBackground = true;
            gm.Save.settings.useWebcam = false;
            WebcamFeed.SetEnabled(false);
            // A fresh machine would show the first-launch dialogs; give this in-memory profile an identity (restored after).
            // ageRecorded = true keeps this placeholder out of the player age record file (persistentDataPath).
            if (gm.Save.age <= 0) { gm.Save.age = 20; gm.Save.ageBand = (int)AgeBand.Adult; gm.Save.ageRecorded = true; }
            if (string.IsNullOrEmpty(gm.Save.playerName)) { gm.Save.playerName = "SmokeTest"; gm.Save.ageRecorded = true; }

            // Act + Assert: menus.
            gm.ShowScreen("home");
            yield return Frames(10);
            gm.ShowScreen("select");
            yield return Frames(10);
            gm.ShowScreen("settings");
            yield return Frames(10);

            // A match against the CPU on Plain, running.
            var m = gm.QuickMatch(FighterClass.Melee, FighterClass.Heavy, true, ChibiStageTable.Plain);
            Assert.IsNotNull(m, "a match starts");
            for (int i = 0; i < 900 && !(m.State != null && m.State.Running); i++) yield return null;
            Assert.IsTrue(m.State != null && m.State.Running, "the match is running after its intro");
            yield return Frames(60);

            // Finish it: P1 on the last stock past the side KO line.
            var st = ChibiStageTable.Get(ChibiStageTable.Plain);
            var f = m.State.F[0];
            f.Stocks = 1;
            f.X = st.BlastX + 1000; f.Y = 0; f.Grounded = false; f.Invuln = 0; f.HoverTicks = 0;
            for (int i = 0; i < 900 && !m.Finished; i++) yield return null;
            Assert.IsTrue(m.Finished, "the KO on the last stock ends the match");

            // The results screen draws (it appears 1.3 s after the end).
            yield return new WaitForSecondsRealtime(2f);
            Assert.AreEqual(1, m.Winner, "the CPU won");
        }
    }
}
#endif
