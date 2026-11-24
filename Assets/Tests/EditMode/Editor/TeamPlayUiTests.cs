// Story type: Logic (team-play UI rules, TP-6 phase 3: slot colours, format picker, select slot building, 4-plate
// HUD layout, FFA results ordering, remembered format in the save) -- evidence: automated EditMode unit test --
// gate: BLOCKING. Screens touched get retained screenshots in production/qa/evidence/ separately.
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>Pure helpers behind the phase 3 team-play UI.</summary>
    public class TeamPlayUiTests
    {
        const int F1 = ChibiMatchConfig.Format1v1, Ffa = ChibiMatchConfig.FormatFfa, F2 = ChibiMatchConfig.Format2v2;

        sealed class MemoryStore : ISaveStore
        {
            public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
            public string Get(string key) => Data.TryGetValue(key, out var v) ? v : "";
            public void Set(string key, string value) => Data[key] = value;
            public void Flush() { }
        }

        // ---------------------------------------------------------------- colours

        [Test]
        public void SlotColor_1v1_MatchesPlayerColor()
        {
            Assert.AreEqual(Gui.PlayerColor(0), Gui.SlotColor(F1, 0, 0));
            Assert.AreEqual(Gui.PlayerColor(1), Gui.SlotColor(F1, 1, 1));
        }

        [Test]
        public void SlotColor_2v2_IsByTeam()
        {
            Assert.AreEqual(Gui.P1, Gui.SlotColor(F2, 0, 0));
            Assert.AreEqual(Gui.P1, Gui.SlotColor(F2, 0, 1));
            Assert.AreEqual(Gui.P2, Gui.SlotColor(F2, 1, 2));
            Assert.AreEqual(Gui.P2, Gui.SlotColor(F2, 1, 3));
        }

        [Test]
        public void SlotColor_Ffa_IsBySlot()
        {
            Assert.AreEqual(Gui.P1, Gui.SlotColor(Ffa, 0, 0));
            Assert.AreEqual(Gui.P2, Gui.SlotColor(Ffa, 1, 1));
            Assert.AreEqual(Gui.Gold, Gui.SlotColor(Ffa, 2, 2));
            Assert.AreEqual(Gui.Green, Gui.SlotColor(Ffa, 3, 3));
        }

        // ---------------------------------------------------------------- format picker

        [Test]
        public void CycleFormat_RightAndLeft_WrapThroughAllThree()
        {
            Assert.AreEqual(F2, TeamPlaySetup.CycleFormat(F1, 1));
            Assert.AreEqual(Ffa, TeamPlaySetup.CycleFormat(F2, 1));
            Assert.AreEqual(F1, TeamPlaySetup.CycleFormat(Ffa, 1));
            Assert.AreEqual(Ffa, TeamPlaySetup.CycleFormat(F1, -1));
            Assert.AreEqual(F1, TeamPlaySetup.CycleFormat(F2, -1));
            Assert.AreEqual(F1, TeamPlaySetup.CycleFormat(99, 0));
        }

        // ---------------------------------------------------------------- select slots

        static readonly FighterClass[] Picks = { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic };
        static readonly int[] Levels = { 1, 2, 3, 1 };

        static int Build(int format, SlotMode[] modes, out int mask, out FighterClass[] cls, out int[] lv, out int[] src)
        {
            cls = new FighterClass[4]; lv = new int[4]; src = new int[4];
            return TeamPlaySetup.Build(format, modes, Picks, Levels, cls, lv, src, out mask);
        }

        [Test]
        public void Build_2v2_AlwaysFour_OffPlaysAsCpu()
        {
            var modes = new[] { SlotMode.Human, SlotMode.Off, SlotMode.Human, SlotMode.Cpu };
            Assert.AreEqual(4, Build(F2, modes, out int mask, out _, out var lv, out _));
            Assert.AreEqual(0b0101, mask);
            Assert.AreEqual(2, lv[1]);
            Assert.AreEqual(0, lv[0]);
        }

        [Test]
        public void Build_FfaOneOff_CompactsToThree()
        {
            var modes = new[] { SlotMode.Human, SlotMode.Cpu, SlotMode.Off, SlotMode.Human };
            Assert.AreEqual(3, Build(Ffa, modes, out int mask, out var cls, out var lv, out var src));
            CollectionAssert.AreEqual(new[] { FighterClass.Melee, FighterClass.Heavy, FighterClass.Magic }, new[] { cls[0], cls[1], cls[2] });
            CollectionAssert.AreEqual(new[] { 0, 1, 3, -1 }, src);
            Assert.AreEqual(0b101, mask);
            Assert.AreEqual(2, lv[1]);
        }

        [Test]
        public void Build_FfaNeverBelowThree()
        {
            var modes = new[] { SlotMode.Off, SlotMode.Off, SlotMode.Off, SlotMode.Cpu };
            Assert.AreEqual(3, Build(Ffa, modes, out _, out _, out _, out _));
        }

        [Test]
        public void Build_1v1_IsTwo()
        {
            var modes = new[] { SlotMode.Human, SlotMode.Cpu, SlotMode.Human, SlotMode.Human };
            Assert.AreEqual(2, Build(F1, modes, out int mask, out _, out _, out _));
            Assert.AreEqual(1, mask);
        }

        [Test]
        public void NextMode_OffOnlyInFfa_AndOnlyOnce()
        {
            var modes = new[] { SlotMode.Human, SlotMode.Cpu, SlotMode.Cpu, SlotMode.Cpu };
            Assert.AreEqual(SlotMode.Human, TeamPlaySetup.NextMode(F2, modes, 1));
            Assert.AreEqual(SlotMode.Off, TeamPlaySetup.NextMode(Ffa, modes, 1));
            modes[2] = SlotMode.Off;
            Assert.AreEqual(SlotMode.Human, TeamPlaySetup.NextMode(Ffa, modes, 1));
            Assert.IsFalse(TeamPlaySetup.CanSetOff(Ffa, modes, 0));
        }

        // ---------------------------------------------------------------- HUD plates

        const float Inset = 16f, Top = 14f, TimerW = 124f, TimerTop = 12f, TimerH = 72f;

        [TestCase(1280f, 720f)]
        [TestCase(1920f, 1080f)]
        [TestCase(960f, 720f)]
        public void FourPlates_DoNotOverlapEachOtherOrTimer_AndStayOnScreen(float w, float h)
        {
            var timer = new Rect(w * 0.5f - TimerW * 0.5f, TimerTop, TimerW, TimerH);
            var screen = new Rect(0f, 0f, w, h);
            for (int count = 3; count <= 4; count++)
            {
                var r = new Rect[count];
                for (int i = 0; i < count; i++)
                {
                    r[i] = TeamPlayHudLayout.Plate(i, count, w, Inset, Top, TimerW);
                    Assert.IsTrue(screen.Contains(r[i].min) && screen.Contains(r[i].max), $"plate {i} off screen at {w}");
                    Assert.IsFalse(r[i].Overlaps(timer), $"plate {i} under the timer at {w}");
                }
                for (int i = 0; i < count; i++)
                    for (int j = i + 1; j < count; j++)
                        Assert.IsFalse(r[i].Overlaps(r[j]), $"plates {i},{j} overlap at {w} ({count} fighters)");
            }
        }

        [TestCase(1280f)]
        [TestCase(1706.667f)]
        public void PanelRect1v1_IsPinned(float w)
        {
            // Values of Hud.PanelRect before TP-6 (inset 16, top 14, 440 x 130): the 1v1 HUD must not move a pixel.
            Assert.AreEqual(new Rect(16f, 14f, 440f, 130f), Hud.PanelRect1v1(0, w));
            Assert.AreEqual(new Rect(w - 16f - 440f, 14f, 440f, 130f), Hud.PanelRect1v1(1, w));
        }

        [Test]
        public void PanelRect1v1_At1280_IsExactPixels()
        {
            Assert.AreEqual(824f, Hud.PanelRect1v1(1, 1280f).x);
        }

        // ---------------------------------------------------------------- input: pads 3 / 4 have no keyboard half

        [TestCase(2)]
        [TestCase(3)]
        public void Bindings_PlayersWithoutKeys_NeverThrowNorReadKeys(int player)
        {
            Assert.IsFalse(Bindings.HasKeys(player));
            Assert.DoesNotThrow(() => Bindings.Held(player, BindAction.Jump));
            Assert.IsFalse(Bindings.Held(player, BindAction.Jump));
            Assert.IsFalse(Bindings.Pressed(player, BindAction.Light));
        }

        [Test]
        public void Bindings_KeyboardHalves_AreP1AndP2Only()
        {
            Assert.IsTrue(Bindings.HasKeys(0));
            Assert.IsTrue(Bindings.HasKeys(1));
        }

        [Test]
        public void FourPlates_ReadLeftToRight_P1ToP4()
        {
            float prev = -1f;
            for (int i = 0; i < 4; i++)
            {
                float x = TeamPlayHudLayout.Plate(i, 4, 1280f, Inset, Top, TimerW).x;
                Assert.Greater(x, prev);
                prev = x;
            }
        }

        [Test]
        public void PlateWidth_IsPreferredWhenItFits()
        {
            Assert.AreEqual(TeamPlayHudLayout.PlateW, TeamPlayHudLayout.PlateWidth(1920f, Inset, TimerW));
            Assert.Less(TeamPlayHudLayout.PlateWidth(1280f, Inset, TimerW), TeamPlayHudLayout.PlateW);
        }

        // ---------------------------------------------------------------- camera

        [Test]
        public void CameraMinSize_1v1Unchanged_GrowsPerExtraFighter()
        {
            Assert.AreEqual(3f, ChibiSimView.MinSizeFor(2, 3f));
            Assert.AreEqual(3f + ChibiSimView.CamExtraFighterMin, ChibiSimView.MinSizeFor(3, 3f), 1e-5f);
            Assert.AreEqual(3f + 2f * ChibiSimView.CamExtraFighterMin, ChibiSimView.MinSizeFor(4, 3f), 1e-5f);
        }

        // ---------------------------------------------------------------- results ordering

        [Test]
        public void RankByPlacement_SortsBest_FirstUnsetLast_TiesBySlot()
        {
            var order = new int[4];
            TeamPlayHudLayout.RankByPlacement(new[] { 3, 1, 2, 4 }, 4, order);
            CollectionAssert.AreEqual(new[] { 1, 2, 0, 3 }, order);
            TeamPlayHudLayout.RankByPlacement(new[] { 0, 2, 2, 1 }, 4, order);
            CollectionAssert.AreEqual(new[] { 3, 1, 2, 0 }, order);
            TeamPlayHudLayout.RankByPlacement(new[] { 2, 3, 1, 0 }, 3, order);
            CollectionAssert.AreEqual(new[] { 2, 0, 1 }, new[] { order[0], order[1], order[2] });
        }

        [Test]
        public void NoContestReason_NeverEmptyForNoContestCauses_EmptyOtherwise()
        {
            Assert.IsNotEmpty(TeamPlayHudLayout.NoContestReason(ChibiEndCause.Desync));
            Assert.IsNotEmpty(TeamPlayHudLayout.NoContestReason(ChibiEndCause.DropVoteFailed));
            Assert.IsNotEmpty(TeamPlayHudLayout.NoContestReason(ChibiEndCause.ProtocolViolation));
            Assert.IsNotEmpty(TeamPlayHudLayout.NoContestReason(ChibiEndCause.PeerNeverStarted));
            Assert.IsEmpty(TeamPlayHudLayout.NoContestReason(ChibiEndCause.MatchOver));
        }

        [Test]
        public void MatchConfig_InputOf_FollowsBuildSource_ForCompactedFfa()
        {
            var modes = new[] { SlotMode.Human, SlotMode.Cpu, SlotMode.Off, SlotMode.Human };
            Build(Ffa, modes, out _, out _, out _, out var src);
            var cfg = new MatchConfig();
            for (int i = 0; i < 4; i++) cfg.inputOf[i] = src[i] >= 0 ? src[i] : i;
            Assert.AreEqual(3, cfg.InputOf(2), "the human from select slot 3 plays sim slot 2 with player 4's pad");
            Assert.AreEqual(0, cfg.InputOf(0));
            Assert.AreEqual(2, new MatchConfig().InputOf(2), "identity by default");
        }

        [Test]
        public void DisplaySlot_FfaSlot2Off_SimSlot2KeepsSelectIdentityP4Green()
        {
            var modes = new[] { SlotMode.Human, SlotMode.Cpu, SlotMode.Off, SlotMode.Cpu };
            Build(Ffa, modes, out _, out _, out _, out var src);
            int shown = TeamPlaySetup.DisplaySlot(false, src, 2);
            Assert.AreEqual(3, shown, "the fighter picked as P4 stays P4");
            Assert.AreEqual(Gui.Green, Gui.SlotColor(Ffa, 2, shown));
            Assert.AreEqual(2, TeamPlaySetup.DisplaySlot(true, src, 2), "online: the sim slot");
            Assert.AreEqual(1, TeamPlaySetup.DisplaySlot(false, null, 1), "no remap: the sim slot");
        }

        [Test]
        public void HasDevice_P1P2Keyboard_P3P4NeedTheirPad()
        {
            Assert.IsTrue(TeamPlaySetup.HasDevice(0, 0));
            Assert.IsTrue(TeamPlaySetup.HasDevice(1, 0));
            Assert.IsFalse(TeamPlaySetup.HasDevice(2, 2));
            Assert.IsTrue(TeamPlaySetup.HasDevice(2, 3));
            Assert.IsFalse(TeamPlaySetup.HasDevice(3, 3));
            Assert.IsTrue(TeamPlaySetup.HasDevice(3, 4));
        }

        [TestCase(180f)]
        [TestCase(200f)]
        [TestCase(230f)]
        [TestCase(250f)]
        [TestCase(269f)]
        [TestCase(290f)]
        public void MiniLayout_ChipRowEndsLeftOfPercentBlock(float plateW)
        {
            TeamPlayHudLayout.MiniLayout(plateW, out float portrait, out float pctW, out float chipScale, out float statusW);
            float chipsEnd = portrait + 20f + TeamPlayHudLayout.ChipRowW * chipScale;
            float pctLeft = plateW - 6f - pctW;
            Assert.LessOrEqual(chipsEnd, pctLeft - 4f + 1e-3f, $"chips overlap the percent at {plateW}");
            Assert.LessOrEqual(portrait + 20f + statusW, pctLeft + 1e-3f, $"status text overlaps the percent at {plateW}");
        }

        [Test]
        public void MatchConfig_CpuLevelOf_FallsBackToGlobalDifficulty()
        {
            var cfg = new MatchConfig { difficulty = 3 };
            Assert.AreEqual(3, cfg.CpuLevelOf(1));
            cfg.cpuLevel[1] = 1;
            Assert.AreEqual(1, cfg.CpuLevelOf(1));
        }

        // ---------------------------------------------------------------- online format modal

        [Test]
        public void FormatModal_OpensOnRemembered_StepsWithPadLeftRight()
        {
            var m = new FormatModalState();
            Assert.IsTrue(m.Show(F2, busy: false));
            Assert.AreEqual(F2, m.Format);
            m.Step(1);
            Assert.AreEqual(Ffa, m.Format);
            m.Step(1);
            Assert.AreEqual(F1, m.Format);
            m.Step(-1);
            Assert.AreEqual(Ffa, m.Format);
            Assert.IsTrue(m.Confirm(out int chosen));
            Assert.AreEqual(Ffa, chosen);
            Assert.IsFalse(m.Open);
        }

        [Test]
        public void FormatModal_Back_ClosesWithoutConfirming()
        {
            var m = new FormatModalState();
            m.Show(F1, busy: false);
            m.Back();
            Assert.IsFalse(m.Open);
            Assert.IsFalse(m.Confirm(out _), "B must not start (or reach) a search");
        }

        [Test]
        public void FormatModal_KeepOnlyOn_ClosesOffTheOnlinePage()
        {
            var m = new FormatModalState();
            m.Show(F2, busy: false);
            m.KeepOnlyOn(true);
            Assert.IsTrue(m.Open, "stays open on the ONLINE page");
            m.KeepOnlyOn(false);
            Assert.IsFalse(m.Open, "any other page closes it");
            Assert.IsFalse(m.Confirm(out _), "closing never starts a search");
        }

        // ---------------------------------------------------------------- rewards

        [Test]
        public void RewardSlot_AllCpu_IsNone()
        {
            var cpu = new[] { true, true, true, true };
            Assert.AreEqual(-1, MatchRewardRules.RewardSlot(false, 0, false, false, cpu, null, 4));
        }

        [Test]
        public void RewardSlot_DebugOrTraining_IsNone()
        {
            var cpu = new[] { false, true, true, true };
            Assert.AreEqual(-1, MatchRewardRules.RewardSlot(false, 0, false, true, cpu, null, 4));
            Assert.AreEqual(-1, MatchRewardRules.RewardSlot(false, 0, true, false, cpu, null, 4));
            Assert.AreEqual(0, MatchRewardRules.RewardSlot(false, 0, false, false, cpu, null, 4));
        }

        [Test]
        public void RewardSlot_OnlyP2Human_IsNone()
        {
            var cpu = new[] { true, false, true, true };
            Assert.AreEqual(-1, MatchRewardRules.RewardSlot(false, 0, false, false, cpu, null, 4), "P1's profile did not play");
        }

        [Test]
        public void RewardSlot_FollowsInputRemap()
        {
            var cpu = new[] { true, false, true, false };
            var inputOf = new[] { 1, 0, 3, 2 };   // local player 1 drives sim slot 1
            Assert.AreEqual(1, MatchRewardRules.RewardSlot(false, 0, false, false, cpu, inputOf, 4));
        }

        [Test]
        public void RewardSlot_Online_IsLocalPlayer()
        {
            Assert.AreEqual(2, MatchRewardRules.RewardSlot(true, 2, false, false, new[] { true, true, true, true }, null, 4));
        }

        [Test]
        public void FormatModal_DoesNotOpenDuringActiveSearch()
        {
            var m = new FormatModalState();
            Assert.IsFalse(m.Show(F2, busy: true));
            Assert.IsFalse(m.Open);
        }

        [Test]
        public void FormatModal_UnknownRemembered_FallsBackTo1v1()
        {
            var m = new FormatModalState();
            m.Show(42, busy: false);
            Assert.AreEqual(F1, m.Format);
            Assert.AreEqual(0, m.Index);
        }

        // ---------------------------------------------------------------- save

        [Test]
        public void Save_RoundTrip_KeepsLastFormats()
        {
            var store = new MemoryStore();
            var d = SaveData.Load(store);
            d.settings.lastFormatOffline = Ffa;
            d.settings.lastFormatOnline = F2;
            d.Save(store);
            var r = SaveData.Load(store);
            Assert.AreEqual(Ffa, r.settings.lastFormatOffline);
            Assert.AreEqual(F2, r.settings.lastFormatOnline);
        }

        [Test]
        public void Save_MigrationFromSchema1_DefaultsTo1v1()
        {
            var store = new MemoryStore();
            store.Data[SaveData.Key] = "{\"schemaVersion\":1,\"coins\":42,\"settings\":{\"cpuDifficulty\":3,\"lastFormatOffline\":2,\"lastFormatOnline\":1}}";
            var d = SaveData.Load(store);
            Assert.AreEqual(F1, d.settings.lastFormatOffline);
            Assert.AreEqual(F1, d.settings.lastFormatOnline);
            Assert.AreEqual(3, d.settings.cpuDifficulty);
            Assert.AreEqual(SaveData.SchemaVersion, d.schemaVersion);
        }

        [Test]
        public void Save_UnknownFormat_RepairsTo1v1()
        {
            var store = new MemoryStore();
            store.Data[SaveData.Key] = "{\"schemaVersion\":2,\"settings\":{\"lastFormatOffline\":7,\"lastFormatOnline\":-1}}";
            var d = SaveData.Load(store);
            Assert.AreEqual(F1, d.settings.lastFormatOffline);
            Assert.AreEqual(F1, d.settings.lastFormatOnline);
        }
    }
}
