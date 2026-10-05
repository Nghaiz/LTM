# P33 — GPU-driven terrain details: the draws that take the client's CPU

- **Started:** 2026-10-05. Owner's report: the client takes the whole CPU on 10+ players' machines
  (theirs included) while far heavier AAA games do not; "it is the game's bug, fix it the way big
  games do, with every feature still shown in full".
- **Owner rules for this phase:** the metric is CPU use. Never explain or test anything through
  temperature, the power supply or the frame rate (see memory `never-use-temperature-in-perf-verdicts`,
  `cpu-pegging-is-in-the-game`). No visible feature may be lost. Single-threaded, no subagents.
- **Branch:** `perf/client-draw-attribution` already holds the measuring tools (commit `cef20340`,
  `GpuCostProbe` content states). Its PR #545 was closed on purpose: tools alone are not a fix.
  The P33 fix lands on top of it in ONE PR to `develop`.

## What was measured (v4.2.0, 95b65b0, Forest Lake, 100 bots, Ultra, focused client)

| Check | Result | Verdict |
|---|---|---|
| Idle in the main menu | 0.11 cores for the whole process; job workers ~1 % each | no spinning workers |
| Threads born and dying | process CPU = sum of long-lived threads (gap ±0.05 cores); 0-1 new thread ids in 10 s | no thread churn |
| Networking | `MasterClient` reads with `ReadAsync`; game transport is polled on the main thread | no busy loop |
| Playing client, per thread | main 0.94 cores, render thread (`UnityGfxDeviceWorker`) 0.79, NVIDIA DX11 driver thread (`nvwgf2umx.dll`) 0.64, four job workers 1.35 | the CPU goes to **submitting draws** |
| DX12 + Split graphics jobs (`gfx-threading-mode=6`, `-force-d3d12`) | ~117 ms CPU per frame vs ~52 ms on DX11 + Legacy jobs; `D3D12 Submission Thread` alone 0.85 cores | rejected; DX11 stays |
| `GpuCostProbe` states (`tools/perf/probe_attribution.py`) | shadows off: draws 1,400-1,700 -> 334-383, SetPass 650-720 -> 144-166 | **shadow passes ~78 % of draws** |
| Frame Debugger walk of one frame (`tools/perf/FrameDebuggerWalk.cs`) | 2,354 events / 2,612 draw calls: shadow map 65 %, camera depth 17 %, main pass 17 % | see below |

The walked frame, by content:

| Content | Events | Share | Notes |
|---|---|---|---|
| **Terrain details** (`SM_Grass_A_LOD3` + flower, blueberry, fern; shader `M_Foliage_Wind`) | **1,232** | **52 %** | 705 in the shadow map, 216 depth, 216 main; 511 instances per draw (Unity's cap), cause "Unknown reason" |
| Instanced trees (`Hidden/Ironfront/InstancedTrees/*`) | ~590 | 25 % | mostly the shadow cascades (each command drawn into all four) |
| Everything else (Standard props, vehicles, soldiers, rocks, terrain, UI) | ~530 | 23 % | |

Why the details cost so much: Forest Lake's detail layer 0 (`Ter_Grass_A`, prototype mesh,
instancing on, renderer shadow **On**) holds **3.8 M instances** on 1.35 M cells (detail resolution
1984, 64 cells per patch, `InstanceCountMode`). Unity draws a detail **patch** (64 x 64 cells,
~99 m) whole whenever it touches the view or a cascade, 511 instances a call, and uploads the
instances' matrices again for every pass: ~110 k grass instances in the main pass of one frame,
~360 k instance-draws in the shadow map. Prototypes 1-3 (fern 22 k, blueberry 29 k, flower 111 k)
also cast shadows; 4-7 (rocks, `Standard`) do not.

## Goal

Draw Forest Lake's terrain details the way `InstancedTreeRenderer` draws its trees: instance data
on the GPU, a compute pass that keeps only the instances inside the detail distance and the view
(or able to throw a shadow into it), and one indirect draw per (prototype, pass). Same meshes,
same materials, same density, same sizes, same shadows -- a few dozen draw calls instead of ~1,200.

## Ownership

- `Ironfront_Reborn/Assets/Scripts/Rendering/` — new `InstancedDetailRenderer.cs`, `DetailCatalog.cs`,
  `Resources/DetailCulling.compute`, instanced detail shaders under `Resources/InstancedDetails/`
- `Ironfront_Reborn/Assets/Scripts/Rendering/Editor/` — extend `InstancedTreeShaderGenerator` (or a
  sibling) to emit the instanced variants of `M_Foliage_Wind` and `Standard` the details use
- `Ironfront_Reborn/Assets/Scripts/Rendering/InstancedTreeBootstrap.cs` — attach the detail renderer
  where the tree renderer is attached
- `Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/GpuCostProbe.cs` — a `nodetailsgpu` state
- EditMode tests beside the tree renderer's

## Design (mirror `InstancedTreeRenderer`; read it and `TreeCulling.compute` first)

1. **Catalog.** Per detail prototype: mesh + submesh + material (from the prototype GameObject),
   renderer shadow-casting mode, width/height ranges, `positionJitter`, `holeEdgePadding`,
   `alignToGround`. Refuse (and let the terrain keep drawing, logged once) anything this cannot
   draw: texture (billboard) details, a material without an instanced variant, a machine without
   compute shaders.
2. **Data on the GPU, uploaded once.** One R8/R16 texture per prototype from
   `GetDetailLayer` (instance count per cell in `InstanceCountMode`), the heightmap, the holes map.
   Do NOT bake 3.8 M instances into a buffer.
3. **Per frame, compute.** Visit only the cells within `detailObjectDistance` of the camera
   (Ultra 120 m, density scale `terrainDetailDensityScale`). For instance *k* of cell *(x, z)* derive
   position jitter, scale and Y rotation from a hash of (prototype, x, z, k) -- deterministic, so
   grass never shimmers. Height from the heightmap, skip holes. Append to the prototype's bucket:
   in view -> drawn with its shadow (the tree renderer's `Shadowed`); out of view but within the
   shadow range and able to cast into the view -> `ShadowOnly` (only for prototypes whose renderer
   casts). Write the indirect args.
4. **Draw.** `Graphics.DrawMeshInstancedIndirect` per (prototype, pass) with the prototype's own
   shadow-casting mode, layer and receive-shadow flags, exactly as the tree renderer does.
5. **Hand-off.** While it runs the terrain draws no details (`detailObjectDensity = 0` or
   `detailObjectDistance = 0`, restored on disable); trees and the heightmap are untouched. The
   minimap's one-off render must still see what it saw before (the tree renderer's "taken over
   from the second frame" rule).
6. **Settings.** Detail distance and density follow the quality preset exactly as the terrain's do;
   changing the preset in Settings changes what this draws.

Unity's own scatter (its per-cell random sequence) is not reproduced bit for bit: count per cell,
jitter range, scale ranges and rotation distribution are. Say so in the class remarks.

## Steps

1. Check which maps carry details (`Dustbowl`, `Island`, `ForestLake`; prototypes and modes) and
   record them here. → verify: a table in this file.
2. Catalog + compute + renderer + shaders as above, all files first, one verify at the end. →
   verify: Editor compiles; EditMode tests for the catalog's refusals and the hash's determinism.
3. Side-by-side: same Forest Lake spot, terrain details vs GPU details, screenshots at ground level,
   prone and from a hill, day and night. → verify: same coverage and look; shadows present.
4. Measure with the Frame Debugger walk and an interleaved probe A/B (release IL2CPP player with
   `-KeepDiagnostics`, `IRONFRONT_GPU_PROBE=1`, state `nodetailsgpu` switching back to terrain
   details). → verify: acceptance below.
5. `dotnet test Ironfront.sln` (8 projects, 0 failed), `tools/check-net-layering.ps1`, then one PR.

## Acceptance

- Grass-heavy Forest Lake view: terrain-detail draw calls from ~1,200 to **≤ 40** per frame; total
  draws per frame down by at least 45 % on the same view (Frame Debugger walk, before/after).
- Playing client CPU (process cores, and render thread + driver thread + job workers) lower in an
  interleaved A/B on the same match; numbers in the PR.
- No visible loss: details at the same distance, density and sizes, casting and receiving shadows,
  wind unchanged; screenshots in the PR.
- Maps without details, a machine without compute shaders and the minimap render behave as before.

## Next after P33 (not in scope)

Instanced trees still draw every shadow command into all four cascades (~590 events in the walked
frame). Fewer LOD buckets in the shadow passes or per-cascade buckets are the candidates; measure
first with the same two tools.

## Tools (all in the repo)

- `tools/perf/FrameDebuggerWalk.cs` + `tools/perf/unity_exec.py` — walk one frame of a development
  player (`build-player.ps1 -Development`) from a shell; `tools/perf/fd_summary.py` summarises it.
- `GpuCostProbe` (`IRONFRONT_GPU_PROBE=1`) + `tools/perf/probe_attribution.py` — price features,
  layers and root groups in a live match from the `[render]`/`[frames]` log lines.
- `tmp/perf/` holds this session's raw logs (`attr-c1.log`, `fd-walk.tsv`); untracked.
