// Story type: Logic (moveset mechanics, rules v18) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: design/gdd/movesets/README.md section 5 (flags) and sections 3-4 (knockback, hitboxes).
// Timing convention: the press step starts the row (MoveTick 0); move tick t runs on the (t + 1)-th game step after it.
using System;
using ChibiClash.Netcode;
using NUnit.Framework;
using static ChibiClash.Tests.MovesetKit;

namespace ChibiClash.Tests
{
    /// <summary>Each README section 5 mechanic with an exact-tick assertion.</summary>
    public class ChibiMovesetMechanicsTests
    {
        const int VelSub = ChibiSimConst.VelSub;

        /// <summary>Puts a harmless-looking plain shot of fighter <paramref name="owner"/> on top of its target: it hits on the next game step.</summary>
        static void InjectShot(ChibiSimState s, int owner, int mult = 500, int kbx = 60000, int kby = 30000, int stun = 20)
        {
            var t = s.F[1 - owner];
            foreach (var p in s.Proj)
            {
                if (p.Active) continue;
                p.Active = true; p.Owner = owner; p.X = t.X; p.Y = t.Y + ChibiSimConst.CenterOffY; p.Vx = 0; p.Vy = 0;
                p.XRem = 0; p.YRem = 0; p.Life = 5; p.Mult = mult; p.KbX = kbx; p.KbY = kby; p.Stun = stun;
                p.SpawnFrame = s.GameTicks - 1; p.Kind = ChibiProjKind.Arrow; p.Flags = 0; p.Radius = 600; p.GrowPerTick = 0;
                p.Speed = 0; p.ReturnTicks = 0; p.ReturnMult = 0; p.ResetMoveset();
                return;
            }
            Assert.Fail("fixture: projectile pool full");
        }

        static ChibiProjectileSim FirstShot(ChibiSimState s, int owner)
        {
            foreach (var p in s.Proj) if (p.Active && p.Owner == owner) return p;
            return null;
        }

        static int CountShots(ChibiSimState s, int owner)
        {
            int n = 0;
            foreach (var p in s.Proj) if (p.Active && p.Owner == owner) n++;
            return n;
        }

        // ================================================================== timing / box

        [Test]
        public void ForwardTilt_HitsOnItsFirstActiveTick_NotBefore()
        {
            // Arrange: Kaia ftilt (startup 5) vs Bruna 1200 ahead.
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 1200);
            var row = Row(FighterClass.Melee, "kaia_ftilt");
            int hp0 = s.F[1].HpU;

            // Act
            Game(s, P(ChibiDir.E, ChibiButtons.Light));
            Idle(s, row.Startup);
            int hpBefore = s.F[1].HpU;
            Idle(s, 1);

            // Assert
            Assert.AreEqual(hp0, hpBefore, "no hit during startup");
            Assert.Less(s.F[1].HpU, hp0, "hit on move tick 5");
            Assert.AreEqual(row.Stun, s.F[1].Stun, "row stun");
            Assert.AreEqual(row.KbX, s.F[1].Vx, "row kb toward Facing");
        }

        [Test]
        public void Row_EndsAfterStartupActiveRecovery()
        {
            var s = Arena(FighterClass.Melee);
            var row = Row(FighterClass.Melee, "kaia_ftilt");
            Game(s, P(ChibiDir.E, ChibiButtons.Light));
            Idle(s, row.Startup + row.Active + row.Recovery - 1);
            Assert.AreEqual(Slot(FighterClass.Melee, "kaia_ftilt"), s.F[0].MoveSlot, "still locked on the last recovery tick");
            Idle(s, 1);
            Assert.AreEqual(-1, s.F[0].MoveSlot, "free after startup + active + recovery move ticks");
        }

        [TestCase(1274, true)]
        [TestCase(1275, false)]
        public void CircleHitbox_StrictRadiusFromItsCentre(int gap, bool expectHit)
        {
            // Arrange: Kaia nair, r 900 centred (X, Y + 675); both airborne at the same height (fall together).
            var s = Arena(FighterClass.Melee, FighterClass.Heavy);
            Place(s.F[0], 0, 10000, 1);
            Place(s.F[1], gap, 10000, -1);      // hurtbox near edge at gap - 375 from the centre (above every platform)
            int hp0 = s.F[1].HpU;

            // Act
            Game(s, P(ChibiDir.Neutral, ChibiButtons.Light));
            Idle(s, Row(FighterClass.Melee, "kaia_nair").Startup + 1);

            // Assert
            Assert.AreEqual(expectHit, s.F[1].HpU < hp0);
        }

        // ================================================================== knockback kinds

        [Test]
        public void Spike_VsGroundedVictim_PopsUp_VsAirborne_Spikes()
        {
            var row = Row(FighterClass.Melee, "kaia_dair");

            // Arrange A: grounded victim under the diving Kaia.
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 0);
            Place(s.F[0], 0, 1500, 1);
            Game(s, P(ChibiDir.S, ChibiButtons.Light));
            Idle(s, row.Startup + 1);
            Assert.Less(s.F[1].HpU, s.F[1].Stats.MaxHp * ChibiCombatStats.HpScale, "precondition: dair hit");
            Assert.AreEqual(-row.KbY / 2, s.F[1].Vy, "grounded victim: KbY = -KbY / 2 (pop-up)");

            // Arrange B: airborne victim (both fall together, above every platform).
            s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 0);
            Place(s.F[0], 0, 10500, 1);
            Place(s.F[1], 0, 9000, -1);
            Game(s, P(ChibiDir.S, ChibiButtons.Light));
            Idle(s, row.Startup + 1);
            Assert.AreEqual(row.KbY, s.F[1].Vy, "airborne victim: spiked down");
        }

        [Test]
        public void Trip_GroundedUsesTripValues_AirborneUsesLowTier()
        {
            var row = Row(FighterClass.Heavy, "bruna_dtilt");
            var s = Arena(FighterClass.Heavy, FighterClass.Melee, 0, 1200);
            Game(s, P(ChibiDir.S, ChibiButtons.Light));
            Idle(s, row.Startup + 1);
            Assert.AreEqual(ChibiMovesetConst.TripStun, s.F[1].Stun, "grounded: trip stun 34");
            Assert.AreEqual(ChibiMovesetConst.TripKbX, s.F[1].Vx);

            s = Arena(FighterClass.Heavy, FighterClass.Melee, 0, 1200);
            Place(s.F[1], 1200, 300, -1);
            Game(s, P(ChibiDir.S, ChibiButtons.Light));
            Idle(s, row.Startup + 1);
            Assert.IsFalse(s.F[1].Grounded && s.F[1].Stun == ChibiMovesetConst.TripStun, "airborne victim is not tripped");
            Assert.AreEqual(ChibiMovesetConst.TripAirStun, s.F[1].Stun, "airborne: L tier stun 22");
            Assert.AreEqual(ChibiMovesetConst.TripAirKbX, s.F[1].Vx);
        }

        [Test]
        public void BackAir_HitsBehind_KnocksBackward()
        {
            var row = Row(FighterClass.Melee, "kaia_bair");
            var s = Arena(FighterClass.Melee, FighterClass.Heavy);
            Place(s.F[0], 0, 10000, 1);
            Place(s.F[1], -1200, 10000, 1);
            Game(s, P(ChibiDir.W, ChibiButtons.Light));
            Idle(s, row.Startup + 1);
            Assert.AreEqual(-row.KbX, s.F[1].Vx, "Dir = -Facing");
        }

        [Test]
        public void CenteredRow_KnocksAwayFromTheAttackerOnBothSides()
        {
            var row = Row(FighterClass.Melee, "kaia_dsmash");
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, -1200);
            Game(s, P(ChibiDir.S, ChibiButtons.Heavy));
            Idle(s, row.Startup + 1);
            Assert.AreEqual(-row.KbX, s.F[1].Vx, "victim behind is pushed away (positional Dir)");
        }

        // ================================================================== charge / hold / multi

        [Test]
        public void Charge_HoldsTickZero_ThenScalesMult()
        {
            // Arrange: Kaia fsmash, Heavy held for 30 ticks after the press.
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 6000);
            var row = Row(FighterClass.Melee, "kaia_fsmash");
            Game(s, P(ChibiDir.E, ChibiButtons.Heavy));
            int baseMult = s.F[0].MoveMult;

            // Act
            for (int k = 0; k < 30; k++) Game(s, P(ChibiDir.Neutral, ChibiButtons.Heavy));
            int tickWhileCharging = s.F[0].MoveTick;
            Game(s, N, N);

            // Assert
            Assert.AreEqual(0, tickWhileCharging, "MoveTick does not advance while charging");
            Assert.AreEqual(30, s.F[0].ChargeHeld);
            Assert.AreEqual(baseMult * (1000 + 400 * 30 / 60) / 1000, s.F[0].MoveMult, "x1.2 after half a charge");
            Assert.AreEqual(1, s.F[0].MoveTick, "tick 0 runs on the release step");
            Assert.AreEqual(row.Mult, baseMult, "precondition: no Patience on Kaia p0");
        }

        [Test]
        public void Charge_StopsAtMax_EvenWhileHeld()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.E, ChibiButtons.Heavy));
            int baseMult = s.F[0].MoveMult;
            for (int k = 0; k < 61; k++) Game(s, P(ChibiDir.Neutral, ChibiButtons.Heavy));
            Assert.AreEqual(60, s.F[0].ChargeHeld);
            Assert.IsFalse(s.F[0].Charging);
            Assert.AreEqual(baseMult * 1400 / 1000, s.F[0].MoveMult, "x1.4 at full charge");
        }

        [TestCase(0, "ren_nspec_lv1")]
        [TestCase(19, "ren_nspec_lv1")]
        [TestCase(20, "ren_nspec_lv2")]
        [TestCase(39, "ren_nspec_lv2")]
        [TestCase(40, "ren_nspec_lv3")]
        public void ChargeLevel_PicksTheRowByHeldTicks(int held, string expected)
        {
            var s = Arena(FighterClass.Ranged, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.Neutral, ChibiButtons.Special));
            for (int k = 0; k < held; k++) Game(s, P(ChibiDir.Neutral, ChibiButtons.Special));
            Game(s, N, N);
            Assert.AreEqual(expected, IdOf(s.F[0]));
        }

        [Test]
        public void Hold_ExtendsTheActiveWindow_UpToMax()
        {
            // Arrange: Bruna Fortress Stance (startup 5, active 20, hold 60), Special held all the time.
            var s = Arena(FighterClass.Heavy, FighterClass.Melee, 0, 6000);
            var row = Row(FighterClass.Heavy, "bruna_dspec");
            var held = P(ChibiDir.Neutral, ChibiButtons.Special);
            Game(s, P(ChibiDir.S, ChibiButtons.Special));

            // Act
            for (int k = 0; k < row.Startup + row.Active + 80; k++) Game(s, held);

            // Assert
            Assert.AreEqual(60, s.F[0].HoldExt, "hold capped at 60");
            Assert.AreEqual(row.Active + 60, s.F[0].MoveActive);

            // Released early: no extension.
            s = Arena(FighterClass.Heavy, FighterClass.Melee, 0, 6000);
            Game(s, P(ChibiDir.S, ChibiButtons.Special));
            Idle(s, row.Startup + row.Active + row.Recovery);
            Assert.AreEqual(0, s.F[0].HoldExt);
            Assert.AreEqual(-1, s.F[0].MoveSlot, "ends on time without the hold");
        }

        [Test]
        public void MultiHit_LinkHitsThenTheRowFinisher()
        {
            // Arrange: Kaia Blade Flurry, multi:4:3 (hits on active ticks 0, 3, 6, 9), victim in range.
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 1000);
            var row = Row(FighterClass.Melee, "kaia_nspec");
            Game(s, P(ChibiDir.Neutral, ChibiButtons.Special));
            Idle(s, row.Startup + 1);
            int firstStun = s.F[1].Stun;

            // Act
            Idle(s, 9);

            // Assert
            Assert.AreEqual(row.Rehit + ChibiMovesetConst.LinkStunPad, firstStun, "hit 1 = LINK stun (rehit + 4)");
            Assert.AreEqual(4, s.F[0].HitsLanded, "exactly 4 hits");
            Assert.AreEqual(row.Stun, s.F[1].Stun, "hit 4 uses the row stun");
            Assert.AreEqual(row.KbX, s.F[1].Vx, "hit 4 uses the row kb");
        }

        // ================================================================== armor / invuln / counter / reflect

        [TestCase(3, true)]
        [TestCase(4, false)]
        [TestCase(10, false)]
        [TestCase(11, true)]
        public void Armor_NoStunDuringItsWindow(int moveTick, bool expectInterrupted)
        {
            // Arrange: Kaia Blade Reversal armor:4:10:0; a shot lands on move tick moveTick.
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.S, ChibiButtons.Skill2));
            Idle(s, moveTick);
            int hp0 = s.F[0].HpU;
            InjectShot(s, 1);

            // Act
            Game(s, N, N);

            // Assert
            Assert.Less(s.F[0].HpU, hp0, "armor still takes the damage");
            Assert.AreEqual(expectInterrupted, s.F[0].MoveSlot < 0);
            Assert.AreEqual(expectInterrupted ? 20 : 0, s.F[0].Stun);
        }

        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(13, false)]
        [TestCase(14, true)]
        public void InvulnWindow_CoversMoveTicksAToB(int moveTick, bool expectDamage)
        {
            // Arrange: Kaia Tech Roll invuln:2:13 (Dash cooldown clear, no other invuln).
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.S, ChibiButtons.Skill1));
            Idle(s, moveTick);
            int hp0 = s.F[0].HpU;
            InjectShot(s, 1);

            // Act
            Game(s, N, N);

            // Assert
            Assert.AreEqual(expectDamage, s.F[0].HpU < hp0);
        }

        [Test]
        public void Counter_CatchesMelee_ThenUnblockableRiposteNextTick()
        {
            // Arrange: find when Bruna's light hits an idle Kaia 1200 away.
            int hitStep = -1;
            {
                var probe = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 1200);
                int hp = probe.F[0].HpU;
                for (int k = 0; k < 30 && hitStep < 0; k++)
                {
                    Game(probe, N, k == 0 ? P(ChibiDir.Neutral, ChibiButtons.Light) : N);
                    if (probe.F[0].HpU < hp) hitStep = k;
                }
            }
            Assert.Greater(hitStep, 4, "fixture: Bruna light lands later than the riposte startup");
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 1200);
            int kaiaHp = s.F[0].HpU, brunaHp = s.F[1].HpU;

            // Act: Kaia presses S2+U so that the light lands on her move tick 4 (active 2..7).
            for (int k = 0; k <= hitStep; k++)
                Game(s, k == hitStep - 5 ? P(ChibiDir.N, ChibiButtons.Skill2) : N, k == 0 ? P(ChibiDir.Neutral, ChibiButtons.Light) : N);
            bool caught = s.F[0].HpU == kaiaHp && s.F[0].CounterStrike == 1;
            int invuln = s.F[0].Invuln;
            Game(s, N, N);

            // Assert
            Assert.IsTrue(caught, "the light is cancelled and the counter armed");
            Assert.AreEqual(ChibiMovesetConst.CounterInvuln, invuln);
            Assert.Less(s.F[1].HpU, brunaHp, "riposte lands on the next tick");
            Assert.AreEqual(0, s.F[0].CounterStrike);
        }

        [Test]
        public void Counter_DoesNotCatchProjectiles_UnlessCounterProj()
        {
            // Kaia Parry Riposte (counter): a shot still hurts.
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.N, ChibiButtons.Skill2));
            Idle(s, 3);
            int hp0 = s.F[0].HpU;
            InjectShot(s, 1);
            Game(s, N, N);
            Assert.Less(s.F[0].HpU, hp0, "plain counter ignores projectiles");

            // Taro Jade Stance (counter:proj): the shot is cancelled and removed.
            s = Arena(FighterClass.Polearm, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.S, ChibiButtons.Special));
            Idle(s, Row(FighterClass.Polearm, "taro_dspec").Startup + 1);
            hp0 = s.F[0].HpU;
            InjectShot(s, 1);
            Game(s, N, N);
            Assert.AreEqual(hp0, s.F[0].HpU, "counter:proj cancels the shot");
            Assert.AreEqual(1, s.F[0].CounterStrike);
            Assert.AreEqual(0, CountShots(s, 1), "countered shot despawned");
        }

        [Test]
        public void Reflect_TurnsAnEnemyShotAround()
        {
            // Arrange: Lyra Prism Reflection (startup 12, active 24, reflect 1250).
            var s = Arena(FighterClass.Magic, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.S, ChibiButtons.Skill2));
            Idle(s, Row(FighterClass.Magic, "lyra_prism").Startup + 1);
            InjectShot(s, 1, mult: 800);
            var shot = FirstShot(s, 1);
            shot.Vx = -9000;
            int hp0 = s.F[0].HpU;

            // Act
            Game(s, N, N);

            // Assert
            Assert.AreEqual(hp0, s.F[0].HpU, "reflected, not hit");
            Assert.AreEqual(0, shot.Owner, "owner flipped");
            Assert.AreEqual(9000, shot.Vx, "velocity reversed");
            Assert.AreEqual(1000, shot.Mult, "Mult x 1250 / 1000");
        }

        // ================================================================== air mechanics

        [Test]
        public void RestoreJump_GivesBackTheAirJumpOnTheFirstActiveTick()
        {
            var s = Arena(FighterClass.Melee);
            Place(s.F[0], 0, 6000, 1);
            s.F[0].AirJumps = 0;
            var row = Row(FighterClass.Melee, "kaia_wallkick");
            Game(s, P(ChibiDir.N, ChibiButtons.Skill1));
            Idle(s, row.Startup);
            int before = s.F[0].AirJumps;
            Idle(s, 1);
            Assert.AreEqual(0, before);
            Assert.AreEqual(1, s.F[0].AirJumps);
        }

        [Test]
        public void Dive_HoversDuringStartup_ThenLandsIntoRecoveryWithALandingHit()
        {
            // Arrange: Bruna Meteor Anvil from 3000 up; Kaia standing 1200 away: outside the 1400-wide dive box, inside the
            // 2600-wide landing box.
            var s = Arena(FighterClass.Heavy, FighterClass.Melee, 0, 1200);
            var row = Row(FighterClass.Heavy, "bruna_meteor");
            Place(s.F[0], 0, 3000, 1);
            Game(s, P(ChibiDir.S, ChibiButtons.Special));
            Idle(s, row.Startup);
            int yAfterStartup = s.F[0].Y;
            int hp0 = s.F[1].HpU;

            // Act: dive until the landing step.
            int steps = 0;
            while (!s.F[0].Grounded && steps < row.Active) { Game(s, N, N); steps++; }

            // Assert
            Assert.Greater(yAfterStartup, 2800, "startup hovers (V forced 0, one tick of gravity per tick)");
            Assert.IsTrue(s.F[0].Grounded, "landed inside the dive window");
            Assert.AreEqual(row.Startup + row.Active, s.F[0].MoveTick, "landing jumps to recovery");
            Assert.Less(s.F[1].HpU, hp0, "landing hit on the landing step");
            Assert.AreEqual(row.Ext.LandKbY, s.F[1].Vy, "landing hit kb (launch up)");
        }

        [Test]
        public void Dive_HitInTheAir_PopsUpIntoRecovery()
        {
            var s = Arena(FighterClass.Heavy, FighterClass.Melee, 0, 0);
            var row = Row(FighterClass.Heavy, "bruna_meteor");
            Place(s.F[0], 0, 9000, 1);
            Place(s.F[1], 0, 6000, -1);
            s.F[1].Vy = 0;
            Game(s, P(ChibiDir.S, ChibiButtons.Special));
            int hp0 = s.F[1].HpU;
            for (int k = 0; k < row.Startup + row.Active && s.F[1].HpU == hp0; k++)
            {
                s.F[1].Vy = 0; s.F[1].Y = 6000;             // keep the target hanging (fixture)
                Game(s, N, N);
            }
            Assert.Less(s.F[1].HpU, hp0, "precondition: the dive hit");
            Assert.AreEqual(ChibiMovesetConst.DivePopVy * VelSub, s.F[0].Vy, "pop up");
            Assert.AreEqual(row.Startup + row.Active, s.F[0].MoveTick, "recovery in the air");
        }

        [Test]
        public void Teleport_MovesByTheDistanceOnTheFirstActiveTick()
        {
            // Arrange: Lyra Blink Ascension, up-right (diagonal: 2000 x 707 / 1000 = 1414 per axis).
            var s = Arena(FighterClass.Magic, FighterClass.Heavy, -3000, 6000);
            var row = Row(FighterClass.Magic, "lyra_uspec");
            Game(s, P(ChibiDir.NE, ChibiButtons.Special));
            Idle(s, row.Startup);
            int x0 = s.F[0].X, y0 = s.F[0].Y;

            // Act
            Idle(s, 1);

            // Assert
            Assert.AreEqual(x0 + 1414, s.F[0].X, "teleport X (Vx 0 on the ground before)");
            Assert.GreaterOrEqual(s.F[0].Y - y0, 1414, "teleport Y (plus this tick's self_vy rise)");
            Assert.Less(s.F[0].Y - y0, 1414 + 200);
        }

        [Test]
        public void Teleport_Up_IsClampedUnderTheCeiling()
        {
            var s = Arena(FighterClass.Magic, FighterClass.Heavy, 0, 6000);
            var st = ChibiStageTable.Get(0);
            Place(s.F[0], 0, st.CeilingY - 2500, 1);
            Game(s, P(ChibiDir.N, ChibiButtons.Skill2));           // Blink Ascension+ (3500) straight up
            Assert.AreEqual("lyra_blinkplus", IdOf(s.F[0]));
            var row = Row(FighterClass.Magic, "lyra_blinkplus");
            // Fixture: hold the fighter at its height through startup.
            for (int k = 0; k < row.Startup; k++) { s.F[0].Y = st.CeilingY - 2500; s.F[0].Vy = 0; Game(s, N, N); }
            s.F[0].Y = st.CeilingY - 2500; s.F[0].Vy = 0;
            Game(s, N, N);
            Assert.LessOrEqual(s.F[0].Y, st.CeilingY - ChibiMovesetConst.TeleportMargin + 300, "clamped to ceiling - 2000 (+ this tick's rise)");
            Assert.IsFalse(s.MatchOver);
        }

        [Test]
        public void Roll_MovesBackwardByDefault_AndFacesBackAfterAForwardRoll()
        {
            var row = Row(FighterClass.Melee, "kaia_roll");
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.S, ChibiButtons.Skill1));
            Idle(s, 1);
            Assert.AreEqual(-row.SelfVx * VelSub, s.F[0].Vx, "neutral roll goes backward (-Facing)");
            Idle(s, row.Startup + row.Active);
            Assert.AreEqual(1, s.F[0].Facing, "backward roll keeps facing");

            s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.SE, ChibiButtons.Skill1));
            Idle(s, row.Startup + row.Active);
            Assert.AreEqual(-1, s.F[0].Facing, "forward roll ends facing back (Facing = -r)");
        }

        [Test]
        public void Recoil_SetsVelocityOnTheFirstRecoveryTick()
        {
            var s = Arena(FighterClass.Ranged, FighterClass.Heavy, 0, 6000);
            var row = Row(FighterClass.Ranged, "ren_rebound");
            Game(s, P(ChibiDir.S, ChibiButtons.Skill1));
            Idle(s, row.Startup + row.Active + 1);
            Assert.AreEqual(row.Ext.RecoilVx * VelSub, s.F[0].Vx, "recoil -200 milli/tick toward Facing");
        }

        // ================================================================== projectiles

        [Test]
        public void ExplicitVelocity_FallingPinShootsStraightDown()
        {
            var s = Arena(FighterClass.Ranged, FighterClass.Heavy, 0, 6000);
            Place(s.F[0], 0, 5000, 1);
            Game(s, P(ChibiDir.S, ChibiButtons.Special));
            Idle(s, Row(FighterClass.Ranged, "ren_fallingpin").Startup + 1);
            var p = FirstShot(s, 0);
            Assert.IsNotNull(p, "shot on the first active tick");
            Assert.AreEqual(0, p.Vx);
            Assert.AreEqual(-330000, p.Vy);
            Assert.AreEqual(ChibiProjKind.LanceArrow, p.Kind);
        }

        [Test]
        public void Fan_FiveShotsEveryFourTicks_WithTheirOwnVelocities()
        {
            var s = Arena(FighterClass.Ranged, FighterClass.Heavy, 0, 9000);
            var row = Row(FighterClass.Ranged, "ren_volley");
            Game(s, P(ChibiDir.N, ChibiButtons.Skill3));
            Idle(s, row.Startup + 1);
            Assert.AreEqual(1, CountShots(s, 0), "first shot on active tick 0");
            Assert.AreEqual(row.Ext.FanVy[0], FirstShot(s, 0).Vy);
            Idle(s, 16);
            Assert.AreEqual(5, s.F[0].ShotIdx, "shots on active ticks 0, 4, 8, 12, 16");
        }

        [Test]
        public void Pierce_ShotKeepsFlyingAfterAHit()
        {
            var s = Arena(FighterClass.Ranged, FighterClass.Heavy, 0, 2000);
            var row = Row(FighterClass.Ranged, "ren_sspec");
            int hp0 = s.F[1].HpU;
            Game(s, P(ChibiDir.E, ChibiButtons.Special));
            Idle(s, row.Startup + 4);
            Assert.Less(s.F[1].HpU, hp0, "precondition: hit");
            Assert.AreEqual(1, CountShots(s, 0), "piercing shot still active");
            Assert.AreEqual(0, FirstShot(s, 0).Flags & ChibiProjectileSim.FDestructible, "pierce = not destructible");
        }

        [Test]
        public void Bounce_OffTheFloor_FlipsVyAndSwitchesMult()
        {
            var s = Arena(FighterClass.Ranged, FighterClass.Heavy, 0, 12000);
            var row = Row(FighterClass.Ranged, "ren_ricochet");
            Game(s, P(ChibiDir.S, ChibiButtons.Skill3));
            Idle(s, row.Startup + 1);
            var p = FirstShot(s, 0);
            Assert.IsNotNull(p);
            Assert.Less(p.Vy, 0, "starts downward");
            for (int k = 0; k < 20 && p.Bounces == 0; k++) Game(s, N, N);
            Assert.AreEqual(1, p.Bounces);
            Assert.Greater(p.Vy, 0, "Vy flipped");
            Assert.AreEqual(row.Ext.BounceMult, p.Mult);
        }

        [Test]
        public void TetherSelf_ExpiresInTheAir_PullsTheOwnerToTheAnchor()
        {
            var s = Arena(FighterClass.Ranged, FighterClass.Heavy, 0, 12000);
            var row = Row(FighterClass.Ranged, "ren_uspec");
            Game(s, P(ChibiDir.N, ChibiButtons.Special));
            Idle(s, row.Startup + 1);
            Assert.AreEqual(ChibiProjKind.Grapple, FirstShot(s, 0).Kind);
            for (int k = 0; k < 20 && s.F[0].PullTicks == 0; k++) Game(s, N, N);
            Assert.AreEqual(row.Ext.TetherMax, s.F[0].PullTicks, "anchored on expiry");
            Game(s, N, N);
            int dx = s.F[0].AnchorX - s.F[0].X, dy = s.F[0].AnchorY - s.F[0].Y;
            Assert.Greater(s.F[0].Vy, 0, "pulled up toward the anchor");
            Assert.Greater(dx + dy, -1, "anchor ahead / above");
        }

        [Test]
        public void TetherFoe_PullsTheVictimTowardTheAttacker()
        {
            var s = Arena(FighterClass.Ranged, FighterClass.Heavy, 0, 3000);
            var row = Row(FighterClass.Ranged, "ren_grapple2");
            int hp0 = s.F[1].HpU;
            Game(s, P(ChibiDir.N, ChibiButtons.Skill1));
            for (int k = 0; k < row.Startup + 12 && s.F[1].HpU == hp0; k++) Game(s, N, N);
            Assert.Less(s.F[1].HpU, hp0, "precondition: grapple hit");
            Assert.AreEqual(-row.KbX / 2, s.F[1].Vx, "pull (Dir reversed), Ren kb halved (OD-4)");
        }

        // ================================================================== zones

        [Test]
        public void ContactZone_HitsOnTheFirstOverlapThenDisappears()
        {
            // Arrange: Kaia Blade Trap (zone:contact:0:240, centred 1600 wide) with Bruna standing in it.
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 500);
            var row = Row(FighterClass.Melee, "kaia_dspec");
            int hp0 = s.F[1].HpU;
            Game(s, P(ChibiDir.S, ChibiButtons.Special));
            Idle(s, row.Startup + 1);
            bool spawned = s.Zones[0].Active;
            int hpAtSpawn = s.F[1].HpU;

            // Act
            Game(s, N, N);

            // Assert
            Assert.IsTrue(spawned, "zone spawned on the first active tick");
            Assert.AreEqual(hp0, hpAtSpawn, "no hit on the spawn step");
            Assert.Less(s.F[1].HpU, hp0, "hit on the zone's first update");
            Assert.IsFalse(s.Zones[0].Active, "consumed");
        }

        [Test]
        public void TimedZone_BurstsAfterItsDelay()
        {
            // Lyra Starfall Trap zone:timed:60:12, 1300 ahead.
            var s = Arena(FighterClass.Magic, FighterClass.Heavy, 0, 2000);
            var row = Row(FighterClass.Magic, "lyra_starfall");
            int hp0 = s.F[1].HpU;
            Game(s, P(ChibiDir.S, ChibiButtons.Skill1));
            Idle(s, row.Startup + 1 + 60);
            int hpBefore = s.F[1].HpU;
            Idle(s, 1);
            Assert.AreEqual(hp0, hpBefore, "nothing before zone age 60");
            Assert.Less(s.F[1].HpU, hp0, "burst at zone age 60");
        }

        [Test]
        public void PeriodicZone_PulsesAndSlows_ThenExpires()
        {
            // Lyra Glyph Zone zone:periodic:20:120 + slow:30, centred 3000 wide.
            var s = Arena(FighterClass.Magic, FighterClass.Heavy, 0, 600);
            var row = Row(FighterClass.Magic, "lyra_dspec");
            Game(s, P(ChibiDir.S, ChibiButtons.Special));
            Idle(s, row.Startup + 2);
            Assert.AreEqual(1, s.F[0].HitsLanded, "pulse at age 0");
            Assert.Greater(s.F[1].SlowTicks, 0, "slowed while inside");
            Idle(s, 125);
            Assert.IsFalse(s.Zones[0].Active, "expired at age 120");
        }
    }
}
