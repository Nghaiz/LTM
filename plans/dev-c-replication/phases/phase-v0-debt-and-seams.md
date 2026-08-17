# Dev C — Phase V0: Determinism debt, headless safety, and the seams the vehicle stream needs

> Design of record: [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md).
> Read it first. This phase implements its § 6 row **V0**; the decisions in its § 4 are settled and
> are not re-opened here.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files), engine-free logic
> in `Ironfront.Net.Replication` with Unity holding only a thin seam, C# 9 in `Assets/`.
>
> **Every line number below was read from the working tree at `1544e8f`, not recalled.** Re-verify
> with the greps in the design doc § 10.

---

## 1. Objectives

V0 is the load-bearing phase. Nothing in V3–V7 converges without it, for one reason: **two peers
running the same input at different framerates currently produce different vehicle state**, so a
prediction blend has nothing stable to converge onto, and a headless server does not survive long
enough to argue about it.

By the end of this phase:

1. Vehicle drive code runs at a fixed timestep on every peer. `Car` no longer pushes render-rate
   torque into a fixed-rate solver; `Helicopter`'s `rotorSpeed` — which multiplies every force in
   `FixedUpdate` — no longer integrates at render rate.
2. Turret slew is framerate-independent and reads from an **authoritative `yaw`/`pitch` field** that
   an external caller can set. Today there is no such field and no setter of any kind: both
   `GetInput()` methods are `private` and read `Input.GetAxis` directly inside `Update`.
3. `Vehicle` health can be written authoritatively, and `Damage` carries who did it. Both are
   signature changes, and both are prerequisites for V4's damage sink.
4. Client vehicle input is clamped **server-side** on every vehicle, including `NaN`.
5. Four latent gameplay bugs that predate the netcode are fixed: `Boat`'s world-vs-local torque
   axis, the `AutoDamage` double-schedule, the explosion falloff range mismatch, and the 0.5 s
   wall-clock coroutine that races every seat change.
6. A dedicated headless server survives vehicle spawn, damage, death and respawn without an NRE.
7. The pure parts of all of the above live in `Ironfront.Net.Replication/Vehicles/` and are graded
   by `dotnet test` in CI, with no Unity Editor.

**Zero wire change.** No opcode, no message, no `PROTOCOL_VERSION` bump, no `SpecChecker` edit.
Anything that needs a byte on the wire is V3's, and saying so is part of this phase's job — see
§ 2 D6.

**Not in this phase:** vehicle snapshots, seat arbitration, projectiles, explosions-on-the-wire,
weapon configs, capture points. Those are V1–V8.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **D1** | **Fix the debt, do not work around it.** Design-doc D8. The consequence is stated and accepted, not hidden: offline single-player handling **changes**. See § 7. |
| **D2** | **The pure math moves to `Ironfront.Net.Replication/Vehicles/`; Unity keeps a thin caller.** Same rule as phase-05 D2. Turret slew, tick timers, input clamping and explosion range policy are arithmetic, and arithmetic written inside a `MonoBehaviour` is arithmetic that can only be tested by opening the Editor. PhysX itself does **not** move — design-doc § 3.2 settled that. |
| **D3** | **Turret aim is a stored `float yaw` / `float pitch` that the joint is driven FROM, not accumulated INTO.** Today `TankTurret` reads `towerJoint.targetRotation.eulerAngles` back out of the joint and writes a delta into it (`TankTurret.cs:29-33`), and `MountedTurret` does the same through `towerTransform.localEulerAngles` (`MountedTurret.cs:19-24`). A value that lives only inside an engine object cannot be snapshotted, cannot be set by a server, and round-trips through `Quaternion.eulerAngles` — which is not injective. The field is the source of truth; the joint is an output. |
| **D4** | **Aim integration happens at fixed timestep; visual application may stay per-frame.** `TankTurret` drives physics joints, so both halves go to `FixedUpdate`. `MountedTurret` drives plain `Transform`s with no rigidbody, so it integrates in `FixedUpdate` and *applies* in `Update`, reading the same field. Splitting them keeps 144 Hz aiming smooth without making the authoritative value framerate-dependent. |
| **D5** | **Input clamping is server-authoritative and rejects `NaN`.** `Mathf.Clamp(float.NaN, -1f, 1f)` returns `NaN`, so the existing `Vehicle.Clamp2` (`Vehicle.cs:416`) is not a validation boundary — it is a range limiter that a hostile client walks straight through. The engine-free clamp treats non-finite as zero. |
| **D6** | **V0 stops at the seam.** Every task here ends at a public field, a setter, or a pure function. No task adds a message, a field to an existing message, or a caller of `ServerEventWriter`. If a task appears to need one, it is mis-scoped and belongs in V3. |
| **D7** | **Assembly-CSharp fixes are graded by source-invariant tests, and we say plainly that these are weaker than behavioural tests.** `Assembly-CSharp` does not compile under `dotnet test`, so a behavioural assertion on `Car.FixedUpdate` requires Unity. A test that reads the `.cs` file as text and asserts the invariant (`Car` has no `Update()` driving wheels; turret slew multiplies by a delta-time; `Boat`/`Tank` input passes through a clamp) pins the regression in CI at near-zero cost. It proves the *shape*, not the *behaviour*. Behaviour is Dev A's two-client Editor run. Both are listed in § 4. |
| **D8** | **`Vehicle` keeps `private float health`; the setter and `Damage` both route through one private `ApplyHealth`.** Two write paths that each run their own burning/particle ladder is exactly the derived-state divergence `development-principles.md` § "No Derived Fields" forbids, and it is what phase-05 D9 already removed once for `NetServerActor.Health`. |
| **D9** | **Attacker identity is a plain `int` actor id with `-1` meaning "none", declared on `Vehicle`.** Not a `ushort`, not a netcode type: `Assembly-CSharp` must not gain a compile-time dependency on the replication library's id width to fix a pre-existing bug. V4's damage sink does the narrowing at its own seam, where the mapping already lives. |
| **D10** | **`Actor.cs` is opened exactly once, in this phase, early.** Design-doc § 9 scores "`Actor.cs` conflicts with Dev A's branch" at 12 and its mitigation is sequencing. Task 8 is the only `Actor.cs` change in the whole V-track plan; it is announced in the PR before Dev A opens the file. |

---

## 3. Detailed tasks

### Task 1 — The engine-free vehicle seam (M, 1.5 d)

Everything pure that Tasks 2–9 need, in one place, so the Unity edits that follow are one-line
calls rather than inline arithmetic.

**Files (all new), `Ironfront.Net.Replication/Vehicles/`:**

| File | Contents |
|---|---|
| `TurretAimState.cs` | `struct TurretAimState { public float Yaw; public float Pitch; }` — the authoritative pair. Yaw wraps to `[0, 360)`; pitch does not wrap. |
| `TurretAimLimits.cs` | `struct TurretAimLimits { public float YawRateDegPerSec; public float PitchRateDegPerSec; public float PitchMin; public float PitchMax; }`. Replaces the two bare `MAX_TURN_DELTA` constants (`TankTurret.cs:5` = 5, `MountedTurret.cs:5` = 10) with named, per-turret data — and makes explicit that those numbers were **degrees per frame**, which is the bug. |
| `TurretAimCore.cs` | `public static void Step(ref TurretAimState state, float yawInput, float pitchInput, in TurretAimLimits limits, float dt)`. Clamps each input to `[-1, 1]` via `VehicleInputClamp`, integrates `rate * input * dt`, wraps yaw, clamps pitch to `[PitchMin, PitchMax]`. Pure, no allocation, no branching on engine state. |
| `VehicleInputClamp.cs` | `public static float Axis(float v)` → `0f` when `float.IsNaN(v) \|\| float.IsInfinity(v)`, else clamped to `[-1, 1]`. Plus `Magnitude(float x, float y, float max)` for the `Vector2.ClampMagnitude` case at `MountedTurret.cs:18`. This is the D5 validation boundary. |
| `TickTimer.cs` | `struct TickTimer { public int TicksRemaining; public void Arm(int ticks); public bool Tick(); }` — `Tick()` decrements and returns `true` on the transition to zero, `false` otherwise and on an already-expired timer. Replaces `WaitForSeconds` where the wait gates authoritative state. |
| `ExplosionRanges.cs` | `struct ExplosionRanges { public float DamageRange; public float BalanceRange; }` with `public bool TryGetDamageT(float distance, out float t)` — returns `false` (no damage at all) when `distance >= DamageRange`, else `t = distance / DamageRange`. And `public float GetBalanceT(float distance)` → `distance / BalanceRange` clamped. This is the Task 7 policy, isolated from `AnimationCurve`, which cannot leave Unity. |

**Constraints.** No `System.Linq`, no `foreach`, no allocation in any method, no `UnityEngine`
reference anywhere in the folder. `TurretAimCore.Step` takes `ref`/`in` so no struct is copied per
call.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter "FullyQualifiedName~Vehicles"` —
green once Task 10's first four tests land.

---

### Task 2 — Framerate coupling in `Car` and `Helicopter` (M, 1 d)

The two of the three couplings in design-doc § 3.3 that are pure timestep problems. The third
(turret slew) is Task 3, because it also needs the authoritative field.

**`Car.cs` — `Update()` at `:88` splits in two.**

`Car` never overrides `FixedUpdate`; all of its drive code lives in `Update` and writes
`WheelCollider.motorTorque` / `.brakeTorque` / `.steerAngle` (`:111`, `:114`, `:119`, `:125`) plus
a `Mathf.MoveTowards` steer integration scaled by `Time.deltaTime` (`:105`). PhysX reads those
values once per fixed step, so at 144 Hz the solver sees the last of ~2.4 writes per step and at
30 Hz it sees a value integrated over a step it never took.

| Moves to `protected override void FixedUpdate()` | Stays in `Update()` |
|---|---|
| The `HasDriver() && !burning` block, `:94-128` — input read, wheel-RPM average, `steerAngle` integration (now `Time.fixedDeltaTime`), all four collider writes | `steeringWheel.localEulerAngles` from `steerAngle`, `:90-92` — cosmetic, reads the field |
| — | `enginePitch` / `audio.pitch`, `:129-134` — cosmetic |

`FixedUpdate` must call `base.FixedUpdate()` first, matching `Boat.cs:44`, `Tank.cs:56` and
`Helicopter.cs:84`; `Vehicle.FixedUpdate` (`:163`) owns ram-checking and the burn countdown and
must not be skipped. `target` (`:93`) is computed in the drive block, so the audio line reads it
from a new private field rather than a local.

**`Helicopter.cs` — `rotorSpeed` integration moves out of `Update()` at `:49`.**

`rotorSpeed` is multiplied into **every** force in `FixedUpdate` (`:93`), so integrating it at
render rate makes lift itself framerate-dependent — the single largest divergence source in the
vehicle set.

| Moves to `FixedUpdate` (before `base.FixedUpdate()` consumers) | Stays in `Update()` |
|---|---|
| `rotorSpeed` integration, `:55` and `:63` — `Time.deltaTime * 0.3f` becomes `Time.fixedDeltaTime * 0.3f` | `audio.volume` / `audio.pitch`, `:51-52` |
| The inverted-flight `Damage(Time.deltaTime * 30f)`, `:56-59` — see below | `solidRotor` / `blurredRotor` toggle, `:65-67` |
| `isAirborne` raycast, `:69` — read by `ShouldBeAvoided()` (`:129`), which AI consults; a physics query belongs at physics rate | `rotor.Rotate(...)`, `:68` — cosmetic spin, correctly per-frame |

**The inverted-flight damage path is damage-per-frame and is a gameplay bug, not just a
determinism one.** At `Helicopter.cs:56-59`, a driven helicopter whose `transform.up.y < 0` takes
`Time.deltaTime * 30f` **per rendered frame** — nominally 30 HP/s, but the rate is exactly right
only because `deltaTime` sums to one second per second. It becomes wrong the moment the call is
made from anywhere else, and it fires at render rate on a client that has no damage authority.
Moving it to `FixedUpdate` with `Time.fixedDeltaTime * 30f` makes it 30 HP/s at a fixed rate on
every peer. It also becomes a **server-only** call in V4; V0 does not add that guard (D6), but the
move is what makes adding it a one-line change.

`Tank.cs` needs no timestep change: its drive code is already in `FixedUpdate` (`:54-61`) and
`UpdateTrack`'s `Time.deltaTime` (`:129`) is a UV scroll in `Update`, which is correct.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter "CarDriveCodeIsNotInUpdate|HelicopterRotorSpeedIsNotIntegratedInUpdate"` (Task 10, source-invariant per D7). Behavioural
confirmation is Dev A's Editor run — § 7.

---

### Task 3 — Authoritative turret aim, and the setter that does not exist today (M, 1.5 d)

Both turrets accumulate their aim *into* an engine object and read it back out next frame, and
neither exposes any way for a non-local caller to aim them. `GetInput()` is `private` in both
(`TankTurret.cs:58`, `MountedTurret.cs:48`) and reads `Input.GetAxis("Mouse X")` plus
`OptionsUi.GetOptions()` directly (`TankTurret.cs:66`, `MountedTurret.cs:56`) — bypassing
`ActorController` entirely. There is no abstract member for turret aim anywhere in the hierarchy.

**Shared shape, both files:**

```csharp
// authoritative, replicated in V4; the joint/transform is an OUTPUT of this
private TurretAimState _aim;
public  TurretAimLimits aimLimits;          // serialized, per-prefab; Dev A fills

public float Yaw   { get { return _aim.Yaw;   } }
public float Pitch { get { return _aim.Pitch; } }

/// Server/replication entry point. V0 adds it; V4 and V6 are its only callers.
public void SetAim(float yaw, float pitch) { _aim.Yaw = yaw; _aim.Pitch = pitch; }
```

**`TankTurret.cs`:**

- `Update()` (`:23`) → `protected override void FixedUpdate()`. It drives a `ConfigurableJoint`
  and a `HingeJoint` spring; those are physics and belong at physics rate (D4). Keep the
  `base.Update()` call the class currently makes at `:25` — `Weapon.Update` (`Weapon.cs:179`) owns
  fire timing and must keep running per-frame, so it stays behind in an `Update()` override that
  does nothing else.
- Replace the read-back at `:29-33`. Today: `eulerAngles = towerJoint.targetRotation.eulerAngles`,
  then `eulerAngles.z = Mathf.Clamp(eulerAngles.z - input.x, eulerAngles.z - 5f, eulerAngles.z + 5f)`
  — a clamp whose bounds are derived from the value being clamped, which is a no-op guard, and
  which round-trips through `Quaternion.eulerAngles`. After: `TurretAimCore.Step(ref _aim, -input.x, -input.y, aimLimits, Time.fixedDeltaTime)`, then
  `towerJoint.targetRotation = Quaternion.Euler(0f, 0f, _aim.Yaw)` and
  `spring.targetPosition = _aim.Pitch`.
- `aimLimits.PitchMin` / `PitchMax` are seeded from `cannonJoint.limits.min` / `.max` in `Awake`,
  preserving the clamp at `:32` exactly. The `5f` from `MAX_TURN_DELTA` becomes
  `YawRateDegPerSec = 300f` / `PitchRateDegPerSec = 300f` — 5 °/frame × 60 fps, i.e. the rate the
  original game exhibits at its design framerate. **This number is the accepted D8 behaviour
  change**: it is now what a 144 Hz client gets too.
- `GetInput()` becomes `protected virtual`. Its body is unchanged in V0; V4 overrides the source.

**`MountedTurret.cs`:**

- Integration moves to a new `protected override void FixedUpdate()`; the transform write stays in
  `Update()` reading `_aim` (D4 — no rigidbody here, so per-frame application is free smoothness).
- `Vector2.ClampMagnitude(GetInput(), 10f)` (`:18`) becomes
  `VehicleInputClamp.Magnitude(...)` feeding `TurretAimCore.Step`, with
  `YawRateDegPerSec = PitchRateDegPerSec = 600f` (10 °/frame × 60 fps).
- `PitchMin = -40f`, `PitchMax = 15f` — the literals currently inline at `:23`. They move to the
  serialized `aimLimits`, so per-prefab tuning becomes data.
- The `Mathf.DeltaAngle(0f, localEulerAngles2.x - vector.y)` dance at `:23` disappears: it exists
  only to recover a signed angle from a `localEulerAngles` read-back that no longer happens.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter "TurretSlewIsFramerateIndependent"` — steps `TurretAimCore` for 1 simulated second at `dt = 1/30` and at `dt = 1/144` with identical
input and asserts the traverse matches within 1e-4°. This is design-doc acceptance criterion 4,
graded in CI rather than by two Editor sessions.

---

### Task 4 — The `Vehicle` damage channel (M, 1 d)

Three changes in `Vehicle.cs`, all prerequisites for V4's damage sink and none of them transport.

**4a. An authoritative health write (`:60`, `:261-276`).** `private float health` has no setter, so
a replicated HP value has nowhere to land. Per D8, add one write path and route both callers
through it:

```csharp
private float health;                                   // :60, unchanged
public  float Health      { get { return health; } }
public  float MaxHealth   { get { return maxHealth; } }

/// The only place health changes. Runs the burning + particle ladder exactly once.
private void ApplyHealth(float newHealth, float appliedDamage, int attackerActorId) { ... }

/// Server/replication entry point. V4 is its only caller.
public void SetHealthAuthoritative(float value) { ApplyHealth(Mathf.Clamp(value, 0f, maxHealth), 0f, NoAttacker); }
```

`Damage` becomes a thin wrapper computing `health - amount` and delegating. The ladder inside
`ApplyHealth` is the existing one, unchanged in behaviour: `amount > 900f → HeavyDamage()`
(`:264-267`), `health <= 0 && !dead && !burning → StartBurning()` (`:268-271`), and the
`damageParticles` threshold at `:272-275`. `GetHealthRatio()` (`:518`) stays and now reads through
the same field. Note `SetHealthAuthoritative` passes `appliedDamage = 0f` deliberately —
`HeavyDamage()` is a *local* screenshake keyed off a damage magnitude, and a corrective snapshot is
not a hit.

**4b. Attacker identity on `Damage` (`:261`).** Today the signature is one `float`: no direction,
no impact point, no attacker. Per D9:

```csharp
public const int NoAttacker = -1;

public void Damage(float amount) { Damage(amount, NoAttacker); }      // every existing caller
public void Damage(float amount, int attackerActorId) { ... }
```

Keeping the one-argument overload means the existing call sites compile untouched:
`AutoDamage` (`:245`), `OnCollisionEnter` (`:372`), `Helicopter`'s inverted-flight path
(`Helicopter.cs:58`), and `ActorManager.Explode` (`ActorManager.cs:368`). Only the last of those
has an attacker to pass, and it gains one in **V1**, not here — V0 opens the parameter, it does not
thread it (D6). Store the last attacker in a `private int _lastDamagedBy` so V4's death event has
something to read.

**4c. `Time.deltaTime` inside `FixedUpdate` (`:175`).** The burn countdown reads
`burnTime -= Time.deltaTime` from inside `Vehicle.FixedUpdate`. Unity happens to return
`fixedDeltaTime` from `Time.deltaTime` during the fixed loop, so this is correct today **by
accident** and silently wrong the moment the burn tick moves — which V4 does when it drives the
countdown from the 30 Hz netcode accumulator. Change to `Time.fixedDeltaTime`. Zero behaviour
change today; that is the point.

While here, record but do **not** fix: `burnTime` (`:52`) is simultaneously the serialized designer
default and the live countdown, so replicating the prefab value does not replicate the timer. That
is a V4 problem — it needs a second field, and adding one now would be a field V0 has no caller for.

**Verify:** solution compiles; `dotnet test Ironfront.Net.Replication.Tests --filter "VehicleHasSingleHealthWritePath|VehicleDamageCarriesAttacker"` (source-invariant, D7).

---

### Task 5 — Server-side input clamping, and `Boat`'s torque axis (S, 0.5 d)

**5a. Neither `Boat` nor `Tank` clamps.** `Car.cs:96` wraps its input in `Vehicle.Clamp2` and
`Helicopter.cs:93` in `Clamp4`. `Boat.cs:60` reads `Driver().controller.BoatInput()` raw, and
`Tank.cs:86` reads `Driver().controller.CarInput()` raw — then multiplies straight into
`AddForce`/`AddRelativeTorque` (`Boat.cs:65-66`) and `motorTorque` (`Tank.cs:90-91`). A client that
sends `10.0` on an axis gets ten times the thrust; one that sends `NaN` propagates `NaN` into the
rigidbody and removes the vehicle from the simulation entirely.

Wrap both reads. Per D5 the clamp is `VehicleInputClamp.Axis` (non-finite → `0`), not the existing
`Vehicle.Clamp2` (`:416`), which passes `NaN` through unchanged because `Mathf.Clamp` does.
`Vehicle.Clamp2` and `Clamp4` are re-implemented over `VehicleInputClamp.Axis` so all four vehicles
share one boundary and `Car`/`Helicopter` gain the `NaN` rejection for free.

**5b. `Boat.cs:66` applies a world-space axis as a local one.**

```csharp
rigidbody.AddRelativeTorque(base.transform.up * turnSpeed * vector.x, ForceMode.Acceleration);
```

`AddRelativeTorque` interprets its argument **in the body's local space**, but `transform.up` is a
**world-space** vector. The two coincide only while the boat is perfectly level and unrotated in
yaw; the moment it turns or rolls, steering torque leaks into pitch and roll. The line above it
(`:65`) is correct — it uses `AddForce`, which *is* world-space. Fix: `Vector3.up * turnSpeed * vector.x`.

This changes boat handling in offline play. That is D1/D8, accepted and recorded.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter "ClampRejectsNonFinite|BoatUsesLocalTorqueAxis"`.

---

### Task 6 — The `AutoDamage` double-schedule (S, 0.25 d)

`Vehicle.Repair` schedules the abandoned-vehicle decay timer **unconditionally**:

```csharp
// Vehicle.cs:325-326
CancelInvoke("AutoDamage");
InvokeRepeating("AutoDamage", 50f, 2f);
```

`AutoDamage` (`:243-246`) is meant to decay *unoccupied* vehicles: `OccupantEntered` cancels it
(`:203`), `OccupantLeft` schedules it only `if (IsEmpty())` (`:236-238`). `Repair` ignores both
conditions. So:

1. Player enters → `CancelInvoke` (`:203`), nothing pending.
2. Player repairs while seated → `Repair` schedules a repeating invoke on an **occupied** vehicle.
3. Player leaves → `OccupantLeft` sees `IsEmpty()` and schedules a **second** one, without
   cancelling the first.

`InvokeRepeating` stacks, so the vehicle now decays at `2 × 7% of maxHealth` every 2 s. Repeat the
cycle and it stacks again. On a server where bots enter, repair and leave vehicles continuously,
this is unbounded.

Fix: `Repair` only re-arms when the vehicle is actually empty, and `OccupantLeft` cancels before it
schedules.

```csharp
// Vehicle.Repair, replacing :325-326
CancelInvoke("AutoDamage");
if (IsEmpty()) InvokeRepeating("AutoDamage", AUTO_DAMAGE_START_TIME, AUTO_DAMAGE_PERIOD);
```

While here, replace the magic `50f` / `2f` / `0.07f` at `:238`, `:245` and `:326` with the constants
already declared and unused at `:16-20` (`AUTO_DAMAGE_START_TIME`, `AUTO_DAMAGE_PERIOD`,
`AUTO_DAMAGE_PERCENT`). No behaviour change; it removes the possibility of the two call sites
drifting apart, which is how this bug got in.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter "RepairDoesNotStackAutoDamage"` (source-invariant: the `InvokeRepeating` in `Repair` is guarded by an emptiness test, and the
literals are gone).

---

### Task 7 — The 6 m / 9 m explosion falloff mismatch (S, 0.5 d)

`ActorManager.Explode` (`ActorManager.cs:341-373`) queries with one radius and normalizes the
falloff with another:

```csharp
List<Actor> list = ActorsInRange(point, configuration.balanceRange);           // :343  — 9 m
float num = configuration.damageFalloff.Evaluate(
                Mathf.Clamp01(magnitude / configuration.damageRange));         // :349  — 6 m
```

`Mathf.Clamp01` **saturates** rather than excludes. An actor at 8 m gets `t = 8/6 = 1.33 → 1.0` and
receives `damageFalloff.Evaluate(1.0)` — identical to an actor at 6.001 m. Whatever the curve's
endpoint value is, the 6–9 m band is a flat plateau at it, not a falloff, and the actual damage
cut-off is `balanceRange`, not `damageRange`. The vehicle loop immediately below gets this right:
it tests `if (num3 < configuration.damageRange)` first (`:365`) and *then* evaluates. The actor loop
does not.

Fix: route both loops through `ExplosionRanges` from Task 1.

```csharp
// per actor, replacing the bare Clamp01 at :349
float t;
if (!ranges.TryGetDamageT(magnitude, out t)) { /* balance + force only, no damage */ }
else { float num = configuration.damageFalloff.Evaluate(t); ... }
```

Balance damage and knockback still apply across the full `balanceRange` — that is the existing and
correct intent of the wider query radius (`:350` already normalizes balance against `balanceRange`).
Only the *damage* term gains the cut-off the vehicle loop always had.

**Deliberately out of scope, recorded for V1:** `ActorsInRange` returns an allocated `List<Actor>`
(`:343`), `instance.vehicles.ToArray()` allocates per explosion (`:361`), and both loops are
`foreach`. `Explode` becomes a server-authoritative per-tick path in V1 and those allocations are
V1's to remove, along with threading the attacker id Task 4b opened. Fixing them here would mean
rewriting a method V1 rewrites anyway.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter "ExplosionDamageStopsAtDamageRange"` — asserts `TryGetDamageT` returns `false` at and beyond `DamageRange` and a strictly increasing
`t` inside it.

---

### Task 8 — `ReactivateCollisionsWith`: 0.5 s of wall clock becomes a tick count (S, 0.5 d)

`Actor.LeaveSeat` (`:955-983`) ends by starting a coroutine that holds hitbox layer state across a
wall-clock wait and then re-reads seat state:

```csharp
// Actor.cs:985-997
private IEnumerator ReactivateCollisionsWith(Vehicle vehicle)
{
    yield return new WaitForSeconds(0.5f);
    bool reenteredThatVehicle = IsSeated() && seat.vehicle == vehicle;   // :988
    if (vehicle != null && !reenteredThatVehicle) { /* hitboxes → layer 8 */ }
}
```

The read at `:988` is a race against every network-driven seat change: a `S_SEAT_CHANGE` arriving
inside that 0.5 s window decides whether the actor's hitboxes come back, and a paused or
time-scaled server changes the window's length. It is also unowned — nothing can cancel it, and
`LeaveSeat` starts a fresh one on every exit.

Replace with a `TickTimer` field decremented in `Actor.FixedUpdate` (which already exists,
`Actor.cs:336`):

- `LeaveSeat` stores the vehicle in `_collisionReactivateTarget` and calls
  `_collisionReactivateTimer.Arm(ReactivateCollisionTicks)`.
- `Actor.FixedUpdate` calls `Tick()`; on the transition to zero it runs the existing body.
- Re-entering a seat **cancels** the timer explicitly in `EnterSeat`, which is what `:988`'s
  re-check was approximating. Cancelling on the state change is deterministic; sampling the state
  0.5 s later is not.
- `ReactivateCollisionTicks` = 25 at the current 50 Hz fixed step, preserving today's 0.5 s. It is a
  named constant, not a literal, so V4 can retune it against the netcode's 30 Hz accumulator
  without hunting for a magic number.

This is the whole of `Actor.cs` for the entire V-track (D10). It is announced in the PR title.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter "TickTimerFiresExactlyOnceOnTheArmedTick"` for the primitive, plus the source invariant that `Actor.cs` contains no
`WaitForSeconds` in `ReactivateCollisionsWith`. Behavioural confirmation (leave a vehicle, re-enter
inside the window, verify hitboxes) is on Dev A's Editor list — § 7.

---

### Task 9 — Headless NRE guards (M, 0.75 d)

Every site in design-doc § 3.6, each an unguarded dereference of a field that is either
`null`-by-design in a stripped headless build or simply optional on a prefab. A dedicated server
NREs on vehicle spawn today, so **nothing downstream of V0 can even be run** until these land.

| # | Site | Call | Guard |
|---|---|---|---|
| 1 | `VehicleSpawner.cs:33` | `GetComponent<Renderer>().enabled = false` | `var r = GetComponent<Renderer>(); if (r != null) r.enabled = false;` |
| 2 | `VehicleSpawner.cs:49` | `GameManager.instance.noVehicles` | `if (GameManager.instance == null \|\| !GameManager.instance.noVehicles)` — preserve "spawn unless suppressed" |
| 3 | `Vehicle.cs:274` | `damageParticles.Play()` | null check, matching the `burnParticles` guard already at `:282` |
| 4 | `Vehicle.cs:323` | `damageParticles.Stop()` | same |
| 5 | `Vehicle.cs:374-376` | `impactAudio.transform` / `.pitch` / `.Play()` | one guard around all three |
| 6 | `Vehicle.cs:389-393` | `deathParticles.Play()`, `audio.Stop()/pitch/volume`, `explosionSound.Play()` | three separate guards — `rigidbody` force at `:386-388` must still run, it is gameplay |
| 7 | `Helicopter.cs:44-45` | `rotor.GetComponent<Renderer>()`, `rotor.GetChild(0).GetComponent<Renderer>()` | guard `rotor`, then each renderer |
| 8 | `Helicopter.cs:66-67` | `solidRotor.enabled` / `blurredRotor.enabled`, dereferenced **every frame** | guard both; #7 leaves them null on a headless build |
| 9 | `Vehicle.cs:542` | `ActorManager.instance.debug && !dead && Camera.main != null` — `instance` is dereferenced **before** the `Camera.main` guard on the same line | reorder: `if (ActorManager.instance != null && ActorManager.instance.debug && ...)` |
| 10 | `Vehicle.cs:252` | `spawner.FirstDriverEntered(this)` in `DriverEntered()` | **Found in this audit, not in § 3.6.** `spawner` (`:68`) is only set by `SetSpawner` (`:411`), which only `VehicleSpawner.SpawnCoroutine` (`:62`) calls — so any vehicle placed directly in a scene NREs the first time a driver enters it. Guard `spawner != null`; `reportedFirstDriver` still latches. |

Rule for all ten: the guard protects a **cosmetic** call. Where a guarded block also contains
gameplay (#6's explosion impulse), the gameplay stays outside the guard. Where the field is
genuinely required for gameplay, it is not guarded — it is a prefab error and should throw.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter "HeadlessDereferencesAreGuarded"` — the source invariant enumerates the ten sites by pattern and fails on an unguarded match. The
real gate is design-doc acceptance criterion 11 (headless spawn → damage → death → respawn with
zero NREs), which needs a running server and is graded in V9.

---

### Task 10 — Tests (M, 1.5 d, written alongside Tasks 1–9)

All in `Ironfront.Net.Replication.Tests`, all `dotnet test`, no Editor.

**Behavioural, over the Task 1 engine-free types** — these are real tests:

| Test | Asserts |
|---|---|
| `TurretSlewIsFramerateIndependent` | 1 s of identical input at `dt = 1/30` and `dt = 1/144` traverses the same angle to 1e-4°. **Design-doc criterion 4, in CI.** |
| `TurretPitchClampsAndYawWraps` | pitch saturates at `PitchMin`/`PitchMax`; yaw crosses 360→0 without a discontinuity |
| `TurretAimIsDrivenFromTheFieldNotAccumulated` | `SetAim` followed by `Step(dt: 0)` leaves the state exactly as set — the property D3 exists for |
| `ClampRejectsNonFinite` | `NaN`, `+∞`, `−∞` → `0f`; `10f` → `1f`; `-0.5f` unchanged |
| `TickTimerFiresExactlyOnceOnTheArmedTick` | `Arm(25)` → `false` × 24, `true` on 25, `false` after; `Arm(0)` never fires |
| `ExplosionDamageStopsAtDamageRange` | `TryGetDamageT` false at `DamageRange` and beyond; `t` strictly increasing inside; balance `t` normalizes over `BalanceRange` |

**Source-invariant, over `Assembly-CSharp/*.cs` read as text** — per D7 these prove shape, not
behaviour, and the file says so in its class comment. One file,
`VehicleSourceInvariantTests.cs`, resolving the repo root by walking up from
`AppContext.BaseDirectory` to the directory containing `Ironfront.sln`.

| Test | Asserts |
|---|---|
| `CarDriveCodeIsNotInUpdate` | `Car.cs` declares `FixedUpdate`; no `motorTorque`/`brakeTorque`/`steerAngle` write appears inside `Update()` |
| `HelicopterRotorSpeedIsNotIntegratedInUpdate` | no `rotorSpeed +=`/`Mathf.Clamp01(rotorSpeed` inside `Update()`; no `Damage(` inside `Update()` |
| `TurretSlewUsesADeltaTime` | `TankTurret.cs` and `MountedTurret.cs` each contain `Time.fixedDeltaTime` — design-doc § 10's `grep -n "Time.deltaTime" TankTurret.cs MountedTurret.cs` returning **zero** is the bug this pins |
| `TurretsExposeAPublicAimSetter` | both files declare `public void SetAim(` |
| `VehicleInputIsClampedOnEveryVehicle` | `Boat.cs` and `Tank.cs` input reads pass through `Clamp2`; no raw `controller.BoatInput()`/`CarInput()` reaches a force call |
| `BoatUsesLocalTorqueAxis` | `Boat.cs` contains no `AddRelativeTorque(base.transform.` |
| `VehicleHasSingleHealthWritePath` | exactly one `health =` assignment in `Vehicle.cs`, inside `ApplyHealth` |
| `VehicleDamageCarriesAttacker` | `Vehicle.cs` declares `Damage(float amount, int attackerActorId)` |
| `BurnCountdownUsesFixedDeltaTime` | no `Time.deltaTime` inside `Vehicle.FixedUpdate` |
| `RepairDoesNotStackAutoDamage` | the `InvokeRepeating("AutoDamage"` in `Repair` is inside an `IsEmpty()` branch; no bare `50f`/`2f` literals remain at the three call sites |
| `HeadlessDereferencesAreGuarded` | the ten Task 9 sites, by pattern |
| `ActorSeatCollisionTimerIsTickCounted` | `Actor.cs` contains no `WaitForSeconds` in `ReactivateCollisionsWith` |

A source-invariant test is a regression pin, not a correctness proof — that is exactly what
design-doc § 9's *"pin the new behaviour with a test so it does not drift again"* asks for on the
`Car` row, and it is the only mechanism available without Unity.

**Verify:** `dotnet test` green across the solution.

---

## 4. Acceptance criteria

1. `Car` and `Helicopter` drive code runs at fixed timestep; `Car.Update` and `Helicopter.Update`
   contain only cosmetic work (audio, steering-wheel angle, rotor spin, renderer toggles).
2. `Helicopter`'s inverted-flight damage is 30 HP/s at a fixed rate, not per rendered frame.
3. Both turrets carry a `float Yaw` / `float Pitch` that the joint or transform is driven **from**,
   and both expose `public void SetAim(float, float)`. No aim value is read back out of a
   `Quaternion` or a `localEulerAngles`.
4. Turret traverse over 1 simulated second is identical at 30 Hz and 144 Hz to 1e-4° —
   **design-doc criterion 4, graded by `TurretSlewIsFramerateIndependent` in CI.**
5. `Vehicle` exposes `Health`, `MaxHealth` and `SetHealthAuthoritative`, and there is exactly one
   assignment to `health` in the file.
6. `Vehicle.Damage(float, int)` exists; the one-argument overload still compiles every existing
   caller unchanged.
7. `Boat` and `Tank` clamp driver input; a `NaN` axis from any of the four vehicles resolves to `0`.
8. `Boat` steering torque uses a local axis; steering no longer leaks into pitch and roll when the
   hull is not level.
9. An occupied → `Repair` → leave cycle leaves exactly one pending `AutoDamage`, at any repetition
   count.
10. Explosion damage is zero at and beyond `damageRange`; balance damage and knockback still reach
    `balanceRange`.
11. `Actor`'s post-exit collision reactivation is tick-counted and is cancelled by re-entry, not
    re-sampled after a wall-clock wait. `Actor.cs` is touched exactly once in the V-track.
12. All ten headless dereference sites are guarded, and every guard protects cosmetic work only.
13. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no allocation in any
    file under `Ironfront.Net.Replication/Vehicles/`.
14. **Zero wire change:** `git diff` touches no file under `Ironfront.Net.Protocol/`, no
    `PROTOCOL_VERSION`, no `protocol-spec.md`, no `SpecChecker`. This is checkable and is checked.

Criteria 1–3, 5–9 and 11–12 are pinned by source-invariant tests (D7) and confirmed behaviourally by
Dev A's Editor pass (§ 7). Criteria 4, 10 and 13 are fully graded in CI. Design-doc criterion 11
(headless survives spawn → damage → death → respawn) needs a running server and is graded in **V9**;
V0 removes the known causes.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| `Car` `Update`→`FixedUpdate` changes single-player handling feel | 5 | 2 | **10** | Accepted and recorded (D1, design-doc § 7 and D8). Task 10 pins the new shape so it does not silently drift back. Dev A confirms the feel is playable, not identical — identical is not on offer. |
| Turret rate constants (300 / 600 °/s) are wrong for the prefabs | 4 | 2 | **8** | They are the original per-frame values × 60 fps, i.e. correct at the design framerate, and they are **serialized `aimLimits`** — retuning is a prefab edit by Dev A, not a code change. |
| `Actor.cs` conflicts with Dev A's branch | 4 | 3 | **12** | D10 — one task, one method, early, announced in the PR title before Dev A opens the file. Design-doc § 9 row, same mitigation. |
| Source-invariant tests are brittle: a harmless refactor turns them red | 4 | 2 | **8** | They match on the *invariant* (no `WaitForSeconds` in this method; a `fixedDeltaTime` is present), not on formatting. A red one is a prompt to re-read the invariant, and the class comment says so. Cheaper than the regression they catch. |
| A source-invariant test passes while the behaviour is still wrong | 3 | 4 | **12** | Stated openly in D7 — they pin shape. Every one of them is paired with a Dev A behavioural check in § 7, and criteria 4/10/13 are behavioural in CI. The failure mode this guards against is silent *re*-introduction, which is the one that actually happened. |
| Splitting `Car.Update` misses a cross-dependency between the drive block and the audio block | 3 | 3 | **9** | `target` is the only value crossing the split (`Car.cs:93`, consumed at `:129`); it becomes a field. `steerAngle` is already a field (`:34`). Enumerated, not assumed. |
| `ApplyHealth` changes damage behaviour by running the ladder at a different point | 3 | 4 | **12** | The ladder body is moved verbatim, not rewritten, and `SetHealthAuthoritative` passes `appliedDamage = 0f` so the `> 900f` heavy-damage branch cannot fire on a snapshot correction. Reviewed as a diff against `:261-276`, line for line. |
| `Boat` torque axis fix makes boats feel worse to a playtester used to the bug | 3 | 2 | **6** | It is a correctness fix with an accepted feel consequence (D1). If steering is now too weak, `turnSpeed` is a serialized field — data, not code. |
| A headless guard silently swallows a real prefab misconfiguration | 3 | 3 | **9** | Guards are applied **only** to cosmetic fields, enumerated one by one in Task 9. Gameplay-required fields stay unguarded so a bad prefab still throws — `development-principles.md` § "Errors Over Silent Fallbacks". |
| Scope creep from V0 into V3 — a task "needs just one field on the wire" | 3 | 4 | **12** | D6 plus acceptance criterion 14, which is a `git diff` check, not a judgement call. |
| `TickTimer` at 25 ticks assumes a 50 Hz fixed step that a project setting could change | 2 | 3 | **6** | Named constant with the assumption in its comment; V4 retunes it against the netcode accumulator. Today's value reproduces today's 0.5 s exactly. |

Highest score is **12**, reached by four rows. None reaches the 15 threshold that would mandate a
gate before the phase starts. The two 12s worth watching are *"a source invariant passes while the
behaviour is wrong"* — answered by pairing every one with a Dev A check — and *"scope creep into
V3"*, answered by a mechanical diff check rather than discipline.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — Engine-free vehicle seam | M (1.5 d) | **Start here.** Tasks 3, 5, 7, 8 all call into it. |
| 2 — `Car` / `Helicopter` timestep | M (1.0 d) | Independent of Task 1; can run alongside. |
| 3 — Authoritative turret aim | M (1.5 d) | Needs Task 1. The largest single behaviour change. |
| 4 — `Vehicle` damage channel | M (1.0 d) | Independent. Unblocks V4's damage sink. |
| 5 — Input clamping + `Boat` torque | S (0.5 d) | Needs Task 1 (`VehicleInputClamp`). |
| 6 — `AutoDamage` double-schedule | S (0.25 d) | Fully independent. Smallest real bug fix in the phase. |
| 7 — Explosion falloff range | S (0.5 d) | Needs Task 1 (`ExplosionRanges`). |
| 8 — Tick-counted seat collision timer | S (0.5 d) | Needs Task 1 (`TickTimer`). **The only `Actor.cs` change; sequence it early** (D10). |
| 9 — Headless NRE guards | M (0.75 d) | Fully independent. Do it early — nothing downstream runs headless until it lands. |
| 10 — Tests | M (1.5 d) | Written alongside 1–9, not after. |
| **Total** | **~9 d (≈2 weeks)** | Critical path: **1 → 3 → 10**. Tasks 2, 4, 6 and 9 are off it and parallelise cleanly. |

Sequencing notes:

- **Task 8 before Dev A opens `Actor.cs`** — the only hard ordering constraint against another
  person's work.
- **Task 9 first among the independents** — every later phase's manual verification runs headless.
- Tasks 2, 4, 6, 9 touch disjoint files (`Car`/`Helicopter`, `Vehicle`, `Vehicle`, `Vehicle`+
  `VehicleSpawner`+`Helicopter`) — 4, 6 and 9 all write `Vehicle.cs` and must **not** be fanned out
  to parallel agents sharing one working tree. Sequence them, or give each a `git worktree`.

---

## 7. Handoff

### Split of work — Dev C writes all code

Per the recorded consumer decision in design-doc § 7, **Dev C writes every line in this phase,
including the files under `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/`** — `Vehicle.cs`,
`Car.cs`, `Helicopter.cs`, `Boat.cs`, `Tank.cs`, `TankTurret.cs`, `MountedTurret.cs`,
`VehicleSpawner.cs`, `ActorManager.cs`, `Actor.cs`. This is a deliberate departure from
`conventions.md` § 7 (`Assets/**` → owner A, "who else may edit: nobody"), on the same precedent as
phase-05 Task 6: **PR plus a Dev A review round**, not silent editing.

**Dev A owns only what requires the Unity Editor:**

| Item | Why it cannot be done outside the Editor |
|---|---|
| `.meta` files for the new `Ironfront.Net.Replication/Vehicles/` types, if any are referenced from a `MonoBehaviour` | Unity generates and owns GUIDs |
| Serializing `aimLimits` on every turret prefab — `YawRateDegPerSec` and `PitchRateDegPerSec` on both turrets, plus `PitchMin`/`PitchMax` on **`MountedTurret` only** (Task 3) | New serialized field; existing prefabs have no value for it. **`TankTurret`'s stops are not prefab data** — `Awake` reads them from `cannonJoint.limits`, which is the joint's own truth; a serialized copy would be a second source that drifts. Tuning a tank's elevation means editing the joint, as it always did. |
| Confirming `damageParticles`, `deathParticles`, `impactAudio`, `explosionSound`, `fireAlarm` assignments on every vehicle prefab (Task 9) | The guards make a missing reference silent; someone must confirm which are *intentionally* empty |
| The two-client Editor behavioural pass (below) | Needs the running game |
| The Profiler run | Editor-only tooling |

**No prefab or scene file is edited by Dev C in this phase.** The turret rate constants ship as
code defaults so the phase merges and runs before Dev A's prefab pass; the pass tunes them.

### Behavioural checks for Dev A's Editor pass

Each pairs with a source-invariant test that pins the shape but cannot prove the behaviour (D7):

1. Drive a `Car` at an uncapped framerate and at 30 fps — handling differs from the pre-V0 build
   (expected, D1) but is **the same between the two framerates**.
2. Traverse a `TankTurret` and a `MountedTurret` for a fixed wall-clock second at 30 fps and at
   144 fps — the traverse matches. This is the Editor half of criterion 4.
3. Steer a `Boat` while rolled — steering no longer induces pitch or roll (Task 5b).
4. Leave a vehicle and re-enter it inside 0.5 s — hitboxes behave correctly on both paths (Task 8).
5. Fly a `Helicopter` inverted — it takes ~30 HP/s regardless of framerate (Task 2).
6. Enter, repair while seated, leave, repeat five times — the vehicle decays at one rate, not five
   (Task 6).

### Deviations from this plan, found in implementation review

Seven, all inside D6 (nothing reaches the wire). Recorded here rather than fixed silently, because
four of them are places where following the plan literally would have shipped a regression.

1. **`GetInput()` returns a normalized demand in `[-1, 1]`, not raw per-frame degrees.** Task 3
   says its "body is unchanged in V0". Following that literally is a **5× (tank) / 10× (mounted)
   sensitivity increase at the design framerate**, because this plan mis-reads the shipped line.
   `Mathf.Clamp(z - input.x, z - 5f, z + 5f)` has bounds derived from the value being clamped, so
   it is algebraically `z -= Mathf.Clamp(input.x, -5f, +5f)`: a **1:1 mouse-degrees mapping with a
   speed limit**, *not* a rate at full deflection. `MAX_TURN_DELTA` is a ceiling, and § 3 Task 3's
   "5 °/frame × 60 fps = the rate the original exhibits" is true only while saturating. Feeding the
   raw number to a rate integrator that normalizes it first therefore multiplies the gain by the
   ceiling — and turns the bots' proportional aim controller into bang-bang, since their input is an
   error term that only saturates near `|err| ≥ 0.33`. The two sources now convert by different
   constants because they are different quantities: mouse motion is a **distance** (divide by the
   arc the step can cover), bot facing is a **state** (divide by `LEGACY_STEP_DEG`). Both reduce to
   the shipped behaviour exactly at 60 fps.
2. **The mouse delta is latched in `Update` and drained in `FixedUpdate`.** `Input.GetAxis("Mouse X")`
   is the delta since the last *rendered* frame and refreshes once per `Update`. Moving the caller
   into `FixedUpdate` — which this phase requires — makes it lossy: at 144 fps ~65% of the player's
   motion is never read, at 30 fps most frames are read twice. That would have replaced one
   framerate dependence with a worse one and defeated criterion 4 for the player while every CI
   test stayed green.
3. **Both turrets seed `_aim` from the authored pose in `Awake`.** Not specified here. Without it a
   turret snaps to (0, 0) on its first fixed step. These are the only engine-angle reads left in
   either file, and `TurretAimIsWrittenOnlyBySeedSetterAndStep` pins them to `Awake` — criterion 3's
   "no read-back" is about the per-step round trip, and a one-time initialization is held to that.
   For the same reason `Mathf.DeltaAngle` survives in `MountedTurret.Awake`, against Task 3's text:
   `localEulerAngles` reports `[0, 360)` and the stops are signed. It runs once, not every frame.
4. **`MountedTurret.Update` keeps its `user != null` guard** on the transform write. Applying
   unconditionally would snap a turret authored outside its stops to the clamped seed on the first
   frame of the level, and hold it there. V4 widens this when a server aims unmanned turrets.
5. **`_lastDamagedBy` is written on any damage, including unattributed damage.** Writing it only
   when an attacker is known leaves it naming a player who chipped the paint minutes before decay
   or a collision actually killed the vehicle — and V4's death event reads it.
6. **The `damageParticles` call is edge-triggered.** The shipped `Damage` called `Play()` on every
   tick below half health and never `Stop()`; `Repair` did the reverse. One ladder cannot do both
   without an edge. Verified indistinguishable on the shipped prefabs: `tank`, `jeep` and
   `helicopter` all carry `looping: 1, playOnAwake: 0` smoke, on which repeated `Play()` and a
   single `Play()` are the same, and `Stop()` on a stopped system is a no-op.
7. **Criterion 4's 1e-4 is graded at 90 °/s; the shipped 300 and 600 °/s are graded at 1e-6
   *relative*.** Neither `300/144` nor `600/144` is exactly representable, so an absolute 1e-4 at
   those rates would be measuring float summation rather than the integrator. 90 °/s was chosen
   because its per-step delta is exact at both framerates. The relative bound is four orders below
   the 2.4× divergence this phase removes.

### Accepted cost, stated plainly

**Offline single-player handling changes.** Design-doc D8 chose this explicitly and § 7 recorded it
so it is not later discovered as a regression. Concretely, three changes are user-visible in
single-player:

- `Car` handling at high framerates (Task 2) — the largest of the three.
- `Boat` steering (Task 5b) — steering torque now stays in yaw.
- Turret traverse rate away from 60 fps (Task 3) — slower above 60, faster below, relative to the
  original. **At** 60 fps the traverse is unchanged; see deviation 1 above for why that took a
  conversion constant rather than falling out of the port.

The original game is not the target; a game that behaves the same on every peer is. These are not
regressions to be filed, and a bug report saying "the car feels different" should be closed citing
this section.

### What this unblocks

- **V3** (protocol v3) can specify the vehicle entry's turret yaw/pitch fields against a real
  authoritative source rather than a value trapped inside a joint.
- **V4** has a health setter and an attacker-carrying damage channel to build its sink on, and a
  headless server that survives long enough to run.
- **V5**'s prediction blend has a fixed-timestep simulation on both peers to converge between —
  design-doc § 9's score-15 risk names V0 as its first mitigation.
- **V1** inherits an `ActorManager.Explode` whose range policy is correct before it becomes
  server-authoritative, and a `Damage` overload with an attacker slot already open.

### Still outside Dev C

Unchanged from phase-05: **B7** (a player id on `ConnectionInfo`, Dev B) and confirming the server
appears in the master's list (Dev D). Neither blocks V0.
