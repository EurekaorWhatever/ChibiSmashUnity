"""
Build Assets/ChibiClash/Resources/Loc/<code>.json from the translator sheets tools/loc/<code>/*.tsv
(key<TAB>text, one per line, \\n for a line break). Keys follow en.json order. Checks, and fails on:
  - a key that is not in en.json (stale or misspelt)
  - {n} placeholders that differ from the English text
Missing keys are listed (they fall back to English in game) but do not fail the build.

    python -P tools/loc/build_table.py th
"""
import glob
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LOC = os.path.join(ROOT, "Assets", "ChibiClash", "Resources", "Loc")
PH = re.compile(r"\{\d+\}")


def main():
    code = sys.argv[1]
    en = json.load(open(os.path.join(LOC, "en.json"), encoding="utf-8"))
    tr, errors = {}, []
    for path in sorted(glob.glob(os.path.join(os.path.dirname(__file__), code, "*.tsv"))):
        for n, line in enumerate(open(path, encoding="utf-8"), 1):
            line = line.rstrip("\r\n")
            if not line.strip() or line.startswith("#"):
                continue
            key, text = line.split("\t", 1)
            text = text.replace("\\n", "\n")
            where = "%s:%d %s" % (os.path.basename(path), n, key)
            if key not in en:
                errors.append(where + ": not in en.json")
                continue
            if key in tr:
                errors.append(where + ": duplicate")
            if sorted(PH.findall(text)) != sorted(PH.findall(en[key])):
                errors.append(where + ": placeholders %s, English has %s" % (PH.findall(text), PH.findall(en[key])))
            tr[key] = text
    if errors:
        sys.exit("\n".join(errors))
    missing = [k for k in en if k not in tr]
    out = os.path.join(LOC, code + ".json")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        items = [(k, tr[k]) for k in en if k in tr]
        f.write("{\n" + ",\n".join("  %s: %s" % (json.dumps(k, ensure_ascii=False), json.dumps(v, ensure_ascii=False))
                                   for k, v in items) + "\n}\n")
    print("%s.json: %d keys, %d missing (English fallback)%s" % (code, len(tr), len(missing),
                                                                ": " + ", ".join(missing[:20]) if missing else ""))


if __name__ == "__main__":
    main()
