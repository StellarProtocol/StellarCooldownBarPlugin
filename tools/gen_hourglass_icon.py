"""Generate the Cooldown Bar header glyph: a white outlined hourglass with sand -> Resources/hourglass-<N>.png.

WHITE on transparent: it sits on the live theme-accent icon tile (art can't follow the theme, so the colour lives in the
tile). One pre-drawn picture per size (house rule: never scale one master down). Geometry is the approved prototype's
24-unit SVG (cooldownbar-redesign.html): the outline path
    M6 3h12v2c0 3-2.5 5-4.5 7 2 2 4.5 4 4.5 7v2H6v-2c0-3 2.5-5 4.5-7C8.5 10 6 8 6 5z
stroked 2 units wide, plus the filled sand triangle M9 19h6l-3-3z.
Hinting: the stroke is a whole number of pixels at every size, and the straight edges (the caps' x = 6 / 18 sides and
the y = 3 / 5 / 19 / 21 rows) are placed so the stroke's EDGES land on pixel boundaries (even stroke -> centre on a
grid line, odd stroke -> centre on a pixel centre). Only the curved waist and the sand's slopes are anti-aliased
(8x supersample, box-filtered down). The window header draws the 24 px picture (40 px tile, 8 px all round); the other
sizes are the house set.

Run (conda python, from the plugin root):  python tools/gen_hourglass_icon.py
"""
import math
import os
from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 64, 128]
SS = 8
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Resources")


def snap(u, s, w):
    """Unit coordinate u -> pixel-space stroke centre whose stroke edges sit on pixel boundaries."""
    c = u * s
    return float(round(c)) if w % 2 == 0 else math.floor(c) + 0.5


def bezier(p0, p1, p2, p3, steps=24):
    pts = []
    for i in range(1, steps + 1):
        t = i / steps
        mt = 1 - t
        x = mt ** 3 * p0[0] + 3 * mt * mt * t * p1[0] + 3 * mt * t * t * p2[0] + t ** 3 * p3[0]
        y = mt ** 3 * p0[1] + 3 * mt * mt * t * p1[1] + 3 * mt * t * t * p2[1] + t ** 3 * p3[1]
        pts.append((x, y))
    return pts


def draw(n):
    s = n / 24.0
    w = max(1, round(2 * s))                      # stroke width in whole pixels
    xl, xr = snap(6, s, w), snap(18, s, w)        # cap sides
    yt, yts = snap(3, s, w), snap(5, s, w)        # top cap rows
    ybs, yb = snap(19, s, w), snap(21, s, w)      # bottom cap rows
    wl, wr, wy = 10.5 * s, 13.5 * s, 12 * s       # waist (anti-aliased)

    # Outline path in pixel space (the prototype's curves, scaled; anchors snapped).
    path = [(xl, yt), (xr, yt), (xr, yts)]
    path += bezier((xr, yts), (xr, 8 * s), (15.5 * s, 10 * s), (wr, wy))
    path += bezier((wr, wy), (15.5 * s, 14 * s), (xr, 16 * s), (xr, ybs))
    path += [(xr, yb), (xl, yb), (xl, ybs)]
    path += bezier((xl, ybs), (xl, 16 * s), (8.5 * s, 14 * s), (wl, wy))
    path += bezier((wl, wy), (8.5 * s, 10 * s), (xl, 8 * s), (xl, yts))
    path.append((xl, yt))

    img = Image.new("L", (n * SS, n * SS), 0)
    d = ImageDraw.Draw(img)
    # Stroke by stamping exact squares (straight runs) / disks (curves) along the path, every supersampled pixel.
    # Pillow's own wide `line` rasterises even widths one pixel lopsided (a faint extra column on the right caps).
    half = w * SS / 2
    big = [(x * SS, y * SS) for x, y in path]
    for (x0, y0), (x1, y1) in zip(big, big[1:]):
        straight = abs(x0 - x1) < 1e-6 or abs(y0 - y1) < 1e-6
        steps = max(1, int(math.hypot(x1 - x0, y1 - y0)))
        for i in range(steps + 1):
            t = i / steps
            cx, cy = x0 + (x1 - x0) * t, y0 + (y1 - y0) * t
            box = [round(cx - half), round(cy - half), round(cx + half) - 1, round(cy + half) - 1]
            (d.rectangle if straight else d.ellipse)(box, fill=255)
    # Sand: base on the inner edge of the bottom cap, apex 3 units up.
    base = ybs - w / 2
    sand = [(9 * s, base), (15 * s, base), (12 * s, base - 3 * s)]
    d.polygon([(x * SS, y * SS) for x, y in sand], fill=255)

    alpha = img.resize((n, n), Image.BOX)
    out = Image.new("RGBA", (n, n), (255, 255, 255, 0))
    out.putalpha(alpha)
    return out


if __name__ == "__main__":
    for n in SIZES:
        path = os.path.join(OUT, f"hourglass-{n}.png")
        draw(n).save(path, optimize=True)
        print("wrote", os.path.normpath(path))
