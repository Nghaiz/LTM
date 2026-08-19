# the replication track → the client track — the open work list

> ## ⚠ Round 8 is superseded — read [`plans/debt-closure/debt-ledger.md`](../debt-closure/debt-ledger.md) instead
>
> **Superseded 2026-08-19** by the debt ledger, which re-verified every row below against
> `develop` @ `38ac29a` and carries `file:line` evidence for each verdict.
>
> **This file is kept, not retired.** Its round-by-round history is how "what closed since round 7"
> stays readable, and several rows below are still the best description of *why* an item exists.
> What it is no longer is a statement of what is open.
>
> **Do not action a row from this file without checking its ledger row first.** Round 8 was written
> 2026-08-16 and predates four merges. Two things it says are now actively misleading:
>
> - **D1 no longer "BLOCKS THE ENTIRE DEPLOYMENT" as written.** The build target is fixed
>   (`e7f61e3`) and the artifact is published (release `gameserver-v0.1.0`). What blocks deployment
>   is one step later — the game-server image has never been pushed, because `images.yml` has never
>   been dispatched. Ledger rows **E-1**, **E-2**, **E-3**.
> - **A3 is closed and was already closed when this file was written.** It closed 2026-08-14, two
>   days before round 8, which still lists it as 🔴 blocking A4. A4 is closed too. Ledger rows
>   **E-5**, **A-13**.
>
> **Seven rows were answered the day after this file was written**, by
> [`plans/unity-client/reports/2026-08-17-round8.md`](../unity-client/reports/2026-08-17-round8.md)
> (summary table at `:573-584`) — A3, A4, A7, A11, A12, A13 and D2. That report, not this checklist,
> is the source for those. **A7's answer was YES**, which turns it from a confirmation into
> implementation work (ledger row **E-6**).

**From:** the replication track (replication) · **Date:** 2026-08-16 · **Round:** 8
**Human-readable Vietnamese version:** [`integration-gate-board.html`](integration-gate-board.html)
**Superseded rounds 1–7:** in git history of this file. Everything closed there stays closed;
this file lists only what is still open, plus what changed under it since round 7.

Items are ordered by what unblocks the most. Effort is play/Editor time, not reading time.

---

## What closed since round 7

Do not redo any of these.

| Item | Closed by | Evidence |
|---|---|---|
| V1–V5 | round 5 | [`2026-08-13-unity-v1-v5.md`](../reports/2026-08-13-unity-v1-v5.md) |
| S1, S2, S3 | your S4 session | Dustbowl ran as a loopback server with 1 player + 47 bots |
| S4 | 2026-08-15 | 0 B GC alloc, 0.12 ms snapshot stage, 0.01 ms input stage |
| S6, S7 | 2026-08-15 | [`2026-08-15-s4-s7-and-s5-blocker.md`](reports/2026-08-15-s4-s7-and-s5-blocker.md) |
| A1, A2, A5 | round 2 | PR #12, `NetPredictionClock` |
| A6 — weapon id registry | landed | `WeaponIds.cs`, `WeaponManager.NetworkIdOf`, `SpecChecker` validates it against the prefab on every CI run |
| A8 | cancelled | round 3 |

**A6 is worth a note.** The registry landed, but nothing read it until 2026-08-16 —
`NetServerActor._weaponId` was a serialized field the snapshot read and nobody wrote, so every
actor in every snapshot and every `S_WEAPON_FIRE` reported weapon 0. Fixed in #81 as a
pass-through to `Actor.activeWeapon.NetworkId`. Listed here because it changes what you will see
in the Editor: remote actors should now hold visible weapons.

---

## D1 — Build the Linux dedicated server artifact ⏱ ~45 min 🔴 BLOCKS THE ENTIRE DEPLOYMENT

**GitHub issue:** #80. This is the single highest-priority item in the project right now.

The Azure VM is provisioned, the master image is built and pushed, and the deployment runbook is
written ([issue #78](https://github.com/Sagitoaz/LTM/issues/78)). The game server cannot start,
because there is no game-server image and CI cannot build one: GitHub-hosted runners have no Unity
licence. `.github/workflows/images.yml` only *packages* an artifact produced on a licensed machine.

Without this, the deployed server runs auth, lobby and matchmaking, and no match can be entered.

### D1.1 — Fix the build target first

`tools/build-server.ps1` never passes `-buildTarget`, and `Assets/Editor/EditorBuild.cs` never
calls `SwitchActiveBuildTarget`. On a Windows build host — the documented one — this means:

- `EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server`
  (`EditorBuild.cs:63`) is applied to the **currently active** standalone platform, which is
  Windows, not Linux. The comment above it claims it stops a later interactive build from
  reverting; it cannot do that for a platform that is not active.
- `BuildPipeline.BuildPlayer` with a non-active target triggers an implicit platform switch and a
  full reimport inside a `-quit` batch run. This is where "the build succeeded but produced the
  wrong subtarget" bugs live.

Fix either way:

```csharp
EditorUserBuildSettings.SwitchActiveBuildTarget(
    BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
```

before the subtarget assignment, **or** pass `-buildTarget Linux64` from `build-server.ps1`.

Minor, same file: `EditorBuild.cs:63` sets `standaloneBuildSubtarget = Server` and never restores
it, so the interactive menu item leaves your Editor in server-subtarget mode.

### D1.2 — Build and publish

```powershell
pwsh tools/build-server.ps1
```

Produces `gameserver-linux.tar.gz` containing `Ironfront.Server.x86_64`. Then:

1. Create a GitHub Release (any tag, e.g. `gs-2026-08-16`) and attach the tarball as an asset
   named exactly `gameserver-linux.tar.gz` — `images.yml` matches on that filename.
2. Run the `images` workflow manually with `gameserver_release_tag` set to that tag.
3. The workflow pushes `ghcr.io/sagitoaz/ironfront-gameserver` and fails loudly if the tarball has
   no `Ironfront.Server.x86_64` inside.

**Reply with:** the release tag and the pushed image digest. The master-server track needs the digest for
`IRONFRONT_GAMESERVER_IMAGE` in the VM's `.env`.

---

## D2 — Sign off on `Assets/Editor/EditorBuild.cs` ⏱ ~10 min 🟡 ownership

The master-server track added this file in #77. It lives in your `Assets/` tree but exists to serve the image
pipeline (`gameserver.Dockerfile` and `images.yml` both depend on the artifact it produces).

It needs your sign-off because it writes to `EditorUserBuildSettings`, which is Editor-global
state, not project state. Its `Application.isBatchMode` guard around `EditorApplication.Exit` is
correct discipline; the build-target gap in D1.1 is the part to fix.

**Reply with:** keep it where it is, or move it somewhere you prefer.

---

## E — Verify the eight Unity-side fixes from #81 and #82 ⏱ ~30 min 🔴 nothing has observed these

PRs #81 and #82 fixed eleven defects in the server authority layer. **Eight of them are Unity-side
and were argued structurally, not observed** — no Editor session was run. They cannot be tested
under CI for the reason in item F below.

Each row is a specific thing to look at, with what "correct" looks like.

| # | What changed | What to check | Correct result |
|---|---|---|---|
| E1 | `NetServerActor.WeaponId` now reads `Actor.activeWeapon.NetworkId` | Join with a second client, look at the other player | They hold a visible weapon, and switching weapons changes what you see |
| E2 | Yaw now written from the input frame, not the transform | Have the other player spin on the spot | Their body turns. Before this, they faced their spawn heading forever |
| E3 | `LagCompensator.Occlusion` now assigned to `Physics.Linecast` with mask `-2049` | Aim through a solid wall at a target and fire | No hit, no kill, and `ShotsResolved > 0 && ShotsHit + ShotsOccluded == ShotsResolved`. **Amended round 9** — the original wording ("check `ShotsOccluded` is no longer 0") fails closed: with an empty candidate span both `ShotsHit` and `ShotsOccluded` are structurally 0, so it cannot tell "occlusion works" from "nothing was ever tested". It read as a failure while masking defect 4 instead |
| E4 | Input drain is metered by a per-tick budget | Play normally, then play over a lossy link | Movement feels unchanged. `SpeedViolations` and `InputThrottleEvents` stay at 0 for an honest client |
| E5 | `S_DESPAWN_ACTOR` sent on disconnect; slot health/alive reset on claim | Have a player leave, then rejoin | No frozen body left standing. The rejoining player is alive at full health, not a corpse |
| E6 | Respawn teleports to an `ActorManager` spawn point | Die at a chokepoint, respawn | You appear at a spawn point, not where you died, and no speed violation is logged for the teleport |
| E7 | Hitbox history written under each owed tick | Play with the server hitching or under 32-bot load | Lag compensation stays consistent. `PresentFallbacks` should not climb with load |
| E8 | `ActorIdPool` is now the id source; `ForgetActor` fires for bots too | Play a full round with bots spawning and despawning, then reset the match | Actors keep appearing normally. **This is the change most likely to surprise — if actors stop spawning, look here first** |

Also expect: the `[net] match reset left state behind` error no longer fires on every round
transition (it was checking `Sessions == 0`, and a reset deliberately keeps its sessions).

**Reply with:** one line per row — matches / does not match, and what you saw if it does not.

---

## E9 — `LobbyShellOverlay` gained three serialized fields ⏱ ~5 min 🟡 scene hygiene

#77 added `_masterTls`, and two TLS string fields, to `LobbyShellOverlay`. `_masterTls` is a `bool`
with no initializer, so it defaults to `false` and the LAN/plaintext path is byte-for-byte
unchanged.

The thing to know: **any prefab or scene referencing this component will be marked dirty the next
time you save in the Editor.** That is expected, not a bug. It is also the most likely origin of
the accidental `New Terrain.asset` re-serialization that rode into #77 — see #79 for the
`.gitattributes` rule that stopped that from corrupting the file.

---

## A3 — Re-run the shadow comparison ⏱ ~35 min 🔴 blocks A4

Still open from round 7. PR #42 isolated the last flat-ground warnings to two sprint-edge physics
ticks: the harness sampled the raw Sprint button in `FixedUpdate` while the legacy controller
consumed the `sprinting` value latched by `FpsActorController.Update`, so two physics ticks before
each render update compared different inputs. The harness now reads the exact legacy sprint latch
and waits until both legacy input and the `CharacterController` are active.

**Re-run focused:** flat ground, several walk↔sprint transitions.

**Reply with:** the grounded summary line, plus any remaining flat-ground warnings. A3 and A4 stay
open until that rerun is clean.

---

## A4 — `NetMovementAgent` + `NetPredictionClock` on the player prefab ⏱ ~10 min 🟡 blocked by A3

Both components onto the player prefab, **`NetPredictionClock` disabled**. The clock being enabled
before A3 is clean is the trap in this item: it takes over the timestep, and a shadow comparison
against a timestep the clock is already driving measures nothing.

**Reply with:** the prefab path, and confirmation the clock is disabled.

---

## S5 / A9 — Bot LOD profiling ⏱ ~25 min 🟢 UNBLOCKED SINCE YOUR LAST REPORT

Your 2026-08-15 report was correct: `BotLodScheduler` was an engine-free policy class with no Unity
wrapper, so an LOD-on/LOD-off comparison would have measured the same code path twice.

**That is fixed.** [`Assets/Scripts/Net/Server/BotLodGate.cs`](../../Ironfront_Reborn/Assets/Scripts/Net/Server/BotLodGate.cs)
is the seam, with a serialized `BotLodMode`:

| Mode | Behaviour |
|---|---|
| `Scheduler` | asks `BotLodScheduler` — the shipping behaviour |
| `AlwaysOn` | every bot ticks every frame — the LOD-off baseline |
| `AlwaysOff` | deterministic override for the comparison |

Your objection to toggling `AiActorController.enabled` was also right — the controller starts eight
coroutines and does `Time.deltaTime`-driven work in `Update`, so toggling the MonoBehaviour alone
splits paused `Update` state from independently paced coroutine state. `BotLodGate` is the seam
that gates the intended work without that split.

**Reply with:** ~~32-bot tick p99 with `AlwaysOn`, and with `Scheduler`~~ — **amended round 9.**
Server tick p99 cannot separate the arms and never could: `AiActorController` runs in `Update`, and
no stage of `ServerTickLoop` contains it, so no LOD setting can move that number. The client track measured
4.830 / 4.694 / 4.602 ms across `Scheduler` / `AlwaysOn` / `AlwaysOff` — flat, structurally.

Reply instead with the **AI-cost pair above the `AlwaysOff` floor**, at 40 bots: AI ms/frame under
`AlwaysOn` and under `Scheduler`, plus `granted` / `skipped` from `BotLodScheduler`. That is the
pair the M2 criterion is actually asking about. Take the samples interleaved in blocks rather than
arm-after-arm — a back-to-back run loads Editor settling and cold A* caches onto whichever arm went
first and reported `Scheduler` as the slower one.

Sample all nine workloads, not `AiActorController.Update()` alone: eight of the nine guards sit in
coroutines whose time is not in `BehaviourUpdate`, and `Recorder.Get` returns `isValid == true` for
a marker that does not exist because it creates one — so the obvious single recorder reads 0.000 ms
in both arms and says nothing is wrong.

---

## A7 — Can a player reach past ±2048 m? ⏱ ~10 min 🟢 confirmation only

Measured from YAML already, since the scenes are force-text: Dustbowl's `LevelBounds` is
1700 × 700 × 1600 centred at (-70.8, 207.6, -88.6), worst playable coordinate 920.8 m against a
`POS_MAX` of 2048 — 2.2× headroom, recorded as settled at the protocol freeze.

What YAML cannot answer is whether a player can *reach* past 2048 m via a vehicle, a lift, or an
out-of-bounds route. Dustbowl has ~1,900 transforms past 2048 m, all backdrop terrain outside the
play box.

**Reply with:** yes or no. If yes, position quantization clamps there and the actor sticks to an
invisible wall, and I need to know before a player finds it.

---

## A11 — Master-link plugin DLLs ⏱ ~10 min 🟡 now actually needed

This was optional while the master server did not exist. The master-server track is deploying it now
([issue #78](https://github.com/Sagitoaz/LTM/issues/78)), so the client needs to reach it.

`tools/build-libs.ps1` already copies all six libraries including `Ironfront.MasterClient.dll` and
`Ironfront.Net.MasterLink.dll` into `Assets/Plugins`. Run it and commit the result.

**Gotcha, and it has bitten this repo twice:** the CI drift check compares the DLL commit time
against its source directory commit time. If you edit a library and do not rebuild, Unity loads a
stale build and the compile error appears in the Editor with no CI signal. #77 nearly shipped
exactly that — three stale DLLs whose missing members would have broken the Unity compile, and the
drift check could not see the worst one because its glob was `Ironfront.Net.*.dll`, which never
matched `Ironfront.MasterClient.dll`. The glob is widened now.

**Reply with:** done, or "after the master-server track confirms the master is reachable".

---

## A12 — Server CPU percentage ⏱ ~2 min 🟡 decision, still open

`GS_HEARTBEAT` carries a `cpuPercent` the master sorts servers on. Unity exposes no portable
process-CPU counter, so I send **−1** rather than a fabricated number: a made-up value on a
matchmaking input is worse than an absent one, because the master acts on it. Average tick time is
a real load signal and is sent alongside.

**Reply with:** leave it at −1, or name a counter you would rather I read.

---

## A13 — Who owns the kill/death tally ⏱ ~5 min 🟡 decision, still open

`GS_MATCH_ENDED` carries per-player kills/deaths/score. `S_DEATH` already names the killer, but
nothing accumulates it, so `ServerMasterReporter.CollectScores` reports an **empty** list rather
than a row of zeroes per player — all-zero rows are indistinguishable from a match where nobody
scored, and the master stores what it is given.

Ticket accounting is unaffected: `MatchController.ReportDeath(team)` costs the dying team a ticket
and that is wired.

**Reply with:** whether the scoreboard is yours or mine. If mine, tell me where a kill is resolved
on the server and I will tally from there.

---

## F — The asmdef split ⏱ a separate phase 🟡 needs your agreement, not your time today

**GitHub issue:** #83. Not an action item for this round — a decision to make before the next one.

All eleven defects in #81 and #82 shared one property: **no test caught them, because no test can
reach them.** `Assets/` has no `.asmdef`, so `Assets/Scripts/Net/**` compiles into
`Assembly-CSharp`, and it depends on three types that live there:

| Type | Used by |
|---|---|
| `Actor` | `NetServerActor` — health, dead, activeWeapon |
| `Weapon` | `NetServerActor.WeaponId` |
| `IngameUi` | `ServerTickLoop` |

`Assembly-CSharp` is a *predefined assembly*: Unity compiles it after every asmdef, so **no asmdef
can reference it**. Two consequences:

1. Putting `Net/Server` in an asmdef costs it visibility of those three types — it stops compiling.
2. A test asmdef obeys the same rule, so no test assembly can see `NetServerActor` at all.

Adding the Test Framework does not fix this. The shape that does:

```
Ironfront.Net.Unity.Server (asmdef)
  IGameplayActor  { Health, IsAlive, WeaponNetworkId, Teleport(...) }
  IHudSink        { HideIngameUi() }

Assembly-CSharp (no asmdef, sees every asmdef automatically)
  ActorGameplayAdapter : IGameplayActor
  IngameUiSink         : IHudSink
```

The dependency inverts: the asmdef declares interfaces, `Assembly-CSharp` implements and registers
them. That is the direction Unity permits.

It touches your files, so it needs your agreement. Until it lands, the workaround stands: push
decisions down into the netstandard library where CI can test them, and leave Unity as a thin
adapter.

---

## Summary — what I need back

In this order. D1 blocks the deployment; E blocks trusting eleven fixes; A3 blocks A4.

| # | Item | Effort | Reply with |
|---|---|---|---|
| **D1** | Linux dedicated build + build-target fix | 45 min | release tag + image digest |
| **E1–E8** | Verify the eight Unity-side fixes | 30 min | one line per row |
| **A3** | Focused shadow-comparison rerun | 35 min | grounded summary + flat-ground warnings |
| **A4** | Prefab wiring, clock disabled | 10 min | prefab path + clock disabled |
| **S5/A9** | Bot LOD profiling — unblocked | 25 min | AI ms/frame under `AlwaysOn` and `Scheduler`, plus granted/skipped (amended round 9 — tick p99 cannot separate the arms) |
| **A11** | Master-link DLLs | 10 min | done, or after the master-server track confirms |
| **D2** | Sign off on `EditorBuild.cs` | 10 min | keep or move |
| **A7** | Reachable past ±2048 m? | 10 min | yes / no |
| **A12** | `cpuPercent` | 2 min | −1, or a counter |
| **A13** | Kill/death tally ownership | 5 min | yours or mine |
| **F** | Asmdef split — agree, do not start | — | agreed / objections |

Roughly 3 hours of Editor work. **D1 is the one nobody else can pull forward** — everything about
the deployment is finished except the artifact only a licensed Unity machine can produce.

Three of these are decisions rather than work (A12, A13, F) and cost minutes; answering them
unblocks work on my side.
