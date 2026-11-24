"""Build the bundled per-script fonts (localization stage 3, docs/localization.md "Fonts").

Usage:  python -P tools/fonts/build_fonts.py <download_dir>

<download_dir> holds the official Noto downloads (fetch them with tools/fonts/fetch_fonts.sh into a fresh folder).
This script never runs code from that folder: it only reads the font files with fontTools and writes subsets to
Assets/ChibiClash/Resources/Fonts/<Script>/, one Resources folder per script so the game loads only the script it
needs (Resources.LoadAsync). The SIL OFL licence of each family is copied next to its font as OFL.txt.

CJK fonts are subset to common characters (GB2312 level 1 for Simplified Chinese, JIS X 0208 level 1 + kana for
Japanese, KS X 1001 Hangul for Korean) plus every character of the matching translation table
(Resources/Loc/<code>.json) and of languages.json. Re-run after a CJK table ships so its characters are included.
A tiny "<Script>Names" subset per CJK script holds only the endonyms of the language list, so opening the
language window does not load a full CJK font.
"""
import json
import os
import sys

from fontTools import subset
from fontTools.ttLib import TTFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "Assets", "ChibiClash", "Resources", "Fonts")
LOC = os.path.join(ROOT, "Assets", "ChibiClash", "Resources", "Loc")

BASIC = [(0x20, 0x7E), (0xA0, 0xFF), (0x2000, 0x206F), (0x3000, 0x303F), (0xFF00, 0xFFEF)]

# ISO 15924 script -> (Noto family file stem, licence file in the download dir, ranges to keep: None = whole font,
# a list of (first, last) code points, or a language code for the CJK common-character sets)
FAMILIES = {
    "Latn": ("NotoSans", "NotoSans-OFL.txt",
             [(0x20, 0x7E), (0xA0, 0x36F), (0x370, 0x3FF), (0x400, 0x52F), (0x1AB0, 0x1AFF), (0x1C80, 0x1C8F),
              (0x1D00, 0x1DFF), (0x1E00, 0x1FFF), (0x2000, 0x20CF), (0x2100, 0x215F), (0x2190, 0x21FF),
              (0x2C60, 0x2C7F), (0x2DE0, 0x2DFF), (0xA640, 0xA69F), (0xA720, 0xA7FF), (0xAB30, 0xAB6F),
              (0xFB00, 0xFB06), (0xFFFD, 0xFFFD)]),
    "Arab": ("NotoSansArabic", "NotoSansArabic-OFL.txt", None),
    "Armn": ("NotoSansArmenian", "NotoSansArmenian-OFL.txt", None),
    "Avst": ("NotoSansAvestan", "NotoSansAvestan-OFL.txt", None),
    "Beng": ("NotoSansBengali", "NotoSansBengali-OFL.txt", None),
    "Cans": ("NotoSansCanadianAboriginal", "NotoSansCanadianAboriginal-OFL.txt", None),
    "Deva": ("NotoSansDevanagari", "NotoSansDevanagari-OFL.txt", None),
    "Ethi": ("NotoSansEthiopic", "NotoSansEthiopic-OFL.txt", [(0x1200, 0x139F), (0x2D80, 0x2DDF), (0xAB00, 0xAB2F), (0x20, 0x7E)]),
    "Geor": ("NotoSansGeorgian", "NotoSansGeorgian-OFL.txt", None),
    "Gujr": ("NotoSansGujarati", "NotoSansGujarati-OFL.txt", None),
    "Guru": ("NotoSansGurmukhi", "NotoSansGurmukhi-OFL.txt", None),
    "Hebr": ("NotoSansHebrew", "NotoSansHebrew-OFL.txt", None),
    "Khmr": ("NotoSansKhmer", "NotoSansKhmer-OFL.txt", None),
    "Knda": ("NotoSansKannada", "NotoSansKannada-OFL.txt", None),
    "Laoo": ("NotoSansLao", "NotoSansLao-OFL.txt", None),
    "Mlym": ("NotoSansMalayalam", "NotoSansMalayalam-OFL.txt", None),
    "Mymr": ("NotoSansMyanmar", "NotoSansMyanmar-OFL.txt", None),
    "Orya": ("NotoSansOriya", "NotoSansOriya-OFL.txt", None),
    "Sinh": ("NotoSansSinhala", "NotoSansSinhala-OFL.txt", None),
    "Taml": ("NotoSansTamil", "NotoSansTamil-OFL.txt", None),
    "Telu": ("NotoSansTelugu", "NotoSansTelugu-OFL.txt", None),
    "Thaa": ("NotoSansThaana", "NotoSansThaana-OFL.txt", None),
    "Thai": ("NotoSansThai", "NotoSansThai-OFL.txt", None),
    "Tibt": ("NotoSerifTibetan", "NotoSerifTibetan-OFL.txt", [(0x0F00, 0x0FFF), (0x20, 0x7E), (0x25CC, 0x25CC)]),
    "Yiii": ("NotoSansYi", "NotoSansYi-OFL.txt", None),
    "Hans": ("NotoSansSC", "NotoSansCJK-LICENSE.txt", "zh"),
    "Jpan": ("NotoSansJP", "NotoSansCJK-LICENSE.txt", "ja"),
    "Kore": ("NotoSansKR", "NotoSansCJK-LICENSE.txt", "ko"),
}
CJK = {"Hans", "Jpan", "Kore"}


def dbcs_chars(codec, rows, cols=range(0xA1, 0xFF)):
    out = set()
    for r in rows:
        for c in cols:
            try:
                out.update(bytes([r, c]).decode(codec))
            except UnicodeDecodeError:
                pass
    return out


def common_cjk(code):
    s = set(chr(c) for a, b in BASIC for c in range(a, b + 1))
    if code == "zh":      # GB2312: symbol rows A1-A9 + level-1 hanzi B0-D7 (3755)
        s |= dbcs_chars("gb2312", list(range(0xA1, 0xAA)) + list(range(0xB0, 0xD8)))
    elif code == "ja":    # JIS X 0208 (EUC-JP): symbol + kana rows A1-A8 + level-1 kanji B0-CF (2965)
        s |= dbcs_chars("euc_jp", list(range(0xA1, 0xA9)) + list(range(0xB0, 0xD0)))
        s |= set(chr(c) for c in range(0x3040, 0x3100)) | set(chr(c) for c in range(0x31F0, 0x3200))
    elif code == "ko":    # KS X 1001 (EUC-KR): symbol + jamo rows A1-AC + 2350 Hangul syllables B0-C8
        s |= dbcs_chars("euc_kr", list(range(0xA1, 0xAD)) + list(range(0xB0, 0xC9)))
        s |= set(chr(c) for c in range(0x3130, 0x3190))
    return s


def table_chars(code):
    p = os.path.join(LOC, code + ".json")
    if not os.path.exists(p):
        return set()
    with open(p, encoding="utf-8") as f:
        return set("".join(json.load(f).values()))


def endonyms(script):
    with open(os.path.join(LOC, "languages.json"), encoding="utf-8") as f:
        langs = json.load(f)["languages"]
    return set("".join(l["native"] for l in langs if l["script"] == script)) | set(" ()-,")


def write_subset(src, dst, chars, ranges):
    opts = subset.Options()
    opts.layout_features = ["*"]       # keep every OpenType feature (shaping, marks, ligatures)
    opts.name_IDs = ["*"]
    opts.notdef_outline = True
    opts.hinting = False
    opts.glyph_names = False
    opts.drop_tables += ["DSIG"]
    font = TTFont(src)
    unicodes = set(ord(c) for c in chars)
    if ranges is None and not chars:
        unicodes = set(font.getBestCmap().keys())
    elif ranges:
        for a, b in ranges:
            unicodes.update(range(a, b + 1))
    sub = subset.Subsetter(opts)
    sub.populate(unicodes=unicodes)
    sub.subset(font)
    font.save(dst)


META = """fileFormatVersion: 2
guid: {guid}
TrueTypeFontImporter:
  externalObjects: {{}}
  serializedVersion: 4
  fontSize: 16
  forceTextureCase: -2
  characterSpacing: 0
  characterPadding: 1
  includeFontData: 1
  fontNames:
  - {family}
  fallbackFontReferences:{fallback}
  customCharacters:
  fontRenderingMode: 0
  ascentCalculationMode: 1
  useLegacyBoundsCalculation: 0
  shouldRoundAdvanceValue: 1
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def guid_for(rel):
    """Stable GUID per font path, so a rebuild keeps every reference."""
    import hashlib
    return hashlib.md5(("chibiclash-font:" + rel).encode("utf-8")).hexdigest()


def write_meta(dst, family, latin_guid):
    """Font importer settings: dynamic, font data included, and Noto Sans (Latin / Greek / Cyrillic) as the
    fallback so Latin words, digits and punctuation inside a script string still render."""
    rel = os.path.relpath(dst, ROOT).replace("\\", "/")
    fallback = " []" if latin_guid is None else "\n  - {fileID: 12800000, guid: %s, type: 3}" % latin_guid
    with open(dst + ".meta", "w", encoding="utf-8", newline="\n") as f:
        f.write(META.format(guid=guid_for(rel), family=family, fallback=fallback))


def family_name(stem):
    """"NotoSansThai" -> "Noto Sans Thai" (also the OS family name, used if the font is installed)."""
    out = ""
    for i, c in enumerate(stem):
        if c.isupper() and i > 0 and not (stem[i - 1].isupper() and stem[i - 1:i + 1] in ("SC", "JP", "KR")):
            out += " "
        out += c
    return out.replace("S C", "SC").replace("J P", "JP").replace("K R", "KR")


def copy(src, dst):
    with open(src, "rb") as f, open(dst, "wb") as g:
        g.write(f.read())


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    dl = sys.argv[1]
    total = 0
    latin_guid = guid_for("Assets/ChibiClash/Resources/Fonts/Latn/NotoSans-Regular.ttf")
    for script, (stem, lic, spec) in sorted(FAMILIES.items()):
        ext = ".otf" if script in CJK else ".ttf"
        src = os.path.join(dl, stem + "-Regular" + ext)
        folder = os.path.join(OUT, script)
        os.makedirs(folder, exist_ok=True)
        dst = os.path.join(folder, stem + "-Regular" + ext)
        if script in CJK:
            write_subset(src, dst, common_cjk(spec) | table_chars(spec) | endonyms(script), None)
            names = os.path.join(OUT, script + "Names")
            os.makedirs(names, exist_ok=True)
            ndst = os.path.join(names, stem + "Names-Regular" + ext)
            write_subset(src, ndst, endonyms(script), None)
            copy(os.path.join(dl, lic), os.path.join(names, "OFL.txt"))
            write_meta(ndst, family_name(stem), latin_guid)
            total += os.path.getsize(ndst)
            print("%-6s %-40s %9d bytes" % (script + "N", os.path.basename(ndst), os.path.getsize(ndst)))
        else:
            write_subset(src, dst, set(), spec)
        copy(os.path.join(dl, lic), os.path.join(folder, "OFL.txt"))
        write_meta(dst, family_name(stem), None if script == "Latn" else latin_guid)
        size = os.path.getsize(dst)
        total += size
        print("%-6s %-40s %9d bytes" % (script, os.path.basename(dst), size))
    print("total %.2f MB" % (total / 1048576.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
