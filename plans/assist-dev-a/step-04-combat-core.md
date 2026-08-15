# Step 04 — Combat core as a testable library

**Feeds** Dev A phase-02 tasks 3, 4, 6 · **Session size** large · **Editor needed after** Dev A binds ragdoll and hitmarker

> Goal: fire intent, health, death, respawn and hit feedback exist as plain C# with unit tests, so
> what is left in Unity is drawing.

---

## What is actually missing

Phase-02's first half shipped: prediction is `Net/Client/ClientPredictionStage.cs`, reconciliation is
`Ironfront.Net.Replication/Client/PredictionReconciler.cs`. The second half did not. Grepping
`Fire|Health|Damage|Respawn|Ragdoll` across `Assets/Scripts/Net/` hits **server files only** —
`NetServerActor.cs` and `ServerTickLoop.cs`. The client has no combat.

The server side already exists in the library: `Replication/Combat/` has `ServerFireResolver`,
`LagCompensator`, `HitboxHistory`, `WeaponModel`. This step builds the client's half of the same
conversation.

## Deliverable

In a `netstandard2.1` library — extend `Ironfront.Net.Replication/Client/` rather than starting a new
assembly unless the seam earns it (`code-conventions.md` § "Modular Boundaries"):

1. **Fire intent** — what the client sends, and what it predicts locally while waiting. The predicted
   effects are muzzle flash, recoil and the ammo decrement; the *hit* is never predicted, because the
   server owns it via `ServerFireResolver` and lag compensation.
2. **Local combat state** — health, alive/dead, respawn timing, driven by the snapshot rather than by
   local damage.
3. **The ammo anti-flicker rule** — phase-02 trap 4. Predicted ammo 29 against a snapshot saying 30
   makes the HUD flip 30, 29, 30. Only take ammo from the snapshot when it differs by more than 2, or
   on a reload event; otherwise trust the client. This is three lines and one of the highest
   value-per-line items in the phase.
4. **Killfeed and hitmarker event models** — the data, not the drawing. A hitmarker is "a hit was
   confirmed at tick N with this severity"; whether that is a white cross or a sound is Unity's
   problem.

## Why this split is the point

Everything above is a decision, and decisions are testable by `dotnet test`. What remains for Dev A
is instantiating a ragdoll, playing a sound and drawing a cross — none of which this track can do,
and all of which are quick once the state feeding them is correct and proven.

Resist putting any of it in a `MonoBehaviour`. The moment it needs `UnityEngine`, it leaves the reach
of every test project in the solution, and `Assets/` has no `.asmdef` to run Unity tests from.

## What this step proves, and what it does not

**Proves:** the state machine, the anti-flicker rule and the event models, by unit test. These are
exactly the parts that are annoying to debug by playing.

**Cannot prove:** phase-02 criteria 3, 4, 5 — hitmarkers appearing, hits landing on a strafing target
at 150 ms, ragdolls falling. All are two-client video criteria.

**Dev A checks:** bind the event models to `IngameUi`, drop in the ragdoll and the audio, then record
the two-client video the criteria ask for.

## Done when

- Fire intent, combat state, anti-flicker and the event models exist with tests
- No `UnityEngine` reference anywhere in the new code
- The Unity-side binding points are listed in the PR body so Dev A knows exactly what to attach
- Merged and green
