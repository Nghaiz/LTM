# P34 — Tree shadows drawn per cascade

- **Opened:** 2026-10-06, after P33. Same owner rules as P33: the metric is CPU use (draw
  submission); never reason through temperature, the power supply or the frame rate; no visible
  feature may be lost; single-threaded, no subagents; one PR to `develop`.
- **Read first:** `Ironfront_Reborn/Assets/Scripts/Rendering/InstancedTreeRenderer.cs`,
  `Resources/TreeCulling.compute`, and P33's "Step 4b" in
  `plans/phases/phase-p33-gpu-driven-terrain-details.md` (why the cheap fix failed).

## The problem (measured)

Forest Lake's trees are drawn by `InstancedTreeRenderer`: indirect instanced draws, one per
(prototype, LOD, part, pass), a GPU cull filling the buckets. Every shadow-casting draw lands in
**all four** shadow cascades. Frame Debugger walk, 2026-10-06 (development player, 50-a-side
match): the tree sequence in the shadow map is identical in each cascade — `M_Foliage` x10,
`M_Foliage_Wind` x68, rock x2, `M_Foliage_Wind` x2 — 328 of the frame's 554 shadow-map events, and
each draw renders every kept instance of its bucket four times on the GPU.

## What does not work (measured, do not retry)

Bounding each bucket's draw by the cells that reach it instead of the whole terrain:
pixel-identical, and no fewer draws (same walk sequence; probe A/B 929 vs 980 draws, noise). A LOD
bucket is a ring of distance around the camera and the tree cells are 128 m, so its bounding box
always holds the camera; Unity culls casters per cascade by bounds against volumes that all hold
the camera. Any bounds-only variant hits the same wall.

## Candidate design (spike it first)

Draw the tree shadow casters ourselves, once per cascade, with only the trees that cascade needs:

1. Per cascade `i`, a command buffer on the shadow light:
   `sun.AddCommandBuffer(LightEvent.BeforeShadowMapPass, cb[i], ShadowMapPass.DirectionalCascade0 << i)`
   (check the exact flag values). Each issues
   `cb.DrawMeshInstancedIndirect(mesh, submesh, material, shadowCasterPassIndex, args, offset, props)`
   per reachable bucket.
2. The GPU cull fills per-cascade shadow buckets: a tree goes to cascade `i` when its sphere,
   swept along the light (the existing `ShadowReachPerRadius` sweep), meets cascade `i`'s split
   sphere (`QualitySettings.shadowCascade4Split` x `shadowDistance`, Ultra/High: 6.7 %, 20 %,
   46.7 % of 150 m). The main-pass and depth draws keep casting `Off`.
3. CPU reachability per cascade (the `MarkReachable` cell test against each split sphere), so a
   cascade issues no draw for a bucket it cannot hold: far LODs never enter the near cascades.
4. Without a shadow light, with shadows off, or on one cascade (Low/Medium use 1 and 2), keep
   today's path.

**Spike questions, answer before building:** does Unity set the cascade's view-projection,
`unity_LightShadowBias` and the shadow-caster keywords before `BeforeShadowMapPass` for each
cascade, so the procedural `ShadowCaster` pass renders correctly from a command buffer? Does the
per-cascade flag fire once per cascade per frame? Does the Moon (Night Mode's `RenderSettings.sun`)
work the same? If any answer is no, stop and record it here.

## Acceptance

- Same Forest Lake view: tree shadow-map events from 4x the bucket count to the per-cascade
  reachable count (target: at most half of today's 328), shown by `tools/perf/FrameDebuggerWalk.cs`.
- Pixel A/B in the Editor at the six views of P33 plus low sun: shadows unchanged (the P33 method:
  render A, render B, render A again; compare B with the second A).
- Interleaved live probe A/B (`GpuCostProbe`, a state that switches back to today's casting):
  draws and CPU per frame lower; numbers in the PR.
- EditMode: per-cascade buckets hold exactly the trees each cascade needs (synthetic terrain, as
  `InstancedTreeShadowCullingTests`); all Rendering tests green on the GPU.

## Tools

`tools/perf/FrameDebuggerWalk.cs` + `unity_exec.py` + `fd_summary.py` (development player:
`tools/build-player.ps1 -Development`); `GpuCostProbe` + `probe_attribution.py`;
`tools/perf/thread_cpu.ps1` + `thread_ab.py`. The two-client match launcher pattern: two
`MenuAutopilot` clients (`IRONFRONT_AUTOPLAY_CREATE=name;3;50`, `IRONFRONT_AUTOPLAY_JOIN=name`).
Wait for a walk by the client log's "Changing Frame Debugger Draw Call Limit" count and the TSV's
mtime, not by `# done` (an old file already ends with it).
