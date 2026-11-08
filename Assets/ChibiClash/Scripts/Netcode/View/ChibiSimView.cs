using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Draws a ChibiLocalLoop's deterministic simulation with the original game visuals (FighterVisual, StageBuilder,
    /// Fx). Read-only: the view never writes to simulation state; the sim decides, the view only draws.
    /// Positions are interpolated between the last two GAME steps (timescale-conversion.md 8.3(2)); presentation events
    /// come from ChibiSimEvents, diffed once per game step (frozen real ticks are skipped so nothing re-fires).
    /// Presentation speed follows the original MatchController.Update (MatchController.cs:112-116): Time.timeScale is
    /// set to the sim's current scale (hitstop 0.05, cinematic 0.2) and to 0.3 for 1.2 s after the match ends. The
    /// sim clock runs on Time.unscaledDeltaTime (ChibiLocalLoop.cs:54) and never reads Time.timeScale, so this only
    /// slows Fx, FighterVisual animation and anything else on scaled time.
    /// </summary>
    public sealed class ChibiSimView : MonoBehaviour
    {
        // Visual mapping (presentation only). Sim Y = 0 is the floor; the fighter transform sits at the collider
        // centre used by Fighter.cs (collider 1.35 tall, offset -0.05), i.e. 0.725 above the feet.
        const float MilliToWorld = 1f / 1000f;
        const float FeetToPivot = 0.725f;
        const float StunVisualTicks = 18f;        // Fighter.cs shows the stun stars above 0.3 s of hitstun
        const float CamLerp = 6f, CamSizeLerp = 5f, CamMinSize = 5.5f, CamMaxSize = 10f, CamPad = 3.2f;
        const float ShakeOnHit = 0.25f, ShakeDecay = 2.5f, ShakeScale = 0.6f;
        // Ult cinematic, MatchController.Cinematic (MatchController.cs:184-193) and CameraController (Stage.cs:161-164).
        const float CinematicFocusSec = 0.8f, CinematicFocusSize = 3.6f, CinematicShake = 0.3f;
        const float CinematicFlash = 0.8f, FlashDecay = 2.5f;          // MatchController.cs:189 / :119
        // Finish slow-mo, MatchController.cs:114 (view only; the sim has already stopped at MatchOver).
        const float FinishScale = 0.3f, FinishSec = 1.2f;

        enum Anim : byte { Swing, Thrust, Spin, Slam, Recoil }

        /// <summary>Skill/ult animation per class for slots 2..5, taken from the Visual.PlayX calls in Fighter.cs.</summary>
        static readonly Anim[,] SkillAnim =
        {
            { Anim.Thrust, Anim.Swing,  Anim.Swing,  Anim.Swing  },  // Melee: DashThrust, Parry(fallback), ThrustBarrage, Sword Aura(fallback)
            { Anim.Thrust, Anim.Spin,   Anim.Swing,  Anim.Slam   },  // Heavy: Grab, TornadoSpin, (none), UltSmash
            { Anim.Recoil, Anim.Thrust, Anim.Recoil, Anim.Recoil },  // Ranged: BackstepShot, Choke, ArrowBarrage, ThousandArrows
            { Anim.Recoil, Anim.Recoil, Anim.Recoil, Anim.Recoil },  // Magic: Fireball, Minions, Cloud, Fire Ring
            { Anim.Swing,  Anim.Spin,   Anim.Thrust, Anim.Thrust },  // Polearm: LeapSlam, SpinGuard, ThrowSpear, PolearmKick
        };
        static readonly float[,] SkillDur =
        {
            { 0.3f, 0.3f, 0.3f, 0.3f },
            { 0.2f, 1.0f, 0.3f, 0.4f },
            { 0.2f, 0.3f, 0.3f, 0.3f },
            { 0.3f, 0.3f, 0.3f, 0.3f },
            { 0.5f, 1.2f, 0.3f, 0.3f },
        };

        ChibiLocalLoop _loop;
        readonly ChibiSimState _before = new ChibiSimState();   // state one tick before _seen (interpolation start)
        readonly ChibiSimState _seen = new ChibiSimState();     // latest tick the view has processed
        readonly List<ChibiSimEvent> _events = new List<ChibiSimEvent>(32);
        readonly FighterVisual[] _vis = new FighterVisual[2];
        readonly Skin[] _skin = new Skin[2];
        readonly ClassDef[] _def = new ClassDef[2];
        ProjVis[] _proj;
        readonly bool[] _weaponHidden = new bool[2];   // last SpearOut applied to FighterVisual.SetWeaponVisible
        // VolleyLead expiries seen in OnSimFrame (position taken from the pre-step state): Fx.Burst in LateUpdate.
        readonly List<Vector2> _leadBurstPos = new List<Vector2>(4);
        readonly List<int> _leadBurstOwner = new List<int>(4);
        // Projectile looks, CombatObjects.cs:27-42 (Glow 0.5 alpha x2.2 at order 59, Core at order 60) with the
        // sizes / colours from Fighter.cs. Trail: CombatObjects.cs:79-86.
        const float ProjGlowScale = 2.2f, ProjGlowAlpha = 0.5f, TrailInterval = 0.03f;
        const float SmashWaveBaseR = 600f;             // sim Radius at spawn; y scale = Radius / 600 (GrowRate, CO:77)
        const float LeadBurstSpeed = 7f, LeadBurstSize = 0.4f;
        const int LeadBurstCount = 40;
        static readonly Color ArrowColor = new Color(0.95f, 0.9f, 0.75f);   // Fighter.cs SpawnArrow / volley arrows
        static readonly Color FireballColor = new Color(1f, 0.45f, 0.1f), FireballTrail = new Color(1f, 0.8f, 0.2f);
        static readonly Color PoseFireballColor = new Color(1f, 0.55f, 0.15f);   // Fighter.cs:388-389
        static readonly Color SpearColor = new Color(0.85f, 0.88f, 0.95f);

        /// <summary>Per-slot projectile visual: Glow + Core sprites under one root, plus the identity they were built for.</summary>
        sealed class ProjVis
        {
            public Transform Root;
            public SpriteRenderer Glow, Core;
            public bool Live, Grow;
            public int SpawnFrame, Owner;
            public ChibiProjKind Kind;
            public Color Trail;
            public float TrailTimer;
        }
        StageInfo _stage;
        Camera _cam;
        float _shake;
        int _focus = -1;           // fighter the camera is focused on during the ult cinematic, -1 none
        float _focusUntil;         // unscaled time the focus ends
        bool _finished;            // MatchOver seen
        float _finishedTime;       // unscaled time MatchOver was first seen

        /// <summary>Fighter of the latest ult cinematic (0/1), -1 before any. Read by ChibiSimTestHud.</summary>
        public int CinematicFighter { get; private set; } = -1;
        /// <summary>Time.unscaledTime the latest cinematic started (Hud.cs:18 shows the band while under 1.5 s).</summary>
        public float CinematicStart { get; private set; } = -10f;
        /// <summary>Cinematic band colour: Gui.Gold for gold skins, else the skin's ultFx (Hud.cs:186).</summary>
        public Color CinematicColor { get; private set; }
        /// <summary>Band title (Hud.cs:192-193), built once per cinematic.</summary>
        public string CinematicTitle { get; private set; } = "";
        /// <summary>Band subtitle "HERO · ult description" (Hud.cs:194), built once per cinematic.</summary>
        public string CinematicSubtitle { get; private set; } = "";
        /// <summary>Full-screen flash alpha; drawn at half alpha (Hud.cs:55-58).</summary>
        public float FlashAlpha { get; private set; }
        /// <summary>Full-screen flash colour (the ult user's ultFx, MatchController.cs:190).</summary>
        public Color FlashColor { get; private set; }

        /// <summary>Ground height of the drawn stage in world units.</summary>
        public float GroundY => _stage != null ? _stage.groundY : 0f;

        /// <summary>Build the visuals for <paramref name="loop"/> (already begun). Call once.</summary>
        /// <example><code>view.Begin(loop, cfg);</code></example>
        public void Begin(ChibiLocalLoop loop, in ChibiMatchConfig cfg)
        {
            _loop = loop;
            var state = loop.Session.State;
            _before.CopyFrom(state);
            _seen.CopyFrom(state);

            Fx.Init(transform);
            // Stage chosen by cfg.Stage (0 Sky Dojo, 1 Neon City, 2 Lava Forge); sim Y 0 maps to _stage.groundY.
            _stage = StageBuilder.Build(cfg.Stage, transform);

            for (int i = 0; i < 2; i++)
            {
                var cls = i == 0 ? cfg.Class0 : cfg.Class1;
                _def[i] = ClassDef.Get(cls);
                _skin[i] = Skin.Default(cls);           // test match: no save data, default skins
                var go = new GameObject("SimP" + (i + 1));
                go.transform.SetParent(transform, false);
                _vis[i] = go.AddComponent<FighterVisual>();
                _vis[i].Build(_def[i], _skin[i], 10 + i * 20, WeaponDef.Get(cls, 0));
                _vis[i].SetFacing(state.F[i].Facing >= 0 ? 1 : -1);
                go.transform.position = WorldPos(state.F[i].X, state.F[i].Y);
            }

            // One Glow + Core pair per pool slot, created once; Configure re-skins it when the slot's identity changes.
            _proj = new ProjVis[state.Proj.Length];
            for (int i = 0; i < _proj.Length; i++)
            {
                var root = new GameObject("SimProj" + i).transform;
                root.SetParent(transform, false);
                var v = new ProjVis { Root = root };
                v.Glow = Art.Sprite(root, "Glow", Art.Soft, Color.clear, Vector2.zero, Vector2.one, 59);
                v.Core = Art.Sprite(root, "Core", Art.Circle, Color.white, Vector2.zero, Vector2.one, 60);
                root.gameObject.SetActive(false);
                _proj[i] = v;
            }

            _cam = Camera.main;
            loop.Session.OnFrame += OnSimFrame;
        }

        Vector3 WorldPos(int x, int y) => new Vector3(x * MilliToWorld, GroundY + FeetToPivot + y * MilliToWorld, 0f);
        Vector3 ProjPos(int x, int y) => new Vector3(x * MilliToWorld, GroundY + y * MilliToWorld, 0f);

        void OnSimFrame(int frame, ChibiInputPacket a, ChibiInputPacket b)
        {
            var cur = _loop.Session.State;
            // Frozen real tick (hitstop / cinematic, no game step ran): the world did not change, so keep the
            // interpolation pair and emit nothing.
            if (cur.GameTicks == _seen.GameTicks && cur.MatchOver == _seen.MatchOver) return;
            if (!_seen.MatchOver && cur.MatchOver && !_finished)
            {
                _finished = true;
                _finishedTime = Time.unscaledTime;
            }
            int first = _events.Count;
            ChibiSimEvents.Diff(_seen, cur, _events);
            // Thousand Arrows lead expiry burst (Fighter.cs:1021): the event carries no position, so read it from the
            // state that still holds the lead before _seen is overwritten.
            for (int i = first; i < _events.Count; i++)
            {
                var e = _events[i];
                if (e.Kind != ChibiSimEventKind.ProjectileGone || e.Value2 != (int)ChibiProjKind.VolleyLead) continue;
                var lead = _seen.Proj[e.Index];
                _leadBurstPos.Add(ProjPos(lead.X, lead.Y));
                _leadBurstOwner.Add(Mathf.Clamp(e.Value, 0, 1));
            }
            _before.CopyFrom(_seen);
            _seen.CopyFrom(cur);
        }

        /// <summary>Presentation time scale for this frame, mirroring MatchController.cs:111-116.</summary>
        float ViewTimeScale(ChibiSimState live)
        {
            // MatchController.cs:114 overrides hitstop/cinematic once the match is finished. The sim stops stepping
            // at MatchOver (its timers freeze), so its scale is ignored from then on.
            if (_finished) return Time.unscaledTime - _finishedTime < FinishSec ? FinishScale : 1f;
            return ChibiSimulation.CurrentScalePermille(live) / 1000f;   // 1 / 0.2 / 0.05, the same values as :112-113
        }

        void Update()
        {
            if (_loop == null || _loop.Session == null) return;
            Time.timeScale = ViewTimeScale(_loop.Session.State);   // restored by ChibiSimTestMatch.Stop
        }

        void LateUpdate()
        {
            if (_loop == null || _loop.Session == null) return;
            var live = _loop.Session.State;
            // timescale-conversion.md 8.3(2): f = min(0.999, (sinceStep + RenderAlpha) * scale / 1000). NOT TimeAccum,
            // which keeps the carried phase and would stall every tick after the first hitstop. After MatchOver the
            // sim no longer steps, so show the final state.
            float alpha = live.MatchOver ? 1f
                : Mathf.Min(0.999f, (live.RealTicksSinceStep + Mathf.Clamp01(_loop.RenderAlpha))
                                    * ChibiSimulation.CurrentScalePermille(live) / 1000f);

            for (int i = 0; i < _events.Count; i++) Apply(_events[i]);
            _events.Clear();
            for (int i = 0; i < _leadBurstPos.Count; i++)
                Fx.Burst(_leadBurstPos[i], _skin[_leadBurstOwner[i]].ultFx, LeadBurstCount, LeadBurstSpeed, LeadBurstSize);
            _leadBurstPos.Clear();
            _leadBurstOwner.Clear();

            for (int i = 0; i < 2; i++)
            {
                var a = _before.F[i];
                var f = _seen.F[i];
                var v = _vis[i];
                bool teleport = System.Math.Abs(f.X - a.X) > 2000 || System.Math.Abs(f.Y - a.Y) > 2000;   // respawn
                Vector3 p0 = WorldPos(a.X, a.Y), p1 = WorldPos(f.X, f.Y);
                v.transform.position = teleport ? p1 : Vector3.Lerp(p0, p1, alpha);
                v.SetFacing(f.Facing >= 0 ? 1 : -1);

                // Sim velocities are sub-units (1/VelSub milli per tick): convert to world units per second.
                var vel = new Vector2(f.Vx, f.Vy) * (ChibiSimConst.TickRate * MilliToWorld / ChibiSimConst.VelSub);
                v.Tick(vel, f.Grounded, f.Shielding);

                bool parry = f.ParryTicks > 0;
                float guard = 1f;
                int gmax = ChibiCombatStats.GuardMaxU(f.Stats.Defense);
                if (f.Shielding && gmax > 0) guard = Mathf.Clamp01(f.GuardU / (float)gmax);
                v.SetShield(f.Shielding || parry, parry, guard);
                v.SetAura(f.AuraTicks > 0 || f.RingTicks > 0, _skin[i].ultFx);
                v.SetStunned(f.Stun > StunVisualTicks);
                // Thrown spear (Fighter.cs ThrowSpear / OnGone): hide the held weapon while it is out. Only on change,
                // SetWeaponVisible allocates (GetComponentsInChildren).
                if (f.SpearOut != _weaponHidden[i])
                {
                    _weaponHidden[i] = f.SpearOut;
                    v.SetWeaponVisible(!f.SpearOut);
                }
            }

            float dt = Time.deltaTime;   // scaled, like the original Projectile.Update trail timer
            for (int i = 0; i < _proj.Length; i++)
            {
                var b = _seen.Proj[i];
                var v = _proj[i];
                if (!b.Active)
                {
                    if (v.Live) { v.Live = false; v.Root.gameObject.SetActive(false); }
                    continue;
                }
                bool fresh = !v.Live || v.SpawnFrame != b.SpawnFrame || v.Kind != b.Kind || v.Owner != b.Owner;
                if (fresh) Configure(v, b);

                var a = _before.Proj[i];
                bool lerp = a.Active && ChibiSimEvents.SameProjectile(a, b);
                var p = ProjPos(b.X, b.Y);
                if (lerp) p = Vector3.Lerp(ProjPos(a.X, a.Y), p, alpha);
                v.Root.position = p;

                // transform.right = velocity direction (CO:41, :76); growing waves keep their spawn rotation (CO:76).
                if ((fresh || !v.Grow) && (b.Vx != 0 || b.Vy != 0))
                    v.Root.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(b.Vy, b.Vx) * Mathf.Rad2Deg);
                if (v.Grow)
                {
                    float r = lerp ? Mathf.Lerp(a.Radius, b.Radius, alpha) : b.Radius;
                    v.Root.localScale = new Vector3(1f, r / SmashWaveBaseR, 1f);
                }

                if (v.Trail.a > 0f)
                {
                    v.TrailTimer -= dt;
                    if (v.TrailTimer <= 0f)
                    {
                        v.TrailTimer = TrailInterval;
                        Fx.Burst(p, v.Trail, 1, 0.3f, 0.18f, 0.25f);   // CO:84
                    }
                }
            }

            Fx.Tick(Time.deltaTime);   // scaled, like MatchController.cs:118
            FlashAlpha = Mathf.MoveTowards(FlashAlpha, 0f, Time.unscaledDeltaTime * FlashDecay);   // MatchController.cs:119
            UpdateCamera();
        }

        void Apply(in ChibiSimEvent e)
        {
            switch (e.Kind)
            {
                case ChibiSimEventKind.MoveStarted:
                {
                    var f = _seen.F[e.Index];
                    var v = _vis[e.Index];
                    if (e.Value <= 1)
                    {
                        float dur = (f.MoveStartup + f.MoveActive + f.MoveRecovery) / (float)ChibiSimConst.TickRate;
                        v.PlayNormal(e.Value == 1, e.Value2, dur);
                    }
                    else
                    {
                        int cls = Mathf.Clamp(f.Class, 0, SkillAnim.GetLength(0) - 1);
                        int col = Mathf.Clamp(e.Value - 2, 0, SkillAnim.GetLength(1) - 1);
                        Play(v, SkillAnim[cls, col], SkillDur[cls, col]);
                        if (e.Value >= 2 && e.Value <= 4)
                        {
                            var names = _def[e.Index].skillNames;
                            int k = e.Value - 2;
                            if (names != null && k < names.Length) Fx.Text(Center(e.Index) + Vector2.up * 1.25f, names[k], _def[e.Index].color, 0.75f);
                        }
                        else if (e.Value == 5) Fx.Burst(Center(e.Index), _skin[e.Index].ultFx, 40, 8f, 0.5f);
                    }
                    break;
                }
                case ChibiSimEventKind.Damaged:
                    _vis[e.Index].Flash();
                    if (_seen.F[e.Index].MoveSlot < 0) _vis[e.Index].PlayRecoil(0.25f);
                    Fx.Burst(Center(e.Index), Color.white, 12, 5f, 0.25f);
                    Fx.Text(Center(e.Index) + Vector2.up * 0.9f, e.Value.ToString(), Gui.Yellow, 1f);
                    _shake = Mathf.Max(_shake, ShakeOnHit);
                    break;
                case ChibiSimEventKind.Parried:
                    Fx.Text(Center(e.Index) + Vector2.up * 1.2f, "PARRY", Color.white, 1.1f);
                    break;
                case ChibiSimEventKind.RingOut:
                    Fx.Text(Center(e.Index) + Vector2.up * 1.2f, "RING OUT", Gui.Red, 1.1f);
                    break;
                case ChibiSimEventKind.DashStarted:
                    Fx.Burst(Center(e.Index), _skin[e.Index].ultFx, 8, 3f, 0.3f);   // Fighter.cs dash start
                    break;
                case ChibiSimEventKind.KO:
                    Fx.Burst(Center(e.Index), _skin[e.Index].ultFx, 40, 8f, 0.5f);
                    Fx.Text(Center(e.Index) + Vector2.up * 1.4f, "K.O.", Gui.Red, 1.5f);
                    break;
                case ChibiSimEventKind.CinematicStarted:
                    StartCinematic(e.Index);
                    break;
            }
        }

        /// <summary>MatchController.Cinematic (MatchController.cs:184-193) for fighter <paramref name="i"/>.</summary>
        void StartCinematic(int i)
        {
            var skin = _skin[i];
            var def = _def[i];
            CinematicFighter = i;
            CinematicStart = Time.unscaledTime;                              // :187
            FlashAlpha = CinematicFlash;                                     // :189
            FlashColor = skin.ultFx;                                         // :190
            _focus = i;                                                      // :191 cam.Focus(f, 0.8f)
            _focusUntil = Time.unscaledTime + CinematicFocusSec;
            _shake = Mathf.Max(_shake, CinematicShake);                      // :192 Shake(0.3f)
            // Hud.cs:186, :192-194; strings built once here, not per OnGUI.
            CinematicColor = skin.goldCinematic ? Gui.Gold : skin.ultFx;
            CinematicTitle = skin.goldCinematic ? "★ " + def.ultName + " ★" : def.ultName;
            CinematicSubtitle = def.heroName.ToUpper() + " · " + def.ultDesc;
        }

        /// <summary>Re-skin slot visual <paramref name="v"/> for projectile <paramref name="b"/> (new identity).</summary>
        void Configure(ProjVis v, ChibiProjectileSim b)
        {
            var ult = _skin[Mathf.Clamp(b.Owner, 0, 1)].ultFx;
            UnityEngine.Sprite sprite = Art.Rounded;
            Color c, trail = Color.clear;
            Vector2 size;
            switch (b.Kind)
            {
                case ChibiProjKind.Arrow: c = ArrowColor; size = new Vector2(0.6f, 0.08f); trail = ult; break;      // Fighter.cs:536-540
                case ChibiProjKind.ArrowBig: c = ArrowColor; size = new Vector2(0.9f, 0.14f); trail = ult; break;
                case ChibiProjKind.Bolt:                                                                            // Fighter.cs:481-484
                case ChibiProjKind.SeekerOrb:   // temporary: magic bolt look until port step 13
                    sprite = Art.Circle; c = Color.Lerp(ult, Color.white, 0.3f); size = new Vector2(0.35f, 0.35f); break;
                case ChibiProjKind.BoltBig:
                    sprite = Art.Circle; c = Color.Lerp(ult, Color.white, 0.3f); size = new Vector2(0.55f, 0.55f); break;
                case ChibiProjKind.AuraWave: c = new Color(ult.r, ult.g, ult.b, 0.85f); size = new Vector2(0.25f, 1.2f); break;   // Fighter.cs:492-493
                case ChibiProjKind.Fireball:                                                                        // Fighter.cs:880-883
                    sprite = Art.Circle; c = FireballColor; size = new Vector2(0.55f, 0.55f); trail = FireballTrail; break;
                case ChibiProjKind.PoseFireball:
                    sprite = Art.Circle; c = PoseFireballColor; size = new Vector2(0.45f, 0.45f); break;
                case ChibiProjKind.Spear: c = SpearColor; size = new Vector2(1.5f, 0.1f); break;                    // Fighter.cs:940-941
                case ChibiProjKind.SmashWave: c = ult; size = new Vector2(0.5f, 1.1f); break;                       // Fighter.cs:999-1000
                case ChibiProjKind.VolleyLead: c = ult; size = new Vector2(1f, 0.18f); break;                       // Fighter.cs:1014-1015
                case ChibiProjKind.VolleyArrow: c = ArrowColor; size = new Vector2(0.55f, 0.07f); trail = ult; break;   // Fighter.cs:1025-1028
                default: sprite = Art.Circle; c = Color.white; size = new Vector2(0.35f, 0.35f); break;
            }
            v.Glow.color = new Color(c.r, c.g, c.b, ProjGlowAlpha);
            v.Glow.transform.localScale = new Vector3(size.x * ProjGlowScale, size.y * ProjGlowScale, 1f);
            v.Core.sprite = sprite;
            v.Core.color = c;
            v.Core.transform.localScale = new Vector3(size.x, size.y, 1f);
            v.Root.rotation = Quaternion.identity;   // CO:41 falls back to Vector3.right for a zero velocity
            v.Root.localScale = Vector3.one;
            v.Grow = b.Kind == ChibiProjKind.SmashWave;
            v.Trail = trail;
            v.TrailTimer = 0f;                       // first trail puff on the first drawn frame, as CO's trailTimer starts at 0
            v.SpawnFrame = b.SpawnFrame;
            v.Kind = b.Kind;
            v.Owner = b.Owner;
            if (!v.Live) { v.Live = true; v.Root.gameObject.SetActive(true); }
        }

        static void Play(FighterVisual v, Anim a, float d)
        {
            switch (a)
            {
                case Anim.Thrust: v.PlayThrust(d); break;
                case Anim.Spin: v.PlaySpin(d); break;
                case Anim.Slam: v.PlaySlam(d); break;
                case Anim.Recoil: v.PlayRecoil(d); break;
                default: v.PlaySwing(d); break;
            }
        }

        Vector2 Center(int i) => _vis[i] != null ? (Vector2)_vis[i].transform.position : Vector2.zero;

        void UpdateCamera()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            _cam.orthographic = true;
            float dt = Time.unscaledDeltaTime;
            Vector2 goal;
            float size;
            if (_focus >= 0 && Time.unscaledTime < _focusUntil)
            {
                goal = Center(_focus);                                       // Stage.cs:161-164
                size = CinematicFocusSize;
            }
            else
            {
                Vector2 a = Center(0), b = Center(1);
                goal = (a + b) * 0.5f;
                goal.y = Mathf.Max(goal.y, 0.5f);
                float dist = Mathf.Max(Mathf.Abs(a.x - b.x) * 0.5f / Mathf.Max(0.1f, _cam.aspect), Mathf.Abs(a.y - b.y) * 0.5f);
                size = Mathf.Clamp(dist + CamPad, CamMinSize, CamMaxSize);
            }
            var t = _cam.transform;
            Vector3 basePos = Vector3.Lerp(t.position, new Vector3(goal.x, goal.y, -10f), 1f - Mathf.Exp(-dt * CamLerp));
            basePos.z = -10f;
            _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, size, 1f - Mathf.Exp(-dt * CamSizeLerp));
            Vector3 offset = Vector3.zero;
            if (_shake > 0f)
            {
                offset = (Vector3)(FxRandom.InsideUnitCircle * _shake * ShakeScale);
                _shake = Mathf.MoveTowards(_shake, 0f, dt * ShakeDecay);
            }
            t.position = basePos + offset;
        }

        void OnDestroy()
        {
            if (_loop != null && _loop.Session != null) _loop.Session.OnFrame -= OnSimFrame;
            _events.Clear();
            _leadBurstPos.Clear();
            _leadBurstOwner.Clear();
            Fx.Popups.Clear();
        }
    }

    /// <summary>
    /// Spawns / tears down the sim-driven test match from code (the editor menu just calls these). While it runs,
    /// the real game (GameManager and any MatchController) is deactivated so nothing overlaps; Stop restores it.
    /// P1 = original game devices (ChibiPlayerInputSource.ForPlayer(0)), P2 = scripted idle training dummy.
    /// </summary>
    public static class ChibiSimTestMatch
    {
        const uint TestSeed = 12345;

        static GameObject _root;
        static readonly List<GameObject> Disabled = new List<GameObject>();
        static CameraController _camCtrl;
        static bool _camCtrlWasEnabled;
        static Vector3 _camPos;
        static float _camSize;
        static Color _camBg;
        static float _timeScale = 1f;
        static GameObject _createdCamera;   // "Main Camera" Start had to create; destroyed again in Stop

        /// <summary>True while a test match exists.</summary>
        public static bool IsRunning => _root != null;

        /// <summary>Player 1's input source (original game devices + gesture tracker) while running; else null.</summary>
        public static ChibiPlayerInputSource P1Input { get; private set; }

        /// <summary>Start the test match on <paramref name="stage"/> (no-op if already running). Play Mode only.</summary>
        /// <param name="stage">Index into StageBuilder.Names (0 Sky Dojo, 1 Neon City, 2 Lava Forge); clamped to range.</param>
        /// <returns>The root GameObject of the running test match.</returns>
        /// <example><code>ChibiSimTestMatch.Start(1);   // Neon City</code></example>
        public static GameObject Start(int stage = 0)
        {
            if (_root != null) return _root;

            Disabled.Clear();
            var match = Object.FindAnyObjectByType<MatchController>();
            if (match != null) Disable(match.gameObject);
            var gm = Object.FindAnyObjectByType<GameManager>();
            if (gm != null) Disable(gm.gameObject);

            var cam = Camera.main;
            if (cam != null)
            {
                _camPos = cam.transform.position;
                _camSize = cam.orthographicSize;
                _camBg = cam.backgroundColor;
                _camCtrl = cam.GetComponent<CameraController>();
                _camCtrlWasEnabled = _camCtrl != null && _camCtrl.enabled;
                if (_camCtrl != null) _camCtrl.enabled = false;
            }
            else
            {
                var go = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
                cam = go.GetComponent<Camera>();
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                _camCtrl = null;
                _createdCamera = go;
            }
            _timeScale = Time.timeScale;
            Time.timeScale = 1f;      // MatchController may have left hitstop / pause scaling on

            var cfg = new ChibiMatchConfig
            {
                Seed = TestSeed, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy, Passive0 = 0, Passive1 = 0,
                Stage = Mathf.Clamp(stage, 0, StageBuilder.Names.Length - 1)
            };
            _root = new GameObject("SimTestMatch");
            var loop = _root.AddComponent<ChibiLocalLoop>();
            P1Input = ChibiPlayerInputSource.ForPlayer(0);
            loop.Begin(cfg, P1Input, new ChibiScriptedInputSource(f => default), recordReplay: false);
            var view = _root.AddComponent<ChibiSimView>();
            view.Begin(loop, cfg);
            var hud = _root.AddComponent<ChibiSimTestHud>();
            hud.Bind(loop);
            return _root;
        }

        /// <summary>Destroy the test match and re-enable whatever Start deactivated.</summary>
        /// <example><code>ChibiSimTestMatch.Stop();</code></example>
        public static void Stop()
        {
            if (_root != null) Object.Destroy(_root);
            _root = null;
            P1Input = null;   // holds no disposable resources (pose sources / MoveNet singleton are not owned)

            MatchController restoredMatch = null;
            foreach (var go in Disabled)
            {
                if (go == null) continue;
                go.SetActive(true);
                var m = go.GetComponent<MatchController>();
                if (m != null) restoredMatch = m;
            }
            Disabled.Clear();

            if (restoredMatch != null)
            {
                // Fx is a static singleton; the test match re-pointed it at its own (now destroyed) particle system.
                var oldFx = restoredMatch.transform.Find("FX");
                if (oldFx != null) Object.Destroy(oldFx.gameObject);
                Fx.Init(restoredMatch.transform);
            }

            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = _camPos;
                cam.orthographicSize = _camSize;
                cam.backgroundColor = _camBg;
            }
            if (_camCtrl != null) _camCtrl.enabled = _camCtrlWasEnabled;
            _camCtrl = null;
            if (_createdCamera != null) Object.Destroy(_createdCamera);
            _createdCamera = null;
            Time.timeScale = _timeScale;
        }

        static void Disable(GameObject go)
        {
            if (!go.activeSelf) return;
            go.SetActive(false);
            Disabled.Add(go);
        }
    }
}
