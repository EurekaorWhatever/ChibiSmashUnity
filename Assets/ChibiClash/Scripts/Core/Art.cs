using UnityEngine;

namespace ChibiClash
{
    /// <summary>Procedurally generated sprites, textures and materials so the game has no asset dependencies.</summary>
    public static class Art
    {
        static Texture2D solidTex, circleTex, ringTex, softTex, roundedTex;
        static UnityEngine.Sprite square, circle, ring, soft, rounded;
        static Material spriteMat, particleMat;

        public static Texture2D SolidTex => solidTex ? solidTex : (solidTex = MakeSolid());
        public static Texture2D CircleTex => circleTex ? circleTex : (circleTex = MakeCircle(128, 0f));
        public static Texture2D RingTex => ringTex ? ringTex : (ringTex = MakeCircle(128, 0.8f));
        public static Texture2D SoftTex => softTex ? softTex : (softTex = MakeSoft(64));
        public static Texture2D RoundedTex => roundedTex ? roundedTex : (roundedTex = MakeRounded(64, 18));

        public static UnityEngine.Sprite Square => square ? square : (square = Make(SolidTex));
        public static UnityEngine.Sprite Circle => circle ? circle : (circle = Make(CircleTex));
        public static UnityEngine.Sprite Ring => ring ? ring : (ring = Make(RingTex));
        public static UnityEngine.Sprite Soft => soft ? soft : (soft = Make(SoftTex));
        public static UnityEngine.Sprite Rounded => rounded ? rounded : (rounded = Make(RoundedTex));

        public static Material SpriteMat
        {
            get
            {
                if (spriteMat) return spriteMat;
                var sh = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                spriteMat = new Material(sh) { name = "ChibiSpriteUnlit" };
                return spriteMat;
            }
        }

        public static Material ParticleMat
        {
            get
            {
                if (particleMat) return particleMat;
                particleMat = new Material(SpriteMat) { name = "ChibiParticle", mainTexture = SoftTex };
                return particleMat;
            }
        }

        static UnityEngine.Sprite Make(Texture2D t)
        {
            return UnityEngine.Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width);
        }

        static Texture2D NewTex(int size)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
        }

        static Texture2D MakeSolid()
        {
            var t = NewTex(4);
            var px = new Color32[16];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static Texture2D MakeCircle(int size, float inner)
        {
            var t = NewTex(size);
            var px = new Color32[size * size];
            float r = size / 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
                float a = Mathf.Clamp01((1f - d) * r * 0.5f);
                if (inner > 0f) a = Mathf.Min(a, Mathf.Clamp01((d - inner) * r * 0.5f));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static Texture2D MakeSoft(int size)
        {
            var t = NewTex(size);
            var px = new Color32[size * size];
            float r = size / 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
                float a = Mathf.Clamp01(1f - d);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * a * 255));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static Texture2D MakeRounded(int size, int radius)
        {
            var t = NewTex(size);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                float a = Mathf.Clamp01(radius - d + 0.5f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        public static SpriteRenderer Sprite(Transform parent, string name, UnityEngine.Sprite s, Color c, Vector2 pos, Vector2 scale, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = s;
            sr.color = c;
            sr.sortingOrder = order;
            sr.sharedMaterial = SpriteMat;
            return sr;
        }
    }
}
