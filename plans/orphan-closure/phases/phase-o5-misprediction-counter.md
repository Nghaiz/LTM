# Phase O5 — `corrections: N` becomes a mispredict rate rather than a lag metric

- **Track:** [`plan.md`](../plan.md) · **Effort:** S (1 d)
- **Depends on:** nothing. Runs in parallel with O1–O4.
- **Closes:** **X-41** → **B-8** can read the number it is graded on

---

## 1. The defect, restated from the row

`Reconcile` compares the client's **current** position against an authoritative state for a tick
`lag` in the past, so a client predicting perfectly is compared against a position it has
legitimately left. Once `lag × speed` exceeds `PositionToleranceMetres` — 0.25 m, which is **2.1
ticks at a walk and 1.2 at a sprint** — every snapshot returns `Corrected` even though the replay
then moves the client by nothing at all.

Two consequences:

1. **`corrections: N` in any artifact is a lag metric, not a mispredict rate.** B-8 must not read
   it as one.
2. `ClientPredictionStage` calls `ApplyCorrectedState(hardSnap: false)` on every `Corrected`, which
   pushes the `CharacterController` through a `CharacterMove` of ~zero every snapshot — the exact
   redundant move that method's own remark says it avoids by only writing back on a change.

## 2. The decision — O-D5 and O-D6

**The reconciler keeps a position history beside the input ring and compares at the acknowledged
tick.** That is what the row said the fix needs, and it is a change to what the class stores rather
than to what it does.

**`Record` grows a REQUIRED third parameter, not an optional one.** A defaulted position would let
a caller that forgot it keep the broken comparison silently — which is the failure mode this row
is about, reintroduced by the fix for it.

**`PredictionReplayTests.TheCorrectionCounterMeasuresLagNotMisprediction` is INVERTED, never
re-pinned (O-D6).** It is a pinned baseline asserting 16 corrections on a client that mispredicted
nothing. When the count drops to zero the honest move is to delete the constant and assert the
healthy state, rewriting the message so a future non-zero reads as a regression. Re-pinning it to
a new number would convert a fix into a permanent baseline — the trap
`pinned-baseline-test-companion.md` names, and the reason that rule exists.

## 3. Task O5.1 — the history (S)

A `Vec3[]` parallel to `_inputs` and `_ticks`, written by `Record` with the predicted position
**after** that tick's input was applied. `NetPredictionClock` already raises `OnTickSimulated`
after `_agent.Tick(...)`, so the caller has exactly that value to hand and no new ordering is
introduced.

`Reconcile` then compares `authoritative.Position` against the recorded position for
`lastProcessedInputTick`:

- **found, and within tolerance** → `Agreed`. The client mispredicted nothing; everything it has
  done since is built on a base the server confirms.
- **found, and outside tolerance** → a real misprediction: adopt authority, replay, count it.
- **not found** — the acked tick has fallen out of the ring — → fall back to comparing the current
  position, which is the pre-O5 behaviour and the only comparison still available. That is the
  resynchronise neighbourhood, and it is honest about being an approximation.

## 4. What this changes about existing artifacts

Every `corrections: N` recorded before this phase is a **lag** measurement and stays one. They are
not re-interpretable and must not be compared with numbers taken after it. The inverted test's
failure message says so, so a reader who meets an old artifact is not left to work it out.

The second consequence closes with the first and needs no separate change: with `Agreed` returned
on a correctly-predicting client, `ClientPredictionStage` stops calling `ApplyCorrectedState` at
all on those snapshots, and the redundant `CharacterMove` stops with it.

## 5. Acceptance

1. A client running the same `MovementCore` as the server over the same inputs, `lag` ticks behind,
   reports **zero** corrections — at a walk and at a sprint, and at a lag well past the one the
   old pin used.
2. A client that genuinely mispredicts — a position the server refuses — is still corrected, and
   the correction still lands it where its own replay says.
3. An acknowledgement older than the ring still resynchronises.
4. The X-41 pin is **inverted**, not re-pinned, and its message forbids re-pinning.
5. **Observed RED before the fix**, named in the report with the mutation used.
6. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0.

## 6. Out of scope

**`PositionToleranceMetres` does not move.** It is sized against the wire's 6.25 cm quantiser step
and its own remark explains the arithmetic; the defect was never the threshold but what was being
compared to it. Changing both at once would make it impossible to say which one fixed anything.
