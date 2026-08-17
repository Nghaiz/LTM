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
> defect found in V4-V7 would be indistinguishable from the six this phase closes.

> Design of record:
> [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md)
> § 2.4 and § 6. **This phase is not in that document's § 6 phase table.** It was approved on
> 2026-08-17 after the gap in § 1 was found by grep and verified; this file is the record of that
> addition.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files, `Span<byte>` over
> `byte[]`) and § 7 (ownership). Per design § 7, Dev C writes every file here including those under
> `Assembly-CSharp/`; Dev A owns only the Editor half, enumerated as **E1-E9** in § 7.
>
> **Depends on V0.** **No wire change.** Every byte this phase consumes is already defined, already
> implemented, already conformance-tested, and already being sent by a shipped server.

---

## 1. Objectives

`ClientMessageRouter` raises **nine** events. **Three** have a production subscriber. **Six** have
none — in any file, anywhere under `Ironfront_Reborn/Assets/Scripts/`.

| Event | Declared | Production subscriber |
|---|---|---|
| `OnSpawnActor` | `ClientMessageRouter.cs:66` | `RemoteActorRegistry.cs:77` |
| `OnDespawnActor` | `:69` | `RemoteActorRegistry.cs:78` |
| `OnSnapshotApplied` | `:114` | `ClientPredictionStage.cs:76` |
| **`OnHitConfirm`** | **`:79`** | **none** |
| **`OnDeath`** | **`:89`** | **none** |
| **`OnWeaponFire`** | **`:98`** | **none** |
| **`OnMatchState`** | **`:101`** | **none** |
| **`OnCapturePoint`** | **`:104`** | **none** |
| **`OnExplosion`** | **`:107`** | **none** |

Reproduce — scope is every `*.cs` under `Ironfront_Reborn/Assets/Scripts/`:

```bash
for e in OnDeath OnWeaponFire OnHitConfirm OnMatchState OnCapturePoint OnExplosion \
         OnSnapshotApplied OnSpawnActor OnDespawnActor; do
  printf '%-20s %s\n' "$e" "$(grep -rn "$e" --include=*.cs Ironfront_Reborn/Assets/Scripts/ | wc -l)"
done
```

`CapturePointMessage`, `MatchStateMessage`, `DeathMessage`, `WeaponFireMessage` and
`HitConfirmMessage` appear only in server-side files — `ServerCombatBridge.cs`,
`ServerCombatEvents.cs`, `ServerTickLoop.cs`. `Ironfront.Net.Replication/Client/CombatFeed.cs`
exists, is complete, and has zero Unity consumers.

**So the server half of phase-05 (combat) and phase-03 (capture points) shipped, and the client half
was never built.** In a networked match today a remote player fires silently and invisibly, takes
hits and never dies, and the capture points that decide where everyone respawns render nothing at
all. This is [`wired-not-just-present.md`](../../../.claude/rules/wired-not-just-present.md) at
six-event scale: present, conformance-tested, and never run.

Two comments in shipped code are promises this phase keeps:

- `ServerActorDamageSink.cs:69-73` — *"`Actor.Die()` is deliberately NOT called … The death
  choreography is per-client anyway — corpses are never replicated (AD-4), so each client runs its
  own ragdoll off `S_DEATH`."* No client runs anything off `S_DEATH` today.
- `ServerEventWriter.WeaponFireAudibleRadius` — earshot filtering, implemented in phase-05 for an
  audience that does not exist.

By the end of this phase:

1. A remote player's shot produces a muzzle flash, a report and a tracer on every client in earshot.
2. A death produces a ragdoll driven by the **replicated** force vector, not a locally invented one.
3. A hit produces a hitmarker on the shooter's screen and on nobody else's.
4. Score, tickets, phase and the phase timer render from the server's authoritative numbers.
5. Capture points render — flag colour, capture progress, minimap — from the one authority V8
   establishes, never from the scene component's own arithmetic.
6. An explosion is seen by everyone, and **your own is seen immediately** rather than one RTT later.
7. A **regression gate** fails the build when any `ClientMessageRouter` event loses its last
   production subscriber, so this class of gap cannot recur silently.

**Not in this phase.** No protocol change — not one byte moves that is not already specified and
conformance-tested. No server-side emit work: V1 owns the explosion emitter, and phase-05 already
shipped the other five. No vehicles, seats or projectiles. No `ScoreUi` scoring redesign — V8 D9
recorded that as a deliberate divergence and this phase respects the boundary rather than reopening
it.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **D1** | **Three presenter components — not six, not one.** `NetClientCombatPresenter` (death, weapon fire, hit confirm), `NetClientObjectivePresenter` (match state, capture points), `NetClientExplosionPresenter` (explosions). Each subscribes in `OnEnable` and unsubscribes in `OnDisable`, byte-for-byte the lifecycle `RemoteActorRegistry.cs:77-85` and `ClientPredictionStage.cs:76-82` already use — this phase adds no new pattern to learn. Six components would be six `.meta` files and six prefab wirings for Dev A; one component would carry every serialized `ParticleSystem`, `AudioClip` and UI reference in the build on a single inspector. Three is the grouping where each component's serialized references are one coherent set. |
| **D2** | **Death arrives from `S_DEATH` and is never inferred from health reaching zero.** phase-05 D5 suppresses the health mutation and `Die()` at the client role while letting `ReceivedDamage`, blood decals, ragdoll force and knockback run — so a client-role actor today takes hits, bleeds, and **never dies**. This phase closes that half-open state. `Actor.Die()` is `private` and reaches for `IngameUi` and `ScoreUi` (`ServerActorDamageSink.cs:69-70`), so the presenter drives the corpse through the seam in Task 1 rather than calling it. |
| **D3** | **Your own explosion is predicted locally and the matching `S_EXPLOSION` is suppressed by `SourceActorId`.** This **overrides V1 D6**, which chose server-sourced-for-everyone. It is not a contradiction of V1: **V1 D6 records this exact mechanism as its own named fallback** — *"play locally and suppress the matching `S_EXPLOSION` by `SourceActorId` — one branch in the presenter, recorded here so it is not re-derived."* The consumer took that branch on 2026-08-17, before V9 rather than after. **Accepted cost:** a predicted explosion the server never confirms (a grenade destroyed in flight) shows a phantom blast with no damage. Bounded by the suppression window in Task 7. |
| **D4** | **V10 owns `NetClientExplosionPresenter.cs` outright; V1 Task 4 is superseded.** Two phases cannot create one file, and V10's version carries D3's prediction branch that V1's does not. V1 keeps its Tasks 1, 2, 3 and 5 — encoding, emit, `ActorManager.Explode` authority, tests — all untouched. **This requires a one-line amendment to V1 Task 4**, listed in § 7 rather than silently assumed. |
| **D5** | **Capture-point consumption writes through V8's `ApplyAuthoritativeOwner`, so Task 6 is blocked on V8 Task 1.** V8 D3 makes that method the single write path for `owner`, `control`, `pendingOwner` and `isContested`, and already names *"the client's capture-point message handler"* as one of its two callers — this phase is that caller. Landing Task 6 before V8 would put a second writer on the client while `UpdateOwner`'s 1 Hz arithmetic is still running there (V8 D2 is what stops it), reproducing design § 2.1's duplicate-authority bug one process further out. Task 6 is severable and last, the same shape as V8's own Task 6. |
| **D6** | **The hitmarker is shooter-only and stays that way.** `S_HIT_CONFIRM` is already sent to the shooter alone (phase-05 Task 3). Rendering it for anyone else would tell a player that someone, somewhere, hit something — a server-served wallhack. Recorded because "why does only one client get this event" is exactly the question a future reader answers by broadcasting it. |
| **D7** | **The HUD consumes the server's authoritative numbers and revives none of `ScoreUi`'s own.** `ScoreUi.cs:46-57` documents itself as holding match state in a UI component and notes that the original game neither scores nor ends headless. V8 D9 recorded that as a deliberate divergence and declined to fix it. V10 renders `MatchStateMessage`'s `Phase`, `Tickets0`, `Tickets1`, `PhaseSecondsRemaining` and `HumanPlayerCount`, and touches neither `ScoreMultiplier`, nor `victoryPoints`, nor `AddFlag`'s arithmetic. Respecting a recorded boundary is cheaper than re-deciding it. |
| **D8** | **`OnSnapshotApplied` already has a production subscriber and is out of scope.** `ClientPredictionStage.cs:76` subscribes it, `:82` unsubscribes it. This **corrects the premise this phase was commissioned under**, which listed it among the dead events. It matters twice: the phase does not build a subscriber that exists, and the regression gate's expected-pass set is three events rather than two — a gate that is red for everything it checks proves nothing ([`green-that-proves-nothing.md`](../../../.claude/rules/green-that-proves-nothing.md)). |
| **D9** | **The regression gate enumerates by reflection and detects by source scan.** The event list comes from `typeof(ClientMessageRouter).GetEvents()` — that type is engine-free, so the test project loads it directly and the enumeration side cannot drift when an event is renamed. The subscriber side is a text scan of `Assets/Scripts/`, because the test project is engine-free and cannot load the Unity assembly to reflect over it. **A registration manifest was rejected:** a test asserting every event has a manifest entry proves the manifest is complete, not that anything is wired — precisely the failure `green-that-proves-nothing.md` describes. |
| **D10** | **`CombatFeed.cs` is reused verbatim for hitmarkers and the killfeed. No parallel implementation is written.** `HitmarkerModel` and `KillfeedModel` (phase-02 task 6) already consume `HitConfirmMessage` and `DeathMessage` directly, are allocation-free, and carry severity and expiry. They need **no extension** for their half. What V10 adds is only what is genuinely absent — see Task 2's reuse audit. |
| **D11** | **Every presenter is inert unless `NetContext.IsClient`, and every singleton dereference is null-guarded.** `IngameUi.instance`, `ScoreUi.instance`, `MinimapUi.instance` and `DecalManager` do not exist in a stripped headless build. The role check is the guard; the null check is the backstop for a client that loads a scene before its UI — the same defence-in-depth V8 Task 1 applies to `flagRenderer`. |
| **D12** | **The remote actor is a pooled `Transform`, not an `Actor`.** `RemoteActorRegistry._live` is a `private Dictionary<ushort, Transform>` (`:49-50`) whose values are instances of the serialized `_remoteActorPrefab` (`:42`). Whether that prefab carries a ragdoll rig, a muzzle socket or a decal receiver is **authored in the Editor and cannot be read from source** — so it is a named Dev A precondition (**E1**), not an assumption. Task 3 degrades explicitly rather than silently if it is unmet. |

---

## 3. Detailed tasks

### Task 1 — The lookup seam, and the guard every presenter shares (0.5 day)

Nothing can be presented until `ushort actorId` resolves to something in the scene, and **there is no
public lookup today**. `RemoteActorRegistry`'s entire public surface is `LiveCount` (`:55`) and
`PooledCount` (`:58`); `_live` is private.

| File | Change |
|---|---|
| `Net/Client/RemoteActorRegistry.cs` | **Edit**, Dev C. Add `public bool TryGetActorTransform(ushort actorId, out Transform t)` — a one-line `_live.TryGetValue` pass-through. Read-only, no allocation, and it does not expose the dictionary, so the pooling invariant stays owned here. |
| `Net/Client/NetClientPresenterGuard.cs` | **New**, Dev C. `public static bool IsPresentable` — `NetContext.IsClient && !NetContext.IsOffline`. One static property so D11's role guard is written once rather than three times slightly differently, and so the regression gate has one predicate to point a reviewer at. |

**Why a `Transform` and not an `Actor`.** D12 — the registry stores `Transform` and never touches
`Actor`. Widening the return type would force the registry to `GetComponent<Actor>()` on a path that
runs per despawn, and would make the return null for any prefab that is a visual proxy. The presenter
resolves the components it needs from the transform, guarded, and says so when they are absent.

**Constraint.** No allocation. `TryGetActorTransform` is a dictionary probe and nothing else.

**Verify:** engine-free — none; this is a two-method Unity edit. Graded by Task 9's
`ARemoteActorResolvesFromItsNetworkId` compiling against the new signature, and by `dotnet build
Ironfront.sln` clean.

> **Observation, not a task.** `RemoteActorRegistry.cs:105` iterates `_live` with `foreach` inside
> `Update()`. A `Dictionary<K,V>` struct enumerator does not allocate, so this is a § 3.2 style
> violation rather than a live defect. It is recorded in § 7 rather than fixed, because
> `coding-guidelines.md` § 3 forbids improving adjacent code that is not broken.

---

### Task 2 — The reuse audit, and the three models that are genuinely missing (1 day)

`search-before-you-build.md` first. `CombatFeed.cs` already covers two of the six events **completely**:

| Existing type | Covers | Verdict |
|---|---|---|
| `HitmarkerModel` (`CombatFeed.cs:88-128`) | `S_HIT_CONFIRM` | **Fit as shipped.** `Push(in HitConfirmMessage, uint atTick, float nowSeconds)`, `IsVisible(float)`, `Current`, `HitCount`, `Reset()`. Allocation-free, severity-aware, newest-wins semantics already argued in its own remarks. **No change.** |
| `KillfeedModel` (`:185-270`) | `S_DEATH`, killfeed half | **Fit as shipped.** Fixed ring, `Push(in DeathMessage, float)`, `Prune(float)` compacting rather than truncating, indexer newest-first. **No change.** |
| `HitmarkerSeverity`, `HitmarkerEvent`, `KillfeedEntry` | both | **Fit as shipped.** `HitmarkerEvent.From` and `KillfeedEntry.From` already decode the wire structs. **No change.** |

So the hitmarker and the killfeed cost **zero new engine-free code**. What `CombatFeed` does *not*
model is the ragdoll impulse, the shot, the match state, the capture point, and the explosion
suppression window. Those are new, and they go beside it in the same folder:

| File (all new), `Ironfront.Net.Replication/Client/` | Contents |
|---|---|
| `DeathImpulse.cs` | `readonly struct DeathImpulse` — `VictimActorId`, `KillerActorId`, `CauseOfDeath Cause`, `Vec3 Force`, `HitboxType Hitbox`, `bool KilledByEnvironment`. `static DeathImpulse From(in DeathMessage)` unpacks the `i16 × 3` force through the same `Quantize` helper the snapshot uses, so the force the ragdoll receives and the force the server computed cannot drift by being unpacked two ways. `KillerActorId == 0xFFFF` sets `KilledByEnvironment` — the constant already exists as `DeathMessage.EnvironmentKiller` and is **not** re-declared here. |
| `ShotEvent.cs` | `readonly struct ShotEvent` — `ShooterActorId`, `WeaponId`, `Vec3 Direction` (unpacked `i16 × 3`). `static ShotEvent From(in WeaponFireMessage)`. No state: a shot is instantaneous and the presenter fires cosmetics on receipt. |
| `MatchStateModel.cs` | Holds the last `MatchStateMessage` plus a client-side countdown. `Apply(in MatchStateMessage, float nowSeconds)` and `float SecondsRemaining(float nowSeconds)`, which **interpolates between messages** — `PhaseSecondsRemaining` arrives at the match broadcast rate, not per frame, and a timer that only moves when a packet lands reads as a stutter. `IsStale(float nowSeconds)` so the HUD can dim rather than lie when the stream stops. |
| `CapturePointView.cs` | `Apply(in CapturePointMessage)` into a fixed array indexed by `PointId`, exposing `OwnerQ` (`i8`, −100…+100), `OwningTeam`, `IsContested` and a `bool DirtySinceLastRead(int pointId)` edge so the presenter repaints on change rather than every frame. Array sized to `ProtocolConstants` — no dictionary, no allocation. |
| `ExplosionSuppressor.cs` | D3's mechanism. `PredictLocal(ushort sourceActorId, float nowSeconds)` records a prediction; `bool ShouldSuppress(in ExplosionMessage, float nowSeconds)` returns true when a live prediction matches `SourceActorId`. Backed by a small fixed ring with a `SuppressionWindowSeconds` default of `1.0f` — long enough to cover a bad-RTT confirmation, short enough that a second grenade from the same actor is not eaten. Entries expire; nothing accumulates. |

**Constraints.** Every type here is engine-free, allocation-free, and uses `Vec3` rather than
`UnityEngine.Vector3`. No `System.Linq`, no `foreach`. `CapturePointView` and `ExplosionSuppressor`
are backed by arrays indexed by id, on the `ServerRespawnGate` precedent from phase-05 Task 1.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~ClientEvent` —
red until Task 9's tests exist and these types back them. `CombatFeed.cs` shows **zero diff**; a
reviewer checking that file is the cheapest confirmation D10 held.

---

### Task 3 — Death, and the ragdoll the server already promised (1.5 days)

**Files:** new `Net/Client/NetClientCombatPresenter.cs` (Dev C).

Subscribes `OnDeath` in `OnEnable`, unsubscribes in `OnDisable`, guarded by
`NetClientPresenterGuard.IsPresentable`. On each `DeathMessage`:

1. `DeathImpulse.From(message)` — one unpack, per Task 2.
2. `KillfeedModel.Push(in message, Time.time)` — the shipped model, per D10. The killfeed line is
   drawn from `KillfeedModel`'s indexer, pruned once a frame.
3. `RemoteActorRegistry.TryGetActorTransform(impulse.VictimActorId, out Transform t)` — Task 1's
   seam. A miss is a normal outcome (the victim died outside interest range) and draws the killfeed
   line without a corpse. **It is not an error and must not log as one.**
4. Drive the corpse: disable the animator, enable the ragdoll rig, and apply `impulse.Force` at the
   hitbox indicated by `impulse.Hitbox`.

**The honest limit on step 4.** Whether `_remoteActorPrefab` carries a ragdoll rig is authored in the
Editor and cannot be read from source (D12). This task therefore ships **behind a seam and degrades
explicitly**:

| Prefab state | Behaviour |
|---|---|
| Ragdoll rig present | Full choreography: animator off, rig on, `impulse.Force` applied at the hit limb. |
| Rig absent | The presenter logs **once per session, at warning**, naming **E1**, and falls back to hiding the transform and playing the death effect. A silent no-op here would be indistinguishable from the bug this phase exists to close. |

The local player is excluded — its death runs through the existing local path, exactly as
`RemoteActorRegistry` excludes it from interpolation (`:25-28`).

**Constraint.** No allocation per death. The component cache is a field resolved on spawn, not a
`GetComponent` per message.

**Verify:** engine-free — Task 9's `ADeathMessageProducesOneKillfeedLineAndOneImpulse` and
`AnEnvironmentKillerResolvesToTheEnvironmentFlag` grade the decode and the model, driven through a
fake registry. The ragdoll itself is **E7**. `dotnet build Ironfront.sln` clean.

---

### Task 4 — Shooting feedback: muzzle flash, report, tracer, hitmarker (1.5 days)

**Files:** `Net/Client/NetClientCombatPresenter.cs` (Dev C, continued);
`Assembly-CSharp/IngameUi.cs` (Dev A file — PR plus one review round, per design § 7).

**`OnWeaponFire` → the shot.** `ShotEvent.From(message)`, resolve the shooter through Task 1's seam,
then play a muzzle flash at the shooter's muzzle socket, a report `AudioClip` selected by `WeaponId`,
and a tracer along `Direction`. Earshot filtering is **already done server-side** by
`ServerEventWriter.WeaponFireAudibleRadius` (phase-05), so the presenter plays every message it
receives and adds no distance test of its own — a second filter would be a second thing to keep in
agreement with the first.

An unknown `WeaponId` draws the default flash and plays no report, rather than throwing — the same
forward-compatibility rule V1 Task 4 states, and the same one `WeaponIds.NameOf` already follows by
returning empty.

**`OnHitConfirm` → the hitmarker.** `HitmarkerModel.Push(in message, tick, Time.time)` — the shipped
model, unchanged (D10). Drawn while `IsVisible(Time.time)`. Shooter-only by D6; the presenter adds no
broadcast path, and there is nothing to filter because the server already sent it to one client.

**The one gap that needs a Dev A file.** `IngameUi.Hit()` is `public static void Hit()` with **no
parameters** (`IngameUi.cs:65`), so it cannot express the severity `HitmarkerModel` was built to
carry — `Normal`, `Headshot`, `Kill`, each with its own colour and pitch (`CombatFeed.cs:12-22`).
Rendering all three identically would discard shipped phase-02 work.

```csharp
// IngameUi.cs — minimum change, every existing caller preserved
public static void Hit() => Hit(0);
public static void Hit(int severity) { /* 0 normal, 1 headshot, 2 kill */ }
```

`int` rather than `HitmarkerSeverity` deliberately: `Assembly-CSharp` does not take a dependency on
`Ironfront.Net.Replication` for a cosmetic enum, and the presenter casts at the one call site. The
no-arg overload keeps every existing caller compiling untouched, so the blast radius is one method.

**Constraint.** No allocation per shot. The `AudioClip[]` is serialized and indexed; tracers come
from a pre-warmed pool, not `Instantiate`.

**Verify:** engine-free — `AWeaponFireMessageDecodesToAShotEvent`,
`AnUnknownWeaponIdDoesNotThrow`, `AHitConfirmRaisesTheMarkerAndTheNewestHitWins`. The `IngameUi`
overload is graded by `dotnet build Ironfront.sln` clean plus the existing no-arg callers still
compiling. Cosmetics are **E3** and **E7**.

---

### Task 5 — The match HUD (1 day)

**Files:** new `Net/Client/NetClientObjectivePresenter.cs` (Dev C).

Subscribes `OnMatchState`. `MatchStateModel.Apply(in message, Time.time)`, then renders `Phase`,
`Tickets0`, `Tickets1`, `PhaseSecondsRemaining` and `HumanPlayerCount` into the HUD.

Per **D7** this reads the server's numbers and revives none of `ScoreUi`'s own arithmetic. `ScoreUi`
is written to, never read from, and `ScoreMultiplier`, `victoryPoints` and `AddFlag` are untouched —
V8 D9 recorded that boundary and this phase honours it.

The timer interpolates between messages via `MatchStateModel.SecondsRemaining(Time.time)` rather than
freezing between broadcasts, and dims on `IsStale` rather than displaying a stale number as a live
one — `development-principles.md` § "Errors Over Silent Fallbacks" applied to a clock.

**Constraint.** No per-frame string allocation. The HUD strings rebuild only when the underlying
value changes — the same fix phase-05 Task 7 M8 applied to the lobby overlay, for the same reason.

**Verify:** engine-free — `AMatchStateMessageAppliesEveryField`,
`ThePhaseTimerInterpolatesBetweenBroadcasts`, `AStaleMatchStateIsReportedStaleNotZero`. HUD layout is
**E5** and **E7**.

---

### Task 6 — Capture points (1 day) — **blocked on V8 Task 1, severable, last**

**Files:** `Net/Client/NetClientObjectivePresenter.cs` (Dev C, continued).

**Hard precondition (D5): V8 Task 1 is on `develop`.** Two reasons, and both are correctness rather
than convenience:

1. `ApplyAuthoritativeOwner(int team, float control, bool contested)` does not exist until V8 Task 1
   lands. It is V8 D3's single write path for `owner`, `control`, `pendingOwner` and `isContested`,
   and V8 D3 already names this handler as one of its two callers.
2. Until V8 D2 lands, `CapturePoint.UpdateOwner` is **still running its own 1 Hz arithmetic on the
   client**. Writing replicated ownership in beside it would produce two client-side writers — design
   § 2.1's bug, one process further out, and harder to see because both writers would be ours.

On each `CapturePointMessage`: `CapturePointView.Apply(in message)`, then for each point that came
back dirty, call `component.ApplyAuthoritativeOwner(team, control, contested)` where `control` is
`Math.Abs(OwnerQ) / 100f` — the same `Abs` mapping `CapturePointSlave.Apply` uses on the server
(V8 Task 3), so the flag-pole height means the same thing on both sides.

**What comes free, and must not be re-implemented.** `ApplyAuthoritativeOwner` calls the existing
`SetOwner(team)` once per flip (V8 Task 1), and `SetOwner` already drives `MinimapUi.UpdateSpawnPointButtons`
and the flag renderer. **So the minimap and the flag colour require no code in this phase** — they
require the write to go through the one path. The capture bar is the presenter's, read from
`CapturePointView.OwnerQ`.

`OwnerQ` is `i8` in −100…+100 and `OwningTeam` is a `byte`; neutral maps to `-1` explicitly rather
than by cast, for the reason V8 Task 3 gives — a neutral point written as team `0` hands every
neutral flag on the map to blue.

**Verify:** engine-free — `ACapturePointMessageAppliesToTheView`,
`AContestedPointReportsContested`, `ANeutralPointDoesNotResolveToTeamZero`,
`TheViewMarksOnlyChangedPointsDirty`. The write-through is graded against a fake component
implementing V8's method, so no Editor is needed for the contract. Rendering is **E8**.

---

### Task 7 — Explosions, with local prediction for your own (1.5 days)

**Files:** new `Net/Client/NetClientExplosionPresenter.cs` (Dev C). **Supersedes V1 Task 4 (D4).**

Subscribes `OnExplosion`. On each message:

```
ExplosionSuppressor.ShouldSuppress(message, Time.time)
  ├─ true  → drop it. This is the confirmation of an explosion already drawn locally.
  └─ false → unpack centre (Quantize.UnpackPos) + radius (V1's ExplosionEncoding.UnpackRadiusMetres),
             index the serialized ParticleSystem[] by (byte)Kind, scale effect + camera shake
             by radius, apply corpse ragdoll impulse locally.
```

**The prediction half (D3).** When this client's own explosive detonates, the local code path calls
`ExplosionSuppressor.PredictLocal(localActorId, Time.time)` and plays the effect **immediately**;
the server's confirming `S_EXPLOSION` then matches on `SourceActorId` and is dropped. The player sees
their own grenade at once instead of one RTT later.

**Why a window and not a counter.** The suppressor holds each prediction for
`SuppressionWindowSeconds` (default `1.0f`) rather than waiting for a matching confirmation
indefinitely. A grenade destroyed in flight never produces a confirmation, and an unbounded entry
would eat the *next* real explosion from the same actor — turning a cosmetic latency win into a
missing blast. Expiry bounds the damage to D3's accepted cost: one phantom flash, never a swallowed
one.

**This applies no health damage.** Health arrives in the snapshot, exactly as phase-05 D5 established
for bullets and V1 Task 4 established for blasts. The presenter is cosmetic and corpse-local only.

Every array index and serialized reference is guarded: an `ExplosionKind` this build does not know
draws nothing rather than throwing — V1 Task 4's rule, carried over unchanged with the file.

**Constraint.** No allocation per explosion. `ExplosionSuppressor` is a fixed ring; effects come from
the serialized array.

**Verify:** engine-free — `AnOwnExplosionIsSuppressedOnce`,
`ASuppressedPredictionExpiresAndDoesNotEatTheNextBlast`, `AForeignExplosionIsNeverSuppressed`,
`AnUnknownExplosionKindDoesNotThrow`. V1's `AnExplosionFramedByTheServerRoutesToTheClientHandler`
grades the router join and is **not** duplicated here. Cosmetics are **E4** and **E9**.

---

### Task 8 — The regression gate (1 day)

The point of the phase. Without it, the seventh dead event is a matter of time.

**Files:** new `Ironfront.Net.Replication.Tests/ClientEventSubscriptionGateTests.cs`.

Per **D9**, two halves with different mechanisms because they have different reliability needs:

| Half | Mechanism | Why |
|---|---|---|
| **Enumerate** the events | `typeof(ClientMessageRouter).GetEvents(BindingFlags.Public \| BindingFlags.Instance)` | `ClientMessageRouter` is engine-free, so the test assembly loads it directly. A renamed event changes the gate's input automatically and cannot be missed. |
| **Detect** subscribers | Text scan for `<EventName> +=` across `*.cs` under `Ironfront_Reborn/Assets/Scripts/` | The test project is engine-free and cannot load the Unity assembly to reflect over it. This is the honest ceiling, and it is stated rather than hidden. |

**Exclusions, each for a stated reason:** `ClientMessageRouter.cs` itself (declaration and `Invoke`
sites are not subscriptions), anything under `obj/` or `bin/`, and any `*Tests.cs` (a test
subscribing an event does not make the game render it — counting them is exactly how a gate goes
green over a dead feature).

**Repo-root resolution fails loudly.** The test walks up from `AppContext.BaseDirectory` looking for
`Ironfront.sln`. If it is not found the test **fails** with a message naming what it searched for. It
does **not** skip: a gate that quietly passes when it cannot find the source tree is the
`green-that-proves-nothing.md` failure in its purest form, and would have reported healthy for the
entire life of this bug.

**Proving the gate can fail.** A check never seen failing is unproven, so the detector is a pure
function over a string and is tested directly against fixtures:

| Test | Asserts |
|---|---|
| `TheGateFindsASubscriptionInAFixture` | a fixture containing `Router.OnDeath += Handler;` reports subscribed |
| `TheGateReportsAnUnsubscribedEventInAFixture` | a fixture with only a declaration reports **unsubscribed** — the red path, executed on every CI run |
| `TheGateIgnoresATestFileSubscription` | a fixture path ending `Tests.cs` does not count |
| `TheGateIgnoresTheRouterDeclarationItself` | `public event Action<DeathMessage>? OnDeath;` is not a subscription |
| `EveryRouterEventHasAProductionSubscriber` | **the gate itself**, over the real tree |
| `TheGateFailsWhenTheRepoRootCannotBeFound` | the loud-failure path, with a synthetic base directory |

**The expected-pass set is nine, not six.** Three events pass today (D8) and six do not, so before
this phase's presenters land `EveryRouterEventHasAProductionSubscriber` fails naming exactly the six
— which is the correct starting state and the proof the gate discriminates rather than blanket-fails.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~ClientEventSubscriptionGate`
— five green and `EveryRouterEventHasAProductionSubscriber` red before Tasks 3-7, all six green
after. Delete one `+=` line locally and watch it go red before merging.

---

### Task 9 — Tests (1.5 days, written alongside Tasks 1-8)

All engine-free, all in CI, no Editor. New file
`Ironfront.Net.Replication.Tests/ClientEventConsumptionTests.cs`, alongside Task 8's gate file.

| Test | Asserts |
|---|---|
| `ADeathMessageProducesOneKillfeedLineAndOneImpulse` | Task 3's decode; one line per death, not one per subscriber |
| `AnEnvironmentKillerResolvesToTheEnvironmentFlag` | `KillerActorId == 0xFFFF` → `KilledByEnvironment`, not actor 65535 |
| `TheDeathForceUnpacksToTheServerVector` | `i16 × 3` round-trips through the same `Quantize` path the server packed with — the drift D-decode exists to prevent |
| `ARemoteActorResolvesFromItsNetworkId` | Task 1's seam, and that a miss returns false rather than throwing |
| `AWeaponFireMessageDecodesToAShotEvent` | shooter, weapon, direction |
| `AnUnknownWeaponIdDoesNotThrow` | forward compatibility, Task 4 |
| `AHitConfirmRaisesTheMarkerAndTheNewestHitWins` | `HitmarkerModel`'s shipped semantics still hold through the presenter's call shape |
| `AKillHitmarkerOutranksAHeadshot` | `SeverityOf`, so Task 4's `IngameUi.Hit(int)` gets the right number |
| `AMatchStateMessageAppliesEveryField` | all five fields, Task 5 |
| `ThePhaseTimerInterpolatesBetweenBroadcasts` | the timer moves between messages |
| `AStaleMatchStateIsReportedStaleNotZero` | unknown is not good — `green-that-proves-nothing.md` |
| `ACapturePointMessageAppliesToTheView` | Task 6 |
| `AContestedPointReportsContested` | the `CaptureFlags` bit |
| `ANeutralPointDoesNotResolveToTeamZero` | V8 Task 3's neutral mapping, asserted on the client side too |
| `TheViewMarksOnlyChangedPointsDirty` | repaint-on-change, not per frame |
| `AnOwnExplosionIsSuppressedOnce` | D3 — the confirmation is dropped, and only once |
| `ASuppressedPredictionExpiresAndDoesNotEatTheNextBlast` | the window bound, Task 7 |
| `AForeignExplosionIsNeverSuppressed` | suppression keys on `SourceActorId` and nothing else |
| `AnUnknownExplosionKindDoesNotThrow` | carried over from V1 Task 4 with the file |
| `NoClientModelAllocatesOverAThousandEvents` | § 3.2, across all five new models at once |

---

## 4. Acceptance criteria

1. Every one of the nine `ClientMessageRouter` events has at least one production subscriber outside
   the test projects, and `EveryRouterEventHasAProductionSubscriber` is green.
2. The gate is **proven able to fail**: `TheGateReportsAnUnsubscribedEventInAFixture` and
   `TheGateFailsWhenTheRepoRootCannotBeFound` are green, exercising the red paths on every CI run.
3. A remote player's shot produces a muzzle flash, a report and a tracer on a second client, and no
   client applies a distance filter of its own on top of the server's earshot filter.
4. A death drives the corpse from the **replicated** force vector, or — when the prefab carries no
   rig — logs once naming **E1** and degrades visibly. It never silently does nothing.
5. A client-role actor that reaches zero health dies. Today it does not (D2), and that is the
   half-open state phase-05 D5 left behind.
6. A hitmarker appears on the shooter's client and on no other client, and a kill marker outranks a
   headshot marker.
7. Score, tickets, phase and the phase timer render from `MatchStateMessage`; the timer moves between
   broadcasts and reports staleness rather than displaying a stale value as live.
8. Capture points render flag colour, capture progress and minimap state from `CapturePointState` via
   `ApplyAuthoritativeOwner`, and **no client-side code writes `owner`, `control`, `pendingOwner` or
   `isContested` by any other path** — confirmed by grep in review, the same check V8 criterion 3
   makes on the server.
9. Your own explosion renders immediately and exactly once; the server's confirmation for it is
   suppressed; a foreign explosion is never suppressed; a prediction that is never confirmed expires
   without eating the next blast.
10. `CombatFeed.cs` has **zero diff** in this phase. `HitmarkerModel` and `KillfeedModel` are reused,
    not re-implemented (D10).
11. `IngameUi.Hit()` still compiles for every existing caller, and the severity overload is additive.
12. Offline single-player behaviour is unchanged: every presenter is inert at `NetRole.Offline`
    (D11), and no presenter exists in a server build.
13. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-event allocation
    in any new logic file.
14. `PROTOCOL_VERSION` is unchanged and `tools/SpecChecker` passes untouched. This phase consumes no
    byte that was not already specified.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| **The phase reports green in CI while nothing actually renders.** Every deliverable here is cosmetic, and CI has no Editor — so the seam contracts pass and the screen stays black | **4** | **4** | **16** | The structural answer, not a promise to be careful. (a) CI grades the *contracts* — decode, model semantics, write-path, suppression — and the phase says plainly that it grades nothing visual. (b) **E1-E9 are enumerated in § 7 as named, individually checkable Editor items**, not a vague "Dev A verifies". (c) **E7-E9 are two-client tests with stated pass conditions**, so "it works" is a reproducible observation. (d) Criterion 4 forbids the silent-no-op failure mode specifically. |
| **`_remoteActorPrefab` carries no ragdoll rig**, so `S_DEATH` arrives and the corpse does nothing (D12 — unreadable from source) | **4** | **4** | **16** | E1 is a **blocking** Editor precondition, named in Task 3 and first in § 7. Task 3 ships the degraded path deliberately: log once at warning naming E1, hide and play the death effect. The engine-free tests grade the impulse decode and the killfeed, which hold either way, so the task is not blocked on the prefab — only its final third is. |
| **Task 6 lands before V8 Task 1**, putting a second ownership writer on the client while `UpdateOwner` still runs there | 3 | **5** | **15** | D5 states it as a hard precondition, checked in the PR description, and Task 6 is **severable and last** so nothing above it waits. Criterion 8's grep is the same check V8 criterion 3 makes on the server side, so a second writer fails review on both sides of the wire. |
| The regression gate's text scan produces a false green — a `+=` inside a comment, a `#if` block, or dead code | 3 | 4 | **12** | Two-sided defence. The fixture tests pin the detector's behaviour on both the positive and negative path, and the exclusion list is explicit rather than incidental. Residual risk is a *false green on a commented-out subscription*, which is recorded here as the gate's known ceiling — a Roslyn analyzer would close it and is not worth its cost against six events. |
| The gate's repo-root walk fails on a CI layout that differs from a developer checkout, and the gate quietly stops running | 3 | 4 | **12** | It **fails loudly** rather than skipping (Task 8), and `TheGateFailsWhenTheRepoRootCannotBeFound` executes that path on every run. This is the one mitigation in the table that is itself a test rather than a promise. |
| `IngameUi.cs` conflicts with Dev A's branch | 3 | 3 | 9 | One method, one additive overload, one review round, announced in the PR. Rides the same precedent as phase-05 Task 6 and V1 Task 3. The no-arg form is preserved, so a conflicting merge cannot break an existing caller. |
| Local explosion prediction shows a phantom blast the server never confirms | 3 | 2 | 6 | Accepted and recorded in D3. Bounded by `SuppressionWindowSeconds`; the failure is one extra flash with no damage, and `ASuppressedPredictionExpiresAndDoesNotEatTheNextBlast` pins that the opposite failure — a swallowed real blast — cannot happen. |
| V1 also creates `NetClientExplosionPresenter.cs` because its Task 4 was never amended | 3 | 2 | 6 | D4 names the supersession, and § 7 lists the one-line amendment as an explicit handoff item rather than an assumption. Worst case is a merge conflict on a new file, which is loud. |
| Match HUD strings allocate per frame and show up in V9's p99 | 2 | 3 | 6 | Task 5 rebuilds strings only on change — the fix phase-05 Task 7 M8 already made for the lobby overlay, applied here from the start rather than retrofitted. |
| `MatchStateModel`'s interpolated timer drifts from the server's between broadcasts | 2 | 2 | 4 | Each message re-seeds the countdown, so drift is bounded by one broadcast interval and self-corrects. `IsStale` covers the case where broadcasts stop entirely. |

**Three risks reach 15 or higher, and two of them are the same risk seen from two sides:** this phase
produces work that CI structurally cannot grade. That is why § 7's Editor checklist is enumerated to
the individual check rather than delegated as a category, and why criterion 4 forbids the silent
degradation path by name. The third (Task 6 ordering) is discharged by sequencing, not by new code.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — Lookup seam + shared guard | S (0.5d) | No dependencies. **Start here** — every other task needs `TryGetActorTransform`. |
| 2 — Reuse audit + the five missing models | S (1d) | Needs nothing. The audit half is the first hour and may shrink the other four. |
| 3 — Death + ragdoll | M (1.5d) | Needs 1 and 2. Final third gated on **E1**. |
| 4 — Shooting feedback + `IngameUi` overload | M (1.5d) | Needs 1 and 2. Carries the phase's only Dev A review round. |
| 5 — Match HUD | S (1d) | Needs 2. Independent of 1, 3, 4. |
| 6 — Capture points | S (1d) | **Blocked on V8 Task 1. Severable, last.** Nothing above waits for it. |
| 7 — Explosions + local prediction | M (1.5d) | Needs 2. Independent of 3-6. Supersedes V1 Task 4. |
| 8 — Regression gate | S (1d) | **Independent of everything** — write it first if convenient; it is red until 3-7 land, and that red is informative. |
| 9 — Tests | M (1.5d) | Written alongside 1-8, not after. |
| **Total** | **~10.5 days (~2 weeks)** | Critical path: **1 → 3 → 4**. Tasks 5, 7 and 8 are off it and run in parallel. Task 6 is outside the estimate and lands whenever V8 Task 1 does. |

---

## 7. Handoff

### To Dev A — the Editor half, enumerated

CI cannot grade any of this. Each item is individually checkable and has a stated pass condition;
**E1 is blocking**, the rest are not.

| # | Item | Pass condition |
|---|---|---|
| **E1** | **`_remoteActorPrefab` (`RemoteActorRegistry.cs:42`) carries a ragdoll rig, a muzzle socket transform, and a decal receiver.** **Blocking for Tasks 3 and 4.** | A remote actor can be ragdolled, has a named muzzle transform, and accepts a blood decal. If any is absent, say so — Task 3's degraded path is designed for it, and the warning names this row. |
| **E2** | `.meta` files for `NetClientCombatPresenter.cs`, `NetClientObjectivePresenter.cs`, `NetClientExplosionPresenter.cs`, `NetClientPresenterGuard.cs`, and the five new files in `Ironfront.Net.Replication/Client/` | All present, no missing-meta warnings on import. **Note:** `ServerActorDamageSink.cs`, `ServerCombatBridge.cs` and `ServerCombatEvents.cs` are still missing theirs from phase-05, and V1 § 7 already asked for them — one pass covers all three phases. |
| **E3** | `NetClientCombatPresenter` prefab wiring: muzzle-flash `ParticleSystem`, report `AudioClip[]` indexed by weapon id, pooled tracer prefab | Every weapon id in `WeaponIds` has a clip or is deliberately left null; a null draws the default flash and plays nothing, and does not throw. |
| **E4** | `NetClientExplosionPresenter` prefab wiring: `ParticleSystem[]` indexed by `ExplosionKind` | Indices 0 (`Grenade`) and 1 (`Rocket`) filled; 2 (`Vehicle`) and 3 (`Environment`) may be empty and must not throw — carried over from V1 § 7 item 3 with the file. |
| **E5** | `NetClientObjectivePresenter` HUD wiring: ticket labels, phase label, phase timer, capture-progress bar | All five `MatchStateMessage` fields have somewhere to render; the timer dims on stale rather than freezing. |
| **E6** | The three presenters exist on the client bootstrap object and are absent from — or inert in — the server scene | A headless server build logs nothing from any presenter and dereferences no UI singleton. |
| **E7** | **Two-client test — combat.** A shoots B | B's client shows A's muzzle flash, hears the report, sees the tracer. A's client shows a hitmarker; B's does not. Both show B's ragdoll driven along the shot direction, and one killfeed line each. |
| **E8** | **Two-client test — capture point.** Both clients watch one point flip | Flag colour, capture bar and minimap marker change on both clients at the same authoritative value, and neither client's `CapturePoint` runs its own arithmetic. |
| **E9** | **Two-client test — grenade.** A throws a grenade | A sees the blast immediately (no RTT delay) and **exactly once**. B sees it once. Neither sees it twice. |
| — | Per design § 7: the Profiler run and per-weapon `Configuration` values in `_Managers.prefab` | Unchanged from the design doc; V2 owns the weapon table, not this phase. |

### To V1 — one amendment

**V1 Task 4 is superseded by V10 Task 7 (D4).** V1 should strike Task 4 and its
`NetClientExplosionPresenter.cs` row, keeping Tasks 1, 2, 3 and 5 unchanged; its § 7 item 3 (the
`ParticleSystem[]` wiring) moves to **E4** above. **V1 D6 is overridden by V10 D3** — using V1 D6's
own recorded fallback clause, so no new decision was made, only an earlier one taken.

V1 Task 5's `AnExplosionFramedByTheServerRoutesToTheClientHandler` still grades the router join and
is deliberately **not** duplicated in V10 Task 9.

### To V8 — one dependency, one confirmation

V10 Task 6 is **blocked on V8 Task 1** (D5) and is the second caller V8 D3 already anticipates. When
V8 Task 1 lands, `ApplyAuthoritativeOwner` should keep the exact signature V8 D3 states —
`(int team, float control, bool contested)` — because V10 Task 6 calls it with `Math.Abs(OwnerQ)/100f`
to match `CapturePointSlave.Apply`'s `Math.Abs(state.Owner)` on the server. If that mapping changes
on one side it must change on both, in one commit.

### To V9

V10 is a precondition for V9's two-client Editor test being meaningful at all: before this phase, a
second client renders no combat, no HUD and no objectives, so "the same vehicle in the same place"
(design criterion 1) would be the only thing observable. E7-E9 are the smaller versions of the same
test and should run first.

### Observations recorded, not fixed

- `RemoteActorRegistry.cs:105` iterates `_live` with `foreach` inside `Update()`. The `Dictionary`
  struct enumerator does not allocate, so this is a § 3.2 style violation and not a live defect.
  Recorded rather than fixed, per `coding-guidelines.md` § 3.
- The premise this phase was commissioned under listed **seven** dead events. It is **six** —
  `OnSnapshotApplied` has been subscribed by `ClientPredictionStage.cs:76` since prediction landed
  (D8). Recorded because the gate's expected-pass set depends on it.

**Still outside Dev C:** nothing in this phase.
