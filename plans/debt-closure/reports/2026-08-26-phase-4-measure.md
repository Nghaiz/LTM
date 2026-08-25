# Phase 4 — four numbers taken, two rows that cannot be run, and a map wider than the wire can say

- **Written:** 2026-08-26
- **Phase:** [`phase-4-measure.md`](../phases/phase-4-measure.md)
- **Base commit:** `75361ac`
- **Lane A runs (new):** `artifacts/lane-a/p4-clean-*`, `p4-typical-*`, `p4-control-*`, `p4-smoke-*`
- **Closes:** acceptance criteria 1, 2, 4, 5 · **Fails:** 6 (two rows unrunnable) · **Blocked:** 3

---

## 1. What this phase was for, and the shape of what it found

Four assertions were supposed to become numbers. Three did. The fourth — the per-weapon release
delay — belongs to phase 6, which has not landed, so there is nothing to verify and this report
says so rather than inventing a verification.

The interesting result is not any single number. It is that **two of the bandwidth table's five
rows describe worlds this tree cannot build**, and saying that plainly was more useful than
approximating them. The rows were written expecting four runnable configurations; there is one.
Splitting that one run's bytes by opcode gave three of the rows a measured value anyway, and the
other two are reported as unrunnable with the reason.

Along the way the harness caught two things nobody was looking for: **the map extends past the
position quantizer's range**, and **X-32 is worse than 3E recorded** — not 4 of 8 clients lost, but
2 of 8 and then 0 of 8 on identical configuration.

---

## 2. The instrument, and why a decomposition instead of four runs

Phase 4 § 2 asks for a row per increment: no vehicles, then the finished seat field, then vehicles,
then projectiles. Three of those four are not configurations this tree has.

| Row | Why it cannot be run | What was done instead |
|---|---|---|
| **no vehicles** | No knob removes vehicles from a scene. Every `IRONFRONT_*` variable was enumerated; the closest is `IRONFRONT_GAMESERVER_SCENE`, and both shipped scenes carry vehicles | Derived as *total minus the vehicle share*, and labelled an upper bound |
| **+ SeatInfo (20 → 23)** | **Already shipped.** `InterestManager.MaxEntrySize` is `SnapshotMessage.EntrySize(SnapshotField.Full)` ([InterestManager.cs:114](../../../Ironfront.Net.Replication/Interest/InterestManager.cs#L114)), and `Full` includes `SeatInfo`. The 20 is history; no build produces it | Measured the consequence the row was really asking about — see § 4 |
| **+ projectiles** | `HarnessBehavior` is `Idle` or `Move` ([HarnessOptions.cs](../../../Ironfront.Net.LoadHarness/HarnessOptions.cs)). **No synthetic client can fire** | Reported as a measured zero, with both named regression mechanisms checked dead |

So: `Ironfront.Net.LoadHarness/WireByteTally.cs` attributes every received byte to the message type
that carried it, by re-reading each batch with a second `PayloadFrameReader` **after**
`ClientMessageRouter.Route` has had it. **No shipped code changed** — putting a counter inside the
router would have put measurement on the path the Unity client runs in production.

Three levels are reported separately because they are not the same number: **datagram** bytes (the
transport's own counter, what a link actually carries), **payload** bytes (what reached the router),
and **per-type** bytes. The gap between the first two is transport overhead, and it is printed
rather than assumed away — it turns out to be a third of the traffic.

### The instrument does not perturb what it measures

Not asserted — tested. The unmodified harness was rebuilt from `git stash` and run on identical
configuration:

| Run | Harness | Mean per client | Worst client |
|---|---|---|---|
| `p4-control` | **unmodified** (`/1`) | 2,591 B/s | 3,101 B/s |
| `p4-clean` | instrumented (`/2`) | **2,590 B/s** | **3,094 B/s** |

One byte per second apart. Every reconciliation check passed on every client in every run:
frame headers + message headers + bodies + unaccounted = payload bytes, **exactly**, as integers.

---

## 3. Task 4.1 — the bandwidth table (AC-1)

**Configuration, once, because every row shares it.** A byte rate without its conditions is a number,
not a measurement.

```
server   build/windows/Ironfront.exe -batchmode -nographics       (Windows player, NOT the Linux build)
         IRONFRONT_GAMESERVER_TRANSPORT=udp  UDP 27015
         IRONFRONT_LOAD_JSONL=artifacts/lane-a/p4-clean-ticks.jsonl  IRONFRONT_LOAD_SEED=12345
harness  --clients 8 --seconds 120 --behavior move --input-hz 30   (clean wire, no simulator)
world    56 actors, 14 vehicles, Dustbowl                          duration 121.1 s, 8/8 held
```

| Row | What moved | Measured, per client |
|---|---|---|
| Shipped baseline, no vehicles | **NOT RUNNABLE.** Upper bound by subtraction | **≤ 1.90 KB/s** |
| + `SnapshotField.SeatInfo` finished | **NOT RUNNABLE — already shipped.** See § 4 | — |
| + vehicles streaming | the ~1.6 KB/s projection | **0.63 KB/s** — the projection is **2.5× high** |
| + projectiles | ~16 B per shot | **0.00 KB/s — a measured zero.** See § 3.2 |
| **Full load** | the graded number | **mean 2.53 KB/s · worst client 3.02 KB/s** |

Full decomposition of the graded run:

| Message type | Messages | Wire bytes | Per client | Share |
|---|---|---|---|---|
| `Snapshot` | 19,305 | 885,094 | 0.89 KB/s | 35.27% |
| `VehicleSnapshot` | 11,763 | 618,765 | 0.62 KB/s | 24.66% |
| `CapturePoint` | 3,592 | 21,552 | 0.02 KB/s | 0.86% |
| `SpawnActor` | 1,152 | 19,584 | 0.02 KB/s | 0.78% |
| `MatchState` | 1,002 | 11,022 | 0.01 KB/s | 0.44% |
| `VehicleSpawn` / `VehicleDespawn` / `PlayerList` | 484 | 6,560 | 0.01 KB/s | 0.26% |
| frame headers | — | 76,605 | 0.08 KB/s | 3.05% |
| **transport overhead** | — | **870,143** | **0.88 KB/s** | **34.68%** |
| **TOTAL (datagrams)** | | **2,509,325** | **2.53 KB/s** | |

**A third of the bandwidth is not game state.** Transport framing costs 0.88 KB/s per client —
more than the entire vehicle stream, and within 1% of the actor snapshot stream it carries. No
rung of the D5 ladder touches it. This has never appeared in a bandwidth figure before because
every prior measurement was taken at the snapshot level, above the transport.

### 3.1. Which budget — and there are three

The phase text grades against **≤ 5 KB/s**. That number comes from
[`phase-v4-vehicle-server-authority.md:364`](../../replication/phases/phase-v4-vehicle-server-authority.md),
where it is the *vehicle* budget. The project's design of record says **8 KB/s**
([`plans/replication/plan.md:303`](../../replication/plan.md),
[`docs/report-chapter-state-synchronization.md:47`](../../../docs/report-chapter-state-synchronization.md)),
and an early roadmap says 12. **The run is inside all three**, so nothing turns on the choice
today — but the discrepancy is recorded rather than resolved by picking the loosest, and the
analysis script prints which budget it used.

### 3.2. The projectile row is a zero, and both regressions are dead

The row exists to check two mechanisms V7 raised. Both are dead on this tree:

- **id-0 bullet broadcasts** — a hitscan bullet is never announced;
  [`ProjectileNetAnnouncer.cs:71-73`](../../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ProjectileNetAnnouncer.cs#L71-L73)
  gates `AttachSync` on `netProjectileId != 0` and says why in its own comment.
- **resting-medipack re-announce** — `ShouldReAnnounce` returns false when not moving
  ([`ServerDeployableAuthority.cs:373`](../../../Ironfront.Net.Replication/Projectiles/ServerDeployableAuthority.cs#L373)),
  with a lifetime-divergence trigger that fires only on a heal.

But **0.00 KB/s is a property of the harness, not of the game.** No synthetic client can fire, so
this row says only that the two regressions do not fire on their own. The real projectile cost is
still unmeasured, and adding a firing behaviour is V9's job, not a gap to paper over.

### 3.3. The 1.67 KB/s baseline is not comparable, and should stop being quoted as if it were

The phase asks to "re-measure the phase-04 1.67 KB/s figure on this harness." It cannot be
re-measured here, and the reason matters more than the row:

| | phase-04's 1.67 KB/s | this run |
|---|---|---|
| Load | 16 players + 32 bots | 8 clients + 56 actors |
| Duration | 5 minutes | 121 s |
| Level measured | **snapshot bytes**, in-process | **datagram bytes**, over UDP |
| Source | `Phase04ExperimentTests` | a real two-process run |

The middle row is the one that disqualifies the comparison: the older figure never included the
34.68% of transport framing this one does. **The two numbers are not the same quantity**, and the
≈1.5× ratio between them is mostly that difference, not a regression.

---

## 4. What row 2 was really asking, since it cannot be run (AC-1)

Row 2's parenthetical is the substance: `MaxEntrySize` 20 → 23 **changes shedding behaviour**,
dropping the admitted actor count from 58 to 50. That is testable even though the 20-byte build no
longer exists.

Both halves read off the same messages the clients received — the entry count from each snapshot's
own `ActorCount` byte, not from the server's `entriesSent`:

| | Clean run | Typical run |
|---|---|---|
| Entries carried | 40,328 | 8,003 |
| Snapshot messages | 19,305 | 3,682 |
| Entries per snapshot | 2.09 | 2.17 |
| Fixed 13 B headers | 30.3% of snapshot bytes | 29.8% |
| **Mean bytes per entry** | **14.29 B** | **14.07 B** |
| Against the 23 B ceiling | **37.9% under** | 38.8% under |

And over the whole clean run: `entriesConsidered` 350,336 · `entriesCulled` 298,474 ·
`entriesHeld` 13,953 · **`entriesShed` 0**.

**Nothing shed, at any point, at this load.** The pessimistic projection stays pessimistic with
37.9% to spare. The 20 → 23 step consumed head-room that the shipping case does not need — at
8 clients. It says nothing about 16, which is V9's to take.

> **A number that had to be thrown away.** The first version of this check divided received
> snapshot bytes by the tick JSONL's `entriesSent` and got **38.80 B per entry — against a 23 B
> ceiling.** An impossible answer, and that is the only reason it was caught. `entriesSent` is
> `InterestManager.EntriesRefreshed`, a refresh-or-hold *decision* counter, not a count of entries
> written into a body; the two were never a matched pair. Both halves now come off the same
> messages. Anyone reading `entriesSent` as "entries sent" should expect the same trap.

---

## 5. Task 4.2 — server tick cost (AC-2)

Sample size beside every percentile, per the phase's own § 3.

**Clean run** — seed `IRONFRONT_LOAD_SEED=12345`, 8 clients / 120 s, no simulator:

| Sample | n | p50 | p95 | **p99** | max | mean |
|---|---|---|---|---|---|---|
| All ticks | 3,960 | 861 µs | 1,242 µs | **1,497 µs** | 44,527 µs | 854 µs |
| **Loaded** (≥1 connection) | **3,637** | 881 µs | 1,259 µs | **1,502 µs** | 44,527 µs | 903 µs |

**Typical run** — `--sim typical --sim-seed 12345`; see § 7 before reading these:

| Sample | n | p50 | p95 | **p99** | max | mean |
|---|---|---|---|---|---|---|
| Loaded | 2,482 | 511 µs | 1,043 µs | **1,304 µs** | 15,393 µs | 572 µs |

**Against a 33,333 µs budget at 30 Hz, p99 is 4.5% of it.** One loaded tick of 3,637 exceeded
budget on the clean run (0.03%); none did on the typical run. The tick loop is nowhere near its
ceiling at this load, and that is the half of the cutover's evidence Phase 5 was promised.

The max (44,527 µs) is a single outlier — a GC or scheduler event, not a load characteristic. It is
reported because a max that is never printed is a max nobody investigates, and it is not
represented as a percentile.

---

## 6. Task 4.1 — the cross-check (AC-1)

Server per-connection bytes (`perConn` in the tick JSONL) against each client's transport counter.

**Clean run: worst delta −0.17%. AGREES.** All eight connections between −0.12% and −0.17%.

**Typical run: worst delta −62.32%. DISAGREES — and the disagreement is X-32**, not an accounting
error. The server booked bytes to connections that had stopped receiving them. That is the same
defect § 7 describes, seen from the byte side.

> **A correction to the tool, made mid-phase.** The script originally asserted that the client's
> counter should read *higher* (it counts whole datagrams; the server counts payload handed to its
> transport). Across three clean runs the residue came in at −0.19%, −0.27% and **+0.26%** — same
> magnitude, both signs. Two effects pull opposite ways: framing pushes the client's number up,
> datagrams still in flight at teardown push it down. The script now says a small delta of either
> sign proves nothing, rather than predicting a sign it does not control.

---

## 7. X-32 is worse than 3E recorded, and it is reproducible

3E found that under `typical` (50 ms ± 20 ms, 5% loss, 2% reorder) only 4 of 8 clients survived two
minutes. Two further runs at identical configuration:

| Run | Wire | Held to end |
|---|---|---|
| 3E `run-01` | typical | 4 / 8 |
| `p4-typical` (first) | typical | **2 / 8** |
| `p4-typical` (final) | typical | **0 / 8** |
| `p4-clean`, `p4-control` | clean | **8 / 8**, **8 / 8** |

Three runs, monotonically worse, nothing changed between them but the run itself. Clean-wire runs
held 8 of 8 every single time. **X-32 is confirmed, and its severity is worse than one run
suggested** — the condition check 7 names is not "degraded", it is "nobody survives it."

The typical run's bandwidth figures are therefore **not graded**, and the analysis script now
refuses to grade them:

> A disconnected client stops receiving while the run duration keeps counting, so its
> bytes-per-second falls. The worse the run goes, the healthier the number looks. Before the guard,
> the 0-of-8 run printed **"WITHIN the 8 KB/s budget"** over 0.49 KB/s — a green that could only
> ever have been produced by the failure it was concealing.

---

## 8. New finding — the map is wider than the wire can describe

`Quantize.POS_MIN/POS_MAX` are ±2048 m over a signed short, 6.25 cm per step
([`Quantize.cs:25-27`](../../../Ironfront.Net.Protocol/Quantize.cs#L25-L27)). The encoder clamps
(`Clamp01`, `:88`), so the quantized value **32767 is reachable only when x ≥ +2048 m** — it cannot
be produced any other way, which makes it proof rather than a symptom.

In the control run, **9 of 62 distinct entities — 8 of the 14 vehicles, plus one actor — reported a
saturated X at least once**, while their Y and Z decoded to ordinary values (9.34 m, 1,140 m). They
are not corrupt; they are genuinely east of the representable world, and every one of them
replicates at **exactly 2,048.00 m**. Two vehicles 50 m apart out there arrive at the same
coordinate.

Prevalence varies enormously with where the run's clients spawn: 6.5% of captured samples in 3E's
clean run, 17% in the control, 53% in the first instrumented run. That variance is
[X-22](../debt-ledger.md)'s finding reappearing in lane A — a seed pins the draw *sequence*, not
which client consumes which draw.

**Filed as X-39.** What is *not* established: whether that region is reachable in play. If it is
only scenery parked off-map, this is cosmetic; if a player can drive there, combat there is
impossible. That distinction needs the Editor and is not claimed here.

## 8.1. And clients disagree on a clean wire, at a rate that varies 6×

| Run | Wire | Disagreements | Comparisons | Rate |
|---|---|---|---|---|
| 3E `run-02-clean` | clean | 31 | 32,520 | 0.095% |
| `p4-control` (unmodified harness) | clean | 286 | 53,522 | 0.534% |
| `p4-clean` | clean | 271 | 48,885 | 0.554% |

**On a clean wire, with no loss, two clients decode different world state.** Two distinct shapes
appear: a 1-unit difference on one axis (`26150,689` vs `26150,688`) which is a quantizer edge and
benign, and wholly different values which are not. `FirstDisagreement` reports only the first, so
the mix is unknown.

This is **pre-existing** — the unmodified control has the highest rate of the three — and it bears
directly on 3E's check 3, which passed on decoded agreement. **Filed as X-40.**

---

## 9. Task 4.3 — BLOCKED, and reported failed rather than skipped (AC-3 FAILS)

Acceptance criterion 3 cannot be met, because the work it verifies has not been done. Phase 4's own
header makes 4.3 depend on [`phase-6-rows-no-run-closes.md`](../phases/phase-6-rows-no-run-closes.md)
task 6.1, and phase 6 is not merged. Verified at `75361ac`:

| What 4.3 verifies | State |
|---|---|
| Per-weapon values authored from their clips | **Not authored.** `public float releaseDelay = 0.6f;` is still a bare literal — [`Weapon.cs:61`](../../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Weapon.cs#L61) |
| The replacement test fails on a de-tuned value | **Not replaced.** `OfflineBehaviourChangeTests.cs:74-76` still declares `const float releaseDelaySeconds = 0.6f` and feeds it to **both** `fromServer` and `fromClient` — true by construction, exactly as the ledger's D-1 row describes |
| V7's D7 record carries the offline-vs-server amendment | **Not amended** |

The clip times remain as Phase 0 read them: `frag_throw.anim:2248-2250` → **1.2381772 s**;
`Ammobox Throw.anim:1429-1431` → **0.4142947 s**. Three times apart, and `0.6f` is wrong for both.

The Editor half of 4.3 — confirming each throwable prefab's Animator still fires `SpawnThrowable()`
— is deferred with it, since it has no value ahead of the authoring it is meant to check.

---

## 10. Task 4.4 — the three assumed-closed claims, re-verified (AC-4)

| Claim | Verdict | Evidence |
|---|---|---|
| phase-05 Task 6's `Actor.Damage` guard is still present | **PRESENT** | [`Actor.cs:934`](../../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Actor.cs#L934) `bool ownsHealth = !NetContext.IsClient;`, gating `health -=` at `:936` and `Die()` + `ReportDeath` at `:952`. First re-verification since `2026-08-18-phase-v1-closure.md:57` |
| V8's A5 fix — the Unity CI gate that could never fail — is in `tools/ci.ps1` | **PRESENT** | [`ci.ps1:145-171`](../../../tools/ci.ps1#L145-L171). `Start-Process -Wait -PassThru` with an explicit `$unity.ExitCode -ne 0` throw, and a comment naming the original failure: the call operator returned instantly, `Invoke-Step` read the *previous* command's exit code, and the step printed PASS on every run it ever had |
| V8 Task 4 elimination-by-spawn-point-loss behaves as described | **VERIFIED BY TEST RUN** | `ObjectiveAuthorityTests` — **30 passed, 0 failed**. Six tests cover the behaviour: `LosingEverySpawnPointEndsTheMatchOnce`, `TheEliminatedTeamIsTheOneThatLoses`, `BothTeamsAtZeroSpawnPointsIsADraw`, `EliminationDoesNotFireInsideTheGraceWindow`, `UnreportedSpawnPointCountsLeaveEliminationInert`, `SpawnPointOwnerTracksTheAuthoritativeTeamEveryTick` |

The third is the one that needed the run: V8's closure recorded *structural presence*, and a grep
would have reproduced exactly that. It now has a test result.

---

## 11. Task 4.5 — V7's ten tests recorded won't-do (AC-5)

Written into [`phase-v7-projectiles.md`](../../replication/phases/phase-v7-projectiles.md) § 6.1.1,
per **P-D9**. Both supporting facts re-verified on 2026-08-26:

- `Assets/Tests/EditMode/Ironfront.Net.Unity.Server.Tests.asmdef` references exactly
  `Ironfront.Net.Unity.Server` and `Ironfront.Net.Unity.Shared`.
- `Assets/Scripts/Net/Client/` contains **zero** `.asmdef` files, so its types land in
  `Assembly-CSharp`, which no `.asmdef` may reference.

The record states the reason, marks the tests **won't-do** rather than "not yet", and carries the
reopening condition: if `Net/Client` gains its own asmdef — which
[`plans/asmdef-seam/plan.md`](../../asmdef-seam/plan.md) exists to do — the ten become writable.
The row it replaces said only "this phase adds no EditMode harness", which reads as scheduling and
invites the next reader to try.

---

## 12. Acceptance criteria — honest scoring

| # | Criterion | Verdict |
|---|---|---|
| 1 | Five rows filled; cross-check agrees within 5% | **PARTIAL — 3 of 5 rows measured.** Two are unrunnable on this tree and say so (§ 2). Cross-check agrees at −0.17% clean; the typical run's −62.32% is investigated and named as X-32 (§ 6) |
| 2 | Tick p50/p99/max with sample size, seed, configuration | **MET** (§ 5) |
| 3 | Release delays verified, test seen failing, D7 amended | **FAILED — blocked on phase 6 task 6.1**, which has not landed (§ 9) |
| 4 | Three assumed-closed claims carry a citation or a test run | **MET** (§ 10) |
| 5 | V7 § 6.1 records the ten tests won't-do with reason and reopening condition | **MET** (§ 11) |
| 6 | Any failing criterion reported failed; no target re-scoped | **MET, and load-bearing.** Criteria 1 and 3 are reported failed above. No budget was re-scoped — and § 3.1 records that three different budgets are in circulation rather than quietly grading against the loosest |

**Four of six met, one partial, one failed.** The failure is a dependency that has not landed, not
a measurement that came out badly.

---

## 13. Ledger movement

| Row | Movement |
|---|---|
| **B-16** (bandwidth per client) | → **CLOSED** at this load. 2.53 KB/s mean, 3.02 KB/s worst client, 8 clients / 120 s / clean, decomposed by message type. V9 re-takes it at 16 |
| **B-17** (tick budget) | → **CLOSED** at this load. p99 1,502 µs against 33,333 µs, n = 3,637 |
| **D-3** (`Actor.Damage` guard assumed) | → **CLOSED**, citation `Actor.cs:934` |
| **D-1** (`releaseDelay`) | unchanged — **remains phase 6's**, and phase 4 confirms it is untouched |
| **X-32** | severity raised: 4/8 → 2/8 → **0/8** on three runs |
| **X-39** *(new)* | Entities beyond `POS_MAX` all replicate at exactly 2,048 m. 9 of 62 entities in one run |
| **X-40** *(new)* | Clients decode different world state on a **clean** wire, 0.095%–0.554% across four runs, pre-existing |

---

## 14. Handoff

**To Phase 5:** the tick-budget half of the cutover's evidence is done — p99 1,502 µs against a
33,333 µs budget, with 37.9% entry-size head-room and zero shedding. Nothing about the tick loop
argues against the cutover at 8 clients.

**To Phase 6:** task 6.1 is unblocked and now has a consumer waiting. When it lands, 4.3's
verification is three checks against artifacts that will exist.

**To V9:** every number here is a **first** measurement at 8 clients on one machine over loopback,
and the two rows this phase could not run are still not run. V9 re-takes them at 16 clients and 12
vehicles; these are the baseline it compares against, not a substitute for it. Two specific
inheritances: a firing behaviour for the harness (without which the projectile row stays a zero),
and the transport-overhead share (34.68%), which no rung of the D5 ladder can reduce.

**To whoever picks up X-39:** the open question is whether the region past x = +2048 m is reachable
in play. That needs the Editor, and until it is answered the severity is unknown rather than low.
