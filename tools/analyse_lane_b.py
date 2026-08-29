#!/usr/bin/env python3
"""Read one lane-B run directory and print the fields each of the thirteen checks is graded on.

WHY THIS EXISTS. `plans/phases/phase-p4-lane-b-regrade.md` § 3.2 asks every row to "name the
artifact, name the field in it, state the verdict". Doing that by eye over three 16 KB JSONL
files and four 90 KB logs is how a reader reports the field they remembered rather than the one
on disk. This prints the field, from the file, per checkpoint, per client -- so a verdict quotes
a line that can be re-derived by re-running the command.

IT GRADES NOTHING. Every section prints numbers and leaves the verdict to the reader. A script
that decided PASS/FAIL would be a second opinion nobody audited, and the two checks that matter
most here (8 and 9) are human judgment by construction.

Usage:
    python tools/analyse_lane_b.py artifacts/lane-b/p4-combat-01
    python tools/analyse_lane_b.py artifacts/lane-b/p4-vehicle-01 --section alloc
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys

CLIENTS = ("driver", "observer-a", "observer-b")

# One exception line looks like "NullReferenceException: Object reference not set ...". The
# leading token before the colon is the type; grouping on it is P1's run-health rule, which
# counts PER TYPE rather than in total -- 60 of one defect and 60 of sixty are different runs.
EXCEPTION = re.compile(r"^([A-Za-z_][\w.]*Exception)\s*:", re.MULTILINE)

# Printed even at zero. P1 § 4: an absent line reads as "not measured" and a 0 reads as
# "measured and clean", and those must not look alike -- O6 graded a run of 72 ArgumentExceptions
# as "zero throws at any site" because the measurement behind it counted only NullReference.
ALWAYS_REPORT = ("ArgumentException", "NullReferenceException")


def load(run: pathlib.Path, label: str) -> list[dict]:
    path = run / f"{label}-checkpoints.jsonl"
    if not path.exists():
        return []
    return [
        json.loads(line)
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]


def section(title: str) -> None:
    print(f"\n=== {title} ===")


def run_health(run: pathlib.Path) -> None:
    section("run health -- exceptions per type, and the summary exit codes")
    for log in sorted(run.glob("*.log")):
        text = log.read_text(encoding="utf-8", errors="replace")
        counts: dict[str, int] = {k: 0 for k in ALWAYS_REPORT}
        for kind in EXCEPTION.findall(text):
            counts[kind] = counts.get(kind, 0) + 1
        total = sum(counts.values())
        detail = ", ".join(f"{k} x{v}" for k, v in sorted(counts.items(), key=lambda kv: -kv[1]))
        print(f"{log.name:26} exceptions={total:<5} {detail}")

    for summary in sorted(run.glob("*-summary.json")):
        print(f"{summary.name:26} {summary.read_text(encoding='utf-8').strip()}")


def pose(run: pathlib.Path) -> None:
    section("position / rtt / snapshots -- per checkpoint, per client")
    for label in CLIENTS:
        for d in load(run, label):
            la = d.get("localActor") or {}
            print(
                f"{label:11} {d['checkpoint']:22} t={d['elapsedSeconds']:7.2f} "
                f"rtt={d.get('rttMs') or 0:6.1f} snaps={d.get('snapshotsApplied') or 0:6} "
                f"remotes={d.get('remoteActorCount') or 0:3} "
                f"pos=({la.get('x', 0):8.1f},{la.get('y', 0):6.1f},{la.get('z', 0):8.1f})"
            )


def killfeed(run: pathlib.Path) -> None:
    section("check 1 -- fire, hit, kill, killfeed line WITH A NAME")
    for label in CLIENTS:
        for d in load(run, label):
            c = d.get("combat") or {}
            feed = c.get("killfeed") or []
            names = "; ".join(
                f"{e.get('killerName')}({e.get('killerActorId')}) -> "
                f"{e.get('victimName')}({e.get('victimActorId')}) {e.get('cause')}"
                for e in feed
            )
            print(
                f"{label:11} {d['checkpoint']:22} hp={c.get('health')} alive={c.get('alive')} "
                f"shots={c.get('predictedShots')} hits={c.get('hitmarkerHits')} "
                f"kills={c.get('killfeedTotalKills')} named={c.get('namedPlayers')} "
                f"weapon={c.get('weaponId')} | {names or '(feed empty)'}"
            )


def hud(run: pathlib.Path) -> None:
    section("check 2 -- HUD reflects authoritative state (drawn vs the OFFLINE model)")
    for label in CLIENTS:
        for d in load(run, label):
            h = d.get("hud") or {}
            drawn = (
                h.get("blueScoreText"),
                h.get("redScoreText"),
                h.get("blueFlagsText"),
                h.get("redFlagsText"),
            )
            offline = (
                h.get("offlineBlueScore"),
                h.get("offlineRedScore"),
                h.get("offlineBlueFlags"),
                h.get("offlineRedFlags"),
            )
            agree = str(drawn[0]) == str(offline[0]) and str(drawn[1]) == str(offline[1])
            print(
                f"{label:11} {d['checkpoint']:22} drawn={drawn} offline={offline} "
                f"scoresAgree={agree} phase={h.get('phaseText')!r}"
            )


def respawn(run: pathlib.Path) -> None:
    section("check 13 -- death -> input disable -> respawn screen")
    for label in CLIENTS:
        for d in load(run, label):
            c = d.get("combat") or {}
            print(
                f"{label:11} {d['checkpoint']:22} alive={c.get('alive')} hp={c.get('health')} "
                f"localInputEnabled={c.get('localInputEnabled')} driverEnabled={c.get('driverEnabled')} "
                f"canRespawn={c.get('canRespawn')} untilRespawn={c.get('secondsUntilRespawn')}"
            )


def vehicles(run: pathlib.Path) -> None:
    section("check 7 / 12 -- the same vehicle on every client, and its turret")
    order: list[str] = []
    per_cp: dict[str, dict] = {}
    for label in CLIENTS:
        for d in load(run, label):
            cp = d["checkpoint"]
            if cp not in per_cp:
                per_cp[cp] = {"seen": {}, "driven": {}, "seat": {}}
                order.append(cp)
            per_cp[cp]["seen"][label] = {v["id"]: v for v in d.get("vehicles") or []}
            per_cp[cp]["driven"][label] = d.get("drivenVehicleId")
            per_cp[cp]["seat"][label] = d.get("occupiedVehicleId")

    for cp in order:
        rec = per_cp[cp]
        print(f"\n-- {cp}   drivenVehicleId={rec['driven']}  occupiedVehicleId={rec['seat']}")
        ids = sorted({vid for c in rec["seen"].values() for vid in c})
        for vid in ids:
            seen = {lab: c[vid] for lab, c in rec["seen"].items() if vid in c}
            xs = [v["x"] for v in seen.values()]
            ys = [v["y"] for v in seen.values()]
            zs = [v["z"] for v in seen.values()]
            yaws = [v["yaw"] for v in seen.values()]
            tys = [v.get("turretYaw") for v in seen.values() if v.get("turretYaw") is not None]
            spread = max(max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs))
            turret = (
                f"turretYawSpread={max(tys) - min(tys):.3f} over {['%.2f' % t for t in tys]}"
                if tys
                else "turretYaw=null on every client"
            )
            print(
                f"   vehicle {vid:<4} on {len(seen)} client(s)  maxComponentDelta={spread:8.2f} m  "
                f"hullYawSpread={max(yaws) - min(yaws):7.2f} deg  {turret}"
            )
            for lab, v in sorted(seen.items()):
                print(
                    f"      {lab:11} ({v['x']:9.2f},{v['y']:7.2f},{v['z']:9.2f}) "
                    f"yaw={v['yaw']:7.2f} mode={v['mode']:9} turretYaw={v.get('turretYaw')}"
                )


def prediction(run: pathlib.Path) -> None:
    section(
        "check 8 (numeric half) -- reconciliation counters. "
        "`corrections` counts LAG, not mispredicts (X-41)"
    )
    for label in CLIENTS:
        for d in load(run, label):
            la = d.get("localActor") or {}
            print(
                f"{label:11} {d['checkpoint']:22} snaps={d.get('correctionSnaps')} "
                f"blends={d.get('correctionBlends')} posErr={d.get('lastPositionErrorM')} "
                f"angErr={d.get('lastAngleErrorDeg')} mode={d.get('predictionMode')} "
                f"corrections={la.get('corrections')} pending={la.get('pendingInputs')} "
                f"rtt={d.get('rttMs') or 0:.1f}"
            )


def alloc(run: pathlib.Path) -> None:
    section("check 10 -- per-frame allocation. A DIFFERENCE between windows, never one figure")
    for label in CLIENTS:
        for d in load(run, label):
            a = d.get("allocation") or {}
            print(
                f"{label:11} {d['checkpoint']:22} valid={a.get('valid')} "
                f"frames={a.get('frames') or 0:>6} "
                f"bytesPerFrame={a.get('bytesPerFrame') or 0:11.1f} "
                f"max={a.get('maxBytesInAFrame') or 0:>12} driven={d.get('drivenVehicleId')}"
            )


def cameras(run: pathlib.Path) -> None:
    section("check 5 / 6 -- active cameras and presenter ordering")
    for label in CLIENTS:
        for d in load(run, label):
            cams = d.get("activeCameras") or []
            print(
                f"{label:11} {d['checkpoint']:22} cameras={len(cams)} "
                f"{[c['name'] for c in cams]} "
                f"orphanPresenters={d.get('presentersWithNoBootstrapCount')}"
            )


def explosions(run: pathlib.Path) -> None:
    section("check 4 / B-15 -- explosions, the only projectile evidence the record carries")
    for label in CLIENTS:
        for d in load(run, label):
            print(
                f"{label:11} {d['checkpoint']:22} attached={d.get('explosionsAttached')} "
                f"total={d.get('explosionsTotal')} {json.dumps(d.get('explosions'))}"
            )


SECTIONS = {
    "health": run_health,
    "pose": pose,
    "killfeed": killfeed,
    "hud": hud,
    "respawn": respawn,
    "vehicles": vehicles,
    "prediction": prediction,
    "alloc": alloc,
    "cameras": cameras,
    "explosions": explosions,
}


def gate(run: pathlib.Path) -> int:
    """Exit non-zero when any process in the run threw. Returns the exit code.

    This is the detector behind the null-source projectile fix, and it is the shape the phase
    asks for: "the exception count per type is reported for every run, and a run that throws is
    not graded" (phase-p4 § 4 criterion 6). It was observed RED before the fix -- 64 + 29 + 2
    NullReferenceExceptions over three clients of artifacts/lane-b/p4-combat-01 -- so it is a
    check that has been seen failing rather than one that has only ever been seen passing.

    build.log is excluded and named as excluded. It is the Unity Editor's own build transcript,
    not a process under test, and its SocketExceptions come from the package manager talking to
    itself; counting them would make the gate red on every run for a reason no fix could clear,
    which is the fastest way to teach a reader to ignore it.
    """
    logs = sorted(p for p in run.glob("*.log") if p.name != "build.log")
    if not logs:
        print(f"GATE INCONCLUSIVE: no process logs under {run} -- this is not a green.")
        return 2

    total = 0
    for log in logs:
        text = log.read_text(encoding="utf-8", errors="replace")
        counts: dict[str, int] = {k: 0 for k in ALWAYS_REPORT}
        for kind in EXCEPTION.findall(text):
            counts[kind] = counts.get(kind, 0) + 1
        n = sum(counts.values())
        total += n
        detail = ", ".join(f"{k} x{v}" for k, v in sorted(counts.items(), key=lambda kv: -kv[1]))
        print(f"{'FAIL' if n else 'ok  '} {log.name:20} exceptions={n:<5} {detail}")

    print(f"\n{len(logs)} process log(s) checked, build.log excluded by design.")
    if total:
        print(f"GATE RED: {total} exception(s). A run that throws is not a run that grades.")
        return 1
    print("GATE GREEN: 0 exceptions of any type, in every process log.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=pathlib.Path)
    parser.add_argument("--section", action="append", choices=sorted(SECTIONS), default=None)
    parser.add_argument(
        "--gate",
        action="store_true",
        help="exit 1 if any process log carries an exception; print nothing else",
    )
    args = parser.parse_args()

    if not args.run.is_dir():
        print(f"not a run directory: {args.run}", file=sys.stderr)
        return 2

    print(f"# {args.run}")
    if args.gate:
        return gate(args.run)

    for name in args.section or SECTIONS:
        SECTIONS[name](args.run)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
