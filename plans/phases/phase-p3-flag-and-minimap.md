# Phase P3 — the flag on the pole, and the icons on the map

- **Plan:** [`../plan.md`](../plan.md) · **Closes:** two of the four first-minute defects · **Size:** M
- **Filed:** 2026-08-29, from the running Editor. Every fact below was read out of
  `Dustbowl.unity` and `Ingame UI Container.prefab` through the live Editor, not from YAML.

---

## 1. The flag — authoring is fine; the mechanism hides it

**All six capture points are correctly authored.** `lqFlag` → `Flag`, `hqFlag` → `HQ Flag`,
`flagParent` → `Flag Parent`, on Bridge, Oasis, Mine, Town, Fortress and Outpost alike. So the
first hypothesis — an unassigned serialized field — is **false**, and the row this would have
become does not exist.

The mechanism is two lines, and they agree with each other:

```csharp
// CapturePoint.Update()
localPosition.y = 1.2f + 4.8f * control;      // control 0 → flag at the BOTTOM of the pole
// CapturePoint.ApplyAuthoritativeOwner(), :294
SetFlagVisible(control > 0f);                 // control 0 → renderer DISABLED
```

At `control == 0` the cloth is lowered to the pole's base **and** switched off. What is left on
screen is a pole. That is the reported symptom precisely.

**Four of the six points open at `owner = -1`** (Bridge, Mine, Town, Outpost); Oasis opens at 0 and
Fortress at 1. So on a fresh map most flags start hidden **by design** — a neutral point flies
nothing — and the question is whether an *owned* point ever gets a non-zero `control` on a client.

The wire path is short enough to state in full:

```
server → S_CAPTURE_POINT.Owner (float, sign = team, magnitude = control)
       → NetClientObjectivePresenter:137-140
       → CapturePointOwnership.ToControl(owner)   // = owner < 0 ? -owner : owner
       → CapturePoint.ApplyAuthoritativeOwner(team, control, contested)
```

**So `control` is derived from the magnitude of `Owner`.** If the server quantises ownership to
−1 / 0 / +1, an owned point yields 1 and flies its flag; if it sends 0 for anything it considers
uncontested-but-owned, every flag on the map disappears. **That is the measurement this phase
exists to take**, and it must be taken on the wire, not inferred from either side's source.

**A second finding, recorded rather than acted on.** `QualitySettings.GetQualityLevel()` is **5**
(`Fantastic`) and `HQ_QUALITY_LEVEL` is 5, so `Awake` takes the HQ branch: `hqFlag` active,
`lqFlag` **deactivated**, and `flagRenderer` is a `SkinnedMeshRenderer`. A build running at a lower
quality level takes the other branch and renders a different object with a `MeshRenderer`. Both are
authored, so neither is broken — but a fix verified only at quality 5 is verified on one of the two
paths, and this is why acceptance below names both.

## 2. The minimap — the blip path cannot see a networked body

`MinimapUi.AddActorBlip(actor)` has **exactly one caller**: `ActorManager.cs:58`, inside
`ActorManager.Register`. Scope searched: all of `Assets/Scripts/**`.

And a remote networked body **deliberately never registers**. Ledger **A-2** is the decision and it
is not reversible in passing: `ActorManager.Register` ends
`if (!actor.aiControlled) instance.player = actor`, so registering a server-owned proxy would
repoint `ActorManager.Player` at somebody else's body and break every position, team, health and
resupply read that property exists to protect.

**So the missing icons are a structural consequence of a correct decision, not an oversight.** The
blip path is keyed to `Actor`; replicated bodies are not `Actor`s and must not become ones.

`actorBlipPrefab` **is** authored (`Actor Blip`) on `Ingame UI Container.prefab`. But
`capturePointMarkerPrefab` is **`<<NULL>>`** — a real authoring gap that the nine `ClientWiringGate`
authoring checks do not cover, so it has been passing green. Its own remark names the consequence:
the marker falls back to a spawn-point icon, "at least the right size and in the right place",
which is why nobody noticed a capture point wearing the wrong icon.

---

## 3. Tasks

### 3.1 — Measure `Owner` on the wire (S) — do this before writing any fix

One run, two clients, one owned point. Record the raw `S_CAPTURE_POINT.Owner` float per point per
message, alongside the `control` each client computes. Three outcomes, three different phases:

- **Owner carries magnitude and the client applies it** → the flag logic is correct and the defect
  is elsewhere (renderer, quality branch, `flagParent` lerp). Continue at 3.2.
- **Owner is quantised to −1/0/+1** → owned points fly flags, neutral points do not, and the
  reported symptom is a *map opening state*, not a defect. Say so and stop.
- **Owner is 0 for owned points** → a server-side defect in what is written, and the fix is on the
  server, not in `CapturePoint`.

**Do not skip to a fix.** `SetFlagVisible(control > 0f)` is the original game's neutral-point
behaviour; changing it without this measurement would make every neutral point fly a flag and call
that a fix.

### 3.2 — Render the flag on both quality branches (M)

Whatever 3.1 finds, verify the outcome at quality level 5 **and** at a level below 5 — the two
branches select different objects and different renderer types.

### 3.3 — Author `capturePointMarkerPrefab`, and gate it (S)

Author the field on `Ingame UI Container.prefab` **through the Editor**, never by editing YAML —
fileIDs are assigned by the Editor and a hand-written reference resolves to null while looking
assigned. Then extend `AssetWiringDetectors` so the field is checked: the existing nine checks
passed this prefab with the field null, which is the shape of a green that proves nothing.

The detector must assert more than non-nullness. Following `ScoreUiTextRefsAreAssigned`, which
exists in its current form because three mutations proved a weaker draft green: the field is
assigned; its fileID names an object that exists; and that object is not one another field already
drives.

### 3.4 — A blip path that does not require an `Actor` (M)

The subject is a transform with a team and a kind, not an `Actor`. `MinimapMarker` already exists
for exactly this — its own doc calls it "the `Transform`-shaped counterpart to `ActorBlip` … rather
than an `ActorBlip` with half its fields unused" — and `MinimapUi.markers` is keyed by `Transform`.
Use it; do not widen `ActorBlip`, and do not register proxies with `ActorManager`.

Three subject kinds, and the local player is **not** automatically one of them — verify whether the
local body registers in a networked session before assuming its own icon is present:

| Subject | Source |
|---|---|
| Remote players and replicated bots | the remote-actor registry the client already maintains |
| The local player | `ActorManager` — **confirm** it registers in a networked session |
| Vehicles | already replicated; 14 were counted on both observers in a prior run |

Colour by team through `ColorScheme.TeamColor`, as `ActorBlip` does, so friendly / enemy read the
same on both paths.

### 3.5 — Answer "map thì bé tí" before changing a number (S)

The complaint is ambiguous between the minimap **widget** being small and the rendered world being
small. `MINIMAP_SCALE` is 1.3 and `MinimapCamera` drives an orthographic render texture. Establish
which one is meant — a screenshot settles it — and then change the one thing. Changing both is how
a UI ends up tuned to no requirement at all.

---

## 4. Acceptance

| # | Criterion |
|---|---|
| 1 | The raw `Owner` float is recorded from a real run, and the flag verdict follows from it rather than from either side's source |
| 2 | An owned capture point renders its flag raised, verified at quality level 5 **and** below 5 |
| 3 | A neutral point still flies nothing — the neutral behaviour is not "fixed" away |
| 4 | `capturePointMarkerPrefab` is authored through the Editor and gated by a detector observed RED |
| 5 | A two-client screenshot shows friendly, enemy and own icons, distinguishable by team colour |
| 6 | No proxy is registered with `ActorManager`; `ActorManager.Player` still resolves to the local body after a remote body joins |
| 7 | Whichever of widget-size / world-size 3.5 identifies is changed, and the other is left alone |

---

## 5. Out of scope

- **Ledger A-2 stays WON'T-DO.** Nothing here authors `_actor`, a ragdoll rig, or remote weapon
  models. The icon path is built to avoid needing them.
- **The HUD's own fields** (`ScoreUi`) are authored and gated already; this phase does not touch
  them.
