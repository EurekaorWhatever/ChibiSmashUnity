"""
Chibi Clash procedural SFX generator.

Reads design/audio/sfx-spec.md (the tables of sections 4 and 5), renders every sound id and baked variant with the
recipe notation of spec section 1.1, and writes

    Assets/ChibiClash/Resources/Audio/Sfx/<id>_<nn>.wav     44 100 Hz, mono, 16-bit PCM (fmt + data chunks only)
    Assets/ChibiClash/Resources/Audio/Sfx/<id>_loop.wav     the two loop exports (lyra_vmm, taro_whrr)
    Assets/ChibiClash/Resources/Audio/Sfx/*.wav.meta        AudioImporter settings (written BEFORE the wav, stable GUID)
    Assets/ChibiClash/Resources/Audio/sfx_meta.txt          runtime mix table (bus, priority, max voices, cooldown...)

    python tools/audio/gen_sfx.py              render everything, validate, print the summary table
    python tools/audio/gen_sfx.py --only kaia  render only ids containing "kaia" (no stale-file cleanup)
    python tools/audio/gen_sfx.py --check      render in memory and validate, write nothing

Dependencies: numpy + the standard library (wave, zlib). No scipy.
Determinism: every file is rendered from numpy's PCG64 seeded with crc32(id) + nn, in a fixed order, with no
threading. Re-running produces byte-identical files; unchanged files are not rewritten (no needless Unity reimport).

Implementation notes (where the notation needed an interpretation; see tools/audio/README.md):
  * Each layer's source+filter chain is peak-normalised to 1 before its envelope, so the layer gain column is the
    layer's peak level relative to the other layers.
  * saw / sq / tri are additive band-limited (no partial above 18 kHz, stricter than polyBLEP); FM is rendered at 4x and
    brick-walled at 18 kHz before decimation.
  * ADSR release ends at the end of the sound: release starts at max(A + D, layer length - R).
  * A layer with no envelope and no gain (`saw 330 + saw 495 + saw 660 -> lp ... {..} 0 (each -4)`) joins the next
    layer as an extra source of the same chain; `(each G)` is the per-source gain.
  * `+ rev T m` as its own layer is a reverb on the sum of the other layers.
  * Variant time factor stretches durations, envelope times, glides, sweeps and layer offsets.
  * Trailing-silence trim (-70 dBFS) runs before the 6 ms fade-out so the file always ends on a fade, and the peak is
    normalised once more after the fades so every file meets its Peak column exactly.
"""
import argparse
import math
import os
import re
import struct
import sys
import wave
import zlib
import hashlib

import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SPEC = os.path.join(ROOT, "design", "audio", "sfx-spec.md")
OUT_DIR = os.path.join(ROOT, "Assets", "ChibiClash", "Resources", "Audio", "Sfx")
META_TABLE = os.path.join(ROOT, "Assets", "ChibiClash", "Resources", "Audio", "sfx_meta.txt")

SR = 44100
MAX_PARTIAL = 18000.0
BLOCK = 32                      # biquad coefficient update interval (spec 1)
SOFT_DRIVE = 1.2
FADE_IN_MS, FADE_OUT_MS = 2.0, 6.0
TRIM_DBFS = -70.0

# Loop exports (spec 5.4 lyra_vmm, 5.5 taro_whrr): sustain-only, seamless.
LOOPS = {"lyra_vmm": 1000, "taro_whrr": 500}

# ----------------------------------------------------------------------------------------------------------------------
# Spec parsing
# ----------------------------------------------------------------------------------------------------------------------

SECTION_BUS = {"4.1": "UI", "4.2": "STG", "4.3": "CMB", "4.4": "MOV", "4.5": "PRJ", "4.6": "STG"}
# Use-cell "bus X" mentions that describe a secondary use (the main id stays on its default bus).
BUS_NOTE_SECONDARY = {"kaia_ult", "bruna_smash"}


class SoundDef:
    def __init__(self, **kw):
        self.__dict__.update(kw)


def num(tok):
    """'2.5k' -> 2500.0, '-4' -> -4.0."""
    tok = tok.strip()
    mult = 1.0
    if tok.endswith("k"):
        mult, tok = 1000.0, tok[:-1]
    return float(tok) * mult


def parse_spec(path=SPEC):
    sounds = []
    section = None
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            m = re.match(r"^###\s+(\d+\.\d+)", line)
            if m:
                section = m.group(1)
                continue
            if not line.startswith("| ") or section is None:
                continue
            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cells) != 10 or not re.match(r"^[a-z]+_[a-z_]+$", cells[0]):
                continue
            sid, use, dur, recipe_cell, var, peak, pri, mx, cd, pitch = cells
            rm = re.search(r"`([^`]+)`", recipe_cell)
            if not rm:
                raise ValueError("no recipe for " + sid)
            vm = re.match(r"V(\d+)(?::\s*p±([\d.]+)%)?(?:\s*t±([\d.]+)%)?", var)
            if not vm:
                raise ValueError("bad variant cell for %s: %r" % (sid, var))
            if section in SECTION_BUS:
                bus = SECTION_BUS[section]
            else:
                bm = re.search(r"\bbus (UI|CMB|MOV|PRJ|STG)\b", use)
                bus = bm.group(1) if bm and sid not in BUS_NOTE_SECONDARY else "CMB"
            sounds.append(SoundDef(
                id=sid, section=section, use=use, dur=float(dur), recipe=rm.group(1),
                click_onset=recipe_cell.lstrip().startswith("click-onset"),
                variants=int(vm.group(1)), pitch_var=float(vm.group(2) or 0) / 100.0,
                time_var=float(vm.group(3) or 0) / 100.0,
                peak=float(peak), bus=bus, pri=int(pri), max=int(mx), cd=0 if cd.strip() == "-" else int(cd),
                pitch=float(pitch)))
    return sounds


class Src:
    def __init__(self, kind, args):
        self.kind, self.args = kind, args


class Layer:
    def __init__(self):
        self.sources = []          # list of Src
        self.each_db = 0.0
        self.chain = []            # list of (name, params)
        self.env = None            # ("p", A, R) or ("adsr", A, D, S, R)
        self.gain_db = 0.0
        self.offset = 0.0


def parse_points(tok):
    """'800>300/60' -> ([800, 300], 60); '6k' -> ([6000], 0)."""
    t = 0.0
    if "/" in tok:
        tok, ts = tok.split("/")
        t = float(ts)
    return [num(p) for p in tok.split(">")], t


def parse_recipe(text):
    """Returns (layers, global_reverb or None)."""
    layers, pending, reverb = [], [], None
    for raw in text.split(" + "):
        s = raw.strip()
        each = None
        for c in re.findall(r"\(([^)]*)\)", s):
            em = re.match(r"each\s+(-?[\d.]+)", c.strip())
            if em:
                each = float(em.group(1))
        s = re.sub(r"\([^)]*\)", "", s).strip()
        if s.startswith("rev "):
            p = s.split()
            reverb = (float(p[1]), float(p[2]))
            continue
        env = None
        em = re.search(r"\{([^}]*)\}", s)
        tail = ""
        if em:
            e = em.group(1).strip()
            if e.startswith("p "):
                a, r = e[2:].split("/")
                env = ("p", float(a), float(r))
            else:
                a, d, sl, r = e.split("/")
                env = ("adsr", float(a), float(d), float(sl), float(r))
            tail = s[em.end():].strip()
            s = s[:em.start()].strip()
        parts = [p.strip() for p in s.split("->")]
        src_tok = parts[0].split()
        kind = src_tok[0]
        srcs = []
        if kind in ("wn", "pn", "bn"):
            srcs.append(Src(kind, None))
        elif kind == "fm":
            c, r = src_tok[1].split(":")
            itok = src_tok[2][1:]
            ipts, it = parse_points(itok)
            srcs.append(Src("fm", (num(c), float(r), ipts, it)))
        else:
            for f in src_tok[1].split("+"):
                srcs.append(Src(kind, parse_points(f)))
        chain = []
        for p in parts[1:]:
            tk = p.split()
            name = tk[0]
            if name in ("lp", "hp", "bp"):
                pts, t = parse_points(tk[1])
                q = None
                for extra in tk[2:]:
                    if extra.startswith("Q"):
                        qs = extra[1:]
                        if "/" in qs:
                            qs, ts = qs.split("/")
                            t = float(ts)
                        q = float(qs)
                chain.append((name, (pts, t, q if q is not None else (1.0 if name == "bp" else 0.7071))))
            elif name == "am":
                pts, t = parse_points(tk[1])
                chain.append(("am", (pts, t, float(tk[2]))))
            elif name == "dist":
                chain.append(("dist", float(tk[1])))
            elif name == "rev":
                chain.append(("rev", (float(tk[1]), float(tk[2]))))
            else:
                raise ValueError("unknown chain op %r in %r" % (name, raw))
        if env is None and not tail:
            # Bare source: joins the next layer (shared chain / envelope).
            pending.extend(srcs)
            continue
        lay = Layer()
        lay.sources = pending + srcs
        pending = []
        lay.chain, lay.env = chain, env
        tt = tail.split()
        lay.gain_db = float(tt[0]) if tt else 0.0
        for x in tt[1:]:
            if x.startswith("@"):
                lay.offset = float(x[1:])
        if each is not None:
            lay.each_db = each
        layers.append(lay)
    if pending:
        raise ValueError("dangling bare source in " + text)
    return layers, reverb


# ----------------------------------------------------------------------------------------------------------------------
# DSP
# ----------------------------------------------------------------------------------------------------------------------

def db(x):
    return 10.0 ** (x / 20.0)


def trajectory(pts, t_ms, n, sr, pf=1.0, tf=1.0):
    """Per-sample value of an exponential multi-point glide (points equally spaced over t_ms), scaled by pf."""
    vals = [p * pf for p in pts]
    if len(vals) == 1 or t_ms <= 0:
        return np.full(n, vals[-1], dtype=np.float64)
    t = np.arange(n, dtype=np.float64) / sr * 1000.0
    total = t_ms * tf
    seg = total / (len(vals) - 1)
    out = np.full(n, vals[-1], dtype=np.float64)
    for i in range(len(vals) - 1):
        a, b = vals[i], vals[i + 1]
        t0 = seg * i
        mask = (t >= t0) & (t < t0 + seg)
        u = (t[mask] - t0) / seg
        out[mask] = a * (b / a) ** u
    return out


def linear_traj(pts, t_ms, n, sr, tf=1.0):
    if len(pts) == 1 or t_ms <= 0:
        return np.full(n, pts[-1], dtype=np.float64)
    t = np.arange(n, dtype=np.float64) / sr * 1000.0
    u = np.clip(t / (t_ms * tf), 0.0, 1.0)
    return pts[0] + (pts[-1] - pts[0]) * u


def phase_of(freq, sr):
    """Integrated phase in cycles, starting at 0."""
    ph = np.cumsum(freq) / sr
    return np.concatenate(([0.0], ph[:-1]))


def norm_peak(x):
    p = float(np.max(np.abs(x))) if x.size else 0.0
    return x / p if p > 1e-12 else x


def shaped_noise(rng, n, kind):
    w = rng.uniform(-1.0, 1.0, n)
    if kind == "wn":
        return w
    spec = np.fft.rfft(w)
    f = np.fft.rfftfreq(n, 1.0 / SR)
    f = np.maximum(f, 20.0)
    spec *= (1.0 / np.sqrt(f)) if kind == "pn" else (1.0 / f)
    spec[0] = 0.0
    return norm_peak(np.fft.irfft(spec, n))


def additive(kind, freq, sr):
    """Band-limited saw / sq / tri from a per-sample frequency track (no partial above MAX_PARTIAL)."""
    ph = 2.0 * np.pi * phase_of(freq, sr)
    fmin = max(float(np.min(freq)), 1.0)
    kmax = int(MAX_PARTIAL / fmin) + 1
    out = np.zeros_like(freq)
    for k in range(1, kmax + 1):
        if kind in ("sq", "tri") and k % 2 == 0:
            continue
        taper = np.clip((MAX_PARTIAL - k * freq) / 1500.0, 0.0, 1.0)
        if not taper.any():
            continue
        if kind == "saw":
            amp = 1.0 / k
        elif kind == "sq":
            amp = 1.0 / k
        else:
            amp = (1.0 / (k * k)) * (1 if ((k - 1) // 2) % 2 == 0 else -1)
        out += amp * taper * np.sin(k * ph)
    return out


def p_env(n, sr, a_ms, r_ms):
    t = np.arange(n, dtype=np.float64) / sr * 1000.0
    env = np.empty(n)
    if a_ms > 0:
        att = t < a_ms
        env[att] = t[att] / a_ms
    else:
        att = np.zeros(n, dtype=bool)
    span = max(r_ms - a_ms, 0.1)
    dec = ~att
    env[dec] = np.exp(math.log(1e-3) * (t[dec] - a_ms) / span)
    return env


def adsr_env(n, sr, a, d, s, r):
    t = np.arange(n, dtype=np.float64) / sr * 1000.0
    length = n / sr * 1000.0
    rel = max(a + d, length - r)
    env = np.full(n, s)
    if a > 0:
        m = t < a
        env[m] = t[m] / a
    if d > 0:
        m = (t >= a) & (t < a + d)
        env[m] = 1.0 - (1.0 - s) * (t[m] - a) / d
    m = t >= rel
    lvl = s if rel >= a + d else 1.0
    env[m] = np.maximum(0.0, lvl * (1.0 - (t[m] - rel) / max(r, 0.1)))
    return env


def make_env(env, n, sr, tf, scale_r=1.0):
    if env is None:
        return np.ones(n)
    if env[0] == "p":
        a, r = env[1] * tf, env[2] * tf
        r = a + (r - a) * scale_r if scale_r != 1.0 else r
        return p_env(n, sr, a, r)
    _, a, d, s, r = env
    return adsr_env(n, sr, a * tf, d * tf, s, r * tf)


def rbj(kind, f, q, sr):
    f = min(max(f, 10.0), sr * 0.45)
    w0 = 2.0 * math.pi * f / sr
    cw, sw = math.cos(w0), math.sin(w0)
    alpha = sw / (2.0 * q)
    if kind == "lp":
        b0, b1, b2 = (1 - cw) / 2, 1 - cw, (1 - cw) / 2
    elif kind == "hp":
        b0, b1, b2 = (1 + cw) / 2, -(1 + cw), (1 + cw) / 2
    else:
        b0, b1, b2 = alpha, 0.0, -alpha
    a0 = 1 + alpha
    return b0 / a0, b1 / a0, b2 / a0, (-2 * cw) / a0, (1 - alpha) / a0


def biquad_blocks(x, coefs):
    """Direct form I, coefficients per BLOCK samples (coefs[i] for block i). Plain Python floats: deterministic."""
    xl = x.tolist()
    n = len(xl)
    out = [0.0] * n
    x1 = x2 = y1 = y2 = 0.0
    for bi in range(0, n, BLOCK):
        b0, b1, b2, a1, a2 = coefs[bi // BLOCK]
        for i in range(bi, min(n, bi + BLOCK)):
            xi = xl[i]
            yi = b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
            x2 = x1
            x1 = xi
            y2 = y1
            y1 = yi
            out[i] = yi
    return np.array(out)


def apply_filter(x, kind, params, pf, tf):
    pts, t_ms, q = params
    n = len(x)
    nb = (n + BLOCK - 1) // BLOCK
    starts = np.arange(nb) * BLOCK
    traj = trajectory(pts, t_ms, n, SR, pf, tf) if n else np.zeros(0)
    coefs = [rbj(kind, float(traj[s]), q, SR) for s in starts]
    return biquad_blocks(x, coefs)


def resonator(x, f, q):
    r = math.exp(-math.pi * (f / q) / SR)
    w = 2.0 * math.pi * min(f, SR * 0.45) / SR
    c = (1.0 - r, 0.0, 0.0, -2.0 * r * math.cos(w), r * r)
    return biquad_blocks(x, [c] * ((len(x) + BLOCK - 1) // BLOCK))


def am(x, params, tf):
    pts, t_ms, depth = params
    rate = trajectory(pts, t_ms, len(x), SR, 1.0, tf)
    ph = 2.0 * np.pi * phase_of(rate, SR)
    return x * (1.0 - depth * 0.5 * (1.0 - np.cos(ph)))


def comb(x, d, g):
    y = x.copy()
    for s in range(d, len(x), d):
        e = min(len(x), s + d)
        y[s:e] += g * y[s - d:e - d]
    return y


def allpass(x, d, g):
    y = np.zeros_like(x)
    n = len(x)
    for s in range(0, n, d):
        e = min(n, s + d)
        y[s:e] = -g * x[s:e]
        if s >= d:
            y[s:e] += x[s - d:e - d] + g * y[s - d:e - d]
    return y


def reverb(x, rt60_ms, mix):
    """Small Schroeder reverb: 4 parallel combs + 2 series allpasses; wet scaled to the dry RMS."""
    if len(x) == 0 or mix <= 0:
        return x
    wet = np.zeros_like(x)
    for dms in (29.7, 37.1, 41.1, 43.7):
        d = int(round(dms * SR / 1000.0))
        g = 10.0 ** (-3.0 * dms / rt60_ms)
        wet += comb(x, d, g)
    for dms in (5.0, 1.7):
        wet = allpass(wet, int(round(dms * SR / 1000.0)), 0.7)
    rd, rw = float(np.sqrt(np.mean(x * x))), float(np.sqrt(np.mean(wet * wet)))
    if rw > 1e-12:
        wet *= rd / rw
    return (1.0 - mix) * x + mix * wet


def dc_block(x):
    """1-pole DC blocker, HP 20 Hz."""
    r = 1.0 - 2.0 * math.pi * 20.0 / SR
    xl = x.tolist()
    out = [0.0] * len(xl)
    xp = yp = 0.0
    for i, xi in enumerate(xl):
        yp = xi - xp + r * yp
        xp = xi
        out[i] = yp
    return np.array(out)


BELL = ((1.0, 0.0, 1.00), (2.76, -6.0, 0.60), (5.40, -12.0, 0.35), (8.93, -18.0, 0.20))
METAL = ((1.0, 0, 1.00), (1.47, -3, 0.80), (2.09, -5, 0.60), (2.56, -8, 0.45), (3.31, -10, 0.35), (4.23, -14, 0.25))


def render_source(src, n, rng, pf, tf, env):
    """Returns (signal, env_applied). Bells / metals carry per-partial decays, so they apply the layer envelope."""
    k = src.kind
    if k in ("wn", "pn", "bn"):
        return shaped_noise(rng, n, k), False
    if k == "fm":
        c, ratio, ipts, it = src.args
        os_ = 4
        m = n * os_
        sr4 = SR * os_
        t = np.arange(m, dtype=np.float64) / sr4
        idx = linear_traj(ipts, it, m, sr4, tf)
        y = np.sin(2 * np.pi * c * pf * t + idx * np.sin(2 * np.pi * c * pf * ratio * t))
        spec = np.fft.rfft(y)
        f = np.fft.rfftfreq(m, 1.0 / sr4)
        spec[f > MAX_PARTIAL] = 0.0
        return np.fft.irfft(spec, m)[::os_].copy(), False
    pts, t_ms = src.args
    if k in ("sin", "tri", "saw", "sq"):
        freq = trajectory(pts, t_ms, n, SR, pf, tf)
        if k == "sin":
            return np.sin(2 * np.pi * phase_of(freq, SR)), False
        return additive(k, freq, SR), False
    f0 = pts[0] * pf
    if k == "click":
        t = np.arange(n, dtype=np.float64) / SR
        burst = np.sin(2 * np.pi * f0 * t) * np.exp(math.log(1e-3) * t / 0.0015)
        return burst, False
    if k == "wood":
        ex = np.zeros(n)
        nb = min(n, max(1, int(round(0.0005 * SR))))
        ex[:nb] = rng.uniform(-1.0, 1.0, nb)
        return resonator(ex, f0, 12.0), False
    if k in ("bell", "metal"):
        table = BELL if k == "bell" else METAL
        t = np.arange(n, dtype=np.float64) / SR
        out = np.zeros(n)
        phases = rng.uniform(0.0, 2 * np.pi, len(table)) if k == "metal" else np.zeros(len(table))
        for (mul, gdb, dec), ph in zip(table, phases):
            f = f0 * mul
            if f > MAX_PARTIAL:
                continue
            e = make_env(env, n, SR, tf, scale_r=dec) if env is not None and env[0] == "p" else make_env(env, n, SR, tf)
            out += db(gdb) * np.sin(2 * np.pi * f * t + ph) * e
        return out, True
    raise ValueError("unknown source " + k)


def render_layer(lay, n, rng, pf, tf):
    if n <= 0:
        return np.zeros(0)
    sig = np.zeros(n)
    env_done = False
    for src in lay.sources:
        s, done = render_source(src, n, rng, pf, tf, lay.env)
        env_done = env_done or done
        sig += db(lay.each_db) * norm_peak(s)
    sig = norm_peak(sig)
    for name, params in lay.chain:
        if name in ("lp", "hp", "bp"):
            sig = apply_filter(sig, name, params, pf, tf)
        elif name == "am":
            sig = am(sig, params, tf)
        elif name == "dist":
            sig = np.tanh(params * norm_peak(sig)) / math.tanh(params)
        elif name == "rev":
            sig = reverb(sig, params[0] * tf, params[1])
    sig = norm_peak(sig)
    if not env_done:
        sig = sig * make_env(lay.env, n, SR, tf)
    return sig * db(lay.gain_db)


def finish(mix, peak_dbfs, click_onset, dc=True):
    """DC block, soft clip, peak normalise, trim, fades. Returns float array scaled to full scale 1.0."""
    if dc:
        mix = dc_block(mix)
    mix = norm_peak(mix)
    mix = np.tanh(SOFT_DRIVE * mix) / math.tanh(SOFT_DRIVE)
    mix = mix - float(np.mean(mix)) if not dc else mix
    mix = norm_peak(mix) * db(peak_dbfs)
    thr = db(TRIM_DBFS)
    loud = np.nonzero(np.abs(mix) >= thr)[0]
    if loud.size:
        mix = mix[:loud[-1] + 1]
    n = len(mix)
    fo = min(n, int(round(FADE_OUT_MS * SR / 1000.0)))
    if fo > 1:
        mix[n - fo:] *= np.linspace(1.0, 0.0, fo)
    if not click_onset:
        fi = min(n, int(round(FADE_IN_MS * SR / 1000.0)))
        if fi > 1:
            mix[:fi] *= np.linspace(0.0, 1.0, fi)
    # Re-normalise: a fade can shave the peak when it sits in the first 2 ms; the Peak column is the contract.
    return norm_peak(mix) * db(peak_dbfs)


def variant_factors(sd, nn):
    rng = np.random.default_rng(zlib.crc32(sd.id.encode("ascii")) + nn)
    u_p, u_t = rng.uniform(-1.0, 1.0, 2)
    if nn == 1:
        return rng, 1.0, 1.0
    return rng, 1.0 + u_p * sd.pitch_var, 1.0 + u_t * sd.time_var


def render(sd, nn):
    layers, rev = parse_recipe(sd.recipe)
    rng, pf, tf = variant_factors(sd, nn)
    n = int(round(sd.dur * tf * SR / 1000.0))
    mix = np.zeros(n)
    for lay in layers:
        off = int(round(lay.offset * tf * SR / 1000.0))
        if off >= n:
            continue
        mix[off:] += render_layer(lay, n - off, rng, pf, tf)
    if rev is not None:
        mix = reverb(mix, rev[0] * tf, rev[1])
    return finish(mix, sd.peak, sd.click_onset), pf, tf


# ---- loops: sustain only, perfectly periodic (circular filtering, integer cycles per loop) --------------------------

def circular_filter(x, kind, f, q):
    b0, b1, b2, a1, a2 = rbj(kind, f, q, SR)
    n = len(x)
    w = 2 * np.pi * np.fft.rfftfreq(n, 1.0 / SR) / SR
    z1, z2 = np.exp(-1j * w), np.exp(-2j * w)
    h = (b0 + b1 * z1 + b2 * z2) / (1 + a1 * z1 + a2 * z2)
    return np.fft.irfft(np.fft.rfft(x) * h, n)


def render_loop(sd, loop_ms):
    """Sustain section only: sweeps held at their end values, every frequency rounded to whole cycles per loop,
    filters applied circularly, envelope = sustain. Then rotated to an upward zero crossing."""
    layers, _ = parse_recipe(sd.recipe)
    rng = np.random.default_rng(zlib.crc32((sd.id + "_loop").encode("ascii")))
    n = int(round(loop_ms * SR / 1000.0))
    secs = n / SR
    whole = lambda f: max(1.0, round(f * secs)) / secs     # noqa: E731
    t = np.arange(n, dtype=np.float64) / SR
    mix = np.zeros(n)
    approximations = []
    for lay in layers:
        sig = np.zeros(n)
        for src in lay.sources:
            if src.kind in ("wn", "pn", "bn"):
                s = shaped_noise(rng, n, src.kind) if src.kind != "wn" else rng.uniform(-1.0, 1.0, n)
            elif src.kind == "sin":
                f = src.args[0][-1]
                fw = whole(f)
                if abs(fw - f) > 1e-9:
                    approximations.append("%s sin %g -> %g Hz" % (sd.id, f, fw))
                s = np.sin(2 * np.pi * fw * t)
            elif src.kind == "fm":
                c, ratio, ipts, _ = src.args
                cw, mw = whole(c), whole(c * ratio)
                s = np.sin(2 * np.pi * cw * t + ipts[-1] * np.sin(2 * np.pi * mw * t))
            else:
                raise ValueError("loop source %s unsupported" % src.kind)
            sig += db(lay.each_db) * norm_peak(s)
        sig = norm_peak(sig)
        for name, params in lay.chain:
            if name in ("lp", "hp", "bp"):
                pts, _, q = params
                sig = circular_filter(sig, name, pts[-1], q)
            elif name == "am":
                pts, _, depth = params
                rw = whole(pts[-1])
                if abs(rw - pts[-1]) > 1e-9:
                    approximations.append("%s am %g -> %g Hz" % (sd.id, pts[-1], rw))
                sig = sig * (1.0 - depth * 0.5 * (1.0 - np.cos(2 * np.pi * rw * t)))
            elif name == "dist":
                sig = np.tanh(params * norm_peak(sig)) / math.tanh(params)
        sustain = lay.env[3] if lay.env is not None and lay.env[0] == "adsr" else 1.0
        mix += norm_peak(sig) * sustain * db(lay.gain_db)
    mix = mix - float(np.mean(mix))
    mix = norm_peak(mix)
    mix = np.tanh(SOFT_DRIVE * mix) / math.tanh(SOFT_DRIVE)
    mix = mix - float(np.mean(mix))
    mix = norm_peak(mix) * db(sd.peak)
    # Rotate to the upward zero crossing with the smallest |sample| (still exactly one period long).
    cross = np.nonzero((np.roll(mix, 1) < 0) & (mix >= 0))[0]
    if cross.size:
        mix = np.roll(mix, -int(cross[np.argmin(np.abs(mix[cross]))]))
    return mix, approximations


# ----------------------------------------------------------------------------------------------------------------------
# Output
# ----------------------------------------------------------------------------------------------------------------------

META_TEMPLATE = """fileFormatVersion: 2
guid: {guid}
AudioImporter:
  externalObjects: {{}}
  serializedVersion: 8
  defaultSettings:
    serializedVersion: 2
    loadType: {load}
    sampleRateSetting: 0
    sampleRateOverride: 44100
    compressionFormat: 1
    quality: {quality}
    conversionMode: 0
    preloadAudioData: 1
  platformSettingOverrides: {{}}
  forceToMono: 1
  normalize: 0
  loadInBackground: 0
  ambisonic: 0
  3D: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def to_pcm(x):
    q = np.clip(np.round(x * 32767.0), -32768, 32767).astype("<i2")
    return q


def wav_bytes(pcm):
    import io
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    return buf.getvalue()


def write_if_changed(path, data):
    if isinstance(data, str):
        data = data.encode("utf-8")
    if os.path.exists(path):
        with open(path, "rb") as fh:
            if fh.read() == data:
                return False
    with open(path, "wb") as fh:
        fh.write(data)
    return True


def meta_for(path, seconds):
    rel = os.path.relpath(path, ROOT).replace("\\", "/")
    guid = hashlib.md5(("chibi-sfx:" + rel).encode("utf-8")).hexdigest()
    if os.path.exists(path + ".meta"):
        m = re.search(r"^guid: ([0-9a-f]{32})", open(path + ".meta", encoding="utf-8").read(), re.M)
        if m:
            guid = m.group(1)
    stinger = seconds >= 1.0
    # Spec 6: Decompress On Load under 1 s; Compressed In Memory (Vorbis q 0.7) for stingers.
    return META_TEMPLATE.format(guid=guid, load=1 if stinger else 0, quality="0.7" if stinger else "1")


def stats(pcm, target_db):
    x = pcm.astype(np.float64) / 32768.0
    peak = float(np.max(np.abs(x))) if x.size else 0.0
    rms = float(np.sqrt(np.mean(x * x))) if x.size else 0.0
    return {
        "ms": len(pcm) * 1000.0 / SR,
        "peak_db": 20 * math.log10(peak) if peak > 0 else -999.0,
        "clip": int(np.sum(np.abs(pcm.astype(np.int32)) >= 32767)),
        "dc": abs(float(np.mean(x))),
        "rms_db": 20 * math.log10(rms) if rms > 0 else -999.0,
        "target": target_db,
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only", help="render only ids containing this text (skips stale-file cleanup)")
    ap.add_argument("--check", action="store_true", help="render and validate in memory, write nothing")
    args = ap.parse_args()

    sounds = parse_spec()
    if not args.check:
        os.makedirs(OUT_DIR, exist_ok=True)
    produced, written, problems, approximations = set(), 0, [], []
    rows = []
    for sd in sounds:
        if args.only and args.only not in sd.id:
            continue
        per = []
        for nn in range(1, sd.variants + 1):
            x, pf, tf = render(sd, nn)
            pcm = to_pcm(x)
            st = stats(pcm, sd.peak)
            name = "%s_%02d.wav" % (sd.id, nn)
            produced.add(name)
            expect = sd.dur * tf
            if st["clip"]:
                problems.append("%s: %d clipped samples" % (name, st["clip"]))
            if abs(st["peak_db"] - sd.peak) > 0.1:
                problems.append("%s: peak %.2f dBFS, target %.1f" % (name, st["peak_db"], sd.peak))
            if st["dc"] > 0.005:
                problems.append("%s: DC offset %.4f" % (name, st["dc"]))
            if st["rms_db"] < -60:
                problems.append("%s: near silent (RMS %.1f dBFS)" % (name, st["rms_db"]))
            if st["ms"] > expect + 0.5 or st["ms"] < 0.25 * expect:
                problems.append("%s: %.0f ms, expected <= %.0f ms" % (name, st["ms"], expect))
            if not args.check:
                path = os.path.join(OUT_DIR, name)
                write_if_changed(path + ".meta", meta_for(path, st["ms"] / 1000.0))
                written += write_if_changed(path, wav_bytes(pcm))
            per.append(st)
        if sd.id in LOOPS:
            x, apx = render_loop(sd, LOOPS[sd.id])
            approximations += apx
            pcm = to_pcm(x)
            st = stats(pcm, sd.peak)
            name = sd.id + "_loop.wav"
            produced.add(name)
            seam = abs(int(pcm[-1]) - int(pcm[0]))
            step = float(np.median(np.abs(np.diff(pcm.astype(np.int32))))) + 1.0
            if seam > 4 * step + 50:
                problems.append("%s: loop seam jump %d (median step %.0f)" % (name, seam, step))
            if st["clip"] or abs(st["peak_db"] - sd.peak) > 0.1 or st["rms_db"] < -60:
                problems.append("%s: loop level problem %r" % (name, st))
            if not args.check:
                path = os.path.join(OUT_DIR, name)
                write_if_changed(path + ".meta", meta_for(path, st["ms"] / 1000.0))
                written += write_if_changed(path, wav_bytes(pcm))
            rows.append((name[:-4], 1, "loop", "%.0f" % st["ms"], "%.0f" % st["ms"], st["peak_db"], sd.peak,
                         st["dc"], st["rms_db"], "seam %d" % seam))
        rows.append((sd.id, len(per), sd.bus, "%.0f" % min(p["ms"] for p in per), "%.0f" % max(p["ms"] for p in per),
                     max(p["peak_db"] for p in per), sd.peak, max(p["dc"] for p in per),
                     min(p["rms_db"] for p in per), "spec %g" % sd.dur))

    # Runtime mix table.
    table = ["# Generated by tools/audio/gen_sfx.py from design/audio/sfx-spec.md. Do not edit by hand.",
             "# id\tbus\tpri\tmax\tcd_ms\tpitch_pct\tpeak_dbfs\tvariants"]
    for sd in sounds:
        table.append("%s\t%s\t%d\t%d\t%d\t%g\t%g\t%d" % (sd.id, sd.bus, sd.pri, sd.max, sd.cd, sd.pitch, sd.peak, sd.variants))
    for lid in LOOPS:
        sd = next(s for s in sounds if s.id == lid)
        table.append("%s_loop\t%s\t%d\t%d\t%d\t0\t%g\t1" % (lid, sd.bus, sd.pri, 2, 0, sd.peak))
    if not args.check and not args.only:
        write_if_changed(META_TABLE, "\n".join(table) + "\n")
        for f in sorted(os.listdir(OUT_DIR)):
            if f.endswith(".wav") and f not in produced:
                os.remove(os.path.join(OUT_DIR, f))
                if os.path.exists(os.path.join(OUT_DIR, f + ".meta")):
                    os.remove(os.path.join(OUT_DIR, f + ".meta"))
                print("removed stale", f)

    print("%-20s %3s %-4s %7s %7s %8s %6s %8s %8s  %s" % ("id", "var", "bus", "min ms", "max ms", "peak", "target",
                                                       "max DC", "min RMS", "note"))
    for r in rows:
        print("%-20s %3d %-4s %7s %7s %8.2f %6.1f %8.5f %8.1f  %s" % r)
    total_files = sum(r[1] for r in rows)
    size = sum(os.path.getsize(os.path.join(OUT_DIR, f)) for f in os.listdir(OUT_DIR)
               if f.endswith(".wav")) if os.path.isdir(OUT_DIR) else 0
    print("\n%d ids, %d files, %.2f MB on disk, %d written/changed this run" %
          (len([r for r in rows if r[2] != "loop"]), total_files, size / 1e6, written))
    for a in approximations:
        print("loop approximation:", a)
    if problems:
        print("\nVALIDATION FAILED (%d):" % len(problems))
        for p in problems:
            print("  " + p)
        return 1
    print("validation OK: no clipping, |DC| <= 0.005, peaks within 0.1 dB of target, durations within spec, no silent files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
