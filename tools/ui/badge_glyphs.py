"""Glyphs only an achievement badge carries, in the same 100x100 one-colour language as glyphs.py.

Owner's list of 2026-10-11, item 4: no two achievements may share a picture (MID-AIR and DOGFIGHT
both showed the wings). Thirty-three badges had borrowed another's glyph; each now has its own,
drawn here so the UI icon folder does not fill with shapes no screen uses. ``badges.py`` reads
``GLYPHS`` and these together and refuses to build when two achievements name the same glyph.
"""
from glyphs import GLYPHS, circle, path, ring, star_points


def star_path(cx, cy, outer, inner, points=5, rotate=-90):
    """A star as a closed path, so it can be cut out of a shape with the even-odd rule."""
    return "M" + " L".join(star_points(cx, cy, outer, inner, points, rotate).split(" ")) + " Z"


def stroke(d, width, extra=""):
    return (f'<path fill="none" stroke="currentColor" stroke-width="{width}" stroke-linecap="round" '
            f'stroke-linejoin="round" {extra} d="{d}"/>')


def crosshair_ticks(cx=50, cy=50, reach=48, length=13, width=6):
    """Four bars on a sight's ring, top, bottom, left and right."""
    h = width / 2
    return path(f"M{cx - h} {cy - reach} h{width} v{length} h{-width} Z "
                f"M{cx - h} {cy + reach - length} h{width} v{length} h{-width} Z "
                f"M{cx - reach} {cy - h} h{length} v{width} h{-length} Z "
                f"M{cx + reach - length} {cy - h} h{length} v{width} h{-length} Z", rule="nonzero")


def _claws():
    marks = []
    for i in range(3):
        sx, sy, ex, ey = 38 + 18 * i, 6, 16 + 18 * i, 94
        marks.append(f"M{sx} {sy} Q{40 + 18 * i} 53 {ex} {ey} Q{22 + 18 * i} 48 {sx} {sy} Z")
    return path(" ".join(marks), rule="nonzero")


def _knife():
    return ("M50 4 C56 14 56 40 55 54 H45 C44 40 44 14 50 4 Z M37 54 h26 v6 h-26 Z "
            "M45 60 h10 v26 q0 6 -5 6 q-5 0 -5 -6 Z")


def _missile():
    body = path("M50 6 Q58 16 58 28 V70 H42 V28 Q42 16 50 6 Z M42 52 L30 74 L42 70 Z M58 52 L70 74 L58 70 Z "
                "M42 70 h16 l4 8 h-24 Z", rule="nonzero")
    flame = path("M44 82 Q50 100 56 82 Z", rule="nonzero")
    return f'<g transform="rotate(38 50 50)">{body}{flame}</g>'


BADGE_GLYPHS = {
    # ---- after dark
    "claws": _claws(),
    "moon-sight": (path(ring(50, 50, 42, 6)) + crosshair_ticks()
                   + path("M48 28 A22 22 0 1 0 48 72 A27 27 0 0 1 48 28 Z")),
    "bat": path("M50 36 L44 24 L45 38 C30 34 15 29 3 30 C10 40 12 48 12 53 Q20 46 27 56 Q35 50 41 62 "
                "Q46 58 50 72 Q54 58 59 62 Q65 50 73 56 Q80 46 88 53 C88 48 90 40 97 30 C85 29 70 34 55 38 "
                "L56 24 Z", rule="nonzero"),
    "owl": path("M14 40 L16 6 L36 22 Q50 16 64 22 L84 6 L86 40 C86 68 70 92 50 94 C30 92 14 68 14 40 Z "
                + circle(34, 44, 13) + " " + circle(66, 44, 13) + " " + circle(34, 44, 6) + " "
                + circle(66, 44, 6) + " M45 58 L55 58 L50 68 Z"),
    "tombstone": (path("M22 90 V40 C22 20 34 8 50 8 C66 8 78 20 78 40 V90 Z "
                       "M46 22 h8 v12 h12 v8 h-12 v28 h-8 v-28 h-12 v-8 h12 Z")
                  + path("M8 90 h84 v8 h-84 Z")),
    # ---- long range
    "shell": (path("M36 10 h28 v52 h-28 Z M41 12 h3 v9 h-3 Z M48.5 12 h3 v9 h-3 Z M56 12 h3 v9 h-3 Z")
              + path("M33 64 h34 v16 h-34 Z M29 82 h42 v9 h-42 Z " + circle(83, 22, 5) + " "
                     + circle(88, 38, 4.5) + " " + circle(81, 52, 4), rule="nonzero")),
    "wind": (stroke("M8 30 H58 c10 0 16 -6 16 -13 c0 -7 -6 -11 -12 -11 c-6 0 -9 4 -9 8", 8)
             + stroke("M8 52 H82 c7 0 12 -5 12 -11", 8)
             + stroke("M8 74 H54 c9 0 14 6 14 12 c0 6 -5 10 -10 10 c-5 0 -8 -3 -8 -7", 8)),
    "trajectory": (path("M2 98 Q50 70 98 98 Z")
                   + stroke("M8 74 Q46 -14 84 62", 6, 'stroke-dasharray="9 7"')
                   + path(ring(84, 66, 10, 4)) + path(circle(84, 66, 3))),
    "pistol": path("M6 28 h76 v16 h-76 Z M8 22 h6 v6 h-6 Z M82 28 l10 -4 v14 l-10 2 Z "
                   "M56 44 h26 l8 46 h-22 Z "
                   "M38 44 h20 v16 q0 8 -8 8 h-6 q-6 0 -6 -6 Z M43 48 h10 v8 q0 4 -4 4 h-2 q-4 0 -4 -4 Z"),
    "aviator": (path("M16 60 C16 30 31 12 50 12 C69 12 84 30 84 60 Z "
                     + circle(35, 42, 12) + " " + circle(65, 42, 12) + " " + circle(35, 42, 5.5) + " "
                     + circle(65, 42, 5.5) + " M46 39 h8 v5 h-8 Z")
                + path("M16 58 h13 v24 q0 7 -6.5 7 q-6.5 0 -6.5 -7 Z M84 58 h-13 v24 q0 7 6.5 7 q6.5 0 6.5 -7 Z",
                       rule="nonzero")),
    # ---- the hunt
    "skull-target": (path(ring(50, 50, 44, 5)) + crosshair_ticks(reach=49, length=12)
                     + f'<g transform="translate(26 25) scale(0.48)">{GLYPHS["skull"]}</g>'),
    "paw": (path("M28 72 C28 56 39 48 50 48 C61 48 72 56 72 72 C72 82 63 86 50 84 C37 86 28 82 28 72 Z")
            + '<g fill="currentColor">'
              '<ellipse cx="20" cy="44" rx="9" ry="12" transform="rotate(-24 20 44)"/>'
              '<ellipse cx="38" cy="24" rx="9" ry="12" transform="rotate(-8 38 24)"/>'
              '<ellipse cx="62" cy="24" rx="9" ry="12" transform="rotate(8 62 24)"/>'
              '<ellipse cx="80" cy="44" rx="9" ry="12" transform="rotate(24 80 44)"/></g>'),
    "ten": (path(ring(50, 50, 45, 6))
            + '<g fill="currentColor">'
              '<rect x="44" y="16" width="12" height="68" transform="rotate(38 50 50)"/>'
              '<rect x="44" y="16" width="12" height="68" transform="rotate(-38 50 50)"/></g>'),
    "eye-cross": (path("M3 50 Q50 2 97 50 Q50 98 3 50 Z " + circle(50, 50, 24))
                  + path(ring(50, 50, 15, 4)) + crosshair_ticks(reach=21, length=9, width=4)),
    "crossed-knives": (f'<g transform="rotate(42 50 50)">{path(_knife(), rule="nonzero")}</g>'
                       f'<g transform="rotate(-42 50 50)">{path(_knife(), rule="nonzero")}</g>'),
    # ---- armour and air
    "tank-aa": (path("M10 74 h80 q6 0 6 6 v4 q0 10 -10 10 h-72 q-10 0 -10 -10 v-4 q0 -6 6 -6 Z "
                     "M18 62 h64 l8 12 h-80 Z M30 48 h32 l6 14 h-44 Z "
                     + circle(22, 86, 4) + " " + circle(38, 86, 4) + " " + circle(54, 86, 4) + " "
                     + circle(70, 86, 4))
                + stroke("M50 50 L78 14", 7)
                + f'<polygon fill="currentColor" points="{star_points(86, 8, 9, 3.5, 8)}"/>'),
    "ace": path("M22 6 h56 a6 6 0 0 1 6 6 v76 a6 6 0 0 1 -6 6 h-56 a6 6 0 0 1 -6 -6 v-76 a6 6 0 0 1 6 -6 Z "
                "M50 26 C44 36 30 42 30 55 C30 63 38 67 46 61 L42 74 h16 L54 61 C62 67 70 63 70 55 "
                "C70 42 56 36 50 26 Z"),
    "missile": _missile(),
    "eagle": path("M90 96 C92 72 86 48 70 34 C60 24 46 20 34 22 C26 23 18 26 12 32 C6 38 4 46 8 56 "
                  "C10 49 14 46 19 47 C24 48 27 50 31 52 C37 54 41 57 44 63 C48 72 50 84 48 96 Z "
                  + circle(36, 34, 4.5) + " M24 30 Q36 23 50 28 Q36 28 24 33 Z "
                  "M58 52 C66 60 70 72 68 86 L74 86 C76 72 72 60 64 50 Z"),
    "galea": (path("M12 30 Q50 -6 88 30 Q50 16 12 30 Z M46 20 h8 v10 h-8 Z", rule="nonzero")
              + path("M24 62 C24 38 36 28 50 28 C64 28 76 38 76 62 Z M20 60 h60 v7 h-60 Z "
                     "M24 69 h15 v15 q0 8 -8 8 h-7 Z M76 69 h-15 v15 q0 8 8 8 h7 Z", rule="nonzero")),
    "officer-cap": (path("M10 40 C10 24 30 16 50 16 C70 16 90 24 90 40 L84 48 H16 Z "
                         + star_path(50, 32, 10, 4.2))
                    + path("M16 51 h68 v11 h-68 Z") + path("M14 65 h72 Q74 84 50 84 Q26 84 14 65 Z")),
    # ---- the field
    "palm": (path("M12 94 Q50 74 88 94 Z")
             + path("M47 84 Q51 58 45 34 L51 33 Q59 58 55 84 Z", rule="nonzero")
             + path("M48 32 Q70 18 94 30 Q70 26 49 37 Z M48 32 Q26 16 6 28 Q26 24 49 37 Z "
                    "M49 32 Q62 8 82 6 Q64 18 51 37 Z M49 32 Q36 6 16 6 Q34 18 47 37 Z "
                    "M51 34 Q74 38 84 54 Q68 43 50 39 Z M47 34 Q24 38 14 54 Q30 43 50 39 Z", rule="nonzero")),
    "brush": (path("M14 12 h62 a6 6 0 0 1 6 6 v12 a6 6 0 0 1 -6 6 h-62 a6 6 0 0 1 -6 -6 v-12 a6 6 0 0 1 6 -6 Z")
              + stroke("M82 24 h8 v22 h-40 v14", 6)
              + path("M43 58 h14 v32 q0 6 -7 6 q-7 0 -7 -6 Z")),
    "checklist": path("M20 4 h44 l18 18 v74 h-62 Z M64 4 v18 h18 "
                      "M30 54 L44 68 L70 40 L77 47 L44 82 L23 61 Z"),
    "wheel": (path(ring(50, 50, 44, 10)) + path(circle(50, 50, 11))
              + path("M8 46 h34 v8 h-34 Z M58 46 h34 v8 h-34 Z M46 58 h8 v34 h-8 Z")),
    "whistle": (path(circle(38, 60, 27) + " " + circle(38, 60, 9) + " M50 34 h44 v20 h-30 Z")
                + path(ring(16, 22, 11, 4)) + path("M24 26 L40 36 L36 40 L20 30 Z", rule="nonzero")),
    "globe": (path(ring(50, 50, 44, 6))
              + '<g fill="none" stroke="currentColor" stroke-width="5">'
                '<ellipse cx="50" cy="50" rx="18" ry="41"/>'
                '<line x1="7" y1="50" x2="93" y2="50"/>'
                '<line x1="14" y1="29" x2="86" y2="29"/><line x1="14" y1="71" x2="86" y2="71"/></g>'),
    "tornado": path("M8 6 h84 a5 5 0 0 1 0 10 h-84 a5 5 0 0 1 0 -10 Z "
                    "M18 22 h68 a5 5 0 0 1 0 10 h-68 a5 5 0 0 1 0 -10 Z "
                    "M30 38 h50 a5 5 0 0 1 0 10 h-50 a5 5 0 0 1 0 -10 Z "
                    "M36 54 h34 a5 5 0 0 1 0 10 h-34 a5 5 0 0 1 0 -10 Z "
                    "M40 70 h22 a5 5 0 0 1 0 10 h-22 a5 5 0 0 1 0 -10 Z "
                    "M44 86 h12 a5 5 0 0 1 0 10 h-12 a5 5 0 0 1 0 -10 Z "
                    + circle(16, 66, 3.5) + " " + circle(84, 82, 3) + " " + circle(24, 86, 2.5), rule="nonzero"),
    "football": ('<g transform="rotate(-35 50 50)">'
                 + path("M6 50 Q50 10 94 50 Q50 90 6 50 Z M28 46 h44 v8 h-44 Z")
                 + path("M34 47 h4 v6 h-4 Z M44 47 h4 v6 h-4 Z M54 47 h4 v6 h-4 Z M64 47 h4 v6 h-4 Z")
                 + '</g>'),
    "cat": path("M14 32 L16 6 L38 22 Q50 18 62 22 L84 6 L86 32 C92 48 88 66 76 78 C66 88 34 88 24 78 "
                "C12 66 8 48 14 32 Z M27 46 Q36 37 45 46 Q36 55 27 46 Z M55 46 Q64 37 73 46 Q64 55 55 46 Z "
                "M45 60 h10 l-5 6 Z"),
    "can": (path("M24 32 h52 v56 q0 6 -26 6 q-26 0 -26 -6 Z M24 50 h52 v4 h-52 Z M24 68 h52 v4 h-52 Z")
            + '<ellipse cx="50" cy="32" rx="26" ry="7" fill="none" stroke="currentColor" stroke-width="4"/>'
            + '<ellipse cx="64" cy="14" rx="22" ry="6.5" fill="currentColor" transform="rotate(-28 64 14)"/>'),
    "ghost": path("M18 92 V44 C18 22 32 8 50 8 C68 8 82 22 82 44 V92 L71 82 L61 92 L50 82 L39 92 L29 82 Z "
                  + circle(38, 42, 7) + " " + circle(62, 42, 7) + " " + circle(50, 62, 6)),
    "compass": (path(ring(50, 50, 45, 5))
                + f'<polygon fill="currentColor" points="{star_points(50, 50, 40, 8, 4)}"/>'
                + f'<polygon fill="currentColor" points="{star_points(50, 50, 24, 5, 4, -45)}"/>'),
}
