using System.Collections.Generic;

namespace ChibiClash
{
    /// <summary>
    /// Thai mark placement for IMGUI text (user report 2026-11-27: tone marks collided with upper vowels, as in เริ่ม).
    /// Legacy IMGUI has no OpenType positioning, so the bundled Thai font (tools/fonts/build_thai_imgui.py) carries
    /// raised and left-shifted copies of the upper marks at Private Use code points, and this pass swaps them in:
    /// a tone mark after an upper vowel, or before sara am, is raised; upper vowels and tone marks over a tall consonant
    /// (ป ฝ ฟ ฬ) are shifted left. Pure and cached: the same input string returns the same output instance.
    /// <example><code>string drawn = ThaiShaper.Apply("เริ่มอาร์เคด");</code></example>
    /// </summary>
    public static class ThaiShaper
    {
        // Keep in sync with tools/fonts/build_thai_imgui.py PUA.
        const char ToneHigh = '', ToneLowLeft = '', ToneHighLeft = '', UpperLeft = '';

        static readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        const int CacheCap = 4096;

        /// <summary>Tone marks and thanthakhat (U+0E48..U+0E4C).</summary>
        public static bool IsTone(char c) => c >= '่' && c <= '์';

        /// <summary>Upper vowels and marks that sit where a tone mark would (U+0E31, U+0E34..U+0E37, U+0E47, U+0E4D).</summary>
        public static bool IsUpper(char c) => c == 'ั' || (c >= 'ิ' && c <= 'ื') || c == '็' || c == 'ํ';

        /// <summary>Consonants whose ascender reaches the mark zone.</summary>
        public static bool IsTall(char c) => c == 'ป' || c == 'ฝ' || c == 'ฟ' || c == 'ฬ';

        static bool IsBelow(char c) => c >= 'ุ' && c <= 'ฺ';

        static int UpperIndex(char c)
        {
            switch (c)
            {
                case 'ั': return 0;
                case 'ิ': return 1;
                case 'ี': return 2;
                case 'ึ': return 3;
                case 'ื': return 4;
                case '็': return 5;
                default: return 6;   // U+0E4D
            }
        }

        /// <summary>The string with Thai marks swapped for their positioned forms; s itself when nothing changes.</summary>
        public static string Apply(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (cache.TryGetValue(s, out var hit)) return hit;
            string r = Shape(s);
            if (cache.Count >= CacheCap) cache.Clear();
            cache[s] = r;
            return r;
        }

        /// <summary>Uncached shaping (tests).</summary>
        public static string Shape(string s)
        {
            char[] buf = null;
            char baseC = '\0';
            bool upperSeen = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                char o = c;
                if (IsUpper(c))
                {
                    if (IsTall(baseC)) o = (char)(UpperLeft + UpperIndex(c));
                    upperSeen = true;
                }
                else if (IsTone(c))
                {
                    bool high = upperSeen || (i + 1 < s.Length && s[i + 1] == 'ำ');
                    bool left = IsTall(baseC);
                    int k = c - '่';
                    if (high) o = (char)((left ? ToneHighLeft : ToneHigh) + k);
                    else if (left) o = (char)(ToneLowLeft + k);
                }
                else if (!IsBelow(c))
                {
                    baseC = c;            // a new base (consonant, vowel, space, Latin...) starts a new cluster
                    upperSeen = false;
                }
                if (o != c)
                {
                    if (buf == null) buf = s.ToCharArray();
                    buf[i] = o;
                }
            }
            return buf == null ? s : new string(buf);
        }
    }
}
