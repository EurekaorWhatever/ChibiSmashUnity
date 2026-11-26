// Story type: Logic (rules v27 feedback pass, user request 2026-11-27) -- evidence: automated EditMode unit test -- gate: BLOCKING.
// Ren's neutral heavy 1.5 s cooldown, Lyra's 1 s, Kaia's Sword Aura 25 % fewer waves, quest rewards grouped as one line.
//
// Ticks are game steps (ChibiSimState.GameTicks); hitstop real ticks do not count.
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Rules v27: heavy shot cooldowns, Sword Aura wave gap; grouped quest reward lines.</summary>
    public class ChibiFeedbackV27Tests
    {
        static readonly ChibiInputPacket N = default;

        /// <summary>Mashes neutral Heavy (press, release) and returns the game ticks of the first two heavy starts.</summary>
        static int[] HeavyStarts(FighterClass cls, int steps)
        {
            var s = MovesetKit.Arena(cls, FighterClass.Heavy, -3000, 9000);
            var starts = new List<int>();
            int prevSlot = -1, prevTick = -1;
            for (int k = 0; k < steps; k++)
            {
                var a = k % 2 == 0 ? MovesetKit.P(ChibiDir.Neutral, ChibiButtons.Heavy) : N;
                MovesetKit.Game(s, a, N);
                var f = s.F[0];
                bool started = f.MoveSlot == 1 && (prevSlot != 1 || f.MoveTick < prevTick);
                if (started) starts.Add(k);
                prevSlot = f.MoveSlot; prevTick = f.MoveTick;
            }
            return starts.ToArray();
        }

        [Test]
        public void RenNeutralHeavy_HasA90TickCooldown()
        {
            var starts = HeavyStarts(FighterClass.Ranged, 200);
            Assert.GreaterOrEqual(starts.Length, 2, "Ren should fire the heavy again once the cooldown ends");
            int gap = starts[1] - starts[0];
            Assert.GreaterOrEqual(gap, ChibiSimConst.HeavyShotCdRanged, "Ren heavy fired again before 1.5 s");
            Assert.LessOrEqual(gap, ChibiSimConst.HeavyShotCdRanged + 3, "Ren heavy waited far longer than its cooldown");
        }

        [Test]
        public void LyraNeutralHeavy_HasA60TickCooldown()
        {
            var starts = HeavyStarts(FighterClass.Magic, 160);
            Assert.GreaterOrEqual(starts.Length, 2, "Lyra should fire the heavy again once the cooldown ends");
            int gap = starts[1] - starts[0];
            Assert.GreaterOrEqual(gap, ChibiSimConst.HeavyShotCdMagic, "Lyra heavy fired again before 1 s");
            Assert.LessOrEqual(gap, ChibiSimConst.HeavyShotCdMagic + 3, "Lyra heavy waited far longer than its cooldown");
        }

        [Test]
        public void HeavyShotCooldown_OnlyRenAndLyra()
        {
            Assert.AreEqual(90, ChibiSimConst.HeavyShotCd((int)FighterClass.Ranged));
            Assert.AreEqual(60, ChibiSimConst.HeavyShotCd((int)FighterClass.Magic));
            Assert.AreEqual(0, ChibiSimConst.HeavyShotCd((int)FighterClass.Melee));
            Assert.AreEqual(0, ChibiSimConst.HeavyShotCd((int)FighterClass.Heavy));
            Assert.AreEqual(0, ChibiSimConst.HeavyShotCd((int)FighterClass.Polearm));
        }

        [Test]
        public void HeavyCd_IsCopiedAndChecksummed()
        {
            var a = MovesetKit.Arena(FighterClass.Ranged);
            var b = new ChibiSimState();
            b.CopyFrom(a);
            Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum());
            b.F[0].HeavyCd = 17;
            Assert.AreNotEqual(a.ComputeChecksum(), b.ComputeChecksum(), "HeavyCd must be in the checksum");
            a.CopyFrom(b);
            Assert.AreEqual(17, a.F[0].HeavyCd, "HeavyCd must be copied");
            b.F[0].AuraWaveCredit = 5;
            Assert.AreNotEqual(a.ComputeChecksum(), b.ComputeChecksum(), "AuraWaveCredit must be in the checksum");
        }

        /// <summary>Kaia mashes neutral Light under Sword Aura; returns (strikes, waves).</summary>
        static (int strikes, int waves) AuraMash(int steps)
        {
            var s = MovesetKit.Arena(FighterClass.Melee, FighterClass.Heavy, -1000, 12000);
            var f = s.F[0];
            f.Facing = -1;                                       // throw the waves away from the foe (no hits, no hitstop)
            f.AuraTicks = ChibiSimConst.AuraDuration;
            f.AuraWaveCredit = ChibiSimConst.AuraWaveGapTicks;
            var wasWave = new bool[s.Proj.Length];
            int strikes = 0, waves = 0, prevSlot = -1, prevTick = -1;
            for (int k = 0; k < steps; k++)
            {
                var a = k % 2 == 0 ? MovesetKit.P(ChibiDir.Neutral, ChibiButtons.Light) : N;
                MovesetKit.Game(s, a, N);
                if (f.MoveSlot == 0 && (prevSlot != 0 || f.MoveTick < prevTick)) strikes++;
                prevSlot = f.MoveSlot; prevTick = f.MoveTick;
                for (int n = 0; n < s.Proj.Length; n++)
                {
                    bool w = s.Proj[n].Active && s.Proj[n].Kind == ChibiProjKind.AuraWave;
                    if (w && !wasWave[n]) waves++;
                    wasWave[n] = w;
                }
            }
            return (strikes, waves);
        }

        [Test]
        public void SwordAura_LightChain_ThrowsAQuarterFewerWaves()
        {
            var (strikes, waves) = AuraMash(300);
            Assert.GreaterOrEqual(strikes, 16, "fixture: the light chain should strike often");
            float ratio = waves / (float)strikes;
            // Before rules v27 every strike threw a wave (ratio 1.0). Now a wave every 14 ticks of credit: ~3 in 4.
            Assert.That(ratio, Is.InRange(0.65f, 0.85f), $"waves {waves} / strikes {strikes}");
        }

        [Test]
        public void SwordAura_FirstStrike_ThrowsAWave()
        {
            var (strikes, waves) = AuraMash(12);
            Assert.GreaterOrEqual(strikes, 1);
            Assert.AreEqual(1, waves, "the aura starts with one full wave of credit");
        }

        [Test]
        public void QuestProgress_ManyCompletions_LogOneGroupedLine()
        {
            var save = new SaveData();
            save.Repair();
            var log = new List<string>();
            int target = 0, reward = 0;
            foreach (var q in save.quests) if (q.id == "parry") { target = q.target; reward = q.reward; }
            Assert.Greater(target, 0, "fixture: the parry quest exists");
            int earned = save.Progress("parry", target * 9, log);
            Assert.AreEqual(reward * 9, earned);
            Assert.AreEqual(1, log.Count, "nine completions of one quest must be one line");
            StringAssert.Contains("×9", log[0]);
            StringAssert.Contains((reward * 9).ToString(), log[0]);
        }

        [Test]
        public void QuestProgress_OneCompletion_HasNoMultiplier()
        {
            var save = new SaveData();
            save.Repair();
            var log = new List<string>();
            save.Progress("win", 3, log);
            Assert.AreEqual(1, log.Count);
            StringAssert.DoesNotContain("×", log[0]);
        }

        [Test]
        public void ThaiShaper_ToneOverUpperVowel_IsRaised()
        {
            Assert.AreEqual("เริม", ThaiShaper.Shape("เริ่ม"));   // เริ่ม
        }

        [Test]
        public void ThaiShaper_ToneBeforeSaraAm_IsRaised()
        {
            Assert.AreEqual("นำ", ThaiShaper.Shape("น้ำ"));   // น้ำ
        }

        [Test]
        public void ThaiShaper_MarksOverTallConsonant_ShiftLeft()
        {
            Assert.AreEqual("ป", ThaiShaper.Shape("ป่"));                 // ป่
            Assert.AreEqual("ป", ThaiShaper.Shape("ปิ่"));   // ปิ่
        }

        [Test]
        public void ThaiShaper_PlainText_ReturnsSameInstance()
        {
            const string plain = "ก่ ABC 123";   // ก่ : a tone with no upper vowel stays put
            Assert.AreSame(plain, ThaiShaper.Shape(plain));
        }

        [Test]
        public void PerfStats_RttX8ToMs_ConvertsTicks()
        {
            Assert.AreEqual(100, PerfStats.RttX8ToMs(48, 60));   // 6 ticks at 60 Hz
            Assert.AreEqual(-1, PerfStats.RttX8ToMs(-1, 60));
        }

        [Test]
        public void PerfStats_Format_PutsFpsBeforePing()
        {
            string t = PerfStats.Format(60, 42);
            StringAssert.Contains("60", t);
            StringAssert.Contains("42", t);
            Assert.Less(t.IndexOf("60"), t.IndexOf("42"), "FPS comes first");
            StringAssert.Contains("--", PerfStats.Format(60, -1));
        }
    }
}
