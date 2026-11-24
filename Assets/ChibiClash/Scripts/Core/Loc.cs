using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>One row of Resources/Loc/languages.json (every ISO 639-1 language).</summary>
    [Serializable]
    public class LanguageInfo
    {
        /// <summary>ISO 639-1 code, also the table file name ("th" -> Resources/Loc/th.json).</summary>
        public string code;
        /// <summary>English name ("Thai").</summary>
        public string name;
        /// <summary>Endonym, the name in the language itself ("ไทย").</summary>
        public string native;
        /// <summary>ISO 15924 script code of the usual writing system ("Thai", "Latn", "Arab").</summary>
        public string script;
        /// <summary>Written right to left.</summary>
        public bool rtl;
        /// <summary>A table ships for it (false: the game shows English text).</summary>
        public bool translated;
    }

    [Serializable]
    class LanguageList
    {
        public LanguageInfo[] languages;
    }

    /// <summary>
    /// Player-facing text lookup (docs/localization.md). <c>Loc.T("common.back")</c> returns the text of the chosen
    /// language, else English (Resources/Loc/en.json, the source), else the key itself (a missing English key is a bug the
    /// EditMode tests catch). Tables are flat JSON objects { "key": "text" } parsed once and cached; a lookup does not
    /// allocate. The language is per device (SaveData.language): nothing localized is ever sent over the network.
    /// </summary>
    public static class Loc
    {
        /// <summary>Source and fallback language.</summary>
        public const string Source = "en";
        /// <summary>Resources folder of the tables.</summary>
        public const string Folder = "Loc/";

        static Dictionary<string, string> source, current;
        static LanguageInfo[] languages;

        /// <summary>Code of the language in use ("en" until <see cref="Use"/> is called).</summary>
        public static string Code { get; private set; } = Source;

        /// <summary>Bumped on every language change; caches of formatted text compare it to know they are stale.</summary>
        public static int Version { get; private set; }

        /// <summary>The language in use is written right to left (layout mirroring is stage 3).</summary>
        public static bool Rtl { get; private set; }

        // ---------------------------------------------------------------- lookup

        /// <summary>The text for key in the current language, else English, else the key.</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (source == null) Use(Code);
            if (current != null && current.TryGetValue(key, out var s)) return s;
            if (source.TryGetValue(key, out s)) return s;
            return key;
        }

        /// <summary>T(key) with {0} filled (string.Format, invariant culture).</summary>
        public static string T(string key, object a0) => Format(T(key), a0, null, null, 1);
        /// <summary>T(key) with {0} and {1} filled.</summary>
        public static string T(string key, object a0, object a1) => Format(T(key), a0, a1, null, 2);
        /// <summary>T(key) with {0}, {1} and {2} filled.</summary>
        public static string T(string key, object a0, object a1, object a2) => Format(T(key), a0, a1, a2, 3);

        static string Format(string pattern, object a0, object a1, object a2, int n)
        {
            try
            {
                return n == 1 ? string.Format(CultureInfo.InvariantCulture, pattern, a0)
                     : n == 2 ? string.Format(CultureInfo.InvariantCulture, pattern, a0, a1)
                     : string.Format(CultureInfo.InvariantCulture, pattern, a0, a1, a2);
            }
            catch (FormatException)
            {
                return pattern;   // a broken translation never throws in OnGUI
            }
        }

        /// <summary>The English table has this key.</summary>
        public static bool Has(string key)
        {
            if (source == null) Use(Code);
            return source.ContainsKey(key);
        }

        // ---------------------------------------------------------------- language choice

        /// <summary>
        /// Switch to code (a languages.json code). A language without a table keeps its code (the choice is remembered)
        /// but every lookup falls back to English.
        /// </summary>
        public static void Use(string code)
        {
            if (string.IsNullOrEmpty(code)) code = Source;
            if (source == null) source = LoadTable(Source) ?? new Dictionary<string, string>();
            current = code == Source ? null : LoadTable(code);
            Code = code;
            var info = Find(code);
            Rtl = info != null && info.rtl;
            Version++;
        }

        static Dictionary<string, string> LoadTable(string code)
        {
            var asset = Resources.Load<TextAsset>(Folder + code);
            if (asset == null) return null;
            try { return Parse(asset.text); }
            catch (FormatException e)
            {
                Debug.LogWarning("ChibiClash: language table " + code + ".json is unreadable (" + e.Message + "), using English");
                return null;
            }
        }

        /// <summary>Every ISO 639-1 language, in file order (sorted by English name).</summary>
        public static LanguageInfo[] Languages
        {
            get
            {
                if (languages == null)
                {
                    var asset = Resources.Load<TextAsset>(Folder + "languages");
                    languages = ParseLanguages(asset != null ? asset.text : null);
                }
                return languages;
            }
        }

        /// <summary>Parse languages.json ({ "languages": [ ... ] }); never null.</summary>
        public static LanguageInfo[] ParseLanguages(string json)
        {
            if (string.IsNullOrEmpty(json)) return new[] { new LanguageInfo { code = Source, name = "English", native = "English", script = "Latn", translated = true } };
            var list = JsonUtility.FromJson<LanguageList>(json);
            return list != null && list.languages != null ? list.languages : new LanguageInfo[0];
        }

        /// <summary>The languages.json row for code, or null.</summary>
        public static LanguageInfo Find(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            foreach (var l in Languages) if (l.code == code) return l;
            return null;
        }

        /// <summary>The language matches a search typed by the player (English or native name, or the code).</summary>
        public static bool Matches(LanguageInfo l, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            query = query.Trim();
            if (query.Length == 0) return true;
            var cmp = CultureInfo.InvariantCulture.CompareInfo;
            const CompareOptions o = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            return cmp.IndexOf(l.name ?? "", query, o) >= 0 || cmp.IndexOf(l.native ?? "", query, o) >= 0 ||
                   string.Equals(l.code, query, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- device language

        static string capturedCulture;   // the OS culture name before NetworkRoomManager switches to invariant

        // SubsystemRegistration runs before the BeforeSceneLoad hook that makes the culture invariant. Without a domain
        // reload the culture is already invariant in a later Play session, so the first capture is kept.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void CaptureCulture()
        {
            string n = CultureInfo.CurrentUICulture.Name;
            if (string.IsNullOrEmpty(n)) n = CultureInfo.CurrentCulture.Name;
            if (!string.IsNullOrEmpty(n) && capturedCulture == null) capturedCulture = n;
        }

        /// <summary>The suggested language for this device: Application.systemLanguage, else the OS culture, else English.</summary>
        public static string DeviceLanguage()
        {
            string code = FromSystemLanguage(Application.systemLanguage);
            if (code == null && capturedCulture != null) code = FromCultureName(capturedCulture);
            return code != null && Find(code) != null ? code : Source;
        }

        /// <summary>"th-TH" -> "th" ("" or unknown -> null).</summary>
        public static string FromCultureName(string culture)
        {
            if (string.IsNullOrEmpty(culture)) return null;
            int dash = culture.IndexOfAny(new[] { '-', '_' });
            string two = (dash > 0 ? culture.Substring(0, dash) : culture).ToLowerInvariant();
            if (two == "iw") two = "he";
            if (two == "in") two = "id";
            if (two == "fil") two = "tl";
            return two.Length == 2 ? two : null;
        }

        /// <summary>Unity's SystemLanguage to an ISO 639-1 code (null for Unknown).</summary>
        public static string FromSystemLanguage(SystemLanguage l)
        {
            switch (l)
            {
                case SystemLanguage.Afrikaans: return "af";
                case SystemLanguage.Arabic: return "ar";
                case SystemLanguage.Basque: return "eu";
                case SystemLanguage.Belarusian: return "be";
                case SystemLanguage.Bulgarian: return "bg";
                case SystemLanguage.Catalan: return "ca";
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.ChineseTraditional: return "zh";
                case SystemLanguage.Czech: return "cs";
                case SystemLanguage.Danish: return "da";
                case SystemLanguage.Dutch: return "nl";
                case SystemLanguage.English: return "en";
                case SystemLanguage.Estonian: return "et";
                case SystemLanguage.Faroese: return "fo";
                case SystemLanguage.Finnish: return "fi";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.German: return "de";
                case SystemLanguage.Greek: return "el";
                case SystemLanguage.Hebrew: return "he";
                case SystemLanguage.Hungarian: return "hu";
                case SystemLanguage.Icelandic: return "is";
                case SystemLanguage.Indonesian: return "id";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.Latvian: return "lv";
                case SystemLanguage.Lithuanian: return "lt";
                case SystemLanguage.Norwegian: return "no";
                case SystemLanguage.Polish: return "pl";
                case SystemLanguage.Portuguese: return "pt";
                case SystemLanguage.Romanian: return "ro";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.SerboCroatian: return "sr";
                case SystemLanguage.Slovak: return "sk";
                case SystemLanguage.Slovenian: return "sl";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.Swedish: return "sv";
                case SystemLanguage.Thai: return "th";
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Ukrainian: return "uk";
                case SystemLanguage.Vietnamese: return "vi";
                case SystemLanguage.Hindi: return "hi";
                default: return null;
            }
        }

        // ---------------------------------------------------------------- table parser

        /// <summary>
        /// Parse a flat JSON object of string values ({ "a.b": "text", ... }). Throws FormatException on anything else.
        /// Keys starting with "//" are comments and are skipped.
        /// </summary>
        public static Dictionary<string, string> Parse(string json)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0;
            Ws(json, ref i);
            Expect(json, ref i, '{');
            Ws(json, ref i);
            if (Peek(json, i) == '}') return d;
            while (true)
            {
                Ws(json, ref i);
                string k = Str(json, ref i);
                Ws(json, ref i);
                Expect(json, ref i, ':');
                Ws(json, ref i);
                string v = Str(json, ref i);
                if (!k.StartsWith("//", StringComparison.Ordinal))
                {
                    if (d.ContainsKey(k)) throw new FormatException("duplicate key " + k);
                    d[k] = v;
                }
                Ws(json, ref i);
                char c = Peek(json, i);
                i++;
                if (c == '}') break;
                if (c != ',') throw new FormatException("expected , or } at " + (i - 1));
            }
            return d;
        }

        static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n' || s[i] == '﻿')) i++;
        }

        static void Expect(string s, ref int i, char c)
        {
            if (Peek(s, i) != c) throw new FormatException("expected " + c + " at " + i);
            i++;
        }

        static string Str(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("bad escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape \\" + e);
                }
            }
        }
    }

    /// <summary>
    /// A formatted Loc string cached until its argument or the language changes (HUD text drawn every frame allocates
    /// only when the value changes). One instance per call site.
    /// </summary>
    public sealed class LocCache
    {
        readonly string key;
        int version = -1;
        long a0 = long.MinValue, a1 = long.MinValue;
        string text;

        public LocCache(string key) { this.key = key; }

        /// <summary>T(key) (re-read only when the language changes).</summary>
        public string Get()
        {
            if (version != Loc.Version || text == null) { text = Loc.T(key); version = Loc.Version; }
            return text;
        }

        /// <summary>T(key, n) for an integer argument.</summary>
        public string Get(long n)
        {
            if (version != Loc.Version || n != a0 || text == null) { text = Loc.T(key, n); a0 = n; version = Loc.Version; }
            return text;
        }

        /// <summary>T(key, n, m) for two integer arguments.</summary>
        public string Get(long n, long m)
        {
            if (version != Loc.Version || n != a0 || m != a1 || text == null) { text = Loc.T(key, n, m); a0 = n; a1 = m; version = Loc.Version; }
            return text;
        }
    }
}
