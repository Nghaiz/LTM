"""Achievement badges: a tier frame, a dark medallion, and the achievement's glyph in the middle.

The achievement list itself (names, descriptions, rules) is ``AchievementCatalog`` in
Ironfront.Net.Protocol; this file only says what each one LOOKS like. ``AchievementArtTests``
(Unity EditMode) fails when an achievement has no badge, so the two cannot drift silently.
"""
import math

from glyphs import GLYPHS, star_points

# Rim gradient (light, dark), glyph ink, accent: per tier.
TIERS = {
    "bronze":   ("#F2B27A", "#7A3E14", "#FFD9B3", "#E58A46"),
    "silver":   ("#F4F8FC", "#6E7E90", "#FFFFFF", "#A9C3DA"),
    "gold":     ("#FFE68A", "#A86A06", "#FFF1C2", "#FFC233"),
    "platinum": ("#D9FBFF", "#2B7FA6", "#E8FDFF", "#66E0FF"),
    "hidden":   ("#C9A6FF", "#3D1F73", "#EADCFF", "#9B6BFF"),
}

# id -> (glyph, tier). Order follows AchievementCatalog.
ACHIEVEMENT_ART = [
    ("boots_on_the_ground", "parachute", "bronze"),
    ("first_blood", "first-blood", "bronze"),
    ("decorated", "medal", "silver"),
    ("veteran", "chevrons-up", "gold"),
    ("war_machine", "skull", "platinum"),
    ("tour_of_duty", "rifles", "bronze"),
    ("career_soldier", "rifles", "silver"),
    ("forever_war", "hourglass", "gold"),
    ("victory", "trophy", "bronze"),
    ("champion", "trophy", "silver"),
    ("conqueror", "laurel", "gold"),
    ("two_for_one", "crosshair", "bronze"),
    ("triple_threat", "target", "silver"),
    ("unstoppable", "flame", "gold"),
    ("legend_never_dies", "crown", "platinum"),
    ("sharpshooter", "headshot", "bronze"),
    ("head_hunter", "headshot", "gold"),
    ("long_shot", "scope", "silver"),
    ("eagle_eye", "scope", "platinum"),
    ("up_close", "knife", "bronze"),
    ("frag_out", "grenade", "silver"),
    ("demolition_expert", "explosion", "gold"),
    ("tank_buster", "tank", "silver"),
    ("road_rage", "jeep", "bronze"),
    ("armored_fist", "tank", "gold"),
    ("air_superiority", "helicopter", "gold"),
    ("anchors_aweigh", "boat", "silver"),
    ("payback", "bolt", "bronze"),
    ("against_all_odds", "wings", "gold"),
    ("flawless", "shield", "platinum"),
    ("mvp", "star", "silver"),
    ("flag_bearer", "flag", "bronze"),
    ("blitzkrieg", "flag", "gold"),
    ("world_traveler", "map", "silver"),
    ("night_owl", "moon", "bronze"),
    ("night_stalker", "eye", "silver"),
    ("quartermaster", "ammo", "bronze"),
    ("patched_up", "heart", "bronze"),
    ("man_vs_machine", "bot", "bronze"),
    ("player_hunter", "person", "silver"),
    ("gravity_wins", "boot", "hidden"),
    ("sleeping_with_the_fishes", "wave", "hidden"),
    ("friendly_fire", "handshake", "hidden"),
    ("own_goal", "explosion", "hidden"),
    ("not_today", "heart", "hidden"),
    ("hat_trick", "headshot", "hidden"),
    ("basic_training", "target", "bronze"),
    ("bot_buster", "bot", "silver"),
    ("one_man_army", "people", "gold"),
    ("student_of_war", "book", "bronze"),
]


def _frame(tier):
    """The outer emblem shape for a tier: disc, hexagon, shield, star-burst; hidden gets the disc."""
    if tier == "silver":
        pts = " ".join(f"{50 + 47 * math.cos(math.radians(a)):.2f},{50 + 47 * math.sin(math.radians(a)):.2f}"
                       for a in range(-90, 270, 60))
        return f'<polygon points="{pts}"/>'
    if tier == "gold":
        return '<path d="M50 2 L92 16 C92 56 78 82 50 98 C22 82 8 56 8 16 Z"/>'
    if tier == "platinum":
        return f'<polygon points="{star_points(50, 50, 49, 41, points=12, rotate=-90)}"/>'
    return '<circle cx="50" cy="50" r="47"/>'


def badge_svg(glyph, tier, dim_glyph=False):
    light, dark, ink, accent = TIERS[tier]
    frame = _frame(tier)
    stars = {"bronze": 0, "silver": 1, "gold": 2, "platinum": 3, "hidden": 0}[tier]
    star_row = ""
    for k in range(stars):
        x = 50 + (k - (stars - 1) / 2.0) * 11
        star_row += f'<polygon fill="{accent}" points="{star_points(x, 88, 4.6, 2.0)}"/>'
    glyph_fragment = GLYPHS[glyph]
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">
<defs>
  <linearGradient id="rim" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="{light}"/><stop offset="1" stop-color="{dark}"/>
  </linearGradient>
  <radialGradient id="core" cx="0.5" cy="0.38" r="0.62">
    <stop offset="0" stop-color="#18405F"/><stop offset="0.65" stop-color="#0A2236"/>
    <stop offset="1" stop-color="#040F1A"/>
  </radialGradient>
  <linearGradient id="shine" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.38"/>
    <stop offset="0.5" stop-color="#FFFFFF" stop-opacity="0"/>
  </linearGradient>
</defs>
<g fill="url(#rim)">{frame}</g>
<g transform="translate(50 50) scale(0.84) translate(-50 -50)" fill="url(#core)">{frame}</g>
<g transform="translate(50 50) scale(0.84) translate(-50 -50)" fill="none" stroke="{accent}" stroke-opacity="0.55" stroke-width="1.6">{frame}</g>
<g transform="translate(24 22) scale(0.52)" color="{ink}" opacity="{0.45 if dim_glyph else 1}">{glyph_fragment}</g>
{star_row}
<g transform="translate(50 50) scale(0.84) translate(-50 -50)" fill="url(#shine)">{frame}</g>
</svg>"""


def hidden_badge_svg():
    """What a locked hidden achievement shows: no glyph, only a question mark on a violet disc."""
    return badge_svg("question", "hidden")


def all_badges():
    for key, glyph, tier in ACHIEVEMENT_ART:
        yield key, badge_svg(glyph, tier)
    yield "_hidden", hidden_badge_svg()
