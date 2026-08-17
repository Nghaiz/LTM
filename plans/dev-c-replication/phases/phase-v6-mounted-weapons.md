# Dev C — Phase V6: Mounted weapons and turrets

> Design of record: [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md).
> § 5 is the integration contract; **D4** — all gameplay randomness resolves server-side — governs
> this phase. Read it first: it carries the evidence for why the shape is what it is, and its § 4
> decisions are not re-litigated here.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files; engine-free logic
> in `Ironfront.Net.Replication` with Unity holding a thin seam) and § 7 (ownership), departed from
> for `Assembly-CSharp/` files with recorded consent — brainstorm § 7, on the phase-05 Task 6
> precedent.
>
> **Depends on V4** (`VehicleIdPool`, vehicle registry, capture/encode, interest bands, seat
> arbitration). **Blocks V7** — the muzzle transform a projectile launches from is the transform
> this phase makes authoritative.

---

## 1. Objectives

Every mounted weapon in the game — `TankTurret`, `MountedTurret`, `AlternatingMountedWeapon`,
`CarHorn` — currently aims from `Input.GetAxis` read directly inside `Update`
(`TankTurret.cs:66`, `MountedTurret.cs:56`) and fires through a chokepoint the server never
reaches. Nothing about a turret is on the wire, and a headless build dereferences
`OptionsUi.GetOptions()` on the same line.

By the end of this phase:

1. Turret aim is **authoritative on the server**, framerate-independent, and replicated in the
   vehicle entry's turret field (§ 5). V0 introduced the authoritative `float yaw` / `pitch` pair
   and the public setter; V6 is what drives them from replicated input and puts them on the wire.
2. Firing a mounted weapon spends **server** ammo. `Weapon.CanFire()` (`Weapon.cs:306-309`) — the
   single gate every mounted subclass already funnels through — consults server state, and
   `Weapon.Shoot` (`Weapon.cs:321`) runs its gameplay half only at `NetRole.Server`.
3. Mounted spare ammo is modelled **per weapon**, matching `MountedWeapon.cs:26-41`, and not
   confused with the Actor-held 5-slot pool infantry uses (`Actor.cs:1117`).
4. `AlternatingMountedWeapon.currentMuzzle` (`:7`) is replicated. It is not cosmetic: it selects
   the transform `MuzzlePosition()` (`:19-22`) returns, which AI aiming and V7's projectile origin
   both read.
5. Firing a tank cannon perturbs the tank's own rigidbody (`TankTurret.cs:74`) **server-side only**,
   with the randomized component of the impulse drawn on the server per D4.
6. `CarHorn`'s `user.Highlight()` — a gameplay effect that reveals the occupant to AI — happens on
   the server, and its `lastFired = Time.time` session clock is never treated as replicated state.
7. A headless server runs all of the above with zero NREs, and all of the logic is graded by
   `dotnet test` in CI without opening Unity.

**Not in this phase:** projectile flight, detonation, throwables and deployables — those are V7,
which depends on this one for the muzzle pose. No Editor session and no Profiler run; prefab field
wiring stays with Dev A (§ 7 Handoff).

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **D1** | **Fire intent for a mounted weapon rides the existing `InputButtons.Fire` bit in `C_INPUT`.** `C_VEHICLE_INPUT` carries 4 axes + turret aim and nothing else, exactly as § 5 specifies. A seated player already sends `C_INPUT`; adding a second fire bit would create two paths to the same authority check, and the one nobody tests is the one that gets exploited. |
| **D2** | **Aim authority is the server's own turret simulation, not the replicated pose.** The pose in the vehicle entry exists so remote peers *draw* the turret correctly. What decides where a shell goes is the server's `TurretAimState` at the tick `Shoot` runs. This is why turret input must be applied earlier in the server tick than fire resolution — see Task 3's ordering note. |
| **D3** | **The vehicle entry's single turret slot (§ 5, `u16 + i8`) belongs to the vehicle's first mounted-weapon seat in `Vehicle.seats` order.** A second turret on the same vehicle is driven, while occupied, from its occupant's already-replicated actor rotation (`SnapshotField.Rotation`), and holds its last pose when vacant. The residual error is **cosmetic only**, because the shot itself never reads a remote client's transform — V7's `S_PROJECTILE_SPAWN` carries a server-computed origin. Recorded as a risk rather than hidden. |
| **D4-local** | **Recoil is client-local and the server never applies it to a human player.** `Weapon.Shoot:348` kicks the camera; the consequence of that kick is already inside the *next* `C_INPUT` frame's yaw and pitch, which the server accepts as the aim. Replicating recoil would apply it twice. For **AI actors**, which have no input frame, recoil applies server-side, and its `Random.insideUnitSphere` draw is a server draw per D4. |
| **D5-local** | **`TankTurret`'s tower replicates its joint *target*, not the resulting transform.** `towerJoint.targetRotation` and `cannonJoint.spring.targetPosition` are **inputs** to PhysX, not outputs of it. A value that PhysX consumes replicates exactly; the transform PhysX produces does not (brainstorm § 3.2). This is what keeps the D3 prediction problem out of turrets. |
| **D6** | **One `WeaponRuntimeState`, extended with `short SpareAmmo`, plus an `ISpareAmmoPool` seam.** Infantry's pool lives on the Actor across 5 slots; a mounted weapon's lives on the weapon. Two near-identical runtime structs would drift (`development-principles.md` § SSOT), and a single struct with no pool seam would force one of the two owners to be wrong. The `-1` (no resupply) and `-2` (infinite) sentinels from `Weapon.Configuration.spareAmmo` are carried through unchanged — `short`, not `byte`, exists to hold them. |
| **D7** | **`Seat.CanUseWeapon()` is behaviour to preserve, not a bug to fix — and it is renamed to `CanUseCarriedWeapon()`.** See § 3 Task 6 for the evidence. The behaviour is correct; only the name is wrong, and a misleading name on a nine-site gate is exactly what produces a confident future "fix". |
| **D8** | **`CarHorn` gets `WeaponIds.CAR_HORN = 18`.** Append-only id assignment is explicitly permitted (`WeaponIds.cs:20-23`) and is not a wire-format change, so it needs a shared-file PR (spec § 4.8 row + `SpecChecker` + the prefab registry name) but **not** a `PROTOCOL_VERSION` bump. Folded into V3's PR if V3 is still open. |
| **D9** | **The `NetRole.Offline` path is a no-op everywhere in this phase.** Single-player mounted-weapon behaviour is unchanged, byte for byte, and a test pins it. This is the same guard shape phase-05 Task 6 used at `Actor.Damage`, for the same reason. |

---

## 3. Detailed tasks

### Task 1 — Engine-free turret aim (M, 2 days)

**Files (all new), `Ironfront.Net.Replication/Vehicles/`:**

| File | Contents |
|---|---|
| `TurretAimState.cs` | `struct TurretAimState { float Yaw; float Pitch; }` — the authoritative pose, in degrees, that V0's setter writes into the joint or the euler angles. |
| `TurretAimLimits.cs` | `readonly struct TurretAimLimits { float MinPitch, MaxPitch; float SlewDegreesPerSecond; bool YawUnlimited; float MinYaw, MaxYaw; }`. `MountedTurret` clamps pitch to `[-40, +15]` (`:23`); `TankTurret` clamps to `cannonJoint.limits` — both become authored `TurretAimLimits`, not literals inside an `Update`. |
| `TurretAimPolicy.cs` | `static void Step(ref TurretAimState, in TurretAimLimits, float yawAxis, float pitchAxis, float dt)`. Clamps both axes to `[-1, +1]` **before** use — a client sending 10⁶ is the brainstorm's acceptance criterion 3 — then advances by at most `SlewDegreesPerSecond * dt`, then clamps pitch and wraps yaw into `[0, 360)`. |
| `TurretAimQuantization.cs` | `PackYaw` / `UnpackYaw` / `PackPitch` / `UnpackPitch` **delegating to `Ironfront.Net.Protocol.Quantize`**, not reimplementing it. The actor entry's `SnapshotField.Rotation` is already `u16 yaw + i8 pitch`; the turret slot in § 5 is the same shape, so it must be the same code. A second packing of the same quantity is the SSOT violation the week-1 freeze exists to prevent (`conventions.md` § 7, the `Quantize` correction). |

**The framerate bug this closes.** `TankTurret.cs:31-32` and `MountedTurret.cs:20-23` contain **no
`Time.deltaTime` at all** — a 144 Hz client traverses ~2.4× faster than a 60 Hz one (brainstorm
§ 3.3). V0 introduced the authoritative floats; `TurretAimPolicy.Step` is the only place that
advances them, and it takes `dt`.

**Constraints.** `TurretAimPolicy` is `static` with no state, so nothing allocates. No `foreach`,
no LINQ.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests` — `ATurretTraversesTheSameArcAtAnyTimestep`
steps the same total time as 1 × 1 s, 30 × 33 ms and 144 × 6.9 ms and asserts the three final yaws
agree within one quantization step. This is brainstorm acceptance criterion 4, graded in CI without
Unity.

---

### Task 2 — The Unity turret seam (M, 2 days)

**New file, `Assets/Scripts/Net/Shared/NetTurret.cs`** (Dev C-owned, `conventions.md` § 7).

One component per turret. It resolves the aim source by role and writes the result through V0's
setter — the two `Update` bodies stop reading input themselves:

| Role | Aim source |
|---|---|
| `NetRole.Server` | The turret axes in the last accepted `C_VEHICLE_INPUT` for the occupant, through `TurretAimPolicy.Step`. **Authoritative.** |
| `NetRole.Client`, locally occupied | Local mouse input through the *same* `TurretAimPolicy.Step`, for zero-latency feel, corrected toward the replicated pose. Converges by construction per D5-local: both sides drive the same joint target from the same policy. |
| `NetRole.Client`, remote vehicle | The decoded pose from the vehicle entry, interpolated on the same schedule V5 uses for vehicle transforms. Never runs the policy. |
| `NetRole.Offline` | `Input.GetAxis` exactly as today (D9). |

**Edits to `Assembly-CSharp/` (Dev A files — one PR, one review round, per brainstorm § 7):**

| File | Change |
|---|---|
| `TankTurret.cs` | `Update` (`:23-36`) stops calling `GetInput()` and instead writes `towerJoint.targetRotation` / `cannonJoint.spring.targetPosition` from the `NetTurret`'s `TurretAimState`. The `MAX_TURN_DELTA = 5f` per-frame clamp (`:31-32`) is replaced by `SlewDegreesPerSecond` — it was a per-*frame* limit, which is the framerate bug restated. |
| `MountedTurret.cs` | Same shape: `Update` (`:13-26`) writes `towerTransform.localEulerAngles.z` and `turretTransform.localEulerAngles.x` from the state. `Vector2.ClampMagnitude(…, 10f)` (`:18`) becomes `SlewDegreesPerSecond`. |
| both | `GetInput()` becomes `NetRole.Offline`-only. Its `OptionsUi.GetOptions()` and `Input.GetAxis` dereferences (`TankTurret.cs:66`, `MountedTurret.cs:56`) are then unreachable on a headless server, closing two of the § 3.6 NREs that V0 could not reach without this seam. |
| both | `Unholster` / `Holster` touch `camera.enabled` and `FpsActorController.instance` (`TankTurret.cs:38-56`, `MountedTurret.cs:28-46`) — guard both at `NetRole.Server`. `FpsActorController.instance` is null in a headless build. |

**Encode/decode.** V4's vehicle encoder gains the 3-byte turret field; the change mask bit is
already allocated in § 5's entry shape. The turret field is masked **on change only**, like every
other field, so a parked vehicle with a still turret costs nothing.

**Verify:** solution compiles; `ATurretPoseRoundTripsThroughTheVehicleEntry` asserts encode → decode
returns the same pose within one quantization step, and that an unchanged pose sets no mask bit.

---

### Task 3 — Fire authority at the chokepoint (L, 3 days)

`Weapon.CanFire()` (`Weapon.cs:306-309`) already centralizes the ammo, reload, holster and cooldown
gate for every subclass. `Weapon.Shoot` (`:321`) is the single place a shot's effects happen. Those
are the two hooks; nothing else is touched.

**Files (all new), `Ironfront.Net.Replication/Combat/`:**

| File | Contents |
|---|---|
| `MountedWeaponAuthority.cs` | The `ServerCombatAuthority` counterpart for weapons keyed by `(vehicleId, seatIndex)` rather than `actorId`. Same `Step` ordering (complete a running reload → accept a reload intent → the trigger), same non-allocating shape, same `FireRejection` enum reused rather than cloned. |
| `ISpareAmmoPool.cs` | `int Take(ushort ownerId, byte slot, int count)` — the engine-free mirror of `Weapon.RemoveSpareAmmo` (`Weapon.cs:272`, overridden at `MountedWeapon.cs:26`). Two implementations: `ActorSpareAmmoPool` (5 slots on the Actor, `Actor.cs:1117`) and `MountedSpareAmmoPool` (the weapon's own `short`). Both are long-lived fields, so the interface call allocates nothing. |
| `MountedWeaponRegistry.cs` | Fixed-capacity arrays indexed by a packed `(vehicleId, seatIndex)` key, sized from `ReplicationConfig`. No dictionary, no per-tick allocation. Participates in `AssertCleanState()` (brainstorm criterion 13). |

**Edits:**

| File | Change |
|---|---|
| `Combat/WeaponModel.cs` | `WeaponRuntimeState` gains `short SpareAmmo` (D6). `Loaded(in WeaponConfig)` initialises it from a new `WeaponConfig.SpareAmmo`, preserving the `-1` / `-2` sentinels. |
| `Combat/ServerReloadPolicy.cs` | `CompleteReloadIfElapsed` gains an `ISpareAmmoPool` parameter and mirrors `Weapon.ReloadDone()` (`:262-270`) exactly: `count = ClipSize - AmmoInClip`, `taken = pool.Take(...)`, `AmmoInClip += taken`. It currently refills to `ClipSize` unconditionally, which is correct only for an infinite pool. |
| `Assembly-CSharp/Weapon.cs` | `CanFire()` gains one leading term: `if (!NetWeaponAuthority.MayFire(this)) return false;`. `Shoot()` splits its gameplay half (`user.Highlight()` `:325`, `SpawnProjectile` `:336-339`, `ammo--` `:344-347`, `AmmoChanged()` `:349`) from its cosmetic half (muzzle flash, casing, audio, animator trigger, reverb) behind the role check. |
| `Assembly-CSharp/TankTurret.cs` | `SpawnProjectile` (`:72-76`) — the `AddForceAtPosition` with `Random.insideUnitSphere * randomKick` runs **server-side only**, and the resulting vehicle motion reaches clients through the vehicle entry's velocity fields. Per brainstorm D4 the roll is a server roll; per § 3.3 it cannot be seeded, because the same `UnityEngine.Random` stream carries cosmetic audio-pitch draws. |

**New file, `Assets/Scripts/Net/Shared/NetWeaponAuthority.cs`** (Dev C-owned):

```
NetRole.Offline  → true, always. Single-player is untouched (D9).
NetRole.Server   → MountedWeaponAuthority / ServerCombatAuthority says so.
NetRole.Client   → true only for the local player's own weapon (cosmetic prediction);
                   false for every remote actor's weapon, which is driven by S_WEAPON_FIRE.
```

**Ordering inside the server tick — load-bearing.** Turret input must be applied *before* fire
resolution, because `Weapon.SpawnProjectile` (`:388-394`) reads `configuration.muzzle.position`,
which is the transform Task 2 just rotated. The `ServerPlayer` tick becomes:

```
ServerVehicleTick
  ├─ apply C_VEHICLE_INPUT axes            (V4)
  ├─ NetTurret.ApplyAim  → TurretAimPolicy.Step   ← muzzle transform settles here
  ├─ Unity FixedUpdate / PhysX
  └─ InputAuthority.ApplyPendingInput(..., _combatObserver)
        └─ MountedWeaponAuthority.Step → Weapon.CanFire → Weapon.Shoot
             └─ reads configuration.muzzle.position   ← now correct
```

Getting this backwards produces shots that leave from where the turret pointed **last** tick — a
33 ms lag that is invisible on a static target and systematically wrong on a traversing one.

**Constraint.** `MountedWeaponAuthority` holds the resolver, the sink and the pool as constructor
fields. Nothing on this path allocates per tick; no `System.Linq`; no `foreach`.

**Verify:** `AMountedWeaponSpendsServerAmmoAndHonoursItsOwnCooldown`;
`AMountedReloadDrawsFromThePerWeaponPoolNotTheActorPool`;
`AnInfiniteSpareAmmoSentinelNeverDecrements`; `AClientCannotFireARemoteActorsMountedWeapon`.

---

### Task 4 — `currentMuzzle` replication (S, 1 day)

`AlternatingMountedWeapon.currentMuzzle` (`:7`) is `private int`, advanced inside `SpawnProjectile`
(`:12`), and read by `MuzzlePosition()` (`:19-22`). AI aiming reads `MuzzlePosition()`, and V7's
projectile origin will too — so an unreplicated value is an **aim divergence**, not a cosmetic one.

| File | Change |
|---|---|
| `Assembly-CSharp/AlternatingMountedWeapon.cs` | `private int currentMuzzle` → `[NonSerialized] public byte currentMuzzle`, so the encoder can read it and the decoder can write it. Advance moves into `AdvanceMuzzle()` so there is one mutation site. |
| V4's vehicle encoder | `currentMuzzle` occupies the subtype tail § 5 already reserves for it (`rotorSpeed` / `steerAngle` / `currentMuzzle` + friction, 2 bytes). Sent on change only. |
| decoder | Applies `index % muzzles.Length` — see below. |

**Two details that are easy to get wrong.**

1. **The advance happens before the spawn, not after.** `:11-12` reads `muzzles[currentMuzzle]`,
   *then* increments — so the projectile leaves the old muzzle while `MuzzlePosition()` immediately
   afterwards returns the **next** one. That asymmetry is what the AI aims with today. Preserve it
   exactly; a test pins the sequence.
2. **The receiver must take the modulo.** `muzzles.Length` is a per-prefab authored value. A client
   whose prefab revision has fewer muzzles than the server's would index out of range and throw
   inside the render path. `index % muzzles.Length` on decode costs nothing and turns a crash into
   a wrong-but-harmless muzzle choice.

**Verify:** `AlternatingMuzzleAdvancesBeforeTheSpawnNotAfter` — assert the spawn transform and the
post-shot `MuzzlePosition()` differ, in that order; `ADecodedMuzzleIndexIsClampedByModulo` — feed an
index of 200 against a 2-muzzle prefab and assert no throw.

---

### Task 5 — `CarHorn` (S, 0.5 day)

`CarHorn : MountedWeapon` (`CarHorn.cs:3`) — not a `Vehicle`, and its `Shoot` override (`:5-13`)
does three things:

| Line | What it is | Where it belongs |
|---|---|---|
| `:9` `user.Highlight()` | **Gameplay.** Reveals the occupant to AI (`Actor.cs:889`). | **Server.** Behind the D9 role guard, same as every other `Highlight()` call. |
| `:11` `audio.Play()` | Cosmetic. | Client and offline. Guarded — `Weapon.Awake` (`:145`) assigns `audio` from `GetComponent<AudioSource>()`, which is null on a stripped headless prefab. |
| `:12` `lastFired = Time.time` | A **session-relative clock**. | Neither. Never replicated — see below. |

**Why `lastFired` is not replicated state.** `Time.time` is seconds since *this process* started.
Two peers that launched a minute apart hold values a minute apart for the same event, so the field
is meaningless off-machine and its only legitimate use is the local `CoolingDown()` comparison
(`Weapon.cs:311-314`), which is a difference against the same clock. The authoritative horn cooldown
lives in `MountedWeaponRuntimeState.LastFiredTime` on the **server** clock; the client's copy stays
a purely local cosmetic gate. The same reasoning applies to every `lastFired` in the weapon
hierarchy, and it is why Task 3 puts the gate in `MountedWeaponAuthority` rather than reading the
MonoBehaviour's field.

**The horn spends no ammo and spawns no projectile** — its override skips `ammo--` and
`AmmoChanged()` entirely — so it is an AI-visibility event, not a fire event. It replicates as
`S_WEAPON_FIRE` with `WeaponIds.CAR_HORN = 18` (D8) on the cosmetic channel, filtered by
`ServerEventWriter.WeaponFireAudibleRadius`.

**Verify:** `AHornHighlightsOnlyOnTheServer`; `AHornSpendsNoAmmo`; a headless-role test that
constructs the component with a null `AudioSource` and asserts `Shoot` does not throw.

---

### Task 6 — `Seat.CanUseWeapon()`: preserve the behaviour, fix the name (S, 0.5 day + a Dev A review round)

**Independent and last.** Everything above merges without it.

`Seat.CanUseWeapon()` returns `type == Type.Passenger` (`Seat.cs:84-87`), so a **Gunner** seat
returns false. That reads like a bug. It is not. The nine call sites say what it actually means:

| Site | What it gates |
|---|---|
| `Actor.cs:443` | `controller.Fire() && (!IsSeated() \|\| seat.CanUseWeapon() \|\| seat.HasMountedWeapon())` — a Gunner **does** fire, through the `HasMountedWeapon()` clause. |
| `Actor.cs:937` (`EnterSeat`) | Anyone who cannot use it gets their carried weapon **holstered** and `ik.turnBody = false`. |
| `Actor.cs:1001`, `:1018`, `:1035` | `NextWeapon` / `PreviousWeapon` / `SwitchWeapon` are refused. |
| `Actor.cs:1092` | `ControllingVehicle()` is *defined* as `!CanUseWeapon()`. |
| `FpsActorController.cs:318`, `:329` | Camera and HUD behaviour for a seat whose occupant is not holding their own gun. |

Read together, the predicate is **"may use their own carried weapon while seated"** — true for a
Passenger leaning out of a window, false for a Driver, a Pilot and a Gunner, all three of whom have
their hands on something else. The mounted weapon is reached through `HasMountedWeapon()`, which is
a different question. The behaviour is correct.

**Decision (D7): preserve the behaviour; rename to `CanUseCarriedWeapon()`.** A pure rename, one
declaration and nine call sites, zero behavioural change. The justification for touching a Dev A
file for a rename is that the current name is the trap: the next person to read `CanUseWeapon()
== false` on a Gunner seat will "fix" it, and that fix silently re-arms a seated player's rifle and
un-holsters it inside a tank.

**Verify:** `AGunnerSeatFiresItsMountedWeaponButNotACarriedOne` — pins both halves of the predicate
so the rename cannot drift into a behaviour change; the full existing suite green (rename only).

---

### Task 7 — Tests (M, 2 days, written alongside Tasks 1-6)

All engine-free, all in CI, no Editor.

| Test | Asserts |
|---|---|
| `ATurretTraversesTheSameArcAtAnyTimestep` | Brainstorm criterion 4 — 30 Hz and 144 Hz agree within one quantization step |
| `ATurretClampsOutOfRangeClientInput` | Criterion 3 — an axis of 10⁶ traverses exactly `SlewDegreesPerSecond × dt` |
| `ATurretPitchClampsToItsAuthoredLimits` | `[-40, +15]` for `MountedTurret`, joint limits for `TankTurret` |
| `ATurretPoseRoundTripsThroughTheVehicleEntry` | Encode → decode within one step; unchanged pose sets no mask bit |
| `AMountedWeaponSpendsServerAmmoAndHonoursItsOwnCooldown` | Task 3 |
| `AMountedReloadDrawsFromThePerWeaponPoolNotTheActorPool` | D6 — the distinction at `MountedWeapon.cs:26-41` |
| `AnInfiniteSpareAmmoSentinelNeverDecrements` | The `-2` sentinel (`Weapon.cs:532-535`) survives the `short` |
| `ANoResupplySentinelIsNeverRefilled` | The `-1` sentinel (`Weapon.cs:508-511`) |
| `AClientCannotFireARemoteActorsMountedWeapon` | `NetWeaponAuthority` at `NetRole.Client` |
| `AlternatingMuzzleAdvancesBeforeTheSpawnNotAfter` | Task 4 detail 1 |
| `ADecodedMuzzleIndexIsClampedByModulo` | Task 4 detail 2 |
| `AHornHighlightsOnlyOnTheServer` | Task 5 |
| `AGunnerSeatFiresItsMountedWeaponButNotACarriedOne` | D7 — the behaviour the rename must not change |
| **`OfflineMountedWeaponBehaviourIsUnchanged`** | **D9.** Drives a full fire → reload → fire cycle at `NetRole.Offline` and asserts every observable matches the pre-phase recording. Without this, the guard in `Weapon.CanFire` is a single-player regression nobody notices until someone plays offline. |

---

## 4. Acceptance criteria

1. Turret aim is identical on the server and on all clients, and slew rate is framerate-independent
   — verified by driving the same turret at 30 Hz and 144 Hz and comparing traverse over 1 s
   (brainstorm criterion 4).
2. A client that sends out-of-range turret input is clamped server-side and gains no traverse
   advantage (brainstorm criterion 3).
3. Firing a mounted weapon decrements the **server's** ammo, and a client firing faster than the
   server's cooldown is rejected with `FireRejection.OnCooldown`.
4. A mounted weapon's reload draws from that weapon's own spare pool. The Actor's 5-slot pool is
   untouched by it, and the `-1` and `-2` sentinels behave as they do today.
5. `AlternatingMountedWeapon.currentMuzzle` on a remote client matches the server's after any
   sequence of shots, and a mismatched muzzle count degrades to a wrong muzzle rather than a throw.
6. A tank firing its cannon perturbs the tank's rigidbody on the server, and remote clients see the
   resulting motion through the vehicle entry's velocity — not through a locally re-rolled impulse.
7. Sounding the horn reveals the occupant to AI exactly once, on the server.
8. A headless server survives turret traverse, mounted fire, reload and seat change with **zero
   NREs** (brainstorm criterion 11, this phase's slice of it).
9. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-tick allocation
   in any new logic file (brainstorm criterion 12).
10. Offline single-player mounted-weapon behaviour is unchanged (D9).
11. `AssertCleanState()` passes across five back-to-back matches including the mounted-weapon
    registry (brainstorm criterion 13, this phase's slice).

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| The `Weapon.CanFire()` guard changes offline single-player behaviour | 3 | 5 | **15** | `NetRole.Offline` is an explicit early `true`, and `OfflineMountedWeaponBehaviourIsUnchanged` pins a full fire→reload→fire cycle against a pre-phase recording. This is the phase-05 Task 6 shape, which is precedent rather than invention. |
| Dev A's prefab wiring (turret references, `NetTurret` attachment, the CAR HORN registry row) slips | 3 | 3 | 9 | Tasks 1, 3 and 4 are all gradable in CI with no prefab. Only the two-client Editor check needs Dev A, and it is the last thing. `SpecChecker` fails loudly on the missing registry name rather than shipping a silent id mismatch. |
| Mounted spare ammo double-spends against the Actor pool | 3 | 3 | 9 | D6's `ISpareAmmoPool` makes the two owners *structurally* different objects rather than two branches in one method, and `AMountedReloadDrawsFromThePerWeaponPoolNotTheActorPool` grades it. |
| `WeaponRuntimeState` gaining `SpareAmmo` breaks phase-05's combat tests | 3 | 2 | 6 | The field defaults to the infinite sentinel, so every phase-05 test keeps its current behaviour; only `ServerReloadPolicy`'s signature moves, and its call sites are two. |
| A client's decoded `currentMuzzle` indexes past a shorter local `muzzles` array | 2 | 4 | 8 | Modulo on decode (Task 4 detail 2), graded by `ADecodedMuzzleIndexIsClampedByModulo`. |
| A second turret on the same vehicle diverges visually (D3) | 3 | 2 | 6 | Bounded to cosmetics by construction: V7's `S_PROJECTILE_SPAWN` carries a server-computed origin, so no shot ever reads a remote transform. Recorded in D3 rather than discovered later. |
| Turret prediction for the local gunner oscillates against the correction | 2 | 4 | 8 | D5-local — the replicated quantity is the joint *target*, a PhysX input, not a PhysX output. Both sides run the identical `TurretAimPolicy` on the identical clamped axes, so the steady-state error is one quantization step. |
| `TankTurret.SpawnProjectile`'s impulse is applied on both sides and doubles | 2 | 4 | 8 | The call is inside the server-only half of the `Shoot` split (Task 3). A test asserts the client role applies zero impulse. |
| Adding `WeaponIds.CAR_HORN` collides with a concurrent id assignment in another branch | 2 | 3 | 6 | Ids are append-only and `SpecChecker` compares against the spec table and the prefab on every CI run, so a collision is a red build, not a runtime mystery. |

One score reaches 15. Its mitigation — an explicit offline no-op plus the recording test — is a
**precondition of starting Task 3**, not a follow-up.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — Engine-free turret aim | M (2d) | No dependencies beyond V4. Start here. |
| 2 — Unity turret seam | M (2d) | Needs 1. Closes two § 3.6 headless NREs. |
| 3 — Fire authority at the chokepoint | L (3d) | Needs 2 — the ordering note is why. **V7 unblocks here.** |
| 4 — `currentMuzzle` replication | S (1d) | Needs 3's encoder work; otherwise independent. |
| 5 — `CarHorn` | S (0.5d) | Independent of 1-2; needs 3's role split. |
| 6 — `CanUseWeapon` rename | S (0.5d) + review round | Severable, last, Dev A file. |
| 7 — Tests | M (2d) | Written alongside 1-6, not after. |
| **Total** | **~2 weeks** | Critical path: **1 → 2 → 3**. Tasks 4, 5 and 6 are off it. |

---

## 7. Handoff

To **Dev A**, in one PR per file, each with its pinning test attached:

- `TankTurret.cs`, `MountedTurret.cs` — the `Update` bodies and the `Unholster`/`Holster` guards
  (Task 2).
- `Weapon.cs` — one leading term in `CanFire()` and the role split inside `Shoot()` (Task 3).
- `AlternatingMountedWeapon.cs` — the field visibility change (Task 4).
- `CarHorn.cs` — the role split (Task 5).
- `Seat.cs` + nine call sites — the `CanUseCarriedWeapon()` rename (Task 6, severable).

Editor-only work that stays with Dev A: attaching `NetTurret` to every turret prefab and authoring
its `TurretAimLimits`; adding the `CAR HORN` row to the weapon registry in `_Managers.prefab` so
`SpecChecker` passes on `WeaponIds.CAR_HORN = 18`; the two-client turret-parity check.

To **V7**: the muzzle transform is now authoritative at the tick `Shoot` runs, which is the
precondition `S_PROJECTILE_SPAWN`'s origin field depends on. `MountedWeaponAuthority`,
`ISpareAmmoPool` and the `WeaponRuntimeState.SpareAmmo` field are all reused there — V7's deployable
resupply writes through the same pool.

To **V9**: the mounted-weapon registry is a new id space and joins `AssertCleanState()`.
