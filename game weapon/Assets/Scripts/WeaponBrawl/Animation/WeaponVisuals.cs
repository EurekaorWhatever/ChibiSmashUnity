using UnityEngine;
using static WeaponBrawl.WeaponAnimator;

namespace WeaponBrawl
{
    /// <summary>Builds placeholder weapon models out of sprites. Every weapon points along the pivot's +X.</summary>
    public static class WeaponVisuals
    {
        public struct Parts
        {
            public Transform root, tip, accent;
        }

        static readonly Color Steel = new Color(0.82f, 0.86f, 0.92f);
        static readonly Color DarkSteel = new Color(0.38f, 0.4f, 0.46f);
        static readonly Color Wood = new Color(0.55f, 0.36f, 0.2f);
        static readonly Color Grip = new Color(0.25f, 0.18f, 0.12f);
        public static readonly Color ArrowColor = new Color(0.95f, 0.9f, 0.75f);

        const int Order = 6;

        public static WeaponAnimator.Pose RestPose(WeaponType type)
        {
            switch (type)
            {
                case WeaponType.Melee: return P(-40f, 0.05f, -0.05f);
                case WeaponType.Heavy: return P(115f, -0.15f, 0.05f);
                case WeaponType.Ranged: return P(-12f, 0.1f, -0.05f);
                case WeaponType.Magic: return P(80f, 0.1f, -0.1f);
                default: return P(25f, 0f, -0.1f);
            }
        }

        public static AccentMode AccentModeFor(WeaponType type) =>
            type == WeaponType.Ranged ? AccentMode.PullBack :
            type == WeaponType.Magic ? AccentMode.Glow : AccentMode.None;

        /// <summary>Staff orb and bolt color for the Magic variants: fire, water, earth.</summary>
        public static Color ElementColor(int variant)
        {
            switch (variant)
            {
                case 1: return new Color(0.25f, 0.65f, 1f);
                case 2: return new Color(0.55f, 0.75f, 0.3f);
                default: return new Color(1f, 0.5f, 0.15f);
            }
        }

        public static Parts Build(WeaponType type, int variant, Transform pivot, Color color)
        {
            var root = new GameObject("Weapon").transform;
            root.SetParent(pivot, false);
            var parts = new Parts { root = root };

            switch (type)
            {
                case WeaponType.Melee when variant == 1: // rapier
                    Part(root, "Grip", Grip, 0.08f, 0f, 0.24f, 0.07f);
                    SpriteFactory.Part(root, "CupGuard", SpriteFactory.Circle, color, new Vector2(0.22f, 0f), Vector2.one * 0.28f, Order + 1);
                    Part(root, "Blade", Steel, 0.78f, 0f, 1.05f, 0.05f);
                    Part(root, "Point", Steel, 1.3f, 0f, 0.05f, 0.05f, 0, 45f);
                    parts.tip = Anchor(root, "Tip", 1.32f, 0f);
                    break;

                case WeaponType.Melee: // sword
                    Part(root, "Grip", Grip, 0.08f, 0f, 0.24f, 0.08f);
                    Part(root, "Guard", color, 0.22f, 0f, 0.06f, 0.32f, 1);
                    Part(root, "Blade", Steel, 0.72f, 0f, 0.95f, 0.11f);
                    Part(root, "Edge", Color.white, 0.72f, 0.035f, 0.95f, 0.03f, 1);
                    Part(root, "Point", Steel, 1.19f, 0f, 0.11f, 0.11f, 0, 45f);
                    parts.tip = Anchor(root, "Tip", 1.22f, 0f);
                    break;

                case WeaponType.Heavy when variant == 1: // mace
                    Part(root, "Handle", Wood, 0.5f, 0f, 1.1f, 0.09f);
                    SpriteFactory.Part(root, "Head", SpriteFactory.Circle, DarkSteel, new Vector2(1.12f, 0f), Vector2.one * 0.5f, Order + 1);
                    Part(root, "SpikeFront", Steel, 1.38f, 0f, 0.13f, 0.13f, 0, 45f);
                    Part(root, "SpikeUp", Steel, 1.12f, 0.26f, 0.13f, 0.13f, 0, 45f);
                    Part(root, "SpikeDown", Steel, 1.12f, -0.26f, 0.13f, 0.13f, 0, 45f);
                    SpriteFactory.Part(root, "Stud", SpriteFactory.Circle, color, new Vector2(1.12f, 0f), Vector2.one * 0.2f, Order + 2);
                    parts.tip = Anchor(root, "Tip", 1.25f, 0f);
                    break;

                case WeaponType.Heavy when variant == 2: // claymore
                    Part(root, "Grip", Grip, 0f, 0f, 0.3f, 0.09f);
                    Part(root, "Guard", color, 0.18f, 0f, 0.1f, 0.52f, 1);
                    Part(root, "Blade", Steel, 0.72f, 0f, 1f, 0.2f);
                    Part(root, "Edge", Color.white, 0.72f, 0.06f, 1f, 0.04f, 1);
                    Part(root, "Point", Steel, 1.22f, 0f, 0.15f, 0.15f, 0, 45f);
                    parts.tip = Anchor(root, "Tip", 1.25f, 0f);
                    break;

                case WeaponType.Heavy: // hammer
                    Part(root, "Pommel", DarkSteel, -0.05f, 0f, 0.12f, 0.14f);
                    Part(root, "Handle", Wood, 0.55f, 0f, 1.2f, 0.1f);
                    Part(root, "Head", DarkSteel, 1.18f, 0f, 0.38f, 0.62f, 1);
                    Part(root, "Band", color, 1.18f, 0f, 0.4f, 0.14f, 2);
                    parts.tip = Anchor(root, "Tip", 1.2f, 0f);
                    break;

                case WeaponType.Ranged when variant == 1: // crossbow
                    Part(root, "Stock", Wood, 0.22f, 0f, 0.72f, 0.11f);
                    Part(root, "UpperLimb", DarkSteel, 0.48f, 0.2f, 0.07f, 0.42f, 1, 18f);
                    Part(root, "LowerLimb", DarkSteel, 0.48f, -0.2f, 0.07f, 0.42f, 1, -18f);
                    Part(root, "String", new Color(1f, 1f, 1f, 0.85f), 0.42f, 0f, 0.02f, 0.76f, 1);
                    parts.accent = Anchor(root, "NockedBolt", 0.3f, 0f);
                    Part(parts.accent, "Shaft", ArrowColor, 0.2f, 0f, 0.45f, 0.05f, 2);
                    Part(parts.accent, "Head", Steel, 0.44f, 0f, 0.09f, 0.09f, 2, 45f);
                    parts.tip = Anchor(root, "Tip", 0.62f, 0f);
                    break;

                case WeaponType.Ranged when variant == 2: // slingshot
                    Part(root, "Handle", Wood, 0.12f, 0f, 0.34f, 0.08f);
                    Part(root, "UpperFork", Wood, 0.4f, 0.1f, 0.3f, 0.06f, 0, 35f);
                    Part(root, "LowerFork", Wood, 0.4f, -0.1f, 0.3f, 0.06f, 0, -35f);
                    Part(root, "Band", color, 0.5f, 0f, 0.025f, 0.36f, 1);
                    parts.accent = Anchor(root, "Pouch", 0.46f, 0f);
                    SpriteFactory.Part(parts.accent, "Stone", SpriteFactory.Circle, DarkSteel, Vector2.zero, Vector2.one * 0.16f, Order + 2);
                    parts.tip = Anchor(root, "Tip", 0.55f, 0f);
                    break;

                case WeaponType.Ranged: // bow
                    Part(root, "Grip", Grip, 0.32f, 0f, 0.1f, 0.26f, 1);
                    Part(root, "UpperLimb", Wood, 0.26f, 0.36f, 0.08f, 0.52f, 0, 18f);
                    Part(root, "LowerLimb", Wood, 0.26f, -0.36f, 0.08f, 0.52f, 0, -18f);
                    Part(root, "UpperCap", color, 0.18f, 0.6f, 0.09f, 0.09f, 1);
                    Part(root, "LowerCap", color, 0.18f, -0.6f, 0.09f, 0.09f, 1);
                    Part(root, "String", new Color(1f, 1f, 1f, 0.85f), 0.18f, 0f, 0.02f, 1.21f);
                    parts.accent = Anchor(root, "NockedArrow", 0.18f, 0f);
                    Part(parts.accent, "Shaft", ArrowColor, 0.35f, 0f, 0.75f, 0.045f, 2);
                    Part(parts.accent, "Head", Steel, 0.74f, 0f, 0.1f, 0.1f, 2, 45f);
                    parts.tip = Anchor(root, "Tip", 0.6f, 0f);
                    break;

                case WeaponType.Magic: // staff
                    var element = ElementColor(variant);
                    Part(root, "Shaft", Wood, 0.55f, 0f, 1.3f, 0.08f);
                    Part(root, "ProngUp", element, 1.2f, 0.1f, 0.22f, 0.05f, 1, 35f);
                    Part(root, "ProngDown", element, 1.2f, -0.1f, 0.22f, 0.05f, 1, -35f);
                    var orb = SpriteFactory.Part(root, "Orb", SpriteFactory.Circle, Color.Lerp(element, Color.white, 0.3f), new Vector2(1.32f, 0f), Vector2.one * 0.32f, Order + 2);
                    SpriteFactory.Part(orb.transform, "Core", SpriteFactory.Circle, Color.white, Vector2.zero, Vector2.one * 0.45f, Order + 3);
                    parts.accent = orb.transform;
                    parts.tip = Anchor(root, "Tip", 1.32f, 0f);
                    break;

                default: // spear, lance or halberd
                    BuildSpear(root, color, Order, variant);
                    parts.tip = Anchor(root, "Tip", 1.85f, 0f);
                    break;
            }
            return parts;
        }

        /// <summary>Polearm model; also used for the thrown weapon and the impaled weapon of the Polearm ultimate.</summary>
        public static void BuildSpear(Transform parent, Color color, int order, int variant)
        {
            SpriteFactory.Part(parent, "Butt", SpriteFactory.Square, DarkSteel, new Vector2(-0.45f, 0f), new Vector2(0.08f, 0.09f), order);
            if (variant == 1) // lance
            {
                SpriteFactory.Part(parent, "Grip", SpriteFactory.Square, Wood, new Vector2(-0.1f, 0f), new Vector2(0.65f, 0.08f), order);
                SpriteFactory.Part(parent, "Vamplate", SpriteFactory.Circle, color, new Vector2(0.25f, 0f), Vector2.one * 0.34f, order + 1);
                SpriteFactory.Part(parent, "Shaft", SpriteFactory.Square, Steel, new Vector2(0.75f, 0f), new Vector2(1f, 0.15f), order);
                SpriteFactory.Part(parent, "Taper", SpriteFactory.Square, Steel, new Vector2(1.45f, 0f), new Vector2(0.45f, 0.08f), order);
                SpriteFactory.Part(parent, "Point", SpriteFactory.Square, Steel, new Vector2(1.7f, 0f), new Vector2(0.09f, 0.09f), order, 45f);
                return;
            }
            if (variant == 2) // halberd
            {
                SpriteFactory.Part(parent, "Shaft", SpriteFactory.Square, Wood, new Vector2(0.5f, 0f), new Vector2(1.9f, 0.07f), order);
                SpriteFactory.Part(parent, "Socket", SpriteFactory.Square, color, new Vector2(1.34f, 0f), new Vector2(0.34f, 0.11f), order + 1);
                SpriteFactory.Part(parent, "AxeBlade", SpriteFactory.Square, Steel, new Vector2(1.34f, 0.22f), new Vector2(0.32f, 0.4f), order);
                SpriteFactory.Part(parent, "BackSpike", SpriteFactory.Square, Steel, new Vector2(1.34f, -0.13f), new Vector2(0.12f, 0.12f), order, 45f);
                SpriteFactory.Part(parent, "TopSpike", SpriteFactory.Square, Steel, new Vector2(1.66f, 0f), new Vector2(0.38f, 0.07f), order);
                return;
            }
            SpriteFactory.Part(parent, "Shaft", SpriteFactory.Square, Wood, new Vector2(0.5f, 0f), new Vector2(1.9f, 0.07f), order);
            SpriteFactory.Part(parent, "Socket", SpriteFactory.Square, color, new Vector2(1.47f, 0f), new Vector2(0.1f, 0.11f), order + 1);
            SpriteFactory.Part(parent, "HeadBase", SpriteFactory.Square, Steel, new Vector2(1.6f, 0f), new Vector2(0.2f, 0.2f), order, 45f);
            SpriteFactory.Part(parent, "HeadBlade", SpriteFactory.Square, Steel, new Vector2(1.72f, 0f), new Vector2(0.3f, 0.09f), order);
        }

        static void Part(Transform parent, string name, Color color, float x, float y, float w, float h, int orderOffset = 0, float rotation = 0f) =>
            SpriteFactory.Part(parent, name, SpriteFactory.Square, color, new Vector2(x, y), new Vector2(w, h), Order + orderOffset, rotation);

        static Transform Anchor(Transform parent, string name, float x, float y)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = new Vector2(x, y);
            return t;
        }
    }
}
