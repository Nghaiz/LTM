# A10 mutation evidence — P19 criterion 9

`tools/ClientWiringGate` check `EveryMapSceneCarriesNetcode`. A detector that has only ever
been green on a map that was already authored has proved nothing, so each clause below was
made to fail against the real `Island.unity` and then restored. The scene file was backed up
to the scratchpad and copied back after each run — not `git checkout`, which on the first
attempt silently reverted the uncommitted authoring along with the mutation.

## Before the authoring — RED, unprompted

`plans/reports/harness/p19/gate-red-before-authoring.txt`, exit **1**:

```
[A10] map 2 (Island) has no NetServer object …
[A10] map 2 (Island) has no NetClient object …
[A10] map 2 (Island): no LevelBounds in this scene …
```

Zero findings against map 1, which is what makes it a discrimination rather than a blanket
complaint. The nine checks that predate A10 reported map 2 clean in the same run.

## After the authoring — green

`[asset-wiring] 16 authoring check(s) clean across 4 scene(s) and 63 prefab(s).` exit **0**.

## The four mutations

| Mutation | Clause | Gate said | Exit |
|---|---|---|---|
| `NetClientCombatPresenter._registry` → `{fileID: 0}` | null single reference | `NetClientCombatPresenter._registry is null (fileID: 0).` | 1 |
| swap kinds 0 and 1 in the **client's** `_prefabsByKind` only | the two arrays agree element by element | `kind 0 is prefab 317e41fe… on the server and 527c1bd5… on the client. The server will spawn one and every client will draw the other, and nothing errors.` (and the same for kind 1) | 1 |
| `ServerSnapshotStage`'s `m_GameObject` → `{fileID: 0}` | full script roster on the root | `ServerSnapshotStage is not on the NetServer object (&1940902172). no snapshot is ever framed, so every client sees an empty, frozen world.` | 1 |
| the Level Bounds `MeshRenderer`'s `m_GameObject` → `{fileID: 0}` | `LevelBounds` object carries a Renderer | `the LevelBounds object (&1172781992) carries no Renderer. LevelBounds.Awake calls GetComponent<Renderer>().enabled = false and will throw before the play volume is installed…` | 1 |

Restored after each: `git status` reports `Island.unity` unmodified and the gate returns to
16 checks clean, exit 0.

## What these four do NOT prove

The swap mutation is the only one whose clause is new to the tree; the other three overlap
with checks A1–A6 in scope while differing in *which scenes they visit*, which is the whole
point of A10 and is exactly the thing a mutation on an already-authored scene cannot show.
The evidence for that half is the RED run above, taken before the authoring existed.

Two clauses are **not** mutation-covered here: the missing-scene finding (a `MapCatalog` row
naming a scene that does not exist) and the dangling-guid branch of `GradeOne`. Both would
require editing `MapCatalog.cs` or deleting a prefab, and neither was exercised — stated
rather than left for a reader to assume.
