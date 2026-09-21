# The recovered Ravenfield build — baseline, and what was actually measured

**Audience:** whoever runs the port-back phases (P23 static flags, P26 shaders and geometry), and
anyone who later wonders where these numbers came from.

The owner reverse-engineered the shipped Ravenfield Beta 5 Windows build back into a Unity
**5.4.0f3** project. It is the only reference we have for what `Ironfront_Reborn` is supposed to
look like. It lives at `tmp/recovered/` — 191 MB, and **`tmp/` is gitignored**, so a single
`git clean` destroys it.

This document plus `tools/recovered/*.json` are what survive that. Once both exist,
`tmp/recovered/` can be deleted without losing anything the port-back needs.

---

## 1. How to read this document

Every factual row below is tagged. The distinction matters, because the recovered build ships
four `.md` files of its own and they are the author's notes, not independently checked results.

| Tag | Meaning |
|---|---|
| **measured** | Re-derived on 2026-09-20 by reading both asset trees directly. Reproducible with `python tools/extract_recovered.py --summary`. |
| **claimed** | Asserted by the recovered build's own docs (`README.md`, `SHADERS_TODO.md`, `SO_SANH_VOI_REPO_CONG_KHAI.md`, `STATIC_OBJECTS.md`). Plausible and internally consistent — **not** verified here. |

Do not promote a *claimed* row to a decision without checking it first.

## 2. Regenerating the data

```
python tools/extract_recovered.py             # writes tools/recovered/*.json
python tools/extract_recovered.py --summary   # prints the tables in section 4, writes nothing
```

The script reads `tmp/recovered/` and `Ironfront_Reborn/Assets/`. It refuses to run when the
recovered build is absent rather than emitting empty files, and it asserts every count in
section 4 before it writes anything — a mismatch means the recovered build changed or the parser
broke, and either way the old JSON is left alone.

Nothing else in the repo reads `tmp/recovered/`. `tools/ci.ps1` does not reference it, and the
five JSON files are committed, so every later phase works from the repo alone.

| File | Holds |
|---|---|
| `tools/recovered/static-flags.Dustbowl.json` | 1,096 static-flagged objects: name, `parentPath`, full `path`, `localPos`, flag bits |
| `tools/recovered/static-flags.Island.json` | the same for Island's 345 |
| `tools/recovered/material-shader-map.json` | 71 materials that lost their shader, each with the shader the original assigned |
| `tools/recovered/missing-objects.Dustbowl.json` | 144 absent objects with components and transforms, plus the displaced and added populations |
| `tools/recovered/scene-baseline.json` | per-scene GameObject / MeshRenderer / static counts, both sides |

Both `parentPath` (ancestors only) and `path` (including the object) are emitted, because 913 of
Dustbowl's 1,096 static objects share a name with something else and cannot be addressed by name
alone.

## 3. What the recovery achieved — *claimed*

From the recovered build's `README.md`. None of this was re-verified here; it is recorded so the
port-back knows what the reference is supposed to contain.

| Item | Claim |
|---|---|
| `Assembly-CSharp` | 408 files / 71,568 lines; **5,631 / 5,631** methods reproduced, verified at IL metadata level |
| `Assembly-CSharp-firstpass` | 112 files / 9,860 lines; 655 / 659 methods (the 4 are compiler-generated empty `.cctor`s) |
| User-defined types | 698 / 698, byte-identical type set vs the shipped DLLs |
| Broken asset references | **0** — every `m_Script` GUID in every scene and prefab resolves |
| Not recoverable | comments, local variable names, and **all shader CG bodies** (only compiled d3d9/d3d11 bytecode ships) |

The phase plan's two negative results — *zero unresolvable `m_Script` GUIDs* and *only 8 dropped
components (6 `Cloth`, 2 `GUILayer`)* across `Ironfront_Reborn/Assets` — are likewise **claimed**,
not re-checked in this phase. They are worth trusting far enough to stop digging there, and worth
re-running before anything expensive rests on them.

## 4. Scene counts — *measured*

Read directly off both `.unity` files on 2026-09-20.

| Scene | GameObjects (original → ours) | MeshRenderers | Static-flagged |
|---|---|---|---|
| Dustbowl | 5,587 → 5,465 (**−122**) | 2,307 → 2,173 (**−134**) | 1,096 → **0** |
| Island | 2,584 → 2,602 (+18) | 829 → 829 | 345 → **1** |
| Menu | 362 → 879 (+517) | 22 → 22 | 0 → 0 |
| Splash | 62 → 62 | 6 → 6 | 0 → 0 |

The static-flag column is the headline. Every batching flag on both playable maps is gone, which
is the stutter: each of those meshes is now its own draw call. Menu's +517 and Island's +18 are
Ironfront's own additions, not damage.

## 5. Matching an object across the two projects — *measured*

Every later phase has to answer "which object over here is that object over there". This is the
evidence for how, measured against the static-flagged population.

| Key | Dustbowl (of 1,096) | Island (of 345) |
|---|---|---|
| `fileID` | **0 unique**, 1,096 unmatched | 1 unique, 344 unmatched |
| `m_Name` | 179 unique, 913 ambiguous, 4 unmatched | 155 unique, 190 ambiguous, 0 unmatched |
| **`(m_Name, m_LocalPosition)`** | **792 unique**, 63 ambiguous, 241 unmatched | **341 unique**, 0 ambiguous, 4 unmatched |

**`fileID` is useless.** 2,183 fileIDs do coincide between the two Dustbowl scenes, but not one of
them belongs to a static object — they are probes, lights and managers that kept their Unity 5.4
ids. Every piece of geometry was reissued when the project was upgraded to 2017.3.

Use `(m_Name, m_LocalPosition)`, rounded — the two engines re-serialise floats differently. Fall
back to `parentPath` for the 63 that remain ambiguous. The 241 unmatched are the lost geometry of
section 7, not a keying problem.

> Two corrections to `plans/phases/phase-p22-recovered-ground-truth.md` §3, which this supersedes:
> Dustbowl's ambiguous count is **63, not 55** (792 + 55 + 241 = 1,088, which is 8 short of the
> 1,096 total), and Island's is **190, not 189** (155 + 189 = 344, one short of 345). The figures
> here sum correctly and come out of the same code that writes the JSON.

## 6. Two YAML dialect traps — *measured*

The two projects serialise the same structures differently. **A parser written for one dialect
returns empty on the other, with no error and no warning**, which is how a survey measurement
during this work produced an empty component list and nearly became a published finding.

**Trap 1 — component lists.**

```yaml
# original (Unity 5.4)          # ours (Unity 6)
m_Component:                    m_Component:
- 4: {fileID: 6419}             - component: {fileID: 6419}
- 215: {fileID: 18473}
```

**Trap 2 — material properties.** Found while writing this phase, and it bit exactly as
predicted: the extractor's first version read the Unity 6 form only, returned zero properties for
all 71 original materials, and reported a "mismatch" on 56 of them that had nothing to do with the
materials.

```yaml
# original (Unity 5.4)          # ours (Unity 6)
- first:                        - _BumpMap:
    name: _BumpMap                  m_Texture: {fileID: 0}
  second:
    m_Texture: {fileID: 0}
```

`tools/extract_recovered.py` handles both and **asserts** that both stay reachable: an all-empty
component parse or an all-empty original-property parse is a hard failure, not a zero. Any new
parser on this track owes the same two guards.

## 7. What Dustbowl is actually missing — *measured*

The GameObject delta is −122, and that number is a **net figure, not a population**. Rebuilding
"122 objects" would rebuild the wrong set.

| | Count |
|---|---|
| Objects in the original with no counterpart here at all | **144** (across 23 names) |
| Objects here that the original never had | 22 |
| Net | **−122** |
| Present but at a different local position | 291 |

The 144 absent, by name — 116 `Railroad Sleeper(Clone)`, 5 `surface`, 3 `Container`, and 20
singles (`Clay Well`, `Hanging_Rope`, `Tied_Rope`, `Mount`, `Well`, `Marker0003`, `Side Objects`,
…). That is the EasyRoads3D railway and its fittings, matching the recovered docs' account. Only
**3** of the 144 have a surviving parent, so a rebuild attaches three subtrees rather than 144
loose objects.

The 22 additions are ours and must not be "restored away": `NetServer`, `NetClient`,
`CapturePoint`, `Controllers`, the `Explosion FX (Grenade)` and `Explosion FX (Rocket)` prefab
trees, two extra `Checkpoint Light`s, `Objects`, `Objects (1)`, `Static Props 1`.

The 291 displaced are a different repair from the 144 absent — present, just somewhere else — and
are carried separately in the JSON under `displaced`.

> `absentFromOurScene` is cross-checked against a name-frequency deficit computed with no position
> matching at all. The obvious check — "absent minus added must equal the raw GameObject delta" —
> was written first and is worthless: it is an algebraic identity that holds for any matching,
> good or broken, and a mutation that disabled half the matcher left it green.

## 8. Materials and shaders — *measured*

**255** materials here against **193** in the original; **71** of ours point at built-in
`fileID: 45`, having lost their shader in the 2017 upgrade. The original assigned 191 of its 193
to a real shader file, so the mapping is recoverable for all 71:

| Shader the original assigned | Materials |
|---|---|
| `Standard` | 66 |
| `Nature/SpeedTree` | 2 |
| `Nature/SpeedTree Billboard` | 1 |
| `Custom/Multiply No Soft` | 1 |
| `UI/Additive` | 1 |

Six of the 71 are AssetRipper duplicates (`Red 1.mat` for `Red.mat`) with no same-named original;
they resolve by stripping the suffix, and the JSON labels those rows `matchedVia:
"duplicate-suffix-stripped"` so the inference is visible rather than hidden.

**Re-pointing the shader is lossless for 69 of 71.** Our materials carry a *superset* of the
original's properties (the newer `Standard` serialises `_SpecColor` and `_SpecGlossMap` on
everything), so strict equality is the wrong test and reads False on all 71. What matters is the
other direction — a property the original had that we do not — and that happens on exactly two:
`Branches_0_1` and `Fronds_2_1` lose `_Cull`, `_DetailTex`, `_HueVariation` and `_Shininess`.
Those two need their values restored, not merely re-pointed; the JSON lists them under
`needsPropertyRestore`.

Corroborating the diagnosis: several of the 71 carry properties belonging to shaders they were
never assigned — `Desert Bridge Roof` and `Material_002_0` hold `_AlphaTex` / `_ColorMask` /
`_EnableExternalAlpha` (`Sprites/Default`), `Desert Facility Concrete` holds `_Focus` /
`_MainBump` / `_Mask`. The upgrade did not lose one mapping; it scattered several.

### The three re-authored shaders — *measured*

The build ships only compiled bytecode, so no tool returns the original CG. What *was* recovered
exactly is each shader's properties, tags, passes, blend mode and render state. Three custom
Ravenfield shaders were re-authored by hand from that render state and now live in the repo,
replacing AssetRipper's `//DummyShaderTextExporter` placeholders:

| File | Declares | Used by |
|---|---|---|
| `Ironfront_Reborn/Assets/Shader/Flag.shader` | `Custom/Flag` | `Flag` |
| `Ironfront_Reborn/Assets/Shader/MultiplyNoSoft.shader` | `Custom/Multiply No Soft` | `DamageVignette`, `Dark Scope` |
| `Ironfront_Reborn/Assets/Shader/StandardDoubleSide.shader` | `Custom/StandardDoubleSide` | `Dollar` |

Each `.meta` was left untouched, so the GUIDs are unchanged and the materials already pointing at
them keep working — the bodies change, the wiring does not.

**They compile.** Verified in Unity 6000.3.21f1 batchmode, not inferred: all three report
`ShaderUtil.ShaderHasError == false` with zero messages. The two dependencies that a 5.4-era
shader could plausibly have lost both still ship — `UnityBuiltin3xTreeLibrary.cginc` (with
`LeafSurfaceOutput` and `LightingTreeLeaf`, which `Custom/Flag` needs) and the deprecated
`UNITY_MATRIX_MVP` (which `Custom/Multiply No Soft` uses), defined in
`UnityShaderVariables.cginc`. The check itself was mutation-tested: an undeclared identifier
planted in `Flag.shader` made it report `ERROR ... undeclared identifier` and exit non-zero.

The remaining 42 shaders in the recovered build are Unity built-in placeholders — fixed by
re-picking the built-in from the material's Shader dropdown, which is what
`material-shader-map.json` enumerates. The 100 `Standard` materials switch losslessly: the 27
property names they serialise were diffed against the real `Standard` property list and match
exactly (*claimed* — and independently consistent with the 27 properties this phase parsed out of
`Asphalt.mat`).

## 9. Why the engine version is the deeper problem — *claimed*

From `SO_SANH_VOI_REPO_CONG_KHAI.md`, comparing the recovered build against the public
`JonJon565/Ravenfield_Beta_5_Decomp` repo that `Ironfront_Reborn` descends from. **Not verified
here**, and recorded because it bounds what the port-back can achieve.

The C# is equivalent — 263 of 322 files byte-identical, the remaining 59 differing only in lambda
naming, `delegate` vs `=>`, local numbering and indentation. `AiActorController.cs` differs by 39
lines with no semantic change. Physics layers, the collision matrix, `TimeManager`,
`QualitySettings` and all 7 script execution orders match.

What differs is the engine. The public repo was genuinely upgraded to **2017.3.0f3** (it carries a
`Library/`, `RenderSettings: serializedVersion: 8` against 5.4's `7`, and 2017-style long
fileIDs). Ravenfield is a ragdoll game: every character is a `ConfigurableJoint` chain driven
through `slerpDrive`, and `ActiveRaggy.cs:104` sets `jointDrive.mode = JointDriveMode.Position`
— identical in both copies. Unity 5.5 moved PhysX 3.3 → 3.4 and **dropped `JointDriveMode`**
(3.4 always applies position and velocity drive together). The line still compiles on 2017.3 and
later and simply does nothing.

If that account holds, restoring static flags, shaders and geometry fixes the *stutter* and the
*visuals*, and the ragdoll feel stays wrong for a reason no code change reaches. Worth confirming
before anyone budgets time against "the ragdoll bug".

## 10. Provenance

Merged from the four documents inside `tmp/recovered/` — `README.md`, `SHADERS_TODO.md`,
`SO_SANH_VOI_REPO_CONG_KHAI.md`, `STATIC_OBJECTS.md` — plus the measurements of
`plans/phases/phase-p22-recovered-ground-truth.md`, all re-derived here.

`STATIC_OBJECTS.md`'s per-name tables (826 `RockDesert`, 21 `Clay Hut Base`, …) are deliberately
**not** reproduced. They are the JSON's contents, and one copy of a fact is enough.
