# the replication track — Phase V5: Client vehicle replication

> **Status: ☑ Done (2026-08-19).** Closure report:
> [`../reports/2026-08-19-phase-v5-closure.md`](../reports/2026-08-19-phase-v5-closure.md).
> All 12 CI-gradable criteria met under **both** config presets; the six Editor-only checks
> in § 4 remain the client track's and are not claimed. Four recorded departures — the
> subtype tail is stepped rather than blended, the hold window lives with the seat table,
> the server bridge reaches the controller through a binding seam, and the phase ships with
> no authored-asset change at all — are argued in the report's § 4.

> Design of record: [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md).
> Read **D3** carefully (§ 4) — driver prediction here is *error-corrected simulation*, not input
> replay, and the difference is the whole phase. Also § 3.2 (PhysX cannot be ported), § 3.3 (the
> framerate couplings V0 removes), and § 9 (prediction non-convergence scores 15, and its fallback
> existing is a **precondition of starting this phase**).
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files) and § 7 (file
> ownership — with the recorded departure in design § 7 for `Assembly-CSharp/` and the client track's
> `Net/Input/` files, by PR plus a review round).
>
> **Depends on:** V4 (vehicle ids, `S_VEHICLE_SNAPSHOT`, `S_SEAT_CHANGE`, `S_VEHICLE_DESPAWN`,
> and the subtype tail) and, through it, V3 and V0.
> **Blocks:** nothing. V6 and V7 depend on V4, not on V5.

---

## 1. Objectives

V4 leaves the server authoritative and the client deaf: `S_VEHICLE_SNAPSHOT` arrives and nothing
consumes it. By the end of this phase:

1. Remote vehicles render smoothly from the snapshot stream, on the same discipline
   `SnapshotInterpolator` already proved for actors — render `DelayTicks = 2` (66 ms) behind the
   newest snapshot, **never extrapolate**, hold the last pose when the buffer runs dry.
2. The driving client's own vehicle is predicted per design **D3**: it simulates continuously and
   each snapshot produces a *blended correction*, never a re-simulation.
3. **The no-prediction fallback ships in this phase**, as one config flag, tested green — not as a
   note for later. Design § 9 makes it a precondition, and the remote-interpolation path is shared,
   so it is a config change rather than a rewrite.
4. `C_VEHICLE_INPUT` is produced by the local driver and consumed by the server, including the four
   helicopter axes that do not flow through `IInputSource` today.
5. Graded in CI without the Editor wherever the quantity is arithmetic; the checks that genuinely
   need Unity are named and handed to the client track.

**Not in this phase.** Turret aim and mounted-weapon fire (V6) — `C_VEHICLE_INPUT`'s turret fields
are declared by V3, sent as zeros here, and a test pins their round-trip so V6 needs no wire change.
Projectiles (V7). Objectives (V8). No Profiler run, no Editor session.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **V5-D1** | **A separate `VehicleSnapshotInterpolator`, sharing constants but not code.** `SnapshotInterpolator` lerps position and a single **yaw** (`TryLerpYaw`, `:263`); a vehicle needs a full quaternion slerp because vehicles roll (design § 5), and it rides its own stream at its own cadence with its own ring buffer. `DelayTicks` (2) and `Capacity` (16) are **read from `SnapshotInterpolator`**, never redeclared — those are the values, and there is one definition of them. The sampling code differs because the sampled quantity differs. `OutOfOrderCount` / `StalledCount` accounting is reproduced so the two streams are diagnosable the same way. |
| **V5-D2** | **Never extrapolate; hold the last pose on starvation.** Identical to the actor rule (`SnapshotInterpolator.cs:43`, `:157`). A vehicle at 30 m/s extrapolated through a 200 ms gap is 6 m wrong and then snaps back — visibly worse than a 200 ms freeze, and it is the freeze that tells you the network is bad. `StalledCount` moving is the signal. |
| **V5-D3** | **Remote vehicles are kinematic on clients and their drive components are disabled.** A replicated vehicle whose `Rigidbody` is still dynamic runs local PhysX *against* the incoming snapshots — the two fight, and the result is jitter that looks like a network problem and is not. At `NetRole.Client`, a non-predicted vehicle gets `rigidbody.isKinematic = true` and its `Car` / `Boat` / `Tank` / `Helicopter` drive path disabled. Cosmetics that used to read the body (`Helicopter`'s rotor blur off `rotorSpeed`, `Car`'s steering-wheel angle off `steerAngle`) are driven from the **subtype tail** the vehicle entry already carries — that is exactly why design § 5 put it there. |
| **V5-D4** | **Driver prediction is error-corrected simulation (design D3), and the correction maths is engine-free.** The local vehicle keeps its dynamic body and runs its normal `FixedUpdate` against local input. Every accepted snapshot entry for it produces a target: `serverPos + serverLinVel × (RTT/2)`, and `serverRot` advanced by `serverAngVel × (RTT/2)`. The error is closed by exponential blend over `CorrectionBlendSeconds` (position) and `Slerp` (rotation). Past `HardSnapMetres` or `HardSnapDegrees` it is a teleport with velocities overwritten. `PredictionReconciler`'s replay approach is **not** available: it replays unacked inputs through `MovementCore` because `MovementCore` is a pure function, and PhysX is not — tick N−3 cannot be re-simulated without re-running the scene (design § 3.2). |
| **V5-D5** | **The server never accepts a client transform.** `C_VEHICLE_INPUT` carries axes only. Nothing in this phase adds a client→server position, rotation or velocity field. That is what makes D3's local simulation safe: the client is predicting a value the server computes independently, exactly as infantry prediction already works. |
| **V5-D6** | **The fallback is a runtime config flag, evaluated per vehicle, present from day one.** `VehicleReplicationConfig { bool PredictLocalVehicle; float CorrectionBlendSeconds; float HardSnapMetres; float HardSnapDegrees; }` with `Shipped` and `NoPrediction` presets, mirroring the `ReplicationConfig.Shipped` pattern `InterestManager` already uses (`InterestManager.cs:126`). With `PredictLocalVehicle = false`, the driven vehicle takes the **same** kinematic remote path as every other vehicle (V5-D3) and the driver's input is still sent — the server still simulates, the client just watches. **The whole test suite runs green under both presets**; a fallback nobody has ever flipped is not a fallback. |
| **V5-D7** | **No new `ActorController` subclass. The intercept is `IInputSource`.** All four vehicles PULL through `Driver().controller.XInput()` (`Car.cs:96`, `Boat.cs:60`, `Tank.cs:86`, `Helicopter.cs:93`), and `FpsActorController` already resolves `CarInput` / `BoatInput` through `inputSource.MoveX` / `MoveZ` (`:222-225`). So Car, Boat and Tank need **no new type at all** — they need the server to install a `NetInputSource`, which nothing does today (see V5-D9). Introducing a network controller type would trip `Actor.aiControlled`, frozen in `Awake` from `controller.GetType() == typeof(AiActorController)` (`Actor.cs:178`) and read by UI, LOD and weapon culling; extending the existing seam does not go near it. A test pins `aiControlled` unchanged for a networked driver. |
| **V5-D8** | **The four helicopter axes are added to `IInputSource`, because over the wire they are structurally unreachable otherwise.** `NetInputSource.LookDeltaX` and `LookDeltaY` return **`0f`** (`NetInputSource.cs:61`, `:64`) — and correctly so: `C_INPUT` carries absolute yaw and pitch, and a per-frame mouse *delta* is a different quantity that an absolute-angle protocol cannot express (`IInputSource.cs:57-60`). The `helicopterType == 2` branch (`FpsActorController.cs:229-239`) additionally reads raw `Input.GetAxis`, booked as accepted debt in an in-file comment. Both roads end at the same place: `IInputSource` gains `HeliYaw`, `HeliCollective`, `HeliRoll`, `HeliPitch`, `LocalInputSource` computes them exactly as `HelicopterInput` does today, `NetInputSource` reads them from `C_VEHICLE_INPUT`, and `FpsActorController.HelicopterInput` becomes a `Vector4` assembled from the four. |
| **V5-D9** | **The client applies its own sensitivity and inversion, and sends the post-scaled axes.** `HelicopterInput` multiplies by `OptionsUi.GetOptions().mouseSensitivity × helicopterSensitivity` and applies four per-user invert flags (`FpsActorController.cs:230-238`). Those are client-local settings the server does not have — and reaching `OptionsUi.GetOptions()` at server role is both an authority hole and a headless NRE. So the scaling happens on the sender and the server treats the result as an opaque control vector, bounded by `Vehicle.Clamp4` (`Vehicle.cs:421`) at decode (V4-D13). Sending "full stick" every frame is inside the legal envelope, which is what the clamp defines. **A grep gate asserts no `OptionsUi` read on any server-role path.** |
| **V5-D10** | **The helicopter axis mapping is pinned verbatim from `Helicopter.cs:94-95`, sign included.** See § 3 Task 5. It is implicit in one line today, it is a `Vector4` with no field names, and getting a component wrong produces a helicopter that flies — badly, in a way nobody can attribute. It is pinned as a table and as a test. |
| **V5-D11** | **The server holds the last received axes for a bounded window, then zeroes them.** `C_VEHICLE_INPUT` is unreliable on channel 3; a dropped packet must not freeze the throttle at full. `VEHICLE_INPUT_HOLD_TICKS = 6` (200 ms at 30 Hz) after which the axes decay to zero, so a driver whose connection stalls coasts to a stop instead of driving into the sea. Server policy, not a wire constant — it needs no protocol change and no client agreement. |
| **V5-D12** | **V0's framerate fixes are load-bearing here, and this phase says so out loud.** `Helicopter.rotorSpeed` integrates in `Update` with `Time.deltaTime` (`:55-63`) and then **multiplies every force** in `FixedUpdate` (`:93`); `Car` drives WheelCollider torque from `Update` (`:88`). Two peers at different framerates therefore produce different physics **from identical input**, and no blend converges against a systematic divergence — it only chases it. If V0 has not landed these, the correct action is to ship with `PredictLocalVehicle = false` (V5-D6) and say so, not to widen the blend window until the symptom hides. |

---

## 3. Detailed tasks

### Task 1 — `VehicleSnapshotInterpolator` (2 days)

Per V5-D1 and V5-D2. Engine-free; this is arithmetic over quantised poses and is fully testable
under `dotnet test`.

| File | Change | Side |
|---|---|---|
| `Ironfront.Net.Replication/Client/VehicleSnapshotInterpolator.cs` | **New.** Ring buffer of `VehicleWorldSnapshot`, `Capacity` and `DelayTicks` referencing `SnapshotInterpolator`'s constants. `Push(VehicleWorldSnapshot)`, `RenderTick(double tickFraction)` = `newest.ServerTick + fraction - DelayTicks`, `TrySample(ushort vehicleId, double renderTick, out VehiclePose pose)`, `Reset()`, and the `OutOfOrderCount` / `StalledCount` counters. Linear scan backwards from newest, not a binary search — the target is at most `DelayTicks` back and the branches cost more than they save (`SnapshotInterpolator.cs:208`). | engine-free |
| `Ironfront.Net.Replication/Client/VehiclePose.cs` | **New.** `readonly struct VehiclePose { Vec3 Position; Quat Rotation; Vec3 LinearVelocity; Vec3 AngularVelocity; float Health; VehicleFlags Flags; float TurretYaw, TurretPitch, SubtypeTail; }`. | engine-free |
| `Ironfront.Net.Replication/Client/QuatMath.cs` | **New** (or extend the existing `Quat` type if V3 added one). `Slerp` with the shortest-arc dot-sign flip, plus `Normalize`. The dot-sign flip is not optional: without it a quaternion pair straddling the sign boundary slerps the long way round and a car visibly spins through 300° to turn 60°. | engine-free |
| `Ironfront.Net.Replication/Client/ClientMessageRouter.cs` | **Edit.** `case ServerMessageType.VehicleSnapshot` (0x4C) → `VehicleDecoder` (V3) → `VehicleSnapshotInterpolator.Push`. Plus `OnVehicleSpawn` / `OnVehicleDespawn` / `OnSeatChange` events, matching the existing `OnSpawnActor` / `OnDespawnActor` shape (`:66-107`). | engine-free |

**Verify.** `VehicleInterpolationTests`: a sample at exactly `DelayTicks` behind returns the exact
snapshot pose; a sample between two snapshots lies on the segment; a sample **newer** than the
newest snapshot returns the newest pose and increments `StalledCount` rather than extrapolating
(V5-D2); an out-of-order push increments `OutOfOrderCount` and does not corrupt the buffer; a
slerp across the quaternion sign boundary takes the short arc.

---

### Task 2 — `VehicleCorrectionSolver` and the config (2 days)

Per V5-D4 and V5-D6. **This is where the fallback is built**, not a later phase.

| File | Change | Side |
|---|---|---|
| `Ironfront.Net.Replication/Client/VehicleReplicationConfig.cs` | **New.** `readonly struct` per V5-D6 with `Shipped` (`PredictLocalVehicle = true`, `CorrectionBlendSeconds = 0.15f`, `HardSnapMetres = 4f`, `HardSnapDegrees = 45f`) and `NoPrediction` (`PredictLocalVehicle = false`, rest ignored) presets. Values are named constants, never inline literals (`code-conventions.md`). | engine-free |
| `Ironfront.Net.Replication/Client/VehicleCorrectionSolver.cs` | **New. Pure function, no state, no allocation.** `CorrectionMode Solve(in VehiclePose local, in VehiclePose server, float rttSeconds, float dt, in VehicleReplicationConfig config, out VehiclePose corrected)`. Steps: extrapolate the server pose forward by `rttSeconds * 0.5f` using the server's own velocities; measure position and angular error against `local`; if either exceeds its hard-snap threshold return `CorrectionMode.Snap` with the extrapolated pose and the server velocities verbatim; otherwise return `CorrectionMode.Blend` with position exponentially decayed and rotation slerped toward the target by `1 - exp(-dt / CorrectionBlendSeconds)`. | engine-free |

Exponential decay, not a linear lerp with a fixed alpha: a fixed per-frame alpha makes the
convergence rate depend on framerate, which is the exact class of bug design § 3.3 catalogues and
V0 exists to remove. `1 - exp(-dt / tau)` is framerate-independent by construction.

`CorrectionMode` is reported, not swallowed — `SnapCount` and `BlendCount` are surfaced so
"prediction is not converging" is a number rather than a feeling. A rising `SnapCount` under normal
conditions is the trigger for V5-D6's fallback.

**Verify.** `VehicleCorrectionTests`: a zero-error input returns the local pose unchanged and
`Blend`; an error past `HardSnapMetres` returns `Snap` with the server's velocities, not the
local ones; halving `dt` and doubling the step count converges to the same pose within tolerance
(framerate independence — the property that makes this different from a lerp); a converging series
of snapshots drives position error monotonically toward zero and `SnapCount` stays at zero;
`NoPrediction` never calls the solver at all.

---

### Task 3 — Client vehicle registry and the remote path (2 days)

Per V5-D3. Unity, because it attaches to `Rigidbody` and to the drive components.

| File | Change | Side |
|---|---|---|
| `Ironfront_Reborn/Assets/Scripts/Net/Client/RemoteVehicleRegistry.cs` | **New.** Vehicle id → `NetClientVehicle`, mirroring the shape of the existing `RemoteActorRegistry`. Handles `S_VEHICLE_SPAWN` (bind an id to a scene vehicle), `S_VEHICLE_DESPAWN` (stop applying snapshots for that id **immediately**, then play the local destruction per V4-D12). | Unity |
| `Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientVehicle.cs` | **New.** Per vehicle: samples `VehicleSnapshotInterpolator` each frame and writes `rigidbody.position` / `.rotation`; applies `health`, `burning` and the subtype tail; owns the kinematic switch. Two modes, one component — `Remote` (V5-D3) and `Predicted` (Task 4) — because that is what makes V5-D6 a flag rather than a rewrite. | Unity |
| `Ironfront_Reborn/Assets/Scripts/Net/Client/ClientVehicleStage.cs` | **New.** A `MonoBehaviour` driving the per-frame sample for every registered vehicle, mirroring `ClientPredictionStage`. One pre-sized list, iterated with a `for`; no `foreach`, no allocation. | Unity |
| `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs` and the four subtypes | **Edit** (the client track files, batched into V4's existing PR where possible). A `NetContext.IsClient` guard that disables the drive path on a non-predicted vehicle, and drives `Helicopter.rotorSpeed` / `Car.steerAngle` from the replicated subtype tail instead of from local integration. Offline is an early no-op and behaves exactly as today. | Unity |

Cosmetics that must keep working off replicated values rather than local physics, enumerated so
none is discovered at integration: `Helicopter`'s solid/blurred rotor swap and rotor spin
(`:66-68`, driven by `rotorSpeed`), `Helicopter.isAirborne`'s downward raycast (`:69`),
`Car`'s steering-wheel angle (`:89-91`, driven by `steerAngle`), `Vehicle`'s engine audio pitch,
`Boat.inWater` (`Boat.cs:56`), and the burn / damage particle systems (`Vehicle.cs:274`, `:282`).
The first two of those are the reason design § 5 reserved a subtype tail at all.

**Verify.** A scripted client-role harness feeds a recorded `S_VEHICLE_SNAPSHOT` sequence and
asserts the applied pose matches the interpolator's sample for every frame; a despawn stops
application on the same frame it arrives; the drive path is provably disabled at client role (a
test asserts a non-predicted vehicle's pose is a pure function of the snapshot stream, so any
surviving local force would fail it). Rendering itself is the client track's — see § 4.

---

### Task 4 — Local driver prediction, and the fallback (2 days)

Per V5-D4 and V5-D6.

| File | Change | Side |
|---|---|---|
| `Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientVehicle.cs` | **Edit** (Task 3 created it). `Predicted` mode: the body stays dynamic, the drive path stays live against local input, and each accepted snapshot entry runs `VehicleCorrectionSolver.Solve` with `rttSeconds` from the connection's `SmoothedRttMs` — the same source phase-05 Task 3 wired for `LagCompensator`, not a second RTT estimate. `Blend` writes `rigidbody.position` / `.rotation`; `Snap` additionally overwrites `linearVelocity` / `angularVelocity`. | Unity |
| `Ironfront_Reborn/Assets/Scripts/Net/Client/ClientVehicleStage.cs` | **Edit.** Reads `VehicleReplicationConfig`; when `PredictLocalVehicle` is false, the locally-driven vehicle is registered in `Remote` mode and nothing else changes. **That is the entire fallback.** | Unity |
| `Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/` | **Edit.** Surface `SnapCount`, `BlendCount` and mean position error on the existing net-debug overlay, so non-convergence is visible during play rather than only in a test. | Unity |

Which seat is "mine" comes from `S_SEAT_CHANGE` plus the actor entry's `SnapshotField.SeatInfo`
(V4, design D2) — the client never decides locally that it is driving. A refused seat request
therefore cannot leave a client predicting a vehicle the server does not think it is in.

**Verify.** `VehiclePredictionTests` (engine-free, over the solver and a fake pose stream): with a
server stream generated by the same integration the client runs, position error stays under
`HardSnapMetres` and `SnapCount` stays zero over 30 s; with a deliberately divergent server stream,
`SnapCount` rises rather than the error growing unbounded — the failure is loud. The **whole V5
suite runs twice**, once per preset (V5-D6), and both runs must be green.

---

### Task 5 — The input seam: `IInputSource` vehicle axes and `C_VEHICLE_INPUT` (2.5 days + a the client track review round)

Per V5-D7 through V5-D11. **Severable and last**, on the phase-05 D8 precedent — Tasks 1–4 merge
without it.

A finding that shapes this task: **`SetInputSource` has zero production call sites** —
`grep -rn "SetInputSource\|NetInputSource" --include=*.cs .` across the whole repository (excluding
`obj/` and `bin/`) returns only the definition at `FpsActorController.cs:119`, one comment, and
`Ironfront.Client.Input.Tests`. `NetInputSource` is constructed only by tests. Nothing noticed,
because server movement bypasses the controller entirely (`ServerPlayer` drives `NetMovementAgent`
and `MovementSimulation`; `NetServerActor.cs:150-164` records that the server has no mouse). The
moment a networked player drives, `Driver().controller.CarInput()` reads a `LocalInputSource` in a
headless build. Installing the source is part of this task.

| File | Change | Side |
|---|---|---|
| `Ironfront_Reborn/Assets/Scripts/Net/Input/IInputSource.cs` | **Edit** (the client track file — PR + review). Four members: `HeliYaw`, `HeliCollective`, `HeliRoll`, `HeliPitch`. No `using UnityEngine` — the file and its pure siblings are `<Compile Include>` linked into `Ironfront.Client.Input.Tests`, and adding one silently drops them out of coverage (`IInputSource.cs:13-20`). | Unity-adjacent, engine-free by construction |
| `Ironfront_Reborn/Assets/Scripts/Net/Input/LocalInputSource.cs` | **Edit.** Computes the four exactly as `FpsActorController.HelicopterInput` does today, including the `OptionsUi` scaling and the four invert flags (V5-D9). | same |
| `Ironfront_Reborn/Assets/Scripts/Net/Input/NetInputSource.cs` | **Edit.** Returns the four from the last accepted `C_VEHICLE_INPUT`, decaying to zero after `VEHICLE_INPUT_HOLD_TICKS` (V5-D11). `LookDeltaX` / `LookDeltaY` stay `0f` — they remain genuinely unrepresentable, and the four axes are the answer, not a workaround. | same |
| `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/FpsActorController.cs` | **Edit** (the client track file, same PR). `HelicopterInput()` becomes a `Vector4` assembled from the four `IInputSource` members. The `helicopterType == 2` raw-`Input.GetAxis` branch moves into `LocalInputSource`, closing the accepted debt its in-file comment books. | Unity |
| `Ironfront.Net.Replication/Server/ServerMessageRouter.cs` | **Edit.** `case ClientMessageType.VehicleInput` (0x21) → `IVehicleInputHandler`, same field-held-interface shape as `IAcceptedFrameObserver` / `ISeatRequestHandler`. Malformed increments `MalformedMessages`; never throws. | engine-free |
| `Ironfront.Net.Replication/Server/VehicleInputAuthority.cs` | **New.** Holds the last accepted axes per driver, applies the V4-D13 decode clamp and the V5-D11 hold window, and refuses input from an actor the seat record does not show as the driver of that vehicle. | engine-free |
| `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerVehicleInputBridge.cs` | **New.** Installs a `NetInputSource` on the driver actor's `FpsActorController` via `SetInputSource` on seat entry, and restores `NullInputSource.Instance` on seat exit. **This is the wiring nothing does today.** | Unity |
| `Ironfront_Reborn/Assets/Scripts/Net/Client/ClientVehicleStage.cs` | **Edit.** Sends `C_VEHICLE_INPUT` on channel 3 while, and only while, seated as driver. | Unity |

**The helicopter axis mapping, pinned verbatim** (`Helicopter.cs:94-95`):

```csharp
Vector4 vector = Vehicle.Clamp4(Driver().controller.HelicopterInput()) * rotorSpeed;
float y = vector.y;
Vector3 vector2 = new Vector3(vector.w, vector.x, 0f - vector.z) * manouverability * 0.0069999998f;
// ... rigidbody.AddRelativeTorque(vector2, ForceMode.VelocityChange)
```

| `Vector4` component | Meaning | Where it lands |
|---|---|---|
| `.x` | **yaw** | torque local **Y** |
| `.y` | **collective** | `AddForce` along the lift axis, scaled by `rotorForce` |
| `.z` | **roll** | torque local **Z**, **negated** (`0f - vector.z`) |
| `.w` | **pitch** | torque local **X** |

The negation on `.z` is part of the contract. `C_VEHICLE_INPUT` transmits `(yaw, collective, roll,
pitch)` in that order and the sign convention above; a test asserts the assembled `Vector4` is
component-identical to what `HelicopterInput` produces today for the same inputs.

Note `* rotorSpeed`: the axes are scaled on the vehicle by a value that integrates in `Update`
with `Time.deltaTime` (`Helicopter.cs:55-63`). That is V0's to fix and V5-D12's to depend on.

**Verify.** `HelicopterAxisMappingTests` (engine-free, over the linked input files): the four
axes assemble to the exact `Vector4` the current code produces, sign included. `NetInputSource`
returns zero after `VEHICLE_INPUT_HOLD_TICKS` with no fresh message. `VehicleInputAuthorityTests`:
input from a non-driver is refused; out-of-range axes are clamped to `Clamp4`'s bounds.
`NoOptionsUiAtServerRole` — a grep gate over the server-role call graph (V5-D9).
`AiControlledIsUnchangedForANetworkedDriver` — the V5-D7 hazard, asserted rather than assumed.

---

### Task 6 — Tests (2 days, written alongside Tasks 1–5)

Engine-free, under `dotnet test`, no Editor. **Every one of them runs twice** — once under
`VehicleReplicationConfig.Shipped` and once under `NoPrediction` (V5-D6).

| Test | Asserts |
|---|---|
| `ASampleAtDelayTicksReturnsTheExactSnapshotPose` | V5-D1 — the 66 ms render offset |
| `ASampleNewerThanTheNewestSnapshotHoldsAndStalls` | V5-D2 — never extrapolate; `StalledCount` moves |
| `AnOutOfOrderPushDoesNotCorruptTheBuffer` | `OutOfOrderCount` moves; sampling is unaffected |
| `ASlerpAcrossTheSignBoundaryTakesTheShortArc` | the 300°-to-turn-60° bug |
| `AZeroErrorCorrectionReturnsTheLocalPose` | V5-D4 — prediction is not a disguised snap |
| `AnErrorPastTheThresholdSnapsAndOverwritesVelocity` | V5-D4 — and takes the server's velocities |
| `TheBlendRateIsFramerateIndependent` | `1 - exp(-dt/tau)`; halving `dt` converges identically |
| `AConvergingStreamDrivesErrorToZeroWithoutSnapping` | `SnapCount == 0` over 30 s |
| `ADivergingStreamRaisesSnapCountRatherThanDrift` | the failure is loud, not silent |
| `NoPredictionUsesTheSameRemotePathAsEveryOtherVehicle` | V5-D6 — the fallback is a flag, not a branch of its own |
| `TheHelicopterAxisMappingIsComponentIdentical` | V5-D10, sign included |
| `TurretAimFieldsRoundTripAsZero` | V6 needs no wire change |
| `InputFromANonDriverIsRefused` | V5-D5 / authority |
| `AxesDecayToZeroAfterTheHoldWindow` | V5-D11 — a stalled driver coasts |
| `OutOfRangeAxesAreClampedOnDecode` | design § 8 criterion 3 |
| `AiControlledIsUnchangedForANetworkedDriver` | V5-D7's named hazard |
| `NoOptionsUiReadOnAnyServerRolePath` | V5-D9 |
| `TheClientSuiteAllocatesNothingPerFrame` | conventions § 3.2, measured |

---

## 4. Acceptance criteria

1. A remote vehicle's rendered pose is a pure function of the snapshot stream and equals the
   interpolator's sample at `DelayTicks` behind newest, every frame.
2. A starved buffer holds the last pose, never extrapolates, and moves `StalledCount`.
3. The locally-driven vehicle responds to input on the frame it is pressed — no round-trip delay
   (design § 8 criterion 2, first half).
4. Under a well-behaved server stream, the driven vehicle's position error stays below
   `HardSnapMetres` and `SnapCount` stays at zero over 30 s (criterion 2, second half).
5. `PredictLocalVehicle = false` yields a correct — if input-lagged — driven vehicle with **zero
   code changes**, and the entire test suite is green under that preset (design § 9's precondition).
6. `C_VEHICLE_INPUT` carries axes only; nothing client→server carries a transform (V5-D5).
7. Out-of-range axes are clamped at decode and gain the sender no advantage (design § 8
   criterion 3).
8. The four helicopter axes round-trip component-identically, sign included, and no server-role
   path reads `OptionsUi`.
9. `Actor.aiControlled` is unchanged for a networked driver.
10. A driver whose input stops coasts to a stop within `VEHICLE_INPUT_HOLD_TICKS`.
11. Turret aim fields round-trip as zero, so V6 needs no protocol change.
12. `dotnet test` green across the solution under both config presets; no `System.Linq`, no
    `foreach`, no per-frame allocation in any new logic file.

### What genuinely needs the Editor — handed to the client track

| Check | Why it needs Unity |
|---|---|
| **Two clients see the same vehicle in the same place while a third drives it, at 100 ms RTT and 5% loss** (design § 8 criterion 1) | Three real clients, real transport, real rendering. CI can prove the *sample* is right; it cannot prove what a human sees. |
| No perceptible input lag, and convergence without visible snapping (criterion 2, subjective half) | Judged by eye; CI grades the numeric half (criteria 3 and 4). |
| `NetClientVehicle` attached to every vehicle prefab, plus `.meta` for every new script | Prefab authoring |
| The kinematic remote path does not break a cosmetic nobody listed | The enumerated list in Task 3 is what CI covers; the Editor is what catches the one that was missed |
| Profiler: the client vehicle stage adds no per-frame allocation | Unity Profiler |
| A headless server survives a full drive → damage → burn → death cycle with a networked driver | Batch mode |

---

## 5. Which side each piece lands on

| Piece | Side | Why |
|---|---|---|
| `VehicleSnapshotInterpolator`, `VehiclePose`, `QuatMath` | engine-free | Arithmetic over quantised poses |
| `VehicleCorrectionSolver`, `VehicleReplicationConfig` | engine-free | **A pure function.** This is the part of D3 that would otherwise be Editor-only, and it is the part most likely to be wrong |
| `VehicleInputAuthority` | engine-free | Clamping, hold window, driver check |
| `ClientMessageRouter`, `ServerMessageRouter` edits | engine-free | Already are |
| `IInputSource` + `LocalInputSource` + `NetInputSource` | engine-free **by construction** | No `using UnityEngine`; `<Compile Include>` linked into `Ironfront.Client.Input.Tests` (`IInputSource.cs:13-20`) |
| `NetClientVehicle`, `ClientVehicleStage`, `RemoteVehicleRegistry` | **Unity** | Write `rigidbody.position` / `.rotation` / `.isKinematic`. This is the whole PhysX exception, and it is an *application* of a decision made above it |
| `ServerVehicleInputBridge` | **Unity** | Calls `SetInputSource` on a `MonoBehaviour` |
| `FpsActorController`, `Vehicle` + subtypes | **Unity** (the client track files) | The pull sites and the drive paths live there |

Everything that can be got wrong quietly — the blend, the extrapolation ban, the clamp, the axis
mapping — is on the engine-free side and graded in CI.

---

## 6. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| **Prediction never converges because PhysX diverges faster than the blend corrects** (design § 9) | 3 | 5 | **15** | V5-D6: the `NoPrediction` preset is built in Task 2, wired in Task 4, and **the whole suite runs green under it** before the phase is done. Design § 9 makes the fallback's existence a precondition of starting V5; running it in CI is how that precondition is verified rather than asserted. |
| **V0's framerate couplings are not fully removed, so two peers diverge from identical input** (`Helicopter.rotorSpeed`, `Car`'s `Update` torque) | 3 | 5 | **15** | V5-D12 names the exact sites. The convergence test (Task 4) runs the solver against a *deliberately divergent* stream and asserts `SnapCount` rises — so the failure is measured, not felt. If V0 has not landed, ship `NoPrediction` and record it; do not widen the blend window until the symptom hides. |
| A cosmetic that read local physics breaks when remote vehicles go kinematic | 4 | 3 | **12** | Task 3 enumerates the six sites and drives the two that matter from the replicated subtype tail — which is why design § 5 reserved it. The remaining risk is one nobody listed; that is explicitly the client track's Editor check in § 4. |
| `OptionsUi.GetOptions()` reached at server role — an authority hole **and** a headless NRE | 3 | 4 | **12** | V5-D9 puts the scaling on the sender. A grep gate over the server-role call graph is a named test, not a review habit. |
| The `IInputSource` edit is a the client track file and the review round slips the phase | 3 | 2 | 6 | Task 5 is severable and last (phase-05 D8 precedent). Tasks 1–4 merge without it; remote interpolation — the half two of three clients see — does not depend on it. |
| A new controller type flips `Actor.aiControlled` and silently changes UI / LOD / weapon culling | 2 | 4 | 8 | V5-D7 avoids the type entirely by extending the existing `IInputSource` seam. `AiControlledIsUnchangedForANetworkedDriver` pins it, so a future "let's just subclass the controller" goes red. |
| Smallest-three quaternion at 4 bytes jitters a slowly-rotating vehicle | 2 | 3 | 6 | The interpolator slerps between decoded values, which smooths quantisation by construction. If it is visible, the escalation is a wire change and therefore V3's, not a client patch. |
| A client keeps predicting a vehicle after a refused seat request | 2 | 4 | 8 | Task 4: "which seat is mine" comes from `S_SEAT_CHANGE` and the actor entry's `SeatInfo`, never from a local decision. The refusal path V4-D7 built is what makes this expressible. |
| Two RTT estimates drift apart (one for lag comp, one for the correction) | 2 | 3 | 6 | Task 4 reads the connection's `SmoothedRttMs` — the same source phase-05 Task 3 wired. No second estimator is introduced. |

Two risks reach 15, and they are the same failure seen from two sides — the physics diverges, or
the correction cannot keep up. Both are mitigated by the same artefact: a fallback that is built,
wired and **run in CI** in this phase, and a convergence test whose failure mode is a rising
counter rather than a drifting car.

---

## 7. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — `VehicleSnapshotInterpolator` | M (2d) | Needs V4's stream. Start here. |
| 2 — `VehicleCorrectionSolver` + config | M (2d) | Independent of Task 1; can run alongside. **Builds the fallback.** |
| 3 — Client registry + remote path | M (2d) | Needs Task 1. Two of three clients see the result of this alone. |
| 4 — Local prediction + fallback wiring | M (2d) | Needs 2 and 3. |
| 5 — Input seam + `C_VEHICLE_INPUT` | M (2.5d) + review round | Severable, last. Needs a the client track round on `IInputSource` and `FpsActorController`. |
| 6 — Tests | M (2d) | Written alongside 1–5, not after. Run twice, once per preset. |
| **Total** | **~2 weeks** | Critical path: 1 → 3 → 4. Tasks 2 and 5 run off it. |

---

## 8. Handoff

**To V6.** `C_VEHICLE_INPUT`'s turret-aim fields exist, round-trip and are pinned by a test at zero,
so V6 adds turret aim without a protocol change. What V6 still has to build is the seam itself:
`TankTurret.cs:66` and `MountedTurret.cs:56` read `Input.GetAxis` and `OptionsUi.GetOptions()`
directly inside `Update`, there is no abstract `ActorController` member for turret aim, and the slew
has **no `Time.deltaTime` at all** (design § 3.3) — a 144 Hz client traverses ~2.4× faster than a
60 Hz one. V0 fixes the slew; V6 owns the input member. V5-D7's precedent applies: extend
`IInputSource`, do not subclass the controller.

**To the client track.** One PR against `Net/Input/IInputSource.cs`, `LocalInputSource.cs`,
`NetInputSource.cs` and `Assembly-CSharp/FpsActorController.cs` (Task 5), plus the `Vehicle.cs`
client guards batched into V4's existing vehicle PR. Plus the six Editor-only checks tabulated
in § 4 — of which criterion 1 (three clients, 100 ms RTT, 5% loss) is the one that can still
*fail* rather than merely be unmeasured.

**To V9.** The measurement this phase cannot make: bandwidth and tick p99 at 16 players + 32 bots
+ 12 vehicles (design § 8 criteria 9 and 10). V5 supplies the client side of the two-process
harness; V9 runs it.
