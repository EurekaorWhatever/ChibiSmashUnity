namespace ChibiClash
{
    /// <summary>
    /// Pure rules for who may earn match rewards. Kept apart from <see cref="ChibiMatch"/> so they are unit-testable.
    /// </summary>
    public static class MatchRewardRules
    {
        /// <summary>
        /// True when the local slot is driven by a human (not a CPU). Offline rewards and the profile save happen only
        /// then; an all-CPU (spectator) match pays nothing. An out-of-range slot or a missing flag array counts as not human.
        /// </summary>
        /// <example><code>if (!MatchRewardRules.LocalSlotIsHuman(config.cpu, localPlayer)) return; // no rewards</code></example>
        public static bool LocalSlotIsHuman(bool[] cpu, int localPlayer) =>
            cpu != null && localPlayer >= 0 && localPlayer < cpu.Length && !cpu[localPlayer];
    }
}
