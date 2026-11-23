using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Cinematic post-processing for matches (URP 2D renderer): bloom so blade trails, sparks, lava and glows bloom like a
    /// modern action game; per-stage colour grading from the map sheet palettes; a soft vignette; and short "impact
    /// punches" (bloom spike + chromatic aberration) on KOs and ultimates. Presentation only: it READS the displayed sim
    /// state to detect KOs / ult cinematics and never writes it (netcode-core rule).
    ///
    /// Built at runtime (no scene / asset edits): a global <see cref="Volume"/> with its own <see cref="VolumeProfile"/>, and
    /// the main camera's post-processing switched on for the match and restored on destroy.
    /// </summary>
    public sealed class ChibiPostFx : MonoBehaviour
    {
        /// <summary>Player setting (PlayerPrefs "chibi_postfx", default on). Off = no volume, camera untouched.</summary>
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(PrefKey, 1) != 0;
            set { PlayerPrefs.SetInt(PrefKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }
        const string PrefKey = "chibi_postfx";

        /// <summary>Per-stage grade: bloom tint, vignette colour, saturation, contrast, exposure, bloom intensity.</summary>
        struct Grade
        {
            public Color Bloom, Vignette;
            public float Saturation, Contrast, Exposure, BloomIntensity;
        }

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        // Stage index (ChibiStageTable) -> grade. 0..2 classic arenas (neutral-warm), 3..7 the map sheet stages, graded from
        // each page's palette (rune glow / sunlit yellow / magma core / sunset gold / glowing cyan as the bloom tint).
        static readonly Grade[] Grades =
        {
            new Grade { Bloom = Color.white, Vignette = Hex(0x101828), Saturation = 10f, Contrast = 8f, Exposure = 0f, BloomIntensity = 0.8f },
            new Grade { Bloom = Hex(0xC9A8FF), Vignette = Hex(0x0A0618), Saturation = 14f, Contrast = 12f, Exposure = 0f, BloomIntensity = 1.1f },
            new Grade { Bloom = Hex(0xFFB070), Vignette = Hex(0x1A0604), Saturation = 12f, Contrast = 12f, Exposure = 0f, BloomIntensity = 1.0f },
            new Grade { Bloom = Hex(0x7DFFD0), Vignette = Hex(0x0E2A22), Saturation = 12f, Contrast = 10f, Exposure = 0.05f, BloomIntensity = 0.85f },  // Mountain
            new Grade { Bloom = Hex(0xFFF0A3), Vignette = Hex(0x123018), Saturation = 14f, Contrast = 8f, Exposure = 0.08f, BloomIntensity = 0.8f },   // Plain
            new Grade { Bloom = Hex(0xFF9A1F), Vignette = Hex(0x1B1022), Saturation = 16f, Contrast = 16f, Exposure = 0f, BloomIntensity = 1.35f },   // Volcano
            new Grade { Bloom = Hex(0xFFC069), Vignette = Hex(0x2A1408), Saturation = 14f, Contrast = 12f, Exposure = 0.04f, BloomIntensity = 1.0f },  // Bird Lair
            new Grade { Bloom = Hex(0x7CF8FF), Vignette = Hex(0x031B33), Saturation = 12f, Contrast = 12f, Exposure = 0f, BloomIntensity = 1.2f },    // Ocean
        };

        // Bloom only on genuinely bright pixels (white-hot trail cores, sparks, lava, glows), never on the whole painted stage.
        const float BloomThreshold = 0.82f, BloomScatter = 0.68f, VignetteIntensity = 0.24f, VignetteSmooth = 0.45f;
        const float PunchKoSec = 0.45f, PunchUltSec = 0.6f, PunchBloomAdd = 1.6f, PunchAberration = 0.55f;
        /// <summary>Resting chromatic aberration: 0, so URP skips the full-screen aberration pass for the whole fight.</summary>
        const float AberrationRest = 0f;
        /// <summary>
        /// Warm-up: for the first <see cref="WarmFrames"/> rendered frames after the match is built (behind the READY intro)
        /// the aberration runs at an invisible <see cref="AberrationWarm"/>, so URP compiles / loads the aberration variant
        /// of the uber post shader then, not on the first KO punch mid-fight (Editor placeholder flash / first-use hitch;
        /// performance audit 2026-11-12 section 6). Then it rests at 0 until a punch.
        /// </summary>
        const float AberrationWarm = 0.002f;
        const int WarmFrames = 3;
        /// <summary>Heavy hit (percent gained >= ChibiFeel.PctFullHit x HeavyHit): a small bloom / vignette punch.</summary>
        const float PunchHeavySec = 0.22f, PunchHeavyAmt = 0.3f;

        Volume _volume;
        VolumeProfile _profile;
        Bloom _bloom;
        ChromaticAberration _aberration;
        Vignette _vignette;
        float _baseBloom;
        UniversalAdditionalCameraData _camData;
        bool _camWasPost;
        Func<ChibiSimState> _state;
        readonly int[] _stocks = { -1, -1, -1, -1 };
        readonly int[] _pct = { -1, -1, -1, -1 };
        int _cinematic = -1;
        float _punchT, _punchLen, _punchAmt;
        int _warmLeft = WarmFrames;
        float _lastK = -1f;   // last applied punch level: volume parameters are only written when it changes

        /// <summary>
        /// Add match post-processing for <paramref name="stage"/> under <paramref name="host"/> (destroyed with it), reading
        /// <paramref name="state"/> for KO / ult punches. Returns null when the player turned post FX off.
        /// </summary>
        /// <example><code>ChibiPostFx.Attach(transform, cfg.Stage, () => _loop.State);</code></example>
        public static ChibiPostFx Attach(Transform host, int stage, Func<ChibiSimState> state)
        {
            if (!Enabled) return null;
            var go = new GameObject("PostFx");
            go.transform.SetParent(host, false);
            var fx = go.AddComponent<ChibiPostFx>();
            fx.Build(Grades[Mathf.Clamp(stage, 0, Grades.Length - 1)]);
            fx._state = state;
            return fx;
        }

        void Build(Grade g)
        {
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "ChibiMatchPostFx";

            _bloom = _profile.Add<Bloom>(true);
            _bloom.threshold.Override(BloomThreshold);
            _bloom.intensity.Override(g.BloomIntensity);
            _bloom.scatter.Override(BloomScatter);
            _bloom.tint.Override(g.Bloom);
            _bloom.highQualityFiltering.Override(true);
            _baseBloom = g.BloomIntensity;

            var tone = _profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.Neutral);   // keeps the cel palette; ACES would shift the sheet colours

            var color = _profile.Add<ColorAdjustments>(true);
            color.saturation.Override(g.Saturation);
            color.contrast.Override(g.Contrast);
            color.postExposure.Override(g.Exposure);

            _vignette = _profile.Add<Vignette>(true);
            _vignette.color.Override(g.Vignette);
            _vignette.intensity.Override(VignetteIntensity);
            _vignette.smoothness.Override(VignetteSmooth);
            _vignette.rounded.Override(false);

            _aberration = _profile.Add<ChromaticAberration>(true);
            _aberration.intensity.Override(AberrationWarm);   // warm-up pulse; LateUpdate drops it to AberrationRest

            _volume = gameObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 10f;
            _volume.sharedProfile = _profile;

            var cam = Camera.main;
            if (cam != null)
            {
                _camData = cam.GetUniversalAdditionalCameraData();
                if (_camData != null)
                {
                    _camWasPost = _camData.renderPostProcessing;
                    _camData.renderPostProcessing = true;
                }
            }
        }

        /// <summary>Trigger an impact punch now (strength 0..1). Used for KOs and ult starts; callable by other view code.</summary>
        public void Punch(float strength, float seconds)
        {
            if (strength * seconds < _punchAmt * Mathf.Max(0f, _punchLen - _punchT)) return;   // keep the stronger one
            _punchAmt = Mathf.Clamp01(strength);
            _punchLen = seconds;
            _punchT = 0f;
        }

        void LateUpdate()
        {
            var s = _state?.Invoke();
            if (s != null) Detect(s);

            // Ease-out punch on unscaled time (hitstop / cinematic slow-mo must not stretch it).
            float k = 0f;
            if (_punchLen > 0f)
            {
                _punchT += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(_punchT / _punchLen);
                k = _punchAmt * (1f - t) * (1f - t);
                if (t >= 1f) _punchLen = 0f;
            }
            bool warming = _warmLeft > 0;
            if (warming) _warmLeft--;
            if (!warming && k == _lastK) return;   // resting: no per-frame volume writes
            _lastK = warming ? -1f : k;
            if (_bloom != null) _bloom.intensity.value = _baseBloom + PunchBloomAdd * k;
            if (_aberration != null)
                _aberration.intensity.value = Mathf.Max(warming ? AberrationWarm : AberrationRest, AberrationRest + PunchAberration * k);
            if (_vignette != null) _vignette.intensity.value = VignetteIntensity + 0.12f * k;
        }

        /// <summary>
        /// KO = a fighter's stock count dropped; ult = a new cinematic started; heavy hit = percent rose by a heavy amount.
        /// Rollback-safe: only edges are punched.
        /// </summary>
        void Detect(ChibiSimState s)
        {
            const int heavyPctM = (int)(ChibiFeel.PctFullHit * ChibiFeel.HeavyHit * 1000f);
            int nf = ChibiSimEvents.Fighters(s);
            for (int i = 0; i < nf; i++)
            {
                int st = s.F[i].Stocks;
                if (_stocks[i] >= 0 && st < _stocks[i]) Punch(1f, PunchKoSec);
                _stocks[i] = st;
                int pct = s.F[i].PctM;
                if (_pct[i] >= 0 && pct - _pct[i] >= heavyPctM) Punch(PunchHeavyAmt, PunchHeavySec);
                _pct[i] = pct;
            }
            int cin = s.CinematicRealTicks > 0 ? s.CinematicFighter : -1;
            if (cin >= 0 && _cinematic < 0) Punch(0.7f, PunchUltSec);
            _cinematic = cin;
        }

        void OnDestroy()
        {
            if (_camData != null) _camData.renderPostProcessing = _camWasPost;
            if (_profile != null) Destroy(_profile);
        }
    }
}
