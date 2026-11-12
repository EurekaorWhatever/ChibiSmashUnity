using ChibiClash.Netcode;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Living atmosphere of the five design stages (light shafts, mist, embers, petals, bubbles, caustics, creatures):
    /// the cinematic layer on top of the painted art built by <see cref="ChibiDesignStage"/>. View only: it never reads
    /// or writes the simulation; the few event reactions (eruption glow, gust streaks, whale shadow) read the hazard
    /// sprites the stage view already drives.
    ///
    /// Rules: pooled (every renderer is made once in <see cref="Attach"/>, nothing allocates per frame), unscaled time
    /// (keeps breathing through hitstop and pause), one atlas + one material, and sorting that keeps everything behind
    /// fighters except a handful of small, low-alpha front motes (AtmosFx.Order*). Runs after ChibiSimView moved the
    /// camera (execution order 100), so the camera-relative parallax never lags a frame.
    ///
    /// Budget per stage (self-imposed, the project has none yet): at most ~110 sprite renderers, 1 atlas 1024^2 + 1
    /// caustic tile 256^2 (BC3 + mips, about 1.5 MB), one shared material; large soft quads (rays, mist, glows) limited to
    /// about 15 per stage to bound overdraw.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class ChibiStageAtmosphere : MonoBehaviour
    {
        const int MaxFeatures = 24;
        readonly AtmosFeature[] _fx = new AtmosFeature[MaxFeatures];
        int _n, _kind;
        Transform _host;
        float _time;
        Camera _cam;

        // Event hooks (read-only views of sprites ChibiDesignStage drives).
        SpriteRenderer _pillarL, _pillarR, _birdSwipe, _whale;
        AtmosGlows _hookGlows;
        AtmosField _hookField, _hookField2;
        AtmosRays _hookRays;
        AtmosCaustics _hookCaustics;
        float _hook;

        const int KMountain = 0, KPlain = 1, KVolcano = 2, KBirdLair = 3, KOcean = 4;

        /// <summary>
        /// Builds the atmosphere of design stage <paramref name="stage"/> (3..7) under <paramref name="root"/>. Call once from
        /// ChibiDesignStage.Build after the platforms and hazards exist (it finds them by name). Returns null when the
        /// stage is not a design stage or the Fx atlas is missing. <paramref name="art"/> is the stage's art component
        /// (kept for the hook signature; not needed today).
        /// </summary>
        /// <example><code>ChibiStageAtmosphere.Attach(root, stage, art);</code></example>
        public static ChibiStageAtmosphere Attach(Transform root, int stage, ChibiDesignStage art)
        {
            if (root == null || !ChibiDesignStage.IsDesign(stage)) return null;
            if (AtmosFx.Get(AtmosSprite.Dot) == null) return null;
            var host = new GameObject("Atmosphere").transform;
            host.SetParent(root, false);
            var a = root.gameObject.AddComponent<ChibiStageAtmosphere>();
            a._host = host;
            a._kind = stage - ChibiStageTable.Mountain;
            switch (a._kind)
            {
                case KMountain: a.Mountain(root); break;
                case KPlain: a.Plain(); break;
                case KVolcano: a.Volcano(root); break;
                case KBirdLair: a.BirdLair(root); break;
                case KOcean: a.Ocean(root); break;
            }
            return a;
        }

        T Add<T>(T f) where T : AtmosFeature
        {
            if (f != null && _n < MaxFeatures) _fx[_n++] = f;
            return f;
        }

        AtmosField Field(AtmosFieldDesc d, uint seed) => Add(new AtmosField(_host, d, seed));

        static SpriteRenderer FindSr(Transform root, string name)
        {
            var t = root.Find(name);
            return t != null ? t.GetComponent<SpriteRenderer>() : null;
        }

        static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

        // ------------------------------------------------------------------ Mountain of the Beginning

        void Mountain(Transform root)
        {
            var gold = AtmosFx.Hex(0xFFD27A);
            // Golden god-rays from the sun behind the top-right peaks, and the sun's halo.
            Add(new AtmosRays(_host, 5, AtmosFx.OrderRays, 0.08f, gold)
                .Add(new Vector2(12.5f, 13f), -30f, 24f, 4.2f, 0.13f, 1.5f, 13f)
                .Add(new Vector2(13.5f, 13f), -38f, 26f, 3.2f, 0.11f, 1.2f, 17f)
                .Add(new Vector2(12f, 13.5f), -46f, 22f, 4.8f, 0.10f, 1.8f, 11f)
                .Add(new Vector2(14f, 12.5f), -54f, 20f, 3.0f, 0.12f, 1.4f, 15f)
                .Add(new Vector2(11f, 13.5f), -22f, 20f, 2.6f, 0.08f, 1.6f, 19f));
            Add(new AtmosGlows(_host, 1, 0.05f).Add(AtmosSprite.Glow, AtmosFx.OrderRays, new Vector2(12f, 10.5f),
                new Vector2(5f, 5f), gold, 0.22f, 0.04f, 9f));

            // Mist bands across the drop: three depths, three speeds.
            var mist = C(0.92f, 0.97f, 1f);
            Add(new AtmosBands(_host, 4, AtmosSprite.Mist, AtmosFx.OrderFar, 0.15f, 0.25f, -26f, 26f, mist, 0.38f, 0.15f)
                .Add(-20f, -3.4f, 3.3f, 1.6f).Add(-7f, -3.9f, 3.0f, 1.4f, true).Add(6f, -3.3f, 3.4f, 1.7f).Add(19f, -3.8f, 3.1f, 1.5f, true));
            Add(new AtmosBands(_host, 4, AtmosSprite.Mist, AtmosFx.OrderMid, 0.05f, 0.42f, -26f, 26f, mist, 0.4f, 0.2f)
                .Add(-14f, -4.9f, 3.2f, 1.5f, true).Add(-1f, -5.3f, 3.5f, 1.6f).Add(12f, -4.8f, 3.0f, 1.4f, true).Add(25f, -5.2f, 3.3f, 1.5f));
            Add(new AtmosBands(_host, 3, AtmosSprite.Mist, AtmosFx.OrderOnPlatform, -0.05f, 0.65f, -26f, 26f, mist, 0.3f, 0.2f)
                .Add(-18f, -6.2f, 4f, 1.6f).Add(0f, -6.5f, 3.6f, 1.5f, true).Add(17f, -6.1f, 4.2f, 1.7f));

            // Waterfall (right, painted in the backdrop at x 4.5..9.4): falling spray sparkles, mist and glints at its foot.
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Droplet, Count = 20, Order = AtmosFx.OrderFar, Spawn = new Rect(4.8f, 4.5f, 4.4f, 5f),
                VelMin = new Vector2(-0.15f, -4.5f), VelMax = new Vector2(0.15f, -3f), Accel = new Vector2(0f, -3f),
                LifeMin = 1.4f, LifeMax = 2.4f, SizeMin = 0.35f, SizeMax = 0.6f, Motion = AtmosMotion.Align, RotOffset = 90f,
                ColorA = C(0.85f, 0.97f, 1f), Alpha = 0.6f, Twinkle = 3f, TwinkleDepth = 0.5f, FadeIn = 0.1f, FadeOut = 0.3f,
            }, 11);
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Puff, Count = 6, Order = AtmosFx.OrderMid, Spawn = new Rect(7.4f, -3.2f, 2.4f, 1.2f),
                VelMin = new Vector2(-0.25f, 0.25f), VelMax = new Vector2(0.3f, 0.6f), LifeMin = 2.5f, LifeMax = 4f,
                SizeMin = 1.4f, SizeMax = 2.2f, ColorA = C(0.95f, 0.99f, 1f), Alpha = 0.32f, Spin = 15f, FadeIn = 0.3f, FadeOut = 0.5f,
            }, 12);
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Glint, Count = 5, Order = AtmosFx.OrderMid, Spawn = new Rect(7.2f, -2.8f, 2.6f, 2f),
                VelMin = new Vector2(-0.2f, 0.1f), VelMax = new Vector2(0.2f, 0.5f), LifeMin = 0.8f, LifeMax = 1.6f,
                SizeMin = 0.25f, SizeMax = 0.4f, Alpha = 0.7f, Spin = 60f, FadeIn = 0.4f, FadeOut = 0.6f,
            }, 13);

            // Jade runes on the moving platforms A, B, D (glyph centres measured on the sprites, local to the top pivot).
            Add(new AtmosRunes(AtmosFx.Hex(0x7DFFD0))
                .AddPlatform(root.Find("Plat_A"), new[] { new Vector2(-1.01f, -1.17f), new Vector2(-0.02f, -1.21f), new Vector2(0.90f, -1.25f) })
                .AddPlatform(root.Find("Plat_B"), new[] { new Vector2(-0.96f, -1.19f), new Vector2(0.01f, -1.22f), new Vector2(0.94f, -1.27f) })
                .AddPlatform(root.Find("Plat_D"), new[] { new Vector2(-0.98f, -1.18f), new Vector2(0.00f, -1.21f), new Vector2(0.92f, -1.25f) }));

            // Golden dust in the light, a few soft motes up front.
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Dot, Count = 20, Order = AtmosFx.OrderMid, Follow = 0.1f, Spawn = new Rect(-16f, -3f, 32f, 15f),
                VelMin = new Vector2(-0.15f, 0.04f), VelMax = new Vector2(0.2f, 0.18f), WobbleAmp = 0.3f, WobbleFreq = 0.35f,
                LifeMin = 6f, LifeMax = 10f, SizeMin = 0.16f, SizeMax = 0.28f, ColorA = C(1f, 0.93f, 0.7f), ColorB = C(1f, 0.85f, 0.55f),
                Alpha = 0.6f, Twinkle = 0.8f, TwinkleDepth = 0.5f,
            }, 14);
            Field(FrontMotes(AtmosSprite.Dot, C(1f, 0.93f, 0.75f), 5, new Rect(-16f, -5f, 32f, 16f)), 15);

            // Distant birds crossing the sky now and then.
            var ink = C(0.16f, 0.26f, 0.32f);
            Add(new AtmosSchool(_host, AtmosSprite.Bird, 5, AtmosFx.OrderFar, 0.2f, 7.5f, 10.5f, 22f, 1.3f, 0.42f, ink, 0.55f, true, 5f, 12f, 16));
            Add(new AtmosSchool(_host, AtmosSprite.Bird, 3, AtmosFx.OrderFar, 0.25f, 5.5f, 8f, 22f, 1.0f, 0.32f, ink, 0.45f, true, 8f, 16f, 17));
        }

        // ------------------------------------------------------------------ Plain of Unity

        void Plain()
        {
            var sun = AtmosFx.Hex(0xFFF0A3);
            Add(new AtmosRays(_host, 4, AtmosFx.OrderRays, 0.05f, sun)
                .Add(new Vector2(15.5f, 14.5f), -36f, 26f, 4.2f, 0.11f, 1.2f, 14f)
                .Add(new Vector2(16f, 14f), -44f, 28f, 3.2f, 0.10f, 1.5f, 11f)
                .Add(new Vector2(15f, 14.5f), -52f, 24f, 4.8f, 0.09f, 1.0f, 17f)
                .Add(new Vector2(16.5f, 13.5f), -60f, 22f, 3.0f, 0.12f, 1.4f, 13f));
            Add(new AtmosGlows(_host, 1, 0.05f).Add(AtmosSprite.Glow, AtmosFx.OrderRays, new Vector2(15f, 13.5f),
                new Vector2(5.5f, 5.5f), sun, 0.25f, 0.04f, 10f));

            // Slow cloud shadows sweeping the ground (behind fighters).
            Add(new AtmosBands(_host, 3, AtmosSprite.Mist, AtmosFx.OrderOnPlatform, 0f, 0.7f, -28f, 28f, C(0.04f, 0.16f, 0.12f), 0.16f, 0.1f)
                .Add(-18f, 0.3f, 3.4f, 1.7f).Add(0f, -0.4f, 3.0f, 1.5f, true).Add(17f, 0.6f, 3.6f, 1.8f));

            // Wind: petals (pink / white), torn grass blades, faint streaks, pollen.
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Petal, Count = 24, Order = AtmosFx.OrderOnPlatform, Spawn = new Rect(-21f, -0.5f, 34f, 12.5f),
                VelMin = new Vector2(1.0f, -0.35f), VelMax = new Vector2(2.1f, -0.1f), WobbleAmp = 0.45f, WobbleFreq = 0.55f,
                LifeMin = 8f, LifeMax = 13f, SizeMin = 0.26f, SizeMax = 0.36f, ColorA = AtmosFx.Hex(0xFFC2D6), ColorB = C(1f, 0.97f, 0.96f),
                Alpha = 0.9f, Spin = 140f, TumbleFreq = 0.7f, Motion = AtmosMotion.Tumble, FadeIn = 0.1f, FadeOut = 0.2f,
            }, 21);
            Field(new AtmosFieldDesc     // GDD FRONT layer: petals below Y -0.3 only, alpha <= 0.4
            {
                Sprite = AtmosSprite.Petal, Count = 4, Order = AtmosFx.OrderFront, Follow = -0.15f, Spawn = new Rect(-21f, -3.6f, 34f, 3f),
                VelMin = new Vector2(1.4f, -0.1f), VelMax = new Vector2(2.4f, 0f), WobbleAmp = 0.4f, WobbleFreq = 0.5f,
                LifeMin = 9f, LifeMax = 13f, SizeMin = 0.5f, SizeMax = 0.62f, ColorA = AtmosFx.Hex(0xFFC2D6), ColorB = C(1f, 0.97f, 0.96f),
                Alpha = 0.35f, Spin = 120f, TumbleFreq = 0.6f, Motion = AtmosMotion.Tumble, FadeIn = 0.15f, FadeOut = 0.25f,
            }, 22);
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Blade, Count = 9, Order = AtmosFx.OrderOnPlatform, Spawn = new Rect(-21f, 0f, 34f, 4f),
                VelMin = new Vector2(2.0f, -0.1f), VelMax = new Vector2(3.2f, 0.3f), WobbleAmp = 0.3f, WobbleFreq = 0.8f,
                LifeMin = 4f, LifeMax = 7f, SizeMin = 0.4f, SizeMax = 0.55f, ColorA = AtmosFx.Hex(0xC9F35F), ColorB = AtmosFx.Hex(0x199A4A),
                Alpha = 0.85f, Spin = 220f, TumbleFreq = 1.1f, Motion = AtmosMotion.Tumble, FadeIn = 0.15f, FadeOut = 0.3f,
            }, 23);
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Streak, Count = 5, Order = AtmosFx.OrderMid, Follow = 0.05f, Spawn = new Rect(-20f, 1f, 34f, 11f),
                VelMin = new Vector2(5f, 0f), VelMax = new Vector2(7f, 0.2f), LifeMin = 1.2f, LifeMax = 2f, SizeMin = 0.7f, SizeMax = 1f,
                AspectY = 0.6f, Motion = AtmosMotion.Align, Alpha = 0.2f, FadeIn = 0.3f, FadeOut = 0.5f,
            }, 24);
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Dot, Count = 14, Order = AtmosFx.OrderMid, Follow = 0.08f, Spawn = new Rect(-17f, 0f, 34f, 10f),
                VelMin = new Vector2(0.3f, -0.05f), VelMax = new Vector2(0.8f, 0.15f), WobbleAmp = 0.3f, WobbleFreq = 0.4f,
                LifeMin = 7f, LifeMax = 11f, SizeMin = 0.16f, SizeMax = 0.26f, ColorA = C(1f, 0.97f, 0.75f), Alpha = 0.6f,
                Twinkle = 0.9f, TwinkleDepth = 0.45f,
            }, 25);
        }

        // ------------------------------------------------------------------ The Volcano Abyss

        void Volcano(Transform root)
        {
            _pillarL = FindSr(root, "Haz_PillarLeft");
            _pillarR = FindSr(root, "Haz_PillarRight");
            var crimson = AtmosFx.Hex(0xD4261C);
            var orange = AtmosFx.Hex(0xFF9A1F);
            const float Lava = -4.066f;     // stage-geometry.json hazards.lavaTopMilli

            // Red light breathing on the cave walls and a glow band over the magma lake; both flare during an eruption.
            _hookGlows = Add(new AtmosGlows(_host, 5, 0f)
                .Add(AtmosSprite.Glow, AtmosFx.OrderFar, new Vector2(-15f, 1f), new Vector2(6f, 8f), crimson, 0.12f, 0.06f, 3.4f, 0.14f)
                .Add(AtmosSprite.Glow, AtmosFx.OrderFar, new Vector2(-14.5f, 9f), new Vector2(5f, 6f), crimson, 0.09f, 0.05f, 4.1f, 0.1f)
                .Add(AtmosSprite.Glow, AtmosFx.OrderFar, new Vector2(15f, 1f), new Vector2(6f, 8f), crimson, 0.12f, 0.06f, 3.7f, 0.14f)
                .Add(AtmosSprite.Glow, AtmosFx.OrderFar, new Vector2(14.5f, 9f), new Vector2(5f, 6f), crimson, 0.09f, 0.05f, 4.4f, 0.1f)
                .Add(AtmosSprite.Glow, AtmosFx.OrderFar, new Vector2(0f, Lava + 0.2f), new Vector2(20f, 1.4f), orange, 0.16f, 0.05f, 2.7f, 0.1f));

            // Rising embers: far (parallax), mid, and a few soft ones up front. Orange / crimson only: the pale magma-core
            // colour is reserved for the fire-column telegraph (03-volcano.md readability).
            _hookField = Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Ember, Count = 26, Order = AtmosFx.OrderFar, Follow = 0.2f, Spawn = new Rect(-17f, -4.6f, 34f, 2.4f),
                VelMin = new Vector2(-0.2f, 0.6f), VelMax = new Vector2(0.2f, 1.3f), WobbleAmp = 0.4f, WobbleFreq = 0.6f,
                LifeMin = 8f, LifeMax = 13f, SizeMin = 0.32f, SizeMax = 0.5f, ColorA = C(1f, 0.55f, 0.12f), ColorB = C(1f, 0.36f, 0.08f),
                Alpha = 0.75f, Hdr = 1.3f, Twinkle = 5f, TwinkleDepth = 0.3f, FadeIn = 0.08f, FadeOut = 0.4f,
            }, 31);
            _hookField2 = Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Ember, Count = 22, Order = AtmosFx.OrderMid, Spawn = new Rect(-15f, -4.4f, 30f, 3f),
                VelMin = new Vector2(-0.3f, 1.0f), VelMax = new Vector2(0.3f, 2.0f), WobbleAmp = 0.6f, WobbleFreq = 0.8f,
                LifeMin = 5f, LifeMax = 9f, SizeMin = 0.5f, SizeMax = 0.85f, ColorA = C(1f, 0.58f, 0.15f), ColorB = C(1f, 0.38f, 0.1f),
                Alpha = 0.9f, Hdr = 1.25f, Twinkle = 7f, TwinkleDepth = 0.3f, FadeIn = 0.08f, FadeOut = 0.4f,
            }, 32);
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Ember, Count = 5, Order = AtmosFx.OrderFront, Follow = -0.12f, Spawn = new Rect(-16f, -4.2f, 32f, 4f),
                VelMin = new Vector2(-0.3f, 1.2f), VelMax = new Vector2(0.3f, 2.2f), WobbleAmp = 0.5f, WobbleFreq = 0.6f,
                LifeMin = 4f, LifeMax = 7f, SizeMin = 0.9f, SizeMax = 1.2f, ColorA = C(1f, 0.55f, 0.15f), Alpha = 0.3f, Hdr = 1.1f,
                FadeIn = 0.15f, FadeOut = 0.4f,
            }, 33);

            // Heat shimmer: faint wavy veils lifting off the lake (no distortion pass needed).
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Shimmer, Count = 7, Order = AtmosFx.OrderOnPlatform, Spawn = new Rect(-16f, Lava + 0.35f, 32f, 0.7f),
                VelMin = new Vector2(-0.05f, 0.25f), VelMax = new Vector2(0.05f, 0.45f), WobbleAmp = 0.3f, WobbleFreq = 0.25f,
                LifeMin = 2.6f, LifeMax = 4.2f, SizeMin = 1.5f, SizeMax = 2.3f, ColorA = C(1f, 0.78f, 0.55f), Alpha = 0.11f,
                Motion = AtmosMotion.Shimmer, FadeIn = 0.35f, FadeOut = 0.5f,
            }, 34);

            // Lava bubbles popping with a glow on the lake line.
            Add(new AtmosLavaBubbles(_host, 6, Lava, -16f, 16f, C(1f, 0.5f, 0.12f), C(1f, 0.62f, 0.2f)));
        }

        // ------------------------------------------------------------------ The Giant Bird Lair

        void BirdLair(Transform root)
        {
            _birdSwipe = FindSr(root, "Haz_BirdSwipe");
            var gold = AtmosFx.Hex(0xFFC069);
            var sunPos = new Vector2(0.2f, 1f);

            // Sunset: halo behind the plateau, warm horizon haze, a soft fan of rays rising from the sun.
            Add(new AtmosGlows(_host, 2, 0.05f)
                .Add(AtmosSprite.Glow, AtmosFx.OrderFar, sunPos, new Vector2(6f, 6f), gold, 0.3f, 0.05f, 8f)
                .Add(AtmosSprite.Mist, AtmosFx.OrderFar, new Vector2(0f, 0.5f), new Vector2(8f, 0.9f), AtmosFx.Hex(0xFF8A3C), 0.2f, 0.04f, 11f));
            Add(new AtmosRays(_host, 4, AtmosFx.OrderFar, 0.05f, gold)
                .Add(sunPos, 142f, 14f, 3f, 0.07f, 2f, 16f)
                .Add(sunPos, 166f, 16f, 2.6f, 0.08f, 1.5f, 13f)
                .Add(sunPos, 192f, 15f, 2.8f, 0.08f, 1.8f, 15f)
                .Add(sunPos, 216f, 13f, 3.2f, 0.06f, 2f, 18f));

            // Clouds drifting below the plateau: a far bank (parallax) and a near bank over the plateau's body.
            Add(new AtmosBands(_host, 5, AtmosSprite.Cloud, AtmosFx.OrderFar, 0.15f, 0.35f, -24f, 24f, AtmosFx.Hex(0xFFF1CF), 0.55f, 0.12f)
                .Add(-19f, -2.1f, 1.5f, 1f).Add(-9f, -2.5f, 1.8f, 1.1f, true).Add(0f, -2.2f, 1.4f, 0.9f).Add(9f, -2.6f, 1.7f, 1.1f, true).Add(18f, -2.0f, 1.5f, 1f));
            Add(new AtmosBands(_host, 4, AtmosSprite.Cloud, AtmosFx.OrderOnPlatform, -0.03f, 0.6f, -26f, 26f, C(1f, 0.95f, 0.86f), 0.6f, 0.1f)
                .Add(-20f, -3.9f, 2.2f, 1.3f).Add(-7f, -4.2f, 2.4f, 1.3f, true).Add(6f, -3.8f, 2f, 1.2f).Add(19f, -4.3f, 2.3f, 1.3f, true));

            // Drifting feathers (+ two soft ones up front below Y -2.5), fast wind streaks that turn with the gust, motes.
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Feather, Count = 12, Order = AtmosFx.OrderOnPlatform, Spawn = new Rect(-17f, -1f, 32f, 11f),
                VelMin = new Vector2(0.8f, -0.4f), VelMax = new Vector2(1.6f, -0.15f), WobbleAmp = 0.6f, WobbleFreq = 0.45f,
                LifeMin = 8f, LifeMax = 13f, SizeMin = 0.45f, SizeMax = 0.65f, ColorA = AtmosFx.Hex(0x8A5A3A), ColorB = C(0.96f, 0.9f, 0.8f),
                Alpha = 0.85f, Spin = 90f, TumbleFreq = 0.45f, Motion = AtmosMotion.Tumble, FadeIn = 0.12f, FadeOut = 0.2f,
            }, 41);
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Feather, Count = 2, Order = AtmosFx.OrderFront, Follow = -0.15f, Spawn = new Rect(-17f, -4.4f, 32f, 1.8f),
                VelMin = new Vector2(0.9f, -0.05f), VelMax = new Vector2(1.5f, 0.05f), WobbleAmp = 0.5f, WobbleFreq = 0.4f,
                LifeMin = 10f, LifeMax = 14f, SizeMin = 0.8f, SizeMax = 0.95f, ColorA = AtmosFx.Hex(0x8A5A3A), ColorB = C(0.96f, 0.9f, 0.8f),
                Alpha = 0.3f, Spin = 70f, TumbleFreq = 0.4f, Motion = AtmosMotion.Tumble, FadeIn = 0.15f, FadeOut = 0.25f,
            }, 42);
            _hookField = Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Streak, Count = 10, Order = AtmosFx.OrderMid, Follow = 0.05f, Spawn = new Rect(-20f, -1f, 40f, 12f),
                VelMin = new Vector2(9f, -0.2f), VelMax = new Vector2(13f, 0.2f), LifeMin = 0.8f, LifeMax = 1.4f, SizeMin = 0.7f, SizeMax = 1.3f,
                AspectY = 0.55f, Motion = AtmosMotion.Align, ColorA = C(1f, 0.97f, 0.9f), Alpha = 0.26f, FadeIn = 0.25f, FadeOut = 0.45f,
            }, 43);
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Dot, Count = 10, Order = AtmosFx.OrderMid, Follow = 0.1f, Spawn = new Rect(-15f, -2f, 30f, 12f),
                VelMin = new Vector2(0.2f, -0.05f), VelMax = new Vector2(0.6f, 0.12f), WobbleAmp = 0.3f, WobbleFreq = 0.4f,
                LifeMin = 7f, LifeMax = 11f, SizeMin = 0.16f, SizeMax = 0.24f, ColorA = C(1f, 0.9f, 0.7f), Alpha = 0.5f,
                Twinkle = 0.8f, TwinkleDepth = 0.45f,
            }, 44);
        }

        // ------------------------------------------------------------------ The Abandoned Ocean Monument

        void Ocean(Transform root)
        {
            _whale = FindSr(root, "Haz_Whale");
            // Swaying light rays from the surface.
            _hookRays = Add(new AtmosRays(_host, 6, AtmosFx.OrderRays, 0.1f, C(0.72f, 0.95f, 1f))
                .Add(new Vector2(-14f, 10.5f), 6f, 20f, 4f, 0.10f, 3f, 12f)
                .Add(new Vector2(-8f, 10.5f), -4f, 22f, 3f, 0.12f, 2.5f, 10f)
                .Add(new Vector2(-2f, 10.5f), 5f, 18f, 4.5f, 0.09f, 3f, 14f)
                .Add(new Vector2(4f, 10.5f), -6f, 21f, 3.4f, 0.11f, 2.8f, 11f)
                .Add(new Vector2(9.5f, 10.5f), 4f, 19f, 4f, 0.10f, 3.2f, 13f)
                .Add(new Vector2(14.5f, 10.5f), -3f, 22f, 3f, 0.09f, 2.4f, 15f));

            // Caustic light rippling over every platform.
            _hookCaustics = Add(new AtmosCaustics(C(0.7f, 1f, 1f), 0.2f)
                .AddPlatform(root.Find("Plat_MAIN")).AddPlatform(root.Find("Plat_T1"))
                .AddPlatform(root.Find("Plat_T2")).AddPlatform(root.Find("Plat_T3")));

            // Mid layer life: two fish schools and drifting jellyfish with a soft glow.
            Add(new AtmosSchool(_host, AtmosSprite.Fish, 7, AtmosFx.OrderFar, 0.25f, 2.5f, 5f, 22f, 1.4f, 0.5f, C(1f, 0.85f, 0.45f), 0.8f, false, 3f, 9f, 51));
            Add(new AtmosSchool(_host, AtmosSprite.Fish, 6, AtmosFx.OrderFar, 0.25f, -7f, -4.5f, 22f, 1.0f, 0.45f, C(0.55f, 0.95f, 0.85f), 0.7f, false, 5f, 12f, 52));
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Jelly, Count = 3, Order = AtmosFx.OrderFar, Follow = 0.25f, Spawn = new Rect(-15f, -9f, 30f, 8f),
                VelMin = new Vector2(-0.06f, 0.25f), VelMax = new Vector2(0.06f, 0.45f), LifeMin = 24f, LifeMax = 34f,
                SizeMin = 0.9f, SizeMax = 1.25f, ColorA = C(0.8f, 0.66f, 1f), ColorB = C(0.6f, 0.95f, 1f), Alpha = 0.6f,
                Motion = AtmosMotion.Pulse, TumbleFreq = 0.45f, FadeIn = 0.12f, FadeOut = 0.2f,
                Halo = AtmosSprite.Glow, HaloScale = 1.1f, HaloAlpha = 0.35f, HaloColor = C(0.55f, 0.9f, 1f),
            }, 53);

            // Rising bubbles and floating plankton (+ a few soft ones up front).
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Bubble, Count = 24, Order = AtmosFx.OrderMid, Follow = 0.05f, Spawn = new Rect(-17f, -11f, 34f, 9f),
                VelMin = new Vector2(-0.05f, 0.7f), VelMax = new Vector2(0.05f, 1.4f), WobbleAmp = 0.22f, WobbleFreq = 1.2f,
                LifeMin = 9f, LifeMax = 16f, SizeMin = 0.16f, SizeMax = 0.34f, ColorA = C(0.85f, 0.97f, 1f), Alpha = 0.55f,
                FadeIn = 0.08f, FadeOut = 0.15f,
            }, 54);
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Bubble, Count = 4, Order = AtmosFx.OrderFront, Follow = -0.12f, Spawn = new Rect(-17f, -11f, 34f, 9f),
                VelMin = new Vector2(-0.05f, 0.9f), VelMax = new Vector2(0.05f, 1.5f), WobbleAmp = 0.25f, WobbleFreq = 1f,
                LifeMin = 10f, LifeMax = 15f, SizeMin = 0.38f, SizeMax = 0.5f, ColorA = C(0.85f, 0.97f, 1f), Alpha = 0.28f,
                FadeIn = 0.1f, FadeOut = 0.15f,
            }, 55);
            Field(new AtmosFieldDesc
            {
                Sprite = AtmosSprite.Dot, Count = 26, Order = AtmosFx.OrderMid, Follow = 0.1f, Spawn = new Rect(-17f, -10f, 34f, 19f),
                VelMin = new Vector2(-0.08f, 0.02f), VelMax = new Vector2(0.08f, 0.1f), WobbleAmp = 0.15f, WobbleFreq = 0.3f,
                LifeMin = 8f, LifeMax = 14f, SizeMin = 0.12f, SizeMax = 0.2f, ColorA = C(0.75f, 1f, 0.95f), Alpha = 0.5f,
                Twinkle = 1.4f, TwinkleDepth = 0.5f,
            }, 56);
            Field(FrontMotes(AtmosSprite.Dot, C(0.75f, 1f, 0.95f), 5, new Rect(-17f, -10f, 34f, 19f)), 57);
        }

        /// <summary>Soft out-of-focus motes over the fighters: few, large, alpha 0.2, near-plane parallax.</summary>
        static AtmosFieldDesc FrontMotes(AtmosSprite sprite, Color col, int count, Rect spawn) => new AtmosFieldDesc
        {
            Sprite = sprite, Count = count, Order = AtmosFx.OrderFront, Follow = -0.12f, Spawn = spawn,
            VelMin = new Vector2(-0.12f, 0.03f), VelMax = new Vector2(0.15f, 0.15f), WobbleAmp = 0.3f, WobbleFreq = 0.3f,
            LifeMin = 8f, LifeMax = 12f, SizeMin = 0.35f, SizeMax = 0.5f, ColorA = col, Alpha = 0.2f, FadeIn = 0.25f, FadeOut = 0.35f,
        };

        // ------------------------------------------------------------------ per frame

        void LateUpdate()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            _time += dt;
            if (_cam == null) _cam = Camera.main;
            Vector2 cam = _cam != null ? (Vector2)_cam.transform.position : Vector2.zero;
            Hooks(dt);
            for (int i = 0; i < _n; i++) _fx[i].Tick(dt, _time, cam);
        }

        /// <summary>Event reactions, read from the hazard sprites ChibiDesignStage drives (no sim access).</summary>
        void Hooks(float dt)
        {
            switch (_kind)
            {
                case KVolcano:
                {
                    bool erupt = (_pillarL != null && _pillarL.enabled) || (_pillarR != null && _pillarR.enabled);
                    _hook = Mathf.MoveTowards(_hook, erupt ? 1f : 0f, dt * (erupt ? 3f : 0.8f));
                    if (_hookGlows != null) _hookGlows.Boost = _hook;
                    if (_hookField != null) _hookField.SpeedMul = 1f + 0.5f * _hook;
                    if (_hookField2 != null) _hookField2.SpeedMul = 1f + 0.6f * _hook;
                    break;
                }
                case KBirdLair:
                {
                    // Gust: streaks follow the swipe's direction (design/gdd/stages/04-bird-lair.md FRONT row) and speed up.
                    bool gust = _birdSwipe != null && _birdSwipe.enabled;
                    _hook = Mathf.MoveTowards(_hook, gust ? 1f : 0f, dt * 2.5f);
                    if (_hookField != null)
                    {
                        _hookField.DirX = gust && _birdSwipe.flipX ? -1f : 1f;
                        _hookField.SpeedMul = 1f + 0.8f * _hook;
                        _hookField.Intensity = 1f + 0.6f * _hook;
                    }
                    break;
                }
                case KOcean:
                {
                    // The descending whale (drawn in front, order -60) shadows the water: rays and caustics dim.
                    bool shadow = _whale != null && _whale.enabled && _whale.sortingOrder > -150;
                    _hook = Mathf.MoveTowards(_hook, shadow ? 1f : 0f, dt * 0.8f);
                    if (_hookRays != null) _hookRays.Intensity = 1f - 0.6f * _hook;
                    if (_hookCaustics != null) _hookCaustics.Intensity = 1f - 0.5f * _hook;
                    break;
                }
            }
        }
    }
}
