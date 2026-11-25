using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Bundled Noto fonts per writing system (localization stage 3, docs/localization.md "Fonts"). Each script has its
    /// own Resources folder (Resources/Fonts/&lt;ISO 15924&gt;/) and is loaded with Resources.LoadAsync the first time a
    /// string of that script is drawn, so only the scripts on screen are decoded. Every script font falls back to Noto
    /// Sans (Latin / Greek / Cyrillic) through its importer settings, so digits and Latin words inside it render.
    /// CJK: the full font loads only while the UI language uses it; elsewhere (the language list) a tiny endonym subset
    /// ("HansNames" ...) is used. Fonts are rebuilt with tools/fonts/build_fonts.py.
    /// <example><code>Font f = FontBank.Get("Thai");   // null until the async load finishes</code></example>
    /// </summary>
    public static class FontBank
    {
        /// <summary>
        /// Script code -> Resources path (no extension). Greek and Cyrillic share Noto Sans with Latin. Only the scripts
        /// of the 16 supported languages are bundled (2026-11-25); any other script draws with the normal font.
        /// </summary>
        public static readonly Dictionary<string, string> Paths = new Dictionary<string, string>
        {
            { "Latn", "Fonts/Latn/NotoSans-Regular" },
            { "Grek", "Fonts/Latn/NotoSans-Regular" },
            { "Cyrl", "Fonts/Latn/NotoSans-Regular" },
            { "Thai", "Fonts/Thai/NotoSansThai-Regular" },
            { "Hans", "Fonts/Hans/NotoSansSC-Regular" },
            { "Jpan", "Fonts/Jpan/NotoSansJP-Regular" },
            { "Kore", "Fonts/Kore/NotoSansKR-Regular" },
            { "HansNames", "Fonts/HansNames/NotoSansSCNames-Regular" },
            { "JpanNames", "Fonts/JpanNames/NotoSansJPNames-Regular" },
            { "KoreNames", "Fonts/KoreNames/NotoSansKRNames-Regular" },
        };

        /// <summary>CJK scripts: large fonts, loaded in full only for the UI language.</summary>
        public static bool IsCjk(string script) => script == "Hans" || script == "Jpan" || script == "Kore";

        static readonly Dictionary<string, Font> loaded = new Dictionary<string, Font>();
        static readonly Dictionary<string, ResourceRequest> pending = new Dictionary<string, ResourceRequest>();
        static readonly HashSet<string> missing = new HashSet<string>();
        static string langScript;
        static int langVersion = -1;

        /// <summary>ISO 15924 script of the UI language (Loc.Code), or "Latn".</summary>
        public static string LanguageScript
        {
            get
            {
                if (langVersion != Loc.Version)
                {
                    var info = Loc.Find(Loc.Code);
                    langScript = info != null && !string.IsNullOrEmpty(info.script) ? info.script : "Latn";
                    langVersion = Loc.Version;
                }
                return langScript;
            }
        }

        /// <summary>
        /// The bundled font for script, or null while it loads (the caller draws with its normal font for that frame)
        /// or if the script has no bundled font. CJK scripts that are not the UI language's get the endonym subset.
        /// </summary>
        public static Font Get(string script)
        {
            if (string.IsNullOrEmpty(script)) return null;
            if (IsCjk(script) && script != LanguageScript)
            {
                var full = Peek(script);   // already loaded (the language was CJK earlier): keep using it
                return full != null ? full : Load(script + "Names");
            }
            return Load(script);
        }

        static Font Peek(string key) => loaded.TryGetValue(key, out var f) ? f : null;

        static Font Load(string key)
        {
            if (loaded.TryGetValue(key, out var f)) return f;
            if (missing.Contains(key) || !Paths.TryGetValue(key, out var path)) return null;
            if (!pending.TryGetValue(key, out var req))
            {
                req = Resources.LoadAsync<Font>(path);
                pending[key] = req;
            }
            if (!req.isDone) return null;
            pending.Remove(key);
            f = req.asset as Font;
            if (f == null)
            {
                missing.Add(key);
                Debug.LogWarning("ChibiClash: bundled font missing: Resources/" + path);
                return null;
            }
            loaded[key] = f;
            return f;
        }
    }

    /// <summary>
    /// Which writing system a UI string needs (localization stage 3). Pure: no Unity calls, tested by FontScriptTests.
    /// Latin, Greek and Cyrillic return null (the normal display / body fonts draw them).
    /// </summary>
    public static class TextScript
    {
        /// <summary>
        /// ISO 15924 code of the first non-Latin script in s (null for Latin / Greek / Cyrillic / symbols). Han without
        /// kana or Hangul follows the UI language (Japanese -> "Jpan", Korean -> "Kore", else "Hans"). extendedLatin:
        /// also report "Latn" for Latin / Cyrillic letters beyond the basic default font (Vietnamese, African and IPA
        /// letters; used on WebGL where no OS font can supply them).
        /// </summary>
        public static string Detect(string s, string languageScript, bool extendedLatin)
        {
            if (string.IsNullOrEmpty(s)) return null;
            bool han = false, ext = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x0180) continue;
                string sc = Of(c);
                if (sc == null)
                {
                    if (extendedLatin && IsExtendedLatin(c)) ext = true;
                    else if (c == 0xD802 && i + 1 < s.Length && s[i + 1] >= 0xDF00 && s[i + 1] <= 0xDF3F) return "Avst";
                    continue;
                }
                if (sc == "Han") { han = true; continue; }
                return sc;
            }
            if (han) return languageScript == "Jpan" || languageScript == "Kore" ? languageScript : "Hans";
            return ext ? "Latn" : null;
        }

        static bool IsExtendedLatin(char c) =>
            (c >= 0x0180 && c <= 0x036F) || (c >= 0x0460 && c <= 0x052F) || (c >= 0x1D00 && c <= 0x1EFF) ||
            (c >= 0x2C60 && c <= 0x2C7F) || (c >= 0xA720 && c <= 0xA7FF);

        /// <summary>Script of one UTF-16 unit ("Han" for shared CJK ideographs / punctuation), null for the base fonts.</summary>
        public static string Of(char c)
        {
            if (c < 0x0530) return null;
            if (c < 0x0590) return "Armn";
            if (c < 0x0600) return "Hebr";
            if (c < 0x0780) return "Arab";   // Arabic, Syriac (drawn with the Arabic font), Arabic Supplement
            if (c < 0x07C0) return "Thaa";
            if (c < 0x0860) return null;
            if (c < 0x0900) return "Arab";
            if (c < 0x0980) return "Deva";
            if (c < 0x0A00) return "Beng";
            if (c < 0x0A80) return "Guru";
            if (c < 0x0B00) return "Gujr";
            if (c < 0x0B80) return "Orya";
            if (c < 0x0C00) return "Taml";
            if (c < 0x0C80) return "Telu";
            if (c < 0x0D00) return "Knda";
            if (c < 0x0D80) return "Mlym";
            if (c < 0x0E00) return "Sinh";
            if (c < 0x0E80) return "Thai";
            if (c < 0x0F00) return "Laoo";
            if (c < 0x1000) return "Tibt";
            if (c < 0x10A0) return "Mymr";
            if (c < 0x1100) return "Geor";
            if (c < 0x1200) return "Kore";
            if (c < 0x13A0) return "Ethi";
            if (c >= 0x1400 && c < 0x1680) return "Cans";
            if (c >= 0x1780 && c < 0x1800) return "Khmr";
            if (c >= 0x18B0 && c < 0x1900) return "Cans";
            if (c >= 0x19E0 && c < 0x1A00) return "Khmr";
            if (c >= 0x1C90 && c < 0x1CC0) return "Geor";
            if (c >= 0x2D80 && c < 0x2DE0) return "Ethi";
            if (c >= 0x2E80 && c < 0x3040) return "Han";
            if (c >= 0x3040 && c < 0x3100) return "Jpan";
            if (c >= 0x3100 && c < 0x3130) return "Hans";
            if (c >= 0x3130 && c < 0x3190) return "Kore";
            if (c >= 0x31F0 && c < 0x3200) return "Jpan";
            if (c >= 0x3400 && c < 0xA000) return "Han";
            if (c >= 0xA000 && c < 0xA4D0) return "Yiii";
            if (c >= 0xAB00 && c < 0xAB30) return "Ethi";
            if (c >= 0xAC00 && c < 0xD7B0) return "Kore";
            if (c >= 0xF900 && c < 0xFB00) return "Han";
            if (c >= 0xFB1D && c < 0xFB50) return "Hebr";
            if (c >= 0xFB50 && c < 0xFE00) return "Arab";
            if (c >= 0xFE70 && c < 0xFF00) return "Arab";
            if (c >= 0xFF00 && c < 0xFFF0) return "Han";
            return null;
        }

        /// <summary>
        /// Scripts whose letters must be reordered or stacked by an OpenType shaper (conjuncts, pre-base vowels, stacks).
        /// IMGUI cannot shape them, so Gui draws them through <see cref="ShapedText"/> (UI Toolkit, HarfBuzz).
        /// </summary>
        public static bool NeedsShaper(string script)
        {
            switch (script)
            {
                case "Deva": case "Beng": case "Guru": case "Gujr": case "Orya": case "Taml": case "Telu":
                case "Knda": case "Mlym": case "Sinh": case "Khmr": case "Mymr": case "Tibt": case "Laoo":
                    return true;
                default:
                    return false;
            }
        }
    }
}
