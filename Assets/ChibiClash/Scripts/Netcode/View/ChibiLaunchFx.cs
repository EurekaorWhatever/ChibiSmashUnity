using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Camera framing limits derived from a stage's KO lines and solid edges (pure math, presentation only). The view
    /// never shows far past an active blast line (<see cref="KoOvershoot"/>), shows a little past a solid wall so its edge
    /// reads (Mountain), and keeps the floor / sky cap of the no-KO edges in frame. Read from
    /// <see cref="ChibiStageTable"/> at call time, so a moved KO line (GL-3) needs no view change.
    /// </summary>
    public static class ChibiCameraFraming
    {
        /// <summary>World units the view may extend past an ACTIVE KO line (room for the KO blast origin).</summary>
        public const float KoOvershoot = 1.0f;
        /// <summary>World units the view may extend past a solid side wall plane (the wall edge stays visible).</summary>
        public const float WallMargin = 0.6f;
        /// <summary>World units shown below a solid floor (Plain / Bird Lair): keeps the ground in the lower part of the frame.</summary>
        public const float FloorMargin = 2.0f;
        /// <summary>World units shown above the sky cap of a no-top stage (a capped fighter's body stays in frame).</summary>
        public const float SkyCapMargin = 2.0f;

        /// <summary>
        /// World-space rectangle the camera view must stay inside for stage <paramref name="st"/> drawn with sim Y 0 at
        /// <paramref name="groundY"/>. Edges: KO sides = BlastX + KoOvershoot, walls = BlastX + WallMargin, KO top =
        /// CeilingY + KoOvershoot, sky cap = CeilingY + SkyCapMargin, KO bottom = KillY - KoOvershoot, solid floor =
        /// KillY - FloorMargin.
        /// </summary>
        /// <example><code>Rect r = ChibiCameraFraming.Bounds(ChibiStageTable.Get(3), 0f); // Mountain: x -16.6..16.6</code></example>
        public static Rect Bounds(ChibiStageDef st, float groundY)
        {
            float bx = st.BlastX * 0.001f + (st.KoSides ? KoOvershoot : WallMargin);
            float top = groundY + st.CeilingY * 0.001f + (st.KoTop ? KoOvershoot : SkyCapMargin);
            float bottom = groundY + st.KillY * 0.001f - (st.KoBottom ? KoOvershoot : FloorMargin);
            return Rect.MinMaxRect(-bx, bottom, bx, top);
        }

        /// <summary>Largest orthographic size whose view fits in <paramref name="r"/>: min(cap, halfH, halfW / aspect).</summary>
        public static float MaxSize(Rect r, float aspect, float cap)
        {
            aspect = Mathf.Max(0.1f, aspect);
            return Mathf.Max(0.5f, Mathf.Min(cap, Mathf.Min(r.height * 0.5f, r.width * 0.5f / aspect)));
        }

        /// <summary>
        /// Clamps a view centre so a view of orthographic <paramref name="size"/> stays inside <paramref name="r"/>; an
        /// axis on which the view is larger than the rect is centred on it.
        /// </summary>
        public static Vector2 ClampCentre(Vector2 c, float size, float aspect, Rect r)
        {
            float hw = size * Mathf.Max(0.1f, aspect), hh = size;
            c.x = r.width > 2f * hw ? Mathf.Clamp(c.x, r.xMin + hw, r.xMax - hw) : r.center.x;
            c.y = r.height > 2f * hh ? Mathf.Clamp(c.y, r.yMin + hh, r.yMax - hh) : r.center.y;
            return c;
        }
    }

    /// <summary>
    /// Smash-style launch / KO / respawn presentation for the netcode view (percent-knockback.md 3.4; stages README 3.1).
    /// Presentation only: <see cref="OnSimStep"/> reads two sim states (shown before, now) and never writes them; all
    /// drawing happens in <see cref="Tick"/>. Allocation-free after construction: one sprite pool plus five hover sprites
    /// per fighter, procedural textures built once, particles through the shared <see cref="Fx"/> system.
    /// <list type="bullet">
    /// <item>Launch trail: a hit whose launch speed reaches <c>TrailMinUps</c> starts a trail (bright core streak + colour
    /// glow + cel-shaded ink smoke puffs) that lasts while hitstun runs and the speed stays above <c>TrailKeepFrac</c> of
    /// the launch speed; stronger launches give longer, denser, bigger trails; percent shifts it from white-blue to red.
    /// Landing or hitting a wall while launched kicks ink dust puffs.</item>
    /// <item>KO blast: Stocks decreased or OutTicks started = KO on an active edge; a beam + ray fan + ink speed lines +
    /// star flash + shock rings + smoke from the crossing point (clamped onto the edge) back into the arena, opposite
    /// the launch direction; the view adds a screen flash and shake (<see cref="TakeShake"/>, <see cref="TakeFlash"/>).
    /// Solid edges never KO, so nothing fires there.</item>
    /// <item>Respawn: a light pillar on arrival, a glowing ink-rimmed disc under the fighter while HoverTicks &gt; 0 in the
    /// character's Fx colour, then a body blink while the respawn invulnerability runs (<see cref="BodyAlpha"/>).</item>
    /// </list>
    /// Pool budget: 224 sprites (worst case two strong trails + one KO blast ~170 live), recycled oldest-first when full.
    /// </summary>
    public sealed class ChibiLaunchFx
    {
        // ---- tuning (world units, seconds)
        const float SubToUps = ChibiSimConst.TickRate / (1000f * ChibiSimConst.VelSub);   // sim sub/tick -> world u/s
        const float TrailMinUps = 7f;          // launch speed that starts a trail (~KB 70, Kaia fsmash at ~10 %)
        const float TrailStrongUps = 18f;      // launch speed of a full-strength trail (~KB 175, KO territory)
        const float TrailKeepFrac = 0.35f;     // trail ends once the speed falls below this fraction of the launch speed
        const float PctWarm = 50f, PctHot = 150f;   // percent ramp of the trail colour (white-blue -> red)
        const float ImpactMinUps = 4f;         // landing / wall speed that kicks dust while launched
        const float KoShakeMin = 0.6f, KoShakeMax = 1.0f, KoFlash = 0.6f;
        const float KoFocusSec = 0.7f;         // unscaled seconds the camera keeps the KO point in frame
        const float BlinkHz = 10f, BlinkLow = 0.3f;
        const int PoolSize = 224, MaxEmitPerFrame = 6;

        // Sorting orders: fighters use 10..45, zones 7..8, stage foreground 70, Fx particles 80.
        const int OrderTrail = 5, OrderTrailCore = 6, OrderHoverBack = 6, OrderDust = 50, OrderHoverInk = 47,
                  OrderHoverDisc = 48, OrderHoverRing = 49, OrderKoSmoke = 72, OrderKo = 74, OrderKoCore = 76;

        static readonly Color TrailCool = new Color(0.78f, 0.9f, 1f), TrailHot = new Color(1f, 0.22f, 0.16f);
        static readonly Color SmokeCool = new Color(0.96f, 0.96f, 0.98f), SmokeHot = new Color(1f, 0.55f, 0.48f);
        static readonly Color CoreCool = Color.white, CoreHot = new Color(1f, 0.86f, 0.78f);
        static readonly Color DustColor = new Color(0.93f, 0.89f, 0.82f);
        // HDR intensities (colour x k): ChibiPostFx blooms above 0.82, so cores, rays and the KO beam glow; smoke and ink stay < 1.
        const float HdrTrailCoreMin = 1.6f, HdrTrailCoreMax = 2.4f, HdrTrailGlow = 1.25f, HdrKoCore = 2.5f, HdrKoGlow = 1.7f,
                    HdrKoRay = 2.2f, HdrArrive = 2f, HdrHoverDisc = 1.3f;
        /// <summary>Optional additive sprite shader (drop-in: a shader with this name makes glows / cores additive).</summary>
        const string AdditiveShader = "ChibiClash/SpriteAdditive";

        sealed class Item
        {
            public Transform Tr;
            public SpriteRenderer Sr;
            public bool Live, Shown, Anchored;
            public float T, Delay, Life, A0, FadePow, Drag, Grow, Spin, Rot;
            public Vector2 Pos, Vel, S0, S1, Dir;
            public Color Col;
        }

        struct Trail
        {
            public bool On, Reset;
            public float S, Speed0, Hot, Dist;
            public Vector2 LastPos;
        }

        struct PendingKo { public bool On; public Vector2 Pos, Dir; public float S; }
        struct PendingImpact { public bool On; public Vector2 Pos, Normal; public float S; }

        readonly Item[] _items = new Item[PoolSize];
        int _next;
        readonly FighterVisual[] _vis;
        readonly Color[] _fx;
        readonly float _groundY, _feetToPivot;

        readonly Trail[] _trail = new Trail[2];
        readonly Vector2[] _vel = new Vector2[2];             // world u/s of each fighter at the last step
        readonly bool[] _out = new bool[2], _hover = new bool[2], _blink = new bool[2];
        readonly PendingKo[] _ko = new PendingKo[2];
        readonly PendingImpact[] _impact = new PendingImpact[2];
        readonly bool[] _arrive = new bool[2];
        readonly Vector2[] _arrivePos = new Vector2[2];

        // Hover platform per fighter: column + back glow, ink rim, disc, pulse ring.
        readonly SpriteRenderer[,] _hov = new SpriteRenderer[2, 5];
        readonly float[] _hoverT = new float[2], _hoverEndT = new float[2], _sparkT = new float[2];
        const int HovColumn = 0, HovGlow = 1, HovInk = 2, HovDisc = 3, HovRing = 4;

        float _shakeReq, _flashReq;
        Color _flashColor;
        Vector2 _koFocus;
        float _koFocusUntil = -1f;

        static Sprite _streak, _puff, _star;
        static Material _addMat;
        static bool _addTried;

        /// <summary>
        /// Material for the light layers: the additive shader when the project has one, else the standard sprite material
        /// (alpha blend; HDR colours above 1 still push the cores past the bloom threshold).
        /// </summary>
        static Material LightMat
        {
            get
            {
                if (_addTried) return _addMat != null ? _addMat : Art.SpriteMat;
                _addTried = true;
                var sh = Shader.Find(AdditiveShader);
                if (sh != null) _addMat = new Material(sh) { name = "ChibiLaunchAdditive", hideFlags = HideFlags.DontSave };
                return _addMat != null ? _addMat : Art.SpriteMat;
            }
        }

        static Color Hdr(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

        /// <summary>
        /// Builds the pool under <paramref name="parent"/>. <paramref name="fx"/> = CharacterRoster Fx colour per fighter;
        /// <paramref name="groundY"/> = world Y of sim Y 0; <paramref name="feetToPivot"/> = feet to body-centre offset.
        /// </summary>
        public ChibiLaunchFx(Transform parent, FighterVisual[] vis, Color[] fx, float groundY, float feetToPivot)
        {
            _vis = vis;
            _fx = fx;
            _groundY = groundY;
            _feetToPivot = feetToPivot;
            var root = new GameObject("LaunchFx").transform;
            root.SetParent(parent, false);
            for (int i = 0; i < PoolSize; i++)
            {
                var sr = Art.Sprite(root, "Lfx", Art.Soft, Color.clear, Vector2.zero, Vector2.one, OrderTrail);
                sr.enabled = false;
                _items[i] = new Item { Tr = sr.transform, Sr = sr };
            }
            for (int f = 0; f < 2; f++)
            {
                _hov[f, HovColumn] = Art.Sprite(root, "HoverColumn", Art.Soft, Color.clear, Vector2.zero, Vector2.one, OrderHoverBack);
                _hov[f, HovGlow] = Art.Sprite(root, "HoverGlow", Art.Soft, Color.clear, Vector2.zero, Vector2.one, OrderHoverBack);
                _hov[f, HovInk] = Art.Sprite(root, "HoverInk", Art.Circle, Color.clear, Vector2.zero, Vector2.one, OrderHoverInk);
                _hov[f, HovDisc] = Art.Sprite(root, "HoverDisc", Art.Circle, Color.clear, Vector2.zero, Vector2.one, OrderHoverDisc);
                _hov[f, HovRing] = Art.Sprite(root, "HoverRing", Art.Ring, Color.clear, Vector2.zero, Vector2.one, OrderHoverRing);
                for (int k = 0; k < 5; k++) _hov[f, k].enabled = false;
                _hov[f, HovColumn].sharedMaterial = LightMat;
                _hov[f, HovGlow].sharedMaterial = LightMat;
                _hov[f, HovRing].sharedMaterial = LightMat;
                _hoverEndT[f] = 99f;
            }
        }

        // ================================================================== sim side (per game step)

        /// <summary>
        /// Captures this game step's launches, impacts, KOs and respawn changes from (<paramref name="prev"/> shown,
        /// <paramref name="cur"/> now). Read-only on both states; call before the view copies cur into its shown state.
        /// </summary>
        public void OnSimStep(ChibiSimState prev, ChibiSimState cur)
        {
            var st = ChibiStageTable.Get(cur.Stage);   // read every step: KO lines may move (GL-3)
            for (int i = 0; i < 2; i++)
            {
                var p = prev.F[i];
                var c = cur.F[i];
                _vel[i] = new Vector2(c.Vx, c.Vy) * SubToUps;
                ref var tr = ref _trail[i];

                // ---- KO: a stock was lost, or the fighter just went Out (training keeps its stocks).
                bool ko = c.Stocks < p.Stocks || (p.OutTicks == 0 && c.OutTicks > 0);
                if (ko) CaptureKo(i, st, p, c);
                _out[i] = c.OutTicks > 0;

                // ---- respawn: arrival pillar, hover disc, blink once the hover ends.
                if (p.HoverTicks == 0 && c.HoverTicks > 0)
                {
                    _arrive[i] = true;
                    _arrivePos[i] = new Vector2(c.X * 0.001f, _groundY + c.Y * 0.001f);
                    _hoverT[i] = 0f;
                }
                if (p.HoverTicks > 0 && c.HoverTicks == 0 && c.OutTicks == 0)
                {
                    _hoverEndT[i] = 0f;
                    _blink[i] = c.Invuln > 0;
                }
                _hover[i] = c.HoverTicks > 0;
                if (c.Invuln <= 0 || c.OutTicks > 0 || c.HoverTicks > 0) _blink[i] = false;

                // ---- launch trail.
                if (c.OutTicks > 0 || c.HoverTicks > 0) { tr.On = false; continue; }
                bool wasLaunched = tr.On || p.Stun > 0;
                if (c.Stun > 0 && c.PctM > p.PctM)
                {
                    float ups = _vel[i].magnitude;
                    if (ups >= TrailMinUps)
                    {
                        tr.On = true;
                        tr.Reset = true;
                        tr.Speed0 = ups;
                        tr.S = Mathf.Clamp01((ups - TrailMinUps) / (TrailStrongUps - TrailMinUps));
                        tr.Hot = Mathf.Clamp01((c.PctM * 0.001f - PctWarm) / (PctHot - PctWarm));
                    }
                    else tr.On = false;
                }
                else if (tr.On && (c.Stun == 0 || c.Grounded || _vel[i].magnitude < tr.Speed0 * TrailKeepFrac))
                    tr.On = false;

                // ---- impacts while launched: landing, or stopping on a solid side wall.
                if (!wasLaunched) continue;
                if (!p.Grounded && c.Grounded)
                {
                    float ups = Mathf.Abs(p.Vy) * SubToUps;
                    if (ups >= ImpactMinUps)
                        _impact[i] = new PendingImpact
                        {
                            On = true, Pos = new Vector2(c.X * 0.001f, _groundY + c.Y * 0.001f), Normal = Vector2.up,
                            S = Mathf.Clamp01((ups - ImpactMinUps) / (TrailStrongUps - ImpactMinUps)),
                        };
                }
                else if (!st.KoSides && c.Vx == 0 && System.Math.Abs(c.X) >= st.WallClampX)
                {
                    float ups = Mathf.Abs(p.Vx) * SubToUps;
                    float side = c.X > 0 ? 1f : -1f;
                    if (ups >= ImpactMinUps)
                        _impact[i] = new PendingImpact
                        {
                            On = true, Pos = new Vector2(side * st.BlastX * 0.001f, _groundY + c.Y * 0.001f + _feetToPivot),
                            Normal = new Vector2(-side, 0f),
                            S = Mathf.Clamp01((ups - ImpactMinUps) / (TrailStrongUps - ImpactMinUps)),
                        };
                }
            }
        }

        /// <summary>
        /// KO point = the crossing point clamped onto the crossed active edge; beam direction = opposite of the launch
        /// (the last in-play velocity), bent to point into the arena.
        /// </summary>
        void CaptureKo(int i, ChibiStageDef st, ChibiFighterSim p, ChibiFighterSim c)
        {
            _trail[i].On = false;
            Vector2 v = new Vector2(p.Vx, p.Vy) * SubToUps;
            int edge = CrossedEdge(st, c.X, c.Y);
            if (edge == 0) edge = EdgeByDirection(st, v);
            if (edge == 0) return;   // no active edge at all: nothing to blast from

            float bx = st.BlastX * 0.001f, kill = _groundY + st.KillY * 0.001f, ceil = _groundY + st.CeilingY * 0.001f;
            float cx = c.X * 0.001f, cy = _groundY + c.Y * 0.001f + _feetToPivot;
            Vector2 pos, inward;
            switch (edge)
            {
                case 1: pos = new Vector2(bx, Mathf.Clamp(cy, kill, ceil)); inward = Vector2.left; break;
                case 2: pos = new Vector2(-bx, Mathf.Clamp(cy, kill, ceil)); inward = Vector2.right; break;
                case 3: pos = new Vector2(Mathf.Clamp(cx, -bx, bx), ceil); inward = Vector2.down; break;
                default: pos = new Vector2(Mathf.Clamp(cx, -bx, bx), kill); inward = Vector2.up; break;
            }
            float speed = v.magnitude;
            Vector2 dir = speed > 0.01f ? -v / speed : inward;
            float d = Vector2.Dot(dir, inward);
            if (d < 0.5f) dir = (dir + inward * ((0.5f - d) * 2f)).normalized;
            float s = Mathf.Max(Mathf.Clamp01((speed - TrailMinUps) / (TrailStrongUps - TrailMinUps)),
                                0.5f * Mathf.Clamp01((p.PctM * 0.001f - PctWarm) / (PctHot - PctWarm)));
            _ko[i] = new PendingKo { On = true, Pos = pos, Dir = dir, S = s };
        }

        /// <summary>Active edge the feet point is past (largest overshoot): 1 right, 2 left, 3 top, 4 bottom, 0 none.</summary>
        static int CrossedEdge(ChibiStageDef st, int x, int y)
        {
            int best = 0;
            long over = 0;
            if (st.KoSides && x > st.BlastX && (long)x - st.BlastX > over) { best = 1; over = (long)x - st.BlastX; }
            if (st.KoSides && x < -st.BlastX && (long)-st.BlastX - x > over) { best = 2; over = (long)-st.BlastX - x; }
            if (st.KoTop && y > st.CeilingY && (long)y - st.CeilingY > over) { best = 3; over = (long)y - st.CeilingY; }
            if (st.KoBottom && y < st.KillY && (long)st.KillY - y > over) { best = 4; }
            return best;
        }

        /// <summary>Fallback when the stored position is not past a line: the active edge the travel points at most.</summary>
        static int EdgeByDirection(ChibiStageDef st, Vector2 v)
        {
            int best = 0;
            float score = float.NegativeInfinity;
            if (st.KoSides && v.x > score) { best = 1; score = v.x; }
            if (st.KoSides && -v.x > score) { best = 2; score = -v.x; }
            if (st.KoTop && v.y > score) { best = 3; score = v.y; }
            if (st.KoBottom && -v.y > score) { best = 4; }
            return best;
        }

        // ================================================================== view side (per rendered frame)

        /// <summary>Body opacity for fighter <paramref name="i"/>: 0 while KO'd (off-screen), blinking during respawn invulnerability.</summary>
        public float BodyAlpha(int i)
        {
            if (_out[i]) return 0f;
            if (_blink[i]) return Mathf.Repeat(Time.time * BlinkHz, 1f) < 0.5f ? 1f : BlinkLow;
            return 1f;
        }

        /// <summary>True while fighter <paramref name="i"/> is KO'd and waiting off-screen (camera ignores it).</summary>
        public bool IsOut(int i) => _out[i];

        /// <summary>
        /// Launch the camera should lean into: true while fighter <paramref name="i"/> has a trail; <paramref name="vel"/>
        /// is its world velocity (u/s) at the last step, <paramref name="strength"/> 0..1.
        /// </summary>
        public bool LaunchLead(int i, out Vector2 vel, out float strength)
        {
            vel = _vel[i];
            strength = _trail[i].S;
            return _trail[i].On;
        }

        /// <summary>KO point the camera keeps in frame for <c>KoFocusSec</c> unscaled seconds after a KO blast.</summary>
        public bool KoFocus(out Vector2 point)
        {
            point = _koFocus;
            return Time.unscaledTime < _koFocusUntil;
        }

        /// <summary>Camera shake requested since the last call (0 = none); the view max-merges it into its shake.</summary>
        public float TakeShake()
        {
            float s = _shakeReq;
            _shakeReq = 0f;
            return s;
        }

        /// <summary>Screen flash requested since the last call (alpha, colour); false = none.</summary>
        public bool TakeFlash(out Color color, out float alpha)
        {
            color = _flashColor;
            alpha = _flashReq;
            _flashReq = 0f;
            return alpha > 0f;
        }

        /// <summary>Plays pending effects and advances everything; <paramref name="dt"/> = scaled frame time (hitstop slows it).</summary>
        public void Tick(float dt)
        {
            for (int i = 0; i < 2; i++)
            {
                if (_ko[i].On) { _ko[i].On = false; PlayKo(i, _ko[i]); }
                if (_impact[i].On) { _impact[i].On = false; PlayImpact(_impact[i]); }
                if (_arrive[i]) { _arrive[i] = false; PlayArrive(i, _arrivePos[i]); }
                EmitTrail(i);
                UpdateHover(i, dt);
            }
            UpdateItems(dt);
        }

        /// <summary>Hides and frees everything (end of match).</summary>
        public void Clear()
        {
            for (int k = 0; k < PoolSize; k++) Release(_items[k]);
            for (int f = 0; f < 2; f++)
            {
                for (int k = 0; k < 5; k++) if (_hov[f, k] != null) _hov[f, k].enabled = false;
                _trail[f] = default;
                _ko[f] = default;
                _impact[f] = default;
                _arrive[f] = false;
                _out[f] = _hover[f] = _blink[f] = false;
            }
            _shakeReq = _flashReq = 0f;
        }

        // ------------------------------------------------------------------ launch trail

        void EmitTrail(int i)
        {
            ref var tr = ref _trail[i];
            if (!tr.On || _vis[i] == null) return;
            Vector2 pos = _vis[i].transform.position;
            if (tr.Reset) { tr.Reset = false; tr.LastPos = pos; tr.Dist = 0f; }
            Vector2 delta = pos - tr.LastPos;
            float len = delta.magnitude;
            if (len > 4f) { tr.LastPos = pos; return; }   // snap (respawn / teleport): no streak across the screen
            Vector2 travel = len > 1e-4f ? delta / len : (_vel[i].sqrMagnitude > 1e-4f ? _vel[i].normalized : Vector2.up);
            float step = Mathf.Lerp(0.42f, 0.24f, tr.S);
            tr.Dist += len;
            int n = 0;
            while (tr.Dist >= step && n < MaxEmitPerFrame)
            {
                tr.Dist -= step;
                Vector2 at = pos - travel * tr.Dist;   // spread evenly along this frame's path
                EmitTrailStep(i, at, travel, tr.S, tr.Hot);
                n++;
            }
            if (n == MaxEmitPerFrame) tr.Dist = 0f;
            tr.LastPos = pos;
        }

        void EmitTrailStep(int i, Vector2 at, Vector2 travel, float s, float hot)
        {
            Color glowCol = Color.Lerp(Color.Lerp(TrailCool, _fx[i], 0.25f), TrailHot, hot);
            Color coreCol = Color.Lerp(CoreCool, CoreHot, hot);
            Color smoke = Color.Lerp(SmokeCool, SmokeHot, hot);
            Vector2 back = -travel;
            float lifeMul = 1f + 0.35f * hot;

            // Colour glow streak, then the bright core on top: thick at the fighter, tapering back along the path.
            var g = Take(Streak, OrderTrail, true);
            g.Anchored = true; g.Pos = at; g.Dir = back; g.Grow = 0.05f;
            g.S0 = new Vector2(0f, Mathf.Lerp(0.55f, 0.95f, s)); g.S1 = new Vector2(Mathf.Lerp(1.4f, 2.6f, s), 0.12f);
            g.Life = Mathf.Lerp(0.22f, 0.36f, s) * lifeMul; g.A0 = Mathf.Lerp(0.45f, 0.7f, s); g.FadePow = 1.3f; g.Col = Hdr(glowCol, HdrTrailGlow);
            SetRot(g, back);
            if (s > 0.15f || hot > 0.3f)
            {
                var c = Take(Streak, OrderTrailCore, true);
                c.Anchored = true; c.Pos = at; c.Dir = back; c.Grow = 0.04f;
                c.S0 = new Vector2(0f, Mathf.Lerp(0.16f, 0.3f, s)); c.S1 = new Vector2(Mathf.Lerp(1.1f, 2.1f, s), 0.03f);
                c.Life = Mathf.Lerp(0.16f, 0.26f, s) * lifeMul; c.A0 = 0.95f; c.FadePow = 1.6f;
                c.Col = Hdr(coreCol, Mathf.Lerp(HdrTrailCoreMin, HdrTrailCoreMax, Mathf.Max(s, hot)));
                SetRot(c, back);
            }

            // Cel-shaded ink smoke puff, drifting back and slightly up, growing as it fades.
            var p = Take(Puff, OrderTrail);
            Vector2 side = new Vector2(-travel.y, travel.x) * FxRandom.Range(-0.18f, 0.18f);
            p.Pos = at + side; p.Vel = back * FxRandom.Range(0.4f, 1.0f) + Vector2.up * 0.35f; p.Drag = 2f;
            float sz = Mathf.Lerp(0.32f, 0.55f, s) * FxRandom.Range(0.85f, 1.15f);
            p.S0 = new Vector2(sz, sz); p.S1 = p.S0 * Mathf.Lerp(1.7f, 2.4f, s);
            p.Life = Mathf.Lerp(0.38f, 0.8f, s) * lifeMul * FxRandom.Range(0.85f, 1.15f); p.A0 = Mathf.Lerp(0.6f, 0.9f, s); p.FadePow = 1.2f;
            p.Col = smoke; p.Rot = FxRandom.Range(0f, 360f); p.Spin = FxRandom.Range(-90f, 90f);

            if (hot > 0.5f && FxRandom.Value < hot) Fx.Burst(at, Hdr(TrailHot, 1.8f), 1, 1.6f, 0.13f, 0.35f);   // embers at red percent
        }

        // ------------------------------------------------------------------ impacts

        void PlayImpact(in PendingImpact im)
        {
            Vector2 tan = new Vector2(im.Normal.y, -im.Normal.x);
            int n = 4 + Mathf.RoundToInt(im.S * 4f);
            for (int k = 0; k < n; k++)
            {
                float side = (k & 1) == 0 ? 1f : -1f;
                var p = Take(Puff, OrderDust);
                p.Pos = im.Pos + im.Normal * 0.15f + tan * side * FxRandom.Range(0f, 0.3f);
                p.Vel = tan * side * FxRandom.Range(1.5f, 3.5f + 3f * im.S) + im.Normal * FxRandom.Range(0.3f, 1.2f);
                p.Drag = 4f;
                float sz = Mathf.Lerp(0.3f, 0.5f, im.S) * FxRandom.Range(0.8f, 1.2f);
                p.S0 = new Vector2(sz, sz); p.S1 = p.S0 * 1.9f;
                p.Life = FxRandom.Range(0.35f, 0.6f); p.A0 = 0.9f; p.FadePow = 1.3f; p.Col = DustColor;
                p.Rot = FxRandom.Range(0f, 360f); p.Spin = FxRandom.Range(-120f, 120f);
            }
            Fx.Burst(im.Pos, DustColor, 6 + Mathf.RoundToInt(im.S * 8f), 3f + 3f * im.S, 0.18f);
            _shakeReq = Mathf.Max(_shakeReq, 0.12f + 0.2f * im.S);
        }

        // ------------------------------------------------------------------ KO blast

        void PlayKo(int i, in PendingKo ko)
        {
            Color col = _fx[i];
            Color bright = Color.Lerp(col, Color.white, 0.55f);
            Color ink = new Color(col.r * 0.12f, col.g * 0.1f, col.b * 0.16f, 1f);
            float s = ko.S;
            Vector2 P = ko.Pos, D = ko.Dir;

            // Main beam: wide colour glow with a white core, shooting from the edge into the arena.
            var glow = Take(Streak, OrderKo, true);
            glow.Anchored = true; glow.Pos = P; glow.Dir = D; glow.Grow = 0.09f;
            glow.S0 = new Vector2(0f, Mathf.Lerp(2.4f, 3.4f, s)); glow.S1 = new Vector2(Mathf.Lerp(10f, 16f, s), 0.25f);
            glow.Life = 0.85f; glow.A0 = 0.85f; glow.FadePow = 1.4f; glow.Col = Hdr(col, HdrKoGlow);
            SetRot(glow, D);
            var core = Take(Streak, OrderKoCore, true);
            core.Anchored = true; core.Pos = P; core.Dir = D; core.Grow = 0.07f;
            core.S0 = new Vector2(0f, Mathf.Lerp(0.9f, 1.4f, s)); core.S1 = new Vector2(Mathf.Lerp(8.5f, 13f, s), 0.05f);
            core.Life = 0.6f; core.A0 = 1f; core.FadePow = 1.6f; core.Col = Hdr(Color.white, HdrKoCore);
            SetRot(core, D);

            // Ray fan: alternating white and colour rays.
            float baseAng = Mathf.Atan2(D.y, D.x) * Mathf.Rad2Deg;
            for (int k = 0; k < 10; k++)
            {
                float a = baseAng + FxRandom.Range(-50f, 50f);
                Vector2 rd = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad));
                var r = Take(Streak, (k & 1) == 0 ? OrderKoCore : OrderKo, true);
                r.Anchored = true; r.Pos = P; r.Dir = rd; r.Grow = 0.08f; r.Delay = FxRandom.Range(0f, 0.06f);
                float w = FxRandom.Range(0.15f, 0.38f) * (1f + 0.4f * s);
                r.S0 = new Vector2(0f, w); r.S1 = new Vector2(FxRandom.Range(4f, 10f) * (0.8f + 0.4f * s), 0.02f);
                r.Life = FxRandom.Range(0.4f, 0.6f); r.A0 = 0.95f; r.FadePow = 1.5f; r.Col = Hdr((k & 1) == 0 ? Color.white : bright, HdrKoRay);
                SetRot(r, rd);
            }

            // Manga ink speed lines, starting a little inside the arena.
            for (int k = 0; k < 7; k++)
            {
                float a = baseAng + FxRandom.Range(-70f, 70f);
                Vector2 rd = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad));
                var l = Take(Streak, OrderKo - 1);
                l.Anchored = true; l.Pos = P + rd * FxRandom.Range(1.2f, 2.2f); l.Dir = rd; l.Grow = 0.06f;
                l.Delay = FxRandom.Range(0.02f, 0.08f);
                l.S0 = new Vector2(0f, FxRandom.Range(0.07f, 0.13f)); l.S1 = new Vector2(FxRandom.Range(5f, 11f), 0.02f);
                l.Life = FxRandom.Range(0.3f, 0.45f); l.A0 = 0.9f; l.FadePow = 1.2f; l.Col = ink;
                SetRot(l, rd);
            }

            // Star flash, colour bloom and two shock rings at the edge.
            var star = Take(Star, OrderKoCore, true);
            star.Pos = P; star.S0 = new Vector2(0.6f, 0.6f); star.S1 = Vector2.one * Mathf.Lerp(4f, 6f, s);
            star.Life = 0.38f; star.A0 = 1f; star.FadePow = 2f; star.Col = Hdr(Color.white, HdrKoCore);
            star.Rot = FxRandom.Range(0f, 45f); star.Spin = 90f;
            var bloom = Take(Art.Soft, OrderKo, true);
            bloom.Pos = P; bloom.S0 = Vector2.one * 1.5f; bloom.S1 = Vector2.one * Mathf.Lerp(7f, 10f, s);
            bloom.Life = 0.6f; bloom.A0 = 0.85f; bloom.FadePow = 1.3f; bloom.Col = Hdr(col, HdrKoGlow);
            for (int k = 0; k < 2; k++)
            {
                var ring = Take(Art.Ring, OrderKoCore, true);
                ring.Pos = P; ring.Delay = k * 0.07f; ring.S0 = Vector2.one * 0.6f;
                ring.S1 = Vector2.one * Mathf.Lerp(5f, 8f, s) * (k == 0 ? 1f : 0.7f);
                ring.Life = 0.45f; ring.A0 = 0.9f; ring.FadePow = 1.5f; ring.Col = Hdr(k == 0 ? Color.white : bright, HdrKoGlow);
            }

            // Ink smoke pushed into the arena.
            for (int k = 0; k < 8; k++)
            {
                float a = baseAng + FxRandom.Range(-55f, 55f);
                Vector2 rd = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad));
                var p = Take(Puff, OrderKoSmoke);
                p.Pos = P + rd * 0.3f; p.Vel = rd * FxRandom.Range(4f, 10f) * (0.8f + 0.4f * s); p.Drag = 3.2f;
                float sz = FxRandom.Range(0.55f, 0.85f);
                p.S0 = new Vector2(sz, sz); p.S1 = p.S0 * 2.1f;
                p.Life = FxRandom.Range(0.7f, 1.1f); p.A0 = 0.95f; p.FadePow = 1.2f;
                p.Col = Color.Lerp(SmokeCool, col, 0.35f); p.Rot = FxRandom.Range(0f, 360f); p.Spin = FxRandom.Range(-80f, 80f);
            }

            Fx.Burst(P, Hdr(col, HdrKoGlow), 40, 12f, 0.42f, 0.6f);
            Fx.Burst(P, Hdr(Color.white, HdrKoRay), 24, 9f, 0.28f);

            _shakeReq = Mathf.Max(_shakeReq, Mathf.Lerp(KoShakeMin, KoShakeMax, s));
            _flashReq = Mathf.Max(_flashReq, KoFlash);
            _flashColor = Color.Lerp(col, Color.white, 0.5f);
            _koFocus = P;
            _koFocusUntil = Time.unscaledTime + KoFocusSec;
        }

        // ------------------------------------------------------------------ respawn

        void PlayArrive(int i, Vector2 feet)
        {
            Color col = _fx[i];
            var g = Take(Streak, OrderTrail, true);
            g.Anchored = true; g.Pos = feet; g.Dir = Vector2.up; g.Grow = 0.08f;
            g.S0 = new Vector2(0f, 1.6f); g.S1 = new Vector2(7f, 0.1f); g.Life = 0.55f; g.A0 = 0.8f; g.FadePow = 1.4f; g.Col = Hdr(col, HdrKoGlow);
            SetRot(g, Vector2.up);
            var c = Take(Streak, OrderTrailCore, true);
            c.Anchored = true; c.Pos = feet; c.Dir = Vector2.up; c.Grow = 0.06f;
            c.S0 = new Vector2(0f, 0.5f); c.S1 = new Vector2(6f, 0.03f); c.Life = 0.4f; c.A0 = 1f; c.FadePow = 1.6f; c.Col = Hdr(Color.white, HdrArrive);
            SetRot(c, Vector2.up);
            Fx.Burst(feet, col, 16, 4f, 0.25f);
        }

        void UpdateHover(int i, float dt)
        {
            bool on = _hover[i] && _vis[i] != null;
            float fade;
            float grow;
            if (on)
            {
                _hoverT[i] += dt;
                fade = Mathf.Clamp01(_hoverT[i] / 0.15f);
                grow = Mathf.Lerp(0.3f, 1f, fade);
                _hoverEndT[i] = 99f;
            }
            else
            {
                _hoverEndT[i] += dt;
                float u = _hoverEndT[i] / 0.3f;
                if (u >= 1f || _vis[i] == null || _out[i])
                {
                    if (_hov[i, HovDisc].enabled) for (int k = 0; k < 5; k++) _hov[i, k].enabled = false;
                    return;
                }
                fade = 1f - u;
                grow = 1f + 0.4f * u;
            }

            Color col = _fx[i];
            Vector2 feet = (Vector2)_vis[i].transform.position - new Vector2(0f, _feetToPivot + 0.04f);
            float t = Time.time;
            float pulse = 1f + 0.05f * Mathf.Sin(t * 6f);
            Set(_hov[i, HovColumn], feet + new Vector2(0f, 1.2f), new Vector2(1.3f, 3f) * grow, col, 0.2f * fade);
            Set(_hov[i, HovGlow], feet, new Vector2(2.9f, 0.95f) * grow * pulse, col, 0.55f * fade);
            Set(_hov[i, HovInk], feet, new Vector2(1.74f, 0.44f) * grow, new Color(col.r * 0.15f, col.g * 0.12f, col.b * 0.2f), 0.95f * fade);
            Set(_hov[i, HovDisc], feet, new Vector2(1.6f, 0.32f) * grow, Hdr(Color.Lerp(col, Color.white, 0.45f), HdrHoverDisc), 0.95f * fade);
            float ru = Mathf.Repeat(t * 1.6f, 1f);   // a ring that keeps expanding out of the disc
            Set(_hov[i, HovRing], feet, new Vector2(1.6f, 0.34f) * grow * (1f + 0.6f * ru), col, (1f - ru) * fade);

            if (on)
            {
                _sparkT[i] -= dt;
                if (_sparkT[i] <= 0f)
                {
                    _sparkT[i] = 0.09f;
                    Fx.Burst(feet + new Vector2(FxRandom.Range(-0.7f, 0.7f), 0.05f), Color.Lerp(col, Color.white, 0.3f), 1, 1.2f, 0.12f, 0.4f);
                }
            }
        }

        static void Set(SpriteRenderer sr, Vector2 pos, Vector2 scale, Color c, float a)
        {
            if (!sr.enabled) sr.enabled = true;
            sr.transform.position = new Vector3(pos.x, pos.y, 0f);
            sr.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            c.a = a;
            sr.color = c;
        }

        // ------------------------------------------------------------------ pool

        Item Take(Sprite s, int order, bool light = false)
        {
            Item it = null;
            for (int k = 0; k < PoolSize; k++)
            {
                var c = _items[(_next + k) % PoolSize];
                if (!c.Live) { it = c; _next = (_next + k + 1) % PoolSize; break; }
            }
            if (it == null) { it = _items[_next]; _next = (_next + 1) % PoolSize; }   // full: recycle the oldest slot
            it.Live = true; it.Shown = false; it.Anchored = false;
            it.T = 0f; it.Delay = 0f; it.Life = 0.3f; it.A0 = 1f; it.FadePow = 1f; it.Drag = 0f; it.Grow = 0f;
            it.Spin = 0f; it.Rot = 0f;
            it.Pos = it.Vel = it.Dir = Vector2.zero;
            it.S0 = it.S1 = Vector2.one;
            it.Col = Color.white;
            it.Sr.sprite = s;
            it.Sr.sortingOrder = order;
            var mat = light ? LightMat : Art.SpriteMat;
            if (it.Sr.sharedMaterial != mat) it.Sr.sharedMaterial = mat;
            it.Sr.enabled = false;
            return it;
        }

        /// <summary>Anchored streaks: the thick (head) end of the texture sits at the origin, the taper runs along Dir.</summary>
        static void SetRot(Item it, Vector2 dir) => it.Rot = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg + 180f;

        static void Release(Item it)
        {
            if (it == null) return;
            it.Live = false;
            if (it.Sr != null) it.Sr.enabled = false;
        }

        void UpdateItems(float dt)
        {
            for (int k = 0; k < PoolSize; k++)
            {
                var it = _items[k];
                if (!it.Live) continue;
                if (it.Delay > 0f)
                {
                    it.Delay -= dt;
                    if (it.Delay > 0f) continue;
                }
                it.T += dt;
                float u = it.T / it.Life;
                if (u >= 1f) { Release(it); continue; }
                float e = 1f - (1f - u) * (1f - u);
                Vector2 sc;
                if (it.Grow > 0f)
                {
                    float g = Mathf.Clamp01(it.T / it.Grow);
                    g = 1f - (1f - g) * (1f - g) * (1f - g);
                    sc = new Vector2(it.S1.x * g, Mathf.Lerp(it.S0.y, it.S1.y, e));
                }
                else sc = Vector2.LerpUnclamped(it.S0, it.S1, e);
                if (it.Drag > 0f) it.Vel *= 1f / (1f + it.Drag * dt);
                it.Pos += it.Vel * dt;
                it.Rot += it.Spin * dt;
                Vector2 p = it.Anchored ? it.Pos + it.Dir * (sc.x * 0.5f) : it.Pos;
                it.Tr.position = new Vector3(p.x, p.y, 0f);
                it.Tr.localScale = new Vector3(sc.x, sc.y, 1f);
                it.Tr.rotation = Quaternion.Euler(0f, 0f, it.Rot);
                var c = it.Col;
                c.a *= it.A0 * Mathf.Pow(1f - u, it.FadePow);
                it.Sr.color = c;
                if (!it.Shown) { it.Shown = true; it.Sr.enabled = true; }
            }
        }

        // ------------------------------------------------------------------ procedural textures (built once)

        /// <summary>Tapered streak (1 x 1 unit): blunt bright head at +x, soft edges, thin tail at -x. White, tinted per use.</summary>
        static Sprite Streak => _streak != null ? _streak : (_streak = MakeSprite("LfxStreak", 128, StreakPixel));
        /// <summary>Cel-shaded smoke puff with an ink rim: lit fill, a shaded lower-right crescent, a dark outline.</summary>
        static Sprite Puff => _puff != null ? _puff : (_puff = MakeSprite("LfxPuff", 128, PuffPixel));
        /// <summary>Eight-point flash star (four long, four short points) with a hot centre.</summary>
        static Sprite Star => _star != null ? _star : (_star = MakeSprite("LfxStar", 128, StarPixel));

        delegate Color32 PixelFn(float u, float v);

        static Sprite MakeSprite(string name, int n, PixelFn fn)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name, hideFlags = HideFlags.DontSave,
            };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    px[y * n + x] = fn((x + 0.5f) / n * 2f - 1f, (y + 0.5f) / n * 2f - 1f);
            tex.SetPixels32(px);
            tex.Apply(false, true);
            var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            sp.name = name;
            return sp;
        }

        static Color32 StreakPixel(float u, float v)
        {
            float x = (u + 1f) * 0.5f;                                   // 0 tail .. 1 head
            float hw = x < 0.86f ? 0.92f * Mathf.Pow(x / 0.86f, 0.55f)
                                 : 0.92f * Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Pow((x - 0.86f) / 0.14f, 2f)));
            float d = Mathf.Abs(v);
            if (hw <= 0.001f || d >= hw) return new Color32(255, 255, 255, 0);
            float k = d / hw;                                            // 0 centre .. 1 edge
            float a = Mathf.Clamp01((1f - k) * 2.2f) * Mathf.Clamp01(x * 4f);
            byte w = (byte)(255 * Mathf.Lerp(1f, 0.82f, k));
            return new Color32(w, w, w, (byte)(255 * a));
        }

        static float Blob(float u, float v)
        {
            float d = Circle(u, v, 0f, -0.08f, 0.55f);
            d = Mathf.Min(d, Circle(u, v, -0.36f, 0.06f, 0.38f));
            d = Mathf.Min(d, Circle(u, v, 0.34f, 0.1f, 0.36f));
            d = Mathf.Min(d, Circle(u, v, 0.02f, 0.34f, 0.36f));
            return d;
        }

        static float Circle(float u, float v, float cx, float cy, float r)
        {
            float dx = u - cx, dy = v - cy;
            return Mathf.Sqrt(dx * dx + dy * dy) - r;
        }

        static Color32 PuffPixel(float u, float v)
        {
            float d = Blob(u, v);
            const float aa = 0.025f, rim = 0.07f;
            if (d > aa) return new Color32(255, 255, 255, 0);
            float a = Mathf.Clamp01((aa - d) / (2f * aa));
            float tone;
            if (d > -rim) tone = 0.2f;                                    // ink outline
            else tone = Blob(u + 0.12f, v - 0.12f) < -0.06f ? 1f : 0.74f; // lit (upper-left) vs shaded crescent
            byte w = (byte)(255 * tone);
            return new Color32(w, w, w, (byte)(255 * a));
        }

        static Color32 StarPixel(float u, float v)
        {
            float r = Mathf.Sqrt(u * u + v * v);
            float th = Mathf.Atan2(v, u);
            float c4 = Mathf.Cos(4f * th);
            float lim = 0.18f + 0.78f * Mathf.Pow(Mathf.Max(0f, c4), 10f) + 0.42f * Mathf.Pow(Mathf.Max(0f, -c4), 10f);
            float a = Mathf.Clamp01((lim - r) / 0.04f);
            float glow = Mathf.Clamp01(1f - r / 0.95f);
            a = Mathf.Max(a, glow * glow * 0.6f);
            return new Color32(255, 255, 255, (byte)(255 * a));
        }
    }
}
