using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// The playable characters (one per class), from the character sheets in design/art/character-sheets/.
    /// Presentation data only: the simulation never reads this (gameplay is keyed by <see cref="FighterClass"/>), so
    /// names, colours and art can change without touching the online core.
    ///
    /// Art contract (by <see cref="CharacterInfo.Id"/>):
    ///   Resources/Characters/&lt;Id&gt;.png              full-body front cutout, 1024x1024, transparent, feet at bottom centre
    ///   Resources/Characters/&lt;Id&gt;_Portrait.png     bust portrait for menus / HUD (square, transparent)
    ///   Resources/Weapons/Hero_&lt;Id&gt;.png             signature weapon cutout
    ///   Resources/Animations/&lt;Id&gt;/&lt;Clip&gt;.png      horizontal sprite strips (see SpriteAnim / tools/art)
    /// </summary>
    public sealed class CharacterInfo
    {
        /// <summary>Asset key and save id (stable, ASCII).</summary>
        public string Id;
        /// <summary>Display name.</summary>
        public string Name;
        public FighterClass Class;
        /// <summary>Role line from the sheet ("Agile Striker").</summary>
        public string Role;
        /// <summary>Style blurb from the sheet.</summary>
        public string Style;
        /// <summary>Signature weapon name.</summary>
        public string Weapon;
        /// <summary>Main and accent colours from the sheet palette (UI frames, VFX tint, fallback body colours).</summary>
        public Color Primary, Accent, Fx;
        /// <summary>Reference stat bars from the sheet, 1..5 (display only; real numbers live in ChibiCombatStats).</summary>
        public int Power, Speed, Range, Endurance, Recovery;
    }

    /// <summary>Lookup for the five characters. Index order = <see cref="FighterClass"/> order.</summary>
    public static class CharacterRoster
    {
        static Color C(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

        /// <summary>All characters, indexed by (int)FighterClass.</summary>
        public static readonly CharacterInfo[] All =
        {
            new CharacterInfo
            {
                Id = "Kaia", Name = "Kaia", Class = FighterClass.Melee, Role = "Agile Striker",
                Style = "Fast, mobile close-range combat with precise, chainable slashes.", Weapon = "Twin Curved Swords",
                Primary = C(232, 92, 92), Accent = C(32, 168, 160), Fx = C(70, 220, 210),
                Power = 3, Speed = 5, Range = 3, Endurance = 2, Recovery = 4,
            },
            new CharacterInfo
            {
                Id = "Bruna", Name = "Bruna", Class = FighterClass.Heavy, Role = "Bruiser",
                Style = "High-damage, high-durability brawler that controls space with weighty attacks.", Weapon = "Two-Handed War Hammer",
                Primary = C(240, 128, 48), Accent = C(40, 72, 160), Fx = C(255, 200, 60),
                Power = 5, Speed = 2, Range = 3, Endurance = 5, Recovery = 3,
            },
            new CharacterInfo
            {
                Id = "Ren", Name = "Ren", Class = FighterClass.Ranged, Role = "Agile Marksman",
                Style = "High-mobility zoning and precision pressure with versatile arrow tech.", Weapon = "Mechanized Recurve Bow",
                Primary = C(240, 180, 40), Accent = C(40, 120, 220), Fx = C(80, 200, 255),
                Power = 3, Speed = 4, Range = 5, Endurance = 2, Recovery = 3,
            },
            new CharacterInfo
            {
                Id = "Lyra", Name = "Lyra", Class = FighterClass.Magic, Role = "Zoning / Setup",
                Style = "Agile spellcaster who controls space with fast casts, tricky mobility and precise magic.", Weapon = "Ring Focus & Casting Rod",
                Primary = C(170, 120, 220), Accent = C(120, 220, 180), Fx = C(200, 140, 255),
                Power = 3, Speed = 3, Range = 4, Endurance = 2, Recovery = 3,
            },
            new CharacterInfo
            {
                Id = "Taro", Name = "Taro", Class = FighterClass.Polearm, Role = "Reach Specialist",
                Style = "Disciplined, mobile control with long-reach pressure and precise strikes.", Weapon = "Jadeleaf Glaive",
                Primary = C(30, 120, 80), Accent = C(240, 230, 200), Fx = C(80, 230, 150),
                Power = 3, Speed = 4, Range = 5, Endurance = 3, Recovery = 4,
            },
        };

        /// <summary>The character that plays <paramref name="c"/>.</summary>
        public static CharacterInfo Of(FighterClass c) => All[(int)c];

        /// <summary>Find by <see cref="CharacterInfo.Id"/> (case-insensitive); null if unknown.</summary>
        public static CharacterInfo Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var ch in All) if (string.Equals(ch.Id, id, System.StringComparison.OrdinalIgnoreCase)) return ch;
            return null;
        }
    }
}
