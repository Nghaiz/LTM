# Dev A — S4 evidence, S5 blocker, and S7 wiring

**Date:** 2026-08-15
**Branch:** `feat/a4-player-prediction-components`
**Unity:** 6000.3.21f1

## S4 — measured

Dustbowl was run as a loopback server with one player and 47 bots. The final clean
session had zero Console errors.

- `ServerSnapshotStage.FixedUpdate`: **0 B GC Alloc**, **0.12 ms** in the selected
  Profiler sample (3 calls).
- `ServerInputStage.FixedUpdate`: **0 B GC Alloc**, **0.01 ms** in the selected
  Profiler sample (3 calls).
- No `[net] server over budget` warning appeared in the session log.

The first run emitted six identical Cloth errors. They were traced to the six inactive
`HQ Flag` objects in Dustbowl; each Cloth component had a skinned mesh with zero bones.
The six invalid Cloth components are removed in the scene patch. A subsequent run was
clean.

## S5 / A9 — blocked before a valid measurement

`BotLodScheduler` currently exists only as an engine-free policy class. No Unity wrapper
calls `ShouldTick`, and there is no component or serialized threshold in the Inspector to
toggle. Therefore an LOD-on/LOD-off Profiler comparison cannot currently exercise two
different code paths; recording it would produce two measurements of the same behaviour.

The proposed default mechanism, repeatedly assigning
`AiActorController.enabled`, is not accepted by Dev A without a wrapper change. The
controller starts eight coroutines (`AiBlocked`, `AiVehicle`, `AiOrders`, `AiTarget`,
`AiWeapon`, `AiTrack`, `AiScan`, and `AiTrackClosestActors`) and also performs
`Time.deltaTime`-driven work in `Update`. Toggling only the MonoBehaviour risks splitting
the controller into paused `Update` state and independently paced coroutine state.

A single `updateInterval` guard around `Update` is also incomplete because it does not gate
the eight coroutine workloads. Dev C needs to provide or agree on a Unity integration seam
that gates all intended AI work and exposes a deterministic LOD-off override. After that
lands, Dev A can record the requested 32-bot p99 and AI before/after numbers.

## S7 — wired and Unity-verified

`MatchController` and `ServerMasterReporter` are serialized on the Dustbowl `NetServer`
GameObject. The reporter remains in its supported standalone defaults. Six capture points
are wired in stable Hierarchy order, which defines wire ids 0 through 5:

0. Bridge
1. Town
2. Outpost
3. Oasis
4. Mine
5. Fortress

Static validation confirms all component references are owned exactly once, the array has
no gaps, and the six invalid Cloth blocks are absent.

Unity imported the minimal scene patch and completed a Play session from 00:25:42 to
00:25:57 on 2026-08-15. `Editor.log` and
`ironfront-20260815-002542.log` show:

- `[net] role = Server`;
- the loopback server started with 16 slots;
- no compile error, exception, capture-point error, Cloth error, or server-over-budget line;
- the session ended normally.

The unsigned-ticket warning is the documented development configuration. The remaining
MCP logger warning occurred during shutdown and is unrelated to the game/server layer.
