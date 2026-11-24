using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ChibiClash
{
    /// <summary>
    /// Complex-script text for the IMGUI screens (localization stage 3). Legacy IMGUI text has no OpenType shaping, so
    /// Indic, Sinhala, Khmer, Myanmar, Lao and Tibetan strings are laid out by UI Toolkit's Advanced Text Generator
    /// (HarfBuzz + ICU, the default UI Toolkit text engine since Unity 6.5) into one off-screen RenderTexture atlas, and
    /// Gui draws the atlas rectangle with GUI.DrawTextureWithTexCoords. Each (string, pixel size, bold, wrap width,
    /// rich-text colour) is laid out once and cached; a new string appears two frames after it is first requested
    /// (Gui draws the unshaped IMGUI text meanwhile). When the atlas is full it is cleared and refilled on demand.
    /// If UI Toolkit cannot be set up the class turns itself off and Gui keeps the IMGUI fallback.
    /// <example><code>if (!ShapedText.TryDraw(rect, s, font, 18, true, color, TextAnchor.MiddleLeft, false)) GUI.Label(rect, s, style);</code></example>
    /// Single-thread only (OnGUI).
    /// </summary>
    public sealed class ShapedText : MonoBehaviour
    {
        const int AtlasSize = 2048;
        const int Pad = 2;

        struct Key : System.IEquatable<Key>
        {
            public string text; public Font font; public int px; public bool bold; public int wrap; public Color32 rich;
            public bool Equals(Key o) => px == o.px && bold == o.bold && wrap == o.wrap && font == o.font &&
                                         rich.r == o.rich.r && rich.g == o.rich.g && rich.b == o.rich.b && text == o.text;
            public override bool Equals(object o) => o is Key k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = text.GetHashCode();
                    h = h * 31 + px; h = h * 31 + wrap; h = h * 31 + (bold ? 1 : 0);
                    h = h * 31 + (font != null ? font.GetInstanceID() : 0);
                    return h * 31 + rich.r + (rich.g << 8) + (rich.b << 16);
                }
            }
        }

        sealed class Entry
        {
            public Label label;
            public int addedFrame, placedFrame = -1;
            public RectInt slot;
        }

        static ShapedText inst;
        static bool failed;

        RenderTexture rt;
        PanelSettings panel;
        UIDocument doc;
        readonly Dictionary<Key, Entry> map = new Dictionary<Key, Entry>();
        readonly List<Entry> waiting = new List<Entry>();
        int shelfX, shelfY, shelfH;

        /// <summary>The atlas path works (false until set up, or after a failure).</summary>
        public static bool Available => !failed && Ensure();

        static bool Ensure()
        {
            if (inst != null) return true;
            if (failed) return false;
            try
            {
                var go = new GameObject("ChibiShapedText") { hideFlags = HideFlags.HideAndDontSave };
                if (Application.isPlaying) DontDestroyOnLoad(go);
                inst = go.AddComponent<ShapedText>();
                inst.Init();
                return true;
            }
            catch (System.Exception e)
            {
                failed = true;
                Debug.LogWarning("ChibiClash: complex-script text (UI Toolkit atlas) unavailable, using IMGUI text: " + e.Message);
                return false;
            }
        }

        void Init()
        {
            rt = new RenderTexture(AtlasSize, AtlasSize, 0, RenderTextureFormat.ARGB32) { name = "ChibiShapedTextAtlas", useMipMap = false, filterMode = FilterMode.Bilinear };
            rt.Create();
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.targetTexture = rt;
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            panel.scale = 1f;
            panel.clearColor = true;
            panel.colorClearValue = new Color(0f, 0f, 0f, 0f);
            doc = gameObject.AddComponent<UIDocument>();
            doc.panelSettings = panel;
        }

        void OnDestroy()
        {
            if (rt != null) rt.Release();
            if (panel != null) Destroy(panel);
            if (inst == this) inst = null;
        }

        void LateUpdate()
        {
            // Place the labels laid out last frame into the atlas (their size is known now).
            for (int i = waiting.Count - 1; i >= 0; i--)
            {
                var e = waiting[i];
                if (Time.frameCount <= e.addedFrame) continue;
                var l = e.label.layout;
                if (float.IsNaN(l.width) || l.width <= 0f) { if (Time.frameCount > e.addedFrame + 30) { failed = Fail("layout never resolved"); return; } continue; }
                int w = Mathf.CeilToInt(l.width) + Pad * 2, h = Mathf.CeilToInt(l.height) + Pad * 2;
                if (!Allocate(w, h, out var slot)) { Clear(); return; }
                e.slot = slot;
                e.label.style.left = slot.x + Pad;
                e.label.style.top = slot.y + Pad;
                e.label.style.visibility = Visibility.Visible;
                e.placedFrame = Time.frameCount;
                waiting.RemoveAt(i);
            }
        }

        static bool Fail(string why)
        {
            Debug.LogWarning("ChibiClash: complex-script text disabled (" + why + "), using IMGUI text");
            return true;
        }

        bool Allocate(int w, int h, out RectInt slot)
        {
            slot = default;
            if (w > AtlasSize || h > AtlasSize) return false;
            if (shelfX + w > AtlasSize) { shelfX = 0; shelfY += shelfH; shelfH = 0; }
            if (shelfY + h > AtlasSize) return false;
            slot = new RectInt(shelfX, shelfY, w, h);
            shelfX += w;
            shelfH = Mathf.Max(shelfH, h);
            return true;
        }

        void Clear()
        {
            doc.rootVisualElement.Clear();
            map.Clear();
            waiting.Clear();
            shelfX = shelfY = shelfH = 0;
        }

        Entry Request(Key k, int align)
        {
            if (map.TryGetValue(k, out var e)) return e;
            var root = doc.rootVisualElement;
            if (root == null) return null;
            var label = new Label(k.text) { enableRichText = true, pickingMode = PickingMode.Ignore };
            var st = label.style;
            st.position = Position.Absolute;
            st.left = 0; st.top = 0;
            st.visibility = Visibility.Hidden;
            st.fontSize = k.px;
            st.unityFontDefinition = FontDefinition.FromFont(k.font);
            st.unityFontStyleAndWeight = k.bold ? FontStyle.Bold : FontStyle.Normal;
            st.color = k.rich.a > 0 ? (Color)k.rich : Color.white;
            st.marginLeft = st.marginRight = st.marginTop = st.marginBottom = 0;
            st.paddingLeft = st.paddingRight = st.paddingTop = st.paddingBottom = 0;
            st.unityTextAlign = align == 0 ? TextAnchor.UpperLeft : align == 1 ? TextAnchor.UpperCenter : TextAnchor.UpperRight;
            if (k.wrap > 0) { st.whiteSpace = WhiteSpace.Normal; st.maxWidth = k.wrap; }
            else st.whiteSpace = WhiteSpace.NoWrap;
            root.Add(label);
            e = new Entry { label = label, addedFrame = Time.frameCount };
            map[k] = e;
            waiting.Add(e);
            return e;
        }

        static Key MakeKey(string s, Font font, int size, bool bold, bool wrap, float wrapWidth, Color c, out float px)
        {
            float scale = Mathf.Max(0.25f, Mathf.Round(Gui.Scale * 4f) / 4f);   // quantized: a resize does not relayout everything
            px = scale;
            var k = new Key
            {
                text = s, font = font, px = Mathf.Max(4, Mathf.RoundToInt(size * scale)), bold = bold,
                wrap = wrap ? Mathf.Max(1, Mathf.RoundToInt(wrapWidth * scale)) : 0,
                rich = s.IndexOf("<color", System.StringComparison.Ordinal) >= 0 ? (Color32)new Color(c.r, c.g, c.b, 1f) : new Color32(0, 0, 0, 0),
            };
            return k;
        }

        /// <summary>Size in Gui units of the shaped string, false while it is not laid out yet (or the path is off).</summary>
        public static bool TryMeasure(string s, Font font, int size, bool bold, out Vector2 measured)
        {
            measured = default;
            if (font == null || string.IsNullOrEmpty(s) || !Available) return false;
            var k = MakeKey(s, font, size, bold, false, 0f, Color.white, out float scale);
            var e = inst.Request(k, 0);
            if (e == null || e.placedFrame < 0) return false;
            measured = new Vector2((e.slot.width - Pad * 2) / scale, (e.slot.height - Pad * 2) / scale);
            return true;
        }

        /// <summary>
        /// Draw s shaped inside r (Gui units, current GUI.matrix), tinted c. False when the string is not ready yet or
        /// the path is off: the caller then draws it with IMGUI.
        /// </summary>
        public static bool TryDraw(Rect r, string s, Font font, int size, bool bold, Color c, TextAnchor anchor, bool wrap)
        {
            if (font == null || string.IsNullOrEmpty(s) || !Available) return false;
            int col = (int)anchor % 3;
            var k = MakeKey(s, font, size, bold, wrap, r.width, c, out float scale);
            var e = inst.Request(k, wrap ? col : 0);
            if (e == null || e.placedFrame < 0 || Time.frameCount <= e.placedFrame) return false;
            if (Event.current == null || Event.current.type != EventType.Repaint) return true;
            float w = (e.slot.width - Pad * 2) / scale, h = (e.slot.height - Pad * 2) / scale;
            int row = (int)anchor / 3;
            float x = col == 0 ? r.x : col == 1 ? r.x + (r.width - w) * 0.5f : r.xMax - w;
            float y = row == 0 ? r.y : row == 1 ? r.y + (r.height - h) * 0.5f : r.yMax - h;
            // UI Toolkit draws the panel top at the top of the texture; texture v runs bottom-up.
            var uv = new Rect((e.slot.x + Pad) / (float)AtlasSize, 1f - (e.slot.y + Pad + (h * scale)) / AtlasSize,
                              w * scale / AtlasSize, h * scale / AtlasSize);
            var old = GUI.color;
            GUI.color = k.rich.a > 0 ? new Color(1f, 1f, 1f, c.a) : c;
            GUI.DrawTextureWithTexCoords(new Rect(x, y, w, h), inst.rt, uv, true);
            GUI.color = old;
            return true;
        }
    }
}
