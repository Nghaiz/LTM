# P26 — the shader the plan called a dummy is not the dummy, and the fix it prescribed would have made things worse

Implementation report, 2026-09-21. Phase: [`phases/phase-p26-visual-fidelity.md`](../phases/phase-p26-visual-fidelity.md).

**Two of the phase's load-bearing claims do not survive measurement, and one of them is a trap.**

`fileID: 45`, which §1 calls a "shader dummy", resolves in the Editor to **`Standard (Specular
setup)`** — a real Unity shader, and the wrong one for these materials. The 71 materials on it are
not rendering a placeholder; they are rendering metallic-workflow maps through the specular
workflow.

The actual dummy is a *different* file with a *different* 40 materials on it.
`Assets/Shader/Shader.shader` is AssetRipper's `//DummyShaderTextExporter` output — Standard's
full 27-property block over a surface body that samples `_MainTex` into Albedo and nothing else.
No normal map, no metallic, no occlusion, no emission. Sandbags, Rock, Sandstone, Soil, Old Wood,
Desert Cliff, both Windmill materials and both Railroad materials were sitting on it.

And that file **declares `Shader "Standard"`**, shadowing Unity's built-in. So `Shader.Find("Standard")`
returns the stub — which is precisely the mechanism §6.1 prescribes. Running the phase as written
would have moved 66 materials off a real-but-wrong shader **onto the stub** and reported success.

The §5 risk gate — the one the phase says to answer before promising anything — answers **yes**,
and for a reason the gate did not anticipate.

---

## 1. The §5 risk gate: all meshes present, and the road was never lost

§5 asks whether the meshes the 144 missing objects reference still exist, and warns that if the
EasyRoads3D road surfaces are gone they cannot be regenerated, because the asset is obfuscated in
the shipped build.

`tools/p26_mesh_gate.py` walks the recovered Unity 5.4 scene, reads each missing object's actual
`MeshFilter.m_Mesh` GUID, and resolves it against `Ironfront_Reborn/Assets` by GUID first, then by
asset basename (the re-import reassigned GUIDs).

| Measure | Result |
|---|---|
| Missing objects | 144, all resolved in the recovered YAML |
| Distinct meshes needed | 8 |
| Meshes **absent** from the project | **0** |
| Objects blocked by an absent mesh | **0** |
| Search scope | `tmp/recovered/.../ExportedProject/Assets` vs `Ironfront_Reborn/Assets`, every `.meta` in both |

The road fear is unfounded, but **not** because the road meshes survived. `road` and all five
`surface` objects carry `m_Mesh: {fileID: 0}` **in the original** — a null mesh — and `road`'s
MeshRenderer is `m_Enabled: 0` there too. They are EasyRoads3D authoring scaffolding that never
held runtime geometry. The visible road surface lives in the `Combined Mesh` assets, which are
present and which P23 already established are the original's baked static batching.

Evidence: [`tools/recovered/mesh-gate.Dustbowl.json`](../../tools/recovered/mesh-gate.Dustbowl.json).

## 2. Three populations, not one

Measured across all 260 materials in the project, by dumping what each one actually resolves to in
the Editor and diffing against the recovered original.

| Count | Current shader | Original | Verdict |
|---|---|---|---|
| 71 | `Standard (Specular setup)` (built-in, fileID 45) | mostly `Standard` | wrong shader — §1's target |
| 40 | `Assets/Shader/Shader.shader` (the stub) | `Standard` ×39, `Nature/SpeedTree` ×1 | **the real dummy** |
| 38 | `EasyRoads3D/EasyRoads3DMarker` | same, 36 of 38 | **not a defect** — faithful |
| 42 | built-in `Standard` | `Standard` | correct |
| ~41 | assorted (see §7) | assorted | real, out of scope |

The marker-38 looked like a defect and is not; it is reported here so nobody re-opens it.

The stub-40 is invisible to a name-based diff, because the stub *is named* "Standard" — comparing
shader names says these 40 materials are fine. Only the resolved asset path distinguishes them.

**Scope decision:** the owner chose to fix both the 71 and the 40 in this phase, and to rename the
stub's declaration rather than delete the file. Two of the 40 (`Railroad Metal`, `Railroad Sleeper`)
are the materials the rebuilt railroad uses, so fixing one population without the other would have
shipped the rebuilt geometry visibly flat.

## 3. What changed

| § | Work | Result |
|---|---|---|
| 6.1 | Re-point the mis-assigned materials | **113** materials: 105 → `Standard`, 3 → `Nature/SpeedTree`, 1 → `Nature/SpeedTree Billboard`, 2 → `Custom/Multiply No Soft`, 1 → `Custom/Flag`, 1 → `UI/Additive` |
| 6.2 | Custom shaders assigned | `Flag` → `Custom/Flag`, `DamageVignette` + `Dark Scope` → `Custom/Multiply No Soft`; `Dollar` was already on `Custom/StandardDoubleSide` |
| — | Kill the name collision | `Assets/Shader/Shader.shader` now declares `Recovered/StandardStub`; `Shader.Find("Standard")` returns the built-in again |
| 6.3 | Rebuild the missing objects | **144** objects, **294** components, 0 problems |
| 6.4 | Restore dropped Cloth | **6** on Dustbowl's `HQ Flag`s; Dustbowl back to the original's 7 |
| 6.5 | Backfill static flags | **4** — `Hanging_Rope`, `Mount`, `Tied_Rope`, `Well`, exactly as predicted |
| 6.6 | Widen the baseline test | Already identity-based; the 4 new GlobalObjectIds are in the regenerated baseline |

**The 25 "missing shaders" of §4 were never missing.** All five shaders the 71 materials need
resolve today: `Standard`, `Nature/SpeedTree` and `Nature/SpeedTree Billboard` are built-in
(fileIDs 46, 14000, 14001, read from the Editor), and `Custom/Multiply No Soft` and `UI/Additive`
are in the project. The 46-vs-21 shader-file gap in §3 is a file-count difference, not an
assignment blocker.

**`Standard` is lossless, confirmed at the source.** §4 asks for the claim to be checked on 5
materials before converting 100. Better was available: built-in `Standard` and the recovered
`Standard` declare the **same 27 properties, byte-identical lists**, read out of Unity's own
`ShaderUtil`. The repair is a text rewrite of one line per file, so the property block is not
re-serialised at all.

## 4. The rebuild restores structure, not appearance

§7.4 asks for `MeshRenderer` on Dustbowl to reach 2,307. It does, exactly. That number is a poor
proxy for what the map looks like, and this is worth stating plainly rather than banking the green.

| Measure | Value |
|---|---|
| Objects rebuilt | 144 |
| …**active in hierarchy** | **16** |
| …**actually rendering** (active + enabled renderer + non-null mesh) | **7** |
| Colliders that can block anything | **2** (`Clay Well/Mount`, `Clay Well/Well`) |

The 116 `Railroad Sleeper(Clone)` objects and their `Container` are `m_IsActive: 0` **in the
original**. The five `Marker000N` are inactive there too. The `surface` objects are inactive and
meshless. So restoring them is faithful — and changes almost nothing on screen.

**The visual win in P26 is the shader work, not the rebuild.** 113 materials changed how they
render; 7 objects appeared.

## 5. Evidence

| Claim | How it was proven |
|---|---|
| 113 materials resolve to the intended shader | `VerifyShaderAssignment` asserts name **and asset path**, 113/113, 0 failures |
| …and the check can fail | Mutation-tested both ways: stub-swap → *"same name, wrong shader"*; name-swap → the name mismatch. A name-only check passes on the stub |
| Nothing is left on either wrong shader | Project-wide sweep: 0 on the stub, 0 on `Standard (Specular setup)` |
| The rebuild injects no NullReferenceException | Every scene reference classified before it reached the Editor: 17 `in-rebuild-set`, 14 `null`, **0 dangling**. `SurfaceScript`'s chained deref walked link by link |
| …and that check can fail | Mutation-tested: dangling `objectScript`, missing parent, parent without `MarkerScript` — all three go red |
| The rebuild is idempotent | Second run: created 0, reused 144, components added 0 |
| Cloth coefficients are the original's | 35 entries, pinned at [12, 13, 29, 30, 31]; settings block byte-identical to the recovered one |
| …and the cloth gate can fail | Mutation-tested: collapsing two targets onto one id reports *"the scene holds 5, expected 6"* and withholds the save |
| Static flags | Dustbowl 1,040 / 1,096, `changed=4`; Island `changed=0` |
| …and the baseline covers the new four | Mutation-tested: clearing `Clay Well/Well` → *"1 LOST the flag"*; flagging a rebuilt sleeper → *"1 GAINED the flag"* |
| EditMode suite | 198 / 198 |

Before/after renders: [`data/p26-shaders/`](data/p26-shaders/) — 12 sphere pairs (left = before,
right = after) plus two in-scene shots.

**What the sphere shots do and do not show.** They are conclusive for the Standard-family swaps:
`Sandbags` beside its stub render is the whole defect in one image — flat, washed out, no falloff
on the left; correct khaki and proper shading on the right. They are **not** conclusive for
`Custom/Flag`: `Flag.mat` carries no `_MainTex` at all, so the shader samples white × white and the
preview is a white ball. All 12 in-scene flag renderers use that material and take their colour
from vertex colour at runtime, so the flag has to be judged in a match, not on a preview sphere.
`in-scene-HQ-Flag.png` shows it hanging correctly and double-sided, which is what `Cull Off` in the
rewritten shader is for.

## 6. A gate that reported success while doing one sixth of the work

Worth recording, because it is the failure mode this repo keeps a rule about.

`RestoreClothComponents` first ran with a `(name, localPosition)` match key — the same key P22 and
P23 use, and the key that is correct for the 144-object rebuild, where it was **measured** unique.
It was never measured here. All six `HQ Flag` objects sit at `(0.00, -0.60, -0.04)` under their own
`Flag Parent`, so all six targets matched the same object: the first got a Cloth, and the other
five reported "already present".

The report read `targets 6, restored 1, already present 5, refused 0, problems 0`. Clean. The scene
had two Cloths where it needed seven.

Three changes, not one:
- the key is now the scene-local file id;
- the spec tool asserts the chosen key separates the targets, and records that `localPosition` does not;
- the script **re-counts the components in the scene** and refuses to save when the total disagrees
  with the target count. Counting outcomes is not counting what you changed.

## 7. Not done, and why

**~41 further materials are on the wrong shader.** Same 2017-upgrade damage, different population:
`Legacy Shaders/Particles/Additive` where the original used `Particles/Additive` (8), `Nature/Tree
Creator Leaves Fast` where it used `Nature/SpeedTree` (8+), `UI/Lit/Refraction` where it used
`Decal/AlphaBlend` (2), and about twenty more. P26 does not scope them, several have ambiguous
duplicate-name originals across `Material`/`Material 2`/`Material3`, and the owner scoped this phase
to the two dummy populations. The full list is in the session record; it wants its own phase.

**56 static flags remain unplaced on Dustbowl** (1,040 of 1,096). They are same-named siblings under
a common parent that no match key separates. P23 said so; P26 does not claim them.

**82 serialized fields were skipped on the rebuilt objects**, all of them arrays: obfuscated
EasyRoads3D list state and the Cloth coefficient/collider arrays on the one rebuilt cloth. Each is
reported by name and count. They are editor-time authoring state on objects that are inactive at
runtime.

**§7.6 lane-B and §7.8 owner playtest are not done here.** Lane-B runs a built player, and this
phase changed only assets, so it needs a fresh build to exercise anything. The risk it guards is
small and now measured: two new colliders, both at the Clay Well, both faithful to the original.

## 8. Reproducing

```bash
# ground truth (needs tmp/recovered, 191 MB, gitignored)
python tools/extract_recovered.py          # material map now covers BOTH dummy populations
python tools/p26_mesh_gate.py              # the §5 risk gate
python tools/p26_rebuild_spec.py           # 144 objects + reference classification
python tools/p26_cloth_spec.py             # the six dropped Cloth components

# apply (the material rewrite needs no Unity)
python tools/p26_assign_shaders.py --dry-run
python tools/p26_assign_shaders.py

# in the Editor, under Ironfront > Recovered Port
#   Verify Shader Assignment
#   Rebuild Missing Objects (Dustbowl)
#   Restore Cloth Components (Dustbowl)
#   Restore Static Flags (all scenes)
#   Shader Before-After Shots
```

Every one of those is idempotent, and every one refuses to report success over an empty input.
