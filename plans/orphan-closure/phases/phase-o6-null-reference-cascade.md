# Phase O6 — The AI null-reference cascade, from the vehicle that took its riders with it

- **Track:** [`plan.md`](../plan.md) · **Effort:** M (2 d)
- **Depends on:** nothing. Found by O1's run; fixed on its own evidence.
- **Closes:** **X-55** (`LocalAvoidanceVelocity` walks a squad roster holding destroyed members),
  **X-56** (`FindPotentialTargets` hands a destroyed actor to `HasEffectiveWeaponAgainst`) and
  **X-57** (a suspended bot brain keeps running all eight of its AI coroutines) — the third
  found by this phase's own first run. **Files X-58**, found by its second.

---

## 1. What the run showed, and what it did not

`artifacts/lane-a/o1/o1-combat-01-server.log` carries **4,183 `NullReferenceException`s** in 150 s.
The O1 report filed them as two rows rather than fixing them blind, and bounded them with a control
run (`o1-move-01`, same build, `move` behaviour, zero seat requests) that still produced **796**.
So they are not the driver-input path. What the control run could not say is what they ARE.

The log says it, in the line numbers:

| Site | Count | First appears at | The line before it |
|---|---|---|---|
| `AiActorController.HasEffectiveWeaponAgainst` → `Actor.Position()` | 45 | log line 1851 | 1805 `[net] match phase -> Resetting` |
| `AiActorController.LocalAvoidanceVelocity` → `Actor.Position()` | 2,044 | log line 3518 | 3458 `[net] match phase -> Resetting` |

Neither site throws once in the twenty minutes of match before the first reset. Both start within
fifty lines of one. **The trigger is the match reset, not combat and not driving** — which is also
why the `move` control run reproduced it: a reset happens either way.

## 2. The cause — a vehicle destroyed without dying takes its riders with it

`Actor.EnterSeat` parents the body to the seat:

```csharp
base.transform.parent = seat.transform;
```

so every occupant is a **child of the vehicle's GameObject**. `Vehicle.Die` knows this and empties
the seats before the wreck is cleaned up. `MatchController.OnResetRequested` →
`NetWorldLifecycle.RaiseReset()` → `VehicleSpawner.OnWorldReset` does **not** — it calls
`Destroy(lastSpawnedVehicle.gameObject)` on a vehicle that is alive and occupied, and Unity
destroys the children with the parent.

Every bot riding a vehicle at the round transition is therefore **destroyed without `Actor.Die`
ever running**. `Actor.OnDestroy` (X-49) still empties `ActorManager`'s registers, but `Squad` is a
plain C# object with no lifecycle at all: `Squad.members` keeps the destroyed
`AiActorController`, `AiActorController.Die` — the only caller of `DropMember` — never runs, and
every surviving squad-mate then reads that member once per frame forever.

**This is X-49's own sentence, one register further out.** Its remark predicted the shape:
Unity's overloaded `==` reports a destroyed object as equal to null, so a stale entry passes every
`member != this` test and is then dereferenced. `LocalAvoidanceVelocity` reads `member.actor` — a
managed field, which does not throw — then `actor.Position()`, which reaches `base.transform` and
does.

## 3. The decisions

**O-D8 — a vehicle destroyed without dying puts its riders down first, and does not kill them.**
The eject goes on `Vehicle`, next to the one `Die` already performs, because the knowledge that a
seat parents its occupant belongs to the vehicle and to nothing else. It is NOT `Die`: a round
transition is not a kill, and routing the reset through `Die` would score deaths, spawn a wreck,
detonate an explosion and hand out 200 balance damage to eight bots for the crime of being seated
when the clock ran out.

**O-D9 — a destroyed `AiActorController` leaves its squad, from its own `OnDestroy`.**
O-D8 removes the one path that is known to destroy a seated bot; O-D9 makes every *other* path
safe, including the ones not yet written. This is the exact remedy X-49 applied to
`ActorManager.actors` and `Vehicle` applied to the vehicle register — the register drops the entry
on the way out, rather than each of a dozen consumers null-guarding a corpse.

**What is deliberately NOT done: null-guarding the consumers.** A guard inside
`LocalAvoidanceVelocity` would silence 2,044 lines and leave the destroyed member in the roster to
be counted by `UpdateGroupedUpFlag`, averaged into the squad's centre by
`member.transform.position` — which throws in exactly the same way — asked for a target by
`GetTarget`, and made leader by `DropMember`. Silencing the loudest reader is not fixing the
roster.

## 4. Tasks

- **O6.1 (S)** `Vehicle.EjectOccupants()` — every occupied seat's `LeaveSeat()`, no damage.
- **O6.2 (S)** `VehicleSpawner.OnWorldReset` calls it before `Destroy`.
- **O6.3 (S)** `AiActorController.OnDestroy` → `squad.DropMember(this)` under `InSquad()`.
- **O6.4 (S)** `AiWorkAllowed()` also answers *is this controller steering this body* — added
  after the phase's own first run, see § 4b.

## 4b. What the first run found — X-57, and why it is in this phase

`o6-combat-01` took the two sites above to **zero**, and left **five** throws at a third:

```
NullReferenceException
  at AiActorController.PushAntiStuckEvent () ... AiActorController.cs:628
  at AiActorController+<AiVehicle>d__153.MoveNext () ... AiActorController.cs:585
```

Line 628 is `squad.squadVehicle.stuck = true;` on a body whose `squad` is **null** — the case
`AiActorController.Die`'s own remark calls ORDINARY, because *every networked player slot is one
of these characters*. It is **X-45's defect at a site X-45 did not reach**, and it only became
reachable once O1 let a networked player actually drive.

**The cause is that `Suspend()` is half a suspension.** `IAiDriver.Suspend` sets
`enabled = false`, and Unity does not stop a coroutine when a MonoBehaviour is disabled — it stops
`Update` and nothing else. All eight AI coroutines kept running on a body the server was driving.

**So the guard goes on `AiWorkAllowed()`, the one gate all eight already park on**, not on
`PushAntiStuckEvent`. The same coroutine calls `squad.ExitVehicle()` and `squad.MoveTo()` two
branches away on the same squadless body; guarding the site that happened to throw is muting a
stack trace, not fixing a suspension.

This is written as a task of O6 rather than a new phase because it is the same defect class, found
by this phase's own instrument, on the same run — and because a phase that reported "we took 4,183
to 5" and stopped would be exactly the green this track exists to refuse.

## 5. Acceptance

1. A world reset destroys the vehicle and **not** its occupants.
2. A destroyed `AiActorController` is not in `squad.members` afterwards.
2b. A suspended `AiActorController` does no AI work — not merely no `Update`.
3. `Vehicle.Die`'s own eject and its damage are unchanged — a burned vehicle still hurts the
   people inside it.
4. **Observed RED before the fix**, named in the report with the mutation used.
5. **The run:** a lane-A drill through at least one match reset with **zero**
   `NullReferenceException` at **any** site. A drop is not the criterion; zero is — which is
   why the first run's 5 opened X-57 instead of being reported as a 99.9 % improvement.
6. `dotnet test`, `SpecChecker`, `check-net-layering.ps1`, `check-unity-meta.ps1` exit 0.

## 4c. What the second and third runs found — X-58, filed and contained

`o6-combat-02` took X-57's site to zero and threw twice, both at a reset, both from the eject
itself: `Actor.LeaveSeat` at `Actor.cs:1145`, whose first statement reads `seat.transform`. The
seat was booked (`Seat.IsOccupied()` true) and the occupant's own `seat` was **null** — a
one-sided link, made exactly the way X-45's remark describes: a throw *after* `Seat.SetOccupant`
and the transform re-parent but *before* `Actor.EnterSeat` finishes.

The eject now checks `occupant.seat == seat` and, on a mismatch, **reports loudly and unparents
the body anyway** — it is still welded to a hierarchy about to be destroyed, so the parent has to
go whatever the seat records say. Letting `LeaveSeat` throw aborted the loop and left every LATER
seat of that vehicle un-ejected, which is the one thing an eject must not do; a silent `continue`
would have been the fallback this codebase forbids.

`o6-combat-03` then took *that* to zero and threw twice at a **second site of the same state**:
`Car.FixedUpdate` → `Driver().controller.CarInput()` → `actor.seat.vehicle`, because
`Vehicle.HasDriver()` read `seats[0].IsOccupied()` — the seat's half alone. `HasDriver()` and
`Driver()` now require **both** halves, so a corrupt booking reads as driverless rather than
throwing, and `Boat`, `Helicopter`, `Tank`, `Javelin` and the ram-damage check are safe with it.
Reported **once per vehicle**: `HasDriver` runs in FixedUpdate for every vehicle in the map.

**Both of those throws were in the ORIGINAL run**, buried under 4,180 louder lines.

`Driver()` was made strict in the same edit and **reverted**: `Actor.EnterSeat` calls
`seat.SetOccupant(this)` before assigning its own `seat` field, and `SetOccupant` reaches
`Tank.DriverEntered`, which reads `Driver().team` — so inside the entry window the strict version
threw, aborted the entry and produced the very corruption it was written to survive (6 vehicles in
one run that had none before it). The reason lives on the method so nobody tidies it back.

**The producer is not identified and is filed as X-58 rather than guessed at.** Both new error
lines name the vehicle and the occupant, so the next run that hits it says who made it — and both
`CarInput` throws landed immediately after a transport `Connection.Fail`, which is the one lead
this phase has.

## 6. Out of scope

**Whether a match reset should respawn or reposition the surviving actors at all.** Today it
resets vehicles, tickets and capture-point ownership and leaves bodies where they stand; this
phase restores that intent rather than extending it. If round two should start with everyone at a
spawn point, that is a design change and gets its own row.
