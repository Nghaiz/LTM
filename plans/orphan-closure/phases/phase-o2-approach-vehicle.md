# Phase O2 — A programme verb that walks to a vehicle instead of to a player

- **Track:** [`plan.md`](../plan.md) · **Effort:** S (1 d)
- **Depends on:** nothing. Runs in parallel with O1 and O3–O5.
- **Closes:** **X-44** → unblocks verdict-closure R1.3's vehicle programme set, and with it
  checks 5, 7, 9 and 12 (**B-5**, **B-7**, **B-9**, **B-13**).

---

## 1. The defect, restated from the row

R2 closed X-30, so a scripted client can *ask* for a seat: `ScriptedInputStep.seatToggle` is an
edge, `ClientSeatRequester` sends `C_SEAT_REQUEST`, and the arbiter answers. **Asking is not
reaching.** `ClientSeatRequester.TryFindNearestSeat` only considers vehicles within
`SeatArbiter.MaxSeatReachMetres` (6 m) of where the player is standing, and the programme
vocabulary has no verb that walks to one: `approach` resolves through
`ScriptedTargetSolver.Solve(step.aimAtPlayer)`, which takes a **player display name**, and a
vehicle has none.

So a driver programme whose first step is *enter a vehicle* only works if a vehicle happens to be
parked within 6 m of the pinned spawn point — which is not a property any run controls.

## 2. The decision

The row named two candidates: *an `approachVehicle` step verb resolving against the client vehicle
registry*, or *a spawn pin chosen for vehicle adjacency*.

**The verb wins, and the spawn pin is rejected for a reason rather than on taste.** A spawn pin
chosen for adjacency is a property of one map at one moment: it breaks when the map's vehicle pads
move, it cannot be checked from the artifact, and it would put every lane-B role — shooter, victim
and witness — next to a vehicle whether or not their programme wants one. X-28 is already open
about what a shared pin costs. A verb is data in the programme, is visible in the record, and works
on any map.

## 3. Task O2.1 — the solver learns about vehicles (S)

`ScriptedTargetSolver` gains `SolveNearestVehicle(float maxSearchMetres)`, resolving against
`RemoteVehicleRegistry` — the client's own replicated vehicle set, which is the only vehicle truth
a client has. It returns the same `Solution` shape as `Solve`, with two changes:

- a new `VehicleId` field, because an actor id and a vehicle id are different namespaces and
  folding a vehicle into `ActorId` would make the artifact lie about which one it found;
- `Distance` is planar, as it already is, so `ScriptedAim.ApproachMoveZ` needs no new arithmetic.

Memoized per frame on `Time.frameCount` exactly as the player solve is, and for the same reason:
`Yaw`, `Pitch` and the harness's `MoveInput` builder all ask in one frame and must get **one**
answer, or the client turns along one bearing and walks along another.

## 4. Task O2.2 — the step verb and the movement half (S)

`ScriptedInputStep.approachVehicle` (bool) and `vehicleSearchMetres` (float, default 120).
`ScriptedInputSource.Aim()` returns the vehicle solution when the flag is set, so the client both
**faces** and **walks toward** the vehicle; `LaneBHarness.BuildMoveInput` drives `moveZ` through
the existing `ScriptedAim.ApproachMoveZ`.

**`holdDistanceMeters` must be under `SeatArbiter.MaxSeatReachMetres` (6 m) for the step that
precedes a `seatToggle`**, or the client stops outside the reach the arbiter measures and the seat
request is refused `RejectedTooFar`. The step's default of 8 m is *wrong for a vehicle* and right
for a player, so a vehicle approach that leaves it unset resolves to **4 m** rather than silently
inheriting the player default — stated in the field's own remark so a programme author is not
required to know the arbiter's constant.

**`approachVehicle` and `aimAtPlayer` are mutually exclusive**, and the vehicle wins with a warning
rather than a silent precedence: a step that names both is a programme bug, and a run that hides it
grades something nobody wrote.

## 5. Task O2.3 — the record says which vehicle (S)

`LaneBCheckpointRecorder.AppendAim` writes `targetVehicleId` beside `targetActorId`. Without it an
approach that resolved to the wrong vehicle, or to none, produces the same artifact as one that
worked — which is the failure `AppendAim`'s own remark exists to prevent.

## 6. Acceptance

1. A step with `approachVehicle: true` walks the client toward the nearest replicated vehicle
   within `vehicleSearchMetres` and stops at `holdDistanceMeters`, defaulting to a distance inside
   `SeatArbiter.MaxSeatReachMetres`.
2. `ScriptedAim` keeps all the arithmetic; the solver keeps only lookups and transform reads —
   the split `ScriptedTargetSolver`'s own remark declares, and the only reason any of this is
   reachable by `dotnet test` at all.
3. The checkpoint record names the vehicle it resolved, or records that it resolved none.
4. **Observed RED before the fix**, named in the report with the mutation used.
5. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1`,
   `check-diagnostics-exclusion.ps1` exit 0.

## 7. Out of scope

Writing the vehicle programme set itself is verdict-closure **R1.3**. This phase ships the verb it
was blocked on and one programme that exercises it; the four checks R1.3 owns are graded there.
