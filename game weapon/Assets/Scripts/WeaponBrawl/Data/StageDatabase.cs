using System.Collections.Generic;
using UnityEngine;

namespace WeaponBrawl
{
    public class StageDefinition
    {
        public string name;
        public Color sky, ground, edge, pillar, cap;
    }

    public static class StageDatabase
    {
        static readonly StageDefinition[] stages =
        {
            new StageDefinition
            {
                name = "NEON ALLEY",
                sky = new Color(0.07f, 0.06f, 0.16f),
                ground = new Color(0.13f, 0.12f, 0.25f),
                edge = new Color(1f, 0.2f, 0.6f),
                pillar = new Color(0.14f, 0.11f, 0.3f),
                cap = new Color(0f, 0.9f, 1f),
            },
            new StageDefinition
            {
                name = "SKY DOCKS",
                sky = new Color(0.36f, 0.6f, 0.82f),
                ground = new Color(0.25f, 0.3f, 0.42f),
                edge = new Color(0.96f, 0.95f, 0.91f),
                pillar = new Color(0.52f, 0.7f, 0.87f),
                cap = new Color(0.9f, 0.94f, 0.98f),
            },
            new StageDefinition
            {
                name = "TEMPLE RUINS",
                sky = new Color(0.46f, 0.33f, 0.26f),
                ground = new Color(0.3f, 0.22f, 0.18f),
                edge = new Color(0.85f, 0.65f, 0.35f),
                pillar = new Color(0.37f, 0.26f, 0.2f),
                cap = new Color(0.6f, 0.45f, 0.3f),
            },
        };

        public static IReadOnlyList<StageDefinition> All => stages;
    }

    /// <summary>Everything the character select screen hands to the game.</summary>
    public class MatchConfig
    {
        public WeaponType p1Type, p2Type;
        public int p1Variant, p2Variant;
        public int p1Passive, p2Passive;
        public int stage;
        public bool p2Human, opponentAI;
        public float matchSeconds;
    }
}
