# Dev C — Phase V10: The client half of combat, which was never built

> ## ⚠ Execution order — run this **immediately after V0**, **before V3**.
>
> **The filename sorts last. The phase does not run last.** V10 is numbered 10 because it was added
> after the design of record was written and approved, not because it comes after V9. Its slot is:
>
> ```
> V0  →  V10  →  V1 / V2 / V8 (parallel)  →  V3  →  V4  →  V5 / V6 / V7  →  V9
>        ↑ here
> ```
>
> Running it late would mean building vehicles, mounted weapons and projectiles on top of a client
> that cannot render a death, a muzzle flash, a hitmarker, a score or a capture point — so every
> defect found in V4-V7 would be indistinguishable from the ones this phase closes.

> Design of record:
> [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md)
> § 2.4 and § 6. **This phase is not in that document's § 6 phase table.** It was approved on
> 2026-08-17 after the gaps in § 1 were found by grep and verified at source; this file is the record
> of that addition.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files, `Span<byte>` over
> `byte[]`) and § 7 (ownership). Per design § 7, Dev C writes every file here including those under
> `Assembly-CSharp/`; Dev A owns only the Editor half, enumerated as **E1-E11** in § 7.
>
> **Depends on V0.** **No wire change.** Every byte this phase consumes is already defined, already
> implemented, already conformance-tested, and already being sent by a shipped server.

---

## 1. Objectives

### 1.1 Six of nine router events are dead

`ClientMessageRouter` raises **nine** events. **Three** have a production subscriber. **Six** have
none.

| Event | Declared | Production subscriber |
|---|---|---|
| `OnSpawnActor` | `ClientMessageRouter.cs:66` | `RemoteActorRegistry.cs:77` |
| `OnDespawnActor` | `:69` | `RemoteActorRegistry.cs:78` |
| `OnSnapshotApplied` | `:114` | `ClientPredictionStage.cs:76` |
| **`OnHitConfirm`** | **`:79`** | **none** (tests only: `ClientCombatTests.cs:522`, `:562`) |
| **`OnDeath`** | **`:89`** | **none** (tests only: `ClientCombatTests.cs:523`) |
| **`OnWeaponFire`** | **`:98`** | **none** (tests only: `ClientCombatTests.cs:524`) |
| **`OnMatchState`** | **`:101`** | **none — no subscriber anywhere, not even a test** |
| **`OnCapturePoint`** | **`:104`** | **none — no subscriber anywhere, not even a test** |
| **`OnExplosion`** | **`:107`** | **none — no subscriber anywhere, not even a test** |

Scope of that negative result: a grep for all nine names over `**/*.cs` across the whole repository
root, including `Ironfront_Reborn/Assets/Scripts/**`, every `Ironfront.Net.*`, every `*.Tests`, and
`tools/`.

### 1.2 The client throws away the snapshot fields it already receives

`RemoteActorRegistry._live` is a `Dictionary<ushort, Transform>` (`:49-50`) — a raw `Transform`, not
an `Actor`, not any component. Its interpolation loop (`:105-113`) applies **exactly two fields**:
`position` from `TryLerpPosition` (`:107-108`) and `rotation = Quaternion.Euler(0f, yaw, 0f)` from
`TryLerpYaw` (`:110-111`).

Meanwhile `ActorSnapshotEntry` already carries **Pitch, VelX/Y/Z, StateFlags** (`IsAlive`,
`IsCrouching`, `IsProne`, `IsSprinting`, `IsAiming`, `IsInWater`, `IsRagdoll`, `IsSeated`),
**Health, WeaponId, AmmoInClip and Team** — and `DeltaDecoder` decodes all of it. Every one of those
fields is decoded and discarded. **Remote players today slide at a fixed pose: never crouch, never
aim, never ragdoll, always the same weapon.**

This is why the phase is not "wire up six subscribers". A muzzle flash needs a weapon transform and a
death needs a ragdoll, and neither exists on a bare pooled `Transform`. **§ 3 sequences a remote-actor
representation first, and the event layer on top of it.**

### 1.3 Two client-side objective bugs on the same path

- **`MinimapUi.UpdateSpawnPointButtons` is hardcoded to team 0.** `MinimapUi.cs:129` declares
  `int num = 0;` and never reassigns it; `:140` sets `button.interactable = owner == num`. In the
  original single-player game the human is always team 0, so this was invisible. **In multiplayer it
  disables the respawn UI for every team-1 player.**
- **An empty catch is hiding it.** `CapturePoint.cs:262-268` calls `UpdateSpawnPointButtons()` inside
  `try { … } catch (Exception) { }` with an empty body — against
  `development-principles.md` § "Errors Over Silent Fallbacks". And the null guard is incomplete:
  `:125` checks `instance == null` but `:130` dereferences `instance.minimapSpawnPointButton`, which
  `SetupMinimap()` (`:58`, dict at `:67`) builds later — so a flag flip landing first throws an NRE
  that the empty catch then eats.

### 1.4 What this adds up to

The server half of phase-05 (combat) and phase-03 (capture points) shipped, and the client half was
never built. This is [`wired-not-just-present.md`](../../../.claude/rules/wired-not-just-present.md)
at six-event scale, plus a representation layer that decodes fields it discards.

Three comments in shipped code are promises this phase keeps:

- `ServerActorDamageSink.cs:69-73` — *"`Actor.Die()` is deliberately NOT called … The death
  choreography is per-client anyway — corpses are never replicated (AD-4), so each client runs its
  own ragdoll off `S_DEATH`."* No client runs anything off it today.
- `ServerEventWriter.WeaponFireAudibleRadius` — earshot filtering, implemented in phase-05 for an
  audience that does not exist.
- `ClientMessageRouter.cs:95-97` — weapon fire is *"a cue to play an effect, never a fact to
  accumulate"*. Nothing plays any effect.

By the end of this phase:

1. A remote player crouches, aims, holds the right weapon, and ragdolls — from snapshot fields that
   already arrive.
2. A remote player's shot produces a muzzle flash, a report and a tracer on every client in earshot.
3. A death produces a ragdoll driven by the **replicated** force vector, at the **replicated** hitbox.
4. A hit produces a hitmarker on the shooter's screen and on nobody else's.
5. Score, tickets, phase and the phase timer render from the server's authoritative numbers.
6. Capture points render, and **a team-1 player can select a spawn point**.
7. An explosion is seen by everyone, and **your own is seen immediately** rather than one RTT later.
8. A **regression gate** fails the build when any router event loses its last production subscriber.

**Not in this phase.** No protocol change — not one byte moves that is not already specified and
conformance-tested. No server-side emit work: V1 owns the explosion emitter, phase-05 shipped the
other five. No vehicles, seats or projectiles. No `ScoreUi` scoring redesign — V8 D9 recorded that as
a deliberate divergence and this phase respects the boundary rather than reopening it. **No killfeed
names** — see § 7's recorded gap.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **D1** | **The representation layer comes first, and the event layer sits on it.** Task 2 builds `RemoteActorView` — a component on the pooled remote prefab that consumes the snapshot fields `DeltaDecoder` already produces (§ 1.2) and exposes the transforms and state the cosmetics need. Every task from 4 onward depends on it. Wiring six subscribers onto a bare `Transform` would produce six handlers with nothing to drive. |
| **D2** | **Three presenter components, not six, not one.** `NetClientCombatPresenter` (death, weapon fire, hit confirm), `NetClientObjectivePresenter` (match state, capture points), `NetClientExplosionPresenter` (explosions). Each caches `NetClientBootstrap.Current` in `Awake` and subscribes in `OnEnable` / unsubscribes in `OnDisable` — byte-for-byte the lifecycle `RemoteActorRegistry.cs:60-85` and `ClientPredictionStage.cs:76-82` already use. Six components would be six `.meta` files and six prefab wirings; one would carry every serialized reference in the build on a single inspector. |
| **D3** | **The engine-free models are the policy half and the presenters are thin adapters.** New models live in `Ironfront.Net.Replication/Client/`, which `Ironfront.Net.Replication.Tests` already references — so they are CI-testable with no new wiring. The presenters hold only Unity calls. The **linked-source pattern** (`Ironfront.Client.Flow.Tests`, `Ironfront.Client.Input.Tests`) is deliberately **not** used: it forbids `using UnityEngine;` in a linked file, and every presenter needs it. The split by assembly achieves the same testability without that constraint. |
| **D4** | **Death arrives from the death message and is never inferred from health reaching zero.** phase-05 D5 has **landed** — `Actor.cs:789` reads `bool ownsHealth = !NetContext.IsClient;`, so health mutation and `Die()` are client-skipped while `ReceivedDamage`, blood decals, ragdoll force and knockback still run. A client-role actor therefore takes hits, bleeds, and **never dies**. This phase closes that half-open state and builds on the guard rather than replacing it. |
| **D5** | **`DeathMessage.HitboxHit` is consumed, not left unused, and it is consumed without touching `Actor.cs`.** The byte is on the wire (`protocol-spec.md:457`) with no consumer today. `Actor.ApplyRigidbodyForce` (`:641-644`) is hardcoded to `ActiveRaggy.MainRigidbody()` = `rigidbodies[0]`, `ForceMode.Impulse` — that is the **local/offline** path and stays exactly as it is. V10's **remote** corpse path is the presenter's own (D4: `Die()` is `private`), so it selects the rig body matching `HitboxHit` itself and falls back to the root body when the rig has no such bone. Leaving the byte unconsumed would reproduce design § 2.2's failure shape one field down. |
| **D6** | **Remote weapon cosmetics never enter `Weapon`.** `Weapon.SpawnProjectile` sets `component.source = user` (`Weapon.cs:392`), so a naive cosmetic spawn on a client does **real damage**; `Weapon.Shoot` is `protected` (`:321`) and `Fire` is gated by `CanFire()` (`:306-309`) requiring `unholstered` plus ammo. Rather than widen either, the presenter plays flash, report and tracer from its own serialized references on `RemoteActorView`, indexed by the replicated `WeaponId`. **No cosmetics path is reachable from the authority path, because there is no shared entry point at all.** |
| **D7** | **A cosmetics-only tracer is new work.** No tracer system exists — scope: `[Tt]racer`, `TrailRenderer`, `LineRenderer` across `Assets/Scripts/`. The visible streak in the original game **is** the `Projectile`, which D6 forbids. So Task 5 ships a pooled, non-damaging streak owned by the presenter. This is honest new work and is in the estimate. |
| **D8** | **V10 does not read or write `aiControlled`.** It is frozen in `Awake` from `controller.GetType()` (`Actor.cs:178`) and gates shell casings (`Weapon.cs:350`), reverb (`:362`), the ammo HUD and the health HUD — and it has no correct value for a remote actor. V10 drives every remote cosmetic from `RemoteActorView` and the replicated fields instead, so it neither needs a value nor changes one. **This does not contradict V5-D7**, which pins a test that `aiControlled` is *unchanged* for a networked driver: V5 guards against tripping the flag, V10 simply never consults it. |
| **D9** | **Your own explosion is predicted locally and the confirming message is suppressed by `SourceActorId`.** This **overrides V1 D6**, which chose server-sourced-for-everyone. It is not a contradiction: **V1 D6 records this exact mechanism as its own named fallback** — *"play locally and suppress the matching `S_EXPLOSION` by `SourceActorId` — one branch in the presenter, recorded here so it is not re-derived."* The consumer took that branch on 2026-08-17, before V9 rather than after. **Accepted cost:** a prediction the server never confirms (a grenade destroyed in flight) shows a phantom blast with no damage. Bounded by the window in Task 9. |
| **D10** | **V10 owns `NetClientExplosionPresenter.cs` outright; V1 Task 4 is superseded.** Two phases cannot create one file, and V10's version carries D9's prediction branch that V1's does not. V1 keeps Tasks 1, 2, 3 and 5 — encoding, emit, `ActorManager.Explode` authority, tests — untouched. **This needs a one-line amendment to V1**, listed in § 7 rather than silently assumed. |
| **D11** | **Capture-point consumption writes through V8's `ApplyAuthoritativeOwner`, so Task 7 is blocked on V8 Task 1.** V8 D3 makes that the single write path for `owner`, `control`, `pendingOwner` and `isContested`, and already names *"the client's capture-point message handler"* as one of its two callers. Landing Task 7 first would add a second client-side writer while `UpdateOwner`'s 1 Hz arithmetic still runs there (V8 D2 is what stops it) — design § 2.1's bug, one process further out. Task 7 is severable and last. |
| **D12** | **`MinimapUi.UpdateSpawnPointButtons` gains a `localTeam` parameter; it stops guessing.** The no-arg overload is preserved and resolves the team at the call site — the local actor's replicated `Team`, at `NetRole.Offline` the literal `0`. **Offline single-player is therefore byte-for-byte unchanged**, because the human there *is* team 0 and `num = 0` was accidentally correct. Same additive-overload shape as `IngameUi.Hit` in Task 5, and the same promise. |
| **D13** | **The local team comes from the replicated snapshot, not from `FpsActorController.playerTeam`.** That field is documented as staying `-1` on a server, and V10 needs a value that is correct at the client role **before the first flag flip**. `ActorSnapshotEntry.Team` for `NetClientBootstrap.LocalActorId` is authoritative, arrives at spawn (which precedes any capture), and is the same number the server used. When no snapshot has arrived yet the buttons stay non-interactable and the method logs once — it does **not** fall back to team 0, because that is the bug. |
| **D14** | **The hitmarker is shooter-only and stays that way.** The hit-confirm message is already sent to the shooter alone (phase-05 Task 3). Rendering it for anyone else would tell a player that someone, somewhere, hit something — a server-served wallhack. Recorded because "why does only one client get this event" is exactly the question a future reader answers by broadcasting it. |
| **D15** | **The HUD consumes the server's authoritative numbers and revives none of `ScoreUi`'s own.** `ScoreUi.cs:46-57` documents itself as holding match state in a UI component and notes the original neither scores nor ends headless. V8 D9 recorded that as a deliberate divergence and declined to fix it. V10 renders the five `MatchStateMessage` fields and touches neither `ScoreMultiplier`, nor `victoryPoints`, nor `AddFlag`'s arithmetic. |
| **D16** | **`CombatFeed.cs` is reused verbatim for the hitmarker and the killfeed line, and extended for nothing.** `HitmarkerModel` and `KillfeedModel` (phase-02 task 6) already consume the wire structs, are allocation-free, and carry severity and expiry. **But `KillfeedEntry.From` (`CombatFeed.cs:159-166`) drops `ForceX/Y/Z` and the raw `HitboxHit`** — so the ragdoll cannot be fed from `KillfeedModel`. The fork is resolved explicitly: **the death presenter subscribes `OnDeath` directly for the impulse and pushes the same message into `KillfeedModel` for the line.** One message, two consumers, `CombatFeed.cs` unchanged. |
| **D17** | **New models go in sibling files, not into `CombatFeed.cs`.** It is already 271 lines against the repo's ~200-line convention, and match state, capture points and explosions are not a combat feed. |
| **D18** | **The regression gate enumerates by reflection and detects by source scan.** `typeof(ClientMessageRouter).GetEvents()` works today — that type is engine-free and the test project already references it (precedent: `WeaponIdTests.cs:24-25` does exactly this with `GetFields`). The subscriber side must be a **text scan**, because `Ironfront_Reborn/Assets` contains **zero `.asmdef` files**, so no Unity assembly exists for `dotnet test` to load and CI has no licensed Editor. **A registration manifest was rejected**: a test asserting every event has a manifest entry proves the manifest is complete, not that anything is wired — precisely `green-that-proves-nothing.md`. |
| **D19** | **Every presenter is inert unless `NetContext.IsClient`, every singleton dereference is null-guarded, and no handler ever throws.** `ClientMessageRouter.Route` counts malformed input rather than throwing (`:24-29`); a handler that throws would propagate into the transport pump. `IngameUi.instance`, `ScoreUi.instance`, `MinimapUi.instance` and `DecalManager` do not exist in a stripped headless build. |

---

## 3. Detailed tasks

### Task 1 — The lookup seam and the local-actor identity (0.5 day)

Nothing can be presented until an actor id resolves to something in the scene, and **there is no
public lookup today**: `RemoteActorRegistry`'s entire public surface is `LiveCount` (`:55`) and
`PooledCount` (`:58`).

| File | Change |
|---|---|
| `Net/Client/RemoteActorRegistry.cs` | **Edit**, Dev C. Add `public bool TryFind(ushort actorId, out Transform t)` — a `_live.TryGetValue` pass-through. **Named `TryFind` for symmetry with `ServerActorRegistry.cs:109`'s `public bool TryFind(ushort actorId, out NetServerActor actor)`**, so the two sides of the wire read alike. |
| `Net/Client/NetClientPresenterGuard.cs` | **New**, Dev C. `static bool IsPresentable` (`NetContext.IsClient`), and `static bool TryResolveLocalActorId(out ushort id)`. One place D19's role guard and D13's identity lookup are written, rather than three slightly different copies. |

**Three traps this seam must carry, all verified in `RemoteActorRegistry`:**

1. **The local player is deliberately excluded from `_live`** (`:118`). Every lookup will **miss** the
   local actor. Each presenter must special-case `LocalActorId` on its own — "who fired" and "who
   died" are frequently the local player.
2. **Transforms are recycled.** Despawn deactivates and pushes back to the pool (`:126-133`); spawn
   pops and reactivates (`:121-123`). **Never cache a transform across a despawn.**
3. **`_client` is captured once in `Awake`** (`:62`). If `NetClientBootstrap.Current` is null at that
   moment the subscribe silently no-ops **for the object's whole life** — no error, no log. Every new
   presenter logs once at warning when it resolves null, rather than inheriting that silence.

**Constraint.** No allocation; `TryFind` is a dictionary probe and nothing else. It returns
`Transform` — what is actually stored — not `Actor`: the registry never touches an `Actor` component,
and a `GetComponent` per lookup would be a new per-event cost.

**Verify:** `dotnet build Ironfront.sln` clean; Task 11's `ARemoteActorResolvesFromItsNetworkId` and
`TheLocalActorIsNotInTheRemoteRegistry` compile against the new signature.

---

### Task 2 — `RemoteActorView`: the representation the cosmetics hang on (3 days)

**The critical path, and the task that was not in the original brief.** Per **D1**, everything from
Task 4 onward needs this.

| File | Change |
|---|---|
| `Net/Client/RemoteActorView.cs` | **New**, Dev C. A component on the pooled remote prefab. `Apply(in ActorSnapshotEntry entry)` consumes the fields § 1.2 shows are already decoded and discarded: **Pitch** (aim, driving the upper-body/head bone), **StateFlags** (`IsCrouching`, `IsProne`, `IsSprinting`, `IsAiming`, `IsInWater`, `IsRagdoll`, `IsSeated`, `IsAlive`), **Health**, **WeaponId**, **Team**. Exposes the sockets the event layer needs: `Transform MuzzleSocket`, `Transform HeadSocket`, `Rigidbody[] RagdollBodies`, `byte WeaponId`, `byte Team`. |
| `Net/Client/RemoteActorRegistry.cs` | **Edit**, Dev C. Resolve the `RemoteActorView` **once on spawn** (`:121-123`) into the pooled entry, not per snapshot — `GetComponent` at 30 Hz × 48 actors is the allocation-free-but-slow trap. Feed `view.Apply(entry)` from the existing interpolation loop. |

**What "consume" means per field**, so this does not become an open-ended animation task:

| Field | Rendered as |
|---|---|
| `Pitch` | Aim direction on the upper body; also the origin ray for the tracer in Task 5. |
| `IsCrouching` / `IsProne` | Stance on the animator; also drops the muzzle socket, so a crouched shooter's flash is at the right height. |
| `IsSprinting` / `IsAiming` | Animator parameters only. |
| `IsRagdoll` | Rig enabled/disabled. **This is the field Task 4's death path sets and the snapshot then confirms** — so a death that arrives out of order self-corrects instead of leaving a standing corpse. |
| `IsAlive` | Gates every cosmetic; a dead actor fires nothing. |
| `WeaponId` | Selects the weapon model and the Task 5 flash/report/tracer set. |
| `Team` | Material/insignia, and the value D13 reads for the local actor. |
| `Health` | Nothing visible on a remote actor. **Consumed into the view and deliberately not rendered** — recorded so the next reader does not think it was missed. |
| `IsInWater` / `IsSeated` | **Deliberately not rendered in V10.** `IsSeated` belongs to V5's vehicle work; `IsInWater` has no cosmetic in the original. Named here per V1 D5's rule — an unconsumed field that nobody writes down is how § 2.2 happened. |

**Constraint.** `Apply` allocates nothing and runs per interpolated actor per frame. No `foreach`, no
`System.Linq`. Animator parameters are cached `int` hashes resolved once, not string lookups.

**Verify:** engine-free — `RemoteActorViewStateTests` grade the **decode-to-intent** mapping through
a fake view interface (flags in → stance/aim/ragdoll intent out), which is the half that can be
tested without Unity. The rendering itself is **E7**. `dotnet build Ironfront.sln` clean.

> **Honest limit.** Whether `_remoteActorPrefab` (`RemoteActorRegistry.cs:42`) carries an animator, a
> ragdoll rig, a muzzle socket and a weapon model is **authored in the Editor and cannot be read from
> source**. It is **E1**, blocking, and Task 4's degraded path exists for the case where it is unmet.

---

### Task 3 — The reuse audit, and the models genuinely missing (1.5 days)

`search-before-you-build.md` first. Two of the six streams are **already fully modelled**:

| Existing type | Covers | Verdict |
|---|---|---|
| `HitmarkerModel` (`CombatFeed.cs:88-128`) | hit confirm | **Fit as shipped.** Single-slot latch, newest-wins, `Push(in HitConfirmMessage, uint, float)`, `IsVisible(float)`, `Current`, `Reset()`. **No change.** |
| `KillfeedModel` (`:185-270`) | death, killfeed **line only** | **Fit as shipped** for the line. Fixed ring of 5, newest at index 0, `Prune(float)` compacts rather than truncates. **The caller must run `Prune` once a frame** — the type has no clock by design (`:176-183`). **No change.** |
| `ClientCombatState` (`ClientCombatState.cs:34`) | the **local** player's death, respawn timer, ammo prediction | **Already done.** `ApplyDeath` (`:287`) returns false unless `VictimActorId == LocalActorId` (`:289`); `CanRequestRespawn` (`:296`), `SecondsUntilRespawn` (`:300`). **V10 does not duplicate any of it** — only remote and global consumption is missing. |

So the hitmarker, the killfeed line and the local death screen cost **zero new engine-free code**.
What is genuinely absent goes in sibling files (D17), in `Ironfront.Net.Replication/Client/`:

| File (all new) | Contents |
|---|---|
| `DeathImpulse.cs` | `readonly struct` — `VictimActorId`, `KillerActorId`, `CauseOfDeath`, `Vec3 Force`, `HitboxType Hitbox`, `bool KilledByEnvironment`. `static From(in DeathMessage)`. **This is D16's fork:** `KillfeedEntry.From` drops the force and the hitbox, so the ragdoll is fed from here and the line from `KillfeedModel`, off the same message. |
| `ShotEvent.cs` | `readonly struct` — `ShooterActorId`, `WeaponId`, `Vec3 Direction`. `static From(in WeaponFireMessage)`. **No state and no accumulation**, per the router's own doc (`:95-97`): weapon fire is the one event on the **cosmetic channel** (unreliable-sequenced, ch 1) and is a cue, not a fact. |
| `MatchStateModel.cs` | Latches the last `MatchStateMessage`. `Apply(in, float now)`, `SecondsRemaining(float now)`, `IsStale(float now)`. See Task 6 for the phase-specific timer rule. |
| `CapturePointView.cs` | `Apply(in CapturePointMessage)` into a fixed array indexed by `PointId`; exposes `OwnerQ`, `OwningTeam`, `IsContested`, and `DirtySinceLastRead(int)` so the presenter repaints on change. |
| `ExplosionSuppressor.cs` | D9's mechanism. `PredictLocal(ushort sourceActorId, float now)`, `bool ShouldSuppress(in ExplosionMessage, float now)`. Fixed ring, entries expire after `SuppressionWindowSeconds` (default `1.0f`). |

**The five decode traps, each verified and each silently wrong if missed:**

| # | Trap |
|---|---|
| 1 | **Use `Quantize.UnpackVel16(short)` (`Quantize.cs:130`)** for the death force and the shot direction — **not** `UnpackVel(sbyte)` (`:105`). The `i8` form is the *snapshot's* slot and saturates at 64 m/s; using it would clamp every kill's force and make heavy weapons feel identical to light ones. `PackVel16`'s own doc (`:107-118`) names these two messages explicitly. |
| 2 | **`DeathMessage` is victim-first, killer-second** (`CombatMessages.cs:84-85`). `KillfeedEntry`'s ctor (`CombatFeed.cs:146`) is the **opposite** order. Trivially swappable, silently wrong. |
| 3 | **`DeathMessage.HitboxHit` is a raw `byte`, not `HitboxType`.** Cast at the use site, as `CombatFeed.cs:165` already does. |
| 4 | **Explosion position uses `Quantize.UnpackPos(short)` (`:57`)**, a different pair from the velocity path. |
| 5 | **A capture point can be fully owned *and* contested at once** (`GameplayEnums.cs:182-188`). `IsContested` is not mutually exclusive with `OwningTeam`. |

**Message-type names.** The `S_*` spellings exist only in `protocol-spec.md` § 4.1 and in doc
comments. The C# identifiers are `ServerMessageType.Death` (0x44), `.WeaponFire` (0x49),
`.HitConfirm` (0x43), `.MatchState` (0x45), `.CapturePoint` (0x46), `.Explosion` (0x4A)
(`MessageTypes.cs:29-52`). **Do not write `S_DEATH` as a C# identifier.**

**Constraints.** Engine-free, allocation-free, `Vec3` not `UnityEngine.Vector3`, no `System.Linq`, no
`foreach`. `CapturePointView` and `ExplosionSuppressor` are arrays indexed by id, on the
`ServerRespawnGate` precedent from phase-05 Task 1.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~ClientEvent` —
red until Task 11's tests exist. **`CombatFeed.cs` and `ClientCombatState.cs` show zero diff**; a
reviewer checking those two files is the cheapest confirmation D16 held.

---

### Task 4 — Death, the ragdoll, and the hitbox byte (1.5 days)

**Files:** new `Net/Client/NetClientCombatPresenter.cs` (Dev C). **Needs Tasks 1, 2, 3.**

Subscribes `OnDeath`. On each message, per **D16**, one message feeds two consumers:

1. `KillfeedModel.Push(in message, Time.time)` — the line. `Prune(Time.time)` once a frame.
2. `DeathImpulse.From(message)` — the force and the hitbox, which the killfeed drops.
3. Resolve the victim: `LocalActorId` first (Task 1 trap 1), else `registry.TryFind`. A miss is a
   **normal outcome** — the victim died outside interest range — and draws the line without a corpse.
   **It is not an error and must not log as one.**
4. Drive the corpse through `RemoteActorView`: animator off, `RagdollBodies` on, apply
   `impulse.Force` to the body matching `impulse.Hitbox` per **D5**, falling back to the root body
   when the rig has no such bone.

`Actor.ApplyRigidbodyForce` (`:641-644`) is **not** called and **not** modified — it is the
local/offline path, hardcoded to `MainRigidbody()`, and stays that way. Corpse ragdoll force on
clients is already settled (`phase-v1-explosions.md:126`, AD-4) and is not reopened.

**Degradation, stated rather than silent** (E1 may be unmet):

| Prefab state | Behaviour |
|---|---|
| Rig present, bone matches | Full choreography at the hit limb. |
| Rig present, no matching bone | Force at the root body. Normal, not an error. |
| No rig | Log **once per session at warning naming E1**, hide the transform, play the death effect. A silent no-op would be indistinguishable from the bug this phase exists to close. |

**Constraint.** No allocation per death. Component references come from the `RemoteActorView`
resolved at spawn (Task 2), never `GetComponent` per message. The handler never throws (D19).

**Verify:** engine-free — `ADeathMessageProducesOneKillfeedLineAndOneImpulse`,
`TheDeathForceUnpacksThroughVel16NotVel8`, `AnEnvironmentKillerResolvesToTheEnvironmentFlag`,
`TheHitboxByteSelectsARagdollBody`. Rendering is **E7**.

---

### Task 5 — Shooting feedback: flash, report, tracer, hitmarker (2.5 days)

**Files:** `Net/Client/NetClientCombatPresenter.cs` (continued); new
`Net/Client/CosmeticTracerPool.cs` (Dev C); `Assembly-CSharp/IngameUi.cs` (Dev A file — PR plus one
review round, per design § 7).

**Weapon fire → the shot.** `ShotEvent.From(message)`, resolve the shooter (local first, then
`TryFind`), then from `RemoteActorView`: muzzle flash at `MuzzleSocket`, report `AudioClip` indexed by
`WeaponId`, tracer along `Direction` from `CosmeticTracerPool`.

Earshot filtering is **already done server-side** by `ServerEventWriter.WeaponFireAudibleRadius`, so
the presenter plays every message it receives and adds no distance test — a second filter would be a
second thing to keep in agreement with the first.

**Per D6, none of this enters `Weapon`.** Not `SpawnProjectile` (it sets `source = user`, `:392`, and
would do **real damage** from a client), not `Shoot` (`protected`, `:321`), not `Fire` (gated by
`CanFire()`, `:306-309`). There is no shared entry point, so no cosmetics path can ever be reached by
the authority path.

**Per D7 the tracer is new.** `CosmeticTracerPool` is a pre-warmed pool of non-damaging streaks with
a fixed lifetime, carrying no collider, no `Projectile` component and no `source`. It is the one
place a tracer is created, so "does this tracer do damage" has exactly one file to check.

An unknown `WeaponId` draws the default flash and plays no report rather than throwing — the same
forward-compatibility rule `WeaponIds.NameOf` already follows by returning empty.

**Hit confirm → the hitmarker.** `HitmarkerModel.Push(in message, tick, Time.time)` — shipped model,
unchanged (D16). Drawn while `IsVisible(Time.time)`. Shooter-only by **D14**; nothing to filter,
because the server already sent it to one client.

**The one gap needing a Dev A file.** `IngameUi.Hit()` is `public static void Hit()` with **no
parameters** (`IngameUi.cs:65`), so it cannot express the severity `HitmarkerModel` was built to
carry — `Normal`, `Headshot`, `Kill`, each with its own colour and pitch (`CombatFeed.cs:12-22`).
Rendering all three identically would discard shipped phase-02 work.

```csharp
// IngameUi.cs — minimum change; every existing caller preserved
public static void Hit() => Hit(0);
public static void Hit(int severity) { /* 0 normal, 1 headshot, 2 kill */ }
```

`int` rather than `HitmarkerSeverity` deliberately: `Assembly-CSharp` takes no dependency on
`Ironfront.Net.Replication` for a cosmetic enum, and the presenter casts at the one call site.

**Constraint.** No allocation per shot: serialized `AudioClip[]`, pooled tracers, no `Instantiate`.

**Verify:** engine-free — `AWeaponFireMessageDecodesToAShotEvent`, `AnUnknownWeaponIdDoesNotThrow`,
`AHitConfirmRaisesTheMarkerAndTheNewestHitWins`, `AKillHitmarkerOutranksAHeadshot`. Plus a **grep
gate** in Task 10 asserting no file under `Net/Client/` references `SpawnProjectile` or `Weapon.Fire`
— D6 enforced mechanically, not by review memory. Cosmetics are **E3**, **E4**, **E7**.

---

### Task 6 — The match HUD (1 day)

**Files:** new `Net/Client/NetClientObjectivePresenter.cs` (Dev C).

Subscribes `OnMatchState`; `MatchStateModel.Apply(in message, Time.time)`; renders `Phase`,
`Tickets0`, `Tickets1`, `PhaseSecondsRemaining` and `HumanPlayerCount`.

**The phase-specific timer rule, which a naive HUD gets wrong.**
`MatchStateMessage.PhaseSecondsRemaining` is **0 during `MatchPhase.Playing`** (`MatchMessages.cs:44-47`)
— that phase ends on tickets, not on a clock.

| Phase | Timer |
|---|---|
| `WaitingForPlayers`, `Warmup`, `Ended`, `Resetting` | Meaningful. Interpolated between broadcasts via `SecondsRemaining(Time.time)`, because the value arrives at the match broadcast rate and a timer that only moves when a packet lands reads as a stutter. |
| `Playing` | **Hidden, not rendered as `0:00`.** The HUD shows tickets. Rendering a zero here would tell every player the round is over. |

`WinningTeam` (`:69`) is a **computed property with no wire field** — use it, do not derive one.
`TeamId.None` is **255, not 2** (`GameplayEnums.cs:170`), chosen so a client switching on 0/1 falls
through rather than rendering neutral as a third team. `IsStale` dims the HUD rather than displaying a
stale number as live — `development-principles.md` § "Errors Over Silent Fallbacks", applied to a clock.

Per **D15** this writes to `ScoreUi` and never reads from it; `ScoreMultiplier`, `victoryPoints` and
`AddFlag` are untouched.

**Constraint.** No per-frame string allocation — strings rebuild only when the value changes, the fix
phase-05 Task 7 M8 already made for the lobby overlay.

**Verify:** engine-free — `AMatchStateMessageAppliesEveryField`,
`ThePlayingPhaseRendersNoTimer`, `ThePhaseTimerInterpolatesOutsidePlaying`,
`AStaleMatchStateIsReportedStaleNotZero`, `ATieResolvesToTeamIdNone`. Layout is **E5**, **E8**.

---

### Task 7 — Capture points (1 day) — **blocked on V8 Task 1, severable, last**

**Files:** `Net/Client/NetClientObjectivePresenter.cs` (continued).

**Hard precondition (D11): V8 Task 1 is on `develop`.** Two reasons, both correctness:

1. `ApplyAuthoritativeOwner(int team, float control, bool contested)` does not exist until V8 Task 1
   lands, and V8 D3 already names this handler as one of its two callers.
2. Until V8 D2 lands, `CapturePoint.UpdateOwner` is **still running its own 1 Hz arithmetic on the
   client**. Writing replicated ownership beside it makes two client-side writers — design § 2.1's
   bug, one process out, and harder to see because both writers would be ours.

On each message: `CapturePointView.Apply(in message)`, then for each dirty point call
`ApplyAuthoritativeOwner(team, control, contested)` with `control = Math.Abs(OwnerQ) / 100f` — the same
`Abs` mapping `CapturePointSlave.Apply` uses on the server (V8 Task 3), so the flag-pole height means
the same thing on both sides.

**What comes free and must not be re-implemented.** `ApplyAuthoritativeOwner` calls the existing
`SetOwner(team)` once per flip, and `SetOwner` already drives `MinimapUi.UpdateSpawnPointButtons` and
the flag renderer. **So the flag colour and the minimap need no code here** — they need the write to
go through the one path. The capture bar is the presenter's, read from `OwnerQ`.

Neutral maps to `-1` explicitly rather than by cast (V8 Task 3's reason: a neutral point written as
team `0` hands every neutral flag to blue). A point may be owned **and** contested (Task 3 trap 5).

**Verify:** engine-free — `ACapturePointMessageAppliesToTheView`,
`AnOwnedPointCanAlsoBeContested`, `ANeutralPointDoesNotResolveToTeamZero`,
`TheViewMarksOnlyChangedPointsDirty`, graded against a fake component implementing V8's method.
Rendering is **E9**.

---

### Task 8 — The minimap team-0 hardcode, and the empty catch hiding it (1 day)

**Files:** `Assembly-CSharp/MinimapUi.cs`, `Assembly-CSharp/CapturePoint.cs` (Dev A files — one PR,
one review round, per design § 7).

**Why V10 and not V8.** This is client-side UI reading the local player's team, which is V10's remit.
V8 touches the call path but **explicitly preserves the `UpdateSpawnPointButtons` call** through its
new `ApplyAuthoritativeOwner` (`phase-v8-objectives.md:87`) — so the bug survives V8's refactor
untouched unless V10 fixes it. Both phases must not assume the other did.

Three defects, all pre-existing, all on one path:

| # | Site | Defect | Fix |
|---|---|---|---|
| 1 | `MinimapUi.cs:129` | `int num = 0;` is **never reassigned**, and `:140` sets `button.interactable = owner == num`. Every team-1 player is unable to select a spawn point. | **D12** — `UpdateSpawnPointButtons(int localTeam)`, with the no-arg overload preserved and delegating. |
| 2 | `MinimapUi.cs:125` | Guards `instance == null`, then `:130` dereferences `instance.minimapSpawnPointButton`, built later in `SetupMinimap()` (`:58`, dict at `:67`). A flag flip landing first NREs. | Guard the dictionary too, and return early with **one** logged warning rather than throwing. |
| 3 | `CapturePoint.cs:262-268` | `try { … } catch (Exception) { }` — a **bare empty catch** around the call, swallowing defect 2 and anything else on the objectives path. | Delete the catch. Defect 2's guard is the real fix; anything still thrown is logged at error, per `development-principles.md` § "Errors Over Silent Fallbacks". |

**Where `localTeam` comes from — D13, stated explicitly because it is the decision that makes or
breaks the fix.** The local actor's replicated `ActorSnapshotEntry.Team`, resolved through
`NetClientPresenterGuard.TryResolveLocalActorId` (Task 1) against `NetClientBootstrap.LocalActorId`.
**Not** `FpsActorController.playerTeam`, which is documented as staying `-1` on a server and is not a
value V10 can rely on being correct at the client role before the first flag flip. Spawn precedes any
capture, so the snapshot always arrives first; if it somehow has not, the buttons stay
non-interactable and the method logs once — it does **not** fall back to team 0, because that is the
bug.

**At `NetRole.Offline` the no-arg overload passes `0`.** The human in single-player *is* team 0, so
`num = 0` was accidentally correct there and offline behaviour is byte-for-byte unchanged (D12).

**Verify:** a role-parameterised test asserts a **team-1** local player gets `interactable == true`
for team-1-owned spawn points and `false` for team-0-owned ones, and that a team-0 local player is
unchanged from today. A **grep gate** in Task 10 asserts no `catch (Exception) { }` with an empty body
remains under `Assets/Scripts/Assembly-CSharp/CapturePoint.cs` — the second half is mechanical and
belongs in the gate, not in a reviewer's memory.

---

### Task 9 — Explosions, with local prediction for your own (1.5 days)

**Files:** new `Net/Client/NetClientExplosionPresenter.cs` (Dev C). **Supersedes V1 Task 4 (D10).**

```
ExplosionSuppressor.ShouldSuppress(message, Time.time)
  ├─ true  → drop it. This is the confirmation of a blast already drawn locally.
  └─ false → unpack centre (Quantize.UnpackPos) + radius (V1's ExplosionEncoding.UnpackRadiusMetres),
             index the serialized ParticleSystem[] by (byte)Kind, scale effect + camera shake
             by radius, apply corpse ragdoll impulse locally.
```

**The prediction half (D9).** When this client's own explosive detonates, the local path calls
`ExplosionSuppressor.PredictLocal(localActorId, Time.time)` and plays the effect **immediately**; the
server's confirming message then matches on `SourceActorId` and is dropped.

**Why a window and not a pending flag.** A prediction is held for `SuppressionWindowSeconds`
(default `1.0f`) rather than until a matching confirmation arrives. A grenade destroyed in flight
never produces a confirmation, and an unbounded entry would eat the **next** real explosion from the
same actor — turning a cosmetic latency win into a missing blast. Expiry bounds the damage to D9's
accepted cost: one phantom flash, never a swallowed one.

`SourceActorId` uses `DeathMessage.EnvironmentKiller` (`0xFFFF`) for a world-sourced blast
(`ActorLifecycleMessages.cs:158`), which can never match a local actor id and is therefore never
suppressed — correct by construction, and recorded so nobody adds a special case.

**This applies no health damage.** Health arrives in the snapshot, exactly as phase-05 D5 established
for bullets and V1 Task 4 for blasts. An `ExplosionKind` this build does not know draws nothing rather
than throwing — V1 Task 4's rule, carried over with the file.

**Verify:** engine-free — `AnOwnExplosionIsSuppressedOnce`,
`ASuppressedPredictionExpiresAndDoesNotEatTheNextBlast`, `AForeignExplosionIsNeverSuppressed`,
`AWorldSourcedExplosionIsNeverSuppressed`, `AnUnknownExplosionKindDoesNotThrow`. V1 Task 5's
`AnExplosionFramedByTheServerRoutesToTheClientHandler` grades the router join and is **not**
duplicated. Cosmetics are **E6**, **E10**.

---

### Task 10 — The regression gate (1 day)

The point of the phase. Without it, the seventh dead event is a matter of time.

**Files:** new `Ironfront.Net.Replication.Tests/ClientEventSubscriptionGateTests.cs` (xunit, net8.0 —
the project already references `Ironfront.Net.Replication`, so **no csproj change and no new CI
wiring**; `dotnet test Ironfront.sln` already runs it at `ci.yml:83`).

Per **D18**, two halves with different mechanisms because they have different reliability needs:

| Half | Mechanism | Why |
|---|---|---|
| **Enumerate** the events | `typeof(ClientMessageRouter).GetEvents(BindingFlags.Public \| BindingFlags.Instance)` | Engine-free type, already referenced. A renamed event changes the gate's input automatically. Precedent: `WeaponIdTests.cs:24-25` does exactly this with `GetFields`. |
| **Detect** subscribers | Text scan for `<EventName> +=` across `*.cs` under `Ironfront_Reborn/Assets/Scripts/` | **`Ironfront_Reborn/Assets` contains zero `.asmdef` files**, so no Unity assembly exists for `dotnet test` to load, and `ci.yml`'s `unity-compile` job is disabled for want of a licensed Editor. This is the honest ceiling and it is stated rather than hidden. |

*(Considered and not chosen: adding the check to `tools/SpecChecker`, which already reflects over a
referenced assembly and reads a file under `Assets/` (`Program.cs:162`, `:185-195`). It would need a
new `ProjectReference` to `Ironfront.Net.Replication` and a new CI line; the test project needs
neither. Recorded so the alternative is not re-derived.)*

**Exclusions, each for a stated reason:** `ClientMessageRouter.cs` itself (declarations and `Invoke`
sites are not subscriptions), `obj/` and `bin/`, and any `*Tests.cs` — a test subscribing an event
does not make the game render it, and counting them is exactly how a gate goes green over a dead
feature. **Today's tests would supply four false positives** (`ClientCombatTests.cs:522-524`, `:562`),
so this exclusion is load-bearing rather than tidy.

**Two loud failures, not two skips.**

1. The test walks up from `AppContext.BaseDirectory` for `Ironfront.sln`. Not found → **fail**, naming
   what it searched for.
2. **It asserts it scanned more than zero `.cs` files, and found exactly nine events.** Taken from
   `UnitySyntaxCheck`'s own code, which errors on an empty file set because *"a check that passes
   because it looked at nothing is worse than no check: it reports green forever from the wrong
   working directory."*

**Also gated here** (same file, same scan, no new machinery):

- **D6's enforcement** — no file under `Assets/Scripts/Net/Client/` references `SpawnProjectile` or
  `Weapon.Fire`. A cosmetics path that becomes a damage path fails the build instead of a review.
- **Task 8 defect 3** — no empty `catch (Exception) { }` remains in `CapturePoint.cs`.

**Proving the gate can fail.** A check never seen failing is unproven, so the detector is a pure
function over a string, tested against fixtures:

| Test | Asserts |
|---|---|
| `TheGateFindsASubscriptionInAFixture` | `Router.OnDeath += Handler;` reports subscribed |
| `TheGateReportsAnUnsubscribedEventInAFixture` | declaration only reports **unsubscribed** — the red path, run every CI build |
| `TheGateIgnoresATestFileSubscription` | a path ending `Tests.cs` does not count |
| `TheGateIgnoresTheRouterDeclarationItself` | `public event Action<DeathMessage>? OnDeath;` is not a subscription |
| `TheGateFailsWhenTheRepoRootCannotBeFound` | loud failure, synthetic base directory |
| `TheGateFailsWhenItScansZeroFiles` | the empty-file-set failure |
| `EveryRouterEventHasAProductionSubscriber` | **the gate**, over the real tree |

**The expected-pass set is nine, and three pass today.** Before this phase's presenters land,
`EveryRouterEventHasAProductionSubscriber` fails naming exactly the six — the correct starting state,
and the proof the gate discriminates rather than blanket-fails.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~ClientEventSubscriptionGate`
— six green and `EveryRouterEventHasAProductionSubscriber` red before Tasks 4-9, all seven green
after. Delete one `+=` locally and watch it go red before merging.

---

### Task 11 — Tests (2.5 days, written alongside Tasks 1-10)

All engine-free, all in CI, no Editor. New files
`Ironfront.Net.Replication.Tests/ClientEventConsumptionTests.cs` and `RemoteActorViewStateTests.cs`,
alongside Task 10's gate file. **xunit 2.9.3, net8.0.** Central Package Management is on with
`CentralPackageVersionOverrideEnabled=false`, so **any inline `Version=` is an NU1008 error on
purpose** — new packages go in `Directory.Packages.props` first. No new package is expected.

| Test | Asserts |
|---|---|
| `ARemoteActorResolvesFromItsNetworkId` | Task 1's seam; a miss returns false rather than throwing |
| `TheLocalActorIsNotInTheRemoteRegistry` | `RemoteActorRegistry.cs:118`'s deliberate exclusion, so every presenter's local special-case is justified by a test |
| `SnapshotFlagsMapToStanceAimAndRagdollIntent` | Task 2's decode-to-intent, the half testable without Unity |
| `AWeaponIdChangeSelectsADifferentCosmeticSet` | Task 2 → Task 5 |
| `SeatedAndInWaterAreDecodedAndDeliberatelyUnrendered` | pins the recorded non-consumption so it stays deliberate |
| `ADeathMessageProducesOneKillfeedLineAndOneImpulse` | D16's fork: one message, two consumers |
| `TheDeathForceUnpacksThroughVel16NotVel8` | trap 1 — the failure that would silently clamp every kill |
| `TheKillfeedEntryArgumentOrderIsVictimKillerCorrect` | trap 2 — the swap that compiles and is wrong |
| `AnEnvironmentKillerResolvesToTheEnvironmentFlag` | `0xFFFF`, not actor 65535 |
| `TheHitboxByteSelectsARagdollBody` | D5 — the byte has a consumer |
| `AWeaponFireMessageDecodesToAShotEvent` | shooter, weapon, direction |
| `AnUnknownWeaponIdDoesNotThrow` | forward compatibility |
| `AHitConfirmRaisesTheMarkerAndTheNewestHitWins` | `HitmarkerModel`'s shipped semantics survive the presenter's call shape |
| `AKillHitmarkerOutranksAHeadshot` | `SeverityOf`, so Task 5's `Hit(int)` gets the right number |
| `AMatchStateMessageAppliesEveryField` | all five fields |
| `ThePlayingPhaseRendersNoTimer` | the `0` that must not become `0:00` |
| `ThePhaseTimerInterpolatesOutsidePlaying` | the timer moves between broadcasts |
| `AStaleMatchStateIsReportedStaleNotZero` | unknown is not good |
| `ATieResolvesToTeamIdNone` | `None == 255`, not 2 |
| `ACapturePointMessageAppliesToTheView` | Task 7 |
| `AnOwnedPointCanAlsoBeContested` | trap 5 |
| `ANeutralPointDoesNotResolveToTeamZero` | V8 Task 3's mapping, asserted client-side too |
| `TheViewMarksOnlyChangedPointsDirty` | repaint on change |
| `ATeamOnePlayerGetsInteractableSpawnPointButtons` | **Task 8 defect 1 — the reported bug** |
| `ATeamZeroPlayerIsUnchangedFromToday` | Task 8's no-regression half |
| `AnUnresolvedLocalTeamLeavesButtonsDisabledRatherThanTeamZero` | D13 — the fallback that is not the bug |
| `AnOwnExplosionIsSuppressedOnce` | D9 |
| `ASuppressedPredictionExpiresAndDoesNotEatTheNextBlast` | the window bound |
| `AForeignExplosionIsNeverSuppressed` | suppression keys on `SourceActorId` alone |
| `AWorldSourcedExplosionIsNeverSuppressed` | `0xFFFF` never matches a local id |
| `AnUnknownExplosionKindDoesNotThrow` | carried from V1 Task 4 with the file |
| `NoHandlerThrowsOnAMalformedMessage` | D19 — the router counts rather than throws (`:24-29`), and a handler must not break that |
| `NoClientModelAllocatesOverAThousandEvents` | § 3.2, across all five new models at once |

---

## 4. Acceptance criteria

1. Every one of the nine `ClientMessageRouter` events has at least one production subscriber outside
   the test projects, and `EveryRouterEventHasAProductionSubscriber` is green.
2. The gate is **proven able to fail**: `TheGateReportsAnUnsubscribedEventInAFixture`,
   `TheGateFailsWhenTheRepoRootCannotBeFound` and `TheGateFailsWhenItScansZeroFiles` are green,
   exercising the red paths on every CI run.
3. A remote player visibly crouches, aims, holds the replicated weapon and ragdolls — driven from
   snapshot fields that arrive today and are currently discarded.
4. A remote player's shot produces a muzzle flash, a report and a tracer, and **no file under
   `Net/Client/` references `SpawnProjectile` or `Weapon.Fire`** — asserted by the gate, not by review.
5. A death drives the corpse from the replicated force **at the replicated hitbox**; `HitboxHit` has a
   consumer. Where the prefab has no rig, it logs once naming **E1** and degrades visibly. It never
   silently does nothing.
6. A client-role actor that reaches zero health dies. Today it does not (D4).
7. A hitmarker appears on the shooter's client and no other, and a kill marker outranks a headshot.
8. The HUD renders all five match-state fields; the timer is **hidden during `Playing`**, interpolates
   outside it, reports staleness, and a tie resolves to `TeamId.None`.
9. **A team-1 player can select a spawn point from the minimap**, a team-0 player is unchanged, and an
   unresolved local team leaves the buttons disabled rather than defaulting to team 0.
10. **No empty `catch (Exception) { }` remains in `CapturePoint.cs`** — asserted by the gate.
11. Capture points render from `CapturePointState` via `ApplyAuthoritativeOwner`, and **no client-side
    code writes `owner`, `control`, `pendingOwner` or `isContested` by any other path** — confirmed by
    grep in review, the same check V8 criterion 3 makes on the server.
12. Your own explosion renders immediately and exactly once; the confirmation is suppressed; a foreign
    or world-sourced explosion never is; an unconfirmed prediction expires without eating the next blast.
13. **`CombatFeed.cs` and `ClientCombatState.cs` have zero diff.** `HitmarkerModel`, `KillfeedModel`
    and the local death path are reused, not re-implemented (D16).
14. `IngameUi.Hit()` and `MinimapUi.UpdateSpawnPointButtons()` still compile for every existing caller;
    both new forms are additive overloads.
15. Offline single-player is unchanged: every presenter is inert at `NetRole.Offline` (D19), the
    minimap overload passes `0` there (D12), and no presenter exists in a server build.
16. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-event allocation in
    any new logic file.
17. `PROTOCOL_VERSION` is unchanged and `tools/SpecChecker` passes untouched. This phase consumes no
    byte that was not already specified.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| **The phase reports green in CI while nothing actually renders.** Nearly every deliverable is cosmetic and CI has no Editor — the seam contracts pass and the screen stays black | **4** | **5** | **20** | Structural, not a promise to be careful. (a) CI grades *contracts* — decode, model semantics, write-path, suppression — and the phase says plainly it grades nothing visual. (b) **E1-E11 are enumerated in § 7 as individually checkable items with stated pass conditions**, not a category handed over. (c) **E7-E11 are two-client tests with explicit pass conditions**, so "it works" is reproducible. (d) Criteria 5 and 9 forbid the silent-no-op failure mode by name. |
| **Task 2 (the representation layer) is the real critical path and was not in the original brief.** It is the largest single task and everything from Task 4 depends on it | **4** | **4** | **16** | Named as the critical path in § 6 and estimated at 3 days rather than folded into "wiring". Tasks 6, 8, 9 and 10 are deliberately **independent of it**, so a Task 2 overrun does not stall the HUD, the minimap fix, explosions or the gate. The engine-free half (decode-to-intent) is testable before any prefab exists. |
| **`_remoteActorPrefab` carries no animator, rig, muzzle socket or weapon model.** Unreadable from source — it is authored in the Editor | **4** | **4** | **16** | **E1 is blocking** and is first in § 7. Task 4 ships the degraded path deliberately: log once at warning naming E1, hide, play the death effect. The engine-free tests grade decode and models, which hold either way — only the final third of Tasks 2, 4 and 5 is gated on the prefab. |
| **A cosmetics-only path becomes a real damage path.** `Weapon.SpawnProjectile` sets `source = user` (`:392`), so a client-side "just show a bullet" does real damage | 2 | **5** | **10** | **D6** removes the shared entry point entirely — V10 never calls into `Weapon`. **Task 10's grep gate asserts it mechanically**: no `SpawnProjectile` or `Weapon.Fire` under `Net/Client/`. This is the one mitigation in the table that cannot decay, because it fails the build rather than a review. |
| **Task 7 lands before V8 Task 1**, putting a second ownership writer on the client while `UpdateOwner` still runs there | 3 | **5** | **15** | **D11** states it as a hard precondition, checked in the PR description, and Task 7 is **severable and last**. Criterion 11's grep is the same check V8 criterion 3 makes server-side, so a second writer fails review on both sides of the wire. |
| **The minimap fix changes offline single-player**, where `num = 0` was accidentally correct | 3 | 4 | **12** | **D12** — the no-arg overload passes `0` at `NetRole.Offline`, so the offline path is byte-for-byte unchanged. `ATeamZeroPlayerIsUnchangedFromToday` pins it. Same shape and same promise as phase-05 D5 and V8 D2. |
| **A presenter's `Awake` runs before `NetClientBootstrap.Current` exists**, so its subscribe silently no-ops for the object's whole life — no error, no log (`RemoteActorRegistry.cs:62`) | 3 | 4 | **12** | Every new presenter logs once at warning on a null resolve (Task 1), rather than inheriting the existing silence. `[DefaultExecutionOrder]` matching `RemoteActorRegistry`'s `-50` and **E11** (scene ordering) close the rest. The gate cannot catch this — it is a runtime ordering fault, and saying so is why E11 exists. |
| The gate's text scan produces a false green — a `+=` in a comment, a `#if` block, or dead code | 3 | 4 | **12** | Two-sided: the fixture tests pin both the positive and the negative path, and the exclusion list is explicit rather than incidental. **Residual risk is a false green on a commented-out subscription**, recorded here as the gate's known ceiling — a Roslyn analyzer would close it (`UnitySyntaxCheck` is the in-repo precedent) and is not worth its cost against nine events. |
| Building a tracer from scratch (D7) drifts into an open-ended VFX task | 3 | 3 | 9 | Scoped to one file, `CosmeticTracerPool`, with a fixed lifetime, no collider and no `Projectile` component. The *look* is **E4**, an Editor item with an owner, not a code task without an end. |
| `IngameUi.cs`, `MinimapUi.cs` and `CapturePoint.cs` conflict with Dev A's branch | 3 | 3 | 9 | All three land in **one** PR (§ 7), early, announced — the phase's only Dev A review round. Every change is an additive overload or a deleted empty catch, so a conflicting merge cannot break an existing caller. Same precedent as phase-05 Task 6, V1 Task 3 and V8 Task 1. |
| Local explosion prediction shows a phantom blast the server never confirms | 3 | 2 | 6 | Accepted in D9, bounded by `SuppressionWindowSeconds`. `ASuppressedPredictionExpiresAndDoesNotEatTheNextBlast` pins that the opposite failure — a swallowed real blast — cannot happen. |
| V1 also creates `NetClientExplosionPresenter.cs` because its Task 4 was never amended | 3 | 2 | 6 | D10 names the supersession and § 7 lists the amendment as an explicit handoff item. Worst case is a conflict on a new file, which is loud. |

**Four risks reach 15 or higher, and the top one is the phase's defining condition:** almost
everything here is work CI structurally cannot grade. That is why § 7's Editor checklist is
enumerated to the individual check with pass conditions rather than delegated as a category, why
criteria 5 and 9 forbid silent degradation by name, and why D6's damage-path risk is enforced by a
grep gate rather than by anyone remembering.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — Lookup seam + local identity | S (0.5d) | No dependencies. **Start here** — everything needs `TryFind`. |
| 2 — `RemoteActorView` representation | **L (3d)** | Needs 1. **The critical path.** Not in the original brief; do not fold it into "wiring". |
| 3 — Reuse audit + the five missing models | M (1.5d) | Needs nothing. The audit half is the first hour and shrinks the rest. |
| 4 — Death + ragdoll + hitbox byte | M (1.5d) | Needs 1, 2, 3. Final third gated on **E1**. |
| 5 — Shooting feedback + tracer + `Hit(int)` | **L (2.5d)** | Needs 1, 2, 3. Grew for D7's from-scratch tracer. |
| 6 — Match HUD | S (1d) | Needs 3 only. **Independent of Task 2.** |
| 7 — Capture points | S (1d) | **Blocked on V8 Task 1. Severable, last.** Nothing above waits. |
| 8 — Minimap team fix + empty catch | S (1d) | Needs 1 for the local-team lookup. **Independent of Task 2.** Rides the Dev A PR. |
| 9 — Explosions + local prediction | M (1.5d) | Needs 3 only. **Independent of Task 2.** Supersedes V1 Task 4. |
| 10 — Regression gate + grep gates | S (1d) | **Independent of everything.** Write it early — it is red until 4-9 land, and that red is informative. |
| 11 — Tests | L (2.5d) | Written alongside 1-10, not after. |
| **Total** | **~17 days (~3.5 weeks)** | Critical path: **1 → 2 → 4 → 5** ≈ 7.5 days of strictly serial work. Tasks 3, 6, 8, 9 and 10 run in parallel with Task 2 and are the reason the total is not the sum. Task 7 is outside the estimate and lands whenever V8 Task 1 does. |

> **The estimate grew from an initial ~10.5 days.** Task 2 (3d) and Task 8 (1d) were not in the
> original brief and were added after source verification; Tasks 5 and 11 grew for the from-scratch
> tracer and the larger test matrix. Recorded so the change is visible rather than absorbed.

---

## 7. Handoff

### To Dev A — the Editor half, enumerated

CI cannot grade any of this. Each item is individually checkable with a stated pass condition. **E1
is blocking; the rest are not.**

| # | Item | Pass condition |
|---|---|---|
| **E1** | **`_remoteActorPrefab` (`RemoteActorRegistry.cs:42`) carries an animator, a ragdoll rig with named bones, a muzzle socket transform, a weapon-model mount, and a decal receiver.** **Blocking for Tasks 2, 4, 5.** | A remote actor can crouch, aim, ragdoll, and show a flash at the right height. If any part is absent, **say which** — Tasks 2 and 4 have degraded paths designed for it, and their warnings name this row. |
| **E2** | `.meta` files for `RemoteActorView.cs`, `NetClientCombatPresenter.cs`, `NetClientObjectivePresenter.cs`, `NetClientExplosionPresenter.cs`, `NetClientPresenterGuard.cs`, `CosmeticTracerPool.cs`, and the five new files in `Ironfront.Net.Replication/Client/` | No missing-meta warnings on import. **Note:** `ServerActorDamageSink.cs`, `ServerCombatBridge.cs` and `ServerCombatEvents.cs` are still missing theirs from phase-05, and V1 § 7 already asked — one pass covers all three phases. |
| **E3** | `NetClientCombatPresenter` wiring: muzzle-flash `ParticleSystem`, report `AudioClip[]` indexed by weapon id | Every id in `WeaponIds` has a clip or is deliberately null; a null draws the default flash, plays nothing, and does not throw. |
| **E4** | **`CosmeticTracerPool` visual.** New asset — no tracer exists in the project today (D7) | A streak that reads as a bullet, with **no collider, no `Projectile` component, no `source`**. This is the asset most likely to be assumed to exist; it does not. |
| **E5** | `NetClientObjectivePresenter` HUD wiring: ticket labels, phase label, phase timer, capture-progress bar | All five match-state fields have somewhere to render, and **the timer is hidden during `Playing`** rather than showing `0:00`. |
| **E6** | `NetClientExplosionPresenter` wiring: `ParticleSystem[]` indexed by `ExplosionKind` | Indices 0 (`Grenade`) and 1 (`Rocket`) filled; 2 (`Vehicle`) and 3 (`Environment`) may be empty and must not throw — carried from V1 § 7 item 3 with the file. |
| **E7** | **Two-client test — combat.** A shoots B | B's client shows A's flash at the correct height for A's stance, hears the report, sees the tracer. A's client shows a hitmarker; B's does not. Both show B's ragdoll driven along the shot direction from the hit limb, and one killfeed line each. |
| **E8** | **Two-client test — HUD.** Watch a full round | Tickets, phase and player count track the server on both clients. No timer during `Playing`; a timer during warmup and after the end. |
| **E9** | **Two-client test — capture point.** Both clients watch one point flip | Flag colour, capture bar and minimap marker change on both clients at the same authoritative value, and neither client runs its own arithmetic. |
| **E10** | **Two-client test — grenade.** A throws one | A sees the blast immediately (no RTT delay) and **exactly once**. B sees it once. Neither sees it twice. |
| **E11** | **Scene ordering.** The three presenters sit on the client bootstrap object, resolve `NetClientBootstrap.Current` successfully in `Awake`, and are absent from (or inert in) the server scene | No presenter logs its null-bootstrap warning on a normal client start; a headless build logs nothing from any presenter and dereferences no UI singleton. **This is the one failure the gate cannot catch** — it is runtime ordering, not source shape. |
| — | Per design § 7: the Profiler run, and per-weapon `Configuration` values in `_Managers.prefab` | Unchanged. V2 owns the weapon table, not this phase. |

**The Dev A PR is one PR, three files:** `IngameUi.cs` (Task 5's `Hit(int)` overload), `MinimapUi.cs`
and `CapturePoint.cs` (Task 8). Every change is either an additive overload or a deleted empty catch,
with the offline-unchanged tests attached. One review round is assumed.

### To V1 — one amendment

**V1 Task 4 is superseded by V10 Task 9 (D10).** V1 should strike Task 4 and its
`NetClientExplosionPresenter.cs` row, keeping Tasks 1, 2, 3 and 5 unchanged; its § 7 item 3 moves to
**E6** above. **V1 D6 is overridden by V10 D9** — using V1 D6's own recorded fallback clause, so no
new decision was made, only an earlier one taken. V1 Task 5's
`AnExplosionFramedByTheServerRoutesToTheClientHandler` still grades the router join and is
deliberately **not** duplicated here.

### To V8 — one dependency, one confirmation, one boundary

- V10 Task 7 is **blocked on V8 Task 1** (D11) and is the second caller V8 D3 already anticipates.
- `ApplyAuthoritativeOwner` must keep the signature V8 D3 states — `(int team, float control, bool
  contested)` — because Task 7 calls it with `Math.Abs(OwnerQ)/100f` to match `CapturePointSlave.Apply`'s
  `Math.Abs(state.Owner)`. If that mapping changes on one side it changes on both, in one commit.
- **The minimap fix is V10's, not V8's.** V8 Task 1 explicitly *preserves* the
  `MinimapUi.UpdateSpawnPointButtons` call through `ApplyAuthoritativeOwner`
  (`phase-v8-objectives.md:87`), so the team-0 hardcode survives V8's refactor untouched. V10 Task 8
  fixes it. **Neither phase should assume the other did** — this row exists so that assumption is
  impossible.

### To V5 — one non-overlap

V10 **never reads or writes `aiControlled`** (D8). V5-D7 pins a test that the flag is *unchanged* for
a networked driver; that is a different concern — V5 guards against tripping it, V10 simply never
consults it. Neither contradicts the other, and V10's remote cosmetics are driven entirely from
`RemoteActorView` and replicated fields.

### To V3 — the gap this phase cannot close

**`ServerMessageType.PlayerList = 0x4B` is declared (`MessageTypes.cs:52`) with no message struct and
no router case.** `KillfeedEntry` therefore carries actor **ids only** — so a killfeed line has a
killer, a victim, a cause and a headshot flag, and **no names to render**. V10 ships the line's data
and its expiry; the names need a protocol addition and belong with V3's bump. **Named here rather
than discovered later, per V1 D5's rule** — an unbuilt message that nobody writes down is exactly how
this phase's six dead events came to exist.

### To V9

V10 is a precondition for V9's two-client Editor test being meaningful at all: before this phase a
second client renders no combat, no HUD and no objectives, so "the same vehicle in the same place"
(design criterion 1) would be the only observable thing. E7-E11 are the smaller versions of the same
test and should run first.

### Observations recorded, not fixed

- `RemoteActorRegistry.cs:105` iterates `_live` with `foreach` inside `Update()`. A `Dictionary`
  struct enumerator does not allocate, so this is a § 3.2 style violation and not a live defect.
  Recorded rather than fixed, per `coding-guidelines.md` § 3.
- The premise this phase was commissioned under listed **seven** dead events and named
  `OnSnapshotApplied` among them. It is **six** — `OnSnapshotApplied` has been subscribed by
  `ClientPredictionStage.cs:76` since prediction landed. Recorded because the gate's expected-pass set
  depends on it.
- `NetContext` imports `UnityEngine` (`NetContext.cs:1`), so it can never be linked into an
  engine-free test project. Its own doc (`:26-31`) states that shared simulation must never consult
  the role: *"The role governs who drives the simulation, never what it computes."* Every V10 role
  branch is in a presenter, never in a model.

**Still outside Dev C:** nothing in this phase.
