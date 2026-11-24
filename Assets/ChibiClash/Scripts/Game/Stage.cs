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
        /// <summary>Live art of a design stage (3..7): bind it to the displayed sim state. Null for the classic stages.</summary>
        public ChibiDesignStage design;
    }

    /// <summary>
    /// Builds the three arenas: a solid main stage plus one-way floating platforms. The collider geometry below is the
    /// visual twin of the simulation's Netcode/ChibiStageTable and must not change; the painted art on top of it lives in
    /// StageMaps.cs (UI-1, decoration only).
    /// </summary>
    public static partial class StageBuilder
    {
        /// <summary>
        /// Stage names by sim index (ChibiStageTable): 0..2 the classic arenas, 3..7 the map design sheet's five stages
        /// (decision ST-1). Every menu loops over this array, so a new stage only needs entries here and in the sim table.
        /// </summary>
        public static readonly string[] Names =
        {
            "Sky Dojo", "Neon City", "Lava Forge",
            ChibiDesignStage.Names[0], ChibiDesignStage.Names[1], ChibiDesignStage.Names[2], ChibiDesignStage.Names[3], ChibiDesignStage.Names[4],
        };
        /// <summary>Compact names for stage buttons.</summary>
        public static readonly string[] ShortNames =
        {
            "Sky Dojo", "Neon City", "Lava Forge",
            ChibiDesignStage.ShortNames[0], ChibiDesignStage.ShortNames[1], ChibiDesignStage.ShortNames[2], ChibiDesignStage.ShortNames[3], ChibiDesignStage.ShortNames[4],
        };
        // Loc keys per stage index (stage.3.name ...); the English arrays above stay the source.
        static readonly string[] NameKeys = { "stage.0.name", "stage.1.name", "stage.2.name", "stage.3.name", "stage.4.name", "stage.5.name", "stage.6.name", "stage.7.name" };
        static readonly string[] ShortKeys = { "stage.0.short", "stage.1.short", "stage.2.short", "stage.3.short", "stage.4.short", "stage.5.short", "stage.6.short", "stage.7.short" };
        static readonly string[] TaglineKeys = { "stage.0.tagline", "stage.1.tagline", "stage.2.tagline", "stage.3.tagline", "stage.4.tagline", "stage.5.tagline", "stage.6.tagline", "stage.7.tagline" };

        /// <summary>Stage i's name in the current language.</summary>
        public static string NameText(int i) => i >= 0 && i < NameKeys.Length ? Loc.T(NameKeys[i]) : Names[i];
        /// <summary>Stage i's compact name in the current language.</summary>
        public static string ShortNameText(int i) => i >= 0 && i < ShortKeys.Length ? Loc.T(ShortKeys[i]) : ShortNames[i];
        /// <summary>Stage i's tagline in the current language.</summary>
        public static string TaglineText(int i) => i >= 0 && i < TaglineKeys.Length ? Loc.T(TaglineKeys[i]) : Taglines[i];

        /// <summary>Order stages are offered in menus: the five design stages first, then the classics (ST-1).</summary>
        public static readonly int[] MenuOrder = { 3, 4, 5, 6, 7, 0, 1, 2 };
        /// <summary>Default stage for new matches (Mountain of the Beginning).</summary>
        public const int DefaultStage = 3;

        // Stage-select preview palette (sky, main platform, trim) and a one-line description; mirrors Build below.
        // Design stages (3..7) draw their real illustration instead (StagePreview), these colours are only the fallback.
        public static readonly Color[] PreviewSky =
        {
            new Color(0.42f, 0.68f, 0.95f), new Color(0.07f, 0.05f, 0.16f), new Color(0.22f, 0.07f, 0.05f),
            ChibiDesignStage.Sky[0], ChibiDesignStage.Sky[1], ChibiDesignStage.Sky[2], ChibiDesignStage.Sky[3], ChibiDesignStage.Sky[4],
        };
        public static readonly Color[] PreviewGround =
        {
            new Color(0.64f, 0.61f, 0.55f), new Color(0.22f, 0.19f, 0.34f), new Color(0.17f, 0.14f, 0.16f),
            new Color(0.71f, 0.66f, 0.55f), new Color(0.1f, 0.6f, 0.29f), new Color(0.11f, 0.06f, 0.13f),
            new Color(0.54f, 0.35f, 0.23f), new Color(0.73f, 0.87f, 0.89f),
        };
        public static readonly Color[] PreviewTrim =
        {
            new Color(0.3f, 0.82f, 0.6f), new Color(0.2f, 0.95f, 1f), new Color(1f, 0.45f, 0.1f),
            new Color(0.49f, 1f, 0.82f), new Color(0.79f, 0.95f, 0.37f), new Color(1f, 0.6f, 0.12f),
            new Color(1f, 0.75f, 0.41f), new Color(0.49f, 0.97f, 1f),
        };
        public static readonly string[] Taglines =
        {
            "Mountain shrine above the clouds", "Rooftop arena under neon rain", "Molten foundry over a lava sea",
            ChibiDesignStage.Taglines[0], ChibiDesignStage.Taglines[1], ChibiDesignStage.Taglines[2], ChibiDesignStage.Taglines[3], ChibiDesignStage.Taglines[4],
        };

        /// <summary>Illustration thumbnail of a design stage (3..7), loaded once; null for the classic stages.</summary>
        public static Texture2D Thumbnail(int index)
        {
            if (!ChibiDesignStage.IsDesign(index)) return null;
            int k = index - 3;
            if (_thumbs[k] == null)
            {
                string[] ids = { "Mountain", "Plain", "Volcano", "BirdLair", "Ocean" };
                _thumbs[k] = Resources.Load<Texture2D>("Stages/" + ids[k] + "/Full");
            }
            return _thumbs[k];
        }
        static readonly Texture2D[] _thumbs = new Texture2D[5];

        public static StageInfo Build(int index, Transform parent)
        {
            var root = new GameObject("Stage").transform;
            root.SetParent(parent, false);
            StageInfo s;
            if (ChibiDesignStage.IsDesign(index))
            {
                // The map design sheet's stages: layered art synced to the sim (StageDesign.cs). Sim Y 0 = world 0.
                s = ChibiDesignStage.Build(index, root);
                var c = Camera.main;
                if (c != null) c.backgroundColor = s.sky;
                return s;
            }
            switch (index)
            {
                case 1:
                    s = Info("Neon City", 9f, -2.5f, new Color(0.07f, 0.05f, 0.16f));
                    NeonCityBackdrop(root, s);
                    Main(root, s);
                    NeonCityMain(root, s);
                    NeonCityPlatform(Float(root, new Vector2(-5.5f, -0.2f), 3.4f), 3.4f, 0);
                    NeonCityPlatform(Float(root, new Vector2(5.5f, -0.2f), 3.4f), 3.4f, 1);
                    NeonCityPlatform(Float(root, new Vector2(0f, 2.0f), 4f), 4f, 2);
                    break;
                case 2:
                    s = Info("Lava Forge", 7f, -2.5f, new Color(0.22f, 0.07f, 0.05f));
                    LavaForgeBackdrop(root, s);
                    Main(root, s);
                    LavaForgeMain(root, s);
                    LavaForgePlatform(Float(root, new Vector2(-3.6f, 0.6f), 3f), 3f, 0);
                    LavaForgePlatform(Float(root, new Vector2(3.6f, 0.6f), 3f), 3f, 1);
                    break;
                default:
                    s = Info("Sky Dojo", 8f, -2.5f, new Color(0.45f, 0.72f, 1f));
                    SkyDojoBackdrop(root, s);
                    Main(root, s);
                    SkyDojoMain(root, s);
                    SkyDojoPlatform(Float(root, new Vector2(-4.2f, 0.4f), 3.4f), 3.4f, 0);
                    SkyDojoPlatform(Float(root, new Vector2(4.2f, 0.4f), 3.4f), 3.4f, 1);
                    SkyDojoPlatform(Float(root, new Vector2(0f, 2.9f), 3f), 3f, 2);
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

        /// <summary>Solid main-stage collider (top at groundY). Art is drawn separately (StageMaps.cs).</summary>
        static void Main(Transform root, StageInfo s)
        {
            var go = new GameObject("MainStage");
            go.transform.SetParent(root, false);
            go.transform.position = new Vector2(0f, s.groundY - 0.6f);
            var col = go.AddComponent<BoxCollider2D>();
            col.size = new Vector2(s.halfWidth * 2f, 1.2f);
        }

        /// <summary>One-way floating platform collider; the platform art goes on the returned transform.</summary>
        static Transform Float(Transform root, Vector2 pos, float width)
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
            Art.Sprite(go.transform, "Shadow", Art.Soft, new Color(0f, 0f, 0f, 0.2f), new Vector2(0f, -0.35f), new Vector2(width * 0.9f, 0.4f), -12);
            return go.transform;
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
