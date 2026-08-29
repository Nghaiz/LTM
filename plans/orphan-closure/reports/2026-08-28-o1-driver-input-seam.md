# O1 report — a networked player in a driver's seat drives the vehicle

- **Phase:** [`phase-o1-driver-input-seam.md`](../phases/phase-o1-driver-input-seam.md) · **Date:** 2026-08-28
- **Closes:** **X-46** → unblocks check 11's *drive* verb (**B-11**) and any driving window **B-10** would be graded from
- **Commit:** `fix(client): a networked driver's input reaches the vehicle`

---

## 1. What was wrong

`NetDriverInputSink.Attach` did `GetComponent<FpsActorController>()` and returned `null` without
one. `IronfrontNetBindings.CreatePlayerBody` instantiates `ActorManager.actorPrefab` — the **bot**
character — so a player-slot body carries an `AiActorController` and **never** an
`FpsActorController`. Every networked driver took the null branch: the bridge counted an
unreachable controller, the authority kept accepting `C_VEHICLE_INPUT`, and there was nothing on
the other end of it.

The sink's own remark had predicted the case in writing. It went unseen because nothing could ask
for a seat until R2 gave the shipped client a sender and R5 gave lane A one.

## 2. What changed

**O-D1 — a controller-agnostic seam, not an `FpsActorController` on the server-side body.**
`NetVehicleAxisRelay` (a plain `MonoBehaviour`, not an `ActorController`) carries the accepted
axes; `Attach` falls back to it; `AiActorController`'s `CarInput`, `BoatInput` and
`HelicopterInput` return it **when the controller is suspended**, above the `hasPath` return rather
than below it.

**O-D2 — the condition is `enabled`**, the same one X-45 and X-47 established, so a real bot's
controller never reads the relay and an AI convoy still drives itself.

`UnreachableControllers` now counts a destroyed body and nothing else, which is what its own
summary always claimed.

## 3. Evidence — the static half

Three gates in `VehicleClientSourceInvariantTests`, **five mutants all observed RED**: dropping the
`CarInput` guard, moving the `BoatInput` guard below `hasPath`, reverting `HelicopterInput` to zero,
restoring `Attach`'s null return, and making the relay an `ActorController` subclass.

Live-domain compile verified through the Editor by reflection rather than by a log line:
`NetVehicleAxisRelay` base `UnityEngine.MonoBehaviour`, `NetRelayDriverInputSink` implements
`IDriverInputSink`, `AiActorController.CarInput` returns `Vector2`.

## 4. Evidence — the run

**`artifacts/lane-a/o1/o1-combat-01`** — 8 clients / 150 s, 8/8 held to the end:

```
verb Drive   first at decoded tick 875 (t+11.4s) by client 3, 3426 sighting(s)
             - vehicle 10 moved 2.5 m while this client held seat 0 of it,
               having sent 56 vehicle input(s)
drill sent   47 seat request(s), 42 refused; 5822 vehicle input(s)
```

Against `artifacts/lane-a/r5/r5-combat-05`, which the row was filed from: **1,285 accepted
`C_VEHICLE_INPUT` messages and no `Drive` verb at all**, with the server naming the body twice —
*"actor 43 took a driver seat and no driver input sink could be attached to
`Ai Character Optimizations(Clone)`"*.

**`grep -c "no driver input sink could be attached" o1-combat-01-server.log` → 0.** Both halves of
the row's sentence moved together.

**Independently confirmed by a lane-B run that was not about this row.**
`artifacts/lane-b/o2-vehicle-01` (O2's evidence) has the driver take seat 0 of vehicle 15 at t=49 s
and travel from `(2097, 1150)` to `(2156, 1209)` by t=72 s — **83 m**, on 618 vehicle inputs. A hull
that moves 83 m under a scripted client is the whole of what X-46 said could not happen.

## 5. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | A relay on a suspended controller is what the three overrides return; an enabled one ignores it | **MET** — asserted per method, and the relay is reached exactly once in each |
| 2 | `Attach` returns non-null for a body with no `FpsActorController`, and still returns the controller sink when one is present | **MET** |
| 3 | The source-invariant suite stays green (no new `ActorController` subclass, `SetInputSource` keeps its call site, `aiControlled`'s freeze untouched) | **MET** |
| 4 | Observed RED before the fix | **MET** — five mutants |
| 5 | Gates exit 0 | **MET** |

## 6. What the run surfaced that O1 did not fix

**The lane-A server log carries 4,183 `NullReferenceException`s in 150 s.** They are reported here
rather than left in a log nobody reads, and they are **filed as their own rows** rather than fixed
blind — the same call V-D2 makes.

Two distinct throw sites, classified from the stacks:

| Site | Count | Reached from |
|---|---|---|
| `AiActorController.LocalAvoidanceVelocity` → `Actor.Position()` | 2,044 | `Actor.Update` → `UpdateMovement` → `controller.Velocity()` |
| `AiActorController.HasEffectiveWeaponAgainst` → `Actor.Position()` | 45 | `FindPotentialTargets`'s `RemoveAll` predicate |

**Neither is O1's, and the evidence for that is a control run rather than an argument.**
`artifacts/lane-a/o1/o1-move-01` — same build, `move` behaviour, **0 seat requests, 0 vehicle
inputs, 0 trigger ticks** — still produces **796** of them, with `LocalAvoidanceVelocity` the same
dominant site. The three methods O1 touched are `CarInput`, `BoatInput` and `HelicopterInput`, and
no call path reaches `Velocity()` from any of them.

**What is NOT claimed:** that these predate O1 entirely. The R5 lane-A logs show zero of either
site, but they were taken on a build without X-45, X-47 or this track, so "R5 was clean" does not by
itself date the defect. The control run bounds it to *not the driver-input path*; dating it needs a
run on the pre-O1 tree, which is the first thing the new rows ask for.

Filed as **X-55** (`LocalAvoidanceVelocity` walks a squad roster holding destroyed members) and
**X-56** (`FindPotentialTargets` hands a destroyed actor to `HasEffectiveWeaponAgainst`).

> **Closed the same day by O6**, and the control run above turned out to be the load-bearing part
> of this section: it is what stopped the cascade being fixed blind as part of X-46, and what
> pointed at the match reset. Cause, fix and evidence:
> [`2026-08-28-o6-null-reference-cascade.md`](2026-08-28-o6-null-reference-cascade.md). O6 also
> found a third site (**X-57**) and a fourth state (**X-58**) that this run's 4,183 lines had
> buried.

## 7. Out of scope, as the phase said in advance

`Vehicle.cs:232` gates ram damage on `!Driver().aiControlled`, and a player-slot body reports
`aiControlled = true` because it is the bot prefab. That is a second consequence of the same prefab
choice, it is not the driver input sink, and it stays open under its own row if it ever needs one.
