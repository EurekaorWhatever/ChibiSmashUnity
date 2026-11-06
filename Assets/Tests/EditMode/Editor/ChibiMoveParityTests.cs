using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Checks that moves in the deterministic simulation behave like the original Fighter versions.
    /// One test group per ported move; reference values are cited from Fighter.cs.
    /// </summary>
    public class ChibiMoveParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;     // let spawn invulnerability wear off
        const int WatchTicks = 60;       // longer than any move under test
        const int DashThrustSlot = 2;    // Melee skill 1

        /// <summary>
        /// Fresh match, P1 (Melee) at x = 0 facing right, dummy P2 (Heavy) at <paramref name="distance"/> milli-units.
        /// P1 presses <paramref name="button"/> for two ticks. Returns P2's HP loss within WatchTicks.
        /// </summary>
        static int DamageFromPress(int distance, ChibiButtons button, out ChibiSimState state)
            => DamageFromPress(FighterClass.Melee, distance, button, out state);

        /// <summary>Same as above with <paramref name="attacker"/> as P1's class.</summary>
        static int DamageFromPress(FighterClass attacker, int distance, ChibiButtons button, out ChibiSimState state)
        {
            var cfg = new ChibiMatchConfig { Seed = Seed, Class0 = attacker, Class1 = FighterClass.Heavy };
            state = new ChibiSimState();
            ChibiSimulation.Init(state, cfg);
            for (int t = 0; t < SettleTicks; t++) ChibiSimulation.Step(state, default, default);

            state.F[0].X = 0; state.F[0].Facing = 1;
            state.F[1].X = distance; state.F[1].Facing = -1;
            int hp0 = state.F[1].Hp;
            for (int t = 0; t < WatchTicks; t++)
            {
                var p1 = t < 2 ? ChibiInputPacket.Make(ChibiDir.Neutral, button) : default;
                ChibiSimulation.Step(state, p1, default);
            }
            return hp0 - state.F[1].Hp;
        }

        // ------------------------------------------------------------------ melee normals reach (Fighter.NormalStrike)

        // Original: MeleeBox(Def.reach * (heavy ? 1.15 : 1), 1.3) spans from the attacker's centre to reach + 0.3 ahead,
        // and the target's collider is 0.375 half-wide, so it hits targets whose centre is up to reach + 0.675 away.
        // Def.reach from ClassData.cs: Melee 1.35, Heavy 1.55, Polearm 2.0.
        const int HurtHalfW = 375, BoxPad = 300, EdgeMargin = 50;

        static int OriginalReach(FighterClass c, bool heavy)
        {
            int reach = c == FighterClass.Melee ? 1350 : c == FighterClass.Heavy ? 1550 : 2000;
            return heavy ? (reach * 115 + 50) / 100 : reach;
        }

        [TestCase(FighterClass.Melee, false)]
        [TestCase(FighterClass.Melee, true)]
        [TestCase(FighterClass.Heavy, false)]
        [TestCase(FighterClass.Heavy, true)]
        [TestCase(FighterClass.Polearm, false)]
        [TestCase(FighterClass.Polearm, true)]
        public void MeleeNormal_HitsAtOriginalMaxReach(FighterClass cls, bool heavy)
        {
            int maxHit = OriginalReach(cls, heavy) + BoxPad + HurtHalfW;
            var button = heavy ? ChibiButtons.Heavy : ChibiButtons.Light;
            int dmg = DamageFromPress(cls, maxHit - EdgeMargin, button, out _);
            Assert.Greater(dmg, 0, cls + (heavy ? " heavy" : " light") + " should hit at " + (maxHit - EdgeMargin) / 1000f + " units like the original");
        }

        [TestCase(FighterClass.Melee, false)]
        [TestCase(FighterClass.Melee, true)]
        [TestCase(FighterClass.Heavy, false)]
        [TestCase(FighterClass.Heavy, true)]
        [TestCase(FighterClass.Polearm, false)]
        [TestCase(FighterClass.Polearm, true)]
        public void MeleeNormal_MissesJustBeyondOriginalReach(FighterClass cls, bool heavy)
        {
            int maxHit = OriginalReach(cls, heavy) + BoxPad + HurtHalfW;
            var button = heavy ? ChibiButtons.Heavy : ChibiButtons.Light;
            int dmg = DamageFromPress(cls, maxHit + EdgeMargin, button, out _);
            Assert.AreEqual(0, dmg, cls + (heavy ? " heavy" : " light") + " should not reach further than the original");
        }

        // ------------------------------------------------------------------ Melee: Dash & Thrust (Fighter.DashThrust)

        [TestCase(0)]
        [TestCase(250)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(1500)]
        [TestCase(3000)]
        public void DashThrust_HitsFromCloseToFar(int distance)
        {
            int dmg = DamageFromPress(distance, ChibiButtons.Skill1, out _);
            Assert.Greater(dmg, 0, "Dash & Thrust should hit a target " + distance / 1000f + " units away");
        }

        [Test]
        public void DashThrust_HitsOnlyOncePerUse()
        {
            int light = DamageFromPress(0, ChibiButtons.Light, out _);
            int dash = DamageFromPress(0, ChibiButtons.Skill1, out _);
            // Dash is a single 1.6x hit; a multi-hit bug would be several times the light-attack damage.
            Assert.Greater(dash, light, "1.6x should beat a 1.0x light hit");
            Assert.Less(dash, light * 2, "Dash & Thrust must hit once, not every frame of the dash");
        }

        [Test]
        public void DashThrust_FrameData_MatchesOriginal()
        {
            var mv = ChibiMoveTable.Get((int)FighterClass.Melee, DashThrustSlot);
            Assert.AreEqual(0, mv.Startup, "original checks for a hit from the first frame");
            Assert.AreEqual(13, mv.Active, "original dash lasts 0.22 s = 13 ticks");
            Assert.AreEqual(24, mv.Startup + mv.Active + mv.Recovery, "original locks actions for 0.4 s = 24 ticks");
            Assert.AreEqual(1600, mv.Mult, "original damage is 1.6x");
        }
    }
}
