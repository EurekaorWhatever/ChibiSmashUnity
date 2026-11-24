"""
Extraction helper (docs/localization.md): move hardcoded English literals of ONE C# file into Resources/Loc/en.json.

    python -P tools/loc/apply.py <file.cs> <spec.txt>

spec.txt, one string per line, TAB separated (UTF-8):
    key<TAB>English literal as written in C#      -> every "literal" in the file becomes Loc.T("key")
    key<TAB>=English literal                      -> every "literal" becomes "key" (arrays / data that are wrapped in
                                                     Loc.T where they are drawn; fix those call sites by hand)
    key<TAB>+English text                         -> only add the key to en.json (format strings written by hand)
Lines that are comments, Debug.Log calls or attributes are never touched. A key already in en.json with a different
text is an error. en.json keeps insertion order.
"""
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
EN = os.path.join(ROOT, "Assets", "ChibiClash", "Resources", "Loc", "en.json")
SKIP = ("Debug.Log", "[Header", "Tooltip(", "///")


def cs_unescape(s):
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == "\\" and i + 5 < len(s) + 0 and s[i + 1] == "u":
            out.append(chr(int(s[i + 2:i + 6], 16)))
            i += 6
        elif c == "\\" and i + 1 < len(s):
            n = s[i + 1]
            out.append({"n": "\n", "t": "\t", '"': '"', "\\": "\\", "r": "\r", "0": "\0"}.get(n, "\\" + n))
            i += 2
        else:
            out.append(c)
            i += 1
    return "".join(out)


def load_en():
    if not os.path.exists(EN):
        return {}
    with open(EN, encoding="utf-8") as f:
        return json.load(f)


def save_en(d):
    with open(EN, "w", encoding="utf-8", newline="\n") as f:
        f.write("{\n")
        items = list(d.items())
        for i, (k, v) in enumerate(items):
            f.write("  %s: %s%s\n" % (json.dumps(k, ensure_ascii=False), json.dumps(v, ensure_ascii=False),
                                     "," if i < len(items) - 1 else ""))
        f.write("}\n")


def main():
    path, spec = sys.argv[1], sys.argv[2]
    en = load_en()
    with open(path, encoding="utf-8") as f:
        src = f.read()
    lines = src.split("\n")
    total = 0
    for raw in open(spec, encoding="utf-8"):
        raw = raw.rstrip("\r\n")
        if not raw.strip() or raw.startswith("#"):
            continue
        key, text = raw.split("\t", 1)
        mode = "T"
        if text.startswith("=") and len(text) > 1:
            mode, text = "K", text[1:]
        elif text.startswith("+") and len(text) > 1:
            mode, text = "A", text[1:]
        value = cs_unescape(text)
        if key in en and en[key] != value:
            sys.exit("key %s already means %r, not %r" % (key, en[key], value))
        en[key] = value
        if mode == "A":
            continue
        lit = '"' + text + '"'
        rep = ('Loc.T("%s")' % key) if mode == "T" else ('"%s"' % key)
        pat = re.compile(r'(?<![@$\\])' + re.escape(lit))
        n = 0
        for i, l in enumerate(lines):
            s = l.strip()
            if s.startswith("//") or any(k in l for k in SKIP) or lit not in l:
                continue
            code, sep, comment = l.partition("//") if '"' not in l.partition("//")[2] else (l, "", "")
            new, c = pat.subn(rep, code)
            if c:
                lines[i] = new + sep + comment
                n += c
        if n == 0:
            print("WARN not found:", key, lit)
        total += n
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write("\n".join(lines))
    save_en(en)
    print("%s: %d replacements, en.json %d keys" % (os.path.basename(path), total, len(en)))


if __name__ == "__main__":
    main()
