using UnityEngine;

namespace ChibiClash.Netcode
{
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
    public sealed class ChibiLocalLoop : MonoBehaviour
    {
        const float Dilation = 1f;

        /// <summary>The running session, or null before Begin.</summary>
        public ChibiLocalSession Session { get; private set; }

        /// <summary>Replay of every simulated frame, or null when recording was disabled.</summary>
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
            if (recordReplay)
            {
                Replay = new ChibiReplayRecorder(cfg);
                Session.OnFrame += Replay.Add;
            }
            _clock.Reset();
        }

        void Update()
        {
            if (Session == null) return;
            _p0.PollRenderFrame();
            _p1.PollRenderFrame();

            int n = _clock.Advance(Time.unscaledDeltaTime, Dilation);
            for (int i = 0; i < n; i++) Session.Tick();
        }
    }
}
