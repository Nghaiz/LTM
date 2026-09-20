# P24 — A* is not the frame hitch, and the 395 lines are not a divergence

Investigation report, 2026-09-20. Phase: [`phases/phase-p24-astar-hitch.md`](../phases/phase-p24-astar-hitch.md).

**Negative result, and the phase closes on it.** A* Pathfinding costs the main thread
**0.17 % of wall clock on Dustbowl and 0.16 % on Island** in a 32-bot match. The six files that
look like 395 lines of divergence from the recovered Ravenfield build contain **zero** unexplained
changes: 10 lines are a required Unity API migration and the other 357 are two decompilers
printing the same IL differently.

The phase plan allowed for this ending in §1 and §4.4, and asked that a negative result state what
was measured. §4 of this report does that.

---

## 1. What was measured, and on what

`Ironfront_Reborn/Assets/Editor/RecoveredPort/AstarHitchProbe.cs`, driven twice — once per map.
Raw output: [`tools/recovered/astar-hitch.p24.json`](../../tools/recovered/astar-hitch.p24.json).

| | |
|---|---|
| Engine / A* | Unity 6000.3.21f1 Editor Play Mode · A* Pathfinding Project **3.8.1** |
| Role | `NetRole.Server` — the only role that spawns bots, and so the only one that pathfinds |
| Population | **32 AI controllers, 33 actors**, the full `team0Bots 16 + team1Bots 16` roster |
| Window | **3600 frames** (~27.5 s), opening on the frame the roster filled |
| Threads | 6 pathfinding worker threads (`threadCount: AutomaticLowLoad`) |
| vsync | forced off; a frame-time percentile against a vsync pin measures the pin |

**The load-bearing number is not frame time.** P23 threw away a 4.89 → 3.64 ms "win" that turned
out to be run-to-run noise, and Editor Play Mode frame time is indicative at best. So the probe
times **`AstarPath.Update` itself** — the component is disabled and the probe calls its private
`Update()` by reflection with a stopwatch around it, so the work happens once per frame exactly as
before, just later in the frame. Moving *when* the work runs does not change *how long* it takes,
and that duration is the only quantity claimed here. The reflection call is inside the measurement,
so every figure below is an over-estimate.

### The denominator guard, which is why this run means anything

"A* was cheap" is worthless if A* was idle, so the probe hooks `AstarPath.OnPathPreSearch` /
`OnPathPostSearch` (public statics, no library edit) and refuses to call a run valid without
completed paths. That guard earned its keep twice:

- **The first smoke run sampled after a 600-frame warmup** and caught 7 paths in 14 s. The one
  moment A* is guaranteed to be busy — 32 bots each requesting a first path — had happened
  *during the warmup*. The warmup is gone; the window now opens on roster fill.
- **Island came up as `NetRole.Client` and the run was marked INVALID**, with the reason written
  into the JSON. A client spawns no bots, so it would have reported a beautifully cheap idle
  library. See §5.

## 2. The measurement

| | Dustbowl | Island |
|---|---|---|
| Sample frames / seconds | 3600 / 27.43 | 3600 / 27.74 |
| Paths completed | 46 | 60 |
| Paths/sec, mean | 1.68 | 2.16 |
| **Paths/sec, busiest single second** | **15** | **20** |
| Max paths in flight | 4 | 5 |
| Path errors | 0 | 0 |
| Path duration mean / max *(worker thread)* | 1.84 / 27.84 ms | 0.76 / 4.82 ms |
| **`AstarPath.Update` mean** | **0.0131 ms** | **0.0126 ms** |
| `AstarPath.Update` p50 / p95 / p99 | 0.010 / 0.017 / 0.025 ms | 0.010 / 0.016 / 0.027 ms |
| **`AstarPath.Update` max** | **5.089 ms** | **4.531 ms** |
| `AstarPath.Update` total over the window | 46.997 ms | 45.429 ms |
| **Share of wall clock** | **0.17 %** | **0.16 %** |
| Frame ms mean / p99 / max *(indicative)* | 7.62 / 11.31 / 167.1 | 7.71 / 11.52 / 158.8 |
| Frames over 33.3 ms | 3 | 3 |

Forty-seven milliseconds of main-thread pathfinding in twenty-seven and a half seconds. The
median frame spends **ten microseconds** in A*.

### The spikes, and what is in them

Three frames per map crossed 33.3 ms. The probe records the A* cost *inside those specific
frames*, which is the question — a 160 ms frame whose `AstarPath.Update` took 0.01 ms did not come
out of pathfinding.

| | Dustbowl | Island |
|---|---|---|
| Spike frames | 3 | 3 |
| Their total frame time | 250.22 ms | 236.22 ms |
| **A* inside them** | **5.11 ms (2.0 %)** | **0.03 ms (0.01 %)** |

**Island's answer is unambiguous: 0.03 ms of 236 ms.** Whatever those frames were, they were not
A*, and this report does not claim to know what they were.

**Dustbowl's is worth stating precisely rather than rounding to zero.** Essentially all of its
5.11 ms is one frame — frame 58, `astarUpdateMs 5.089` inside a `frameMs 36.97` — and frame 58 is
about a second into the window, i.e. at roster fill, the same moment the busiest-second counter
reads 15 paths. So: **once per match, when 32 bots are released and all ask for a first path at
once, A* costs about 5 ms on a single frame, and that frame ran long.** That is consistent with a
burst of path-completion callbacks landing together — `ReturnPaths` time-slices at 1 ms but only
checks the clock after every 5 paths — though this run does not isolate the cause and the report
will not assert one.

One 37 ms frame at match start is not the recurring in-match stutter this phase is about. It is
also not literally nothing, and the table above says so rather than reporting a clean zero.

## 3. Why the load is this small — the structure behind the number

The measurement is the evidence; this is the mechanism that makes it unsurprising. Every claim
here is a grep over `Ironfront_Reborn/Assets` or a read of the live Editor.

**Only one thing in the project ever requests a path.** `AiActorController.Goto` →
`seeker.StartPath` is the sole live call site. Every other consumer shipped with the asset package
— `AIPath`, `RichAI`, `AILerp`, `AIFollow`, `LocalSpaceRichAI`, `ActorSpawner`, `MineBotAI`,
`RVOExampleAgent`, `PathTypesDemo` — is **referenced by no scene and no prefab**. `AiActorController`
lives on `Ai Character Optimizations.prefab` and `Ai Character Optimizations 1.prefab`, and nothing
else.

**A client requests zero paths.** `ActorManager.SpawnWave` returns early on
`NetContext.IsClient`, so a client spawns no bots at all — the fix from `63fb18a`, whose own
comment records the 2026-09-04 playtest where each client *was* simulating a private war of ~40 AI
actors. Remote bodies are `Remote Actor Proxy.prefab`, which carries exactly one script,
`RemoteActorView`: no `Seeker`, no `AiActorController`. `Player Fps Actor` does carry a `Seeker`,
but a `Seeker` alone never starts a path and nothing calls `StartPath` on it.

So on the machine where a player actually sees frames, A*'s entire contribution is deserializing
the cached graphs at scene load: **53.9 ms on Dustbowl, 32.3 ms on Island**, once. That is a
loading cost, not a hitch.

**`Goto` refuses to repath unless the goal has moved more than 2 m**, and `calculatingPath`
blocks re-entry — which is why 32 bots produce 1.7–2.2 paths per second rather than 32.

**The graphs are never scanned at runtime.** Both scenes ship `scanOnStartup: 0`,
`cacheStartup: 1` and a `*_GraphCache` asset, so `Scan()` never runs — and with it neither does
`Voxelize` nor `RecastGraph.ScanInternal`, which is where two of the six divergent files live
entirely. Verified live: Dustbowl loads 4 graphs (Infantry RecastGraph 8 804 nodes, Boat 0, Car
6 319, PointGraph 0), Island loads 3.

**`ProceduralGridMover` is referenced by nothing** — not a scene, not a prefab, not an asset,
anywhere under `Assets/`. §3.1's fourth question, *"do spikes line up with ProceduralGridMover
updating the grid?"*, has no answer because the component never runs. It also only works on a
`GridGraph`, and every graph here is a `RecastGraph` or a `PointGraph`.

**Nothing else is expensive on the main thread either.** `logPathResults: OnlyErrors`, so no
per-path string building. `threadCount: AutomaticLowLoad` resolves to 6 worker threads, so search
never touches the main thread. `euclideanEmbedding.mode: Random` is live and `RecalculateCosts`
does run once from `Awake`'s `dirty = true` — it self-heals a null pivot set — but that is one
event at startup, before the measured window.

## 4. Scope of the negative result

Per `rules/negative-result-scope.md`, the claim is a claim about this search. Stated plainly:

**Measured:** Dustbowl and Island, 32 bots plus 1 actor each, `NetRole.Server`, 3600 frames
(~27.5 s) per map beginning at roster fill, Unity 6000.3.21f1 Editor Play Mode, A* 3.8.1, 6
pathfinding threads, vsync off, one machine, 2026-09-20.

**Not measured, and therefore not claimed:**

- **A built player.** This is the Editor. P25's lesson — measure from the shipped build — applies.
  The counter-argument is the size of the gap: at a 0.0131 ms mean, A* would have to get **76×
  more expensive** to cost even 1 ms per frame, and **~2 500× more expensive** to account for a
  33 ms one.
- **A real client with a human in it.** No client run happened, because a client cannot pathfind
  (§3). That is a structural argument backed by greps, not a measurement.
- **A headless dedicated server under real load.** The Editor server had no connected players.
- **Longer than 27.5 s, or a full match.** A rare event outside a 27.5 s window would not appear.
  The one event known to be bursty — roster fill — is deliberately inside it.
- **Whatever caused the 167 ms and 159 ms frames.** Not identified. Only excluded from A*.
- **The other 92 files** that differ from the recovered build. That is [P27](../phases/phase-p27-logic-triage.md).

## 5. The 395 lines — classified, and the plan's taxonomy was missing a bucket

Reproducible: `python tools/classify_astar_diff.py` (needs `tmp/recovered/` from
`tools/extract_recovered.py`). Output:
[`tools/recovered/astar-diff-classification.json`](../../tools/recovered/astar-diff-classification.json).

| File | changed | (a) Ironfront | (b) Unity API | (c) decomp **loss** | (d) decompiler **rendering** |
|---|---|---|---|---|---|
| `AstarPath.cs` | 107 | 0 | 0 | **0** | 107 |
| `ProceduralGridMover.cs` | 92 | 0 | 0 | **0** | 92 |
| `EuclideanEmbedding.cs` | 60 | 0 | 0 | **0** | 60 |
| `RecastGraph.cs` | 48 | 0 | **10** | **0** | 38 |
| `Voxelize.cs` | 44 | 0 | 0 | **0** | 44 |
| `PathUtilities.cs` | 16 | 0 | 0 | **0** | 16 |
| **Total** | **367** | **0** | **10** | **0** | **357** |

*(367, not the plan's 395 — this is a whitespace-insensitive `diff -u` counting changed lines.
The plan counted differently; the populations are the same six files.)*

### (d) is a bucket the plan does not have, and it holds 97 % of the diff

Both trees are **decompiled from assemblies**, and not by the same tool at the same settings. The
"divergence" is largely a diff of two printers:

| Original | Ours | Same IL? |
|---|---|---|
| `for (int num = 0; ...` | `for (int j = 0; ...` | yes — local name |
| `WebsiteDownload,` | `WebsiteDownload = 0,` | yes — implicit enum value |
| `new AstarWorkItem { init = X, update = Y }` | `itm.init = X; itm.update = Y;` | yes — initializer vs field writes |
| `logPathResults != PathLog.None` | `logPathResults != 0` | yes — enum name vs literal |
| `(tagMask >> t) & 1` | `((uint)(tagMask >> t) & (true ? 1u : 0u))` | yes |
| `GetRandom() % n` | `(long)GetRandom() % (long)i` | yes — `uint % int` **already** promotes to `long` |
| `num5++` | `num5 = (ushort)(num5 + 1)` | yes |
| `& -32769` | `& 0xFFFF7FFFu` | yes |
| `//` *(absent)* | `// Discarded unreachable code: IL_0099` | yes — decompiler remark |

Counting those as divergence is counting the decompiler. **Nobody has edited A* for the netcode**:
zero hits for `Ironfront`, `NetContext`, `IsServer`, `#if` or `UNITY_*` across all six diffs.

**[P27](../phases/phase-p27-logic-triage.md) already has this bucket** — its row in the plan reads
*"(a) netcode / (b) migration / (c) mất mát / (d) vô nghĩa"*. P24's taxonomy lost the fourth one
somewhere between the two phases, and it is the bucket that holds 97 % of the diff. P27 is about
to classify 92 more files from the same two decompilers; it should expect the same shape, and
`tools/classify_astar_diff.py` is a starting point rather than a one-off.

### (b) — the 10 lines that are real

All in `RecastGraph.ScanInternal`, all the same migration:

```diff
-  float[,] heights = terrainData.GetHeights(0, 0, terrainData.heightmapWidth, terrainData.heightmapHeight);
+  float[,] heights = terrainData.GetHeights(0, 0, terrainData.heightmapResolution, terrainData.heightmapResolution);
-  int heightmapWidth = terrainData.heightmapWidth;
+  int heightmapWidth = terrainData.heightmapResolution;
```

`TerrainData.heightmapWidth` / `.heightmapHeight` were removed in Unity 2019.3. Required, correct,
**keep** — and it is in scan-time code that never executes at runtime (§3).

### (c) is zero *because the unclassified list is empty*

That distinction matters, so the tool enforces it: any changed line it cannot place is printed by
name and it **exits 1**. (c) being zero is the absence of unclassified lines, not an assertion.

**Mutation-tested rather than trusted.** `Debug.LogWarning("p24 mutation probe");` was inserted
into the real `AstarPath.Update`, and the tool went RED, named that exact line, and exited 1. The
mutation is reverted; `AstarPath.cs` is byte-identical to `develop`.

## 6. The net-role coin flip — **already known as ledger X-10**, not a new finding

> **Corrected 2026-09-21.** This section first called this an "incidental finding" and said both
> bootstraps sit "at execution order 0". Both claims were wrong, and both would send a reader the
> wrong way — see §6.1. The behaviour is real and it did cost this investigation two runs; it is
> just neither undiscovered nor unaddressed.

Island's first two probe runs came up `NetRole.Client` and spawned no bots. The cause, read out of
the live Editor:

```
== Dustbowl                                   == Island
   ServerTickLoop      go='NetServer'            NetClientBootstrap  go='NetClient'
   NetServerBootstrap  go='NetServer'            NetServerBootstrap  go='NetServer'
   NetClientBootstrap  go='NetClient'            ServerTickLoop      go='NetServer'
```

Same components, both active, and **both carry `[DefaultExecutionOrder(-1000)]`** — equal order, so
`Awake` order falls to hierarchy order. And the two bootstraps are mirror images:

- `NetClientBootstrap.Awake`: `if (!NetContext.IsServer) SetRole(Client);`
- `NetServerBootstrap.Awake`: `if (!NetContext.IsClient) SetRole(Server);`

Whichever runs first wins. Dustbowl lists `NetServer` first and becomes a server; Island lists
`NetClient` first and becomes a client.

The probe works around it in memory (it deactivates the `NetClient` **GameObject** — disabling the
*component* is not enough, because Unity runs `Awake` on a disabled component) and never saves the
scene.

### 6.1 Two corrections to what this section first said

**"Both at execution order 0" was a bad reading of the tool, not of the code.** The probe asked
`MonoImporter.GetExecutionOrder`, which reports the **Project Settings → Script Execution Order**
value and returns 0 for "not set there". It does **not** see a `[DefaultExecutionOrder]` attribute.
Both bootstraps carry `[DefaultExecutionOrder(-1000)]`. The conclusion is unchanged — equal order,
so hierarchy order decides — but anyone acting on "they are at 0" would go looking in the wrong
place. Reading an execution order means reading the attribute *and* the project setting.

**Calling it an "incidental finding" was wrong: it is ledger X-10, and it is documented in the
code that fixes it.** `NetRoleBootstrap` exists precisely for this, at
`Assets/Scripts/Net/Shared/NetRoleBootstrap.cs`, and its own remarks describe this exact coin flip
— including that both bootstraps are at -1000 and that `NetClientPresenterGuard.IsPresentable`
latches on the loser, leaving "a dead killfeed, a dead name table and a dead local combat driver
for the rest of its life". It also records why it stayed hidden: lane B declares
`IRONFRONT_LANEB_ROLE` before any scene `Awake`, so **every lane-B run is correct and the shipped
client is not** — a green lane-B run makes this *less* likely to be found, not more.

So the mechanism is already shipped and already announced. Verified live on Island, 2026-09-21:

```
[net] this rendered process declared no role, so whichever of NetClientBootstrap and
NetServerBootstrap Unity Awakes first decides it. ... Set IRONFRONT_ROLE=client (or pass
-ironfront-role=client) on a client build. Offline single-player is unaffected and needs
nothing (ledger X-10).
```

`NetRoleDeclaration.Resolve` returns `Undeclared` for a rendered process with no
`IRONFRONT_ROLE` / `-ironfront-role`, and `NetRoleBootstrap`'s remark says the default is left
alone **deliberately**, because that is what keeps offline single-player and the Editor sandbox
working. What is genuinely open is the product decision it names: *should a rendered process
default to `Client`?* That is a decision, not a missing mechanism — so this phase records it and
does not pick it.


## 7. Verification

| | |
|---|---|
| `tools/ci.ps1 -SkipUnity` | **CI PASSED** — build, dotnet tests, spec, meta, duplicate assemblies, plugin DLL freshness, harness, net layering, diagnostics strip, style, analyzers |
| Unity compile | proven by the live Editor compiling the probe, rather than by a batchmode run that cannot coexist with an open Editor |
| Unity EditMode suite | **198 / 198**, 0 failed, 0 skipped — exactly P23's baseline |
| `tools/classify_astar_diff.py` | exit 0 (no unclassified lines); goes RED on a planted change |
| Working tree | no scene, prefab, DLL or runtime source modified — the change is one Editor-only probe, one Python tool, two JSON artifacts |

**The net-layering gate caught a real defect in this branch, and it was mine.** The probe first
declared `enum Phase` and `struct Sample`. Those are predefined-assembly declarations, so the gate
— which matches type *names*, not references — then read pre-existing uses of the words `Phase`
and `Sample` in `Net/Client` and `Net/Diagnostics` as reach-backs across the asmdef boundary. Three
rule violations, none of which `dotnet build` can see. Renamed to `ProbePhase` / `ProbeSample`;
gate green. Confirmed mine by removing the probe and re-running the gate clean.

## 8. Verdict

**A* Pathfinding Project is not the source of the remaining frame hitch.** It costs the main
thread 0.16–0.17 % of wall clock with the full 32-bot roster on both maps, it does no work at all
on a client, and the 367 lines that diverge from the recovered build contain one required Unity API
migration and no unexplained change whatsoever.

Nothing was fixed, because nothing here is broken. Per §1 and §4.4 of the plan, that closes the
phase.

The remaining stutter, if it is still there after P23, is somewhere else — and §4 above says
exactly which rocks this investigation did not look under.
