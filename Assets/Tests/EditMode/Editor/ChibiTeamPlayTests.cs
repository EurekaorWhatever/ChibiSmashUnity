// Story type: Logic (rules v25 team play, phase 1) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source: user decisions 2026-11-23 (formats 1v1 / FFA 3-4 / 2v2, friendly fire off, leaver eliminated, team win and
// timeout rules, FFA placements); implementation Netcode/ChibiSimulation.Teams.cs.
using System.Collections.Generic;
using System.IO;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    public class ChibiTeamPlayTests
    {
        const uint Seed = 2525;
        static readonly FighterClass[] Classes =
            { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic, FighterClass.Polearm };

        // ================================================================== fixture helpers

        static ChibiMatchConfig Cfg(int format, int count, FighterClass c0, FighterClass c1, FighterClass c2 = FighterClass.Ranged,
                                    FighterClass c3 = FighterClass.Magic, int stage = 0) => new ChibiMatchConfig
        {
            Seed = Seed, Format = format, Count = count, Class0 = c0, Class1 = c1, Class2 = c2, Class3 = c3,
            Stage = stage, HazardsOff = true,
        };

        static ChibiInputPacket[] Inputs() => new ChibiInputPacket[ChibiSimConst.MaxFighters];

        /// <summary>One game step with these inputs (repeated through hitstop / cinematic real ticks).</summary>
        static void Game(ChibiSimState s, ChibiInputPacket[] ins)
        {
            int g0 = s.GameTicks;
            for (int n = 0; n < 64; n++)
            {
                bool over = s.MatchOver;
                ChibiSimulation.Step(s, ins);
                if (over || s.GameTicks != g0) return;
            }
            Assert.Fail("fixture: no game step within 64 real ticks");
        }

        static void Idle(ChibiSimState s, int steps)
        {
            var none = Inputs();
            for (int k = 0; k < steps; k++) Game(s, none);
        }

        /// <summary>Slot 0 presses <paramref name="b"/> (stick <paramref name="dir"/>) for one game step; everyone else idle.</summary>
        static void Press(ChibiSimState s, ChibiButtons b, ChibiDir dir = ChibiDir.Neutral)
        {
            var ins = Inputs();
            ins[0] = ChibiInputPacket.Make(dir, b);
            Game(s, ins);
        }

        /// <summary>Inits a match, lets everyone land, then places the fighters at <paramref name="xs"/> (grounded).</summary>
        static ChibiSimState Arena(ChibiMatchConfig cfg, params int[] xs)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            Idle(s, MovesetKit.Settle);
            for (int i = 0; i < xs.Length; i++) MovesetKit.Place(s.F[i], xs[i], 0, xs[i] <= 0 ? 1 : -1);
            Idle(s, 3);
            return s;
        }

        /// <summary>Puts fighter <paramref name="i"/> past Sky Dojo's side KO line; the next game step KOs it.</summary>
        static void SendOut(ChibiSimState s, int i) => MovesetKit.Place(s.F[i], 30000, 0, 1);

        /// <summary>Deterministic busy input (no gestures): stick and one button change every 6 frames.</summary>
        static ChibiInputPacket Bot(int seed, int f)
        {
            unchecked
            {
                uint h = (uint)(seed * 73856093) ^ (uint)((f / 6) * 19349663);
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                var dir = (ChibiDir)(h % 9);
                var b = (ChibiButtons)(1 << (4 + (int)((h >> 8) % 9)));
                if (((h >> 16) & 3) == 0) b = ChibiButtons.None;
                return ChibiInputPacket.Make(dir, b);
            }
        }

        static void AssertHoldInvariant(ChibiSimState s, string when)
        {
            for (int i = 0; i < s.Count; i++)
            {
                var f = s.F[i];
                if (!f.Held) { Assert.AreEqual(-1, f.HeldBy, when + ": fighter " + i + " not held keeps HeldBy"); continue; }
                Assert.That(f.HeldBy >= 0 && f.HeldBy < s.Count && f.HeldBy != i, when + ": fighter " + i + " HeldBy in range");
                var h = s.F[f.HeldBy];
                Assert.IsTrue(h.Holding && h.HoldTarget == i, when + ": holder " + f.HeldBy + " holds " + i);
            }
        }

        // ================================================================== 1v1 unchanged

        [Test]
        public void OneVsOne_TwoInputStepAndNInputStep_ChecksumsIdenticalEveryFrame()
        {
            foreach (var c in Classes)
            {
                // Arrange
                var cfg = new ChibiMatchConfig { Seed = 31 + (uint)c, Class0 = c, Class1 = Classes[((int)c + 2) % 5], Stage = (int)c % 8 };
                var a = new ChibiSimState(); ChibiSimulation.Init(a, cfg);
                var b = new ChibiSimState(); ChibiSimulation.Init(b, cfg);
                for (int i = 0; i < 2; i++) a.F[i].Ult = b.F[i].Ult = ChibiSimConst.UltMax;
                var ins = new ChibiInputPacket[2];

                for (int f = 0; f < 2400; f++)
                {
                    // Act
                    var p0 = Bot(1, f); var p1 = Bot(2, f);
                    ChibiSimulation.Step(a, p0, p1);
                    ins[0] = p0; ins[1] = p1;
                    ChibiSimulation.Step(b, ins);

                    // Assert
                    if (a.ComputeChecksum() != b.ComputeChecksum()) Assert.Fail($"{c}: N-input step diverged at frame {f}");
                }
                Assert.IsFalse(a.Multi, "a 1v1 state hashes no team-play field");
            }
        }

        [Test]
        public void OneVsOne_DefaultConfig_IsTwoFightersOnOwnTeams_ConfigHashIgnoresTeamFields()
        {
            // Arrange
            var plain = new ChibiMatchConfig { Seed = 5, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy };
            var noisy = plain;
            noisy.Count = 4; noisy.Class2 = FighterClass.Polearm; noisy.Team2 = 1;

            // Act
            var s = new ChibiSimState(); ChibiSimulation.Init(s, plain);

            // Assert
            Assert.AreEqual(2, s.Count);
            Assert.AreEqual(0, s.Team[0]); Assert.AreEqual(1, s.Team[1]);
            Assert.AreEqual(plain.ComputeHash(), noisy.ComputeHash(), "1v1 ignores the team-play fields");
            Assert.AreNotEqual(plain.ComputeHash(), new ChibiMatchConfig { Seed = 5, Format = ChibiMatchConfig.FormatFfa }.ComputeHash());
        }

        // ================================================================== determinism / rollback with 4 fighters

        [TestCase(ChibiMatchConfig.FormatFfa, 4)]
        [TestCase(ChibiMatchConfig.FormatFfa, 3)]
        [TestCase(ChibiMatchConfig.Format2v2, 4)]
        public void FourFighters_SnapshotRestoreResimulate_IdenticalToStraightRun(int format, int count)
        {
            // Arrange
            var cfg = Cfg(format, count, FighterClass.Heavy, FighterClass.Polearm, FighterClass.Ranged, FighterClass.Magic);
            var straight = new ChibiSimState(); ChibiSimulation.Init(straight, cfg);
            var rolled = new ChibiSimState(); ChibiSimulation.Init(rolled, cfg);
            for (int i = 0; i < straight.Count; i++) straight.F[i].Ult = rolled.F[i].Ult = ChibiSimConst.UltMax;
            var snap = new ChibiSimState();
            var good = Inputs(); var wrong = Inputs();

            for (int f = 0; f < 2400; f++)
            {
                for (int i = 0; i < 4; i++) good[i] = Bot(10 + i, f);
                ChibiSimulation.Step(straight, good);
                if (f % 97 == 0)
                {
                    // Act: rollback drill with wrong inputs, restore, re-run the right ones.
                    snap.CopyFrom(rolled);
                    for (int k = 0; k < 8; k++)
                    {
                        for (int i = 0; i < 4; i++) wrong[i] = Bot(90 + i, f + k);
                        ChibiSimulation.Step(rolled, wrong);
                    }
                    rolled.CopyFrom(snap);
                }
                ChibiSimulation.Step(rolled, good);

                // Assert
                if (straight.ComputeChecksum() != rolled.ComputeChecksum())
                    Assert.Fail($"diverged at frame {f}: a team-play field is not restored by CopyFrom");
            }
            Assert.IsTrue(straight.Multi);
            Assert.AreEqual(0, straight.DroppedShots); Assert.AreEqual(0, straight.DroppedSummons);
        }

        [Test]
        public void FourFighters_CopyFrom_CopiesTeamPlayFields_AndChecksumSeesThem()
        {
            // Arrange
            var s = new ChibiSimState(); ChibiSimulation.Init(s, Cfg(ChibiMatchConfig.Format2v2, 4, FighterClass.Melee, FighterClass.Heavy));
            s.Placement[2] = 3; s.DroppedMask = 8; s.F[3].HoldTarget = 1; s.F[1].HeldBy = 3; s.Proj[0].Active = true; s.Proj[0].Target = 2;
            s.Minions[0].Active = true; s.Minions[0].Target = 3; s.Clouds[0].Active = true; s.Clouds[0].Target = 2;

            // Act
            var c = new ChibiSimState(); c.CopyFrom(s);

            // Assert
            Assert.AreEqual(s.ComputeChecksum(), c.ComputeChecksum());
            CollectionAssert.AreEqual(s.Team, c.Team); CollectionAssert.AreEqual(s.Placement, c.Placement);
            Assert.AreEqual(4, c.Count); Assert.AreEqual(ChibiMatchConfig.Format2v2, c.Format); Assert.AreEqual(8, c.DroppedMask);
            Assert.AreEqual(1, c.F[3].HoldTarget); Assert.AreEqual(3, c.F[1].HeldBy);
            Assert.AreEqual(2, c.Proj[0].Target); Assert.AreEqual(3, c.Minions[0].Target); Assert.AreEqual(2, c.Clouds[0].Target);
            c.F[3].HoldTarget = 2;
            Assert.AreNotEqual(s.ComputeChecksum(), c.ComputeChecksum(), "HoldTarget is hashed outside 1v1");
        }

        // ================================================================== friendly fire off / any hits any

        [TestCase(ChibiMatchConfig.Format2v2, false)]
        [TestCase(ChibiMatchConfig.FormatFfa, true)]
        public void Melee_AllyInTheBox_IsNotHit_EnemyInTheSameSpotIs(int format, bool expectHit)
        {
            // Arrange: Polearm light (box 0..2300 in front) with slot 1 right in front; slots 2 / 3 far away.
            var s = Arena(Cfg(format, 4, FighterClass.Polearm, FighterClass.Heavy), -3000, -2400, 5000, 6500);

            // Act
            Press(s, ChibiButtons.Light);
            Idle(s, 30);

            // Assert
            Assert.AreEqual(expectHit, s.F[1].PctM > 0, format == ChibiMatchConfig.Format2v2 ? "ally passes through" : "FFA: any hits any");
        }

        [TestCase(ChibiMatchConfig.Format2v2, false)]
        [TestCase(ChibiMatchConfig.FormatFfa, true)]
        public void Projectile_PassesThroughAlly_HitsTheEnemyBehind(int format, bool allyIsEnemy)
        {
            // Arrange: Ranged light arrow from -4000 to the right; slot 1 at -2500 in the path, slot 2 at -1000 behind it.
            var s = Arena(Cfg(format, 4, FighterClass.Ranged, FighterClass.Heavy, FighterClass.Heavy), -4000, -2500, -1000, 6500);

            // Act
            Press(s, ChibiButtons.Light);
            Idle(s, 45);

            // Assert
            if (allyIsEnemy) Assert.Greater(s.F[1].PctM, 0, "FFA: the first fighter in the path is hit");
            else
            {
                Assert.AreEqual(0, s.F[1].PctM, "2v2: the arrow passes through the ally");
                Assert.Greater(s.F[2].PctM, 0, "and hits the enemy behind");
            }
        }

        static bool FindZoneRow(out FighterClass cls, out int slot)
        {
            foreach (var c in Classes)
                for (int k = ChibiMovesetData.FirstNewSlot; k < ChibiMoveTable.Count((int)c); k++)
                {
                    var mv = ChibiMoveTable.Get((int)c, k);
                    if (mv.Ext != null && mv.Ext.Has(ChibiMoveExt.FZone) && mv.Mult > 0) { cls = c; slot = k; return true; }
                }
            cls = FighterClass.Melee; slot = -1;
            return false;
        }

        [Test]
        public void Zone_HitsTheEnemyInside_NeverTheAlly()
        {
            // Arrange: a zone of slot 0's class covering the ally (slot 1) and an enemy (slot 2).
            Assert.IsTrue(FindZoneRow(out var cls, out int slot), "the moveset has a damaging zone row");
            var s = Arena(Cfg(ChibiMatchConfig.Format2v2, 4, cls, FighterClass.Heavy, FighterClass.Heavy, FighterClass.Heavy),
                          -6000, -500, 500, 7000);
            var z = s.Zones[0];
            z.Active = true; z.Owner = 0; z.Slot = slot; z.X0 = -2000; z.X1 = 2000; z.Y0 = -200; z.Y1 = 2500;
            z.Age = 0; z.Mult = ChibiMoveTable.Get((int)cls, slot).Mult; z.Facing = 1; z.SpawnTick = -5;

            // Act
            for (int k = 0; k < 240 && s.F[2].PctM == 0; k++)
            {
                MovesetKit.Place(s.F[1], -500, 0, 1);
                if (s.F[2].Stun == 0) MovesetKit.Place(s.F[2], 500, 0, -1);
                if (!z.Active) { z.Active = true; z.Age = 0; }
                Idle(s, 1);
            }

            // Assert
            Assert.Greater(s.F[2].PctM, 0, "the enemy in the zone is hit");
            Assert.AreEqual(0, s.F[1].PctM, "the ally in the zone is not");
        }

        [Test]
        public void CloudAndMinions_TargetTheNearestEnemy_NeverTheNearerAlly()
        {
            // Arrange: Magic at -4000 with the ally right beside it; the enemies far to the right.
            var s = Arena(Cfg(ChibiMatchConfig.Format2v2, 4, FighterClass.Magic, FighterClass.Heavy, FighterClass.Heavy, FighterClass.Heavy),
                          -4000, -3200, 3000, 5000);

            // Act
            Press(s, ChibiButtons.Skill3);             // Thunder Cloud
            Idle(s, ChibiSimConst.CloudLock + 4);
            Press(s, ChibiButtons.Skill2);             // minions
            Idle(s, 2);

            // Assert
            int clouds = 0, minions = 0;
            foreach (var c in s.Clouds) if (c.Active) { clouds++; Assert.AreEqual(2, c.Target, "cloud follows the nearest enemy"); }
            foreach (var m in s.Minions) if (m.Active) { minions++; Assert.AreEqual(2, m.Target, "minion chases the nearest enemy"); }
            Assert.AreEqual(1, clouds); Assert.Greater(minions, 0);
            for (int k = 0; k < 400; k++)
            {
                MovesetKit.Place(s.F[1], -3200, 0, 1);   // the ally stays next to its summons
                Idle(s, 1);
            }
            Assert.AreEqual(0, s.F[1].PctM, "summons never hurt the ally");
        }

        [Test]
        public void FireRing_HitsEnemies_NotAllies()
        {
            // Arrange: Magic ring running; ally and enemy both inside the 2.1 radius.
            var s = Arena(Cfg(ChibiMatchConfig.Format2v2, 4, FighterClass.Magic, FighterClass.Heavy, FighterClass.Heavy, FighterClass.Heavy),
                          0, -1000, 1000, 7000);
            s.F[0].RingTicks = 61;

            // Act
            Idle(s, 4);

            // Assert
            Assert.Greater(s.F[2].PctM, 0, "enemy in the ring");
            Assert.AreEqual(0, s.F[1].PctM, "ally in the ring");
        }

        // ================================================================== holds / reflect / targeting

        [Test]
        public void Grab_ThreeFighters_TakesTheNearestEnemy_HoldPartnersStayConsistent()
        {
            // Arrange: Heavy grab box 150..1450 in front; slots 1 (nearer) and 2 both inside it.
            var s = Arena(Cfg(ChibiMatchConfig.FormatFfa, 3, FighterClass.Heavy, FighterClass.Melee, FighterClass.Polearm), -3000, -2400, -1900);

            // Act
            Press(s, ChibiButtons.Skill1);
            Idle(s, 1);

            // Assert
            Assert.IsTrue(s.F[1].Held, "the nearer enemy is grabbed");
            Assert.AreEqual(0, s.F[1].HeldBy);
            Assert.AreEqual(1, s.F[0].HoldTarget);
            Assert.IsFalse(s.F[2].Held, "only one victim");
            for (int k = 0; k < 90; k++) { AssertHoldInvariant(s, "step " + k); Idle(s, 1); }
            Assert.IsFalse(s.F[1].Held, "released after the slam");
            Assert.Greater(s.F[1].PctM, 0, "the slam hits the held victim");
            Assert.AreEqual(0, s.F[2].PctM, "the bystander is not slammed");
        }

        [Test]
        public void Reflect_ThreeFighters_ShotBelongsToReflector_AndTargetsTheOriginalOwner()
        {
            // Arrange: Lyra Prism Reflection active; slot 1's shot at slot 0; slot 2 is the nearer enemy of slot 0.
            var s = Arena(Cfg(ChibiMatchConfig.FormatFfa, 3, FighterClass.Magic, FighterClass.Heavy, FighterClass.Ranged), 0, 6000, -2000);
            Press(s, ChibiButtons.Skill2, ChibiDir.S);
            Idle(s, MovesetKit.Row(FighterClass.Magic, "lyra_prism").Startup + 1);
            ChibiProjectileSim shot = null;
            foreach (var p in s.Proj) if (!p.Active) { shot = p; break; }
            var t = s.F[0];
            shot.Active = true; shot.Owner = 1; shot.Target = 0; shot.X = t.X; shot.Y = t.Y + ChibiSimConst.CenterOffY;
            shot.Vx = -9000; shot.Vy = 0; shot.XRem = 0; shot.YRem = 0; shot.Life = 5; shot.Mult = 800; shot.KbX = 60000;
            shot.KbY = 30000; shot.Stun = 20; shot.SpawnFrame = s.GameTicks - 1; shot.Kind = ChibiProjKind.Arrow; shot.Flags = 0;
            shot.Radius = 600; shot.GrowPerTick = 0; shot.Speed = 0; shot.ReturnTicks = 0; shot.ReturnMult = 0; shot.ResetMoveset();

            // Act
            Idle(s, 1);

            // Assert
            Assert.AreEqual(0, shot.Owner, "the reflector owns the shot");
            Assert.AreEqual(1, shot.Target, "it flies at its old owner, not the nearest enemy (2)");
            Assert.AreEqual(9000, shot.Vx, "reversed");
        }

        [Test]
        public void NearestEnemy_TieGoesToLowestIndex_AlliesNeverCount()
        {
            // Arrange
            var ffa = Arena(Cfg(ChibiMatchConfig.FormatFfa, 4, FighterClass.Melee, FighterClass.Heavy), 0, 1000, -1000, 5000);
            var duo = Arena(Cfg(ChibiMatchConfig.Format2v2, 4, FighterClass.Melee, FighterClass.Heavy), 0, 100, 3000, -2000);

            // Act / Assert
            Assert.AreEqual(1, ChibiSimulation.NearestEnemy(ffa, 0), "equal distance: lowest index");
            ffa.F[1].X = 1001;
            Assert.AreEqual(2, ChibiSimulation.NearestEnemy(ffa, 0), "strictly nearer wins");
            Assert.AreEqual(3, ChibiSimulation.NearestEnemy(duo, 0), "the ally at 100 is skipped");
            Assert.IsTrue(ChibiSimulation.IsAlly(duo, 0, 1)); Assert.IsFalse(ChibiSimulation.IsFoe(duo, 0, 1));
            var one = Arena(new ChibiMatchConfig { Seed = 1, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy }, 0, 9000);
            Assert.AreEqual(1, ChibiSimulation.NearestEnemy(one, 0)); Assert.AreEqual(0, ChibiSimulation.NearestEnemy(one, 1));
        }

        // ================================================================== match end / placement / drop

        [Test]
        public void TwoVsTwo_TeamAliveWhileAnyMemberHasStocks_EndsWhenBothAreOut()
        {
            // Arrange
            var s = Arena(Cfg(ChibiMatchConfig.Format2v2, 4, FighterClass.Melee, FighterClass.Heavy), -3000, -1000, 1000, 3000);
            s.F[2].Stocks = 1; s.F[3].Stocks = 1;

            // Act: one member of team 1 loses its last stock.
            SendOut(s, 2);
            Idle(s, 1);
            Idle(s, 300);

            // Assert
            Assert.IsFalse(s.MatchOver, "team 1 still has a member with stocks");
            Assert.AreEqual(0, s.F[2].Stocks);
            Assert.Greater(s.F[2].OutTicks, 0, "an eliminated fighter never respawns");
            Assert.AreEqual(4, s.Placement[2]);

            SendOut(s, 3);
            Idle(s, 1);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(0, s.Winner, "Winner is the team id");
            Assert.AreEqual(1, s.Placement[0]); Assert.AreEqual(1, s.Placement[1]); Assert.AreEqual(3, s.Placement[3]);
        }

        [Test]
        public void Ffa_EveryoneLosesTheLastStockOnTheSameStep_IsADraw()
        {
            // Arrange
            var s = Arena(Cfg(ChibiMatchConfig.FormatFfa, 3, FighterClass.Melee, FighterClass.Heavy), -3000, 0, 3000);
            for (int i = 0; i < 3; i++) { s.F[i].Stocks = 1; SendOut(s, i); }

            // Act
            Idle(s, 1);

            // Assert
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(-1, s.Winner, "no team alive: draw (D-F1)");
            for (int i = 0; i < 3; i++) Assert.AreEqual(1, s.Placement[i], "simultaneous eliminations share a place");
        }

        [Test]
        public void Ffa_Placement_FollowsTheKoOutOrder()
        {
            // Arrange
            var s = Arena(Cfg(ChibiMatchConfig.FormatFfa, 4, FighterClass.Melee, FighterClass.Heavy), -4000, -1000, 1000, 4000);
            for (int i = 0; i < 4; i++) s.F[i].Stocks = 1;

            // Act / Assert
            SendOut(s, 3); Idle(s, 1);
            Assert.AreEqual(4, s.Placement[3]); Assert.IsFalse(s.MatchOver);
            SendOut(s, 1); Idle(s, 1);
            Assert.AreEqual(3, s.Placement[1]); Assert.IsFalse(s.MatchOver);
            SendOut(s, 2); Idle(s, 1);
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(0, s.Winner);
            Assert.AreEqual(2, s.Placement[2]); Assert.AreEqual(1, s.Placement[0]);
        }

        [TestCase(2, 1, 1, 1, 0, 0, 0, 0, 0)]                     // more total stocks
        [TestCase(1, 1, 1, 1, 50000, 50000, 30000, 30000, 1)]      // equal stocks: lower total percent
        [TestCase(1, 1, 1, 1, 40000, 20000, 30000, 30000, -1)]     // equal stocks and percent: draw
        public void TwoVsTwo_Timeout_TotalStocksThenTotalPercent(int s0, int s1, int s2, int s3, int p0, int p1, int p2, int p3, int winner)
        {
            // Arrange
            var s = Arena(Cfg(ChibiMatchConfig.Format2v2, 4, FighterClass.Melee, FighterClass.Heavy), -3000, -1000, 1000, 3000);
            s.F[0].Stocks = s0; s.F[1].Stocks = s1; s.F[2].Stocks = s2; s.F[3].Stocks = s3;
            s.F[0].PctM = p0; s.F[1].PctM = p1; s.F[2].PctM = p2; s.F[3].PctM = p3;
            s.MatchClock = ChibiSimConst.MatchTicks - 1;

            // Act
            Idle(s, 1);

            // Assert
            Assert.IsTrue(s.MatchOver);
            Assert.AreEqual(winner, s.Winner);
        }

        [Test]
        public void TeamTimeoutWinner_InOneVsOne_EqualsTheV24TimeoutWinner()
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = 3, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy });
            int[] stocks = { 1, 2, 3 }, pct = { 0, 30000, 99000 };
            foreach (int a in stocks) foreach (int b in stocks) foreach (int pa in pct) foreach (int pb in pct)
            {
                s.F[0].Stocks = a; s.F[1].Stocks = b; s.F[0].PctM = pa; s.F[1].PctM = pb;
                Assert.AreEqual(ChibiSimulation.TimeoutWinner(s.F[0], s.F[1]), ChibiSimulation.TeamTimeoutWinner(s), $"{a}/{b} {pa}/{pb}");
            }
        }

        [Test]
        public void DroppedSlot_IsEliminatedThatFrame_InputIgnored_MatchContinues()
        {
            // Arrange
            var s = Arena(Cfg(ChibiMatchConfig.FormatFfa, 3, FighterClass.Melee, FighterClass.Heavy), -3000, 0, 3000);
            var ins = Inputs();
            ins[2] = ChibiInputPacket.Dropped;

            // Act
            ChibiSimulation.Step(s, ins);

            // Assert
            Assert.AreEqual(4, s.DroppedMask);
            Assert.AreEqual(0, s.F[2].Stocks, "stocks forfeited on that frame");
            Assert.IsFalse(s.F[2].InPlay);
            ins[2] = ChibiInputPacket.Make(ChibiDir.E, ChibiButtons.Jump);
            for (int k = 0; k < 400; k++) ChibiSimulation.Step(s, ins);
            Assert.IsFalse(s.MatchOver, "two fighters are left");
            Assert.Greater(s.F[2].OutTicks, 0, "a dropped fighter never comes back");
            Assert.AreEqual(3, s.Placement[2]);
        }

        [Test]
        public void DroppedSlot_TwoVsTwoWholeTeam_EndsTheMatch_OneVsOneIgnoresTheSentinel()
        {
            // Arrange
            var duo = Arena(Cfg(ChibiMatchConfig.Format2v2, 4, FighterClass.Melee, FighterClass.Heavy), -3000, -1000, 1000, 3000);
            var one = Arena(new ChibiMatchConfig { Seed = 9, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy }, -3000, 3000);
            var ins = Inputs();
            ins[2] = ins[3] = ChibiInputPacket.Dropped;

            // Act
            Game(duo, ins);
            for (int k = 0; k < 10; k++) ChibiSimulation.Step(one, default, ChibiInputPacket.Dropped);

            // Assert
            Assert.IsTrue(duo.MatchOver); Assert.AreEqual(0, duo.Winner);
            Assert.AreEqual(0, one.DroppedMask, "1v1: the sentinel sanitizes to neutral as in v24");
            Assert.AreEqual(ChibiPercentConst.StocksStart, one.F[1].Stocks);
        }

        [Test]
        public void DroppedSlot_InTraining_NeverRespawns()
        {
            // Arrange: training revives KOs, but a dropped slot must stay out (review 2026-11-23 item 4: no zombie fighter).
            var cfg = Cfg(ChibiMatchConfig.FormatFfa, 3, FighterClass.Melee, FighterClass.Heavy);
            cfg.Training = true;
            var s = Arena(cfg, -3000, 0, 3000);
            var ins = Inputs();
            ins[2] = ChibiInputPacket.Dropped;

            // Act
            ChibiSimulation.Step(s, ins);
            ins[2] = default;
            for (int k = 0; k < 600; k++) ChibiSimulation.Step(s, ins);

            // Assert
            Assert.IsTrue(ChibiSimulation.Eliminated(s, 2), "dropped is out for good, training included");
            Assert.IsFalse(s.F[2].InPlay, "a dropped fighter never respawns in training");
            Assert.IsFalse(ChibiSimulation.IsFoe(s, 0, 2), "nobody can target or hit a dropped slot");
        }

        // ================================================================== spawns

        [TestCase(ChibiMatchConfig.FormatFfa, 3)]
        [TestCase(ChibiMatchConfig.FormatFfa, 4)]
        [TestCase(ChibiMatchConfig.Format2v2, 4)]
        public void Spawns_EveryStage_DistinctAndLandOnTheMainStage(int format, int count)
        {
            for (int stage = 0; stage < ChibiStageTable.Count; stage++)
            {
                // Arrange
                var s = new ChibiSimState();
                ChibiSimulation.Init(s, Cfg(format, count, FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic, stage));
                var st = ChibiStageTable.Get(stage);
                var xs = new HashSet<int>();
                for (int i = 0; i < s.Count; i++)
                {
                    Assert.IsTrue(xs.Add(s.F[i].X), $"stage {stage}: spawn {i} distinct");
                    Assert.Less(System.Math.Abs(s.F[i].X), st.HalfWidth, $"stage {stage}: spawn {i} over the main stage");
                    if (format == ChibiMatchConfig.Format2v2)
                        Assert.AreEqual(s.Team[i] == 0, s.F[i].X < 0, $"stage {stage}: team 0 left, team 1 right");
                }

                // Act
                Idle(s, 60);

                // Assert
                for (int i = 0; i < s.Count; i++)
                    Assert.IsTrue(s.F[i].Grounded && s.F[i].Y == 0, $"stage {stage}: fighter {i} landed on the main stage");
            }
        }

        [Test]
        public void Spawns_OneVsOne_KeepPlusMinusSpawnX()
        {
            for (int stage = 0; stage < ChibiStageTable.Count; stage++)
            {
                var s = new ChibiSimState();
                ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = 1, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy, Stage = stage });
                Assert.AreEqual(-ChibiStageTable.Get(stage).SpawnX, s.F[0].X); Assert.AreEqual(1, s.F[0].Facing);
                Assert.AreEqual(ChibiStageTable.Get(stage).SpawnX, s.F[1].X); Assert.AreEqual(-1, s.F[1].Facing);
            }
        }

        // ================================================================== replay v4

        static byte[] RecordFfaReplay(out ChibiMatchConfig cfg)
        {
            cfg = Cfg(ChibiMatchConfig.FormatFfa, 4, FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Polearm);
            var src = new IChibiInputSource[4];
            for (int i = 0; i < 4; i++) src[i] = new ChibiCpuInputSource(i, 3, cfg.Seed);
            var session = new ChibiLocalSession(cfg, src);
            var rec = new ChibiReplayRecorder(cfg);
            session.OnFrameAll += rec.Add;
            for (int f = 0; f < 900 && session.Tick(); f++) { }
            var ms = new MemoryStream();
            ChibiReplay.Write(ms, cfg, rec.Lanes);
            return ms.ToArray();
        }

        [Test]
        public void Replay_V4_FourFighters_RoundTripsAndVerifies()
        {
            // Arrange
            var bytes = RecordFfaReplay(out var cfg);

            // Act
            bool read = ChibiReplay.TryRead(new MemoryStream(bytes), out var back, out List<ushort>[] lanes, out _);

            // Assert
            Assert.IsTrue(read);
            Assert.AreEqual(4, lanes.Length);
            Assert.AreEqual(cfg.ComputeHash(), back.ComputeHash());
            Assert.AreEqual(FighterClass.Polearm, back.Class3);
            Assert.IsTrue(ChibiReplay.Verify(new MemoryStream(bytes)), "re-simulation reproduces the final checksum");
        }

        [TestCase(4, 3, "old version 3")]
        [TestCase(9, 7, "unknown format")]
        [TestCase(10, 5, "count 5")]
        [TestCase(10, 1, "count 1")]
        [TestCase(16, 0, "FFA slot 1 not on its own team")]
        [TestCase(11, 200, "bad class")]
        [TestCase(12, 2, "bad passive")]
        public void Replay_V4_BadHeader_IsRejected(int offset, int value, string why)
        {
            // Arrange: header = magic 0..3, version 4, seed 5..8, format 9, count 10, slots from 11 (class, passive, team).
            var bytes = RecordFfaReplay(out _);
            bytes[offset] = (byte)value;

            // Act / Assert
            Assert.IsFalse(ChibiReplay.TryRead(new MemoryStream(bytes), out ChibiMatchConfig _, out List<ushort>[] _, out _), why);
        }

        [Test]
        public void Replay_V4_TwoVsTwoWithThreeOnOneTeam_IsRejected()
        {
            // Arrange
            var cfg = Cfg(ChibiMatchConfig.Format2v2, 4, FighterClass.Melee, FighterClass.Heavy);
            var lanes = new IList<ushort>[4];
            for (int i = 0; i < 4; i++) lanes[i] = new List<ushort> { 0, 0, 0 };
            var ms = new MemoryStream();
            ChibiReplay.Write(ms, cfg, lanes);
            var bytes = ms.ToArray();
            Assert.IsTrue(ChibiReplay.TryRead(new MemoryStream(bytes), out ChibiMatchConfig _, out List<ushort>[] _, out _), "well-formed");
            bytes[11 + 2 * 3 + 2] = 0;   // slot 2 moves to team 0

            // Act / Assert
            Assert.IsFalse(ChibiReplay.TryRead(new MemoryStream(bytes), out ChibiMatchConfig _, out List<ushort>[] _, out _));
        }

        // ================================================================== CPU targeting

        [Test]
        public void Cpu_TwoVsTwo_NeverTargetsAnAlly()
        {
            // Arrange
            var cfg = Cfg(ChibiMatchConfig.Format2v2, 4, FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Polearm);
            var cpus = new ChibiCpuInputSource[4];
            for (int i = 0; i < 4; i++) cpus[i] = new ChibiCpuInputSource(i, 3, cfg.Seed);
            var session = new ChibiLocalSession(cfg, cpus);

            // Act / Assert
            for (int f = 0; f < 1800 && session.Tick(); f++)
                for (int i = 0; i < 4; i++)
                {
                    if (ChibiSimulation.Eliminated(session.State, i)) continue;
                    int t = cpus[i].Target;
                    Assert.AreNotEqual(i, t, "never itself");
                    Assert.IsFalse(ChibiSimulation.IsAlly(session.State, i, t), $"frame {f}: CPU {i} targets ally {t}");
                }
        }

        [Test]
        public void Cpu_OneVsOne_TargetIsAlwaysTheOpponent()
        {
            var cfg = new ChibiMatchConfig { Seed = 12, Class0 = FighterClass.Magic, Class1 = FighterClass.Polearm };
            var a = new ChibiCpuInputSource(0, 3, cfg.Seed); var b = new ChibiCpuInputSource(1, 3, cfg.Seed);
            var session = new ChibiLocalSession(cfg, a, b);
            for (int f = 0; f < 900 && session.Tick(); f++)
            {
                Assert.AreEqual(1, a.Target);
                Assert.AreEqual(0, b.Target);
            }
        }
    }
}
