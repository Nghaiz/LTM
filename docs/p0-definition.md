# P0 — what it means, before anything is graded against it

Written 2026-08-30 for P8 *(file deleted -- `git show 509c70d:plans/phases/phase-p8-capstone-deliverables.md`)* task 3.1, which
exists because M4's clause **"0 P0 bugs"** could not be graded without it. An ungraded severity
scale grades everything as pass: with no definition, every open row is arguably not-P0 and the
clause is met by saying so. This file is written **before** the grading in § 4, and the order is
the point.

---

## 1. The definition

> **A defect is P0 when it stops a 16-player match from being played to a conclusion on the
> shipping build, with nobody intervening while it runs.**

"Played to a conclusion" is the whole M3 loop at the M4 scale: sixteen clients log in, join one
room, play the round, and see a winner — then land back in the lobby. "On the shipping build"
excludes anything only reachable through the lane harnesses or the Editor. "Nobody intervening"
excludes defects a human can work around **during** the run, and includes ones a human has to
prevent **before** it, because a player has no operator.

Three clauses widen it, and each names a way a match can be worthless without technically
stopping:

- **Wrong outcome.** The round ends, and the winner is not the team that won — a scoring, capture
  or damage-authority fault that survives to the final scoreboard.
- **Unplayable for a majority.** The round ends and most players could not meaningfully take part:
  they cannot see each other, cannot shoot, or cannot move.
- **Unrecoverable state.** A player who drops cannot get back to a playable state without
  restarting the process.

One clause narrows it: **severity is judged at the shipping configuration**, 16 players on one
map in Conquest with bots. A defect that needs 32 clients, a second map, or a vehicle is not P0,
because none of those is in core scope ([`plan.md`](../plans/plan.md) § 5 rule 6).

---

## 2. Two that are P0

**X-69 — the NRE storm in `AiActorController.LocalAvoidanceVelocity`.** P7 reproduced it at
**10,126 occurrences** in one 600-second run at 16 clients. It is P0 not because exceptions are
ugly but because it is the shipping AI path under the shipping load: a run that throws ten
thousand times is not one whose conclusion anybody should trust, and the exception gate voided a
P5 run outright over it. It fails the *unplayable for a majority* clause with a measured number
attached.

**The client flow was never wired into Unity.** Found by P8 task 3.2 and recorded in
[`m3-flow-manual-interventions.md`](m3-flow-manual-interventions.md): `MasterSession` was
constructed in exactly one place in the repository — a test project — `LobbyShellOverlay.Bind`
had no callers under `Assets/`, and no client code called `SceneManager.LoadScene`. Sixteen
players could not have reached one room, because one player could not. This is the cleanest P0 on
record here and it was invisible to every gate in `ci.ps1`, which is [`plan.md`](../plans/plan.md)
§ 5 rule 1 stated as a defect rather than as a principle.

---

## 3. Two that are not

**X-70 — four vehicle spawners produce `quadbike`/`jeep` with no network id.** Live, confirmed,
and reported four times in P7's own tally. Not P0: vehicles are outside core scope, and a
16-player infantry Conquest round reaches a conclusion with every one of those spawners broken.
It is a real defect with a real cost and it does not stop the match.

**Criterion 9 — bandwidth 22% over budget (worst client 6,251 B/s against 5,120).** Measured,
failing, and P7 re-opened B-16 over it. Not P0: the budget is a design target chosen for a
domestic uplink, `EntriesShed` was **0** on all three runs — nothing was dropped — and every
sixteen-client run played to a conclusion at that rate. It is a performance failure, not a
completion failure. Were `EntriesShed` non-zero the judgement would flip, because shedding
entries is state that never arrives.

The two borderline calls worth writing down:

- **X-73, projectile ids surviving a world reset.** Five leaked ids across eight resets. Not P0
  at one match, because a single round concludes correctly; it becomes P0 the moment the leak is
  shown to exhaust the pool inside a session a player would actually sit through, which is
  exactly what the 30-minute soak in P8 task 3.5 is for. Graded **not-P0, pending that run** —
  and named here so the pending half is not quietly forgotten.
- **X-71, the server walking a claimed player body ~518 m.** Not P0 by the letter: the match
  concludes. It sits one step from the *wrong outcome* clause, because a body the server is
  moving without input can capture a point.

---

## 4. Grading "0 P0 bugs" as of 2026-08-30

**FAILING — 2 open.** X-69, and the unwired client flow (closed by this phase's own
implementation; it is listed because it was open when the definition was written, and a
definition written after the grading would be worthless).

| Row | Verdict | Clause |
|---|---|---|
| X-69, 10,126 NREs at 16 clients | **P0, open** | unplayable for a majority |
| Client flow never wired (P8 § 3.2) | **P0, closed by P8** | stops the match starting at all |
| X-73, leaked projectile ids | not P0, pending the 30-minute soak | — |
| X-71, server walks a claimed body | not P0 | one step from *wrong outcome* |
| X-70, unauthored vehicle prefabs | not P0 | out of core scope |
| B-16, bandwidth 22% over budget | not P0 | `EntriesShed` 0; match concludes |
| X-61, X-67, X-68, X-72, X-75 and the instrument rows | not P0 | harness and authoring gaps |

The clause is not met, and the honest reading of M4 is that it cannot be met until X-69 closes.

---

## Related

- [`../plans/plan.md`](../plans/plan.md) § 5 — the standing rules this grading obeys
- [`../plans/debt-ledger.md`](../plans/debt-ledger.md) — the source of truth for every X- row above
- [`m3-flow-manual-interventions.md`](m3-flow-manual-interventions.md) — the audit that produced
  the second P0
- [`capstone-measurement-tables.md`](capstone-measurement-tables.md) — M4's other two clauses
