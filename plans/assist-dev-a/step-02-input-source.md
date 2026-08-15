# Step 02 — `IInputSource` and the shadow compare

**Feeds** Dev A phase-00 task 3 · **Session size** large · **Editor needed after** Dev A plays 5 min

> Goal: every gameplay input in `FpsActorController` arrives through an interface, so a networked
> controller can supply one — without changing what single-player feels like by one frame.

---

## Why this is the highest-value item here

Phase-00 objective 2 says it outright: *"Required for `NetworkActorController` to exist at all"*. The
netcode currently runs beside the game because there is no seam to plug it into. This is the seam.

It is also smaller than the plan implies. 85 `Input.*` hits sounds like a sweep of the whole game;
they are not evenly spread:

```
FpsActorController.cs   37   ← the entire job
SpectatorCamera.cs       9   ┐
PathTypesDemo.cs         9   │ phase-00 criterion 6 permits these to stay
ObjectPlacer.cs          4   │ ("leaves only UI/debug hits")
CommandRoomCamera.cs     4   │
GroupController.cs       4   │
MainMenu.cs              3   │
WeaponManager.cs         2   ┘
```

One file. The other 48 hits are spectator, editor tooling and menus, which criterion 6 explicitly
allows to keep using `Input` directly.

## Deliverable

1. `IInputSource` — the interface. [Phase-00 task 3](../dev-a-unity-client/phases/phase-00-foundation.md)
   gives the member list, and the table inside it gives the per-line translation for all 37 sites.
2. `LocalInputSource` — reads `Input.*` exactly as the current code does, including
   `Input.GetAxis("Mouse X/Y")` moved into `Sample()`.
3. `FpsActorController` rewritten against `_input`, with **no behavioural change**.
4. **`InputShadowCompare`** — the safety net, and the reason this step is takeable at all.

## The shadow compare

The risk is precise: 37 substitutions, made without ever running the game, against phase-00
criterion 5 — *"single-player still plays exactly as before the refactor"*. A wrong axis sign or an
inverted `&&` produces a game that runs and feels subtly wrong, which is the worst failure mode
available.

The repo already solved this shape of problem once. `Net/Shared/MovementShadowCompare.cs` runs the
shared movement simulation beside the original code and logs where the two disagree. Do the same
thing for input: sample the old expression and the new `_input` value in the same frame, compare, and
log the first disagreement per site with the site's name.

That turns "Dev A has to eyeball 37 diffs" into "Dev A plays for five minutes and the console is
either silent or names the exact site". Keep it behind the same kind of toggle
`MovementShadowCompare` uses, and delete it in a follow-up once Dev A confirms silence.

## Constraints

- **Do not touch the 48 permitted hits.** Criterion 6 wants them left alone; touching them widens the
  blast radius for no gain.
- **No behaviour changes smuggled in.** If a line looks wrong, leave it wrong and note it. This step
  is a refactor and nothing else — `coding-guidelines.md` § 3.
- `Assembly-CSharp/` is Dev A's under `conventions.md` § 7. This is lead-authorised assist work; say
  so in the PR body.

## What this step proves, and what it does not

**Proves:** it compiles, and `dotnet build` stays warning-free.

**Cannot prove:** that the game feels identical. There is no automated path to criterion 5 — the
shadow compare converts that from a judgement into an observation, but somebody still has to play.

**Dev A checks:** run, shoot, crouch, lean, switch weapons, enter a vehicle, die, respawn — five
minutes, console open. Silence means the substitution set is right.

## Done when

- `grep -rn "Input\." Assets/Scripts/Assembly-CSharp/FpsActorController.cs` returns only what
  `LocalInputSource` owns
- `InputShadowCompare` exists and is documented as temporary
- Merged and green, with the Dev A verification step named in the PR body as outstanding
