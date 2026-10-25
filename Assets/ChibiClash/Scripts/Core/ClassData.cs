using UnityEngine;

namespace ChibiClash
{
    public enum FighterClass { Melee, Heavy, Ranged, Magic, Polearm }

    public enum Gesture { None, CrossArms, PunchForward, RaiseHands }

    /// <summary>Static design data for one of the five classes (GDD section 4.2).</summary>
    public class ClassDef
    {
        public FighterClass type;
        public string heroName;
        public string className;
        public string weapon;
        public float attack, health, defense;
        public float critRate = 0.05f;
        public float critDmg = 0.5f;
        public float moveSpeed;
        public int patternLength;
        public float reach;
        public bool rangedNormals;
        public string[] passiveNames;
        public string[] passiveDescs;
        public string[] skillNames;
        public string[] skillDescs;
        public float[] skillCooldowns;
        public string ultName, ultDesc;
        public Gesture ultGesture;
        public Color color;

        public int SkillCount => skillNames.Length;

        public static ClassDef Get(FighterClass c) => All[(int)c];

        public static string GestureName(Gesture g)
        {
            switch (g)
            {
                case Gesture.CrossArms: return "CROSS ARMS";
                case Gesture.PunchForward: return "PUNCH FORWARD";
                case Gesture.RaiseHands: return "RAISE BOTH HANDS";
                default: return "-";
            }
        }

        public static readonly ClassDef[] All =
        {
            new ClassDef
            {
                type = FighterClass.Melee, heroName = "Kira", className = "Melee", weapon = "Rapier",
                attack = 20, health = 500, defense = 20, moveSpeed = 7.6f, patternLength = 4, reach = 1.35f,
                passiveNames = new[] { "Swift Blade", "Blood Edge" },
                passiveDescs = new[] { "+15% attack speed", "5% life steal" },
                skillNames = new[] { "Dash & Thrust", "Parry", "Thrust Barrage" },
                skillDescs = new[] { "Dash forward and thrust into the opponent", "Perfect timing reflects damage", "Thrust many times quickly" },
                skillCooldowns = new[] { 4f, 5f, 7f },
                ultName = "SWORD AURA", ultDesc = "10s: normal attacks unleash a horizontal wave",
                ultGesture = Gesture.CrossArms, color = new Color(0.35f, 0.65f, 1f)
            },
            new ClassDef
            {
                type = FighterClass.Heavy, heroName = "Brutus", className = "Heavy", weapon = "Hammer",
                attack = 35, health = 600, defense = 30, moveSpeed = 6.3f, patternLength = 5, reach = 1.55f,
                passiveNames = new[] { "Crusher", "Juggernaut" },
                passiveDescs = new[] { "+20% damage, -15% attack speed", "+20% damage, -15% move speed" },
                skillNames = new[] { "Grab", "Tornado Spin" },
                skillDescs = new[] { "Grab the opponent and smash them on the ground", "Spin like a tornado, hitting repeatedly" },
                skillCooldowns = new[] { 6f, 7f },
                ultName = "SMASH", ultDesc = "Smash the floor to create a shockwave",
                ultGesture = Gesture.RaiseHands, color = new Color(0.95f, 0.45f, 0.25f)
            },
            new ClassDef
            {
                type = FighterClass.Ranged, heroName = "Pip", className = "Ranged", weapon = "Bow",
                attack = 15, health = 400, defense = 10, moveSpeed = 7.3f, patternLength = 4, reach = 0.9f, rangedNormals = true,
                passiveNames = new[] { "Eagle Eye", "Tailwind" },
                passiveDescs = new[] { "+20% crit rate", "+10% move speed for 10s (30s cooldown)" },
                skillNames = new[] { "Backstep Shot", "Bowstring Choke", "Arrow Barrage" },
                skillDescs = new[] { "Dash back horizontally and shoot", "Strangle a close opponent and push them away", "Shoot an arrow barrage horizontally" },
                skillCooldowns = new[] { 4f, 6f, 7f },
                ultName = "THOUSAND ARROWS", ultDesc = "One arrow multiplies into a storm of arrows",
                ultGesture = Gesture.PunchForward, color = new Color(0.4f, 0.85f, 0.4f)
            },
            new ClassDef
            {
                type = FighterClass.Magic, heroName = "Luma", className = "Magic", weapon = "Staff",
                attack = 20, health = 375, defense = 10, moveSpeed = 6.9f, patternLength = 4, reach = 0.9f, rangedNormals = true,
                passiveNames = new[] { "Hex", "Ward" },
                passiveDescs = new[] { "10% chance on hit: -15% move, -10% dmg, -15% atk speed", "20% chance to block damage" },
                skillNames = new[] { "Homing Fireball", "Summon Minions", "Thunder Cloud" },
                skillDescs = new[] { "Fire a homing fire projectile", "Summon 3 minions", "A cloud follows the foe and strikes with thunder" },
                skillCooldowns = new[] { 4f, 12f, 10f },
                ultName = "FIRE RING", ultDesc = "10s ring of fire: burns foes, blocks projectiles",
                ultGesture = Gesture.CrossArms, color = new Color(0.72f, 0.45f, 0.98f)
            },
            new ClassDef
            {
                type = FighterClass.Polearm, heroName = "Ren", className = "Polearm", weapon = "Spear",
                attack = 25, health = 500, defense = 20, moveSpeed = 7.0f, patternLength = 4, reach = 2.0f,
                passiveNames = new[] { "Armor Break", "Patience" },
                passiveDescs = new[] { "10% chance on hit: -15% enemy defense", "After 3s without attacking, next hit +50%" },
                skillNames = new[] { "Leap Slam", "Spin Guard", "Throw & Recall" },
                skillDescs = new[] { "Extend, jump and swing downwards", "Spin the polearm to block projectiles", "Throw the spear and call it back" },
                skillCooldowns = new[] { 5f, 6f, 6f },
                ultName = "POLEARM KICK", ultDesc = "Stab the foe, then kick the handle in deeper",
                ultGesture = Gesture.PunchForward, color = new Color(0.97f, 0.8f, 0.3f)
            },
        };
    }
}
