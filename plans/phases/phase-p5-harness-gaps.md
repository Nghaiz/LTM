# Phase P5 — the three gaps in the harness itself

- **Plan:** [`../plan.md`](../plan.md) · **Closes:** **X-28**, **X-29**, **X-37** · **Size:** M
- **Runs after:** [P4](phase-p4-lane-b-regrade.md). These are gaps in the *instrument*, and P4's
  run set is what shows which of them still bite.

---

## 1. Three gaps, and one of them has already half-closed without anyone re-reading it

### X-37 — check 5 has no programme, and its blocker just died

**Half closed 2026-08-27:** check 6 (E12, scene ordering) graded PASS — its case is an ordinary
client start, so no programme was ever missing, only the reading.

**Check 5 (E11, the A16 camera hijack) stays open, and its stated blocker was X-44** — *"a client
that can reach a turret"*, because `ClientSeatRequester.TryFindNearestSeat` only sees seats within
`SeatArbiter.MaxSeatReachMetres` and the programme vocabulary had no verb that walks to one
(`approach` resolves a **player display name**, not a vehicle).

**X-44 closed on 2026-08-28.** A scripted client can now walk to a vehicle. So check 5's blocker is
gone and nobody has written the programme. That is this phase's first task, and it is small.

### X-29 — two checks have no measurement in the record

**Check 13's middle term.** "Death → **input disable** → respawn screen" is graded on two of its
three terms. `combat.driverEnabled` records whether `NetClientLocalCombatDriver` is *running* — and
it must keep running to accept a respawn request, so its staying `true` after death is **correct**
and is not the measurement. Nothing in the record says whether a dead player's movement and fire
input are suppressed.

A second, smaller gap sits beside it: the victim's programme sets `respawn: true` on its last step
and the last capture is at that step's *start*, so **no artifact shows the respawn landing**.

**Check 2's half was WITHDRAWN 2026-08-25** — the measurement was there and was mis-read. Drawn-vs-
offline *is* the discriminator, because `SetAuthoritativeState` writes the server's totals straight
to the text fields and never touches the offline scoreboard. Drawn ≠ offline at 0 of 7 checkpoints,
on a different clock. **Do not re-open this half.** The residual caveat is narrow and stated: it
proves *not offline-driven*, and authoritative-driven only by way of `ScoreUi`'s two-source
structure rather than by comparing against the server's own number.

### X-28 — one spawn point puts all three players in each other's fire

X-22 narrowed the spawn directory to one slot so the pair would be adjacent, which is what it was
for. The cost is that it co-locates the **witness** too, and does not isolate the point.

**First half addressed 2026-08-25** — `combat-observer-b.json` strafes for its first seven seconds,
pure programme data, nothing under test changed. Measured over three runs the resolver's nearest
target is the intended TARGET in two and the WITNESS in the third. **Two of three: seven seconds of
walk against a shared spawn point buys separation rather than guaranteeing it.**

**Still open, and the second half is the more serious one.** A third party with **no actor id**
(`killerActorId 65535`) shot the driver, which respawned **1.6 km** from the pinned point and the
run graded nothing. It did not recur across the next three runs, which is not evidence it is fixed.

**And a consequence found while measuring, which raises this row's importance:** with the spawn
pinned, all three bodies start **inside** the driver's `holdDistanceMeters: 6.0`, so `ApproachMoveZ`
returns 0 from the first frame and the driver never moves — the 1.5–3.3 m spread is spawn jitter,
not an approach. The pin does not merely co-locate the witness; it parks the shooter inside the
range where the old flake fired.

---

## 2. Tasks

### 2.1 — X-37: write the E11 programme, now that a client can reach a turret (S)

E11 is *"B enters a mounted turret and takes damage while A watches"*. The verb X-44 shipped is the
one that was missing. Write the programme, run it, and grade **B-5** — pass or fail, but not
"ungraded".

If the verb turns out not to reach a *turret* seat specifically — X-44 closed for driver seats —
say so and file it. Do not widen `TryFindNearestSeat`'s reach constant to make a programme work;
that is patching a game seam inside the harness.

### 2.2 — X-29: record the middle term, and capture the respawn landing (M)

**The behaviour exists** — verified 2026-08-29 against the tree:
`NetClientLocalCombatDriver` carries `_inputSuppressedByDeath` and calls `local.DisableInput()` at
`:325`. So this is a missing *measurement*, not a missing feature, and the measurement should
**confirm** rather than discover. Say so when it passes; a check that could only ever pass is worth
less than one that was seen failing, so mutate the suppression off once and watch the new
measurement go red.

Add the one measurement check 13 is missing: whether a dead player's movement and fire input are
actually suppressed. Record it beside `driverEnabled` rather than instead of it — the two answer
different questions and conflating them is what produced this row.

**One stale comment to correct while here.** `NetClientCombatPresenter.cs:276-278` still reads
*"no Unity component holds one yet, which is a recorded gap"* about `ClientCombatState`. That gap
closed: `NetClientLocalCombatDriver` declares itself "the one production owner" and holds one at
`:50`. The comment is the last surviving copy of a fact that stopped being true, which is the same
decay this whole consolidation exists to end.

Move the victim programme's last capture to *after* the respawn step, so an artifact exists of the
respawn landing.

### 2.3 — X-28: separate the three roles' spawns (M)

Three candidate shapes, already enumerated; pick with a measurement, not a preference:

- separate near-adjacent pins per role rather than one shared slot;
- a programme step that walks the witness out of the line (the seven-second strafe, generalised);
- a spawn point the bots do not contest.

**The acceptance test is not "the run passed".** It is that the resolver's nearest target is the
intended target in **N of N** runs, and that the shooter starts **outside** `holdDistanceMeters`
so `ApproachMoveZ` is non-zero on the first frame. Both are measurable per run; quote both.

The un-recurring third party stays **open and named** unless a run reproduces it. Say "did not
recur in N runs" — never "fixed".

---

## 3. Acceptance

| # | Criterion |
|---|---|
| 1 | B-5 carries a verdict from a run of a real E11 programme |
| 2 | Check 13's input-suppression term is recorded, separately from `driverEnabled` |
| 3 | An artifact exists showing a respawn landing |
| 4 | Across a run set, the resolver's nearest target is the intended target in every run, with the count quoted |
| 5 | The shooter starts outside `holdDistanceMeters`, evidenced by a non-zero `ApproachMoveZ` on the first frame |
| 6 | X-28's third-party half is either reproduced and diagnosed, or reported as not-recurring with the run count |
| 7 | No fix reaches into game code to make a harness programme work |

---

## 4. Out of scope

- **X-29's check-2 half.** Withdrawn 2026-08-25 with a measurement; re-opening it needs a new
  finding, not a re-reading.
- **Any game defect these runs surface.** File it; do not fix it here.
