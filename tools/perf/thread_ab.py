#!/usr/bin/env python3
"""Group a thread_cpu.ps1 sample file by the GpuCostProbe state that was active (P33).

    python tools/perf/thread_ab.py tmp/perf/<client>.log tmp/perf/threads-<client>.jsonl [skip]

The client log's "[ab] <state> at t=Ns" lines count seconds from engine start; the samples carry
seconds since the process started, which leads it by a second or two, so the first `skip`
seconds of every state (default 4) are dropped. Threads are grouped into the main thread,
Unity's render thread, the graphics driver's threads, the job workers and the rest; each row is
the state's mean cores and its difference from "base", with the number of one-second samples.
"""
import json
import re
import sys
from collections import defaultdict

DRIVER = ("nvwgf2um", "nvd3dum", "igd", "amdxx", "atidx", "aticfx", "amdxc")


def group(name: str) -> str:
    low = name.lower()
    if name == "main":
        return "main"
    if name == "UnityGfxDeviceWorker":
        return "render"
    if low.startswith("job.worker") or low.startswith("background job.worker"):
        return "jobs"
    if any(low.startswith(d) for d in DRIVER):
        return "driver"
    return "other"


def main() -> int:
    log, samples = sys.argv[1], sys.argv[2]
    skip = float(sys.argv[3]) if len(sys.argv) > 3 else 4.0
    changes = []
    for line in open(log, encoding="utf-8", errors="replace"):
        m = re.search(r"\[ab\] (\S+(?: \S+)*?) at t=(\d+)s", line)
        if m:
            changes.append((float(m.group(2)), m.group(1)))
    if not changes:
        print("no [ab] state changes in the log")
        return 1

    rows = defaultdict(lambda: defaultdict(list))
    order = []
    for line in open(samples, encoding="utf-8"):
        s = json.loads(line)
        t = s["uptime"]
        current = None
        for at, state in changes:
            if at <= t:
                current = (at, state)
        if current is None or t - current[0] < skip:
            continue
        state = current[1]
        if state not in order:
            order.append(state)
        groups = defaultdict(float)
        for name, cores in s["threads"].items():
            groups[group(name)] += cores
        rows[state]["total"].append(s["total"])
        for g in ("main", "render", "driver", "jobs", "other"):
            rows[state][g].append(groups[g])

    keys = ("total", "main", "render", "driver", "jobs", "other")
    mean = lambda v: sum(v) / len(v) if v else float("nan")
    base = {k: mean(rows["base"][k]) for k in keys} if "base" in rows else None
    print(f"{'state':28}" + "".join(f"{k:>16}" for k in keys) + f"{'n':>5}")
    for state in order:
        cells = []
        for k in keys:
            m = mean(rows[state][k])
            cells.append(f"{m:7.2f}({m - base[k]:+5.2f})" if base and state != "base" else f"{m:16.2f}")
        print(f"{state:28}" + "".join(f"{c:>16}" for c in cells) + f"{len(rows[state]['total']):>5}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
