#!/usr/bin/env python3
"""Recount the debt-ledger roll-up from its own row tables.

Section 8 of plans/debt-closure/debt-ledger.md drifted twice from hand-decremented
totals -- once before Phase 2, and again by Phase 8 (Group E carried 11 rows while
the table held 13, Group X carried 29 while the table held 40). The fix is to stop
computing it by hand: this derives every cell from the rows themselves.

Classification reads the status cell (column 3) and is deliberately strict -- a
status this vocabulary does not recognise is reported as UNCLASSIFIED and exits
non-zero, rather than defaulting to a bucket and going quietly wrong.

Usage:
  python tools/recount_debt_ledger.py            # print the recomputed roll-up
  python tools/recount_debt_ledger.py --check    # exit 1 if the file's table disagrees
"""
import re
import sys
import subprocess

LEDGER = "plans/debt-closure/debt-ledger.md"

GROUPS = [
    ("A", "A - authoring"),
    ("B", "B - two clients"),
    ("C", "C - code"),
    ("D", "D - unverified claims"),
    ("E", "E - ops round 8"),
    ("X", "X - found in Phase 0"),
]
BUCKETS = ["open", "closed", "void", "decided", "partial"]

ROW = re.compile(r"^\|\s*\*\*([A-EX]-\d+[a-z]?)\*\*\s*\|")


def classify(status):
    """Map a status cell to a roll-up bucket. Order matters -- see the docstring."""
    s = re.sub(r"[*_`]", "", status).strip().upper()
    if s.startswith("VOID"):
        return "void"
    # A reasoned won't-do is a decision, not a void: the ledger's legend puts
    # "nothing is owed" under DECIDED, and reserves VOID for rows whose subject
    # no longer exists. E-3a (Phase 7) is the first of these.
    if s.startswith("DECIDED") or s.startswith("WON'T-DO"):
        return "decided"
    # A split or partial closure is neither open nor closed; the roll-up has always
    # carried it in its own column (A-2 was the first).
    # The separator is an em dash in the B rows and a hyphen elsewhere; matching only
    # ASCII "-" silently dropped B-8 and B-11 into "open" while A-2 counted as partial.
    if s.startswith("SPLIT") or "PARTIALLY CLOSED" in s or re.search(r"[-–—,:] PARTIAL\b", s):
        return "partial"
    if "CLOSED" in s:
        return "closed"
    if s.startswith("VERIFIED-OPEN") or s.startswith("RUN "):
        # A RUN that did not close the row (FLAKY / BLOCKED / NOT GRADED /
        # UNVERDICTED / PASS-with-caveat) leaves it open. RUN + CLOSED is caught above.
        return "open"
    return None


def parse(path):
    rows, bad = {}, []
    for line in open(path, encoding="utf-8"):
        m = ROW.match(line)
        if not m:
            continue
        rid = m.group(1)
        if rid in rows:
            continue  # first occurrence wins; later mentions are cross-references
        status = line.split("|")[3].strip()
        bucket = classify(status)
        if bucket is None:
            bad.append((rid, status[:90]))
        rows[rid] = bucket
    return rows, bad


def tally(rows):
    out = {}
    for key, _ in GROUPS:
        counts = {b: 0 for b in BUCKETS}
        for rid, bucket in rows.items():
            if rid.split("-")[0] == key:
                counts[bucket] += 1
        counts["total"] = sum(counts[b] for b in BUCKETS)
        out[key] = counts
    return out


def main():
    rows, bad = parse(LEDGER)
    if bad:
        print("UNCLASSIFIED status cells -- extend the vocabulary in classify():")
        for rid, status in bad:
            print(f"  {rid}: {status}")
        return 2

    counts = tally(rows)
    sha = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    dirty = subprocess.run(["git", "status", "--porcelain"], capture_output=True, text=True).stdout.strip()

    print(f"Recomputed from {LEDGER} at {sha[:7]}{' (DIRTY TREE)' if dirty else ''}\n")
    print("| Group | Open | Closed | Void | Decided | Partial | Total |")
    print("|---|---|---|---|---|---|---|")
    grand = {b: 0 for b in BUCKETS + ["total"]}
    for key, label in GROUPS:
        c = counts[key]
        for b in grand:
            grand[b] += c[b]
        cells = " | ".join(str(c[b]) if c[b] else "-" for b in BUCKETS)
        print(f"| {label} | {cells} | {c['total']} |")
    cells = " | ".join(f"**{grand[b]}**" for b in BUCKETS)
    print(f"| **Total** | {cells} | **{grand['total']}** |")

    if "--check" in sys.argv:
        text = open(LEDGER, encoding="utf-8").read()
        want = f"| **Total** | {' | '.join('**%d**' % grand[b] for b in BUCKETS)} | **{grand['total']}** |"
        if want not in text:
            print(f"\nDISAGREES with the roll-up in the file. Expected row:\n{want}")
            return 1
        print("\nRoll-up in the file agrees with the recount.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
