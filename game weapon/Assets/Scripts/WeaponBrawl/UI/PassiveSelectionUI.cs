using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace WeaponBrawl
{
    /// <summary>
    /// Two-step pre-match screen: (1) pick one of the 5 weapon classes, (2) pick 1 of its 2 passives.
    /// Mouse/touch or keyboard (1-5, 1-2, Enter, Esc).
    /// </summary>
    public class PassiveSelectionUI : MonoBehaviour
    {
        static readonly Color CardColor = new Color(0.13f, 0.14f, 0.19f);
        static readonly Color Muted = new Color(0.62f, 0.65f, 0.72f);
        static readonly Color Gold = new Color(1f, 0.83f, 0.3f);
        static readonly Color Confirm = new Color(0.2f, 0.65f, 0.35f);

        public event Action<WeaponType, int> Confirmed;
        public event Action Cancelled;

        /// <summary>When true (a battle is running), Esc on the class page closes the screen.</summary>
        public bool CanCancel { get; set; }

        RectTransform classPage, passivePage;
        Text passiveTitle, details, cancelHint;
        readonly Button[] passiveButtons = new Button[2];
        readonly Outline[] passiveOutlines = new Outline[2];
        readonly Text[] passiveNames = new Text[2];
        readonly Text[] passiveDescriptions = new Text[2];
        Button confirmButton;
        WeaponDefinition selected;
        int selectedPassive = -1;

        public static PassiveSelectionUI Create(Canvas canvas)
        {
            var rt = UIFactory.Node("WeaponSelect", canvas.transform).Stretch();
            rt.gameObject.AddComponent<Image>().color = new Color(0.04f, 0.05f, 0.08f, 0.96f);
            var ui = rt.gameObject.AddComponent<PassiveSelectionUI>();
            ui.BuildClassPage();
            ui.BuildPassivePage();
            ui.Close();
            return ui;
        }

        public void Open()
        {
            gameObject.SetActive(true);
            ShowClassPage();
        }

        public void Close() => gameObject.SetActive(false);

        // ---------------------------------------------------------------- step 1: class

        void BuildClassPage()
        {
            classPage = UIFactory.Node("ClassPage", transform).Stretch();
            UIFactory.Label(classPage, "Title", "CHOOSE YOUR WEAPON CLASS", 60, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold)
                .rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -60f), new Vector2(1600f, 80f));
            UIFactory.Label(classPage, "Subtitle", "Every class has base stats, a looping combo, skills and an ultimate. Click a card or press 1-5.", 26, Muted)
                .rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -140f), new Vector2(1600f, 40f));

            var row = UIFactory.Node("Cards", classPage).Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -40f), new Vector2(1760f, 660f));
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 22f;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = true;

            var defs = WeaponDatabase.All;
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                var card = UIFactory.MakeButton(row, def.displayName, null, CardColor, () => ShowPassivePage(def));
                var v = card.gameObject.AddComponent<VerticalLayoutGroup>();
                v.padding = new RectOffset(24, 24, 0, 24);
                v.spacing = 12f;
                v.childControlWidth = v.childControlHeight = true;
                v.childForceExpandWidth = true;
                v.childForceExpandHeight = false;

                UIFactory.Size(UIFactory.Panel(card.transform, "Strip", def.color), height: 14f);
                Line(card.transform, $"[{i + 1}]", 22, Muted);
                Line(card.transform, def.displayName.ToUpperInvariant(), 42, def.color, FontStyle.Bold);
                Line(card.transform, def.examples, 22, Muted, FontStyle.Italic);
                Line(card.transform, StatsText(def), 26, Color.white);
                Line(card.transform, $"<b>Skills</b>\n{SkillList(def)}", 22, new Color(0.85f, 0.87f, 0.92f));
                Line(card.transform, $"<b>Ultimate</b>\n{def.ultimate.name}", 24, Gold);
            }

            cancelHint = UIFactory.Label(classPage, "CancelHint", "ESC - back to battle", 24, Muted);
            cancelHint.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(800f, 40f));
        }

        static Text Line(Transform parent, string text, int size, Color color, FontStyle style = FontStyle.Normal) =>
            UIFactory.Label(parent, "Line", text, size, color, TextAnchor.UpperLeft, style);

        static string StatsText(WeaponDefinition d) =>
            $"ATK  <b>{d.attack}</b>\n" +
            $"HP   <b>{d.health}</b>\n" +
            $"DEF  <b>{d.defense}</b>  <color=#9AA0AE>(-{Mathf.FloorToInt(d.defense / 10f)} dmg taken)</color>\n" +
            $"CRIT <b>{d.critRate * 100f:0}%</b>  <color=#9AA0AE>x{1f + d.critDamage:0.0} dmg</color>\n" +
            $"COMBO <b>{d.comboLength}-hit loop</b>";

        static string SkillList(WeaponDefinition d)
        {
            var sb = new StringBuilder();
            foreach (var s in d.skills) sb.Append("• ").Append(s.name).Append('\n');
            return sb.ToString().TrimEnd();
        }

        void ShowClassPage()
        {
            classPage.gameObject.SetActive(true);
            passivePage.gameObject.SetActive(false);
            cancelHint.gameObject.SetActive(CanCancel);
        }

        // ---------------------------------------------------------------- step 2: passive

        void BuildPassivePage()
        {
            passivePage = UIFactory.Node("PassivePage", transform).Stretch();
            passiveTitle = UIFactory.Label(passivePage, "Title", "", 60, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            passiveTitle.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -60f), new Vector2(1600f, 80f));
            UIFactory.Label(passivePage, "Subtitle", "Choose 1 passive from 2. Click a card or press 1 / 2, then Enter.", 26, Muted)
                .rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -140f), new Vector2(1600f, 40f));

            var row = UIFactory.Node("Passives", passivePage).Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 150f), new Vector2(1300f, 280f));
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 40f;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = true;

            for (int i = 0; i < 2; i++)
            {
                int index = i;
                var card = UIFactory.MakeButton(row, "Passive" + i, null, CardColor, () => SelectPassive(index));
                var v = card.gameObject.AddComponent<VerticalLayoutGroup>();
                v.padding = new RectOffset(32, 32, 28, 28);
                v.spacing = 14f;
                v.childControlWidth = v.childControlHeight = true;
                v.childForceExpandWidth = true;
                v.childForceExpandHeight = false;

                var outline = card.gameObject.AddComponent<Outline>();
                outline.effectDistance = new Vector2(5f, -5f);
                outline.enabled = false;

                Line(card.transform, $"[{i + 1}]", 22, Muted);
                passiveNames[i] = Line(card.transform, "", 40, Color.white, FontStyle.Bold);
                passiveDescriptions[i] = Line(card.transform, "", 28, new Color(0.85f, 0.87f, 0.92f));
                passiveButtons[i] = card;
                passiveOutlines[i] = outline;
            }

            var detailPanel = UIFactory.Panel(passivePage, "Details", CardColor);
            detailPanel.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -170f), new Vector2(1300f, 300f));
            details = UIFactory.Label(detailPanel.transform, "Text", "", 26, Color.white, TextAnchor.UpperLeft);
            details.rectTransform.Stretch(28f);

            UIFactory.MakeButton(passivePage, "Back", "<  BACK", new Color(0.3f, 0.32f, 0.38f), ShowClassPage)
                .GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-330f, 50f), new Vector2(360f, 90f));
            confirmButton = UIFactory.MakeButton(passivePage, "Confirm", "ENTER BATTLE  >", Confirm, ConfirmSelection);
            confirmButton.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(330f, 50f), new Vector2(420f, 90f));
        }

        void ShowPassivePage(WeaponDefinition def)
        {
            selected = def;
            selectedPassive = -1;
            passiveTitle.text = $"<color={UIFactory.Hex(def.color)}>{def.displayName.ToUpperInvariant()}</color>  -  CHOOSE 1 PASSIVE";
            for (int i = 0; i < 2; i++)
            {
                passiveNames[i].text = def.passives[i].name;
                passiveNames[i].color = def.color;
                passiveDescriptions[i].text = def.passives[i].description;
                passiveOutlines[i].effectColor = def.color;
            }
            details.text = DetailsText(def);
            RefreshPassiveCards();
            classPage.gameObject.SetActive(false);
            passivePage.gameObject.SetActive(true);
        }

        void SelectPassive(int index)
        {
            selectedPassive = index;
            RefreshPassiveCards();
        }

        void RefreshPassiveCards()
        {
            for (int i = 0; i < 2; i++)
            {
                bool on = i == selectedPassive;
                passiveOutlines[i].enabled = on;
                UIFactory.SetColor(passiveButtons[i], on ? Color.Lerp(CardColor, selected.color, 0.35f) : CardColor);
            }
            confirmButton.interactable = selectedPassive >= 0;
        }

        void ConfirmSelection()
        {
            if (selected == null || selectedPassive < 0) return;
            Close();
            Confirmed?.Invoke(selected.type, selectedPassive);
        }

        static string DetailsText(WeaponDefinition d)
        {
            var sb = new StringBuilder("<b>SKILLS</b>\n");
            for (int i = 0; i < d.skills.Length; i++)
            {
                var s = d.skills[i];
                sb.Append($"<color={UIFactory.Hex(d.color)}>[{Controls.Skills[i]}]</color>  <b>{s.name}</b>: {s.description}  <color=#9AA0AE>({s.cooldown:0}s)</color>\n");
            }
            sb.Append($"\n<b>ULTIMATE</b>  <color=#FFD54A>[{Controls.Ultimate}]</color>  <b>{d.ultimate.name}</b>: {d.ultimate.description}");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- keyboard

        void Update()
        {
            if (classPage.gameObject.activeSelf)
            {
                var defs = WeaponDatabase.All;
                for (int i = 0; i < defs.Count && i < 9; i++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i)) ShowPassivePage(defs[i]);
                if (CanCancel && Input.GetKeyDown(KeyCode.Escape))
                {
                    Close();
                    Cancelled?.Invoke();
                }
                return;
            }

            if (Input.GetKeyDown(KeyCode.Alpha1)) SelectPassive(0);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SelectPassive(1);
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) ConfirmSelection();
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)) ShowClassPage();
        }
    }
}
