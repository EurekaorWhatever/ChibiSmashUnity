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
