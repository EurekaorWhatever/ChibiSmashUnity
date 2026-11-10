using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Procedural textures of the arcade UI design system (ported from the copy-ghibli-smash fork's Art.cs additions):
    /// halftone dots, diagonal hatching, grain, vertical fades and a halftone ramp. Kept in UI/ so Core/Art.cs stays
    /// untouched; built once (loading screen) and cached.
    /// </summary>
    public static class GuiArt
    {
        static Texture2D dotTex, stripeTex, grainTex, fadeTex, rampTex;

        /// <summary>One halftone dot per tile; draw tiled (wrap = Repeat).</summary>
        public static Texture2D DotTex => dotTex ? dotTex : (dotTex = MakeDot(32, 0.3f));
        /// <summary>45-degree hatching; draw tiled.</summary>
        public static Texture2D StripeTex => stripeTex ? stripeTex : (stripeTex = MakeStripes(32, 9));
        /// <summary>Sparse distressed speckles; draw tiled.</summary>
        public static Texture2D GrainTex => grainTex ? grainTex : (grainTex = MakeGrain(128));
        /// <summary>Vertical alpha ramp: transparent at the top of the rect, opaque at the bottom.</summary>
        public static Texture2D FadeTex => fadeTex ? fadeTex : (fadeTex = MakeFade(128));
        /// <summary>Halftone dots that shrink from the left edge to nothing at the right edge.</summary>
        public static Texture2D HalftoneRamp => rampTex ? rampTex : (rampTex = MakeRamp(32, 12, 10));

        /// <summary>Builds every procedural UI texture now (loading screen), so menus never hitch on first use.</summary>
        public static void WarmUp()
        {
            var _ = Art.SolidTex; _ = Art.CircleTex; _ = Art.RingTex; _ = Art.SoftTex; _ = Art.RoundedTex;
            _ = DotTex; _ = StripeTex; _ = GrainTex; _ = FadeTex; _ = HalftoneRamp;
        }

        static Texture2D Tiled(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
        }

        static Texture2D Clamped(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
        }

        static Texture2D MakeDot(int size, float radius)
        {
            var t = Tiled(size, size);
            var px = new Color32[size * size];
            float c = size / 2f, r = radius * size;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(c, c));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(r - d + 0.5f) * 255));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static Texture2D MakeStripes(int size, int thickness)
        {
            var t = Tiled(size, size);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int k = (x + y) % size;
                float a = Mathf.Clamp01(Mathf.Min(k + 0.5f, thickness - k + 0.5f));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static Texture2D MakeGrain(int size)
        {
            var t = Tiled(size, size);
            var px = new Color32[size * size];
            var rng = new System.Random(1207);   // fixed seed: the same grain every run (cosmetic only)
            for (int i = 0; i < px.Length; i++)
            {
                double v = rng.NextDouble();
                byte a = v > 0.985 ? (byte)255 : v > 0.95 ? (byte)110 : (byte)0;
                px[i] = new Color32(255, 255, 255, a);
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static Texture2D MakeFade(int size)
        {
            var t = Clamped(4, size);
            var px = new Color32[4 * size];
            for (int y = 0; y < size; y++)
            {
                float a = 1f - y / (size - 1f);          // row 0 is the bottom of the drawn rect
                a = a * a * (3f - 2f * a);
                for (int x = 0; x < 4; x++) px[y * 4 + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static Texture2D MakeRamp(int cols, int rows, int cell)
        {
            int w = cols * cell, h = rows * cell;
            var t = Clamped(w, h);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int col = x / cell;
                float k = 1f - col / (cols - 1f);
                float r = k * cell * 0.5f;
                float d = Vector2.Distance(new Vector2(x % cell + 0.5f, y % cell + 0.5f), new Vector2(cell * 0.5f, cell * 0.5f));
                px[y * w + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(r - d + 0.5f) * 255));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }
}
