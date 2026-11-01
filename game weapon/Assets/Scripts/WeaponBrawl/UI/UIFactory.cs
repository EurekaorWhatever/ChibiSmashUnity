using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WeaponBrawl
{
    /// <summary>Helpers to build uGUI from code (no prefabs needed).</summary>
    public static class UIFactory
    {
        static Font font;

        public static Font Font
        {
            get
            {
                if (font == null)
                {
#if UNITY_2022_2_OR_NEWER
                    font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
                    font = Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
                }
                return font;
            }
        }

        public static Canvas CreateCanvas(string name, int sortingOrder)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        public static RectTransform Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static RectTransform Place(this RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            return rt;
        }

        public static RectTransform Stretch(this RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
            return rt;
        }

        /// <summary>Simple image for textures such as splatters and brush strokes. Doesn't block clicks.</summary>
        public static Image Art(Transform parent, string name, Sprite sprite, Color color)
        {
            var img = Node(name, parent).gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Hard-angled panel. Decorative by default, so it doesn't block clicks.</summary>
        public static SkewPanel Skew(Transform parent, string name, Color color, float skew = 0f)
        {
            var panel = Node(name, parent).gameObject.AddComponent<SkewPanel>();
            panel.color = color;
            panel.Skew = skew;
            panel.raycastTarget = false;
            return panel;
        }

        /// <summary>Clickable hard-angled panel. Controller focus is handled by the screens themselves, so uGUI navigation is off.</summary>
        public static Button SkewButton(Transform parent, string name, Color color, float skew, UnityAction onClick)
        {
            var panel = Skew(parent, name, color, skew);
            panel.raycastTarget = true;
            var button = panel.gameObject.AddComponent<Button>();
            button.targetGraphic = panel;
            var nav = button.navigation;
            nav.mode = Navigation.Mode.None;
            button.navigation = nav;
            var cb = ColorBlock.defaultColorBlock;
            cb.highlightedColor = new Color(0.92f, 0.92f, 0.92f);
            cb.pressedColor = new Color(0.75f, 0.75f, 0.75f);
            cb.selectedColor = Color.white;
            cb.fadeDuration = 0.06f;
            button.colors = cb;
            if (onClick != null) button.onClick.AddListener(onClick);
            return button;
        }

        public static Text Label(Transform parent, string name, string content, int size, Color color,
            TextAnchor align = TextAnchor.MiddleCenter, FontStyle style = FontStyle.Normal)
        {
            var t = Node(name, parent).gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = content;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = style;
            t.supportRichText = true;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        public static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }
}
