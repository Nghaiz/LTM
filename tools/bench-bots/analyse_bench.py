"""Summarise bench-run.sh output: one row per (run, server), measured only while bots are in the world.

Usage: python analyse_bench.py <dir with the VM's ~/bench files> <label> [<label> ...]
The window opens 20 s after the last server released its bots and closes when loopback traffic
(the harness) stops, so warmup and teardown never dilute the numbers.
"""
import csv, glob, os, re, statistics, sys
from datetime import datetime, timedelta

D = sys.argv[1]
LABELS = sys.argv[2:]

def ts(s):  # 2026-09-29T11:10:07.897360795Z -> datetime
    return datetime.strptime(s[:19], "%Y-%m-%dT%H:%M:%S")

def pct(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(round(p / 100 * (len(xs) - 1))))] if xs else float("nan")

rows = []
for label in LABELS:
    samples = list(csv.DictReader(open(os.path.join(D, f"{label}.samples.csv"))))
    logs = sorted(glob.glob(os.path.join(D, f"{label}.bench-*.log")))
    # the sampler writes UTC time of day only; the server log carries the date
    first = next(l for l in open(logs[0], encoding="utf-8", errors="replace") if l[:4].isdigit())
    day = first[:10]
    for s in samples:
        s["_t"] = datetime.strptime(f"{day} {s['t']}", "%Y-%m-%d %H:%M:%S")
    rel_times, ends = [], []
    per = {}
    for lg in logs:
        name = os.path.basename(lg)[len(label) + 1:-4]
        text = open(lg, encoding="utf-8", errors="replace").read().splitlines()
        rel = next((l for l in text if "bots released" in l), None)
        if rel is None:
            per[name] = {"error": "no bots released line"}
            continue
        t_rel = ts(rel)
        bots = re.search(r"released: (\d+) for team 0, (\d+) for team 1", rel)
        per[name] = {"t_rel": t_rel, "bots": int(bots.group(1)) + int(bots.group(2)), "lines": text}
        rel_times.append(t_rel)
    if not rel_times:
        continue
    start = max(rel_times) + timedelta(seconds=20)
    # the harness is gone once loopback traffic collapses
    live = [s for s in samples if float(s["lo_kBps"]) > 20]
    end = max(s["_t"] for s in live) - timedelta(seconds=5)
    win = [s for s in samples if start <= s["_t"] <= end]
    busy = [float(s["busy_pct"]) for s in win]
    steal = [float(s["steal_pct"]) for s in win]
    harness = [float(s["harness_cores"]) for s in win]
    tot_cores = [sum(float(v) for k, v in s.items() if k.endswith("_cores") and k != "harness_cores") for s in win]
    for name, info in per.items():
        if "error" in info:
            rows.append((label, name, info["error"]))
            continue
        cores = [float(s[f"{name}_cores"]) for s in win]
        mem = [int(s[f"{name}_mem_MB"]) for s in win]
        fps, ticks, hitch, mx, p99 = [], [], 0, 0.0, 0.0
        errs = {}
        for l in info["lines"]:
            if not l[:4].isdigit():
                continue
            t = ts(l)
            if not (start <= t <= end):
                continue
            m = re.search(r"\[frames\].*fps=([\d.]+) mean=([\d.]+)ms p99=([\d.]+)ms max=([\d.]+)ms hitches=(\d+).*ticks/s=([\d.]+)", l)
            if m:
                fps.append(float(m.group(1))); p99 = max(p99, float(m.group(3)))
                mx = max(mx, float(m.group(4))); hitch += int(m.group(5)); ticks.append(float(m.group(6)))
            for key in ("Exception", "no free id", "pool", "full", "refus", "truncat", "too large", "oversize"):
                if key.lower() in l.lower() and "[frames]" not in l:
                    errs[key] = errs.get(key, 0) + 1
        port = name.rsplit("-", 1)[1]
        mapname = name.split("-")[1].capitalize()
        htxt = open(os.path.join(D, f"{label}.{mapname}-{port}.harness.txt"), encoding="utf-8", errors="replace").read()
        bw = re.search(r"bandwidth\s+(\d+) B/s per client", htxt)
        held = re.search(r"(\d+)/(\d+) client\(s\) held to the end", htxt)
        mal = re.search(r"malformed/unknown\s+(\d+)/(\d+)", htxt)
        rows.append(dict(
            run=label, server=name, bots=info["bots"], n=len(win),
            cores_mean=statistics.mean(cores), cores_p95=pct(cores, 95), cores_max=max(cores),
            mem_max=max(mem), fps_min=min(fps) if fps else None, ticks_min=min(ticks) if ticks else None,
            hitches=hitch, frame_max_ms=mx, p99_max_ms=p99,
            vm_busy_mean=statistics.mean(busy), vm_busy_max=max(busy), steal_max=max(steal),
            servers_total_mean=statistics.mean(tot_cores), harness_mean=statistics.mean(harness),
            bw_client=int(bw.group(1)) if bw else None,
            held=f"{held.group(1)}/{held.group(2)}" if held else "?",
            malformed=f"{mal.group(1)}/{mal.group(2)}" if mal else "?", errs=errs))

hdr = ["run", "server", "bots", "n", "cores_mean", "cores_p95", "cores_max", "mem_max", "fps_min", "ticks_min",
       "hitches", "frame_max_ms", "p99_max_ms", "vm_busy_mean", "vm_busy_max", "steal_max", "servers_total_mean",
       "harness_mean", "bw_client", "held", "malformed", "errs"]
print("\t".join(hdr))
for r in rows:
    if isinstance(r, tuple):
        print("\t".join(map(str, r))); continue
    print("\t".join(f"{r[h]:.3f}" if isinstance(r[h], float) else str(r[h]) for h in hdr))
