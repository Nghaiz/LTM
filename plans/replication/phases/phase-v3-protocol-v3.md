# the replication track — Phase V3: Protocol v3.0.0 — the vehicle wire

> Design of record: [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md).
> Read it first. Its § 5 is the integration contract for V4–V7 and is reproduced **verbatim** in
> § 2 below — every opcode, every field, every byte count. Nothing in this phase re-derives it,
> paraphrases it, or improves it.
>
> Binding process: [`../../00-shared/protocol-spec.md`](../../00-shared/protocol-spec.md) § 15 — a
> wire change lands via a PR with **2 approvals**, a changelog row, a `SpecChecker` update, and a
> `PROTOCOL_VERSION` bump when the bytes change. They do.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2 —
> no allocation on the hot path, no `System.Linq`, no `foreach` in logic files. Every message in
> this codebase is a `readonly struct` with a hand-written `Write` + `TryParse` over
> `SpanWriter`/`SpanReader`. There is no codegen and no reflection. This phase adds none.

---

## 1. Objectives

`Ironfront.Net.Protocol` today can describe an infantryman and nothing else. `SnapshotField` is a
`byte` with 8 of 8 bits allocated (`Enums/GameplayEnums.cs:53-79`), quantized velocity is an `i8`
saturating at 64 m/s (`Quantize.cs:37`), and rotation is yaw + pitch with no roll. A vehicle is
not expressible in that entry — not "tight", *not expressible*.

By the end of this phase:

1. `PROTOCOL_VERSION` is **3**, and the seven opcodes of the design of record § 5 exist as codecs
   with hand-written `Write` + `TryParse`, hex-sample conformance tests, and a spec section.
2. `VehicleSnapshotEntry` exists with its own `u16` change mask and its own delta
   encoder/decoder, sized to exactly the 30-byte full entry the contract pins.
3. `Quantize` can pack a full rotation — smallest-three quaternion in 4 bytes — because vehicles
   roll and the actor entry's `u16` yaw + `i8` pitch cannot say so.
4. `SnapshotField.SeatInfo` is **finished** on the actor entry (D2). It is half-built today:
   `DeltaDecoder.ApplyEntry` already applies it (`DeltaDecoder.cs:209-213`), but
   `SnapshotBuilder.Capture` never sets it and `DeltaEncoder.ComputeChangeMask`
   (`DeltaEncoder.cs:180-206`) never diffs it. Turning it on moves `InterestManager.MaxEntrySize`
   (`Interest/InterestManager.cs:98`) from **20 to 23** and therefore silently changes shedding
   behaviour — § 4 Task 6 quantifies it and § 5 grades it.
5. `tools/SpecChecker` gates the new constants and a new vehicle-type registry, so the wire change
   stays a three-file change by design: spec doc ↔ code ↔ asset.
6. All of it graded by tests that run under `dotnet test` with no Unity Editor.

**Not in this phase.** No server capture, no interest banding of vehicles, no client
interpolation, no prediction, no seat arbitration, no projectile flight. Those are V4–V7 and they
consume this contract; they do not negotiate it. This phase ships **bytes and their meaning**.

**Depends on:** V0 (debt + seams). V0 opens `Vehicle.cs` for the health setter and the attacker id;
Task 7 below opens the same file for `networkId`. V0 lands first so the two do not collide — the
design of record § 9 already scores `Actor.cs`/the client track branch conflict at 12 for exactly this reason.

---

## 2. The wire contract — verbatim from the design of record § 5

> Everything in this section is reproduced byte-for-byte from
> [`2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md)
> § 5. It is the integration contract for phases V4–V7. **Do not edit it here.** If it is wrong,
> it is wrong in the design of record, and it changes there first.

### 2.1. Opcodes

| Opcode | Dir | Ch | Purpose |
|---|---|---|---|
| `C_VEHICLE_INPUT = 0x21` | C→S | 3 | 4 axes + turret aim, sent only while seated. Uses the one free client opcode |
| `C_SEAT_REQUEST = 0x26` | C→S | 2 | **Already reserved**, currently falls through to `UnknownMessages++`. Carries `(vehicleId, seatIndex, enter/leave)` |
| `S_VEHICLE_SNAPSHOT = 0x4C` | S→C | 1 | Vehicle entity stream |
| `S_VEHICLE_SPAWN / DESPAWN = 0x4D / 0x4E` | S→C | 2 | Spawner lifecycle, wreck cleanup |
| `S_PROJECTILE_SPAWN = 0x4F` | S→C | 2 | Launch parameters per D5 |
| `S_SEAT_CHANGE = 0x50` | S→C | 2 | Authoritative enter/leave. Required by § 3.5 — rejections currently have no path home |
| `S_EXPLOSION = 0x4A` | S→C | 2 | **Exists.** Needs a caller and a subscriber, nothing more |

Plus: finish `SnapshotField.SeatInfo` on the actor entry (D2), which moves
`InterestManager.MaxEntrySize` from 20 → 23 and therefore changes shedding behaviour — that is why
§ 8 grades it rather than assuming it.

### 2.2. Vehicle entry shape

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

### 2.3. Bandwidth

Measured today: **1.67 KB/s/client** shipped, against a 5–7 KB/s spec target — roughly 3–4× headroom
(`plans/replication/reports/2026-08-13-phase-04-report.md:57-72`).

Vehicles ride the existing interest bands (Near 20 Hz, Mid 10 Hz, Far 4 Hz). A realistic
8-vehicles-visible distribution of 2 Near / 3 Mid / 3 Far gives 82 entries/s; at a ~20 B typical
delta that is **~1.6 KB/s**, for a total near 3.3 KB/s. Inside the target, but close enough that
§ 8 grades it as a criterion rather than assuming it.

`SeatInfo` on actors adds 3 B per *seated* actor and only on change — negligible. Projectiles are
events, not a stream — one ~16 B message per shot.

*(End of verbatim reproduction.)*

---

## 3. Decisions taken (do not re-litigate)

The design of record's D1–D8 stand unchanged. These are the decisions **this phase** adds, all of
them below the level § 5 pins — the contract fixes the entry; the header, the sentinels and the
budget rule are this phase's to settle.

| # | Decision | Why |
|---|---|---|
| **V3-1** | **`VehicleSnapshotHeader` is 9 bytes**: `u32 serverTick` + `u32 baselineTick` + `u8 vehicleCount`. It carries **no** `lastProcessedInputTick`. | The actor header carries one because `PredictionReconciler` replays unacked inputs. D3 says driver prediction is *error-corrected simulation, not input replay* — the client never re-runs a tick, so it has nothing to ack against, and a field nobody reads is 4 B × 20 Hz of nothing. |
| **V3-2** | **Vehicle ids allocate from 1. `vehicleId == 0` means "no vehicle".** `VehicleIdPool` is a separate `u16` space from `ActorIdPool`, with the same 5-second (150-tick) quarantine `protocol-spec.md § 4.3.1` mandates for actor ids. | `SnapshotField.SeatInfo` must be able to say *left the vehicle*, and it is sent on change — including the change to "not seated". A sentinel is the only way to express that in a `u16` field, and 0 is the same convention `weaponId` already uses for none/unknown. The quarantine exists for the same reason it does on actors: tail packets for a destroyed vehicle are in flight for up to one interpolation buffer. |
| **V3-3** | **`MAX_VEHICLES = 16`.** `vehicleCount` is a `u8` like `actorCount`, but the pool caps at 16. | Acceptance criterion 9 grades bandwidth at 12 vehicles. 16 gives the quarantine window room to hold ids while a spawner replaces a wreck, and keeps the worst-case vehicle body at 16 × 30 + 9 = **489 B** — bounded, unlike the actor body. |
| **V3-4** | **The subtype tail is a fixed 2 bytes for every vehicle type**, discriminated by the type the client learned from `S_VEHICLE_SPAWN`. | A variable-width tail would make the stream unparseable by any decoder that missed the spawn — one lost type mapping and every *subsequent* entry in the datagram misaligns. Fixed width means an unknown type costs 2 skipped bytes and nothing else. This is the same property that makes `changeMask` safe. |
| **V3-5** | **The vehicle snapshot is a separate message in the same channel-1 payload batch, written FIRST; the actor snapshot gets the remainder of the budget.** | The vehicle body is bounded (V3-3: ≤ 489 B); the actor body is elastic and already has a shedding mechanism (phase-05 Task 4). Sizing the elastic one against what the bounded one actually consumed is exact. Reserving a slice instead would need unused-reserve-return logic for no gain, and two datagrams would cost a second 16-byte GSP header at 20 Hz (~320 B/s) to solve a problem neither stream has. |
| **V3-6** | **V3 declares the co-residency rule and the constants; V4 implements the budget split.** This phase ships `VehicleSnapshotMessage.MaxBodySize`, the documented rule, and a test that the worst case fits one datagram. | The seam this repo already draws: `SnapshotMessage` (shared protocol) decides how bytes lie; `SnapshotBuilder`/`InterestManager` (the replication track, replication) decide which entities and which fields go in at all — `SnapshotBuilder.cs:11-16` states it. Putting shedding policy in the protocol assembly would break it. |
| **V3-7** | **`S_EXPLOSION` (0x4A) is not touched.** Its 10-byte layout (`Messages/ActorLifecycleMessages.cs:152-200`) is already correct and already round-trip tested. V3 only documents it in the new spec section as part of the world stream. | § 5 says it "needs a caller and a subscriber, nothing more". A caller and a subscriber are V1 and V7. Changing bytes that do not need changing would put a working codec into the 2-approval queue for nothing. |
| **V3-8** | **A vehicle-type id registry (`VehicleIds`) ships in this phase**, with a spec section and a `SpecChecker` gate against a new serialized `Vehicle.networkId`. | `S_VEHICLE_SPAWN` carries a `u8 vehicleType` and the client instantiates a prefab from it. This is `weaponId` again exactly: `protocol-spec.md § 4.8` and `WeaponIds` exist *because* "the mapping existed only inside `_Managers.prefab`, which the server cannot read" (§ 15 changelog, 2.0.1). Shipping the same hole twice, knowing it is a hole, is not a defensible call. |
| **V3-9** | **`C_VEHICLE_INPUT` carries no redundancy and no frame batching**, unlike `C_INPUT`. | `C_INPUT` repeats 3 frames because a lost frame costs a tick of movement *and* a button edge. Vehicle axes are continuous and level-triggered — a lost throttle frame is corrected by the next one 33 ms later. The one genuinely edge-triggered vehicle action (leaving a seat) travels on `C_SEAT_REQUEST`, which is channel 2 and reliable. |

---

## 4. Detailed tasks

### Task 1 — Opcodes, enums and constants (S, 0.5 day)

**Files, `Ironfront.Net.Protocol/`:**

| File | Change |
|---|---|
| `Enums/MessageTypes.cs` | `ClientMessageType.VehicleInput = 0x21`. `SeatRequest = 0x26` already exists — update its doc comment from "(stretch goal)" to the real behaviour. `ServerMessageType`: `VehicleSnapshot = 0x4C`, `VehicleSpawn = 0x4D`, `VehicleDespawn = 0x4E`, `ProjectileSpawn = 0x4F`, `SeatChange = 0x50`. |
| `Enums/VehicleEnums.cs` **(new)** | `VehicleField : ushort` (the change mask, § 2.2), `VehicleStateFlags : byte`, `VehicleKind : byte`, `SeatAction : byte`, `SeatChangeResult : byte`, `VehicleDespawnReason : byte`, `ProjectileKind : byte`. |
| `ProtocolConstants.cs` | `PROTOCOL_VERSION` 2 → **3** (Task 11, last). `MAX_VEHICLES = 16`. `VEHICLE_ID_QUARANTINE_TICKS = 150`. |

`VehicleField` bit allocation — 8 of 16 used, 8 spare, mirroring the § 2.2 row order:

| Bit | Field | Bytes |
|---|---|---|
| 0 | `Position` — i16 × 3 | 6 |
| 1 | `Rotation` — smallest-three quaternion, u32 | 4 |
| 2 | `LinearVelocity` — i16 × 3 | 6 |
| 3 | `AngularVelocity` — i8 × 3 | 3 |
| 4 | `Health` — u8 | 1 |
| 5 | `Flags` — u8 `VehicleStateFlags` | 1 |
| 6 | `Turret` — u16 yaw + i8 pitch | 3 |
| 7 | `Subtype` — fixed 2 B tail (V3-4) | 2 |
| 8–15 | reserved | — |

`VehicleStateFlags`: `Dead = 1<<0`, `Burning = 1<<1`, `InWater = 1<<2`, `Airborne = 1<<3`,
bits 4–7 reserved. Exactly the four the § 2.2 table names; the rest are spare on purpose.

**Constraint.** The mask is a `u16` and the header is `2 + 2 = 4` bytes, so the full entry is
`4 + 6 + 4 + 6 + 3 + 1 + 1 + 3 + 2 = 30` — the § 2.2 total, arrived at by addition rather than by
assertion. A stationary vehicle's delta is **4 bytes**.

**Verify:** `dotnet build` green; Task 8's enum-pinning tests assert every numeric value above.

---

### Task 2 — Smallest-three quaternion in `Quantize` (S, 1 day)

**File:** `Ironfront.Net.Protocol/Quantize.cs` (edit).

```
public static uint  PackQuat(float x, float y, float z, float w);
public static void  UnpackQuat(uint packed, out float x, out float y, out float z, out float w);
```

Layout, 32 bits exactly: `[31:30]` = index of the largest-magnitude component (0=x, 1=y, 2=z,
3=w); `[29:20]`, `[19:10]`, `[9:0]` = the other three in source order, each a 10-bit unsigned
quantization of the range `[-1/√2, +1/√2]`. The largest component is reconstructed as
`sqrt(1 - a² - b² - c²)`, which is why it does not need to be sent.

Three properties the implementation must have, each because getting it wrong is silent:

- **Sign canonicalization.** `q` and `-q` are the same rotation. The largest component is forced
  positive before packing, so the dropped component's reconstructed sign is always `+`. Without
  this, half of all rotations decode mirrored.
- **Clamp under the radical.** Floating-point round-trip can push `1 - a² - b² - c²` fractionally
  negative; `sqrt` of that is `NaN`, and a `NaN` quaternion propagates into a Unity transform as
  a vehicle that vanishes rather than as an exception. Clamp to `0` first.
- **Renormalize on unpack.** 10-bit quantization leaves the result off unit length by up to
  ~0.1%. Unity tolerates it; three frames of interpolated blending against it do not.

`Quantize` must not reference `UnityEngine` (`Quantize.cs:16-19`), so this is plain `System.Math`.
No allocation: both signatures are value-in, value-out.

**Resolution.** 10 bits over `2/√2` is 1.38 × 10⁻³ per step, i.e. **< 0.16° of angular error** —
finer than the 0.5 m/s velocity resolution the same stream already accepts.

**Verify:** `QuaternionPackTests` (Task 8) — round-trip angular error < 0.2° over a deterministic
sweep of 10⁴ rotations covering all four largest-component branches; `PackQuat(q)` ==
`PackQuat(-q)`; unpacked length within 10⁻³ of 1; no `NaN` for any 32-bit input, including the
`0xFFFFFFFF` an attacker sends.

---

### Task 3 — `VehicleSnapshotEntry` and its codec (M, 2 days)

**File (new):** `Ironfront.Net.Protocol/Messages/VehicleSnapshotMessage.cs`.

Three types, following `SnapshotMessage.cs` shape for shape:

| Type | Contents |
|---|---|
| `VehicleSnapshotHeader` | `readonly struct`, `const int Size = 9`. `uint ServerTick`, `uint BaselineTick`, `byte VehicleCount`. `IsFullSnapshot => BaselineTick == 0` (V3-1). |
| `VehicleSnapshotEntry` | **Mutable** `struct` — the parser fills it in place in a caller-owned array, exactly as `ActorSnapshotEntry` does (`SnapshotMessage.cs:38-41`), so a 20 Hz stream produces no garbage. Fields in § 2.2 order: `ushort VehicleId`, `VehicleField ChangeMask`, `short PosX/PosY/PosZ`, `uint Rotation`, `short VelX/VelY/VelZ`, `sbyte AngVelX/AngVelY/AngVelZ`, `byte Health`, `VehicleStateFlags Flags`, `ushort TurretYaw`, `sbyte TurretPitch`, `byte SubtypeA`, `byte SubtypeB`. `bool Has(VehicleField)`. |
| `VehicleSnapshotMessage` | `static`. `const int EntryHeaderSize = 4`. `EntrySize(VehicleField)`, `SizeFor(ReadOnlySpan<VehicleSnapshotEntry>)`, `Write(Span<byte>, in VehicleSnapshotHeader, ReadOnlySpan<VehicleSnapshotEntry>)`, `TryParse(ReadOnlySpan<byte>, Span<VehicleSnapshotEntry>, out VehicleSnapshotHeader, out int)`. |

Plus one budget constant, per V3-5/V3-6:

```
/// Worst case: MAX_VEHICLES full entries plus the header. 16 * 30 + 9 = 489.
public const int MaxBodySize = ProtocolConstants.MAX_VEHICLES * 30 + VehicleSnapshotHeader.Size;
```

**The subtype tail (V3-4).** Two bytes, meaning fixed by the `VehicleKind` the client learned from
`S_VEHICLE_SPAWN`:

| Kind | `SubtypeA` | `SubtypeB` |
|---|---|---|
| `Car` | `steerAngle`, i8, degrees | `surfaceFriction`, u8, 0..255 → 0..1 |
| `Tank` | `steerAngle`, i8, degrees | `currentMuzzle`, u8 index |
| `Helicopter` | `rotorSpeed` low byte | `rotorSpeed` high byte (u16, 0..65535 → 0..1 normalized) |
| `Boat` | `steerAngle`, i8, degrees | `surfaceFriction`, u8 |
| unknown | opaque — skipped, 2 bytes | opaque |

**Constraints.** `Write` returns `w.Ok ? w.Position : -1` — the `SpanWriter` latches `Ok=false` on
overflow and never throws (`Io/SpanWriter.cs:9-20`), so the whole entry is written optimistically
and checked once. `TryParse` returns `false` on any short read. No `foreach`, no `System.Linq`, no
allocation in either direction. `entries.Length < vehicleCount` → `false` before any parse, the
same guard `SnapshotMessage.TryParse:195` uses.

**Verify:** `EntrySize(VehicleField.Full)` == **30** exactly (asserted against the § 2.2 table row
by row, not against 30 as a magic number); `EntrySize(VehicleField.None)` == 4; round-trip of a
16-entry full body == `MaxBodySize` == 489 and fits `ServerPayloadWriter.MaxSnapshotBodySize`
(1178) with 689 bytes to spare for actors.

---

### Task 4 — Vehicle delta encoder / decoder (M, 2 days)

**Files (new), `Ironfront.Net.Replication/`:**

| File | Contents |
|---|---|
| `VehicleWorldSnapshot.cs` | The vehicle counterpart of `WorldSnapshot`: a fixed `VehicleSnapshotEntry[MAX_VEHICLES]`, `ServerTick`, `VehicleCount`, `CopyFrom`, `Clear`, `TryFind(ushort, out VehicleSnapshotEntry)`. Allocated once, recycled — same contract as `WorldSnapshot`. |
| `VehicleDeltaEncoder.cs` | Mirrors `DeltaEncoder`: a `BaselineHistory = 32` ring keyed by `tick % 32` with the stored tick verified before use (`DeltaEncoder.cs:126-130` — the ring index alone is not proof), `OnClientAck` routed through `SequenceMath.IsNewer32`, `Reset()`, and `public static VehicleField ComputeChangeMask(in VehicleSnapshotEntry baseline, in VehicleSnapshotEntry current)`. |
| `VehicleDeltaDecoder.cs` | Mirrors `DeltaDecoder`: applies a masked entry onto a baseline entry, one `if` per bit, unknown baseline counted not thrown. |

**Two behaviours copied deliberately, and one that must differ.**

Copied: change detection compares **quantized** values, never raw floats. `DeltaEncoder.cs:169-179`
records why — comparing floats sets the position bit on every entity every tick, the delta carries
every field, bandwidth matches a full snapshot, and every test still passes. A vehicle idling on a
slope is exactly the case that exposes it.

Copied: a vehicle absent from the baseline gets the **full** mask, not a changed-fields mask,
because the client has never seen it and a sparse mask would leave garbage in the rest.

Differs: `DeltaEncoder`'s not-in-baseline branch uses `SnapshotField.FullNoSeat` specifically to
avoid claiming 3 junk bytes of `seatInfo` (`DeltaEncoder.cs:153-156`). There is no equivalent
opt-out here — every `VehicleField` bit is a field a vehicle genuinely has — so the vehicle branch
uses `VehicleField.Full`, all 8 bits. Task 6 changes the actor side of that comment; this task must
not copy the stale reasoning across.

**Verify:** a stationary vehicle produces a 4-byte entry; a vehicle that only rotates produces
`4 + 4 = 8`; an entry whose baseline is missing produces 30; a decoded delta over a baseline equals
the original entry field-for-field.

---

### Task 5 — The five event messages (M, 2 days)

**File (new):** `Ironfront.Net.Protocol/Messages/VehicleMessages.cs`. One `readonly struct` per
message, each with `const int Size`, a `Write(Span<byte>)` returning bytes-or-`-1`, and a static
`TryParse`. Same shape as `ExplosionMessage` (`ActorLifecycleMessages.cs:152-200`).

| Message | Opcode | Layout | Size |
|---|---|---|---|
| `VehicleInputMessage` | `C_VEHICLE_INPUT` 0x21, ch 3 | `u32 tick` + `u16 vehicleId` + `i8 throttle` + `i8 steer` + `i8 pitchAxis` + `i8 auxAxis` + `u16 turretYaw` + `i16 turretPitch` + `u16 buttons` | **16** |
| `SeatRequestMessage` | `C_SEAT_REQUEST` 0x26, ch 2 | `u16 vehicleId` + `u8 seatIndex` + `u8 SeatAction` | **4** |
| `VehicleSpawnMessage` | `S_VEHICLE_SPAWN` 0x4D, ch 2 | `u16 vehicleId` + `u8 VehicleKind` + `u8 networkTypeId` + `i16 posX/Y/Z` + `u32 rotation` + `u8 seatCount` + `u8 flags` | **16** |
| `VehicleDespawnMessage` | `S_VEHICLE_DESPAWN` 0x4E, ch 2 | `u16 vehicleId` + `u8 VehicleDespawnReason` | **3** |
| `ProjectileSpawnMessage` | `S_PROJECTILE_SPAWN` 0x4F, ch 2 | `u16 ownerActorId` + `u8 ProjectileKind` + `i16 originX/Y/Z` + `i16 velX/Y/Z` + `u32 spawnTick` | **19** |
| `SeatChangeMessage` | `S_SEAT_CHANGE` 0x50, ch 2 | `u16 actorId` + `u16 vehicleId` + `u8 seatIndex` + `u8 SeatChangeResult` | **6** |

Notes that are load-bearing, not colour:

- **`vehicleId` in `C_VEHICLE_INPUT`** looks redundant — the server knows which seat the client is
  in. It is there so the server can discard input addressed at a vehicle the client has already
  left, which is precisely the window `Actor.SwitchSeat` opens (design of record § 3.5: it is a
  `LeaveSeat()` + `EnterSeat()` pair in one frame that bypasses `CanEnterSeat()`).
- **`buttons` is level-triggered**, per V3-9. Fire, handbrake, horn, lights are held states. There
  is no edge-triggered bit; exit travels on `C_SEAT_REQUEST`.
- **`turretPitch` is `i16` here and `i8` in the snapshot entry.** Input is what the player asked
  for and wants at full `PackPitch` precision; the snapshot is what the world looks like and the
  § 2.2 table pins `u16 + i8`. The asymmetry is the same one `C_INPUT` already has against
  `SnapshotField.Rotation` (`PackPitch` vs `PackPitchByte`).
- **`ProjectileSpawnMessage` is 19 B, not the "~16 B" of § 2.3.** § 2.3 is a bandwidth *estimate*;
  § 2.2 is the pinned table and it does not cover projectiles. 3 extra bytes on a per-shot event
  moves nothing measurable. It is recorded here rather than fudged because a later reader comparing
  the two numbers deserves to find the answer instead of a discrepancy.
- **`ProjectileSpawnMessage` carries no projectile id.** D5: clients simulate from parameters and
  the server owns hits; detonation replicates as `S_EXPLOSION`, which carries its own position.
  Nothing needs to correlate the two.
- **`VehicleSpawnMessage` carries both `VehicleKind` and `networkTypeId`.** `VehicleKind` is the
  four-way physics family that decides how to read the subtype tail (V3-4); `networkTypeId` is the
  `VehicleIds` registry entry that decides which prefab to instantiate (V3-8, Task 7). Collapsing
  them would make adding a second tank model a wire change.

**Verify:** each `Size` asserted against the field sum; round-trip for each; `TryParse` on a body
one byte short returns `false` and leaves `out` at `default`.

---

### Task 6 — Finish `SnapshotField.SeatInfo` on the actor entry (S, 1 day)

The half of D2 that is missing. `DeltaDecoder.ApplyEntry` already applies the field
(`DeltaDecoder.cs:209-213`) and `SnapshotMessage` already reads and writes it
(`SnapshotMessage.cs:165-168`, `:224-227`). Two producers never set it.

**Edits:**

| File | Change |
|---|---|
| `Enums/GameplayEnums.cs` | Add `Full = FullNoSeat \| SeatInfo`. Keep `FullNoSeat` — `DeltaEncoder` still needs the no-seat mask for unseated actors. |
| `Replication/SnapshotBuilder.cs` | `Capture` gains `ushort vehicleId, byte seatIndex` and sets `ChangeMask = vehicleId != 0 ? SnapshotField.Full : SnapshotField.FullNoSeat`. `WriteFull` (`:109-110`) forces `FullNoSeat` on every entry — it must instead preserve the seat bit when the entry carries a non-zero `VehicleId`, or a full snapshot never carries seat state at all and a joining client sees every passenger standing in the road. |
| `Replication/DeltaEncoder.cs` | `ComputeChangeMask` (`:180-206`) gains `if (baseline.VehicleId != current.VehicleId \|\| baseline.SeatIndex != current.SeatIndex) mask \|= SnapshotField.SeatInfo;`. The not-in-baseline branch (`:153-156`) gains the same `vehicleId != 0` condition as `Capture`, and its comment — which currently explains why `FullNoSeat` beats `0xFF` for a field "v1 does not populate" — is rewritten, because v3 populates it. |

**The consequence, stated because it is silent.** `InterestManager.MaxEntrySize` is
`SnapshotMessage.EntrySize(SnapshotField.FullNoSeat)` (`Interest/InterestManager.cs:98`), i.e. **20**
today. Once seat info can appear, the pessimistic projection that shedding is built on must become
`EntrySize(SnapshotField.Full)` = **23**, or the encode overruns and the whole snapshot is discarded
— the exact failure shedding exists to remove (`InterestManager.cs:90-97`).

The arithmetic, so nobody has to rediscover it:

| | Today | After Task 6 | After Task 6 + V3-5 (typical) | worst case |
|---|---|---|---|---|
| `MaxSnapshotBodySize` | 1178 | 1178 | 1178 − 169 = 1009 | 1178 − 489 = 689 |
| less `SnapshotHeader.Size` (13) | 1165 | 1165 | 996 | 676 |
| `MaxEntrySize` | 20 | 23 | 23 | 23 |
| **actors admitted before shedding** | **58** | **50** | **43** | **29** |

The 58 is not inferred — it is the number `InterestManager.cs:95` already states in its own comment.
Interest management trims a typical viewer to ~20 actors, so a 29-actor floor still carries ~45%
headroom over the typical case; but the margin over the 48-actor *worst* case is gone, and that is
what acceptance criterion 3 grades rather than assumes.

**Verify:** an actor entry with `SeatInfo` encodes to 23 bytes; a seated actor's full snapshot
carries `vehicleId`/`seatIndex`; an actor that leaves a seat produces a delta with `SeatInfo` set
and `vehicleId == 0` (V3-2); `InterestManagementTests` gains a case asserting the admitted-actor
ceiling is 50 with vehicles absent, so the number is pinned rather than drifting.

---

### Task 7 — `VehicleIds` registry and the asset side (S, 1 day + a the client track review round)

Per V3-8. Three copies, gated against each other by Task 10, exactly as `WeaponIds` is:

| File | Role |
|---|---|
| `Ironfront.Net.Protocol/VehicleIds.cs` **(new)** | `public const byte` per vehicle type + `MAX_ASSIGNED` + `static string NameOf(byte)`. Ids are `1..255`; **0 is reserved for unknown**. Ids are permanent — a removed vehicle keeps its id rather than freeing it. Byte-for-byte the shape of `WeaponIds.cs`. |
| `plans/00-shared/protocol-spec.md § 4.9` **(new)** | The value table, in a fenced `csharp` block declaring `class VehicleIds` — `SpecChecker.ExtractClassBlock` matches on the class declaration inside a fence (`Program.cs:273-284`), so the block must be shaped that way or the checker silently finds nothing. |
| `Assembly-CSharp/Vehicle.cs` | A `[SerializeField] private byte networkId;` with a public getter. |

`Vehicle.cs` is a the client track file. Per the design of record § 7 the code is written here, with a PR and
a the client track review round; **The client track authors the per-prefab values**, the same way the per-weapon
`Configuration` values are theirs. V0 has already opened this file for the health setter and the
attacker id, so this edit rides on top of V0's, not beside it.

**Verify:** `VehicleIds.NameOf(0)` returns empty; `NameOf` covers `1..MAX_ASSIGNED` with no gap;
Task 10's gate fails when a prefab id is reassigned.

---

### Task 8 — Conformance tests and hex samples (M, 2 days)

All in `Ironfront.Net.Protocol.Tests/Conformance/`, all under `dotnet test`, no Editor.

> **Every expected hex string is written out from the byte tables in this document by hand, not
> captured from the implementation's own output.** That distinction is the entire value of the
> suite (`PacketHexSampleTests.cs:15-20`): a test that records what the code currently does proves
> only that the code agrees with itself.

| File | Asserts |
|---|---|
| `QuaternionPackTests.cs` **(new)** | Task 2's four properties: < 0.2° round-trip over a deterministic 10⁴ sweep, all four largest-component branches exercised, `PackQuat(q) == PackQuat(-q)`, unit length within 10⁻³, and **no `NaN` for any input including `0xFFFFFFFF`**. |
| `VehicleSnapshotTests.cs` **(new)** | `EntrySize` per bit against the § 2.2 table row by row; `EntrySize(Full) == 30`; `EntrySize(None) == 4`; header is 9; a 16-vehicle full body is 489 and fits `MaxSnapshotBodySize`; `TryParse` rejects `vehicleCount` beyond the caller's span; a mask with only `Position` set yields a 10-byte entry and touches no other field. |
| `VehicleMessageTests.cs` **(new)** | Every `Size` in Task 5's table; round-trip each; short-body `TryParse` returns `false`. |
| `PacketHexSampleTests.cs` (edit) | Hand-written hex, both directions, for: `C_VEHICLE_INPUT`, `C_SEAT_REQUEST`, `S_VEHICLE_SNAPSHOT` (**one 30-byte full entry followed by one 4-byte stationary entry** — the mixed case is the one that catches a mis-sized `EntrySize`), `S_VEHICLE_SPAWN`, `S_VEHICLE_DESPAWN`, `S_PROJECTILE_SPAWN`, `S_SEAT_CHANGE`, and an **`ActorSnapshotEntry` with `SeatInfo` set, asserted at 23 bytes**. Extend the existing enum-pinning block (`:261` already pins `SeatRequest = 0x26`) with `0x21` and `0x4C`–`0x50`. |
| `SnapshotTests.cs` (edit) | `SnapshotField.Full == 0xFF`; `EntrySize(Full) == 23`. `:53` already pins `SeatInfo == 1 << 7`. |
| `Replication.Tests/SnapshotAndDeltaTests.cs` (edit) | Seat enter → delta carries `SeatInfo`; seat leave → delta carries `SeatInfo` with `vehicleId == 0`; unchanged seat → `SeatInfo` absent. Vehicle delta cases from Task 4. |
| `Replication.Tests/InterestManagementTests.cs` (edit) | The admitted-actor ceiling is **50** with `MaxEntrySize = 23`, pinned as a number so Task 6's shift cannot drift back unnoticed. |

**Verify:** `dotnet test` green across the solution.

---

### Task 9 — `protocol-spec.md` (S, 1 day)

| Section | Change |
|---|---|
| § 4.1 `msgType` table | The six new rows. `0x26` moves from reserved to defined. |
| § 4.3.1 | The line *"7 bits used, 1 spare (bit 7, `seatInfo`, is the stretch-goal vehicle field)"* is now **wrong** — all 8 are used and populated. Rewrite it, and rewrite the "Size estimate" table's **Full** row from 20 to 23. This is stale text the design of record § 3.1 already flagged; leaving it is how the next reader plans against a spare bit that does not exist. |
| **§ 4.9 `vehicleId` — the value space** (new) | Per V3-8, modelled on § 4.8. Fenced `csharp` block declaring `class VehicleIds`. |
| **§ 4.10 The vehicle stream** (new) | The `S_VEHICLE_SNAPSHOT` byte layout, the `VehicleField` bit table, the § 2.2 entry table, the subtype-tail table (V3-4), the id allocation + quarantine + `0 = none` rules (V3-2, mirroring § 4.3.1's structure), the co-residency rule (V3-5), and the six event-message layouts. `S_EXPLOSION` is cross-referenced, not restated (V3-7). |
| § 14 conformance checklist | Four new items: the quaternion round-trip, the 30-byte full vehicle entry, the 23-byte seated actor entry, the worst-case combined body inside one datagram. |
| **§ 15 changelog** | The row below. |

```
| **3.0.0** | Week 3 | the replication track | **The vehicle wire.** Six new opcodes (0x21, 0x4C–0x50) and
0x26 promoted from reserved; new `VehicleSnapshotEntry` with its own u16 change mask (new
§ 4.10); smallest-three quaternion packing in `Quantize`; `SnapshotField.SeatInfo` finished on
the actor entry, moving the full actor entry 20 → 23 B; new `VehicleIds` value space (new
§ 4.9), SpecChecker now gates spec ↔ code ↔ vehicle prefab | **Yes** | (this PR) |
```

**Verify:** `dotnet run --project tools/SpecChecker` exits 0 *after* Task 10, and exits **non-zero
before it** — a spec section the checker does not read is a section that cannot fail.

---

### Task 10 — `tools/SpecChecker` (S, 1 day)

`Program.cs` walks three classes (`:56-58`) plus the weapon prefab (`:63`). A wire change is a
three-file change by design, so the vehicle registry gets the same treatment.

- `checkedCount += Check(spec, "VehicleIds", typeof(VehicleIds), failures);` — no new machinery;
  `ExtractClassBlock` finds it by class declaration inside a fence, and `TryEvaluate` already
  handles plain byte literals.
- `CheckVehiclePrefabs(repoRoot, failures)` — the counterpart of `CheckWeaponPrefab`
  (`:183-266`). Same four failure classes, worded the same way: duplicate id, id outside `1..255`,
  an id the prefab has that `VehicleIds` does not know, and an id `VehicleIds` declares that no
  prefab carries. The scan is over the vehicle prefabs rather than one `_Managers.prefab`, matching
  `networkId:` against the `Vehicle` script block.
- **`MAX_VEHICLES` and `VEHICLE_ID_QUARANTINE_TICKS` are picked up for free** — they are `public
  const` in `ProtocolConstants`, which is already checked, so they only need to appear in the
  spec's `ProtocolConstants` fenced block. Adding the constant to the code without the spec is
  already a build failure; that is the intended behaviour, not an oversight.

**A caveat to record rather than discover.** `CheckWeaponPrefab` matches on *shape* — a
`- NetworkId:` line followed immediately by `name:` (`:194-197`, and the comment at `:176-181`
concedes it). The vehicle check inherits that fragility: reordering the serialized fields on
`Vehicle` silently drops the parse to 0 entries. The existing code already handles this correctly
by treating a 0-entry parse as a **failure** rather than a pass (`:199-206`); the vehicle check must
copy that, because it is the difference between a checker and a decoration.

**Verify:** exits 0 on a consistent tree; exits 1 with the right message for each of the four
failure classes, driven by a temp-file fixture; exits 1 when `PROTOCOL_VERSION` in code and spec
disagree (which is what makes Task 11 unskippable).

---

### Task 11 — `PROTOCOL_VERSION` 2 → 3 and the PR (S, 0.5 day + a 2-approval review round)

**Last, and deliberately so.** Every task above is additive to a v2 wire: new opcodes a v2 peer
counts as unknown, new codecs nothing calls yet, a spec section, a checker. The moment
`PROTOCOL_VERSION` moves, a v2 client gets `CONNECT_DENIED` code 2 and cannot connect at all. Doing
it last means the review round is spent on a complete, green change set rather than on a
half-landed one.

- `ProtocolConstants.PROTOCOL_VERSION = 3`, and the same number in the spec's § 1 fenced block —
  `SpecChecker` fails the build if only one moves.
- `Ironfront.Net.Protocol/**` is **shared ownership**: PR with **2 approvals** (`protocol-spec.md`
  § 15, `conventions.md` § 2). Budget a full review round; § 7 does.
- No test breaks on the bump. `PacketHexSampleTests.ConnectRequestHex` is built from the constant
  precisely so a version bump does not silently keep asserting the previous protocol
  (`:27-33`).

**On phased rollout.** `ClientMessageRouter` counts unknown message types rather than erroring
(`ClientMessageRouter.cs:31-35`, `:221`), and `ServerMessageRouter` does the same (`:131`). That
makes the rollout safe **within** v3 — a v3 server that has V4 wired can emit `0x4C` at a v3 client
that has not yet subscribed, and the result is a non-zero `UnknownMessages` counter rather than a
dropped batch or a crash. It does **not** make the rollout safe *across* the version bump: v2 and
v3 peers never complete a handshake. That is fine and intended — D7 exists because the client and
the server ship together — but the two claims are different and only the first is about the router.

**Verify:** `dotnet test` green across the solution; `dotnet run --project tools/SpecChecker`
exits 0; a v3 client against a v2 server gets `CONNECT_DENIED` code 2 (already covered by the
handshake tests, which read the constant).

---

## 5. Acceptance criteria

1. `PROTOCOL_VERSION == 3` in `ProtocolConstants.cs` **and** in `protocol-spec.md § 1`, with a
   § 15 changelog row marked `Wire change? Yes`, landed by a PR with 2 approvals.
2. `VehicleSnapshotMessage.EntrySize(VehicleField.Full) == 30`, matching § 2.2 field by field, and
   a stationary vehicle's delta is exactly 4 bytes.
3. `SnapshotMessage.EntrySize(SnapshotField.Full) == 23`, `InterestManager.MaxEntrySize == 23`, and
   the admitted-actor ceiling is pinned at **50** by a test — not left to be discovered by a
   bandwidth regression later.
4. A seated actor's snapshot carries `vehicleId`/`seatIndex`; leaving a seat produces a delta with
   `SeatInfo` set and `vehicleId == 0`; an unchanged seat produces no `SeatInfo` bit.
5. Smallest-three quaternion round-trips within **0.2°** across all four largest-component
   branches, is sign-canonical, unit-normalized, and returns no `NaN` for any 32-bit input.
6. Hand-written hex samples exist and pass in both directions for all six new messages, for a mixed
   full+stationary vehicle snapshot, and for a 23-byte seated actor entry.
7. The worst case — 16 full vehicle entries (489 B) plus an actor snapshot — fits one un-fragmented
   datagram inside `ServerPayloadWriter.MaxSnapshotBodySize` (1178) and
   `ProtocolConstants.MAX_CHANNEL_PAYLOAD` (1181), with the actor budget reduced by exactly what
   the vehicle body consumed (V3-5).
8. `SnapshotHeader.ActorCount` remains a `u8` and `MAX_ACTORS` remains 64 — this phase adds no
   entity to that space. Vehicles occupy a **separate** `u16` id space capped at
   `MAX_VEHICLES = 16`, allocated from 1, quarantined 150 ticks.
9. `tools/SpecChecker` gates `VehicleIds` against the spec **and** against the vehicle prefabs, and
   fails on each of the four failure classes when driven with a broken fixture.
10. `protocol-spec.md § 4.3.1`'s "7 bits used, 1 spare" claim and its 20-byte Full row are corrected.
11. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no allocation in any
    new logic file; every new codec is a `readonly struct` (or a mutable parse-in-place entry
    struct) with hand-written `Write` + `TryParse`.

---

## 6. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Finishing `SeatInfo` narrows the shedding budget and a bandwidth regression only surfaces at load | 4 | 4 | **16** | Task 6 computes the 58 → 50 → 43 → 29 ladder up front and criterion 3 pins the ceiling as a number in `InterestManagementTests`. The viewer's own actor is already never shed (phase-05 D6); V3-5 keeps the vehicle body bounded at 489 B so the actor floor can never fall below 29. If it still bites, the § 9 fallback ladder from the design of record applies — drop angular velocity at Mid/Far first. |
| The vehicle entry as specified does not survive first contact with V4's real capture, and the change costs a second 2-approval round | 3 | 5 | **15** | The § 2.2 table is reproduced verbatim and Task 3 arrives at 30 by **addition**, so a mismatch fails a test rather than shipping. `VehicleField` keeps 8 of 16 bits spare, so a *new* field is an additive change to an existing `u16` mask, not a mask widening — the failure mode that made `SnapshotField` unextendable is designed out here. |
| A vehicle-type id is reassigned in the Inspector; server and client disagree about which prefab id 4 is, at runtime, for every player | 3 | 5 | **15** | This is the `weaponId` incident (§ 15 changelog, 2.0.1) with the serial numbers filed off. V3-8 + Task 10 gate it on every CI run *before* it can ship. Precondition for starting V4: the vehicle prefab gate must be green, not merely present. |
| The 2-approval review round blocks V4–V7 | 3 | 3 | 9 | Design of record § 6: V1, V2 and V8 are off the vehicle critical path and land during the review. Task 11 is last, so the review is on a complete green change set. |
| `Vehicle.cs` conflicts with V0's edits or the client track's branch | 3 | 3 | 9 | V3 depends on V0 (§ 6 of the design of record), so V0's `Vehicle.cs` edits land first and Task 7 rides on top. Task 7 is one serialized field and is severable — it can land in its own PR after the protocol PR. |
| The subtype tail's per-kind meaning drifts between server and client | 3 | 4 | 12 | V3-4's fixed 2-byte width makes a wrong *interpretation* a wrong number, never a misaligned stream. The per-kind table lives in § 4.10 (one place) and the hex samples pin one entry per kind. |
| Smallest-three sign or clamp bug ships silently — half of all rotations mirrored, or a `NaN` transform | 3 | 4 | 12 | Task 2 names the three failure modes explicitly and Task 8 tests each as a separate assertion, including the hostile `0xFFFFFFFF` input. A round-trip-only test would pass with the sign bug present, which is why the sign case is its own test. |
| `SpecChecker`'s shape-matching prefab parse silently finds 0 vehicle entries after a field reorder | 2 | 4 | 8 | The existing weapon check already treats a 0-entry parse as a **failure** (`Program.cs:199-206`); Task 10 copies that, and criterion 9 drives it with a broken fixture rather than trusting it. |
| `ProjectileSpawnMessage` at 19 B contradicts § 2.3's "~16 B" and someone later "fixes" the wrong one | 2 | 2 | 4 | Recorded in Task 5 with the reason. § 2.3 is an estimate; § 2.2 is the pinned table and does not cover projectiles. |

Three risks reach 15+. Each has a mitigation that is a **precondition of the phase**, not a plan
for later: the shedding ladder is computed before Task 6 is written, the entry size is derived
rather than asserted, and the prefab gate must be green before V4 begins.

---

## 7. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — Opcodes, enums, constants | S (0.5d) | No dependencies. Start here; everything else references these names. |
| 2 — Smallest-three quaternion | S (1d) | Independent of 1. Can run alongside. |
| 3 — `VehicleSnapshotEntry` + codec | M (2d) | Needs 1 and 2. |
| 4 — Vehicle delta encoder/decoder | M (2d) | Needs 3. |
| 5 — The five event messages | M (2d) | Needs 1. Independent of 3 and 4 — parallel-safe. |
| 6 — Finish `SnapshotField.SeatInfo` | S (1d) | Needs 1 only. **Touches `InterestManager`** — do not run this in parallel with any other actor-snapshot work. |
| 7 — `VehicleIds` + `Vehicle.networkId` | S (1d) + the client track review | Needs V0 merged. Severable; can land in its own PR. |
| 8 — Conformance tests + hex samples | M (2d) | Written **alongside** 2–6, not after. |
| 9 — `protocol-spec.md` | S (1d) | Needs 1–7 settled. Must land in the same PR as the code. |
| 10 — `tools/SpecChecker` | S (1d) | Needs 7 and 9. Gates them both. |
| 11 — `PROTOCOL_VERSION` bump + PR | S (0.5d) + **2-approval review round** | Last. The review round is scheduled time, not slack. |
| **Total** | **~2 weeks** incl. one review round | Critical path: **1 → 3 → 4 → 8 → 9 → 10 → 11**. Tasks 5, 6 and 7 are off it. |

**File ownership within the phase.** No two tasks write the same file:
Task 1 owns `MessageTypes.cs` / `VehicleEnums.cs` / `ProtocolConstants.cs` · Task 2 owns
`Quantize.cs` · Task 3 owns `VehicleSnapshotMessage.cs` · Task 4 owns the three new
`Ironfront.Net.Replication/Vehicle*.cs` · Task 5 owns `VehicleMessages.cs` · Task 6 owns
`GameplayEnums.cs`, `SnapshotBuilder.cs`, `DeltaEncoder.cs`, `InterestManager.cs` · Task 7 owns
`VehicleIds.cs` and `Vehicle.cs` · Task 8 owns the test files · Task 9 owns `protocol-spec.md` ·
Task 10 owns `tools/SpecChecker/Program.cs`. Task 1 and Task 6 both touch the `Enums/` folder but
different files. `ProtocolConstants.PROTOCOL_VERSION` is the one line two tasks touch (1 and 11) —
Task 11 owns it; Task 1 leaves it at 2.

---

## 8. Handoff

**To the protocol reviewers (2 approvals, shared ownership of `Ironfront.Net.Protocol/**`).** One
PR carrying Tasks 1–6 and 8–11: the codecs, the tests, the spec section, the changelog row, the
checker, and the version bump together. Reviewing them separately would put a spec section in front
of a reviewer before the bytes it describes exist. Task 7's `Vehicle.cs` edit is severable and may
be split into a second PR if the client track round runs long.

**To V4 (vehicle server authority).** This phase hands over:

- `VehicleSnapshotEntry` + `VehicleSnapshotMessage` — the codec. V4 owns `VehicleIdPool`, the
  registry, capture, and the interest banding.
- `VehicleDeltaEncoder` / `VehicleWorldSnapshot` — the per-client baseline machinery, mirroring
  `DeltaEncoder`. V4 wires one instance per `ClientSession`.
- **The budget split is V4's to implement (V3-6).** V3 ships `VehicleSnapshotMessage.MaxBodySize`
  (489), the co-residency rule, and the test that the worst case fits. `ServerPayloadWriter` writing
  two messages into one payload batch, and `InterestManager.BuildView` taking the reduced actor
  budget, are V4 changes. The ladder in Task 6 is the number V4 must hit.
- **The vehicle prefab gate must be green before V4 starts** (§ 6, risk score 15).

**To V5 (client vehicle replication).** `VehicleDeltaDecoder` and `Quantize.UnpackQuat`. Note V3-1:
there is no `lastProcessedInputTick` on the vehicle header, because D3 blends rather than replays.
If V5 discovers it needs one, that is a wire change and a second version bump — raise it before
writing code against the assumption.

**To V7 (projectiles).** `ProjectileSpawnMessage` at 19 B, no projectile id, detonation via the
existing `S_EXPLOSION`. V1 supplies `S_EXPLOSION`'s caller and subscriber; V3 changed neither.

**To the client track.** One field: `[SerializeField] private byte networkId` on `Vehicle`, plus authoring the
per-prefab values against `protocol-spec.md § 4.9`. Same shape as the per-weapon `Configuration`
values already owned there. Nothing else in this phase touches a the client track file, and nothing in it needs
the Editor.
