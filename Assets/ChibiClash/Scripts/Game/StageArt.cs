using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Procedural cel-shaded stage art: ink-outlined shapes, triangles and gradients, parallax layers, ambient motion,
    /// and larger set pieces (birds, whale). No asset dependencies, like <see cref="Art"/>. Ported from the
    /// copy-ghibli-smash fork (UI-1) WITHOUT its stage hazards and moving platforms: everything here is decoration that
    /// follows the camera / clock; the simulation's stage geometry (ChibiStageTable) never reads it.
    /// </summary>
    public static class StageArt
    {
        public static readonly Color InkColor = new Color(0.05f, 0.05f, 0.09f);

        static UnityEngine.Sprite tri, grad;

        /// <summary>Isosceles triangle, apex up, 1x1 world units.</summary>
        public static UnityEngine.Sprite Triangle => tri ? tri : (tri = MakeTriangle(128));
        /// <summary>Opaque at the bottom, transparent at the top, 1x1 world units.</summary>
        public static UnityEngine.Sprite Gradient => grad ? grad : (grad = MakeGradient(64));

        static Texture2D NewTex(int size)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
        }

        static UnityEngine.Sprite MakeSprite(Texture2D t) =>
            UnityEngine.Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width);

        static UnityEngine.Sprite MakeTriangle(int size)
        {
            var t = NewTex(size);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                float d = (0.5f * (1f - v) - Mathf.Abs(u - 0.5f)) * size * 0.9f;
                d = Mathf.Min(d, (y + 0.5f));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(d + 0.5f) * 255));
            }
            t.SetPixels32(px);
            t.Apply();
            return MakeSprite(t);
        }

        static UnityEngine.Sprite MakeGradient(int size)
        {
            var t = NewTex(size);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                float a = 1f - y / (size - 1f);
                a = a * a * (3f - 2f * a);
                for (int x = 0; x < size; x++) px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            t.SetPixels32(px);
            t.Apply();
            return MakeSprite(t);
        }

        public static SpriteRenderer Shape(Transform parent, string name, UnityEngine.Sprite s, Color c, Vector2 pos, Vector2 scale, int order, float rot = 0f)
        {
            var sr = Art.Sprite(parent, name, s, c, pos, scale, order);
            if (rot != 0f) sr.transform.localRotation = Quaternion.Euler(0f, 0f, rot);
            return sr;
        }

        /// <summary>Shape with a bold ink outline behind it (outline = line width in world units). Uses order and order - 1.</summary>
        public static SpriteRenderer Inked(Transform parent, string name, UnityEngine.Sprite s, Color c, Vector2 pos, Vector2 scale, int order, float rot = 0f, float outline = 0.08f)
        {
            var ink = new Color(InkColor.r, InkColor.g, InkColor.b, c.a);
            Shape(parent, name + "Ink", s, ink, pos, new Vector2(Mathf.Sign(scale.x) * (Mathf.Abs(scale.x) + outline * 2f), scale.y + outline * 2f), order - 1, rot);
            return Shape(parent, name, s, c, pos, scale, order, rot);
        }

        /// <summary>Background layer that follows the camera by <paramref name="parallax"/> (0 = world-fixed, 1 = screen-fixed).</summary>
        public static Transform Layer(Transform parent, string name, float parallax)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            if (parallax != 0f) go.AddComponent<Parallax>().factor = parallax;
            return go.transform;
        }

        /// <summary>Vertical sky: flat top colour with a gradient into the horizon colour.</summary>
        public static void Sky(Transform layer, Color top, Color horizon, float horizonY, int order = -100)
        {
            Shape(layer, "SkyTop", Art.Square, top, new Vector2(0f, horizonY + 40f), new Vector2(160f, 100f), order);
            Shape(layer, "SkyHorizon", StageArt.Gradient, horizon, new Vector2(0f, horizonY + 6f), new Vector2(160f, 22f), order + 1);
            Shape(layer, "SkyBelow", Art.Square, horizon, new Vector2(0f, horizonY - 45f), new Vector2(160f, 90f), order + 1);
        }

        /// <summary>Cel-shaded cumulus: lit puffs over a shadow tone, with an ink rim.</summary>
        public static Transform Cloud(Transform layer, Vector2 pos, float size, Color lit, Color shade, int order, bool ink = true)
        {
            var go = new GameObject("Cloud");
            go.transform.SetParent(layer, false);
            go.transform.localPosition = pos;
            var t = go.transform;
            Vector2[] puffs = { new Vector2(-1.1f, -0.1f), new Vector2(0f, 0.35f), new Vector2(1.1f, 0f), new Vector2(0.45f, 0.75f), new Vector2(-0.45f, 0.55f) };
            float[] sizes = { 1.5f, 2f, 1.6f, 1.3f, 1.2f };
            if (ink)
                for (int i = 0; i < puffs.Length; i++)
                    Shape(t, "PuffInk", Art.Circle, InkColor, puffs[i] * size, Vector2.one * (sizes[i] * size + 0.16f), order - 2);
            Shape(t, "Base", Art.Rounded, shade, new Vector2(0f, -0.45f * size), new Vector2(3.6f * size, 0.9f * size), order - 1);
            for (int i = 0; i < puffs.Length; i++)
                Shape(t, "Shade", Art.Circle, shade, puffs[i] * size, Vector2.one * sizes[i] * size, order - 1);
            for (int i = 0; i < puffs.Length; i++)
                Shape(t, "Puff", Art.Circle, lit, puffs[i] * size + new Vector2(-0.08f, 0.1f) * size, Vector2.one * sizes[i] * size * 0.86f, order);
            return t;
        }

        public static void Pine(Transform layer, Vector2 basePos, float h, Color leaf, int order)
        {
            Shape(layer, "Trunk", Art.Square, new Color(0.25f, 0.17f, 0.12f), basePos + new Vector2(0f, h * 0.15f), new Vector2(h * 0.08f, h * 0.3f), order);
            for (int i = 0; i < 3; i++)
            {
                float w = h * (0.7f - i * 0.17f);
                Inked(layer, "Leaves", Triangle, Color.Lerp(leaf, Color.white, i * 0.06f), basePos + new Vector2(0f, h * (0.38f + i * 0.2f)), new Vector2(w, h * 0.38f), order + 2 + i * 2, 0f, 0.05f);
            }
        }

        // ---------------------------------------------------------------- ambient motion shortcuts

        public static Ambient Bob(Component c, Vector2 amount, float speed, float phase = 0f) => Ambient.Add(c, Ambient.Mode.Bob, amount, speed, phase);
        public static Ambient Sway(Component c, float degrees, float speed, float phase = 0f) => Ambient.Add(c, Ambient.Mode.Sway, new Vector2(degrees, 0f), speed, phase);
        /// <summary>Elliptical loop (radii = amount); the object's x scale flips to face its direction of travel.</summary>
        public static Ambient Orbit(Component c, Vector2 radii, float speed, float phase = 0f) => Ambient.Add(c, Ambient.Mode.Orbit, radii, speed, phase);
        public static Ambient Pulse(Component c, float depth, float speed, float phase = 0f) => Ambient.Add(c, Ambient.Mode.Pulse, new Vector2(depth, 0f), speed, phase);

        /// <summary>Constant drift with wrap-around between min and max along x (or y).</summary>
        public static Ambient Drift(Component c, Vector2 velocity, float min, float max, bool alongY = false)
        {
            var a = Ambient.Add(c, Ambient.Mode.Drift, Vector2.zero, 1f, 0f);
            a.velocity = velocity;
            a.wrapMin = min;
            a.wrapMax = max;
            a.wrapY = alongY;
            return a;
        }

        // ---------------------------------------------------------------- set pieces

        /// <summary>Great bird facing +x (flip the returned root's x scale to face left). Wings flap.</summary>
        public static Transform Bird(Transform parent, string name, Color body, Color wing, Color belly, int order, float scale, bool flap = true)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);
            root.localScale = new Vector3(scale, scale, 1f);

            var back = new GameObject("WingBack").transform;
            back.SetParent(root, false);
            back.localPosition = new Vector2(-0.2f, 0.4f);
            Inked(back, "Feathers", Triangle, Color.Lerp(wing, Color.black, 0.25f), new Vector2(-0.5f, 1.4f), new Vector2(2.4f, 3.6f), order, 28f);
            if (flap) Sway(back, 22f, 9f, 0.4f);

            Inked(root, "Tail", Triangle, wing, new Vector2(-2.1f, -0.05f), new Vector2(1.3f, 1.8f), order + 2, 90f);
            Inked(root, "Body", Art.Circle, body, Vector2.zero, new Vector2(3.4f, 1.3f), order + 4);
            Shape(root, "Belly", Art.Circle, belly, new Vector2(0.2f, -0.28f), new Vector2(2.4f, 0.6f), order + 5);
            Inked(root, "Head", Art.Circle, body, new Vector2(1.75f, 0.38f), new Vector2(1.05f, 0.95f), order + 6);
            Inked(root, "Beak", Triangle, new Color(1f, 0.78f, 0.25f), new Vector2(2.55f, 0.28f), new Vector2(0.55f, 0.85f), order + 6, -90f);
            Shape(root, "Eye", Art.Circle, new Color(1f, 0.95f, 0.7f), new Vector2(1.95f, 0.48f), new Vector2(0.22f, 0.22f), order + 8);
            Shape(root, "Pupil", Art.Circle, InkColor, new Vector2(1.99f, 0.48f), new Vector2(0.1f, 0.12f), order + 9);

            var front = new GameObject("WingFront").transform;
            front.SetParent(root, false);
            front.localPosition = new Vector2(0.1f, 0.3f);
            Inked(front, "Feathers", Triangle, wing, new Vector2(-0.4f, 1.5f), new Vector2(2.6f, 4f), order + 10, 22f);
            Shape(front, "Tips", Triangle, Color.Lerp(wing, Color.white, 0.25f), new Vector2(-0.7f, 2.4f), new Vector2(1.2f, 1.8f), order + 11, 22f);
            if (flap) Sway(front, 26f, 9f);
            return root;
        }

        /// <summary>Huge whale facing +x, body about 15 x 4.5 units at scale 1.</summary>
        public static Transform Whale(Transform parent, string name, Color body, Color belly, Color glow, int order)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);
            var tail = new GameObject("Tail").transform;
            tail.SetParent(root, false);
            tail.localPosition = new Vector2(-7.4f, 0.3f);
            Inked(tail, "Stock", Triangle, body, new Vector2(0.4f, 0f), new Vector2(2.4f, 3.6f), order, 90f, 0.14f);
            Inked(tail, "FlukeUp", Triangle, body, new Vector2(-1.6f, 1.2f), new Vector2(1.6f, 3.2f), order, 125f, 0.14f);
            Inked(tail, "FlukeDown", Triangle, body, new Vector2(-1.6f, -1.2f), new Vector2(1.6f, 3.2f), order, 55f, 0.14f);
            Sway(tail, 7f, 1.3f);

            Inked(root, "Body", Art.Circle, body, Vector2.zero, new Vector2(14f, 4.4f), order + 2, 0f, 0.16f);
            Inked(root, "Head", Art.Circle, body, new Vector2(4.6f, 0.25f), new Vector2(6f, 3.9f), order + 2, 0f, 0.16f);
            Shape(root, "Belly", Art.Circle, belly, new Vector2(1f, -1.2f), new Vector2(11f, 1.8f), order + 3);
            for (int i = 0; i < 6; i++)
                Shape(root, "Groove", Art.Square, Color.Lerp(belly, body, 0.5f), new Vector2(1.5f + i * 0.9f, -1.35f), new Vector2(0.6f, 0.06f), order + 4);
            Inked(root, "Fin", Triangle, Color.Lerp(body, Color.black, 0.2f), new Vector2(1.4f, -1.9f), new Vector2(1.2f, 2.6f), order + 5, 150f, 0.1f);
            Shape(root, "Eye", Art.Circle, glow, new Vector2(5.6f, -0.1f), new Vector2(0.38f, 0.34f), order + 6);
            Shape(root, "EyeGlow", Art.Soft, new Color(glow.r, glow.g, glow.b, 0.5f), new Vector2(5.6f, -0.1f), new Vector2(1.6f, 1.6f), order + 5);
            for (int i = 0; i < 5; i++)
                Pulse(Shape(root, "Rune", Art.Ring, new Color(glow.r, glow.g, glow.b, 0.8f), new Vector2(-3.5f + i * 1.6f, 1.1f - Mathf.Abs(i - 2) * 0.25f), new Vector2(0.5f, 0.5f), order + 4), 0.7f, 2f, i * 0.8f);
            return root;
        }
    }

    /// <summary>Moves a background layer with the camera to fake depth.</summary>
    public class Parallax : MonoBehaviour
    {
        public float factor;              // 0 = fixed in the world, 1 = fixed to the screen
        public float vertical = 0.6f;     // vertical parallax is damped so horizons stay put

        Vector3 basePos;
        bool init;

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (!init)
            {
                basePos = transform.localPosition;
                init = true;
            }
            var c = cam.transform.position;
            transform.localPosition = basePos + new Vector3(c.x * factor, c.y * factor * vertical, 0f);
        }
    }

    /// <summary>Lightweight ambient animation for stage decor (bob, sway, alpha pulse, wrap-around drift).</summary>
    public class Ambient : MonoBehaviour
    {
        public enum Mode { Bob, Sway, Pulse, Drift, Orbit }

        public Mode mode;
        public Vector2 amount;
        public float speed = 1f, phase;
        public Vector2 velocity;
        public float wrapMin, wrapMax;
        public bool wrapY;

        Vector3 basePos;
        float baseRot;
        SpriteRenderer sr;
        Color baseColor;

        public static Ambient Add(Component c, Mode mode, Vector2 amount, float speed, float phase)
        {
            var a = c.gameObject.AddComponent<Ambient>();
            a.mode = mode;
            a.amount = amount;
            a.speed = speed;
            a.phase = phase;
            return a;
        }

        void Start()
        {
            basePos = transform.localPosition;
            baseRot = transform.localEulerAngles.z;
            sr = GetComponent<SpriteRenderer>();
            if (sr != null) baseColor = sr.color;
        }

        void Update()
        {
            float t = Time.time * speed + phase;
            switch (mode)
            {
                case Mode.Bob:
                    transform.localPosition = basePos + (Vector3)(amount * Mathf.Sin(t));
                    break;
                case Mode.Sway:
                    transform.localRotation = Quaternion.Euler(0f, 0f, baseRot + amount.x * Mathf.Sin(t));
                    break;
                case Mode.Pulse:
                    if (sr != null)
                    {
                        var c = baseColor;
                        c.a = baseColor.a * (1f - amount.x * (0.5f + 0.5f * Mathf.Sin(t)));
                        sr.color = c;
                    }
                    break;
                case Mode.Orbit:
                    transform.localPosition = basePos + new Vector3(Mathf.Cos(t) * amount.x, Mathf.Sin(t) * amount.y, 0f);
                    var sc = transform.localScale;
                    float face = -Mathf.Sin(t) * Mathf.Sign(speed);
                    if (Mathf.Abs(face) > 0.05f) transform.localScale = new Vector3(Mathf.Abs(sc.x) * Mathf.Sign(face), sc.y, sc.z);
                    break;
                case Mode.Drift:
                    var p = transform.localPosition + (Vector3)(velocity * Time.deltaTime);
                    float v = wrapY ? p.y : p.x;
                    if (v > wrapMax) v = wrapMin;
                    else if (v < wrapMin) v = wrapMax;
                    if (wrapY) p.y = v; else p.x = v;
                    transform.localPosition = p;
                    break;
            }
        }
    }
}
