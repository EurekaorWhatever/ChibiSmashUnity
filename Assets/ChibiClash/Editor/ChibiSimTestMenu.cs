using ChibiClash.Netcode;
using UnityEditor;

namespace ChibiClash.EditorTools
{
    /// <summary>Editor menu for the sim-driven test match (Play Mode only). Logic lives in ChibiSimTestMatch.</summary>
    public static class ChibiSimTestMenu
    {
        const string StartPath = "Chibi Clash/Start Sim Test Match";
        const string StopPath = "Chibi Clash/Stop Sim Test Match";

        /// <summary>Hide the real game and start the deterministic-sim test match.</summary>
        [MenuItem(StartPath)]
        public static void StartMatch() => ChibiSimTestMatch.Start();

        [MenuItem(StartPath, true)]
        static bool ValidateStart() => EditorApplication.isPlaying && !ChibiSimTestMatch.IsRunning;

        /// <summary>Destroy the test match and restore the real game.</summary>
        [MenuItem(StopPath)]
        public static void StopMatch() => ChibiSimTestMatch.Stop();

        [MenuItem(StopPath, true)]
        static bool ValidateStop() => EditorApplication.isPlaying && ChibiSimTestMatch.IsRunning;
    }
}
