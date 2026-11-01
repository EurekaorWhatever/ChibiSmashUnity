using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>Generates the graphic-novel textures at runtime: ink splatters, dry-brush strokes and halftone dots.</summary>
    public static class ComicArt
    {
        const int SplatterSize = 256;
        const int BrushWidth = 512, BrushHeight = 128;
        const int DotSize = 16;

        static readonly Sprite[] splatters = new Sprite[3];
        static Sprite brush, halftone;

        public static Sprite Splatter(int variant)
        {
            variant = Mathf.Abs(variant) % splatters.Length;
            if (splatters[variant] == null) splatters[variant] = MakeSplatter(variant * 37 + 11);
            return splatters[variant];
        }

        public static Sprite Brush
        {
            get
            {
                if (brush == null) brush = MakeBrush();
                return brush;
            }
        }

        /// <summary>Repeating dot pattern; use with Image.Type.Tiled.</summary>
        public static Sprite Halftone
        {
            get
            {
                if (halftone == null)
                {
                    var a = new float[DotSize * DotSize];
                    Stamp(a, DotSize, DotSize / 2f, DotSize / 2f, DotSize * 0.28f);
                    halftone = ToSprite(a, DotSize, DotSize, TextureWrapMode.Repeat);
                }
                return halftone;
            }
        }

        static Sprite MakeSplatter(int seed)
        {
            var rng = new System.Random(seed);
            var a = new float[SplatterSize * SplatterSize];
            float c = SplatterSize / 2f;

            // Core blot made of overlapping blobs
            for (int i = 0; i < 18; i++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float dist = (float)rng.NextDouble() * SplatterSize * 0.16f;
                Stamp(a, SplatterSize, c + Mathf.Cos(angle) * dist, c + Mathf.Sin(angle) * dist, 10f + (float)rng.NextDouble() * 24f);
            }

            // Flung droplets, shrinking along rays
            for (int ray = 0; ray < 14; ray++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float dist = SplatterSize * 0.2f;
                float size = 3f + (float)rng.NextDouble() * 6f;
                while (dist < SplatterSize * 0.47f && size > 0.8f)
                {
                    Stamp(a, SplatterSize, c + Mathf.Cos(angle) * dist, c + Mathf.Sin(angle) * dist, size);
                    dist += size * (1.5f + (float)rng.NextDouble() * 3f);
                    size *= 0.72f;
                }
            }
            return ToSprite(a, SplatterSize, SplatterSize, TextureWrapMode.Clamp);
        }

        static Sprite MakeBrush()
        {
            var a = new float[BrushWidth * BrushHeight];
            for (int y = 0; y < BrushHeight; y++)
            {
                float v = y / (float)(BrushHeight - 1);
                float taper = Mathf.Sin(v * Mathf.PI);
                float start = BrushWidth * (0.02f + 0.1f * Mathf.PerlinNoise(3.1f, y * 0.11f));
                float end = BrushWidth * (0.7f + 0.3f * Mathf.PerlinNoise(9.7f, y * 0.09f) * taper);
                for (int x = 0; x < BrushWidth; x++)
                {
                    if (x < start || x > end) continue;
                    float along = (x - start) / (end - start);
                    float bristle = Mathf.PerlinNoise(x * 0.012f, y * 0.45f);
                    // The stroke runs out of ink towards its end, leaving dry bristle streaks.
                    a[y * BrushWidth + x] = bristle > Mathf.Lerp(0.05f, 0.62f, along * along) ? 1f : 0f;
                }
            }
            return ToSprite(a, BrushWidth, BrushHeight, TextureWrapMode.Clamp);
        }

        static void Stamp(float[] a, int size, float cx, float cy, float radius)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - radius - 1f)), maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + radius + 1f));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - radius - 1f)), maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + radius + 1f));
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float coverage = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                int i = y * size + x;
                if (coverage > a[i]) a[i] = coverage;
            }
        }

        static Sprite ToSprite(float[] alpha, int w, int h, TextureWrapMode wrap)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = wrap };
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, (byte)(alpha[i] * 255f));
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }
    }
}
