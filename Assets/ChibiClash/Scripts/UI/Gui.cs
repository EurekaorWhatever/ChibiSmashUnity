using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChibiClash
{
    /// <summary>
    /// Arcade-fighter immediate-mode UI design system on a 1280x720 virtual canvas: red-vs-blue palette, heavy
    /// condensed display type, slanted panels, halftone / speed-line graphics, and keyboard + gamepad focus
    /// navigation that works for every <see cref="Button"/> and <see cref="Hit"/> area drawn through it.
    /// Ported from the copy-ghibli-smash fork (UI-1). Display only: nothing here reads or writes the simulation.
    /// Call <see cref="Begin"/> at the start of OnGUI and <see cref="End"/> at its end (focus snapshot).
    /// </summary>
    public static class Gui
    {
        public static float Scale { get; private set; } = 1f;
        public static float W { get; private set; } = 1280f;
        public static float H { get; private set; } = 720f;

        static Color Hex(int rgb, float a = 1f) =>
            new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);

        // ---------------------------------------------------------------- palette

        public static readonly Color Navy = Hex(0x101B30);
        public static readonly Color Steel = Hex(0x293B57);
        public static readonly Color Ink = Hex(0x070D1A);
        public static readonly Color P1 = Hex(0xEF344D);
        public static readonly Color P2 = Hex(0x168FEA);
        /// <summary>Warm white (#F5F2EC).</summary>
        public static readonly Color Paper = Hex(0xF5F2EC);
        public static readonly Color Gold = Hex(0xF6C74C);
        public static readonly Color Disabled = Hex(0x262D3A);
        static readonly Color ButtonFill = Hex(0x15233B);

        // Names used by the HUD and older screens, mapped onto the arcade palette.
        public static readonly Color Panel = Hex(0x0C1526, 0.94f);
        public static readonly Color PanelLight = Hex(0x1C2B44, 0.97f);
        public static readonly Color Accent = P1;
        public static readonly Color Accent2 = P2;
        public static readonly Color Yellow = Gold;
        public static readonly Color Green = Hex(0x3BE07A);
        public static readonly Color Red = P1;
        public static readonly Color Muted = Hex(0x9AA6BC);

        public static Color PlayerColor(int i) => i == 0 ? P1 : P2;

        /// <summary>
        /// In-match colour of a fighter (TP-6). 2v2: team 0 = <see cref="P1"/> red, team 1 = <see cref="P2"/> blue.
        /// FFA: per slot P1 red, P2 blue, P3 <see cref="Gold"/>, P4 <see cref="Green"/>. 1v1 (or any other format) =
        /// <see cref="PlayerColor"/>. Pure: no GUI state is read.
        /// </summary>
        /// <example><code>Color c = Gui.SlotColor(State.Format, State.Team[i], i);</code></example>
        public static Color SlotColor(int format, int team, int slot)
        {
            if (format == Netcode.ChibiMatchConfig.Format2v2) return team == 0 ? P1 : P2;
            if (format == Netcode.ChibiMatchConfig.FormatFfa)
                switch (slot) { case 0: return P1; case 1: return P2; case 2: return Gold; default: return Green; }
            return PlayerColor(slot);
        }

        /// <summary>Global opacity multiplier applied to everything drawn through Gui (screen fades).</summary>
        public static float Alpha = 1f;
        static Color A(Color c) { c.a *= Alpha; return c; }

        // ---------------------------------------------------------------- frame

        static readonly Stack<Matrix4x4> matrices = new Stack<Matrix4x4>();

        public static void Begin()
        {
            Scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            W = Screen.width / Scale;
            H = Screen.height / Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
            GUI.color = Color.white;
            matrices.Clear();
            Alpha = 1f;
            Interactive = true;
            rects.Clear();
            live.Clear();
            preferred = -1;
            markNext = false;
        }

        /// <summary>Call at the end of OnGUI: snapshots the focusable widgets for keyboard / gamepad navigation.</summary>
        public static void End()
        {
            if (Event.current.type != EventType.Repaint) return;
            lastRects.Clear();
            lastRects.AddRange(rects);
            lastLive.Clear();
            lastLive.AddRange(live);
            lastPreferred = preferred;
            lastClaimLR = claimLR;
            claimLR = false;
            segStep = 0;   // a step nobody consumed this frame (focus was not on a row) must not jump a row later
            activate = -1;
            if (focus >= 0)
            {
                bool valid = focus < lastRects.Count && lastLive[focus];
                if (!valid || Vector2.Distance(lastRects[focus].center, focusRect.center) > 3f) focus = Nearest(focusRect.center);
            }
            if (focus < 0 && NavMode) focus = DefaultFocus();
            if (focus >= 0) focusRect = lastRects[focus];
        }

        public static Vector2 WorldToGui(Vector3 world)
        {
            var cam = Camera.main;
            if (cam == null) return Vector2.zero;
            var sp = cam.WorldToScreenPoint(world);
            return new Vector2(sp.x / Scale, (Screen.height - sp.y) / Scale);
        }

        public static void PushSkew(float k, float pivotY)
        {
            matrices.Push(GUI.matrix);
            var s = Matrix4x4.identity;
            s.m01 = -k;
            s.m03 = k * pivotY;
            GUI.matrix = GUI.matrix * s;
        }

        public static void PushOffset(Vector2 o)
        {
            matrices.Push(GUI.matrix);
            GUI.matrix = GUI.matrix * Matrix4x4.Translate(new Vector3(o.x, o.y, 0f));
        }

        public static void Pop()
        {
            if (matrices.Count > 0) GUI.matrix = matrices.Pop();
        }

        public static float EaseOut(float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            return 1f - u * u * u;
        }

        public static float EaseInOut(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;
        }

        // ---------------------------------------------------------------- fonts

        static Font displayFont, bodyFont;
        static bool fontsTried;
        static GUIStyle style, displayStyle;
        static readonly GUIContent measureContent = new GUIContent();   // reused: Measure runs many times per frame

        // First installed family wins; the rest stay as glyph fallbacks.
        static readonly string[] DisplayNames = { "Anton", "Bebas Neue", "Oswald", "Impact", "Barlow Condensed ExtraBold", "Bahnschrift", "Arial Black", "Segoe UI Symbol", "Arial" };
        static readonly string[] BodyNames = { "Barlow Condensed", "Oswald", "Bahnschrift SemiCondensed", "Bahnschrift", "Roboto Condensed", "Arial Narrow", "Segoe UI", "Segoe UI Symbol", "Arial" };

        /// <summary>Resolves the condensed OS fonts once (loading screen).</summary>
        public static void WarmUpFonts()
        {
            if (fontsTried) return;
            fontsTried = true;
            displayFont = OsFont(DisplayNames);
            bodyFont = OsFont(BodyNames);
        }

        static Font OsFont(string[] names)
        {
            try
            {
                var installed = new HashSet<string>(Font.GetOSInstalledFontNames());
                var list = new List<string>();
                foreach (var n in names)
                    if (installed.Contains(n)) list.Add(n);
                return list.Count == 0 ? null : Font.CreateDynamicFontFromOSFont(list.ToArray(), 32);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("ChibiClash: OS font lookup failed, using the default font: " + e.Message);
                return null;
            }
        }

        static GUIStyle Style(bool display)
        {
            if (style == null)
            {
                WarmUpFonts();
                style = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, clipping = TextClipping.Overflow };
                if (bodyFont != null) style.font = bodyFont;
                displayStyle = new GUIStyle(style) { wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };
                if (displayFont != null) displayStyle.font = displayFont;
            }
            return display ? displayStyle : style;
        }

        public static float Measure(string s, int size, bool display = false, bool bold = true)
        {
            var st = Style(display);
            st.fontSize = size;
            st.fontStyle = display ? (displayFont == null ? FontStyle.Bold : FontStyle.Normal) : (bold ? FontStyle.Bold : FontStyle.Normal);
            measureContent.text = s;
            return st.CalcSize(measureContent).x;
        }

        public static void Text(Rect r, string s, int size, Color c, TextAnchor anchor = TextAnchor.MiddleCenter, bool bold = true, bool shadow = true)
        {
            var st = Style(false);
            st.fontSize = size;
            st.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            st.alignment = anchor;
            c = A(c);
            if (shadow && !s.Contains("<color"))
            {
                SetTextColor(st, new Color(0f, 0f, 0f, c.a * 0.6f));
                GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), s, st);
            }
            SetTextColor(st, c);
            GUI.Label(r, s, st);
        }

        /// <summary>Heavy condensed headline type, slanted like an italic, with a hard offset shadow (pass Color.clear for none).</summary>
        public static void Display(Rect r, string s, int size, Color c, TextAnchor anchor = TextAnchor.MiddleLeft, float slant = 0.2f, Color? shadow = null, float shadowOffset = -1f)
        {
            var st = Style(true);
            st.fontSize = size;
            st.fontStyle = displayFont == null ? FontStyle.Bold : FontStyle.Normal;
            st.alignment = anchor;
            if (slant != 0f) PushSkew(slant, r.center.y);
            var sc = shadow ?? Ink;
            if (sc.a > 0f)
            {
                float o = shadowOffset >= 0f ? shadowOffset : Mathf.Max(2f, size * 0.06f);
                SetTextColor(st, A(new Color(sc.r, sc.g, sc.b, sc.a * c.a)));
                GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), s, st);
            }
            SetTextColor(st, A(c));
            GUI.Label(r, s, st);
            if (slant != 0f) Pop();
        }

        /// <summary>
        /// IMGUI draws a label in its hover/active state when the mouse is over it; set them all so text never
        /// changes colour (or "glows") under the cursor.
        /// </summary>
        static void SetTextColor(GUIStyle st, Color c)
        {
            st.normal.textColor = c; st.hover.textColor = c; st.active.textColor = c; st.focused.textColor = c;
            st.onNormal.textColor = c; st.onHover.textColor = c; st.onActive.textColor = c; st.onFocused.textColor = c;
            st.normal.background = null; st.hover.background = null; st.active.background = null; st.focused.background = null;
        }

        // ---------------------------------------------------------------- primitives

        // The arcade look is sharp-edged: small corner radii are flattened, only circles keep their radius.
        static float Corner(Rect r, float radius) => radius >= Mathf.Min(r.width, r.height) * 0.45f ? radius : Mathf.Min(radius, 1.5f);

        public static void Rect(Rect r, Color c, float radius = 0f)
        {
            GUI.DrawTexture(r, Art.SolidTex, ScaleMode.StretchToFill, true, 0f, A(c), 0f, Corner(r, radius));
        }

        public static void Frame(Rect r, Color c, float width, float radius = 0f)
        {
            GUI.DrawTexture(r, Art.SolidTex, ScaleMode.StretchToFill, true, 0f, A(c), width, Corner(r, radius));
        }

        public static void Circle(Rect r, Color c)
        {
            GUI.DrawTexture(r, Art.CircleTex, ScaleMode.StretchToFill, true, 0f, A(c), 0f, 0f);
        }

        public static void Glow(Rect r, Color c)
        {
            GUI.DrawTexture(r, Art.SoftTex, ScaleMode.StretchToFill, true, 0f, A(c), 0f, 0f);
        }

        public static void Tex(Rect r, Texture t, Color c, bool flipX = false)
        {
            if (t == null) return;
            if (!flipX)
            {
                GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true, 0f, A(c), 0f, 0f);
                return;
            }
            var old = GUI.color;
            GUI.color = A(c);
            GUI.DrawTextureWithTexCoords(r, t, new Rect(1f, 0f, -1f, 1f), true);
            GUI.color = old;
        }

        public static void Tiled(Rect r, Texture t, float cell, Color c, Vector2 scroll = default)
        {
            var old = GUI.color;
            GUI.color = A(c);
            GUI.DrawTextureWithTexCoords(r, t, new Rect(scroll.x, scroll.y, r.width / cell, r.height / cell), true);
            GUI.color = old;
        }

        /// <summary>Parallelogram: the rect sheared about its centre (positive k leans right).</summary>
        public static void Skew(Rect r, Color c, float k = 0.22f)
        {
            PushSkew(k, r.center.y);
            Rect(r, c);
            Pop();
        }

        public static void SkewFrame(Rect r, Color c, float width, float k = 0.22f)
        {
            PushSkew(k, r.center.y);
            Frame(r, c, width);
            Pop();
        }

        /// <summary>Solid chevron arrow (">" or "&lt;") built from two sheared strokes, no font glyph needed.</summary>
        public static void Chevron(Rect r, Color c, bool right = true)
        {
            float t = r.width * 0.45f;
            float k = (r.width - t) / (r.height * 0.5f);
            if (!right) k = -k;
            float x = r.x + (r.width - t) * 0.5f;
            Skew(new Rect(x, r.y, t, r.height * 0.5f), c, -k);
            Skew(new Rect(x, r.y + r.height * 0.5f, t, r.height * 0.5f), c, k);
        }

        public static void Halftone(Rect r, Color c, float cell = 10f) => Tiled(r, GuiArt.DotTex, cell, c);
        /// <summary>Halftone dots that fade out to the right (or left when flipped).</summary>
        public static void HalftoneFade(Rect r, Color c, bool flipX = false) => Tex(r, GuiArt.HalftoneRamp, c, flipX);
        public static void Hatch(Rect r, Color c, float cell = 14f, float scroll = 0f) => Tiled(r, GuiArt.StripeTex, cell, c, new Vector2(scroll, 0f));
        public static void Grain(Rect r, float a) => Tiled(r, GuiArt.GrainTex, 128f, new Color(1f, 1f, 1f, a));
        /// <summary>Colour fading from transparent (top) to opaque (bottom).</summary>
        public static void FadeUp(Rect r, Color c) => Tex(r, GuiArt.FadeTex, c);

        static float Hash(int n)
        {
            n = (n << 13) ^ n;
            return ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 2147483647f;
        }

        /// <summary>Horizontal motion streaks travelling right at roughly speed px/s.</summary>
        public static void SpeedLines(Rect area, Color c, int count, float speed, int seed = 0)
        {
            float t = Time.unscaledTime * speed;
            for (int i = 0; i < count; i++)
            {
                float h1 = Hash(i * 7 + seed), h2 = Hash(i * 13 + seed + 1), h3 = Hash(i * 29 + seed + 2);
                float len = area.width * (0.12f + 0.3f * h2);
                float y = area.y + h1 * area.height;
                float span = area.width + len;
                float x = area.x - len + Mathf.Repeat(h3 * span + t * (0.6f + h2), span);
                Rect(new Rect(x, y, len, 1f + 2f * h3), new Color(c.r, c.g, c.b, c.a * (0.35f + 0.65f * h2)));
            }
        }

        /// <summary>Angular content panel: dark fill, hard drop shadow, coloured top edge and tab.</summary>
        public static void Box(Rect r, Color accent, float alpha = 0.93f)
        {
            Rect(new Rect(r.x + 5f, r.y + 5f, r.width, r.height), new Color(0f, 0f, 0f, 0.35f * alpha));
            Rect(r, new Color(0.055f, 0.09f, 0.16f, alpha));
            Frame(r, new Color(Steel.r, Steel.g, Steel.b, 0.9f * alpha), 1f);
            Rect(new Rect(r.x, r.y, r.width, 3f), accent);
            Skew(new Rect(r.x + 12f, r.y - 5f, 54f, 6f), accent, 0.6f);
        }

        // ---------------------------------------------------------------- focus navigation

        /// <summary>True while the player drives menus with keys / pad (focus highlight shown, mouse hover ignored).</summary>
        public static bool NavMode;
        /// <summary>When false, widgets still draw but take no input (modal backgrounds, screen wipes). Reset by Begin.</summary>
        public static bool Interactive = true;

        static readonly List<Rect> rects = new List<Rect>(), lastRects = new List<Rect>();
        static readonly List<bool> live = new List<bool>(), lastLive = new List<bool>();
        static int focus = -1, activate = -1, preferred = -1, lastPreferred = -1;
        static Rect focusRect;
        static bool markNext;

        /// <summary>The next focusable widget becomes the default focus of this screen.</summary>
        public static void PreferNext() => markNext = true;

        /// <summary>Forget the focused widget (screen change); the screen's preferred widget is focused next.</summary>
        public static void ResetFocus()
        {
            focus = -1;
            activate = -1;
        }

        static int Register(Rect r, bool enabled)
        {
            var m = GUI.matrix;
            Vector3 a = m.MultiplyPoint3x4(new Vector3(r.xMin, r.yMin, 0f)) / Scale;
            Vector3 b = m.MultiplyPoint3x4(new Vector3(r.xMax, r.yMax, 0f)) / Scale;
            int id = rects.Count;
            rects.Add(UnityEngine.Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)));
            live.Add(enabled && Interactive);
            if (markNext && enabled)
            {
                preferred = id;
                markNext = false;
            }
            return id;
        }

        static int Nearest(Vector2 p)
        {
            int best = -1;
            float bd = float.MaxValue;
            for (int i = 0; i < lastRects.Count; i++)
            {
                if (!lastLive[i]) continue;
                float d = (lastRects[i].center - p).sqrMagnitude;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        static int DefaultFocus()
        {
            if (lastPreferred >= 0 && lastPreferred < lastLive.Count && lastLive[lastPreferred]) return lastPreferred;
            for (int i = 0; i < lastLive.Count; i++)
                if (lastLive[i]) return i;
            return -1;
        }

        static float Gap(float a0, float a1, float b0, float b1) => Mathf.Max(0f, Mathf.Max(b0 - a1, a0 - b1));

        /// <summary>Move focus to the nearest widget in a direction (spatial navigation over last frame's widgets).</summary>
        public static void Navigate(int dx, int dy)
        {
            if (!NavMode || focus < 0 || focus >= lastRects.Count || !lastLive[focus])
            {
                NavMode = true;
                if (focus < 0 || focus >= lastRects.Count || !lastLive[focus]) focus = DefaultFocus();
                if (focus >= 0)
                {
                    focusRect = lastRects[focus];
                    if (dx != 0 || dy != 0) UiSound.Navigate();
                }
                return;
            }
            var from = lastRects[focus];
            int best = -1;
            float bestScore = float.MaxValue;
            for (int i = 0; i < lastRects.Count; i++)
            {
                if (i == focus || !lastLive[i]) continue;
                var to = lastRects[i];
                Vector2 d = to.center - from.center;
                float primary = dx != 0 ? d.x * dx : d.y * dy;
                if (primary <= 2f) continue;
                float edge = dx > 0 ? to.xMin - from.xMax : dx < 0 ? from.xMin - to.xMax : dy > 0 ? to.yMin - from.yMax : from.yMin - to.yMax;
                float gap = dx != 0 ? Gap(from.yMin, from.yMax, to.yMin, to.yMax) : Gap(from.xMin, from.xMax, to.xMin, to.xMax);
                float score = Mathf.Max(edge, 0f) + gap * 3f + Mathf.Abs(dx != 0 ? d.y : d.x) * 0.15f;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0)
            {
                focus = best;
                focusRect = lastRects[best];
                UiSound.Navigate();
            }
        }

        /// <summary>Press the focused widget (the first press after using the mouse only reveals the focus).</summary>
        public static void Activate()
        {
            if (!NavMode || focus < 0 || focus >= lastLive.Count || !lastLive[focus])
            {
                Navigate(0, 0);
                return;
            }
            activate = focus;
        }

        static bool Hover(Rect r) => Interactive && !NavMode && r.Contains(Event.current.mousePosition);

        static bool HitId(Rect r, bool enabled, out bool hot, out bool focused)
        {
            int id = Register(r, enabled);
            bool on = enabled && Interactive;
            bool hover = on && Hover(r);
            if (hover && Event.current.type == EventType.Repaint)
            {
                focus = id;
                focusRect = rects[id];
            }
            focused = on && NavMode && id == focus;
            hot = hover || focused;
            if (!on)
            {
                // A click on a disabled widget: the error sound (the click itself does nothing).
                var e = Event.current;
                if (Interactive && !enabled && e.type == EventType.MouseDown && r.Contains(e.mousePosition)) UiSound.Error();
                return false;
            }
            bool click = GUI.Button(r, GUIContent.none, GUIStyle.none);
            if (activate == id)
            {
                activate = -1;
                click = true;
            }
            if (click)
            {
                focus = id;
                UiSound.Confirm();   // a back / lock-in / error request from the caller this frame outranks it
            }
            return click;
        }

        /// <summary>
        /// Interactive area for custom widgets: hot = hovered by the mouse or holding keyboard / gamepad focus.
        /// Returns true when clicked or activated.
        /// </summary>
        public static bool Hit(Rect r, out bool hot, bool enabled = true) => HitId(r, enabled, out hot, out _);

        /// <summary>True while a hot widget is being held down (mouse button or confirm), for press-in animation.</summary>
        public static bool Pressing(bool hot)
        {
            if (!hot) return false;
            if (NavMode) return MenuInput.ConfirmHeld;
            return Mouse.current != null && Mouse.current.leftButton.isPressed;
        }

        // ---------------------------------------------------------------- widgets

        /// <summary>Slanted arcade button. Default navy, hover tints to the accent, selected fills it, pressed compresses.</summary>
        public static bool Button(Rect r, string label, Color c, int size = 20, bool enabled = true, bool selected = false)
        {
            bool click = HitId(r, enabled, out bool hot, out bool focused);
            bool press = Pressing(hot);
            var b = press ? new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f) : r;
            float k = Mathf.Clamp(10f / Mathf.Max(r.height, 1f), 0.08f, 0.3f);

            Color bg;
            if (!enabled) bg = Disabled;
            else if (selected) bg = hot ? Color.Lerp(c, Color.white, 0.12f) : c;
            else bg = hot ? Color.Lerp(Steel, c, 0.45f) : ButtonFill;
            bg.a = 1f;

            PushSkew(k, b.center.y);
            if (enabled && !press) Rect(new Rect(b.x + 3f, b.y + 3f, b.width, b.height), new Color(0f, 0f, 0f, 0.45f));
            Rect(b, bg);
            if (enabled && !selected) Rect(new Rect(b.x, b.y, 4f, b.height), hot ? Paper : c);
            if (selected) Rect(new Rect(b.x, b.yMax - 3f, b.width, 3f), Color.Lerp(c, Color.black, 0.35f));
            Frame(b, !enabled ? new Color(Muted.r, Muted.g, Muted.b, 0.25f) : hot ? Paper : new Color(c.r, c.g, c.b, selected ? 1f : 0.5f), hot ? 2f : 1f);
            if (focused)
            {
                float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 8f);
                Frame(new Rect(b.x - 4f, b.y - 4f, b.width + 8f, b.height + 8f), new Color(Gold.r, Gold.g, Gold.b, pulse), 2f);
            }
            Pop();

            float lum = 0.299f * bg.r + 0.587f * bg.g + 0.114f * bg.b;
            bool darkText = lum > 0.6f;
            Text(b, label, size, !enabled ? Muted * 0.8f : (darkText ? Ink : Paper), TextAnchor.MiddleCenter, true, !darkText);
            return click;
        }

        // ---- segmented control (TP-6 format picker): one focusable row that owns left / right while focused.
        static bool claimLR, lastClaimLR;
        static int segStep;

        /// <summary>
        /// The focused widget is a <see cref="Segmented"/> row: menu navigation should send left / right (and LB / RB)
        /// to <see cref="StepFocused"/> instead of moving focus.
        /// </summary>
        public static bool FocusClaimsHorizontal => NavMode && lastClaimLR;

        /// <summary>Step the focused <see cref="Segmented"/> row by dir (-1 / +1) on its next draw.</summary>
        public static void StepFocused(int dir) => segStep = dir;

        /// <summary>
        /// Invisible focusable row over a custom widget (TP-6: the stage carousel). While focused it owns left / right
        /// and LB / RB like <see cref="Segmented"/>: <paramref name="step"/> is -1 / +1 for this draw. Draw it after
        /// the widget's own mouse hit areas so they keep their clicks. Returns true when activated (confirm / click).
        /// </summary>
        /// <example><code>if (Gui.FocusRow(r, out bool f, out int d)) Gui.FocusPreferred(); if (d != 0) StageStep(d);</code></example>
        public static bool FocusRow(Rect r, out bool focused, out int step, bool enabled = true)
        {
            step = 0;
            bool click = HitId(r, enabled, out _, out focused);
            if (focused)
            {
                claimLR = true;
                if (segStep != 0) { step = segStep; segStep = 0; }
                float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 8f);
                Frame(new Rect(r.x - 4f, r.y - 4f, r.width + 8f, r.height + 8f), new Color(Gold.r, Gold.g, Gold.b, pulse), 2f);
            }
            return click;
        }

        /// <summary>Move keyboard / gamepad focus to this screen's preferred widget (the one after <see cref="PreferNext"/>).</summary>
        public static void FocusPreferred()
        {
            int f = DefaultFocus();
            if (f < 0) return;
            NavMode = true;
            focus = f;
            focusRect = lastRects[f];
        }

        /// <summary>
        /// Segmented control: one focusable row of <paramref name="labels"/>, <paramref name="index"/> filled with
        /// <paramref name="c"/>. Mouse: click a segment to pick it. Keyboard / gamepad: focus the row, left / right or
        /// LB / RB cycle (wrapping), confirm sets <paramref name="confirmed"/>. Returns the (possibly new) index.
        /// </summary>
        /// <example><code>i = Gui.Segmented(r, i, TeamPlaySetup.CycleLabels, Gui.P2, out bool ok, !busy);</code></example>
        public static int Segmented(Rect r, int index, string[] labels, Color c, out bool confirmed, bool enabled = true)
        {
            confirmed = false;
            int n = labels.Length;
            bool click = HitId(r, enabled, out bool hot, out bool focused);
            if (focused)
            {
                claimLR = true;
                if (segStep != 0)
                {
                    index = ((index + segStep) % n + n) % n;
                    segStep = 0;
                    UiSound.Navigate();
                }
            }
            if (click)
            {
                if (NavMode) confirmed = true;
                else
                {
                    float t = (Event.current.mousePosition.x - r.x) / Mathf.Max(r.width, 1f);
                    index = Mathf.Clamp((int)(t * n), 0, n - 1);
                }
            }
            float k = Mathf.Clamp(10f / Mathf.Max(r.height, 1f), 0.08f, 0.3f);
            float sw = r.width / n;
            PushSkew(k, r.center.y);
            if (enabled) Rect(new Rect(r.x + 3f, r.y + 3f, r.width, r.height), new Color(0f, 0f, 0f, 0.45f));
            Rect(r, enabled ? ButtonFill : Disabled);
            for (int i = 0; i < n; i++)
            {
                var s = new Rect(r.x + i * sw, r.y, sw, r.height);
                bool sel = i == index;
                if (sel) Rect(new Rect(s.x + 2f, s.y + 2f, s.width - 4f, s.height - 4f), enabled ? c : Steel);
                if (i > 0) Rect(new Rect(s.x, s.y + 6f, 1f, s.height - 12f), new Color(Muted.r, Muted.g, Muted.b, 0.35f));
                Text(s, labels[i], 16, !enabled ? Muted * 0.8f : sel ? Paper : Muted, TextAnchor.MiddleCenter, true, sel);
            }
            Frame(r, !enabled ? new Color(Muted.r, Muted.g, Muted.b, 0.25f) : hot ? Paper : new Color(c.r, c.g, c.b, 0.6f), hot ? 2f : 1f);
            if (focused)
            {
                float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 8f);
                Frame(new Rect(r.x - 4f, r.y - 4f, r.width + 8f, r.height + 8f), new Color(Gold.r, Gold.g, Gold.b, pulse), 2f);
                Text(new Rect(r.x, r.y, 22f, r.height), "<", 16, Gold);
                Text(new Rect(r.xMax - 22f, r.y, 22f, r.height), ">", 16, Gold);
            }
            Pop();
            return index;
        }

        public static void Bar(Rect r, float t, Color fill, Color back, float radius = 0f)
        {
            Rect(r, back);
            if (t > 0.001f) Rect(new Rect(r.x, r.y, r.width * Mathf.Clamp01(t), r.height), fill);
        }

        /// <summary>Segmented, slanted stat meter.</summary>
        public static void Meter(Rect r, float t, Color fill, int segments = 10)
        {
            float gap = 3f;
            float sw = (r.width - gap * (segments - 1)) / segments;
            float filled = Mathf.Clamp01(t) * segments;
            for (int i = 0; i < segments; i++)
            {
                var s = new Rect(r.x + i * (sw + gap), r.y, sw, r.height);
                float f = Mathf.Clamp01(filled - i);
                Skew(s, new Color(1f, 1f, 1f, 0.08f), 0.5f);
                if (f > 0f) Skew(new Rect(s.x, s.y, sw * f, s.height), fill, 0.5f);
            }
        }

        public static string Key(string keyboard, string gamepad) => MenuInput.UsingGamepad ? gamepad : keyboard;

        static Color KeyColor(string key)
        {
            if (!MenuInput.UsingGamepad) return Paper;
            switch (key)
            {
                case "A": return Green;
                case "B": return P1;
                case "X": return P2;
                case "Y": return Gold;
                default: return Paper;
            }
        }

        public static float KeyWidth(string key, float h) => Mathf.Max(h, Measure(key, Mathf.RoundToInt(h * 0.62f)) + 10f);

        /// <summary>Keyboard / controller prompt glyph such as [ENTER] or (A). Returns its width.</summary>
        public static float KeyCap(float x, float y, string key, float h = 20f)
        {
            float w = KeyWidth(key, h);
            var r = new Rect(x, y, w, h);
            Skew(r, KeyColor(key), 0.18f);
            Text(r, key, Mathf.RoundToInt(h * 0.62f), Ink, TextAnchor.MiddleCenter, true, false);
            return w;
        }

        /// <summary>Bottom prompt strip: a breadcrumb on the left and (key, action) pairs right-aligned.</summary>
        public static void HintBar(string left, params string[] pairs)
        {
            var bar = new Rect(0f, H - 30f, W, 30f);
            Rect(bar, new Color(Ink.r, Ink.g, Ink.b, 0.94f));
            Rect(new Rect(0f, bar.y, W, 2f), Steel);
            Skew(new Rect(-10f, bar.y, 190f, 3f), P1, 1f);
            Skew(new Rect(W - 180f, bar.y, 190f, 3f), P2, 1f);
            if (!string.IsNullOrEmpty(left)) Text(new Rect(20f, bar.y, W * 0.5f, 30f), left, 12, Muted, TextAnchor.MiddleLeft, true, false);
            float x = W - 20f;
            for (int i = pairs.Length - 2; i >= 0; i -= 2)
            {
                string key = pairs[i], act = pairs[i + 1];
                float tw = Measure(act, 12);
                x -= tw;
                Text(new Rect(x, bar.y, tw + 6f, 30f), act, 12, Paper, TextAnchor.MiddleLeft, true, false);
                x -= 6f + KeyWidth(key, 18f);
                KeyCap(x, bar.y + 6f, key, 18f);
                x -= 22f;
            }
        }

        public static void Line(Vector2 a, Vector2 b, Color c, float width)
        {
            var m = GUI.matrix;
            float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            float len = Vector2.Distance(a, b);
            GUIUtility.RotateAroundPivot(angle, a * Scale);
            Rect(new Rect(a.x, a.y - width * 0.5f, len, width), c, width * 0.5f);
            GUI.matrix = m;
        }

        /// <summary>Chibi profile picture drawn from class + cosmetic skin.</summary>
        public static void Pfp(Rect r, ClassDef def, Skin skin)
        {
            Circle(r, new Color(0.06f, 0.1f, 0.18f));
            float s = r.width;
            var face = new Rect(r.x + s * 0.12f, r.y + s * 0.18f, s * 0.76f, s * 0.72f);
            Circle(face, new Color(1f, 0.88f, 0.78f));
            Circle(new Rect(r.x + s * 0.1f, r.y + s * 0.08f, s * 0.8f, s * 0.46f), skin.body);
            Circle(new Rect(r.x + s * 0.32f, r.y + s * 0.52f, s * 0.1f, s * 0.14f), new Color(0.12f, 0.1f, 0.15f));
            Circle(new Rect(r.x + s * 0.58f, r.y + s * 0.52f, s * 0.1f, s * 0.14f), new Color(0.12f, 0.1f, 0.15f));
            Glow(new Rect(r.x + s * 0.18f, r.y + s * 0.66f, s * 0.18f, s * 0.1f), new Color(1f, 0.45f, 0.5f, 0.7f));
            Glow(new Rect(r.x + s * 0.64f, r.y + s * 0.66f, s * 0.18f, s * 0.1f), new Color(1f, 0.45f, 0.5f, 0.7f));
            if (skin.hat == Hat.Halo) Frame(new Rect(r.x + s * 0.25f, r.y - s * 0.04f, s * 0.5f, s * 0.14f), Gold, 3f, s * 0.07f);
            if (skin.hat == Hat.Horns)
            {
                Rect(new Rect(r.x + s * 0.2f, r.y, s * 0.1f, s * 0.2f), skin.accent, 3f);
                Rect(new Rect(r.x + s * 0.7f, r.y, s * 0.1f, s * 0.2f), skin.accent, 3f);
            }
            if (skin.hat == Hat.Bandana) Rect(new Rect(r.x + s * 0.1f, r.y + s * 0.34f, s * 0.8f, s * 0.08f), skin.accent);
            if (skin.hat == Hat.Cat)
            {
                Rect(new Rect(r.x + s * 0.14f, r.y + s * 0.02f, s * 0.18f, s * 0.18f), skin.body, 3f);
                Rect(new Rect(r.x + s * 0.68f, r.y + s * 0.02f, s * 0.18f, s * 0.18f), skin.body, 3f);
            }
            Frame(r, def.color, 3f, s * 0.5f);
        }

        /// <summary>Camera feed panel with tracked skeleton, posture and telemetry readouts (GDD 5).</summary>
        static readonly string[] PostureText = new string[202];

        public static void CvPanel(Rect r, GestureTracker tracker, Color stateColor, bool showFeed, string title)
        {
            Rect(r, new Color(0.02f, 0.03f, 0.06f, 0.9f));
            var feed = new Rect(r.x + 4f, r.y + 22f, r.width - 8f, r.height - 44f);
            if (showFeed && WebcamFeed.Running)
                GUI.DrawTextureWithTexCoords(feed, WebcamFeed.Texture, new Rect(1f, 0f, -1f, 1f));
            else
            {
                Rect(feed, new Color(0.05f, 0.09f, 0.16f));
                for (float y = feed.y; y < feed.yMax; y += 6f) Rect(new Rect(feed.x, y, feed.width, 1f), new Color(0.3f, 0.6f, 1f, 0.05f));
            }

            if (tracker != null && tracker.Source.HasPose) Skeleton(feed, tracker.Source.Current, new Color(0.3f, 1f, 0.6f), 3f);

            Frame(r, stateColor, 3f);
            Rect(new Rect(r.x, r.y, r.width, 20f), new Color(stateColor.r, stateColor.g, stateColor.b, 0.25f));
            Text(new Rect(r.x + 8f, r.y + 1f, r.width - 16f, 20f), title, 12, Paper, TextAnchor.MiddleLeft);
            if (tracker == null) return;
            var g = tracker.Stable == Gesture.None ? "-" : ClassDef.GestureName(tracker.Stable);
            Text(new Rect(r.x + 8f, r.y + 1f, r.width - 16f, 20f), g, 12, Accent2, TextAnchor.MiddleRight);
            float posture = tracker.Posture;
            var pc = posture >= 0.6f ? Green : Yellow;
            int pp = Mathf.Clamp(Mathf.RoundToInt(posture * 100f), 0, 100);
            int pk = pp * 2 + (posture < 0.6f ? 1 : 0);
            string pt = PostureText[pk] ?? (PostureText[pk] = "Posture " + pp + "%" + (posture < 0.6f ? " - sit up!" : ""));   // cached (perf audit P4)
            Text(new Rect(r.x + 8f, r.yMax - 21f, r.width - 16f, 20f), pt, 11, pc, TextAnchor.MiddleLeft);
            Text(new Rect(r.x + 8f, r.yMax - 21f, r.width - 16f, 20f), tracker.TelemetryValid ? "Telemetry OK" : tracker.Validator.Reason, 11, tracker.TelemetryValid ? Muted : Red, TextAnchor.MiddleRight);
        }

        public static void Skeleton(Rect area, PoseFrame f, Color c, float width)
        {
            Vector2 P(int i)
            {
                var p = f.p[i];
                return new Vector2(area.x + p.x * area.width, area.y + (1f - p.y) * area.height);
            }
            for (int b = 0; b < PoseFrame.Bones.GetLength(0); b++)
                Line(P(PoseFrame.Bones[b, 0]), P(PoseFrame.Bones[b, 1]), c, width);
            var nose = P(PoseFrame.Nose);
            float hr = area.height * 0.09f;
            Frame(new Rect(nose.x - hr, nose.y - hr, hr * 2f, hr * 2f), c, width, hr);
            for (int i = 1; i < PoseFrame.Count; i++)
            {
                var p = P(i);
                float jr = width * (1.4f - Mathf.Clamp(f.p[i].z * 4f, -1f, 0.5f));
                Circle(new Rect(p.x - jr, p.y - jr, jr * 2f, jr * 2f), Color.white);
            }
        }
    }
}
