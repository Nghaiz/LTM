# Dev C → Dev A — the precise checklist

**From:** Dev C (replication) · **Date:** 2026-08-12 · **Milestone:** M0 closing, M1 starting
**Replaces:** the 4-item request in
[phase-00 § Task 6](../phases/phase-00-foundation.md#task-6--send-your-requests-to-dev-a-half-a-day-do-it-in-week-1)

Everything that could be built without the Unity Editor is built, tested and merged. What is left
needs the Editor, which under conventions.md § 1.3 means it needs you.

Items are ordered by what unblocks the most.

> **Round 2 — 2026-08-12, afternoon.** A1, A2 and A5 are closed. Three more PRs merged since
> (#12 yours, #13, #14): the two bugs you reported — cannot quit, no logs — plus four Unity 6
> errors in the scene files. Everything verifiable without the Editor has been verified;
> everything else is now **group V below, and it blocks A3.** Start there, not at A3.
>
> Step-by-step version of the same thing, with the exact clicks:
> [`dev-a-gate-board.html`](dev-a-gate-board.html).

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

## V — Confirm today's three PRs  ⏱ ~25 min  🔴 blocks A3

Run V1→V5 in order. If V1 fails, the other four are meaningless — stop and tell me.

**Close Unity before `git pull`.** PR #14 edits scene and prefab files directly, and an open
Editor holds the scene in memory and writes it back on save.

```powershell
cd d:\Coding\LTM
git checkout develop
git pull
pwsh tools/build-libs.ps1
```

| # | Do | Report back |
|---|---|---|
| **V1** | Reopen the project, open `Assets/Scenes/Menu.unity`, read the Console. Count **red lines only** — Unity 6 still warns about legacy serialization in 60 places and that is known and deliberate. | `"0 error"`, or each red line verbatim **with the scene name** |
| **V2** | Press Play. Line one of the Console gives the log file path. Open it; the first ten lines are an assembly census. | `"census clean"`, or paste the ten `assembly ...` lines |
| **V3** | Confirm that log file exists, is non-empty, grows while the game runs, and ends with `session ended`. | `"log written"`, or what is missing |
| **V4** | In Play mode: Esc → menu → Quit. | Which of the three rows below |
| **V5** | Open `Island.unity`, `Dustbowl.unity`, `Splash.unity` once each, clearing the Console before each. No need to Play. | `"clean"` ×3, or the red line with its scene name |

**V2 is the one that can change a decision.** If the census reports
`[IronfrontLog] <name> is loaded 2 times`, then Unity 6's .NET Standard profile already supplies
that shim and the copy in `Assets/Plugins` is both redundant and actively breaking things: delete
the named `.dll` and `.dll.meta`, and tell me which, so I can stop `build-libs.ps1` copying them.
A duplicated `System.Memory` never produces an error that names `System.Memory` — it produces a
`TypeLoadException`, or a `Span<byte>` that will not assign to a `Span<byte>`, and an afternoon
spent reading the wrong file.

**V4 — three outcomes, and they mean different things.** `Application.Quit()` is a documented
no-op in the Editor, which is why both Quit buttons did nothing; `AppQuit.Quit()` now stops Play
mode in the Editor and quits for real in a build.

| Play mode | `[AppQuit] quit requested` in Console | Meaning |
|---|---|---|
| Stops | yes | Working. Done. |
| Keeps running | **no** | The button is **not wired** to the method in the scene — Inspector → the Quit button's **On Click ()** must point at the `IngameMenuUi` / `MainMenu` object and select `Quit()`. Check both: the main-menu button and the Esc-menu button are different buttons in different scenes. |
| Keeps running | yes | The code ran and the Editor did not stop. Different bug — tell me immediately. |

**V5 — why I cannot do this one from outside.** I scanned every scene's YAML and I am *certain*
class 92 (GUILayer) was the only removed class in `Menu.unity` — certain because Unity said so
itself, reporting GUI Layer and nothing else while that file also contains FlareLayer, Animator,
ParticleSystem and Canvas. The other three scenes additionally carry `TextMesh`, `Animation`,
`Cloth`, `Terrain` and others. Every hosting module is in the manifest so they are very likely
fine, but **that is suspicion, not verification**, and opening each scene once settles it free.

---

## A3 — Run the shadow comparison and send me the summary  ⏱ ~30 min play + 5 min report  🔴 closes phase-00 criterion 8

**Do group V first.** And do A3 before A4 — see A4 for why.

Attach `MovementShadowCompare` to the player prefab, press Play, and move around deliberately:

> **The Console prints one line the moment you press Play**, before you have moved:
> `[MovementShadowCompare] attached to '...' and ticking.` If that line is absent, the component
> is not running — **do not spend thirty minutes playing.** It is on a GameObject that was never
> spawned, or disabled, or on the scene object rather than the prefab. I added that line in #13
> precisely because it was missing: the old version was completely silent when attached to the
> wrong object, which is indistinguishable from a broken logger. A `ran zero ticks` warning on
> exit means the same thing, and says where to look.

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

## A4 — Add `NetMovementAgent` + `NetPredictionClock` to the player prefab  ⏱ ~10 min  🟡 blocks M1 integration

**Do this after A3, not before.** See the warning below.

Add both components to the same GameObject that has the `CharacterController` (`NetMovementAgent`
is `[RequireComponent]`, so the Editor will insist anyway). Then **untick `NetPredictionClock`**
and save.

> **Why the clock ships disabled.** `NetMovementAgent` sits inert — nothing calls `Tick()` until
> M1 integration. `NetPredictionClock` is the thing that calls it, from the first frame, 30 times
> a second, and `Tick()` ends in `CharacterController.Move()`. `FirstPersonController` is already
> calling `Move()` on that same controller. Enable it now and two systems drive one character:
> A3's shadow comparison measures nonsense, and the nonsense looks plausible. Enabling it is an
> M1 step, once the original controller is switched off.

**Report back:** the prefab path, that the clock is disabled, and that the Console is still clean.

---

## A5 — Fixed-timestep decision  ✅ DECIDED: **B**  ·  implemented, nothing left to do

`ProjectSettings/TimeManager.asset` has `Fixed Timestep: 0.02` — **50 Hz**. `SIM_TICK_RATE` is
**30**. Client prediction and the server must step the *same* dt or prediction disagrees with
authority on every airborne tick.

| Option | Effect | Cost |
|---|---|---|
| A. Set `Fixed Timestep` to `0.0333` | Everything lines up, one setting | Physics runs at 30 Hz. Ragdolls will feel slightly different |
| **B. Keep 0.02, run prediction on its own 30 Hz accumulator** ← **chosen** | Physics unchanged | Prediction no longer rides `FixedUpdate`; more moving parts |
| C. Change `SIM_TICK_RATE` to 50 | Everything lines up | **Protocol change** — PR, 2 approvals, `PROTOCOL_VERSION` bump, and 66% more snapshot bandwidth |

**Dev A chose B.** Implemented as
[`NetPredictionClock`](../../../Ironfront_Reborn/Assets/Scripts/Net/Shared/NetPredictionClock.cs):
an accumulator in `Update` that calls `NetMovementAgent.Tick` at exactly
`MovementSimulation.FixedDeltaTime`, whatever the physics rate happens to be.
`ProjectSettings/TimeManager.asset` is untouched.

### I recommended A, and I was wrong — A could not have worked

The recommendation assumed `Fixed Timestep` in the asset is what the game runs at. It is not.
Two files overwrite it at runtime:

| File | Line | Assignment | When |
|---|---|---|---|
| `IngameMenuUi.cs` | 29 | `Time.fixedDeltaTime = Time.timeScale / 60f` | `Hide()`, called from `Awake()` — so before the first frame |
| `FpsActorController.cs` | 497 | `Time.fixedDeltaTime = Time.timeScale / 60f` | every slow-motion toggle |

So the live timestep is **1/60 during normal play** and **0.2/60 in slow motion** — never the
0.02 in the asset, and never the 0.0333 option A would have written there. Option A would have
edited a value that is overwritten before the first `FixedUpdate` of every session, and the
symptom would have been prediction that disagrees with authority for a reason no one could find
in the netcode, because the netcode would have been correct.

B is not merely the safer choice here. It is the only one of the three that a `Time.timeScale`
assignment in someone else's file cannot silently break.

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
| `NetPredictionClock` | New, #13. The 30 Hz accumulator that makes A5 option B real. Attach in A4, leave disabled until M1 |
| `IronfrontLog` | New, #13. Mirrors the Console to a file and prints the assembly census. Self-starting, nothing to attach |
| `AppQuit` | New, #13. One exit point, correct in the Editor and in a build |
| `tools/strip-removed-components.ps1` | New, #14. Deletes components of Unity-removed classes from scenes and prefabs without an Editor re-save of the whole file |

Two gotchas worth knowing before you meet them:

- **`OnMessage` hands you a pooled buffer that is recycled the moment your handler returns.** Keep
  the data and you will read someone else's packet later. Copy it if it must outlive the callback.
  This is a genuinely nasty bug to trace.
- **`SimulatorConfig.ReorderPercent = 100` reorders nothing.** Reordering is an extra delay on the
  chosen packets, so choosing all of them shifts the stream uniformly and preserves order exactly.
  Useful values are well under 50. It is pinned as a test so it cannot regress.

---

## Summary — what I need back

Do them in this order. Group V blocks A3, and A3 blocks A4.

| # | Item | Effort | Reply with |
|---|---|---|---|
| A1 | ✅ DLLs load in the Editor | — | done — PR #12 |
| A2 | ✅ Drop-in scripts installed + `.meta` committed | — | done — PR #12 |
| A5 | ✅ **Fixed timestep — chose B** | — | done — `NetPredictionClock` |
| **V1** | Pull, build libs, open Editor, read the Console | 10 min | `"0 error"`, or each red line with its scene |
| **V2** | Read the assembly census in the log file | 5 min | `"census clean"`, or the ten `assembly ...` lines |
| **V3** | Confirm the log file is written | 3 min | `"log written"`, or what is missing |
| **V4** | Test the Quit button | 3 min | one of the three rows in the V4 table |
| **V5** | Open the other three scenes once each | 5 min | `"clean"` ×3, or the red line with its scene |
| A3 | Shadow-comparison run | 35 min | the summary line + flat-ground warnings |
| A4 | `NetMovementAgent` + `NetPredictionClock` on the prefab, **clock disabled** | 10 min | prefab path + clock disabled |
| A6 | Weapon id registry | 30 min | how to read the ids |
| A7 | Can a player pass ±2048 m? | 10 min | yes/no |
| A8 | Skim the movement analysis | 10 min | anything that contradicts what you know |

**A1, A2 and A5 are closed; roughly 1 hour 55 minutes of Editor work left.** Nothing in this
round needs a decision from you — A5 was the last one. V2 is the only item whose answer changes
a decision already made, A3 is still the one most likely to find a real bug, and A4 is the one
with a trap in it.
