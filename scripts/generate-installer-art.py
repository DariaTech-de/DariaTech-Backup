#!/usr/bin/env python3
"""Render the Windows installer wizard artwork from branding/product.json and the official logo mark.

Outputs (committed, so Windows builds need no Python/Pillow):
  branding/installer-wizard-{100,150,200}.png  side panel on the welcome and finish pages
  branding/installer-small-{100,150,200}.png   header badge on the inner wizard pages
Requires Pillow. Run after changing the palette, product name or logo.
"""
import json, pathlib
from PIL import Image, ImageDraw, ImageFont

root = pathlib.Path(__file__).resolve().parents[1]
brand = json.loads((root / "branding/product.json").read_text())
palette = brand["palette"]
font_file = root / "DariaTech/Console/wwwroot/fonts/geist-latin-wght-normal.woff2"

def rgb(value, alpha=255):
    value = value.lstrip("#")
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4)) + (alpha,)

DEEP = palette["sidebar"]
ACCENT = palette["logoAccent"]
TEXT = palette["sidebarText"]

# Official brand-mark geometry from branding/logo.svg (viewBox 11.215 1.256 4.2 4.2, scaled by 0.76525).
MARK = [
    [(14.1266, -0.3000), (13.6096, -0.8170), (11.2833, 1.5094), (13.6096, 3.8358), (14.1266, 3.3188), (12.3172, 1.5094)],
    [(14.6436, 0.2169), (15.9360, 1.5094), (14.6436, 2.8018), (13.3511, 1.5094)],
]

def mark(draw, cx, cy, size, fill):
    s = 0.7652503081223757
    pts = [[(x * s + 2.89993, y * s + 2.20076) for x, y in poly] for poly in MARK]
    xs = [x for p in pts for x, _ in p]; ys = [y for p in pts for _, y in p]
    scale = size / max(max(xs) - min(xs), max(ys) - min(ys))
    ox = cx - (max(xs) + min(xs)) / 2 * scale; oy = cy - (max(ys) + min(ys)) / 2 * scale
    for poly in pts:
        draw.polygon([(ox + x * scale, oy + y * scale) for x, y in poly], fill=fill)

def font(px, weight):
    f = ImageFont.truetype(str(font_file), px)
    try: f.set_variation_by_axes([weight])
    except OSError: pass
    return f

def wizard(scale):
    w, h, ss = round(164 * scale), round(314 * scale), 4  # supersample for smooth curves
    img = Image.new("RGBA", (w * ss, h * ss), rgb(DEEP))
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0)); d = ImageDraw.Draw(layer)
    cx, cy = w * ss * 0.5, h * ss * 0.78
    for r, a in ((0.55, 70), (0.85, 55), (1.15, 40), (1.45, 28)):
        rr = w * ss * r
        d.ellipse((cx - rr, cy - rr, cx + rr, cy + rr), outline=rgb(ACCENT, a), width=max(2, round(scale * ss * 0.8)))
    d.ellipse((cx + w * ss * 0.85 - 3 * ss * scale, cy - 3 * ss * scale, cx + w * ss * 0.85 + 3 * ss * scale, cy + 3 * ss * scale), fill=rgb(ACCENT, 200))
    img = Image.alpha_composite(img, layer)
    d = ImageDraw.Draw(img)
    tile, tx, ty = 44 * scale * ss, 22 * scale * ss, 28 * scale * ss
    d.rounded_rectangle((tx, ty, tx + tile, ty + tile), radius=12 * scale * ss, fill=rgb("#0f3d32"), outline=rgb(ACCENT, 110), width=max(1, round(scale * ss)))
    mark(d, tx + tile / 2, ty + tile / 2, tile * 0.62, rgb(ACCENT))
    img = img.resize((w, h), Image.LANCZOS)
    d = ImageDraw.Draw(img)
    name = brand["productName"].split(" ", 1)
    y = round(92 * scale)
    d.text((round(22 * scale), y), name[0], font=font(round(19 * scale), 650), fill=rgb("#ffffff"))
    if len(name) > 1:
        d.text((round(22 * scale), y + round(23 * scale)), name[1], font=font(round(19 * scale), 650), fill=rgb(ACCENT))
    d.text((round(22 * scale), y + round(56 * scale)), "Verschlüsselt sichern.\nSicher wiederherstellen.", font=font(round(10.5 * scale), 420), fill=rgb(TEXT, 230), spacing=round(4 * scale))
    return img.convert("RGB")

def small(scale):
    n, ss = round(55 * scale), 4
    img = Image.new("RGBA", (n * ss, n * ss), (0, 0, 0, 0)); d = ImageDraw.Draw(img)
    d.rounded_rectangle((0, 0, n * ss - 1, n * ss - 1), radius=14 * scale * ss, fill=rgb(DEEP))
    mark(d, n * ss / 2, n * ss / 2, n * ss * 0.56, rgb(ACCENT))
    return img.resize((n, n), Image.LANCZOS)

for pct, scale in ((100, 1.0), (150, 1.5), (200, 2.0)):
    wizard(scale).save(root / f"branding/installer-wizard-{pct}.png", optimize=True)
    small(scale).save(root / f"branding/installer-small-{pct}.png", optimize=True)
print("Installer artwork written to branding/.")
