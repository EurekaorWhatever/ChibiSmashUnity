using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Painted cel-shaded art for the three stages, adapted from the copy-ghibli-smash fork's maps (UI-1): Sky Dojo wears
    /// the "Mountain of the Beginning" look, Lava Forge the "Volcano Abyss" look, Neon City a new painted night skyline in
    /// the same style. VISUAL ONLY: the colliders and platform positions stay exactly as in Stage.cs (= ChibiStageTable),
    /// the fork's different layouts, KO rules, moving platforms and hazards are not used.
    /// Sorting: sky -100.., far layers -90..-60, mid -60..-20, stage -16..-6, fighters 9..45, foreground 50+.
    /// </summary>
    public static partial class StageBuilder
    {
        static readonly Color Ink = StageArt.InkColor;

        static float Hash(int n)
        {
            n = (n << 13) ^ n;
            return ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 2147483647f;
        }

        static Transform Group(Transform parent, string name, Vector2 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            return go.transform;
        }

        /// <summary>Floating rock under a one-way platform: inked slab with a downward point, coloured top, roots, rune.</summary>
        static void RockArt(Transform t, float width, Color rock, Color top, Color glow, bool roots, bool rune, float depth = 1.5f)
        {
            StageArt.Inked(t, "Rock", StageArt.Triangle, rock, new Vector2(0f, -0.2f - depth * 0.5f), new Vector2(width * 0.95f, depth), -13, 180f);
            StageArt.Shape(t, "RockShade", StageArt.Triangle, Color.Lerp(rock, Color.black, 0.3f), new Vector2(width * 0.14f, -0.3f - depth * 0.55f), new Vector2(width * 0.42f, depth * 0.75f), -11, 180f);
            StageArt.Inked(t, "Slab", Art.Rounded, Color.Lerp(rock, Color.white, 0.12f), new Vector2(0f, -0.08f), new Vector2(width, 0.42f), -9);
            StageArt.Shape(t, "Top", Art.Rounded, top, new Vector2(0f, 0.1f), new Vector2(width + 0.06f, 0.16f), -8);
            if (roots)
                for (int i = 0; i < 4; i++)
                {
                    float x = (i - 1.5f) * width * 0.22f;
                    float len = 0.6f + 0.35f * ((i * 7) % 3);
                    var r = StageArt.Shape(t, "Root", Art.Square, new Color(0.2f, 0.32f, 0.18f), new Vector2(x, -0.35f - len * 0.5f), new Vector2(0.07f, len), -12);
                    StageArt.Sway(r, 6f, 1.3f, i);
                }
            if (rune)
            {
                StageArt.Pulse(StageArt.Shape(t, "RuneGlow", Art.Soft, new Color(glow.r, glow.g, glow.b, 0.7f), new Vector2(0f, -0.65f), new Vector2(1.5f, 1.1f), -10), 0.6f, 2.2f);
                StageArt.Pulse(StageArt.Shape(t, "Rune", Art.Ring, glow, new Vector2(0f, -0.65f), new Vector2(0.42f, 0.42f), -10), 0.5f, 2.2f, 0.5f);
            }
        }

        // ---------------------------------------------------------------- 0. Sky Dojo (mountain shrine above the clouds)

        static void SkyDojoBackdrop(Transform root, StageInfo s)
        {
            var sky = StageArt.Layer(root, "Sky", 0.95f);
            StageArt.Sky(sky, new Color(0.36f, 0.62f, 0.95f), new Color(1f, 0.86f, 0.58f), -2f);
            StageArt.Shape(sky, "SunGlow", Art.Soft, new Color(1f, 0.9f, 0.55f, 0.9f), new Vector2(7f, 6f), new Vector2(16f, 16f), -97);
            StageArt.Shape(sky, "Sun", Art.Circle, new Color(1f, 0.97f, 0.82f), new Vector2(7f, 6f), new Vector2(2.6f, 2.6f), -96);
            for (int i = 0; i < 4; i++)
                StageArt.Drift(StageArt.Cloud(sky, new Vector2(-24f + i * 14f, 7f + (i % 2) * 3f), 1.4f + (i % 3) * 0.4f, Color.white, new Color(0.82f, 0.86f, 0.95f), -94, false), new Vector2(0.35f, 0f), -32f, 32f);

            // far karst peaks with pines and a pagoda
            var far = StageArt.Layer(root, "FarPeaks", 0.8f);
            float[] fx = { -24f, -17f, -11f, -4f, 3f, 10f, 17f, 24f };
            float[] fh = { 14f, 19f, 11f, 16f, 12f, 20f, 13f, 16f };
            var farC = new Color(0.56f, 0.75f, 0.8f);
            for (int i = 0; i < fx.Length; i++)
            {
                float w = 3.2f + (i % 3) * 0.9f;
                StageArt.Inked(far, "Karst", Art.Rounded, Color.Lerp(farC, Color.white, (i % 2) * 0.08f), new Vector2(fx[i], -7f + fh[i] * 0.5f), new Vector2(w, fh[i]), -90, 0f, 0.05f);
                StageArt.Shape(far, "KarstShade", Art.Rounded, Color.Lerp(farC, Color.black, 0.12f), new Vector2(fx[i] + w * 0.22f, -7f + fh[i] * 0.45f), new Vector2(w * 0.45f, fh[i] * 0.9f), -89);
                if (i != 5) StageArt.Pine(far, new Vector2(fx[i] - w * 0.15f, -7f + fh[i] - 0.35f), 1.3f, new Color(0.3f, 0.52f, 0.46f), -88);
            }
            var pag = Group(far, "Pagoda", new Vector2(10f, -7f + 20f - 0.1f));
            for (int k = 0; k < 3; k++)
            {
                float w = 1.3f - k * 0.3f, y = k * 0.62f;
                StageArt.Inked(pag, "Floor", Art.Square, new Color(0.78f, 0.22f, 0.16f), new Vector2(0f, y + 0.25f), new Vector2(w * 0.6f, 0.4f), -86, 0f, 0.04f);
                StageArt.Inked(pag, "Roof", StageArt.Triangle, new Color(0.18f, 0.28f, 0.3f), new Vector2(0f, y + 0.55f), new Vector2(w, 0.32f), -84, 0f, 0.04f);
            }
            StageArt.Shape(pag, "Spire", Art.Square, new Color(1f, 0.8f, 0.35f), new Vector2(0f, 2.15f), new Vector2(0.05f, 0.5f), -83);
            StageArt.Shape(far, "Mist", StageArt.Gradient, new Color(1f, 0.97f, 0.92f, 0.9f), new Vector2(0f, -3.5f), new Vector2(110f, 9f), -80);

            // waterfall from a cliff in the middle distance
            var fall = StageArt.Layer(root, "Waterfall", 0.6f);
            StageArt.Inked(fall, "Cliff", Art.Rounded, new Color(0.34f, 0.5f, 0.44f), new Vector2(-12f, 1f), new Vector2(8f, 26f), -78, 0f, 0.1f);
            StageArt.Shape(fall, "Fall", Art.Square, new Color(0.78f, 0.93f, 1f, 0.95f), new Vector2(-6.8f, 1.5f), new Vector2(2.8f, 24f), -76);
            StageArt.Shape(fall, "FallEdgeL", Art.Square, Ink, new Vector2(-8.25f, 1.5f), new Vector2(0.08f, 24f), -75);
            StageArt.Shape(fall, "FallEdgeR", Art.Square, Ink, new Vector2(-5.35f, 1.5f), new Vector2(0.08f, 24f), -75);
            for (int i = 0; i < 10; i++)
            {
                var streak = StageArt.Shape(fall, "Streak", Art.Square, new Color(1f, 1f, 1f, 0.8f), new Vector2(-8f + Hash(i) * 2.4f, -10f + Hash(i + 50) * 23f), new Vector2(0.1f, 1.6f + Hash(i + 9) * 2f), -74);
                StageArt.Drift(streak, new Vector2(0f, -7f - Hash(i + 3) * 4f), -10.5f, 13f, true);
            }
            StageArt.Pine(fall, new Vector2(-14.5f, 13.5f), 2.2f, new Color(0.18f, 0.38f, 0.3f), -72);

            // nearer jade peaks framing the arena
            var mid = StageArt.Layer(root, "JadePeaks", 0.4f);
            StageArt.Inked(mid, "PeakL", Art.Rounded, new Color(0.28f, 0.56f, 0.46f), new Vector2(-19f, -5f), new Vector2(6f, 18f), -60, 0f, 0.12f);
            StageArt.Inked(mid, "PeakR", Art.Rounded, new Color(0.28f, 0.56f, 0.46f), new Vector2(19.5f, -6f), new Vector2(7f, 17f), -60, 0f, 0.12f);
            StageArt.Pine(mid, new Vector2(-19.5f, 3.8f), 2.6f, new Color(0.16f, 0.36f, 0.26f), -57);
            StageArt.Pine(mid, new Vector2(19f, 2.3f), 2.4f, new Color(0.16f, 0.36f, 0.26f), -57);

            // the cloud sea below the shrine (ring-out drop)
            var below = StageArt.Layer(root, "CloudSea", 0.2f);
            StageArt.Shape(below, "MistRise", StageArt.Gradient, new Color(1f, 0.98f, 0.94f, 0.95f), new Vector2(0f, s.killY + 1.5f), new Vector2(110f, 10f), -30);
            StageArt.Shape(below, "MistFloor", Art.Square, new Color(1f, 0.98f, 0.94f), new Vector2(0f, s.killY - 18f), new Vector2(110f, 30f), -30);
            for (int i = 0; i < 6; i++)
                StageArt.Drift(StageArt.Cloud(below, new Vector2(-22f + i * 9f, s.killY + 3f + (i % 2) * 0.8f), 1.2f + (i % 3) * 0.3f, Color.white, new Color(0.88f, 0.9f, 0.95f), -27, false), new Vector2(0.5f, 0f), -28f, 28f);

            for (int i = 0; i < 10; i++)
                StageArt.Bob(StageArt.Shape(root, "Mote", Art.Soft, new Color(1f, 0.9f, 0.5f, 0.75f), new Vector2(-12f + Hash(i + 70) * 24f, -1f + Hash(i + 80) * 10f), new Vector2(0.35f, 0.35f), -7), new Vector2(0.3f, 0.6f), 0.6f + Hash(i) * 0.6f, i);
        }

        /// <summary>Carved stone main stage (top surface at groundY), jade trim, gold carvings, moss and lantern posts.</summary>
        static void SkyDojoMain(Transform root, StageInfo s)
        {
            float hw = s.halfWidth, gy = s.groundY;
            var stone = new Color(0.64f, 0.61f, 0.55f);
            var jade = new Color(0.3f, 0.82f, 0.6f);
            var gold = new Color(1f, 0.8f, 0.35f);
            StageArt.Inked(root, "Underside", StageArt.Triangle, new Color(0.46f, 0.43f, 0.4f), new Vector2(0f, gy - 3.4f), new Vector2(hw * 1.9f, 3.8f), -15, 180f, 0.12f);
            StageArt.Shape(root, "UnderShade", StageArt.Triangle, new Color(0.36f, 0.34f, 0.32f), new Vector2(hw * 0.3f, gy - 3.6f), new Vector2(hw * 0.8f, 3f), -14, 180f);
            StageArt.Inked(root, "Stone", Art.Square, stone, new Vector2(0f, gy - 0.75f), new Vector2(hw * 2f, 1.5f), -11, 0f, 0.1f);
            StageArt.Shape(root, "StoneShade", Art.Square, Color.Lerp(stone, Color.black, 0.15f), new Vector2(0f, gy - 1.32f), new Vector2(hw * 2f, 0.36f), -10);
            StageArt.Inked(root, "Trim", Art.Square, jade, new Vector2(0f, gy - 0.08f), new Vector2(hw * 2f + 0.3f, 0.22f), -8, 0f, 0.06f);
            int carvings = Mathf.FloorToInt(hw / 1.15f) - 1;
            for (int k = -carvings; k <= carvings; k++)
            {
                StageArt.Shape(root, "Carving", Art.Ring, gold, new Vector2(k * 1.15f, gy - 0.78f), new Vector2(0.5f, 0.5f), -9);
                StageArt.Shape(root, "CarvingDot", Art.Square, gold, new Vector2(k * 1.15f + 0.575f, gy - 0.78f), new Vector2(0.16f, 0.16f), -9, 45f);
            }
            for (int i = 0; i < 9; i++)
            {
                float len = 0.7f + Hash(i + 30) * 1.4f;
                var moss = StageArt.Shape(root, "Moss", Art.Square, new Color(0.22f, 0.45f, 0.26f), new Vector2(-hw + 0.8f + i * (hw * 2f - 1.6f) / 8f, gy - 1.5f - len * 0.5f), new Vector2(0.09f, len), -12);
                StageArt.Sway(moss, 5f, 1.1f, i);
            }
            for (int side = -1; side <= 1; side += 2)
            {
                float x = side * (hw - 0.45f);
                StageArt.Inked(root, "Post", Art.Square, new Color(0.45f, 0.18f, 0.12f), new Vector2(x, gy + 0.55f), new Vector2(0.14f, 1.1f), -13, 0f, 0.04f);
                StageArt.Inked(root, "Lantern", Art.Rounded, new Color(0.92f, 0.25f, 0.18f), new Vector2(x, gy + 1.25f), new Vector2(0.42f, 0.52f), -12, 0f, 0.05f);
                StageArt.Pulse(StageArt.Shape(root, "LanternGlow", Art.Soft, new Color(1f, 0.75f, 0.35f, 0.6f), new Vector2(x, gy + 1.25f), new Vector2(1.6f, 1.6f), -14), 0.35f, 2.5f, side);
            }
        }

        static void SkyDojoPlatform(Transform t, float width, int i)
        {
            RockArt(t, width, new Color(0.5f, 0.47f, 0.42f), new Color(0.38f, 0.7f, 0.36f), new Color(0.3f, 0.82f, 0.6f), true, i != 2);
        }

        // ---------------------------------------------------------------- 1. Neon City (rooftops under neon rain)

        static readonly Color NeonPink = new Color(1f, 0.3f, 0.8f), NeonCyan = new Color(0.2f, 0.95f, 1f);

        static void NeonCityBackdrop(Transform root, StageInfo s)
        {
            var sky = StageArt.Layer(root, "Sky", 0.95f);
            StageArt.Sky(sky, new Color(0.05f, 0.03f, 0.13f), new Color(0.42f, 0.1f, 0.38f), -3f);
            StageArt.Shape(sky, "MoonGlow", Art.Soft, new Color(0.9f, 0.75f, 1f, 0.5f), new Vector2(-8f, 8f), new Vector2(9f, 9f), -97);
            StageArt.Inked(sky, "Moon", Art.Circle, new Color(1f, 0.95f, 0.88f), new Vector2(-8f, 8f), new Vector2(2.4f, 2.4f), -96, 0f, 0.06f);
            StageArt.Shape(sky, "MoonShade", Art.Circle, new Color(0.88f, 0.82f, 0.9f), new Vector2(-7.6f, 7.8f), new Vector2(0.6f, 0.6f), -95);

            // far skyline: flat silhouettes with a few lit windows
            var far = StageArt.Layer(root, "FarSkyline", 0.8f);
            for (int i = 0; i < 16; i++)
            {
                float x = -30f + i * 4f + Hash(i) * 1.5f, h = 6f + Hash(i + 11) * 10f, w = 2.4f + Hash(i + 21) * 1.6f;
                StageArt.Shape(far, "Tower", Art.Square, new Color(0.13f, 0.08f, 0.26f), new Vector2(x, -7f + h * 0.5f), new Vector2(w, h), -90);
                for (int k = 0; k < 3; k++)
                    if (Hash(i * 5 + k) > 0.4f)
                        StageArt.Shape(far, "Window", Art.Square, new Color(1f, 0.85f, 0.5f, 0.55f), new Vector2(x - w * 0.25f + k * w * 0.25f, -7f + h * (0.4f + 0.18f * k)), new Vector2(0.3f, 0.4f), -89);
            }
            StageArt.Shape(far, "Haze", StageArt.Gradient, new Color(0.6f, 0.2f, 0.55f, 0.6f), new Vector2(0f, -4f), new Vector2(110f, 8f), -85);

            // mid towers: inked, neon roof trims that pulse, a sign
            var mid = StageArt.Layer(root, "Towers", 0.45f);
            float[] mx = { -22f, -16f, -10.5f, 11f, 16.5f, 22f };
            float[] mh = { 14f, 9f, 12f, 11f, 15f, 10f };
            for (int i = 0; i < mx.Length; i++)
            {
                float w = 4f + (i % 2) * 1.2f;
                var neon = i % 2 == 0 ? NeonPink : NeonCyan;
                StageArt.Inked(mid, "Tower", Art.Square, new Color(0.17f, 0.12f, 0.3f), new Vector2(mx[i], -8f + mh[i] * 0.5f), new Vector2(w, mh[i]), -60, 0f, 0.1f);
                StageArt.Shape(mid, "Shade", Art.Square, new Color(0.12f, 0.08f, 0.22f), new Vector2(mx[i] + w * 0.3f, -8f + mh[i] * 0.5f), new Vector2(w * 0.35f, mh[i]), -59);
                StageArt.Pulse(StageArt.Shape(mid, "Trim", Art.Square, neon, new Vector2(mx[i], -8f + mh[i]), new Vector2(w + 0.2f, 0.16f), -58), 0.4f, 1.5f + i * 0.3f, i);
                for (int k = 0; k < 4; k++)
                    StageArt.Shape(mid, "Window", Art.Square, new Color(neon.r, neon.g, neon.b, 0.35f), new Vector2(mx[i] - w * 0.3f + (k % 2) * w * 0.35f, -8f + mh[i] * (0.35f + 0.2f * (k / 2))), new Vector2(0.6f, 0.9f), -58);
            }
            var sign = Group(mid, "Sign", new Vector2(-16f, 4.6f));
            StageArt.Inked(sign, "Board", Art.Rounded, new Color(0.1f, 0.05f, 0.18f), Vector2.zero, new Vector2(3.4f, 1.3f), -56, 0f, 0.08f);
            StageArt.Pulse(StageArt.Shape(sign, "Glow", Art.Soft, new Color(NeonPink.r, NeonPink.g, NeonPink.b, 0.6f), Vector2.zero, new Vector2(5f, 3f), -57), 0.5f, 3f);
            StageArt.Shape(sign, "Tube", Art.Rounded, NeonPink, new Vector2(0f, 0.2f), new Vector2(2.6f, 0.14f), -55);
            StageArt.Shape(sign, "Tube2", Art.Rounded, NeonCyan, new Vector2(0f, -0.2f), new Vector2(1.8f, 0.14f), -55);

            // neon rain
            for (int i = 0; i < 24; i++)
            {
                var drop = StageArt.Shape(root, "Rain", Art.Square, new Color(0.7f, 0.9f, 1f, 0.35f), new Vector2(-20f + Hash(i + 40) * 40f, -6f + Hash(i + 41) * 22f), new Vector2(0.04f, 0.9f), -20, 10f);
                StageArt.Drift(drop, new Vector2(-2f, -16f - Hash(i + 42) * 6f), s.killY, 16f, true);
            }
            // street glow far below (the ring-out drop)
            var street = StageArt.Layer(root, "Street", 0.2f);
            StageArt.Pulse(StageArt.Shape(street, "StreetGlow", Art.Soft, new Color(1f, 0.35f, 0.75f, 0.55f), new Vector2(0f, s.killY + 1f), new Vector2(60f, 8f), -30), 0.3f, 1.2f);
        }

        /// <summary>Rooftop main stage: inked concrete slab, cyan trim, vents and a water tank.</summary>
        static void NeonCityMain(Transform root, StageInfo s)
        {
            float hw = s.halfWidth, gy = s.groundY;
            var body = new Color(0.22f, 0.19f, 0.34f);
            StageArt.Inked(root, "Building", Art.Square, Color.Lerp(body, Color.black, 0.25f), new Vector2(0f, gy - 6f), new Vector2(hw * 1.7f, 10f), -15, 0f, 0.12f);
            for (int k = 0; k < 6; k++)
                StageArt.Shape(root, "Window", Art.Square, new Color(NeonCyan.r, NeonCyan.g, NeonCyan.b, 0.3f), new Vector2(-hw * 0.6f + (k % 3) * hw * 0.6f, gy - 3f - (k / 3) * 2.4f), new Vector2(1.2f, 1.2f), -14);
            StageArt.Inked(root, "Roof", Art.Square, body, new Vector2(0f, gy - 0.6f), new Vector2(hw * 2f, 1.2f), -11, 0f, 0.1f);
            StageArt.Shape(root, "RoofShade", Art.Square, Color.Lerp(body, Color.black, 0.2f), new Vector2(0f, gy - 1.05f), new Vector2(hw * 2f, 0.3f), -10);
            StageArt.Pulse(StageArt.Inked(root, "Trim", Art.Square, NeonCyan, new Vector2(0f, gy - 0.07f), new Vector2(hw * 2f + 0.2f, 0.16f), -8, 0f, 0.05f), 0.25f, 2f);
            for (int side = -1; side <= 1; side += 2)
            {
                StageArt.Inked(root, "Vent", Art.Rounded, new Color(0.35f, 0.33f, 0.45f), new Vector2(side * (hw - 1f), gy + 0.3f), new Vector2(0.8f, 0.6f), -13, 0f, 0.05f);
                StageArt.Pulse(StageArt.Shape(root, "Steam", Art.Soft, new Color(1f, 1f, 1f, 0.25f), new Vector2(side * (hw - 1f), gy + 1.2f), new Vector2(1.2f, 1.4f), -14), 0.6f, 1.4f, side);
            }
        }

        static void NeonCityPlatform(Transform t, float width, int i)
        {
            var neon = i == 2 ? NeonCyan : NeonPink;
            StageArt.Inked(t, "Slab", Art.Rounded, new Color(0.16f, 0.12f, 0.28f), new Vector2(0f, -0.08f), new Vector2(width, 0.42f), -9);
            StageArt.Shape(t, "Top", Art.Rounded, neon, new Vector2(0f, 0.1f), new Vector2(width + 0.06f, 0.14f), -8);
            StageArt.Pulse(StageArt.Shape(t, "Glow", Art.Soft, new Color(neon.r, neon.g, neon.b, 0.45f), new Vector2(0f, -0.3f), new Vector2(width * 1.1f, 1.1f), -12), 0.5f, 2f, i);
            StageArt.Shape(t, "Strut", Art.Square, Ink, new Vector2(-width * 0.3f, -0.9f), new Vector2(0.08f, 1.3f), -13);
            StageArt.Shape(t, "Strut", Art.Square, Ink, new Vector2(width * 0.3f, -0.9f), new Vector2(0.08f, 1.3f), -13);
        }

        // ---------------------------------------------------------------- 2. Lava Forge (foundry over a magma sea)

        static readonly Color Lava = new Color(1f, 0.42f, 0.06f), LavaHot = new Color(1f, 0.82f, 0.3f), Basalt = new Color(0.17f, 0.14f, 0.16f);

        static void LavaForgeBackdrop(Transform root, StageInfo s)
        {
            var sky = StageArt.Layer(root, "Sky", 0.95f);
            StageArt.Sky(sky, new Color(0.07f, 0.02f, 0.03f), new Color(0.42f, 0.07f, 0.04f), -9f);
            StageArt.Shape(sky, "Opening", Art.Soft, new Color(1f, 0.32f, 0.15f, 0.85f), new Vector2(0f, 18f), new Vector2(22f, 12f), -96);
            StageArt.Shape(sky, "OpeningCore", Art.Circle, new Color(1f, 0.55f, 0.3f, 0.7f), new Vector2(0f, 19f), new Vector2(7f, 3f), -95);

            var far = StageArt.Layer(root, "FarWalls", 0.75f);
            for (int side = -1; side <= 1; side += 2)
            {
                StageArt.Inked(far, "Wall", StageArt.Triangle, new Color(0.24f, 0.07f, 0.06f), new Vector2(side * 16f, 2f), new Vector2(16f, 34f), -88, 0f, 0.08f);
                StageArt.Inked(far, "Spire", Art.Rounded, new Color(0.2f, 0.06f, 0.05f), new Vector2(side * 10f, -2f), new Vector2(3f, 16f), -86, 0f, 0.08f);
            }
            // the forge: chimneys and hanging chains in the middle distance
            var forge = StageArt.Layer(root, "Forge", 0.55f);
            for (int i = 0; i < 2; i++)
            {
                float x = i == 0 ? -12.5f : 12.5f;
                StageArt.Inked(forge, "Chimney", Art.Square, new Color(0.22f, 0.13f, 0.12f), new Vector2(x, 2f), new Vector2(2f, 16f), -70, 0f, 0.1f);
                StageArt.Pulse(StageArt.Shape(forge, "Mouth", Art.Soft, new Color(1f, 0.5f, 0.1f, 0.7f), new Vector2(x, -2f), new Vector2(3f, 2.4f), -69), 0.4f, 1.6f, i);
                StageArt.Pulse(StageArt.Shape(forge, "Smoke", Art.Soft, new Color(0.3f, 0.2f, 0.2f, 0.5f), new Vector2(x, 11f), new Vector2(5f, 4f), -71), 0.5f, 0.8f, i);
            }
            for (int i = 0; i < 4; i++)
            {
                var chain = StageArt.Shape(forge, "Chain", Art.Square, Ink, new Vector2(-6f + i * 4f, 9f), new Vector2(0.08f, 6f + (i % 2) * 2f), -66);
                StageArt.Sway(chain, 2f, 0.7f, i);
            }

            var near = StageArt.Layer(root, "Obsidian", 0.35f);
            for (int side = -1; side <= 1; side += 2)
            {
                StageArt.Inked(near, "Obsidian", StageArt.Triangle, new Color(0.07f, 0.05f, 0.08f), new Vector2(side * 17f, 4f), new Vector2(14f, 40f), -60, 0f, 0.14f);
                StageArt.Shape(near, "Sheen", StageArt.Triangle, new Color(0.18f, 0.12f, 0.2f), new Vector2(side * 16f - side * 1.5f, 3f), new Vector2(4f, 30f), -59);
                for (int i = 0; i < 6; i++)
                {
                    var crack = StageArt.Shape(near, "Crack", Art.Rounded, Lava, new Vector2(side * (13.5f + Hash(i + side * 9 + 20) * 3f), -6f + i * 3.2f), new Vector2(0.14f, 1.4f + Hash(i + 4) * 1.6f), -58, -30f + Hash(i + side) * 60f);
                    StageArt.Pulse(crack, 0.55f, 1.6f + Hash(i) * 1.2f, i);
                }
            }

            // magma sea at the ring-out line
            var magma = StageArt.Layer(root, "Magma", 0.05f);
            float surf = s.killY + 0.2f;
            StageArt.Pulse(StageArt.Shape(magma, "Heat", Art.Soft, new Color(1f, 0.38f, 0.05f, 0.75f), new Vector2(0f, surf + 1.5f), new Vector2(70f, 9f), -22), 0.25f, 1.4f);
            StageArt.Shape(magma, "Lake", Art.Square, Lava, new Vector2(0f, surf - 4f), new Vector2(90f, 8f), -20);
            StageArt.Shape(magma, "LakeDeep", Art.Square, new Color(0.75f, 0.15f, 0.04f), new Vector2(0f, surf - 6.5f), new Vector2(90f, 4f), -19);
            StageArt.Inked(magma, "Surface", Art.Square, LavaHot, new Vector2(0f, surf), new Vector2(90f, 0.35f), -18, 0f, 0.05f);
            for (int i = 0; i < 10; i++)
            {
                float x = -16f + i * 3.4f + Hash(i + 90) * 1.5f;
                StageArt.Bob(StageArt.Inked(magma, "Bubble", Art.Circle, LavaHot, new Vector2(x, surf + 0.05f), new Vector2(0.5f, 0.3f), -16, 0f, 0.04f), new Vector2(0f, 0.15f), 2f + Hash(i) * 2f, i);
                StageArt.Drift(StageArt.Inked(magma, "Crust", Art.Rounded, new Color(0.25f, 0.1f, 0.06f), new Vector2(x + 1.2f, surf - 0.05f), new Vector2(0.9f + Hash(i + 2) * 0.8f, 0.14f), -16, 0f, 0.03f), new Vector2(0.25f, 0f), -20f, 20f);
            }
            for (int i = 0; i < 24; i++)
            {
                var e = StageArt.Shape(root, "Ember", Art.Square, Hash(i) < 0.5f ? Lava : LavaHot, new Vector2(-14f + Hash(i + 100) * 28f, s.killY + Hash(i + 101) * 24f), new Vector2(0.09f, 0.09f), -7, 45f);
                StageArt.Drift(e, new Vector2(Hash(i + 102) * 0.6f - 0.3f, 2.2f + Hash(i + 103) * 2.4f), s.killY, 15f, true);
                StageArt.Pulse(e, 0.6f, 4f + Hash(i) * 4f, i);
            }
        }

        /// <summary>Basalt main stage with rim light, columns and glowing seams.</summary>
        static void LavaForgeMain(Transform root, StageInfo s)
        {
            float hw = s.halfWidth, gy = s.groundY;
            StageArt.Inked(root, "Underside", StageArt.Triangle, Color.Lerp(Basalt, Color.black, 0.3f), new Vector2(0f, gy - 3.4f), new Vector2(hw * 1.85f, 3.8f), -15, 180f, 0.12f);
            StageArt.Pulse(StageArt.Shape(root, "UnderGlow", Art.Soft, new Color(1f, 0.4f, 0.08f, 0.55f), new Vector2(0f, gy - 4.8f), new Vector2(5f, 3.5f), -16), 0.4f, 1.3f);
            StageArt.Inked(root, "Basalt", Art.Square, Basalt, new Vector2(0f, gy - 0.8f), new Vector2(hw * 2f, 1.6f), -11, 0f, 0.1f);
            for (int i = 0; i < 5; i++)
                StageArt.Shape(root, "Column", Art.Square, Color.Lerp(Basalt, Color.black, 0.35f), new Vector2(-hw + 1.1f + i * (hw * 2f - 2.2f) / 4f, gy - 0.95f), new Vector2(0.07f, 1.2f), -10);
            for (int i = 0; i < 4; i++)
                StageArt.Pulse(StageArt.Shape(root, "Seam", Art.Rounded, Lava, new Vector2(-hw * 0.7f + i * hw * 0.48f, gy - 1.1f + (i % 2) * 0.3f), new Vector2(0.8f, 0.07f), -9, -20f + i * 15f), 0.5f, 1.8f, i);
            StageArt.Inked(root, "Rim", Art.Square, new Color(0.95f, 0.45f, 0.16f), new Vector2(0f, gy - 0.06f), new Vector2(hw * 2f + 0.1f, 0.14f), -8, 0f, 0.05f);
            // anvil on the left edge (the forge)
            StageArt.Inked(root, "Anvil", Art.Square, new Color(0.3f, 0.3f, 0.34f), new Vector2(-hw + 1.2f, gy + 0.35f), new Vector2(0.9f, 0.3f), -13, 0f, 0.05f);
            StageArt.Inked(root, "AnvilFoot", Art.Square, new Color(0.25f, 0.25f, 0.28f), new Vector2(-hw + 1.2f, gy + 0.1f), new Vector2(0.4f, 0.25f), -13, 0f, 0.05f);
        }

        static void LavaForgePlatform(Transform t, float width, int i)
        {
            RockArt(t, width, Basalt, new Color(0.95f, 0.45f, 0.16f), Lava, false, false, 1.2f);
            StageArt.Pulse(StageArt.Shape(t, "Seam", Art.Rounded, Lava, new Vector2(0f, -0.55f), new Vector2(width * 0.4f, 0.06f), -10, 10f), 0.5f, 2f, i);
        }
    }
}
