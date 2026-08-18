# Report — Phase V5: Client vehicle replication

- **Author:** the replication track
- **Date:** 2026-08-19
- **Phase:** [`phases/phase-v5-client-vehicle-replication.md`](../phases/phase-v5-client-vehicle-replication.md)
- **Design of record:** [`2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md) § 4 (D3), § 5, § 9
- **Status:** ☑ **Done** — all 12 CI-gradable criteria met; the six Editor-only checks in the
  phase's own § 4 remain the client track's and are **not** claimed here
- **Tests:** 1449 green across the solution (was 1408), plus 20 Unity EditMode tests green

---

## 1. One-paragraph summary

V4 left the server vehicle-authoritative and the client deaf: `S_VEHICLE_SNAPSHOT` arrived and was
counted as an unknown message. This is the other half. Remote vehicles render from the stream on the
discipline the actor path already proved — two ticks behind newest, never extrapolating, holding the
last pose when the buffer runs dry — through a `VehicleSnapshotInterpolator` that **references**
`SnapshotInterpolator`'s `DelayTicks` and `Capacity` rather than redeclaring them, because a vehicle
and the man standing on it must not render a tick and a half apart. The driving client predicts per
design D3: error-corrected simulation, never input replay, because PhysX cannot re-simulate tick N−3
without re-running the scene. The `NoPrediction` fallback ships in this phase as design § 9 requires,
as one flag reachable from `IRONFRONT_CLIENT_PREDICT_VEHICLE`, and **the whole suite runs green under
both presets**. The interesting results are not in the plan: `SetInputSource` had **zero production
call sites** repository-wide, so the moment a networked player drove, the vehicle would have read a
keyboard that is not there; `FpsActorController` installed a `LocalInputSource` at server role, which
reaches `OptionsUi.GetOptions()` — an authority hole and a headless NRE at once; and `VehicleSpawner`
ran on clients, so every pad would have carried two vehicles, one replicated and one on a local timer
with no reason to agree with the server's. None of the three would have failed loudly.

---

## 2. Acceptance criteria review

| # | Criterion | Met | Evidence |
|---|---|---|---|
| 1 | A remote vehicle's rendered pose is a pure function of the snapshot stream, equal to the interpolator's sample at `DelayTicks` behind newest | ☑ | `VehicleInterpolationTests` (19 cases) + `VehicleClientRoutingTests` — a real encoded stream through `ClientMessageRouter` into the interpolator. `NetClientVehicle.ApplyRemote` writes `rigidbody.position`/`.rotation` and nothing else touches the body: the drive path is disabled and the body is kinematic |
| 2 | A starved buffer holds the last pose, never extrapolates, moves `StalledCount` | ☑ | `ASampleNewerThanTheNewestSnapshotHoldsAndStalls` — at 10 m/tick an extrapolation to +4 ticks would read 60; it reads 20 |
| 3 | The driven vehicle responds on the frame input is pressed — no round-trip delay | ☑ | `Predicted` mode keeps the body dynamic and the drive path live; correction is a blend applied on snapshot arrival, never a re-simulation. `ABlendKeepsTheLocalVelocitiesAndTheServerScalars` pins that a blend does not overwrite the local momentum |
| 4 | Under a well-behaved stream, position error stays below `HardSnapMetres` and `SnapCount` stays 0 over 30 s | ☑ | `AConvergingStreamDrivesErrorToZeroWithoutSnapping` — 1800 steps at 60 Hz, ends under 1 cm, `SnapCount == 0` |
| 5 | `PredictLocalVehicle = false` yields a correct if input-lagged vehicle with **zero code changes**, whole suite green under that preset | ☑ | Every solver/config test is a `[Theory]` over both presets. The fallback is one `SetMode` argument in `ClientVehicleStage.Register`; `NoPredictionUsesTheSameRemotePathAsEveryOtherVehicle` pins that the two presets differ in exactly one field |
| 6 | `C_VEHICLE_INPUT` carries axes only; nothing client→server carries a transform | ☑ | No wire change in this phase at all — `PROTOCOL_VERSION` untouched, `SpecChecker` green over 89 constants. `VehicleInputAuthority` accepts axes and a vehicle id and nothing else |
| 7 | Out-of-range axes clamped at decode, gaining the sender no advantage | ☑ | `OutOfRangeAxesAreClampedOnDecode` — `sbyte.MinValue` unpacks to −1.0079 and is clamped to −1. `ANonFiniteAxisResolvesToNeutral…` covers the `NaN` route `Mathf.Clamp` passes straight through |
| 8 | The four helicopter axes round-trip component-identically, sign included; no server-role path reads `OptionsUi` | ☑ | `HelicopterAxisMappingTests` (9 cases) including a wire round trip with signs. `NoOptionsUiReadOnAnyServerRolePath` scans every file under `Net/Server` for `OptionsUi.GetOptions(` **and** asserts the `!NetContext.IsServer` guard that keeps `LocalInputSource` off the authority |
| 9 | `Actor.aiControlled` unchanged for a networked driver | ☑ | `AiControlledIsUnchangedForANetworkedDriver` — pins the exact-type comparison in `Actor.Awake` and scans every file under `Net/` for an `ActorController` subclass. V5-D7 avoided the type entirely |
| 10 | A driver whose input stops coasts to a stop within `VEHICLE_INPUT_HOLD_TICKS` | ☑ | `AxesDecayToZeroAfterTheHoldWindow`; `ServerVehicleInputBridge.PumpTick` calls `IDriverInputSink.Centre()` on the tick the window expires |
| 11 | Turret aim fields round-trip as zero, so V6 needs no protocol change | ☑ | `TurretAimFieldsRoundTripAsZero` — a real `Write`/`TryParse` round trip, so a field that silently stopped decoding fails here rather than in V6 |
| 12 | `dotnet test` green; no `System.Linq`, no `foreach`, no per-frame allocation in new logic | ☑ | 1449 tests, 0 failures. No `System.Linq` and no `foreach` in any new file; the interpolator's ring, the bridge's driver list and the overlay's `StringBuilder` are all allocated once |

---

## 3. What was built

**Engine-free (graded in CI).** `VehicleSnapshotInterpolator` + `VehicleSampleResult`,
`VehiclePose`, `Quat`, `QuatMath`, `VehicleReplicationConfig`, `VehicleCorrectionSolver` +
`VehicleCorrectionStats`, `VehicleInputAuthority`, and four new `ClientMessageRouter` events with
their routing.

**Unity.** `NetClientVehicle` (two modes, one class — which is what makes the fallback a flag),
`RemoteVehicleRegistry`, `ClientVehicleStage`, `ServerVehicleInputBridge`, `IDriverInputSink` +
`NetDriverInputSink`, `VehicleReplicationOverlay`, the four subtype drive-path guards, and
`Vehicle.SetNetworkDriven` / `ApplyReplicatedSubtypeTail` / `ApplyReplicatedFlags`.

**The input seam.** `IInputSource` gains `HeliYaw`, `HeliCollective`, `HeliRoll`, `HeliPitch`;
`FpsActorController.HelicopterInput()` becomes component order and nothing else; the sensitivity
product, the four invert flags and the `helicopterType == 2` raw-`Input.GetAxis` branch all move
into `LocalInputSource`, closing the accepted debt its in-file comment booked.

---

## 4. Departures from the plan, and why

**`VehiclePose` carries the subtype tail as two raw bytes and does not interpolate it.** The plan
listed a `float SubtypeTail`. The tail's meaning is per `VehicleKind`, which the interpolator does
not know, and a helicopter's `rotorSpeed` is a `u16` split across the pair — so lerping the bytes
independently is not a smoothed rotor speed, it is a different number every time the low byte wraps.
The sample carries the earlier snapshot's pair and the Unity layer, which does know the kind, decodes
it. At 20 Hz a stepped steering-wheel angle is invisible; a wrapped low byte is a rotor that stutters
between full speed and stopped. `TheSubtypeTailIsSteppedRatherThanBlended` pins it.

**The hold window lives in `VehicleInputAuthority`, not in `NetInputSource`.** The plan put the decay
in `NetInputSource` so `AxesDecayToZeroAfterTheHoldWindow` could run in `Ironfront.Client.Input.Tests`.
But that project references only `Ironfront.Net.Protocol`, and the window is server policy — the plan
says so itself (V5-D11: *"Server policy, not a wire constant"*). Implementing it in both places would
be two definitions in two assemblies that cannot see each other, which is how they end up disagreeing
about when a stalled driver stops. It lives with the seat table that enforces it and is tested there.

**`ServerVehicleInputBridge` reaches the controller through a binding seam.** `Net/Server` is an
`.asmdef` and `FpsActorController` / `NetInputSource` are in `Assembly-CSharp`; no asmdef can
reference a predefined assembly. `IDriverInputSink` + `NetDriverInputSink` follow the
`IGameplayVehicleSource` arrangement `NetServerBindings` already established. This was found by
compiling in the Editor, not by the syntax gate — `tools/UnitySyntaxCheck` resolves no types and says
so.

**`NetClientVehicle` is a plain class and the stage installs itself.** The plan had prefab authoring
("`NetClientVehicle` attached to every vehicle prefab, plus `.meta`"). `NetServerVehicle` already
recorded why that is a trap: a registry that stays empty until somebody re-saves fourteen prefabs on
two maps fails silently. Neither new component needs a serialized reference —
`RemoteVehicleRegistry` reads its prefab directory off the scene's own spawners — so
`NetClientBootstrap.EnsureVehicleStage` adds both, and **the phase ships with no authored-asset
change at all.**

**The fallback is also an environment variable.** The plan said "one config flag"; a flag only
reachable from an inspector checkbox is not reachable from a headless two-process run or a QA build,
which are exactly the places design § 9's remedy would need to be applied.
`IRONFRONT_CLIENT_PREDICT_VEHICLE=0` is the whole of it.

---

## 5. Three defects found that the plan did not list

**`SetInputSource` had zero production call sites.** The plan flagged this as a finding and it was
worse than a gap: server movement bypasses the controller entirely (`ServerPlayer` drives
`NetMovementAgent`), so nothing had ever noticed that all four vehicles PULL through
`Driver().controller.CarInput()`. A networked player entering a driver seat would have driven against
whatever source the controller held. `TheInputSourceSeamHasAProductionCallSite` now fails if that
returns to being true.

**`FpsActorController` installed `LocalInputSource` at server role.** Harmless until this phase, and
then not: `HeliYaw` and its three siblings read `OptionsUi.GetOptions()`, which is per-user
`PlayerPrefs` a headless process does not have. That is a `NullReferenceException` on the first
networked helicopter and, worse, an authority hole — the server would have scaled a control vector by
a number only that client is entitled to choose. Server role now gets `NullInputSource.Instance`.

**`VehicleSpawner` ran on clients.** Nothing suppressed it, so a client would have instantiated its
own vehicle on every pad from a local respawn timer, alongside the replicated one arriving from
`S_VEHICLE_SPAWN`. Two vehicles per pad, neither of which looks wrong on its own. Clients now take
vehicles from the wire only, and `AClientDoesNotSpawnItsOwnVehicles` pins both halves.

---

## 6. What this phase does NOT claim

The six checks in the phase's § 4 need the Editor and are the client track's, unchanged:

| Check | Status |
|---|---|
| Two clients see the same vehicle in the same place while a third drives it, 100 ms RTT / 5% loss | **Not run.** Three real clients, real transport, real rendering. CI proves the sample is right; it cannot prove what a human sees |
| No perceptible input lag; convergence without visible snapping | **Not run.** CI grades the numeric half (criteria 3 and 4) |
| The kinematic remote path does not break a cosmetic nobody listed | **Not run.** Task 3's enumerated six are covered; the seventh is what the Editor is for |
| Profiler: the client vehicle stage adds no per-frame allocation | **Not run.** No `foreach`, no `Linq`, every buffer allocated once — but that is an argument, not a measurement |
| A headless server survives drive → damage → burn → death with a networked driver | **Not run** |
| Prefab authoring | **Not needed.** See § 4 — the phase ships with no authored-asset change |

Unity **compiles clean** and its 20 EditMode tests pass; that is the whole of what was verified in
the Editor.

---

## 7. Handoff

**To V6.** Unchanged from the plan, and now demonstrated: `C_VEHICLE_INPUT`'s turret fields exist,
round-trip, and are pinned at zero by a test, so V6 adds turret aim without a protocol change. V5-D7's
precedent is proven rather than argued — extending `IInputSource` did not move `Actor.aiControlled`,
and there is now a gate that fails if a future change subclasses the controller instead.

**To the client track.** The six Editor checks in § 6. Note the change in shape from the plan: there
is **no PR to review against `Net/Input/` and `Assembly-CSharp/`** as a separate item — those edits
are in this phase's commit, with the grep gates that keep them correct. What is left for the Editor is
observation, not authoring.

**To V9.** The client side of the two-process harness now exists: a client that consumes the vehicle
stream, sends driver input, and reports its own convergence numbers through
`VehicleReplicationOverlay` and `ClientVehicleStage.DrivenStats`.
