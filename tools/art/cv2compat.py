"""
Minimal OpenCV stand-in for the art tools (sheet_anims.py, unity_meta.py) built on numpy, scipy and Pillow.

    try:
        import cv2
    except ImportError:
        import cv2compat as cv2

Only the calls those tools make are implemented, with OpenCV's argument order and conventions (BGR channel order,
(width, height) sizes, uint8 or float32 images). Results match OpenCV closely but not bit for bit: Lanczos and cubic
resampling come from Pillow, distance transforms are exact Euclidean (OpenCV's 3x3 mask is an approximation),
and HSV / Lab use OpenCV's 8-bit scaling.
"""
import io

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

__version__ = "compat"

# ------------------------------------------------------------------------------------------------ constants

COLOR_BGR2HSV, COLOR_BGR2LAB, COLOR_BGR2RGB, COLOR_RGB2BGR, COLOR_BGR2GRAY, COLOR_BGRA2RGBA = range(6)
INTER_NEAREST, INTER_LINEAR, INTER_CUBIC, INTER_AREA, INTER_LANCZOS4 = 0, 1, 2, 3, 4
MORPH_ERODE, MORPH_DILATE, MORPH_OPEN, MORPH_CLOSE = 0, 1, 2, 3
MORPH_RECT, MORPH_CROSS, MORPH_ELLIPSE = 0, 1, 2
DIST_L2 = 2
BORDER_CONSTANT, BORDER_REPLICATE, BORDER_REFLECT = 0, 1, 2
RETR_EXTERNAL, CHAIN_APPROX_NONE = 0, 1
IMWRITE_PNG_COMPRESSION = 16
IMREAD_COLOR, IMREAD_UNCHANGED = 1, -1

# ------------------------------------------------------------------------------------------------ io


def imread(path, flags=IMREAD_COLOR):
    """File -> BGR (or BGRA with IMREAD_UNCHANGED) uint8 array, None when unreadable."""
    try:
        im = Image.open(path)
        im.load()
    except (OSError, ValueError):
        return None
    if flags == IMREAD_UNCHANGED and im.mode in ("RGBA", "LA", "P"):
        a = np.asarray(im.convert("RGBA"))
        return a[..., [2, 1, 0, 3]].copy()
    a = np.asarray(im.convert("RGB"))
    return a[..., ::-1].copy()


def _to_pil(img):
    img = np.asarray(img)
    if img.dtype != np.uint8:
        img = np.clip(img, 0, 255).astype(np.uint8)
    if img.ndim == 2:
        return Image.fromarray(img, "L")
    if img.shape[2] == 4:
        return Image.fromarray(img[..., [2, 1, 0, 3]].copy(), "RGBA")
    return Image.fromarray(img[..., ::-1].copy(), "RGB")


def imencode(ext, img, params=()):
    """(True, uint8 buffer) like cv2.imencode; PNG only."""
    level = 6
    params = list(params)
    if IMWRITE_PNG_COMPRESSION in params:
        level = int(params[params.index(IMWRITE_PNG_COMPRESSION) + 1])
    buf = io.BytesIO()
    _to_pil(img).save(buf, format="PNG", compress_level=level)
    return True, np.frombuffer(buf.getvalue(), np.uint8)


def imwrite(path, img, params=()):
    ok, data = imencode(".png", img, params)
    with open(path, "wb") as f:
        f.write(data.tobytes())
    return ok

# ------------------------------------------------------------------------------------------------ colour


def _srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def cvtColor(img, code):
    img = np.asarray(img)
    if code in (COLOR_BGR2RGB, COLOR_RGB2BGR):
        return img[..., ::-1].copy()
    if code == COLOR_BGRA2RGBA:
        return img[..., [2, 1, 0, 3]].copy()
    if code == COLOR_BGR2GRAY:
        f = img.astype(np.float32)
        return np.clip(0.114 * f[..., 0] + 0.587 * f[..., 1] + 0.299 * f[..., 2] + 0.5, 0, 255).astype(np.uint8)
    if code == COLOR_BGR2HSV:
        f = img.astype(np.float32) / 255.0
        b, g, r = f[..., 0], f[..., 1], f[..., 2]
        mx, mn = f.max(axis=2), f.min(axis=2)
        d = mx - mn
        s = np.where(mx > 0, d / np.maximum(mx, 1e-12), 0)
        h = np.zeros_like(mx)
        nz = d > 1e-12
        rm = nz & (mx == r)
        gm = nz & (mx == g) & ~rm
        bm = nz & ~rm & ~gm
        h[rm] = (60 * (g - b)[rm] / d[rm]) % 360
        h[gm] = 60 * (b - r)[gm] / d[gm] + 120
        h[bm] = 60 * (r - g)[bm] / d[bm] + 240
        return np.dstack([np.round(h / 2) % 180, np.round(s * 255), np.round(mx * 255)]).astype(np.uint8)
    if code == COLOR_BGR2LAB:
        f = _srgb_to_linear(img.astype(np.float32)[..., ::-1] / 255.0)
        m = np.array([[0.412453, 0.357580, 0.180423], [0.212671, 0.715160, 0.072169],
                      [0.019334, 0.119193, 0.950227]], np.float32)
        xyz = f @ m.T
        xyz /= np.array([0.950456, 1.0, 1.088754], np.float32)
        t = np.where(xyz > 0.008856, np.cbrt(xyz), 7.787 * xyz + 16.0 / 116.0)
        L = np.where(xyz[..., 1] > 0.008856, 116 * t[..., 1] - 16, 903.3 * xyz[..., 1])
        a = 500 * (t[..., 0] - t[..., 1])
        bb = 200 * (t[..., 1] - t[..., 2])
        return np.clip(np.dstack([L * 255 / 100, a + 128, bb + 128]) + 0.5, 0, 255).astype(np.uint8)
    raise NotImplementedError("cvtColor code %r" % code)

# ------------------------------------------------------------------------------------------------ morphology


def getStructuringElement(shape, ksize):
    w, h = ksize
    if shape == MORPH_RECT:
        return np.ones((h, w), np.uint8)
    if shape == MORPH_CROSS:
        k = np.zeros((h, w), np.uint8)
        k[h // 2, :] = 1
        k[:, w // 2] = 1
        return k
    # OpenCV's ellipse inscribed in the w x h box
    k = np.zeros((h, w), np.uint8)
    r, c = h // 2, w // 2
    inv_r2 = 1.0 / (r * r) if r else 0.0
    for y in range(h):
        dy = y - r
        if c:
            dx = int(round(c * np.sqrt(max(0.0, 1.0 - dy * dy * inv_r2)))) if r else c
            k[y, max(0, c - dx):min(w, c + dx + 1)] = 1
        else:
            k[y, 0] = 1
    return k


def _kernel(kernel):
    return np.asarray(kernel if kernel is not None else np.ones((3, 3), np.uint8)) > 0


def dilate(src, kernel, iterations=1):
    src = np.asarray(src)
    out = ndi.grey_dilation(src, footprint=_kernel(kernel)) if src.dtype != bool else \
        ndi.binary_dilation(src, _kernel(kernel))
    for _ in range(iterations - 1):
        out = ndi.grey_dilation(out, footprint=_kernel(kernel))
    return out.astype(src.dtype)


def erode(src, kernel, iterations=1):
    src = np.asarray(src)
    # OpenCV pads with +inf for erosion: the border never erodes the image
    out = ndi.grey_erosion(src, footprint=_kernel(kernel), mode="nearest")
    for _ in range(iterations - 1):
        out = ndi.grey_erosion(out, footprint=_kernel(kernel), mode="nearest")
    return out.astype(src.dtype)


def morphologyEx(src, op, kernel):
    if op == MORPH_OPEN:
        return dilate(erode(src, kernel), kernel)
    if op == MORPH_CLOSE:
        return erode(dilate(src, kernel), kernel)
    if op == MORPH_ERODE:
        return erode(src, kernel)
    if op == MORPH_DILATE:
        return dilate(src, kernel)
    raise NotImplementedError("morphologyEx op %r" % op)

# ------------------------------------------------------------------------------------------------ labelling


def _structure(connectivity):
    return ndi.generate_binary_structure(2, 1 if connectivity == 4 else 2)


def connectedComponents(image, connectivity=8):
    lab, n = ndi.label(np.asarray(image) > 0, structure=_structure(connectivity))
    return n + 1, lab.astype(np.int32)


def connectedComponentsWithStats(image, connectivity=8):
    """(n labels incl. background, labels, stats [x, y, w, h, area] per label, centroids [x, y])."""
    n, lab = connectedComponents(image, connectivity)
    h, w = lab.shape
    stats = np.zeros((n, 5), np.int32)
    cents = np.zeros((n, 2), np.float64)
    stats[0] = (0, 0, w, h, int((lab == 0).sum()))
    if n > 1:
        objs = ndi.find_objects(lab)
        idx = np.arange(1, n)
        areas = ndi.sum(np.ones_like(lab), lab, idx)
        cy = ndi.sum(np.indices(lab.shape)[0], lab, idx) / np.maximum(areas, 1)
        cx = ndi.sum(np.indices(lab.shape)[1], lab, idx) / np.maximum(areas, 1)
        for i, sl in enumerate(objs, start=1):
            ys, xs = sl
            stats[i] = (xs.start, ys.start, xs.stop - xs.start, ys.stop - ys.start, int(areas[i - 1]))
            cents[i] = (cx[i - 1], cy[i - 1])
    return n, lab, stats, cents


def floodFill(image, mask, seedPoint, newVal):
    """In-place 4-connected fill of the region of equal value around seedPoint (x, y); mask is ignored."""
    x, y = seedPoint
    region = image == image[y, x]
    lab, _ = ndi.label(region, structure=_structure(4))
    image[lab == lab[y, x]] = newVal
    return 0, image, mask, None


def distanceTransform(src, distanceType=DIST_L2, maskSize=3):
    """Euclidean distance of each non-zero pixel to the nearest zero pixel (float32)."""
    return ndi.distance_transform_edt(np.asarray(src) > 0).astype(np.float32)

# ------------------------------------------------------------------------------------------------ filtering


def _sigma_for(k):
    return 0.3 * ((k - 1) * 0.5 - 1) + 0.8


def GaussianBlur(src, ksize, sigmaX, sigmaY=0, borderType=None):
    src = np.asarray(src)
    k = ksize[0]
    s = sigmaX if sigmaX > 0 else _sigma_for(k)
    trunc = ((k - 1) / 2.0) / s if k > 0 else 4.0
    f = src.astype(np.float32)
    sig = (s, s, 0) if f.ndim == 3 else (s, s)
    out = ndi.gaussian_filter(f, sig, mode="mirror", truncate=max(trunc, 0.5))
    if src.dtype == np.uint8:
        return np.clip(out + 0.5, 0, 255).astype(np.uint8)
    return out.astype(src.dtype)


def filter2D(src, ddepth, kernel, anchor=None, borderType=BORDER_CONSTANT):
    """Correlation with `kernel` anchored at its centre (the only anchor the tools use)."""
    f = np.asarray(src, np.float32)
    mode = "constant" if borderType == BORDER_CONSTANT else "nearest"
    ker = np.asarray(kernel, np.float32)
    if f.ndim == 3:
        return np.dstack([ndi.correlate(f[..., i], ker, mode=mode, cval=0.0) for i in range(f.shape[2])])
    return ndi.correlate(f, ker, mode=mode, cval=0.0)


def line(img, pt1, pt2, color, thickness=1):
    """1 px line (DDA) into a 2-D array, in place."""
    (x0, y0), (x1, y1) = pt1, pt2
    n = int(max(abs(x1 - x0), abs(y1 - y0))) + 1
    xs = np.round(np.linspace(x0, x1, n)).astype(int)
    ys = np.round(np.linspace(y0, y1, n)).astype(int)
    ok = (xs >= 0) & (ys >= 0) & (xs < img.shape[1]) & (ys < img.shape[0])
    img[ys[ok], xs[ok]] = color
    return img


def copyMakeBorder(src, top, bottom, left, right, borderType, value=0):
    pad = [(top, bottom), (left, right)] + [(0, 0)] * (np.asarray(src).ndim - 2)
    if borderType == BORDER_REFLECT:
        return np.pad(src, pad, mode="symmetric")
    if borderType == BORDER_REPLICATE:
        return np.pad(src, pad, mode="edge")
    return np.pad(src, pad, mode="constant", constant_values=value)

# ------------------------------------------------------------------------------------------------ geometry


_PIL_FILTER = {INTER_NEAREST: Image.NEAREST, INTER_LINEAR: Image.BILINEAR, INTER_CUBIC: Image.BICUBIC,
               INTER_AREA: Image.BOX, INTER_LANCZOS4: Image.LANCZOS}


def resize(src, dsize, fx=None, fy=None, interpolation=INTER_LINEAR):
    """cv2.resize: dsize = (width, height), or None with fx / fy. uint8 and float32, any channel count."""
    src = np.asarray(src)
    h, w = src.shape[:2]
    if dsize is None or dsize == (0, 0):
        dsize = (int(round(w * fx)), int(round(h * fy)))
    nw, nh = int(dsize[0]), int(dsize[1])
    filt = _PIL_FILTER.get(interpolation, Image.BILINEAR)
    if filt == Image.BOX and (nw > w or nh > h):
        filt = Image.BILINEAR                           # OpenCV's INTER_AREA enlarges bilinearly

    def one(ch):
        if ch.dtype == np.uint8:
            return np.asarray(Image.fromarray(ch, "L").resize((nw, nh), filt))
        return np.asarray(Image.fromarray(ch.astype(np.float32), "F").resize((nw, nh), filt))

    if src.ndim == 2:
        return one(src).astype(src.dtype)
    return np.dstack([one(src[..., i]) for i in range(src.shape[2])]).astype(src.dtype)


def warpAffine(src, M, dsize, flags=INTER_LINEAR, borderMode=BORDER_CONSTANT, borderValue=0):
    """dst(x, y) = src(M^-1 (x, y)); M maps src -> dst, (x, y) order like OpenCV."""
    src = np.asarray(src, np.float32)
    M = np.asarray(M, np.float64)
    A = np.vstack([M, [0, 0, 1]])
    inv = np.linalg.inv(A)
    # scipy works in (row, col) = (y, x): swap axes of the inverse map
    mat = np.array([[inv[1, 1], inv[1, 0]], [inv[0, 1], inv[0, 0]]])
    off = np.array([inv[1, 2], inv[0, 2]])
    order = 3 if flags == INTER_CUBIC else (0 if flags == INTER_NEAREST else 1)
    w, h = dsize
    bv = borderValue[0] if isinstance(borderValue, (tuple, list)) else borderValue

    def one(ch):
        return ndi.affine_transform(ch, mat, offset=off, output_shape=(h, w), order=order, mode="constant",
                                    cval=float(bv), prefilter=order > 1)

    if src.ndim == 2:
        return one(src)
    return np.dstack([one(src[..., i]) for i in range(src.shape[2])])

# ------------------------------------------------------------------------------------------------ debug drawing


def findContours(image, mode=RETR_EXTERNAL, method=CHAIN_APPROX_NONE):
    """Returns ([boundary mask], None): enough for drawContours below (debug overlays only)."""
    m = np.asarray(image) > 0
    edge = m & ~ndi.binary_erosion(m, structure=_structure(4))
    return [edge], None


def drawContours(img, contours, idx, color, thickness=1):
    for edge in contours:
        img[edge] = color
    return img
