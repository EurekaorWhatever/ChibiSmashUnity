using UnityEngine;

namespace ChibiClash
{
    public struct HitSpec
    {
        public float mult;
        public Vector2 knockback;
        public float stun;
        public bool projectile;
        public bool unblockable;

        public static HitSpec Make(float mult, Vector2 kb, float stun, bool projectile = false, bool unblockable = false)
        {
            return new HitSpec { mult = mult, knockback = kb, stun = stun, projectile = projectile, unblockable = unblockable };
        }
    }

    public enum HitResult { Miss, Hit, Blocked, Parried, Negated }

    /// <summary>
    /// Every damage number in the game is produced here. In online play this class runs on the
    /// authoritative server only (GDD 3: hitboxes and damage are validated server-side), so clients
    /// cannot modify the formula.
    /// </summary>
    public static class CombatAuthority
    {
        public const float PerfectParryWindow = 0.18f;
        public const float GuardBreakStun = 1.0f;     // seconds stunned when the guard shatters
        public const float GuardBrokenTime = 3.0f;    // seconds before a broken guard can be raised again
        public const float GuardRegenPerSecond = 24f;

        /// <summary>Share of damage that still gets through a block: 40% at 10 DEF, 30% at 20 DEF, 20% at 30 DEF.</summary>
        public static float BlockedFraction(float defense) => Mathf.Clamp(0.5f - defense * 0.01f, 0.1f, 0.5f);

        /// <summary>Guard durability: blocked raw damage drains it; at 0 the guard breaks. 70 / 100 / 130 HP at 10 / 20 / 30 DEF.</summary>
        public static float GuardMax(float defense) => 40f + defense * 3f;

        /// <summary>Archer and mage hits push half as far (balance).</summary>
        public static float KnockbackScale(FighterClass attacker) =>
            attacker == FighterClass.Ranged || attacker == FighterClass.Magic ? 0.5f : 1f;

        public static float RollDamage(Fighter attacker, float mult, out bool crit)
        {
            crit = Random.value < attacker.CritRate;
            float dmg = attacker.AttackPower * mult;
            if (crit) dmg *= 1f + attacker.Def.critDmg;
            return dmg;
        }

        /// <summary>Every 10 defense removes 1 incoming damage.</summary>
        public static float ApplyDefense(float raw, float defense)
        {
            return Mathf.Max(1f, raw - Mathf.Floor(defense / 10f));
        }
    }
}
