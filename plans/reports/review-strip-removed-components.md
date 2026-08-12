# Review — Unity 6 legacy scene data fixes

Branch `fix/unity6-legacy-scene-data`, uncommitted. Two changes reviewed:
A) `tools/strip-removed-components.ps1` + the GUILayer (`!u!92`) strip across 8 files.
B) `m_LightingDataAsset: {fileID: 0}` in Menu / Island / Dustbowl.

**Verdict: do not revert either change.** Both are correct on the evidence. Two Important
findings are about the *tool* and the *stated rationale*, not about the resulting files.

---

## Important

### 1. The lighting rationale is wrong as stated — the conclusion survives on different evidence

Claim under review: *"`m_Lightmaps` is empty (0 entries) in all four scenes, therefore no scene
consumes baked lightmap data."*

`m_Lightmaps` **does not exist** in these files. The full `LightmapSettings` document
(`Ironfront_Reborn/Assets/Scenes/Island.unity:89608-89658`, `serializedVersion: 11`) contains
`m_GIWorkflowMode`, `m_GISettings`, `m_LightmapEditorSettings`, `m_LightingDataAsset`,
`m_UseShadowmask` — and nothing else. Likewise the renderers carry no lightmap binding:
`Island.unity:5245` (`MeshRenderer &5245`) has `m_ScaleInLightmap` / `m_LightmapParameters` but
**no `m_LightmapIndex` and no `m_LightmapTilingOffset`** — `grep -c m_LightmapIndex` is 0 in all
four scenes.

That is exactly the LightingDataAsset-era layout: the lightmap texture array, the
renderer→lightmap index/tiling mapping, and light-probe coefficients all live *inside* the LDA,
not in the scene YAML. So the absence of `m_Lightmaps` proves nothing at all. Answering the
question directly: **yes, an LDA can supply lightmaps at load time without them appearing in the
scene YAML — that is the normal case.** I verified this by direct inspection of the serialized
form, not by plausibility.

**Why the change is still right** (independently verified):

- **No lightmap textures exist on disk.** `find Ironfront_Reborn -iname 'Lightmap-*'` → 0 hits.
  The scene folders contain only `LightingData.asset` and `ReflectionProbe-N.exr`
  (`Assets/Scenes/Island/`, `Assets/Scenes/Dustbowl/`). No `.gitignore` rule hides them
  (`grep -E 'Lightmap|\.exr|LightingData' .gitignore Ironfront_Reborn/.gitignore` → 0 hits).
  An LDA references lightmap textures by guid; it cannot supply textures that do not exist.
- `m_EnableBakedLightmaps: 0` in Island (`:89619`), Dustbowl (`:78077`), Splash (`:2626`).
  Menu is `1` (`:10636`) but its LDA guid `30be3c86e3fcdc148a12299ad8010898` resolves to no
  `.meta` in the repo — confirmed independently.
- Unity 6 had **already refused** both surviving LDAs (5.4.0f3 / 2017.3.0f3 version strings are
  visible in the binary headers). Reverting would restore a warning, not any lighting.

**Action:** keep the change; correct the commit message. A future reader who trusts
"`m_Lightmaps` was empty" will apply that test to a scene that *does* have baked lighting and
delete it. The sound test is "no `Lightmap-*` textures on disk" + "the editor rejects the LDA".

### 2. `strip-removed-components.ps1` never verifies the owner entry was actually removed

`tools/strip-removed-components.ps1:131-138` looks for
`^\s*-\s*(component|\d+):\s*\{fileID: <id>\}\s*$` and drops it — but nothing asserts a line was
found. Meanwhile `:144-146` prints *"removed class N &id and its owner's list entry"*
unconditionally.

Failure scenario: the single reference that passed the pass-2 check (`:110`) is not an
`m_Component` entry but an ordinary field, e.g. `m_TargetLayer: {fileID: 598}`. Count is 1, the
file is accepted, the block is deleted, pass 3 matches nothing, and the script reports success
while leaving a dangling reference — Unity then logs a *different* error and the tool's output
says it fixed the file.

Fix: record `$drop.Count` before and after the pass-3 loop per block and require exactly
`blockLength + 1`; otherwise skip the file and warn.

Not triggered here — verified 0 dangling references to every removed id, per file
(all 11 ids, `grep -c "fileID: <id>}"` → 0 after the edit).

### 3. The "referenced more than once" check is file-local and format-narrow

`:110` counts `fileID:\s*<id>\}` in `$raw`. That pattern cannot match either of the two forms a
real external reference takes:

- cross-file: `{fileID: 92549472157447402, guid: <prefab-guid>, type: 2}`
- prefab override: `target: {fileID: 92549472157447402, guid: ...}` inside a scene's
  `m_Modifications`, or an entry in `m_RemovedComponents`.

Both are `fileID: N,` (comma) not `fileID: N}`, so the count stays at 1 and the script proceeds.
It also never looks at any file other than the one it is editing. A scene that overrode
`m_Enabled` on a prefab's GUILayer would silently keep a modification pointing at a deleted
object.

The regex is otherwise sound in the direction the brief asked about: `fileID:\s*598\}` cannot
match `fileID: 5980}` (the `}` anchor) and cannot match a longer id, because `fileID:` is a
literal single anchor point.

Not triggered here — I checked all six prefab GUILayer ids repo-wide
(`grep -rl "fileID: <id>" Ironfront_Reborn/Assets`) → **no file at all**, including the owning
prefab, confirming the only reference was the `m_Component` line now removed.

---

## Minor

### 4. Last-block-to-EOF swallows the trailing newline

`:76` splits on `\r?\n`, so a file ending in a newline yields a final empty element. `:98` sets
`End = lines.Count - 1` for an unclosed block, which includes that element, and `:152`
(`$kept -join $eol`) never re-appends a terminator. If a stripped class were the last document in
a file, the file would lose its trailing newline. Not hit here — all 11 blocks were followed by
another document (e.g. `--- !u!104` after Dustbowl's, `--- !u!95` after Menu's).

### 5. `$eol` detection is all-or-nothing

`:75` picks CRLF if the file contains *any* CRLF, then rewrites every line with it. A
mixed-ending file would be normalised wholesale and produce a whole-file diff. Not hit here —
the 8 files are uniformly CRLF and the diff is exactly 88 deletions.

### 6. Orphaned lighting artifacts — leaving them is defensible; I'd delete the LDAs

- The four `ReflectionProbe-*.exr` are referenced by **nothing**: I resolved each `.exr.meta`
  guid and grepped all `.unity`/`.prefab`/`.mat` → 0 hits. The four `ReflectionProbe` components
  (`Dustbowl.unity:78747,78779`, `Island.unity:156437,156469`) are all `m_Mode: 1` (Realtime)
  with `m_RefreshMode: 2`, so they never consume a baked cubemap.
- The two `LightingData.asset` are now referenced by nothing.

Leaving them costs only repo size and is harmless — a future *Generate Lighting* overwrites the
folder anyway. My preference is to delete the two `LightingData.asset` (+ `.meta`) in a
**separate commit**, because leaving a 5.4-era LDA next to the scene invites someone to re-point
the reference at it. The `.exr` files can go with them or stay; either is fine. This is a
judgment call, not a defect.

---

## Verified clean (no finding)

### The GUILayer diff itself

88 deletions, 0 insertions, decomposing exactly: 11 × 7-line `!u!92` documents (header,
type name, `m_ObjectHideFlags`, `m_PrefabParentObject`, `m_PrefabInternal`, `m_GameObject`,
`m_Enabled`) = 77, plus 11 owner `m_Component` entries = 88. I aggregated every changed line
across all 8 files (`git diff -U0 | sort | uniq -c`) and every one falls in that set. No
`GameObject`, `Transform`, `MonoBehaviour`, or any other document was touched, and no line was
added. Both YAML spellings appear and both were handled — `- component: {fileID: N}` in scenes,
`- 92: {fileID: N}` in prefabs; the block type name is `Behaviour:` in scenes and `GUILayer:` in
prefabs, and the script keyed off `!u!92` rather than the name, which is correct.

### Removing GUILayer is safe in this project

- No `!u!131` (GUITexture) or `!u!132` (GUIText) block exists anywhere under
  `Ironfront_Reborn/Assets` — 0 hits.
- No C# reference to `GUILayer`, `GUIText`, `GUITexture` or `GUIElement` across all 441 `.cs`
  files under `Assets` — 0 hits.
- GUILayer only ever serviced `GUIText`/`GUITexture` raycasting, so with neither present nothing
  can observe its absence.

### `Player Fps Actor.prefab` is intact for Dev A's work

The two removed components belonged to the two camera GameObjects:

- `&1369040739045613` "FP Camera" — `Player Fps Actor.prefab:341`, still has
  `4` Transform, `20` Camera, `81` AudioListener, `124` FlareLayer, `164` AudioReverbFilter and
  two `114` MonoBehaviours.
- `&1055126122634302` "Third Person Camera" — `:4097`, still has `4` Transform, `20` Camera,
  `124` FlareLayer and two `114` MonoBehaviours.

`CharacterController` (`!u!143`, one instance, `:73`) sits on a different GameObject and was not
touched. Both `!u!20` Camera documents survive. No `Transform` (`!u!4`) document or
`m_Component` entry other than the two `- 92:` lines was removed, so the hierarchy is
byte-identical. `FpsActorController` is one of the surviving `114`s — no `m_Script` reference was
removed from any file.

### The lighting edit itself

3 insertions / 6 deletions in `Assets/Scenes` beyond the GUILayer lines — exactly one wrapped
two-line mapping collapsed to `m_LightingDataAsset: {fileID: 0}` per scene, in
Menu / Island / Dustbowl (Splash already 0). Line endings unchanged: the diff for each scene is
a handful of lines, which would be impossible if the file had been re-encoded.

### What else the LDA carries — nothing here depends on it

- **Light probes:** zero `!u!220` LightProbeGroup in any of the four scenes.
- **Baked reflection probes:** all four probes are Realtime (`m_Mode: 1`), see finding 6.
- **Shadowmask / mixed-light baked GI:** `m_UseShadowmask: 0` in all four scenes
  (`Menu.unity:10675`, `Island.unity:89658`, `Dustbowl.unity:78116`, `Splash.unity:2665`).
- **Renderer→lightmap mapping:** moot, no lightmap textures exist.

---

## Item 5 — other Unity-5-era classes that Unity 6 may have removed

I inventoried every `!u!N` class id in every serialized asset under `Assets` and resolved each to
its type name from the YAML itself:

`1 GameObject, 4 Transform, 20 Camera, 21 Material, 23 MeshRenderer, 29 OcclusionCullingSettings,
33 MeshFilter, 43 Mesh, 54 Rigidbody, 59 HingeJoint, 64 MeshCollider, 65 BoxCollider,
74 AnimationClip, 81 AudioListener, 82 AudioSource, 90 Avatar, 91 AnimatorController,
95 Animator, 102 TextMesh, 104 RenderSettings, 108 Light, 111 Animation, 114 MonoBehaviour,
121 Flare, 124 FlareLayer, 134 PhysicMaterial, 135 SphereCollider, 136 CapsuleCollider,
137 SkinnedMeshRenderer, 143 CharacterController, 146 WheelCollider, 153 ConfigurableJoint,
154 TerrainCollider, 157 LightmapSettings, 164 AudioReverbFilter, 170 AudioDistortionFilter,
183 Cloth, 196 NavMeshSettings, 198 ParticleSystem, 199 ParticleSystemRenderer, 205 LODGroup,
206 BlendTree, 213 Sprite, 215 ReflectionProbe, 218 Terrain, 222 CanvasRenderer, 223 Canvas,
224 RectTransform, 226 BillboardAsset, 228 SpeedTreeWindAsset, 241/243/244/245 AudioMixer*,
1001 Prefab, 1101/1102/1107 Animator*, 1953259897 TerrainLayer`
— plus the now-removed `92 GUILayer`.

**`92` was the only removed class.** Confidence per scene differs, and I am stating this
explicitly rather than asserting a blanket clear:

- **Menu.unity — editor-verified.** Unity 6000.3.21f1 opened it and reported *only* GUI Layer,
  while the file contains `124 FlareLayer`, `95 Animator`, `198/199 ParticleSystem`,
  `222/223/224` UI and `29/104/157/196` settings objects. Empirical proof those all still load.
- **Island / Dustbowl / Splash — not editor-verified.** They have not been opened. Their class
  sets add `21, 64, 102 TextMesh, 111 Animation, 136, 154 TerrainCollider, 183 Cloth, 205,
  215, 218 Terrain` over Menu's. I **suspect, and did not verify against Unity 6 API docs**, that
  all ten still exist. Supporting evidence: every engine module that hosts them is present in
  `Ironfront_Reborn/Packages/manifest.json` — `com.unity.modules.vehicles` (WheelCollider),
  `com.unity.modules.cloth` (Cloth), `com.unity.modules.terrain` + `terrainphysics`
  (Terrain, TerrainCollider), `com.unity.modules.animation` (Animation), `com.unity.modules.wind`
  (SpeedTreeWindAsset). The two I would watch are `102 TextMesh` and `111 Animation` — both
  legacy, both to my knowledge still shipped, neither confirmed. **The authoritative check is
  free: open each of the three scenes once and read the console.** Do that before declaring the
  branch done.

Two things I checked that are *not* removed-class problems but would produce a similar-looking
console wave, both clean:

- **Missing scripts:** every `m_Script` guid in the four scenes and in `Player Fps Actor.prefab`
  resolves. The one guid absent from `Assets/**/*.meta` —
  `f70555f144d8491a825f0804e09c671c`, `type: 3` — is the `UnityEngine.UI` package assembly,
  satisfied by `com.unity.ugui: 2.0.0` in the manifest. No orphaned MonoBehaviours.
- **Legacy prefab format:** 60 `!u!1001 Prefab:` documents (Unity 5 spelling, with
  `m_PrefabParentObject`). Unity 6 reads these but rewrites them to `PrefabInstance` on first
  save. Not an error — but it is where the unavoidable multi-thousand-line diff will come from
  the first time anyone saves a scene or prefab. Worth deciding deliberately (a one-shot
  "open and save everything" upgrade commit) rather than letting it land inside Dev A's
  netcode PR.

---

## Score: 8/10

The output files are correct and the diff is minimal and fully accounted for. Points off for the
tool's missing post-condition (finding 2) and its unsound safety check (finding 3), and for a
stated rationale on the lighting change that does not support its own conclusion (finding 1) —
all three are latent traps for the next person who reuses the reasoning or the script, none of
them broke anything this time.
