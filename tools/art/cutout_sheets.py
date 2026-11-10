"""
Character-sheet cutter for Chibi Clash.

    python -P tools/art/cutout_sheets.py [--preview DIR] [Id ...]

Reads design/art/character-sheets/<id>-costume.png and writes, per character (CharacterRoster ids):

    Assets/ChibiClash/Resources/Characters/<Id>.png            full-body FRONT figure (turnaround), 1024x1024 RGBA,
                                                             feet centred at (512, 1010), figure 970 px tall
    Assets/ChibiClash/Resources/Characters/<Id>_Portrait.png   bust crop of the big action pose, 512x512 RGBA
    Assets/ChibiClash/Resources/Weapons/Hero_<Id>.png          weapon from the WEAPON panel, upright: business end up,
                                                             grip near the bottom (the layout gen_cutout_anims.py expects)

Background removal ("ink-barrier flood"): the sheets are line art with dark outlines on a pale, brush-stroked
background. A flood fill from the region border runs through every pixel that is not ink (dark outlines, closed by a
1 px dilation so small gaps do not leak), so it never enters a figure, however pale its clothes are. The flood is then
grown back to the outline, enclosed pockets of plain background (between an arm and the body) are cleared, specks are
dropped and only the main figure is kept. Colours are bled outward before upscaling so the edge never picks up the
pale background (no halo on dark stages); alpha is upscaled smoothly and feathered by a fraction of a pixel.

Every box below is in sheet pixels (the sheets are 1055 x 1491). Re-run after editing a sheet or a box.
"""
import os
import sys

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unity_meta import save_with_meta, write_folder_meta  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SHEETS = os.path.join(ROOT, "design", "art", "character-sheets")
RES = os.environ.get("CHIBI_RES") or os.path.join(ROOT, "Assets", "ChibiClash", "Resources")   # CHIBI_RES: stage elsewhere

# Per character:
#   front:   region around the FRONT turnaround figure (borders must be background)
#   pose:    region around the big action pose (borders must be background), used for the portrait
#   portrait: square crop (x0, y0, size) inside the sheet, taken from the segmented pose
#   weapon:  region around the weapon drawing; "drop" rectangles are blanked first (labels, detail insets, arrows)
#   wrot:    how to turn the weapon crop so its business end points UP (degrees counter-clockwise, any angle)
#   wflip:   mirror the weapon horizontally after turning (bows: arc forward = right, string = left)
#   ink:     luminance below which a pixel counts as outline
#   pocket:  max colour distance from the background for an enclosed pocket to be cleared
CFG = {
    "Kaia": dict(sheet="kaia", front=(548, 40, 725, 428), pose=(0, 0, 560, 862), portrait=(205, 8, 280),
                 weapon=(560, 628, 920, 712), drop=[], wrot=-90 - 10, wflip=False),
    "Bruna": dict(sheet="bruna", front=(535, 40, 728, 356), pose=(0, 0, 545, 800), portrait=(262, 14, 250),
                  weapon=(560, 518, 912, 790), drop=[(700, 704, 850, 815), (836, 693, 912, 745)], wrot=54, wflip=False),
    "Ren": dict(sheet="ren", front=(552, 40, 722, 384), pose=(0, 0, 560, 845), portrait=(135, 100, 230), pdrop=[(30, 125, 205, 172)],
                weapon=(645, 520, 845, 862), drop=[], wrot=0, wflip=True, wstring=True),
    "Lyra": dict(sheet="lyra", ink=140, front=(556, 42, 716, 378), pose=(0, 0, 525, 805), portrait=(170, 35, 230), pdrop=[(30, 130, 205, 175)],
                 weapon=(728, 552, 868, 726), drop=[], wrot=34, wflip=False),
    "Taro": dict(sheet="taro", front=(546, 36, 722, 395), pose=(0, 0, 565, 835), portrait=(260, 55, 250),
                 weapon=(512, 586, 1048, 676), drop=[], wrot=-90, wflip=False, wclose=2, wthick=1.9, woutline=3),
}

INK = 105          # outline luminance threshold
POCKET = 16        # enclosed-pocket colour distance
MIN_SPECK = 60     # fg components smaller than this (sheet px) are dropped
FIG_H = 970        # figure height in the 1024 canvas (fork layout)
FEET_Y = 1010      # feet line in the 1024 canvas


def load(name):
    im = cv2.imread(os.path.join(SHEETS, name + "-costume.png"), cv2.IMREAD_COLOR)
    if im is None:
        raise SystemExit("missing sheet: " + name)
    return im


def bg_colour(bgr):
    b = np.concatenate([bgr[0], bgr[-1], bgr[:, 0], bgr[:, -1]]).astype(np.float32)
    light = b[b.mean(1) > 215]
    return np.median(light if len(light) > 10 else b, 0)


def segment(bgr, ink=INK, pocket=POCKET, seed_sides="tblr", near=6, refine=False, close=1):
    """Hard foreground mask (uint8 0/1) for a region whose seeded borders are background."""
    h, w = bgr.shape[:2]
    lum = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY).astype(np.float32)
    B = bg_colour(bgr)
    dist = np.sqrt(((bgr.astype(np.float32) - B) ** 2).sum(2))
    is_ink = (lum < ink).astype(np.uint8)
    barrier = cv2.dilate(is_ink, np.ones((2 * close + 1, 2 * close + 1), np.uint8))   # close outline gaps
    passable = (barrier == 0).astype(np.uint8)
    n, lab = cv2.connectedComponents(passable, connectivity=4)
    seeds = set()
    if "t" in seed_sides: seeds.update(np.unique(lab[0]))
    if "b" in seed_sides: seeds.update(np.unique(lab[-1]))
    if "l" in seed_sides: seeds.update(np.unique(lab[:, 0]))
    if "r" in seed_sides: seeds.update(np.unique(lab[:, -1]))
    seeds.discard(0)
    bg = np.isin(lab, list(seeds)).astype(np.uint8)
    # grow back up to the real outline (the dilation left a 1 px ring of background)
    grow_ok = ((is_ink == 0) & ((dist < 60) | (lum > 165))).astype(np.uint8)
    for _ in range(2 + close):
        bg = np.maximum(bg, cv2.dilate(bg, np.ones((3, 3), np.uint8)) & grow_ok)
    fg = 1 - bg
    if refine:
        fg = grabcut_restore(bgr, fg)
    # enclosed pockets of plain background
    n2, lab2, st2, _ = cv2.connectedComponentsWithStats(((fg == 1) & (is_ink == 0) & (dist < pocket)).astype(np.uint8), connectivity=4)
    for i in range(1, n2):
        if st2[i, cv2.CC_STAT_AREA] >= 12:
            m = lab2 == i
            ring = (cv2.dilate(m.astype(np.uint8), np.ones((3, 3), np.uint8)) == 1) & ~m
            outlined = (lum[ring] < ink + 40).mean() if ring.any() else 0.0   # real pockets are ringed by outline
            if dist[m].mean() < pocket * 0.75 and dist[m].std() < 8 and outlined > 0.6:
                fg[cv2.dilate(m.astype(np.uint8), np.ones((3, 3), np.uint8)).astype(bool) & (grow_ok == 1)] = 0
    # keep the main figure plus sizeable parts lying close to it (a hand or hair lock cut off by a background gap);
    # labels, badges and other drawings further away are dropped
    n3, lab3, st3, _ = cv2.connectedComponentsWithStats(fg, connectivity=8)
    if n3 <= 1:
        return fg
    main = 1 + int(np.argmax(st3[1:, cv2.CC_STAT_AREA]))
    out = (lab3 == main).astype(np.uint8)
    halo = cv2.dilate(out, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * near + 1, 2 * near + 1)))
    for i in range(1, n3):
        thin = min(st3[i, cv2.CC_STAT_WIDTH], st3[i, cv2.CC_STAT_HEIGHT]) <= 4      # rules, shadow dashes
        if i != main and not thin and st3[i, cv2.CC_STAT_AREA] >= MIN_SPECK and halo[lab3 == i].any():
            out[lab3 == i] = 1
    return out


def grabcut_restore(bgr, fg, reach=18, iters=5):
    """Repair flood leaks. A light line crossing an outline (a bow string, an arrow, a stroke of the background
    brush) can let the flood into a face or a sleeve. GrabCut learns the figure's colours from the flood result and
    may only ADD such leaked areas back (never remove what the flood kept); background further than `reach` px
    from the figure is fixed as background so brush strokes stay out."""
    dist_fg = cv2.distanceTransform((1 - fg).astype(np.uint8), cv2.DIST_L2, 3)
    gc = np.full(fg.shape, cv2.GC_PR_FGD, np.uint8)
    gc[(fg == 0) & (dist_fg <= reach)] = cv2.GC_PR_BGD
    gc[(fg == 0) & (dist_fg > reach)] = cv2.GC_BGD
    gc[cv2.erode(fg.astype(np.uint8), np.ones((5, 5), np.uint8)) == 1] = cv2.GC_FGD
    bgd, fgd = np.zeros((1, 65), np.float64), np.zeros((1, 65), np.float64)
    cv2.grabCut(bgr, gc, None, bgd, fgd, iters, cv2.GC_INIT_WITH_MASK)
    add = ((gc == cv2.GC_FGD) | (gc == cv2.GC_PR_FGD)) & (fg == 0)
    # only keep restored areas that are enclosed-ish: drop thin slivers along the outside of the silhouette
    add = cv2.morphologyEx(add.astype(np.uint8), cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    return np.maximum(fg, add).astype(np.uint8)


def unmix(bgr, fg):
    """Soft alpha + background-free colour for the outermost foreground pixels.

    Edge pixels are blends of the outline and the pale background. Their alpha comes from how much darker
    (or more colourful) than the background they are, and their colour is solved from C = a*F + (1-a)*B,
    so a 50 % grey edge becomes a half-transparent ink pixel instead of a light dot on dark stages."""
    B = bg_colour(bgr)
    c = bgr.astype(np.float32)
    lum = c.mean(2)
    bl = float(B.mean())
    d = c - B
    chroma = np.sqrt(((d - d.mean(2, keepdims=True)) ** 2).sum(2))
    a_l = np.clip((bl - lum) / max(1.0, bl - 60.0), 0, 1)
    a_c = np.clip(chroma / 45.0, 0, 1)
    edge = (fg == 1) & (cv2.dilate((1 - fg).astype(np.uint8), np.ones((3, 3), np.uint8)) == 1)
    alpha = fg.astype(np.float32)
    a = np.maximum(a_l, a_c)
    a = np.clip(a, 0.15, 1.0)
    alpha[edge] = a[edge]
    out = c.copy()
    F = (c - (1 - a[..., None]) * B) / a[..., None]
    out[edge] = np.clip(F[edge], 0, 255)
    return out.astype(np.uint8), alpha


def bleed(bgr, mask, iters=12):
    """Push foreground colours outward into the transparent area so resampling never mixes in the background."""
    out = bgr.astype(np.float32).copy()
    m = mask.astype(np.float32)
    k = np.ones((3, 3), np.float32)
    acc = out * m[..., None]
    for _ in range(iters):
        s = cv2.filter2D(acc, -1, k, borderType=cv2.BORDER_CONSTANT)
        c = cv2.filter2D(m, -1, k, borderType=cv2.BORDER_CONSTANT)
        new = (c > 0) & (m == 0)
        acc[new] = s[new] / c[new][:, None]
        m = np.where(new, 1.0, m)
        acc = acc * 1.0
    fill = m > 0
    out[fill] = acc[fill]
    return np.clip(out, 0, 255).astype(np.uint8)


def to_rgba(bgr, mask, scale, feather=0.5, sharpen=0.4):
    """Upscale a masked crop to RGBA (premultiplication-safe, mild unsharp mask, sub-pixel feather)."""
    col, alpha = unmix(bgr, mask)
    col = bleed(col, mask)
    size = (max(1, int(round(bgr.shape[1] * scale))), max(1, int(round(bgr.shape[0] * scale))))
    up = cv2.resize(col, size, interpolation=cv2.INTER_LANCZOS4 if scale > 1 else cv2.INTER_AREA)
    if sharpen > 0 and scale > 1:
        blur = cv2.GaussianBlur(up, (0, 0), 1.1 * scale / 2.5)
        up = cv2.addWeighted(up, 1 + sharpen, blur, -sharpen, 0)
    a = cv2.resize(alpha, size, interpolation=cv2.INTER_CUBIC if scale > 1 else cv2.INTER_AREA)
    if feather > 0:
        a = cv2.GaussianBlur(a, (0, 0), feather)
    a = np.clip(a, 0, 1)
    rgba = cv2.cvtColor(up, cv2.COLOR_BGR2BGRA)
    rgba[..., 3] = (a * 255).astype(np.uint8)
    rgba[rgba[..., 3] == 0, :3] = 0
    return rgba


def bbox(mask):
    ys, xs = np.where(mask > 0)
    return xs.min(), ys.min(), xs.max() + 1, ys.max() + 1


def front_cutout(cfg, sheet):
    x0, y0, x1, y1 = cfg["front"]
    roi = sheet[y0:y1, x0:x1]
    mask = segment(roi, ink=cfg.get("ink", INK), pocket=cfg.get("pocket", POCKET), refine=True)
    bx0, by0, bx1, by1 = bbox(mask)
    # horizontal centre = centre of the feet (lowest 8% of the figure)
    feet = mask[by1 - max(2, (by1 - by0) // 12):by1]
    fx = np.where(feet.any(0))[0]
    cx = (fx.min() + fx.max() + 1) / 2.0
    s = FIG_H / float(by1 - by0)
    pad = 2
    crop = roi[max(0, by0 - pad):by1 + pad, max(0, bx0 - pad):bx1 + pad]
    cm = mask[max(0, by0 - pad):by1 + pad, max(0, bx0 - pad):bx1 + pad]
    rgba = to_rgba(crop, cm, s)
    canvas = np.zeros((1024, 1024, 4), np.uint8)
    ox = int(round(512 - (cx - max(0, bx0 - pad)) * s))
    oy = int(round(FEET_Y - (by1 - max(0, by0 - pad)) * s))
    paste(canvas, rgba, ox, oy)
    return canvas


def portrait_cutout(cfg, sheet):
    x0, y0, x1, y1 = cfg["pose"]
    roi = sheet[y0:y1, x0:x1].copy()
    B = bg_colour(roi)
    for dx0, dy0, dx1, dy1 in cfg.get("pdrop", []):
        roi[dy0 - y0:dy1 - y0, dx0 - x0:dx1 - x0] = B
    mask = segment(roi, ink=cfg.get("ink", INK), pocket=cfg.get("pocket", POCKET), refine=True)
    px, py, ps = cfg["portrait"]
    px -= x0
    py -= y0
    crop = roi[py:py + ps, px:px + ps]
    cm = mask[py:py + ps, px:px + ps].copy()
    rgba = to_rgba(crop, cm, 512.0 / ps, feather=0.5, sharpen=0.35)
    # soft fade over the bottom 14% so the bust does not end on a hard cut
    h = rgba.shape[0]
    ramp = np.ones(h, np.float32)
    f0 = int(h * 0.86)
    ramp[f0:] = np.linspace(1, 0, h - f0)
    rgba[..., 3] = (rgba[..., 3].astype(np.float32) * ramp[:, None]).astype(np.uint8)
    return rgba


def weapon_cutout(cfg, sheet):
    x0, y0, x1, y1 = cfg["weapon"]
    roi = sheet[y0:y1, x0:x1].copy()
    B = bg_colour(roi)
    for dx0, dy0, dx1, dy1 in cfg.get("drop", []):
        roi[max(0, dy0 - y0):max(0, dy1 - y0), max(0, dx0 - x0):max(0, dx1 - x0)] = B
    mask = segment(roi, ink=cfg.get("wink", cfg.get("ink", INK)), pocket=cfg.get("pocket", POCKET), near=cfg.get("wnear", 2),
                   refine=True, close=cfg.get("wclose", 1))
    bx0, by0, bx1, by1 = bbox(mask)
    pad = 3
    crop = roi[max(0, by0 - pad):by1 + pad, max(0, bx0 - pad):bx1 + pad]
    cm = mask[max(0, by0 - pad):by1 + pad, max(0, bx0 - pad):bx1 + pad]
    rgba = to_rgba(crop, cm, 2.0, feather=0.5, sharpen=0.35)
    ang = cfg["wrot"]
    if ang is None:
        ang = principal_up_angle(rgba)
    rgba = rotate(rgba, ang)
    if cfg.get("wflip"):
        rgba = rgba[:, ::-1].copy()
    if cfg.get("wstring"):
        draw_string(rgba)
    if cfg.get("wthick", 1.0) != 1.0:
        # thin weapons (a glaive is 1/60 as wide as it is long) vanish at game scale: widen across the shaft
        h, w = rgba.shape[:2]
        rgba = cv2.resize(rgba, (int(round(w * cfg["wthick"])), h), interpolation=cv2.INTER_CUBIC)
    if cfg.get("woutline", 0):
        rgba = sticker_outline(rgba, cfg["woutline"])
    # trim and pad a little
    a = rgba[..., 3]
    ys, xs = np.where(a > 8)
    rgba = rgba[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    out = np.zeros((rgba.shape[0] + 16, rgba.shape[1] + 16, 4), np.uint8)
    out[8:-8, 8:-8] = rgba
    return out


def sticker_outline(rgba, px, colour=(26, 20, 24)):
    """Dark outline of px pixels behind the art so a thin weapon keeps a readable silhouette when scaled down."""
    pad = px + 2
    big = np.zeros((rgba.shape[0] + 2 * pad, rgba.shape[1] + 2 * pad, 4), np.uint8)
    big[pad:-pad, pad:-pad] = rgba
    a = big[..., 3].astype(np.float32) / 255.0
    k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * px + 1, 2 * px + 1))
    ring = cv2.GaussianBlur(cv2.dilate(a, k), (0, 0), 0.6)
    out = np.zeros_like(big, np.float32)
    out[..., :3] = colour
    out[..., 3] = ring
    # composite art over the outline
    fa = a[..., None]
    col = big[..., :3].astype(np.float32) * fa + out[..., :3] * ring[..., None] * (1 - fa)
    al = fa[..., 0] + ring * (1 - fa[..., 0])
    res = np.zeros_like(big)
    res[..., :3] = np.clip(col / np.maximum(al[..., None], 1e-4), 0, 255).astype(np.uint8)
    res[..., 3] = np.clip(al * 255, 0, 255).astype(np.uint8)
    return res


def draw_string(rgba, colour=(255, 240, 215)):
    """Bows: the sheet's string is a faint line the cutter drops; redraw it tip to tip (BGR colour, 2 px)."""
    a = rgba[..., 3] > 128
    ys, xs = np.where(a)
    top = (int(xs[ys == ys.min()].min()), int(ys.min()))
    bot = (int(xs[ys == ys.max()].min()), int(ys.max()))
    layer = np.zeros_like(rgba)
    cv2.line(layer, top, bot, colour + (255,), 3, cv2.LINE_AA)
    la = layer[..., 3:4].astype(np.float32) / 255.0
    under = rgba[..., 3:4].astype(np.float32) / 255.0
    rgba[..., :3] = (layer[..., :3] * la + rgba[..., :3] * (1 - la)).astype(np.uint8)
    rgba[..., 3] = (np.maximum(la, under)[..., 0] * 255).astype(np.uint8)


def principal_up_angle(rgba):
    """Angle (CCW degrees) that turns the weapon's long axis vertical, with the heavier end (head / crystal) on top."""
    a = rgba[..., 3] > 128
    ys, xs = np.where(a)
    pts = np.stack([xs, -ys], 1).astype(np.float32)          # y up
    c = pts.mean(0)
    cov = np.cov((pts - c).T)
    ev, evec = np.linalg.eigh(cov)
    v = evec[:, np.argmax(ev)]
    proj = (pts - c) @ v
    # heavier end = more mass beyond 30% of the half length
    L = np.abs(proj).max()
    pos = (proj > 0.3 * L).sum()
    neg = (proj < -0.3 * L).sum()
    if neg > pos:
        v = -v
    cur = np.degrees(np.arctan2(v[1], v[0]))
    return 90.0 - cur


def rotate(rgba, ang):
    """Rotate counter-clockwise by ang degrees, expanding the canvas."""
    h, w = rgba.shape[:2]
    M = cv2.getRotationMatrix2D((w / 2, h / 2), ang, 1.0)
    cos, sin = abs(M[0, 0]), abs(M[0, 1])
    nw, nh = int(h * sin + w * cos) + 2, int(h * cos + w * sin) + 2
    M[0, 2] += nw / 2 - w / 2
    M[1, 2] += nh / 2 - h / 2
    # rotate premultiplied colour so edges stay clean
    pm = rgba.astype(np.float32)
    pm[..., :3] *= pm[..., 3:4] / 255.0
    r = cv2.warpAffine(pm, M, (nw, nh), flags=cv2.INTER_CUBIC, borderValue=(0, 0, 0, 0))
    r = np.clip(r, 0, 255)
    al = r[..., 3:4]
    r[..., :3] = np.where(al > 0, r[..., :3] * 255.0 / np.maximum(al, 1e-3), 0)
    return np.clip(r, 0, 255).astype(np.uint8)


def paste(dst, src, x, y):
    h, w = src.shape[:2]
    sx0, sy0 = max(0, -x), max(0, -y)
    dx0, dy0 = max(0, x), max(0, y)
    ww = min(w - sx0, dst.shape[1] - dx0)
    hh = min(h - sy0, dst.shape[0] - dy0)
    if ww <= 0 or hh <= 0:
        return
    dst[dy0:dy0 + hh, dx0:dx0 + ww] = src[sy0:sy0 + hh, sx0:sx0 + ww]


def on_dark(rgba, colour=(40, 32, 30)):
    a = rgba[..., 3:4].astype(np.float32) / 255.0
    bg = np.empty(rgba.shape[:2] + (3,), np.float32)
    bg[:] = colour
    return (rgba[..., :3] * a + bg * (1 - a)).astype(np.uint8)


def save_png(path, rgba, kind=None):
    save_with_meta(path, rgba, kind)


def main(argv):
    preview = None
    if "--preview" in argv:
        i = argv.index("--preview")
        preview = argv[i + 1]
        argv = argv[:i] + argv[i + 2:]
    ids = argv or list(CFG)
    for cid in ids:
        cfg = CFG[cid]
        sheet = load(cfg["sheet"])
        body = front_cutout(cfg, sheet)
        port = portrait_cutout(cfg, sheet)
        weap = weapon_cutout(cfg, sheet)
        os.makedirs(os.path.join(RES, "Characters"), exist_ok=True)
        write_folder_meta(os.path.join(RES, "Characters"))
        save_png(os.path.join(RES, "Characters", cid + ".png"), body, "character")
        save_png(os.path.join(RES, "Characters", cid + "_Portrait.png"), port, "character")
        save_png(os.path.join(RES, "Weapons", "Hero_" + cid + ".png"), weap)     # Weapons: WeaponTexturePostprocessor
        print("cut", cid, "weapon", weap.shape[1], "x", weap.shape[0])
        if preview:
            os.makedirs(preview, exist_ok=True)
            hh = 1024
            wp = cv2.resize(weap, (max(1, int(weap.shape[1] * min(1.0, hh / weap.shape[0]))), min(hh, weap.shape[0])), interpolation=cv2.INTER_AREA)
            sheet_img = np.full((hh, 1024 + 512 + wp.shape[1] + 20, 3), (40, 32, 30), np.uint8)
            sheet_img[:, :1024] = on_dark(body)
            sheet_img[:512, 1024:1536] = on_dark(port)
            sheet_img[:wp.shape[0], 1546:1546 + wp.shape[1]] = on_dark(wp)
            cv2.imwrite(os.path.join(preview, "cut_" + cid + ".png"), sheet_img)


if __name__ == "__main__":
    main(sys.argv[1:])
