# Debt-closure track — everything V0–V10 left open, except V9

- **Created:** 2026-08-19 · **Branch base:** `develop` · **Owner:** single-owner project (`899e75d`)
- **Design of record:** [`plans/reports/2026-08-19-v0-v10-debt-closure-brainstorm.md`](../reports/2026-08-19-v0-v10-debt-closure-brainstorm.md)
- **Supersedes:** [`plans/replication/integration-checklist.md`](../replication/integration-checklist.md) round 8 (2026-08-16, stale — predates four merges)

---

## 1. Why this track exists

Eleven phases merged between 2026-08-13 and 2026-08-19. Each closed with an honest STILL-OPEN
table and handed its residue to a track — "the client track", "V4", "V7", "a client-flow phase" —
that stopped existing when the project went single-owner. Roughly forty items are now open,
ownerless, spread across five documents, and an unknown number of them have already closed
silently under a later merge.

Two are load-bearing:

1. **`NetClientProjectilePresenter._prefabsByKind` is unauthored.** Until it is filled, no
   replicated projectile renders and six of V7's thirteen acceptance criteria cannot be met.
2. **`ServerProjectileBridge.AuthoritativeFlight` defaults off** because the Unity server already
   simulates every projectile it spawns and applies its damage through `Hitbox.ProjectileHit` /
   `ActorManager.Explode`. Running both would apply every damage number twice. Turning the flag on
   is not a config change — its first task is deleting the engine-side damage call.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **P-D1** | **Ledger-first sequencing.** Phase 0 re-verifies every OPEN row before any work is specified against it. Several rows are probably already closed and re-doing them is the expensive failure. |
| **P-D2** | **The cutover is prepared, not enabled.** Phase 2 writes the delete-path for the engine-side damage call behind the existing flag with a double-damage test; `AuthoritativeFlight` stays default off until Phase 5 holds a harness and real numbers. |
| **P-D3** | **A minimal two-client harness is built now**, as a deliberate slice of V9 Task 1. V9 inherits it rather than rebuilding it. Scope is locked by a fixed check list, not by a tool boundary. |
| **P-D4** | **Four product items are in scope:** `PlayerList` → named killfeed, an owner for `ClientCombatState`, `ScoreUi` state extraction (V8 D9), and the cosmetic backlog (capture-point minimap marker, scorch `DecalType`, per-bone ragdoll force). |
| **P-D5** | **Author, then pin.** Every Editor-authoring task ships a gate that fails when the authoring is undone. Authoring without pinning is what turned group A into ownerless debt in the first place. |
| **P-D6** | **The gates live in `tools/ClientWiringGate`.** Prefab-YAML detectors join the existing source detectors: one gate, one SSOT, exit code 2 already reserved for "the gate could not tell". EditMode tests cannot do this job — see § 3. |
| **P-D7** | **The observational half of group B gets a scripted rendered-client path**, not a manual checklist. Costlier and deeper into V9 than a manual run, and repeatable forever. |
| **P-D8** | **`PlayerList` does not bump `PROTOCOL_VERSION`.** Opcode `0x4B` is already declared in the enum; adding its struct fills a reserved slot and changes no existing message layout — the same reasoning V6-D8 used for `CAR_HORN`. It is a shared-file PR against `protocol-spec.md` § 5, not a version event. |
| **P-D9** | **V7's ten unwritten Unity tests are recorded won't-do, with the reason.** They exercise `MonoBehaviour` behaviour in `Assembly-CSharp`, which no test assembly may reference (§ 3); the arithmetic they cover is already pinned at the library level. |
| **P-D10** | **Out of scope, stated:** grenades and deployables are never ballistically stepped (pinned deliberate by `ABouncingOrRigidbodyProjectileIsNotBallisticallyStepped`); `GameManager`'s five loose booleans; V9 proper. |
| **P-D11** | **A wreck damages.** `Vehicle.Explode()` gains its `ActorManager.Explode` call with `ExplosionKind.Vehicle`. V1-D5 handed this to V4 as a gameplay decision and V4 did not take it; taken here. Cover behind a burning vehicle becomes dangerous, which is the intended consequence. |
| **P-D12** | **`ExplosionKind.Environment` gets a source.** An `ExplosiveProp` component lets a scene fuel drum or gas cylinder detonate through the same server-authoritative path as every other explosion. This is a small feature rather than a debt repayment, and Phase 2 sizes it as one. |

---

## 3. Prior art — what already exists (searched: `Ironfront_Reborn/Assets/`, `tools/`, `Ironfront.Net.*/`, `plans/`)

**Two CI gates already do this shape of work, without Unity.**
`tools/SpecChecker/Program.cs:173-206` parses `Ironfront_Reborn/Assets/Resources/_Managers.prefab`
by shape and fails the build when the serialized weapon registry disagrees with `WeaponIds`.
`tools/ClientWiringGate` fails the build when a `ClientMessageRouter` event loses its last
production subscriber, and reserves exit code 2 for "the gate could not tell" — deliberately
distinct from 0, so an empty scan never reads as a pass. Prefab presence gates therefore need no
Unity licence and no EditMode assembly.

**EditMode tests cannot reach the authoring.**
`Ironfront_Reborn/Assets/Tests/EditMode/Ironfront.Net.Unity.Server.Tests.asmdef` references only
`Ironfront.Net.Unity.Server` and `Ironfront.Net.Unity.Shared`. `Assets/Scripts/Net/Client/`
carries **no asmdef**, so `NetClientProjectilePresenter` and `RemoteActorRegistry` compile into
`Assembly-CSharp`, as does `ScoreUi` — and `Assembly-CSharp` is a predefined assembly no asmdef
may name (the constraint V6 already recorded). This is why P-D6 and P-D9 are what they are.

**The harness has partial prior art.**
`Ironfront.Tools.LoadTest/` is N synthetic clients against the **master** server over MSP, with
`LatencyRecorder`, `MetricsSampler` and a JSON `LoadTestReport` — the right shape, the wrong
target. `Assets/Scripts/Net/Headless/LocalClient.cs` and
`Assets/Scripts/Net/Diagnostics/{VehicleReplicationOverlay,TransportDebugOverlay,MovementShadowCompare}.cs`
exist and are reusable. Zero two-process game-server harness exists across `tools/`,
`Ironfront.*/` and `Assets/Scripts/` — that part is genuinely new, and V9 Task 1 is its design.

**`ClientCombatState` exists** at `Ironfront.Net.Replication/Client/ClientCombatState.cs` — the gap
is an owner, not the type.

---

## 4. Phases

| Phase | File | Goal | Effort |
|---|---|---|---|
| 0 | `phase-0-ledger.md` ([index](phases/README.md)) | One evidence-backed ledger replaces five sources of truth | S (1d) |
| 1 | `phase-1-authoring.md` ([index](phases/README.md)) | Group A authored **and** pinned by gates that can fail | M (3d) |
| 2 | `phase-2-code.md` ([index](phases/README.md)) | Four product items, ledger cleanups, cutover prepared | L (1wk) |
| 3 | `phase-3-harness.md` ([index](phases/README.md)) | Two-process harness + scripted rendered clients | L (1.5wk) |
| 3A | `phase-3a-player-slots.md` ([index](phases/README.md)) | The server admits `MaxConnections` players, not one | M (3d) |
| 3B | `phase-3b-handshake-residual.md` ([index](phases/README.md)) | Account for `BadSignature`, correct #151 and the proof report | S (1d) |
| 3C | `phase-3c-client-input.md` ([index](phases/README.md)) | Fire / Aim / Reload and `C_ACK_BASELINE` reach the wire (**X-3**) | M (3d) |
| 3D | `phase-3d-lane-b.md` ([index](phases/README.md)) | Lane B — two scripted rendered clients, an artifact per checkpoint | L (1wk) |
| 3E | `phase-3e-run-and-ledger.md` ([index](phases/README.md)) | Thirteen verdicts, and the ledger rows #150/#152 left standing | M (3d) |
| **3F** | `phase-3f-x19-drawn-vs-held.md` ([index](phases/README.md)) | **X-19 — the client draws a body 0.332 m below the one the server holds, and every shot passes over. Blocks 16 of the 28 open rows** | S–M (1–2d) |
| 4 | `phase-4-measure.md` ([index](phases/README.md)) | Bandwidth, tick p99, per-weapon release delay verified | M (3d) |
| 5 | `phase-5-cutover-gate.md` ([index](phases/README.md)) | `AuthoritativeFlight` on with proof, or off with a reason | S (1d) |
| **6** | `phase-6-rows-no-run-closes.md` ([index](phases/README.md)) | The five rows no acceptance run can close — D-1, E-6, X-6, X-7, X-8 — plus group A's residue | M (3d) |
| **7** | `phase-7-ops-to-digest.md` ([index](phases/README.md)) | The server shipped as far as a digest; E-3 corrected; three dead limits retired from the docs | S (1d) |
| **8** | `phase-8-hygiene.md` ([index](phases/README.md)) | Nine stale branches, one of them an unmerged PR; a roll-up recomputed rather than decremented | XS |

**Phase 3 is split.** Tasks 3.1/3.2 landed in #150/#152; acceptance criterion 1 stayed red on a
defect that was never a handshake defect. 3A–3E carry the phase to its acceptance criteria —
ordering `3A → 3B? → 3C → 3D → 3E`, where 3B runs only if `--smoke` is still red after 3A. Design and
root cause: [`2026-08-20-brainstorm-phase-3-completion.md`](reports/2026-08-20-brainstorm-phase-3-completion.md).

**Critical path:** 0 → 1 → 3 → 4 → 5. Phase 2 runs parallel to 1 and 3 with **one hard ordering
constraint**: task 2c (extract `ScoreUi` state) lands before task 1.6 authors `ScoreUi`'s text refs,
or the authoring targets fields the refactor is about to move.

**Total: ~4 weeks.**

### 4a. Where the track stands — CLOSED 2026-08-26

**All nine phases are merged.** 0, 1, 2, 3A, 3B, 3C, 3D, 3E, 3F, 4, 5, 6, 7 and 8, in that order of
landing. Every acceptance criterion is graded in its own report, and the three that were not met say
so there rather than here:

| Phase | Criteria | Not met, and why |
|---|---|---|
| **3E** | 6 | AC-1 **PARTIAL** — 11 of 13 checks have an artifact; checks 6 and 10 have none because no programme provokes the case (**X-37**) and nothing samples an allocator (**X-33**). AC-6 **PARTIAL** — one flake rate over a controlled run set, and no rate quoted where there is no repeat sample |
| **4** | 6 | AC-1 **PARTIAL** — 3 of 5 rows measured, two unrunnable on this tree. AC-3 **FAILED on the day** and **discharged 2026-08-26 by phase 6 task 6.1**; the failure line is left standing and the discharge recorded beneath it |
| **8** | 4 | All four met. Its finding 6 — 35 rows owned by phases that had finished — was reported and deliberately not acted on, because re-owning a row is a plan's job |

**The residue is real and it now has a plan.** Thirty-one rows are open. Three of the thirty-four
phase 8 counted closed on 2026-08-26 with no new engineering (**X-25**, **X-27**, **E-11b** — the
work was on disk, only the ledger cell lagged), and the remaining twenty-eight are owned by
[`plans/verdict-closure/plan.md`](../verdict-closure/plan.md) R1–R6 plus
[`plans/asmdef-seam/phases/phase-c5-autoreferenced.md`](../asmdef-seam/phases/phase-c5-autoreferenced.md).

**Read the ledger, not this section.** [`debt-ledger.md`](debt-ledger.md) § 8 is computed by
`tools/recount_debt_ledger.py` and `--check` fails the moment it drifts; this paragraph is prose and
has no such guard. The ledger stays the single source of truth and is **not** forked by the
successor track (V-D1).

**`develop` is green.** At `b23aa28`: `dotnet test` **1,782 / 0 / 0** across seven projects
(Protocol 259, Replication 1,172, Transport 89, MasterServer 84, Client.Flow 79, Client.Input 39,
Configuration 60), `SpecChecker` 0, `ClientWiringGate` 0.

**Require-status-check is ON**, contrary to what this section said on 2026-08-25. Verified against
the API rather than the doc: ruleset `protect-shared-branches` (id 21395850) covers
`refs/heads/main` and `refs/heads/develop` with `deletion`, `non_fast_forward` and
`required_status_checks` — contexts `build-test (ubuntu-latest)`, `build-test (windows-latest)`,
`analyze (csharp)`. The eleven-merge red streak that X-23 recorded now has something standing in its
way.

**Phase files deleted 2026-08-26.** The nine phase specs were executed and their reports are the
durable record; see [`phases/README.md`](phases/README.md) for the index and the commit each file
was last present at.

### 4b. Deployment — where it is, and what is waiting on a person

Not a phase of this track; recorded here because it is the other half of "what is open".

| Item | State |
|---|---|
| Master + game-server images | **Done.** Both GHCR packages are **public**; master `sha256:5c1770f8…` (develop, 2026-08-25), game server `sha256:f88f04e2…` (`gameserver-v0.2.0`). Phase 7's "nobody has run the renamed workflow" no longer holds |
| Azure VM (`20.214.142.73`) stack | **Waiting on ngtukien**, issues #78 § 3.2–3.6 and #127. Nobody else can do it: `ssh_source_cidrs` admits one IP. Steps and both digests: [`docs/handover-ngtukien.md`](../../docs/handover-ngtukien.md) |
| fly.io master | **Landed** (#174) — `infra/fly/`, TCP 27000, SQLite volume, digest-pinned, one machine |
| fly.io game server | **Blocked, and not by us.** Fly carries no UDP over public IPv6 and requires a bind to `fly-global-services`; the design is IPv6-only and `UdpPeer.cs:92` binds `IPAddress.Any`. Three steps to unblock, in [`infra/fly/README.md`](../../infra/fly/README.md). Until then the game server runs only on the compose VM |

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Phase 3 grows into V9 proper | 4 | 4 | **16** | Scope locked by the fixed check list in `phase-3-harness.md` § 2, not by a tool boundary. Anything not on that list returns to V9 |
| A gate is written that cannot fail | 3 | 5 | **15** | Every detector must be observed RED on today's tree before the authoring lands. A detector never seen failing does not ship (`green-that-proves-nothing.md`) |
| Phase 1 authoring is unreviewable in a diff | 4 | 3 | 12 | P-D5 — the gate is the review artifact; prefab YAML diffs are read but not trusted alone |
| `releaseDelay` turns out ≠ 0.6 s | 3 | 3 | 9 | Phase 4 reads it before Phase 5 judges anything downstream. D7's divergence changes shape rather than disappearing |
| Scripted rendered clients are flaky | 3 | 3 | 9 | `--smoke` first (2 clients, 30 s) per `preview-first-batch.md`; a flaky check is reported flaky, not re-run until green |
| Phase 0 finds most rows already closed | 3 | 1 | 3 | That is the success case — it is what makes Phases 1–4 small |

---

## 6. Success criteria

1. One ledger replaces five sources of truth; every row carries `file:line` evidence and a status of `VERIFIED-OPEN` / `ALREADY-CLOSED` / `VOID`.
2. Every group-A item is authored **and** pinned by a gate observed failing before the authoring landed.
3. V7 acceptance criteria 1, 5, 6, 8, 9 and 11 are graded from an actual run, not asserted.
4. `releaseDelay` is a number read from the throw clip, not a guess.
5. Bandwidth per client and server tick p99 have a first measurement on record, with seed and configuration printed beside them.
6. `AuthoritativeFlight` is either **on** with a proof that damage applies exactly once, or **off** with a written reason.
7. `dotnet test`, `SpecChecker` and `ClientWiringGate` all exit 0 at every phase boundary.

---

## 7. Tracker

The `plane` MCP server is not registered in this session, so the Plane work-item gate degrades to
this warning and does not block: **no Plane work item is bound to this track.**
