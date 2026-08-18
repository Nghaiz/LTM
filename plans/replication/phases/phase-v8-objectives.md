# the replication track — Phase V8: Objectives — one capture authority, and a spawner that survives a headless server

> Design of record:
> [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md).
> Read it first. **§ 2.1** is the evidence for the duplicate-authority bug and **D6** is the
> resolution; neither is re-derived here. This phase also finishes
> [`phase-03-match.md`](phase-03-match.md) Task 2, which shipped the replicated half and never
> connected it to the scene.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2 —
> no allocation on the hot path, no `System.Linq`, no `foreach` in logic files. Engine-free logic
> in `Ironfront.Net.Replication`, Unity holding only a thin seam. C# 9 in `Assets/`.
>
> **Off the vehicle critical path.** V8 depends on no other V-phase and can land while V3 is in
> review (design § 6). The one part that touches the wire — Task 6 — is deliberately severed
> behind a seam so the other five tasks merge without waiting for a protocol bump.

---

## 1. Objectives

Two capture-point systems are running simultaneously today, they disagree on every axis, and the
one that decides where players respawn is the one that is **not** replicated. That is a live
gameplay bug with or without vehicles, and it is what this phase closes.

By the end of this phase:

1. There is exactly one capture-point authority — `CapturePointState`. The scene's `CapturePoint`
   MonoBehaviour keeps its geometry and its contested-spawn logic and loses its ownership
   arithmetic.
2. `SpawnPoint.owner` is written from `CapturePointState.OwningTeam` every tick, so
   `ActorManager.RandomSpawnPointForTeam` (`ActorManager.cs:190`) and
   `ServerCombatBridge.MoveToSpawnPoint` (`:233`) select spawns from the authoritative value
   instead of a 1 Hz scene component nobody replicates.
3. Contested-spawn safety reads one contested flag, not two. The
   `CapturePoint.GetSpawnPosition()` override (`:276-283`) stays — it is real gameplay — and its
   `isContested` branch and its safe-direction flags are fed from authoritative presence.
4. `MatchController._capturePoints` is bound to the real `CapturePoint` components, so radius and
   capture speed have a single authored home instead of two serialized floats on the controller
   that no level designer can vary per point.
5. `VehicleSpawner` has a bounded, re-entrancy-safe, headless-safe lifecycle with a server
   authority behind it and a lifecycle seam ready for `S_VEHICLE_SPAWN` / `S_VEHICLE_DESPAWN`.
6. The objective-side gap the netcode does **not** cover is closed where it can be and recorded
   where it cannot: elimination-by-spawn-point-loss moves into `MatchStateMachine`; score,
   score-by-flag-count and the `victoryPoints` win live in `ScoreUi` and stay there.

**Not in this phase:** no vehicle snapshot, no seats, no projectiles, no protocol bump. No Editor
session — but this phase produces the single largest the client track item in the track (§ 7), and that item
is on V9's critical path.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **D1** | **`CapturePointState` is the authority; the scene `CapturePoint` becomes a slave.** Design-of-record **D6**, restated here because every task below assumes it. The component is not deleted: deleting it takes `GetSpawnPosition()`'s contested-spawn logic (`CapturePoint.cs:276-302`) with it, and slaving is smaller and reversible. |
| **D2** | **`UpdateOwner` is disabled by role, not by deletion, and it is disabled on the CLIENT too.** `NetRole.Offline` runs it unchanged; `Server` and `Client` do not. A client running its own 1 Hz arithmetic would fight the `S_CAPTURE_POINT` messages it is already receiving and produce the same disagreement one process further out. Same role-guard shape as phase-05 Task 6's `Actor.Damage` guard, and the same promise: single-player behaviour is byte-for-byte unchanged. |
| **D3** | **Ownership enters the component through exactly one method**, `ApplyAuthoritativeOwner(int team, float control, bool contested)`. The server slave-writer and the client's capture-point message handler both call it; nothing else writes `owner`, `control`, `pendingOwner` or `isContested`. Two writers into a private field are how § 2.1 happened. |
| **D4** | **`UpdateOwner` splits into arithmetic and presence.** Only the arithmetic is authoritative; the presence scan still has to run on the server, because `contestedSpawnpointIsSafe[]` is what makes a contested spawn safe, and it is computed nowhere else. Disabling `UpdateOwner` wholesale would leave the flags stuck at all-true (`ClearContestedSpawnpointSafeFlags`, `:208-214`) and quietly turn safe spawning into random spawning — the exact class of silent-degradation this phase exists to remove. |
| **D5** | **The presence scan is fed from `MatchController._presence`, not from `ActorManager.AliveActorsInRange`.** The original allocates a `List<Actor>` and a `Dictionary<int,int>` on every call (`CapturePoint.cs:120-121`) and uses `foreach`. The controller already builds an allocation-free `ActorPresence[]` every `FixedUpdate` (`MatchController.cs:174-188`); reusing that span costs nothing and keeps § 3.2 satisfied. |
| **D6** | **`_capturePoints` changes type from `Transform[]` to `CapturePoint[]`.** Radius and capture speed then come from the component that a level designer already authors (`captureRange`, and a new `captureSpeed`), and `_captureRadius` / `_captureSpeed` on the controller (`MatchController.cs:45-46`) demote to defaults used only where the component leaves them unset. Point **id remains the array index** — that is the wire id and it must not become discovery-order-dependent. |
| **D7** | **The type change loses the scene references, and that is handled with a logged fallback, not a silent one.** Unity drops serialized references on a field type change, so the array comes back all-null and `BuildCapturePoints` (`:148-172`) yields zero points — a headless server that silently plays deathmatch. V8 therefore ships a name-ordinal `FindObjectsOfType<CapturePoint>()` fallback that **logs an error naming the rebinding step and prints the resolved id order**. Documented, logged and surfaced, per `development-principles.md` § "Errors Over Silent Fallbacks". The client track's rebind (§ 7) removes it from the log. |
| **D8** | **V8 owns the vehicle-spawner authority and its lifecycle seam, not the bytes.** The seam is `IVehicleLifecycleSink` with a no-op default, so Tasks 1-5 merge with no dependency on protocol v3. Task 6 — the actual `S_VEHICLE_SPAWN` / `S_VEHICLE_DESPAWN` emission — is half a day that lands the moment V3 merges, and V4 is its real consumer. This is what keeps the design's "V8 depends on —" honest. |
| **D9** | **Elimination moves into `MatchStateMachine`; scoring does not.** `ScoreUi` holds match state in a UI component and its own doc comment already flags it (`ScoreUi.cs:46-57`): on a headless server the original game neither scores nor ends. Elimination is a *loss condition* and belongs to the authoritative match. Score, `ScoreMultiplier(flags)` (`:142-145`) and the `victoryPoints` race (`:77-84`) are a **rendering-and-rules redesign** that is the client track's call, and the networked match uses tickets instead. Recorded as a deliberate divergence, not a defect to fix here. |
| **D10** | **Elimination counts spawn points, not capture points.** The faithful port of `ScoreUi.AddFlag`'s condition is `ActorManager.HasSpawnPoint(team)` (`ScoreUi.cs:100-107`, `ActorManager.cs:219`), which counts every `SpawnPoint` with `owner == team` — including uncapturable HQs (`canBeCaptured`, `CapturePoint.cs:23`). Counting only capture points would make elimination fire on maps where a team still has a base, and never fire on maps where the HQ is uncapturable. |

---

## 3. Detailed tasks

### Task 1 — Split `CapturePoint` into geometry and arithmetic (1.5 days)

**Files:** `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/CapturePoint.cs` (the client track file — PR plus
a review round, per design § 7).

`UpdateOwner()` (`:112-206`) currently does five jobs in one 1 Hz method: it scans actors in range,
counts them per team, computes `isContested`, refreshes the contested-spawn safe flags, runs the
two-stage `control` / `pendingOwner` arithmetic, and drives the `IngameUi` flag indicator. Only the
arithmetic is being taken away.

| Member | Change |
|---|---|
| `Start()` (`:77-103`) | `InvokeRepeating("UpdateOwner", 1f, 1f)` runs **only** when `NetContext.IsOffline`. The `reverseMode` / `assaultMode` initialisation above it is unchanged in every role — it decides the *opening* ownership, which the server then adopts as its own initial value. |
| `UpdateOwner()` | Retained verbatim for the offline role. Its presence half is factored out into `RefreshPresence(...)` below so the two callers share one implementation rather than diverging. |
| **new** `RefreshPresence(ReadOnlySpan<ActorPresence> actors)` | Recomputes `isContested` and `contestedSpawnpointIsSafe[]` from a caller-owned span. No allocation, no `foreach`, no `Dictionary`. Returns `(int team0Count, int team1Count)` so the offline path can keep using it for its own arithmetic. |
| **new** `ApplyAuthoritativeOwner(int team, float control, bool contested)` | D3's single write path. Sets `isContested`, sets `control`, and calls the existing `SetOwner(team)` (`:234-269`) only when `team != owner`, so `ScoreUi.AddFlag` and `MinimapUi.UpdateSpawnPointButtons` still fire exactly once per flip and no more. |
| **new** `public float CaptureSpeed = 0.2f` | The per-point authored capture speed D6 needs. Default matches `MatchController._captureSpeed`, so an unedited prefab behaves exactly as it does today. |
| `Update()` (`:153-158`) | Unchanged. The flag-pole lerp is cosmetic and is driven by `control`, which now arrives authoritatively. |
| `flagRenderer.enabled` (`:363`) | Moves into `ApplyAuthoritativeOwner`, because it was the one line of rendering trapped inside the arithmetic. Null-guarded — a headless server has no `flagRenderer` when `lqFlag`/`hqFlag` are stripped. |
| `IngameUi` calls (`:240-251`) | Stay in the offline/client presentation path only. They are `IngameUi.instance` dereferences with no null guard and they are on the server's path today. |

**Why the arithmetic is not merely deleted.** The offline game is a shipping product. D2 promises it
is unchanged, and the only way to keep that promise cheaply is to leave the code that produces it in
place behind a role guard.

**Verify:** a role-parameterised test asserts `UpdateOwner` is scheduled at `NetRole.Offline` and
not at `Server` or `Client`; a second asserts `ApplyAuthoritativeOwner` calls `SetOwner` exactly
once across ten consecutive applications of the same team, and once per flip across an alternating
sequence.

---

### Task 2 — Bind `MatchController` to the real components (1 day)

**Files:** `Ironfront_Reborn/Assets/Scripts/Net/Server/MatchController.cs` (the replication track).

```
_capturePoints : Transform[]   →   CapturePoint[]
```

`BuildCapturePoints()` (`:148-172`) then reads `t.position` **and** `cp.captureRange` **and**
`cp.CaptureSpeed` instead of `t.position` plus two controller-wide floats. `_captureRadius` and
`_captureSpeed` (`:45-46`) stay as the values used when a component leaves them at zero, and their
tooltips say so.

`canBeCaptured == false` (an HQ) produces a `CapturePointState` whose `CaptureSpeed` is `0f`, so
`CapturePointState.Tick` moves it nowhere while it still counts for spawning, bleed and
elimination. That is the minimum-change way to honour the flag without adding a branch to a
well-tested engine-free class.

**D7's migration guard.** In `Awake`, before `BuildCapturePoints`:

- array is non-empty and every slot is non-null → normal path, no log.
- array is empty **and** the scene contains `CapturePoint` components → `Debug.LogError` naming the
  rebinding step, then populate from `FindObjectsOfType<CapturePoint>()` ordered by `name` (ordinal),
  and log the resolved `id → name` order so a client/server id mismatch is diagnosable from one line.
- array is empty and the scene has no capture points → deathmatch, silent. That is the existing,
  intended configuration.
- array has holes → the existing per-slot `LogError` at `:159-163` is unchanged; ids are slot
  indices and a hole renumbers every point after it.

**Verify:** an edit-mode test builds a scene with three `CapturePoint`s and an unbound array and
asserts three states are produced, the error is logged, and the id order is name-ordinal and stable
across two runs; a second test asserts an authored array of three produces the same three ids with
**no** log.

---

### Task 3 — `SpawnPoint.owner` writeback and one contested flag (1 day)

**Files:** new `Ironfront_Reborn/Assets/Scripts/Net/Server/CapturePointSlave.cs` (the replication track);
`MatchController.cs` (the replication track).

This is the task that fixes the reported bug. `MatchController.FixedUpdate` (`:114-124`) already
collects presence and ticks the match; one call is added after `_match.Tick(...)` and before the
broadcasts:

```
CollectPresence()
_match.Tick(dt, playerCount, presence)
_slave.Apply(_match.CapturePoints, presence, dt)     ← new
BroadcastMatchStateIfDirty()
BroadcastDirtyCapturePoints()
```

`CapturePointSlave.Apply` walks the state array and the component array in lockstep — same index,
same id — and for each point:

1. `component.ApplyAuthoritativeOwner(state.OwningTeam, control, state.IsContested)`, where
   `control` is `Math.Abs(state.Owner)` so the flag-pole height still reads as "how far along is
   this capture".
2. `component.owner = state.OwningTeam` — inherited from `SpawnPoint` (`SpawnPoint.cs:6`). **This
   single assignment is what makes `ActorManager.RandomSpawnPointForTeam` and
   `ServerCombatBridge.MoveToSpawnPoint` authoritative.**
3. Every `ContestedRefreshTicks`-th tick (default 6, i.e. 5 Hz at a 30 Hz loop),
   `component.RefreshPresence(presence)` so the safe-direction flags track the attackers.

`OwningTeam` is `byte` and `SpawnPoint.owner` is `int` with `-1` meaning neutral, so the slave maps
the neutral case explicitly rather than casting — a neutral point written as team `0` would hand
every neutral flag on the map to blue as a spawn.

**Rate, and why it is not per-tick.** The safe flags feed spawn *selection*, which happens at most
once per death. Refreshing at 5 Hz is 1900-odd dot products a second on a five-point map and is
indistinguishable from per-tick at the timescale a player can die. The divider is a named constant,
not a literal.

**Verify:** a headless test drives two teams across a point for sixty ticks and asserts
`SpawnPoint.owner == CapturePointState.OwningTeam` **on every tick**, including the tick of the
flip — the assertion design § 9's risk row calls for. A second asserts a neutral point leaves
`SpawnPoint.owner == -1`. A third asserts `Apply` allocates zero bytes over 1000 ticks.

---

### Task 4 — Elimination in the authoritative match (1 day)

**Files:** `Ironfront.Net.Replication/Match/MatchStateMachine.cs`,
`Ironfront.Net.Replication/Match/MatchRules.cs` (the replication track); `MatchController.cs` (the replication track).

`MatchStateMachine` gains, without changing `Tick`'s signature:

| Member | Contents |
|---|---|
| `void SetSpawnPointCounts(int team0, int team1)` | Called from `MatchController.FixedUpdate` before `Tick`. Counts every scene `SpawnPoint` with `owner == team`, per D10 — not just capture points. |
| `MatchRules.EliminationGraceSeconds` | Default `1f`, mirroring the original's `ElapsedGameTime() > 1f` guard (`ScoreUi.cs:98`). Without it a map whose points all start neutral ends instantly on tick one. |

Inside `Tick`, under `MatchPhase.Playing` and past the grace window: a team with zero spawn points
loses, which ends the match by the same path the ticket exhaustion already uses, so `MatchEnded`,
the phase broadcast and the reset all behave identically. If **both** teams hit zero — a
degenerate map or a mid-match teardown — the match ends as a draw rather than awarding it to
whichever index is tested first.

**What is recorded and NOT fixed here** (D9), stated in the class doc so the next reader does not
go looking:

- Score and the `victoryPoints` win condition live in `ScoreUi` and do not run headless.
- `ScoreMultiplier(flags)` returns the flag count (`:142-145`), so a team holding zero flags scores
  zero for every kill — a team that is being eliminated cannot score its way out. Faithful to the
  original and irrelevant to a ticket-based networked match.
- `GameManager`'s modes are five loose booleans (`reverseMode:23`, `assaultMode:25`, `nightMode:27`,
  `noVehicles:29`) plus `victoryPoints:31`, not an enum, so there is no single value for the server
  to replicate as "the mode". `reverseMode` and `assaultMode` are consumed once at
  `CapturePoint.Start()` (`:79-101`) and are therefore already covered — they change the opening
  ownership, which the server adopts.

**Verify:** a test drives a two-point map to zero-for-team-0 and asserts the match ends, ends once,
and ends past the grace window and not before; a second asserts a mid-match reset with both counts
at zero produces a draw rather than a win.

---

### Task 5 — `VehicleSpawner` authority and headless survival (1.5 days)

**Files:** new `Ironfront.Net.Replication/World/VehicleSpawnScheduler.cs` (the replication track); new
`Ironfront_Reborn/Assets/Scripts/Net/Server/NetVehicleSpawner.cs` (the replication track);
`Assembly-CSharp/VehicleSpawner.cs`, `Assembly-CSharp/Vehicle.cs` (the client track files — one PR, one review
round).

Five defects, all pre-existing, all fatal on a dedicated server:

| # | Site | Defect | Fix |
|---|---|---|---|
| 1 | `VehicleSpawner.cs:55-64` | `while (SpawnIsBlocked()) yield return new WaitForSeconds(1f)` is an **unbounded** retry. A spawner permanently blocked by a wreck retries forever, once a second, for the life of the process. | `VehicleSpawnScheduler` grants a bounded budget (`MaxBlockedRetries`, default 30). On exhaustion it stops, logs once, and re-arms only on the next `VehicleDied` / `FirstDriverEntered`. |
| 2 | `VehicleSpawner.cs:29` | `spawningQueued` is declared and never read or written — dead field where a re-entrancy guard clearly belonged. `StartSpawnCountdown` is `Invoke("SpawnVehicle", spawnTime)` (`:44`) with no guard, so two `VehicleDied` calls schedule two spawns and one spawner produces two vehicles. | The field becomes the guard it was named for: set on schedule, cleared when the coroutine completes or the budget is exhausted, and consulted by `StartSpawnCountdown`. |
| 3 | `VehicleSpawner.cs:49` | `GameManager.instance.noVehicles` — unguarded singleton deref in the spawn path. | Read into a local with a null check; a missing `GameManager` means "vehicles enabled", logged once. |
| 4 | `VehicleSpawner.cs:33` | `GetComponent<Renderer>().enabled = false` — no null check, and a headless build strips it. | Null-guarded. |
| 5 | `Vehicle.cs:252` | `spawner.FirstDriverEntered(this)` is unguarded while the sibling call at `:337` **is** guarded (`if (spawner != null)`). A vehicle placed directly in a scene rather than by a spawner NREs the first time anyone drives it. | Match the guard at `:337`. An asymmetric null check is a bug, not a style difference. |

**`VehicleSpawnScheduler`** is the engine-free half: per-spawner state (`Idle`, `CountingDown`,
`Blocked`, `Spawned`), the countdown, the retry budget, and the `RespawnType` rules
(`AfterDestroyed` / `AfterMoved` / `Never`). It is a pure function of `(state, dt, blocked,
events)`, backed by an array indexed by spawner id — no dictionary, no allocation, unit-testable in
CI without Unity. `NetVehicleSpawner` is the seam: it reads `SpawnIsBlocked()`, instantiates, and
reports back.

**`IVehicleLifecycleSink`** (D8) — `OnVehicleSpawned(ushort spawnerId, ushort vehicleId, in Vec3
position, in Quat rotation)` and `OnVehicleDespawned(ushort vehicleId, DespawnReason reason)`. V8
ships `NullVehicleLifecycleSink`, which is what the phase merges with; V4 supplies the writer.

**`WorldResetRequested` has zero subscribers.** Verified by grep across every `*.cs` in the
repository outside `obj/`: the event is declared at `MatchController.cs:73` and invoked at `:256`,
its doc comment says *"The spawner subscribes"*, and nothing does. So match two inherits match one's
vehicles and wrecks. `NetVehicleSpawner` subscribes, despawns its vehicle, and returns the scheduler
to `Idle`. Without this, V9's criterion 13 cannot pass.

**Verify:** engine-free tests for the scheduler — a blocked spawner gives up after exactly
`MaxBlockedRetries` and re-arms on the next death event; two `VehicleDied` calls produce one
countdown, not two; `Never` never schedules; `AfterMoved` schedules on first driver and not again
on that vehicle's death. A headless play test spawns, drives, destroys and respawns a vehicle with
`GameManager.instance == null` and asserts zero NREs. A reset test asserts the vehicle count is zero
after `WorldResetRequested`.

---

### Task 6 — The wire, when V3 lands (0.5 day)

**Severable and last (D8).** Everything above merges without it.

`ServerVehicleLifecycleSink : IVehicleLifecycleSink` writes `S_VEHICLE_SPAWN (0x4D)` and
`S_VEHICLE_DESPAWN (0x4E)` on the reliable channel via `ServerEventWriter`, following the shape of
the existing `WriteSpawn` / `WriteDespawn` (`ServerEventWriter.cs:41`, `:50`). `NetVehicleSpawner`
swaps its null sink for it. Vehicle ids come from V4's `VehicleIdPool` — verified absent from the
repository today, so if V4 has not landed the sink allocates from a local monotonic counter and the
swap is one line.

**Verify:** a loopback test asserts a spawn produces exactly one `S_VEHICLE_SPAWN` and a death
exactly one `S_VEHICLE_DESPAWN`, and that a `WorldResetRequested` teardown despawns every live
vehicle exactly once.

---

### Task 7 — Tests (1.5 days, written alongside Tasks 1-6)

All engine-free and in CI except the two marked, which are headless play tests with no Editor
session. New file `Ironfront.Net.Replication.Tests/ObjectiveAuthorityTests.cs` alongside the
existing `CapturePointTests.cs` and `MatchLifecycleTests.cs`.

| Test | Asserts |
|---|---|
| `SpawnPointOwnerTracksTheAuthoritativeTeamEveryTick` | Task 3's central assertion, across a full capture including the flip tick |
| `ANeutralPointLeavesSpawnPointOwnerAtMinusOne` | The `byte` → `int` neutral mapping |
| `TheSceneComponentDoesNotRunItsOwnArithmeticOnServerOrClient` | D2 — `UpdateOwner` scheduled only at `NetRole.Offline` |
| `OfflineCaptureBehaviourIsUnchanged` | D2's promise: the same input sequence produces the same `owner`/`control` trace as before the split |
| `ApplyAuthoritativeOwnerFiresSetOwnerOncePerFlip` | D3 — no duplicate `ScoreUi.AddFlag` |
| `ContestedFlagsComeFromAuthoritativePresence` | D4 — the safe flags move when an attacker moves, with `UpdateOwner` disabled |
| `AnUnboundCapturePointArrayFallsBackAndLogs` | D7 — the fallback fires, logs, and yields a stable name-ordinal id order |
| `AnAuthoredArrayProducesTheSameIdsWithNoLog` | D7's other half — the fallback does not fire when it should not |
| `AnUncapturablePointDoesNotMoveButStillCounts` | `canBeCaptured` → `CaptureSpeed = 0f`, still counted for spawn, bleed and elimination |
| `LosingEverySpawnPointEndsTheMatchOnce` | D10, past the grace window |
| `BothTeamsAtZeroSpawnPointsIsADraw` | The degenerate case |
| `TheSlaveAllocatesNothingOverAThousandTicks` | § 3.2 |
| `ABlockedSpawnerGivesUpAfterItsBudget` | Task 5 defect 1 |
| `TwoDeathEventsScheduleOneRespawn` | Task 5 defect 2 |
| `AWorldResetDespawnsEveryVehicle` | The zero-subscriber finding |
| **headless** `AHeadlessSpawnerSurvivesWithNoGameManager` | Task 5 defects 3-5, zero NREs |
| **headless** `AHeadlessCaptureCycleLogsNothingAtError` | The `IngameUi` / `flagRenderer` derefs removed from the server path |

---

## 4. Acceptance criteria

1. There is exactly one capture-point authority. `SpawnPoint.owner == CapturePointState.OwningTeam`
   at all times, asserted every tick by a test, including the tick ownership changes hands.
2. `CapturePoint.UpdateOwner` is scheduled at `NetRole.Offline` and at no other role, and offline
   single-player capture behaviour is unchanged by the split.
3. `owner`, `control`, `pendingOwner` and `isContested` have exactly one write path on the server
   and on the client — `ApplyAuthoritativeOwner`. Confirmed by grep in review.
4. `ServerCombatBridge.MoveToSpawnPoint` and `ActorManager.RandomSpawnPointForTeam` select from the
   authoritative value; a contested spawn picks a safe direction computed from authoritative
   presence, not from the scene component's own 10 m scan.
5. `MatchController._capturePoints` is a `CapturePoint[]`; radius and capture speed are authored per
   point; an unbound array logs an error, names the rebinding step, prints the resolved id order,
   and still produces a playable server.
6. A team that loses every spawn point loses the match, once, past the grace window — on a headless
   server, with no `ScoreUi` present.
7. A headless server spawns, drives, destroys and respawns a vehicle with zero NREs and with
   `GameManager.instance` null.
8. A blocked vehicle spawner gives up after a bounded number of retries; two death events schedule
   one respawn; `WorldResetRequested` despawns every live vehicle.
9. The objective-side gaps that are **not** closed — score, score-by-flag-count, the `victoryPoints`
   race, and `GameManager`'s boolean modes — are recorded in the `MatchStateMachine` class doc with
   the reason, not left to be rediscovered.
10. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-tick allocation
    in any new logic file.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| The `Transform[]` → `CapturePoint[]` type change silently drops the scene bindings and the server plays deathmatch | **5** | **4** | **20** | D7. The fallback is mandatory, not optional: it logs at error, names the rebinding step, prints the id order, and keeps the server playable. A test asserts the fallback fires. The client track's rebind (§ 7) is the first item in the handoff and blocks V9. |
| Both capture systems write ownership during the transition | 3 | 4 | **12** | D6 of the design; D1-D3 here. The scene component is slaved rather than deleted, ownership has a single write path, and a test asserts `SpawnPoint.owner == CapturePointState.OwningTeam` every tick. |
| Disabling `UpdateOwner` also disables the contested safe-flag computation, turning safe spawns into random ones with no error anywhere | 4 | 3 | **12** | D4 — the split is presence-vs-arithmetic, not on/off. `RefreshPresence` runs on the server at 5 Hz from authoritative presence, and a test asserts the flags move when an attacker moves. |
| `CapturePoint.cs` / `Vehicle.cs` / `VehicleSpawner.cs` conflict with the client track's branch | 4 | 3 | **12** | All three land in **one** PR, early, announced. Same precedent as phase-05 Task 6. Tasks 2-4 touch only the replication track files and merge independently of that review round. |
| The role guard changes offline single-player capture behaviour | 3 | 4 | 12 | `NetRole.Offline` runs the original method unchanged; `OfflineCaptureBehaviourIsUnchanged` pins the `owner`/`control` trace against a pre-split recording. Same shape and same promise as phase-05 D5. |
| Ids become discovery-order-dependent, so client and server disagree about which flag is which | 2 | 5 | 10 | D6 — the id stays the array index. The fallback's order is name-ordinal (deterministic across runs and platforms) and is printed, so a mismatch is one log line rather than an investigation. |
| Elimination fires on a map whose points all start neutral, ending the match on tick one | 3 | 3 | 9 | `EliminationGraceSeconds`, mirroring the original's `ElapsedGameTime() > 1f`. Counted over spawn points, not capture points (D10), so an HQ keeps a team alive. |
| `RefreshPresence` at 5 Hz on five points is measurable on the tick budget | 2 | 3 | 6 | Allocation-free and span-fed (D5), roughly 1900 dot products a second on a five-point map. V9's p99 measurement grades it; the divider is a named constant if it needs to move. |
| Task 6's wire lands against a `VehicleIdPool` that V4 has not shipped | 3 | 2 | 6 | D8 — the sink defaults to a local monotonic counter and the swap to V4's pool is one line. Verified today: no `VehicleIdPool` exists anywhere in the repository. |

**One risk scores 20.** The scene-binding loss is both the most likely and among the most damaging
failures in this phase, because its natural failure mode is *quiet* — a server that comes up, accepts
players, and plays a mode nobody chose. D7's logged fallback is a precondition of merging Task 2, not
a follow-up.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — Split `CapturePoint` | M (1.5d) | the client track file. Starts the review round early, so it does not gate the rest. |
| 2 — Bind `MatchController` to components | S (1d) | the replication track only. Needs Task 1's `CaptureSpeed` field. |
| 3 — `SpawnPoint.owner` writeback | S (1d) | Needs 1 and 2. **The reported bug closes here.** |
| 4 — Elimination in `MatchStateMachine` | S (1d) | Independent of 1-3 entirely. |
| 5 — `VehicleSpawner` authority | M (1.5d) | the client track files; rides Task 1's PR. Independent of 1-4. |
| 6 — The wire | S (0.5d) | **Severable, last.** Blocked on V3 merging; nothing above waits for it. |
| 7 — Tests | M (1.5d) | Written alongside 1-6, not after. |
| **Total** | **~8 days (~1.5 weeks)** | Critical path: 1 → 2 → 3. Tasks 4 and 5 are off it and can run in parallel. Task 6 is outside the estimate's critical path and lands whenever V3 does. |

---

## 7. Handoff

**To the client track — one PR, three files, and one Editor task that blocks V9.**

The PR carries `CapturePoint.cs` (Task 1), `VehicleSpawner.cs` and `Vehicle.cs` (Task 5), with the
offline-unchanged test attached. One review round is assumed.

The Editor task is **rebinding `MatchController._capturePoints`** to the real `CapturePoint`
components after the type change (design § 7 already lists it). Until it is done every server logs
D7's error on startup and runs on the name-ordinal fallback. It is the first line of V9's
prerequisites, because V9 measures a 16-player match on a map whose flags must be authored, not
discovered.

Also the client track's, unchanged from design § 7: `.meta` files for `CapturePointSlave.cs`,
`NetVehicleSpawner.cs` and `VehicleSpawnScheduler.cs`.

**To V4:** `IVehicleLifecycleSink` is the seam to implement, and `VehicleSpawnScheduler` already
owns spawner-side lifecycle state — V4 supplies ids and bytes, not policy.

**To V9:** criteria 7 and 13 of the design's acceptance list depend on this phase. Criterion 7
(`SpawnPoint.owner` matches `CapturePointState.OwningTeam`) is graded by Task 3's per-tick
assertion; criterion 13 (five clean matches) depends on the `WorldResetRequested` subscriber Task 5
adds, without which vehicles leak across rounds.

**Not in this phase and not the client track's either:** the `ScoreUi` redesign (D9). It is recorded in the
`MatchStateMachine` class doc as a divergence with a reason, and it is a product decision rather
than a netcode defect.

---

## 8. Amendments — as built (2026-08-18)

Five things the plan got wrong or that the tree had already moved past. Each is recorded here
rather than silently absorbed, because every one of them changes what a later phase inherits.

### A1 — D6's type change is impossible, and not doing it retires D7's score-20 risk

`_capturePoints : Transform[] → CapturePoint[]` cannot be written. `MatchController` lives in the
`Ironfront.Net.Unity.Server` assembly definition and `CapturePoint` compiles into
`Assembly-CSharp`, which is compiled last and which **no** asmdef may reference — the same
constraint that produced `ISpawnPointDirectory` and `IGameplayActorSource`, and which
`IronfrontNetBindings` documents at length.

D6's *content* ships unchanged: radius and capture speed are authored per point, and the id is
still the array index. It arrives through a new `ICapturePointDirectory` seam, implemented by
`SceneCapturePoints` in `Assembly-CSharp`, bound once in `Awake`.

**This deletes the phase's only score-20 risk rather than mitigating it.** That risk existed
*because* a serialized field would change type and Unity would drop its references. No field
changes type, so nothing is dropped. Verified against the shipped scene: `Dustbowl.unity` authors
all six slots and all six carry real `CapturePoint` components (Bridge, Fortress, Mine, Oasis,
Outpost, Town). D7's logged name-ordinal fallback still ships, for the different and pre-existing
case of a scene that never authored the array at all.

**§ 7's handoff item "rebind `MatchController._capturePoints`" is therefore void**, and V9 is not
blocked on it.

**One real gameplay change comes with it.** The server had been capturing on a flat
`_captureRadius: 15` that nobody authored; the six points author 20, 25, 27, 30, 30 and 34, which
is what the offline game has always used. The networked match now agrees with the map and with
single-player. Capture speed is unaffected — the new per-point default matches the controller's
`0.2`.

### A2 — the contested flag has two meanings, and the plan conflated them

Task 3 said to pass `state.IsContested` into `ApplyAuthoritativeOwner`. That is the **wire's**
sense — `CaptureFlags.Contested` means *both teams are present*. The scene component's
`isContested` drives `GetSpawnPosition()`'s safe-spawn branch and means *somebody hostile to the
owner is present*.

They differ exactly when an owned point is attacked by one team and defended by nobody: not
contested on the wire, and precisely the moment a defender most needs to spawn away from the
attackers. Passing the wire value would have quietly disabled safe spawning in the case it exists
for.

So the server feeds `ApplyAuthoritativeOwner` the value `RefreshPresence` computed, cached by
`CapturePointSlave` across the ticks between refreshes. D3 holds — one write path — and D4's
presence-versus-arithmetic split holds. The client, when V10 task 8 subscribes `OnCapturePoint`,
passes the wire flag, which is correct there: the client does not select spawns.

### A3 — three of task 5's five defects were already fixed

Defects 3 (`GameManager.instance` deref), 4 (`GetComponent<Renderer>()` deref) and 5
(`Vehicle.cs:252`'s unguarded `spawner` call) were closed by phase-V0's headless audit and are
pinned by `VehicleSourceInvariantTests.HeadlessDereferencesAreGuarded`. Only 1 (unbounded retry)
and 2 (missing re-entrancy guard) remained; both are closed here, and the guard tests were
extended rather than duplicated.

`VehicleSpawnScheduler` is **one instance per spawner**, not an array indexed by spawner id.
Every input it needs arrives from one `MonoBehaviour` about itself, so a central array would be a
table each spawner writes exactly one row of. It allocates once in `Awake` and nothing on the
tick path allocates — asserted.

The world-reset subscriber reaches `Assembly-CSharp` through a new static
`NetWorldLifecycle.ResetRequested` rather than through `MatchController`'s instance event. Vehicle
spawners are authored assets scattered across a map; a per-spawner serialized reference is a
manual step that gets forgotten on exactly the map nobody re-opened, which is the failure mode
this task exists to remove. The instance event is left in place and unchanged.

### A4 — task 6 is not in this PR

Severable and last, per D8, and genuinely blocked: `S_VEHICLE_SPAWN (0x4D)` and
`S_VEHICLE_DESPAWN (0x4E)` do not exist in `Ironfront.Net.Protocol` — V3 has not merged.
`IVehicleLifecycleSink` and `NullVehicleLifecycleSink` ship, so V4 has a seam to implement.
Rotation is carried as euler degrees because this library has no quaternion type, and the phase
that puts the value on the wire should choose its encoding.

### A5 — found on the way: the Unity CI gate could never fail

`tools/ci.ps1` step 4 invoked `Unity.exe` with the call operator. Unity is a GUI-subsystem binary,
so PowerShell does not wait for it and does not set `$LASTEXITCODE` from it — `Invoke-Step` read
the *previous* command's exit code and printed `PASS` on every run it has ever had. It printed
`PASS` over a real `Aborting batchmode due to failure: Scripts have compiler errors` during this
phase, which is how it was noticed.

Fixed with `Start-Process -Wait -PassThru`, which also surfaces the `error CS` lines instead of
only an exit code. Proved red by breaking a script on purpose and watching the gate fail, then
restoring it.

---

## 9. Amendments — task 6, as built (2026-08-18)

A4 left task 6 blocked on protocol v3. V3 merged as #135 with `S_VEHICLE_SPAWN (0x4D)`,
`S_VEHICLE_DESPAWN (0x4E)`, their codecs and their hex-sample conformance tests — and, as
V3 shipped them, no sender. This closes that, and three things in § 3's task 6 turned out to be
wrong.

### A6 — the seam had to widen, and one of the fields it needed does not exist in the scene

`IVehicleLifecycleSink` as V8 shipped it carried a spawner id, a vehicle id, a position and
euler degrees. `S_VEHICLE_SPAWN` needs a `VehicleKind`, a `networkTypeId`, a seat count and a
smallest-three-packed quaternion, so three of the four had to change.

- **The report carries `networkTypeId` and `SeatCount`.** The alternative was a sink that
  reaches back into the scene for the component it was handed an id for — a lookup that can
  fail, on a path whose entire job is to report a fact that already happened.
- **It does NOT carry the kind.** Nothing in the scene authors one: the prefab has
  `networkId` and that is all. `VehicleIds.TryGetKind` derives it against § 4.9 instead, so a
  caller cannot supply a kind that disagrees with the id beside it — a disagreement nothing
  downstream could have adjudicated. The kind column had no copy in code before this and no
  gate; `SpecChecker.CheckVehicleKindTable` now compares the two on every CI run, in both
  directions, and was watched failing on a deliberately flipped row before being trusted.
- **Rotation is four quaternion components, not euler degrees.** A4 reserved the encoding
  choice for this phase on the grounds that the library has no quaternion type. It still has
  none, and `Quantize.PackQuat` takes components — so the answer is to pass what the packer
  wants. Euler could not have been packed here at all without importing trigonometry.
- **`OnVehicleSpawned` returns the id rather than taking one.** Ids belong to the wire, and
  only the wire's owner can honour a quarantine. Letting `Assembly-CSharp` pick would put
  allocation where nothing knows what is still in flight. The null sink returning 0 is then
  exactly right off the server: no network id, because there is no network.

### A7 — the plan's id fallback would have failed on the process this phase exists to keep alive

Task 6 said that with no `VehicleIdPool` from V4, "the sink allocates from a local monotonic
counter and the swap is one line". At fourteen spawners replacing a vehicle every 16 s, a
`ushort` wraps in about ten hours. A dedicated server runs for days, and the wrap reissues a
live id with no quarantine at all — the collision the quarantine exists to prevent, arriving
silently on the machines that stay up longest.

So `VehicleIdPool` ships here rather than waiting for V4. It is `ActorIdPool`'s argument one
value space over, and it consumes `ProtocolConstants.VEHICLE_ID_QUARANTINE_TICKS`, which V3
declared at the freeze with nothing reading it. Capacity is `MAX_VEHICLES` (16) because that is
what the vehicle snapshot body is sized against — a seventeenth live vehicle has nowhere on the
wire to go, so handing out a seventeenth id would move the failure somewhere harder to see.
Both shipped maps author fourteen spawners, so the ceiling has two spare and the 150-tick
quarantine clears well inside a 16 s respawn.

Exhaustion returns id 0 and the spawner logs once, naming the pad — a vehicle that exists on
the server and reaches no client is not something to discover from a bug report.

### A8 — `NetVehicleSpawner.cs` was never built, and did not need to be

Task 5 named a new `NetVehicleSpawner` Unity seam. A3 folded the scheduler straight into
`VehicleSpawner` instead, and that decision holds here: the wire reaches the spawner through a
static `NetVehicleLifecycle`, for exactly A3's reason. Vehicle spawners are authored assets
scattered across a map — fourteen per scene — and a serialized reference on each is a per-map
manual step that gets forgotten on the map nobody re-opened. The symptom would have been a map
whose vehicles are invisible to every client, with nothing in the log.

`ServerTickLoop` installs the sink in `Bind` and uninstalls in `Unbind`, so a client and an
offline build keep the null object and the spawner's code path is identical in every role — the
same promise tasks 1-3 made about capture points. The loop implements
`IReliablePayloadSender` directly; its existing `BroadcastReliable` already had the signature.

**Still not covered, and deliberately:** a `Vehicle` placed directly in a scene rather than by a
spawner is not replicated. The seam is the spawner's, the scheduler's state is per-spawner, and
a scene-placed vehicle has no lifecycle to report. V4 owns snapshots and will need its own
answer for those; recorded here so it is a known gap rather than a discovery.
