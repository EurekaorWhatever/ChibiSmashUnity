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

        /// <summary>
        /// Respawn point of fighter <paramref name="i"/> on the current stage (spec 3.4 / stages README 3.5); Ocean moves it
        /// under MAIN during the whale. Rules v25: with 3-4 fighters each slot hovers at its own X offset
        /// (<see cref="RespawnSpreadX"/>) so two fighters KO'd close together never respawn stacked. 1v1: no offset.
        /// </summary>
        static void RespawnPoint(ChibiSimState s, int i, out int x, out int y)
        {
            var st = ChibiStageTable.Get(s.Stage);
            x = st.RespawnX; y = st.RespawnY;
            HazardRespawnOverride(s, ref x, ref y);
            if (s.PlayerCount > 2 && i >= 0 && i < RespawnSpreadX.Length) x += RespawnSpreadX[i];
        }

        // ---- rules v25: spawn points for 3 and 4 fighters (four-player-design.md section 5, Phase 2a).
        // Derived from the stage tables (ChibiStageTable SpawnX / HalfWidth) and design/gdd/stages/stage-geometry.json (MAIN
        // half-widths 7981 / 16991 / 7599 / 7765 / 7613 for stages 3..7): outer = min(1.5 x SpawnX, MAIN half-width - 1500)
        // rounded to 100, inner = outer / 3 rounded to 100, so four fighters stand evenly spaced on MAIN. Feet Y = SpawnY
        // (airborne, they drop onto MAIN top 0). 1v1 keeps +-SpawnX exactly.

        /// <summary>Outer spawn |X| per stage (fighters 0 / 1 with 3-4 fighters). Index = stage.</summary>
        public static readonly int[] SpawnOuterX = { 5400, 6000, 4700, 6000, 6800, 6000, 6200, 6000 };
        /// <summary>Inner spawn |X| per stage (fighters 2 / 3 with 4 fighters). Index = stage.</summary>
        public static readonly int[] SpawnInnerX = { 1800, 2000, 1600, 2000, 2300, 2000, 2100, 2000 };
        /// <summary>Respawn hover X offset per fighter slot with 3-4 fighters (milli).</summary>
        public static readonly int[] RespawnSpreadX = { -1200, 1200, -400, 400 };

        /// <summary>
        /// Spawn feet position and facing of fighter <paramref name="i"/> for <paramref name="players"/> fighters on
        /// <paramref name="stage"/>. 2: P0 at -SpawnX, P1 at +SpawnX (unchanged). 3: -outer, +outer, centre. 4: -outer,
        /// +outer, -inner, +inner. Everyone faces the centre (the centre fighter faces right).
        /// </summary>
        /// <example><code>ChibiSimulation.SpawnPoint(3, 4, 2, out int x, out int y, out int facing); // x = -2000</code></example>
        public static void SpawnPoint(int stage, int players, int i, out int x, out int y, out int facing)
        {
            stage = ChibiStageTable.Normalize(stage);
            var st = ChibiStageTable.Stages[stage];
            y = st.SpawnY;
            if (players <= 2)
                x = i == 0 ? -st.SpawnX : i == 1 ? st.SpawnX : (i == 2 ? -st.SpawnX / 2 : st.SpawnX / 2);
            else
            {
                int outer = SpawnOuterX[stage], inner = SpawnInnerX[stage];
                x = i == 0 ? -outer : i == 1 ? outer : i == 2 ? (players == 3 ? 0 : -inner) : inner;
            }
            facing = x > 0 ? -1 : 1;
        }

        /// <summary>The 3-4 fighter spawn and respawn tables of <paramref name="stage"/>, mixed into the config hash.</summary>
        public static void MixSpawnHash(ref uint h, int stage)
        {
            stage = ChibiStageTable.Normalize(stage);
            ChibiSimState.Mix(ref h, SpawnOuterX[stage]); ChibiSimState.Mix(ref h, SpawnInnerX[stage]);
            for (int k = 0; k < RespawnSpreadX.Length; k++) ChibiSimState.Mix(ref h, RespawnSpreadX[k]);
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
