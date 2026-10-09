#!/usr/bin/env python3
"""Writes docs/achievements-wiki.md, the players' page of every achievement, from the catalogue.

    python tools/ui/write_achievements_doc.py           # rewrite the page
    python tools/ui/write_achievements_doc.py --check   # fail if the page is out of date

The catalogue (Ironfront.Net.Protocol/Achievements/AchievementCatalog.cs) is the one list the
master unlocks from and the game draws; this page is generated from it so a title or a rule cannot
differ between the game and its documentation. CareerServiceTests checks the page names every
achievement. docs/achievements.md is the owner's design document (Vietnamese) and is written by hand.
"""
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CATALOG = os.path.join(ROOT, "Ironfront.Net.Protocol", "Achievements", "AchievementCatalog.cs")
OUT = os.path.join(ROOT, "docs", "achievements-wiki.md")
BADGES = "../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/"

CALL = re.compile(r'(?P<helper>Feat|Counter|Best|PartsOf|Claimed|new Achievement)\((?P<number>\d+), (?P<body>.*?)\),\n',
                  re.S)
STRING = re.compile(r'"((?:[^"\\]|\\.)*)"')
TIERS = {"B": "Bronze", "S": "Silver", "G": "Gold", "P": "Platinum", "M": "Mythic"}
POINTS = {"Bronze": 10, "Silver": 25, "Gold": 50, "Platinum": 100, "Mythic": 250}
ORDER = ["Bronze", "Silver", "Gold", "Platinum", "Mythic"]
INTRO = {
    "Bronze": "A few hours of play, and the funny disasters.",
    "Silver": "Regular play with some skill behind it.",
    "Gold": "Real skill, or real persistence.",
    "Platinum": "The top of a skill, a role, or a rare situation.",
    "Mythic": "The moments players talk about for years.",
}


def load():
    source = open(CATALOG, encoding="utf-8-sig").read()
    start = source.index("public static readonly IReadOnlyList<Achievement> All")
    entries = []
    for m in CALL.finditer(source, start):
        body = m.group("body")
        strings = STRING.findall(body)
        if m.group("helper") == "Best":
            title_id, unit, description, teaser = strings[0], strings[2], strings[3], strings[4] if len(strings) > 4 else ""
        else:
            title_id, unit, description, teaser = strings[0], "", strings[2], strings[3] if len(strings) > 3 else ""
        tier_token = re.search(r'"[^"]*", "[^"]*", (\w+),', body).group(1)
        tags = re.search(r'"[^"]*", "[^"]*", \w+, ([\w |]+),', body).group(1)
        tier = TIERS.get(tier_token, tier_token)
        entries.append({
            "number": int(m.group("number")),
            "id": title_id,
            "title": strings[1],
            "tier": tier,
            "description": description,
            "teaser": teaser,
            "online": "On" in tags.split(" | "),
            "practice": "Pr" in tags.split(" | "),
            "night": "Night" in tags.split(" | "),
            "hidden": "Hid" in tags.split(" | "),
        })
    entries.sort(key=lambda e: e["number"])
    if len(entries) != 80 or [e["number"] for e in entries] != list(range(1, 81)):
        sys.exit(f"expected achievements 1 to 80 in the catalogue, parsed {len(entries)}")
    return entries


def tag_words(e):
    words = ["Online" if e["online"] else "Practice"]
    if e["night"]:
        words.append("Night Mode")
    if e["hidden"]:
        words.append("Hidden")
    return ", ".join(words)


def render(entries):
    total = sum(POINTS[e["tier"]] for e in entries)
    lines = [
        "# Achievements",
        "",
        f"Eighty achievements worth {total:,} points, earned online and in practice. Open them from",
        "**ACHIEVEMENTS** on the main menu or the Esc menu in a match; compare yours with any player",
        "from **GLOBAL RANKING**.",
        "",
        "## How they work",
        "",
        "- **Online achievements are judged by the master server** from what the game server saw in",
        "  each round. Unlocks arrive within seconds, as a banner at the top of the screen; only you see",
        "  yours.",
        "- **A round counts** for \"finish\" and \"win\" when you are in it at its end and played at least",
        "  5 minutes of it. Achievements that ask for longer say how long.",
        "- **Practice achievements are judged by your own game** in offline matches, kept on your",
        "  computer and claimed for your account the next time you sign in.",
        "- **Hidden achievements** show their name, a black silhouette and a hint. The rule appears",
        "  once you earn it; this page shows the hint only.",
        "- **Points**: Bronze 10, Silver 25, Gold 50, Platinum 100, Mythic 250.",
        "- Badges come from `tools/ui/badges.py` (rendered by `tools/ui/make_icons.py`); this page is",
        "  generated from the catalogue by `tools/ui/write_achievements_doc.py`. Do not edit it by hand.",
        "",
        "| Metal | Achievements | Points each |",
        "|---|---|---|",
    ]
    for tier in ORDER:
        lines.append(f"| {tier} | {sum(1 for e in entries if e['tier'] == tier)} | {POINTS[tier]} |")
    lines += [f"| **All** | **{len(entries)}** | **{total:,} in all** |", ""]

    for tier in ORDER:
        lines += [f"## {tier}", "", INTRO[tier], "",
                  "| # | Badge | Achievement | How to earn it | Kind |", "|---|---|---|---|---|"]
        for e in (x for x in entries if x["tier"] == tier):
            badge = e["id"] + ("_shadow" if e["hidden"] else "")
            img = f'<img src="{BADGES}{badge}.png" width="64" alt="{e["title"]}">'
            rule = f'*{e["teaser"]}*' if e["hidden"] else e["description"]
            lines.append(f'| {e["number"]} | {img} | **{e["title"]}**<br>`{e["id"]}` | {rule} | {tag_words(e)} |')
        lines.append("")
    return "\n".join(lines)


def main():
    page = render(load())
    if "--check" in sys.argv:
        current = open(OUT, encoding="utf-8").read() if os.path.exists(OUT) else ""
        if current != page:
            sys.exit("docs/achievements-wiki.md is out of date: run python tools/ui/write_achievements_doc.py")
        print("docs/achievements-wiki.md is up to date")
        return
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(page)
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
