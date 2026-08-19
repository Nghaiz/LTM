# Code Review: `ScoreUi.phaseText` / `phaseTimerText` authoring + `ScoreUiTextRefsAreAssigned`

Adversarial review of the uncommitted change on `develop` (task 1.6, ledger A-9).
Gate re-run against the current tree: **exit 0**, `8 authoring check(s) clean across 4 scene(s)
and 62 prefab(s)`. The GREEN half of the brief's claim is re-derived.

## Verdict summary

The authoring is correct and the prefab migration is clean. The detector is **weaker than
its own docstring claims**: it pins "each field is not its own paired fallback", not "each
field is a distinct, real label", and three plausible broken authorings pass it green.

---

## Critical

_None._ No finding here produces a false green on the CURRENT tree; all are gaps that would
open on a future edit.

## Important

### I-1 — `ScoreUiTextRefsAreAssigned` passes three plausible broken authorings
`tools/ClientWiringGate/AssetWiringDetectors.cs:510-560`

The check is a **named-pair** comparison (`phaseText` vs `blueFlagsText`, `phaseTimerText` vs
`redFlagsText`) rather than a **distinctness** check. All three of these return zero findings:

| Broken authoring | Runtime result | Detector |
|---|---|---|
| `phaseText: {fileID: 902}` (= `redFlagsText`), `phaseTimerText: {fileID: 901}` (= `blueFlagsText`) — the two assignments swapped | still borrowing both flag labels; the exact E5 collision | **green** |
| `phaseText` and `phaseTimerText` both → the same new label | `SetAuthoritativeState` writes phase then timer to one `Text`; the timer wins, the phase never renders (`ScoreUi.cs:198-208`) | **green** |
| either → `blueScoreText` / `redScoreText` / `victoryText` | collides with the ticket counters, which ARE written on every state change | **green** |

Swapping two inspector drags and duplicating a row without re-targeting are the two most
common ways this authoring actually breaks. Per `green-that-proves-nothing.md` the question is
"if the thing this guards were broken right now, would this go red" — for these three, no.

The invariant that actually holds is stronger and no harder to write: `phaseText` and
`phaseTimerText` must each be distinct from **every other** `Text` ref on the component and
from each other. Suggested shape — collect the five pre-existing refs
(`blueScoreText`, `redScoreText`, `blueFlagsText`, `redFlagsText`, `victoryText`) plus the
sibling of the field under test into a set and report any collision, naming the field it
collided with.

### I-2 — a dangling or non-`Text` reference reads as clean
`tools/ClientWiringGate/AssetWiringDetectors.cs:519-527`

`IsNull` is `FileId == 0` only. `phaseText: {fileID: 999999}` where no such anchor exists in
the prefab passes the check, and Unity deserializes it to `null` at runtime — so the HUD
silently returns to the fallback the check exists to forbid, with the gate green. Same for a
`fileID` that resolves to a `RectTransform` or a `CanvasRenderer` rather than a
`UnityEngine.UI.Text`.

Both sibling detectors that follow a reference DO resolve it and raise `AssetGateUnknown` on a
dangle — `RemoteActorPrefabIsAuthored` at `AssetWiringDetectors.cs:352-357` and
`TracerPrefabIsCosmeticOnly` at `:430-434`. This one does not, and the documents it would need
are already in hand: `index.Documents(path).Any(d => d.AnchorId == assigned.Value.FileId)`.

(Pinning the target's script guid is the stronger form but is currently brittle — see M-2: the
project is mid-migration between the legacy uGUI DLL guid and the package guids, so
`UnityEngine.UI.Text` has two guids live in the tree. Anchor existence is the safe half.)

### I-3 — the zero-instance `throw` cites a sibling precedent that does not apply
`tools/ClientWiringGate/AssetWiringDetectors.cs:562-568`, remark at `:510-514`

The siblings are consistent once you state the rule correctly: **absence of the check's
navigation ANCHOR is `Unknown`; absence of the check's SUBJECT is a finding.**

| Detector | Missing thing | Verdict | Which is it |
|---|---|---|---|
| `PresentersAreOnTheClientObject` (`:207`) | client scene | throw | anchor |
| `ProjectileCatalogInstallerIsWired` (`:255`) | server scene | throw | anchor |
| `RemoteActorPrefabIsAuthored` (`:398`) | `RemoteActorRegistry` | throw | **anchor** — the subject is the prefab's fields |
| `TracerPrefabIsCosmeticOnly` (`:465`) | `CosmeticTracerPool` | **finding** | subject |
| `ExplosionEffectsAreAuthored` (`:305`) | the presenter | **finding** | subject |
| `CheckPrefabArray` (`:619`) | the component | **finding** | subject |

`ScoreUi` is the **subject** of A-9, so the sibling that matches is `TracerPrefabIsCosmeticOnly`
(also "a shipped component on one known object"), not `RemoteActorPrefabIsAuthored`. The
remark's stated reason is therefore wrong even though the outcome is defensible — exit 2 still
fails CI, so this is not a false green, and `CheckPrefabArray`'s doctrine ("must not be
satisfiable by deleting the component") is not violated either. But `PrefabsByKindIsComplete`'s
docstring at `:216-219` says explicitly that zero instances must be a finding for exactly this
class of check, and deleting `ScoreUi` from the prefab should read as "the HUD is gone", not as
"the gate cannot grade this".

Fix either the verdict or the remark. My recommendation: make it a finding, matching
`TracerPrefabIsCosmeticOnly` verbatim.

### I-4 — `ScoreUi.cs` comments are now false, and the change did not update them
`Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ScoreUi.cs:38-42`, `:152-160`

- `:38-42` — "Optional and **unset on the shipped prefab**: when the client track adds real
  elements they are assigned here". They are now assigned. The comment is stale as of this diff.
- `:152-160` — "`ScoreUi` has exactly four `Text` fields and **none of them is a dedicated
  phase, timer or human-count element** … **The client track still has to add real
  phase/timer/human-count elements to this prefab** and this mapping should be deleted once
  that lands." Two of the three now exist.

A reader of `SetAuthoritativeState` is now told the opposite of what the prefab contains. This
is squarely `development-principles.md` § "Update Skills After Every Error" / the ledger's own
author-then-pin discipline applied to source comments.

### I-5 — ledger A-6 overstates closure: the human-count element is still unauthored
`plans/debt-closure/debt-ledger.md:47`

A-6 now reads "**The authoring half is done; what is left is not authoring**" and "[the runtime
timer clause] is **the only thing** still holding this row open".

`ScoreUi.cs:152-160` names **three** elements E5 owes — phase, timer, **and human-count** — and
the human count is still concatenated into the phase label string at `ScoreUi.cs:201`
(`PhaseLabel(phase) + " (" + humanPlayerCount + ")"`). The same remark also says "this mapping
should be deleted once that lands", and the fallback ternaries at `:198` and `:204` are still
present.

So at least two authoring/code obligations E5 named remain. Either narrow the A-6 claim to
"`phaseText` / `phaseTimerText` are authored" or record the human-count element and the
fallback retirement as explicit residue. As written the row would let the next reader close E5
on a false premise.

---

## Minor

### M-1 — nothing asserts the registered check set, contrary to the runner's own docstring
`tools/ClientWiringGate/AssetGateRunner.cs:31-35`

> "A list rather than a chain of calls so `--list-asset-checks` can print it and **the fixture
> tests can assert the registered set**. A check that exists as a method but is not in this
> list is a file that runs on nobody's machine."

`grep -rn "AssetGateRunner.Checks" Ironfront.Net.Replication.Tests/` returns nothing. No test
asserts the set. This change registered the detector correctly, but the guard the docstring
claims does not exist — a future detector could ship unregistered and every test would stay
green (`wired-not-just-present.md`). Pre-existing, surfaced by this diff because it is the
diff's own wiring step.

### M-2 — the prefab is now the second asset on the new uGUI package guids; 10 files still on the legacy DLL guid

The save rewrote all 139 `m_Script` refs from the legacy `UnityEngine.UI.dll` guid
`f70555f144d8491a825f0804e09c671c` (type selected by `fileID`) to per-type package guids
(`5f7201a12d95ffc409449d95f23cf332` = `Text`, etc.). One-to-one, no orphans. `Scenes/Menu.unity`
was already migrated; 10 assets under `Assets/` still carry the legacy guid and 61 files still
use the legacy `m_PrefabParentObject` prefab format. Not a defect, but it means (a) the same
~10K-line churn will recur on each of those files the first time it is saved, and (b) any future
check that pins a uGUI script guid must handle both forms.

### M-3 — "styled from `blueFlagsText`" is approximate
`plans/debt-closure/debt-ledger.md:50`

Font, `m_FontSize: 14`, `m_BestFit: 1`, `m_MinSize: 10` match. `m_MaxSize` is 40 on the new
labels vs 100 on `blueFlagsText`, and `m_RaycastTarget` is 0 vs 1. Both deviations look
deliberate and correct (a non-interactive label should not be a raycast target); the ledger
sentence just claims more sameness than exists.

### M-4 — `Run` returns 2 on the first check that throws, dropping later checks' findings
`tools/ClientWiringGate/AssetGateRunner.cs:88-98`

Pre-existing, and it contradicts the comment two blocks up ("Every check runs even after one
has produced findings … a gate that stopped at the first would turn a single Editor session
into six round trips"). An `Unknown` from any check discards every finding after it. Not
introduced here; noted because `ScoreUiTextRefsAreAssigned` adds one more throw site and it is
registered **last**, so it is the check most likely to be the one silenced rather than the
silencer.

---

## Verified correct (stated briefly, so it is on the record)

- **The guid comparison in the same-object clause is right.** `AssetWiringDetectors.cs:544-546`.
  For a reference inside the same file Unity writes no `guid`, so `UnityObjectRef.Guid` is
  `null` on both sides and `string.Equals(null, null, …)` is `true` — the local-vs-local case
  works. The mixed case (one local, one external with the same `fileID`) is correctly NOT
  flagged, because those genuinely are different objects. `UnityAssetYaml.cs:31-34` documents
  exactly this distinction.
- **The fallback pairing matches the runtime.** `ScoreUi.cs:198` → `blueFlagsText`,
  `ScoreUi.cs:204` → `redFlagsText`. The detector's pairs are correct.
- **The `WarnOnce` can no longer fire.** `ScoreUi.cs:209-216` fires only when either field is
  null; both are now non-zero.
- **Prefab authoring is well-formed.** `phaseText: {fileID: 5948664229585240334}` and
  `phaseTimerText: {fileID: 5727226060667409086}` (`Ingame UI Container.prefab:7623-7624`) are
  distinct `!u!114` docs whose `m_Script` is the uGUI `Text` guid, on active GameObjects
  `Phase Label` / `Phase Timer`, parented to `Phase Row` (`&3454051964927800542`), which is
  child 3 of the `Score UI Canvas` RectTransform. Parent/child lists are mutually consistent.
- **The four new tests that can fail, do.** `AnUnassignedPhaseTextIsReported` (absent key),
  `AZeroFileIdTimerRefIsReported` (`{fileID: 0}`), `AssigningTheFlagLabelsDoesNotSatisfyTheCheck`
  (exact count 2, so the clause cannot silently stop working), and
  `ATreeWithNoScoreUiIsUnknownRatherThanClean` all pin a real direction. None is vacuous:
  `DedicatedPhaseAndTimerLabelsAreClean`'s `Assert.Empty` cannot be satisfied by the
  zero-instance path, because that path throws before returning.

## Prefab migration audit (focus area 4)

Re-derived independently by parsing both revisions into per-anchor field maps and diffing.

- **Anchors:** 463 → 472. Exactly one removal, `!u!1001 &100100000`; its body was
  `m_Modifications: []`, `m_RemovedComponents: []`, `m_ParentPrefab: {fileID: 0}` — nothing
  semantic was lost. 10 additions, all the new Phase Row subtree.
- **References into the interior:** exactly one external reference to this prefab's guid
  `8adbfb3c0de5c3e42bb18e9204ca3b2d` exists anywhere under `Assets/` —
  `Resources/_Managers.prefab:222` `ingameUiPrefab: {fileID: 1827090739459004, …, type: 2}`.
  That anchor is the root GameObject and survives the migration (HEAD line 14 → new line 7930).
  **Nothing references `100100000`**, so its removal breaks no link.
- **`.meta`:** unchanged (`git status` shows only the `.prefab`), which is correct — the asset
  guid is unaffected by a format migration.
- **Value churn, all accounted for:** `m_Component` list syntax (`- 224:` → `- component:`);
  YAML quoting of `m_Text`; float notation (`3.0517578E-05` → `0.000030517578`);
  `m_PrefabParentObject`/`m_PrefabInternal` → `m_CorrespondingSourceObject`/`m_PrefabInstance`/
  `m_PrefabAsset`; `m_RootOrder` dropped in favour of `m_Children` order; and ~250
  `<absent> → Unity default` additions (`m_CullTransparentMesh: 1`, `m_UseSpriteMesh: 0`,
  `m_PixelsPerUnitMultiplier: 1`, `m_Navigation.m_WrapAround: 0`, `m_Colors.m_SelectedColor`,
  `m_SpriteState.m_SelectedSprite`, `m_AnimationTriggers.m_SelectedTrigger: Highlighted`,
  three `Canvas` flags, `AudioSource` curve blocks).
- **The one change that is not pure format:** 32 RectTransforms had a non-zero
  `m_LocalPosition` and now read `{x: 0, y: 0, z: 0}`. Spot-checked `&224023659080760079` and
  `&224030939269533271`: `m_AnchorMin`/`m_AnchorMax`/`m_AnchoredPosition`/`m_SizeDelta`/`m_Pivot`
  are byte-identical across the migration. `RectTransform` recomputes `localPosition` from
  those at layout, so this is derived data being re-normalised, not lost geometry.
- **Risk NOT covered by the brief's checks, now closed:** the brief verified object identity and
  the in-Editor resolve, but not whether any *other* asset keyed on the removed `100100000` or
  on an interior fileID. It does not — see above. `Ironfront_Reborn/Assets/Scenes/*.unity` carry
  no instance of this prefab at all; it is instantiated from `Resources/_Managers.prefab`.

## Ledger accuracy (focus area 5)

Claim-by-claim against the files:

| Claim (`debt-ledger.md`) | Verdict |
|---|---|
| "All 462 pre-existing objects kept their fileIDs" | ✅ 463 − 1 removed = 462 |
| "the only removal is `!u!1001 &100100000`" | ✅ |
| "anchors x 0.3-0.7, y 0.855-0.900" | ✅ exact |
| "sits directly under the ticket panel" | ✅ `Panel` is y 0.905-0.97, no overlap |
| "clear of … the bar, score labels, both flag circles" | ✅ those are inside `Panel` |
| "sibling of `Panel`" | ✅ children of `Score UI Canvas` are `Panel`, `Victory Screen`, `Phase Row` |
| "`Phase Label` (`MiddleLeft`) / `Phase Timer` (`MiddleRight`)" | ✅ `m_Alignment: 3` / `5` |
| "all eleven serialized fields are now assigned" | ✅ 11 fields, none `{fileID: 0}` |
| "the `WarnOnce` naming E5 can no longer fire" | ✅ |
| "both styled from `blueFlagsText`" | ⚠️ see M-3 |
| A-6: "the authoring half is done … the only thing still holding this row open" | ❌ see I-5 |
| Handoff: "verifiable from git history: … **A-9** in the commit that added …" | ⚠️ the commit does not exist yet; the sentence becomes true only if detector + authoring + ledger land in one commit. Keep them atomic. |

---

## Empirical proof of I-1 and I-2

Not argued from reading — run. The prefab was backed up, mutated, graded, and restored
byte-identically (md5 `1caf3516c83b8263e6d4f7d9fc917ed7` before and after; `git status` unchanged).

**Mutation A — the two assignments swapped onto the flag labels:**

```
  blueFlagsText:  {fileID: 114266313168939955}
  redFlagsText:   {fileID: 114040315688348750}
  phaseText:      {fileID: 114040315688348750}   <- redFlagsText's target
  phaseTimerText: {fileID: 114266313168939955}   <- blueFlagsText's target
```

→ `[asset-wiring] 8 authoring check(s) clean`, **exit 0**. The HUD is borrowing both flag
labels — the precise state A-9 was opened for — and the gate reports the row closed.

**Mutation B — both fields pointing at one non-existent anchor:**

```
  phaseText:      {fileID: 999999999999999}
  phaseTimerText: {fileID: 999999999999999}
```

→ `[asset-wiring] 8 authoring check(s) clean`, **exit 0**. Unity deserializes both to `null`,
`SetAuthoritativeState` takes the fallback, `WarnOnce` fires every match — and the gate is green.

That is the `green-that-proves-nothing.md` shape verbatim: the check cannot go red for the
failure it exists to catch, only for the two narrow spellings of it that the tests happen to pin.

## Scope of the negative results above

- "No other asset references this prefab's interior": `grep -rn 8adbfb3c0de5c3e42bb18e9204ca3b2d`
  across `Ironfront_Reborn/Assets/**/*.{prefab,unity,asset,cs}` — one hit,
  `Resources/_Managers.prefab:222`.
- "Nothing keys on `100100000`": same sweep; no hit pairs that fileID with this guid.
- "No test asserts the registered check set": `grep -rn "AssetGateRunner.Checks\|Checks.Count"`
  across `Ironfront.Net.Replication.Tests/**/*.cs` — zero.
- **I-5's human-count clause rests on `ScoreUi.cs:152-160` alone.** I searched `plans/**/*.md`
  for the client-track E5 item and it is not in this repo (`plans/replication/integration-checklist.md:142`
  defines a *different* E5, about `S_DESPAWN_ACTOR`). The source remark is the only statement of
  E5's HUD clauses I could reach, and it names three elements. If the authoritative E5 lives
  outside this repo and names only two, I-5 downgrades to "the stale comment at `:152-160` should
  be corrected" (still I-4).

---

## Recommended order of work

1. **I-1 + I-2 together** — one edit to `ScoreUiTextRefsAreAssigned`: build the set of the other
   five `Text` refs plus the sibling field, report any collision naming the field collided with;
   and assert `index.Documents(path).Any(d => d.AnchorId == assigned.Value.FileId)`. Add two
   fixture tests (cross-swap, both-same-object) and one for the dangling anchor.
2. **I-4** — correct `ScoreUi.cs:38-42` and `:152-160` in the same commit as the authoring.
3. **I-5** — narrow the A-6 claim or record the residue.
4. **I-3** — pick one: make zero-instance a finding (my recommendation), or rewrite the remark to
   stop citing `RemoteActorPrefabIsAuthored`.
5. M-1 — a one-line test asserting `AssetGateRunner.Checks` covers every public
   `IEnumerable<GateFinding>` method on `AssetWiringDetectors` by reflection.

## Score: 7/10

Strong work with one real hole. The authoring is correct and well-placed, the prefab migration
was audited more carefully than most people would, the ledger prose is accurate on almost every
checkable claim, and none of the five tests is vacuous. The deduction is for I-1/I-2: the
detector's docstring claims it defeats "a false green with a fileID attached", and two of the
three most likely wrong authorings produce exactly that. A check that is 60% of the way to its
own stated contract is more dangerous than one that is honest about being a null check, because
the docstring is what the next reader will trust.

