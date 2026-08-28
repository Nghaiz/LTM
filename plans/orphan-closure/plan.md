# Orphan-closure track — the nine rows verdict-closure found and had no phase left to give

- **Created:** 2026-08-28 · **Branch base:** `develop` · **Owner:** single-owner project
- **Opened by:** [`plans/verdict-closure/plan.md`](../verdict-closure/plan.md) § 6 criterion 1, which
  reported itself **NOT MET** rather than re-scoping: nine rows with neither an owner nor a
  parking, every one a live defect that track's own runs found, and R6 was its last phase.
- **Ledger:** [`plans/debt-closure/debt-ledger.md`](../debt-closure/debt-ledger.md) stays the single
  source of truth. This track does **not** fork it (**V-D1**, inherited). Every phase updates the
  row it closes in the same commit as the closing work, and re-runs
  `tools/recount_debt_ledger.py --check`.

---

## 1. Why this track exists

Verdict-closure closed X-25, X-27, E-11b, X-30, X-31, X-36, X-38, X-45 and X-47, and in doing so
filed nine new rows it could not take: `X-41`, `X-42`, `X-43`, `X-44`, `X-46`, `X-48`, `X-49`, plus
`B-4` and `B-14`, which chain to `X-42`. R6 deliberately did not park them — a parking says nothing
is owed, and something is owed by all nine.

Two of the nine were closed on `develop` before this track opened. Their ledger cells already read
`CLOSED 2026-08-28 (playable-first track)` — **X-48** (every lane-B screenshot rendered the deploy
menu) and **X-49** (the client-side AI NullReferenceException cascade) — so this track inherits
them closed and counts them as such rather than re-opening the question.

**Seven remain.** Five are defects; two — `B-4` and `B-14` — are group-B acceptance checks that
have never had anything to grade, and become gradeable the moment `X-42` lands.

## 2. The shape of what is left

```
O1 (the driver input sink reaches a player-slot body)   X-46 ──▶ B-10, B-11 drive verb
O2 (a scripted client can walk to a vehicle)            X-44 ──▶ R1.3's vehicle programme set
O3 (a thrown weapon detonates)                          X-42 ──▶ B-4, B-14
O4 (a weapon switch stops re-arming the clip)           X-43 ─── independent
O5 (the correction counter measures misprediction)      X-41 ──▶ B-8 reads it honestly
```

**One ordering constraint, and it is the only one:** **O3 lands before the grenade run that grades
B-4 and B-14.** Everything else is independent, and O1/O2 together are what make a vehicle
programme worth writing — but neither blocks the other.

## 3. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **O-D1** | **X-46 is closed with a controller-agnostic seam, not by putting an `FpsActorController` on the server-side player body.** The second candidate loses on three counts: the body would carry two `ActorController` components, so `GetComponent<ActorController>()` becomes order-dependent; `Actor.aiControlled` is frozen in `Awake` from an exact type test and is read by UI, LOD and weapon culling (**V5-D7** exists to keep that field still); and `FpsActorController` expects a camera rig a headless server does not have. |
| **O-D2** | **The suspended-controller condition is `enabled`, and it is the same one X-45 and X-47 already established.** `NetServerActor.Claim` suspends the bot brain by setting `enabled = false`, so `!enabled` names exactly "this controller is not steering this body". Keying off `squad != null` instead would make a genuine AI setup fault indistinguishable from a networked player. |
| **O-D3** | **X-42 routes the carried-weapon path into the engine's own weapon, rather than teaching `ServerCombatAuthority` to launch ballistics.** `ServerProjectileAuthority.StepsKind` already refuses `ProjectileKind.Grenade` in writing, and says why: nothing in the library models a bounce, so a stepped grenade detonates on the first wall it grazes. **V7-D1** already puts a grenade's flight on the engine and its detonation on `ActorManager.Explode` → `S_EXPLOSION`. The defect is that the netcode fire path never reaches that weapon, not that the library lacks a stepper. |
| **O-D4** | **X-43 is closed with a per-weapon clip memory on the session, not by growing the wire.** The outgoing weapon's clip is parked under its own id and restored on the way back. `SnapshotField` is 8/8 full and `AmmoInClip` already travels for the weapon in hand; a switch is a local event on both sides, so nothing new needs replicating for the held weapon's count to be right. |
| **O-D5** | **X-41 gives the reconciler a position history and compares at the acknowledged tick.** `Record` grows a required third parameter rather than an optional one: a defaulted position would let a caller that forgot it silently keep the broken comparison, which is the failure mode the row is about. |
| **O-D6** | **`PredictionReplayTests.TheCorrectionCounterMeasuresLagNotMisprediction` is INVERTED, never re-pinned.** It is a pinned baseline asserting 16 corrections on a client that mispredicted nothing; when O5 lands, the honest number is zero. Re-pinning it to a new count would convert a fix into a permanent baseline (`pinned-baseline-test-companion.md`). |
| **O-D7** | **X-48 and X-49 are recorded as closed-by-develop, with the commit that closed each.** They are not silently dropped from the count, and the roll-up is recomputed rather than decremented. |

## 4. Phases

| # | Phase | Goal | Closes | Effort |
|---|---|---|---|---|
| **O1** | [`phase-o1-driver-input-seam.md`](phases/phase-o1-driver-input-seam.md) | A networked player in a driver's seat drives the vehicle | X-46 | M (2 d) |
| **O2** | [`phase-o2-approach-vehicle.md`](phases/phase-o2-approach-vehicle.md) | A programme verb that walks to a vehicle instead of to a player | X-44 | S (1 d) |
| **O3** | [`phase-o3-thrown-weapons.md`](phases/phase-o3-thrown-weapons.md) | A thrown grenade detonates, and B-4 / B-14 get something to compare | X-42 → B-4, B-14 | M (3 d) |
| **O4** | [`phase-o4-per-weapon-clip.md`](phases/phase-o4-per-weapon-clip.md) | A weapon switch stops handing out a free magazine | X-43 | S–M (1–2 d) |
| **O5** | [`phase-o5-misprediction-counter.md`](phases/phase-o5-misprediction-counter.md) | `corrections: N` becomes a mispredict rate rather than a lag metric | X-41 | S (1 d) |

**Critical path:** O3 → the grenade run. O1, O2, O4 and O5 run in parallel with it and with each
other.

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| O1's relay makes a **bot** read network axes, so every AI vehicle goes inert | 2 | 5 | 10 | **O-D2**: the relay is consulted only on a suspended controller, and a bot's is enabled. A test drives both paths on one class. |
| O3 routes fire into the engine weapon and a *hitscan* weapon loses its server-authoritative damage | 3 | 5 | **15** | The branch is on the weapon's own kind, defaults to the existing hitscan path, and the rifle's damage tests stay untouched and green. |
| O4's clip memory grows without bound on a long match | 3 | 2 | 6 | Fixed-capacity table keyed by weapon id, sized to the loadout, cleared on respawn — the same shape `ActorSpareAmmoPool` already uses. |
| O5 changes the correction rate and a prior artifact's `corrections: N` becomes incomparable | 4 | 2 | 8 | That is the point of the row, and it is stated: numbers taken before O5 are lag metrics and are quoted as such. The inverted test says so in its own failure text. |
| A fix ships with a detector that was never seen RED | 3 | 4 | 12 | Criterion 3 below, inherited verbatim from verdict-closure. Each phase report names the mutation and the observed failure. |

## 6. Success criteria

1. **Every row this track claims is either closed with evidence or reported open with a reason.**
   No row leaves this track in the state that opened it — ownerless and unparked.
2. **B-4 and B-14 carry a verdict and a named artifact**, or a filed row saying which instrument or
   programme is still missing (**V-D2**, inherited).
3. Every defect fixed here ships a test or gate rule **observed RED** against the tree before the
   fix landed. No detector ships unproven (`green-that-proves-nothing.md`).
4. `dotnet test`, `SpecChecker`, `ClientWiringGate` and `check-net-layering.ps1` exit 0 at every
   phase boundary.
5. `tools/recount_debt_ledger.py --check` exits 0 at every phase boundary, and the roll-up is
   recomputed rather than decremented.
