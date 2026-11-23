// Story type: Logic (four players, Phase 2a: Battle Royale + 2v2 rules, RulesVersion 25) -- evidence: automated EditMode
// unit test -- gate: BLOCKING
// Spec: docs/architecture/netcode/four-player-design.md sections 1.3, 1.4, 2.4 (presentMask), 5 (spawns), 6 test 3;
// decisions in section 9; the Phase 1 review's "Must fix in Phase 2" list in four-player-progress.md.
// Every scenario is set up by writing state directly (positions, stocks, percent, timers) and stepping with neutral input,
// so no test depends on move timing or randomness.
using System;
using System.Reflection;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Four-player match rules: friendly fire, eliminations, places, timeout, leavers, grabs, pierce, spawns.</summary>
    public class ChibiFourPlayerRulesTests
    {
        const int SettleTicks = 120;          // fighters spawn airborne at feet Y 775 and land on MAIN well within this
        const int TeamA0B1 = 0b1100;          // TeamOf: fighters 0, 1 on team 0; fighters 2, 3 on team 1
        const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

        static ChibiMatchConfig Cfg(int players, ChibiMatchMode mode = ChibiMatchMode.FreeForAll, int teamOf = 0,
                                    int stage = ChibiStageTable.SkyDojo, FighterClass all = FighterClass.Melee) =>
            new ChibiMatchConfig
            {
                Seed = 0x2A2Au, PlayerCount = players, Mode = mode, TeamOf = teamOf, Stage = stage, HazardsOff = true,
                Class0 = all, Class1 = all, Class2 = all, Class3 = all,
            };

        static ChibiSimState New(in ChibiMatchConfig cfg)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            return s;
        }

        static void Neutral(ChibiSimState s, int ticks)
        {
            Span<ChibiInputPacket> none = stackalloc ChibiInputPacket[ChibiSimState.MaxFighters];
            for (int t = 0; t < ticks; t++) ChibiSimulation.Step(s, none);
        }

        /// <summary>Puts fighter <paramref name="i"/> past the side KO line (Sky Dojo: every edge KOs) for the next step.</summary>
        static void PushOut(ChibiSimState s, int i)
        {
            var f = s.F[i];
            f.X = ChibiStageTable.Get(s.Stage).BlastX + 2000; f.Y = 3000; f.Vx = 0; f.Vy = 0; f.Grounded = false;
        }

        static void SetStocks(ChibiSimState s, int stocks)
        {
            for (int i = 0; i < s.PlayerCount; i++) s.F[i].Stocks = stocks;
        }

        /// <summary>Same as the Phase 1 hit test helper: a still shot from <paramref name="owner"/>, spawned last step.</summary>
        static ChibiProjectileSim Shot(ChibiSimState s, int owner, int x, int y, int flags)
        {
            for (int n = 0; n < s.Proj.Length; n++)
            {
                var p = s.Proj[n];
                if (p.Active) continue;
                p.Active = true; p.Owner = owner; p.X = x; p.Y = y; p.Vx = 0; p.Vy = 0; p.XRem = 0; p.YRem = 0;
                p.Life = 10; p.Mult = 1000; p.KbX = 22500; p.KbY = 18000; p.Stun = 17;
                p.Kind = ChibiProjKind.Arrow; p.Flags = flags; p.Radius = 250; p.GrowPerTick = 0; p.Speed = 0;
                p.ReturnTicks = 0; p.ReturnMult = 0; p.SpawnFrame = s.GameTicks - 1; p.HitMask = 0;
                p.SrcSlot = -1; p.Bounces = 0; p.Rearm = 0; p.Reflects = 0;
                return p;
            }
            Assert.Fail("precondition: no free projectile slot");
            return null;
        }

        static int CenterY(ChibiFighterSim f) => f.Y + ChibiSimConst.CenterOffY;

        // ================================================================== friendly fire (section 1.3, decision 9.1)

        [Test]
        public void Team_FriendlyFireOff_ProjectileSkipsTeammate_HitsEnemy()
        {
            var s = New(Cfg(4, ChibiMatchMode.Team, TeamA0B1));
            Neutral(s, SettleTicks);
            Shot(s, 0, s.F[1].X, CenterY(s.F[1]), 0);   // fighter 1 is fighter 0's teammate
            Shot(s, 0, s.F[2].X, CenterY(s.F[2]), 0);   // fighter 2 is an enemy
            Neutral(s, 1);
            Assert.AreEqual(0, s.F[1].PctM, "a teammate's shot does not hit");
            Assert.Greater(s.F[2].PctM, 0, "the same shot hits an enemy");
            Assert.AreEqual(0, s.F[2].LastHitBy, "hit attribution");
        }

        /// <summary>One Light swing of fighter 0 at a fighter standing in front of it; returns that fighter's percent.</summary>
        static int SwingAt(ChibiMatchMode mode, int victim)
        {
            var s = New(Cfg(4, mode, TeamA0B1));
            Neutral(s, SettleTicks);
            for (int i = 0; i < 4; i++) { s.F[i].X = -9000 + 6000 * i; s.F[i].Vx = 0; }   // far apart ...
            s.F[0].X = 0; s.F[0].Facing = 1;
            s.F[victim].X = 700; s.F[victim].Facing = -1;                                   // ... except the victim
            Span<ChibiInputPacket> inputs = stackalloc ChibiInputPacket[ChibiSimState.MaxFighters];
            inputs[0] = ChibiInputPacket.Make(0, ChibiButtons.Light);
            ChibiSimulation.Step(s, inputs);
            Neutral(s, 40);
            return s.F[victim].PctM;
        }

        [Test]
        public void Team_FriendlyFireOff_MeleeSkipsTeammate_SameSwingHitsInFreeForAll()
        {
            Assert.Greater(SwingAt(ChibiMatchMode.FreeForAll, 1), 0, "precondition: the swing connects in FFA");
            Assert.AreEqual(0, SwingAt(ChibiMatchMode.Team, 1), "Team: the swing passes through the teammate");
            Assert.Greater(SwingAt(ChibiMatchMode.Team, 2), 0, "Team: the swing hits an enemy");
        }

        // ================================================================== eliminations and places (section 1.4)

        [Test]
        public void FreeForAll_EliminationOrder_GivesPlaces_LastStandingWins()
        {
            var s = New(Cfg(4));
            SetStocks(s, 1);
            PushOut(s, 2); Neutral(s, 1);
            Assert.AreEqual(4, s.Place[2], "first out: place 4");
            Assert.IsFalse(s.MatchOver);
            PushOut(s, 0); Neutral(s, 1);
            Assert.AreEqual(3, s.Place[0], "second out: place 3");
            Assert.IsFalse(s.MatchOver);
            PushOut(s, 1); Neutral(s, 1);
            Assert.IsTrue(s.MatchOver, "one fighter left");
            Assert.AreEqual(2, s.Place[1]); Assert.AreEqual(1, s.Place[3]);
            Assert.AreEqual(3, s.Winner); Assert.AreEqual(1 << 3, s.WinnerMask);
            Assert.AreEqual(0, s.F[2].Stocks, "an eliminated fighter stays out");
            Assert.Greater(s.F[2].OutTicks, 0, "an eliminated fighter is never respawned");
        }

        [Test]
        public void FreeForAll_EliminatedFighter_IsNeverRespawned()
        {
            var s = New(Cfg(3));
            SetStocks(s, 1);
            PushOut(s, 2); Neutral(s, 1);
            Neutral(s, ChibiPercentConst.RespawnDelay * 3);
            Assert.IsFalse(s.F[2].InPlay, "still out after three respawn delays");
            Assert.AreEqual(3, s.Place[2]);
        }

        [Test]
        public void FreeForAll_SameTickLastKOs_ShareFirst_Draw()
        {
            var s = New(Cfg(3));
            SetStocks(s, 1);
            PushOut(s, 2); Neutral(s, 1);
            PushOut(s, 0); PushOut(s, 1); Neutral(s, 1);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(-1, s.Winner, "draw");
            Assert.AreEqual(0, s.WinnerMask, "draw: no winner bits");
            Assert.AreEqual(1, s.Place[0]); Assert.AreEqual(1, s.Place[1]);
            Assert.AreEqual(3, s.Place[2], "the earlier elimination keeps its place");
        }

        [Test]
        public void FreeForAll_TwoOutOnTheSameTick_SharePlace()
        {
            var s = New(Cfg(4));
            SetStocks(s, 1);
            PushOut(s, 1); PushOut(s, 3); Neutral(s, 1);
            Assert.AreEqual(3, s.Place[1]); Assert.AreEqual(3, s.Place[3]);
            Assert.IsFalse(s.MatchOver, "two fighters left");
        }

        [Test]
        public void Team_BothEnemiesOut_TeamWins_TeammateFightsOn()
        {
            var s = New(Cfg(4, ChibiMatchMode.Team, TeamA0B1));
            SetStocks(s, 1);
            PushOut(s, 0); Neutral(s, 1);
            Assert.IsFalse(s.MatchOver, "the teammate fights on");
            PushOut(s, 2); Neutral(s, 1);
            Assert.IsFalse(s.MatchOver, "both teams still have a fighter");
            PushOut(s, 3); Neutral(s, 1);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(0b0011, s.WinnerMask, "team 0 wins, both members (also the eliminated one)");
            Assert.AreEqual(0, s.Winner, "lowest index of the winning team");
            CollectionAssert.AreEqual(new[] { 1, 1, 2, 2 }, s.Place);
        }

        [Test]
        public void Team_SameTickLastKOs_Draw_EveryMemberSharesFirst()
        {
            var s = New(Cfg(4, ChibiMatchMode.Team, TeamA0B1));
            SetStocks(s, 1);
            PushOut(s, 0); Neutral(s, 1);
            PushOut(s, 1); PushOut(s, 2); PushOut(s, 3); Neutral(s, 1);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(-1, s.Winner); Assert.AreEqual(0, s.WinnerMask);
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 1 }, s.Place, "a team draw shows no elimination places");
        }

        // ================================================================== timeout (section 1.4)

        static void RunOutTheClock(ChibiSimState s)
        {
            s.MatchClock = ChibiSimConst.MatchTicks - 1;
            Neutral(s, 1);
            Assert.IsTrue(s.MatchOver, "the timer ended the match");
        }

        [Test]
        public void FreeForAll_Timeout_RanksByStocksThenPercent()
        {
            var s = New(Cfg(4));
            s.F[0].Stocks = 3; s.F[0].PctM = 90000;
            s.F[1].Stocks = 2; s.F[1].PctM = 1000;
            s.F[2].Stocks = 2; s.F[2].PctM = 5000;
            s.F[3].Stocks = 1; s.F[3].PctM = 0;
            RunOutTheClock(s);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, s.Place);
            Assert.AreEqual(0, s.Winner); Assert.AreEqual(1, s.WinnerMask);
        }

        [Test]
        public void FreeForAll_Timeout_TiedLeaders_Draw_ShareFirst()
        {
            var s = New(Cfg(3));
            s.F[0].Stocks = 3; s.F[0].PctM = 2000;
            s.F[1].Stocks = 3; s.F[1].PctM = 2000;
            s.F[2].Stocks = 1; s.F[2].PctM = 0;
            RunOutTheClock(s);
            Assert.AreEqual(-1, s.Winner); Assert.AreEqual(0, s.WinnerMask);
            Assert.AreEqual(1, s.Place[0]); Assert.AreEqual(1, s.Place[1]); Assert.AreEqual(3, s.Place[2]);
        }

        [Test]
        public void Team_Timeout_CountsOnlyMembersStillIn()
        {
            var s = New(Cfg(4, ChibiMatchMode.Team, TeamA0B1));
            s.F[1].Stocks = 1;
            PushOut(s, 1); Neutral(s, 1);                       // fighter 1 eliminated ...
            Assert.AreEqual(4, s.Place[1]);
            s.F[1].PctM = 50000;                                // ... with a big frozen percent that must not count
            s.F[0].Stocks = 2; s.F[0].PctM = 1000;
            s.F[2].Stocks = 1; s.F[2].PctM = 3000;
            s.F[3].Stocks = 1; s.F[3].PctM = 0;
            RunOutTheClock(s);                                  // stocks 2 vs 2, percent 1000 vs 3000
            Assert.AreEqual(0b0011, s.WinnerMask, "team 0 wins on percent of the members still in");
        }

        [Test]
        public void Team_Timeout_EqualStocksAndPercent_Draw()
        {
            var s = New(Cfg(4, ChibiMatchMode.Team, TeamA0B1));
            s.F[0].PctM = 1000; s.F[3].PctM = 1000;
            RunOutTheClock(s);
            Assert.AreEqual(-1, s.Winner); Assert.AreEqual(0, s.WinnerMask);
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 1 }, s.Place);
        }

        // ================================================================== leavers (presentMask, section 2.4)

        static void Leave(ChibiSimState s, int mask)
        {
            Span<ChibiInputPacket> none = stackalloc ChibiInputPacket[ChibiSimState.MaxFighters];
            ChibiSimulation.StepPresent(s, none, mask);
        }

        [Test]
        public void Leaver_IsEliminatedOnThatTick_MatchContinues()
        {
            var s = New(Cfg(4));
            Neutral(s, 10);
            Assert.AreEqual(0b1111, s.PresentMask, "Init: everyone present");
            Leave(s, 0b1011);
            Assert.AreEqual(0b1011, s.PresentMask);
            Assert.AreEqual(0, s.F[2].Stocks, "all stocks gone");
            Assert.IsFalse(s.F[2].InPlay);
            Assert.AreEqual(4, s.Place[2], "placed on the leave tick");
            Assert.AreEqual(0, s.F[2].KosTaken, "a leave is not a KO");
            Assert.IsFalse(s.MatchOver);
            Leave(s, 0b1111);
            Assert.AreEqual(0b1011, s.PresentMask, "a cleared bit never comes back");
        }

        [Test]
        public void Leaver_1v1_OtherPlayerWins()
        {
            var s = New(Cfg(2));
            Neutral(s, 10);
            Leave(s, 0b01);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(0, s.Winner); Assert.AreEqual(1, s.WinnerMask);
        }

        [Test]
        public void Leaver_Team_TeammatePlaysOn_SecondLeaveEndsIt()
        {
            var s = New(Cfg(4, ChibiMatchMode.Team, TeamA0B1));
            Neutral(s, 10);
            Leave(s, 0b1110);
            Assert.IsFalse(s.MatchOver, "1v2 goes on");
            Leave(s, 0b1100);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(0b1100, s.WinnerMask, "team 1 wins");
        }

        [Test]
        public void Leaver_SameTickReplay_IsDeterministic_AndChangesTheChecksum()
        {
            uint Run(int leaveTick)
            {
                var s = New(Cfg(4));
                Span<ChibiInputPacket> none = stackalloc ChibiInputPacket[ChibiSimState.MaxFighters];
                for (int t = 0; t < 60; t++)
                    ChibiSimulation.StepPresent(s, none, t >= leaveTick ? 0b0111 : 0b1111);
                return s.ComputeChecksum();
            }
            Assert.AreEqual(Run(20), Run(20), "same leave tick, same state");
            Assert.AreNotEqual(Run(20), Run(21), "the leave tick is part of the state");
            Assert.AreNotEqual(Run(20), Run(1000), "a leave changes the checksum");
        }

        [Test]
        public void Leaver_WhileHolding_ReleasesTheVictim()
        {
            var s = New(Cfg(4));
            Hold(s, 3, 1);
            Leave(s, 0b0111);
            Assert.AreEqual(-1, s.F[1].HeldBy, "the victim is free");
            Assert.AreEqual(-1, s.F[3].HoldTarget);
        }

        // ================================================================== grabs and holds (must-fix 1 and 6)

        static void Hold(ChibiSimState s, int holder, int victim) =>
            typeof(ChibiSimulation).GetMethod("Hold", Priv).Invoke(null, new object[] { s, holder, victim });

        static void MultiGrabs(ChibiSimState s) =>
            typeof(ChibiSimulation).GetMethod("MultiGrabs", Priv).Invoke(null, new object[] { s });

        /// <summary>Fighter <paramref name="i"/> (Heavy) on grab frame 0 this tick (GrabStartsNow), at x facing <paramref name="facing"/>.</summary>
        static void Grabber(ChibiSimState s, int i, int x, int facing)
        {
            var f = s.F[i];
            f.X = x; f.Y = 0; f.Facing = facing; f.MoveSlot = 2; f.MoveTick = 1; f.MovePhase = 0;
        }

        static void Stand(ChibiSimState s, int i, int x)
        {
            var f = s.F[i];
            f.X = x; f.Y = 0; f.MoveSlot = -1; f.MoveTick = 0; f.MovePhase = 0;
        }

        /// <summary>
        /// A grabs B while C grabs A on the same tick. Seats a, b, c are permuted by the caller; the outcome must not depend
        /// on them: C holds A, A's grab whiffs, B is free.
        /// </summary>
        static void GrabChain(int a, int b, int c, int far)
        {
            var s = New(Cfg(4, all: FighterClass.Heavy));
            Grabber(s, a, 0, 1);       // A's box covers B at 800
            Stand(s, b, 800);
            Grabber(s, c, -1200, 1);   // C's box (-1050..250) covers A only; A's box (150..1450) covers B only
            Stand(s, far, -9000);
            MultiGrabs(s);
            string seats = $"seats A={a} B={b} C={c}";
            Assert.AreEqual(-1, s.F[a].HoldTarget, "A was grabbed on the same tick: its grab whiffs, " + seats);
            Assert.AreEqual(-1, s.F[b].HeldBy, "B stays free, " + seats);
            Assert.AreEqual(0, s.F[b].Stun, "B was never grabbed (no grab stun), " + seats);
            Assert.AreEqual(c, s.F[a].HeldBy, "C holds A, " + seats);
            Assert.AreEqual(a, s.F[c].HoldTarget, seats);
        }

        [Test]
        public void GrabChain_SameTick_OutcomeDoesNotDependOnSeatOrder()
        {
            GrabChain(0, 1, 2, 3);
            GrabChain(1, 2, 0, 3);
            GrabChain(2, 3, 1, 0);
        }

        [Test]
        public void GrabCycle_SameTick_AllWhiff()
        {
            var s = New(Cfg(3, all: FighterClass.Heavy));
            // 0 at 0 facing right grabs 1 at 800; 1 facing right grabs 2 at 1600; 2 facing left covers 1 and 0 and picks 0.
            Grabber(s, 0, 0, 1); Grabber(s, 1, 800, 1); Grabber(s, 2, 1600, -1);
            MultiGrabs(s);
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(-1, s.F[i].HoldTarget, $"F[{i}] whiffs");
                Assert.AreEqual(-1, s.F[i].HeldBy, $"F[{i}] is not held");
            }
        }

        [Test]
        public void TwoGrabsOnOneVictim_LowerIndexWins()
        {
            var s = New(Cfg(3, all: FighterClass.Heavy));
            Grabber(s, 2, -1000, 1); Stand(s, 1, 0); Grabber(s, 0, 1000, -1);   // both boxes cover only fighter 1
            MultiGrabs(s);
            Assert.AreEqual(0, s.F[1].HeldBy);
            Assert.AreEqual(-1, s.F[2].HoldTarget, "the higher index whiffs");
        }

        [Test]
        public void GrabbingAHolder_FreesItsVictim()
        {
            var s = New(Cfg(3, all: FighterClass.Heavy));
            Stand(s, 0, 0); Stand(s, 1, 800);
            Hold(s, 0, 1);                       // 0 holds 1 from an earlier tick
            Grabber(s, 2, -800, 1);              // 2 grabs the holder
            MultiGrabs(s);
            Assert.AreEqual(2, s.F[0].HeldBy, "the holder is grabbed");
            Assert.AreEqual(-1, s.F[1].HeldBy, "its victim is freed by the grab's stun");
            Assert.AreEqual(-1, s.F[0].HoldTarget);
        }

        [Test]
        public void HoldSteal_ClearsTheOldHoldersProgram_NoSlamOnTheStolenVictim()
        {
            var s = New(Cfg(3, all: FighterClass.Heavy));
            var h = s.F[0];
            h.MoveSlot = 2; h.MovePhase = 1; h.ProgTarget = 1;
            Hold(s, 0, 1);
            Hold(s, 2, 1);                       // fighter 2 (e.g. a Polearm Kick) takes the victim over
            Assert.AreEqual(2, s.F[1].HeldBy); Assert.AreEqual(1, s.F[2].HoldTarget);
            Assert.AreEqual(-1, h.HoldTarget, "the old holder lost the victim");
            Assert.AreEqual(-1, h.ProgTarget, "and its program victim");
            var probe = typeof(ChibiSimulation).GetMethod("ProgramProbe", Priv);
            const int PGrab = 2;
            object hit = probe.Invoke(null, new object[] { s, 0, 1, PGrab, ChibiSimConst.GrabReleaseFrame });
            Assert.IsFalse((bool)hit.GetType().GetField("Valid").GetValue(hit), "the old holder's slam no longer hits fighter 1");
        }

        // ================================================================== pierce shots (must-fix 7)

        [Test]
        public void PierceShot_VictimItAlreadyHit_CanStillDestroyIt()
        {
            var s = New(Cfg(3));
            Neutral(s, SettleTicks);
            s.F[1].GuardTicks = 100;                                         // Spin Guard aura destroys shots in 1.6
            var p = Shot(s, 0, s.F[1].X, CenterY(s.F[1]),
                         ChibiProjectileSim.FPierce | ChibiProjectileSim.FDestructible | ChibiProjectileSim.FHasHit);
            p.HitMask = 1 << 1;                                              // it already hit fighter 1
            Neutral(s, 1);
            Assert.IsFalse(p.Active, "fighter 1 still destroys the pierce shot that hit it");
        }

        [Test]
        public void PierceShot_DoesNotHitTheSameVictimTwice_HitsTheNextEnemy()
        {
            var s = New(Cfg(3));
            Neutral(s, SettleTicks);
            var p = Shot(s, 0, s.F[1].X, CenterY(s.F[1]), ChibiProjectileSim.FPierce | ChibiProjectileSim.FHasHit);
            p.HitMask = 1 << 1;
            Neutral(s, 1);
            Assert.AreEqual(0, s.F[1].PctM, "no second hit on fighter 1");
            Assert.IsTrue(p.Active, "the shot flies on");
            p.X = s.F[2].X; p.Y = CenterY(s.F[2]);
            Neutral(s, 1);
            Assert.Greater(s.F[2].PctM, 0, "it still hits an enemy it has not hit");
            Assert.AreEqual(0b110, p.HitMask);
        }

        // ================================================================== respawn attribution (must-fix 3)

        [Test]
        public void LastHitBy_IsClearedOnRespawn()
        {
            var s = New(Cfg(3));
            s.F[1].LastHitBy = 2;
            PushOut(s, 1); Neutral(s, 1);
            Assert.AreEqual(2, s.F[1].LastHitBy, "kept through the KO (KO credit can read it)");
            Neutral(s, ChibiPercentConst.RespawnDelay + 2);
            Assert.AreEqual(-1, s.F[1].LastHitBy, "a new stock starts with no attacker");
        }

        // ================================================================== spawns (section 5)

        [Test]
        public void Spawns_1v1_KeepPlusMinusSpawnX()
        {
            for (int stage = 0; stage < ChibiStageTable.Count; stage++)
            {
                var st = ChibiStageTable.Get(stage);
                ChibiSimulation.SpawnPoint(stage, 2, 0, out int x0, out int y0, out int f0);
                ChibiSimulation.SpawnPoint(stage, 2, 1, out int x1, out int y1, out int f1);
                Assert.AreEqual(-st.SpawnX, x0, $"stage {stage}"); Assert.AreEqual(st.SpawnX, x1, $"stage {stage}");
                Assert.AreEqual(st.SpawnY, y0); Assert.AreEqual(st.SpawnY, y1);
                Assert.AreEqual(1, f0); Assert.AreEqual(-1, f1);
            }
        }

        [Test]
        public void Spawns_3And4Players_DistinctFacingCentre_LandOnMain_AllStages()
        {
            for (int stage = 0; stage < ChibiStageTable.Count; stage++)
            for (int players = 3; players <= 4; players++)
            {
                string at = $"stage {stage}, {players} players";
                var st = ChibiStageTable.Get(stage);
                var s = New(Cfg(players, stage: stage));
                for (int i = 0; i < players; i++)
                {
                    var f = s.F[i];
                    if (f.X != 0) Assert.AreEqual(f.X > 0 ? -1 : 1, f.Facing, $"{at}: F[{i}] faces the centre");
                    for (int j = i + 1; j < players; j++)
                        Assert.GreaterOrEqual(Math.Abs(f.X - s.F[j].X), 4 * ChibiSimConst.HurtHalfW, $"{at}: F[{i}] / F[{j}] apart");
                }
                Neutral(s, SettleTicks);
                for (int i = 0; i < players; i++)
                {
                    var f = s.F[i];
                    Assert.IsTrue(f.Grounded, $"{at}: F[{i}] landed");
                    Assert.AreEqual(0, f.Y, $"{at}: F[{i}] stands on MAIN (top 0)");
                    Assert.LessOrEqual(Math.Abs(f.X) + ChibiSimConst.HurtHalfW, st.HalfWidth, $"{at}: F[{i}] fully on MAIN");
                }
            }
        }

        [Test]
        public void Respawn_3Or4Players_SlotsHoverApart()
        {
            var s = New(Cfg(4));
            PushOut(s, 0); PushOut(s, 1); Neutral(s, 1);
            Neutral(s, ChibiPercentConst.RespawnDelay + 1);
            Assert.Greater(s.F[0].HoverTicks, 0, "precondition: hovering");
            Assert.AreNotEqual(s.F[0].X, s.F[1].X, "two fighters KO'd together do not respawn stacked");
        }
    }
}
