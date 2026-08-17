# Brainstorm — completing multiplayer: vehicles, mounted weapons, projectiles, objectives

**Written:** 2026-08-17 · **Author:** Dev C · **Status:** design approved, plan pending
**Audited against:** the repository at `1544e8f`. Every line number below came from a read, not a
recollection. Reproduce with the greps in § 10.

> **Design of record** for the vehicle/weapon/objective completion track. Phase files derived from
> this document are authoritative for *what each task is*; this page is authoritative for *why the
> shape is what it is*, and for the decisions nobody should re-litigate.

---

## 1. What the question assumed, and what is actually true

The brief was: *infantry and bots are synced; vehicles, weapons and capture points are not.* One
third of that is right. Six independent read-only audits (four vehicle/weapon lanes, one netcode
lane, one objectives lane) say this instead:

| System | Believed | Actual |
|---|---|---|
| Hitscan combat | not synced | **Server half done, client half never built.** Authority, `LagCompensator`, `HitboxHistory`, ammo, reload and respawn gating all shipped in phase-05 — and nothing on the client consumes the events. See § 2.4 |
| Capture points | not synced | **Built, duplicated, and invisible.** Two capture systems run simultaneously and disagree (§ 2.1), and the client renders neither (§ 2.4) |
| Explosions | not synced | **Wired end-to-end at the protocol layer and dead at both ends** — see § 2.2 |
| Per-weapon behaviour | assumed working | **Every weapon behaves as a rifle** — see § 2.3 |
| Vehicles | not synced | **Correct. Zero wiring.** |
| Projectile weapons | not synced | **Correct. Zero wiring.** |

So the work is not four greenfield subsystems. It is:

- **one greenfield system** — vehicles, seats, mounted weapons, projectiles;
- **one dead wire to connect** — explosions;
- **one duplicate-authority bug to resolve** — capture points;
- **one lookup table to populate** — weapon configs.

That reordering matters, because two of the four are days of work, not weeks, and they unblock
gameplay that is currently silently wrong.

---

## 2. The three findings that were not in anyone's plan

### 2.1. Two capture-point systems are running at once, and the wrong one decides respawns

`MatchController._capturePoints` is a `Transform[]`
([`MatchController.cs:43`](../../Ironfront_Reborn/Assets/Scripts/Net/Server/MatchController.cs#L43)),
not a `CapturePoint[]`. It reads only `t.position` (`:166`) and applies its own serialized
`_captureRadius = 15f` (`:45`) and `_captureSpeed = 0.2f` (`:46`).

Meanwhile the scene's original `CapturePoint` MonoBehaviour is **still running** its own
`InvokeRepeating("UpdateOwner", 1f, 1f)`
([`CapturePoint.cs:102`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/CapturePoint.cs#L102))
at `captureRange = 10f` (`:21`) and `CAPTURE_RATE_PER_PERSON = 0.05f` (`:9`), maintaining its own
`owner` int.

They disagree on every axis:

| | Netcode (`CapturePointState`) | Scene (`CapturePoint`) |
|---|---|---|
| Radius | 15 m | 10 m |
| Rate | 0.2/s/person, capped at 4 | 0.05/s/person, **uncapped** |
| Model | float −1…+1, single threshold | two-stage `control` + `pendingOwner` — lower the enemy flag, *then* raise yours |
| Tick | every `FixedUpdate` | 1 Hz |

And the one that loses is the one that is replicated: `ActorManager.RandomSpawnPointForTeam` and
`ServerCombatBridge.MoveToSpawnPoint` (`:233`) both read `SpawnPoint.owner` — the **scene**
component's value. `CapturePointState.OwningTeam` is never written back to it. Contested-spawn
safety has the same split: `ServerCombatBridge` does reach the original
`CapturePoint.GetSpawnPosition()` override (via the virtual call at `:241`), but that override
branches on `isContested`, computed by the scene component from *its* 10 m radius, not by
`CapturePointState.IsContested`.

`CapturePointState`'s own doc comment states the intended wiring —
*"The Unity wrapper reads position and radius off the existing `CapturePoint` component … and copies
them in"*
([`CapturePointState.cs:12-14`](../../Ironfront.Net.Replication/Match/CapturePointState.cs#L12)) —
and the wrapper does not do it.

**This is a live gameplay bug today, with or without vehicles.**

### 2.2. Explosions round-trip in tests and never fire in production

- `ServerMessageType.Explosion = 0x4A` — declared
  ([`MessageTypes.cs:52`](../../Ironfront.Net.Protocol/Enums/MessageTypes.cs#L52))
- `ExplosionMessage` — implemented, 10 bytes (`ActorLifecycleMessages.cs:152-200`)
- `ServerEventWriter.WriteExplosion` — implemented (`ServerEventWriter.cs:98`), **zero call sites**
- `ClientMessageRouter.OnExplosion` — implemented (`:107`), **zero subscribers**
- `ActorLifecycleMessageTests.cs:153-196` — passes, proving the codec, proving nothing about the game

This is exactly the shape `rules/wired-not-just-present.md` describes: present, tested, and never
run. It is also the cheapest real win available, because the transport half already exists.

### 2.3. Every weapon is a rifle

`ClientSession` hardcodes `WeaponConfig.Rifle`
([`ClientSession.cs:111`](../../Ironfront.Net.Replication/Server/ClientSession.cs#L111)) with no
assignment path — the in-file comment says *"until a loadout message or Dev A's weapon assets"*.
So all 17 weapons in `WeaponIds` share cooldown 0.1 s, damage 25, range 300 m, clip 30
(`WeaponModel.cs:52-54`).

The weapon **id** replicates correctly (`NetServerActor.cs:123-129`), so a remote client draws the
right model — and it then shoots like a rifle. A sniper, an SMG and a shotgun are currently
indistinguishable to the server. Damage drop-off over distance and balance/stagger damage are
absent from `WeaponConfig` entirely, though the original game has both
(`Projectile.cs:175-178`, `ActorManager.cs:353`).

### 2.4. The client subscribes to almost nothing

**Added 2026-08-17, after approval.** Surfaced by the V1/V2 planning lane while auditing § 2.2, then
verified independently. It is larger than the explosion gap, and it corrects § 1 of this document's
first draft.

`ClientMessageRouter` raises nine events. Exactly **two** have a production Unity subscriber —
`OnSpawnActor` and `OnDespawnActor`, both at
[`RemoteActorRegistry.cs:77-78`](../../Ironfront_Reborn/Assets/Scripts/Net/Client/RemoteActorRegistry.cs#L77).
`CapturePointMessage`, `MatchStateMessage`, `DeathMessage`, `WeaponFireMessage` and
`HitConfirmMessage` appear **only** in server-side files, and `Client/CombatFeed.cs` has zero Unity
consumers.

What a player experiences in multiplayer today:

| Behaviour | State |
|---|---|
| Other players and bots move | works |
| Other players shooting | silent and invisible — no `S_WEAPON_FIRE` consumer |
| Anyone dying | no feedback — no `S_DEATH` consumer |
| Your own hitmarker | absent — no `S_HIT_CONFIRM` consumer |
| Score, tickets, match phase, timer | no HUD — no `S_MATCH_STATE` consumer |
| Capture points | render nothing — no `S_CAPTURE_POINT` consumer |

So the answer to "what about capture points" is sharper than § 2.1 alone suggests: the server
computes capture correctly, the wire carries it, and the client draws nothing. The same holds for
every combat event phase-05 shipped.

This is `rules/wired-not-just-present.md` at scale. It is why phase **V10** exists, and why it runs
early rather than last.

---

## 3. The constraints that decide the architecture

### 3.1. The actor snapshot is full

`SnapshotField` is a `byte` and **8 of 8 bits are allocated**
([`GameplayEnums.cs:53-79`](../../Ironfront.Net.Protocol/Enums/GameplayEnums.cs#L53)).
`protocol-spec.md:334` still claims "7 used, 1 spare"; that is stale — bit 7 (`SeatInfo`) is
defined, sized and parsed. `ActorStateFlags` is likewise 8/8.

Two further ceilings sit behind it: `SnapshotHeader.ActorCount` is a `u8` (hard 255-entity limit per
snapshot, `SnapshotMessage.cs:19`), and quantized velocity is `i8` saturating at **64 m/s**
(`Quantize.cs:37`) — adequate for infantry and cars, silently wrong for a helicopter.

### 3.2. Vehicle physics cannot be ported to the engine-free library

The project's standing convention (phase-05 D2) is engine-free logic in
`Ironfront.Net.Replication`, Unity holding a thin seam. `MovementCore` proves it works for
infantry — it is a pure function, so the client can replay it.

Vehicles cannot follow. All four are `Rigidbody` + PhysX: `Car` and `Tank` through `WheelCollider`
force, `Boat` and `Helicopter` through `AddForce`/`AddRelativeTorque`. `Tank` additionally carries a
**second rigidbody** (`towerRigidbody`, `Tank.cs:36`) joined by a `Joint` that is *destroyed* on
explode (`Tank.cs:157`) — a topology change, not a value change. There is no porting PhysX.

The countervailing fact makes this survivable: **the server already runs Unity physics.**
`NetServerBootstrap` deliberately leaves `Time.fixedDeltaTime` alone and lets the netcode own a
separate 30 Hz accumulator
([`NetServerBootstrap.cs:135`](../../Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerBootstrap.cs#L135)).
Server-side vehicle simulation is therefore free — the work is capture and transport, not
simulation.

### 3.3. Determinism is not available, and seeding cannot buy it back

Three independent framerate couplings, all pre-existing:

| Site | Problem |
|---|---|
| `Car.cs:88` | Drives WheelCollider torque/brake/steer from **`Update()`**; never overrides `FixedUpdate`. Render-rate torque into a fixed-rate solver |
| `Helicopter.cs:55,63` → `:93` | `rotorSpeed` integrates in `Update` with `Time.deltaTime`, then **multiplies every force** in `FixedUpdate` |
| `TankTurret.cs:23-36`, `MountedTurret.cs:13-26` | Turret slew has **no `Time.deltaTime` at all** — a 144 Hz client traverses ~2.4× faster than a 60 Hz one |

Plus two latent ones: `Vehicle.cs:175` uses `Time.deltaTime` *inside* `FixedUpdate` (correct today
by accident, silently wrong if the burn tick ever moves), and `Helicopter.cs:56-59` applies
`Damage(Time.deltaTime * 30f)` from `Update` when inverted — damage-per-frame.

And the randomness cannot be synchronized by seeding. The gameplay-affecting draws —
projectile spread (`Weapon.cs:390`, `AlternatingMountedWeapon.cs:13`), turret kickback
(`TankTurret.cs:74`), recoil (`Weapon.cs:348`), tank explosion impulse on two bodies
(`Tank.cs:164-166`), helicopter burning torque (`Helicopter.cs:117`), vehicle explosion impulse
(`Vehicle.cs:387-388`) — share the **global** `UnityEngine.Random` stream with cosmetic audio-pitch
rolls (`Vehicle.cs:375`, `ExplodingProjectile.cs:80`, `Projectile.cs:95`, `Weapon.cs:157`). A
headless server that skips one audio roll desynchronizes every subsequent gameplay roll. Separating
the streams would mean touching every call site; server-authoritative resolution does not.

### 3.4. Vehicles are not `Hurtable`, and their damage channel carries no attacker

`Vehicle : MonoBehaviour` ([`Vehicle.cs:4`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs#L4)).
`Actor` is the **only** `Hurtable` subclass in the assembly. Vehicles take damage through:

```csharp
// Vehicle.cs:261
public void Damage(float amount)
```

One float. No direction, no impact point, no piercing flag, **no attacker identity**, no return
value. And `health` is `private` with no setter (`Vehicle.cs:60`) — an authoritative HP snapshot has
nowhere to land. Both are signature changes, not transport problems.

Note also that vehicles do not die at zero HP: `health <= 0` sets `burning` (`:270`), and death
arrives via the `burnTime` countdown in `FixedUpdate` (`:173-180`). `burnTime` (`:52`) is
simultaneously the serialized designer default **and** the live countdown — replicating the prefab
value is not replicating the timer.

### 3.5. Seats have no network identity, and rejections have no path home

`Seat.cs` carries no id field of any kind. The only stable handle is `(vehicle, index into
vehicle.seats)`, which is how `SwitchSeat` addresses it (`Actor.cs:1064`). `seats[0]` is the driver
**by array-index convention**, not by `Seat.Type` — `Seat.Type.Driver` exists and is never consulted
(`Vehicle.cs:118`, `:123`, `:190`, `:224`).

`Actor.EnterSeat` returns `bool` and **every caller discards it** —
`FpsActorController.cs:643`, `AiActorController.cs:599`, `Actor.cs:1067`. A server rejection has no
existing surface to travel back on.

Two further hazards on the seat path:

- `SwitchSeat` (`Actor.cs:1059-1070`) is a `LeaveSeat()` + `EnterSeat()` pair that teleports the
  rigidbody to the exit offset (`:973-974`) and back in the same frame, and bypasses
  `CanEnterSeat()` — so the 1 s re-entry lockout it starts at `:981` is enforced on the use-ray path
  and not here.
- `ReactivateCollisionsWith` (`Actor.cs:985-997`) holds hitbox layer state across a **0.5 s
  wall-clock** wait and re-reads seat state after it. That is a direct race against any
  network-driven seat change.

### 3.6. A dedicated server crashes on vehicle spawn today

Unguarded dereferences that NRE in a stripped headless build:

| Site | Call |
|---|---|
| `VehicleSpawner.cs:33` | `GetComponent<Renderer>().enabled = false` — no null check |
| `Vehicle.cs:274`, `:323` | `damageParticles.Play()` / `.Stop()` (contrast `burnParticles` at `:282`, which *is* guarded) |
| `Vehicle.cs:389-393` | `deathParticles`, `audio`, `explosionSound` |
| `Vehicle.cs:374-376` | `impactAudio` |
| `Helicopter.cs:44-45` | `rotor.GetComponent<Renderer>()`, dereferenced every `Update` at `:66-67` |
| `Vehicle.cs:542` | `ActorManager.instance.debug` — dereferenced **before** the `Camera.main != null` guard on the same line |
| `VehicleSpawner.cs:49` | `GameManager.instance.noVehicles` |
| `Vehicle.cs:252` | `spawner.FirstDriverEntered(this)` — **added 2026-08-17 (A7).** Unguarded, while the sibling call at `:337` *is* guarded. A scene-placed vehicle NREs the first time anyone drives it, in the Editor as well as headless |
| `ExplodingProjectile.cs:75-79` | `impactParticles.Play()`, `audioSource.Stop()/.pitch/.Play()` — **added 2026-08-17.** Unguarded, while `trailParticles` at `:72` *is* guarded |

Plus `TankTurret.cs:66` and `MountedTurret.cs:56` read `Input.GetAxis` and `OptionsUi.GetOptions()`
directly inside `Update`, bypassing `ActorController` entirely — there is no abstract member for
turret aim.

---

## 4. Decisions taken (do not re-litigate)

| # | Decision | Why |
|---|---|---|
| **D1** | **Vehicles get their own entity stream** — new opcode `S_VEHICLE_SNAPSHOT = 0x4C`, own `u16` change mask, own `VehicleIdPool`. Not extra bits on `ActorSnapshotEntry` | `SnapshotField` is 8/8 (§ 3.1). Beyond capacity, the actor entry cannot *express* a vehicle: `i8` velocity saturates at 64 m/s, and rotation is yaw+pitch with no roll. 20 server opcodes are free (0x4C–0x5F) |
| **D2** | **Seat occupancy lives on the ACTOR entry**, via the existing `SnapshotField.SeatInfo`. The vehicle entry carries no occupancy | The path is already half-built — `DeltaDecoder.ApplyEntry` applies it (`:209-213`); only `SnapshotBuilder.Capture` and `DeltaEncoder.ComputeChangeMask` are missing. One source of truth for "who is in what seat" beats two that can disagree |
| **D3** | **Driver prediction is error-corrected simulation, not input replay.** Client simulates its own vehicle continuously; each snapshot produces a blended correction (extrapolate server state by RTT/2, blend pos/rot over ~150 ms, hard-snap past a threshold) | `PredictionReconciler` replays unacked inputs through `MovementCore` because that is a pure function. PhysX is not — tick N−3 cannot be re-simulated without re-running the scene. Authority is preserved because the server never accepts a client transform |
| **D4** | **All gameplay randomness resolves server-side** and replicates as a result | § 3.3 — the streams are shared, so seeding cannot work without touching every call site |
| **D5** | **Projectiles replicate by parameter, not by state** — one spawn event carrying `(origin, velocity, spawnTick)`; clients simulate locally; server owns hits | Once the timestep is fixed, the trajectory is fully determined and gravity is constant. Costs ~16 B per shot instead of a per-tick entity stream |
| **D6** | **`CapturePointState` wins; the scene component becomes a slave.** Its `InvokeRepeating("UpdateOwner")` is disabled on the server and `SpawnPoint.owner` is written from the authoritative value | Deleting the component would take `GetSpawnPosition()`'s contested-spawn logic with it, which is real gameplay worth keeping. Slaving it is smaller and reversible |
| **D7** | **One protocol bump covers everything** — `PROTOCOL_VERSION` 2 → 3, one changelog row, one 2-approval PR | The change process costs a review round each time; batching is strictly cheaper and the client and server ship together |
| **D8** | **All pre-existing determinism and headless bugs are fixed**, not worked around | Explicitly chosen. Consequence: offline single-player handling is **no longer byte-identical** to the original — see § 7 |

---

## 5. Protocol v3.0.0

Follows the documented process in `protocol-spec.md § 15`: PR with 2 approvals, a changelog row, a
`SpecChecker` update, and `PROTOCOL_VERSION` 2 → 3 because the bytes on the wire change.

| Opcode | Dir | Ch | Purpose |
|---|---|---|---|
| `C_VEHICLE_INPUT = 0x21` | C→S | 3 | 4 axes + turret aim, sent only while seated. Uses the one free client opcode |
| `C_SEAT_REQUEST = 0x26` | C→S | 2 | **Already reserved**, currently falls through to `UnknownMessages++`. Carries `(vehicleId, seatIndex, enter/leave)` |
| `S_VEHICLE_SNAPSHOT = 0x4C` | S→C | 1 | Vehicle entity stream |
| `S_VEHICLE_SPAWN / DESPAWN = 0x4D / 0x4E` | S→C | 2 | Spawner lifecycle, wreck cleanup. Despawn carries a `VehicleDespawnReason { Destroyed, Wrecked, Cleanup }` — `Wrecked` is what makes the tank's turret detachment replicable as an event rather than as state |
| `S_PROJECTILE_SPAWN = 0x4F` | S→C | 2 | Launch parameters per D5. **20 B**, not the "~16 B" this document first estimated: the field list is the phase's to fix, and V7 needs a `RemainingLifetimeDeciseconds` byte to express the Medipack's self-shortening lifetime. Reaching 16 would need a truncated `u16 spawnTick` plus wrap resolution — a bad trade, not taken |
| `S_SEAT_CHANGE = 0x50` | S→C | 2 | Authoritative enter/leave. Required by § 3.5 — rejections currently have no path home |
| `S_EXPLOSION = 0x4A` | S→C | 2 | **Exists.** Needs a caller and a subscriber, nothing more |

Plus: finish `SnapshotField.SeatInfo` on the actor entry (D2), which moves
`InterestManager.MaxEntrySize` from 20 → 23 and therefore changes shedding behaviour — that is why
§ 8 grades it rather than assuming it.

### Vehicle entry shape

| Field | Wire | Bytes |
|---|---|---|
| `VehicleId` | u16 | 2 |
| `ChangeMask` | u16 | 2 |
| Position | i16 × 3, `Quantize.PackPos` | 6 |
| Rotation | smallest-three quaternion | 4 |
| Linear velocity | i16 × 3 | 6 |
| Angular velocity | i8 × 3 | 3 |
| Health | u8 (normalized against `maxHealth`) | 1 |
| Flags (`dead`, `burning`, `inWater`, `airborne`) | u8 | 1 |
| Turret yaw / pitch | u16 + i8 | 3 |
| Subtype tail (`rotorSpeed` / `steerAngle` / `currentMuzzle` + friction) | — | 2 |
| **Full** | | **30** |

Rotation is a full quaternion rather than yaw+pitch because vehicles roll. Velocity is `i16`
rather than `i8` for the reason in § 3.1.

### Two vehicle constants, not one

**Added 2026-08-17.** The V3 and V4 lanes proposed 16 and 32 for the same name, and both were right
about different things — conflating them is the error.

Dustbowl and Island carry **14 `VehicleSpawner` instances each** (counted by GUID in the scene
files), and each spawner holds at most one live vehicle. The id arithmetic does not fit in 16:
`Vehicle.Die()` invokes `Cleanup` at t=15 s and the 5 s quarantine runs to t=20 s, while
`VehicleSpawner` respawns at `spawnTime` = 16 s — so a replacement spawns **four seconds before the
dead id leaves quarantine**. If all 14 die together, the pool needs 28 ids for that window.

| Constant | Value | What it bounds |
|---|---|---|
| `MAX_VEHICLES` | **32** | The id space and the quarantine window. 14 live, doubled for overlap, plus slack — the same shape as `MAX_ACTORS = 64` over 48 live |
| `MAX_VEHICLES_PER_SNAPSHOT` | **16** | Interest-management admission, and therefore the 489 B worst-case vehicle body that protects the actor floor |

Interest management already caps admission; the id pool does not need to.

### Bandwidth

Measured today: **1.67 KB/s/client** shipped, against a 5–7 KB/s spec target — roughly 3–4× headroom
(`plans/dev-c-replication/reports/2026-08-13-phase-04-report.md:57-72`).

Vehicles ride the existing interest bands (Near 20 Hz, Mid 10 Hz, Far 4 Hz). A realistic
8-vehicles-visible distribution of 2 Near / 3 Mid / 3 Far gives 82 entries/s; at a ~20 B typical
delta that is **~1.6 KB/s**, for a total near 3.3 KB/s. Inside the target, but close enough that
§ 8 grades it as a criterion rather than assuming it.

`SeatInfo` on actors adds 3 B per *seated* actor and only on change — negligible. Projectiles are
events, not a stream — one ~16 B message per shot.

---

## 6. Phases

| # | Phase | Depends on | Wire change |
|---|---|---|---|
| **V0** | **Debt + seams.** `Car` `Update`→`FixedUpdate`; `Time.deltaTime` on turret slew; authoritative `yaw`/`pitch` floats driving the joint instead of accumulating into it; `Vehicle.health` setter + attacker id on `Damage`; server-side clamping for `Boat`/`Tank`; the `AutoDamage` double-schedule; `Boat`'s world/local `AddRelativeTorque` axis bug; the 6 m/9 m explosion falloff quirk; every unguarded headless NRE from § 3.6 | — | none |
| **V10** | **Client event consumption.** Subscribe to `S_DEATH`, `S_WEAPON_FIRE`, `S_HIT_CONFIRM`, `S_MATCH_STATE`, `S_CAPTURE_POINT`, `S_EXPLOSION` (§ 2.4). Plus a regression gate asserting every router event has a production subscriber | V0 | none |
| **V1** | **Explosions.** Connect the dead wire (§ 2.2); server-authoritative `ActorManager.Explode` | V0 | none |
| **V2** | **Weapon configs.** `weaponId → WeaponConfig` table; damage drop-off; balance/stagger damage | — | none |
| **V3** | **Protocol v3.** All of § 5, plus finishing `SeatInfo`; conformance tests; spec doc; `SpecChecker` | V0 | **yes** |
| **V4** | **Vehicle server authority.** `VehicleIdPool`, registry, capture/encode, interest bands, seat arbitration, damage sink | V3 | — |
| **V5** | **Client vehicle replication.** Remote interpolation + driver prediction per D3 | V4 | — |
| **V6** | **Mounted weapons + turrets.** Aim replication, fire authority, `currentMuzzle` | V4 | — |
| **V7** | **Projectiles.** Deterministic-from-parameters flight; grenades, rockets; ammo bag and medipack as world entities | V1, V3 | — |
| **V8** | **Objectives.** Resolve § 2.1 per D6; `SpawnPoint.owner` writeback; `VehicleSpawner` lifecycle | — | — |
| **V9** | **Integration + measurement.** Two-process harness, 16-player load, bandwidth and p99 re-measure | all | — |

**Execution order is not filename order.** V10 sorts last and runs second — immediately after V0 and
before the vehicle chain. It carries the highest gameplay value per day in the plan, because it makes
combat and capture points that are *already shipped server-side* visible for the first time, and it
needs no protocol change. Running it last would mean grading every intervening phase against a client
that shows no combat feedback, which is what makes V9's integration results uninterpretable.

**V1, V2 and V8 are off the vehicle critical path** and can land while V3 is in review. V0 is
load-bearing for everything: without it, prediction cannot converge, because two peers at different
framerates diverge from identical input.

### Post-approval amendments

Recorded here rather than edited silently into the sections above, so the delta from the approved
design stays auditable.

| # | Amendment | Origin |
|---|---|---|
| A1 | **V10 added and sequenced second.** § 2.4 was not known at approval time | User decision, 2026-08-17 |
| A2 | **`VehicleIds` registry kept** (`Vehicle.networkId` + a `SpecChecker` gate). `S_VEHICLE_SPAWN`'s `u8 vehicleType` would otherwise have no authority outside the prefabs — the exact hole that produced spec § 4.8 and `WeaponIds` at changelog 2.0.1. Costs ~1 day and a Dev A authoring round | User decision, 2026-08-17 |
| A3 | **Your own explosion is predicted locally** and the matching `S_EXPLOSION` suppressed by `SourceActorId`. Overrides V1's D6, which chose server-sourced cosmetics for everyone; V1 D6's fallback text becomes the primary path | User decision, 2026-08-17 |
| A4 | **`MAX_VEHICLES` split into two constants** — see § 5 | Reconciling the V3 and V4 lanes |
| A5 | **`S_PROJECTILE_SPAWN` is 20 B**, not ~16 B | V7 lane; § 5 |
| A6 | **`S_VEHICLE_DESPAWN` carries a reason enum** | V4 lane needs it; V3 owns the byte |
| A7 | **`Vehicle.cs:252` added to the § 3.6 hazard list** — `DriverEntered()` calls `spawner.FirstDriverEntered(this)` unguarded while the sibling call at `:337` is guarded, so a **scene-placed** vehicle NREs the first time anyone drives it. Found independently by two lanes; in the Editor as well as headless, so it is a live single-player bug | V0 and V8 lanes |
| A8 | **`MatchController.WorldResetRequested` has zero subscribers** — declared at `:73`, invoked at `:256`, doc comment claims "the spawner subscribes". Match two inherits match one's vehicles, so acceptance criterion 13 cannot pass until V8 adds one | V8 lane |
| A9 | **`SetInputSource` has zero production call sites** and `NetInputSource` is constructed only by tests. Nothing noticed because server movement bypasses the controller entirely — so the moment a networked player drives, `Driver().controller.CarInput()` reads a `LocalInputSource` in a headless build. Installing it on seat entry became a V5 deliverable | V4/V5 lane |
| A10 | **`Vehicle.Explode()` never calls `ActorManager.Explode`** — vehicle wrecks do zero blast damage in the original game, so `ExplosionKind.Vehicle` has no caller and adding one is a gameplay change, not a wiring change | V1 lane |
| A11 | **`Vehicle.Clamp2`/`Clamp4` were never a validation boundary** — `Mathf.Clamp(NaN, -1, 1)` returns `NaN`, so even the two vehicles that *do* clamp were open to a `NaN` axis reaching the rigidbody | V0 lane |

---

## 7. Ownership boundary

Per the consumer decision on 2026-08-17: **all code is written here, including files under
`Assets/Scripts/Assembly-CSharp/`** (`Vehicle.cs`, `Car.cs`, `Tank.cs`, `TankTurret.cs`,
`Actor.cs`). This is a deliberate, recorded departure from `conventions.md § 7`, on the same
precedent as phase-05 Task 6 — PR plus a Dev A review round.

**Dev A owns only what requires the Unity Editor:**

- `.meta` files for new scripts
- Prefab field wiring (turret references, `NetVehicle` component attachment)
- Scene binding of `MatchController._capturePoints` to the real `CapturePoint` components
- Per-weapon `Configuration` values in `_Managers.prefab` (currently the only place they exist —
  the server cannot read them)
- The Profiler run and the two-client Editor test

**Accepted cost of D8:** fixing `Car`'s `Update`→`FixedUpdate` changes single-player handling feel
at high framerates. Offline play is no longer byte-identical to the original game. This was chosen
explicitly; it is recorded here so it is not discovered later as a regression.

---

## 8. Acceptance criteria

1. Two clients see the same vehicle in the same place while a third drives it, at 100 ms RTT and 5%
   loss.
2. The driving client's own vehicle has no perceptible input lag, and its position converges to the
   server's without visible snapping under normal conditions.
3. A client that sends out-of-range vehicle input is clamped server-side and gains no advantage.
4. Turret aim is identical on server and all clients, and slew rate is **framerate-independent** —
   verified by driving the same turret at 30 Hz and 144 Hz and comparing traverse over 1 s.
5. A grenade thrown by one client detonates at the same position on every client, and the resulting
   damage is applied once, by the server.
6. Explosion damage moves authoritative health; `S_EXPLOSION` has a caller **and** a subscriber.
7. There is exactly one capture-point authority. `SpawnPoint.owner` matches
   `CapturePointState.OwningTeam` at all times.
8. A weapon that is not a rifle behaves differently from a rifle on the server.
9. **Bandwidth ≤ 5 KB/s/client** measured at 16 players + 32 bots + 12 vehicles. A non-zero
   `EntriesShed` at that load is a **failure**, not a pass — same convention as `InterestManager.cs:149-155`.
10. Tick p99 < 33 ms at the same load.
11. A headless server survives vehicle spawn, damage, death and respawn with zero NREs.
12. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-tick allocation
    in any new logic file.
13. Five matches back to back with `AssertCleanState()` passing, including vehicle and projectile id
    pools.

---

## 9. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Bandwidth exceeds budget once vehicles stream | 3 | 4 | **12** | Criterion 9 grades it rather than assuming it. Fallbacks in priority order: drop angular velocity at Mid/Far, widen the Far band, cut the vehicle snapshot to 10 Hz |
| Prediction never converges because PhysX diverges faster than the blend corrects | 3 | 5 | **15** | V0 removes the framerate coupling *first*. If it still diverges, D3 degrades to no-prediction behind one flag — the remote-interpolation path is shared, so this is a config change, not a re-architecture |
| `Actor.cs` conflicts with Dev A's branch | 4 | 3 | **12** | V0 touches it once, early. Sequence V0 before Dev A opens the file; announce it in the PR |
| Fixing `Car` `Update`→`FixedUpdate` changes single-player feel | 5 | 2 | 10 | Accepted and recorded (§ 7). Pin the new behaviour with a test so it does not drift again |
| Both capture systems write ownership during the transition | 3 | 4 | 12 | D6 slaves the scene component rather than deleting it; a test asserts `SpawnPoint.owner == CapturePointState.OwningTeam` every tick |
| Seat-change race against `ReactivateCollisionsWith`'s 0.5 s coroutine | 3 | 3 | 9 | V0 replaces the coroutine with a tick-counted timer owned by the server; the re-check at `Actor.cs:988` becomes an authoritative read |
| `Tank` turret detachment (destroyed joint + free second body) is not expressible as value sync | 2 | 3 | 6 | Replicate it as an **event** (`S_VEHICLE_DESPAWN` with a wreck flag), not as state. The wreck is cosmetic after death |
| Protocol v3 review round blocks V4–V7 | 3 | 3 | 9 | V1, V2 and V8 are severable and land during the review |
| Per-weapon `Configuration` values live only in `_Managers.prefab` | 4 | 2 | 8 | V2 defines the table shape and ships placeholder values; Dev A fills them. The seam takes a `WeaponConfig`, so swapping numbers is data, not code |

Two scores reach 12+ and one reaches 15. The 15 (prediction convergence) has a stated fallback that
costs one flag; that fallback existing is a precondition of starting V5.

---

## 10. Reproducing every claim here

```bash
cd Ironfront_Reborn/Assets/Scripts/Assembly-CSharp

# § 3.4 — Vehicle is not Hurtable; Actor is the only subclass
grep -rn ": Hurtable" *.cs                       # 1 hit: Actor.cs
grep -n "class Vehicle" Vehicle.cs               # : MonoBehaviour

# § 3.3 — the framerate couplings
grep -n "void Update\|void FixedUpdate" Car.cs Helicopter.cs Tank.cs Boat.cs
grep -n "Time.deltaTime" TankTurret.cs MountedTurret.cs   # zero hits = the bug

# § 3.3 — the shared Random stream
grep -n "Random\." Vehicle.cs Tank.cs Helicopter.cs TankTurret.cs \
                   Weapon.cs Projectile.cs ExplodingProjectile.cs

# § 3.6 — headless hazards
grep -n "GetComponent<Renderer>\|Camera.main\|OnGUI" Vehicle.cs VehicleSpawner.cs Helicopter.cs

cd ../../../..

# § 3.1 — the change mask is full
grep -n "SnapshotField" Ironfront.Net.Protocol/Enums/GameplayEnums.cs

# § 2.2 — explosions: declared, implemented, never called
grep -rn "WriteExplosion\|OnExplosion" --include=*.cs .

# § 2.1 — the two capture systems
grep -n "_capturePoints\|_captureRadius\|_captureSpeed" \
     Ironfront_Reborn/Assets/Scripts/Net/Server/MatchController.cs
grep -n "UpdateOwner\|captureRange\|CAPTURE_RATE" \
     Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/CapturePoint.cs

# the finding that started this: zero vehicle wiring in the netcode
grep -rln "Vehicle\|Seat\|Tank\|Helicopter" Ironfront_Reborn/Assets/Scripts/Net/
```

---

## 11. Related

- [`plans/00-shared/protocol-spec.md`](../00-shared/protocol-spec.md) § 4.3, § 15 — the change mask
  and the wire-change process
- [`plans/00-shared/conventions.md`](../00-shared/conventions.md) § 7 — the ownership boundary this
  track departs from, with consent
- [`plans/dev-c-replication/phases/phase-03-match.md`](../dev-c-replication/phases/phase-03-match.md)
  — the capture-point plan whose Task 2 § 2.1 shows was only half-executed
- [`plans/dev-c-replication/phases/phase-05-combat-authority.md`](../dev-c-replication/phases/phase-05-combat-authority.md)
  — the precedent for editing a Dev A file by PR, and the combat authority this track extends
- [`docs/codebase-map.md`](../../docs/codebase-map.md) — the original game's shooting flow; § 6 of
  that document explicitly deferred vehicles, which is what this track picks up
