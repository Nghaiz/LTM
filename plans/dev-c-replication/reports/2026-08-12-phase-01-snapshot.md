# Report — Phase 01: Snapshots, deltas, and the server loop

- **Author:** Dev C (Replication & Simulation)
- **Date:** 2026-08-12
- **Week:** 2 / 14 *(phase 01 is scheduled for weeks 3–6; the engine-free half was pulled forward
  because it does not need the Editor and Dev A was the critical path)*
- **Phase:** [phases/phase-01-snapshot.md](../phases/phase-01-snapshot.md)
- **Status:** ☑ **Partially done** — 6 of 10 M1 criteria met and green; the remaining 4 need Unity

---

## 1. One-paragraph summary

Everything in M1 that does not require the Editor is written, measured and merged: the tick
scheduler, authoritative input handling with its three anti-cheat checks, full snapshots, delta
encoding against acked baselines, and an end-to-end integration test that runs a server, Dev B's
`LoopbackTransport` and a fake client with real framing and real packet loss. Deltas measure a
**44.7% saving** against full snapshots and **10.94 KB/s per client** at 48 actors — inside the
35% and 12 KB/s budgets, before interest management. The two traps the phase document warns about
are closed structurally rather than by discipline: `WorldSnapshot` stores already-quantized
entries, so change detection *cannot* compare raw floats (trap 4), and `DeltaDecoder` starts each
entry as a copy of its baseline rather than a fresh struct, so an omitted field is inherited
rather than zeroed (trap 5). The Unity half has since landed too: `Assets/Scripts/Net/Server/`
holds the bootstrap, the tick loop and the two ordering stages that straddle Unity's simulation,
with the −200 / +200 split declared in `[DefaultExecutionOrder]` so Dev A's project settings are
untouched. What is left is not code: **criteria 1 and 9 need somebody to press Play and read the
Profiler** (checklist S4), and **criterion 7 is blocked on Dev B, not on the Editor** —
`LoopbackTransport` is in-process and reaches exactly one client, so two clients waits on the UDP
transport.

---

## 2. Acceptance criteria review (M1)

| # | Criterion | Met | Evidence |
|---|---|---|---|
| 1 | Steady 30 Hz tick with 48 actors, p99 < 33 ms | ☑ **pacing** / ☐ **under real load** | `ServerTickScheduler` holds 30 Hz on a steady clock and clamps a 2 s stall to 3 ticks instead of spiralling. `TickTimeStats` gives nearest-rank p50/p99. The Unity loop that puts physics + AI inside the sample now exists; reading p99 off it is checklist **S4** |
| 2 | Full snapshots round-trip bit-for-bit | ☑ | `FullSnapshotRoundTripsEveryField` — 48 actors, every field compared at the quantized level |
| 3 | Deltas with 20% packet loss end in a matching state | ☑ | `DeltasSurvive20PercentPacketLoss` — 1000 ticks × **4 seeds** (42, 1337, 20260812, 7), exact equality, not a tolerance |
| 4 | Deltas save ≥ 35% vs full | ☑ | **44.7%** measured over 595 snapshots / 30 s at 48 actors |
| 5 | Speed hacks blocked (`moveX = moveZ = 127`) | ☑ | `ASpeedHackingClientIsClampedByTheServer` — a fake client sending the raw i8 maximum on both axes covers no more ground than an honest sprinter |
| 6 | 3 ticks of missing input → still moves smoothly | ☑ | `ThreeTicksOfMissingInputKeepTheCharacterMoving` — coasts exactly 3 ticks, then stops rather than running to the horizon |
| 7 | 2 Unity clients see each other in sync | ☐ **blocked on Dev B** | `LoopbackTransport` is in-process — one Editor, one client, never a second process. Two clients needs the UDP transport. The offline equivalent passes: `SnapshotFlowIntegrationTests` runs server + loopback + fake client at lan/typical/bad and converges exactly |
| 8 | Bandwidth ≤ 12 KB/s/client | ☑ | **10.94 KB/s** including the GSP header and payload framing |
| 9 | 0 allocations per tick on the server | ☑ **by construction** / ☐ **profiled** | Fixed-size rings, pre-allocated buffers, per-player delegates cached in the constructor; no LINQ, no per-tick `new`. The Profiler read is checklist **S4** |
| 10 | ≥ 45 tests, all green | ☑ | **297** total; **137** in the replication/transport suite |

**6 met, 3 met in code and awaiting an Editor run, 1 blocked on Dev B.**

---

## 3. Bandwidth budget — measured

Reproducible via `MeasurementReportTests.PrintTheSnapshotAndBandwidthTable`.

| Component | Budget | Measured | Over? |
|---|---|---|---|
| Snapshots / client | 4.8 KB/s | **10.94 KB/s** | ⚠ **yes — expected at this stage** |
| Events / client | 1.5 KB/s | not yet implemented | — |
| **Total down / client** | **8 KB/s** | **10.94 KB/s** | ⚠ |
| Up / client (input) | 0.87 KB/s | **0.85 KB/s** (29 B × 30 Hz) | ☑ |
| Server total (16 clients) | 109 KB/s | ~175 KB/s projected | ⚠ |

**Measurement conditions:** 48 actors, all visible, 20 Hz, 30 s, mixed movement (60% running in a
straight line, 20% manoeuvring, 20% stationary), including the 16-byte GSP header and 6 bytes of
payload framing per snapshot.

**On being over the `plan.md § 10` snapshot budget.** The 4.8 KB/s line assumes interest
management, which is phase 02 — it is what trims the set from 48 actors to the ~20 a client can
actually see. Against the number this phase is actually graded on, criterion 8's **12 KB/s
before interest management**, 10.94 KB/s passes with headroom. Projecting the phase-02 cull gives
roughly 4.5–6 KB/s, inside § 10. No action taken; re-measure after interest management and escalate
then if it does not land.

**Per-actor cost**, matching the spec § 4.3 estimate exactly:

| Case | Bytes/actor |
|---|---|
| Full (every v1 field) | 20 |
| Typical delta (position + rotation) | 12 |
| Stationary actor | 3 |

| Snapshot size | Value |
|---|---|
| Full, 48 actors | 973 B |
| Full, 64 actors (join) | 1293 B — **over the 1184 B payload limit, fragments as the spec predicts** |
| Mean delta, 48 actors | 537.9 B |
| Smallest / largest delta observed | 529 B / 543 B |
| Full snapshots sent in 600 | **1** (the warm-up) |

---

## 4. Server CPU budget

| Metric | Threshold | Measured |
|---|---|---|
| Time per tick (avg) | < 20 ms | **not measurable yet** — needs Unity physics + AI in the loop |
| Time per tick (p99) | < 33 ms | **not measurable yet** |
| Of which: applying input | | instrumented, `TickTimeStats` |
| Of which: Unity sim (physics + AI) | | needs headless build |
| Of which: building snapshots | | 973 B full / 538 B delta encode, sub-millisecond |
| Of which: interest management | | phase 02 |
| Of which: hitbox history | | phase 02 |
| Alloc/tick | 0 B | **0 by construction**, not yet profiler-confirmed |

Being honest about this table: the encoder and the input path allocate nothing after
construction — fixed rings, pre-allocated `WorldSnapshot` history, no LINQ, no per-tick `new`.
But "0 allocations" as an *acceptance criterion* means measured in the Unity Profiler with the
real server running, and that is criterion 9, which is blocked on the Editor. The design supports
it; nobody has yet watched it.

---

## 5. Test results

```
Passed!  - Failed: 0, Passed: 160, Skipped: 0, Total: 160 - Ironfront.Net.Protocol.Tests.dll
Passed!  - Failed: 0, Passed: 137, Skipped: 0, Total: 137 - Ironfront.Net.Replication.Tests.dll
Build succeeded. 0 Warning(s), 0 Error(s)   (TreatWarningsAsErrors=true)
```

| Group | Tests | Pass | Fail |
|---|---|---|---|
| Server messaging (`ServerMessagingTests`) — routing + framing | 14 | 14 | 0 |
| Bit packing (`BitStreamTests`, verifies Dev B) | 25 | 25 | 0 |
| Network simulator (`NetworkSimulatorTests`) | 24 | 24 | 0 |
| Server authority + tick pacing (`ServerAuthorityTests`) | 20 | 20 | 0 |
| Movement port (`MovementCoreTests`) | 18 | 18 | 0 |
| Snapshot + delta (`SnapshotAndDeltaTests`) | 16 | 16 | 0 |
| Loopback transport (`LoopbackTransportTests`) | 12 | 12 | 0 |
| End-to-end integration (`SnapshotFlowIntegrationTests`) | 5 | 5 | 0 |
| Measurement report (`MeasurementReportTests`) | 3 | 3 | 0 |
| Protocol conformance (existing suite) | 160 | 160 | 0 |
| Interest management | 0 | — | — | *(phase 02)* |
| Lag compensation | 0 | — | — | *(phase 02)* |

---

## 6. Technical decisions

| # | Problem | Chosen | Rejected | Reason |
|---|---|---|---|---|
| C-01-1 | Delta against what? | **The client's acked tick** (C-AD-1), 32-snapshot history per client | The previous snapshot | Deltaing against N-1 makes one lost packet corrupt every snapshot after it. Against an acked tick, a lost snapshot costs exactly that snapshot. Cost is ~42 KB per client of history |
| C-01-2 | Trap 4 — comparing raw floats defeats delta encoding silently | `WorldSnapshot` stores **already-quantized** `ActorSnapshotEntry` | Store floats, remember to quantize before comparing | Makes the bug unwriteable rather than merely documented. Storing floats would compile, run, produce correct output and save nothing — with no symptom but a disappointing report number |
| C-01-3 | Trap 5 — an omitted delta field means "unchanged", not "zero" | `DeltaDecoder` seeds each entry from its baseline and overwrites only what the mask carries | Build a fresh entry and fill in the advertised fields | The rejected form is the natural way to write the loop, and teleports any actor that stopped moving to the world origin |
| C-01-4 | New actor absent from the baseline | Send `FullNoSeat` (bits 0–6) | `0xFF` as the phase sketch shows | `0xFF` also claims a `seatInfo` field v1 never populates — 3 junk bytes per new actor |
| C-01-5 | Tick comparisons are u32; `SequenceMath` only covered u16 | Added `IsNewer32`/`Distance32`, route every tick comparison through them | Raw `>` — u32 at 30 Hz takes 4.5 years to wrap | Wrapping is not the risk; unsigned subtraction is. `Distance32` turns "the ack is ahead of us" into a negative that fails a range check instead of a 4-billion distance that passes every check |
| C-01-6 | `ServerTickLoop` is a MonoBehaviour, so untestable in CI | Split: `ServerTickScheduler` (engine-free, tested) + a thin Unity wrapper later | Write it as a MonoBehaviour now | "Does the server hold 30 Hz" becomes answerable without a headless build. The Unity wrapper still needs the two-MonoBehaviour split for script execution order (phase trap 1) |
| C-01-7 | The diagonal speed exploit is already dead in `MovementCore` | Keep the explicit normalize in `InputAuthority` anyway | Rely on the port's normalize | Relying on a side effect of the movement port to be the anti-cheat means the hole reopens quietly the day someone restores the slope projection |
| C-01-8 | Speed clamp derived from horizontal speed fires on every jump | Clamp on the combined horizontal + vertical bound, ×1.3 | Horizontal only | A clamp that drags players back down through their own jump arc is worse than the cheat it prevents. `AJumpIsNotMistakenForASpeedHack` pins it |
| C-01-9 | Loopback needs the same simulator for both directions | One simulator pair **per direction** | One shared pair | `Flush` releases every due packet and returns its buffer, so a shared instance lets one direction's flush consume and drop the other's packets |
| C-01-10 | The Unity loop is a MonoBehaviour, so nothing in it is reachable from CI | Push every decision out of it: `ServerMessageRouter` (inbound decode) and `ServerPayloadWriter` (outbound framing) are engine-free and tested; the MonoBehaviour is wiring | Decode and frame inside `ServerTickLoop` | The identical decode already existed inline in the integration test's fake server, which meant the code the real server would run existed only in a test. Extracting it deletes that copy and puts the end-to-end scenario on the shipped path |
| C-01-11 | Trap 1 wants two execution orders, which the phase document assigns to `ProjectSettings` | `[DefaultExecutionOrder]` on the three components | Ask Dev A to edit `ProjectSettings/ScriptExecutionOrder` | A cross-owner dependency for a value that never changes. The attribute is visible in a diff, travels with the file, and cannot be silently absent |
| C-01-12 | Task 1's sketch sets `Time.fixedDeltaTime = 1f / SIM_TICK_RATE` in `Awake` | **Do not set it.** The scheduler is fed the wall clock and reports how many 30 Hz ticks are owed | Force the physics rate to 1/30 | `IngameMenuUi.cs:29` and `FpsActorController.cs:497` both assign `Time.timeScale / 60f` at runtime, so the assignment would be overwritten before the first physics step. It would also contradict A5 option B, which deliberately left the physics rate alone |
| C-01-13 | `WriteSnapshot` could discover the buffer was too small only after encoding | Validate the destination **before** calling the encoder | Encode, then check | `DeltaEncoder.Write` files the snapshot into its baseline history as a side effect of succeeding. Failing afterwards leaves the server believing it sent a snapshot the client never saw, and a later ack then selects a baseline the two sides do not share — pinned by `ADestinationTooSmallLeavesTheEncoderHistoryUntouched` |

---

## 7. Things tried that FAILED

| Tried | Why it didn't work | Signs |
|---|---|---|
| Bit-packing the snapshot per the phase-01 sketch (`health` 7 bits, `weapon` 5, `ammo` 8, `team` 2) | The spec froze at v1.0.0 with those fields byte-aligned (§ 4.3), and the sketch predates the freeze. Shipping it would have been an unannounced wire-format change | Would have saved 1.25 B/actor ≈ 1.2 KB/s. Not worth a `PROTOCOL_VERSION` bump mid-milestone. `BitWriter` still shipped, as a general utility with its conformance suite |
| A single `NetworkSimulator` for both loopback directions | Flushing one direction consumed the other's due packets and returned their buffers — silent, direction-dependent loss appearing only when both directions were busy | Caught by reading `Flush` rather than by a failing test. `FlushingOneDirectionDoesNotConsumeTheOther` now guards it |
| `ReorderPercent = 100` in the stale-packet test | Reordering is an extra delay on the chosen packets; choosing all of them shifts the stream uniformly and reorders nothing | `StaleDroppedCount == 0` with reordering "at maximum". Now two tests and a documented remark on the field |
| Asserting p99 of a 100-sample window equals the single worst sample | Nearest-rank is correct: p99 of 100 is the 99th. "p99 < 33 ms" claims 99% of ticks were under budget, and one hitch in a hundred does not violate that | Expected 300 ms, got 8 ms. Both cases kept as tests so the definition is pinned rather than re-argued next time it surprises someone |
| Testing the malicious `frameCount = 255` with a body honestly sized for 255 frames | 255 frames is 2045 bytes and does not fit a 1184-byte datagram at all, so the fixture failed while building the packet rather than while parsing it — it was testing an attack that cannot be delivered | The real shape is a 29-byte packet *claiming* 255 frames, betting the server sizes a buffer from the claim before checking it. Now three cases: honest-length 0, honest-length 9, and the small-body 255 |
| Sizing the integration test's snapshot body buffer to `MAX_PAYLOAD` | A body allowed to fill the whole datagram leaves no room for the 6 bytes of framing around it. Latent, never fired: a 48-actor snapshot is 973 B, so nothing ever reached the limit | Found while extracting `ServerPayloadWriter`, whose `MaxSnapshotBodySize` makes the budget explicit. Had it fired, the symptom would have been snapshots silently not sent |

---

## 8. Blocked / needs someone else

| What's blocking | Who's needed | Reported yet |
|---|---|---|
| Criteria 1 & 9 under real load — tick time p99 and 0 alloc/tick with Unity physics + AI | Dev A — Editor only | ☑ checklist **S1–S4**. The Unity loop now exists; what is left is pressing Play and reading the Profiler |
| Criterion 7 — two Unity clients in sync | **Dev B — UDP transport** | `LoopbackTransport` is in-process and reaches exactly one client. Not an Editor problem and not Dev A's to unblock |
| ~~`ServerTickLoop` script execution order (trap 1)~~ | ~~Dev A owns project settings~~ | ✅ **Closed without asking.** Declared in `[DefaultExecutionOrder]` on `NetServerBootstrap` (−1000), `ServerInputStage` (−200) and `ServerSnapshotStage` (+200), so `ProjectSettings` is untouched |
| ~~Fixed timestep 0.02 vs `SIM_TICK_RATE` 30~~ | Dev A | ✅ **A5 answered: B** — `NetPredictionClock` steps the simulation at 1/30 from `Update`, independent of the physics rate |
| Stable weapon ids | Dev A | ☑ **A6** — `weaponId` ships as 0 until then |
| `C_ACK_BASELINE` has no byte layout in the spec | All 4, PR + 2 approvals | Implemented as `u32 baselineTick` and flagged in code and in both reports. No `PROTOCOL_VERSION` bump — it documents an unspecified message rather than changing a specified one |

---

## 9. Next phase

- **First task:** phase 02 — lag compensation. `HitboxHistory` and the rewind formula are
  engine-free enough to build and test the same way this phase was, ahead of schedule, while
  Dev A works the checklist.
- **Risks I can see coming:**
  - **Bandwidth is over `plan.md § 10` and stays over until interest management lands.** It is
    inside the criterion this phase is graded on, but the § 10 number is the one that matters at
    M3, and phase 02's optimisation ("only keep history for actors that could actually be shot")
    depends on the same visibility set. Interest management is now on two critical paths.
  - ~~**The fixed-timestep decision (A5) is the highest-value open item.**~~ **Answered: B.**
    `NetPredictionClock` steps the simulation at exactly 1/30 from `Update`, so the physics rate
    is now irrelevant to the netcode — which is the only arrangement that survives
    `IngameMenuUi.cs:29` and `FpsActorController.cs:497` assigning `Time.fixedDeltaTime` at
    runtime. Full account in the phase-00 report § 9.
  - Criterion 9 (0 alloc/tick) is designed for but still unmeasured. The wrapper now exists and
    was written for it — fixed rings, pre-allocated buffers, per-player delegates built once in
    the constructor rather than as lambdas at the call site, a `List` indexed by `int` instead of
    a `Dictionary` enumeration. Every one of those is a claim until the Profiler agrees (**S4**).
  - **Criterion 7 moved off Dev A and onto Dev B, and that is worth saying out loud.** It was
    filed as "needs Editor" on the assumption that two Editor clients could talk to each other.
    They cannot: `LoopbackTransport` is in-process by construction. Nothing Dev A does closes it,
    so chasing it through the Editor checklist would have burned a round finding that out.
