# Report — Phase V0 closed: the six behavioural checks ran, and one of them found a defect

- **Author:** the replication track (Replication & Simulation)
- **Date:** 2026-08-18
- **Phase:** [phases/phase-v0-debt-and-seams.md](../phases/phase-v0-debt-and-seams.md)
- **Supersedes the open items in:** [2026-08-17-phase-v0-closure.md](2026-08-17-phase-v0-closure.md) § 4, § 6
- **Status:** ☑ **Done** — all six § 7 checks pass, the Profiler ran, and the one defect the pass surfaced is fixed and pinned

---

## 1. One-paragraph summary

The MCP bridge is up, so the two items [yesterday's report](2026-08-17-phase-v0-closure.md) had to
leave blocked — the six behavioural checks and the Profiler run — both ran today. All six pass, and
they pass as **measurements with numbers attached** rather than as a play session described
afterwards: the checks live in `Assets/Editor/V0BehaviouralPass.cs` and are re-runnable from a menu
item. Four of the six come out **bit-identical** between 30 fps and 144 fps, which is a stronger
result than the phase asked for. The pass also earned its keep immediately: check 4 led to the
discovery that **`REACTIVATE_COLLISION_TICKS = 25` was wrong** — the game runs physics at 60 Hz, not
the 50 Hz the phase assumed, so V0 had quietly shortened the hitbox window from 0.5 s to 0.417 s.
That is fixed at 30 ticks, pinned by a test, and re-verified. V0 is closed.

---

## 2. Results

Read from `Editor.log`, Unity 6000.3.21f1, PID 8572. Every framerate row rebuilds the vehicle from
its prefab between the two runs, so the only difference between them is `Time.captureFramerate`.

| § 7 check | Measurement | Verdict |
|---|---|---|
| **0 — control** (not in § 7; see § 4) | per-frame integration: `sum30=45.000000°` `sum144=216.000000°` **Δ171°** | ☑ **diverges, as it must** |
| **1** — `Car` at 30 vs uncapped | `p30=(18.8632, 1.0333, 11.8143)` `p144=(18.8632, 1.0333, 11.8143)` over 22.28 m; `steps30=steps144=150`; **posΔ=0.000000 m, angΔ=0.000000°** | ☑ bit-identical |
| **2** — `TankTurret` traverse, 1 s | `yaw30=-89.999390°` `yaw144=-89.999390°` **Δ=0.000000°** | ☑ bit-identical |
| **2** — `MountedTurret` traverse, 1 s | `yaw30=89.999990°` `yaw144=89.999990°` **Δ=0.000000°** | ☑ bit-identical |
| **2b** — mouse latch (see § 3) | 90 °/s injected; `yaw30=-90.000000°` `yaw144=-90.000000°` **Δ=0.000000°** | ☑ bit-identical |
| **3** — `Boat` steering while rolled 40° | one-step body angular velocity `(0.0000, 0.1200, 0.0000)`; on-axis 0.120000, **off-axis 0.000000** | ☑ pure yaw |
| **4** — leave and re-enter inside the window | at 16.667 ms/step: seated **16**, mid-window **16**, re-entered +40 ticks **16**, tick 29 **16**, tick 32 **8** | ☑ (after the § 5 fix) |
| **5** — inverted `Helicopter` | `lost30fps=59.997560 HP` `lost144fps=59.997560 HP` against an expected 60.0 over 2 s | ☑ identical |
| **6a** — enter/repair/leave ×5 | 3.000000 decay ticks (one schedule) | ☑ |
| **6b** — repair an EMPTY vehicle ×5 | 3.000000 decay ticks; a stack of five would give 15 | ☑ |

`dotnet test Ironfront.sln` — see § 7. Zero Unity console errors and zero exceptions across every
play session run today.

**Profiler run** (Dustbowl, live match, 40 bots, 14 vehicles):

```
FrameTimeMs 11.561   Fps 86.50    VSync 1        Direct3D11, MultiThreaded
FixedDeltaTimeMs 16.6667         TimeScale 1
TotalReservedMemoryMB 5500.84    TotalAllocatedMemoryMB 4910.73
MonoHeapSizeMB 199.40            MonoUsedSizeMB 125.41
GraphicsMemoryMB 339.11          TempAllocatorSizeMB 17.70
```

---

## 3. Two checks the phase did not ask for, and why they are here

**2b — the mouse latch.** § 7's check 2 is satisfied by the bot input path, which supplies a
constant demand per fixed step. That path cannot exercise deviation 2 — the `Update`-side latch that
exists because `Input.GetAxis` is a per-*rendered*-frame delta. A regression to sampling it from
`FixedUpdate` would drop ~65% of the player's motion at 144 fps and double-count it at 30, and
check 2 as written would stay green through all of it. 2b injects a constant 90 °/s of hand movement
per second of wall clock and asserts the traverse matches; it comes out at exactly −90.000000° at
both framerates.

**6b — repairing an empty vehicle.** § 7's sequence (6a) ends on a *leave*, and `OccupantLeft` calls
`CancelInvoke("AutoDamage")` — which cancels *every* pending invoke of that name — before arming one.
So 6a lands on a single schedule whether or not Task 6's fix is present: **it is a green that could
not have gone red**, and on its own it proves nothing. The shipped `Repair` armed unconditionally and
without cancelling, so the case that actually discriminates is repairing an *already-empty* vehicle,
where nothing afterwards collapses the stack. Both are reported; 6b is the one with teeth.

---

## 4. The control, and why every green above is worth reading

A suite of nine passes is worth nothing until something in it has been seen to fail. Check 0 is a
negative control: it integrates a fixed step **per rendered frame** — precisely what the shipped
turret and helicopter code did — and applies the same 30-vs-144 equality the real checks apply. It
reports 45.000000° against 216.000000°, a 171° gap, and is rejected. So the comparison can tell
framerate-dependent code from framerate-independent code, and the zeros in § 2 mean what they say.

Two of the checks were also observed red *during* this session, for reasons worth recording because
both were the harness being wrong rather than the code:

1. **Check 1 failed at 4.93 m / 18.16°** on the first run. `Time.captureFramerate` takes effect from
   the *next* frame, so the run that spawned immediately took its first step at whatever rate the
   previous run had left behind. Printing the step counts is what settled it — the failing run had
   mismatched counts; with one settling frame both read exactly 150 and the positions became
   identical to four decimals. **Every framerate comparison in the file now yields once before
   spawning**, so this passes by construction rather than by luck.
2. **Check 3 failed with off-axis ≈ on-axis.** The rhib was spawned at `y = 0`, which is *inside* the
   4000 × 4000 ground slab whose top surface is at `y = 0` — so what the check measured was contact
   torque. Moved clear, and sphere-ised the inertia tensor so a free body spun about one axis does
   not precess onto the others (Euler's equations, not a steering bug), and it reads exactly
   `(0, 0.12, 0)`.

Neither was a V0 defect. The one that was is § 5.

---

## 5. The defect the pass found — `REACTIVATE_COLLISION_TICKS`

Task 8 replaced `yield return new WaitForSeconds(0.5f)` with a tick count, and stated:

> `ReactivateCollisionTicks` = 25 at the current 50 Hz fixed step, preserving today's 0.5 s.

**The premise is false.** `TimeManager.asset` does carry `Fixed Timestep: 0.02`, but both
`FpsActorController.cs:539` and `IngameMenuUi.cs:37` assign `Time.fixedDeltaTime = Time.timeScale / 60f`
at runtime, and `NetServerBootstrap` deliberately does not fight them (its decision A5 says so in as
many words, and names both call sites). The Profiler and the harness independently measured
**16.66667 ms** in a live Dustbowl session. So the fixed step is 60 Hz in every real session, and
25 ticks was **0.417 s** — a 17% shortening of a window Task 8 believed it was preserving, applied to
the hitbox layer state of every actor leaving a vehicle.

Fixed at **30** (30 / 60 Hz = 0.500 s exactly), and check 4 now straddles the boundary — held at
tick 29, returned by tick 32 — rather than probing one side of it.

**Why it got through.** `ActorSeatCollisionTimerIsTickCounted` asserted that the source *contains*
`REACTIVATE_COLLISION_TICKS`. The shape was right and the number was wrong, and an assertion on the
name cannot see that. It now pins the value:

```csharp
Assert.Contains("private const int REACTIVATE_COLLISION_TICKS = 30;", source);
```

**Left open, deliberately.** A peer that never constructs `FpsActorController` or `IngameMenuUi` —
a dedicated server build — keeps the project's 50 Hz and would get 0.6 s from the same constant. Two
peers disagreeing about a physics rate is a determinism concern in its own right and is larger than
V0; **V4 should settle it** when it retunes this constant against the netcode's 30 Hz accumulator,
which is the retuning Task 8 anticipated.

---

## 6. Where the checks live, and why not in a test assembly

`Ironfront_Reborn/Assets/Editor/V0BehaviouralPass.cs`, run from **Ironfront ▸ V0 behavioural pass**
(two menu items: the isolated checks, and the seat timer). Results go to the console as single
`[V0PASS]` lines so an external driver can read them without a UI. Same precedent as the existing
`NetVerificationHarness.cs`.

They are not PlayMode tests because of [#83](https://github.com/Sagitoaz/LTM/issues/83): the project
has no assembly definitions, so every game type lives in the predefined `Assembly-CSharp`, and an
`.asmdef` test assembly **structurally cannot reference a predefined assembly**.
`Assembly-CSharp-Editor` is the only assembly that both sees `Vehicle` and is excluded from player
builds. When #83 lands these become real PlayMode tests; the measurements do not change, only where
they live.

Two design notes that matter if anyone edits them:

- **`Time.captureFramerate`, not a hand-rolled loop.** It makes Unity advance the clock by exactly
  `1/fps` per rendered frame, so the *real* engine loop runs the *real* number of `FixedUpdate`
  calls — which is the thing under test. Invoking `FixedUpdate` by reflection would only prove the
  code is framerate-independent when somebody else supplies the timestep, which is the claim rather
  than evidence for it.
- **The occupant is built on an inactive `GameObject`**, so `Actor.Awake` — which wants a ragdoll, an
  animator and an IK rig none of these checks use — never runs. Nothing in the vehicle code under
  test reads more of its occupant than `controller`, `aiControlled` and `team`. Check 4 is the
  exception and runs against a live Dustbowl match, because a hitbox-layer check needs a real actor.

---

## 7. Test results

```
$ dotnet test Ironfront.Net.Replication.Tests
Passed!  - Failed: 0, Passed: 571, Skipped: 0, Total: 571, Duration: 13 s
```

Full solution: 1,069 passed, 0 failed across 7 suites (input 22, protocol 198, configuration 33, flow 79, transport 85, master-server 81, replication 571). Zero Unity console errors and zero exceptions across every play
session today, including the two that ran a full 40-bot Dustbowl match.

---

## 8. Not done here, and not blocking V0

- **`InputShadowCompare`** reported `CLEAN over 52651 frames` in a prior session and its own message
  asks for its deletion plus the `Install` call in `FpsActorController.Awake` once someone is
  satisfied. That is input-track scaffolding, not V0's, and removing it changes shipping code — left
  for whoever owns that call.
- **The `aimLimits` prefab pass** stays optional, unchanged from
  [yesterday's § 3.4](2026-08-17-phase-v0-closure.md): both turrets already traverse at the intended
  rates from their code defaults, and check 2 above is the measurement that says so.
- **B7** (a player id on `ConnectionInfo`, the transport track) and confirming the server appears in
  the master's list (the master-server track), both unchanged from phase-05.

---

## 9. Next

V0 is closed. Its § 7 "what this unblocks" now stands without qualification:

- **V3** can specify the vehicle entry's turret yaw/pitch against a real authoritative source —
  measured above at exactly 90 °/s on both turrets at both framerates.
- **V4** has `SetHealthAuthoritative` and an attacker-carrying `Damage`, and should settle the
  50-vs-60 Hz question in § 5 when it retunes `REACTIVATE_COLLISION_TICKS`.
- **V5**'s prediction blend has a fixed-timestep simulation on both peers to converge between, and
  § 2 says the two peers agree to the bit.
- **V1** inherits a range-correct `ActorManager.Explode` and a `Damage` overload with an attacker
  slot already open.
