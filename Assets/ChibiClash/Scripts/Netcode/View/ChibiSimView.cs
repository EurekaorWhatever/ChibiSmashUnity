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
    /// set to the sim's current scale (hitstop 0.05, cinematic 0.2) and, after the final KO, to the
    /// <see cref="ChibiFeel.FinishTimeScale"/> slow-mo curve (with a camera zoom on the KO). The sim clock runs on
    /// Time.unscaledDeltaTime (ChibiLocalLoop.cs:54) and never reads Time.timeScale, so this only slows Fx,
    /// FighterVisual animation and anything else on scaled time.
    /// Game feel (all tunables in <see cref="ChibiFeel"/>): during the sim's own hitstop the hit fighter vibrates and
    /// flashes, heavy hits punch the camera in with a directional kick and a radial burst (<see cref="ChibiImpactFx"/>),
    /// camera shake is smooth trauma noise capped at <see cref="ChibiFeel.CamMaxOffset"/>.
    /// </summary>
    public sealed class ChibiSimView : MonoBehaviour
    {
        // Visual mapping (presentation only). Sim Y = 0 is the floor; the fighter transform sits at the collider
        // centre used by Fighter.cs (collider 1.35 tall, offset -0.05), i.e. 0.725 above the feet.
        const float MilliToWorld = 1f / 1000f;
        const float FeetToPivot = 0.725f;
        const float StunVisualTicks = 18f;        // Fighter.cs shows the stun stars above 0.3 s of hitstun
        const float CamMinSize = 5.5f, CamMaxSize = 10f, CamPad = 3.2f;
        // Critically damped springs (SmoothDamp, unscaled time): position / zoom / ult-focus smooth times in seconds.
        const float CamSmooth = 0.22f, CamSizeSmooth = 0.3f, CamFocusSmooth = 0.12f;
        // Classic framing keeps the centre >= ground + 3 (was the fixed 0.5 at ground -2.5); below the ground (Ocean
        // tiers, recoveries) it follows the lowest fighter + 2.275 instead.
        const float CamGroundLift = 3f, CamLowLift = 2.275f;
        // Launched fighter: lean toward the blast line (velocity x 0.3 s, max 4 u) and zoom out up to +1.2 at full
        // strength. Everyone else: a slight lead in the direction of motion (velocity x 0.12 s, max 1.2 u).
        const float CamLeadSec = 0.3f, CamLeadMax = 4f, CamLaunchZoom = 1.2f, CamMoveLeadSec = 0.12f, CamMoveLeadMax = 1.2f;
        // Ult cinematic, MatchController.Cinematic (MatchController.cs:184-193) and CameraController (Stage.cs:161-164).
        const float CinematicFocusSec = 0.8f, CinematicFocusSize = 3.6f, CinematicShake = 0.3f;
        const float CinematicFlash = 0.8f, FlashDecay = 2.5f;          // MatchController.cs:189 / :119
        // Finish slow-mo (view only; the sim has already stopped at MatchOver): ChibiFeel.FinishTimeScale / FinishZoom01.

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

        /// <summary>
        /// Signature sheet clip for non-program skills, slots 2..5 per class (null = keep the generic tween clip).
        /// Skill1 / Skill2 were drawn for these kits by tools/art/gen_cutout_anims.py (class_moves).
        /// </summary>
        static readonly string[,] SkillClip =
        {
            { "Skill1", "Skill2", null,     null },   // Kaia: Dash and Thrust, Parry, (Thrust Barrage: program)
            { null,     null,     null,     null },   // Bruna: Grab, Tornado, Smash are programs (see ProgramFx)
            { null,     "Skill2", null,     null },   // Ren: (Backstep: program), Choke
            { "Skill1", "Cast",   "Cast",   null },   // Lyra: magic projectile, minions, cloud
            { null,     null,     "Thrust", null },   // Taro: (Leap Slam, Spin Guard: programs), Throw Spear
        };

        IChibiSimFeed _loop;   // offline ChibiLocalLoop or online ChibiOnlineMatchRunner
        readonly ChibiSimState _before = new ChibiSimState();   // state one tick before _seen (interpolation start)
        readonly ChibiSimState _seen = new ChibiSimState();     // latest tick the view has processed
        readonly List<ChibiSimEvent> _events = new List<ChibiSimEvent>(32);
        readonly FighterVisual[] _vis = new FighterVisual[2];
        readonly Skin[] _skin = new Skin[2];
        readonly ClassDef[] _def = new ClassDef[2];
        ProjVis[] _proj;
        readonly bool[] _weaponHidden = new bool[2];   // last SpearOut applied to FighterVisual.SetWeaponVisible
        readonly Transform[] _ring = new Transform[2];   // FireRing visual per fighter (CombatObjects.cs:229-235), shown while RingTicks > 0
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
        static readonly Color GrappleColor = new Color(0.92f, 0.86f, 0.7f), GroundWaveDust = new Color(0.86f, 0.8f, 0.72f, 0.8f);

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
        // Mage minions / Thunder Cloud looks, CombatObjects.cs Minion.Spawn :126-147 and ThunderCloud.Spawn :184-194.
        sealed class MinionVis
        {
            public Transform Root;
            public SpriteRenderer Body;
            public bool Live;
            public int SpawnTick, Owner;
        }
        sealed class CloudVis
        {
            public Transform Root;
            public SpriteRenderer Bolt;
            public bool Live;
            public int SpawnTick, Owner;
        }
        MinionVis[] _minion;
        CloudVis[] _cloud;
        static readonly Color CloudGrey = new Color(0.35f, 0.35f, 0.45f);       // CO:186
        static readonly Color BoltColor = new Color(1f, 0.95f, 0.4f);           // CO:192 bolt, CO:211 strike burst
        const float BoltDrop = 3.2f;                                            // CO:211 burst at cloud + down * 3.2
        /// <summary>Minion / cloud bursts captured in OnSimFrame (positions from the states of that step), drawn in LateUpdate.</summary>
        struct PendingBurst { public Vector2 Pos; public Color Color; public int Count; public float Speed, Size; }
        readonly List<PendingBurst> _bursts = new List<PendingBurst>(16);
        StageInfo _stage;
        Camera _cam;

        // ---- Moveset presentation (rules v18 slots >= ChibiMovesetData.FirstNewSlot), captured per sim step in
        // OnSimFrame from (shown, now) so a rollback re-sim never fires twice, played in LateUpdate.
        struct NewMove { public int Fighter, Cls, Slot, Facing; public float Startup, Active, Total; }
        struct NewHit { public int Attacker, Cls, Slot; public Vector2 At; }
        struct KoFly { public int Fighter; public Vector2 From, Vel; }
        readonly List<NewMove> _newMoves = new List<NewMove>(8);
        readonly List<NewHit> _newHits = new List<NewHit>(8);
        readonly List<KoFly> _koFly = new List<KoFly>(2);
        readonly Color[] _fx = new Color[2];          // CharacterRoster Fx colour per fighter (move VFX, popups, trails)
        readonly Color[] _accent = new Color[2];
        ChibiMoveVfx _moveFx;
        ChibiLaunchFx _launchFx;   // launch trails, KO blasts, respawn hover / blink (presentation only)
        int _stageIndex;           // camera bounds are read from ChibiStageTable every frame (KO lines may move)
        Vector2 _camGoal;          // last framing goal / size, kept while nobody is in play
        float _camGoalSize;
        bool _camGoalSet;
        Vector2 _camBase, _camVel; // spring state: camera centre without shake, and its velocity
        float _camSizeVel;
        bool _camBaseSet;
        bool _victoryShown;
        // Moveset zones (rune fields): one ring + glow per pool slot, built once.
        sealed class ZoneVis { public Transform Root; public SpriteRenderer Ring, Glow; public bool Live; public int SpawnTick, Owner; }
        ZoneVis[] _zones;
        ChibiImpactFx _impactFx;   // silhouette flash, radial burst, speed lines, dust (presentation only)
        readonly ChibiCameraFeel _camFeel = new ChibiCameraFeel();   // trauma shake, punch-in, directional kick
        float _camSize;            // ortho-size spring state (without the punch-in)
        bool _camSizeSet;
        // Hitstop decoration: strength of the last hit per fighter (0 = none) and the sim hitstop length it started with.
        readonly float[] _hitShakeS = new float[2];
        int _hitStopTotal = 1;
        Vector2 _finishFocus;      // final-KO camera focus, captured on the first frame after MatchOver
        bool _finishFocusSet;
        // Cinematic band strings per fighter, built once in Begin (no per-ult string concatenation).
        readonly string[] _cinTitle = new string[2], _cinSub = new string[2];
        int _focus = -1;          // fighter the camera is focused on during the ult cinematic, -1 none
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

        /// <summary>World position of fighter <paramref name="i"/>'s body centre (name tags, popups). Zero before Begin.</summary>
        public Vector2 FighterCenter(int i) => i >= 0 && i < 2 ? Center(i) : Vector2.zero;

        /// <summary>True once the view has seen MatchOver; <see cref="FinishedTime"/> is the unscaled time it did.</summary>
        public bool Finished => _finished;
        /// <summary>Time.unscaledTime at which MatchOver was first shown (results appear 1.3 s later, Hud.cs:62).</summary>
        public float FinishedTime => _finishedTime;
        /// <summary>The drawn stage (name, ground height).</summary>
        public StageInfo Stage => _stage;

        /// <summary>Build the visuals for <paramref name="loop"/> (already begun). Call once.</summary>
        /// <param name="skins">Per-player skins (the profile's equipped skins); null = class defaults (test match).</param>
        /// <param name="weapons">Per-player weapon variants; null = variant 0.</param>
        /// <example><code>view.Begin(loop, cfg);</code></example>
        public void Begin(IChibiSimFeed loop, in ChibiMatchConfig cfg, Skin[] skins = null, WeaponDef[] weapons = null)
        {
            _loop = loop;
            var state = loop.State;
            _before.CopyFrom(state);
            _seen.CopyFrom(state);

            Fx.Init(transform);
            // Stage chosen by cfg.Stage (0..2 classic, 3..7 the map design sheet); sim Y 0 maps to _stage.groundY.
            _stage = StageBuilder.Build(cfg.Stage, transform);
            // Design stages move platforms and play stage events from the displayed sim state (read-only).
            if (_stage.design != null) _stage.design.Bind(() => _loop != null ? _loop.State : null);
            // Cinematic post FX (bloom, stage grade, vignette, KO / ult punches); off when the player disabled it.
            ChibiPostFx.Attach(transform, cfg.Stage, () => _loop != null ? _loop.State : null);
            ChibiBladeTrails.Attach(transform, GroundY, () => _loop != null ? _loop.State : null);   // [blade-trails hook] HDR blade ribbons, hit sparks, muzzle flares, afterimages (reads state only)

            for (int i = 0; i < 2; i++)
            {
                var cls = i == 0 ? cfg.Class0 : cfg.Class1;
                _def[i] = ClassDef.Get(cls);
                _skin[i] = skins != null && skins[i] != null ? skins[i] : Skin.Default(cls);   // test match: defaults
                var go = new GameObject("SimP" + (i + 1));
                go.transform.SetParent(transform, false);
                _vis[i] = go.AddComponent<FighterVisual>();
                _vis[i].Build(_def[i], _skin[i], 10 + i * 20, weapons != null && weapons[i] != null ? weapons[i] : WeaponDef.Get(cls, 0));
                _vis[i].EnableSheetOnly();   // fight: moveset-sheet clips only (FightClips), never cutouts or the body drawing
                var ch = CharacterRoster.Of(cls);
                _fx[i] = ch.Fx;
                _accent[i] = ch.Accent;
                _vis[i].SnapFacing(state.F[i].Facing >= 0 ? 1 : -1);   // spawn: no turn animation
                go.transform.position = WorldPos(state.F[i].X, state.F[i].Y);

                // FireRing.Spawn (CombatObjects.cs:229-235): soft glow + ring sprite, built once and toggled by RingTicks.
                var ring = new GameObject("SimFireRing" + (i + 1)).transform;
                ring.SetParent(transform, false);
                Art.Sprite(ring, "Glow", Art.Soft, RingGlow, Vector2.zero, new Vector2(5f, 5f), 5);
                Art.Sprite(ring, "Ring", Art.Ring, RingColor, Vector2.zero, new Vector2(4f, 4f), 46);
                ring.gameObject.SetActive(false);
                _ring[i] = ring;
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

            // Minion and cloud pools, one sprite set per sim slot, built once (like ProjVis).
            _minion = new MinionVis[state.Minions.Length];
            for (int i = 0; i < _minion.Length; i++)
            {
                var root = new GameObject("SimMinion" + i).transform;
                root.SetParent(transform, false);
                var v = new MinionVis { Root = root };
                v.Body = Art.Sprite(root, "Body", Art.Circle, Color.white, Vector2.zero, new Vector2(0.5f, 0.45f), 40);
                Art.Sprite(root, "Eye", Art.Circle, Color.white, new Vector2(0.08f, 0.05f), new Vector2(0.14f, 0.14f), 41);
                Art.Sprite(root, "Pupil", Art.Circle, Color.black, new Vector2(0.1f, 0.05f), new Vector2(0.07f, 0.07f), 42);
                root.gameObject.SetActive(false);
                _minion[i] = v;
            }
            _cloud = new CloudVis[state.Clouds.Length];
            for (int i = 0; i < _cloud.Length; i++)
            {
                var root = new GameObject("SimCloud" + i).transform;
                root.SetParent(transform, false);
                var v = new CloudVis { Root = root };
                Art.Sprite(root, "C1", Art.Circle, CloudGrey, new Vector2(-0.4f, 0f), new Vector2(0.8f, 0.6f), 45);
                Art.Sprite(root, "C2", Art.Circle, CloudGrey, new Vector2(0.1f, 0.15f), new Vector2(0.9f, 0.75f), 45);
                Art.Sprite(root, "C3", Art.Circle, CloudGrey, new Vector2(0.5f, -0.05f), new Vector2(0.7f, 0.55f), 45);
                v.Bolt = Art.Sprite(root, "Bolt", Art.Square, BoltColor, new Vector2(0f, -1.6f), new Vector2(0.14f, 3.2f), 44);
                v.Bolt.enabled = false;
                root.gameObject.SetActive(false);
                _cloud[i] = v;
            }

            _zones = new ZoneVis[state.Zones.Length];
            for (int i = 0; i < _zones.Length; i++)
            {
                var root = new GameObject("SimZone" + i).transform;
                root.SetParent(transform, false);
                var z = new ZoneVis { Root = root };
                z.Glow = Art.Sprite(root, "Glow", Art.Soft, Color.clear, Vector2.zero, Vector2.one, 7);
                z.Ring = Art.Sprite(root, "Ring", Art.Ring, Color.clear, Vector2.zero, Vector2.one, 8);
                root.gameObject.SetActive(false);
                _zones[i] = z;
            }
            _moveFx = new ChibiMoveVfx(transform, _vis);
            ChibiImpactFx.WarmShared();   // also ChibiLaunchFx.WarmShared: textures + light materials before the first fight frame
            _launchFx = new ChibiLaunchFx(transform, _vis, _fx, GroundY, FeetToPivot);
            _impactFx = new ChibiImpactFx(transform, _vis, _fx, GroundY);
            _stageIndex = cfg.Stage;
            for (int i = 0; i < 2; i++)
            {
                // Hud.cs:186, :192-194; built once here, not per cinematic.
                var def = _def[i];
                _cinTitle[i] = _skin[i].goldCinematic ? "★ " + def.ultName + " ★" : def.ultName;
                _cinSub[i] = def.heroName.ToUpper() + " · " + def.ultDesc;
            }

            _cam = Camera.main;
            loop.OnFrame += OnSimFrame;
            ChibiAudio.BeginMatch(loop);   // [audio hook] SFX player binds this match (presentation only)
        }

        Vector3 WorldPos(int x, int y) => new Vector3(x * MilliToWorld, GroundY + FeetToPivot + y * MilliToWorld, 0f);
        Vector3 ProjPos(int x, int y) => new Vector3(x * MilliToWorld, GroundY + y * MilliToWorld, 0f);

        void OnSimFrame(int frame, ChibiInputPacket a, ChibiInputPacket b)
        {
            var cur = _loop.State;
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
            ChibiAudio.OnSimStep(_seen, cur, _events, first);   // [audio hook] reads prev/cur + this step's events, before _seen moves on
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
            // Minion / cloud bursts: positions come from this step's states (a gone minion's last position is only in
            // _seen; a slot may be reused before LateUpdate runs), so capture them here.
            for (int i = first; i < _events.Count; i++)
            {
                var e = _events[i];
                var ult = _skin[Mathf.Clamp(e.Value, 0, 1)].ultFx;
                switch (e.Kind)
                {
                    case ChibiSimEventKind.MinionSpawned:   // Minion.Spawn CO:147
                    {
                        var m = cur.Minions[e.Index];
                        AddBurst(ProjPos(m.X, m.Y), ult, 10, 3f, 0.3f);
                        break;
                    }
                    case ChibiSimEventKind.MinionGone:      // Minion.Update despawn CO:157
                    {
                        var m = _seen.Minions[e.Index];
                        AddBurst(ProjPos(m.X, m.Y), ult, 8, 2f, 0.25f);
                        break;
                    }
                    case ChibiSimEventKind.CloudStrike:     // ThunderCloud.Update strike CO:211 (no Shake in the original)
                    {
                        var c = cur.Clouds[e.Index];
                        AddBurst((Vector2)ProjPos(c.MilliX, c.MilliY) + Vector2.down * BoltDrop, BoltColor, 12, 4f, 0.25f);
                        break;
                    }
                }
            }
            CaptureMoveset(cur, first);
            _launchFx.OnSimStep(_seen, cur);   // launches / KOs / respawns of this step (reads both states)
            _impactFx.OnSimStep(_seen, cur);   // dashes / landings / take-offs / bounces of this step (reads both states)
            _before.CopyFrom(_seen);
            _seen.CopyFrom(cur);
        }

        /// <summary>
        /// This step's moveset starts and hits, and ring-out flights, with positions from the states of the step
        /// (_seen = shown before, cur = now). View only: reads both states, writes neither.
        /// </summary>
        void CaptureMoveset(ChibiSimState cur, int first)
        {
            int firstNew = ChibiMovesetData.FirstNewSlot;
            for (int k = first; k < _events.Count; k++)
            {
                var e = _events[k];
                switch (e.Kind)
                {
                    case ChibiSimEventKind.MoveStarted:
                    {
                        if (e.Value < firstNew) break;
                        var f = cur.F[e.Index];
                        float tr = ChibiSimConst.TickRate;
                        _newMoves.Add(new NewMove
                        {
                            Fighter = e.Index, Cls = f.Class, Slot = e.Value, Facing = f.Facing >= 0 ? 1 : -1,
                            Startup = f.MoveStartup / tr, Active = f.MoveActive / tr,
                            Total = (f.MoveStartup + f.MoveActive + f.MoveRecovery) / tr,
                        });
                        break;
                    }
                    case ChibiSimEventKind.Damaged:
                    {
                        int d = e.Index, a = ChibiSimView.Attacker(cur, d);
                        int slot = NewMoveSlotOfHit(cur, a);
                        if (slot < firstNew) break;
                        var fd = cur.F[d];
                        float side = cur.F[a].X >= fd.X ? 1f : -1f;
                        Vector2 at = (Vector2)WorldPos(fd.X, fd.Y) + new Vector2(side * 0.35f, 0.25f);
                        _newHits.Add(new NewHit { Attacker = a, Cls = cur.F[a].Class, Slot = slot, At = at });
                        break;
                    }
                    case ChibiSimEventKind.RingOut:
                    case ChibiSimEventKind.KO:   // rules v20: every KO is a blast-line KO (ChibiSimEvents emits KO per stock)
                    {
                        // The respawned state is already in cur; the flight starts where the fighter left (_seen).
                        var f = _seen.F[e.Index];
                        var vel = new Vector2(f.Vx, f.Vy) * (ChibiSimConst.TickRate * MilliToWorld / ChibiSimConst.VelSub);
                        _koFly.Add(new KoFly { Fighter = e.Index, From = WorldPos(f.X, f.Y), Vel = vel });
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Moveset slot that landed a hit on fighter 1 - <paramref name="a"/> this step: the attacker's current (or just
        /// ended) move, else a moveset projectile of the attacker that hit or vanished this step; -1 if none.
        /// </summary>
        int NewMoveSlotOfHit(ChibiSimState cur, int a)
        {
            int firstNew = ChibiMovesetData.FirstNewSlot;
            if (cur.F[a].MoveSlot >= firstNew) return cur.F[a].MoveSlot;
            if (_seen.F[a].MoveSlot >= firstNew) return _seen.F[a].MoveSlot;
            int n = Mathf.Min(cur.Proj.Length, _seen.Proj.Length);
            for (int i = 0; i < n; i++)
            {
                var p = _seen.Proj[i];
                if (!p.Active || p.Owner != a || p.SrcSlot < firstNew) continue;
                var c = cur.Proj[i];
                bool gone = !c.Active || !ChibiSimEvents.SameProjectile(p, c);
                bool hitNow = !gone && (c.Flags & ChibiProjectileSim.FHasHit) != 0 && (p.Flags & ChibiProjectileSim.FHasHit) == 0;
                if (gone || hitNow) return p.SrcSlot;
            }
            return -1;
        }

        /// <summary>Plays the moves / hits / ring-out flights captured this frame (LateUpdate, after Apply).</summary>
        void PlayMoveset()
        {
            for (int k = 0; k < _newMoves.Count; k++)
            {
                var m = _newMoves[k];
                var info = ChibiMoveInfo.Get(m.Cls, m.Slot);
                var v = _vis[m.Fighter];
                // AN-1 sheet poses (strike frame on the first active tick), else the kind clip
                v.PlayMoveClip(info.Id, ChibiMoveFxTable.ClipFor(info.AnimKind), m.Startup, m.Active, m.Total - m.Startup - m.Active);
                if (info.Valid && !string.IsNullOrEmpty(info.SfxStart)) ChibiAudio.Play(info.SfxStart, m.Fighter);   // [audio hook] move start
                ChibiMoveFxTable.Get(m.Cls, m.Slot, out var fa, out var fb, out bool dual, out bool accent);
                var c = accent ? _accent[m.Fighter] : _fx[m.Fighter];
                _moveFx.PlayMove(m.Fighter, fa, c, m.Facing, m.Startup, m.Active, dual);
                if (fb != MoveFx.None) _moveFx.PlayMove(m.Fighter, fb, _fx[m.Fighter], m.Facing, m.Startup, m.Active, dual);
            }
            _newMoves.Clear();

            for (int k = 0; k < _newHits.Count; k++)
            {
                var h = _newHits[k];
                var info = ChibiMoveInfo.Get(h.Cls, h.Slot);
                if (!info.Valid) continue;
                if (!string.IsNullOrEmpty(info.SfxHit)) ChibiAudio.Play(info.SfxHit, h.Attacker);                  // [audio hook] move hit
                var c = _fx[h.Attacker];
                _moveFx.PlayHit(h.At, c);
                Fx.Burst(h.At, c, 10, 5f, 0.25f);
                // Comic onomatopoeia from the sheet ("KRA-THUD!") above the damage number; the HUD pops and fades it.
                if (!string.IsNullOrEmpty(info.Onomatopoeia))
                    Fx.Comic(h.At + new Vector2(0f, 0.45f), info.Onomatopoeia, c, OnomatopoeiaSize);
            }
            _newHits.Clear();

            for (int k = 0; k < _koFly.Count; k++)
            {
                var f = _koFly[k];
                _vis[f.Fighter].SpawnKoGhost(f.From, f.Vel);
            }
            _koFly.Clear();
        }

        const float OnomatopoeiaSize = 1.45f;

        /// <summary>Moveset rune fields: a flat ring + glow on the floor under each live zone, in the owner's colour.</summary>
        void UpdateZones()
        {
            var zs = _seen.Zones;
            for (int i = 0; i < _zones.Length && i < zs.Length; i++)
            {
                var z = zs[i];
                var v = _zones[i];
                if (!z.Active)
                {
                    if (v.Live) { v.Live = false; v.Root.gameObject.SetActive(false); }
                    continue;
                }
                if (!v.Live || v.SpawnTick != z.SpawnTick || v.Owner != z.Owner)
                {
                    v.Live = true; v.SpawnTick = z.SpawnTick; v.Owner = z.Owner;
                    v.Root.gameObject.SetActive(true);
                }
                var c = _fx[Mathf.Clamp(z.Owner, 0, 1)];
                float w = Mathf.Max(0.3f, (z.X1 - z.X0) * MilliToWorld);
                float pulse = 0.8f + 0.2f * Mathf.Sin(Time.time * 6f + i);
                v.Root.position = ProjPos((z.X0 + z.X1) / 2, z.Y0) + new Vector3(0f, 0.05f, 0f);
                v.Ring.transform.localScale = new Vector3(w, w * 0.22f, 1f);
                v.Ring.color = new Color(c.r, c.g, c.b, 0.85f * pulse);
                v.Glow.transform.localScale = new Vector3(w * 1.1f, w * 0.3f, 1f);
                v.Glow.color = new Color(c.r, c.g, c.b, 0.35f * pulse);
            }
        }

        /// <summary>Presentation time scale for this frame, mirroring MatchController.cs:111-116.</summary>
        float ViewTimeScale(ChibiSimState live)
        {
            // MatchController.cs:114 overrides hitstop/cinematic once the match is finished. The sim stops stepping
            // at MatchOver (its timers freeze), so its scale is ignored from then on.
            if (_finished) return ChibiFeel.FinishTimeScale(Time.unscaledTime - _finishedTime);
            return ChibiSimulation.CurrentScalePermille(live) / 1000f;   // 1 / 0.2 / 0.05, the same values as :112-113
        }

        void Update()
        {
            if (_loop == null || _loop.State == null) return;
            // Offline pause freezes presentation too (MC:109 timeScale 0); restored by ChibiSimTestMatch.Stop / ChibiMatch.
            Time.timeScale = _loop is ChibiLocalLoop l && l.Paused ? 0f : ViewTimeScale(_loop.State);
        }

        void LateUpdate()
        {
            if (_loop == null || _loop.State == null) return;
            var live = _loop.State;
            // timescale-conversion.md 8.3(2): f = min(0.999, (sinceStep + RenderAlpha) * scale / 1000). NOT TimeAccum,
            // which keeps the carried phase and would stall every tick after the first hitstop. After MatchOver the
            // sim no longer steps, so show the final state.
            float alpha = live.MatchOver ? 1f
                : Mathf.Min(0.999f, (live.RealTicksSinceStep + Mathf.Clamp01(_loop.RenderAlpha))
                                    * ChibiSimulation.CurrentScalePermille(live) / 1000f);

            for (int i = 0; i < _events.Count; i++) Apply(_events[i]);
            _events.Clear();
            PlayMoveset();
            if (!_victoryShown && _seen.MatchOver)
            {
                _victoryShown = true;
                if (_seen.Winner >= 0 && _seen.Winner < 2) _vis[_seen.Winner].SetVictory(true);
            }
            for (int i = 0; i < _leadBurstPos.Count; i++)
                Fx.Burst(_leadBurstPos[i], _skin[_leadBurstOwner[i]].ultFx, LeadBurstCount, LeadBurstSpeed, LeadBurstSize);
            _leadBurstPos.Clear();
            _leadBurstOwner.Clear();
            for (int i = 0; i < _bursts.Count; i++)
            {
                var pb = _bursts[i];
                Fx.Burst(pb.Pos, pb.Color, pb.Count, pb.Speed, pb.Size);
            }
            _bursts.Clear();

            for (int i = 0; i < 2; i++)
            {
                var a = _before.F[i];
                var f = _seen.F[i];
                var v = _vis[i];
                bool teleport = System.Math.Abs(f.X - a.X) > 2000 || System.Math.Abs(f.Y - a.Y) > 2000;   // respawn
                Vector3 p0 = WorldPos(a.X, a.Y), p1 = WorldPos(f.X, f.Y);
                Vector3 shown = teleport ? p1 : Vector3.Lerp(p0, p1, alpha);
                // Impact freeze decoration: the sim's hitstop already freezes the world (time scale 0.05); the fighter
                // that was hit vibrates sideways for exactly that freeze, fading out as it ends (never moves the sim).
                if (_hitShakeS[i] > 0f)
                {
                    if (live.HitstopRealTicks > 0)
                        shown.x += ChibiFeel.HitShake(_hitShakeS[i], live.HitstopRealTicks / (float)_hitStopTotal, Time.unscaledTime);
                    else _hitShakeS[i] = 0f;
                }
                v.transform.position = shown;
                v.SetFacing(f.Facing >= 0 ? 1 : -1);

                // Sim velocities are sub-units (1/VelSub milli per tick): convert to world units per second.
                var vel = new Vector2(f.Vx, f.Vy) * (ChibiSimConst.TickRate * MilliToWorld / ChibiSimConst.VelSub);
                v.SetAlpha(_launchFx.BodyAlpha(i));   // hidden while KO'd, blinking during respawn invulnerability
                // Fight-mode clip facts, read-only from the shown state (FightClips.StateOf picks the sheet clip).
                var ff = FightFlags.None;
                if (f.Stun > 0) ff |= FightFlags.Hitstun;
                // Tumble from sim data only (hitstun, airborne, launch gravity / speed), never from view FX state.
                if (ChibiFeel.IsTumble(f.Stun, f.Grounded, f.InPlay, f.LaunchGravP, f.Vx, f.Vy)) ff |= FightFlags.Launched;
                if (f.OutTicks > 0) ff |= FightFlags.Out;
                if (f.HoverTicks > 0) ff |= FightFlags.Hover;
                if (_seen.IntroRealTicks > 0) ff |= FightFlags.Intro;
                v.Tick(vel, f.Grounded, f.Shielding, ff);

                bool parry = f.ParryTicks > 0;
                float guard = 1f;
                int gmax = ChibiCombatStats.GuardMaxU(f.Stats.Defense);
                if (f.Shielding && gmax > 0) guard = Mathf.Clamp01(f.GuardU / (float)gmax);
                v.SetShield(f.Shielding || parry, parry, guard);
                v.SetAura(f.AuraTicks > 0, _skin[i].ultFx);   // Sword Aura only (F:195); Fire Ring draws its own ring
                UpdateFireRing(i, f.RingTicks > 0, f.Facing >= 0 ? 1 : -1);
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

            UpdateMinionsAndClouds(alpha);
            UpdateZones();
            _moveFx.Tick(Time.deltaTime);   // scaled, like FighterVisual and Fx
            _launchFx.Tick(Time.deltaTime);
            _impactFx.Tick(Time.deltaTime, Time.unscaledDeltaTime);   // speed lines / dust scaled (hitstop freezes them), flash + burst unscaled
            _camFeel.AddTrauma(_launchFx.TakeShake());
            if (_launchFx.TakeFlash(out var koFlashColor, out float koFlash) && koFlash > FlashAlpha)
            {
                FlashAlpha = koFlash;
                FlashColor = koFlashColor;
            }

            Fx.Tick(Time.deltaTime);   // scaled, like MatchController.cs:118
            FlashAlpha = Mathf.MoveTowards(FlashAlpha, 0f, Time.unscaledDeltaTime * FlashDecay);   // MatchController.cs:119
            UpdateCamera();
            // Design stages: after the camera (parallax), with the fighters' own game-step alpha (moving platforms, hazards).
            if (_stage != null && _stage.design != null) _stage.design.Sync(alpha);
        }

        void AddBurst(Vector2 pos, Color c, int count, float speed, float size) =>
            _bursts.Add(new PendingBurst { Pos = pos, Color = c, Count = count, Speed = speed, Size = size });

        /// <summary>
        /// Minion and cloud pools: show live slots, interpolate only while the slot holds the same occupant
        /// (SameMinion / SameCloud, TD amendment 5), else snap. Read-only on sim state.
        /// </summary>
        void UpdateMinionsAndClouds(float alpha)
        {
            for (int i = 0; i < _minion.Length; i++)
            {
                var b = _seen.Minions[i];
                var v = _minion[i];
                if (!b.Active)
                {
                    if (v.Live) { v.Live = false; v.Root.gameObject.SetActive(false); }
                    continue;
                }
                if (!v.Live || v.SpawnTick != b.SpawnTick || v.Owner != b.Owner)
                {
                    v.Live = true; v.SpawnTick = b.SpawnTick; v.Owner = b.Owner;
                    // CO:132 body = Lerp(ultFx, black, 0.2); eye and pupil are fixed colours.
                    v.Body.color = Color.Lerp(_skin[Mathf.Clamp(b.Owner, 0, 1)].ultFx, Color.black, 0.2f);
                    v.Root.gameObject.SetActive(true);
                }
                var a = _before.Minions[i];
                var p = ProjPos(b.X, b.Y);
                if (a.Active && ChibiSimEvents.SameMinion(a, b)) p = Vector3.Lerp(ProjPos(a.X, a.Y), p, alpha);
                v.Root.position = p;
                v.Root.localScale = new Vector3(b.Facing >= 0 ? 1f : -1f, 1f, 1f);   // CO:161 eye/scale flip
            }

            for (int i = 0; i < _cloud.Length; i++)
            {
                var b = _seen.Clouds[i];
                var v = _cloud[i];
                if (!b.Active)
                {
                    if (v.Live) { v.Live = false; v.Root.gameObject.SetActive(false); }
                    continue;
                }
                if (!v.Live || v.SpawnTick != b.SpawnTick || v.Owner != b.Owner)
                {
                    v.Live = true; v.SpawnTick = b.SpawnTick; v.Owner = b.Owner;
                    v.Root.gameObject.SetActive(true);
                }
                var a = _before.Clouds[i];
                var p = ProjPos(b.MilliX, b.MilliY);
                if (a.Active && ChibiSimEvents.SameCloud(a, b)) p = Vector3.Lerp(ProjPos(a.MilliX, a.MilliY), p, alpha);
                v.Root.position = p;
                // TD amendment 6: the original enables the bolt from the frame after the strike (CO:206-207).
                bool bolt = b.BoltTicks > 0 && b.BoltTicks < ChibiSimConst.CloudBolt;
                if (v.Bolt.enabled != bolt) v.Bolt.enabled = bolt;
            }
        }

        void Apply(in ChibiSimEvent e)
        {
            switch (e.Kind)
            {
                case ChibiSimEventKind.MoveStarted:
                {
                    var f = _seen.F[e.Index];
                    var v = _vis[e.Index];
                    if (e.Value >= ChibiMovesetData.FirstNewSlot) break;   // moveset rows: PlayMoveset (captured per step)
                    // Fight mode: the Move_ sheet clip standing in for this pre-v18 slot (the cutout Play* calls below are ignored).
                    v.PlayLegacyMove(e.Value, f.MoveStartup / (float)ChibiSimConst.TickRate, f.MoveActive / (float)ChibiSimConst.TickRate,
                                     f.MoveRecovery / (float)ChibiSimConst.TickRate);
                    if (e.Value <= 1)
                    {
                        float dur = (f.MoveStartup + f.MoveActive + f.MoveRecovery) / (float)ChibiSimConst.TickRate;
                        v.PlayNormal(e.Value == 1, e.Value2, dur);
                    }
                    else if (e.Value == ChibiSimConst.PoseSlot)
                    {
                        // Pose Fireball (F:386-390): recoil and the pose label; the shot itself is a PoseFireball projectile.
                        v.PlayRecoil(0.25f);
                        Fx.Text(Center(e.Index) + Vector2.up * 1.2f, "POSE: FIREBALL", new Color(1f, 0.6f, 0.2f), 0.7f);
                    }
                    else
                    {
                        int cls = Mathf.Clamp(f.Class, 0, SkillAnim.GetLength(0) - 1);
                        int col = Mathf.Clamp(e.Value - 2, 0, SkillAnim.GetLength(1) - 1);
                        // Program moves animate per frame from ProgramFrame (their frame 0 follows in the same diff).
                        if (!ChibiSimEvents.IsProgram(f.Class, e.Value))
                        {
                            Play(v, SkillAnim[cls, col], SkillDur[cls, col]);
                            // Signature sheet clip where the character has one for this skill (natural speed, locked
                            // against the generic tween clips); the procedural tween above still drives the fallback.
                            var sig = SkillClip[cls, col];
                            if (sig != null) v.PlayClipLocked(sig, 0f);
                        }
                        if (e.Value >= 2 && e.Value <= 4)
                        {
                            var names = _def[e.Index].skillNames;
                            int k = e.Value - 2;
                            if (names != null && k < names.Length) Fx.Text(Center(e.Index) + Vector2.up * 1.25f, names[k], _def[e.Index].color, 0.75f);
                        }
                        // Slot 5: the common FireUltimate burst (F:962) is in CinematicStarted, which every ult emits.
                    }
                    break;
                }
                case ChibiSimEventKind.Damaged:
                    _vis[e.Index].Flash();
                    if (_seen.F[e.Index].MoveSlot < 0) _vis[e.Index].Hurt();   // Hit sheet (or the recoil squash)
                    Fx.Burst(Center(e.Index), Color.white, 12, 5f, 0.25f);
                    Fx.Text(Center(e.Index) + Vector2.up * 0.9f, DamageText(e.Value), Gui.Yellow, 1f);
                    OnHitFeel(e.Index, e.Value);
                    break;
                case ChibiSimEventKind.Parried:
                    Fx.Text(Center(e.Index) + Vector2.up * 1.2f, "PARRY", Color.white, 1.1f);
                    break;
                case ChibiSimEventKind.RingOut:
                    Fx.Text(Center(e.Index) + Vector2.up * 1.2f, "RING OUT", Gui.Red, 1.1f);
                    break;
                case ChibiSimEventKind.Revived:
                    Fx.Text(Center(e.Index) + Vector2.up * 1.5f, "REVIVED", Color.white, 1f);   // MC:160
                    break;
                case ChibiSimEventKind.PoseSuperJump:
                {
                    // Super Jump pose (F:380-381): burst at the feet and the pose label.
                    var f = _seen.F[e.Index];
                    var feet = new Vector2(f.X * MilliToWorld, GroundY + f.Y * MilliToWorld);
                    Fx.Burst(feet, new Color(0.6f, 0.9f, 1f), 18, 5f, 0.35f);
                    Fx.Text(Center(e.Index) + Vector2.up * 1.2f, "POSE: SUPER JUMP", new Color(0.6f, 0.9f, 1f), 0.7f);
                    break;
                }
                case ChibiSimEventKind.DashStarted:
                    Fx.Burst(Center(e.Index), _skin[e.Index].ultFx, 8, 3f, 0.3f);   // Fighter.cs dash start
                    break;
                case ChibiSimEventKind.EscapeDash:   // rules v24 (ED-1): broke out of a launch
                    Fx.Burst(Center(e.Index), _skin[e.Index].ultFx, 16, 5f, 0.35f);
                    Fx.Text(Center(e.Index) + Vector2.up * 1.2f, "ESCAPE!", Color.white, 0.9f);
                    break;
                case ChibiSimEventKind.KO:
                    // Rules v20: the KO'd body is already off-screen (out / respawn); the KO blast at the blast line comes from
                    // ChibiLaunchFx and the flying ghost from the KoFly above, so no burst / text at the (hidden) body here.
                    _vis[e.Index].PlayClipLocked("KO", 0f);
                    break;
                case ChibiSimEventKind.CinematicStarted:
                    StartCinematic(e.Index);
                    _vis[e.Index].PlayClipLocked("Ultimate", 0f);   // the character's ultimate sheet; program tweens cannot replace it
                    // FireUltimate common burst (F:962). Here, not in MoveStarted: Sword Aura / Fire Ring start with MoveSlot -1.
                    Fx.Burst(Center(e.Index), _skin[e.Index].ultFx, 40, 8f, 0.5f);
                    break;
                case ChibiSimEventKind.ProgramFrame:
                    ProgramFx(e.Index, e.Value, e.Value2, e.On);
                    break;
                case ChibiSimEventKind.MovePhaseChanged:
                    // Leap Slam landed (F:911-915). Grab's phase 1 (connect) has no effect of its own in the original.
                    if (_seen.F[e.Index].Class == (int)FighterClass.Polearm && e.Value2 == 2 && e.Value == 1)
                    {
                        var f = _seen.F[e.Index];
                        _vis[e.Index].PlaySlam(0.2f);
                        var feet = new Vector2(f.X * MilliToWorld + f.Facing * 1.2f, GroundY + f.Y * MilliToWorld);
                        Fx.Burst(feet, LeapDust, 18, 5f, 0.35f);
                        _camFeel.AddTrauma(0.25f);                    // Match.Shake(0.25f)
                    }
                    else if (_seen.F[e.Index].Class == (int)FighterClass.Heavy && e.Value2 == 5 && e.Value == 1)
                    {
                        // ULT Smash landed (F:993, F:1006-1007): slam pose, burst at the feet (Center.x, col.bounds.min.y).
                        var f = _seen.F[e.Index];
                        _vis[e.Index].PlaySlam(0.2f);
                        var feet = new Vector2(f.X * MilliToWorld, GroundY + f.Y * MilliToWorld);
                        Fx.Burst(feet, _skin[e.Index].ultFx, 50, 9f, 0.5f);
                        _camFeel.AddTrauma(0.6f);                     // Match.Shake(0.6f)
                    }
                    break;
            }
        }

        /// <summary>
        /// Impact feel for a hit on <paramref name="d"/> worth <paramref name="pct"/> percent: hitstop vibration (timed by
        /// the sim's HitstopRealTicks), silhouette flash, camera trauma, and on heavy hits a punch-in toward the impact,
        /// a kick along the launch and a radial burst. Strength from the shown state (read only).
        /// </summary>
        /// <summary>
        /// Attacker of the latest hit on fighter <paramref name="d"/> (four-player-design.md 1.3, read only): the sim's
        /// <see cref="ChibiFighterSim.LastHitBy"/>, else the 1v1 opponent. In 1v1 both are 1 - d.
        /// </summary>
        internal static int Attacker(ChibiSimState s, int d)
        {
            int a = s.F[d].LastHitBy;
            return a >= 0 && a < s.PlayerCount && a != d ? a : 1 - d;
        }

        void OnHitFeel(int d, int pct)
        {
            int a = Attacker(_seen, d);
            var fd = _seen.F[d];
            var launch = new Vector2(fd.Vx, fd.Vy) * ChibiFeel.SubToUps;
            float s = ChibiFeel.HitStrength(pct, launch.magnitude);
            // Remaining sim hitstop (real ticks). Above the normal 3 (a tick may already have run) = crit / parry: reads heavy.
            int stop = _loop.State.HitstopRealTicks;
            if (stop > ChibiSimConst.HitstopNormal) s = Mathf.Max(s, ChibiFeel.HeavyHit);
            if (stop > 0)
            {
                _hitShakeS[d] = Mathf.Max(_hitShakeS[d], s);
                _hitStopTotal = Mathf.Max(1, stop);
            }

            Vector2 dc = Center(d), ac = Center(a);
            float side = ac.x >= dc.x ? 1f : -1f;
            Vector2 at = dc + new Vector2(side * 0.3f, 0.15f);
            var flash = Color.Lerp(Color.white, _fx[a], ChibiFeel.FlashTint);
            float camSize = _cam != null ? _cam.orthographicSize : CamMinSize;
            _impactFx.Hit(d, at, _fx[a], flash, s, camSize);

            _camFeel.AddTrauma(ChibiFeel.HitTrauma(s));
            if (s >= ChibiFeel.HeavyHit)
            {
                _camFeel.Punch(ChibiFeel.PunchZoom(s), at);
                Vector2 dir = launch.sqrMagnitude > 1f ? launch : new Vector2(-side, 0f);
                _camFeel.Kick(dir, s);
            }
        }

        // Damage-number strings, built once per value (perf: was one int.ToString allocation per hit).
        static readonly string[] DamageStrings = new string[1000];

        /// <summary>The popup text of a damage value: the same string int.ToString gives, cached for 0..999.</summary>
        public static string DamageText(int value)
        {
            if ((uint)value >= (uint)DamageStrings.Length) return value.ToString();
            return DamageStrings[value] ??= value.ToString();
        }

        // Program-move effect colours, Fighter.cs:816 (grab slam), :832 (tornado), :914 (leap slam).
        static readonly Color GrabDust = new Color(0.8f, 0.7f, 0.6f), TornadoWind = new Color(0.9f, 0.9f, 1f, 0.6f),
                              LeapDust = new Color(0.9f, 0.8f, 0.5f);

        /// <summary>
        /// Per-frame Visual / Fx / Shake calls of the seven program moves (Fighter.cs:775-933), keyed by the sim frame
        /// (multiphase-conversion.md section 2 frame tables). View only: reads _seen, uses Fx (FxRandom), never the sim RNG.
        /// </summary>
        void ProgramFx(int i, int slot, int frame, bool phase)
        {
            var f = _seen.F[i];
            var v = _vis[i];
            switch ((FighterClass)f.Class)
            {
                case FighterClass.Melee:   // Thrust Barrage F:775-786: PlayThrust(0.1) per hit
                    if (slot == 4 && frame % ChibiSimConst.ThrustPeriod == 0 &&
                        frame <= ChibiSimConst.ThrustPeriod * (ChibiSimConst.ThrustHits - 1))
                        v.PlayThrust(0.1f);
                    break;
                case FighterClass.Heavy:
                    if (slot == 5)
                    {
                        if (frame == 0) v.PlaySwing(0.4f);   // ULT Smash jump F:989 (slam: MovePhaseChanged)
                    }
                    else if (slot == 2)
                    {
                        // Grab F:788-818: PlayThrust(0.2) at start, PlaySlam(0.2) after the lift, burst + shake at the slam.
                        if (frame == 0) v.PlayThrust(0.2f);
                        else if (phase && frame == ChibiSimConst.GrabLiftFrames) v.PlaySlam(0.2f);
                        else if (phase && frame == ChibiSimConst.GrabReleaseFrame)
                        {
                            Fx.Burst(WorldPos(f.GrabLX, f.GrabLY), GrabDust, 20, 5f, 0.4f);   // at `slam` (F:806, F:816)
                            _camFeel.AddTrauma(0.35f);                               // Match.Shake(0.35f)
                        }
                    }
                    else if (slot == 3)
                    {
                        // Tornado F:820-842: PlaySpin(1.0) at start, wind burst on every hit tick (0, 12, .., 48).
                        if (frame == 0) { v.PlaySpin(1.0f); v.PlayClipLocked("Skill2", 1.0f); }   // Bruna tornado sheet
                        if (frame % ChibiSimConst.TornadoPeriod == 0 && frame <= ChibiSimConst.TornadoLastHit)
                            Fx.Burst(Center(i), TornadoWind, 6, 4f, 0.3f);
                    }
                    break;
                case FighterClass.Ranged:
                    if (slot == 5) { if (frame == 0) v.PlayRecoil(0.3f); }   // ULT Thousand Arrows F:1014 (split burst: lead expiry)
                    else if (slot == 2 && frame == 0) v.PlayClipLocked("Skill1", 0f);                     // Ren backstep-shot sheet
                    else if (slot == 2 && frame == ChibiSimConst.BackstepShotFrame) v.PlayRecoil(0.2f);   // Backstep F:853
                    else if (slot == 4 && frame % ChibiSimConst.BarragePeriod == 0 &&
                             frame < ChibiSimConst.BarragePeriod * ChibiSimConst.BarrageShots)
                        v.PlayRecoil(0.08f);                                                           // Barrage F:870
                    break;
                case FighterClass.Polearm:
                    if (slot == 5) { KickFx(i, frame); break; }
                    if (frame != 0) break;
                    if (slot == 2) v.PlaySwing(0.5f);        // Leap Slam F:905 (slam: MovePhaseChanged)
                    else if (slot == 3) { v.PlaySpin(1.2f); v.PlayClipLocked("Skill2", 1.2f); }   // Spin Guard F:923 + Taro spin sheet
                    break;
            }
        }

        // FireRing sprite colours, CombatObjects.cs:233-234.
        static readonly Color RingGlow = new Color(1f, 0.4f, 0.05f, 0.35f), RingColor = new Color(1f, 0.55f, 0.1f, 0.9f);
        const float RingSpin = 180f, RingPulseHz = 10f, RingPulse = 0.04f, RingSparkR = 2f;

        /// <summary>
        /// FireRing.Update visuals (CombatObjects.cs:238-245): follows fighter i, spins 180 deg/s, pulses 4% at 10 rad/s,
        /// mirrors with facing, one spark per render frame on a radius-2 circle. View only (Fx / FxRandom); the ring's
        /// damage ticks are the sim's.
        /// </summary>
        void UpdateFireRing(int i, bool on, int facing)
        {
            var ring = _ring[i];
            if (ring == null) return;
            if (ring.gameObject.activeSelf != on)
            {
                ring.gameObject.SetActive(on);
                if (on) ring.localRotation = Quaternion.identity;   // a fresh FireRing starts unrotated
            }
            if (!on) return;
            Vector2 c = Center(i);
            ring.position = c;
            ring.Rotate(0f, 0f, RingSpin * Time.deltaTime);
            float s = 1f + Mathf.Sin(Time.time * RingPulseHz) * RingPulse;
            ring.localScale = new Vector3(s * facing, s, 1f);
            float ang = FxRandom.Value * Mathf.PI * 2f;
            Fx.Burst(c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * RingSparkR,
                     new Color(1f, FxRandom.Range(0.3f, 0.8f), 0.1f), 1, 1.5f, 0.3f);
        }

        // ULT Kick hit-1 burst colour, Fighter.cs:1070.
        static readonly Color KickHitRed = new Color(1f, 0.3f, 0.3f);

        /// <summary>
        /// ULT Polearm Kick per-frame effects (Fighter.cs:1036-1079). The branch is the current MovePhase (1 = dash, else the
        /// in-range lerp / hold / release program); frames follow ChibiSimConst Kick* (lerp 0..8, hold 9, rise 30, release 50).
        /// </summary>
        void KickFx(int i, int frame)
        {
            var f = _seen.F[i];
            var v = _vis[i];
            var ult = _skin[i].ultFx;
            if (f.MovePhase == 1)
            {
                if (frame == 0) v.PlayThrust(0.3f);                                         // F:1044 out of range: dash
                return;
            }
            if (frame < ChibiSimConst.KickLerpFrames) Fx.Burst(Center(i), ult, 2, 2f, 0.3f);   // F:1062 every lerp frame
            else if (frame == ChibiSimConst.KickLerpFrames)
            {
                v.PlayThrust(0.25f);                                                        // F:1068
                Fx.Burst(Center(1 - i), KickHitRed, 20, 5f, 0.35f);                         // F:1070 at o.Center
            }
            else if (frame == ChibiSimConst.KickRiseFrame) v.PlaySpin(0.35f);               // F:1073
            else if (frame == ChibiSimConst.KickReleaseFrame)
            {
                v.PlaySwing(0.2f);                                                          // F:1075
                Fx.Burst(Center(1 - i), ult, 45, 9f, 0.5f);                                 // F:1078 at o.Center
                _camFeel.AddTrauma(0.55f);                                          // Match.Shake(0.55f)
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
            _camFeel.AddTrauma(CinematicShake);                      // :192 Shake(0.3f)
            // Hud.cs:186, :192-194; strings built once here, not per OnGUI.
            CinematicColor = skin.goldCinematic ? Gui.Gold : skin.ultFx;
            CinematicTitle = _cinTitle[i];
            CinematicSubtitle = _cinSub[i];
        }

        /// <summary>Re-skin slot visual <paramref name="v"/> for projectile <paramref name="b"/> (new identity).</summary>
        void Configure(ProjVis v, ChibiProjectileSim b)
        {
            var ult = _skin[Mathf.Clamp(b.Owner, 0, 1)].ultFx;
            var fx = _fx[Mathf.Clamp(b.Owner, 0, 1)];
            UnityEngine.Sprite sprite = Art.Rounded;
            Color c, trail = Color.clear;
            Vector2 size;
            switch (b.Kind)
            {
                case ChibiProjKind.Arrow: c = ArrowColor; size = new Vector2(0.6f, 0.08f); trail = ult; break;      // Fighter.cs:536-540
                case ChibiProjKind.ArrowBig: c = ArrowColor; size = new Vector2(0.9f, 0.14f); trail = ult; break;
                case ChibiProjKind.Bolt:                                                                            // Fighter.cs:481-484
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
                // Rules v18 moveset projectiles, in the owner's character palette.
                case ChibiProjKind.WindBlade: sprite = ChibiMoveVfx.Crescent; c = Color.Lerp(fx, Color.white, 0.25f); size = new Vector2(0.55f, 0.95f); trail = fx; break;
                case ChibiProjKind.GroundWave: c = fx; size = new Vector2(0.45f, 0.9f); trail = GroundWaveDust; break;
                case ChibiProjKind.Grapple: c = GrappleColor; size = new Vector2(0.7f, 0.07f); trail = fx; break;
                case ChibiProjKind.LanceArrow: c = Color.Lerp(fx, Color.white, 0.4f); size = new Vector2(1.0f, 0.12f); trail = fx; break;
                default: sprite = Art.Circle; c = Color.white; size = new Vector2(0.35f, 0.35f); break;
            }
            if (b.SrcSlot >= ChibiMovesetData.FirstNewSlot) trail = fx;   // any shot of a moveset row trails its palette colour
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
            float aspect = Mathf.Max(0.1f, _cam.aspect);
            var t = _cam.transform;
            if (!_camBaseSet) { _camBase = t.position; _camBaseSet = true; }
            // Camera rect from the stage's KO lines / walls, read live (GL-3 may move the lines): never far past a blast
            // line, wall edges visible, floor kept low in frame.
            Rect bounds = ChibiCameraFraming.Bounds(ChibiStageTable.Get(_stageIndex), GroundY);
            if (_stage != null && _stage.design != null)                     // design stages: never past the painted art
                bounds = ChibiCameraFraming.WithinArt(bounds, _stage.design.ArtRect);
            Vector2 goal;
            float size, smooth = CamSmooth;
            if (_focus >= 0 && Time.unscaledTime < _focusUntil)
            {
                goal = Center(_focus);                                       // Stage.cs:161-164
                size = CinematicFocusSize;
                smooth = CamFocusSmooth;
            }
            else if (FrameFighters(aspect, out goal, out size))
            {
                _camGoal = goal; _camGoalSize = size; _camGoalSet = true;
            }
            else
            {
                goal = _camGoalSet ? _camGoal : _camBase;                    // nobody in play: hold the last framing
                size = _camGoalSet ? _camGoalSize : _cam.orthographicSize;
            }
            // Final KO: slow-mo (ViewTimeScale) plus a zoom onto the KO blast, leaning toward the winner, then back out.
            if (_finished)
            {
                float w = ChibiFeel.FinishZoom01(Time.unscaledTime - _finishedTime);
                if (!_finishFocusSet) CaptureFinishFocus(goal);
                if (w > 0f)
                {
                    goal = Vector2.Lerp(goal, _finishFocus, w);
                    size = Mathf.Lerp(size, ChibiFeel.FinishKoSize, w);
                    smooth = Mathf.Lerp(smooth, CamFocusSmooth, w);
                }
            }
            size = Mathf.Min(size, ChibiCameraFraming.MaxSize(bounds, aspect, CamMaxSize));
            if (!_camSizeSet) { _camSize = _cam.orthographicSize; _camSizeSet = true; }
            // Springs carry the framing only; the punch-in and shake are layered on top and never fed back (no jitter).
            _camSize = Mathf.SmoothDamp(_camSize, size, ref _camSizeVel, CamSizeSmooth, Mathf.Infinity, dt);
            goal = ChibiCameraFraming.ClampCentre(goal, _camSize, aspect, bounds);
            Vector2 p = Vector2.SmoothDamp(_camBase, goal, ref _camVel, smooth, Mathf.Infinity, dt);
            _camBase = ChibiCameraFraming.ClampCentre(p, _camSize, aspect, bounds);

            _camFeel.Tick(dt);
            float shown = _camSize * _camFeel.ZoomFactor;                    // heavy-hit punch-in (<= PunchZoomMax closer)
            Vector2 centre = _camBase;
            float pw = _camFeel.PunchWeight;
            if (pw > 0f) centre = Vector2.Lerp(centre, _camFeel.PunchAt, ChibiFeel.PunchFocus * pw);
            centre = ChibiCameraFraming.ClampCentre(centre, shown, aspect, bounds);
            _cam.orthographicSize = shown;
            Vector2 offset = _camFeel.Offset(Time.unscaledTime);             // smooth trauma shake + directional kick, capped
            t.position = new Vector3(centre.x + offset.x, centre.y + offset.y, -10f);
        }

        /// <summary>Final-KO focus: the KO blast point (ChibiLaunchFx) leaning toward the winner; else the winner; else the framing goal.</summary>
        void CaptureFinishFocus(Vector2 fallback)
        {
            _finishFocusSet = true;
            int win = _seen.Winner;
            bool hasWinner = win >= 0 && win < 2 && _vis[win] != null && !_launchFx.IsOut(win);
            if (_launchFx.KoFocus(out var ko))
                _finishFocus = hasWinner ? Vector2.Lerp(ko, Center(win), ChibiFeel.FinishFocusToWinner) : ko;
            else _finishFocus = hasWinner ? Center(win) : fallback;
        }

        /// <summary>
        /// Framing goal and size for the fighters on screen (KO'd ones are skipped; a hovering one counts): their bounding
        /// box, plus a launched fighter's lean point toward the blast line, plus the KO point for a moment after a KO;
        /// centre kept above the floor (CamGroundLift / CamLowLift), a slight lead in the direction of motion, and a
        /// gentle zoom-out on strong launches. False when nobody is on screen.
        /// </summary>
        bool FrameFighters(float aspect, out Vector2 goal, out float size)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            float lowest = float.MaxValue, launch = 0f;
            Vector2 motion = Vector2.zero;
            int n = 0;
            for (int i = 0; i < 2; i++)
            {
                if (_vis[i] == null || _launchFx.IsOut(i)) continue;
                Vector2 c = Center(i);
                Encapsulate(c, ref minX, ref maxX, ref minY, ref maxY);
                lowest = Mathf.Min(lowest, c.y);
                n++;
                bool launched = _launchFx.LaunchLead(i, out var v, out float s);
                float m = v.magnitude;
                if (launched && m > 0.01f)
                {
                    Encapsulate(c + v / m * Mathf.Min(m * CamLeadSec, CamLeadMax) * Mathf.Lerp(0.5f, 1f, s), ref minX, ref maxX, ref minY, ref maxY);
                    launch = Mathf.Max(launch, s);
                }
                else motion += v;
            }
            if (n == 0) { goal = Vector2.zero; size = 0f; return false; }
            if (_launchFx.KoFocus(out var ko)) Encapsulate(ko, ref minX, ref maxX, ref minY, ref maxY);
            goal = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
            goal.y = Mathf.Max(goal.y, Mathf.Min(GroundY + CamGroundLift, lowest + CamLowLift));
            goal += Vector2.ClampMagnitude(motion / n * CamMoveLeadSec, CamMoveLeadMax);
            float dist = Mathf.Max((maxX - minX) * 0.5f / aspect, (maxY - minY) * 0.5f);
            size = Mathf.Clamp(dist + CamPad + CamLaunchZoom * launch, CamMinSize, CamMaxSize);
            return true;
        }

        static void Encapsulate(Vector2 p, ref float minX, ref float maxX, ref float minY, ref float maxY)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
        }

        void OnDestroy()
        {
            if (_loop != null && !(_loop is Object o && o == null)) _loop.OnFrame -= OnSimFrame;
            ChibiAudio.EndMatch(_loop);   // [audio hook] stop this match's loops
            _events.Clear();
            _leadBurstPos.Clear();
            _leadBurstOwner.Clear();
            _newMoves.Clear();
            _newHits.Clear();
            _koFly.Clear();
            _moveFx?.Clear();
            _launchFx?.Clear();
            _impactFx?.Clear();
            _camFeel.Reset();
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
                Stage = Mathf.Clamp(stage, 0, StageBuilder.Names.Length - 1), Intro = true,
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
