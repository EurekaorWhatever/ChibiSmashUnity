using System;
using UnityEngine;
using UnityEngine.UI;

namespace WeaponBrawl
{
    /// <summary>
    /// Round action button with a radial cooldown sweep, key badge and name label.
    /// Clicking/tapping it performs the action (works for mouse and touch).
    /// </summary>
    public class SkillButton : MonoBehaviour
    {
        static readonly Color LockedColor = new Color(0.25f, 0.26f, 0.3f);

        Image face, mask, ring, badge;
        Text glyph, label, keyText;
        Button button;

        Func<bool> isReady;
        Func<float> cooldownFill;
        Func<string> centerText;
        Action onPress;
        Color color;
        bool locked, wasReady = true, pulseWhenReady, flashOnReady;
        float flash, punch;

        public static SkillButton Create(Transform parent, string name, Vector2 position, float size)
        {
            var rt = UIFactory.Node(name, parent).Place(new Vector2(1f, 0f), new Vector2(0.5f, 0.5f), position, Vector2.one * size);
            var sb = rt.gameObject.AddComponent<SkillButton>();

            sb.ring = UIFactory.Panel(rt, "Ring", Color.clear, SpriteFactory.Ring);
            sb.ring.rectTransform.Stretch(-size * 0.12f);
            sb.ring.raycastTarget = false;

            sb.face = UIFactory.Panel(rt, "Face", Color.white, SpriteFactory.Circle);
            sb.face.rectTransform.Stretch();
            sb.button = sb.face.gameObject.AddComponent<Button>();
            sb.button.targetGraphic = sb.face;
            sb.button.onClick.AddListener(sb.Press);

            sb.mask = UIFactory.Panel(rt, "Cooldown", new Color(0f, 0f, 0f, 0.65f), SpriteFactory.Circle);
            sb.mask.rectTransform.Stretch();
            sb.mask.type = Image.Type.Filled;
            sb.mask.fillMethod = Image.FillMethod.Radial360;
            sb.mask.fillOrigin = (int)Image.Origin360.Top;
            sb.mask.fillClockwise = false;
            sb.mask.fillAmount = 0f;
            sb.mask.raycastTarget = false;

            sb.glyph = UIFactory.Label(rt, "Glyph", "", Mathf.RoundToInt(size * 0.26f), Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            sb.glyph.rectTransform.Stretch();

            sb.badge = UIFactory.Panel(rt, "KeyBadge", new Color(0.08f, 0.09f, 0.12f, 0.95f), SpriteFactory.Circle);
            sb.badge.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(size * 0.12f, -size * 0.12f), Vector2.one * 40f);
            sb.badge.raycastTarget = false;
            sb.keyText = UIFactory.Label(sb.badge.transform, "Key", "", 20, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            sb.keyText.rectTransform.Stretch();

            sb.label = UIFactory.Label(rt, "Name", "", 20, new Color(0.9f, 0.92f, 0.96f), TextAnchor.UpperCenter, FontStyle.Bold);
            sb.label.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -6f), new Vector2(size + 80f, 50f));
            return sb;
        }

        public void Bind(string title, KeyCode key, Color buttonColor, Func<bool> ready, Func<float> fill, Func<string> center,
            Action press, bool pulse = false, bool flashWhenReady = true)
        {
            locked = false;
            color = buttonColor;
            isReady = ready;
            cooldownFill = fill;
            centerText = center;
            onPress = press;
            pulseWhenReady = pulse;
            flashOnReady = flashWhenReady;
            label.text = title;
            keyText.text = KeyName(key);
            badge.gameObject.SetActive(true);
            button.interactable = true;
            UIFactory.SetColor(button, Color.white);
        }

        public void SetLocked(string title)
        {
            locked = true;
            onPress = null;
            label.text = title;
            glyph.text = "—";
            badge.gameObject.SetActive(false);
            face.color = LockedColor;
            mask.fillAmount = 0f;
            ring.color = Color.clear;
            button.interactable = false;
        }

        /// <summary>Visual feedback when the action fires (also triggered by keyboard input).</summary>
        public void Flash() => punch = 1f;

        void Press() => onPress?.Invoke();

        void Update()
        {
            punch = Mathf.MoveTowards(punch, 0f, Time.unscaledDeltaTime * 6f);
            transform.localScale = Vector3.one * (1f - Mathf.Sin(punch * Mathf.PI) * 0.12f);
            if (locked || isReady == null) return;

            bool ready = isReady();
            mask.fillAmount = cooldownFill != null ? Mathf.Clamp01(cooldownFill()) : 0f;
            glyph.text = centerText != null ? centerText() : "";
            face.color = ready ? color : Color.Lerp(color, LockedColor, 0.55f);

            if (ready && !wasReady && flashOnReady) flash = 1f; // just came off cooldown
            wasReady = ready;
            flash = Mathf.MoveTowards(flash, 0f, Time.unscaledDeltaTime * 2.5f);

            float pulse = ready && pulseWhenReady ? 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f) : 0f;
            float alpha = Mathf.Max(flash, pulse * 0.9f, ready ? 0.25f : 0f);
            ring.color = new Color(color.r, color.g, color.b, alpha);
            ring.rectTransform.localScale = Vector3.one * (1f + flash * 0.25f + pulse * 0.06f);
        }

        static string KeyName(KeyCode key)
        {
            string s = key.ToString();
            return s.StartsWith("Alpha") ? s.Substring(5) : s;
        }
    }
}
