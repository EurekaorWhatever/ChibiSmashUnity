using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Offline (same-device / vs-bot) driver for the deterministic simulation. Pure C#, no Unity types, so it is
    /// unit testable headless. No rollback and no input delay: each Tick samples every source once and steps.
    /// Not thread-safe.
    ///
    /// Usage:
    /// <code>
    /// var s = new ChibiLocalSession(cfg, new ChibiScriptedInputSource(f =&gt; default), keyboardSource);
    /// var rec = new ChibiReplayRecorder(cfg);
    /// s.OnFrameAll += rec.Add;
    /// while (s.Tick()) { }
    /// </code>
    /// </summary>
    public sealed class ChibiLocalSession
    {
        readonly IChibiInputSource[] _src;
        readonly ChibiInputPacket[] _ins = new ChibiInputPacket[ChibiSimConst.MaxFighters];

        /// <summary>The live simulation state (read it for rendering; do not mutate).</summary>
        public ChibiSimState State { get; }

        /// <summary>The config the match was initialised with.</summary>
        public ChibiMatchConfig Config { get; }

        /// <summary>Next frame to simulate (== State.Frame).</summary>
        public int Frame => State.Frame;

        /// <summary>
        /// Raised after each simulated frame with that frame's number and the inputs of slots 0 and 1 (wire-masked), in
        /// ascending order. Signature matches the 1v1 ChibiReplayRecorder.Add.
        /// </summary>
        public event Action<int, ChibiInputPacket, ChibiInputPacket> OnFrame;

        /// <summary>
        /// Rules v25: raised after each simulated frame with every fighter's input (slot order, Count entries used). The
        /// array is reused: read it inside the callback only. Signature matches ChibiReplayRecorder.Add(frame, inputs).
        /// </summary>
        public event Action<int, ChibiInputPacket[]> OnFrameAll;

        /// <summary>Create and initialise a 1v1 match. Neither source may be null.</summary>
        /// <example><code>var s = new ChibiLocalSession(cfg, p0, p1);</code></example>
        public ChibiLocalSession(in ChibiMatchConfig cfg, IChibiInputSource p0, IChibiInputSource p1)
            : this(cfg, new[] { p0 ?? throw new ArgumentNullException(nameof(p0)), p1 ?? throw new ArgumentNullException(nameof(p1)) })
        {
        }

        /// <summary>
        /// Rules v25: create and initialise a match with one source per fighter (at least
        /// <see cref="ChibiMatchConfig.EffectiveCount"/>, none null; extra entries are ignored).
        /// </summary>
        /// <example><code>var s = new ChibiLocalSession(ffaCfg, new IChibiInputSource[] { human, cpu1, cpu2, cpu3 });</code></example>
        /// <exception cref="ArgumentException">Too few sources or a null source.</exception>
        public ChibiLocalSession(in ChibiMatchConfig cfg, IChibiInputSource[] sources)
        {
            int n = cfg.EffectiveCount;
            if (sources == null || sources.Length < n) throw new ArgumentException("one input source per fighter is required", nameof(sources));
            _src = new IChibiInputSource[n];
            for (int i = 0; i < n; i++) _src[i] = sources[i] ?? throw new ArgumentNullException(nameof(sources), "source " + i + " is null");
            Config = cfg;
            State = new ChibiSimState();
            ChibiSimulation.Init(State, cfg);
            // A CPU (or any state-reading source) sees the state at the start of each tick it is sampled for.
            for (int i = 0; i < n; i++) (_src[i] as IChibiStateAwareSource)?.Bind(State);
        }

        /// <summary>Number of input sources (= fighters).</summary>
        public int SourceCount => _src.Length;

        /// <summary>Input source of slot <paramref name="i"/>.</summary>
        public IChibiInputSource Source(int i) => _src[i];

        /// <summary>
        /// Simulate one frame. Returns false once the match is over (including the tick that ends it);
        /// after that it does nothing and samples no input.
        /// </summary>
        /// <example><code>while (session.Tick()) { }</code></example>
        public bool Tick()
        {
            if (State.MatchOver) return false;
            int frame = State.Frame;
            for (int i = 0; i < _src.Length; i++)
                _ins[i] = new ChibiInputPacket { Raw = (ushort)(_src[i].SampleTick().Raw & ChibiInputPacket.WireMask) }.Sanitized();
            ChibiSimulation.Step(State, _ins);
            OnFrame?.Invoke(frame, _ins[0], _ins[1]);
            OnFrameAll?.Invoke(frame, _ins);
            return !State.MatchOver;
        }
    }
}
