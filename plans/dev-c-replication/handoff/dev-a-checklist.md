# Dev C → Dev A — the precise checklist

**From:** Dev C (replication) · **Date:** 2026-08-12 · **Milestone:** M0 closing, M1 starting
**Replaces:** the 4-item request in
[phase-00 § Task 6](../phases/phase-00-foundation.md#task-6--send-your-requests-to-dev-a-half-a-day-do-it-in-week-1)

Everything that could be built without the Unity Editor is built, tested and merged. What is left
needs the Editor, which under conventions.md § 1.3 means it needs you.

Items are ordered by what unblocks the most. **A1 and A2 unblock everything else.**

---

## The original request has changed, and here is why

The phase-00 plan asked you for six new members on `Actor.cs`. **Do not do that.** The request
was written on the assumption that `Actor.cs` owns character movement. It does not.

Movement is three hops away, and the last hop leaves the assembly entirely:

```
Actor.cs:528              controller.Velocity()        <- consumes, never computes
FpsActorController.cs:157 controller.Velocity()        <- forwards
Assets/Plugins/Assembly-CSharp-firstpass/.../FirstPersonController.cs:216
                          m_CharacterController.Move(...)   <- the real simulation
```

Full derivation with line references: [`docs/movement-analysis.md`](../../../docs/movement-analysis.md).

So five of those six members would have been pass-throughs on a 1188-line file you own, forwarding
to a controller that forwards to a `CharacterController`. Instead the ask is **one new component**
(`NetMovementAgent`) that talks to the `CharacterController` directly. Smaller ask, no edits to
`Actor.cs`, and nothing calls it until you wire it — so it cannot regress existing gameplay.

---

## A1 — Run `build-libs.ps1` and confirm the DLLs load  ⏱ ~15 min  🔴 blocks everything

```powershell
pwsh tools/build-libs.ps1
ls Ironfront_Reborn/Assets/Plugins/Ironfront.Net.*.dll
```

Then open the Editor and confirm the Console is clean.

**The trap, and it costs hours if you meet it cold:** `netstandard2.1` plus `Span<byte>` needs
`System.Memory.dll`, `System.Buffers.dll`, `System.Runtime.CompilerServices.Unsafe.dll` and
`System.Numerics.Vectors.dll` present alongside our three DLLs. Copy only the main assemblies and
Unity throws `TypeLoadException` — an error that says nothing about a missing assembly.
`build-libs.ps1` copies all four and warns if it cannot find them in the NuGet cache. **If it
warns, stop and tell me** rather than working around it.

**Report back:** "DLLs load, Console clean" or the exact error text.

---

## A2 — Install the drop-in scripts  ⏱ ~10 min  🔴 blocks A3, A4

```powershell
mkdir -Force Ironfront_Reborn/Assets/Scripts/Net/Shared
mv plans/dev-c-replication/handoff/unity-dropin/*.cs Ironfront_Reborn/Assets/Scripts/Net/Shared/
```

Open the Editor so it generates `.meta` files, then commit the `.cs` **and** the `.meta` files
together. Full notes: [`unity-dropin/README.md`](unity-dropin/README.md).

Three files, all mine under conventions.md § 7 — `MovementSimulation.cs`, `NetMovementAgent.cs`,
`MovementShadowCompare.cs`.

---

## A3 — Run the shadow comparison and send me the summary  ⏱ ~30 min play + 5 min report  🔴 closes phase-00 criterion 8

Attach `MovementShadowCompare` to the player prefab, press Play, and move around deliberately:

| Do this | For | Watching for |
|---|---|---|
| Walk forward/back/strafe on flat ground | 30 s | **Must be clean.** Any divergence here is a real bug |
| Sprint on flat ground | 30 s | Must be clean |
| Crouch-walk on flat ground | 30 s | Must be clean — see the note below |
| Jump repeatedly, flat ground | 30 s | Small divergence on the landing tick is expected |
| Walk up and down slopes | 60 s | **Divergence expected and documented** |
| Walk into walls, along walls | 60 s | **Divergence expected and documented** |

`MovementShadowCompare` is read-only by construction — there is no code path in it that writes to
the `CharacterController`, the transform, or any `Actor` field. It cannot change how the game
plays.

On exiting Play mode it prints a one-line summary (`[MovementShadowCompare] ...`). **Send me that
line plus any `MOVEMENT DIVERGED` warnings from flat ground.**

> **Why crouch-walking is on that list specifically.** The phase-00 plan assumed a
> `CROUCH_SPEED` of 2.0 m/s. No such value exists anywhere in the project — crouching changes the
> collider height and nothing else (`FpsActorController.cs:678-682`), and speed selection has two
> branches on the sprint flag alone (`FirstPersonController.cs:280-282`). I built the simulation
> with no crouch speed. If crouch-walking diverges, my reading is wrong and I need to know before
> M1 lands, because the symptom in production would be rubber-banding *only while crouched* —
> intermittent, and traceable to a constant nobody ever wrote down.

---

## A4 — Add `NetMovementAgent` to the player prefab  ⏱ ~10 min  🟡 blocks M1 integration

Add the component to the same GameObject that has the `CharacterController` (it is
`[RequireComponent]`, so the Editor will insist anyway). Do not wire it to anything yet — M1
integration calls `Tick()`; until then it just sits there holding state.

**Report back:** the prefab path and that the Console is still clean.

---

## A5 — Confirm the fixed-timestep decision  ⏱ ~15 min  🟡 real M1 risk, needs your call

`ProjectSettings/TimeManager.asset` has `Fixed Timestep: 0.02` — **50 Hz**. `SIM_TICK_RATE` is
**30**. Client prediction and the server must step the *same* dt or prediction disagrees with
authority on every airborne tick.

Three options; the choice is yours because it is your project:

| Option | Effect | Cost |
|---|---|---|
| **A. Set `Fixed Timestep` to `0.0333`** (recommended) | Everything lines up, one setting | Physics runs at 30 Hz. Ragdolls will feel slightly different |
| **B. Keep 0.02, run prediction on its own 30 Hz accumulator** | Physics unchanged | Prediction no longer rides `FixedUpdate`; more moving parts |
| **C. Change `SIM_TICK_RATE` to 50** | Everything lines up | **Protocol change** — PR, 2 approvals, `PROTOCOL_VERSION` bump, and 66% more snapshot bandwidth |

I lean **A**. `MovementSimulation.FixedDeltaTime` already exposes `1/SIM_TICK_RATE`, so the
netcode is correct under any of the three; this decides what happens to the rest of the game.

**Reply with A, B or C.** This is the one item I genuinely cannot decide for you.

---

## A6 — Weapon id registry  ⏱ ~30 min  🟡 blocks the snapshot weapon field

Snapshots carry `weaponId` as a `u8` (spec § 4.3) and I have no stable id → weapon mapping.
`Actor.activeWeapon` / `Actor.weapons[5]` / `activeWeaponSlot` are object references, and a
reference is not something that survives a network hop.

I need a **stable, ordered list of weapons with fixed integer ids** — stable meaning id 3 is the
same weapon next week and on every machine. A `ScriptableObject` registry, or a serialized array
on `WeaponManager`, or just a documented enum. Your call on the shape; I only need the ids to be
fixed and readable from code.

Until this lands, snapshots ship `weaponId = 0` and the field is inert.

---

## A7 — Confirm the map bounding box empirically  ⏱ ~10 min  🟢 confirmation only

I already measured this without the Editor — the scenes are force-text YAML, so `LevelBounds`
reads straight out of `Dustbowl.unity`: 1700 × 700 × 1600 centred at (-70.8, 207.6, -88.6), worst
playable coordinate 920.8 m against a `POS_MAX` of 2048. That is 2.2× headroom, and the freeze
recorded it as settled.

What I could not check from YAML is whether a player can actually *reach* somewhere past 2048 m
via a vehicle, a lift, or an out-of-bounds route. Dustbowl does have ~1,900 transforms past 2048 m,
all backdrop terrain outside the play box.

**If a player can get past ±2048 m on any map, tell me** — position quantization clamps there and
the actor sticks to an invisible wall. Otherwise no action.

---

## A8 — The 60-minute `Actor.cs` walkthrough  ⏱ 0 min  ✅ **cancelled**

Phase-00 criterion 10 asked for a session where you explain the movement code in `Actor.cs`. It is
not needed, because `Actor.cs` does not contain the movement code — see the top of this document.

What that session was meant to produce now exists as
[`docs/movement-analysis.md`](../../../docs/movement-analysis.md), written from the source and
pinned by 18 unit tests. **Please skim § 0 and § 5 and tell me if anything contradicts what you
know**, particularly § 5's known divergences. That review is worth more to me than the meeting
would have been, and costs you ten minutes instead of an hour.

---

## Two things I found in your files — reported, not touched

conventions.md § 7 says to tell you rather than edit. Neither is urgent and neither is mine.

1. **`ForceEndCrouch` hard-codes the stand-up lift.** `FpsActorController.cs:696-700` sets
   `height = 1.8f` and lifts the transform by `1.3f / 2f`. The `1.3` is `1.8 - 0.5` written out by
   hand, so changing either height in the prefab silently desynchronises the lift from the
   collider and the player stands up half-buried in the floor. Deriving it (`(stand - crouch) / 2`)
   would make it self-consistent.

2. **`IsProne` has no implementation.** `InputButtons.Prone` (bit 6) and `ActorStateFlags.IsProne`
   (bit 2) are both in the frozen protocol and nothing in the game produces or consumes them. I
   have left them as reserved wire space — removing them would be a `PROTOCOL_VERSION` bump for no
   gain. Flagging so it does not surprise you later.

---

## What is already done, so you can plan around it

M0 and the offline half of M1 are merged and green: 283 tests, 0 warnings.

| Available now | What it gives you |
|---|---|
| `ITransportClient` / `ITransportServer` | The frozen API. Code against this and nothing else |
| `LoopbackTransport` | In-memory client+server, no socket. Test prediction against 200 ms latency inside one Editor process |
| `NetworkSimulator` | 5 impairments, fixed seed. `IRONFRONT_SIM=typical` / `bad` / `awful` at runtime, no rebuild |
| `MovementCore` / `MovementSimulation` | The shared simulation, real constants, 18 tests |
| `SnapshotBuilder`, `DeltaEncoder`, `DeltaDecoder` | Full and delta snapshots. Measured: 44.7% saving, 10.94 KB/s per client at 48 actors |
| `ServerTickScheduler`, `InputAuthority`, `ClientSession` | 30 Hz pacing, anti-cheat input handling |
| `BitWriter` / `BitReader` | Dev B's, with the conformance suite that judges them |

Two gotchas worth knowing before you meet them:

- **`OnMessage` hands you a pooled buffer that is recycled the moment your handler returns.** Keep
  the data and you will read someone else's packet later. Copy it if it must outlive the callback.
  This is a genuinely nasty bug to trace.
- **`SimulatorConfig.ReorderPercent = 100` reorders nothing.** Reordering is an extra delay on the
  chosen packets, so choosing all of them shifts the stream uniformly and preserves order exactly.
  Useful values are well under 50. It is pinned as a test so it cannot regress.

---

## Summary — what I need back

| # | Item | Effort | Reply with |
|---|---|---|---|
| A1 | DLLs load in the Editor | 15 min | "clean" or the error |
| A2 | Drop-in scripts installed + `.meta` committed | 10 min | done |
| A3 | Shadow-comparison run | 35 min | the summary line + flat-ground warnings |
| A4 | `NetMovementAgent` on the player prefab | 10 min | prefab path |
| A5 | **Fixed timestep: A, B or C** | 15 min | **A, B or C** |
| A6 | Weapon id registry | 30 min | how to read the ids |
| A7 | Can a player pass ±2048 m? | 10 min | yes/no |
| A8 | Skim the movement analysis | 10 min | anything that contradicts what you know |

**Roughly 2 hours 15 minutes of Editor work.** A5 is the only one that needs a decision rather
than a check, and A3 is the one most likely to find something.
