#!/usr/bin/env python3
"""Renders the Nexus app icon into every format the app and installers need.

    python3 packaging/generate-icons.py      (requires Pillow)

Outputs (committed, so builds don't need Pillow):
  Assets/nexus.ico, Assets/nexus-256.png    window/taskbar icon + in-app logo
  packaging/icons/nexus.icns                macOS bundle icon (with Big Sur-style padding)
  packaging/icons/hicolor/<n>x<n>/nexus.png Linux freedesktop icon theme sizes
  packaging/icons/nexus.png                 512 px master (README, installers)

The mark: an "N" drawn as a small graph — four nodes joined by three links — on a
blue→indigo rounded square. A nexus is a point where connections meet.
"""
import os
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SS = 4  # supersampling factor

TOP_LEFT = (14, 165, 233)     # sky-500
BOTTOM_RIGHT = (79, 70, 229)  # indigo-600
INNER = (37, 99, 235)         # blue-600 (node centres)


def gradient(size):
    """Diagonal two-stop gradient, built from 1-px strips (no numpy needed)."""
    small = Image.new("RGB", (64, 64))
    px = small.load()
    for y in range(64):
        for x in range(64):
            t = (x + y) / 126.0
            px[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(TOP_LEFT, BOTTOM_RIGHT))
    return small.resize((size, size), Image.BICUBIC)


def render(size, body=0.92, shadow=False):
    S = size * SS
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    b = S * body
    off = (S - b) / 2
    radius = b * 0.225

    if shadow:
        sh = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ImageDraw.Draw(sh).rounded_rectangle(
            [off, off + b * 0.02, off + b, off + b * 1.02], radius, fill=(0, 0, 0, 110))
        img = Image.alpha_composite(img, sh.filter(ImageFilter.GaussianBlur(b * 0.025)))

    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([off, off, off + b, off + b], radius, fill=255)
    bg = gradient(S).convert("RGBA")
    # soft top highlight
    hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    for i in range(64):
        a = int(38 * (1 - i / 63))
        y0 = off + b * 0.5 * i / 64
        hd.rectangle([0, y0, S, y0 + b * 0.5 / 64 + 1], fill=(255, 255, 255, a))
    bg = Image.alpha_composite(bg, hl)
    img.paste(bg, (0, 0), mask)

    d = ImageDraw.Draw(img)
    def P(fx, fy):
        return (off + b * fx, off + b * fy)

    bl, tl, br, tr = P(0.30, 0.72), P(0.30, 0.28), P(0.70, 0.72), P(0.70, 0.28)
    # thicker strokes at tiny sizes so the mark survives 16 px
    k = 1.35 if size <= 24 else (1.15 if size <= 48 else 1.0)
    w = b * 0.085 * k
    for p, q in ((bl, tl), (tl, br), (br, tr)):
        d.line([p, q], fill=(255, 255, 255, 255), width=round(w))
    r = b * 0.095 * k
    for (x, y) in (bl, tl, br, tr):
        d.ellipse([x - r, y - r, x + r, y + r], fill=(255, 255, 255, 255))
        if size > 24:
            ri = r * 0.46
            d.ellipse([x - ri, y - ri, x + ri, y + ri], fill=INNER + (255,))

    return img.resize((size, size), Image.LANCZOS)


def main():
    assets = os.path.join(ROOT, "Assets")
    icons = os.path.join(ROOT, "packaging", "icons")
    os.makedirs(assets, exist_ok=True)
    os.makedirs(icons, exist_ok=True)

    ico_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [render(s) for s in ico_sizes]
    frames[-1].save(os.path.join(assets, "nexus.ico"), sizes=[(s, s) for s in ico_sizes],
                    append_images=frames[:-1])
    render(256).save(os.path.join(assets, "nexus-256.png"))
    render(512).save(os.path.join(icons, "nexus.png"))

    for s in (16, 24, 32, 48, 64, 128, 256, 512):
        d = os.path.join(icons, "hicolor", f"{s}x{s}")
        os.makedirs(d, exist_ok=True)
        render(s).save(os.path.join(d, "nexus.png"))

    # macOS: body ≈ 80% of the canvas with a drop shadow, per Apple's icon grid
    mac = render(1024, body=0.805, shadow=True)
    mac.save(os.path.join(icons, "nexus.icns"),
             append_images=[render(s, body=0.805, shadow=True) for s in (16, 32, 64, 128, 256, 512)])
    print("icons written")


if __name__ == "__main__":
    main()
