# Phase S1 — The seat link becomes a pair, and X-58 stops having a producer to find

- **Track:** [`plan.md`](../plan.md) · **Effort:** S–M (1 d)
- **Depends on:** nothing. Closes a row orphan-closure O6 filed, contained and could not root-cause.
- **Closes:** **X-58** (a vehicle seat can be booked by a body that does not think it is sitting
  in it) — **two producers, not one**: the non-atomic pair (§ 2) and a double entry with no throw
  in it at all (§ 5b), the second found by the run that closed the first.
- **Finds and fixes** a defect the same window was hiding: `MountedWeapon`'s network declaration
  resolved nothing on a dedicated server.
- **Files:** **X-59**, **X-60** — both pre-existing, both surfaced by these runs, neither fixed.

---

## 1. What O6 left, and why it was the right place to stop

X-58 was filed with two observation sites, a disconnect correlation and no producer. The report
said why:

> Guessing at it from two stack frames would be the "one more attempt" this track's own rules
> forbid.

That was correct against the evidence it had. What it did not have is that the question is
**decidable by exhaustion**, and cheaply.

## 2. The pair has five write sites, and that is the whole search space

`Seat.occupant` is written in exactly two places, `Actor.seat` in exactly three:

```
$ grep -rn "seat = null\|seat=null" --include="*.cs" Ironfront_Reborn/Assets/Scripts
Assembly-CSharp/Actor.cs:270    (SpawnAt)
Assembly-CSharp/Actor.cs:1153   (LeaveSeat)
```

| Site | Writes | Order it used |
|---|---|---|
| `Actor.EnterSeat` | both | `seat.SetOccupant(this)` … **5 statements** … `this.seat = seat` |
| `Actor.LeaveSeat` | both | `seat.OccupantLeft()` … `seat = null` |
| `Actor.SpawnAt` | both | `seat.OccupantLeft()` … `seat = null` |
| `Seat.SetOccupant` | the seat's half | first statement of the method |
| `Seat.OccupantLeft` | the seat's half | first statement of the method |

Every one of the three transitions **publishes one half, runs a re-entrant callback, and then
publishes the other**. That is the defect, and it is structural:

- `EnterSeat`'s window spans `SetOccupant` (→ `Vehicle.OccupantEntered` → `Car.DriverEntered`,
  `Tank.DriverEntered`), the transform re-parent, and `controller.StartSeated(seat)`. A throw
  anywhere in it leaves **the seat booked by a body whose own half is null** — X-58 verbatim.
- `LeaveSeat`'s and `SpawnAt`'s windows span `OccupantLeft` (→ `Vehicle.OccupantLeft` →
  `DriverExited`). A throw there leaves **the mirror state**: a body that still thinks it is
  sitting in a seat nobody occupies. Nothing in O6's containment covers that direction.

**So there is no single producer to name.** X-45 closed one throw in the entry window; O6 § 6b's
strict `Driver()` manufactured a second and was reverted. Both are instances, not the cause. The
cause is that the pair is not published atomically, and any future throw in either window
re-creates the state — which is precisely why two sessions of hunting produced a correlation and
no culprit.

**That accounts for every producer that needs a throw. It did not account for all of them** — see
§ 5b, which the run rather than the reasoning is what found.

## 3. The fix — publish the pair, then call out

`Seat.SetOccupant` and `Seat.OccupantLeft` each write the seat's half as their **first**
statement. So assigning the body's half immediately before the call leaves no callback between
the two writes, and no window to observe:

```csharp
// EnterSeat
this.seat = seat;          // body's half
seat.SetOccupant(this);    // seat's half is this method's first statement

// LeaveSeat / SpawnAt
Seat leaving = seat;
seat = null;               // body's half
leaving.OccupantLeft();    // seat's half is this method's first statement
```

**Not a `try/catch`** (**S-D2**). A catch restores consistency after the fact; this removes the
inconsistent state from the program. A throw after the pair is published now leaves a body that
is genuinely, consistently in the seat — which the eject and `HasDriver()` already handle
correctly — rather than a booking nothing can interpret.

**The O6 containments stay** (**S-D3**). `EjectOccupants`'s `occupant.seat == seat` check and
`HasDriver()`'s both-halves rule are now belt-and-braces, and their mutants stay green. Trading a
proved guard for an argument would be the wrong direction, and the argument covers only the
producers this phase can enumerate today.

## 4. What the window was also hiding — the mounted weapon that never declared itself

`Seat.SetOccupant` calls `weapon.DeclareToNet()` → `MountedWeapon.ResolveNetSeat()`, which opens:

```csharp
if (user == null || user.seat == null)
{
    netVehicleId = 0;
    ...
    return;
}
```

With the body's half assigned five statements later, **`user.seat` was always null here**, so
this guard was true on every entry, `netVehicleId` stayed 0, and `NetWeaponAuthority.Declare`
was never reached. The method's own remark names the consequence:

> on a dedicated server nothing drives a networked gunner's controller, so `CanFire` is never
> called and the weapon would never announce itself — leaving an authority that exists, compiles
> and grades nothing.

That is exactly what happened, and the trigger V6 task 3 installed to prevent it was itself
disabled by the ordering. It is fixed as a **consequence** of the reorder, not as a separate
change, and it is called out because a future reader tidying `EnterSeat` needs to be able to find
the mounted-weapon dependency from the weapon's side too — which is why the test asserting it
lives against `MountedWeapon.cs` as well.

## 5. Why the reorder is safe — enumerated, not assumed

The reorder makes `Actor.seat` visible *earlier* and clears it *earlier*. Every reader inside the
two windows:

| Reader | Reads | Affected? |
|---|---|---|
| `Vehicle.OccupantEntered` | `seat.occupant.aiControlled`, `.team` | No — the seat's half, set inside `SetOccupant` before the callback, unchanged |
| `Vehicle.OccupantLeft` | its `leaver` parameter | No — a parameter, not a field |
| `Car`/`Boat`/`Tank`.`DriverEntered`/`DriverExited` | wheels, audio, `ownerIndicator`, `Driver()` | No — none reads `Actor.seat` |
| `Seat.SetOccupant` → `MountedWeapon.ResolveNetSeat` | **`user.seat`** | **Yes — and it is the defect above, fixed** |

`EnterSeat`'s early-return guard (`seat.vehicle.dead || seat.IsOccupied()`) still runs before any
assignment, so a refused entry writes nothing.

## 5b. The run found a second producer, and it needed no throw

`s2-combat-01`, the first run on the atomic build, **still reported one booking** — a jeep
Passenger seat, fourteen lines after a match reset. So the window was not the only route in.

`Actor.EnterSeat` refused a dead vehicle and an occupied seat, but never a body **already sitting
somewhere else**. A second successful entry re-points that actor's own half and leaves the OLD
seat booked by a body that no longer agrees. Three of its four callers guard that externally
(`CanEnterSeat()` for the AI and the use-ray; `SwitchSeat` leaves first) — the network path,
`IronfrontNetBindings.TryEnterSeat`, does not. The arbiter is meant to cover it with
`RejectedAlreadySeated`, and does, **except where its record and the scene disagree**, which
`ServerVehicleRegistry.Clear()` causes deliberately at every round boundary.

The guard therefore goes in `Actor.EnterSeat`, before either half is written: the scene is
authoritative about the scene, and `TryEnterSeat`'s `false` becomes a refusal the bridge rolls
back (**V4-D7**), correcting the arbiter instead of being overruled by it.

## 6. Evidence — five mutants, all observed RED

| # | Mutation | What it restores |
|---|---|---|
| M15 | the body's half is assigned last again | X-58 verbatim, plus the silent mounted-weapon miss |
| M16 | a duplicate late assignment is left behind | the window, for everything between the two writes |
| M17 | `LeaveSeat` clears the seat's half first again | the mirror one-sided state |
| M18 | `SpawnAt` clears the seat's half first again | respawn-while-seated, the case a networked death takes |
| M24 | `EnterSeat` admits an already-seated body again | the double-entry producer § 5b names |

All four assert **order**, not presence: a version that sets both halves in the wrong order
contains every string they look for, compiles, and reproduces the defect exactly.

**M16 exists because "the fix is present" is not the property.** Leaving the old late assignment
in place alongside the new early one reads as harmless and puts the window straight back for
anything between them.

**M24 caught its own test.** The first version searched the method body for `IsSeated()` — which
appears in the new guard's own comment — so it passed on a tree with the check deleted. It now
matches the guard expression, the same correction O6 made to
`TheSuspensionIsGatedAtTheOneGateAndNotAtTheSiteThatHappenedToThrow` after prose turned it red.

## 7. Acceptance

| # | Criterion | How it is judged |
|---|---|---|
| 1 | Both halves of the seat link are published before any callback, in all three transitions | M15–M18 |
| 2 | The mounted weapon's declaration is reachable on a server | the `ResolveNetSeat` ordering test |
| 3 | O6's eject and `HasDriver` containments are unchanged | their five mutants stay green |
| 4 | Observed RED before the fix | four mutants |
| 5 | A lane-A drill through ≥1 reset with zero one-sided bookings reported | **MET** — `s2-combat-03`, 3 resets, 0 bookings, 8/8 held, exit 0 |
| 6 | Zero throws at any site | **NOT met, and not this row's** — 2 `NullReferenceException` at `PushAntiStuckEvent` (**X-60**, which threw 5× and 3× in runs predating this work) and 56 `ArgumentException` (**X-59**, likewise). Both filed with counts rather than absorbed |

## 8. Out of scope, and said so

**Whether a disconnect should leave the scene's seat booked at all.** `ServerTickLoop.OnClientDisconnected`
releases the slot and forgets the actor without calling `Actor.LeaveSeat()`, so a body handed back
to the bot brain is still sitting where the departed client left it. That is consistent — both
halves agree — so it is not X-58, and this phase does not widen into it. It is worth its own row
if a run ever shows a re-claimed slot inheriting a seat.
