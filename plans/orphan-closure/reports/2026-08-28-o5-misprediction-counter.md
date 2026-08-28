# O5 report — `corrections: N` becomes a mispredict rate

- **Phase:** [`phase-o5-misprediction-counter.md`](../phases/phase-o5-misprediction-counter.md) · **Date:** 2026-08-28
- **Closes:** **X-41** → **B-8** can read the number it is graded on
- **Commit:** `fix(replication): the correction counter measures misprediction, not lag`

---

## 1. What was wrong

`Reconcile` compared `predicted.Position` — where the client is standing **now** — against an
authoritative state for tick `lastProcessedInputTick`, which is `lag` ticks in the past. A client
predicting perfectly has legitimately moved on from that position, so once `lag × speed` exceeds
`PositionToleranceMetres` (0.25 m: **2.1 ticks at a walk, 1.2 at a sprint**) every single snapshot
returned `Corrected` — and the replay then moved the client by nothing at all, which
`ACorrectedClientIsNotMOVEDByTheCorrection` had already proved.

## 2. What changed

A `Vec3[Capacity]` beside the input ring, written by `Record` with the position that tick's input
left the client at. `Reconcile` compares the server's answer for tick T against **the client's own
recorded position for tick T**.

`Record` grows a **required** third parameter. A defaulted one would let a caller that forgot it
keep the old comparison with nothing reporting the difference — this row reintroduced by its own
fix (O-D5).

An acked tick that has fallen out of the ring falls back to the current position. That is the
pre-X-41 comparison and the only one still available; it is the resynchronise neighbourhood, and
the code says it is an approximation rather than a second opinion.

**The second consequence closed with the first and needed no separate change.** With `Agreed`
returned on a correctly-predicting client, `ClientPredictionStage` stops calling
`ApplyCorrectedState(hardSnap: false)` on those snapshots, so the redundant `CharacterMove` of ~zero
stops with it.

## 3. The pin was INVERTED, not re-pinned

`TheCorrectionCounterMeasuresLagNotMisprediction` asserted **16 corrections on a client that
mispredicted nothing**. That is a pinned baseline in the `pinned-baseline-test-companion.md` sense,
and when the gap it tracks closes the rule is explicit: invert it, never re-pin to whatever the run
now reports.

It is now `TheCorrectionCounterMeasuresMispredictionRatherThanLag`, a `[Theory]` asserting **zero**
at 4, 10 and 25 ticks of lag — every lag rather than the one the old pin happened to use. 25 ticks
is 0.83 s of walking, nearly three metres of legitimate lead over the server.

Two things carried over deliberately:

- **The premise guard survives as a live assertion.** Each case still asserts that its lag exceeds
  the tolerance, so a zero is the fix working rather than the test having quietly stopped
  exercising anything, and the message says to raise the lag rather than delete the case.
- **The failure text forbids re-pinning**: a rise here is a regression in the comparison, not a new
  baseline.

`ARealMispredictionIsStillCorrected` is the companion direction, and it is the one that stops the
fix from degenerating into "never correct anything": the server refuses the client's position at
the acknowledged tick, the correction fires, and the client lands on authority plus the four
unacknowledged inputs.

## 4. Evidence

**Three mutants, all observed RED** (3 of 28 red each, against `PredictionReplayTests` +
`ClientReplicationTests`):

| # | Mutation | What it restores |
|---|---|---|
| M1 | `predictedThen = predicted.Position` | The X-41 defect verbatim |
| M2 | the history is looked up at `tick + 1` | An off-by-one comparison — the shape that "shows up as a correction that never converges" |
| M3 | `_positions[slot]` is never written | The history exists and is empty |

Full suite: **1,289 passed / 0 failed** in the replication suite, 1,960 across the solution.

## 5. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | A correctly-predicting client at lag reports zero corrections | **MET** — at 4, 10 and 25 ticks |
| 2 | A genuine misprediction is still corrected, and lands where the replay says | **MET** — `ARealMispredictionIsStillCorrected` |
| 3 | An acknowledgement older than the ring still resynchronises | **MET** — `AnAcknowledgementOlderThanTheInputBufferResynchronises`, unchanged |
| 4 | The pin is inverted, not re-pinned, and forbids re-pinning | **MET** — § 3 |
| 5 | Observed RED before the fix | **MET** — three mutants |
| 6 | Gates exit 0 | **MET** |

## 6. Every `corrections: N` recorded before today is a lag measurement

They are not re-interpretable and must not be compared with numbers taken after this phase. That
sentence is on `CorrectionCount` itself and in the inverted test's own text, so a reader who meets
an old artifact is not left to work it out from the ledger.

**`PositionToleranceMetres` did not move**, deliberately: the defect was never the threshold but
what was being compared against it, and changing both at once would make it impossible to say which
one fixed anything.
