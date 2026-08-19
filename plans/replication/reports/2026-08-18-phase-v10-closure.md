# Report — Phase V10 closed, late, by one severed task nobody noticed had unblocked itself

- **Author:** the replication track (Replication & Simulation)
- **Date:** 2026-08-18
- **Phase:** [phases/phase-v10-client-event-consumption.md](../phases/phase-v10-client-event-consumption.md)
- **PR:** #129
- **Status:** ☑ **Done** — all twelve tasks landed; the one that shipped after the PR merged is confirmed in the tree today

---

## 1. One-paragraph summary

V10 shipped with Tasks 1-7 and 9-12 landed and Task 8 (capture-point consumption) severed, exactly as
its own § 8 implementation record says: Task 8 was hard-blocked on `V8 Task 1` (D15) — `ApplyAuthoritativeOwner`
did not exist yet, and until V8's `NetContext.IsOffline` gate landed, `CapturePoint.UpdateOwner` was
still running its own 1 Hz arithmetic on the client, so writing replicated ownership beside it would
have produced a second client-side writer. The phase shipped a **self-retiring** gate exemption instead
of silently shipping the handler early — `GateRunner.KnownUnwiredEvents` named the one dead event, the
blocking work and the reason, and was built to fail loudly if that event were ever found subscribed
while the entry was still present. **V8 landed and the blocker cleared, and for a while nothing picked
Task 8 back up** — the exemption stayed correct in shape but stale in fact, reporting a blocker that no
longer existed. That gap is now closed: `NetClientObjectivePresenter.OnCapturePoint` (commit
97dbe7f on this branch) subscribes the event and writes through `ICapturePointDirectory.ApplyAuthoritativeOwner`,
and its own doc comment records the pickup in exactly those terms: *"Task 8 was severed from V10 and
landed afterwards ... V8 shipped both halves ... and the blocker cleared without anything picking the
task back up. This is that pick-up."*

---

## 2. What the phase's own record already says, verified against the tree today

The phase file carries a `## 8. Implementation record — 2026-08-18` section that is itself a closure
document for eleven of twelve tasks and an open item for the twelfth. Re-reading it against the source
rather than re-deriving it:

| Phase-file claim | Verified against |
|---|---|
| Tasks 1-7, 9-12 landed | `RemoteActorRegistry.TryFind`, `NetClientPresenterGuard`, `RemoteActorView`, the `IsLocalActor` gating in `Actor.cs`/`TankTurret.cs`/`MountedTurret.cs`, `NetClientCombatPresenter`, `NetClientObjectivePresenter`, `NetClientExplosionPresenter`, the `MinimapUi`/`CapturePoint`/`DecalManager` guard fixes, and `tools/ClientWiringGate/` all exist in the tree |
| Task 8 not written at merge time, `OnCapturePoint` the one dead event | matches; superseded (§ 3 below) |
| `GateRunner.KnownUnwiredEvents` self-retires on subscription, not on unblocking | its own doc comment (`GateRunner.cs:32-60`) narrates exactly the staleness this report describes, and states the list is **empty today** |

---

## 3. STILL-OPEN — the table that matters

| Item | Handed to | Open today? | Evidence |
|---|---|---|---|
| **Task 8 — capture-point consumption (`OnCapturePoint` → `ApplyAuthoritativeOwner`)** | Severed on V8 Task 1 (D15); nobody named as picking it up once V8 landed | **CLOSED.** `NetClientObjectivePresenter.cs:131-143` subscribes `OnCapturePoint` and calls `ICapturePointDirectory.ApplyAuthoritativeOwner` with the same `CapturePointOwnership` mapping `CapturePointSlave.Apply` uses server-side. `GateRunner.KnownUnwiredEvents` is documented empty in its own remarks ("Empty since task 8 landed"). |
| **Killfeed lines have no player names** (§ 7 "To V3") | V3 (needs a `PlayerList` protocol message; `ServerMessageType.PlayerList = 0x4B` is declared with no struct and no router case) | **OPEN.** `grep -rn "PlayerList"` across `Ironfront.Net.Protocol` finds the enum member only, no message type, no handler. |
| **No capture-point minimap marker** (§ 7 "To V3") | "V8 or a UI phase" | **OPEN, unowned.** `MinimapUi` still has no marker API for capture points; its markers are the `SpawnPoint` buttons `SetupMinimap()` builds once, and `AddActorBlip` is add-only over an `Actor`, not a `Transform`. |
| **No scorch `DecalType`** (§ 7 "To V3", cross-referenced from V1 § 7) | V7 | **OPEN.** `DecalManager.DecalType` is still `Impact` / `BloodBlue` / `BloodRed`; explosions still reuse `Impact`. |
| **Per-bone ragdoll force / `ApplyRigidbodyForce` hardcoded to `MainRigidbody()`** | "unowned, recorded" | **OPEN, unowned.** No per-bone force API exists anywhere in `Assembly-CSharp`. |
| **`ClientCombatState` is instantiated by nothing** (§ 8, "two gaps this phase opened") | "V3 or a client-flow phase" | **OPEN, unowned.** `grep -rn "new ClientCombatState\|ClientCombatState instance"` across `Assets/Scripts/` returns zero MonoBehaviour owner. The local player's death state (input disable, respawn screen) still has no driver beyond `NetClientCombatPresenter.KnockOverLocalActor` felling the body. |
| **`ScoreUi` has no phase/timer/human-count `Text` assigned** (§ 8) | the client track (E5) | **OPEN — and now has no owner.** `ScoreUi.cs:23,25` declare `phaseText`/`phaseTimerText` as optional fields the client track was to assign in the Editor; the project has been single-owner since 899e75d, with no dev-role handoff left to receive an Editor-only item. `SetAuthoritativeState` (`ScoreUi.cs:251-257`) still borrows the flag labels and logs a `WarnOnce` naming E5 when they are unset. |
| **E1-E6 prefab/asset wiring** (remote-actor rig/muzzle/mount, per-weapon flash/report refs, tracer visual, HUD wiring, explosion `ParticleSystem[]`) | the client track, Editor-only | **OPEN, and now unowned** for the same single-owner reason above. `_remoteActorPrefab` (`RemoteActorRegistry.cs:39,110,206`) is still resolved by field with no code-visible guarantee of its Editor contents; **unverified whether the animator/rig/muzzle/mount are authored** — verifying it requires opening the Editor and inspecting the prefab, which this report does not do. |
| **E7-E12 two-client Editor tests** (combat, HUD, capture point, grenade, A16 camera hijack, scene ordering) | the client track, Editor-only, two clients | **OPEN, unowned, unverified.** No test harness or recorded run exists in the repo for any of the six. These are the tests the phase itself calls "the smaller versions of the same test" V9 needs to run first (§ 7 "To V9"). |
| **Drift found while verifying** — `plans/00-shared/architecture.md:314` still cites the private `IngameUi.ShowHitmarker()`; `phase-v7-projectiles.md:211` and `phase-v8-objectives.md:91` cite stale line numbers; `docs/codebase-map.md`'s eight `Actor.cs` references have shifted | recorded, not assigned | **OPEN.** Documentation drift only — no code impact. Not re-verified line-by-line here; the phase file's own table (§ 7 "Plan-document drift") is the source. |

---

## 4. What this report does NOT claim

- It does not re-run the Editor. Whether the remote-actor prefab actually carries an animator, ragdoll
  rig, muzzle anchor and weapon mount (E1) is **unverified** — the code path that consumes it
  (`RemoteActorRegistry.cs`) exists and null-guards correctly either way, which is all that is
  checkable from source.
- It does not re-run `tools/ClientWiringGate` or `dotnet test`. The claim that `KnownUnwiredEvents` is
  empty is read from the source comment at `GateRunner.cs:53-60`, not from a fresh CI run.
- It does not assert who, if anyone, will pick up the client-only items in § 3. The single-owner
  status is a fact about the project, not a resolution of those items.

---

## 5. Next

Nothing in the replication track blocks on this phase closing. The open items above are either (a)
protocol work for V3, (b) unowned Editor/UI work with no client track to receive it, or (c) recorded
drift with no code effect. None of them block V9's server-side acceptance criteria; they block the
**Editor-verified** half of V9 (E7-E12's two-client tests), which V9's own report should state as
unverified rather than assumed passing.
