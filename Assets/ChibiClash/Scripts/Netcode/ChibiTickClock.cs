using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Wall-clock to simulation-tick converter (a fixed-step time accumulator). Extracted from ChibiP2PPeerLoop so
    /// the online and offline hosts share one implementation and it can be unit tested without Unity.
    ///
    /// This is the ONLY netcode type that uses float, because it measures real (render) time, not sim state.
    /// It never feeds a float into the simulation; it only decides how many integer ticks to run.
    ///
    /// Not thread-safe: call it from one thread (the Unity main thread).
    ///
    /// Usage (dilation that may change between ticks, e.g. rollback time sync):
    /// <code>
    /// var clock = new ChibiTickClock();
    /// Func&lt;float&gt; dil = () =&gt; session.TimeDilation;   // cache delegates once, not per frame
    /// Action tick = () =&gt; session.Tick();
    /// void Update() { clock.Run(Time.unscaledDeltaTime, dil, tick); }
    /// </code>
    /// Usage (constant dilation, e.g. offline play):
    /// <code>
    /// int n = clock.Advance(Time.unscaledDeltaTime, 1f);
    /// for (int i = 0; i &lt; n; i++) session.Tick();
    /// </code>
    /// </summary>
    public sealed class ChibiTickClock
    {
        /// <summary>Nominal length of one simulation tick in seconds (1 / ChibiSimConst.TickRate).</summary>
        public const float TickSeconds = 1f / ChibiSimConst.TickRate;

        /// <summary>
        /// Most ticks run in one render frame. When reached, the remaining backlog is dropped instead of
        /// spiralling (the game slows down during a hitch rather than freezing).
        /// </summary>
        public const int MaxTicksPerRenderFrame = 5;

        float _acc;

        /// <summary>Fraction of the next nominal tick that has already elapsed, for render interpolation.</summary>
        /// <example><code>float a = clock.RenderAlpha; pos = Lerp(prev, cur, a);</code></example>
        public float RenderAlpha => _acc / TickSeconds;

        /// <summary>Clear the accumulated time (call when a match starts).</summary>
        /// <example><code>clock.Reset();</code></example>
        public void Reset() { _acc = 0f; }

        /// <summary>
        /// Add <paramref name="deltaSeconds"/> of wall time and run as many ticks as fit, re-reading
        /// <paramref name="dilation"/> after every tick (a tick may change it). Each tick lasts
        /// TickSeconds * dilation. Returns the number of ticks run (0..MaxTicksPerRenderFrame).
        /// </summary>
        /// <example><code>int ran = clock.Run(Time.unscaledDeltaTime, dil, tick);</code></example>
        public int Run(float deltaSeconds, Func<float> dilation, Action tick)
        {
            if (dilation == null) throw new ArgumentNullException(nameof(dilation));
            if (tick == null) throw new ArgumentNullException(nameof(tick));

            _acc += deltaSeconds;
            float step = TickSeconds * dilation();
            int ran = 0;
            while (_acc >= step && ran < MaxTicksPerRenderFrame)
            {
                tick();
                _acc -= step;
                ran++;
                step = TickSeconds * dilation();
            }
            if (ran == MaxTicksPerRenderFrame) _acc = 0f;   // hitch: drop the backlog instead of spiralling
            return ran;
        }

        /// <summary>
        /// Frame-pacing helper (performance audit 2026-11-11, docs/performance/2026-11-11-smoothness-audit.md F2):
        /// with vSync on, frames are shown at exact multiples of the display refresh period, but the measured delta
        /// jitters by a few hundred microseconds around it. Fed raw into the accumulator, that jitter flips frames
        /// between 0 and 2 ticks at 60 Hz and makes the interpolation alpha wobble at 120/144 Hz. This returns the
        /// nearest whole number of refresh periods when <paramref name="deltaSeconds"/> is within
        /// <paramref name="toleranceSeconds"/> of it (1..4 periods), otherwise the delta unchanged (vSync off, real
        /// hitches). Pure function: the caller supplies the refresh period, so this stays Unity-free.
        /// Offline only: snapping trades a tiny wall-clock drift for even pacing, which online time sync must not do.
        /// </summary>
        /// <example><code>float dt = ChibiTickClock.SnapToRefresh(0.01702f, 1f / 60f);   // 0.016667 (one 60 Hz frame)</code></example>
        public static float SnapToRefresh(float deltaSeconds, float refreshSeconds, float toleranceSeconds = 0.0005f)
        {
            if (refreshSeconds <= 0f || deltaSeconds <= 0f) return deltaSeconds;
            float periods = (float)Math.Round(deltaSeconds / refreshSeconds);
            if (periods < 1f || periods > 4f) return deltaSeconds;
            float snapped = periods * refreshSeconds;
            return Math.Abs(deltaSeconds - snapped) <= toleranceSeconds ? snapped : deltaSeconds;
        }

        /// <summary>
        /// Constant-dilation variant: add <paramref name="deltaSeconds"/> and return how many ticks the caller
        /// should run now. Same arithmetic as <see cref="Run"/> when dilation does not change between ticks.
        /// </summary>
        /// <example><code>int n = clock.Advance(1f / 60f, 1f);   // n == 1</code></example>
        public int Advance(float deltaSeconds, float dilation)
        {
            _acc += deltaSeconds;
            float step = TickSeconds * dilation;
            int ran = 0;
            while (_acc >= step && ran < MaxTicksPerRenderFrame)
            {
                _acc -= step;
                ran++;
            }
            if (ran == MaxTicksPerRenderFrame) _acc = 0f;
            return ran;
        }
    }
}
