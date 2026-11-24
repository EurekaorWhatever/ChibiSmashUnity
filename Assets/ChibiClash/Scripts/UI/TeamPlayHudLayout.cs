using ChibiClash.Netcode;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Pure layout and ordering rules for the 3-4 fighter HUD and results (TP-6). The 1v1 HUD keeps its own constants
    /// in Hud; nothing here is used for a 2-fighter match. No allocation: callers own every array.
    /// </summary>
    public static class TeamPlayHudLayout
    {
        /// <summary>Preferred plate size and the gap between neighbouring plates (and between a plate and the timer).</summary>
        public const float PlateW = 290f, PlateH = 110f, PlateGap = 12f;
        /// <summary>Smallest plate width before the layout stops shrinking (a 4:3 virtual screen still fits).</summary>
        public const float MinPlateW = 180f;

        /// <summary>
        /// Plate width for a virtual screen <paramref name="screenW"/> wide: <see cref="PlateW"/> when two plates per
        /// side fit beside the centred timer, else shrunk to fit. At 1280 (16:9) this is 269, not 290: four 290-wide
        /// plates plus the 124-wide timer need 1284 px before margins.
        /// </summary>
        public static float PlateWidth(float screenW, float inset, float timerW)
        {
            float side = screenW * 0.5f - timerW * 0.5f - PlateGap - inset;   // room on one side of the timer
            float w = (side - PlateGap) * 0.5f;
            return Mathf.Clamp(w, MinPlateW, PlateW);
        }

        /// <summary>
        /// Plate rect of fighter <paramref name="slot"/> in a <paramref name="count"/>-fighter match (3 or 4). Slots 0,1
        /// sit left of the timer (0 outermost), slots 2,3 right of it reading on (3 outermost); with 3 fighters slot 2
        /// takes the outer right place. In 2v2 that puts the red team left and the blue team right.
        /// </summary>
        /// <example><code>Rect r = TeamPlayHudLayout.Plate(i, State.Count, Gui.W, 16f, 14f, 124f);</code></example>
        public static Rect Plate(int slot, int count, float screenW, float inset, float top, float timerW)
        {
            float w = PlateWidth(screenW, inset, timerW);
            float pitch = w + PlateGap;
            if (slot < 2) return new Rect(inset + slot * pitch, top, w, PlateH);
            int k = count <= 3 ? 0 : (slot == 3 ? 0 : 1);   // k = 0 is the outer right place
            return new Rect(screenW - inset - w - k * pitch, top, w, PlateH);
        }

        /// <summary>Width of the 3-chip stock row at full size (3 x (24 + 5)); more than 3 stocks is not a TP-6 default.</summary>
        public const float ChipRowW = 87f;

        /// <summary>
        /// Mini plate layout for a plate <paramref name="plateW"/> wide (180..290): portrait size, percent block width,
        /// stock-chip scale and the width the RESPAWN / DROP IN text may use, so the chip row (from x = portrait + 20)
        /// always ends left of the percent block with a 4 px gap.
        /// </summary>
        public static void MiniLayout(float plateW, out float portrait, out float pctW, out float chipScale, out float statusW)
        {
            portrait = plateW < 250f ? 40f : 62f;
            pctW = Mathf.Min(100f, plateW * 0.36f);
            float avail = plateW - 6f - pctW - (portrait + 20f) - 4f;
            chipScale = Mathf.Clamp(avail / ChipRowW, 0.3f, 1f);
            statusW = Mathf.Clamp(avail, 40f, 80f);
        }

        /// <summary>
        /// Fills <paramref name="order"/> with fighter slots 0..count-1 sorted by <paramref name="placement"/>
        /// (1 = best); a placement of 0 or less (unset) ranks after every placed fighter. Ties keep slot order
        /// (insertion sort, stable, no allocation). Returns count.
        /// </summary>
        /// <example><code>int n = TeamPlayHudLayout.RankByPlacement(State.Placement, State.Count, rankOrder);</code></example>
        public static int RankByPlacement(int[] placement, int count, int[] order)
        {
            for (int i = 0; i < count; i++)
            {
                int key = Key(placement[i]);
                int j = i - 1;
                while (j >= 0 && Key(placement[order[j]]) > key) { order[j + 1] = order[j]; j--; }
                order[j + 1] = i;
            }
            return count;
        }

        static int Key(int p) => p > 0 ? p : int.MaxValue;

        /// <summary>
        /// Short, culprit-free reason shown under an online "NO CONTEST" (TP-6: never names who caused it).
        /// Returns "" for causes that are not a no-contest.
        /// </summary>
        public static string NoContestReason(ChibiEndCause cause)
        {
            switch (cause)
            {
                case ChibiEndCause.Desync: return "The games fell out of sync.";
                case ChibiEndCause.DropVoteFailed: return "A player dropped and the vote to continue failed.";
                case ChibiEndCause.ProtocolViolation: return "A player's game sent invalid data.";
                case ChibiEndCause.PeerNeverStarted: return "A player left before the match started.";
                default: return "";
            }
        }
    }
}
