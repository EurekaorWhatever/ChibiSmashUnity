using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// FPS and ping readout (user request 2026-11-27): a chip beside the language button on menu pages and a small line in
    /// the top-right corner during a match, FPS first. Shown while Settings "Show FPS / ping" is on. FPS is averaged over
    /// half a second; ping is the online rollback session's smoothed round trip, "--" when there is none. The text is
    /// rebuilt only when a shown number changes, so drawing it allocates nothing per frame.
    /// <example><code>PerfStats.Tick(Time.unscaledDeltaTime); PerfStats.PingMs = match.PingMs; Gui.Text(r, PerfStats.Text, 11, Gui.Muted);</code></example>
    /// </summary>
    public static class PerfStats
    {
        /// <summary>Seconds per FPS sample window.</summary>
        public const float Window = 0.5f;

        static float acc;
        static int frames;
        static int fps = -1, shownFps = -2, shownPing = -2;
        static int locVersion = -1;
        static string text = "";

        /// <summary>Round trip in ms of the current online match, or -1 (offline, menus, no sample yet). Set each frame.</summary>
        public static int PingMs = -1;

        /// <summary>Last averaged frames per second (-1 before the first window ends).</summary>
        public static int Fps => fps;

        /// <summary>Counts one rendered frame; call once per Update with the unscaled delta.</summary>
        public static void Tick(float dt)
        {
            acc += dt;
            frames++;
            if (acc < Window) return;
            fps = Mathf.RoundToInt(frames / acc);
            acc = 0f;
            frames = 0;
        }

        /// <summary>"60 FPS · PING 42 ms" (or "PING --") in the current language, cached.</summary>
        public static string Text
        {
            get
            {
                if (fps != shownFps || PingMs != shownPing || locVersion != Loc.Version)
                {
                    shownFps = fps; shownPing = PingMs; locVersion = Loc.Version;
                    text = Format(fps, PingMs);
                }
                return text;
            }
        }

        /// <summary>Pure formatting (tested): FPS first, then ping; unknown values show "--".</summary>
        public static string Format(int fpsValue, int pingMs) =>
            Loc.T("stats.fps", fpsValue >= 0 ? fpsValue.ToString() : "--") + "  ·  " +
            (pingMs >= 0 ? Loc.T("stats.ping", pingMs) : Loc.T("stats.ping_none"));

        /// <summary>Round trip in ms from the rollback session's x8 tick value (-1 stays -1).</summary>
        public static int RttX8ToMs(int rttTicksX8, int tickRate) =>
            rttTicksX8 < 0 ? -1 : (int)((long)rttTicksX8 * 1000 / (8L * tickRate));
    }
}
