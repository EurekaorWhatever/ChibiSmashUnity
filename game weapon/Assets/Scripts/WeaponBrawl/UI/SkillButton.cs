using System;
using UnityEngine;
using UnityEngine.UI;

namespace WeaponBrawl
{
    /// <summary>
    /// Parallelogram action button: cooldown shutter that drains downward, button-prompt badge and name label.
    /// Clicking/tapping it performs the action (works for mouse and touch).
    /// </summary>
    public class SkillButton : MonoBehaviour
    {
        SkewPanel face, mask, glow, badge;
        Text glyph, label, keyText;
        Button button;

        Func<bool> isReady;
        Func<float> cooldownFill;
        Func<string> centerText, keyLabel;
        Action onPress;
        Color color;
        bool locked, wasReady = true, pulseWhenReady, flashOnReady;
        float flash, punch;

        public static SkillButton Create(Transform parent, string name, Vector2 position, float size)
        {
            var rt = UIFactory.Node(name, parent).Place(new Vector2(1f, 0f), new Vector2(0.5f, 0.5f), position, new Vector2(size * 1.15f, size));
            var sb = rt.gameObject.AddComponent<SkillButton>();
            float skew = size * 0.2f;

            sb.glow = UIFactory.Skew(rt, "Glow", Color.white, skew);
            sb.glow.Hollow = true;
            sb.glow.BorderWidth = 7f;
            sb.glow.rectTransform.Stretch(-9f);

            sb.button = UIFactory.SkewButton(rt, "Face", Color.white, skew, sb.Press);
            sb.face = (SkewPanel)sb.button.targetGraphic;
            sb.face.rectTransform.Stretch();
            sb.face.BorderWidth = 4f;
            sb.face.BorderColor = UITheme.Navy;

            sb.mask = UIFactory.Skew(rt, "Cooldown", new Color(UITheme.Navy.r, UITheme.Navy.g, UITheme.Navy.b, 0.78f), skew);
            sb.mask.rectTransform.Stretch();
            sb.mask.FillMode = SkewPanel.Fill.FromBottom;
            sb.mask.FillAmount = 0f;

            sb.glyph = UIFactory.Label(rt, "Glyph", "", Mathf.RoundToInt(size * 0.3f), UITheme.Navy, TextAnchor.MiddleCenter, UITheme.Heading);
            sb.glyph.rectTransform.Stretch();

            sb.badge = UIFactory.Skew(rt, "KeyBadge", UITheme.Paper, 8f);
            sb.badge.BorderWidth = 3f;
            sb.badge.BorderColor = UITheme.Navy;
            sb.badge.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(skew * 0.5f + 4f, -2f), new Vector2(64f, 34f));
            sb.keyText = UIFactory.Label(sb.badge.transform, "Key", "", 19, UITheme.Navy, TextAnchor.MiddleCenter, FontStyle.Bold);
            sb.keyText.rectTransform.Stretch();

            sb.label = UIFactory.Label(rt, "Name", "", 20, Color.white, TextAnchor.UpperCenter, UITheme.Heading);
            sb.label.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(size + 80f, 50f));
            var shadow = sb.label.gameObject.AddComponent<Outline>();
            shadow.effectColor = UITheme.Navy;
            shadow.effectDistance = new Vector2(2f, -2f);
            return sb;
        }

        public void Bind(string title, Func<string> key, Color buttonColor, Func<bool> ready, Func<float> fill, Func<string> center,
            Action press, bool pulse = false, bool flashWhenReady = true)
        {
            locked = false;
            color = buttonColor;
            isReady = ready;
            cooldownFill = fill;
            centerText = center;
            keyLabel = key;
            onPress = press;
            pulseWhenReady = pulse;
            flashOnReady = flashWhenReady;
            label.text = title.ToUpperInvariant();
            badge.gameObject.SetActive(true);
            button.interactable = true;
        }

        public void SetLocked(string title)
        {
            locked = true;
            onPress = null;
            label.text = title.ToUpperInvariant();
            glyph.text = "-";
            badge.gameObject.SetActive(false);
            face.color = UITheme.NavySoft;
            mask.FillAmount = 0f;
            glow.color = Color.clear;
            button.interactable = false;
        }

        /// <summary>Visual feedback when the action fires (also triggered by keyboard and gamepad input).</summary>
        public void Flash() => punch = 1f;

        void Press() => onPress?.Invoke();

        void Update()
        {
            punch = Mathf.MoveTowards(punch, 0f, Time.unscaledDeltaTime * 6f);
            transform.localScale = Vector3.one * (1f - Mathf.Sin(punch * Mathf.PI) * 0.12f);
            if (locked || isReady == null) return;

            bool ready = isReady();
            mask.FillAmount = cooldownFill != null ? cooldownFill() : 0f;
            glyph.text = centerText != null ? centerText() : "";
            glyph.color = mask.FillAmount > 0.01f ? Color.white : UITheme.Navy;
            keyText.text = keyLabel != null ? keyLabel() : "";
            face.color = ready ? color : Color.Lerp(color, UITheme.NavySoft, 0.6f);

            if (ready && !wasReady && flashOnReady) flash = 1f; // just came off cooldown
            wasReady = ready;
            flash = Mathf.MoveTowards(flash, 0f, Time.unscaledDeltaTime * 2.5f);

            float pulse = ready && pulseWhenReady ? 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f) : 0f;
            glow.color = new Color(1f, 1f, 1f, Mathf.Max(flash, pulse * 0.9f));
            glow.BorderColor = color;
            glow.rectTransform.localScale = Vector3.one * (1f + flash * 0.1f + pulse * 0.03f);
        }
    }
}
