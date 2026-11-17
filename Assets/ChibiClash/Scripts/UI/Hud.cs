using System.Collections.Generic;
using ChibiClash.Netcode;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// In-match HUD (GDD 5): corner player panels, center timer, CV overlay and gesture prompts. Display only: it reads the
    /// deterministic simulation through <see cref="ChibiMatch"/> (port step 18) and returns the player's menu choice; it
    /// never changes game state. Presentation follows the copy-ghibli-smash fork's arcade style (UI-1): slanted panels,
    /// display type, character portraits and names from <see cref="CharacterRoster"/>.
    /// </summary>
    public static class Hud
    {
        public enum Action { None, Resume, Rematch, CharacterSelect, Menu, Forfeit }

        static readonly string[][] PoseKeys = { new[] { "1", "2", "3" }, new[] { "8", "9", "0" } };
        static readonly List<string> StatusScratch = new List<string>(8);
        static readonly string[] UpperNames = new string[5];
        static string cinematicSub = "";
        static float cinematicSubFor = -1f;
        static bool resultsFocus;

        /// <summary>Character display name ("Kaia") for a class: what players see instead of class names.</summary>
        public static string CharName(FighterClass c) => CharacterRoster.Of(c).Name;

        /// <summary>Upper-case character name, cached (no per-frame allocation).</summary>
        public static string CharNameUpper(FighterClass c)
        {
            int i = (int)c;
            return UpperNames[i] ?? (UpperNames[i] = CharacterRoster.Of(c).Name.ToUpperInvariant());
        }

        public static Action Draw(ChibiMatch m, GameManager gm)
        {
            var s = m.State;
            if (s == null) return Action.None;
            var settings = gm.Save.settings;
            Track(m);
            BuildPlates(m, settings.showCvOverlay);
            Tags(m);
            Popups();

            var v = m.View;
            if (v != null)
            {
                float ct = Time.unscaledTime - v.CinematicStart;
                if (v.CinematicFighter >= 0 && ct < 1.5f) Cinematic(m, v, ct);
            }

            bool motion = settings.screenShake;   // the player's motion preference also calms the HUD's shake
            PlayerPanel(m, 0, PanelRect(0), false, motion);
            PlayerPanel(m, 1, PanelRect(1), true, motion);
            Timer(m);
            if (m.Config.training) TrainingReadout(m);

            if (settings.showCvOverlay)
            {
                for (int i = 0; i < 2; i++)
                {
                    var tr = m.Tracker(i);
                    if (tr == null) continue;
                    Gui.CvPanel(CvRect(i), tr, StateColor(m, i), i == 0 && settings.useWebcam, CvTitles[i * 2 + (tr.IsLive ? 1 : 0)]);
                }
            }

            bool both = PromptVisible(s.F[0]) && PromptVisible(s.F[1]);
            for (int i = 0; i < 2; i++)
            {
                if (!PromptVisible(s.F[i])) continue;
                float x = both ? (i == 0 ? Gui.W * 0.5f - 330f : Gui.W * 0.5f + 30f) : Gui.W * 0.5f - 150f;
                GesturePrompt(m, i, new Rect(x, 175f, 300f, 230f));
            }

            Hints(m);

            if (s.IntroRealTicks > 0 && !s.MatchOver)
            {
                bool fight = s.IntroRealTicks <= ChibiSimConst.IntroFightTicks;   // last 0.8 s of the intro
                float pulse = 1f + Mathf.Sin(Time.unscaledTime * 10f) * 0.04f;
                var band = new Rect(-40f, Gui.H * 0.38f + 4f, Gui.W + 80f, 112f);
                Gui.Skew(band, new Color(Gui.Ink.r, Gui.Ink.g, Gui.Ink.b, 0.75f), 0f);
                Gui.Rect(new Rect(0f, band.y, Gui.W, 3f), fight ? Gui.P1 : Gui.P2);
                Gui.Rect(new Rect(0f, band.yMax - 3f, Gui.W, 3f), fight ? Gui.P2 : Gui.P1);
                Gui.Display(new Rect(0f, Gui.H * 0.38f, Gui.W, 120f), fight ? "FIGHT!" : "READY...", Mathf.RoundToInt((fight ? 104 : 80) * pulse),
                            fight ? Gui.P1 : Gui.Paper, TextAnchor.MiddleCenter, 0.2f, Gui.Ink, 6f);
                Gui.Text(new Rect(0f, Gui.H * 0.38f + 122f, Gui.W, 30f), introLine, 18, Gui.Gold);
            }

            if (v != null && v.FlashAlpha > 0.01f)
            {
                var c = v.FlashColor;
                Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(c.r, c.g, c.b, v.FlashAlpha * 0.5f));
            }

            FireWarnings(m);
            if (m.Paused) return Pause();
            if (m.Online && m.LeavePrompt && !m.Finished) return LeavePrompt();
            if (m.Finished && Time.unscaledTime - m.FinishedTime > 1.3f) return Results(m, gm);
            resultsFocus = false;
            return Action.None;
        }

        const string FireHere = "FIRE!", FireLeft = "<  FIRE!", FireRight = "FIRE!  >";
        static readonly Color FireWarn = new Color(1f, 0.28f, 0.08f, 1f);

        /// <summary>
        /// Volcano (review 2026-11-16 item 26): the fire lanes sit beyond the ledges and the stage's own telegraph flare is
        /// at the lava surface, both often off camera. During the telegraph and the eruption, each active lane gets a
        /// "FIRE!" badge at stage height: over the lane when it is on screen, pinned to the screen edge with an arrow when
        /// it is not. The telegraph badge pulses and carries a bar that empties as the eruption nears. View only.
        /// </summary>
        static void FireWarnings(ChibiMatch m)
        {
            var s = m.State;
            if (s == null || !s.HazardsOn || !s.Running) return;
            if (ChibiStageTable.Get(s.Stage).Hazard != ChibiStageTable.HazardFire) return;
            bool tele = s.HazPhase == ChibiHazardConst.Telegraph, erupt = s.HazPhase == ChibiHazardConst.PhaseFireErupt;
            if (!tele && !erupt) return;
            float pulse = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 9f));
            const float w = 150f, h = 42f, margin = 14f;
            for (int lane = 0; lane < 2; lane++)
            {
                if (!ChibiSimulation.FireLaneOn(s, lane)) continue;
                int lx = lane == 0 ? ChibiHazardConst.FireLaneLeftX : ChibiHazardConst.FireLaneRightX;
                var g = Gui.WorldToGui(new Vector3(lx / 1000f, 1.5f, 0f));   // stage height above the lane
                bool offLeft = g.x < margin, offRight = g.x > Gui.W - margin;
                float x = Mathf.Clamp(g.x - w * 0.5f, margin, Gui.W - w - margin);
                float y = Mathf.Clamp(g.y - h, Gui.H * 0.22f, Gui.H - 190f);
                var r = new Rect(x, y, w, h);
                Gui.Rect(r, new Color(FireWarn.r, FireWarn.g, FireWarn.b, tele ? 0.55f + 0.4f * pulse : 0.95f), 6f);
                Gui.Text(r, offLeft ? FireLeft : offRight ? FireRight : FireHere, 20, Gui.Paper);
                if (tele)
                {
                    float left = Mathf.Clamp01(s.HazTimer / (float)ChibiHazardConst.FireTelegraph);
                    Gui.Rect(new Rect(x + 6f, y + h - 6f, (w - 12f) * left, 3f), Gui.Paper);
                }
            }
        }

        static bool PromptVisible(ChibiFighterSim f) =>
            f.UltWindowTicks > 0 || f.UltPrompt == (int)ChibiUltPrompt.Detected || f.UltPrompt == (int)ChibiUltPrompt.Failed;

        static Color StateColor(ChibiMatch m, int i)
        {
            var f = m.State.F[i];
            if (f.UltWindowTicks > 0) return Gui.Yellow;
            if (f.UltPrompt == (int)ChibiUltPrompt.Detected) return Gui.Green;
            if (f.UltPrompt == (int)ChibiUltPrompt.Failed) return Gui.Red;
            var tr = m.Tracker(i);
            if (tr != null && tr.Stable != Gesture.None) return Gui.Accent2;
            return new Color(0.4f, 0.4f, 0.5f);
        }

        /// <summary>
        /// Label of fighter <paramref name="i"/>. Online it is the player's chosen name: short form ("Kira") in tight spots,
        /// full ("Kira#4821") when <paramref name="full"/>. Falls back to RIVAL / P1 when no name was sent.
        /// </summary>
        static string Who(ChibiMatch m, int i, bool full = false) => full ? whoFull[i] : whoShort[i];

        /// <summary>Builds <see cref="Who"/>'s strings (and everything that embeds the name) when a name changes.</summary>
        static void RefreshNames(ChibiMatch m, bool force)
        {
            for (int i = 0; i < 2; i++)
            {
                string n = m.NameOf(i);
                if (!force && ReferenceEquals(n, whoSrc[i])) continue;
                whoSrc[i] = n;
                n = n ?? "";
                if (m.IsCpu(i)) whoShort[i] = whoFull[i] = m.Config.training ? "DUMMY" : "CPU";
                else if (m.Online)
                {
                    whoFull[i] = n.Length > 0 ? n : i != m.LocalPlayer ? "RIVAL" : "YOU";
                    whoShort[i] = n.Length > 0 ? ChibiSocialRules.BaseName(n) : whoFull[i];
                }
                else whoShort[i] = whoFull[i] = n.Length > 0 ? n : i == 0 ? "P1" : "P2";
                promptHead[i * 3] = whoShort[i] + " · ULTIMATE READY";
                promptHead[i * 3 + 1] = whoShort[i] + " · MOVE DETECTED";
                promptHead[i * 3 + 2] = whoShort[i] + " · FAILED";
                trainTitle[i] = "TRAINING · " + whoShort[i];
            }
        }

        /// <summary>
        /// Match-start text (audit P4): intro line, stage label, panel subtitles, ULT READY labels, skill key prefixes and
        /// the control hints. Bindings cannot change mid-match (rebinding lives in Settings), so once per match is enough.
        /// </summary>
        static void BuildMatchText(ChibiMatch m)
        {
            int st = Mathf.Clamp(m.Config.stage, 0, StageBuilder.Names.Length - 1);
            string stage = StageBuilder.Names[st].ToUpperInvariant();
            stageUpper = Gui.Measure(stage, 10) <= 118f ? stage : StageBuilder.ShortNames[st].ToUpperInvariant();
            introLine = CharNameUpper(m.Def(0).type) + "  VS  " + CharNameUpper(m.Def(1).type) + "  ·  " + stage +
                        (m.Config.training ? "  ·  TRAINING" : "") + (m.Online ? "  ·  ONLINE  ·  ESC / START: LEAVE" : "");
            for (int i = 0; i < 2; i++)
            {
                var def = m.Def(i);
                var ch = CharacterRoster.Of(def.type);
                var f = m.State.F[i];
                bool showKeys = !m.IsCpu(i) && (!m.Online || i == m.LocalPlayer);
                int keyPlayer = m.Online ? 0 : i;
                subLine[i] = ch.Weapon + " · " + def.passiveNames[f.Passive];
                ultReady[i] = showKeys ? "ULT READY - press " + Bindings.Label(keyPlayer, BindAction.Ultimate) : "ULT READY";
                for (int s = 0; s < 3; s++)
                {
                    string key = showKeys ? Bindings.Label(keyPlayer, (BindAction)((int)BindAction.Skill1 + s)) + " " : "";
                    skillBase[i * 3 + s] = s < def.SkillCount ? key + def.skillNames[s] : "";
                    skillText[i * 3 + s] = skillBase[i * 3 + s];
                    skillKey[i * 3 + s] = 0;
                }
                simKey[i] = "sim key: " + PoseKeys[keyPlayer][Mathf.Clamp((int)def.ultGesture - 1, 0, 2)];
                promptKey[i] = -1;
                statusKey[i * StatusKeyLen] = int.MinValue;   // force the first status build
            }
            // Control hints: both camera states, built once (each KeyHint is ~12 Bindings.Label calls).
            for (int live = 0; live < 2; live++)
            {
                string h = KeyHint(0) + (live == 1 ? " · poses: cross arms block / arm out = fireball / hands up = jump"
                                                   : " · poses 1 cross / 2 punch / 3 raise · P slouch");
                if (!m.Online && !m.IsCpu(1)) h += "\n" + KeyHint(1) + " · poses 8/9/0";
                if (m.Config.training) h += "\nTRAINING: T fills your ultimate · KO'd fighters respawn at 0% · Esc pause";
                hintText[live] = h;
            }
            trainPctKey = trainLastKey = trainComboKey = trainKosKey = -1;
            resultsFor = null;
        }

        static void PlayerPanel(ChibiMatch m, int i, Rect r, bool right, bool motion)
        {
            var f = m.State.F[i];
            var def = m.Def(i);
            var ch = CharacterRoster.Of(def.type);
            var pc = Gui.PlayerColor(i);

            const float k = PanelSkew;
            Gui.Skew(new Rect(r.x + PanelShadow, r.y + PanelShadow, r.width, r.height), new Color(0f, 0f, 0f, 0.4f), k);
            Gui.Skew(r, new Color(0.055f, 0.09f, 0.16f, 0.93f), k);
            Gui.Skew(new Rect(r.x, r.y, r.width, 4f), pc, k);
            Gui.SkewFrame(r, new Color(ch.Primary.r, ch.Primary.g, ch.Primary.b, 0.7f), 1.5f, k);

            float px = right ? r.xMax - 100f : r.x + 10f;
            var port = new Rect(px, r.y + 10f, 90f, 90f);
            Gui.Rect(port, new Color(ch.Primary.r * 0.3f, ch.Primary.g * 0.3f, ch.Primary.b * 0.3f, 1f));
            CharacterArt.DrawPortrait(port, ch, 1f, right);
            Gui.Frame(port, ch.Primary, 2f);
            var tag = new Rect(px - 6f, r.y + 102f, 102f, 20f);
            Gui.Skew(tag, pc, 0.3f);
            Gui.Text(tag, Who(m, i), 13, Gui.Paper, TextAnchor.MiddleCenter, true, false);

            float bx = right ? r.x + 12f : r.x + 110f;
            float bw = 318f;
            var anchor = right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            Gui.Display(new Rect(bx, r.y + 4f, bw, 26f), CharNameUpper(def.type), 24, Gui.Paper, anchor, 0.15f);
            float nw = Gui.Measure(CharNameUpper(def.type), 24, true) + 12f;
            var sub = right ? new Rect(bx, r.y + 6f, bw - nw, 22f) : new Rect(bx + nw, r.y + 6f, bw - nw, 22f);
            Gui.Text(sub, subLine[i], 12, Gui.Muted, anchor, true, false);

            // Damage percent (percent-knockback.md 3.5): big number on the screen-centre side, stocks on the portrait side.
            var pctBlock = right ? new Rect(bx, r.y + 24f, PctBlockW, 58f) : new Rect(bx + bw - PctBlockW, r.y + 24f, PctBlockW, 58f);
            PercentNumber(pctBlock, i, shownPct[i], !right, motion);
            StockRow(m, i, f, ch, right ? bx + bw : bx, r.y + 33f, right);

            float ult = f.Ult / (float)ChibiSimConst.UltMax;
            bool full = ult >= 1f;
            float ultW = bw - PctBlockW - 4f;
            var ultRect = new Rect(right ? bx + bw - ultW : bx, r.y + 62f, ultW, 14f);
            var ultColor = full ? Color.Lerp(Gui.Gold, Color.white, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f)) : Color.Lerp(Gui.Accent2, Gui.Accent, ult);
            if (full) Gui.Glow(new Rect(ultRect.x - 10f, ultRect.y - 10f, ultRect.width + 20f, ultRect.height + 20f), new Color(1f, 0.8f, 0.2f, 0.5f));
            Gui.Meter(ultRect, ult, ultColor, 20);
            int ultN = Mathf.Clamp(Mathf.FloorToInt(ult * 100f), 0, 100);
            string ultLabel = full ? ultReady[i] : UltPctText[ultN] ?? (UltPctText[ultN] = "ULT " + ultN + "%");
            Gui.Text(ultRect, ultLabel, 11, full ? Gui.Ink : Gui.Paper, TextAnchor.MiddleCenter, true, !full);

            float sw = (bw - 8f) / 3f;
            for (int s = 0; s < 3; s++)
            {
                var sr = new Rect(bx + s * (sw + 4f), r.y + 82f, sw, 22f);
                if (s >= def.SkillCount) { Gui.Skew(sr, new Color(0f, 0f, 0f, 0.25f), 0.3f); continue; }
                float cdSec = f.Cd[s] / (float)ChibiSimConst.TickRate;
                float cd = def.skillCooldowns[s] > 0f ? cdSec / def.skillCooldowns[s] : 0f;
                Gui.Skew(sr, new Color(ch.Primary.r, ch.Primary.g, ch.Primary.b, 0.4f), 0.3f);
                if (cd > 0f) Gui.Skew(new Rect(sr.x, sr.y, sr.width * Mathf.Clamp01(cd), sr.height), new Color(0f, 0f, 0f, 0.6f), 0.3f);
                // cooldown text rebuilt only when its tenth of a second changes (audit P4 #2)
                int tenths = cd > 0f ? Mathf.Max(1, Mathf.RoundToInt(cdSec * 10f)) : 0;
                int si = i * 3 + s;
                if (tenths != skillKey[si])
                {
                    skillKey[si] = tenths;
                    skillText[si] = tenths > 0 ? skillBase[si] + " " + (tenths / 10f).ToString("0.0") : skillBase[si];
                }
                Gui.Text(sr, skillText[si], 10, cd > 0f ? Gui.Muted : Gui.Paper);
            }

            var st = StatusLine(m, i, f, def);
            if (st.Length > 0) Gui.Text(new Rect(bx, r.y + 106f, bw, 20f), st, 12, Gui.Gold, anchor);
        }

        // ================================================================ damage percent + stocks (percent-knockback.md 3.5)
        // View-only: everything below READS PctM / Stocks / OutTicks / HoverTicks / KosTaken and keeps its own animation
        // clocks. Nothing here feeds back into the simulation (DEVELOPER-GUIDE section 2).

        /// <summary>HUD colour ramp (spec 7, view-only tuning knob): percent stops and their colours, Brawlhalla-style.</summary>
        static readonly float[] RampAt = { 0f, 50f, 100f, 150f, 200f };
        static readonly Color[] RampCol =
        {
            new Color(1f, 0.98f, 0.94f),     // 0 %    white
            new Color(1f, 0.86f, 0.24f),     // 50 %   yellow
            new Color(1f, 0.53f, 0.12f),     // 100 %  orange
            new Color(0.95f, 0.17f, 0.18f),  // 150 %  red
            new Color(0.66f, 0.04f, 0.09f),  // 200 %+ dark red
        };
        const float PctBlockW = 140f;     // width reserved for the big number in a player panel
        const int PctBig = 52, PctSmall = 22;
        const float PopTime = 0.32f;      // pop + shake length after the number goes up
        const float CountRate = 22f;      // per-second rate the shown number catches up with the sim's (count-up)
        const float LostTime = 0.7f, MinusTime = 1.3f;

        static ChibiMatch trackFor;
        static int trackFrame = -1;
        static readonly int[] seenPct = new int[2], seenStocks = new int[2], seenKos = new int[2], lostSlot = new int[2];
        static readonly float[] shownPct = new float[2], popAt = { -10f, -10f }, popAmt = new float[2], lostAt = { -10f, -10f };
        // training readout: last hit, running combo (hits landed while the target was still in hitstun)
        static readonly int[] lastHitM = new int[2], comboM = new int[2], comboN = new int[2];
        static readonly bool[] wasStunned = new bool[2];
        static readonly string[] WholeText = new string[1000];
        static readonly string[] TenthText = { ".0%", ".1%", ".2%", ".3%", ".4%", ".5%", ".6%", ".7%", ".8%", ".9%" };
        static int clockSecs = -1;
        static string clockText = "";

        /// <summary>Colour of a percent on the HUD ramp (white 0 % to dark red 200 %+).</summary>
        public static Color PctColor(float pct)
        {
            if (pct <= RampAt[0]) return RampCol[0];
            for (int k = 1; k < RampAt.Length; k++)
                if (pct < RampAt[k]) return Color.Lerp(RampCol[k - 1], RampCol[k], (pct - RampAt[k - 1]) / (RampAt[k] - RampAt[k - 1]));
            return RampCol[RampAt.Length - 1];
        }

        /// <summary>
        /// Once per rendered frame: diff the sim's percent / stocks against what the HUD last saw and start the pop, the
        /// "-1" and the count-up. A rollback that lowers PctM just snaps (no pop), so corrections never look like hits.
        /// </summary>
        static void Track(ChibiMatch m)
        {
            var s = m.State;
            if (trackFor != m)
            {
                trackFor = m;
                for (int i = 0; i < 2; i++)
                {
                    var f = s.F[i];
                    seenPct[i] = f.PctM; seenStocks[i] = f.Stocks; seenKos[i] = f.KosTaken; shownPct[i] = f.PctM / 1000f;
                    popAt[i] = lostAt[i] = -10f; popAmt[i] = 0f; lastHitM[i] = comboM[i] = comboN[i] = 0; wasStunned[i] = false;
                }
                clockSecs = -1;
                RefreshNames(m, true);
                BuildMatchText(m);
            }
            else RefreshNames(m, false);
            if (trackFrame == Time.frameCount) return;   // OnGUI runs several events per frame; animate once
            trackFrame = Time.frameCount;
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            for (int i = 0; i < 2; i++)
            {
                var f = s.F[i];
                int d = f.PctM - seenPct[i];
                if (d > 0)
                {
                    popAt[i] = now;
                    popAmt[i] = d / 1000f;
                    lastHitM[i] = d;
                    if (wasStunned[i]) { comboM[i] += d; comboN[i]++; } else { comboM[i] = d; comboN[i] = 1; }
                }
                if (f.Stocks < seenStocks[i]) { lostAt[i] = now; lostSlot[i] = f.Stocks; }
                else if (f.KosTaken > seenKos[i] && f.Stocks == seenStocks[i]) { lostAt[i] = now; lostSlot[i] = -1; }   // training KO
                seenPct[i] = f.PctM; seenStocks[i] = f.Stocks; seenKos[i] = f.KosTaken;
                wasStunned[i] = f.Stun > 0;

                // count-up: the shown number races to the sim value; going down (respawn, lifesteal, rollback) snaps
                float target = Mathf.Min(f.PctM, ChibiPercentConst.PctCapM) / 1000f;
                if (target <= shownPct[i]) shownPct[i] = target;
                else
                {
                    float gap = target - shownPct[i];
                    shownPct[i] = gap < 0.05f ? target : shownPct[i] + Mathf.Max(gap * (1f - Mathf.Exp(-CountRate * dt)), Mathf.Min(gap, 30f * dt));
                }
            }
        }

        /// <summary>
        /// The big damage number: whole percent in display type plus a small ".x%", ink outline and drop shadow, ramp
        /// colour, a pop + shake when it goes up (shake off with the screen-shake setting) and a glow from 100 %.
        /// </summary>
        static void PercentNumber(Rect block, int i, float pct, bool alignRight, bool motion)
        {
            float now = Time.unscaledTime;
            float k = Mathf.Clamp01((now - popAt[i]) / PopTime);
            float e = (1f - k) * (1f - k);
            float scale = 1f + 0.3f * e;
            float amp = Mathf.Clamp(2f + popAmt[i] * 0.6f, 2f, 9f);
            var shake = motion ? new Vector2(Mathf.Sin(now * 97f), Mathf.Cos(now * 83f)) * amp * e : Vector2.zero;

            int whole = Mathf.Clamp(Mathf.FloorToInt(pct), 0, 999);
            int tenth = Mathf.Clamp(Mathf.FloorToInt(pct * 10f) - whole * 10, 0, 9);
            string a = WholeText[whole] ?? (WholeText[whole] = whole.ToString());
            string b = TenthText[tenth];
            var col = PctColor(pct);
            var face = Color.Lerp(col, Color.white, 0.55f * e);   // hit flash

            int big = Mathf.RoundToInt(PctBig * scale), small = Mathf.RoundToInt(PctSmall * scale);
            float wa = Gui.Measure(a, big, true), wb = Gui.Measure(b, small, true);
            float total = wa + wb + 2f;
            float x0 = (alignRight ? block.xMax - total - 6f : block.x + 4f) + shake.x;
            float bottom = block.center.y + PctBig * 0.5f + shake.y;
            var bigR = new Rect(x0, bottom - big * 1.2f, wa + 10f, big * 1.2f);
            var smallR = new Rect(x0 + wa + 2f, bottom - small * 1.2f - (big - small) * 0.16f, wb + 10f, small * 1.2f);

            float glow = (pct >= 100f ? 0.22f + 0.08f * Mathf.Sin(now * 5f) : 0f) + 0.45f * e;
            if (glow > 0.01f)
                Gui.Glow(new Rect(x0 - 22f, block.y - 10f, total + 44f, block.height + 20f), new Color(col.r, col.g, col.b, glow));
            Outlined(bigR, a, big, face, 2.5f);
            Outlined(smallR, b, small, face, 1.8f);
        }

        /// <summary>Display type with a solid ink outline (8 offsets) and a hard drop shadow, the HUD's comic lettering.</summary>
        static void Outlined(Rect r, string s, int size, Color c, float o)
        {
            const float slant = 0.15f;
            var ink = new Color(Gui.Ink.r, Gui.Ink.g, Gui.Ink.b, c.a);
            float d = o + Mathf.Max(2f, size * 0.06f);
            Gui.Display(new Rect(r.x + d, r.y + d, r.width, r.height), s, size, new Color(0f, 0f, 0f, 0.55f * c.a), TextAnchor.LowerLeft, slant, Color.clear);
            // 4 diagonal copies instead of 8 around a circle (review 2026-11-16 perf fix D): every IMGUI label allocates
            // inside Unity, and this ran 10 labels per number. The diagonal offset is o * 0.8 on each axis so the corners
            // stay covered.
            float q = o * 0.8f;
            Gui.Display(new Rect(r.x - q, r.y - q, r.width, r.height), s, size, ink, TextAnchor.LowerLeft, slant, Color.clear);
            Gui.Display(new Rect(r.x + q, r.y - q, r.width, r.height), s, size, ink, TextAnchor.LowerLeft, slant, Color.clear);
            Gui.Display(new Rect(r.x - q, r.y + q, r.width, r.height), s, size, ink, TextAnchor.LowerLeft, slant, Color.clear);
            Gui.Display(new Rect(r.x + q, r.y + q, r.width, r.height), s, size, ink, TextAnchor.LowerLeft, slant, Color.clear);
            Gui.Display(r, s, size, c, TextAnchor.LowerLeft, slant, Color.clear);
        }

        /// <summary>
        /// Stock chips (character portraits) from <paramref name="edge"/> outward, the lost chip bursting away, the "-1"
        /// popup, and the RESPAWN / DROP IN state next to the chips.
        /// </summary>
        static void StockRow(ChibiMatch m, int i, ChibiFighterSim f, CharacterInfo ch, float edge, float y, bool right)
        {
            const float cs = 24f, gap = 5f;
            float now = Time.unscaledTime;
            int n = Mathf.Max(ChibiPercentConst.StocksStart, f.Stocks);
            Rect Chip(int j) => new Rect(right ? edge - (j + 1) * (cs + gap) + gap : edge + j * (cs + gap), y, cs, cs);
            var pc = Gui.PlayerColor(i);
            for (int j = 0; j < n; j++)
            {
                var c = Chip(j);
                if (j < f.Stocks)
                {
                    Gui.Rect(new Rect(c.x + 2f, c.y + 2f, cs, cs), new Color(0f, 0f, 0f, 0.5f));
                    Gui.Rect(c, new Color(ch.Primary.r * 0.3f, ch.Primary.g * 0.3f, ch.Primary.b * 0.3f, 1f));
                    CharacterArt.DrawPortrait(c, ch, 1f, right);
                    Gui.Frame(c, Gui.Paper, 1.5f);
                    Gui.Rect(new Rect(c.x, c.yMax - 3f, cs, 3f), pc);
                }
                else
                {
                    Gui.Rect(c, new Color(0f, 0f, 0f, 0.45f));
                    Gui.Frame(c, new Color(Gui.Steel.r, Gui.Steel.g, Gui.Steel.b, 0.9f), 1f);
                    Gui.Line(new Vector2(c.x + 5f, c.yMax - 5f), new Vector2(c.xMax - 5f, c.y + 5f), new Color(Gui.Steel.r, Gui.Steel.g, Gui.Steel.b, 0.9f), 2f);
                }
            }

            float lt = now - lostAt[i];
            if (lt < LostTime && lostSlot[i] >= 0 && lostSlot[i] < n)
            {
                float k = lt / LostTime, sc = 1f + 0.8f * k;
                var c = Chip(lostSlot[i]);
                var burst = new Rect(c.center.x - cs * sc * 0.5f, c.center.y - cs * sc * 0.5f - 10f * k, cs * sc, cs * sc);
                Gui.Glow(new Rect(burst.x - 12f, burst.y - 12f, burst.width + 24f, burst.height + 24f), new Color(1f, 0.3f, 0.2f, 0.6f * (1f - k)));
                CharacterArt.DrawPortrait(burst, ch, 1f - k, right);
                Gui.Frame(burst, new Color(1f, 0.35f, 0.3f, 1f - k), 2f);
            }
            if (lt < MinusTime)
            {
                float k = lt / MinusTime, a = 1f - Mathf.Clamp01((k - 0.6f) / 0.4f);
                float pop = 1f + Mathf.Max(0f, 0.25f - lt) * 1.6f;
                string txt = lostSlot[i] >= 0 ? "-1" : "KO";
                var anchorChip = Chip(Mathf.Clamp(lostSlot[i] >= 0 ? lostSlot[i] : 0, 0, n - 1));
                int size = Mathf.RoundToInt(30f * pop);
                float w = Gui.Measure(txt, size, true) + 10f;
                Outlined(new Rect(anchorChip.center.x - w * 0.5f, anchorChip.y + 10f - 22f * k - size * 1.2f, w, size * 1.2f), txt, size,
                         new Color(1f, 0.32f, 0.28f, a), 2.2f);
            }

            // State next to the chips: off-screen countdown, then the hover on the respawn point.
            float sx = right ? edge - n * (cs + gap) - 8f : edge + n * (cs + gap) + 4f;
            const float sw = 96f;
            var sr = new Rect(right ? sx - sw : sx, y, sw, cs);
            var anchor = right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            if (f.OutTicks > 0)
            {
                float t = 1f - f.OutTicks / (float)ChibiPercentConst.RespawnDelay;
                Gui.Text(new Rect(sr.x, sr.y - 2f, sw, 16f), "RESPAWN", 11, Gui.Muted, anchor, true, false);
                var bar = new Rect(sr.x, sr.y + 16f, sw - 10f, 4f);
                if (right) bar.x += 10f;
                Gui.Bar(bar, t, pc, new Color(0f, 0f, 0f, 0.5f));
            }
            else if (f.HoverTicks > 0)
            {
                float pulse = 0.6f + 0.4f * Mathf.Sin(now * 8f);
                Gui.Text(sr, "DROP IN!", 13, new Color(Gui.Gold.r, Gui.Gold.g, Gui.Gold.b, pulse), anchor, true, true);
            }
        }

        /// <summary>Respawn platform under a hovering fighter: player-colour halo and a shrinking hover-time bar.</summary>
        static void RespawnHalo(ChibiMatch m, int i, ChibiFighterSim f, Color c)
        {
            var feet = Gui.WorldToGui(m.Center(i) + Vector2.down * 0.95f);
            float now = Time.unscaledTime, pulse = 0.5f + 0.5f * Mathf.Sin(now * 6f);
            Gui.Glow(new Rect(feet.x - 70f, feet.y - 22f, 140f, 44f), new Color(c.r, c.g, c.b, 0.35f + 0.25f * pulse));
            Gui.Skew(new Rect(feet.x - 44f, feet.y - 3f, 88f, 7f), new Color(1f, 1f, 1f, 0.9f), 0.4f);
            Gui.Skew(new Rect(feet.x - 40f, feet.y + 4f, 80f, 4f), c, 0.4f);
            float t = f.HoverTicks / (float)ChibiPercentConst.RespawnHoverMax;
            Gui.Rect(new Rect(feet.x - 40f, feet.y + 12f, 80f * t, 3f), new Color(c.r, c.g, c.b, 0.8f));
        }

        /// <summary>
        /// Training panel under the timer: the target's exact percent, the last hit and the running combo (hits that landed
        /// while it was still in hitstun), and KOs. Target = the dummy (the CPU side), else P2.
        /// </summary>
        static void TrainingReadout(ChibiMatch m)
        {
            int t = m.IsCpu(1) ? 1 : m.IsCpu(0) ? 0 : 1;
            var f = m.State.F[t];
            var r = TrainingRect();
            Gui.Skew(new Rect(r.x + TrainShadow, r.y + TrainShadow, r.width, r.height), new Color(0f, 0f, 0f, 0.35f), TrainSkew);
            Gui.Skew(r, new Color(0.055f, 0.09f, 0.16f, 0.88f), TrainSkew);
            Gui.Skew(new Rect(r.x, r.y, r.width, 3f), Gui.Gold, TrainSkew);
            int pctTenths = Mathf.Min(f.PctM, ChibiPercentConst.PctCapM) / 100;
            float pct = pctTenths / 10f;
            // every line is rebuilt only when its number changes (audit P4)
            if (pctTenths != trainPctKey) { trainPctKey = pctTenths; trainPct = pct.ToString("0.0") + "%"; }
            int lastKey = t * 1_000_000 + lastHitM[t] / 100;
            if (lastKey != trainLastKey)
            {
                trainLastKey = lastKey;
                trainLast = "LAST HIT  " + (lastHitM[t] > 0 ? "+" + (lastHitM[t] / 100 / 10f).ToString("0.0") + "%" : "-");
            }
            int comboKey = comboN[t] >= 2 ? comboN[t] * 100_000 + comboM[t] / 100 : 0;
            if (comboKey != trainComboKey)
            {
                trainComboKey = comboKey;
                trainCombo = "COMBO  " + (comboN[t] >= 2 ? comboN[t] + " HITS  +" + (comboM[t] / 100 / 10f).ToString("0.0") + "%" : "-");
            }
            if (f.KosTaken != trainKosKey) { trainKosKey = f.KosTaken; trainKos = "KOs  " + f.KosTaken; }
            Gui.Text(new Rect(r.x + 14f, r.y + 6f, 150f, 20f), trainTitle[t], 11, Gui.Gold, TextAnchor.MiddleLeft, true, false);
            Gui.Display(new Rect(r.x + 14f, r.y + 22f, 130f, 36f), trainPct, 30, PctColor(pct), TextAnchor.MiddleLeft, 0.15f);
            var lr = new Rect(r.x + 150f, r.y + 6f, r.width - 164f, 18f);
            Gui.Text(lr, trainLast, 12, Gui.Paper, TextAnchor.MiddleRight, true, false);
            Gui.Text(new Rect(lr.x, lr.y + 18f, lr.width, 18f), trainCombo, 12, comboN[t] >= 2 ? Gui.Gold : Gui.Muted, TextAnchor.MiddleRight, true, false);
            Gui.Text(new Rect(lr.x, lr.y + 36f, lr.width, 18f), trainKos, 12, Gui.Muted, TextAnchor.MiddleRight, true, false);
        }

        // ================================================================ cached text (perf audit P4: no per-OnGUI strings)

        static readonly string[] CvTitles = { "P1 CAMERA · SIM", "P1 CAMERA · LIVE", "P2 CAMERA · SIM", "P2 CAMERA · LIVE" };
        static readonly string[] Digits = { "0", "1", "2", "3" };
        static readonly string[] UltPctText = new string[101];
        static readonly string[] whoShort = { "P1", "P2" }, whoFull = { "P1", "P2" }, whoSrc = new string[2];
        static readonly string[] promptHead = new string[6], trainTitle = new string[2], promptMsg = { "", "" };
        static readonly int[] promptKey = { -1, -1 };
        static readonly string[] subLine = new string[2], ultReady = new string[2], simKey = new string[2], hintText = { "", "" };
        static readonly string[] skillBase = new string[6], skillText = new string[6];
        static readonly int[] skillKey = new int[6];
        static string introLine = "", stageUpper = "";
        static int trainPctKey = -1, trainLastKey = -1, trainComboKey = -1, trainKosKey = -1;
        static string trainPct = "", trainLast = "", trainCombo = "", trainKos = "";

        // status line: rebuilt only when one of its inputs changes (the same inputs ChibiMatch.Statuses reads)
        const int StatusKeyLen = 10;
        static readonly int[] statusKey = new int[2 * StatusKeyLen];
        static readonly string[] statusText = { "", "" };

        static void SKey(int at, int v, ref bool changed)
        {
            if (statusKey[at] == v) return;
            statusKey[at] = v;
            changed = true;
        }

        static string StatusLine(ChibiMatch m, int i, ChibiFighterSim f, ClassDef def)
        {
            const float tr = ChibiSimConst.TickRate;
            int guardMax = ChibiCombatStats.GuardMaxU(f.Stats.Defense);
            int o = i * StatusKeyLen;
            bool changed = false;
            SKey(o + 0, f.AuraTicks > 0 ? Mathf.CeilToInt(f.AuraTicks / tr) : 0, ref changed);
            SKey(o + 1, f.RingTicks > 0 ? Mathf.CeilToInt(f.RingTicks / tr) : 0, ref changed);
            SKey(o + 2, f.TailwindTicks > 0 ? 1 : 0, ref changed);
            SKey(o + 3, f.GuardBrokenTicks > 0 ? Mathf.RoundToInt(f.GuardBrokenTicks / tr * 10f) + 1 : 0, ref changed);
            SKey(o + 4, f.GuardU < guardMax - ChibiCombatStats.HpScale / 2 ? Mathf.RoundToInt(100f * f.GuardU / guardMax) + 1 : 0, ref changed);
            SKey(o + 5, def.rangedNormals && f.SetCd > 0 ? Mathf.RoundToInt(f.SetCd / tr * 10f) + 1 : 0, ref changed);
            SKey(o + 6, f.SlowTicks > 0 ? 1 : 0, ref changed);
            SKey(o + 7, f.ArmorBreakTicks > 0 ? 1 : 0, ref changed);
            SKey(o + 8, f.ComboHits >= 2 ? f.ComboHits : 0, ref changed);
            if (!changed) return statusText[i];
            var st = string.Join("  ", m.Statuses(i, StatusScratch));
            if (f.ComboHits >= 2) st = f.ComboHits + " HITS  " + st;
            return statusText[i] = st;
        }

        static ChibiMatch resultsFor;
        static int resultsRewards = -1;
        static string resTitle = "", resWhy = "", resRewardsHead = "", resRewards = "", resCoins = "";
        static readonly string[] resStocks = new string[2], resPct = new string[2], resStats = new string[2];

        /// <summary>Results text from the final state, built once per match (the reward lines when the log grows).</summary>
        static void BuildResultsText(ChibiMatch m, int w, bool noContest)
        {
            if (resultsFor != m)
            {
                resultsFor = m;
                resultsRewards = -1;
                resTitle = noContest ? "NO CONTEST" : w < 0 ? "DRAW!" : m.IsCpu(w) ? "CPU WINS" : m.Online ? (w == m.LocalPlayer ? "YOU WIN!" : "RIVAL WINS")
                         : CharNameUpper(m.Def(w).type) + " WINS!";
                string why = m.OnlineEndText;
                resWhy = why.Length > 0 ? why : !noContest && w >= 0 && !m.Online && !m.IsCpu(w) ? Who(m, w, true) + " takes the round" : "";
                for (int i = 0; i < 2; i++)
                {
                    var f = m.State.F[i];
                    resStocks[i] = (f.Stocks == 1 ? "1 STOCK" : f.Stocks + " STOCKS") + "  ·";
                    resPct[i] = (Mathf.Min(f.PctM, ChibiPercentConst.PctCapM) / 100 / 10f).ToString("0.0") + "%";
                    var tr = m.Tracker(i);
                    resStats[i] = "Hits landed  " + f.HitsLanded + "\nFull combos  " + f.CombosLanded + "   ·   Best combo  " + f.MaxCombo +
                                  "\nGesture ultimates  " + f.UltsUsed + "\nParries  " + f.Parries +
                                  (tr == null ? "" : "\nGood posture  " + Mathf.RoundToInt(tr.PostureAverage * 100f) + "%");
                }
                string me = m.NameOf(m.LocalPlayer) ?? "";
                resRewardsHead = "REWARDS (" + (me.Length > 0 ? ChibiSocialRules.BaseName(me) + "'s" : m.Online ? "your" : "P1") + " profile)";
            }
            if (m.RewardLog.Count != resultsRewards)
            {
                resultsRewards = m.RewardLog.Count;
                resRewards = string.Join("\n", m.RewardLog);
                resCoins = "+" + m.CoinsEarned + " coins  (total " + m.GM.Save.coins + ")";
            }
        }

        static void Timer(ChibiMatch m)
        {
            var s = m.State;
            var r = TimerRect();
            Gui.Skew(new Rect(r.x + TimerShadow, r.y + TimerShadow, r.width, r.height), new Color(0f, 0f, 0f, 0.4f), TimerSkew);
            Gui.Skew(r, new Color(0.055f, 0.09f, 0.16f, 0.95f), TimerSkew);
            Gui.Skew(new Rect(r.x, r.y, r.width * 0.5f, 4f), Gui.P1, TimerSkew);
            Gui.Skew(new Rect(r.center.x, r.y, r.width * 0.5f, 4f), Gui.P2, TimerSkew);
            float left = s.TimeLeftTicks / (float)ChibiSimConst.TickRate;
            int secs = Mathf.CeilToInt(left);
            if (secs != clockSecs) { clockSecs = secs; clockText = secs / 60 + ":" + (secs % 60).ToString("00"); }
            string t = m.Config.training ? "∞" : clockText;
            var tc = m.Config.training ? Gui.Paper : left < 10f ? Gui.Red : left < 30f ? Gui.Gold : Gui.Paper;
            Gui.Display(new Rect(r.x, r.y + 2f, r.width, 50f), t, m.Config.training ? 44 : 40, tc, TextAnchor.MiddleCenter, 0.15f);
            Gui.Text(new Rect(r.x, r.y + 48f, r.width, 20f), stageUpper, 10, Gui.Muted, TextAnchor.MiddleCenter, true, false);
        }

        static void GesturePrompt(ChibiMatch m, int i, Rect r)
        {
            var f = m.State.F[i];
            var def = m.Def(i);
            bool open = f.UltWindowTicks > 0;
            bool detected = f.UltPrompt == (int)ChibiUltPrompt.Detected;
            Color state = open ? Gui.Yellow : detected ? Gui.Green : Gui.Red;
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f);
            Gui.Glow(new Rect(r.x - 40f, r.y - 40f, r.width + 80f, r.height + 80f), new Color(0.3f, 0.9f, 1f, 0.25f + 0.2f * pulse));
            Gui.Rect(r, new Color(0.05f, 0.25f, 0.35f, 0.55f));
            for (float y = r.y + 4f; y < r.yMax - 4f; y += 5f)
                Gui.Rect(new Rect(r.x + 6f, y, r.width - 12f, 1f), new Color(0.5f, 1f, 1f, 0.07f));
            Gui.Frame(r, state, 3f);

            string header = promptHead[i * 3 + (open ? 0 : detected ? 1 : 2)];
            Gui.Text(new Rect(r.x, r.y + 6f, r.width, 24f), header, 15, state);

            var fig = new Rect(r.x + 14f, r.y + 36f, 120f, 150f);
            Gui.Rect(fig, new Color(0f, 0.1f, 0.15f, 0.5f));
            Gui.Skeleton(fig, SimulatedPoseSource.TemplateCached(def.ultGesture, false), new Color(0.5f, 1f, 1f, 0.6f + 0.4f * pulse), 3f);

            var info = new Rect(r.x + 144f, r.y + 36f, r.width - 156f, 150f);
            Gui.Text(new Rect(info.x, info.y, info.width, 44f), ClassDef.GestureName(def.ultGesture), 17, Gui.Paper);
            float windowLeft = f.UltWindowTicks / (float)ChibiSimConst.TickRate;
            if (open)
            {
                int n = Mathf.Clamp(Mathf.CeilToInt(windowLeft), 1, 3);
                float frac = windowLeft - Mathf.Floor(windowLeft);
                float ring = 60f + 20f * frac;
                var c = new Rect(info.center.x - ring * 0.5f, info.y + 82f - ring * 0.5f, ring, ring);
                Gui.Frame(c, state, 4f, ring * 0.5f);
                Gui.Display(new Rect(info.x, info.y + 52f, info.width, 60f), Digits[n], 46, state, TextAnchor.MiddleCenter, 0.1f);
                var tr = m.Tracker(i);
                if (tr != null) Gui.Text(new Rect(info.x, info.y + 118f, info.width, 30f), tr.IsLive ? "do the pose on camera" : simKey[i], 11, Gui.Muted);
            }
            else
            {
                Gui.Text(new Rect(info.x, info.y + 52f, info.width, 60f), detected ? "✓" : "✗", 48, state);
            }

            var bar = new Rect(r.x + 14f, r.yMax - 34f, r.width - 28f, 10f);
            Gui.Bar(bar, open ? f.UltWindowTicks / (float)ChibiSimConst.UltWindowTicks : 1f, state, new Color(0f, 0f, 0f, 0.4f));
            int pk = f.UltWindowTicks > 0 ? 1 : 2 + f.UltPrompt;   // PromptMessage depends on these two only
            if (pk != promptKey[i]) { promptKey[i] = pk; promptMsg[i] = m.PromptMessage(i); }
            Gui.Text(new Rect(r.x, r.yMax - 24f, r.width, 20f), promptMsg[i], 12, Gui.Paper);
        }

        static void Cinematic(ChibiMatch m, ChibiSimView v, float t)
        {
            float inT = Mathf.Clamp01(t / 0.2f);
            float outT = Mathf.Clamp01((1.5f - t) / 0.3f);
            float k = Mathf.Min(inT, outT);
            float bar = 80f * k;
            Gui.Rect(new Rect(0f, 0f, Gui.W, bar), Color.black);
            Gui.Rect(new Rect(0f, Gui.H - bar, Gui.W, bar), Color.black);

            // Subtitle with the character's name (the view's own subtitle carries the old class hero name).
            if (cinematicSubFor != v.CinematicStart)
            {
                cinematicSubFor = v.CinematicStart;
                int who = Mathf.Clamp(v.CinematicFighter, 0, 1);
                cinematicSub = CharNameUpper(m.Def(who).type) + " · " + m.Def(who).ultDesc;
            }

            var c = v.CinematicColor;
            float slide = (1f - inT) * Gui.W;
            var band = new Rect(-slide - 40f, Gui.H * 0.62f, Gui.W + 80f, 90f);
            Gui.Skew(band, new Color(c.r * 0.3f, c.g * 0.3f, c.b * 0.3f, 0.85f * k), 0f);
            Gui.Rect(new Rect(band.x, band.y, band.width, 4f), new Color(c.r, c.g, c.b, k));
            Gui.Rect(new Rect(band.x, band.yMax - 4f, band.width, 4f), new Color(c.r, c.g, c.b, k));
            Gui.Display(new Rect(band.x, band.y + 2f, band.width, 58f), v.CinematicTitle, 50, new Color(1f, 1f, 1f, k), TextAnchor.MiddleCenter, 0.2f);
            Gui.Text(new Rect(band.x, band.y + 58f, band.width, 26f), cinematicSub, 15, new Color(c.r, c.g, c.b, k));
        }

        // ================================================================ HUD plates + world label layout (art review pass 2 #4)
        // The plate geometry below is shared by the drawing code above and the label layout, so the keep-out rects are
        // always the rects actually drawn. All values are Gui virtual-canvas units (scaled with the resolution by Gui).

        const float PanelInset = 16f, PanelTop = 14f, PanelW = 440f, PanelH = 130f, PanelSkew = 0.12f, PanelShadow = 5f;
        const float TimerTop = 12f, TimerW = 124f, TimerH = 72f, TimerSkew = 0.15f, TimerShadow = 4f;
        const float TrainTop = 92f, TrainW = 300f, TrainH = 62f, TrainSkew = 0.12f, TrainShadow = 4f;
        const float CvW = 230f, CvH = 172f, CvFromBottom = 196f;

        // Name tag (above the fighter's body centre) and its pointer.
        const float TagLiftWorld = 1.25f, TagLift = 24f, TagH = 18f, TagPadX = 16f, TagSkew = 0.3f;
        const int TagFont = 13;
        const float PointerW = 12f, PointerH = 8f, PointerGap = 2f;
        const float PointerReach = PointerGap + PointerH;   // room a tag keeps above and below itself for its pointer
        // Popups: draw box (body text wraps past its width) and the line height the display lettering uses (Outlined).
        const float PopupW = 300f, PopupH = 40f, PopupLineH = 1.2f, ComicOutline = 2.5f;
        // Popups: font size at popup size 1 (drawn at this size, the spawn "pop" is a GUI scale, so the font atlas is not
        // re-rasterised every frame), and how fast (1/s) a popup eases to its placed spot instead of snapping there.
        const float PopupBaseFont = 20f, PopupFollowRate = 14f;
        /// <summary>Minimum space between a world label and a plate or another label.</summary>
        const float LabelGap = 6f;

        // Keep-out rects: up to 6 plates, then the placed name tags (tag + pointer room), then the placed popups.
        const int MaxPlates = 6, MaxPopupBlockers = 24;
        static readonly Rect[] labelBlockers = new Rect[MaxPlates + 2 + MaxPopupBlockers];
        static int plateCount, blockerCount;
        static Rect labelArea;

        /// <summary>Per-popup layout cache: base font size and text width measured once, and the eased on-screen centre.</summary>
        sealed class PopupLayout
        {
            public string Text;
            public int Size;
            public float Width;
            public Vector2 Shown;
            public int Frame;
        }
        static readonly Dictionary<Fx.Popup, PopupLayout> popupLayouts = new Dictionary<Fx.Popup, PopupLayout>();
        static readonly List<Fx.Popup> popupStale = new List<Fx.Popup>(8);
        static int popupFrame = -1;

        static Rect PanelRect(int i) => new Rect(i == 0 ? PanelInset : Gui.W - PanelInset - PanelW, PanelTop, PanelW, PanelH);
        static Rect TimerRect() => new Rect(Gui.W * 0.5f - TimerW * 0.5f, TimerTop, TimerW, TimerH);
        static Rect TrainingRect() => new Rect(Gui.W * 0.5f - TrainW * 0.5f, TrainTop, TrainW, TrainH);
        static Rect CvRect(int i) => new Rect(i == 0 ? PanelInset : Gui.W - PanelInset - CvW, Gui.H - CvFromBottom, CvW, CvH);

        /// <summary>
        /// Once per OnGUI pass, before any world label: the screen extents of this frame's HUD plates (player panels,
        /// timer, training readout, camera panels) and the safe area under the top band that labels may use.
        /// </summary>
        static void BuildPlates(ChibiMatch m, bool cvOverlay)
        {
            int n = 0;
            labelBlockers[n++] = HudLabelLayout.SkewedPlate(PanelRect(0), PanelSkew, PanelShadow);
            labelBlockers[n++] = HudLabelLayout.SkewedPlate(PanelRect(1), PanelSkew, PanelShadow);
            labelBlockers[n++] = HudLabelLayout.SkewedPlate(TimerRect(), TimerSkew, TimerShadow);
            if (m.Config.training) labelBlockers[n++] = HudLabelLayout.SkewedPlate(TrainingRect(), TrainSkew, TrainShadow);
            if (cvOverlay)
                for (int i = 0; i < 2; i++)
                    if (m.Tracker(i) != null) labelBlockers[n++] = CvRect(i);
            plateCount = blockerCount = n;
            float band = HudLabelLayout.BandBottom(labelBlockers, plateCount, Gui.H);
            labelArea = HudLabelLayout.SafeArea(Gui.W, Gui.H, PanelInset, band, LabelGap);
        }

        /// <summary>
        /// Name tags: over each fighter, kept below the top HUD band, out of the plates and off the other fighter's tag.
        /// The pointer always points at the fighter (up when the tag had to drop below a plate the fighter is behind).
        /// </summary>
        static void Tags(ChibiMatch m)
        {
            for (int i = 0; i < 2; i++)
            {
                var f = m.State.F[i];
                if (f.OutTicks > 0) continue;   // KO'd: the body sits off-screen at the blast line until the respawn
                var c = Gui.PlayerColor(i);
                if (f.HoverTicks > 0) RespawnHalo(m, i, f, c);
                var p = Gui.WorldToGui(m.Center(i) + Vector2.up * TagLiftWorld);
                string who = Who(m, i);
                float w = Gui.Measure(who, TagFont) + TagPadX;
                // Layout box = tag plus pointer room on both sides, so a stacked tag never lands on another's pointer.
                var want = new Rect(p.x - w * 0.5f, p.y - TagLift - PointerReach, w, TagH + 2f * PointerReach);
                var box = HudLabelLayout.Place(want, labelArea, labelBlockers, blockerCount, LabelGap, false);
                if (blockerCount < labelBlockers.Length) labelBlockers[blockerCount++] = box;
                var tag = new Rect(box.x, box.y + PointerReach, w, TagH);
                Gui.Skew(tag, c, TagSkew);
                Gui.Text(tag, who, TagFont, Gui.Paper, TextAnchor.MiddleCenter, true, false);
                var ptr = HudLabelLayout.Pointer(tag, p, PointerW, PointerH, PointerGap, out bool down);
                Pointer(ptr, c, down);
            }
        }

        /// <summary>Tag pointer: <see cref="Gui.Chevron"/> turned to point down (or up) inside <paramref name="r"/>.</summary>
        static void Pointer(Rect r, Color c, bool down)
        {
            var saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(down ? 90f : -90f, r.center * Gui.Scale);   // pivot in screen pixels (as Gui.Line)
            Gui.Chevron(new Rect(r.center.x - r.height * 0.5f, r.center.y - r.width * 0.5f, r.height, r.width), c);
            GUI.matrix = saved;
        }

        /// <summary>
        /// Floating text (move callouts, comic SFX, damage): each one is kept on screen, out of the HUD plates, off both
        /// name tags and off the popups placed before it (older first, so a new damage number never covers a callout),
        /// stacking above a tag it would cover (below it when the tag is already under the top band). The drawn
        /// position eases toward the placed spot (no snapping when a popup crosses a tag or plate); the text is measured
        /// once per popup at its base size and the spawn "pop" is a GUI scale, so no per-frame font sizes.
        /// </summary>
        static void Popups()
        {
            int frame = Time.frameCount;
            bool newFrame = frame != popupFrame;   // OnGUI runs several passes per frame: ease once per frame
            popupFrame = frame;
            float dt = Time.unscaledDeltaTime;
            foreach (var p in Fx.Popups)
            {
                bool fresh = !popupLayouts.TryGetValue(p, out var lay) || !ReferenceEquals(lay.Text, p.text);
                if (fresh)
                {
                    if (lay == null) popupLayouts[p] = lay = new PopupLayout();
                    lay.Text = p.text;
                    lay.Size = Mathf.Max(1, Mathf.RoundToInt(PopupBaseFont * p.size));
                    lay.Width = Gui.Measure(p.text, lay.Size);
                }
                lay.Frame = frame;
                var g = Gui.WorldToGui(p.pos);
                float a = 1f - Mathf.Clamp01((p.t - 0.6f) / 0.5f);
                float pop = 1f + Mathf.Max(0f, 0.3f - p.t) * 1.5f;
                float pad = p.comic ? 2f * ComicOutline : 0f;
                var ext = (p.comic ? HudLabelLayout.RotatedSize(lay.Width + pad, lay.Size * PopupLineH + pad, p.tilt)
                                   : new Vector2(lay.Width, lay.Size * PopupLineH)) * pop;
                var placed = HudLabelLayout.PlaceAndBlock(new Rect(g.x - ext.x * 0.5f, g.y - ext.y * 0.5f, ext.x, ext.y),
                                                          labelArea, labelBlockers, ref blockerCount, LabelGap, true);
                if (fresh) lay.Shown = placed.center;   // a new popup appears at its free spot
                else if (newFrame) lay.Shown = HudLabelLayout.Follow(lay.Shown, placed.center, dt, PopupFollowRate);
                g = lay.Shown;
                float dw = Mathf.Max(PopupW, lay.Width + pad);   // never narrower than the text: body text would wrap
                var rect = new Rect(g.x - dw * 0.5f, g.y - PopupH * 0.5f, dw, PopupH);
                var saved = GUI.matrix;
                var pivot = g * Gui.Scale;   // pivot in screen pixels (as Gui.Line)
                if (pop > 1f) GUIUtility.ScaleAroundPivot(new Vector2(pop, pop), pivot);
                if (p.comic)
                {
                    // Comic SFX lettering (character sheets): thick dark outline, slight tilt, colour fill on top.
                    GUIUtility.RotateAroundPivot(p.tilt, pivot);
                    var ink = new Color(0.08f, 0.06f, 0.1f, a);
                    const float o = ComicOutline;
                    for (int k = 0; k < 8; k++)
                    {
                        float ang = k * Mathf.PI / 4f;
                        Gui.Text(new Rect(rect.x + Mathf.Cos(ang) * o, rect.y + Mathf.Sin(ang) * o, rect.width, rect.height),
                                 p.text, lay.Size, ink);
                    }
                }
                Gui.Text(rect, p.text, lay.Size, new Color(p.color.r, p.color.g, p.color.b, a));
                GUI.matrix = saved;
            }
            if (popupLayouts.Count > Fx.Popups.Count)
            {
                // drop the layouts of popups that expired (or were cleared with the match); no allocation
                popupStale.Clear();
                foreach (var kv in popupLayouts) if (kv.Value.Frame != frame) popupStale.Add(kv.Key);
                foreach (var k in popupStale) popupLayouts.Remove(k);
                popupStale.Clear();
            }
        }

        static void Hints(ChibiMatch m)
        {
            int me = m.Online ? m.LocalPlayer : 0;
            var tr = m.Tracker(me);
            Gui.Text(new Rect(260f, Gui.H - 58f, Gui.W - 520f, 54f), hintText[tr != null && tr.IsLive ? 1 : 0], 11, new Color(1f, 1f, 1f, 0.55f), TextAnchor.LowerCenter, false);
        }

        static string KeyHint(int p)
        {
            string L(BindAction a) => Bindings.Label(p, a);
            return "P" + (p + 1) + ": " + L(BindAction.Left) + "/" + L(BindAction.Right) + " move · " + L(BindAction.Jump) + " jump · " +
                   L(BindAction.Light) + " light · " + L(BindAction.Heavy) + " heavy · " + L(BindAction.Special) + " special · " +
                   L(BindAction.Block) + " block · " + L(BindAction.Dash) + " dash · " +
                   L(BindAction.Skill1) + " " + L(BindAction.Skill2) + " " + L(BindAction.Skill3) + " skills · " + L(BindAction.Ultimate) + " ult";
        }

        static Action Pause()
        {
            Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(0f, 0f, 0f, 0.55f));
            var r = new Rect(Gui.W * 0.5f - 170f, Gui.H * 0.5f - 130f, 340f, 260f);
            Gui.Box(r, Gui.P1);
            Gui.Display(new Rect(r.x, r.y + 16f, r.width, 56f), "PAUSED", 48, Gui.Paper, TextAnchor.MiddleCenter);
            Gui.PreferNext();
            if (Gui.Button(new Rect(r.x + 40f, r.y + 90f, 260f, 50f), "RESUME", Gui.Accent2)) return Action.Resume;
            if (Gui.Button(new Rect(r.x + 40f, r.y + 160f, 260f, 50f), "QUIT TO MENU", Gui.Accent)) return Action.Menu;
            Gui.Text(new Rect(r.x, r.yMax - 30f, r.width, 22f), "B / Backspace to resume", 12, Gui.Muted);
            return Action.None;
        }

        /// <summary>Online leave confirm (the match keeps running underneath): STAY closes it, LEAVE forfeits.</summary>
        static Action LeavePrompt()
        {
            Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(0f, 0f, 0f, 0.35f));
            var r = new Rect(Gui.W * 0.5f - 200f, Gui.H * 0.5f - 130f, 400f, 270f);
            Gui.Box(r, Gui.Red);
            Gui.Display(new Rect(r.x, r.y + 14f, r.width, 50f), "LEAVE MATCH?", 40, Gui.Paper, TextAnchor.MiddleCenter);
            Gui.Text(new Rect(r.x + 20f, r.y + 66f, r.width - 40f, 22f), "The match keeps going. Leaving counts as a loss.", 13, Gui.Muted);
            Gui.PreferNext();
            if (Gui.Button(new Rect(r.x + 70f, r.y + 100f, 260f, 50f), "STAY", Gui.Accent2)) return Action.Resume;
            if (Gui.Button(new Rect(r.x + 70f, r.y + 165f, 260f, 50f), "LEAVE", Gui.Red)) return Action.Forfeit;
            Gui.Text(new Rect(r.x, r.yMax - 30f, r.width, 22f), "B / Backspace / Esc / Start to stay", 12, Gui.Muted);
            return Action.None;
        }

        static Action Results(ChibiMatch m, GameManager gm)
        {
            if (!resultsFocus)
            {
                // fresh focus so attack-mashing at the KO can't press a results button by accident
                resultsFocus = true;
                Gui.ResetFocus();
                Gui.NavMode = false;
            }
            var s = m.State;
            Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(0f, 0f, 0f, 0.6f));
            var r = new Rect(Gui.W * 0.5f - 360f, Gui.H * 0.5f - 280f, 720f, 560f);
            int w = m.Winner;
            bool noContest = m.Online && m.OnlineResult.Outcome != ChibiMatchOutcome.Win && m.OnlineResult.Outcome != ChibiMatchOutcome.Loss &&
                             m.OnlineResult.Outcome != ChibiMatchOutcome.Draw;
            BuildResultsText(m, w, noContest);
            Gui.Box(r, noContest || w < 0 ? Gui.Gold : Gui.PlayerColor(w));
            Gui.Display(new Rect(r.x, r.y + 10f, r.width, 64f), resTitle, 54, !noContest && w == m.LocalPlayer ? Gui.Gold : Gui.Paper, TextAnchor.MiddleCenter);
            if (resWhy.Length > 0) Gui.Text(new Rect(r.x, r.y + 70f, r.width, 20f), resWhy, 14, Gui.Muted);

            for (int i = 0; i < 2; i++)
            {
                var f = s.F[i];
                var def = m.Def(i);
                var ch = CharacterRoster.Of(def.type);
                var col = new Rect(r.x + 30f + i * 340f, r.y + 96f, 320f, 190f);
                Gui.Rect(col, Gui.PanelLight);
                Gui.Rect(new Rect(col.x, col.y, col.width, 3f), Gui.PlayerColor(i));
                if (!noContest && i == w) Gui.Skew(new Rect(col.xMax - 86f, col.y - 10f, 76f, 20f), Gui.Gold, 0.3f);
                if (!noContest && i == w) Gui.Text(new Rect(col.xMax - 86f, col.y - 10f, 76f, 20f), "WINNER", 12, Gui.Ink, TextAnchor.MiddleCenter, true, false);
                var port = new Rect(col.x + 10f, col.y + 10f, 64f, 64f);
                CharacterArt.DrawPortrait(port, ch);
                Gui.Frame(port, ch.Primary, 2f);
                Gui.Display(new Rect(col.x + 84f, col.y + 8f, 230f, 30f), CharNameUpper(def.type), 26, Gui.Paper, TextAnchor.MiddleLeft, 0.15f);
                Gui.Text(new Rect(col.x + 84f, col.y + 38f, 230f, 18f), Who(m, i, true), 13, Gui.PlayerColor(i), TextAnchor.MiddleLeft);
                // Stocks left as small portrait chips, then the final percent in its ramp colour (spec 3.5).
                float sx = col.x + 84f;
                int chips = Mathf.Max(ChibiPercentConst.StocksStart, f.Stocks);
                for (int k = 0; k < chips; k++)
                {
                    var chip = new Rect(sx + k * 20f, col.y + 58f, 16f, 16f);
                    if (k < f.Stocks) { CharacterArt.DrawPortrait(chip, ch); Gui.Frame(chip, Gui.Paper, 1f); }
                    else { Gui.Rect(chip, new Color(0f, 0f, 0f, 0.45f)); Gui.Frame(chip, Gui.Steel, 1f); }
                }
                float fp = Mathf.Min(f.PctM, ChibiPercentConst.PctCapM) / 1000f;
                float tx = sx + chips * 20f + 6f;
                Gui.Text(new Rect(tx, col.y + 56f, 120f, 20f), resStocks[i], 12, Gui.Muted, TextAnchor.MiddleLeft);
                tx += Gui.Measure(resStocks[i], 12) + 6f;
                Gui.Display(new Rect(tx, col.y + 53f, 110f, 24f), resPct[i], 20, PctColor(fp), TextAnchor.MiddleLeft, 0.15f);
                Gui.Text(new Rect(col.x + 14f, col.y + 82f, 300f, 104f), resStats[i], 13, Gui.Paper, TextAnchor.UpperLeft, false);
            }

            var rw = new Rect(r.x + 30f, r.y + 298f, r.width - 60f, 160f);
            Gui.Rect(rw, new Color(0f, 0f, 0f, 0.3f));
            if (m.Config.training)
                Gui.Text(rw, "Training: no rewards", 16, Gui.Muted);
            else
            {
                Gui.Text(new Rect(rw.x + 14f, rw.y + 6f, rw.width, 24f), resRewardsHead, 14, Gui.Gold, TextAnchor.MiddleLeft);
                Gui.Text(new Rect(rw.x + 14f, rw.y + 32f, rw.width - 28f, 120f), resRewards, 13, Gui.Paper, TextAnchor.UpperLeft, false);
                Gui.Text(new Rect(rw.x, rw.y + 6f, rw.width - 14f, 24f), resCoins, 16, Gui.Gold, TextAnchor.MiddleRight);
            }

            float by = r.yMax - 78f;
            Gui.PreferNext();
            if (!m.Online && Gui.Button(new Rect(r.x + 30f, by, 210f, 52f), gm.RematchLabel, Gui.Accent)) return Action.Rematch;
            if (Gui.Button(new Rect(r.x + 255f, by, 210f, 52f), m.Online ? "BACK" : "CHARACTERS", Gui.Accent2)) return Action.CharacterSelect;
            if (Gui.Button(new Rect(r.x + 480f, by, 210f, 52f), "MAIN MENU", Gui.Muted)) return Action.Menu;
            return Action.None;
        }
    }
}
