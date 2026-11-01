using System;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponBrawl
{
    public enum WeaponType { Melee, Heavy, Ranged, Magic, Polearm }

    public enum PassiveId
    {
        SwiftBlade,     // Melee: +15% attack speed
        Bloodthirst,    // Melee: 5% life steal
        BruteForce,     // Heavy: +20% damage, -15% attack speed
        Juggernaut,     // Heavy: +20% damage, -15% movement speed
        EagleEye,       // Ranged: +20% crit rate
        FleetFoot,      // Ranged: +10% move speed for 10s, 30s cooldown
        Hex,            // Magic: 10% chance on hit to slow / weaken the target
        ArcaneWard,     // Magic: 20% chance to block damage
        ArmorPiercer,   // Polearm: 10% chance on hit to reduce enemy defense by 15%
        PatientStrike,  // Polearm: 3s without attacking -> next attack +50% damage
    }

    [Serializable]
    public class PassiveInfo
    {
        public PassiveId id;
        public string name;
        public string description;

        public PassiveInfo(PassiveId id, string name, string description)
        {
            this.id = id;
            this.name = name;
            this.description = description;
        }
    }

    [Serializable]
    public class SkillInfo
    {
        public string name;
        public string description;
        public float cooldown;

        public SkillInfo(string name, string description, float cooldown = 0f)
        {
            this.name = name;
            this.description = description;
            this.cooldown = cooldown;
        }
    }

    [Serializable]
    public class WeaponDefinition
    {
        public WeaponType type;
        public string displayName;
        public string examples;
        public string playstyle;   // archetype tag shown on the select screen
        public string[] variants;  // looks only: every variant of a type has the same stats, skills and passives
        public Color color;

        [Header("Base stats")]
        public float attack;
        public float health;
        public float defense;      // every 10 defense = -1 incoming damage
        public float critRate = 0.05f;
        public float critDamage = 0.5f;

        public int comboLength;
        public PassiveInfo[] passives;
        public SkillInfo[] skills;
        public SkillInfo ultimate;
    }

    /// <summary>All 5 weapon classes from the technical document (section 4.2).</summary>
    public static class WeaponDatabase
    {
        static readonly WeaponDefinition[] definitions =
        {
            new WeaponDefinition
            {
                type = WeaponType.Melee,
                displayName = "Melee",
                examples = "Sword, rapier",
                playstyle = "RUSH DOWN",
                variants = new[] { "Sword", "Rapier" },
                color = new Color(0.35f, 0.75f, 1f),
                attack = 20, health = 500, defense = 20,
                comboLength = 4,
                passives = new[]
                {
                    new PassiveInfo(PassiveId.SwiftBlade, "Swift Blade", "Attack speed +15%."),
                    new PassiveInfo(PassiveId.Bloodthirst, "Bloodthirst", "Life steal 5% of damage dealt."),
                },
                skills = new[]
                {
                    new SkillInfo("Dash and Thrust", "Dash forward and thrust into the opponent.", 5f),
                    new SkillInfo("Parry", "Activate at the right time to reflect the incoming damage.", 6f),
                    new SkillInfo("Thrust Barrage", "Thrust many times quickly.", 8f),
                },
                ultimate = new SkillInfo("Sword Aura", "For 10 seconds, normal attacks unleash a horizontal projectile wave."),
            },
            new WeaponDefinition
            {
                type = WeaponType.Heavy,
                displayName = "Heavy",
                examples = "Hammer, mace, claymore",
                playstyle = "GRAPPLER",
                variants = new[] { "Hammer", "Mace", "Claymore" },
                color = new Color(0.95f, 0.55f, 0.2f),
                attack = 35, health = 600, defense = 30,
                comboLength = 5,
                passives = new[]
                {
                    new PassiveInfo(PassiveId.BruteForce, "Brute Force", "Attack damage +20%, attack speed -15%."),
                    new PassiveInfo(PassiveId.Juggernaut, "Juggernaut", "Attack damage +20%, movement speed -15%."),
                },
                skills = new[]
                {
                    new SkillInfo("Energy", "Release energy or a blade that surges through the air and stuns the enemy on hit.", 6f),
                    new SkillInfo("Grab", "Grab the opponent and smash them on the ground.", 8f),
                    new SkillInfo("Spin", "Spin like a tornado, then attack the opponent.", 7f),
                },
                ultimate = new SkillInfo("Smash", "Smash the floor with your weapon to create a shockwave."),
            },
            new WeaponDefinition
            {
                type = WeaponType.Ranged,
                displayName = "Ranged",
                examples = "Crossbow, bow, slingshot",
                playstyle = "ZONER",
                variants = new[] { "Bow", "Crossbow", "Slingshot" },
                color = new Color(0.45f, 0.85f, 0.35f),
                attack = 15, health = 400, defense = 10,
                comboLength = 4,
                passives = new[]
                {
                    new PassiveInfo(PassiveId.EagleEye, "Eagle Eye", "Crit rate +20%."),
                    new PassiveInfo(PassiveId.FleetFoot, "Fleet Foot", "Movement speed +10% for 10 seconds when you act. Cooldown 30 seconds."),
                },
                skills = new[]
                {
                    new SkillInfo("Dash Shot", "Dash back horizontally and shoot.", 5f),
                    new SkillInfo("Bowstring Choke", "Close range: strangle the opponent with the bow and push them away.", 7f),
                    new SkillInfo("Arrow Barrage", "Shoot an arrow barrage horizontally.", 8f),
                },
                ultimate = new SkillInfo("Thousand Arrows", "Shoot one arrow that multiplies itself into a rain of arrows."),
            },
            new WeaponDefinition
            {
                type = WeaponType.Magic,
                displayName = "Magic",
                examples = "Fire, water, earth",
                playstyle = "SETPLAY",
                variants = new[] { "Fire", "Water", "Earth" },
                color = new Color(0.75f, 0.45f, 1f),
                attack = 20, health = 375, defense = 10,
                comboLength = 4,
                passives = new[]
                {
                    new PassiveInfo(PassiveId.Hex, "Hex", "Attacks have a 10% chance to inflict a debuff: -15% movement speed, -10% attack damage, -15% attack speed."),
                    new PassiveInfo(PassiveId.ArcaneWard, "Arcane Ward", "20% chance to block damage (defensive magic)."),
                },
                skills = new[]
                {
                    new SkillInfo("Fireball", "Shoot a homing fire projectile horizontally.", 4f),
                    new SkillInfo("Summon Minions", "Summon 3 minions that chase the opponent.", 12f),
                    new SkillInfo("Thunder Cloud", "Cast a cloud that follows the opponent and strikes with thunder.", 10f),
                },
                ultimate = new SkillInfo("Fire Ring", "For 10s, a ring of fire around you damages opponents and blocks projectiles."),
            },
            new WeaponDefinition
            {
                type = WeaponType.Polearm,
                displayName = "Polearm",
                examples = "Spear, lance, halberd",
                playstyle = "MIX UP",
                variants = new[] { "Spear", "Lance", "Halberd" },
                color = new Color(0.95f, 0.8f, 0.3f),
                attack = 25, health = 500, defense = 20,
                comboLength = 4,
                passives = new[]
                {
                    new PassiveInfo(PassiveId.ArmorPiercer, "Armor Piercer", "Attacks have a 10% chance to decrease enemy defense by 15%."),
                    new PassiveInfo(PassiveId.PatientStrike, "Patient Strike", "When not attacking for 3s, your next attack deals 50% more damage."),
                },
                skills = new[]
                {
                    new SkillInfo("Leap Strike", "Extend your weapon, jump, and swing it downwards.", 6f),
                    new SkillInfo("Spin Guard", "Spin the polearm to block incoming projectiles.", 7f),
                    new SkillInfo("Spear Throw", "Throw the spear at the opponent and call it back.", 6f),
                },
                ultimate = new SkillInfo("Polearm Kick", "Stab the polearm into the opponent's chest, jump, and kick the handle to drive it deeper."),
            },
        };

        public static IReadOnlyList<WeaponDefinition> All => definitions;

        public static WeaponDefinition Get(WeaponType type)
        {
            foreach (var d in definitions)
                if (d.type == type) return d;
            throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
}
