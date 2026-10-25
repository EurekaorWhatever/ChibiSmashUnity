using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// A weapon drawn from a hand-painted sprite in Resources/Weapons. The sprite file name is the weapon id,
    /// e.g. Weapon_Melee_SharkSword.png. Weapons are cosmetic: every weapon of a class has identical stats.
    /// </summary>
    public class WeaponDef
    {
        public string resource;        // file name (no extension) under Resources/Weapons
        public string name;            // shown in menus / HUD
        public FighterClass cls;
        public float rotation;         // degrees to turn the sprite so its business end points along +x
        public float gripX, gripY;     // where the hand holds it: 0..1 from the sprite's bottom-left
        public float length;           // world units along the sprite's longest side
        public bool flip;              // mirror the art so its striking side faces the opponent

        Sprite sprite;
        bool tried;

        public Sprite Sprite
        {
            get
            {
                if (!tried)
                {
                    tried = true;
                    sprite = Resources.Load<Sprite>("Weapons/" + resource);
                    if (sprite == null) Debug.LogWarning("ChibiClash: weapon sprite missing: Weapons/" + resource);
                }
                return sprite;
            }
        }

        public static readonly List<WeaponDef> All = new List<WeaponDef>
        {
            // blade/head points UP in the artwork -> rotate -90 so it points forward; spear/claymore tips point DOWN -> +90
            new WeaponDef { cls = FighterClass.Melee,   name = "Shark Sword",   resource = "Weapon_Melee_SharkSword",     rotation = -90f, gripX = 0.527f, gripY = 0.147f, length = 1.7f },
            new WeaponDef { cls = FighterClass.Heavy,   name = "Skull Hammer",  resource = "Weapon_Heavy_SkullHammer",    rotation = -90f, gripX = 0.506f, gripY = 0.176f, length = 1.9f, flip = true },
            new WeaponDef { cls = FighterClass.Heavy,   name = "Spiked Mace",   resource = "Weapon_Heavy_SpikedMace",     rotation = -90f, gripX = 0.500f, gripY = 0.327f, length = 2.0f },
            new WeaponDef { cls = FighterClass.Heavy,   name = "Claymore",      resource = "Weapon_Heavy_Claymore",       rotation = 90f,  gripX = 0.495f, gripY = 0.843f, length = 2.3f },
            new WeaponDef { cls = FighterClass.Ranged,  name = "Finger Bow",    resource = "Weapon_Ranged_FingerBow",     rotation = 0f,   gripX = 0.628f, gripY = 0.483f, length = 1.5f },
            new WeaponDef { cls = FighterClass.Magic,   name = "Skull Staff",   resource = "Weapon_Magic_SkullStaff",     rotation = -90f, gripX = 0.437f, gripY = 0.413f, length = 1.9f },
            new WeaponDef { cls = FighterClass.Magic,   name = "Orb Totem",     resource = "Weapon_Magic_OrbTotem",       rotation = 0f,   gripX = 0.401f, gripY = 0.228f, length = 1.5f },
            new WeaponDef { cls = FighterClass.Polearm, name = "Skull Spear",   resource = "Weapon_Polearm_SkullSpear",   rotation = 90f,  gripX = 0.489f, gripY = 0.629f, length = 2.5f },
        };

        public static List<WeaponDef> ForClass(FighterClass c) => All.FindAll(w => w.cls == c);

        /// <summary>Weapon for a class by variant index (clamped), so stale indices never crash.</summary>
        public static WeaponDef Get(FighterClass c, int index)
        {
            var list = ForClass(c);
            return list[Mathf.Clamp(index, 0, list.Count - 1)];
        }
    }
}
