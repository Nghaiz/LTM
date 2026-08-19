# Phase 3A — the sixteen slots the server said it had, and now has

- **Plan:** [`phase-3a-player-slots.md`](../phases/phase-3a-player-slots.md)
- **Date:** 2026-08-20
- **Branch:** `feat/phase-3a-player-slots`

---

## 1. What shipped

`ServerPlayerSlotPool` builds `Config.MaxConnections` claimable bodies at server start, from the
same AI character prefab and by the same steps `ActorManager.CreateAIActor` uses for a bot. The
startup log now reads the claimable count back out of the registry instead of restating the
configured number, and errors when the two disagree. `NetServerActor.Claim()` suspends the body's
bot brain through a typed `IAiDriver` seam and `Release()` hands it back.

Measured in a live Editor play session, not inferred:

```
claimable=16  totalActors=56  poolSlots=16  maxConnections=16
admitted=16   distinct=16     refusedAtConnection=17
claimedBodyAiEnabled=False    afterReleaseAiEnabled=True
```

Sixteen bodies, sixteen distinct actor ids handed out, the seventeenth connection refused, and the
bot brain off for exactly as long as a connection owns the body. `totalActors=56` is 40 bots plus
the pool, against a `MAX_ACTORS` of 64.

## 2. Two premises in the plan that the code had already moved past

**§ 4.2 was already satisfied on the dedicated path.** The plan says `GameManager.cs:88`
instantiates `Player Fps Actor` unconditionally. It does not — that line has been inside
`if (LocalClient.Exists)` since phase-00 task 5, and `LocalClient.Exists` is false for a
`UNITY_SERVER` build and false under `-batchmode`. A dedicated server has never spawned the host
body. No change was needed there and none was made.

**The residual was one scenario over, and is the one that mattered.** On an Editor *listen* server
`LocalClient.Exists` is true, so the host body IS spawned — and its prefab carried
`_availableForPlayers: 1`, which made it claimable by a remote joiner. That is precisely the
failure § 3's decision table names ("otherwise the first remote player inherits the host's body"),
and it would also have put pin 3 at N+1 at runtime while the EditMode suite read a clean N. The
prefab flag is now `0`: the pool is the sole source of claimable bodies on every server path, which
the probe above confirms — no `Player Fps Actor` appears in the claimable set of a listen server
that had one standing in the scene.

## 3. Files touched beyond § 6

Approved before implementation, and each one is a necessity of § 4.1 rather than new scope:

| File | Why |
|---|---|
| `Bindings/IAiDriver.cs` (new) | The typed seam replacing the harness's reflect-on-type-name. |
| `Bindings/NetServerBindings.cs` | Declares `AiDriverResolver` and `PlayerBodyFactory`. |
| `NetBindings/IronfrontNetBindings.cs` | The `Assembly-CSharp` half: `AiActorControllerDriver`, and the body factory that can call `Actor.SetTeam`. |
| `NetServerActor.cs` | `MarkAvailableForPlayers`, and the suspend/resume on `Claim`/`Release`. |
| `ServerActorRegistry.cs` | `ClaimableCount` — the number the startup log now reads. |
| `Prefab/Player Fps Actor.prefab` | § 2 above. |

`Dustbowl.unity` was **not** touched. The plan's file list anticipated a serialized prefab field on
`NetServerBootstrap`; a factory seam turned out to be strictly better and needs no scene wiring at
all. A serialized field is one more thing that can be left unwired in a scene that still starts
cleanly and then admits nobody — the exact shape of failure the fail-closed remark on
`RegisterTicketValidator` exists to prevent. Nothing under `Ironfront.Net.Transport/**` or
`Ironfront.Net.Protocol/**` was touched.

## 4. Design decisions the plan left open

**The pool fills in `Start()`, not `StartServer()`.** `NetServerBootstrap` runs at execution order
-1000 so the network role is set before anything reads it, which puts its `Awake` ahead of
`LevelTester`'s — and `LevelTester` is what instantiates the `_Managers` prefab that
`ActorManager.instance` comes from. Filling in `Awake` would find no `ActorManager`, build zero
bodies, and log an error on a server that had started perfectly cleanly. Nothing can connect before
`Start`: the transport binds in `Awake`, but connections are only admitted when the tick loop polls
it, and that is `FixedUpdate`.

**`Suspend`, not destroy or replace.** `Actor.aiControlled` is frozen in `Awake` from
`controller.GetType() == typeof(AiActorController)` and then read by `ActorManager.Register`, the
minimap, LOD, weapon culling and `Binoculars`. Removing or swapping the controller flips that flag's
meaning under all of them at once — the same argument `NetDriverInputSink` makes for not
subclassing `ActorController` (V5-D7). Disabling the component also halts its eight coroutines,
which is what actually stops the bot steering; a flag the controller checked itself would leave
every coroutine running and merely idle.

**Teams alternate 0,1,0,1 and go through `Actor.SetTeam`.** `SetTeam` colours the renderer and the
ragdoll's renderer from `ColorScheme.TeamColor`. A body that skipped it would be on team 0 wearing
the wrong colours, and nothing would report it.

## 5. Pins, and the mutation that made each one red

Six mutations against the real artifact, one per fault claimed. Every one was applied, compiled,
run, and reverted; the suite is 40/40 green with all six reverted.

| # | Mutation to shipping code | Went RED |
|---|---|---|
| 1 | pool loop clamped to `i < 1` | `SecondConnection_ClaimsASlot` — *"connection 2 found no free player slot — this is the phase-3A defect, back"* |
| 2 | pool loop `i < slotCount + 1` | `EveryAdmittedConnection_GetsABody_AndTheNextIsRefused` — *"connection 17 was admitted; the pool exceeds transport capacity"* |
| 3 | pool loop `i < 16` (ignores its argument) | `ClaimableCount_FollowsTheConfiguredNumber_NotALiteral` — *"the pool ignored its slot count. Expected: 5, But was: 16"* |
| 4 | headroom check replaced with `if (false)` | `PoolLargerThanTheRegistryCanHold_CreatesNothing` **and** `ExistingActors_CountAgainstTheHeadroom` |
| 5 | rollback removed from the factory-failure branch | `FactoryFailingPartWay_RollsBack` — *"the three bodies built before the failure survived. Expected: 0, But was: 3"* |
| 6 | `Claim()` no longer calls `Suspend()` | `ClaimSuspendsTheBotBrain_AndReleaseResumesIt` — *"claiming a body left its AI driving"* |

**Mutation 3 is the one worth reading twice.** Under it, `ClaimableCount_EqualsMaxConnections`
**passed** — 16 equals 16, and a pool hard-coded to 16 satisfies it perfectly. Only the companion
that drives a *different* configured number (5) caught it. That is `pinned-baseline-test-companion.md`
§ "assert by identity, not by count" reproduced exactly: the count pin alone would have graded the
original defect's own authoring style as correct.

## 6. Acceptance criteria

| # | Criterion | Result |
|---|---|---|
| 1 | `dotnet test` green; Unity EditMode green; solution builds under `TreatWarningsAsErrors` | **Met.** 1595 engine-free tests across 7 assemblies, 0 failed. EditMode 40/40. `dotnet build -p:TreatWarningsAsErrors=true` → 0 warnings, 0 errors. |
| 2 | All three § 5 pins pass, and the report records the mutation that made each red | **Met.** § 5 above — six mutations, one per fault. |
| 3 | `--smoke` (2 clients, 30 s) connects, both processes exit 0 | **Not run — see § 7.** The slot half is proven in play mode (16 admitted, 17th refused); the two-process smoke is 3B's gate. |
| 4 | `grep -rn "OpenSecondSlot"` returns zero hits, or the report states why one remains | **Four remain, all prose.** The method and `FindAiController` are gone; what is left is four `///` remarks in `IAiDriver`, `NetServerActor`, `IronfrontNetBindings` and the harness itself, each citing the retired mechanism as the reason the current one is shaped the way it is. Deleting the name would delete the argument. No code references it — `grep` for a call site returns nothing. |
| 5 | The claimable-body count and the number in the startup log are read from one source | **Met.** `NetServerBootstrap.FillPlayerSlots` logs `ServerActorRegistry.ClaimableCount` — what `TryClaimPlayerSlot` will actually walk — and errors when it differs from `Config.MaxConnections`. The transport-up line now says "connections", not "slots", because that is what it is counting. |

## 7. Known unknowns and handoffs

**To 3B — `BadSignature` was not exercised.** Phase 3A's verification ran through the registry
directly and through the loopback path; the UDP handshake the § 8 known-unknown names is untouched
by this work and untested by it. AC-3 stays open until 3B runs, exactly as the plan predicted.

**A joining player is not placed at a spawn point.** `MoveToSpawnPoint` is called from
`TryRespawn`, not from `OnClientConnected`. Pool bodies are stamped `deathTimestamp = Time.time` so
`ActorManager.SpawnWave` places them like bots, which is what puts them on the ground — but a
connection that arrives before that map's first wave claims a body still standing at the prefab's
origin. Pre-existing, out of § 4's scope, and worth a line in whichever phase owns join flow.

**The pool and the bots share one budget.** 40 bots plus 16 slots is 56 of 64 `MAX_ACTORS`. Raising
`team0Bots`/`team1Bots` past 24 each, or `MaxConnections` past 24, now fails loudly at server start
with both numbers named — which is the designed behaviour, not a limit that was introduced here.
Vehicles are a separate registry and do not count against it.

**Idle-body cost is unmeasured.** Sixteen eager bodies on a process that renders nothing is the
price of pin 3 being checkable at all. § 4.1 says that is a Phase 4 measurement rather than a
planning guess, and no measurement was taken here.
