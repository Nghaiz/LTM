#!/usr/bin/env python3
"""Renders the game's UI icons and achievement badges from tools/ui/glyphs.py and badges.py.

    python tools/ui/make_icons.py            # every icon and badge
    python tools/ui/make_icons.py --check    # fail if a rendered PNG is missing

UI icons are white glyphs on transparency (tinted in Unity), 128 px, into
``Ironfront_Reborn/Assets/Resources/IronfrontUi/Icons``. Achievement badges are full-colour
256 px emblems into ``.../IronfrontUi/Achievements``, one per entry of ACHIEVEMENT_ART in
badges.py, plus the hidden-achievement badge.

SVG goes through ImageMagick's librsvg delegate (``magick``), which is on the build PC; the PNGs
are committed, so nobody else needs it. Re-run after editing a glyph or a badge.
"""
import os
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from glyphs import GLYPHS  # noqa: E402
import badges  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ICONS = os.path.join(ROOT, "Ironfront_Reborn", "Assets", "Resources", "IronfrontUi", "Icons")
BADGES = os.path.join(ROOT, "Ironfront_Reborn", "Assets", "Resources", "IronfrontUi", "Achievements")


def render(svg, out_png, size):
    """Writes ``svg`` to a temporary file and rasterises it to ``out_png`` at ``size`` px."""
    with tempfile.NamedTemporaryFile("w", suffix=".svg", delete=False, encoding="utf-8") as f:
        f.write(svg)
        tmp = f.name
    try:
        # A higher density than the target, then a box-filtered resize: rsvg's own anti-aliasing at
        # 128 px leaves stair-steps on the curves.
        subprocess.run(["magick", "-background", "none", "-density", "384", tmp,
                        "-filter", "Lanczos", "-resize", f"{size}x{size}", "-define",
                        "png:color-type=6", out_png], check=True)
    finally:
        os.unlink(tmp)


def icon_svg(name):
    return ('<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100" '
            f'color="#FFFFFF">{GLYPHS[name]}</svg>')


def main():
    check = "--check" in sys.argv
    os.makedirs(ICONS, exist_ok=True)
    os.makedirs(BADGES, exist_ok=True)
    missing = []

    for name in sorted(GLYPHS):
        out = os.path.join(ICONS, name + ".png")
        if check:
            if not os.path.exists(out):
                missing.append(out)
            continue
        render(icon_svg(name), out, 128)

    for key, svg in badges.all_badges():
        out = os.path.join(BADGES, key + ".png")
        if check:
            if not os.path.exists(out):
                missing.append(out)
            continue
        render(svg, out, 256)

    if missing:
        print("missing:\n  " + "\n  ".join(missing))
        sys.exit(1)
    print(f"{len(GLYPHS)} icons, {len(list(badges.all_badges()))} badges" + (" present" if check else " rendered"))


if __name__ == "__main__":
    main()
