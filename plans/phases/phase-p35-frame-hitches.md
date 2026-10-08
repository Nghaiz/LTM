# Phase P35: long frames on the owner's Ultra machine (handover)

Status: **investigated, not fixed.** Handed over by the owner on 2026-10-07 for a later session.
Everything below was measured on the owner's own machine (RTX 4060 Laptop, 8 GB, 32 threads,
Ultra preset), which is also the development PC.

## The report

Owner, 2026-10-06: "938 frames longer than 168 ms on my machine (Ultra), most of them while dead
waiting to respawn; read the logs."

## Where the number comes from

`%USERPROFILE%\AppData\LocalLow\LTM10\IronfrontReborn\Player-prev.log` (v4.3.0, Forest Lake, Night
Mode). The prediction clock logs `[NetPredictionClock] dropped N tick(s) after a X ms frame`
whenever a frame runs past its tick budget (about 168 ms); there are exactly **938** such lines,
168 to 1728 ms, median 238 ms. `Player.log` (a later day match) has 153.

Two caveats, both from `TickDropReport`/`NetPredictionClock`:

- The warning prints **only while the window has focus**. In the background drops are folded into
  one summary line, so an unfocused measuring client shows 0 here whatever happens.
- The clock is enabled at the **first deploy**, so the pre-deploy screen never appears in the count.

Analysis scripts (scratch, copy them if needed): `drops_by_state.py`, `deaths.py`,
`alive_drops.py`, `hitch_by_state.py` grouped the drops by the `[predict]` state lines.

## What was measured

| Situation | Long frames (>168 ms) | Notes |
|---|---|---|
| Alive, playing | ~0.11 per second | 764 of the 938. Spread through play; elevated in the first 15 s after a respawn. |
| Dead (`NoLiveBody`) | ~0.7 per second | 174 of the 938. Every death, vehicle or not; peaks 2 to 6 s after the death, when the loadout screen opens. |
| The respawn itself | one 400 to 840 ms frame per respawn | Reproduced. See finding 1. |
| Pre-deploy screen (match join) | 0.8 to 5 fps for 5 to 15 s | Reproduced. See finding 2. Invisible in the owner's count. |

`Stale` predict lines (211 drops) are a symptom, not a state: a stalled frame makes the next ack old.

## Findings

### 1. Every respawn costs one 400 to 840 ms frame: the grass renderer bakes synchronously

Reproduced with two diagnostics clients (`build/windows-ab`, `IRONFRONT_LOG_FRAMES=1`, autopilot) in a
live Azure room: each respawn logged `PreLateUpdate.ScriptRunBehaviourLateUpdate` at 280 to 736 ms
in one frame. In the Editor, teleporting the player 1261 m with `SpawnAt` put 56 ms into
`InstancedDetailRenderer.Frame` in that frame.

Cause: `InstancedDetailRenderer.Stream` reads every patch now inside the detail distance **at once**
while it holds the details ("a teleport or a respawn", by design), through
`TerrainData.ComputeDetailInstanceTransforms` (~10 ms for a dense patch). A respawn moves the camera
hundreds of metres, so every near patch is new.

Candidate fixes, to measure before choosing:

- On a jump (camera moved more than the prefetch distance in a frame), release to the terrain
  (`TerrainDetailHandOff.Release`) and stream the patches back under `BakeBudgetMs` over the next
  frames, nearest first. The terrain's own drawing then covers the gap; check its own cost on a jump.
- Or bake only the patches in the view frustum and within ~30 m at once, the rest under the budget.
- The owner's log matches: `deploy requested` is followed by one 400 to 650 ms frame every time.

**Fixed 2026-10-08 (prefetch at the picked spawn point).** While the player is dead with a spawn
point picked, `FpsActorController` sets `InstancedDetailRenderer.PrefetchPoint`; the renderer reads
the patches round it within 3 ms a frame, nearest first, and keeps them until the jump. The v4.5.0
playtest log (2026-10-07) still showed it: 25 deploys, 25 frames of 196 to 706 ms right after.
Editor, Forest Lake's own terrain at Ultra (120 m, density 1.0), an 800 m jump: 57.4 ms and 104
patches read in the jump frame before; 0.1 ms and none after, the 128 patches read over 14 frames
of the wait (worst 13.3 ms: one dense patch). Not yet measured in a player build. A deploy with no
flag picked ("flag any") is not covered: the server chooses the spot.

### 2. The pre-deploy screen renders at 0.8 to 5 fps

Measured on the joining client: GPU 157 to 1294 ms per frame, 2,000 to 3,300 draws and 3.4 to 5.7 M
triangles (play: ~1,800 draws, 3.1 M triangles, 23 ms GPU). Draw count is not the cost; per-pixel
work is (night lights, overdraw).

Two cameras render: the map's `SceneryCamera` (enabled by `FpsActorController.Start`, depth 100, full
culling mask, clears to skybox) and the player's FP camera at the parked body, **(0, 1000, 0)**,
looking over the whole map at Ultra. The scenery camera paints over the FP camera's whole frame, so
that frame is pure waste. First thing to try: disable the FP camera (or its rendering) until
`EnterDeployedView`.

**Fixed 2026-10-08.** `FpsActorController.Start` turns the FP camera off with the scenery camera on;
`SpawnAt` and `EnterDeployedView` turn it back on (`FirstPersonCamera`). Editor, Forest Lake's
pre-deploy screen, toggled live: batches 1,695 to 1,075, SetPass calls 1,321 to 840, triangles 2.45 M
to 1.99 M, `Camera.Render` 7.7 to about 5.2 ms a frame. The per-pixel cost that made the owner's
GPU take 157 to 1,294 ms was this camera's whole frame; not yet measured in a player build.

### 3. With focus, most of a long frame is outside the player loop

The single most important lead for the alive and dead long frames, found last:

- Background client: `[hitch]` frame time equals the summed player-loop time, dominated by
  `TimeUpdate.WaitForLastPresentationAndUpdateTime` (GPU bound).
- **Focused** client (`tmp/hitch-probe/focused-a-player.log`): frames of 170 to 640 ms with only 20
  to 60 ms inside the timed player loop. The rest is spent between frames: Windows message
  processing, input, or something hooked into the focused window.

Not yet separated: cua-driver's UIA client (it was running; see memory
`cua-driver-uia-crashes-unity-on-exit`), overlays (NVIDIA, Game Bar), raw mouse input, IME. Next step:
reproduce focused with cua-driver stopped and overlays off, then attribute with a development build
and the Profiler (`build-player.ps1 -Development`), whose timeline shows the main thread between
`PlayerLoop` calls.

### 2026-10-09: measured in a player build

Release IL2CPP player with diagnostics (`-KeepDiagnostics`), the owner's machine, two autopilot
clients in a live Azure room (Forest Lake, night, 50 bots a side), the measured one focused.

- **Finding 3 did not reproduce.** About 190 s focused with cua-driver running idle, then three
  cua-driver reads of the window (UIA tree, screenshot): no frame over 168 ms, no dropped tick, and
  every long frame (50 to 66 ms) inside the player loop (physics and scripts in a fight). The
  2026-10-07 probe's focused stalls remain unexplained; the autopilot stands still, the owner plays.
- **Reading grass patches is the terrain's own `ComputeDetailInstanceTransforms`**, main thread
  only (it throws on a worker), and costs 2 to 3 microseconds a detail in this player once a match
  is under way (0.4 at the very first deploy, and in the Editor): a "flag any" respawn read 88 to 96
  patch prototypes, 117 to 154 thousand details, in 332 to 440 ms, of which the terrain's read was
  298 ms, packing 33 and upload 1. While moving, one dense patch alone is 20 to 60 ms, past the
  1 ms budget (at least one is read a frame).
- **Kept off the GPU (2026-10-09).** `DetailPatchCache` keeps a patch's packed details in memory
  when it leaves the GPU (at most 1.5 M details, about 30 MB, oldest forgotten first), so reading
  it again costs only its upload. Editor, Forest Lake at Ultra: returning to a place left 800 m
  behind, 56.6 ms and 104 terrain reads became 0.6 ms and none.
- A frame that spends 20 ms or more reading patches now logs `[details] ... read N patch
  prototype(s) ... in X ms` with the split (at most once in 5 s), in every build, so the next
  playtest's logs say how much of the stutter is grass.

### Not the cause (measured)

- Night Mode: the 153-drop `Player.log` is a day match.
- The minimap and the night minimap picture: both render once.
- The loadout UI's own work: under 25 ms in every itemised hitch.
- GC: `gc0+0` on nearly every long frame; incremental GC is on.
- Practice (offline): dead frames cost +6 ms GPU and +8 ms main thread in the Editor, nothing like
  the network client.

## How to measure

- Player: `tools/build-player.ps1 -KeepDiagnostics -OutputDirectory build/windows-ab`, run with
  `IRONFRONT_LOG_FRAMES=1` (`[frames]`, `[hitch]`, `[loop]`, `[render]` every 5 s).
- Two clients in one live room (no local servers): `IRONFRONT_AUTOPLAY=claudetest1:Claudetest123`
  plus `IRONFRONT_AUTOPLAY_CREATE=name;3;50;night` on one, `..._JOIN=name` on the other,
  `IRONFRONT_AUTOPLAY_UNCAPPED=1` on the measured one. The autopilot redeploys 2 s after the
  loadout opens, so its deaths are short; the owner's last 4 to 17 s.
- Keep the measured window **focused** (finding 3), or the owner's numbers cannot be reproduced.
- `-logFile` paths are overwritten on relaunch: copy a log before starting the next run.
