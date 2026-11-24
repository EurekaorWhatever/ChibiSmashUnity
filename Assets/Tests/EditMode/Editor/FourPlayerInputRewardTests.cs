using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Story type: Logic. Evidence: automated EditMode unit test. Gate: BLOCKING.
    /// Guards for 3-4 fighter matches (TP-9, 2026-11-24): players 3 and 4 have no keyboard half, so binding lookups for
    /// them must neither throw (Bindings.Get indexes Defaults[player], which has 2 rows) nor read P1 / P2 keys; and a
    /// match with no human in the local slot must not pay rewards.
    /// </summary>
    public class FourPlayerInputRewardTests
    {
        [TestCase(2)]
        [TestCase(3)]
        public void Bindings_PlayersWithoutKeys_NeverThrowNorReadKeys(int player)
        {
            // Arrange / Act / Assert
            Assert.IsFalse(Bindings.HasKeys(player));
            Assert.DoesNotThrow(() => Bindings.Held(player, BindAction.Jump));
            Assert.DoesNotThrow(() => Bindings.Pressed(player, BindAction.Light));
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
        public void Rewards_LocalSlotHuman_Allowed()
        {
            Assert.IsTrue(MatchRewardRules.LocalSlotIsHuman(new[] { false, true, true, true }, 0));
        }

        [Test]
        public void Rewards_AllCpu_NoRewards()
        {
            Assert.IsFalse(MatchRewardRules.LocalSlotIsHuman(new[] { true, true, true, true }, 0));
        }

        [TestCase(-1)]
        [TestCase(4)]
        public void Rewards_SlotOutOfRange_NoRewards(int slot)
        {
            Assert.IsFalse(MatchRewardRules.LocalSlotIsHuman(new[] { false, false, false, false }, slot));
        }

        [Test]
        public void Rewards_NoCpuFlags_NoRewards()
        {
            Assert.IsFalse(MatchRewardRules.LocalSlotIsHuman(null, 0));
        }
    }
}
