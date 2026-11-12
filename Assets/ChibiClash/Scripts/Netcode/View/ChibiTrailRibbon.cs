using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>Weapon trail shapes (presentation only).</summary>
    public enum TrailShape : byte
    {
        None,
        /// <summary>Short straight streak at shoulder height.</summary>
        Jab,
        /// <summary>Overhead arc, top -> forward -> down.</summary>
        Swing,
        /// <summary>Rising arc, low front -> overhead.</summary>
        Upswing,
        /// <summary>Flat ellipse along the ground, back -> front.</summary>
        LowSweep,
        /// <summary>Long straight streak forward.</summary>
        Thrust,
        /// <summary>Body-wide motion streak behind the fighter.</summary>
        Rush,
        /// <summary>Full circle (one or more turns) around the body.</summary>
        Spin,
        /// <summary>Vertical arc in front that widens into a wedge at the bottom.</summary>
        Slam,
        /// <summary>Vertical wedge from above down through the fighter.</summary>
        Dive,
        /// <summary>Rising crescent close in front.</summary>
        Uppercut,
        /// <summary>Big rising crescent, low back -> high front.</summary>
        Launch,
    }

    /// <summary>
    /// A trail path in fighter-local space (facing +1, origin at the body centre): an arc band between an inner and an outer
    /// radius (the area the blade sweeps), or a straight streak with a half width. <see cref="Eval"/> maps the swing progress
    /// u (0..1) to the inner / outer edge points. Pure math, unit-testable.
    /// </summary>
    public struct TrailPath
    {
        /// <summary>True = arc band, false = straight streak.</summary>
        public bool Arc;
        /// <summary>Texture profile: centred streak (core in the middle) or arc (core along the tip edge).</summary>
        public bool Centred;
        /// <summary>Arc: centre, radius, y squash, start / end angle (radians), inner radius ratio at u = 0 / u = 1.</summary>
        public Vector2 C;
        public float R, Ky, A0, A1, In0, In1;
        /// <summary>Streak: start / end of the centre line and half width at u = 0 / u = 1.</summary>
        public Vector2 P0, P1;
        public float W0, W1;
        /// <summary>Visible trail length behind the head, in u.</summary>
        public float Span;

        /// <summary>Inner and outer edge at progress <paramref name="u"/> (fighter-local, facing +1).</summary>
        public void Eval(float u, out Vector2 inner, out Vector2 outer)
        {
            if (Arc)
            {
                float a = Mathf.Lerp(A0, A1, u);
                var d = new Vector2(Mathf.Cos(a), Ky * Mathf.Sin(a));
                outer = C + d * R;
                inner = C + d * (R * Mathf.Lerp(In0, In1, u));
                return;
            }
            var p = Vector2.Lerp(P0, P1, u);
            var t = P1 - P0;
            var n = t.sqrMagnitude > 1e-6f ? new Vector2(-t.y, t.x).normalized : Vector2.up;
            float w = Mathf.Lerp(W0, W1, u);
            inner = p - n * w;
            outer = p + n * w;
        }
    }

    /// <summary>Shape tables: AnimKind / classic slot to shape, and shape + reach to a <see cref="TrailPath"/>.</summary>
    public static class ChibiTrailShapes
    {
        const float Deg = Mathf.Deg2Rad;

        /// <summary>Default blade reach per class (world units from the shoulder): Melee, Heavy, Ranged, Magic, Polearm.</summary>
        static readonly float[] ClassReach = { 1.25f, 1.55f, 0.95f, 1.0f, 2.0f };

        /// <summary>Shape of a moveset AnimKind; None for shoot / cast / guard (muzzle flares or nothing).</summary>
        public static TrailShape FromAnimKind(string kind)
        {
            switch (kind)
            {
                case "jab": return TrailShape.Jab;
                case "swing": return TrailShape.Swing;
                case "upswing": return TrailShape.Upswing;
                case "lowsweep": return TrailShape.LowSweep;
                case "thrust": return TrailShape.Thrust;
                case "rush":
                case "dash": return TrailShape.Rush;
                case "spin": return TrailShape.Spin;
                case "slam": return TrailShape.Slam;
                case "dive": return TrailShape.Dive;
                case "uppercut": return TrailShape.Uppercut;
                case "launch": return TrailShape.Launch;
                default: return TrailShape.None;
            }
        }

        /// <summary>
        /// Shape of classic slots 0-6 (light combo step <paramref name="combo"/>, heavy, skills), mirroring the clips
        /// FighterVisual.PlayNormal / ChibiSimView.SkillAnim play. Ranged / Magic return None (muzzle flares instead); program
        /// moves with their own timing (Thrust Barrage, Tornado, Leap Slam, Spin Guard) are handled by the caller.
        /// </summary>
        public static TrailShape Classic(FighterClass cls, int slot, int combo)
        {
            switch (cls)
            {
                case FighterClass.Melee:
                    if (slot == 0) return combo % 3 == 0 ? TrailShape.Jab : combo % 3 == 1 ? TrailShape.Swing : TrailShape.Upswing;
                    if (slot == 1) return TrailShape.Swing;
                    if (slot == 2) return TrailShape.Thrust;      // Dash Thrust
                    return TrailShape.None;                        // Parry, Barrage (program), ult, pose
                case FighterClass.Heavy:
                    if (slot == 0) return combo % 2 == 0 ? TrailShape.Swing : TrailShape.Upswing;
                    if (slot == 1) return TrailShape.Slam;
                    if (slot == 4) return TrailShape.Swing;
                    return TrailShape.None;                        // Grab, Tornado (program), Smash (cinematic)
                case FighterClass.Polearm:
                    if (slot == 0) return combo % 2 == 0 ? TrailShape.Thrust : TrailShape.LowSweep;
                    if (slot == 1) return TrailShape.Swing;
                    if (slot == 4) return TrailShape.Thrust;      // Throw Spear
                    return TrailShape.None;                        // Leap Slam / Spin Guard (programs), Kick
                default:
                    return TrailShape.None;
            }
        }

        /// <summary>Class default reach (world units).</summary>
        public static float Reach(int cls) => ClassReach[Mathf.Clamp(cls, 0, ClassReach.Length - 1)];

        /// <summary>
        /// Path for <paramref name="shape"/> with blade reach <paramref name="reach"/>, shifted by <paramref name="dy"/> toward
        /// the hitbox's height; <paramref name="turns"/> = full turns of a Spin.
        /// </summary>
        public static TrailPath Path(TrailShape shape, float reach, float dy, float turns = 1f)
        {
            float r = reach;
            var p = new TrailPath { Ky = 1f, Span = 0.7f };
            switch (shape)
            {
                case TrailShape.Jab: Streak(ref p, new Vector2(0.15f, 0.12f + dy), new Vector2(r * 0.95f, 0.12f + dy), 0.05f, 0.09f, 0.9f); break;
                case TrailShape.Thrust: Streak(ref p, new Vector2(-0.25f, 0.08f + dy), new Vector2(r * 1.15f, 0.08f + dy), 0.05f, 0.13f, 0.8f); break;
                case TrailShape.Rush: Streak(ref p, new Vector2(-r * 1.7f, dy * 0.5f), new Vector2(0.1f, dy * 0.5f), 0.1f, 0.34f, 0.75f); break;   // ends at the body: trails behind
                case TrailShape.Dive: Streak(ref p, new Vector2(-0.1f, 1.3f), new Vector2(0.25f, -0.85f), 0.06f, 0.34f, 0.8f); break;
                case TrailShape.Swing: Arc(ref p, new Vector2(0.05f, 0.1f + dy), r, 1f, 115f, -65f, 0.5f, 0.42f, 0.7f); break;
                case TrailShape.Upswing: Arc(ref p, new Vector2(0.05f, dy), r, 1f, -75f, 110f, 0.5f, 0.45f, 0.7f); break;
                case TrailShape.LowSweep: Arc(ref p, new Vector2(0f, -0.4f + dy * 0.5f), r * 1.05f, 0.42f, 200f, 345f, 0.55f, 0.5f, 0.75f); break;
                case TrailShape.Spin:
                {
                    turns = Mathf.Max(1f, turns);
                    Arc(ref p, new Vector2(0f, 0.05f), r * 0.9f, 0.85f, 95f, 95f - 360f * turns, 0.55f, 0.55f, Mathf.Min(1f, 0.85f / turns));
                    break;
                }
                case TrailShape.Slam: Arc(ref p, new Vector2(0.2f, 0.15f + dy * 0.5f), r, 1f, 105f, -80f, 0.6f, 0.12f, 0.75f); break;
                case TrailShape.Uppercut: Arc(ref p, new Vector2(0.25f, -0.1f), r * 0.85f, 1f, -105f, 95f, 0.55f, 0.35f, 0.7f); break;
                case TrailShape.Launch: Arc(ref p, new Vector2(0.15f, -0.1f), r, 1f, -70f, 80f, 0.5f, 0.3f, 0.75f); break;   // 150 deg max
            }
            return p;
        }

        static void Streak(ref TrailPath p, Vector2 a, Vector2 b, float w0, float w1, float span)
        {
            p.Arc = false; p.Centred = true;
            p.P0 = a; p.P1 = b; p.W0 = w0; p.W1 = w1; p.Span = span;
        }

        static void Arc(ref TrailPath p, Vector2 c, float r, float ky, float a0, float a1, float in0, float in1, float span)
        {
            p.Arc = true; p.Centred = false;
            p.C = c; p.R = r; p.Ky = ky; p.A0 = a0 * Deg; p.A1 = a1 * Deg; p.In0 = in0; p.In1 = in1; p.Span = span;
        }
    }

    /// <summary>
    /// One pooled weapon ribbon: a blade mesh (white-hot core -> Fx colour -> transparent edge, drawn over the fighters) and a
    /// wider soft glow mesh (drawn behind the owner's body). Fixed topology (<see cref="Segments"/> x 3 quads, 132 vertices, built once);
    /// each frame only vertex positions and colours are rewritten, into preallocated arrays (no allocation).
    /// The ribbon is rebuilt analytically from the path every frame, so it stays smooth at any frame rate.
    /// </summary>
    public sealed class ChibiTrailRibbon
    {
        /// <summary>Segments along the ribbon.</summary>
        public const int Segments = 32;
        const int Sections = Segments + 1;
        /// <summary>
        /// Vertices across the ribbon and their V (inner edge 0 .. tip edge 1). Four rows keep the white core line (V ~0.85)
        /// straight on wide, strongly curved arcs; with two rows the per-triangle interpolation zigzags it.
        /// </summary>
        const int Across = 4;
        static readonly float[] AcrossV = { 0f, 0.55f, 0.8f, 1f };
        /// <summary>Glow mesh widening: outward past the tip edge / inward past the inner edge (fraction of the band width).</summary>
        const float GlowOut = 0.35f, GlowIn = 0.08f, GlowStreak = 1.1f;
        /// <summary>Glow colour level (x Peak on screen) and alpha.</summary>
        const float GlowLevel = 0.42f, GlowAlpha = 0.8f;
        /// <summary>Fraction of the ribbon length (at the head) over which it tapers to a point.</summary>
        const float HeadTaper = 0.12f;

        readonly Mesh _bladeMesh, _glowMesh;
        readonly MeshRenderer _blade, _glow;
        readonly Vector3[] _bv = new Vector3[Sections * Across], _gv = new Vector3[Sections * Across];
        readonly Color32[] _bc = new Color32[Sections * Across], _gc = new Color32[Sections * Across];

        /// <summary>True while drawn.</summary>
        public bool Visible { get; private set; }

        /// <summary>Builds both meshes under <paramref name="parent"/> (world space, identity transform).</summary>
        public ChibiTrailRibbon(Transform parent, string name, int bladeOrder, int glowOrder)
        {
            _bladeMesh = NewMesh(name + "Blade");
            _glowMesh = NewMesh(name + "Glow");
            _blade = NewRenderer(parent, name + "Blade", _bladeMesh, bladeOrder);
            _glow = NewRenderer(parent, name + "Glow", _glowMesh, glowOrder);
        }

        /// <summary>Material pair for this ribbon (swapped per swing: arc or streak profile; no allocation).</summary>
        public void SetMaterials(Material blade, Material glow, int glowOrder)
        {
            _blade.sharedMaterial = blade;
            _glow.sharedMaterial = glow;
            _glow.sortingOrder = glowOrder;
        }

        /// <summary>Hide both meshes.</summary>
        public void Hide()
        {
            if (!Visible) return;
            Visible = false;
            _blade.enabled = false;
            _glow.enabled = false;
        }

        /// <summary>
        /// Rebuild along <paramref name="path"/> from progress <paramref name="tail"/> to <paramref name="head"/>, placed at
        /// <paramref name="anchor"/> mirrored by <paramref name="facing"/>, glow tinted <paramref name="fx"/>, overall
        /// <paramref name="alpha"/> (0..1).
        /// </summary>
        public void Draw(in TrailPath path, float tail, float head, Vector2 anchor, int facing, Color fx, float alpha)
        {
            if (head - tail < 0.004f || alpha <= 0.003f) { Hide(); return; }
            float fs = facing >= 0 ? 1f : -1f;
            byte gr = (byte)(Mathf.Clamp01(fx.r * GlowLevel) * 255f), gg = (byte)(Mathf.Clamp01(fx.g * GlowLevel) * 255f),
                 gb = (byte)(Mathf.Clamp01(fx.b * GlowLevel) * 255f);
            for (int i = 0; i < Sections; i++)
            {
                float s = i / (float)Segments;                     // 0 tail .. 1 head
                float u = Mathf.Lerp(tail, head, s);
                path.Eval(u, out var inner, out var outer);
                // Taper toward the tail: arcs shrink onto the tip edge, streaks onto their centre line; a short round-off at the head.
                // Leading end: over the last HeadTaper of the length the width closes to a point (arcs onto the tip edge) and
                // the alpha eases off, so every ribbon ends in a sharp crescent tip instead of a flat cut.
                float headK = s > 1f - HeadTaper ? (1f - s) / HeadTaper : 1f;
                float taper = (0.15f + 0.85f * Mathf.Pow(s, 0.6f)) * Mathf.Pow(headK, 0.75f);
                Vector2 bi, bo, gi, go;
                if (path.Arc)
                {
                    bi = Vector2.Lerp(outer, inner, taper);
                    bo = outer;
                    var w = bo - bi;
                    gi = bi - w * GlowIn;
                    go = bo + w * GlowOut;
                }
                else
                {
                    var mid = (inner + outer) * 0.5f;
                    var h = (outer - inner) * (0.5f * taper);
                    bi = mid - h; bo = mid + h;
                    gi = mid - h * (1f + GlowStreak); go = mid + h * (1f + GlowStreak);
                }
                // Fade toward the tail (s^1.4) with a slightly hotter head.
                float a = Mathf.Clamp01(alpha * Mathf.Pow(s, 1.4f) * (0.85f + 0.15f * s) * (0.25f + 0.75f * headK));
                var cb = new Color32(255, 255, 255, (byte)(a * 255f));
                var cg = new Color32(gr, gg, gb, (byte)(a * GlowAlpha * 255f));
                for (int j = 0; j < Across; j++)
                {
                    float v = AcrossV[j];
                    var pb = Vector2.LerpUnclamped(bi, bo, v);
                    var pg = Vector2.LerpUnclamped(gi, go, v);
                    int n = i * Across + j;
                    _bv[n] = new Vector3(anchor.x + pb.x * fs, anchor.y + pb.y, 0f);
                    _gv[n] = new Vector3(anchor.x + pg.x * fs, anchor.y + pg.y, 0f);
                    _bc[n] = cb;
                    _gc[n] = cg;
                }
            }
            _bladeMesh.vertices = _bv;
            _bladeMesh.colors32 = _bc;
            _bladeMesh.RecalculateBounds();
            _glowMesh.vertices = _gv;
            _glowMesh.colors32 = _gc;
            _glowMesh.RecalculateBounds();
            if (!Visible)
            {
                Visible = true;
                _blade.enabled = true;
                _glow.enabled = true;
            }
        }

        /// <summary>Destroys the meshes (materials belong to the caller).</summary>
        public void Dispose()
        {
            if (_bladeMesh != null) Object.Destroy(_bladeMesh);
            if (_glowMesh != null) Object.Destroy(_glowMesh);
        }

        static Mesh NewMesh(string name)
        {
            var m = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            m.MarkDynamic();
            var v = new Vector3[Sections * Across];
            var uv = new Vector2[Sections * Across];
            var tri = new int[Segments * (Across - 1) * 6];
            for (int i = 0; i < Sections; i++)
            for (int j = 0; j < Across; j++)
                uv[i * Across + j] = new Vector2(i / (float)Segments, AcrossV[j]);   // V: inner edge 0 .. tip edge 1
            int k = 0;
            for (int i = 0; i < Segments; i++)
            for (int j = 0; j < Across - 1; j++)
            {
                int a = i * Across + j, b = a + 1, c = a + Across, d = c + 1;
                tri[k++] = a; tri[k++] = b; tri[k++] = c;
                tri[k++] = b; tri[k++] = d; tri[k++] = c;
            }
            m.vertices = v;
            m.uv = uv;
            m.colors32 = new Color32[Sections * Across];
            m.triangles = tri;
            return m;
        }

        static MeshRenderer NewRenderer(Transform parent, string name, Mesh mesh, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortingOrder = order;
            r.enabled = false;
            return r;
        }
    }
}
