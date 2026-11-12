using System;
using ChibiClash.Netcode;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Presentation of the five design stages (sim indices 3..7, decision ST-1; design/art/maps, design/gdd/stages).
    /// Builds the layered art cut from the map sheet (tools/art/stages/build_stages.py -> Resources/Stages/&lt;Id&gt;/) and keeps
    /// moving platforms and stage events in step with the simulation every frame. View only: it READS the sim through
    /// <see cref="ChibiSimulation"/>'s read-only helpers and never writes sim state (netcode-core rule).
    ///
    /// Alignment contract (design/gdd/stages/stage-geometry.json "conventions"): every sprite is imported with the stage's
    /// pixels-per-unit and a pivot at its anchor; world (0, 0) = centre of MAIN's walkable top = sim (0, 0), and this view
    /// uses groundY = 0, so a platform sprite placed at (PlatformCx, PlatformTop) / 1000 sits exactly on its collider,
    /// moving platforms included.
    /// </summary>
    public sealed class ChibiDesignStage : MonoBehaviour
    {
        /// <summary>Resource folder per design stage (index - 3).</summary>
        static readonly string[] Ids = { "Mountain", "Plain", "Volcano", "BirdLair", "Ocean" };
        /// <summary>Art name of each sim platform (ChibiStageTable order), per design stage. MAIN is the solid main stage.</summary>
        static readonly string[][] PlatNames =
        {
            new[] { "A", "B", "C", "D" }, new string[0], new[] { "L1", "L2", "L3", "L4" }, new[] { "L", "R" }, new[] { "T1", "T2", "T3" },
        };

        /// <summary>Display names from the map design sheet.</summary>
        public static readonly string[] Names =
        {
            "Mountain of the Beginning", "Plain of Unity", "The Volcano Abyss", "The Giant Bird Lair", "The Abandoned Ocean Monument",
        };
        /// <summary>Short names for compact stage buttons.</summary>
        public static readonly string[] ShortNames = { "Mountain", "Plain", "Volcano", "Bird Lair", "Ocean" };
        /// <summary>Taglines (KO edges + event, from the sheet's matrix).</summary>
        public static readonly string[] Taglines =
        {
            "Vertical KO · moving jade platforms above a misty drop",
            "Horizontal KO · one long field, nowhere to hide",
            "Every edge KOs · fire bursts from the magma lake",
            "Horizontal KO · the colossal bird sweeps the arena",
            "Every edge KOs · the whale descends, drop to the tiers",
        };
        /// <summary>Camera clear colour per stage (sheet palette: sky / deep tone).</summary>
        public static readonly Color[] Sky =
        {
            Hex(0x2F9FE6), Hex(0x1B8CF0), Hex(0x1B1022), Hex(0x13788F), Hex(0x073F7A),
        };

        // Sorting orders (fighters use 10..45, projectiles 59..60): stage art below, the foreground strip above.
        const int OrderFar = -300, OrderMid = -290, OrderBackdrop = -200, OrderCreature = -150, OrderHazardBack = -120,
                  OrderPlatform = -100, OrderHazardFront = -60, OrderForeground = 70;
        // Parallax: fraction of the camera's offset from the stage centre that a layer follows (1 = static backdrop).
        const float FarFollow = 0.55f, MidFollow = 0.3f;

        Func<ChibiSimState> _state;
        int _stage;
        Transform[] _plats;
        Transform _far, _mid;
        Vector3 _farHome, _midHome;
        SpriteRenderer _pillarL, _pillarR, _whale, _birdSwipe, _birdSil;
        float _pillarNativeH;
        Vector3 _whaleHome, _silHome;
        readonly SpriteRenderer[] _embers = new SpriteRenderer[16];
        float _emberT;
        // Audio (tools-programmer, design/audio/sfx-events.md 8.1 / 8.2): ambience bed token + hazard sound dedupe.
        readonly ChibiStageSfx _stageSfx = new ChibiStageSfx();
        int _ambToken;

        /// <summary>True for the sim indices of the design stages (3..7).</summary>
        public static bool IsDesign(int stage) => stage >= ChibiStageTable.Mountain && stage <= ChibiStageTable.Ocean;

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        /// <summary>Build stage <paramref name="stage"/> (3..7) under <paramref name="root"/> and describe it for the view.</summary>
        /// <example><code>var info = ChibiDesignStage.Build(5, root);   // The Volcano Abyss</code></example>
        public static StageInfo Build(int stage, Transform root)
        {
            int k = stage - ChibiStageTable.Mountain;
            var def = ChibiStageTable.Get(stage);
            var art = root.gameObject.AddComponent<ChibiDesignStage>();
            art._stage = stage;
            string dir = "Stages/" + Ids[k] + "/";

            art._far = Layer(root, dir + "Far", OrderFar, Vector3.zero);
            art._mid = Layer(root, dir + "Mid", OrderMid, Vector3.zero);
            if (art._far != null) art._farHome = art._far.position;
            if (art._mid != null) art._midHome = art._mid.position;
            var backdrop = Layer(root, dir + "Backdrop", OrderBackdrop, Vector3.zero);
            if (backdrop == null) backdrop = Layer(root, dir + "Full", OrderBackdrop, Vector3.zero);   // fallback: the whole illustration
            if (backdrop != null)
            {
                var b = backdrop.GetComponent<SpriteRenderer>().bounds;
                art.ArtRect = Rect.MinMaxRect(b.min.x, b.min.y, b.max.x, b.max.y);
            }
            Layer(root, dir + "Plat_MAIN", OrderPlatform, Vector3.zero);
            Layer(root, dir + "Foreground", OrderForeground, Vector3.zero);

            var names = PlatNames[k];
            art._plats = new Transform[def.Platforms.Length];
            for (int p = 0; p < art._plats.Length && p < names.Length; p++)
                art._plats[p] = Layer(root, dir + "Plat_" + names[p],
                    OrderPlatform, new Vector3(def.Platforms[p].Cx / 1000f, def.Platforms[p].Top / 1000f, 0f));

            switch (def.Hazard)
            {
                case ChibiStageTable.HazardFire:
                    art._pillarL = Sprite(root, dir + "Haz_PillarLeft", OrderHazardBack);
                    art._pillarR = Sprite(root, dir + "Haz_PillarRight", OrderHazardBack);
                    if (art._pillarL != null) art._pillarNativeH = art._pillarL.sprite.bounds.size.y;
                    for (int i = 0; i < art._embers.Length; i++)
                    {
                        var e = Sprite(root, dir + "Haz_Ember", OrderHazardFront);
                        if (e == null) break;
                        e.enabled = false;
                        art._embers[i] = e;
                    }
                    break;
                case ChibiStageTable.HazardBird:
                    art._birdSil = Sprite(root, dir + "Haz_BirdSilhouette", OrderCreature);
                    if (art._birdSil != null) { art._silHome = new Vector3(0.15f, 5.65f, 0f); art._birdSil.transform.position = art._silHome; }
                    art._birdSwipe = Sprite(root, dir + "Haz_BirdSwipe", OrderHazardFront);
                    if (art._birdSwipe != null) art._birdSwipe.enabled = false;
                    break;
                case ChibiStageTable.HazardWhale:
                    art._whale = Sprite(root, dir + "Haz_Whale", OrderCreature);
                    if (art._whale != null) { art._whaleHome = new Vector3(0.844f, 7.519f, 0f); art._whale.transform.position = art._whaleHome; }
                    break;
            }

            art._ambToken = ChibiAudio.PlayAmbience(stage);   // stage ambience bed (no-op outside Play Mode)
            ChibiStageAtmosphere.Attach(root, stage, art);    // living atmosphere (Game/StageAtmosphere.cs, view only)

            return new StageInfo
            {
                name = Names[k],
                halfWidth = def.HalfWidth / 1000f,
                groundY = 0f,
                blastX = def.BlastX / 1000f,
                killY = def.KillY / 1000f,
                ceilingY = def.CeilingY / 1000f,
                spawns = new[] { new Vector2(-def.SpawnX / 1000f, def.SpawnY / 1000f), new Vector2(def.SpawnX / 1000f, def.SpawnY / 1000f) },
                respawn = new Vector2(def.RespawnX / 1000f, def.RespawnY / 1000f),
                sky = Sky[k],
                design = art,
            };
        }

        static SpriteRenderer Sprite(Transform root, string path, int order)
        {
            var sp = Resources.Load<Sprite>(path);
            if (sp == null) return null;
            var go = new GameObject(path.Substring(path.LastIndexOf('/') + 1));
            go.transform.SetParent(root, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sp;
            sr.sortingOrder = order;
            return sr;
        }

        static Transform Layer(Transform root, string path, int order, Vector3 pos)
        {
            var sr = Sprite(root, path, order);
            if (sr == null) return null;
            sr.transform.position = pos;
            return sr.transform;
        }

        /// <summary>World rect of the painted backdrop (static, no parallax): the camera view is kept inside it so the edge
        /// of the art is never shown (ChibiCameraFraming.WithinArt). Empty when the stage has no backdrop sprite.</summary>
        public Rect ArtRect { get; private set; }

        /// <summary>Follow <paramref name="state"/> (the displayed sim state). Call once after Build; the view then calls Sync.</summary>
        public void Bind(Func<ChibiSimState> state) { _state = state; }

        /// <summary>Fades this stage's ambience bed out (only if no newer stage started its own).</summary>
        void OnDestroy() { ChibiAudio.StopAmbience(_ambToken); }

        /// <summary>Game-step interpolation fraction of the frame being drawn (see <see cref="Sync"/>).</summary>
        float _stepAlpha = 1f;

        /// <summary>How far behind the current game step to draw (1 - alpha): 0 = exactly the latest step.</summary>
        float Behind() => 1f - Mathf.Clamp01(_stepAlpha);

        /// <summary>
        /// Draw this frame. Called by ChibiSimView at the END of its LateUpdate (after the camera moved, so parallax never lags
        /// a frame) with the SAME time-scaled game-step alpha the fighters use (hitstop / slow-mo included), so fighters
        /// standing on moving platforms, the bird and the whale glide in lockstep at any refresh rate (perf audit 1 + 2).
        /// </summary>
        /// <param name="stepAlpha">Fraction (0..1) of the next game step already elapsed; 1 = the latest step.</param>
        public void Sync(float stepAlpha)
        {
            _stepAlpha = stepAlpha;
            var s = _state?.Invoke();
            if (s == null) return;
            var def = ChibiStageTable.Get(_stage);

            // Moving platforms: the sim's positions, interpolated from the previous tick (static ones never move).
            float behind = Behind();
            int clock = ChibiSimulation.PlatformClock(s);
            for (int p = 0; p < _plats.Length; p++)
            {
                if (_plats[p] == null) continue;
                var pd = def.Platforms[p];
                float cx = pd.CxAt(clock), top = pd.TopAt(clock);
                if (clock > 0 && behind > 0f)
                {
                    cx = Mathf.Lerp(cx, pd.CxAt(clock - 1), behind);
                    top = Mathf.Lerp(top, pd.TopAt(clock - 1), behind);
                }
                _plats[p].position = new Vector3(cx / 1000f, top / 1000f, 0f);
            }

            Parallax();
            switch (def.Hazard)
            {
                case ChibiStageTable.HazardFire: SyncFire(s); break;
                case ChibiStageTable.HazardBird: SyncBird(s); break;
                case ChibiStageTable.HazardWhale: SyncWhale(s); break;
            }
        }

        void Parallax()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var c = cam.transform.position;
            if (_far != null) _far.position = _farHome + new Vector3(c.x, c.y, 0f) * FarFollow;
            if (_mid != null) _mid.position = _midHome + new Vector3(c.x, c.y, 0f) * MidFollow;
        }

        // ------------------------------------------------------------------ Volcano: fire columns from the magma lake

        void SyncFire(ChibiSimState s)
        {
            ChibiAudio.StageHazard(_stageSfx, s);   // telegraph rumble / eruption roar per lane, once per (HazCount, phase)
            float bottom = ChibiHazardConst.FireBottom / 1000f;
            float top = ChibiSimulation.FireColumnTop(s) / 1000f;
            bool tele = s.HazPhase == ChibiHazardConst.Telegraph, erupt = s.HazPhase == ChibiHazardConst.PhaseFireErupt,
                 fade = s.HazPhase == ChibiHazardConst.PhaseFireFade;
            float flicker = 0.75f + 0.25f * Mathf.Sin(Time.time * 30f);
            for (int lane = 0; lane < 2; lane++)
            {
                var sr = lane == 0 ? _pillarL : _pillarR;
                if (sr == null || _pillarNativeH <= 0f) continue;
                bool on = ChibiSimulation.FireLaneOn(s, lane);
                float x = (lane == 0 ? ChibiHazardConst.FireLaneLeftX : ChibiHazardConst.FireLaneRightX) / 1000f;
                float h;
                if (on && erupt) h = Mathf.Max(0.3f, top - bottom);
                else if (on && fade) h = Mathf.Max(0.3f, (top - bottom) * 0.6f);
                else if (on && tele) h = 0.6f + 0.4f * flicker;   // telegraph: a low, flickering flare on the lane
                else h = 0f;
                sr.enabled = h > 0f;
                if (!sr.enabled) continue;
                sr.transform.position = new Vector3(x, bottom, 0f);
                sr.transform.localScale = new Vector3(tele ? 0.7f : 1f, h / _pillarNativeH, 1f);
                sr.color = new Color(1f, 1f, 1f, fade ? 0.55f : (tele ? 0.75f * flicker : 1f));
            }
            Embers(s, bottom, erupt || tele);
        }

        void Embers(ChibiSimState s, float bottom, bool active)
        {
            _emberT += Time.deltaTime;
            for (int i = 0; i < _embers.Length; i++)
            {
                var sr = _embers[i];
                if (sr == null) continue;
                var e = sr.transform;
                sr.enabled = active;
                if (!active) continue;
                int lane = i % 2;
                if (!ChibiSimulation.FireLaneOn(s, lane)) { sr.enabled = false; continue; }
                float x = (lane == 0 ? ChibiHazardConst.FireLaneLeftX : ChibiHazardConst.FireLaneRightX) / 1000f;
                float ph = Mathf.Repeat(_emberT * 0.9f + i * 0.137f, 1f);
                e.position = new Vector3(x + Mathf.Sin(i * 7.1f + _emberT * 3f) * 0.6f, bottom + ph * 9f, 0f);
                e.localScale = Vector3.one * (0.6f + 0.4f * Mathf.Sin(i * 3.3f));
                sr.color = new Color(1f, 1f, 1f, 1f - ph);
            }
        }

        // ------------------------------------------------------------------ Bird Lair: circling silhouette, then the swipe

        void SyncBird(ChibiSimState s)
        {
            ChibiAudio.StageHazard(_stageSfx, s);   // screech, gust, body pass (pan sweep), once per (HazCount, phase)
            bool tele = s.HazPhase == ChibiHazardConst.Telegraph;
            bool swipe = ChibiSimulation.BirdGustActive(s);
            if (_birdSil != null)
            {
                float t = Time.time * 0.35f;
                // Idle: a slow circle around its painted spot; telegraph: it banks away to start its dive (fades, grows).
                float grow = tele ? 1f + 0.6f * Mathf.Clamp01(s.HazElapsed / 90f) : 1f;
                _birdSil.enabled = !swipe;
                _birdSil.transform.position = _silHome + new Vector3(Mathf.Cos(t) * 2.2f, Mathf.Sin(t * 2f) * 0.6f, 0f);
                _birdSil.transform.localScale = new Vector3(Mathf.Cos(t) >= 0f ? grow : -grow, grow, 1f);
                _birdSil.color = new Color(1f, 1f, 1f, tele ? 1f - 0.7f * Mathf.Clamp01(s.HazElapsed / 90f) : 0.85f);
            }
            if (_birdSwipe != null)
            {
                bool body = ChibiSimulation.BirdBodyActive(s);
                _birdSwipe.enabled = body;
                if (body)
                {
                    int dir = s.HazArg >= 0 ? 1 : -1;
                    float band = (ChibiHazardConst.BirdBandY0 + ChibiHazardConst.BirdBandY1) * 0.5f / 1000f;
                    // 425 milli per tick: interpolate toward the previous tick so the pass glides at any refresh rate.
                    float bx = ChibiSimulation.BirdBodyX(s) - dir * ChibiHazardConst.BirdBodySpeed * Behind();
                    _birdSwipe.transform.position = new Vector3(bx / 1000f, band, 0f);
                    _birdSwipe.flipX = dir < 0;
                }
            }
        }

        // ------------------------------------------------------------------ Ocean: the whale descends from the top

        void SyncWhale(ChibiSimState s)
        {
            ChibiAudio.StageHazard(_stageSfx, s);   // whale song, descent swell, rise, once per (HazCount, phase)
            if (_whale == null) return;
            float halfH = _whale.sprite.bounds.extents.y;
            switch (s.HazPhase)
            {
                case ChibiHazardConst.Idle:
                    // Swims in the background at its painted spot.
                    _whale.sortingOrder = OrderCreature;
                    _whale.transform.position = _whaleHome + new Vector3(Mathf.Sin(Time.time * 0.25f) * 1.5f, Mathf.Sin(Time.time * 0.6f) * 0.2f, 0f);
                    _whale.color = new Color(0.75f, 0.85f, 1f, 0.8f);
                    break;
                case ChibiHazardConst.Telegraph:
                {
                    // Rises out of view, then its shadow darkens the water (the sim's telegraph length).
                    float k = Mathf.Clamp01(s.HazElapsed / 60f);
                    _whale.sortingOrder = OrderCreature;
                    _whale.transform.position = Vector3.Lerp(_whaleHome, new Vector3(0f, ChibiHazardConst.WhaleTopY / 1000f + halfH, 0f), k);
                    _whale.color = new Color(0.6f, 0.7f, 0.9f, 0.8f);
                    break;
                }
                default:
                {
                    // Interpolate the descent / rise from the previous tick (40 / 60 milli per tick).
                    float bottom = ChibiSimulation.WhaleBottom(s);
                    if (s.HazPhase == ChibiHazardConst.PhaseWhaleDescend) bottom += ChibiHazardConst.WhaleDescendSpeed * Behind();
                    else if (s.HazPhase == ChibiHazardConst.PhaseWhaleRise) bottom -= ChibiHazardConst.WhaleRiseSpeed * Behind();
                    _whale.sortingOrder = OrderHazardFront;
                    _whale.transform.position = new Vector3(0f, bottom / 1000f + halfH, 0f);
                }
                    _whale.color = Color.white;
                    break;
            }
        }
    }
}
