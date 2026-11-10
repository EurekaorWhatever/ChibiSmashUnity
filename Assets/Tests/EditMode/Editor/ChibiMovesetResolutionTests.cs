// Story type: Logic (input -> move resolution, rules v18) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: design/gdd/movesets/README.md section 1 (precedence table, legality, gates), decisions CH-3, OD-1, OD-2.
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;
using static ChibiClash.Tests.MovesetKit;

namespace ChibiClash.Tests
{
    /// <summary>Every CSV input pattern resolves to its row on the ground / in the air, and the README 1.2 rules around it.</summary>
    public class ChibiMovesetResolutionTests
    {
        /// <summary>(class, CSV input, air, expected CSV id): every row and ground/air state of movesets.csv (charge-level groups: Lv1).</summary>
        static readonly (string cls, string input, bool air, string id)[] Table =
        {
            ("Melee", "L+F", false, "kaia_ftilt"), ("Melee", "L+U", false, "kaia_utilt"), ("Melee", "L+D", false, "kaia_dtilt"),
            ("Melee", "L@dash", false, "kaia_dash"), ("Melee", "H+F", false, "kaia_fsmash"), ("Melee", "H+U", false, "kaia_usmash"),
            ("Melee", "H+D", false, "kaia_dsmash"), ("Melee", "L+D@dash", false, "kaia_lowsweep"), ("Melee", "L", true, "kaia_nair"),
            ("Melee", "L+F", true, "kaia_fair"), ("Melee", "L+BK", true, "kaia_bair"), ("Melee", "L+U", true, "kaia_uair"),
            ("Melee", "L+D", true, "kaia_dair"), ("Melee", "B", false, "kaia_nspec"), ("Melee", "B", true, "kaia_nspec"),
            ("Melee", "B+F", false, "kaia_sspec"), ("Melee", "B+F", true, "kaia_sspec"), ("Melee", "B+U", false, "kaia_uspec"),
            ("Melee", "B+U", true, "kaia_uspec"), ("Melee", "B+D", false, "kaia_dspec"), ("Melee", "S3+U", false, "kaia_ccc"),
            ("Melee", "S2+U", false, "kaia_riposte"), ("Melee", "S2+U", true, "kaia_riposte"), ("Melee", "S2+D", false, "kaia_reversal"),
            ("Melee", "S1+U", true, "kaia_wallkick"), ("Melee", "S1+D", false, "kaia_roll"),
            ("Heavy", "L+F", false, "bruna_ftilt"), ("Heavy", "L+U", false, "bruna_utilt"), ("Heavy", "L+D", false, "bruna_dtilt"),
            ("Heavy", "L@dash", false, "bruna_dash"), ("Heavy", "H+F", false, "bruna_fsmash"), ("Heavy", "H+U", false, "bruna_usmash"),
            ("Heavy", "H+D", false, "bruna_dsmash"), ("Heavy", "L", true, "bruna_nair"), ("Heavy", "L+F", true, "bruna_fair"),
            ("Heavy", "L+BK", true, "bruna_bair"), ("Heavy", "L+U", true, "bruna_uair"), ("Heavy", "L+D", true, "bruna_dair"),
            ("Heavy", "B", false, "bruna_nspec"), ("Heavy", "B+F", false, "bruna_sspec"), ("Heavy", "B+F", true, "bruna_sspec"),
            ("Heavy", "B+D", false, "bruna_dspec"), ("Heavy", "B+U", false, "bruna_uspec"), ("Heavy", "B+U", true, "bruna_uspec"),
            ("Heavy", "B+D", true, "bruna_meteor"), ("Heavy", "S1+U", false, "bruna_uppercut"), ("Heavy", "S1+D", false, "bruna_seismic"),
            ("Heavy", "S2+U", false, "bruna_rocketstrike"), ("Heavy", "S2+U", true, "bruna_rocketstrike"),
            ("Heavy", "S2+D", false, "bruna_fortressguard"),
            ("Ranged", "L+F", false, "ren_ftilt"), ("Ranged", "L+U", false, "ren_utilt"), ("Ranged", "L+D", false, "ren_dtilt"),
            ("Ranged", "L@dash", false, "ren_dash"), ("Ranged", "H+F", false, "ren_fsmash"), ("Ranged", "H+U", false, "ren_usmash"),
            ("Ranged", "H+D", false, "ren_dsmash"), ("Ranged", "L", true, "ren_nair"), ("Ranged", "L+F", true, "ren_fair"),
            ("Ranged", "L+BK", true, "ren_bair"), ("Ranged", "L+U", true, "ren_uair"), ("Ranged", "L+D", true, "ren_dair"),
            ("Ranged", "B", false, "ren_nspec_lv1"), ("Ranged", "B", true, "ren_nspec_lv1"), ("Ranged", "B+F", false, "ren_sspec"),
            ("Ranged", "B+F", true, "ren_sspec"), ("Ranged", "B+D", false, "ren_dspec"), ("Ranged", "B+U", false, "ren_uspec"),
            ("Ranged", "B+U", true, "ren_uspec"), ("Ranged", "B+D", true, "ren_fallingpin"), ("Ranged", "S3+U", false, "ren_volley"),
            ("Ranged", "S3+U", true, "ren_volley"), ("Ranged", "S3+D", false, "ren_ricochet"), ("Ranged", "S3+D", true, "ren_ricochet"),
            ("Ranged", "S1+D", false, "ren_rebound"), ("Ranged", "S1+D", true, "ren_rebound_air"),
            ("Ranged", "S1+U", false, "ren_grapple2"), ("Ranged", "S1+U", true, "ren_grapple2"),
            ("Magic", "L+F", false, "lyra_ftilt"), ("Magic", "L+U", false, "lyra_utilt"), ("Magic", "L+D", false, "lyra_dtilt"),
            ("Magic", "L@dash", false, "lyra_dash"), ("Magic", "H+F", false, "lyra_fsmash"), ("Magic", "H+U", false, "lyra_usmash"),
            ("Magic", "H+D", false, "lyra_dsmash"), ("Magic", "L", true, "lyra_nair"), ("Magic", "L+F", true, "lyra_fair"),
            ("Magic", "L+BK", true, "lyra_bair"), ("Magic", "L+U", true, "lyra_uair"), ("Magic", "L+D", true, "lyra_dair"),
            ("Magic", "B", false, "lyra_nspec"), ("Magic", "B", true, "lyra_nspec"), ("Magic", "B+F", false, "lyra_sspec"),
            ("Magic", "B+F", true, "lyra_sspec"), ("Magic", "B+D", false, "lyra_dspec"), ("Magic", "B+U", false, "lyra_uspec"),
            ("Magic", "B+U", true, "lyra_uspec"), ("Magic", "S1+U", false, "lyra_lift"), ("Magic", "S1+D", false, "lyra_starfall"),
            ("Magic", "S2+D", false, "lyra_prism"), ("Magic", "S2+D", true, "lyra_prism"), ("Magic", "B+D", true, "lyra_comet"),
            ("Magic", "S2+U", false, "lyra_blinkplus"), ("Magic", "S2+U", true, "lyra_blinkplus"),
            ("Polearm", "L+F", false, "taro_ftilt"), ("Polearm", "L+U", false, "taro_utilt"), ("Polearm", "L+D", false, "taro_dtilt"),
            ("Polearm", "L@dash", false, "taro_dash"), ("Polearm", "H+F", false, "taro_fsmash"), ("Polearm", "H+U", false, "taro_usmash"),
            ("Polearm", "H+D", false, "taro_dsmash"), ("Polearm", "L", true, "taro_nair"), ("Polearm", "L+F", true, "taro_fair"),
            ("Polearm", "L+BK", true, "taro_bair"), ("Polearm", "L+U", true, "taro_uair"), ("Polearm", "L+D", true, "taro_dair"),
            ("Polearm", "B", false, "taro_nspec"), ("Polearm", "B", true, "taro_nspec"), ("Polearm", "B+F", false, "taro_sspec"),
            ("Polearm", "B+F", true, "taro_sspec"), ("Polearm", "B+D", false, "taro_dspec"), ("Polearm", "B+U", false, "taro_uspec"),
            ("Polearm", "B+U", true, "taro_uspec"), ("Polearm", "S1+U", false, "taro_skyhook"), ("Polearm", "S2+U", false, "taro_reaper"),
            ("Polearm", "S2+U", true, "taro_reaper"), ("Polearm", "S2+D", false, "taro_jadecounter"),
            ("Polearm", "B+D", true, "taro_fallingspear"),
        };

        static IEnumerable<TestCaseData> Cases()
        {
            foreach (var r in Table)
                yield return new TestCaseData(r.cls, r.input, r.air, r.id)
                    .SetName($"Input_{r.cls}_{r.input.Replace("+", "").Replace("@", "_")}_{(r.air ? "Air" : "Ground")}_Starts_{r.id}");
        }

        [TestCaseSource(nameof(Cases))]
        public void Input_ResolvesToItsCsvRow(string cls, string input, bool air, string id)
        {
            // Arrange
            var c = (FighterClass)System.Enum.Parse(typeof(FighterClass), cls);
            var s = Arena(c, FighterClass.Heavy, -3000, 6000);

            // Act + Assert (StartRow asserts the resolved id)
            StartRow(s, c, id, input, air);
            Assert.AreEqual(0, s.F[0].MoveTick, "the row's tick 0 runs on the next step");
        }

        [Test]
        public void EveryCsvRow_IsReachableFromTheTable()
        {
            // Arrange: every row (except higher charge levels, reached by charging) must appear in Table.
            var listed = new HashSet<string>();
            foreach (var r in Table) listed.Add(r.id);

            // Act / Assert
            foreach (var c in Classes)
                for (int k = 0; k < ChibiMoveInfo.Count((int)c); k++)
                {
                    int slot = ChibiMovesetData.FirstNewSlot + k;
                    var e = ChibiMoveTable.Get((int)c, slot).Ext;
                    if (e.Has(ChibiMoveExt.FChargeLevel) && e.ChargeGroupBase != slot) continue;
                    Assert.IsTrue(listed.Contains(ChibiMoveInfo.Get((int)c, slot).Id), ChibiMoveInfo.Get((int)c, slot).Id + " unreachable");
                }
        }

        // ------------------------------------------------------------------ unchanged neutral inputs (CH-2)

        [TestCase(FighterClass.Melee)]
        [TestCase(FighterClass.Heavy)]
        [TestCase(FighterClass.Ranged)]
        [TestCase(FighterClass.Magic)]
        [TestCase(FighterClass.Polearm)]
        public void NeutralInputsOnTheGround_KeepTheExistingMoves(FighterClass c)
        {
            // Arrange / Act / Assert: neutral Light = combo (0), neutral Heavy = heavy (1), neutral / horizontal skill k = slot 2+k.
            var s = Arena(c); Game(s, P(ChibiDir.Neutral, ChibiButtons.Light));
            Assert.AreEqual(0, s.F[0].MoveSlot, "neutral Light = light combo");
            s = Arena(c); Game(s, P(ChibiDir.Neutral, ChibiButtons.Heavy));
            Assert.AreEqual(1, s.F[0].MoveSlot, "neutral Heavy = heavy");
            for (int k = 0; k < 3; k++)
            {
                if (!ChibiMoveTable.Get((int)c, 2 + k).Exists) continue;
                s = Arena(c); Game(s, P(ChibiDir.Neutral, (ChibiButtons)((int)ChibiButtons.Skill1 << k)));
                Assert.AreEqual(2 + k, s.F[0].MoveSlot, $"neutral Skill{k + 1} = existing skill");
                s = Arena(c); Game(s, P(ChibiDir.E, (ChibiButtons)((int)ChibiButtons.Skill1 << k)));
                Assert.AreEqual(2 + k, s.F[0].MoveSlot, $"horizontal Skill{k + 1} = existing skill");
            }
        }

        [TestCase(FighterClass.Melee)]
        [TestCase(FighterClass.Ranged)]
        public void HeavyInTheAir_AnyDirection_IsTheExistingHeavy(FighterClass c)
        {
            // Arrange
            var s = Arena(c);
            Place(s.F[0], -3000, 4000, 1);

            // Act
            Game(s, P(ChibiDir.N, ChibiButtons.Heavy));

            // Assert: rule 7 (Ranged / Magic keep air shots with Heavy, OD-2)
            Assert.AreEqual(1, s.F[0].MoveSlot);
        }

        [Test]
        public void EmptyTechniqueSlot_FallsBackToTheExistingSkill()
        {
            // Arrange: Kaia has no S3+D row (README kaia.md: falls back to Thrust Barrage, slot 4).
            var s = Arena(FighterClass.Melee);

            // Act
            Game(s, P(ChibiDir.S, ChibiButtons.Skill3));

            // Assert
            Assert.AreEqual(4, s.F[0].MoveSlot);
        }

        [Test]
        public void WrongGroundAir_TechniqueFallsBack_SpecialIsDropped()
        {
            // Arrange: Kaia S1+U is air only (Air Kick-off); on the ground it falls back to Dash & Thrust (slot 2).
            var s = Arena(FighterClass.Melee);
            Game(s, P(ChibiDir.N, ChibiButtons.Skill1));
            Assert.AreEqual(2, s.F[0].MoveSlot, "S1+U on the ground = existing skill 1");

            // Kaia B+D is ground only (Blade Trap): in the air the press is dropped.
            s = Arena(FighterClass.Melee);
            Place(s.F[0], -3000, 4000, 1);
            Game(s, P(ChibiDir.S, ChibiButtons.Special));
            Assert.AreEqual(-1, s.F[0].MoveSlot, "air B+D without an air down special is ignored");
        }

        [Test]
        public void TechniqueOnCooldown_FiresNothing_NotTheFallback()
        {
            // Arrange: Kaia Tech Roll (S1+D, cd 60) used once, then pressed again after the move ends but inside the cd.
            var s = Arena(FighterClass.Melee);
            var roll = Row(FighterClass.Melee, "kaia_roll");
            Game(s, P(ChibiDir.S, ChibiButtons.Skill1));
            Idle(s, roll.Startup + roll.Active + roll.Recovery + 1);
            Assert.AreEqual(-1, s.F[0].MoveSlot, "precondition: roll over");

            // Act
            Game(s, P(ChibiDir.S, ChibiButtons.Skill1));

            // Assert
            Assert.AreEqual(-1, s.F[0].MoveSlot, "technique on cooldown: nothing fires");
            Assert.AreEqual(0, s.F[0].Cd[0], "the existing skill 1 did not fire either");
        }

        [Test]
        public void SameTick_LightBeatsHeavySpecialAndSkills()
        {
            // Arrange
            var s = Arena(FighterClass.Melee);

            // Act
            Game(s, P(ChibiDir.E, ChibiButtons.Light | ChibiButtons.Heavy | ChibiButtons.Special | ChibiButtons.Skill1));

            // Assert
            Assert.AreEqual("kaia_ftilt", IdOf(s.F[0]));
        }

        [Test]
        public void SameTick_SpecialBeatsSkills()
        {
            var s = Arena(FighterClass.Melee);
            Game(s, P(ChibiDir.N, ChibiButtons.Special | ChibiButtons.Skill2));
            Assert.AreEqual("kaia_uspec", IdOf(s.F[0]));
        }

        [Test]
        public void Reload_GatesOnlyTheNeutralNormals()
        {
            // Arrange: Ren reloading (SetCd > 0).
            var s = Arena(FighterClass.Ranged);
            s.F[0].SetCd = 50;

            // Act: neutral Light is gated (falls through), forward Light (tilt) is not.
            Game(s, P(ChibiDir.Neutral, ChibiButtons.Light));
            int neutral = s.F[0].MoveSlot;
            var s2 = Arena(FighterClass.Ranged);
            s2.F[0].SetCd = 50;
            Game(s2, P(ChibiDir.E, ChibiButtons.Light));

            // Assert
            Assert.AreEqual(-1, neutral, "rule 4 gated by reload");
            Assert.AreEqual("ren_ftilt", IdOf(s2.F[0]), "tilts are not gated by reload");
        }

        [Test]
        public void SpearOut_GatesLightAndHeavyRows_NotSpecials()
        {
            // Arrange
            var s = Arena(FighterClass.Polearm);
            s.F[0].SpearOut = true;

            // Act
            Game(s, P(ChibiDir.E, ChibiButtons.Heavy));
            int smash = s.F[0].MoveSlot;
            Game(s, P(ChibiDir.Neutral, ChibiButtons.Special));

            // Assert
            Assert.AreEqual(-1, smash, "forward smash blocked while the spear flies");
            Assert.AreEqual("taro_nspec", IdOf(s.F[0]), "specials are not gated by SpearOut");
        }

        // ------------------------------------------------------------------ dash window

        [Test]
        public void DashWindow_LightOnDashTick_StartsDashAttack()
        {
            // Arrange
            var s = Arena(FighterClass.Melee);

            // Act: Light + Dash on the same tick (dash start runs before attack selection).
            Game(s, P(ChibiDir.E, ChibiButtons.Light | ChibiButtons.Dash));

            // Assert
            Assert.AreEqual("kaia_dash", IdOf(s.F[0]));
            Assert.AreEqual(0, s.F[0].DashTicks, "the dash attack ends the dash");
        }

        [TestCase(16, true)]
        [TestCase(17, false)]
        public void DashWindow_EndsSixteenTicksAfterTheDashStart(int ticksAfter, bool expectDashAttack)
        {
            // Arrange: dash on tick 0, Light on tick ticksAfter (16 = last tick of the window).
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, -3000, 6000);
            Game(s, P(ChibiDir.E, ChibiButtons.Dash));
            Idle(s, ticksAfter - 1);

            // Act
            Game(s, P(ChibiDir.Neutral, ChibiButtons.Light));

            // Assert
            Assert.AreEqual(expectDashAttack ? Slot(FighterClass.Melee, "kaia_dash") : 0, s.F[0].MoveSlot);
        }

        [Test]
        public void DashWindow_LightDown_KaiaLowSweep_OthersDashAttack()
        {
            var s = Arena(FighterClass.Melee);
            Game(s, P(ChibiDir.E, ChibiButtons.Dash));
            Game(s, P(ChibiDir.S, ChibiButtons.Light));
            Assert.AreEqual("kaia_lowsweep", IdOf(s.F[0]));

            s = Arena(FighterClass.Heavy);
            Game(s, P(ChibiDir.E, ChibiButtons.Dash));
            Game(s, P(ChibiDir.S, ChibiButtons.Light));
            Assert.AreEqual("bruna_dash", IdOf(s.F[0]));
        }

        [Test]
        public void DashBackward_DashAttackFacesTheDashDirection()
        {
            var s = Arena(FighterClass.Melee);
            Game(s, P(ChibiDir.W, ChibiButtons.Dash));
            Game(s, P(ChibiDir.Neutral, ChibiButtons.Light));
            Assert.AreEqual("kaia_dash", IdOf(s.F[0]));
            Assert.AreEqual(-1, s.F[0].Facing);
        }

        // ------------------------------------------------------------------ air facing (OD-1)

        [Test]
        public void Air_FacingIsLocked_BackInputGivesBackAir()
        {
            // Arrange
            var s = Arena(FighterClass.Melee);
            Place(s.F[0], -3000, 4000, 1);

            // Act: hold back (W) one tick, then back + Light.
            Game(s, P(ChibiDir.W));
            int facing = s.F[0].Facing;
            Game(s, P(ChibiDir.W, ChibiButtons.Light));

            // Assert
            Assert.AreEqual(1, facing, "air drift does not turn the fighter");
            Assert.AreEqual("kaia_bair", IdOf(s.F[0]));
        }

        [Test]
        public void Air_SideSpecialBackward_TurnsAround()
        {
            var s = Arena(FighterClass.Melee);
            Place(s.F[0], -3000, 4000, 1);
            Game(s, P(ChibiDir.W, ChibiButtons.Special));
            Assert.AreEqual("kaia_sspec", IdOf(s.F[0]));
            Assert.AreEqual(-1, s.F[0].Facing, "turnaround side special");
        }

        [Test]
        public void Ground_HorizontalLight_TurnsFirstThenForwardTilt()
        {
            var s = Arena(FighterClass.Melee);
            Game(s, P(ChibiDir.W, ChibiButtons.Light));
            Assert.AreEqual("kaia_ftilt", IdOf(s.F[0]));
            Assert.AreEqual(-1, s.F[0].Facing);
        }

        [Test]
        public void AirOnce_SecondUseInTheSameAirtime_IsRefused_LandingRestoresIt()
        {
            // Arrange: Kaia Skyhook (aironce) used in the air.
            var s = Arena(FighterClass.Melee);
            var row = Row(FighterClass.Melee, "kaia_uspec");
            Place(s.F[0], -3000, 6000, 1);
            Game(s, P(ChibiDir.N, ChibiButtons.Special));
            Assert.AreEqual("kaia_uspec", IdOf(s.F[0]));
            Idle(s, row.Startup + row.Active + row.Recovery);
            Assert.IsFalse(s.F[0].Grounded, "precondition: still airborne");

            // Act
            Game(s, P(ChibiDir.N, ChibiButtons.Special));

            // Assert
            Assert.AreEqual(-1, s.F[0].MoveSlot, "aironce spent");
            for (int t = 0; t < 400 && !s.F[0].Grounded; t++) Game(s, N, N);
            Assert.AreEqual(0, s.F[0].AirUsedMask, "landing clears the mask");
        }
    }
}
