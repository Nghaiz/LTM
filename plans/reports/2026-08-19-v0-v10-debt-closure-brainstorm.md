# Brainstorm — closing everything v0–v10 left open, except v9

- **Date:** 2026-08-19 · **Branch:** `develop` · **Scope:** phases V0–V10 except V9 (not cooked)
- **Status:** design approved by the owner; hand-off to `/t1k:plan`
- **Sources read:** 13 phase docs under `plans/replication/phases/`, 9 closure reports under
  `plans/replication/reports/`, 3 adversarial reviews under `plans/reports/`, and
  `plans/replication/integration-checklist.md` (round 8, 2026-08-16 — now stale)

---

## 1. Problem statement

Eleven phases merged between 2026-08-13 and 2026-08-19. Each closed with an honest
STILL-OPEN table, and each handed its residue to a track — "the client track", "V4", "V7",
"a client-flow phase" — that stopped existing when the project went single-owner at `899e75d`.
The result is roughly forty open items with no owner, spread across five documents, several of
which may already have closed silently under a later merge and nobody has checked.

Two of them are load-bearing rather than cosmetic:

1. **The client prefab array is unauthored.** `NetClientProjectilePresenter._prefabsByKind` has to
   be filled in the Editor. Until it is, no replicated projectile renders and six of V7's
   thirteen acceptance criteria cannot be met.
2. **The library stepper is not the production hit path.** `ServerProjectileBridge.AuthoritativeFlight`
   defaults off because the Unity server already simulates every projectile it spawns and applies
   its damage through `Hitbox.ProjectileHit` / `ActorManager.Explode` — the phase-05/V1 path.
   Running both would apply every damage number twice. Turning the flag on is not a config change;
   its first task is deleting the engine-side damage call.

---

## 2. The consolidated debt, by group

| Group | What | Count | Why it stalled |
|---|---|---|---|
| **A** | Editor/authoring | ~10 | Needs the Editor, and leaves no trace CI can read |
| **B** | Verification needing two real clients | ~12 | Needs a harness that only V9 was scheduled to build |
| **C** | Architectural / product follow-ups in code | ~14 | Handed to tracks that no longer exist |
| **D** | Claims made but never verified | 4 | No measurement was ever taken |
| **E** | Ops checklist, round 8 | ~7 | Written 2026-08-16, predates four merges |

### A — Editor/authoring

`NetClientProjectilePresenter._prefabsByKind` (V7, blocks criteria 1/5/6/8/9/11) ·
E1–E6 prefab wiring: remote-actor rig, muzzle, mount, per-weapon flash/report refs, tracer visual,
HUD wiring, explosion `ParticleSystem[]` (V10) · `NetTurret` + `TurretAimLimits` on every turret
prefab (V6) · the `CAR HORN` row in `_Managers.prefab` so `SpecChecker` passes on
`WeaponIds.CAR_HORN = 18` (V6) · `ScoreUi.phaseText` / `phaseTimerText` (V10 E5) · authored
`damageDropOff` curves sampled into `ProjectileConfig` (V7) · `aimLimits` on turret prefabs
(V0 § 7, optional per the V0 closure) · `LobbyShellOverlay`'s three serialized fields (E9) ·
A4 — `NetMovementAgent` + `NetPredictionClock` on the player prefab (blocked by A3).

### B — Verification needing two clients

E7–E12 (combat, HUD, capture point, grenade, A16 camera hijack, scene ordering) · V5's six Editor
checks · V6's two-client turret parity · V7's grenade parity and Profiler run · **bandwidth
(criterion 8) and tick p99 (criterion 9) have never been measured at all**. This set is nearly
congruent with V9 Tasks 1–3.

### C — Code follow-ups

`AuthoritativeFlight` cutover · `ClientCombatState` is instantiated by nothing, so a dead local
player has no input-disable or respawn driver · `PlayerList` exists only as enum `0x4B` — no
message struct, no router case, no caller for `ServerEventWriter.WritePlayerList`, so every
killfeed line is nameless · `ScoreUi` still holds match state that does not run headless (V8 D9;
V10 Task 7 closed the rendering half only) · `GameManager`'s five loose booleans · no capture-point
minimap marker · no scorch `DecalType` · per-bone ragdoll force hardcoded to `MainRigidbody()` ·
`World/VehicleLifecycle.cs` carries rotation as euler degrees where `PackQuat` now exists ·
`Vehicle.Explode()` never calls `ActorManager.Explode`, so a wreck does zero blast damage
(`ExplosionKind.Vehicle`) · `ExplosionKind.Environment` has no source · grenades and deployables
are never ballistically stepped at any setting (pinned as deliberate) · documentation drift in four
places.

### D — Unverified claims

`Weapon.Configuration.releaseDelay = 0.6f` is a guess never read from the throw clip · ten
plan-named Unity tests for V7 are unwritten · phase-05 Task 6's `Actor.Damage` guard was assumed,
not re-verified · V8's A5 CI-gate fix and elimination-by-spawn-point-loss were recorded, not re-run.

### E — Ops checklist, round 8

D1 (Linux dedicated-server artifact) is very likely closed by `c80c09e` but the checklist still
calls it "BLOCKS THE ENTIRE DEPLOYMENT" · D2 `EditorBuild.cs` sign-off · A3 shadow re-run · A7
±2048 m confirmation · A11 master-link plugin DLLs · A12 server CPU percentage (decision) · A13
kill/death tally ownership (decision).

---

## 3. Approaches considered

| Approach | Argument for | Why not chosen |
|---|---|---|
| **Authoring-first** — open the Editor, sweep group A in one sitting | Unlocks the most acceptance criteria per hour | Risks authoring what is already authored, and `ScoreUi` would be authored into fields a later refactor moves |
| **Cutover-first** — delete the engine-side damage call and flip the flag first | The one genuinely architectural item; everything else is smaller | Without a two-client harness there is no way to prove damage is applied exactly once. Highest-consequence change, zero observability |
| **Ledger-first hybrid** ✅ | Several "OPEN, unverified" rows are probably already closed; verifying costs half a day and prevents redoing merged work | Costs a phase before any visible progress |

**Chosen: ledger-first hybrid.**

---

## 4. Design of record — six phases, one gate in the middle

### The governing principle: author, then pin

Group A became ownerless because Editor authoring leaves no artifact CI reads, so nobody can prove
it happened — which is exactly why every V10 row says "unverified whether the animator/rig/muzzle
are authored". Every authoring item in Phase 1 therefore ships with an EditMode gate test asserting
the *presence* of what was authored: an entry per `ProjectileKind` in `_prefabsByKind`, `NetTurret`
on every turret prefab, non-null text refs on `ScoreUi`. Authoring without pinning re-creates the
debt one cycle later.

### Phase 0 — An evidence-backed ledger (0.5 d, read-only)

Re-verify every OPEN row against today's tree with grep and `file:line`, classified
`VERIFIED-OPEN` / `ALREADY-CLOSED` / `VOID`. Prime suspects for already-closed: D1 (by `c80c09e`),
V3's `SeatInfo` shedding cost (by V4, `ce69391`), V1's two `ExplosionKind` rows (by V4 and V7).
`MatchController._capturePoints` is already known VOID rather than open.

Output: `plans/replication/debt-ledger.md`, superseding round 8 of `integration-checklist.md`.
Group E's three decision items (A12, A13, D2 sign-off) surface here as questions for the owner,
not as work.

**Gate: no item enters Phases 1–4 without a verified ledger row.**

### Phase 1 — One Editor session, all of group A

Ordered by unlock value: `_prefabsByKind` first, then E1–E6, `NetTurret` + `TurretAimLimits`, the
`CAR HORN` registry row (this one CI proves for itself through `SpecChecker`), `ScoreUi` texts,
`damageDropOff` curves, `LobbyShellOverlay`. A4 depends on A3; Phase 0 says whether A3 still needs
a run.

### Phase 2 — Code, parallel with Phase 1

The four product items chosen by the owner — `PlayerList` → named killfeed, an owner for
`ClientCombatState`, `ScoreUi` state extracted from the UI (D9), and the cosmetic backlog
(capture-point minimap marker, scorch `DecalType`, per-bone ragdoll force) — plus the small ledger
cleanups (`VehicleLifecycle` euler → `PackQuat`, wreck blast damage, an `ExplosionKind.Environment`
source, documentation drift), plus **cutover preparation**: the delete-path for the engine-side
damage call written behind the existing flag with a library-level double-damage test,
`AuthoritativeFlight` still default off.

**Ordering constraint:** 2c (extract `ScoreUi` state) lands before Phase 1 authors `ScoreUi`'s text
refs, or the authoring targets fields the refactor is about to move.

**Wire note:** `PlayerList` occupies a declared opcode (`0x4B`), so adding the struct is a
shared-file PR against `Ironfront.Net.Protocol` + `plans/00-shared/protocol-spec.md` § 5. Whether it
bumps `PROTOCOL_VERSION` past V3's single bump is a Phase 2 decision, not assumed here.

### Phase 3 — A minimal two-client harness

A deliberate slice of V9 Task 1: one headless server, two scripted client processes. V9 inherits it
rather than rebuilding it. Runs E7–E12, V5's six checks, V6's turret parity, V7's grenade parity.

**Scope lock:** the harness runs only the checks listed above. Everything else returns to V9.

### Phase 4 — Measure, and close the unverified claims

Bandwidth per client, tick p99, and the throw clip's animation-event time read once and authored
into `releaseDelay`. If 0.6 s is wrong, D7's divergence does not disappear — it changes shape from
client-vs-server to offline-vs-server. Decide, with a stated reason either way, whether V7's ten
unwritten Unity tests get written or recorded as won't-do.

### Phase 5 — The cutover gate

Only here, holding a harness and real numbers, does `AuthoritativeFlight` get flipped or recorded
as won't-do. Not before.

---

## 5. Explicitly out of scope this round

- Grenades and deployables are never ballistically stepped — pinned as deliberate by
  `ABouncingOrRigidbodyProjectileIsNotBallisticallyStepped`; the library models no bounce and a
  deployable's pose comes from a Rigidbody.
- `GameManager`'s five loose booleans. `reverseMode` / `assaultMode` are already covered through
  `CapturePoint.Start()`; the other three have no proposed replication path and no product need
  was stated.
- V9 proper. Phase 3 takes one slice of its Task 1 and nothing else.

---

## 6. Risks

| Risk | Mitigation |
|---|---|
| Phase 3 grows into V9 | Scope lock: a fixed check list, everything else returns to V9 |
| Phase 1 authoring is unreviewable | The author-then-pin rule — every item ships a presence gate |
| `releaseDelay` turns out wrong | Phase 4 reads it before Phase 5 judges anything that depends on it |
| Phase 0 finds most rows already closed | That is a success, not waste — it is the outcome that makes Phases 1–4 small |

---

## 7. Success criteria

1. One ledger replaces five sources of truth, every row carrying `file:line` evidence.
2. Every group-A item is authored **and** pinned by a gate that fails if the authoring is undone.
3. V7 acceptance criteria 1, 5, 6, 8, 9, 11 are graded from an actual run, not asserted.
4. `releaseDelay` is a number read from the clip, not a guess.
5. Bandwidth and tick p99 have a first measurement on record.
6. `AuthoritativeFlight` is either on with a double-damage proof, or off with a written reason.

---

## 8. Next

`/t1k:plan` — break these six phases into task-level phase files with file ownership and
per-task acceptance criteria.
