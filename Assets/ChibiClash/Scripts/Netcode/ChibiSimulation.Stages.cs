using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Rules v20 stage mechanics (design/gdd/stages/README.md 3.2 and 3.5, decisions ST-1..ST-5): moving-platform riders,
    /// respawn point, the stage part of the config hash, and read-only helpers for the view / HUD / CPU.
    /// </summary>
    public static partial class ChibiSimulation
    {
        /// <summary>
        /// Platform clock the view should use after a step: the game tick the last step simulated (positions used by its
        /// collisions). GameTicks is incremented after each game step.
        /// </summary>
        public static int PlatformClock(ChibiSimState s) => s.GameTicks > 0 ? s.GameTicks - 1 : 0;

        /// <summary>Centre X (milli) of platform <paramref name="p"/> of the current stage as last simulated (view / CPU).</summary>
        public static int PlatformCx(ChibiSimState s, int p) => ChibiStageTable.Get(s.Stage).Platforms[p].CxAt(PlatformClock(s));

        /// <summary>Top Y (milli, feet convention) of platform <paramref name="p"/> of the current stage as last simulated.</summary>
        public static int PlatformTop(ChibiSimState s, int p) => ChibiStageTable.Get(s.Stage).Platforms[p].TopAt(PlatformClock(s));

        /// <summary>
        /// README 3.2: a fighter (or minion) resting on a moving platform at the end of the previous game step is carried
        /// by that platform's delta (F1 at GameTicks minus F1 at GameTicks - 1) before anything else this step. Held and
        /// Out fighters are not carried. Static platforms have a zero delta.
        /// </summary>
        static void CarryRiders(ChibiSimState s)
        {
            int t = s.GameTicks;
            if (t == 0) return;
            var st = ChibiStageTable.Get(s.Stage);
            var plats = st.Platforms;
            bool any = false;
            for (int p = 0; p < plats.Length; p++) any |= plats[p].Moves;
            if (!any) return;
            for (int i = 0; i < s.PlayerCount; i++)
            {
                var f = s.F[i];
                if (f.Held || f.OutTicks > 0) continue;
                int p = RestingPlatform(f, st, t - 1);
                if (p < 0 || !plats[p].Moves) continue;
                f.X += plats[p].CxAt(t) - plats[p].CxAt(t - 1);
                f.Y += plats[p].TopAt(t) - plats[p].TopAt(t - 1);
            }
            const int r = ChibiSimConst.MinionR;
            for (int n = 0; n < s.Minions.Length; n++)
            {
                var m = s.Minions[n];
                if (!m.Active || m.YRem != 0) continue;
                for (int p = 0; p < plats.Length; p++)
                {
                    if (!plats[p].Moves || m.Y != plats[p].TopAt(t - 1) + r ||
                        Math.Abs(m.X - plats[p].CxAt(t - 1)) >= plats[p].HalfW) continue;
                    m.X += plats[p].CxAt(t) - plats[p].CxAt(t - 1);
                    m.Y += plats[p].TopAt(t) - plats[p].TopAt(t - 1);
                    break;
                }
            }
        }

        /// <summary>Respawn point of the current stage (spec 3.4 / stages README 3.5); Ocean moves it under MAIN during the whale.</summary>
        static void RespawnPoint(ChibiSimState s, out int x, out int y)
        {
            var st = ChibiStageTable.Get(s.Stage);
            x = st.RespawnX; y = st.RespawnY;
            HazardRespawnOverride(s, ref x, ref y);
        }

        /// <summary>Projectiles created by an ultimate (rules v20 ult base bonus): Smash waves, Thousand Arrows.</summary>
        /// <remarks>Rules v21 (lead): Sword Aura waves are NOT ult hits (they come from normal heavies during the aura).</remarks>
        static bool IsUltProjectile(ChibiProjKind k) =>
            k == ChibiProjKind.SmashWave || k == ChibiProjKind.VolleyLead || k == ChibiProjKind.VolleyArrow;

        /// <summary>Mixes a stage's full geometry into the config hash (rules v20): peers with other geometry refuse each other.</summary>
        public static void MixStageHash(ref uint h, ChibiStageDef st)
        {
            ChibiSimState.Mix(ref h, st.HalfWidth); ChibiSimState.Mix(ref h, st.MainBottom); ChibiSimState.Mix(ref h, st.BlastX);
            ChibiSimState.Mix(ref h, st.KillY); ChibiSimState.Mix(ref h, st.CeilingY);
            ChibiSimState.Mix(ref h, (st.KoSides ? 1 : 0) | (st.KoTop ? 2 : 0) | (st.KoBottom ? 4 : 0));
            ChibiSimState.Mix(ref h, st.SpawnX); ChibiSimState.Mix(ref h, st.SpawnY);
            ChibiSimState.Mix(ref h, st.RespawnX); ChibiSimState.Mix(ref h, st.RespawnY); ChibiSimState.Mix(ref h, st.Hazard);
            ChibiSimState.Mix(ref h, st.HasFloor ? 1 : 0); ChibiSimState.Mix(ref h, st.FloorY); ChibiSimState.Mix(ref h, st.FloorHalfW);   // v21
            var plats = st.Platforms;
            ChibiSimState.Mix(ref h, plats.Length);
            for (int p = 0; p < plats.Length; p++)
            {
                var q = plats[p];
                ChibiSimState.Mix(ref h, q.Cx); ChibiSimState.Mix(ref h, q.Top); ChibiSimState.Mix(ref h, q.HalfW);
                ChibiSimState.Mix(ref h, q.Axis); ChibiSimState.Mix(ref h, q.Min); ChibiSimState.Mix(ref h, q.Max);
                ChibiSimState.Mix(ref h, q.Period); ChibiSimState.Mix(ref h, q.Phase);
            }
            MixHazardHash(ref h);
        }
    }
}
