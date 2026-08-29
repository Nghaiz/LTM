# Seat-and-burn closure — the two rows the orphan-closure track handed on

- **Created:** 2026-08-29 · **Branch base:** `develop` · **Owner:** single-owner project
- **Opened by:** [`plans/orphan-closure/plan.md`](../orphan-closure/plan.md), which met its own
  success criteria and left exactly two things standing: **X-58**, filed and contained but not
  root-caused, and **B-11**, re-graded to 3 of 4 verbs with `Missing: ["Burn"]`.
- **Ledger:** [`plans/debt-closure/debt-ledger.md`](../debt-closure/debt-ledger.md) stays the
  single source of truth. This track does **not** fork it (**V-D1**, inherited). Each phase
  updates the row it closes in the same commit as the closing work and re-runs
  `tools/recount_debt_ledger.py --check`.

---

## 1. Why this track exists

Orphan-closure closed nine rows and reported two open with reasons rather than guessing at them.
Both reasons have since stopped being true, and for the same underlying cause — **the track's own
fixes changed what was reachable**:

- **X-58** was left open because "guessing the producer from two stack frames would be the *one
  more attempt* this track's rules forbid." That was the right call against the evidence it had.
  What it did not have is that the pair `Seat.occupant` / `Actor.seat` has only **five write
  sites**, which makes the question decidable by exhaustion rather than by guessing.
- **B-11** was left at 3 of 4 verbs because "the only route open to a client with no explosive is
  `Vehicle.AutoDamage`." That premise died the moment **X-46** closed in O1 and the drill could
  actually drive — and the artifacts of O6's own final run already show it dead.

## 2. The shape of what is left

```
S1 (the one-sided seat booking has no single producer)   X-58 ─── finds the mounted-weapon miss
S2 (the fourth verb was missing for want of patience)    B-11 ─── needs S1's build, nothing more
```

**No ordering constraint between them.** S1 is a shipped-path change in `Actor.cs`; S2 is a drill
programme change in the harness. They share one lane-A run because one run grades both.

## 3. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **S-D1** | **X-58 is closed by making the seat link atomic, not by naming the throw that produced it.** `Seat.occupant` is written in exactly two places and `Actor.seat` in exactly three, and BOTH transitions published one half, ran a re-entrant callback, then published the other. Every throw inside either window leaves a one-sided booking, which is why no single producer was identifiable: there isn't one. Closing the window makes the state unproducible by ANY throw, which is strictly stronger than closing the throw that happened to be found. |
| **S-D2** | **The pair is published before the callback, in both directions — not wrapped in try/catch.** `Seat.SetOccupant`'s own first statement is `occupant = actor`, so with `Actor.seat` assigned immediately before it there is no callback between the two writes and no window to observe. The same trick inverted covers `LeaveSeat` and `SpawnAt`: capture the seat, null the body's half, *then* call `OccupantLeft()`. A `try/catch` would restore consistency after the fact; this removes the inconsistent state from the program. |
| **S-D3** | **`Vehicle.EjectOccupants`'s and `HasDriver()`'s O6 containments STAY.** They are now belt-and-braces rather than the only defence, and their five mutants stay green. Removing them would be trading a proved guard for an argument, and the argument covers only the producers this track can enumerate today. |
| **S-D4** | **`Driver()` stays permissive.** O6 § 6b established why, and the reorder does not reopen it: making it strict was a separate change that manufactured the very state it was written to survive. The reorder happens to make a strict `Driver()` viable; that is not a reason to make it strict. |
| **S-D5** | **B-11's Burn verb comes from crash damage while driving, not from `AutoDamage`.** The prefabs author crash damage generously (quadbike 400 max health, 2 m/s threshold, ×15 multiplier) and `crashSkipsBurn` is 0 on every ground vehicle, so a wrecked hull burns rather than dying outright. `AutoDamage` needs **78 s** of one hull left alone by all eight clients — a longer run and a cooling protocol — to buy the same verb more slowly and less honestly. |
| **S-D6** | **The drill finishes a hull it has nearly wrecked rather than the run being made longer.** Burn was missing for want of patience, not a route: `o6-combat-04` drove vehicle 4 from 100 to **13** health with eight of sixteen hulls damaged, and every drill let go at exactly `SeatedMs` regardless. A longer run buys the verb by luck; the finish rule makes it a property of the programme. |
| **S-D7** | **The finishing ride is bounded by `MaxSeatedMs`.** A hull can sit under the threshold and stop taking damage — wedged, or on ground too flat to crash on. Unbounded, that drill holds a driver seat for the rest of the run and seven other clients contend for one fewer vehicle: the rule would buy the fourth verb by damaging the first. |
| **S-D8** | **`DrillWorld.SeatedVehicleHealth` is a REQUIRED constructor parameter.** O-D5's reason one track over: a defaulted reading lets a caller that forgot it keep the old always-leave behaviour while still compiling, and the only symptom would be Burn quietly staying missing — the exact failure the parameter exists to end. |

## 4. Phases

| # | Phase | Goal | Closes | Effort |
|---|---|---|---|---|
| **S1** | [`phase-s1-atomic-seat-link.md`](phases/phase-s1-atomic-seat-link.md) | A one-sided seat booking cannot be produced by any throw | X-58 | S–M (1 d) |
| **S2** | [`phase-s2-burn-verb.md`](phases/phase-s2-burn-verb.md) | Check 11's fourth verb fires from a client action | B-11 | S (1 d) |

**Critical path:** neither blocks the other; one lane-A `combat` run grades both.

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| The reorder changes every seat entry in the game and breaks one | 3 | 5 | **15** | This is exactly O6 § 6b's failure, and the mitigation is the same instrument that caught it: a lane-B build-and-play set on the way out, plus a lane-A run through several match resets. Four mutants pin the order in both directions. |
| A callback reads the half that is now set earlier and changes behaviour | 2 | 4 | 8 | Enumerated rather than assumed: `Vehicle.OccupantEntered` reads `seat.occupant` (set inside `SetOccupant`, unchanged), `OccupantLeft` reads its `leaver` parameter, and the `DriverEntered`/`DriverExited` overrides read neither half. The one reader of `Actor.seat` in the window is `MountedWeapon.ResolveNetSeat`, which the reorder **fixes** — see S1 § 4. |
| The finish rule starves other clients of vehicles | 3 | 3 | 9 | **S-D7**'s ceiling, pinned by a mutant that removes it. |
| The finish rule fires on a hull the snapshot has not named | 2 | 4 | 8 | An `UnknownHealth` sentinel that cannot satisfy the threshold, pinned by a constant-relationship test — written **because** the mutation run showed the obvious guard could not fail. See S2 § 5. |
| A fix ships with a detector never seen RED | 3 | 4 | 12 | Criterion 3 below, inherited verbatim. Nine mutants, all observed. |

## 6. Success criteria

1. **X-58 is closed with a mechanism, or reported open with a better reason than it has now.**
2. **B-11 carries a verdict**, or a filed row saying which instrument or programme is still
   missing (**V-D2**, inherited).
3. Every defect fixed here ships a test or gate rule **observed RED** against the tree before the
   fix landed. No detector ships unproven (`green-that-proves-nothing.md`).
4. `dotnet test`, `SpecChecker`, `check-net-layering.ps1`, `check-harness-no-decoder.ps1` and
   `check-unity-meta.ps1` exit 0 at every phase boundary.
5. `tools/recount_debt_ledger.py --check` exits 0, and the roll-up is recomputed rather than
   decremented.
