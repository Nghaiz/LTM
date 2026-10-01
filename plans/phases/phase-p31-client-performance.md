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
| A1 | Every release so far is a **Development** build: `EditorBuildWindowsHarness` hard-codes `BuildOptions.Development`; the v3.1.1 player's `boot.config` has `player-connection-mode=Listen` | `build-player.ps1` builds release by default (`-Development` opts in); `package-release.ps1` refuses a development build | PR A |
| A2 | Scripting backend **Mono** (`scriptingBackend: Standalone: 0`) | Release builds use **IL2CPP** (C++ config Release, "Faster runtime", stripping Minimal, stack traces with file/line). Harness/lane-B builds stay Mono for build speed. IL2CPP module installed through Hub 2026-10-02; MSVC 17.14 Build Tools installed | PR A |
| A3 | **GPU skinning off** (`meshDeformation: 0`): every soldier is skinned on the CPU | GPU (Batched) — Unity 6 batches the compute dispatches; meant for many skinned meshes | PR A |
| A4 | **Graphics jobs off.** DX11 supports only *Legacy* jobs; *Split* (Unity's recommendation) needs DX12, which costs VRAM and can hitch on first-use pipeline states | A/B: DX11, DX11 + Legacy jobs, DX12 + Split jobs. Keep DX11 as fallback whatever wins | todo |
| A5 | Stack traces captured for **every** log type (`m_StackTraceTypes` all ScriptOnly) | Log and Warning: None. Error, Assert, Exception keep their traces | PR A |
| A6 | `TimeManager.asset` says 0.333 s, but `NetServerBootstrap.Awake` already sets `Time.maximumDeltaTime = 0.1` on every process that loads a map, client and server alike (the 0.333 only applies in menus). A client-side copy was written and removed: its own test found the existing writer | none | done (already) |
| A7 | `DynamicsManager.asset` is serializedVersion 2 (never re-saved): Reuse Collision Callbacks off, Auto Sync Transforms likely on | Reuse callbacks on (after checking no code keeps a `Collision`); auto-sync only if the profiler shows the cost and no query depends on it | investigate |
| A8 | `bakeCollisionMeshes: 0` — mesh colliders are cooked when a vehicle or prop spawns mid-match | Prebake collision meshes | PR A |
| A9 | Incremental GC | already on (`gcIncremental: 1`) | done |
| A10 | Multithreaded rendering | always on for Windows players (separate render thread); nothing to set — confirm the render thread in the capture | verify |

## B. Per-frame CPU on the client

| ID | Finding (evidence) | Action | Status |
|---|---|---|---|
| B1 | Soldier prefabs ship `Animator` culling *Cull Update Transforms*, but `ActiveRaggy.Awake` forces **AlwaysAnimate** on every actor — all bots animate and write bones even behind the camera | On a networked client, remote actors cull when unseen; force a pose refresh before ragdolling; the server (`NetServerActor`) and the local player keep AlwaysAnimate | todo |
| B2 | Animated hierarchies that share a parent cannot write back in parallel (Unity guide, "Separate animating hierarchies") | Check where actors and remote proxies are parented | investigate |
| B3 | Minimap markers move every frame (canvas rebuilds) | Profile `Canvas.SendWillRenderCanvases`; split static/dynamic canvases; Raycast Target off on non-interactive graphics | profile |
| B4 | GC allocations per frame | Profile `GC.Alloc` on the client in a match | profile |
| B5 | Log volume | Count client log lines per minute in a match (A5 reduces the cost per line) | measure |

## C. Rendering

| ID | Finding (evidence) | Action | Status |
|---|---|---|---|
| C1 | Terrain `drawInstanced`: Forest Lake 1, **Island 0, Dustbowl 0** | Turn on; compare screenshots | todo |
| C2 | Repeated props and trees may not use GPU instancing | Find the most-drawn materials in the frame debugger; enable instancing where the shader supports it | investigate |
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
