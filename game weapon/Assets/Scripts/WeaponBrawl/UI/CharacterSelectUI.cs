using System;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace WeaponBrawl
{
    /// <summary>
    /// Arcade-style character select: tabs on top, roster grid in the middle, big P1 / P2 portraits on the flanks
    /// (with playstyle tag and the 1-of-2 passive choice), stage carousel below and a button legend in the footer.
    /// Built for gamepads; keyboard and mouse/touch work too.
    /// </summary>
    public class CharacterSelectUI : MonoBehaviour
    {
        enum MenuTab { Local, Online, Training, Settings }
        enum Step { Fighter, Passive, Ready }

        class PassiveChip
        {
            public SkewPanel panel;
            public Text title, description;
        }

        class Flank
        {
            public RectTransform figureHolder;
            public ComicFigure figure;
            public Image art;
            public SkewPanel tagPlate, stylePlate, variantPlate;
            public Text tag, nameText, style, stats, stamp, hint, variantText;
            public readonly PassiveChip[] chips = new PassiveChip[2];
        }

        class Slot
        {
            public int index, cursor, passive, variant, shownVariant;
            public Step step;
            public bool joined;
            public WeaponDefinition locked;   // set once the fighter is confirmed
            public WeaponDefinition shown;
            public bool shownValid;
            public float slam;
            public Flank flank;
            public SkewPanel frame;
            public WeaponDefinition Hovered => locked ?? (cursor < WeaponDatabase.All.Count ? WeaponDatabase.All[cursor] : null);
        }

        class StageCard
        {
            public RectTransform root;
            public SkewPanel panel;
        }

        const int Columns = 3, Rows = 2, RandomCell = 5;
        const float CellWidth = 220f, CellHeight = 190f, CellGap = 12f;
        const float TabWidth = 190f;

        static readonly string[] TabNames = { "LOCAL", "ONLINE", "TRAINING", "SETTINGS" };
        static readonly float[] MatchTimes = { 60f, 99f, 180f };
        static readonly Vector2 TopCenter = new Vector2(0.5f, 1f);
        static readonly Vector2 TopLeft = new Vector2(0f, 1f);
        static readonly Vector2 TopRight = new Vector2(1f, 1f);

        public event Action<MatchConfig> Confirmed;
        public event Action Cancelled;

        /// <summary>When true (a battle is running), backing out of the first step returns to the battle.</summary>
        public bool CanCancel { get; set; }

        readonly Slot[] slots = { new Slot { index = 0, cursor = 0 }, new Slot { index = 1, cursor = RandomCell } };
        readonly SkewPanel[] cells = new SkewPanel[Columns * Rows];
        readonly SkewPanel[] tabPanels = new SkewPanel[TabNames.Length];
        readonly Text[] tabLabels = new Text[TabNames.Length];
        readonly StageCard[] stageCards = new StageCard[StageDatabase.All.Count];
        readonly SkewPanel[] settingRows = new SkewPanel[3];
        readonly Text[] settingValues = new Text[3];

        RectTransform rosterRoot, settingsRoot, onlineRoot, fightBanner, footerRow, stageRoot;
        RectTransform gridRoot;
        Text details, stageHeading, tabPrevPrompt, tabNextPrompt;
        MenuTab tab = MenuTab.Local;
        int stageIndex, settingCursor;
        int matchTimeIndex = MatchTimes.Length - 1;
        bool cpuAI = true, cameraShake = true;
        string footerSignature;

        bool AllReady => slots[0].step == Step.Ready && slots[1].step == Step.Ready;
        Slot P2Slot => slots[1];

        public static CharacterSelectUI Create(Canvas canvas)
        {
            var rt = UIFactory.Node("CharacterSelect", canvas.transform).Stretch();
            var ui = rt.gameObject.AddComponent<CharacterSelectUI>();
            ui.BuildBackground();
            ui.BuildFlank(ui.slots[0]);
            ui.BuildFlank(ui.slots[1]);
            ui.BuildRoster();
            ui.BuildSettings();
            ui.BuildOnline();
            ui.BuildTabs();
            ui.BuildFooter();
            ui.SetTab(MenuTab.Local);
            ui.Close();
            return ui;
        }

        public void Open()
        {
            gameObject.SetActive(true);
            foreach (var s in slots)
            {
                s.step = Step.Fighter;
                s.locked = null;
                s.variant = 0;
                s.joined = false;
                s.shownValid = false;
            }
            if (tab == MenuTab.Online) SetTab(MenuTab.Local);
            footerSignature = null;
        }

        public void Close() => gameObject.SetActive(false);

        // ---------------------------------------------------------------- building

        void BuildBackground()
        {
            var paper = gameObject.AddComponent<Image>();
            paper.color = UITheme.Paper;

            Slash(UITheme.Navy, -96f, 760f, 200f, TopLeft);
            Slash(UITheme.P1, -120f, 760f, 200f, TopLeft);
            Slash(UITheme.Navy, 96f, 760f, -200f, TopRight);
            Slash(UITheme.P2, 120f, 760f, -200f, TopRight);

            var dots = UIFactory.Art(transform, "Halftone", ComicArt.Halftone, new Color(UITheme.Navy.r, UITheme.Navy.g, UITheme.Navy.b, 0.07f));
            dots.type = Image.Type.Tiled;
            dots.rectTransform.Stretch();

            Splat(UITheme.P1, 0, new Vector2(625f, -200f), 330f, 20f);
            Splat(UITheme.P1, 1, new Vector2(470f, -860f), 260f, 130f);
            Splat(UITheme.P2, 2, new Vector2(1295f, -860f), 330f, -40f);
            Splat(UITheme.P2, 1, new Vector2(1450f, -210f), 250f, 210f);
        }

        void Slash(Color color, float x, float width, float skew, Vector2 anchor)
        {
            UIFactory.Skew(transform, "Slash", color, skew).rectTransform
                .Place(anchor, anchor, new Vector2(x, 0f), new Vector2(width, 1080f));
        }

        void Splat(Color color, int variant, Vector2 topLeftPosition, float size, float rotation)
        {
            var img = UIFactory.Art(transform, "Splatter", ComicArt.Splatter(variant), color);
            img.rectTransform.Place(TopLeft, new Vector2(0.5f, 0.5f), topLeftPosition, Vector2.one * size);
            img.rectTransform.localRotation = Quaternion.Euler(0f, 0f, rotation);
        }

        void BuildFlank(Slot slot)
        {
            bool left = slot.index == 0;
            float sx = left ? 1f : -1f;
            var anchor = left ? TopLeft : TopRight;
            var align = left ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
            Color color = UITheme.Player(slot.index);
            var flank = new Flank();
            slot.flank = flank;

            var root = UIFactory.Node(left ? "P1Flank" : "P2Flank", transform)
                .Place(anchor, anchor, Vector2.zero, new Vector2(590f, 1080f));

            RectTransform Side(RectTransform rt, float x, float y, float w, float h) =>
                rt.Place(anchor, anchor, new Vector2(x * sx, y), new Vector2(w, h));

            // Dry-brush stroke behind the fighter
            var stroke = UIFactory.Art(root, "Brush", ComicArt.Brush, new Color(1f, 1f, 1f, 0.9f));
            Side(stroke.rectTransform, 10f, -250f, 570f, 230f);
            stroke.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 10f * sx);
            if (!left) stroke.rectTransform.localScale = new Vector3(-1f, 1f, 1f);

            flank.figureHolder = Side(UIFactory.Node("Portrait", root), 20f, -90f, 550f, 500f);
            flank.figure = UIFactory.Node("Figure", flank.figureHolder).gameObject.AddComponent<ComicFigure>();
            flank.figure.rectTransform.Stretch();
            flank.figure.raycastTarget = false;
            if (!left) flank.figure.rectTransform.localScale = new Vector3(-1f, 1f, 1f); // both fighters face the middle
            flank.art = UIFactory.Art(flank.figureHolder, "Art", null, Color.white);
            flank.art.rectTransform.Stretch();
            flank.art.preserveAspect = true;
            flank.art.enabled = false;

            flank.tagPlate = UIFactory.Skew(root, "Tag", UITheme.Navy, 14f * sx);
            Side(flank.tagPlate.rectTransform, 24f, -104f, 120f, 56f);
            flank.tag = UIFactory.Label(flank.tagPlate.transform, "Text", "", 32, Color.white, TextAnchor.MiddleCenter, UITheme.Heading);
            flank.tag.rectTransform.Stretch();

            flank.hint = UIFactory.Label(root, "JoinHint", "", 22, UITheme.Navy, align, FontStyle.Bold);
            Side(flank.hint.rectTransform, 156f, -104f, 400f, 56f);

            flank.stamp = UIFactory.Label(root, "Ready", "READY!", 112, color, TextAnchor.MiddleCenter, UITheme.Heading);
            Side(flank.stamp.rectTransform, 20f, -280f, 550f, 150f);
            flank.stamp.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 8f * sx);
            flank.stamp.gameObject.AddComponent<Outline>().effectDistance = new Vector2(4f, -4f);
            flank.stamp.GetComponent<Outline>().effectColor = UITheme.Navy;

            var variantButton = UIFactory.SkewButton(root, "Variant", UITheme.Paper, 14f * sx, () => CycleVariant(slot, 1));
            flank.variantPlate = (SkewPanel)variantButton.targetGraphic;
            Side(flank.variantPlate.rectTransform, 16f, -546f, 300f, 46f);
            flank.variantPlate.BorderWidth = 3f;
            flank.variantPlate.BorderColor = UITheme.Navy;
            flank.variantText = UIFactory.Label(flank.variantPlate.transform, "Text", "", 25, UITheme.Navy, TextAnchor.MiddleCenter, UITheme.Heading);
            flank.variantText.rectTransform.Stretch();

            var namePlate = UIFactory.Skew(root, "NamePlate", UITheme.Navy, 34f * sx);
            Side(namePlate.rectTransform, 16f, -596f, 560f, 88f);
            flank.nameText = UIFactory.Label(namePlate.transform, "Name", "", 60, Color.white, TextAnchor.MiddleCenter, UITheme.Heading);
            flank.nameText.rectTransform.Stretch();

            flank.stylePlate = UIFactory.Skew(root, "Playstyle", color, 18f * sx);
            Side(flank.stylePlate.rectTransform, 16f, -688f, 250f, 44f);
            flank.style = UIFactory.Label(flank.stylePlate.transform, "Text", "", 25, UITheme.Navy, TextAnchor.MiddleCenter, UITheme.Heading);
            flank.style.rectTransform.Stretch();

            flank.stats = UIFactory.Label(root, "Stats", "", 22, UITheme.Navy, align, FontStyle.Bold);
            Side(flank.stats.rectTransform, 272f, -688f, 310f, 44f);

            for (int i = 0; i < 2; i++)
            {
                int chipIndex = i;
                var button = UIFactory.SkewButton(root, "Passive" + i, UITheme.Paper, 16f * sx, () => ClickPassive(slot, chipIndex));
                var panel = (SkewPanel)button.targetGraphic;
                Side(panel.rectTransform, 16f, -740f - i * 134f, 560f, 126f);
                panel.BorderWidth = 3f;
                panel.BorderColor = UITheme.Navy;

                var chip = new PassiveChip { panel = panel };
                chip.title = UIFactory.Label(panel.transform, "Title", "", 25, UITheme.Navy, TextAnchor.UpperLeft, UITheme.Heading);
                chip.title.rectTransform.Stretch(0f);
                chip.title.rectTransform.offsetMin = new Vector2(34f, 0f);
                chip.title.rectTransform.offsetMax = new Vector2(-34f, -9f);
                chip.description = UIFactory.Label(panel.transform, "Description", "", 20, UITheme.Navy, TextAnchor.UpperLeft);
                chip.description.rectTransform.Stretch(0f);
                chip.description.rectTransform.offsetMin = new Vector2(34f, 4f);
                chip.description.rectTransform.offsetMax = new Vector2(-34f, -40f);
                flank.chips[i] = chip;
            }
        }

        void BuildRoster()
        {
            rosterRoot = UIFactory.Node("Roster", transform).Stretch();

            var heading = UIFactory.Label(rosterRoot, "Heading", "CHOOSE YOUR WEAPON", 36, UITheme.Navy, TextAnchor.MiddleCenter, UITheme.Heading);
            heading.rectTransform.Place(TopCenter, TopCenter, new Vector2(0f, -96f), new Vector2(720f, 50f));

            float gridWidth = Columns * CellWidth + (Columns - 1) * CellGap;
            float gridHeight = Rows * CellHeight + (Rows - 1) * CellGap;
            gridRoot = UIFactory.Node("Grid", rosterRoot).Place(TopCenter, TopCenter, new Vector2(0f, -150f), new Vector2(gridWidth, gridHeight));

            var defs = WeaponDatabase.All;
            for (int i = 0; i < cells.Length; i++)
            {
                int cell = i;
                var button = UIFactory.SkewButton(gridRoot, "Cell" + i, UITheme.Paper, 24f, () => ClickCell(cell));
                var panel = (SkewPanel)button.targetGraphic;
                panel.rectTransform.Place(TopLeft, TopLeft, CellPosition(i), new Vector2(CellWidth, CellHeight));
                panel.BorderWidth = 4f;
                panel.BorderColor = UITheme.Navy;
                cells[i] = panel;

                bool isRandom = i == RandomCell;
                if (!isRandom)
                {
                    var icon = UIFactory.Node("Icon", panel.transform).gameObject.AddComponent<ComicFigure>();
                    icon.rectTransform.Place(TopCenter, TopCenter, new Vector2(0f, -10f), new Vector2(CellWidth - 60f, 116f));
                    icon.raycastTarget = false;
                    icon.ShowWeapon(defs[i].type, 0, defs[i].color);
                }
                else
                {
                    UIFactory.Label(panel.transform, "Question", "?", 100, UITheme.Navy, TextAnchor.MiddleCenter, UITheme.Heading)
                        .rectTransform.Place(TopCenter, TopCenter, new Vector2(0f, -8f), new Vector2(CellWidth - 60f, 120f));
                }

                UIFactory.Label(panel.transform, "Name", isRandom ? "RANDOM" : defs[i].displayName.ToUpperInvariant(), 28, UITheme.Navy, TextAnchor.MiddleCenter, UITheme.Heading)
                    .rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 28f), new Vector2(CellWidth - 40f, 34f));
                UIFactory.Label(panel.transform, "Style", isRandom ? "ANY STYLE" : defs[i].playstyle, 17, UITheme.NavySoft, TextAnchor.MiddleCenter, FontStyle.Bold)
                    .rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(CellWidth - 40f, 22f));
            }

            foreach (var s in slots) BuildCursor(s);

            details = UIFactory.Label(rosterRoot, "Details", "", 22, UITheme.Navy, TextAnchor.UpperLeft);
            details.rectTransform.Place(TopCenter, TopCenter, new Vector2(0f, -556f), new Vector2(700f, 180f));

            var banner = UIFactory.SkewButton(rosterRoot, "Fight", UITheme.Navy, 40f, Launch);
            fightBanner = ((SkewPanel)banner.targetGraphic).rectTransform;
            fightBanner.Place(TopCenter, new Vector2(0.5f, 0.5f), new Vector2(0f, -640f), new Vector2(640f, 150f));
            var bannerText = UIFactory.Label(fightBanner, "Text", "FIGHT!", 96, UITheme.P1, TextAnchor.MiddleCenter, UITheme.Heading);
            bannerText.rectTransform.Stretch();

            stageRoot = UIFactory.Node("Stages", rosterRoot).Place(TopCenter, TopCenter, new Vector2(0f, -738f), new Vector2(720f, 250f));
            stageHeading = UIFactory.Label(stageRoot, "Heading", "STAGE", 26, UITheme.Navy, TextAnchor.MiddleCenter, UITheme.Heading);
            stageHeading.rectTransform.Place(TopCenter, TopCenter, Vector2.zero, new Vector2(720f, 34f));

            var stages = StageDatabase.All;
            for (int i = 0; i < stages.Count; i++)
                stageCards[i] = BuildStageCard(stages[i], i);
        }

        void BuildCursor(Slot slot)
        {
            bool first = slot.index == 0;
            Color color = UITheme.Player(slot.index);
            float grow = first ? 16f : 6f; // P1's frame sits outside P2's, so both stay visible on the same cell

            slot.frame = UIFactory.Skew(gridRoot, "Cursor", color, 24f);
            slot.frame.Hollow = true;
            slot.frame.BorderWidth = first ? 8f : 7f;
            slot.frame.BorderColor = color;
            slot.frame.rectTransform.Place(TopLeft, new Vector2(0.5f, 0.5f), CellCenter(slot.cursor), new Vector2(CellWidth + grow, CellHeight + grow));

            var chip = UIFactory.Skew(slot.frame.transform, "Chip", color, 10f);
            var chipAnchor = first ? TopLeft : TopRight;
            chip.rectTransform.Place(chipAnchor, chipAnchor, new Vector2(first ? 20f : -8f, 12f), new Vector2(56f, 30f));
            UIFactory.Label(chip.transform, "Text", first ? "P1" : "P2", 22, UITheme.Navy, TextAnchor.MiddleCenter, UITheme.Heading).rectTransform.Stretch();
        }

        StageCard BuildStageCard(StageDefinition stage, int index)
        {
            const float width = 226f, height = 168f;
            int card = index;
            var button = UIFactory.SkewButton(stageRoot, stage.name, stage.sky, 26f, () => ClickStage(card));
            var panel = (SkewPanel)button.targetGraphic;
            var rt = panel.rectTransform;
            rt.Place(TopLeft, new Vector2(0.5f, 0.5f), new Vector2(width * 0.5f + 8f + index * (width + 13f), -40f - height * 0.5f), new Vector2(width, height));
            panel.BorderWidth = 3f;
            panel.BorderColor = UITheme.Navy;

            float[] xs = { 44f, 96f, 150f };
            float[] hs = { 70f, 96f, 58f };
            for (int i = 0; i < xs.Length; i++)
            {
                var pillar = UIFactory.Skew(rt, "Pillar", stage.pillar, 6f);
                pillar.rectTransform.Place(Vector2.zero, Vector2.zero, new Vector2(xs[i], 34f), new Vector2(26f, hs[i]));
                var cap = UIFactory.Skew(rt, "Cap", stage.cap, 6f);
                cap.rectTransform.Place(Vector2.zero, Vector2.zero, new Vector2(xs[i], 34f + hs[i]), new Vector2(30f, 6f));
            }

            var plate = UIFactory.Skew(rt, "NamePlate", UITheme.Navy, 18f);
            plate.rectTransform.Place(Vector2.zero, Vector2.zero, new Vector2(3f, 3f), new Vector2(width - 6f, 36f));
            UIFactory.Label(plate.transform, "Name", stage.name, 22, Color.white, TextAnchor.MiddleCenter, UITheme.Heading).rectTransform.Stretch();
            return new StageCard { root = rt, panel = panel };
        }

        void BuildSettings()
        {
            settingsRoot = UIFactory.Node("Settings", transform).Stretch();
            UIFactory.Label(settingsRoot, "Heading", "SETTINGS", 44, UITheme.Navy, TextAnchor.MiddleCenter, UITheme.Heading)
                .rectTransform.Place(TopCenter, TopCenter, new Vector2(0f, -150f), new Vector2(720f, 60f));

            string[] names = { "MATCH TIME", "CPU AI", "CAMERA SHAKE" };
            for (int i = 0; i < names.Length; i++)
            {
                int row = i;
                var button = UIFactory.SkewButton(settingsRoot, names[i], UITheme.Paper, 28f, () =>
                {
                    settingCursor = row;
                    ChangeSetting(row, 1);
                });
                var panel = (SkewPanel)button.targetGraphic;
                panel.rectTransform.Place(TopCenter, TopCenter, new Vector2(0f, -240f - i * 110f), new Vector2(680f, 96f));
                panel.BorderWidth = 4f;
                panel.BorderColor = UITheme.Navy;
                settingRows[i] = panel;

                var nameLabel = UIFactory.Label(panel.transform, "Name", names[i], 30, UITheme.Navy, TextAnchor.MiddleLeft, UITheme.Heading);
                nameLabel.rectTransform.Stretch();
                nameLabel.rectTransform.offsetMin = new Vector2(50f, 0f);
                settingValues[i] = UIFactory.Label(panel.transform, "Value", "", 32, UITheme.Navy, TextAnchor.MiddleRight, UITheme.Heading);
                settingValues[i].rectTransform.Stretch();
                settingValues[i].rectTransform.offsetMax = new Vector2(-50f, 0f);
            }
        }

        void BuildOnline()
        {
            onlineRoot = UIFactory.Node("Online", transform).Stretch();
            var plate = UIFactory.Skew(onlineRoot, "Plate", UITheme.Navy, 40f);
            plate.rectTransform.Place(TopCenter, TopCenter, new Vector2(0f, -350f), new Vector2(680f, 220f));
            UIFactory.Label(plate.transform, "Text", "ONLINE\n<size=26>Not available yet. This build has no netcode.</size>", 72, Color.white, TextAnchor.MiddleCenter, UITheme.Heading)
                .rectTransform.Stretch();
        }

        void BuildTabs()
        {
            float width = TabNames.Length * TabWidth;
            var bar = UIFactory.Skew(transform, "TabBar", UITheme.Navy);
            bar.BottomInset = 30f;
            bar.rectTransform.Place(TopCenter, TopCenter, Vector2.zero, new Vector2(width + 240f, 74f));

            for (int i = 0; i < TabNames.Length; i++)
            {
                var value = (MenuTab)i;
                var button = UIFactory.SkewButton(bar.transform, TabNames[i], Color.clear, 16f, () => SetTab(value));
                var panel = (SkewPanel)button.targetGraphic;
                panel.rectTransform.Place(TopCenter, TopCenter, new Vector2((i - (TabNames.Length - 1) * 0.5f) * TabWidth, -10f), new Vector2(TabWidth - 8f, 52f));
                tabPanels[i] = panel;
                tabLabels[i] = UIFactory.Label(panel.transform, "Text", TabNames[i], 26, Color.white, TextAnchor.MiddleCenter, UITheme.Heading);
                tabLabels[i].rectTransform.Stretch();
            }

            tabPrevPrompt = TabPrompt(bar.transform, TopLeft, new Vector2(26f, -18f));
            tabNextPrompt = TabPrompt(bar.transform, TopRight, new Vector2(-26f, -18f));
        }

        static Text TabPrompt(Transform parent, Vector2 anchor, Vector2 position)
        {
            var chip = UIFactory.Skew(parent, "Bumper", UITheme.Paper, 10f);
            chip.rectTransform.Place(anchor, anchor, position, new Vector2(62f, 36f));
            var label = UIFactory.Label(chip.transform, "Text", "", 22, UITheme.Navy, TextAnchor.MiddleCenter, FontStyle.Bold);
            label.rectTransform.Stretch();
            return label;
        }

        void BuildFooter()
        {
            var bar = UIFactory.Skew(transform, "Footer", UITheme.Navy);
            bar.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(1920f, 60f));
            bar.rectTransform.anchorMin = Vector2.zero;
            bar.rectTransform.anchorMax = new Vector2(1f, 0f);
            bar.rectTransform.sizeDelta = new Vector2(0f, 60f);

            footerRow = UIFactory.Node("Prompts", bar.transform);
            footerRow.Stretch();
            var h = footerRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleCenter;
            h.spacing = 12f;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
        }

        static Vector2 CellPosition(int i) =>
            new Vector2(i % Columns * (CellWidth + CellGap), -(i / Columns) * (CellHeight + CellGap));

        static Vector2 CellCenter(int i) => CellPosition(i) + new Vector2(CellWidth * 0.5f, -CellHeight * 0.5f);

        // ---------------------------------------------------------------- input

        void Update()
        {
            var p1 = GameInput.P1;
            if (p1.Down(GameAction.TabPrev)) SetTab((MenuTab)(((int)tab + TabNames.Length - 1) % TabNames.Length));
            else if (p1.Down(GameAction.TabNext)) SetTab((MenuTab)(((int)tab + 1) % TabNames.Length));

            switch (tab)
            {
                case MenuTab.Settings: UpdateSettings(p1); break;
                case MenuTab.Online:
                    if (p1.Down(GameAction.Cancel)) SetTab(MenuTab.Local);
                    break;
                default: UpdateRoster(p1, GameInput.P2); break;
            }

            RefreshVisuals();
        }

        void UpdateRoster(PlayerInputMap p1, PlayerInputMap p2)
        {
            var second = P2Slot;
            if (tab == MenuTab.Local && !second.joined && (p2.Down(GameAction.Confirm) || p2.Down(GameAction.Start)))
            {
                second.joined = true;
                second.slam = 1f;
                footerSignature = null;
                return;
            }

            bool wasAllReady = AllReady; // the press that locks the last fighter must not also start the match
            if (second.joined)
            {
                Drive(slots[0], p1);
                Drive(second, p2);
            }
            else if (slots[0].step != Step.Ready)
            {
                Drive(slots[0], p1);
            }
            else if (second.step != Step.Ready)
            {
                Drive(second, p1); // no second player: P1 picks the CPU's fighter
            }
            else if (p1.Down(GameAction.Cancel))
            {
                second.step = Step.Passive;
                return;
            }

            if (wasAllReady && AllReady) DriveStage(p1);
        }

        void Drive(Slot slot, PlayerInputMap input)
        {
            switch (slot.step)
            {
                case Step.Fighter:
                    MoveCursor(slot, input.NavX, input.NavY);
                    if (input.Down(GameAction.Confirm)) LockFighter(slot);
                    else if (input.Down(GameAction.Cancel)) BackFromFighter(slot);
                    break;
                case Step.Passive:
                    if (input.NavY != 0) slot.passive = 1 - slot.passive;
                    if (input.NavX != 0) CycleVariant(slot, input.NavX);
                    if (input.Down(GameAction.Confirm)) slot.step = Step.Ready;
                    else if (input.Down(GameAction.Cancel)) Unlock(slot);
                    break;
                default:
                    if (input.Down(GameAction.Cancel)) slot.step = Step.Passive;
                    break;
            }
        }

        void DriveStage(PlayerInputMap input)
        {
            if (input.NavX != 0) stageIndex = (stageIndex + input.NavX + stageCards.Length) % stageCards.Length;
            if (input.Down(GameAction.Confirm) || input.Down(GameAction.Start)) Launch();
        }

        static void MoveCursor(Slot slot, int dx, int dy)
        {
            if (dx == 0 && dy == 0) return;
            int column = slot.cursor % Columns, row = slot.cursor / Columns;
            column = (column + dx + Columns) % Columns;
            row = (row - dy + Rows) % Rows; // stick up = previous row
            slot.cursor = row * Columns + column;
            slot.slam = 1f;
        }

        void LockFighter(Slot slot)
        {
            var defs = WeaponDatabase.All;
            bool random = slot.cursor == RandomCell;
            slot.locked = random ? defs[Random.Range(0, defs.Count)] : defs[slot.cursor];
            slot.variant = random ? Random.Range(0, slot.locked.variants.Length) : 0;
            slot.passive = 0;
            slot.step = Step.Passive;
            slot.slam = 1f;
        }

        /// <summary>Changes the weapon's look only. Stats, skills and passives are shared by the whole type.</summary>
        static void CycleVariant(Slot slot, int delta)
        {
            if (slot.step != Step.Passive) return;
            int count = slot.locked.variants.Length;
            slot.variant = (slot.variant + delta + count) % count;
            slot.slam = 0.5f;
        }

        static void Unlock(Slot slot)
        {
            slot.locked = null;
            slot.variant = 0;
            slot.step = Step.Fighter;
            slot.slam = 1f;
        }

        void BackFromFighter(Slot slot)
        {
            if (slot.index == 1)
            {
                if (slot.joined) slot.joined = false; // P2 leaves; the CPU takes the slot
                else slots[0].step = Step.Passive;
                footerSignature = null;
                return;
            }
            if (!CanCancel) return;
            Close();
            Cancelled?.Invoke();
        }

        // ---------------------------------------------------------------- mouse / touch

        /// <summary>Mouse clicks act for whichever player still has to choose.</summary>
        Slot ChoosingSlot()
        {
            if (slots[0].step != Step.Ready) return slots[0];
            return slots[1].step != Step.Ready ? slots[1] : null;
        }

        void ClickCell(int cell)
        {
            var slot = ChoosingSlot();
            if (slot == null) return;
            if (slot.step == Step.Passive) Unlock(slot);
            slot.cursor = cell;
            LockFighter(slot);
        }

        void ClickPassive(Slot slot, int chip)
        {
            if (slot.step == Step.Fighter || (slot.index == 1 && !slot.joined && slots[0].step != Step.Ready)) return;
            slot.passive = chip;
            slot.step = Step.Ready;
        }

        void ClickStage(int index)
        {
            if (index == stageIndex && AllReady) Launch();
            stageIndex = index;
        }

        void Launch()
        {
            if (!AllReady) return;
            Close();
            Fx.ShakeEnabled = cameraShake;
            Confirmed?.Invoke(new MatchConfig
            {
                p1Type = slots[0].locked.type,
                p1Variant = slots[0].variant,
                p2Variant = slots[1].variant,
                p1Passive = slots[0].passive,
                p2Type = slots[1].locked.type,
                p2Passive = slots[1].passive,
                stage = stageIndex,
                p2Human = slots[1].joined,
                opponentAI = tab != MenuTab.Training && cpuAI,
                matchSeconds = MatchTimes[matchTimeIndex],
            });
        }

        // ---------------------------------------------------------------- tabs and settings

        void SetTab(MenuTab value)
        {
            tab = value;
            bool roster = value == MenuTab.Local || value == MenuTab.Training;
            rosterRoot.gameObject.SetActive(roster);
            settingsRoot.gameObject.SetActive(value == MenuTab.Settings);
            onlineRoot.gameObject.SetActive(value == MenuTab.Online);
            if (value == MenuTab.Training) P2Slot.joined = false; // the training partner is always a dummy
            footerSignature = null;

            for (int i = 0; i < tabPanels.Length; i++)
            {
                bool on = i == (int)value;
                tabPanels[i].color = on ? UITheme.P1 : Color.clear;
                tabLabels[i].color = on ? UITheme.Navy : (i == (int)MenuTab.Online ? UITheme.NavySoft : Color.white);
            }
        }

        void UpdateSettings(PlayerInputMap input)
        {
            if (input.NavY != 0) settingCursor = (settingCursor - input.NavY + settingRows.Length) % settingRows.Length;
            if (input.NavX != 0) ChangeSetting(settingCursor, input.NavX);
            if (input.Down(GameAction.Confirm)) ChangeSetting(settingCursor, 1);
            if (input.Down(GameAction.Cancel)) SetTab(MenuTab.Local);
        }

        void ChangeSetting(int row, int delta)
        {
            switch (row)
            {
                case 0: matchTimeIndex = (matchTimeIndex + delta + MatchTimes.Length) % MatchTimes.Length; break;
                case 1: cpuAI = !cpuAI; break;
                default: cameraShake = !cameraShake; break;
            }
        }

        // ---------------------------------------------------------------- visuals

        void RefreshVisuals()
        {
            float dt = Time.unscaledDeltaTime;
            foreach (var s in slots) RefreshFlank(s, dt);
            RefreshTabs();
            RefreshFooter();

            if (tab == MenuTab.Settings) RefreshSettings();
            else if (rosterRoot.gameObject.activeSelf) RefreshRoster(dt);
        }

        void RefreshFlank(Slot slot, float dt)
        {
            var flank = slot.flank;
            var def = slot.Hovered;
            Color color = UITheme.Player(slot.index);

            if (!slot.shownValid || slot.shown != def || slot.shownVariant != slot.variant)
            {
                slot.shown = def;
                slot.shownVariant = slot.variant;
                slot.shownValid = true;
                ShowPortrait(slot, def);
            }

            slot.slam = Mathf.MoveTowards(slot.slam, 0f, dt * 5f);
            float sx = slot.index == 0 ? 1f : -1f;
            float slam = slot.slam * slot.slam; // portrait slides in from the screen edge
            flank.figureHolder.anchoredPosition = new Vector2((20f - slam * 70f) * sx, -90f);
            flank.figureHolder.localScale = Vector3.one * (1f + slam * 0.08f);

            bool human = slot.index == 0 || slot.joined;
            flank.tag.text = human ? (slot.index == 0 ? "P1" : "P2") : (tab == MenuTab.Training ? "DUMMY" : "CPU");
            flank.tag.fontSize = human ? 32 : 24;
            flank.tagPlate.color = human ? color : UITheme.Navy;
            flank.tag.color = human ? UITheme.Navy : Color.white;

            flank.hint.text = slot.index == 1 && !slot.joined && tab == MenuTab.Local
                ? $"P2: PRESS {GameInput.P2.Label(GameAction.Confirm)} TO JOIN"
                : "";
            flank.stamp.enabled = slot.step == Step.Ready;

            flank.variantPlate.gameObject.SetActive(def != null);
            if (def != null)
            {
                string look = def.variants[slot.variant].ToUpperInvariant();
                bool picking = slot.step == Step.Passive;
                flank.variantText.text = picking ? $"<  {look}  >" : look;
                flank.variantPlate.BorderColor = picking ? color : UITheme.Navy;
                flank.variantPlate.BorderWidth = picking ? 5f : 3f;
            }

            flank.nameText.text = def != null ? def.displayName.ToUpperInvariant() : "RANDOM";
            flank.style.text = def != null ? def.playstyle : "ANY STYLE";
            flank.stats.text = def != null ? $"ATK {def.attack}   HP {def.health}   DEF {def.defense}" : "";

            for (int i = 0; i < 2; i++) RefreshChip(slot, def, i, color);
        }

        static void RefreshChip(Slot slot, WeaponDefinition def, int i, Color color)
        {
            var chip = slot.flank.chips[i];
            var info = def?.passives[i];
            chip.title.text = info != null ? $"{i + 1}. {info.name.ToUpperInvariant()}" : "PASSIVE " + (i + 1);
            chip.description.text = info != null ? info.description : "Revealed when the fighter is rolled.";

            bool choosing = slot.step == Step.Passive;
            bool picked = slot.passive == i && slot.step != Step.Fighter;
            bool dimmed = slot.step == Step.Ready && !picked;
            float pulse = choosing && picked ? 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f) : 1f;

            chip.panel.color = picked ? color : dimmed ? UITheme.PaperDim : UITheme.Paper;
            chip.panel.BorderWidth = picked ? 3f + pulse * 3f : 3f;
            var textColor = dimmed ? UITheme.NavySoft : UITheme.Navy;
            chip.title.color = textColor;
            chip.description.color = textColor;
        }

        void ShowPortrait(Slot slot, WeaponDefinition def)
        {
            var flank = slot.flank;
            var art = def != null ? Resources.Load<Sprite>("Portraits/" + def.type) : null;
            flank.art.sprite = art;
            flank.art.enabled = art != null;
            if (art != null && slot.index == 1) flank.art.rectTransform.localScale = new Vector3(-1f, 1f, 1f);

            if (art != null || def == null) flank.figure.Clear();
            else flank.figure.ShowFighter(def.type, slot.variant, UITheme.Player(slot.index), def.color);
        }

        void RefreshRoster(float dt)
        {
            bool ready = AllReady;
            float pulse = 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 7f);

            foreach (var s in slots)
            {
                var rt = s.frame.rectTransform;
                rt.anchoredPosition = Vector2.Lerp(rt.anchoredPosition, CellCenter(s.cursor), 1f - Mathf.Exp(-dt * 22f));
                var c = UITheme.Player(s.index);
                bool active = IsSlotActive(s);
                s.frame.color = new Color(1f, 1f, 1f, active ? pulse : 1f);
                s.frame.BorderColor = c;
                s.frame.gameObject.SetActive(active || s.locked != null);
            }

            for (int i = 0; i < cells.Length; i++)
            {
                Color fill = UITheme.Paper;
                foreach (var s in slots)
                    if (s.locked != null && s.cursor == i) fill = Color.Lerp(UITheme.Paper, UITheme.Player(s.index), 0.45f);
                cells[i].color = fill;
            }

            var focus = ChoosingSlot();
            details.gameObject.SetActive(!ready);
            fightBanner.gameObject.SetActive(ready);
            if (ready) fightBanner.localScale = Vector3.one * (1f + 0.04f * Mathf.Sin(Time.unscaledTime * 7f));
            if (focus != null) details.text = DetailsText(focus);

            stageHeading.text = ready ? $"STAGE   <color=#4C576F>{GameInput.P1.Label(GameAction.Confirm)} TO FIGHT</color>" : "STAGE";
            for (int i = 0; i < stageCards.Length; i++)
            {
                bool on = i == stageIndex;
                var card = stageCards[i];
                card.root.localScale = Vector3.one * (on ? 1.05f : 0.95f);
                card.panel.BorderWidth = on ? 6f : 3f;
                card.panel.BorderColor = on ? UITheme.P1 : UITheme.Navy;
                card.panel.color = Color.Lerp(StageDatabase.All[i].sky, UITheme.Paper, on ? 0f : 0.45f);
            }
        }

        bool IsSlotActive(Slot s)
        {
            if (s.step == Step.Ready) return false;
            return s.index == 0 || s.joined || slots[0].step == Step.Ready;
        }

        static string DetailsText(Slot slot)
        {
            var def = slot.Hovered;
            string who = $"<color={UIFactory.Hex(UITheme.Player(slot.index))}><b>P{slot.index + 1}</b></color>";
            if (def == null) return $"{who}  <b>RANDOM</b>  Rolls one of the five weapon classes.";

            var sb = new System.Text.StringBuilder();
            sb.Append($"{who}  <b>{def.displayName.ToUpperInvariant()}</b>  {def.examples}  |  {def.comboLength}-hit combo\n");
            foreach (var s in def.skills) sb.Append($"<b>{s.name}</b>  {s.cooldown:0}s\n");
            sb.Append($"<b>Ultimate: {def.ultimate.name}</b>");
            return sb.ToString();
        }

        void RefreshSettings()
        {
            settingValues[0].text = $"< {MatchTimes[matchTimeIndex]:0}s >";
            settingValues[1].text = cpuAI ? "< ON >" : "< OFF >";
            settingValues[2].text = cameraShake ? "< ON >" : "< OFF >";
            for (int i = 0; i < settingRows.Length; i++)
            {
                bool on = i == settingCursor;
                settingRows[i].color = on ? UITheme.P1 : UITheme.Paper;
            }
        }

        void RefreshTabs()
        {
            tabPrevPrompt.text = GameInput.P1.Label(GameAction.TabPrev);
            tabNextPrompt.text = GameInput.P1.Label(GameAction.TabNext);
        }

        // ---------------------------------------------------------------- footer

        void RefreshFooter()
        {
            var p1 = GameInput.P1;
            string move = p1.MoveLabel, confirm = p1.Label(GameAction.Confirm), back = p1.Label(GameAction.Cancel);
            string tabs = $"{p1.Label(GameAction.TabPrev)}/{p1.Label(GameAction.TabNext)}";

            string[] pairs;
            if (tab == MenuTab.Settings) pairs = new[] { move, "Change", confirm, "Next value", back, "Back", tabs, "Tabs" };
            else if (tab == MenuTab.Online) pairs = new[] { back, "Back", tabs, "Tabs" };
            else if (AllReady) pairs = new[] { move, "Stage", confirm, "Fight!", back, "Back", tabs, "Tabs" };
            else
            {
                bool passive = (ChoosingSlot() ?? slots[0]).step == Step.Passive;
                pairs = new[] { move, passive ? "Passive / Weapon look" : "Move", confirm, passive ? "Lock in" : "Select", back, "Back", tabs, "Tabs" };
            }

            string signature = string.Join("|", pairs);
            if (signature == footerSignature) return;
            footerSignature = signature;

            for (int i = footerRow.childCount - 1; i >= 0; i--) Destroy(footerRow.GetChild(i).gameObject);
            for (int i = 0; i < pairs.Length; i += 2) AddPrompt(pairs[i], pairs[i + 1]);
        }

        void AddPrompt(string button, string action)
        {
            var chip = UIFactory.Skew(footerRow, "Button", UITheme.Prompt(button), 8f);
            var fit = chip.gameObject.AddComponent<LayoutElement>();
            fit.preferredWidth = Mathf.Max(44f, button.Length * 15f + 26f);
            fit.preferredHeight = 38f;
            UIFactory.Label(chip.transform, "Text", button, 21, UITheme.PromptText(button), TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform.Stretch();

            var label = UIFactory.Label(footerRow, "Action", action, 23, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);
            var labelFit = label.gameObject.AddComponent<LayoutElement>();
            labelFit.preferredWidth = action.Length * 13f + 34f;
        }
    }
}
