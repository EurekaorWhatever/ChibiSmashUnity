"""
Stage atmosphere particle textures for Chibi Clash (technical-artist, 2026-11-11).

    python tools/art/stages/build_atmos_fx.py

Writes (procedural, deterministic, reproducible):
    Assets/ChibiClash/Resources/Stages/Fx/AtmosAtlas.png   1024x1024, 128 px grid, white shapes tinted at runtime
    Assets/ChibiClash/Resources/Stages/Fx/Caustics.png     256x256, seamless (wrap Repeat), animated caustic light
plus their .meta files (Default texture, mipmaps ON, alpha is transparency) the first time only, so GUIDs stay stable.

The cell layout below is mirrored by AtmosFx.Cells in Assets/ChibiClash/Scripts/Game/StageAtmosphereFx.cs. Keep the two in
sync (name, col, row, w, h in 128 px cells, origin top-left). Every shape is centred in its cell with a transparent margin
so mip levels never bleed a neighbour's content.
"""
import os
import uuid

import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
OUT = os.path.join(ROOT, "Assets", "ChibiClash", "Resources", "Stages", "Fx")
CELL = 128
SS = 2  # supersampling

# name, col, row, w, h  (cells)   -- mirrored in StageAtmosphereFx.cs
CELLS = [
    ("Dot", 0, 0, 1, 1), ("Ember", 1, 0, 1, 1), ("Glint", 2, 0, 1, 1), ("Bubble", 3, 0, 1, 1),
    ("Petal", 4, 0, 1, 1), ("Feather", 5, 0, 1, 1), ("Blade", 6, 0, 1, 1), ("Ring", 7, 0, 1, 1),
    ("Fish", 0, 1, 1, 1), ("Jelly", 1, 1, 1, 1), ("Bird", 2, 1, 1, 1), ("Droplet", 3, 1, 1, 1),
    ("LavaDome", 4, 1, 1, 1), ("Puff", 5, 1, 1, 1),
    ("Streak", 0, 2, 4, 1), ("Shimmer", 4, 2, 4, 1),
    ("Cloud", 0, 3, 4, 2), ("Mist", 4, 3, 4, 2),
    ("Ray", 0, 5, 1, 3), ("Glow", 1, 5, 2, 2),
]

rng = np.random.default_rng(20261111)


def grid(w, h):
    """Normalised coords: u in [-w/h*0.5 .. ], v in [-0.5, 0.5] (y up), supersampled."""
    W, H = w * SS, h * SS
    ys, xs = np.mgrid[0:H, 0:W].astype(np.float64)
    x = (xs + 0.5) / H - (W / H) * 0.5
    y = 0.5 - (ys + 0.5) / H
    return x, y


def smooth(e0, e1, v):
    t = np.clip((v - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def value_noise(x, y, scale, seed, periodic=None):
    """Smooth value noise; periodic=(px, py) wraps the lattice."""
    r = np.random.default_rng(seed)
    n = 64
    lat = r.random((n, n))
    gx, gy = x * scale, y * scale
    x0, y0 = np.floor(gx).astype(int), np.floor(gy).astype(int)
    fx, fy = gx - x0, gy - y0
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)

    def L(i, j):
        if periodic:
            i, j = i % periodic[0], j % periodic[1]
        return lat[j % n, i % n]

    a, b = L(x0, y0), L(x0 + 1, y0)
    c, d = L(x0, y0 + 1), L(x0 + 1, y0 + 1)
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def fbm(x, y, scale, seed, octaves=4):
    s, amp, tot = 0.0, 0.5, 0.0
    for o in range(octaves):
        s = s + amp * value_noise(x, y, scale * (2 ** o), seed + o)
        tot += amp
        amp *= 0.5
    return s / tot


def down(arr):
    H, W = arr.shape[:2]
    return arr.reshape(H // SS, SS, W // SS, SS, *arr.shape[2:]).mean(axis=(1, 3))


# ------------------------------------------------------------------ shapes: return (lum, alpha) in [0,1]

def dot(w, h):
    x, y = grid(w, h)
    r = np.hypot(x, y)
    a = np.exp(-(r / 0.16) ** 2 * 1.0) * (1 - smooth(0.38, 0.48, r))
    return np.ones_like(a), a


def ember(w, h):
    x, y = grid(w, h)
    r = np.hypot(x, y)
    core = 1 - smooth(0.035, 0.07, r)
    halo = np.exp(-(r / 0.15) ** 2) * 0.55 * (1 - smooth(0.36, 0.47, r))
    a = np.clip(core + halo, 0, 1)
    lum = 0.72 + 0.28 * core
    return lum, a


def glint(w, h):
    x, y = grid(w, h)
    r = np.hypot(x, y)
    ax = np.exp(-(y / 0.018) ** 2) * np.exp(-(np.abs(x) / 0.2) ** 1.4)
    ay = np.exp(-(x / 0.018) ** 2) * np.exp(-(np.abs(y) / 0.2) ** 1.4)
    core = np.exp(-(r / 0.06) ** 2)
    halo = np.exp(-(r / 0.16) ** 2) * 0.3
    a = np.clip(ax + ay + core + halo, 0, 1) * (1 - smooth(0.4, 0.48, r))
    return np.ones_like(a), a


def bubble(w, h):
    x, y = grid(w, h)
    r = np.hypot(x, y)
    R = 0.36
    rim = np.exp(-((r - R) / 0.028) ** 2)
    inner = (1 - smooth(R - 0.01, R + 0.01, r)) * 0.14
    hl = np.exp(-(np.hypot(x + 0.13, y - 0.14) / 0.06) ** 2)
    a = np.clip(rim * 0.85 + inner + hl, 0, 1)
    return np.ones_like(a), a


def petal(w, h):
    x, y = grid(w, h)
    # teardrop along x, notch at the wide end
    t = (x + 0.36) / 0.72
    half = 0.17 * np.sin(np.clip(t, 0, 1) * np.pi) ** 0.8 * (0.55 + 0.45 * np.clip(t, 0, 1))
    notch = 0.08 * np.exp(-(y / 0.04) ** 2) * smooth(0.82, 1.0, t)
    inside = (np.abs(y) < half) & (t > 0) & (t < 1 - notch / 0.72)
    d = np.where(inside, half - np.abs(y), -1)
    a = smooth(0.0, 0.012, d) * inside
    lum = 0.62 + 0.38 * smooth(0.0, 0.05, d)       # darker rim = soft ink edge after tint
    lum = lum * (0.9 + 0.1 * smooth(0.2, 0.7, t))
    return lum, a


def feather(w, h):
    x, y = grid(w, h)
    t = (x + 0.4) / 0.8
    half = 0.11 * np.sin(np.clip(t, 0, 1) * np.pi) ** 0.6 * (0.6 + 0.4 * (1 - np.clip(t, 0, 1)))
    yy = y - 0.03 * np.sin(t * 3.0)
    vane = (np.abs(yy) < half) & (t > 0.08) & (t < 1)
    # barbs: notches
    barb = 0.5 + 0.5 * np.sin((x * 60 + np.abs(yy) * 40))
    d = np.where(vane, half - np.abs(yy), -1)
    a = smooth(0.0, 0.01, d) * vane * (0.82 + 0.18 * barb)
    shaft = np.exp(-(yy / 0.008) ** 2) * (t > 0.0) * (t < 1.0)
    a = np.clip(a + shaft * 0.9, 0, 1)
    lum = 0.7 + 0.3 * smooth(0.0, 0.05, d) - shaft * 0.35
    return np.clip(lum, 0, 1), a


def blade(w, h):
    x, y = grid(w, h)
    # crescent blade along a curve y = 0.25 x^2, from x -0.38 to 0.38
    cy = 0.6 * (x ** 2) - 0.05
    t = (x + 0.38) / 0.76
    half = 0.035 * (1 - np.clip(t, 0, 1)) ** 0.7 + 0.004
    d = half - np.abs(y - cy)
    inside = (t > 0) & (t < 1)
    a = smooth(0.0, 0.008, d) * inside
    lum = 0.65 + 0.35 * np.clip(t, 0, 1)
    return lum, a


def ring(w, h):
    x, y = grid(w, h)
    r = np.hypot(x, y)
    a = np.exp(-((r - 0.36) / 0.035) ** 2)
    return np.ones_like(a), a


def fish(w, h):
    x, y = grid(w, h)
    body = ((x - 0.04) / 0.26) ** 2 + (y / 0.11) ** 2
    tx = x + 0.3
    tail = (tx < 0.08) & (tx > -0.1) & (np.abs(y) < 0.02 + (0.08 - tx) * 0.9) & (np.abs(y) > (0.08 - tx) * 0.35 - 0.02)
    inside = (body < 1) | tail
    edge = np.where(body < 1, 1 - body, 0.3)
    a = np.clip(inside * smooth(0, 0.08, edge) + tail * 1.0, 0, 1)
    lum = 0.55 + 0.45 * smooth(0.05, 0.35, edge) * (body < 1) + tail * 0.25
    lum = lum + 0.12 * (y > 0.0)
    eye = np.hypot(x - 0.18, y - 0.02) < 0.022
    lum = np.where(eye, 0.1, lum)
    return np.clip(lum, 0, 1), a


def jelly(w, h):
    x, y = grid(w, h)
    bell = (x / 0.24) ** 2 + ((y - 0.08) / 0.2) ** 2
    bell_in = (bell < 1) & (y > 0.0)
    skirt = np.abs(y - 0.0) < 0.02
    skirt &= np.abs(x) < 0.24
    a = bell_in * (0.55 + 0.4 * smooth(0.5, 1.0, bell)) + skirt * 0.8
    lum = np.where(bell_in, 0.75 + 0.25 * smooth(0.6, 1.0, bell), 1.0)
    for i, ox in enumerate([-0.15, -0.05, 0.05, 0.15]):
        cx = ox + 0.025 * np.sin(-y * 26 + i * 1.7)
        tent = np.exp(-((x - cx) / 0.012) ** 2) * (y < 0.0) * (y > -0.4) * (1 - smooth(-0.4, 0.0, -y - 0.0) * 0)
        tent *= np.clip((y + 0.4) / 0.4, 0, 1)
        a = np.maximum(a, tent * 0.75)
    a = np.clip(a, 0, 1)
    return lum, a


def bird(w, h):
    x, y = grid(w, h)
    # gull "M": two arcs
    ax = np.abs(x)
    cy = 0.11 * np.sin(np.clip(ax / 0.36, 0, 1) * np.pi) - 0.02 * (ax / 0.36)
    th = 0.028 * (1 - np.clip(ax / 0.38, 0, 1)) ** 0.6 + 0.004
    d = th - np.abs(y - cy)
    a = smooth(0.0, 0.01, d) * (ax < 0.38)
    body = np.exp(-((x / 0.05) ** 2 + ((y + 0.0) / 0.03) ** 2))
    a = np.clip(a + body, 0, 1)
    return np.ones_like(a), a


def droplet(w, h):
    x, y = grid(w, h)
    a = np.exp(-(x / 0.03) ** 2) * np.exp(-((y + 0.05) / 0.25) ** 2) * (1 - smooth(0.4, 0.48, np.abs(y)))
    head = np.exp(-(np.hypot(x, y + 0.2) / 0.04) ** 2)
    a = np.clip(a + head, 0, 1)
    return np.ones_like(a), a


def lavadome(w, h):
    x, y = grid(w, h)
    r = np.hypot(x, y)
    R = 0.34
    inside = 1 - smooth(R - 0.015, R + 0.005, r)
    rim = np.exp(-((r - R + 0.03) / 0.04) ** 2)
    hl = np.exp(-(np.hypot(x + 0.1, y - 0.15) / 0.07) ** 2)
    a = np.clip(inside, 0, 1)
    lum = np.clip(0.55 + 0.35 * rim + 0.45 * hl, 0, 1)
    return lum, a


def puff(w, h):
    x, y = grid(w, h)
    r = np.hypot(x, y)
    n = fbm(x + 3, y + 3, 6, 11)
    a = np.exp(-(r / 0.22) ** 2) * (0.55 + 0.6 * n) * (1 - smooth(0.4, 0.48, r))
    return np.ones_like(a), np.clip(a, 0, 1)


def streak(w, h):
    x, y = grid(w, h)
    X = w / h * 0.5
    t = (x + X * 0.92) / (X * 1.84)
    prof = np.clip(t, 0, 1) ** 1.6 * (1 - smooth(0.92, 1.0, t))
    a = np.exp(-(y / 0.035) ** 2) * prof
    return np.ones_like(a), np.clip(a, 0, 1)


def shimmer(w, h):
    x, y = grid(w, h)
    X = w / h * 0.5
    env = (1 - smooth(X * 0.55, X * 0.95, np.abs(x))) * (1 - smooth(0.25, 0.45, np.abs(y)))
    a = np.zeros_like(x)
    for i, (oy, f, ph) in enumerate([(-0.18, 9.0, 0.0), (0.0, 7.0, 1.3), (0.17, 11.0, 2.1)]):
        cy = oy + 0.05 * np.sin(x * f + ph)
        a = np.maximum(a, np.exp(-((y - cy) / 0.03) ** 2) * (0.6 + 0.4 * np.sin(x * f * 0.5 + ph)))
    return np.ones_like(a), np.clip(a * env, 0, 1)


def cloud(w, h):
    x, y = grid(w, h)
    X = w / h * 0.5
    blobs = [(-0.55, -0.08, 0.25), (-0.2, 0.05, 0.32), (0.2, 0.02, 0.3), (0.55, -0.1, 0.22), (0.0, -0.14, 0.28),
             (-0.75, -0.18, 0.15), (0.78, -0.2, 0.14)]
    f = np.full_like(x, -1.0)
    for bx, by, br in blobs:
        f = np.maximum(f, 1 - np.hypot(x - bx, y - by) / br)
    f = np.where(y < -0.28, f - (-0.28 - y) * 6, f)          # flat-ish base
    n = fbm(x + 7, y + 2, 5, 23)
    a = smooth(-0.05, 0.35, f + (n - 0.5) * 0.25) * (1 - smooth(X * 0.92, X, np.abs(x)))
    lum = 0.82 + 0.18 * smooth(-0.3, 0.3, y)
    return lum, np.clip(a, 0, 1)


def mist(w, h):
    x, y = grid(w, h)
    X = w / h * 0.5
    n = fbm(x + 1.7, y * 1.6 + 5, 3.5, 31, 5)
    env_x = 1 - smooth(X * 0.45, X * 0.98, np.abs(x))
    env_y = np.exp(-(y / 0.2) ** 2)
    a = env_x * env_y * (0.45 + 0.75 * n)
    return np.ones_like(a), np.clip(a, 0, 1)


def ray(w, h):
    x, y = grid(w, h)            # x in [-1/6, 1/6], y in [-0.5, 0.5]; top = y 0.5
    t = (0.5 - y)                # 0 at top, 1 at bottom
    halfw = 0.06 + 0.08 * t
    edge = 1 - smooth(halfw * 0.35, halfw, np.abs(x))
    along = smooth(0.0, 0.08, t) * (1 - smooth(0.25, 0.98, t)) ** 1.2
    n = 0.85 + 0.15 * np.sin(x * 90 + t * 3)
    a = edge * along * n
    return np.ones_like(a), np.clip(a, 0, 1)


def glow(w, h):
    x, y = grid(w, h)
    r = np.hypot(x, y)
    a = np.exp(-(r / 0.2) ** 2 * 1.1) * (1 - smooth(0.4, 0.49, r))
    return np.ones_like(a), a


SHAPES = {
    "Dot": dot, "Ember": ember, "Glint": glint, "Bubble": bubble, "Petal": petal, "Feather": feather, "Blade": blade,
    "Ring": ring, "Fish": fish, "Jelly": jelly, "Bird": bird, "Droplet": droplet, "LavaDome": lavadome, "Puff": puff,
    "Streak": streak, "Shimmer": shimmer, "Cloud": cloud, "Mist": mist, "Ray": ray, "Glow": glow,
}


def build_atlas():
    size = 8 * CELL
    rgba = np.zeros((size, size, 4), np.float64)
    rgba[..., :3] = 1.0
    for name, c, r, w, h in CELLS:
        lum, a = SHAPES[name](w * CELL, h * CELL)
        lum, a = down(np.broadcast_to(lum, a.shape).copy()), down(a)
        px, py, pw, ph = c * CELL, r * CELL, w * CELL, h * CELL
        # 4 px hard transparent margin on every cell edge (mip safety)
        m = np.ones_like(a)
        m[:4, :] = m[-4:, :] = 0
        m[:, :4] = m[:, -4:] = 0
        rgba[py:py + ph, px:px + pw, 0] = lum
        rgba[py:py + ph, px:px + pw, 1] = lum
        rgba[py:py + ph, px:px + pw, 2] = lum
        rgba[py:py + ph, px:px + pw, 3] = a * m
    return Image.fromarray((np.clip(rgba, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")


def build_caustics(n=256, cells=5, seed=7):
    """Seamless caustic net: bright ridges where Worley F2 - F1 is small (points on a torus)."""
    r = np.random.default_rng(seed)
    pts = 0.2 + 0.6 * r.random((cells * cells, 2))   # jitter inside the cell: no coincident points, no flat blobs
    base = (np.stack(np.meshgrid(np.arange(cells), np.arange(cells)), -1).reshape(-1, 2) + pts) / cells
    W = n * SS
    ys, xs = np.mgrid[0:W, 0:W].astype(np.float64)
    u, v = (xs + 0.5) / W, (ys + 0.5) / W
    # warp for organic lines (periodic)
    u2 = u + 0.03 * np.sin(2 * np.pi * (v * 2 + 0.3))
    v2 = v + 0.03 * np.sin(2 * np.pi * (u * 2 + 0.7))
    d = []
    for p in base:
        dx = np.abs(u2 - p[0]); dx = np.minimum(dx, 1 - dx)
        dy = np.abs(v2 - p[1]); dy = np.minimum(dy, 1 - dy)
        d.append(np.hypot(dx, dy))
    d = np.sort(np.stack(d), axis=0)
    e = (d[1] - d[0]) * cells
    a = np.exp(-(e / 0.09) ** 2) * 0.9 + np.exp(-(e / 0.3) ** 2) * 0.18
    a = down(a)
    rgba = np.ones((n, n, 4))
    rgba[..., 3] = np.clip(a, 0, 1)
    return Image.fromarray((rgba * 255 + 0.5).astype(np.uint8), "RGBA")


META = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 1
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: {wrap}
    wrapV: {wrap}
    wrapW: {wrap}
  nPOTScale: 1
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 0
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 0
  alphaUsage: 1
  alphaIsTransparency: 1
  spriteTessellationMethod: 0
  spriteTessellationDetail: -1
  spriteGeometrySubdivision: -1
  textureType: 0
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData:
    physicsShape: []
    bones: []
    spriteID:
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName:
  pSDRemoveMatte: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""

FOLDER_META = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def write_meta(path, text):
    if os.path.exists(path):
        return  # keep the GUID stable once Unity has seen it
    with open(path, "w", newline="\n") as f:
        f.write(text)


def main():
    os.makedirs(OUT, exist_ok=True)
    write_meta(OUT + ".meta", FOLDER_META.format(guid=uuid.uuid4().hex))
    atlas = os.path.join(OUT, "AtmosAtlas.png")
    build_atlas().save(atlas, optimize=True)
    write_meta(atlas + ".meta", META.format(guid=uuid.uuid4().hex, wrap=1))       # clamp
    caus = os.path.join(OUT, "Caustics.png")
    build_caustics().save(caus, optimize=True)
    write_meta(caus + ".meta", META.format(guid=uuid.uuid4().hex, wrap=0))        # repeat (seamless tiling)
    print("wrote", atlas, "and", caus)


if __name__ == "__main__":
    main()
