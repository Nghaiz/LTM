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



---

# Follow-up review — merged state `4a6cb55` (PR #146)

Re-verified independently: the merged code was re-read, both original mutations re-run against
the real prefab, one **new** mutation added, the registration guard deliberately broken, and the
suite re-counted. Prefab md5 `1caf3516c83b8263e6d4f7d9fc917ed7` — byte-identical to the revision
originally reviewed, before and after all mutation runs. `git status` clean at exit.

## All five Important findings verified closed

| # | Fix | Independently re-derived |
|---|---|---|
| I-1 | `RenderedLabels` = all 7 labels; `other == field` self-skip; both owed fields in the set (`AssetWiringDetectors.cs:150-161`) | **Mutation A re-run → exit 1**, two findings naming `redFlagsText` / `blueFlagsText` correctly |
| I-2 | Anchor resolution: local fileID must name a document; non-null unresolvable guid throws | **Mutation B re-run → exit 1**, 4 findings (2 resolution + 2 both-fields-same-object) |
| I-3 | Zero instances is now a finding; docstring states the anchor-vs-subject rule explicitly | Test **inverted**, not deleted or re-pinned: `ATreeWithNoScoreUiIsUnknownRatherThanClean` → `ATreeWithNoScoreUiIsReportedRatherThanVacuouslyClean`. Exactly `pinned-baseline-test-companion.md`'s required move |
| I-4 | Both comment blocks in `ScoreUi.cs` corrected, and the human-count residue named in the source itself | Read the diff; accurate, and it now points forward at the detector that pins it |
| I-5 | A-6 names both remaining clauses and records the scope caveat verbatim | Read `debt-ledger.md:47` |
| M-1 | `EveryDetectorIsRegisteredWithTheRunner` with `Assert.NotEmpty(declared)` | **Deliberately unregistered `ScoreUiTextRefsAreAssigned` → RED**: `Assert.All() Failure: 1 out of 8 items`. Non-vacuous, and the "8" confirms reflection finds every detector |

Suite: **1592 passed, 0 failed** (re-counted across all four assemblies). Asset-gate fixtures: 40
passed. Gate on the restored tree: exit 0, 8 checks clean.

The fixture bug the coordinator caught — anchors 901-905 now carrying real documents — was the
right catch and would otherwise have made all four new red-path tests pass for the wrong reason.

## New finding (Minor) — an existing anchor of the WRONG TYPE still reads clean

`tools/ClientWiringGate/AssetWiringDetectors.cs` — the resolution clause
`index.Documents(target).Any(d => d.AnchorId == assigned.FileId)`.

**Mutation C**, run against the real prefab: point `phaseText` at `{fileID: 3454051964927800542}`
— the `Phase Row` **RectTransform** (`!u!224`), an anchor that genuinely exists in the file.

```
[asset-wiring] 8 authoring check(s) clean across 4 scene(s) and 62 prefab(s)
EXIT=0
```

Unity deserializes a `Text` field naming a `RectTransform` fileID as `null` → the fallback runs →
the exact state A-9 closed, gate green. The resolution clause closed the *dangling* half of I-2
but not the *type* half.

Narrower than the original hole — Editor drag-and-drop cannot produce it. But this project does
not author that way: `debt-ledger.md:50` records the authoring was done programmatically via
`PrefabUtility.LoadPrefabContents` / `SaveAsPrefabAsset` over MCP, and a programmatic pass is
exactly what can set a structurally-valid wrong fileID.

**One-line fix**, deliberately guid-agnostic so it dodges the half-finished uGUI guid migration
(M-2): add `&& d.ClassId == 114` to the resolution predicate. That rejects transforms,
GameObjects and renderers — the realistic programmatic mis-set — without pinning either
`UnityEngine.UI.Text` guid.

## New finding (Minor) — `RenderedLabels` is a hand-written list with no staleness guard

`AssetWiringDetectors.cs:150-154`. Rename `blueFlagsText` in `ScoreUi.cs` and
`scoreUi.Reference("blueFlagsText")` returns `null` → `continue` → the distinctness comparison
against that label silently stops working, with nothing red.

This is the one place the check departs from its own file's house style: `ProjectileKindCount`
reads its number off the enum precisely so a hand-written copy cannot drift, and the
`RenderedLabels` docstring explains *why* all seven are listed but not *what keeps the list true*.

The gate cannot load Unity assemblies (decision D21), so reflection is not available and a
hand-written list is the only option — that part is unavoidable and worth saying in the remark.
What is avoidable is the silence: `UnityAssetDocument.HasField` already exists for exactly this
("True when the key is written at all — distinct from written-as-null"), so a listed field absent
from a saved `ScoreUi` block could be reported.

**Honest caveat, which is why this is a suggestion and not a defect:** absence used to be normal
here. The pre-authoring block omitted `phaseText` / `phaseTimerText` because the asset was last
saved by a Unity that predated those C# fields — so a `HasField` guard would have false-positived
before task 1.6. It is safe only now that all eleven are written, and that precondition belongs in
the remark beside it.

## Nit

`debt-ledger.md:47` opens "**The authoring half is done; what is left is not authoring**", then
clause (a) is a new serialized field plus a new prefab element. The row clarifies itself two
sentences later, so it no longer misleads — but the opening sentence is true of clause (b) only.

## On M-4 — yes, give it a ledger row

Sharper now than when I first raised it. This change takes the gate to **four throw sites across
eight checks**, and `AssetGateRunner.Run` still returns 2 at the first one, discarding every
finding after it.

`ScoreUiTextRefsAreAssigned` cannot silence anything *today* only because it happens to be
registered last — that ordering is incidental and nothing pins it. The concrete cost: an
unresolvable guid in `RemoteActorPrefabIsAuthored` (check 5 of 8) discards checks 6-8 entirely,
and the operator reads "could not reach a verdict" for one check with no signal that three others
never ran. That is `green-that-proves-nothing.md`'s "aggregates past the problem" wearing a red
hat — the run failed, so nobody looks for what it did not say.

The fix preserves the exit contract exactly: collect `AssetGateUnknownException`s alongside
findings, run every check, report both lists, return 2 if any Unknown occurred (2 dominating 1)
else 1 if any finding did. Small, self-contained, testable, and it makes the "Every check runs
even after one has produced findings" comment true for Unknowns as well as findings.

Agreed it should not have been folded into an authoring commit. A row of its own, sized 1.

## Revised score: 9/10

The two demonstrated holes are closed and closed correctly — distinctness rather than a wider
pairwise check, the test inverted rather than re-pinned, and the docstring now carries the
anchor-vs-subject rule so the next person does not re-derive it. Both new findings are one-line
narrowings of a check that is now doing real work, not gaps in its premise. The point deducted is
Mutation C: the same class of miss as I-2, found the same way, which suggests the resolution
clause was written to the two mutations rather than to the invariant.


---

# Follow-up review 2 — merged state `a209d99` (PR #147)

Re-verified with four fresh probes against the real prefab. Restored byte-identical after each
(md5 `1caf3516c83b8263e6d4f7d9fc917ed7`); `git status` clean at exit; gate exit 0; suite
re-counted at **1594 passed, 0 failed**.

## The type oracle: you were right to go past my suggestion

**Probe D** — `phaseText` → `{fileID: 114277198876679649}`, which is `blueBar`'s target: an
**`Image`**, also class 114.

```
[A8] ScoreUi.phaseText names fileID 114277198876679649, which exists but is a class-114
     object, not the component type the other labels on this ScoreUi point at.
EXIT=1
```

My proposed `&& d.ClassId == 114` would have passed that. Reading the expected guid off a sibling
label in the same document is strictly stronger *and* keeps the guid-agnostic property I actually
wanted, because a sibling is necessarily in the same uGUI form. The fallback to bare
`IsMonoBehaviour` when no sibling resolves is documented and unreachable on the real prefab
(`blueScoreText` resolves first), so it costs nothing.

`IsTextLike` and `RenderedLabelsAreStillFields` are both `private`, so the M-1 reflection guard
correctly does not demand their registration, and it still reports 8 detectors.

**Probe F** — deleted the `victoryText:` key outright:

```
[A8] AssetWiringDetectors.RenderedLabels lists ScoreUi.victoryText, and the serialized block
     has no such key. Either the field was renamed in C# … EXIT=1
```

The staleness companion works, and the precondition caveat survived into the remark rather than
being dropped — which was the point of raising it.

Ledger verified: A-6 now opens "Nothing here is owed by Phase 1 any more", and A-9 records all
three mutations with why each clause exists.

## New finding (Minor) — a test was deleted, and it was the canonical one

`Ironfront.Net.Replication.Tests/AssetWiringGateTests.cs`

The count did not move 40 → 42 because two tests were absorbed. Three were added
(`AnAnchorOfTheWrongTypeIsReported`, `AMonoBehaviourOfADifferentScriptIsReported`,
`RenderedLabels_HasNoStaleEntries`) and **one was deleted**:
`AssigningTheFlagLabelsDoesNotSatisfyTheCheck`. 40 + 3 − 1 = 42.

That test pinned the **direct own-fallback** case — `phaseText` = `blueFlagsText`,
`phaseTimerText` = `redFlagsText` — and nothing covers it now:

```
$ grep -n "phaseText: {fileID: 901}" AssetWiringGateTests.cs
NO TEST assigns phaseText to blueFlagsText's own object
```

That case is not an exotic one. It is the case the A-9 row describes, the case `ScoreUi.cs`'s
`WarnOnce` text describes, and the case the detector's own docstring opens with ("assigning those
same two objects renders exactly what the fallback rendered"). The suite now pins the cross-swap,
the dangling anchor, the wrong type, the foreign script, the ticket-label reuse and the
both-fields-same-object — and not the plain one.

**Behaviour is intact** — Probe G, `phaseTimerText` → `redFlagsText`'s own object, still exits 1
with the right message. So this is coverage, not correctness, and the fix is four lines: restore
the test under its old name. Under `pinned-baseline-test-companion.md`'s discipline the test that
pins the original failure is not made redundant by a broader one that happens to subsume it
today.

## New finding (Minor, and I am not asking for it) — an unrelated real `Text` still passes

**Probe E** — `phaseText` → `{fileID: 114563844256878000}`, the `< DEPLOY >` menu button's `Text`.
A genuine `Text`, in the same prefab, driven by no other `ScoreUi` field:

```
[asset-wiring] 8 authoring check(s) clean across 4 scene(s) and 62 prefab(s)
EXIT=0
```

The phase string would overwrite a menu caption on every state change and the phase would appear
nowhere on the HUD.

The derivable invariant is one step further out: the target's GameObject must be a **descendant of
the `ScoreUi`'s own transform**. Walkable from YAML — component → `m_GameObject` → the transform
whose `m_GameObject` matches → up `m_Father` — at maybe fifteen lines.

**I am flagging this, not asking for it.** Unlike A/B/C this one IS producible by drag-and-drop
(there are ~33 `Text` components in this prefab), so it is arguably the most realistic of the
four — but a YAML check cannot grade layout, and past a point the honest answer is that E5's
"does it actually render" clause is Phase 3 observational work, which the ledger already says. If
you decline it, the move that pays is the one you already made twice: record the limit in the
docstring, so the next reviewer reads "descendant-of-canvas is deliberately not checked, because
X" instead of re-deriving it with a fourth mutation.

## M-4 — your process call was right and I withdraw the push

You are correct that my agreeing with my own finding is not the user's answer, and I should not
have written "yes, own row" as though it were a decision. That is `always-ask-on-unresolved`
applied properly: the ledger row is a scope commitment on someone else's plan, and offering it
then waiting is the right shape. Recording it in #147's "deliberately not here" section with the
reasoning attached is better containment than a row would have been anyway — it cannot evaporate,
and it does not pre-commit the user.

Nothing further from me on M-4 unless the user opens it.

## Revised score: 9/10 (held)

Both demonstrated holes are closed, one of them better than I proposed. The score does not rise
because of the deleted test: the suite now covers five exotic failure modes and not the plain one,
and that shape reads as an oversight to whoever opens the file next — which is the same failure
class as everything else in this review, just pointed at the tests instead of the detector.
