# P25 — the line the diff blames was already dead in the original build

Investigation report, 2026-09-21. Phase: [`phases/phase-p25-ragdoll-drive.md`](../phases/phase-p25-ragdoll-drive.md).

**The premise does not survive.** P25 opens by saying the ragdoll diverged from the original
Ravenfield because `ActiveRaggy.cs` lost one line — `jointDrive.mode = JointDriveMode.Position;` —
and that PhysX 4 therefore reads `positionSpring` / `positionDamper` differently, so the drive
needs recalibrating against a measured baseline.

In Unity **5.4.0f3**, the engine `Ravenfield.exe` itself ships, that property is
`[Obsolete("JointDriveMode is obsolete")]`, its setter's entire IL body is `ret`, and the
`JointDrive` struct has no field to store it in. The assignment stored nothing and reached nothing.
It was already dead in the original build, so deleting it changed no behaviour, and it cannot be
the cause of any difference in how the ragdoll feels.

Every drive parameter that *does* reach PhysX is identical across the two trees, on all **62**
ConfigurableJoints. The phase's §5.4 asked for one check "first" precisely because it could
invalidate the premise. It does.

Two candidate differences survive, and this report measures both. One of them moves the drive
**4.3×**; it is not the one the plan was going to spend a week calibrating.

---

## 1. The feasibility gate answered yes (§4.3)

The plan's §4.3 is a hard gate: before any measurement code, prove the recompiled
`Assembly-CSharp.dll` is a drop-in replacement for the shipped one. It is.

| Step | Result |
|---|---|
| Copy `tmp/Ravenfield/` to a sandbox | `tmp/rf-gate/`, 190.8 MB, original untouched |
| Drop in `tmp/recovered/build/*.dll` unmodified | `Assembly-CSharp.dll` md5 `dd09bf30…`, `Assembly-CSharp-firstpass.dll` md5 `8de4b32e…` |
| Launch | boots to the Beta 5 main menu |
| Enter a match | ISLAND → loadout → DEPLOY → first-person, 100 HP, 30/180 |
| Play | ~10 min, 50 bots, score ran 0-0 → **255-207**, capture points changed hands repeatedly |
| Player death | grenade suicide; death-cam ragdoll, respawn, redeploy, all normal |
| Exceptions | **zero**, across the whole session |

Evidence: [`tools/recovered/ragdoll-dll-swap-gate.output_log.txt`](../../tools/recovered/ragdoll-dll-swap-gate.output_log.txt)
— 81 lines, `Initialize engine version: 5.4.0f3`, the recompiled assemblies loading by path, and no
`Exception` / `Error` / `MissingMethod` / `MissingField` / `TypeLoad` anywhere in it. The only
warning is `Look rotation viewing vector is zero` ×3, which is benign.

That green is worth something specific: menu, map select, weapon loadout, deploy and scoring are
all `Assembly-CSharp` code, so they exercise the recompiled assembly rather than just proving it
loads. Deaths ran `ActiveRaggy.Ragdoll()` repeatedly — the player's own, plus bot deaths visible as
bodies and blood decals throughout — and a serialization mismatch there would have thrown on the
first one and filled the log. (How many bot deaths is not claimed: Ravenfield's score counts
capture ticks as well as kills, so the 462 points scored do not convert to a death count.)

**So the measurement rig the plan wanted is buildable.** That conclusion stands whatever happens to
the rest of the phase, and it is §6.1b of the acceptance list. The rig was not then built, because
of what §3 below turned up.

Toolchain, also confirmed: `tmp/recovered/src/**/*.csproj` rebuilds under the .NET 8 SDK with
`-p:Nullable=disable -p:TreatWarningsAsErrors=false` (the repo's root `Directory.Build.props` leaks
into `tmp/` and has to be neutralised, exactly as `Ironfront_Reborn/Directory.Build.props` does for
the Unity tree). Both assemblies build with 0 errors. Adding probe code and recompiling is a solved
problem, not a risk.

## 2. What the plan blamed

```csharp
// tmp/recovered/.../ActiveRaggy.cs:103-106      // Ironfront_Reborn/.../ActiveRaggy.cs:103-105
jointDrive = default(JointDrive);                 jointDrive = default(JointDrive);
jointDrive.mode = JointDriveMode.Position;        // <- the plan's culprit
jointDrive.maximumForce = 1E+13f;                 jointDrive.maximumForce = 1E+13f;
SetDrive(1000f, 3f);                              SetDrive(1000f, 3f);
```

The plan's reasoning: Unity 5.5 moved PhysX 3.3 → 3.4 and removed `JointDriveMode` because 3.4
always applies position and velocity drive together; therefore the original ran in position-only
mode, we run in position-and-velocity mode, therefore the units changed, therefore recalibrate.

The first clause is true. Every clause after it depends on that line having done something.

## 3. What Unity 5.4 actually does with that line

Read out of `tmp/Ravenfield/Ravenfield_Data/Managed/UnityEngine.dll` — the 2016-07-27 assembly the
shipped game loads — not from documentation or recollection:

| | Unity 5.4.0f3 (original) | Unity 6000.3.21f1 (ours) |
|---|---|---|
| `JointDrive` fields | `m_PositionSpring`, `m_PositionDamper`, `m_MaximumForce` | + `m_UseAcceleration` |
| `JointDrive.mode` | property, **no backing field**, `[Obsolete("JointDriveMode is obsolete")]` | removed |
| `get_mode` IL | `16 2A` → `ldc.i4.0; ret` — always returns `None` | — |
| `set_mode` IL | `2A` → `ret` — **stores nothing** | — |
| `JointDriveMode` | `None=0, Position=1, Velocity=2, PositionAndVelocity=3` | type absent |

The struct marshals three floats to native. There is nowhere for a mode to live and nothing that
transmits one. `jointDrive.mode = JointDriveMode.Position;` compiled, in 2016, to a call that
returns immediately.

This is the whole finding. Not "the line is less important than the plan thought" — the line never
did anything, in either engine.

## 4. The check §5.4 asked for first

> `ConfigurableJoint.targetAngularVelocity` — **kiểm tra trước** … nếu nó đang mặc định 0 thì damper
> hành xử như damping thuần và giả định "công thức đổi" cần xem lại

Checked, exhaustively, across both trees:

| | Ironfront | Recovered original |
|---|---|---|
| Prefabs carrying ConfigurableJoints | 4 | 3 |
| ConfigurableJoints | 37 | 25 |
| `m_TargetAngularVelocity` = (0,0,0) | 37 / 37 | 25 / 25 |
| `m_RotationDriveMode` = 1 (Slerp) | 37 / 37 | 25 / 25 |
| `useAcceleration: 1` in any prefab | 0 | 0 |
| Runtime writes to `targetAngularVelocity` | **none** | **none** |

62 of 62 joints, zero. Nothing in either C# tree ever writes that field. So the damper term has
always been damping toward zero on both sides, which is what §5.4 said would mean the assumption
needs re-examining.

## 5. Everything else that reaches PhysX is identical

| Element | Original | Ironfront | Same? |
|---|---|---|---|
| `SetDrive` on wake | `(1000, 3)` `ActiveRaggy.cs:106` | `(1000, 3)` `ActiveRaggy.cs:105` | ✓ |
| `SetDrive` alive | `(700, 3)` `Actor.cs:213` | `(700, 3)` `Actor.cs:291` **and** `:347` | ✓ values |
| `SetDrive` dead | `(50, 1)` `Actor.cs:711` | `(50, 1)` `Actor.cs:1070` | ✓ |
| `maximumForce` | `1E+13` | `1E+13` | ✓ |
| Drive channel | `slerpDrive` | `slerpDrive` | ✓ |
| Target-pose maths | `ConfigurableJointExtensions.cs` | byte-identical, sha256 `8A5E844E…` | ✓ |
| Angular limits | all 12 joints at 0, motion = Limited | all 12 joints at 0, motion = Limited | ✓ |

The extra `(700, 3)` at `Actor.cs:347` is not a divergence: it is Ironfront's
`EnterNetworkDeployedState()`, the netcode path for a network-deployed player, installing the same
values through the same call. The plan's "everything else is identical" holds for the drive.

Raw: [`tools/recovered/ragdoll-drive-facts.json`](../../tools/recovered/ragdoll-drive-facts.json).

## 6. `ActiveRaggy.cs` does not differ by one line

Comments and blank lines stripped, 43 code lines differ, in four groups:

1. **20 lines — decompiler rendering.** Enum members printed `X = 0, XNegative = 1, …` in our tree
   and `X, XNegative, …` in the recovered one. Same IL, two printers. This is P24's bucket (d).
2. **1 line — the dead `mode` assignment** above.
3. **2 lines — a required Unity 6 API migration.** `rigidbody.velocity` → `rigidbody.linearVelocity`.
4. **20 lines — an Ironfront addition.** `RigidbodyForBone(HumanBodyBones)`, additive, touches no
   drive parameter.

Only group 2 is what the plan is about, and group 2 is inert.

## 7. What IS different, and how far it moves the drive

Two candidates survive:

- **fixed timestep** — 0.02 (50 Hz) originally, 1/60 here. Deliberate, issue #123, not revertible.
- **`JointDrive.useAcceleration`** — a field Unity 6 has and 5.4 did not. `ActiveRaggy` builds its
  drive from `default(JointDrive)`, so here it is `false`. What 5.4's native drive did is **not
  expressed in managed metadata** and is not knowable from the assembly.

So instead of guessing, measure the sensitivity to each.
[`Ironfront_Reborn/Assets/Editor/RecoveredPort/RagdollDriveProbe.cs`](../../Ironfront_Reborn/Assets/Editor/RecoveredPort/RagdollDriveProbe.cs)
instantiates the real `Player Fps Actor` ragdoll in an isolated preview scene, anchors the hip,
commands a 30° pose offset through the shipped `SetTargetRotationLocal`, and steps
`PhysicsScene.Simulate(dt)` by hand for 3 simulated seconds — 32 conditions, 0 invalid.

Because the probe passes `dt` as an argument, **`TimeManager` is never touched.** The plan proposed
editing it temporarily and remembering not to commit the change, a risk its own table scores 15.
That risk is now unnecessary, and both timesteps are compared inside a single run.

Peak angular velocity across the chain, gravity off (rad/s):

| Drive | 60 Hz, accel off | 60 Hz, accel **on** | 50 Hz, accel off |
|---|---|---|---|
| `(1000, 3)` wake | 44.18 | **10.31** (0.23×) | 39.82 (0.90×) |
| `(700, 3)` alive | 39.10 | **9.15** (0.23×) | 36.01 (0.92×) |
| `(50, 1)` dead | 12.29 | **3.76** (0.31×) | 12.23 (1.00×) |

Steady-state pose error after 3 s, same conditions (degrees):

| Drive | 60 Hz, accel off | 60 Hz, accel **on** | 50 Hz, accel off |
|---|---|---|---|
| `(1000, 3)` wake | 0.85 | 1.58 | 0.87 |
| `(700, 3)` alive | 0.84 | **2.67** | 0.84 |
| `(50, 1)` dead | 1.08 | **7.06** | 0.98 |

**`useAcceleration` changes the drive by a factor of ~4.3. The 50 → 60 Hz timestep changes it by
0 – 10%, and by essentially nothing on the death drive.** If a real difference in ragdoll feel
exists, `useAcceleration` is roughly forty times the lever the timestep is — and it is one boolean,
not the five-dimensional calibration space §5.4 proposed.

Raw: [`tools/recovered/ragdoll-drive-response.json`](../../tools/recovered/ragdoll-drive-response.json).

### What the probe's own controls establish, and what they do not

A zero-drive control row (`spring = 0, damper = 0`) collapses peak angular velocity from ~44 rad/s
to **0.008** and never approaches the commanded 30°, which is what licenses reading the rows above
as measurements of the drive rather than of something else moving the chain.

That control is **not** a gravity control and the artifact says so. With the drive off the chain
does not visibly move under gravity either, so the gravity column's near-identical rows have two
candidate causes it cannot separate. Gravity is proven live instead by a separate harness
self-test: one free rigidbody, one simulated second, fell **4.987 m** against 4.905 expected,
recorded in the JSON as `harnessSelfTest.gravityConfirmed`. Without that the gravity column would
have been a green that proves nothing.

One thing deliberately **not** claimed: all 12 joints have every angular limit at zero with motion
= Limited (identically in both trees), yet the drive reaches within 0.84° of a 30° target. It also
reaches within 0.85° at `maximumForce = 100`, so the shipped `1e13` is not what overcomes those
limits. Why gravity alone does not move the chain while a 100-unit drive does is not established
here, and no mechanism is asserted.

## 8. What this means for the phase

The goal in §1 — bring the ragdoll's feel close to the original, by measurement rather than by
taste — is untouched. What has gone is the *diagnosis*, and with it the reason to believe there is
anything to fix:

- The plan inferred a divergence **from the diff**. With the diff inert and every other drive
  parameter identical, there is now **no evidence that the ragdoll behaves differently at all**.
  Nobody has measured the two side by side; the belief rested on reading `ActiveRaggy.cs`.
- Calibrating `positionSpring` / `positionDamper` away from `(1000,3)/(700,3)/(50,1)` would be
  moving values that provably match the original, to chase a difference not yet shown to exist.
  That is how a phase spends three rounds and converges on nothing.

So the next question is not "what should the drive values be" but **"is there a difference to
explain?"** — and §1 of this report says that is now cheap to answer, because the DLL swap works.

The ordering that follows from the evidence:

1. **Establish the difference exists.** Same scenario both sides — shipped `Ravenfield.exe` via the
   proven DLL swap, and Ironfront — and compare. If the numbers match, P25 closes as a negative
   result and nothing is calibrated.
2. **If it differs, try `useAcceleration = true` first.** One line in `ActiveRaggy.Awake`, 4.3× the
   effect of anything else on the table, and cheap to falsify.
3. **Only then consider spring/damper.** And only with a baseline in hand.

The 50 Hz variable can be measured at any time with the probe, with no `TimeManager` edit and no
risk of resurrecting #123.

## 9. Reproducing

```bash
# facts: engine metadata + IL, prefab joints, source call sites
pwsh tools/ragdoll_drive_facts.ps1          # needs tmp/Ravenfield + UNITY_PATH

# drive sensitivity: 32 conditions on the real ragdoll
# Unity menu: Ironfront/Recovered Port/Measure Ragdoll Drive Response

# rebuild the recovered assemblies (the DLL-swap path)
dotnet build tmp/recovered/src/Assembly-CSharp/Assembly-CSharp.csproj -c Release \
  -o tmp/rf-build/main -p:Nullable=disable -p:TreatWarningsAsErrors=false
```

`tools/ragdoll_drive_facts.ps1` refuses to run without the shipped build rather than emitting a
file with holes in it; the committed JSON is the durable artifact and `tmp/` is disposable.

## 10. Not done, and why

- **No `ragdoll-baseline.json` from the shipped build** (§6.1). Building the instrumented DLL and
  the two-sided harness is the bulk of the phase's estimate, and §8 argues it should now be aimed
  at a different question than the plan aimed it at. The gate that blocked it is cleared, so it is
  a decision about what to spend, not a blocker.
- **No variable-separation table against the original** (§6.3). Same reason. The 50 Hz half of it
  *is* measured, on our side, in §7.
- **`TimeManager` untouched**, still 60 Hz (§6.5).
