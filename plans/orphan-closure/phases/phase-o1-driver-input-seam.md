# Phase O1 — A networked player in a driver's seat drives the vehicle

- **Track:** [`plan.md`](../plan.md) · **Effort:** M (2 d)
- **Depends on:** nothing. Runs in parallel with O2–O5.
- **Closes:** **X-46** → unblocks check 11's *drive* verb (**B-11**) and any driving window
  **B-10** would be graded from.

---

## 1. The defect, restated from the row

`ServerVehicleInputBridge.Install` calls `NetServerBindings.AttachDriverInput`, which resolves to
`NetDriverInputSink.Attach`, which does `GetComponent<FpsActorController>()` and returns `null`
without one. A player-slot body is `ActorManager.actorPrefab` — the **bot** character — so it
carries an `AiActorController` and never an `FpsActorController`. The bridge counts
`UnreachableControllers++`, the authority keeps accepting `C_VEHICLE_INPUT`, and there is nothing
on the other end of it.

Measured in one run, `artifacts/lane-a/r5/r5-combat-05` (8 clients / 150 s): **1,285
`C_VEHICLE_INPUT` messages** from three clients holding driver seats, **no `Drive` verb at all**,
and the server naming the body twice — `Ai Character Optimizations(Clone)`, which is the prefab
identity the mechanism predicts. `r5-combat-01` is the same shape at 1,138 inputs.

## 2. The decision, and the candidate that lost

**O-D1.** The seam is controller-agnostic. The rejected candidate — put an `FpsActorController` on
the server-side player body — loses on three counts:

1. The body would carry **two** `ActorController` components, so `GetComponent<ActorController>()`
   becomes order-dependent, and `Actor.controller` is a serialized field the prefab already fills.
2. `Actor.aiControlled` is frozen in `Awake` from `controller.GetType() == typeof(AiActorController)`
   and is then read by UI, LOD and weapon culling. **V5-D7** exists precisely to keep that field
   still, and `VehicleClientSourceInvariantTests.AiControlledIsUnchangedForANetworkedDriver` pins it.
3. `FpsActorController` expects a camera rig, which a headless server does not have.

## 3. Task O1.1 — the relay (M)

`NetVehicleAxisRelay`, a `MonoBehaviour` in `Assets/Scripts/NetBindings/`, carries the axes the
server accepted for one body. It is not an `ActorController` and not an `IInputSource`; it is a
value the vehicle-input overrides read.

`AiActorController`'s three vehicle-input overrides consult it **when the controller is suspended**
and only then:

| override | before O1 | after O1 |
|---|---|---|
| `CarInput()` | `Vector2.zero` when `!hasPath` | the relay's car axes when `!enabled` |
| `BoatInput()` | `Vector2.zero` when `!hasPath` | the relay's car axes when `!enabled` |
| `HelicopterInput()` | `Vector4.zero` when `!enabled` (X-47) | the relay's helicopter axes when `!enabled` |

**`enabled`, not `squad != null` (O-D2).** `NetServerActor.Claim` suspends the bot brain through
`IAiDriver.Suspend`, which sets `enabled = false`, so `!enabled` names exactly "this controller is
not steering this body" — the same condition X-45 and X-47 established. A genuine AI setup fault on
an *enabled* controller still behaves as it does today.

`SwimInput` is deliberately untouched: swimming is not a seat and no vehicle pulls it.

## 4. Task O1.2 — the sink chooses (S)

`NetDriverInputSink.Attach` keeps the `FpsActorController` path first — a listen server or the
Editor really does have one, and its `IInputSource` seam is the right answer there — and falls back
to the relay when there is none. The fallback returns a real sink, so `Install` stops counting an
`UnreachableController` for the ordinary networked case, and `UnreachableControllers` goes back to
meaning what its own summary says: **expected to be zero**.

`Detach` on the relay path centres the axes and removes nothing: the component is cheap, the same
body enters and leaves seats all match, and destroying it per exit would allocate one per entry on
a path `ServerVehicleInputBridge` already keeps a dictionary to avoid churning.

## 5. Acceptance

1. A `NetVehicleAxisRelay` on a suspended `AiActorController` is what `CarInput`, `BoatInput` and
   `HelicopterInput` return. An **enabled** controller ignores it entirely.
2. `NetDriverInputSink.Attach` returns non-null for a body with an `AiActorController` and no
   `FpsActorController`, and still returns the `FpsActorController` sink when one is present.
3. The source-invariant suite stays green: no new `ActorController` subclass, `SetInputSource` still
   has its production call site, `aiControlled`'s freeze is untouched.
4. **Observed RED before the fix**, named in the report with the mutation used.
5. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0.

## 6. Out of scope, and said so rather than discovered later

`Vehicle.cs:232` gates ram damage on `!Driver().aiControlled`, and a player-slot body reports
`aiControlled = true` because it is the bot prefab. That is a **second** consequence of the same
prefab choice, it is not the driver input sink, and closing X-46 does not close it. If it needs
closing it gets its own row.
