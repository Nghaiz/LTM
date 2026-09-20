#!/usr/bin/env python3
"""Find what our tree LOST relative to the recovered Ravenfield build (P27 section 5.1).

WHY THIS EXISTS
    322 .cs files exist in both our tree and the recovered original, and ~8.5k lines differ.
    Reading 8.5k lines to find a handful of real defects is not a plan, and three quarters of
    those lines are not defects at all. The phase taxonomy is:

        (a) Ironfront changed it on purpose -- netcode, headless server
        (b) Unity API migration -- 5.4 to Unity 6
        (c) LOSS from the 2017 decompilation our tree descends from   <-- the only bug class
        (d) meaningless -- two decompilers printing the same IL differently

    This script exists to shrink the (c) search, and it does so with one observation:

        A LOSS CAN ONLY SHOW UP AS A LINE THE ORIGINAL HAS AND WE DO NOT.

    An ADDED line is, by construction, not something we lost. Half of a unified diff is therefore
    irrelevant to the question, and our tree adds a great deal -- netcode, guards, and a very
    large volume of doc comments. Dropping the additions, the comments, and the lines that merely
    MOVED takes 8,547 changed lines to ~660 candidates. That is a list a person can read.

WHAT IT DOES NOT DO
    It does not decide (a) vs (b) vs (c). A regex cannot tell "deleted the pause key because
    multiplayer has no pause" from "dropped a statement by accident" -- both are a line we do not
    have. It hands a human the short list and the nearest counterpart line in our tree, and the
    human decides. Reporting a bucket here would be a number nobody could check.

    It is also position-insensitive, so a statement REORDERED inside a method is invisible to it.
    That is a deliberate trade: the decompilers emit members in different orders, and an
    order-sensitive diff drowns in that. Between two renderings of the same program a genuine
    reorder is vanishingly rare, and it would almost always come with other changed lines.

THE ONLY REASON TO TRUST THE NUMBER IT PRINTS
    `--self-test` injects five kinds of real defect into our side of a file pair that currently
    has zero survivors, and requires the detector to report each one. A filter that cannot be
    watched going red is decoration -- and this one filters away 92% of its input, so it has
    every opportunity to be quietly wrong.

USAGE
    python tools/classify_recovered_diff.py --self-test     # prove it can fail
    python tools/classify_recovered_diff.py                 # write the candidate list
    python tools/classify_recovered_diff.py --check         # regression gate against the record

    Needs the recovered tree extracted at tmp/recovered/ (tools/extract_recovered.py). tmp/ is
    gitignored, so this reads it and writes the RESULT into the repo -- the result is the
    artifact, the decompiled source is not.

EXIT CODES
    0  ok
    1  --self-test: an injected defect went unreported / --check: the candidate set moved
    2  recovered tree not extracted
"""

import argparse
import collections
import difflib
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OURS_ROOT = os.path.join(REPO, "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp")
REC_ROOTS = [os.path.join(REPO, "tmp/recovered/src/Assembly-CSharp"),
             os.path.join(REPO, "tmp/recovered/src/Assembly-CSharp-firstpass")]
RECORD = "tools/recovered/logic-triage.p27.json"

# The six A* files P24 already classified. Section 9 of the P27 plan carves them out; the OTHER
# A* files are still in scope and land in this script's output.
P24_FILES = {"AstarPath.cs", "ProceduralGridMover.cs", "EuclideanEmbedding.cs",
             "RecastGraph.cs", "Voxelize.cs", "PathUtilities.cs"}

# Lines that carry no behaviour on their own. Counting them as candidates would bury the list in
# braces: a rewritten method body produces dozens of them and not one is a fact about the game.
TRIVIAL = {"{", "}", "};", "});", ")", ");", "else", "break;", "continue;", "return;",
           "#endif", "#else", "try", "finally", "do", "default"}

CAST = re.compile(r"\((?:s?byte|u?short|u?int|u?long|float|double|decimal)\)")
NUMERIC_SUFFIX = re.compile(r"(?<=[\d.])[fFdDuUlLmM]+\b")
ENUM_EXPLICIT_VALUE = re.compile(r"^(\w+)\s*=\s*-?\d+\s*(,?)$")


def strip_comments(src):
    """Drop // and /* */ comments. Both trees keep their comments in the same places as code,
    and ours carries a great deal of prose the original never had -- none of which can be a
    thing we lost."""
    out, in_block = [], False
    for raw in src.splitlines():
        s = raw
        if in_block:
            if "*/" in s:
                s, in_block = s.split("*/", 1)[1], False
            else:
                continue
        while "/*" in s:
            pre, rest = s.split("/*", 1)
            if "*/" in rest:
                s = pre + rest.split("*/", 1)[1]
            else:
                s, in_block = pre, True
                break
        if "//" in s:
            quoted, cut, i = False, None, 0
            while i < len(s):
                ch = s[i]
                if ch == '"' and (i == 0 or s[i - 1] != "\\"):
                    quoted = not quoted
                elif not quoted and s.startswith("//", i):
                    cut = i
                    break
                i += 1
            if cut is not None:
                s = s[:cut]
        out.append(s)
    return out


def normalise(line):
    """Erase the ways two decompilers print the same IL differently -- bucket (d).

    Deliberately NOT done here: renaming generated locals (`num2` -> `_v`). That pass removes a
    further ~120 lines, and it buys them by collapsing every short identifier in the codebase,
    including real field names. A filter that aggressive stops being evidence."""
    s = re.sub(r"\s+", " ", line.replace("this.", "")).strip()
    if not s:
        return ""
    s = ENUM_EXPLICIT_VALUE.sub(r"\1\2", s)          # `No = 0,`      <-> `No,`
    s = CAST.sub("", s)                              # `(long)x`      <-> `x`
    s = NUMERIC_SUFFIX.sub("", s)                    # `1u` / `0.5f`  <-> `1` / `0.5`
    s = re.sub(r"\bdefault\(\w+\)", "default", s)    # `default(int)` <-> `default`
    return re.sub(r"\s+", " ", s).strip()


def index(root):
    m = collections.defaultdict(list)
    for dirpath, _, filenames in os.walk(root):
        for fn in filenames:
            if fn.endswith(".cs"):
                m[fn].append(os.path.join(dirpath, fn))
    return m


def pair_files():
    """Match by basename. The two trees disagree on directory layout (`Pathfinding.Voxels/` vs
    `Pathfinding/Voxels/`), and a basename is unique enough here -- the two that are not
    (AssemblyInfo.cs, Extensions.cs) are reported rather than silently resolved."""
    ours = index(OURS_ROOT)
    rec = collections.defaultdict(list)
    for r in REC_ROOTS:
        for k, v in index(r).items():
            rec[k].extend(v)
    pairs, ambiguous = [], []
    for name in sorted(set(ours) & set(rec)):
        if len(ours[name]) > 1 or len(rec[name]) > 1:
            ambiguous.append(name)
        pairs.append((name,
                      os.path.relpath(rec[name][0], REPO).replace("\\", "/"),
                      os.path.relpath(ours[name][0], REPO).replace("\\", "/")))
    return pairs, ambiguous, sorted(set(ours) - set(rec)), sorted(set(rec) - set(ours))


def read(rel):
    with open(os.path.join(REPO, rel), encoding="utf-8", errors="replace") as f:
        return strip_comments(f.read())


def survivors(rec_rel, ours_rel):
    """Lines the recovered file has that ours does not, after (d)-normalisation, ignoring order.

    The multiset is what makes it position-insensitive AND duplicate-safe: three identical calls
    in the original and two in ours leaves exactly one survivor, which is the honest answer."""
    ours_counts = collections.Counter(normalise(l) for l in read(ours_rel))
    ours_lines = [l.strip() for l in read(ours_rel)]
    ours_norm = [normalise(l) for l in ours_lines]
    pool = [n for n in ours_norm if n]

    out, seen = [], collections.Counter()
    for lineno, raw in enumerate(read(rec_rel), 1):
        n = normalise(raw)
        if not n or n in TRIVIAL:
            continue
        seen[n] += 1
        if seen[n] <= ours_counts.get(n, 0):
            continue
        near = difflib.get_close_matches(n, pool, n=1, cutoff=0.6)
        counterpart = ours_lines[ours_norm.index(near[0])] if near else None
        out.append({
            "line": lineno,
            "recovered": raw.strip(),
            "ours": counterpart,
            "similarity": round(difflib.SequenceMatcher(None, n, near[0]).ratio(), 2) if near else 0.0,
        })
    return out


# --------------------------------------------------------------------------- self-test

MUTATIONS = [
    ("change-constant",
     lambda l: re.search(r"=\s*-?\d+(\.\d+)?f?;", l),
     lambda s: re.sub(r"=\s*-?\d+(?:\.\d+)?f?;", "= 999f;", s, count=1)),
    ("flip-operator",
     lambda l: re.search(r"[^<>=!]<[^<=]", l) and re.search(r"\b(if|while|for)\b", l),
     lambda s: s.replace("<", "<=", 1)),
    ("flip-equality",
     lambda l: "==" in l and "if" in l,
     lambda s: s.replace("==", "!=", 1)),
    ("negate-condition",
     lambda l: re.match(r"\s*if \(!", l),
     lambda s: s.replace("(!", "(", 1)),
    ("delete-statement",
     lambda l: l.strip().endswith(";") and "=" in l,
     None),
]


def self_test(pairs):
    """Inject each defect kind into a file that currently has zero survivors, and require the
    detector to report it. Mutation sites must be lines BOTH trees have -- mutating a line we
    added can never produce a survivor, and would score a pass the detector did not earn."""
    clean = [(n, r, o) for n, r, o in pairs if n not in P24_FILES and not survivors(r, o)]

    # Prefer a subject whose two renderings actually DIFFER. A file the two trees agree on
    # byte-for-byte would pass this test without the normaliser ever being consulted, which
    # proves the diff works and says nothing about the filter sitting on top of it.
    def rendering_distance(triple):
        _, rec_rel, ours_rel = triple
        a = {l.strip() for l in read(rec_rel) if l.strip()}
        b = {l.strip() for l in read(ours_rel) if l.strip()}
        return -len(a ^ b)

    clean.sort(key=rendering_distance)
    if not clean:
        print("self-test: no zero-survivor file to mutate", file=sys.stderr)
        return 1

    results = {}
    for name, rec_rel, ours_rel in clean:
        if all(k in results for k, _, _ in MUTATIONS):
            break
        abspath = os.path.join(REPO, ours_rel)
        # Byte-exact round trip. Every file in this tree is CRLF, and a self-test that silently
        # rewrites one as LF has modified the repo to prove the repo is unmodified.
        with open(abspath, "rb") as f:
            original = f.read()
        eol = b"\r\n" if b"\r\n" in original else b"\n"
        lines = original.decode("utf-8", "replace").splitlines()
        rec_norm = {normalise(l) for l in read(rec_rel)}
        shared = [i for i, l in enumerate(lines)
                  if normalise(l) in rec_norm and normalise(l) not in TRIVIAL and normalise(l)]

        for kind, applies, mutate in MUTATIONS:
            if kind in results:
                continue
            idx = next((i for i in shared if applies(lines[i])), None)
            if idx is None:
                continue
            mutated = list(lines)
            site = mutated[idx].strip()
            if mutate is None:
                del mutated[idx]
            else:
                mutated[idx] = mutate(mutated[idx])
            try:
                with open(abspath, "wb") as f:
                    f.write(eol.join(l.encode("utf-8") for l in mutated) + eol)
                found = survivors(rec_rel, ours_rel)
            finally:
                with open(abspath, "wb") as f:
                    f.write(original)
            results[kind] = (name, bool(found), site[:56])

    ok = True
    for kind, _, _ in MUTATIONS:
        if kind not in results:
            print(f"  {kind:<20} NO SITE FOUND -- unverified")
            ok = False
            continue
        name, caught, site = results[kind]
        ok &= caught
        print(f"  {kind:<20} {'RED (caught)' if caught else 'GREEN (MISSED)':<16} {name:<22} {site}")
    print("\nself-test:", "PASS -- every injected defect was reported" if ok else "FAIL")
    return 0 if ok else 1


# --------------------------------------------------------------------------- main

def build(pairs, include_astar):
    files, skipped = {}, []
    for name, rec_rel, ours_rel in pairs:
        if name in P24_FILES and not include_astar:
            skipped.append(name)
            continue
        found = survivors(rec_rel, ours_rel)
        if found:
            files[name] = {"recovered": rec_rel, "ours": ours_rel,
                           "survivors": len(found),
                           "outrightDeletions": sum(1 for c in found if c["ours"] is None),
                           "candidates": found}
    return files, skipped


def check(files, record_path):
    """Regression gate. Two directions, both of which matter, and neither of which may be
    silenced by writing the new number into the record:

      - a candidate that APPEARED is a line the original had, we had, and we have just dropped.
      - a candidate that VANISHED means a gap closed. Shrink the list; never re-pin a count to
        whatever today's run produced, or a fix quietly becomes the new expected state.
    """
    if not os.path.exists(os.path.join(REPO, record_path)):
        print(f"no record at {record_path} -- run without --check first", file=sys.stderr)
        return 1
    with open(os.path.join(REPO, record_path), encoding="utf-8") as f:
        old = json.load(f)["files"]

    def ident(d):
        return {n: {c["recovered"] for c in v["candidates"]} for n, v in d.items()}

    a, b = ident(old), ident(files)
    appeared = {n: sorted(b.get(n, set()) - a.get(n, set())) for n in set(a) | set(b)}
    vanished = {n: sorted(a.get(n, set()) - b.get(n, set())) for n in set(a) | set(b)}
    appeared = {k: v for k, v in appeared.items() if v}
    vanished = {k: v for k, v in vanished.items() if v}

    for label, d in (("APPEARED (a line the original has that we have just stopped having)", appeared),
                     ("VANISHED (a gap closed -- shrink the record, do NOT re-pin a count)", vanished)):
        if d:
            print(f"\n{label}:")
            for n, lines in sorted(d.items()):
                for l in lines:
                    print(f"  {n}: {l[:100]}")
    if not appeared and not vanished:
        print("candidate set unchanged")
        return 0
    return 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default=RECORD)
    ap.add_argument("--self-test", action="store_true",
                    help="inject real defects and require the detector to report them")
    ap.add_argument("--check", action="store_true",
                    help="compare against the recorded candidate set instead of rewriting it")
    ap.add_argument("--include-astar", action="store_true",
                    help="also report the six A* files P24 already classified")
    args = ap.parse_args()

    for root in REC_ROOTS:
        if not os.path.isdir(root):
            sys.stderr.write("recovered tree not extracted -- run tools/extract_recovered.py "
                             f"first.\nmissing: {root}\n")
            return 2

    pairs, ambiguous, ours_only, rec_only = pair_files()
    if args.self_test:
        return self_test(pairs)

    files, skipped = build(pairs, args.include_astar)
    if args.check:
        return check(files, args.json)

    total = sum(v["survivors"] for v in files.values())
    deletions = sum(v["outrightDeletions"] for v in files.values())
    doc = {
        "note": "Lines the recovered original has that our tree does not, after dropping "
                "comments, decompiler-rendering differences and moved lines. NOT a bug list: "
                "each candidate is still (a), (b), (c) or (d) until a person reads it. "
                "Verified by --self-test.",
        "scope": {
            "commonFiles": len(pairs),
            "filesWithCandidates": len(files),
            "candidates": total,
            "outrightDeletions": deletions,
            "skippedAsP24": sorted(skipped),
            "ambiguousBasenames": ambiguous,
            "onlyInOurs": len(ours_only),
            "onlyInRecovered": len(rec_only),
        },
        "files": dict(sorted(files.items(), key=lambda kv: -kv[1]["survivors"])),
    }

    print(f"{'file':<28}{'cand':>6}{'deleted':>9}")
    for name, v in list(doc["files"].items())[:25]:
        print(f"{name:<28}{v['survivors']:>6}{v['outrightDeletions']:>9}")
    print(f"{'TOTAL (' + str(len(files)) + ' files)':<28}{total:>6}{deletions:>9}")

    out = os.path.join(REPO, args.json)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=2)
        f.write("\n")
    print(f"\nwrote {args.json}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
