# Phase S2 — The fourth verb, and the premise that stopped being true

- **Track:** [`plan.md`](../plan.md) · **Effort:** S (1 d)
- **Depends on:** nothing. Shares S1's build and S1's run.
- **Closes:** **B-11** (V5 — a headless server survives drive → damage → burn → death with a
  networked driver), last graded PARTIAL at 3 of 4 verbs with `Missing: ["Burn"]`.

---

## 1. The premise this row was waiting on

B-11's cell says:

> **BURN — no, and it is downstream of DRIVE.** The only route open to a client with no explosive
> is `Vehicle.AutoDamage`, which decays an ABANDONED vehicle 7% every 2 s from 50 s after it
> empties — so it needs a vehicle to have been driven and left. No vehicle health moved by a
> point and no `Burning` flag was ever set, across all eight clients' captures.

Every sentence of that was true **on 2026-08-27**, when it was written against a run in which
X-46 was still open and no client could drive at all. O1 closed X-46 the next day. The row was
re-graded for the *drive* verb and the burn sentence was carried forward unchanged.

## 2. What the artifacts actually say now

`artifacts/lane-a/o6/o6-combat-04` — the run O6 shipped on, 8 clients, 221 s, five match resets —
holds the answer in its own capture. Minimum health reached, per vehicle, across the run:

| vehicle | 1 | 2 | 3 | **4** | 5 | 6 | 7 | 8 | **9** | 10 | 11 | 12 | 13 | 14 | 15 | 16 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| min health | 100 | 77 | 100 | **13** | 100 | 95 | 86 | 100 | **58** | 86 | 86 | 100 | 100 | 80 | 100 | 92 |

**Eight of sixteen hulls took real damage and vehicle 4 finished on 13.** "No vehicle health moved
by a point" has not been true for a day. The `Burning` flag histogram over all 139,162 vehicle
samples is `{0: 139162}` — so the chain ran the whole way to *nearly* dead, sixteen times over,
and never once crossed the line.

**The route is crash damage, and it is a shipped path.** `Vehicle.OnCollisionEnter` deals
`(impactSpeed - threshold) * multiplier`, and the prefabs author it generously:

| prefab | maxHealth | speed threshold | multiplier | `crashSkipsBurn` |
|---|---|---|---|---|
| quadbike | 400 | 2 | **15** | 0 |
| jeep | 1000 | 2.5 | 20 | 0 |
| tank | 2000 | 3 | 4 | 0 |
| rhib | 700 | 2 | 4 | 0 |
| helicopter | 1000 | 2 | 50 | **1** |

A 10 m/s impact is 120 damage to a quadbike — four solid crashes. And `crashSkipsBurn` is **0 on
every ground vehicle**, so a wrecked hull reaches `StartBurning()` rather than dying outright,
which is exactly what the verb watches: `SyntheticClient` already records `Burn` off
`VehicleStateFlags.Burning`, and `StateCapture` has carried the vehicle `flags` column since X-34.

**Nothing was missing but patience.** Every drill let go of its hull at exactly `SeatedMs`
(20 s), whatever state that hull was in — including the one on 13 health.

## 3. The change — finish a hull you have nearly wrecked

`DecideDrive` gains one clause:

```csharp
bool finishing =
    world.SeatedVehicleHealth != DrillWorld.UnknownHealth
    && world.SeatedVehicleHealth <= FinishHullAtOrBelowHealth   // 45
    && heldMs < MaxSeatedMs;                                    // 75 s

if (heldMs >= SeatedMs && !finishing) { ...leave... }
```

`DrillWorld` grows `SeatedVehicleHealth`, read in `SyntheticClient.BuildWorld` from the **shipped**
vehicle decoder that was already being walked — no new decoding, so
`tools/check-harness-no-decoder.ps1` stays satisfied by construction. It is captured **before**
the loop's `Dead` filter, deliberately: that filter exists to stop the drill *walking* to a wreck,
not to hide the hull it is sitting in.

**Why 45** (S-D6): health is a percentage byte on the wire, one crash is worth roughly 30 points
of a quadbike, and the snapshot a drill reads is a tick or two old — a threshold near the floor
would be crossed and passed between two readings. 45 is about one and a half impacts of headroom.

**Why a ceiling** (S-D7): a hull can sit under the threshold and stop taking damage — wedged
against a rock, or on ground too flat to crash on. Unbounded, that drill holds a driver seat for
the rest of the run and seven other clients contend for one fewer vehicle. The rule would buy the
fourth verb by damaging the first.

**Why the run was not simply made longer.** A longer run buys Burn by luck — the same spawn-distance
variance the R5 report recorded, where six Combat runs produced four different verb sets. The
finish rule makes the verb a property of the programme: any client that drives a hull under the
threshold stays to finish it, whoever that client is and wherever it spawned.

## 4. Why a REQUIRED constructor parameter

`DrillWorld.SeatedVehicleHealth` is not defaulted (**S-D8**), for O-D5's reason one track over: a
defaulted reading lets a caller that forgot it keep the old always-leave behaviour while still
compiling, and the only symptom would be Burn quietly staying missing — the exact failure the
parameter exists to end. Eight construction sites across two files is the whole cost.

## 5. The guard that could not fail, and what replaced it

The first version of this phase shipped `world.SeatedVehicleHealth != DrillWorld.UnknownHealth`
with a behavioural test asserting an unnamed hull does not hold its seat. **The mutation run
returned GREEN**: removing the clause changes nothing, because `UnknownHealth` is 255,
`FinishHullAtOrBelowHealth` is 45, and `255 <= 45` is already false. The clause is real
documentation and a real guard *if the threshold ever rises*, but today it is not the thing
holding the property — and a test that cannot fail is decoration
(`green-that-proves-nothing.md`).

So the load-bearing invariant is pinned directly instead:

```csharp
Assert.True(DrillWorld.UnknownHealth > CombatDrill.FinishHullAtOrBelowHealth);
```

and the mutants were rewritten to remove **both** guards (M20) and the numeric one alone (M23).
This is worth recording rather than quietly fixing: the mutation pass caught a decorative
detector written *by* a phase whose own acceptance criterion is that no detector ships unproven.

## 6. Evidence — five mutants, all observed RED

| # | Mutation | What it restores |
|---|---|---|
| M19 | the drill lets go of every hull on time | B-11 verbatim: Burn stays missing |
| M20 | both sentinel guards removed | an unnamed hull reads as a wrecked one |
| M21 | the finishing ride has no ceiling | a wedged hull holds a driver seat for the whole run |
| M22 | the threshold admits a healthy hull | "always stay", which the finish test alone would accept |
| M23 | the sentinel becomes 0, the value its own remark rejects | the numeric guard alone |

M22 exists because the positive test has an obvious cheat: a `finishing` that is always true
passes it. The negative case one point above the threshold is what makes the two disagree.

## 7. Acceptance

| # | Criterion | How it is judged |
|---|---|---|
| 1 | A lane-A `combat` run records **Burn**, with `AllFour: true` and `Missing: []` | the run |
| 2 | The verb comes from a client action, not from a decay timer nobody drove | the `Burning` flag arrives on a hull this drill held |
| 3 | The finish rule cannot fire on an unnamed hull | M20, M23 + the constant test |
| 4 | The finish rule cannot hold a seat forever | M21 |
| 5 | Observed RED before the fix | five mutants |
| 6 | `check-harness-no-decoder.ps1` still passes | the gate |

## 8. Out of scope, and said so

**The grenade route.** `ActorManager.Explode` damages vehicles and O3 proved a thrown grenade
detonates, so a client with an explosive is a second route to this verb. It is not taken here
because the LoadHarness has **no weapon-switch capability at all** — it would need new drill
opcodes, which is a larger change than the verb requires. Recorded so the next reader knows it
was considered rather than missed.

**Whether `AutoDamage` ever fires in a lane-A run.** It arms only on a vehicle becoming empty and
needs 78 s of that hull being left alone by all eight clients. Nothing here measures whether that
happens; the row is closed on the crash route, and the decay route stays unproven either way.
