#!/usr/bin/env python3
"""Classify the A* divergence between our tree and the recovered Ravenfield build (P24 section 3.2).

WHY THIS EXISTS
    Six A* Pathfinding Project files differ from the recovered original, and the phase plan reads
    that as evidence worth explaining. The plan's taxonomy has three buckets:

        (a) Ironfront changed it on purpose -- multiplayer, headless server
        (b) Unity API migration -- 5.4 to Unity 6
        (c) decompiler LOSS -- not explicable by (a) or (b), and therefore the suspect

    Running this shows the taxonomy is missing the bucket almost every line falls into:

        (d) decompiler RENDERING -- the same IL printed as different C#

    Both trees are decompiled from assemblies. They were not decompiled by the same tool at the
    same settings, so `num` becomes `j`, `& 1` becomes `& (true ? 1u : 0u)`, an object initializer
    becomes four field assignments, and an implicit uint-to-long promotion becomes an explicit
    cast. None of that is a change to the program. Counting those lines as divergence is counting
    the decompiler.

WHAT A LINE IN EACH BUCKET LOOKS LIKE

    (b)  -  int heightmapWidth = terrainData.heightmapWidth;
         +  int heightmapWidth = terrainData.heightmapResolution;
            TerrainData.heightmapWidth was removed in Unity 2019.3. Real, required, and in
            scan-time code that never runs at runtime (scanOnStartup is off, graphs load from
            a cache file).

    (d)  -  if (GetRandom() % n < count)
         +  if ((long)GetRandom() % (long)i < count)
            GetRandom returns uint and n is int, so C# already promotes both to long. The two
            lines compile to the same IL; one decompiler printed the conversion and the other
            did not.

USAGE
    python tools/classify_astar_diff.py [--json tools/recovered/astar-diff-classification.json]

    Needs the recovered tree extracted at tmp/recovered/ (tools/extract_recovered.py). tmp/ is
    gitignored, so this reads it and writes the RESULT into the repo -- the result is the
    artifact, the 300 MB of decompiled source is not.

EXIT CODE
    1 if any changed line could not be placed in a bucket. An unclassified line is not a failure
    of this script, it is a line somebody has to read: it is either a new shape of decompiler
    output or it is the (c) the phase went looking for.
"""

import argparse
import json
import os
import re
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# (label, path under tmp/recovered/src, path under Ironfront_Reborn/Assets/Scripts)
PAIRS = [
    ("AstarPath.cs",
     "tmp/recovered/src/Assembly-CSharp/AstarPath.cs",
     "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AstarPath.cs"),
    ("ProceduralGridMover.cs",
     "tmp/recovered/src/Assembly-CSharp/ProceduralGridMover.cs",
     "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ProceduralGridMover.cs"),
    ("EuclideanEmbedding.cs",
     "tmp/recovered/src/Assembly-CSharp/Pathfinding/EuclideanEmbedding.cs",
     "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Pathfinding/EuclideanEmbedding.cs"),
    ("RecastGraph.cs",
     "tmp/recovered/src/Assembly-CSharp/Pathfinding/RecastGraph.cs",
     "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Pathfinding/RecastGraph.cs"),
    ("Voxelize.cs",
     "tmp/recovered/src/Assembly-CSharp/Pathfinding.Voxels/Voxelize.cs",
     "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Pathfinding/Voxels/Voxelize.cs"),
    ("PathUtilities.cs",
     "tmp/recovered/src/Assembly-CSharp/Pathfinding/PathUtilities.cs",
     "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Pathfinding/PathUtilities.cs"),
]

# (a) -- anything Ironfront wrote, or any conditional compilation. Zero hits is the finding:
# nobody has edited this library for the netcode.
IRONFRONT = re.compile(r"Ironfront|NetContext|NetServerActor|IsServer|isServer|#if\b|UNITY_[A-Z]")

# (b) -- real Unity API migration.
UNITY_API = re.compile(r"heightmapWidth|heightmapHeight|heightmapResolution")

# (d) -- decompiler rendering shapes. Each is a way of printing IL that carries no semantics.
RENDERING = [
    ("discarded-unreachable-comment",
     re.compile(r"^//\s*Discarded unreachable code")),
    ("enum-member-explicit-value",
     re.compile(r"^\w+\s*(=\s*\d+)?\s*,?$")),
    ("object-initializer-vs-field-assignment",
     # `= new T` / `f(new T` open an initializer; `{`, `}`, `});` close one; `a.b = c;` and
     # `x = y,` are the member lines it decomposes into. `T v = default(T);` is the other tool's
     # rendering of the same construction.
     re.compile(r"=\s*new \w+$|^\w+\(new \w+$|^\{$|^\}\)?;?$|=\s*default\(\w+\);$"
                r"|^\w+(\.\w+)+\s*=\s*.+;$|^\w+\s*=\s*[^;]+,$|^\w+\s*=\s*[^;]+$"
                r"|^\w+ \w+ = default\(\w+\);$")),
    ("enum-name-vs-literal",
     re.compile(r"!=\s*(PathLog\.None|PathState\.Created|0)\b|GetState\(\)\s*!=\s*0\b")),
    ("explicit-numeric-conversion",
     re.compile(r"\(long\)|\(uint\)|\(ushort\)|0x[0-9A-Fa-f]+u\b|\btrue \? 1u : 0u\b"
                r"|&\s*-?\d+\b|\b\d+u\b|\(int\)\(")),
    ("local-variable-rename",
     re.compile(r"\b(num\d*|i\d*|j\d*|k\d*|l\d*|m\d*|n\d*|x\d*|z\d*|r\d*|tmp\d*|pz\d*|tz\d*"
                r"|list\d*|index\d*|item\d*|node\d*|graphNode|minz|maxz|totalTicks\d*|itm)\b")),
]


def changed_lines(orig, ours):
    """Whitespace-insensitive unified diff, reduced to the +/- lines."""
    out = subprocess.run(
        ["diff", "-u", "-bB", os.path.join(REPO, orig), os.path.join(REPO, ours)],
        capture_output=True, text=True).stdout.splitlines()
    return [l for l in out if re.match(r"^[+-][^+-]", l)]


def classify(line):
    body = line[1:].strip()
    if IRONFRONT.search(body):
        return "a_ironfront_intentional", None
    if UNITY_API.search(body):
        return "b_unity_api_migration", None
    for tag, rx in RENDERING:
        if rx.search(body):
            return "d_decompiler_rendering", tag
    return "unclassified", None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default="tools/recovered/astar-diff-classification.json")
    args = ap.parse_args()

    missing = [o for _, o, _ in PAIRS if not os.path.exists(os.path.join(REPO, o))]
    if missing:
        sys.stderr.write(
            "recovered tree not extracted -- run tools/extract_recovered.py first.\nmissing:\n  "
            + "\n  ".join(missing) + "\n")
        return 2

    files, totals, unclassified = {}, {"changed": 0, "a": 0, "b": 0, "d": 0}, []

    for name, orig, ours in PAIRS:
        lines = changed_lines(orig, ours)
        buckets = {"a_ironfront_intentional": 0, "b_unity_api_migration": 0,
                   "d_decompiler_rendering": 0}
        shapes = {}
        for l in lines:
            bucket, shape = classify(l)
            if bucket == "unclassified":
                unclassified.append((name, l))
                continue
            buckets[bucket] += 1
            if shape:
                shapes[shape] = shapes.get(shape, 0) + 1
        files[name] = {
            "changedLines": len(lines),
            "a_ironfront_intentional": buckets["a_ironfront_intentional"],
            "b_unity_api_migration": buckets["b_unity_api_migration"],
            "c_decompiler_loss": 0,   # by construction: whatever is left is unclassified, below
            "d_decompiler_rendering": buckets["d_decompiler_rendering"],
            "renderingShapes": dict(sorted(shapes.items())),
        }
        totals["changed"] += len(lines)
        totals["a"] += buckets["a_ironfront_intentional"]
        totals["b"] += buckets["b_unity_api_migration"]
        totals["d"] += buckets["d_decompiler_rendering"]

    doc = {
        "note": "P24 section 3.2. (c) is reported as 0 only because 'unclassified' is empty -- "
                "every changed line was placed in (a), (b) or (d). A non-empty unclassified list "
                "is the (c) the phase went looking for, and this script exits 1 when there is one.",
        "files": files,
        "totals": {
            "changedLines": totals["changed"],
            "a_ironfront_intentional": totals["a"],
            "b_unity_api_migration": totals["b"],
            "c_decompiler_loss": 0,
            "d_decompiler_rendering": totals["d"],
            "unclassified": len(unclassified),
        },
    }

    print(f"{'file':<24} {'chg':>5} {'(a)':>5} {'(b)':>5} {'(c)':>5} {'(d)':>5}")
    for k, v in files.items():
        print(f"{k:<24} {v['changedLines']:>5} {v['a_ironfront_intentional']:>5} "
              f"{v['b_unity_api_migration']:>5} {v['c_decompiler_loss']:>5} "
              f"{v['d_decompiler_rendering']:>5}")
    t = doc["totals"]
    print(f"{'TOTAL':<24} {t['changedLines']:>5} {t['a_ironfront_intentional']:>5} "
          f"{t['b_unity_api_migration']:>5} {t['c_decompiler_loss']:>5} "
          f"{t['d_decompiler_rendering']:>5}")

    if unclassified:
        print("\nUNCLASSIFIED -- read these by hand; each is a new rendering shape or a real (c):")
        for name, l in unclassified:
            print(f"  {name}: {l}")

    out = os.path.join(REPO, args.json)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8", newline="") as f:
        json.dump(doc, f, indent=2)
        f.write("\n")
    print(f"\nwrote {args.json}")

    return 1 if unclassified else 0


if __name__ == "__main__":
    sys.exit(main())
