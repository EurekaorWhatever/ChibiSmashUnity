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

        /// <summary>Image; a rounded 9-sliced panel unless a sprite is given.</summary>
        public static Image Panel(Transform parent, string name, Color color, Sprite sprite = null)
        {
            var img = Node(name, parent).gameObject.AddComponent<Image>();
            img.sprite = sprite != null ? sprite : SpriteFactory.Rounded;
            img.type = sprite != null ? Image.Type.Simple : Image.Type.Sliced;
            img.color = color;
            return img;
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

        public static Button MakeButton(Transform parent, string name, string label, Color color, UnityAction onClick, int fontSize = 30)
        {
            var img = Panel(parent, name, Color.white);
            var button = img.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            SetColor(button, color);
            if (onClick != null) button.onClick.AddListener(onClick);
            if (!string.IsNullOrEmpty(label))
                Label(img.transform, "Label", label, fontSize, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform.Stretch();
            return button;
        }

        public static void SetColor(Button button, Color color)
        {
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = color;
            cb.highlightedColor = Color.Lerp(color, Color.white, 0.2f);
            cb.pressedColor = Color.Lerp(color, Color.black, 0.25f);
            cb.selectedColor = color;
            cb.disabledColor = new Color(color.r * 0.5f, color.g * 0.5f, color.b * 0.5f, 0.6f);
            cb.fadeDuration = 0.08f;
            button.colors = cb;
        }

        public static LayoutElement Size(Component c, float width = -1f, float height = -1f, float flexibleWidth = -1f, float flexibleHeight = -1f)
        {
            var le = c.GetComponent<LayoutElement>();
            if (le == null) le = c.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.preferredHeight = height;
            le.flexibleWidth = flexibleWidth;
            le.flexibleHeight = flexibleHeight;
            return le;
        }

        public static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }
}
