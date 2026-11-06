using UnityEngine;

namespace ChibiClash
{
    public class StageInfo
    {
        public string name;
        public float halfWidth;
        public float groundY;
        public float blastX, killY, ceilingY;
        public Vector2[] spawns;
        public Vector2 respawn;
        public Color sky;
    }

    /// <summary>Builds the three arenas from primitives: a solid main stage plus one-way floating platforms.</summary>
    public static class StageBuilder
    {
        public static readonly string[] Names = { "Sky Dojo", "Neon City", "Lava Forge" };

        public static StageInfo Build(int index, Transform parent)
        {
            var root = new GameObject("Stage").transform;
            root.SetParent(parent, false);
            StageInfo s;
            switch (index)
            {
                case 1:
                    s = Info("Neon City", 9f, -2.5f, new Color(0.07f, 0.05f, 0.16f));
                    Decor(root, new Color(0.9f, 0.2f, 0.8f, 0.25f), new Color(0.2f, 0.9f, 1f, 0.2f), true);
                    Main(root, s, new Color(0.2f, 0.18f, 0.32f), new Color(0.2f, 0.95f, 1f));
                    Float(root, new Vector2(-5.5f, -0.2f), 3.4f, new Color(1f, 0.3f, 0.8f));
                    Float(root, new Vector2(5.5f, -0.2f), 3.4f, new Color(1f, 0.3f, 0.8f));
                    Float(root, new Vector2(0f, 2.0f), 4f, new Color(0.2f, 0.95f, 1f));
                    break;
                case 2:
                    s = Info("Lava Forge", 7f, -2.5f, new Color(0.22f, 0.07f, 0.05f));
                    Decor(root, new Color(1f, 0.4f, 0.1f, 0.25f), new Color(1f, 0.8f, 0.2f, 0.15f), false);
                    Art.Sprite(root, "Lava", Art.Soft, new Color(1f, 0.35f, 0.05f, 0.8f), new Vector2(0f, -9f), new Vector2(40f, 8f), -50);
                    Main(root, s, new Color(0.25f, 0.2f, 0.2f), new Color(1f, 0.45f, 0.1f));
                    Float(root, new Vector2(-3.6f, 0.6f), 3f, new Color(1f, 0.6f, 0.2f));
                    Float(root, new Vector2(3.6f, 0.6f), 3f, new Color(1f, 0.6f, 0.2f));
                    break;
                default:
                    s = Info("Sky Dojo", 8f, -2.5f, new Color(0.45f, 0.72f, 1f));
                    Decor(root, new Color(1f, 1f, 1f, 0.55f), new Color(1f, 0.95f, 0.8f, 0.5f), false);
                    Main(root, s, new Color(0.55f, 0.4f, 0.3f), new Color(0.45f, 0.85f, 0.4f));
                    Float(root, new Vector2(-4.2f, 0.4f), 3.4f, new Color(0.95f, 0.85f, 0.6f));
                    Float(root, new Vector2(4.2f, 0.4f), 3.4f, new Color(0.95f, 0.85f, 0.6f));
                    Float(root, new Vector2(0f, 2.9f), 3f, new Color(0.95f, 0.85f, 0.6f));
                    break;
            }
            var cam = Camera.main;
            if (cam != null) cam.backgroundColor = s.sky;
            return s;
        }

        static StageInfo Info(string name, float halfWidth, float groundY, Color sky)
        {
            return new StageInfo
            {
                name = name,
                halfWidth = halfWidth,
                groundY = groundY,
                blastX = halfWidth + 9f,
                killY = groundY - 9f,
                ceilingY = 16f,
                spawns = new[] { new Vector2(-halfWidth * 0.45f, groundY + 1.5f), new Vector2(halfWidth * 0.45f, groundY + 1.5f) },
                respawn = new Vector2(0f, 6f),
                sky = sky
            };
        }

        static void Main(Transform root, StageInfo s, Color body, Color top)
        {
            var go = new GameObject("MainStage");
            go.transform.SetParent(root, false);
            go.transform.position = new Vector2(0f, s.groundY - 0.6f);
            var col = go.AddComponent<BoxCollider2D>();
            col.size = new Vector2(s.halfWidth * 2f, 1.2f);
            Art.Sprite(go.transform, "Body", Art.Rounded, body, new Vector2(0f, -0.4f), new Vector2(s.halfWidth * 2f, 2f), -10);
            Art.Sprite(go.transform, "Underside", Art.Soft, Color.Lerp(body, Color.black, 0.4f), new Vector2(0f, -1.5f), new Vector2(s.halfWidth * 1.6f, 2.2f), -11);
            Art.Sprite(go.transform, "Top", Art.Rounded, top, new Vector2(0f, 0.5f), new Vector2(s.halfWidth * 2f + 0.2f, 0.32f), -9);
        }

        static void Float(Transform root, Vector2 pos, float width, Color c)
        {
            var go = new GameObject("Platform");
            go.transform.SetParent(root, false);
            go.transform.position = pos;
            var col = go.AddComponent<BoxCollider2D>();
            col.size = new Vector2(width, 0.3f);
            col.usedByEffector = true;
            var eff = go.AddComponent<PlatformEffector2D>();
            eff.useOneWay = true;
            eff.surfaceArc = 170f;
            Art.Sprite(go.transform, "Slab", Art.Rounded, c, Vector2.zero, new Vector2(width, 0.3f), -8);
            Art.Sprite(go.transform, "Shadow", Art.Soft, new Color(0f, 0f, 0f, 0.2f), new Vector2(0f, -0.35f), new Vector2(width * 0.9f, 0.4f), -12);
        }

        static void Decor(Transform root, Color a, Color b, bool city)
        {
            var rnd = new System.Random(7);
            for (int i = 0; i < 14; i++)
            {
                float x = (float)rnd.NextDouble() * 50f - 25f;
                float y = (float)rnd.NextDouble() * 14f - 2f;
                float s = 1.5f + (float)rnd.NextDouble() * 4f;
                if (city)
                {
                    float h = 3f + (float)rnd.NextDouble() * 9f;
                    Art.Sprite(root, "Tower", Art.Square, new Color(0.12f, 0.1f, 0.25f), new Vector2(x, -6f + h * 0.5f), new Vector2(1.5f + s * 0.4f, h), -40);
                    Art.Sprite(root, "Neon", Art.Square, i % 2 == 0 ? a : b, new Vector2(x, -6f + h), new Vector2(1.5f + s * 0.4f, 0.12f), -39);
                }
                else
                {
                    Art.Sprite(root, "Cloud", Art.Soft, i % 2 == 0 ? a : b, new Vector2(x, y), new Vector2(s * 1.8f, s), -45);
                }
            }
            Art.Sprite(root, "Sun", Art.Soft, b, new Vector2(9f, 9f), new Vector2(8f, 8f), -48);
        }
    }

    /// <summary>Smash-style camera: frames both fighters, zooms with their distance, shakes on impact.</summary>
    public class CameraController : MonoBehaviour
    {
        public Fighter[] targets;
        Camera cam;
        float shake;
        Fighter focus;
        float focusUntil;
        Vector3 pos;

        void Awake()
        {
            cam = GetComponent<Camera>();
            cam.orthographic = true;
            pos = transform.position;
        }

        public void Shake(float amount) => shake = Mathf.Max(shake, amount);

        /// <summary>Stop following <paramref name="owner"/>'s fighters, unless a newer match has already taken over the camera (Rematch).</summary>
        public void ReleaseTargets(Fighter[] owner)
        {
            if (targets == owner) targets = null;
        }

        public void Focus(Fighter f, float seconds)
        {
            focus = f;
            focusUntil = Time.unscaledTime + seconds;
        }

        void LateUpdate()
        {
            if (targets == null || targets.Length == 0 || targets[0] == null) return;
            float dt = Time.unscaledDeltaTime;
            Vector2 goal;
            float size;
            if (focus != null && Time.unscaledTime < focusUntil)
            {
                goal = focus.Center;
                size = 3.6f;
            }
            else
            {
                Vector2 a = targets[0].Center, b = targets[1].Center;
                goal = (a + b) * 0.5f + Vector2.up * 1.2f;
                goal.y = Mathf.Max(goal.y, 0.5f);
                float dist = Mathf.Max(Mathf.Abs(a.x - b.x) * 0.5f / cam.aspect, Mathf.Abs(a.y - b.y) * 0.5f);
                size = Mathf.Clamp(dist + 3.2f, 5.5f, 10f);
            }
            pos = Vector3.Lerp(pos, new Vector3(goal.x, goal.y, -10f), 1f - Mathf.Exp(-dt * 6f));
            cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, size, 1f - Mathf.Exp(-dt * 5f));
            Vector3 offset = Vector3.zero;
            if (shake > 0f)
            {
                offset = (Vector3)(FxRandom.InsideUnitCircle * shake * 0.6f);
                shake = Mathf.MoveTowards(shake, 0f, dt * 2.5f);
            }
            transform.position = pos + offset;
        }
    }
}
