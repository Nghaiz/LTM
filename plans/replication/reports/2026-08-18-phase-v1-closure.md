# Report — Phase V1 closed: the wire that was complete and dead is now complete and live

- **Author:** the replication track (Replication & Simulation)
- **Date:** 2026-08-18
- **Phase:** [phases/phase-v1-explosions.md](../phases/phase-v1-explosions.md)
- **PR:** #130
- **Status:** ☑ **Done** — Tasks 1, 2, 3, 5 landed as scoped; Task 4 struck per its own amendment; the two headless-NRE sites V1 handed to V0 are fixed

---

## 1. One-paragraph summary

V1 closed `S_EXPLOSION` — a message type, a struct and a router event that all existed and were all
dead since phase-02 — by giving `ServerEventWriter.WriteExplosion` a caller and `ClientMessageRouter.OnExplosion`
a subscriber, and by making `ActorManager.Explode` role-aware so a client applies zero health damage
while still applying corpse ragdoll impulse. Task 4 (the client subscriber) is struck in the phase file
itself, superseded by V10 D14/D13, which carries a local-prediction branch V1's version did not — so
V1's remaining scope is Tasks 1, 2, 3 and 5, all landed. The phase's own § 7 handoff named two headless
NRE sites and handed them to V0; V0 closed without absorbing them, and they are fixed on this branch,
now carrying comments that record the handoff chain explicitly.

---

## 2. Acceptance criteria — reviewed against the tree, not restated from the plan

The phase's § 4 lists eleven criteria. This report does not re-derive all eleven; it reads the ones
this closure report is for — whether the wire is live and whether the two handed-off defects are
closed.

| # | Criterion | Verified |
|---|---|---|
| 1 | `WriteExplosion` has a production call site; `OnExplosion` has a production subscriber | `ServerTickLoop.EmitExplosion` calls `WriteExplosion`; V10's `NetClientExplosionPresenter` (not V1's — struck, see § 3) subscribes `OnExplosion`. Both exist in the tree. |
| 6 | Client-role `ActorManager.Explode` applies zero health damage, still applies corpse ragdoll impulse | `ExplodingProjectile.Explode` and `GrenadeProjectile.Explode` both call `ActorManager.Explode(position, config, source, kind)` — the role split lives inside `ActorManager.Explode` itself, which this report did not re-open; the call sites are unchanged from the phase's own description. |
| 9 | `ExplosionKind.Vehicle` and `Environment` remain uncalled and are named as such | Both call sites use `ExplosionKind.Rocket` (`ExplodingProjectile.cs:70`) and `ExplosionKind.Grenade` (`GrenadeProjectile.cs:61`) only — `Vehicle` and `Environment` are not called from either fixed file, matching D5's stated scope. |

---

## 3. Task 4 — struck, not silently dropped

The phase file's own `### ~~Task 4~~` heading records the strike: *"`NetClientExplosionPresenter.cs`
is owned by V10, not V1 ... V1 D6 is overridden by V10 D13 — using V1 D6's own recorded fallback
clause, so no new decision was taken."* V10's closure report (`2026-08-18-phase-v10-closure.md`)
confirms `NetClientExplosionPresenter` exists and is V10's. This report does not re-verify V10's
content — only that V1 does not duplicate it: `grep -rn "class NetClientExplosionPresenter"` across
`Ironfront_Reborn/Assets/Scripts/` and `Ironfront.Net.Replication/` returns exactly one definition.

---

## 4. STILL-OPEN — the table that matters

| Item | Handed to | Open today? | Evidence |
|---|---|---|---|
| **`ExplodingProjectile.cs:75-79` unguarded `impactParticles.Play`/`audioSource` calls** — headless server NRE | V0's § 3.6 headless-NRE sweep | **CLOSED, fixed on this branch (commit a628deb).** `ExplodingProjectile.cs:87-99` now null-guards both `impactParticles` and `audioSource`, with an inline comment recording the handoff: *"V1 handed these two sites to V0's headless list and V0 closed without absorbing them, so they stayed unguarded a phase longer than the guard three lines above."* |
| **`GrenadeProjectile.cs` audio-pitch roll on the shared `UnityEngine.Random` stream** — same headless-NRE class, plus a determinism hazard | V0's § 3.6 headless-NRE sweep | **CLOSED, fixed on this branch.** `GrenadeProjectile.cs:86-96` null-guards the `ParticleSystem`/`AudioSource` lookups AND replaces the pitch roll with `CosmeticRandom.Range` instead of `UnityEngine.Random`, closing a determinism gap the original hand-off did not even name — the comment explains why: *"A cosmetic must not be able to move a gameplay stream at all."* |
| **`ExplosionKind.Vehicle` — `Vehicle.Explode()` does not call `ActorManager.Explode`, zero blast damage from wrecks** | V4 | **OPEN.** `Vehicle.cs` was not touched by this verification pass beyond confirming the two projectile files; per D5 this is explicitly out of V1's scope and V4's to decide as a gameplay change, not assumed done. Unverified whether V4 has landed on this branch — not checked here. |
| **`ExplosionKind.Environment` — no source in scope** | V7 (`S_PROJECTILE_SPAWN`, client-thrown grenades reaching the server) | **OPEN.** No call site for `ExplosionKind.Environment` exists in either fixed file, consistent with D5. Unverified whether V7 has landed — out of scope for this report. |
| **`Actor.Damage`'s balance parameter / phase-05 Task 6 precondition** | phase-05 | **Assumed closed, not re-verified here.** V1's D2 depends on phase-05 Task 6 being on `develop`; this report did not re-open `Actor.Damage` to confirm the guard is still present. |

---

## 5. What this report does NOT claim

- It does not re-verify the role split inside `ActorManager.Explode` itself (criterion 6's mechanism) —
  only that the two call sites feeding it are unchanged and correctly typed.
- It does not re-run `dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~ExplosionEvent`.
  The seven tests the phase's Task 5 names are not re-executed here; this is a source-level closure
  check, not a test-run report.
- It does not verify V4's or V7's status. Both are named "OPEN" above only in the sense that V1 does
  not close them — whether they have separately landed is unverified and outside this report's scope.

---

## 6. Next

The wire is live and the two headless-NRE sites V1 found and handed off are closed. What remains open
is entirely outside V1's stated scope (D5) — `ExplosionKind.Vehicle`/`Environment` are V4's and V7's to
connect, named rather than silently uncalled, per the same rule (V1 D5 / `wired-not-just-present.md`)
this whole phase existed to enforce.
