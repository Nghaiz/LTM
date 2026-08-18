# Movement analysis — where character movement actually lives

**Author:** the replication track · **Date:** 2026-08-12 · **Closes:** replication phase-00 acceptance
criterion 7 (task 5.1, "the 4-question note")

> Read this before touching `MovementSimulation`, `MovementCore`, or anything that predicts or
> replicates a player's position. It is the derivation behind every constant in
> [`MovementCore.cs`](../Ironfront.Net.Replication/Movement/MovementCore.cs); the code cites this
> file and this file cites the game.

---

## 0. The headline finding: movement is not in `Actor.cs`

The phase-00 plan assigns 2 days to reading `Actor.cs` (1188 lines) on the premise that the
movement code is in it. **It is not.** `Actor.cs` never moves the character. It reads the
character's velocity from elsewhere and drives animation, ragdoll and IK from it.

The chain is three hops long, and the last one leaves the assembly:

| Hop | File | Assembly | Role |
|---|---|---|---|
| 1 | [`Actor.cs`](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Actor.cs) | `Assembly-CSharp` | **Consumes** movement. Animation, ragdoll, IK, facing |
| 2 | [`FpsActorController.cs`](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/FpsActorController.cs) | `Assembly-CSharp` | **Forwards** movement. Owns crouch/sprint policy |
| 3 | `UnityStandardAssets/Characters/FirstPerson/FirstPersonController.cs` | **`Assembly-CSharp-firstpass`** | **Produces** movement. The real simulation |

Hop 3 lives under `Assets/Plugins/Assembly-CSharp-firstpass/`, which is why a search of
`Assets/Scripts/` finds nothing. `Actor.cs:528` is the seam:

```csharp
// Actor.UpdateMovement(float dt)
Vector3 vector = controller.Velocity();      // <- asks the controller, does not compute
```

and `FpsActorController.cs:157` forwards it straight on:

```csharp
public override Vector3 Velocity()  => controller.Velocity();   // controller is FirstPersonController
public override bool    OnGround()  => controller.OnGround();
```

**Consequence for the plan.** Task 5.1's 2-day budget and the 60-minute session with the client track were
scoped against the wrong file. The actual movement code is 326 lines of stock Unity Standard
Assets, publicly documented and already understood by anyone who has used it. The risk the plan
called C8 ("the cost of learning `Actor.cs` is underestimated") did not materialise, because
`Actor.cs` turns out not to be on the critical path at all. What *is* on the critical path is
the part nobody flagged: **the movement constants are not in code.** See § 2.

---

## 1. Question 1 — Rigidbody, CharacterController, or ragdoll forces?

**`CharacterController`.** Not a Rigidbody, and not the ragdoll.

`FirstPersonController` is `[RequireComponent(typeof(CharacterController))]` and moves the
character with exactly one call, in `FixedUpdate`:

```csharp
// FirstPersonController.cs:214-217
if (m_CharacterController.enabled)
{
    m_CollisionFlags = m_CharacterController.Move(m_MoveDir * Time.fixedDeltaTime);
}
```

`Actor` *does* hold `hipRigidbody` and `headRigidbody` (`Actor.cs:64,66`), and it *does* apply
forces to them — but only for **swimming and ragdoll**, never for walking:

```csharp
// Actor.cs:335-365 — the ONLY forces Actor applies, all buoyancy/ragdoll
protected virtual void FixedUpdate()
{
    if (!ragdoll.ragdollObject.activeInHierarchy) return;   // <- inactive while alive and upright
    if (inWater) { hipRigidbody.AddForce(-Physics.gravity * 3f, ForceMode.Acceleration); ... }
}
```

The guard on the first line is the tell: while a player is alive and on their feet the ragdoll
object is **inactive**, so `Actor.FixedUpdate` returns immediately and applies nothing.

There is also a second Rigidbody, `Actor.rigidbody` (`Actor.cs:162,180`), used only for
**rotation** and for the `autoMoveActor` bot path:

```csharp
// Actor.cs:573 — rotation only
rigidbody.MoveRotation(Quaternion.Slerp(base.transform.rotation, b2, dt * 2f));
// Actor.cs:596-599 — position, but ONLY for autoMoveActor (AI), not players
if (autoMoveActor) rigidbody.position = vector4;
```

**Which method runs where:**

| Method | Timing | What it does |
|---|---|---|
| `FirstPersonController.FixedUpdate` | **FixedUpdate** | The whole simulation: input, speed, gravity, jump, `Move()` |
| `FirstPersonController.Update` | Update | Mouse look, jump-button latch, landing detection |
| `Actor.Update` → `UpdateMovement(dt)` | **Update** | Animation params, crouch policy, facing, ground projection |
| `Actor.FixedUpdate` | FixedUpdate | Water buoyancy and ragdoll damping only |

> **Trap for the netcode.** `Actor.UpdateMovement` runs in `Update`, on `Time.time` deltas
> (`Actor.cs:403`), and is additionally **rate-limited to 5 Hz for distant actors** by the
> `IsLowQuality()` path (`Actor.cs:401-420`). None of that is deterministic and none of it may
> ever be part of the replicated simulation. It is presentation.

---

## 2. Question 2 — where do the speed constants live?

**In the prefab, not in code.** Every one of them is a `[SerializeField]` with no initialiser:

```csharp
// FirstPersonController.cs:21-38 — declared, never assigned in code
[SerializeField] private float m_WalkSpeed;
[SerializeField] private float m_RunSpeed;
[SerializeField] private float m_JumpSpeed;
[SerializeField] private float m_StickToGroundForce;
[SerializeField] private float m_GravityMultiplier;
```

Reading the source alone therefore yields **zero** — the default for every one of them is `0f`,
and a `MovementSimulation` written from the source would produce a character that cannot move.
The real values are in the prefab YAML, which is force-text and readable without the Editor:

| Constant | Value | Source |
|---|---|---|
| `m_WalkSpeed` | **3.5** m/s | `Assets/Prefab/Player Fps Actor.prefab:101` |
| `m_RunSpeed` | **6.5** m/s | `…prefab:102` |
| `m_RunstepLenghten` | 0.5 | `…prefab:103` — audio only, not movement |
| `m_JumpSpeed` | **5** m/s | `…prefab:104` |
| `m_StickToGroundForce` | **10** | `…prefab:105` |
| `m_GravityMultiplier` | **1.2** | `…prefab:106` |
| CharacterController height | **1.8** m | `…prefab:82` |
| CharacterController radius | 0.3 m | `…prefab:83` |
| CharacterController slope limit | 45° | `…prefab:84` |
| CharacterController step offset | 0.3 m | `…prefab:85` |
| CharacterController skin width | 0.08 m | `…prefab:86` |

**There is no crouch speed.** The phase-00 plan's sketch assumed `CROUCH_SPEED = 2.0f`. No such
value exists anywhere in the project. Crouching changes the collider height and nothing else:

```csharp
// FpsActorController.cs:678-682
public override void StartCrouch()
{
    characterController.height = 0.5f;      // the ONLY effect of crouching on movement
    crouching = true;
}
```

and speed selection has exactly two branches, on the sprint flag alone:

```csharp
// FirstPersonController.cs:280-282
m_IsWalking = !sprinting;
speed = ((!m_IsWalking) ? m_RunSpeed : m_WalkSpeed);
```

> **This is the single most valuable finding in this document.** Had `MovementSimulation` shipped
> with the plan's assumed `CROUCH_SPEED = 2.0f`, the server would move a crouching player at 2.0
> m/s while their own client moved them at 3.5 m/s. The symptom is rubber-banding *only while
> crouch-walking* — intermittent, hard to reproduce on demand, and traceable only to a constant
> that was never written down anywhere. `MovementCoreTests.CrouchingDoesNotChangeSpeed` pins it.

**Crouch heights are asymmetric, and that is a real bug in the original.** `StartCrouch` sets
height `1.8 → 0.5` (a drop of 1.3), and `ForceEndCrouch` restores it and lifts the transform by
`1.3f / 2f`:

```csharp
// FpsActorController.cs:696-700
private void ForceEndCrouch()
{
    characterController.height = 1.8f;
    characterController.transform.position += Vector3.up * 1.3f / 2f;
}
```

The lift is hard-coded to `1.3/2` rather than derived from the height difference, so changing
either height in the prefab silently desynchronises the two. Left alone (it is the client track's file and
outside this milestone's scope) but flagged in the client track checklist.

---

## 3. Question 3 — gravity and jumping

**Gravity** is `Physics.gravity` scaled by the multiplier, applied **only while airborne**:

```csharp
// FirstPersonController.cs:199-213
if (m_CharacterController.isGrounded)
{
    m_MoveDir.y = 0f - m_StickToGroundForce;        // pinned down, NOT zero
    if (m_Jump) { m_MoveDir.y = m_JumpSpeed; m_Jump = false; m_Jumping = true; }
}
else
{
    m_MoveDir += Physics.gravity * m_GravityMultiplier * Time.fixedDeltaTime;
}
```

- `Physics.gravity.y` = **-9.81** (`ProjectSettings/DynamicsManager.asset:7`)
- effective gravity = `-9.81 × 1.2` = **-11.772 m/s²**
- peak jump height = `5² / (2 × 11.772)` ≈ **1.06 m** — matched by `MovementCoreTests.AJumpArcRisesThenFalls`

Two details that matter and are easy to miss:

1. **Grounded vertical velocity is `-10`, not `0`.** `StickToGroundForce` keeps the controller
   pressed into the surface so it does not skip down slopes and lose ground contact on alternate
   ticks. A simulation that sets `velocity.y = 0` while grounded diverges from the client on
   every slope and every stair.
2. **The jump is latched in `Update`, consumed in `FixedUpdate`** (`FirstPersonController.cs:157-160`
   and `:202-208`). A netcode input frame carries the *button state*, so the server sees the jump on
   the tick it is pressed; the original could see it a frame later depending on where the latch
   fell. Divergence of at most one tick, and unavoidable — noted, accepted.

**Jump is also entirely unguarded by stance.** There is no crouch check, no stamina, no cooldown.
Holding jump while grounded jumps every tick.

---

## 4. Question 4 — hard (deterministic) vs decorative

**Hard — must be identical on client and server. Belongs in `MovementCore`:**

| Element | Source |
|---|---|
| Speed selection from the sprint flag | `FirstPersonController.GetInput():280-282` |
| Wish direction from camera yaw + input axes | `:189-193` |
| **Normalizing the wish direction** | `:196` — see the note below |
| Horizontal velocity = wish × speed | `:197-198` |
| Grounded → `velocity.y = -StickToGroundForce` | `:201` |
| Jump → `velocity.y = JumpSpeed` | `:204` |
| Airborne → `velocity.y += gravity × dt` | `:212` |
| `Move(velocity × dt)` and its collision response | `:216` |
| Crouch → collider height | `FpsActorController.StartCrouch():680` |

**Decorative — client-only, must never influence the replicated position:**

| Element | Source |
|---|---|
| Head bob, jump bob, FOV kick | `FirstPersonController.cs:256-274, 295-309` |
| Footstep / jump / landing audio, step cycle | `:231-254` |
| Mouse look smoothing | `:312-315` |
| Animator params (`moving`, `sprinting`, `crouched`, `movement x/y`, `lean`) | `Actor.UpdateMovement():531-572` |
| Body rotation slerp toward movement | `Actor.cs:573` |
| Ragdoll drive, balance, get-up, fall-over | `Actor.cs:611-642`, `UpdateGetup():468` |
| IK aim point and weight | `Actor.UpdateFacing():513-517` |
| Water buoyancy forces | `Actor.FixedUpdate():341-365` |
| `ProjectToGround` sphere-cast snapping | `Actor.cs:576-595` |
| Low-quality 5 Hz update throttling | `Actor.cs:401-420` |

> ### The one line that changes how you think about input
>
> ```csharp
> // FirstPersonController.cs:196
> vector = Vector3.ProjectOnPlane(vector, hitInfo.normal).normalized;
> ```
>
> The `.normalized` comes **after** the input has been scaled, so **any non-zero input produces
> full speed**. A half-deflected analog stick walks at 3.5 m/s, not 1.75. That is the shipped
> game's behaviour and `MovementCore` reproduces it deliberately
> (`MovementCoreTests.PartialInputStillProducesFullSpeed`).
>
> It also means **the classic diagonal speed exploit cannot work here**: sending
> `moveX = moveZ = 127` for a vector of length 1.41 normalizes back to the same unit length.
> `InputAuthority` still normalizes the raw axes anyway — relying on a side effect of the
> movement port to be the anti-cheat means the hole reopens quietly the day someone restores the
> slope projection.

---

## 5. Known divergences in the port — expect these in the shadow-comparison logs

`MovementCore` is not a bit-exact clone, and both gaps are structural rather than oversights.

| # | Divergence | Where it shows | Why |
|---|---|---|---|
| 1 | **No slope projection.** The original projects the wish direction onto the ground normal from a `SphereCast` (`:194-196`) | Only on slopes. On flat ground the normal is straight up and the projection is a no-op, so the port is **exact** there | Needs a collision query. `Ironfront.Net.Replication` must not reference UnityEngine (architecture.md § 5.1) |
| 2 | **No collision resolution.** `MovementCore.Step` returns the motion it *wants* | Against any geometry | `CharacterController.Move` does this on both sides. Returning a delta is what keeps the seam honest |
| 3 | ~~**Fixed timestep mismatch, and this one is a live risk.**~~ **Closed — A5 decided B.** The project's fixed timestep stays where it is; prediction runs its own 30 Hz accumulator | Nowhere, now | `NetPredictionClock` owns the netcode's clock; `SIM_TICK_RATE` and `Time.fixedDeltaTime` no longer have to agree |

Divergence 3 is worth stating plainly: at 0.02 the original applies gravity 50 times a second; at
1/30 the simulation applies it 30 times. Same acceleration, same trajectory in continuous time —
but the *discrete* positions differ, and client prediction compares discrete positions. The
client must run prediction at `1/SIM_TICK_RATE`, not at the project's fixed timestep.

**How it was closed, and the thing that makes it worth reading twice.** the client track chose option B —
keep the physics rate, give prediction its own accumulator
(`Assets/Scripts/Net/Shared/NetPredictionClock.cs`). Option A, which this document's checklist
originally recommended, would not have worked at all: `Time.fixedDeltaTime` is *assigned at
runtime* by two files that have nothing to do with netcode —

| File | Line | Assignment | When |
|---|---|---|---|
| `IngameMenuUi.cs` | 29 | `Time.fixedDeltaTime = Time.timeScale / 60f` | `Hide()`, reached from `Awake()`, so before frame 1 |
| `FpsActorController.cs` | 497 | `Time.fixedDeltaTime = Time.timeScale / 60f` | every slow-motion toggle |

— so the live rate is 1/60 in normal play and 0.2/60 in slow motion, and the 0.02 in
`ProjectSettings/TimeManager.asset` is never what the game actually runs at. Writing 0.0333 into
that asset would have been overwritten before the first physics step, and the resulting
mispredictions would have looked like a bug in the simulation rather than in the clock.
**Do not read a timestep out of `TimeManager.asset` and assume it is live.**

---

## 6. What the client track needs to expose (task 6, revised)

The plan asks A for six members on `Actor`. Given § 0 — that `Actor` does not own movement —
**five of the six are the wrong request**, and asking for them would mean A adding pass-throughs
on `Actor` that forward to a controller that forwards to a `CharacterController`.

| Plan's request | Status | Where it really is |
|---|---|---|
| `Vector3 NetVelocity { get; set; }` | **Redirect** | `FirstPersonController.Velocity()` exists (`:111`); the *setter* does not — `ResetVelocity()` (`:116`) is the only mutator |
| `bool IsGrounded { get; }` | **Already exists** | `FirstPersonController.OnGround()` (`:126`), surfaced via `ActorController.OnGround()` |
| `void CharacterMove(Vector3 delta)` | **Redirect** | This is `m_CharacterController.Move`, private inside `FirstPersonController` |
| `byte PackStateFlags()` | **Missing** | Backing state is spread across `Actor.dead`/`fallenOver`/`inWater` (`:78,81,106`), `IsAiming()` (`:190`), `FpsActorController.crouching` (`:90`), `IsSprinting()` (`:713`), `Actor.IsSeated()` (`:873`) |
| `void ApplyStateFlags(byte flags)` | **Missing** | Same fields, write side |
| `Hitbox[] GetHitboxes()` | **Nearly exists** | `Actor.hitboxColliders` is private (`:83`), populated in `Awake` (`:181`). `Hitbox.cs` exists with `LAYER`/`RAGDOLL_LAYER`/`SEATED_LAYER` |

Rather than six edits to a 1188-line file A owns, the checklist asks for **one new component**
that owns the seam. See
[`plans/replication/integration-checklist.md`](../plans/replication/integration-checklist.md).

---

## 7. Snapshot-relevant state

For `SnapshotBuilder`, mapping spec § 4.3 fields onto real members:

| Snapshot field | Real source | Note |
|---|---|---|
| `health` u8 0..100 | `Actor.health` — **`float`**, `[NonSerialized]`, init `100f` (`Actor.cs:72`) | Clamped, not cast: `ResupplyHealth` (`:1154`) can push it up, damage can take it negative |
| `team` u8 | `Hurtable.team` — **`int`** (`Hurtable.cs:3`), set via `Actor.SetTeam` (`:1056`) | Values 0/1 in practice (`ScoreUi.AddScore`, `Actor.cs:721`) |
| `weaponId` / `ammoInClip` | `Actor.activeWeapon` (`:109`), `Actor.weapons[5]` (`:112`), `activeWeaponSlot` (`:114`) | Weapon id needs a stable registry — **open item**, see the checklist |
| `IsAlive` | `!Actor.dead` (`:78`) | |
| `IsCrouching` | `FpsActorController.crouching` (`:90`) | Public field |
| `IsProne` | **does not exist** | No prone in this codebase. Bit 2 stays 0 in v1 |
| `IsSprinting` | `FpsActorController.IsSprinting()` (`:713`) | Composite: not crouching, not aiming, not reloading, not seated |
| `IsAiming` | `Actor.IsAiming()` (`:190`) | Public |
| `IsInWater` | `Actor.inWater` (`:106`) | Public, set in `Update` (`:372`) |
| `IsRagdoll` | `Actor.fallenOver` (`:81`) / `ragdoll.IsRagdoll()` | Corpses are never synced (AD-4) |
| `IsSeated` | `Actor.IsSeated()` (`:873`) | Public |

**`IsProne` has no implementation.** `InputButtons.Prone` (bit 6) and `ActorStateFlags.IsProne`
(bit 2) are both in the frozen protocol, and nothing in the game produces or consumes them. Left
as-is: they are reserved wire space, and removing them would be a `PROTOCOL_VERSION` bump for no
gain.

---

## 8. Verification

Every constant and behaviour above is pinned by
[`MovementCoreTests`](../Ironfront.Net.Replication.Tests/MovementCoreTests.cs) (18 tests) and
printed by `MeasurementReportTests.PrintTheMovementConstantTable`. If the prefab changes, those
tests fail — which is the point: the numbers live in a YAML file nobody diffs, so the tripwire
has to be somewhere people look.
