# Debt-closure phase specs — executed; thirteen deleted 2026-08-26, two kept

All nine phases of [`../plan.md`](../plan.md) merged between 2026-08-19 and 2026-08-26. A phase
**spec** is an instruction to a run that has happened; the **reports** are the durable record and
they stay. The specs are deleted rather than archived because git already keeps them, and a
directory of executed instructions reads to the next person as work outstanding.

**Thirteen were deleted. Two were not, and the reason is the same in both cases:** a living document
cites them as a **contract**, not as history.

| Kept | Why it is not spent |
|---|---|
| [`phase-3-harness.md`](phase-3-harness.md) | Its **§ 2 is the thirteen-check scope lock**, cited as authority by [`verdict-closure`](../../verdict-closure/plan.md) R1 and R5, and by the ledger. R5 task R5.2 **edits** it — check 10 is assigned to lane A there and cannot be graded by lane A at all (**X-33**) |
| [`phase-3d-lane-b.md`](phase-3d-lane-b.md) | Its **§ 2** owns B-1…B-9, B-13, B-14 as lane B (the ledger's § 9 cites it to correct a paragraph that read as if they all went to one lane); its **§ 5 item 5** is the human-pass deliverable that **X-38** is filed against; its **§ 6** is the rule V-D7 inherits — *no phase may patch a game defect inside the harness* |

**Every deleted file is recoverable at `b23aa28`**, the last commit at which the whole set was
present:

```
git show b23aa28:plans/debt-closure/phases/phase-4-measure.md
git checkout b23aa28 -- plans/debt-closure/phases/          # all of them back
```

| Phase | Spec file | Record of what it did |
|---|---|---|
| 0 | `phase-0-ledger.md` *(deleted)* | [`../debt-ledger.md`](../debt-ledger.md) — the ledger **is** phase 0's deliverable |
| 1 | `phase-1-authoring.md` *(deleted)* | [`2026-08-19-phase-1-authoring.md`](../reports/2026-08-19-phase-1-authoring.md) + red / green / play-mode proofs |
| 2 | `phase-2-code.md` *(deleted)* | [`2026-08-19-phase-2-code.md`](../reports/2026-08-19-phase-2-code.md) + red / green / compile proofs |
| 3 | [`phase-3-harness.md`](phase-3-harness.md) **(kept)** | split into 3A–3F; § 2 is a live scope lock |
| 3A | `phase-3a-player-slots.md` *(deleted)* | [`2026-08-20-phase-3a-player-slots.md`](../reports/2026-08-20-phase-3a-player-slots.md) |
| 3B | `phase-3b-handshake-residual.md` *(deleted)* | [`2026-08-20-phase-3b-handshake-residual.md`](../reports/2026-08-20-phase-3b-handshake-residual.md) |
| 3C | `phase-3c-client-input.md` *(deleted)* | [`2026-08-20-phase-3c-client-input.md`](../reports/2026-08-20-phase-3c-client-input.md) |
| 3D | [`phase-3d-lane-b.md`](phase-3d-lane-b.md) **(kept)** | [`2026-08-25-phase-3d-lane-b-verdicts.md`](../reports/2026-08-25-phase-3d-lane-b-verdicts.md) |
| 3E | `phase-3e-run-and-ledger.md` *(deleted)* | [`2026-08-25-phase-3e-run-and-ledger.md`](../reports/2026-08-25-phase-3e-run-and-ledger.md) |
| 3F | `phase-3f-x19-drawn-vs-held.md` *(deleted)* | [`2026-08-23-x19-drawn-not-recorded.txt`](../reports/2026-08-23-x19-drawn-not-recorded.txt), [`lane-b-rerun`](../reports/2026-08-23-x19-lane-b-rerun.txt), [`mutation-proof`](../reports/2026-08-23-x19-mutation-proof.txt) |
| 4 | `phase-4-measure.md` *(deleted)* | [`2026-08-26-phase-4-measure.md`](../reports/2026-08-26-phase-4-measure.md) |
| 5 | `phase-5-cutover-gate.md` *(deleted)* | [`2026-08-26-phase-5-cutover-gate.md`](../reports/2026-08-26-phase-5-cutover-gate.md) + [`ac2-mutation-proof`](../reports/2026-08-26-phase-5-ac2-mutation-proof.txt) |
| 6 | `phase-6-rows-no-run-closes.md` *(deleted)* | commit **`fa275d5`** (#195) and five mutation proofs — [`d1`](../reports/2026-08-26-d1-mutation-proof.txt), [`e6`](../reports/2026-08-26-e6-mutation-proof.txt), [`x6`](../reports/2026-08-26-x6-mutation-proof.txt), [`x7`](../reports/2026-08-26-x7-mutation-proof.txt), [`x8`](../reports/2026-08-26-x8-mutation-proof.txt). **Phase 6 shipped no `.md` report**, which is why its five task records live in the commit body and nowhere else |
| 7 | `phase-7-ops-to-digest.md` *(deleted)* | [`2026-08-26-phase-7-ops-to-digest.md`](../reports/2026-08-26-phase-7-ops-to-digest.md) |
| 8 | `phase-8-hygiene.md` *(deleted)* | [`2026-08-26-phase-8-hygiene.md`](../reports/2026-08-26-phase-8-hygiene.md) |

## Two things this deletion does not do

**It does not touch [`plans/asmdef-seam/phases/`](../../asmdef-seam/phases/).** That track shipped
**no reports at all** — C2, C3 and C4 carry their execution record inline (`## 3.1 C4a — done
2026-08-26`, and each phase's § 0/§ 1 holds the enumeration that corrected the plan's counts three
times). Deleting them would destroy the only record. They stay, and C5 joins them.

**It does not rewrite the reports' inbound links.** A report written during phase N links to
`../phases/phase-N.md`, and those targets are gone. Left dead deliberately: a report is a dated
record of what was true when it was written, and silently re-pointing its links would make it a
document that has been edited since. Use the `git show` line above. Links from **living** documents
— the ledger, `plan.md`, `phase-v7-projectiles.md` — were repointed here in the same commit, because
those are read for current truth rather than as history.

## What is still open, and where it went

Thirty-one ledger rows. Twenty-eight are owned by
[`plans/verdict-closure/plan.md`](../../verdict-closure/plan.md) R1–R6, one more step belongs to
[`asmdef-seam C5`](../../asmdef-seam/phases/phase-c5-autoreferenced.md), and three are parked by
decision (**X-14**, **C-5**, **C-12**). [`../debt-ledger.md`](../debt-ledger.md)'s `closes in` column
is the authority, and every cell in it now names a phase file that exists or a written parking.
