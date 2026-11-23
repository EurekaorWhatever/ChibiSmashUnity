// Four players (docs/architecture/netcode/four-player-design.md sections 1.1-1.5, decisions in section 9).
// Phase 1 made the engine run 2..4 fighters with 1v1 bit-identical to rules v24. Phase 2a (rules v25): FFA (Battle
// Royale) and Team (2v2) rules, every four-player field hashed always (the Phase 1 "1v1 canonical" trick is gone),
// presentMask (a leaver is eliminated at an exact tick), 3-4 fighter spawns per stage.
// Integers only, no statics with state, fixed iteration order (netcode-core rules).
using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Match mode for 2..4 fighters (four-player-design.md 1.3). FreeForAll: everyone is an enemy (1v1 and Battle
    /// Royale). Team: fighters on the same <see cref="ChibiMatchConfig.TeamOf"/> bit are teammates (2v2, Phase 2).
    /// </summary>
    public enum ChibiMatchMode { FreeForAll = 0, Team = 1 }

    public partial struct ChibiMatchConfig
    {
        /// <summary>Fighters 2 and 3 (used when <see cref="PlayerCount"/> &gt; 2).</summary>
        public FighterClass Class2, Class3;
        /// <summary>Passives of fighters 2 and 3.</summary>
        public int Passive2, Passive3;
        /// <summary>Fighters in the match, 2..4. 0 or 1 (an unset struct) plays as 2; above 4 plays as 4.</summary>
        public int PlayerCount;
        /// <summary>Free-for-all (default) or Team.</summary>
        public ChibiMatchMode Mode;
        /// <summary>Team of fighter i in bit i (Team mode only).</summary>
        public int TeamOf;

        /// <summary>Player count clamped to 2..<see cref="ChibiSimState.MaxFighters"/>.</summary>
        public int Players => PlayerCount <= 2 ? 2 : (PlayerCount > ChibiSimState.MaxFighters ? ChibiSimState.MaxFighters : PlayerCount);

        /// <summary>Class of fighter <paramref name="i"/> (0..3).</summary>
        public FighterClass ClassOf(int i) => i == 0 ? Class0 : i == 1 ? Class1 : i == 2 ? Class2 : Class3;

        /// <summary>Passive of fighter <paramref name="i"/> (0..3).</summary>
        public int PassiveOf(int i) => i == 0 ? Passive0 : i == 1 ? Passive1 : i == 2 ? Passive2 : Passive3;

        /// <summary>True when this config uses anything beyond a 1v1 (more players, a mode, teams).</summary>
        public bool IsMultiplayer => PlayerCount > 2 || Mode != ChibiMatchMode.FreeForAll || TeamOf != 0;

        /// <summary>Four-player part of <see cref="ComputeHash"/> (called last). Rules v25: mixed for every config.</summary>
        void MixPlayers(ref uint h)
        {
            ChibiSimState.Mix(ref h, ChibiSimState.MaxFighters);
            ChibiSimState.Mix(ref h, Players);
            ChibiSimState.Mix(ref h, (int)Mode);
            ChibiSimState.Mix(ref h, TeamOf);
            // Seats 2..3 only when they play, so a 1v1 hash does not depend on unused seat values.
            for (int i = 2; i < Players; i++) { ChibiSimState.Mix(ref h, (int)ClassOf(i)); ChibiSimState.Mix(ref h, PassiveOf(i)); }
            ChibiSimulation.MixSpawnHash(ref h, ChibiStageTable.Normalize(Stage));   // 3-4 fighter spawns (Stages.cs)
        }
    }

    public sealed partial class ChibiFighterSim
    {
        /// <summary>Index of the fighter holding this one, or -1. Invariant: F[v].HeldBy == h &lt;=&gt; F[h].HoldTarget == v.</summary>
        public int HeldBy = -1;
        /// <summary>Index of the fighter this one holds (Grab, Polearm Kick), or -1.</summary>
        public int HoldTarget = -1;
        /// <summary>One bit per victim already hit by the current swing (hit once per victim per arm). Replaces the v23 bool.</summary>
        public int MoveHitMask;
        /// <summary>Fighter whose hit (landed or blocked) last reached this one, or -1. View attribution and KO credit.</summary>
        public int LastHitBy = -1;
        /// <summary>Victim chosen by a grab or Polearm Kick program (its box-less hits go to this fighter only), or -1.</summary>
        public int ProgTarget = -1;

        /// <summary>This fighter is held (kinematic: no input, no Physics, V 0).</summary>
        public bool Held => HeldBy >= 0;
        /// <summary>This fighter holds another fighter.</summary>
        public bool Holding => HoldTarget >= 0;
        /// <summary>The current swing has hit someone (the v23 hit-once flag).</summary>
        public bool MoveHit => MoveHitMask != 0;

        void CopySeats(ChibiFighterSim o)
        {
            HeldBy = o.HeldBy; HoldTarget = o.HoldTarget; MoveHitMask = o.MoveHitMask; LastHitBy = o.LastHitBy;
            ProgTarget = o.ProgTarget;
        }

        internal void ResetSeats()
        {
            HeldBy = -1; HoldTarget = -1; MoveHitMask = 0; LastHitBy = -1; ProgTarget = -1;
        }

        /// <summary>Four-player fighter fields, mixed into the checksum for every live fighter (rules v25).</summary>
        internal void HashSeats(ref uint h)
        {
            ChibiSimState.Mix(ref h, HeldBy); ChibiSimState.Mix(ref h, HoldTarget); ChibiSimState.Mix(ref h, MoveHitMask);
            ChibiSimState.Mix(ref h, LastHitBy); ChibiSimState.Mix(ref h, ProgTarget);
        }
    }

    public sealed partial class ChibiProjectileSim
    {
        /// <summary>
        /// One bit per fighter this shot already hit (pierce shots hit each enemy once). Set and cleared together with
        /// <see cref="FHasHit"/>, which stays as the "hit anyone" flag the view reads.
        /// </summary>
        public int HitMask;
    }

    public sealed partial class ChibiSimState
    {
        /// <summary>Fighter capacity of every state (the F array length).</summary>
        public const int MaxFighters = 4;

        /// <summary>Fighters in this match, 2..4 (set by Init). Slots at or above it are never stepped or hashed.</summary>
        public int PlayerCount = 2;
        /// <summary>Match mode copied from the config.</summary>
        public ChibiMatchMode Mode;
        /// <summary>Team of fighter i in bit i (Team mode).</summary>
        public int TeamOf;
        /// <summary>Bit per winning fighter once MatchOver (0 = draw). 1v1: 1 &lt;&lt; Winner.</summary>
        public int WinnerMask;
        /// <summary>Finish place per fighter (1 = first, shared on a tie), 0 while still playing.</summary>
        public readonly int[] Place = new int[MaxFighters];
        /// <summary>
        /// Bit i set while player i is still in the match (four-player-design.md 2.4). Init sets every live bit; the
        /// session clears a bit through <c>ChibiSimulation.StepPresent</c>, and that fighter is
        /// eliminated on that exact tick. Sticky: a cleared bit never comes back.
        /// </summary>
        public int PresentMask;

        /// <summary>Fighters in play this match (alias of <see cref="PlayerCount"/>).</summary>
        public int Count => PlayerCount;

        void CopyPlayers(ChibiSimState o)
        {
            PlayerCount = o.PlayerCount; Mode = o.Mode; TeamOf = o.TeamOf; WinnerMask = o.WinnerMask; PresentMask = o.PresentMask;
            for (int i = 0; i < MaxFighters; i++) Place[i] = o.Place[i];
        }

        /// <summary>Fighters hashed: PlayerCount clamped to 2..MaxFighters.</summary>
        int HashedFighters => PlayerCount <= 2 ? 2 : (PlayerCount > MaxFighters ? MaxFighters : PlayerCount);

        /// <summary>
        /// Four-player part of <see cref="ComputeChecksum"/>, mixed last: fighters 2.., every fighter's seat fields, each
        /// live projectile's HitMask and the match fields. Rules v25: always (no 1v1 special case).
        /// </summary>
        void HashPlayers(ref uint h)
        {
            int n = HashedFighters;
            for (int i = 2; i < n; i++) F[i].Hash(ref h);
            for (int i = 0; i < n; i++) F[i].HashSeats(ref h);
            for (int k = 0; k < Proj.Length; k++)
                if (Proj[k].Active) Mix(ref h, Proj[k].HitMask);
            Mix(ref h, PlayerCount); Mix(ref h, (int)Mode); Mix(ref h, TeamOf); Mix(ref h, WinnerMask); Mix(ref h, PresentMask);
            for (int i = 0; i < MaxFighters; i++) Mix(ref h, Place[i]);
        }
    }

    public static partial class ChibiSimulation
    {
        /// <summary>
        /// Foe test (four-player-design.md 1.3, without the InPlay part): j is another fighter of this match and, in Team
        /// mode, on the other team. Every probe still runs its own checks (InPlay, invuln, boxes).
        /// </summary>
        public static bool IsFoe(ChibiSimState s, int i, int j)
        {
            if (j == i || j < 0 || j >= s.PlayerCount || j >= ChibiSimState.MaxFighters) return false;
            return s.Mode != ChibiMatchMode.Team || ((s.TeamOf >> i) & 1) != ((s.TeamOf >> j) & 1);
        }

        /// <summary>Enemy(i, j) of section 1.3: a foe that is in play (not Out, not hovering after a respawn).</summary>
        public static bool IsEnemy(ChibiSimState s, int i, int j) => IsFoe(s, i, j) && s.F[j].InPlay;

        /// <summary>
        /// Nearest enemy of fighter <paramref name="i"/>: smallest dx² + dy² (feet, as long); on a tie the lowest index.
        /// Returns -1 when no enemy is in play.
        /// </summary>
        /// <example><code>int t = ChibiSimulation.NearestEnemy(s, 0); if (t &gt;= 0) aimX = s.F[t].X;</code></example>
        public static int NearestEnemy(ChibiSimState s, int i) => Nearest(s, i, true);

        /// <summary>
        /// The fighter that "the opponent" means for <paramref name="i"/>. 1v1: always 1 - i (exactly the rules-v24 lookup,
        /// even while that fighter is Out). More fighters: the nearest enemy; with none in play, the nearest foe (Out or
        /// hovering, so the caller's own InPlay checks decide); with no foe at all, the lowest other index.
        /// </summary>
        public static int Foe(ChibiSimState s, int i)
        {
            if (s.PlayerCount <= 2) return 1 - i;
            int e = Nearest(s, i, true);
            if (e >= 0) return e;
            e = Nearest(s, i, false);
            if (e >= 0) return e;
            return i == 0 ? 1 : 0;
        }

        static int Nearest(ChibiSimState s, int i, bool inPlayOnly)
        {
            var f = s.F[i];
            int best = -1;
            long bestD = 0;
            for (int j = 0; j < s.PlayerCount && j < ChibiSimState.MaxFighters; j++)
            {
                if (!IsFoe(s, i, j) || (inPlayOnly && !s.F[j].InPlay)) continue;
                long dx = (long)s.F[j].X - f.X, dy = (long)s.F[j].Y - f.Y;
                long d = dx * dx + dy * dy;
                if (best < 0 || d < bestD) { best = j; bestD = d; }
            }
            return best;
        }

        /// <summary>
        /// Fighter i is out of the match for good (not training): it left (presentMask), or N &gt; 2 and no stocks left.
        /// Eliminated fighters are never updated again (they stay Out).
        /// </summary>
        static bool Eliminated(ChibiSimState s, int i) =>
            !s.Training && ((s.PresentMask & (1 << i)) == 0 || (s.PlayerCount > 2 && s.F[i].Stocks <= 0));

        /// <summary>
        /// presentMask hook (four-player-design.md 2.4): every live fighter whose bit is set in s.PresentMask but clear in
        /// <paramref name="presentMask"/> leaves now: eliminated (all stocks gone, Out, holds released) on this exact tick;
        /// the match end is resolved on the next running game step like any elimination (a leaver gets a loss). Ignored in
        /// training and once the match is over.
        /// </summary>
        static void ApplyPresence(ChibiSimState s, int presentMask)
        {
            if (s.MatchOver || s.Training) return;
            int gone = s.PresentMask & ~presentMask;
            if (gone == 0) return;
            for (int i = 0; i < s.PlayerCount; i++)
                if ((gone & (1 << i)) != 0) LeaveMatch(s, i);
            s.PresentMask &= ~gone;
        }

        /// <summary>A player left: all stocks gone and Out (not a KO: KosTaken is not counted).</summary>
        static void LeaveMatch(ChibiSimState s, int i)
        {
            GoOut(s, i);
            s.F[i].Stocks = 0;
        }

        /// <summary>The fighter a box-less grab / kick program hit goes to: 1v1 the opponent, else <see cref="ChibiFighterSim.ProgTarget"/>.</summary>
        static int ProgVictim(ChibiSimState s, int i) => s.PlayerCount <= 2 ? 1 - i : s.F[i].ProgTarget;

        /// <summary>Copies the config's player fields into the state and clears the results (Init).</summary>
        static void InitPlayers(ChibiSimState s, in ChibiMatchConfig cfg)
        {
            s.PlayerCount = cfg.Players;
            s.Mode = cfg.Mode;
            s.TeamOf = cfg.TeamOf & ((1 << ChibiSimState.MaxFighters) - 1);
            s.WinnerMask = 0;
            s.PresentMask = (1 << s.PlayerCount) - 1;
            for (int i = 0; i < ChibiSimState.MaxFighters; i++) s.Place[i] = 0;
        }

        /// <summary>1v1 results in the four-player fields (WinnerMask, Place), from Winner.</summary>
        static void SetLegacyResults(ChibiSimState s)
        {
            s.WinnerMask = s.MatchOver && s.Winner >= 0 ? 1 << s.Winner : 0;
            s.Place[0] = s.Winner == 1 ? 2 : 1;
            s.Place[1] = s.Winner == 0 ? 2 : 1;
        }

        /// <summary>
        /// Match end for 3..4 fighters (section 1.4), after this step's KOs: newly eliminated fighters share the place
        /// "players left + 1"; FreeForAll ends with at most one fighter left, Team with at most one team left; then the
        /// timer. Winner = the single winner (FFA) or the lowest index of the winning team; -1 on a draw.
        /// </summary>
        static void ResolveMultiMatchEnd(ChibiSimState s)
        {
            int n = s.PlayerCount;
            int left = 0, newlyOut = 0;
            for (int i = 0; i < n; i++)
            {
                if (s.Place[i] != 0) continue;
                if (s.F[i].Stocks <= 0) newlyOut |= 1 << i; else left++;
            }
            for (int i = 0; i < n; i++) if ((newlyOut & (1 << i)) != 0) s.Place[i] = left + 1;

            int aliveMask = 0;
            for (int i = 0; i < n; i++) if (s.Place[i] == 0) aliveMask |= 1 << i;
            if (s.Mode == ChibiMatchMode.Team)
            {
                int teams = 0;
                for (int i = 0; i < n; i++) if ((aliveMask & (1 << i)) != 0) teams |= 1 << ((s.TeamOf >> i) & 1);
                if (teams == 1 || teams == 2) { EndTeam(s, teams == 2 ? 1 : 0); return; }
                if (teams == 0) { EndDraw(s); return; }
            }
            else
            {
                if (aliveMask == 0) { EndDraw(s); return; }
                if ((aliveMask & (aliveMask - 1)) == 0) { EndSingle(s, aliveMask); return; }
            }

            s.MatchClock++;
            if (s.MatchClock < ChibiSimConst.MatchTicks) return;
            if (s.Mode == ChibiMatchMode.Team)
            {
                long st0 = 0, st1 = 0, pc0 = 0, pc1 = 0;
                for (int i = 0; i < n; i++)
                {
                    if ((aliveMask & (1 << i)) == 0) continue;   // eliminated members: 0 stocks, frozen percent not counted
                    bool t1 = ((s.TeamOf >> i) & 1) != 0;
                    if (t1) { st1 += s.F[i].Stocks; pc1 += s.F[i].PctM; } else { st0 += s.F[i].Stocks; pc0 += s.F[i].PctM; }
                }
                if (st0 != st1) EndTeam(s, st0 > st1 ? 0 : 1);
                else if (pc0 != pc1) EndTeam(s, pc0 < pc1 ? 0 : 1);
                else EndDraw(s);
                return;
            }
            // FFA timeout: rank the fighters still in by stocks, then percent; tied leaders = draw (they share place 1).
            for (int i = 0; i < n; i++)
            {
                if ((aliveMask & (1 << i)) == 0) continue;
                int better = 0;
                for (int j = 0; j < n; j++)
                    if (j != i && (aliveMask & (1 << j)) != 0 && RanksAbove(s.F[j], s.F[i])) better++;
                s.Place[i] = better + 1;
            }
            int firsts = 0;
            for (int i = 0; i < n; i++) if (s.Place[i] == 1) firsts |= 1 << i;
            s.MatchOver = true;
            if ((firsts & (firsts - 1)) == 0) { s.WinnerMask = firsts; s.Winner = LowestBit(firsts); }
            else { s.WinnerMask = 0; s.Winner = -1; }
        }

        static bool RanksAbove(ChibiFighterSim a, ChibiFighterSim b) =>
            a.Stocks != b.Stocks ? a.Stocks > b.Stocks : a.PctM < b.PctM;

        static int LowestBit(int mask)
        {
            for (int i = 0; i < ChibiSimState.MaxFighters; i++) if ((mask & (1 << i)) != 0) return i;
            return -1;
        }

        static void EndSingle(ChibiSimState s, int aliveMask)
        {
            int w = LowestBit(aliveMask);
            s.Place[w] = 1;
            s.MatchOver = true; s.Winner = w; s.WinnerMask = aliveMask;
        }

        /// <summary>
        /// Draw. FFA: the last fighters out on the same step already got place left + 1 = 1, so they share first. Team:
        /// both teams share first, every member (also the ones eliminated earlier).
        /// </summary>
        static void EndDraw(ChibiSimState s)
        {
            if (s.Mode == ChibiMatchMode.Team)
                for (int i = 0; i < s.PlayerCount; i++) s.Place[i] = 1;
            s.MatchOver = true; s.Winner = -1; s.WinnerMask = 0;
        }

        static void EndTeam(ChibiSimState s, int team)
        {
            int mask = 0;
            for (int i = 0; i < s.PlayerCount; i++)
            {
                bool mine = ((s.TeamOf >> i) & 1) == team;
                if (mine) mask |= 1 << i;
                s.Place[i] = mine ? 1 : 2;
            }
            s.MatchOver = true; s.WinnerMask = mask; s.Winner = LowestBit(mask);
        }
    }
}
