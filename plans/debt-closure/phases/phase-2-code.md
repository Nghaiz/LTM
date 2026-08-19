# Phase 2 — Four product items, the ledger cleanups, and the cutover prepared

- **Track:** [`plans/debt-closure/plan.md`](../plan.md) · **Effort:** L (1 week)
- **Depends on:** Phase 0's group-C rows. Runs parallel to Phase 1 **except** task 2c, which must land before Phase 1 task 1.6.
- **Verification:** `dotnet test`, `dotnet run --project tools/ClientWiringGate`, `dotnet run --project tools/SpecChecker`

---

## 1. Goal

Close the code half of the debt: the four product items the owner put in scope, the small ledger
cleanups, and the preparation — not the enabling — of the `AuthoritativeFlight` cutover.

---

## 2. Task 2a — `PlayerList`, so a killfeed line has a name (M)

**Files:** `Ironfront.Net.Protocol/` (new message struct), the client router, `ServerEventWriter`,
`plans/00-shared/protocol-spec.md` § 5.

Today `ServerMessageType.PlayerList = 0x4B` is declared **and nothing else exists** — zero message
struct, zero router case, and `ServerEventWriter.WritePlayerList` has no caller (V10 closure § 3,
V3 closure "still open, with owners"). V3's closure also records the asymmetry worth knowing: the
client half is declared in `ClientWiringGate`'s `KnownUnwiredEvents` and reported every run, while
**the server half has no gate at all**, because that tool only inspects router events.

Deliver: the struct, the router case, a real call site for the writer, and the § 5 field table.
Add a `ClientWiringGate` detector for the **writer** side so the asymmetry does not return.

**Per P-D8 this does not bump `PROTOCOL_VERSION`** — the opcode is already reserved and no existing
message layout changes. Assert that in a test, so a future edit that does change a layout is forced
to notice.

## 3. Task 2b — An owner for `ClientCombatState` (M)

**Files:** `Ironfront.Net.Replication/Client/ClientCombatState.cs` (reads only), a new MonoBehaviour
under `Assets/Scripts/Net/Client/`.

The type exists; nothing constructs it (`grep -rn "new ClientCombatState" Assets/Scripts/` → zero).
A dead local player is felled by `NetClientCombatPresenter.KnockOverLocalActor` and then has no
driver at all — no input disable, no respawn screen. Give it one, and shrink
`ClientWiringGate.KnownUnwiredEvents` by the events it now consumes.

## 4. Task 2c — `ScoreUi` state out of the UI (V8 D9) (M) — **lands before Phase 1 task 1.6**

**Files:** `Assets/Scripts/Assembly-CSharp/ScoreUi.cs`, plus wherever the state lands.

`ScoreUi.cs:82-83` still carries its own doc comment saying so: *"This class still holds match state
that does not run headless, and that remains V8 D9's recorded divergence."* V10 Task 7 closed the
rendering half only. Move score, `ScoreMultiplier` and the `victoryPoints` win condition out of the
`MonoBehaviour` so a headless server can hold them; leave `ScoreUi` rendering what it is given.

**Ordering:** this changes which fields exist on `ScoreUi`, so it must merge before Phase 1 authors
`phaseText` / `phaseTimerText`.

## 5. Task 2d — The cosmetic backlog (M)

- Capture-point minimap marker. `MinimapUi` has no marker API for capture points today; its markers
  are the `SpawnPoint` buttons `SetupMinimap()` builds once, and `AddActorBlip` is add-only over an
  `Actor` rather than a `Transform`. A `Transform`-based marker API is the change.
- Scorch `DecalType`. `DecalManager.DecalType` is `Impact` / `BloodBlue` / `BloodRed`; explosions
  reuse `Impact`.
- Per-bone ragdoll force. `ApplyRigidbodyForce` is hardcoded to `MainRigidbody()`; no per-bone API
  exists anywhere in `Assembly-CSharp`.

## 6. Task 2e — Prepare the cutover, leave it off (M)

**Files:** `ServerProjectileBridge`, `Hitbox.ProjectileHit` / `ActorManager.Explode` call sites,
plus a library-level test.

Write the delete-path for the engine-side damage call behind the existing
`AuthoritativeFlight` flag, and a test that asserts **exactly one** damage application in both
configurations: flag off → engine applies it, library does not; flag on → library applies it, engine
call is gone. `AuthoritativeFlight` **stays default off** (P-D2). Phase 5 is the only place it flips.

The point of writing it now is that Phase 5 becomes a decision with a prepared patch and a proof
obligation, rather than a fresh refactor made under measurement pressure.

## 7. Task 2f — Ledger cleanups (S each)

| Item | Change |
|---|---|
| `World/VehicleLifecycle.cs` rotation | euler degrees → `PackQuat`. V3's closure notes this is a change to V8's sink signature, not to the codec |
| Wreck blast damage | **Decided: a wreck damages.** `Vehicle.Explode()` gains its `ActorManager.Explode` call with `ExplosionKind.Vehicle`, at that kind's own configured radius and damage. Handed to V4 by V1-D5 as a gameplay decision; taken here. Balance note for the phase report: taking cover behind a burning vehicle becomes dangerous, which is the intended consequence |
| `ExplosionKind.Environment` | **Decided: give it a source.** An `ExplosiveProp` component lets a scene fuel drum or gas cylinder detonate, emitting `ExplosionKind.Environment` through the same server-authoritative path as every other explosion — the client applies zero health damage and the corpse ragdoll impulse only. This is a small feature, not a debt repayment, and is sized as such |
| Documentation drift | `plans/00-shared/architecture.md:314` cites the private `IngameUi.ShowHitmarker()`; `phase-v7-projectiles.md:211` and `phase-v8-objectives.md:91` cite stale line numbers; `docs/codebase-map.md`'s eight `Actor.cs` references have shifted |

---

## 8. File ownership

Writes: `Ironfront.Net.Protocol/**`, `Ironfront.Net.Replication/**`,
`Ironfront_Reborn/Assets/Scripts/Net/Client/**`, `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/{ScoreUi,DecalManager,Vehicle,MinimapUi}.cs`,
`tools/ClientWiringGate/**` (the writer-side detector only), `plans/00-shared/{protocol-spec.md,architecture.md}`,
`docs/codebase-map.md`.

Does **not** write prefabs, scenes or assets — those belong to Phase 1. `tools/ClientWiringGate` is
touched by both phases: Phase 1 owns the prefab detectors, Phase 2 owns the writer-side detector.
Sequence the two commits rather than editing the file concurrently.

---

## 9. Acceptance criteria

1. `dotnet test` green across the solution; no test skipped without a written justification.
2. `PROTOCOL_VERSION` is unchanged, and a test asserts it.
3. `ServerEventWriter.WritePlayerList` has a production caller, `PlayerList` has a router case, and a killfeed line renders a name in a smoke run.
4. `ClientCombatState` has exactly one production owner; `KnownUnwiredEvents` shrinks by the events it consumes.
5. `ScoreUi` holds no match state that a headless server needs; its class doc no longer claims a D9 divergence.
6. The double-damage test passes in both flag configurations, and `AuthoritativeFlight` still defaults **off**.
7. `ClientWiringGate` and `SpecChecker` both exit 0.
8. Every ledger row this phase touches moves to `CLOSED` in the same commit as the fix.

---

## 10. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| The cutover patch is written and then accidentally enabled | 2 | 5 | 10 | Acceptance criterion 6 asserts the default in a test, not in a comment |
| `ScoreUi` extraction breaks the rendering V10 Task 7 just fixed | 3 | 3 | 9 | Task 2c moves state only; rendering call sites keep their signatures |
| `PlayerList` struct disagrees with the spec table | 3 | 3 | 9 | `SpecChecker` parses the spec, so the § 5 row and the code are gated against each other |
| Concurrent edits to `ClientWiringGate` from Phases 1 and 2 | 3 | 2 | 6 | Ownership note in § 8 — sequence the commits |

---

## 11. Handoff

To **Phase 1**: task 2c is the unblocker for task 1.6.
To **Phase 3**: `PlayerList` and the `ClientCombatState` owner are both on the observational check
list — a killfeed with names and a respawn flow are things two rendered clients can see.
To **Phase 5**: the prepared cutover patch and its double-damage test.
