using UnityEngine;

namespace ChibiClash
{
    /// <summary>Arcade-style immediate-mode UI helpers on a 1280x720 virtual canvas.</summary>
    public static class Gui
    {
        public static float Scale { get; private set; } = 1f;
        public static float W { get; private set; } = 1280f;
        public static float H { get; private set; } = 720f;

        public static readonly Color Panel = new Color(0.08f, 0.07f, 0.14f, 0.95f);
        public static readonly Color PanelLight = new Color(0.16f, 0.14f, 0.26f, 0.98f);
        public static readonly Color Accent = new Color(1f, 0.38f, 0.62f);
        public static readonly Color Accent2 = new Color(0.3f, 0.85f, 1f);
        public static readonly Color Gold = new Color(1f, 0.82f, 0.25f);
        public static readonly Color Yellow = new Color(1f, 0.85f, 0.15f);
        public static readonly Color Green = new Color(0.3f, 1f, 0.45f);
        public static readonly Color Red = new Color(1f, 0.3f, 0.3f);
        public static readonly Color Muted = new Color(0.7f, 0.68f, 0.8f);

        static GUIStyle style;

        public static void Begin()
        {
            Scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            W = Screen.width / Scale;
            H = Screen.height / Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
        }

        public static Vector2 WorldToGui(Vector3 world)
        {
            var cam = Camera.main;
            if (cam == null) return Vector2.zero;
            var sp = cam.WorldToScreenPoint(world);
            return new Vector2(sp.x / Scale, (Screen.height - sp.y) / Scale);
        }

        public static void Rect(Rect r, Color c, float radius = 0f)
        {
            GUI.DrawTexture(r, Art.SolidTex, ScaleMode.StretchToFill, true, 0f, c, 0f, radius);
        }

        public static void Frame(Rect r, Color c, float width, float radius = 0f)
        {
            GUI.DrawTexture(r, Art.SolidTex, ScaleMode.StretchToFill, true, 0f, c, width, radius);
        }

        public static void Circle(Rect r, Color c)
        {
            GUI.DrawTexture(r, Art.CircleTex, ScaleMode.StretchToFill, true, 0f, c, 0f, 0f);
        }

        public static void Glow(Rect r, Color c)
        {
            GUI.DrawTexture(r, Art.SoftTex, ScaleMode.StretchToFill, true, 0f, c, 0f, 0f);
        }

        public static void Text(Rect r, string s, int size, Color c, TextAnchor anchor = TextAnchor.MiddleCenter, bool bold = true, bool shadow = true)
        {
            if (style == null) style = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, clipping = TextClipping.Overflow };
            style.fontSize = size;
            style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            style.alignment = anchor;
            if (shadow && !s.Contains("<color"))
            {
                SetTextColor(style, new Color(0f, 0f, 0f, c.a * 0.6f));
                GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), s, style);
            }
            SetTextColor(style, c);
            GUI.Label(r, s, style);
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

        public static bool Button(Rect r, string label, Color c, int size = 20, bool enabled = true, bool selected = false)
        {
            bool hover = enabled && r.Contains(Event.current.mousePosition);
            // Opaque fill so the hover glow can never wash out the label; text colour follows the fill brightness.
            Color bg;
            if (!enabled) bg = new Color(0.2f, 0.2f, 0.25f, 1f);
            else if (selected) bg = Color.Lerp(c, Color.white, hover ? 0.12f : 0f);
            else bg = Color.Lerp(PanelLight, c, hover ? 0.28f : 0.08f);
            bg.a = 1f;
            if (hover || selected) Glow(new Rect(r.x - 10f, r.y - 10f, r.width + 20f, r.height + 20f), new Color(c.r, c.g, c.b, 0.16f));
            Rect(r, bg, 10f);
            Frame(r, enabled ? (hover ? Color.Lerp(c, Color.white, 0.4f) : c) : Muted * 0.6f, hover ? 3f : 2f, 10f);
            float lum = 0.299f * bg.r + 0.587f * bg.g + 0.114f * bg.b;
            bool darkText = lum > 0.55f;
            Text(r, label, size, !enabled ? Muted : (darkText ? new Color(0.06f, 0.05f, 0.1f) : Color.white), TextAnchor.MiddleCenter, true, !darkText);
            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        public static void Bar(Rect r, float t, Color fill, Color back, float radius = 4f)
        {
            Rect(r, back, radius);
            if (t > 0.001f) Rect(new Rect(r.x, r.y, r.width * Mathf.Clamp01(t), r.height), fill, radius);
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
            Circle(r, new Color(0.1f, 0.08f, 0.16f));
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
        public static void CvPanel(Rect r, GestureTracker tracker, Color stateColor, bool showFeed, string title)
        {
            Rect(r, new Color(0.02f, 0.03f, 0.06f, 0.9f), 8f);
            var feed = new Rect(r.x + 4f, r.y + 22f, r.width - 8f, r.height - 44f);
            if (showFeed && WebcamFeed.Running)
                GUI.DrawTextureWithTexCoords(feed, WebcamFeed.Texture, new Rect(1f, 0f, -1f, 1f));
            else
            {
                Rect(feed, new Color(0.06f, 0.1f, 0.16f), 4f);
                for (float y = feed.y; y < feed.yMax; y += 6f) Rect(new Rect(feed.x, y, feed.width, 1f), new Color(0.3f, 0.8f, 1f, 0.05f));
            }

            if (tracker != null && tracker.Source.HasPose) Skeleton(feed, tracker.Source.Current, new Color(0.3f, 1f, 0.6f), 3f);

            Frame(r, stateColor, 3f, 8f);
            Text(new Rect(r.x + 8f, r.y + 2f, r.width - 16f, 20f), title, 12, Color.white, TextAnchor.MiddleLeft);
            if (tracker == null) return;
            var g = tracker.Stable == Gesture.None ? "-" : ClassDef.GestureName(tracker.Stable);
            Text(new Rect(r.x + 8f, r.y + 2f, r.width - 16f, 20f), g, 12, Accent2, TextAnchor.MiddleRight);
            float posture = tracker.Posture;
            var pc = posture >= 0.6f ? Green : Yellow;
            Text(new Rect(r.x + 8f, r.yMax - 21f, r.width - 16f, 20f), "Posture " + Mathf.RoundToInt(posture * 100f) + "%" + (posture < 0.6f ? " - sit up!" : ""), 11, pc, TextAnchor.MiddleLeft);
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
