#!/usr/bin/env python3
"""Grade a lane-A run against V9's thirteen criteria, and print the configuration it reached.

WHY THIS EXISTS
---------------
P7 ships a verdict, and three of its four named risks are measurement failures rather than code
failures:

  * the load never actually reached 16 + 32 + 12, so a passing number means nothing;
  * a criterion is quietly marked met on partial evidence;
  * the soak passes because the audit fields read zero from pools that were never populated.

Each one is a thing a human reading a JSONL by eye gets wrong, and each one has a mechanical
answer.  So the grading is a script: it reads the artifacts a run produced, states the
configuration that run REACHED beside every figure, and refuses to call a criterion met on
evidence it did not find.  "ungraded" is a permitted verdict here and "assumed" is not -- a
criterion whose artifact is missing comes back UNGRADED with the missing path named, never MET.

WHAT IT READS
-------------
  <tag>-ticks.jsonl               per stepped tick, from HeadlessLoadBootstrap (process A)
  <tag>-ticks.jsonl.summary.json  the run summary, incl. logByType (criterion 11)
  <tag>-errors.jsonl              one record per Error/Exception/Assert entry, with its site
  <tag>-report.json               the harness report (process B), ironfront.loadharness/2

USAGE
  python tools/grade_v9.py --tag p7-load --dir artifacts/lane-a
  python tools/grade_v9.py --tag p7-load --clients 16 --bots 32 --vehicles 12 --json out.json
  python tools/grade_v9.py --selftest

EXIT CODES
  0  no criterion this script can grade came back FAILED
  1  at least one criterion FAILED
  2  the artifacts could not be read at all
"""

from __future__ import annotations

import argparse
import json
import os
import sys

# ---------------------------------------------------------------- shared vocabulary

#: Positional order of the ``audit`` array HeadlessLoadBootstrap.AppendAudit writes.  Named in
#: exactly one place on each side, so the two cannot drift into a silent off-by-one.
AUDIT_FIELDS = (
    "actorIdsInUse",
    "actorIdsFree",
    "actorIdsQuarantined",
    "hitboxHistoryActors",
    "interestPairs",
    "spawnAckPairs",
    "sessions",
    "vehicleIdsInUse",
    "vehicleIdsQuarantined",
    "vehicleInterestPairs",
    "vehiclesRegistered",
    "mountedWeapons",
    "turrets",
    "projectileIds",
)

#: Ironfront.Net.Protocol.MatchPhase.
PHASE_NAMES = {0: "WaitingForPlayers", 1: "Warmup", 2: "Playing", 3: "Ended", 4: "Resetting"}
PHASE_PLAYING = 2
PHASE_RESETTING = 4

#: Design of record section 8 criterion 9.
BANDWIDTH_BUDGET_BYTES_PER_SEC = 5 * 1024

#: Criterion 10.  33.3 ms is the whole frame at the 30 Hz sim rate.
TICK_BUDGET_MICROS = 33_333

MET, FAILED, UNGRADED = "MET", "FAILED", "UNGRADED"


# ---------------------------------------------------------------- small statistics


def percentile(values, pct):
    """Nearest-rank percentile.  Returns None on an empty sample rather than 0.

    A zero here would be indistinguishable from a genuinely instant tick, and this script's
    whole job is to keep "unknown" and "good" from rendering the same way.
    """
    if not values:
        return None
    ordered = sorted(values)
    rank = max(1, int(round(pct / 100.0 * len(ordered))))
    return ordered[min(rank, len(ordered)) - 1]


def summarise(values):
    """p50 / p95 / p99 / max / mean / n for a sample, or an all-None row when empty."""
    if not values:
        return {"n": 0, "p50": None, "p95": None, "p99": None, "max": None, "mean": None}
    return {
        "n": len(values),
        "p50": percentile(values, 50),
        "p95": percentile(values, 95),
        "p99": percentile(values, 99),
        "max": max(values),
        "mean": sum(values) / float(len(values)),
    }


# ---------------------------------------------------------------- loading


def read_jsonl(path):
    """Every parseable record, plus the count of lines that were not.

    A malformed line is reported rather than skipped silently: a truncated tail is exactly what
    a killed server leaves behind, and it changes what the percentiles below describe.
    """
    records, bad = [], 0
    if not os.path.exists(path):
        return None, 0
    with open(path, "r", encoding="utf-8-sig") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except ValueError:
                bad += 1
    return records, bad


def read_json(path):
    if not os.path.exists(path):
        return None
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            return json.load(handle)
    except ValueError:
        return None


# ---------------------------------------------------------------- the reached configuration


def reached_configuration(ticks, targets):
    """What the run actually carried, per phase-plan risk 1 -- asserted, never assumed.

    Measured over the LOADED sample -- Playing records carrying at least one connection.

    Two narrowings, and each one was earned.  Playing, because a run's warm-up carries no bots
    and no clients and a band over the whole file would describe the first second rather than
    the load.  At least one connection, because the server keeps playing rounds either side of
    the harness's window: on the p7-smoke run 749 of the Playing records had NO client attached,
    so the median client count came back 0 and the shortfall assertion fired against a run that
    had in fact held both its clients.  B-17's own 8-client figure was taken this way and says
    so -- "Loaded sample (>=1 connection), n = 3,637".

    The narrowing is REPORTED, not silent: ``sample`` names which population was used and
    ``recordsGraded`` how many records it held, so a band taken over the wrong one is visible
    rather than inferred.  Falling back widens rather than failing -- a run with no loaded
    record at all still gets a configuration, labelled as the weaker population it came from.
    """
    playing = [r for r in ticks if r.get("phase") == PHASE_PLAYING]
    loaded = [r for r in playing if int(r.get("conns", 0)) >= 1]

    if loaded:
        sample, population = loaded, "playing+loaded"
    elif playing:
        sample, population = playing, "playing (NO record carried a connection)"
    else:
        sample, population = ticks, "WHOLE RUN (no Playing record at all)"

    def series(key):
        return [int(r.get(key, 0)) for r in sample]

    conns, actors, vehicles = series("conns"), series("actors"), series("vehicles")
    players = series("players")

    # Bots are actors that are not connected players.  Derived rather than stored, because the
    # server has no separate bot counter and inventing one here would be a second implementation
    # of a number nothing else agrees with.
    bots = [a - p for a, p in zip(actors, players)]

    def band(values):
        if not values:
            return {"min": None, "median": None, "max": None}
        return {"min": min(values), "median": percentile(values, 50), "max": max(values)}

    reached = {
        "recordsGraded": len(sample),
        "population": population,
        "playingRecords": len(playing),
        "loadedRecords": len(loaded),
        "gradedOverPlayingPhase": bool(playing),
        "clients": band(conns),
        "players": band(players),
        "actors": band(actors),
        "bots": band(bots),
        "vehicles": band(vehicles),
        "targets": dict(targets),
        "shortfall": {},
    }

    # The assertion.  A median below target is a shortfall; the run is still reported, with the
    # configuration it reached, per the deleted spec's D8/D9.
    #
    # An OVERSHOOT is recorded separately and is NOT a shortfall.  The shipped Dustbowl authors
    # more bots and more vehicle spawners than the design's 32 + 12, so a run on the shipping map
    # carries a HARDER load than the criterion asks for.  That is a stronger result, not a
    # weaker one -- but a figure taken at 56 bots must not be quoted as a figure at 32, so it is
    # named beside every number rather than rounded down to the target in the prose.
    reached["overshoot"] = {}
    for name, target in targets.items():
        got = reached[name]["median"]
        if target is None or got is None:
            continue
        if got < target:
            reached["shortfall"][name] = {"target": target, "reached": got}
        elif got > target:
            reached["overshoot"][name] = {"target": target, "reached": got}

    return reached


def configuration_note(reached):
    """One line naming the configuration, to be printed beside every figure."""
    def median_of(key):
        value = reached[key]["median"]
        return "?" if value is None else value

    note = "reached {c} clients / {b} bots / {v} vehicles ({a} actors)".format(
        c=median_of("clients"), b=median_of("bots"), v=median_of("vehicles"),
        a=median_of("actors"))
    if reached["shortfall"]:
        parts = ", ".join(
            "{0} {1} < {2}".format(k, v["reached"], v["target"])
            for k, v in sorted(reached["shortfall"].items()))
        note += " -- SHORT OF TARGET: " + parts
    if reached.get("overshoot"):
        parts = ", ".join(
            "{0} {1} > {2}".format(k, v["reached"], v["target"])
            for k, v in sorted(reached["overshoot"].items()))
        note += " -- over target (a harder load, not a weaker one): " + parts
    return note


# ---------------------------------------------------------------- the soak


#: Audit fields a reset is genuinely required to empty.
#:
#: ``actorIdsInUse`` is deliberately NOT here.  ``ServerTickLoop.ResetForNewMatch`` collects
#: every live actor's id and passes it to ``ActorIdPool.ResetAll`` so the pool cannot re-offer an
#: id an actor still holds -- on the shipping Dustbowl that is all 56, every round, by design.
#: Requiring it to be zero would grade a correct retention as a leak, which is precisely what the
#: server's own ``IsCleanOfActorState`` does today (see the report's finding on that predicate).
#: The retention is instead CHECKED, against the live actor count carried on the same record.
RESET_MUST_EMPTY = (
    "hitboxHistoryActors",
    "interestPairs",
    "spawnAckPairs",
    "vehicleIdsInUse",
    "vehicleInterestPairs",
    "vehiclesRegistered",
    "mountedWeapons",
    "turrets",
    "projectileIds",
)


def grade_soak(ticks):
    """Rounds completed, and whether each audited pool rose before it fell.

    The risk this answers verbatim: "the soak passes because the audit fields read zero from
    pools that were never populated".  So each field is checked for a non-zero reading DURING a
    round before its post-reset zero is allowed to count for anything.  A field that never rose
    is reported as ``neverRose`` and is NOT counted as a clean reset.

    **Resets are read off the sink's own reset records, never inferred from the phase field.**
    ``MatchPhase.Resetting`` is entered and left inside one ``MatchStateMachine.Tick`` call at
    execution order 100, so a phase sampled at order 300 shows either the state BEFORE the reset
    or no ``Resetting`` record at all, depending on whether that frame stepped a tick.  Measured
    on ``p7-load-move``: the server logged three resets, and phase-sampling found one -- with the
    pre-reset state attached to it, which then read as a leak.  A reset record is written by the
    reset itself and cannot be missed or mistimed.
    """
    audited = [r for r in ticks if isinstance(r.get("audit"), list) and "reset" not in r]
    resets = [r for r in ticks if "reset" in r and isinstance(r.get("audit"), list)]

    if not audited and not resets:
        return {"available": False, "reason": "no record carries an audit array"}

    peak = {name: 0 for name in AUDIT_FIELDS}
    for record in audited:
        if record.get("phase") != PHASE_PLAYING:
            continue
        values = record["audit"]
        for index, name in enumerate(AUDIT_FIELDS):
            if index < len(values) and values[index] > peak[name]:
                peak[name] = values[index]

    if not resets:
        return {
            "available": True,
            "resetRecords": False,
            "roundsReset": 0,
            "cleanAtReset": 0,
            "dirtyAtReset": 0,
            "peakDuringPlay": peak,
            "neverRose": [name for name in AUDIT_FIELDS if peak[name] == 0],
            "dirtyExamples": [],
            "retentionMismatches": [],
        }

    clean, dirty, dirty_examples, retention = 0, 0, [], []
    for record in resets:
        values = dict(zip(AUDIT_FIELDS, record["audit"]))
        leaked = {k: values[k] for k in RESET_MUST_EMPTY if values.get(k)}

        # The retention check that replaces the ActorIdsInUse == 0 demand: every id still in use
        # must belong to an actor that is still alive.  More ids than actors IS a leak; equal is
        # the documented retention; fewer would mean an actor holding an id the pool has forgotten.
        live = record.get("liveActors")
        if isinstance(live, int) and values.get("actorIdsInUse") != live:
            retention.append({"reset": record.get("reset"), "tick": record.get("t"),
                              "actorIdsInUse": values.get("actorIdsInUse"), "liveActors": live})

        if leaked:
            dirty += 1
            if len(dirty_examples) < 5:
                dirty_examples.append({"reset": record.get("reset"), "tick": record.get("t"),
                                       "leaked": leaked})
        else:
            clean += 1

    return {
        "available": True,
        "resetRecords": True,
        "roundsReset": len(resets),
        "cleanAtReset": clean,
        "dirtyAtReset": dirty,
        "peakDuringPlay": peak,
        "neverRose": [name for name in AUDIT_FIELDS if peak[name] == 0],
        "dirtyExamples": dirty_examples,
        "retentionMismatches": retention,
    }


# ---------------------------------------------------------------- the grade


def grade(tag, directory, targets):
    """Every criterion this script can reach, as {n, criterion, verdict, artifact, note}."""
    ticks_path = os.path.join(directory, tag + "-ticks.jsonl")
    summary_path = ticks_path + ".summary.json"
    errors_path = os.path.join(directory, tag + "-errors.jsonl")
    report_path = os.path.join(directory, tag + "-report.json")

    ticks, bad_ticks = read_jsonl(ticks_path)
    errors, _ = read_jsonl(errors_path)
    summary = read_json(summary_path)
    report = read_json(report_path)

    if ticks is None:
        return None, "no tick JSONL at " + ticks_path

    result = {
        "tag": tag,
        "artifacts": {
            "ticks": ticks_path,
            "summary": summary_path,
            "errors": errors_path,
            "report": report_path,
        },
        "malformedTickLines": bad_ticks,
        "reached": reached_configuration(ticks, targets),
    }
    result["configurationNote"] = configuration_note(result["reached"])

    # ---- tick timing, split by stage (task 4.2) ----
    # The same loaded sample the configuration is stated over, for the same reason: a p99 that
    # includes 749 idle ticks with nobody connected is a percentile of a different experiment.
    def micros(key):
        return [int(r[key]) for r in ticks
                if r.get("phase") == PHASE_PLAYING and int(r.get("conns", 0)) >= 1
                and isinstance(r.get(key), int)]

    result["tick"] = {
        "step": summarise(micros("stepMicros")),
        "input": summarise(micros("inputMicros")),
        "gameplay": summarise(micros("gameplayMicros")),
        "snapshot": summarise(micros("snapshotMicros")),
        "frame": summarise(micros("frameMicros")),
        "budgetMicros": TICK_BUDGET_MICROS,
        "note": ("step = input + gameplay + snapshot, all of them FixedUpdate and so all of "
                 "them before Unity steps PhysX. frame is Time.unscaledDeltaTime and does "
                 "include physics."),
    }

    # ---- interest shedding (criterion 9's failure condition) ----
    shed = sum(int(r.get("entriesShed", 0)) for r in ticks)
    sent = sum(int(r.get("entriesSent", 0)) for r in ticks)
    result["interest"] = {"entriesShed": shed, "entriesSent": sent}

    # ---- bandwidth, from the harness's own per-connection accounting ----
    bandwidth = {"available": False}
    if report:
        per_client = [c.get("ReceivedBytesPerSecond") for c in report.get("Clients", [])
                      if isinstance(c.get("ReceivedBytesPerSecond"), (int, float))]
        if per_client:
            bandwidth = {
                "available": True,
                "meanBytesPerSec": sum(per_client) / float(len(per_client)),
                "worstBytesPerSec": max(per_client),
                "clients": len(per_client),
                "heldToEnd": report.get("ClientsHeldToEnd"),
                "requested": report.get("ClientsRequested"),
                "budgetBytesPerSec": BANDWIDTH_BUDGET_BYTES_PER_SEC,
            }
    result["bandwidth"] = bandwidth

    result["soak"] = grade_soak(ticks)

    # ---- the log, by LogType (criterion 11) ----
    result["log"] = {
        "byType": (summary or {}).get("logByType"),
        "errorRecords": len(errors) if errors is not None else None,
        "sites": error_sites(errors),
    }

    result["criteria"] = build_criteria(result)
    return result, None


def error_sites(errors):
    """The distinct first stack frames, most frequent first -- what triage reads."""
    if not errors:
        return []
    tally = {}
    for record in errors:
        key = (record.get("type", "?"), (record.get("site") or "").strip())
        tally[key] = tally.get(key, 0) + 1
    ordered = sorted(tally.items(), key=lambda kv: (-kv[1], kv[0]))
    return [{"type": k[0], "site": k[1], "count": v} for k, v in ordered[:10]]


def build_criteria(result):
    """The thirteen rows.  Each names its artifact; each unreachable one says why."""
    note = result["configurationNote"]
    rows = []

    def row(number, text, verdict, artifact, why):
        rows.append({"n": number, "criterion": text, "verdict": verdict,
                     "artifact": artifact, "note": why})

    editor = "needs the Unity Editor; lane B grades it (design section 7)"
    ticks_file = result["artifacts"]["ticks"]
    report_file = result["artifacts"]["report"]

    row(1, "two clients see the same vehicle in the same place while a third drives it",
        UNGRADED, report_file,
        "the decoded-state half is lane A's Agreement block; the RENDERED half " + editor)
    row(2, "no perceptible input lag; convergence without visible snapping",
        UNGRADED, report_file, "perceptual in full; " + editor)
    row(3, "out-of-range vehicle input is clamped server-side", UNGRADED, "dotnet test",
        "graded by VehicleInputClamp's unit tests, not by this run")
    row(4, "turret aim identical everywhere; slew framerate-independent at 30 and 144 Hz",
        UNGRADED, "dotnet test", "graded engine-free by the turret tests, not by this run")
    row(5, "a grenade detonates at the same position on every client; damage applied once",
        UNGRADED, report_file, "needs a firing client programme; " + editor)
    row(6, "explosion damage moves authoritative health; S_EXPLOSION has caller and subscriber",
        UNGRADED, "ClientWiringGate", "graded by the wiring gate, not by this run")
    row(7, "exactly one capture-point authority; SpawnPoint.owner matches OwningTeam",
        UNGRADED, "dotnet test", "graded by the capture-point tests, not by this run")
    row(8, "a weapon that is not a rifle behaves differently from a rifle on the server",
        UNGRADED, "dotnet test", "graded by the weapon-registry tests, not by this run")

    # 9 -- bandwidth, and the shed count that overrides it.
    bandwidth = result["bandwidth"]
    shed = result["interest"]["entriesShed"]
    if not bandwidth.get("available"):
        row(9, "bandwidth <= 5 KB/s/client at full load", UNGRADED, report_file,
            "no per-client byte accounting in the harness report")
    else:
        worst = bandwidth["worstBytesPerSec"]
        mean = bandwidth["meanBytesPerSec"]
        within = worst <= BANDWIDTH_BUDGET_BYTES_PER_SEC
        verdict = MET if (within and shed == 0) else FAILED
        reason = "mean {0:.0f} B/s, worst {1:.0f} B/s against {2} B/s; entriesShed {3}".format(
            mean, worst, BANDWIDTH_BUDGET_BYTES_PER_SEC, shed)
        if shed:
            reason += " -- a non-zero shed count at load is a failure, not a pass"
        row(9, "bandwidth <= 5 KB/s/client at full load", verdict, report_file,
            reason + "; " + note)

    # 10 -- tick p99, on the span that is actually measurable.
    step = result["tick"]["step"]
    if step["n"] == 0:
        row(10, "tick p99 < 33 ms at the same load", UNGRADED, ticks_file,
            "no Playing-phase tick records")
    else:
        verdict = MET if step["p99"] < TICK_BUDGET_MICROS else FAILED
        frame = result["tick"]["frame"]
        frame_text = ("frame p99 {0} us".format(frame["p99"])
                      if frame["p99"] is not None else "frame p99 unrecorded")
        row(10, "tick p99 < 33 ms at the same load", verdict, ticks_file,
            "script-span p99 {0} us of {1} us over n={2}; input/gameplay/snapshot p99 "
            "{3}/{4}/{5} us; {6} (PhysX is in the frame, not the span); {7}".format(
                step["p99"], TICK_BUDGET_MICROS, step["n"],
                result["tick"]["input"]["p99"], result["tick"]["gameplay"]["p99"],
                result["tick"]["snapshot"]["p99"], frame_text, note))

    # 11 -- the log, not the exit code.
    by_type = result["log"]["byType"]
    if not by_type:
        row(11, "a headless server survives the vehicle lifecycle with zero NREs",
            UNGRADED, result["artifacts"]["summary"],
            "no logByType in the summary: the counts are UNKNOWN, not zero")
    else:
        graded = (int(by_type.get("Error", 0)) + int(by_type.get("Exception", 0))
                  + int(by_type.get("Assert", 0)))
        observed = int(by_type.get("Log", 0)) + int(by_type.get("Warning", 0))
        if graded == 0 and observed == 0:
            row(11, "a headless server survives the vehicle lifecycle with zero NREs",
                UNGRADED, result["artifacts"]["summary"],
                "zero entries of EVERY type: the sink recorded nothing, which is a silent "
                "subscription failure rather than a clean run")
        else:
            sites = "; ".join("{0}x {1} {2}".format(s["count"], s["type"], s["site"])
                              for s in result["log"]["sites"][:3])
            row(11, "a headless server survives the vehicle lifecycle with zero NREs",
                MET if graded == 0 else FAILED, result["artifacts"]["errors"],
                "Error {0} / Exception {1} / Assert {2} against Log {3} + Warning {4}{5}".format(
                    by_type.get("Error"), by_type.get("Exception"), by_type.get("Assert"),
                    by_type.get("Log"), by_type.get("Warning"),
                    (" -- " + sites) if sites else ""))

    row(12, "dotnet test green; no System.Linq, no foreach, no per-tick allocation",
        UNGRADED, "tools/ci.ps1", "graded by the solution gate, not by this run")

    # 13 -- five matches, each pool proven to rise before it is asked to fall.
    soak = result["soak"]
    if not soak.get("available"):
        row(13, "five matches back to back with AssertCleanState() passing",
            UNGRADED, ticks_file, soak.get("reason", "no audit records"))
    else:
        rounds = soak["roundsReset"]
        vacuous = [n for n in soak["neverRose"]
                   if n in ("vehicleIdsInUse", "vehiclesRegistered", "vehicleInterestPairs",
                            "actorIdsInUse", "interestPairs")]
        if not soak.get("resetRecords"):
            verdict = UNGRADED
            why = ("this run carries no reset record -- the sink that writes one postdates it, "
                   "and a reset inferred from the phase field is both undercounted and sampled "
                   "before the reset ran")
        elif rounds < 5:
            verdict = UNGRADED
            why = "only {0} reset(s) in this run; five are needed".format(rounds)
        elif soak["dirtyAtReset"]:
            verdict = FAILED
            why = "{0} of {1} resets left state behind: {2}".format(
                soak["dirtyAtReset"], rounds,
                "; ".join("reset {0} {1}".format(d["reset"], d["leaked"])
                          for d in soak["dirtyExamples"]))
        elif soak["retentionMismatches"]:
            verdict = FAILED
            why = ("{0} of {1} resets left more actor ids in use than there are live actors -- "
                   "that is the leak the retention is meant to make visible: {2}".format(
                       len(soak["retentionMismatches"]), rounds,
                       soak["retentionMismatches"][:3]))
        elif vacuous:
            verdict = UNGRADED
            why = ("all {0} resets clean, but {1} never rose above zero during play -- a "
                   "counter that cannot rise cannot fall meaningfully".format(
                       rounds, ", ".join(vacuous)))
        else:
            verdict = MET
            why = ("{0} resets, every pool a reset must empty at zero and every one of them "
                   "non-zero mid-round first; actor ids in use equal the live actor count at "
                   "each reset, which is the documented retention rather than a leak").format(
                       rounds)
        row(13, "five matches back to back with AssertCleanState() passing",
            verdict, ticks_file, why + "; " + note)

    return rows


# ---------------------------------------------------------------- rendering


def render(result):
    lines = []
    lines.append("=== V9 grade: {0} ===".format(result["tag"]))
    lines.append(result["configurationNote"])
    if result["malformedTickLines"]:
        lines.append("WARNING: {0} malformed line(s) in the tick JSONL -- the run may be "
                     "truncated.".format(result["malformedTickLines"]))
    lines.append("")

    reached = result["reached"]
    lines.append("-- configuration reached (population: {0}; n={1} of {2} Playing) --".format(
        reached["population"], reached["recordsGraded"], reached["playingRecords"]))
    for key in ("clients", "players", "bots", "actors", "vehicles"):
        band = reached[key]
        target = reached["targets"].get(key)
        lines.append("  {0:<9} min {1} / median {2} / max {3}{4}".format(
            key, band["min"], band["median"], band["max"],
            "   target {0}".format(target) if target else ""))
    lines.append("")

    tick = result["tick"]
    lines.append("-- tick, by stage (microseconds, Playing phase only) --")
    lines.append("  {0:<10} {1:>7} {2:>8} {3:>8} {4:>9} {5:>9}".format(
        "", "n", "p50", "p95", "p99", "max"))
    for name in ("step", "input", "gameplay", "snapshot", "frame"):
        stage = tick[name]
        lines.append("  {0:<10} {1:>7} {2:>8} {3:>8} {4:>9} {5:>9}".format(
            name, stage["n"], stage["p50"], stage["p95"], stage["p99"], stage["max"]))
    lines.append("  " + tick["note"])
    lines.append("")

    soak = result["soak"]
    if soak.get("available"):
        lines.append("-- soak --")
        lines.append("  resets {0}  clean {1}  dirty {2}   (source: {3})".format(
            soak["roundsReset"], soak["cleanAtReset"], soak["dirtyAtReset"],
            "reset records" if soak.get("resetRecords") else "NONE -- no reset record in file"))
        if soak.get("retentionMismatches"):
            lines.append("  retention mismatch: " + str(soak["retentionMismatches"][:3]))
        peaks = ", ".join("{0}={1}".format(k, v) for k, v in soak["peakDuringPlay"].items() if v)
        lines.append("  peak during play: " + (peaks or "NOTHING ROSE ABOVE ZERO"))
        if soak["neverRose"]:
            lines.append("  never rose: " + ", ".join(soak["neverRose"]))
        lines.append("")

    lines.append("-- the thirteen --")
    for entry in result["criteria"]:
        lines.append("  {0:>2}. {1:<9} {2}".format(
            entry["n"], entry["verdict"], entry["criterion"]))
        lines.append("      {0}".format(entry["note"]))
    lines.append("")

    counts = {}
    for entry in result["criteria"]:
        counts[entry["verdict"]] = counts.get(entry["verdict"], 0) + 1
    lines.append("MET {0}  FAILED {1}  UNGRADED {2}  (blanks are not a permitted verdict)".format(
        counts.get(MET, 0), counts.get(FAILED, 0), counts.get(UNGRADED, 0)))
    return "\n".join(lines)


# ---------------------------------------------------------------- self-test


def run_selftest():
    """Every check below is MUTATED into failing first, then repaired.

    A detector that has never been observed RED is decoration (plan.md standing rule 4), and
    this file is the thing P7's verdict is trusted from.
    """
    failures = []

    def check(name, condition, detail=""):
        if condition:
            print("  PASS  " + name)
        else:
            print("  FAIL  " + name + ("  " + detail if detail else ""))
            failures.append(name)

    def tick(t, phase, audit, clean, step=900, shed=0, conns=16, actors=48,
             players=16, vehicles=12):
        return {"t": t, "phase": phase, "stepMicros": step, "inputMicros": 100,
                "gameplayMicros": 700, "snapshotMicros": 100, "frameMicros": 30000,
                "conns": conns, "actors": actors, "players": players, "vehicles": vehicles,
                "entriesShed": shed, "entriesSent": 40,
                "audit": list(audit), "auditClean": 1 if clean else 0}

    live = [3, 0, 0, 4, 9, 9, 16, 5, 0, 12, 5, 2, 2, 1]
    empty = [0, 0, 0, 0, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0]
    targets = {"clients": 16, "bots": 32, "vehicles": 12}

    # ---- percentile: unknown must never render as good ----
    check("percentile of an empty sample is None, not 0", percentile([], 99) is None)
    check("percentile of a real sample", percentile([1, 2, 3, 4, 100], 99) == 100)

    # ---- the reached configuration ----
    good = [tick(i, PHASE_PLAYING, live, True) for i in range(10)]
    reached = reached_configuration(good, targets)
    check("a run at target reports no shortfall", reached["shortfall"] == {},
          str(reached["shortfall"]))

    short = [tick(i, PHASE_PLAYING, live, True, conns=8, actors=20, players=8, vehicles=4)
             for i in range(10)]
    reached_short = reached_configuration(short, targets)
    check("MUTATION: a short run is reported short on all three axes",
          set(reached_short["shortfall"]) == {"clients", "bots", "vehicles"},
          str(reached_short["shortfall"]))
    check("the shortfall reaches the note",
          "SHORT OF TARGET" in configuration_note(reached_short))

    mixed = ([tick(i, 1, empty, True, conns=0, actors=0, players=0, vehicles=0)
              for i in range(50)]
             + [tick(50 + i, PHASE_PLAYING, live, True) for i in range(10)])
    check("warm-up records do not drag the reached configuration down",
          reached_configuration(mixed, targets)["shortfall"] == {})

    # ---- the soak: resets come from reset records, never from the phase ----
    #
    # A reset record carries the POST-reset audit.  ``clean_reset`` is what a correct reset
    # leaves: every pool a reset must empty at zero, and actorIdsInUse equal to the live actor
    # count -- the documented Dustbowl retention, not a leak.
    clean_reset = [56, 8, 0, 0, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0]
    leaky_reset = [56, 8, 0, 4, 9, 0, 16, 0, 0, 12, 5, 0, 0, 0]

    def reset_record(n, t, audit, live_actors=56):
        return {"reset": n, "t": t, "liveActors": live_actors, "liveVehicles": 0,
                "audit": list(audit), "auditClean": 0}

    def soak_run(pool, reset_audit=None, rounds=5, with_reset_records=True,
                 live_actors=56):
        records, t = [], 0
        for n in range(1, rounds + 1):
            for _ in range(5):
                t += 1
                records.append(tick(t, PHASE_PLAYING, pool, True))
            t += 1
            # The phase-sampled record a pre-correction grader would have counted. It is
            # deliberately still here: the point is that it must NOT be counted.
            records.append(tick(t, PHASE_RESETTING, pool, False))
            if with_reset_records:
                records.append(reset_record(
                    n, t, reset_audit if reset_audit is not None else clean_reset,
                    live_actors))
            t += 1
            records.append(tick(t, 0, empty, True))
        return records

    healthy = grade_soak(soak_run(live))
    check("five clean resets are counted from the reset records",
          healthy["roundsReset"] == 5 and healthy["dirtyAtReset"] == 0, str(healthy))
    check("three rounds give three resets",
          grade_soak(soak_run(live, rounds=3))["roundsReset"] == 3)

    check("MUTATION: a phase-sampled Resetting record is NOT counted as a reset",
          grade_soak(soak_run(live, with_reset_records=False))["roundsReset"] == 0)
    check("MUTATION: a run with no reset record says so",
          grade_soak(soak_run(live, with_reset_records=False))["resetRecords"] is False)

    vacuous_soak = grade_soak(soak_run(empty))
    check("MUTATION: pools that never rose are named",
          "vehicleIdsInUse" in vacuous_soak["neverRose"], str(vacuous_soak["neverRose"]))

    dirty = grade_soak(soak_run(live, reset_audit=leaky_reset))
    check("MUTATION: a reset that left a pool populated is dirty",
          dirty["dirtyAtReset"] == 5, str(dirty["dirtyExamples"][:1]))
    check("the leaked pools are named, not just counted",
          set(dirty["dirtyExamples"][0]["leaked"]) ==
          {"hitboxHistoryActors", "interestPairs", "vehicleInterestPairs", "vehiclesRegistered"},
          str(dirty["dirtyExamples"][0]["leaked"]))

    check("a retained actor id equal to the live actor count is NOT a leak",
          grade_soak(soak_run(live))["retentionMismatches"] == [])
    check("MUTATION: more ids in use than live actors IS reported",
          len(grade_soak(soak_run(live, live_actors=40))["retentionMismatches"]) == 5)

    # ---- criteria over those soaks ----
    def crit(records, report=None, summary_log=None):
        base = {
            "tag": "t",
            "artifacts": {"ticks": "T", "summary": "S", "errors": "E", "report": "R"},
            "malformedTickLines": 0,
            "reached": reached_configuration(records, targets),
            "interest": {"entriesShed": sum(r.get("entriesShed", 0) for r in records),
                         "entriesSent": 0},
            "bandwidth": report or {"available": False},
            "soak": grade_soak(records),
            "log": {"byType": summary_log, "errorRecords": None, "sites": []},
        }
        base["configurationNote"] = configuration_note(base["reached"])
        playing = [r["stepMicros"] for r in records if r.get("phase") == PHASE_PLAYING]
        base["tick"] = {"step": summarise(playing), "input": summarise([]),
                        "gameplay": summarise([]), "snapshot": summarise([]),
                        "frame": summarise([]), "budgetMicros": TICK_BUDGET_MICROS, "note": ""}
        return {r["n"]: r for r in build_criteria(base)}

    check("criterion 13 is MET on a healthy soak", crit(soak_run(live))[13]["verdict"] == MET)
    check("MUTATION: criterion 13 is UNGRADED, never MET, on pools that never rose",
          crit(soak_run(empty))[13]["verdict"] == UNGRADED)
    check("MUTATION: criterion 13 FAILS on a reset that left a pool populated",
          crit(soak_run(live, reset_audit=leaky_reset))[13]["verdict"] == FAILED)
    check("MUTATION: criterion 13 FAILS when ids outnumber live actors",
          crit(soak_run(live, live_actors=40))[13]["verdict"] == FAILED)
    check("MUTATION: criterion 13 is UNGRADED below five rounds",
          crit(soak_run(live, rounds=4))[13]["verdict"] == UNGRADED)
    check("MUTATION: criterion 13 is UNGRADED, never MET, with no reset record",
          crit(soak_run(live, with_reset_records=False))[13]["verdict"] == UNGRADED)

    # ---- criterion 9: shed overrides a passing byte figure ----
    inside = {"available": True, "meanBytesPerSec": 2600.0, "worstBytesPerSec": 3100.0,
              "clients": 16, "budgetBytesPerSec": BANDWIDTH_BUDGET_BYTES_PER_SEC}
    over = dict(inside, worstBytesPerSec=6000.0)
    check("criterion 9 is MET inside budget with no shedding",
          crit(soak_run(live), report=inside)[9]["verdict"] == MET)
    check("MUTATION: criterion 9 FAILS over budget",
          crit(soak_run(live), report=over)[9]["verdict"] == FAILED)

    shed_records = soak_run(live)
    shed_records[3]["entriesShed"] = 1
    check("MUTATION: criterion 9 FAILS on a single shed entry inside budget",
          crit(shed_records, report=inside)[9]["verdict"] == FAILED)

    # ---- criterion 10 ----
    check("criterion 10 is MET under budget", crit(soak_run(live))[10]["verdict"] == MET)
    slow = soak_run(live)
    for record in slow:
        record["stepMicros"] = 40000
    check("MUTATION: criterion 10 FAILS over the 33 ms budget",
          crit(slow)[10]["verdict"] == FAILED)

    # ---- criterion 11: the silent-subscription trap ----
    check("criterion 11 is MET on a clean log with real traffic",
          crit(soak_run(live), summary_log={"Error": 0, "Exception": 0, "Assert": 0,
                                            "Log": 4000, "Warning": 12})[11]["verdict"] == MET)
    check("MUTATION: criterion 11 FAILS on one Error",
          crit(soak_run(live), summary_log={"Error": 1, "Exception": 0, "Assert": 0,
                                            "Log": 4000, "Warning": 0})[11]["verdict"] == FAILED)
    check("MUTATION: criterion 11 FAILS on one Exception",
          crit(soak_run(live), summary_log={"Error": 0, "Exception": 1, "Assert": 0,
                                            "Log": 4000, "Warning": 0})[11]["verdict"] == FAILED)
    check("MUTATION: an all-zero tally is UNGRADED, not MET -- the sink never attached",
          crit(soak_run(live), summary_log={"Error": 0, "Exception": 0, "Assert": 0,
                                            "Log": 0, "Warning": 0})[11]["verdict"] == UNGRADED)
    check("MUTATION: a missing summary is UNGRADED, not MET",
          crit(soak_run(live), summary_log=None)[11]["verdict"] == UNGRADED)

    # ---- no blanks, ever ----
    rows = crit(soak_run(live), report=inside,
                summary_log={"Error": 0, "Exception": 0, "Assert": 0, "Log": 9, "Warning": 0})
    check("all thirteen criteria are present", sorted(rows) == list(range(1, 14)))
    check("every criterion carries a verdict and an artifact",
          all(r["verdict"] in (MET, FAILED, UNGRADED) and r["artifact"]
              for r in rows.values()))

    print("")
    if failures:
        print("SELFTEST FAILED: " + ", ".join(failures))
        return 1
    print("SELFTEST PASSED")
    return 0


# ---------------------------------------------------------------- entry point


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--tag", help="the run tag, e.g. p7-load")
    parser.add_argument("--dir", default="artifacts/lane-a", help="artifact directory")
    parser.add_argument("--clients", type=int, default=16, help="target connected clients")
    parser.add_argument("--bots", type=int, default=32, help="target bots (actors - players)")
    parser.add_argument("--vehicles", type=int, default=12, help="target live vehicles")
    parser.add_argument("--json", help="write the full grade to this path")
    parser.add_argument("--selftest", action="store_true",
                        help="mutate every check into failing, then repair it")
    args = parser.parse_args(argv)

    if args.selftest:
        return run_selftest()

    if not args.tag:
        parser.error("--tag is required unless --selftest is given")

    targets = {"clients": args.clients, "bots": args.bots, "vehicles": args.vehicles}
    result, error = grade(args.tag, args.dir, targets)
    if error:
        print("COULD NOT GRADE: " + error)
        return 2

    print(render(result))

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(result, handle, indent=2)
        print("\nfull grade -> " + args.json)

    return 1 if any(r["verdict"] == FAILED for r in result["criteria"]) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
