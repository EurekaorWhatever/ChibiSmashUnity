using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Game-feel tunables and pure curves for the fight view (presentation only; nothing here is read by the sim).
    /// Every number the impact / camera / speed-line polish uses lives here with its unit, so tuning is one file.
    /// Timing that gameplay owns is NOT invented here: hitstop length is the sim's <c>HitstopRealTicks</c> (3 normal,
    /// 5 crit, 6 parry, 8 reflect real ticks at time scale 0.05), and the view only decorates that freeze.
    /// All functions are allocation-free and deterministic for given inputs (EditMode tested: ChibiFeelTests).
    /// </summary>
    public static class ChibiFeel
    {
        /// <summary>Sim velocity sub-units per tick -> world units per second.</summary>
        public const float SubToUps = ChibiSimConst.TickRate / (1000f * ChibiSimConst.VelSub);

        // ------------------------------------------------------------------ hit strength

        /// <summary>Percent gained by one hit that counts as full strength (same scale as the hit sparks).</summary>
        public const float PctFullHit = 14f;
        /// <summary>Launch speed (u/s) where launch strength starts / reaches 1 (matches ChibiLaunchFx trail speeds).</summary>
        public const float LaunchMinUps = 7f, LaunchStrongUps = 18f;
        /// <summary>Strength at or above which a hit is "heavy": camera punch-in, directional kick, radial burst, 2-frame flash.</summary>
        public const float HeavyHit = 0.55f;

        // ------------------------------------------------------------------ tumble (sim-derived launched pose)

        /// <summary>Airborne hitstun faster than this (u/s) shows the tumble pose even after a weak-gravity-free launch.</summary>
        public const float TumbleMinUps = 7f;

        // ------------------------------------------------------------------ hitstop decoration (timing = sim)

        /// <summary>Defender vibration amplitude (world units) during the sim's hitstop, light .. full-strength hit.</summary>
        public const float HitShakeMin = 0.035f, HitShakeMax = 0.13f;
        /// <summary>Vibration frequency (Hz, unscaled time): left/right flips this many times per second.</summary>
        public const float HitShakeHz = 30f;

        // ------------------------------------------------------------------ impact flash

        /// <summary>One flash frame in unscaled seconds (60 fps frame), and frames for light / heavy hits.</summary>
        public const float FlashFrameSec = 1f / 60f;
        /// <summary>Flash frames for a light / heavy hit.</summary>
        public const int FlashFramesLight = 1, FlashFramesHeavy = 2;
        /// <summary>Silhouette flash opacity and how much of the attacker's colour tints it (0 = pure white).</summary>
        public const float FlashAlpha = 0.9f, FlashTint = 0.25f;

        // ------------------------------------------------------------------ radial burst (heavy hits)

        /// <summary>Focus lines in a heavy-hit burst.</summary>
        public const int BurstLines = 20;
        /// <summary>Inner radius as a fraction of the camera's ortho size; line length range in world units.</summary>
        public const float BurstInnerOfSize = 0.42f, BurstLenMin = 6f, BurstLenMax = 12f;
        /// <summary>Burst life in unscaled seconds (it plays during the hitstop freeze).</summary>
        public const float BurstSec = 0.17f;

        // ------------------------------------------------------------------ camera

        /// <summary>Max punch-in zoom as a fraction of the ortho size (0.12 = 12 % closer), reached at strength 1.</summary>
        public const float PunchZoomMax = 0.12f;
        /// <summary>Punch-in ease-in / ease-out times (unscaled seconds).</summary>
        public const float PunchAttackSec = 0.05f, PunchReleaseSec = 0.34f;
        /// <summary>Fraction of the way the framing centre leans toward the impact point at full punch.</summary>
        public const float PunchFocus = 0.22f;
        /// <summary>Max shake offset (world units) at trauma 1; offset = max x trauma^2 (smooth Perlin noise, never per-frame random).</summary>
        public const float ShakeMaxOffset = 0.42f;
        /// <summary>Trauma lost per unscaled second.</summary>
        public const float TraumaDecay = 1.7f;
        /// <summary>Shake noise frequency (Hz).</summary>
        public const float ShakeNoiseHz = 18f;
        /// <summary>Directional kick (camera pushed along the hit direction, then eases back): max offset and length.</summary>
        public const float KickMax = 0.32f, KickSec = 0.24f;
        /// <summary>Hard cap on the summed shake + kick offset (world units): never nauseating.</summary>
        public const float CamMaxOffset = 0.55f;
        /// <summary>Trauma added by a light .. full-strength hit.</summary>
        public const float HitTraumaMin = 0.22f, HitTraumaMax = 0.62f;

        // ------------------------------------------------------------------ final KO

        /// <summary>Presentation time scale held right after the final KO, the hold length and the total slow-mo length (unscaled s).</summary>
        public const float FinishMinScale = 0.2f, FinishHoldSec = 0.7f, FinishSec = 1.2f;
        /// <summary>Ortho size the camera zooms to on the final KO, and the zoom-in ease time.</summary>
        public const float FinishKoSize = 4.4f, FinishZoomInSec = 0.22f;
        /// <summary>Final-KO focus = KO point blended this far toward the winner (keeps both the blast and the winner in shot).</summary>
        public const float FinishFocusToWinner = 0.3f;

        // ------------------------------------------------------------------ speed lines / dust

        /// <summary>Seconds (scaled) between speed-line emissions while dashing / tumbling.</summary>
        public const float DashLineEvery = 0.035f, LaunchLineEvery = 0.03f;
        /// <summary>Speed-line life (scaled seconds) and alpha.</summary>
        public const float SpeedLineLife = 0.16f, SpeedLineAlpha = 0.55f;
        /// <summary>Fall speed (u/s) of a non-launched landing that kicks dust.</summary>
        public const float HardLandUps = 11f;
        /// <summary>Fall speed (u/s) of a launched ground bounce that adds a ground shock ring.</summary>
        public const float BounceRingUps = 9f;

        // ================================================================== pure functions

        /// <summary>0..1 hit strength from percent gained and the defender's launch speed (the larger of the two).</summary>
        public static float HitStrength(int pctGained, float launchUps)
        {
            float p = Mathf.Clamp01(pctGained / PctFullHit);
            float l = Mathf.Clamp01((launchUps - LaunchMinUps) / (LaunchStrongUps - LaunchMinUps));
            return Mathf.Max(p, l);
        }

        /// <summary>
        /// Tumble / launched pose from sim fields only: airborne hitstun while in play, with either a knockback strong enough
        /// to reduce launch gravity (<paramref name="launchGravP"/> &lt; 1000, KB &gt; 60) or a speed of at least
        /// <see cref="TumbleMinUps"/>. Stable for the whole hitstun (gravity permille persists until the launch clears).
        /// </summary>
        public static bool IsTumble(int stun, bool grounded, bool inPlay, int launchGravP, int vx, int vy)
        {
            if (stun <= 0 || grounded || !inPlay) return false;
            if (launchGravP < 1000) return true;
            float ups = Mathf.Sqrt((float)vx * vx + (float)vy * vy) * SubToUps;
            return ups >= TumbleMinUps;
        }

        /// <summary>
        /// Horizontal defender vibration during the sim hitstop: amplitude by <paramref name="strength"/>, scaled by the
        /// fraction of hitstop left (<paramref name="left01"/>, decays to 0 as the freeze ends), square-wave sign at
        /// <see cref="HitShakeHz"/> of unscaled <paramref name="time"/>.
        /// </summary>
        public static float HitShake(float strength, float left01, float time)
        {
            float amp = Mathf.Lerp(HitShakeMin, HitShakeMax, Mathf.Clamp01(strength)) * Mathf.Clamp01(left01);
            return Mathf.Repeat(time * HitShakeHz, 1f) < 0.5f ? amp : -amp;
        }

        /// <summary>Silhouette flash length in unscaled seconds: 1 frame for a light hit, 2 for a heavy one.</summary>
        public static float FlashSeconds(float strength) =>
            (strength >= HeavyHit ? FlashFramesHeavy : FlashFramesLight) * FlashFrameSec;

        /// <summary>Camera trauma a hit of <paramref name="strength"/> adds.</summary>
        public static float HitTrauma(float strength) => Mathf.Lerp(HitTraumaMin, HitTraumaMax, Mathf.Clamp01(strength));

        /// <summary>Punch-in zoom fraction for a hit: 0 below <see cref="HeavyHit"/>, then half .. full <see cref="PunchZoomMax"/>.</summary>
        public static float PunchZoom(float strength)
        {
            if (strength < HeavyHit) return 0f;
            float u = Mathf.Clamp01((strength - HeavyHit) / (1f - HeavyHit));
            return PunchZoomMax * Mathf.Lerp(0.5f, 1f, u);
        }

        /// <summary>Punch envelope 0..1 at <paramref name="t"/> seconds: smoothstep in over the attack, quadratic ease-out over the release.</summary>
        public static float PunchEnvelope(float t)
        {
            if (t <= 0f) return 0f;
            if (t < PunchAttackSec) return Smooth01(t / PunchAttackSec);
            float u = (t - PunchAttackSec) / PunchReleaseSec;
            if (u >= 1f) return 0f;
            return (1f - u) * (1f - u);
        }

        /// <summary>Directional kick weight at <paramref name="t"/>: 1 at the hit, cubic ease back to 0 over <see cref="KickSec"/>.</summary>
        public static float KickEnvelope(float t)
        {
            if (t < 0f) return 0f;
            float u = t / KickSec;
            if (u >= 1f) return 0f;
            float k = 1f - u;
            return k * k * k;
        }

        /// <summary>Trauma after <paramref name="dt"/> unscaled seconds of linear decay, never below 0.</summary>
        public static float DecayTrauma(float trauma, float dt) => Mathf.Max(0f, trauma - dt * TraumaDecay);

        /// <summary>Smooth shake offset for <paramref name="trauma"/> at unscaled <paramref name="time"/>: |offset| &lt;= ShakeMaxOffset x trauma^2.</summary>
        public static Vector2 ShakeOffset(float trauma, float time)
        {
            trauma = Mathf.Clamp01(trauma);
            if (trauma <= 0f) return Vector2.zero;
            float amp = ShakeMaxOffset * trauma * trauma;
            float x = Mathf.Clamp(Mathf.PerlinNoise(time * ShakeNoiseHz, 0.37f) * 2f - 1f, -1f, 1f);
            float y = Mathf.Clamp(Mathf.PerlinNoise(11.3f, time * ShakeNoiseHz) * 2f - 1f, -1f, 1f);
            return Vector2.ClampMagnitude(new Vector2(x, y), 1f) * amp;
        }

        /// <summary>Presentation time scale <paramref name="t"/> unscaled seconds after the final KO: hold, smooth ramp to 1.</summary>
        public static float FinishTimeScale(float t)
        {
            if (t < 0f || t >= FinishSec) return 1f;
            if (t < FinishHoldSec) return FinishMinScale;
            return Mathf.Lerp(FinishMinScale, 1f, Smooth01((t - FinishHoldSec) / (FinishSec - FinishHoldSec)));
        }

        /// <summary>Final-KO camera zoom weight 0..1: eases in over <see cref="FinishZoomInSec"/>, holds, eases out by <see cref="FinishSec"/>.</summary>
        public static float FinishZoom01(float t)
        {
            if (t <= 0f || t >= FinishSec) return 0f;
            float inW = Smooth01(t / FinishZoomInSec);
            float outW = t < FinishHoldSec ? 1f : 1f - Smooth01((t - FinishHoldSec) / (FinishSec - FinishHoldSec));
            return Mathf.Min(inW, outW);
        }

        /// <summary>Smoothstep on 0..1 (input clamped).</summary>
        public static float Smooth01(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }
    }

    /// <summary>
    /// Camera feel state (presentation only): trauma shake, a punch-in zoom toward an impact point and a directional kick.
    /// Fed by hits / KOs, advanced once per rendered frame on unscaled time, read by <see cref="ChibiSimView"/>'s camera.
    /// No Unity objects, no allocation.
    /// </summary>
    public sealed class ChibiCameraFeel
    {
        float _trauma, _punchT = float.MaxValue, _punchAmt, _kickT = float.MaxValue, _kickAmt;
        Vector2 _punchAt, _kickDir;

        /// <summary>Current trauma 0..1.</summary>
        public float Trauma => _trauma;
        /// <summary>World point the punch-in leans toward.</summary>
        public Vector2 PunchAt => _punchAt;
        /// <summary>Punch envelope 0..1 now.</summary>
        public float PunchWeight => ChibiFeel.PunchEnvelope(_punchT);
        /// <summary>Ortho-size multiplier now (1 = no punch, never below 1 - PunchZoomMax).</summary>
        public float ZoomFactor => 1f - Mathf.Min(ChibiFeel.PunchZoomMax, _punchAmt * PunchWeight);

        /// <summary>Max-merge <paramref name="amount"/> (0..1) into the trauma (legacy Match.Shake amounts map 1:1).</summary>
        public void AddTrauma(float amount) => _trauma = Mathf.Max(_trauma, Mathf.Clamp01(amount));

        /// <summary>Punch in toward <paramref name="at"/> with zoom fraction <paramref name="zoom"/>; a weaker punch never cuts a stronger live one.</summary>
        public void Punch(float zoom, Vector2 at)
        {
            if (zoom <= 0f) return;
            if (zoom < _punchAmt * PunchWeight) return;
            _punchAmt = Mathf.Min(zoom, ChibiFeel.PunchZoomMax);
            _punchAt = at;
            _punchT = 0f;
        }

        /// <summary>Push the camera along <paramref name="dir"/> by <paramref name="amount"/> (0..1) x KickMax, easing back.</summary>
        public void Kick(Vector2 dir, float amount)
        {
            if (dir.sqrMagnitude < 1e-6f || amount <= 0f) return;
            _kickDir = dir.normalized;
            _kickAmt = Mathf.Clamp01(amount);
            _kickT = 0f;
        }

        /// <summary>Advance by unscaled <paramref name="dt"/>.</summary>
        public void Tick(float dt)
        {
            _trauma = ChibiFeel.DecayTrauma(_trauma, dt);
            if (_punchT < float.MaxValue) _punchT += dt;
            if (_kickT < float.MaxValue) _kickT += dt;
        }

        /// <summary>Shake + kick offset at unscaled <paramref name="time"/>, capped at <see cref="ChibiFeel.CamMaxOffset"/>.</summary>
        public Vector2 Offset(float time)
        {
            Vector2 o = ChibiFeel.ShakeOffset(_trauma, time) + _kickDir * (ChibiFeel.KickMax * _kickAmt * ChibiFeel.KickEnvelope(_kickT));
            return Vector2.ClampMagnitude(o, ChibiFeel.CamMaxOffset);
        }

        /// <summary>Clears everything (match end).</summary>
        public void Reset()
        {
            _trauma = 0f;
            _punchT = _kickT = float.MaxValue;
            _punchAmt = _kickAmt = 0f;
        }
    }

    /// <summary>
    /// Fixed-capacity slot allocator for pooled FX (pure, no Unity objects): <see cref="Take"/> returns a free slot, or
    /// recycles the next slot in round-robin order when all are live, so a burst can never allocate.
    /// </summary>
    public sealed class ChibiFxRing
    {
        readonly bool[] _live;
        int _next;

        /// <summary>Pool of <paramref name="capacity"/> slots (at least 1).</summary>
        public ChibiFxRing(int capacity) { _live = new bool[Mathf.Max(1, capacity)]; }

        /// <summary>Number of slots.</summary>
        public int Capacity => _live.Length;
        /// <summary>Live slots now.</summary>
        public int LiveCount { get; private set; }

        /// <summary>True when slot <paramref name="i"/> is in use.</summary>
        public bool IsLive(int i) => _live[i];

        /// <summary>A slot to use; <paramref name="recycled"/> = it was still live (its old effect is cut).</summary>
        public int Take(out bool recycled)
        {
            int n = _live.Length;
            for (int k = 0; k < n; k++)
            {
                int i = (_next + k) % n;
                if (_live[i]) continue;
                _live[i] = true;
                LiveCount++;
                _next = (i + 1) % n;
                recycled = false;
                return i;
            }
            int r = _next;
            _next = (r + 1) % n;
            recycled = true;
            return r;
        }

        /// <summary>Frees slot <paramref name="i"/> (no-op when already free).</summary>
        public void Release(int i)
        {
            if (!_live[i]) return;
            _live[i] = false;
            LiveCount--;
        }

        /// <summary>Frees every slot.</summary>
        public void Clear()
        {
            for (int i = 0; i < _live.Length; i++) _live[i] = false;
            LiveCount = 0;
            _next = 0;
        }
    }
}
