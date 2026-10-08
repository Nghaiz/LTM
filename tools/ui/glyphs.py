"""The game's own icon glyphs, as SVG fragments in a 100x100 box, drawn in one colour.

Every glyph is filled with ``currentColor`` so the same shape serves a white UI icon (tinted in
Unity) and the coloured centre of an achievement badge (badges.py). Shapes are built from
paths, circles and rects that librsvg 2.40 (ImageMagick's SVG delegate) renders exactly.

Add a glyph by adding a key; ``make_icons.py`` renders every key, and the Unity side loads them
by the same name from ``Resources/IronfrontUi/Icons``.
"""


def ring(cx, cy, r, w):
    """A ring as an even-odd path: a circle of radius r with a hole of radius r - w."""
    i = r - w
    return (f"M{cx - r} {cy} a{r} {r} 0 1 0 {2 * r} 0 a{r} {r} 0 1 0 {-2 * r} 0 Z "
            f"M{cx - i} {cy} a{i} {i} 0 1 0 {2 * i} 0 a{i} {i} 0 1 0 {-2 * i} 0 Z")


def circle(cx, cy, r):
    return f"M{cx - r} {cy} a{r} {r} 0 1 0 {2 * r} 0 a{r} {r} 0 1 0 {-2 * r} 0 Z"


def star_points(cx, cy, outer, inner, points=5, rotate=-90):
    import math
    pts = []
    for k in range(points * 2):
        r = outer if k % 2 == 0 else inner
        a = math.radians(rotate + k * 180.0 / points)
        pts.append(f"{cx + r * math.cos(a):.2f},{cy + r * math.sin(a):.2f}")
    return " ".join(pts)


def path(d, rule="evenodd"):
    return f'<path fill="currentColor" fill-rule="{rule}" d="{d}"/>'


def laurel():
    """Two branches of leaves round an open wreath, each leaf an ellipse along the arc."""
    import math
    leaves = []
    for side in (-1, 1):
        for k in range(7):
            a = math.radians(115 + k * 19)          # from the top of the branch down to its foot
            x = 50 + side * 38 * math.cos(a) * -1
            y = 52 - 38 * math.sin(a) * -1 * -1
            angle = math.degrees(a) * (-side) + (90 if side > 0 else -90)
            leaves.append(f'<ellipse cx="{x:.1f}" cy="{y:.1f}" rx="9" ry="4.2" '
                          f'transform="rotate({angle:.1f} {x:.1f} {y:.1f})"/>')
    stem = ('<path fill="none" stroke="currentColor" stroke-width="3.5" '
            'd="M44 92 C22 84 10 64 14 34 M56 92 C78 84 90 64 86 34"/>')
    return f'<g fill="currentColor">{"".join(leaves)}</g>{stem}'


GLYPHS = {
    # ---- the scoreboard's columns -------------------------------------------------------
    "skull": path(
        "M50 8 C29 8 15 23 15 43 C15 56 22 65 30 69 L30 82 Q30 90 38 90 L62 90 Q70 90 70 82 L70 69 "
        "C78 65 85 56 85 43 C85 23 71 8 50 8 Z "
        + circle(36, 46, 10) + " " + circle(64, 46, 10) + " "
        "M50 56 L43 69 L57 69 Z "
        "M41 78 h4 v12 h-4 Z M48 78 h4 v12 h-4 Z M55 78 h4 v12 h-4 Z"),
    "crosshair": path(
        ring(50, 50, 34, 7) + " M47 6 h6 v24 h-6 Z M47 70 h6 v24 h-6 Z M6 47 h24 v6 h-24 Z "
        "M70 47 h24 v6 h-24 Z " + circle(50, 50, 5)),
    "ratio": path(
        circle(27, 27, 13) + " " + circle(73, 73, 13) + " M68 14 L80 20 L32 88 L20 82 Z"),
    "headshot": path(
        "M50 14 C35 14 25 25 25 40 C25 52 31 60 38 64 L38 74 Q38 80 44 80 L56 80 Q62 80 62 74 L62 64 "
        "C69 60 75 52 75 40 C75 25 65 14 50 14 Z")
        + '<g fill="none" stroke="currentColor" stroke-width="6">'
          '<circle cx="50" cy="44" r="38" stroke-opacity="0.95"/>'
          '<path d="M50 0 v16 M50 72 v28 M0 44 h16 M84 44 h16"/></g>',
    "flame": path(
        "M50 4 C56 22 74 30 76 54 C78 76 64 94 50 94 C34 94 22 80 24 62 C25 50 32 42 36 34 "
        "C38 46 42 52 48 54 C46 38 44 20 50 4 Z "
        "M50 92 C60 92 66 82 64 72 C62 62 54 58 52 50 C48 60 40 64 40 76 C40 86 44 92 50 92 Z"),
    "crown": path(
        "M10 30 L30 52 L50 18 L70 52 L90 30 L82 78 L18 78 Z M18 84 h64 v10 h-64 Z "
        + circle(50, 13, 6) + " " + circle(10, 26, 6) + " " + circle(90, 26, 6)),
    "star": f'<polygon fill="currentColor" points="{star_points(50, 52, 46, 19)}"/>',
    "signal": path("M8 72 h16 v20 h-16 Z M32 54 h16 v38 h-16 Z M56 34 h16 v58 h-16 Z M80 12 h16 v80 h-16 Z"),
    "medal": path(
        "M24 4 L44 4 L58 40 L48 46 Z M76 4 L56 4 L42 40 L52 46 Z "
        + ring(50, 66, 28, 6) + " " + circle(50, 66, 16)),
    "person": path(circle(50, 26, 18) + " M18 92 C18 64 32 52 50 52 C68 52 82 64 82 92 Z"),
    "people": path(
        circle(36, 30, 15) + " M8 90 C8 66 20 56 36 56 C52 56 64 66 64 90 Z "
        + circle(70, 26, 12) + " M60 50 C66 47 72 47 76 48 C88 50 94 62 94 82 L68 82 C68 68 66 58 60 50 Z"),
    "bot": path(
        "M22 30 h56 q8 0 8 8 v42 q0 8 -8 8 h-56 q-8 0 -8 -8 v-42 q0 -8 8 -8 Z "
        + circle(36, 54, 8) + " " + circle(64, 54, 8) + " M36 70 h28 v6 h-28 Z "
        "M47 10 h6 v20 h-6 Z" + " " + circle(50, 10, 7)
        + " M4 48 h10 v20 h-10 Z M86 48 h10 v20 h-10 Z"),
    "flag": path("M16 6 h8 v88 h-8 Z M24 10 C40 2 54 18 70 10 C78 6 86 8 92 12 L92 56 C86 52 78 50 70 54 "
                 "C54 62 40 46 24 54 Z"),
    "trophy": path(
        "M26 8 h48 v28 C74 52 64 62 50 62 C36 62 26 52 26 36 Z "
        "M26 14 h-16 v10 C10 38 18 46 28 48 L30 40 C22 38 18 32 18 24 h8 Z "
        "M74 14 h16 v10 C90 38 82 46 72 48 L70 40 C78 38 82 32 82 24 h-8 Z "
        "M44 62 h12 v14 h-12 Z M28 80 h44 v12 h-44 Z"),
    "podium": path("M36 26 h28 v66 h-28 Z M6 48 h28 v44 h-28 Z M66 60 h28 v32 h-28 Z "
                   f'M50 4 L53 12 L62 12 L55 17 L58 25 L50 20 L42 25 L45 17 L38 12 L47 12 Z'),
    "question": path(ring(50, 50, 46, 7)
                     + " M36 38 C36 28 42 22 51 22 C61 22 67 28 67 36 C67 44 62 48 56 52 C53 54 53 56 53 62 "
                       "L46 62 C46 54 48 50 52 47 C57 44 59 42 59 37 C59 32 56 29 51 29 C46 29 43 32 43 38 Z "
                     + circle(49.5, 73, 5)),
    "book": path("M8 16 C24 12 38 14 47 22 L47 90 C38 82 24 80 8 84 Z M92 16 C76 12 62 14 53 22 L53 90 "
                 "C62 82 76 80 92 84 Z"),
    "keyboard": path(
        "M6 26 h88 q4 0 4 4 v40 q0 4 -4 4 h-88 q-4 0 -4 -4 v-40 q0 -4 4 -4 Z "
        "M12 34 h10 v9 h-10 Z M26 34 h10 v9 h-10 Z M40 34 h10 v9 h-10 Z M54 34 h10 v9 h-10 Z "
        "M68 34 h10 v9 h-10 Z M82 34 h8 v9 h-8 Z M12 47 h14 v9 h-14 Z M30 47 h10 v9 h-10 Z "
        "M44 47 h10 v9 h-10 Z M58 47 h10 v9 h-10 Z M72 47 h18 v9 h-18 Z M22 60 h56 v8 h-56 Z"),
    "gear": path(
        "M44 4 h12 l3 13 l9 4 l11 -7 l9 9 l-7 11 l4 9 l13 3 v12 l-13 3 l-4 9 l7 11 l-9 9 l-11 -7 "
        "l-9 4 l-3 13 h-12 l-3 -13 l-9 -4 l-11 7 l-9 -9 l7 -11 l-4 -9 l-13 -3 v-12 l13 -3 l4 -9 "
        "l-7 -11 l9 -9 l11 7 l9 -4 Z " + circle(50, 50, 15)),
    "monitor": path("M6 12 h88 q4 0 4 4 v52 q0 4 -4 4 h-88 q-4 0 -4 -4 v-52 q0 -4 4 -4 Z "
                    "M12 20 v44 h76 v-44 Z M42 72 h16 v12 h-16 Z M26 84 h48 v8 h-48 Z"),
    "speaker": path("M8 36 h18 L52 14 L52 86 L26 64 h-18 Z")
               + '<g fill="none" stroke="currentColor" stroke-width="7" stroke-linecap="round">'
                 '<path d="M64 34 C72 42 72 58 64 66"/><path d="M76 22 C90 38 90 62 76 78"/></g>',
    "sliders": path("M10 20 h80 v6 h-80 Z M10 47 h80 v6 h-80 Z M10 74 h80 v6 h-80 Z "
                    + circle(32, 23, 10) + " " + circle(68, 50, 10) + " " + circle(42, 77, 10), rule="nonzero"),
    "lock": path("M22 44 h56 q6 0 6 6 v38 q0 6 -6 6 h-56 q-6 0 -6 -6 v-38 q0 -6 6 -6 Z "
                 "M30 44 v-12 C30 18 38 8 50 8 C62 8 70 18 70 32 v12 h-9 v-12 C61 22 57 16 50 16 "
                 "C43 16 39 22 39 32 v12 Z " + circle(50, 64, 7) + " M47 66 h6 v14 h-6 Z"),
    "clock": path(ring(50, 50, 44, 8) + " M46 22 h8 v28 h-8 Z M46 46 h26 v8 h-26 Z"),
    # Reload a list (the ranking and the achievements, owner's list of 2026-10-09, item 4).
    "refresh": ('<path fill="none" stroke="currentColor" stroke-width="11" stroke-linecap="round" '
                'd="M70 27 A31 31 0 1 0 81 55"/>' + path("M60 12 L90 22 L72 46 Z")),
    "rifles": path(
        "M8 16 L16 8 L60 52 L66 46 L74 54 L68 60 L84 76 L94 76 L94 86 L84 86 L84 94 L76 94 L76 84 "
        "L60 68 L54 74 L46 66 L52 60 Z "
        "M92 16 L84 8 L40 52 L34 46 L26 54 L32 60 L16 76 L6 76 L6 86 L16 86 L16 94 L24 94 L24 84 "
        "L40 68 L46 74 L54 66 L48 60 Z", rule="nonzero"),
    "percent": path(circle(26, 26, 14) + " " + circle(74, 74, 14) + " M70 10 L82 16 L30 90 L18 84 Z"),
    "tank": path("M10 58 h80 q6 0 6 6 v10 q0 10 -10 10 h-72 q-10 0 -10 -10 v-10 q0 -6 6 -6 Z "
                 "M24 40 h40 l8 14 h-56 Z M60 42 h36 v6 h-36 Z "
                 + circle(20, 72, 5) + " " + circle(36, 72, 5) + " " + circle(52, 72, 5)
                 + " " + circle(68, 72, 5) + " " + circle(82, 72, 5)),
    "helicopter": path("M6 20 h88 v5 h-88 Z M47 24 h6 v10 h-6 Z "
                       "M26 34 h40 q18 0 22 18 l2 6 h-64 q-14 0 -14 -12 q0 -12 14 -12 Z "
                       "M10 40 L4 30 h6 l8 10 Z M8 40 h20 v6 h-20 Z M30 62 h6 v12 h-6 Z M60 62 h6 v12 h-6 Z "
                       "M20 74 h60 v5 h-60 Z", rule="nonzero"),
    "jeep": path("M8 50 l10 -22 h40 l8 14 h18 q8 0 8 8 v16 h-84 Z "
                 + circle(26, 72, 12) + " " + circle(74, 72, 12) + " M24 34 l-6 12 h20 v-12 Z", rule="nonzero"),
    "boat": path("M4 56 h92 l-14 26 h-64 Z M26 38 h36 l10 14 h-50 Z M40 20 h6 v18 h-6 Z", rule="nonzero"),
    "grenade": path("M34 26 h32 v8 h-32 Z M42 14 h16 v12 h-16 Z M58 16 L76 8 L80 14 L62 22 Z "
                    "M30 36 h40 q14 8 14 30 q0 28 -34 28 q-34 0 -34 -28 q0 -22 14 -30 Z "
                    "M36 56 h28 v4 h-28 Z M36 70 h28 v4 h-28 Z M48 44 h4 v42 h-4 Z"),
    "knife": path("M8 92 L30 70 L38 78 L16 100 Z M30 70 L36 64 L44 72 L38 78 Z "
                  "M36 64 L88 6 C94 22 82 50 44 72 Z", rule="nonzero"),
    "scope": path(ring(50, 50, 38, 6) + " M47 4 h6 v30 h-6 Z M47 66 h6 v30 h-6 Z M4 47 h30 v6 h-30 Z "
                  "M66 47 h30 v6 h-30 Z " + circle(50, 50, 3)),
    "explosion": f'<polygon fill="currentColor" points="{star_points(50, 52, 48, 22, points=9, rotate=-90)}"/>',
    "heart": path("M50 90 C20 70 6 54 6 34 C6 18 18 8 32 8 C40 8 46 12 50 18 C54 12 60 8 68 8 "
                  "C82 8 94 18 94 34 C94 54 80 70 50 90 Z M44 30 h12 v14 h14 v12 h-14 v14 h-12 v-14 "
                  "h-14 v-12 h14 Z"),
    "ammo": path("M18 30 C18 16 24 8 28 8 C32 8 38 16 38 30 L38 88 L18 88 Z "
                 "M42 30 C42 16 48 8 52 8 C56 8 62 16 62 30 L62 88 L42 88 Z "
                 "M66 30 C66 16 72 8 76 8 C80 8 86 16 86 30 L86 88 L66 88 Z", rule="nonzero"),
    "map": path("M6 18 L34 8 L66 18 L94 8 L94 82 L66 92 L34 82 L6 92 Z M34 16 L34 76 L40 78 L40 18 Z "
                "M60 22 L60 84 L66 86 L66 24 Z"),
    "shield": path("M50 4 L90 18 C90 56 76 80 50 96 C24 80 10 56 10 18 Z M50 16 L22 26 C24 54 34 72 50 84 Z"),
    "arrow-left": path("M64 10 L74 20 L44 50 L74 80 L64 90 L24 50 Z", rule="nonzero"),
    "arrow-right": path("M36 10 L26 20 L56 50 L26 80 L36 90 L76 50 Z", rule="nonzero"),
    "chevrons-up": path("M50 8 L90 44 L80 54 L50 28 L20 54 L10 44 Z M50 42 L90 78 L80 88 L50 62 L20 88 L10 78 Z",
                        rule="nonzero"),
    "parachute": path("M6 44 C6 20 26 6 50 6 C74 6 94 20 94 44 C86 40 78 40 72 44 C66 40 56 40 50 44 "
                      "C44 40 34 40 28 44 C22 40 14 40 6 44 Z")
                 + '<g fill="none" stroke="currentColor" stroke-width="4">'
                   '<path d="M8 44 L46 78 M28 44 L48 78 M72 44 L52 78 M92 44 L54 78"/></g>'
                 + path("M42 76 h16 v18 h-16 Z", rule="nonzero"),
    "wave": '<g fill="none" stroke="currentColor" stroke-width="9" stroke-linecap="round">'
            '<path d="M6 40 C18 28 30 52 42 40 C54 28 66 52 78 40 C86 32 92 36 96 40"/>'
            '<path d="M6 68 C18 56 30 80 42 68 C54 56 66 80 78 68 C86 60 92 64 96 68"/></g>',
    "moon": path("M64 6 C40 10 24 30 24 54 C24 78 44 96 68 94 C78 93 86 88 92 82 C66 84 46 64 46 40 "
                 "C46 26 54 14 64 6 Z", rule="nonzero"),
    "eye": path("M50 20 C24 20 8 40 4 50 C8 60 24 80 50 80 C76 80 92 60 96 50 C92 40 76 20 50 20 Z "
                + circle(50, 50, 18) + " " + circle(50, 50, 8)),
    "bolt": f'<polygon fill="currentColor" points="58,4 18,56 46,56 38,96 82,40 54,40 64,4"/>',
    "target": path(ring(50, 50, 44, 8) + " " + ring(50, 50, 28, 8) + " " + circle(50, 50, 10)),
    "boot": path("M20 6 h30 v46 l30 14 q12 6 12 18 v8 h-74 Z", rule="nonzero"),
    "handshake": path("M4 30 h18 l14 -8 h22 l8 8 h30 v34 h-14 l-26 22 c-6 4 -10 0 -8 -4 c-6 4 -12 0 -9 -5 "
                      "c-6 3 -11 -2 -8 -7 c-5 1 -9 -4 -6 -8 l-17 -2 Z", rule="nonzero"),
    "infinity": '<path fill="none" stroke="currentColor" stroke-width="10" d="M50 50 C40 34 16 30 12 50 '
                'C16 70 40 66 50 50 C60 34 84 30 88 50 C84 70 60 66 50 50 Z"/>',
    "hourglass": path("M20 6 h60 v10 h-6 C74 34 62 42 56 50 C62 58 74 66 74 84 h6 v10 h-60 v-10 h6 "
                      "C26 66 38 58 44 50 C38 42 26 34 26 16 h-6 Z M36 84 h28 C64 72 54 66 50 62 "
                      "C46 66 36 72 36 84 Z"),
    "dice": path("M14 14 h72 q6 0 6 6 v60 q0 6 -6 6 h-72 q-6 0 -6 -6 v-60 q0 -6 6 -6 Z "
                 + circle(30, 30, 7) + " " + circle(70, 30, 7) + " " + circle(50, 50, 7) + " "
                 + circle(30, 70, 7) + " " + circle(70, 70, 7)),
    "wrench": path("M60 6 C74 2 90 14 88 30 L76 22 L66 28 L66 40 L78 46 C66 58 50 50 48 40 L16 72 "
                   "C10 78 2 70 8 64 L40 32 C34 20 44 10 60 6 Z", rule="nonzero"),
    "first-blood": path("M50 4 C50 4 18 44 18 63 C18 81 32 94 50 94 C68 94 82 81 82 63 C82 44 50 4 50 4 Z "
                        "M47 42 L56 38 L56 80 L47 80 L47 51 L41 54 L39 47 Z"),
    "wings": path(
        "M44 40 C32 28 16 24 2 26 C8 30 13 33 18 36 C10 36 5 38 2 41 C10 43 17 44 24 45 C16 47 11 50 8 54 "
        "C17 55 25 54 32 52 C27 56 24 60 23 64 C32 62 40 56 46 50 Z "
        "M56 40 C68 28 84 24 98 26 C92 30 87 33 82 36 C90 36 95 38 98 41 C90 43 83 44 76 45 C84 47 89 50 92 54 "
        "C83 55 75 54 68 52 C73 56 76 60 77 64 C68 62 60 56 54 50 Z "
        + circle(50, 47, 9) + " M50 58 L56 70 L50 76 L44 70 Z", rule="nonzero"),
    "laurel": laurel(),
}
