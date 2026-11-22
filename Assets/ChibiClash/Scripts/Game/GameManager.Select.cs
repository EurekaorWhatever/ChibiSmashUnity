using UnityEngine;
using UnityEngine.InputSystem;

namespace ChibiClash
{
    /// <summary>
    /// Character select (ported from the copy-ghibli-smash fork, UI-1): centre roster grid of our five characters plus a
    /// RANDOM tile, independent P1 / P2 cursors, large character art on each side, per-player loadout (passive +
    /// cosmetic weapon) and the stage carousel with the FIGHT button.
    /// Flow per slot: Picking (move cursor, confirm) -> Loadout (passive / weapon, confirm) -> Ready.
    /// Training: P1 picks the dummy after locking in. VS CPU: the AI's fighter stays a surprise (picked at random when
    /// the fight starts) and its level is chosen on its panel. Arcade: the rival is hidden.
    /// Selecting a character selects its <see cref="FighterClass"/> (decision CH-1); nothing here touches the simulation.
    /// </summary>
    public partial class GameManager
    {
        enum SlotState { Picking, Loadout, Ready }

        const int RosterCols = 3, RosterRows = 2, RosterTiles = RosterCols * RosterRows, RandomTile = RosterTiles - 1;

        readonly SlotState[] slotState = new SlotState[2];
        readonly int[] cursor = new int[2];
        readonly int[] loadRow = new int[2];
        readonly float[] lockTime = { -10f, -10f };
        readonly float[] previewTime = { -10f, -10f };
        float stageAnim, stageTarget;
        bool selectMouse;

        static bool TileIsFighter(int t) => t >= 0 && t < CharacterRoster.All.Length;
        static int Mod(int a, int n) => ((a % n) + n) % n;

        bool Solo => pending.cpu[1];
        /// <summary>The opponent is not picked on this screen (arcade ladder, or the VS CPU surprise pick).</summary>
        bool HiddenRival => arcade || aiPicksItself;
        bool AllReady => slotState[0] == SlotState.Ready && slotState[1] == SlotState.Ready;
        /// <summary>The slot P1's inputs drive: their own until ready, then the dummy's in training.</summary>
        int P1Slot => Solo && !HiddenRival && slotState[0] == SlotState.Ready ? 1 : 0;
        /// <summary>The slot mouse clicks act on.</summary>
        int MouseSlot => !Solo && slotState[0] == SlotState.Ready && slotState[1] != SlotState.Ready ? 1 : P1Slot;

        void EnterSelect()
        {
            float now = Time.unscaledTime;
            for (int i = 0; i < 2; i++)
            {
                slotState[i] = SlotState.Picking;
                cursor[i] = (int)pending.cls[i];
                loadRow[i] = 0;
                previewTime[i] = now;
                lockTime[i] = -10f;
                pending.weapon[i] = Mathf.Clamp(pending.weapon[i], 0, WeaponDef.ForClass(pending.cls[i]).Count - 1);
            }
            if (HiddenRival) slotState[1] = SlotState.Ready;
            if (arcade) pending.stage = ArcadeStage(arcadeRound);
            pending.stage = Mathf.Clamp(pending.stage, 0, StageBuilder.Names.Length - 1);
            stageTarget = stageAnim = StagePos(pending.stage);   // the carousel scrolls menu positions, not sim indices
            selectMouse = false;
        }

        // ---------------------------------------------------------------- input

        void UpdateSelect()
        {
            // Select uses its own per-player cursors instead of Gui's single focus.
            Gui.NavMode = false;
            Gui.ResetFocus();
            var m = Mouse.current;
            if (m != null && (m.delta.ReadValue().sqrMagnitude > 4f || m.leftButton.wasPressedThisFrame)) selectMouse = true;
            if (MenuInput.AnyFired()) selectMouse = false;

            bool solo = Solo;
            if (AllReady)
            {
                bool Pressed(NavCmd c) => MenuInput.Player(0, c, solo) || (!solo && MenuInput.Player(1, c, false));
                if (Pressed(NavCmd.Left)) StageStep(-1);
                if (Pressed(NavCmd.Right)) StageStep(1);
                if (Pressed(NavCmd.Alt)) ToggleHazards();
                if (VsAiSelect && MenuInput.Player(0, NavCmd.Up, solo)) CpuLevel(1);
                if (VsAiSelect && MenuInput.Player(0, NavCmd.Down, solo)) CpuLevel(-1);
                if (Pressed(NavCmd.Confirm)) Confirm();
                else if (MenuInput.Player(0, NavCmd.Back, solo)) Unready(solo && !HiddenRival ? 1 : 0);
                else if (!solo && MenuInput.Player(1, NavCmd.Back, false)) Unready(1);
                return;
            }
            SlotInput(P1Slot, 0, solo);
            if (!solo) SlotInput(1, 1, false);
        }

        bool VsAiSelect => aiPicksItself && !arcade;

        void CpuLevel(int d)
        {
            UiSound.Step();
            Save.settings.cpuDifficulty = Mathf.Clamp(Save.settings.cpuDifficulty + d, 1, 3);
            Save.Save();
        }

        void SlotInput(int s, int player, bool solo)
        {
            bool P(NavCmd c) => MenuInput.Player(player, c, solo);
            switch (slotState[s])
            {
                case SlotState.Picking:
                    if (P(NavCmd.Left)) MoveCursor(s, -1, 0);
                    if (P(NavCmd.Right)) MoveCursor(s, 1, 0);
                    if (P(NavCmd.Up)) MoveCursor(s, 0, -1);
                    if (P(NavCmd.Down)) MoveCursor(s, 0, 1);
                    if (P(NavCmd.Alt))
                    {
                        SetCursor(s, RandomTile);
                        LockIn(s);
                    }
                    else if (P(NavCmd.Confirm)) LockIn(s);
                    else if (P(NavCmd.Back))
                    {
                        if (s == 1 && solo) { slotState[0] = SlotState.Loadout; UiSound.Back(); }    // back to P1's own loadout
                        else if (s == 0) LeaveSelect();
                    }
                    break;
                case SlotState.Loadout:
                    int rows = LoadoutRows(s);
                    if (P(NavCmd.Up)) { loadRow[s] = (loadRow[s] + rows - 1) % rows; UiSound.Navigate(); }
                    if (P(NavCmd.Down)) { loadRow[s] = (loadRow[s] + 1) % rows; UiSound.Navigate(); }
                    if (P(NavCmd.Left)) ChangeLoadout(s, -1);
                    if (P(NavCmd.Right)) ChangeLoadout(s, 1);
                    if (P(NavCmd.Confirm)) SetReady(s);
                    else if (P(NavCmd.Back)) { slotState[s] = SlotState.Picking; UiSound.Back(); }
                    break;
                case SlotState.Ready:
                    if (P(NavCmd.Back)) Unready(s);
                    break;
            }
        }

        void MoveCursor(int s, int dx, int dy)
        {
            int col = Mod(cursor[s] % RosterCols + dx, RosterCols);
            int row = Mod(cursor[s] / RosterCols + dy, RosterRows);
            SetCursor(s, row * RosterCols + col);
        }

        void SetCursor(int s, int t)
        {
            if (cursor[s] == t) return;
            cursor[s] = t;
            UiSound.Navigate();
            if (TileIsFighter(t)) SetPreview(s, CharacterRoster.All[t].Class);
            else previewTime[s] = Time.unscaledTime;
        }

        void SetPreview(int s, FighterClass cls)
        {
            if (pending.cls[s] == cls) return;
            pending.cls[s] = cls;
            pending.passive[s] = 0;
            pending.weapon[s] = 0;
            previewTime[s] = Time.unscaledTime;
        }

        void LockIn(int s)
        {
            int t = cursor[s];
            if (t == RandomTile)
            {
                var cls = (FighterClass)Random.Range(0, CharacterRoster.All.Length);
                pending.cls[s] = cls;
                pending.passive[s] = Random.Range(0, 2);
                pending.weapon[s] = Random.Range(0, WeaponDef.ForClass(cls).Count);
            }
            else if (TileIsFighter(t)) SetPreview(s, CharacterRoster.All[t].Class);
            else return;
            UiSound.LockIn();
            slotState[s] = SlotState.Loadout;
            loadRow[s] = 0;
            lockTime[s] = previewTime[s] = Time.unscaledTime;
        }

        int LoadoutRows(int s) => WeaponDef.ForClass(pending.cls[s]).Count > 1 ? 2 : 1;

        void ChangeLoadout(int s, int d)
        {
            UiSound.Step();
            if (loadRow[s] == 0) pending.passive[s] = Mod(pending.passive[s] + d, 2);
            else
            {
                int n = WeaponDef.ForClass(pending.cls[s]).Count;
                pending.weapon[s] = Mod(pending.weapon[s] + d, n);
            }
        }

        void SetReady(int s)
        {
            UiSound.Confirm();
            slotState[s] = SlotState.Ready;
            lockTime[s] = Time.unscaledTime;
        }

        void Unready(int s)
        {
            if (s == 1 && HiddenRival) s = 0;
            slotState[s] = SlotState.Loadout;
            UiSound.Back();
        }

        void StageStep(int d)
        {
            if (arcade) { UiSound.Error(); return; }
            UiSound.Navigate();
            stageTarget += d;
            pending.stage = StageAt(Mathf.RoundToInt(stageTarget));
        }

        /// <summary>Stage hazards on / off for the next match (ST-5; moving platforms move either way).</summary>
        void ToggleHazards()
        {
            pending.hazards = !pending.hazards;
            UiSound.Step();
        }

        void LeaveSelect()
        {
            UiSound.Back();
            // Vs Local (Solo) came through the play-style screen: back goes there; Vs CPU goes back to Play.
            bool local = !arcade && !pending.training && !pending.cpu[1];
            var to = arcade ? Page.Arcade : pending.training ? Page.Training : local ? Page.PlayStyle : Page.Play;
            Wipe(null, () =>
            {
                arcade = false;
                if (local) playStyleOnline = false;
                Enter(to);
            });
        }

        /// <summary>FIGHT button: lock everyone in with their current picks and go.</summary>
        void QuickFight()
        {
            for (int s = 0; s < 2; s++)
            {
                if (slotState[s] == SlotState.Picking && cursor[s] == RandomTile) LockIn(s);
                slotState[s] = SlotState.Ready;
            }
            Confirm();
        }

        string ModeLabel()
        {
            if (arcade) return "ARCADE  ·  ROUND " + (arcadeRound + 1) + " / " + ArcadeRounds;
            if (pending.training) return "TRAINING";
            if (pending.cpu[1]) return "VS CPU  ·  " + DifficultyUpper[Save.settings.cpuDifficulty];
            return "LOCAL VERSUS";
        }

        string SlotName(int s)
        {
            if (s == 0)
            {
                string me = Netcode.ChibiSocialRules.CleanSavedName(Save.playerName);   // P1 is the profile owner
                return me.Length > 0 ? me.ToUpperInvariant() : "PLAYER 1";
            }
            if (arcade) return "ARCADE RIVAL";
            if (pending.training) return "TRAINING DUMMY";
            if (pending.cpu[1]) return "CPU  ·  " + DifficultyUpper[Save.settings.cpuDifficulty];
            return "PLAYER 2";
        }

        string ConfirmKey(int s) => MenuInput.UsingGamepad ? "A" : (Solo || s == 0 ? "SPACE" : "ENTER");

        // ---------------------------------------------------------------- drawing

        void SelectView()
        {
            float t = Time.unscaledTime;
            var full = new Rect(0f, 0f, W, H);
            float sideW = Mathf.Clamp((W - 520f) * 0.5f, 300f, 520f);
            Gui.Rect(full, Gui.Navy);
            Gui.Hatch(full, new Color(1f, 1f, 1f, 0.018f), 22f, t * 0.04f);
            Gui.Rect(new Rect(sideW, 0f, W - 2f * sideW, H), new Color(0.04f, 0.07f, 0.13f, 0.55f));
            Gui.SpeedLines(new Rect(sideW, 0f, W - 2f * sideW, H), new Color(1f, 1f, 1f, 0.03f), 12, 300f, 9);
            Gui.Grain(full, 0.05f);

            SidePanel(0, new Rect(0f, 0f, sideW, H));
            SidePanel(1, new Rect(W - sideW, 0f, sideW, H));

            float cx = W * 0.5f;
            float ha = Gui.EaseOut(Since / 0.35f);
            Gui.Alpha = ha;
            Gui.Display(new Rect(0f, 8f - (1f - ha) * 20f, W, 52f), "CHARACTER SELECT", 42, Gui.Paper, TextAnchor.MiddleCenter);
            var ml = ModeLabel();
            float mw = Gui.Measure(ml, 13) + 30f;
            var mr = new Rect(cx - mw * 0.5f, 58f, mw, 20f);
            Gui.Skew(mr, arcade ? Gui.P1 : Gui.Steel, 0.3f);
            Gui.Text(mr, ml, 13, Gui.Paper, TextAnchor.MiddleCenter, true, false);
            Gui.Alpha = 1f;

            if (Gui.Button(new Rect(16f, 12f, 112f, 32f), "BACK", Gui.Steel, 14)) LeaveSelect();

            Roster(new Rect(cx - 238f, 90f, 476f, 290f));
            StageCarousel(cx, 392f);
            FightButton(new Rect(cx - 150f, 586f, 300f, 54f));

            if (AllReady)
                Gui.HintBar(ml, Gui.Key(Solo ? "A / D" : "A D / ARROWS", "D-PAD"), "STAGE", Gui.Key(Solo ? "R" : "R / DEL", "Y"), "HAZARDS",
                            Gui.Key(Solo ? "SPACE" : "SPACE / ENTER", "A"), "FIGHT", Gui.Key(Solo ? "ESC" : "ESC / BKSP", "B"), "CHANGE");
            else if (Solo)
                Gui.HintBar(ml, Gui.Key("WASD", "D-PAD"), "MOVE", Gui.Key("SPACE", "A"), "CONFIRM", Gui.Key("R", "Y"), "RANDOM", Gui.Key("ESC", "B"), "BACK");
            else
                Gui.HintBar(MenuInput.UsingGamepad ? ml + "   ·   GAMEPAD 1 = 1P, GAMEPAD 2 = 2P" : ml + "   ·   1P  WASD  SPACE  ESC  R      2P  ARROWS  ENTER  BACKSPACE  DEL",
                    Gui.Key("SPACE / ENTER", "A"), "CONFIRM", Gui.Key("R / DEL", "Y"), "RANDOM", Gui.Key("ESC / BKSP", "B"), "BACK");
        }

        void Roster(Rect g)
        {
            const float gap = 10f;
            float tw = (g.width - gap * (RosterCols - 1)) / RosterCols;
            float th = (g.height - gap * (RosterRows - 1)) / RosterRows;
            Gui.Skew(new Rect(g.x - 16f, g.y - 12f, g.width + 32f, g.height + 24f), new Color(0.04f, 0.07f, 0.13f, 0.92f), 0.05f);
            Gui.Skew(new Rect(g.x - 16f, g.y - 12f, g.width * 0.5f + 16f, 3f), Gui.P1, 0.05f);
            Gui.Skew(new Rect(g.center.x, g.y - 12f, g.width * 0.5f + 16f, 3f), Gui.P2, 0.05f);
            for (int t = 0; t < RosterTiles; t++)
            {
                float ap = Gui.EaseOut((Since - 0.08f - t * 0.05f) / 0.3f);
                var r = new Rect(g.x + (t % RosterCols) * (tw + gap), g.y + (t / RosterCols) * (th + gap) + (1f - ap) * 24f, tw, th);
                Gui.Alpha = ap;
                Tile(t, r);
                Gui.Alpha = 1f;
            }
        }

        bool CursorVisible(int s)
        {
            if (s == 1 && HiddenRival) return false;
            // the dummy's cursor appears once P1 starts picking it
            if (s == 1 && Solo) return slotState[0] == SlotState.Ready || slotState[1] != SlotState.Picking;
            return true;
        }

        void Tile(int t, Rect r)
        {
            float now = Time.unscaledTime;
            bool random = t == RandomTile;
            var ch = TileIsFighter(t) ? CharacterRoster.All[t] : null;

            int ms = MouseSlot;
            bool mouseOk = slotState[ms] != SlotState.Ready && !(HiddenRival && ms == 1);
            bool click = Gui.Hit(r, out bool hot, mouseOk);
            hot &= selectMouse;
            if (hot && slotState[ms] == SlotState.Picking) SetCursor(ms, t);
            if (click)
            {
                slotState[ms] = SlotState.Picking;
                SetCursor(ms, t);
                LockIn(ms);
            }

            Color fill = random ? new Color(0.13f, 0.11f, 0.07f) : Color.Lerp(new Color(0.08f, 0.12f, 0.22f), ch.Primary, 0.25f);
            Gui.Rect(new Rect(r.x + 4f, r.y + 4f, r.width, r.height), new Color(0f, 0f, 0f, 0.45f));
            Gui.Rect(r, fill);
            var bar = new Rect(r.x, r.yMax - 30f, r.width, 30f);
            if (ch != null)
            {
                Gui.HalftoneFade(new Rect(r.x, r.y, r.width, r.height * 0.55f), WithA(ch.Accent, 0.3f));
                float ps = Mathf.Min(r.width, r.height - 26f);
                CharacterArt.DrawPortrait(new Rect(r.center.x - ps * 0.5f, r.y + 2f, ps, ps), ch);
                Gui.Rect(bar, new Color(0.03f, 0.05f, 0.1f, 0.92f));
                Gui.Rect(new Rect(bar.x, bar.y, bar.width, 2f), ch.Primary);
                Gui.Display(new Rect(bar.x + 8f, bar.y + 1f, bar.width - 16f, 18f), Hud.CharNameUpper(ch.Class), 17, Gui.Paper, TextAnchor.MiddleLeft, 0.15f);
                Gui.Text(new Rect(bar.x + 8f, bar.y + 17f, bar.width - 16f, 12f), ch.Role.ToUpper(), 9, ch.Accent, TextAnchor.MiddleLeft, true, false);
            }
            else
            {
                Gui.Hatch(r, WithA(Gui.Gold, 0.08f), 12f, now * 0.3f);
                Gui.Display(new Rect(r.x, r.y, r.width, r.height - 26f), "?", 64, Gui.Gold, TextAnchor.MiddleCenter, 0.15f);
                Gui.Rect(bar, new Color(0.03f, 0.05f, 0.1f, 0.92f));
                Gui.Rect(new Rect(bar.x, bar.y, bar.width, 2f), Gui.Gold);
                Gui.Display(new Rect(bar.x + 8f, bar.y + 1f, bar.width - 16f, 18f), "RANDOM", 17, Gui.Gold, TextAnchor.MiddleLeft, 0.15f);
                Gui.Text(new Rect(bar.x + 8f, bar.y + 17f, bar.width - 16f, 12f), "LEAVE IT TO FATE", 9, Gui.Muted, TextAnchor.MiddleLeft, true, false);
            }
            Gui.Frame(r, Gui.Steel, 1f);
            if (hot) Gui.Frame(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), Gui.Paper, 2f);

            bool both = cursor[0] == t && cursor[1] == t && CursorVisible(0) && CursorVisible(1);
            for (int s = 0; s < 2; s++)
            {
                if (cursor[s] != t || !CursorVisible(s)) continue;
                var pc = Gui.PlayerColor(s);
                bool lockedIn = slotState[s] != SlotState.Picking;
                float inset = both && s == 1 ? 3f : -3f;
                Gui.Frame(new Rect(r.x - inset, r.y - inset, r.width + inset * 2f, r.height + inset * 2f), pc, lockedIn ? 4f : 3f);
                float flash = 1f - Mathf.Clamp01((now - lockTime[s]) / 0.35f);
                if (flash > 0f) Gui.Rect(r, WithA(Gui.Paper, 0.6f * flash));
                float bob = lockedIn ? 0f : Mathf.Sin(now * 8f + s * 1.7f) * 2f;
                string label = s == 0 ? "1P" : pending.training ? "DUMMY" : Solo ? "CPU" : "2P";
                float lw = Gui.Measure(label, 12) + 14f;
                var tag = s == 0 ? new Rect(r.x - 6f, r.y - 9f + bob, lw, 16f) : new Rect(r.xMax + 6f - lw, r.y - 9f + bob, lw, 16f);
                Gui.Skew(tag, pc, 0.3f);
                Gui.Text(tag, label, 12, Gui.Paper, TextAnchor.MiddleCenter, true, false);
                if (lockedIn)
                {
                    var ok = s == 0 ? new Rect(r.x + 2f, r.yMax - 46f, 24f, 13f) : new Rect(r.xMax - 26f, r.yMax - 46f, 24f, 13f);
                    Gui.Skew(ok, Gui.Gold, 0.3f);
                    Gui.Text(ok, "OK", 11, Gui.Ink, TextAnchor.MiddleCenter, true, false);
                }
            }
        }

        void SidePanel(int s, Rect z)
        {
            float now = Time.unscaledTime;
            bool right = s == 1;
            var pc = Gui.PlayerColor(s);
            var anchor = right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            float enter = Gui.EaseOut((Since - 0.05f) / 0.45f);
            float dir = right ? 1f : -1f;

            bool rivalHidden = right && HiddenRival;
            bool mystery = rivalHidden || (slotState[s] == SlotState.Picking && !TileIsFighter(cursor[s]));
            var ch = CharacterRoster.Of(pending.cls[s]);
            var tint = mystery ? pc : ch.Primary;

            // slanted colour slab behind the fighter, tinted with the character's palette
            var slab = right ? new Rect(z.x + 30f, 72f, z.width + 40f, H - 108f) : new Rect(z.x - 70f, 72f, z.width + 40f, H - 108f);
            slab.x += dir * (1f - enter) * 120f;
            Gui.Skew(slab, new Color(Mathf.Lerp(Gui.Navy.r, tint.r, 0.22f), Mathf.Lerp(Gui.Navy.g, tint.g, 0.22f), Mathf.Lerp(Gui.Navy.b, tint.b, 0.22f), 0.96f), 0.2f);
            var edge = right ? new Rect(slab.x - 4f, slab.y, 12f, slab.height) : new Rect(slab.xMax - 8f, slab.y, 12f, slab.height);
            Gui.Skew(edge, pc, 0.2f);
            Gui.Skew(new Rect(edge.x + (right ? -12f : 16f), slab.y, 4f, slab.height), Gui.Paper, 0.2f);
            Gui.HalftoneFade(new Rect(z.x, 72f, z.width, 280f), WithA(mystery ? pc : ch.Accent, 0.22f), right);
            Gui.Display(new Rect(z.x + 16f, 70f, z.width - 32f, 240f), right ? (Solo ? (pending.training ? "DMY" : "CPU") : "2P") : "1P", 220, WithA(pc, 0.12f), anchor, 0.2f, Color.clear);

            // character art (or the mystery card)
            float T = H - 372f;
            float S = Mathf.Min(z.width * 1.05f, 420f);
            float k = Gui.EaseOut((now - previewTime[s]) / 0.35f) * enter;
            var art = new Rect(z.x + z.width * 0.5f - S * 0.5f - dir * 12f + dir * (1f - k) * 50f, T - S + 70f, S, S);
            var def = ClassDef.Get(pending.cls[s]);
            var weapon = WeaponDef.Get(def.type, pending.weapon[s]);
            if (!mystery) CharacterArt.DrawBody(art, ch, k, right);
            else
            {
                Gui.Glow(new Rect(art.x + S * 0.15f, art.y + S * 0.15f, S * 0.7f, S * 0.7f), WithA(pc, 0.25f * k));
                Gui.Display(new Rect(art.x, art.y + S * 0.12f, S, S * 0.7f), "?", 300, WithA(rivalHidden || cursor[s] == RandomTile ? Gui.Gold : Gui.Muted, 0.75f * k), TextAnchor.MiddleCenter, 0.2f);
            }
            float flash = 1f - Mathf.Clamp01((now - lockTime[s]) / 0.4f);
            if (flash > 0f) Gui.Glow(new Rect(art.x - 40f, art.y - 40f, S + 80f, S + 80f), WithA(Gui.Paper, 0.45f * flash));

            // info panel
            Gui.FadeUp(new Rect(z.x, T - 110f, z.width, 140f), WithA(Gui.Navy, 0.96f));
            Gui.Rect(new Rect(z.x, T + 30f, z.width, H - 30f - (T + 30f)), WithA(Gui.Navy, 0.96f));

            float ix = z.x + 26f, iw = z.width - 52f;
            string who = SlotName(s);
            float cw = Gui.Measure(who, 13) + 26f;
            var chip = right ? new Rect(z.xMax - 26f - cw, 84f, cw, 24f) : new Rect(ix, 84f, cw, 24f);
            Gui.Skew(chip, pc, 0.3f);
            Gui.Text(chip, who, 13, Gui.Paper, TextAnchor.MiddleCenter, true, false);
            string state;
            Color stateC = Gui.Muted;
            bool blink = false;
            if (rivalHidden) { state = arcade ? "ROUND " + (arcadeRound + 1) + " RIVAL" : "PICKS AT FIGHT START"; stateC = Gui.Gold; }
            else if (slotState[s] == SlotState.Ready) { state = "READY!"; stateC = Gui.Gold; }
            else if (slotState[s] == SlotState.Loadout) { state = "LOADOUT"; stateC = Gui.Paper; }
            else if (s == 1 && Solo && slotState[0] != SlotState.Ready) state = "WAITING FOR 1P";
            else if (s == 1 && Solo) { state = "1P PICKS THE DUMMY"; stateC = Gui.Paper; blink = true; }
            else { state = "SELECTING"; blink = true; }
            if (blink) stateC = WithA(stateC, 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(now * 3f)));
            Gui.Text(new Rect(ix, 110f, iw, 20f), state, 13, stateC, anchor, true, false);

            if (slotState[s] == SlotState.Ready && !rivalHidden)
            {
                float st = Gui.EaseOut((now - lockTime[s]) / 0.25f);
                float sc = Mathf.Lerp(1.6f, 1f, st);
                var stamp = new Rect(z.x + z.width * 0.5f - 130f * sc, T - 150f - 30f * sc, 260f * sc, 60f * sc);
                Gui.Skew(stamp, WithA(Gui.Gold, 0.92f * st), 0.25f);
                Gui.Display(stamp, "READY", Mathf.RoundToInt(46f * sc), WithA(Gui.Ink, st), TextAnchor.MiddleCenter, 0.2f, Color.clear);
            }

            string name, role, line;
            if (rivalHidden && arcade) { name = "???"; role = "IDENTITY HIDDEN"; line = "REVEALED WHEN THE ROUND STARTS"; }
            else if (rivalHidden) { name = "???"; role = "THE AI PICKS ITS FIGHTER"; line = "A RANDOM CHARACTER EVERY FIGHT"; }
            else if (mystery) { name = "RANDOM"; role = "LEAVE IT TO FATE"; line = "ANY CHARACTER · RANDOM PASSIVE AND WEAPON"; }
            else
            {
                name = Hud.CharNameUpper(ch.Class);
                role = ch.Role.ToUpper();
                line = "SIGNATURE WEAPON  ·  " + ch.Weapon.ToUpper();
            }
            Gui.Display(new Rect(ix, T - 4f, iw, 68f), name, 62, Gui.Paper, anchor, 0.2f, mystery ? pc : ch.Primary, 4f);
            Gui.Display(new Rect(ix, T + 60f, iw, 24f), role, 20, Gui.Gold, anchor, 0.18f);
            Gui.Text(new Rect(ix, T + 84f, iw, 16f), line, 12, Gui.Muted, anchor, true, false);

            if (rivalHidden)
            {
                if (arcade) ArcadeLadder(new Rect(ix, T + 110f, iw, H - 44f - (T + 110f)));
                else CpuLevelPanel(new Rect(ix, T + 110f, iw, H - 44f - (T + 110f)));
                return;
            }
            if (mystery) return;

            Gui.Text(new Rect(ix, T + 100f, iw, 28f), ch.Style, 11, Gui.Paper, right ? TextAnchor.UpperRight : TextAnchor.UpperLeft, false, false);
            // palette swatches (main, accent, effect colour from the character sheet)
            for (int p = 0; p < 3; p++)
            {
                var col = p == 0 ? ch.Primary : p == 1 ? ch.Accent : ch.Fx;
                float sx = right ? ix + iw - 18f - p * 22f : ix + p * 22f;
                Gui.Skew(new Rect(sx, 134f, 16f, 10f), col, 0.3f);
            }

            float sy = T + 130f;
            for (int i = 0; i < 5; i++)
                StatRow(ix, sy + i * 12f, iw, StatNames[i], Stat(ch, i), pc);
            Gui.Text(new Rect(ix, sy + 61f, iw, 14f), "<color=#F6C74C>ULT</color>  " + def.ultName + "  <color=#9AA6BC>·  " + ClassDef.GestureName(def.ultGesture) + "</color>", 11, Gui.Paper, anchor, true, false);

            LoadoutPanel(s, def, new Rect(ix, T + 210f, iw, H - 36f - (T + 210f)));
        }

        static readonly string[] StatDigits = { "0", "1", "2", "3", "4", "5" };

        /// <summary>Reference stat bar from the character sheet, 1..5 (display only; real numbers live in the simulation).</summary>
        void StatRow(float x, float y, float w, string label, int v, Color c)
        {
            Gui.Text(new Rect(x, y - 1f, 80f, 12f), label, 9, Gui.Muted, TextAnchor.MiddleLeft, true, false);
            Gui.Meter(new Rect(x + 82f, y + 1f, w - 110f, 8f), v / 5f, c, 5);
            Gui.Text(new Rect(x + w - 20f, y - 1f, 20f, 12f), StatDigits[Mathf.Clamp(v, 0, 5)], 10, Gui.Paper, TextAnchor.MiddleRight, true, false);
        }

        void LoadoutPanel(int s, ClassDef def, Rect r)
        {
            var pc = Gui.PlayerColor(s);
            var st = slotState[s];
            bool picking = st == SlotState.Picking;
            bool focusRows = st == SlotState.Loadout && (s == P1Slot || (!Solo && s == 1));
            Gui.Alpha = picking ? 0.4f : 1f;

            // passive
            Gui.Text(new Rect(r.x, r.y, r.width, 16f), "PASSIVE", 11, Gui.Gold, TextAnchor.MiddleLeft, true, false);
            if (focusRows && loadRow[s] == 0) Gui.Chevron(new Rect(r.x - 16f, r.y + 20f, 8f, 16f), pc);
            float pw = (r.width - 8f) * 0.5f;
            for (int p = 0; p < 2; p++)
            {
                if (Gui.Button(new Rect(r.x + p * (pw + 8f), r.y + 17f, pw, 26f), def.passiveNames[p].ToUpper(), pc, 12, !picking, pending.passive[s] == p))
                {
                    pending.passive[s] = p;
                    loadRow[s] = 0;
                }
            }
            Gui.Text(new Rect(r.x, r.y + 44f, r.width, 16f), def.passiveDescs[pending.passive[s]], 11, Gui.Muted, TextAnchor.MiddleLeft, false, false);

            // Secondary: the cosmetic weapon look from the armory (the signature weapon is shown above). Only classes with
            // more than one look get the selector.
            var variants = WeaponDef.ForClass(def.type);
            float wy = r.y + 62f;
            var weapon = WeaponDef.Get(def.type, pending.weapon[s]);
            if (variants.Count > 1)
            {
                Gui.Text(new Rect(r.x, wy, 70f, 24f), "LOOK", 11, Gui.Gold, TextAnchor.MiddleLeft, true, false);
                if (focusRows && loadRow[s] == 1) Gui.Chevron(new Rect(r.x - 16f, wy + 4f, 8f, 16f), pc);
                if (Gui.Hit(new Rect(r.x + 64f, wy, 26f, 24f), out bool hl, !picking)) { loadRow[s] = 1; ChangeLoadout(s, -1); }
                Gui.Chevron(new Rect(r.x + 72f, wy + 5f, 9f, 14f), hl ? Gui.Paper : pc, false);
                Gui.Text(new Rect(r.x + 92f, wy, r.width - 184f, 24f), weapon.name.ToUpper() + "  <color=#9AA6BC>" + (pending.weapon[s] + 1) + "/" + variants.Count + "</color>", 13, Gui.Paper, TextAnchor.MiddleCenter, true, false);
                if (Gui.Hit(new Rect(r.xMax - 90f, wy, 26f, 24f), out bool hr, !picking)) { loadRow[s] = 1; ChangeLoadout(s, 1); }
                Gui.Chevron(new Rect(r.xMax - 82f, wy + 5f, 9f, 14f), hr ? Gui.Paper : pc);
                Gui.Text(new Rect(r.x, wy, r.width, 24f), "COSMETIC", 10, Gui.Muted, TextAnchor.MiddleRight, true, false);
            }
            Gui.Alpha = 1f;

            // status / action row
            var row = new Rect(r.x, r.yMax - 28f, r.width, 28f);
            if (picking)
            {
                bool cpuWaiting = s == 1 && Solo && slotState[0] != SlotState.Ready;
                if (!cpuWaiting)
                {
                    float kw = Gui.KeyCap(row.x, row.y + 4f, ConfirmKey(s), 20f);
                    Gui.Text(new Rect(row.x + kw + 8f, row.y, row.width - kw - 8f, row.height), "LOCK IN  ·  OR CLICK A CHARACTER", 12, Gui.Paper, TextAnchor.MiddleLeft, true, false);
                }
            }
            else if (st == SlotState.Loadout)
            {
                if (Gui.Button(new Rect(row.x, row.y, 150f, row.height), "READY!", pc, 15)) SetReady(s);
                float kx = row.x + 164f;
                kx += Gui.KeyCap(kx, row.y + 4f, ConfirmKey(s), 20f) + 6f;
                Gui.Text(new Rect(kx, row.y, row.xMax - kx, row.height), "TO READY UP", 11, Gui.Muted, TextAnchor.MiddleLeft, true, false);
            }
            else
            {
                Gui.Skew(new Rect(row.x, row.y + 2f, 120f, row.height - 4f), Gui.Gold, 0.3f);
                Gui.Display(new Rect(row.x, row.y, 120f, row.height), "READY", 20, Gui.Ink, TextAnchor.MiddleCenter, 0.15f, Color.clear);
                if (Gui.Button(new Rect(row.x + 134f, row.y, 110f, row.height), "CHANGE", Gui.Steel, 12)) Unready(s);
            }
        }

        /// <summary>VS CPU: the AI's level (Easy / Normal / Hard), saved as the profile's CPU difficulty.</summary>
        void CpuLevelPanel(Rect r)
        {
            Gui.Text(new Rect(r.x, r.y, r.width, 16f), "AI LEVEL", 11, Gui.Gold, TextAnchor.MiddleLeft, true, false);
            float bw = (r.width - 16f) / 3f;
            for (int lvl = 1; lvl <= 3; lvl++)
                if (Gui.Button(new Rect(r.x + (lvl - 1) * (bw + 8f), r.y + 20f, bw, 34f), DifficultyUpper[lvl], Gui.P2, 14, true, Save.settings.cpuDifficulty == lvl))
                {
                    Save.settings.cpuDifficulty = lvl;
                    Save.Save();
                }
            Gui.Text(new Rect(r.x, r.y + 60f, r.width, 34f), "No internet needed. The AI picks a new character, passive and weapon for every fight, rematches too.", 11, Gui.Muted, TextAnchor.UpperLeft, false, false);
            if (AllReady)
                Gui.Text(new Rect(r.x, r.y + 96f, r.width, 16f), MenuInput.UsingGamepad ? "D-PAD UP / DOWN  ·  AI LEVEL" : "W / S  ·  AI LEVEL", 10, Gui.Muted, TextAnchor.MiddleLeft, true, false);
        }

        void ArcadeLadder(Rect r)
        {
            float rh = Mathf.Min(30f, (r.height - 4f) / ArcadeRounds);
            for (int i = 0; i < ArcadeRounds; i++)
            {
                var row = new Rect(r.x, r.y + i * rh, r.width, rh - 4f);
                bool done = i < arcadeRound, current = i == arcadeRound;
                Gui.Skew(row, current ? WithA(Gui.P2, 0.85f) : new Color(0.07f, 0.11f, 0.2f, 0.9f), 0.25f);
                Gui.Text(new Rect(row.x + 12f, row.y, 90f, row.height), i == ArcadeRounds - 1 ? "FINAL" : "ROUND " + (i + 1), 12, current ? Gui.Paper : Gui.Muted, TextAnchor.MiddleLeft, true, false);
                Gui.Text(new Rect(row.x + 100f, row.y, 120f, row.height), StageLabel(ArcadeStage(i), 116f, 12), 12, Gui.Paper, TextAnchor.MiddleLeft, true, false);
                string rival = done ? Hud.CharNameUpper(arcadeLadder[i]) + "  DEFEATED" : "???";
                Gui.Text(new Rect(row.x, row.y, row.width - 12f, row.height), rival, 12, done ? Gui.Gold : Gui.Paper, TextAnchor.MiddleRight, true, false);
            }
        }

        // ---------------------------------------------------------------- stage carousel

        void StageCarousel(float cx, float y)
        {
            int n = StageCount;
            bool focus = AllReady;
            Gui.Text(new Rect(cx - 250f, y, 500f, 18f), arcade ? "ARCADE ROUTE  ·  STAGE " + (arcadeRound + 1) + " / " + ArcadeRounds : "SELECT STAGE", 13, focus ? Gui.Gold : Gui.Muted, TextAnchor.MiddleCenter, true, false);
            float py = y + 24f;
            const float bigW = 230f, bigH = 104f, spread = 168f;
            int baseI = Mathf.RoundToInt(stageAnim);
            for (int pass = 0; pass < 2; pass++)
                for (int k = -2; k <= 2; k++)
                {
                    int v = baseI + k;
                    float off = v - stageAnim, ao = Mathf.Abs(off);
                    if (ao > 1.5f) continue;
                    bool center = ao < 0.5f;
                    if ((pass == 1) != center) continue;
                    float sc = Mathf.Lerp(1f, 0.5f, Mathf.Clamp01(ao));
                    float w = bigW * sc, h = bigH * sc;
                    var r = new Rect(cx + off * spread - w * 0.5f, py + (bigH - h) * 0.5f, w, h);
                    int idx = StageAt(v);   // carousel position -> sim stage (MenuOrder)
                    float alpha = Mathf.Clamp01(1.3f - ao * 0.55f);
                    bool hot = false;
                    if (!center && !arcade && Gui.Hit(r, out hot)) StageStep(off > 0f ? 1 : -1);
                    Gui.Rect(new Rect(r.x + 5f, r.y + 5f, r.width, r.height), new Color(0f, 0f, 0f, 0.45f * alpha));
                    StagePreview(r, idx, alpha);
                    if (center)
                    {
                        float pulse = focus ? 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 7f) : 1f;
                        Gui.Frame(new Rect(r.x - 3f, r.y - 3f, r.width + 6f, r.height + 6f), focus ? WithA(Gui.Gold, pulse) : Gui.Paper, 3f);
                        Gui.Skew(new Rect(r.x - 8f, r.y - 8f, 46f, 6f), Gui.P1, 0.6f);
                        Gui.Skew(new Rect(r.xMax - 38f, r.yMax + 2f, 46f, 6f), Gui.P2, 0.6f);
                    }
                    else
                    {
                        Gui.Rect(r, new Color(0.02f, 0.04f, 0.08f, 0.45f));
                        Gui.Frame(r, hot ? Gui.Paper : Gui.Steel, hot ? 2f : 1f);
                    }
                }

            float ny = py + bigH + 6f;
            int cur = Mathf.Clamp(pending.stage, 0, StageBuilder.Names.Length - 1);
            int curPos = StagePos(cur);
            string stageName = StageBuilder.Names[cur].ToUpperInvariant();
            int nameSize = 30;
            while (nameSize > 20 && Gui.Measure(stageName, nameSize, true) > 270f) nameSize -= 2;   // long design-stage names shrink between the chevrons
            Gui.Display(new Rect(cx - 200f, ny, 400f, 34f), stageName, nameSize, Gui.Paper, TextAnchor.MiddleCenter);
            Gui.Text(new Rect(cx - 200f, ny + 32f, 400f, 16f), StageBuilder.Taglines[cur], 12, Gui.Muted, TextAnchor.MiddleCenter, false, false);
            if (!arcade)
            {
                if (Gui.Hit(new Rect(cx - 170f, ny + 2f, 30f, 30f), out bool hl)) StageStep(-1);
                Gui.Chevron(new Rect(cx - 162f, ny + 8f, 12f, 18f), hl ? Gui.Paper : Gui.Gold, false);
                if (Gui.Hit(new Rect(cx + 140f, ny + 2f, 30f, 30f), out bool hr)) StageStep(1);
                Gui.Chevron(new Rect(cx + 150f, ny + 8f, 12f, 18f), hr ? Gui.Paper : Gui.Gold);
            }
            for (int i = 0; i < n; i++)
                Gui.Skew(new Rect(cx - n * 11f + i * 22f + 3f, ny + 52f, 16f, 4f), i == curPos ? Gui.Gold : WithA(Gui.Paper, 0.25f), 0.6f);
            HazardsChip(new Rect(cx + 116f, py, 112f, 20f), focus);   // above the right-hand side preview
        }

        /// <summary>
        /// HAZARDS ON / OFF chip at the top right of the carousel (ST-5). Click, or R / DEL / gamepad Y once everyone is
        /// ready. Writes only the menu's pending match config.
        /// </summary>
        void HazardsChip(Rect r, bool focus)
        {
            bool on = pending.hazards;
            if (Gui.Hit(r, out bool hot)) ToggleHazards();
            var c = on ? Gui.P1 : Gui.Steel;
            Gui.Skew(new Rect(r.x + 3f, r.y + 3f, r.width, r.height), new Color(0f, 0f, 0f, 0.45f), 0.3f);
            Gui.Skew(r, hot ? Color.Lerp(c, Gui.Paper, 0.2f) : c, 0.3f);
            Gui.SkewFrame(r, hot ? Gui.Paper : WithA(Gui.Paper, on ? 0.6f : 0.3f), 1f, 0.3f);
            Gui.Text(r, on ? "HAZARDS  ON" : "HAZARDS  OFF", 11, on ? Gui.Paper : Gui.Muted, TextAnchor.MiddleCenter, true, false);
            if (focus)
            {
                Gui.KeyCap(r.xMax + 6f, r.y + 2f, Gui.Key("R", "Y"), 16f);
            }
        }

        /// <summary>Procedural thumbnail of a stage: sky, backdrop, main platform and floating platforms (matches Stage.cs).</summary>
        void StagePreview(Rect r, int idx, float alpha)
        {
            float a0 = Gui.Alpha;
            Gui.Alpha = a0 * alpha;
            idx = Mathf.Clamp(idx, 0, StageBuilder.Names.Length - 1);
            var thumb = StageBuilder.Thumbnail(idx);
            if (thumb != null)
            {
                // Design stages show their real illustration (cropped to the slot's aspect around the arena).
                float ta = (float)thumb.width / thumb.height, ra = r.width / r.height;
                var uv = ta > ra ? new Rect((1f - ra / ta) * 0.5f, 0f, ra / ta, 1f) : new Rect(0f, (1f - ta / ra) * 0.5f, 1f, ta / ra);
                var col = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, Gui.Alpha);
                GUI.DrawTextureWithTexCoords(r, thumb, uv);
                GUI.color = col;
                Gui.Alpha = a0;
                return;
            }
            var sky = StageBuilder.PreviewSky[idx];
            var ground = StageBuilder.PreviewGround[idx];
            var trim = StageBuilder.PreviewTrim[idx];
            GUI.BeginClip(r);
            float w = r.width, h = r.height, t = Time.unscaledTime;
            Gui.Rect(new Rect(0f, 0f, w, h), sky);
            Gui.FadeUp(new Rect(0f, h * 0.3f, w, h * 0.7f), new Color(sky.r * 0.5f, sky.g * 0.5f, sky.b * 0.6f, 0.8f));
            switch (idx)
            {
                case 1:   // neon skyline
                    for (int i = 0; i < 9; i++)
                    {
                        float bw = w * 0.1f, bh = h * (0.25f + 0.35f * Mathf.Abs(Mathf.Sin(i * 2.3f)));
                        var b = new Rect(i * w * 0.115f, h * 0.78f - bh, bw, bh);
                        Gui.Rect(b, new Color(0.12f, 0.08f, 0.24f));
                        Gui.Rect(new Rect(b.x, b.y, b.width, 1.5f), i % 2 == 0 ? new Color(1f, 0.3f, 0.8f) : new Color(0.2f, 0.95f, 1f));
                    }
                    break;
                case 2:   // lava glow
                    Gui.Glow(new Rect(-w * 0.2f, h * 0.65f, w * 1.4f, h * 0.7f), new Color(1f, 0.4f, 0.05f, 0.75f + 0.15f * Mathf.Sin(t * 3f)));
                    for (int i = 0; i < 6; i++)
                        Gui.Circle(new Rect(w * (0.1f + 0.15f * i), h * (0.2f + 0.5f * Mathf.Repeat(i * 0.37f - t * 0.1f, 1f)), 3f, 3f), new Color(1f, 0.7f, 0.2f, 0.8f));
                    break;
                default:  // peaks, clouds and sun
                    Gui.Glow(new Rect(w * 0.68f, h * 0.06f, h * 0.45f, h * 0.45f), new Color(1f, 0.95f, 0.7f, 0.9f));
                    for (int i = 0; i < 5; i++)
                        Gui.Rect(new Rect(w * (0.05f + 0.2f * i), h * (0.3f + 0.12f * (i % 2)), w * 0.08f, h * 0.6f), new Color(0.56f, 0.75f, 0.8f, 0.8f));
                    for (int i = 0; i < 4; i++)
                        Gui.Glow(new Rect(Mathf.Repeat(i * w * 0.31f + t * 6f, w * 1.3f) - w * 0.2f, h * (0.15f + 0.12f * i), w * 0.35f, h * 0.18f), new Color(1f, 1f, 1f, 0.7f));
                    break;
            }
            var main = new Rect(w * 0.14f, h * 0.76f, w * 0.72f, h * 0.24f);
            Gui.Rect(main, ground);
            Gui.Rect(new Rect(main.x, main.y, main.width, Mathf.Max(2f, h * 0.035f)), trim);
            float ph = Mathf.Max(2f, h * 0.03f);
            Gui.Rect(new Rect(w * 0.2f, h * 0.55f, w * 0.18f, ph), trim);
            Gui.Rect(new Rect(w * 0.62f, h * 0.55f, w * 0.18f, ph), trim);
            if (idx != 2) Gui.Rect(new Rect(w * 0.41f, h * 0.36f, w * 0.18f, ph), trim);   // Lava Forge has no top platform
            Gui.Halftone(new Rect(0f, 0f, w, h), new Color(1f, 1f, 1f, 0.04f), 6f);
            GUI.EndClip();
            Gui.Alpha = a0;
        }

        void FightButton(Rect r)
        {
            bool ready = AllReady;
            bool click = Gui.Hit(r, out bool hot);
            hot &= selectMouse || ready;
            bool press = Gui.Pressing(hot);
            var b = press ? new Rect(r.x + 3f, r.y + 3f, r.width - 6f, r.height - 6f) : r;
            float pulse = ready ? 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f) : 0f;
            Color c = ready ? Color.Lerp(Gui.P1, Color.Lerp(Gui.P1, Gui.Paper, 0.25f), pulse) : hot ? Color.Lerp(Gui.Steel, Gui.P1, 0.5f) : new Color(0.1f, 0.15f, 0.26f);
            if (ready) Gui.Glow(new Rect(b.x - 40f, b.y - 30f, b.width + 80f, b.height + 60f), WithA(Gui.P1, 0.35f + 0.2f * pulse));
            if (!press) Gui.Skew(new Rect(b.x + 6f, b.y + 6f, b.width, b.height), new Color(0f, 0f, 0f, 0.5f), 0.25f);
            Gui.Skew(b, c, 0.25f);
            Gui.Skew(new Rect(b.x - 20f, b.y, 10f, b.height), Gui.P1, 0.25f);
            Gui.Skew(new Rect(b.xMax + 10f, b.y, 10f, b.height), Gui.P2, 0.25f);
            Gui.SkewFrame(b, ready || hot ? Gui.Paper : Gui.Steel, 2f, 0.25f);
            Gui.Display(b, pending.training ? "START TRAINING" : "FIGHT!", ready ? 40 : 34, ready || hot ? Gui.Paper : WithA(Gui.Paper, 0.75f), TextAnchor.MiddleCenter, 0.2f);
            if (ready)
            {
                string key = MenuInput.UsingGamepad ? "A" : Solo ? "SPACE" : "SPACE / ENTER";
                float kw = Gui.KeyWidth(key, 20f);
                Gui.KeyCap(r.center.x - kw * 0.5f, r.yMax + 8f, key, 20f);
            }
            else Gui.Text(new Rect(r.x - 40f, r.yMax + 6f, r.width + 80f, 18f), "OR CLICK TO START WITH THE CURRENT PICKS", 10, Gui.Muted, TextAnchor.MiddleCenter, true, false);
            if (click && wipeStart < 0f) QuickFight();
        }
    }
}
