using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Draws a ChibiLocalLoop's deterministic simulation with the original game visuals (FighterVisual, StageBuilder,
    /// Fx). Read-only: the view never writes to simulation state; the sim decides, the view only draws.
    /// Positions are interpolated between the last two ticks with loop.RenderAlpha; presentation events come from
    /// ChibiSimEvents, diffed once per simulated tick.
    /// </summary>
    public sealed class ChibiSimView : MonoBehaviour
    {
        // Visual mapping (presentation only). Sim Y = 0 is the floor; the fighter transform sits at the collider
        // centre used by Fighter.cs (collider 1.35 tall, offset -0.05), i.e. 0.725 above the feet.
        const float MilliToWorld = 1f / 1000f;
        const float FeetToPivot = 0.725f;
        const int StageIndex = 0;                 // Sky Dojo: halfWidth 8 == ChibiSimConst.StageHalfWidth
        const float StunVisualTicks = 18f;        // Fighter.cs shows the stun stars above 0.3 s of hitstun
        const float CamLerp = 6f, CamSizeLerp = 5f, CamMinSize = 5.5f, CamMaxSize = 10f, CamPad = 3.2f;
        const float ShakeOnHit = 0.25f, ShakeDecay = 2.5f, ShakeScale = 0.6f;

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
        SpriteRenderer[] _proj;
        StageInfo _stage;
        Camera _cam;
        float _shake;

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
            _stage = StageBuilder.Build(StageIndex, transform);

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

            var size = new Vector2(ChibiSimConst.ProjHalfW * 2 * MilliToWorld, ChibiSimConst.ProjHalfH * 2 * MilliToWorld);
            _proj = new SpriteRenderer[state.Proj.Length];
            for (int i = 0; i < _proj.Length; i++)
            {
                _proj[i] = Art.Sprite(transform, "SimProj" + i, Art.Circle, Color.white, Vector2.zero, size, 60);
                _proj[i].enabled = false;
            }

            _cam = Camera.main;
            loop.Session.OnFrame += OnSimFrame;
        }

        Vector3 WorldPos(int x, int y) => new Vector3(x * MilliToWorld, GroundY + FeetToPivot + y * MilliToWorld, 0f);

        void OnSimFrame(int frame, ChibiInputPacket a, ChibiInputPacket b)
        {
            var cur = _loop.Session.State;
            ChibiSimEvents.Diff(_seen, cur, _events);
            _before.CopyFrom(_seen);
            _seen.CopyFrom(cur);
        }

        void LateUpdate()
        {
            if (_loop == null || _loop.Session == null) return;
            float alpha = Mathf.Clamp01(_loop.RenderAlpha);

            for (int i = 0; i < _events.Count; i++) Apply(_events[i]);
            _events.Clear();

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
                int gmax = ChibiCombatStats.GuardMax10(f.Stats.Defense);
                if (f.Shielding && gmax > 0) guard = Mathf.Clamp01(f.GuardHp10 / (float)gmax);
                v.SetShield(f.Shielding || parry, parry, guard);
                v.SetAura(f.AuraTicks > 0 || f.RingTicks > 0, _skin[i].ultFx);
                v.SetStunned(f.Stun > StunVisualTicks);
            }

            for (int i = 0; i < _proj.Length; i++)
            {
                var b = _seen.Proj[i];
                var sr = _proj[i];
                if (!b.Active) { if (sr.enabled) sr.enabled = false; continue; }
                var a = _before.Proj[i];
                var p1 = new Vector3(b.X * MilliToWorld, GroundY + b.Y * MilliToWorld, 0f);
                if (a.Active && a.Owner == b.Owner)
                    p1 = Vector3.Lerp(new Vector3(a.X * MilliToWorld, GroundY + a.Y * MilliToWorld, 0f), p1, alpha);
                sr.transform.position = p1;
                if (!sr.enabled) sr.enabled = true;
            }

            Fx.Tick(Time.deltaTime);
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
                case ChibiSimEventKind.KO:
                    Fx.Burst(Center(e.Index), _skin[e.Index].ultFx, 40, 8f, 0.5f);
                    Fx.Text(Center(e.Index) + Vector2.up * 1.4f, "K.O.", Gui.Red, 1.5f);
                    break;
                case ChibiSimEventKind.ProjectileSpawned:
                {
                    int owner = Mathf.Clamp(e.Value, 0, 1);
                    _proj[e.Index].color = Color.Lerp(_def[owner].color, Color.white, 0.3f);
                    break;
                }
            }
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
            Vector2 a = Center(0), b = Center(1);
            Vector2 goal = (a + b) * 0.5f;
            goal.y = Mathf.Max(goal.y, 0.5f);
            float dist = Mathf.Max(Mathf.Abs(a.x - b.x) * 0.5f / Mathf.Max(0.1f, _cam.aspect), Mathf.Abs(a.y - b.y) * 0.5f);
            float size = Mathf.Clamp(dist + CamPad, CamMinSize, CamMaxSize);
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

        /// <summary>Start the test match (no-op if already running). Play Mode only.</summary>
        /// <example><code>ChibiSimTestMatch.Start();</code></example>
        public static GameObject Start()
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
                Seed = TestSeed, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy, Passive0 = 0, Passive1 = 0
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
