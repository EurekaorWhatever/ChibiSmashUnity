"""
Sheet-pose move animator for Chibi Clash (decision AN-1).

    python -P tools/art/sheet_anims.py [kaia bruna ...] [--debug DIR] [--only MOVEID,...]

Cuts the side-view pose drawings out of every numbered move panel on the moveset sheets
(design/art/character-sheets/<char>-moveset.png), separates the drawn effect (no ink outline) from the ink-outlined
figure, normalises the poses (facing right, feet on one baseline, one scale per character) and builds one clip per
CSV move id plus an idle breathing loop:

    Assets/ChibiClash/Resources/Animations/<Id>/Move_<moveId>.png   18 frames, 640x256 each, 30 fps
    Assets/ChibiClash/Resources/Animations/<Id>/Idle_Sheet.png      24 frames, 640x256 each, 15 fps (seamless loop)
    Assets/ChibiClash/Resources/Animations/<Id>/Move_<char>_jab.png  Jab 1-2-3 (panel 1, no CSV row) + _jab1.._jab3
    Assets/ChibiClash/Resources/Animations/<Id>/Sheet_<State>.png   Run Jump Fall Guard Hit Launch KO Ultimate, built
                                                                    from the sheet's own side-view drawings (STATE_SRC)
    Assets/ChibiClash/Resources/Animations/<Id>/clips.txt           name frameCount fps strikeFrame recoverFrame cols

Sheets are grids of 640x256 frames, GRID_COLS per row, read left to right, top to bottom (<= 4096 px).

Move clip: anticipation (wind-up with squash), a motion-blur smear frame, the strike (drawn effect, 2-frame
stretch/squash), a hold while the effect fades, and a cross-dissolve into Idle_Sheet frame 0. FighterVisual remaps
time per move so the strike frame lands on the first active tick (clips.txt). Upscale: Real-ESRGAN anime x4
(CHIBI_ESRGAN_MODEL, needs torch), else EDSR x4 (CHIBI_SR_MODEL), else Lanczos (see upscale4); then one resample to
the frame scale, darken-only unsharp, ink re-darkened, hardened alpha.

Frame convention (SpriteAnimLibrary): the frame is 1 world unit tall, feet at the horizontal centre FeetPivot (4 %)
above the bottom, the standing character fills FigureFill (78 %) of the height. Move frames are 2.5x as wide as they
are tall (SpriteAnimLibrary.MoveFrameAspect) so drawn slash arcs fit. The .meta (no mipmaps, see save()) is written
before the PNG so Unity imports at full size.

Panel rectangles are measured by hand (PANELS below: the drawing area of each panel, between the title bar and the
frame-data text, in sheet pixels). Panel numbers follow design/gdd/movesets/<char>.md; MOVES maps them to CSV ids.
"""
import hashlib
import math
import os
import sys
import tempfile
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import cv2
except ImportError:            # OpenCV is optional: numpy / scipy / Pillow stand-in (tools/art/cv2compat.py)
    import cv2compat as cv2
from unity_meta import _TEMPLATE, _guid, in_assets, write_folder_meta  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SHEETS = os.path.join(ROOT, "design", "art", "character-sheets")
OUT = os.environ.get("CHIBI_ANIM_OUT") or os.path.join(ROOT, "Assets", "ChibiClash", "Resources", "Animations")

H = 256
ASPECT = 2.5                     # SpriteAnimLibrary.MoveFrameAspect
W = int(H * ASPECT)              # 640
FEET_Y = H - H * 0.04            # baseline (px from top)
FIG = H * 0.78                   # standing figure height (SpriteAnimLibrary.FigureFill)
SS = 1                           # canvas = frame resolution: the layers are resampled once, from the 4x colour

IDS = {"kaia": "Kaia", "bruna": "Bruna", "ren": "Ren", "lyra": "Lyra", "taro": "Taro"}

# ------------------------------------------------------------------------------------------------ panel table
# key: panel label ("idle" or md number) -> (x0, y0, x1, y1) drawing area in sheet pixels
PANELS = {
    "kaia": {
        "idle": (258, 118, 405, 214),
        2: (645, 120, 840, 198), 3: (845, 118, 1000, 198), 4: (1005, 120, 1110, 198), 5: (1115, 118, 1305, 198),
        6: (255, 315, 480, 393), 7: (485, 315, 690, 393), 8: (695, 315, 895, 393), 9: (900, 315, 1100, 393),
        10: (1105, 315, 1305, 393),
        11: (262, 510, 480, 589), 12: (485, 510, 690, 589), 13: (695, 510, 900, 589), 14: (905, 510, 1105, 589),
        15: (1110, 510, 1305, 589),
        16: (272, 705, 525, 764), 17: (545, 705, 795, 764), 18: (808, 705, 1050, 764), 19: (1062, 705, 1305, 764),
        20: (270, 888, 490, 966), 21: (500, 888, 715, 966), 23: (905, 888, 1100, 966), 24: (1110, 888, 1305, 966),
    },
    "bruna": {
        "idle": (262, 108, 455, 203),
        2: (775, 115, 1030, 205), 3: (1045, 108, 1300, 205),
        4: (258, 315, 470, 400), 5: (480, 315, 690, 400), 6: (693, 312, 905, 400), 7: (905, 308, 1100, 400),
        8: (1105, 312, 1305, 400),
        9: (258, 512, 478, 596), 10: (485, 512, 690, 596), 11: (698, 512, 895, 596), 12: (905, 512, 1100, 596),
        13: (1108, 512, 1305, 596),
        14: (262, 708, 518, 777), 15: (528, 708, 778, 777), 16: (790, 708, 1050, 777), 17: (1062, 708, 1305, 777),
        18: (258, 895, 458, 980), 19: (468, 895, 672, 980), 20: (680, 895, 888, 980), 21: (895, 895, 1098, 980),
        22: (1105, 895, 1305, 980),
    },
    "ren": {
        "idle": (245, 108, 302, 205),
        2: (705, 105, 935, 202),
        3: (245, 300, 475, 396), 4: (485, 300, 705, 396), 5: (715, 300, 935, 396),
        6: (245, 490, 475, 562), 7: (485, 490, 705, 562), 8: (715, 490, 935, 562),
        9: (245, 645, 475, 704), 10: (485, 645, 705, 704), 11: (715, 645, 935, 704),
        12: (245, 780, 575, 832), 13: (585, 780, 935, 832),
        14: (950, 118, 1225, 177), 15: (1235, 118, 1530, 180),
        16: (950, 318, 1225, 369), 17: (1235, 318, 1530, 369),
        18: (950, 517, 1270, 572), 19: (950, 602, 1270, 652), 20: (950, 690, 1270, 740), 21: (950, 773, 1270, 823),
        22: (950, 850, 1270, 892),
    },
    "lyra": {
        "idle": (265, 75, 348, 160),
        2: (258, 255, 445, 323), 3: (450, 255, 625, 323), 4: (630, 255, 805, 323), 5: (812, 255, 990, 323),
        6: (1005, 255, 1185, 323), 7: (1190, 255, 1360, 323), 8: (1365, 255, 1530, 323),
        9: (258, 455, 410, 523), 10: (415, 455, 565, 523), 11: (568, 455, 710, 523), 12: (712, 455, 860, 523),
        13: (862, 455, 1005, 523),
        17: (258, 650, 575, 708), 18: (580, 650, 895, 708), 19: (900, 650, 1205, 708), 20: (1210, 650, 1530, 708),
        21: (262, 830, 478, 902), 22: (485, 830, 672, 902), 23: (678, 830, 892, 902), 24: (898, 830, 1085, 902),
        25: (1090, 830, 1285, 902),
    },
    "taro": {
        "idle": (232, 105, 428, 236),
        2: (722, 110, 975, 207), 3: (978, 110, 1238, 207),
        4: (222, 305, 425, 392), 5: (430, 305, 640, 392), 6: (645, 305, 850, 392), 7: (855, 305, 1030, 392),
        8: (1035, 305, 1240, 392),
        9: (222, 498, 430, 570), 10: (435, 498, 640, 570), 11: (645, 498, 840, 570), 12: (842, 498, 1025, 570),
        13: (1028, 498, 1240, 570),
        14: (222, 692, 470, 762), 15: (475, 692, 720, 762), 16: (725, 692, 970, 762), 17: (975, 692, 1240, 762),
        18: (222, 892, 470, 982), 19: (475, 892, 720, 982), 20: (725, 892, 970, 982), 21: (975, 892, 1240, 982),
    },
}

# Per-panel tweaks: flip (drawn facing left), keep (indices of poses to use, left to right),
# erase (sheet-pixel rectangles painted over with the paper colour first: lettered sound words)
TWEAKS = {
    ("ren", 2): {"erase": [(874, 155, 930, 195)]},
    ("ren", 3): {"erase": [(395, 300, 455, 338)]},
    ("ren", 4): {"erase": [(648, 320, 702, 358)]},
    ("ren", 5): {"erase": [(858, 308, 920, 348)]},
    ("ren", 6): {"erase": [(408, 512, 470, 556)]},
    ("ren", 7): {"erase": [(636, 514, 704, 556)]},
    ("ren", 8): {"erase": [(858, 508, 930, 550)]},
    ("kaia", 23): {"keep": [0]},          # the second drawing is Kaia on the wall prop
    # "cut": sheet points inside a paper gap the outline encloses (paper seen between the legs): the pale region
    # around each point is removed from the figure. Paper and white cloth have the same colour statistics on these
    # sheets, so these few gaps are marked by hand.
    ("bruna", 5): {"cut": [(546, 370)]},
    ("ren", 13): {"keep": [1]},           # the sheet draws the dive pose ~1.3x larger than the first: strike pose only
}

# Panels with no CSV row, used for the jab and the Sheet_* state clips: 1 = Jab 1-2-3 (3 poses = 3 hits),
# "move" = the idle / movement strip (Ren: idle walk run dash; Lyra: idle walk dash dash-stop jump double-jump),
# "dodge" / "parry" / "guard" = Lyra's defensive panels, Kaia 22 = air dodge.
PANELS["kaia"].update({1: (410, 120, 640, 198), 22: (720, 895, 895, 966)})
PANELS["bruna"].update({1: (470, 112, 765, 201)})
PANELS["ren"].update({1: (465, 108, 700, 198), "move": (245, 108, 455, 205)})
PANELS["lyra"].update({1: (840, 62, 1360, 130), "move": (262, 58, 825, 162), "dodge": (1012, 455, 1190, 520),
                       "parry": (1195, 455, 1365, 520), "guard": (1372, 455, 1530, 520)})
PANELS["taro"].update({1: (432, 105, 715, 200)})
TWEAKS.update({
    ("bruna", 1): {"erase": [(543, 170, 577, 195), (640, 163, 673, 192), (714, 158, 763, 190)]},   # THUD! THUD! SMASH!
    ("ren", 1): {"erase": [(648, 172, 700, 200)]},                                                  # THWIP!
    ("taro", 1): {"erase": [(535, 104, 600, 130)], "grow": 1},                                      # SWISH!; spears nearly touch
})

# drawing-area fixes where a raised weapon / leap reaches into the title row (title fragments are dropped by the
# edge filter in segment)
for _c, _n, _y0 in (("bruna", 3, 98), ("bruna", 18, 886), ("bruna", 19, 886), ("bruna", 20, 886),
                    ("bruna", 21, 886), ("bruna", 22, 886),
                    ("taro", 9, 489), ("taro", 10, 489), ("taro", 11, 489), ("taro", 12, 489), ("taro", 13, 489),
                    ("taro", 17, 674), ("taro", 18, 885), ("taro", 19, 885), ("taro", 20, 885), ("taro", 21, 885)):
    _b = PANELS[_c][_n]
    PANELS[_c][_n] = (_b[0], _y0, _b[2], _b[3])


def load_moves():
    """Panel number -> list of CSV ids, parsed from the per-character mapping tables."""
    res = {}
    for c in IDS:
        m = {}
        path = os.path.join(ROOT, "design", "gdd", "movesets", c + ".md")
        for line in open(path, encoding="utf-8"):
            cells = [x.strip() for x in line.strip().strip("|").split("|")]
            if len(cells) < 9 or not cells[0].isdigit():
                continue
            note = cells[-1]
            ids = []
            for tok in note.replace(",", " ").replace("/", " ").split():
                tok = tok.strip("*`():;.")
                if not tok.replace("_", "").isalnum():
                    continue
                if tok.startswith(c + "_"):
                    ids.append(tok)
            if c == "ren" and "ren_nspec_lv1-3" in note:
                ids = ["ren_nspec_lv1", "ren_nspec_lv2", "ren_nspec_lv3"]
            ids = [i for i in ids if not i.endswith("-3")]
            if ids:
                m[int(cells[0])] = ids
        res[c] = m
    return res


def csv_ids():
    import csv
    out = {}
    with open(os.path.join(ROOT, "design", "gdd", "movesets", "movesets.csv"), encoding="utf-8") as f:
        for r in csv.DictReader(f):
            out.setdefault(r["char"], []).append(r["id"])
    return out

# ------------------------------------------------------------------------------------------------ segmentation


def segment(sheet, box, erase=()):
    """Cuts one panel's drawing area: returns (rgb crop, figure mask, effect mask), masks uint8 0/1."""
    x0, y0, x1, y1 = box
    crop = sheet[y0:y1, x0:x1].copy()
    if erase:
        paper = np.median(np.concatenate([crop[0], crop[-1]]), axis=0).astype(np.uint8)
        for ex0, ey0, ex1, ey1 in erase:
            crop[max(0, ey0 - y0):max(0, ey1 - y0), max(0, ex0 - x0):max(0, ex1 - x0)] = paper
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    lab = cv2.cvtColor(crop, cv2.COLOR_BGR2LAB).astype(np.float32)
    border = np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]])
    bg = np.median(border, axis=0)
    dist = np.linalg.norm(lab - bg, axis=2)
    sat, val = hsv[..., 1].astype(np.int32), hsv[..., 2].astype(np.int32)
    # passable for the background flood: near the paper colour, or pale and greyish (ground shadows, dust, speed lines)
    passable = ((dist < 24) | ((sat < 55) & (val > 165)) | ((sat < 30) & (val > 120))).astype(np.uint8)
    # light skin and highlights are "pale" too: the flood used to leak into faces through 1-3 px breaks in the
    # outline. The ink, closed over such breaks, is a wall the flood cannot cross.
    ink0 = (val < 105).astype(np.uint8)
    wall = cv2.morphologyEx(ink0, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)))
    passable &= (1 - wall)
    n, lab_cc = cv2.connectedComponents(passable, connectivity=4)
    edge = set(np.unique(np.concatenate([lab_cc[0], lab_cc[-1], lab_cc[:, 0], lab_cc[:, -1]]))) - {0}
    bgmask = np.isin(lab_cc, list(edge)) & (passable > 0)
    # enclosed pale regions stay solid (cream cloth and skin are paper-coloured), unless they are plain paper AND
    # big: a real gap, e.g. between bow and string or an arm and the body (>= 150 sheet px, ~1600 frame px)
    n2, cc2, st2, _ = cv2.connectedComponentsWithStats(((passable > 0) & ~bgmask).astype(np.uint8), 4)
    for i in range(1, n2):
        m = cc2 == i
        if st2[i, 4] >= 150 and float(dist[m].mean()) < 10:
            bgmask |= m
    fg = (~bgmask).astype(np.uint8)
    fg = cv2.morphologyEx(fg, cv2.MORPH_OPEN, np.ones((2, 2), np.uint8))
    # drop specks and text-sized bits
    n, cc, st, _ = cv2.connectedComponentsWithStats(fg, 8)
    keep = np.zeros_like(fg)
    for i in range(1, n):
        x, y, w, h, a = st[i]
        edge = x == 0 or y == 0 or x + w >= fg.shape[1] or y + h >= fg.shape[0]
        if edge and (h <= 16 or w <= 8 or a < 0.12 * w * h):
            continue                                   # panel border / title-bar / text fragment cut by the box
        if a >= 25 and (h >= 14 or w >= 30):
            keep[cc == i] = 1
    fg = keep
    # ink-enclosed figure: close the outlines, fill the holes
    ink = ((val < 105) & (fg > 0)).astype(np.uint8)
    closed = cv2.morphologyEx(ink, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)))
    filled = fill_holes(closed)
    figure = (filled > 0) & (fg > 0)
    figure = cv2.dilate(figure.astype(np.uint8), np.ones((3, 3), np.uint8)) & fg
    rest = (fg > 0) & (figure == 0)
    # leftover pieces: big unoutlined swathes are the effect, small pieces touching the figure belong to it
    effect = np.zeros_like(fg)
    n, cc, st, _ = cv2.connectedComponentsWithStats(rest.astype(np.uint8), 8)
    fig_d = cv2.dilate(figure, np.ones((3, 3), np.uint8))
    for i in range(1, n):
        a = st[i, 4]
        m = cc == i
        touching = (fig_d[m] > 0).any()
        # how much of the piece's rim borders the figure: a cloth / skin patch the ink closing missed is mostly
        # surrounded by figure, a slash arc only touches it where the blade is
        mu = m.astype(np.uint8)
        rim = cv2.dilate(mu, np.ones((3, 3), np.uint8)) & (1 - mu)
        enclosed = (figure[rim > 0] > 0).mean() if rim.any() else 0.0
        if (a >= 60 and enclosed < 0.45) or not touching:
            effect[m] = 1
        else:
            figure[m] = 1
    # ground shadow caught under the feet: pale, greyish pixels below the lowest ink of their column
    rows = np.arange(fg.shape[0])[:, None]
    has_ink = ink.any(axis=0)
    low_ink = np.where(has_ink, fg.shape[0] - 1 - np.argmax(ink[::-1], axis=0), -1)
    pale = (val > 175) & (sat < 70)
    under = (rows > low_ink[None, :] + 1) & pale
    figure = figure.astype(np.uint8)
    figure[under] = 0
    effect = effect.astype(np.uint8)
    effect[under & (sat < 40)] = 0
    # pinholes: any pocket under 40 sheet px fully enclosed by the figure is drawing, not background (the large
    # paper gaps kept above are >= 150 px)
    # and any drawn pixel enclosed by the figure is part of it: a saturated sash / skirt panel without its own ink
    # outline used to be taken for the effect layer and left a hole when the effect is hidden
    pockets = fill_holes(figure) - figure
    n3, cc3, st3, _ = cv2.connectedComponentsWithStats(pockets.astype(np.uint8), 4)
    for i in range(1, n3):
        m = cc3 == i
        if st3[i, 4] < 40:
            figure[m] = 1
        else:
            figure[m & (fg > 0)] = 1
    effect[figure > 0] = 0
    figure = restore_skin(figure, hsv)
    figure = peel_paper(figure, hsv, wall)
    return crop, figure, effect


def restore_skin(figure, hsv):
    """Gives back the light skin the background flood ate: where a face or arm meets the paper with a faint outline,
    the flood (pale = passable) bit notches into cheeks and ears, and faces lost their side. Warm, light pixels
    (skin hue, saturation above the neutral paper's) that a closing of the figure covers are figure again."""
    h, sat, val = hsv[..., 0].astype(np.int32), hsv[..., 1].astype(np.int32), hsv[..., 2].astype(np.int32)
    warm = ((h <= 22) | (h >= 170)) & (sat >= 9) & (val > 150)      # paper: sat 2-6; Ren's pale skin: ~10-20
    closed = cv2.morphologyEx(figure.astype(np.uint8), cv2.MORPH_CLOSE,
                              cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (11, 11)))
    add = (figure == 0) & (closed > 0) & warm
    if not add.any():
        return figure
    out = figure.copy()
    out[add] = 1
    return out


def cut_gaps(crop, figure, box, points):
    """TWEAKS "cut": removes the pale (paper-coloured) region of the figure around each sheet point."""
    if not points:
        return figure
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    pale = ((hsv[..., 2] > 200) & (hsv[..., 1] < 30) & (figure > 0)).astype(np.uint8)
    n, cc = cv2.connectedComponents(pale, connectivity=4)
    out = figure.copy()
    for sx, sy in points:
        x, y = sx - box[0], sy - box[1]
        if 0 <= y < cc.shape[0] and 0 <= x < cc.shape[1] and cc[y, x] > 0:
            out[cc == cc[y, x]] = 0
        else:
            print("   warning: cut point (%d, %d) is not on a pale part of the figure" % (sx, sy))
    return out


def peel_paper(figure, hsv, wall):
    """Removes paper-white pixels the figure mask picked up OUTSIDE the ink outline: the white fringe around the
    silhouette, ground shadows / dust streaks under the feet and paper caught between legs. A pale pixel is peeled
    only when a path of pale pixels leads to it from outside the figure without crossing ink (the closed outline is
    the wall), so white drawn INSIDE the outline (eye whites, white cloth, highlights) always stays."""
    sat, val = hsv[..., 1].astype(np.int32), hsv[..., 2].astype(np.int32)
    # paper is neutral (sat < 18); light skin and cream cloth are warm (sat 25-60), so they are never peeled even
    # where the outline has a gap
    pale = ((val > 205) & (sat < 18)) & (figure > 0) & (wall == 0)
    if not pale.any():
        return figure
    outside = cv2.dilate((figure == 0).astype(np.uint8), np.ones((3, 3), np.uint8)) > 0
    n, cc = cv2.connectedComponents(pale.astype(np.uint8), connectivity=4)
    if n <= 1:
        return figure
    touch = np.unique(cc[outside & pale])
    out = figure.copy()
    # only a thin rim (<= 3 sheet px from the outside) is peeled: white cloth (Taro's robe, Ren's shirt) that meets
    # the paper through a gap in its outline would otherwise be eaten whole
    rim = cv2.distanceTransform(figure.astype(np.uint8), cv2.DIST_L2, 3) <= 3.0
    out[np.isin(cc, touch[touch > 0]) & rim] = 0
    # ground slab: the sheets draw a pale dust / floor patch between and under the feet and close it with a dark
    # ground-shadow stroke, so the outside flood above cannot reach it. A pale patch whose bottom sits on the
    # figure's lowest rows, in the lower third of the body, is floor, not costume (no costume white reaches the
    # ground on these five sheets: boots are dark).
    ys = np.nonzero(out)[0]
    if len(ys):
        top, bot = ys.min(), ys.max()
        # ground-shadow stroke: a long, thin horizontal band along the figure's lowest rows (feet stand on it).
        # Boots are short and tall; the stroke is >= 16 sheet px long and <= 4 px thick.
        band = np.zeros_like(out)
        band[max(top, bot - 6):bot + 1] = out[max(top, bot - 6):bot + 1]
        stroke = cv2.morphologyEx(band, cv2.MORPH_OPEN, np.ones((1, 16), np.uint8))
        if stroke.any():
            thick = cv2.erode(out, np.ones((5, 1), np.uint8)) > 0
            kill = (stroke > 0) & ~thick
            out[kill] = 0
            ys = np.nonzero(out)[0]
            bot = ys.max() if len(ys) else bot
        low = ((val > 178) & (sat < 72)) & (out > 0)
        n2, cc2, st2, _ = cv2.connectedComponentsWithStats(low.astype(np.uint8), 4)
        for i in range(1, n2):
            x, y, w, h, a = st2[i]
            if a >= 16 and y + h - 1 >= bot - 5 and y >= top + 0.62 * (bot - top):
                out[cc2 == i] = 0
    # a peel can leave 1-px slivers of paper-grey along the outline: open them away, then drop crumbs
    out = cv2.morphologyEx(out, cv2.MORPH_OPEN, np.ones((2, 2), np.uint8))
    n, cc, st, _ = cv2.connectedComponentsWithStats(out, 8)
    if n > 2:
        big = int(np.argmax(st[1:, 4])) + 1
        for i in range(1, n):
            if i != big and st[i, 4] < 12:
                out[cc == i] = 0
    return out


def fill_holes(mask):
    h, w = mask.shape
    pad = np.zeros((h + 2, w + 2), np.uint8)
    pad[1:-1, 1:-1] = mask
    ff = pad.copy()
    m2 = np.zeros((h + 4, w + 4), np.uint8)
    cv2.floodFill(ff, m2, (0, 0), 1)
    holes = (ff == 0)
    return (pad | holes.astype(np.uint8))[1:-1, 1:-1]


def split_poses(figure, effect, keep=None, grow=7):
    """Figure components big enough to be a character -> list of (figure mask, effect mask) left to right."""
    # label on a slightly grown mask so a figure the ink fill split (head / torso / spear) stays one piece
    grown = cv2.dilate(figure, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (grow, grow)))
    n, cc, st, cen = cv2.connectedComponentsWithStats(grown, 8)
    if n <= 1:
        return []
    cc = np.where(figure > 0, cc, 0)
    areas = st[1:, 4]
    tallest = st[1:, 3].max()
    # a body: big AND tall (rocks, spears and arrows are big but flat)
    big = [i + 1 for i in range(n - 1)
           if areas[i] >= max(150, 0.22 * areas.max()) and st[i + 1, 3] >= 0.55 * tallest]
    big.sort(key=lambda i: st[i, 0] + st[i, 2] / 2)
    # bodies that overlap horizontally are one figure split by the mask (head over torso)
    groups = []
    for i in big:
        x, y, w, h = st[i, 0], st[i, 1], st[i, 2], st[i, 3]
        for g in groups:
            gx0, gx1, gy0, gy1 = g[1], g[2], g[3], g[4]
            ov = min(x + w, gx1) - max(x, gx0)
            ovy = min(y + h, gy1) - max(y, gy0)
            if ov > 0.4 * min(w, gx1 - gx0) and ovy < 0.5 * min(h, gy1 - gy0):
                g[0].append(i)
                g[1], g[2], g[3], g[4] = min(gx0, x), max(gx1, x + w), min(gy0, y), max(gy1, y + h)
                break
        else:
            groups.append([[i], x, x + w, y, y + h])
    poses = [np.isin(cc, g[0]).astype(np.uint8) for g in groups]
    used = {i for g in groups for i in g[0]}
    # other inked pieces: touching a body -> part of it (weapon, hair split by the mask), else drawn effect (rocks)
    dts = [cv2.distanceTransform(1 - p, cv2.DIST_L2, 3) for p in poses]
    extra = np.zeros_like(effect)
    for i in range(1, n):
        if i in used:
            continue
        m = cc == i
        d = [dt[m].min() for dt in dts]
        j = int(np.argmin(d))
        if d[j] < 12:
            poses[j][m] = 1
        else:
            extra[m] = 1
    if extra.any():
        dts = [cv2.distanceTransform(1 - p, cv2.DIST_L2, 3) for p in poses]
        effect = effect | extra
    effs = [np.zeros_like(effect) for _ in poses]
    n2, cc2 = cv2.connectedComponents(effect, connectivity=8)
    for i in range(1, n2):
        m = cc2 == i
        d = [dt[m].min() for dt in dts]
        effs[int(np.argmin(d))][m] = 1
    out = list(zip(poses, effs))
    if keep is not None:
        out = [out[i] for i in keep if i < len(out)]
    return out

# ------------------------------------------------------------------------------------------------ upscale + layers

# Super-resolution, best available first (none of the model files is in the repo):
#   1. Real-ESRGAN x4plus_anime_6B (needs torch): CHIBI_ESRGAN_MODEL -> RealESRGAN_x4plus_anime_6B.pth from
#      https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.2.4/RealESRGAN_x4plus_anime_6B.pth
#      (the RRDBNet below is written here; only the 'params_ema' weights are loaded).
#   2. OpenCV dnn_superres EDSR x4 (opencv-contrib): CHIBI_SR_MODEL -> EDSR_x4.pb from
#      https://github.com/Saafke/EDSR_Tensorflow/raw/master/models/EDSR_x4.pb
#   3. Lanczos.
# Results are cached per model and crop hash in CHIBI_SR_CACHE (default: %TEMP%/chibi_sr_cache).
ESRGAN_MODEL = os.environ.get("CHIBI_ESRGAN_MODEL", "")
SR_MODEL = os.environ.get("CHIBI_SR_MODEL", "")
SR_CACHE = os.environ.get("CHIBI_SR_CACHE") or os.path.join(tempfile.gettempdir(), "chibi_sr_cache")
_sr = None
_esr = None


def edge_sharpen(img, radius=2, blur=1.0, strength=1.0, min_contrast=40.0):
    """Toggle-mapping sharpen of a 4x Lanczos image (decision 2026-11-13, replaces Real-ESRGAN as the default):
    each pixel is pushed towards the darker or lighter extreme of its (2r+1)^2 neighbourhood, whichever it is
    closer to, only where the neighbourhood has contrast. Edges become crisp ink steps with no ringing, flat areas
    are left alone, and a 1-3 px dark dot (an eye on the sheet) stays a dark dot. The anime GAN upscalers painted
    those dots over as skin, which is why the faces had no eyes."""
    from scipy import ndimage as ndi
    f = img.astype(np.float32)
    if blur > 0:
        f = ndi.gaussian_filter(f, (blur, blur, 0))
    lum = f.mean(axis=2)
    fp = np.ones((2 * radius + 1, 2 * radius + 1), bool)
    mn, mx = ndi.minimum_filter(lum, footprint=fp), ndi.maximum_filter(lum, footprint=fp)
    t = (lum - mn) / np.maximum(mx - mn, 1e-3)
    t = t * t * (3 - 2 * t)
    t = t * t * (3 - 2 * t)
    w = strength * np.clip((mx - mn) / min_contrast, 0, 1)
    cmn = np.dstack([ndi.minimum_filter(f[..., i], footprint=fp) for i in range(3)])
    cmx = np.dstack([ndi.maximum_filter(f[..., i], footprint=fp) for i in range(3)])
    tgt = cmn + (cmx - cmn) * t[..., None]
    return np.clip(f + (tgt - f) * w[..., None] + 0.5, 0, 255).astype(np.uint8)


def _sr_backend():
    """'esrgan', 'edsr' or 'lanczos' (Lanczos + edge_sharpen), the best one that can run here. The free, local
    'lanczos' path is the default and the one the shipped clips use: set neither model variable."""
    if os.environ.get("CHIBI_SR_GAN", "") != "1":
        return "lanczos"
    if ESRGAN_MODEL and os.path.exists(ESRGAN_MODEL):
        try:
            import torch  # noqa: F401
            return "esrgan"
        except ImportError:
            pass
    if SR_MODEL and os.path.exists(SR_MODEL):
        return "edsr"
    return "lanczos"


def upscale4(crop):
    """Panel crop (BGR uint8) -> 4x BGR uint8 with the best available backend (see above)."""
    global _sr, _esr
    backend = _sr_backend()
    if backend == "lanczos":
        return edge_sharpen(cv2.resize(crop, None, fx=4, fy=4, interpolation=cv2.INTER_LANCZOS4))
    key = hashlib.md5(backend.encode() + crop.tobytes() + str(crop.shape).encode()).hexdigest()
    path = os.path.join(SR_CACHE, key + ".png")
    if os.path.exists(path):
        img = cv2.imread(path)
        if img is not None:
            return img
    if backend == "esrgan":
        if _esr is None:
            _esr = _load_esrgan(ESRGAN_MODEL)
        img = _tiled(crop, _esrgan_up, tile=96, pad=10)
    else:
        if _sr is None:
            _sr = cv2.dnn_superres.DnnSuperResImpl_create()
            _sr.readModel(SR_MODEL)
            _sr.setModel("edsr", 4)
        img = _tiled(crop, _sr.upsample, tile=72, pad=8)
    os.makedirs(SR_CACHE, exist_ok=True)
    cv2.imwrite(path, img)
    return img


def _tiled(crop, fn, tile, pad):
    """4x upscale in overlapping tiles (a whole panel at once runs the CPU backends out of memory)."""
    h, w = crop.shape[:2]
    out = np.zeros((h * 4, w * 4, 3), np.uint8)
    src = cv2.copyMakeBorder(crop, pad, pad, pad, pad, cv2.BORDER_REFLECT)
    for y in range(0, h, tile):
        for x in range(0, w, tile):
            th, tw = min(tile, h - y), min(tile, w - x)
            up = fn(np.ascontiguousarray(src[y:y + th + 2 * pad, x:x + tw + 2 * pad]))
            out[y * 4:(y + th) * 4, x * 4:(x + tw) * 4] = up[pad * 4:(pad + th) * 4, pad * 4:(pad + tw) * 4]
    return out


def _load_esrgan(path):
    """RRDBNet (num_block=6, num_feat=64, num_grow_ch=32, x4) with the Real-ESRGAN anime weights."""
    import torch
    from torch import nn
    import torch.nn.functional as F

    class RDB(nn.Module):
        def __init__(self, nf=64, gc=32):
            super().__init__()
            self.conv1 = nn.Conv2d(nf, gc, 3, 1, 1)
            self.conv2 = nn.Conv2d(nf + gc, gc, 3, 1, 1)
            self.conv3 = nn.Conv2d(nf + 2 * gc, gc, 3, 1, 1)
            self.conv4 = nn.Conv2d(nf + 3 * gc, gc, 3, 1, 1)
            self.conv5 = nn.Conv2d(nf + 4 * gc, nf, 3, 1, 1)
            self.lrelu = nn.LeakyReLU(0.2, inplace=True)

        def forward(self, x):
            x1 = self.lrelu(self.conv1(x))
            x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
            x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
            x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
            x5 = self.conv5(torch.cat((x, x1, x2, x3, x4), 1))
            return x5 * 0.2 + x

    class RRDB(nn.Module):
        def __init__(self, nf=64, gc=32):
            super().__init__()
            self.rdb1, self.rdb2, self.rdb3 = RDB(nf, gc), RDB(nf, gc), RDB(nf, gc)

        def forward(self, x):
            return self.rdb3(self.rdb2(self.rdb1(x))) * 0.2 + x

    class RRDBNet(nn.Module):
        def __init__(self, nb=6, nf=64, gc=32):
            super().__init__()
            self.conv_first = nn.Conv2d(3, nf, 3, 1, 1)
            self.body = nn.Sequential(*[RRDB(nf, gc) for _ in range(nb)])
            self.conv_body = nn.Conv2d(nf, nf, 3, 1, 1)
            self.conv_up1 = nn.Conv2d(nf, nf, 3, 1, 1)
            self.conv_up2 = nn.Conv2d(nf, nf, 3, 1, 1)
            self.conv_hr = nn.Conv2d(nf, nf, 3, 1, 1)
            self.conv_last = nn.Conv2d(nf, 3, 3, 1, 1)
            self.lrelu = nn.LeakyReLU(0.2, inplace=True)

        def forward(self, x):
            feat = self.conv_first(x)
            feat = feat + self.conv_body(self.body(feat))
            feat = self.lrelu(self.conv_up1(F.interpolate(feat, scale_factor=2, mode="nearest")))
            feat = self.lrelu(self.conv_up2(F.interpolate(feat, scale_factor=2, mode="nearest")))
            return self.conv_last(self.lrelu(self.conv_hr(feat)))

    net = RRDBNet()
    state = torch.load(path, map_location="cpu", weights_only=True)
    net.load_state_dict(state["params_ema"] if "params_ema" in state else state, strict=True)
    net.eval()
    return net


def _esrgan_up(bgr):
    import torch
    x = torch.from_numpy(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0).permute(2, 0, 1)[None]
    with torch.no_grad():
        y = _esr(x)[0].clamp(0, 1).permute(1, 2, 0).numpy()
    return cv2.cvtColor((y * 255 + 0.5).astype(np.uint8), cv2.COLOR_RGB2BGR)


def to_layer(crop, big, mask, scale):
    """Masked crop -> cleaned BGRA float layer (premultiplied) at `scale` canvas px per sheet px + its offset (bbox
    origin in crop px). `big` is the 4x super-resolved crop the colour comes from; the alpha is cut at sheet
    resolution (paper fringe removed) and upsampled."""
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        return None
    x0, x1, y0, y1 = xs.min() - 2, xs.max() + 3, ys.min() - 2, ys.max() + 3
    x0, y0 = max(0, x0), max(0, y0)
    x1, y1 = min(mask.shape[1], x1), min(mask.shape[0], y1)
    a = mask[y0:y1, x0:x1].astype(np.float32)
    w4, h4 = (x1 - x0) * 4, (y1 - y0) * 4
    rgb4 = big[y0 * 4:y1 * 4, x0 * 4:x1 * 4].astype(np.float32) / 255.0
    # matte: the sheet-resolution mask, upsampled once to the 4x super-resolved colour and hardened
    a4 = cv2.resize(cv2.GaussianBlur(a, (3, 3), 0.5), (w4, h4), interpolation=cv2.INTER_LINEAR)
    a4 = smoothstep(0.35, 0.65, a4)
    # paper fringe: within ~2 sheet px of the outer edge the colour is a blend with the white paper. Fading those
    # pixels out (the old fix) still left a pale rim on a dark stage, so their colour is replaced by the nearest
    # colour from inside the outline (colour decontamination) and only the alpha keeps the soft edge.
    solid = (a4 > 0.5).astype(np.uint8)
    inner = cv2.erode(solid, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9)))
    mn, mx = rgb4.min(axis=2), rgb4.max(axis=2)
    paper = np.clip((mn - 0.72) / 0.12, 0, 1) * np.clip(1 - (mx - mn) / 0.20, 0, 1)
    rim = (a4 > 0) & (inner == 0)
    if inner.any() and rim.any():
        from scipy import ndimage as ndi
        _, (iy, ix) = ndi.distance_transform_edt(inner == 0, return_indices=True)
        near = rgb4[iy, ix]
        k = (paper * rim)[..., None]
        rgb4 = rgb4 * (1 - k) + near * k
        a4 = a4 * (1 - 0.6 * paper * rim * (1 - solid))      # the outermost soft pixels: thinner, never white
    # one resample from the 4x colour straight to the frame scale (area filter when shrinking, as it is at 30 fps
    # frame size: about 3.3 frame px per sheet px, i.e. 0.83x of the 4x image)
    nw, nh = max(1, int(round((x1 - x0) * scale))), max(1, int(round((y1 - y0) * scale)))
    interp = cv2.INTER_AREA if nw < w4 else cv2.INTER_CUBIC
    lay = np.clip(cv2.resize(np.dstack([rgb4 * a4[..., None], a4]), (nw, nh), interpolation=interp), 0, 1)
    # crisp lines at the final size: darken-only unsharp on luminance (no light halos), ink re-darkened, alpha
    # edge hardened
    A = lay[..., 3]
    rgb = lay[..., :3] / np.maximum(A, 1e-4)[..., None]
    L = rgb.mean(axis=2)
    d = L - cv2.GaussianBlur(L, (0, 0), 0.8)
    L2 = L + 0.7 * np.minimum(d, 0) + 0.15 * np.maximum(d, 0)
    L2 = L2 * (1 - 0.35 * np.clip((0.40 - L2) / 0.40, 0, 1))
    rgb = np.clip(rgb * (np.clip(L2, 0, 1) / np.maximum(L, 1e-4))[..., None], 0, 1)
    A = smoothstep(0.2, 0.8, A)
    return np.dstack([rgb * A[..., None], A]).astype(np.float32), (x0, y0)


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def interior_holes(layer, min_px=6):
    """Areas (frame px) of transparent pockets fully enclosed by the layer's opaque part, largest first."""
    if layer is None:
        return []
    solid = (layer[0][..., 3] > 0.5).astype(np.uint8)
    holes = fill_holes(solid) - solid
    n, _, st, _ = cv2.connectedComponentsWithStats(holes.astype(np.uint8), 4)
    return sorted((int(st[i, 4]) for i in range(1, n) if st[i, 4] > min_px), reverse=True)


class Pose:
    """One cut pose: figure + effect layers in a shared coordinate system (crop px), anchored at the feet."""

    def __init__(self, crop, big, fig, eff, scale, flip):
        ys, xs = np.nonzero(fig)
        self.bottom = ys.max()
        band = ys > ys.max() - max(4, 0.22 * (ys.max() - ys.min()))
        self.anchor_x = xs[band].mean()
        self.height = ys.max() - ys.min()
        self.scale = scale
        self.flip = flip
        self.fig = to_layer(crop, big, fig, scale)
        self.eff = to_layer(crop, big, eff, scale) if eff.any() else None


def blit(canvas, layer, x, y, alpha=1.0):
    """Premultiplied 'over' of layer at integer (x, y) (top-left, canvas px)."""
    lh, lw = layer.shape[:2]
    ch, cw = canvas.shape[:2]
    cx0, cy0 = max(0, x), max(0, y)
    cx1, cy1 = min(cw, x + lw), min(ch, y + lh)
    if cx0 >= cx1 or cy0 >= cy1:
        return
    src = layer[cy0 - y:cy1 - y, cx0 - x:cx1 - x] * alpha
    dst = canvas[cy0:cy1, cx0:cx1]
    canvas[cy0:cy1, cx0:cx1] = src + dst * (1 - src[..., 3:4])


def pose_canvas(p, eff_a=0.0, fig_a=1.0):
    """Pose drawn on a 2x canvas (feet anchor at the frame's pivot), effect behind the figure."""
    c = np.zeros((H * SS, W * SS, 4), np.float32)
    ax, ay = W * SS / 2, FEET_Y * SS
    for lay, a in ((p.eff, eff_a), (p.fig, fig_a)):
        if lay is None or a <= 0:
            continue
        img, (ox, oy) = lay
        if p.flip:
            img = img[:, ::-1]
        s = p.scale
        if not p.flip:
            x = ax + (ox - p.anchor_x) * s
        else:
            x = ax - (ox + img.shape[1] / s - p.anchor_x) * s
        y = ay + (oy - p.bottom - 1) * s
        blit(c, img, int(round(x)), int(round(y)), a)
    return c


def transform(c, rot=0.0, sx=1.0, sy=1.0, dx=0.0, dy=0.0, pivot=None):
    """Rotate (deg, + = lean back, counter-clockwise when facing right) / squash about `pivot` (canvas px; default
    the feet, so grounded poses keep their feet planted), then shift."""
    if rot == 0.0 and sx == 1.0 and sy == 1.0 and dx == 0.0 and dy == 0.0:
        return c
    ax, ay = pivot if pivot is not None else (W * SS / 2, FEET_Y * SS)
    r = math.radians(rot)
    m = np.array([[sx * math.cos(r), sy * math.sin(r), 0], [-sx * math.sin(r), sy * math.cos(r), 0]], np.float32)
    m[:, 2] = np.array([ax, ay]) - m[:, :2] @ np.array([ax, ay]) + np.array([dx * SS, dy * SS])
    out = cv2.warpAffine(c, m, (c.shape[1], c.shape[0]), flags=cv2.INTER_CUBIC, borderValue=(0, 0, 0, 0))
    out[..., 3] = np.clip(out[..., 3], 0, 1)
    out[..., :3] = np.clip(out[..., :3], 0, out[..., 3:4])        # cubic overshoot: keep it premultiplied
    return out


def over(a, b, alpha=1.0):
    """a over b."""
    return a * alpha + b * (1 - a[..., 3:4] * alpha)


def mix(a, b, t):
    """Premultiplied cross-dissolve a -> b."""
    return a * (1 - t) + b * t


def smear(c, vec, length):
    """Directional motion blur of a premultiplied canvas: a trail `length` canvas px long behind `vec`."""
    k = int(length) * 2 + 1
    ker = np.zeros((k, k), np.float32)
    cx = cy = k // 2
    cv2.line(ker, (cx, cy), (int(round(cx + vec[0] * length)), int(round(cy + vec[1] * length))), 1.0, 1)
    ker /= ker.sum()
    return cv2.filter2D(c, -1, ker, anchor=(cx, cy), borderType=cv2.BORDER_CONSTANT)


def strike_vector(p):
    """Unit direction of the strike in canvas coordinates: from the figure's centre towards its drawn effect."""
    if p.eff is None:
        return (1.0, 0.0)
    fig = pose_canvas(p)[..., 3]
    eff = pose_canvas(p, eff_a=1.0, fig_a=0)[..., 3]
    ys, xs = np.nonzero(fig > 0.5)
    ey, ex = np.nonzero(eff > 0.3)
    if len(xs) == 0 or len(ex) == 0:
        return (1.0, 0.0)
    v = np.array([ex.mean() - xs.mean(), ey.mean() - ys.mean()], np.float32)
    n = float(np.linalg.norm(v))
    return (1.0, 0.0) if n < 1e-3 else (float(v[0] / n), float(v[1] / n))


def finish(c):
    small = c if c.shape[:2] == (H, W) else cv2.resize(c, (W, H), interpolation=cv2.INTER_AREA)
    a = np.clip(small[..., 3:4], 1e-6, 1)
    rgb = np.where(small[..., 3:4] > 1e-4, small[..., :3] / a, 0)
    # effects that run past the frame fade out over the outer 32 px instead of ending in a hard cut
    xs = np.arange(W, dtype=np.float32)
    edge = np.clip(np.minimum(xs, W - 1 - xs) / 32.0, 0, 1)
    out = np.dstack([np.clip(rgb, 0, 1), np.clip(small[..., 3], 0, 1) * edge[None, :]])
    return (out * 255 + 0.5).astype(np.uint8)

# ------------------------------------------------------------------------------------------------ clips
# All clips are 30 fps (the idle and guard loops 15 fps) with eased in-betweens. The root never travels: grounded
# poses rotate and squash about the feet (planted), airborne ones about the hips; direction reads from lean, rotation
# and the effect arc. Squash / stretch stays within +-6 %.
#
# Move clip (MOVE_FRAMES): 0..STRIKE-1 = startup (ease out of idle into the first drawing, wind-up away from the
# strike, smear), STRIKE..RECOVER-1 = active (strike pose held, effect layer lagging a frame and fading),
# RECOVER.. = recovery (eased dissolve into Idle_Sheet frame 0). FighterVisual remaps the clip so STRIKE lands on the
# move's first active tick and RECOVER on its first recovery tick (movesets.csv startup / active / recovery).
FPS = 30
MOVE_FRAMES = 18
STRIKE = 6
RECOVER = 12
IDLE_FRAMES = 24
IDLE_FPS = 15

# weight: heavier characters squash deeper and settle slower (Bruna, Taro); Kaia and Lyra are snappier
WEIGHT = {"kaia": 0.8, "lyra": 0.8, "ren": 1.0, "bruna": 1.35, "taro": 1.2}


def ease_io(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3 - 2 * t)


def ease_out(t):
    t = max(0.0, min(1.0, t))
    return 1 - (1 - t) ** 3


def settle(t, w):
    """Recovery curve: heavier = slower start (weight lingers), always eased."""
    return ease_io(max(0.0, min(1.0, t)) ** w)


def hips(c):
    """Canvas point to rotate airborne poses about: the centre of the figure's opaque pixels."""
    ys, xs = np.nonzero(c[..., 3] > 0.5)
    if len(xs) == 0:
        return (W * SS / 2, FEET_Y * SS)
    return (float(xs.mean()), float(ys.mean()))


def tilt_of(vec):
    """Lean (deg, + = back) that sends the body along the strike: forward / down lean in, up / back lean back."""
    return float(np.clip(4.0 * (-vec[1]) - 2.5 * vec[0], -6.0, 6.0))


def flash(c, k):
    """Hit flash: blend the colour towards white, keeping alpha."""
    out = c.copy()
    out[..., :3] = out[..., :3] * (1 - k) + out[..., 3:4] * k
    return out


def strike_index(poses):
    """Which drawing of a panel is the hit: the one carrying the most drawn effect (the slash / impact is drawn on
    the striking pose; panels read wind-up -> strike -> follow-through, but not always in that order), else the last."""
    best, area = len(poses) - 1, 0.0
    for i, p in enumerate(poses):
        a = float(p.eff[0][..., 3].sum()) if p.eff is not None else 0.0
        if a > area * 1.15:
            best, area = i, a
    return best


def recovery_frames(n, baseS, tilt, idle0, w, pivS=None, follow=None):
    """The n recovery frames, pose to pose, never a cross-dissolve (two drawings blended read as a ghost):
    the follow-through drawing (or the strike drawing) is HELD while its lean eases out, then the idle drawing takes
    over on a settle with a small squash that eases out (heavier = deeper, slower)."""
    out = []
    hold = max(1, int(round(n * 0.55)))
    src = follow if follow is not None else baseS
    for i in range(hold):
        t = ease_out((i + 1) / float(hold))
        out.append(transform(src, tilt * 0.35 * (1 - t), pivot=pivS))
    sq = 0.03 * w
    m = n - hold
    for i in range(m):
        t = (i + 1) / float(m)
        k = 1 - ease_out(t)
        out.append(transform(idle0, 0.0, 1 + sq * k, 1 - sq * k))
    return out


def move_frames(poses, idle0, w=1.0, air=False):
    """MOVE_FRAMES premultiplied canvases from 1-3 drawn poses; idle0 = canvas of Idle_Sheet frame 0.
    Pose to pose (decision 2026-11-13): every frame shows exactly ONE drawing, moved by an eased transform. Drawings
    are cut between, never cross-dissolved: startup = the wind-up drawing(s) held while the body loads up against
    the strike (or the strike drawing pulled back when the sheet has no wind-up), one smear frame, the strike held
    through the active frames, then the follow-through drawing (if any) and a settle into idle."""
    s = strike_index(poses)
    pre, strike, post = poses[:s], poses[s], poses[s + 1:]
    baseS = pose_canvas(strike)
    effS = pose_canvas(strike, eff_a=1.0, fig_a=0) if strike.eff is not None else None
    vec = strike_vector(strike)
    tilt = tilt_of(vec)
    pivS = hips(baseS) if air else None
    sq = 0.03 * w                                  # squash depth (<= 0.045)
    frames = []
    # startup (5 frames): wind up against the strike direction, eased; each wind-up drawing is held, then cut
    for i in range(5):
        k = ease_io((i + 1) / 5.0)
        if pre:
            src = pre[min(len(pre) - 1, i * len(pre) // 5)]
            c0 = pose_canvas(src)
            c = transform(c0, -tilt * 1.5 * k, 1 + sq * k, 1 - sq * k, -vec[0] * 3 * k, -vec[1] * 3 * k,
                          pivot=hips(c0) if air else None)
        else:
            c = transform(baseS, -tilt * 2.2 * k, 1 + sq * k, 1 - sq * k, -vec[0] * 9 * k, -vec[1] * 9 * k,
                          pivot=pivS)
        frames.append(c)
    # smear: the strike drawing, motion-blurred back along the strike at 60 % behind a sharp, stretched copy
    sharp = transform(baseS, tilt * 0.5, 1 - sq * 0.6, 1 + sq * 0.6, pivot=pivS)
    c = over(smear(sharp, (-vec[0], -vec[1]), 13 * SS), np.zeros_like(baseS), 0.6)
    if effS is not None:
        c = over(transform(effS, 0, 0.9, 0.9, -vec[0] * 10, -vec[1] * 10, pivot=hips(baseS)), c, 0.35)
    frames.append(over(sharp, c))

    # active: stretch on the hit, squash next, settle; the effect lags one frame behind the body (overlap)
    body = [(tilt, 1 - sq, 1 + sq), (tilt * 0.8, 1 + sq * 0.8, 1 - sq * 0.8), (tilt * 0.6, 1, 1),
            (tilt * 0.45, 1, 1), (tilt * 0.3, 1, 1), (tilt * 0.2, 1, 1)]
    fx = [(0.8, 0.94, -5), (1.0, 1.0, 0), (0.9, 1.02, 2), (0.65, 1.03, 3), (0.4, 1.04, 4), (0.18, 1.05, 5)]
    for (r, sx, sy), (ea, es, lag) in zip(body, fx):
        c = np.zeros_like(baseS)
        if effS is not None:
            c = over(transform(effS, 0, es, es, vec[0] * lag, vec[1] * lag, pivot=hips(baseS)), c, ea)
        frames.append(over(transform(baseS, r, sx, sy, pivot=pivS), c))
    # recovery: follow-through drawing (or the strike) held, then cut to idle with a settle
    follow = pose_canvas(post[0]) if post else None
    frames += recovery_frames(MOVE_FRAMES - RECOVER, transform(baseS, tilt * 0.2, pivot=pivS), tilt, idle0, w,
                              pivS, follow)
    assert len(frames) == MOVE_FRAMES
    return frames


def jab_frames(poses, idle0, w=1.0):
    """Jab 1-2-3 in one clip (18 frames, 6 per hit): ease in, strike, short hold, chain into the next drawing.
    Returns (frames, strike frame of the first hit, first frame after the third hit's strike)."""
    cs = [pose_canvas(p) for p in poses]
    es = [pose_canvas(p, eff_a=1.0, fig_a=0) if p.eff is not None else None for p in poses]
    sq = 0.025 * w
    frames = []
    for h, (c, e) in enumerate(zip(cs, es)):
        vec = strike_vector(poses[h])
        tilt = tilt_of(vec)
        # anticipation: this hit's drawing pulled back against the strike (a cut, not a blend with the last pose)
        frames.append(transform(c, -tilt * 1.6, 1 + sq, 1 - sq, -vec[0] * 7, -vec[1] * 7))
        frames.append(over(transform(c, -tilt * 0.5, 1 + sq * 0.5, 1 - sq * 0.5),
                           over(smear(c, (-vec[0], -vec[1]), 10), np.zeros_like(c), 0.5)))
        for k, (ea, r, sx, sy) in enumerate([(1.0, tilt, 1 - sq, 1 + sq), (0.85, tilt * 0.6, 1 + sq * 0.6, 1 - sq * 0.6),
                                             (0.5, tilt * 0.3, 1, 1), (0.2, 0.0, 1, 1)]):
            f = np.zeros_like(c)
            if e is not None:
                f = over(e, f, ea)
            frames.append(over(transform(c, r, sx, sy), f))
    # the last hit's tail settles into the idle drawing (cut + eased squash, no blend)
    frames[-2:] = recovery_frames(2, cs[-1], 0.0, idle0, w)[-2:]
    return frames, 2, 14


def idle_frames(pose):
    """Seamless breathing loop (sin over the whole loop, frame 0 = the untransformed rest pose, feet planted)."""
    base = pose_canvas(pose)
    out = [base]
    for i in range(1, IDLE_FRAMES):
        k = math.sin(2 * math.pi * i / IDLE_FRAMES)
        out.append(transform(base, 0.4 * k, 1.0 - 0.008 * k, 1.0 + 0.016 * k, 0))
    return out


RUN_FRAMES = 12


def run_frames(pose, w=1.0):
    """Seamless run cycle (two strides per loop): forward lean, stride bob, squash on contact, hips sway."""
    base = pose_canvas(pose)
    piv = hips(base)
    out = []
    for i in range(RUN_FRAMES):
        ph = 2 * math.pi * i / RUN_FRAMES
        contact = 0.5 + 0.5 * math.cos(2 * ph)          # 1 at each foot contact
        sq = 0.035 * w * contact
        bob = -(7.0 / w) * (1 - contact)                # up between contacts (heavier = lower)
        rot = -5.0 + 3.0 * math.sin(ph)                 # lean into the run, hips sway with the stride
        out.append(transform(base, rot, 1 + sq * 0.6, 1 - sq, 0, bob, pivot=(W * SS / 2, FEET_Y * SS)))
    return out


def jump_frames(pose, w=1.0):
    """Take-off: crouch squash, stretch on the rise, ease to the airborne pose (one-shot, 8 frames)."""
    base = pose_canvas(pose)
    piv = hips(base)
    keys = [(0.0, 1.04, 0.95), (2.0, 0.97, 1.05), (3.0, 0.96, 1.06), (2.5, 0.97, 1.04),
            (1.8, 0.985, 1.02), (1.0, 0.995, 1.01), (0.5, 1.0, 1.0), (0.0, 1.0, 1.0)]
    return [transform(base, r, sx, sy, pivot=piv) for r, sx, sy in keys]


def fall_frames(pose, w=1.0):
    """Airborne fall loop (8 frames): slight tuck, gentle sway of the hips; seamless."""
    base = pose_canvas(pose)
    piv = hips(base)
    out = []
    for i in range(8):
        ph = 2 * math.pi * i / 8
        out.append(transform(base, -2.0 + 1.5 * math.sin(ph), 1.0 + 0.012 * math.cos(ph), 0.975 - 0.012 * math.cos(ph),
                             pivot=piv))
    return out


GUARD_FRAMES = 16


def guard_frames(pose, w=1.0):
    """Guard loop (15 fps): braced stance with a small breathing sway, feet planted; seamless."""
    base = pose_canvas(pose, eff_a=0.0)
    out = []
    for i in range(GUARD_FRAMES):
        k = math.sin(2 * math.pi * i / GUARD_FRAMES)
        out.append(transform(base, 0.5 * k, 1.0 + 0.006 * k, 1.0 - 0.012 * k))
    return out


def hit_frames(pose, w=1.0):
    """Impact: sharp recoil away (lean back ~15 deg, squash, white flash), then a slower eased recovery."""
    base = pose_canvas(pose)
    peak = 15.0 + 2.0 * (1.0 - w)                     # light characters fly back a touch more
    keys = [(peak, 1.04, 0.95, 0.45), (peak * 1.07, 1.05, 0.94, 0.12), (peak, 1.04, 0.96, 0.0)]
    out = [flash(transform(base, r, sx, sy), f) for r, sx, sy, f in keys]
    n = 5
    for i in range(1, n + 1):
        t = settle(i / n, w)
        out.append(transform(base, peak * (1 - t), 1 + 0.04 * (1 - t), 1 - 0.04 * (1 - t)))
    return out


def launch_frames(pose, w=1.0):
    """Tumble loop (12 frames): a full spin about the hips, slightly stretched along the flight; seamless."""
    base = pose_canvas(pose)
    piv = hips(base)
    return [transform(base, -360.0 * i / 12, 0.97, 1.04, pivot=piv) for i in range(12)]


def ko_frames(pose, w=1.0):
    """KO: the tumbling launch pose flying off up and back, shrinking and fading (one-shot, 14 frames)."""
    base = pose_canvas(pose)
    piv = hips(base)
    out = []
    for i in range(14):
        t = i / 13.0
        e = ease_out(t)
        c = transform(base, -540.0 * e, 0.97 * (1 - 0.35 * e), 1.04 * (1 - 0.35 * e), -120 * e, -150 * e, pivot=piv)
        out.append(c * (1 - ease_io((t - 0.35) / 0.65)))
    return out


ULT_FRAMES = 24
ULT_STRIKE = 12
ULT_RECOVER = 18


def ult_frames(poses, idle0, w=1.0):
    """Ultimate: a long charge (wind-up with the effect pulsing in), smear, strike with the full effect, hold,
    eased return to idle. 24 frames; strike 12, recovery 18."""
    s = strike_index(poses)
    strike = poses[s]
    first = poses[0] if s > 0 else strike
    base0, baseS = pose_canvas(first), pose_canvas(strike)
    effS = pose_canvas(strike, eff_a=1.0, fig_a=0) if strike.eff is not None else None
    vec = strike_vector(strike)
    tilt = tilt_of(vec)
    sq = 0.04 * w
    out = []
    for i in range(11):                                   # charge
        k = ease_io((i + 1) / 11.0)
        pull = 0.0 if s > 0 else 10.0                     # no wind-up drawing: the strike drawing pulled back
        c = transform(base0, -tilt * 1.8 * k, 1 + sq * k, 1 - sq * k, -vec[0] * pull * k, -vec[1] * pull * k)
        if effS is not None and i >= 3:                   # the effect gathers: small and faint -> bigger
            g = (i - 3) / 7.0
            c = over(c, transform(effS, 0, 0.5 + 0.3 * g, 0.5 + 0.3 * g, pivot=hips(baseS)),
                     0.15 + 0.25 * g * (0.75 + 0.25 * math.sin(i * 1.7)))
        out.append(flash(c, 0.25 * max(0, (i - 7) / 3.0)))
    sharp = transform(baseS, tilt * 0.5, 1 - sq * 0.6, 1 + sq * 0.6)
    out.append(over(sharp, over(smear(sharp, (-vec[0], -vec[1]), 16), np.zeros_like(sharp), 0.6)))   # 11: smear
    for k, (r, sx, sy, ea) in enumerate([(tilt, 1 - sq, 1 + sq, 1.0), (tilt * 0.8, 1 + sq * 0.8, 1 - sq * 0.8, 1.0),
                                         (tilt * 0.6, 1, 1, 0.95), (tilt * 0.45, 1, 1, 0.8), (tilt * 0.3, 1, 1, 0.55),
                                         (tilt * 0.2, 1, 1, 0.3)]):
        c = np.zeros_like(baseS)
        if effS is not None:
            c = over(transform(effS, 0, 1 + 0.02 * k, 1 + 0.02 * k, pivot=hips(baseS)), c, ea)
        f = over(transform(baseS, r, sx, sy), c)
        out.append(flash(f, 0.5) if k == 0 else f)
    post = poses[s + 1:]
    out += recovery_frames(ULT_FRAMES - ULT_RECOVER, transform(baseS, tilt * 0.2), tilt, idle0, w,
                           follow=pose_canvas(post[0]) if post else None)
    assert len(out) == ULT_FRAMES
    return out


# Sheet_* state clips: which drawing each one uses, per character (panel key, pose index). All side view.
#   run: a running / dashing drawing; jump & fall: airborne drawings; guard: a braced or parry stance;
#   hit / launch / KO use the idle drawing (hit) and an airborne drawing (tumble); ultimate: the strongest special.
STATE_SRC = {
    "kaia": {"run": (5, 0), "jump": (11, 0), "fall": (22, -1), "guard": (20, 0), "launch": (22, -1), "ult": 10},
    "bruna": {"run": (5, 0), "jump": (9, 0), "fall": (12, 0), "guard": (16, 0), "launch": (13, 0), "ult": 20},
    "ren": {"run": ("move", 2), "jump": (9, 1), "fall": (12, 0), "guard": (16, 0), "launch": (10, 0), "ult": 14},
    "lyra": {"run": ("move", 2), "jump": ("move", 4), "fall": ("move", 5), "guard": ("guard", 0),
             "launch": ("dodge", -1), "ult": 24},
    "taro": {"run": (5, 0), "jump": (9, 0), "fall": (12, 0), "guard": (16, 1), "launch": (10, 0), "ult": 19},
}
# name, frames, fps (strike / recover -1: not an attack)
STATE_CLIPS = [("Sheet_Run", RUN_FRAMES, FPS), ("Sheet_Jump", 8, FPS), ("Sheet_Fall", 8, FPS),
               ("Sheet_Guard", GUARD_FRAMES, IDLE_FPS), ("Sheet_Hit", 8, FPS), ("Sheet_Launch", 12, FPS),
               ("Sheet_KO", 14, FPS)]


GRID_COLS = 6          # 6 x 640 = 3840 px wide; 18 frames = 3 rows (768 px), 24 = 4 rows (1024 px): all <= 4096


def strip(frames, cols=GRID_COLS):
    """Frames -> grid sheet, left to right then top to bottom, empty cells transparent."""
    fs = [finish(f) for f in frames]
    rows = (len(fs) + cols - 1) // cols
    out = np.zeros((rows * H, cols * W, 4), np.uint8)
    for i, f in enumerate(fs):
        r, c = divmod(i, cols)
        out[r * H:(r + 1) * H, c * W:(c + 1) * W] = f
    return out


def _put(path, data):
    """Atomic write that survives the open Unity editor: write to '<path>.tmp~' (Unity ignores names ending in '~'),
    then swap it in, retrying while the editor holds the file during an import."""
    tmp = path + ".tmp~"
    with open(tmp, "wb") as f:
        f.write(data)
    for i in range(60):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            time.sleep(0.5)
    raise PermissionError(path)


def save(path, bgra):
    """BGRA grid sheet -> PNG, with an anim .meta written first: NO mipmaps, bilinear-trilinear filter, max size 4096,
    high-quality compression (BC7 on desktop).

    Mipmaps are off on purpose (2026-11-22, replaces art review M7): Unity does not block-compress mipmapped
    non-power-of-two textures, so with mipmaps on every sheet shipped as RGBA32 (5x the memory, 1.1 GB in total)."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if in_assets(path):
        text = _TEMPLATE.format(guid=_guid(path), mip=0, size=4096)
        text = text.replace("    filterMode: 1", "    filterMode: 2")
        # frames are drawn on screen at 0.5-0.9x: trilinear alone blends in mip 1 and softens the ink; a negative
        # bias keeps the sharper level until the sprite is really small
        text = text.replace("    mipBias: 0", "    mipBias: -0.6")
        _put(path + ".meta", text.encode("utf-8"))
    ok, png = cv2.imencode(".png", bgra, [cv2.IMWRITE_PNG_COMPRESSION, 9])
    assert ok, path
    _put(path, png.tobytes())


_TEXT_META = ("fileFormatVersion: 2\nguid: {guid}\nTextScriptImporter:\n  externalObjects: {{}}\n  userData: \n"
              "  assetBundleName: \n  assetBundleVariant: \n")


def save_table(folder, rows):
    """clips.txt (TextAsset): name frameCount fps strikeFrame recoverFrame columns, read by SpriteAnimLibrary."""
    path = os.path.join(folder, "clips.txt")
    if in_assets(path):
        _put(path + ".meta", _TEXT_META.format(guid=_guid(path)).encode("utf-8"))
    lines = ["# generated by tools/art/sheet_anims.py: name frameCount fps strikeFrame recoverFrame columns"]
    lines += ["%s %d %d %d %d %d" % (r + (GRID_COLS,)) for r in rows]
    _put(path, ("\n".join(lines) + "\n").encode("utf-8"))


# ------------------------------------------------------------------------------------------------ main


def char_scale(sheet, char):
    """Sheet px -> frame px (x SS), from the idle drawing (standing height -> FIG) and the move poses."""
    crop, fig, eff = segment(sheet, PANELS[char]["idle"])
    poses = split_poses(fig, eff)
    ys, _ = np.nonzero(poses[0][0])
    idle_h = ys.max() - ys.min()
    return FIG * SS / idle_h, (crop, poses[0])


# One scale per character: the standing height (sheet px) of the character at the size the MOVE panels draw it.
# The idle panels of Kaia, Bruna and Taro are drawn larger than their move panels, so the idle height cannot be used
# directly; these are measured from the upright move poses (Kaia/Lyra 60, Bruna 68, Taro 76) so that a standing pose
# fills FigureFill like the cutout clips and crouched / aerial poses stay proportionally smaller.
# Ren's sheet draws each section at its own size, so his reference is per panel range.
STAND_H = {"kaia": 60.0, "bruna": 68.0, "lyra": 60.0, "taro": 76.0,
           "ren": {range(2, 9): 84.0, range(9, 14): 62.0, range(14, 18): 56.0, range(18, 23): 46.0}}
def stand_h(c, num):
    v = STAND_H[c]
    if isinstance(v, dict):
        for r, h in v.items():
            if num in r:
                return h
        return max(v.values())
    return v


def decide_facing(cut, c):
    """Per pose: (mirror?, note). Every body on the five sheets is drawn facing right (checked pose by pose; a
    weapon or arc trailing behind is a follow-through, not a turned body, and back moves aim behind on purpose), so
    nothing is mirrored unless TWEAKS sets "flip" (bool, or the list of pose indices drawn facing left). An automatic
    head-match test was tried and rejected: it mirrored correct poses (heads in profile are too small to match)."""
    flips = {}
    for num, (crop, ps, tw) in cut.items():
        f = tw.get("flip", False)
        for k in range(len(ps)):
            flips[(num, k)] = ((k in f) if isinstance(f, (list, tuple)) else bool(f), "")
    return flips


def flip_check(img, n):
    """Consecutive frames whose alpha matches the mirrored neighbour better than the neighbour itself."""
    al = []
    for i in range(n):
        r, c = divmod(i, GRID_COLS)
        al.append(img[r * H:(r + 1) * H, c * W:(c + 1) * W, 3].astype(np.float32))
    bad = []
    for i in range(n - 1):
        a, b = al[i] - al[i].mean(), al[i + 1] - al[i + 1].mean()
        bm = b[:, ::-1]
        na = np.linalg.norm(a) * np.linalg.norm(b) + 1e-6
        if float((a * bm).sum()) / na > float((a * b).sum()) / na + 0.08:
            bad.append(i + 1)
    return bad


def csv_air():
    """CSV id -> True when the move is aerial only (ground_air == 'A')."""
    import csv
    out = {}
    with open(os.path.join(ROOT, "design", "gdd", "movesets", "movesets.csv"), encoding="utf-8") as f:
        for r in csv.DictReader(f):
            out[r["id"]] = r["ground_air"].strip().upper() == "A"
    return out


def run(chars, debug=None, only=None):
    moves = load_moves()
    air = csv_air()
    report = {}
    for c in chars:
        sheet = cv2.imread(os.path.join(SHEETS, c + "-moveset.png"))
        cid = IDS[c]
        w = WEIGHT[c]
        folder = os.path.join(OUT, cid)
        os.makedirs(folder, exist_ok=True)
        write_folder_meta(folder)
        idle_scale, (icrop, (ifig, ieff)) = char_scale(sheet, c)
        tw = TWEAKS.get((c, "idle"), {})
        ip = Pose(icrop, upscale4(icrop), ifig, np.zeros_like(ieff), idle_scale, tw.get("flip", False))
        idle = idle_frames(ip)
        table = [("Idle_Sheet", IDLE_FRAMES, IDLE_FPS, -1, -1)]
        if only is None:
            save(os.path.join(folder, "Idle_Sheet.png"), strip(idle))
        cut = {}
        for num, box in PANELS[c].items():
            if num == "idle":
                continue
            tw = TWEAKS.get((c, num), {})
            crop, fig, eff = segment(sheet, box, tw.get("erase", ()))
            fig = cut_gaps(crop, fig, box, tw.get("cut", ()))
            ps = split_poses(fig, eff, tw.get("keep"), tw.get("grow", 7))
            cut[num] = (crop, ps, tw)
        flips = decide_facing(cut, c)
        bigs = {}

        def poses_of(num):
            crop, ps, tw = cut[num]
            if num not in bigs:
                bigs[num] = upscale4(crop)
            # the movement strips are drawn at the idle's size, the move panels at the character's move size
            scale = idle_scale if num == "move" else FIG * SS / stand_h(c, num if isinstance(num, int) else 9)
            return [Pose(crop, bigs[num], f, e, scale, flips[(num, k)][0]) for k, (f, e) in enumerate(ps)]

        made, missing, suspects, holes, states = [], [], [], [], []
        ih = interior_holes(ip.fig)
        if ih:
            holes.append("Idle_Sheet:%s" % ih[:4])

        def emit(name, frames, fps, strike, recover, pose_list=()):
            for p in pose_list:
                hs = interior_holes(p.fig)
                if hs:
                    holes.append("%s:%s" % (name, hs[:4]))
            img = strip(frames)
            if only is None or name in only:
                save(os.path.join(folder, name + ".png"), img)
            table.append((name, len(frames), fps, strike, recover))
            return img

        # moveset moves (CSV ids)
        for num, ids in sorted(moves[c].items()):
            if num not in cut or not cut[num][1]:
                missing.extend(ids)
                continue
            poses = poses_of(num)
            frames = move_frames(poses, idle[0], w, air.get(ids[0], False))
            for mid in ids:
                img = emit("Move_" + mid, frames, FPS, STRIKE, RECOVER, poses if mid == ids[0] else ())
                made.append((mid, len(poses)))
            bad = flip_check(img, MOVE_FRAMES)
            if bad:
                suspects.append("%s@%s" % (ids[0], bad))
            if debug:
                dbg_panel(debug, c, num, cut[num][0], cut[num][1])
        # jab 1-2-3 (panel 1, no CSV row): the whole string, and one clip per hit for the light-combo step
        if 1 in cut and len(cut[1][1]) >= 3:
            jp = poses_of(1)[:3]
            frames, s, r = jab_frames(jp, idle[0], w)
            emit("Move_%s_jab" % c, frames, FPS, s, r, jp)
            for h in range(3):
                emit("Move_%s_jab%d" % (c, h + 1), move_frames(jp[max(0, h - 1):h + 1], idle[0], w), FPS, STRIKE, RECOVER)
            made.append(("%s_jab (+jab1-3)" % c, 3))
        else:
            missing.append("%s_jab" % c)
        # Kaia's air dodge panel (no CSV row: the dash covers it)
        if c == "kaia" and 22 in cut and cut[22][1]:
            emit("Move_kaia_airdodge", move_frames(poses_of(22)[-1:], idle[0], w, True), FPS, STRIKE, RECOVER)
            made.append(("kaia_airdodge", 1))
        # Sheet_* fight states, side-view sheet drawings only
        src = STATE_SRC[c]

        def pick(key):
            num, k = src[key]
            ps = poses_of(num)
            return ps[k if k < len(ps) else -1]

        builders = {"Sheet_Run": lambda: run_frames(pick("run"), w), "Sheet_Jump": lambda: jump_frames(pick("jump"), w),
                    "Sheet_Fall": lambda: fall_frames(pick("fall"), w), "Sheet_Guard": lambda: guard_frames(pick("guard"), w),
                    "Sheet_Hit": lambda: hit_frames(ip, w), "Sheet_Launch": lambda: launch_frames(pick("launch"), w),
                    "Sheet_KO": lambda: ko_frames(pick("launch"), w)}
        for name, n, fps in STATE_CLIPS:
            frames = builders[name]()
            assert len(frames) == n, (name, len(frames))
            used = [ip] if name == "Sheet_Hit" else [pick({"Sheet_Run": "run", "Sheet_Jump": "jump", "Sheet_Fall": "fall",
                                                           "Sheet_Guard": "guard", "Sheet_Launch": "launch",
                                                           "Sheet_KO": "launch"}[name])]
            emit(name, frames, fps, -1, -1, used)
            states.append(name)
        up = poses_of(src["ult"])
        emit("Sheet_Ultimate", ult_frames(up, idle[0], w), FPS, ULT_STRIKE, ULT_RECOVER, up)
        states.append("Sheet_Ultimate")
        save_table(folder, table)
        report[c] = (made, missing, states)
        print("%s: %d move clips, poses %s, no drawing: %s" % (c, len(made), [n for _, n in made], missing or "none"))
        print("   state clips: %s" % " ".join(states))
        print("   frame-to-frame mirror suspects: %s" % (" ".join(suspects) or "none"))
        print("   interior holes > 6 px in a figure (frame px): %s" % (" ".join(holes) or "none"))
    return report


def dbg_panel(d, c, num, crop, ps):
    os.makedirs(d, exist_ok=True)
    vis = crop.copy()
    cols = [(0, 0, 255), (0, 200, 0), (255, 0, 0), (0, 200, 200)]
    for i, (f, e) in enumerate(ps):
        cnt, _ = cv2.findContours(f, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        cv2.drawContours(vis, cnt, -1, cols[i % 4], 1)
        cnt, _ = cv2.findContours(e, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        cv2.drawContours(vis, cnt, -1, (255, 0, 255), 1)
    cv2.imwrite(os.path.join(d, "%s_%s.png" % (c, num)), cv2.resize(vis, None, fx=2, fy=2))


if __name__ == "__main__":
    args = sys.argv[1:]
    dbg = None
    only = None
    if "--debug" in args:
        i = args.index("--debug")
        dbg = args[i + 1]
        del args[i:i + 2]
    if "--only" in args:
        i = args.index("--only")
        only = set(args[i + 1].split(","))          # clip names, e.g. Move_kaia_ftilt,Sheet_Run
        del args[i:i + 2]
    run([a for a in args if a in IDS] or list(IDS), dbg, only)
