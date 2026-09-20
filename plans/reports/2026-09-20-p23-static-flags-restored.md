# P23 — the static flags are back, and the reason they were going to help is wrong

Implementation report, 2026-09-20. Phase: [`phases/phase-p23-static-batching.md`](../phases/phase-p23-static-batching.md).

The flags are restored and pinned by tests. The measurement that was supposed to show the win
shows nothing, and the investigation into why turned up something the phase plan did not know:
**the original build's static batching is already baked into our scenes and has been all along.**

---

## 1. What landed

| | Dustbowl | Island |
|---|---|---|
| Recovered entries | 1 096 | 345 |
| **Flags placed** | **1 036** | **343** |
| stage 1 — `(name, localPos)` | 800 | 342 |
| stage 1 + hierarchy path tie-break | 52 | 0 |
| stage 2 — name alone | 184 | 1 |
| Refused by the guard | 0 | 0 |
| Ambiguous — named, not guessed | 56 | 1 |
| Absent from the scene | 4 | 1 |
| Already flagged before the run | 0 | 1 |

Island therefore ends at 344 `BatchingStatic` objects, not 343. The one that was already flagged
is carried in the baseline as `pre-existing` and is what stops the test's "no stranger is
flagged" direction from firing on scenery nobody touched.

The plan predicted 963 and 343. Dustbowl came out 73 higher because of a tie-break the YAML
survey could not perform: our scenes wrap whole groups under an `Objects (1)` root, so the
original's `Clay Hut Balanced Windows (3)/Clay Hut Base` is our
`Objects (1)/Clay Hut Balanced Windows (3)/Clay Hut Base`. Twenty huts each carry a child of the
same name at the same local position; a **suffix** match on the path singles each one out
exactly. That recovered 52.

The other 21 (stage 1 800 against the survey's 792, stage 2 184 against 171) come from the
Editor working on the resolved hierarchy rather than on the scene YAML: prefab-instance children
exist in one and not the other, and the pools differ accordingly. They are not a separate
mechanism worth a name.

Stage 2 is the weaker claim — name alone, believing that a uniquely-named leftover is the same
object — so it is counted separately and it was checked rather than assumed. All 184 of
Dustbowl's stage-2 matches turn out to be one population: objects the original held at the scene
root and we hold under `Objects (1)`, each with a name no other object in the scene shares
(`Clay Hut Balanced Windows (13)`, `Barrier Open (1)`, and so on). The same reparenting that
moved their local position off the stage-1 key is what left their name unique. That is exactly
the `displaced` population P22's census recorded, arriving from a completely different direction.

**Everything not placed is named, not counted:**

- Dustbowl ambiguous — all 56 are `RockDesert`, siblings sharing one parent and one path. Nothing
  in the data tells them apart. This is the population the plan already expected to lose.
- Dustbowl absent — `Hanging_Rope`, `Mount`, `Tied_Rope`, `Well`, all under `Clay Well`. Exactly
  the four the plan said [P26](../phases/phase-p26-visual-fidelity.md) owns.
- Island ambiguous — one `Vehicle Spawner (6)`; absent — one `Dirt Barrier (2)`. P22's
  missing-object census only covered Dustbowl, so Island's single absentee is new information.

Every one of the 1 379 placed flags is on an object that has a Renderer. None is inert.

The scene diff is 1 379 lines and nothing else: `m_StaticEditorFlags: 0` → `127` (Everything, as
this Unity version defines it), which is the faithful translation of the 5.4 `4294967295`.

## 2. The guard refused nothing, and that is the right answer

The guard skips anything a `Rigidbody`, `Animator`, `Animation`, `ParticleSystem`, `LineRenderer`,
`TrailRenderer` or an `Ironfront.Net*` behaviour can move — on the object or on any ancestor,
because those move a whole subtree.

It refused **zero** of the objects in the recovered static set. That is not a dead guard:

- Across the whole of Dustbowl it refuses **15** objects (13 ParticleSystem, plus `ServerTickLoop`
  and `NetClientBootstrap`); across Island, **34** (32 ParticleSystem and the same two).
- Neither scene contains a single `Rigidbody`. The vehicles are spawned from prefabs at runtime,
  so that clause could not fire here however correct it is — which is exactly why it is
  mutation-tested below rather than trusted.

## 3. The tests, and proof they can fail

`Assets/Tests/EditMode/StaticFlagsBaselineTests.cs` pins the result by `GlobalObjectId`, not by
count, and asserts **both** directions in one test: flags that vanished and flags that appeared.
A second test asserts no batched object carries a moving component — the guard's leash, which is
what would catch someone later adding a `Rigidbody` to a prop that is now welded into a mesh.

Three mutations, each run against the real suite:

| Mutation | Result |
|---|---|
| Clear `BatchingStatic` on `Static Props (1)/ConcreteBase` | RED — *"1 LOST the flag"*, names the object |
| Flag `Vehicle Spawners/Vehicle Spawner (5)`, which the restore never placed | RED — *"1 GAINED the flag outside the restore"*, names it |
| Add a `Rigidbody` to a flagged object | `GuardReason` flips `<null>` → `Rigidbody`; leash test RED, names it |

All three reverted. Full EditMode suite: **198/198**, which is the 194 baseline plus these four.

## 4. The measurement shows nothing — and the reason matters

Editor Play Mode, four camera poses pinned to the same positions on both sides, 120 sampled
frames each:

| | static-batched renderers | batches | draw calls | setPass | triangles |
|---|---|---|---|---|---|
| Dustbowl before | 1 092 / 2 321 | 1 953 | 5 779 | 1 438 | 2 446 618 |
| Dustbowl after | 1 092 / 2 321 | 1 960 | 5 782 | 1 440 | 2 445 852 |
| Island before | 343 / 622 | 2 383 | 3 777 | 444 | 1 503 624 |
| Island after | 343 / 622 | 2 383 | 3 794 | 446 | 1 503 624 |

Nothing moved. The frame times also differ between the runs (Dustbowl 4.89 → 3.64 ms mean) but
**that is run-to-run noise and is not a result** — a change that moved no rendering count did not
make the renderer faster, and reporting it as a win would be dishonest.

### Why nothing moved

`Dustbowl.unity` already contains, on 1 092 of its 2 173 MeshRenderers, a populated
`m_StaticBatchInfo` with `subMeshCount >= 1`. Resolving what those renderers actually draw:

```
Dustbowl                                            Island
Mesh3/Combined Mesh (root scene) 4.asset     289    Mesh2/Combined Mesh (root scene).asset    262
Mesh3/Combined Mesh (root scene) 2_0.asset   279    Mesh2/Combined Mesh (root scene) 2.asset   62
Mesh3/Combined Mesh (root scene) 3_0.asset   279    Mesh2/Combined Mesh (root scene) 3.asset   19
Mesh3/Combined Mesh (root scene)_0.asset     168                                        ---------
Mesh3/Combined Mesh (root scene) 5.asset      77    drawing a combined mesh                   343
                                       --------    drawing an ordinary per-object mesh         0
drawing a combined mesh                    1 092
drawing an ordinary per-object mesh            0
```

Both counts land exactly on the `staticBatchedRenderers` the probe reported, from the other side:
1 092 and 343.

**The original Ravenfield build's static batching survived the rip.** AssetRipper preserved the
combined meshes and each renderer's `firstSubMesh`/`subMeshCount` index into them. Those objects
have been batched at runtime the entire time, flags or no flags, and Editor Play Mode reuses the
baked data rather than re-deriving it.

So the phase plan's §1 — *"mọi mesh tĩnh trong hai map đang là một draw call riêng"* — **is not
true**. Roughly half of Dustbowl's renderers and more than half of Island's were already
batched. Whatever is causing the stutter, this was not it in the way the plan described.

### The build says the same thing, and clears a risk worth naming

Editor Play Mode reuses baked data, so it could not settle what a real build would do — and there
was a genuine hazard there. If Unity's build-time static batching had re-combined renderers that
already index into a combined mesh, 289 renderers sharing one mesh would have produced a mesh
289 times the size. That is a memory blow-up on shipped content, and it had to be checked rather
than reasoned about.

A full `tools/build-player.ps1` with the flags on, against the build taken this morning without
them:

| | before | after |
|---|---|---|
| `level2` (Island scene) | 4 264 304 | 4 264 304 |
| `level3` (Dustbowl scene) | 2 125 560 | 2 125 560 |
| `sharedassets2.assets` + `.resS` | 16 960 924 / 39 508 288 | 16 960 924 / 39 508 288 |
| `sharedassets3.assets` + `.resS` | 27 128 776 / 47 916 720 | 27 128 776 / 47 916 720 |

Byte-identical. (`sharedassets1.assets` moved 461 132 → 461 148, sixteen bytes in the Menu
scene's shared assets — build metadata, not geometry.)

**No blow-up, and no difference at all**, which is the expected result once you notice that
`m_StaticEditorFlags` is editor-only data: it is never serialized into the player. Only its
*effects* ship — combined meshes, lightmap UVs, occlusion data — and all three were either
already baked or are not baked at all in this project.

#### That green needed a control

A build that finishes in 105 seconds and emits byte-identical output is exactly what a build
serving stale cache looks like, and a comparison against cached output proves nothing at all.
The §5 dedupe supplied the control for free: it deleted 61 assets and rewrote 1 716 references,
so if *that* build had also come out byte-identical, both comparisons were worthless.

It did come out byte-identical — but the build report shows the pipeline genuinely re-ran:

| Build report, "Used Assets" | before dedupe | after dedupe |
|---|---|---|
| from `Assets/Mesh/` | 148 | 192 |
| from `Assets/Mesh2/` | 41 | 9 |
| from `Assets/Mesh3/` | 24 | 12 |
| **total meshes** | **213** | **213** |

`Icosphere_2.asset` is listed at `Assets/Mesh2/` in one and `Assets/Mesh/` in the other, at the
same 11.8 kb. 32 moved out of `Mesh2` and 12 out of `Mesh3` — the 44 merged meshes, exactly —
and 148 + 44 = 192. The build reprocessed and the paths moved; the size did not, because the
swap is one-for-one. So the flags comparison above is measuring a real build, not a cache.

### What the flags are still worth

They are not decoration:

- The baked combining is a frozen artifact of a 2016 build. Nothing added, moved or reparented
  since is in it, and nothing ever will be until the batches are rebuilt from flags.
- A player build re-runs static batching from `BatchingStatic`. With the flags at zero, anything
  that caused a rebuild would have dropped the inherited batching entirely — the scenes were one
  re-save away from losing it.
- `127` restores occluder/occludee, lightmap and reflection-probe authoring state too, all of
  which the upgrade cleared.

## 5. The duplicate folders are mostly not duplicates

`Ironfront > Recovered Port > Audit Duplicate Assets` loads both sides of each pair and compares
what the engine sees — shader identity and every declared property for a material, topology and a
geometry hash for a mesh, decoded pixels for a texture. Byte comparison is useless here: all 120
material and mesh pairs differ as text because the rips use different serialization dialects.

| Folder | identical | differs | no canonical | references |
|---|---|---|---|---|
| `Material 2` | 0 | 26 | 1 | 629 |
| `Material3` | 0 | 24 | 0 | 3 550 |
| `Mesh2` | 32 | 10 | 3 | 1 338 |
| `Mesh3` | 12 | 12 | 5 | 2 094 |
| `Texture2D_2` | 0 | 12 | 0 | 20 |
| `Texture2D_3` | 1 | 15 | 0 | 32 |
| `Images3` | 14 | 2 | 0 | **0** |

- **No material is a duplicate.** Most differ by *shader*: `Material 2/Concrete.mat` is Standard
  (Specular setup) where `Material/Concrete.mat` is Standard. They are two different materials,
  and merging them would change how the game looks. The plan's §3 claim that two identical
  materials under different GUIDs are blocking batching does not hold — there are no identical
  materials.
- **No texture is safely mergeable either.** The source PNGs *are* byte-identical, but the import
  settings are not (`serializedVersion` 4 vs 13, different `maxTextureSize`), so the imported
  textures differ. Choosing a winner is a visual decision, not a cleanup.
- **`Images3` is referenced by nothing at all** — 16 files of dead weight.
- **44 meshes are genuinely identical** — same topology, same bounds, and the same hash over
  every vertex stream. uv2 is in that hash deliberately: P23 just set LightmapStatic on 1 379
  objects, so a "duplicate" that happened to lack lightmap UVs would break a bake silently, and
  a hash that only covers position/normal/uv0 declares meshes identical on the strength of the
  streams it chose to look at.

### The near-miss

`Combined Mesh (root scene)*.asset` are the combined meshes from §4 — five in `Mesh3` that 1 092
Dustbowl renderers draw from, three in `Mesh2` that 343 Island renderers draw from. They appear
in the audit as `no-canonical`, because they have no counterpart in `Mesh/`: they are not
leftovers from a second rip, they are load-bearing. A §6.3 executed as written — *"giữ bản trong
thư mục chuẩn, xoá bản thừa"* — would have deleted `Mesh2` and `Mesh3` and taken half of
Dustbowl's geometry and half of Island's with them.

`tools/dedupe_assets.py` cannot do that: it acts only on pairs the Editor audit called
`identical`, or on assets with zero references, and these are neither.

### What was merged, and how it was checked

61 assets removed: 44 meshes, one texture (`Default-Checker.png`), and all 16 of `Images3`.
1 716 references rewritten — both halves of each `{fileID, guid}` together, because swapping only
the guid leaves a local id the canonical asset does not contain, and Unity resolves that to
nothing at all rather than to an error.

The scene diffs are exclusively `m_Mesh:` lines: 1 715 of them, nothing else. The one remaining
rewrite is `Material3/Gray.mat`'s `_MainTex`.

| Check | Result |
|---|---|
| `dedupe_assets.py --verify` | zero surviving references to anything removed |
| MeshFilters with a null mesh | Dustbowl 0, Island 18 — **and 18 at HEAD too**, pre-existing |
| MeshRenderers with a null material | Dustbowl 1 (`Level Bounds`), unchanged from HEAD |
| Remaining unresolved mesh guids | 247 + 244, all `0000000000000000e000000000000000` — Unity's built-in resources |
| EditMode suite | 198/198 |

It saves nothing at runtime, and the build report is what says so. Static batching groups by
material, so two copies of one mesh under different GUIDs never cost a draw call — and the merge
did not shrink the build either, because the canonical copies were the ones nothing referenced.
The build pulled in 213 meshes before and 213 after; 44 of them simply arrive from `Mesh/` now
instead of `Mesh2/` and `Mesh3/`. `Images3` was referenced by nothing, so it was never in the
build to begin with.

This is repo hygiene: 61 fewer assets to mistake for the real one, and `Images3`'s several
megabytes out of git. It is worth having and it is not a performance change.

## 6. Lane-B: nothing froze

`run-lane-b.ps1 -Scene Dustbowl -Port 27115 -SpawnIndex '3,5'` against the rebuilt player, after
both the flags and the merge. `passed: true`, no failures, no client lost its connection, all
three finished their programme.

| Client | checkpoints | path walked | scoreboard |
|---|---|---|---|
| driver | 29 | 4 916 m | 10 kills / 10 deaths |
| observer-a | 31 | 3 620 m | 10 / 10 |
| observer-b | 30 | 6 220 m | 10 / 10 |

Path length is summed from each client's own `localActor` block rather than from `aim.distanceM`,
which is known to freeze in these records. Kilometres of movement is well clear of the ~500 m the
server drifts an idle body, and the checkpoint series (`approach`, `firing`, `killed`,
`respawn-window`, `scoreboard`) is a real engagement, not a spawn that stood still.

This is the weaker half of the freeze evidence. The stronger half is §4: the player build is
byte-identical with the flags and without, so there is nothing in it that *could* have frozen.

## 7. What is left for the owner

Per the plan's §7.4 and the 2026-09-20 ruling, there is no hard perf threshold and the verdict is
the owner's from the numbers plus a playtest. The numbers say the flags changed no rendering count
in the Editor, for the reason in §4. A playtest via `tools/playtest-local.ps1` is the remaining
evidence, and it is the owner's call.

If the stutter is still there — and §4 suggests it may well be, since the batching the plan
blamed was largely already present — the next suspect is
[P24](../phases/phase-p24-astar-hitch.md), the A* hitch.

## Files

| Path | What |
|---|---|
| `Ironfront_Reborn/Assets/Editor/RecoveredPort/RestoreStaticFlags.cs` | the restore, the guard, the report writer |
| `Ironfront_Reborn/Assets/Editor/RecoveredPort/AuditDuplicateAssets.cs` | semantic duplicate verdicts |
| `Ironfront_Reborn/Assets/Editor/RecoveredPort/ScenePerfProbe.cs` | Play Mode census, pinned cameras |
| `Ironfront_Reborn/Assets/Tests/EditMode/StaticFlagsBaselineTests.cs` | the identity baseline, both directions |
| `tools/measure-scene-perf.ps1` | wrapper; `-Compare before,after` prints the delta table |
| `tools/dedupe_assets.py` | reference counts, plan, apply, verify |
| `tools/recovered/static-flags-applied.*.json` | the pinned baseline — regenerate, never hand-edit |
| `tools/recovered/duplicate-assets.json` | the audit verdicts |
| `tools/recovered/scene-perf.{before,after}.json` | the measurement |
