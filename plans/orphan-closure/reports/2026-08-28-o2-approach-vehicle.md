# O2 report — a scripted client can walk to a vehicle

- **Phase:** [`phase-o2-approach-vehicle.md`](../phases/phase-o2-approach-vehicle.md) · **Date:** 2026-08-28
- **Closes:** **X-44** → unblocks verdict-closure R1.3's vehicle programme set, and with it checks 5, 7, 9 and 12
- **Commit:** `feat(tools): a programme verb that walks a scripted client to a vehicle`

---

## 1. What was wrong

R2 closed X-30, so a scripted client could *ask* for a seat. Asking is not reaching:
`ClientSeatRequester.TryFindNearestSeat` only considers vehicles within
`SeatArbiter.MaxSeatReachMetres` (6 m) of where the player is standing, and the programme vocabulary
had no verb that walks to one — `approach` resolves through
`ScriptedTargetSolver.Solve(step.aimAtPlayer)`, which takes a player **display name**, and a vehicle
has none.

## 2. What changed, and what was rejected

The row named two candidates. **The verb won; the spawn pin lost, for a stated reason:** a pin
chosen for vehicle adjacency is a property of one map at one moment, cannot be checked from the
artifact, and would put the shooter, the victim and the witness next to a vehicle whether or not
their programme wants one. X-28 is already open about what a shared pin costs — and this session
watched a pin fail live (§ 4).

- `ScriptedTargetSolver.SolveNearestVehicle` resolves against `RemoteVehicleRegistry` through
  `TryGetPose` — the seam phase C4c added precisely so `NetClientVehicle` need not be public.
- The nearest-within scan is `ScriptedAim.NearestIndexWithin`, on the engine-free side, because that
  is the only half `dotnet test` can reach and its edge cases (nothing in range, an empty set, a tie
  at a spawn pad) are the ones a run cannot be relied on to produce.
- `Solution.VehicleId` is its own field: an actor id and a vehicle id are different namespaces that
  overlap numerically.
- `vehicleHoldDistanceMeters` defaults to **4 m**, and a test pins it against
  `SeatArbiter.MaxSeatReachMetres` rather than against a number restated in a comment. 8 m — the
  player default — would stop the client outside the reach the arbiter measures, which is
  `RejectedTooFar`: a round trip spent to be told no.
- A step naming **both** `approachVehicle` and `aimAtPlayer` is refused at **load** time by
  `ScriptedInputProgramme.FindConflictingStep`. Whichever the code picked, the other is a sentence
  somebody wrote that the run did not honour, and the symptom downstream is a bearing nobody can
  trace.

## 3. Evidence — the static half

**15 tests**, **nine mutants all observed RED**: the radius stops bounding the scan; a tie goes to
the later index; the scan runs to array capacity rather than to `count`; the hold distance leaves
the arbiter's reach; the conflict check finds nothing; a vehicle step uses the player hold distance;
the two solves share a memo key; the solver reaches past the pose seam; the record stops naming the
vehicle.

## 4. Evidence — the run

**`artifacts/lane-b/o2-vehicle-01`**, driver checkpoints, with the new `targetVehicleId` field:

| checkpoint | t | aim | position | seat | vehicle inputs |
|---|---|---|---|---|---|
| `spawned` | 5.0 s | `null` | (2087, 1140) | — | 0 |
| `walking-to-vehicle` | 7.0 s | `resolved: false` | (2087, 1140) | — | 0 |
| `at-vehicle` | 47.0 s | `vehicle 15, 3.99 m` | (2095, 1148) | — | 0 |
| `seat-requested` | 49.0 s | `vehicle 15, 3.99 m` | (2095, 1148) | — | 0 |
| `driving` | 52.0 s | `vehicle 15, 0.68 m` | (2097, 1150) | **15** | 81 |
| `driven` | 72.0 s | `vehicle 15, 0.68 m` | (2156, 1209) | **15** | 618 |

Every claim the phase made, in one run: the verb **resolved** a vehicle by id, the client **walked**
to it, it **stopped at 3.99 m** — inside both the 4 m hold and the arbiter's 6 m reach — the seat
request was **granted**, and the hull then travelled **83 m**.

`resolved: false` at t=7 s is the honest first reading rather than a fault: no vehicle had been
replicated to a client seven seconds into its life, the step's own `moveZ` stood, and the recorder
wrote the miss down — which is exactly the behaviour `AppendAim`'s remark asks for.

## 5. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | The step walks to the nearest replicated vehicle within the radius and stops inside `MaxSeatReachMetres` | **MET** — 3.99 m, then a granted seat |
| 2 | The arithmetic stays in `ScriptedAim`; the solver keeps lookups and a transform read | **MET** — pinned, and the pose-seam mutant is red |
| 3 | The record names the vehicle it resolved, or records none | **MET** — `targetVehicleId: 15`, and `resolved: false` at t=7 |
| 4 | Observed RED before the fix | **MET** — nine mutants |
| 5 | Gates exit 0 | **MET** |

## 6. Noticed while running, and worth writing down

**A spawn pin can fail for one team, and the harness says so in the same line it succeeds.**
`o3-grenade-02` was run with `-SpawnIndex 0` and produced this:

```
[lane-b] spawn pinned to index 0 of 6 at (1088.82, 103.45, 951.98) ...
         team0Eligible=False team1Eligible=True
[net]    actor 41 (team 0) has no eligible spawn point among 6, so it stays where it is.
```

The driver was never placed and fell from 846 m at the world origin, and the whole run graded
nothing. Slot eligibility is not static — the same index reads `team0Eligible=True` in older logs —
so pinning is not the reproducibility lever it looks like. **That is more of X-28**, and it is
recorded here because it is the second time this session that a pinned run was less comparable than
an unpinned one, not less.
