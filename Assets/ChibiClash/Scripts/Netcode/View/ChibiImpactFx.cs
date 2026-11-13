using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Anime-fighter impact polish for the netcode view (presentation only; reads sim states, never writes them):
    /// <list type="bullet">
    /// <item>Silhouette flash: a 1-2 frame white / attacker-tinted additive copy of the hit fighter's body sprite.</item>
    /// <item>Radial burst on heavy hits: focus lines converging on the impact from beyond the screen edge, a shock ring
    /// and a core flash, on unscaled time so they play during the sim's hitstop freeze.</item>
    /// <item>Speed lines while dashing and while tumbling from a launch (scaled time: hitstop freezes them too).</item>
    /// <item>Cel-shaded dust clouds on grounded dash starts, hard (non-launched) landings and launch take-offs, and a
    /// ground shock ring on strong launched ground bounces (ChibiLaunchFx keeps its own bounce / wall puffs).</item>
    /// </list>
    /// Everything is one sprite pool of <see cref="PoolSize"/> built at match start (slot allocation by
    /// <see cref="ChibiFxRing"/>, oldest recycled when full) plus one overlay sprite per fighter: zero allocation per
    /// frame. Tunables: <see cref="ChibiFeel"/>.
    /// </summary>
    public sealed class ChibiImpactFx
    {
        /// <summary>Pooled sprites (budget: burst 23, dust event ~8, speed lines ~12 live per fighter).</summary>
        public const int PoolSize = 192;
        // Sorting: speed lines behind the fighters (10..45), dust at the feet like ChibiLaunchFx, the burst above the stage
        // foreground (70) and below Fx particles (80).
        const int OrderSpeedLine = 5, OrderDust = 50, OrderGroundRing = 49, OrderBurst = 78, OrderBurstCore = 79;
        const float P = ChibiFxTextures.Peak;
        static readonly Color DustColor = new Color(0.93f, 0.89f, 0.82f);

        sealed class Item
        {
            public Transform Tr;
            public SpriteRenderer Sr;
            public bool Unscaled;
            public float T, Life, A0, FadePow, Drag, Spin, Rot, NatH;
            public Vector2 Pos, Vel, S0, S1;
            public Color Col;
        }

        struct Pending
        {
            public bool Dash, DashGrounded;
            public int DashDir;
            public float Land, Takeoff, Bounce;
            public Vector2 Feet;
        }

        readonly Item[] _items = new Item[PoolSize];
        readonly ChibiFxRing _ring = new ChibiFxRing(PoolSize);
        readonly FighterVisual[] _vis;
        readonly Color[] _fx;
        readonly float _groundY;
        readonly Material _light, _alpha;

        readonly SpriteRenderer[] _flash = new SpriteRenderer[2];
        readonly float[] _flashLeft = new float[2];
        readonly Color[] _flashCol = new Color[2];

        readonly Pending[] _pend = new Pending[2];
        readonly float[] _lineT = new float[2];
        readonly bool[] _dashing = new bool[2], _tumble = new bool[2];
        readonly int[] _dashDir = { 1, 1 };
        readonly Vector2[] _vel = new Vector2[2];

        /// <summary>
        /// Builds the pool under <paramref name="parent"/>. <paramref name="vis"/> = the two fighter visuals,
        /// <paramref name="fx"/> = CharacterRoster Fx colour per fighter, <paramref name="groundY"/> = world Y of sim Y 0.
        /// </summary>
        public ChibiImpactFx(Transform parent, FighterVisual[] vis, Color[] fx, float groundY)
        {
            WarmShared();
            _vis = vis;
            _fx = fx;
            _groundY = groundY;
            _light = ChibiFxTextures.SpriteLight;
            _alpha = Art.SpriteMat;
            var root = new GameObject("ImpactFx").transform;
            root.SetParent(parent, false);
            root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject("Ifx");
                go.transform.SetParent(root, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sharedMaterial = _alpha;
                sr.enabled = false;
                _items[i] = new Item { Tr = go.transform, Sr = sr };
            }
            for (int f = 0; f < 2; f++)
            {
                var go = new GameObject("ImpactFlash" + f);
                go.transform.SetParent(root, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sharedMaterial = _light;
                sr.enabled = false;
                _flash[f] = sr;
            }
        }

        /// <summary>
        /// Builds the shared procedural sprites and materials now (match build, behind the wipe / READY intro) so the
        /// first hit, dash or KO never creates a texture or looks up a shader. Idempotent.
        /// </summary>
        public static void WarmShared()
        {
            ChibiLaunchFx.WarmShared();
            _ = ChibiFxTextures.Line;
            _ = ChibiFxTextures.Ring;
            _ = ChibiFxTextures.Glow;
            _ = ChibiFxTextures.SpriteLight;
        }

        // ================================================================== sim side (per game step)

        /// <summary>
        /// Captures this step's dash starts, landings, take-offs and bounces from (<paramref name="prev"/> shown,
        /// <paramref name="cur"/> now). Read-only on both; call before the view copies cur into its shown state.
        /// </summary>
        public void OnSimStep(ChibiSimState prev, ChibiSimState cur)
        {
            for (int i = 0; i < 2; i++)
            {
                var p = prev.F[i];
                var c = cur.F[i];
                ref var pd = ref _pend[i];
                _vel[i] = new Vector2(c.Vx, c.Vy) * ChibiFeel.SubToUps;
                _dashing[i] = c.DashTicks > 0 && c.InPlay;
                if (c.DashDir != 0) _dashDir[i] = c.DashDir > 0 ? 1 : -1;
                _tumble[i] = ChibiFeel.IsTumble(c.Stun, c.Grounded, c.InPlay, c.LaunchGravP, c.Vx, c.Vy);
                if (!c.InPlay || !p.InPlay) continue;

                var feet = new Vector2(c.X * 0.001f, _groundY + c.Y * 0.001f);
                if (c.DashTicks > 0 && (p.DashTicks == 0 || c.DashTicks > p.DashTicks))
                {
                    pd.Dash = true;
                    pd.DashGrounded = c.Grounded;
                    pd.DashDir = _dashDir[i];
                    pd.Feet = feet;
                }
                if (!p.Grounded && c.Grounded)
                {
                    float fall = Mathf.Abs(p.Vy) * ChibiFeel.SubToUps;
                    if (p.Stun > 0 && fall >= ChibiFeel.BounceRingUps)
                        pd.Bounce = Mathf.Max(pd.Bounce, Mathf.Clamp01((fall - ChibiFeel.BounceRingUps) / (ChibiFeel.LaunchStrongUps - ChibiFeel.BounceRingUps)) * 0.7f + 0.3f);
                    else if (p.Stun == 0 && fall >= ChibiFeel.HardLandUps)
                        pd.Land = Mathf.Max(pd.Land, Mathf.Clamp01((fall - ChibiFeel.HardLandUps) / ChibiFeel.HardLandUps) * 0.6f + 0.4f);
                    pd.Feet = feet;
                }
                else if (p.Grounded && !c.Grounded && c.Stun > 0 && c.PctM > p.PctM)
                {
                    float ups = _vel[i].magnitude;
                    if (ups >= ChibiFeel.LaunchMinUps)
                    {
                        pd.Takeoff = Mathf.Max(pd.Takeoff, ChibiFeel.HitStrength(0, ups));
                        pd.Feet = new Vector2(p.X * 0.001f, _groundY + p.Y * 0.001f);
                    }
                }
            }
        }

        // ================================================================== view side

        /// <summary>
        /// A hit on fighter <paramref name="defender"/>: silhouette flash in <paramref name="flashColor"/> for
        /// <see cref="ChibiFeel.FlashSeconds"/>, and on heavy hits a radial burst at <paramref name="at"/> in
        /// <paramref name="color"/> sized for a camera of ortho size <paramref name="camSize"/>.
        /// </summary>
        public void Hit(int defender, Vector2 at, Color color, Color flashColor, float strength, float camSize)
        {
            if (defender >= 0 && defender < 2)
            {
                _flashLeft[defender] = Mathf.Max(_flashLeft[defender], ChibiFeel.FlashSeconds(strength));
                _flashCol[defender] = flashColor;
            }
            if (strength >= ChibiFeel.HeavyHit) Burst(at, color, strength, camSize);
        }

        /// <summary>Plays pending effects and advances everything: <paramref name="dt"/> scaled, <paramref name="udt"/> unscaled.</summary>
        public void Tick(float dt, float udt)
        {
            for (int i = 0; i < 2; i++)
            {
                ref var pd = ref _pend[i];
                if (pd.Dash) { pd.Dash = false; if (pd.DashGrounded) DashDust(pd.Feet, pd.DashDir); EmitSpeedLines(i, 3); }
                if (pd.Land > 0f) { LandDust(pd.Feet, pd.Land); pd.Land = 0f; }
                if (pd.Takeoff > 0f) { LandDust(pd.Feet, pd.Takeoff); GroundRing(pd.Feet, pd.Takeoff); pd.Takeoff = 0f; }
                if (pd.Bounce > 0f) { GroundRing(pd.Feet, pd.Bounce); pd.Bounce = 0f; }

                bool fast = _tumble[i] && _vel[i].magnitude >= ChibiFeel.LaunchMinUps;
                if ((_dashing[i] || fast) && _vis[i] != null)
                {
                    _lineT[i] -= dt;
                    if (_lineT[i] <= 0f)
                    {
                        _lineT[i] = fast ? ChibiFeel.LaunchLineEvery : ChibiFeel.DashLineEvery;
                        EmitSpeedLines(i, fast ? 2 : 1);
                    }
                }
                else _lineT[i] = 0f;
                UpdateFlash(i, udt);
            }
            for (int k = 0; k < PoolSize; k++)
            {
                if (!_ring.IsLive(k)) continue;
                var it = _items[k];
                if (!Advance(it, it.Unscaled ? udt : dt)) { it.Sr.enabled = false; _ring.Release(k); }
            }
        }

        /// <summary>Hides and frees everything (end of match).</summary>
        public void Clear()
        {
            for (int k = 0; k < PoolSize; k++) if (_items[k] != null && _items[k].Sr != null) _items[k].Sr.enabled = false;
            _ring.Clear();
            for (int f = 0; f < 2; f++)
            {
                if (_flash[f] != null) _flash[f].enabled = false;
                _flashLeft[f] = 0f;
                _pend[f] = default;
                _dashing[f] = _tumble[f] = false;
            }
        }

        // ------------------------------------------------------------------ flash

        void UpdateFlash(int i, float udt)
        {
            var sr = _flash[i];
            if (sr == null) return;
            var body = _vis[i] != null ? _vis[i].BodyRenderer : null;
            bool on = _flashLeft[i] > 0f && body != null && body.enabled && body.sprite != null && body.color.a >= 0.5f;
            if (!on)
            {
                if (sr.enabled) sr.enabled = false;
                _flashLeft[i] = Mathf.Max(0f, _flashLeft[i] - udt);
                return;
            }
            if (sr.sprite != body.sprite) sr.sprite = body.sprite;
            sr.flipX = body.flipX;
            sr.flipY = body.flipY;
            sr.sortingLayerID = body.sortingLayerID;
            sr.sortingOrder = body.sortingOrder + 1;
            var bt = body.transform;
            sr.transform.SetPositionAndRotation(bt.position, bt.rotation);
            sr.transform.localScale = bt.lossyScale;
            var c = _flashCol[i];
            sr.color = new Color(c.r, c.g, c.b, ChibiFeel.FlashAlpha);   // x Peak in the material: a near-white additive silhouette
            if (!sr.enabled) sr.enabled = true;
            _flashLeft[i] -= udt;   // shown this frame, then counted down: a 1-frame flash is always seen once
        }

        // ------------------------------------------------------------------ effects

        void Burst(Vector2 at, Color col, float s, float camSize)
        {
            float inner = Mathf.Max(1.2f, camSize * ChibiFeel.BurstInnerOfSize);
            int n = ChibiFeel.BurstLines;
            var white = Light(Color.white, 2.4f);
            var tint = Light(Color.Lerp(col, Color.white, 0.35f), 2f);
            for (int k = 0; k < n; k++)
            {
                float a = (k + FxRandom.Range(-0.3f, 0.3f)) * (360f / n);
                float r = a * Mathf.Deg2Rad;
                var d = new Vector2(Mathf.Cos(r), Mathf.Sin(r));
                float len = FxRandom.Range(ChibiFeel.BurstLenMin, ChibiFeel.BurstLenMax);
                float th = FxRandom.Range(0.08f, 0.16f) * (0.8f + 0.4f * s);
                var it = Take(ChibiFxTextures.Line, ChibiFxTextures.LineH, OrderBurst, _light, true);
                it.Pos = at + d * (inner + len * 0.5f + FxRandom.Range(0f, 1.2f));
                it.Vel = -d * FxRandom.Range(10f, 18f);
                it.Rot = a + 180f;                                 // bright head points at the impact
                it.S0 = new Vector2(len, th); it.S1 = new Vector2(len * 0.55f, th * 0.3f);
                it.Life = ChibiFeel.BurstSec * FxRandom.Range(0.8f, 1.1f); it.A0 = 1f; it.FadePow = 1.4f;
                it.Col = (k & 1) == 0 ? white : tint;
                Show(it);
            }
            var ring = Take(ChibiFxTextures.Ring, ChibiFxTextures.RoundH, OrderBurst, _light, true);
            ring.Pos = at; ring.S0 = Vector2.one * 0.5f; ring.S1 = Vector2.one * (4f + 3f * s);
            ring.Life = 0.2f; ring.A0 = 1f; ring.FadePow = 1.3f; ring.Col = tint;
            Show(ring);
            var core = Take(ChibiFxTextures.Glow, ChibiFxTextures.RoundH, OrderBurstCore, _light, true);
            core.Pos = at; core.S0 = Vector2.one * 1.6f; core.S1 = Vector2.one * (3f + 1.5f * s);
            core.Life = 0.1f; core.A0 = 1f; core.FadePow = 2f; core.Col = white;
            Show(core);
        }

        void EmitSpeedLines(int i, int count)
        {
            var v = _vis[i];
            if (v == null) return;
            Vector2 pos = v.transform.position;
            Vector2 vel = _vel[i];
            float speed = vel.magnitude;
            Vector2 dir = speed > 0.5f ? vel / speed : new Vector2(_dashDir[i], 0f);
            float s = Mathf.Clamp01((speed - ChibiFeel.LaunchMinUps) / (ChibiFeel.LaunchStrongUps - ChibiFeel.LaunchMinUps));
            var perp = new Vector2(-dir.y, dir.x);
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            var col = Light(Color.Lerp(Color.white, _fx[i], 0.2f), 1.6f);
            for (int k = 0; k < count; k++)
            {
                float len = Mathf.Lerp(0.9f, 2.4f, s) * FxRandom.Range(0.8f, 1.2f);
                var it = Take(ChibiFxTextures.Line, ChibiFxTextures.LineH, OrderSpeedLine, _light, false);
                it.Pos = pos - dir * (FxRandom.Range(0.3f, 1.0f) + len * 0.5f) + perp * FxRandom.Range(-0.6f, 0.6f);
                it.Vel = dir * speed * 0.15f;
                it.Rot = ang;                                     // head toward the travel direction
                float th = FxRandom.Range(0.05f, 0.09f);
                it.S0 = new Vector2(len, th); it.S1 = new Vector2(len * 1.3f, th * 0.4f);
                it.Life = ChibiFeel.SpeedLineLife * FxRandom.Range(0.8f, 1.2f); it.A0 = ChibiFeel.SpeedLineAlpha; it.FadePow = 1.2f;
                it.Col = col;
                Show(it);
            }
        }

        void DashDust(Vector2 feet, int dir)
        {
            for (int k = 0; k < 4; k++)
            {
                var p = Puff(feet + new Vector2(-dir * FxRandom.Range(0.1f, 0.45f), 0.12f), FxRandom.Range(0.28f, 0.42f));
                p.Vel = new Vector2(-dir * FxRandom.Range(1.5f, 4f), FxRandom.Range(0.2f, 1f));
                Show(p);
            }
        }

        void LandDust(Vector2 feet, float s)
        {
            int n = 4 + Mathf.RoundToInt(4f * s);
            for (int k = 0; k < n; k++)
            {
                float side = (k & 1) == 0 ? 1f : -1f;
                var p = Puff(feet + new Vector2(side * FxRandom.Range(0.05f, 0.35f), 0.1f), Mathf.Lerp(0.3f, 0.55f, s) * FxRandom.Range(0.85f, 1.15f));
                p.Vel = new Vector2(side * FxRandom.Range(1.5f, 3f + 3f * s), FxRandom.Range(0.2f, 1.1f));
                Show(p);
            }
        }

        void GroundRing(Vector2 feet, float s)
        {
            var r = Take(ChibiFxTextures.Ring, ChibiFxTextures.RoundH, OrderGroundRing, _light, false);
            r.Pos = feet + new Vector2(0f, 0.05f);
            r.S0 = new Vector2(0.5f, 0.12f); r.S1 = new Vector2(2.5f + 2.5f * s, 0.5f + 0.4f * s);
            r.Life = 0.3f; r.A0 = 0.9f; r.FadePow = 1.4f; r.Col = Light(DustColor, 1.6f);
            Show(r);
        }

        Item Puff(Vector2 at, float size)
        {
            var p = Take(ChibiLaunchFx.PuffSprite, 1f, OrderDust, _alpha, false);
            p.Pos = at; p.Drag = 4f;
            p.S0 = new Vector2(size, size); p.S1 = p.S0 * 1.9f;
            p.Life = FxRandom.Range(0.35f, 0.6f); p.A0 = 0.9f; p.FadePow = 1.3f; p.Col = DustColor;
            p.Rot = FxRandom.Range(0f, 360f); p.Spin = FxRandom.Range(-120f, 120f);
            return p;
        }

        /// <summary>Colour at <paramref name="k"/> x on screen for the Peak-tinted light material.</summary>
        static Color Light(Color c, float k) => new Color(Mathf.Clamp01(c.r * k / P), Mathf.Clamp01(c.g * k / P), Mathf.Clamp01(c.b * k / P), 1f);

        // ------------------------------------------------------------------ pool

        Item Take(Sprite s, float natH, int order, Material mat, bool unscaled)
        {
            int idx = _ring.Take(out _);
            var it = _items[idx];
            it.Unscaled = unscaled;
            it.T = 0f; it.Life = 0.3f; it.A0 = 1f; it.FadePow = 1f; it.Drag = 0f; it.Spin = 0f; it.Rot = 0f;
            it.NatH = natH > 0f ? natH : 1f;
            it.Pos = it.Vel = Vector2.zero;
            it.S0 = it.S1 = Vector2.one;
            it.Col = Color.white;
            if (it.Sr.sprite != s) it.Sr.sprite = s;
            it.Sr.sortingOrder = order;
            if (it.Sr.sharedMaterial != mat) it.Sr.sharedMaterial = mat;
            return it;
        }

        /// <summary>Places a freshly configured item at its start state and shows it.</summary>
        void Show(Item it)
        {
            Advance(it, 0f);
            it.Sr.enabled = true;
        }

        /// <summary>Advances one item by <paramref name="dt"/>; false once its life is over.</summary>
        static bool Advance(Item it, float dt)
        {
            it.T += dt;
            float u = it.T / it.Life;
            if (u >= 1f) return false;
            if (it.Drag > 0f) it.Vel *= 1f / (1f + it.Drag * dt);
            it.Pos += it.Vel * dt;
            it.Rot += it.Spin * dt;
            float e = 1f - (1f - u) * (1f - u);
            var sc = Vector2.LerpUnclamped(it.S0, it.S1, e);
            it.Tr.SetPositionAndRotation(new Vector3(it.Pos.x, it.Pos.y, 0f), Quaternion.Euler(0f, 0f, it.Rot));
            it.Tr.localScale = new Vector3(Mathf.Max(0.0001f, sc.x), Mathf.Max(0.0001f, sc.y / it.NatH), 1f);
            var c = it.Col;
            c.a *= it.A0 * Mathf.Pow(1f - u, it.FadePow);
            it.Sr.color = c;
            return true;
        }
    }
}
