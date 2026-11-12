// Story type: Logic (rules v20 damage percent, knockback and stocks) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Spec: design/gdd/percent-knockback.md (sections 3-4 and 8), lead decisions GL-1 / GL-2 / GL-3 in
// production/session-state/active.md. Thresholds carry >= 15 % margins (spec 8).
using System;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Percent conversion, the knockback formula, weight, hitstun, KO percents and the stock / respawn rules.</summary>
    public class ChibiPercentKnockbackTests
    {
        static readonly ChibiInputPacket N = default;

        // ================================================================== formulas (spec 4)

        [Test]
        public void Pct_ConvertsMicroHpHalfUp()
        {
            Assert.AreEqual(3600, ChibiSimulation.Pct(18_000_000), "18 HP = 3.6 % (AC 1)");
            Assert.AreEqual(200, ChibiSimulation.Pct(1_000_000), "the 1 HP floor = 0.2 %");
            Assert.AreEqual(1, ChibiSimulation.Pct(2_500), "half-up: 2 500 micro-HP = 0.5 milli-percent -> 1");
            Assert.AreEqual(0, ChibiSimulation.Pct(2_499));
            Assert.AreEqual(0, ChibiSimulation.Pct(0));
        }

        [Test]
        public void Pct_BrunaFsmashOnLyra_Is22312()
        {
            // AC 1: Attack 35 x Mult 2.68 x Heavy +20 % = 112.56 HP raw, Lyra DEF 10 -> -1 HP = 111.56 HP -> 22.312 %.
            var bruna = ChibiCombatStats.Create(FighterClass.Heavy, 0);
            var lyra = ChibiCombatStats.Create(FighterClass.Magic, 0);
            var rng = new ChibiRng(NoCritSeed());
            long raw = ChibiCombatStats.RawDamageNano(bruna, 2680, false, ref rng, out bool crit);
            Assert.IsFalse(crit, "fixture: no crit");
            int dmgU = ChibiCombatStats.FinalDamageMicro(raw, ChibiCombatStats.DefenseH(lyra, false), false);
            Assert.AreEqual(22_312, ChibiSimulation.Pct(dmgU));
        }

        [Test]
        public void Weights_FromMaxHp()
        {
            Assert.AreEqual(75, ChibiCombatStats.Create(FighterClass.Magic, 0).Weight, "Lyra");
            Assert.AreEqual(80, ChibiCombatStats.Create(FighterClass.Ranged, 0).Weight, "Ren");
            Assert.AreEqual(100, ChibiCombatStats.Create(FighterClass.Melee, 0).Weight, "Kaia");
            Assert.AreEqual(100, ChibiCombatStats.Create(FighterClass.Polearm, 0).Weight, "Taro");
            Assert.AreEqual(120, ChibiCombatStats.Create(FighterClass.Heavy, 0).Weight, "Bruna");
        }

        [Test]
        public void Knockback_WorkedExample_KaiaFsmashOnKaiaAtZero()
        {
            // Spec 4.7 / AC 3 (stun per GL-3 R2: a strong hit uses KB x 0.4, not the legacy 38).
            const int mag = 187_500;
            Assert.AreEqual(1000, ChibiSimulation.GrowthP(mag));
            Assert.AreEqual(35_000, ChibiSimulation.BaseM(mag, false));
            Assert.AreEqual(115_000, ChibiSimulation.BaseM(mag, true), "ult base bonus 80 000");
            int kb = ChibiSimulation.KnockbackM(10_920, 10_920, 100, mag, false, 1000);
            Assert.AreEqual(62_875, kb);
            long ls = (long)kb * ChibiPercentConst.LsSubPerKb / 1000;
            Assert.AreEqual(96_198, ls);
            Assert.AreEqual(76_958, (int)(ls * 150_000 / mag), "Vx");
            Assert.AreEqual(57_718, (int)(ls * 112_500 / mag), "Vy");
            Assert.AreEqual(952, ChibiSimulation.LaunchGravityP(kb), "LaunchGravP");
            Assert.AreEqual(25, ChibiSimulation.HitstunFor(kb, mag, 38), "strong hit: KB x 0.4 (GL-3 R2)");
            Assert.AreEqual(38, ChibiSimulation.HitstunFromKb(kb, 38), "weak-hit rule would keep the legacy 38");
        }

        [Test]
        public void Knockback_InSim_KaiaFsmashOnKaiaAtZero_MatchesTheWorkedExample()
        {
            var s = Duel(ChibiStageTable.SkyDojo, FighterClass.Melee, 0, -1200, 0);
            int hit = FsmashHit(s);
            Assert.GreaterOrEqual(hit, 0, "fixture: the fsmash connects");
            var v = s.F[1];
            Assert.AreEqual(10_920, v.PctM, "10.92 % (spec 4.1 table)");
            Assert.AreEqual(76_958, v.Vx, "Vx = Dir x 76 958");
            Assert.AreEqual(57_718, v.Vy, "Vy = 57 718");
            Assert.AreEqual(952, v.LaunchGravP);
            Assert.AreEqual(25, v.Stun, "GL-3 R2");
            Assert.AreEqual(2160, v.LaunchDecX, "decay 2700 x 4/5");
            Assert.AreEqual(1620, v.LaunchDecY, "decay 2700 x 3/5");
        }

        [Test]
        public void Knockback_DamageTermCappedAt15Percent()
        {
            int capped = ChibiSimulation.KnockbackM(100_000, 15_000, 100, 187_500, false, 1000);
            Assert.AreEqual(capped, ChibiSimulation.KnockbackM(100_000, 40_000, 100, 187_500, false, 1000), "d above 15 % adds nothing");
            Assert.Less(ChibiSimulation.KnockbackM(100_000, 14_000, 100, 187_500, false, 1000), capped, "below the cap d still counts");
        }

        [Test]
        public void Knockback_WeightOrdering_LighterFliesFarther()
        {
            int Kb(int w) => ChibiSimulation.KnockbackM(80_000, 10_000, w, 187_500, false, 1000);
            Assert.Greater(Kb(75), Kb(80), "Lyra > Ren");
            Assert.Greater(Kb(80), Kb(100), "Ren > Kaia / Taro");
            Assert.Greater(Kb(100), Kb(120), "Kaia / Taro > Bruna");
        }

        [Test]
        public void Knockback_ZonersTimesPoint85_AndCapped()
        {
            int full = ChibiSimulation.KnockbackM(50_000, 10_000, 100, 187_500, false, 1000);
            Assert.AreEqual(full * 850L / 1000, ChibiSimulation.KnockbackM(50_000, 10_000, 100, 187_500, false,
                ChibiSimulation.ClassKbP((int)FighterClass.Magic)), "Lyra x0.85", 1);
            Assert.AreEqual(850, ChibiSimulation.ClassKbP((int)FighterClass.Ranged));
            Assert.AreEqual(1000, ChibiSimulation.ClassKbP((int)FighterClass.Heavy));
            Assert.AreEqual(ChibiPercentConst.KbCapM, ChibiSimulation.KnockbackM(999_900, 50_000, 75, 250_000, true, 1000), "KB cap");
        }

        [Test]
        public void Hitstun_CappedAt90_StrongMin10_WeakKeepsLegacy()
        {
            Assert.AreEqual(90, ChibiSimulation.HitstunFor(ChibiPercentConst.KbCapM, 187_500, 38), "cap 90");
            Assert.AreEqual(90, ChibiSimulation.HitstunFor(ChibiPercentConst.KbCapM, 48_023, 17), "cap 90 (weak)");
            Assert.AreEqual(10, ChibiSimulation.HitstunFor(1_000, 150_000, 30), "strong min 10");
            Assert.AreEqual(17, ChibiSimulation.HitstunFor(20_000, 48_023, 17), "weak: legacy floor");
        }

        [Test]
        public void Jab_AtZeroPercent_KeepsStun17()
        {
            // GL-3 test list / AC 7: weak hits keep the legacy combo timing.
            var s = Duel(ChibiStageTable.SkyDojo, FighterClass.Melee, 0, -1000, 0);
            int pct = s.F[1].PctM;
            for (int t = 0; t < 12 && s.F[1].PctM == pct; t++) Step(s, t == 0 ? Press(ChibiButtons.Light) : N, N);
            Assert.AreEqual(3600, s.F[1].PctM, "Kaia jab on Kaia: 3.6 %");
            Assert.AreEqual(17, s.F[1].Stun, "legacy stun 17");
        }

        [Test]
        public void LinkHit_At300Percent_IsSetKnockback()
        {
            // AC 8: multi-hit link hits keep (Dir x 13 500, 9 000) and stun = rehit + 4 at any percent.
            var row = MovesetKit.Row(FighterClass.Melee, "kaia_nspec");
            var s = MovesetKit.Arena(FighterClass.Melee, FighterClass.Heavy, 0, 1000);
            s.F[1].PctM = 300_000;
            s.Rng = new ChibiRng(NoCritSeed());
            MovesetKit.Game(s, MovesetKit.P(ChibiDir.Neutral, ChibiButtons.Special));
            MovesetKit.Idle(s, row.Startup + 1);
            Assert.Greater(s.F[1].PctM, 300_000, "fixture: link hit landed");
            Assert.AreEqual(ChibiMovesetConst.LinkKbX, s.F[1].Vx, "set knockback X");
            Assert.AreEqual(ChibiMovesetConst.LinkKbY, s.F[1].Vy, "set knockback Y");
            Assert.AreEqual(row.Rehit + ChibiMovesetConst.LinkStunPad, s.F[1].Stun, "stun = rehit + 4");
        }

        [Test]
        public void Lifesteal_LowersAttackerPercent_FloorZero()
        {
            var s = Duel(ChibiStageTable.SkyDojo, FighterClass.Melee, 0, -1000, 0, passive0: 1);   // Blood Edge
            s.F[0].PctM = 100;
            for (int t = 0; t < 12 && s.F[1].PctM == 0; t++) Step(s, t == 0 ? Press(ChibiButtons.Light) : N, N);
            Assert.Greater(s.F[1].PctM, 0, "fixture: hit landed");
            Assert.AreEqual(0, s.F[0].PctM, "0.9 HP heal = 180 milli-percent, clamped at 0 (AC 14)");
        }

        // ================================================================== KO percents (spec 4.8 / AC 4-6, GL-3)

        [TestCase(ChibiStageTable.Mountain)]
        [TestCase(ChibiStageTable.Plain)]
        [TestCase(ChibiStageTable.Volcano)]
        [TestCase(ChibiStageTable.BirdLair)]
        [TestCase(ChibiStageTable.Ocean)]
        public void UnchargedKaiaFsmash_AtZero_NeverKOs_AnyWeight_FromTheTrueWalkableEdge(int stage)
        {
            // GL-3 + lead decision (rules v21): no move KOs anyone at 0 % on any stage, starting 300 milli inside the TRUE
            // outermost walkable edge (the static surface reaching farthest toward +X: MAIN, a floor or a platform).
            OutermostStaticEdge(ChibiStageTable.Get(stage), out int edgeX, out int edgeY);
            foreach (var victim in new[] { FighterClass.Magic, FighterClass.Ranged, FighterClass.Melee, FighterClass.Heavy })
            {
                Assert.IsFalse(FsmashKos(stage, victim, 0, edgeX - 300, edgeY), $"stage {stage}: 0 % {victim} KO'd from the edge ({edgeX}, {edgeY})");
                Assert.IsFalse(FsmashKos(stage, victim, 0, 0), $"stage {stage}: 0 % {victim} KO'd from the centre");
            }
        }

        /// <summary>The static walkable surface whose right end reaches farthest (MAIN, floor, static platforms).</summary>
        static void OutermostStaticEdge(ChibiStageDef st, out int x, out int y)
        {
            x = st.HalfWidth; y = 0;
            if (st.HasFloor && st.FloorHalfW > x) { x = st.FloorHalfW; y = st.FloorY; }
            foreach (var p in st.Platforms)
                if (!p.Moves && p.Cx + p.HalfW > x) { x = p.Cx + p.HalfW; y = p.Top; }
        }

        [Test]
        public void KaiaFsmashOnKaia_Volcano_Ledge_85NoKo_120Ko()
        {
            var st = ChibiStageTable.Get(ChibiStageTable.Volcano);
            int ledgeX = st.HalfWidth - 300;
            Assert.IsFalse(FsmashKos(ChibiStageTable.Volcano, FighterClass.Melee, 85_000, ledgeX), "85 % from the ledge: no KO");
            Assert.IsTrue(FsmashKos(ChibiStageTable.Volcano, FighterClass.Melee, 120_000, ledgeX), "120 % from the ledge: KO");
        }

        [Test]
        public void KaiaFsmash_SkyDojo_Ledge_LighterDiesEarlier()
        {
            // AC 4 / 6 shape on the classic stage (side line 9 000 beyond the ledge), margins >= 15 %.
            var st = ChibiStageTable.Get(ChibiStageTable.SkyDojo);
            int ledgeX = st.HalfWidth - 300;
            Assert.IsFalse(FsmashKos(ChibiStageTable.SkyDojo, FighterClass.Magic, 0, 0), "Lyra 0 % centre: no KO");
            Assert.IsFalse(FsmashKos(ChibiStageTable.SkyDojo, FighterClass.Magic, 60_000, ledgeX), "Lyra 60 % ledge: no KO");
            Assert.IsFalse(FsmashKos(ChibiStageTable.SkyDojo, FighterClass.Melee, 70_000, ledgeX), "Kaia 70 % ledge: no KO");
            Assert.IsTrue(FsmashKos(ChibiStageTable.SkyDojo, FighterClass.Melee, 200_000, ledgeX), "Kaia 200 % ledge: KO");
            Assert.LessOrEqual(MinKoPct(ChibiStageTable.SkyDojo, FighterClass.Magic, ledgeX),
                               MinKoPct(ChibiStageTable.SkyDojo, FighterClass.Heavy, ledgeX), "Lyra dies no later than Bruna");
        }

        // ================================================================== stocks / respawn (spec 3.4, AC 11)

        [Test]
        public void Ko_StockLost_Out60_Hover_ThenInvuln120()
        {
            var s = Duel(ChibiStageTable.SkyDojo, FighterClass.Melee, 0, -3000, 3000);
            s.F[1].PctM = 77_000;
            s.F[1].X = ChibiStageTable.Get(0).BlastX + 1;
            Step(s, N, N);
            var f = s.F[1];
            Assert.AreEqual(2, f.Stocks);
            Assert.AreEqual(1, f.KosTaken);
            Assert.AreEqual(ChibiPercentConst.RespawnDelay, f.OutTicks);
            Assert.IsFalse(f.InPlay);
            for (int k = 0; k < ChibiPercentConst.RespawnDelay; k++) Step(s, N, N);
            Assert.AreEqual(0, f.OutTicks);
            Assert.AreEqual(0, f.PctM, "percent reset");
            Assert.AreEqual(ChibiPercentConst.RespawnHoverMax, f.HoverTicks, "hovering");
            int y = f.Y;
            for (int k = 0; k < 20; k++) Step(s, N, N);
            Assert.AreEqual(y, f.Y, "hover: gravity off");
            // Hover gate: an attack on the hovering fighter does nothing.
            Assert.IsFalse(f.InPlay);
            Step(s, N, Press(ChibiButtons.Shield));
            Assert.AreEqual(0, f.HoverTicks, "any input ends the hover");
            Assert.AreEqual(ChibiPercentConst.RespawnInvuln, f.Invuln, "invuln 120 after the hover");
        }

        [Test]
        public void Hover_EndsByItselfAfter180()
        {
            var s = Duel(ChibiStageTable.SkyDojo, FighterClass.Melee, 0, -3000, 3000);
            s.F[1].X = -(ChibiStageTable.Get(0).BlastX + 1);
            Step(s, N, N);
            for (int k = 0; k < ChibiPercentConst.RespawnDelay + ChibiPercentConst.RespawnHoverMax; k++) Step(s, N, N);
            Assert.AreEqual(0, s.F[1].HoverTicks);
            Assert.Greater(s.F[1].Invuln, ChibiPercentConst.RespawnInvuln - 3, "invuln started when the hover ran out");
        }

        [Test]
        public void Percent_SaturatesAtCap_AfterAHit()
        {
            var s = Duel(ChibiStageTable.SkyDojo, FighterClass.Heavy, 0, -1000, 0);
            s.F[1].PctM = ChibiPercentConst.PctCapM - 100;
            for (int t = 0; t < 12 && s.F[1].PctM < ChibiPercentConst.PctCapM; t++) Step(s, t == 0 ? Press(ChibiButtons.Light) : N, N);
            Assert.AreEqual(ChibiPercentConst.PctCapM, s.F[1].PctM, "999.9 % cap");
        }

        [Test]
        public void Hover_NoInput_EndsExactlyOnTick180_Invuln120()
        {
            var s = Duel(ChibiStageTable.SkyDojo, FighterClass.Melee, 0, -3000, 3000);
            s.F[1].X = ChibiStageTable.Get(0).BlastX + 1;
            Step(s, N, N);
            for (int k = 0; k < ChibiPercentConst.RespawnDelay; k++) Step(s, N, N);
            Assert.AreEqual(ChibiPercentConst.RespawnHoverMax, s.F[1].HoverTicks, "respawned, hovering");
            for (int k = 0; k < ChibiPercentConst.RespawnHoverMax - 1; k++) Step(s, N, N);
            Assert.AreEqual(1, s.F[1].HoverTicks, "still hovering on tick 179");
            Step(s, N, N);
            Assert.AreEqual(0, s.F[1].HoverTicks, "hover over on tick 180");
            Assert.AreEqual(ChibiPercentConst.RespawnInvuln, s.F[1].Invuln, "invuln 120");
        }

        [Test]
        public void HighPercent_NeverKOsByItself()
        {
            var s = Duel(ChibiStageTable.SkyDojo, FighterClass.Melee, 0, -3000, 3000);
            s.F[1].PctM = ChibiPercentConst.PctCapM;
            for (int k = 0; k < 120; k++) Step(s, N, N);
            Assert.AreEqual(ChibiPercentConst.StocksStart, s.F[1].Stocks);
            Assert.IsFalse(s.MatchOver);
        }

        [Test]
        public void LastStock_KO_OpponentWins_TrainingKeepsStocks()
        {
            var s = Duel(ChibiStageTable.SkyDojo, FighterClass.Melee, 0, -3000, 3000);
            s.F[0].Stocks = 1;
            s.F[0].Y = ChibiStageTable.Get(0).KillY - 1;
            Step(s, N, N);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(1, s.Winner);

            var t = Duel(ChibiStageTable.SkyDojo, FighterClass.Melee, 0, -3000, 3000, training: true);
            t.F[0].Stocks = 1;
            t.F[0].Y = ChibiStageTable.Get(0).KillY - 1;
            Step(t, N, N);
            Assert.IsFalse(t.MatchOver, "training: no stock loss");
            Assert.AreEqual(1, t.F[0].Stocks);
        }

        // ================================================================== helpers

        /// <summary>First seed whose first combat draw is not a crit (and not a 10 % proc).</summary>
        static uint NoCritSeed()
        {
            for (uint seed = 1; ; seed++) if (new ChibiRng(seed).Range(1000) >= 200) return seed;
        }

        static ChibiInputPacket Press(ChibiButtons b, ChibiDir d = ChibiDir.Neutral) => ChibiInputPacket.Make(d, b);

        static void Step(ChibiSimState s, ChibiInputPacket a, ChibiInputPacket b) => ChibiTestTime.StepGame(s, a, b);

        /// <summary>Kaia (F[0], facing +1) at x0 and the victim (F[1]) at x1, both standing on the main floor, hazards off.</summary>
        static ChibiSimState Duel(int stage, FighterClass victim, int victimPassive, int x0, int x1, int passive0 = 0, bool training = false,
                                  int y = 0)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig
            {
                Seed = NoCritSeed(), Class0 = FighterClass.Melee, Class1 = victim, Passive0 = passive0, Passive1 = victimPassive,
                Stage = stage, HazardsOff = true, Training = training,
            });
            for (int t = 0; t < 60; t++) ChibiSimulation.Step(s, N, N);
            MovesetKit.Place(s.F[0], x0, y, 1);
            MovesetKit.Place(s.F[1], x1, y, -1);
            for (int t = 0; t < 3; t++) Step(s, N, N);
            s.Rng = new ChibiRng(NoCritSeed());
            return s;
        }

        /// <summary>Uncharged Kaia fsmash (H+F, released at once); returns the hit step index or -1.</summary>
        static int FsmashHit(ChibiSimState s)
        {
            int pct = s.F[1].PctM;
            Step(s, Press(ChibiButtons.Heavy, ChibiDir.E), N);
            for (int t = 1; t < 40; t++)
            {
                Step(s, N, N);
                if (s.F[1].PctM != pct) return t;
            }
            return -1;
        }

        /// <summary>
        /// GL-3 KO definition: the victim crosses an active edge during its tumble (hitstun + 10 ticks) after an uncharged
        /// Kaia fsmash from 1200 behind it toward +X, at <paramref name="pctBefore"/>.
        /// </summary>
        static bool FsmashKos(int stage, FighterClass victim, int pctBefore, int victimX, int surfaceY = 0)
        {
            var s = Duel(stage, victim, 0, victimX - 1200, victimX, y: surfaceY);
            Assert.IsTrue(s.F[0].Grounded && s.F[1].Grounded, $"fixture: both standing on the surface at {surfaceY}");
            s.F[1].PctM = pctBefore;
            int hit = FsmashHit(s);
            Assert.GreaterOrEqual(hit, 0, $"fixture: fsmash connects (stage {stage}, {victim}, x {victimX})");
            int tumble = s.F[1].Stun + 10;
            for (int t = 0; t < tumble && s.F[1].KosTaken == 0; t++) Step(s, N, N);
            return s.F[1].KosTaken > 0;
        }

        /// <summary>Lowest percent (10 % steps) at which the fsmash KOs, 999 when never.</summary>
        static int MinKoPct(int stage, FighterClass victim, int victimX)
        {
            for (int p = 0; p <= 300; p += 10) if (FsmashKos(stage, victim, p * 1000, victimX)) return p;
            return 999;
        }
    }
}
