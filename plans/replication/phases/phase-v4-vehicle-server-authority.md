# the replication track — Phase V4: Vehicle server authority

> Design of record: [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md).
> Read § 3.2 (why vehicle physics stays in Unity), § 3.4 (the damage channel), § 3.5 (seats have no
> identity), § 5 (the wire shape, fixed), and D1/D2/D4 (§ 4). This phase implements those decisions;
> it does not re-derive them.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation inside hot loops, no LINQ **in the hot path**, no exceptions for normal control
> flow, `Span<byte>` over `byte[]`) and § 7 (file ownership — with the recorded departure in design § 7, which lets this
> track edit `Assembly-CSharp/` files by PR plus a the client track review round).
>
> **Depends on:** V3 (protocol v3 — `S_VEHICLE_SNAPSHOT` 0x4C, `S_VEHICLE_SPAWN/DESPAWN`
> 0x4D/0x4E, `S_SEAT_CHANGE` 0x50, `C_VEHICLE_INPUT` 0x21, `C_SEAT_REQUEST` 0x26, the 30-byte
> vehicle entry, `SnapshotField.SeatInfo` finished) and V0 (`Vehicle.health` setter, attacker id on
> `Vehicle.Damage`, the framerate fixes, the headless NRE guards).
> **Blocks:** V5 (client vehicle replication), V6 (mounted weapons).

---

## 1. Objectives

Vehicles today have **zero** netcode wiring — the grep in design § 10 returns nothing under
`Assets/Scripts/Net/`. There is no id space, no registry, no capture, no interest handling, no seat
arbitration, and no authoritative damage path. `Vehicle` is not `Hurtable`, its `health` is `private`
with no setter (`Vehicle.cs:60`), and `Damage(float)` (`:261`) carries no attacker.

By the end of this phase:

1. Every replicated vehicle holds a stable network id from a `VehicleIdPool` with the same
   quarantine guarantee `ActorIdPool` gives actors.
2. The server captures each vehicle's Rigidbody state once per tick into a vehicle world buffer and
   encodes it as `S_VEHICLE_SNAPSHOT` deltas.
3. Vehicles ride the existing interest bands (Near 60 m / 20 Hz, Mid 150 m / 10 Hz, Far
   300–500 m / 4 Hz, nothing inside 500 m culled) **without** disturbing the actor AOI path or
   colliding with it in the rate table.
4. Seat entry and exit are server-arbitrated over `C_SEAT_REQUEST` → `S_SEAT_CHANGE`, and a
   rejection reaches the requesting client — which is impossible today (design § 3.5).
5. Vehicle damage, health, `burning` and death are authoritative; death replicates as an event, not
   as a health threshold.
6. `seatsClaimedByBots` stops being an identity-free counter drained by a timer.
7. All of it is graded by tests that run in CI under `dotnet test` with no Editor. The checks that
   genuinely need Unity are named and handed to the client track.

**Not in this phase.** Client rendering, interpolation and driver prediction (V5). Mounted-weapon
aim and fire authority, `currentMuzzle` (V6). Projectiles (V7). `VehicleSpawner` match lifecycle
(V8). No Profiler run, no Editor session.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **V4-D1** | **Vehicles get their own id space and their own pool.** `VehicleIdPool` — `ushort`, 1-based, `0` = unassigned, FIFO queue (not a stack), 5-second quarantine before reuse, `ResetAll()` on match reset. Not shared with `ActorIdPool`: sharing would spend the 64-id actor budget on vehicles and would force every message carrying an entity id to disambiguate two kinds at the parse. |
| **V4-D2** | **The pool mechanism is extracted, not copied.** `ActorIdPool` and `VehicleIdPool` become thin sealed wrappers over one internal `QuarantinedIdPool`. `development-principles.md` § SSOT forbids a second implementation of a rule whose entire value is that it is uniform. `ActorIdPool`'s public surface is preserved byte-identically so its phase-03 tests are not edited. |
| **V4-D3** | **The AOI rate table is per entity kind.** `InterestManager._lastSentSnapshot` keys on `PackPair(viewer, target)` = `(viewer << 16) \| target`, a `uint`. Vehicle id 7 and actor id 7 produce the **same key** and would silently steal each other's rate slots — a Far vehicle would starve whenever a Far actor shared its number. Vehicles get a `VehicleInterestTracker` with its own pair dictionary and its own `EntriesShed` / `LastViewShedCount` counters, reusing `InterestManager`'s band constants and `SendEveryN` table so "Near is 60 m at 20 Hz" is defined once. |
| **V4-D4** | **`InterestManager.Evaluate` gains a positional overload via a shared struct — not an interface, not a generic.** Extract `readonly struct InterestSubject { ushort Id; short PosX, PosY, PosZ; byte Team; ushort Yaw; }` with `From(in ActorSnapshotEntry)` and `From(in VehicleSnapshotEntry)`. The existing `Evaluate(in ActorSnapshotEntry, in ActorSnapshotEntry)` becomes a forwarder with its signature unchanged, so phase-02/03/05 AOI tests are untouched. An interface would box or dispatch virtually on the 30 Hz path (§ 3.2); a generic would need the interface anyway. `InterestSubject` carries exactly the fields the classifier reads today — id, packed position, team, and the yaw `IsInViewCone` needs — and nothing else. |
| **V4-D5** | **A vehicle is never a viewer.** `InterestSubject.From(in VehicleSnapshotEntry)` writes `Team = TeamId.None`, so the teammate floor at `InterestManager.cs:224` cannot fire for a vehicle, and the view-cone test is only ever reached with an actor viewer. The vehicle path calls `Evaluate` with an actor viewer only; a debug assert pins it. |
| **V4-D6** | **Seat identity on the wire is `(vehicleId u16, seatIndex u8)`.** `Seat.cs` gains no id field. The index is the index into `Vehicle.seats`, which is already the handle `Actor.SwitchSeat` uses (`Actor.cs:1064`). `seats[0]` stays the driver **by array-index convention** — `Seat.Type.Driver` remains unconsulted, exactly as today (`Vehicle.cs:118`, `:123`, `:190`, `:224`). Changing that convention is prefab data, i.e. The client track. V4 **pins** it with a test instead of changing it. |
| **V4-D7** | **The rejection path is the message, not the return value.** The three existing `Actor.EnterSeat` call sites that discard its `bool` (`FpsActorController.cs:643`, `AiActorController.cs:599`, `Actor.cs:1067`) are left alone — they are the offline and AI paths. A networked client never calls `EnterSeat` speculatively: it sends `C_SEAT_REQUEST` and acts only on `S_SEAT_CHANGE`. The server has **one new** call site, inside the seat arbiter's bridge, and that one **does** check the bool; a `false` becomes `SeatChangeResult.Refused` addressed to the requester alone. |
| **V4-D8** | **Network seat *switching* is expressed as leave-then-enter, two requests.** `Actor.SwitchSeat` (`:1059-1070`) is a `LeaveSeat()` + `EnterSeat()` pair that teleports the rigidbody to the exit offset and back inside one frame (`:973-974`) and **bypasses `CanEnterSeat()`**, so the 1-second re-entry lockout started at `:981` is enforced on the use-ray path and not on this one. Routing the network path through two independently arbitrated requests buys back the lockout and the capacity check, at the cost of one extra round trip on a rare action. |
| **V4-D9** | **Seat mutations are serialised through one arbiter, inside the tick, never from a coroutine.** Requests arriving in the same tick are resolved in arrival order, ties broken by ascending connection id so a test is deterministic. V0 replaces `Actor.ReactivateCollisionsWith`'s 0.5-second wall-clock `WaitForSeconds` (`Actor.cs:985-997`) with a tick-counted timer; V4 consumes that and never mutates seat state outside `ServerTickLoop.RunInputStage`. |
| **V4-D10** | **`seatsClaimedByBots` becomes derived, not mirrored.** Today it is an `int` counter (`Vehicle.cs:34`) incremented by `ClaimSeat` (`:206`), decremented by `DropSeatClaim` (`:212`) and drained by a 10-second `drainClaimAction` timer (`:111`) — two bots claiming and one dying leaves it permanently wrong, and it names nobody, so no client could reconcile it. At server role the claims live in `ServerVehicleRegistry` as a per-vehicle `ushort[]` of claiming actor ids, released on `ServerActorRegistry.ActorUnregistered` (which already fires) or on a per-claim timeout. The public member becomes a **computed** value at server role, so there is no stored duplicate — `code-conventions.md` § "No Derived Fields" forbids one. Offline behaviour is unchanged. **It is not replicated:** it is server-side AI bookkeeping and a client has no use for it. |
| **V4-D11** | **Vehicles do not die at zero health; the wire reflects that.** `health <= 0` sets `burning` (`Vehicle.cs:270`) and death arrives from the `burnTime` countdown in `FixedUpdate` (`:173-180`). The vehicle entry therefore carries `health` (u8, normalised against `maxHealth`) and a `burning` flag bit; **`burnTime` is not replicated**, because the field (`:52`) is simultaneously the serialised designer default and the live countdown and a client receiving it could not tell which it held. Clients run a cosmetic burn from the flag; the server owns when it ends and announces it. |
| **V4-D12** | **Death and wrecking replicate as an event.** `S_VEHICLE_DESPAWN` carries a reason (`Destroyed` / `Wrecked` / `Cleanup`). `Tank.Die` destroys `towerJoint` (`Tank.cs:157`) and leaves a second free rigidbody — a **topology** change no value stream can express (design § 9). The client plays its own local destruction and stops applying snapshots for that id from the moment the event lands. |
| **V4-D13** | **Input is clamped twice, at the wire and at the vehicle, and that is not duplication.** `Car` clamps through `Vehicle.Clamp2` (`Car.cs:96`) but `Tank` (`:86`) and `Boat` (`:60`) read `CarInput()` / `BoatInput()` raw. V0 fixes the vehicle-side clamp — a gameplay invariant that must also hold offline. V4 clamps again at message decode, so an out-of-range axis never reaches Unity at all — a protocol invariant, graded by design § 8 criterion 3. |
| **V4-D14** | **Capture reads the `Rigidbody`, not the `Transform`.** `rigidbody.position` / `.rotation` / `.linearVelocity` / `.angularVelocity`. The transform lags the body by up to one physics substep, and shipping that lag would put a constant interpolation error into every client for free. |
| **V4-D15** | **The engine-free / Unity split is drawn at *decides* vs *applies*.** Every rule — id allocation, interest classification, rate limiting, seat arbitration, the damage and burn state machine — is engine-free in `Ironfront.Net.Replication` and fully testable under `dotnet test`. Unity holds only the two things PhysX and MonoBehaviour make impossible to move: **reading** the Rigidbody (capture) and **calling** `Actor.EnterSeat` / `LeaveSeat` / `Vehicle.Damage` (application). See § 5 for the file-by-file statement. |

---

## 3. Detailed tasks

### Task 1 — `VehicleIdPool`, extracted from `ActorIdPool` (0.5 day)

Per V4-D1 and V4-D2.

| File | Change | Side |
|---|---|---|
| `Ironfront.Net.Replication/Server/QuarantinedIdPool.cs` | **New, internal.** The whole mechanism lifted verbatim from `ActorIdPool.cs:38-169`: free `Queue<ushort>`, quarantine `Queue<QuarantinedId>`, `HashSet<ushort> _inUse`, `TryAcquire(now, out id)`, `Release(id, now)`, `ReleaseExpired(now)`, `ResetAll()`, `IsFullyReleased`. Clock passed in, never read from `DateTime`. | engine-free |
| `Ironfront.Net.Replication/Server/ActorIdPool.cs` | **Edit.** Becomes a sealed wrapper delegating to `QuarantinedIdPool`. Every public member — `FirstId`, `FreeCount`, `QuarantinedCount`, `InUseCount`, `Capacity`, `IsFullyReleased`, `TryAcquire`, `Release`, `IsInUse`, `ReleaseExpired`, `ResetAll`, and the `(capacity = MAX_ACTORS, quarantineSeconds = 5f)` constructor — keeps its exact signature and its doc comments. | engine-free |
| `Ironfront.Net.Replication/Server/VehicleIdPool.cs` | **New.** Same wrapper, defaulting to `capacity: ProtocolConstants.MAX_VEHICLES`, `quarantineSeconds: 5f`. Its doc comment states the vehicle-specific version of the hazard: a snapshot for a burnt-out tank arriving after its id was reissued to a freshly spawned jeep applies the wreck's pose and health to the live vehicle, with no error anywhere. | engine-free |

`ProtocolConstants.MAX_VEHICLES` is a **V3 deliverable** (it sizes the id space and the vehicle
snapshot's count byte). V4 assumes `32` — design § 5 measures the budget at 12 visible and § 8
criterion 9 loads 12; 32 leaves the same kind of headroom `MAX_ACTORS = 64` leaves over 48.

**Verify.** `dotnet test Ironfront.Net.Replication.Tests --filter ActorIdPool` — the existing
phase-03 suite passes **unedited**, which is the whole point of the wrapper. New
`VehicleIdPoolTests` cover the 5-second boundary (`t=4.999` still quarantined, `t=5.0` free),
FIFO rotation, double-release returning `false`, and `ResetAll` bypassing quarantine.

---

### Task 2 — Vehicle registry and per-tick capture (2 days)

The registry is the SSOT for "which vehicles exist and what is authoritative about them". Capture
turns that into the wire buffer V3 defined.

| File | Change | Side |
|---|---|---|
| `Ironfront.Net.Replication/Vehicles/VehicleState.cs` | **New.** `struct VehicleState` — the authoritative record: `VehicleId`, `SpawnerId`, `VehicleKind` (`Car`/`Tank`/`Boat`/`Helicopter`), `Health`, `MaxHealth`, `Burning`, `Dead`, `BurnEndsAtTick`, `OwnerTeam`, and the `ushort[] SeatOccupants` / `ushort[] SeatClaims` arrays. Pre-sized at construction; nothing here allocates per tick. | engine-free |
| `Ironfront.Net.Replication/Vehicles/VehicleWorldSnapshot.cs` | **New.** The vehicle analogue of `WorldSnapshot`: a pre-sized `VehicleSnapshotEntry[]` sized `MAX_VEHICLES`, `VehicleCount`, `ServerTick`, `Clear()`, `IndexOf(ushort)`. Same shape as `WorldSnapshot` so `VehicleDeltaEncoder` (V3) reads it the way `DeltaEncoder` reads its actor counterpart. | engine-free |
| `Ironfront.Net.Replication/Vehicles/VehicleRegistry.cs` | **New.** Add / remove / lookup by id, backed by an array indexed by id (not a dictionary — `ServerRespawnGate` set the precedent in phase-05 Task 1). Owns the `VehicleIdPool`. Exposes `CaptureInto(VehicleWorldSnapshot, uint tick)` taking an `IVehiclePoseSource` per vehicle. | engine-free |
| `Ironfront.Net.Replication/Vehicles/IVehiclePoseSource.cs` | **New.** `void ReadPose(out Vec3 position, out Quat rotation, out Vec3 linearVelocity, out Vec3 angularVelocity)` plus `float TurretYaw`, `float TurretPitch`, `float SubtypeTail`. **This interface is the entire PhysX seam.** Everything above it is testable with a fake; the only real implementation reads a `Rigidbody`. | engine-free |
| `Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerVehicle.cs` | **New.** `MonoBehaviour` implementing `IVehiclePoseSource` over `Vehicle.rigidbody` per V4-D14. Registers with `ServerVehicleRegistry` in `OnEnable`, unregisters in `OnDisable` (releasing the id, which starts the quarantine). Holds the assigned `VehicleId`. | Unity |
| `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerVehicleRegistry.cs` | **New.** The Unity-side companion, mirroring `ServerActorRegistry`'s shape (`Instance`, `Register`, `Unregister`, `TryFind`, `CaptureInto`, `UseIdPool`) and holding the `NetServerVehicle` list the engine-free registry cannot. | Unity |

Quantisation is V3's (`Quantize.PackPos` for position, smallest-three for rotation, `i16 × 3` for
linear velocity because `i8` saturates at 64 m/s and a helicopter exceeds it — design § 3.1,
`Quantize.cs:37`). V4 calls it; V4 does not define it.

**Verify.** `VehicleCaptureTests` with a fake `IVehiclePoseSource`: a captured entry round-trips
through V3's encoder and decoder to within the documented quantisation error; `VehicleCount`
tracks registration; capture of 32 vehicles allocates zero bytes (measured with
`GC.GetAllocatedBytesForCurrentThread` around the call, the same technique the phase-04
measurement tests use).

---

### Task 3 — Interest management for vehicles (2.5 days) — *the invasive one*

`InterestManager.Evaluate` is typed on `ActorSnapshotEntry` and is called from
`BuildViewCore`'s pass 1 (`InterestManager.cs:383`), which is the hottest loop in the server.
This task is scored highest in § 5 because it edits a class four merged phases depend on.

| File | Change | Side |
|---|---|---|
| `Ironfront.Net.Replication/Interest/InterestSubject.cs` | **New.** `readonly struct InterestSubject` per V4-D4, with `From(in ActorSnapshotEntry)` and `From(in VehicleSnapshotEntry)` static factories, both `[MethodImpl(AggressiveInlining)]`. | engine-free |
| `Ironfront.Net.Replication/Interest/InterestManager.cs` | **Edit, additive.** `Evaluate(in InterestSubject viewer, in InterestSubject target)` becomes the real implementation; the existing `Evaluate(in ActorSnapshotEntry, in ActorSnapshotEntry)` becomes a two-line forwarder with **its signature unchanged**. `IsInViewCone` and the `SendEveryN` table are made accessible to the vehicle tracker (internal, not public — the vehicle tracker ships in the same assembly). No other member changes. | engine-free |
| `Ironfront.Net.Replication/Interest/VehicleInterestTracker.cs` | **New.** Per V4-D3: its own `Dictionary<uint,uint>` pair table, `IsDue` / `RecordSend` / `ShouldSend` / `Forget` / `Reset`, its own `EntriesShed`, `EntriesCulled`, `EntriesHeld`, `LastViewShedCount`, and `BuildView(viewerActorId, VehicleWorldSnapshot world, uint snapshotIndex, VehicleWorldSnapshot destination, int byteBudget)`. Band constants and `SendEveryN` are **read from `InterestManager`**, never redeclared. | engine-free |

Three properties the tracker must have, each of them a thing the actor path already learned the
hard way:

- **Shed lowest band first, rotating within a band by a per-client cursor** (phase-05 D6). A
  vehicle that loses the byte race must not also lose its rate slot — that is why
  `InterestManager` split `IsDue` out of `ShouldSend` (`:249-259`), and the vehicle tracker
  copies that split rather than the convenience method.
- **Two byte budgets, one datagram.** Actor and vehicle snapshots are separate messages that share
  `ServerPayloadWriter.MaxSnapshotBodySize`. The vehicle budget is what remains after the actor
  snapshot has been built for that viewer, so the actor stream is never starved by vehicles.
  Projection is pessimistic for the same reason `MaxEntrySize` is (`InterestManager.cs:90-99`):
  the full 30-byte entry, not the expected delta.
- **`Forget(vehicleId)` on despawn.** The trap-2 leak `InterestManager` documents at `:106-107` is
  identical here, one dictionary over.

**Verify.** `VehicleInterestTests`: band edges at 59.9/60.1, 149.9/150.1, 499.9/500.1 m;
`EntriesShed` is zero at 12 vehicles + 48 actors (design § 8 criterion 9 makes non-zero a
**failure**, matching `InterestManager.cs:149-155`); a vehicle and an actor **with the same
numeric id** do not share a rate slot (the V4-D3 collision, which nothing else would catch); the
pair table returns to zero entries after every vehicle despawns.
`InterestManagerTests` from phases 02/03/05 pass **unedited** — the gate on the refactor.

---

### Task 4 — Seat arbitration (2 days)

Per V4-D6 through V4-D9.

| File | Change | Side |
|---|---|---|
| `Ironfront.Net.Replication/Vehicles/SeatArbiter.cs` | **New.** Pure state machine. `SeatDecision Decide(in SeatRequest request, in VehicleState vehicle, float nowSeconds)`. Refusal reasons: `NoSuchVehicle`, `NoSuchSeat`, `SeatOccupied`, `VehicleDead`, `WrongTeam`, `TooFar`, `LockedOut`, `AlreadySeated`. Requests within one tick are drained in arrival order, ties by ascending connection id. No allocation; the pending queue is a pre-sized ring. | engine-free |
| `Ironfront.Net.Replication/Vehicles/SeatRequest.cs` / `SeatDecision.cs` | **New.** `readonly struct`s. `SeatRequest { ushort ConnectionId; ushort ActorId; ushort VehicleId; byte SeatIndex; bool Enter; uint ClientTick; }`. `SeatDecision { SeatChangeResult Result; ushort ActorId; ushort VehicleId; byte SeatIndex; }`. | engine-free |
| `Ironfront.Net.Replication/Server/ServerMessageRouter.cs` | **Edit.** `case ClientMessageType.SeatRequest` (0x26) — today it falls through to `UnknownMessages++` (design § 5). Routes to an `ISeatRequestHandler`, mirroring the `ISpawnRequestHandler` pattern added in phase-05 Task 3. A malformed request increments `MalformedMessages` and is dropped; it never throws (§ 3.2). | engine-free |
| `Ironfront.Net.Replication/Server/ISeatRequestHandler.cs` | **New.** One method, so the Unity bridge can be held as a field rather than a capturing lambda — the same reasoning as `IAcceptedFrameObserver` in phase-05 Task 2. | engine-free |
| `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerSeatBridge.cs` | **New.** Implements `ISeatRequestHandler`. Applies an accepted decision by calling `Actor.EnterSeat` / `Actor.LeaveSeat` — **and checks `EnterSeat`'s `bool`** (V4-D7). A `false` from a check the arbiter could not make (a Unity-side condition) is downgraded to `SeatChangeResult.Refused` rather than left as a silent divergence between the arbiter's record and the scene. | Unity |
| `Ironfront.Net.Replication/Server/ServerEventWriter.cs` | **Edit.** `WriteSeatChange(...)` emitting `S_SEAT_CHANGE` (0x50) on channel 2. An accept is broadcast (everyone must see who is in the vehicle); a refusal is addressed **to the requester alone**. | engine-free |

The occupancy that clients read comes from `SnapshotField.SeatInfo` on the **actor** entry (design
D2), which V3 finishes. `S_SEAT_CHANGE` is the *transition* and the *refusal*; the snapshot is the
*state*. There is deliberately one source of truth for "who is in what seat", and it is the actor
entry.

**Verify.** `SeatArbiterTests`: two clients requesting `seats[0]` of the same vehicle in one tick
produce exactly one accept and one `SeatOccupied` refusal, and the outcome is identical when the
arrival order is reversed only in connection id (determinism); the 1-second re-entry lockout is
enforced on the network path (the hole V4-D8 closes — `SwitchSeat` bypasses `CanEnterSeat`);
a request naming a dead vehicle is refused; a refusal is addressed to one connection and an accept
to all. `SeatIndexConventionTests` pins `seats[0] == driver` (V4-D6) so a prefab reorder fails a
test instead of failing silently in a match.

---

### Task 5 — The vehicle damage sink, burning and death (1.5 days)

Per V4-D11 and V4-D12. **Hard-blocked on V0** delivering `Vehicle.health`'s setter and the
attacker id on `Damage` — both are signature changes, not transport problems (design § 3.4).

| File | Change | Side |
|---|---|---|
| `Ironfront.Net.Replication/Vehicles/IVehicleDamageSink.cs` | **New.** `VehicleDamageOutcome ApplyDamage(ushort vehicleId, float amount, ushort attackerId)`; `VehicleDamageOutcome { float RemainingHealth; bool StartedBurning; bool Died; }`. Deliberately the same shape as phase-05's `IActorDamageSink`, so the two damage paths read alike. | engine-free |
| `Ironfront.Net.Replication/Vehicles/VehicleBurnClock.cs` | **New.** The two-stage death machine: `health <= 0` → `Burning`, `BurnEndsAtTick = now + burnTime`; `Tick(uint serverTick)` returns the ids that died this tick. Tick-counted, not `Time.deltaTime` — `Vehicle.cs:175` uses `Time.deltaTime` *inside* `FixedUpdate`, correct today only by accident and silently wrong the moment the burn tick moves (design § 3.3). | engine-free |
| `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerVehicleDamageSink.cs` | **New.** Implements `IVehicleDamageSink` over `ServerVehicleRegistry`. **The only place vehicle health is written on the server**, mirroring phase-05 D9: one number, not a mirror that can drift. | Unity |
| `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs` | **Edit** (the client track file — PR + review round, design § 7). A role guard at the top of `Damage`, the same shape as phase-05 Task 6's guard on `Actor.Damage`: **server** routes the health change through the sink then lets the rest run; **client** runs the cosmetics (`damageParticles`, `impactAudio`, `heavyDamageAudio`) but skips the `health -=` and the `StartBurning()` / `Die()` — those arrive from the snapshot flag and `S_VEHICLE_DESPAWN`; **offline** is an early no-op and behaves exactly as today. | Unity |
| `Ironfront.Net.Replication/Server/ServerEventWriter.cs` | **Edit.** `WriteVehicleSpawn` / `WriteVehicleDespawn` (0x4D / 0x4E) with the `Destroyed` / `Wrecked` / `Cleanup` reason per V4-D12. | engine-free |

**Verify.** `VehicleDamageTests`: damage reaching zero health sets `burning` and does **not** emit
death; death is emitted exactly `burnTime` later and exactly once; `crashSkipsBurn` short-circuits
to immediate death; a tank's death emits `Wrecked`, not `Destroyed` (V4-D12), and no snapshot entry
for that id is produced afterward. `VehicleOfflineGuardTests` asserts the `Damage` guard is a
literal no-op at `NetRole.Offline`, the same criterion phase-05 Task 6 is graded on.

---

### Task 6 — Bot seat claims become identity-bearing (1 day)

Per V4-D10.

| File | Change | Side |
|---|---|---|
| `Ironfront.Net.Replication/Vehicles/BotSeatClaims.cs` | **New.** `TryClaim(ushort vehicleId, byte seatIndex, ushort botActorId, float now)`, `Release(ushort botActorId)`, `ReleaseExpired(float now)`, `int ClaimCount(ushort vehicleId)`, `bool HasUnclaimedSeats(ushort vehicleId)`. Backed by arrays indexed by vehicle id. The 10-second drain becomes a **per-claim** expiry rather than a whole-vehicle timer, which is what makes it reconcilable. | engine-free |
| `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerVehicleRegistry.cs` | **Edit.** Subscribe to `ServerActorRegistry.ActorUnregistered` (the event already exists, `ServerActorRegistry.cs:92`) and release that bot's claims. | Unity |
| `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs` | **Edit** (the client track file, same PR as Task 5). `seatsClaimedByBots` becomes computed at server role from `BotSeatClaims`; `ClaimSeat` / `DropSeatClaim` / `HasUnclaimedSeats` route to it at server role and are unchanged offline. No stored duplicate — `code-conventions.md` § "No Derived Fields". | Unity |

Not replicated (V4-D10): this is AI bookkeeping and no client consumes it.

**Verify.** `BotSeatClaimTests`: two bots claiming and one dying leaves the count at exactly one
(the bug the `int` counter cannot express); a claim expires per-claim, not per-vehicle;
`HasUnclaimedSeats` agrees with the seat array after an arbitrary interleave of claims, releases
and expiries.

---

### Task 7 — Unity wiring into the tick loop (2 days)

The replication track-owned files only (`Net/Server/**` is C's per conventions § 7), except the two
`Assembly-CSharp` edits already booked in Tasks 5 and 6.

```
ServerTickLoop.RunInputStage
  └─ _router.Route(...)  → ClientMessageType.SeatRequest → _seatBridge.OnSeatRequest(...)
                                → SeatArbiter.Decide → ServerSeatBridge applies → WriteSeatChange

ServerTickLoop.RunSnapshotStage
  ├─ _vehicles.CaptureInto(_vehicleWorld, tick)          ← reads Rigidbody (V4-D14)
  ├─ _burnClock.Tick(tick) → WriteVehicleDespawn(...)    ← V4-D11 / D12
  └─ per viewer:
       ├─ _interest.BuildView(session, _world, ..., byteBudget)          ← unchanged
       └─ _vehicleInterest.BuildView(session.ActorId, _vehicleWorld, ...,
                                     byteBudget: remaining after the actor snapshot)
             → VehicleDeltaEncoder (V3) → S_VEHICLE_SNAPSHOT on channel 1
```

| File | Change |
|---|---|
| `Net/Server/ServerTickLoop.cs` | Fields `_vehicles`, `_vehicleWorld`, `_vehicleView`, `_vehicleInterest`, `_burnClock`, `_seatBridge`, `_vehicleDamageSink`, all constructed once. `BindMatch` also binds the `VehicleIdPool`; `ResetForNewMatch` resets it and the tracker; `AuditState` gains the vehicle pool and the tracker's pair count so `AssertCleanState()` covers them (design § 8 criterion 13). |
| `Net/Server/ServerSnapshotStage.cs` | Unchanged — it already just calls `RunSnapshotStage`. |
| `Net/Server/NetServerBootstrap.cs` | No change. It already deliberately leaves `Time.fixedDeltaTime` alone (`:135`) so Unity physics runs at its own rate and the netcode owns a separate 30 Hz accumulator — which is exactly why server-side vehicle simulation costs nothing here. |

**Constraint.** Everything on this path is a field constructed once. Nothing allocates per tick,
nothing uses `System.Linq`, nothing uses `foreach` (conventions § 3.2).

**Verify.** The solution compiles (`dotnet build`, warnings-as-errors). A two-vehicle scripted
scenario through `ServerTickLoop` produces a well-formed `S_VEHICLE_SNAPSHOT` and a
`S_SEAT_CHANGE`, asserted at the byte level. Allocation posture is unchanged by inspection; the
Profiler run that proves it is **The client track's**, out of scope here — same boundary phase-05 Task 3 drew.

---

### Task 8 — Tests (2 days, written alongside Tasks 1–7)

All engine-free, all under `dotnet test`, none needing the Editor.

| Test | Asserts |
|---|---|
| `AVehicleIdIsNotReissuedInsideTheQuarantine` | `t=4.999` still held, `t=5.0` free (V4-D1) |
| `TheActorIdPoolSuiteIsUnchangedByTheExtraction` | the phase-03 file is not edited and is green (V4-D2) |
| `AVehicleAndAnActorWithTheSameIdDoNotShareARateSlot` | the V4-D3 collision — nothing else would catch it |
| `TheActorInterestSuiteIsUnchangedByTheSubjectRefactor` | V4-D4's forwarder preserves behaviour exactly |
| `VehicleBandEdgesMatchTheActorBands` | 60 / 150 / 500 m, read from `InterestManager`, not redeclared |
| `NothingInsideFiveHundredMetresIsCulled` | the design's stated invariant |
| `TwelveVehiclesAndFortyEightActorsShedNothing` | design § 8 criterion 9 — non-zero shed is a **failure** |
| `AShedVehicleKeepsItsRateSlot` | `IsDue` / `RecordSend` split; no starvation |
| `TheVehiclePairTableEmptiesOnDespawn` | the trap-2 leak, one dictionary over |
| `TwoClientsRacingForTheDriverSeatProduceOneAcceptAndOneRefusal` | V4-D9, and the same result under reversed arrival |
| `ARefusalReachesOnlyTheRequester` | V4-D7 — the path that does not exist today |
| `TheReentryLockoutHoldsOnTheNetworkPath` | the `SwitchSeat` hole V4-D8 closes |
| `SeatZeroIsTheDriver` | pins the array-index convention (V4-D6) |
| `ZeroHealthStartsBurningAndDoesNotKill` | V4-D11, against `Vehicle.cs:270` |
| `DeathArrivesExactlyBurnTimeLaterAndExactlyOnce` | V4-D11, tick-counted |
| `ATankDeathEmitsWreckedAndStopsSnapshotting` | V4-D12 |
| `OutOfRangeVehicleInputIsClampedAtDecode` | design § 8 criterion 3 (V4-D13) |
| `TwoBotsClaimingAndOneDyingLeavesOneClaim` | the counter bug V4-D10 fixes |
| `CaptureOfThirtyTwoVehiclesAllocatesNothing` | conventions § 3.2, measured not asserted |
| `AssertCleanStateCoversTheVehiclePool` | design § 8 criterion 13 |

---

## 4. Acceptance criteria

1. Every live vehicle has a unique non-zero `ushort` id, and an id released this tick cannot be
   reissued for 5 seconds.
2. `ActorIdPool`'s and `InterestManager`'s existing test suites pass **unedited** — the two
   refactors in Tasks 1 and 3 are behaviour-preserving.
3. A vehicle and an actor sharing a numeric id do not interfere in the AOI rate table.
4. Vehicles classify into Near / Mid / Far at the same radii as actors, read from one definition;
   nothing inside 500 m is culled.
5. At 16 players + 32 bots + 12 vehicles, `VehicleInterestTracker.EntriesShed` is **zero**.
   Non-zero is a failure, not a pass (design § 8 criterion 9).
6. A seat request is answered exactly once, to the right recipients: an accept to everyone, a
   refusal to the requester alone.
7. Two clients racing for one seat produce exactly one occupant, deterministically.
8. Vehicle health at zero sets `burning` and does not kill; death arrives from the burn countdown
   and is announced by `S_VEHICLE_DESPAWN` with a reason.
9. A tank's death is replicated as an event; no vehicle snapshot entry for that id follows it.
10. Out-of-range vehicle input is clamped at decode and gains the sender no advantage.
11. `seatsClaimedByBots` reflects the actual set of claiming bots after any interleave of claims,
    deaths and expiries.
12. Offline single-player vehicle behaviour is unchanged by the `Vehicle.cs` guards.
13. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-tick allocation
    in any new logic file.
14. `AssertCleanState()` passes across five back-to-back matches including the vehicle id pool and
    the vehicle interest pair table.

### What genuinely needs the Editor — handed to the client track

These cannot run in CI, because the Unity project has no `.asmdef` and `dotnet test` cannot reach
`UnityEngine` (see the `IInputSource` header comment for why the pure files are `<Compile Include>`
linked instead).

| Check | Why it needs Unity |
|---|---|
| Headless server survives vehicle spawn → damage → burn → death → cleanup with **zero NREs** (design § 8 criterion 11) | Needs a batch-mode run against real prefabs. V4 supplies the scripted scenario; V0 supplied the guards. |
| `NetServerVehicle` attached to every vehicle prefab, and its `.meta` | Prefab authoring |
| `Rigidbody` capture fidelity vs. what the client renders | Needs real PhysX |
| Profiler: the vehicle stage adds no per-tick allocation | Unity Profiler |
| Two-client Editor test with one player driving | Editor |

---

## 5. Which side each piece lands on

The convention is engine-free logic in `Ironfront.Net.Replication` with Unity holding a thin seam
(design § 3.2). Vehicles are the case where that cannot be absolute, so the boundary is stated
explicitly rather than assumed.

| Piece | Side | Why |
|---|---|---|
| `QuarantinedIdPool`, `ActorIdPool`, `VehicleIdPool` | engine-free | Pure bookkeeping over an injected clock |
| `VehicleState`, `VehicleWorldSnapshot`, `VehicleRegistry` | engine-free | Records and buffers; no engine type appears |
| `IVehiclePoseSource` | engine-free (interface) | **The PhysX seam.** Declared here, implemented once in Unity |
| `InterestSubject`, `VehicleInterestTracker`, `InterestManager` | engine-free | Arithmetic over quantised positions |
| `SeatArbiter`, `SeatRequest`, `SeatDecision` | engine-free | A decision function; fully testable |
| `VehicleBurnClock`, `IVehicleDamageSink` | engine-free | A tick-counted state machine |
| `ServerMessageRouter`, `ServerEventWriter` edits | engine-free | Already are |
| `NetServerVehicle` | **Unity** | Reads `Rigidbody.position/rotation/linearVelocity/angularVelocity`. There is no porting PhysX (design § 3.2) |
| `ServerVehicleRegistry` | **Unity** | Holds `MonoBehaviour` references and subscribes to Unity lifecycle |
| `ServerSeatBridge` | **Unity** | Calls `Actor.EnterSeat` / `LeaveSeat`, which are `MonoBehaviour` methods that move a `Rigidbody` |
| `ServerVehicleDamageSink` | **Unity** | Writes `Vehicle.health` and touches `Vehicle` state |
| `Vehicle.cs` role guards | **Unity** (the client track file) | The damage choke point lives there |

Every decision is on the engine-free side. Unity only reads a body and calls a method.

---

## 6. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Two clients end up in `seats[0]` of the same vehicle — the seat race | 3 | 5 | **15** | V4-D9: one arbiter, one mutation point, inside the tick, deterministic ordering. The race test (N concurrent requests, both arrival orders) is a **precondition** of merging Task 4, not a follow-up. |
| The `InterestSubject` refactor changes actor AOI behaviour — four merged phases depend on `Evaluate` | 3 | 4 | **12** | The actor overload's signature is preserved byte-identically and becomes a forwarder; the gate is that `InterestManagerTests` from phases 02/03/05 pass **unedited**. Reviewed as a diff on one method, not a rewrite. |
| Bandwidth exceeds the 5 KB/s budget once vehicles stream (design § 9) | 3 | 4 | **12** | Criterion 5 grades it rather than assuming it. Fallbacks in the design's stated priority: drop angular velocity at Mid/Far, widen Far, cut the vehicle stream to 10 Hz. All three are `VehicleInterestTracker` config, not re-architecture. |
| Vehicle/actor id collision in a shared rate table is introduced later by someone "simplifying" the two trackers into one | 2 | 5 | 10 | V4-D3 is stated as a decision **and** pinned by a named test, so a merge that reunifies them goes red rather than silently starving Far vehicles. |
| V0 does not land `Vehicle.health`'s setter or the attacker id on `Damage` | 2 | 4 | 8 | Task 5 is hard-blocked and says so. Tasks 1–4, 6 and 7 do not depend on it and merge independently. |
| Extracting `QuarantinedIdPool` regresses a frozen phase-03 class | 2 | 4 | 8 | Public surface preserved member for member; the unedited phase-03 suite is the gate (criterion 2). |
| `ProtocolConstants.MAX_VEHICLES` is not pinned by V3, or is pinned lower than 32 | 3 | 3 | 9 | V4 consumes the constant and never redeclares it; only the pool's default capacity and the world buffer's size read it, so a different value is a one-line change with no code impact. |
| The client track's review round on the two `Vehicle.cs` edits slips the phase | 3 | 2 | 6 | Tasks 5 and 6 batch their `Vehicle.cs` edits into **one** PR, and everything else merges without it — the same severability phase-05 D8 used. |
| Seat change races `Actor.ReactivateCollisionsWith`'s 0.5 s coroutine | 3 | 3 | 9 | V0 replaces the coroutine with a tick-counted timer; V4-D9 forbids mutating seat state outside the tick. If V0 has not landed it, Task 4 is blocked, not worked around. |
| `Vehicle.cs` conflicts with the client track's branch | 3 | 3 | 9 | One PR, one file, announced before it opens — the design § 9 mitigation applied one file over from `Actor.cs`. |

One risk reaches 15. Its mitigation (the deterministic-race test) is a precondition of merging
Task 4, in the same sense phase-05 made the offline no-op test a precondition of Task 6.

---

## 7. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — `VehicleIdPool` extraction | S (0.5d) | No dependencies. Start here. |
| 2 — Registry + capture | M (2d) | Needs Task 1. |
| 3 — Interest for vehicles | L (2.5d) | Needs Task 2's `VehicleSnapshotEntry` shape. **The invasive one — review it as a diff.** |
| 4 — Seat arbitration | M (2d) | Independent of 2 and 3; needs V0's tick-counted collision timer. |
| 5 — Damage sink, burn clock, death | M (1.5d) | **Hard-blocked on V0.** Independent of 3 and 4. |
| 6 — Bot seat claims | S (1d) | Needs Task 2. Batches its `Vehicle.cs` edit with Task 5's. |
| 7 — Unity wiring | M (2d) | Needs 2, 3, 4, 5. |
| 8 — Tests | M (2d) | Written alongside 1–7, not after. |
| **Total** | **~2 weeks** | Critical path: 1 → 2 → 3 → 7. Tasks 4, 5 and 6 run in parallel off it. |

---

## 8. Handoff

**To V5.** The server is vehicle-authoritative: ids are stable and quarantined, `S_VEHICLE_SNAPSHOT`
carries pose, velocity, health and flags at the correct band rate, `S_SEAT_CHANGE` says who is
driving, and `S_VEHICLE_DESPAWN` says when a vehicle stops existing. V5 consumes all four and adds
nothing to the wire. Note for V5: the subtype tail (`rotorSpeed` / `steerAngle`) is captured and
sent from V4 onward — V5 needs it because remote vehicles are not locally simulated and cannot
derive it.

**To V6.** `IVehiclePoseSource` already declares `TurretYaw` / `TurretPitch` and V4 captures and
sends them, but nothing writes them authoritatively yet — `TankTurret` and `MountedTurret` read
`Input.GetAxis` and `OptionsUi.GetOptions()` directly inside `Update` and there is no abstract
`ActorController` member for turret aim (design § 3.6). V6 owns building that seam. The wire fields
already exist, so V6 needs no protocol change.

**To the client track.** One PR against `Assembly-CSharp/Vehicle.cs` carrying both the `Damage` role guard
(Task 5) and the `seatsClaimedByBots` derivation (Task 6), with the offline no-op test attached.
Plus the five Editor-only checks tabulated in § 4.

**Still outside V4.** `VehicleSpawner`'s match lifecycle and the `GameManager.instance.noVehicles`
dereference at `VehicleSpawner.cs:49` belong to V8; the headless NRE guards themselves belong to V0.

---

## 9. Departures from this plan, as built

This plan was written before V3 and V8 task 6 merged. Both shipped pieces V4 had reserved for
itself, and where the merged code and this document disagree the merged code won — a plan is
intent, and re-deriving a decision that is already on `develop` is how two answers to one question
get shipped. Each departure is recorded here rather than left as a silent difference between § 3
and the diff.

| # | Plan said | As built | Why |
|---|---|---|---|
| 1 | Task 1: extract `QuarantinedIdPool`; `ActorIdPool` and `VehicleIdPool` become thin wrappers (V4-D2). | **Skipped.** Both pools stay as they are. | V8 task 6 had already shipped `VehicleIdPool` standalone, and it quarantines in **ticks** (`VEHICLE_ID_QUARANTINE_TICKS = 150`) where `ActorIdPool` quarantines in wall-clock **seconds**. Their surfaces had also diverged (`ReleaseAll` / `ReturnUnused` vs `ResetAll` / `ReleaseExpired`). Unifying them now means reconciling two clocks behind one mechanism and putting a refactor under `ActorIdPool`, whose **unedited** suite is acceptance criterion 2 — for zero behaviour gain. Task 1's budget went to tasks 2–8. |
| 2 | `VehicleRegistry` owns the `VehicleIdPool`. | The registry owns **no** pool; `ServerVehicleLifecycleSink` (V8) keeps allocating, and a vehicle is registered *with* the id it was already given. | Two allocators over one id space is the duplicate SSOT `development-principles.md` forbids, and only the thing that puts `S_VEHICLE_SPAWN` on the wire can honour a quarantine relative to what was actually announced. |
| 3 | Task 3: the vehicle byte budget is what remains **after** the actor snapshot is built. | Reversed: the vehicle body is written **first**, and the actor body takes the remainder. | protocol-spec.md § 4.10 "Co-residency" fixed this at the v3 freeze — the vehicle body is bounded (16 × 30 + 9 = 489 B) and the actor body is elastic and already sheds, so sizing the elastic one against what the bounded one consumed is exact. `ServerPayloadWriter.ActorBodyBudget` implements it. |
| 4 | `SeatArbiter` refusal reasons include `WrongTeam` and `LockedOut`. | `WrongTeam` **dropped**; `LockedOut` **added to the wire** as `SeatChangeResult.RejectedLockedOut = 7`. | `Actor.CanEnterSeat()` is `!IsSeated() && cannotEnterVehicleAction.TrueDone()` — there is no team check anywhere in the shipped seat path, so `WrongTeam` would be a code nothing can produce. `LockedOut` is real and had no wire code; appending a value moves no byte (`S_SEAT_CHANGE` stays 6 B, `result` stays `u8`), so `PROTOCOL_VERSION` is unchanged. It is the only refusal whose remedy is *ask again shortly*, and mapping it onto `RejectedAlreadySeated` would be a lie whenever the actor is on foot. |
| 5 | `S_VEHICLE_DESPAWN` carries `Destroyed` / `Wrecked` / `Cleanup` (V4-D12). | The frozen v3 pair, `Destroyed` / `WorldReset`. A burnt-out vehicle despawns as `Destroyed`. | V4 consumes the wire rather than redefining it, and a wreck *is* destroyed by damage. No client behaviour currently turns on the distinction, so splitting it would be a wire change for nothing. |
| 6 | `NetServerVehicle` is a `MonoBehaviour` registering in `OnEnable`. | A plain class; registration is driven from `NetVehicleLifecycle.ReportSpawned`. | § 4 of this plan hands "`NetServerVehicle` attached to every vehicle prefab, and its `.meta`" to the client track as prefab authoring. A component form would therefore ship a registry that stays empty until fourteen prefabs on two maps are re-saved, with nothing reporting that it is empty. |
| 7 | `VehicleState` carries `ushort[] SeatOccupants` and `SeatClaims`. | Scalars only. Occupancy lives in `VehicleRegistry`'s record; claims live in `BotSeatClaims`. | An array field on a struct is a shared reference that survives every copy, so two "copies" of a vehicle's state would silently share one seat table. Claims on the struct would also be a second copy of what `BotSeatClaims` owns. |
| 8 | Tasks 5 and 6 batch into **one** `Assembly-CSharp` PR, one file (`Vehicle.cs`). | **Five** `Assembly-CSharp` files: `Vehicle.cs`, `Car.cs`, `Helicopter.cs`, `Squad.cs`, `VehicleSpawner.cs`. | Each follows from a requirement the plan itself set. V4-D10 needs claims to *name a bot*, and the identity is only available at `Squad`'s call sites (`ClaimSeat()` takes no argument). § 8 promises V5 the subtype tail, and `steerAngle` / `rotorSpeed` are `private` on `Car` / `Helicopter`. And registration needs the vehicle's `GameObject`, which only `VehicleSpawner.AnnounceSpawn` holds beside the id. All five edits are additive; the no-argument `ClaimSeat()` / `DropSeatClaim()` remain and are the offline path. |
| 9 | Interest classification reads id, position, team, yaw (V4-D4). | Plus an `InterestSpace` discriminator. | `Evaluate` short-circuits `viewer.Id == target.Id` to Near. Once one method sees both kinds, actor 7 looking at vehicle 7 matches that test and the vehicle is pinned to 20 Hz from anywhere on the map. V4-D3 names this hazard for the rate *table*; the discriminator puts the same fact where the *comparison* is. |

**Two things V4 captures but does not yet populate**, stated here so they are not mistaken for
working: `TurretYaw` / `TurretPitch` are 0 (V6 owns the aim seam — design § 3.6), and the
`Airborne` flag bit ships clear because `Vehicle` keeps no grounded state — `Car` asks each
`WheelCollider` and a helicopter has no wheels. Both are wire fields that exist and are honest
zeros rather than synthesised guesses. `Car`'s `surfaceFriction` tail byte is 1.0 for the same
reason: friction is per-wheel and averaging four of them into a byte would name nothing.

**This file's own header misquoted its binding convention, and the header is corrected above.**
It summarised `conventions.md` § 3.2 as "no allocation on the hot path, no `System.Linq`, no
`foreach` in logic files". § 3.2 contains no such clauses: it bans allocation inside hot loops and
LINQ **in the hot path**, and says nothing about `foreach` or about a category called "logic
files" — that phrase appears nowhere in the document. The overstatement propagated into a code
comment in `SeatArbiter.cs` before it was caught, and it costs real budget: a reviewer audits
against a rule that does not exist, and either burns the pass proving conformance to nothing or
"fixes" code that was already correct. A `foreach` over a concrete `Dictionary` or array binds a
struct enumerator by pattern and boxes nothing; iterating through an `IEnumerable<T>` interface is
the thing that actually allocates.

## 10. What adversarial review found after the first commit

Three review passes ran against the branch. What they caught is recorded here rather than only in
commit messages, because the pattern matters more than the count: **five of the findings were
places where a comment asserted an invariant the code did not deliver.** That is more expensive
than an ordinary bug — the next reader stops checking.

| Finding | Verdict |
|---|---|
| `Vehicle.Repair` never got the role guard `Damage` did | **Real, and the worst.** The scene's health rose while the authoritative record did not, so the snapshot shipped a stale byte and the next hit subtracted from a stale value — one more shot killed a fully repaired vehicle. Worse, `StopBurning()` cleared the scene flag while `BurnEndsAtTick` stayed armed, so the burn clock despawned a repaired, drivable, possibly occupied vehicle on schedule. Fixed: `IVehicleDamageSink.ApplyRepair` + `VehicleBurnClock.CancelBurn`. |
| Two racing death authorities | **Real.** `Vehicle.FixedUpdate` counted `burnTime` on the wall clock at 60 Hz and called `Die()` independently of `VehicleBurnClock`. V4-D11 says the server owns when a burn ends; it did not. Fixed: both scene-side `Die()` triggers guard on `NetVehicleAuthority.ServerOwnsVehicleDeath`, and the clock kills through `IGameplayVehicleSource.Kill`. |
| A dead vehicle shipped one last snapshot entry | **Real.** Capture ran BEFORE the burn advance and the per-viewer view read that buffer, so criterion 9's second half never held — and the comment four lines above claimed it did. Fixed by moving `AdvanceVehicleBurn` ahead of the capture. The test the plan named for it did not exist and now does. |
| `SnapshotField.SeatInfo` was never populated | **Real, and nobody had noticed.** Design D2 makes the actor entry the single source of truth for occupancy and V3 finished its codec, but no server code ever passed `vehicleId`/`seatIndex` to `SnapshotBuilder.Capture`. Every actor reported as on foot, so `S_SEAT_CHANGE` — which only fires on a client request — was the only carrier. Fixed in `NetServerActor.Capture`, reading the arbiter's record. |
| The idempotent seat accept was broadcast | **Real.** `C_SEAT_REQUEST` has no rate limit, so a client repeating "enter the seat I am already in" multiplied into N-players × request-rate reliable sends. Now addressed, via `SeatDecision.ChangedNothing`. |
| `ReleaseExpiredClaims` ran per vehicle per physics step | **Real.** A global table sweep with a per-instance trigger: 16× redundant at a full map, and never at all on a map with no vehicles. Moved to one call per step in the tick loop. |
| The audit's vehicle args default to null and read as clean | **Real risk.** `Bind` and `BindMatch` are called by different components with no defined order, so the id pool could be null and criterion 14 would grade nothing, silently. The sink is now constructed in the tick loop's constructor, with a loud check behind it. |
| An out-of-range `SeatAction` byte was a silent Enter | **Real.** Every byte except 1 parsed as Enter and counted as well-formed, blinding `MalformedMessages`. Now range-checked. |
| The shed cursor starves a bucket | **NOT REAL — rejected after checking.** The arithmetic is right (the cursor advances by the total and is applied modulo each bucket's length) and the conclusion is wrong: a not-due entry is skipped with `continue` and consumes no budget, so the scan reaches the entries behind it, and rate limiting rotates the window for free. Near is the one band where everything is always due, and it cannot starve because if it sheds, no lower bucket gets budget and the total advance IS Near's own count. A first attempt at "fixing" this added per-bucket cursors; the guard written for it passed with and without them, which is what exposed the error. Reverted, reasoning documented at the `continue`, and the test now pins the mechanism that actually provides the guarantee. |
| One ack cannot serve two independently-rated streams | **Real, not fixed, and not fixable at v3.** When the acked tick carried no vehicle body the encoder writes a full body instead of a delta — ~30 B per entry instead of ~10, over the already rate-limited view. It falls on viewers whose best band is not Near (about half of a Mid viewer's bodies, four in five of a Far-only viewer's). Accepting the ack anyway would be unsound: the server cannot know the client received an older datagram, and a delta against a baseline the client lacks is discarded with no recovery. A real fix needs per-stream ack state on the wire. `ClientSession` now says so instead of claiming the opposite. |

**Every new guard above was watched go RED against its own bug before being taken green.** That is
how the shed-cursor finding was rejected rather than "fixed": its guard was green either way.

**One graded criterion is met by an existing suite rather than a new one.** Criterion 1 (the
5-second id quarantine) is covered by `VehicleLifecycleWireTests.ARetiredIdIsNotReissuedUntil` `ItsQuarantineExpires`
and friends, shipped with V8 task 6. A second suite over the same class would be the duplicate
`development-principles.md` forbids, and it would be the copy that goes stale.
