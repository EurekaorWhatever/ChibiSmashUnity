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

        public static Parts Build(WeaponType type, Transform pivot, Color color)
        {
            var root = new GameObject("Weapon").transform;
            root.SetParent(pivot, false);
            var parts = new Parts { root = root };

            switch (type)
            {
                case WeaponType.Melee: // sword
                    Part(root, "Grip", Grip, 0.08f, 0f, 0.24f, 0.08f);
                    Part(root, "Guard", color, 0.22f, 0f, 0.06f, 0.32f, 1);
                    Part(root, "Blade", Steel, 0.72f, 0f, 0.95f, 0.11f);
                    Part(root, "Edge", Color.white, 0.72f, 0.035f, 0.95f, 0.03f, 1);
                    Part(root, "Point", Steel, 1.19f, 0f, 0.11f, 0.11f, 0, 45f);
                    parts.tip = Anchor(root, "Tip", 1.22f, 0f);
                    break;

                case WeaponType.Heavy: // hammer
                    Part(root, "Pommel", DarkSteel, -0.05f, 0f, 0.12f, 0.14f);
                    Part(root, "Handle", Wood, 0.55f, 0f, 1.2f, 0.1f);
                    Part(root, "Head", DarkSteel, 1.18f, 0f, 0.38f, 0.62f, 1);
                    Part(root, "Band", color, 1.18f, 0f, 0.4f, 0.14f, 2);
                    parts.tip = Anchor(root, "Tip", 1.2f, 0f);
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
                    Part(root, "Shaft", Wood, 0.55f, 0f, 1.3f, 0.08f);
                    Part(root, "ProngUp", color, 1.2f, 0.1f, 0.22f, 0.05f, 1, 35f);
                    Part(root, "ProngDown", color, 1.2f, -0.1f, 0.22f, 0.05f, 1, -35f);
                    var orb = SpriteFactory.Part(root, "Orb", SpriteFactory.Circle, Color.Lerp(color, Color.white, 0.3f), new Vector2(1.32f, 0f), Vector2.one * 0.32f, Order + 2);
                    SpriteFactory.Part(orb.transform, "Core", SpriteFactory.Circle, Color.white, Vector2.zero, Vector2.one * 0.45f, Order + 3);
                    parts.accent = orb.transform;
                    parts.tip = Anchor(root, "Tip", 1.32f, 0f);
                    break;

                default: // spear
                    BuildSpear(root, color, Order);
                    parts.tip = Anchor(root, "Tip", 1.85f, 0f);
                    break;
            }
            return parts;
        }

        /// <summary>Spear model; also used for the thrown spear and the impaled spear of the Polearm ultimate.</summary>
        public static void BuildSpear(Transform parent, Color color, int order)
        {
            SpriteFactory.Part(parent, "Butt", SpriteFactory.Square, DarkSteel, new Vector2(-0.45f, 0f), new Vector2(0.08f, 0.09f), order);
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
