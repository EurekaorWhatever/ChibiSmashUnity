using UnityEngine;

namespace ChibiClash
{
    /// <summary>Shapes in Resources/Stages/Fx/AtmosAtlas.png (tools/art/stages/build_atmos_fx.py CELLS, same order).</summary>
    public enum AtmosSprite
    {
        Dot, Ember, Glint, Bubble, Petal, Feather, Blade, Ring, Fish, Jelly, Bird, Droplet, LavaDome, Puff,
        Streak, Shimmer, Cloud, Mist, Ray, Glow,
        /// <summary>Count of shapes; also "none" for optional slots.</summary>
        Count,
    }

    /// <summary>
    /// Shared resources and sorting for the stage atmosphere (StageAtmosphere.cs). One atlas texture + one unlit material
    /// for every ambient sprite, so consecutive atmosphere sprites at the same sorting order batch together.
    /// View only, never touches the simulation.
    /// </summary>
    public static class AtmosFx
    {
        // Sorting (stage art: Far -300, Mid -290, Backdrop -200, creatures -150, hazards -120 / -60, platforms -100,
        // fighters 10..45, projectiles ~60, Foreground 70). Atmosphere stays behind fighters except OrderFront motes.
        /// <summary>Just over the painted backdrop: distant birds, fish, far embers, back mist, wall light.</summary>
        public const int OrderFar = -195;
        /// <summary>Light shafts: over the far layer, under creatures and platforms.</summary>
        public const int OrderRays = -185;
        /// <summary>Mid air between the creatures (-150) and the back hazards (-120): motes, bubbles, embers.</summary>
        public const int OrderMid = -130;
        /// <summary>Caustic light, masked to the platform sprites (exclusive mask range).</summary>
        public const int OrderCaustic = -96;
        /// <summary>On top of platform art, under front hazards and fighters: rune glints, near mist, shimmer, petals.</summary>
        public const int OrderOnPlatform = -95;
        /// <summary>A few soft motes over fighters (always low alpha, small counts), under the Foreground strip (70).</summary>
        public const int OrderFront = 65;
        /// <summary>Lava surface bubbles: over the opaque magma Foreground (70) at the lake line, where no fight happens.</summary>
        public const int OrderOverLava = 72;

        const string AtlasPath = "Stages/Fx/AtmosAtlas", CausticsPath = "Stages/Fx/Caustics";
        const int Cell = 128, AtlasPx = 1024;
        /// <summary>World size of one caustic tile (Caustics.png repeats seamlessly).</summary>
        public const float CausticTile = 3f;

        // col, row, w, h in 128 px cells, origin top-left (mirrors build_atmos_fx.py CELLS).
        static readonly byte[] Layout =
        {
            0, 0, 1, 1,  1, 0, 1, 1,  2, 0, 1, 1,  3, 0, 1, 1,  4, 0, 1, 1,  5, 0, 1, 1,  6, 0, 1, 1,  7, 0, 1, 1,
            0, 1, 1, 1,  1, 1, 1, 1,  2, 1, 1, 1,  3, 1, 1, 1,  4, 1, 1, 1,  5, 1, 1, 1,
            0, 2, 4, 1,  4, 2, 4, 1,
            0, 3, 4, 2,  4, 3, 4, 2,
            0, 5, 1, 3,  1, 5, 2, 2,
        };

        static readonly Sprite[] Sprites = new Sprite[(int)AtmosSprite.Count];
        static Texture2D _atlas;
        static Sprite _caustic;

        /// <summary>
        /// Sprite for <paramref name="id"/>: 1 world unit per 128 px cell (Dot = 1 x 1, Streak = 4 x 1, Ray = 1 x 3 with
        /// its pivot at the top, everything else centred). Null when the atlas is missing.
        /// </summary>
        public static Sprite Get(AtmosSprite id)
        {
            int i = (int)id;
            if (i < 0 || i >= Sprites.Length) return null;
            if (Sprites[i] != null) return Sprites[i];
            if (_atlas == null) _atlas = Resources.Load<Texture2D>(AtlasPath);
            if (_atlas == null) return null;
            float k = _atlas.width / (float)AtlasPx;     // survives a max-size downscale on import
            int c = Layout[i * 4], r = Layout[i * 4 + 1], w = Layout[i * 4 + 2], h = Layout[i * 4 + 3];
            var rect = new Rect(c * Cell * k, _atlas.height - (r + h) * Cell * k, w * Cell * k, h * Cell * k);
            var pivot = id == AtmosSprite.Ray ? new Vector2(0.5f, 1f) : new Vector2(0.5f, 0.5f);
            var sp = Sprite.Create(_atlas, rect, pivot, Cell * k, 0, SpriteMeshType.FullRect);
            sp.name = "Atmos" + id;
            Sprites[i] = sp;
            return sp;
        }

        /// <summary>Seamless caustic tile (<see cref="CausticTile"/> units square, for SpriteDrawMode.Tiled). Null when missing.</summary>
        public static Sprite Caustic
        {
            get
            {
                if (_caustic != null) return _caustic;
                var tex = Resources.Load<Texture2D>(CausticsPath);
                if (tex == null) return null;
                _caustic = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f),
                    tex.width / CausticTile, 0, SpriteMeshType.FullRect);
                _caustic.name = "AtmosCaustic";
                return _caustic;
            }
        }

        /// <summary>New sprite renderer under <paramref name="parent"/> with the shared unlit material. Null when the atlas is missing.</summary>
        public static SpriteRenderer NewRenderer(Transform parent, string name, AtmosSprite id, int order)
        {
            var sp = Get(id);
            if (sp == null) return null;
            return NewRenderer(parent, name, sp, order);
        }

        /// <summary>New sprite renderer for an explicit sprite.</summary>
        public static SpriteRenderer NewRenderer(Transform parent, string name, Sprite sprite, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sharedMaterial = Art.SpriteMat;
            sr.sortingOrder = order;
            sr.color = new Color(1f, 1f, 1f, 0f);
            return sr;
        }

        /// <summary>Hex colour helper (0xRRGGBB).</summary>
        public static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        /// <summary>Smoothstep of x clamped to 0..1.</summary>
        public static float Smooth01(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        /// <summary>
        /// Writes colour <paramref name="c"/> scaled by <paramref name="hdr"/> with alpha <paramref name="a"/> and hides the
        /// renderer when it is invisible (saves the draw).
        /// </summary>
        public static void Paint(SpriteRenderer sr, Color c, float hdr, float a)
        {
            bool on = a > 0.004f;
            if (sr.enabled != on) sr.enabled = on;
            if (on) sr.color = new Color(c.r * hdr, c.g * hdr, c.b * hdr, a);
        }
    }

    /// <summary>Tiny xorshift RNG for cosmetic variation (keeps UnityEngine.Random and the sim RNG untouched).</summary>
    public struct AtmosRng
    {
        uint _s;

        /// <summary>Seeded generator (0 is remapped).</summary>
        public AtmosRng(uint seed) { _s = seed == 0 ? 0x9E3779B9u : seed; }

        /// <summary>Uniform 0..1.</summary>
        public float Next()
        {
            _s ^= _s << 13;
            _s ^= _s >> 17;
            _s ^= _s << 5;
            return (_s & 0xFFFFFF) / 16777216f;
        }

        /// <summary>Uniform a..b.</summary>
        public float Range(float a, float b) => a + (b - a) * Next();
    }

    /// <summary>One ambient element of a stage. Ticked by <see cref="ChibiStageAtmosphere"/> after the camera moved.</summary>
    public abstract class AtmosFeature
    {
        /// <summary>Runtime alpha multiplier (stage hooks: eruptions, whale shadow).</summary>
        public float Intensity = 1f;

        /// <summary>Advance and draw. <paramref name="cam"/> = camera XY used for parallax (layer pos + cam x follow).</summary>
        public abstract void Tick(float dt, float time, Vector2 cam);
    }
}
