# Lane B, first run where the clients stayed connected — and what became visible

- **Date:** 2026-08-21 · **Branch:** `develop` @ `fef4fe7` + the recorder fix below
- **Runs:** `artifacts/lane-b/smoke-fix01`, `artifacts/lane-b/combat-fix01`
- **Supersedes the blocker in:** [`2026-08-21-laneb-blocker-reliable-ack.md`](2026-08-21-laneb-blocker-reliable-ack.md)
- **Verdicts delivered: still 0 of 11** — for reasons that have nothing to do with the blocker,
  and that no run could previously have exposed. § 4 grades this honestly rather than partially.

---

## 1. The blocker is closed, and here is the evidence rather than the claim

Every previous lane-B run lost all three clients to `TransportError` about a second after they
joined. Both runs today, on a player rebuilt from `develop`:

| Signal | Before (this morning) | Now |
|---|---|---|
| `reliable sequence N abandoned after M resends` | every run, all three clients | **0** |
| `conn N left (TransportError)` | all three, inside ~1 s | **0** |
| Actor ids | server re-issued **41** to each arriving client, because nobody was left holding it | **41 / 42 / 43**, one per client |
| How the clients left | dropped | `LocalRequest` — they finished their programme and quit |
| Driver across all 7 checkpoints | `conn=0 actor=0 rtt=0` | `conn=1 actor=41`, rtt **19.1 → 24.9 ms** |
| `lostConnection` / `connectedAtFinish` | true / false | **false / true**, all three, both runs |

An RTT that moves is the part worth pausing on: it is only computable from acks the server
actually received, so it is the one number in this table that could not be produced by a
connection that merely *looks* alive.

## 2. What the run then showed, which is not a pass

The combat set ran to completion — three clients, seven checkpoints each, exit 0 — and **nothing
in it happened**:

| Field (at every checkpoint, all three clients) | Value |
|---|---|
| `combat.health` / `combat.alive` | `100` / `true` |
| `combat.weaponId`, `clipSize`, `ammoInClip` | `0`, `0`, `0` |
| `combat.predictedShots`, `hitmarkerHits` | `0`, `0` |
| `combat.killfeedTotalKills`, `killfeed`, `namedPlayers` | `0`, `[]`, `0` |
| `aim.requested` / `aim.resolved` / `aim.targetActorId` | `"OBS-A"` / `false` / `0` |

These are read from `ClientCombatState driver.State` — the authoritative client state, not a
vehicle-scoped view (§ 3 is about why that sentence needs saying). So check 1 (E7 — fire, hit,
kill, killfeed line **with a name**) is a genuine **FAIL**: the shooter carries no weapon and its
scripted aim never resolved a target, so no shot was fired and nobody died.

That is a *useful* failure. It is the first time the question could be asked at all.

## 3. Five fields that read as "replication is dead" and mean "no vehicle in this programme"

The same record also showed `snapshotsApplied: 0`, `inputsSent: 0`, and four zeroed `interp*`
fields. Read at face value that says the client applied no snapshots and buffered nothing — a
dead replication stage. **It cost a real investigation before two facts contradicted it:**
`remoteActorCount: 55`, and the client's own log line `[net] first snapshot applied at server
tick 143`.

Every one of those readings came from the vehicle path:

```csharp
Num("snapshotsApplied", client.Router.VehicleSnapshotsApplied);   // ← vehicle
Num("interpBuffered",   client.Router.VehicleInterpolator.Count); // ← vehicle
Num("inputsSent",       stage.InputsSent);                        // ← ClientVehicleStage
```

On an on-foot programme every one of them is **correctly** zero, and `occupiedVehicleId: 0`
confirms it. This is the mirror image of a green that proves nothing: a red that proves nothing,
and the more dangerous direction, because a false alarm sends someone to debug a healthy system.

`snapshotsApplied` was worse than vague — the name was already **taken**.
`NetVerificationHarness.cs:578` publishes it from `Router.SnapshotsApplied`, the actor-stream
counter. Two harnesses, one key, two meanings, and nothing in the artifact to tell you which one
you were holding.

**Fixed.** The vehicle readings are now `vehicleSnapshotsApplied`, `vehicleInterpBuffered`,
`vehicleInterpNewestTick`, `vehicleInterpStalled`, `vehicleInterpReordered`, `vehicleInputsSent`,
`vehicleStarvedFrames`; `snapshotsApplied` now carries `Router.SnapshotsApplied`, matching the
other harness. Pinned by
`TheCheckpointRecorderNamesVehicleCountersAsVehicleCounters`, which asserts both directions — the
vehicle names present, the generic ones absent — and was **mutation-verified**: reverting
`vehicleInputsSent` to `inputsSent` turns it red, restoring it turns it green.

## 4. Why 0 of 11, stated plainly

Two independent reasons, and only the second is about today's run:

**Only three of the eleven checks have a programme to run.** `tools/lane-b/` holds `smoke.json`
and the three `combat-*.json` files. Nothing else exists:

| # | Check | Programme | Status |
|---|---|---|---|
| 1 | E7 — fire, hit, kill, killfeed with a name | `combat` | **FAIL** (§ 2) |
| 2 | E8 — HUD reflects authoritative state | `combat` | not graded — the match never left `0–0` |
| 13 | death → input disable → respawn screen | `combat` | not graded — nobody died |
| 3 | E9 — capture point owner on both clients | — | **unwritten** |
| 4 | E10 — grenade detonates in the same place | — | **unwritten** |
| 5 | E11 — A16 camera hijack | — | **unwritten** |
| 6 | E12 — scene ordering | — | **unwritten**, and blocked at source by **X-1** |
| 7–9 | vehicle: three clients, 100 ms RTT / 5 % loss | — | **unwritten** |
| 12 | turret parity | — | **unwritten** |

So no `B-*` ledger row is closed by this run, and none should be. Authoring the missing
programmes is now ordinary work rather than impossible work, which is the whole change.

**And the one set that does exist does not arm its shooter.** Before any further programme is
written, `combat` needs the driver to hold a weapon and its aim to resolve — otherwise every set
built on the same scaffolding inherits a shooter that cannot shoot.

## 4b. Why the shooter cannot aim — four candidates eliminated, one open

`aim.resolved: false` is downstream of one number: **`namedPlayers: 0`**.
`ScriptedTargetSolver.ResolveActorId` resolves a name by scanning
`presenter.Names` (`PlayerNameTable`), so an empty table cannot resolve anything, and E7 does not
merely need a kill — it needs a killfeed line **with a name**.

Traced end to end. Four plausible causes are eliminated by reading the tree, not by argument:

| Candidate | Status |
|---|---|
| The server never sends a player list | **Eliminated.** `ServerTickLoop.EmitPlayerList` writes it via `ServerEventWriter.WritePlayerList` and dispatches with `BroadcastReliable` — to everyone, on the reliable channel. |
| Our actor ids are dropped by the u8 narrowing | **Eliminated.** `EmitPlayerList` skips `actorId > byte.MaxValue`; this run's ids are 41, 42, 43. |
| The client never routes it | **Eliminated.** `ClientMessageRouter` handles `ServerMessageType.PlayerList` (`:353`) and raises `OnPlayerList` (`:183`). |
| Nothing subscribes — the `NetLog` shape | **Eliminated.** `NetClientCombatPresenter:104` does `_client.Router.OnPlayerList += _names.Apply`, and the script IS in the scene: guid `bc6c11e3…` appears in `Assets/Scenes/Dustbowl.unity`. |

That last row also **retires part of ledger row X-1**, which reads "none of the nine presenter
scripts is referenced anywhere". At least `NetClientCombatPresenter` is referenced now; the row
needs re-verifying per script rather than being carried forward as a block.

> **RESOLVED 2026-08-21 — and both candidates below were wrong.** The cause was neither
> `EmitPlayerList`'s cadence nor the scene-strip: `NetContext.Role` read `Server` for the whole
> `Awake` pass of a client process, so `NetClientCombatPresenter` disabled itself before it could
> subscribe. The same window killed the combat driver, so the two threads this section says not to
> assume share a cause **do** share one. Full account, evidence and fix:
> [`2026-08-21-lane-b-role-race.md`](2026-08-21-lane-b-role-race.md). Ledger **X-9**.

**Still open, and deliberately not guessed at:** `EmitPlayerList` fires on join and on leave and
nowhere else, so a client that subscribes after the last join receives nothing until somebody
leaves. Whether that is the cause here — or whether the harness's scene-strip removes the
subscribing object on a client process, since every client boots as a listen server first
(`[net] role = Server` precedes `[net] role = Client` in every client log) — is the next
measurement. Both are cheap to distinguish: log the arrival of `OnPlayerList` per process.

The missing weapon (`weaponId: 0`, `clipSize: 0`) is a separate thread from the missing names and
should not be assumed to share a cause.

## 5. Next, in order

1. ~~Log `OnPlayerList` arrivals per process to settle § 4b's one open candidate, and separately
   find why the scripted client spawns with `weaponId: 0`.~~ **Done 2026-08-21, and the answer was
   one cause for both** — see [`2026-08-21-lane-b-role-race.md`](2026-08-21-lane-b-role-race.md).
   No logging was needed; the ordering was already in `driver.log`. Both are fixed, and what they
   were hiding is that the scripted client never spawns into the map at all.
2. Re-run `-Set combat`; grade 1, 2 and 13 against `phase-3-harness.md` § 2.
3. Author the missing sets — capture, grenade, camera, vehicle, turret — reusing the combat
   scaffolding once it is proven by (2).

## 6. One caveat that must travel with every verdict from this runner

`run-lane-b.ps1` runs the server as a **Windows player**. The product's server is the Linux
dedicated build. The runner's own header says it: a verdict reached here describes the **game**,
not the deployment target. Any check that turns on server-side floating point or platform
behaviour has to be re-read on Linux before it is trusted — and
[`tools/local-server-smoke.sh`](../../../tools/local-server-smoke.sh) is now the way to get a real
Linux server up to do it.
