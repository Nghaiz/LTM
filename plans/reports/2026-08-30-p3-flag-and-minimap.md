# P3 — the flag that was never a flag, and icons for bodies that are not Actors

- **Phase:** [`../phases/phase-p3-flag-and-minimap.md`](../phases/phase-p3-flag-and-minimap.md)
- **Date:** 2026-08-30 · **Branch base:** `develop` · **Branch:** `p3-flag-and-minimap`
- **Closes:** the two first-minute defects the phase was filed for — **neither by the mechanism
  the phase named** (§ 1, § 3)

---

## 1. The phase's central premise was false, and one screenshot falsified it

The phase was written from the running Editor and it located the flag defect at
`CapturePoint.cs:294`:

> `SetFlagVisible(control > 0f)` disables the renderer at zero control, and `Update()` lerps the
> flag to the bottom of the pole at the same value.

That mechanism is real, it does what the phase says, and **it has nothing to do with the reported
symptom.** Task 3.1's instruction was to measure `Owner` on the wire before writing any fix. The
measurement that settled it was cheaper than that and pointed somewhere else: render one owned
capture point, at full control, with the renderer explicitly enabled, on each of the two quality
branches.

| Branch | Quality | Renderer | Result |
|---|---|---|---|
| HQ | 5 (`Fantastic`, the default) | `SkinnedMeshRenderer` on `HQ Flag` | **bare pole** |
| LQ | 4 | `MeshRenderer` on `Flag` | flag renders, correctly, on the pole |

`control` was 1. `flagRenderer.enabled` was `true`. `flagParent.localPosition.y` was 6.00, the top
of the pole. The high branch drew nothing anyway.

**The phase also recorded the opposite of this, explicitly:** *"Both are authored, so neither is
broken."* That sentence is what a reader of the YAML concludes, and it is wrong — see § 2 for why
it is the kind of wrong that reads as right.

---

## 2. Root cause: two guids that name no asset

`HQ Flag`'s `SkinnedMeshRenderer` on all six Dustbowl capture points:

```yaml
m_Materials:
- {fileID: 2100000, guid: 2aaff793b776d0b45b232fc08ea42a5f, type: 2}
m_Mesh:  {fileID: 4300000, guid: 195886543318f6a41bd0575b175957e7, type: 2}
```

**No asset anywhere under `Assets/` carries either guid.** They were lost when the project was
reconstructed. Unity resolves a dangling reference to `null`, so the renderer had no mesh and no
material and could not draw at any ownership value, on any map, ever.

Read off the live Editor before the fix:

```
sharedMesh=NULL rootBone=NULL bones=0 mats=1 mat0=NULL enabled=True
localBounds=Center: (0.00, 0.78, 0.00), Extents: (0.01, 0.78, 0.55)
```

That `localBounds` is the receipt. It matches `Assets/Mesh/Flag.asset`'s own bounds to the
decimal — the reference was correct once, was serialized, and the asset it named went away. The
bounds are a fossil of an authoring that no longer resolves.

`QualitySettings.GetQualityLevel()` is **5** and `HQ_QUALITY_LEVEL` is 5, so `CapturePoint.Awake`
selects the broken object on every default-quality client. The five points that open neutral would
have hidden their flags anyway; **Oasis and Fortress open owned and should have flown one**, and
did not. Six poles, one cause.

**Why nine authoring checks passed it.** None looked at a renderer, and the YAML is not obviously
wrong: `m_Mesh` and `m_Materials` both hold a well-formed reference with a plausible guid. Only
resolving the guid against the tree separates a live reference from a dead one. This is
`rules/green-that-proves-nothing.md` in its "checks the wrong artifact" form — the gate was asking
whether a field was assigned, and the field was assigned.

**Fix:** the six renderers were re-pointed at `Assets/Mesh/Flag.asset` and
`Assets/Material/Flag.mat` through the Editor. Verified by re-screenshotting both branches — both
now render the flag raised and team-coloured.

Renders are in `artifacts/p3/` (git-ignored, local to the machine that ran this):
`flag-before-quality5.png` (a bare pole) beside `flag-before-quality4.png` (the flag), then
`flag-after-quality5.png` and `flag-after-quality4.png` (both flying, team-coloured); plus
`minimap-before.png` and `minimap-after.png` for § 4.

**No runtime fallback was added**, deliberately. A `CapturePoint.Awake` that silently swapped to
the low-quality object when the high one cannot draw would hide exactly the defect the new gate
now names out loud, and this defect survived three years of the project by being silent.

---

## 3. The minimap: a decision, an authoring gap, and a dead method

### 3.1 Ledger A-2 stands; the icon path was built around it

`MinimapUi.AddActorBlip` has one caller, `ActorManager.Register`, and a replicated proxy must
never reach it — `Register` ends `if (!actor.aiControlled) instance.player = actor`, so
registering a proxy repoints `ActorManager.Player` at somebody else's body. The phase said so and
it is right.

So the icons go through `MinimapUi.SetMarker`, which is keyed by `Transform` and was already built
for exactly this shape. `RemoteActorRegistry` and `RemoteVehicleRegistry` now bind an icon on
spawn and drop it on despawn. **No proxy is registered with `ActorManager`.**

### 3.2 The seam the phase did not know it needed

`MinimapUi`, `ColorScheme` and `MinimapMarker` compile into `Assembly-CSharp`, which is compiled
last and which **no assembly definition may reference** — the constraint that already produced
`ICapturePointDirectory` and `IDecalSink`. The registries live in `Ironfront.Net.Unity.Client` and
cannot name the minimap at all.

So the phase's "use `MinimapMarker`; do not widen `ActorBlip`" is delivered through a new
`IMinimapMarkers` seam registered from `IronfrontNetBindings`, alongside the four that already
exist. The team crosses as an `int` in `SpawnPoint.owner`'s spelling; the colour does not, because
`ColorScheme.TeamColor` is the one answer to "what does team N look like" and a `Color` crossing
the seam would let a second answer grow inside the netcode.

### 3.3 `SendFullMatchStateTo` had zero callers

Found while tracing task 3.1's wire path. `MatchController.SendFullMatchStateTo` — whose own
summary reads *"the state a joining client needs before its first snapshot"* — was called from
nowhere in the repository.

The consequence is not small. Capture points broadcast only when **dirty**, and
`CapturePointState.AdoptOpeningOwner` deliberately marks the opening value as already-sent, so a
point nobody walks onto emits nothing for the whole match. A joining client therefore received no
capture-point state and no match state at all, and rendered every flag from
`CapturePoint.Start`'s local defaults. On Dustbowl at t=0 those defaults happen to agree with the
authoritative values, which is why nothing looked wrong; join after a point has changed hands and
they do not.

This is the third instance of the same shape in one file — `WritePlayerList` and `WriteDespawn`
both carry a comment saying the writer had no caller anywhere in the repo. `WriterCoverageRunner`
catches an unused *writer*; `SendFullMatchStateTo` is not a writer, so nothing covered it.

Wired into `ServerTickLoop.OnClientConnected`, after `EmitPlayerList()`.

### 3.4 `capturePointMarkerPrefab` was null

A real authoring gap the nine checks did not cover, exactly as the phase says. Authored through
the Editor as `Assets/Prefab/Capture Point Marker.prefab` — a `RawImage` on
`Assets/Texture2D/flag.png`, distinct from both `Actor Blip` and `Spawn Point Button`.

---

## 4. Task 3.5 — the world, not the widget

The user settled the ambiguity: the world drawn inside the minimap is what reads as too small.

**The phase's description of the mechanism is wrong.** `MinimapCamera` is **not orthographic** —
`orthographic=False`, and its `orthographicSize` of 5 is inert. It is a perspective camera at
y=4064 with a 22° field of view. And `MINIMAP_SCALE` (1.3) does not scale the world: it scales
`minimapSize`, a widget measure in `MinimapUi.Awake`. Neither of the two knobs the phase names is
the one that governs the framing.

Measured instead:

| | Before | After |
|---|---|---|
| Camera centre | (1500, 1419) | (1587.2, 1384.7) |
| Field of view | 22° | 16.80° |
| Ground span | 1564 m | 1187 m |
| Capture points, viewport | [0.199 .. 0.871], margins 0.233 / 0.129 | [0.074 .. 0.916], margins symmetric |

The playable area — all six spawn points, which on Dustbowl are the six capture points — spans
997 × 860 m centred on (1587.2, 1384.7). It filled 64% of the minimap's width and 55% of its
height, off-centre, on a 3000 m terrain.

**1.3× is the honest ceiling here, and it is worth saying plainly.** Past ~17° the outermost
points cross the frame edge and their icons clip; at 13.5° the Fortress and the Oasis are cut off
outright. If the map still reads small after this change, the remaining smallness is the widget,
not the world, and that is a different change to a different file.

**This landed as code, not as a scene value.** `MinimapCamera` now frames itself from the map's
spawn points at `Awake`. A hand-tuned camera is correct for exactly one map and silently wrong for
the next, with nothing to report it (`rules/replicate-and-automate.md`). Every icon on the
minimap — `ActorBlip`, `MinimapMarker`, the spawn buttons — positions itself with
`WorldToViewportPoint` against this same camera, so they follow the new framing without knowing it
moved. A scene with no spawn points keeps its authored framing.

---

## 5. Two new gates, both observed RED before they were green

`CapturePointFlagsCanDraw` — both flag objects on every capture point carry a renderer whose mesh
and material resolve to assets the tree contains.

- **RED on the pre-fix tree: 11 findings** — 6 dangling meshes and 5 dangling materials. (Oasis
  had kept its material; only its mesh was lost. That asymmetry is itself evidence the references
  decayed rather than never existing.)
- Green after the authoring, across 4 scenes and 63 prefabs.
- **Its first draft was wrong in the dangerous direction.** It reported all eleven `lqFlag`
  objects as dangling, because they draw the built-in `Cube` whose guid
  (`0000000000000000e000000000000000`) has no `.meta` in the tree. A gate that fires on healthy
  authoring teaches the reader to skip its output, which is how the real finding beside it gets
  missed. Unity's three built-in resource libraries are now recognised, and a fixture test pins
  that.

`MinimapMarkerPrefabsAreAuthored` — all three `MinimapUi` icon prefabs assigned, resolving, and
distinct from each other.

- **RED on the pre-fix tree: 1 finding** (`capturePointMarkerPrefab` unassigned). Green after.

Both assert more than non-nullness, following `ScoreUiTextRefsAreAssigned`, which is in its
current shape because three mutations proved a weaker draft green. **17 fixture tests** pin the
mutations that a null-only check would pass: a `fileID` naming no object, a guid no asset carries,
two fields aimed at one prefab, a reference into the same asset rather than a prefab, an empty
material list, a flag object with no renderer, a `MeshRenderer` with no `MeshFilter`, and the
built-in guids.

---

## 6. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | The raw `Owner` float is recorded from a real run, and the flag verdict follows from it rather than from either side's source | **MET** — § 7.1; the verdict is the phase's outcome 1, and it exonerates the mechanism the phase suspected |
| 2 | An owned capture point renders its flag raised, verified at quality 5 **and** below 5 | **MET** — screenshots at 5 and 4, before and after |
| 3 | A neutral point still flies nothing | **MET** — `SetFlagVisible(control > 0f)` is untouched |
| 4 | `capturePointMarkerPrefab` authored through the Editor and gated by a detector observed RED | **MET** — § 3.4, § 5 |
| 5 | A two-client screenshot shows friendly, enemy and own icons by team colour | **UNGRADEABLE FROM THE ARTIFACT** — § 7.3. Not a fail and not a pass: no lane-B client can open the minimap, so no run of this harness can answer it either way |
| 6 | No proxy registered with `ActorManager`; `ActorManager.Player` still resolves to the local body | **MET** — § 3.1; the icon path is `Transform`-keyed by construction |
| 7 | Whichever of widget-size / world-size 3.5 identifies is changed, the other left alone | **MET** — camera only; no `RectTransform`, no `MINIMAP_SCALE` |

---

## 7. The run

`artifacts/lane-b/p3-flags-01` — `-Build -Set combat`, 3/3 clients exit 0, 7/7 checkpoints each,
21 frames. Seeds `UnityEngine.Random=20260821`, `NetworkSimulator=off/12345`. Spawn **sampled,
not pinned** (X-22: this run is a coin flip and is not comparable to another run's positions).

### 7.1 Task 3.1 — `Owner` on the wire

The server's opening adoption, per point:

```
[net] opening point 0: scene owner 1 -> Owner 1.00 (control 1.00)
[net] opening point 3: scene owner 0 -> Owner -1.00 (control 1.00)
[net] opening ownership adopted: 6 of 6 capture point(s) start owned.
```

What arrived on each client, at join, for all six points:

```
[net] capture point 0: OwnerQ 100 -> Owner 1.00, OwningTeam 1 -> spawn owner 1,
      control 1.00, contested False -- flag VISIBLE at pole height 6.00
[net] capture point 3: OwnerQ -100 -> Owner -1.00, OwningTeam 0 -> spawn owner 0,
      control 1.00, contested False -- flag VISIBLE at pole height 6.00
```

And mid-capture, as a point was fought over:

```
OwnerQ 99 -> control 0.99, pole height 5.95     OwnerQ 96 -> control 0.96, pole height 5.81
OwnerQ 97 -> control 0.97, pole height 5.86     OwnerQ 95 -> control 0.95, pole height 5.76
```

**Verdict: the phase's first outcome.** *"`Owner` carries magnitude and the client applies it →
the flag logic is correct and the defect is elsewhere."* The sign carries the team, the magnitude
carries the progress, `ToControl` takes the magnitude, and the pole height follows it continuously
— 2,165 to 2,169 messages per client over the run, none of them wrong. **Nothing in the ownership
path was ever broken.** The defect was § 2's dangling guid, and no amount of measuring `Owner`
would have found it.

**Two things this run could not have shown before this branch.** The join-time block of six exists
only because § 3.3 wired `SendFullMatchStateTo`; without it the first message for an untouched
point never arrives. And this configuration runs `assaultMode`, so `CapturePoint.Start` hands the
four neutral points to team 1 and the map opens **6 of 6 owned** — which is why every point here
reports a visible flag rather than the two the phase's static read of Dustbowl predicted.

### 7.2 The icon path ran

`remoteActorCount` is **42** on OBS-A and **41** on DRIVER at the first checkpoint, so
`RemoteActorRegistry.OnSpawn` — where the marker is bound, unconditionally — ran forty-odd times
per client. **No `minimap-no-marker-prefab` warning** appears in any client log, so `SetMarker`
resolved a prefab on every one of those calls, and no exception names any minimap type.

The only NRE in the run is `ActorManager.RegisterProjectile` (`ActorManager.cs:373`, from
`Rocket.Start`), which is pre-existing and unrelated.

### 7.3 Criterion 5 is UNGRADEABLE FROM THIS ARTIFACT, and that is a harness gap

**The minimap is not in any of the 21 frames, and no lane-B run can put it there.**
`MinimapUi.Update` reads `Input.GetKey(KeyCode.M)` directly, and a scripted client drives the
actor through `ScriptedInputSource`, never through Unity's `Input`. The minimap panel is therefore
parked off-screen (`ingameParent.anchorMin.y` lerped to −1) for the whole life of every lane-B
client, at every checkpoint, on every run this harness has ever produced.

This is the X-48 shape one layer over: not "the frames render the wrong thing" — they render the
game correctly now — but "the thing this check is about is not reachable by any input the harness
can produce". Filed rather than folded in; closing it is P5 work, and it needs the minimap's
open/close to read through the same input seam the rest of the client already uses, not a lane-B
special case wired into shipped code.

**What is established instead:** the path runs (§ 7.2), its prefab is authored and gated (§ 3.4,
§ 5), the seam is registered, and `MinimapMarker.SetColor` writes `ColorScheme.TeamColor` — the
same call `ActorBlip` makes, so friendly and enemy read identically on both paths by construction.
**What is not established:** that a human looking at two clients sees three distinguishable icons.
Do not read § 7.2 as that.

---

## 8. What this phase did not do

- **Ledger A-2 stays WON'T-DO.** Nothing here authors `_actor`, a ragdoll rig or remote weapon
  models.
- **`ScoreUi`'s own fields** are untouched.
- **No runtime fallback for an undrawable flag**, per § 2.
- **The widget half of 3.5** is untouched, per criterion 7 — and per § 4, it is where any
  remaining "too small" has to be addressed.
