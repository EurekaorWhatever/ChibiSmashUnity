using ChibiClash.Netcode;
using UnityEditor;

namespace ChibiClash.EditorTools
{
    /// <summary>Editor menu for the sim-driven test match (Play Mode only). Logic lives in ChibiSimTestMatch.</summary>
    /// <remarks>
    /// MenuItem paths must be compile-time constants, so the stage names below are literals that mirror
    /// StageBuilder.Names (index 0 Sky Dojo, 1 Neon City, 2 Lava Forge). Keep them in sync if stages change.
    /// </remarks>
    public static class ChibiSimTestMenu
    {
        const string StartRoot = "Chibi Clash/Start Sim Test Match/";
        const string StartSkyDojo = StartRoot + "Sky Dojo";
        const string StartNeonCity = StartRoot + "Neon City";
        const string StartLavaForge = StartRoot + "Lava Forge";
        const string StopPath = "Chibi Clash/Stop Sim Test Match";

        const int PrioSkyDojo = 100, PrioNeonCity = 101, PrioLavaForge = 102, PrioStop = 120;

        /// <summary>Hide the real game and start the deterministic-sim test match on Sky Dojo (stage 0).</summary>
        [MenuItem(StartSkyDojo, false, PrioSkyDojo)]
        public static void StartSkyDojoMatch() => ChibiSimTestMatch.Start(0);

        /// <summary>Hide the real game and start the deterministic-sim test match on Neon City (stage 1).</summary>
        [MenuItem(StartNeonCity, false, PrioNeonCity)]
        public static void StartNeonCityMatch() => ChibiSimTestMatch.Start(1);

        /// <summary>Hide the real game and start the deterministic-sim test match on Lava Forge (stage 2).</summary>
        [MenuItem(StartLavaForge, false, PrioLavaForge)]
        public static void StartLavaForgeMatch() => ChibiSimTestMatch.Start(2);

        [MenuItem(StartSkyDojo, true, PrioSkyDojo)]
        static bool ValidateStartSkyDojo() => CanStart();

        [MenuItem(StartNeonCity, true, PrioNeonCity)]
        static bool ValidateStartNeonCity() => CanStart();

        [MenuItem(StartLavaForge, true, PrioLavaForge)]
        static bool ValidateStartLavaForge() => CanStart();

        static bool CanStart() => EditorApplication.isPlaying && !ChibiSimTestMatch.IsRunning;

        /// <summary>Destroy the test match and restore the real game.</summary>
        [MenuItem(StopPath, false, PrioStop)]
        public static void StopMatch() => ChibiSimTestMatch.Stop();

        [MenuItem(StopPath, true, PrioStop)]
        static bool ValidateStop() => EditorApplication.isPlaying && ChibiSimTestMatch.IsRunning;
    }
}
