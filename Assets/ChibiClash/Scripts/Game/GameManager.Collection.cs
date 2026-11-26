using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// CHARACTER COLLECTION roster screen, ported from the reference fork (copy-ghibli-smash, 2026-11-18) on 2026-11-26:
    /// red title banner, roster column, character stage with a katakana watermark, and an info panel (class tag, name,
    /// tagline, stat pips, move list, ultimate, passives). ↑/↓ (W/S) walk the roster, ←/→ and Q/E (LB/RB) are PREV / NEXT,
    /// both wrap; Esc goes back. The skins / gacha armory stays one click away (SKINS · BANNER). Display only: moves and
    /// numbers come from design/gdd/movesets/movesets.csv and ClassDef, nothing here touches the simulation. Every label
    /// goes through <see cref="Loc"/> (move names and inputs are game terms and stay English, like the mode names).
    /// </summary>
    public partial class GameManager
    {
        // ---------------------------------------------------------------- data

        /// <summary>One move-list row: input, move name, damage, startup.</summary>
        struct CollMove
        {
            public string Input, Name, Dmg, Startup;
        }

        static CollMove M(string i, string n, string d, string s) => new CollMove { Input = i, Name = n, Dmg = d, Startup = s };

        /// <summary>Presentation extras per character, index = (int)FighterClass.</summary>
        sealed class CollEntry
        {
            public string Kana, ClassKana, TaglineKey;
            public CollMove[] Moves;
        }

        // Move rows: design/gdd/movesets/movesets.csv (startup in ticks = frames at 60 fps, damage in percent).
        static readonly CollEntry[] CollData =
        {
            new CollEntry
            {
                Kana = "カイア", ClassKana = "ソードダンサー", TaglineKey = "coll.tagline.kaia",
                Moves = new[]
                {
                    M("→ + LIGHT", "Forward Tilt", "7%", "5f"), M("↓ + LIGHT", "Down Tilt", "6%", "5f"),
                    M("→ + HEAVY", "Forward Smash", "16%", "14f"), M("↑ + HEAVY", "Up Smash", "15%", "16f"),
                    M("SPECIAL", "Blade Flurry", "1.8% ×4", "8f"), M("→ + SPECIAL", "Shadow Lunge", "12%", "10f"),
                    M("↑ + SPECIAL", "Skyhook", "10%", "10f"), M("↓ + SPECIAL", "Blade Trap", "8%", "12f"),
                    M("↓ + LIGHT (AIR)", "Down Air", "10%", "9f"),
                },
            },
            new CollEntry
            {
                Kana = "ブルーナ", ClassKana = "ハンマー", TaglineKey = "coll.tagline.bruna",
                Moves = new[]
                {
                    M("→ + LIGHT", "Forward Tilt", "7%", "6f"), M("↓ + LIGHT", "Down Tilt", "6.5%", "5f"),
                    M("→ + HEAVY", "Forward Smash", "18%", "14f"), M("↑ + HEAVY", "Up Smash", "17%", "16f"),
                    M("SPECIAL", "Earth Shock", "11%", "10f"), M("→ + SPECIAL", "Hammer Rush", "12%", "8f"),
                    M("↑ + SPECIAL", "Rocket Hammer", "8%", "9f"), M("↓ + SPECIAL", "Fortress Stance", "–", "5f"),
                    M("↓ + LIGHT (AIR)", "Down Air", "10%", "9f"),
                },
            },
            new CollEntry
            {
                Kana = "レン", ClassKana = "アーチャー", TaglineKey = "coll.tagline.ren",
                Moves = new[]
                {
                    M("→ + LIGHT", "Forward Tilt", "6.5%", "5f"), M("↓ + LIGHT", "Down Tilt", "5.5%", "6f"),
                    M("→ + HEAVY", "Forward Smash", "16%", "15f"), M("↑ + HEAVY", "Up Smash", "15%", "16f"),
                    M("SPECIAL", "Focus Shot", "6%", "12f"), M("→ + SPECIAL", "Piercing Arrow", "12%", "14f"),
                    M("↑ + SPECIAL", "Grapple Arrow", "4%", "8f"), M("↓ + SPECIAL", "Trap Mine", "9%", "10f"),
                    M("↓ + LIGHT (AIR)", "Down Air", "10%", "8f"),
                },
            },
            new CollEntry
            {
                Kana = "リラ", ClassKana = "メイジ", TaglineKey = "coll.tagline.lyra",
                Moves = new[]
                {
                    M("→ + LIGHT", "Forward Tilt", "7%", "5f"), M("↓ + LIGHT", "Down Tilt", "5.8%", "5f"),
                    M("→ + HEAVY", "Forward Smash", "16%", "16f"), M("↑ + HEAVY", "Up Smash", "15%", "15f"),
                    M("SPECIAL", "Arcane Shot", "7%", "10f"), M("→ + SPECIAL", "Spell Lance", "11%", "12f"),
                    M("↑ + SPECIAL", "Blink Ascension", "6%", "8f"), M("↓ + SPECIAL", "Glyph Zone", "2%", "14f"),
                    M("↓ + LIGHT (AIR)", "Down Air", "9%", "7f"),
                },
            },
            new CollEntry
            {
                Kana = "タロウ", ClassKana = "ランサー", TaglineKey = "coll.tagline.taro",
                Moves = new[]
                {
                    M("→ + LIGHT", "Forward Tilt", "7%", "5f"), M("↓ + LIGHT", "Down Tilt", "5.5%", "6f"),
                    M("→ + HEAVY", "Forward Smash", "16%", "16f"), M("↑ + HEAVY", "Up Smash", "15%", "14f"),
                    M("SPECIAL", "Wind Lance", "8%", "11f"), M("→ + SPECIAL", "Piercing Dash", "11%", "9f"),
                    M("↑ + SPECIAL", "Staff Vault", "6%", "6f"), M("↓ + SPECIAL", "Jade Stance", "8%", "5f"),
                    M("↓ + LIGHT (AIR)", "Down Air", "10.5%", "10f"),
                },
            },
        };

        // Brief palette.
        static readonly Color CollCrimson = new Color(0.898f, 0.282f, 0.302f);   // #E5484D
        static readonly Color CollGold = new Color(0.949f, 0.757f, 0.306f);      // #F2C14E
        static readonly Color CollBlue = new Color(0.231f, 0.545f, 0.922f);      // #3B8BEB
        static readonly Color CollBody = new Color(0.788f, 0.812f, 0.878f);      // #C9CFE0
        static readonly Color CollMuted = new Color(0.490f, 0.525f, 0.639f);     // #7D86A3
        static readonly Color CollLine = new Color(0.165f, 0.200f, 0.314f);      // #2A3350
        static readonly Color CollPipEmpty = new Color(0.165f, 0.200f, 0.314f);
        static readonly string[] CollStatKeys = { "stats.power", "stats.speed", "stats.range", "stats.endurance" };

        // ---------------------------------------------------------------- state

        int collIndex, collPrevIndex = -1, collDir = 1;
        float collSwapAt = -10f;
        bool collArmory;

        const float CollSwapTime = 0.25f;

        /// <summary>First frame after entering the page: start on the character the armory last showed.</summary>
        bool CollStale => Since < 0.05f && collSwapAt < pageSince;

        /// <summary>Selects roster entry <paramref name="i"/> (wraps) and starts the swap transition.</summary>
        void CollSelect(int i, int dir)
        {
            int n = CharacterRoster.All.Length;
            i = ((i % n) + n) % n;
            if (i == collIndex) return;
            UiSound.Navigate();
            collPrevIndex = collIndex;
            collIndex = i;
            collDir = dir;
            collSwapAt = Time.unscaledTime;
            armoryClass = CharacterRoster.All[i].Class;
        }

        /// <summary>
        /// Keyboard / gamepad on the roster screen: ↑/↓ and ←/→ / Q/E step the roster (wrapping), Esc goes back.
        /// Returns false when the armory sub-view is open (normal menu navigation applies).
        /// </summary>
        bool CollectionNav()
        {
            if (page != Page.Collection || exitPrompt) return false;
            if (collArmory)
            {
                if (!MenuInput.Any(NavCmd.Back)) return false;
                UiSound.Back();
                collArmory = false;
                Gui.ResetFocus();
                return true;
            }
            if (MenuInput.Any(NavCmd.Up)) CollSelect(collIndex - 1, -1);
            else if (MenuInput.Any(NavCmd.Down)) CollSelect(collIndex + 1, 1);
            else if (MenuInput.Any(NavCmd.Left) || MenuInput.Any(NavCmd.PrevTab)) CollSelect(collIndex - 1, -1);
            else if (MenuInput.Any(NavCmd.Right) || MenuInput.Any(NavCmd.NextTab)) CollSelect(collIndex + 1, 1);
            if (MenuInput.Any(NavCmd.Back)) Back();
            return true;
        }

        // ---------------------------------------------------------------- view

        void CollectionView()
        {
            if (CollStale && !collArmory) { collIndex = (int)armoryClass; collPrevIndex = -1; }
            if (collArmory)
            {
                Backdrop();
                NavBar();
                var a = BeginPage("04", Loc.T("nav.collection"), Loc.T("collection.kicker"));
                ArmoryTab(a);
                EndPage();
                if (Gui.Button(new Rect(W - 230f, 70f, 190f, 36f), Loc.T("coll.roster_button"), Gui.P2, 14)) collArmory = false;
                Gui.HintBar(Loc.T("coll.hint_skins"), Gui.Key("WASD", "D-PAD"), Loc.T("hint.move"), Gui.Key("SPACE", "A"), Loc.T("hint.select"),
                            Gui.Key("ESC", "B"), Loc.T("common.back"));
                return;
            }

            collIndex = Mathf.Clamp(collIndex, 0, CharacterRoster.All.Length - 1);
            float t = Time.unscaledTime;
            float swap = Mathf.Clamp01((t - collSwapAt) / CollSwapTime);
            float enter = Gui.EaseOut(Since / 0.35f);

            CollBackdrop(t);
            NavBar();
            Gui.Alpha = enter;

            var ch = CharacterRoster.All[collIndex];
            CollStage(ch, t, swap);
            CollTitle();
            CollTopButtons();
            CollRoster(t);
            CollInfo(ch, t, swap);

            Gui.Alpha = 1f;
            Gui.HintBar(Loc.T("collection.hint"), Gui.Key("W / S", "D-PAD ↑↓"), Loc.T("coll.roster"), Gui.Key("Q / E", "LB / RB"), Loc.T("coll.prev_next"),
                        Gui.Key("ESC", "B"), Loc.T("common.back"));
        }

        /// <summary>Deep navy gradient, 115° pinstripes, a crimson glow in the bottom-left corner.</summary>
        void CollBackdrop(float t)
        {
            var full = new Rect(0f, 0f, W, H);
            Gui.Rect(full, new Color(0.059f, 0.078f, 0.141f));                         // #0f1424
            Gui.FadeUp(full, new Color(0.102f, 0.129f, 0.251f));                       // -> #1a2140
            Gui.Hatch(full, new Color(1f, 1f, 1f, 0.025f), 10f, t * 0.01f);
            Gui.Glow(new Rect(-W * 0.35f, H * 0.35f, W * 0.8f, H * 1.0f), WithA(CollCrimson, 0.25f));
            Gui.Grain(full, 0.035f);
        }

        /// <summary>"ROSTER · 01 / 05" over the red banner with the huge title, katakana below.</summary>
        void CollTitle()
        {
            int n = CharacterRoster.All.Length;
            float x = 40f;
            CollMicro(new Rect(x + 8f, 60f, 300f, 14f), Loc.T("coll.roster_count", (collIndex + 1).ToString("00"), n.ToString("00")), CollBody);
            string title = Loc.T("collection.hint");
            // The banner must end before the info column (x 650) or it covers the class tag line: shrink long titles.
            int size = 50;
            float tw = Gui.Measure(title, size, true);
            while (size > 28 && x + tw + 56f > 630f) { size -= 2; tw = Gui.Measure(title, size, true); }
            var banner = new Rect(x, 78f, tw + 56f, 66f);
            Gui.Skew(new Rect(banner.x + 5f, banner.y + 5f, banner.width, banner.height), new Color(0f, 0f, 0f, 0.35f), 0.21f);
            Gui.Skew(banner, CollCrimson, 0.21f);
            Gui.Display(new Rect(banner.x + 26f, banner.y, tw + 20f, banner.height), title, size, Color.white,
                        TextAnchor.MiddleLeft, 0.2f, new Color(0.45f, 0.08f, 0.1f), 3f);
            Gui.Text(new Rect(x + 14f, banner.yMax + 4f, 200f, 14f), "キャラクター", 11, CollMuted, TextAnchor.MiddleLeft, true, false);
        }

        /// <summary>BACK (with an ESC chip) and the SKINS · BANNER shortcut, top right.</summary>
        void CollTopButtons()
        {
            var back = new Rect(W - 40f - 150f, 88f, 150f, 40f);   // below the language chip
            bool click = Gui.Hit(back, out bool hot);
            Gui.Skew(back, hot ? CollCrimson : new Color(0.07f, 0.09f, 0.16f, 0.95f), 0.21f);
            Gui.SkewFrame(back, hot ? Color.white : CollLine, 1f, 0.21f);
            float kw = Gui.KeyCap(back.x + 18f, back.y + 10f, Gui.Key("ESC", "B"), 20f);
            Gui.Display(new Rect(back.x + 26f + kw, back.y, 90f, back.height), Loc.T("common.back"), 22, Color.white, TextAnchor.MiddleLeft, 0.2f, Color.clear);
            if (click) { UiSound.Back(); Back(); }

            var skins = new Rect(back.x - 14f - 170f, back.y, 170f, 40f);
            bool sClick = Gui.Hit(skins, out bool sHot);
            Gui.Skew(skins, sHot ? CollBlue : new Color(0.07f, 0.09f, 0.16f, 0.95f), 0.21f);
            Gui.SkewFrame(skins, sHot ? Color.white : CollLine, 1f, 0.21f);
            Gui.Display(skins, Loc.T("coll.skins_button"), 17, sHot ? Color.white : CollBody, TextAnchor.MiddleCenter, 0.2f, Color.clear);
            if (sClick) { UiSound.Navigate(); collArmory = true; Gui.ResetFocus(); }
        }

        /// <summary>Vertical roster: tinted portrait cards, gold border on the selected one, hover nudges right.</summary>
        void CollRoster(float t)
        {
            var all = CharacterRoster.All;
            const float size = 84f, gap = 8f;
            float y0 = 176f;
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                var r = new Rect(40f, y0 + i * (size + gap), size, size);
                bool sel = i == collIndex;
                bool click = Gui.Hit(r, out bool hot);
                if (hot && !sel) r.x += 4f;

                Gui.Rect(r, new Color(0.06f, 0.08f, 0.14f));
                Gui.Rect(r, WithA(c.Primary, sel ? 0.55f : hot ? 0.5f : 0.32f));
                Gui.Hatch(r, new Color(1f, 1f, 1f, 0.05f), 8f);
                CharacterArt.DrawPortrait(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f), c, sel || hot ? 1f : 0.6f);
                Gui.FadeUp(new Rect(r.x, r.yMax - 30f, r.width, 30f), new Color(0f, 0f, 0f, 0.85f));
                Gui.Display(new Rect(r.x + 6f, r.yMax - 22f, r.width - 8f, 20f), c.Name.ToUpperInvariant(), 15, Color.white,
                            TextAnchor.MiddleLeft, 0.2f, Color.clear);
                if (sel)
                {
                    Gui.Glow(new Rect(r.x - 14f, r.y - 14f, r.width + 28f, r.height + 28f), WithA(CollGold, 0.18f + 0.06f * Mathf.Sin(t * 4f)));
                    Gui.Frame(r, CollGold, 3f);
                    Gui.Skew(new Rect(r.xMax + 6f, r.center.y - 8f, 4f, 16f), CollGold, 0.2f);
                }
                else Gui.Frame(r, hot ? Color.white : CollLine, 1f);
                if (click) CollSelect(i, i > collIndex ? 1 : -1);
            }
        }

        /// <summary>Crimson parallelogram, katakana watermark and the full-body art with the swap slide and idle float.</summary>
        void CollStage(CharacterInfo ch, float t, float swap)
        {
            var e = CollData[(int)ch.Class];
            var back = new Rect(160f, 176f, 430f, 470f);
            Color tint = Color.Lerp(CollCrimson, ch.Primary, 0.35f);
            Gui.Skew(back, WithA(tint, 0.45f * (0.4f + 0.6f * swap)), 0.21f);
            Gui.Hatch(back, new Color(1f, 1f, 1f, 0.03f), 12f);

            // Vertical katakana watermark (stroke look: four offset ghosts, no fill), and a fainter class column.
            CollVertical(e.Kana, 470f, 150f, 76, 0.16f);
            CollVertical(e.ClassKana, 196f, 190f, 30, 0.08f);

            float ease = Gui.EaseOut(swap);
            var body = new Rect(110f, 118f, 540f, 540f);
            float bob = Mathf.Sin(t * 2.2f) * 2.5f;
            if (collPrevIndex >= 0 && swap < 1f)
            {
                var old = CharacterRoster.All[collPrevIndex];
                var ro = body; ro.x -= collDir * 120f * ease; ro.y += bob;
                CharacterArt.DrawBody(ro, old, 1f - ease);
            }
            var rn = body; rn.x += collDir * 120f * (1f - ease); rn.y += bob;
            CharacterArt.DrawBody(rn, ch, collPrevIndex >= 0 ? ease : 1f);
        }

        void CollVertical(string s, float x, float y, int size, float alpha)
        {
            float step = size * 1.02f;
            var c = new Color(1f, 1f, 1f, alpha);
            for (int i = 0; i < s.Length; i++)
            {
                var r = new Rect(x, y + i * step, size * 1.3f, step);
                string g = s[i].ToString();
                const float o = 1.4f;
                Gui.Text(new Rect(r.x - o, r.y, r.width, r.height), g, size, c, TextAnchor.MiddleCenter, true, false);
                Gui.Text(new Rect(r.x + o, r.y, r.width, r.height), g, size, c, TextAnchor.MiddleCenter, true, false);
                Gui.Text(new Rect(r.x, r.y - o, r.width, r.height), g, size, c, TextAnchor.MiddleCenter, true, false);
                Gui.Text(new Rect(r.x, r.y + o, r.width, r.height), g, size, c, TextAnchor.MiddleCenter, true, false);
            }
        }

        /// <summary>Right column: class tag, name, tagline, stats, meta, move list, ultimate, passives, PREV / NEXT.</summary>
        void CollInfo(CharacterInfo ch, float t, float swap)
        {
            var def = ClassDef.Get(ch.Class);
            var e = CollData[(int)ch.Class];
            float x = 650f, w = Mathf.Max(520f, W - 40f - x);
            float y = 118f;

            // Class tag.
            CollBar(x, y + 1f);
            CollMicro(new Rect(x + 12f, y, w, 16f), (ch.RoleText + " · " + ch.Weapon).ToUpperInvariant(), CollBody);
            y += 16f;

            // Name: clip-path style wipe from the left, crimson offset shadow.
            const int nameSize = 72;
            float nw = Gui.Measure(ch.Name.ToUpperInvariant(), nameSize, true) + 40f;
            float wipe = Gui.EaseOut(Mathf.Clamp01((t - collSwapAt - 0.04f) / 0.3f));
            float keep = Gui.Alpha;
            Gui.Alpha = keep * wipe;
            Gui.Display(new Rect(x, y, nw, 86f), ch.Name.ToUpperInvariant(), nameSize, Color.white, TextAnchor.MiddleLeft, 0.2f, CollCrimson, 4f);
            Gui.Alpha = keep;
            if (wipe < 1f) Gui.Skew(new Rect(x + nw * wipe - 26f, y + 8f, 26f, 70f), CollCrimson, 0.2f);   // wipe edge
            Gui.Text(new Rect(x + nw + 4f, y + 30f, 200f, 20f), e.Kana, 14, CollMuted, TextAnchor.MiddleLeft, true, false);
            y += 88f;

            // Tagline.
            CollBar(x, y + 2f);
            Gui.PushSkew(0.2f, y + 10f);
            Gui.Text(new Rect(x + 12f, y, w, 20f), Loc.T(e.TaglineKey), 15, CollBody, TextAnchor.MiddleLeft, false, false);
            Gui.Pop();
            y += 30f;

            // Stats: four bars of ten skewed pips filling one after another (30 ms stagger).
            int[] vals = { ch.Power * 2, ch.Speed * 2, ch.Range * 2, ch.Endurance * 2 };
            float colW = Mathf.Min(150f, w / 4f);
            float since = t - collSwapAt;
            for (int s = 0; s < 4; s++)
            {
                float sx = x + s * colW;
                CollMicro(new Rect(sx, y, colW, 14f), Loc.T(CollStatKeys[s]) + "  " + vals[s], CollMuted);
                for (int p = 0; p < 10; p++)
                {
                    bool filled = p < vals[s] && since > (s * 3 + p) * 0.03f;
                    Gui.Skew(new Rect(sx + p * 12f, y + 18f, 9f, 11f), filled ? CollCrimson : CollPipEmpty, 0.3f);
                }
            }
            y += 38f;

            // Meta line.
            Gui.Text(new Rect(x, y, w, 30f), Loc.T("coll.meta", ch.Weapon.ToUpperInvariant(), Mathf.RoundToInt(def.health / 5f), ch.StyleText),
                     12, CollMuted, TextAnchor.UpperLeft, false, false);
            y += 32f;

            // Move list.
            float cIn = x + 8f, cMove = x + 158f, cDmg = x + w - 86f, cSu = x + w - 8f;
            CollMicro(new Rect(cIn, y, 140f, 14f), Loc.T("coll.col_input"), CollMuted);
            CollMicro(new Rect(cMove, y, 200f, 14f), Loc.T("coll.col_move"), CollMuted);
            CollMicro(new Rect(cDmg - 80f, y, 80f, 14f), Loc.T("coll.col_dmg"), CollMuted, TextAnchor.MiddleRight);
            CollMicro(new Rect(cSu - 70f, y, 70f, 14f), Loc.T("coll.col_startup"), CollMuted, TextAnchor.MiddleRight);
            y += 16f;
            Gui.Rect(new Rect(x, y, w, 1f), CollLine);
            Vector2 mouse = Event.current.mousePosition;
            const float rowH = 21f;
            foreach (var m in e.Moves)
            {
                var row = new Rect(x, y + 1f, w, rowH);
                if (row.Contains(mouse)) Gui.Rect(row, new Color(1f, 1f, 1f, 0.05f));
                Gui.Text(new Rect(cIn, row.y, 150f, rowH), m.Input, 12, Color.white, TextAnchor.MiddleLeft, true, false);
                Gui.Text(new Rect(cMove, row.y, cDmg - cMove - 90f, rowH), m.Name, 13, CollBody, TextAnchor.MiddleLeft, false, false);
                Gui.Text(new Rect(cDmg - 90f, row.y, 90f, rowH), m.Dmg, 13, CollBody, TextAnchor.MiddleRight, false, false);
                Gui.Text(new Rect(cSu - 70f, row.y, 70f, rowH), m.Startup, 13, CollBody, TextAnchor.MiddleRight, false, false);
                y += rowH + 1f;
                Gui.Rect(new Rect(x, y, w, 1f), CollLine);
            }

            // Ultimate row: the key, and (rules v26) the camera pose that fires it.
            var ult = new Rect(x, y + 2f, w, 36f);
            if (ult.Contains(mouse)) Gui.Rect(ult, new Color(1f, 1f, 1f, 0.05f));
            Gui.Rect(new Rect(x, ult.y, 3f, ult.height), CollGold);
            Gui.Text(new Rect(cIn, ult.y, w * 0.62f, 18f), Loc.T("coll.ultimate", def.ultName), 13, CollGold, TextAnchor.MiddleLeft, true, false);
            Gui.Text(new Rect(cIn, ult.y + 17f, w * 0.62f, 18f), def.UltDesc, 12, CollGold, TextAnchor.MiddleLeft, false, false);
            Gui.Text(new Rect(x + w * 0.55f, ult.y, w * 0.45f - 8f, ult.height),
                     Loc.T("coll.ult_how", Bindings.Label(0, BindAction.Ultimate), ClassDef.GestureName(def.ultGesture)), 11, Color.white,
                     TextAnchor.MiddleRight, true, false);
            y += 42f;
            Gui.Rect(new Rect(x, y, w, 1f), CollLine);
            y += 6f;

            // Passives.
            string gold = ColorUtility.ToHtmlStringRGB(CollGold), body = ColorUtility.ToHtmlStringRGB(CollBody);
            string pas = "<color=#" + gold + ">" + Loc.T("coll.passive_pick", def.className.ToUpperInvariant()) + "</color>  <color=#" + body + ">";
            for (int i = 0; i < def.passiveNames.Length; i++)
                pas += (i > 0 ? "  ·  " : "") + def.passiveNames[i].ToUpperInvariant() + " — " + def.PassiveDesc(i);
            pas += "  ·  " + Loc.T("coll.crit", Mathf.RoundToInt(def.critRate * 100f), (1f + def.critDmg).ToString("0.0")) + "</color>";
            Gui.Text(new Rect(x, y, w, 36f), pas, 12, Color.white, TextAnchor.UpperLeft, false, false);

            // PREV / NEXT.
            float by = Mathf.Min(H - 30f - 50f, y + 44f);
            if (CollPageButton(new Rect(x, by, 150f, 38f), Loc.T("coll.prev"), Gui.Key("Q", "LB"))) CollSelect(collIndex - 1, -1);
            if (CollPageButton(new Rect(x + 168f, by, 150f, 38f), Loc.T("coll.next"), Gui.Key("E", "RB"))) CollSelect(collIndex + 1, 1);
        }

        bool CollPageButton(Rect r, string label, string key)
        {
            bool click = Gui.Hit(r, out bool hot);
            Gui.Skew(r, hot ? CollCrimson : new Color(0.07f, 0.09f, 0.16f, 0.95f), 0.21f);
            Gui.SkewFrame(r, hot ? Color.white : CollLine, 1f, 0.21f);
            float kw = Gui.KeyWidth(key, 18f);
            Gui.KeyCap(r.x + 14f, r.y + 10f, key, 18f);
            Gui.Display(new Rect(r.x + 20f + kw, r.y, r.width - kw - 24f, r.height), label, 19, Color.white, TextAnchor.MiddleCenter, 0.2f, Color.clear);
            return click;
        }

        /// <summary>4 x 16 crimson accent bar before a section label.</summary>
        static void CollBar(float x, float y) => Gui.Skew(new Rect(x, y, 4f, 14f), CollCrimson, 0.2f);

        /// <summary>Tiny caps micro-label (letter-spaced).</summary>
        static void CollMicro(Rect r, string s, Color c, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            Gui.Text(r, s, 10, c, anchor, true, false);
        }
    }
}
