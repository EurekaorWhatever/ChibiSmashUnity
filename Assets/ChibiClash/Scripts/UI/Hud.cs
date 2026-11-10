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
        public enum Action { None, Resume, Rematch, CharacterSelect, Menu }

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
            Tags(m);
            Popups();

            var v = m.View;
            if (v != null)
            {
                float ct = Time.unscaledTime - v.CinematicStart;
                if (v.CinematicFighter >= 0 && ct < 1.5f) Cinematic(m, v, ct);
            }

            PlayerPanel(m, 0, new Rect(16f, 14f, 440f, 130f), false);
            PlayerPanel(m, 1, new Rect(Gui.W - 456f, 14f, 440f, 130f), true);
            Timer(m);

            if (settings.showCvOverlay)
            {
                for (int i = 0; i < 2; i++)
                {
                    var tr = m.Tracker(i);
                    if (tr == null) continue;
                    var r = i == 0 ? new Rect(16f, Gui.H - 196f, 230f, 172f) : new Rect(Gui.W - 246f, Gui.H - 196f, 230f, 172f);
                    Gui.CvPanel(r, tr, StateColor(m, i), i == 0 && settings.useWebcam, "P" + (i + 1) + " CAMERA · " + (tr.IsLive ? "LIVE" : "SIM"));
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
                Gui.Text(new Rect(0f, Gui.H * 0.38f + 122f, Gui.W, 30f),
                         CharNameUpper(m.Def(0).type) + "  VS  " + CharNameUpper(m.Def(1).type) + "  ·  " + m.StageName.ToUpper() +
                         (m.Config.training ? "  ·  TRAINING" : "") + (m.Online ? "  ·  ONLINE" : ""), 18, Gui.Gold);
            }

            if (v != null && v.FlashAlpha > 0.01f)
            {
                var c = v.FlashColor;
                Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(c.r, c.g, c.b, v.FlashAlpha * 0.5f));
            }

            if (m.Paused) return Pause();
            if (m.Finished && Time.unscaledTime - m.FinishedTime > 1.3f) return Results(m, gm);
            resultsFocus = false;
            return Action.None;
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
        static string Who(ChibiMatch m, int i, bool full = false)
        {
            if (m.IsCpu(i)) return m.Config.training ? "DUMMY" : "CPU";
            if (m.Online)
            {
                string n = m.NameOf(i);
                if (n.Length > 0) return full ? n : ChibiSocialRules.BaseName(n);
                return i != m.LocalPlayer ? "RIVAL" : "YOU";
            }
            string own = m.NameOf(i);
            return own.Length > 0 ? own : i == 0 ? "P1" : "P2";
        }

        static void PlayerPanel(ChibiMatch m, int i, Rect r, bool right)
        {
            var f = m.State.F[i];
            var def = m.Def(i);
            var ch = CharacterRoster.Of(def.type);
            var pc = Gui.PlayerColor(i);
            bool showKeys = !m.IsCpu(i) && (!m.Online || i == m.LocalPlayer);
            int keyPlayer = m.Online ? 0 : i;   // online, the local player always uses P1's bindings

            const float k = 0.12f;
            Gui.Skew(new Rect(r.x + 5f, r.y + 5f, r.width, r.height), new Color(0f, 0f, 0f, 0.4f), k);
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
            Gui.Text(sub, ch.Weapon + " · " + def.passiveNames[f.Passive], 12, Gui.Muted, anchor, true, false);

            float maxHp = f.Stats.MaxHp;
            float hpNow = f.HpU / (float)ChibiCombatStats.HpScale;
            float hp = maxHp > 0f ? hpNow / maxHp : 0f;
            var hpColor = hp > 0.5f ? Color.Lerp(Gui.Yellow, Gui.Green, (hp - 0.5f) * 2f) : Color.Lerp(Gui.Red, Gui.Yellow, hp * 2f);
            var hpRect = new Rect(bx, r.y + 34f, bw - 96f, 22f);
            if (right) hpRect.x += 96f;
            Gui.Skew(hpRect, new Color(0f, 0f, 0f, 0.55f), 0.3f);
            if (hp > 0.001f)
            {
                float fw = hpRect.width * Mathf.Clamp01(hp);
                Gui.Skew(right ? new Rect(hpRect.xMax - fw, hpRect.y, fw, hpRect.height) : new Rect(hpRect.x, hpRect.y, fw, hpRect.height), hpColor, 0.3f);
            }
            Gui.Text(hpRect, f.Hp + " / " + f.Stats.MaxHp, 13, Gui.Paper);
            var pctRect = right ? new Rect(bx, r.y + 26f, 92f, 36f) : new Rect(bx + bw - 92f, r.y + 26f, 92f, 36f);
            Gui.Display(pctRect, Mathf.CeilToInt(hp * 100f) + "%", 32, hpColor, right ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight, 0.15f);

            float ult = f.Ult / (float)ChibiSimConst.UltMax;
            bool full = ult >= 1f;
            var ultRect = new Rect(bx, r.y + 62f, bw, 14f);
            var ultColor = full ? Color.Lerp(Gui.Gold, Color.white, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f)) : Color.Lerp(Gui.Accent2, Gui.Accent, ult);
            if (full) Gui.Glow(new Rect(ultRect.x - 10f, ultRect.y - 10f, ultRect.width + 20f, ultRect.height + 20f), new Color(1f, 0.8f, 0.2f, 0.5f));
            Gui.Meter(ultRect, ult, ultColor, 20);
            string ultLabel = full ? (showKeys ? "ULT READY - press " + Bindings.Label(keyPlayer, BindAction.Ultimate) : "ULT READY") : "ULT " + Mathf.FloorToInt(ult * 100f) + "%";
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
                string key = showKeys ? Bindings.Label(keyPlayer, (BindAction)((int)BindAction.Skill1 + s)) + " " : "";
                Gui.Text(sr, key + def.skillNames[s] + (cd > 0f ? " " + cdSec.ToString("0.0") : ""), 10, cd > 0f ? Gui.Muted : Gui.Paper);
            }

            var st = string.Join("  ", m.Statuses(i, StatusScratch));
            if (f.ComboHits >= 2) st = f.ComboHits + " HITS  " + st;
            if (st.Length > 0) Gui.Text(new Rect(bx, r.y + 106f, bw, 20f), st, 12, Gui.Gold, anchor);
        }

        static void Timer(ChibiMatch m)
        {
            var s = m.State;
            var r = new Rect(Gui.W * 0.5f - 62f, 12f, 124f, 72f);
            Gui.Skew(new Rect(r.x + 4f, r.y + 4f, r.width, r.height), new Color(0f, 0f, 0f, 0.4f), 0.15f);
            Gui.Skew(r, new Color(0.055f, 0.09f, 0.16f, 0.95f), 0.15f);
            Gui.Skew(new Rect(r.x, r.y, r.width * 0.5f, 4f), Gui.P1, 0.15f);
            Gui.Skew(new Rect(r.center.x, r.y, r.width * 0.5f, 4f), Gui.P2, 0.15f);
            float left = s.TimeLeftTicks / (float)ChibiSimConst.TickRate;
            string t = m.Config.training ? "∞" : Mathf.CeilToInt(left).ToString();
            Gui.Display(new Rect(r.x, r.y + 2f, r.width, 50f), t, 44, left < 10f && !m.Config.training ? Gui.Red : Gui.Paper, TextAnchor.MiddleCenter, 0.15f);
            Gui.Text(new Rect(r.x, r.y + 48f, r.width, 20f), m.StageName.ToUpper(), 10, Gui.Muted, TextAnchor.MiddleCenter, true, false);
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

            string who = Who(m, i);
            string header = open ? who + " · ULTIMATE READY" : detected ? who + " · MOVE DETECTED" : who + " · FAILED";
            Gui.Text(new Rect(r.x, r.y + 6f, r.width, 24f), header, 15, state);

            var fig = new Rect(r.x + 14f, r.y + 36f, 120f, 150f);
            Gui.Rect(fig, new Color(0f, 0.1f, 0.15f, 0.5f));
            Gui.Skeleton(fig, SimulatedPoseSource.Template(def.ultGesture, false), new Color(0.5f, 1f, 1f, 0.6f + 0.4f * pulse), 3f);

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
                Gui.Display(new Rect(info.x, info.y + 52f, info.width, 60f), n.ToString(), 46, state, TextAnchor.MiddleCenter, 0.1f);
                var tr = m.Tracker(i);
                int keyPlayer = m.Online ? 0 : i;
                if (tr != null) Gui.Text(new Rect(info.x, info.y + 118f, info.width, 30f), tr.IsLive ? "do the pose on camera" : "sim key: " + PoseKeys[keyPlayer][(int)def.ultGesture - 1], 11, Gui.Muted);
            }
            else
            {
                Gui.Text(new Rect(info.x, info.y + 52f, info.width, 60f), detected ? "✓" : "✗", 48, state);
            }

            var bar = new Rect(r.x + 14f, r.yMax - 34f, r.width - 28f, 10f);
            Gui.Bar(bar, open ? f.UltWindowTicks / (float)ChibiSimConst.UltWindowTicks : 1f, state, new Color(0f, 0f, 0f, 0.4f));
            Gui.Text(new Rect(r.x, r.yMax - 24f, r.width, 20f), m.PromptMessage(i), 12, Gui.Paper);
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

        static void Tags(ChibiMatch m)
        {
            for (int i = 0; i < 2; i++)
            {
                var p = Gui.WorldToGui(m.Center(i) + Vector2.up * 1.25f);
                var c = Gui.PlayerColor(i);
                string who = Who(m, i);
                float w = Gui.Measure(who, 13) + 16f;
                var tag = new Rect(p.x - w * 0.5f, p.y - 24f, w, 18f);
                Gui.Skew(tag, c, 0.3f);
                Gui.Text(tag, who, 13, Gui.Paper, TextAnchor.MiddleCenter, true, false);
                Gui.Chevron(new Rect(p.x - 6f, p.y - 4f, 12f, 8f), c);
            }
        }

        static void Popups()
        {
            foreach (var p in Fx.Popups)
            {
                var g = Gui.WorldToGui(p.pos);
                float a = 1f - Mathf.Clamp01((p.t - 0.6f) / 0.5f);
                float pop = 1f + Mathf.Max(0f, 0.3f - p.t) * 1.5f;
                int size = Mathf.RoundToInt(20f * p.size * pop);
                var rect = new Rect(g.x - 150f, g.y - 20f, 300f, 40f);
                if (p.comic)
                {
                    // Comic SFX lettering (character sheets): thick dark outline, slight tilt, colour fill on top.
                    var saved = GUI.matrix;
                    GUIUtility.RotateAroundPivot(p.tilt, g);
                    var ink = new Color(0.08f, 0.06f, 0.1f, a);
                    const float o = 2.5f;
                    for (int k = 0; k < 8; k++)
                    {
                        float ang = k * Mathf.PI / 4f;
                        Gui.Text(new Rect(rect.x + Mathf.Cos(ang) * o, rect.y + Mathf.Sin(ang) * o, rect.width, rect.height),
                                 p.text, size, ink);
                    }
                    Gui.Text(rect, p.text, size, new Color(p.color.r, p.color.g, p.color.b, a));
                    GUI.matrix = saved;
                    continue;
                }
                Gui.Text(rect, p.text, size, new Color(p.color.r, p.color.g, p.color.b, a));
            }
        }

        static void Hints(ChibiMatch m)
        {
            int me = m.Online ? m.LocalPlayer : 0;
            var tr = m.Tracker(me);
            string h = KeyHint(0) + (tr != null && tr.IsLive ? " · poses: cross arms block / arm out = fireball / hands up = jump"
                                                             : " · poses 1 cross / 2 punch / 3 raise · P slouch");
            if (!m.Online && !m.IsCpu(1)) h += "\n" + KeyHint(1) + " · poses 8/9/0";
            if (m.Config.training) h += "\nTRAINING: T fills your ultimate · dummy revives · Esc pause";
            Gui.Text(new Rect(260f, Gui.H - 58f, Gui.W - 520f, 54f), h, 11, new Color(1f, 1f, 1f, 0.55f), TextAnchor.LowerCenter, false);
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
            Gui.Box(r, noContest || w < 0 ? Gui.Gold : Gui.PlayerColor(w));
            string title = noContest ? "NO CONTEST" : w < 0 ? "DRAW!" : m.IsCpu(w) ? "CPU WINS" : m.Online ? (w == m.LocalPlayer ? "YOU WIN!" : "RIVAL WINS")
                         : CharNameUpper(m.Def(w).type) + " WINS!";
            Gui.Display(new Rect(r.x, r.y + 10f, r.width, 64f), title, 54, !noContest && w == m.LocalPlayer ? Gui.Gold : Gui.Paper, TextAnchor.MiddleCenter);
            string why = m.OnlineEndText;
            if (why.Length > 0) Gui.Text(new Rect(r.x, r.y + 70f, r.width, 20f), why, 14, Gui.Muted);
            else if (!noContest && w >= 0 && !m.Online && !m.IsCpu(w)) Gui.Text(new Rect(r.x, r.y + 70f, r.width, 20f), Who(m, w, true) + " takes the round", 14, Gui.Muted);

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
                float hpFrac = f.Stats.MaxHp > 0 ? f.HpU / ((float)f.Stats.MaxHp * ChibiCombatStats.HpScale) : 0f;
                Gui.Text(new Rect(col.x + 84f, col.y + 56f, 230f, 18f), "HP left " + Mathf.CeilToInt(hpFrac * 100f) + "%", 13, Gui.Muted, TextAnchor.MiddleLeft);
                var tr = m.Tracker(i);
                string stats = "Hits landed  " + f.HitsLanded + "\nFull combos  " + f.CombosLanded + "   ·   Best combo  " + f.MaxCombo +
                               "\nGesture ultimates  " + f.UltsUsed + "\nParries  " + f.Parries +
                               (tr == null ? "" : "\nGood posture  " + Mathf.RoundToInt(tr.PostureAverage * 100f) + "%");
                Gui.Text(new Rect(col.x + 14f, col.y + 82f, 300f, 104f), stats, 13, Gui.Paper, TextAnchor.UpperLeft, false);
            }

            var rw = new Rect(r.x + 30f, r.y + 298f, r.width - 60f, 160f);
            Gui.Rect(rw, new Color(0f, 0f, 0f, 0.3f));
            if (m.Config.training)
                Gui.Text(rw, "Training: no rewards", 16, Gui.Muted);
            else
            {
                Gui.Text(new Rect(rw.x + 14f, rw.y + 6f, rw.width, 24f), "REWARDS (" + (m.NameOf(m.LocalPlayer).Length > 0 ? ChibiSocialRules.BaseName(m.NameOf(m.LocalPlayer)) + "'s" : m.Online ? "your" : "P1") + " profile)", 14, Gui.Gold, TextAnchor.MiddleLeft);
                Gui.Text(new Rect(rw.x + 14f, rw.y + 32f, rw.width - 28f, 120f), string.Join("\n", m.RewardLog), 13, Gui.Paper, TextAnchor.UpperLeft, false);
                Gui.Text(new Rect(rw.x, rw.y + 6f, rw.width - 14f, 24f), "+" + m.CoinsEarned + " coins  (total " + m.GM.Save.coins + ")", 16, Gui.Gold, TextAnchor.MiddleRight);
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
