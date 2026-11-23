using System;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Blast-Blade-style weapon presentation for the netcode view: luminous blade ribbons (white-hot core -> character Fx
    /// colour -> transparent edge, over a soft colour glow), crisp hit sparks with star flares, muzzle flares on shots and
    /// casts, and afterimage motion streaks while a fighter moves fast. Everything is HDR (<see cref="ChibiFxTextures.Peak"/>)
    /// so <see cref="ChibiPostFx"/> bloom (threshold 0.82) makes the cores glow.
    ///
    /// Presentation only (netcode-core rule): every frame it READS the displayed <see cref="ChibiSimState"/> through the
    /// accessor given to <see cref="Attach"/> and never writes it. Detection is edge-based on that state, so a rollback that
    /// re-simulates the same ticks fires nothing twice:
    /// <list type="bullet">
    /// <item>Swings: a move starts when MoveSlot changes or MoveTick goes back. The ribbon is rebuilt analytically each frame
    /// from a shape per AnimKind (moveset rows, <see cref="ChibiMoveInfo"/>) or per class and slot (classic slots 0-6),
    /// swept over the move's active window plus a couple of ticks either side, sized by the move's hitbox. The head follows
    /// the sim's move clock (smoothed between ticks, slowed by hitstop like the fighter); once the window ends, the fighter
    /// is hit or the move changes, the ribbon releases and fades over <see cref="FadeSec"/> of unscaled time.</item>
    /// <item>Hit sparks: the defender's PctM rose. Size from the percent gained, colour of the attacker (warm white for
    /// stage hazards).</item>
    /// <item>Muzzle flares: a new projectile of a Ranged / Magic fighter, or of a shoot / cast moveset row.</item>
    /// <item>Afterimages: three additive, Fx-tinted copies of <see cref="FighterVisual.BodyRenderer"/> while dashing,
    /// launched or moving faster than <see cref="GhostSpeed"/>.</item>
    /// </list>
    /// Pools are built once at attach (4 ribbons = 8 meshes of 132 vertices, 128 spark sprites, 6 ghost sprites); no
    /// per-frame allocation. Runs after <see cref="ChibiSimView"/> (execution order 100) so it reads this frame's
    /// interpolated fighter positions.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class ChibiBladeTrails : MonoBehaviour
    {
        // ---- tuning (world units, seconds, sim ticks)
        /// <summary>Unscaled seconds a released ribbon takes to fade out.</summary>
        public const float FadeSec = 0.18f;
        const float LeadTicks = 1.5f, LagTicks = 2f, MinWindowTicks = 4f;
        const float HandPull = 0.3f, HandPullMax = 0.4f;
        const float HitboxReachCap = 1.3f;            // trail radius never exceeds 1.3x the move's hitbox reach
        const float PctFullSpark = 14f;               // percent gained by one hit that gives a full-size spark
        /// <summary>World u/s above which a fighter leaves afterimages (run ~7.6, dash ~17).</summary>
        public const float GhostSpeed = 13f;
        const float GhostLaunchSpeed = 7f, GhostEvery = 0.05f, GhostLife = 0.16f, GhostAlpha = 0.55f, GhostLevel = 1.3f;
        const int MaxFlaresPerFrame = 3;
        const float MilliToWorld = 0.001f, FeetToPivot = 0.725f;
        const float SubToUps = ChibiSimConst.TickRate / (1000f * ChibiSimConst.VelSub);
        static readonly Vector2 Shoulder = new Vector2(0.05f, 0.12f);
        static readonly Color HazardSpark = new Color(1f, 0.92f, 0.8f);

        // Sorting: fighter i draws at 10 + 20 i - 1 .. + 15 (ChibiSimView.Begin sortBase). Blades go over both fighters
        // (46, under ChibiLaunchFx hover 47..49), glows and afterimages just behind the owner's body (sortBase + 1).
        const int OrderBlade = 46;
        static int SortBase(int i) => 10 + 20 * i;
        static readonly string[] VisNames = { "SimP1", "SimP2", "SimP3", "SimP4" };   // ChibiSimView.Begin fighter objects (rules v25: up to 4)

        sealed class Swing
        {
            public bool Active, Released;
            public TrailPath Path;
            public float TA, TB, RelT, RelHead, RelTail, LastHead, LastTail;
            public int Slot, Facing;
            public Vector2 Offset;
        }

        enum Program : byte { None, Barrage, LeapSlam }

        sealed class FighterFx
        {
            public FighterVisual Vis;
            public Color Fx;
            public int Slot = -1, Tick, Phase, PrevSlot = -1, Pct, Next, BarrageK;
            public float BaseTick, BaseTime;
            public Program Prog;
            public readonly Swing[] Swings = { new Swing(), new Swing() };
            public ChibiTrailRibbon[] Ribbons;
            public Material BladeEdge, BladeCentre;
            public Texture2D TexEdge, TexCentre;
            public readonly SpriteRenderer[] Ghosts = new SpriteRenderer[3];
            public readonly float[] GhostT = new float[3];
            public int GhostNext;
            public float GhostTimer;
        }

        Func<ChibiSimState> _state;
        float _groundY;
        bool _bound;
        readonly FighterFx[] _f = { new FighterFx(), new FighterFx(), new FighterFx(), new FighterFx() };
        int _n;   // fighters bound this match (state Count, 2..4)
        Material _glowEdge, _glowCentre;
        ChibiHitSparks _sparks;
        Transform _root;
        bool[] _pActive;
        int[] _pSpawn, _pOwner, _pKind;

        /// <summary>
        /// Adds blade trails, hit sparks and afterimages under <paramref name="host"/> (the ChibiSimView; destroyed with it).
        /// <paramref name="groundY"/> = world Y of sim Y 0; <paramref name="state"/> = displayed sim state (read only).
        /// The fighter visuals are found lazily as host children "SimP1" / "SimP2", so this can be attached before they exist.
        /// </summary>
        /// <example><code>ChibiBladeTrails.Attach(transform, GroundY, () => _loop != null ? _loop.State : null);</code></example>
        public static ChibiBladeTrails Attach(Transform host, float groundY, Func<ChibiSimState> state)
        {
            var go = new GameObject("BladeTrails");
            go.transform.SetParent(host, false);
            var t = go.AddComponent<ChibiBladeTrails>();
            t._state = state;
            t._groundY = groundY;
            return t;
        }

        bool Bind(ChibiSimState s)
        {
            var host = transform.parent;
            if (host == null) return false;
            _n = Mathf.Clamp(s.Count, 2, _f.Length);
            for (int i = 0; i < _n; i++)
            {
                var p = host.Find(VisNames[i]);
                _f[i].Vis = p != null ? p.GetComponent<FighterVisual>() : null;
                if (_f[i].Vis == null) return false;
            }

            // World-space root: ribbon vertices and spark positions are world coordinates.
            _root = new GameObject("FxRoot").transform;
            _root.SetParent(transform, false);
            _root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            _glowEdge = ChibiFxTextures.NewRibbonMaterial(ChibiFxTextures.GlowStrip(false), "ChibiTrailGlowArc");
            _glowCentre = ChibiFxTextures.NewRibbonMaterial(ChibiFxTextures.GlowStrip(true), "ChibiTrailGlowStreak");
            for (int i = 0; i < _n; i++)
            {
                var f = _f[i];
                var cls = (FighterClass)Mathf.Clamp(s.F[i].Class, 0, CharacterRoster.All.Length - 1);
                f.Fx = CharacterRoster.Of(cls).Fx;
                f.TexEdge = ChibiFxTextures.BladeStrip(f.Fx, false);
                f.TexCentre = ChibiFxTextures.BladeStrip(f.Fx, true);
                f.BladeEdge = ChibiFxTextures.NewRibbonMaterial(f.TexEdge, "ChibiTrailBladeArc" + i);
                f.BladeCentre = ChibiFxTextures.NewRibbonMaterial(f.TexCentre, "ChibiTrailBladeStreak" + i);
                f.Ribbons = new ChibiTrailRibbon[f.Swings.Length];
                for (int k = 0; k < f.Ribbons.Length; k++)
                    f.Ribbons[k] = new ChibiTrailRibbon(_root, "Trail" + i + "_" + k, OrderBlade, SortBase(i) + 1);
                for (int k = 0; k < f.Ghosts.Length; k++)
                {
                    var go = new GameObject("Afterimage" + i + "_" + k);
                    go.transform.SetParent(_root, false);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sharedMaterial = ChibiFxTextures.SpriteLight;
                    sr.sortingOrder = SortBase(i) + 1;
                    sr.enabled = false;
                    f.Ghosts[k] = sr;
                }
                var st = s.F[i];
                f.Slot = st.MoveSlot;
                f.Tick = st.MoveTick;
                f.Phase = st.MovePhase;
                f.PrevSlot = st.MoveSlot;
                f.Pct = st.PctM;
                f.BaseTick = st.MoveTick - 1;
                f.BaseTime = Time.unscaledTime;
            }
            _sparks = new ChibiHitSparks(_root);

            int n = s.Proj.Length;
            _pActive = new bool[n];
            _pSpawn = new int[n];
            _pOwner = new int[n];
            _pKind = new int[n];
            for (int k = 0; k < n; k++) Remember(k, s.Proj[k]);
            return true;
        }

        void LateUpdate()
        {
            var s = _state?.Invoke();
            if (s == null) return;
            if (!_bound)
            {
                _bound = Bind(s);
                return;   // first frame only records the baseline (nothing fires for a state already on screen)
            }
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            float scale = ChibiSimulation.CurrentScalePermille(s) / 1000f;

            for (int i = 0; i < _n; i++) UpdateMoves(i, s, now, scale);
            DetectHits(s);
            DetectShots(s);
            for (int i = 0; i < _n; i++)
            {
                DrawSwings(i, s, now, scale, dt);
                UpdateGhosts(i, s, dt);
            }
            _sparks.Tick(dt);
        }

        // ------------------------------------------------------------------ swings

        float MoveTime(FighterFx f, float now, float scale) =>
            f.BaseTick + Mathf.Clamp01((now - f.BaseTime) * ChibiSimConst.TickRate * scale);

        void UpdateMoves(int i, ChibiSimState s, float now, float scale)
        {
            var f = _f[i];
            var st = s.F[i];
            bool started = st.MoveSlot >= 0 && (st.MoveSlot != f.Slot || st.MoveTick < f.Tick);
            if (started || st.MoveTick != f.Tick)
            {
                f.BaseTick = st.MoveTick - 1;   // the tick the sim just processed (frame index of the move)
                f.BaseTime = now;
            }
            f.PrevSlot = f.Slot;
            f.Slot = st.MoveSlot;
            f.Tick = st.MoveTick;
            if (started)
            {
                // A new move ends whatever the previous one was drawing.
                for (int k = 0; k < f.Swings.Length; k++) if (f.Swings[k].Active && !f.Swings[k].Released) Release(f.Swings[k]);
                f.Phase = st.MovePhase;
                OnMoveStart(i, st);
            }
            if (st.MoveSlot < 0) { f.Prog = Program.None; f.Phase = st.MovePhase; return; }

            float t = MoveTime(f, now, scale);
            switch (f.Prog)
            {
                case Program.Barrage:
                {
                    // Thrust Barrage: one thrust streak per hit (frames 0, P, 2P, ..).
                    int p = ChibiSimConst.ThrustPeriod;
                    int k = t < 0f ? -1 : Mathf.FloorToInt((t + 1f) / p);
                    if (k != f.BarrageK && k >= 0 && k < ChibiSimConst.ThrustHits)
                    {
                        f.BarrageK = k;
                        StartSwing(i, st, TrailShape.Thrust, k * p - 1f, k * p + 3f, 1f);
                    }
                    break;
                }
                case Program.LeapSlam:
                    if (st.MovePhase != 0 && f.Phase == 0) StartSwing(i, st, TrailShape.Slam, t - 1f, t + 6f, 1f);   // landed
                    break;
            }
            f.Phase = st.MovePhase;
        }

        void OnMoveStart(int i, ChibiFighterSim st)
        {
            var f = _f[i];
            f.Prog = Program.None;
            f.BarrageK = -1;
            int slot = st.MoveSlot;
            var cls = (FighterClass)st.Class;
            float su = st.MoveStartup, ac = st.MoveActive;

            if (slot >= ChibiMovesetData.FirstNewSlot)
            {
                var info = ChibiMoveInfo.Get(st.Class, slot);
                if (!info.Valid) return;
                var shape = ChibiTrailShapes.FromAnimKind(info.AnimKind);
                if (shape == TrailShape.None) return;   // shoot / cast (muzzle flares) and guard
                float turns = shape == TrailShape.Spin ? Mathf.Clamp(ac / 14f, 1f, 4f) : 1f;
                StartSwing(i, st, shape, su - LeadTicks, su + ac + LagTicks, turns);
                return;
            }

            if (cls == FighterClass.Melee && slot == 4) { f.Prog = Program.Barrage; return; }
            if (cls == FighterClass.Heavy && slot == 3)      // Tornado Spin: hits every 12 frames up to 48
            {
                float end = ChibiSimConst.TornadoLastHit + ChibiSimConst.TornadoPeriod;
                StartSwing(i, st, TrailShape.Spin, -0.5f, end, end / 60f * 2.4f);
                return;
            }
            if (cls == FighterClass.Polearm && slot == 3)    // Spin Guard
            {
                float end = ChibiSimConst.SpinGuardTicks;
                StartSwing(i, st, TrailShape.Spin, -0.5f, end, end / 60f * 2f);
                return;
            }
            if (cls == FighterClass.Polearm && slot == 2)    // Leap Slam: rising swing now, slam on landing (phase 1)
            {
                f.Prog = Program.LeapSlam;
                StartSwing(i, st, TrailShape.Upswing, -0.5f, 10f, 1f);
                return;
            }
            if (slot >= 5) return;                           // ults (cinematics have their own effects), pose lock

            int combo = 0, len = st.Stats.PatternLength;
            if (slot == 0 && len > 0) combo = (st.ComboIdx + len - 1) % len;   // StartMove advanced it past the step used
            var sh = ChibiTrailShapes.Classic(cls, slot, combo);
            if (sh != TrailShape.None) StartSwing(i, st, sh, su - LeadTicks, su + ac + LagTicks, 1f);
        }

        void StartSwing(int i, ChibiFighterSim st, TrailShape shape, float ta, float tb, float turns)
        {
            var f = _f[i];
            if (tb - ta < MinWindowTicks) tb = ta + MinWindowTicks;

            // Reach and height from the move's melee hitbox when it has one (the trail covers what can hit).
            float reach = ChibiTrailShapes.Reach(st.Class), dy = 0f;
            if (st.MoveSlot >= 0 && st.Class >= 0 && st.Class < 5 && st.MoveSlot < ChibiMoveTable.Count(st.Class))
            {
                var mv = ChibiMoveTable.Get(st.Class, st.MoveSlot);
                if (mv.BoxW > 0 && mv.ProjSpeed == 0)
                {
                    float far = ((mv.Flags & ChibiMove.FCentered) != 0 ? mv.BoxW * 0.5f : mv.BoxX + mv.BoxW) * MilliToWorld;
                    reach = Mathf.Clamp(Mathf.Min(Mathf.Max(reach * 0.75f, far * 0.95f), far * HitboxReachCap), 0.6f, 2.8f);
                    float mid = (mv.BoxY + mv.BoxH * 0.5f) * MilliToWorld - FeetToPivot;
                    dy = Mathf.Clamp(mid, -0.6f, 0.7f) * 0.5f;
                }
            }

            // Free swing slot, else the one released earliest (oldest).
            int k = -1;
            for (int j = 0; j < f.Swings.Length; j++) if (!f.Swings[j].Active) { k = j; break; }
            if (k < 0) { k = f.Next; f.Next = (f.Next + 1) % f.Swings.Length; }
            var w = f.Swings[k];
            w.Active = true;
            w.Released = false;
            w.Path = ChibiTrailShapes.Path(shape, reach, dy, turns);
            w.TA = ta;
            w.TB = tb;
            w.Slot = st.MoveSlot;
            w.Facing = st.Facing >= 0 ? 1 : -1;
            w.LastHead = w.LastTail = 0f;

            // Lean the pivot toward the weapon hand (HandWorld) so the arc grows out of the weapon, clamped for stability.
            Vector2 pos = f.Vis.transform.position, hand = f.Vis.HandWorld;
            var handLocal = new Vector2((hand.x - pos.x) * w.Facing, hand.y - pos.y);
            w.Offset = Vector2.ClampMagnitude((handLocal - Shoulder) * HandPull, HandPullMax);

            var r = f.Ribbons[k];
            r.SetMaterials(w.Path.Centred ? f.BladeCentre : f.BladeEdge, w.Path.Centred ? _glowCentre : _glowEdge, SortBase(i) + 1);
        }

        /// <summary>Freeze the ribbon where it was last drawn and start its fade.</summary>
        static void Release(Swing w)
        {
            w.Released = true;
            w.RelT = 0f;
            w.RelHead = w.LastHead;
            w.RelTail = w.LastTail;
        }

        void DrawSwings(int i, ChibiSimState s, float now, float scale, float dt)
        {
            var f = _f[i];
            var st = s.F[i];
            Vector2 pos = f.Vis.transform.position;
            float t = MoveTime(f, now, scale);
            for (int k = 0; k < f.Swings.Length; k++)
            {
                var w = f.Swings[k];
                var r = f.Ribbons[k];
                if (!w.Active) { r.Hide(); continue; }
                float head, tail, alpha = 1f;
                if (!w.Released)
                {
                    // Interrupted (hit, move changed or ended) or the window is over: release at the current head.
                    bool lost = st.MoveSlot != w.Slot || st.Stun > 0 || !st.InPlay;
                    if (!lost && t < w.TA) { r.Hide(); continue; }   // still in startup
                    if (lost || t >= w.TB)
                    {
                        if (t >= w.TB) { w.LastHead = 1f; w.LastTail = Mathf.Max(0f, 1f - w.Path.Span); }
                        Release(w);
                    }
                }
                if (w.Released)
                {
                    w.RelT += dt;
                    float k01 = w.RelT / FadeSec;
                    if (k01 >= 1f) { w.Active = false; r.Hide(); continue; }
                    head = w.RelHead;
                    tail = Mathf.Lerp(w.RelTail, w.RelHead, k01 * k01 * (3f - 2f * k01) * 0.9f);   // the tail catches up
                    alpha = Mathf.Pow(1f - k01, 1.6f);
                }
                else
                {
                    head = Mathf.Clamp01((t - w.TA) / (w.TB - w.TA));
                    tail = Mathf.Max(0f, head - w.Path.Span);
                    w.LastHead = head;
                    w.LastTail = tail;
                }
                var anchor = pos + new Vector2(w.Offset.x * w.Facing, w.Offset.y);
                r.Draw(w.Path, tail, head, anchor, w.Facing, f.Fx, alpha);
            }
        }

        // ------------------------------------------------------------------ hits and shots

        void DetectHits(ChibiSimState s)
        {
            for (int d = 0; d < _n; d++)
            {
                var f = _f[d];
                int pct = s.F[d].PctM;
                int gained = pct - f.Pct;
                f.Pct = pct;
                if (gained <= 0 || !s.F[d].InPlay) continue;   // respawn reset / lifesteal: no spark

                int a = ChibiSimulation.LikelyAttacker(s, d);
                if (a < 0 || a >= _n) a = d;   // no enemy left (hazard): spark from the defender itself
                Vector2 dc = f.Vis.transform.position, ac = _f[a].Vis.transform.position;
                float side = ac.x >= dc.x ? 1f : -1f;
                var at = dc + new Vector2(side * 0.3f, 0.15f);
                bool byFighter = s.F[a].MoveSlot >= 0 || _f[a].PrevSlot >= 0 || OwnsProjectile(s, a);
                _sparks.Hit(at, byFighter ? _f[a].Fx : HazardSpark, gained / (1000f * PctFullSpark));
            }
        }

        static bool OwnsProjectile(ChibiSimState s, int owner)
        {
            var p = s.Proj;
            for (int k = 0; k < p.Length; k++) if (p[k].Active && p[k].Owner == owner) return true;
            return false;
        }

        void DetectShots(ChibiSimState s)
        {
            int flares = 0;
            var p = s.Proj;
            int n = Mathf.Min(p.Length, _pActive.Length);
            for (int k = 0; k < n; k++)
            {
                var pr = p[k];
                bool fresh = pr.Active && (!_pActive[k] || _pSpawn[k] != pr.SpawnFrame || _pOwner[k] != pr.Owner || _pKind[k] != (int)pr.Kind);
                Remember(k, pr);
                if (!fresh || flares >= MaxFlaresPerFrame || pr.Owner < 0 || pr.Owner >= _n || !WantsFlare(s, pr)) continue;
                flares++;
                var at = new Vector2(pr.X * MilliToWorld, _groundY + pr.Y * MilliToWorld);
                _sparks.Muzzle(at, new Vector2(pr.Vx, pr.Vy), _f[pr.Owner].Fx);
            }
        }

        static bool WantsFlare(ChibiSimState s, ChibiProjectileSim pr)
        {
            int cls = s.F[pr.Owner].Class;
            if (cls == (int)FighterClass.Ranged || cls == (int)FighterClass.Magic) return true;
            if (pr.SrcSlot < ChibiMovesetData.FirstNewSlot) return false;
            var info = ChibiMoveInfo.Get(cls, pr.SrcSlot);
            return info.Valid && (info.AnimKind == "shoot" || info.AnimKind == "cast");
        }

        void Remember(int k, ChibiProjectileSim pr)
        {
            _pActive[k] = pr.Active;
            _pSpawn[k] = pr.SpawnFrame;
            _pOwner[k] = pr.Owner;
            _pKind[k] = (int)pr.Kind;
        }

        // ------------------------------------------------------------------ afterimages

        void UpdateGhosts(int i, ChibiSimState s, float dt)
        {
            var f = _f[i];
            var st = s.F[i];
            float speed = new Vector2(st.Vx, st.Vy).magnitude * SubToUps;
            bool fast = st.InPlay && (st.DashTicks > 0 || (st.Stun > 0 && speed > GhostLaunchSpeed) || speed > GhostSpeed);
            f.GhostTimer -= dt;
            if (fast && f.GhostTimer <= 0f)
            {
                f.GhostTimer = GhostEvery;
                SpawnGhost(f);
            }
            for (int k = 0; k < f.Ghosts.Length; k++)
            {
                var g = f.Ghosts[k];
                if (!g.enabled) continue;
                f.GhostT[k] += dt;
                float k01 = f.GhostT[k] / GhostLife;
                if (k01 >= 1f) { g.enabled = false; continue; }
                var c = g.color;
                c.a = GhostAlpha * Mathf.Pow(1f - k01, 1.3f);
                g.color = c;
            }
        }

        void SpawnGhost(FighterFx f)
        {
            var body = f.Vis.BodyRenderer;
            if (body == null || !body.enabled || body.sprite == null || body.color.a < 0.5f) return;
            int k = f.GhostNext;
            f.GhostNext = (k + 1) % f.Ghosts.Length;
            var g = f.Ghosts[k];
            if (g.sprite != body.sprite) g.sprite = body.sprite;
            g.flipX = body.flipX;
            g.flipY = body.flipY;
            var bt = body.transform;
            g.transform.SetPositionAndRotation(bt.position, bt.rotation);
            g.transform.localScale = bt.lossyScale;
            float m = GhostLevel / ChibiFxTextures.Peak;
            var tint = Color.Lerp(f.Fx, Color.white, 0.2f);
            g.color = new Color(tint.r * m, tint.g * m, tint.b * m, GhostAlpha);
            f.GhostT[k] = 0f;
            g.enabled = true;
        }

        void OnDestroy()
        {
            for (int i = 0; i < _n; i++)
            {
                var f = _f[i];
                if (f.Ribbons != null) for (int k = 0; k < f.Ribbons.Length; k++) f.Ribbons[k]?.Dispose();
                if (f.BladeEdge != null) Destroy(f.BladeEdge);
                if (f.BladeCentre != null) Destroy(f.BladeCentre);
                if (f.TexEdge != null) Destroy(f.TexEdge);
                if (f.TexCentre != null) Destroy(f.TexCentre);
            }
            if (_glowEdge != null) Destroy(_glowEdge);
            if (_glowCentre != null) Destroy(_glowCentre);
        }
    }
}
