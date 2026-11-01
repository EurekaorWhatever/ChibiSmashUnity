using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>The "Vs." palette: hot coral for P1, electric cyan for P2, off-white paper, slate navy for readability.</summary>
    public static class UITheme
    {
        public static readonly Color Paper = new Color(0.96f, 0.95f, 0.91f);
        public static readonly Color PaperDim = new Color(0.86f, 0.85f, 0.81f);
        public static readonly Color Navy = new Color(0.08f, 0.11f, 0.2f);
        public static readonly Color NavySoft = new Color(0.3f, 0.34f, 0.44f);
        public static readonly Color P1 = new Color(1f, 0.29f, 0.25f);
        public static readonly Color P2 = new Color(0f, 0.76f, 1f);
        public static readonly Color Gold = new Color(1f, 0.78f, 0.2f);

        public const FontStyle Heading = FontStyle.BoldAndItalic;

        public static Color Player(int index) => index == 0 ? P1 : P2;

        /// <summary>Face-button colors for prompts; everything else is a neutral chip.</summary>
        public static Color Prompt(string label)
        {
            switch (label)
            {
                case "A": return new Color(0.3f, 0.75f, 0.3f);
                case "B": return new Color(0.9f, 0.27f, 0.25f);
                case "X": return new Color(0.2f, 0.5f, 0.95f);
                case "Y": return new Color(0.95f, 0.75f, 0.15f);
                default: return Paper;
            }
        }

        public static Color PromptText(string label) => label == "A" || label == "B" || label == "X" ? Color.white : Navy;
    }
}
