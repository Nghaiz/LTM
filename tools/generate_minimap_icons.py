"""Regenerates the minimap icon textures in Ironfront_Reborn/Assets/Texture2D.

Why a generator and not hand-painted files: the original icons were additive glow line art on
an opaque black square, drawn with the additive "HUD White" material. Additive blending adds
light, so on Dustbowl's pale sand a light-blue soldier added almost nothing and read as a smudge
(owner report 2026-09-29: the map icons are too small and blurry). Every icon here is instead a
white shape with a dark outline on a transparent background, blended normally: the UI tint turns
the white into the team colour and leaves the outline dark, so the icon stays legible on sand,
grass, rock and sea alike.

The vehicle silhouettes are traced from the original line art (kept unmodified in
tools/minimap-icons/originals/) rather than redrawn, so a player who knows the original still
tells a jeep from a quad bike at a glance, and a re-run always starts from the same source.

Usage (from the repo root):  python tools/generate_minimap_icons.py
Needs Pillow. Writes in place; existing .meta files, and so every prefab reference, are untouched.
"""

from collections import deque
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
TEXTURES = ROOT / "Ironfront_Reborn" / "Assets" / "Texture2D"
ORIGINALS = ROOT / "tools" / "minimap-icons" / "originals"

SIZE = 256          # authored size; Unity mip-maps it down to the ~20-50 px it is drawn at
SUPERSAMPLE = 4     # shapes are drawn at 4x and filtered down for anti-aliased edges
OUTLINE = (12, 14, 18)

VEHICLES = ["jeep_blip", "quad_blip", "tank_blip", "boat_blip", "heli_blip"]


def _downsample(image):
    return image.resize((SIZE, SIZE), Image.LANCZOS)


def _dilate(mask, radius):
    size = radius * 2 + 1
    return mask.filter(ImageFilter.MaxFilter(size)).filter(ImageFilter.GaussianBlur(radius * 0.3))


def _compose(fill_mask, detail_mask=None, outline_px=18):
    """White fill over a dark rim; optional dark detail lines inside the fill."""
    rim = _dilate(fill_mask, outline_px).point(lambda v: 255 if v > 40 else int(v * 6.4))
    out = Image.new("RGBA", (SIZE, SIZE), OUTLINE + (0,))
    out.putalpha(rim)
    white = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 255))
    out = Image.composite(white, out, fill_mask)
    if detail_mask is not None:
        dark = Image.new("RGBA", (SIZE, SIZE), OUTLINE + (255,))
        # Darken to ~45% rather than to black, so the team tint still shows through the lines.
        detail = ImageChops.multiply(detail_mask.point(lambda v: int(v * 0.55)), fill_mask)
        out = Image.composite(dark, out, detail)
    return out


def arrow_mask():
    """The soldier icon: a chevron pointing the way the soldier faces (up = the icon's heading)."""
    big = SIZE * SUPERSAMPLE
    img = Image.new("L", (big, big), 0)
    draw = ImageDraw.Draw(img)

    def p(x, y):
        return (x * big, y * big)

    draw.polygon([p(0.5, 0.10), p(0.83, 0.86), p(0.5, 0.67), p(0.17, 0.86)], fill=255)
    return _downsample(img)


def round_dot():
    big = SIZE * SUPERSAMPLE
    img = Image.new("L", (big, big), 0)
    r = big * 0.40
    ImageDraw.Draw(img).ellipse([big / 2 - r, big / 2 - r, big / 2 + r, big / 2 + r], fill=255)
    mask = _downsample(img).filter(ImageFilter.GaussianBlur(6))
    out = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
    out.putalpha(mask)
    return out


def halo():
    """A soft ring, tinted with the team colour, pulsing behind the player's own arrow."""
    big = SIZE * SUPERSAMPLE
    img = Image.new("L", (big, big), 0)
    draw = ImageDraw.Draw(img)
    outer, inner = big * 0.47, big * 0.33
    draw.ellipse([big / 2 - outer, big / 2 - outer, big / 2 + outer, big / 2 + outer], fill=255)
    draw.ellipse([big / 2 - inner, big / 2 - inner, big / 2 + inner, big / 2 + inner], fill=0)
    ring = _downsample(img).filter(ImageFilter.GaussianBlur(4))
    out = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
    out.putalpha(ring)
    return out


def trace_vehicle(path):
    """Silhouette and interior detail traced from one original glow line-art blip."""
    src = Image.open(path).convert("L")
    w, h = src.size
    lines = src.point(lambda v: 255 if v > 70 else 0)
    px = lines.load()
    outside = [[False] * w for _ in range(h)]
    queue = deque()
    for x in range(w):
        for y in range(h):
            on_edge = x in (0, w - 1) or y in (0, h - 1)
            if on_edge and px[x, y] == 0:
                outside[y][x] = True
                queue.append((x, y))
    while queue:
        x, y = queue.popleft()
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if 0 <= nx < w and 0 <= ny < h and not outside[ny][nx] and px[nx, ny] == 0:
                outside[ny][nx] = True
                queue.append((nx, ny))
    silhouette = Image.new("L", (w, h), 0)
    sp = silhouette.load()
    for y in range(h):
        for x in range(w):
            if not outside[y][x]:
                sp[x, y] = 255
    silhouette = silhouette.resize((SIZE, SIZE), Image.BICUBIC).filter(ImageFilter.GaussianBlur(1.2))
    silhouette = silhouette.point(lambda v: 0 if v < 60 else (255 if v > 190 else int((v - 60) * 255 / 130)))
    # Only the cores of the original glow lines; the glow halo around them would darken the
    # whole body and bury the team colour.
    detail = src.resize((SIZE, SIZE), Image.BICUBIC)
    detail = detail.point(lambda v: 255 if v > 185 else 0).filter(ImageFilter.GaussianBlur(1.0))
    # The outer boundary is the rim now; only the lines well inside the body stay as detail.
    detail = ImageChops.multiply(detail, silhouette.filter(ImageFilter.MinFilter(17)))
    return silhouette, detail


def main():
    _compose(arrow_mask()).save(TEXTURES / "character_blip.png")
    _compose(arrow_mask(), outline_px=24).save(TEXTURES / "minimap_self_blip.png")
    halo().save(TEXTURES / "minimap_halo.png")
    round_dot().save(TEXTURES / "minimap_trail_dot.png")
    for name in VEHICLES:
        silhouette, detail = trace_vehicle(ORIGINALS / (name + ".png"))
        _compose(silhouette, detail, outline_px=14).save(TEXTURES / (name + ".png"))
    print("wrote character_blip, minimap_self_blip, minimap_halo, minimap_trail_dot, " + ", ".join(VEHICLES))


if __name__ == "__main__":
    main()
