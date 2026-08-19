# Phase 0 — An evidence-backed ledger

- **Track:** [`plans/debt-closure/plan.md`](../plan.md) · **Effort:** S (1 day) · **Depends on:** nothing
- **Nature:** read-only. No source file is edited in this phase.
- **Gate:** no item enters Phases 1–5 without a ledger row carrying `file:line` evidence.

---

## 1. Goal

Replace five stale sources of truth with one ledger in which every open item has been re-checked
against today's tree. The phase's own success case is finding that rows have already closed.

**Why it is first.** Every closure report is honest about what it did *not* verify — V1's table says
"unverified whether V4 has landed", V8's says "does not verify V4's or V7's status", V10's says
"verifying it requires opening the Editor, which this report does not do". Those rows were written
before four merges. Specifying work against them without checking is how a phase re-does something
already on `develop`.

---

## 2. Tasks

### Task 0.1 — Run the two existing gates and record their output (0.5 h)

```
dotnet run --project tools/SpecChecker
dotnet run --project tools/ClientWiringGate
```

`SpecChecker` answers the `CAR HORN` registry row definitively — it parses `_Managers.prefab`
against `WeaponIds` and fails on a missing id. `ClientWiringGate` answers which router events still
have no production subscriber, which covers the `ClientCombatState` and `PlayerList` rows.

Record both exit codes and full output in the ledger. **Exit code 2 is not a pass** — it means the
gate could not tell, and a row it could not tell about stays `VERIFIED-OPEN`.

### Task 0.2 — Re-verify group A, the ten authoring items (2 h)

Read the prefab and scene YAML directly; no Unity needed.

| Item | How to check |
|---|---|
| `_prefabsByKind` | grep the scene/prefab YAML for the `NetClientProjectilePresenter` component block and count array entries against `ProjectileKind` members |
| E1–E6 | `RemoteActorRegistry._remoteActorPrefab` (`RemoteActorRegistry.cs:39,110,206`) → resolve the GUID → read the prefab for animator/muzzle/mount children |
| `NetTurret` + `TurretAimLimits` | grep every turret prefab for the component guid |
| `CAR HORN` row | Task 0.1's `SpecChecker` output |
| `ScoreUi.phaseText` / `phaseTimerText` | `ScoreUi.cs:23,25` fields → grep the prefab for non-zero fileIDs |
| `damageDropOff` curves | grep `ProjectileConfig` build step for its sampling source |
| `aimLimits` | V0 closure § 3.4 says defaults are already correct and a prefab pass is optional — confirm that still holds |
| `LobbyShellOverlay` three fields | grep the scene YAML |
| A4 `NetMovementAgent` + `NetPredictionClock` | grep the player prefab; **and** decide whether A3's shadow re-run is still required, or whether later merges made it moot |

### Task 0.3 — Re-verify group C, the fourteen code rows (2 h)

Each row gets a grep with its scope stated inline (`zero across <paths>`, never a bare "does not
exist" — `negative-result-scope.md`). Prime suspects for `ALREADY-CLOSED`:

- V3's `SeatInfo` shedding cost — V3's closure says it stays live "until `VehicleIdPool` exists and
  `NetServerActor.Capture()` can pass a real `vehicleId`". V4 (`ce69391`) may have delivered both.
- V1's `ExplosionKind.Vehicle` row — handed to V4, which has since merged.
- V1's `ExplosionKind.Environment` row — handed to V7, which has since merged.
- V1's `Actor.Damage` balance-parameter row — "assumed closed, not re-verified".

### Task 0.4 — Re-verify group E, the round-8 ops items (1 h)

D1 is the important one: `integration-checklist.md:34` still calls it "BLOCKS THE ENTIRE
DEPLOYMENT", but `c80c09e` is titled *"produce the Linux dedicated-server artifact — two defects in
the pipeline"*. Read that commit's diff and grade D1 against it. Then A3, A7, A11.

### Task 0.5 — Write the ledger and retire the checklist (1.5 h)

Write `plans/debt-closure/debt-ledger.md`: one row per item, columns
`id | group | source doc | status | evidence (file:line or commit) | phase that closes it`.

Add a header to `plans/replication/integration-checklist.md` pointing at the ledger and marking
round 8 superseded. **Do not delete it** — its round-by-round history is how "what closed since
round 7" stays readable.

### Task 0.6 — Surface the three decision-only items to the owner (0.5 h)

`A12` (server CPU percentage), `A13` (who owns the kill/death tally) and `D2` (`EditorBuild.cs`
sign-off) are decisions, not work. Batch them into one `AskUserQuestion` at the end of the phase and
write the answers into the ledger as decided facts.

---

## 3. File ownership

Writes: `plans/debt-closure/debt-ledger.md` (new), `plans/replication/integration-checklist.md`
(header only). Reads: everything else.

---

## 4. Acceptance criteria

1. Every item from the design-of-record's groups A–E has exactly one ledger row.
2. Every row carries a status of `VERIFIED-OPEN`, `ALREADY-CLOSED` or `VOID` **and** `file:line` or
   a commit SHA as evidence. Zero rows read "unverified".
3. Every negative claim states the paths searched.
4. Both gate runs are recorded with their exit codes; any exit-2 row stays `VERIFIED-OPEN`.
5. `integration-checklist.md` carries a superseded header pointing at the ledger.
6. A12, A13 and D2 are recorded as decided facts, not as open questions.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| A row is graded `ALREADY-CLOSED` on a weak grep and the work silently never happens | 3 | 4 | 12 | A close verdict needs a positive citation (`file:line` showing the fix), never the absence of a match |
| Prefab YAML is read wrongly without Unity | 3 | 3 | 9 | Where YAML is ambiguous, mark `VERIFIED-OPEN` and let Phase 1 settle it in the Editor. Ambiguity is not evidence of closure |
| The ledger becomes the sixth stale document | 2 | 4 | 8 | Every later phase updates the row it closes, in the same commit as the closing work |

---

## 6. Handoff

To **Phase 1**: the group-A rows still `VERIFIED-OPEN`, plus the A3/A4 verdict.
To **Phase 2**: the group-C rows still `VERIFIED-OPEN`.
To **Phase 4**: any row whose status is "cannot be graded without a measurement".
