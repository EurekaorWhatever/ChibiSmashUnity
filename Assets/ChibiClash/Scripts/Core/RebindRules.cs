namespace ChibiClash
{
    /// <summary>What the rebind listener does this frame.</summary>
    public enum RebindStep
    {
        /// <summary>Keep listening.</summary>
        Wait,
        /// <summary>Stop listening and keep the old binding.</summary>
        Cancel,
        /// <summary>Bind the reported binding string and stop listening.</summary>
        Bind,
    }

    /// <summary>One frame of input while Settings listens for a new binding (filled by GameManager from the devices).</summary>
    public struct RebindFrame
    {
        /// <summary>Seconds since listening started (unscaled).</summary>
        public float Elapsed;
        /// <summary>Esc was pressed this frame.</summary>
        public bool Escape;
        /// <summary>A gamepad's B / East or Back / Select was pressed this frame.</summary>
        public bool PadCancel;
        /// <summary>Key enum name of a keyboard key pressed this frame (not Esc), else null.</summary>
        public string Key;
        /// <summary>Mouse binding string (<see cref="Bindings.MouseLeft"/> ...) of a button pressed this frame, else null.</summary>
        public string MouseButton;
        /// <summary>The pointer is over the row being rebound.</summary>
        public bool PointerOnRow;
    }

    /// <summary>
    /// Pure rules of the rebind listener (review 2026-11-16 item 16): Esc or a gamepad B / Back cancels, listening
    /// times out after <see cref="TimeoutSeconds"/>, a mouse button binds only when clicked on the row being rebound
    /// (a click anywhere else cancels), and nothing reacts in the first <see cref="GraceSeconds"/> (the click that
    /// started listening).
    /// </summary>
    /// <example><code>
    /// var step = RebindRules.Decide(frame, out string binding);
    /// if (step == RebindStep.Bind) Bindings.Set(player, action, binding);
    /// </code></example>
    public static class RebindRules
    {
        /// <summary>Listening gives up after this many seconds.</summary>
        public const float TimeoutSeconds = 5f;
        /// <summary>Mouse buttons are ignored this long after listening starts.</summary>
        public const float GraceSeconds = 0.25f;

        /// <summary>The listener's decision for one frame; <paramref name="binding"/> is set when it returns Bind.</summary>
        public static RebindStep Decide(in RebindFrame f, out string binding)
        {
            binding = null;
            if (f.Escape || f.PadCancel || f.Elapsed > TimeoutSeconds) return RebindStep.Cancel;
            if (f.MouseButton != null && f.Elapsed > GraceSeconds)
            {
                if (!f.PointerOnRow) return RebindStep.Cancel;          // a click elsewhere means "never mind"
                binding = f.MouseButton;
                return RebindStep.Bind;
            }
            if (f.Key != null) { binding = f.Key; return RebindStep.Bind; }
            return RebindStep.Wait;
        }
    }
}
