using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Procedural textures, sprites and HDR materials for the blade trails, hit sparks and afterimages
    /// (<see cref="ChibiBladeTrails"/>). Presentation only, built once and cached (no per-frame allocation).
    ///
    /// HDR contract: every material made here carries a tint of <see cref="Peak"/> (material <c>_Color</c>), so colours
    /// are authored as <c>value / Peak</c> and a texel or vertex colour of 1 renders at Peak (3x). ChibiPostFx blooms above
    /// 0.82, so white-hot cores (1.0 -> 3.0) bloom hard and colour bands authored around 0.45-0.6 bloom softly. This works
    /// whether or not the platform clamps vertex / renderer colours to 0..1.
    ///
    /// Shaders: <c>ChibiClash/FxSpriteAdditive</c> (sprites) and <c>ChibiClash/FxMeshAdditive</c> (ribbon meshes) live in
    /// Resources/Fx so builds include them. When one is missing or unsupported the stock sprite shader is used instead
    /// (alpha blend; the HDR tint still pushes the cores past the bloom threshold).
    /// </summary>
    public static class ChibiFxTextures
    {
        /// <summary>Material tint multiplier: authored colours are divided by this, the material multiplies it back.</summary>
        public const float Peak = 3f;
        /// <summary>
        /// Additive sprite shader (the URP 2D Sprite-Unlit-Default pass, additive blend). Deliberately NOT named
        /// "ChibiClash/SpriteAdditive": ChibiLaunchFx looks that name up and would silently switch to additive.
        /// </summary>
        public const string SpriteAdditiveShader = "ChibiClash/FxSpriteAdditive";
        /// <summary>Additive unlit vertex-colour shader for the ribbon meshes.</summary>
        public const string MeshAdditiveShader = "ChibiClash/FxMeshAdditive";

        /// <summary>Natural height of the sprites below in world units (they are all 1 unit wide).</summary>
        public const float RayH = 0.25f, LineH = 0.125f, RoundH = 1f;

        /// <summary>Texture height (across the ribbon) of the gradient strips; width is 2 texels (U is unused).</summary>
        const int StripH = 64;

        static Sprite _ray, _glow, _ring, _line;
        static Texture2D _glowEdge, _glowCentre;
        static Material _spriteLight;
        static Shader _meshShader, _spriteShader;
        static bool _shadersLooked;

        /// <summary>Lens-shaped flare spike, 128x32, bright centre tapering to both points (star flare rays).</summary>
        public static Sprite Ray => _ray != null ? _ray : (_ray = MakeSprite(Fill(128, 32, RayAlpha)));
        /// <summary>Soft glow dot with a hot centre, 64x64 (spark cores and halos).</summary>
        public static Sprite Glow => _glow != null ? _glow : (_glow = MakeSprite(Fill(64, 64, GlowAlpha)));
        /// <summary>Thin soft shock ring, 128x128.</summary>
        public static Sprite Ring => _ring != null ? _ring : (_ring = MakeSprite(Fill(128, 128, RingAlpha)));
        /// <summary>Speed line, 64x8: transparent tail on the left, bright rounded head on the right (+x).</summary>
        public static Sprite Line => _line != null ? _line : (_line = MakeSprite(Fill(64, 8, LineAlpha)));

        /// <summary>Additive (or fallback) sprite material with the HDR tint: sparks, flares and afterimages.</summary>
        public static Material SpriteLight
        {
            get
            {
                if (_spriteLight != null) return _spriteLight;
                LookShaders();
                _spriteLight = _spriteShader != null ? new Material(_spriteShader) : new Material(Art.SpriteMat);
                _spriteLight.name = "ChibiFxSpriteLight";
                _spriteLight.hideFlags = HideFlags.DontSave;
                _spriteLight.color = new Color(Peak, Peak, Peak, 1f);
                return _spriteLight;
            }
        }

        /// <summary>
        /// New ribbon material sampling <paramref name="tex"/> with the HDR tint. The caller owns it (destroy on teardown).
        /// </summary>
        public static Material NewRibbonMaterial(Texture2D tex, string name)
        {
            LookShaders();
            var m = _meshShader != null ? new Material(_meshShader) : new Material(Art.SpriteMat);
            m.name = name;
            m.hideFlags = HideFlags.DontSave;
            m.mainTexture = tex;
            m.color = new Color(Peak, Peak, Peak, 1f);
            return m;
        }

        /// <summary>Generic glow strip (white, tinted by vertex colour): peak near the outer edge (arcs) or the centre (streaks).</summary>
        public static Texture2D GlowStrip(bool centred)
        {
            if (centred) return _glowCentre != null ? _glowCentre : (_glowCentre = Strip(Color.white, true, true));
            return _glowEdge != null ? _glowEdge : (_glowEdge = Strip(Color.white, false, true));
        }

        /// <summary>
        /// Blade strip baked for character colour <paramref name="fx"/> (caller owns it): V = 0 inner edge .. 1 blade tip for
        /// arcs (<paramref name="centred"/> false), or edge .. centre .. edge for streaks. White-hot core -> Fx colour ->
        /// transparent edge, stored / Peak.
        /// </summary>
        public static Texture2D BladeStrip(Color fx, bool centred) => Strip(fx, centred, false);

        // ------------------------------------------------------------------ profiles (pure functions, also mirrored in the preview script)

        /// <summary>Blade profile at <paramref name="v"/> (0..1 across): core weight (white mix) and alpha.</summary>
        public static void BladeProfile(float v, bool centred, out float core, out float alpha, out float body)
        {
            if (centred)
            {
                float d = Mathf.Abs(v - 0.5f) * 2f;                         // 0 centre .. 1 edge
                core = Mathf.Exp(-(d / 0.24f) * (d / 0.24f));
                alpha = 1f - Smooth(0.3f, 1f, d);
                body = 1f - 0.35f * d;
            }
            else
            {
                // Inner edge (v 0) fades in; a white-hot line rides just inside the tip edge (v ~0.86); crisp outer edge.
                core = Mathf.Exp(-((v - 0.85f) / 0.075f) * ((v - 0.85f) / 0.075f));
                alpha = Mathf.Pow(Smooth(0f, 0.7f, v), 1.3f) * (1f - Smooth(0.93f, 1f, v));
                body = 0.35f + 0.65f * v;
            }
        }

        /// <summary>Glow profile at <paramref name="v"/>: alpha only (colour comes from the vertex colour).</summary>
        public static float GlowProfile(float v, bool centred)
        {
            if (centred)
            {
                float d = Mathf.Abs(v - 0.5f) * 2f;
                return Mathf.Exp(-(d / 0.5f) * (d / 0.5f)) * (1f - Smooth(0.85f, 1f, d));
            }
            float x = (v - 0.78f) / 0.24f;
            return Mathf.Exp(-x * x) * (1f - Smooth(0.95f, 1f, v)) * Smooth(0f, 0.15f, v);
        }

        /// <summary>Colour band brightness (x Peak on screen): 0.5 -> 1.5x the Fx colour, blooms softly under the white core.</summary>
        public const float BandLevel = 0.5f;

        static Texture2D Strip(Color fx, bool centred, bool glow)
        {
            var t = NewTex(2, StripH);
            var px = new Color32[2 * StripH];
            for (int y = 0; y < StripH; y++)
            {
                float v = (y + 0.5f) / StripH;
                Color c;
                if (glow) c = new Color(1f, 1f, 1f, GlowProfile(v, centred));
                else
                {
                    BladeProfile(v, centred, out float core, out float a, out float body);
                    var band = new Color(fx.r * BandLevel * body, fx.g * BandLevel * body, fx.b * BandLevel * body);
                    c = Color.Lerp(band, Color.white, core);   // core texel 1.0 -> Peak (3x) on screen
                    c.a = a;
                }
                px[y * 2] = px[y * 2 + 1] = c;
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static float Smooth(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        // fx, fy: -1..1 across the texture.
        static float RayAlpha(float fx, float fy)
        {
            float along = 1f - Mathf.Abs(fx);
            float w = 0.1f + 0.9f * Mathf.Pow(along, 1.4f);
            float q = fy / w;
            return Mathf.Pow(along, 1.7f) * Mathf.Exp(-q * q * 3.5f);
        }

        static float GlowAlpha(float fx, float fy)
        {
            float d2 = fx * fx + fy * fy;
            float d = Mathf.Sqrt(d2);
            return Mathf.Clamp01((Mathf.Exp(-d2 * 7f) * 0.7f + Mathf.Exp(-d2 * 40f) * 0.3f) * (1f - Smooth(0.85f, 1f, d)));
        }

        static float RingAlpha(float fx, float fy)
        {
            float d = Mathf.Sqrt(fx * fx + fy * fy);
            float r = (d - 0.84f) / 0.055f, f = (d - 0.7f) / 0.2f;
            return Mathf.Clamp01((Mathf.Exp(-r * r) + 0.12f * Mathf.Exp(-f * f)) * (1f - Smooth(0.96f, 1f, d)));
        }

        static float LineAlpha(float fx, float fy)
        {
            float x = (fx + 1f) * 0.5f;                                     // 0 tail .. 1 head
            float head = Mathf.Clamp01((1f - x) / 0.1f);
            return Mathf.Pow(x, 1.3f) * Mathf.Sqrt(head) * Mathf.Exp(-fy * fy * 4f);
        }

        static Texture2D Fill(int w, int h, System.Func<float, float, float> alpha)
        {
            var t = NewTex(w, h);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) / w * 2f - 1f, fy = (y + 0.5f) / h * 2f - 1f;
                px[y * w + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha(fx, fy)));
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static Texture2D NewTex(int w, int h) => new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontSave,
        };

        // 1 world unit wide, centred pivot.
        static Sprite MakeSprite(Texture2D t)
        {
            var s = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width);
            s.hideFlags = HideFlags.DontSave;
            return s;
        }

        static void LookShaders()
        {
            if (_shadersLooked) return;
            _shadersLooked = true;
            var s = Shader.Find(SpriteAdditiveShader);
            _spriteShader = s != null && s.isSupported ? s : null;
            var m = Shader.Find(MeshAdditiveShader);
            _meshShader = m != null && m.isSupported ? m : null;
        }
    }
}
