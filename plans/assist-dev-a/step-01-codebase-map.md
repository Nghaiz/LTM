# Step 01 — `docs/codebase-map.md`

**Feeds** Dev A phase-00 task 1 / criterion 1 · **Session size** small · **Editor needed after** none

> Goal: write the map of the original game that phase-00 asks for, so nobody else has to read 169
> files to find out where damage comes from.

---

## Why this is first and why it is free

Phase-00 task 1 is two days of reading with no code at the end of it, and its deliverable is a
document. That is the cheapest thing on this list to hand over and the one Dev A gains the most
hours from. It also front-loads the knowledge steps 02, 03 and 04 need — those three all edit
`Assembly-CSharp/`, and editing it blind is how a refactor breaks single-player.

Nothing here touches Unity, so there is no verification gap: the document is either accurate or it
is not, and every claim in it can be checked with `grep`.

## Reading order

Phase-00 task 1 names the files. Sizes are the plan's, confirm them:

| # | File | Focus |
|---|---|---|
| 1 | `ActorController.cs` (~60 lines) | The abstract method list, in full |
| 2 | `Actor.cs` (~1,188) | `Update()`, `FixedUpdate()`, the ragdoll part, the damage part |
| 3 | `FpsActorController.cs` (~752) | Every `Input.*` — 37 of them |
| 4 | `Weapon.cs` (~561) | `Fire()`, `SpawnProjectile()`, spread |
| 5 | `ActorManager.cs` | `Register`, `Drop`, `Explode`, the spawn-point list |
| 6 | `GameManager.cs` | `StartGame()`, `OnLevelLoaded()` |
| 7 | `AiActorController.cs` (~2,153) | **Skim.** What it consumes, not how it works |
| 8 | `Hitbox.cs`, `Hurtable.cs` | The damage flow |

## Deliverable

`docs/codebase-map.md`, containing the three things phase-00 asks for plus one this track needs:

1. **A mermaid flow diagram**: input → controller → actor → weapon → hitbox → damage.
2. **A table of every `Actor` state that needs replicating** — cross-check against what
   `Ironfront.Net.Replication/WorldSnapshot.cs` already carries, and mark anything present in one
   and missing from the other. That gap list is worth more than the table.
3. **Every place `Actor` calls into a singleton**, with the file and line. Step 03 consumes this
   directly.
4. **Added by this track:** for each of the 37 `Input.*` sites in `FpsActorController`, the
   surrounding condition. Step 02 consumes this, and gathering it while reading is nearly free.

Use the mermaid conventions already in the repo (see `README.md` § "Repository layout") so it renders
the same way.

## What this step proves, and what it does not

**Proves:** nothing executable. It is a document.

**Dev A still checks:** that the diagram matches their mental model. Phase-00 criterion 1 is
*"someone else reads it and understands it"* — that someone is Dev A, and it is not a check this
track can perform on its own behalf.

## Done when

- `docs/codebase-map.md` exists, and every file and line it cites resolves
- The replication-gap list in item 2 is explicit — "nothing missing" is an acceptable answer, an
  absent list is not
- Merged and green
