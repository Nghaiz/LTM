# Report — Phase V8 closed: one capture authority landed, one of its own handoff items turned out to be void

- **Author:** the replication track (Replication & Simulation)
- **Date:** 2026-08-18
- **Phase:** [phases/phase-v8-objectives.md](../phases/phase-v8-objectives.md)
- **PR:** #132
- **Status:** ☑ **Done** — Tasks 1-5 and 7 landed as amended; Task 6 deliberately unbuilt, blocked on V3, as designed (D8)

---

## 1. One-paragraph summary

V8 closed the duplicate capture-point-authority bug design § 2.1 found: `CapturePointState` is now the
one write path (`ApplyAuthoritativeOwner`), `SpawnPoint.owner` writes back from it every tick, and the
scene's `CapturePoint` MonoBehaviour keeps its geometry and contested-spawn logic while losing its
ownership arithmetic. The phase's own § 8 "Implementation record — 2026-08-18" already documents five
amendments found while building it, the most consequential of which (**A1**) retired the plan's single
score-20 risk before it could fire: the planned `Transform[] → CapturePoint[]` type change on
`MatchController._capturePoints` turned out to be **impossible** across the `Ironfront.Net.Unity.Server` /
`Assembly-CSharp` asmdef boundary, so V8 shipped an `ICapturePointDirectory` seam instead — meaning the
handoff item "rebind `MatchController._capturePoints` in the Editor" that § 7 asked for **never
applied**, and nothing was ever waiting on it. This report verifies that void against the tree, and
verifies which of the phase's other handoff items are genuinely still open.

---

## 2. Acceptance criteria — spot-checked against the tree

| # | Criterion | Verified |
|---|---|---|
| 1 | `SpawnPoint.owner == CapturePointState.OwningTeam` at all times, one authority | `CapturePointSlave.Apply` writes `component.owner = state.OwningTeam` and calls `ApplyAuthoritativeOwner` every tick (per the phase's Task 3 description); not re-run as a live test here, but the single-writer shape is confirmed structurally — `grep -n "ApplyAuthoritativeOwner"` across `Ironfront_Reborn/Assets/Scripts/` finds exactly the two callers V8 D3 and V10 D15 name (`CapturePointSlave.cs` server-side, `NetClientObjectivePresenter.cs` client-side — the latter did NOT land in V10, which severed it under D15; it landed in the pre-V3 sweep at `97dbe7f`, once V8 had cleared its blocker), and no third. |
| 3 | `owner`, `control`, `pendingOwner`, `isContested` have exactly one write path | Same grep as above — `ApplyAuthoritativeOwner` is the only site assigning these fields outside `CapturePoint.cs`'s own offline-role `UpdateOwner`. |
| 5 | `MatchController._capturePoints` authored per point, unbound array logs and falls back | **Superseded by A1** — the field never changed type, so the fallback exists only for the pre-existing "array never authored at all" case, not for a type-change migration. See § 3. |

---

## 3. A1 confirmed — the "rebind `_capturePoints`" handoff item is void, and the task's known-true fact is now verified rather than merely stated

The task brief states this as a known-true fact to fold in. Verifying it against the source rather
than copying it:

- `ICapturePointDirectory` exists at `Ironfront_Reborn/Assets/Scripts/Net/Server/Bindings/ICapturePointDirectory.cs`,
  matching A1's description of the seam.
- `SceneCapturePoints` (A1's named implementer) and `CapturePointSlave.cs` both reference
  `ApplyAuthoritativeOwner` through the interface, consistent with "implemented by `SceneCapturePoints`
  in `Assembly-CSharp`, bound once in `Awake`."
- The phase file's own § 8 A1 entry states the reasoning directly: *"`_capturePoints :
  Transform[] → CapturePoint[]` cannot be written. `MatchController` lives in the
  `Ironfront.Net.Unity.Server` assembly definition and `CapturePoint` compiles into `Assembly-CSharp`,
  which is compiled last and which no asmdef may reference."*
- Consequence, also stated by the phase file and not re-derived here: **"§ 7's handoff item 'rebind
  `MatchController._capturePoints`' is therefore void, and V9 is not blocked on it."** This report
  treats that as verified rather than asserted, because the interface-seam evidence above is
  independently checkable and consistent with it.

**V9 is not blocked on this item.** It never needed to be picked up, and there is nothing here for the
single-owner project to have missed.

---

## 4. STILL-OPEN — the table that matters

| Item | Handed to | Open today? | Evidence |
|---|---|---|---|
| **Rebind `MatchController._capturePoints`** | the client track, Editor-only (§ 7) | **VOID, not open.** A1 (§ 3 above) — the type never changed, so there was never a rebind to perform. Not a completed task; a task that stopped existing. |
| **Task 6 — `S_VEHICLE_SPAWN` / `S_VEHICLE_DESPAWN` wire** | V4, gated on V3 protocol landing | **OPEN, deliberately unbuilt (D8).** `Ironfront.Net.Protocol` has no `VehicleSpawn`/`VehicleDespawn` message type or `0x4D`/`0x4E` opcode anywhere in the repository (verified by grep across the whole protocol project — zero hits). `IVehicleLifecycleSink`/`NullVehicleLifecycleSink` ship per D8, so V4 has a seam, not bytes, to implement. This is the phase working as designed, not a gap. |
| **D9 — `ScoreUi` redesign (score, `ScoreMultiplier`, `victoryPoints` win condition) does not run headless** | "the client track's call" (§ 7), explicitly recorded as a divergence rather than a defect | **OPEN, and now unowned.** `ScoreUi.cs:82-83` still carries the doc comment: *"This class still holds match state that does not run headless, and that remains V8 D9's recorded divergence; moving the state itself out of this UI [component] ..."* V10 Task 7 closed the **rendering** half only (V10 D12) — the state itself is unmoved. With the project single-owner since 899e75d, there is no client track left to make the redesign call D9 explicitly deferred to. |
| **`GameManager`'s five loose booleans** (`reverseMode`, `assaultMode`, `nightMode`, `noVehicles`, `victoryPoints`) — not a single replicable "mode" value | Recorded in `MatchStateMachine`'s class doc per D9/criterion 9, no owner named | **OPEN, unowned.** `reverseMode`/`assaultMode` are consumed once at `CapturePoint.Start()` and are covered (they set opening ownership, which the server adopts); the other three booleans have no replication path and none is proposed. Not independently re-verified in this pass beyond the phase file's own record. |
| **Elimination-by-spawn-point-loss (Task 4)** | landed in this phase | **CLOSED, not re-verified live.** Structural presence confirmed: `MatchRules.EliminationGraceSeconds` and the `SetSpawnPointCounts` shape described in Task 4 are consistent with the phase's own § 7 criterion 6 claim. Not re-run as a test here. |
| **A5 — the Unity CI gate that could never fail** | fixed in this phase, per its own § 8 | **CLOSED per the phase's own record, not independently re-verified** — this report did not re-inspect `tools/ci.ps1` for the `Start-Process -Wait -PassThru` fix. |

---

## 5. What this report does NOT claim

- It does not re-run any of the phase's fourteen tests (`ObjectiveAuthorityTests.cs` et al.) — the
  single-writer and per-tick claims above are verified structurally (grep for write sites), not by
  executing the test suite.
- It does not verify V4's or V7's status, both named as owners of open items above. Whether either has
  separately landed on this branch is unverified and out of this report's scope.
- It does not re-verify A2, A3 or A5 from the phase file's own § 8 beyond what is restated in § 4 above
  — those are the phase file's own record, cited, not independently re-derived here.

---

## 6. Next

The one item this report specifically set out to check — "rebind `_capturePoints`" — does not need a
new owner because it never described real remaining work; A1 already retired it. What remains genuinely
open (Task 6, D9's `ScoreUi` redesign) is either deliberately sequenced behind V3/V4 or a product
decision with no client track left to make it. Neither blocks V9's server-side acceptance criteria 7
and 13, both of which this phase's own § 7 names and which are unaffected by the two open items above.
