"""Do the clients in a lane-B run agree about which vehicles exist? Ledger X-64.

The question is deliberately narrow, and it needs NOBODY TO DRIVE -- which is the
whole reason it is asked this way. Every lane-B vehicle run since P4 has been
ungradeable because no client could get into a seat (X-66), so every check written in
terms of a driver has been blocked for weeks. Whether two clients hold the same set of
vehicle ids is answerable from any run at all.

A client is told a vehicle exists by S_VEHICLE_SPAWN and told to forget it by
S_VEHICLE_DESPAWN, and `RemoteVehicleRegistry.LiveIds` -- which is what the checkpoint
records -- is driven by exactly those two messages and nothing else. It is NOT filtered
by interest: a client that is missing an id was never told, or was told to forget.

THE JOIN-ORDER SIGNATURE IS THE DECIDABLE PART. The defect this was written for is that
`ServerVehicleLifecycleSink.OnVehicleSpawned` broadcast to whoever was connected at that
instant and nothing ever replayed it, so a client only ever learned about vehicles that
spawned after it joined. That produces one specific shape: the sets are NESTED, ordered
by join time, and they only ever grow. Any other disagreement -- a set that shrinks, or
two clients each holding an id the other lacks -- is something else, and this script says
so rather than filing it under the same row.

Usage:  python tools/analyse_vehicle_sets.py artifacts/lane-b/<run>

Exit 0 when no join-order nesting is found. Exit 1 when it is. Other disagreements are
REPORTED and do not set the exit code, because attributing them needs a diagnosis this
script does not have.
"""
import json
import os
import sys

# Roster order is join order: run-lane-b.ps1 starts driver, then observer-a, then
# observer-b. That ordering is what makes the nesting test meaningful.
CLIENTS = ("driver", "observer-a", "observer-b")


def sets_by_checkpoint(run):
    """checkpoint -> client -> (sorted ids, newest vehicle tick), in recorded order."""
    order, out = [], {}
    for client in CLIENTS:
        path = os.path.join(run, client + "-checkpoints.jsonl")
        if not os.path.exists(path):
            continue
        for line in open(path, encoding="utf-8"):
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            name = row.get("checkpoint") or "?"
            vehicles = row.get("vehicles") or []
            ids = sorted(v.get("id") for v in vehicles if v.get("id") is not None)
            if name not in out:
                out[name] = {}
                order.append(name)
            out[name][client] = (ids, row.get("vehicleInterpNewestTick"))
    return order, out


def main(run):
    order, sets = sets_by_checkpoint(run)
    if not order:
        print("no checkpoints found in " + run)
        return 2

    header = "{:<18}".format("checkpoint") + "".join("{:>16}".format(c) for c in CLIENTS)
    print(header)
    print("-" * len(header))

    nested = []
    other = []

    for name in order:
        row = sets[name]
        cells = []
        for client in CLIENTS:
            if client in row:
                ids, tick = row[client]
                cells.append("{:>10}@{:<5}".format(len(ids), tick if tick is not None else "?"))
            else:
                cells.append("{:>16}".format("-"))
        print("{:<18}".format(name) + "".join(cells))

        present = [(c, set(row[c][0])) for c in CLIENTS if c in row]
        if len(present) < 2:
            continue
        if all(s == present[0][1] for _, s in present):
            continue

        # Nested and ordered by join time is the X-64 signature: every earlier-joining
        # client holds a SUBSET of every later-joining one. Checked as a chain over the
        # roster order rather than pairwise, so a partial overlap does not read as nesting.
        chain = all(
            present[i][1] <= present[i + 1][1] for i in range(len(present) - 1)
        )
        (nested if chain else other).append((name, present))

    print()
    print("counts are `vehicles@vehicleInterpNewestTick`; roster order is join order.")
    print()

    if nested:
        print("JOIN-ORDER NESTING (the X-64 signature) at {} checkpoint(s):".format(len(nested)))
        for name, present in nested:
            union = set().union(*(s for _, s in present))
            for client, held in present:
                missing = sorted(union - held)
                if missing:
                    print("  {:<16} {:<12} missing {}".format(name, client, missing))
    else:
        print("no join-order nesting: no client's set was a strict subset of a "
              "later-joining client's at any checkpoint.")

    if other:
        print()
        print("OTHER disagreements at {} checkpoint(s) -- NOT the join-order shape, and "
              "not attributed here:".format(len(other)))
        for name, present in other:
            for client, held in present:
                print("  {:<16} {:<12} {}".format(name, client, sorted(held)))

    return 1 if nested else 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
