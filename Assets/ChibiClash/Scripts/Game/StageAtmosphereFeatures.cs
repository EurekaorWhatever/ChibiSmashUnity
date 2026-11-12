using UnityEngine;

namespace ChibiClash
{
    /// <summary>Swaying light shafts (pivot at the top, the shaft hangs down at angle 0, positive angles lean right).</summary>
    public sealed class AtmosRays : AtmosFeature
    {
        readonly SpriteRenderer[] _sr;
        readonly Vector2[] _anchor;
        readonly float[] _angle, _len, _width, _alpha, _sway, _period, _ph;
        readonly Color _col;
        readonly float _follow;
        int _n;

        /// <summary>Room for <paramref name="capacity"/> shafts tinted <paramref name="col"/>.</summary>
        public AtmosRays(Transform parent, int capacity, int order, float follow, Color col)
        {
            _sr = new SpriteRenderer[capacity];
            _anchor = new Vector2[capacity];
            _angle = new float[capacity]; _len = new float[capacity]; _width = new float[capacity];
            _alpha = new float[capacity]; _sway = new float[capacity]; _period = new float[capacity]; _ph = new float[capacity];
            _col = col;
            _follow = follow;
            for (int i = 0; i < capacity; i++) _sr[i] = AtmosFx.NewRenderer(parent, "Ray", AtmosSprite.Ray, order);
        }

        /// <summary>Adds a shaft hanging from <paramref name="anchor"/>; <paramref name="length"/> and <paramref name="width"/> in units.</summary>
        public AtmosRays Add(Vector2 anchor, float angle, float length, float width, float alpha, float swayDeg, float period)
        {
            if (_n >= _sr.Length) return this;
            _anchor[_n] = anchor; _angle[_n] = angle; _len[_n] = length; _width[_n] = width;
            _alpha[_n] = alpha; _sway[_n] = swayDeg; _period[_n] = Mathf.Max(1f, period); _ph[_n] = _n * 1.37f;
            _n++;
            return this;
        }

        /// <inheritdoc/>
        public override void Tick(float dt, float time, Vector2 cam)
        {
            for (int i = 0; i < _n; i++)
            {
                var sr = _sr[i];
                if (sr == null) continue;
                float w = 6.2832f / _period[i];
                float rot = _angle[i] + _sway[i] * Mathf.Sin(time * w + _ph[i]);
                float breathe = 0.72f + 0.28f * Mathf.Sin(time * w * 0.61f + _ph[i] * 2.3f);
                AtmosFx.Paint(sr, _col, 1f, _alpha[i] * breathe * Intensity);
                var p = _anchor[i] + cam * _follow;
                sr.transform.SetPositionAndRotation(new Vector3(p.x, p.y, 0f), Quaternion.Euler(0f, 0f, rot));
                sr.transform.localScale = new Vector3(_width[i], _len[i] / 3f, 1f);   // the Ray sprite is 1 x 3 units
            }
        }
    }

    /// <summary>Large soft sprites that breathe: sun halos, wall light, horizon glow. <see cref="Boost"/> adds a flare.</summary>
    public sealed class AtmosGlows : AtmosFeature
    {
        readonly SpriteRenderer[] _sr;
        readonly Vector2[] _pos;
        readonly float[] _base, _amp, _period, _boostA;
        readonly Color[] _col;
        readonly float _follow;
        int _n;

        /// <summary>0..1 flare level set by the stage hook (e.g. fire columns erupting).</summary>
        public float Boost;

        /// <summary>Room for <paramref name="capacity"/> glows.</summary>
        public AtmosGlows(Transform parent, int capacity, float follow)
        {
            _sr = new SpriteRenderer[capacity];
            _pos = new Vector2[capacity];
            _base = new float[capacity]; _amp = new float[capacity]; _period = new float[capacity]; _boostA = new float[capacity];
            _col = new Color[capacity];
            _follow = follow;
            for (int i = 0; i < capacity; i++) _sr[i] = null;
            _parent = parent;
        }

        readonly Transform _parent;

        /// <summary>Adds a glow of <paramref name="sprite"/> at <paramref name="pos"/> scaled <paramref name="scale"/>.</summary>
        public AtmosGlows Add(AtmosSprite sprite, int order, Vector2 pos, Vector2 scale, Color col, float alpha, float amp,
            float period, float boostAlpha = 0f)
        {
            if (_n >= _sr.Length) return this;
            var sr = AtmosFx.NewRenderer(_parent, "Glow", sprite, order);
            if (sr == null) return this;
            sr.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            _sr[_n] = sr; _pos[_n] = pos; _col[_n] = col; _base[_n] = alpha; _amp[_n] = amp;
            _period[_n] = Mathf.Max(0.5f, period); _boostA[_n] = boostAlpha;
            _n++;
            return this;
        }

        /// <inheritdoc/>
        public override void Tick(float dt, float time, Vector2 cam)
        {
            for (int i = 0; i < _n; i++)
            {
                var sr = _sr[i];
                float a = (_base[i] + _amp[i] * Mathf.Sin(time * 6.2832f / _period[i] + i * 1.9f)) * Intensity + _boostA[i] * Boost;
                AtmosFx.Paint(sr, _col[i], 1f, a);
                var p = _pos[i] + cam * _follow;
                sr.transform.position = new Vector3(p.x, p.y, 0f);
            }
        }
    }

    /// <summary>Wide sprites drifting sideways and wrapping: mist bands, cloud banks, cloud shadows.</summary>
    public sealed class AtmosBands : AtmosFeature
    {
        readonly SpriteRenderer[] _sr;
        readonly float[] _x, _y, _ph;
        readonly float _speed, _xMin, _xMax, _follow, _alpha, _bob;
        readonly Color _col;
        readonly Transform _parent;
        readonly AtmosSprite _sprite;
        readonly int _order;
        int _n;

        /// <summary>Speed in units/s (sign = direction); wraps inside [<paramref name="xMin"/>, <paramref name="xMax"/>].</summary>
        public AtmosBands(Transform parent, int capacity, AtmosSprite sprite, int order, float follow, float speed,
            float xMin, float xMax, Color col, float alpha, float bob)
        {
            _sr = new SpriteRenderer[capacity];
            _x = new float[capacity]; _y = new float[capacity]; _ph = new float[capacity];
            _parent = parent; _sprite = sprite; _order = order; _follow = follow; _speed = speed;
            _xMin = xMin; _xMax = xMax; _col = col; _alpha = alpha; _bob = bob;
        }

        /// <summary>Adds a band centred at (<paramref name="x"/>, <paramref name="y"/>), scale (sx, sy), optional mirror.</summary>
        public AtmosBands Add(float x, float y, float sx, float sy, bool flip = false)
        {
            if (_n >= _sr.Length) return this;
            var sr = AtmosFx.NewRenderer(_parent, _sprite.ToString(), _sprite, _order);
            if (sr == null) return this;
            sr.transform.localScale = new Vector3(flip ? -sx : sx, sy, 1f);
            _sr[_n] = sr; _x[_n] = x; _y[_n] = y; _ph[_n] = _n * 2.17f;
            _n++;
            return this;
        }

        /// <inheritdoc/>
        public override void Tick(float dt, float time, Vector2 cam)
        {
            float span = _xMax - _xMin;
            for (int i = 0; i < _n; i++)
            {
                _x[i] += _speed * dt;
                if (_x[i] > _xMax) _x[i] -= span;
                else if (_x[i] < _xMin) _x[i] += span;
                // Fade near the wrap edges so a band never pops (edges sit outside the art anyway).
                float edge = Mathf.Min(_x[i] - _xMin, _xMax - _x[i]);
                float a = _alpha * AtmosFx.Smooth01(edge / 4f) * (0.85f + 0.15f * Mathf.Sin(time * 0.23f + _ph[i])) * Intensity;
                AtmosFx.Paint(_sr[i], _col, 1f, a);
                float y = _y[i] + _bob * Mathf.Sin(time * 0.17f + _ph[i]);
                _sr[i].transform.position = new Vector3(_x[i] + cam.x * _follow, y + cam.y * _follow, 0f);
            }
        }
    }

    /// <summary>A school of fish or a flock of birds crossing the layer now and then, alternating direction.</summary>
    public sealed class AtmosSchool : AtmosFeature
    {
        readonly SpriteRenderer[] _sr;
        readonly Vector2[] _off;
        readonly float[] _ph;
        readonly float _follow, _yMin, _yMax, _xRange, _speed, _size, _alpha, _gapMin, _gapMax;
        readonly bool _flap;
        readonly Color _col;
        AtmosRng _rng;
        float _x, _y, _dir = -1f, _wait;
        bool _active;

        /// <summary>
        /// <paramref name="count"/> members of <paramref name="sprite"/> (Fish faces +X; Bird is symmetric and flaps when
        /// <paramref name="flap"/>). Passes cross X -xRange..xRange at <paramref name="speed"/> units/s, then wait
        /// <paramref name="gapMin"/>..<paramref name="gapMax"/> s.
        /// </summary>
        public AtmosSchool(Transform parent, AtmosSprite sprite, int count, int order, float follow, float yMin, float yMax,
            float xRange, float speed, float size, Color col, float alpha, bool flap, float gapMin, float gapMax, uint seed)
        {
            _rng = new AtmosRng(seed);
            _sr = new SpriteRenderer[count];
            _off = new Vector2[count];
            _ph = new float[count];
            _follow = follow; _yMin = yMin; _yMax = yMax; _xRange = xRange; _speed = speed; _size = size;
            _col = col; _alpha = alpha; _flap = flap; _gapMin = gapMin; _gapMax = gapMax;
            for (int i = 0; i < count; i++)
            {
                _sr[i] = AtmosFx.NewRenderer(parent, sprite.ToString(), sprite, order);
                if (flap)   // loose V behind the leader
                    _off[i] = new Vector2(-0.75f * ((i + 1) / 2) + _rng.Range(-0.15f, 0.15f), (i % 2 == 0 ? 0.32f : -0.32f) * ((i + 1) / 2));
                else        // a loose shoal
                    _off[i] = new Vector2(-_rng.Range(0f, 2.4f), _rng.Range(-0.7f, 0.7f));
                _ph[i] = _rng.Next() * 6.2832f;
            }
            // Start mid-pass so the stage opens with life in it.
            _active = true;
            _dir = 1f;
            _x = _rng.Range(-xRange * 0.5f, xRange * 0.3f);
            _y = _rng.Range(yMin, yMax);
        }

        /// <inheritdoc/>
        public override void Tick(float dt, float time, Vector2 cam)
        {
            if (!_active)
            {
                _wait -= dt;
                if (_wait > 0f) { for (int i = 0; i < _sr.Length; i++) if (_sr[i] != null && _sr[i].enabled) _sr[i].enabled = false; return; }
                _active = true;
                _dir = -_dir;
                _x = -_dir * _xRange;
                _y = _rng.Range(_yMin, _yMax);
            }
            _x += _dir * _speed * dt;
            if (_x * _dir > _xRange + 3f) { _active = false; _wait = _rng.Range(_gapMin, _gapMax); return; }

            float bob = Mathf.Sin(time * 0.45f) * 0.45f;
            for (int i = 0; i < _sr.Length; i++)
            {
                var sr = _sr[i];
                if (sr == null) continue;
                float ph = _ph[i];
                float px = _x + _off[i].x * _dir + Mathf.Sin(time * 0.7f + ph) * 0.25f;
                float py = _y + _off[i].y + bob + Mathf.Sin(time * 0.9f + ph * 1.7f) * 0.18f;
                float sx, sy, rot;
                if (_flap)
                {
                    sx = _size;
                    sy = _size * (0.35f + 0.65f * Mathf.Abs(Mathf.Sin(time * 4.2f + ph)));
                    rot = Mathf.Sin(time * 0.8f + ph) * 4f;
                }
                else
                {
                    sx = _size * _dir;
                    sy = _size * (1f + 0.05f * Mathf.Sin(time * 9f + ph));
                    rot = Mathf.Sin(time * 5.5f + ph) * 5f * _dir;
                }
                AtmosFx.Paint(sr, _col, 1f, _alpha * Intensity);
                sr.transform.SetPositionAndRotation(new Vector3(px + cam.x * _follow, py + cam.y * _follow, 0f), Quaternion.Euler(0f, 0f, rot));
                sr.transform.localScale = new Vector3(sx, sy, 1f);
            }
        }
    }

    /// <summary>
    /// Glowing runes on platforms: a breathing glow, a sparkle that flashes now and then and a mote rising off each glyph.
    /// Everything is parented to the platform transform, so it rides moving platforms exactly; the glow brightens with
    /// platform speed ("rune glow pulses with motion", design/gdd/stages/01-mountain.md).
    /// </summary>
    public sealed class AtmosRunes : AtmosFeature
    {
        const int Max = 16;
        readonly SpriteRenderer[] _glow = new SpriteRenderer[Max], _star = new SpriteRenderer[Max], _mote = new SpriteRenderer[Max];
        readonly Vector2[] _off = new Vector2[Max];
        readonly int[] _plat = new int[Max];
        readonly float[] _flashAt = new float[Max];
        readonly Transform[] _plats = new Transform[4];
        readonly Vector3[] _last = new Vector3[4];
        readonly float[] _boost = new float[4];
        readonly Color _col;
        AtmosRng _rng = new AtmosRng(77);
        int _n, _np;

        /// <summary>Rune colour (Mountain: #7DFFD0).</summary>
        public AtmosRunes(Color col) { _col = col; }

        /// <summary>Adds glyphs at local <paramref name="offsets"/> of platform <paramref name="plat"/> (null is ignored).</summary>
        public AtmosRunes AddPlatform(Transform plat, Vector2[] offsets)
        {
            if (plat == null || _np >= _plats.Length) return this;
            int pi = _np++;
            _plats[pi] = plat;
            _last[pi] = plat.position;
            for (int k = 0; k < offsets.Length && _n < Max; k++)
            {
                _glow[_n] = AtmosFx.NewRenderer(plat, "RuneGlow", AtmosSprite.Glow, AtmosFx.OrderOnPlatform);
                _star[_n] = AtmosFx.NewRenderer(plat, "RuneGlint", AtmosSprite.Glint, AtmosFx.OrderOnPlatform);
                _mote[_n] = AtmosFx.NewRenderer(plat, "RuneMote", AtmosSprite.Dot, AtmosFx.OrderOnPlatform);
                if (_glow[_n] == null) return this;
                _glow[_n].transform.localPosition = offsets[k];
                _glow[_n].transform.localScale = new Vector3(0.6f, 0.6f, 1f);
                _star[_n].transform.localPosition = offsets[k];
                _off[_n] = offsets[k];
                _plat[_n] = pi;
                _flashAt[_n] = _rng.Range(0.5f, 4f);
                _n++;
            }
            return this;
        }

        /// <inheritdoc/>
        public override void Tick(float dt, float time, Vector2 cam)
        {
            for (int p = 0; p < _np; p++)
            {
                if (_plats[p] == null) continue;
                var pos = _plats[p].position;
                float speed = dt > 0f ? (pos - _last[p]).magnitude / dt : 0f;
                _last[p] = pos;
                _boost[p] = Mathf.MoveTowards(_boost[p], Mathf.Clamp01(speed / 2.5f), dt * 2f);
            }
            for (int i = 0; i < _n; i++)
            {
                if (_glow[i] == null) continue;
                float boost = _boost[_plat[i]];
                float breathe = 0.5f + 0.5f * Mathf.Sin(time * 3.9f - i * 0.9f);
                AtmosFx.Paint(_glow[i], _col, 1f, (0.16f + 0.16f * breathe) * (1f + 0.9f * boost) * Intensity);

                // Sparkle: a 0.55 s flash every few seconds per glyph.
                float ft = time - _flashAt[i];
                if (ft > 0.55f) { _flashAt[i] = time + _rng.Range(2.2f, 6f) * (1f - 0.5f * boost); ft = -1f; }
                float fa = ft >= 0f ? Mathf.Sin(ft / 0.55f * Mathf.PI) : 0f;
                AtmosFx.Paint(_star[i], _col, 1.15f, 0.85f * fa * Intensity);
                if (_star[i].enabled)
                {
                    float s = 0.45f + 0.35f * fa;
                    _star[i].transform.localScale = new Vector3(s, s, 1f);
                    _star[i].transform.localRotation = Quaternion.Euler(0f, 0f, ft * 40f);
                }

                // A mote lifting off the glyph (1.8 s cycle).
                float u = Mathf.Repeat(time / 1.8f + i * 0.37f, 1f);
                AtmosFx.Paint(_mote[i], _col, 1f, 0.55f * Mathf.Sin(u * Mathf.PI) * Intensity);
                if (_mote[i].enabled)
                {
                    _mote[i].transform.localPosition = new Vector3(_off[i].x + Mathf.Sin(time * 2f + i) * 0.12f, _off[i].y + 0.1f + 0.75f * u, 0f);
                    float ms = 0.14f * (1f - 0.4f * u);
                    _mote[i].transform.localScale = new Vector3(ms, ms, 1f);
                }
            }
        }
    }

    /// <summary>
    /// Animated caustic light on platforms: two seamless tiled layers scrolling against each other, clipped to each
    /// platform sprite by a SpriteMask whose range covers only <see cref="AtmosFx.OrderCaustic"/>.
    /// </summary>
    public sealed class AtmosCaustics : AtmosFeature
    {
        const int Max = 8;
        readonly SpriteRenderer[] _a = new SpriteRenderer[Max], _b = new SpriteRenderer[Max];
        readonly Vector2[] _centre = new Vector2[Max];
        readonly Color _col;
        readonly float _alpha;
        int _n;
        const float ScaleB = 1.4f;

        /// <summary>Caustic tint and peak alpha.</summary>
        public AtmosCaustics(Color col, float alpha) { _col = col; _alpha = alpha; }

        /// <summary>Adds caustics over platform <paramref name="plat"/> (needs a SpriteRenderer; null is ignored).</summary>
        public AtmosCaustics AddPlatform(Transform plat)
        {
            if (plat == null || _n >= Max) return this;
            var psr = plat.GetComponent<SpriteRenderer>();
            var tile = AtmosFx.Caustic;
            if (psr == null || psr.sprite == null || tile == null) return this;

            var maskGo = new GameObject("CausticMask");
            maskGo.transform.SetParent(plat, false);
            var mask = maskGo.AddComponent<SpriteMask>();
            mask.sprite = psr.sprite;
            mask.alphaCutoff = 0.35f;
            mask.isCustomRangeActive = true;              // clip only the caustic layers, nothing else on the stage
            mask.frontSortingLayerID = psr.sortingLayerID;
            mask.backSortingLayerID = psr.sortingLayerID;
            mask.frontSortingOrder = AtmosFx.OrderCaustic;
            mask.backSortingOrder = AtmosFx.OrderCaustic - 1;

            var b = psr.sprite.bounds;      // local space of the platform
            _centre[_n] = b.center;
            _a[_n] = Layer(plat, tile, b.size, 1f);
            _b[_n] = Layer(plat, tile, b.size, ScaleB);
            _n++;
            return this;
        }

        static SpriteRenderer Layer(Transform plat, Sprite tile, Vector3 size, float scale)
        {
            var sr = AtmosFx.NewRenderer(plat, "Caustic", tile, AtmosFx.OrderCaustic);
            sr.drawMode = SpriteDrawMode.Tiled;
            sr.tileMode = SpriteTileMode.Continuous;
            float pad = AtmosFx.CausticTile * 2f;
            sr.size = new Vector2(size.x / scale + pad, size.y / scale + pad);
            sr.transform.localScale = new Vector3(scale, scale, 1f);
            sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
            return sr;
        }

        /// <inheritdoc/>
        public override void Tick(float dt, float time, Vector2 cam)
        {
            float tA = AtmosFx.CausticTile, tB = AtmosFx.CausticTile * ScaleB;
            // Scroll by less than one tile and wrap: the tiling makes every wrap invisible.
            var offA = new Vector2(Mathf.Repeat(time * 0.32f, tA) - tA * 0.5f, Mathf.Repeat(time * 0.11f, tA) - tA * 0.5f);
            var offB = new Vector2(Mathf.Repeat(-time * 0.21f, tB) - tB * 0.5f, Mathf.Repeat(time * 0.07f, tB) - tB * 0.5f);
            float pa = 0.75f + 0.25f * Mathf.Sin(time * 0.9f), pb = 0.75f + 0.25f * Mathf.Sin(time * 0.9f + 2.4f);
            for (int i = 0; i < _n; i++)
            {
                if (_a[i] == null) continue;
                AtmosFx.Paint(_a[i], _col, 1f, _alpha * pa * Intensity);
                AtmosFx.Paint(_b[i], _col, 1f, _alpha * 0.8f * pb * Intensity);
                _a[i].transform.localPosition = new Vector3(_centre[i].x + offA.x, _centre[i].y + offA.y, 0f);
                _b[i].transform.localPosition = new Vector3(_centre[i].x + offB.x, _centre[i].y + offB.y, 0f);
            }
        }
    }

    /// <summary>Lava bubbles that swell on the magma surface and pop with a glow flash, a ring and two droplets.</summary>
    public sealed class AtmosLavaBubbles : AtmosFeature
    {
        readonly SpriteRenderer[] _dome, _ring, _glow, _drop;
        readonly float[] _x, _size, _t, _grow, _wait;
        readonly Vector2[] _dropV, _dropP;
        readonly float _surface, _xMin, _xMax;
        readonly Color _lava, _hot;
        AtmosRng _rng = new AtmosRng(4242);
        const float PopTime = 0.45f;

        /// <summary><paramref name="count"/> bubbles along the surface Y <paramref name="surface"/> between xMin..xMax.</summary>
        public AtmosLavaBubbles(Transform parent, int count, float surface, float xMin, float xMax, Color lava, Color hot)
        {
            _dome = new SpriteRenderer[count]; _ring = new SpriteRenderer[count]; _glow = new SpriteRenderer[count];
            _drop = new SpriteRenderer[count * 2];
            _x = new float[count]; _size = new float[count]; _t = new float[count]; _grow = new float[count]; _wait = new float[count];
            _dropV = new Vector2[count * 2]; _dropP = new Vector2[count * 2];
            _surface = surface; _xMin = xMin; _xMax = xMax; _lava = lava; _hot = hot;
            for (int i = 0; i < count; i++)
            {
                _glow[i] = AtmosFx.NewRenderer(parent, "LavaPopGlow", AtmosSprite.Glow, AtmosFx.OrderOverLava - 1);
                _dome[i] = AtmosFx.NewRenderer(parent, "LavaBubble", AtmosSprite.LavaDome, AtmosFx.OrderOverLava);
                _ring[i] = AtmosFx.NewRenderer(parent, "LavaPopRing", AtmosSprite.Ring, AtmosFx.OrderOverLava);
                _drop[i * 2] = AtmosFx.NewRenderer(parent, "LavaDrop", AtmosSprite.Ember, AtmosFx.OrderOverLava);
                _drop[i * 2 + 1] = AtmosFx.NewRenderer(parent, "LavaDrop", AtmosSprite.Ember, AtmosFx.OrderOverLava);
                _wait[i] = _rng.Range(0f, 3f);
                _t[i] = -1f;
            }
        }

        /// <inheritdoc/>
        public override void Tick(float dt, float time, Vector2 cam)
        {
            for (int i = 0; i < _dome.Length; i++)
            {
                if (_dome[i] == null) continue;
                if (_t[i] < 0f)
                {
                    _wait[i] -= dt;
                    Hide(i);
                    if (_wait[i] > 0f) continue;
                    _t[i] = 0f;
                    _x[i] = _rng.Range(_xMin, _xMax);
                    _size[i] = _rng.Range(0.35f, 0.75f);
                    _grow[i] = _rng.Range(0.7f, 1.3f);
                }
                _t[i] += dt;
                float t = _t[i], s = _size[i];
                if (t < _grow[i])
                {
                    // Swelling dome, wobbling slightly.
                    float k = AtmosFx.Smooth01(t / _grow[i]);
                    float ds = s * (0.25f + 0.75f * k);
                    AtmosFx.Paint(_dome[i], _lava, 1f, 0.95f * Mathf.Min(1f, t * 6f));
                    _dome[i].transform.position = new Vector3(_x[i], _surface + ds * 0.12f, 0f);
                    _dome[i].transform.localScale = new Vector3(ds * (1f + 0.05f * Mathf.Sin(t * 19f)), ds * 0.72f, 1f);
                    AtmosFx.Paint(_glow[i], _hot, 1f, 0.12f * k);
                    _glow[i].transform.position = new Vector3(_x[i], _surface, 0f);
                    _glow[i].transform.localScale = new Vector3(s * 1.2f, s * 0.8f, 1f);
                    if (_ring[i].enabled) _ring[i].enabled = false;
                    if (t + dt >= _grow[i])   // about to pop: launch the droplets
                        for (int d = 0; d < 2; d++)
                        {
                            int j = i * 2 + d;
                            _dropP[j] = new Vector2(_x[i], _surface + s * 0.2f);
                            _dropV[j] = new Vector2((d == 0 ? -1f : 1f) * _rng.Range(0.4f, 1.1f), _rng.Range(2.2f, 3.4f));
                        }
                    continue;
                }
                float pu = (t - _grow[i]) / PopTime;
                if (pu >= 1f) { _t[i] = -1f; _wait[i] = _rng.Range(0.6f, 2.6f); Hide(i); continue; }
                if (_dome[i].enabled) _dome[i].enabled = false;
                // Pop: an expanding ring and a short hot flash (blooms: the colour's red channel is at 1).
                AtmosFx.Paint(_ring[i], _hot, 1.2f, 0.8f * (1f - pu));
                float rs = s * (0.4f + 1.3f * pu);
                _ring[i].transform.position = new Vector3(_x[i], _surface + s * 0.1f, 0f);
                _ring[i].transform.localScale = new Vector3(rs, rs * 0.55f, 1f);
                AtmosFx.Paint(_glow[i], _hot, 1.1f, 0.42f * (1f - pu) * (1f - pu));
                _glow[i].transform.localScale = new Vector3(s * 2.6f, s * 1.6f, 1f);
                for (int d = 0; d < 2; d++)
                {
                    int j = i * 2 + d;
                    _dropV[j].y -= 9f * dt;
                    _dropP[j] += _dropV[j] * dt;
                    AtmosFx.Paint(_drop[j], _hot, 1.3f, _dropP[j].y > _surface ? 1f - pu : 0f);
                    _drop[j].transform.position = new Vector3(_dropP[j].x, _dropP[j].y, 0f);
                    _drop[j].transform.localScale = new Vector3(s * 0.55f, s * 0.55f, 1f);
                }
            }
        }

        void Hide(int i)
        {
            if (_dome[i].enabled) _dome[i].enabled = false;
            if (_ring[i] != null && _ring[i].enabled) _ring[i].enabled = false;
            if (_glow[i] != null && _glow[i].enabled) _glow[i].enabled = false;
            for (int d = 0; d < 2; d++)
                if (_drop[i * 2 + d] != null && _drop[i * 2 + d].enabled) _drop[i * 2 + d].enabled = false;
        }
    }
}
