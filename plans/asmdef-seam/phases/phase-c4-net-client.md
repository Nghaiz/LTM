# Phase C4 — `Net/Client`, and the tests it makes possible

- **Track:** [`plan.md`](../plan.md) · **Effort:** L — **declared multi-phase up front**
- **Depends on:** [`phase-c2-net-input.md`](phase-c2-net-input.md) **must land first.** Not a
  preference: `Net/Client` and `Net/Input` share `FpsActorController`, and binding it twice —
  differently, by somebody who cannot see the first binding — is the failure this ordering prevents.
- **Inherited from C3 (2026-08-26):** the `Net/Diagnostics` asmdef. See § 0.

---

## 0. What C3 handed over, and the number to re-measure before sizing this

[`phase-c3-net-diagnostics.md`](phase-c3-net-diagnostics.md) § 0 enumerated `Net/Diagnostics` and
found that **8 of its 13 crossings are `Net/Client` types** — `ClientPredictionStage`,
`ClientVehicleStage`, `NetClientBootstrap`, `NetClientCombatPresenter`,
`NetClientLocalCombatDriver`, `NetClientVehicle`, `RemoteActorRegistry`, `RemoteVehicleRegistry`.
They read as legacy today only because `Net/Client` is still inside `Assembly-CSharp`.

So C3 shipped its gate and deferred its assembly here. **This phase now produces two asmdefs, and
the second is cheap because the first exists:**

| | Before C4 | After |
|---|---|---|
| `Ironfront.Net.Unity.Client` | — | ~10 bindings, as § 2 splits them |
| `Ironfront.Net.Unity.Diagnostics` | 13 bindings needed | **5** — `CapturePoint`, `FpsActorController`, `MatchScoreboard`, `ScoreUi`, `Vehicle` — plus `references: ["Ironfront.Net.Unity.Client", …]`, and `defineConstraints: ["!IRONFRONT_NO_DIAGNOSTICS"]` |

Two consequences worth carrying into the work:

1. **`Vehicle` may not need a binding at all.** Diagnostics' only reference is
   `vehicle.Vehicle.transform` via `NetClientVehicle.Vehicle`, which is declared **`internal`** —
   invisible across the assembly boundary once Client is sealed. Decide what that member becomes
   as part of the Vehicle cluster, not afterwards.
2. **Landing Diagnostics folds a gate.** `tools/check-diagnostics-exclusion.ps1` RULE 1 and RULE 2
   are replaced by the `defineConstraints` line and RULE 3 by the compiler. **Delete them; do not
   leave them running against a folder that no longer works that way.** RULE 4 — that
   `EditorBuildWindowsHarness` still builds the excluded configuration — outlives the asmdef and
   stays.

> **This phase's own `31 distinct legacy types` is the last unverified number on the track, and
> it is the one most likely to be wrong.** `Actor 53× / Vehicle 47× / Weapon 23×` were counted
> over a tree in which `Net/Client` AND `Net/Diagnostics` were both inside `Assembly-CSharp`, so
> an unknown share of those hits is `Net/Client` naming its own neighbours — exactly the error
> that made `Helicopter 16×` and C3's `15 types` wrong. **Enumerate before sizing any cluster in
> § 2.** The tooling to do it is `tools/check-net-layering.ps1` RULE 5b's discipline: drop comment
> lines, blank double-quoted literals, compare ordinally, and read declarations rather than
> substrings.

---

## 1. Scope, and what it is actually worth

Twenty-five files, **31 distinct legacy types**, with three that dominate: `Actor` **53×**,
`Vehicle` **47×**, `Weapon` **23×**. Roughly ten bindings, mirroring the server set.

**What this phase buys**, stated plainly so it is not oversold a third time: an EditMode test path
for `Net/Client`'s own 25 files — `Reconciler`, `RemoteActorRegistry`, the killfeed model — which
have **no tests today**.

**What it does NOT buy**, all three previously claimed and all three false
([`plan.md`](../plan.md) § 2):

- It does **not** close E-11. That closed on 2026-08-21 by a gate, and an asmdef could not have done
  it.
- It does **not** unlock P-D6. That gate must read prefab YAML and legacy source.
- It does **not** unlock P-D9. Those ten tests exercise `Weapon` / `ThrowableWeapon` — legacy
  MonoBehaviours and authored assets.

If this phase is ever justified by one of those three, the justification is wrong.

## 2. Why it splits, and how

Ten bindings against three types referenced 123 times between them is not one diff anybody can
review. It splits **by binding cluster**, never by file count:

| Cluster | Anchored on | Rough size |
|---|---|---|
| Actor/presence | `Actor` (53×) | the largest; likely its own sub-phase |
| Vehicle | `Vehicle` (47×) | second |
| Weapon/combat | `Weapon` (23×) | third |
| The remainder | the other 28 legacy types | last, and smallest |

Each cluster: declare the interface, implement on the legacy component, move the files that only
need that cluster, **Unity compile over MCP**, layering rule observed RED. The asmdef itself lands
**last** — once nothing left in the folder names a legacy type. A folder that is 90 % bound still
cannot be sealed, so sealing is the final step, not the first.

## 3. Work, per sub-phase

1. Enumerate the cluster's legacy references at `file:line`. The 31/53/47/23 figures are from
   [`plans/consolidation/plan.md`](../../consolidation/plan.md) § 3 and are re-counted, not trusted.
2. Declare the binding on the sealed side, in the `Net/Server/Bindings/` shape.
3. Implement on the legacy component.
4. Move only the files whose remaining references are now all bound.
5. Unity compile over MCP.
6. Layering rule for the cluster, observed RED against a deliberate violation.

## 4. Acceptance criteria

1. `Net/Client` compiles as its own assembly with **zero** `Assembly-CSharp` references.
2. Every crossing goes through an interface owned by the sealed side; `Actor`, `Vehicle` and `Weapon`
   each have **one** binding, not one per call site.
3. **At least one EditMode test exists for `Net/Client` that could not have been written before this
   phase.** This is the deliverable that justifies the phase; without it the phase is a move.
4. Every sub-phase graded by a Unity compile over MCP, output kept.
5. Each cluster's layering rule was observed RED before landing.
6. No gameplay behaviour changed.
7. The phase report does **not** claim E-11, P-D6 or P-D9 as closed by this work.

## 5. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Starts before C2; `FpsActorController` gets rival bindings | 3 | 5 | **15** | The depends-on line, and `plan.md` § 5 |
| Splits by file count and a sub-phase ends unsealed and unreviewable | 4 | 3 | 12 | § 2 — split by binding cluster; the asmdef lands last, deliberately |
| Lands with no test, i.e. a pure move | 3 | 4 | 12 | AC-3 is the phase's justification, graded as a criterion |
| One binding per call site instead of per type | 3 | 4 | 12 | AC-2 names the three by count |
| Re-sold as unlocking P-D6 / P-D9 | 3 | 3 | 9 | § 1 and AC-7 |
