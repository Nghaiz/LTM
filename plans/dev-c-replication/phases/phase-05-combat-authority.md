# Dev C — Phase 05: Server combat authority, and the debt cleared before the Editor phase

> Design of record: [`../../reports/2026-08-15-server-combat-authority-brainstorm.md`](../../reports/2026-08-15-server-combat-authority-brainstorm.md).
> Read it first — it carries the evidence for why this phase exists and why a reload-only patch
> does not close the reported bug.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files), engine-free
> logic in `Ironfront.Net.Replication` with Unity holding only a thin seam, C# 9 in `Assets/`.

---

## 1. Objectives

The server owns combat. Today it owns nothing of it: `InputAuthority.ApplyPendingInput` converts
an `InputFrame` into a `MoveInput` that carries only Jump / Sprint / Crouch
(`Movement/MoveInput.cs:57-59`), so Fire, Reload and Aim are dropped at the translation, and every
combat class Dev C shipped in phases 01-02 has no caller.

By the end of this phase:

1. Firing spends server ammo, reloading refills it on the server clock, and the snapshot's
   `SnapshotField.Weapon` therefore changes — which is what clears the client's `_reloadPending`
   and closes the reported desync.
2. Hits are resolved through `LagCompensator` with the connection's real `SmoothedRttMs`, settling
   the debt phase-02 § 9 recorded and phase-03 never picked up.
3. Damage, death and the respawn gate are authoritative, and `S_WEAPON_FIRE` / `S_HIT_CONFIRM` /
   `S_DEATH` are emitted.
4. A snapshot that does not fit one datagram sheds actors instead of being discarded whole.
5. The four open client minors (M7-M10) are closed, in the files Dev A is about to open.
6. All of it is graded by tests that run in CI without opening Unity.

**Not in this phase:** no Editor session, no Profiler. S5 / S4 / S7 stay with Dev A, B7 with
Dev B, master-list confirmation with Dev D.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| D1 | Full combat authority, not a reload patch. The server never spent ammo, so `SnapshotField.Weapon` never changed (`DeltaEncoder.cs:201` masks on change only) and a reload-only fix leaves the two sides disagreeing after the **first shot**. |
| D2 | The logic is engine-free in `Ironfront.Net.Replication/Combat/`; Unity holds a thin seam. Writing it in `ServerPlayer` would make every combat bug an Editor-only bug. |
| D3 | `ProtocolConstants.RELOAD_SECONDS = 2f`, `RESPAWN_SECONDS = 3f`, `EYE_HEIGHT` and `EYE_HEIGHT_CROUCHED` are the shared SSOT. `ClientCombatState.DefaultReloadSeconds` / `DefaultRespawnDelaySeconds` refer to them instead of declaring their own literals. |
| D4 | Bot and world damage route through the server at `Actor.Damage` (`Assembly-CSharp/Actor.cs:761`) — the one choke point every damage source in the original game funnels through (`Hitbox`, `MeleeWeapon`, `ExplodingProjectile`, `ActorManager`, `Vehicle`, `AiActorController`). **Not** `AiActorController`, whose eight coroutines were the reason PR #47 declined a change there. |
| D5 | At the client role the guard suppresses the **health mutation and `Die()` only**. `ReceivedDamage`, blood decals, ragdoll force and knockback still run, so hit feedback survives; health and death arrive from the snapshot and `S_DEATH`. |
| D6 | Snapshot overflow sheds the **lowest interest level first** (Far, then Mid), and within a level rotates by a per-client cursor carried between snapshots, so no actor starves. |
| D7 | The server mirrors the client's reload rules exactly: fire is refused while reloading and does **not** cancel the reload. Any change to that is a change to both sides in one commit. |
| D8 | Task 6 (the Dev A file) is last and independent. Everything before it merges without waiting on Dev A's review round. |
| D9 | There is one health field. `NetServerActor.Health` loses its own `_health` backing field and becomes a pass-through to `Actor.health`, so `Die()`, the AI, the ragdoll and the snapshot all read the same number. Two fields kept in sync by a sink would be exactly the silent divergence this phase exists to remove — and `development-principles.md` § "No Derived Fields" already forbids it. |
| D10 | The shot originates at `session.State.Position` plus a shared eye height, dropped when crouched or prone. `ProtocolConstants` gains `EYE_HEIGHT` and `EYE_HEIGHT_CROUCHED`; the stance comes from the `MoveState` the server already owns. Reading a camera or muzzle transform would drag hitscan into Unity, against D2, and would make the hit tests Editor-only. |

---

## 3. Detailed tasks

### Task 1 — The engine-free combat core (2 days)

**Files (all new unless noted), `Ironfront.Net.Replication/Combat/`:**

| File | Contents |
|---|---|
| `ServerReloadPolicy.cs` | `BeginReload(ref WeaponRuntimeState, in WeaponConfig, float now)` and `CompleteReloadIfElapsed(ref …)`. Refuses while already reloading, clip full, dead, or holstered. Same shape as `ClientCombatState` so the two sides read alike. |
| `ServerCombatAuthority.cs` | `Step(...)` — one actor, one accepted input frame: complete an elapsed reload → accept `Reload` → accept `Fire` via `ServerFireResolver.Resolve` → route hits into the damage sink. Returns `CombatTickResult { FireRejection Rejection; int HitCount; bool WeaponChanged; bool VictimDied; }`. |
| `IActorDamageSink.cs` | `DamageOutcome ApplyDamage(ushort victimId, float amount, ushort attackerId)`; `DamageOutcome { float RemainingHealth; bool Died; }`. |
| `ServerRespawnGate.cs` | `MarkDeath(ushort actorId, float now)`, `bool MayRespawn(ushort actorId, float now)`, `float SecondsUntilRespawn(ushort actorId, float now)`. The server counterpart of the client's existing `CanRequestRespawn`. |

**Edits:**
- `Combat/WeaponModel.cs` — `WeaponRuntimeState` gains `float ReloadStartedAt`; `Loaded` sets it to `float.NegativeInfinity`.
- `Server/ClientSession.cs` — gains `public WeaponRuntimeState Weapon;` and `public WeaponConfig WeaponConfig;`, initialised to `WeaponConfig.Rifle` / `WeaponRuntimeState.Loaded(...)`.

**Constraints.** No allocation in `Step`. Hits are written into a caller-owned `Span<HitResult>`.
`ServerRespawnGate` is backed by a pre-sized array indexed by actor id, not a dictionary.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests` green; new tests from Task 5 red until this lands.

---

### Task 2 — The input seam (1 day)

The root fix. `InputAuthority.ApplyPendingInput` gains an overload taking an observer invoked for
each **accepted** frame, with the intact `InputFrame`:

```csharp
public static int ApplyPendingInput(
    ClientSession session, float dt, Func<Vec3, Vec3> applyMove,
    IAcceptedFrameObserver? observer = null)
```

**Why an interface and not a delegate.** A capturing lambda allocates per call, and this is the
30 Hz path — § 3.2. The Unity seam implements the interface on a component it already holds, so the
reference is a field.

**Why per accepted frame, not per tick.** Grading on acceptance is what keeps rapid fire closed:
`ServerFireResolver.CheckCanFire` still measures against the server clock, so a client sending ten
frames a tick gets one shot and nine `OnCooldown` rejections, and `FireRateViolations` moves — the
signal criterion 6 is graded on.

The existing three-argument overload keeps working; the movement tests are untouched.

**Verify:** existing `InputAuthority` tests unchanged and green; a new test asserts the observer
sees exactly the frames that were accepted, and none of the ones rejected by `TryAccept`.

---

### Task 3 — Unity wiring (2 days)

Dev C-owned files only.

```
ServerPlayer.Tick
  ├─ InputAuthority.ApplyPendingInput(session, dt, move, _combatObserver)
  │     └─ _combatObserver.OnAccepted(frame) → ServerCombatAuthority.Step(...)
  │            ├─ rtt: Transport.GetInfo(connectionId).SmoothedRttMs   ← the phase-02 debt
  │            └─ hits → _damageSink → NetServerActor.Health
  └─ actor.AmmoInClip = session.Weapon.AmmoInClip     ← the line that closes the reported bug
```

- `ServerActorDamageSink` implements `IActorDamageSink` over `ServerActorRegistry`; it is the only
  place health is written on the server. Per D9, `NetServerActor.Health` drops its `_health`
  backing field and reads and writes `Actor.health` directly, so there is one number rather than a
  mirror that can drift.
- The shot origin is `session.State.Position + eyeHeight` per D10, with `eyeHeight` taken from
  `ProtocolConstants.EYE_HEIGHT` or `EYE_HEIGHT_CROUCHED` according to the stance already carried
  in `MoveState`. Aim comes from the `InputFrame`'s yaw and pitch, not from a Unity transform.
- Events: `S_WEAPON_FIRE` on the cosmetic channel filtered by
  `ServerEventWriter.WeaponFireAudibleRadius`, `S_HIT_CONFIRM` to the shooter alone, `S_DEATH`
  broadcast, then `MatchController.ReportDeath(team)` — which already exists and is already wired.
- `ServerMessageRouter` gains a `SpawnRequest` (0x23) route; it currently routes only `Input` and
  `AckBaseline`. The route consults `ServerRespawnGate` and silently drops an early request rather
  than throwing — a client whose clock is a little fast is not a protocol violation.

**Constraint.** `ServerPlayer` holds the observer and the sink as fields constructed once. Nothing
in this path allocates per tick, and nothing in it uses `System.Linq` or `foreach`.

**Verify:** solution compiles; `ServerTickLoop`'s allocation posture is unchanged by inspection
(the Profiler run that proves it is Dev A's S4, out of scope here).

---

### Task 4 — Snapshot overflow sheds actors instead of dropping the snapshot (2 days)

Today `ServerPayloadWriter.WriteSnapshot` returns `-1` and `ServerTickLoop.cs:295` discards the
whole snapshot with a `LogError`. A 48-actor full snapshot is 973 bytes and fits; 64 is 1293 and
does not.

**Not** by handing the oversized payload to the transport. `Connection.Send` does fragment
(`Connection.cs:356-388`) but forces `PacketFlags.Reliable` on every fragment, which would turn
snapshots reliable-ordered and introduce head-of-line blocking on the one channel whose whole
design is that a late snapshot is worthless.

Instead: `InterestManager.BuildView` takes a byte budget and stops adding actors once the projected
encode exceeds it, shedding the **lowest interest level first** (Far, then Mid) and rotating within
a level by a per-client cursor carried between snapshots (D6). Deferring an actor is already a
supported concept — rate-limited actors are omitted the same way, and the baseline is the client's
*acked* snapshot, so an omitted actor is picked up by a later delta with no special handling.

The viewer's own actor is never shed.

**Verify:** the 64-actor test in Task 5 — no snapshot dropped, every actor delivered within a
bounded number of snapshots, and the starvation assertion holds.

---

### Task 5 — Tests (2 days, written alongside Tasks 1-4)

All engine-free, all in CI, no Editor.

| Test | Asserts |
|---|---|
| `AReloadCompletesOnTheServerClock` | `AmmoInClip == ClipSize` after `RELOAD_SECONDS`, not before |
| `AReloadIsRefusedWhenTheClipIsFull` | and while dead, and while holstered |
| `FiringSpendsServerAmmoAndHonoursCooldown` | ammo decrements; a second shot inside `Cooldown` is `OnCooldown` and moves `FireRateViolations` |
| `FiringIsRefusedWhileReloading` | `FireRejection.Reloading`, and the reload is **not** cancelled (D7) |
| `DamageReachingZeroHealthEmitsDeath` | sink reports `Died`, `S_DEATH` framed, `ReportDeath` called once |
| `ARespawnRequestBeforeTheDelayIsRefused` | and accepted at exactly `RESPAWN_SECONDS` |
| `AnOverBudgetSnapshotShedsActorsRatherThanDropping` | 64 actors, nothing dropped, viewer always present |
| `AShedActorIsNotStarvedAcrossSnapshots` | every actor appears within a bounded window (D6) |
| `TheObserverSeesOnlyAcceptedFrames` | Task 2's contract |
| `AShotOriginatesAtEyeHeightAndDropsWhenCrouched` | D10 — a shot fired from the feet misses everything a standing shot hits, and nothing else would catch it |
| **`AReloadClearsWhenTheDeltaCarriesNoWeaponField`** | **the regression that has never been covered** — `ClientCombatTests` always fed `LocalEntry(ammo: 30)`, i.e. a snapshot that *does* carry the field, so the delta case that produced finding C2 has never run |

---

### Task 6 — Bot and world damage through the server (1 day + a Dev A review round)

**Independent and last.** Everything above merges without it (D8).

One guard at the top of `Actor.Damage` (`Assembly-CSharp/Actor.cs:761`):

- `NetContext.IsServer` → route the health change through `IActorDamageSink` so the authoritative
  copy is the one that moves, then let the rest of the method run.
- `NetContext.IsClient` → run `ReceivedDamage`, the blood decals, the ragdoll force and the
  knockback, but **skip the `health -=` and the `Die()`** (D5). Health and death arrive from the
  snapshot and `S_DEATH`.
- `NetContext.IsOffline` → unchanged. The single-player game must behave exactly as it does today.

`Actor.cs` is Dev A's file: PR plus their approval, and the schedule assumes one review round.

**Verify:** offline play is byte-for-byte unchanged in behaviour (the guard is a no-op at
`NetRole.Offline`); a server-role test shows bot bullets moving the authoritative health.

---

### Task 7 — Client minors M7-M10 (0.5 day)

In the files Dev A is about to open, so they land before the Editor phase, not during it.

| # | Fix |
|---|---|
| M7 | `IPAddress.TryParse` first; resolve a hostname off the GUI thread and wrap it — `Dns.GetHostAddresses` currently blocks inside `OnGUI` and throws `SocketException` / `NotSupportedException` into the unprotected path. |
| M8 | Cache the interpolated strings, stop boxing the `GameFlowState` enum, rebuild only when the value changes. |
| M9 | Wrap the three bare calls the class doc already claims are wrapped — `_flow.Transition`, `_session.EnterMatch()`, `_session.ConnectDirect`. Fix the code, not the comment. |
| M10 | Clear `_password` after a successful login. |

---

## 4. Acceptance criteria

1. Pressing R spends the server's reload time and refills the server's clip; the next snapshot
   carries a changed `SnapshotField.Weapon`.
2. Firing decrements server ammo; a client firing faster than `WeaponConfig.Cooldown` is rejected
   and `FireRateViolations` moves.
3. `LagCompensator.ResolveHitscan` is called with the connection's real `SmoothedRttMs`.
4. A hit moves the victim's authoritative health; zero health emits `S_DEATH` and reports to
   `MatchController` exactly once. There is exactly one health field in the server build — the
   snapshot, `Die()` and the AI all read `Actor.health`.
5. A shot originates at eye height and drops when the shooter is crouched or prone.
6. A respawn request before `RESPAWN_SECONDS` is refused; at the boundary it is accepted.
7. A 64-actor world produces a snapshot every time, and no actor is starved.
8. Client and server read `RELOAD_SECONDS`, `RESPAWN_SECONDS` and the eye heights from one place.
9. The delta-without-`Weapon` regression test is present and green.
10. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-tick allocation
    in any new logic file.
11. Offline single-player behaviour is unchanged by Task 6's guard.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| `Actor.Damage` guard changes offline single-player behaviour | 3 | 5 | **15** | `NetRole.Offline` is an explicit early no-op; a test pins it. Task 6 is last and independent, so a rejection costs nothing already merged. |
| Dev A's review round slips the phase | 4 | 2 | 8 | D8 — Task 6 is severable. The reported bug closes at Task 3. |
| Shedding actors hides a bandwidth regression behind "it always sends something" | 3 | 4 | 12 | Assert the shed count in the density sweep, not just that a snapshot was produced. A non-zero shed count on Dustbowl at 48 actors is a failure, not a pass. |
| Per-client shed cursor is new per-session state that can desync from the ack baseline | 2 | 4 | 8 | The cursor only picks *order*; correctness comes from the baseline, which is untouched. Starvation test covers the ordering. |
| `WeaponConfig.Rifle` placeholder numbers diverge from Dev A's real weapon assets | 3 | 2 | 6 | Documented in the brainstorm report; the seam takes a `WeaponConfig`, so swapping the numbers is data, not code. |
| D9 collapses a serialized field, so any scene or prefab that had authored a `NetServerActor` health value loses it | 3 | 3 | 9 | The value was never read by anything — nothing writes `_health` today, so no authored value is live. Confirm by inspecting the Dustbowl `NetServer` prefabs before removing the field, and note the removal in the Task 6 PR so Dev A sees it. |
| Observer overload silently breaks an existing movement test | 2 | 3 | 6 | The three-argument overload is preserved; movement tests are not edited. |
| Client predicts a 2 s reload, the server confirms one RTT later | 4 | 1 | 4 | Already reconciled client-side, and both sides now read one constant. |

No score reaches the 15 threshold except the `Actor.Damage` guard, whose mitigation (severable last
task + offline no-op test) is a precondition of starting Task 6.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — Engine-free combat core | M (2d) | No dependencies. Start here. |
| 2 — Input seam | S (1d) | Independent of Task 1; can run alongside. |
| 3 — Unity wiring | M (2d) | Needs 1 and 2. **The reported bug closes here.** |
| 4 — Snapshot overflow | M (2d) | Independent of 1-3 entirely. |
| 5 — Tests | M (2d) | Written alongside 1-4, not after. |
| 6 — `Actor.Damage` guard | S (1d) + review round | Severable, last. |
| 7 — Client minors M7-M10 | S (0.5d) | Independent. |
| **Total** | **~2 weeks** | Critical path: 1 → 3. Tasks 4 and 7 are off the critical path. |

---

## 7. Handoff

To **Dev A**: one PR against `Assembly-CSharp/Actor.cs` (Task 6), one method, with the offline
no-op test attached. Nothing else in this phase touches a Dev A file.

To the **Editor phase**: the backend is combat-complete and the debt list is empty, so the only
things left needing Unity are the ones that always did — **S5** (32-bot p99, the last criterion that
can still *fail* rather than merely be unmeasured; `BotLodGate` has unblocked it), and the S4 / S7
evidence already recorded on 2026-08-15.

Still outside Dev C: **B7** (a player id on `ConnectionInfo`, Dev B) and confirming the server
appears in the master's list (Dev D).
