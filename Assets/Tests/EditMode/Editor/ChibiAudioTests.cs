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
            over.F[1].HpU = 0;
            var router = new ChibiSfxRouter { NeedsConfirm = true };
            var first = Run(router, s, over, T0, new ChibiSimEvent(ChibiSimEventKind.KO, 1));
            Assert.IsEmpty(Ids(first));
            Assert.AreEqual(Frame, router.PendingEndFrame);

            var output = new List<SfxRequest>();
            router.ConfirmEnd(0, true, output);
            CollectionAssert.AreEqual(new[] { "core_ko", "core_victory_sting" }, Ids(output));
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
            over.F[0].HpU = 0;
            over.F[1].HpU = 0;
            var ids = Ids(Run(new ChibiSfxRouter(), s, over, T0,
                new ChibiSimEvent(ChibiSimEventKind.KO, 0), new ChibiSimEvent(ChibiSimEventKind.KO, 1)));
            CollectionAssert.AreEqual(new[] { "core_ko", "core_draw_sting" }, ids);
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
    }
}
