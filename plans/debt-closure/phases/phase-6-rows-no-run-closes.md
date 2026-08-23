# Phase 6 — The five rows no acceptance run can close

- **Track:** [`plan.md`](../plan.md) · **Effort:** M (3 d)
- **Depends on:** nothing. Runs in parallel with 3F / 3D / 3E / 4.
- **Hard ordering constraint:** **X-6 lands before [`phase-5-cutover-gate.md`](phase-5-cutover-gate.md).**
  Phase 5 decides `AuthoritativeFlight` on the strength of the `ownsHealth` guard, and that guard has
  no pin today.
- **Source of scope:** [`plans/consolidation/plan.md`](../../consolidation/plan.md) § 4, which
  separated the seventeen observational rows (one run closes them all) from the five that are real
  code work whatever the run says.

---

## 1. Why these five are grouped

The lane-B acceptance set collapses `B-1`…`B-17` into one programme because they are seventeen
*assertions over one run shape*. These five are not assertions. Each is a defect or a missing guard
that a green run would not have noticed — and in three cases a green run would have **hidden**.

## 2. Task 6.1 — D-1: one release delay cannot serve two clips (M)

`Weapon.cs:61` declares `public float releaseDelay = 0.6f;`, consumed at `ThrowableWeapon.cs:40`.
The ledger already read the clips out of force-text YAML, so the number is not in question:

| Clip | `SpawnThrowable` event time |
|---|---|
| `Assets/AnimationClip/frag_throw.anim:2248-2250` | **1.2381772 s** (clip `m_StopTime: 1.8333335`) |
| the second throwable clip | **0.414 s** |

**Three times apart.** One constant is wrong for both, and `0.6f` is wrong for either. This is
therefore a per-weapon authored value, not a number to read once — which is why
[`phase-4-measure.md`](phase-4-measure.md) task 4.3's original framing ("read it from the clip,
expect 0.6 s") has been amended rather than executed.

Work: make `releaseDelay` per-weapon, author both values from their own clip, and **fix the test that
is true by construction** — it currently feeds the same constant to both sides, so it cannot fail
whatever the clips say (`green-that-proves-nothing.md`). The replacement asserts the authored value
against the clip's event time, and is observed RED against today's `0.6f` before it ships.

**D7's divergence does not disappear — it changes shape**, from client-vs-server to
offline-vs-server. Write that into `plans/replication/phases/phase-v7-projectiles.md` in the same
commit, per phase-4 AC-3.

## 3. Task 6.2 — E-6: the level bounds nothing calls (S)

`LevelBounds.IsInside` has **zero callers**, and Dustbowl has 14 `VehicleSpawner`s of which two are
respawning helicopters. Two of them reach ±2048 m in well under a minute from the worst playable
coordinate. The symptom is not a wall — it is a **silent permanent rubber-band**, which is the worst
shape a bug can take because it reads as lag.

Work: call it, decide the response (clamp, damage, or authoritative teleport — pick one and write the
reason), and pin the choice with a test that fails when the call is removed.

## 4. Task 6.3 — X-6: the pin the cutover depends on (S)

`C-14` / `D-3` confirm `Actor.Damage`'s `ownsHealth` guard is present. **No test asserts `ownsHealth`
is false on a client.** Under P-D5 that owes a pin, and it is the exact guard the whole
`AuthoritativeFlight` cutover rests on: if the guard silently inverts, Phase 5's "damage applies
exactly once" proof is measuring a world that no longer holds.

Work: one test, observed RED with the guard flipped. **This task is the ordering constraint** — it
lands before Phase 5 runs.

## 5. Task 6.4 — X-7: matchmaking picks by dictionary order (S)

The master's `Allocate` orders candidate servers by `Dictionary` iteration order, because every server
reports `cpuPercent: -1` and there is no other signal. E-8 decided "leave it at −1"; that decision
does not cover the ordering, and a live matchmaking defect is what is left.

**Belongs to the master-server track**, and is carried here only because that track has no active
owner. Work: give `Allocate` an ordering signal that exists — `AverageTickMs` is already reported —
and pin the ordering with a test that fails when it degrades to insertion order.

## 6. Task 6.5 — X-8: three messages the server routes and nobody writes (S)

`Chat`, `LoadoutSelect` and `Ping` are `ClientMessageType`s the server routes and the client never
writes. Split out of X-3 deliberately by Phase 3C rather than absorbed into its closure (3C's AC-5
graded the split), because no check in [`phase-3-harness.md`](phase-3-harness.md) § 2 needs any of
the three.

Work: the client senders, plus the **G6-shaped** gate coverage in the other direction — a
`ClientMessageType` the server routes with no production writer should fail the build, the way G6
already grades `ServerEventWriter` writers. That gate is what stops the fourth one from sitting
uncalled for four phases the way `WritePlayerList` did.

## 7. Task 6.6 — Group A residue (S)

| Row | What is left |
|---|---|
| **A-6** | Not authoring any more — its YAML half closed with A-9. Two clauses hold it: a dedicated **human-count** element (a `.cs` change) and E5's runtime *"timer hidden during `Playing`"* clause, which needs a Play Mode observation |
| **A-4** | `ALREADY-CLOSED`, but the opportunistic `WeaponIds` → prefab confirmation was never performed. An open **opportunity**, not a blocker — do it while the Editor is open for A-6, or record that it was skipped |

## 7a. Task 6.7 — Three rows whose `closes in` column is empty (S)

Found while writing this phase, not by a run: **X-10**, **X-14** and **X-16** are `VERIFIED-OPEN`
with an owner column of `-` or `own decision`. Nothing schedules them. That is the exact shape that
turned group A into ownerless debt in the first place, so they are claimed here rather than left to
be rediscovered.

| Row | What it is | Why it is not a harness row |
|---|---|---|
| **X-10** | A **real rendered client** loading `Dustbowl` has X-9's `Awake` role race, and nothing declares its role. X-9's fix reaches lane B only because the **harness** declares one; `SetRole` has four call sites and none of them is an ordinary client | A green lane-B run **hides** this — the harness supplies the very thing the shipped client is missing (`green-that-proves-nothing.md`) |
| **X-14** | A networked human **cannot change weapon server-side**. `InputButtons.SwitchWeapon0..3` (bits 11–14) have a producer and a consumer; what is missing sits between them | Deliberately not bundled into the defect-5 fix. Still a gameplay hole no verdict grades |
| **X-16** | `ClientCombatState.PredictFire` has **zero production callers** — `predictedShots: 0` at every checkpoint while the server emptied a magazine. The identical shape to X-11, one method over | X-11 was the same shape and closed as code; a run only reports the symptom |

X-10 is the one to take first: it is a **shipped-client** defect that the harness's own correctness
conceals, so every future green run makes it *less* likely to be found, not more.

## 8. File ownership

```
Ironfront_Reborn/Assets/Scripts/**            (6.1, 6.2, 6.3, 6.5, 6.6)
Ironfront.MasterServer/**                     (6.4)
tools/ClientWiringGate/**                     (6.5's gate)
plans/replication/phases/phase-v7-projectiles.md   (6.1's D7 amendment)
plans/debt-closure/debt-ledger.md             (the six rows this phase moves)
```

## 9. Acceptance criteria

1. `releaseDelay` is per-weapon, both values authored from their own clip's event time, and the
   replacement test was observed RED against `0.6f`.
2. V7's D7 record is amended in the **same commit** as 6.1.
3. `LevelBounds.IsInside` has a production caller, the response is a written decision, and a test
   fails when the call is removed.
4. A test asserts `ownsHealth` is false on a client, observed RED with the guard flipped. **This
   criterion gates Phase 5.**
5. `Allocate` orders by a signal that exists, pinned by a test that fails on insertion order.
6. `Chat`, `LoadoutSelect` and `Ping` have production client senders, and a gate fails the build when
   a routed `ClientMessageType` has none.
7. Every row moved in the ledger quotes the `file:line` or test that justifies it. No row closes by
   count or by argument (`phase-3e-run-and-ledger.md` § 5 applies here too).
8. Each task lands in **its own commit** — six rows in one commit is six rows nobody can revert
   independently.

## 10. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| 6.1's replacement test is written true-by-construction again | 3 | 5 | **15** | AC-1 requires the RED observation against `0.6f`; the current test's failure mode is named in § 2 so it is not re-invented |
| X-6 slips and Phase 5 runs without its pin | 3 | 5 | **15** | Stated as the phase's one hard ordering constraint, and repeated in `phase-5-cutover-gate.md` |
| E-6's chosen response changes gameplay unexpectedly | 3 | 3 | 9 | The response is a **written decision** with a reason, per AC-3, not an implementation detail |
| X-7 drags the master-server track back open | 2 | 3 | 6 | Scope is `Allocate`'s ordering and its pin. Anything else returns to that track |

## 11. Handoff

To **Phase 5**: the `ownsHealth` pin (X-6), without which the cutover has no guard to trust.
To **Phase 4**: 6.1's authored numbers, which make task 4.3 a verification instead of a discovery.
