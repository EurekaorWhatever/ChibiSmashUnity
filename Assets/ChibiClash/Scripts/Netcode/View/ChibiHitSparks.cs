using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Pooled hit sparks and muzzle flares (presentation only): a 4- or 6-point star flare (white-hot spikes over wider
    /// colour spikes), a hot core + colour halo, radial speed lines and a quick shock ring. Additive HDR sprites
    /// (<see cref="ChibiFxTextures.SpriteLight"/>), sorted 62..67 (above fighters 9..45, below the stage foreground 70 and
    /// Fx particles 80). Unscaled time: hitstop does not stretch them. Pool of <see cref="PoolSize"/> sprites built once,
    /// recycled oldest-first when full; no per-frame allocation.
    /// Budget: a full-strength hit uses 20 sprites, a muzzle flare 9; 128 covers ~6 overlapping strong hits.
    /// </summary>
    public sealed class ChibiHitSparks
    {
        /// <summary>Sprites in the pool.</summary>
        public const int PoolSize = 128;
        // Sorting: halo < ring < lines < colour spikes < white spikes < core.
        const int OrderHalo = 62, OrderRing = 63, OrderLine = 64, OrderSpikeColor = 65, OrderSpike = 66, OrderCore = 67;
        const float P = ChibiFxTextures.Peak;

        enum Fade : byte { Out, Flash }

        sealed class Item
        {
            public Transform Tr;
            public SpriteRenderer Sr;
            public bool Live;
            public float T, Life, Drag, NatH;
            public Vector2 Pos, Vel, S0, S1;   // world width / height at start and end
            public float Rot, A0, FadePow;
            public Fade Mode;
            public Color Col;
        }

        readonly Item[] _items = new Item[PoolSize];
        int _next;

        /// <summary>Builds the pool under <paramref name="parent"/>.</summary>
        public ChibiHitSparks(Transform parent)
        {
            var root = new GameObject("HitSparks").transform;
            root.SetParent(parent, false);
            var mat = ChibiFxTextures.SpriteLight;
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject("Spark");
                go.transform.SetParent(root, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sharedMaterial = mat;
                sr.enabled = false;
                _items[i] = new Item { Tr = go.transform, Sr = sr };
            }
        }

        /// <summary>
        /// Hit spark at <paramref name="at"/> in attacker colour <paramref name="fx"/>; <paramref name="strength"/> 0..1 from the
        /// hit's percent (bigger, longer, 6 points above 0.45).
        /// </summary>
        public void Hit(Vector2 at, Color fx, float strength)
        {
            float s = Mathf.Clamp01(strength);
            float life = 0.15f + 0.1f * s;
            int points = s > 0.45f ? 6 : 4;
            int rays = points / 2;

            // Hot core + colour halo.
            Spawn(ChibiFxTextures.Glow, ChibiFxTextures.RoundH, OrderCore, at, Vector2.zero, 0f,
                  Sq(0.45f + 0.35f * s), Sq(0.8f + 0.6f * s), Hdr(Color.white, 2.6f), 1f, 1.6f, life * 0.8f, Fade.Flash);
            Spawn(ChibiFxTextures.Glow, ChibiFxTextures.RoundH, OrderHalo, at, Vector2.zero, 0f,
                  Sq(1.0f + 1.0f * s), Sq(1.5f + 1.5f * s), Hdr(fx, 1.5f), 0.7f, 1.4f, life, Fade.Out);

            // Star flare: rays spanning both directions through the centre, near-axis so it reads as a star, not noise.
            float baseAng = FxRandom.Range(-14f, 14f) + (points == 4 ? 0f : 90f);
            float len = 1.7f + 2.3f * s, thick = 0.2f + 0.1f * s;
            for (int j = 0; j < rays; j++)
            {
                float ang = baseAng + j * 180f / rays;
                float lj = j == 0 ? len : len * 0.82f;                       // one dominant spike
                Spawn(ChibiFxTextures.Ray, ChibiFxTextures.RayH, OrderSpikeColor, at, Vector2.zero, ang,
                      new Vector2(lj * 0.55f, thick * 1.9f), new Vector2(lj * 1.25f, 0f), Hdr(fx, 1.9f), 0.9f, 1.2f, life, Fade.Out);
                Spawn(ChibiFxTextures.Ray, ChibiFxTextures.RayH, OrderSpike, at, Vector2.zero, ang,
                      new Vector2(lj * 0.45f, thick), new Vector2(lj, 0f), Hdr(Color.white, 2.8f), 1f, 1f, life * 0.9f, Fade.Out);
            }

            // Radial speed lines flying out.
            int lines = 7 + Mathf.RoundToInt(4f * s);
            var lineCol = Hdr(Color.Lerp(Color.white, fx, 0.35f), 2.2f);
            for (int j = 0; j < lines; j++)
            {
                float a = (j + FxRandom.Range(-0.35f, 0.35f)) * (360f / lines) * Mathf.Deg2Rad;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                float l = (0.45f + 0.7f * s) * FxRandom.Range(0.7f, 1.2f);
                Spawn(ChibiFxTextures.Line, ChibiFxTextures.LineH, OrderLine, at + d * (0.25f + 0.2f * s), d * (8f + 9f * s) * FxRandom.Range(0.8f, 1.15f),
                      a * Mathf.Rad2Deg, new Vector2(l, 0.07f + 0.04f * s), new Vector2(l * 0.25f, 0.03f), lineCol, 1f, 1f, life * 0.9f, Fade.Out, 6f);
            }

            // Shock ring.
            Spawn(ChibiFxTextures.Ring, ChibiFxTextures.RoundH, OrderRing, at, Vector2.zero, 0f,
                  Sq(0.35f), Sq(1.5f + 1.7f * s), Hdr(fx, 2.2f), 0.9f, 1.3f, life * 1.1f, Fade.Out);
        }

        /// <summary>Small muzzle flare at <paramref name="at"/> pointing along <paramref name="dir"/> (shots, casts).</summary>
        public void Muzzle(Vector2 at, Vector2 dir, Color fx)
        {
            if (dir.sqrMagnitude < 1e-6f) dir = Vector2.right;
            dir.Normalize();
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            const float life = 0.13f;
            Spawn(ChibiFxTextures.Glow, ChibiFxTextures.RoundH, OrderCore, at, Vector2.zero, 0f, Sq(0.3f), Sq(0.55f),
                  Hdr(Color.white, 2.4f), 1f, 1.6f, life * 0.8f, Fade.Flash);
            Spawn(ChibiFxTextures.Glow, ChibiFxTextures.RoundH, OrderHalo, at, Vector2.zero, 0f, Sq(0.6f), Sq(1.1f),
                  Hdr(fx, 1.5f), 0.8f, 1.4f, life, Fade.Out);
            Spawn(ChibiFxTextures.Ray, ChibiFxTextures.RayH, OrderSpikeColor, at + dir * 0.2f, Vector2.zero, ang,
                  new Vector2(0.6f, 0.26f), new Vector2(1.3f, 0f), Hdr(fx, 1.9f), 0.9f, 1.2f, life, Fade.Out);
            Spawn(ChibiFxTextures.Ray, ChibiFxTextures.RayH, OrderSpike, at + dir * 0.2f, Vector2.zero, ang,
                  new Vector2(0.5f, 0.14f), new Vector2(1.0f, 0f), Hdr(Color.white, 2.8f), 1f, 1f, life, Fade.Out);
            Spawn(ChibiFxTextures.Ray, ChibiFxTextures.RayH, OrderSpike, at, Vector2.zero, ang + 90f,
                  new Vector2(0.25f, 0.1f), new Vector2(0.5f, 0f), Hdr(Color.white, 2.4f), 1f, 1f, life * 0.8f, Fade.Out);
            var lineCol = Hdr(Color.Lerp(Color.white, fx, 0.35f), 2f);
            for (int j = -1; j <= 1; j++)
            {
                float a = (ang + j * 22f + FxRandom.Range(-6f, 6f)) * Mathf.Deg2Rad;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Spawn(ChibiFxTextures.Line, ChibiFxTextures.LineH, OrderLine, at + d * 0.25f, d * 9f, a * Mathf.Rad2Deg,
                      new Vector2(0.45f, 0.06f), new Vector2(0.12f, 0.03f), lineCol, 1f, 1f, life, Fade.Out, 6f);
            }
            Spawn(ChibiFxTextures.Ring, ChibiFxTextures.RoundH, OrderRing, at, Vector2.zero, 0f, Sq(0.2f), Sq(0.8f),
                  Hdr(fx, 2f), 0.8f, 1.3f, life, Fade.Out);
        }

        /// <summary>Advance and draw every live sprite by unscaled <paramref name="dt"/>.</summary>
        public void Tick(float dt)
        {
            for (int i = 0; i < PoolSize; i++)
            {
                var it = _items[i];
                if (!it.Live) continue;
                it.T += dt;
                float k = it.T / it.Life;
                if (k >= 1f)
                {
                    it.Live = false;
                    it.Sr.enabled = false;
                    continue;
                }
                if (it.Drag > 0f) it.Vel *= Mathf.Exp(-it.Drag * dt);
                it.Pos += it.Vel * dt;
                float e = 1f - (1f - k) * (1f - k) * (1f - k);           // ease-out cubic for size
                var wh = Vector2.Lerp(it.S0, it.S1, e);
                float a = it.Mode == Fade.Flash
                    ? it.A0 * Mathf.Pow(1f - k, it.FadePow) * (k < 0.08f ? 1f : 0.85f)
                    : it.A0 * Mathf.Pow(1f - k, it.FadePow);
                it.Tr.localPosition = it.Pos;
                it.Tr.localScale = new Vector3(Mathf.Max(0.0001f, wh.x), Mathf.Max(0.0001f, wh.y / it.NatH), 1f);
                var c = it.Col;
                c.a *= a;
                it.Sr.color = c;
            }
        }

        static Vector2 Sq(float v) => new Vector2(v, v);

        /// <summary>Colour at <paramref name="k"/> x on screen (authored / Peak; the material multiplies Peak back).</summary>
        static Color Hdr(Color c, float k) => new Color(Mathf.Clamp01(c.r * k / P), Mathf.Clamp01(c.g * k / P), Mathf.Clamp01(c.b * k / P), 1f);

        void Spawn(Sprite spr, float natH, int order, Vector2 pos, Vector2 vel, float rotDeg, Vector2 s0, Vector2 s1, Color col,
                   float a0, float fadePow, float life, Fade mode, float drag = 0f)
        {
            var it = _items[_next];
            _next = (_next + 1) % PoolSize;
            it.Live = true;
            it.T = 0f;
            it.Life = Mathf.Max(0.02f, life);
            it.Pos = pos;
            it.Vel = vel;
            it.Drag = drag;
            it.S0 = s0;
            it.S1 = s1;
            it.NatH = natH;
            it.Rot = rotDeg;
            it.A0 = a0;
            it.FadePow = fadePow;
            it.Mode = mode;
            it.Col = col;
            if (it.Sr.sprite != spr) it.Sr.sprite = spr;
            it.Sr.sortingOrder = order;
            it.Tr.localRotation = Quaternion.Euler(0f, 0f, rotDeg);
            it.Tr.localPosition = pos;
            it.Tr.localScale = new Vector3(s0.x, s0.y / natH, 1f);
            col.a = 0f;
            it.Sr.color = col;
            it.Sr.enabled = true;
        }
    }
}
