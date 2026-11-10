"""
Cutout sprite-sheet animator for Chibi Clash (adapted from the copy-ghibli-smash fork's tools/art/gen_cutout_anims.py).

    python -P tools/art/gen_cutout_anims.py [Id ...] [--clips Idle,Swing,...]

For every CharacterRoster character (Kaia, Bruna, Ren, Lyra, Taro) it poses the full-body FRONT cutout
(Resources/Characters/<Id>.png, made by tools/art/cutout_sheets.py) frame by frame, swings the signature weapon
(Resources/Weapons/Hero_<Id>.png, drawn upright) from the hand, and draws flat-colour, black-outlined effects in the
character's palette (VFX colours from the sheets). Output, one horizontal row per clip (+ a Unity .meta written first):

    Assets/ChibiClash/Resources/Animations/<Id>/<Clip>.png

Clips: Idle Run Jump Hit KO Victory (basic), Skill1 Skill2 Ultimate (class kit), and the generic attack set the new
moveset uses: Jab Swing Upswing LowSweep Thrust Slam Spin Shoot Cast Dive Uppercut Guard Rush Launch Dash.
Frame counts are FRAMES below and must match SpriteAnimLibrary.DefaultFrames (SpriteAnim.cs).

Frames are 384 x 256: feet at the horizontal centre, 4% above the bottom, figure 78% of the frame height (the layout
SpriteAnimLibrary expects), 24 fps, anticipation -> strike -> follow-through -> recovery with squash/stretch.
These are cutout animations of the front-facing drawing; replace any file with a hand-drawn sheet of the same name.
"""
import math
import os
import random
import sys

from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unity_meta import in_assets, write_folder_meta, write_meta  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
RES = os.path.join(ROOT, "Assets", "ChibiClash", "Resources")
OUT = os.environ.get("CHIBI_ANIM_OUT") or os.path.join(RES, "Animations")

W, H = 384, 256                 # frame size (1x)
SS = 2                          # supersampling
FX, FY = W // 2, H - int(H * 0.04)
FIG = H * 0.78                  # figure height in px (SpriteAnimLibrary.FigureFill)
PPU = FIG / 1.85                # px per world unit (ArtHeight = 1.85)
INK = (24, 20, 26, 255)
HAND = (0.2 * PPU, 0.75 * PPU)  # hand rest relative to the feet (x right, y up)

PHA, PHS, PHF = 0.24, 0.42, 0.62

# ------------------------------------------------------------------------------------------------ hero data (mirrors C#)

KIND = {"Melee": 35, "Heavy": 70, "Ranged": 0, "Magic": -10, "Polearm": 12}     # rest angle by archetype
# id: (kind, gripX, gripY, length (world units), rest angle or None = by kind, fx colour, accent colour)
# Weapons are cut upright (business end up); grip is 0..1 from the bottom-left. Mirrors FighterVisual.BuildHeroWeapon.
HEROES = {
    "Kaia": ("Melee", .60, .12, 0.95, None, (70, 220, 210), (232, 92, 92)),      # Twin Curved Swords (Waveblade)
    "Bruna": ("Heavy", .39, .25, 1.45, None, (255, 200, 60), (240, 128, 48)),     # Two-Handed War Hammer
    "Ren": ("Ranged", .83, .50, 1.3, 80, (80, 200, 255), (240, 180, 40)),        # Mechanized Recurve Bow, held upright
    "Lyra": ("Magic", .27, .35, 0.7, 80, (200, 140, 255), (120, 220, 180)),      # Casting Rod (charm catalyst)
    "Taro": ("Polearm", .50, .30, 2.1, None, (80, 230, 150), (240, 230, 200)),   # Jadeleaf Glaive
}

# ------------------------------------------------------------------------------------------------ easing


def clamp01(t):
    return max(0.0, min(1.0, t))


def ease_out(t):
    t = clamp01(t)
    return 1 - (1 - t) ** 2


def ease_in(t):
    t = clamp01(t)
    return t ** 3


def ease_io(t):
    t = clamp01(t)
    return t * t * (3 - 2 * t)


def lerp(a, b, t):
    return a + (b - a) * t


def seg(t, a, b):
    """Local 0..1 progress of t inside [a, b]."""
    return clamp01((t - a) / max(1e-6, b - a))


def phases(t, rest, wind, hit, over):
    """Value through anticipation -> strike -> follow-through -> recovery."""
    if t < PHA:
        return lerp(rest, wind, ease_out(t / PHA))
    if t < PHS:
        return lerp(wind, hit, ease_in(seg(t, PHA, PHS)))
    if t < PHF:
        return lerp(hit, over, ease_out(seg(t, PHS, PHF)))
    return lerp(over, rest, ease_io(seg(t, PHF, 1)))


def striking(t):
    return PHA <= t < PHF


def strike_k(t):
    """0..1 envelope for effects: rises at the strike, fades through the follow-through."""
    if t < PHA:
        return 0.0
    if t < PHS:
        return seg(t, PHA, PHS)
    return 1 - seg(t, PHS, 0.8)

# ------------------------------------------------------------------------------------------------ assets


class Assets:
    def __init__(self, hero):
        self.hero = hero
        kind, gx, gy, length, rest, color, accent = HEROES[hero]
        self.kind, self.color, self.accent = kind, color, accent
        wrot = -90                                  # upright art: turn so the business end points +x
        self.rest = KIND[kind] if rest is None else rest
        art = Image.open(os.path.join(RES, "Characters", hero + ".png")).convert("RGBA")
        s = FIG * SS / 970.0
        self.art = art.resize((int(1024 * s), int(1024 * s)), Image.LANCZOS)
        self.art_feet = (512 * s, 1010 * s)
        w = Image.open(os.path.join(RES, "Weapons", "Hero_" + hero + ".png")).convert("RGBA")
        L = length * PPU * SS
        ws = L / max(w.size)
        w = w.resize((max(1, int(w.size[0] * ws)), max(1, int(w.size[1] * ws))), Image.LANCZOS)
        # turn so the business end points +x, remember where the grip ends up
        grip = (gx * w.size[0], (1 - gy) * w.size[1])
        c = (w.size[0] / 2, w.size[1] / 2)
        self.weapon = w.rotate(wrot, expand=True, resample=Image.BICUBIC)
        a = math.radians(wrot)
        vx, vy = grip[0] - c[0], grip[1] - c[1]
        rx, ry = vx * math.cos(a) + vy * math.sin(a), -vx * math.sin(a) + vy * math.cos(a)
        nc = (self.weapon.size[0] / 2, self.weapon.size[1] / 2)
        self.grip = (nc[0] + rx, nc[1] + ry)

# ------------------------------------------------------------------------------------------------ frame context


class Frame:
    """One frame: pose state + effect layers, rendered at SS x."""

    def __init__(self, assets):
        self.a = assets
        self.back = Image.new("RGBA", (W * SS, H * SS), (0, 0, 0, 0))
        self.front = Image.new("RGBA", (W * SS, H * SS), (0, 0, 0, 0))
        self.db = ImageDraw.Draw(self.back)
        self.df = ImageDraw.Draw(self.front)
        # pose (x right, y up, px at 1x relative to the feet)
        self.dx = self.dy = 0.0
        self.rot = 0.0          # degrees, + = lean back (counter-clockwise for a right-facing character)
        self.sx = self.sy = 1.0
        self.alpha = 1.0
        self.flash = 0.0        # 0..1 white
        self.tint = None        # (r,g,b,amount)
        self.body = True
        self.wangle = assets.rest
        self.hand = [0.0, 0.0]  # extra hand offset
        self.weapon = True
        self.ghosts = []        # (dx, dy, alpha, tint)
        self.scale_extra = 1.0
        self.free = False       # True: full translation (KO flying off); otherwise moves stay mostly in place

    # --- coordinates
    def P(self, x, y):
        return ((FX + x) * SS, (FY - y) * SS)

    def off(self):
        """Body translation actually drawn: the game moves the fighter itself, so sheets stay near the centre."""
        if self.free:
            return self.dx, self.dy
        return self.dx * 0.5, max(-10.0, min(36.0, self.dy * 0.6))

    def hand_pos(self):
        ox, oy = self.off()
        hx, hy = HAND[0] * self.sx, HAND[1] * self.sy
        r = math.radians(self.rot)
        rx, ry = hx * math.cos(r) - hy * math.sin(r), hx * math.sin(r) + hy * math.cos(r)
        return (ox + rx + self.hand[0], oy + ry + self.hand[1])

    # --- drawing primitives (layer = 'f' front / 'b' back)
    def _d(self, layer):
        return self.df if layer == "f" else self.db

    def line(self, pts, color, w, layer="f", outline=True, alpha=1.0):
        d = self._d(layer)
        p = [self.P(x, y) for x, y in pts]
        if outline:
            d.line(p, fill=INK[:3] + (int(255 * alpha),), width=int((w + 4) * SS), joint="curve")
        d.line(p, fill=tuple(color[:3]) + (int(255 * alpha),), width=int(w * SS), joint="curve")

    def arc(self, cx, cy, r, a0, a1, color, w, layer="f", alpha=1.0, taper=True):
        """Slash crescent from angle a0 to a1 (degrees, CCW from +x), thickest at a1."""
        n = 18
        pts_out, pts_in = [], []
        for i in range(n + 1):
            u = i / n
            ang = math.radians(lerp(a0, a1, u))
            th = w * (u if taper else 1.0)
            pts_out.append((cx + math.cos(ang) * (r + th / 2), cy + math.sin(ang) * (r + th / 2)))
            pts_in.append((cx + math.cos(ang) * (r - th / 2), cy + math.sin(ang) * (r - th / 2)))
        self.poly(pts_out + pts_in[::-1], color, layer, alpha)

    def poly(self, pts, color, layer="f", alpha=1.0, outline=True):
        d = self._d(layer)
        d.polygon([self.P(x, y) for x, y in pts], fill=tuple(color[:3]) + (int(255 * alpha),),
                  outline=INK[:3] + (int(255 * alpha),) if outline else None, width=3 * SS if outline else 0)

    def circle(self, x, y, r, color, layer="f", alpha=1.0, outline=True, ring=0):
        d = self._d(layer)
        b = [(FX + x - r) * SS, (FY - y - r) * SS, (FX + x + r) * SS, (FY - y + r) * SS]
        if ring:
            if outline:
                d.ellipse(b, outline=INK[:3] + (int(255 * alpha),), width=int((ring + 4) * SS))
            d.ellipse(b, outline=tuple(color[:3]) + (int(255 * alpha),), width=int(ring * SS))
        else:
            d.ellipse(b, fill=tuple(color[:3]) + (int(255 * alpha),), outline=INK[:3] + (int(255 * alpha),) if outline else None,
                      width=3 * SS if outline else 0)

    def ellipse(self, x, y, rx, ry, color, layer="f", alpha=1.0, ring=0):
        d = self._d(layer)
        b = [(FX + x - rx) * SS, (FY - y - ry) * SS, (FX + x + rx) * SS, (FY - y + ry) * SS]
        if ring:
            d.ellipse(b, outline=INK[:3] + (int(255 * alpha),), width=int((ring + 4) * SS))
            d.ellipse(b, outline=tuple(color[:3]) + (int(255 * alpha),), width=int(ring * SS))
        else:
            d.ellipse(b, fill=tuple(color[:3]) + (int(255 * alpha),), outline=INK[:3] + (int(255 * alpha),), width=3 * SS)

    def star(self, x, y, r, color, points=5, layer="f", alpha=1.0, inner=0.45, rot=0.0):
        pts = []
        for i in range(points * 2):
            ang = math.radians(rot + 90 + i * 180.0 / points)
            rr = r if i % 2 == 0 else r * inner
            pts.append((x + math.cos(ang) * rr, y + math.sin(ang) * rr))
        self.poly(pts, color, layer, alpha)

    def sparkle(self, x, y, r, color, layer="f", alpha=1.0):
        self.star(x, y, r, color, 4, layer, alpha, 0.25)

    # --- shapes used by many moves
    def arrow(self, x, y, ang, length, color=(240, 235, 215), layer="f", alpha=1.0):
        a = math.radians(ang)
        tx, ty = x + math.cos(a) * length, y + math.sin(a) * length
        self.line([(x, y), (tx, ty)], (150, 110, 70), 3, layer, alpha=alpha)
        hx, hy = math.cos(a) * 10, math.sin(a) * 10
        self.poly([(tx + hx, ty + hy), (tx - hy * 0.6, ty + hx * 0.6), (tx + hy * 0.6, ty - hx * 0.6)], color, layer, alpha)

    def orb(self, x, y, r, color, layer="f", alpha=1.0):
        self.circle(x, y, r * 1.6, color, "b", alpha * 0.35, outline=False)
        self.circle(x, y, r, color, layer, alpha)
        self.circle(x - r * 0.3, y + r * 0.3, r * 0.3, (255, 255, 255), layer, alpha * 0.9, outline=False)

    def diamond(self, x, y, r, color, layer="f", alpha=1.0):
        self.poly([(x, y + r), (x + r * 0.7, y), (x, y - r), (x - r * 0.7, y)], color, layer, alpha)

    def note(self, x, y, s, color, layer="f", alpha=1.0):
        self.ellipse(x, y, s * 0.6, s * 0.45, color, layer, alpha)
        self.line([(x + s * 0.5, y), (x + s * 0.5, y + s * 1.8), (x + s * 1.2, y + s * 1.4)], color, 2, layer, alpha=alpha)

    def puff(self, x, y, r, color=(200, 200, 205), layer="f", alpha=1.0):
        for ox, oy, k in ((-0.5, 0, 0.7), (0.5, 0.1, 0.75), (0, 0.4, 0.85), (0, -0.2, 0.8)):
            self.circle(x + ox * r, y + oy * r, r * k, color, layer, alpha)

    def shockwave(self, x, r, color, alpha=1.0):
        self.ellipse(x, 0, r, r * 0.22, color, "f", alpha, ring=5)

    def debris(self, x, k, seed=0):
        rnd = random.Random(seed)
        for i in range(7):
            ang = math.radians(rnd.uniform(30, 150))
            d = 20 + 70 * k
            px, py = x + math.cos(ang) * d, math.sin(ang) * d * 1.2 - 30 * k * k
            if py > -2:
                s = rnd.uniform(4, 8)
                self.poly([(px - s, py), (px, py + s), (px + s, py - s * 0.3)], (130, 115, 100), "f", 1 - k * 0.6)

    def ghost(self, dx, dy, alpha, tint=(40, 40, 70)):
        self.ghosts.append((dx, dy, alpha, tint))

    # --- render
    def body_image(self, tint=None, alpha=None):
        a = self.a
        sx, sy = self.sx * self.scale_extra, self.sy * self.scale_extra
        im = a.art.resize((max(1, int(a.art.size[0] * sx)), max(1, int(a.art.size[1] * sy))), Image.BILINEAR)
        fx, fy = a.art_feet[0] * sx, a.art_feet[1] * sy
        if self.flash > 0:
            white = Image.new("RGBA", im.size, (255, 255, 255, 255))
            white.putalpha(im.getchannel("A"))
            im = Image.blend(im, white, min(1, self.flash))
        t = tint or self.tint
        if t:
            col = Image.new("RGBA", im.size, t[:3] + (255,))
            col.putalpha(im.getchannel("A"))
            im = Image.blend(im, col, t[3])
        al = self.alpha if alpha is None else alpha
        if al < 0.999:
            im.putalpha(im.getchannel("A").point(lambda v: int(v * al)))
        # rotate around the feet
        size = int(max(im.size) * 2.2)
        canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        canvas.alpha_composite(im, (int(size / 2 - fx), int(size / 2 - fy)))
        if abs(self.rot) > 0.01:
            canvas = canvas.rotate(self.rot, resample=Image.BICUBIC, center=(size / 2, size / 2))
        return canvas

    def render(self):
        a = self.a
        out = Image.new("RGBA", (W * SS, H * SS), (0, 0, 0, 0))
        out.alpha_composite(self.back)
        ox, oy = self.off()
        for gdx, gdy, gal, gt in self.ghosts:
            img = self.body_image((gt[0], gt[1], gt[2], 0.75), gal * self.alpha)
            paste(out, img, (FX + ox + gdx) * SS - img.size[0] / 2, (FY - oy - gdy) * SS - img.size[1] / 2)
        if self.body and self.alpha > 0.01:
            img = self.body_image()
            paste(out, img, (FX + ox) * SS - img.size[0] / 2, (FY - oy) * SS - img.size[1] / 2)
        if self.body and self.weapon and self.alpha > 0.01:
            wimg = a.weapon.rotate(self.wangle, expand=True, resample=Image.BICUBIC)
            ang = math.radians(self.wangle)
            c0 = (a.weapon.size[0] / 2, a.weapon.size[1] / 2)
            vx, vy = a.grip[0] - c0[0], a.grip[1] - c0[1]
            rx, ry = vx * math.cos(ang) + vy * math.sin(ang), -vx * math.sin(ang) + vy * math.cos(ang)
            nc = (wimg.size[0] / 2, wimg.size[1] / 2)
            hx, hy = self.hand_pos()
            if self.alpha < 0.999:
                wimg.putalpha(wimg.getchannel("A").point(lambda v: int(v * self.alpha)))
            paste(out, wimg, (FX + hx) * SS - (nc[0] + rx), (FY - hy) * SS - (nc[1] + ry))
        out.alpha_composite(self.front)
        return out.resize((W, H), Image.LANCZOS)


def paste(dst, src, x, y):
    x, y = int(round(x)), int(round(y))
    sx0, sy0 = max(0, -x), max(0, -y)
    dx0, dy0 = max(0, x), max(0, y)
    w = min(src.size[0] - sx0, dst.size[0] - dx0)
    h = min(src.size[1] - sy0, dst.size[1] - dy0)
    if w <= 0 or h <= 0:
        return
    dst.alpha_composite(src.crop((sx0, sy0, sx0 + w, sy0 + h)), (dx0, dy0))

# ------------------------------------------------------------------------------------------------ basic animations


def idle(f, t, p):
    b = math.sin(t * 2 * math.pi)
    f.sy = 1 + 0.02 * b
    f.sx = 1 - 0.012 * b
    f.dy = 1.5 * (b + 1)
    f.wangle = f.a.rest + 4 * math.sin(t * 2 * math.pi + 0.6)
    f.rot = 1.2 * math.sin(t * 2 * math.pi + 1.2)


def run(f, t, p):
    ph = t * 2 * math.pi * 2           # two steps per loop
    step = abs(math.sin(ph))
    f.dy = 7 * step
    f.sy = 1 + 0.06 * (step - 0.5)
    f.sx = 1 - 0.04 * (step - 0.5)
    f.rot = -9 + 3 * math.sin(ph)
    f.wangle = f.a.rest - 10 + 12 * math.sin(ph)
    for i in range(3):
        y = 30 + i * 25
        f.line([(-60 - 30 * ((t * 3 + i * 0.3) % 1), y), (-25 - 30 * ((t * 3 + i * 0.3) % 1), y)], (255, 255, 255), 2, "b", alpha=0.6)
    if step < 0.15:
        f.puff(-20, 4, 6, (220, 215, 205), "b", 0.7)


def jump(f, t, p):
    if t < 0.2:                        # crouch
        k = ease_out(t / 0.2)
        f.sy, f.sx = 1 - 0.18 * k, 1 + 0.12 * k
    elif t < 0.45:                     # leap
        k = seg(t, 0.2, 0.45)
        f.dy = 60 * ease_out(k)
        f.sy, f.sx = 1.12 - 0.12 * k, 0.92 + 0.08 * k
        if k < 0.4:
            f.puff(-12, 2, 9, (225, 220, 210), "b", 1 - k * 2)
            f.puff(14, 2, 8, (225, 220, 210), "b", 1 - k * 2)
    elif t < 0.7:                      # tuck at the apex
        k = seg(t, 0.45, 0.7)
        f.dy = 60 + 6 * math.sin(k * math.pi)
        f.sy, f.sx = 0.92, 1.04
        f.rot = -8 * math.sin(k * math.pi)
        f.wangle = f.a.rest + 25
    else:                              # fall
        k = seg(t, 0.7, 1)
        f.dy = 66 * (1 - ease_in(k))
        f.sy, f.sx = 1.08, 0.95
        f.wangle = f.a.rest + 15


def hit(f, t, p):
    k = 1 - t
    f.dx = -14 * math.sin(min(1, t * 2.2) * math.pi * 0.5) * (1 - ease_io(seg(t, 0.5, 1)))
    f.rot = 16 * k
    f.sx, f.sy = 1 + 0.1 * k, 1 - 0.08 * k
    f.flash = 0.7 if t < 0.2 else 0
    f.wangle = f.a.rest + 40 * k
    if t < 0.5:
        f.star(26, 70, 18 * (1 - t), (255, 240, 120), 6, "f", 1 - t * 1.6)
        f.star(26, 70, 9 * (1 - t), (255, 255, 255), 6, "f", 1 - t * 1.6)


def ko(f, t, p):
    f.free = True
    f.rot = 720 * t
    f.dx = 140 * ease_in(t)
    f.dy = 20 + 70 * ease_out(t)
    f.sx = f.sy = 1 - 0.45 * t
    f.flash = 0.4 if t < 0.15 else 0
    for i in range(4):
        ang = t * 600 + i * 90
        r = 50 + 20 * t
        f.sparkle(f.dx + math.cos(math.radians(ang)) * r, f.dy + 60 + math.sin(math.radians(ang)) * r * 0.5, 9, (255, 230, 90), "f", 1 - t * 0.5)
    if t > 0.6:
        f.star(150, 150, 22 * (1 - (t - 0.6) * 2), (255, 255, 255), 4, "f", 1, 0.25)


def victory(f, t, p):
    if t < 0.35:
        k = ease_out(t / 0.35)
        f.sy, f.sx = 1 - 0.12 * k, 1 + 0.08 * k
        f.wangle = f.a.rest + 30 * k
    elif t < 0.7:
        k = seg(t, 0.35, 0.7)
        f.dy = 34 * math.sin(k * math.pi)
        f.sy, f.sx = 1.1, 0.94
        f.wangle = lerp(f.a.rest + 30, 95, ease_out(k))
    else:
        k = seg(t, 0.7, 1)
        f.wangle = 95
        f.sy = 1 - 0.05 * math.sin(k * math.pi)
    for i in range(5):
        ang = i * 72 + t * 80
        r = 70 + 10 * math.sin(t * 6 + i)
        f.sparkle(math.cos(math.radians(ang)) * r * 0.9, 110 + math.sin(math.radians(ang)) * r * 0.6, 7, f.a.color, "b", min(1, t * 3))

# ------------------------------------------------------------------------------------------------ move templates


def slash(f, t, p):
    """Weapon arc with a coloured trail. p: color, up (rising slash), lunge, frm, to, impact, crouch."""
    col = p.get("color", f.a.color)
    frm, to = p.get("frm", 115), p.get("to", -55)
    if p.get("up"):
        frm, to = -70, 100
    f.wangle = phases(t, f.a.rest, frm + (25 if frm > to else -25), to, to + (-18 if frm > to else 18))
    f.rot = phases(t, 0, 8, -12, -6)
    f.sx = phases(t, 1, 1.05, 0.94, 1)
    f.sy = phases(t, 1, 0.92 if p.get("crouch") else 0.95, 1.08, 1)
    f.dx = phases(t, 0, -6, p.get("lunge", 18), p.get("lunge", 18) * 0.8)
    if p.get("up"):
        f.dy = phases(t, 0, -4, 16, 10)
    k = strike_k(t)
    if k > 0:
        hx, hy = f.hand_pos()
        cur = f.wangle
        start = frm
        r = p.get("radius", 70)
        f.arc(hx, hy, r, start, cur, col, p.get("width", 22), "f", alpha=min(1, k * 1.4))
        if p.get("drag") and t < PHS:
            f.line([(f.dx - 10, 4), (f.dx + 40, 2)], (200, 170, 130), 3, "b", alpha=0.8)
    if p.get("impact") and PHS <= t < 0.75:
        q = seg(t, PHS, 0.75)
        ix, iy = p.get("impact")
        f.star(ix, iy, 26 * (1 - q * 0.5), (255, 245, 150), 7, "f", 1 - q)
        f.star(ix, iy, 12 * (1 - q * 0.5), (255, 255, 255), 7, "f", 1 - q)


def thrust(f, t, p):
    col = p.get("color", (255, 255, 255))
    f.wangle = phases(t, f.a.rest, 8, 0, 0)
    lunge = p.get("lunge", 40)
    f.dx = phases(t, 0, -10, lunge, lunge * 0.8)
    f.hand[0] = phases(t, 0, -18, 40, 46)
    f.rot = phases(t, 0, 10, -14, -8)
    f.sx = phases(t, 1, 0.95, 1.08, 1)
    k = strike_k(t)
    if k > 0:
        hx, hy = f.hand_pos()
        L = p.get("length", 150)
        f.line([(hx - 30, hy + 2), (hx + L * k, hy + 2)], col, p.get("width", 5), "f", alpha=k)
        f.line([(hx - 10, hy - 9), (hx + L * k * 0.7, hy - 9)], col, 2, "f", alpha=k * 0.7)
        if p.get("ghosts"):
            for g in range(3):
                f.ghost(-20 - g * 18, 0, 0.35 - g * 0.1, (255, 255, 255))


def slam(f, t, p):
    """Jump up with the weapon raised, smash down, shockwave + debris."""
    col = p.get("color", f.a.color)
    jump_h = p.get("height", 55)
    if t < 0.18:
        k = ease_out(t / 0.18)
        f.sy, f.sx = 1 - 0.16 * k, 1 + 0.1 * k
        f.wangle = lerp(f.a.rest, 130, k)
    elif t < 0.42:
        k = seg(t, 0.18, 0.42)
        f.dy = jump_h * math.sin(k * math.pi * 0.5)
        f.dx = 14 * k
        f.sy, f.sx = 1.1, 0.93
        f.wangle = 150
        f.rot = 8 * k
    elif t < 0.5:
        k = seg(t, 0.42, 0.5)
        f.dy = jump_h * (1 - ease_in(k))
        f.dx = 14 + 8 * k
        f.wangle = lerp(150, -80, ease_in(k))
        f.rot = lerp(8, -14, k)
    else:
        k = seg(t, 0.5, 1)
        f.dx = 22 * (1 - ease_io(seg(t, 0.75, 1)))
        f.wangle = lerp(-80, f.a.rest, ease_io(seg(t, 0.7, 1)))
        f.sy = 1 - 0.18 * (1 - k) if k < 0.4 else 1
        f.sx = 1 + 0.12 * (1 - k) if k < 0.4 else 1
        f.rot = lerp(-14, 0, ease_io(k))
        hx = 60
        f.shockwave(hx, 30 + 120 * ease_out(k), col, 1 - k)
        if p.get("ring"):
            f.circle(hx, 20, 20 + 110 * ease_out(k), col, "b", (1 - k) * 0.8, ring=6)
        if p.get("debris", True):
            f.debris(hx, k, 3)
        if p.get("cracks") and k < 0.8:
            for ang in (-15, 10, 40):
                a = math.radians(ang)
                f.line([(hx, 1), (hx + math.cos(a) * 40, 1 + math.sin(a) * 4)], (60, 50, 45), 2, "f", False, 1 - k)
        if p.get("flames"):
            for i in range(5):
                x = hx - 40 + i * 22
                hgt = 18 + 10 * math.sin(k * 20 + i)
                f.poly([(x - 8, 1), (x, hgt), (x + 8, 1)], (255, 120 + i * 20, 40), "f", 0.9)
        if p.get("dust", True) and k < 0.6:
            f.puff(hx - 40, 6, 12, (215, 205, 190), "f", 1 - k / 0.6)
            f.puff(hx + 40, 6, 12, (215, 205, 190), "f", 1 - k / 0.6)


def shoot(f, t, p):
    """Ranged release: draw/raise, release snap, projectile(s) leaving the frame. p: shape, count, spread, color, lob, hop."""
    col = p.get("color", f.a.color)
    f.wangle = phases(t, f.a.rest, f.a.rest + p.get("raise", 6), f.a.rest - 4, f.a.rest)
    f.hand[0] = phases(t, 0, -12, 6, 0)
    f.rot = phases(t, 0, 6, -6, -2)
    if p.get("hop"):
        k = seg(t, 0, 0.6)
        f.dx = -45 * ease_out(k)
        f.dy = 40 * math.sin(k * math.pi)
        f.rot = -15 * math.sin(k * math.pi)
    if t >= PHA:
        k = seg(t, PHA, 0.85)
        hx, hy = f.hand_pos()
        n = p.get("count", 1)
        for i in range(n):
            spread = (i - (n - 1) / 2) * p.get("spread", 0)
            ang = p.get("angle", 0) + spread
            dist = 30 + 190 * k
            x = hx + 20 + math.cos(math.radians(ang)) * dist
            y = hy + math.sin(math.radians(ang)) * dist
            if p.get("lob"):
                x = hx + 20 + 170 * k
                y = hy + 70 * math.sin(k * math.pi) - 60 * k * k
            draw_proj(f, p.get("shape", "orb"), x, y, ang, col, 1.0 if k < 0.9 else (1 - k) * 10)
            if p.get("trail"):
                f.line([(x - 40 * math.cos(math.radians(ang)), y - 40 * math.sin(math.radians(ang))), (x, y)], p.get("trail"), 3, "b", False, 0.7)
        if t < PHS + 0.08:
            f.star(hx + 18, hy, 14, (255, 250, 200), 6, "f", 1 - seg(t, PHA, PHS + 0.08))
    if p.get("pop") and t > 0.75:
        q = seg(t, 0.75, 1)
        f.star(185, f.hand_pos()[1], 22 * (0.5 + q), col, 8, "f", 1 - q)


def draw_proj(f, shape, x, y, ang, col, alpha):
    if shape == "arrow":
        f.arrow(x - 30, y, ang, 30, (240, 235, 215), "f", alpha)
    elif shape == "orb":
        f.orb(x, y, 9, col, "f", alpha)
    elif shape == "fireball":
        f.circle(x - 10, y, 12, (255, 200, 60), "b", alpha * 0.6, False)
        f.orb(x, y, 11, (255, 110, 30), "f", alpha)
    elif shape == "diamond":
        f.diamond(x, y, 14, col, "f", alpha)
    elif shape == "shard":
        f.poly([(x + 16, y), (x - 10, y + 6), (x - 10, y - 6)], (190, 235, 255), "f", alpha)
    elif shape == "bullet":
        f.line([(x - 18, y), (x, y)], (255, 235, 150), 3, "f", alpha=alpha)
    elif shape == "kunai":
        f.poly([(x + 14, y), (x - 4, y + 5), (x - 4, y - 5)], (200, 205, 215), "f", alpha)
        f.line([(x - 4, y), (x - 16, y)], (60, 60, 80), 3, "f", alpha=alpha)
    elif shape == "coin":
        f.circle(x, y, 7, (250, 210, 70), "f", alpha)
    elif shape == "page":
        f.poly([(x - 9, y - 6), (x + 9, y - 2), (x + 7, y + 7), (x - 8, y + 4)], (250, 246, 230), "f", alpha)
    elif shape == "flask":
        f.circle(x, y, 9, col, "f", alpha)
        f.line([(x, y + 9), (x, y + 16)], (230, 240, 245), 4, "f", alpha=alpha)
    elif shape == "pouch":
        f.circle(x, y, 8, (180, 150, 90), "f", alpha)
    elif shape == "turnip":
        f.circle(x, y, 9, (245, 235, 245), "f", alpha)
        f.poly([(x - 4, y + 8), (x, y + 18), (x + 4, y + 8)], (110, 180, 80), "f", alpha)
    elif shape == "crescent":
        f.arc(x - 10, y, 26, -60, 60, col, 12, "f", alpha, taper=False)
    elif shape == "wave":
        f.circle(x - 30, y, 34, col, "f", alpha * 0.9, ring=4)
    elif shape == "barrel":
        f.circle(x, 14, 13, (150, 100, 55), "f", alpha)
        f.line([(x - 12, 14), (x + 12, 14)], (90, 60, 35), 2, "f", False, alpha)


def cast(f, t, p):
    """Raise the weapon and release an aura effect. p: effect, color, raise (or raise_)."""
    if "raise_" in p:
        p = dict(p)
        p["raise"] = p.pop("raise_")
    col = p.get("color", f.a.color)
    f.wangle = phases(t, f.a.rest, f.a.rest + 15, p.get("raise", 85), p.get("raise", 85) - 5)
    f.dy = phases(t, 0, -4, 6, 3)
    f.sy = phases(t, 1, 0.94, 1.06, 1)
    k = seg(t, PHA, 0.95)
    eff = p.get("effect", "ring")
    cx, cy = p.get("at", (40, 70))
    if t < PHA:
        return
    if eff == "ring":
        for j in range(3):
            q = clamp01(k * 1.5 - j * 0.25)
            if q > 0:
                f.circle(cx, cy, 15 + 90 * q, col, "f", 1 - q, ring=5)
    elif eff == "sparkles":
        for j in range(9):
            q = (k + j / 9.0) % 1
            f.sparkle(-50 + j * 13, 20 + q * 150, 7, col, "f" if j % 2 else "b", 1 - q)
        f.ellipse(0, 70, 60, 85, col, "b", 0.15 * (1 - abs(k - 0.5) * 2))
    elif eff == "notes":
        for j in range(6):
            q = (k + j / 6.0) % 1
            f.note(20 + q * 150, 60 + 30 * math.sin(q * 6 + j), 8, col, "f", 1 - q)
    elif eff == "plus":
        for j in range(5):
            q = (k + j / 5.0) % 1
            x, y = -30 + j * 15, 60 + q * 100
            f.poly([(x - 3, y - 9), (x + 3, y - 9), (x + 3, y - 3), (x + 9, y - 3), (x + 9, y + 3), (x + 3, y + 3), (x + 3, y + 9),
                    (x - 3, y + 9), (x - 3, y + 3), (x - 9, y + 3), (x - 9, y - 3), (x - 3, y - 3)], (110, 220, 120), "f", 1 - q)
    elif eff == "aura":
        f.ellipse(0, 90, 70 + 15 * math.sin(k * 20), 105, col, "b", 0.45 * (1 - k * 0.5))
        for j in range(6):
            q = (k * 2 + j / 6.0) % 1
            f.poly([(-50 + j * 20, 10 + q * 120), (-45 + j * 20, 30 + q * 120), (-55 + j * 20, 30 + q * 120)], col, "b", 1 - q)
    elif eff == "dome":
        f.ellipse(0, 90, 90 * ease_out(k * 1.5), 110 * ease_out(k * 1.5), col, "f", 0.35)
        f.ellipse(0, 90, 90 * ease_out(k * 1.5), 110 * ease_out(k * 1.5), col, "f", 0.9, ring=3)
    elif eff == "barrier":
        h = 160 * ease_out(k * 1.6)
        f.poly([(55, 0), (85, 0), (85, h), (55, h)], col, "f", 0.4)
        f.line([(55, 0), (55, h)], col, 3, "f")
        f.line([(85, 0), (85, h)], col, 3, "f")
    elif eff == "mark":
        f.circle(150, 175, 10 + 14 * ease_out(k * 2), col, "f", 0.9, ring=4)
        f.star(150, 175, 10 * ease_out(k * 2), col, 4, "f", 1, 0.3)
    elif eff == "bark":
        f.tint = (120, 85, 55, 0.45 * math.sin(k * math.pi))
        f.ellipse(0, 90, 70, 100, (120, 200, 100), "b", 0.35 * math.sin(k * math.pi))
    elif eff == "smoke":
        f.alpha = 1 - ease_out(seg(t, 0.4, 0.8)) * 0.85
        for j in range(6):
            a = j * 60 + k * 40
            f.puff(math.cos(math.radians(a)) * 40 * ease_out(k * 2), 40 + math.sin(math.radians(a)) * 30, 18, (190, 190, 195), "f", 1 - k * 0.6)
    elif eff == "icefloor":
        L = 180 * ease_out(k * 1.4)
        f.poly([(-40, 0), (L, 0), (L - 10, 8), (-40, 8)], (190, 230, 255), "f", 0.95)
        for j in range(int(L / 30)):
            f.sparkle(-20 + j * 30, 14, 5, (230, 250, 255), "f", 0.9)
    elif eff == "vines":
        for j in range(4):
            q = ease_out(k * 1.5 - j * 0.1)
            base = 70 + j * 18
            pts = [(base + 8 * math.sin(i * 0.8 + j), i * 7 * q) for i in range(7)]
            f.line(pts, (80, 150, 60), 4, "f")
    elif eff == "sigil":
        f.ellipse(-60, 2, 40 * ease_out(k * 2), 10 * ease_out(k * 2), col, "b", 0.9, ring=4)
    elif eff == "scroll":
        f.poly([(60, 150), (120, 150), (120, 110), (60, 110)], (245, 220, 140), "f", min(1, k * 3))
        f.star(90, 130, 14, (250, 200, 60), 5, "f", min(1, k * 3))
    elif eff == "talisman":
        x = 30 + 80 * ease_out(k * 2)
        f.poly([(x - 9, 120), (x + 9, 120), (x + 9, 90), (x - 9, 90)], (250, 245, 225), "f", 1)
        f.line([(x, 115), (x, 95)], (200, 40, 50), 3, "f", False)
        if k > 0.5:
            f.circle(x, 105, 26, (255, 120, 120), "b", 0.4 * (1 - k), False)
    elif eff == "cauldron":
        q = ease_out(k * 2)
        f.ellipse(80, 18 * q, 26, 18 * q, (60, 55, 70), "f")
        for j in range(4):
            f.circle(70 + j * 8, 40 + 30 * ((k * 3 + j * 0.3) % 1), 6, (170, 80, 210), "f", 1 - ((k * 3 + j * 0.3) % 1))
    elif eff == "glyph":
        f.ellipse(0, 4, 50, 12, col, "b", 0.8, ring=3)
        f.sparkle(0, 120, 14 * math.sin(k * math.pi), col, "f")


def dash(f, t, p):
    """Crouch, blur forward with afterimages, X-slash marks behind."""
    col = p.get("color", f.a.color)
    if t < PHA:
        k = ease_out(t / PHA)
        f.sy, f.sx, f.rot = 1 - 0.14 * k, 1 + 0.08 * k, 6 * k
    elif t < PHF:
        k = seg(t, PHA, PHF)
        f.dx = 110 * ease_out(k)
        f.rot = -16
        f.wangle = 0
        for g in range(4):
            f.ghost(-25 * (g + 1) * min(1, k * 3), 0, 0.4 - g * 0.09, (30, 30, 50))
        if p.get("x"):
            f.line([(30, 50), (80, 110)], (255, 255, 255), 5, "f", alpha=1 - k * 0.5)
            f.line([(30, 110), (80, 50)], (255, 255, 255), 5, "f", alpha=1 - k * 0.5)
        if p.get("trail"):
            f.line([(f.dx - 120, 30), (f.dx, 30)], col, 8, "b", alpha=0.8)
    else:
        k = ease_io(seg(t, PHF, 1))
        f.dx = 110 * (1 - k)
        f.rot = -16 * (1 - k)


def spin(f, t, p):
    col = p.get("color", (240, 240, 255))
    k = seg(t, 0.1, 0.85)
    f.wangle = f.a.rest + 720 * ease_io(k)
    f.rot = 8 * math.sin(k * math.pi * 4)
    f.sx = 1 + 0.06 * math.sin(k * math.pi * 6)
    if 0.05 < k < 0.95:
        for side in (-1, 1):
            f.arc(0, 75, 80, 90 + side * 80 + k * 200, 90 + side * 80 + k * 200 + 110, col, 10, "f", 0.8)
    if p.get("cape"):
        f.arc(0, 75, 70, k * 360, k * 360 + 160, (140, 100, 80), 26, "b", 0.9, taper=False)
        if 0.3 < k < 0.7:
            f.sparkle(70, 90, 10, (255, 230, 120))


def combo(f, t, p):
    """n quick alternating slashes; the last one bigger."""
    n = p.get("n", 3)
    i = min(n - 1, int(t * n))
    lt = t * n - i
    dirs = p.get("dirs") or [("down", "up")[j % 2] for j in range(n)]
    d = dirs[i]
    sub = dict(p)
    sub["lunge"] = 12
    if d == "up":
        sub["up"] = True
    elif d == "spin":
        spin(f, lt, {"color": p.get("color")})
        return
    elif d == "thrust":
        thrust(f, lt, {"color": p.get("color", (255, 255, 255)), "lunge": 14, "length": 110})
        return
    elif d == "punch":
        f.hand[0] = 30 * math.sin(lt * math.pi)
        f.dx = 8 * math.sin(lt * math.pi)
        f.rot = -8 * math.sin(lt * math.pi)
        for g in range(3):
            f.circle(60 + g * 10 + 20 * lt, 70 + ((i * 37 + g * 13) % 40) - 20, 9, (255, 220, 190), "f", 0.6 * (1 - lt))
        if lt > 0.3:
            f.star(85, 70 + (i * 23 % 30) - 15, 10, (255, 255, 255), 5, "f", 1 - lt)
        return
    slash(f, lt, sub)


def blink(f, t, p):
    """Fade out with sparkles, reappear offset. p: leave (sigil/log/bats), offset, color."""
    col = p.get("color", f.a.color)
    off = p.get("offset", 70)
    leave = p.get("leave")
    if t < 0.4:
        k = t / 0.4
        f.alpha = 1 - k
        f.sx = 1 - 0.3 * k
        for j in range(6):
            f.sparkle(math.cos(j) * 40 * k, 60 + math.sin(j * 2) * 50 * k, 7, col, "f", 1 - k)
        if leave == "bats":
            for j in range(8):
                a = j * 45
                x, y = math.cos(math.radians(a)) * 70 * k, 70 + math.sin(math.radians(a)) * 50 * k
                f.poly([(x - 10, y), (x, y + 6), (x + 10, y), (x, y - 3)], (40, 30, 45), "f", 1)
    else:
        k = seg(t, 0.4, 0.75)
        f.dx = off
        f.alpha = ease_out(k)
        f.sx = 0.8 + 0.2 * ease_out(k)
        if k < 1:
            for j in range(6):
                f.sparkle(off + math.cos(j) * 40 * (1 - k), 60 + math.sin(j * 2) * 50 * (1 - k), 7, col, "f", 1 - k)
    if leave == "sigil" and t > 0.25:
        f.ellipse(0, 3, 45, 11, col, "b", 0.9, ring=4)
        f.star(0, 3, 10, col, 5, "b", 0.8)
    if leave == "log" and t > 0.3:
        f.poly([(-20, 2), (20, 2), (20, 26), (-20, 26)], (150, 110, 70), "f", 1)
        f.circle(20, 14, 12, (200, 160, 110), "f")
        if t < 0.6:
            f.puff(0, 25, 22, (225, 225, 230), "f", 1 - seg(t, 0.3, 0.6))


def summon(f, t, p):
    """Raise the weapon; creatures appear and rush forward, then dissolve. p: what, count, color."""
    col = p.get("color", f.a.color)
    f.wangle = phases(t, f.a.rest, f.a.rest + 20, 90, 85)
    f.dy = phases(t, 0, -4, 5, 2)
    if t < 0.3:
        return
    k = seg(t, 0.3, 1)
    what = p.get("what", "spirit")
    n = p.get("count", 3)
    for i in range(n):
        q = clamp01(k * 1.3 - i * 0.12)
        if q <= 0:
            continue
        a = 1 - max(0, (q - 0.8) * 5)
        if what == "skeleton":
            x, y = 30 + i * 30 + q * 110, min(0, -30 + q * 120)
            f.circle(x, y + 52, 11, (235, 232, 220), "f", a)
            f.poly([(x - 8, y + 40), (x + 8, y + 40), (x + 6, y + 10), (x - 6, y + 10)], (225, 222, 210), "f", a)
            f.circle(x + 3, y + 54, 3, (120, 255, 140), "f", a, False)
        elif what == "fox":
            side = -1 if i % 2 else 1
            x = side * (180 - q * 150)
            y = 20 + 60 * math.sin(q * math.pi)
            f.ellipse(x, y, 20, 11, (255, 150, 60), "f", a)
            f.poly([(x + side * 14, y + 6), (x + side * 22, y + 22), (x + side * 26, y + 8)], (255, 150, 60), "f", a)
            f.ellipse(x - side * 22, y + 4, 12, 6, (255, 220, 170), "f", a)
        elif what == "hawk":
            x = -40 + q * 230
            y = 160 - 120 * math.sin(q * math.pi * (i + 1) * 0.5)
            f.ellipse(x, y, 18, 8, (150, 240, 180), "f", a * 0.85)
            f.poly([(x - 6, y + 4), (x + 4, y + 26 * math.cos(q * 20)), (x + 10, y + 4)], (190, 255, 210), "f", a * 0.85)
            f.poly([(x + 18, y), (x + 26, y - 3), (x + 18, y - 5)], (255, 210, 90), "f", a)
        elif what == "clone":
            f.ghost(-60 + i * 60 + q * 30, 10 * math.sin(q * 6 + i), a * 0.8, (35, 30, 60))
            if q > 0.6:
                f.line([(40 + i * 40, 40), (90 + i * 40, 110)], (255, 255, 255), 4, "f", alpha=1 - q)
        else:   # spirit
            size = p.get("size", 18)
            x, y = 30 + q * 120, 80 + 20 * math.sin(q * 8)
            f.circle(x, y, size * 1.6, col, "b", a * 0.4, False)
            f.circle(x, y, size, col, "f", a)
            f.circle(x + size * 0.3, y + size * 0.2, size * 0.2, (255, 255, 255), "f", a, False)
    if p.get("dust") and k > 0.8:
        f.puff(140, 10, 20, (200, 195, 185), "f", (1 - k) * 5)


def sky(f, t, p):
    """Raise the hand: something drops from the top onto the right side with a burst. p: what, color, x."""
    col = p.get("color", (250, 210, 80))
    x = p.get("x", 110)
    f.wangle = phases(t, f.a.rest, f.a.rest + 10, 95, 90)
    f.dy = phases(t, 0, -3, 4, 2)
    what = p.get("what", "sword")
    if t < 0.3:
        return
    k = seg(t, 0.3, 0.6)
    y = 260 - 250 * ease_in(k)
    if k < 1:
        if what == "sword":
            f.poly([(x - 10, y), (x + 10, y), (x + 10, y + 120), (x, y + 140), (x - 10, y + 120)], col, "f")
            f.poly([(x - 30, y + 120), (x + 30, y + 120), (x + 30, y + 132), (x - 30, y + 132)], (200, 160, 60), "f")
        elif what == "beam":
            f.poly([(x - 30, 0), (x + 30, 0), (x + 30, 260), (x - 30, 260)], col, "b", 0.55 * k)
            f.poly([(x - 14, 0), (x + 14, 0), (x + 14, 260), (x - 14, 260)], (255, 255, 230), "f", 0.8 * k)
        elif what == "cannon":
            for i in range(3):
                q = clamp01(k * 1.5 - i * 0.25)
                f.circle(-150 + q * (x + 150 + i * 30), 200 - q * 190, 11, (40, 40, 45), "f", 1 if q < 1 else 0)
        elif what == "spirit":
            f.circle(x, y + 50, 60, col, "b", 0.4, False)
            f.circle(x, y + 50, 46, col, "f")
            f.circle(x - 14, y + 58, 7, (40, 40, 60), "f")
            f.circle(x + 14, y + 58, 7, (40, 40, 60), "f")
    else:
        q = seg(t, 0.6, 1)
        if what == "sword":
            f.poly([(x - 10, 10), (x + 10, 10), (x + 10, 130), (x - 10, 130)], col, "f", 1 - q * 0.4)
        if what == "beam":
            f.poly([(x - 30 * (1 - q), 0), (x + 30 * (1 - q), 0), (x + 30 * (1 - q), 260), (x - 30 * (1 - q), 260)], col, "b", 0.6 * (1 - q))
            f.circle(x, 200, 30 + 20 * q, (255, 230, 120), "f", 1 - q, ring=4)
        if what == "cannon":
            for i in range(3):
                qq = clamp01(q * 1.5 - i * 0.2)
                f.star(x - 30 + i * 30, 20 + i * 15, 24 * (0.5 + qq), (255, 150, 50), 8, "f", 1 - qq)
        else:
            f.star(x, 10, 30 * (0.6 + q), (255, 245, 170), 8, "f", 1 - q)
            f.shockwave(x, 30 + 70 * q, col, 1 - q)
    if p.get("heal") and t > 0.7:
        cast_sparkles(f, seg(t, 0.7, 1), col)


def cast_sparkles(f, k, col):
    for j in range(6):
        f.sparkle(-40 + j * 16, 140 - k * 110 + j * 5, 6, col, "f", 1 - k)


def beam(f, t, p):
    """A stream across the right side: runes / fire wave / pages."""
    col = p.get("color", f.a.color)
    f.wangle = phases(t, f.a.rest, f.a.rest + 10, 5, 5)
    f.hand[0] = phases(t, 0, -10, 10, 8)
    if t < PHA:
        if p.get("burst"):
            f.star(40, 80, 30 * t / PHA, col, 8, "f", 1)
        return
    k = seg(t, PHA, 1)
    kind = p.get("kind", "runes")
    if kind == "wave":
        x = 30 + 200 * k
        f.poly([(x - 60, 0), (x, 0), (x + 20, 70), (x - 10, 140), (x - 40, 90)], col, "f", 1 - k * 0.5)
        f.poly([(x - 50, 0), (x - 10, 0), (x + 4, 55), (x - 22, 95)], (255, 220, 90), "f", 1 - k * 0.5)
    else:
        L = 200 * ease_out(min(1, k * 2))
        f.poly([(40, 60), (40 + L, 55), (40 + L, 95), (40, 90)], col, "b", 0.5 * (1 - max(0, k - 0.7) * 3))
        for j in range(10):
            q = (k * 3 + j / 10.0) % 1
            f.diamond(40 + q * L, 75 + 12 * math.sin(j * 1.7), 6, (230, 240, 255), "f", 1 - q)


def transform_bear(f, t, p):
    k = seg(t, 0, 0.45)
    for j in range(8):
        a = j * 45 + k * 300
        f.ellipse(math.cos(math.radians(a)) * 60, 80 + math.sin(math.radians(a)) * 60, 9, 5, (110, 170, 70), "f", 1 - seg(t, 0.4, 0.55))
    if t > 0.45:
        q = seg(t, 0.45, 1)
        f.tint = (120, 80, 50, 0.7)
        f.scale_extra = 1.25
        f.circle(-26, 205, 12, (110, 75, 50), "b")
        f.circle(26, 205, 12, (110, 75, 50), "b")
        f.weapon = False
        if 0.2 < q < 0.6:
            hx = 60
            f.arc(hx, 90, 55, 120, -40, (255, 255, 255), 16, "f", 1 - seg(q, 0.4, 0.6))
            f.line([(hx - 10, 120), (hx + 20, 70)], (255, 255, 255), 4, "f")


def moon(f, t, p):
    """Big moon behind, still pose, one flash, giant crescent slash."""
    col = p.get("color", (240, 240, 255))
    mc = p.get("moon", (240, 240, 210))
    a = ease_out(seg(t, 0, 0.3))
    f.circle(-70, 170, 60, mc, "b", 0.9 * a)
    if p.get("dark"):
        f.circle(-70, 170, 52, (20, 15, 30), "b", 0.9 * a, False)
    f.wangle = f.a.rest if t < 0.45 else (-20 if t < 0.7 else lerp(-20, f.a.rest, seg(t, 0.7, 1)))
    if 0.45 <= t < 0.52:
        f.flash = 1.0
    if t >= 0.5:
        q = seg(t, 0.5, 0.85)
        f.arc(60, 70, 120, 140, -40, col, 34, "f", 1 - q)
        f.line([(-150, 70 - 40 * q), (190, 110 + 30 * q)], (255, 255, 255), 3, "f", alpha=1 - q)

# ------------------------------------------------------------------------------------------------ sequencing


def seq(*parts):
    """Run sub-templates one after another; parts = (template, params, fraction)."""
    total = sum(fr for _, _, fr in parts)

    def run_seq(f, t, p):
        acc = 0.0
        for fn, prm, fr in parts:
            span = fr / total
            if t <= acc + span or (fn, prm, fr) == parts[-1]:
                fn(f, clamp01((t - acc) / span), prm)
                return
            acc += span
    return run_seq


def tmpl(fn, **params):
    return lambda f, t, p: fn(f, t, params)


# ------------------------------------------------------------------------------------------------ move table (from the animation brief)

# ------------------------------------------------------------------------------------------------ generic attack templates


def sweep(f, t, p):
    """Low sweep: crouch, the weapon arcs along the ground from behind to the front, dust at the strike."""
    sub = dict(p)
    sub.setdefault("frm", -165)
    sub.setdefault("to", -10)
    sub.setdefault("lunge", 14)
    sub.setdefault("radius", 78)
    slash(f, t, sub)
    k = math.sin(clamp01(t) * math.pi)
    f.sy *= 1 - 0.14 * k
    f.sx *= 1 + 0.08 * k
    f.dy = -8 * k
    if PHA <= t < PHF:
        f.puff(70, 5, 10, (215, 205, 190), "b", 1 - seg(t, PHA, PHF))


def rising(f, t, p):
    """Uppercut / launcher: low wind-up, rising slash with a hop, upward speed lines and an impact star."""
    sub = dict(p)
    sub["up"] = True
    sub.setdefault("lunge", 16)
    slash(f, t, sub)
    f.dy = phases(t, 0, -8, p.get("hop", 40), p.get("hop", 40) * 0.7)
    k = strike_k(t)
    if k > 0:
        for i in range(3):
            x = 30 + i * 22
            f.line([(x, 40 + 60 * k), (x, 90 + 60 * k)], (255, 255, 255), 2, "b", False, 0.6 * k)


def dive(f, t, p):
    """Air-to-ground plunge: hang at the top, weapon pointing down, drop with speed lines, impact burst."""
    col = p.get("color", f.a.color)
    if t < PHA:
        k = ease_out(t / PHA)
        f.dy = 40 + 8 * k
        f.rot = 10 * k
        f.wangle = lerp(f.a.rest, 120, k)
        f.sy = 1 - 0.08 * k
    elif t < 0.6:
        k = seg(t, PHA, 0.6)
        f.dy = 48 * (1 - ease_in(k))
        f.rot = lerp(10, -25, k)
        f.wangle = lerp(120, -75, ease_in(k))
        f.sy, f.sx = 1.08, 0.94
        oy = f.off()[1]
        for i in range(4):
            x = -30 + i * 22
            f.line([(x, oy + 150), (x, oy + 200)], (255, 255, 255), 2, "b", False, 0.7)
    else:
        k = seg(t, 0.6, 1)
        f.wangle = lerp(-75, f.a.rest, ease_io(k))
        f.rot = lerp(-25, 0, ease_io(k))
        f.sy = 1 - 0.15 * (1 - k)
        f.sx = 1 + 0.1 * (1 - k)
        f.star(50, 8, 30 * (1 - k * 0.5), col, 8, "f", 1 - k)
        f.shockwave(40, 20 + 90 * ease_out(k), col, 1 - k)


def guard(f, t, p):
    """Raise the weapon across the body behind a shimmering barrier; the last frame is held while guarding."""
    col = p.get("color", f.a.color)
    k = ease_out(clamp01(t / 0.6))
    f.wangle = lerp(f.a.rest, 95, k)
    f.hand[0] = 10 * k
    f.sy, f.sx, f.rot = 1 - 0.06 * k, 1 + 0.04 * k, 4 * k
    f.ellipse(40, 85, 26 + 10 * k, 1 + 95 * k, col, "f", 0.22 * k)
    f.ellipse(40, 85, 26 + 10 * k, 1 + 95 * k, col, "f", 0.85 * k, ring=3)


def rush(f, t, p):
    """Charging strike: blur forward with a coloured trail, finish with a slash."""
    seq((dash, {"trail": True, "color": p.get("color", f.a.color)}, 2), (slash, {"color": p.get("color", f.a.color), "lunge": 10}, 1))(f, t, p)


# ------------------------------------------------------------------------------------------------ clip table

WHITE = (250, 250, 255)

FRAMES = {   # must match SpriteAnimLibrary.DefaultFrames
    "Idle": 8, "Run": 8, "Jump": 8, "Hit": 6, "KO": 10, "Victory": 8, "Skill1": 14, "Skill2": 14, "Ultimate": 28,
    "Jab": 8, "Swing": 10, "Upswing": 10, "LowSweep": 10, "Thrust": 10, "Slam": 12, "Spin": 12, "Shoot": 10,
    "Cast": 12, "Dive": 10, "Uppercut": 10, "Guard": 6, "Rush": 10, "Launch": 10, "Dash": 8,
}
LOOPS = {"Idle", "Run", "Victory"}

# per character: projectile shape for Shoot, cast effect, and the three class moves (Skill1 / Skill2 / Ultimate)
STYLE = {"Kaia": ("crescent", "aura"), "Bruna": ("wave", "aura"), "Ren": ("arrow", "ring"), "Lyra": ("diamond", "sigil"),
         "Taro": ("crescent", "ring")}


def class_moves(hero, col, acc):
    if hero == "Kaia":       # Dash & Thrust / Parry / Sword Aura (crescent slashes, afterimages: sheet VFX)
        return (seq((dash, {"trail": True, "color": col}, 2), (thrust, {"color": col, "lunge": 30}, 1)),
                seq((cast, {"effect": "barrier", "color": col, "raise": 20}, 1), (slash, {"color": WHITE}, 1)),
                seq((cast, {"effect": "aura", "color": col}, 1), (combo, {"n": 4, "dirs": ["down", "up", "down", "up"], "color": col}, 2),
                    (slash, {"color": col, "radius": 95, "width": 30, "impact": (90, 80)}, 1)))
    if hero == "Bruna":      # Grab / Tornado Spin / Smash (ground-slam shockwave)
        return (seq((thrust, {"lunge": 30, "length": 60, "width": 8, "color": acc}, 1), (slam, {"color": acc}, 1)),
                tmpl(spin, color=acc),
                seq((jump, {}, 1), (slam, {"color": col, "ring": True, "cracks": True}, 2)))
    if hero == "Ren":        # Backstep Shot / charged shot / Arrow Barrage (rapid multi-shot)
        return (tmpl(shoot, shape="arrow", hop=True, trail=col),
                seq((cast, {"effect": "ring", "color": col, "raise": 80, "at": (60, 85)}, 1), (shoot, {"shape": "arrow", "trail": col, "pop": True, "color": col}, 1)),
                seq((cast, {"effect": "mark", "color": col, "raise": 80}, 1), (shoot, {"shape": "arrow", "count": 7, "spread": 4, "trail": col}, 2)))
    if hero == "Lyra":       # Magic Projectile / Teleport / Protective Barrier
        return (tmpl(shoot, shape="diamond", color=col, pop=True),
                tmpl(blink, leave="sigil", color=acc, offset=70),
                seq((cast, {"effect": "glyph", "color": col}, 1), (cast, {"effect": "dome", "color": col}, 2), (cast, {"effect": "ring", "color": acc}, 1)))
    if hero == "Taro":       # Thrust Flash / Spiral Spin / Sweeping Slash finisher
        return (tmpl(thrust, lunge=40, length=200, color=col),
                tmpl(spin, color=col),
                seq((spin, {"color": col}, 1), (slash, {"frm": 150, "to": -70, "radius": 100, "width": 30, "color": col}, 1),
                    (thrust, {"length": 200, "color": col}, 1)))
    raise KeyError(hero)


def clip_table(a):
    """name -> (template fn(frame, t, p), frames) for one character."""
    col, acc, kind = a.color, a.accent, a.kind
    shape, effect = STYLE[a.hero]
    s1, s2, ult = class_moves(a.hero, col, acc)
    big = kind in ("Heavy",)
    t = {
        "Idle": idle, "Run": run, "Jump": jump, "Hit": hit, "KO": ko, "Victory": victory,
        "Skill1": s1, "Skill2": s2, "Ultimate": ult,
        "Jab": tmpl(thrust, lunge=14, length=70, width=4, color=WHITE),
        "Swing": tmpl(slash, color=col, frm=150 if big else 115, to=-70 if big else -55, width=28 if big else 22),
        "Upswing": tmpl(slash, color=col, up=True),
        "LowSweep": tmpl(sweep, color=col),
        "Thrust": tmpl(thrust, lunge=40, length=170 if kind == "Polearm" else 130, color=col),
        "Slam": tmpl(slam, color=col, ring=big, cracks=big),
        "Spin": tmpl(spin, color=col),
        "Shoot": tmpl(shoot, shape=shape, color=col, trail=col if shape == "arrow" else None),
        "Cast": tmpl(cast, effect=effect, color=col, raise_=80 if kind in ("Magic", "Ranged") else 60),
        "Dive": tmpl(dive, color=col),
        "Uppercut": tmpl(rising, color=col, hop=40, impact=(50, 170)),
        "Guard": tmpl(guard, color=col),
        "Rush": tmpl(rush, color=col),
        "Launch": tmpl(rising, color=acc, hop=12, impact=(70, 150), radius=85, width=26),
        "Dash": tmpl(dash, color=col),
    }
    return {k: (fn, FRAMES[k]) for k, fn in t.items()}


def render_clip(assets, fn, frames, loop):
    out = Image.new("RGBA", (W * frames, H), (0, 0, 0, 0))
    for i in range(frames):
        t = i / frames if loop else i / max(1, frames - 1)
        fr = Frame(assets)
        fn(fr, t, {})
        out.alpha_composite(fr.render(), (i * W, 0))
    return out


def save(img, path):
    if in_assets(path):
        write_meta(path, "anim")             # meta first: the open editor must never import with default settings
    img.save(path, optimize=True)


def build(hero, only=None):
    a = Assets(hero)
    d = os.path.join(OUT, hero)
    os.makedirs(d, exist_ok=True)
    if in_assets(d):
        write_folder_meta(OUT)
        write_folder_meta(d)
    for name, (fn, n) in clip_table(a).items():
        if only and name not in only:
            continue
        save(render_clip(a, fn, n, name in LOOPS), os.path.join(d, name + ".png"))


def main():
    args = sys.argv[1:]
    only = None
    if "--clips" in args:
        i = args.index("--clips")
        only = set(args[i + 1].split(","))
        args = args[:i] + args[i + 2:]
    for h in args or list(HEROES):
        build(h, only)
        print("animated", h)


if __name__ == "__main__":
    main()
