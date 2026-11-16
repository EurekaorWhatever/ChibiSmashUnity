using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Loading screen, home menu, top navigation bar, Play / Online hub / Arcade / Training / Collection / Settings pages
    /// and the shared screen chrome (ported from the copy-ghibli-smash fork, decision UI-1, with our five characters).
    /// </summary>
    public partial class GameManager
    {
        static readonly string[] HomeItems = { "PLAY", "ONLINE", "TRAINING", "ARCADE", "CHARACTER COLLECTION", "SETTINGS", "EXIT GAME" };
        static readonly string[] HomeDesc =
        {
            "VS CPU, local versus with a friend, or training",
            "Matchmaking, your own rooms, friends and LAN play",
            "Camera setup, gesture practice and a training dummy",
            "All five rivals back to back across every stage",
            "Skins, weapons and the cosmetic banner",
            "Difficulty, camera options and key bindings",
            "Close the game",
        };

        static readonly string[] Tips =
        {
            "Fill the ULT meter and press Ultimate - then strike your fighter's pose on camera before the countdown ends.",
            "Higher DEF shaves damage off every hit and makes your block stronger.",
            "Good posture tracked by the camera earns bonus coins after each match.",
            "Skins and weapons are cosmetic only - stats never change with what a fighter wears.",
            "In Training, press T to fill your ultimate. The dummy always gets back up.",
            "Every fighter brings one of two passives - choose it in the loadout panel after locking in.",
            "Online fights use rollback netcode: both games run the same fight from the same inputs.",
        };

        static readonly string[] StatNames = { "POWER", "SPEED", "RANGE", "ENDURANCE", "RECOVERY" };

        int homeSel;
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
                float w = Gui.Measure(NavNames[i], 15) + 30f;
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
                Gui.Text(r, NavNames[i], 15, active || hot ? Gui.Paper : Gui.Muted, TextAnchor.MiddleCenter, true, false);
                if (i < NavNames.Length - 1) Gui.Skew(new Rect(r.xMax + 4f, 16f, 2f, 20f), Gui.Steel, 0.35f);
                if (click && NavPages[i] != page) Go(NavPages[i]);
                x += w + 10f;
            }
            Gui.KeyCap(x, 16f, Gui.Key("E", "RB"), 20f);
            ProfileChip(new Rect(W - 252f, 8f, 236f, 36f));
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
            Gui.Text(new Rect(r.x + 52f, r.y + 2f, 112f, 18f), shown.Length > 0 ? shown : "SET NAME", 13, Gui.Paper, TextAnchor.MiddleLeft, true, false);
            Gui.Text(new Rect(r.x + 52f, r.y + 17f, 112f, 16f), hot ? "EDIT NAME" : "W " + Save.wins + "  ·  L " + Save.losses, 11, hot ? Gui.Gold : Gui.Muted, TextAnchor.MiddleLeft, false, false);
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

        void EndPage()
        {
            Gui.Pop();
            Gui.Alpha = 1f;
        }

        void NavHints(string where)
        {
            Gui.HintBar(where, Gui.Key("WASD", "D-PAD"), "MOVE", Gui.Key("SPACE", "A"), "SELECT", Gui.Key("ESC", "B"), page == Page.Home ? "EXIT" : "BACK", Gui.Key("Q / E", "LB / RB"), "TABS");
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
            Gui.Display(new Rect(W * 0.45f, H - 152f, W * 0.55f - 50f, 30f), ch.Role.ToUpper() + "  ·  " + ch.Weapon.ToUpper(), 22, Gui.Gold, TextAnchor.MiddleRight, 0.18f);

            // logo and headline, staggered in from the left
            float a1 = Gui.EaseOut((since - 0.05f) / 0.45f), a2 = Gui.EaseOut((since - 0.18f) / 0.45f);
            Gui.Alpha = a1;
            Logo(new Rect(48f - (1f - a1) * 40f, 34f, 640f, 92f), 82);
            Gui.Text(new Rect(54f, 124f, 520f, 20f), "ARENA FIGHTER  ·  BODY-GESTURE ULTIMATES", 13, Gui.Muted, TextAnchor.MiddleLeft, true, false);
            Gui.Alpha = a2;
            float hx = 48f - (1f - a2) * 60f;
            Gui.Display(new Rect(hx, H * 0.33f, W * 0.55f, 84f), "PREPARE", 78, Gui.Paper);
            Gui.Display(new Rect(hx + 22f, H * 0.33f + 74f, W * 0.55f, 84f), "FOR BATTLE", 78, Gui.P1);
            Gui.Text(new Rect(hx + 28f, H * 0.33f + 168f, 520f, 44f), ch.Style, 15, Gui.Muted, TextAnchor.UpperLeft, false);
            Gui.Alpha = 1f;

            var tip = new Rect(48f, H - 168f, 560f, 60f);
            var tag = new Rect(tip.x, tip.y, 52f, 22f);
            Gui.Skew(tag, Gui.Gold, 0.3f);
            Gui.Text(tag, "TIP", 13, Gui.Ink, TextAnchor.MiddleCenter, true, false);
            Gui.Text(new Rect(tip.x + 64f, tip.y, tip.width - 64f, 60f), Tips[loadTip], 14, Gui.Paper, TextAnchor.UpperLeft, false);

            LoadBar(new Rect(48f, H - 74f, W - 96f, 22f));
        }

        /// <summary>Slanted segmented progress bar showing the real loading progress (never ahead of it).</summary>
        void LoadBar(Rect r)
        {
            float p = loadShown;
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f);
            for (int i = 0; i < 3; i++)
                Gui.Chevron(new Rect(r.x + i * 11f, r.y - 22f, 9f, 14f), WithA(Gui.P1, 0.35f + 0.65f * Mathf.Clamp01(pulse * 3f - i)));
            Gui.Text(new Rect(r.x + 42f, r.y - 30f, 700f, 24f), (loadIndex >= loadSteps.Count ? "READY" : "LOADING") + "  ·  " + LoadLabel.ToUpper(), 13, Gui.Muted, TextAnchor.MiddleLeft, true, false);
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
            Gui.Text(new Rect(46f, 140f, 520f, 20f), "ARENA FIGHTER  ·  BODY-GESTURE ULTIMATES", 13, Gui.Gold, TextAnchor.MiddleLeft, true, false);
            Gui.Alpha = 1f;

            const float y0 = 182f, step = 50f;
            // selection slab glides between items
            float sa = Gui.EaseOut((since - 0.15f) / 0.3f);
            var slab = new Rect(-30f, y0 + homeSelAnim * step + 3f, 30f + 470f * sa, step - 6f);
            Gui.Skew(new Rect(slab.x + 5f, slab.y + 5f, slab.width, slab.height), new Color(0f, 0f, 0f, 0.4f));
            Gui.Skew(slab, homeSel == 6 ? Gui.Steel : Gui.P1);
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
                string label = i == 1 && !PlayerIdentity.OnlineAllowed(Save.ageBand) ? "ONLINE  13+" : HomeItems[i];
                Gui.Display(new Rect(r.x + 40f + shift, r.y, 440f, r.height), label, sel ? 38 : 32, sel ? Gui.Paper : (i == 6 ? Gui.Muted : WithA(Gui.Paper, 0.78f)), TextAnchor.MiddleLeft, 0.2f, sel ? Gui.Ink : Color.clear);
                Gui.Alpha = 1f;
                if (click) HomeSelect(i);
            }

            float da = Gui.EaseOut((since - 0.5f) / 0.3f);
            Gui.Alpha = da;
            float dy = y0 + HomeItems.Length * step + 14f;
            Gui.Rect(new Rect(40f, dy, 380f, 1f), Gui.Steel);
            Gui.Chevron(new Rect(40f, dy + 12f, 8f, 12f), Gui.Gold);
            Gui.Text(new Rect(58f, dy + 6f, 500f, 24f), HomeDesc[homeSel], 14, Gui.Muted, TextAnchor.MiddleLeft, false, false);
            Gui.Alpha = 1f;

            NavHints("MAIN MENU");
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
            Gui.Display(new Rect(px, H - 128f, W - px - 48f, 30f), ch.Role.ToUpper(), 24, Gui.Gold, TextAnchor.MiddleRight, 0.18f);
            Gui.Text(new Rect(px, H - 98f, W - px - 52f, 20f), ch.Weapon.ToUpper() + "  ·  ULT " + ClassDef.Get(ch.Class).ultName, 12, Gui.Muted, TextAnchor.MiddleRight, true, false);
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
                case 1: Go(Page.Online); break;
                case 2: Go(Page.Training); break;
                case 3: Go(Page.Arcade); break;
                case 4: Go(Page.Collection); break;
                case 5: Go(Page.Settings); break;
                default: OpenExitPrompt(); break;
            }
        }

        void ExitModal()
        {
            Gui.Rect(new Rect(0f, 0f, W, H), new Color(0f, 0f, 0f, 0.62f));
            var r = new Rect(W * 0.5f - 270f, H * 0.5f - 120f, 540f, 240f);
            Gui.Skew(new Rect(-40f, r.y + 30f, W + 80f, 180f), WithA(Gui.P1, 0.18f), 0f);
            Gui.Box(r, Gui.P1);
            Gui.Display(new Rect(r.x + 30f, r.y + 24f, r.width - 60f, 64f), "EXIT GAME?", 54, Gui.Paper);
            Gui.Text(new Rect(r.x + 32f, r.y + 92f, r.width - 64f, 40f), "Your profile, coins and collection are saved on this device.", 14, Gui.Muted, TextAnchor.UpperLeft, false);
            Gui.PreferNext();
            if (Gui.Button(new Rect(r.x + 30f, r.yMax - 78f, 220f, 52f), "STAY", Gui.P2, 18)) CloseExitPrompt();
            if (Gui.Button(new Rect(r.xMax - 250f, r.yMax - 78f, 220f, 52f), "EXIT GAME", Gui.P1, 18)) QuitGame();
        }

        // ---------------------------------------------------------------- PLAY (offline modes)

        void PlayView()
        {
            Backdrop();
            NavBar();
            var a = BeginPage("01", "PLAY", "CHOOSE YOUR BATTLE  ·  NO INTERNET NEEDED");
            float qw = 380f;
            float cw = (a.width - qw - 30f - 44f) / 3f;
            float ch = Mathf.Min(a.height - 16f, 430f);
            // VS AI runs entirely on this device: no internet, no sign-in, nothing online is touched.
            if (ModeCard(0, new Rect(a.x, a.y + 8f, cw, ch), "VS\nCPU", "1P VS COM", "The computer picks a random fighter every fight. Earn coins and quest progress.", Gui.P1, "COM  " + DifficultyUpper[Save.settings.cpuDifficulty]))
                BeginSelect(true, false);
            if (ModeCard(1, new Rect(a.x + cw + 22f, a.y + 8f, cw, ch), "LOCAL\nVERSUS", "1P VS 2P", "Two players: one keyboard split in half, or two gamepads.", Gui.P2, "2 PLAYERS"))
                BeginSelect(false, false);
            if (ModeCard(2, new Rect(a.x + 2f * (cw + 22f), a.y + 8f, cw, ch), "TRAINING", "1P VS DUMMY", "Practice combos and gesture ultimates on a dummy that never stays down.", Gui.Gold, "NO REWARDS"))
                BeginSelect(true, true);
            Quests(new Rect(a.xMax - qw, a.y + 8f, qw, a.height - 8f));
            EndPage();
            NavHints("PLAY");
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
                    Gui.Text(new Rect(b.x + 24f, b.yMax - 32f, b.width - 62f, 24f), "START", 12, Gui.Paper, TextAnchor.MiddleRight, true, false);
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
            Gui.Display(new Rect(q.x + 18f, q.y + 10f, 200f, 34f), "QUESTS", 28, Gui.Gold, TextAnchor.MiddleLeft, 0.18f);
            Gui.Text(new Rect(q.x + 18f, q.y + 12f, q.width - 36f, 30f), "W " + Save.wins + "  ·  L " + Save.losses, 14, Gui.Muted, TextAnchor.MiddleRight);
            float y = q.y + 56f;
            foreach (var quest in Save.quests)
            {
                if (y > q.yMax - 110f) break;
                Gui.Text(new Rect(q.x + 18f, y, q.width - 100f, 22f), quest.desc, 14, Gui.Paper, TextAnchor.MiddleLeft, false);
                Gui.Text(new Rect(q.x + 18f, y, q.width - 36f, 22f), "+" + quest.reward, 15, Gui.Gold, TextAnchor.MiddleRight);
                Gui.Meter(new Rect(q.x + 18f, y + 26f, q.width - 36f, 8f), (float)quest.progress / quest.target, Gui.P2, 16);
                Gui.Text(new Rect(q.x + 18f, y + 36f, q.width - 36f, 16f), quest.progress + " / " + quest.target + (quest.completions > 0 ? "   ·   completed x" + quest.completions : ""), 11, Gui.Muted, TextAnchor.MiddleLeft, false);
                y += 62f;
            }
            Gui.Text(new Rect(q.x + 18f, q.yMax - 54f, q.width - 36f, 46f), "Coins come only from playing: wins, PvE, quests and good posture tracked by the camera.", 12, Gui.Muted, TextAnchor.MiddleLeft, false);
        }

        // ---------------------------------------------------------------- ONLINE hub

        void OnlineView()
        {
            Backdrop();
            NavBar();
            bool onlineOk = PlayerIdentity.OnlineAllowed(Save.ageBand);
            // MATCHMAKING and ROOMS need the internet (Wi-Fi or mobile data); VS AI, local versus and training never do.
            bool online = Application.internetReachability != NetworkReachability.NotReachable;
            const string Locked = "Online modes are for players 13 and older.";
            const string NoNet = "No internet: connect Wi-Fi or mobile data. VS AI works offline.";
            var a = BeginPage("02", "ONLINE", "MATCHMAKING · ROOMS · FRIENDS · LAN");
            float cw = 270f, ch = Mathf.Min(a.height - 16f, 420f);
            string net = !onlineOk ? "13+ ONLY" : !online ? "NO INTERNET" : "ONLINE";
            if (ModeCard(0, new Rect(a.x, a.y + 8f, cw, ch), "MATCH\nMAKING", net == "ONLINE" ? "QUICK MATCH" : net,
                         "Closest players first: your country, nearby countries, then the world.", Gui.P1,
                         onlineOk && online ? "FIND A RIVAL NOW" : "LOCKED", !onlineOk || !online))
            {
                if (!onlineOk) Deny(Locked);
                else if (!online) Deny(NoNet);
                else StartMatchmaking();
            }
            if (ModeCard(1, new Rect(a.x + cw + 22f, a.y + 8f, cw, ch), "ROOMS", net == "ONLINE" ? "PRIVATE" : net,
                         "Your own named rooms. Invite a friend, both press OK, then START FIGHT.", Gui.Gold,
                         onlineOk && online ? "CREATE OR JOIN" : "LOCKED", !onlineOk || !online))
            {
                if (!onlineOk) Deny(Locked);
                else if (!online) Deny(NoNet);
                else OpenRoomsPage();
            }
            if (ModeCard(2, new Rect(a.x + 2f * (cw + 22f), a.y + 8f, cw, ch), "FRIENDS\n& LAN", onlineOk ? "SOCIAL" : "13+ ONLY",
                         "Friends list, invites by name, room codes and same-network play.", Gui.P2,
                         onlineOk ? "OPEN LOBBY" : "LOCKED", !onlineOk))
            {
                if (!onlineOk) Deny(Locked);
                else OpenLobby(true);
            }

            var info = new Rect(a.x + 3f * (cw + 22f) + 26f, a.y + 8f, a.width - 3f * (cw + 22f) - 26f, ch);
            Gui.Box(info, onlineOk && online ? Gui.Green : Gui.P1);
            float x = info.x + 22f, w = info.width - 44f;
            Gui.Text(new Rect(x, info.y + 18f, w, 20f), "CONNECTION", 13, Gui.Muted, TextAnchor.MiddleLeft);
            float blink = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 4f);
            var sc = onlineOk && online ? Gui.Green : Gui.P1;
            Gui.Skew(new Rect(x, info.y + 50f, 14f, 30f), WithA(sc, 0.5f + 0.5f * blink), 0.3f);
            Gui.Display(new Rect(x + 26f, info.y + 40f, w, 50f), onlineOk ? (online ? "READY" : "OFFLINE") : "13+ ONLY", 42, sc);
            string body = !onlineOk ? "Online play is for players 13 and older. Every offline mode is open to you."
                        : !online ? "No internet right now. LAN play on the same network still works from FRIENDS & LAN."
                        : "Rollback netcode: both games run the same fight from the same inputs, so it stays fast and fair.";
            Gui.Text(new Rect(x, info.y + 100f, w, 110f), body, 14, Gui.Paper, TextAnchor.UpperLeft, false);
            if (onlineOk && roomsSvc.MyRooms.Count > 0)
                Gui.Text(new Rect(x, info.y + 214f, w, 20f), "YOUR ROOMS: " + roomsSvc.MyRooms.Count, 13, Gui.Gold, TextAnchor.MiddleLeft);
            if (Gui.Button(new Rect(x, info.yMax - 70f, w, 50f), "PLAY VS CPU", Gui.P1, 17)) BeginSelect(true, false);
            EndPage();
            NavHints("ONLINE");
        }

        // ---------------------------------------------------------------- ARCADE

        void ArcadeView()
        {
            Backdrop();
            NavBar();
            var a = BeginPage("04", "ARCADE", "FIVE RIVALS · ONE RUN");
            var box = new Rect(a.x, a.y + 8f, 470f, a.height - 8f);
            Gui.Box(box, Gui.P1);
            float x = box.x + 26f, w = box.width - 52f, y = box.y + 22f;
            Gui.Display(new Rect(x, y, w, 50f), "ARCADE MODE", 44, Gui.Paper);
            y += 60f;
            for (int i = 0; i < 5; i++)
            {
                string rule;
                switch (i)
                {
                    case 0: rule = "Fight all " + ArcadeRounds + " characters in a row, in a random order."; break;
                    case 1: rule = "The stage changes every round, from the Mountain of the Beginning to the Ocean Monument."; break;
                    case 2: rule = "Lost a round? Continue and retry it."; break;
                    case 3: rule = "CPU difficulty: " + DifficultyUpper[Save.settings.cpuDifficulty] + " (change it in Settings)."; break;
                    default: rule = "Wins earn coins and quest progress, the same as VS CPU."; break;
                }
                Gui.Skew(new Rect(x, y + 7f, 8f, 8f), Gui.P1, 0.4f);
                Gui.Text(new Rect(x + 18f, y, w - 18f, 40f), rule, 14, Gui.Paper, TextAnchor.UpperLeft, false);
                y += 40f;
            }
            Gui.PreferNext();
            if (Gui.Button(new Rect(x, box.yMax - 82f, w, 58f), "START ARCADE", Gui.P1, 22)) StartArcade();

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
                Gui.Display(new Rect(row.x + 26f, row.y, 200f, row.height), final ? "FINAL" : "ROUND " + (i + 1), 30, final ? Gui.Gold : Gui.Paper);
                int st = ArcadeStage(i);
                var prev = new Rect(row.x + 230f, row.y + 8f, (row.height - 16f) * 2f, row.height - 16f);
                StagePreview(prev, st, 1f);
                Gui.Text(new Rect(prev.xMax + 16f, row.y, 220f, row.height), StageLabel(st, 216f, 15), 15, Gui.Paper, TextAnchor.MiddleLeft);
                Gui.Display(new Rect(row.x, row.y, row.width - 30f, row.height), "RIVAL  ???", 26, Gui.Muted, TextAnchor.MiddleRight);
                Gui.Alpha = 1f;
            }
            EndPage();
            NavHints("ARCADE");
        }

        // ---------------------------------------------------------------- TRAINING / CV SETUP

        static readonly Gesture[] TrainingGestures = { Gesture.CrossArms, Gesture.PunchForward, Gesture.RaiseHands };
        static readonly string[] TrainingUses = { "Block · Melee & Magic ultimate", "Fireball · Ranged & Polearm ultimate", "Super jump · Heavy ultimate" };

        void TrainingView()
        {
            Backdrop();
            NavBar();
            var a = BeginPage("03", "TRAINING", "CAMERA SETUP & PRACTICE");
            TrainingTab(a);
            EndPage();
            if (MenuInput.UsingGamepad) Gui.HintBar("TRAINING  /  CV SETUP", "D-PAD", "MOVE", "A", "SELECT", "B", "BACK", "LB / RB", "TABS");
            else Gui.HintBar("TRAINING  /  CV SETUP", "1 2 3", "SIMULATE POSE", "P", "SLOUCH", "SPACE", "SELECT", "ESC", "BACK", "Q / E", "TABS");
        }

        void TrainingTab(Rect a)
        {
            var feed = new Rect(a.x, a.y, 520f, 400f);
            var st = menuTracker.Stable != Gesture.None ? Gui.Green : Gui.Gold;
            Gui.CvPanel(feed, menuTracker, st, Save.settings.useWebcam, "CV SETUP · " + menuTracker.Source.Name);

            var p = new Rect(a.x + 545f, a.y, a.width - 545f, a.height);
            Gui.Box(p, Gui.P2);
            float x = p.x + 20f, w = p.width - 40f, y = p.y + 14f;
            Gui.Display(new Rect(x, y, w, 30f), "COMPUTER VISION", 26, Gui.Paper, TextAnchor.MiddleLeft, 0.18f);
            y += 36f;
            string status = "Camera: " + (Save.settings.useWebcam ? WebcamFeed.Status : "off") +
                            "\nTracking: " + menuPose.Status() +
                            "\nDetected: " + (menuTracker.Stable == Gesture.None ? "none" : ClassDef.GestureName(menuTracker.Stable)) +
                            "   ·   Posture " + Mathf.RoundToInt(menuTracker.Posture * 100f) + "%" +
                            "   ·   Telemetry " + menuTracker.Validator.Reason;
            Gui.Text(new Rect(x, y, w, 70f), status, 13, Gui.Paper, TextAnchor.UpperLeft, false);
            y += 78f;

            Gui.Text(new Rect(x, y, w, 24f), menuTracker.IsLive ? "GESTURES  (do the pose in front of the camera - punch = one arm straight out to the side)" : "GESTURES  (hold the key to simulate the pose)", 14, Gui.Gold, TextAnchor.MiddleLeft);
            y += 30f;
            float gw = (w - 20f) / 3f;
            for (int i = 0; i < 3; i++)
            {
                var g = new Rect(x + i * (gw + 10f), y, gw, 190f);
                bool active = menuTracker.Stable == TrainingGestures[i];
                Gui.Rect(g, active ? new Color(0.1f, 0.35f, 0.25f, 0.9f) : Gui.PanelLight);
                Gui.Frame(g, active ? Gui.Green : Gui.Steel, 2f);
                if (active) Gui.Rect(new Rect(g.x, g.y, g.width, 3f), Gui.Green);
                Gui.Skeleton(new Rect(g.x + gw * 0.5f - 50f, g.y + 8f, 100f, 110f), SimulatedPoseSource.Template(TrainingGestures[i], false), active ? Gui.Green : Gui.P2, 2.5f);
                Gui.Text(new Rect(g.x, g.y + 120f, gw, 22f), "[" + (i + 1) + "] " + ClassDef.GestureName(TrainingGestures[i]), 13, Gui.Paper);
                Gui.Text(new Rect(g.x + 6f, g.y + 144f, gw - 12f, 40f), TrainingUses[i], 11, Gui.Muted, TextAnchor.UpperCenter, false);
            }
            y += 205f;
            Gui.Text(new Rect(x, y, w, 20f), "Press P to toggle a slouching posture and watch the posture score drop.", 12, Gui.Muted, TextAnchor.MiddleLeft, false);

            if (Gui.Button(new Rect(a.x, a.y + 415f, 250f, 48f), Save.settings.useWebcam ? "CAMERA TRACKING: ON" : "CAMERA TRACKING: OFF", Gui.P2, 16, true, Save.settings.useWebcam))
            {
                Save.settings.useWebcam = !Save.settings.useWebcam;
                WebcamFeed.SetEnabled(Save.settings.useWebcam);
                Save.Save();
            }
            Gui.PreferNext();
            if (Gui.Button(new Rect(a.x + 270f, a.y + 415f, 250f, 48f), "PRACTICE VS DUMMY", Gui.Gold, 16))
                BeginSelect(true, true);
        }

        // ---------------------------------------------------------------- CHARACTER COLLECTION (armory / gacha)

        readonly List<Skin> armorySkins = new List<Skin>();
        int armorySkinsFor = -1;

        void CollectionView()
        {
            Backdrop();
            NavBar();
            var a = BeginPage("05", "COLLECTION", "SKINS · WEAPONS · COSMETIC BANNER");
            ArmoryTab(a);
            EndPage();
            Gui.HintBar("CHARACTER COLLECTION", Gui.Key("WASD", "D-PAD"), "MOVE", Gui.Key("SPACE", "A"), "SELECT", Gui.Key("ESC", "B"), "BACK", Gui.Key("Q / E", "LB / RB"), "TABS");
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
                Gui.Text(new Rect(r.x, r.y + 144f, r.width, 20f), s.rarity.ToString().ToUpper() + (s.goldCinematic ? " · GOLD CINEMATIC" : ""), 11, rc);
                if (owned)
                {
                    if (s == equipped) Gui.Text(new Rect(r.x, r.y + 172f, r.width, 36f), "EQUIPPED", 14, Gui.Green);
                    else if (Gui.Button(new Rect(r.x + 34f, r.y + 172f, 120f, 36f), "EQUIP", Gui.P2, 14)) Save.Equip(s);
                }
                else Gui.Text(new Rect(r.x, r.y + 172f, r.width, 36f), "Pull to unlock", 12, Gui.Muted);
            }

            var g = new Rect(a.x + 820f, a.y, a.width - 820f, a.height);
            Gui.Box(g, Gui.Gold);
            float x = g.x + 18f, w = g.width - 36f, y = g.y + 14f;
            Gui.Display(new Rect(x, y, w, 32f), "COSMETIC BANNER", 26, Gui.Gold, TextAnchor.MiddleCenter, 0.18f);
            y += 36f;
            Gui.Text(new Rect(x, y, w, 50f), "Skins & ultimate cinematics only. Stats are identical on every skin - nothing is paywalled.", 12, Gui.Muted, TextAnchor.UpperCenter, false);
            y += 52f;
            Gui.Text(new Rect(x, y, w, 20f), "Common 60% · Rare 28% · Epic 10% · Legendary 2%", 11, Gui.Paper);
            y += 28f;
            if (Gui.Button(new Rect(x, y, w * 0.5f - 5f, 34f), "ALL FIGHTERS", Gui.P2, 12, true, bannerAll)) bannerAll = true;
            if (Gui.Button(new Rect(x + w * 0.5f + 5f, y, w * 0.5f - 5f, 34f), Hud.CharNameUpper(armoryClass) + " ONLY", Gui.P2, 12, true, !bannerAll)) bannerAll = false;
            y += 46f;
            FighterClass? banner = bannerAll ? (FighterClass?)null : armoryClass;
            if (Gui.Button(new Rect(x, y, w, 46f), "PULL x1  ·  " + SaveData.PullCost, Gui.P1, 16, Save.coins >= SaveData.PullCost))
                DoPull(1, banner);
            y += 54f;
            if (Gui.Button(new Rect(x, y, w, 46f), "PULL x10  ·  " + SaveData.TenPullCost, Gui.Gold, 16, Save.coins >= SaveData.TenPullCost))
                DoPull(10, banner);
            y += 60f;
            foreach (var p in lastPull)
            {
                if (y > g.yMax - 20f) break;
                Gui.Text(new Rect(x, y, w, 18f), Hud.CharName(p.skin.cls) + " · " + p.skin.name + (p.duplicate ? "  (dup +20)" : "  NEW!"), 12, Skin.RarityColor(p.skin.rarity), TextAnchor.MiddleLeft, false);
                y += 18f;
            }
        }

        void DoPull(int n, FighterClass? banner)
        {
            lastPull = Save.Pull(n, banner);
            foreach (var p in lastPull)
                if (p.skin.rarity >= Rarity.Epic && !p.duplicate)
                    Toast(p.skin.rarity.ToString().ToUpper() + "! " + Hud.CharName(p.skin.cls) + " · " + p.skin.name);
        }

        // ---------------------------------------------------------------- SETTINGS

        void SettingsView()
        {
            Backdrop();
            NavBar();
            var a = BeginPage("06", "SETTINGS", "GAME · CAMERA · CONTROLS");
            SettingsTab(a);
            EndPage();
            Gui.HintBar(listening ? "PRESS A KEY OR CLICK THE BOX  ·  ESC / PAD B CANCELS  ·  PAD BUTTONS CAN'T BE BOUND" : "SETTINGS", Gui.Key("WASD", "D-PAD"), "MOVE", Gui.Key("SPACE", "A"), "SELECT", Gui.Key("ESC", "B"), "BACK", Gui.Key("Q / E", "LB / RB"), "TABS");
        }

        void SettingsTab(Rect a)
        {
            var s = Save.settings;
            var p = new Rect(a.x, a.y, 620f, a.height);
            Gui.Box(p, Gui.P1);
            float x = p.x + 24f, y = p.y + 20f;

            Gui.Text(new Rect(x, y, 200f, 40f), "MATCH TIME", 16, Gui.Paper, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(x + 220f, y, 350f, 40f), MatchController.MatchSeconds + " seconds  <size=12><color=#9AA6BC>(fixed for every match)</color></size>", 16, Gui.Paper, TextAnchor.MiddleLeft);
            y += 56f;

            Gui.Text(new Rect(x, y, 200f, 40f), "CPU DIFFICULTY", 16, Gui.Paper, TextAnchor.MiddleLeft);
            for (int i = 1; i <= 3; i++)
            {
                if (s.cpuDifficulty == i) Gui.PreferNext();
                if (Gui.Button(new Rect(x + 220f + (i - 1) * 120f, y, 110f, 40f), DifficultyUpper[i], Gui.P2, 15, true, s.cpuDifficulty == i))
                {
                    s.cpuDifficulty = i;
                    Save.Save();
                }
            }
            y += 56f;

            if (Toggle(x, ref y, "CAMERA TRACKING (WEBCAM)", ref s.useWebcam)) WebcamFeed.SetEnabled(s.useWebcam);
            Toggle(x, ref y, "CV OVERLAY IN MATCH", ref s.showCvOverlay);
            Toggle(x, ref y, "PRE-MATCH CAMERA CHECK", ref s.preMatchCheck);
            Toggle(x, ref y, "SCREEN SHAKE", ref s.screenShake);

            // Volumes live in ChibiAudio (it persists them itself); 10 % steps with - / +, or click the meter.
            float master = Netcode.ChibiAudio.MasterVolume, sfx = Netcode.ChibiAudio.SfxVolume;
            if (VolumeSlider(x, ref y, "MASTER VOLUME", ref master)) Netcode.ChibiAudio.MasterVolume = master;
            if (VolumeSlider(x, ref y, "SFX VOLUME", ref sfx)) Netcode.ChibiAudio.SfxVolume = sfx;

            y += 4f;
            // Two clicks within 3 s (item 22). RESET PROFILE keeps the name, age and sign-in (user decision 2026-11-17).
            bool profileArmed = Time.unscaledTime < resetProfileArmedUntil;
            bool profileLive = profileArmed && Time.unscaledTime > resetProfileArmedUntil - ResetArmSeconds + ResetGraceSeconds;
            if (Gui.Button(new Rect(x, y, 260f, 44f), profileArmed ? "SURE? ALL PROGRESS GOES" : "RESET PROFILE", Gui.Red, profileArmed ? 13 : 15))
            {
                if (profileLive)
                {
                    resetProfileArmedUntil = 0f;
                    Save = Save.ResetProgress(PlayerPrefsSaveStore.Instance);
                    Bindings.Data = Save;
                    WebcamFeed.SetEnabled(Save.settings.useWebcam);
                    Toast("Profile reset (name and sign-in kept)");
                }
                else if (!profileArmed) { resetProfileArmedUntil = Time.unscaledTime + ResetArmSeconds; UiSound.Confirm(); }
            }

            var c = new Rect(a.x + 650f, a.y, a.width - 650f, a.height);
            Gui.Box(c, Gui.Gold);
            Gui.Display(new Rect(c.x + 20f, c.y + 10f, 300f, 30f), "KEY BINDINGS", 24, Gui.Gold, TextAnchor.MiddleLeft, 0.18f);
            bool keysArmed = Time.unscaledTime < resetKeysArmedUntil;
            bool keysLive = keysArmed && Time.unscaledTime > resetKeysArmedUntil - ResetArmSeconds + ResetGraceSeconds;
            if (Gui.Button(new Rect(c.xMax - 170f, c.y + 10f, 150f, 30f), keysArmed ? "SURE?" : "RESET KEYS", Gui.Red, 12))
            {
                if (keysLive)
                {
                    resetKeysArmedUntil = 0f;
                    Bindings.ResetDefaults(Save);
                    Save.Save();
                    listening = false;
                    Toast("Keys reset to defaults");
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
                Gui.Text(new Rect(bx + 10f, by, colW, 22f), pn == 0 ? "PLAYER 1" : "PLAYER 2", 15, pc, TextAnchor.MiddleLeft);
                by += 26f;
                for (int k = 0; k < Bindings.Count; k++)
                {
                    var act = (BindAction)k;
                    bool mine = listening && listenPlayer == pn && listenAction == act;
                    Gui.Text(new Rect(bx, by, colW - 106f, rowH - 3f), Bindings.Names[k], 12, Gui.Paper, TextAnchor.MiddleLeft, false);
                    var row = new Rect(bx + colW - 104f, by, 104f, rowH - 3f);
                    if (mine && Event.current.type == EventType.Repaint) listenPointerOnRow = row.Contains(Event.current.mousePosition);
                    if (Gui.Button(row, mine ? "key or click here" : Bindings.Label(pn, act), mine ? Gui.Gold : pc, 11, true, mine) &&
                        !listening && !listenClickLatch && Time.unscaledTime - listenEndedAt > 0.4f)
                    {
                        listening = true; listenSince = Time.unscaledTime; listenPlayer = pn; listenAction = act;
                        listenPointerOnRow = true;
                    }
                    by += rowH;
                }
            }
            Gui.Text(new Rect(c.x + 20f, c.yMax - 52f, c.width - 40f, 46f),
                "Click a binding, then press a key or click that box with a mouse button (Esc / pad B / a click elsewhere cancels; 5 s timeout). If either player already uses it, the two swap.\n" +
                "Camera poses: cross arms = block · arm out to the side = fireball · both hands up = super jump (keyboard poses: 1/2/3 and 8/9/0).",
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
            if (Gui.Button(new Rect(x + 340f, y, 110f, 40f), "ON", Gui.Green, 15, true, value) && !value) { value = true; changed = true; }
            if (Gui.Button(new Rect(x + 460f, y, 110f, 40f), "OFF", Gui.Red, 15, true, !value) && value) { value = false; changed = true; }
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
