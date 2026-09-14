#!/usr/bin/env python3
"""Read one lane-B run directory and print the fields each of the thirteen checks is graded on.

WHY THIS EXISTS. `plans/phases/phase-p4-lane-b-regrade.md` § 3.2 asks every row to "name the
artifact, name the field in it, state the verdict". Doing that by eye over three 16 KB JSONL
files and four 90 KB logs is how a reader reports the field they remembered rather than the one
on disk. This prints the field, from the file, per checkpoint, per client -- so a verdict quotes
a line that can be re-derived by re-running the command.

THE THIRTEEN-CHECK SECTIONS GRADE NOTHING. Each prints numbers and leaves the verdict to the
reader. A script that decided PASS/FAIL for those would be a second opinion nobody audited, and
the two that matter most (8 and 9) are human judgment by construction.

THE `p10` SECTION IS THE EXCEPTION, AND SAYS SO. Protocol 10's rules are arithmetic over two
checkpoints -- "no round left the clip while sprinting", "one press, one shot" -- and leaving
those to a reader is how a two-human checklist stays a two-human checklist. It still prints the
numbers each verdict was computed from, and it reports INCONCLUSIVE rather than PASS whenever
the run cannot answer the question.

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


# --- protocol 10 ------------------------------------------------------------------------
#
# THESE FOUR SECTIONS DO GRADE, and they are the only ones here that do. The module header
# says the rest deliberately do not, and that stands: checks 8 and 9 are human judgment by
# construction. The protocol-10 items are not. "No round leaves the clip while sprinting" and
# "one press, one shot" are arithmetic over two checkpoints, and leaving them to a reader is
# how a two-human checklist stays a two-human checklist. Every verdict below prints the
# numbers it was computed from, so it can be re-derived by eye from the same file.

# One row per weapon a lane-B body can actually be armed with -- `AiActorController`'s
# primaryWeaponNames and gearNames, which are the only names `-Weapon` / `-Gear` can pin.
# The numbers are `Ironfront.Net.Replication.Combat.WeaponCatalog`'s and that file is the SSOT;
# this table is a GRADING AID, imported by nothing at runtime, stated rather than derived
# because a netstandard catalogue is not reachable from python. A row that goes stale surfaces
# as a spacing mismatch to investigate rather than as a silent wrong verdict, because every
# grade prints the observed figure beside the expected one.
P10_WEAPONS = {
    1:  ("RK-44",        0.095, 30, True),
    2:  ("S-IND7",       0.05,  12, True),
    3:  ("S-IND7 [SUP]", 0.05,  12, True),
    4:  ("76 EAGLE",     1.1,    6, True),
    5:  ("BEU AW1",      0.05,   1, False),
    6:  ("SL-DEFENDER",  1.5,    8, False),
    7:  ("FRAG",         1.3,    1, False),
    8:  ("SPEARHEAD",    1.3,    1, False),
    12: ("BIL SCALPEL",  0.2,    1, False),
    13: ("SIGNAL DMR",   0.14,  20, False),
    15: ("RECON LRR",    0.1,   14, True),
}

# How far under the witnesses' own ground the driver must be before its record is refused.
# Bodies drop through the map in roughly 3% of client-runs and a fallen body still writes a
# complete, plausible checkpoint -- four ledger rows were once diagnosed from runs where the
# witness was under the terrain. NOT an absolute altitude: Dustbowl's players sit near y=10 and
# Island's do not, so a constant floor would grade one shipped map and refuse the other. The
# reference is the two witnesses' own median y in the same run, which both maps supply.
P10_FALLEN_METRES = 50.0

# `ClientCombatState.AmmoResyncThreshold`. ReconcileAmmo keeps the PREDICTED clip whenever it
# is within this of the snapshot, so a recorded ammoInClip can sit up to two rounds below the
# server's. Quoted in the semi-auto grade rather than silently widening its band: a tolerance
# that swallows the difference between one shot and three would defeat that grade entirely.
P10_AMMO_RESYNC_TOLERANCE = 2

# `ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS`. Quoted only in failure text -- no grade is
# computed from it, because the programme's own step durations already put every window well
# clear of the block. Named rather than written inline twice so the two copies cannot disagree.
P10_SPRINT_BLOCK_SECONDS = 0.2


def _p10_set(run: pathlib.Path) -> str:
    """Which p10 programme this run is, or "" when it is not one."""
    meta = run / "run.json"
    if meta.exists():
        try:
            named = (json.loads(meta.read_text(encoding="utf-8")) or {}).get("set") or ""
        except (json.JSONDecodeError, OSError):
            named = ""
        if named.startswith("p10-"):
            return named

    # run.json is written LAST, so a run that died mid-programme has none -- and that is exactly
    # the run somebody wants graded. Fall back to the checkpoint names, which the programme
    # itself declares and the recorder writes as it goes.
    seen = {d.get("checkpoint") for d in load(run, "driver")}
    for name, marker in (
        ("p10-sprint", "sprint-fire"),
        ("p10-semi", "press-1"),
        ("p10-auto", "auto-hold"),
        ("p10-reload", "reload-held"),
    ):
        if marker in seen:
            return name
    return ""


def _p10_ground(run: pathlib.Path) -> float | None:
    """The two witnesses' median y. None when neither recorded a position."""
    ys = [
        (d.get("localActor") or {}).get("y")
        for label in ("observer-a", "observer-b")
        for d in load(run, label)
    ]
    ys = sorted(y for y in ys if y is not None)
    return ys[len(ys) // 2] if ys else None


def _p10_usable(run: pathlib.Path, by_name: dict, needed: tuple) -> list:
    """The preconditions every p10 grade rests on.

    A run that fails these is INCONCLUSIVE and never FAIL. A driver that died, or that fell
    through the terrain, says nothing at all about the trigger -- and reading its flat ammo
    count as "the sprint gate held" would be a green that proves nothing.
    """
    out = []
    ground = _p10_ground(run)
    for cp in needed:
        d = by_name[cp]
        combat = d.get("combat") or {}
        y = (d.get("localActor") or {}).get("y")

        if combat.get("driverEnabled") is False:
            out.append(("INCONCLUSIVE", f"{cp}: driverEnabled=false -- NetClientPresenterGuard "
                                        f"disabled the combat driver, so every combat field "
                                        f"below it reads zero for a reason that is not gameplay"))
        if combat.get("alive") is False:
            out.append(("INCONCLUSIVE", f"{cp}: alive=false -- the server walks idle bodies, so "
                                        f"only the stretch before the first death grades"))
        if ground is not None and y is not None and y < ground - P10_FALLEN_METRES:
            out.append(("INCONCLUSIVE", f"{cp}: y={y:.1f} is {ground - y:.1f} m under the "
                                        f"witnesses' median {ground:.1f} -- graded body is "
                                        f"below the map"))
    return out


def _p10_window(by_name: dict, start: str, end: str) -> dict:
    """What moved between two checkpoints.

    Checkpoints are captured ON ENTERING a step (`ScriptedInputStep.checkpoint`), so the window
    [start, end] is exactly the step `start` names. Every p10 rule is about an interval -- "no
    round left the clip WHILE sprinting" -- and reading either endpoint alone answers a
    different question.

    ROUNDS ARE COUNTED OFF `serverAmmoInClip`, NEVER OFF `ammoInClip`, and the difference is
    not a refinement. `ammoInClip` is the CLIENT's predicted clip and `ClientCombatState.
    ReconcileAmmo` deliberately KEEPS that prediction whenever it is within
    `AmmoResyncThreshold` of the snapshot -- so the bias is sticky, never converges, and a
    subtraction over it is not a round count at all. Reading it produced both errors in one
    afternoon on 2026-09-14: a FAIL on a semi-auto press where the server had correctly fired
    once, and -- far worse -- a PASS on a sprint window where the server fired NOTHING (97
    attempts, 97 refused `Holstered`) because the predicted clip happened to dip by one. A red
    gets investigated; a green ends the question.

    `spent` is None when the recorder did not write the field. Callers MUST check `serverKnown`
    first: an older artifact cannot answer the question and `_p10_unreadable` is the verdict for
    it. The predicted numbers are still carried, as `predictedSpent`/`clipBefore`/`clipAfter`,
    because prediction disagreeing with the server is now a real signal rather than noise.
    """
    before = by_name[start].get("combat") or {}
    after = by_name[end].get("combat") or {}

    server_before, server_after = before.get("serverAmmoInClip"), after.get("serverAmmoInClip")
    known = server_before is not None and server_after is not None

    return {
        "seconds": by_name[end]["elapsedSeconds"] - by_name[start]["elapsedSeconds"],
        "shots": (after.get("predictedShots") or 0) - (before.get("predictedShots") or 0),
        "spent": (server_before - server_after) if known else None,
        "serverKnown": known,
        "serverBefore": server_before,
        "serverAfter": server_after,
        "predictedSpent": (before.get("ammoInClip") or 0) - (after.get("ammoInClip") or 0),
        "corrections": (after.get("ammoCorrections") or 0) - (before.get("ammoCorrections") or 0),
        "clipBefore": before.get("ammoInClip"),
        "clipAfter": after.get("ammoInClip"),
    }


def _p10_unreadable(window: dict, title: str) -> tuple:
    """The verdict for a window whose authoritative clip this build's recorder never wrote.

    INCONCLUSIVE rather than a grade off the predicted clip, and rather than a widened band. A
    tolerance big enough to swallow the reconcile threshold spans 0..3 rounds around an expected
    1, which does not distinguish a correct single shot from a triple -- it would not be a
    weaker grade, it would be no grade wearing one's clothes.
    """
    return ("INCONCLUSIVE",
            f"{title} ({window['seconds']:.1f}s): combat.serverAmmoInClip is absent -- this "
            f"build's LaneBCheckpointRecorder predates it, so the only clip on record is the "
            f"CLIENT's prediction and no round count can be trusted. For a reader: ammoInClip "
            f"{window['clipBefore']} -> {window['clipAfter']} ({window['predictedSpent']} "
            f"round(s)), predictedShots +{window['shots']}, ammoCorrections "
            f"+{window['corrections']} -- that clip may sit up to {P10_AMMO_RESYNC_TOLERANCE} "
            f"rounds off the server's in either direction, and a NON-ZERO ammoCorrections is "
            f"exactly when it has been overwritten and is least trustworthy. Re-run on a build "
            f"that writes serverAmmoInClip")


def _p10_weapon(by_name: dict, needed: tuple):
    """The drawn weapon and its catalogue row, from the first checkpoint that names one."""
    for cp in needed:
        weapon_id = (by_name[cp].get("combat") or {}).get("weaponId")
        if weapon_id:
            return weapon_id, P10_WEAPONS.get(weapon_id)
    return 0, None


def _p10_missing(by_name: dict, needed: tuple) -> list:
    absent = [cp for cp in needed if cp not in by_name]
    if not absent:
        return []
    return [("INCONCLUSIVE", f"the driver never recorded {absent} -- the run did not reach the "
                             f"end of its programme, so there is no window to grade")]


def _p10_grade_sprint(run: pathlib.Path, by_name: dict) -> list:
    """§ 14 item: fire while sprinting, then stop sprinting and keep firing.

    Reads `combat.ammoInClip`, `combat.predictedShots` and `combat.ammoCorrections` on the
    DRIVER, across two windows. FAILS when a round leaves the clip during the sprint window,
    when the client predicted shots the server refused, or when no shot lands after the sprint
    ends (which would mean `SPRINT_FIRE_BLOCK_SECONDS` never expired).
    """
    needed = ("sprint-fire", "sprint-ended", "fire-clear", "settled")
    if (stop := _p10_missing(by_name, needed)):
        return stop

    out = _p10_usable(run, by_name, needed)
    blocked = _p10_window(by_name, "sprint-fire", "sprint-ended")
    freed = _p10_window(by_name, "fire-clear", "settled")

    # THE HEADLINE. ServerCombatAuthority refuses the trigger while Sprint is pressed and for
    # SPRINT_FIRE_BLOCK_SECONDS after it, so a round leaving the clip here is the magazine
    # draining with no muzzle flash -- the exact defect protocol 10 closed.
    if not blocked["serverKnown"]:
        out.append(_p10_unreadable(blocked, "sprint window"))
    elif blocked["spent"] > 0:
        out.append(("FAIL", f"sprint window ({blocked['seconds']:.1f}s of fire+sprint): "
                            f"serverAmmoInClip {blocked['serverBefore']} -> "
                            f"{blocked['serverAfter']}, {blocked['spent']} round(s) spent while "
                            f"sprinting -- the gate let a trigger through"))
    elif blocked["shots"] > 0:
        out.append(("FAIL", f"sprint window ({blocked['seconds']:.1f}s): serverAmmoInClip held "
                            f"at {blocked['serverBefore']} but predictedShots "
                            f"+{blocked['shots']} -- the client predicted shots the server "
                            f"refused, which is the same disagreement pointed the other way"))
    else:
        out.append(("PASS", f"sprint window ({blocked['seconds']:.1f}s of fire+sprint): "
                            f"serverAmmoInClip unchanged at {blocked['serverBefore']}, "
                            f"predictedShots +0, ammoCorrections +{blocked['corrections']}"))

    # § 14 item 4 names the RESERVE beside the clip, and they are not the same field: a gate
    # that refunded the round after taking it would leave ammoInClip flat and spareAmmoRounds
    # short. Read the kind first -- spareAmmoRounds is 0 for both sentinels and means nothing
    # on its own.
    kind = (by_name["sprint-fire"].get("combat") or {}).get("spareAmmoKind")
    before = (by_name["sprint-fire"].get("combat") or {}).get("spareAmmoRounds")
    after = (by_name["sprint-ended"].get("combat") or {}).get("spareAmmoRounds")
    if kind is None:
        out.append(("INCONCLUSIVE", "combat.spareAmmoKind is absent from the record -- this "
                                    "build's recorder predates the protocol-10 contract, so the "
                                    "reserve half of this item cannot be read"))
    elif kind != "finite":
        out.append(("INCONCLUSIVE", f"spareAmmoKind={kind!r}: the reserve cannot move for this "
                                    f"kind, so a flat spareAmmoRounds here is not evidence the "
                                    f"sprint gate protected it"))
    elif after == before:
        out.append(("PASS", f"reserve held across the sprint window: spareAmmoKind='finite', "
                            f"spareAmmoRounds unchanged at {before}"))
    else:
        out.append(("FAIL", f"spareAmmoRounds went {before} -> {after} during the sprint window "
                            f"-- a round left the reserve while the trigger was gated"))

    # `or freed["shots"] > 0` used to be the second half of this condition, and it is the
    # measured false PASS: on an Island p10-sprint run the server fired NOTHING for the whole
    # programme -- 97 [shot] attempts, 97 refused Holstered, because the weapon never came back
    # up after the sprint -- and this printed "PASS post-sprint window: ammoInClip 30 -> 29,
    # predictedShots +34". A predicted shot is the client's own guess; it can never be evidence
    # that the server fired, and a window whose whole claim is "the weapon is up again" must
    # read the one clip no prediction touches.
    if not freed["serverKnown"]:
        out.append(_p10_unreadable(freed, "post-sprint window"))
    elif freed["spent"] > 0:
        out.append(("PASS", f"post-sprint window ({freed['seconds']:.1f}s of fire): "
                            f"serverAmmoInClip {freed['serverBefore']} -> "
                            f"{freed['serverAfter']}, {freed['spent']} round(s) spent; "
                            f"predictedShots +{freed['shots']}, ammoCorrections "
                            f"+{freed['corrections']}"))
    elif freed["shots"] > 0:
        out.append(("FAIL", f"post-sprint window ({freed['seconds']:.1f}s of fire): the SERVER "
                            f"fired nothing -- serverAmmoInClip held at {freed['serverBefore']} "
                            f"while the client predicted +{freed['shots']} shot(s) and its own "
                            f"ammoInClip went {freed['clipBefore']} -> {freed['clipAfter']}. "
                            f"That dip is the prediction being handed back, not a round leaving "
                            f"the gun. The sprint block is "
                            f"{P10_SPRINT_BLOCK_SECONDS}s and this window "
                            f"opens 1 s after the sprint ended, so the weapon should be up -- "
                            f"check the shot log for rejection=Holstered"))
    else:
        out.append(("FAIL", f"post-sprint window ({freed['seconds']:.1f}s of fire): nothing "
                            f"fired and nothing was even predicted -- serverAmmoInClip held at "
                            f"{freed['serverBefore']} and predictedShots +0. The sprint block "
                            f"is {P10_SPRINT_BLOCK_SECONDS}s and this window "
                            f"opens 1 s after the sprint ended, so the weapon should be up"))
    return out


def _p10_grade_semi(run: pathlib.Path, by_name: dict) -> list:
    """§ 14 item: hold fire on a SEMI-AUTOMATIC, release, press again.

    Reads `combat.ammoInClip` on the DRIVER across three windows: the first long press, the
    release, and the second press. Each press must spend EXACTLY ONE round -- that is the
    rising-edge rule in `EffectiveTriggerPolicy.Advance`, which returns `risingEdge` rather
    than `effective` when the weapon is not automatic.
    """
    needed = ("press-1", "released", "press-2", "settled")
    if (stop := _p10_missing(by_name, needed)):
        return stop

    out = _p10_usable(run, by_name, needed)
    weapon_id, row = _p10_weapon(by_name, needed)
    if row is None:
        out.append(("INCONCLUSIVE", f"weaponId={weapon_id} is not a weapon this grade knows; "
                                    f"run with -Weapon 'SIGNAL DMR' or 'SL-DEFENDER'"))
    elif row[3]:
        return out + [("INCONCLUSIVE", f"weaponId={weapon_id} ({row[0]}) is AUTOMATIC, so one "
                                       f"press is meant to spend many rounds. This programme "
                                       f"grades the semi-auto edge; re-run with -Weapon "
                                       f"'SIGNAL DMR' or 'SL-DEFENDER'")]

    windows = (
        ("first press", _p10_window(by_name, "press-1", "released"), 1),
        ("trigger released", _p10_window(by_name, "released", "press-2"), 0),
        ("second press", _p10_window(by_name, "press-2", "settled"), 1),
    )
    for title, window, expected in windows:
        if not window["serverKnown"]:
            out.append(_p10_unreadable(window, title))
        elif window["spent"] == expected:
            out.append(("PASS", f"{title} ({window['seconds']:.1f}s): serverAmmoInClip "
                                f"{window['serverBefore']} -> {window['serverAfter']}, "
                                f"{window['spent']} round spent, expected {expected}; the "
                                f"client predicted +{window['shots']} with "
                                f"+{window['corrections']} correction(s)"))
        elif window["spent"] == 0 and window["shots"] > 0:
            out.append(("FAIL", f"{title} ({window['seconds']:.1f}s): the SERVER spent nothing "
                                f"-- serverAmmoInClip held at {window['serverBefore']} while "
                                f"the client predicted +{window['shots']} shot(s) and its own "
                                f"ammoInClip went {window['clipBefore']} -> "
                                f"{window['clipAfter']}. Expected {expected}. That dip is the "
                                f"prediction being handed back, not a round leaving the gun"))
        else:
            out.append(("FAIL", f"{title} ({window['seconds']:.1f}s): serverAmmoInClip "
                                f"{window['serverBefore']} -> {window['serverAfter']}, "
                                f"{window['spent']} round(s) spent, expected {expected}; "
                                f"predictedShots +{window['shots']}, ammoCorrections "
                                f"+{window['corrections']}. This is the server's own clip, so "
                                f"prediction slack cannot explain it"))
    return out


def _p10_grade_auto(run: pathlib.Path, by_name: dict) -> list:
    """§ 14 item: hold fire on an AUTOMATIC and grade the SPACING, never a count.

    Reads `combat.ammoInClip` and `elapsedSeconds` on the DRIVER across the hold window. A
    fixed shot count would pin the arithmetic rather than the rule: 30 ticks at a 0.1 s
    cooldown is 9 or 10 shots depending on how 1/30 accumulates in a float. So the window's
    own seconds are divided by its own rounds and the result is bracketed against
    `WeaponCatalog`'s cooldown.
    """
    needed = ("auto-hold", "released")
    if (stop := _p10_missing(by_name, needed)):
        return stop

    out = _p10_usable(run, by_name, needed)
    weapon_id, row = _p10_weapon(by_name, needed)
    if row is None:
        return out + [("INCONCLUSIVE", f"weaponId={weapon_id} is not a weapon this grade knows; "
                                       f"run with -Weapon 'RK-44'")]
    name, cooldown, clip_size, automatic = row
    if not automatic:
        return out + [("INCONCLUSIVE", f"weaponId={weapon_id} ({name}) is SEMI-automatic, so one "
                                       f"press is meant to spend one round. Re-run with "
                                       f"-Weapon 'RK-44' to grade cadence")]

    hold = _p10_window(by_name, "auto-hold", "released")
    if not hold["serverKnown"]:
        return out + [_p10_unreadable(hold, "auto window")]

    rounds, seconds = hold["spent"], hold["seconds"]

    # An automatic that predicted a magazine the server refused would otherwise divide a real
    # number of seconds by an imaginary number of rounds and report a plausible cadence. The
    # cadence below is only a statement about the SERVER's rate, so it is computed from the
    # server's clip and this is the arm that catches a refusal outright.
    if rounds <= 0 and hold["shots"] > 0:
        return out + [("FAIL", f"auto window ({seconds:.1f}s of held fire): the SERVER spent "
                               f"nothing -- serverAmmoInClip held at {hold['serverBefore']} "
                               f"while the client predicted +{hold['shots']} shot(s) and its "
                               f"own ammoInClip went {hold['clipBefore']} -> "
                               f"{hold['clipAfter']}. There is no cadence to measure: every "
                               f"attempt was refused. Check the shot log for the rejection")]

    if rounds <= 1:
        return out + [("FAIL", f"auto window ({seconds:.1f}s of held fire): serverAmmoInClip "
                               f"{hold['serverBefore']} -> {hold['serverAfter']}, {rounds} round(s) "
                               f"spent. An automatic held for {seconds:.1f}s at {name}'s "
                               f"{cooldown}s cooldown should sustain; one round or none is the "
                               f"rising-edge rule applied to a weapon that is not semi-automatic")]
    if rounds >= clip_size:
        return out + [("FAIL", f"auto window ({seconds:.1f}s): the clip BOTTOMED OUT -- "
                               f"{rounds} of {clip_size} rounds gone, so the window is censored "
                               f"by the magazine and the true cadence is at least this fast. At "
                               f"{name}'s {cooldown}s cooldown only "
                               f"~{seconds / cooldown:.0f} rounds were due")]

    # Two honest readings of the same window, because the first shot's position inside it is not
    # recorded: n shots span n-1 gaps if the first is at the edge, n if it is one cooldown in.
    # The catalogue figure must fall inside the pair, widened by a quarter for jitter -- the
    # window is bounded by a checkpoint capture, not by a shot.
    tight, loose = seconds / rounds, seconds / (rounds - 1)
    low, high = min(tight, loose) * 0.75, max(tight, loose) * 1.25
    observed = (f"{rounds} rounds over {seconds:.2f}s = {tight:.4f}s/shot "
                f"(or {loose:.4f}s over {rounds - 1} gaps)")

    if low <= cooldown <= high:
        out.append(("PASS", f"auto cadence: {observed}; {name}'s catalogue cooldown {cooldown}s "
                            f"falls inside [{low:.4f}, {high:.4f}]"))
    else:
        out.append(("FAIL", f"auto cadence: {observed}; {name}'s catalogue cooldown {cooldown}s "
                            f"is OUTSIDE [{low:.4f}, {high:.4f}]. A spacing near "
                            f"{1 / 30:.4f}s is one shot per input frame, which is the trigger "
                            f"reading the raw Fire bit instead of the cooldown"))
    return out


def _p10_grade_reload(run: pathlib.Path, by_name: dict) -> list:
    """§ 14 item: a clip-of-one weapon -- fire, reload, then keep holding reload.

    Reads `combat.ammoInClip`, `combat.serverReloading`, `combat.spareAmmoKind` and
    `combat.spareAmmoRounds` on the DRIVER. The clip must refill ONCE and the reserve must move
    ONCE, and continuing to hold reload afterwards must grant nothing further.

    `spareAmmoKind` is read BEFORE `spareAmmoRounds` and decides which rule applies, because
    `spareAmmoRounds` is 0 for both sentinels -- a reader who believes it without checking the
    kind grades an infinite reserve as an exhausted one.
    """
    needed = ("dry", "empty", "reload-running", "reload-held", "still-held", "settled")
    if (stop := _p10_missing(by_name, needed)):
        return stop

    out = _p10_usable(run, by_name, needed)
    empty = by_name["empty"].get("combat") or {}
    done = by_name["still-held"].get("combat") or {}
    final = by_name["settled"].get("combat") or {}
    clip_size = empty.get("clipSize")

    # 1. The weapon actually went dry. Nothing below means anything if it did not.
    shot = _p10_window(by_name, "dry", "empty")
    if empty.get("ammoInClip") != 0:
        return out + [("INCONCLUSIVE", f"ammoInClip is {empty.get('ammoInClip')} at 'empty', not "
                                       f"0 -- the weapon never went dry, so the reload under "
                                       f"test never had anything to do. clipSize={clip_size}; "
                                       f"run with -Gear 'BEU AW1' for a clip of one")]
    out.append(("PASS", f"went dry: ammoInClip {shot['clipBefore']} -> 0 over the firing step, "
                        f"clipSize={clip_size}"))

    # 2. The SERVER says it reloaded. RELOAD_SECONDS is 2 s and both of these checkpoints sit
    #    inside that window (0.5 s and 1.5 s in), so a reload that ran is caught by one of them.
    running = [cp for cp in ("reload-running", "reload-held")
               if (by_name[cp].get("combat") or {}).get("serverReloading") is True]
    if (by_name["reload-running"].get("combat") or {}).get("serverReloading") is None:
        out.append(("INCONCLUSIVE", "combat.serverReloading is absent from the record -- this "
                                    "build's LaneBCheckpointRecorder predates the protocol-10 "
                                    "contract, so the authoritative reload flag cannot be read"))
    elif running:
        out.append(("PASS", f"server reload observed: serverReloading=true at {running}"))
    else:
        out.append(("FAIL", "serverReloading was false at both 'reload-running' (0.5 s in) and "
                            "'reload-held' (1.5 s in), yet RELOAD_SECONDS is 2 s -- the held "
                            "reload never started on the server"))

    # 3. Refilled ONCE. Full at 'still-held' (4 s after the reload was due) and STILL exactly
    #    full at 'settled', 4 s later, with reload held down the whole time.
    if done.get("ammoInClip") == clip_size and final.get("ammoInClip") == clip_size:
        out.append(("PASS", f"clip refilled once: 0 -> {clip_size} by 'still-held' and still "
                            f"{clip_size} at 'settled' after 4 more seconds of held reload"))
    elif done.get("ammoInClip") != clip_size:
        out.append(("FAIL", f"ammoInClip is {done.get('ammoInClip')} at 'still-held', not "
                            f"{clip_size} -- the reload did not complete"))
    else:
        out.append(("FAIL", f"ammoInClip moved {done.get('ammoInClip')} -> "
                            f"{final.get('ammoInClip')} between 'still-held' and 'settled' "
                            f"while reload stayed held -- a second grant"))

    # 4. The reserve. `spareAmmoKind` decides the rule; `spareAmmoRounds` is 0 for BOTH
    #    sentinels and means nothing on its own.
    kind = empty.get("spareAmmoKind")
    if kind is None:
        out.append(("INCONCLUSIVE", "combat.spareAmmoKind is absent from the record -- this "
                                    "build's recorder predates the protocol-10 contract, so no "
                                    "reserve rule can be graded"))
        return out

    before, after, last = (empty.get("spareAmmoRounds"), done.get("spareAmmoRounds"),
                           final.get("spareAmmoRounds"))
    if kind == "finite":
        if before is not None and after == before - 1 and last == after:
            out.append(("PASS", f"reserve spent once: spareAmmoKind='finite', spareAmmoRounds "
                                f"{before} -> {after}, unchanged at 'settled'"))
        else:
            out.append(("FAIL", f"spareAmmoKind='finite' and spareAmmoRounds went {before} -> "
                                f"{after} -> {last}; one reload must spend exactly one round "
                                f"and the held reload after it must spend none"))
    elif kind == "infinite":
        if after == before:
            out.append(("PASS", f"reserve held: spareAmmoKind='infinite', spareAmmoRounds "
                                f"{before} -> {after}. ActorSpareAmmoPool.Take returns the "
                                f"count without decrementing an infinite slot, which is the "
                                f"rule for this kind"))
        else:
            out.append(("FAIL", f"spareAmmoKind='infinite' but spareAmmoRounds moved {before} -> "
                                f"{after} -- an infinite reserve must not decrement"))
        out.append(("INCONCLUSIVE", "the N -> N-1 decrement is NOT exercised by this run, and "
                                    "cannot be on this build: WeaponCatalog passes no spareAmmo "
                                    "for any loadout weapon, so every entry takes the "
                                    "InfiniteSpareAmmo default and ServerCombatBridge."
                                    "SeedSpareAmmo copies it into the pool. Only CAR_HORN is "
                                    "NoResupply and it is not in any loadout. Grading this arm "
                                    "needs a catalogue row with a finite spareAmmo first"))
    elif kind == "no-resupply":
        if done.get("ammoInClip") == 0:
            out.append(("PASS", "spareAmmoKind='no-resupply' and the clip stayed 0 -- a reload "
                                "with nothing to draw from grants nothing"))
        else:
            out.append(("FAIL", f"spareAmmoKind='no-resupply' but the clip refilled to "
                                f"{done.get('ammoInClip')} -- rounds came from a reserve that "
                                f"does not resupply"))
    else:
        out.append(("INCONCLUSIVE", f"spareAmmoKind={kind!r} is not one of 'finite', "
                                    f"'no-resupply' or 'infinite'"))
    return out


P10_GRADES = {
    "p10-sprint": _p10_grade_sprint,
    "p10-semi": _p10_grade_semi,
    "p10-auto": _p10_grade_auto,
    "p10-reload": _p10_grade_reload,
}


def _p10_verdicts(run: pathlib.Path):
    """(set name, verdicts). An empty name means the run is not a p10 run at all."""
    named = _p10_set(run)
    if not named:
        return "", []

    rows = load(run, "driver")
    if not rows:
        return named, [("INCONCLUSIVE", "driver-checkpoints.jsonl is absent or empty -- the "
                                        "client that fires is the one every p10 grade reads")]
    return named, P10_GRADES[named](run, {d["checkpoint"]: d for d in rows})


def p10(run: pathlib.Path) -> None:
    section("protocol 10 -- sprint gate, semi-auto edge, automatic cadence, reload reserve")
    named, verdicts = _p10_verdicts(run)
    if not named:
        print("not a p10 run: run.json names no p10 set and no p10 checkpoint was recorded.")
        return

    rows = load(run, "driver")
    by_name = {d["checkpoint"]: d for d in rows}
    weapon_id, row = _p10_weapon(by_name, tuple(by_name))
    print(f"set={named}  driver weaponId={weapon_id} "
          f"({row[0] if row else 'not in the grading table'})")
    for verdict, text in verdicts:
        print(f"{verdict:14} {text}")


def p10_gate(run: pathlib.Path) -> int:
    """Exit 0 when every protocol-10 rule held, 1 on any FAIL, 2 when nothing could be graded.

    INCONCLUSIVE is deliberately NOT green. A driver that fell through the terrain, a recorder
    that predates the contract and a reserve the catalogue cannot make finite all produce a run
    with no failures in it, and none of the three is evidence that the trigger works.
    """
    named, verdicts = _p10_verdicts(run)
    if not named:
        print(f"P10 GATE INCONCLUSIVE: {run} is not a p10 run -- this is not a green.")
        return 2

    for verdict, text in verdicts:
        print(f"{verdict:14} {text}")

    failed = sum(1 for v, _ in verdicts if v == "FAIL")
    unknown = sum(1 for v, _ in verdicts if v == "INCONCLUSIVE")
    passed = sum(1 for v, _ in verdicts if v == "PASS")
    print(f"\n{named}: {passed} passed, {failed} failed, {unknown} inconclusive.")

    if failed:
        print("P10 GATE RED: a protocol-10 rule did not hold.")
        return 1
    if unknown or not passed:
        print("P10 GATE INCONCLUSIVE: nothing failed, but the run does not prove the rule "
              "either. Read the lines above before reporting a pass.")
        return 2
    print("P10 GATE GREEN: every rule this programme states was observed to hold.")
    return 0


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
    "p10": p10,
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


# --------------------------------------------------------------------------- the grader's own tests
#
# WHY THESE LIVE IN THE TOOL. The p10 grade is the one part of this file that decides rather
# than prints, and on 2026-09-14 it decided wrongly in BOTH directions within one afternoon --
# a FAIL on a semi-auto press the server had fired correctly, and a PASS on a sprint window
# where the server fired nothing at all. A deciding check with no test is a check nobody has
# ever seen fail. There is no Python test project in this repo, so the suite is a mode of the
# tool; `ClientLaneBGraderTests` in Ironfront.Net.Replication.Tests runs it, which is what keeps
# it from being a file nobody executes.
#
# Every case is built from a SYNTHETIC run rather than from an artifact on disk: an artifact
# pins whatever the build that produced it happened to do, and the cases that matter here are
# the ones no build has produced yet.


def _st_cp(name, seconds, server, predicted, shots, weapon=13, corrections=0, **extra):
    """One checkpoint record. `server=None` omits serverAmmoInClip -- an older recorder."""
    combat = {
        "driverEnabled": True, "alive": True, "weaponId": weapon,
        "ammoInClip": predicted, "clipSize": 20, "serverAmmoInClip": server,
        "predictedShots": shots, "ammoCorrections": corrections,
        "spareAmmoKind": "finite", "spareAmmoRounds": 90, "serverReloading": False,
    }
    if server is None:
        del combat["serverAmmoInClip"]
    combat.update(extra)
    return {"checkpoint": name, "elapsedSeconds": seconds, "combat": combat}


def _st_run(tmp: pathlib.Path, label: str, programme: str, checkpoints: list) -> pathlib.Path:
    run = tmp / label
    run.mkdir()
    (run / "run.json").write_text(json.dumps({"set": programme}), encoding="utf-8")
    (run / "driver-checkpoints.jsonl").write_text(
        "\n".join(json.dumps(c) for c in checkpoints), encoding="utf-8")
    return run


def _st_semi(first=(20, 19), second=(19, 18), predicted=(18, 18, 18, 18), shots=(0, 1, 1, 2),
             server=True):
    """A p10-semi programme: one round per press is correct.

    `predicted` defaults to a clip frozen two rounds under the server's for the whole run --
    the measured artifact, and the exact shape that made the old grade print "first press: 1
    round PASS" and "second press: 2 rounds FAIL" off identical correct behaviour.
    """
    s = [first[0], first[1], second[0], second[1]] if server else [None] * 4
    names = ("press-1", "released", "press-2", "settled")
    return [_st_cp(n, i * 5.0, s[i], predicted[i], shots[i]) for i, n in enumerate(names)]


def _st_sprint(server_clip=(30, 30, 30, 29), predicted=(30, 30, 30, 29), shots=(0, 0, 0, 1),
               server=True):
    names = ("sprint-fire", "sprint-ended", "fire-clear", "settled")
    s = list(server_clip) if server else [None] * 4
    return [_st_cp(n, i * 4.0, s[i], predicted[i], shots[i], weapon=1) for i, n in enumerate(names)]


def _st_verdicts(run: pathlib.Path) -> list:
    return [v for v, _ in _p10_verdicts(run)[1]]


def self_test() -> int:
    """Mutation suite for the p10 grades. Returns the number of cases that failed."""
    import tempfile

    failures = []

    def check(label, actual, expected):
        ok = actual == expected
        print(f"{'ok  ' if ok else 'FAIL'} {label}: {actual}")
        if not ok:
            failures.append(f"{label}: expected {expected}, got {actual}")

    with tempfile.TemporaryDirectory() as raw:
        tmp = pathlib.Path(raw)

        # ---- semi: the rule the grade exists to enforce.
        check("semi/one round per press grades PASS",
              _st_verdicts(_st_run(tmp, "semi-ok", "p10-semi", _st_semi())),
              ["PASS", "PASS", "PASS"])

        # The whole point of reading serverAmmoInClip: the predicted clip here is frozen two
        # rounds low for the entire run and moves not at all, which is a state ReconcileAmmo
        # reaches legitimately and never leaves. Slack cannot reach the verdict.
        check("semi/two rounds of prediction slack still grades PASS",
              _st_verdicts(_st_run(tmp, "semi-slack", "p10-semi",
                                   _st_semi(predicted=(18, 18, 18, 18), shots=(0, 1, 1, 2)))),
              ["PASS", "PASS", "PASS"])

        # And the mirror: a genuine second round on the SERVER's clip is a FAIL no tolerance
        # can swallow, because no tolerance is applied to it.
        check("semi/a real double-spend on the second press grades FAIL",
              _st_verdicts(_st_run(tmp, "semi-double", "p10-semi",
                                   _st_semi(second=(19, 17), shots=(0, 1, 1, 3)))),
              ["PASS", "PASS", "FAIL"])

        check("semi/a server that fired nothing grades FAIL, never PASS",
              _st_verdicts(_st_run(tmp, "semi-refused", "p10-semi",
                                   _st_semi(first=(20, 20), second=(20, 20),
                                            predicted=(20, 19, 19, 18), shots=(0, 30, 30, 60)))),
              ["FAIL", "PASS", "FAIL"])

        check("semi/a recorder without the field is INCONCLUSIVE, not graded on the prediction",
              _st_verdicts(_st_run(tmp, "semi-old", "p10-semi", _st_semi(server=False))),
              ["INCONCLUSIVE"] * 3)

        # ---- sprint: the measured false PASS, from an Island run where the server fired
        # nothing for the entire programme (97 attempts, 97 refused Holstered) and the old
        # grade printed "PASS post-sprint window: ammoInClip 30 -> 29, predictedShots +34".
        check("sprint/a clean run grades PASS",
              _st_verdicts(_st_run(tmp, "sprint-ok", "p10-sprint", _st_sprint())),
              ["PASS", "PASS", "PASS"])

        check("sprint/predicted shots alone are not evidence the weapon came back up",
              _st_verdicts(_st_run(tmp, "sprint-refused", "p10-sprint",
                                   _st_sprint(server_clip=(30, 30, 30, 30),
                                              predicted=(30, 30, 30, 29),
                                              shots=(0, 0, 0, 34)))),
              ["PASS", "PASS", "FAIL"])

        check("sprint/a round leaving the server's clip while sprinting grades FAIL",
              _st_verdicts(_st_run(tmp, "sprint-leak", "p10-sprint",
                                   _st_sprint(server_clip=(30, 29, 29, 28)))),
              ["FAIL", "PASS", "PASS"])

        # ---- auto: the same refusal, where it would otherwise divide real seconds by an
        # imaginary round count and print a plausible cadence.
        auto_ok = [_st_cp("auto-hold", 0.0, 30, 30, 0, weapon=1),
                   _st_cp("released", 0.95, 20, 20, 10, weapon=1)]
        check("auto/a sustained burst grades its cadence PASS",
              _st_verdicts(_st_run(tmp, "auto-ok", "p10-auto", auto_ok)), ["PASS"])

        auto_refused = [_st_cp("auto-hold", 0.0, 30, 30, 0, weapon=1),
                        _st_cp("released", 0.95, 30, 29, 28, weapon=1)]
        check("auto/a refused burst has no cadence and grades FAIL",
              _st_verdicts(_st_run(tmp, "auto-refused", "p10-auto", auto_refused)), ["FAIL"])

        # ---- the gate's own exit codes, because a verdict list nobody converts is not a gate.
        check("gate/green run exits 0",
              p10_gate(_st_run(tmp, "gate-ok", "p10-semi", _st_semi())), 0)
        check("gate/failed rule exits 1",
              p10_gate(_st_run(tmp, "gate-red", "p10-semi",
                               _st_semi(second=(19, 17), shots=(0, 1, 1, 3)))), 1)
        check("gate/ungradeable run exits 2, which is NOT a green",
              p10_gate(_st_run(tmp, "gate-old", "p10-semi", _st_semi(server=False))), 2)

    print(f"\nself-test: {len(failures)} failed")
    for line in failures:
        print(f"  {line}")
    return len(failures)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=pathlib.Path, nargs="?")
    parser.add_argument("--section", action="append", choices=sorted(SECTIONS), default=None)
    parser.add_argument(
        "--gate",
        action="store_true",
        help="exit 1 if any process log carries an exception; print nothing else",
    )
    parser.add_argument(
        "--p10-gate",
        action="store_true",
        help="grade the run's protocol-10 programme; exit 1 on a failed rule, 2 when the run "
             "could not be graded at all",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="grade synthetic runs whose answers are known and exit 1 on any disagreement; "
             "takes no run directory",
    )
    args = parser.parse_args()

    if args.self_test:
        return 1 if self_test() else 0

    if args.run is None:
        parser.error("a run directory is required unless --self-test is given")

    if not args.run.is_dir():
        print(f"not a run directory: {args.run}", file=sys.stderr)
        return 2

    print(f"# {args.run}")
    if args.gate:
        return gate(args.run)
    if args.p10_gate:
        return p10_gate(args.run)

    for name in args.section or SECTIONS:
        SECTIONS[name](args.run)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
