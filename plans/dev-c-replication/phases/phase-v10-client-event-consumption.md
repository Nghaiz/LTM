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
> § 2.4 and § 6, plus recorded findings **A15** and **A16**. **This phase is not in that document's
> § 6 phase table.** It was approved on 2026-08-17 after the gaps in § 1 were found by grep and
> verified at source; this file is the record of that addition.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files, `Span<byte>` over
> `byte[]`) and § 7 (ownership). **Per design § 7, Dev C writes every file here including those under
> `Assembly-CSharp/`**; Dev A owns only work that genuinely requires the Editor, enumerated as
> **E1-E12** in § 7.
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

This is why the phase is not "wire up six subscribers". A muzzle flash needs a weapon and a death
needs a ragdoll, and neither exists on a bare pooled `Transform`. **§ 3 sequences a remote-actor
representation first and the event layer on top of it** — and § 1.5 is the reason that ordering is
dangerous as well as necessary.

### 1.3 Nothing turns a remote actor into a corpse

The phase-05 D5 guard **has landed**: `Actor.cs:789` reads `bool ownsHealth = !NetContext.IsClient;`,
and `:807` reads `if (ownsHealth && health <= 0f) { Die(...); }`. So on a client `ownsHealth` is
false, the death branch is unreachable, and the `else if` chain at `:812/:816/:820` fires instead —
`ApplyRigidbodyForce` only when the actor is *already* ragdolled, else `KnockOver`, else `Hurt`.

A client-role actor therefore takes hits, bleeds, staggers — and **never dies**. Nothing in the build
transitions a remote actor into a corpse. That is new wiring, not a re-route.

### 1.4 Two client-side objective bugs on one path

- **`MinimapUi.UpdateSpawnPointButtons` is hardcoded to team 0.** `MinimapUi.cs:129` declares
  `int num = 0;` and never reassigns it; `:140` sets `button.interactable = owner == num`. In the
  original single-player game the human is always team 0, so this was invisible. **In multiplayer it
  disables the respawn UI for every team-1 player.**
- **An empty catch is hiding it.** `CapturePoint.cs:262-268` calls `UpdateSpawnPointButtons()` inside
  `try { … } catch (Exception) { }` with an empty body. **Forensic note:** `ScoreUi.AddFlag` sits at
  `CapturePoint.cs:261`, immediately *outside* that try block, on the same path with the same guard
  style. Someone wrapped the `MinimapUi` call *specifically* — behavioural evidence that the throw
  was **observed**, not guarded against speculatively. The throw is real: `:125` checks
  `instance == null` but `:130` dereferences `instance.minimapSpawnPointButton`, which
  `SetupMinimap()` (`:58`, dict at `:67`) builds later.

### 1.5 The representation layer arms a latent local-singleton hijack (A16)

`TankTurret.Unholster` (`:41-45`) and `MountedTurret.Unholster` (`:31-35`) are identical:

```csharp
if (!user.aiControlled) {
    FpsActorController.instance.DisableCameras();
    camera.enabled = true;
}
```

`MountedTurret.cs:44` does the mirror `EnableCameras()` in `Holster`. **This cannot fire today**
because remote actors are bare `Transform`s with no `Actor` and no controller. **Task 2 is what arms
it:** the moment a remote actor has an `Actor` whose controller is not `AiActorController`,
`aiControlled` is false (`Actor.cs:178` freezes it from `controller.GetType()`) and a *remote* player
entering a turret calls `DisableCameras()` on the **local** player's rig.

And the family is larger than the turrets. Verified scope — `grep -n "aiControlled" Actor.cs`:
**eight** `!aiControlled` sites in `Actor.cs` alone (`:223`, `:716`, `:824`, `:853`, `:1124`,
`:1139`, `:1166`, `:1181`), plus `:267` and `:279` on the inverse. Two are already wrong today:

- `:824-829` — `IngameUi.instance.SetHealth(...)` and `ShowVignette(...)`. A remote **human** has
  `aiControlled == false`, so a remote player taking damage writes **your** health bar and vignette
  from **their** health. This runs on the client path today, because `:790` `ReceivedDamage` and the
  `else if` chain are not role-gated.
- `:716-719` — `IngameUi.instance.Hide()` inside `Die`. Correct for the local player, wrong for a
  remote one.

`!aiControlled` has never meant "is the local player". It meant it only while the local player was
the only non-AI actor in the process. Multiplayer breaks that assumption everywhere at once.

### 1.6 What this adds up to

The server half of phase-05 (combat) and phase-03 (capture points) shipped, and the client half was
never built. This is [`wired-not-just-present.md`](../../../.claude/rules/wired-not-just-present.md)
at six-event scale, on top of a representation layer that decodes fields it discards, on top of an
identity predicate that has silently meant the wrong thing since the first remote actor.

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
3. A death produces a ragdoll driven by the **replicated** force vector.
4. A hit produces a hitmarker on the shooter's screen and on nobody else's.
5. Score, tickets, phase and the phase timer render from the server's authoritative numbers.
6. Capture points render, and **a team-1 player can select a spawn point**.
7. An explosion is seen by everyone, and **your own is seen immediately** rather than one RTT later.
8. **No client-only singleton is ever touched on behalf of a non-local actor**, and a grep gate keeps
   it that way.
9. A **regression gate** fails the build when any router event loses its last production subscriber.

**Not in this phase.** No protocol change — not one byte moves that is not already specified and
conformance-tested. No server-side emit work: V1 owns the explosion emitter, phase-05 shipped the
other five. No vehicles, seats or projectiles. **No killfeed names** and **no scorch decal** — both
recorded in § 7 with owners.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **D1** | **The representation layer comes first, and the event layer sits on it.** Task 2 builds `RemoteActorView`, consuming the snapshot fields `DeltaDecoder` already produces (§ 1.2). Every task from 4 onward depends on it. Wiring six subscribers onto a bare `Transform` would produce six handlers with nothing to drive. |
| **D2** | **"Is this the local actor" is an explicit identity check, never `!aiControlled`.** § 1.5. V10 introduces `NetClientPresenterGuard.IsLocalActor(ushort actorId)` comparing against `NetClientBootstrap.LocalActorId`, and **every** client-only singleton touch on a per-actor path is gated on it. `aiControlled` keeps its original meaning (is this AI) and V10 neither reads it for identity nor writes it — which is also why this does not contradict **V5-D7**, whose pinned `AiControlledIsUnchangedForANetworkedDriver` guards against *tripping* the flag. V5 avoids changing it; V10 stops trusting it. |
| **D3** | **Three presenter components, not six, not one.** `NetClientCombatPresenter` (death, weapon fire, hit confirm), `NetClientObjectivePresenter` (match state, capture points), `NetClientExplosionPresenter` (explosions). Each caches `NetClientBootstrap.Current` in `Awake` and subscribes in `OnEnable` / unsubscribes in `OnDisable` — byte-for-byte the lifecycle `RemoteActorRegistry.cs:60-85` and `ClientPredictionStage.cs:76-82` already use. |
| **D4** | **The engine-free models are the policy half; the presenters are thin adapters.** New models live in `Ironfront.Net.Replication/Client/`, which `Ironfront.Net.Replication.Tests` already references — CI-testable with no new wiring. The **linked-source pattern** (`Ironfront.Client.Flow.Tests`, `Ironfront.Client.Input.Tests`) is deliberately not used: it forbids `using UnityEngine;` in a linked file, and every presenter needs it. |
| **D5** | **Death drives the ragdoll through the existing public pair, and the ragdoll force is applied to the main rigidbody only.** `Actor.cs:632 public void KnockOver(Vector3 force)` already does `if (!ragdoll.IsRagdoll()) { FallOver(); ApplyRigidbodyForce(force); }` — enable plus force in one public call. **`ApplyRigidbodyForce` (`:641-644`) is hardcoded to `MainRigidbody()` = `rigidbodies[0]`, `ForceMode.Impulse` (`ActiveRaggy.cs:305-308`); there is no per-bone API.** V10 **does not add one**: the bone map would depend on rig bone naming that is authored in the Editor and unreadable from source (already the blocking **E1**), and the phase already carries a Dev A PR. |
| **D6** | **`DeathMessage.HitboxHit` IS consumed — for the killfeed headshot icon, not for ragdoll bone selection.** `KillfeedEntry.From` (`CombatFeed.cs:165`) already casts it and compares against `HitboxType.Head`. So the byte is not orphaned. **What is explicitly not consumed is per-bone ragdoll targeting**, owned by whoever adds a per-bone force API — nobody in the V-track today. Named here per V1 D5's rule: an unconsumed capability that nobody writes down is exactly how design § 2.2 happened. |
| **D7** | **Weapon cosmetics are extracted into one shared method that both paths call.** New `public void PlayFireCosmetics()` on `Weapon` containing **only** the muzzle flash (`Weapon.cs:340-343`) and the report audio (`:356`, reverb `:362-365`). The existing `Shoot` (`:321-366`) calls it, so there is **one copy** and offline single-player is byte-for-byte unchanged. This mirrors how the `Actor.Damage` guard was done: one shared path, role decides what is skipped. **Deliberately excluded from the extraction:** `SpawnProjectile` (`:388-394`) — it sets `component.source = user` (`:392`) and would do **real damage** from a client; and `user.ApplyRecoil` (`:348`) — it chains to `FpsActorController.cs:409`'s `fpParent`, the **local** camera rig, so firing it for a remote actor kicks your own view. |
| **D8** | **Full-auto remote fire plays a per-shot report, not the local loop.** The full-auto audio is **not** in `Shoot` — it is a loop started from `Fire()` (`Weapon.cs:200-203`). Calling `Shoot` alone on an automatic weapon is **silent**, which would read as "network audio is flaky" rather than "wrong entry point". Each `WeaponFireMessage` is one shot, so `PlayFireCosmetics` plays one report per message and the loop stays a local-player optimisation. |
| **D9** | **The cosmetic path is stateless and never reads or advances `currentMuzzle`.** Weapon fire is the one event on the **cosmetic channel** (unreliable-sequenced, ch 1) and is documented safe-to-drop (`CombatMessages.cs:139-141`). `AlternatingMountedWeapon.MuzzlePosition()` (`:19-22`) reads `currentMuzzle`, advanced once per shot at `:12` — driving that counter from received fire events would desynchronise **permanently on the first dropped packet**, and would not reproduce in a clean-network test. Either the muzzle index rides the event or the cosmetic does not depend on it. V6 replicates it for the authoritative path; that is a different consumer. |
| **D10** | **A cosmetics-only tracer is new work.** No tracer system exists — scope: `[Tt]racer`, `TrailRenderer`, `LineRenderer` across `Assets/Scripts/`; the only hits are A* internals and a road-editor field. The visible streak in the original game **is** the `Projectile`, which D7 forbids. Task 5 ships a pooled, non-damaging streak. Honest new work, in the estimate. |
| **D11** | **`ScoreUi` gets a real authoritative setter. The public `Text` fields are not poked.** Its mutators are **delta-only with no getters** (`AddScore` `:58`, `AddFlag` `:89`) while `MatchStateMachine`'s state is **get-only** — so feeding the server's numbers as deltas would re-enter `ScoreMultiplier(flags)` (`:64-65`) and **double-drive the win check** (`:75-85`). Adding a method is **code, not Editor work**, and design § 7 puts code here. `ScoreUi` gains one explicit entry point taking the server's values and rendering them, bypassing `AddScore`/`AddFlag` entirely. **The `ScoreUi.cs:46-57` remarks are updated in the same commit** rather than left contradicting the code. |
| **D12** | **V10 closes the *rendering* half of V8 D9's divergence, and only that half.** `ScoreUi` still holds match state that does not run headless; that remains a recorded divergence owned by V8 D9. V10 makes the networked HUD render authoritative numbers; it does not move `ScoreUi`'s state out of the UI component. Cross-referenced so nobody later "tidies it up" by routing tickets back through `AddScore`. |
| **D13** | **Your own explosion is predicted locally and the confirming message is suppressed by `SourceActorId`.** This **overrides V1 D6**, which chose server-sourced-for-everyone. It is not a contradiction: **V1 D6 records this exact mechanism as its own named fallback** — *"play locally and suppress the matching `S_EXPLOSION` by `SourceActorId` — one branch in the presenter, recorded here so it is not re-derived."* The consumer took that branch on 2026-08-17, before V9 rather than after. **Accepted cost:** an unconfirmed prediction shows a phantom blast with no damage, bounded by the window in Task 10. |
| **D14** | **V10 owns `NetClientExplosionPresenter.cs` outright; V1 Task 4 is superseded.** V10's version carries D13's prediction branch that V1's does not. V1 keeps Tasks 1, 2, 3 and 5 untouched. **This needs a one-line amendment to V1**, listed in § 7 rather than silently assumed. |
| **D15** | **Capture-point consumption writes through V8's `ApplyAuthoritativeOwner`, so Task 8 is blocked on V8 Task 1.** V8 D3 makes that the single write path for `owner`, `control`, `pendingOwner` and `isContested`, and already names *"the client's capture-point message handler"* as one of its two callers. Landing it first would add a second client-side writer while `UpdateOwner`'s 1 Hz arithmetic still runs there (V8 D2 stops it) — design § 2.1's bug, one process out. Severable and last. |
| **D16** | **`MinimapUi.UpdateSpawnPointButtons` gains a `localTeam` parameter; it stops guessing.** The no-arg overload is preserved and resolves the team at the call site; at `NetRole.Offline` it passes the literal `0`. **Offline single-player is byte-for-byte unchanged**, because the human there *is* team 0 and `num = 0` was accidentally correct. Same additive-overload shape as `IngameUi.Hit` in Task 5. |
| **D17** | **The local team comes from the replicated snapshot, not from `FpsActorController.playerTeam`.** That field is documented as staying `-1` on a server, and V10 needs a value correct at the client role **before the first flag flip**. `ActorSnapshotEntry.Team` for `NetClientBootstrap.LocalActorId` is authoritative and arrives at spawn, which precedes any capture. With no snapshot yet the buttons stay **non-interactable** and the method logs once — it does **not** fall back to team 0, because that is the bug. |
| **D18** | **The hitmarker is shooter-only.** The hit-confirm message is already sent to the shooter alone (phase-05 Task 3). Rendering it for anyone else would tell a player that someone, somewhere, hit something — a server-served wallhack. Recorded because "why does only one client get this event" is exactly the question a future reader answers by broadcasting it. |
| **D19** | **`CombatFeed.cs` and `ClientCombatState.cs` are reused verbatim and extended for nothing.** `HitmarkerModel` and `KillfeedModel` (phase-02 task 6) already consume the wire structs, allocation-free, with severity and expiry; `ClientCombatState` already owns the **local** player's death, respawn timer and ammo prediction (`ApplyDeath` `:287`, `CanRequestRespawn` `:296`). **But `KillfeedEntry.From` (`:159-166`) drops `ForceX/Y/Z`** — so the ragdoll cannot be fed from `KillfeedModel`. Resolved explicitly: **the presenter subscribes `OnDeath` directly for the impulse and pushes the same message into `KillfeedModel` for the line.** One message, two consumers, both files unchanged. |
| **D20** | **New models go in sibling files, not into `CombatFeed.cs`.** It is already 271 lines against the repo's ~200-line convention, and match state, capture points and explosions are not a combat feed. |
| **D21** | **The gate is a `tools/` console check, not an xunit test.** `Ironfront_Reborn/Assets` contains **zero `.asmdef` files**, so no Unity assembly exists for `dotnet test` to load and CI has no licensed Editor — reflection over Unity types is impossible from any project here. `tools/UnitySyntaxCheck` already Roslyn-parses every `.cs` under `Assets/Scripts` (`Program.cs:36`, CI at `ci.yml:103`) and `tools/SpecChecker` already reflects over a referenced assembly *and* reads a file under `Assets/` (`Program.cs:162`, `:185-195`, CI at `ci.yml:92`). **Roslyn over a text scan matters here**: it ignores comments and `#if` blocks, which closes the false-green a naive `+=` scan would leave open. **A registration manifest was rejected**: a test asserting every event has a manifest entry proves the manifest is complete, not that anything is wired — precisely `green-that-proves-nothing.md`. |
| **D22** | **Every presenter is inert unless `NetContext.IsClient`, every singleton dereference is guarded past its own null check, and no handler ever throws.** `ClientMessageRouter.Route` counts malformed input rather than throwing (`:24-29`); a handler that threw would propagate into the transport pump. All four client singletons use a lowercase public static **field** `instance` assigned unconditionally in `Awake()` — no property, no `FindObjectOfType`, and **no duplicate guard**, so a second instance silently wins. |

---

## 3. Detailed tasks

### Task 1 — The lookup seam and the local-actor identity (0.5 day)

Nothing can be presented until an actor id resolves to something in the scene, and **there is no
public lookup today**: `RemoteActorRegistry`'s entire public surface is `LiveCount` (`:55`) and
`PooledCount` (`:58`).

| File | Change |
|---|---|
| `Net/Client/RemoteActorRegistry.cs` | **Edit**, Dev C. Add `public bool TryFind(ushort actorId, out Transform t)` — a `_live.TryGetValue` pass-through. **Named `TryFind` for symmetry with `ServerActorRegistry.cs:109`**, so both sides of the wire read alike. |
| `Net/Client/NetClientPresenterGuard.cs` | **New**, Dev C. `static bool IsPresentable` (`NetContext.IsClient`), **`static bool IsLocalActor(ushort actorId)`** (D2 — the identity predicate that replaces `!aiControlled` everywhere V10 touches), `static bool TryResolveLocalActorId(out ushort id)`, and `static bool TryResolveLocalTeam(out byte team)` (D17). |

**Three traps this seam must carry, all verified:**

1. **The local player is deliberately excluded from `_live`** (`:118`). Every lookup **misses** the
   local actor, and "who fired" / "who died" is frequently the local player. `IsLocalActor` is checked
   first, always.
2. **Transforms are recycled.** Despawn deactivates and pushes back to the pool (`:126-133`); spawn
   pops and reactivates (`:121-123`). **Never cache a transform across a despawn.**
3. **`_client` is captured once in `Awake`** (`:62`). If `NetClientBootstrap.Current` is null then, the
   subscribe silently no-ops **for the object's whole life** — no error, no log. Every new presenter
   logs once at warning on a null resolve rather than inheriting that silence.

**Verify:** `dotnet build Ironfront.sln` clean; Task 12's `ARemoteActorResolvesFromItsNetworkId`,
`TheLocalActorIsNotInTheRemoteRegistry` and `IsLocalActorMatchesOnlyTheBootstrapActorId`.

---

### Task 2 — `RemoteActorView`: the representation the cosmetics hang on (3 days)

**The critical path, and the task that was not in the original brief.** Per **D1**, everything from
Task 4 onward needs it. **Read Task 3 before starting: this task arms A16, and Task 3 is what
disarms it.**

| File | Change |
|---|---|
| `Net/Client/RemoteActorView.cs` | **New**, Dev C. A component on the pooled remote prefab. `Apply(in ActorSnapshotEntry entry)` consumes the fields § 1.2 shows are decoded and discarded. Exposes `Transform MuzzleAnchor`, `Weapon ActiveWeapon`, `Rigidbody MainRagdollBody`, `byte WeaponId`, `byte Team`, `bool IsLocal`. |
| `Net/Client/RemoteActorRegistry.cs` | **Edit**, Dev C. Resolve the `RemoteActorView` **once on spawn** (`:121-123`) into the pooled entry, never per snapshot — `GetComponent` at 30 Hz × 48 actors is the allocation-free-but-slow trap. Feed `view.Apply(entry)` from the existing interpolation loop. |

**What "consume" means per field**, so this does not become an open-ended animation task:

| Field | Rendered as |
|---|---|
| `Pitch` | Aim on the upper body; also the origin ray for Task 5's tracer. |
| `IsCrouching` / `IsProne` | Stance on the animator; also drops `MuzzleAnchor`, so a crouched shooter's flash is at the right height. |
| `IsSprinting` / `IsAiming` | Animator parameters only. |
| `IsRagdoll` | Rig enabled/disabled. **This is the field Task 4 sets and the snapshot then confirms**, so a death arriving out of order self-corrects instead of leaving a standing corpse. |
| `IsAlive` | Gates every cosmetic; a dead actor fires nothing. |
| `WeaponId` | Selects the weapon model, and therefore the `Weapon` whose `PlayFireCosmetics` Task 5 calls. |
| `Team` | Material/insignia, and the value D17 reads for the local actor. |
| `Health` | Consumed into the view and **deliberately not rendered** on a remote actor — recorded so it does not read as an oversight. |
| `IsInWater` / `IsSeated` | **Deliberately not rendered in V10.** `IsSeated` is V5's vehicle work; `IsInWater` has no cosmetic in the original. Named per V1 D5's rule. |

**First hour, before any rendering:** verify the access modifiers on `Actor.activeWeapon`,
`Actor.weapons[]` and `Actor.HasWeaponInSlot(int)` (`Actor.cs:663-664`, `:687`, `:697-699`). They
were **not** verified during planning and they decide whether `RemoteActorView` can reach a `Weapon`
at all. If any is private, widening it is one additive accessor in the same Dev A PR as Task 5.

**Constraint.** `Apply` allocates nothing and runs per interpolated actor per frame. No `foreach`, no
`System.Linq`. Animator parameters are cached `int` hashes resolved once.

**Verify:** engine-free — `RemoteActorViewStateTests` grade the **decode-to-intent** mapping through
a fake view interface (flags in → stance/aim/ragdoll intent out), the half testable without Unity.
Rendering is **E7**.

> **Honest limit.** Whether `_remoteActorPrefab` (`RemoteActorRegistry.cs:42`) carries an animator, a
> ragdoll rig, a muzzle anchor and a weapon mount is **authored in the Editor and unreadable from
> source**. It is **E1**, blocking, and Task 4's degraded path exists for the case where it is unmet.

---

### Task 3 — Local-only singletons stop firing for remote actors (A16) (2 days)

**Sequence this with Task 2, not after it.** Task 2 gives a remote actor a controller that is not
`AiActorController`, and `aiControlled` (`Actor.cs:178`, frozen in `Awake` from
`controller.GetType()`) then reads **false** for every remote human — which is what turns § 1.5's
latent hijack live.

**Files:** `Assembly-CSharp/Actor.cs`, `Assembly-CSharp/TankTurret.cs`,
`Assembly-CSharp/MountedTurret.cs` (Dev A files — one PR, one review round, per design § 7).

The change is uniform and mechanical: **every client-only singleton touch reached from a per-actor
path is gated on `IsLocalActor`, not on `!aiControlled`.** `aiControlled` keeps its original meaning
and is neither read for identity nor written (D2 — and therefore V5-D7's
`AiControlledIsUnchangedForANetworkedDriver` still holds).

Verified scope — `grep -n "aiControlled" Actor.cs` gives **eight** `!aiControlled` sites (`:223`,
`:716`, `:824`, `:853`, `:1124`, `:1139`, `:1166`, `:1181`) plus `:267` / `:279` on the inverse. Each
is triaged, not blanket-rewritten:

| Site | Today | Change |
|---|---|---|
| `Actor.cs:824-829` | `IngameUi.instance.SetHealth` + `ShowVignette`. **Wrong today** — a remote human writes *your* health bar from *their* health, because `:790` `ReceivedDamage` and the `else if` chain are not role-gated. | Gate on `IsLocalActor`. |
| `Actor.cs:716-719` | `IngameUi.instance.Hide()` inside `Die`. | Gate on `IsLocalActor`. |
| `TankTurret.cs:41-45`, `MountedTurret.cs:31-35` | `FpsActorController.instance.DisableCameras()` in `Unholster`. | Gate on `IsLocalActor`. **The guard is in `Unholster`, not in a combat event — a review scoped to combat events never reaches it.** |
| `MountedTurret.cs:44` | `FpsActorController.instance.EnableCameras()` in `Holster`. | Gate on `IsLocalActor`. The mirror is as damaging as the original. |
| The remaining `!aiControlled` sites | Local-player HUD and input concerns. | **Audited and each classified** as local-only (gate) or genuinely AI-vs-human (leave). The audit result is recorded in the PR, so the next reader sees which were considered and left. |

**At `NetRole.Offline`, `IsLocalActor` returns true for the player's actor and false for AI**, which is
exactly what `!aiControlled` meant there — so **offline single-player is byte-for-byte unchanged**.
That equivalence is the whole reason this is a safe mechanical change, and it is pinned by a test.

**Verify:** `OfflineLocalActorGatingMatchesAiControlled` pins the equivalence across a player actor
and an AI actor. Plus **the A16 grep gate** in Task 11: no `FpsActorController.instance` or
`IngameUi.instance` reached from a per-actor path without an `IsLocalActor` guard. Camera behaviour
itself is **E11**.

---

### Task 4 — The reuse audit, and the models genuinely missing (1.5 days)

`search-before-you-build.md` first. Three streams are **already modelled** and cost zero new code
(D19): `HitmarkerModel` (hit confirm), `KillfeedModel` (the killfeed line), `ClientCombatState` (the
**local** player's death, respawn timer, ammo prediction). What is absent goes in sibling files (D20)
under `Ironfront.Net.Replication/Client/`:

| File (all new) | Contents |
|---|---|
| `DeathImpulse.cs` | `readonly struct` — `VictimActorId`, `KillerActorId`, `CauseOfDeath`, `Vec3 Force`, `bool KilledByEnvironment`. `static From(in DeathMessage)`. **D19's fork:** `KillfeedEntry.From` drops the force, so the ragdoll is fed from here and the line from `KillfeedModel`, off one message. |
| `ShotEvent.cs` | `readonly struct` — `ShooterActorId`, `WeaponId`, `Vec3 Direction`. `static From(in WeaponFireMessage)`. **No state and no accumulation** (D9). |
| `MatchStateModel.cs` | Latches the last `MatchStateMessage`. `Apply(in, float now)`, `SecondsRemaining(float now)`, `IsStale(float now)`. Phase-specific timer rule in Task 7. |
| `CapturePointView.cs` | `Apply(in CapturePointMessage)` into a fixed array indexed by `PointId`; `OwnerQ`, `OwningTeam`, `IsContested`, `DirtySinceLastRead(int)`. |
| `ExplosionSuppressor.cs` | D13's mechanism. `PredictLocal(ushort sourceActorId, float now)`, `bool ShouldSuppress(in ExplosionMessage, float now)`. Fixed ring; entries expire after `SuppressionWindowSeconds` (default `1.0f`). |

**Five decode traps, each verified and each silently wrong if missed:**

| # | Trap |
|---|---|
| 1 | **Use `Quantize.UnpackVel16(short)` (`Quantize.cs:130`)** for the death force and the shot direction — **not** `UnpackVel(sbyte)` (`:105`). The `i8` form is the *snapshot's* slot and saturates at 64 m/s; it would clamp every kill's force and make heavy weapons feel identical to light ones. `PackVel16`'s doc (`:107-118`) names these two messages explicitly. |
| 2 | **`DeathMessage` is victim-first, killer-second** (`CombatMessages.cs:84-85`). `KillfeedEntry`'s ctor (`CombatFeed.cs:146`) is the **opposite** order. Trivially swappable, silently wrong. |
| 3 | **`DeathMessage.HitboxHit` is a raw `byte`, not `HitboxType`.** Cast at the use site, as `CombatFeed.cs:165` already does. |
| 4 | **Explosion position uses `Quantize.UnpackPos(short)` (`:57`)** — a different pair from the velocity path. |
| 5 | **A capture point can be fully owned *and* contested at once** (`GameplayEnums.cs:182-188`). `IsContested` is not mutually exclusive with `OwningTeam`. |

**Message-type names.** The `S_*` spellings exist only in `protocol-spec.md` § 4.1 and doc comments.
The C# identifiers are `ServerMessageType.Death` (0x44), `.WeaponFire` (0x49), `.HitConfirm` (0x43),
`.MatchState` (0x45), `.CapturePoint` (0x46), `.Explosion` (0x4A) (`MessageTypes.cs:29-52`). **Do not
write `S_DEATH` as a C# identifier.**

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~ClientEvent` —
red until Task 12 lands. **`CombatFeed.cs` and `ClientCombatState.cs` show zero diff**; checking those
two files is the cheapest confirmation D19 held.

---

### Task 5 — Death and the ragdoll (1.5 days)

**Files:** new `Net/Client/NetClientCombatPresenter.cs` (Dev C). **Needs Tasks 1, 2, 4.**

Subscribes `OnDeath`. Per **D19**, one message feeds two consumers:

1. `KillfeedModel.Push(in message, Time.time)` — the line; `Prune(Time.time)` once a frame, because
   the type deliberately has no clock (`CombatFeed.cs:176-183`).
2. `DeathImpulse.From(message)` — the force the killfeed drops.
3. Resolve the victim: `IsLocalActor` first (Task 1 trap 1) — the local player's death is already
   owned by `ClientCombatState.ApplyDeath` (`:287`) and V10 does not duplicate it. Otherwise
   `registry.TryFind`. A miss is a **normal outcome** (the victim died outside interest range) and
   draws the line without a corpse. **It is not an error and must not log as one.**
4. `view.Actor.KnockOver(force)` — **D5's ready-made public pair**: `Actor.cs:632` enables the ragdoll
   via `FallOver()` and applies the impulse in one call, with its own `if (!ragdoll.IsRagdoll())`
   re-entrancy guard.

**`Actor.Die` is not called and not widened.** It is `private` (`:691`) with one caller (`:809`), and
`:722` calls `ScoreUi.AddScore` — which on a client would **double-count against the server's
authoritative `MatchStateMessage`**. `ServerActorDamageSink.cs:69-73` already documents that the
netcode deliberately does not call it. `Actor.ApplyRigidbodyForce` is likewise untouched (D5).

**Degradation, stated rather than silent** (E1 may be unmet):

| Prefab state | Behaviour |
|---|---|
| Rig present | `KnockOver(force)`; the snapshot's `IsRagdoll` then confirms it. |
| No rig | Log **once per session at warning naming E1**, hide the transform, play the death effect. A silent no-op would be indistinguishable from the bug this phase exists to close. |

**Verify:** engine-free — `ADeathMessageProducesOneKillfeedLineAndOneImpulse`,
`TheDeathForceUnpacksThroughVel16NotVel8`, `AnEnvironmentKillerResolvesToTheEnvironmentFlag`,
`ALocalDeathIsLeftToClientCombatState`. Rendering is **E7**.

---

### Task 6 — Shooting feedback: flash, report, tracer, hitmarker (2.5 days)

**Files:** `Net/Client/NetClientCombatPresenter.cs` (continued); new
`Net/Client/CosmeticTracerPool.cs` (Dev C); `Assembly-CSharp/Weapon.cs`,
`Assembly-CSharp/IngameUi.cs` (Dev A files — same PR as Task 3).

**Weapon fire → the shot.** `ShotEvent.From(message)`, resolve the shooter, then:

- `view.ActiveWeapon.PlayFireCosmetics()` — **D7's extraction**. One shared method holding only the
  flash (`Weapon.cs:340-343`) and the report (`:356`, `:362-365`); `Shoot` calls it too, so there is
  one copy and offline is byte-for-byte unchanged. **`SpawnProjectile` and `ApplyRecoil` are outside
  it by construction**, so no cosmetics path can reach damage or your camera.
- One report per message, not the `Fire()` loop (**D8**) — `Shoot` alone is silent on an automatic
  weapon.
- A tracer from `CosmeticTracerPool` along `Direction` from `MuzzleAnchor`.

**Per D9 the cosmetic path is stateless**: it never reads or advances `currentMuzzle`
(`AlternatingMountedWeapon.cs:12`, `:19-22`). A dropped fire event must cost one missing flash, never
a permanently offset muzzle.

Earshot filtering is **already done server-side** by `ServerEventWriter.WeaponFireAudibleRadius`, so
the presenter plays every message it receives and adds no distance test — a second filter would be a
second thing to keep in agreement with the first.

**Per D10 the tracer is new.** `CosmeticTracerPool` is a pre-warmed pool of streaks with a fixed
lifetime, **no collider, no `Projectile` component, no `source`**. One file, so "does this tracer do
damage" has exactly one place to check.

**Hit confirm → the hitmarker.** `HitmarkerModel.Push(in message, tick, Time.time)` — shipped model,
unchanged. Drawn while `IsVisible(Time.time)`. Shooter-only by **D18**.

**The one gap needing a new signature.** `IngameUi.Hit()` is `public static void Hit()` with **no
parameters** (`IngameUi.cs:65`), so it cannot express the severity `HitmarkerModel` computes —
`Normal`, `Headshot`, `Kill`, each with its own colour and pitch (`CombatFeed.cs:12-22`).

```csharp
// IngameUi.cs — minimum change; every existing caller preserved
public static void Hit() => Hit(0);
public static void Hit(int severity) { /* 0 normal, 1 headshot, 2 kill */ }
```

`int` rather than `HitmarkerSeverity`: `Assembly-CSharp` takes no dependency on
`Ironfront.Net.Replication` for a cosmetic enum. **Do not reach for `ShowHitmarker()`** — it is
`private` (`IngameUi.cs:172`), and `plans/00-shared/architecture.md:314` calls it, which will not
compile (§ 7 drift list). **Also do not confuse `Hit()` with `Hit(Ray, RaycastHit)`** at
`Projectile.cs:119`, `ExplodingProjectile.cs:43`, `Rocket.cs:18` — those are `protected` members of
the projectile hierarchy, a costly misread.

**Verify:** engine-free — `AWeaponFireMessageDecodesToAShotEvent`, `AnUnknownWeaponIdDoesNotThrow`,
`AHitConfirmRaisesTheMarkerAndTheNewestHitWins`, `AKillHitmarkerOutranksAHeadshot`. Plus the **D7
grep gate** in Task 11: no file under `Net/Client/` references `SpawnProjectile` or `ApplyRecoil`.
Cosmetics are **E3**, **E4**, **E7**.

---

### Task 7 — The match HUD (1.5 days)

**Files:** new `Net/Client/NetClientObjectivePresenter.cs` (Dev C); `Assembly-CSharp/ScoreUi.cs`
(Dev A file — same PR as Task 3).

**`ScoreUi` gains an authoritative render entry point (D11).**

```csharp
// ScoreUi.cs — renders the server's numbers; never re-enters AddScore/AddFlag
public static void SetAuthoritativeState(
    int phase, int tickets0, int tickets1, int secondsRemaining, int humanPlayerCount)
```

It renders and returns. It does **not** touch `ScoreMultiplier` (`:64-65`) or the `victoryPoints` win
check (`:75-85`), because feeding authoritative counts through the delta API would double-drive the
win condition — `ScoreUi`'s mutators are delta-only with no getters, and `MatchStateMachine`'s state
is get-only, so there is no route between them that is not a new setter. **The `ScoreUi.cs:46-57`
remarks are updated in the same commit** so the comment stops contradicting the code.

Per **D12** this closes the *rendering* half of V8 D9's divergence and only that half: `ScoreUi` still
holds match state that does not run headless, and that remains V8 D9's recorded divergence.

**The phase-specific timer rule, which a naive HUD gets wrong.**
`MatchStateMessage.PhaseSecondsRemaining` is **0 during `MatchPhase.Playing`**
(`MatchMessages.cs:44-47`) — that phase ends on tickets, not a clock.

| Phase | Timer |
|---|---|
| `WaitingForPlayers`, `Warmup`, `Ended`, `Resetting` | Meaningful. Interpolated via `SecondsRemaining(Time.time)`, because the value arrives at the broadcast rate and a timer that only moves when a packet lands reads as a stutter. |
| `Playing` | **Hidden, not rendered as `0:00`.** Rendering a zero would tell every player the round is over. |

`WinningTeam` (`MatchMessages.cs:69`) is a **computed property with no wire field** — use it.
`TeamId.None` is **255, not 2** (`GameplayEnums.cs:170`), chosen so a client switching on 0/1 falls
through rather than rendering neutral as a third team. `IsStale` dims the HUD rather than showing a
stale number as live — `development-principles.md` § "Errors Over Silent Fallbacks", applied to a clock.

**Constraint.** No per-frame string allocation — strings rebuild only on change, the fix phase-05
Task 7 M8 already made for the lobby overlay.

**Verify:** engine-free — `AMatchStateMessageAppliesEveryField`, `ThePlayingPhaseRendersNoTimer`,
`ThePhaseTimerInterpolatesOutsidePlaying`, `AStaleMatchStateIsReportedStaleNotZero`,
`ATieResolvesToTeamIdNone`. Plus a **grep gate**: the objective presenter references neither
`AddScore` nor `AddFlag`. Layout is **E5**, **E8**.

---

### Task 8 — Capture points (1 day) — **blocked on V8 Task 1, severable, last**

**Files:** `Net/Client/NetClientObjectivePresenter.cs` (continued).

**Hard precondition (D15): V8 Task 1 is on `develop`.** Two reasons, both correctness:

1. `ApplyAuthoritativeOwner(int team, float control, bool contested)` does not exist until it lands,
   and V8 D3 already names this handler as one of its two callers.
2. Until V8 D2 lands, `CapturePoint.UpdateOwner` is **still running its own 1 Hz arithmetic on the
   client**. Writing replicated ownership beside it makes two client-side writers — design § 2.1's
   bug, one process out, and harder to see because both would be ours.

On each message: `CapturePointView.Apply(in message)`, then for each dirty point call
`ApplyAuthoritativeOwner(team, control, contested)` with `control = Math.Abs(OwnerQ) / 100f` — the
same `Abs` mapping `CapturePointSlave.Apply` uses on the server (V8 Task 3).

**What comes free, stated precisely.** `ApplyAuthoritativeOwner` calls the existing `SetOwner(team)`
once per flip, which already drives the flag renderer and `MinimapUi.UpdateSpawnPointButtons`. **That
updates spawn-point button interactability — the respawn UI — not a capture-point marker.**
`MinimapUi` has **no capture-point marker API at all**: its markers are `SpawnPoint` buttons built
once in a private `SetupMinimap()`, and `AddActorBlip` (`:172`) is add-only and takes an `Actor` while
the registry stores a `Transform`. **A capture-point minimap marker is build-new and is out of V10's
scope** — recorded in § 7 with an owner rather than implied by "the minimap comes free".

The capture bar is the presenter's, read from `OwnerQ`. Neutral maps to `-1` explicitly rather than by
cast (V8 Task 3's reason). A point may be owned **and** contested (Task 4 trap 5).

**Verify:** engine-free — `ACapturePointMessageAppliesToTheView`, `AnOwnedPointCanAlsoBeContested`,
`ANeutralPointDoesNotResolveToTeamZero`, `TheViewMarksOnlyChangedPointsDirty`, graded against a fake
component implementing V8's method. Rendering is **E9**.

---

### Task 9 — The minimap team-0 hardcode, the empty catch, and the NPE both hide (1 day)

**Files:** `Assembly-CSharp/MinimapUi.cs`, `Assembly-CSharp/CapturePoint.cs`,
`Assembly-CSharp/DecalManager.cs` (Dev A files — same PR as Task 3).

**Why V10 and not V8.** This is client-side UI reading the local player's team — V10's remit. V8
touches the call path but **explicitly preserves the `UpdateSpawnPointButtons` call** through its new
`ApplyAuthoritativeOwner` (`phase-v8-objectives.md:87`), so the bug survives V8's refactor untouched
unless V10 fixes it. **Neither phase should assume the other did.**

| # | Site | Defect | Fix |
|---|---|---|---|
| 1 | `MinimapUi.cs:129`, `:140` | `int num = 0;` **never reassigned**; `button.interactable = owner == num`. Every team-1 player cannot select a spawn point. | **D16** — `UpdateSpawnPointButtons(int localTeam)`, no-arg overload preserved and delegating. |
| 2 | `MinimapUi.cs:125` → `:130` | Guards `instance == null`, then dereferences `instance.minimapSpawnPointButton`, built later in `SetupMinimap()` (`:58`, dict at `:67`). **Network messages arrive before `Start()`**, so a net-driven caller hits this. | Guard the collection too; return early with **one** logged warning. |
| 3 | `CapturePoint.cs:262-268` | `try { … } catch (Exception) { }` — a **bare empty catch** swallowing defect 2 and anything else on the objectives path. | Delete it. Defect 2's guard is the real fix; anything still thrown logs at error, per `development-principles.md` § "Errors Over Silent Fallbacks". |
| 4 | `DecalManager.cs:138` | `AddDecal` guards `instance == null` then dereferences collections built later in `StartGame()` — **the same NPE-past-the-guard shape**, on a path Task 11's explosions will drive. | Same fix as defect 2. Found while auditing defect 2; fixed here because it is one line and the same bug. |

**The forensic note that justifies treating this as real rather than speculative.** `ScoreUi.AddFlag`
sits at `CapturePoint.cs:261`, immediately **outside** the try block, on the same path with the same
guard style. Someone wrapped the `MinimapUi` call **specifically**. That is behavioural evidence the
throw was **observed and reproducible**, not defended against speculatively — which is why defect 3 is
a deletion plus a real guard, not a deletion alone.

**Where `localTeam` comes from — D17.** The local actor's replicated `ActorSnapshotEntry.Team`, via
`NetClientPresenterGuard.TryResolveLocalTeam` (Task 1). **Not** `FpsActorController.playerTeam`, which
is documented as staying `-1` on a server. Spawn precedes any capture, so the snapshot always arrives
first; if it somehow has not, the buttons stay non-interactable and the method logs once — it does
**not** fall back to team 0, because that is the bug. At `NetRole.Offline` the no-arg overload passes
`0`, so offline is byte-for-byte unchanged (D16).

**Verify:** `ATeamOnePlayerGetsInteractableSpawnPointButtons` (the reported bug),
`ATeamZeroPlayerIsUnchangedFromToday`, `AnUnresolvedLocalTeamLeavesButtonsDisabledRatherThanTeamZero`.
Plus the **empty-catch grep gate** in Task 11 — mechanical, so it belongs in a gate rather than a
reviewer's memory.

---

### Task 10 — Explosions, with local prediction for your own (1.5 days)

**Files:** new `Net/Client/NetClientExplosionPresenter.cs` (Dev C). **Supersedes V1 Task 4 (D14).**

```
ExplosionSuppressor.ShouldSuppress(message, Time.time)
  ├─ true  → drop it. This is the confirmation of a blast already drawn locally.
  └─ false → unpack centre (Quantize.UnpackPos) + radius (V1's ExplosionEncoding.UnpackRadiusMetres),
             index the serialized ParticleSystem[] by (byte)Kind, scale effect + camera shake
             by radius, apply corpse ragdoll impulse locally.
```

**The prediction half (D13).** When this client's own explosive detonates, the local path calls
`ExplosionSuppressor.PredictLocal(localActorId, Time.time)` and plays the effect **immediately**; the
confirming message then matches on `SourceActorId` and is dropped.

**Why a window and not a pending flag.** A prediction is held for `SuppressionWindowSeconds` (default
`1.0f`) rather than until a confirmation arrives. A grenade destroyed in flight never produces one,
and an unbounded entry would eat the **next** real explosion from the same actor — turning a cosmetic
latency win into a missing blast. Expiry bounds the damage to D13's accepted cost: one phantom flash,
never a swallowed one.

`SourceActorId` uses `DeathMessage.EnvironmentKiller` (`0xFFFF`) for a world blast
(`ActorLifecycleMessages.cs:158`), which can never match a local actor id and is therefore never
suppressed — correct by construction, recorded so nobody adds a special case.

**Corpse ragdoll impulse on clients stays as-is** (`phase-v1-explosions.md:126`, AD-4 — corpses are
never replicated). **This applies no health damage**; health arrives in the snapshot. An
`ExplosionKind` this build does not know draws nothing rather than throwing — V1 Task 4's rule,
carried over with the file. **There is no scorch `DecalType`** — the enum is `Impact` / `BloodBlue` /
`BloodRed` (`DecalManager.cs`), so explosions reuse `Impact` as they do today; a scorch type is § 7's
recorded gap, not silently missing.

**Verify:** engine-free — `AnOwnExplosionIsSuppressedOnce`,
`ASuppressedPredictionExpiresAndDoesNotEatTheNextBlast`, `AForeignExplosionIsNeverSuppressed`,
`AWorldSourcedExplosionIsNeverSuppressed`, `AnUnknownExplosionKindDoesNotThrow`. V1 Task 5's
`AnExplosionFramedByTheServerRoutesToTheClientHandler` grades the router join and is **not**
duplicated. Cosmetics are **E6**, **E10**.

---

### Task 11 — The gate (1.5 days)

The point of the phase. Without it, the seventh dead event is a matter of time.

**Files:** new `tools/ClientWiringGate/` (Dev C), on the `UnitySyntaxCheck` pattern —
`Microsoft.CodeAnalysis.CSharp` 4.14.0 at `LanguageVersion.CSharp9`, plus a `ProjectReference` to
`Ironfront.Net.Replication`. **One new line in `ci.yml`**, beside the existing gates at `:92` and
`:103`.

Per **D21**, two halves with different mechanisms:

| Half | Mechanism | Why |
|---|---|---|
| **Enumerate** the events | `typeof(ClientMessageRouter).GetEvents(BindingFlags.Public \| BindingFlags.Instance)` | Engine-free type. A renamed event changes the gate's input automatically. Precedent: `WeaponIdTests.cs:24-25` does this with `GetFields`; `SpecChecker/Program.cs:6` does it in a `tools/` gate. |
| **Detect** subscribers | **Roslyn** parse of `Assets/Scripts/**/*.cs` | `Ironfront_Reborn/Assets` has **zero `.asmdef` files**, so no Unity assembly exists to reflect over and `ci.yml`'s `unity-compile` is disabled for want of a licensed Editor. Roslyn over a raw text scan **ignores comments and `#if` blocks**, closing the false-green a naive `+=` scan would leave. |

**Four checks, one pass over the tree:**

| # | Check | Guards |
|---|---|---|
| G1 | Every router event has ≥1 subscription in a non-test file outside `ClientMessageRouter.cs` | The phase's whole thesis |
| G2 | No file under `Net/Client/` references `SpawnProjectile` or `ApplyRecoil` | **D7** — a cosmetics path becoming a damage path, or kicking your camera |
| G3 | No empty `catch (Exception) { }` in `CapturePoint.cs` | **Task 9 defect 3** |
| G4 | No `FpsActorController.instance` or `IngameUi.instance` reached from a per-actor path without an `IsLocalActor` guard | **A16 / Task 3** — the next one, not just this one |

**Exclusions, each for a stated reason:** `ClientMessageRouter.cs` itself (declarations and `Invoke`
sites are not subscriptions), `obj/` and `bin/`, and any `*Tests.cs`. **Today's tests would supply
four false positives** (`ClientCombatTests.cs:522-524`, `:562`), so the test exclusion is load-bearing
rather than tidy.

**Two loud failures, not two skips.**

1. Repo root not found → **fail**, naming what it searched for.
2. **It asserts it found exactly nine events and scanned more than zero `.cs` files.** Taken from
   `UnitySyntaxCheck`'s own code, which errors on an empty file set because *"a check that passes
   because it looked at nothing is worse than no check: it reports green forever from the wrong
   working directory."*

**Proving the gate can fail.** A check never seen failing is unproven, so the detectors are pure
functions over a parsed tree and are unit-tested against fixtures in
`Ironfront.Net.Replication.Tests`:

| Test | Asserts |
|---|---|
| `TheGateFindsASubscriptionInAFixture` | `Router.OnDeath += Handler;` reports subscribed |
| `TheGateReportsAnUnsubscribedEventInAFixture` | declaration only reports **unsubscribed** — the red path, every CI run |
| `TheGateIgnoresACommentedOutSubscription` | the false-green Roslyn exists to close |
| `TheGateIgnoresATestFileSubscription` | a path ending `Tests.cs` does not count |
| `TheGateFlagsAnUnguardedLocalSingletonTouch` | G4's red path |
| `TheGateFlagsAnEmptyCatch` | G3's red path |
| `TheGateFailsWhenItScansZeroFiles` | the empty-file-set failure |

**The expected-pass set is nine and three pass today.** Before this phase's presenters land, G1 fails
naming exactly the six — the correct starting state, and the proof the gate discriminates rather than
blanket-fails.

**Verify:** `dotnet run --project tools/ClientWiringGate --configuration Release --no-build` exits
non-zero today naming six events, and zero after Tasks 5-10. Delete one `+=` locally and watch it go
red before merging.

---

### Task 12 — Tests (2.5 days, written alongside Tasks 1-11)

All engine-free, all in CI, no Editor. New files
`Ironfront.Net.Replication.Tests/ClientEventConsumptionTests.cs`, `RemoteActorViewStateTests.cs`,
`ClientWiringGateTests.cs`. **xunit 2.9.3, net8.0.** Central Package Management is on with
`CentralPackageVersionOverrideEnabled=false`, so **any inline `Version=` is an NU1008 error on
purpose** — new packages go in `Directory.Packages.props` first.

| Test | Asserts |
|---|---|
| `ARemoteActorResolvesFromItsNetworkId` | Task 1's seam; a miss returns false rather than throwing |
| `TheLocalActorIsNotInTheRemoteRegistry` | `RemoteActorRegistry.cs:118`'s deliberate exclusion |
| `IsLocalActorMatchesOnlyTheBootstrapActorId` | **D2** — the predicate that replaces `!aiControlled` |
| `OfflineLocalActorGatingMatchesAiControlled` | **Task 3's safety proof** — offline behaviour is unchanged |
| `SnapshotFlagsMapToStanceAimAndRagdollIntent` | Task 2's decode-to-intent |
| `SeatedAndInWaterAreDecodedAndDeliberatelyUnrendered` | pins the recorded non-consumption |
| `ADeathMessageProducesOneKillfeedLineAndOneImpulse` | D19's fork: one message, two consumers |
| `TheDeathForceUnpacksThroughVel16NotVel8` | trap 1 — the failure that would clamp every kill |
| `TheKillfeedEntryArgumentOrderIsVictimKillerCorrect` | trap 2 — the swap that compiles and is wrong |
| `AnEnvironmentKillerResolvesToTheEnvironmentFlag` | `0xFFFF`, not actor 65535 |
| `ALocalDeathIsLeftToClientCombatState` | no duplicate local death path |
| `AWeaponFireMessageDecodesToAShotEvent` | shooter, weapon, direction |
| `AnUnknownWeaponIdDoesNotThrow` | forward compatibility |
| `TheCosmeticPathNeverAdvancesAMuzzleIndex` | **D9** — the desync a dropped packet would cause |
| `AHitConfirmRaisesTheMarkerAndTheNewestHitWins` | `HitmarkerModel`'s shipped semantics survive |
| `AKillHitmarkerOutranksAHeadshot` | `SeverityOf`, so `Hit(int)` gets the right number |
| `AMatchStateMessageAppliesEveryField` | all five fields |
| `ThePlayingPhaseRendersNoTimer` | the `0` that must not become `0:00` |
| `ThePhaseTimerInterpolatesOutsidePlaying` | the timer moves between broadcasts |
| `AStaleMatchStateIsReportedStaleNotZero` | unknown is not good |
| `ATieResolvesToTeamIdNone` | `None == 255`, not 2 |
| `TheHudNeverRoutesTicketsThroughAddScore` | **D11** — the double-driven win check |
| `ACapturePointMessageAppliesToTheView` | Task 8 |
| `AnOwnedPointCanAlsoBeContested` | trap 5 |
| `ANeutralPointDoesNotResolveToTeamZero` | V8 Task 3's mapping, client-side |
| `TheViewMarksOnlyChangedPointsDirty` | repaint on change |
| `ATeamOnePlayerGetsInteractableSpawnPointButtons` | **Task 9 defect 1 — the reported bug** |
| `ATeamZeroPlayerIsUnchangedFromToday` | Task 9's no-regression half |
| `AnUnresolvedLocalTeamLeavesButtonsDisabledRatherThanTeamZero` | D17 — the fallback that is not the bug |
| `AnOwnExplosionIsSuppressedOnce` | D13 |
| `ASuppressedPredictionExpiresAndDoesNotEatTheNextBlast` | the window bound |
| `AForeignExplosionIsNeverSuppressed` | keys on `SourceActorId` alone |
| `AWorldSourcedExplosionIsNeverSuppressed` | `0xFFFF` never matches a local id |
| `AnUnknownExplosionKindDoesNotThrow` | carried from V1 Task 4 |
| `NoHandlerThrowsOnAMalformedMessage` | D22 — the router counts rather than throws (`:24-29`) |
| `NoClientModelAllocatesOverAThousandEvents` | § 3.2, across all five new models |
| *(plus Task 11's seven gate-fixture tests)* | the gate's own red paths |

---

## 4. Acceptance criteria

1. Every one of the nine `ClientMessageRouter` events has ≥1 production subscriber outside the test
   projects, and gate check **G1** passes.
2. The gate is **proven able to fail**: `TheGateReportsAnUnsubscribedEventInAFixture`,
   `TheGateIgnoresACommentedOutSubscription` and `TheGateFailsWhenItScansZeroFiles` are green,
   exercising the red paths every CI run.
3. A remote player visibly crouches, aims, holds the replicated weapon and ragdolls — from snapshot
   fields that arrive today and are currently discarded.
4. **No client-only singleton fires on behalf of a non-local actor.** A remote player taking damage
   does not move your health bar; a remote player entering a turret does not disable your cameras.
   Gate check **G4** passes, and `OfflineLocalActorGatingMatchesAiControlled` proves offline is
   unchanged.
5. A remote player's shot produces a flash, a report and a tracer through `Weapon.PlayFireCosmetics`;
   **no file under `Net/Client/` references `SpawnProjectile` or `ApplyRecoil`** (gate **G2**); and
   the cosmetic path never reads or advances `currentMuzzle`.
6. A death drives the corpse from the replicated force via `KnockOver`. Where the prefab has no rig it
   logs once naming **E1** and degrades visibly. It never silently does nothing.
7. A client-role actor that reaches zero health dies. Today it cannot (§ 1.3).
8. A hitmarker appears on the shooter's client and no other, and a kill marker outranks a headshot.
9. The HUD renders all five match-state fields through `ScoreUi.SetAuthoritativeState`; **no ticket
   count is routed through `AddScore`/`AddFlag`**; the timer is hidden during `Playing`, interpolates
   outside it, reports staleness, and a tie resolves to `TeamId.None`.
10. **A team-1 player can select a spawn point from the minimap**, a team-0 player is unchanged, and
    an unresolved local team leaves the buttons disabled rather than defaulting to team 0.
11. **No empty `catch (Exception) { }` remains in `CapturePoint.cs`** (gate **G3**), and both
    `MinimapUi` and `DecalManager` guard the collections they dereference, not just `instance`.
12. Capture points render via `ApplyAuthoritativeOwner`, and **no client-side code writes `owner`,
    `control`, `pendingOwner` or `isContested` by any other path** — the same check V8 criterion 3
    makes server-side.
13. Your own explosion renders immediately and exactly once; the confirmation is suppressed; a foreign
    or world-sourced explosion never is; an unconfirmed prediction expires without eating the next.
14. **`CombatFeed.cs` and `ClientCombatState.cs` have zero diff** (D19).
15. `IngameUi.Hit()`, `MinimapUi.UpdateSpawnPointButtons()` and `Weapon.Shoot` still behave identically
    for every existing caller; all three new forms are additive.
16. Offline single-player is unchanged: presenters are inert at `NetRole.Offline`, the minimap overload
    passes `0`, `Shoot` still plays the same cosmetics, and `IsLocalActor` matches `!aiControlled` there.
17. `dotnet test` green across the solution; the new gate exits zero; no `System.Linq`, no `foreach`,
    no per-event allocation in any new logic file.
18. `PROTOCOL_VERSION` unchanged and `tools/SpecChecker` passes untouched.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| **Task 2 arms A16 and Task 3 has not landed**, so a remote player entering a turret disables the local player's cameras and a remote player taking damage drives the local HUD | **4** | **5** | **20** | § 1.5 and **D2**. Task 3 is sequenced **with** Task 2, not after — the timeline pairs them explicitly. Gate check **G4** fails the build on an unguarded per-actor singleton touch, so this cannot be reintroduced by the next author either. The guard sits in `Unholster`/`Holster`, which no combat-scoped review would reach — which is exactly why the mitigation is a gate and not a review. |
| **The phase reports green in CI while nothing renders.** Nearly every deliverable is cosmetic and CI has no Editor | **4** | **5** | **20** | Structural. (a) CI grades *contracts* and the phase says plainly it grades nothing visual. (b) **E1-E12 are enumerated in § 7 with pass conditions**, not handed over as a category. (c) **E7-E11 are two-client tests with explicit pass conditions.** (d) Criteria 4, 6 and 10 forbid silent degradation by name. |
| **Task 2 is the real critical path and was not in the original brief.** Largest single task; everything from Task 5 depends on it | **4** | **4** | **16** | Named as the critical path in § 6 and estimated at 3 days rather than folded into "wiring". Tasks 4, 7, 9, 10 and 11 are deliberately **independent of it**, so an overrun does not stall the HUD, the minimap fix, explosions or the gate. |
| **`_remoteActorPrefab` carries no animator, rig, muzzle anchor or weapon mount.** Unreadable from source | **4** | **4** | **16** | **E1 is blocking** and first in § 7. Task 5 ships the degraded path deliberately. Engine-free tests grade decode and models, which hold either way — only the final third of Tasks 2, 5 and 6 is gated on the prefab. |
| **Task 8 lands before V8 Task 1**, adding a second client-side ownership writer | 3 | **5** | **15** | **D15** — hard precondition in the PR description; Task 8 is **severable and last**. Criterion 12's check mirrors V8 criterion 3, so a second writer fails review on both sides of the wire. |
| **Cosmetics reach the damage path or the local camera.** `SpawnProjectile` sets `source = user` (`:392`); `ApplyRecoil` chains to the local `fpParent` (`FpsActorController.cs:409`) | 2 | **5** | **10** | **D7** puts both **outside** the extracted `PlayFireCosmetics` by construction, and gate **G2** asserts it mechanically. This mitigation cannot decay, because it fails the build rather than a review. |
| **The muzzle counter desyncs on a dropped fire event** and does not reproduce on a clean network | 3 | 4 | **12** | **D9** — the cosmetic path is stateless and never touches `currentMuzzle`. `TheCosmeticPathNeverAdvancesAMuzzleIndex` pins it. The failure would otherwise be invisible in exactly the environment it is tested in. |
| **The minimap fix changes offline single-player**, where `num = 0` was accidentally correct | 3 | 4 | **12** | **D16** — the no-arg overload passes `0` at `NetRole.Offline`; `ATeamZeroPlayerIsUnchangedFromToday` pins it. |
| **A presenter's `Awake` runs before `NetClientBootstrap.Current` exists**, so its subscribe silently no-ops for the object's whole life (`RemoteActorRegistry.cs:62`) | 3 | 4 | **12** | Every presenter logs once at warning on a null resolve rather than inheriting the silence; `[DefaultExecutionOrder(-50)]` matches the registry; **E12** covers scene ordering. **The gate cannot catch this** — it is runtime ordering, not source shape, and saying so is why E12 exists. |
| The Dev A PR is now five files (`Actor.cs`, `TankTurret.cs`, `MountedTurret.cs`, `Weapon.cs`, `IngameUi.cs`, `MinimapUi.cs`, `CapturePoint.cs`, `DecalManager.cs`, `ScoreUi.cs`) and conflicts with Dev A's branch | 4 | 3 | **12** | One PR, early, announced, with the offline-unchanged tests attached. **Every change is additive or a deleted empty catch** — no signature is removed and no existing caller is broken, so a conflicting merge degrades to a textual conflict rather than a behavioural one. Same precedent as phase-05 Task 6, V1 Task 3 and V8 Task 1. |
| `ScoreUi.SetAuthoritativeState` is later "tidied up" back through `AddScore`, re-entering the win check | 3 | 4 | **12** | **D11/D12** state the reason in the decision table, the `ScoreUi.cs:46-57` remarks are updated in the same commit rather than left contradicting the code, and `TheHudNeverRoutesTicketsThroughAddScore` fails if it happens. |
| Building a tracer from scratch (D10) drifts into an open-ended VFX task | 3 | 3 | 9 | Scoped to one file with a fixed lifetime, no collider and no `Projectile`. The *look* is **E4** — an Editor item with an owner, not a code task without an end. |
| Local explosion prediction shows a phantom blast the server never confirms | 3 | 2 | 6 | Accepted in D13, bounded by `SuppressionWindowSeconds`. `ASuppressedPredictionExpiresAndDoesNotEatTheNextBlast` pins that the opposite failure cannot happen. |
| V1 also creates `NetClientExplosionPresenter.cs` because its Task 4 was never amended | 3 | 2 | 6 | D14 names the supersession; § 7 lists the amendment. Worst case is a conflict on a new file, which is loud. |

**Five risks reach 15 or higher.** The top two are the phase's defining conditions: it arms a latent
local-singleton hijack, and almost everything it produces is work CI structurally cannot grade. Both
are answered with mechanisms rather than intentions — a grep gate for the first, an enumerated Editor
checklist with pass conditions for the second.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — Lookup seam + local identity | S (0.5d) | No dependencies. **Start here** — everything needs `TryFind` and `IsLocalActor`. |
| 2 — `RemoteActorView` representation | **L (3d)** | Needs 1. **Critical path.** **Pair with Task 3** — it arms A16. |
| 3 — Local-only singleton gating (A16) | **M (2d)** | Needs 1. **Ships with Task 2, not after it.** Dev A files. |
| 4 — Reuse audit + the five missing models | M (1.5d) | Needs nothing. The audit half is the first hour and shrinks the rest. |
| 5 — Death + ragdoll | M (1.5d) | Needs 1, 2, 4. Final third gated on **E1**. |
| 6 — Shooting feedback + tracer + `Hit(int)` | **L (2.5d)** | Needs 1, 2, 4. Grew for D10's from-scratch tracer. Dev A files. |
| 7 — Match HUD + `ScoreUi` setter | M (1.5d) | Needs 4 only. **Independent of Task 2.** Dev A file. |
| 8 — Capture points | S (1d) | **Blocked on V8 Task 1. Severable, last.** Nothing above waits. |
| 9 — Minimap team fix + empty catch + NPE | S (1d) | Needs 1. **Independent of Task 2.** Dev A files. |
| 10 — Explosions + local prediction | M (1.5d) | Needs 4 only. **Independent of Task 2.** Supersedes V1 Task 4. |
| 11 — The `tools/` gate, four checks | M (1.5d) | **Independent of everything.** Write it early — it is red until 5-10 land, and that red is informative. |
| 12 — Tests | L (2.5d) | Written alongside 1-11, not after. |
| **Total** | **~20 days (~4 weeks)** | Critical path: **1 → 2 ‖ 3 → 5 → 6** ≈ 9.5 days of strictly serial work. Tasks 4, 7, 9, 10 and 11 run in parallel with 2/3 and are why the total is not the sum. Task 8 is outside the estimate and lands whenever V8 Task 1 does. |

> **The estimate grew from an initial ~10.5 days** as source verification landed. Task 2
> (representation, 3d), Task 3 (A16 gating, 2d) and Task 9 (minimap, 1d) were not in the original
> brief; Tasks 6, 7, 11 and 12 grew for the from-scratch tracer, the `ScoreUi` setter, the Roslyn gate
> and the larger matrix. Recorded so the change is visible rather than absorbed.

---

## 7. Handoff

### To Dev A — the Editor half, enumerated

Per design § 7, Dev A owns only work that genuinely **requires the Unity Editor**. Every code change
above is written here. Each item below is individually checkable with a stated pass condition. **E1 is
blocking; the rest are not.**

| # | Item | Pass condition |
|---|---|---|
| **E1** | **`_remoteActorPrefab` (`RemoteActorRegistry.cs:42`) carries an animator, a ragdoll rig, a muzzle anchor and a weapon mount.** **Blocking for Tasks 2, 5, 6.** | A remote actor can crouch, aim, ragdoll, and show a flash at the right height. If any part is absent, **say which** — Tasks 2 and 5 have degraded paths whose warnings name this row. |
| **E2** | `.meta` files for the six new `Net/Client/` scripts, the five new `Ironfront.Net.Replication/Client/` files, and `tools/ClientWiringGate/` | No missing-meta warnings on import. **Note:** `ServerActorDamageSink.cs`, `ServerCombatBridge.cs` and `ServerCombatEvents.cs` are still missing theirs from phase-05, and V1 § 7 already asked — one pass covers all three phases. |
| **E3** | Per-weapon muzzle-flash and report references exist on the weapon prefabs `PlayFireCosmetics` reads | Every weapon in `WeaponIds` flashes and reports, or is deliberately silent and does not throw. **No new presenter-side `AudioClip[]` is needed** — D7 reuses the authored per-weapon references. |
| **E4** | **`CosmeticTracerPool` visual.** New asset — no tracer exists in the project today (D10) | A streak that reads as a bullet, with **no collider, no `Projectile` component, no `source`**. The asset most likely to be assumed to exist; it does not. |
| **E5** | HUD wiring for `ScoreUi.SetAuthoritativeState`: ticket labels, phase label, phase timer | All five fields have somewhere to render, and **the timer is hidden during `Playing`** rather than showing `0:00`. |
| **E6** | `NetClientExplosionPresenter` wiring: `ParticleSystem[]` indexed by `ExplosionKind` | Indices 0 (`Grenade`) and 1 (`Rocket`) filled; 2 (`Vehicle`) and 3 (`Environment`) may be empty and must not throw — carried from V1 § 7 item 3 with the file. |
| **E7** | **Two-client test — combat.** A shoots B | B's client shows A's flash at the right height for A's stance, hears the report, sees the tracer. A's client shows a hitmarker; B's does not. Both show B's ragdoll along the shot direction, and one killfeed line each. |
| **E8** | **Two-client test — HUD.** Watch a full round | Tickets, phase and player count track the server on both clients. No timer during `Playing`; a timer during warmup and after the end. |
| **E9** | **Two-client test — capture point.** Both clients watch one point flip | Flag colour and capture bar change on both clients at the same authoritative value; **a team-1 player can select a spawn point**; neither client runs its own arithmetic. |
| **E10** | **Two-client test — grenade.** A throws one | A sees the blast immediately and **exactly once**. B sees it once. Neither sees it twice. |
| **E11** | **Two-client test — A16.** B enters a mounted turret and takes damage while A watches | **A's cameras do not change. A's health bar does not move. A's vignette does not fire.** The single most important observation in this phase, and the one a combat-scoped test would miss. |
| **E12** | **Scene ordering.** The three presenters sit on the client bootstrap object and resolve `NetClientBootstrap.Current` in `Awake` | No presenter logs its null-bootstrap warning on a normal client start; a headless build logs nothing from any presenter. **The one failure the gate cannot catch** — runtime ordering, not source shape. |
| — | Per design § 7: the Profiler run, and per-weapon `Configuration` values in `_Managers.prefab` | Unchanged. V2 owns the weapon table. |

**The Dev A review is one PR** carrying `Actor.cs`, `TankTurret.cs`, `MountedTurret.cs` (Task 3),
`Weapon.cs`, `IngameUi.cs` (Task 6), `ScoreUi.cs` (Task 7), `MinimapUi.cs`, `CapturePoint.cs`,
`DecalManager.cs` (Task 9), with the offline-unchanged tests attached. Every change is additive or a
deleted empty catch. One review round is assumed.

### To V0 — one amendment, already applied

**V0 D10 read "`Actor.cs` is opened exactly once, in this phase" and called its Task 8 "the only
`Actor.cs` change in the whole V-track plan".** Task 3 above re-opens the file, to gate
`Actor.cs:716-719` and `:824-829` on `IsLocalActor`. V10 was approved after V0 was written and is
absent from the design of record's § 6 table, so this is a **count that changed, not a decision that
was overturned** — D10's actual mitigation (open it early, announce it in the PR title before Dev A
opens the file) applies unchanged to this phase's Dev A PR, which is the second announcement.

**Amended in the same commit as this note**, in all four places V0 repeated the count: D10 itself,
Task 8's closing line, acceptance criterion 11, and the § 6 timeline row. Recorded here for the same
reason D14 is recorded: an amendment nobody writes down is exactly how § 1's six dead events came to
exist.

### To V1 — one amendment

**V1 Task 4 is superseded by V10 Task 10 (D14).** V1 should strike Task 4 and its
`NetClientExplosionPresenter.cs` row, keeping Tasks 1, 2, 3 and 5 unchanged; its § 7 item 3 moves to
**E6**. **V1 D6 is overridden by V10 D13** — using V1 D6's own recorded fallback clause, so no new
decision was made, only an earlier one taken. V1 Task 5's
`AnExplosionFramedByTheServerRoutesToTheClientHandler` still grades the router join and is
deliberately **not** duplicated here.

### To V8 — one dependency, one confirmation, one boundary

- V10 Task 8 is **blocked on V8 Task 1** (D15) and is the second caller V8 D3 anticipates.
- `ApplyAuthoritativeOwner` must keep the signature V8 D3 states — `(int team, float control, bool
  contested)` — because Task 8 calls it with `Math.Abs(OwnerQ)/100f` to match `CapturePointSlave.Apply`'s
  `Math.Abs(state.Owner)`. If that mapping changes on one side it changes on both, in one commit.
- **The minimap fix is V10's, not V8's.** V8 Task 1 explicitly *preserves* the
  `MinimapUi.UpdateSpawnPointButtons` call through `ApplyAuthoritativeOwner`
  (`phase-v8-objectives.md:87`), so the team-0 hardcode survives V8's refactor untouched. V10 Task 9
  fixes it. **Neither phase should assume the other did** — this row exists so that assumption is
  impossible.
- **V8 D9 stays open.** V10 Task 7 closes the *rendering* half only (D12); `ScoreUi` still holds match
  state that does not run headless, and that remains V8 D9's recorded divergence.

### To V5 and V6 — two non-overlaps

- **V5:** V10 **never reads or writes `aiControlled`** (D2). V5-D7 pins that the flag is *unchanged*
  for a networked driver; V10 stops *trusting* it for identity. Neither contradicts the other, and V10
  supplies `IsLocalActor` as the predicate V5's remote-driver work can use instead.
- **V6:** V10's cosmetic path never reads or advances `currentMuzzle` (D9). V6 replicates it for the
  **authoritative** path; that is a different consumer and the two do not share state.

### To V3 — the gaps this phase cannot close

Named here rather than discovered later, per V1 D5's rule — an unbuilt message that nobody writes down
is exactly how this phase's six dead events came to exist.

| Gap | Evidence | Owner |
|---|---|---|
| **Killfeed lines have no names.** `KillfeedEntry` carries actor **ids only**, and `ServerMessageType.PlayerList = 0x4B` (`MessageTypes.cs:52`) is declared with **no message struct and no router case**. | V10 ships the line's data and expiry; the names need a protocol addition. | **V3** |
| **No capture-point minimap marker.** `MinimapUi` has no marker API — its markers are `SpawnPoint` buttons built once in a private `SetupMinimap()`, and `AddActorBlip` (`:172`) is add-only and takes an `Actor` while the registry stores a `Transform`. | Build-new UI, not a wire-up. Out of V10's scope. | **V8 or a UI phase** |
| **No scorch `DecalType`.** The enum is `Impact` / `BloodBlue` / `BloodRed`; explosions reuse `Impact`. | A scorch mark is a new enum value plus a new material. | **V7** |
| **Per-bone ragdoll force.** `ApplyRigidbodyForce` is hardcoded to `MainRigidbody()` (D5). `DeathMessage.HitboxHit` **is** consumed — for the killfeed headshot icon (`CombatFeed.cs:165`) — but not for bone selection. | Nobody in the V-track today. | **unowned, recorded** |

### To V9

V10 is a precondition for V9's two-client Editor test being meaningful at all: before this phase a
second client renders no combat, no HUD and no objectives, so "the same vehicle in the same place"
(design criterion 1) would be the only observable thing. E7-E12 are the smaller versions of the same
test and should run first.

### Plan-document drift found while verifying (fix before citing)

| Document | Claim | Actual |
|---|---|---|
| `plans/00-shared/architecture.md:314` | calls `IngameUi.instance.ShowHitmarker()` | **`private`** at `IngameUi.cs:172` — will not compile. Use the static `Hit()`. |
| `phase-v7-projectiles.md:211` | `IngameUi.Hit()` at `:60` | `:65` |
| `phase-v8-objectives.md:91` | `CapturePoint.cs` lines 192-203 | 194 / 198 / 202 (its `ScoreUi.cs:98` and `:100-107` citations **are** accurate) |
| `docs/codebase-map.md` | eight `Actor.cs` line references | shifted |

### Observations recorded, not fixed

- `RemoteActorRegistry.cs:105` iterates `_live` with `foreach` inside `Update()`. A `Dictionary`
  struct enumerator does not allocate, so this is a § 3.2 style violation, not a live defect.
  Recorded rather than fixed, per `coding-guidelines.md` § 3.
- The premise this phase was commissioned under listed **seven** dead events and named
  `OnSnapshotApplied` among them. It is **six** — `OnSnapshotApplied` has been subscribed by
  `ClientPredictionStage.cs:76` since prediction landed. The gate's expected-pass set depends on it.
- All four client singletons assign `instance` **unconditionally** in `Awake()` with no
  `DisallowMultipleComponent` and no duplicate guard, so a second instance silently wins. Not V10's to
  fix; worth knowing when E12 fails.
- `NetContext` imports `UnityEngine` (`NetContext.cs:1`), so it can never be linked into an
  engine-free test project. Its own doc (`:26-31`) states that shared simulation must never consult
  the role: *"The role governs who drives the simulation, never what it computes."* Every V10 role
  branch is in a presenter, never in a model.

**Still outside Dev C:** nothing in this phase.
