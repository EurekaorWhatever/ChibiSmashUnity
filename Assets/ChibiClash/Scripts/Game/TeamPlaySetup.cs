using ChibiClash.Netcode;

namespace ChibiClash
{
    /// <summary>Who plays a slot on the team-play character select (TP-6).</summary>
    public enum SlotMode
    {
        /// <summary>A local player (keyboard / gamepad of that slot).</summary>
        Human = 0,
        /// <summary>A computer fighter with its own difficulty.</summary>
        Cpu = 1,
        /// <summary>No fighter (FFA only, at most one slot, so an FFA keeps 3 or 4 fighters).</summary>
        Off = 2,
    }

    /// <summary>
    /// Pure rules behind the format picker and the team-play select screen (TP-6). No Unity state, no allocation on
    /// the per-frame paths: callers pass in and own every array.
    /// </summary>
    public static class TeamPlaySetup
    {
        /// <summary>Order the format control cycles through (and draws left to right): 1v1, 2v2, FFA.</summary>
        public static readonly int[] CycleOrder = { ChibiMatchConfig.Format1v1, ChibiMatchConfig.Format2v2, ChibiMatchConfig.FormatFfa };

        /// <summary>Short labels in <see cref="CycleOrder"/> order.</summary>
        public static readonly string[] CycleLabels = { "1V1", "2V2", "FFA" };

        /// <summary><paramref name="format"/> if it is a known format id, else 1v1 (edited saves, old builds).</summary>
        /// <example><code>s.lastFormatOnline = TeamPlaySetup.ValidFormat(s.lastFormatOnline);</code></example>
        public static int ValidFormat(int format) =>
            format == ChibiMatchConfig.FormatFfa || format == ChibiMatchConfig.Format2v2 ? format : ChibiMatchConfig.Format1v1;

        /// <summary>Index of <paramref name="format"/> in <see cref="CycleOrder"/> (unknown = 0, 1v1).</summary>
        public static int CycleIndex(int format)
        {
            format = ValidFormat(format);
            for (int i = 0; i < CycleOrder.Length; i++) if (CycleOrder[i] == format) return i;
            return 0;
        }

        /// <summary>The next format in <see cref="CycleOrder"/> for a step of <paramref name="dir"/> (-1 / +1), wrapping.</summary>
        /// <example><code>format = TeamPlaySetup.CycleFormat(format, MenuInput.PadRight ? 1 : -1);</code></example>
        public static int CycleFormat(int format, int dir)
        {
            int n = CycleOrder.Length;
            int i = (CycleIndex(format) + (dir < 0 ? -1 : dir > 0 ? 1 : 0)) % n;
            return CycleOrder[i < 0 ? i + n : i];
        }

        /// <summary>Select-screen columns for a format: 2 for 1v1, 4 for 2v2 and FFA.</summary>
        public static int SlotsFor(int format) => ValidFormat(format) == ChibiMatchConfig.Format1v1 ? 2 : 4;

        /// <summary>Fixed 2v2 team of a select slot: slots 0,1 = team 0 (red), 2,3 = team 1 (blue), as the sim assumes.</summary>
        public static int TeamOfSlot(int slot) => slot < 2 ? 0 : 1;

        /// <summary>
        /// Select-screen identity (colour and "P#" tag) of sim slot <paramref name="simSlot"/>: the select slot it came from
        /// (<paramref name="inputOf"/>, written from <see cref="Build"/>'s outSource), so an FFA with slot 2 Off still shows
        /// the player who picked as P4 in green. Online, and whenever no remap is known, the sim slot itself.
        /// </summary>
        /// <example><code>int who = TeamPlaySetup.DisplaySlot(m.Online, m.Config.inputOf, i);</code></example>
        public static int DisplaySlot(bool online, int[] inputOf, int simSlot)
        {
            if (online || inputOf == null || simSlot < 0 || simSlot >= inputOf.Length) return simSlot;
            int v = inputOf[simSlot];
            return v >= 0 && v < 4 ? v : simSlot;
        }

        /// <summary>
        /// Whether select slot <paramref name="slot"/> can be a human with <paramref name="padCount"/> gamepads connected:
        /// P1 and P2 always (keyboard halves), P3 needs a 3rd pad, P4 a 4th.
        /// </summary>
        public static bool HasDevice(int slot, int padCount) => slot <= 1 || padCount > slot;

        /// <summary>Number of slots set to <see cref="SlotMode.Off"/> among the first <paramref name="n"/>.</summary>
        public static int OffCount(SlotMode[] modes, int n)
        {
            int off = 0;
            for (int i = 0; i < n && i < modes.Length; i++) if (modes[i] == SlotMode.Off) off++;
            return off;
        }

        /// <summary>
        /// Whether <paramref name="slot"/> may be switched Off: FFA only, never slot 0 (player 1 hosts the screen),
        /// and only while no other slot is Off (an FFA keeps at least 3 fighters).
        /// </summary>
        public static bool CanSetOff(int format, SlotMode[] modes, int slot)
        {
            if (ValidFormat(format) != ChibiMatchConfig.FormatFfa || slot <= 0 || slot >= 4) return false;
            if (modes[slot] == SlotMode.Off) return true;
            return OffCount(modes, 4) == 0;
        }

        /// <summary>
        /// Next mode for a slot cycled with A / click: Human → CPU → Off → Human, skipping Off when
        /// <see cref="CanSetOff"/> says no.
        /// </summary>
        public static SlotMode NextMode(int format, SlotMode[] modes, int slot)
        {
            switch (modes[slot])
            {
                case SlotMode.Human: return SlotMode.Cpu;
                case SlotMode.Cpu: return CanSetOff(format, modes, slot) ? SlotMode.Off : SlotMode.Human;
                default: return SlotMode.Human;
            }
        }

        /// <summary>
        /// Turns select slots into a match: the fighting slots in order, compacted (an FFA with slot 2 Off plays
        /// slots 0,1,3 as sim slots 0,1,2). Off counts only in FFA and only once (a second Off plays as CPU), so an
        /// FFA is 3 or 4 fighters, a 2v2 always 4, a 1v1 always 2. Writes <paramref name="outClasses"/>,
        /// <paramref name="outLevels"/> (CPU difficulty, 0 for humans) and <paramref name="outSource"/> (select slot
        /// each sim slot came from, for device mapping); each needs length 4. Returns the fighter count.
        /// </summary>
        /// <example><code>int n = TeamPlaySetup.Build(fmt, modes, picks, levels, cls, lv, src, out int mask);</code></example>
        public static int Build(int format, SlotMode[] modes, FighterClass[] slotClasses, int[] slotLevels,
                                FighterClass[] outClasses, int[] outLevels, int[] outSource, out int humanMask)
        {
            format = ValidFormat(format);
            int slots = SlotsFor(format);
            bool ffa = format == ChibiMatchConfig.FormatFfa;
            humanMask = 0;
            int n = 0;
            bool offUsed = false;
            for (int s = 0; s < slots; s++)
            {
                SlotMode m = modes[s];
                if (m == SlotMode.Off)
                {
                    if (ffa && s > 0 && !offUsed) { offUsed = true; continue; }
                    m = SlotMode.Cpu;
                }
                outClasses[n] = slotClasses[s];
                outSource[n] = s;
                if (m == SlotMode.Human) { humanMask |= 1 << n; outLevels[n] = 0; }
                else outLevels[n] = slotLevels[s] >= 1 && slotLevels[s] <= 3 ? slotLevels[s] : 2;
                n++;
            }
            for (int i = n; i < outSource.Length; i++) outSource[i] = -1;
            return n;
        }
    }

    /// <summary>Who earns match rewards (TP-6 fix: all-CPU / spectator matches must never touch the profile).</summary>
    public static class MatchRewardRules
    {
        /// <summary>
        /// Sim slot whose stats earn rewards for the profile owner, or -1 for none. None in training, in automation runs
        /// (<paramref name="debugNoRewards"/>), and offline when no human slot is driven by local player 1's input
        /// (<paramref name="inputOf"/>: the remapped input player per sim slot; null = identity). Online: the local slot.
        /// </summary>
        /// <example><code>int slot = MatchRewardRules.RewardSlot(false, 0, false, false, cfg.cpu, cfg.inputOf, 4);</code></example>
        public static int RewardSlot(bool online, int localPlayer, bool training, bool debugNoRewards, bool[] cpu, int[] inputOf, int count)
        {
            if (training || debugNoRewards) return -1;
            if (online) return localPlayer;
            for (int i = 0; i < count && i < cpu.Length; i++)
            {
                int input = inputOf != null && i < inputOf.Length && inputOf[i] >= 0 ? inputOf[i] : i;
                if (!cpu[i] && input == 0) return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// State of the online format modal shown before matchmaking (TP-6). Pure: the caller starts the search on
    /// <see cref="Confirm"/>. <see cref="Back"/> only closes the modal; it can never cancel a search, and the modal will
    /// not open while one runs (<see cref="Show"/> with busy = true), so B in the modal never reaches a live search.
    /// </summary>
    public sealed class FormatModalState
    {
        /// <summary>The modal is on screen.</summary>
        public bool Open { get; private set; }
        /// <summary>Format the control shows (ChibiMatchConfig.Format*).</summary>
        public int Format { get; private set; }
        /// <summary>Index of <see cref="Format"/> in <see cref="TeamPlaySetup.CycleOrder"/> (what the segmented row draws).</summary>
        public int Index => TeamPlaySetup.CycleIndex(Format);

        /// <summary>Open on the remembered format; returns false (stays closed) while a search or match is already busy.</summary>
        /// <example><code>if (!modal.Show(Save.settings.lastFormatOnline, OnlineBusy)) OpenLobby(false);</code></example>
        public bool Show(int remembered, bool busy)
        {
            if (busy) { Open = false; return false; }
            Format = TeamPlaySetup.ValidFormat(remembered);
            Open = true;
            return true;
        }

        /// <summary>Cycle the shown format (pad left / right, LB / RB).</summary>
        public void Step(int dir) { if (Open) Format = TeamPlaySetup.CycleFormat(Format, dir); }

        /// <summary>Set the shown format from a segmented-row index.</summary>
        public void SetIndex(int i) { if (Open && i >= 0 && i < TeamPlaySetup.CycleOrder.Length) Format = TeamPlaySetup.CycleOrder[i]; }

        /// <summary>Close and hand back the chosen format (A / FIND MATCH). False if the modal was not open.</summary>
        public bool Confirm(out int format)
        {
            format = Format;
            if (!Open) return false;
            Open = false;
            return true;
        }

        /// <summary>B / Esc / CANCEL: close the modal. Never touches matchmaking.</summary>
        public void Back() => Open = false;

        /// <summary>
        /// The modal belongs to the ONLINE page only: close it (without confirming) whenever the current page is not
        /// ONLINE. Called on every page change and before every draw, so it can never leak onto another screen.
        /// </summary>
        /// <example><code>formatModal.KeepOnlyOn(page == Page.Online);</code></example>
        public void KeepOnlyOn(bool onOnlinePage) { if (!onOnlinePage) Open = false; }
    }
}
