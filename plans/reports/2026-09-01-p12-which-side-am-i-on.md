# P12 — which side am I on

- **Phase:** [`../phases/phase-p12-which-side-am-i-on.md`](../phases/phase-p12-which-side-am-i-on.md) ·
  **Contracts:** [`../00-shared/team-multiplayer-contracts.md`](../00-shared/team-multiplayer-contracts.md)
- **Branch:** `p12-which-side-am-i-on` · **Commits:** `fa7ebdd`, `a5222b0`
- **Date:** 2026-09-01
- **Harness artifacts:** [`harness/p12/`](harness/p12/)

---

## 1. What shipped

Four defects, one sentence in common, all four closed.

The local body now takes its team from the snapshot. `NetClientLocalCombatDriver.ApplyLocalTeam`
polls `NetPresenterGate.TryResolveLocalTeam` and routes the answer through a new
`ILocalPlayerRig.SetTeam` to `Actor.SetTeam` — the same method the offline path uses, so the two
skinned renderers are recoloured rather than the colour being reimplemented client-side.
`FpsActorController.playerTeam` is no longer a field latched in `Awake`; it is a property over
`instance.actor.team`, so the latch class that caused D-1 is gone rather than worked around.
`Player Fps Actor.prefab` authors `team: -1` instead of `team: 0`.

The three engine-side offline mutators are gated. `Actor.Die`'s `AddScore` and
`CapturePoint.SetOwner`'s `AddFlag` carry the `NetContext.IsOffline` guard their three siblings
already had, and `ScoreUi` does not subscribe `MatchScoreboard.Changed` at all on a networked
client — so `UpdateUi` cannot repaint the server's numbers with locally-counted ones.

The minimap marks friendlies. `RemoteActorRegistry` filters at the registry rather than in
`MinimapUi.SetMarker`, in both directions and at spawn as well as per frame.

A player-slot body is created with its bot brain parked. `NetServerActor.MarkAvailableForPlayers`
suspends the driver, and the pool logs how many it parked.

Five mutations, each observed RED against the real artifact before the fix was restored.

---

## 2. Four decisions the phase asked to be recorded

### 2.1 `playerTeam` became a property — hazard 1, and the option not taken

The phase offered two: re-read the field when the team arrives, or make `playerTeam` a property
over `actor.team`. **The property.** A second write only moves the question to "did that one run
late enough"; a property has nothing to be late. The three readers (`ActorBlip.cs:50`,
`AiActorController.cs:584` and `:813`) are untouched and can no longer observe a stale value at
all.

Its unresolved answer stays `-1`, which is what the shipped code used and what
`AiActorController.cs:539`'s own comment names. Changing it to the wire's `TeamId.None` (255)
would have made that comment false in a file this phase does not own.

### 2.2 `ScoreUi` does not subscribe, rather than subscribing and early-returning

The phase named both shapes and asked for the first. The second buys exactly one thing —
surviving a mid-session offline/online flip — and this project has no such flip; `NetContext.Role`
is set once at startup. `OnDestroy` mirrors the gate, and a remark in the file says so, because
the next reader will assume the early-return shape.

### 2.3 The offline gate on `Actor.Die` nearly shipped a server regression

The phase says to match `CapturePoint.cs:147`'s shape *exactly*. Taken literally that is correct,
but only by luck: **`NetContext.IsOffline` is false at the server role too**, so the naive reading
stops the server scoring kills.

It does not, and the reason is worth recording because the phase text does not state it: the
server's authoritative team score lives in `MatchStateMachine.ReportDeath` (`_score0` / `_score1`),
and the kill multiplier reads `MatchStateMachine.OwnedPointCount`. Neither reads the engine-side
`MatchScoreboard` these gates now skip. At the server role that board was written and never read.

### 2.4 Six files were edited that the ownership list omits

`FpsActorController.cs` — unavoidable; hazard 1's two options both live there, and offline's
replacement team-0 default has to go somewhere (§ 3.1). `ILocalPlayerRig.cs`,
`NetClientBindings.cs` and `LocalPlayerRigBinding.cs` — the far side of the seam the phase's own
task 3.1 requires (§ 3.2). `LaneBCheckpointRecorder.cs` — the phase's detector table names a
lane-B record assertion for D-1, and the record could not carry one. `NetClientSeamTests.cs` — its
`FakeLocalPlayerRig` must implement the two new interface members or the test assembly stops
compiling.

None are in the phase's **Not owned** list, which reserves `ScoreUi.SetAuthoritativeState` (P11),
`MinimapUi.SetMarker`'s signature (P17) and `ServerActorRegistry.TryClaimPlayerSlot` (P13). All
three are untouched.

---

## 3. Three things the phase asked for that turned out not to be true

### 3.1 Offline, nothing sets the local player's team — the prefab literal *was* the assignment

Task 3.1 says to delete the hardcoded `team: 0`. Doing only that ships a grey team-`-1`
single-player character.

`GameManager.StartGame:88` instantiates `playerPrefab` and nothing calls `SetTeam` on it. The
complete caller list — `Actor.cs:1326` (the declaration), `ActorManager.cs:117` (offline bots),
`IronfrontNetBindings.cs:190` (server-side bodies) — contains no path to the local player. The
prefab's authored `0` was the only thing that ever answered the question offline, and it answered
it by *serialization*, so `Actor.SetTeam`'s recolour never ran on the local body in any mode.

The replacement is in `FpsActorController.Awake`, gated on `NetContext.IsOffline`, and the literal
`0` is the same one `MinimapUi.cs:193` already carries with the same reason recorded (V10 D16:
the human is always team 0 offline). Offline now goes *through* `SetTeam`, so the body is
recoloured — which is strictly more than it did before.

### 3.2 `Net/Client` cannot name `Actor`

The ownership list puts the local-team apply in `NetClientLocalCombatDriver`, and task 3.1 says to
"call `Actor.SetTeam`". Those cannot both be done directly: the assembly boundary and
`check-net-layering.ps1` RULE 6b forbid `Net/Client` naming `Actor` — the same constraint that
makes `ServerPlayerSlotPool` take a body *factory* instead of a prefab.

The apply lives where the phase put it and reaches the body through `ILocalPlayerRig`, the seam
whose own docs describe it as "the human at this keyboard: their input, their camera, their body",
and which already carries `FellBody` as precedent for mutating that body.

### 3.3 The `IsHighlighted()` disjunct cannot be implemented — the wire has no highlight bit

Task 3.3 requires keeping it. Nothing carries it: `ActorSnapshotEntry`
(`Ironfront.Net.Protocol/Messages/SnapshotMessage.cs:42-73`) declares `ActorId`, `ChangeMask`,
position, `Yaw`, `Pitch`, velocity, `StateFlags`, `Health`, `WeaponId`, `AmmoInClip`, `Team`,
`VehicleId`, `SeatIndex` — and no highlight. Neither does `SpawnActorMessage`.

So spotting an enemy is **already** impossible over the network, for a reason that predates this
filter and is not fixed by weakening it. A disjunct over a value that can only ever be false is a
seam with no producer: it reads as working and is not. The filter is team-only, the gap is named
in the method's remarks and here, and the remark says where a spotted bit goes when one lands.

**This is a gap this phase does not close.** The phase's constraint "enemies must still be
markable" is honoured only in the sense that nothing in the new filter is the obstacle.

### 3.4 A bonus: `SpawnActorMessage` *does* carry a team

`RemoteActorRegistry.OnSpawn` carried a comment saying it does not — which is why the marker was
bound at a neutral colour and corrected later. `ServerTickLoop.AnnounceNewActors` constructs
`new SpawnActorMessage(actor.ActorId, actor.Team, ...)`. The comment was stale; the filter now
applies at the spawn too, so a body that is never in a snapshot is filtered rather than drawn.

---

## 4. The prefab edit, and why it was not made through `PrefabUtility`

P3 § 3.3 mandates editing prefabs through the Editor. Attempting it here
(`PrefabUtility.SavePrefabAsset`) wrote the right value **and** upgraded the entire file from the
legacy Unity-5 `!u!1001 Prefab` serialization to the modern format: a **239.6 KB whole-file
rewrite** for a one-int change. It was reverted.

The mandate's stated hazard is that *fileIDs are Editor-assigned and a hand-written reference
resolves to null while looking assigned*. `team` is a scalar `int`, not a fileID, so that hazard
does not apply — and using the Editor provably caused a worse one. The scalar was edited directly
(one line, `team: 0` → `team: -1`) and then **read back through the Editor**, which is the
assurance the rule was after:

```
[p12] prefab Actor.team as the Editor loads it = -1 | object refs on the component: 6, null: 0
```

Zero null references on the component is the check that matters; the mandate exists for exactly
that failure and it did not occur.

---

## 5. Acceptance

| # | Criterion | State |
|---|---|---|
| 1 | Team-1 client sees its body red; team-0 sees blue | **NOT MET** — needs a live two-client run |
| 2 | Prefab no longer asserts team 0; offline still colours correctly | **MET** — 1-line diff + Editor read-back |
| 3 | Score labels hold across a capture flip | **NOT MET** — needs a live capture flip |
| 4 | Minimap shows friendlies, enemy provably in `CullRadius` | **NOT MET** — needs a live two-client run |
| 5 | Live body count = 40 bots + N humans | **NOT MET** — log line ships, no server has printed it |
| 6 | Each detector observed RED first | **MET** — 5 mutations, § 6 |
| 7 | `tools/ci.ps1` green | **MET** — `CI PASSED`, 02:33 |

**Four of seven are outstanding and all four need a real two-client lane-B run.** The phase says
plainly that no test on this project has ever been able to see a mis-coloured body, so the unit
detectors below do not substitute for criteria 1, 3 and 4. This phase is code-complete and
**not fully accepted**.

---

## 6. The five mutations

Standing rule 4, and the project rule that a detector is unverified until the real artifact is
mutated and it goes red.

| # | Mutation | Result |
|---|---|---|
| 1 | Remove the `IsOffline` guard from `Actor.Die`'s `AddScore` | RED — `[G15] Actor.cs:912` |
| 2 | Remove the guard from `CapturePoint.SetOwner`'s `AddFlag` | RED — `[G15] CapturePoint.cs:484` |
| 3 | Delete `rig.SetTeam(team)` from `ApplyLocalTeam` | RED — 4 tests, incl. `Expected: 1, But was: -1` |
| 4 | `ShouldMarkOnMinimap` → `=> true` (the un-fixed behaviour) | RED — 2 tests, `enemy drawn` |
| 5 | Remove the park from `MarkAvailableForPlayers` | RED — `unclaimed player slot was left AI-driven` |

Mutations 1 and 2 fire **independently**, so G15 is not passing on one call site and inferring the
other.

`G15` is the sibling of the existing `G5`, over the same `DeltaScoreMembers` array — one list, so
the two rules cannot drift about which members are delta mutators. Its own test suite also pins
the case that would have made it decoration: a mutation sitting in the `else` of an
`if (NetContext.IsOffline)` is the *networked* path, and a containment check that ignored which
branch it was in would clear it.

---

## 7. Two pre-existing failures, and the proof they are not mine

The Unity EditMode suite is **106 of 108**. The two failures are
`SpawnPointSelectionTests.APinnedDirectoryNarrowsAndNeverWidens` and
`PinningAnEmptySlotChoosesNothingRatherThanFallingBack`, both throwing
`ArgumentException: spawn slot N is not eligible for team M`.

They are pre-existing on `develop`:

- `SpawnPointSelectionTests.cs` and `PinnedSpawnPointDirectory.cs` are **byte-identical to
  develop** (`git diff --quiet develop --` clean on both) and appear nowhere in this branch's diff.
- `develop`'s own copy of the constructor already contains the throwing X-63 validation.
- The tests construct their own `FakeSpawnPoints` and read no global state this branch touches.

X-63 tightened the constructor (`b02f13c`, 2026-08-31, PR #238) and left two stale fixtures.
**Not fixed here** — the files belong to that work, not to P12.

---

## 8. What is owed

1. **A two-client lane-B run** — criteria 1, 3, 4 and 5. The recorder now publishes
   `localBodyTeam` and `snapshotTeam` side by side, so criterion 1 is gradeable from the artifact
   as well as from a screenshot; the pair is the finding, since either number alone looks
   plausible.
2. **An offline run** — criterion 2's offline half rests on the gates plus
   `LocalTeamApply_NeverRunsOffline`, not on a played game.
3. **The two stale `SpawnPointSelectionTests` fixtures** (§ 7).
4. **A spotted bit on the wire**, if enemy blips are ever wanted again (§ 3.3).
5. **A released player slot resumes its bot brain and becomes an unannounced, unsnapshotted,
   still-shooting bot.** X-18 holds unclaimed slots out of both the announce and the snapshot, so
   after a disconnect the map gains an invisible combatant. This is the same defect family as D-4
   one step later. It is **deliberately not fixed here**: the phase scopes task 3.4 to "a pool body
   is suspended from creation", and `IAiDriver.Resume`'s own remark documents the resume as
   intended. Widening it would have changed a documented behaviour the phase did not ask about.

---

## 9. Risks, re-graded

| Risk | Was | Now | Why |
|---|---|---|---|
| Team applied before the body exists / after `Awake` latched | **16** | **6** | The latch is gone — `playerTeam` reads through to the body. The poll lands on whichever of body-or-snapshot arrives second, pinned by `LocalTeamApply_SurvivesTheTeamArrivingBeforeTheBody`. Residual: nothing has yet rendered a red body. |
| `0` used as "not yet known" | 12 | 2 | The sentinel is `-1`, named as `UNKNOWN_TEAM`, and `LocalTeamApply_TreatsTeamZeroAsAnAnswerNotAsAbsence` fails if team 0 is ever read as absence. |
| Offline regresses from a misplaced guard | 12 | 6 | Same predicate as three existing call sites; offline's own default relocated deliberately (§ 3.1). Residual: no offline run performed. |
| Minimap hides a highlighted enemy | 6 | — | Superseded: the highlight bit is not on the wire at all (§ 3.3). |
| AI suspend leaves pool bodies frozen after release | 6 | 2 | `Release` still resumes; `SetAiDriverSuspended` transition-tracks, and the pre-existing test still passes with its original counts. |
| `ScoreUi.cs` conflict with P11 | 6 | 0 | P11 landed first; disjoint methods; no conflict. |

---

## 10. Out of scope, and honoured

- **Friendly fire** — owner ruling, intended, untouched.
- **Slot-pool sizing to the room's `MaxPlayers`** — P14.
- **Nametags, the Tab scoreboard, the deploy screen** — P17.
- **Registering proxies with `ActorManager`** — ledger **A-2** stays WON'T-DO; the marker path is
  still keyed by `Transform`.
- **`ScoreUi.SetAuthoritativeState`** (P11), **`MinimapUi.SetMarker`'s signature** (P17),
  **`ServerActorRegistry.TryClaimPlayerSlot`** (P13) — all untouched.

---

## 11. Harness gate

The artifact gate reports **BLOCK for finalize** on two semantic grounds:
`disprovenClaims` is non-empty and `reachableRegressions` is non-empty. Both are accurate — § 3
records five assumptions that turned out false during the work, and § 8 item 5 plus the G4
exemption's residual risk are real named regressions. Emptying either field would turn the gate
into decoration, which is the failure the gate exists to prevent. The block is reported rather
than resolved.
