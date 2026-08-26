# Phase 5 — the cutover is decided OFF, and the reason is that the harness cannot pull a trigger

- **Written:** 2026-08-26
- **Phase:** [`phase-5-cutover-gate.md`](../phases/phase-5-cutover-gate.md)
- **Base commit:** `f99a843` · **Branch:** `phase-5-cutover-gate`
- **Hard gate:** X-6 satisfied first, in its own commit (`2be9cb3`) — see § 2
- **Decision:** `ServerProjectileBridge.AuthoritativeFlight` stays **OFF**
- **Closes:** acceptance criteria 1, 2, 4, 5, 6 · **Not applicable:** 3 (the "if on" branch)

---

## 1. The shape of the answer

Phase 5 was written as a decision with a proof obligation: flip the flag with evidence, or leave it
off with a written reason. It is off, and the reason is not that a number came back bad.

**Two of the three required inputs could not be produced at all**, and for one shared cause:
`Ironfront.Net.LoadHarness`'s synthetic client has no way to fire a weapon. `HarnessBehavior`
declares exactly two values, `Idle` and `Move`, and `SyntheticClient.PushInput` builds every frame
with `InputButtons.None` — the single occurrence of `InputButtons` in the project. That is ledger
**X-34**, filed by phase 3E against a different check, and it turns out to be the thing standing
between this track and its cutover.

No projectile is fired, so no projectile damage is applied, so "exactly once" has nothing to count.
No projectile is fired, so no projectile bytes cross the wire, so the projectile bandwidth row is a
zero that describes the instrument rather than the system.

The phase anticipated this exact situation and pre-committed the answer: *"All three inputs in § 3
are required; a missing input is a 'no', not a judgment call."* This report takes that at its word.
**The decision is OFF on availability of evidence, not on the quality of it** — a distinction the
reopening condition in § 5 is built around, because "we could not measure it" and "we measured it
and it was too expensive" have completely different next steps.

---

## 2. The hard gate came first, and it changed what the gate could catch

`phase-5-cutover-gate.md` names one hard ordering constraint: X-6, the `ownsHealth` pin, lands
before this phase runs. It had not. `grep -rn "ownsHealth"` returned seven hits, every one of them
production code or a comment in `Actor.cs`, `ActorManager.cs` and `NetClientCombatPresenter.cs`, and
zero of them a test.

That was closed first, in its own commit, as **ClientWiringGate G8** — not as a test, because
`Actor` compiles into `Assembly-CSharp` and no test assembly may reference it (**E-11b**, and the
same wall § 6.1.1 of the V7 phase file documents for ten other tests).

**The gate found something while being built, which is why the ordering constraint earned its
keep.** The obvious implementation was to reuse G7's `HasGuardAbove` helper. Its own remark forbids
that:

> Like G4 it deliberately does NOT model polarity: it answers "is there a guard at all", and
> claiming to catch an inverted one would be a green that proves nothing.

For G7 that is the right call — an inverted projectile-damage guard still leaves exactly one side
applying damage. For X-6 polarity **is** the fault. `ownsHealth` is `!NetContext.IsClient`; delete
the `!` and a client subtracts health the server already subtracted and calls `Die()` for a death
`S_DEATH` is about to announce, while the declaration still sits there looking correct and G7 stays
green. So G8 asserts the negation explicitly, and that clause is the one nothing in the tree could
previously have caught.

Four mutations, four REDs, recorded in
[`2026-08-26-x6-mutation-proof.txt`](2026-08-26-x6-mutation-proof.txt): the negation removed
(`Actor.cs:934`), the subtraction unguarded (`:936`), the death branch unguarded (`:954`), and — the
one worth naming — the rule's own scope re-pointed at a file that does not exist, which exits **2**
rather than 1. G8 is an absence rule scoped to a single file, and an absence rule inside a per-file
loop is silent when the file is never scanned. It would report clean having graded nothing.

---

## 3. Task 5.1 — grading the three inputs

| § 3 input | Required | Actual | Verdict |
|---|---|---|---|
| Damage accounting under the harness | exactly **1** application per hit, flag **on** | **0 hits producible**, so 0 applications observed | **NOT PRODUCED** |
| Tick p99 with the stepper **active** | < 33,333 µs | **1,502 µs** — but with the stepper **inactive** | **MEASURED, WRONG WORLD** |
| Bandwidth with projectiles **streaming** | inside the criterion-8 budget | **0 B/s** of projectile traffic, because none streams | **NOT MEASURED** |

**0 of 3 inputs answer the question § 3 asks.** Being generous to the middle row still gives 1 of 3,
and the threshold is 3.

### 3.1. Input 1 — damage accounting: nothing to count

Phase 3E's handoff to this phase says the harness's damage accounting *"is still what will prove
'exactly once' when the flag flips"* — future tense, and it stayed future. The blocker is X-34
above. The number to record, per acceptance criterion 1, is **zero projectile hits produced across
every lane-A run in the track**, against a criterion-5 requirement of exactly one damage application
per hit. This is not "1 of 2 configurations passed"; it is a measurement that has never had an input.

What *is* proved, and is worth not overstating: `ProjectileDamageOwnershipTests` (6 tests) shows
engine and library are never simultaneously the owner **in either configuration**, and G7 shows all
three engine-side call sites consult that decision. Together they mean a flip would not double-count
*by construction*. They are a proof about the code, not a measurement of a run, and criterion 5 asks
for the run.

### 3.2. Input 2 — tick p99: a real number about a world without projectiles

From [`2026-08-26-phase-4-measure.md`](2026-08-26-phase-4-measure.md) § 5, clean run, seed 12345,
8 clients / 120 s, 56 actors, 14 vehicles, Dustbowl, 121.1 s, 8/8 clients held:

| Sample | n | p50 | p95 | **p99** | max |
|---|---|---|---|---|---|
| Loaded (≥1 connection) | 3,637 | 881 µs | 1,259 µs | **1,502 µs** | 44,527 µs |

**1,502 µs against a 33,333 µs budget is 4.5%, and that is genuine head-room.** It is also not the
row's measurement. § 3 asks for tick p99 *with the stepper active*; the flag was off for this run
and nothing fired, so the ballistic stepper stepped **zero projectiles**. The number is the
**baseline** the stepper's cost would be added to, which is exactly how Phase 4's own handoff frames
it, and it is recorded here as such rather than counted as a pass.

Phase 4's report describes this input as *"the tick-budget half of the cutover's evidence is done"*.
That is fair about the tick loop and too generous about the cutover: a budget with 31,831 µs of
head-room tells you a stepper *could* fit, not that it does.

### 3.3. Input 3 — bandwidth: a zero from the instrument, not from the system

Same run. Per-client totals, with the control proving the instrument does not perturb what it
measures:

| Run | Harness | Mean per client | Worst client |
|---|---|---|---|
| `p4-control` | unmodified | 2,591 B/s | 3,101 B/s |
| `p4-clean` | instrumented | **2,590 B/s** | **3,094 B/s** |

Projectile share of that traffic: **0 bytes**, for the X-34 reason. Phase 4 reports the row as a
measured zero with both named regression mechanisms checked dead, which is the honest way to report
it and is not the same as "projectiles fit in the budget".

Against the design-of-record budget of **8 KB/s (8,192 B/s)** per client, the shipped baseline leaves
**5,602 B/s** of head-room on the mean and **5,098 B/s** on the worst client. As with the tick
number: that is the space a projectile stream would have to fit into, measured; whether it fits is
unmeasured.

> Phase 4 § 3.1 records that **three different budgets are in circulation** for this criterion. The
> 8 KB/s figure above is the project's design of record. A future run that grades against a looser
> one should say which it used, in the same sentence as the number.

---

## 4. Task 5.2 — the decision

**`ServerProjectileBridge.AuthoritativeFlight` stays OFF.** Phase 2 task 2e's prepared patch — the
one whose first act is deleting the engine-side damage call — **is not landed**. It stays where 2e
left it: written, guarded, and inert behind the flag, referenced from ledger row C-1 so the next
attempt starts from a patch rather than from a decision.

Nothing about the shipped runtime changes. The Unity server keeps applying projectile damage through
`Hitbox.ProjectileHit` and `ActorManager.Explode`, the path phase-05 and V1 established and which
works today.

### 4.1. What did change: the default is now asserted rather than described

Acceptance criterion 2 binds on **both** branches — *"`AuthoritativeFlight`'s default is asserted by
a test, not by a comment"* — and it was unmet. `ProjectileDamageOwnershipTests` takes
`authoritativeFlight` as a **parameter**, so it proves the partition function and says nothing about
which value ships. The flag's default-off status rested on a paragraph of prose, which is precisely
what V7 § 6.1 already flagged as the weak link.

`Assets/Tests/EditMode/ProjectileAuthorityDefaultTests.cs` now carries two tests. The first asserts
the flag is off after `NetProjectileAuthority.Clear()` — not circular, because `Clear()` is what
`[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` runs before any gameplay and is therefore
the mechanism that establishes the shipped runtime default. The second asserts the **consequence**:
at that default, on a dedicated server, `EngineAppliesProjectileDamage` is true and
`LibraryOwnsProjectileDamage` is false. That second test is the one that matters; the first alone
would be a bool asserted next to the line that set it.

Mutation-proved in [`2026-08-26-phase-5-ac2-mutation-proof.txt`](2026-08-26-phase-5-ac2-mutation-proof.txt):
inverting `Clear()` to set the flag **on** turns **both** tests RED (Unity exit 2), and the restored
full EditMode suite is **76/76**.

---

## 5. The reopening condition (acceptance criterion 4)

The failing inputs, named with their numbers: **damage accounting produced 0 hits** where 1
application per hit was required, and **bandwidth measured 0 B/s of projectile traffic** where a
figure inside 8,192 B/s was required. The tick input returned 1,502 µs but with the stepper
inactive.

**The single blocking defect is X-34** — no `HarnessBehavior` fires. Fix that and all three inputs
become producible in one run; leave it and no amount of re-running changes any of the three.

This decision is reopened when **all four** hold:

1. **A firing behaviour exists.** `HarnessBehavior` gains a third value that drives the shipped fire
   opcode, producing **≥ 100 recorded projectile hits** across **≥ 2 seeds**. (X-34 closed.)
2. **Damage applies exactly once.** With `AuthoritativeFlight` **on** across those ≥ 100 hits:
   **0 hits with 2 applications, and 0 hits with 0**. Not a rate — a count of zero in both
   directions.
3. **Tick p99 with the stepper genuinely active** stays **< 33,333 µs** at 16 clients / 12 vehicles
   (V9's load, not this one), over **n ≥ 3,000 loaded ticks**, reported with its sample size. The
   baseline this must not blow past is **1,502 µs**, leaving **31,831 µs** of head-room to spend.
4. **Per-client bandwidth with projectiles streaming** stays **< 8,192 B/s** on both the mean and the
   worst client, stating which of the three budgets it graded against. The baseline is **2,590 B/s**
   mean / **3,094 B/s** worst, leaving **5,602 B/s** and **5,098 B/s** respectively.

Any one of the four failing is a "no" and keeps the flag off, per the same rule that produced this
verdict.

---

## 6. Acceptance criteria

| # | Criterion | Verdict |
|---|---|---|
| 1 | All three evidence inputs recorded with their numbers | **MET** — § 3, including the two that are zeroes and why |
| 2 | The default is asserted by a test, not a comment | **MET** — § 4.1, 2 tests, mutation-proved RED |
| 3 | If **on**: no engine-side damage call remains, harness shows exactly one application | **N/A** — the decision is off; the engine-side call is deliberately retained and G7 keeps it guarded |
| 4 | If **off**: the failing input is named with its number, and a numeric reopening condition | **MET** — § 5, four numbered conditions |
| 5 | V7 § 6.1's table row amended either way | **MET** — new § 6.1.2, and the table row now points at it |
| 6 | The ledger row moves to `CLOSED`, not "deferred" | **MET** — C-1 closed as a decision; X-6 closed as a pin |

---

## 7. What this cost, and what it bought

The honest accounting: this phase did not measure the thing it was created to measure, and it could
not have. What it produced instead is a **guard that did not exist** (G8, catching an inversion
class no rule in the tree covered), a **default that is now asserted** rather than described, and a
**reopening condition with four numbers in it** so the next attempt is arithmetic rather than
re-litigation.

The risk table's third row anticipated the real failure mode here — *"'off' is chosen and quietly
forgotten again"* — and the mitigation was criterion 4's numeric condition plus criterion 6's ban on
the status "deferred". Both are honoured. The flag has now been off for two phases with a stated
reason instead of one phase with silence, which is the difference this gate existed to make.

---

## 8. Handoff

**To V9:** the flag is off, so V9 inherits Phase 2 task 2e's prepared patch and the four-part
reopening number in § 5. V9's re-measurement at 16 clients / 12 vehicles is condition 3, and it
cannot be run before X-34 is closed — the same blocker, one phase later.

**To whoever closes X-34:** it is the highest-leverage row left in this track. It blocks B-11, it
blocks this cutover, and it blocks V9's criterion-9 measurement. One `HarnessBehavior` value that
drives the shipped fire opcode unblocks all three.

**To Phase 6:** task 6.3 is done and its ledger row is closed. The other five rows are untouched by
this phase.
