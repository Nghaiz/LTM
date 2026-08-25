# Phase 3D — Lane B: three real clients, a written programme, and an artifact per verdict

- **Track:** [`plan.md`](../plan.md) · **Parent:** [`phase-3-harness.md`](phase-3-harness.md) § 5 (task 3.3) · **Effort:** L (1wk)
- **Depends on:** [`phase-3a-player-slots.md`](phase-3a-player-slots.md) (a second player can exist), [`phase-3c-client-input.md`](phase-3c-client-input.md) (that player can fire), #151 (a client can join a server that has a secret), #123 (server and clients agree on the physics rate)
- **Unblocks:** [`phase-3e-run-and-ledger.md`](phase-3e-run-and-ledger.md)
- **Status (2026-08-25): the fight resolves. Lane B has its first kill, and eleven checks now have
  verdicts instead of one blocker.** 3F closed X-19 and handed this phase a run where the trigger
  could resolve; it still did not, and the two reasons were both in this phase's own ownership.
  **X-25** — the harness raised BOTH aim endpoints by `EYE_HEIGHT`, so it aimed level at 1.6 m,
  which is 2 cm inside the head box's lower edge at every range and straight through X-24's seam.
  **X-22 was never actually fixed** — the spawn pin was installed at `sceneLoaded`, before
  `ActorManager.StartGame()` fills the spawn-point array, so every "pinned" run logged `outside the
  scene's 0 spawn point(s)` on a six-point map and was a coin flip; `x20-occlusion-01`'s claim to a
  pinned slot is corrected in the ledger.
  With both closed, `artifacts/lane-b/x25-torso-aim-02` fires 30, hits 4, takes OBS-A 100 → 21 → 0
  and posts a killfeed line on **both** observers. Three pinned runs put all three actors on point 0,
  3 of 3.
  **The count, honestly:** 1 pass with a caveat (check 3), 4 partials, 1 flaky (check 1, **1 of 3**),
  2 blocked, 3 not graded — and **no human has watched a frame**, so checks 8 and 9 read unverdicted
  rather than passed. What remains is programme and harness work this phase owns (a vehicle set, a
  grenade step, the A16 and scene-ordering cases, X-29's two missing measurements, and a human pass),
  plus **X-27** and **X-28** before any flake rate is quoted again.
  Filed, not fixed: **X-26** (the victim's own rig bone occludes the shot — X-20's reading 2, proven
  from the collider name), **X-27**, **X-28**, **X-29**.
  Reports: [`2026-08-25-phase-3d-lane-b-verdicts.md`](../reports/2026-08-25-phase-3d-lane-b-verdicts.md).
  Prior: [`2026-08-25-x20-the-linecast-blocked-nothing.txt`](../reports/2026-08-25-x20-the-linecast-blocked-nothing.txt),
  [`2026-08-23-x19-lane-b-rerun.txt`](../reports/2026-08-23-x19-lane-b-rerun.txt).
  Original: [`2026-08-21-phase-3d-lane-b.md`](../reports/2026-08-21-phase-3d-lane-b.md).

---

## 1. Goal

Real Unity clients against one headless server, each fed a recorded input programme, capturing a
screenshot at every checkpoint the check list names. Repeatable, rather than a one-evening manual
pass.

**Three clients, not two — and only check 7 needs the third.** This file said "two" until
2026-08-20, and [`phase-3-harness.md`](phase-3-harness.md) § 2 check 7 reads *"two clients see the
same vehicle in the same place **while a third drives it**, 100 ms RTT / 5 % loss"*. The phase that
owns check 7 could not have satisfied it as written. Ten of the eleven checks run on two clients;
check 7 needs a third participant and needs it as a **driver**, not an observer — the two observers
are comparing what they see of a vehicle somebody else controls, which is the whole point of the
check. Sizing the runner for two and discovering this mid-phase is the avoidable version of this
paragraph.

Nothing else moves: `ServerPlayerSlotPool` (3A) provides sixteen, and the checks that need two
still need two.

## 2. Which checks this lane owns

Eleven of the thirteen in [`phase-3-harness.md`](phase-3-harness.md) § 2 — every row marked lane
**B**: checks 1–9, 12, 13. Checks 10 and 11 are lane A and belong to 3E.

Ledger rows: **B-1**…**B-9**, **B-13**, **B-14**.

## 3. Reuse, not new instrumentation

Prior-art check, `zero new overlays needed across Assets/Scripts/Net/`:

| Existing | Serves |
|---|---|
| `Ironfront.Net.Protocol` `JoinTicket.Issue`, via `NetClientBootstrap` | a client that can join a secret-configured server at all (#151) |
| `Assets/Scripts/Net/Shared/LocalClient.cs` | the client driver (moved out of `Net/Headless/` on 2026-08-21; it is a zero-dependency static class in the Shared assembly's own namespace, so the folder went with it) |
| `Assets/Scripts/Net/Diagnostics/VehicleReplicationOverlay.cs` | `ClientVehicleStage.DrivenStats` — checks 7, 9 |
| `Assets/Scripts/Net/Diagnostics/TransportDebugOverlay.cs` | connection / RTT state |
| `Assets/Scripts/Net/Diagnostics/MovementShadowCompare.cs` | convergence — check 8 |
| `Ironfront.Net.Transport` `NetworkSimulator` | 100 ms RTT / 5% loss — check 7 |

What is genuinely new: the scripted-input driver and the runner script.

## 3a. Cleared before this phase starts — do not re-investigate

[`phase-3c-client-input.md`](phase-3c-client-input.md)'s report handed over two blockers and a
third was found while clearing them. All three are closed; they are listed so nobody spends a day
rediscovering one.

| Was | Now | Evidence |
|---|---|---|
| **#151** — a Unity client could never join a server with a shared secret, and the log blamed a signature | The client mints a signed ticket; the server states it when the accept-unsigned flag is inert instead of ignoring it silently | [`2026-08-20-issue-151-proof.txt`](../reports/2026-08-20-issue-151-proof.txt) |
| **The harness never acked**, so every byte lane A measured was a FULL snapshot | `SyntheticClient` drives the linked `BaselineAckPolicy`; measured 1887 → 1742 B/s per client with the ack on | [`2026-08-20-loadharness-ack-proof.txt`](../reports/2026-08-20-loadharness-ack-proof.txt) |
| **The default `playerId` collided with the harness's first client** — found by the first two-client run, not by reading | Derived from the process id, above the range the harness numbers from | [`2026-08-20-client-player-id-proof.txt`](../reports/2026-08-20-client-player-id-proof.txt) |
| **#123** — a headless server ran 50 Hz physics against a rendered client's 60 | One authority scales the project setting; the server logs its rate at startup | [`2026-08-20-physics-rate-proof.txt`](../reports/2026-08-20-physics-rate-proof.txt) |

Design and reasoning: [`2026-08-20-brainstorm-unblock-3d.md`](../reports/2026-08-20-brainstorm-unblock-3d.md).

Still open and deliberately **not** this phase's: **X-8** (`Chat`, `LoadoutSelect` and `Ping` have
no client sender). No check in [`phase-3-harness.md`](phase-3-harness.md) § 2 needs any of the
three, so closing them here would be scope this phase did not buy.

## 4. Work

1. **Scripted-input driver** under `Assets/Scripts/Net/Diagnostics/` — replays a recorded programme
   through the same `MoveInput` seam a human drives, so nothing under test has a test-only path.
2. **Runner** under `tools/` — launches one headless server plus **three** clients, applies the
   `NetworkSimulator` preset, captures at checkpoints, exits non-zero on any check failing.
   Ten checks read two of the three; check 7 puts the third in the driver's seat.

   **Each client needs its own `IRONFRONT_CLIENT_PLAYER_ID`.** The server enforces one session
   per player once a shared secret is configured, so instances sharing an id have every join
   after the first rejected — reported to the client as a bare `InvalidTicket`, which reads as a
   full server and is not one. Unset now derives an id from the process id (above the range the
   load harness numbers from), so the failure is no longer automatic — but a runner that wants
   its runs replayable against fixed identities sets the variable, and sets
   `IRONFRONT_CLIENT_DISPLAY_NAME` beside it so check 1's killfeed line names something a
   reader can tell apart.
3. **Checkpoint capture** — screenshot pair for a parity check, log excerpt for a state check. The
   artifact is the deliverable; a verdict without one does not count.
4. **Seeds printed with results** — the `UnityEngine.Random` seed and the `NetworkSimulator` seed
   are two generators, and a report naming one claims reproducibility it does not have
   (`HeadlessLoadBootstrap.cs:64-71` already makes this argument for lane A).

## 5. The honesty clause is a deliverable, not a disclaimer

Checks 8 and 9 — *"no perceptible input lag"*, *"without visible snapping"*, *"breaks no cosmetic
outside the enumerated six"* — are human judgments. The harness captures the frames and the numbers;
the verdict is recorded **as a human verdict against a named artifact**. It is not laundered into a
green, and a green with no artifact is a failed row.

A flaky check is reported **flaky**. It is not re-run until it passes.

## 6. File ownership

```
Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/**     (scripted-input driver)
tools/                                                  (runner script)
plans/debt-closure/reports/                             (artifacts + phase report)
```

Does not modify shipped server or client behaviour. Per
[`phase-3-harness.md`](phase-3-harness.md) § 7, a defect found here is filed and fixed in its own
commit — never patched inside the harness. 3A exists because that rule was followed once already.

## 7. Acceptance criteria

1. The runner brings up server + three clients and exits 0 on a clean run, non-zero on any failure.
2. Each of the eleven lane-B checks has a verdict **and** a named artifact path.
3. Human-judgment verdicts are labelled as such, against their artifact.
4. Both seeds are printed with the results.
5. Nothing outside § 2's eleven checks was implemented — phase-3 AC-6.
6. Flaky checks are reported flaky, with the observed flake rate.

## 8. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Scripted clients are flaky and burn the phase | 4 | 3 | 12 | Smoke the three-client bring-up before any check; a check that will not stabilise falls back to a scripted **manual** run, recorded as such |
| A convergence check fails for a reason that is not replication | 3 | 4 | 12 | Closed before the phase starts: #123 unified the physics rate across peers, which was a live 50 Hz / 60 Hz split between a headless server and a rendered client and would have surfaced here as checks 7 and 12 failing. `MovementCore` was never exposed — it pins 30 Hz of its own — so check 8's input-lag half was never at risk |
| Human-judgment checks laundered into greens | 3 | 4 | 12 | § 5 — an unartifacted green is a failed row, graded by AC-2 |
| Scope creep into V9 (16-client load, soak, 12-vehicle) | 4 | 4 | **16** | § 2's list is the contract; AC-5 grades it |
| A defect gets patched inside the harness | 3 | 4 | 12 | § 6 ownership rule; file it, as 3A was filed |
| Editor bring-up outlasts the handshake budget | 3 | 3 | 9 | Known from #152's run — the runner waits for the tick loop, it does not race it |

## 9. Handoff

To **3E**: eleven verdicts with artifacts, ready for the ledger.
To **V9**: the driver, the runner and the report shape. V9 scales the client count and adds the
soak; it does not rebuild this.
