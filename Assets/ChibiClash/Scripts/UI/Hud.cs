using UnityEngine;

namespace ChibiClash
{
    /// <summary>In-match HUD (GDD 5): corner player panels, center timer, CV overlay and gesture prompts.</summary>
    public static class Hud
    {
        public enum Action { None, Resume, Rematch, CharacterSelect, Menu }

        static readonly string[][] PoseKeys = { new[] { "1", "2", "3" }, new[] { "8", "9", "0" } };

        public static Action Draw(MatchController m, GameManager gm)
        {
            var settings = gm.Save.settings;
            Tags(m);
            Popups();

            float ct = Time.unscaledTime - m.CinematicStart;
            if (m.CinematicFighter != null && ct < 1.5f) Cinematic(m.CinematicFighter, ct);

            PlayerPanel(m.F[0], new Rect(16f, 14f, 440f, 130f), false);
            PlayerPanel(m.F[1], new Rect(Gui.W - 456f, 14f, 440f, 130f), true);
            Timer(m);

            if (settings.showCvOverlay)
            {
                for (int i = 0; i < 2; i++)
                {
                    var f = m.F[i];
                    if (f.IsCpu) continue;
                    var r = i == 0 ? new Rect(16f, Gui.H - 196f, 230f, 172f) : new Rect(Gui.W - 246f, Gui.H - 196f, 230f, 172f);
                    Gui.CvPanel(r, f.Gestures, StateColor(f), i == 0 && settings.useWebcam, "P" + (i + 1) + " CAMERA · " + (f.Gestures.IsLive ? "LIVE" : "SIM"));
                }
            }

            bool both = PromptVisible(m.F[0]) && PromptVisible(m.F[1]);
            for (int i = 0; i < 2; i++)
            {
                var f = m.F[i];
                if (!PromptVisible(f)) continue;
                float x = both ? (i == 0 ? Gui.W * 0.5f - 330f : Gui.W * 0.5f + 30f) : Gui.W * 0.5f - 150f;
                GesturePrompt(f, new Rect(x, 175f, 300f, 230f));
            }

            Hints(m);

            if (!m.Running && !m.Finished)
            {
                bool fight = m.IntroTimer < 0.8f;
                float pulse = 1f + Mathf.Sin(Time.unscaledTime * 10f) * 0.04f;
                Gui.Text(new Rect(0f, Gui.H * 0.38f, Gui.W, 120f), fight ? "FIGHT!" : "READY...", Mathf.RoundToInt((fight ? 96 : 72) * pulse), fight ? Gui.Accent : Color.white);
                Gui.Text(new Rect(0f, Gui.H * 0.38f + 100f, Gui.W, 40f), m.Stage.name.ToUpper() + (m.Config.training ? "  ·  TRAINING" : ""), 22, Gui.Muted);
            }

            if (m.FlashAlpha > 0.01f)
            {
                var c = m.FlashColor;
                Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(c.r, c.g, c.b, m.FlashAlpha * 0.5f));
            }

            if (m.Paused) return Pause();
            if (m.Finished && Time.unscaledTime - m.FinishedTime > 1.3f) return Results(m);
            return Action.None;
        }

        static bool PromptVisible(Fighter f) => f.UltWindow || f.Prompt == PromptState.Detected || f.Prompt == PromptState.Failed;

        static Color StateColor(Fighter f)
        {
            if (f.UltWindow) return Gui.Yellow;
            if (f.Prompt == PromptState.Detected) return Gui.Green;
            if (f.Prompt == PromptState.Failed) return Gui.Red;
            if (f.Gestures.Stable != Gesture.None) return Gui.Accent2;
            return new Color(0.4f, 0.4f, 0.5f);
        }

        static void PlayerPanel(Fighter f, Rect r, bool right)
        {
            var def = f.Def;
            Gui.Rect(r, Gui.Panel, 14f);
            Gui.Frame(r, new Color(def.color.r, def.color.g, def.color.b, 0.6f), 2f, 14f);
            float px = right ? r.xMax - 100f : r.x + 10f;
            Gui.Pfp(new Rect(px, r.y + 10f, 90f, 90f), def, f.Skin);
            string who = f.IsCpu ? "CPU" : "P" + (f.Index + 1);
            Gui.Text(new Rect(px, r.y + 100f, 90f, 24f), who, 16, f.Index == 0 ? Gui.Accent : Gui.Accent2);

            float bx = right ? r.x + 12f : r.x + 110f;
            float bw = 318f;
            var anchor = right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            Gui.Text(new Rect(bx, r.y + 6f, bw, 22f), def.heroName.ToUpper() + "  <size=13><color=#b5aed0>" + def.className + " · " + f.Weapon.name + " · " + def.passiveNames[f.Passive] + "</color></size>", 18, Color.white, anchor);

            float hp = f.Hp / f.MaxHp;
            var hpColor = hp > 0.5f ? Color.Lerp(Gui.Yellow, Gui.Green, (hp - 0.5f) * 2f) : Color.Lerp(Gui.Red, Gui.Yellow, hp * 2f);
            var hpRect = new Rect(bx, r.y + 32f, bw - 96f, 24f);
            if (right) hpRect.x += 96f;
            Gui.Bar(hpRect, hp, hpColor, new Color(0f, 0f, 0f, 0.5f), 6f);
            Gui.Text(hpRect, Mathf.CeilToInt(f.Hp) + " / " + f.MaxHp, 13, Color.white);
            var pctRect = right ? new Rect(bx, r.y + 26f, 92f, 36f) : new Rect(bx + bw - 92f, r.y + 26f, 92f, 36f);
            Gui.Text(pctRect, Mathf.CeilToInt(hp * 100f) + "%", 30, hpColor, right ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight);

            float ult = f.Ult / Fighter.UltMax;
            bool full = ult >= 1f;
            var ultRect = new Rect(bx, r.y + 62f, bw, 14f);
            var ultColor = full ? Color.Lerp(Gui.Gold, Color.white, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f)) : Color.Lerp(Gui.Accent2, Gui.Accent, ult);
            if (full) Gui.Glow(new Rect(ultRect.x - 10f, ultRect.y - 10f, ultRect.width + 20f, ultRect.height + 20f), new Color(1f, 0.8f, 0.2f, 0.5f));
            Gui.Bar(ultRect, ult, ultColor, new Color(0f, 0f, 0f, 0.5f), 7f);
            string ultLabel = full ? (f.IsCpu ? "ULT READY" : "ULT READY - press " + Bindings.Label(f.Index, BindAction.Ultimate)) : "ULT " + Mathf.FloorToInt(ult * 100f) + "%";
            Gui.Text(ultRect, ultLabel, 11, full ? new Color(0.15f, 0.1f, 0.05f) : Color.white, TextAnchor.MiddleCenter, true, !full);

            float sw = (bw - 8f) / 3f;
            for (int k = 0; k < 3; k++)
            {
                var sr = new Rect(bx + k * (sw + 4f), r.y + 82f, sw, 22f);
                if (k >= def.SkillCount) { Gui.Rect(sr, new Color(0f, 0f, 0f, 0.25f), 5f); continue; }
                float cd = f.SkillCd[k] / def.skillCooldowns[k];
                Gui.Rect(sr, new Color(def.color.r, def.color.g, def.color.b, 0.35f), 5f);
                if (cd > 0f) Gui.Rect(new Rect(sr.x, sr.y, sr.width * cd, sr.height), new Color(0f, 0f, 0f, 0.6f), 5f);
                string key = f.IsCpu ? "" : Bindings.Label(f.Index, (BindAction)((int)BindAction.Skill1 + k)) + " ";
                Gui.Text(sr, key + def.skillNames[k] + (cd > 0f ? " " + f.SkillCd[k].ToString("0.0") : ""), 10, cd > 0f ? Gui.Muted : Color.white);
            }

            var st = string.Join("  ", f.Statuses());
            if (f.ComboCount >= 2) st = f.ComboCount + " HITS  " + st;
            Gui.Text(new Rect(bx, r.y + 106f, bw, 20f), st, 12, Gui.Gold, anchor);
        }

        static void Timer(MatchController m)
        {
            var r = new Rect(Gui.W * 0.5f - 60f, 12f, 120f, 70f);
            Gui.Rect(r, Gui.Panel, 14f);
            Gui.Frame(r, Gui.Accent, 2f, 14f);
            string t = m.Config.training ? "∞" : Mathf.CeilToInt(m.TimeLeft).ToString();
            Gui.Text(new Rect(r.x, r.y - 4f, r.width, 56f), t, 40, m.TimeLeft < 10f && !m.Config.training ? Gui.Red : Color.white);
            Gui.Text(new Rect(r.x, r.y + 44f, r.width, 22f), m.Stage.name.ToUpper(), 11, Gui.Muted);
        }

        static void GesturePrompt(Fighter f, Rect r)
        {
            Color state = f.UltWindow ? Gui.Yellow : f.Prompt == PromptState.Detected ? Gui.Green : Gui.Red;
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f);
            Gui.Glow(new Rect(r.x - 40f, r.y - 40f, r.width + 80f, r.height + 80f), new Color(0.3f, 0.9f, 1f, 0.25f + 0.2f * pulse));
            Gui.Rect(r, new Color(0.05f, 0.25f, 0.35f, 0.55f), 16f);
            for (float y = r.y + 4f; y < r.yMax - 4f; y += 5f)
                Gui.Rect(new Rect(r.x + 6f, y, r.width - 12f, 1f), new Color(0.5f, 1f, 1f, 0.07f));
            Gui.Frame(r, state, 3f, 16f);

            string who = f.IsCpu ? "CPU" : "P" + (f.Index + 1);
            string header = f.UltWindow ? who + " · ULTIMATE READY" : f.Prompt == PromptState.Detected ? who + " · MOVE DETECTED" : who + " · FAILED";
            Gui.Text(new Rect(r.x, r.y + 6f, r.width, 24f), header, 15, state);

            var fig = new Rect(r.x + 14f, r.y + 36f, 120f, 150f);
            Gui.Rect(fig, new Color(0f, 0.1f, 0.15f, 0.5f), 8f);
            Gui.Skeleton(fig, SimulatedPoseSource.Template(f.Def.ultGesture, false), new Color(0.5f, 1f, 1f, 0.6f + 0.4f * pulse), 3f);

            var info = new Rect(r.x + 144f, r.y + 36f, r.width - 156f, 150f);
            Gui.Text(new Rect(info.x, info.y, info.width, 44f), ClassDef.GestureName(f.Def.ultGesture), 17, Color.white);
            if (f.UltWindow)
            {
                int n = Mathf.Clamp(Mathf.CeilToInt(f.UltWindowLeft), 1, 3);
                float frac = f.UltWindowLeft - Mathf.Floor(f.UltWindowLeft);
                float ring = 60f + 20f * frac;
                var c = new Rect(info.center.x - ring * 0.5f, info.y + 82f - ring * 0.5f, ring, ring);
                Gui.Frame(c, state, 4f, ring * 0.5f);
                Gui.Text(new Rect(info.x, info.y + 52f, info.width, 60f), n.ToString(), 46, state);
                if (!f.IsCpu) Gui.Text(new Rect(info.x, info.y + 118f, info.width, 30f), f.Gestures.IsLive ? "do the pose on camera" : "sim key: " + PoseKeys[f.Index][(int)f.Def.ultGesture - 1], 11, Gui.Muted);
            }
            else
            {
                Gui.Text(new Rect(info.x, info.y + 52f, info.width, 60f), f.Prompt == PromptState.Detected ? "✓" : "✗", 48, state);
            }

            var bar = new Rect(r.x + 14f, r.yMax - 34f, r.width - 28f, 10f);
            Gui.Bar(bar, f.UltWindow ? f.UltWindowLeft / Fighter.UltWindowDuration : 1f, state, new Color(0f, 0f, 0f, 0.4f), 5f);
            Gui.Text(new Rect(r.x, r.yMax - 24f, r.width, 20f), f.PromptMessage, 12, Color.white);
        }

        static void Cinematic(Fighter f, float t)
        {
            float inT = Mathf.Clamp01(t / 0.2f);
            float outT = Mathf.Clamp01((1.5f - t) / 0.3f);
            float k = Mathf.Min(inT, outT);
            float bar = 80f * k;
            Gui.Rect(new Rect(0f, 0f, Gui.W, bar), Color.black);
            Gui.Rect(new Rect(0f, Gui.H - bar, Gui.W, bar), Color.black);

            var c = f.Skin.goldCinematic ? Gui.Gold : f.Skin.ultFx;
            float slide = (1f - inT) * Gui.W;
            var band = new Rect(-slide, Gui.H * 0.62f, Gui.W, 90f);
            Gui.Rect(band, new Color(c.r * 0.3f, c.g * 0.3f, c.b * 0.3f, 0.85f * k));
            Gui.Rect(new Rect(band.x, band.y, band.width, 4f), new Color(c.r, c.g, c.b, k));
            Gui.Rect(new Rect(band.x, band.yMax - 4f, band.width, 4f), new Color(c.r, c.g, c.b, k));
            string star = f.Skin.goldCinematic ? "★ " : "";
            Gui.Text(new Rect(band.x, band.y + 4f, band.width, 56f), star + f.Def.ultName + (f.Skin.goldCinematic ? " ★" : ""), 46, new Color(1f, 1f, 1f, k));
            Gui.Text(new Rect(band.x, band.y + 56f, band.width, 28f), f.Def.heroName.ToUpper() + " · " + f.Def.ultDesc, 15, new Color(c.r, c.g, c.b, k));
        }

        static void Tags(MatchController m)
        {
            foreach (var f in m.F)
            {
                var p = Gui.WorldToGui(f.Center + Vector2.up * 1.25f);
                string who = f.IsCpu ? "CPU" : "P" + (f.Index + 1);
                var c = f.Index == 0 ? Gui.Accent : Gui.Accent2;
                Gui.Text(new Rect(p.x - 40f, p.y - 22f, 80f, 20f), who, 14, c);
                Gui.Text(new Rect(p.x - 40f, p.y - 8f, 80f, 16f), "▼", 10, c);
            }
        }

        static void Popups()
        {
            foreach (var p in Fx.Popups)
            {
                var g = Gui.WorldToGui(p.pos);
                float a = 1f - Mathf.Clamp01((p.t - 0.6f) / 0.5f);
                float pop = 1f + Mathf.Max(0f, 0.3f - p.t) * 1.5f;
                Gui.Text(new Rect(g.x - 150f, g.y - 20f, 300f, 40f), p.text, Mathf.RoundToInt(20f * p.size * pop), new Color(p.color.r, p.color.g, p.color.b, a));
            }
        }

        static void Hints(MatchController m)
        {
            string h = KeyHint(0) + (m.F[0].Gestures.IsLive ? " · poses: cross arms block / arm out = fireball / hands up = jump"
                                                          : " · poses 1 cross / 2 punch / 3 raise · P slouch");
            if (!m.F[1].IsCpu) h += "\n" + KeyHint(1) + " · poses 8/9/0";
            if (m.Config.training) h += "\nTRAINING: T fills your ultimate · dummy revives · Esc pause";
            Gui.Text(new Rect(260f, Gui.H - 58f, Gui.W - 520f, 54f), h, 11, new Color(1f, 1f, 1f, 0.55f), TextAnchor.LowerCenter, false);
        }

        static string KeyHint(int p)
        {
            string L(BindAction a) => Bindings.Label(p, a);
            return "P" + (p + 1) + ": " + L(BindAction.Left) + "/" + L(BindAction.Right) + " move · " + L(BindAction.Jump) + " jump · " +
                   L(BindAction.Light) + " light · " + L(BindAction.Heavy) + " heavy · " + L(BindAction.Block) + " block · " +
                   L(BindAction.Dash) + " dash · " + L(BindAction.Skill1) + " " + L(BindAction.Skill2) + " " + L(BindAction.Skill3) + " skills · " +
                   L(BindAction.Ultimate) + " ult";
        }

        static Action Pause()
        {
            Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(0f, 0f, 0f, 0.55f));
            var r = new Rect(Gui.W * 0.5f - 170f, Gui.H * 0.5f - 130f, 340f, 260f);
            Gui.Rect(r, Gui.Panel, 18f);
            Gui.Frame(r, Gui.Accent, 2f, 18f);
            Gui.Text(new Rect(r.x, r.y + 16f, r.width, 50f), "PAUSED", 36, Color.white);
            if (Gui.Button(new Rect(r.x + 40f, r.y + 90f, 260f, 50f), "RESUME", Gui.Accent2)) return Action.Resume;
            if (Gui.Button(new Rect(r.x + 40f, r.y + 160f, 260f, 50f), "QUIT TO MENU", Gui.Accent)) return Action.Menu;
            return Action.None;
        }

        static Action Results(MatchController m)
        {
            Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(0f, 0f, 0f, 0.6f));
            var r = new Rect(Gui.W * 0.5f - 360f, Gui.H * 0.5f - 270f, 720f, 540f);
            Gui.Rect(r, Gui.Panel, 20f);
            Gui.Frame(r, Gui.Gold, 3f, 20f);
            string title = m.Winner < 0 ? "DRAW!" : (m.F[m.Winner].IsCpu ? "CPU WINS" : "PLAYER " + (m.Winner + 1) + " WINS!");
            Gui.Text(new Rect(r.x, r.y + 14f, r.width, 60f), title, 44, m.Winner == 0 ? Gui.Gold : Color.white);

            for (int i = 0; i < 2; i++)
            {
                var f = m.F[i];
                var col = new Rect(r.x + 30f + i * 340f, r.y + 84f, 320f, 190f);
                Gui.Rect(col, Gui.PanelLight, 12f);
                Gui.Pfp(new Rect(col.x + 10f, col.y + 10f, 64f, 64f), f.Def, f.Skin);
                Gui.Text(new Rect(col.x + 84f, col.y + 10f, 230f, 30f), (f.IsCpu ? "CPU" : "P" + (i + 1)) + " · " + f.Def.heroName, 18, Color.white, TextAnchor.MiddleLeft);
                Gui.Text(new Rect(col.x + 84f, col.y + 38f, 230f, 24f), "HP left " + Mathf.CeilToInt(f.Hp / f.MaxHp * 100f) + "%", 14, Gui.Muted, TextAnchor.MiddleLeft);
                string stats = "Hits landed  " + f.HitsLanded + "\nFull combos  " + f.CombosLanded + "   ·   Best combo  " + f.MaxCombo +
                               "\nGesture ultimates  " + f.UltsUsed + "\nParries  " + f.Parries +
                               (f.IsCpu ? "" : "\nGood posture  " + Mathf.RoundToInt(f.Gestures.PostureAverage * 100f) + "%");
                Gui.Text(new Rect(col.x + 14f, col.y + 82f, 300f, 104f), stats, 13, Color.white, TextAnchor.UpperLeft, false);
            }

            var rw = new Rect(r.x + 30f, r.y + 286f, r.width - 60f, 160f);
            Gui.Rect(rw, new Color(0f, 0f, 0f, 0.3f), 12f);
            if (m.Config.training)
                Gui.Text(rw, "Training: no rewards", 16, Gui.Muted);
            else
            {
                Gui.Text(new Rect(rw.x + 14f, rw.y + 6f, rw.width, 24f), "REWARDS (P1 profile)", 14, Gui.Gold, TextAnchor.MiddleLeft);
                Gui.Text(new Rect(rw.x + 14f, rw.y + 32f, rw.width - 28f, 120f), string.Join("\n", m.RewardLog), 13, Color.white, TextAnchor.UpperLeft, false);
                Gui.Text(new Rect(rw.x, rw.y + 6f, rw.width - 14f, 24f), "+" + m.CoinsEarned + " coins  (total " + m.GM.Save.coins + ")", 16, Gui.Gold, TextAnchor.MiddleRight);
            }

            float by = r.yMax - 76f;
            if (Gui.Button(new Rect(r.x + 30f, by, 210f, 52f), "REMATCH", Gui.Accent)) return Action.Rematch;
            if (Gui.Button(new Rect(r.x + 255f, by, 210f, 52f), "CHARACTERS", Gui.Accent2)) return Action.CharacterSelect;
            if (Gui.Button(new Rect(r.x + 480f, by, 210f, 52f), "MAIN MENU", Gui.Muted)) return Action.Menu;
            return Action.None;
        }
    }
}
