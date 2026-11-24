using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ChibiClash
{
    /// <summary>
    /// Right-to-left text for IMGUI, which has no shaping and no bidirectional layout (docs/localization.md "RTL").
    /// <see cref="Shape"/> turns Arabic-script letters into their contextual presentation forms (isolated, initial,
    /// medial, final) and joins lam + alef into the lam-alef ligatures. <see cref="Reorder"/> puts one line into visual
    /// (left-to-right drawing) order with a simplified Unicode Bidirectional Algorithm (UAX #9 rules W1-W7, N1-N2,
    /// I1-I2, L1-L2 and bracket mirroring; no explicit embeddings or isolates). Combining marks stay after their base
    /// letter, digits and Latin words keep their own order inside right-to-left text.
    /// Covers Arabic, Persian, Urdu, Pashto, Sindhi, Uyghur, Kurdish (Sorani), Kashmiri, Hebrew, Yiddish, Divehi
    /// (Thaana) and Avestan. Pure and allocation-light; tested by RtlTextTests.
    /// </summary>
    public static partial class RtlText
    {
        // Bidi classes (subset of UAX #9).
        enum B : byte { L, R, AL, EN, ES, ET, AN, CS, NSM, WS, ON }

        /// <summary>The text contains a right-to-left letter (Hebrew, Arabic, Thaana, Syriac, NKo, Avestan).</summary>
        public static bool HasRtl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x0590) continue;
                if (c <= 0x08FF || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF)) return true;
                if (c == 0xD802 && i + 1 < s.Length && s[i + 1] >= 0xDF00 && s[i + 1] <= 0xDF3F) return true;   // Avestan
            }
            return false;
        }

        /// <summary>Shape every line, then reorder it for display. rtlBase: the paragraph direction is right to left
        /// (the UI language is RTL); otherwise the first strong letter of each line decides.</summary>
        public static string Visual(string s, bool rtlBase)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('\n') < 0) return Reorder(Shape(s), rtlBase);
            var lines = s.Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = Reorder(Shape(lines[i]), rtlBase);
            return string.Join("\n", lines);
        }

        // ---------------------------------------------------------------- Arabic shaping

        static Dictionary<int, int> formRow;

        static char JoinType(char c)
        {
            if (c >= JoinFirst && c <= JoinLast) return JoinTypes[c - JoinFirst];
            if (c == '‍') return 'C';
            if (c == '‌') return 'U';
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark || cat == UnicodeCategory.Format ? 'T' : 'U';
        }

        static int Form(char c, int which)
        {
            if (formRow == null)
            {
                var d = new Dictionary<int, int>();
                for (int i = 0; i < FormTable.Length; i += 5) d[FormTable[i]] = i;
                formRow = d;
            }
            if (!formRow.TryGetValue(c, out int row)) return 0;
            return FormTable[row + 1 + which];
        }

        const int Iso = 0, Fin = 1, Ini = 2, Med = 3;

        /// <summary>
        /// Arabic contextual shaping in logical order: each letter becomes the presentation form for its joins, lam +
        /// alef (any of U+0622, U+0623, U+0625, U+0627) becomes one lam-alef ligature. Letters without a presentation
        /// form (some Pashto, Sindhi and extended letters) stay as they are. Text without Arabic letters is returned as is.
        /// </summary>
        public static string Shape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            bool any = false;
            foreach (char ch in s) if (ch >= 0x0600 && ch <= 0x08FF) { any = true; break; }
            if (!any) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                char t = JoinType(c);
                if (t != 'D' && t != 'R' && t != 'L')
                {
                    sb.Append(c);
                    continue;
                }
                int p = Prev(s, i), n = Next(s, i);
                bool joinPrev = p >= 0 && (JoinType(s[p]) == 'D' || JoinType(s[p]) == 'L' || JoinType(s[p]) == 'C') && (t == 'D' || t == 'R');

                // Lam-alef ligature (right-joining: it can join the letter before, never the one after).
                if (c == 'ل' && n >= 0 && n == i + 1)
                {
                    int lig = LamAlef(s[n]);
                    if (lig != 0)
                    {
                        sb.Append((char)(joinPrev ? lig + 1 : lig));
                        i = n;
                        continue;
                    }
                }

                bool joinNext = n >= 0 && (t == 'D' || t == 'L') &&
                                (JoinType(s[n]) == 'D' || JoinType(s[n]) == 'R' || JoinType(s[n]) == 'C');
                int f = joinPrev && joinNext ? Form(c, Med) : joinPrev ? Form(c, Fin) : joinNext ? Form(c, Ini) : Form(c, Iso);
                if (f == 0) f = Form(c, joinPrev ? Fin : Iso);
                sb.Append(f != 0 ? (char)f : c);
            }
            return sb.ToString();
        }

        static int LamAlef(char alef)
        {
            switch (alef)
            {
                case 'آ': return 0xFEF5;
                case 'أ': return 0xFEF7;
                case 'إ': return 0xFEF9;
                case 'ا': return 0xFEFB;
                default: return 0;
            }
        }

        static int Prev(string s, int i)
        {
            for (int j = i - 1; j >= 0; j--) if (JoinType(s[j]) != 'T') return j;
            return -1;
        }

        static int Next(string s, int i)
        {
            for (int j = i + 1; j < s.Length; j++) if (JoinType(s[j]) != 'T') return j;
            return -1;
        }

        // ---------------------------------------------------------------- bidi

        static B Class(string s, int i)
        {
            char c = s[i];
            if (c == ' ' || c == '\t') return B.WS;
            if (c >= '0' && c <= '9') return B.EN;
            if (c < 0x80)
            {
                if (char.IsLetter(c)) return B.L;
                if (c == '+' || c == '-') return B.ES;
                if (c == '#' || c == '$' || c == '%') return B.ET;
                if (c == ',' || c == '.' || c == ':' || c == '/') return B.CS;
                return B.ON;
            }
            if (c == 0x00A0) return B.CS;
            if (c >= 0x06F0 && c <= 0x06F9) return B.EN;   // Extended Arabic-Indic digits (Persian, Urdu)
            if (c >= 0x0660 && c <= 0x0669) return B.AN;   // Arabic-Indic digits
            if (c == 0x066B || c == 0x066C) return B.AN;
            if (c == 0x060C) return B.CS;                  // Arabic comma
            if (c == 0x00B0 || c == 0x00A2 || c == 0x00A3 || c == 0x00A5 || c == 0x20AC || c == 0x066A || c == 0x2030) return B.ET;
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark) return B.NSM;
            if (c >= 0x0590 && c <= 0x05FF) return B.R;
            if (c >= 0x07C0 && c <= 0x085F) return B.R;   // NKo, Samaritan, Mandaic
            if (c >= 0x0600 && c <= 0x07BF) return B.AL;  // Arabic, Syriac, Arabic Supplement, Thaana
            if (c >= 0x0860 && c <= 0x08FF) return B.AL;
            if (c >= 0xFB1D && c <= 0xFB4F) return B.R;
            if ((c >= 0xFB50 && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFE)) return B.AL;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length)
            {
                int cp = char.ConvertToUtf32(c, s[i + 1]);
                return cp >= 0x10800 && cp <= 0x10FFF ? B.R : B.L;   // Avestan and the other old RTL scripts
            }
            if (cat == UnicodeCategory.SpaceSeparator) return B.WS;
            if (char.IsLetter(c) || cat == UnicodeCategory.SpacingCombiningMark || char.IsDigit(c)) return B.L;
            return B.ON;
        }

        static readonly List<int> uStart = new List<int>(), uLen = new List<int>();
        static readonly List<B> uType = new List<B>();
        static readonly List<int> uLevel = new List<int>();
        static readonly object gate = new object();

        /// <summary>
        /// Visual order of one line (no '\n'). rtlBase forces a right-to-left paragraph; false lets the first strong
        /// letter decide (P2/P3). Text without right-to-left letters is returned unchanged when the paragraph is LTR.
        /// </summary>
        public static string Reorder(string line, bool rtlBase)
        {
            if (string.IsNullOrEmpty(line)) return line;
            if (!rtlBase && !HasRtl(line)) return line;
            lock (gate)
            {
                // Units: a base character, a surrogate pair, plus the combining marks after it (kept together).
                uStart.Clear(); uLen.Clear(); uType.Clear(); uLevel.Clear();
                for (int i = 0; i < line.Length;)
                {
                    int start = i;
                    B t = Class(line, i);
                    i += char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1;
                    if (t == B.NSM && uType.Count > 0) { uLen[uLen.Count - 1] += i - start; continue; }   // W1
                    while (i < line.Length && Class(line, i) == B.NSM) i++;
                    uStart.Add(start); uLen.Add(i - start); uType.Add(t == B.NSM ? B.ON : t);
                }
                int n = uType.Count;

                // P2/P3: paragraph level.
                int baseLevel = rtlBase ? 1 : 0;
                if (!rtlBase)
                    for (int k = 0; k < n; k++)
                    {
                        if (uType[k] == B.L) break;
                        if (uType[k] == B.R || uType[k] == B.AL) { baseLevel = 1; break; }
                    }
                B sos = baseLevel == 1 ? B.R : B.L;

                // W2: EN after AL is AN. W3: AL -> R.
                B lastStrong = sos;
                for (int k = 0; k < n; k++)
                {
                    B t = uType[k];
                    if (t == B.L || t == B.R || t == B.AL) lastStrong = t;
                    else if (t == B.EN && lastStrong == B.AL) uType[k] = B.AN;
                }
                for (int k = 0; k < n; k++) if (uType[k] == B.AL) uType[k] = B.R;
                // W4: one separator between two numbers of the same kind.
                for (int k = 1; k + 1 < n; k++)
                {
                    B a = uType[k - 1], b = uType[k + 1], t = uType[k];
                    if (t == B.ES && a == B.EN && b == B.EN) uType[k] = B.EN;
                    else if (t == B.CS && a == b && (a == B.EN || a == B.AN)) uType[k] = a;
                }
                // W5: terminators next to European numbers.
                for (int k = 0; k < n; k++)
                {
                    if (uType[k] != B.ET) continue;
                    int e = k;
                    while (e < n && uType[e] == B.ET) e++;
                    bool en = (k > 0 && uType[k - 1] == B.EN) || (e < n && uType[e] == B.EN);
                    if (en) for (int j = k; j < e; j++) uType[j] = B.EN;
                    k = e - 1;
                }
                // W6: leftover separators are neutral. W7: EN after L is L.
                for (int k = 0; k < n; k++) if (uType[k] == B.ES || uType[k] == B.ET || uType[k] == B.CS) uType[k] = B.ON;
                lastStrong = sos;
                for (int k = 0; k < n; k++)
                {
                    B t = uType[k];
                    if (t == B.L || t == B.R) lastStrong = t;
                    else if (t == B.EN && lastStrong == B.L) uType[k] = B.L;
                }
                // N1/N2: neutrals take the direction around them (numbers count as R), else the embedding direction.
                B e0 = baseLevel == 1 ? B.R : B.L;
                for (int k = 0; k < n; k++)
                {
                    if (uType[k] != B.WS && uType[k] != B.ON) continue;
                    int e = k;
                    while (e < n && (uType[e] == B.WS || uType[e] == B.ON)) e++;
                    B before = k == 0 ? sos : Strong(uType[k - 1]);
                    B after = e == n ? sos : Strong(uType[e]);
                    B r = before == after ? before : e0;
                    for (int j = k; j < e; j++) uType[j] = r;
                    k = e - 1;
                }
                // I1/I2: levels.
                int maxLevel = baseLevel;
                for (int k = 0; k < n; k++)
                {
                    B t = uType[k];
                    int lv = baseLevel;
                    if (baseLevel == 0) { if (t == B.R) lv = 1; else if (t == B.AN || t == B.EN) lv = 2; }
                    else if (t == B.L || t == B.EN || t == B.AN) lv = 2;
                    uLevel.Add(lv);
                    if (lv > maxLevel) maxLevel = lv;
                }
                // L1: trailing white space goes back to the paragraph level.
                for (int k = n - 1; k >= 0 && line[uStart[k]] == ' '; k--) uLevel[k] = baseLevel;

                // L2: reverse every run at or above each level, from the highest to the lowest odd level.
                var order = new int[n];
                for (int k = 0; k < n; k++) order[k] = k;
                for (int lv = maxLevel; lv >= 1; lv--)
                {
                    for (int k = 0; k < n; k++)
                    {
                        if (uLevel[order[k]] < lv) continue;
                        int e = k;
                        while (e < n && uLevel[order[e]] >= lv) e++;
                        System.Array.Reverse(order, k, e - k);
                        k = e;
                    }
                }

                var sb = new StringBuilder(line.Length);
                foreach (int k in order)
                {
                    int st = uStart[k], len = uLen[k];
                    if ((uLevel[k] & 1) == 1 && len == 1) sb.Append(Mirror(line[st]));
                    else if ((uLevel[k] & 1) == 1) { sb.Append(Mirror(line[st])); sb.Append(line, st + 1, len - 1); }
                    else sb.Append(line, st, len);
                }
                return sb.ToString();
            }
        }

        static B Strong(B t) => t == B.L ? B.L : B.R;   // R, EN, AN all count as R for N1

        /// <summary>The mirrored glyph drawn for a bracket in right-to-left text (L4).</summary>
        public static char Mirror(char c)
        {
            switch (c)
            {
                case '(': return ')';
                case ')': return '(';
                case '[': return ']';
                case ']': return '[';
                case '{': return '}';
                case '}': return '{';
                case '<': return '>';
                case '>': return '<';
                case '«': return '»';
                case '»': return '«';
                case '‹': return '›';
                case '›': return '‹';
                default: return c;
            }
        }
    }
}
