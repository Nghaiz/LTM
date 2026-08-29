# O6 report — the AI null-reference cascade, and the vehicle that took its riders with it

- **Phase:** [`phase-o6-null-reference-cascade.md`](../phases/phase-o6-null-reference-cascade.md) · **Date:** 2026-08-28
- **Closes:** **X-55**, **X-56**, **X-57** · **Files and contains:** **X-58**
- **Commit:** `fix(client): a match reset stops destroying the bots riding its vehicles`

---

## 1. Where this phase came from

It was not in the track's opening scope. O1's run produced **4,183 `NullReferenceException`s in
150 s**, the O1 report filed them as X-55 and X-56 rather than fixing them blind — and bounded
them with a control run (`o1-move-01`: same build, `move` behaviour, **0 seat requests, 0 vehicle
inputs, 0 trigger ticks**, still **796** throws) so that nobody would mistake them for O1's own
work. The track was then asked to finish them too.

## 2. Reading the log instead of the code

The two sites had nothing in common except a timestamp:

| Site | Count | First at log line | The line before |
|---|---|---|---|
| `HasEffectiveWeaponAgainst` → `Actor.Position()` | 45 | 1851 | 1805 `match phase -> Resetting` |
| `LocalAvoidanceVelocity` → `Actor.Position()` | 2,044 | 3518 | 3458 `match phase -> Resetting` |

Neither throws once in the twenty minutes of match before the first reset. Both start within
fifty lines of one. **The trigger is the round transition** — which is also why the `move` control
run reproduced it with no combat and no driving at all.

## 3. The cause

`Actor.EnterSeat` does `base.transform.parent = seat.transform`, so **an occupant is a child of
the vehicle's GameObject**. `Vehicle.Die` knows that and empties the seats before the wreck is
cleaned up. `MatchController.OnResetRequested` → `NetWorldLifecycle.RaiseReset()` →
`VehicleSpawner.OnWorldReset` did not: it called `Destroy(lastSpawnedVehicle.gameObject)` on a
**live, occupied** vehicle, and Unity took the riders with it.

Every bot in a seat at the reset was therefore destroyed **without `Actor.Die` ever running**. So
`AiActorController.Die` — the only caller of `Squad.DropMember` — never fired, and `Squad`, a
plain C# object with no lifecycle at all, kept the corpse. Unity's overloaded `==` reports a
destroyed object as equal to null, so the corpse passes `member != this`, passes
`member.actor.fallenOver` (a managed field read, which does not throw), and is then asked for
`Position()` — which reaches `base.transform` and does.

**This is X-49's own sentence one register further out**, and X-49's remark had predicted the
mechanism in writing.

## 4. The fix — two parts, and one thing deliberately not done

- **O-D8** — `Vehicle.EjectOccupants()`, called **before** the `Destroy`. Not routed through
  `Die`: a round transition is not a kill, and `Die` would score deaths, spawn a wreck, detonate
  an explosion and hand 200 balance damage to eight bots for being seated when the clock ran out.
  Rescuing them from `Vehicle`'s own `OnDestroy` is not available — by then Unity has already
  committed to the hierarchy — so the eject has to sit at the call site that destroys.
- **O-D9** — `AiActorController.OnDestroy` leaves the squad. The backstop for every *other* destroy
  path, including ones not yet written.

**Not done: null-guarding the consumers.** A guard inside `LocalAvoidanceVelocity` would silence
2,044 lines and leave the corpse in the roster to be counted by `UpdateGroupedUpFlag`, averaged
into the squad's centre through `member.transform.position` — which throws the same way — asked
for a target by `GetTarget`, and made leader by `DropMember`. Silencing the loudest reader is not
mending the roster, and a test asserts the guard is absent.

## 5. What the phase's own first run found — X-57

`o6-combat-01` took both sites to **zero** and left **five** throws at a third:

```
NullReferenceException
  at AiActorController.PushAntiStuckEvent () ... AiActorController.cs:628
  at AiActorController+<AiVehicle>d__153.MoveNext () ... AiActorController.cs:585
```

Line 628 is `squad.squadVehicle.stuck = true` on a body whose `squad` is null — which
`AiActorController.Die`'s own remark calls **ORDINARY**, because every networked player slot is
one of these characters. It is **X-45's defect at a site X-45 did not reach**, and it only became
reachable once O1 let a networked player actually drive.

**`Suspend()` was half a suspension.** `IAiDriver.Suspend` sets `enabled = false`, and **Unity
does not stop a coroutine when a MonoBehaviour is disabled** — only when the GameObject is
deactivated. It stops `Update`; all eight AI coroutines kept running on a body the server was
driving. X-45's own remark asserted the opposite ("stops `Update` and the eight coroutines"); that
sentence has been corrected in place rather than left to mislead the next reader.

The guard therefore goes on **`AiWorkAllowed()`**, the one gate all eight coroutines already park
on (**O-D10**) — not on `PushAntiStuckEvent`. The same coroutine calls `squad.ExitVehicle()` and
`squad.MoveTo()` two branches away on the same squadless body.

**Five was not reported as a 99.9 % improvement.** The acceptance criterion was zero, so five
opened a row.

## 6. What the second and third runs found — X-58, filed and contained

`o6-combat-02` took X-57's site to zero and threw **twice**, both at a reset, both from the eject
itself:

```
[net] a world-reset subscriber threw: System.NullReferenceException
  at Actor.LeaveSeat () ... Actor.cs:1145
  at Vehicle.EjectOccupants () ... Vehicle.cs:806
```

`Seat.IsOccupied()` was true and the occupant's own `seat` was **null** — a one-sided booking.
X-45's remark describes exactly how one is made: a throw *after* `Seat.SetOccupant` and the
transform re-parent but *before* `Actor.EnterSeat` finishes, leaving "the seat booked, the body
welded to it, and the rest of the entry never ran".

The eject now checks `occupant.seat == seat`, and on a mismatch **reports loudly and unparents the
body anyway** — the body is still welded to a hierarchy about to be destroyed, so the parent has
to go whatever the seat's records say. A silent `continue` would have been the fallback this
codebase forbids; worse, letting `LeaveSeat` throw aborted the loop and left every **later** seat
of that vehicle un-ejected, which is the one thing an eject must not do.

`o6-combat-03` then took *that* to zero across two resets and left **two** throws at a **second
observation site of the same state**:

```
NullReferenceException
  at AiActorController.CarInput () ... AiActorController.cs:1704   ← actor.seat.vehicle
  at Car.FixedUpdate () ... Car.cs:153                             ← Driver().controller.CarInput()
```

`Vehicle.HasDriver()` read `seats[0].IsOccupied()`, which is `occupant != null` — the seat's half
of the link and nothing else. So a one-sided booking made the car ask a body that is not sitting in
it for a steering input, once per physics step. **Both throws land immediately after a transport
`Connection.Fail`**, which is the one real lead this phase has on the producer and is recorded on
the row.

`Boat`, `Helicopter`, `Tank`, `Javelin` and this class's own ram-damage check read the same pair
and were latently exposed to exactly the same throw.

**Contained at the register, not at the reader:** `HasDriver()` now requires **both** halves to
agree, so a corrupt booking reads as *driverless* rather than throwing, and every one of those
consumers is safe at once. It is reported **once per vehicle** — `HasDriver` runs inside
FixedUpdate for every vehicle in the map, and an unconditional `Debug.LogError` there would bury
the thing it is reporting, which is the failure shape this whole phase exists to remove.

### 6b. The strict `Driver()` manufactured the corruption it was written to survive

`Driver()` was made strict in the same edit, "to keep the pair consistent". The very next build
said no, in the lane-B run that `-Build` performs on its way out:

```
NullReferenceException
  at Tank.DriverEntered ()      ... Tank.cs:130      ← ownerIndicator.SetOwner(Driver().team)
  at Vehicle.OccupantEntered () ... Vehicle.cs:375
  at Seat.SetOccupant ()        ... Seat.cs:63
  at Actor.EnterSeat ()         ... Actor.cs:1113
```

`Actor.EnterSeat` calls `seat.SetOccupant(this)` **before** it assigns its own `seat` field, and
`SetOccupant` reaches `Tank.DriverEntered`, which reads `Driver().team`. **Inside that window the
two halves always disagree**, so the strict `Driver()` returned null, threw, aborted `EnterSeat`
— and left the seat booked with the body's half unset. That is X-58, produced on purpose by the
containment for X-58: **6 vehicles reporting a one-sided booking in a single run that had none
before it.**

`Driver()` is therefore permissive again, and the method's remark now carries the reason so nobody
tidies it back. `HasDriver()` stays strict, which is safe because every reader outside the entry
sequence pairs the two — and the two that do not (`Tank.DriverEntered`, `Tank.DriverExited`) run
exactly at the moments when the permissive answer is the correct one. A fourteenth mutant pins it.

**This is the phase's own rule catching the phase.** The check that found it was not a test, it was
a run — the same instrument that has found something new at every step here. Had the strict pair
been shipped on the reasoning that it was "obviously safer", it would have broken every tank entry
in the game.

**The producer of the one-sided booking is still not identified**, and X-58 is filed rather than
guessed at. What it now has that it did not have an hour ago: two observation sites, a disconnect
correlation, and two error lines that name the vehicle and the occupant when it next appears.

## 7. Evidence

**The static half — 12 tests, 14 mutants, all observed RED:**

| # | Mutation | What it restores |
|---|---|---|
| M1 | the reset never empties the seats | the defect verbatim |
| M2 | the seats are emptied *after* the `Destroy` | a fix that compiles and rescues nobody |
| M3 | the eject damages its occupants | the round transition as a kill |
| M4 | the `OnDestroy` backstop is deleted | every other destroy path unguarded |
| M5 | the backstop drops its `InSquad` guard | a throw on every squadless player-slot body |
| M6 | the reader is null-guarded instead | silencing rather than mending |
| M7 | a burned vehicle stops hurting its occupants | the companion direction |
| M8 | the suspension stops only `Update` again | X-57 verbatim |
| M9 | the throw site is guarded instead of the gate | muting one stack trace |
| M10 | the eject trusts the seat's half of the link | X-58's abort |
| M11 | a one-sided booking is skipped quietly | the silent fallback |
| M12 | `HasDriver` reads only the seat's half again | the `Car.FixedUpdate` throw |
| M13 | the one-sided driver report fires every step | a log that buries its own finding |
| M14 | `Driver()` is made strict too | § 6b — a fix that produces its own defect |

Two of these assert **order**, not presence — M2 and M10 — because presence is not the property
that matters here.

**One of the gates caught its own author.** The first version of
`TheSuspensionIsGatedAtTheOneGateAndNotAtTheSiteThatHappenedToThrow` counted occurrences of the
string `AiWorkAllowed()` and expected 10; correcting X-45's prose added an eleventh **in a
comment** and the test went red. It now counts `if (!AiWorkAllowed())` — the call, not the name —
which is the same trap `check-net-layering.ps1` documents, met from the other side.

**The run:** see § 8.

## 8. The run

`artifacts/lane-a/o6/o6-combat-04` — 8 clients, **221 s**, 8/8 held to the end, **five match
resets**: **0** `NullReferenceException`, at any site, and `Errors: []` on the report. Against
`artifacts/lane-a/o1/o1-combat-01`: **4,183** on the same drill.

**Five runs, and the shape of them is the point.** Each took its predecessor's sites to zero and
exposed the next thing down; none of the intermediate numbers was reported as a result:

| run | X-55 site | X-56 site | X-57 site | eject (X-58) | `CarInput` (X-58) | total |
|---|---|---|---|---|---|---|
| `o1-combat-01` (before) | 2,044 | 45 | 3 | — | 2 | **4,183** |
| `o6-combat-01` | 0 | 0 | 5 | — | 0 | **5** |
| `o6-combat-02` | 0 | 0 | 0 | 2 | 0 | **2** |
| `o6-combat-03` | 0 | 0 | 0 | 0 | 2 | **2** |
| `o6-combat-04` | 0 | 0 | 0 | 0 | 0 | **0** |

The `CarInput` column is the honest one: those two throws were in the ORIGINAL run all along,
buried under 4,180 others, and only became visible once everything louder had gone. A phase that
had stopped at `o6-combat-01`'s five, or at `o6-combat-02`'s two, would have shipped them.

**The X-58 containment is load-bearing, not decorative.** The same run reports it twice:

```
[net] the driver seat of 'quadbike(Clone)' is booked by 'Ai Character Optimizations(Clone)',
      which does not think it is seated there. Treating the vehicle as driverless.
      Reported once per vehicle; the booking belongs to X-58.
```

Two real one-sided bookings, on two quadbikes, in a run with zero throws — each of which would
have thrown once per physics step for as long as the vehicle stood there. The eject path reported
none, so both were still standing at every reset rather than caught there.

**And the lane-B set that the build runs on its way out is the control for § 6b:** on the strict-
`Driver()` build it carried 6 `NullReferenceException`s and 6 one-sided bookings; on this one,
**0 and 0**.

## 9. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | A world reset destroys the vehicle and not its occupants | **MET** |
| 2 | A destroyed `AiActorController` is not in `squad.members` afterwards | **MET** |
| 2b | A suspended controller does no AI work, not merely no `Update` | **MET** — X-57 |
| 3 | `Vehicle.Die`'s eject and its damage are unchanged | **MET** — pinned by M7 |
| 4 | Observed RED before the fix | **MET** — fourteen mutants |
| 5 | A lane-A drill through ≥1 reset with **zero** throws at **any** site | **MET** — five resets, zero |
| 6 | `dotnet test`, `SpecChecker`, `check-net-layering.ps1`, `check-unity-meta.ps1` exit 0 | **MET** — 1,972 tests, 90 constants |

## 10. Out of scope, and said so

**Whether a match reset should respawn or reposition the surviving actors at all.** Today it
resets vehicles, tickets and capture-point ownership and leaves bodies where they stand. This
phase restores that intent rather than extending it; a round that should start with everyone at a
spawn point is a design change and gets its own row.

**X-58's producer.** Filed with the state, the two observations, and an error line that will name
the vehicle and the occupant next time. Guessing at it from two stack frames would be the
"one more attempt" this track's own rules forbid.
