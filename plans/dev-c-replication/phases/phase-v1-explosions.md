# Dev C — Phase V1: Explosions, or connecting a wire that has been complete and dead since phase-02

> Design of record: [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md)
> § 2.2 and § 6. Read it first — it carries the audit that found this, and the decisions D1-D8
> nobody should re-litigate.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files, `Span<byte>` over
> `byte[]`) and § 7 (ownership). Per the design doc § 7, Dev C writes every file in this phase
> including the ones under `Assembly-CSharp/`; Dev A owns only the Editor half.
>
> **Depends on V0.** Named preconditions are listed in § 3 Task 3. **No wire change** — every byte
> this phase sends is already defined, already implemented, and already conformance-tested.

---

## 1. Objectives

`S_EXPLOSION` is the cleanest instance of `rules/wired-not-just-present.md` in the repository. Four
pieces exist and pass their tests:

| Piece | Where | State |
|---|---|---|
| `ServerMessageType.Explosion = 0x4A` | `Ironfront.Net.Protocol/Enums/MessageTypes.cs:52` | declared |
| `ExplosionMessage`, 10 bytes | `Ironfront.Net.Protocol/Messages/ActorLifecycleMessages.cs:152-200` | implemented, codec-tested |
| `ServerEventWriter.WriteExplosion` | `Ironfront.Net.Replication/Server/ServerEventWriter.cs:98` | implemented, **zero call sites** |
| `ClientMessageRouter.OnExplosion` | `Ironfront.Net.Replication/Client/ClientMessageRouter.cs:107`, raised at `:214` | implemented, **zero subscribers** |

`ActorLifecycleMessageTests.cs:153-196` round-trips the struct. It proves the codec and proves
nothing about the game: no explosion in this build has ever produced a byte on the wire.

By the end of this phase:

1. `ServerEventWriter.WriteExplosion` has a caller and `ClientMessageRouter.OnExplosion` has a
   subscriber, and a single test walks the whole path rather than each half separately.
2. `ActorManager.Explode` (`Assembly-CSharp/ActorManager.cs:341-373`) is role-aware: the server
   decides damage, the client runs cosmetics only, and offline single-player is unchanged.
3. Vehicles inside a blast take damage with an attacker id attached, using V0's new signature.
4. `ServerEventWriter.ExplosionAudibleRadius` — a constant with no consumer since phase-02 — gets
   one.

**Not in this phase.** No Editor session. No projectile replication (V7 owns `S_PROJECTILE_SPAWN`).
No vehicle health replication (V4). `ExplosionKind.Vehicle` and `ExplosionKind.Environment` stay
uncalled, deliberately, and § 2 D5 says why.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **D1** | **The role guard goes on `ActorManager.Explode`, not on its callers.** There are three call sites today (`ExplodingProjectile.cs:66`, `GrenadeProjectile.cs:58`, and the shell path that reaches the first) and V7 adds more. Guarding the one choke point is the identical argument that put phase-05's guard on `Actor.Damage` rather than on each of the six damage sources that funnel into it. |
| **D2** | **Explosion damage to actors needs no new code at all.** `Explode` already calls `item.Damage(...)` at `ActorManager.cs:353`, and phase-05 Task 6 already put an authoritative guard at the top of `Actor.Damage` (`Assembly-CSharp/Actor.cs:778`) that routes the health change through `IActorDamageSink` on the server. Actor blast damage becomes authoritative the moment phase-05 Task 6 is on `develop`. Adding a second explosion-specific damage path would be the two-writers divergence phase-05 D9 exists to forbid. **What remains is the vehicle loop, the emit, and the client suppression.** |
| **D3** | **The blast geometry stays in `ActorManager`, unported.** The falloff is an `AnimationCurve` inside `ExplodingProjectile.ExplosionConfiguration`, authored per prefab by Dev A; porting the selection to `Ironfront.Net.Replication` would mean re-authoring every explosive's curve as data the server can read. That is a real fight and V2 fights it for weapons; it is not worth re-fighting here for a method that already walks a registry rather than a `Physics.OverlapSphere` and is therefore already server-shaped. **What is ported is the decision "does this damage land" (the sink) and "who hears about it" (the emit)** — both of which are CI-gradeable. |
| **D4** | **The emitted radius is sourced from the same field the damage loop selected on**, passed as an explicit parameter, so the wire radius and the damaging radius cannot drift apart by being read independently. V0 owns the 6 m / 9 m asymmetry between the actor selection (`balanceRange`) and the vehicle selection (`damageRange`); V1 quantizes whatever V0 settled and does not re-decide it. |
| **D5** | **V1 wires `ExplosionKind.Grenade` and `Rocket` only.** `Vehicle` has no caller because `Vehicle.Explode()` (`Vehicle.cs:384-394`) is an impulse plus particles and does not call `ActorManager.Explode` — vehicle wrecks do zero blast damage in the original game, and V1 does not add any. `Environment` has no source in scope. Both are V4/V7's to connect. Naming this explicitly is the point: leaving an enum member uncalled *without saying so* is how § 2.2 happened in the first place. |
| **D6** | **In a client build, every explosion cosmetic comes from `S_EXPLOSION`, including your own grenade's.** The client's local `ActorManager.Explode` plays no effect. One source of truth for the cosmetic, for the same reason D2 gives one source of truth for the damage. **Accepted cost:** your own explosive's boom is delayed by roughly RTT/2. That is the same latency its damage already has, so the flash and the kill now agree instead of the flash leading it. Fallback if it feels bad in V9: play locally and suppress the matching `S_EXPLOSION` by `SourceActorId` — one branch in the presenter, recorded here so it is not re-derived. |
| **D7** | **`S_EXPLOSION` is filtered by earshot, not broadcast.** `EmitDeath` broadcasts because the killfeed is global; an explosion 900 m away is not. `ServerEventWriter.ExplosionAudibleRadius = 200f` and `ServerTickLoop.SendToListenersInEarshot` (phase-05 Task 3) both already exist and have been waiting for each other. Reliable, on channel 2, because the class doc at `ServerEventWriter.cs:91-96` already argues it: a missed muzzle flash is invisible, a missed explosion is a player dying to nothing. |
| **D8** | **`ExplodingProjectile.Hit` is left not chaining to `base.Hit`.** It overrides without calling base (`ExplodingProjectile.cs:42-60`), so piercing, hitbox resolution and impulse never run for shells and their only actor-damage path is `ActorManager.Explode`. Chaining would change single-player behaviour and double-apply damage (direct hit **and** blast). Recorded so it is not later reported as a bug. The consequence is favourable: making `Explode` authoritative is *sufficient* to make shell damage authoritative, and no second seam is needed. |

---

## 3. Detailed tasks

### Task 1 — Prove the wire, and add the one piece that is genuinely missing (0.5 day)

The framing and the routing are both correct as shipped. What does not exist is (a) a test that
walks from `WriteExplosion` to `OnExplosion` in one go, and (b) a place to turn a float radius into
the message's `byte RadiusMetres` without each caller inventing its own rounding.

| File | Change |
|---|---|
| `Ironfront.Net.Replication/Combat/ExplosionEncoding.cs` | **New.** `public static byte PackRadiusMetres(float radiusMetres)` — `ceil`, clamped to `[0, 255]`. Ceil rather than round so a client's effect never renders smaller than the blast that hurt the player. Also `public static float UnpackRadiusMetres(byte)`, so the presenter does not open-code the inverse. |
| `Ironfront.Net.Replication/Server/ServerEventWriter.cs` | **No change.** Listed so a reader does not go looking for one. |
| `Ironfront.Net.Replication/Client/ClientMessageRouter.cs` | **No change.** Same reason. |

**Constraint.** `ExplosionEncoding` is a static class of pure functions, no state, no allocation.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~ExplosionEvent`
— red until Task 5's `AnExplosionFramedByTheServerRoutesToTheClientHandler` and
`AnExplosionRadiusSaturatesRatherThanWrapping` exist and this file backs them.

---

### Task 2 — The server caller (1 day)

Dev C-owned Unity files only. Mirrors `ServerTickLoop.EmitDeath` (`:497-522`) and
`ServerCombatEvents.ReportDeath` line for line, because the shape is already established and a
second shape would be a second thing to be right about.

| File | Change |
|---|---|
| `Net/Server/ServerTickLoop.cs` | **New method** `EmitExplosion(ushort sourceActorId, Vector3 centre, float radiusMetres, ExplosionKind kind)`. Quantizes `centre` with `Quantize.PackPos`, the radius with `ExplosionEncoding.PackRadiusMetres`, frames via `ServerEventWriter.WriteExplosion` into the existing `_eventPayload` buffer, and sends with `SendToListenersInEarshot(centre, ServerEventWriter.ExplosionAudibleRadius, …, (byte)ServerEventWriter.ReliableChannel, reliable: true)` per D7. No `MarkDeath`, no match report — an explosion is not a death, and the deaths it causes arrive through `Actor.Damage` → phase-05's existing path. |
| `Net/Server/ServerCombatEvents.cs` | **New method** `ReportExplosion(Component source, Vector3 centre, float radiusMetres, ExplosionKind kind)`. No-op when `!NetContext.IsServer`, when `ServerTickLoop.Current` is null, and when `Transport` is null. Resolves `SourceActorId` through `source.GetComponent<NetServerActor>()`, falling back to `DeathMessage.EnvironmentKiller` when the source is the world or is not replicated. Static, for the reason its own class doc gives at `ServerCombatEvents.cs:31-38`: `Actor` has no reference to the tick loop and acquiring one would mean a serialized field on every actor prefab in the game. |
| `Net/Server/ServerActorDamageSink.cs` | **No change.** Listed to pin D2 — explosion damage reuses this sink and does not get a second one. |

**Constraint.** `_eventPayload` is the existing pre-allocated buffer; `EmitExplosion` allocates
nothing. `SendToListenersInEarshot` already compares squared distance, so no `sqrt` per
(event, client) pair.

**Verify:** `dotnet build Ironfront.sln` clean, and
`dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~ExplosionEvent` green —
`AnExplosionIsFramedOnTheReliableChannel` pins the channel and opcode this method uses, so a later
"optimization" onto the cosmetic channel fails the build rather than losing damage silently.

---

### Task 3 — Authority and suppression inside `ActorManager.Explode` (1 day + a Dev A review round)

`Assembly-CSharp/ActorManager.cs` is a Dev A file. Per design doc § 7 this is written here, on the
phase-05 Task 6 precedent, with a PR and one Dev A review round.

**Preconditions, both hard:**

- **phase-05 Task 6 is on `develop`.** Without the `Actor.Damage` guard, D2 does not hold and this
  task ships a half-authoritative blast — actors damaged locally on both sides, vehicles guarded.
  That is worse than shipping nothing, because it looks finished.
- **V0 is on `develop`**, for `Vehicle.Damage(float amount, ushort attackerId)` and for the 6/9 m
  falloff resolution D4 defers to.

Three-way split at the top of `Explode`, matching phase-05 D5 exactly:

| Role | Actor loop (`:345-360`) | Vehicle loop (`:361-371`) | Emit |
|---|---|---|---|
| `NetContext.IsServer` | **unchanged.** `item.Damage(...)` already routes through the sink via phase-05's guard | `vehicle.Damage(amount, sourceActorId)` — V0's signature | `ServerCombatEvents.ReportExplosion(...)` once, at the end, after both loops |
| `NetContext.IsClient` | skip `item.Damage(...)`; **keep** `ApplyRigidbodyForce` on corpses (`:358`) — corpses are never replicated (AD-4), so their ragdoll is legitimately local | skip `vehicle.Damage(...)` entirely | none, and no cosmetic (D6) |
| `NetContext.IsOffline` | unchanged | unchanged | none |

**Why the client vehicle guard matters in a phase where vehicle health is not yet replicated.** It
does not change anything visible in V1. It is forward compatibility bought at the cost of one
branch: when V4 starts streaming vehicle health, an unguarded client loop would subtract damage
locally *and* receive the authoritative value, and the symptom would be a vehicle whose health bar
stutters — a bug that would be attributed to V4's interpolation rather than to a line written here.

**Two allocations, removed while we are in the method.** `ActorsInRange` allocates a
`new List<Actor>()` per call (`:276-287`) and `Explode` allocates again with
`instance.vehicles.ToArray()` (`:361`). This is not the 30 Hz tick path, so § 3.2's no-allocation
rule does not strictly bind — but a grenade volley is exactly when a GC spike is least welcome, and
the fix is one field each. Add a non-allocating overload
`ActorsInRange(Vector3 point, float range, List<Actor> into)` and keep the allocating one for its
existing callers; iterate `instance.vehicles` by index rather than copying it. Both loops become
indexed `for`, satisfying the no-`foreach` convention on a path this phase is touching anyway.

**Verify:** `dotnet build Ironfront.sln` clean (Unity assembly compiles under the batch check in
`tools/ci.ps1` step 4 where Unity is available; the C# is checked by the editor-independent build
otherwise), plus Task 5's `AnExplosionEmitsExactlyOneEventPerBlast` and
`AClientRoleExplosionAppliesNoDamage` driven through a test double of the sink — neither needs the
Editor, because both assert against `IActorDamageSink` and `ServerEventWriter`, not against
`ActorManager`.

> **Honest limit.** The role split *inside* `ActorManager.Explode` is Unity code and cannot be
> executed in CI. What CI grades is the contract on both sides of it: that the sink receives damage
> exactly once per victim per blast, and that a framed explosion round-trips. The branch itself is
> graded by the Dev A review round and by V9 criterion 11.

---

### Task 4 — The client subscriber (0.5 day)

| File | Change |
|---|---|
| `Net/Client/NetClientExplosionPresenter.cs` | **New**, Dev C-owned. Subscribes `_client.Router.OnExplosion` in `OnEnable` and unsubscribes in `OnDisable`, the same lifecycle `RemoteActorRegistry.cs:77` already uses for `OnSpawnActor`. Guarded on `NetContext.IsClient`. |

On each message: unpack the centre with `Quantize.UnpackPos`, the radius with
`ExplosionEncoding.UnpackRadiusMetres`, index a serialized `ParticleSystem[]` by
`(byte)message.Kind` for the effect, scale the effect and the camera shake by the radius, and apply
the corpse ragdoll impulse locally per D6. **It applies no health damage** — health arrives in the
snapshot, exactly as phase-05 D5 established for bullets.

Guard every array index and every serialized reference: a `Kind` this build does not know must draw
nothing rather than throw, for the same reason `WeaponIds.NameOf` returns empty rather than
throwing — a newer server shipping a fifth explosion kind should cost one missing effect, not a
dropped payload.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~ExplosionEvent`
green — `AnExplosionFramedByTheServerRoutesToTheClientHandler` grades the router half in CI. The
presenter's prefab wiring is Dev A's (§ 7 handoff) and is graded in the two-client Editor test.

---

### Task 5 — Tests (0.5 day, written alongside Tasks 1-4)

All engine-free, all in CI, no Editor. New file
`Ironfront.Net.Replication.Tests/ExplosionEventTests.cs`.

| Test | Asserts |
|---|---|
| `AnExplosionFramedByTheServerRoutesToTheClientHandler` | `WriteExplosion` → the raw payload bytes → `ClientMessageRouter` → `OnExplosion` fires once with byte-identical fields. **The test § 2.2 says has never existed** — the codec test covers the struct, this covers the join, and the join is what was dead. |
| `AnExplosionIsFramedOnTheReliableChannel` | channel 2 and opcode `0x4A` in the framed header, so D7 cannot be quietly reversed |
| `AnExplosionRadiusSaturatesRatherThanWrapping` | `PackRadiusMetres(300f) == 255`, not 44; `PackRadiusMetres(6.1f) == 7` (ceil, per Task 1) |
| `AnExplosionEmitsExactlyOneEventPerBlast` | one blast damaging four actors and two vehicles produces one `ExplosionMessage`, not six. The per-victim/per-blast confusion is the same shape as phase-05's edge-triggered `DamageOutcome.Died` |
| `AClientRoleExplosionAppliesNoDamage` | with a fake sink, a client-role blast records zero `ApplyDamage` calls and a server-role blast records one per live victim (D2/D5) |
| `AnExplosionOutsideEarshotIsNotSent` | a listener at 250 m receives nothing, at 150 m receives one — `ExplosionAudibleRadius`'s first assertion in the repository |
| `AnUnknownExplosionKindDoesNotThrow` | `(ExplosionKind)9` parses, routes, and is handled — the forward-compatibility rule Task 4 states |

**Verify:** `dotnet test Ironfront.Net.Replication.Tests` green, and `dotnet test` green across the
solution.

---

## 4. Acceptance criteria

1. `ServerEventWriter.WriteExplosion` has at least one production call site and
   `ClientMessageRouter.OnExplosion` has at least one production subscriber.
   `grep -rn "WriteExplosion\|OnExplosion" --include=*.cs .` returns hits outside the test
   projects — which it does not today.
2. One test walks server framing to client handler in a single method, and it is green.
3. A blast damaging N actors emits exactly one `ExplosionMessage`.
4. Explosion damage to an actor moves the same authoritative health that a bullet moves — one sink,
   one health field (phase-05 D9 preserved, not forked).
5. A vehicle damaged by a blast records the attacker id V0's signature carries.
6. At the client role, `ActorManager.Explode` applies zero health damage and still applies corpse
   ragdoll impulse.
7. Offline single-player behaviour through `ActorManager.Explode` is byte-for-byte unchanged; the
   guard is an explicit early no-op at `NetRole.Offline`.
8. An explosion beyond `ExplosionAudibleRadius` is not sent to that listener.
9. `ExplosionKind.Vehicle` and `Environment` remain uncalled **and are named as such in the phase
   report**, with their owning phase. An uncalled enum member that nobody writes down is how this
   phase came to exist.
10. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-blast
    allocation in `ActorManager.Explode`.
11. `PROTOCOL_VERSION` is unchanged and `tools/SpecChecker` passes untouched. This phase sends no
    byte that was not already specified.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| phase-05 Task 6 has not merged when Task 3 lands, so D2 is false and the blast is half-authoritative while looking finished | 2 | 5 | **10** | Named as a hard precondition in Task 3, checked in the PR description. `AClientRoleExplosionAppliesNoDamage` fails loudly if the guard is absent, because the fake sink records nothing on either side. |
| `ExplodingProjectile.Explode` NREs on the headless server at `impactParticles.Play` / `audioSource` (`:75-79`), leaking the projectile GameObject | 4 | 3 | **12** | Two structural halves. (a) **Ordering already protects the network event**: `ActorManager.Explode` runs at `ExplodingProjectile.cs:66`, *before* the cosmetic block, so the damage and the emit have both completed before anything can throw. (b) The two sites are handed to **V0**, which already owns the § 3.6 headless sweep — see § 7. V9 criterion 11 grades the result. |
| The emitted radius and the damaging radius are read from different fields and drift | 3 | 3 | 9 | D4 — `ReportExplosion` takes the radius as an explicit parameter sourced at the same place the selection used, so they cannot be read independently. V0 owns the 6/9 m asymmetry; V1 does not re-decide it. |
| Your own grenade's boom is delayed by RTT/2 and feels wrong (D6) | 3 | 2 | 6 | Accepted and recorded. The fallback is one branch in the presenter keyed on `SourceActorId`, written down in D6 so V9 can apply it without re-deriving it. |
| `ExplosionKind.Vehicle` / `Environment` ship uncalled, reproducing § 2.2 one level down | 3 | 2 | 6 | D5 names them, criterion 9 forces the phase report to name them, and their owning phases (V4, V7) are recorded. The defect in § 2.2 was not the uncalled code — it was that nothing said it was uncalled. |
| `ActorManager.cs` conflicts with Dev A's branch | 3 | 3 | 9 | One method, one review round, announced in the PR. Design doc § 9 already sequences V0's `Actor.cs` edit early for the same reason; this rides behind it. |
| Earshot filtering hides an explosion whose *damage* reached further than 200 m | 2 | 3 | 6 | No explosive in scope has a `balanceRange` beyond 9 m; `ExplosionAudibleRadius` is 200 m, a 22x margin. `AnExplosionOutsideEarshotIsNotSent` pins the behaviour, and the margin is recorded here so a future 250 m weapon does not silently inherit the filter. |
| Removing the two allocations changes `ActorsInRange` behaviour for its other callers | 2 | 3 | 6 | The allocating overload is kept unchanged; only `Explode` moves to the buffered one. A pre-delete reference grep on `ActorsInRange` runs before the edit, per `development-principles.md`. |

No score reaches 15. The two at 10-12 are both dependency risks rather than design risks, and both
are discharged by sequencing rather than by new code.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — `ExplosionEncoding` + wire proof | S (0.5d) | No dependencies. Start here; it is the only file that unblocks the tests. |
| 2 — `EmitExplosion` + `ReportExplosion` | S (1d) | Needs Task 1's `PackRadiusMetres`. |
| 3 — `ActorManager.Explode` role split | S (1d) + review round | **Blocked on V0 and on phase-05 Task 6.** The only Dev A review in this phase. |
| 4 — Client presenter | S (0.5d) | Independent of Task 3; needs Task 1. |
| 5 — Tests | S (0.5d) | Written alongside 1-4, not after. |
| **Total** | **~3.5 days (M)** | Critical path: 1 → 2 → 3. Task 4 runs in parallel with 2 and 3. |

Off the vehicle critical path entirely (design doc § 6) — this can land while V3's protocol review
is open.

---

## 7. Handoff

**To V0** — two additions to its § 3.6 headless-NRE list, found while auditing this path and not in
the design doc's table:

| Site | Call |
|---|---|
| `ExplodingProjectile.cs:75-79` | `impactParticles.Play(true)`, `audioSource.Stop()` / `.pitch` / `.Play()` — all unguarded (contrast `trailParticles` at `:72`, which *is* guarded) |
| `GrenadeProjectile.cs` | the same audio-pitch roll on the shared `UnityEngine.Random` stream (design doc § 3.3) |

They belong in V0 rather than here because V0 already owns the sweep and lands first; V1 only found
them.

**To Dev A** — three things, all Editor-only:

1. One PR review round on `Assembly-CSharp/ActorManager.cs` (Task 3): one method, three branches,
   with the offline branch an explicit early no-op.
2. `.meta` for `Net/Client/NetClientExplosionPresenter.cs`. Note that
   `Net/Server/ServerActorDamageSink.cs`, `ServerCombatBridge.cs` and `ServerCombatEvents.cs` are
   still missing `.meta` files from phase-05 — the same pass should pick them up.
3. Prefab wiring for the presenter's `ParticleSystem[]` indexed by `ExplosionKind`. Indices 0
   (`Grenade`) and 1 (`Rocket`) are the only two V1 uses; 2 (`Vehicle`) and 3 (`Environment`) may be
   left empty and the presenter must not throw on them.

**To V4** — `ExplosionKind.Vehicle` is the caller you are missing. `Vehicle.Explode()`
(`Vehicle.cs:384-394`) does not call `ActorManager.Explode` and therefore does no blast damage in
the original game; if V4 wants a wreck to hurt people, that is a gameplay change and needs its own
decision, not an assumption.

**To V7** — `ExplosionKind.Environment` is yours, and `S_PROJECTILE_SPAWN` is the message that makes
a client-thrown grenade reach the server's `Explode` in the first place. Until V7, the sources
exercising this phase are bot-thrown grenades and shells, both of which already run on the server.

**Still outside Dev C:** nothing in this phase.
