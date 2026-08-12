# Report — Phase 00: Freezing the protocol, the referee role, and `MovementSimulation`

- **Author:** Dev C (Replication & Simulation)
- **Date:** 2026-08-12
- **Week:** 2 / 14
- **Phase:** [phases/phase-00-foundation.md](../phases/phase-00-foundation.md)
- **Status:** ☑ **Done** — 8 of 10 criteria met outright, 2 handed to Dev A with a written
  substitute for the part that needed a meeting

---

## 1. One-paragraph summary

The protocol froze at v1.0.0 in week 1 and the conformance suite has been green since; what
remained of this phase was the half of criterion 4 waiting on a bit stream that did not exist,
and tasks 5–6, which the plan scoped against the wrong file. **Character movement is not in
`Actor.cs`** — it is in Unity Standard Assets' `FirstPersonController` under
`Assets/Plugins/Assembly-CSharp-firstpass/`, and its speed constants are not in code at all but
`[SerializeField]` values in `Assets/Prefab/Player Fps Actor.prefab`. Reading the source alone
would have produced a `MovementSimulation` whose every speed was `0f`. That finding is written up
in [`docs/movement-analysis.md`](../../../docs/movement-analysis.md) with line references, it
cancels the 60-minute walkthrough the plan asked Dev A for, and it caught a constant the plan had
invented: there is no crouch speed in this game, so the plan's assumed `CROUCH_SPEED = 2.0f` would
have made the server authoritatively slower than the client on every crouch. `BitWriter`/
`BitReader` now exist with 25 hand-written-hex conformance tests, `MovementCore` ports the real
constants with 18 tests pinning them, and the Unity-side shadow-comparison harness is written and
staged for Dev A to run.

---

## 2. Acceptance criteria review

| # | Criterion | Met | Evidence |
|---|---|---|---|
| 1 | `protocol-spec.md` frozen with 4 approvals | ☑ | v1.0.0, all 8 open questions recorded in [§ 15.1](../../00-shared/protocol-spec.md#151-questions-settled-at-the-freeze) |
| 2 | `ProtocolConstants.cs` matches the spec, self-checking test | ☑ | `tools/SpecChecker`, 27 constants, proven to fail on drift |
| 3 | ≥ 25 conformance tests green | ☑ | **283** across two projects (160 protocol + 123 replication/transport) |
| 4 | Hard-coded hex tests for `Quantize` **and `BitWriter`**, verifying Dev B's code | ☑ | **Was half-done.** `BitWriter`/`BitReader` written, 25 tests in `Conformance/BitStreamTests.cs`, expected bytes hand-derived from the spec |
| 5 | `PackPos` round-trip error < 0.07 m across the range | ☑ | Worst observed **0.0625 m** swept at 0.37 m steps across ±2048 (`MeasurementReportTests`) |
| 6 | `InputSerializer` rejects truncated packets and malicious `frameCount` | ☑ | `ClientInputMessage.TryParse` rejects 0, 9, 255 and truncated bodies |
| 7 | **The 4-question note on movement** | ☑ | [`docs/movement-analysis.md`](../../../docs/movement-analysis.md) — 8 sections, every claim carries a `file:line` |
| 8 | **`MovementSimulation` runs as a shadow with comparison logging** | ☑ **code** / ☐ **playtest** | `MovementShadowCompare.cs` written and staged. Plan requires "steps 1–2 finished"; the playtest is step 3 and needs the Editor → **A3** |
| 9 | The request has been sent to Dev A | ☑ | [`handoff/dev-a-checklist.md`](../handoff/dev-a-checklist.md) — 8 items, ~2h15m, one genuine decision |
| 10 | 60-minute `Actor.cs` session with A | ☐ **cancelled, deliberately** | The session was to explain movement code `Actor.cs` does not contain. Replaced by the written analysis; A is asked to review it instead (**A8**, 10 min vs 60) |

**8 met, 1 met in code and awaiting a playtest, 1 cancelled with a substitute.** Nothing is
silently outstanding.

---

## 3. Bandwidth budget — measured

Not applicable to phase 00 (no snapshots yet). Measured in the
[phase-01 report](2026-08-12-phase-01-snapshot.md#3-bandwidth-budget--measured).

---

## 4. Server CPU budget

Not applicable to phase 00. The tick-time instrumentation that will answer it
(`TickTimeStats`, p50/p99 nearest-rank) shipped with phase 01.

---

## 5. Test results

```
Passed!  - Failed: 0, Passed: 160, Skipped: 0, Total: 160 - Ironfront.Net.Protocol.Tests.dll
Passed!  - Failed: 0, Passed: 123, Skipped: 0, Total: 123 - Ironfront.Net.Replication.Tests.dll
Build succeeded. 0 Warning(s), 0 Error(s)   (TreatWarningsAsErrors=true)
```

| Group | Tests | Pass | Fail |
|---|---|---|---|
| Bit packing (`BitStreamTests`) — **verifies Dev B** | 25 | 25 | 0 |
| Quantization (`QuantizeTests`, protocol suite) | 18 | 18 | 0 |
| Conformance, protocol referee (whole protocol suite) | 160 | 160 | 0 |
| Movement port (`MovementCoreTests`) | 18 | 18 | 0 |
| Network simulator (`NetworkSimulatorTests`) | 24 | 24 | 0 |
| Delta encoding | 16 | 16 | 0 |
| Interest management | 0 | — | — | *(phase 02)* |
| Lag compensation | 0 | — | — | *(phase 02)* |

---

## 6. Technical decisions

| # | Problem | Chosen | Rejected | Reason |
|---|---|---|---|---|
| C-00-1 | Phase-01's snapshot sketch bit-packs health into 7 bits; the frozen spec § 4.3 says `u8` | **Follow the frozen spec.** Byte-aligned, `BitWriter` ships as a general utility, not used by the snapshot codec | Bit-pack per the sketch | The spec is frozen; changing the wire format needs a PR, 2 approvals and a `PROTOCOL_VERSION` bump. The sketch predates the freeze. Saving would have been 1.25 B/actor |
| C-00-2 | Movement constants are `[SerializeField]`, so source reads `0f` | Read the prefab YAML, hard-code with `file:line` provenance, pin with a test that fails if the prefab changes | Guess plausible values; ask A to read them out | Guessing is what produced the phantom `CROUCH_SPEED`. Prefabs are force-text and readable without the Editor |
| C-00-3 | The plan's assumed `CROUCH_SPEED = 2.0f` does not exist | **No crouch speed.** Crouch changes collider height only | Implement 2.0 as planned | `FpsActorController.StartCrouch` only sets `height`; speed selection has two branches on the sprint flag. Implementing it would rubber-band every crouching player |
| C-00-4 | Movement maths must be unit-testable, but `MovementSimulation` needs `UnityEngine` | Split: `MovementCore` (engine-free, netstandard2.1, tested) + a thin Unity adapter with no logic | Put everything in the Unity file | An engine-free core is testable in CI. 18 tests replace most of what a playtest would have caught |
| C-00-5 | Six new members requested on `Actor.cs`, which does not own movement | One new `NetMovementAgent` component on the `CharacterController` | Six pass-throughs on A's 1188-line file | Smaller ask, no edits to A's file, cannot regress gameplay because nothing calls it until wired |
| C-00-6 | The Unity scripts need a DLL that is build output and not committed | Stage them in `handoff/unity-dropin/` with a copy step | Commit them straight into `Assets/` | Anyone pulling before running `build-libs.ps1` would open the Editor to a project that will not compile. Only A opens the Editor, so that would be A's afternoon |
| C-00-7 | `C_ACK_BASELINE` (0x27) is in the message table with no byte layout | Implement `u32 baselineTick`, flag it in XML docs and here as owing a spec section | Block phase-01; invent a richer format | Matches the width `baselineTick` already has in the snapshot header. Documents an unspecified message rather than changing a specified one, so no `PROTOCOL_VERSION` bump |

---

## 7. Things tried that FAILED

| Tried | Why it didn't work | Signs |
|---|---|---|
| Reading `Actor.cs` end-to-end for the movement simulation, as the plan directs | `Actor.cs` never moves the character — it reads `controller.Velocity()` and drives animation and ragdoll from it. Two of the plan's four questions cannot be answered from that file at all | `Actor.cs:528` asks the controller; `Actor.FixedUpdate` returns immediately while the ragdoll is inactive, which is the whole time a player is alive and upright |
| Deriving the speed constants from source | Every one is `[SerializeField]` with no initialiser, so the source value is `0f`. A simulation built from it produces a character that cannot move | Five fields declared at `FirstPersonController.cs:21-38`, assigned nowhere |
| `p99` asserted as the single worst sample in a 100-sample window | Nearest-rank p99 of 100 samples is the 99th, not the 100th — and that is correct. "p99 < 33 ms" claims 99% of ticks were under budget; one bad tick in a hundred does not violate it | Test expected 300 ms, got 8 ms. Kept **both** cases as tests so the definition is pinned rather than re-argued |
| `ReorderPercent = 100` to force reordering in a test | Reordering is an extra delay applied to the *chosen* packets, so choosing all of them shifts the stream uniformly and preserves order exactly. The test measured the opposite of its intent | `StaleDroppedCount == 0` with 100% reorder configured. Now pinned as a test **and** documented on `SimulatorConfig.ReorderPercent`, because A and B will meet it too |
| One `NetworkSimulator` shared by both loopback directions | `Flush` releases every due packet and returns its buffer, so flushing client→server consumed the server→client packets that happened to be due and dropped them — direction-dependent loss nobody configured | Caught by reasoning before it reached a test; `FlushingOneDirectionDoesNotConsumeTheOther` is the regression guard |

---

## 8. Blocked / needs someone else

| What's blocking | Who's needed | Reported yet |
|---|---|---|
| Shadow-comparison playtest (criterion 8, step 3) | Dev A — Editor only | ☑ **A3** |
| Fixed timestep is 0.02 (50 Hz) against `SIM_TICK_RATE` 30 | Dev A — project-wide call | ☑ **A5**, with three costed options and a recommendation |
| Stable weapon id registry for the snapshot `weaponId` field | Dev A | ☑ **A6**. Until then snapshots ship `weaponId = 0` |
| Confirm no reachable position past ±2048 m | Dev A | ☑ **A7**. Measured from scene YAML already; only "can a player physically get there" is open |

Nothing is blocked on Dev B or Dev D. `BitWriter`/`BitReader` were B's to write and were landed
here to unblock the referee role; the implementer/verifier split is preserved — the conformance
tests were written from the spec, not from the implementation, and B should review both.

---

## 9. Next phase

- **First task:** phase 01 is already largely done — see the
  [phase-01 report](2026-08-12-phase-01-snapshot.md). What remains there is Unity-side
  (`ServerTickLoop` MonoBehaviour, script execution order, two clients in sync) and gated on
  **A1–A5**.
- **Risks I can see coming:**
  - **The fixed-timestep mismatch (A5) is the live one.** Prediction and authority stepping
    different `dt` values is a divergence that appears only while airborne and only under load,
    which is the worst possible signature.
  - Weapon ids (A6) are a small task that will become annoying if it slips past interest
    management, because the snapshot field is inert until it lands.
  - The shadow comparison may find slope divergence larger than expected. That gap is documented
    and structural (no collision query in a netstandard library) — if it turns out to matter, the
    fix is to feed the ground normal in as an input rather than to move the simulation into Unity.
