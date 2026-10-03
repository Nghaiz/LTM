# P31 — Client performance: smooth on a mid-range PC without cutting the far view

- **Started:** 2026-10-02. Owner's request: "client stutters and lags on weak PCs, especially
  Forest Lake; make it smooth for mid-range PCs without hurting graphics; binoculars and aircraft
  must still see the whole map." Then: "apply every optimisation the community, the docs and the
  papers recommend; GPU skinning, graphics jobs, incremental GC, backend, multithreaded
  rendering... handle all of it."
- **Target (owner, 2026-10-02): 60 fps in the release build.** The heaviest scene -- Forest Lake,
  100 bots -- must hold a mean frame of 16.7 ms or less on the default preset on a mid-range PC.
  The 30 fps `BackgroundFrameCap` applies only to an unfocused window and stays.
- **Rule for every item:** measure before and after on the same map and spot. A change that cannot
  show a number is reported as unmeasured, not as a win.
- **Sources:** Unity, *Optimize your game performance for consoles and PCs (Unity 6 edition)*;
  Unity Manual, *DirectX* feature table (Unity 6); Unity blog, *DirectX 12 improvements in Unity 6*;
  Unity Discussions threads on graphics jobs and batched compute skinning.

## Done before this phase

| PR | What |
|---|---|
| #465 | Four graphics presets (Low/Medium/High/Ultra), High default, recommended preset on first launch, applied at startup (it was never applied before) |

## A. Build and player configuration

| ID | Finding (evidence) | Action | Status |
|---|---|---|---|
| A1 | Every release so far is a **Development** build: `EditorBuildWindowsHarness` hard-codes `BuildOptions.Development`; the v3.1.1 player's `boot.config` has `player-connection-mode=Listen` | `build-player.ps1` builds release by default (`-Development` opts in); `package-release.ps1` refuses a development build | done #466 |
| A2 | Scripting backend **Mono** (`scriptingBackend: Standalone: 0`) | Release builds use **IL2CPP** (C++ config Release, "Faster runtime", stripping Minimal, stack traces with file/line). Harness/lane-B builds stay Mono for build speed. IL2CPP module installed through Hub 2026-10-02; MSVC 17.14 Build Tools installed | done #466 |
| A3 | **GPU skinning off** (`meshDeformation: 0`): every soldier is skinned on the CPU | GPU (Batched) — Unity 6 batches the compute dispatches; meant for many skinned meshes | done #466 |
| A4 | **Graphics jobs off.** DX11 supports only *Legacy* jobs; *Split* (Unity's recommendation) needs DX12, which costs VRAM and can hitch on first-use pipeline states | A/B: DX11, DX11 + Legacy jobs, DX12 + Split jobs. Keep DX11 as fallback whatever wins | done #472 (DX11 + Legacy) |
| A5 | Stack traces captured for **every** log type (`m_StackTraceTypes` all ScriptOnly) | Log and Warning: None. Error, Assert, Exception keep their traces | done #466 |
| A6 | `TimeManager.asset` says 0.333 s, but `NetServerBootstrap.Awake` already sets `Time.maximumDeltaTime = 0.1` on every process that loads a map, client and server alike (the 0.333 only applies in menus). A client-side copy was written and removed: its own test found the existing writer | none | done (already) |
| A7 | `DynamicsManager.asset` is serializedVersion 2 (never re-saved): Reuse Collision Callbacks off, Auto Sync Transforms likely on | Reuse callbacks on (after checking no code keeps a `Collision`); auto-sync only if the profiler shows the cost and no query depends on it | auto-sync done #488 (online client only, 63 syncs a frame -> 1); reuse callbacks todo |
| A8 | `bakeCollisionMeshes: 0` — mesh colliders are cooked when a vehicle or prop spawns mid-match | Prebake collision meshes | done #466 |
| A9 | Incremental GC | already on (`gcIncremental: 1`) | done |
| A10 | Multithreaded rendering | always on for Windows players (separate render thread); nothing to set — confirm the render thread in the capture | done (Render Thread in the 2026-10-02 capture) |

## B. Per-frame CPU on the client

| ID | Finding (evidence) | Action | Status |
|---|---|---|---|
| B1 | Soldier prefabs ship `Animator` culling *Cull Update Transforms*, but `ActiveRaggy.Awake` forces **AlwaysAnimate** on every actor — all bots animate and write bones even behind the camera | On a networked client, remote actors cull when unseen; force a pose refresh before ragdolling; the server (`NetServerActor`) and the local player keep AlwaysAnimate | done #476 |
| B2 | Animated hierarchies that share a parent cannot write back in parallel (Unity guide, "Separate animating hierarchies") | Check where actors and remote proxies are parented | done #476 (own hierarchies) |
| B3 | Minimap markers move every frame (canvas rebuilds) | Profile `Canvas.SendWillRenderCanvases`; split static/dynamic canvases; Raycast Target off on non-interactive graphics | done #468, #469, #487 (canvases 9.3 -> ~1.2 ms) |
| B4 | GC allocations per frame | Profile `GC.Alloc` on the client in a match | profile |
| B5 | Log volume | Count client log lines per minute in a match (A5 reduces the cost per line) | measure |

## C. Rendering

| ID | Finding (evidence) | Action | Status |
|---|---|---|---|
| C1 | Terrain `drawInstanced`: Forest Lake 1, **Island 0, Dustbowl 0** | Turn on; compare screenshots | todo |
| C2 | Repeated props and trees may not use GPU instancing | Find the most-drawn materials in the frame debugger; enable instancing where the shader supports it | trees done #481/#482 (GPU culled, instanced); props investigate |
| C3 | Soldier `SkinnedMeshRenderer`s have *Skinned Motion Vectors* on; nothing consumes motion vectors (no TAA, no motion blur) | Off, after confirming no camera requests motion vectors | investigate |
| C4 | No occlusion data baked on any map | Low value on open maps; evaluate after the rest | later |
| C5 | Shadows, LOD bias, terrain and texture limits per preset | done in #465 | done |
| C6 | Two realtime reflection probes per map, rendered once at map load (`ReflectionProber`) | none — not a per-frame cost | n/a |
| C7 | Post-processing: Forest Lake Color Grading + Vignette; Island/Dustbowl none; camera NoiseAndGrain | none | n/a |

## D. Hitches

| ID | Finding | Action | Status |
|---|---|---|---|
| D1 | First use of a shader/material can hitch (driver program creation; worse on DX12) | Warm up at map load if the capture shows `CreateGPUProgram` spikes | profile |
| D2 | Effects, decals and projectiles instantiated per shot | Pool what the capture shows | profile |
| D3 | `asyncUploadBufferSize` 4 MB on every preset | Evaluate 16 MB (Unity's default) against load hitches | evaluate |

## Measurement protocol

- **Baseline:** the v3.1.1 player in `build/windows` (stamp `d7bc61f5`, Development, Mono, DX11),
  which is what players run today, against the Azure servers on the same commit. Forest Lake,
  two clients (accounts claudetest1/2), `IRONFRONT_LOG_FRAMES=1`, plus a profiler `.raw` capture.
- **Each change:** same map and spot, at least three minutes; report p50/p95/p99 frame ms and
  hitches (>50 ms) per minute from the `[frames]` lines, and the top PlayerLoop systems.

## Measurements (Forest Lake, 100 bots, focused client, VSync off, RTX 4060 Laptop / Ryzen 9 7945HX)

| Build | fps | mean ms | p99 ms (median window) | hitches/min |
|---|---|---|---|---|
| v3.1.1 (Development, Mono, Fantastic) | 25.2 | 39.6 | 68.3 | 234 |
| PR A: release, IL2CPP, GPU skinning, no Log stack traces, High | 32.7 | 30.6 | 50.8 | 80 |
| #476, match minute 3-5 / 5-8 / 8+ | 31.7 / 27.2 / 25.6 | 32 / 37 / 40 | | |

**Separate runs do not compare.** The same build swings by 10 ms between matches (how many bodies
lie on the field, how many fights the camera sees), so every result below is an **interleaved A/B
in one match**: a measuring-only env switch flips the change every 30 s in one client, and the
five-second `[frames]`/`[loop]` windows are grouped by the state they ran under. Windows that
straddle a switch, sit under the unfocused 30 fps cap (`WaitForLastPresentation` > 5 ms) or come
before deployment are dropped. The switch is never committed; the scripts are
`tmp/perf/abstats.py` and `tmp/perf/matchstats.py` (local, untracked). V-sync quantizes the frame
mean (many windows sit at exactly 33.4 ms), so compare busy CPU (`[loop]` minus
`WaitForLastPresentation`) rather than the mean.

| Change | Measured |
|---|---|
| #481 GPU trees (12,412 terrain trees) | practice, release player: main-thread rendering 6.47 -> 2.39 ms |
| #488 client auto-sync off, one sync a frame | one 100-bot match, 129 windows: mean 46.6 -> 41.4 ms, p99 124 -> 88 ms, Update 10.3 -> 7.2 ms; prediction corrections 64 -> 18 |
| 30 Hz client physics on foot (rejected, never merged) | one match, 62 + 62 playing windows: busy CPU 26.3 ms at 60 Hz vs **29.5 ms at 30 Hz**; physics 7.5 vs 8.5 ms |

**Where a 100-bot frame goes now** (release IL2CPP with diagnostics, build 4d8ddb67 with auto-sync
off, 63 windows past minute 2, mean 42.2 ms): physics 12.3, rendering 8.1, script Update 7.4,
animation 5.1 (begin 1.5 + end 3.7), LateUpdate 1.6, canvases 1.2, skinned meshes 0.5 ms.
**Status: about 24 fps mid-match against the 60 fps target.**

Baseline profile, in-match frames (v3.1.1, 400 frames): main-thread rendering 10.9 ms, **UI canvases
9.3 ms**, script Update + LateUpdate 8.6 ms, physics 4.0 ms (3 steps), animation 1.3 ms. The
release run's hitch frames average rendering 22.6 ms, physics 16.0 ms (3-4 steps), canvases 8.9 ms,
Update 7.1 ms, LateUpdate 4.1 ms.

**UI root cause found (B3):** PR #381 dropped the line that destroyed the `ActorBlip` a body marker
borrows from the blip prefab. Left alive, it hides the icon every frame and `MinimapMarker` shows
it again: ~50 `OnDisable` + ~50 `OnEnable` per frame. On top of that every trail dot (7-12 per
icon, 100 icons) changes anchors, size and colour every frame, so ~800 graphics rebuild per frame.

## Progress log

- 2026-10-02 — IL2CPP module installed through Unity Hub; VS 2022 Build Tools (MSVC 17.14)
  installed. Research and code survey done (tables above).
- 2026-10-02 — Baseline measured and profiled. PR A (A1, A2, A3, A5, A6, A8) built and measured:
  login (System.Text.Json under IL2CPP + Minimal stripping) and a 100-bot match work.
- 2026-10-02 — #467-#476: minimap and overlays, reflection probes, graphics jobs, frame-log
  phases, corpse freeze, remote animation culling (table rows above).
- 2026-10-02 — #477 practice plays offline (it took no input and the score sat at 1000-1000);
  #478-#480 ragdoll interpolation, remote wheels and ground probes only near or in view;
  #481/#482 GPU trees; #483 `IRONFRONT_PROFILE_AT` capture in a development player; #484 far
  knocked-over bots freeze after landing; #485 no ground probe for a remote helicopter; #486 scope
  blackout IMGUI only on the first-person weapon (68 empty passes a frame); #487 minimap stencil
  mask (`ClipperRegistry.Cull` 1.4 ms); #488 client auto-sync; #490 the auto-sync tests stop the
  Editor re-saving `DynamicsManager.asset`.
- 2026-10-02 — **30 Hz client physics on foot rejected by measurement** (table above). The owner
  allowed it as the one trade-off; a bot cap and half-rate animation for far soldiers were offered
  and not taken. Do not retry a lower client fixed rate without new evidence about what one step
  costs. The project stays at 60 Hz everywhere.
- 2026-10-02 — Paused by the owner, and a release cut from develop anyway: the client is better
  than v3.1.1 even short of the target.

## Next session — start here

Ranked by measured size. Measure every item with the interleaved A/B above.

1. **Physics, 12.3 ms.** About 4.9 ms a step at 2-3 steps a frame, and the step cost follows
   simulated time rather than step count (the 30 Hz result). An Editor bench of the same bodies
   in a Forest Lake preview scene did not explain it: bare map 0.001 ms a step, 24 settled corpses
   0.05, 8 live ragdolls 0.25, 10 remote vehicles moved every step 0.88 (0.55 with wheels rested).
   The development capture puts 1.6 ms a frame in `FinalizeUpdateTask`, 0.7 in contact updates,
   0.7 in `PxVehicles` and 0.6 in `SwapPhysXBuffers`, plus `ClothScene` 1.1 ms on the workers
   (10 `Cloth` components). Next: capture `PxScene.simulate` internals in a match, and count the
   colliders the terrain trees and props add.
   **P32 PR 5 (2026-10-04):** a capture at the peak (268 bodies) read physics 7.1 ms of 29.8, 1.8
   steps a frame; far corpses now freeze without the long wait and parked remote vehicles are not
   rewritten: physics median 3.33 -> 2.87 ms in an interleaved A/B (see the P32 file).
2. **Animation, 5.1 ms.** IK and twist-bone jobs about 1 ms; remote bodies already cull when
   unseen (#476). Look for savings that change nothing on screen: IK on remote bodies, and
   animator parameters `RemoteActorView.Apply` writes every frame even when unchanged.
3. **Rendering, 8.1 ms on the main thread.** Culling 3 ms, `PostProcessLayer.OnPreCull` 0.7 ms,
   `LOD.ComputeLOD` 0.5 ms, shadow casters. C1 (terrain `drawInstanced` on Island and Dustbowl)
   is still open.
4. **Script Update, 7.4 ms.** `RemoteActorRegistry.Update` 2.4 ms self (development capture);
   `NetPredictionClock` and `NetClientBootstrap` about 0.7 ms each.
5. Open rows above: A7 reuse collision callbacks, B4 GC allocations, B5 log volume, C3 skinned
   motion vectors, D1-D3 hitches.
