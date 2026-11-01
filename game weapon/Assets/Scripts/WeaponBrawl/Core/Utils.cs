using System;
using UnityEngine;

namespace WeaponBrawl
{
    public static class Arena
    {
        public const float GroundY = 0f;
        public const float HalfWidth = 11f;
    }

    /// <summary>Rigidbody2D.velocity was renamed to linearVelocity in Unity 6.</summary>
    public static class PhysicsCompat
    {
        public static Vector2 GetVelocity(this Rigidbody2D rb)
        {
#if UNITY_6000_0_OR_NEWER
            return rb.linearVelocity;
#else
            return rb.velocity;
#endif
        }

        public static void SetVelocity(this Rigidbody2D rb, Vector2 velocity)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = velocity;
#else
            rb.velocity = velocity;
#endif
        }
    }

    public static class Ease
    {
        public static float Linear(float t) => t;
        public static float InQuad(float t) => t * t;
        public static float OutQuad(float t) => 1f - (1f - t) * (1f - t);
        public static float OutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);
        public static float OutExpo(float t) => t >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * t);

        public static float OutBack(float t)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
        }
    }

    /// <summary>Generates simple sprites at runtime so the prototype runs with zero art assets.</summary>
    public static class SpriteFactory
    {
        static Sprite square, circle, ring, rounded;
        static Material lineMaterial;

        public static Sprite Square
        {
            get
            {
                if (square == null)
                {
                    var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                    var px = new Color32[16];
                    for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
                    tex.SetPixels32(px);
                    tex.Apply();
                    square = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
                }
                return square;
            }
        }

        public static Sprite Circle
        {
            get
            {
                if (circle == null) circle = CreateRadial(64, r => Mathf.Clamp01((1f - r) * 32f));
                return circle;
            }
        }

        public static Sprite Ring
        {
            get
            {
                if (ring == null) ring = CreateRadial(128, r => Mathf.Clamp01((1f - r) * 64f) * Mathf.Clamp01((r - 0.82f) * 64f));
                return ring;
            }
        }

        /// <summary>9-sliced rounded rectangle for UI panels and buttons.</summary>
        public static Sprite Rounded
        {
            get
            {
                if (rounded == null)
                {
                    const int size = 48;
                    const float radius = 16f;
                    var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    var px = new Color32[size * size];
                    for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        var c = new Vector2(Mathf.Clamp(p.x, radius, size - radius), Mathf.Clamp(p.y, radius, size - radius));
                        float a = Mathf.Clamp01(radius - Vector2.Distance(p, c) + 0.5f);
                        px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                    }
                    tex.SetPixels32(px);
                    tex.Apply();
                    rounded = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                        SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
                }
                return rounded;
            }
        }

        public static Material LineMaterial
        {
            get
            {
                if (lineMaterial == null)
                {
                    var shader = Shader.Find("Sprites/Default");
                    if (shader == null) shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
                    if (shader == null) shader = Shader.Find("Unlit/Color");
                    lineMaterial = new Material(shader);
                }
                return lineMaterial;
            }
        }

        static Sprite CreateRadial(int size, Func<float, float> alpha)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float half = size / 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(half, half)) / half;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(alpha(r) * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        public static SpriteRenderer Part(Transform parent, string name, Sprite sprite, Color color, Vector2 localPos, Vector2 scale, int order, float rotation = 0f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, 0f, rotation);
            go.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            return sr;
        }
    }
}
