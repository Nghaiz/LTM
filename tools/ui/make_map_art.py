#!/usr/bin/env python3
"""Makes the room screens' map pictures: each map by day and in Night Mode.

    python tools/ui/make_map_art.py <renders-folder>

The renders are taken in the Editor, in Play mode, on a practice round of each map: a camera
over the flags' centre looking across the battlefield (1920 x 1080, 8x MSAA), saved as
``<Scene>_day.png`` and ``<Scene>_night.png``. A night render is taken with the fog thinned
for the shot, since Night Mode's own fog hides everything past ~150 m, and comes with
``<Scene>_night_glows.csv``: every pumpkin and lamp the camera can see, one ``x,y,depth`` row
in pixels (y down) and metres.

This grades them for the menu (the probe camera has no post-processing), paints the candles
and lamps the game would light (it lights only the sixteen nearest the player), and writes
``Ironfront_Reborn/Assets/UI/IronfrontReborn/maps/<scene>-day.png`` and ``-night.png`` at
1600 x 900. Owner's run of 2026-10-10, task 4: the map and the mode on the create-room and
practice screens instead of one placeholder.
"""
import csv
import math
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "Ironfront_Reborn", "Assets", "UI", "IronfrontReborn", "maps")
SIZE = (1600, 900)
SCENES = ["Dustbowl", "Island", "ForestLake"]


def to_array(image):
    return np.asarray(image.convert("RGB"), dtype=np.float32) / 255.0


def to_image(pixels):
    return Image.fromarray((np.clip(pixels, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8), "RGB")


def s_curve(pixels, strength):
    """Contrast round mid-grey: a smoothstep blend, so blacks and whites are not clipped."""
    smooth = pixels * pixels * (3.0 - 2.0 * pixels)
    return pixels + (smooth - pixels) * strength


def saturate(pixels, amount):
    grey = pixels @ np.array([0.299, 0.587, 0.114], dtype=np.float32)
    return grey[..., None] + (pixels - grey[..., None]) * amount


def vignette(pixels, strength):
    h, w = pixels.shape[:2]
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    d = np.sqrt(((x - w / 2) / (w / 2)) ** 2 + ((y - h / 2) / (h / 2)) ** 2) / math.sqrt(2.0)
    return pixels * (1.0 - strength * d[..., None] ** 2.2)


def grade_day(pixels):
    # The probe's distance haze, taken back a little: the in-game grade does the same.
    pixels = np.clip((pixels - 0.07) / 0.93, 0.0, 1.0)
    pixels = s_curve(pixels, 0.4)
    pixels = saturate(pixels, 1.18)
    # A touch warmer in the light, as the game's colour grade is.
    pixels = pixels * np.array([1.03, 1.0, 0.96], dtype=np.float32)
    return vignette(pixels, 0.45)


def grade_night(pixels):
    # Lift the moonlit ground out of the black the probe left it in, and cool it.
    pixels = 1.0 - np.power(1.0 - np.clip(pixels * 2.4, 0.0, 1.0), 1.6)
    pixels = pixels * np.array([0.78, 0.9, 1.12], dtype=np.float32)
    pixels = s_curve(np.clip(pixels, 0.0, 1.0), 0.25)
    return vignette(pixels, 0.6)


def paint_glows(pixels, glows):
    """Warm candle and lamp light, nearer ones bigger, added over the graded night."""
    h, w = pixels.shape[:2]
    light = np.zeros((h, w), dtype=np.float32)
    core = np.zeros((h, w), dtype=np.float32)
    for x, y, depth in glows:
        radius = max(1.6, min(12.0, 2600.0 / max(depth, 1.0)))
        x0, x1 = int(max(0, x - radius * 3)), int(min(w, x + radius * 3 + 1))
        y0, y1 = int(max(0, y - radius * 3)), int(min(h, y + radius * 3 + 1))
        if x1 <= x0 or y1 <= y0:
            continue
        yy, xx = np.mgrid[y0:y1, x0:x1].astype(np.float32)
        d2 = ((xx - x) ** 2 + (yy - y) ** 2) / (radius * radius)
        light[y0:y1, x0:x1] += np.exp(-d2 * 1.1) * 0.32
        core[y0:y1, x0:x1] += np.exp(-d2 * 9.0) * 1.0
    warm = np.array([1.0, 0.56, 0.18], dtype=np.float32)
    hot = np.array([1.0, 0.86, 0.55], dtype=np.float32)
    pixels = pixels + light[..., None] * warm + np.clip(core, 0.0, 1.0)[..., None] * hot
    return pixels


def load_glows(path):
    if not os.path.exists(path):
        return []
    with open(path, newline="", encoding="utf-8") as handle:
        return [(float(r[0]), float(r[1]), float(r[2])) for r in csv.reader(handle) if len(r) == 3]


def main(renders):
    os.makedirs(OUT, exist_ok=True)
    for scene in SCENES:
        for mode in ("day", "night"):
            source = os.path.join(renders, f"{scene}_{mode}.png")
            if not os.path.exists(source):
                raise SystemExit(f"missing render: {source}")
            pixels = to_array(Image.open(source))
            if mode == "day":
                pixels = grade_day(pixels)
            else:
                pixels = paint_glows(grade_night(pixels), load_glows(os.path.join(renders, f"{scene}_night_glows.csv")))
            image = to_image(pixels).resize(SIZE, Image.LANCZOS).filter(ImageFilter.UnsharpMask(radius=1.2, percent=60, threshold=2))
            target = os.path.join(OUT, f"{scene.lower()}-{mode}.png")
            image.save(target, optimize=True)
            print("wrote", os.path.relpath(target, ROOT))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    main(sys.argv[1])
