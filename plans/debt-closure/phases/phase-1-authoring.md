# Phase 1 — Group A authored, and pinned by gates that can fail

- **Track:** [`plans/debt-closure/plan.md`](../plan.md) · **Effort:** M (3 days)
- **Depends on:** Phase 0's ledger (which group-A rows are still `VERIFIED-OPEN`), and — for task 1.6 only — Phase 2 task 2c
- **Tooling:** Unity Editor via MCP for the authoring; `dotnet run --project tools/ClientWiringGate` for the gates

---

## 1. Goal

Author the ten Editor-side items, and leave behind a gate for each that fails when the authoring is
undone. `_prefabsByKind` alone unblocks six of V7's thirteen acceptance criteria.

**The governing rule (P-D5): author, then pin.** Group A became ownerless debt because Editor
authoring leaves no artifact CI reads — which is exactly why every V10 row reads "unverified whether
the animator/rig/muzzle are authored". An item authored without a gate is an item that will be
"unverified" again next cycle.

---

## 2. Task 1.1 — Write the detectors FIRST, and watch them go red (0.75 d)

**File:** `tools/ClientWiringGate/ClientWiringDetectors.cs` (extend), `tools/ClientWiringGate/Program.cs` (register)

Prefab-YAML detectors join the existing source detectors. Per P-D6 this is one gate with one repo-root
resolver, and its exit-code contract already carries the right semantics: **0 clean, 1 the gate found
something, 2 the gate could not tell.** A prefab that cannot be located or parsed is exit 2, never 0.

| Detector | Fails when |
|---|---|
| `PrefabsByKindIsComplete` | `NetClientProjectilePresenter._prefabsByKind` has fewer entries than `ProjectileKind` has members, or any entry is a zero fileID |
| `RemoteActorPrefabIsAuthored` | `RemoteActorRegistry._remoteActorPrefab` is unset, or the referenced prefab lacks the animator / muzzle anchor / weapon mount children E1 names |
| `TurretPrefabsCarryNetTurret` | any prefab with a turret component lacks `NetTurret`, or its `TurretAimLimits` are still all-zero |
| `ScoreUiTextRefsAreAssigned` | `ScoreUi.phaseText` or `phaseTimerText` is a zero fileID |
| `ExplosionParticleArrayIsNonEmpty` | the explosion `ParticleSystem[]` is empty |
| `LobbyShellOverlayFieldsAreAssigned` | any of the three serialized fields is unset |

**Mandatory evidence, per `green-that-proves-nothing.md`:** each detector is run against the
pre-authoring tree and observed **RED**, and that output is pasted into the phase report. A detector
never seen failing does not ship. Where Phase 0 graded an item `ALREADY-CLOSED`, its detector must
still be written — and it is expected green, with the ledger row cited as the reason.

---

## 3. Authoring tasks

Ordered by unlock value. Each ends with its detector green.

| # | Item | Notes |
|---|---|---|
| 1.2 | `NetClientProjectilePresenter._prefabsByKind` | **Do first.** Unblocks V7 criteria 1, 5, 6, 8, 9, 11. The server side needs no authoring — it learns each kind's numbers from the first prefab of that kind it fires |
| 1.3 | E1–E6 | remote-actor rig / muzzle anchor / weapon mount, per-weapon flash + report refs, tracer visual, HUD wiring, explosion `ParticleSystem[]` |
| 1.4 | `NetTurret` + `TurretAimLimits` on every turret prefab; `aimLimits` | V0 closure § 3.4 says opening a turret prefab writes `aimLimits` into the YAML with correct defaults on first save — so 1.4 closes it as a side effect |
| 1.5 | `CAR HORN` row in `_Managers.prefab` | Gated by `SpecChecker`, which already fails on `WeaponIds.CAR_HORN = 18` having no prefab row. No new detector needed |
| 1.6 | `ScoreUi.phaseText` / `phaseTimerText` | **Blocked on Phase 2 task 2c.** Authoring these before the state extraction targets fields the refactor moves |
| 1.7 | `damageDropOff` curves | Sampled into `ProjectileConfig` by the build step |
| 1.8 | `LobbyShellOverlay`'s three serialized fields | E9, scene hygiene |
| 1.9 | A4 — `NetMovementAgent` + `NetPredictionClock` on the player prefab | **Conditional.** Only if Phase 0 task 0.2 finds A3's shadow re-run still required and unfinished |

---

## 4. File ownership

Writes: `tools/ClientWiringGate/**`, `Ironfront_Reborn/Assets/**/*.prefab`,
`Ironfront_Reborn/Assets/**/*.unity`, `Ironfront_Reborn/Assets/**/*.asset`.
Does **not** write any `.cs` under `Ironfront_Reborn/Assets/Scripts/` — code changes belong to Phase 2.

---

## 5. Acceptance criteria

1. Every detector in task 1.1 was observed RED against the pre-authoring tree, with the output in the report.
2. `dotnet run --project tools/ClientWiringGate` exits **0** on the post-authoring tree.
3. `dotnet run --project tools/SpecChecker` exits **0** (closes 1.5).
4. `_prefabsByKind` has one entry per `ProjectileKind` member, and `UnrenderableKinds` counts zero in a smoke run.
5. Unity compiles clean and the existing EditMode suite still passes.
6. Every authored item's ledger row moves to `CLOSED` with the detector named as its evidence.
7. Task 1.6 is committed **after** task 2c, verifiable from git history.

---

## 6. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| A detector is written that cannot fail (wrong path, wrong field name, tolerant regex) | 4 | 5 | **20** | The RED-first requirement in task 1.1 is not optional; a detector that will not go red on the pre-authoring tree is broken, not satisfied |
| Prefab YAML is hand-edited instead of authored in the Editor, corrupting a fileID | 2 | 5 | 10 | All authoring goes through the Editor over MCP; YAML is read for verification, never written by hand |
| `_prefabsByKind` is authored with a prefab of the wrong kind | 3 | 3 | 9 | The detector checks entry count; the smoke run's `UnrenderableKinds == 0` checks correctness |
| Task 1.6 lands before 2c through inattention | 3 | 2 | 6 | Stated as an acceptance criterion, checkable from git history |

---

## 7. Handoff

To **Phase 3**: an authored client is the precondition for every observational check — E7–E12 cannot
run against a client that renders no projectiles.
To **Phase 4**: `damageDropOff` and `releaseDelay` are both authored numbers; 1.7 authors the curves,
Phase 4 reads the clip for the delay.
