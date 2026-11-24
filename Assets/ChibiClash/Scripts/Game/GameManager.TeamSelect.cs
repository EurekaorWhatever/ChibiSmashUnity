using ChibiClash.Netcode;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// TP-6 phase 3: the offline format strip and the 4-column character select for 2v2 and FFA (VS CPU and Local
    /// versus). The 1v1 select (GameManager.Select.cs) is unchanged apart from the format strip in its top-right corner.
    /// Slot rules live in <see cref="TeamPlaySetup"/>; this file only draws and routes input.
    /// Input: P1 (WASD / gamepad 1) drives a spatial focus over every widget (Gui.Navigate), LB / RB or Q / E cycle the
    /// format, Esc / B leaves. P2 (arrows / gamepad 2), P3 (gamepad 3) and P4 (gamepad 4) join their slot as a human
    /// with confirm, change their own fighter with left / right and drop back to CPU with back.
    /// </summary>
    public partial class GameManager
    {
        /// <summary>Format shown on the select screen (ChibiMatchConfig.Format*); 1v1 uses the classic layout.</summary>
        int selectFormat = ChibiMatchConfig.Format1v1;
        readonly SlotMode[] teamMode = new SlotMode[4];
        readonly int[] teamLevel = new int[4];
        readonly FighterClass[] teamCls = new FighterClass[4];
        // Build scratch (no per-click allocation).
        readonly FighterClass[] buildCls = new FighterClass[4];
        readonly int[] buildLevel = new int[4], buildSource = new int[4];

        static readonly string[] SlotTags = { "P1", "P2", "P3", "P4" };
        static readonly string[] TeamTags = { "RED TEAM", "BLUE TEAM" };
        static readonly string[] JoinHints = { "", "PRESS ENTER OR A ON GAMEPAD 2 TO JOIN", "PRESS A ON GAMEPAD 3 TO JOIN", "PRESS A ON GAMEPAD 4 TO JOIN" };
        static readonly string[] ModeLabels = { "HUMAN", "CPU", "OFF" };
        static readonly string[] NoPadHints = { "", "", "NO PAD  ·  CONNECT GAMEPAD 3 TO PLAY", "NO PAD  ·  CONNECT GAMEPAD 4 TO PLAY" };

        bool TeamSelect => selectFormat != ChibiMatchConfig.Format1v1;
        /// <summary>The format can be changed here: VS CPU and Local versus only (not training or arcade).</summary>
        bool FormatPickable => !arcade && !pending.training;

        /// <summary>Called from SetupSelect: restore the remembered offline format and default the four slots.</summary>
        void SetupTeamSelect(bool cpu)
        {
            selectFormat = FormatPickable ? TeamPlaySetup.ValidFormat(Save.settings.lastFormatOffline) : ChibiMatchConfig.Format1v1;
            for (int s = 0; s < 4; s++)
            {
                teamMode[s] = s == 0 || (s == 1 && !cpu) ? SlotMode.Human : SlotMode.Cpu;
                teamLevel[s] = Mathf.Clamp(Save.settings.cpuDifficulty, 1, 3);
                teamCls[s] = pending.cls[s];
            }
        }

        /// <summary>Switch the select screen's format and remember it (Settings.lastFormatOffline).</summary>
        void SetOfflineFormat(int format)
        {
            format = TeamPlaySetup.ValidFormat(format);
            if (!FormatPickable || format == selectFormat) return;
            int was = selectFormat;
            selectFormat = format;
            if (format != ChibiMatchConfig.FormatFfa)
                for (int s = 0; s < 4; s++) if (teamMode[s] == SlotMode.Off) teamMode[s] = SlotMode.Cpu;   // Off is FFA-only
            if (Save.settings.lastFormatOffline != format) { Save.settings.lastFormatOffline = format; Save.Save(); }
            if (format == ChibiMatchConfig.Format1v1)
            {
                pending.cls[0] = teamCls[0]; pending.cls[1] = teamCls[1];   // picks carry over to the 1v1 select
                EnterSelect();
            }
            else if (was == ChibiMatchConfig.Format1v1) { teamCls[0] = pending.cls[0]; teamCls[1] = pending.cls[1]; }
            Gui.ResetFocus();
        }

        /// <summary>Format strip (top-right of both select layouts). Returns true when the format changed.</summary>
        void FormatStrip()
        {
            if (!FormatPickable) return;
            var r = new Rect(W - 16f - 252f, 12f, 252f, 32f);
            int i = Gui.Segmented(r, TeamPlaySetup.CycleIndex(selectFormat), TeamPlaySetup.CycleLabels, Gui.Gold, out _);
            if (TeamPlaySetup.CycleOrder[i] != selectFormat) SetOfflineFormat(TeamPlaySetup.CycleOrder[i]);
            Gui.Text(new Rect(r.x - 70f, r.y, 64f, r.height), MenuInput.UsingGamepad ? "LB / RB" : "Q / E", 11, Gui.Muted, TextAnchor.MiddleRight, true, false);
        }

        /// <summary>LB / RB (Q / E) on P1 cycles the format on either layout. Returns true if it fired.</summary>
        bool FormatCycleInput()
        {
            bool solo = !TeamSelect && Solo;
            int d = MenuInput.Player(0, NavCmd.PrevTab, solo) ? -1 : MenuInput.Player(0, NavCmd.NextTab, solo) ? 1 : 0;
            if (d == 0) return false;
            if (TeamSelect && Gui.FocusClaimsHorizontal) { Gui.StepFocused(d); return true; }   // focused row (stage / strip) takes LB / RB
            if (!FormatPickable) return false;
            UiSound.Navigate();
            SetOfflineFormat(TeamPlaySetup.CycleFormat(selectFormat, d));
            return true;
        }

        // ---------------------------------------------------------------- input

        /// <summary>MenuInput source of each slot's own pad / keys for joining: P2 = keys 2 + pad 2, P3 = pad 3, P4 = pad 4.</summary>
        static bool SlotCmd(int s, NavCmd c)
        {
            switch (s)
            {
                case 1: return MenuInput.Source(1, c) || MenuInput.Source(3, c);
                case 2: return MenuInput.Source(4, c);
                case 3: return MenuInput.Source(5, c);
                default: return false;
            }
        }

        void TeamSelectInput()
        {
            // P1: spatial focus over the widgets (keys 1 + gamepad 1).
            if (MenuInput.Player(0, NavCmd.Up, false)) Gui.Navigate(0, -1);
            if (MenuInput.Player(0, NavCmd.Down, false)) Gui.Navigate(0, 1);
            bool lr = Gui.FocusClaimsHorizontal;
            if (MenuInput.Player(0, NavCmd.Left, false)) { if (lr) Gui.StepFocused(-1); else Gui.Navigate(-1, 0); }
            if (MenuInput.Player(0, NavCmd.Right, false)) { if (lr) Gui.StepFocused(1); else Gui.Navigate(1, 0); }
            if (MenuInput.Player(0, NavCmd.Confirm, false)) Gui.Activate();
            if (MenuInput.Player(0, NavCmd.Back, false)) { LeaveSelect(); return; }
            if (MenuInput.Player(0, NavCmd.Alt, false)) ToggleHazards();

            // P2..P4: join, pick their own fighter, leave. A pad that disconnects drops its slot back to CPU.
            int pads = UnityEngine.InputSystem.Gamepad.all.Count;
            for (int s = 1; s < 4; s++)
            {
                if (!TeamPlaySetup.HasDevice(s, pads))
                {
                    if (teamMode[s] == SlotMode.Human) teamMode[s] = SlotMode.Cpu;
                    continue;
                }
                if (teamMode[s] != SlotMode.Human)
                {
                    if (SlotCmd(s, NavCmd.Confirm)) { teamMode[s] = SlotMode.Human; UiSound.LockIn(); }
                    continue;
                }
                if (SlotCmd(s, NavCmd.Left)) StepTeamClass(s, -1);
                if (SlotCmd(s, NavCmd.Right)) StepTeamClass(s, 1);
                if (SlotCmd(s, NavCmd.Alt)) { teamCls[s] = (FighterClass)Random.Range(0, CharacterRoster.All.Length); UiSound.Step(); }
                if (SlotCmd(s, NavCmd.Back)) { teamMode[s] = SlotMode.Cpu; UiSound.Back(); }
            }
        }

        void StepTeamClass(int s, int d)
        {
            teamCls[s] = (FighterClass)Mod((int)teamCls[s] + d, CharacterRoster.All.Length);
            UiSound.Navigate();
        }

        void SetTeamMode(int s, SlotMode m)
        {
            if (s == 0 || teamMode[s] == m) return;
            if (m == SlotMode.Human && !TeamPlaySetup.HasDevice(s, UnityEngine.InputSystem.Gamepad.all.Count)) { UiSound.Error(); return; }
            if (m == SlotMode.Off && !TeamPlaySetup.CanSetOff(selectFormat, teamMode, s)) { UiSound.Error(); return; }
            teamMode[s] = m;
            UiSound.Step();
        }

        /// <summary>FIGHT on the 4-column select: build the slots and start through the shared team-match entry.</summary>
        void StartTeamFromSelect()
        {
            if (wipeStart >= 0f) return;
            int fmt = selectFormat;
            int pads = UnityEngine.InputSystem.Gamepad.all.Count;
            for (int s = 1; s < 4; s++)   // a pad unplugged since joining: that slot plays as CPU
                if (teamMode[s] == SlotMode.Human && !TeamPlaySetup.HasDevice(s, pads)) teamMode[s] = SlotMode.Cpu;
            int n = TeamPlaySetup.Build(fmt, teamMode, teamCls, teamLevel, buildCls, buildLevel, buildSource, out int mask);
            UiSound.LockIn();
            Wipe(null, () => LaunchTeamMatch(fmt, buildCls, n, mask, buildLevel, buildSource, pending.stage));
        }

        // ---------------------------------------------------------------- drawing

        void TeamSelectView()
        {
            float t = Time.unscaledTime;
            var full = new Rect(0f, 0f, W, H);
            Gui.Rect(full, Gui.Navy);
            Gui.Hatch(full, new Color(1f, 1f, 1f, 0.018f), 22f, t * 0.04f);
            Gui.Grain(full, 0.05f);

            float cx = W * 0.5f;
            Gui.Display(new Rect(0f, 8f, W, 52f), "CHARACTER SELECT", 42, Gui.Paper, TextAnchor.MiddleCenter);
            var ml = ModeLabel();
            float mw = Gui.Measure(ml, 13) + 30f;
            var mr = new Rect(cx - mw * 0.5f, 58f, mw, 20f);
            Gui.Skew(mr, Gui.Steel, 0.3f);
            Gui.Text(mr, ml, 13, Gui.Paper, TextAnchor.MiddleCenter, true, false);
            if (Gui.Button(new Rect(16f, 12f, 112f, 32f), "BACK", Gui.Steel, 14)) LeaveSelect();
            FormatStrip();

            const float gap = 12f, top = 90f, colH = 290f;
            float colW = (W - 32f - 3f * gap) / 4f;
            for (int s = 0; s < 4; s++) TeamColumn(s, new Rect(16f + s * (colW + gap), top, colW, colH));

            StageCarousel(cx, 392f);
            // Pad: the carousel is one focusable row (left / right / LB / RB step the stage, A goes to FIGHT!).
            if (Gui.FocusRow(new Rect(cx - 240f, 416f, 480f, 104f), out _, out int sd, !arcade) && Gui.NavMode) Gui.FocusPreferred();
            if (sd != 0) StageStep(sd);
            Gui.PreferNext();
            if (Gui.Button(new Rect(cx - 150f, 586f, 300f, 54f), "FIGHT!", Gui.P1, 30)) StartTeamFromSelect();

            Gui.HintBar(ml, Gui.Key("WASD", "D-PAD"), "MOVE", Gui.Key("SPACE", "A"), "SELECT", Gui.Key("Q / E", "LB / RB"), "FORMAT / STAGE",
                        Gui.Key("ESC", "B"), "BACK");
        }

        void TeamColumn(int s, Rect r)
        {
            bool twoV = selectFormat == ChibiMatchConfig.Format2v2;
            int team = twoV ? TeamPlaySetup.TeamOfSlot(s) : s;
            Color c = Gui.SlotColor(selectFormat, team, s);
            SlotMode m = teamMode[s];
            bool off = m == SlotMode.Off;

            Gui.Box(r, c, off ? 0.55f : 0.93f);
            var head = new Rect(r.x, r.y, r.width, 34f);
            Gui.Rect(head, off ? Gui.Disabled : c);
            Gui.Text(new Rect(head.x + 10f, head.y, 40f, head.height), SlotTags[s], 18, Gui.Paper, TextAnchor.MiddleLeft);
            if (twoV) Gui.Text(new Rect(head.x, head.y, head.width - 10f, head.height), TeamTags[team], 13, Gui.Paper, TextAnchor.MiddleRight);

            // Who plays: P1 is always the human host; others HUMAN / CPU (/ OFF in FFA).
            float y = r.y + 44f, x = r.x + 10f, w = r.width - 20f;
            if (s == 0) Gui.Text(new Rect(x, y, w, 30f), "HUMAN  ·  1P", 14, Gui.Paper);
            else
            {
                int opts = selectFormat == ChibiMatchConfig.FormatFfa ? 3 : 2;
                float bw = (w - (opts - 1) * 6f) / opts;
                for (int k = 0; k < opts; k++)
                {
                    var mode = (SlotMode)k;
                    bool enabled = mode == SlotMode.Off ? TeamPlaySetup.CanSetOff(selectFormat, teamMode, s)
                                 : mode != SlotMode.Human || TeamPlaySetup.HasDevice(s, UnityEngine.InputSystem.Gamepad.all.Count);
                    if (Gui.Button(new Rect(x + k * (bw + 6f), y, bw, 30f), ModeLabels[k], c, 13, enabled, m == mode)) SetTeamMode(s, mode);
                }
            }
            y += 42f;

            if (off)
            {
                Gui.Text(new Rect(x, y + 40f, w, 40f), "NO FIGHTER", 20, Gui.Muted);
                return;
            }

            // Fighter: < NAME > (P1 and mouse use the arrows; joined pads use left / right on their own pad).
            if (Gui.Button(new Rect(x, y + 8f, 34f, 50f), "<", c, 20)) StepTeamClass(s, -1);
            if (Gui.Button(new Rect(x + w - 34f, y + 8f, 34f, 50f), ">", c, 20)) StepTeamClass(s, 1);
            var info = CharacterRoster.All[Mathf.Clamp((int)teamCls[s], 0, CharacterRoster.All.Length - 1)];
            Gui.Text(new Rect(x + 38f, y, w - 76f, 40f), info.Name, 22, Gui.Paper);
            Gui.Text(new Rect(x + 38f, y + 38f, w - 76f, 20f), info.Role, 11, Gui.Muted, TextAnchor.MiddleCenter, true, false);
            y += 72f;

            if (m == SlotMode.Cpu)
            {
                float bw = (w - 12f) / 3f;
                for (int lvl = 1; lvl <= 3; lvl++)
                    if (Gui.Button(new Rect(x + (lvl - 1) * (bw + 6f), y, bw, 30f), DifficultyUpper[lvl], Gui.P2, 12, true, teamLevel[s] == lvl))
                    { teamLevel[s] = lvl; UiSound.Step(); }
                y += 40f;
                if (s > 0)
                {
                    bool pad = TeamPlaySetup.HasDevice(s, UnityEngine.InputSystem.Gamepad.all.Count);
                    Gui.Text(new Rect(x, y, w, 30f), pad ? JoinHints[s] : NoPadHints[s], 10, pad ? Gui.Muted : Gui.Gold, TextAnchor.MiddleCenter, true, false);
                }
            }
            else if (s > 0)
                Gui.Text(new Rect(x, y, w, 40f), "JOINED  ·  LEFT / RIGHT = FIGHTER,  BACK = LEAVE", 10, Gui.Gold, TextAnchor.MiddleCenter, true, false);
        }
    }
}
