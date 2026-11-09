using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>The local player's end-of-match numbers that rewards and quests read (from the deterministic sim + local posture).</summary>
    public struct MatchStatsForRewards
    {
        /// <summary>The local player won (a draw is neither a win nor a loss).</summary>
        public bool Won, Lost;
        /// <summary>Opponent was the CPU (PvE).</summary>
        public bool Pve;
        /// <summary>Training matches give nothing (MC:198).</summary>
        public bool Training;
        public int CombosLanded, UltsUsed, Parries;
        /// <summary>Fraction 0..1 of match time with good posture (GestureTracker.PostureAverage; 1 when never sampled).</summary>
        public float PostureAverage;
    }

    /// <summary>
    /// Match rewards, a straight port of MatchController.AwardRewards (MC:195-225, GDD 4.3): match coins (PvE 50/15,
    /// PvP 40/15), posture coins (posture x 30), quest progress (win, pve, posture &gt;= 0.8, combo, ult, parry) and
    /// profile stats. Pure apart from writing <paramref name="save"/>; the caller persists it.
    /// </summary>
    public static class MatchRewards
    {
        /// <summary>Apply the rewards to <paramref name="save"/>, append the lines to <paramref name="log"/>, return the coins earned.</summary>
        /// <example><code>int coins = MatchRewards.Award(gm.Save, stats, rewardLog); gm.Save.Save();</code></example>
        public static int Award(SaveData save, in MatchStatsForRewards m, List<string> log)
        {
            if (m.Training) return 0;
            int before = save.coins;

            int matchCoins = m.Pve ? (m.Won ? 50 : 15) : (m.Won ? 40 : 15);
            log.Add((m.Pve ? (m.Won ? "PvE victory" : "PvE match") : m.Won ? "Victory" : "Match played") + "  +" + matchCoins);
            save.coins += matchCoins;

            int postureCoins = Mathf.RoundToInt(m.PostureAverage * 30f);
            log.Add("Good posture " + Mathf.RoundToInt(m.PostureAverage * 100f) + "%  +" + postureCoins);
            save.coins += postureCoins;

            if (m.Won) save.Progress("win", 1, log);
            if (m.Won && m.Pve) save.Progress("pve", 1, log);
            if (m.PostureAverage >= 0.8f) save.Progress("posture", 1, log);
            save.Progress("combo", m.CombosLanded, log);
            save.Progress("ult", m.UltsUsed, log);
            save.Progress("parry", m.Parries, log);

            save.matches++;
            if (m.Won) save.wins++;
            else if (m.Lost) save.losses++;
            return save.coins - before;
        }
    }
}
