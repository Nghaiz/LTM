#!/usr/bin/env python3
"""Summarise a frame walked by FrameDebuggerWalk.cs: draws per render target, by shader, by mesh
and by batch-break cause.

    python tools/perf/fd_summary.py tmp/perf/fd-walk.tsv [tmp/perf/fd-causes.txt]
"""
import collections
import csv
import pathlib
import sys


def main() -> int:
    walk = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "tmp/perf/fd-walk.tsv")
    causes_path = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else walk.with_name("fd-causes.txt")
    causes = causes_path.read_text(encoding="utf-8").splitlines() if causes_path.exists() else []
    rows = [r for r in csv.DictReader(walk.open(encoding="utf-8"), delimiter="\t")
            if r["index"] and not r["index"].startswith("#") and r.get("draws") not in (None, "TIMEOUT")]
    total = sum(int(r["draws"] or 0) for r in rows)
    print(f"{len(rows)} events, {total} draw calls")

    by_target = collections.defaultdict(list)
    for r in rows:
        by_target[r["target"] or "(backbuffer)"].append(r)
    for target, events in sorted(by_target.items(), key=lambda kv: -len(kv[1])):
        draws = sum(int(r["draws"] or 0) for r in events)
        print(f"\n== {target}: {len(events)} events, {draws} draw calls ({100 * draws / max(total, 1):.0f}%)")
        for label, key in (("shader", "shader"), ("mesh", "mesh")):
            top = collections.Counter(r[key] or "-" for r in events).most_common(6)
            print(f"   {label}: " + ", ".join(f"{k}={v}" for k, v in top))
        for code, n in collections.Counter(int(r["cause"] or 0) for r in events).most_common(4):
            reason = causes[code] if code < len(causes) else str(code)
            print(f"   {n:5}  cause {code}: {reason[:100]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
