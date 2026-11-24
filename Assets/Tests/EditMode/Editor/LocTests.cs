using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Story type: Logic + Config/Data (localization stage 1, docs/localization.md). Evidence: automated EditMode tests.
    /// The table checks read the shipped files (Resources/Loc) on purpose: they are content validation, the same check
    /// a CI lint would run. The parser / fallback checks use inline JSON and touch no files.
    /// </summary>
    public class LocTests
    {
        static string LocDir => Path.Combine(Application.dataPath, "ChibiClash", "Resources", "Loc");
        static string ScriptsDir => Path.Combine(Application.dataPath, "ChibiClash", "Scripts");
        static readonly Regex Placeholder = new Regex(@"\{\d+\}");

        static Dictionary<string, string> Table(string code) => Loc.Parse(File.ReadAllText(Path.Combine(LocDir, code + ".json")));

        static IEnumerable<string> TranslatedTables()
        {
            foreach (var f in Directory.GetFiles(LocDir, "*.json"))
            {
                string code = Path.GetFileNameWithoutExtension(f);
                if (code != "en" && code != "languages") yield return code;
            }
        }

        static List<string> Sorted(MatchCollection m)
        {
            var l = new List<string>();
            foreach (Match x in m) l.Add(x.Value);
            l.Sort(System.StringComparer.Ordinal);
            return l;
        }

        [TearDown]
        public void ResetLanguage() => Loc.Use(Loc.Source);

        [Test]
        public void EveryKeyUsedInCode_ExistsInEnglish()
        {
            var en = Table("en");
            var prefixes = new HashSet<string>();
            foreach (var k in en.Keys) prefixes.Add(k.Substring(0, k.IndexOf('.')));
            // Loc.T("key" / LocCache("key", plus key literals kept in arrays ("nav.home") whose prefix is a Loc prefix.
            var call = new Regex(@"(?:Loc\.T|LocCache)\(\s*""([^""]+)""");
            var keyLiteral = new Regex(@"""([a-z0-9_]+(?:\.[a-z0-9_]+)+)""");
            var missing = new List<string>();
            foreach (var file in Directory.GetFiles(ScriptsDir, "*.cs", SearchOption.AllDirectories))
            {
                string src = File.ReadAllText(file);
                foreach (Match m in call.Matches(src))
                    if (!m.Groups[1].Value.EndsWith(".") && !en.ContainsKey(m.Groups[1].Value)) missing.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value);
                foreach (Match m in keyLiteral.Matches(src))
                {
                    string k = m.Groups[1].Value;
                    if (prefixes.Contains(k.Substring(0, k.IndexOf('.'))) && !en.ContainsKey(k) && !k.EndsWith("."))
                        missing.Add(Path.GetFileName(file) + ": " + k);
                }
            }
            Assert.IsEmpty(missing, "keys used in code but missing from en.json:\n" + string.Join("\n", missing));
        }

        [Test]
        public void ThaiTable_HasNoKeysOutsideEnglish()
        {
            var en = Table("en");
            var extra = new List<string>();
            foreach (var k in Table("th").Keys) if (!en.ContainsKey(k)) extra.Add(k);
            Assert.IsEmpty(extra, "th.json keys that en.json does not have:\n" + string.Join("\n", extra));
        }

        [Test]
        public void EveryTable_HasNoKeysOutsideEnglish()
        {
            var en = Table("en");
            var extra = new List<string>();
            foreach (var code in TranslatedTables())
                foreach (var k in Table(code).Keys)
                    if (!en.ContainsKey(k)) extra.Add(code + ": " + k);
            Assert.IsEmpty(extra, string.Join("\n", extra));
        }

        [Test]
        public void Placeholders_MatchEnglish_InEveryTable()
        {
            var en = Table("en");
            var bad = new List<string>();
            foreach (var code in TranslatedTables())
                foreach (var kv in Table(code))
                {
                    if (!en.TryGetValue(kv.Key, out var src)) continue;
                    var a = Sorted(Placeholder.Matches(src));
                    var b = Sorted(Placeholder.Matches(kv.Value));
                    if (string.Join(",", a) != string.Join(",", b)) bad.Add(code + ": " + kv.Key + " en=" + string.Join(",", a) + " " + code + "=" + string.Join(",", b));
                }
            Assert.IsEmpty(bad, string.Join("\n", bad));
        }

        [Test]
        public void LanguageList_HasUniqueTwoLetterCodes_AndEveryTableIsListed()
        {
            var langs = Loc.ParseLanguages(File.ReadAllText(Path.Combine(LocDir, "languages.json")));
            Assert.GreaterOrEqual(langs.Length, 180, "every ISO 639-1 language should be listed");
            var seen = new HashSet<string>();
            foreach (var l in langs)
            {
                Assert.IsTrue(Regex.IsMatch(l.code ?? "", "^[a-z]{2}$"), "bad code " + l.code);
                Assert.IsTrue(seen.Add(l.code), "duplicate code " + l.code);
                Assert.IsFalse(string.IsNullOrEmpty(l.name), l.code + " has no English name");
                Assert.IsFalse(string.IsNullOrEmpty(l.native), l.code + " has no native name");
                bool hasTable = l.code == "en" || File.Exists(Path.Combine(LocDir, l.code + ".json"));
                Assert.AreEqual(hasTable, l.translated, l.code + ": translated flag does not match the tables (run tools/loc/gen_languages.py)");
            }
            foreach (var code in TranslatedTables()) Assert.IsTrue(seen.Contains(code), code + ".json has no languages.json row");
        }

        [Test]
        public void Parser_ReadsEscapes_SkipsCommentKeys_RejectsDuplicates()
        {
            var d = Loc.Parse("{ \"// note\": \"x\", \"a.b\": \"line1\\nline2 \\\"q\\\" \\u0E44\" }");
            Assert.AreEqual(1, d.Count);
            Assert.AreEqual("line1\nline2 \"q\" \u0E44", d["a.b"]);
            Assert.Throws<System.FormatException>(() => Loc.Parse("{ \"a\": \"1\", \"a\": \"2\" }"));
            Assert.Throws<System.FormatException>(() => Loc.Parse("{ \"a\": 1 }"));
        }

        [Test]
        public void MissingTable_FallsBackToEnglish_AndUnknownKeyReturnsKey()
        {
            Loc.Use("zu");   // listed, no table yet
            Assert.AreEqual("zu", Loc.Code);
            Assert.AreEqual("BACK", Loc.T("common.back"));
            Assert.AreEqual("no.such.key", Loc.T("no.such.key"));
            Assert.AreEqual("ROUND 3", Loc.T("arcade.round", 3));
        }

        [Test]
        public void Thai_IsUsedWhenChosen_AndVersionChanges()
        {
            int v = Loc.Version;
            Loc.Use("th");
            Assert.Greater(Loc.Version, v);
            Assert.AreEqual(Table("th")["common.back"], Loc.T("common.back"));
            Assert.AreNotEqual("BACK", Loc.T("common.back"));
        }

        [Test]
        public void Search_MatchesEnglishOrNativeName()
        {
            var th = new LanguageInfo { code = "th", name = "Thai", native = "ไทย" };
            Assert.IsTrue(Loc.Matches(th, "tha"));
            Assert.IsTrue(Loc.Matches(th, "ไท"));
            Assert.IsTrue(Loc.Matches(th, "TH"));
            Assert.IsFalse(Loc.Matches(th, "japan"));
        }

        [Test]
        public void CultureNames_MapToIsoCodes()
        {
            Assert.AreEqual("th", Loc.FromCultureName("th-TH"));
            Assert.AreEqual("he", Loc.FromCultureName("iw-IL"));
            Assert.IsNull(Loc.FromCultureName(""));
            Assert.AreEqual("th", Loc.FromSystemLanguage(SystemLanguage.Thai));
        }
    }
}
