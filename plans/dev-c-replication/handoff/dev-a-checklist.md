# Dev C → Dev A — the precise checklist

**From:** Dev C (replication) · **Date:** 2026-08-12 · **Milestone:** M0 closing, M1 starting
**Replaces:** the 4-item request in
[phase-00 § Task 6](../phases/phase-00-foundation.md#task-6--send-your-requests-to-dev-a-half-a-day-do-it-in-week-1)

Everything that could be built without the Unity Editor is built, tested and merged. What is left
needs the Editor, which under conventions.md § 1.3 means it needs you.

Items are ordered by what unblocks the most.

> **Round 5 — 2026-08-13. Group V is closed.** V1: **0 errors** (measured with MCP installed).
> V2: **census clean**. V3: **log written**. V4: Play Mode stopped and
> `[AppQuit] quit requested` was logged; both Quit buttons are wired. V5: **clean x3** for Island,
> Dustbowl, and Splash. Full evidence: [Unity V1-V5 verification](../../reports/2026-08-13-unity-v1-v5.md).
> A3 is no longer blocked by Group V.

> **Round 6 — 2026-08-13. The A3 harness is repaired; please re-run A3.** Your round-5 report was
> right on all three counts and the harness has been fixed for each: the grounded vertical channel
> is now excluded (that was the 787 idle false positives), spawn/respawn/teleport samples are
> detected and skipped instead of scored (the 1123 m sample), and the component now declares
> `[DefaultExecutionOrder(1000)]` so it always samples the transform after the original controller
> has moved for that tick. A fourth bug you did not see is also fixed: `_primed` was never reset on
> re-enable, so a pooled respawn measured against a stale position — the most likely origin of that
> 1123 m entry. The summary line has changed shape: **the verdict is now the GROUNDED number**, and
> it reports skipped discontinuities separately. Details: [harness repair](../../reports/2026-08-13-movement-shadow-harness-repair.md).

> **Round 7 — 2026-08-14. PR #42 isolated the last flat-ground warnings to two sprint-edge
> physics ticks.** The shared simulation was not delaying sprint: the harness sampled the raw
> Sprint button in `FixedUpdate`, while the legacy controller consumed the `sprinting` value
> latched by `FpsActorController.Update`. Two physics ticks before the next render update therefore
> compared different inputs. The harness now reads the exact legacy sprint latch and waits until
> both legacy input and the `CharacterController` are active, removing the pre-deploy airborne
> noise too. After this PR merges, repeat A3 on flat ground with several walk↔sprint transitions
> and send the grounded summary plus any flat-ground warnings. **A3 and A4 remain open until that
> focused rerun is clean.** Evidence: [Dev A's rerun](https://github.com/Sagitoaz/LTM/pull/42).

> **Round 2 — 2026-08-12, afternoon.** A1, A2 and A5 are closed. Three more PRs merged since
> (#12 yours, #13, #14): the two bugs you reported — cannot quit, no logs — plus four Unity 6
> errors in the scene files. Everything verifiable without the Editor has been verified;
> everything else is now **group V below, and it blocks A3.** Start there, not at A3.
>
> Step-by-step version of the same thing, with the exact clicks:
> [`dev-a-gate-board.html`](dev-a-gate-board.html).

> **Round 4 — 2026-08-12, late.** [#17](https://github.com/Sagitoaz/LTM/pull/17) is merged. You
> fixed all three points and I verified each one on the new head: 42 `.meta` files at
> `Any: 0 / Editor: 1`, the define down to `Server` + `Standalone` only, `.mcp.json` and
> `Ironfront_Reborn/.claude/` untracked, commit scope `client` so `style` is green again.
> **Group P is closed, and I withdrew P0** — your Editor has had MCP installed since you opened
> the PR, so merging changed nothing about the measurement. Just write "measured with MCP
> installed" next to the V1 number when you send it.
>
> **Group V is now the only thing blocking A3.** Start there.
>
> One item left open, not urgent: the DLLs are Editor-only but the define still covers
> `Standalone`. If the first player build ever fails on `McpPlugin`, drop the define from
> `Standalone` — that class of error never shows up in the Editor.
>
> **[#21](https://github.com/Sagitoaz/LTM/pull/21) is merged too, and it fixed something I had
> missed.** The meta half is belt-and-braces (`defineConstraints: UNITY_EDITOR` on all 42, which
> holds at compile time rather than at an Inspector toggle). The part that mattered is the three
> plugin DLLs: `Ironfront.Net.Replication.dll` on `develop` had been **missing**
> `ServerMessageRouter` and `ServerPayloadWriter` ever since #19 merged — Unity was loading a build
> older than the source, silently. You rebuilt after #19, so #21 is the first version that matches.
> `chore/install-unity-mcp` had an identical tree, so it needed no second PR and I deleted it.
>
> **This becomes a standing rule.** Those three DLLs are build artifacts of B/C/D source that live
> in git. From now on, whoever merges source into `Ironfront.Net.*` runs `build-libs.ps1` and
> commits the DLLs in the same PR. Unity has no way to tell you it is running last week's code.
> A drift-warning CI job is on Dev D's roadmap.

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

> **How to read the summary after the round-6 repair.** The verdict is the **grounded** count, not
> the total: `CLEAN on the ground` or `N of M GROUNDED ticks diverged`. Airborne divergence is
> reported next to it but does not by itself condemn the port — slopes and geometry are the two
> documented gaps. `skipped_discontinuities=N` counts spawn/respawn/teleport samples that were
> deliberately not scored; a handful is normal, hundreds means something is teleporting the actor
> every few seconds and is worth telling me about. Per-tick warnings now print `dH=` (horizontal,
> always scored) and `dV=` which reads `absorbed` while grounded — that is the collision channel
> being correctly ignored, not a measurement being hidden.

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

## S — Stand the server layer up  ⏱ ~40 min  🔴 closes M1 criteria 1 and 9

New round. `Assets/Scripts/Net/Server/` now exists: `NetServerBootstrap`, `ServerTickLoop`,
`ServerInputStage`, `ServerSnapshotStage`, `NetServerActor`, `ServerActorRegistry`. That was the
last piece of phase-01 that was mine to write, and it is the piece that turns "the encoder is
correct in a unit test" into "the server holds 30 Hz with real physics and real AI in the loop".

I cannot run any of it. Every one of these is Editor-only.

**Script execution order needs nothing from you.** The ordering the phase document asks you to
enter into `ProjectSettings` is declared in `[DefaultExecutionOrder]` on the components instead
— -1000 / -200 / +200. Your project settings file is untouched.

| # | Do | Report back |
|---|---|---|
| **S1** | `git pull`, `pwsh tools/build-libs.ps1`, open the Editor. It compiles the seven new scripts and generates their `.meta`. **Commit the `.meta` files** — I do not create them. | `"0 error"`, or each red line verbatim |
| **S2** | Empty GameObject in `Dustbowl.unity`, name it `NetServer`. Add **`NetServerBootstrap`** — `[RequireComponent]` pulls in `ServerTickLoop` for you. Then add **`ServerInputStage`** and **`ServerSnapshotStage`** by hand. Leave every field at its default. | the GameObject path |
| **S3** | On the player prefab (the one that got `NetMovementAgent` in A4) add **`NetServerActor`** and tick **Available For Players**. Leave `Actor Id` at 0 — the registry assigns it. Do the same on a couple of bot prefabs but leave their tick **off**. | how many actors carry it |
| **S4** | Press Play. Window → Analysis → Profiler, record ~30 s, and read two numbers: **GC Alloc per frame** on the `ServerSnapshotStage.FixedUpdate` / `ServerInputStage.FixedUpdate` rows, and the p99 warning if one appears. | the two numbers, plus any `[net] server over budget` line |

> **S4 is the whole point of the round.** M1 criterion 1 (p99 < 33 ms with 48 actors) and
> criterion 9 (0 allocations per tick) are the only two acceptance criteria in phase-01 that no
> test can answer, because both are about what Unity does, not what the encoder does. Everything
> on my side is allocation-free by construction — fixed rings, pre-allocated buffers, cached
> delegates, no LINQ, no per-tick `new`. **Designed for it is not measured for it**, and the risk
> was never my code; it is whatever the wrapper does per tick, which until this round nobody had
> written.

**What S does NOT close, and I want to be straight about it.** Criterion 7 — *two* Unity clients
seeing each other in sync — is not reachable this round, and not because of anything you have
left to do. The only transport that exists is `LoopbackTransport`, which is in-process: it can
run a server and **one** client inside a single Editor, and cannot reach a second process. Two
clients needs Dev B's UDP transport. So after S the M1 score is 8 of 10 with one criterion on
Dev B, not on you.

**Expect the Console to say `no free player slot` if you skip S3.** That is the server telling
you a connection arrived and there was no `NetServerActor` marked available — it disconnects with
`ServerFull` rather than silently accepting a player nobody can see.

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

## S5 / A9 — Phase 02 landed  ⏱ ~25 min  🔴 closes M2 criteria 7 and 8

Interest management, hitbox history, lag compensation, shot resolution, bot AI LOD and the
gameplay-event framing are all written and green — 156 new tests, 453 in the solution. Every M2
criterion except one is now pinned by a test rather than by an opinion. The exception is the same
shape as S4: it is about what Unity does per tick, not what the encoder does.

| # | Do | Report back |
|---|---|---|
| **S5** | Same Play session as S4, but put **32 bots** in the scene. Record ~60 s in the Profiler and read **tick p99** off the `ServerSnapshotStage.FixedUpdate` row (or the `[net] server over budget` warning, which prints it for you). Then toggle the LOD off — set `BotLodScheduler`'s threshold so every bot ticks — and record again. | the two p99 numbers, and the AI cost before/after |
| **A9** | **A decision, not Editor work.** `BotLodScheduler` decides *which* bots should think this tick; something has to act on that. The obvious mechanism is `AiActorController.enabled = false`, and phase-02 trap 7 warns that a controller using coroutines or `Time.deltaTime` timers can misbehave when toggled at 6 Hz. The clean fix is an `updateInterval` field inside `AiActorController` — **your file**. Tell me which you want. | `"use .enabled"` or `"I'll add updateInterval"` |

> **S5 is M2 criterion 7** — 32 bots with tick p99 under 33 ms — and it is the only M2 criterion
> that can still fail. The thing that makes it affordable is built and measured: distant bots
> think at 6 Hz instead of 30, which skips **50%** of AI updates with 20 of 32 bots distant. But
> that is a skipped-update *share*, not milliseconds, and the criterion asks for the Profiler. If
> p99 comes in over budget the contingency is a scope cut — 32 bots down to 16 — so it is worth
> knowing early.

**A9 needs no work from you if you do not want to do any.** The scheduler does not care which
mechanism is used; policy and mechanism are deliberately separate so your answer does not touch
my code or its tests. `.enabled` is what the wrapper will use unless you say otherwise — I just
do not want to find out at M3 that it quietly broke your AI's timers.

**One thing you should know rather than discover.** Dev B's UDP transport has landed, so
`ITransportServer.OnValidateTicket` is now **fail-closed**: with no ticket validator registered,
every UDP connection is rejected. That is mine to wire, not yours, and it is done — but if you
ever stand a server up by hand and it accepts nobody with no error in the Console, that is why.

---

## S6 / A11–A13 — Phase 03 landed: the match runs itself  ⏱ ~25 min  🔴 closes M3 criteria 1–7

Phase 03 is merged. The server now runs a complete match on its own: warmup, play, capture points,
ticket bleed, a winner, a scoreboard pause, a clean reset, and back to waiting. All of it is
engine-free and tested — 403 tests in the replication suite, 718 across the solution.

Two new scripts landed under `Assets/Scripts/Net/Server/`, and as usual I did not create their
`.meta` files:

| | |
|---|---|
| `MatchController.cs` | Drives the match, reads capture-point occupancy out of the scene, broadcasts `S_MATCH_STATE` and `S_CAPTURE_POINT` |
| `ServerMasterReporter.cs` | Heartbeats and match results to the master. Defaults to standalone, so it does nothing until wired |

### S6 — Compile them and commit their `.meta`  ⏱ 10 min

Same as S1. Pull, run `build-libs.ps1` (the DLLs changed), open the Editor, confirm 0 errors,
commit the two new `.meta` files.

**Reply with:** `"0 error"` — or the red lines.

### S7 — Put both components on the `NetServer` GameObject  ⏱ 10 min

Both declare `[RequireComponent(typeof(ServerTickLoop))]`, so drop them on the same object S2
created. Then fill in `MatchController`'s **Capture Points** array with the map's capture-point
transforms, **in id order** — the array index *is* the point id on the wire, so a gap renumbers
every point after it and desynchronises the flags on every client. Leave the array empty for a
deathmatch: no points, no ticket bleed, and the round then only ends on deaths.

**Reply with:** how many capture points you wired, and for which map.

### A11 — Two more plugin DLLs, if you want the master server connected  ⏱ 10 min  🟡 optional

`ServerMasterReporter` talks to an interface (`IMatchReporter`) that lives in
`Ironfront.Net.Replication.dll`, which you already have. The concrete implementation that speaks
TCP to Dev D's master lives in two assemblies you do not: `Ironfront.MasterClient.dll` and
`Ironfront.Net.MasterLink.dll`.

That split is deliberate. `Replication.dll` ships into the Editor, so it must not drag a socket and
`System.Text.Json` in behind it for four calls made once every five seconds. The cost is that
connecting to the master needs those two DLLs dropped into `Assets/Plugins` with their `.meta`
files — yours, not mine.

**Until you do, the server runs in standalone mode**: complete matches, correct scoring, simply not
advertised anywhere, with clients connecting by IP. That is a supported configuration, not a
degraded one, so this is genuinely optional until Dev D's master is up.

When you do want it, the wiring is one line from a boot script:

```csharp
var link = new GameServerLink();
var reporter = new GameServerMatchReporter(link, ownsLink: true);
await reporter.ConnectAndRegisterAsync(masterHost, masterPort, registration);
GetComponent<ServerMasterReporter>().SetReporter(reporter);
```

**Reply with:** whether you want this now or after Dev D confirms the master is reachable.

### A12 — Server CPU percentage: I am sending −1  ⏱ 2 min  🟡 decision

GS_HEARTBEAT carries a `cpuPercent` that the master sorts servers on. Unity exposes no portable
process-CPU counter, so I send **−1** rather than a number I made up — a fabricated value on a
matchmaking input is worse than an absent one, because the master will act on it. Average tick time
is a real load signal and is sent alongside.

**Reply with:** leave it at −1, or name a counter you would rather I read.

### A13 — Nobody is tallying kills and deaths  ⏱ 5 min  🟡 decision

GS_MATCH_ENDED carries per-player kills/deaths/score. `S_DEATH` already names the killer, but
nothing accumulates it, so I report an **empty** list rather than a row of zeroes per player —
all-zero rows are indistinguishable from a match where nobody scored, and the master stores what it
is given.

Ticket accounting is unaffected: `MatchController.ReportDeath(team)` costs the dying team a ticket
and that is wired.

**Reply with:** whether the scoreboard is yours or mine. If it is mine, tell me where a kill is
resolved on the server and I will tally from there.

---

## B7 — For Dev B, not you: the connection carries no player identity

Recorded here so it is not lost. `ITransportServer.OnValidateTicket` hands me the ticket, and
`OnClientConnected` hands me a `ConnectionInfo` — which has no player id on it. So there is nothing
to match a validated ticket against its connection, and I pair them positionally: admission and
connection happen in the same `Poll`, in order, one immediately after the other.

It is sound in the normal case and it has a cost in the abnormal one. A handshake that validates
and then fails before connecting leaves its admission at the head of the queue, and the next
connection is paired with the wrong player — that player's claim is then not released on
disconnect and lapses on the ticket's own 60-second expiry instead. Nobody is admitted who should
not be; one player waits up to a minute to rejoin after a crash.

The clean fix is a `PlayerId` (or the raw ticket) on `ConnectionInfo`. Filed for Dev B.

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
| `ServerMessageRouter`, `ServerPayloadWriter` | New. Inbound decode and outbound framing, engine-free and unit-tested, so the MonoBehaviour above them holds no decision CI cannot reach |
| `Assets/Scripts/Net/Server/**` | New. The Unity server: bootstrap, tick loop, the two ordering stages, the replicated-actor component and its registry. Needs group **S** |
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
| **S1** | Compile the new server scripts, **commit their `.meta`** | 10 min | `"0 error"` |
| **S2** | `NetServer` GameObject with the bootstrap + both stages | 10 min | the GameObject path |
| **S3** | `NetServerActor` on the player prefab, **Available For Players** ticked | 10 min | how many actors carry it |
| **S4** | Profiler: GC alloc per tick + p99 | 10 min | the two numbers |
| **S5** | Profiler: 32 bots, tick p99 + AI cost with LOD on/off | 25 min | the two p99 numbers |
| **A9** | Bot LOD mechanism: `.enabled` or `updateInterval`? | 2 min | which one |
| A6 | Weapon id registry | 30 min | how to read the ids |
| A7 | Can a player pass ±2048 m? | 10 min | yes/no |
| A8 | Skim the movement analysis | 10 min | anything that contradicts what you know |
| **S6** | Compile the two new match scripts, **commit their `.meta`** | 10 min | `"0 error"` |
| **S7** | `MatchController` + `ServerMasterReporter` on `NetServer`, capture points in id order | 10 min | how many points, which map |
| A11 | Master-link DLLs in `Assets/Plugins` — optional, standalone works without them | 10 min | now, or after Dev D is up |
| A12 | `cpuPercent`: leave at −1, or name a counter | 2 min | which |
| A13 | Who owns the kill/death tally | 5 min | yours or mine |

**A1, A2 and A5 are closed; roughly 3 hours of Editor work left.** Nothing in this
round needs a decision from you — A5 was the last one. V2 is the only item whose answer changes
a decision already made, A3 is still the one most likely to find a real bug, A4 is the one with a
trap in it, **S4 answers the two M1 criteria that no test can reach**, and **S5 answers the last M2
criterion that no test can reach**. A9 is the only item in this round that is a decision.
