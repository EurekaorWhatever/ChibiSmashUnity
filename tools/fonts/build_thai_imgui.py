"""Build the bundled Thai font for the IMGUI screens (user report 2026-11-27).

Usage:  python -P tools/fonts/build_thai_imgui.py <NotoSansThai-Regular.ttf> <NotoSans-Regular.ttf> <out.ttf>

Inputs are the plain Noto files that build_fonts.py writes (Thai: whole font; Latin: the Latn subset). Run this after
build_fonts.py and write the result over Assets/ChibiClash/Resources/Fonts/Thai/NotoSansThai-Regular.ttf.

Why: Unity's legacy IMGUI text (a) does not reach the importer's fallback font in the web build, so every digit, Latin
word and Cyrillic letter inside a Thai string vanished, and (b) has no OpenType positioning, so a tone mark over an upper
vowel (เริ่ม) collided with it. This script:
  1. adds raised / left-shifted copies of the Thai upper marks at Private Use code points (composites, no new outlines),
     which UI/ThaiShaper.cs picks in place of GPOS (the classic Thai PUA approach);
  2. merges the Latin / Greek / Cyrillic glyphs of Noto Sans into the same file, so one font draws a whole mixed line.
OpenType layout tables are dropped (IMGUI never reads them). The PUA table must match ThaiShaper.cs.
"""
import sys

from fontTools.merge import Merger
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._g_l_y_f import Glyph, GlyphComponent

TONES = [0x0E48, 0x0E49, 0x0E4A, 0x0E4B, 0x0E4C]                 # mai ek .. thanthakhat
UPPER = [0x0E31, 0x0E34, 0x0E35, 0x0E36, 0x0E37, 0x0E47, 0x0E4D]  # upper vowels, maitaikhu, nikhahit
RAISE = 215     # tone above an upper vowel: GPOS mark2mark anchors 713..772 minus the tone anchor 536 (font units)
SHIFT = -140    # marks over a tall consonant (ป ฝ ฟ ฬ): GPOS base anchors put them 116..159 units further left

# (PUA code point, source code point, dx, dy) -- keep in sync with ThaiShaper.cs
PUA = []
for k, cp in enumerate(TONES):
    PUA.append((0xF700 + k, cp, 0, RAISE))       # high
    PUA.append((0xF705 + k, cp, SHIFT, 0))       # low, left
    PUA.append((0xF70A + k, cp, SHIFT, RAISE))   # high, left
for k, cp in enumerate(UPPER):
    PUA.append((0xF710 + k, cp, SHIFT, 0))       # upper vowel, left


def add_pua(font):
    glyf, hmtx, cmap = font["glyf"], font["hmtx"], font.getBestCmap()
    for pua, src, dx, dy in PUA:
        base = cmap[src]
        name = "uni%04X" % pua
        comp = GlyphComponent()
        comp.glyphName, comp.x, comp.y, comp.flags = base, dx, dy, 0x4   # ROUND_XY_TO_GRID
        g = Glyph()
        g.numberOfContours = -1
        g.components = [comp]
        glyf[name] = g   # also appends name to the glyph order
        g.recalcBounds(glyf)
        hmtx[name] = (0, getattr(g, "xMin", 0))
    order = list(glyf.glyphOrder)
    font.setGlyphOrder(order)
    for t in font["cmap"].tables:
        if t.isUnicode():
            for pua, _, _, _ in PUA:
                t.cmap[pua] = "uni%04X" % pua
    font["maxp"].numGlyphs = len(order)


def strip_layout(font):
    for tag in ("GSUB", "GPOS", "GDEF", "BASE", "JSTF", "MATH", "STAT", "HVAR", "MVAR"):
        if tag in font:
            del font[tag]


def main(thai_path, latin_path, out_path):
    thai = TTFont(thai_path)
    if 0x41 in thai.getBestCmap():
        sys.exit("error: %s already has Latin glyphs; pass the plain Noto Sans Thai file" % thai_path)
    add_pua(thai)
    strip_layout(thai)
    latin = TTFont(latin_path)
    strip_layout(latin)
    tmp_thai, tmp_latin = out_path + ".thai.tmp", out_path + ".latn.tmp"
    thai.save(tmp_thai)
    latin.save(tmp_latin)
    merged = Merger().merge([tmp_thai, tmp_latin])   # first font wins shared code points (space, punctuation)
    merged.save(out_path)
    import os
    os.remove(tmp_thai)
    os.remove(tmp_latin)
    cmap = TTFont(out_path).getBestCmap()
    for cp in (0x30, 0x41, 0x0440, 0x0E01, 0xF700, 0xF716, 0xD7):
        assert cp in cmap, "missing U+%04X" % cp
    print("wrote", out_path, len(cmap), "code points")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])
