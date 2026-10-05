#!/usr/bin/env python3
"""Group a client log's [render] and [frames] windows by the GpuCostProbe state that was active.

    python tools/perf/probe_attribution.py tmp/perf/<client>.log

The first window after each state change straddles it and is skipped. Each row shows the
state's mean and its difference from "base". Run with IRONFRONT_GPU_PROBE=1 and
IRONFRONT_LOG_FRAMES=1 on a build-player.ps1 -KeepDiagnostics player.
"""
import re
import sys
from collections import defaultdict

state, skip = None, {"render": 0, "frames": 0}
rows = defaultdict(lambda: defaultdict(list))
order = []
num = lambda s: float(s.rstrip("ms"))
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    m = re.search(r"\[ab\] (\S+(?: \S+)*?) at t=\d+s", line)
    if m:
        state = m.group(1)
        skip = {"render": 1, "frames": 1}  # first window straddles the change
        if state not in order:
            order.append(state)
        continue
    if state is None:
        continue
    if "[render]" in line:
        if skip["render"]:
            skip["render"] -= 1
            continue
        for k in ("batches", "setpass", "draws", "shadowCasters", "tris"):
            v = re.search(rf"{k}=(\d+)", line)
            if v:
                rows[state][k].append(float(v.group(1)))
    elif "[frames]" in line:
        if skip["frames"]:
            skip["frames"] -= 1
            continue
        for k in ("fps", "main", "render", "gpu"):
            v = re.search(rf" {k}=([\d.]+)", line)
            if v:
                rows[state][k].append(float(v.group(1)))

avg = lambda xs: sum(xs) / len(xs) if xs else float("nan")
base = rows.get("base", {})
cols = ("batches", "setpass", "draws", "shadowCasters", "fps", "main", "render", "gpu")
print(f"{'state':26}" + "".join(f"{c:>14}" for c in cols) + "   n")
for s in order:
    r = rows[s]
    cells = []
    for c in cols:
        a = avg(r.get(c, []))
        b = avg(base.get(c, []))
        cells.append(f"{a:8.1f}({a - b:+5.0f})" if s != "base" else f"{a:14.1f}")
    print(f"{s:26}" + "".join(f"{x:>14}" for x in cells) + f"   {len(r.get('fps', []))}")
