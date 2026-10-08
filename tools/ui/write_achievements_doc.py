#!/usr/bin/env python3
"""Writes docs/achievements.md, the wiki page of every achievement, from the catalogue.

    python tools/ui/write_achievements_doc.py           # rewrite the page
    python tools/ui/write_achievements_doc.py --check   # fail if the page is out of date

The catalogue (Ironfront.Net.Protocol/Achievements/AchievementCatalog.cs) is the one list the
master unlocks from and the game draws; this page is generated from it so a title or a target
cannot differ between the game and its documentation. CareerServiceTests also checks that the
page names every achievement.
"""
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CATALOG = os.path.join(ROOT, "Ironfront.Net.Protocol", "Achievements", "AchievementCatalog.cs")
OUT = os.path.join(ROOT, "docs", "achievements.md")
BADGES = "../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/"

ENTRY = re.compile(
    r'new Achievement\("(?P<id>[a-z_]+)", "(?P<title>[^"]+)", "(?P<line>[^"]+)", '
    r'AchievementCategory\.(?P<category>\w+), AchievementTier\.(?P<tier>\w+), '
    r'(?:CareerStat\.(?P<stat>\w+)|null), (?P<target>[^,)]+)(?P<hidden>, hidden: true)?\)')

ORDER = ["Multiplayer", "Combat", "Vehicles", "Honor", "Hard", "Secret", "Practice"]

INTRO = {
    "Multiplayer": "Playing online: matches, rounds won, time served, every map.",
    "Combat": "Kills and how they were made: multi-kills, streaks, headshots, long shots, blades and grenades.",
    "Vehicles": "Fighting from the jeeps, tanks, helicopters and boats, and against them.",
    "Honor": "Playing for the side: flags taken, rounds carried, comebacks.",
    "Hard": "The long grind and the rare feat. Most players never see these.",
    "Secret": "Hidden in the game until earned: the page shows a sealed badge and \"???\".",
    "Practice": "Earned offline, against bots, and on the How to play guide. Claimed for your account the next time you sign in.",
}


def tracks(stat, target):
    """How the page and the master measure progress, in a reader's words."""
    if stat is None:
        return "Seen by your game"
    value = int(eval(target, {"__builtins__": {}}))  # the catalogue's own constant, e.g. 24 * 60 * 60
    if stat == "SecondsPlayed":
        return f"time played, {value // 3600:,} h"
    if stat.endswith("Metres"):
        words = re.sub(r"(?<!^)(?=[A-Z])", " ", stat[:-len("Metres")]).lower()
        return f"{words}, {value:,} m"
    words = re.sub(r"(?<!^)(?=[A-Z])", " ", stat).lower()
    return f"{words}, {value:,}"


def load():
    source = open(CATALOG, encoding="utf-8-sig").read()
    entries = [m.groupdict() for m in ENTRY.finditer(source)]
    if len(entries) != 50:
        sys.exit(f"expected 50 achievements in the catalogue, parsed {len(entries)}")
    return entries


def render(entries):
    lines = [
        "# Achievements",
        "",
        "Fifty achievements, earned online and in practice (owner's list of 2026-10-09, item 4).",
        "Open them from **ACHIEVEMENTS** on the main menu or in the Esc menu during a match;",
        "the **GLOBAL RANKING** sits beside them.",
        "",
        "## How they work",
        "",
        "- **Online achievements are judged by the master server.** At the end of every online round",
        "  the game server reports each player's numbers (kills, flags, the longest shot, the best",
        "  multi-kill...) and the master adds them to the account's career. An achievement unlocks the",
        "  moment its career number reaches the target, and a banner drops in at the top of the screen.",
        "- **Practice achievements are seen by your own game.** No server watches an offline match, so",
        "  the game records them on this computer, shows the banner at once, and claims them for your",
        "  account the next time you sign in to multiplayer.",
        "- **The list is sorted by how many players hold each one**, commonest first, like a store's",
        "  global achievement list. The share is out of every player with a career; the rarer ones are",
        "  marked RARE and ULTRA RARE.",
        "- **Hidden achievements** show a sealed badge and \"???\" until earned. Their descriptions are",
        "  below, folded away: open the Secret section only if you want the spoilers.",
        "- **Badges** are generated from `tools/ui/badges.py` by `tools/ui/make_icons.py`; this page is",
        "  generated from the catalogue by `tools/ui/write_achievements_doc.py`. Do not edit it by hand.",
        "",
        "Metals, easiest to hardest: Bronze, Silver, Gold, Platinum.",
        "",
    ]

    counts = {c: sum(1 for e in entries if e["category"] == c) for c in ORDER}
    lines += ["| Family | Achievements |", "|---|---|"]
    lines += [f"| {c} | {counts[c]} |" for c in ORDER]
    lines += [f"| **All** | **{len(entries)}** |", ""]

    for category in ORDER:
        group = [e for e in entries if e["category"] == category]
        lines += [f"## {category}", "", INTRO[category], ""]
        secret = category == "Secret"
        if secret:
            lines += ["<details>", "<summary>Spoilers: the hidden achievements</summary>", ""]
        lines += ["| Badge | Achievement | How to earn it | Metal | Tracked by |", "|---|---|---|---|---|"]
        for e in group:
            badge = f'<img src="{BADGES}{e["id"]}.png" width="64" alt="{e["title"]}">'
            lines.append(f'| {badge} | **{e["title"]}**<br>`{e["id"]}` | {e["line"]} | {e["tier"]} | '
                         f'{tracks(e["stat"], e["target"])} |')
        lines.append("")
        if secret:
            lines += ["</details>", ""]

    lines += [
        "## The hidden badge",
        "",
        f'<img src="{BADGES}_hidden.png" width="64" alt="Hidden achievement"> What every secret',
        "achievement shows until it is earned.",
        "",
    ]
    return "\n".join(lines)


def main():
    page = render(load())
    if "--check" in sys.argv:
        current = open(OUT, encoding="utf-8").read() if os.path.exists(OUT) else ""
        if current != page:
            sys.exit("docs/achievements.md is out of date: run python tools/ui/write_achievements_doc.py")
        print("docs/achievements.md is up to date")
        return
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(page)
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
