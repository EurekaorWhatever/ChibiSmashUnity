using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Rules v25 team play, phase 1 (user decisions 2026-11-23): formats 1v1 / FFA (3-4 fighters, each its own team) /
    /// 2v2, friendly fire off, NearestEnemy targeting, N x N melee, team elimination and timeout, placements and the
    /// dropped-slot sentinel. Integers only, no allocation, iteration always in slot order 0..Count-1. With Count 2 and
    /// Format 1v1 every helper reduces to the v24 "opponent = 1 - i" rule, so a 1v1 match is bit-identical.
    /// </summary>
    public static partial class ChibiSimulation
    {
        /// <summary>
        /// True when slot <paramref name="v"/> is out of the match for good, outside 1v1 (a 1v1 match ends on the same
        /// step, so the v24 rule never needed it): it dropped (always, training included, so a dropped slot never
        /// respawns as a zombie), or it lost its last stock (not in training, where KOs revive).
        /// </summary>
        public static bool Eliminated(ChibiSimState s, int v) =>
            s.Format != ChibiMatchConfig.Format1v1 &&
            ((s.DroppedMask & (1 << v)) != 0 || (!s.Training && s.F[v].Stocks <= 0));

        /// <summary>True when <paramref name="a"/> and <paramref name="v"/> are different fighters on the same team.</summary>
        public static bool IsAlly(ChibiSimState s, int a, int v) =>
            a != v && a >= 0 && v >= 0 && a < s.Count && v < s.Count && s.Team[a] == s.Team[v];

        /// <summary>
        /// True when fighter <paramref name="a"/> may hit / target <paramref name="v"/>: another slot in play range, on
        /// another team and not eliminated. Every attack path (melee, grab, projectile, zone, minion, cloud, fire ring)
        /// checks it, which is the friendly-fire-off rule. Stage hazards do not.
        /// </summary>
        public static bool IsFoe(ChibiSimState s, int a, int v) =>
            v >= 0 && v < s.Count && v != a && s.Team[v] != s.Team[a] && !Eliminated(s, v);

        /// <summary>
        /// The enemy of fighter <paramref name="i"/> with the smallest squared feet distance (long), ties to the lowest
        /// index; -1 when no enemy is left. In 1v1 it is always the opponent (1 - i).
        /// </summary>
        /// <example><code>int t = ChibiSimulation.NearestEnemy(state, 0); // 1 in a 1v1</code></example>
        public static int NearestEnemy(ChibiSimState s, int i)
        {
            var f = s.F[i];
            int best = -1;
            long bestD = long.MaxValue;
            for (int v = 0; v < s.Count; v++)
            {
                if (!IsFoe(s, i, v)) continue;
                var o = s.F[v];
                long d = ChibiFixed.DistSq(o.X - f.X, o.Y - f.Y);
                if (d < bestD) { bestD = d; best = v; }
            }
            return best;
        }

        /// <summary>The fighter <paramref name="i"/>'s grab / kick targets (HoldTarget); with none set in 1v1, the opponent; -1 if none.</summary>
        public static int HoldVictim(ChibiSimState s, int i)
        {
            int t = s.F[i].HoldTarget;
            if (t >= 0) return t;
            return s.Count == 2 ? 1 - i : -1;
        }

        /// <summary>
        /// View helper (read only, never used by the sim): the fighter most likely to have just hit <paramref name="d"/>.
        /// In 1v1 exactly the opponent (1 - d); otherwise whoever holds d, else the nearest enemy, else -1. The sim keeps
        /// no "last attacker" field, so presentation code uses this to place sparks and pick attacker sounds.
        /// </summary>
        public static int LikelyAttacker(ChibiSimState s, int d)
        {
            if (s.Count == 2) return 1 - d;
            int h = s.F[d].HeldBy;
            if (h >= 0 && h < s.Count) return h;
            return NearestEnemy(s, d);
        }

        /// <summary>The fighter holding <paramref name="i"/> (HeldBy); with none set in 1v1, the opponent.</summary>
        static int HolderOf(ChibiSimState s, int i)
        {
            int h = s.F[i].HeldBy;
            if (h >= 0) return h;
            return s.Count == 2 ? 1 - i : -1;
        }

        /// <summary>
        /// Every melee probe, then every hit in (attacker, victim) index order (v25 generalisation of "probe both, then
        /// apply"): simultaneous swings still trade, and in 1v1 the order is h0 then h1 exactly as before.
        /// </summary>
        static void MeleePhase(ChibiSimState s)
        {
            const int M = ChibiSimConst.MaxFighters;
            int n = s.Count;
            Span<PendingHit> hits = stackalloc PendingHit[M * M];
            for (int i = 0; i < n; i++) MeleeProbeAll(s, i, hits.Slice(i * M, M));
            for (int i = 0; i < n; i++)
                for (int v = 0; v < n; v++)
                {
                    var h = hits[i * M + v];
                    if (h.Valid) ApplyHit(s, i, v, h);
                }
        }

        /// <summary>
        /// Spawn point of slot <paramref name="i"/> (feet X and Facing; feet Y is the stage SpawnY). 1v1 keeps (-SpawnX,
        /// +SpawnX). 2v2: team 0 on the left, team 1 on the right, the first member of a team outside (SpawnX), the second
        /// inside (SpawnX / 3). FFA 3: -SpawnX, +SpawnX, 0. FFA 4: -SpawnX, +SpawnX, -SpawnX / 3, +SpawnX / 3. Every point
        /// is above the main stage (|x| &lt;= SpawnX &lt; HalfWidth on every stage). Facing points at the centre (x = 0 faces right).
        /// </summary>
        public static void SpawnPoint(ChibiSimState s, int i, out int x, out int facing)
        {
            var st = ChibiStageTable.Get(s.Stage);
            int far = st.SpawnX, near = st.SpawnX / 3;
            if (s.Count <= 2) x = i == 0 ? -far : far;
            else if (s.Format == ChibiMatchConfig.Format2v2)
            {
                int rank = 0;
                for (int j = 0; j < i; j++) if (s.Team[j] == s.Team[i]) rank++;
                x = (s.Team[i] == 0 ? -1 : 1) * (rank == 0 ? far : near);
            }
            else if (s.Count == 3) x = i == 0 ? -far : i == 1 ? far : 0;
            else x = i == 0 ? -far : i == 1 ? far : i == 2 ? -near : near;
            facing = x > 0 ? -1 : 1;
        }

        /// <summary>
        /// The dropped-slot mechanism (online leaver, decision 2026-11-23): on the frame slot <paramref name="i"/>'s input
        /// is <see cref="ChibiInputPacket.DroppedRaw"/> the fighter forfeits its stocks and goes out for good: its own
        /// hold and any hold on it end, its later inputs are ignored and it never respawns. Idempotent. The match end
        /// (team elimination) is checked at the end of the next game step, so during hitstop or an ult cinematic a
        /// team-wiping drop ends the match only once game time resumes (bounded; both peers agree because it is in state).
        /// </summary>
        static void DropFighter(ChibiSimState s, int i)
        {
            if ((s.DroppedMask & (1 << i)) != 0) return;
            s.DroppedMask |= 1 << i;
            var f = s.F[i];
            Interrupt(s, i);
            if (f.Held)
            {
                int hb = HolderOf(s, i);
                if (hb >= 0) Interrupt(s, hb);
                f.Held = false; f.HeldBy = -1;
            }
            f.Stocks = 0;
            if (f.OutTicks <= 0) f.OutTicks = ChibiPercentConst.RespawnDelay;
            f.HoverTicks = 0;
            f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Stun = 0; f.ClearLaunch();
            f.Grounded = false;
            f.UltFirePending = false; f.UltWindowTicks = 0;
        }

        /// <summary>Teams with at least one member holding stocks; <paramref name="lastTeam"/> = the highest such team id.</summary>
        static int AliveTeams(ChibiSimState s, out int lastTeam)
        {
            int mask = 0;
            for (int i = 0; i < s.Count; i++) if (s.F[i].Stocks > 0) mask |= 1 << s.Team[i];
            int n = 0;
            lastTeam = -1;
            for (int t = 0; t < ChibiSimConst.MaxFighters; t++)
                if ((mask & (1 << t)) != 0) { n++; lastTeam = t; }
            return n;
        }

        /// <summary>
        /// Team timeout winner (rules v25): the most total stocks, then the lowest total percent of the members still
        /// holding stocks; a tie at the top is a draw (-1). With two single-fighter teams this is <see cref="TimeoutWinner"/>.
        /// </summary>
        public static int TeamTimeoutWinner(ChibiSimState s)
        {
            const int M = ChibiSimConst.MaxFighters;
            Span<int> stocks = stackalloc int[M];
            Span<int> pct = stackalloc int[M];
            int present = 0;
            for (int i = 0; i < s.Count; i++)
            {
                int t = s.Team[i];
                present |= 1 << t;
                if (s.F[i].Stocks <= 0) continue;
                stocks[t] += s.F[i].Stocks;
                pct[t] += s.F[i].PctM;
            }
            int best = -1;
            bool tie = false;
            for (int t = 0; t < M; t++)
            {
                if ((present & (1 << t)) == 0) continue;
                if (best < 0) { best = t; continue; }
                if (stocks[t] == stocks[best] && pct[t] == pct[best]) { tie = true; continue; }
                if (stocks[t] > stocks[best] || (stocks[t] == stocks[best] && pct[t] < pct[best])) { best = t; tie = false; }
            }
            return tie ? -1 : best;
        }

        /// <summary>
        /// Placement of the fighters that lost their last stock this step: fighters still holding stocks + 1, so
        /// simultaneous eliminations share a place.
        /// </summary>
        static void UpdatePlacements(ChibiSimState s)
        {
            int newly = 0, aliveAfter = 0;
            for (int i = 0; i < s.Count; i++)
            {
                if (s.Placement[i] != 0) continue;
                if (s.F[i].Stocks <= 0) newly |= 1 << i;
                else aliveAfter++;
            }
            for (int i = 0; i < s.Count; i++) if ((newly & (1 << i)) != 0) s.Placement[i] = aliveAfter + 1;
        }

        /// <summary>
        /// Match end: the survivors get their place. Members of the winning team are 1st; any other survivor is ranked
        /// behind the winners and behind survivors with more stocks (or equal stocks and less percent).
        /// </summary>
        static void FinishPlacements(ChibiSimState s)
        {
            int surv = 0;
            for (int i = 0; i < s.Count; i++) if (s.Placement[i] == 0) surv |= 1 << i;
            for (int i = 0; i < s.Count; i++)
            {
                if ((surv & (1 << i)) == 0) continue;
                if (s.Winner >= 0 && s.Team[i] == s.Winner) { s.Placement[i] = 1; continue; }
                int better = 0;
                for (int j = 0; j < s.Count; j++)
                {
                    if (j == i || (surv & (1 << j)) == 0) continue;
                    var a = s.F[j]; var b = s.F[i];
                    if ((s.Winner >= 0 && s.Team[j] == s.Winner) || a.Stocks > b.Stocks || (a.Stocks == b.Stocks && a.PctM < b.PctM))
                        better++;
                }
                s.Placement[i] = better + 1;
            }
        }
    }
}
