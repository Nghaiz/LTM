# S1 + S2 report — the seat link becomes a pair, and the fourth verb fires

- **Phases:** [`phase-s1-atomic-seat-link.md`](../phases/phase-s1-atomic-seat-link.md),
  [`phase-s2-burn-verb.md`](../phases/phase-s2-burn-verb.md) · **Date:** 2026-08-29
- **Closes:** **X-58** · **Grades B-11 PASS**
- **Also fixes:** a `MountedWeapon` network declaration that resolved nothing on a dedicated
  server, found as a consequence of S1 rather than looked for
- **Files:** **X-59** (an `ArgumentException` storm, 56–76 per run) and **X-60** (X-57's site
  still throwing) — both **pre-existing**, both surfaced by these runs, neither fixed here
- **Commit:** `fix(replication): the seat link becomes a pair, and the fourth verb fires`

---

## 1. Both rows were left open for reasons that had already stopped being true

Orphan-closure did the right thing twice and said so both times. What neither statement could
account for is that **the track's own fixes changed what was reachable underneath them**:

| Row | Left open because | Why that is no longer the reason |
|---|---|---|
| **X-58** | "guessing the producer from two stack frames would be the *one more attempt* this track's rules forbid" | There is no single producer. The write sites are enumerable — five of them — and the defect is a window all three transitions share. |
| **B-11** | "the only route open to a client with no explosive is `Vehicle.AutoDamage`" | X-46 closed the day before, the drill could drive, and O6's own final run already shows eight hulls taking crash damage and one reaching 13 health. |

Neither was a bad call. Both were conclusions about the evidence available at the time, and in
both cases the evidence moved before the conclusion was re-read. That is worth naming because it
is the second time on this ledger a carried-forward sentence outlived its measurement — the
roll-up's own drift note records the first three.

## 2. X-58 — the question was decidable, not open

`Seat.occupant` is written in exactly two places; `Actor.seat` in exactly three. That closes the
search space, and every one of the three transitions turned out to have the same shape:

```
publish one half  →  run a re-entrant callback  →  publish the other
```

- **`EnterSeat`**: `seat.SetOccupant(this)` … `Vehicle.OccupantEntered` → `Car`/`Tank.DriverEntered`,
  the transform re-parent, `controller.StartSeated(seat)` … **then** `this.seat = seat`. A throw
  anywhere in there leaves the seat booked by a body whose own half is null — X-58 verbatim.
- **`LeaveSeat`** and **`SpawnAt`**: `seat.OccupantLeft()` → `Vehicle.OccupantLeft` →
  `DriverExited` … **then** `seat = null`. A throw there leaves the **mirror** state, which
  nothing in O6's containment covers.

So X-45's throw and O6 § 6b's strict-`Driver()` throw were instances, not causes. Hunting for
"the" producer could not have terminated, which is exactly what two sessions of hunting found.

**The fix publishes the pair before calling out**, in both directions. `SetOccupant` and
`OccupantLeft` each write the seat's half as their own first statement, so assigning (or
clearing) the body's half immediately before the call leaves no callback between the two writes:

```csharp
this.seat = seat;  seat.SetOccupant(this);          // entry
Seat leaving = seat;  seat = null;  leaving.OccupantLeft();   // exit, both routes
```

Not a `try/catch` (**S-D2**): a catch restores consistency after the fact, this removes the
inconsistent state from the program. O6's eject and `HasDriver()` containments **stay** and their
five mutants stay green (**S-D3**) — they are now belt-and-braces rather than the only defence.

## 2b. The window was not the only producer — and the run said so

The first lane-A run on the atomic build, `s2-combat-01`, still reported **one** one-sided
booking:

```
[net] seat Passenger of 'jeep(Clone)' is booked by 'Ai Character Optimizations(Clone)',
      which does not think it is seated there.
```

Fourteen lines after `[net] match phase -> Resetting`. So the state was still reachable, and the
containment — not the fix — is what caught it. **That is the phase's own instrument working: the
run is what says whether a mechanism argument is complete, and here it said no.**

The second producer is a **double entry**, and it needed no throw at all. `Actor.EnterSeat`
refused a dead vehicle and an occupied seat, but never a body that was **already sitting
somewhere else**. A second successful entry re-points that actor's own half at the new seat and
leaves the OLD seat booked by a body that no longer agrees — the same corrupt pair, by a
different route.

**Three of the four callers guard it and the fourth is the hole:**

| Caller | Guard |
|---|---|
| `AiActorController` (bot boards a vehicle) | `actor.CanEnterSeat()` — `!IsSeated() && …` |
| `FpsActorController` (use-ray) | same |
| `Actor.SwitchSeat` | calls `LeaveSeat()` first |
| **`IronfrontNetBindings.TryEnterSeat`** (the network path, added later) | **none** |

The arbiter is supposed to cover that hole with `RejectedAlreadySeated`, and does — **except when
its record and the scene disagree**, which `ServerVehicleRegistry.Clear()` causes deliberately at
every round boundary ("Forgets every vehicle and every claim. For a round boundary."). That is
precisely where the surviving booking was found.

**The guard therefore goes in `Actor.EnterSeat`**, before either half is written, because the
scene is authoritative about the scene: `TryEnterSeat` turns the `false` into a refusal the
bridge rolls back (V4-D7), so the arbiter is *corrected* by the scene rather than trusted over
it. Pinned by M24, and by an ordering assertion — a guard that runs after the writes reports the
corrupt pair instead of refusing it.

## 3. What the window was also hiding

`Seat.SetOccupant` calls `weapon.DeclareToNet()` → `MountedWeapon.ResolveNetSeat()`, which opens
with `if (user == null || user.seat == null) { netVehicleId = 0; ... return; }`.

With the body's half assigned five statements later, **that guard was true on every entry**, so
`NetWeaponAuthority.Declare` was never reached. The method's own remark names the cost it was
written to prevent:

> on a dedicated server nothing drives a networked gunner's controller, so `CanFire` is never
> called and the weapon would never announce itself — leaving an authority that exists, compiles
> and grades nothing.

That is precisely what happened, and the trigger V6 task 3 installed against it was disabled by
the very ordering this phase fixes. It is repaired as a consequence, not as a separate change.

**This is the second time the ordering produced a silent defect and the first one was found by a
crash.** X-45 was found because it threw. This one never threw — it returned early and did
nothing — so nothing was ever going to find it except reading the window.

## 4. B-11 — the fourth verb was missing for want of patience

The route was never absent. From `artifacts/lane-a/o6/o6-combat-04-capture.jsonl`, minimum health
reached per vehicle over the run:

| vehicle | 1 | 2 | 3 | **4** | 5 | 6 | 7 | 8 | **9** | 10 | 11 | 12 | 13 | 14 | 15 | 16 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| min health | 100 | 77 | 100 | **13** | 100 | 95 | 86 | 100 | **58** | 86 | 86 | 100 | 100 | 80 | 100 | 92 |

Eight of sixteen hulls took real crash damage; vehicle 4 finished on **13**. The `Burning` flag
histogram across all **139,162** vehicle samples of that run is `{0: 139162}`. The chain
drive → crash → `Vehicle.Damage` → `StartBurning` ran to *nearly* dead sixteen times and never
crossed the line, because every drill let go of its hull at exactly `SeatedMs` regardless of state.

`crashSkipsBurn` is **0** on every ground vehicle (helicopter alone is 1), so a wrecked hull burns
rather than dying outright — which is what the verb watches.

**The change is one clause**: a hull at or below 45 health is *finished*, not abandoned, bounded by
a 75 s ceiling so a wedged hull still releases its seat (**S-D7**). `DrillWorld` gains
`SeatedVehicleHealth`, read from the shipped vehicle decoder already being walked — no new
decoding, so `check-harness-no-decoder.ps1` stays satisfied by construction.

## 5. The guard that could not fail

The first cut of S2 shipped `world.SeatedVehicleHealth != DrillWorld.UnknownHealth` with a
behavioural test asserting an unnamed hull does not hold its seat. **The mutation run returned
GREEN.** `UnknownHealth` is 255 and the threshold is 45, so `255 <= 45` is already false: removing
the clause changes nothing, and the test could not fail for the reason it claimed to exist.

It is recorded here rather than quietly corrected because the phase that wrote it has "no detector
ships unproven" as its own acceptance criterion, and the mutation pass is what caught it —
`green-that-proves-nothing.md` working exactly as intended, against this phase's author.

The load-bearing invariant is now pinned directly
(`Assert.True(DrillWorld.UnknownHealth > CombatDrill.FinishHullAtOrBelowHealth)`), and the mutants
were rewritten to remove **both** guards (M20) and the numeric one alone (M23).

## 6. Evidence — ten mutants, all observed RED

| # | Mutation | What it restores |
|---|---|---|
| M15 | the body's half is assigned last again | X-58 verbatim, plus the silent mounted-weapon miss |
| M24 | `EnterSeat` admits an already-seated body again | the second producer: a double entry orphans the old seat |
| M16 | a duplicate late assignment is left behind | the window, for everything between the two writes |
| M17 | `LeaveSeat` clears the seat's half first again | the mirror one-sided state |
| M18 | `SpawnAt` clears the seat's half first again | respawn-while-seated |
| M19 | the drill lets go of every hull on time | B-11 verbatim: Burn stays missing |
| M20 | both sentinel guards removed | an unnamed hull reads as a wrecked one |
| M21 | the finishing ride has no ceiling | a wedged hull holds a driver seat all run |
| M22 | the threshold admits a healthy hull | "always stay", which the positive test alone accepts |
| M23 | the sentinel becomes 0, the value its own remark rejects | the numeric guard alone |

**M15–M18 assert ORDER, not presence.** A version that sets both halves in the wrong order
contains every string they look for, compiles, and reproduces the defect exactly. **M16 exists
because "the fix is present" is not the property** — leaving the old late assignment alongside the
new early one reads as harmless and puts the window straight back.

**M20 and M23 exist because the first attempt at M20 came back GREEN** (§ 5). **M24's first
version came back GREEN too**, and for a sharper reason: it searched the method body for
`IsSeated()`, which appears in the new guard's own explanatory comment — so it passed on a tree
with the check deleted. That is precisely the trap O6 documented from the other side, when
correcting X-45's prose added an eleventh `AiWorkAllowed()` and turned a counting test red. It now
matches the guard *expression*. Two of ten mutants catching their own author is the argument for
running them at all.

Two mutants also had to be *rewritten* after a build failure that was the test's own fault:
raising `FinishHullAtOrBelowHealth` to 255 makes `(byte)(FinishHullAtOrBelowHealth + 1)` in
`LeavesAHullThatIsStillHealthyOnTime` a compile-time overflow. Mutating the sentinel instead of
the threshold reaches the same states and builds.

## 7. Static verification

| Gate | Result |
|---|---|
| `dotnet test` | **1,982 passed, 0 failed** across 8 projects (+10 from this work: 5 seat-order, 5 drill) |
| `SpecChecker` | OK — 90 constants match `protocol-spec.md` |
| `check-net-layering.ps1` | exit 0 |
| `check-harness-no-decoder.ps1` | exit 0 — 10 files scanned |
| `check-unity-meta.ps1` | exit 0 — 1,920 assets / 1,995 `.meta` |
| `check-duplicate-assemblies.ps1` | exit 0 |
| `check-diagnostics-exclusion.ps1` | exit 0 |
| `recount_debt_ledger.py --check` | exit 0 |

`dotnet test` exiting 0 is not on its own evidence — it does so when a project fails to *build* —
so the log was grepped for `error` (0) and the eight per-project `Passed!` lines counted.

## 8. The runs

**Run of record: `artifacts/lane-a/s2/s2-combat-03`** — 8 clients / 240 s, **8/8 held to the end**,
harness **exit 0**, **3 match resets**, clean wire, seed 12345.

| | `o6-combat-04` (before) | `s2-combat-01` (fix 1) | `s2-combat-03` (fix 1+2) |
|---|---|---|---|
| one-sided bookings | 2 | **1** | **0** |
| `NullReferenceException` | 0 | 0 | 2 — **X-60, not this work** |
| `ArgumentException` | 72 | 67 | 56 — **X-59, pre-existing** |
| match resets | 5 | 2 | 3 |
| verbs | `Missing: ["Burn"]` | **AllFour** | **AllFour** |
| flagged vehicle samples | **0** of 139,162 | 1,072 | 514 |

Decoded divergence **0** over 182,382 same-tick comparisons; `malformed/unknown 0/0`.

**Burn, three times, by three different clients on three different hulls** — so the verb is a
property of the programme rather than of one lucky hull:

| run | client | hull | first seen | sightings |
|---|---|---|---|---|
| `s2-combat-01` | 0 | vehicle 5 | tick 6036 | 265 |
| `s2-combat-02` | 5 | vehicle 1 | t+74.8 s | 58 |
| `s2-combat-03` | 0 | vehicle 15 | t+52.9 s | 16 |

Both lane-B `combat` sets `passed: true`, `failures: []`, **0** NREs and **0** one-sided
bookings — the same instrument that read 6 and 6 on O6 § 6b's strict-`Driver()` build.

### 8b. Three things this section will not round off

**`s2-combat-02` is truncated, and the runner is why.** It was launched with `-Seconds 360`, but
`run-lane-a.ps1` passes `--seconds` to the *harness* only; the server's own `LaneBHarness` ended
at ~300 s, so snapshots froze at t+307 s and the report reads `0/8 held` / `harness exit 3` —
**while still printing a complete verb table and `verbs missing none`.** Its 2 resets and 0
bookings over the ~300 s the server was alive are real and are cited as corroboration; its "held"
and exit numbers are an artifact of asking for a run longer than the server lives. A runner that
produces a full-looking report from a half-finished run is worth its own row if anyone runs long
again.

**`s2-combat-03` carries 2 `NullReferenceException`s and they are not X-58.** They are
`PushAntiStuckEvent` dereferencing a null `squad`, filed as **X-60** — the same site threw **5×**
in `o6-combat-01` and **3×** in `o1-combat-01`, before any of this work. Reporting the run as
"clean" would have been the easy sentence and the wrong one.

**The `ArgumentException` count is not an improvement.** 72 → 67 → 64 → 56 across runs is
variance in a defect nobody has touched (**X-59**), not a trend. It is quoted per-run so that a
future reader cannot mistake it for one.

## 9. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | X-58 closed with a mechanism, or reported open with a better reason | **MET** — closed, two producers, both by exhaustion |
| 2 | B-11 carries a verdict | **MET** — PASS, all four verbs, three runs |
| 3 | Every fix ships a detector observed RED | **MET** — ten mutants; two of them written only *because* a first attempt came back green |
| 4 | `dotnet test`, `SpecChecker`, layering, harness-decoder, meta gates exit 0 | **MET** |
| 5 | `recount_debt_ledger.py --check` exits 0, roll-up recomputed | **MET** |

## 10. Out of scope, and said so

**X-59 and X-60 are filed, not fixed.** Both are pre-existing, both are one register away from
this work, and both need the same exhaustion pass X-58 got — over `SetAlive`'s callers and over
`AiWorkAllowed()`'s conditions respectively. X-60 in particular would change the single gate all
eight AI coroutines park on; appending that to a phase about seat links is exactly the "one more
attempt" the track's rules forbid.

**Whether a disconnect should leave the scene's seat booked at all.**
`ServerTickLoop.OnClientDisconnected` releases the slot and forgets the actor without calling
`Actor.LeaveSeat()`, so a body handed back to the bot brain is still sitting where the departed
client left it. Both halves agree, so it is not X-58 — but it is how a released body ends up in a
vehicle, which is the state X-60 throws from.

**Whether `AutoDamage` ever fires in a lane-A run.** The Burn verb is closed on the crash route;
the decay route stays unproven either way, and nothing here measures it.
