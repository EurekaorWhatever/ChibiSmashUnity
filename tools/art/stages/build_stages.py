"""
Builds the five sheet stages (ST-4) from design/art/maps/0[1-5]-*.jpg:

    Assets/ChibiClash/Resources/Stages/<Id>/Full.png        main illustration, full resolution (3840 x 2160)
    Assets/ChibiClash/Resources/Stages/<Id>/Plat_<Name>.png  one transparent sprite per fighting platform
    Assets/ChibiClash/Resources/Stages/<Id>/Backdrop.png     illustration with the platforms removed and filled
    Assets/ChibiClash/Resources/Stages/<Id>/Foreground.png   front overlay (mist / clouds / front grass), alpha
    Assets/ChibiClash/Resources/Stages/<Id>/Far.png, Mid.png parallax layers from the sheet's layer thumbnails
    Assets/ChibiClash/Resources/Stages/<Id>/Haz_*.png        hazard sprites
    design/gdd/stages/stage-geometry.json                    single source of truth for sim + view geometry

Usage:  python tools/art/stages/build_stages.py [geometry|sprites|all] [StageId ...]
        geometry = Full.png + json (fast); sprites/all = everything.

All platform numbers below were measured on the full-resolution page renders (ink outline of the walkable top:
top = first ink row of the slab, left/right = outer edge of the slab's side outline). Coordinates are illustration
pixels: page pixel minus the PANEL origin. The table is the measurement record; re-measure only if the sheet changes.
"""
import json
import os
import sys
import tempfile
import time

import cv2
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tools", "art"))
import unity_meta  # noqa: E402

MAPS = os.path.join(ROOT, "design", "art", "maps")
OUT = os.path.join(ROOT, "Assets", "ChibiClash", "Resources", "Stages")
JSON_PATH = os.path.join(ROOT, "design", "gdd", "stages", "stage-geometry.json")

# Main illustration panel on every page (page px): x 142..3982, y 577..2737 with a ~4 px rounded corner; we take
# the 3840 x 2160 rect starting one pixel inside.
PANEL = (143, 578, 3840, 2160)

# Per stage: sim index (ST-1), source page, px per world unit, platforms {name: (topPx, leftPx, rightPx)}.
# ppu 106 keeps MAIN <= 16 u and every climb <= a double jump (Ocean T1 -> MAIN 410 px = 3.87 u);
# Bird 160 -> MAIN 15.5 u, blocks 1.73 u above MAIN; Plain 113 -> the 3840 px illustration = 34 u = the
# designer's art rect X -17..17 (ground KO lines at +-15 u fall inside the drawn ground).
STAGES = {
    "Mountain": dict(index=3, page="01-mountain-of-the-beginning.jpg", ppu=106, plats={
        "MAIN": (1374, 1072, 2764),
        "A": (1034, 370, 972),
        "B": (1033, 2870, 3465),
        "C": (784, 1594, 2249),
        "D": (494, 894, 1384),
    }, moving=["A", "B", "D"]),
    "Plain": dict(index=4, page="02-plain-of-unity.jpg", ppu=113, plats={
        # The lit ground strip (dark ink 1579..1585, yellow 1586..1605); the ground runs off both frame edges.
        "MAIN": (1580, 0, 3840),
    }, moving=[]),
    "Volcano": dict(index=5, page="03-volcano-abyss.jpg", ppu=106, plats={
        "MAIN": (1433, 1113, 2724),
        "L1": (1113, 2433, 2925),
        "L2": (853, 1793, 2245),
        "L3": (583, 2253, 2667),   # right end lies on the dark crater wall: measured by eye (inner edge 2653 + ink)
        "L4": (323, 1722, 2085),
    }, moving=[]),
    "BirdLair": dict(index=6, page="04-giant-bird-lair.jpg", ppu=160, plats={
        # Right end = where the slab top disappears under the nest twigs; this also makes MAIN symmetric about the
        # L/R blocks (block centres 1259 / 2519 -> 1889; MAIN centre 1895).
        "MAIN": (1441, 653, 3138),
        "L": (1164, 994, 1524),     # solid trapezoid blocks standing on MAIN (not floating ledges)
        "R": (1164, 2254, 2784),
    }, moving=[]),
    "Ocean": dict(index=7, page="05-abandoned-ocean-monument.jpg", ppu=106, plats={
        "MAIN": (1033, 1113, 2727),
        "T1": (1443, 674, 1446),
        "T2": (1443, 2392, 3164),
        "T3": (1803, 1453, 2385),
    }, moving=[]),
}


# ---------------------------------------------------------------------------------------------------------- helpers
_page_cache = {}


def page(sid):
    if sid not in _page_cache:
        _page_cache[sid] = cv2.imread(os.path.join(MAPS, STAGES[sid]["page"]), cv2.IMREAD_COLOR)
    return _page_cache[sid]


def atomic_write(path, data):
    """Write bytes to path via a temp file outside Assets/ + os.replace, retrying while the open Unity editor holds
    the target (PermissionError on Windows). Unity never sees a half-written file or a stray temp asset."""
    fd, tmp = tempfile.mkstemp(prefix="stagebuild_", suffix=".tmp")
    with os.fdopen(fd, "wb") as f:
        f.write(data)
    for _ in range(40):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            time.sleep(0.25)
    os.remove(tmp)
    raise PermissionError("could not replace %s (locked)" % path)


def write_png(path, img):
    ok, buf = cv2.imencode(".png", img, [cv2.IMWRITE_PNG_COMPRESSION, 6])
    assert ok, path
    atomic_write(path, buf.tobytes())


_illus_cache = {}


def illustration(sid, raw=False):
    """The main illustration. raw=False applies the source repairs every layer shares (Volcano embers, Plain edge
    fade); raw=True is the untouched crop (the fire-cone hazard sprites are cut from it)."""
    key = (sid, raw)
    if key not in _illus_cache:
        im = _illustration_raw(sid)
        if not raw and sid == "Volcano":
            im = tame_embers(im)
        if not raw and sid == "Plain":
            im = fix_plain_edges(im)
        _illus_cache[key] = im
    return _illus_cache[key].copy()


def _illustration_raw(sid):
    x, y, w, h = PANEL
    im = page(sid)[y:y + h, x:x + w].copy()
    # The panel's rounded corners leave a few page-background pixels in the four 8 x 8 corner squares: inpaint them.
    bg = page(sid)[20, 20].astype(int)
    mask = np.zeros(im.shape[:2], np.uint8)
    for cy in (0, h - 8):
        for cx in (0, w - 8):
            sq = im[cy:cy + 8, cx:cx + 8].astype(int)
            mask[cy:cy + 8, cx:cx + 8] = (np.abs(sq - bg).sum(2) < 40).astype(np.uint8) * 255
    if mask.any():
        im = cv2.inpaint(im, mask, 5, cv2.INPAINT_TELEA)
    return im


def origin(sid):
    top, l, r = STAGES[sid]["plats"]["MAIN"]
    return (l + r) // 2, top


def to_milli(sid, px, py):
    """Illustration px -> world milli (x right, y up), world (0,0) = centre of MAIN's walkable top."""
    ox, oy = origin(sid)
    ppu = STAGES[sid]["ppu"]
    return int(round((px - ox) * 1000.0 / ppu)), int(round((oy - py) * 1000.0 / ppu))


def write_meta(path, ppu, pivot_norm):
    """Sprite meta: mipmaps + trilinear (the camera zooms out 2-4x; art review M7), max 8192, no NPOT scaling,
    alpha is transparency, custom pivot, ppu = stage ppu."""
    g = unity_meta._guid(path)
    text = unity_meta._TEMPLATE.format(guid=g, mip=1, size=8192)
    old = "  alignment: 0\n  spritePivot: {x: 0.5, y: 0.5}\n  spritePixelsToUnits: 100\n"
    assert old in text, "unity_meta template changed; update write_meta"
    assert "    filterMode: 1\n" in text
    text = text.replace("    filterMode: 1\n", "    filterMode: 2\n")
    text = text.replace(old, "  alignment: 9\n  spritePivot: {x: %.6f, y: %.6f}\n  spritePixelsToUnits: %s\n"
                        % (pivot_norm[0], pivot_norm[1], ppu))
    atomic_write(path + ".meta", text.encode("utf-8"))


def save_sprite(sid, name, img, pivot_px, ppu=None):
    """Writes Resources/Stages/<sid>/<name>.png (meta first). pivot_px is in the image's own pixels (top-left
    origin, y down). Returns the sprite record for the json (caller adds layer and worldMilli)."""
    folder = os.path.join(OUT, sid)
    os.makedirs(folder, exist_ok=True)
    unity_meta.write_folder_meta(OUT)
    unity_meta.write_folder_meta(folder)
    path = os.path.join(folder, name + ".png")
    ppu = ppu or STAGES[sid]["ppu"]
    h, w = img.shape[:2]
    pn = (pivot_px[0] / float(w), 1.0 - pivot_px[1] / float(h))
    write_meta(path, ppu, pn)
    write_png(path, img)
    return {"file": "Stages/%s/%s" % (sid, name), "sizePx": [w, h], "ppu": ppu,
            "pivotPx": [round(pivot_px[0], 2), round(pivot_px[1], 2)],
            "pivotNorm": [round(pn[0], 6), round(pn[1], 6)]}


# --------------------------------------------------------------------------------------------------------- geometry
def build_geometry(sid, sprites=None):
    st = STAGES[sid]
    ox, oy = origin(sid)
    W, H = PANEL[2], PANEL[3]
    rec_full = save_sprite(sid, "Full", illustration(sid), (ox, oy))
    rec_full.update(layer="full", worldMilli=[0, 0], order=0,
                    note="whole illustration; fallback when the layered art is not used")
    l_m, t_m = to_milli(sid, 0, 0)
    r_m, b_m = to_milli(sid, W, H)
    plats = []
    for name, (top, l, r) in st["plats"].items():
        lm, tm = to_milli(sid, l, top)
        rm, _ = to_milli(sid, r, top)
        plats.append({"id": name, "topPx": top, "leftPx": l, "rightPx": r,
                      "topMilli": tm, "leftMilli": lm, "rightMilli": rm,
                      "cxMilli": (lm + rm) // 2, "halfWMilli": (rm - lm) // 2, "widthMilli": rm - lm,
                      "moves": name in st["moving"]})
    return {
        "stageIndex": st["index"],
        "source": "design/art/maps/" + st["page"],
        "panelPagePx": list(PANEL),
        "imagePx": [W, H],
        "pxPerUnit": st["ppu"],
        "originPx": [ox, oy],
        "imageWorldMilli": {"left": l_m, "right": r_m, "top": t_m, "bottom": b_m},
        "platforms": plats,
        "sprites": [rec_full] + (sprites or []),
    }


def write_json(stage_recs):
    doc = {
        "schema": 1,
        "about": "Stage art <-> sim geometry (ST-4). Written by tools/art/stages/build_stages.py; do not hand-edit.",
        "conventions": {
            "px": "illustration pixels, origin top-left of Full.png / Backdrop.png, y down",
            "milli": "world milli-units (1000 = 1 unit), x right, y up; (0,0) = centre of MAIN's walkable top = "
                     "sim feet-relative milli (MAIN top = 0)",
            "pxToMilli": "xMilli = round((xPx - originPx[0]) * 1000 / pxPerUnit); "
                         "yMilli = round((originPx[1] - yPx) * 1000 / pxPerUnit)",
            "platform": "topPx/topMilli = walkable top (first ink row of the slab); left/right = outer edge of the "
                        "slab outline; cxMilli/topMilli/halfWMilli feed ChibiPlatform(cx, top, halfW)",
            "sprite": "Resources.Load<Sprite>(file) is imported with pixelsPerUnit = ppu and a custom pivot "
                      "(pivotNorm, bottom-left normalized). Place the transform at worldMilli / 1000 with scale 1 "
                      "(parallax layers: scale = 'scale') and the art lands exactly on the colliders. Moving "
                      "platforms: worldMilli is the art (home) position; the view adds the sim's offset.",
        },
        "stages": stage_recs,
    }
    os.makedirs(os.path.dirname(JSON_PATH), exist_ok=True)
    atomic_write(JSON_PATH, (json.dumps(doc, indent=2) + "\n").encode("utf-8"))


# ---------------------------------------------------------------------------------------------- platform sprites
# Cut rectangles (illustration px, x0, y0, x1, y1) generous enough for hanging roots / lava cracks / coral, and the
# ink threshold (grey level below which a pixel counts as outline). Plain's ground is a plain rectangle.
CUTS = {
    "Mountain": dict(ink=80, rois={
        "MAIN": (1050, 1352, 2790, 1890), "A": (350, 1012, 995, 1300), "B": (2848, 1010, 3488, 1305),
        "C": (1572, 762, 2272, 1040), "D": (872, 472, 1408, 765)}),
    "Plain": dict(ink=0, rois={"MAIN": (0, 1572, 3840, 2160)}),
    "Volcano": dict(ink=60, rois={
        "MAIN": (1092, 1412, 2748, 1860), "L1": (2412, 1092, 2948, 1345), "L2": (1772, 832, 2268, 1115),
        "L3": (2232, 562, 2688, 830), "L4": (1700, 302, 2108, 608)}),
    "BirdLair": dict(ink=70, rois={
        "MAIN": (630, 1420, 3165, 1965), "L": (972, 1142, 1548, 1462), "R": (2232, 1142, 2808, 1462)}),
    "Ocean": dict(ink=70, rois={
        "MAIN": (1092, 925, 2750, 1365), "T1": (652, 1372, 1470, 1648), "T2": (2370, 1372, 3188, 1648),
        "T3": (1432, 1735, 2408, 1985)}),
}


def _components_touching(mask, seeds_mask):
    """Union of the 8-connected components of mask that contain any pixel of seeds_mask."""
    n, lab = cv2.connectedComponents(mask.astype(np.uint8), connectivity=8)
    ids = np.unique(lab[(seeds_mask > 0) & (mask > 0)])
    ids = ids[ids != 0]
    return np.isin(lab, ids)


def ink_cutout(rgb, ink_thr, top_row, lr):
    """Foreground = everything enclosed by bold ink, connected to the walkable-top row. Background = the non-ink
    regions reachable from the crop border. Returns a bool mask."""
    g = cv2.cvtColor(rgb, cv2.COLOR_BGR2GRAY)
    ink = g < ink_thr
    inkd = cv2.dilate(ink.astype(np.uint8), np.ones((3, 3), np.uint8)) > 0
    free = ~inkd
    border = np.zeros_like(free)
    border[0, :] = border[-1, :] = border[:, 0] = border[:, -1] = True
    bg = _components_touching(free, border)
    fg = ~bg
    seed = np.zeros_like(fg)
    seed[top_row:top_row + 6, lr[0] + 8:lr[1] - 8] = True
    fg = _components_touching(fg, seed)
    # drop the 1 px halo the ink dilation added on the background side
    halo = inkd & ~ink & (cv2.dilate(bg.astype(np.uint8), np.ones((3, 3), np.uint8)) > 0)
    fg &= ~halo
    # fill pinholes
    inv = _components_touching(~fg, border)
    return ~inv


def grabcut_platform(rgb, ink_thr, top, lr):
    """GrabCut seeded from the ink flood: definite FG = the walkable slab band + the eroded ink-flood result,
    probable FG = the filled convex hull of both, definite BG = the crop border and the sky above the slab ends."""
    h, w = rgb.shape[:2]
    flood = ink_cutout(rgb, ink_thr, top, lr)
    l, r = lr
    band = np.zeros((h, w), bool)
    band[top + 2:top + 22, l + 12:r - 12] = True
    core = cv2.erode(flood.astype(np.uint8), np.ones((5, 5), np.uint8)) > 0
    seed = (band | core).astype(np.uint8)
    pts = cv2.findNonZero(seed)
    hull = np.zeros((h, w), np.uint8)
    cv2.fillConvexPoly(hull, cv2.convexHull(pts), 1)
    hull = cv2.dilate(hull, np.ones((15, 15), np.uint8)) > 0
    gc = np.full((h, w), cv2.GC_PR_BGD, np.uint8)
    gc[hull] = cv2.GC_PR_FGD
    gc[(band | core)] = cv2.GC_FGD
    gc[:max(0, top - 4), :] = np.where(gc[:max(0, top - 4), :] == cv2.GC_FGD, cv2.GC_FGD, cv2.GC_PR_BGD)
    gc[:4, :] = gc[-4:, :] = cv2.GC_BGD
    gc[:, :4] = gc[:, -4:] = cv2.GC_BGD
    # the sky beside the slab ends, at slab height, is never platform
    gc[max(0, top - 4):top + 10, :max(0, l - 6)] = cv2.GC_BGD
    gc[max(0, top - 4):top + 10, min(w, r + 6):] = cv2.GC_BGD
    bgm = np.zeros((1, 65), np.float64)
    fgm = np.zeros((1, 65), np.float64)
    cv2.grabCut(rgb, gc, None, bgm, fgm, 6, cv2.GC_INIT_WITH_MASK)
    m = (gc == cv2.GC_FGD) | (gc == cv2.GC_PR_FGD)
    m = cv2.morphologyEx(m.astype(np.uint8), cv2.MORPH_OPEN, np.ones((3, 3), np.uint8)) > 0
    sd = np.zeros_like(m)
    sd[top + 2:top + 8, l + 12:r - 12] = True
    m = _components_touching(m, sd)
    border = np.zeros_like(m)
    border[0, :] = border[-1, :] = border[:, 0] = border[:, -1] = True
    return ~_components_touching(~m, border)


def alpha_from_mask(mask):
    a = mask.astype(np.float32)
    a = cv2.GaussianBlur(a, (3, 3), 0.7)
    return (np.clip(a, 0, 1) * 255).astype(np.uint8)


def cut_platforms(sid, img):
    """Returns ({name: (bgra_crop, (x0, y0))}, full-size union mask of removed pixels)."""
    cfg = CUTS[sid]
    st = STAGES[sid]
    out = {}
    union = np.zeros(img.shape[:2], bool)
    for name, (x0, y0, x1, y1) in cfg["rois"].items():
        top, l, r = st["plats"][name]
        crop = img[y0:y1, x0:x1]
        if cfg["ink"] == 0:
            m = np.ones(crop.shape[:2], bool)
            m[:max(0, top - y0)] = False
        else:
            m = grabcut_platform(crop, cfg["ink"], top - y0, (l - x0, r - x0))
        m = _post_mask(sid, name, crop, m, (x0, y0))
        ys, xs = np.nonzero(m)
        bx0, by0, bx1, by1 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
        a = alpha_from_mask(m)
        bgra = np.dstack([crop, a])[by0:by1, bx0:bx1]
        out[name] = (bgra, (x0 + bx0, y0 + by0))
        union[y0:y1, x0:x1] |= m
    return out, union


# Bird Lair's L/R are solid trapezoids standing on MAIN; their brown body matches MAIN's rock, so they are cut with
# a measured polygon (illustration px) instead of GrabCut. Bottom = MAIN's slab line (MAIN draws over the seam).
BIRD_BLOCKS = {
    "L": [(994, 1164), (1524, 1164), (1592, 1446), (930, 1446)],
    "R": [(2254, 1164), (2784, 1164), (2846, 1446), (2192, 1446)],
}
ABOVE_TOP = {"Ocean": 95}  # px of decoration (coral, seaweed) kept above the walkable top; elsewhere 3


def _fill_enclosed(m):
    border = np.zeros_like(m)
    border[0, :] = border[-1, :] = border[:, 0] = border[:, -1] = True
    return ~_components_touching(~m, border)


def _post_mask(sid, name, crop, m, off):
    """Per-platform fixes where GrabCut is not enough."""
    x0, y0 = off
    h, w = m.shape
    top, l, r = STAGES[sid]["plats"][name]
    if sid == "BirdLair" and name in BIRD_BLOCKS:
        poly = np.array([(x - x0, y - y0) for x, y in BIRD_BLOCKS[name]], np.int32)
        m = np.zeros((h, w), np.uint8)
        cv2.fillPoly(m, [poly], 1)
        return m > 0
    if sid == "Ocean":
        # background pillars / water are saturated mid blue; the platform stone is pale and the runes bright cyan
        hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
        water = (hsv[..., 0] >= 95) & (hsv[..., 0] <= 125) & (hsv[..., 1] > 110) & (hsv[..., 2] < 205)
        m = m & ~water
        m = cv2.morphologyEx(m.astype(np.uint8), cv2.MORPH_OPEN, np.ones((5, 5), np.uint8)) > 0
    # nothing beside the slab ends, nothing above the top except decoration
    keep = np.zeros_like(m)
    keep[max(0, top - y0 - ABOVE_TOP.get(sid, 3)):, max(0, l - x0 - 30):min(w, r - x0 + 30)] = True
    if sid == "BirdLair" and name == "MAIN":   # the nest twigs and the right peak start at the slab's right end
        keep[:, min(w, r - x0 + 30):] = False
    m = m & keep
    sd = np.zeros_like(m)
    sd[top - y0 + 2:top - y0 + 8, l - x0 + 12:r - x0 - 12] = True
    m = _components_touching(m, sd)
    # close cracks / glowing fissures the colour model dropped (moving platforms get a light touch so little
    # background travels with them)
    k = 7 if name in STAGES[sid]["moving"] else 21
    m = cv2.morphologyEx(m.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((k, k), np.uint8)) > 0
    return _fill_enclosed(m & keep)


def fill_holes(img, mask, grow=6):
    """cv2.inpaint for the removed regions: low-res Telea fill (blurred) for the hole body, full-res Telea for a
    thin rim so the seam matches."""
    m = cv2.dilate(mask.astype(np.uint8) * 255, np.ones((2 * grow + 1, 2 * grow + 1), np.uint8))
    if not m.any():
        return img.copy()
    s = 4
    small = cv2.resize(img, None, fx=1.0 / s, fy=1.0 / s, interpolation=cv2.INTER_AREA)
    sm = cv2.resize(m, (small.shape[1], small.shape[0]), interpolation=cv2.INTER_NEAREST)
    sm = cv2.dilate(sm, np.ones((3, 3), np.uint8))
    lo = cv2.inpaint(small, sm, 12, cv2.INPAINT_TELEA)
    lo = cv2.GaussianBlur(lo, (0, 0), 2.0)
    up = cv2.resize(lo, (img.shape[1], img.shape[0]), interpolation=cv2.INTER_CUBIC)
    res = img.copy()
    res[m > 0] = up[m > 0]
    # soften the seam: feathered blend over a 10 px band
    k = cv2.GaussianBlur((m > 0).astype(np.float32), (0, 0), 4)
    k = np.maximum(k, (m > 0).astype(np.float32))[..., None]
    return (img * (1 - k) + res * k).astype(np.uint8)


# --------------------------------------------------------------------------------------------------------- hazards
def _largest(mask):
    n, lab, stats, _ = cv2.connectedComponentsWithStats(mask.astype(np.uint8), connectivity=8)
    if n <= 1:
        return mask.astype(bool)
    i = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    return lab == i


def _crop_bgra(img, mask, soft=None):
    ys, xs = np.nonzero(mask)
    x0, y0, x1, y1 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
    a = soft if soft is not None else alpha_from_mask(mask)
    return np.dstack([img, a])[y0:y1, x0:x1].copy(), (int(x0), int(y0))


# Volcano: the two drawn lava pillars (illustration px search boxes) and the lava lake surface (approximate).
VOLCANO_PILLARS = {"Left": (680, 330, 1010, 1915), "Right": (2940, 580, 3240, 1915)}
VOLCANO_LAVA_TOP_PX = 1864
# Bird Lair: silhouette box in the main art; event frame on the page (page px) and the bird box inside it.
BIRD_SIL_BOX = (1060, 240, 2800, 840)
BIRD_EVENT_PAGE = (142, 4355, 2546, 5708)
OCEAN_WHALE_BOX = (800, 0, 3220, 690)


def volcano_pillars(img):
    """Bright warm cones: V high and red >> blue, the component that reaches the lake. Soft alpha from brightness."""
    out = {}
    union = np.zeros(img.shape[:2], bool)
    for side, (x0, y0, x1, y1) in VOLCANO_PILLARS.items():
        c = img[y0:y1, x0:x1].astype(int)
        b, g, r = c[..., 0], c[..., 1], c[..., 2]
        hot = (r > 225) & (g > 120) & (r - b > 90)
        seed = np.zeros_like(hot)
        seed[-80:-40, :] = True
        hot = cv2.morphologyEx(hot.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8)) > 0
        core = _components_touching(hot, seed & hot)
        # only the tall cone: columns within the core's spread at its upper half
        core = _fill_enclosed(core)
        region = cv2.dilate(core.astype(np.uint8), np.ones((31, 31), np.uint8)) > 0
        v = c.max(2)
        soft = np.clip((v - 150) / 80.0, 0, 1) * np.clip((r - b - 40) / 60.0, 0, 1)
        soft = np.where(core, 1.0, soft * region)
        soft = cv2.GaussianBlur(soft.astype(np.float32), (0, 0), 1.2)
        a = (np.clip(soft, 0, 1) * 255).astype(np.uint8)
        m = a > 8
        full_m = np.zeros(img.shape[:2], bool)
        full_m[y0:y1, x0:x1] = core
        union |= cv2.dilate(full_m.astype(np.uint8), np.ones((9, 9), np.uint8)) > 0
        crop = img[y0:y1, x0:x1]
        bgra, (bx, by) = _crop_bgra(crop, m, a)
        ys = np.nonzero(core)[0]
        xs_base = np.nonzero(core[ys.max() - 30])[0]
        base = ((xs_base.min() + xs_base.max()) / 2.0 + x0, float(ys.max() + y0))
        out[side] = (bgra, (bx + x0, by + y0), base)
    return out, union


def ember_sprite():
    """A diamond spark like the sheet's embers, in the sheet palette (Magma core #FFF0A0 / Orange #FF9A1F)."""
    W, H = 64, 96
    glow = np.zeros((H, W), np.float32)
    core = np.zeros((H, W), np.uint8)
    pts = np.array([(32, 14), (46, 48), (32, 82), (18, 48)], np.int32)
    cv2.fillPoly(core, [pts], 1)
    glow = cv2.GaussianBlur(core.astype(np.float32), (0, 0), 7) * 1.6
    inner = cv2.GaussianBlur(core.astype(np.float32), (0, 0), 1.0)
    a = np.clip(np.maximum(glow * 0.75, inner), 0, 1)
    orange = np.array([0x1F, 0x9A, 0xFF], np.float32)
    yellow = np.array([0xA0, 0xF0, 0xFF], np.float32)
    t = np.clip(inner, 0, 1)[..., None]
    rgb = orange * (1 - t) + yellow * t
    return np.dstack([rgb.astype(np.uint8), (a * 255).astype(np.uint8)])


def bird_silhouette(img):
    x0, y0, x1, y1 = BIRD_SIL_BOX
    c = img[y0:y1, x0:x1]
    lab = cv2.cvtColor(c, cv2.COLOR_BGR2LAB).astype(np.float32)
    ref = np.median(lab[(c.shape[0] // 2 - 20):(c.shape[0] // 2 + 20), (c.shape[1] // 2 - 20):(c.shape[1] // 2 + 20)]
                    .reshape(-1, 3), axis=0)
    d = np.linalg.norm(lab - ref, axis=2)
    near = d < 18
    m = _largest(near)
    m = near & (cv2.dilate(m.astype(np.uint8), np.ones((61, 61), np.uint8)) > 0)  # head tip cut off by a wind line
    m = cv2.morphologyEx(m.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((31, 31), np.uint8)) > 0
    m = _fill_enclosed(m)
    full = np.zeros(img.shape[:2], bool)
    full[y0:y1, x0:x1] = m
    bgra, (bx, by) = _crop_bgra(c, m)
    return bgra, (bx + x0, by + y0), full


def bird_event(sid):
    """The swooping bird of the event frame: regions enclosed by its bold ink (outline gaps under the white speed
    lines bridged by a 13 px dilation), restricted to the bird's own saturated colours (teal body / wings, orange
    belly, yellow beak). The desaturated background bird, rock, slab and clouds fall out on saturation."""
    px0, py0, px1, py1 = BIRD_EVENT_PAGE
    fr = page(sid)[py0:py1, px0:px1].copy()
    g = cv2.cvtColor(fr, cv2.COLOR_BGR2GRAY)
    hsv = cv2.cvtColor(fr, cv2.COLOR_BGR2HSV).astype(int)
    ink = g < 70
    inkd = cv2.dilate(ink.astype(np.uint8), np.ones((13, 13), np.uint8)) > 0
    border = np.zeros_like(inkd)
    border[0, :] = border[-1, :] = border[:, 0] = border[:, -1] = True
    encl = ~_components_touching(~inkd, border)
    encl = cv2.erode(encl.astype(np.uint8), np.ones((9, 9), np.uint8)) > 0
    H, S, V = hsv[..., 0], hsv[..., 1], hsv[..., 2]
    teal = (H >= 80) & (H <= 108) & (S > 135) & (V > 40)
    warm = (H >= 8) & (H <= 32) & (S > 135) & (V > 190)
    m = encl & (teal | warm)
    m = cv2.morphologyEx(m.astype(np.uint8), cv2.MORPH_OPEN, np.ones((7, 7), np.uint8)) > 0
    m = cv2.morphologyEx(m.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8)) > 0
    m = _fill_enclosed(_largest(m))
    m = (cv2.dilate(m.astype(np.uint8), np.ones((15, 15), np.uint8)) > 0) & (encl | ink)   # + the ink outline
    m = _fill_enclosed(_largest(m))
    bgra, _ = _crop_bgra(fr, m)
    return bgra


def ocean_whale(img):
    x0, y0, x1, y1 = OCEAN_WHALE_BOX
    c = img[y0:y1, x0:x1]
    lab = cv2.cvtColor(c, cv2.COLOR_BGR2LAB).astype(np.float32)
    # reference: the whale's flank (illustration ~ (1870, 230))
    rx, ry = 1870 - x0, 230 - y0
    ref = np.median(lab[ry - 15:ry + 15, rx - 15:rx + 15].reshape(-1, 3), axis=0)
    d = np.linalg.norm(lab - ref, axis=2)
    m = _largest(d < 16)
    m = cv2.morphologyEx(m.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((21, 21), np.uint8)) > 0
    m = _fill_enclosed(m)
    m = cv2.dilate(m.astype(np.uint8), np.ones((7, 7), np.uint8)) > 0   # pick up the thin rim line
    full = np.zeros(img.shape[:2], bool)
    full[y0:y1, x0:x1] = m
    bgra, (bx, by) = _crop_bgra(c, m)
    return bgra, (bx + x0, by + y0), full


# ------------------------------------------------------------------------------------------------------ foreground
def foreground(sid, img, back=None, plats_mask=None):
    """Front overlay band (drawn over fighters by the view at <= 40 % opacity, README 3.5). Returns (bgra, (x, y))
    or None."""
    H, W = img.shape[:2]
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV).astype(np.float32)
    s, v = hsv[..., 1], hsv[..., 2]
    if sid == "Mountain":
        y0 = 1520
        a, whit = mountain_fog_alpha(img, plats_mask if plats_mask is not None else np.zeros((H, W), bool), y0)
        lower = np.zeros((H, W), bool)
        lower[y0 + 300:] = True
        col = np.median(img[(whit > 0.95) & lower].reshape(-1, 3), axis=0).astype(np.uint8)
        rgb = np.empty_like(img)
        rgb[:] = col
        a8 = (np.clip(a, 0, 1) * 255).astype(np.uint8)
        return np.dstack([rgb, a8])[y0:], (0, y0)
    if sid == "BirdLair":
        y0 = 1600
        g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
        cloud = (v > 205) & (s < 120)
        cloud[:y0] = False
        cloud = cv2.morphologyEx(cloud.astype(np.uint8), cv2.MORPH_OPEN, np.ones((9, 9), np.uint8)) > 0
        n, lab, st, _ = cv2.connectedComponentsWithStats(cloud.astype(np.uint8))
        big = np.isin(lab, [i for i in range(1, n) if st[i, cv2.CC_STAT_AREA] > 20000])
        near = cv2.dilate(big.astype(np.uint8), np.ones((25, 25), np.uint8)) > 0
        m = big | (near & (g < 90))          # + the clouds' ink outline
        m = _fill_enclosed(cv2.morphologyEx(m.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8)) > 0)
        m[:y0] = False
        return np.dstack([img, alpha_from_mask(m)])[y0:], (0, y0)
    if sid == "Plain":
        y0 = 1760
        g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).astype(np.float32)
        band = img[y0:].astype(np.float32)
        field = cv2.medianBlur(img[y0:], 61).astype(np.float32)
        darker = (cv2.cvtColor(img[y0:], cv2.COLOR_BGR2GRAY).astype(np.float32)
                  < cv2.cvtColor(field.astype(np.uint8), cv2.COLOR_BGR2GRAY).astype(np.float32) - 18)
        m = darker | (g[y0:] < 70)
        m = cv2.morphologyEx(m.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))
        m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8)) > 0
        ramp = np.clip((np.arange(H - y0, dtype=np.float32) - 40) / 120.0, 0, 1)[:, None]
        a = cv2.GaussianBlur(m.astype(np.float32), (3, 3), 0.8) * ramp
        return np.dstack([band.astype(np.uint8), (a * 255).astype(np.uint8)]), (0, y0)
    if sid == "Volcano" and back is not None:
        # GL-3: opaque magma lake (from the pillar-free Backdrop, bubbles included) so a falling fighter sinks into
        # it; soft glowing top edge 0.4 u (42 px) above the lava surface, fully opaque from the surface down.
        glow = int(round(0.4 * STAGES[sid]["ppu"]))
        y0 = VOLCANO_LAVA_TOP_PX - glow
        band = back[y0:].copy()
        t = np.clip(np.arange(H - y0, dtype=np.float32) / glow, 0, 1)
        a = (t * t * (3 - 2 * t))[:, None] * np.ones((1, W), np.float32)
        # brighten the feather toward the magma-core colour so the edge reads as glow, not as a fade
        core = np.array([0xA0, 0xF0, 0xFF], np.float32)
        k = ((1 - t) * 0.5)[:, None, None]
        band = (band.astype(np.float32) * (1 - k) + core * k).astype(np.uint8)
        return np.dstack([band, (a * 255).astype(np.uint8)]), (0, y0)
    return None


# -------------------------------------------------------------------------------------------------------- parallax
# The three layer thumbnails sit at the same page px on every page (FAR, MID; FOREGROUND is the gameplay plane).
THUMB_SEARCH = {"Far": (2140, 2910, 3290, 3580), "Mid": (3315, 2910, 4465, 3580)}


def thumb(sid, which):
    x0, y0, x1, y1 = THUMB_SEARCH[which]
    p = page(sid)
    bg = p[20, 20].astype(int)
    roi = p[y0:y1, x0:x1]
    d = np.abs(roi.astype(int) - bg).sum(2) > 30
    rows = np.nonzero(d.mean(1) > 0.5)[0]
    cols = np.nonzero(d.mean(0) > 0.5)[0]
    t = roi[rows.min() + 6:rows.max() - 5, cols.min() + 6:cols.max() - 5]   # inside the rounded corners
    return t


def upscale(t, W=3840, H=2160):
    """Thumbnail -> exactly W x H (the thumbnails are within 0.5 % of 16:9; art review m1 wants exact 2160)."""
    t = cv2.fastNlMeansDenoisingColored(t, None, 3, 3, 5, 15)     # JPEG blocking off before enlarging
    up = cv2.resize(t, (W, H), interpolation=cv2.INTER_LANCZOS4)
    blur = cv2.GaussianBlur(up, (0, 0), 2.0)
    return cv2.addWeighted(up, 1.35, blur, -0.35, 0)              # mild unsharp mask


MID_KEY = {"Volcano": (2.5, 5.0)}   # near-black walls sit close to the slate placeholder: key tightly


def mid_alpha(t, rng=(6.0, 14.0)):
    """The MID thumbnails show the layer over a flat slate placeholder: key it out."""
    q = (t // 8).reshape(-1, 3)
    vals, counts = np.unique(q, axis=0, return_counts=True)
    key = vals[np.argmax(counts)].astype(np.float32) * 8 + 4
    lab_t = cv2.cvtColor(t, cv2.COLOR_BGR2LAB).astype(np.float32)
    lab_k = cv2.cvtColor(key.reshape(1, 1, 3).astype(np.uint8), cv2.COLOR_BGR2LAB).astype(np.float32)[0, 0]
    d = np.linalg.norm(lab_t - lab_k, axis=2)
    a = np.clip((d - rng[0]) / rng[1], 0, 1)
    return cv2.medianBlur((a * 255).astype(np.uint8), 7)   # drop JPEG speckle at the key edge


# -------------------------------------------------------------------------------------------------- build sprites
PARALLAX = {"Far": dict(scale_ppu=0.6, factor=0.15, order=-30), "Mid": dict(scale_ppu=0.8, factor=0.4, order=-20)}


def _rec(sid, rec, layer, world, order, **extra):
    rec.update(layer=layer, worldMilli=[int(world[0]), int(world[1])], order=order)
    rec.update(extra)
    return rec


def _place(sid, name, bgra, off, anchor_px, layer, order, **extra):
    """Saves a sprite cut at illustration offset off with its pivot at illustration px anchor_px."""
    piv = (anchor_px[0] - off[0], anchor_px[1] - off[1])
    rec = save_sprite(sid, name, bgra, piv)
    wx = (anchor_px[0] - origin(sid)[0]) * 1000.0 / STAGES[sid]["ppu"]
    wy = (origin(sid)[1] - anchor_px[1]) * 1000.0 / STAGES[sid]["ppu"]
    return _rec(sid, rec, layer, (round(wx), round(wy)), order, **extra)


# ------------------------------------------------------------------------------------- art-review fixes (B1-B5)
# Hazard anchors are kept at their published world positions (worldMilli in the json) so the runtime contract does
# not move when a sprite is re-cut.
HAZ_ANCHOR_MILLI = {"Haz_PillarLeft": (-10127, -4538), "Haz_PillarRight": (11052, -4538),
                    "Haz_Whale": (844, 7519), "Haz_BirdSilhouette": (153, 5622)}


def anchor_px(sid, name):
    ox, oy = origin(sid)
    ppu = STAGES[sid]["ppu"]
    mx, my = HAZ_ANCHOR_MILLI[name]
    return ox + mx * ppu / 1000.0, oy - my * ppu / 1000.0


def _norm_blur(val, w, sigma):
    """Normalized convolution: smooth val using only pixels where w > 0."""
    w = w.astype(np.float32)
    num = cv2.GaussianBlur(val * (w[..., None] if val.ndim == 3 else w), (0, 0), sigma)
    den = cv2.GaussianBlur(w, (0, 0), sigma)
    den = np.maximum(den, 1e-4)
    return num / (den[..., None] if val.ndim == 3 else den)


def _noise(shape, amp=1.2, seed=7):
    rng = np.random.RandomState(seed)
    return rng.normal(0, amp, shape).astype(np.float32)


def _feather_paste(dst, src, hole, sigma=1.5):
    k = cv2.GaussianBlur(hole.astype(np.float32), (0, 0), sigma)
    k = np.maximum(k, cv2.erode(hole.astype(np.uint8), np.ones((3, 3), np.uint8)).astype(np.float32))[..., None]
    return np.clip(dst.astype(np.float32) * (1 - k) + src.astype(np.float32) * k, 0, 255).astype(np.uint8)


_plate_cache = {}


def layer_plate(sid):
    """The sheet's FAR (+ keyed MID) thumbnails composited into a clean background plate, registered onto the
    illustration (ORB + RANSAC similarity on the thumbnail scale) and warped to 3840 x 2160."""
    far = cv2.fastNlMeansDenoisingColored(thumb(sid, "Far"), None, 3, 3, 5, 15)
    comp = far.copy()
    if sid != "Ocean":                       # Ocean's MID carries the whale itself: FAR (rays, arches) only
        mid = cv2.fastNlMeansDenoisingColored(thumb(sid, "Mid"), None, 3, 3, 5, 15)
        mid = cv2.resize(mid, (far.shape[1], far.shape[0]), interpolation=cv2.INTER_AREA)
        a = mid_alpha(mid, MID_KEY.get(sid, (6.0, 14.0)))[..., None] / 255.0
        comp = (mid * a + far * (1 - a)).astype(np.uint8)
    full = illustration(sid)
    fs = comp.shape[1] / float(full.shape[1])
    small = cv2.resize(full, (comp.shape[1], int(round(full.shape[0] * fs))), interpolation=cv2.INTER_AREA)
    orb = cv2.ORB_create(6000)
    k1, d1 = orb.detectAndCompute(cv2.cvtColor(comp, cv2.COLOR_BGR2GRAY), None)
    k2, d2 = orb.detectAndCompute(cv2.cvtColor(small, cv2.COLOR_BGR2GRAY), None)
    bf = cv2.BFMatcher(cv2.NORM_HAMMING, crossCheck=True)
    ms = sorted(bf.match(d1, d2), key=lambda m: m.distance)[:1500]
    p1 = np.float32([k1[m.queryIdx].pt for m in ms])
    p2 = np.float32([k2[m.trainIdx].pt for m in ms])
    M, inl = cv2.estimateAffinePartial2D(p1, p2, method=cv2.RANSAC, ransacReprojThreshold=2.0)
    S = np.array([[1 / fs, 0, 0], [0, 1 / fs, 0], [0, 0, 1]], np.float64)
    Mf = (S @ np.vstack([M, [0, 0, 1]]))[:2]
    if M is None or int(inl.sum()) < 60:
        # too few features (Ocean's FAR is mostly flat rays): the thumbnails sit at the same page place on every
        # sheet, so reuse Mountain's registration
        _, _, Mf = _plate_cache.get("Mountain") or (None, None, None)
        if Mf is None:
            layer_plate("Mountain")
            Mf = _plate_cache["Mountain"][2]
        inl = np.zeros(1)
    _plate_cache[sid] = (None, None, Mf)
    plate = cv2.warpAffine(comp, Mf, (full.shape[1], full.shape[0]), flags=cv2.INTER_LANCZOS4,
                           borderMode=cv2.BORDER_REFLECT)
    blur = cv2.GaussianBlur(plate, (0, 0), 2.0)
    plate = cv2.addWeighted(plate, 1.4, blur, -0.4, 0)
    print("   %s plate: %d inliers, scale %.4f" % (sid, int(inl.sum()), np.hypot(Mf[0, 0], Mf[0, 1])))
    return plate


def fill_from_plate(back, img, hole, plate, ring=70):
    """Hole <- registered layer plate, colour-matched to the art with an offset field measured in a ring around
    the hole (normalized convolution), plus 1 px of grain so the patch carries the same texture."""
    ringm = (cv2.dilate(hole.astype(np.uint8), np.ones((2 * ring + 1, 2 * ring + 1), np.uint8)) > 0) & ~hole
    diff = img.astype(np.float32) - plate.astype(np.float32)
    ringm &= np.abs(diff).max(2) < 30          # ignore fish, bubbles, arches: only the shared water/sky/cliff
    off = _norm_blur(diff, ringm, 45)
    fill = plate.astype(np.float32) + off + _noise(img.shape)
    return _feather_paste(back, np.clip(fill, 0, 255), hole)


def _membrane(src, hole, iters):
    """Laplace (membrane) fill of hole from its boundary, Jacobi iterations on the given array."""
    f = src.astype(np.float32).copy()
    m = hole.astype(bool)
    f[m] = f[~m].mean(0) if f.ndim == 3 else f[~m].mean()
    k = np.array([[0, .25, 0], [.25, 0, .25], [0, .25, 0]], np.float32)
    for _ in range(iters):
        g = cv2.filter2D(f, -1, k, borderType=cv2.BORDER_REPLICATE)
        f[m] = g[m]
    return f


def fill_sky_poly(back, img, hole, region, deg=3):
    """Bird Lair sky (art review B3), no inpaint: membrane (Laplace) interpolation of the clean sky across the hole,
    solved coarse-to-fine from a streak-free copy of the boundary, 1 px grain added; then the wind streaks and
    feathers that the artist drew OVER the bird are put back from the source. (Name kept for the call site.)"""
    x0, y0, x1, y1 = region
    sub = img[y0:y1, x0:x1]
    hs = hole[y0:y1, x0:x1]
    # streak/feather-free reference: streaks and feathers become part of the solve region
    hsv = cv2.cvtColor(sub, cv2.COLOR_BGR2HSV).astype(int)
    junk = ((hsv[..., 1] < 70) & (hsv[..., 2] > 175)) | (hsv[..., 2] < 110) | ((hsv[..., 0] <= 22) & (hsv[..., 1] > 150))
    junk = cv2.dilate(junk.astype(np.uint8), np.ones((11, 11), np.uint8)) > 0
    solve = hs | junk
    # coarse (1/8) solve, then refine at 1/2 and full res starting from the upsampled result
    f = sub.astype(np.float32)
    for sc, it in ((8, 1500), (2, 300), (1, 120)):
        sz = (sub.shape[1] // sc, sub.shape[0] // sc)
        wgt = (~solve).astype(np.float32)
        num = cv2.resize(f * wgt[..., None], sz, interpolation=cv2.INTER_AREA)
        den = cv2.resize(wgt, sz, interpolation=cv2.INTER_AREA)
        small = num / np.maximum(den, 1e-4)[..., None]          # boundary values never mix in the bird
        ms = den < 0.999
        if sc != 8:
            init = cv2.resize(prev, (small.shape[1], small.shape[0]), interpolation=cv2.INTER_LINEAR)
            small[ms] = init[ms]
            g = small.copy()
            k = np.array([[0, .25, 0], [.25, 0, .25], [0, .25, 0]], np.float32)
            for _ in range(it):
                gg = cv2.filter2D(g, -1, k, borderType=cv2.BORDER_REPLICATE)
                g[ms] = gg[ms]
            prev = g
        else:
            prev = _membrane(small, ms, it)
    fit = prev + _noise(prev.shape, 1.0)
    out = back.copy()
    out[y0:y1, x0:x1] = _feather_paste(back[y0:y1, x0:x1], np.clip(fit, 0, 255), hs, 2.0)
    # streaks: white, alpha from whiteness, so no dark fringe from the bird underneath
    hsv_all = cv2.cvtColor(img, cv2.COLOR_BGR2HSV).astype(np.float32)
    # streaks by local contrast: brighter than their 21 px neighbourhood and unsaturated (pale sky is not)
    vv = hsv_all[..., 2].astype(np.uint8)
    lift = hsv_all[..., 2] - cv2.medianBlur(vv, 21).astype(np.float32)
    a = np.clip((lift - 30) / 40.0, 0, 1) * np.clip((110 - hsv_all[..., 1]) / 40.0, 0, 1)
    n, lab, st, _ = cv2.connectedComponentsWithStats(((a > 0.25) & hole).astype(np.uint8))
    longc = np.isin(lab, [i for i in range(1, n) if st[i, cv2.CC_STAT_WIDTH] > 70])   # streaks, not notches
    a = a * (cv2.dilate(longc.astype(np.uint8), np.ones((5, 5), np.uint8)) > 0) * 0.92
    a = (a * hole)[..., None]
    white = np.array([250, 250, 250], np.float32)
    out = (out.astype(np.float32) * (1 - a) + white * a).astype(np.uint8)
    # feathers (orange leaf shapes with their ink), pasted from the source
    # feathers are small, strongly saturated orange shapes with an ink outline (the gold sky near the sun is not)
    orange = (hsv_all[..., 0] <= 22) & (hsv_all[..., 1] > 150) & (hsv_all[..., 2] > 120) & hole
    orange = cv2.morphologyEx(orange.astype(np.uint8), cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
    n, lab, st, _ = cv2.connectedComponentsWithStats(orange)
    inkpx = hsv_all[..., 2] < 90
    keep = np.zeros(n, bool)
    for i in range(1, n):
        x, y, w, h, area = st[i]
        if 150 < area < 6000 and inkpx[max(0, y - 8):y + h + 8, max(0, x - 8):x + w + 8].mean() > 0.05:
            keep[i] = True
    orange = keep[lab]
    leaf = (cv2.dilate(orange.astype(np.uint8), np.ones((9, 9), np.uint8)) > 0) & (orange | inkpx)
    leaf = _fill_enclosed(leaf) & hole
    out[leaf] = img[leaf]
    return out


# Fire cones measured on the source (illustration px): apex, base-left, base-right (outer yellow rim included).
PILLAR_TRI = {"Left": [(838, 368), (710, 1901), (970, 1901)],
              "Right": [(3080, 611), (2960, 1901), (3198, 1901)]}


def pillar_matte(img, side):
    """Fire-cone matte (art review B2): the measured cone triangle only (no cave rock, cracks or halo), alpha
    eroded 1 px, plus a 2 px warm ink stroke (#5a1a0a). Returns (bgra, off, mask)."""
    H, W = img.shape[:2]
    tri = np.zeros((H, W), np.uint8)
    cv2.fillConvexPoly(tri, np.array(PILLAR_TRI[side], np.int32), 1)
    m = cv2.erode(tri, np.ones((3, 3), np.uint8)) > 0
    stroke = (cv2.dilate(m.astype(np.uint8), np.ones((5, 5), np.uint8)) > 0) & ~m
    rgb = img.copy()
    rgb[stroke] = (0x0A, 0x1A, 0x5A)
    full = m | stroke
    ys, xs = np.nonzero(full)
    x0, y0, x1, y1 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
    sub = full[y0:y1, x0:x1]
    a = alpha_from_mask(sub)
    bgra = np.dstack([rgb[y0:y1, x0:x1], a])
    return bgra, (int(x0), int(y0)), full


# Sheet palette (art review pass 2, item 1): obsidian cel fill, ink, cooled-lava cracks, base ember glow. BGR.
OBSIDIAN_DARK, OBSIDIAN_LIT = (0x22, 0x10, 0x1B), (0x2A, 0x1A, 0x3A)
VENT_INK, VENT_CRACK, VENT_GLOW = (0x10, 0x08, 0x0D), (0x10, 0x2A, 0x6A), (0x18, 0x22, 0xA0)
# Cracks per cone as (t along the axis apex 0 .. base 1, u across the cone -1 .. 1) zig-zag polylines.
VENT_CRACKS = [[(0.22, 0.0), (0.30, 0.25), (0.37, -0.1), (0.45, 0.15)],
               [(0.50, -0.45), (0.56, -0.15), (0.63, -0.35), (0.70, -0.05)],
               [(0.62, 0.45), (0.70, 0.2), (0.77, 0.4), (0.86, 0.1)],
               [(0.80, -0.6), (0.86, -0.3), (0.93, -0.5)]]


def _aa_poly(shape, pts, ss=4):
    """Anti-aliased polygon coverage 0..1 (supersampled)."""
    H, W = shape
    big = np.zeros((H * ss, W * ss), np.uint8)
    cv2.fillPoly(big, [np.round(np.array(pts) * ss).astype(np.int32)], 255, lineType=cv2.LINE_AA)
    return cv2.resize(big, (W, H), interpolation=cv2.INTER_AREA).astype(np.float32) / 255.0


def dormant_vents(back, _masks=None):
    """Volcano backdrop, dormant state (pass 2 item 1): each painted fire cone is redrawn as cooled basalt in the
    sheet's obsidian, 2-tone cel (the facet facing the light shaft is lit), 3 px ink outline, 4 dim cooled-lava
    cracks and a small dark-red glow at the base only. The warm wall glow around it is dimmed, never greyed."""
    out = back.astype(np.float32)
    for side, (apex, bl, br) in PILLAR_TRI.items():
        ax, ay = apex
        cone = [(ax, ay - 4), (bl[0] - 3, bl[1]), (br[0] + 3, br[1])]
        x0, x1 = min(p[0] for p in cone) - 120, max(p[0] for p in cone) + 120
        y0, y1 = ay - 120, bl[1] + 40
        sub = out[y0:y1, x0:x1]
        loc = lambda pts: [(px - x0, py - y0) for px, py in pts]
        cov = _aa_poly(sub.shape[:2], loc(cone))
        # calm the painted fire glow around the cone (hue kept, value lowered), strongest next to the cone
        near = cv2.GaussianBlur(cov, (0, 0), 40)
        near = np.clip(near / max(float(near.max()), 1e-6), 0, 1)[..., None]
        sub *= 1 - 0.35 * near
        # cel fill: dark body + lit facet on the side facing the light shaft (stage centre)
        mid_x = (bl[0] + br[0]) / 2.0
        split = (mid_x + (12 if side == "Left" else -12), bl[1])
        if side == "Left":
            lit_poly = [(ax, ay - 4), split, (br[0] + 3, br[1])]
        else:
            lit_poly = [(ax, ay - 4), (bl[0] - 3, bl[1]), split]
        lit = _aa_poly(sub.shape[:2], loc(lit_poly)) * cov
        fill = np.empty_like(sub)
        fill[:] = OBSIDIAN_DARK
        fill = fill * (1 - lit[..., None]) + np.array(OBSIDIAN_LIT, np.float32) * lit[..., None]
        sub[:] = sub * (1 - cov[..., None]) + fill * cov[..., None]
        # dim cooled-lava cracks (~2 px)
        crack = np.zeros((sub.shape[0] * 4, sub.shape[1] * 4), np.uint8)
        for poly in VENT_CRACKS:
            pts = []
            for t, u in poly:
                cy = ay + t * (bl[1] - ay)
                half = t * (br[0] - bl[0]) / 2.0
                cx = ax + (mid_x - ax) * t
                pts.append(((cx + u * half * 0.8 - x0) * 4, (cy - y0) * 4))
            cv2.polylines(crack, [np.round(np.array(pts)).astype(np.int32)], False, 255, 9, cv2.LINE_AA)
        crack = cv2.resize(crack, (sub.shape[1], sub.shape[0]), interpolation=cv2.INTER_AREA).astype(np.float32) / 255.0
        crack *= cov
        sub[:] = sub * (1 - crack[..., None]) + np.array(VENT_CRACK, np.float32) * crack[..., None]
        # small dark-red glow at the base only
        gl = np.zeros(sub.shape[:2], np.float32)
        cv2.ellipse(gl, (int(mid_x - x0), int(bl[1] - y0)), (int((br[0] - bl[0]) * 0.75), 70), 0, 0, 360, 1.0, -1)
        gl = cv2.GaussianBlur(gl, (0, 0), 28) * 0.55
        sub[:] = sub * (1 - gl[..., None]) + np.array(VENT_GLOW, np.float32) * gl[..., None]
        # 3 px ink outline
        ink = np.zeros((sub.shape[0] * 4, sub.shape[1] * 4), np.uint8)
        cv2.polylines(ink, [np.round(np.array(loc(cone)) * 4).astype(np.int32)], True, 255, 12, cv2.LINE_AA)
        ink = cv2.resize(ink, (sub.shape[1], sub.shape[0]), interpolation=cv2.INTER_AREA).astype(np.float32) / 255.0
        sub[:] = sub * (1 - ink[..., None]) + np.array(VENT_INK, np.float32) * ink[..., None]
    return np.clip(out, 0, 255).astype(np.uint8)


WHALE_PLEATS = [(930, 340), (1850, 295), (1860, 610), (930, 580)]   # pleat lines run below the belly


def whale_recut(img):
    """Whale: navy body + pectoral fin (components near the body), the throat pleat lines, the cyan rim, the tail
    fluke rebuilt above the frame by mirroring the lower fluke across the tail axis, and a 3 px ink (#061a33).
    Returns (bgra, off, hole_mask) where off may be negative in y (the fluke rises above the illustration)."""
    L = cv2.cvtColor(img, cv2.COLOR_BGR2LAB)[..., 0].astype(int)
    box = np.zeros(L.shape, bool)
    box[0:720, 800:3240] = True
    # the broken marble arch the head overlaps (pale, unsaturated, with its own ink) is not whale
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV).astype(int)
    arch = (hsv[..., 1] < 90) & (hsv[..., 2] > 140)
    arch[:, 1100:] = False
    arch[:, :700] = False
    arch = cv2.dilate(arch.astype(np.uint8), np.ones((25, 25), np.uint8)) > 0
    dark = (L < 75) & box & ~arch
    body = _largest(dark)
    near = cv2.dilate(body.astype(np.uint8), np.ones((61, 61), np.uint8)) > 0
    n, lab, st, _ = cv2.connectedComponentsWithStats(dark.astype(np.uint8))
    for i in range(1, n):
        if st[i, cv2.CC_STAT_AREA] > 150 and near[lab == i].any():
            body |= lab == i                                           # pectoral fin
    body = _fill_enclosed(cv2.morphologyEx(body.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((25, 25), np.uint8)) > 0)
    # pleats: darker-than-surroundings lines inside the pleat quad
    quad = np.zeros(L.shape, np.uint8)
    cv2.fillConvexPoly(quad, np.array(WHALE_PLEATS, np.int32), 1)
    g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).astype(np.float32)
    bgm = cv2.medianBlur(img, 31)
    pleat = (cv2.cvtColor(bgm, cv2.COLOR_BGR2GRAY).astype(np.float32) - g > 14) & (quad > 0) & ~body
    pleat = cv2.morphologyEx(pleat.astype(np.uint8), cv2.MORPH_OPEN, np.ones((2, 2), np.uint8)) > 0
    n2, lab2, st2, _ = cv2.connectedComponentsWithStats(pleat.astype(np.uint8))
    pleat = np.isin(lab2, [i for i in range(1, n2) if st2[i, cv2.CC_STAT_WIDTH] > 220])   # long lines, not fish
    rim = (cv2.dilate(body.astype(np.uint8), np.ones((15, 15), np.uint8)) > 0)    # the cyan rim line
    sil = rim & ~(arch & ~body)
    # canvas with headroom for the rebuilt fluke
    pad = 220
    H, W = img.shape[:2]
    canvas = np.zeros((H + pad, W, 3), np.uint8)
    canvas[pad:] = img
    msk = np.zeros((H + pad, W), bool)
    msk[pad:] = sil
    # tail axis through the peduncle centre at x=2928 (top 12, bottom 130), direction of the body axis there
    p0 = np.array([2928.0, 71.0 + pad])
    u = np.array([1.0, -0.35]) / np.hypot(1.0, 0.35)
    nrm = np.array([-u[1], u[0]])                    # points down-right of the axis
    ys, xs = np.nonzero(msk[:, 2928:3240])
    xs = xs + 2928
    rel = np.column_stack([xs, ys]) - p0
    side = rel @ nrm
    low = side > 0                                   # the lower fluke half
    src = np.column_stack([xs[low], ys[low]])
    refl = src - 2 * ((src - p0) @ nrm)[:, None] * nrm[None, :]
    rx = np.round(refl[:, 0]).astype(int)
    ry = np.round(refl[:, 1]).astype(int)
    ok = (rx >= 0) & (rx < W) & (ry >= 0) & (ry < pad + 4)
    canvas[ry[ok], rx[ok]] = canvas[src[ok, 1], src[ok, 0]]
    msk[ry[ok], rx[ok]] = True
    msk = _fill_enclosed(cv2.morphologyEx(msk.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((7, 7), np.uint8)) > 0)
    # the fluke region above the frame gets the body navy where the mirror left pinholes
    navy = np.median(img[body & (L < 60)].reshape(-1, 3), axis=0).astype(np.uint8)
    top = msk.copy()
    top[pad:] = False
    holes = top & (canvas.sum(2) == 0)
    canvas[holes] = navy
    ink = (cv2.dilate(msk.astype(np.uint8), np.ones((7, 7), np.uint8)) > 0) & ~msk
    canvas[ink] = (0x33, 0x1A, 0x06)
    pm = np.zeros((H + pad, W), bool)
    pm[pad:] = pleat
    full = msk | ink | pm
    a = alpha_from_mask(full)
    bgra, (bx, by) = _crop_bgra(canvas, full, a)
    hole = (cv2.dilate((sil | pleat).astype(np.uint8), np.ones((9, 9), np.uint8)) > 0)
    return bgra, (bx, by - pad), hole


def mountain_fog_alpha(img, plats_mask, y0):
    """Mountain mist without the platform stencil (art review M8): whiteness alpha, filled across the platform
    silhouettes by normalized convolution, eased so the top 25 % of the strip stays under 0.3."""
    H = img.shape[0]
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV).astype(np.float32)
    whit = np.clip((hsv[..., 2] - 185) / 55.0, 0, 1) * np.clip((75 - hsv[..., 1]) / 50.0, 0, 1)
    blocked = cv2.dilate(plats_mask.astype(np.uint8), np.ones((31, 31), np.uint8)) > 0
    filled = _norm_blur(whit, ~blocked, 60)
    whit = np.where(blocked, filled, whit)
    t = np.clip((np.arange(H, dtype=np.float32) - y0) / 420.0, 0, 1) ** 1.5
    return cv2.GaussianBlur(whit * t[:, None], (0, 0), 6), whit


def build_sprites(sid):
    st = STAGES[sid]
    ppu = st["ppu"]
    ox, oy = origin(sid)
    img = illustration(sid)
    recs = []
    plats, _ = cut_platforms(sid, img)
    # Only what moves or is a hazard leaves the Backdrop (lead direction after art review B1/B5/M9): static
    # platforms stay painted exactly as in Full.png and their Plat_ sprite covers the same pixels.
    H, W = img.shape[:2]
    moving = np.zeros((H, W), bool)
    all_plats = np.zeros((H, W), bool)
    for name, (bgra, (x, y)) in plats.items():
        h, w = bgra.shape[:2]
        mm = bgra[..., 3] > 0
        all_plats[y:y + h, x:x + w] |= mm
        if name in st["moving"]:
            moving[y:y + h, x:x + w] |= mm
    back = img.copy()
    hazards = {}
    if sid == "Mountain":
        hole = cv2.dilate(moving.astype(np.uint8), np.ones((9, 9), np.uint8)) > 0
        back = fill_from_plate(back, img, hole, layer_plate(sid))
    if sid == "Volcano":
        vents = []
        for side in ("Left", "Right"):
            bgra, off, fm = pillar_matte(illustration(sid, raw=True), side)   # fire cones unchanged (raw art)
            vents.append(fm)
            name = "Haz_Pillar" + side
            recs.append(_place(sid, name, bgra, off, anchor_px(sid, name), "hazard", 20,
                               pivot="base centre on the lava surface",
                               note="fire cone only (polygon + fire-hue matte, 2 px ink); the Backdrop keeps a "
                                    "dormant vent at the same place; scale Y for burst height"))
        back = dormant_vents(back, vents)
        recs.append(_rec(sid, save_sprite(sid, "Haz_Ember", ember_sprite(), (32, 48)), "particle", (0, 0), 25,
                         note="ember particle; worldMilli unused"))
        hazards["lavaTopMilli"] = to_milli(sid, 0, VOLCANO_LAVA_TOP_PX)[1]
        hazards["artPillarXMilli"] = [HAZ_ANCHOR_MILLI["Haz_PillarLeft"][0], HAZ_ANCHOR_MILLI["Haz_PillarRight"][0]]
    if sid == "BirdLair":
        bgra, off, m = bird_silhouette(img)
        # + dark fragments next to it (the head tip, split off by a wind line)
        x0, y0, x1, y1 = BIRD_SIL_BOX
        dark = np.zeros_like(m)
        dark[y0:y1, x0:x1] = cv2.cvtColor(img[y0:y1, x0:x1], cv2.COLOR_BGR2HSV)[..., 2] < 110
        m = m | (dark & (cv2.dilate(m.astype(np.uint8), np.ones((121, 121), np.uint8)) > 0))
        sky_hole = cv2.dilate(m.astype(np.uint8), np.ones((111, 111), np.uint8)) > 0   # silhouette + its light halo
        back = fill_sky_poly(back, img, sky_hole, (700, 60, 3200, 1080))
        recs.append(_place(sid, "Haz_BirdSilhouette", bgra, off, anchor_px(sid, "Haz_BirdSilhouette"),
                           "far-creature", -25, note="circling background bird (art position); not a hitbox"))
        ev = bird_event(sid)
        # event-frame scale: the event frame shows the same arena at 2404 / 3840 of the illustration's size
        evs = 3840.0 / (BIRD_EVENT_PAGE[2] - BIRD_EVENT_PAGE[0])
        ev = cv2.resize(ev, None, fx=evs, fy=evs, interpolation=cv2.INTER_CUBIC)
        eh, ew = ev.shape[:2]
        rec = save_sprite(sid, "Haz_BirdSwipe", ev, (ew / 2.0, eh / 2.0))
        recs.append(_rec(sid, rec, "event", (0, 0), 30, pivot="centre",
                         note="swooping bird facing right, rescaled to illustration px (x%.3f); flip X for a "
                              "leftward pass; the sim drives its position" % evs))
    if sid == "Ocean":
        bgra, off, hole = whale_recut(img)
        back = fill_from_plate(back, img, hole, layer_plate(sid))
        recs.append(_place(sid, "Haz_Whale", bgra, off, anchor_px(sid, "Haz_Whale"), "hazard", 30,
                           pivot="centre of the original art cut",
                           note="worldMilli = art (resting) position; fluke rebuilt above the frame; the sim "
                                "drives the descent"))
    # platforms (pivot = exact walkable-top centre from the json numbers)
    for name, (bgra, off) in plats.items():
        top, l, r = st["plats"][name]
        lm, tm = to_milli(sid, l, top)
        rm, _ = to_milli(sid, r, top)
        cxm = (lm + rm) // 2
        anchor = (ox + cxm * ppu / 1000.0, oy - tm * ppu / 1000.0)
        order = 9 if (sid == "BirdLair" and name in ("L", "R")) else 10
        recs.append(_place(sid, "Plat_" + name, bgra, off, anchor, "platform", order, platform=name,
                           moves=name in st["moving"]))
    # backdrop
    recs.append(_rec(sid, save_sprite(sid, "Backdrop", back, (ox, oy)), "backdrop", (0, 0), -10))
    fg = foreground(sid, img, back, all_plats)
    if fg is not None:
        bgra, off = fg
        if sid == "Volcano":
            recs.append(_place(sid, "Foreground", bgra, off, (ox, oy), "front", 50, opacity=1.0,
                               parallaxFactor=1.0,
                               note="GL-3 magma lake, opaque, drawn over fighters so a fall sinks into the lava; "
                                    "gameplay plane (no parallax); lava surface = hazards.lavaTopMilli"))
        else:
            recs.append(_place(sid, "Foreground", bgra, off, (ox, oy), "front", 50, opacity=0.4,
                               parallaxFactor=1.1, note="drawn over fighters; never covers the MAIN top band"))
    # parallax
    W = PANEL[2]
    cxw = (W / 2.0 - ox) * 1000.0 / ppu
    cyw = (oy - PANEL[3] / 2.0) * 1000.0 / ppu
    for which, cfg in PARALLAX.items():
        t = thumb(sid, which)
        up = upscale(t)
        a = mid_alpha(cv2.resize(cv2.fastNlMeansDenoisingColored(t, None, 5, 5, 5, 15), (up.shape[1], up.shape[0]),
                                 interpolation=cv2.INTER_LINEAR),
                      MID_KEY.get(sid, (6.0, 14.0))) \
            if which == "Mid" else np.full(up.shape[:2], 255, np.uint8)
        lp = round(ppu * cfg["scale_ppu"], 3)
        rec = save_sprite(sid, which, np.dstack([up, a]), (up.shape[1] / 2.0, up.shape[0] / 2.0), ppu=lp)
        recs.append(_rec(sid, rec, which.lower(), (round(cxw), round(cyw)), cfg["order"], pivot="centre",
                         parallaxFactor=cfg["factor"],
                         note="shows only outside the opaque Backdrop; centred on the illustration centre"))
    preview(sid, recs)
    return recs, hazards


def preview(sid, recs):
    """_preview.png: Backdrop + every platform / hazard / front sprite placed purely from its json record
    (worldMilli + pivot), red lines on the walkable tops. Proves art and colliders line up."""
    st = STAGES[sid]
    ppu = st["ppu"]
    ox, oy = origin(sid)
    canvas = None
    layers = sorted([r for r in recs if r["layer"] in ("backdrop", "platform", "hazard", "front")],
                    key=lambda r: r["order"])
    for r in layers:
        im = cv2.imread(os.path.join(OUT, sid, r["file"].split("/")[-1] + ".png"), cv2.IMREAD_UNCHANGED)
        if canvas is None:
            canvas = np.zeros((PANEL[3], PANEL[2], 3), np.float32)
        px = ox + r["worldMilli"][0] * ppu / 1000.0 - r["pivotPx"][0]
        py = oy - r["worldMilli"][1] * ppu / 1000.0 - r["pivotPx"][1]
        x, y = int(round(px)), int(round(py))
        h, w = im.shape[:2]
        cx0, cy0, cx1, cy1 = max(0, x), max(0, y), min(PANEL[2], x + w), min(PANEL[3], y + h)
        if cx1 <= cx0 or cy1 <= cy0:
            continue
        part = im[cy0 - y:cy1 - y, cx0 - x:cx1 - x].astype(np.float32)
        a = part[..., 3:4] / 255.0 if part.shape[2] == 4 else np.ones(part.shape[:2] + (1,), np.float32)
        a = a * r.get("opacity", 1.0)
        canvas[cy0:cy1, cx0:cx1] = part[..., :3] * a + canvas[cy0:cy1, cx0:cx1] * (1 - a)
    canvas = canvas.astype(np.uint8)
    for name, (top, l, rr) in st["plats"].items():
        cv2.line(canvas, (l, top), (rr, top), (0, 0, 255), 4)
        cv2.putText(canvas, name, (l + 10, top - 14), cv2.FONT_HERSHEY_SIMPLEX, 1.6, (0, 0, 255), 4)
    cv2.circle(canvas, (ox, oy), 12, (0, 255, 255), -1)
    small = cv2.resize(canvas, (1920, 1080), interpolation=cv2.INTER_AREA)
    # QA preview lives OUTSIDE Resources (anything under Resources ships in every build).
    prev_dir = os.path.join(ROOT, "design", "art", "stage-previews")
    os.makedirs(prev_dir, exist_ok=True)
    cv2.imwrite(os.path.join(prev_dir, sid + ".png"), small)


def fill_rows(back, img, hole):
    """Smooth-sky fill: every row of the hole is a linear blend between the median colours just left and right of
    it (taken from the original art, wind lines excluded by the median), then a light blur. Cleaner than Telea on a
    gradient sky."""
    out = back.astype(np.float32)
    small = cv2.medianBlur(cv2.resize(img, None, fx=0.25, fy=0.25, interpolation=cv2.INTER_AREA), 9)  # no lines
    src = cv2.resize(small, (img.shape[1], img.shape[0]), interpolation=cv2.INTER_LINEAR).astype(np.float32)
    H, W = hole.shape
    for y in range(H):
        xs = np.nonzero(hole[y])[0]
        if xs.size == 0:
            continue
        # contiguous runs
        breaks = np.nonzero(np.diff(xs) > 1)[0]
        starts = np.r_[xs[0], xs[breaks + 1]]
        ends = np.r_[xs[breaks], xs[-1]]
        for a, b in zip(starts, ends):
            la = src[y, max(0, a - 14):max(1, a - 2)]
            rb = src[y, min(W - 1, b + 3):min(W, b + 15)]
            cl = np.median(la, axis=0) if la.size else np.median(rb, axis=0)
            cr = np.median(rb, axis=0) if rb.size else cl
            t = np.linspace(0, 1, b - a + 1, dtype=np.float32)[:, None]
            out[y, a:b + 1] = cl * (1 - t) + cr * t
    sm = cv2.GaussianBlur(out, (0, 0), 6)
    k = cv2.GaussianBlur(hole.astype(np.float32), (0, 0), 3)[..., None]
    k = np.maximum(k, cv2.erode(hole.astype(np.uint8), np.ones((9, 9), np.uint8))[..., None].astype(np.float32))
    res = back.astype(np.float32) * (1 - k) + sm * k
    return np.clip(res, 0, 255).astype(np.uint8)


# ------------------------------------------------------------------------- pass-2 source repairs (Volcano, Plain)
EMBER_CAP_PX = 26               # ~24 px on screen at the default match zoom (pass 2 item 2)
EMBER_PLAY_BAND = (280, 1470)   # illustration y: L4 top (323) .. MAIN lip (1433)
EMBER_PLAY_CUT = 0.4


def find_embers(im):
    """Painted ember diamonds: compact bright warm blobs whose shape matches a diamond (IoU with the ideal diamond
    in their bounding box), outside the fire cones and the lava. Drip segments and lip highlights fail the shape
    test."""
    v = im.max(2)
    bg = cv2.medianBlur(v, 61)
    i = im.astype(int)
    b, g, r = i[..., 0], i[..., 1], i[..., 2]
    hot = ((v.astype(int) - bg.astype(int)) > 45) & (r > 200) & ((r - b) > 40)
    hot = cv2.morphologyEx(hot.astype(np.uint8), cv2.MORPH_OPEN, np.ones((7, 1), np.uint8))  # drops lip highlights
    excl = np.zeros(v.shape, np.uint8)
    for apex, bl, br in PILLAR_TRI.values():
        cv2.fillConvexPoly(excl, np.array([apex, bl, br], np.int32), 1)
    excl = cv2.dilate(excl, np.ones((25, 25), np.uint8))
    excl[VOLCANO_LAVA_TOP_PX - 30:] = 1
    n, lab, st, cen = cv2.connectedComponentsWithStats(hot, connectivity=8)
    out = []
    for k in range(1, n):
        x, y, w, h, a = st[k]
        if not (9 <= h <= 100 and 4 <= w <= 60 and 1.05 <= h / float(w) <= 4.0):
            continue
        cx, cy = cen[k]
        if excl[int(cy), int(cx)]:
            continue
        ideal = np.zeros((h, w), np.uint8)
        cv2.fillConvexPoly(ideal, np.array([(w // 2, 0), (w - 1, h // 2), (w // 2, h - 1), (0, h // 2)], np.int32), 1)
        comp = lab[y:y + h, x:x + w] == k
        iou = (comp & (ideal > 0)).sum() / float((comp | (ideal > 0)).sum())
        if iou < 0.55:
            continue
        out.append(dict(x=int(x), y=int(y), w=int(w), h=int(h), cx=float(cx), cy=float(cy), lab=k))
    return out, lab


def _row_fill(sub, mask):
    """Per row, linear interpolation between the pixels just left and right of each masked run. Lips, the light
    shaft and the cave gradient are near-uniform horizontally, so this rebuilds them cleanly (no blur blotch)."""
    out = sub.astype(np.float32)
    H, W = mask.shape
    for y in range(H):
        xs = np.flatnonzero(mask[y])
        if xs.size == 0:
            continue
        br = np.flatnonzero(np.diff(xs) > 1)
        for a, b in zip(np.r_[xs[0], xs[br + 1]], np.r_[xs[br], xs[-1]]):
            cl = out[y, a - 1] if a > 0 else out[y, min(W - 1, b + 1)]
            cr = out[y, b + 1] if b < W - 1 else cl
            t = np.linspace(0, 1, b - a + 3, dtype=np.float32)[1:-1, None]
            out[y, a:b + 1] = cl * (1 - t) + cr * t
    return out


def _median_bg(src, cores):
    """Large-window median background (edge-preserving, ignores the small ember glows), computed at 1/4 size."""
    q = cv2.inpaint(src, cv2.dilate(cores * 255, np.ones((7, 7), np.uint8)), 3, cv2.INPAINT_TELEA)
    small = cv2.resize(q, (src.shape[1] // 4, src.shape[0] // 4), interpolation=cv2.INTER_AREA)
    med = cv2.medianBlur(small, 41)
    return cv2.resize(med, (src.shape[1], src.shape[0]), interpolation=cv2.INTER_LINEAR).astype(np.float32)


def _erase_ember(im, e, lab, bgmed):
    """Core: row-wise fill of the diamond (dilated 2 px). Glow: subtract only its smooth excess over the median
    background inside a 1.7 h disk, with a radial falloff (no ring)."""
    H, W = im.shape[:2]
    h = e["h"]
    R = h * 3.0
    x0, y0 = max(0, int(e["cx"] - R * 1.2) - 3), max(0, int(e["cy"] - R * 1.2) - 3)
    x1, y1 = min(W, int(e["cx"] + R * 1.2) + 4), min(H, int(e["cy"] + R * 1.2) + 4)
    sub = im[y0:y1, x0:x1]
    if "rect" in e:                      # lip ember: an explicit rectangle (the part over the lip is not "hot")
        core = np.zeros(sub.shape[:2], bool)
        rx0, ry0, rx1, ry1 = e["rect"]
        core[max(0, ry0 - y0):max(0, ry1 - y0), max(0, rx0 - x0):max(0, rx1 - x0)] = True
    else:
        core = cv2.dilate((lab[y0:y1, x0:x1] == e["lab"]).astype(np.uint8), np.ones((7, 7), np.uint8)) > 0
    f = _row_fill(sub, core)
    soft = cv2.GaussianBlur(f, (0, 0), 1.5)
    f[core] = soft[core]                                          # no row-fill dashes
    yy, xx = np.mgrid[y0:y1, x0:x1]
    rr = np.hypot(xx - e["cx"], yy - e["cy"])
    low = cv2.GaussianBlur(f, (0, 0), max(2.0, h * 0.45))
    # min(f, low): the blur must not push the glow outward (that over-subtracts at the glow edge = dark rim);
    # detail brighter than the smooth glow (crack lines) stays
    excess = np.clip(np.minimum(f, low) - bgmed[y0:y1, x0:x1], 0, None)
    fall = np.clip((R - rr) / (R * 0.5), 0, 1)[..., None]
    f = f - excess * fall
    im[y0:y1, x0:x1] = np.clip(f, 0, 255).astype(np.uint8)


def _draw_ember(im, cx, cy, h, core_bgr):
    """Redraw a diamond at cap size: anti-aliased core in its own colour + a soft orange glow."""
    H, W = im.shape[:2]
    w = h * 0.5
    pad = int(h * 1.6) + 4
    x0, y0 = max(0, int(cx) - pad), max(0, int(cy) - pad)
    x1, y1 = min(W, int(cx) + pad), min(H, int(cy) + pad)
    sub = im[y0:y1, x0:x1].astype(np.float32)
    pts = [(cx - x0, cy - h / 2.0 - y0), (cx + w / 2.0 - x0, cy - y0), (cx - x0, cy + h / 2.0 - y0),
           (cx - w / 2.0 - x0, cy - y0)]
    cov = _aa_poly(sub.shape[:2], pts)
    glow = cv2.GaussianBlur(cov, (0, 0), h * 0.45) * 1.4
    orange = np.array([0x1F, 0x9A, 0xFF], np.float32)
    sub = sub + glow[..., None] * orange * 0.4
    sub = sub * (1 - cov[..., None]) + np.array(core_bgr, np.float32) * cov[..., None]
    im[y0:y1, x0:x1] = np.clip(sub, 0, 255).astype(np.uint8)


def tame_embers(im):
    """Pass 2 items 2 and 8. Painted embers on a platform's walkable lip are removed (they read as pickups); 40 % of
    the other embers in the play band (MAIN lip .. L4) are removed; every kept ember taller than the cap is redrawn
    at the cap. Deterministic (fixed seed)."""
    im = im.copy()
    src = im.copy()
    embers, lab = find_embers(im)
    cores = np.isin(lab, [e["lab"] for e in embers]).astype(np.uint8)
    bgmed = _median_bg(src, cores)
    plats = list(STAGES["Volcano"]["plats"].values())
    plats_top = {k: v[0] for k, v in STAGES["Volcano"]["plats"].items()}
    for e in embers:
        e["lip"] = any(l - 10 <= e["cx"] <= r + 10 and top - 30 <= e["y"] + e["h"] and e["y"] <= top + 20
                       for top, l, r in plats)
        # baked on a platform face (m2): inside a platform cut rectangle, below the lip
        e["lip"] = e["lip"] or any(rx0 <= e["cx"] <= rx1 and plats_top[name] + 20 < e["cy"] <= ry1
                                   for name, (rx0, ry0, rx1, ry1) in CUTS["Volcano"]["rois"].items())
    rng = np.random.RandomState(1234)
    order = rng.permutation(len(embers))
    band = [k for k in order if EMBER_PLAY_BAND[0] <= embers[k]["cy"] <= EMBER_PLAY_BAND[1] and not embers[k]["lip"]]
    cut = set(band[:int(round(len(band) * EMBER_PLAY_CUT))])
    stats = dict(found=len(embers), play_band=len(band), lip=0, cut=0, shrunk=0)
    for k, e in enumerate(embers):
        if e["lip"] or k in cut:
            _erase_ember(im, e, lab, bgmed)
            stats["lip" if e["lip"] else "cut"] += 1
        elif e["h"] > EMBER_CAP_PX:
            sel = lab[e["y"]:e["y"] + e["h"], e["x"]:e["x"] + e["w"]] == e["lab"]
            core = np.median(src[e["y"]:e["y"] + e["h"], e["x"]:e["x"] + e["w"]][sel].reshape(-1, 3), axis=0)
            _erase_ember(im, e, lab, bgmed)
            _draw_ember(im, e["cx"], e["cy"], EMBER_CAP_PX, core)
            stats["shrunk"] += 1
    # embers merged with a walkable lip (the diamond test misses them): pixels brighter than their own row's median
    # along the platform (the lip highlight is uniform along the row, a diamond is not), lip band only
    for top, l, r in plats:
        y0, y1 = top - 42, top + 24
        zone = im[y0:y1, l:r]
        vv = zone.max(2).astype(np.float32)
        lift = vv - np.median(vv, axis=1, keepdims=True)
        zi = zone.astype(int)
        hot = (lift > 45) & (zi[..., 2] > 190) & ((zi[..., 2] - zi[..., 0]) > 50)
        hot = cv2.morphologyEx(hot.astype(np.uint8), cv2.MORPH_OPEN, np.ones((2, 2), np.uint8))
        n, lb, st, cen = cv2.connectedComponentsWithStats(hot)
        for k in range(1, n):
            x, y, w, h, a = st[k]
            if a < 10 or w > 70 or h > 90:
                continue
            ex = dict(cx=float(l + x + w / 2.0), cy=float(y0 + y + h / 2.0), h=max(12, int(h)), lab=-1,
                      rect=(l + x - 5, y0 + y - 5, l + x + w + 5, y0 + y + h + 5))
            _erase_ember(im, ex, lab, bgmed)
            stats["lip"] += 1
    print("   Volcano embers:", stats)
    return im


PLAIN_FADE_Y, PLAIN_FADE_W = 1376, 720


def fix_plain_edges(im):
    """Pass 2 item 3 (M11): the sheet paints a cream fade over the outer ~700 px of the ground band (below the hill
    line). Replace each side with a mirror of the unfaded ground next to it (seamless at the seam by construction),
    feathered in over 24 rows at the top so the hill ink line is untouched."""
    im = im.copy()
    H, W = im.shape[:2]
    y0, fw = PLAIN_FADE_Y, PLAIN_FADE_W
    left = im[:, fw:2 * fw][:, ::-1].copy()
    right = im[:, W - 2 * fw:W - fw][:, ::-1].copy()
    ramp = np.clip((np.arange(H, dtype=np.float32) - y0) / 24.0, 0, 1)[:, None, None]
    im[:, :fw] = (im[:, :fw] * (1 - ramp) + left * ramp).astype(np.uint8)
    im[:, W - fw:] = (im[:, W - fw:] * (1 - ramp) + right * ramp).astype(np.uint8)
    return im


def load_json():
    if os.path.exists(JSON_PATH):
        with open(JSON_PATH, encoding="utf-8") as f:
            return json.load(f)["stages"]
    return {}


def main(argv):
    mode = argv[0] if argv and argv[0] in ("geometry", "sprites", "all") else "all"
    ids = [a for a in argv if a in STAGES] or list(STAGES)
    recs = load_json()
    for sid in ids:
        if mode in ("sprites", "all"):
            sprites, hazards = build_sprites(sid)
        else:
            sprites = [s for s in recs.get(sid, {}).get("sprites", []) if s.get("layer") != "full"]
            hazards = recs.get(sid, {}).get("hazards", {})
        recs[sid] = build_geometry(sid, sprites)
        recs[sid]["hazards"] = hazards
        print(sid, "ppu", STAGES[sid]["ppu"], "origin", recs[sid]["originPx"], recs[sid]["imageWorldMilli"])
        for p in recs[sid]["platforms"]:
            print("   %-5s top %6d  x %7d .. %7d  (w %d)" % (p["id"], p["topMilli"], p["leftMilli"],
                                                         p["rightMilli"], p["widthMilli"]))
    write_json({k: recs[k] for k in STAGES if k in recs})


if __name__ == "__main__":
    main(sys.argv[1:])
