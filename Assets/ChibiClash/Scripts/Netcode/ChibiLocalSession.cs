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
        readonly IChibiInputSource[] _sources;
        /// <summary>This tick's sanitized inputs, one per fighter (reused: Tick does not allocate).</summary>
        readonly ChibiInputPacket[] _inputs;

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

        /// <summary>
        /// Raised after each simulated frame with that frame's number and every fighter's input (four players). The array
        /// is reused by the next Tick: copy what you keep.
        /// </summary>
        public event Action<int, ChibiInputPacket[]> OnFrameAll;

        /// <summary>Create and initialise a match. Neither source may be null.</summary>
        /// <example><code>var s = new ChibiLocalSession(cfg, p0, p1);</code></example>
        public ChibiLocalSession(in ChibiMatchConfig cfg, IChibiInputSource p0, IChibiInputSource p1)
            : this(cfg, new[] { p0 ?? throw new ArgumentNullException(nameof(p0)), p1 ?? throw new ArgumentNullException(nameof(p1)) })
        {
        }

        /// <summary>
        /// Create and initialise a match for 2..4 fighters (four-player-design.md 1.1): <paramref name="sources"/>[i] drives
        /// fighter i and its length must equal the config's player count. No entry may be null.
        /// </summary>
        /// <example><code>var s = new ChibiLocalSession(cfg, new IChibiInputSource[] { pad0, pad1, cpu2, cpu3 });</code></example>
        public ChibiLocalSession(in ChibiMatchConfig cfg, IChibiInputSource[] sources)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            if (sources.Length != cfg.Players)
                throw new ArgumentException($"need {cfg.Players} input sources, got {sources.Length}", nameof(sources));
            for (int i = 0; i < sources.Length; i++)
                if (sources[i] == null) throw new ArgumentNullException(nameof(sources), $"source {i} is null");
            _sources = (IChibiInputSource[])sources.Clone();
            _inputs = new ChibiInputPacket[sources.Length];
            Config = cfg;
            State = new ChibiSimState();
            ChibiSimulation.Init(State, cfg);
            // A CPU (or any state-reading source) sees the state at the start of each tick it is sampled for.
            for (int i = 0; i < _sources.Length; i++) (_sources[i] as IChibiStateAwareSource)?.Bind(State);
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
            for (int i = 0; i < _sources.Length; i++)   // sampled in fighter order (1v1: p0 then p1, as before)
                _inputs[i] = new ChibiInputPacket { Raw = (ushort)(_sources[i].SampleTick().Raw & ChibiInputPacket.WireMask) }.Sanitized();
            ChibiSimulation.Step(State, _inputs);
            OnFrame?.Invoke(frame, _inputs[0], _inputs[1]);
            OnFrameAll?.Invoke(frame, _inputs);
            return !State.MatchOver;
        }
    }
}
