using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace WeaponBrawl
{
    /// <summary>
    /// Stand-in illustration drawn from polygons with a bold ink outline and two-tone cell shading: a lunging fighter
    /// holding their weapon, or the weapon alone as an icon. Replace with real art via Resources/Portraits (see README).
    /// </summary>
    public class ComicFigure : MaskableGraphic
    {
        struct Poly
        {
            public Vector2[] points;
            public Color fill;
            public bool outline;
        }

        const float DefaultInk = 1.5f;

        static readonly Color Skin = new Color(1f, 0.84f, 0.7f);
        static readonly Color SkinShade = new Color(0.9f, 0.66f, 0.56f);
        static readonly Color Steel = new Color(0.88f, 0.91f, 0.96f);
        static readonly Color SteelDark = new Color(0.55f, 0.59f, 0.68f);
        static readonly Color Wood = new Color(0.55f, 0.35f, 0.2f);

        readonly List<Poly> polys = new List<Poly>();
        float ink = DefaultInk;

        public void ShowFighter(WeaponType type, int variant, Color playerColor, Color classColor)
        {
            polys.Clear();
            ink = DefaultInk;
            BuildFighter(type, variant, playerColor, classColor);
            SetVerticesDirty();
        }

        public void ShowWeapon(WeaponType type, int variant, Color classColor)
        {
            polys.Clear();
            ink = 2.2f;
            AddWeapon(type, variant, classColor, Vector2.zero, type == WeaponType.Ranged ? 0f : 45f);
            SetVerticesDirty();
        }

        public void Clear()
        {
            polys.Clear();
            SetVerticesDirty();
        }

        // ---------------------------------------------------------------- drawing

        void BuildFighter(WeaponType type, int variant, Color player, Color classColor)
        {
            Color suit = Color.Lerp(player, UITheme.Navy, 0.55f);
            Color suitShade = Color.Lerp(player, UITheme.Navy, 0.8f);

            // Scarf whipping behind: motion reads before anything else.
            Add(player, V(44, 86), V(50, 82), V(24, 90), V(4, 108), V(12, 92), V(-8, 94), V(18, 80));

            // Back limbs
            Limb(V(44, 80), V(30, 68), 6f, 5f, suitShade, suitShade);
            Limb(V(30, 68), V(20, 78), 5f, 4f, SkinShade, SkinShade);
            Limb(V(46, 52), V(28, 34), 8f, 6f, suitShade, suitShade);
            Limb(V(28, 34), V(12, 8), 6f, 4f, suitShade, suitShade);
            Add(UITheme.Navy, V(4, 2), V(20, 2), V(16, 12), V(8, 12));

            // Front leg in a deep lunge
            Limb(V(54, 52), V(72, 38), 9f, 7f, suit, suitShade);
            Limb(V(72, 38), V(74, 8), 7f, 5f, suit, suitShade);
            Add(UITheme.Navy, V(68, 2), V(92, 2), V(84, 12), V(68, 12));

            // Torso
            Add(suit, V(41, 50), V(61, 50), V(66, 82), V(38, 86));
            AddPlain(suitShade, V(41, 50), V(50, 50), V(47, 85), V(38, 86));
            Add(classColor, V(40, 53), V(62, 53), V(62, 59), V(40, 59)); // belt in the weapon-class color

            AddWeapon(type, variant, classColor, V(86, 80), WeaponAngle(type));

            // Front arm reaching for the weapon
            Limb(V(62, 80), V(74, 74), 6f, 5f, suit, suitShade);
            Limb(V(74, 74), V(86, 80), 5f, 4f, Skin, SkinShade);
            AddCircle(Skin, V(86, 80), 4.5f);

            // Head and spiked hair
            AddCircle(Skin, V(56, 98), 10.5f);
            AddPlain(SkinShade, V(46, 96), V(52, 90), V(58, 88), V(50, 100));
            Add(classColor, V(44, 100), V(36, 112), V(46, 108), V(46, 120), V(54, 111), V(60, 122), V(62, 110), V(72, 112), V(66, 101), V(56, 104));
            AddPlain(UITheme.Navy, V(59, 97), V(65, 99), V(65, 96), V(59, 95)); // angled eye
            Add(player, V(45, 101), V(67, 101), V(67, 105), V(45, 105)); // headband
        }

        static float WeaponAngle(WeaponType type)
        {
            switch (type)
            {
                case WeaponType.Melee: return 50f;
                case WeaponType.Heavy: return 68f;
                case WeaponType.Ranged: return 0f;
                case WeaponType.Magic: return 78f;
                default: return 18f;
            }
        }

        /// <summary>Draws the looks other than each type's default. Returns false when the default should be drawn.</summary>
        bool AddVariantWeapon(WeaponType type, int variant, Color color, Vector2 hand, float angle)
        {
            switch (type)
            {
                case WeaponType.Melee when variant == 1: // rapier
                    W(hand, angle, UITheme.Navy, V(-9, -1.8f), V(2, -1.8f), V(2, 1.8f), V(-9, 1.8f));
                    W(hand, angle, Steel, V(5, -1.6f), V(72, -1.6f), V(80, 0), V(72, 1.6f), V(5, 1.6f));
                    AddCircle(color, Transform(hand, angle, V(4, 0)), 6.5f);
                    return true;
                case WeaponType.Heavy when variant == 1: // mace
                    W(hand, angle, Wood, V(-16, -2), V(38, -2), V(38, 2), V(-16, 2));
                    W(hand, angle, Steel, V(44, -12), V(50, -24), V(56, -12));
                    W(hand, angle, Steel, V(44, 12), V(50, 24), V(56, 12));
                    W(hand, angle, Steel, V(60, -6), V(72, 0), V(60, 6));
                    AddCircle(SteelDark, Transform(hand, angle, V(50, 0)), 14f);
                    AddCircle(color, Transform(hand, angle, V(50, 0)), 5.5f);
                    return true;
                case WeaponType.Heavy when variant == 2: // claymore
                    W(hand, angle, UITheme.Navy, V(-14, -2.2f), V(2, -2.2f), V(2, 2.2f), V(-14, 2.2f));
                    W(hand, angle, Steel, V(7, -6.5f), V(70, -6.5f), V(84, 0), V(70, 6.5f), V(7, 6.5f));
                    W(hand, angle, SteelDark, false, V(7, -6.5f), V(70, -6.5f), V(84, 0), V(7, 0));
                    W(hand, angle, color, V(2, -15), V(7, -15), V(7, 15), V(2, 15));
                    return true;
                case WeaponType.Ranged when variant == 1: // crossbow
                    W(hand, angle, Steel, false, V(13, -26), V(14.2f, -26), V(14.2f, 26), V(13, 26));
                    W(hand, angle, SteelDark, V(22, -2), V(28, -2), V(18, -28), V(12, -28));
                    W(hand, angle, SteelDark, V(22, 2), V(28, 2), V(18, 28), V(12, 28));
                    W(hand, angle, Wood, V(-18, -3.5f), V(34, -3.5f), V(34, 3.5f), V(-18, 3.5f));
                    W(hand, angle, Steel, V(4, -1), V(38, -1), V(38, 1), V(4, 1));
                    W(hand, angle, color, V(38, -3.5f), V(46, 0), V(38, 3.5f));
                    return true;
                case WeaponType.Ranged when variant == 2: // slingshot
                    W(hand, angle, Wood, V(-14, -2.5f), V(6, -2.5f), V(6, 2.5f), V(-14, 2.5f));
                    W(hand, angle, Wood, V(5, -2.5f), V(9, 0), V(27, -14), V(23, -18));
                    W(hand, angle, Wood, V(5, 2.5f), V(9, 0), V(27, 14), V(23, 18));
                    W(hand, angle, color, false, V(24.5f, -16), V(25.5f, -16), V(25.5f, 16), V(24.5f, 16));
                    AddCircle(SteelDark, Transform(hand, angle, V(25, 0)), 3.5f);
                    return true;
                case WeaponType.Polearm when variant == 1: // lance
                    W(hand, angle, Wood, V(-30, -2.2f), V(16, -2.2f), V(16, 2.2f), V(-30, 2.2f));
                    W(hand, angle, Steel, V(20, -6), V(100, 0), V(20, 6));
                    W(hand, angle, SteelDark, false, V(20, -6), V(100, 0), V(20, 0));
                    W(hand, angle, color, V(14, -11), V(20, -11), V(20, 11), V(14, 11));
                    return true;
                case WeaponType.Polearm when variant == 2: // halberd
                    W(hand, angle, Wood, V(-34, -1.8f), V(70, -1.8f), V(70, 1.8f), V(-34, 1.8f));
                    W(hand, angle, Steel, V(46, 2), V(66, 2), V(72, 26), V(42, 24));
                    W(hand, angle, SteelDark, V(50, -2), V(62, -2), V(56, -13));
                    W(hand, angle, Steel, V(68, -3.5f), V(96, 0), V(68, 3.5f));
                    W(hand, angle, color, V(44, -3), V(50, -3), V(50, 3), V(44, 3));
                    return true;
                default:
                    return false;
            }
        }

        void AddWeapon(WeaponType type, int variant, Color color, Vector2 hand, float angle)
        {
            if (AddVariantWeapon(type, variant, color, hand, angle)) return;
            if (type == WeaponType.Magic) color = WeaponVisuals.ElementColor(variant);

            switch (type)
            {
                case WeaponType.Melee:
                    W(hand, angle, UITheme.Navy, V(-9, -2), V(2, -2), V(2, 2), V(-9, 2));
                    W(hand, angle, Steel, V(5, -3.5f), V(58, -3.5f), V(68, 0), V(58, 3.5f), V(5, 3.5f));
                    W(hand, angle, SteelDark, false, V(5, -3.5f), V(58, -3.5f), V(68, 0), V(5, 0));
                    W(hand, angle, color, V(2, -9), V(6, -9), V(6, 9), V(2, 9));
                    break;
                case WeaponType.Heavy:
                    W(hand, angle, Wood, V(-16, -2.2f), V(44, -2.2f), V(44, 2.2f), V(-16, 2.2f));
                    W(hand, angle, SteelDark, V(36, -17), V(60, -20), V(60, 20), V(36, 17));
                    W(hand, angle, UITheme.Navy, false, V(36, -17), V(60, -20), V(60, 0), V(36, 0));
                    W(hand, angle, color, V(44, -19), V(50, -19.5f), V(50, 19.5f), V(44, 19));
                    break;
                case WeaponType.Ranged:
                    W(hand, angle, Steel, false, V(-10.6f, -34), V(-9.4f, -34), V(-9.4f, 34), V(-10.6f, 34));
                    W(hand, angle, Wood, V(0, -5), V(4, -5), V(-8, -36), V(-12, -36));
                    W(hand, angle, Wood, V(0, 5), V(4, 5), V(-8, 36), V(-12, 36));
                    W(hand, angle, UITheme.Navy, V(-2, -6), V(5, -6), V(5, 6), V(-2, 6));
                    W(hand, angle, Steel, V(-10, -1), V(36, -1), V(36, 1), V(-10, 1));
                    W(hand, angle, color, V(36, -4), V(46, 0), V(36, 4));
                    break;
                case WeaponType.Magic:
                    W(hand, angle, Wood, V(-28, -2), V(46, -2), V(46, 2), V(-28, 2));
                    W(hand, angle, color, V(42, -3), V(58, -10), V(50, 0), V(58, 10), V(42, 3));
                    AddCircle(color, Transform(hand, angle, V(56, 0)), 8f);
                    AddCircle(Color.white, Transform(hand, angle, V(56, 0)), 3.5f);
                    break;
                default:
                    W(hand, angle, Wood, V(-34, -1.8f), V(64, -1.8f), V(64, 1.8f), V(-34, 1.8f));
                    W(hand, angle, Steel, V(62, 0), V(70, -6), V(92, 0), V(70, 6));
                    W(hand, angle, SteelDark, false, V(62, 0), V(70, -6), V(92, 0));
                    W(hand, angle, color, V(60, -3), V(65, -3), V(65, 3), V(60, 3));
                    break;
            }
        }

        static Vector2 V(float x, float y) => new Vector2(x, y);

        static Vector2 Transform(Vector2 origin, float degrees, Vector2 p)
        {
            float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return origin + new Vector2(p.x * c - p.y * s, p.x * s + p.y * c);
        }

        void W(Vector2 hand, float angle, Color color, params Vector2[] pts) => W(hand, angle, color, true, pts);

        void W(Vector2 hand, float angle, Color color, bool outline, params Vector2[] pts)
        {
            var moved = new Vector2[pts.Length];
            for (int i = 0; i < pts.Length; i++) moved[i] = Transform(hand, angle, pts[i]);
            polys.Add(new Poly { points = moved, fill = color, outline = outline });
        }

        void Add(Color color, params Vector2[] pts) => polys.Add(new Poly { points = pts, fill = color, outline = true });
        void AddPlain(Color color, params Vector2[] pts) => polys.Add(new Poly { points = pts, fill = color, outline = false });

        void AddCircle(Color color, Vector2 center, float radius)
        {
            var pts = new Vector2[20];
            for (int i = 0; i < pts.Length; i++)
            {
                float a = i * Mathf.PI * 2f / pts.Length;
                pts[i] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            }
            Add(color, pts);
        }

        void Limb(Vector2 a, Vector2 b, float widthA, float widthB, Color color, Color shade)
        {
            var n = Vector2.Perpendicular((b - a).normalized);
            Add(color, a + n * widthA, a - n * widthA, b - n * widthB, b + n * widthB);
            if (shade != color) AddPlain(shade, a - n * widthA, b - n * widthB, b, a);
        }

        // ---------------------------------------------------------------- mesh

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (polys.Count == 0) return;

            Vector2 min = polys[0].points[0], max = min;
            foreach (var poly in polys)
            {
                foreach (var p in poly.points)
                {
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
            }

            var r = GetPixelAdjustedRect();
            var size = max - min + Vector2.one * ink * 2f;
            float scale = Mathf.Min(r.width / size.x, r.height / size.y);
            Vector2 center = (min + max) * 0.5f;

            foreach (var poly in polys)
            {
                if (poly.outline) Fan(vh, Offset(poly.points, ink), UITheme.Navy, r.center, center, scale);
                Fan(vh, poly.points, poly.fill, r.center, center, scale);
            }
        }

        void Fan(VertexHelper vh, Vector2[] pts, Color tintColor, Vector2 rectCenter, Vector2 center, float scale)
        {
            Color c = tintColor * color;
            int start = vh.currentVertCount;
            Vector2 mid = Vector2.zero;
            foreach (var p in pts) mid += p;
            mid /= pts.Length;

            vh.AddVert(rectCenter + (mid - center) * scale, c, Vector2.zero);
            foreach (var p in pts) vh.AddVert(rectCenter + (p - center) * scale, c, Vector2.zero);
            for (int i = 0; i < pts.Length; i++)
                vh.AddTriangle(start, start + 1 + i, start + 1 + (i + 1) % pts.Length);
        }

        /// <summary>Pushes every vertex outward along its corner normal, which gives an even ink line around the shape.</summary>
        static Vector2[] Offset(Vector2[] pts, float distance)
        {
            float area = 0f;
            for (int i = 0; i < pts.Length; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Length];
                area += a.x * b.y - b.x * a.y;
            }
            float winding = area >= 0f ? 1f : -1f;

            var result = new Vector2[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                var prev = pts[(i + pts.Length - 1) % pts.Length];
                var cur = pts[i];
                var next = pts[(i + 1) % pts.Length];
                Vector2 n1 = Outward(cur - prev, winding), n2 = Outward(next - cur, winding);
                var n = (n1 + n2).normalized;
                float miter = Mathf.Min(2.2f, 1f / Mathf.Max(0.3f, Vector2.Dot(n, n1)));
                result[i] = cur + n * distance * miter;
            }
            return result;
        }

        static Vector2 Outward(Vector2 edge, float winding)
        {
            var e = edge.normalized;
            return new Vector2(e.y, -e.x) * winding;
        }
    }
}
