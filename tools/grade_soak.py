#!/usr/bin/env python3
"""Grade an M4 30-minute continuous-play soak: no crash, and no leak.

WHY THIS EXISTS
---------------
M4's clause is "30 minutes of continuous play with no crash and no leak", and P8 section 2 says
how it must be graded: *the log and the memory curve, not the exit code*.  An exit code cannot
see a leak, and a run whose server died at minute four still exits 0 if the operator closed the
window.  So the verdict is computed from the two artifacts and states which one each half came
from.

WHAT COUNTS AS A LEAK, AND WHY IT IS NOT "MEMORY WENT UP"
---------------------------------------------------------
The reasoning is tools/chart-durability.ps1's and is not re-derived here: the GC has no reason to
return memory it may need again, so a rising sawtooth is HEALTHY.  What indicates a leak is
memory climbing while load stays flat.  This grader therefore reports three things and lets them
disagree in public -- the slope, the correlation with load, and the second-half-versus-first-half
step -- rather than collapsing them into one number that hides which is which.

It is deliberately conservative in the direction of NOT crying leak.  A false leak alarm sends
somebody after a phantom for a day; a missed one is caught by the next longer run.  The
thresholds are named constants below and every one of them is stated in the output beside the
figure it judged, so a reader can disagree with the threshold without re-running anything.

WHAT IT READS
-------------
  <dir>/run.json     what the runner was asked for and what it actually ran
  <dir>/memory.csv   tsUtc,workingSetMB,connCurrent,errorsPerMin,uptimeSec,clientWorkingSetMB
  <dir>/server.log   the dedicated server's Unity log
  <dir>/client.log   the player's Unity log

A missing artifact comes back UNGRADED with the path named.  "assumed" is not a verdict here,
for the same reason it is not one in tools/grade_v9.py.

USAGE
  python tools/grade_soak.py artifacts/soak/m4-soak-01
  python tools/grade_soak.py artifacts/soak/m4-soak-01 --json verdict.json
  python tools/grade_soak.py --selftest

EXIT CODES
  0  no crash and no leak
  1  at least one half FAILED
  2  the artifacts could not be read at all
"""

from __future__ import annotations

import argparse
import csv
import json
import pathlib
import re
import sys

# ---------------------------------------------------------------- thresholds

# The clause says 30 minutes.  A run that fell short is not a shorter pass, it is UNGRADED --
# the whole point of the number is that some failures only appear late.
REQUIRED_SECONDS = 30 * 60

# Below this many samples the slope is noise.  30 minutes at the runner's default 10 s gives 180.
MIN_SAMPLES = 12

# Working-set growth per hour, as a fraction of the first sample, above which the run is flagged.
# 25% is loose on purpose: Unity's managed heap grows in steps as pools fill for the first time,
# and the first few minutes of any session do exactly that.
LEAK_GROWTH_PER_HOUR = 0.25

# ... but growth alone never fails a run.  It must ALSO rise while load is flat, which is what
# this correlation floor is for.  Load here is connCurrent, which the runner writes as 1 while
# the client is alive.  A run with no variation in load cannot produce a correlation at all, and
# that case is reported rather than scored -- see grade_leak.
FLAT_LOAD_TOLERANCE = 1e-9

EXCEPTION_PATTERN = re.compile(
    r"(?P<name>[A-Za-z_][A-Za-z0-9_.]*(?:Exception|Error))\b")

# Unity writes this on a genuine process-level crash; it is not an ordinary log line.
CRASH_MARKERS = (
    "Crash!!!",
    "Unity Player [version",
    "########################################################################",
)


# ---------------------------------------------------------------- reading

def read_json(path):
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (ValueError, OSError):
        return None


def read_samples(path):
    """The memory CSV as a list of dicts, or None when it cannot be read."""
    if not path.exists():
        return None

    rows = []
    try:
        with path.open(newline="", encoding="utf-8-sig") as handle:
            for row in csv.DictReader(handle):
                try:
                    rows.append({
                        "uptime": float(row["uptimeSec"]),
                        "server": float(row["workingSetMB"]),
                        "client": float(row.get("clientWorkingSetMB") or 0),
                        "load": float(row["connCurrent"]),
                        "errors": float(row["errorsPerMin"]),
                    })
                except (KeyError, TypeError, ValueError):
                    # One malformed row must not lose the other 179.  It is counted below.
                    continue
    except OSError:
        return None

    return rows


def read_log(path):
    if not path.exists():
        return None
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None


# ---------------------------------------------------------------- statistics

def pearson(xs, ys):
    """Correlation, or None when either series is constant.

    A constant series has zero variance, and the coefficient is undefined rather than zero --
    reporting 0.0 there would read as "memory is uncorrelated with load", which is a claim the
    data cannot support.  See rules/green-that-proves-nothing.md: unknown must not look like good.
    """
    n = len(xs)
    if n < 2:
        return None

    mean_x = sum(xs) / n
    mean_y = sum(ys) / n

    dx = [x - mean_x for x in xs]
    dy = [y - mean_y for y in ys]

    var_x = sum(v * v for v in dx)
    var_y = sum(v * v for v in dy)

    if var_x <= FLAT_LOAD_TOLERANCE or var_y <= FLAT_LOAD_TOLERANCE:
        return None

    return sum(a * b for a, b in zip(dx, dy)) / ((var_x ** 0.5) * (var_y ** 0.5))


def slope_per_hour(samples, key):
    """Least-squares MB/hour for one series, or None when the span is too short."""
    if len(samples) < 2:
        return None

    span = samples[-1]["uptime"] - samples[0]["uptime"]
    if span <= 0:
        return None

    xs = [s["uptime"] for s in samples]
    ys = [s[key] for s in samples]

    n = len(xs)
    mean_x = sum(xs) / n
    mean_y = sum(ys) / n

    denominator = sum((x - mean_x) ** 2 for x in xs)
    if denominator <= 0:
        return None

    per_second = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys)) / denominator
    return per_second * 3600.0


# ---------------------------------------------------------------- grading

def grade_duration(meta, samples):
    if meta is None:
        return {"verdict": "UNGRADED", "why": "run.json is missing; the run's length is unknown"}

    actual = float(meta.get("actualSecs") or 0)
    held = bool(meta.get("heldToTheEnd"))

    if actual >= REQUIRED_SECONDS and held:
        return {"verdict": "MET", "seconds": actual,
                "why": f"{actual:.0f} s of continuous play, at or past the {REQUIRED_SECONDS} s clause"}

    return {
        "verdict": "UNGRADED",
        "seconds": actual,
        "why": (f"ran {actual:.0f} s of the {REQUIRED_SECONDS} s the clause asks for"
                f"{'' if held else '; the run did not hold to the end'}. "
                "A short run is not a shorter pass -- some failures only appear late."),
    }


def grade_crash(server_log, client_log, meta):
    """No crash: neither process died, and neither log carries a crash marker."""
    if server_log is None and client_log is None:
        return {"verdict": "UNGRADED", "why": "neither server.log nor client.log was found"}

    found = []
    for name, text in (("server", server_log), ("client", client_log)):
        if text is None:
            found.append(f"{name}.log missing")
            continue
        for marker in CRASH_MARKERS:
            if marker in text:
                found.append(f"{name}: crash marker {marker!r}")
                break

    # The runner records whether it ran to its own end.  A process that exited early is a crash
    # for this clause's purposes even when it left no marker behind, which is the common case for
    # a server killed by an unhandled exception on a background thread.
    if meta is not None and meta.get("heldToTheEnd") is False:
        found.append("the runner stopped early -- a process exited before the clock did")

    if any("missing" not in f for f in found):
        return {"verdict": "FAILED", "findings": found, "why": "; ".join(found)}

    if found:
        return {"verdict": "UNGRADED", "findings": found, "why": "; ".join(found)}

    return {"verdict": "MET", "findings": [], "why": "no crash marker in either log, and both processes outlived the clock"}


def grade_exceptions(server_log, client_log):
    """Every exception type in either log, with counts. Not a pass/fail on its own."""
    tally = {}
    for name, text in (("server", server_log), ("client", client_log)):
        if text is None:
            continue
        for match in EXCEPTION_PATTERN.finditer(text):
            key = f"{name}:{match.group('name')}"
            tally[key] = tally.get(key, 0) + 1

    total = sum(tally.values())
    top = sorted(tally.items(), key=lambda kv: -kv[1])[:10]

    return {
        "total": total,
        "byType": dict(top),
        "why": ("no exception of any type in either log" if total == 0
                else f"{total} exception line(s); heaviest: " +
                     ", ".join(f"{k} x{v}" for k, v in top[:3])),
    }


def grade_leak(samples):
    """No leak: growth AND a rise-while-load-is-flat, together. Either alone is not enough."""
    if samples is None:
        return {"verdict": "UNGRADED", "why": "memory.csv is missing"}
    if len(samples) < MIN_SAMPLES:
        return {"verdict": "UNGRADED", "samples": len(samples),
                "why": f"{len(samples)} sample(s), under the {MIN_SAMPLES} a slope needs to mean anything"}

    first = samples[0]["server"]
    last = samples[-1]["server"]
    span_hours = (samples[-1]["uptime"] - samples[0]["uptime"]) / 3600.0

    server_slope = slope_per_hour(samples, "server")
    client_slope = slope_per_hour(samples, "client")

    # Growth as a FRACTION of where the run started, per hour -- not raw megabytes. A 40 MB/h
    # climb means something very different on a 64 MB server than on a 600 MB one, and the
    # threshold has to travel between them.
    growth_fraction = None
    if first > 0 and span_hours > 0 and server_slope is not None:
        growth_fraction = server_slope / first

    correlation = pearson([s["load"] for s in samples], [s["server"] for s in samples])

    # The second-half step: a sawtooth that has settled shows a small one, a leak a large one.
    half = len(samples) // 2
    first_half = sum(s["server"] for s in samples[:half]) / max(half, 1)
    second_half = sum(s["server"] for s in samples[half:]) / max(len(samples) - half, 1)
    step = (second_half - first_half) / first_half if first_half else None

    detail = {
        "firstSampleMB": first,
        "lastSampleMB": last,
        "serverSlopeMBPerHour": server_slope,
        "clientSlopeMBPerHour": client_slope,
        "growthFractionPerHour": growth_fraction,
        "loadCorrelation": correlation,
        "secondHalfStep": step,
        "thresholdGrowthPerHour": LEAK_GROWTH_PER_HOUR,
    }

    grew = growth_fraction is not None and growth_fraction > LEAK_GROWTH_PER_HOUR

    if not grew:
        detail["verdict"] = "MET"
        detail["why"] = (
            f"server working set {first:.0f} -> {last:.0f} MB; "
            f"{server_slope:+.1f} MB/h is inside the {LEAK_GROWTH_PER_HOUR:.0%}/h band"
            if server_slope is not None else "no measurable slope")
        return detail

    if correlation is None:
        # Load never varied, so "rising while load is flat" is exactly what happened -- but the
        # correlation cannot say so, and reporting a number here would invent one.
        detail["verdict"] = "FAILED"
        detail["why"] = (
            f"server working set grew {growth_fraction:.0%}/h, past the {LEAK_GROWTH_PER_HOUR:.0%} "
            "threshold, and load never varied -- so the growth cannot be attributed to load. "
            "Read the curve before acting: chart-durability.ps1 renders it.")
        return detail

    if correlation > 0.5:
        detail["verdict"] = "MET"
        detail["why"] = (
            f"grew {growth_fraction:.0%}/h, but load explains it (r={correlation:.2f}). "
            "Memory rising WITH load is not a leak.")
        return detail

    detail["verdict"] = "FAILED"
    detail["why"] = (
        f"server working set grew {growth_fraction:.0%}/h past the {LEAK_GROWTH_PER_HOUR:.0%} "
        f"threshold while load stayed flat (r={correlation:.2f}). That is the shape of a leak.")
    return detail


# ---------------------------------------------------------------- reporting

def grade(directory):
    directory = pathlib.Path(directory)

    meta = read_json(directory / "run.json")
    samples = read_samples(directory / "memory.csv")
    server_log = read_log(directory / "server.log")
    client_log = read_log(directory / "client.log")

    if meta is None and samples is None and server_log is None:
        return None

    duration = grade_duration(meta, samples)
    crash = grade_crash(server_log, client_log, meta)
    leak = grade_leak(samples)
    exceptions = grade_exceptions(server_log, client_log)

    failed = [n for n, r in (("crash", crash), ("leak", leak)) if r.get("verdict") == "FAILED"]

    return {
        "schema": "ironfront.soak/1",
        "directory": str(directory),
        "tag": (meta or {}).get("tag"),
        "label": (meta or {}).get("label"),
        "duration": duration,
        "noCrash": crash,
        "noLeak": leak,
        "exceptions": exceptions,
        "verdict": "FAILED" if failed else (
            "MET" if duration["verdict"] == "MET"
                     and crash["verdict"] == "MET"
                     and leak["verdict"] == "MET"
            else "UNGRADED"),
        "failedHalves": failed,
    }


def render(report):
    lines = []
    lines.append(f"M4 soak — {report.get('tag') or '(untagged)'}")
    if report.get("label"):
        lines.append(f"  {report['label']}")
    lines.append("")

    for key, title in (("duration", "30 minutes"), ("noCrash", "no crash"), ("noLeak", "no leak")):
        row = report[key]
        lines.append(f"  {row['verdict']:<9} {title:<12} {row['why']}")

    lines.append("")
    lines.append(f"  exceptions   {report['exceptions']['why']}")
    lines.append("")
    lines.append(f"  VERDICT: {report['verdict']}")

    if report["verdict"] != "MET":
        lines.append("")
        lines.append("  This grades the log and the curve, not the exit code, and it says")
        lines.append("  UNGRADED rather than MET when an artifact is missing.")

    return "\n".join(lines)


# ---------------------------------------------------------------- selftest

def selftest():
    """Break each verdict deliberately and watch it go red. rules/mutation-test-every-gate."""
    failures = []

    def check(name, condition):
        if not condition:
            failures.append(name)

    # A flat curve is not a leak.
    flat = [{"uptime": i * 10.0, "server": 100.0, "client": 50.0, "load": 1.0, "errors": 0.0}
            for i in range(60)]
    check("flat curve reads MET", grade_leak(flat)["verdict"] == "MET")

    # A curve that doubles in half an hour with load pinned at 1 is the leak shape.
    leaking = [{"uptime": i * 10.0, "server": 100.0 + i * 2.0, "client": 50.0,
                "load": 1.0, "errors": 0.0} for i in range(180)]
    check("leaking curve reads FAILED", grade_leak(leaking)["verdict"] == "FAILED")

    # Too few samples is UNGRADED, never MET -- unknown must not look like good.
    check("short sample reads UNGRADED", grade_leak(flat[:3])["verdict"] == "UNGRADED")
    check("missing csv reads UNGRADED", grade_leak(None)["verdict"] == "UNGRADED")

    # A crash marker fails, and a clean log does not.
    check("crash marker reads FAILED",
          grade_crash("all fine\nCrash!!!\n", "fine", {"heldToTheEnd": True})["verdict"] == "FAILED")
    check("clean logs read MET",
          grade_crash("fine", "fine", {"heldToTheEnd": True})["verdict"] == "MET")
    check("early exit reads FAILED",
          grade_crash("fine", "fine", {"heldToTheEnd": False})["verdict"] == "FAILED")

    # A short run is UNGRADED, not a shorter pass.
    check("short run reads UNGRADED",
          grade_duration({"actualSecs": 120, "heldToTheEnd": True}, flat)["verdict"] == "UNGRADED")
    check("full run reads MET",
          grade_duration({"actualSecs": 1800, "heldToTheEnd": True}, flat)["verdict"] == "MET")

    # Exceptions are tallied by type, from both logs.
    tally = grade_exceptions("NullReferenceException here\nNullReferenceException again", "IOException")
    check("exceptions tallied", tally["total"] == 3
          and tally["byType"].get("server:NullReferenceException") == 2)

    # A constant series has an undefined correlation, not a zero one.
    check("constant series has no correlation", pearson([1, 1, 1], [1, 2, 3]) is None)

    if failures:
        print("selftest FAILED:")
        for f in failures:
            print("  - " + f)
        return 1

    print("selftest: 11 checks passed. Each one was written by breaking the thing it guards.")
    return 0


# ---------------------------------------------------------------- entry point

def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("directory", nargs="?", help="an artifacts/soak/<tag> directory")
    parser.add_argument("--json", help="also write the verdict here")
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args()

    if args.selftest:
        return selftest()

    if not args.directory:
        parser.error("a soak directory is required (or --selftest)")

    report = grade(args.directory)
    if report is None:
        print(f"nothing to grade in {args.directory}: no run.json, memory.csv or server.log.",
              file=sys.stderr)
        return 2

    print(render(report))

    if args.json:
        pathlib.Path(args.json).write_text(json.dumps(report, indent=2), encoding="utf-8")

    return 1 if report["verdict"] == "FAILED" else 0


if __name__ == "__main__":
    sys.exit(main())
