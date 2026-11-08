// Story type: Logic (test fixture helper, rules v10 time scale) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/timescale-conversion.md section 5 (Step order) and section 8.2 (D-N1 latch).
//
// D10 (pause-and-step): ChibiSimulation.Step is ONE REAL tick. Hitstop (3/5/6/8 frozen real ticks) and the ult cinematic
// (48 real ticks at 1/5 speed) insert real ticks that run no game step. Parity fixtures written for "1 Step = 1 game tick"
// count GAME STEPS through this helper instead.

using System;
using ChibiClash.Netcode;

namespace ChibiClash.Tests
{
    /// <summary>Test-only clock helpers: advance a <see cref="ChibiSimState"/> by game steps rather than real ticks.</summary>
    static class ChibiTestTime
    {
        /// <summary>Upper bound on real ticks per game step (hitstop 8 + cinematic 1/5 speed, with margin).</summary>
        const int MaxRealTicksPerStep = 64;

        /// <summary>
        /// Advances exactly ONE game step: calls <see cref="ChibiSimulation.Step"/> with the SAME packets on every real tick
        /// until <see cref="ChibiSimState.GameTicks"/> increments once. Repeating the packets is a held input: a press edge
        /// latches on the first real tick only (PrevInput then equals raw), so it reaches the game step once (section 8.2),
        /// and held levels (direction, Shield) stay held through the frozen ticks. With no hitstop / cinematic pending this
        /// is exactly one Step call, identical to the pre-D10 fixture. Once the match is over (no game steps any more) it
        /// makes one call, as before.
        /// </summary>
        public static void StepGame(ChibiSimState s, ChibiInputPacket in0, ChibiInputPacket in1)
        {
            int g0 = s.GameTicks;
            for (int n = 0; n < MaxRealTicksPerStep; n++)
            {
                bool over = s.MatchOver;
                ChibiSimulation.Step(s, in0, in1);
                if (over || s.GameTicks != g0) return;
            }
            throw new InvalidOperationException($"fixture: no game step within {MaxRealTicksPerStep} real ticks");
        }
    }
}
