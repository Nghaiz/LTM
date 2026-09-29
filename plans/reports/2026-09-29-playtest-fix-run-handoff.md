# Handoff: playtest 2026-09-28 fix run (paused 2026-09-29)

The owner paused the run to shut the machine down. Resume from here when told to continue.

## The request

Fix every item from the 2026-09-28 playtest list, fully automated (cua-driver is available).
One PR per item, merged into `develop` before the next starts. CI does not gate each merge: look at CI
only at the end, or when a deploy or release needs it. Keep the game servers (and the master, if it
changes) on the latest build. When everything is merged: update `main` and release **v1.1.0**.
Single-threaded, no subagents.

Standing constraints: no `Co-Authored-By` trailer and no "Generated with" line in commits or PRs
(owner is the sole author). PRs base on `develop`. Merge with
`gh pr merge N --squash --admin --delete-branch`. Test only against the fly master and the Azure
game servers, never local servers. Check free commit memory before any Unity run.

## Done (merged to develop)

| Item | PR | Squash |
|---|---|---|
| Bug 1: a crew killed with its helicopter stays dead on screen | #360 | b575182 |
| Bug 2: hitboxes and lag compensation (whole-trip rewind, 400 ms cap, passenger origin, hull occlusion in the rewound frame) | #361 | 7514e9a |
| Extra: the menu works again after leaving a match | #362 | 8867931 |
| Bug 3: territory award every 5 s, 20-minute time limit, clock on screen | #363 | d57205e |
| Bug 4: a thrown medipack stays on its floor; heal and resupply feedback | #364 | 44a0148 |

The Azure game servers run image **7514e9a** (bugs 1 and 2). Bugs 3 and 4 and both features still
need a server deploy. The master on fly has not changed and needs no redeploy so far.

## In progress: feature 2, the killfeed says who, what and how

Branch `feat/killfeed-how`, one WIP commit. **The tree does not compile yet**: `ReportDeath` has not
been extended and the plugin DLLs have not been rebuilt. Do not open Unity until it compiles, or it
greets you with the Safe Mode prompt.

Done in the WIP commit:

- `DeathMessage` gains an optional 3-byte tail: `WeaponId`, `VehicleType`, `DeathDetail`. Old
  clients parse the first 12 bytes and ignore the rest, so there is no `PROTOCOL_VERSION` bump, and
  a 12-byte message still parses as a legacy death.
- `DeathDetail` flags in `GameplayEnums.cs`: `KillerInVehicle`, `VictimInVehicle`,
  `WentDownWithVehicle`, `Melee`.
- `ServerEventWriter.WriteDeath` sizes its buffer for the tail.
- `KillfeedEntry` carries the three new fields plus `Self`.
- `KillfeedWording` (engine-free): the label between two names for a kill, and a sentence for a
  death nobody scored ("drowned", "went down with the Helicopter", "crashed the Jeep").
- `DeathContext` (Assembly-CSharp): a scope the damage call sites open. Wrapped so far:
  `Projectile.Hit` (weapon), `Weapon.SpawnProjectile` sets `sourceWeaponId`, the explode calls in
  `ExplodingProjectile` and `GrenadeProjectile`, the actor loop in `ActorManager.Explode`
  (explosion), the crew in `Vehicle.Die` (went down with), `MeleeWeapon` (melee). The death branch
  of `Actor.DamageAttributed` already calls the extended `ReportDeath`.

The shipped server has `AuthoritativeFlight` off (it is never assigned true), so every projectile
death runs through `Projectile.Hit` and the scopes above see it. Player hitscan deaths go through
`ServerCombatBridge.EmitDeath` instead.

Remaining, in order:

1. `ServerCombatEvents.ReportDeath(victim, force, attacker, cause, byte weaponId, GameObject vehicle, DeathDetail detail)`.
   - `vehicle != null`: the type comes from `ServerVehicleRegistry.NetworkIdOf` then
     `TryFind(...).NetworkTypeId`. The killer is `attacker`, else the vehicle's recent attacker
     from the damage sink (about 30 s window: the burn time sits between the killing hit and `Die`).
   - A killer seated at a mounted weapon, or driving, gets `KillerInVehicle` and their vehicle's
     type (`VehicleRegistry.TryFindSeatOf`). A passenger firing a hand weapon must not. Find how a
     mounted weapon is identified first.
   - A seated victim gets `VictimInVehicle`, and the vehicle type when none is set yet.
2. `ServerVehicleDamageSink.ApplyDamage`: record the last attacker and tick (the attacker id is 0
   when unknown), and add `TryGetRecentAttacker`.
3. `ServerTickLoop.EmitDeath(..., byte weaponId = NONE, byte vehicleType = NONE, DeathDetail detail = None)`
   builds the 10-argument `DeathMessage`.
4. `ServerCombatBridge.EmitDeath` passes `killer.WeaponId` (and `KillerInVehicle` for a seated shooter).
5. Melee credit: `MeleeWeapon` calls `component.parent.Damage`, which carries no attacker, so a
   melee kill reads "The world". Call `DamageAttributed(..., user)` instead. That closes the melee
   half of the gap in memory `bot-kills-are-never-credited`.
6. Roadkill credit: find the vehicle-collision damage path; the same memory says physics collisions
   report no killer.
7. Client: turn the `IMatchHud` killfeed row API into a struct; build a runtime killfeed view in
   `MatchHud` (dark translucent rows, team colours, a weapon chip, headshot mark, the local player
   highlighted, slide in and fade out); `NetClientCombatPresenter.PushKillfeed` uses `KillfeedWording`.
8. Document the `S_DEATH` tail in `plans/00-shared/protocol-spec.md` §4.6.
9. Tests: `DeathMessage` write and parse at 12 and 15 bytes, the legacy parse, `KillfeedWording`
   cases. Mutation-test each one.
10. `pwsh tools/build-libs.ps1`, then assets-refresh, Unity compile and EditMode tests. Commit
    `DeathContext.cs.meta` once Unity has generated it.
11. PR to `develop`, merge.

## Pending: feature 1, overhead nameplates

A name above a thicker health bar over every player's head, with humans and bots told apart. Bigger,
readable text. Teammates within range; enemies only when visible and in range. A new client
presenter, its own PR.

## Then

- Deploy the game servers: build the Linux server in the Editor
  (`Ironfront.EditorBuild.BuildDedicatedServer` via delayCall, bring Unity to the front with
  cua-driver), `docker build -q -f infra/docker/gameserver.Dockerfile -t ironfront-game-server:<rev> build/server`,
  save `docker logs -t` from the VM first, then
  `pwsh tools/deploy-gameservers-azure.ps1 -Image ironfront-game-server:<rev>`.
- Final live test against the fly master and Azure, with two clients (a room needs two): seated
  hits, a medipack on the farmhouse upper floor plus the heal feedback, the score clock and the
  territory award, killfeed wording, nameplates, returning to the menu.
- Check CI on `develop`, promote `main`, then `build-player.ps1` and `package-release.ps1 -Publish`
  for v1.1.0 (MINOR: protocol-compatible). Test the extracted zip, not `build/windows`.
- Cleanup: drop `stash@{0}` (diag-temp instrumentation from the bug 1 repro).
- Final report to the owner, in Vietnamese: the original scoring rules and what changed (PR #363
  body), how medipacks and ammo bags work (every 3 s, 6 m radius, +30 HP up to 100, 5 s off the
  pack's lifetime per heal, both teams), and the practice-mode role issue (ledger X-10) seen while
  testing offline scoring.
