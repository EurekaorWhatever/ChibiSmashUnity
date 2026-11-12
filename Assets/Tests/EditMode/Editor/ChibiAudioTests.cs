using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Story type: Logic (audio event rules). Evidence: automated EditMode unit test. Gate: BLOCKING.
    /// Pure rules behind ChibiAudio (design/audio/sfx-events.md): event to sound id resolution, hit composition and the
    /// per-defender window, rollback dedupe, confirmed-only match end, countdown, move-kind map and voice stealing.
    /// No AudioListener / AudioSource needed; real time is passed in.
    /// </summary>
    public class ChibiAudioTests
    {
        const float T0 = 10f;
        const int Frame = 200;

        static ChibiSimState NewState(FighterClass c0, FighterClass c1)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = 1, Class0 = c0, Class1 = c1 });
            s.Frame = Frame;
            for (int i = 0; i < 2; i++) { s.F[i].Grounded = true; s.F[i].Shielding = false; s.F[i].Vy = 0; }
            s.HitstopRealTicks = 0;
            return s;
        }

        static ChibiSimState Copy(ChibiSimState s)
        {
            var c = new ChibiSimState();
            c.CopyFrom(s);
            return c;
        }

        static List<string> Ids(List<SfxRequest> r)
        {
            var ids = new List<string>();
            foreach (var x in r) if (x.Id != null) ids.Add(x.Id);
            return ids;
        }

        static List<SfxRequest> Run(ChibiSfxRouter router, ChibiSimState prev, ChibiSimState cur, float now, params ChibiSimEvent[] ev)
        {
            var output = new List<SfxRequest>();
            router.Step(prev, cur, new List<ChibiSimEvent>(ev), 0, now, output);
            return output;
        }

        static ChibiSimEvent Damage(int defender, int hp) => new ChibiSimEvent(ChibiSimEventKind.Damaged, defender, hp);

        [Test]
        public void LightHit_ByKaia_PlaysLightBodyAndKaiaImpact()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            var ids = Ids(Run(new ChibiSfxRouter(), s, Copy(s), T0, Damage(1, 3)));
            CollectionAssert.AreEqual(new[] { "core_hit_light", "kaia_shk" }, ids);
        }

        [Test]
        public void HeavyHit_ByBruna_PlaysHeavyBodyAndBam()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            var router = new ChibiSfxRouter();
            var ids = Ids(Run(router, s, Copy(s), T0, Damage(0, router.HeavyHitHp)));
            CollectionAssert.AreEqual(new[] { "core_hit_heavy", "bruna_bam" }, ids);
        }

        [Test]
        public void ZeroHpTick_PlaysNothing()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            Assert.IsEmpty(Ids(Run(new ChibiSfxRouter(), s, Copy(s), T0, Damage(1, 0))));
        }

        [Test]
        public void HitWindow_SecondLightHitInside60ms_IsDropped_HeavierOneReplacesBody()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            var router = new ChibiSfxRouter();
            Run(router, s, Copy(s), T0, Damage(1, 3));
            var next = Copy(s);
            next.Frame = Frame + 1;
            Assert.IsEmpty(Ids(Run(router, s, next, T0 + 0.03f, Damage(1, 2))), "same-weight hit inside the window");

            next.Frame = Frame + 2;
            var heavier = Run(router, s, next, T0 + 0.04f, Damage(1, router.HeavyHitHp));
            CollectionAssert.AreEqual(new[] { "core_hit_heavy" }, Ids(heavier), "heavier hit replaces only the body");
            Assert.IsTrue(heavier.Exists(r => r.Id == null && r.StopTag == ChibiSfxRouter.BodyTag(1)), "old body stopped");
        }

        [Test]
        public void StringOfHits_AttenuatesWeaponLayer_ToFloor()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            var router = new ChibiSfxRouter();
            SfxRequest weapon = default;
            for (int k = 0; k < 6; k++)
            {
                var cur = Copy(s);
                cur.Frame = Frame + k * 10;
                var r = Run(router, s, cur, T0 + k * 0.1f, Damage(1, 2));
                weapon = r.Find(x => x.Id == "kaia_shk");
            }
            Assert.AreEqual(router.StringFloorDb, weapon.GainDb, 1e-4f);
        }

        [Test]
        public void BlockedHit_PlaysBlockOnly()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Ranged);
            s.F[1].Shielding = true;
            var cur = Copy(s);
            CollectionAssert.AreEqual(new[] { "core_block" }, Ids(Run(new ChibiSfxRouter(), s, cur, T0, Damage(1, 2))));
        }

        [Test]
        public void ProjectileHit_UsesProjectileRow_AndNoExpireSound()
        {
            var s = NewState(FighterClass.Ranged, FighterClass.Heavy);
            var ids = Ids(Run(new ChibiSfxRouter(), s, Copy(s), T0,
                new ChibiSimEvent(ChibiSimEventKind.ProjectileGone, 3, 0, (int)ChibiProjKind.Arrow),
                Damage(1, 3)));
            CollectionAssert.AreEqual(new[] { "core_proj_hit", "ren_zing" }, ids);
        }

        [Test]
        public void ProjectileExpire_WithoutHit_PlaysExpireRow()
        {
            var s = NewState(FighterClass.Magic, FighterClass.Heavy);
            var ids = Ids(Run(new ChibiSfxRouter(), s, Copy(s), T0,
                new ChibiSimEvent(ChibiSimEventKind.ProjectileGone, 0, 0, (int)ChibiProjKind.Fireball)));
            CollectionAssert.AreEqual(new[] { "lyra_boom" }, ids);
        }

        [Test]
        public void PerCharacterLayers_ResolveFromClass()
        {
            var s = NewState(FighterClass.Polearm, FighterClass.Magic);
            var router = new ChibiSfxRouter();
            CollectionAssert.AreEqual(new[] { "core_parry", "taro_ting" },
                Ids(Run(router, s, Copy(s), T0, new ChibiSimEvent(ChibiSimEventKind.Parried, 0))));
            CollectionAssert.AreEqual(new[] { "lyra_shim" },
                Ids(Run(router, s, Copy(s), T0 + 1f, new ChibiSimEvent(ChibiSimEventKind.DashStarted, 1, 1))));
            CollectionAssert.AreEqual(new[] { "core_ult_cinematic", "lyra_ult" },
                Ids(Run(router, s, Copy(s), T0 + 2f, new ChibiSimEvent(ChibiSimEventKind.CinematicStarted, 1))));
        }

        [Test]
        public void Dedupe_DropsReEmissionWithinThreeFrames_KeepsLaterOne()
        {
            var d = new ChibiSfxDedupe();
            Assert.IsFalse(d.Seen(1, 0, 5, 0, 100));
            Assert.IsTrue(d.Seen(1, 0, 5, 0, 102), "rollback re-emission 2 frames later");
            Assert.IsTrue(d.Seen(1, 0, 5, 0, 97), "3 frames earlier");
            Assert.IsFalse(d.Seen(1, 0, 5, 0, 104), "4 frames later is a new event");
            Assert.IsFalse(d.Seen(1, 0, 6, 0, 100), "different value");
            Assert.IsFalse(d.Seen(1, 1, 5, 0, 100), "different fighter");
        }

        [Test]
        public void Router_RollbackReplayOfSameBatch_PlaysNothing()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            var router = new ChibiSfxRouter();
            var ev = new ChibiSimEvent(ChibiSimEventKind.MoveStarted, 0, 1, 0);
            CollectionAssert.AreEqual(new[] { "kaia_shrak" }, Ids(Run(router, s, Copy(s), T0, ev)));
            var shifted = Copy(s);
            shifted.Frame = Frame + 2;
            Assert.IsEmpty(Ids(Run(router, s, shifted, T0 + 0.5f, ev)));
        }

        [Test]
        public void Jump_And_Land_AreDerivedFromGroundedEdges()
        {
            var s = NewState(FighterClass.Heavy, FighterClass.Ranged);
            var air = Copy(s);
            air.F[0].Grounded = false;
            air.F[0].Vy = 1000;
            var router = new ChibiSfxRouter();
            CollectionAssert.AreEqual(new[] { "core_jump" }, Ids(Run(router, s, air, T0)));
            var landed = Copy(s);
            landed.Frame = Frame + 30;
            CollectionAssert.AreEqual(new[] { "core_land", "bruna_armor" }, Ids(Run(router, air, landed, T0 + 0.5f)));
        }

        [Test]
        public void MatchEnd_Online_WaitsForConfirmation()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            var over = Copy(s);
            over.MatchOver = true;
            over.Winner = 0;
            over.F[1].Stocks = 0;
            var router = new ChibiSfxRouter { NeedsConfirm = true };
            var first = Run(router, s, over, T0, new ChibiSimEvent(ChibiSimEventKind.KO, 1));
            Assert.IsEmpty(Ids(first));
            Assert.AreEqual(Frame, router.PendingEndFrame);

            var output = new List<SfxRequest>();
            router.ConfirmEnd(0, true, output);
            CollectionAssert.AreEqual(new[] { "core_final_stock", "core_victory_sting" }, Ids(output));   // v20: replaces core_ko (8.3)
            Assert.AreEqual(SfxDuck.Ko, output[0].Duck);
            Assert.AreEqual(0.9f, output[1].Delay, 1e-6f);

            output.Clear();
            router.ConfirmEnd(0, true, output);
            Assert.IsEmpty(output, "once per match");
        }

        [Test]
        public void MatchEnd_OfflineDoubleKo_IsDrawImmediately()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            var over = Copy(s);
            over.MatchOver = true;
            over.Winner = -1;
            over.F[0].Stocks = 0;
            over.F[1].Stocks = 0;
            var ids = Ids(Run(new ChibiSfxRouter(), s, over, T0,
                new ChibiSimEvent(ChibiSimEventKind.KO, 0), new ChibiSimEvent(ChibiSimEventKind.KO, 1)));
            CollectionAssert.AreEqual(new[] { "core_final_stock", "core_draw_sting" }, ids);
        }

        [Test]
        public void Countdown_TicksThreeTwoOne_ThenGo()
        {
            var router = new ChibiSfxRouter();
            var output = new List<SfxRequest>();
            for (int t = ChibiSimConst.IntroTicks; t >= 0; t--) router.Intro(t, output);
            CollectionAssert.AreEqual(new[] { "core_countdown_tick", "core_countdown_tick", "core_countdown_tick", "core_countdown_go" }, Ids(output));
            Assert.AreEqual(1f, output[0].Pitch, 1e-6f);
            Assert.AreEqual(1.06f, output[1].Pitch, 1e-6f);
            Assert.AreEqual(1.12f, output[2].Pitch, 1e-6f);
        }

        [Test]
        public void MoveKinds_MapToPerCharacterIds()
        {
            var r = new List<SfxRequest>();
            Assert.IsTrue(ChibiSfxMap.Move(FighterClass.Polearm, "launch", false, 0, r));
            CollectionAssert.AreEqual(new[] { "taro_tchk", "taro_whrr" }, Ids(r));
            Assert.AreEqual(-6f, r[1].GainDb, 1e-6f);
            r.Clear();
            ChibiSfxMap.Move(FighterClass.Melee, "jab", true, 0, r);
            ChibiSfxMap.Move(FighterClass.Ranged, "shoot", false, 0, r);
            ChibiSfxMap.Move(FighterClass.Heavy, "uppercut", false, 0, r);
            CollectionAssert.AreEqual(new[] { "kaia_shrak", "ren_twang", "bruna_vroom" }, Ids(r));
            Assert.IsFalse(ChibiSfxMap.Move(FighterClass.Magic, "moonwalk", false, 0, r));
            foreach (var kind in ChibiSfxMap.MoveKinds)
                for (int c = 0; c < 5; c++)
                    Assert.IsTrue(ChibiSfxMap.Move((FighterClass)c, kind, true, 0, r), kind + " class " + c);
        }

        static SfxVoiceInfo V(SfxBus bus, int pri, float start, string id) =>
            new SfxVoiceInfo { Busy = true, Bus = bus, Pri = pri, Start = start, Id = id };

        [Test]
        public void VoiceSteal_BusFull_StealsLowestPriorityThenOldest()
        {
            var voices = new[]
            {
                V(SfxBus.MOV, 25, 1f, "core_jump"), V(SfxBus.MOV, 20, 3f, "core_land"),
                V(SfxBus.MOV, 20, 2f, "core_land"), V(SfxBus.MOV, 35, 0f, "core_dash"),
                new SfxVoiceInfo(),
            };
            Assert.AreEqual(2, ChibiVoiceSteal.Choose(voices, SfxBus.MOV, 4, "core_double_jump", 2, 25), "pri 20, older of the two");
            Assert.AreEqual(4, ChibiVoiceSteal.Choose(voices, SfxBus.CMB, 12, "kaia_shk", 4, 65), "other bus: free voice");
            Assert.AreEqual(-1, ChibiVoiceSteal.Choose(voices, SfxBus.MOV, 4, "x", 2, 10), "never steal a higher priority");
        }

        [Test]
        public void VoiceSteal_IdMax_StealsOldestOfThatId_AndNeverProtected()
        {
            var voices = new[] { V(SfxBus.CMB, 65, 5f, "kaia_shk"), V(SfxBus.CMB, 65, 4f, "kaia_shk"), new SfxVoiceInfo() };
            Assert.AreEqual(1, ChibiVoiceSteal.Choose(voices, SfxBus.CMB, 12, "kaia_shk", 2, 65));

            var stingers = new[] { V(SfxBus.STG, 100, 0f, "core_ko"), V(SfxBus.STG, 95, 1f, "core_ringout"), V(SfxBus.STG, 90, 2f, "core_ult_cinematic") };
            Assert.AreEqual(-1, ChibiVoiceSteal.Choose(stingers, SfxBus.STG, 3, "core_countdown_tick", 1, 100), "Pri >= 90 is protected");
        }

        [Test]
        public void MetaTable_ParsesGeneratedFormat_AndVariantIds()
        {
            var meta = SfxMetaTable.Parse("# header\nkaia_shk\tCMB\t65\t4\t50\t5\t-6\t4\nbad line\n");
            Assert.AreEqual(1, meta.Count);
            Assert.AreEqual(SfxBus.CMB, meta["kaia_shk"].Bus);
            Assert.AreEqual(50, meta["kaia_shk"].CdMs);
            Assert.AreEqual("kaia_shk", ChibiAudio.VariantId("kaia_shk_03"));
            Assert.AreEqual("taro_whrr_loop", ChibiAudio.VariantId("taro_whrr_loop"));
        }

        [Test]
        public void Cooldown_PerIdAndFighter()
        {
            var cd = new ChibiSfxCooldowns();
            Assert.IsTrue(cd.TryPass("kaia_shk", 0, 1f, 50));
            Assert.IsFalse(cd.TryPass("kaia_shk", 0, 1.03f, 50));
            Assert.IsTrue(cd.TryPass("kaia_shk", 1, 1.03f, 50), "other fighter");
            Assert.IsTrue(cd.TryPass("kaia_shk", 0, 1.06f, 50));
        }

        // ------------------------------------------------------------------ rules v20 stocks loop (sfx-events.md 8.3)

        /// <summary>prev -> cur where fighter <paramref name="i"/> lost a stock (KosTaken + 1, Stocks - 1).</summary>
        static ChibiSimState AfterKo(ChibiSimState prev, int i)
        {
            var cur = Copy(prev);
            cur.F[i].KosTaken = prev.F[i].KosTaken + 1;
            if (!cur.Training) cur.F[i].Stocks = prev.F[i].Stocks - 1;
            cur.F[i].OutTicks = ChibiPercentConst.RespawnDelay;
            return cur;
        }

        [Test]
        public void StockLost_NotLast_PlaysKoBlastWithStockKoDuck()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            var output = Run(new ChibiSfxRouter(), s, AfterKo(s, 1), T0);
            CollectionAssert.AreEqual(new[] { "core_ko_blast" }, Ids(output));
            Assert.AreEqual(SfxDuck.StockKo, output[0].Duck);
        }

        [Test]
        public void StockLost_LeavingLastStock_AddsDelayedWarning()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            s.F[0].Stocks = 2;
            s.F[0].KosTaken = 1;
            var router = new ChibiSfxRouter();
            var output = Run(router, s, AfterKo(s, 0), T0);
            CollectionAssert.AreEqual(new[] { "core_ko_blast", "core_last_stock" }, Ids(output));
            Assert.AreEqual(router.LastStockDelaySec, output[1].Delay, 1e-6f);
        }

        [Test]
        public void FinalStockLost_DerivedStepPlaysNothing_MatchEndOwnsIt()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            s.F[1].Stocks = 1;
            s.F[1].KosTaken = 2;
            Assert.IsEmpty(Ids(Run(new ChibiSfxRouter(), s, AfterKo(s, 1), T0)), "core_final_stock comes from the (confirmed) match end");
        }

        [Test]
        public void TrainingKo_PlaysKoBlast_NoLastStockWarning()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            s.Training = true;
            s.F[1].Stocks = 1;   // frozen in training
            CollectionAssert.AreEqual(new[] { "core_ko_blast" }, Ids(Run(new ChibiSfxRouter(), s, AfterKo(s, 1), T0)));
        }

        [Test]
        public void StockKo_ReShownByRollbackFramesLater_PlaysOnce()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            var router = new ChibiSfxRouter();
            var ko = AfterKo(s, 1);
            CollectionAssert.AreEqual(new[] { "core_ko_blast" }, Ids(Run(router, s, ko, T0)));
            var reShown = Copy(ko);
            reShown.Frame = Frame + 6;   // the re-simulation put the same KO six frames later (beyond the 3-frame drift)
            Assert.IsEmpty(Ids(Run(router, s, reShown, T0 + 0.1f)));
        }

        [Test]
        public void RespawnHoverStarts_PlaysChimeOnce()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            s.F[0].KosTaken = 1;
            var hover = Copy(s);
            hover.F[0].HoverTicks = ChibiPercentConst.RespawnHoverMax;
            var router = new ChibiSfxRouter();
            CollectionAssert.AreEqual(new[] { "core_respawn_chime" }, Ids(Run(router, s, hover, T0)));
            Assert.IsEmpty(Ids(Run(router, s, hover, T0 + 0.05f)), "rollback re-show of the same respawn");
        }

        [Test]
        public void HeavyLaunch_AtHighPercent_PlaysWhoosh_LowPercentDoesNot()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            var router = new ChibiSfxRouter();
            s.F[1].PctM = router.HeavyLaunchPctM;
            var launched = Copy(s);
            launched.F[1].PctM = s.F[1].PctM + 12_000;
            launched.F[1].Vx = router.HeavyLaunchSub;
            launched.F[1].Vy = router.HeavyLaunchSub / 2;
            var output = Run(router, s, launched, T0);
            CollectionAssert.AreEqual(new[] { "core_launch_heavy" }, Ids(output));
            Assert.Less(output[0].Pitch, 1f, "stronger launch, lower pitch");

            var low = NewState(FighterClass.Melee, FighterClass.Heavy);
            var lowHit = Copy(low);
            lowHit.F[1].PctM = 12_000;
            lowHit.F[1].Vx = router.HeavyLaunchSub * 2;
            Assert.IsEmpty(Ids(Run(new ChibiSfxRouter(), low, lowHit, T0)), "low percent: no heavy launch");
        }

        [Test]
        public void RingOutEvent_V20_PlaysNothing()
        {
            var s = NewState(FighterClass.Melee, FighterClass.Heavy);
            Assert.IsEmpty(Ids(Run(new ChibiSfxRouter(), s, Copy(s), T0, new ChibiSimEvent(ChibiSimEventKind.RingOut, 0))));
        }

        // ------------------------------------------------------------------ stage audio (sfx-events.md 8.1 / 8.2)

        static List<SfxRequest> Haz(ChibiStageSfx t, int hazard, int count, int phase, int elapsed, int arg)
        {
            var o = new List<SfxRequest>();
            t.Step(hazard, count, phase, elapsed, arg, o);
            return o;
        }

        [Test]
        public void AmbienceId_DesignStagesHaveBeds_ClassicStagesNone()
        {
            Assert.AreEqual("stage_mountain_amb", ChibiStageSfx.AmbienceId(ChibiStageTable.Mountain));
            Assert.AreEqual("stage_plain_amb", ChibiStageSfx.AmbienceId(ChibiStageTable.Plain));
            Assert.AreEqual("stage_volcano_amb", ChibiStageSfx.AmbienceId(ChibiStageTable.Volcano));
            Assert.AreEqual("stage_birdlair_amb", ChibiStageSfx.AmbienceId(ChibiStageTable.BirdLair));
            Assert.AreEqual("stage_ocean_amb", ChibiStageSfx.AmbienceId(ChibiStageTable.Ocean));
            Assert.IsNull(ChibiStageSfx.AmbienceId(0));
            var meta = SfxMetaTable.Parse("stage_ocean_amb\tAMB\t10\t1\t0\t0\t-24\t1\n");
            Assert.AreEqual(SfxBus.AMB, meta["stage_ocean_amb"].Bus);
        }

        [Test]
        public void VolcanoHazard_RollbackReShowsPhases_EachSoundPlaysOnce()
        {
            var t = new ChibiStageSfx();
            const int Fire = ChibiStageTable.HazardFire, Both = 2;
            var tele = Haz(t, Fire, 5, ChibiHazardConst.Telegraph, 0, Both);
            CollectionAssert.AreEqual(new[] { "stage_volcano_burst_warn", "stage_volcano_burst_warn" }, Ids(tele));
            Assert.AreEqual(ChibiHazardConst.FireLaneLeftX, tele[0].PanX);
            Assert.AreEqual(ChibiHazardConst.FireLaneRightX, tele[1].PanX);
            Assert.IsEmpty(Haz(t, Fire, 5, ChibiHazardConst.Telegraph, 1, Both), "same phase next frame");
            CollectionAssert.AreEqual(new[] { "stage_volcano_burst", "stage_volcano_burst" },
                Ids(Haz(t, Fire, 5, ChibiHazardConst.PhaseFireErupt, 0, Both)));

            // Rollback: the view re-shows the telegraph, then the eruption again; then an older event number.
            Assert.IsEmpty(Haz(t, Fire, 5, ChibiHazardConst.Telegraph, 74, Both));
            Assert.IsEmpty(Haz(t, Fire, 5, ChibiHazardConst.PhaseFireErupt, 0, Both));
            Assert.IsEmpty(Haz(t, Fire, 4, ChibiHazardConst.PhaseFireErupt, 3, Both));

            CollectionAssert.AreEqual(new[] { "stage_volcano_burst_warn" }, Ids(Haz(t, Fire, 6, ChibiHazardConst.Telegraph, 0, 1)),
                "next event, right lane only");
        }

        [Test]
        public void BirdHazard_BodyPass_SweepsPanAcrossInFlightDirection()
        {
            var t = new ChibiStageSfx();
            const int Bird = ChibiStageTable.HazardBird;
            CollectionAssert.AreEqual(new[] { "stage_birdlair_screech" }, Ids(Haz(t, Bird, 1, ChibiHazardConst.Telegraph, 0, -1)));
            CollectionAssert.AreEqual(new[] { "stage_birdlair_gust" }, Ids(Haz(t, Bird, 1, ChibiHazardConst.PhaseBirdSwipe, 0, -1)));
            Assert.IsEmpty(Haz(t, Bird, 1, ChibiHazardConst.PhaseBirdSwipe, ChibiHazardConst.BirdBodyStart - 1, -1));
            var pass = Haz(t, Bird, 1, ChibiHazardConst.PhaseBirdSwipe, ChibiHazardConst.BirdBodyStart, -1);
            CollectionAssert.AreEqual(new[] { "stage_birdlair_pass" }, Ids(pass));
            Assert.AreEqual(ChibiHazardConst.BirdBodyStartX, pass[0].PanX, "flying left: starts on the right");
            Assert.AreEqual(-ChibiHazardConst.BirdBodyStartX, pass[0].PanEndX);
            Assert.Greater(pass[0].PanSweepSec, 0f);
            Assert.IsEmpty(Haz(t, Bird, 1, ChibiHazardConst.PhaseBirdSwipe, ChibiHazardConst.BirdBodyStart + 1, -1));
        }

        [Test]
        public void WhaleHazard_PhasesMapToCallDescendRise_LatePhaseIsSkipped()
        {
            var t = new ChibiStageSfx();
            const int Whale = ChibiStageTable.HazardWhale;
            CollectionAssert.AreEqual(new[] { "stage_ocean_whale_call" }, Ids(Haz(t, Whale, 1, ChibiHazardConst.Telegraph, 0, 0)));
            CollectionAssert.AreEqual(new[] { "stage_ocean_whale_descend" }, Ids(Haz(t, Whale, 1, ChibiHazardConst.PhaseWhaleDescend, 0, 0)));
            Assert.IsEmpty(Haz(t, Whale, 1, ChibiHazardConst.PhaseWhaleHold, 0, 0));
            CollectionAssert.AreEqual(new[] { "stage_ocean_whale_rise" }, Ids(Haz(t, Whale, 1, ChibiHazardConst.PhaseWhaleRise, 0, 0)));

            var late = new ChibiStageSfx();
            Assert.IsEmpty(Haz(late, Whale, 2, ChibiHazardConst.PhaseWhaleDescend, late.LateTicks + 1, 0), "stage built mid-descent");
            Assert.IsEmpty(Haz(late, Whale, 2, ChibiHazardConst.PhaseWhaleDescend, 0, 0), "and it stays marked");
            Assert.IsEmpty(Haz(t, ChibiStageTable.HazardNone, 9, ChibiHazardConst.Telegraph, 0, 0));
        }
    }
}
