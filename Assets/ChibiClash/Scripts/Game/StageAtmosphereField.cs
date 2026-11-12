using UnityEngine;

namespace ChibiClash
{
    /// <summary>How a field particle moves and turns.</summary>
    public enum AtmosMotion
    {
        /// <summary>Straight drift + sideways wobble, optional constant spin.</summary>
        Drift,
        /// <summary>Petals, feathers, blades: spin plus a fake 3D flip (X scale swings through zero).</summary>
        Tumble,
        /// <summary>Streaks and spray: the sprite's X axis points along the velocity.</summary>
        Align,
        /// <summary>Jellyfish: the bell contracts and the body rises in pulses.</summary>
        Pulse,
        /// <summary>Heat shimmer: slow breathing width and sideways sway.</summary>
        Shimmer,
    }

    /// <summary>Recipe for an <see cref="AtmosField"/> (allocated once when the stage is built).</summary>
    public sealed class AtmosFieldDesc
    {
        /// <summary>Shape from the atlas.</summary>
        public AtmosSprite Sprite = AtmosSprite.Dot;
        /// <summary>Pooled particle count (constant; dead particles respawn).</summary>
        public int Count = 10;
        /// <summary>Sorting order (see AtmosFx.Order*).</summary>
        public int Order = AtmosFx.OrderMid;
        /// <summary>Parallax: fraction of the camera offset the layer follows (0 = gameplay plane, &gt;0 far, &lt;0 near).</summary>
        public float Follow;
        /// <summary>Spawn area in layer space (world units at camera 0, 0).</summary>
        public Rect Spawn = new Rect(-16f, -4f, 32f, 16f);
        /// <summary>Initial velocity range (units/s); X is mirrored by the field's DirX at spawn.</summary>
        public Vector2 VelMin, VelMax;
        /// <summary>Constant acceleration (units/s^2).</summary>
        public Vector2 Accel;
        /// <summary>Sideways wobble amplitude (units) and frequency (Hz).</summary>
        public float WobbleAmp, WobbleFreq = 0.5f;
        /// <summary>Lifetime range (s).</summary>
        public float LifeMin = 5f, LifeMax = 8f;
        /// <summary>Uniform scale range (the sprite's native size is 1 unit per 128 px cell) and Y/X aspect.</summary>
        public float SizeMin = 0.2f, SizeMax = 0.3f, AspectY = 1f;
        /// <summary>Tint range (each particle picks a point between A and B).</summary>
        public Color ColorA = Color.white, ColorB = Color.white;
        /// <summary>Peak alpha and colour multiplier (&gt; 1 pushes cores over the bloom threshold).</summary>
        public float Alpha = 0.6f, Hdr = 1f;
        /// <summary>Twinkle frequency (Hz, 0 = steady) and depth (0..1).</summary>
        public float Twinkle, TwinkleDepth = 0.35f;
        /// <summary>Max spin (deg/s, random sign) and tumble / pulse frequency (Hz).</summary>
        public float Spin, TumbleFreq = 0.6f;
        /// <summary>Extra rotation (deg) for Align (e.g. -90 for sprites drawn vertically).</summary>
        public float RotOffset;
        /// <summary>Fade-in and fade-out as fractions of the lifetime.</summary>
        public float FadeIn = 0.15f, FadeOut = 0.3f;
        /// <summary>Motion model.</summary>
        public AtmosMotion Motion = AtmosMotion.Drift;
        /// <summary>Optional soft halo drawn under each particle (AtmosSprite.Count = none).</summary>
        public AtmosSprite Halo = AtmosSprite.Count;
        /// <summary>Halo size (x particle size), alpha (x particle alpha) and tint.</summary>
        public float HaloScale = 2f, HaloAlpha = 0.3f;
        /// <summary>Halo tint.</summary>
        public Color HaloColor = Color.white;
    }

    /// <summary>
    /// Pooled, allocation-free particle field built from sprite renderers: a fixed number of particles that fade in, drift,
    /// fade out and respawn somewhere in the spawn area. Prewarmed so the stage opens already alive.
    /// </summary>
    public sealed class AtmosField : AtmosFeature
    {
        readonly AtmosFieldDesc _d;
        readonly Transform[] _tr, _haloTr;
        readonly SpriteRenderer[] _sr, _halo;
        readonly Vector2[] _pos, _vel;
        readonly float[] _t, _life, _size, _phase, _spin, _rot;
        readonly Color[] _col;
        AtmosRng _rng;

        /// <summary>Live speed multiplier (gusts, eruptions).</summary>
        public float SpeedMul = 1f;
        /// <summary>Horizontal direction applied to newly spawned particles (+1 / -1).</summary>
        public float DirX = 1f;

        /// <summary>Builds the pool under <paramref name="parent"/> and prewarms it.</summary>
        public AtmosField(Transform parent, AtmosFieldDesc d, uint seed)
        {
            _d = d;
            _rng = new AtmosRng(seed);
            int n = Mathf.Max(0, d.Count);
            _tr = new Transform[n];
            _sr = new SpriteRenderer[n];
            _pos = new Vector2[n];
            _vel = new Vector2[n];
            _t = new float[n];
            _life = new float[n];
            _size = new float[n];
            _phase = new float[n];
            _spin = new float[n];
            _rot = new float[n];
            _col = new Color[n];
            bool halo = d.Halo != AtmosSprite.Count;
            if (halo) { _halo = new SpriteRenderer[n]; _haloTr = new Transform[n]; }
            string name = d.Sprite.ToString();
            for (int i = 0; i < n; i++)
            {
                if (halo)
                {
                    var h = AtmosFx.NewRenderer(parent, name + "Halo", d.Halo, d.Order);
                    if (h != null) { _halo[i] = h; _haloTr[i] = h.transform; }
                }
                var sr = AtmosFx.NewRenderer(parent, name, d.Sprite, d.Order);
                if (sr == null) continue;
                _sr[i] = sr;
                _tr[i] = sr.transform;
                Spawn(i);
                // Prewarm: random age, position advanced along the path it would have taken.
                float age = _rng.Next() * _life[i];
                _t[i] = age;
                _pos[i] += _vel[i] * age + 0.5f * age * age * d.Accel;
                _vel[i] += d.Accel * age;
            }
        }

        void Spawn(int i)
        {
            var d = _d;
            _pos[i] = new Vector2(d.Spawn.xMin + _rng.Next() * d.Spawn.width, d.Spawn.yMin + _rng.Next() * d.Spawn.height);
            _vel[i] = new Vector2(_rng.Range(d.VelMin.x, d.VelMax.x) * DirX, _rng.Range(d.VelMin.y, d.VelMax.y));
            _t[i] = 0f;
            _life[i] = _rng.Range(d.LifeMin, d.LifeMax);
            _size[i] = _rng.Range(d.SizeMin, d.SizeMax);
            _phase[i] = _rng.Next() * 6.2832f;
            _spin[i] = d.Spin * (_rng.Next() * 2f - 1f);
            _rot[i] = _rng.Next() * 360f;
            _col[i] = Color.Lerp(d.ColorA, d.ColorB, _rng.Next());
        }

        /// <inheritdoc/>
        public override void Tick(float dt, float time, Vector2 cam)
        {
            var d = _d;
            Vector2 shift = cam * d.Follow;
            float twoPi = 6.2832f;
            for (int i = 0; i < _sr.Length; i++)
            {
                var sr = _sr[i];
                if (sr == null) continue;
                _t[i] += dt;
                if (_t[i] >= _life[i]) Spawn(i);
                float u = _t[i] / _life[i];
                float ph = _phase[i];

                float sp = SpeedMul;
                if (d.Motion == AtmosMotion.Pulse)
                {
                    float p = Mathf.Max(0f, Mathf.Sin(time * d.TumbleFreq * twoPi + ph));
                    sp *= 0.35f + 1.6f * p * p;
                }
                _vel[i] += d.Accel * dt;
                _pos[i] += _vel[i] * (dt * sp);

                float wob = d.WobbleAmp > 0f ? Mathf.Sin(time * d.WobbleFreq * twoPi + ph) * d.WobbleAmp : 0f;
                float x = _pos[i].x + wob + shift.x, y = _pos[i].y + shift.y;
                float size = _size[i], sx = size, sy = size * d.AspectY, rot;
                switch (d.Motion)
                {
                    case AtmosMotion.Tumble:
                    {
                        _rot[i] += _spin[i] * dt;
                        rot = _rot[i];
                        float c = Mathf.Cos(time * d.TumbleFreq * twoPi + ph);
                        sx = size * (c >= 0f ? 0.2f + 0.8f * c : -0.2f + 0.8f * c);
                        break;
                    }
                    case AtmosMotion.Align:
                        rot = Mathf.Atan2(_vel[i].y, _vel[i].x) * Mathf.Rad2Deg + d.RotOffset;
                        break;
                    case AtmosMotion.Pulse:
                    {
                        float p = Mathf.Max(0f, Mathf.Sin(time * d.TumbleFreq * twoPi + ph));
                        sx = size * (1f + 0.08f * p);
                        sy = size * d.AspectY * (1f - 0.12f * p);
                        rot = Mathf.Sin(time * 0.35f + ph) * 7f;
                        break;
                    }
                    case AtmosMotion.Shimmer:
                        sx = size * (1f + 0.07f * Mathf.Sin(time * 2.1f + ph));
                        rot = 0f;
                        break;
                    default:
                        _rot[i] += _spin[i] * dt;
                        rot = _rot[i];
                        break;
                }

                float env = (d.FadeIn > 0f ? AtmosFx.Smooth01(u / d.FadeIn) : 1f) *
                            (d.FadeOut > 0f ? AtmosFx.Smooth01((1f - u) / d.FadeOut) : 1f);
                float tw = d.Twinkle > 0f ? 1f - d.TwinkleDepth * (0.5f + 0.5f * Mathf.Sin(time * d.Twinkle * twoPi + ph * 3f)) : 1f;
                float a = d.Alpha * env * tw * Intensity;

                AtmosFx.Paint(sr, _col[i], d.Hdr, a);
                if (sr.enabled)
                {
                    _tr[i].SetPositionAndRotation(new Vector3(x, y, 0f), Quaternion.Euler(0f, 0f, rot));
                    _tr[i].localScale = new Vector3(sx, sy, 1f);
                }
                if (_halo != null && _halo[i] != null)
                {
                    AtmosFx.Paint(_halo[i], d.HaloColor, 1f, a * d.HaloAlpha);
                    if (_halo[i].enabled)
                    {
                        _haloTr[i].position = new Vector3(x, y, 0f);
                        float hs = size * d.HaloScale;
                        _haloTr[i].localScale = new Vector3(hs, hs, 1f);
                    }
                }
            }
        }
    }
}
