# Phase 5 — The cutover gate

- **Track:** [`plans/debt-closure/plan.md`](../plan.md) · **Effort:** S (1 day)
- **Depends on:** Phase 2 task 2e (the prepared patch), Phase 3 (damage accounting), Phase 4 (tick budget)
- **Nature:** a decision with a proof obligation, not a refactor

---

## 1. Goal

Decide `ServerProjectileBridge.AuthoritativeFlight` with evidence in hand: **on**, with a proof that
damage applies exactly once, or **off**, with a written reason. Either outcome closes the item. What
does not close it is leaving the flag off with no statement, which is the state it has been in since
V7 merged.

---

## 2. What is being decided

The Unity server already simulates every projectile it spawns and applies its damage through
`Hitbox.ProjectileHit` and `ActorManager.Explode` — the path phase-05 and V1 established, which
works today. The library stepper is a **second** simulation. Running both applies every damage
number twice, which is precisely what V7's acceptance criterion 5 ("exactly once") protects.

So this is not a config flip. Its first task is **deleting the engine-side damage call**, which
Phase 2 task 2e has already written behind the flag with a test asserting exactly-one application in
both configurations.

Two things stay true whatever is decided, and are not part of this gate: grenades and deployables are
never ballistically stepped at any setting (pinned deliberate — the stepper terminates on the first
surface a segment touches, a grenade bounces, and a deployable's pose comes from a Rigidbody), and
the client's prefab array is what makes any of it visible.

---

## 3. Task 5.1 — Grade the evidence (0.25 d)

Three inputs, all from earlier phases:

| Input | From | What it has to show |
|---|---|---|
| Damage accounting under the harness | Phase 3 | Every hit produces exactly one damage application with the flag on |
| Tick p99 with the stepper active | Phase 4 task 4.2 | Under 33 ms at the § 2 check-list load — V7 criterion 9, never measured |
| Bandwidth with projectiles streaming | Phase 4 task 4.1 | Inside the criterion-8 budget |

## 4. Task 5.2 — Flip, or write the reason (0.75 d)

**If the evidence holds:** land Phase 2 task 2e's patch — delete the engine-side damage call, default
`AuthoritativeFlight` on, and re-run the harness. The double-damage test flips from "asserts both
configurations" to "asserts the shipped one", and V7's criteria 1, 5 and 11 get graded from a run.

**If it does not:** record which input failed and by how much, keep the flag off, and write the
reopening condition — the specific number that would change the answer. The prepared patch stays on
the branch, unmerged and referenced from the ledger, so the next attempt starts from a patch rather
than from a decision.

Either way, amend `phase-v7-projectiles.md` § 6.1 — its "deliberately NOT shipped" table has a row
for this, and that row is what a future reader will find.

---

## 5. File ownership

Writes: `ServerProjectileBridge`, the engine-side damage call sites, the double-damage test,
`plans/replication/phases/phase-v7-projectiles.md` § 6.1, `plans/debt-closure/debt-ledger.md`,
`plans/debt-closure/reports/`.

---

## 6. Acceptance criteria

1. All three evidence inputs are recorded with their numbers, whichever way the decision goes.
2. `AuthoritativeFlight`'s default is asserted by a test, not by a comment.
3. If **on**: no engine-side damage call remains (a grep proves it), and a harness run shows exactly one damage application per hit.
4. If **off**: the failing input is named with its number, and the reopening condition states the specific value that would change the answer.
5. V7 § 6.1's table row is amended either way.
6. The ledger row moves to `CLOSED` — not to "deferred".

---

## 7. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| The flag is flipped on partial evidence | 3 | 5 | **15** | All three inputs in § 3 are required; a missing input is a "no", not a judgment call |
| Double damage ships unnoticed | 2 | 5 | 10 | Acceptance criterion 3 is a harness run, not the unit test alone — the unit test is what makes the run interpretable |
| "Off" is chosen and quietly forgotten again | 3 | 3 | 9 | Acceptance criterion 4 requires a numeric reopening condition; criterion 6 forbids the status "deferred" |
| Tick p99 is measured at a load too small to be meaningful | 3 | 3 | 9 | The number is reported with its load and sample size, and Phase 3's scope lock means V9 re-measures at full load regardless |

---

## 8. Handoff

To **V9**: whichever way this goes, V9 re-measures at 16 clients and 12 vehicles. If the flag went
on, V9's criterion 9 measurement is the one that matters and this one is the baseline. If it stayed
off, V9 inherits the prepared patch and the reopening number.
