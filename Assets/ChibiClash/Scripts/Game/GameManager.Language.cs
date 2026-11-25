using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// The LANGUAGE window (docs/localization.md): a search box (English or native name) over a scrollable list of every
    /// ISO 639-1 language (Resources/Loc/languages.json), native name first. It opens by itself once on first launch,
    /// after the age question and before the player name; later from SETTINGS > LANGUAGE or the globe button at the top
    /// right of every menu page. The choice is saved per device (SaveData.language) and switches the whole UI at once.
    /// Keyboard: type to search, Up / Down / Page Up / Page Down move, Enter confirms, Esc closes (not on first launch).
    /// Gamepad: D-pad up/down moves, left/right jumps a page, A confirms, B closes.
    /// </summary>
    public partial class GameManager
    {
        bool langDialog;
        string langQuery = "", langShownQuery;
        int langSel, langTop;
        string langPick;   // highlighted code
        readonly List<LanguageInfo> langList = new List<LanguageInfo>();

        const int LangRows = 7;
        const float LangRowH = 38f;

        /// <summary>First launch: the age is known but no language was chosen yet.</summary>
        bool NeedsLanguage => string.IsNullOrEmpty(Save.language) && !PlayerIdentity.NeedsAge(Save.ageBand) && Save.age > 0;

        bool LanguageShown => NeedsLanguage || langDialog;

        /// <summary>Open the LANGUAGE window (SETTINGS row, globe button, ShowScreen("language")).</summary>
        void OpenLanguageDialog()
        {
            langDialog = true;
            langQuery = "";
            langShownQuery = null;
            langPick = null;
        }

        /// <summary>Rebuild the filtered list when the search changes (not per frame).</summary>
        void RefreshLanguageList()
        {
            if (langShownQuery == langQuery) return;
            langShownQuery = langQuery;
            langList.Clear();
            foreach (var l in Loc.Languages)
                if (Loc.Matches(l, langQuery)) langList.Add(l);
            if (langPick == null) langPick = Save.language.Length > 0 ? Save.language : Loc.DeviceLanguage();
            langSel = 0;
            for (int i = 0; i < langList.Count; i++)
                if (langList[i].code == langPick) { langSel = i; break; }
            if (langList.Count > 0 && langList[langSel].code != langPick && langQuery.Length > 0) langSel = 0;
            langTop = Mathf.Clamp(langSel - LangRows / 2, 0, Mathf.Max(0, langList.Count - LangRows));
        }

        void MoveLanguage(int d)
        {
            if (langList.Count == 0) return;
            int n = Mathf.Clamp(langSel + d, 0, langList.Count - 1);
            if (n == langSel) return;
            langSel = n;
            langPick = langList[n].code;
            if (langSel < langTop) langTop = langSel;
            if (langSel >= langTop + LangRows) langTop = langSel - LangRows + 1;
            UiSound.Navigate();
        }

        /// <summary>Gamepad driving (Update): D-pad moves the highlight, A confirms (keys type into the search box).</summary>
        void PadLanguageInput()
        {
            if (MenuInput.PadFired(NavCmd.Up)) MoveLanguage(-1);
            if (MenuInput.PadFired(NavCmd.Down)) MoveLanguage(1);
            if (MenuInput.PadFired(NavCmd.Left)) MoveLanguage(-LangRows);
            if (MenuInput.PadFired(NavCmd.Right)) MoveLanguage(LangRows);
            if (MenuInput.PadFired(NavCmd.Confirm)) padSubmit = true;
        }

        void ChooseLanguage(LanguageInfo l)
        {
            Save.language = l.code;
            Save.Save();
            Loc.Use(l.code);
            langDialog = false;
            UiSound.Confirm();
        }

        void LanguageDialog()
        {
            RefreshLanguageList();
            bool first = NeedsLanguage;
            // Height from the content: title, search, list, buttons (no hint line, user request 2026-11-25).
            var r = DialogRect(720f, 134f + LangRows * LangRowH + 14f + 50f + 20f);
            r.y = Mathf.Max(118f, H * 0.5f - r.height * 0.5f);
            Gui.Box(r, Gui.P2);
            Gui.Display(new Rect(r.x, r.y + 12f, r.width, 44f), Loc.T("lang.title"), 34, Gui.Paper, TextAnchor.MiddleCenter);
            Gui.Text(new Rect(r.x + 30f, r.y + 54f, r.width - 60f, 22f), Loc.T("lang.subtitle"), 13, Gui.Muted, TextAnchor.UpperCenter, false);

            // Keyboard list keys before the text field sees them.
            var e = Event.current;
            bool enter = EnterPressed() || TakePadSubmit();
            if (e.type == EventType.KeyDown)
            {
                int d = e.keyCode == KeyCode.UpArrow ? -1 : e.keyCode == KeyCode.DownArrow ? 1 :
                        e.keyCode == KeyCode.PageUp ? -LangRows : e.keyCode == KeyCode.PageDown ? LangRows : 0;
                if (d != 0) { MoveLanguage(d); e.Use(); }
            }

            // Search box with a magnifier drawn from shapes. Left-aligned text that starts after the magnifier: IMGUI places
            // the caret wrongly in a centred text field, so typed letters appeared away from the cursor (user report
            // 2026-11-25). The placeholder sits exactly where the first typed letter goes.
            var field = new Rect(r.x + 30f, r.y + 82f, r.width - 60f, 42f);
            GUI.SetNextControlName("LangSearch");
            string typed = GUI.TextField(field, langQuery ?? "", 40, SearchFieldStyle());
            if (typed != langQuery) langQuery = typed;
            if (GUI.GetNameOfFocusedControl() != "LangSearch") GUI.FocusControl("LangSearch");
            if (string.IsNullOrEmpty(langQuery))
                Gui.Text(new Rect(field.x + SearchTextLeft, field.y, field.width - SearchTextLeft - 10f, field.height),
                         Loc.T("lang.search_hint"), 15, WithA(Gui.Muted, 0.75f), TextAnchor.MiddleLeft, false, false);
            Gui.Circle(new Rect(field.x + 12f, field.y + 11f, 16f, 16f), Gui.Muted);
            Gui.Circle(new Rect(field.x + 15f, field.y + 14f, 10f, 10f), Color.white);
            Gui.Line(new Vector2(field.x + 26f, field.y + 26f), new Vector2(field.x + 32f, field.y + 32f), Gui.Muted, 3f);

            // The list (only the visible rows are drawn; the wheel scrolls, a click picks, a click on the pick confirms).
            var list = new Rect(r.x + 30f, r.y + 134f, r.width - 60f, LangRows * LangRowH);
            Gui.Rect(list, WithA(Gui.Ink, 0.6f));
            if (e.type == EventType.ScrollWheel && list.Contains(e.mousePosition))
            {
                langTop = Mathf.Clamp(langTop + (e.delta.y > 0f ? 2 : -2), 0, Mathf.Max(0, langList.Count - LangRows));
                e.Use();
            }
            LanguageInfo confirm = null;
            if (langList.Count == 0)
                Gui.Text(list, Loc.T("lang.no_match"), 15, Gui.Muted, TextAnchor.MiddleCenter, false);
            for (int row = 0; row < LangRows; row++)
            {
                int i = langTop + row;
                if (i >= langList.Count) break;
                var l = langList[i];
                var rr = new Rect(list.x, list.y + row * LangRowH, list.width - 14f, LangRowH - 2f);
                bool sel = i == langSel;
                bool click = Gui.Hit(rr, out bool hot);
                if (sel) Gui.Rect(rr, Gui.P2);
                else if (hot) Gui.Rect(rr, Gui.Steel);
                if (sel) Gui.Rect(new Rect(rr.x, rr.y, 5f, rr.height), Gui.Gold);
                // Only the language's own name (user request 2026-11-25: no English name, no "English text for now",
                // no current-language marker).
                Gui.Text(new Rect(rr.x + 16f, rr.y, rr.width - 32f, rr.height), l.native, 18, Gui.Paper, TextAnchor.MiddleLeft, true, false);
                if (click)
                {
                    if (sel) confirm = l;
                    else { langSel = i; langPick = l.code; UiSound.Step(); }
                }
            }
            // Scroll bar (track and thumb; click the track to jump).
            if (langList.Count > LangRows)
            {
                var track = new Rect(list.xMax - 8f, list.y + 2f, 6f, list.height - 4f);
                Gui.Rect(track, WithA(Gui.Steel, 0.8f));
                float frac = (float)LangRows / langList.Count;
                float pos = (float)langTop / (langList.Count - LangRows);
                float th = Mathf.Max(18f, track.height * frac);
                Gui.Rect(new Rect(track.x, track.y + (track.height - th) * pos, track.width, th), Gui.Gold);
                if (e.type == EventType.MouseDown && new Rect(track.x - 6f, track.y, track.width + 12f, track.height).Contains(e.mousePosition))
                {
                    float t = Mathf.Clamp01((e.mousePosition.y - track.y) / track.height);
                    langTop = Mathf.Clamp(Mathf.RoundToInt(t * (langList.Count - LangRows)), 0, langList.Count - LangRows);
                    e.Use();
                }
            }

            var cur = langList.Count > 0 ? langList[Mathf.Clamp(langSel, 0, langList.Count - 1)] : null;
            float by = list.yMax + 14f;
            float bw = first ? 300f : 220f;
            float bx = first ? r.x + (r.width - bw) * 0.5f : r.x + r.width * 0.5f - bw - 10f;
            string ok = cur != null ? Loc.T("lang.confirm", cur.native) : Loc.T("lang.confirm_none");
            if (Gui.Button(new Rect(bx, by, bw, 50f), ok, Gui.P1, 18, cur != null) || (enter && cur != null)) confirm = cur;
            if (!first && Gui.Button(new Rect(r.x + r.width * 0.5f + 10f, by, 220f, 50f), Loc.T("common.cancel"), Gui.Steel, 18))
            {
                langDialog = false;
                UiSound.Back();
            }
            if (enter && e.type == EventType.KeyDown) e.Use();
            if (confirm != null) ChooseLanguage(confirm);
        }

        const float SearchTextLeft = 44f;   // text starts right of the magnifier
        GUIStyle searchStyle;

        /// <summary>The search box style: like the other text fields, but left-aligned with room for the magnifier.</summary>
        GUIStyle SearchFieldStyle()
        {
            if (searchStyle == null)
            {
                searchStyle = new GUIStyle(Field(20)) { alignment = TextAnchor.MiddleLeft };
                searchStyle.padding = new RectOffset((int)SearchTextLeft, 10, searchStyle.padding.top, searchStyle.padding.bottom);
            }
            return searchStyle;
        }

        /// <summary>
        /// The language button at the top right of every menu page (under the profile chip): a globe drawn from shapes
        /// and the current language's own name. Opens the LANGUAGE window.
        /// </summary>
        void LanguageButton()
        {
            var info = Loc.Find(Loc.Code);
            string label = info != null ? info.native : Loc.Code;
            float w = Mathf.Max(96f, Gui.Measure(label, 13) + 46f);
            var r = new Rect(W - 16f - w, 56f, w, 26f);
            bool click = Gui.Hit(r, out bool hot);
            Gui.Skew(r, hot ? new Color(0.1f, 0.16f, 0.28f, 0.95f) : new Color(0.06f, 0.1f, 0.18f, 0.95f), 0.3f);
            Gui.SkewFrame(r, hot ? Gui.Paper : Gui.Steel, 1f, 0.3f);
            Globe(new Rect(r.x + 12f, r.y + 5f, 16f, 16f), hot ? Gui.Gold : Gui.P2);
            Gui.Text(new Rect(r.x + 34f, r.y, w - 42f, r.height), label, 13, hot ? Gui.Gold : Gui.Paper, TextAnchor.MiddleLeft, true, false);
            if (click) { UiSound.Confirm(); OpenLanguageDialog(); }
        }

        /// <summary>A small globe icon: a disc, an inner ring, the equator and a meridian.</summary>
        static void Globe(Rect g, Color c)
        {
            Gui.Circle(g, c);
            Gui.Circle(new Rect(g.x + 1.5f, g.y + 1.5f, g.width - 3f, g.height - 3f), Gui.Navy);
            Gui.Circle(new Rect(g.x + g.width * 0.3f, g.y + 1.5f, g.width * 0.4f, g.height - 3f), c);
            Gui.Circle(new Rect(g.x + g.width * 0.3f + 1.5f, g.y + 3f, g.width * 0.4f - 3f, g.height - 6f), Gui.Navy);
            Gui.Rect(new Rect(g.x + 1f, g.center.y - 0.75f, g.width - 2f, 1.5f), c);
            Gui.Rect(new Rect(g.center.x - 0.75f, g.y + 1f, 1.5f, g.height - 2f), c);
        }
    }
}
