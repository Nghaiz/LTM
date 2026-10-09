"""Achievement badges (achievements v2): a frame per metal, a dark medallion, the achievement's glyph
in the middle, and small marks for Night Mode and practice. Every hidden achievement also gets a
black silhouette of its badge (``<id>_shadow``), which the game shows until it is earned.

The metal, and whether an achievement is hidden, night or practice, come from the catalogue
(Ironfront.Net.Protocol/Achievements/AchievementCatalog.cs) through ``write_achievements_doc.load``;
this file only says which glyph each one wears. ``CareerServiceTests`` fails when an achievement
has no badge, a hidden one has no silhouette, or a badge is left over from a retired achievement.
"""
import math

from glyphs import GLYPHS, star_points
from write_achievements_doc import load

# Rim gradient (light, dark), glyph ink, accent: per metal. Mythic is obsidian with red light.
TIERS = {
    "Bronze":   ("#F2B27A", "#7A3E14", "#FFD9B3", "#E58A46"),
    "Silver":   ("#F4F8FC", "#6E7E90", "#FFFFFF", "#A9C3DA"),
    "Gold":     ("#FFE68A", "#A86A06", "#FFF1C2", "#FFC233"),
    "Platinum": ("#D9FBFF", "#2B7FA6", "#E8FDFF", "#66E0FF"),
    "Mythic":   ("#FF8A7A", "#5A0B10", "#FFD6D0", "#FF3D3D"),
    "hidden":   ("#C9A6FF", "#3D1F73", "#EADCFF", "#9B6BFF"),
}
STARS = {"Bronze": 0, "Silver": 1, "Gold": 2, "Platinum": 3, "Mythic": 0, "hidden": 0}
CORES = {
    "Mythic": ("#3A0D12", "#14080A", "#050203"),
}
DEFAULT_CORE = ("#18405F", "#0A2236", "#040F1A")

# id -> glyph. A new achievement without an entry stops make_icons.py with its id.
GLYPH = {
    "roll_call": "person", "lights_out": "moon", "baptism_of_fire": "first-blood", "steady_hand": "crosshair",
    "flag_runner": "flag", "taste_of_victory": "trophy", "speed_bump": "jeep", "by_the_book": "book",
    "cadet": "chevrons-up", "turncoat": "refresh", "victory_lap": "speaker", "bullet_sponge": "target",
    "participation_trophy": "podium",
    "three_fronts": "map", "unbroken": "shield", "predator": "people", "dead_centre": "headshot",
    "overwatch": "scope", "steel_rain": "tank", "rotorhead": "helicopter", "forward_supply": "ammo",
    "night_shift": "eye", "cannon_fodder": "skull", "dust_devil": "flame", "first_past_the_post": "clock",
    "boots_only": "boot", "nine_lives": "heart", "man_overboard": "wave", "mutual_destruction": "explosion",
    "grim_arithmetic": "rifles", "juggernaut": "bolt", "crowd_control": "grenade", "cold_steel": "knife",
    "armourer": "gear", "can_opener": "explosion", "long_campaign": "laurel", "top_brass": "crown",
    "clean_sheet": "shield", "naked_eye": "eye", "night_terror": "moon", "hell_week": "bot",
    "island_hopper": "flag", "lake_monster": "boat", "graveyard_shift": "skull", "motor_pool": "jeep",
    "jack_of_all_trades": "dice", "touchdown": "parachute", "buckshot_sniper": "scope", "dogfight": "wings",
    "gold_standard": "wrench",
    "windreader": "scope", "all_fronts_mastered": "map", "air_defense": "helicopter", "undefeated": "infinity",
    "moonlight_marksman": "moon", "outnumbered": "people", "on_borrowed_time": "hourglass", "pacifist": "handshake",
    "nemesis": "target", "absolute_dominance": "crown", "drill_sergeant": "chevrons-up", "grand_tour": "laurel",
    "impossible_angle": "tank", "from_the_grave": "grenade",
    "centurion": "shield", "rampage": "flame", "curvature": "scope", "perfect_ten": "headshot",
    "dead_eye": "crosshair", "untouchable": "heart", "blade_only": "knife", "tank_ace": "tank",
    "sky_king": "helicopter", "map_painter": "flag", "hail_mary": "clock", "creature_of_the_night": "moon",
    "immaculate": "star", "ironclad": "medal", "mid_air": "wings", "counter_sniper": "bolt",
}


def _centre(tier):
    """Where the medallion sits: the Mythic one is lower, under its crown of thorns."""
    return (50, 57) if tier == "Mythic" else (50, 50)


def _frame(tier):
    """The outer emblem: disc, hexagon, shield, star-burst, and for Mythic a jagged obsidian seal."""
    if tier == "Silver":
        pts = " ".join(f"{50 + 47 * math.cos(math.radians(a)):.2f},{50 + 47 * math.sin(math.radians(a)):.2f}"
                       for a in range(-90, 270, 60))
        return f'<polygon points="{pts}"/>'
    if tier == "Gold":
        return '<path d="M50 2 L92 16 C92 56 78 82 50 98 C22 82 8 56 8 16 Z"/>'
    if tier == "Platinum":
        return f'<polygon points="{star_points(50, 50, 49, 41, points=12, rotate=-90)}"/>'
    if tier == "Mythic":
        return f'<polygon points="{star_points(50, 57, 42, 36, points=18, rotate=-90)}"/>'
    return '<circle cx="50" cy="50" r="47"/>'


def _scaled(tier, fragment, attributes):
    cx, cy = _centre(tier)
    return f'<g transform="translate({cx} {cy}) scale(0.84) translate({-cx} {-cy})" {attributes}>{fragment}</g>'


def _crown(fill):
    """The Mythic crown of thorns over the seal."""
    return (f'<polygon fill="{fill}" points="27,24 31,6 39,18 44,2 50,14 56,2 61,18 69,6 73,24 '
            f'62,20 50,24 38,20"/>')


def _cracks(accent):
    """Red light leaking through cracks in the obsidian."""
    lines = ("M50 26 L46 36 L52 42 L47 52", "M24 58 L33 61 L36 70 L44 73",
             "M77 50 L68 56 L71 64 L63 72", "M40 86 L47 80 L54 85 L60 79")
    glow = "".join(f'<path d="{d}" fill="none" stroke="{accent}" stroke-width="3.2" stroke-opacity="0.22" '
                   f'stroke-linecap="round" stroke-linejoin="round"/>' for d in lines)
    core = "".join(f'<path d="{d}" fill="none" stroke="{accent}" stroke-width="1.1" stroke-opacity="0.9" '
                   f'stroke-linecap="round" stroke-linejoin="round"/>' for d in lines)
    return glow + core


def _mark(x, glyph, ink, accent):
    """A small disc on the rim carrying a mark: a moon for Night Mode, a target for practice."""
    return (f'<circle cx="{x}" cy="15" r="11" fill="#071827" stroke="{accent}" stroke-width="2.2"/>'
            f'<g transform="translate({x - 7.5} 7.5) scale(0.15)" color="{ink}">{GLYPHS[glyph]}</g>')


def badge_svg(glyph, tier, night=False, practice=False, dim_glyph=False):
    light, dark, ink, accent = TIERS[tier]
    inner, middle, outer = CORES.get(tier, DEFAULT_CORE)
    frame = _frame(tier)
    cx, cy = _centre(tier)
    stars = STARS[tier]
    star_row = ""
    for k in range(stars):
        x = 50 + (k - (stars - 1) / 2.0) * 11
        star_row += f'<polygon fill="{accent}" points="{star_points(x, 88, 4.6, 2.0)}"/>'
    mythic = tier == "Mythic"
    marks = (_mark(84, "moon", ink, accent) if night else "") + (_mark(16, "target", ink, accent) if practice else "")
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">
<defs>
  <linearGradient id="rim" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="{light}"/><stop offset="1" stop-color="{dark}"/>
  </linearGradient>
  <radialGradient id="core" cx="0.5" cy="0.38" r="0.62">
    <stop offset="0" stop-color="{inner}"/><stop offset="0.65" stop-color="{middle}"/>
    <stop offset="1" stop-color="{outer}"/>
  </radialGradient>
  <linearGradient id="shine" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.38"/>
    <stop offset="0.5" stop-color="#FFFFFF" stop-opacity="0"/>
  </linearGradient>
</defs>
{_crown("url(#rim)") if mythic else ""}
<g fill="url(#rim)">{frame}</g>
{_scaled(tier, frame, 'fill="url(#core)"')}
{_cracks(accent) if mythic else ""}
{_scaled(tier, frame, f'fill="none" stroke="{accent}" stroke-opacity="0.55" stroke-width="1.6"')}
<g transform="translate({cx - 26} {cy - 28}) scale(0.52)" color="{ink}" opacity="{0.45 if dim_glyph else 1}">{GLYPHS[glyph]}</g>
{star_row}
{_scaled(tier, frame, 'fill="url(#shine)"')}
{marks}
</svg>"""


def shadow_svg(glyph, tier):
    """A hidden achievement before it is earned: its badge's shape in black, the glyph barely there."""
    frame = _frame(tier)
    cx, cy = _centre(tier)
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">
{_crown("#14181F") if tier == "Mythic" else ""}
<g fill="#14181F">{frame}</g>
{_scaled(tier, frame, 'fill="#06080B"')}
{_scaled(tier, frame, 'fill="none" stroke="#9B6BFF" stroke-opacity="0.35" stroke-width="1.6"')}
<g transform="translate({cx - 26} {cy - 28}) scale(0.52)" color="#232A35">{GLYPHS[glyph]}</g>
</svg>"""


def hidden_badge_svg():
    """The generic seal for a hidden achievement whose silhouette is missing: a question mark on violet."""
    return badge_svg("question", "hidden")


def all_badges():
    """Every badge to render, as (file name without .png, svg): one per achievement, a silhouette per hidden one."""
    for entry in load():
        key = entry["id"]
        if key not in GLYPH:
            raise KeyError(f"tools/ui/badges.py: no glyph chosen for '{key}'; add it to GLYPH")
        yield key, badge_svg(GLYPH[key], entry["tier"], entry["night"], entry["practice"])
        if entry["hidden"]:
            yield key + "_shadow", shadow_svg(GLYPH[key], entry["tier"])
    yield "_hidden", hidden_badge_svg()
