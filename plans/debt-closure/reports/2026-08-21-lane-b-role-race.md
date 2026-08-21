# The role race — one cause behind five dead presenters, and what it was hiding

- **Date:** 2026-08-21 · **Branch:** `develop` @ `dc4fd73`, `4e31ac6`
- **Runs:** `artifacts/lane-b/combat-role01` (role fix), `artifacts/lane-b/combat-roster01` (+ aim fix)
- **Continues:** [`2026-08-21-lane-b-first-connected-run.md`](2026-08-21-lane-b-first-connected-run.md) § 4b,
  whose one open candidate this closes — and which named the wrong two suspects.
- **Verdicts delivered: still 0 of 11.** Three blockers down, one new one found, and it is
  bigger than any of them. § 5 states it plainly rather than counting the progress.

---

## 1. § 4b's open question, answered — and both of its candidates were wrong

The previous report eliminated four causes for `namedPlayers: 0` and left one open: either
`EmitPlayerList` fires only on join and leave (so a late subscriber hears nothing), or the
harness's scene-strip removes the subscribing object on a client process. It said the way to
settle it was to log `OnPlayerList` arrivals per process.

Neither was the cause, and no logging was needed — the ordering was already in the artifact:

```
combat-fix01/driver.log:70    [net] role = Server
combat-fix01/driver.log:160   [lane-b] stripping NetServer ('NetServer') before Start
combat-fix01/driver.log:173   [net] role = Client
```

`Dustbowl.unity` carries an **active** `NetServer` (&629676505) and an **active** `NetClient`
(&629676520), both at `DefaultExecutionOrder(-1000)`. Only the client deferred —
`NetClientBootstrap.cs:146`, `if (!NetContext.IsServer)` — so the server won every tie and the
role read `Server` for the entire `Awake` pass of a **client** process. `sceneLoaded`, where the
harness pinned the role, runs after every `Awake` by construction; it could never have helped,
and the class remark claiming it "pins `NetContext.Role`" was wrong in the one way that mattered.

`NetClientPresenterGuard.IsPresentable` is `NetContext.IsClient`. Every presenter checks it in
`Awake`, and the failing branch sets `enabled = false` and returns **with no log**. `OnEnable`
never runs, so the subscription never happens, and nothing re-checks for the rest of the
process's life.

**Two symptoms, one cause**, which the previous report explicitly warned against assuming — the
right call on the evidence it had, and wrong on the evidence the log ordering supplies:

| Symptom | Mechanism |
|---|---|
| `namedPlayers: 0` | `NetClientCombatPresenter` never reached `OnEnable`, never subscribed to `OnPlayerList` |
| `weaponId: 0`, `clipSize: 0`, `predictedShots: 0` | `NetClientBootstrap.Awake` `AddComponent`s the combat driver, which runs that component's `Awake` **inline** — inside the same window |

It was never only those two. **Five** presenters plus the driver share the guard —
`NetClientProjectilePresenter`, `NetClientExplosionPresenter`, `NetClientObjectivePresenter`,
`NetClientCombatPresenter`, `NetClientLocalCombatDriver` — so checks 3 (E9), 4 (E10) and 6 (E12)
were never going to work either, for a reason no programme of theirs could have exposed.

**Fixed** at `dc4fd73`: `NetServerBootstrap` defers to a *declared* client, mirroring the
client's own guard, and `LaneBHarness.DeclareRole` declares at `BeforeSceneLoad` — ahead of every
scene `Awake`. With no declaration the role is `Offline` there and the server still claims it, so
the Editor sandbox and the dedicated build are unchanged. Ledger **X-9**.

**Proven, not asserted.** In `combat-role01/driver.log` the string `role = Server` appears
**zero** times, `role = Client` at line 53, and every checkpoint carries
`driverEnabled: true, presenterEnabled: true` and `namedPlayers: 3`.

## 2. A red that proves nothing, made impossible

The reason § 4b spent four candidates on this is that the failing guard is **silent**. Both
components are found with `FindObjectsInactive.Include`, so the artifact never said `absent` — it
said `weaponId: 0` and `namedPlayers: 0`, which read identically to "the player has not fired
yet" and "nobody has joined".

The checkpoint now carries `driverEnabled` and `presenterEnabled`. One boolean each, and that
particular ambiguity cannot recur.

## 3. The scripted aim asked for a name the server has never heard of

With the presenter alive, `namedPlayers: 3` — and `aim.resolved` was **still false**,
`targetActorId: 0`. Three names in the table, none of them the one all three programmes ask for.

The server does not know `"OBS-A"`. It never parses the join ticket, so the only identity it
holds is the transport's `PlayerId`, and `ServerTickLoop.DisplayNameFor` renders that as
`"#5002"`. That is deliberate, and `ServerPlayer.DisplayName` already documents why at length:
carrying a real username needs a new opcode, and acceptance criterion 2 forbids moving
`PROTOCOL_VERSION`. **So the harness is the side that was wrong**, and the server is left alone.

`run-lane-b.ps1` now exports `IRONFRONT_LANEB_ROSTER` from the name-to-id pairing it already
owns; `ScriptedTargetSolver` tries the literal name first and falls back to `"#<playerId>"`.
Literal-first, so a deployment whose players carry real usernames is unaffected. The roster lives
in the runner rather than in the three `combat-*.json` files, which would couple every recorded
programme to those magic ids and rot the day they change. `4e31ac6`.

`combat-roster01`: `aim.resolved: true`, `targetActorId: 42` — OBS-A, correctly.

## 4. And then the artifact said what had been underneath all of it

Three checks in, the same record carries this:

| checkpoint | t | phase | localActor y | x, z |
|---|---|---|---|---|
| spawned | 5.0 s | Warmup (2) | 996.73 | 0, 0 |
| approach | 7.0 s | Warmup (2) | 995.46 | 0, 0 |
| in-range | 27.0 s | **Ended (3)** | 982.09 | 0, 0 |
| firing | 30.0 s | **Ended (3)** | 979.55 | 0, 0 |
| killed | 38.0 s | **Ended (3)** | 974.10 | 0, 0 |
| victim-input-held | 43.0 s | **Ended (3)** | 970.58 | 0, 0 |
| respawn-window | 48.0 s | Warmup (3) | 967.44 | 0, 0 |

**The scripted client is never spawned into the map.** It sits at the world origin at ~980 m and
descends monotonically for the whole programme — a body falling in empty space. `aim.distanceM`
is `0.044` and `aim.pitch` is `-89.88°` for the same reason: the target it correctly resolved is
falling beside it, also at the origin. The aim is right; there is simply nothing there.

**The match never reaches Playing.** Warmup → Ended → Warmup, with red holding 5 of 6 flags at
the very first checkpoint — the map's own bots have taken everything before the clients arrive,
and the round ends on domination.

This subsumes the weapon symptom: `SpawnLoadoutWeapons` runs on an actor **spawn**, and these
bodies never spawn. `weaponId: 0` is not the disease.

### A separate real gap, found while ruling that out

`ClientCombatState.EquipWeapon` — the method that exists to consume the `WeaponId` that
`SpawnActorMessage` has carried all along — has **zero production callers**. Scope:
`grep -rn "EquipWeapon" --include=*.cs .` across the whole repository, 2026-08-21: 20 call sites,
**all of them in `ClientCombatTests.cs`**, and every one of them green.

So the local player can only ever learn its weapon from a snapshot delta, and
`DeltaEncoder.cs:208` masks `SnapshotField.Weapon` only when the weapon **or the ammo count**
changes. The client's own firing is what would change the ammo. No weapon → cannot fire → ammo
never moves → the field is never sent → no weapon. It is a closed loop, and twenty passing tests
say nothing about it, because every one of them calls `EquipWeapon` first.

Filed as ledger **X-11**. It is not the cause of this run's zeros, and it will be the cause of
the next ones if the spawn is fixed without it.

## 5. Where this leaves the eleven checks

Honestly: **0 of 11, and the count has not moved.** What has moved is what stands between here
and a verdict.

| Blocker | Before | Now |
|---|---|---|
| Clients dropped seconds after joining | fatal | closed (`90feb08`) |
| Every client presenter disabled in `Awake` | invisible | closed (`dc4fd73`, **X-9**) |
| Scripted aim resolved nobody | open | closed (`4e31ac6`) |
| Client never spawns into the map; match never plays | **hidden behind the three above** | **open, and now the only thing in the way of checks 1, 2 and 13** |
| 8 of 11 checks have no programme authored | open | open |

The last two are ordinary work. The one before them is not yet understood, and I am not going to
guess at it in a report — the measurement is to read `ActorManager.SpawnWave` against the match
phase and find out whether a `Warmup`/`Ended` round spawns anybody at all, and whether a
connection that joins during `Ended` is ever put in a wave.

## 6. Two notes that travel with every verdict from this runner

`run-lane-b.ps1` runs the server as a **Windows player**; the product's server is the Linux
dedicated build, and any check that turns on server-side floating point must be re-read on Linux
(`tools/local-server-smoke.sh`).

And **X-10**: the fix in § 1 reaches lane B because the harness declares a role. Nothing declares
one for a real rendered client, so a shipped client's killfeed, name table and local combat
driver are alive only if Unity happens to `Awake` `NetClient` first. That is deliberately not
fixed here — how a shipped client declares itself is a product decision, not a call to make
inside a harness commit.
