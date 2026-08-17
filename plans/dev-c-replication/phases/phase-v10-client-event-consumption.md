# Dev C — Phase V10: The client half of combat, which was never built

> ## ⚠ Execution order: run this **immediately after V0**, **before V3**.
>
> **The filename sorts last. The phase does not run last.** V10 is numbered 10 because it was added
> after the design of record was written and approved — not because it comes after V9. Its slot in
> the track is:
>
> ```
> V0  →  V10  →  V1 / V2 / V8 (parallel)  →  V3  →  V4  →  V5 / V6 / V7  →  V9
>        ↑ here
> ```
>
> Running it late would mean building vehicles, mounted weapons and projectiles on top of a client
> that cannot render a death, a muzzle flash, a hitmarker, a score, or a capture point — so every
> defect found in V4-V7 would be indistinguishable from the ones this phase closes.

> Design of record:
> [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md)
> § 2.4 and § 6. **This phase is not in that document's § 6 phase table.** It was approved on
> 2026-08-17 after the gap in § 3 below was found by grep and verified; this file is the record of
> that addition.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files, `Span<byte>` over
> `byte[]`) and § 7 (ownership). Per design § 7, Dev C writes every file here including those under
> `Assembly-CSharp/`; Dev A owns only the Editor half, enumerated in § 7.
>
> **Depends on V0.** **No wire change.** Every byte this phase consumes is already defined, already
> implemented, already conformance-tested, and already being sent by a shipped server.

---

## 1. Objectives

<!-- SKELETON: the gap statement, the seven dead events, the six outcomes -->

## 2. Decisions taken (do not re-litigate)

<!-- SKELETON -->

## 3. Detailed tasks

<!-- SKELETON: task list with file tables -->

## 4. Acceptance criteria

<!-- SKELETON -->

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| <!-- SKELETON --> | | | | |

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| <!-- SKELETON --> | | |

## 7. Handoff

<!-- SKELETON: Dev A Editor checklist E1..En, V1, V8, V9 -->
