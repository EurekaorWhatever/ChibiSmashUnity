using System.IO;
using ChibiClash.Netcode;
using NUnit.Framework;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Story type: Logic (replay file validation, training replays). Evidence: automated EditMode test. Gate: BLOCKING.
    /// Review 2026-11-16: REP-01 (TryRead trusted class / passive / stage bytes) and the training "T" ult fill, which
    /// changes sim state outside the recorded input stream.
    /// </summary>
    public class ChibiReplayHardeningTests
    {
        /// <summary>A structurally valid CCRP v3 file for <paramref name="cfg"/>, with a matching config hash.</summary>
        static MemoryStream RawReplay(ChibiMatchConfig cfg, int frames = 10)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            {
                w.Write(new[] { (byte)'C', (byte)'C', (byte)'R', (byte)'P' });
                w.Write((byte)3);
                w.Write(cfg.Seed);
                w.Write((byte)cfg.Class0); w.Write((byte)cfg.Class1);
                w.Write((byte)cfg.Passive0); w.Write((byte)cfg.Passive1);
                w.Write(cfg.Stage);
                w.Write((byte)0);
                w.Write(cfg.ComputeHash());   // a hostile file carries a hash that matches its own header
                w.Write((uint)frames);
                w.Write(0u);
                w.Write((ushort)0); w.Write((ushort)0); w.Write((byte)frames);
            }
            ms.Position = 0;
            return ms;
        }

        static ChibiMatchConfig Good() => new ChibiMatchConfig
            { Seed = 7, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy, Passive0 = 0, Passive1 = 1, Stage = 0 };

        static ChibiMatchConfig Bad(string field)
        {
            var c = Good();
            switch (field)
            {
                case "class0": c.Class0 = (FighterClass)200; break;
                case "class1": c.Class1 = (FighterClass)31; break;
                case "passive0": c.Passive0 = 7; break;
                case "passive1": c.Passive1 = 255; break;
                case "stageHigh": c.Stage = 99; break;
                case "stageNegative": c.Stage = -1; break;
            }
            return c;
        }

        [Test]
        public void Rep01_WellFormedHeader_StillReads()
        {
            Assert.IsTrue(ChibiReplay.TryRead(RawReplay(Good()), out var cfg, out var p0, out _, out _));
            Assert.AreEqual(FighterClass.Heavy, cfg.Class1);
            Assert.AreEqual(10, p0.Count);
        }

        [TestCase("class0")]
        [TestCase("class1")]
        [TestCase("passive0")]
        [TestCase("passive1")]
        [TestCase("stageHigh")]
        [TestCase("stageNegative")]
        public void Rep01_OutOfRangeHeaderField_IsRejectedWithoutThrowing(string field)
        {
            bool read = true, verified = true;
            Assert.DoesNotThrow(() => read = ChibiReplay.TryRead(RawReplay(Bad(field)), out _, out _, out _, out _));
            Assert.IsFalse(read, "TryRead accepted an out-of-range " + field);
            Assert.DoesNotThrow(() => verified = ChibiReplay.Verify(RawReplay(Bad(field))));
            Assert.IsFalse(verified, "Verify accepted an out-of-range " + field);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Replay_HazardsSwitch_RoundTripsAndVerifies(bool hazardsOff)
        {
            // Arrange: HazardsOff is in the config hash, so it has to be in the file too (it was not: hazards-off replays
            // failed the hash check on load).
            var cfg = Good();
            cfg.Stage = ChibiStageTable.Volcano;
            cfg.HazardsOff = hazardsOff;
            var p0 = new System.Collections.Generic.List<ushort>();
            var p1 = new System.Collections.Generic.List<ushort>();
            for (int i = 0; i < 120; i++) { p0.Add(0); p1.Add(0); }
            var ms = new MemoryStream();

            // Act
            ChibiReplay.Write(ms, cfg, p0, p1);
            ms.Position = 0;
            bool read = ChibiReplay.TryRead(ms, out var got, out _, out _, out _);
            ms.Position = 0;

            // Assert
            Assert.IsTrue(read, "own replay did not read back");
            Assert.AreEqual(hazardsOff, got.HazardsOff);
            Assert.IsTrue(ChibiReplay.Verify(ms));
        }

        [Test]
        public void TrainingMatch_DoesNotRecordAReplay()
        {
            // The training "T" key fills the ult directly in the sim state, so a training replay could never verify.
            var go = new GameObject("replay-test-loop");
            try
            {
                var loop = go.AddComponent<ChibiLocalLoop>();
                var training = Good(); training.Training = true;

                loop.Begin(training, new ChibiScriptedInputSource(n => default), new ChibiScriptedInputSource(n => default));
                Assert.IsNull(loop.Replay, "training must not record a replay");

                loop.Begin(Good(), new ChibiScriptedInputSource(n => default), new ChibiScriptedInputSource(n => default));
                Assert.IsNotNull(loop.Replay, "a normal offline match still records");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
