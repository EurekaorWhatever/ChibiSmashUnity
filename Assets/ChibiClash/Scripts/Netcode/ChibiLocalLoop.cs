using System;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// What a presenter (ChibiSimView, HUD) needs from a running match, offline or online: the state to draw, the
    /// interpolation fraction, and a callback after every simulated tick. Online, State may be a PREDICTED frame and a
    /// later tick may show a corrected (rolled-back) state; presenters diff consecutive states, so they follow it.
    /// </summary>
    public interface IChibiSimFeed
    {
        /// <summary>The state to draw (do not mutate). Null before the match begins.</summary>
        ChibiSimState State { get; }
        /// <summary>Fraction of the next tick already elapsed (0..~1.1 with time dilation; clamp before use).</summary>
        float RenderAlpha { get; }
        /// <summary>Raised after each tick that advanced the simulation: (frame, input p0, input p1).</summary>
        event Action<int, ChibiInputPacket, ChibiInputPacket> OnFrame;
    }

    /// <summary>
    /// Unity host for an offline ChibiLocalSession. Mirrors ChibiP2PPeerLoop: a ChibiTickClock runs the simulation
    /// at exactly ChibiSimConst.TickRate ticks/second (dilation fixed at 1), independent of render rate and of
    /// FixedUpdate. Rendering reads Session.State; RenderAlpha is the fraction of the next tick elapsed.
    ///
    /// Usage:
    /// <code>
    /// var loop = gameObject.AddComponent&lt;ChibiLocalLoop&gt;();
    /// loop.Begin(cfg, keyboardSource, botSource);
    /// </code>
    /// </summary>
    public sealed class ChibiLocalLoop : MonoBehaviour, IChibiSimFeed
    {
        const float Dilation = 1f;

        /// <inheritdoc/>
        public ChibiSimState State => Session?.State;

        /// <inheritdoc/>
        /// <remarks>Forwards to the session, so subscribe after <see cref="Begin"/>.</remarks>
        public event Action<int, ChibiInputPacket, ChibiInputPacket> OnFrame
        {
            add { if (Session != null) Session.OnFrame += value; }
            remove { if (Session != null) Session.OnFrame -= value; }
        }

        /// <summary>The running session, or null before Begin.</summary>
        public ChibiLocalSession Session { get; private set; }

        /// <summary>
        /// Replay of every simulated frame, or null when recording was disabled. Always null in training: the T key fills
        /// the ult directly in the sim state (outside the input stream), so a training replay could never verify.
        /// </summary>
        public ChibiReplayRecorder Replay { get; private set; }

        /// <summary>Fraction of the next tick that has elapsed, for visual interpolation.</summary>
        public float RenderAlpha => _clock.RenderAlpha;

        readonly ChibiTickClock _clock = new ChibiTickClock();
        IChibiInputSource _p0, _p1;

        /// <summary>Start a new offline match. Pass two distinct source instances.</summary>
        /// <example><code>loop.Begin(cfg, p0, p1, recordReplay: false);</code></example>
        public void Begin(in ChibiMatchConfig cfg, IChibiInputSource p0, IChibiInputSource p1, bool recordReplay = true)
        {
            _p0 = p0;
            _p1 = p1;
            Session = new ChibiLocalSession(cfg, p0, p1);
            Replay = null;
            if (recordReplay && !cfg.Training)
            {
                Replay = new ChibiReplayRecorder(cfg);
                Session.OnFrame += Replay.Add;
            }
            _clock.Reset();
            _skipDelta = true;   // perf audit F3: no catch-up burst for the match-build frame
        }

        /// <summary>
        /// Offline pause (MC:106, rules v16): while true the simulation is not stepped at all, so every sim timer (match
        /// clock, ult window, cooldowns) is frozen and no input is polled. Never used online.
        /// </summary>
        public bool Paused { get; private set; }

        /// <summary>When true (default) Esc toggles <see cref="Paused"/> and, in training, T fills P1's ult (MC:106, MC:131).</summary>
        public bool KeyboardShortcuts = true;

        /// <summary>Pause or resume. Ignored once the match is over (MC:106 checks !Finished).</summary>
        /// <example><code>loop.SetPaused(false);   // the HUD's Resume button</code></example>
        public void SetPaused(bool paused)
        {
            if (Session == null || (paused && Session.State.MatchOver)) return;
            if (Paused && !paused) _clock.Reset();   // no catch-up burst for the paused time
            Paused = paused;
        }

        void Update()
        {
            if (Session == null) return;
            var kb = KeyboardShortcuts ? UnityEngine.InputSystem.Keyboard.current : null;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) SetPaused(!Paused);
            if (Paused) return;
            if (kb != null && kb.tKey.wasPressedThisFrame) ChibiSimulation.TrainingFillUlt(Session.State, 0);

            _p0.PollRenderFrame();
            _p1.PollRenderFrame();

            int n = _clock.Advance(PacedDelta(), Dilation);
            for (int i = 0; i < n; i++) Session.Tick();
        }

        // ------------------------------------------------------------------ frame pacing (perf audit 2026-11-11)
        // docs/performance/2026-11-11-smoothness-audit.md F2/F3. Offline only: ChibiOnlineMatch / ChibiP2PPeerLoop
        // must stay locked to wall time for rollback time sync, so they keep the raw delta.

        /// <summary>
        /// Most wall time one rendered frame may feed the offline clock: 3 ticks (50 ms). Down to 20 fps the game still
        /// runs at full speed; a longer hitch (texture upload, GC, window drag) becomes a brief slowdown instead of a
        /// 4-5 tick jump that teleports fighters and eats inputs. Frame-locked console fighters behave the same way.
        /// </summary>
        const float MaxFrameSeconds = 3f * ChibiTickClock.TickSeconds;

        bool _skipDelta = true;   // the first frame after Begin carries the match-build / load hitch: ignore it

        /// <summary>This frame's wall delta for the offline clock: load frame skipped, vSync jitter snapped, hitches capped.</summary>
        float PacedDelta()
        {
            if (_skipDelta) { _skipDelta = false; return 0f; }
            float dt = Time.unscaledDeltaTime;
            if (QualitySettings.vSyncCount > 0)
            {
                double hz = Screen.currentResolution.refreshRateRatio.value;
                if (hz >= 30.0) dt = ChibiTickClock.SnapToRefresh(dt, (float)(QualitySettings.vSyncCount / hz));
            }
            return Mathf.Min(dt, MaxFrameSeconds);
        }
    }
}
