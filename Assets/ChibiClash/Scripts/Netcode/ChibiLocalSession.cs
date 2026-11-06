using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Offline (same-device / vs-bot) driver for the deterministic simulation. Pure C#, no Unity types, so it is
    /// unit testable headless. No rollback and no input delay: each Tick samples both sources once and steps.
    /// Not thread-safe.
    ///
    /// Usage:
    /// <code>
    /// var s = new ChibiLocalSession(cfg, new ChibiScriptedInputSource(f =&gt; default), keyboardSource);
    /// var rec = new ChibiReplayRecorder(cfg);
    /// s.OnFrame += rec.Add;
    /// while (s.Tick()) { }
    /// </code>
    /// </summary>
    public sealed class ChibiLocalSession
    {
        readonly IChibiInputSource _p0, _p1;

        /// <summary>The live simulation state (read it for rendering; do not mutate).</summary>
        public ChibiSimState State { get; }

        /// <summary>The config the match was initialised with.</summary>
        public ChibiMatchConfig Config { get; }

        /// <summary>Next frame to simulate (== State.Frame).</summary>
        public int Frame => State.Frame;

        /// <summary>
        /// Raised after each simulated frame with that frame's number and both inputs (wire-masked), in ascending
        /// order. Signature matches ChibiReplayRecorder.Add.
        /// </summary>
        public event Action<int, ChibiInputPacket, ChibiInputPacket> OnFrame;

        /// <summary>Create and initialise a match. Neither source may be null.</summary>
        /// <example><code>var s = new ChibiLocalSession(cfg, p0, p1);</code></example>
        public ChibiLocalSession(in ChibiMatchConfig cfg, IChibiInputSource p0, IChibiInputSource p1)
        {
            _p0 = p0 ?? throw new ArgumentNullException(nameof(p0));
            _p1 = p1 ?? throw new ArgumentNullException(nameof(p1));
            Config = cfg;
            State = new ChibiSimState();
            ChibiSimulation.Init(State, cfg);
        }

        /// <summary>
        /// Simulate one frame. Returns false once the match is over (including the tick that ends it);
        /// after that it does nothing and samples no input.
        /// </summary>
        /// <example><code>while (session.Tick()) { }</code></example>
        public bool Tick()
        {
            if (State.MatchOver) return false;
            int frame = State.Frame;
            var in0 = new ChibiInputPacket { Raw = (ushort)(_p0.SampleTick().Raw & ChibiInputPacket.WireMask) };
            var in1 = new ChibiInputPacket { Raw = (ushort)(_p1.SampleTick().Raw & ChibiInputPacket.WireMask) };
            ChibiSimulation.Step(State, in0, in1);
            OnFrame?.Invoke(frame, in0, in1);
            return !State.MatchOver;
        }
    }
}
