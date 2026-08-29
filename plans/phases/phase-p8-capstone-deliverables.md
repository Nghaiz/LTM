# Phase P8 — M3 and M4's unowned clauses

- **Plan:** [`../plan.md`](../plan.md) · **Size:** L
- **Grades:** the bolded halves of **M3** and **M4** · **Runs after:** [P7](phase-p7-v9-integration.md)

---

## 1. These clauses have had no owner since they were written

They existed in exactly two files — `plans/unity-client/phases/phase-03-match.md` and
`phase-04-polish.md` — and when those specs were deleted under the delivered-phases policy on
2026-08-26, the criteria were folded into `plans/00-shared/README.md` **specifically so that
deleting the specs would not silently delete the criteria**.

That README is now deleted too. The search that justified folding them in was recorded: the strings
*"no manual file editing"*, *"wrong password"*, *"returns to the lobby"*, *"0 P0"*, *"5-scenario"*
and *"on/off comparison"* returned **zero** hits anywhere else under `plans/` or `docs/`. So this
phase is the third home for the same six clauses, and the second time carrying them forward is the
only thing preventing their loss.

**They are the capstone's defense deliverables.** They are not netcode, which is why no netcode
track ever adopted them.

---

## 2. The six clauses, and what each one actually demands

### M3 — the flow

**"The flow runs with no manual file editing."** Login → lobby → room → capture point → win/lose →
back to lobby, without editing a config file, a scene, or an env var between steps. Today the
server's scene is chosen by `IRONFRONT_GAMESERVER_SCENE` and the client's endpoint by
configuration — establish which of those a player is currently expected to set by hand, and remove
that expectation. **Measure this by having someone who did not build it run the flow.**

**"A wrong password gives a clear error."** The master server authenticates; what the client renders
on rejection is the deliverable. A silent failure, a hang, or a raw status code all fail this.

**"Disconnecting mid-match returns to the lobby with a message."** Both halves: the return, and the
message. A client that drops to a black screen or sits in a dead match fails it.

> Related and **not** the same thing: `ServerTickLoop.OnClientDisconnected` releases the slot and
> forgets the actor without calling `Actor.LeaveSeat()`, so a departed client's body stays sitting
> in its vehicle. That is a server-state defect ([P1](phase-p1-exception-storm.md) § 4 names it) and
> this clause is a client-flow requirement. Fix them separately; they will look like one bug in a
> demo.

### M4 — the report

**"0 P0 bugs."** Needs a P0 definition before it can be graded. Propose: *anything that stops a
16-player match from being played to a conclusion*. Write the definition down first — an ungraded
severity scale grades everything as pass.

**"The 5-scenario measurement table filled in."** Four of five scenarios were measured for the
master server on 2026-08-14; the fifth was delivered as a measured argument rather than a second
implementation and said so. Re-check that table against what [P7](phase-p7-v9-integration.md)
produces and fill the gaps.

**"The on/off comparison table for the five netcode techniques filled in."** Each of interpolation,
prediction, reconciliation, lag compensation and delta compression, measured with the technique on
and off. **Nothing has ever produced this table**, and it needs a harness switch per technique —
that is the real cost of this clause and it should be scoped before it is scheduled.

**"30 minutes of continuous play with no crash and no leak."** Distinct from P7's five-match soak:
that one asserts pool cleanliness between matches; this one is wall-clock with a human in it.
Grade the log and the memory curve, not the exit code.

Plus the unbolded three, which have partial owners: **load test with 16 clients** (P7), **the
measurement report** (`docs/report-chapter-*.md`, partly written), **documentation**, and a **demo
video** (nothing).

---

## 3. Tasks

### 3.1 — Define P0, in writing, before grading anything (S)

One paragraph, with two examples that are P0 and two that are not.

### 3.2 — Walk the M3 flow and record where a human has to edit a file (M)

One pass, notes at every step. The output is a list of manual interventions; the fix is removing
them, and the list is what says whether that is one task or ten.

### 3.3 — The two error paths (M)

Wrong password, and mid-match disconnect. Both need a rendered message, and both are client work
against a master-server behaviour that already exists.

### 3.4 — Scope the on/off comparison harness (M)

Five techniques, each needing an off switch that does not change anything else. **Scope it before
scheduling it** — if a technique cannot be switched off without disabling another, that is the
finding, and the table's row says so rather than being left blank.

### 3.5 — The 30-minute session, and the demo video (M)

One run, watched, recorded. The recording is the demo video and the log is the leak evidence; they
are the same 30 minutes.

---

## 4. Acceptance

| # | Criterion |
|---|---|
| 1 | P0 is defined in writing before any bug is graded against it |
| 2 | The M3 flow runs end to end with zero manual file edits, verified by someone who did not build it |
| 3 | A wrong password renders a clear message; a mid-match disconnect returns to the lobby with one |
| 4 | The 5-scenario table has a figure or a stated reason in every cell |
| 5 | The on/off table has a figure, or a named reason why that technique cannot be switched off in isolation |
| 6 | 30 minutes of continuous play with the log and memory curve attached |
| 7 | A demo video exists |

---

## 5. Out of scope

- **The 16-client load measurement itself** — [P7](phase-p7-v9-integration.md) produces it; this
  phase reports it.
- **Vehicles, multiple maps, progression, voice chat, anti-cheat beyond basic validation.** All
  cut from core scope, and the anti-scope-creep rule applies: anything added names what leaves.
