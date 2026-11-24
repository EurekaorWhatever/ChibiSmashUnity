using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Loading screen, home menu, top navigation bar, Play (Vs Online / Matchmaking / Vs CPU), the Vs Online play-style screen,
    /// Arcade / Training / Collection / Settings pages and the shared screen chrome (ported from the copy-ghibli-smash
    /// fork, decision UI-1, with our five characters).
    /// </summary>
    public partial class GameManager
    {
        // Loc keys, drawn through Loc.T
        static readonly string[] HomeItems = { "home.play", "home.training", "home.arcade", "home.collection", "home.settings", "home.exit" };
        // Loc keys, drawn through Loc.T
        static readonly string[] HomeDesc =
        {
            "home.play_desc",
            "home.training_desc",
            "home.arcade_desc",
            "home.collection_desc",
            "home.settings_desc",
            "home.exit_desc",
        };

        // Loc keys, drawn through Loc.T
        static readonly string[] Tips =
        {
            "tips.1",
            "tips.2",
            "tips.3",
            "tips.4",
            "tips.5",
            "tips.6",
            "tips.7",
        };

        // Loc keys, drawn through Loc.T
        static readonly string[] StatNames = { "stats.power", "stats.speed", "stats.range", "stats.endurance", "stats.recovery" };

        int homeSel;
        /// <summary>Home menu index of EXIT GAME (the last item).</summary>
        static int HomeExit => HomeItems.Length - 1;
        float homeSelAnim;
        int featured;
        float featuredSince;
        int loadTip = -1;

        static Color WithA(Color c, float a) => new Color(c.r, c.g, c.b, a);

        static int Stat(CharacterInfo c, int i)
        {
            switch (i)
            {
                case 0: return c.Power;
                case 1: return c.Speed;
                case 2: return c.Range;
                case 3: return c.Endurance;
                default: return c.Recovery;
            }
        }

        // ---------------------------------------------------------------- shared chrome

        /// <summary>Navy field with hatching, halftone corners, drifting speed lines and grain.</summary>
        void Backdrop()
        {
            float t = Time.unscaledTime;
            var full = new Rect(0f, 0f, W, H);
            Gui.Rect(full, Gui.Navy);
            Gui.Skew(new Rect(-W * 0.12f, -20f, W * 0.4f, H + 40f), new Color(0.075f, 0.12f, 0.22f), 0.35f);
            Gui.Skew(new Rect(W * 0.78f, -20f, W * 0.4f, H + 40f), new Color(0.075f, 0.12f, 0.22f), 0.35f);
            Gui.Hatch(full, new Color(1f, 1f, 1f, 0.018f), 22f, t * 0.04f);
            Gui.HalftoneFade(new Rect(0f, H * 0.58f, W * 0.42f, H * 0.42f), WithA(Gui.P1, 0.12f));
            Gui.HalftoneFade(new Rect(W * 0.58f, 0f, W * 0.42f, H * 0.36f), WithA(Gui.P2, 0.12f), true);
            Gui.SpeedLines(full, new Color(1f, 1f, 1f, 0.03f), 20, 380f, 3);
            Gui.Grain(full, 0.05f);
            Gui.FadeUp(new Rect(0f, H * 0.62f, W, H * 0.38f), new Color(0f, 0f, 0f, 0.3f));
        }

        void Logo(Rect r, int size)
        {
            float w1 = Gui.Measure("CHIBI", size, true);
            Gui.Display(new Rect(r.x, r.y, w1 + 12f, r.height), "CHIBI", size, Gui.P1);
            Gui.Display(new Rect(r.x + w1 + size * 0.18f, r.y, r.width, r.height), "CLASH", size, Gui.Paper, TextAnchor.MiddleLeft, 0.2f, Gui.P2);
        }

        /// <summary>Backdrop and logo behind an onboarding / text dialog.</summary>
        void DialogBackdrop(string title)
        {
            Backdrop();
            Logo(new Rect(40f, 22f, 600f, 70f), 60);
            Gui.Text(new Rect(46f, 90f, 600f, 22f), title, 14, Gui.Gold, TextAnchor.MiddleLeft, true, false);
        }

        /// <summary>Top navigation: logo, slanted tabs with LB / RB prompts, player profile and coins.</summary>
        void NavBar()
        {
            Gui.Rect(new Rect(0f, 0f, W, 52f), WithA(Gui.Ink, 0.85f));
            Gui.Rect(new Rect(0f, 51f, W, 1f), Gui.Steel);
            Gui.Skew(new Rect(-10f, 50f, 230f, 3f), Gui.P1, 1f);
            Logo(new Rect(20f, 6f, 210f, 40f), 30);

            var owner = NavOwner(page);
            float x = 236f;
            x += Gui.KeyCap(x, 16f, Gui.Key("Q", "LB"), 20f) + 10f;
            for (int i = 0; i < NavNames.Length; i++)
            {
                float w = Gui.Measure(Loc.T(NavNames[i]), 15) + 30f;
                var r = new Rect(x, 9f, w, 34f);
                bool active = NavPages[i] == owner;
                bool click = Gui.Hit(r, out bool hot);
                if (active)
                {
                    Gui.Skew(new Rect(r.x + 3f, r.y + 3f, r.width, r.height), new Color(0f, 0f, 0f, 0.4f));
                    Gui.Skew(r, Gui.P1);
                    Gui.Skew(new Rect(r.x, r.yMax - 3f, r.width, 3f), Gui.Gold);
                }
                else if (hot) Gui.Skew(r, Gui.Steel);
                if (hot) Gui.SkewFrame(r, Gui.Paper, 1.5f);
                Gui.Text(r, Loc.T(NavNames[i]), 15, active || hot ? Gui.Paper : Gui.Muted, TextAnchor.MiddleCenter, true, false);
                if (i < NavNames.Length - 1) Gui.Skew(new Rect(r.xMax + 4f, 16f, 2f, 20f), Gui.Steel, 0.35f);
                if (click && NavPages[i] != page) Go(NavPages[i]);
                x += w + 10f;
            }
            Gui.KeyCap(x, 16f, Gui.Key("E", "RB"), 20f);
            ProfileChip(new Rect(W - 252f, 8f, 236f, 36f));
            LanguageButton();   // just under the profile chip, on every page with this bar
        }

        /// <summary>Who we are (player name, W / L) and coins; click to change the name.</summary>
        void ProfileChip(Rect r)
        {
            bool click = Gui.Hit(r, out bool hot);
            Gui.Skew(r, hot ? new Color(0.1f, 0.16f, 0.28f, 0.95f) : new Color(0.06f, 0.1f, 0.18f, 0.95f), 0.3f);
            Gui.SkewFrame(r, hot ? Gui.Paper : Gui.Steel, 1f, 0.3f);
            var tag = new Rect(r.x + 10f, r.y + 6f, 34f, r.height - 12f);
            Gui.Skew(tag, Gui.P1, 0.3f);
            Gui.Display(tag, "1P", 17, Gui.Paper, TextAnchor.MiddleCenter, 0.12f, Color.clear);
            string shown = DisplayedName();
            Gui.Text(new Rect(r.x + 52f, r.y + 2f, 112f, 18f), shown.Length > 0 ? shown : Loc.T("profile.set_name"), 13, Gui.Paper, TextAnchor.MiddleLeft, true, false);
            Gui.Text(new Rect(r.x + 52f, r.y + 17f, 112f, 16f), hot ? Loc.T("profile.edit_name") : Loc.T("profile.win_loss", Save.wins, Save.losses), 11, hot ? Gui.Gold : Gui.Muted, TextAnchor.MiddleLeft, false, false);
            var cr = new Rect(r.xMax - 76f, r.y, 70f, r.height);
            Gui.Skew(new Rect(cr.x, cr.y + 12f, 12f, 12f), Gui.Gold, 0.6f);
            Gui.Display(new Rect(cr.x + 16f, cr.y, 56f, cr.height), Save.coins.ToString(), 20, Gui.Gold, TextAnchor.MiddleLeft, 0.12f);
            if (click) OpenNameDialog();
        }

        /// <summary>Page heading (index tag, slanted title, kicker) and the animated content area below it. Pair with EndPage.</summary>
        Rect BeginPage(string index, string title, string kicker)
        {
            float k = Gui.EaseOut(Since / 0.35f);
            float x = 40f - (1f - k) * 30f;
            var idx = new Rect(x, 70f, 44f, 38f);
            Gui.Skew(idx, Gui.P1, 0.25f);
            Gui.Display(idx, index, 22, Gui.Paper, TextAnchor.MiddleCenter, 0.12f, Color.clear);
            float tw = Gui.Measure(title, 46, true);
            Gui.Display(new Rect(x + 58f, 60f, tw + 30f, 58f), title, 46, Gui.Paper);
            float kx = x + 58f + tw + 34f;
            Gui.Text(new Rect(kx, 74f, 620f, 28f), kicker, 13, Gui.Gold, TextAnchor.MiddleLeft, true, false);
            Gui.Rect(new Rect(kx, 104f, Mathf.Max(0f, W - 40f - kx), 1f), WithA(Gui.Steel, 0.9f));
            Gui.Alpha = k;
            Gui.PushOffset(new Vector2((1f - k) * 40f, 0f));
            return new Rect(40f, 128f, W - 80f, H - 168f);
        }

        /// <summary>COMMON / RARE / EPIC / LEGENDARY in the current language.</summary>
        static string RarityUpper(Rarity r) => Loc.T("rarity." + r.ToString().ToLowerInvariant());

        /// <summary>A skin's display name ("Mint Pop") in the current language; Skin.id is "Class_key", the Loc key is skin.key.</summary>
        static string SkinName(Skin s) => Loc.T("skin." + s.id.Substring(s.id.IndexOf('_') + 1));

        void EndPage()
        {
            Gui.Pop();
            Gui.Alpha = 1f;
        }

        void NavHints(string where)
        {
            Gui.HintBar(where, Gui.Key("WASD", "D-PAD"), Loc.T("hint.move"), Gui.Key("SPACE", "A"), Loc.T("hint.select"), Gui.Key("ESC", "B"), page == Page.Home ? Loc.T("hint.exit") : Loc.T("common.back"), Gui.Key("Q / E", "LB / RB"), Loc.T("hint.tabs"));
        }

        // ---------------------------------------------------------------- LOADING

        void LoadingView()
        {
            if (loadTip < 0) loadTip = Random.Range(0, Tips.Length);
            var ch = CharacterRoster.All[loadFeatured];
            float t = Time.unscaledTime, since = Since;
            var full = new Rect(0f, 0f, W, H);

            Gui.Rect(full, Gui.Navy);
            Gui.Hatch(full, new Color(1f, 1f, 1f, 0.02f), 20f, t * 0.08f);
            Gui.Skew(new Rect(W * 0.5f, -20f, W * 0.62f, H + 40f), new Color(0.075f, 0.125f, 0.23f), 0.35f);
            Gui.Skew(new Rect(W * 0.455f, -20f, 18f, H + 40f), Gui.P1, 0.35f);
            Gui.Skew(new Rect(W * 0.455f + 28f, -20f, 6f, H + 40f), Gui.P2, 0.35f);
            Gui.HalftoneFade(new Rect(W * 0.5f, 0f, W * 0.5f, H * 0.5f), WithA(ch.Primary, 0.2f), true);
            Gui.HalftoneFade(new Rect(0f, H * 0.62f, W * 0.4f, H * 0.38f), WithA(Gui.P1, 0.12f));
            Gui.SpeedLines(new Rect(0f, 60f, W, H - 170f), new Color(1f, 1f, 1f, 0.06f), 26, 700f, 11);
            Gui.Display(new Rect(W * 0.34f, H * 0.08f, W, 260f), Hud.CharNameUpper(ch.Class), 250, new Color(1f, 1f, 1f, 0.045f), TextAnchor.MiddleLeft, 0.2f, Color.clear);

            // character art slides in from the right
            float k = Gui.EaseOut(since / 0.7f);
            float s = Mathf.Min(H * 0.9f, W * 0.48f);
            CharacterArt.DrawBody(new Rect(W - s * 0.95f + (1f - k) * 140f, H - s - 74f, s, s), ch, k);

            Gui.FadeUp(new Rect(W * 0.45f, H - 280f, W * 0.55f, 190f), WithA(Gui.Navy, 0.9f));
            Gui.Display(new Rect(W * 0.45f, H - 236f, W * 0.55f - 48f, 92f), Hud.CharNameUpper(ch.Class), 88, Gui.Paper, TextAnchor.MiddleRight);
            Gui.Display(new Rect(W * 0.45f, H - 152f, W * 0.55f - 50f, 30f), ch.RoleText.ToUpper() + "  ·  " + ch.Weapon.ToUpper(), 22, Gui.Gold, TextAnchor.MiddleRight, 0.18f);

            // logo and headline, staggered in from the left
            float a1 = Gui.EaseOut((since - 0.05f) / 0.45f), a2 = Gui.EaseOut((since - 0.18f) / 0.45f);
            Gui.Alpha = a1;
            Logo(new Rect(48f - (1f - a1) * 40f, 34f, 640f, 92f), 82);
            Gui.Text(new Rect(54f, 124f, 520f, 20f), Loc.T("home.tagline"), 13, Gui.Muted, TextAnchor.MiddleLeft, true, false);
            Gui.Alpha = a2;
            float hx = 48f - (1f - a2) * 60f;
            Gui.Display(new Rect(hx, H * 0.33f, W * 0.55f, 84f), Loc.T("loading.prepare"), 78, Gui.Paper);
            Gui.Display(new Rect(hx + 22f, H * 0.33f + 74f, W * 0.55f, 84f), Loc.T("loading.for_battle"), 78, Gui.P1);
            Gui.Text(new Rect(hx + 28f, H * 0.33f + 168f, 520f, 44f), ch.StyleText, 15, Gui.Muted, TextAnchor.UpperLeft, false);
            Gui.Alpha = 1f;

            var tip = new Rect(48f, H - 168f, 560f, 60f);
            var tag = new Rect(tip.x, tip.y, 52f, 22f);
            Gui.Skew(tag, Gui.Gold, 0.3f);
            Gui.Text(tag, Loc.T("loading.tip"), 13, Gui.Ink, TextAnchor.MiddleCenter, true, false);
            Gui.Text(new Rect(tip.x + 64f, tip.y, tip.width - 64f, 60f), Loc.T(Tips[loadTip]), 14, Gui.Paper, TextAnchor.UpperLeft, false);

            LoadBar(new Rect(48f, H - 74f, W - 96f, 22f));
        }

        /// <summary>Slanted segmented progress bar showing the real loading progress (never ahead of it).</summary>
        void LoadBar(Rect r)
        {
            float p = loadShown;
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f);
            for (int i = 0; i < 3; i++)
                Gui.Chevron(new Rect(r.x + i * 11f, r.y - 22f, 9f, 14f), WithA(Gui.P1, 0.35f + 0.65f * Mathf.Clamp01(pulse * 3f - i)));
            Gui.Text(new Rect(r.x + 42f, r.y - 30f, 700f, 24f), Loc.T("loading.status", loadIndex >= loadSteps.Count ? Loc.T("loading.ready_upper") : Loc.T("loading.loading"), LoadLabel.ToUpper()), 13, Gui.Muted, TextAnchor.MiddleLeft, true, false);
            Gui.Display(new Rect(r.xMax - 220f, r.y - 56f, 220f, 52f), Mathf.FloorToInt(p * 100f) + "%", 48, Gui.Paper, TextAnchor.MiddleRight);

            const float k = 0.6f;
            Gui.Skew(new Rect(r.x + 4f, r.y + 4f, r.width, r.height), new Color(0f, 0f, 0f, 0.5f), k);
            Gui.Skew(r, Gui.Steel, k);
            float fw = r.width * p;
            if (fw > 1f)
            {
                var fill = new Rect(r.x, r.y, fw, r.height);
                Gui.Skew(fill, Gui.P1, k);
                if (fw > 16f) Gui.Hatch(new Rect(fill.x + 7f, fill.y, fw - 14f, fill.height), new Color(1f, 1f, 1f, 0.16f), 12f, -Time.unscaledTime * 1.5f);
                Gui.Glow(new Rect(fill.xMax - 34f, r.y - 22f, 68f, r.height + 44f), WithA(Gui.P2, 0.35f + 0.25f * pulse));
                Gui.Skew(new Rect(fill.xMax - 8f, r.y - 4f, 8f, r.height + 8f), Gui.P2, k);
            }
            for (int i = 1; i < 20; i++) Gui.Skew(new Rect(r.x + r.width * i / 20f, r.y, 2f, r.height), WithA(Gui.Navy, 0.75f), k);
            Gui.SkewFrame(r, new Color(1f, 1f, 1f, 0.25f), 1f, k);
        }

        // ---------------------------------------------------------------- HOME (main menu)

        void HomeView()
        {
            float since = Since, now = Time.unscaledTime;
            Backdrop();
            if (now - featuredSince > 8f)
            {
                featured = (featured + 1) % CharacterRoster.All.Length;
                featuredSince = now;
            }
            HomeSplash(CharacterRoster.All[featured], Gui.EaseOut((now - featuredSince) / 0.6f));
            NavBar();

            float la = Gui.EaseOut(since / 0.45f);
            Gui.Alpha = la;
            Logo(new Rect(40f - (1f - la) * 40f, 66f, 600f, 80f), 70);
            Gui.Text(new Rect(46f, 140f, 520f, 20f), Loc.T("home.tagline"), 13, Gui.Gold, TextAnchor.MiddleLeft, true, false);
            Gui.Alpha = 1f;

            const float y0 = 182f, step = 50f;
            // selection slab glides between items
            float sa = Gui.EaseOut((since - 0.15f) / 0.3f);
            var slab = new Rect(-30f, y0 + homeSelAnim * step + 3f, 30f + 470f * sa, step - 6f);
            Gui.Skew(new Rect(slab.x + 5f, slab.y + 5f, slab.width, slab.height), new Color(0f, 0f, 0f, 0.4f));
            Gui.Skew(slab, homeSel == HomeExit ? Gui.Steel : Gui.P1);
            Gui.Skew(new Rect(slab.xMax + 8f, slab.y, 10f, slab.height), Gui.P2);
            Gui.Skew(new Rect(slab.xMax + 24f, slab.y, 4f, slab.height), Gui.Paper);

            for (int i = 0; i < HomeItems.Length; i++)
            {
                float ap = Gui.EaseOut((since - 0.12f - i * 0.05f) / 0.32f);
                var r = new Rect(40f - (1f - ap) * 70f, y0 + i * step, 470f, step);
                if (i == 0) Gui.PreferNext();
                bool click = Gui.Hit(r, out bool hot);
                if (hot) homeSel = i;
                bool sel = homeSel == i;
                Gui.Alpha = ap;
                Gui.Text(new Rect(r.x, r.y, 36f, r.height), (i + 1).ToString("00"), 13, sel ? Gui.Paper : Gui.Muted, TextAnchor.MiddleLeft, true, false);
                float shift = sel ? 14f : 0f;
                Gui.Display(new Rect(r.x + 40f + shift, r.y, 440f, r.height), Loc.T(HomeItems[i]), sel ? 38 : 32, sel ? Gui.Paper : (i == HomeExit ? Gui.Muted : WithA(Gui.Paper, 0.78f)), TextAnchor.MiddleLeft, 0.2f, sel ? Gui.Ink : Color.clear);
                Gui.Alpha = 1f;
                if (click) HomeSelect(i);
            }

            float da = Gui.EaseOut((since - 0.5f) / 0.3f);
            Gui.Alpha = da;
            float dy = y0 + HomeItems.Length * step + 14f;
            Gui.Rect(new Rect(40f, dy, 380f, 1f), Gui.Steel);
            Gui.Chevron(new Rect(40f, dy + 12f, 8f, 12f), Gui.Gold);
            Gui.Text(new Rect(58f, dy + 6f, 500f, 24f), Loc.T(HomeDesc[homeSel]), 14, Gui.Muted, TextAnchor.MiddleLeft, false, false);
            Gui.Alpha = 1f;

            NavHints(Loc.T("home.hint"));
        }

        /// <summary>Featured character: slanted colour panel with the art breaking out of its top edge.</summary>
        void HomeSplash(CharacterInfo ch, float k)
        {
            float px = W * 0.56f;
            var panel = new Rect(px, 70f, W - px + 80f, H - 116f);
            Gui.Skew(new Rect(panel.x + 8f, panel.y + 8f, panel.width, panel.height), new Color(0f, 0f, 0f, 0.35f), 0.28f);
            Gui.Skew(panel, new Color(0.085f, 0.14f, 0.255f, 0.97f), 0.28f);
            Gui.Skew(new Rect(panel.x - 30f, panel.y, 16f, panel.height), ch.Primary, 0.28f);
            Gui.Skew(new Rect(panel.x - 8f, panel.y, 5f, panel.height), ch.Accent, 0.28f);
            Gui.HalftoneFade(new Rect(px + 40f, panel.y, W - px, panel.height * 0.5f), WithA(ch.Primary, 0.22f * k), true);
            Gui.SpeedLines(new Rect(px + 60f, panel.y + 20f, W - px - 60f, panel.height - 40f), WithA(Gui.Paper, 0.07f), 14, 520f, 5);
            Gui.Display(new Rect(px - 30f, panel.y + 30f, W, 230f), Hud.CharNameUpper(ch.Class), 220, new Color(1f, 1f, 1f, 0.05f * k), TextAnchor.MiddleLeft, 0.2f, Color.clear);

            float s = Mathf.Min(H * 0.86f, W * 0.44f);
            CharacterArt.DrawBody(new Rect(W - s * 0.96f + (1f - k) * 90f, H - s - 52f, s, s), ch, k);

            Gui.FadeUp(new Rect(px, H - 250f, W - px, 210f), WithA(Gui.Navy, 0.92f));
            Gui.Alpha = k;
            Gui.Display(new Rect(px, H - 214f, W - px - 44f, 96f), Hud.CharNameUpper(ch.Class), 92, Gui.Paper, TextAnchor.MiddleRight);
            Gui.Display(new Rect(px, H - 128f, W - px - 48f, 30f), ch.RoleText.ToUpper(), 24, Gui.Gold, TextAnchor.MiddleRight, 0.18f);
            Gui.Text(new Rect(px, H - 98f, W - px - 52f, 20f), Loc.T("home.weapon_ult", ch.Weapon.ToUpper(), ClassDef.Get(ch.Class).ultName), 12, Gui.Muted, TextAnchor.MiddleRight, true, false);
            Gui.Alpha = 1f;
            int n = CharacterRoster.All.Length;
            for (int i = 0; i < n; i++)
                Gui.Skew(new Rect(W - 52f - (n - 1 - i) * 22f, H - 66f, 16f, 5f), i == featured ? Gui.Paper : WithA(Gui.Paper, 0.25f), 0.6f);
        }

        void HomeSelect(int i)
        {
            switch (i)
            {
                case 0: Go(Page.Play); break;
                case 1: Go(Page.Training); break;
                case 2: Go(Page.Arcade); break;
                case 3: Go(Page.Collection); break;
                case 4: Go(Page.Settings); break;
                default: OpenExitPrompt(); break;
            }
        }

        void ExitModal()
        {
            Gui.Rect(new Rect(0f, 0f, W, H), new Color(0f, 0f, 0f, 0.62f));
            var r = new Rect(W * 0.5f - 270f, H * 0.5f - 120f, 540f, 240f);
            Gui.Skew(new Rect(-40f, r.y + 30f, W + 80f, 180f), WithA(Gui.P1, 0.18f), 0f);
            Gui.Box(r, Gui.P1);
            Gui.Display(new Rect(r.x + 30f, r.y + 24f, r.width - 60f, 64f), Loc.T("exit.title"), 54, Gui.Paper);
            Gui.Text(new Rect(r.x + 32f, r.y + 92f, r.width - 64f, 40f), Loc.T("exit.saved_note"), 14, Gui.Muted, TextAnchor.UpperLeft, false);
            Gui.PreferNext();
            if (Gui.Button(new Rect(r.x + 30f, r.yMax - 78f, 220f, 52f), Loc.T("exit.stay"), Gui.P2, 18)) CloseExitPrompt();
            if (Gui.Button(new Rect(r.xMax - 250f, r.yMax - 78f, 220f, 52f), Loc.T("home.exit_upper"), Gui.P1, 18)) QuitGame();
        }

        // ---------------------------------------------------------------- PLAY (Vs Online / Matchmaking / Vs CPU)

        static string OnlineLockedMsg => Loc.T("menu.online_locked");
        static string NoInternetMsg => Loc.T("menu.no_internet");
        static string FourPlayerMsg => Loc.T("menu.four_player_soon");

        /// <summary>The internet is reachable (Wi-Fi or mobile data). Vs Online and Matchmaking need it; Vs CPU never does.</summary>
        static bool InternetUp => Application.internetReachability != NetworkReachability.NotReachable;

        void PlayView()
        {
            Backdrop();
            NavBar();
            bool onlineOk = PlayerIdentity.OnlineAllowed(Save.ageBand);
            bool online = InternetUp;
            var a = BeginPage("01", Loc.T("nav.play"), Loc.T("play.kicker"));
            float qw = 380f;
            float cw = (a.width - qw - 30f - 44f) / 3f;
            float ch = Mathf.Min(a.height - 16f, 430f);
            // Vs Online and Matchmaking: 13+ and an internet connection, else a locked card that explains why when clicked.
            string net = !onlineOk ? Loc.T("play.age_locked_tag") : !online ? Loc.T("play.no_internet_tag") : Loc.T("play.1p_vs_1p");
            bool onlineLocked = !onlineOk || !online;
            if (ModeCard(0, new Rect(a.x, a.y + 8f, cw, ch), Loc.T("play.online_card"), net,
                         Loc.T("play.online_desc"), Gui.Gold,   // two lines: clears the footer
                         onlineLocked ? Loc.T("play.locked") : Loc.T("play.choose_style"), onlineLocked))
            {
                if (!onlineOk) Deny(OnlineLockedMsg);
                else if (!online) Deny(NoInternetMsg);
                else OpenPlayStyle();
            }
            if (ModeCard(1, new Rect(a.x + cw + 22f, a.y + 8f, cw, ch), Loc.T("play.matchmaking_card"), !onlineOk ? Loc.T("play.age_locked_tag") : !online ? Loc.T("play.no_internet_tag") : Loc.T("play.rooms_tag"),
                         Loc.T("play.matchmaking_desc"), Gui.P2, onlineLocked ? Loc.T("play.locked") : Loc.T("play.create_join"), onlineLocked))
            {
                if (!onlineOk) Deny(OnlineLockedMsg);
                else if (!online) Deny(NoInternetMsg);
                else OpenRoomsPage();
            }
            // VS CPU runs entirely on this device: no internet, no sign-in, nothing online is touched.
            if (ModeCard(2, new Rect(a.x + 2f * (cw + 22f), a.y + 8f, cw, ch), Loc.T("play.cpu_card"), Loc.T("play.cpu_tag"), Loc.T("play.cpu_desc"), Gui.P1, Loc.T("play.com_difficulty", Loc.T(DifficultyUpper[Save.settings.cpuDifficulty]))))
                BeginSelect(true, false);
            Quests(new Rect(a.xMax - qw, a.y + 8f, qw, a.height - 8f));
            EndPage();
            NavHints(Loc.T("nav.play"));
        }

        // ---------------------------------------------------------------- PLAY STYLE (after Vs Online)

        /// <summary>Opens the Vs Online play-style screen (Solo / 2v2 / Battle Royale).</summary>
        void OpenPlayStyle() => Enter(Page.PlayStyle);

        /// <summary>
        /// Vs Online: Solo (1v1), 2v2 (Team) and Battle Royale. Only Solo is playable: the fight engine has two fighters,
        /// so the two 4-player styles are locked cards marked COMING SOON. Solo starts QUICK MATCH (a player near you, no
        /// room). Room invites from friends pop up on this page. Right panel: the connection state, and BACK.
        /// </summary>
        void PlayStyleView()
        {
            if (!PlayerIdentity.OnlineAllowed(Save.ageBand)) { Enter(Page.Play); return; }   // age gate (13+), defence in depth
            Backdrop();
            NavBar();
            bool net = InternetUp;
            var a = BeginPage("01", Loc.T("online.title"), Loc.T("online.kicker"));
            float qw = 380f;
            float cw = (a.width - qw - 30f - 44f) / 3f;
            float ch = Mathf.Min(a.height - 16f, 430f);

            bool soloLocked = !net;
            if (ModeCard(0, new Rect(a.x, a.y + 8f, cw, ch), Loc.T("online.solo_card"), soloLocked ? Loc.T("play.no_internet_tag") : Loc.T("play.solo_1v1_tag"),
                         Loc.T("online.solo_desc"),
                         Gui.Gold, soloLocked ? Loc.T("play.locked") : Loc.T("online.quick_match"), soloLocked))
            {
                if (soloLocked) Deny(NoInternetMsg);
                else StartQuickMatch();
            }
            if (ModeCard(1, new Rect(a.x + cw + 22f, a.y + 8f, cw, ch), Loc.T("play.team_card"), Loc.T("play.team_tag"),
                         Loc.T("online.team_desc"), Gui.P2, Loc.T("play.coming_soon"), true))
                Deny(FourPlayerMsg);
            if (ModeCard(2, new Rect(a.x + 2f * (cw + 22f), a.y + 8f, cw, ch), Loc.T("play.royale_card"), Loc.T("play.royale_tag"),
                         Loc.T("online.royale_desc"), Gui.P1, Loc.T("play.coming_soon"), true))
                Deny(FourPlayerMsg);

            // Right: the connection, where invites come from, and BACK to the Play menu.
            var info = new Rect(a.xMax - qw, a.y + 8f, qw, a.height - 8f);
            var sc = net ? Gui.Green : Gui.P1;
            Gui.Box(info, sc);
            float x = info.x + 22f, w = info.width - 44f;
            Gui.Text(new Rect(x, info.y + 18f, w, 20f), Loc.T("online.connection"), 13, Gui.Muted, TextAnchor.MiddleLeft);
            float blink = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 4f);
            Gui.Skew(new Rect(x, info.y + 50f, 14f, 30f), WithA(sc, 0.5f + 0.5f * blink), 0.3f);
            Gui.Display(new Rect(x + 26f, info.y + 40f, w, 50f), net ? Loc.T("loading.ready_upper") : Loc.T("online.offline"), 42, sc);
            string body = !net ? Loc.T("online.no_internet_body")
                        : Loc.T("online.rollback_body");
            Gui.Text(new Rect(x, info.y + 100f, w, 110f), body, 14, Gui.Paper, TextAnchor.UpperLeft, false);
            Gui.Text(new Rect(x, info.y + 214f, w, 60f), Loc.T("play.four_player_later"), 13, Gui.Muted, TextAnchor.UpperLeft, false);
            Gui.Text(new Rect(x, info.y + 270f, w, 44f), Loc.T("online.invites_note"), 12,
                     Gui.Gold, TextAnchor.UpperLeft, false);
            if (Gui.Button(new Rect(x, info.yMax - 70f, w, 50f), Loc.T("common.back"), Gui.Steel, 18)) { UiSound.Back(); Go(Page.Play); }
            EndPage();
            NavHints(Loc.T("online.hint"));
        }

        /// <summary>Tall slanted mode card; lifts and streaks when hot. A locked card still reports clicks (to explain why).</summary>
        bool ModeCard(int i, Rect r, string title, string tag, string desc, Color c, string footer, bool locked = false)
        {
            bool enabled = !locked;
            if (i == 0) Gui.PreferNext();
            bool click = Gui.Hit(r, out bool hot);
            bool press = Gui.Pressing(hot);
            var b = new Rect(r.x, r.y + (hot ? (press ? -2f : -8f) : 0f), r.width, r.height);
            const float k = 0.08f;
            var baseFill = new Color(0.07f, 0.11f, 0.2f, 0.97f);
            Gui.Skew(new Rect(b.x + 7f, b.y + 7f, b.width, b.height), new Color(0f, 0f, 0f, 0.45f), k);
            Gui.Skew(b, !enabled ? new Color(0.06f, 0.08f, 0.12f, 0.92f) : hot ? Color.Lerp(baseFill, c, 0.2f) : baseFill, k);
            Gui.Skew(new Rect(b.x, b.y, b.width, 6f), enabled ? c : WithA(Gui.Muted, 0.4f), k);
            Gui.HalftoneFade(new Rect(b.x + 14f, b.y + 10f, b.width - 28f, b.height * 0.42f), WithA(c, !enabled ? 0.05f : hot ? 0.3f : 0.14f), true);
            if (hot && enabled) Gui.SpeedLines(new Rect(b.x + 16f, b.y + 24f, b.width - 32f, b.height - 60f), WithA(c, 0.35f), 10, 520f, i * 17 + 1);
            Gui.Display(new Rect(b.x, b.y + 6f, b.width - 22f, 120f), (i + 1).ToString("00"), 110, WithA(c, enabled ? 0.2f : 0.06f), TextAnchor.MiddleRight, 0.2f, Color.clear);

            var chip = new Rect(b.x + 24f, b.y + 30f, Gui.Measure(tag, 13) + 22f, 22f);
            Gui.Skew(chip, enabled ? c : Gui.Disabled, 0.3f);
            bool lightChip = enabled && 0.299f * c.r + 0.587f * c.g + 0.114f * c.b > 0.6f;
            Gui.Text(chip, tag, 13, lightChip ? Gui.Ink : Gui.Paper, TextAnchor.MiddleCenter, true, false);

            Gui.Display(new Rect(b.x + 24f, b.yMax - 200f, b.width - 30f, 116f), title, 46, enabled ? Gui.Paper : Gui.Muted, TextAnchor.LowerLeft);
            Gui.Text(new Rect(b.x + 24f, b.yMax - 80f, b.width - 44f, 44f), desc, 13, Gui.Muted, TextAnchor.UpperLeft, false);
            Gui.Rect(new Rect(b.x + 24f, b.yMax - 34f, b.width - 48f, 1f), Gui.Steel);
            Gui.Text(new Rect(b.x + 24f, b.yMax - 32f, b.width - 48f, 24f), footer, 12, enabled ? Gui.Gold : Gui.P1, TextAnchor.MiddleLeft, true, false);
            if (hot)
            {
                if (enabled)
                {
                    Gui.Text(new Rect(b.x + 24f, b.yMax - 32f, b.width - 62f, 24f), Loc.T("home.start"), 12, Gui.Paper, TextAnchor.MiddleRight, true, false);
                    Gui.Chevron(new Rect(b.xMax - 34f, b.yMax - 26f, 8f, 12f), Gui.Paper);
                }
                Gui.SkewFrame(b, enabled ? Gui.Paper : Gui.Muted, 2f, k);
            }
            if (!enabled) Gui.Hatch(new Rect(b.x + 18f, b.y + 8f, b.width - 36f, b.height - 16f), new Color(1f, 1f, 1f, 0.03f), 14f);
            return click;
        }

        void Quests(Rect q)
        {
            Gui.Box(q, Gui.Gold);
            Gui.Display(new Rect(q.x + 18f, q.y + 10f, 200f, 34f), Loc.T("quests.title"), 28, Gui.Gold, TextAnchor.MiddleLeft, 0.18f);
            Gui.Text(new Rect(q.x + 18f, q.y + 12f, q.width - 36f, 30f), Loc.T("profile.win_loss", Save.wins, Save.losses), 14, Gui.Muted, TextAnchor.MiddleRight);
            float y = q.y + 56f;
            foreach (var quest in Save.quests)
            {
                if (y > q.yMax - 110f) break;
                Gui.Text(new Rect(q.x + 18f, y, q.width - 100f, 22f), quest.DescText, 14, Gui.Paper, TextAnchor.MiddleLeft, false);
                Gui.Text(new Rect(q.x + 18f, y, q.width - 36f, 22f), "+" + quest.reward, 15, Gui.Gold, TextAnchor.MiddleRight);
                Gui.Meter(new Rect(q.x + 18f, y + 26f, q.width - 36f, 8f), (float)quest.progress / quest.target, Gui.P2, 16);
                Gui.Text(new Rect(q.x + 18f, y + 36f, q.width - 36f, 16f), (quest.completions > 0 ? Loc.T("quests.progress_completed", quest.progress, quest.target, quest.completions) : Loc.T("quests.progress", quest.progress, quest.target)), 11, Gui.Muted, TextAnchor.MiddleLeft, false);
                y += 62f;
            }
            Gui.Text(new Rect(q.x + 18f, q.yMax - 54f, q.width - 36f, 46f), Loc.T("quests.coins_note"), 12, Gui.Muted, TextAnchor.MiddleLeft, false);
        }

        // ---------------------------------------------------------------- ARCADE

        void ArcadeView()
        {
            Backdrop();
            NavBar();
            var a = BeginPage("03", Loc.T("wipe.arcade"), Loc.T("arcade.kicker"));
            var box = new Rect(a.x, a.y + 8f, 470f, a.height - 8f);
            Gui.Box(box, Gui.P1);
            float x = box.x + 26f, w = box.width - 52f, y = box.y + 22f;
            Gui.Display(new Rect(x, y, w, 50f), Loc.T("arcade.mode"), 44, Gui.Paper);
            y += 60f;
            for (int i = 0; i < 5; i++)
            {
                string rule;
                switch (i)
                {
                    case 0: rule = Loc.T("arcade.rule_fight_all", ArcadeRounds); break;
                    case 1: rule = Loc.T("arcade.rule_stages"); break;
                    case 2: rule = Loc.T("arcade.rule_retry"); break;
                    case 3: rule = Loc.T("arcade.rule_difficulty", Loc.T(DifficultyUpper[Save.settings.cpuDifficulty])); break;
                    default: rule = Loc.T("arcade.rule_rewards"); break;
                }
                Gui.Skew(new Rect(x, y + 7f, 8f, 8f), Gui.P1, 0.4f);
                Gui.Text(new Rect(x + 18f, y, w - 18f, 40f), rule, 14, Gui.Paper, TextAnchor.UpperLeft, false);
                y += 40f;
            }
            Gui.PreferNext();
            if (Gui.Button(new Rect(x, box.yMax - 82f, w, 58f), Loc.T("arcade.start"), Gui.P1, 22)) StartArcade();

            float lx = box.xMax + 40f, lw = a.xMax - lx;
            float rh = (a.height - 8f - (ArcadeRounds - 1) * 12f) / ArcadeRounds;
            for (int i = 0; i < ArcadeRounds; i++)
            {
                float ap = Gui.EaseOut((Since - 0.1f - i * 0.06f) / 0.35f);
                var row = new Rect(lx + (1f - ap) * 60f + i * 10f, a.y + 8f + i * (rh + 12f), lw - ArcadeRounds * 10f, rh);
                bool final = i == ArcadeRounds - 1;
                Gui.Alpha = ap;
                Gui.Skew(new Rect(row.x + 5f, row.y + 5f, row.width, row.height), new Color(0f, 0f, 0f, 0.4f), 0.2f);
                Gui.Skew(row, new Color(0.07f, 0.11f, 0.2f, 0.96f), 0.2f);
                Gui.Skew(new Rect(row.x, row.y, 10f, row.height), final ? Gui.Gold : Gui.P1, 0.2f);
                Gui.Display(new Rect(row.x + 26f, row.y, 200f, row.height), final ? Loc.T("arcade.final") : Loc.T("arcade.round", i + 1), 30, final ? Gui.Gold : Gui.Paper);
                int st = ArcadeStage(i);
                var prev = new Rect(row.x + 230f, row.y + 8f, (row.height - 16f) * 2f, row.height - 16f);
                StagePreview(prev, st, 1f);
                Gui.Text(new Rect(prev.xMax + 16f, row.y, 220f, row.height), StageLabel(st, 216f, 15), 15, Gui.Paper, TextAnchor.MiddleLeft);
                Gui.Display(new Rect(row.x, row.y, row.width - 30f, row.height), Loc.T("arcade.rival_unknown"), 26, Gui.Muted, TextAnchor.MiddleRight);
                Gui.Alpha = 1f;
            }
            EndPage();
            NavHints(Loc.T("wipe.arcade"));
        }

        // ---------------------------------------------------------------- TRAINING / CV SETUP

        static readonly Gesture[] TrainingGestures = { Gesture.CrossArms, Gesture.PunchForward, Gesture.RaiseHands };
        // Loc keys, drawn through Loc.T
        static readonly string[] TrainingUses = { "training.use_block", "training.use_fireball", "training.use_superjump" };

        void TrainingView()
        {
            Backdrop();
            NavBar();
            var a = BeginPage("02", Loc.T("nav.training"), Loc.T("training.kicker"));
            TrainingTab(a);
            EndPage();
            if (MenuInput.UsingGamepad) Gui.HintBar(Loc.T("training.hint"), "D-PAD", Loc.T("hint.move"), "A", Loc.T("hint.select"), "B", Loc.T("common.back"), "LB / RB", Loc.T("hint.tabs"));
            else Gui.HintBar(Loc.T("training.hint"), "1 2 3", Loc.T("training.simulate_pose"), "P", Loc.T("training.slouch"), "SPACE", Loc.T("hint.select"), "ESC", Loc.T("common.back"), "Q / E", Loc.T("hint.tabs"));
        }

        void TrainingTab(Rect a)
        {
            var feed = new Rect(a.x, a.y, 520f, 400f);
            var st = menuTracker.Stable != Gesture.None ? Gui.Green : Gui.Gold;
            Gui.CvPanel(feed, menuTracker, st, Save.settings.useWebcam, Loc.T("training.cv_setup_source", menuTracker.Source.Name));

            var p = new Rect(a.x + 545f, a.y, a.width - 545f, a.height);
            Gui.Box(p, Gui.P2);
            float x = p.x + 20f, w = p.width - 40f, y = p.y + 14f;
            Gui.Display(new Rect(x, y, w, 30f), Loc.T("training.cv"), 26, Gui.Paper, TextAnchor.MiddleLeft, 0.18f);
            y += 36f;
            string status = Loc.T("training.status", Save.settings.useWebcam ? WebcamFeed.Status : Loc.T("training.off"), menuPose.Status(),
                                  menuTracker.Stable == Gesture.None ? Loc.T("training.none") : ClassDef.GestureName(menuTracker.Stable)) +
                            Loc.T("training.status_posture", Mathf.RoundToInt(menuTracker.Posture * 100f), menuTracker.Validator.Reason);
            Gui.Text(new Rect(x, y, w, 70f), status, 13, Gui.Paper, TextAnchor.UpperLeft, false);
            y += 78f;

            Gui.Text(new Rect(x, y, w, 24f), menuTracker.IsLive ? Loc.T("training.gestures_live") : Loc.T("training.gestures_sim"), 14, Gui.Gold, TextAnchor.MiddleLeft);
            y += 30f;
            float gw = (w - 20f) / 3f;
            for (int i = 0; i < 3; i++)
            {
                var g = new Rect(x + i * (gw + 10f), y, gw, 190f);
                bool active = menuTracker.Stable == TrainingGestures[i];
                Gui.Rect(g, active ? new Color(0.1f, 0.35f, 0.25f, 0.9f) : Gui.PanelLight);
                Gui.Frame(g, active ? Gui.Green : Gui.Steel, 2f);
                if (active) Gui.Rect(new Rect(g.x, g.y, g.width, 3f), Gui.Green);
                Gui.Skeleton(new Rect(g.x + gw * 0.5f - 50f, g.y + 8f, 100f, 110f), SimulatedPoseSource.TemplateCached(TrainingGestures[i], false), active ? Gui.Green : Gui.P2, 2.5f);
                Gui.Text(new Rect(g.x, g.y + 120f, gw, 22f), "[" + (i + 1) + "] " + ClassDef.GestureName(TrainingGestures[i]), 13, Gui.Paper);
                Gui.Text(new Rect(g.x + 6f, g.y + 144f, gw - 12f, 40f), Loc.T(TrainingUses[i]), 11, Gui.Muted, TextAnchor.UpperCenter, false);
            }
            y += 205f;
            Gui.Text(new Rect(x, y, w, 20f), Loc.T("training.slouch_note"), 12, Gui.Muted, TextAnchor.MiddleLeft, false);

            if (Gui.Button(new Rect(a.x, a.y + 415f, 250f, 48f), Save.settings.useWebcam ? Loc.T("training.camera_on") : Loc.T("training.camera_off"), Gui.P2, 16, true, Save.settings.useWebcam))
            {
                Save.settings.useWebcam = !Save.settings.useWebcam;
                WebcamFeed.SetEnabled(Save.settings.useWebcam);
                Save.Save();
            }
            Gui.PreferNext();
            if (Gui.Button(new Rect(a.x + 270f, a.y + 415f, 250f, 48f), Loc.T("training.practice_dummy"), Gui.Gold, 16))
                BeginSelect(true, true);
        }

        // ---------------------------------------------------------------- CHARACTER COLLECTION (armory / gacha)

        readonly List<Skin> armorySkins = new List<Skin>();
        int armorySkinsFor = -1;

        void CollectionView()
        {
            Backdrop();
            NavBar();
            var a = BeginPage("04", Loc.T("nav.collection"), Loc.T("collection.kicker"));
            ArmoryTab(a);
            EndPage();
            Gui.HintBar(Loc.T("collection.hint"), Gui.Key("WASD", "D-PAD"), Loc.T("hint.move"), Gui.Key("SPACE", "A"), Loc.T("hint.select"), Gui.Key("ESC", "B"), Loc.T("common.back"), Gui.Key("Q / E", "LB / RB"), Loc.T("hint.tabs"));
        }

        void ArmoryTab(Rect a)
        {
            // The five characters, each with their portrait.
            for (int i = 0; i < CharacterRoster.All.Length; i++)
            {
                var c = CharacterRoster.All[i];
                if (c.Class == armoryClass) Gui.PreferNext();
                var br = new Rect(a.x, a.y + i * 62f, 180f, 54f);
                if (Gui.Button(br, "      " + Hud.CharNameUpper(c.Class), c.Primary, 15, true, armoryClass == c.Class))
                    armoryClass = c.Class;
                var port = new Rect(br.x + 8f, br.y + 5f, 44f, 44f);
                CharacterArt.DrawPortrait(port, c);
                Gui.Frame(port, c.Accent, 1f);
            }

            if (armorySkinsFor != (int)armoryClass)
            {
                armorySkinsFor = (int)armoryClass;
                armorySkins.Clear();
                foreach (var sk in Skin.All) if (sk.cls == armoryClass) armorySkins.Add(sk);
            }
            var grid = new Rect(a.x + 200f, a.y, 600f, a.height);
            var equipped = Save.Equipped(armoryClass);
            var cdef = ClassDef.Get(armoryClass);
            for (int i = 0; i < armorySkins.Count; i++)
            {
                var s = armorySkins[i];
                var r = new Rect(grid.x + (i % 3) * 200f, grid.y + (i / 3) * 236f, 188f, 224f);
                if (r.yMax > a.yMax + 4f) break;
                bool owned = Save.owned.Contains(s.id);
                var rc = Skin.RarityColor(s.rarity);
                Gui.Rect(new Rect(r.x + 4f, r.y + 4f, r.width, r.height), new Color(0f, 0f, 0f, 0.35f));
                Gui.Rect(r, owned ? Gui.PanelLight : Gui.Panel);
                Gui.Halftone(new Rect(r.x, r.y, r.width, 120f), new Color(rc.r, rc.g, rc.b, owned ? 0.12f : 0.05f), 9f);
                Gui.Frame(r, s == equipped ? Gui.Green : new Color(rc.r, rc.g, rc.b, 0.8f), s == equipped ? 3f : 1f);
                Gui.Skew(new Rect(r.x + 10f, r.y - 4f, 44f, 7f), rc, 0.6f);
                if (owned) Gui.Pfp(new Rect(r.x + 44f, r.y + 14f, 100f, 100f), cdef, s);
                else
                {
                    Gui.Circle(new Rect(r.x + 44f, r.y + 14f, 100f, 100f), new Color(0.04f, 0.06f, 0.11f));
                    Gui.Display(new Rect(r.x + 44f, r.y + 14f, 100f, 100f), "?", 60, Gui.Muted, TextAnchor.MiddleCenter, 0.15f);
                }
                Gui.Display(new Rect(r.x, r.y + 118f, r.width, 26f), s.name.ToUpper(), 20, Gui.Paper, TextAnchor.MiddleCenter, 0.15f);
                Gui.Text(new Rect(r.x, r.y + 144f, r.width, 20f), RarityUpper(s.rarity) + (s.goldCinematic ? Loc.T("collection.gold_cinematic_suffix") : ""), 11, rc);
                if (owned)
                {
                    if (s == equipped) Gui.Text(new Rect(r.x, r.y + 172f, r.width, 36f), Loc.T("collection.equipped"), 14, Gui.Green);
                    else if (Gui.Button(new Rect(r.x + 34f, r.y + 172f, 120f, 36f), Loc.T("collection.equip"), Gui.P2, 14)) Save.Equip(s);
                }
                else Gui.Text(new Rect(r.x, r.y + 172f, r.width, 36f), Loc.T("collection.pull_to_unlock"), 12, Gui.Muted);
            }

            var g = new Rect(a.x + 820f, a.y, a.width - 820f, a.height);
            Gui.Box(g, Gui.Gold);
            float x = g.x + 18f, w = g.width - 36f, y = g.y + 14f;
            Gui.Display(new Rect(x, y, w, 32f), Loc.T("collection.banner"), 26, Gui.Gold, TextAnchor.MiddleCenter, 0.18f);
            y += 36f;
            Gui.Text(new Rect(x, y, w, 50f), Loc.T("collection.banner_note"), 12, Gui.Muted, TextAnchor.UpperCenter, false);
            y += 52f;
            Gui.Text(new Rect(x, y, w, 20f), Loc.T("collection.odds"), 11, Gui.Paper);
            y += 28f;
            if (Gui.Button(new Rect(x, y, w * 0.5f - 5f, 34f), Loc.T("collection.all_fighters"), Gui.P2, 12, true, bannerAll)) bannerAll = true;
            if (Gui.Button(new Rect(x + w * 0.5f + 5f, y, w * 0.5f - 5f, 34f), Loc.T("collection.only", Hud.CharNameUpper(armoryClass)), Gui.P2, 12, true, !bannerAll)) bannerAll = false;
            y += 46f;
            FighterClass? banner = bannerAll ? (FighterClass?)null : armoryClass;
            if (Gui.Button(new Rect(x, y, w, 46f), Loc.T("collection.pull1", SaveData.PullCost), Gui.P1, 16, Save.coins >= SaveData.PullCost))
                DoPull(1, banner);
            y += 54f;
            if (Gui.Button(new Rect(x, y, w, 46f), Loc.T("collection.pull10", SaveData.TenPullCost), Gui.Gold, 16, Save.coins >= SaveData.TenPullCost))
                DoPull(10, banner);
            y += 60f;
            foreach (var p in lastPull)
            {
                if (y > g.yMax - 20f) break;
                Gui.Text(new Rect(x, y, w, 18f), Hud.CharName(p.skin.cls) + " · " + SkinName(p.skin) + (p.duplicate ? Loc.T("collection.dup_suffix") : Loc.T("collection.new_suffix")), 12, Skin.RarityColor(p.skin.rarity), TextAnchor.MiddleLeft, false);
                y += 18f;
            }
        }

        void DoPull(int n, FighterClass? banner)
        {
            lastPull = Save.Pull(n, banner);
            foreach (var p in lastPull)
                if (p.skin.rarity >= Rarity.Epic && !p.duplicate)
                    Toast(Loc.T("collection.pull_toast", RarityUpper(p.skin.rarity), Hud.CharName(p.skin.cls), SkinName(p.skin)));
        }

        // ---------------------------------------------------------------- SETTINGS

        void SettingsView()
        {
            Backdrop();
            NavBar();
            var a = BeginPage("05", Loc.T("nav.settings"), Loc.T("settings.kicker"));
            SettingsTab(a);
            EndPage();
            Gui.HintBar(listening ? Loc.T("settings.listening_hint") : Loc.T("nav.settings"), Gui.Key("WASD", "D-PAD"), Loc.T("hint.move"), Gui.Key("SPACE", "A"), Loc.T("hint.select"), Gui.Key("ESC", "B"), Loc.T("common.back"), Gui.Key("Q / E", "LB / RB"), Loc.T("hint.tabs"));
        }

        void SettingsTab(Rect a)
        {
            var s = Save.settings;
            var p = new Rect(a.x, a.y, 620f, a.height);
            Gui.Box(p, Gui.P1);
            float x = p.x + 24f, y = p.y + 20f;

            Gui.Text(new Rect(x, y, 200f, 40f), Loc.T("settings.match_time"), 16, Gui.Paper, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(x + 220f, y, 350f, 40f), Loc.T("settings.match_time_value", MatchController.MatchSeconds), 16, Gui.Paper, TextAnchor.MiddleLeft);
            y += 56f;

            Gui.Text(new Rect(x, y, 200f, 40f), Loc.T("settings.cpu_difficulty"), 16, Gui.Paper, TextAnchor.MiddleLeft);
            for (int i = 1; i <= 3; i++)
            {
                if (s.cpuDifficulty == i) Gui.PreferNext();
                if (Gui.Button(new Rect(x + 220f + (i - 1) * 120f, y, 110f, 40f), Loc.T(DifficultyUpper[i]), Gui.P2, 15, true, s.cpuDifficulty == i))
                {
                    s.cpuDifficulty = i;
                    Save.Save();
                }
            }
            y += 56f;

            if (Toggle(x, ref y, Loc.T("settings.camera_tracking"), ref s.useWebcam)) WebcamFeed.SetEnabled(s.useWebcam);
            Toggle(x, ref y, Loc.T("settings.cv_overlay"), ref s.showCvOverlay);
            Toggle(x, ref y, Loc.T("settings.precheck"), ref s.preMatchCheck);
            Toggle(x, ref y, Loc.T("settings.screen_shake"), ref s.screenShake);

            // Volumes live in ChibiAudio (it persists them itself); 10 % steps with - / +, or click the meter.
            float master = Netcode.ChibiAudio.MasterVolume, sfx = Netcode.ChibiAudio.SfxVolume;
            if (VolumeSlider(x, ref y, Loc.T("settings.master_volume"), ref master)) Netcode.ChibiAudio.MasterVolume = master;
            if (VolumeSlider(x, ref y, Loc.T("settings.sfx_volume"), ref sfx)) Netcode.ChibiAudio.SfxVolume = sfx;

            y += 4f;
            // Two clicks within 3 s (item 22). RESET PROFILE keeps the name, age and sign-in (user decision 2026-11-17).
            bool profileArmed = Time.unscaledTime < resetProfileArmedUntil;
            bool profileLive = profileArmed && Time.unscaledTime > resetProfileArmedUntil - ResetArmSeconds + ResetGraceSeconds;
            if (Gui.Button(new Rect(x, y, 260f, 44f), profileArmed ? Loc.T("settings.sure_reset_profile") : Loc.T("settings.reset_profile"), Gui.Red, profileArmed ? 13 : 15))
            {
                if (profileLive)
                {
                    resetProfileArmedUntil = 0f;
                    Save = Save.ResetProgress(PlayerPrefsSaveStore.Instance);
                    Bindings.Data = Save;
                    WebcamFeed.SetEnabled(Save.settings.useWebcam);
                    Toast(Loc.T("settings.profile_reset"));
                }
                else if (!profileArmed) { resetProfileArmedUntil = Time.unscaledTime + ResetArmSeconds; UiSound.Confirm(); }
            }
            // CHANGE NAME: the same name window and validation as the first launch (and the profile chip).
            string shownName = DisplayedName();
            if (Gui.Button(new Rect(x + 280f, y, 292f, 44f), shownName.Length > 0 ? Loc.T("settings.change_name_as", shownName) : Loc.T("settings.change_name"), Gui.Gold, 14))
                OpenNameDialog();
            // LANGUAGE: reopens the first-launch language window (the current language in its own name).
            y += 56f;
            var li = Loc.Find(Loc.Code);
            if (Gui.Button(new Rect(x, y, 572f, 44f), Loc.T("settings.language", li != null ? li.native : Loc.Code), Gui.P2, 14))
                OpenLanguageDialog();

            var c = new Rect(a.x + 650f, a.y, a.width - 650f, a.height);
            Gui.Box(c, Gui.Gold);
            Gui.Display(new Rect(c.x + 20f, c.y + 10f, 300f, 30f), Loc.T("settings.key_bindings"), 24, Gui.Gold, TextAnchor.MiddleLeft, 0.18f);
            bool keysArmed = Time.unscaledTime < resetKeysArmedUntil;
            bool keysLive = keysArmed && Time.unscaledTime > resetKeysArmedUntil - ResetArmSeconds + ResetGraceSeconds;
            if (Gui.Button(new Rect(c.xMax - 170f, c.y + 10f, 150f, 30f), keysArmed ? Loc.T("settings.sure") : Loc.T("settings.reset_keys"), Gui.Red, 12))
            {
                if (keysLive)
                {
                    resetKeysArmedUntil = 0f;
                    Bindings.ResetDefaults(Save);
                    Save.Save();
                    listening = false;
                    Toast(Loc.T("settings.keys_reset"));
                }
                else if (!keysArmed) { resetKeysArmedUntil = Time.unscaledTime + ResetArmSeconds; UiSound.Confirm(); }
            }
            float colW = (c.width - 60f) * 0.5f;
            // Every action in Bindings (Count grows with new buttons): rows shrink to fit the panel.
            float rowH = Mathf.Min(29f, (c.height - 50f - 26f - 56f) / Bindings.Count);
            for (int pn = 0; pn < 2; pn++)
            {
                float bx = c.x + 20f + pn * (colW + 20f), by = c.y + 50f;
                var pc = Gui.PlayerColor(pn);
                Gui.Skew(new Rect(bx, by + 2f, 4f, 18f), pc, 0.3f);
                Gui.Text(new Rect(bx + 10f, by, colW, 22f), pn == 0 ? Loc.T("settings.player1") : Loc.T("settings.player2"), 15, pc, TextAnchor.MiddleLeft);
                by += 26f;
                for (int k = 0; k < Bindings.Count; k++)
                {
                    var act = (BindAction)k;
                    bool mine = listening && listenPlayer == pn && listenAction == act;
                    Gui.Text(new Rect(bx, by, colW - 106f, rowH - 3f), Loc.T(Bindings.Names[k]), 12, Gui.Paper, TextAnchor.MiddleLeft, false);
                    var row = new Rect(bx + colW - 104f, by, 104f, rowH - 3f);
                    if (mine && Event.current.type == EventType.Repaint) listenPointerOnRow = row.Contains(Event.current.mousePosition);
                    if (Gui.Button(row, mine ? Loc.T("settings.key_listening") : Bindings.Label(pn, act), mine ? Gui.Gold : pc, 11, true, mine) &&
                        !listening && !listenClickLatch && Time.unscaledTime - listenEndedAt > 0.4f)
                    {
                        listening = true; listenSince = Time.unscaledTime; listenPlayer = pn; listenAction = act;
                        listenPointerOnRow = true;
                    }
                    by += rowH;
                }
            }
            Gui.Text(new Rect(c.x + 20f, c.yMax - 52f, c.width - 40f, 46f),
                Loc.T("settings.keys_help"),
                11, Gui.Muted, TextAnchor.UpperLeft, false);
        }

        static readonly string[] Percent = { "0%", "10%", "20%", "30%", "40%", "50%", "60%", "70%", "80%", "90%", "100%" };

        /// <summary>Volume row: label, -, segmented meter (click to set), +, percentage. Returns true when changed.</summary>
        bool VolumeSlider(float x, ref float y, string label, ref float value)
        {
            Gui.Text(new Rect(x, y, 300f, 40f), label, 15, Gui.Paper, TextAnchor.MiddleLeft);
            int steps = Mathf.RoundToInt(Mathf.Clamp01(value) * 10f);
            int before = steps;
            if (Gui.Button(new Rect(x + 230f, y + 4f, 40f, 32f), "-", Gui.P2, 18, steps > 0)) steps--;
            var bar = new Rect(x + 280f, y + 12f, 200f, 16f);
            Gui.Meter(bar, steps / 10f, Gui.P2, 10);
            if (Gui.Hit(new Rect(bar.x - 4f, y + 4f, bar.width + 8f, 32f), out bool hot))
                steps = Mathf.Clamp(Mathf.RoundToInt((Event.current.mousePosition.x - bar.x) / bar.width * 10f), 0, 10);
            if (hot) Gui.Frame(new Rect(bar.x - 4f, y + 6f, bar.width + 8f, 28f), Gui.Paper, 1f);
            if (Gui.Button(new Rect(x + 490f, y + 4f, 40f, 32f), "+", Gui.P2, 18, steps < 10)) steps++;
            Gui.Text(new Rect(x + 534f, y, 50f, 40f), Percent[steps], 13, Gui.Gold, TextAnchor.MiddleRight);
            y += 46f;
            if (steps == before) return false;
            UiSound.Step();
            value = steps / 10f;
            return true;
        }

        bool Toggle(float x, ref float y, string label, ref bool value)
        {
            Gui.Text(new Rect(x, y, 300f, 40f), label, 15, Gui.Paper, TextAnchor.MiddleLeft);
            bool changed = false;
            if (Gui.Button(new Rect(x + 340f, y, 110f, 40f), Loc.T("common.on"), Gui.Green, 15, true, value) && !value) { value = true; changed = true; }
            if (Gui.Button(new Rect(x + 460f, y, 110f, 40f), Loc.T("common.off"), Gui.Red, 15, true, !value) && value) { value = false; changed = true; }
            y += 52f;
            if (changed) Save.Save();
            return changed;
        }

        // ---------------------------------------------------------------- screen wipe

        void DrawWipe()
        {
            if (wipeStart < 0f) return;
            float t = Mathf.Clamp01((Time.unscaledTime - wipeStart) / WipeTime);
            const float k = 0.42f;
            float over = H * k * 0.5f + 40f;
            float slabW = W + over * 2f + 80f;
            float x = t < 0.5f
                ? Mathf.Lerp(-slabW - over - 200f, -over - 40f, Gui.EaseInOut(t * 2f))
                : Mathf.Lerp(-over - 40f, W + over + 160f, Gui.EaseInOut((t - 0.5f) * 2f));
            var slab = new Rect(x, -20f, slabW, H + 40f);
            Gui.Skew(slab, Gui.Navy, k);
            Gui.PushSkew(k, slab.center.y);
            Gui.Hatch(slab, new Color(1f, 1f, 1f, 0.03f), 18f);
            Gui.Pop();
            Gui.Skew(new Rect(slab.xMax - 4f, slab.y, 70f, slab.height), Gui.P1, k);
            Gui.Skew(new Rect(slab.xMax + 78f, slab.y, 22f, slab.height), Gui.P2, k);
            Gui.Skew(new Rect(slab.x - 64f, slab.y, 40f, slab.height), Gui.P2, k);
            Gui.Skew(new Rect(slab.x - 92f, slab.y, 12f, slab.height), Gui.P1, k);
            if (!string.IsNullOrEmpty(wipeCaption))
            {
                var cap = new Rect(slab.x + over + 40f, H * 0.5f - 60f, W, 120f);
                Gui.Rect(new Rect(cap.x, cap.y + 104f, W, 3f), WithA(Gui.Gold, 0.8f));
                Gui.Display(cap, wipeCaption, 86, Gui.Paper, TextAnchor.MiddleCenter, 0.2f, Gui.P1, 5f);
            }
        }
    }
}
