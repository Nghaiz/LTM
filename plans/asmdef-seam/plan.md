# Plan — the asmdef seam: a boundary the compiler enforces

- **Created:** 2026-08-23 · **Branch base:** `develop`
- **Source of record:** [`plans/consolidation/plan.md`](../consolidation/plan.md) § 3 (**F3**), which
  measured this against the 375 type names defined in `Assembly-CSharp`.
- **Starts after:** [`plans/debt-closure/phases/phase-3e-run-and-ledger.md`](../debt-closure/phases/phase-3e-run-and-ledger.md).
  Refactoring the client while a harness is comparing artifacts across runs is how a run difference
  becomes unattributable.

---

## 1. The fact this track exists for

**453 files sit in `Assembly-CSharp` with no asmdef.** Nothing prevents client code reaching into
server code — and it was not hypothetical: fifteen files named `Ironfront.Net.Unity.Server` outside a
comment, one of them `Net/Client/NetClientObjectivePresenter.cs`, which had been reading
`NetServerBindings.CapturePoints` since V8.

The constraint that decides every design question here: **an asmdef assembly cannot reference
`Assembly-CSharp`.** Dependencies run one way. So the cost of moving a folder into an asmdef equals
the cost of the bindings layer that replaces its legacy references — not the cost of the move.

## 2. What is already closed, and must not be re-attempted

| Was proposed | Outcome |
|---|---|
| **C1 — `Net/Headless` as an asmdef** | **DONE 2026-08-21, and not as an asmdef.** `LocalClient` is a static class whose only `using` is `UnityEngine` — Shared's namespace and Shared's shape — so it was folded into `Net/Shared/` and the folder deleted. One assembly fewer than a one-file asmdef |
| **C4 closes E-11** | **False, and C4 could not have.** E-11 is client code calling server code, and an asmdef cannot stop it: `NetBindings/` must live in `Assembly-CSharp` (the only assembly seeing both halves), so `Assembly-CSharp` must reference the server assembly, so all 333 legacy files may call in. `autoReferenced: false` closes that and kills `NetBindings` with it. E-11's layering half closed on 2026-08-21 via `tools/check-net-layering.ps1`, wired into `ci.ps1` and `ci.yml`. The remainder is **E-11b** |
| **C4 unlocks P-D6 / P-D9** | **False.** P-D6's gate must read prefab YAML and legacy source; P-D9's ten V7 tests exercise `Weapon` and `ThrowableWeapon` — legacy MonoBehaviours and authored assets. No asmdef move touches either. Both stay closed whatever C4 does |

**What C4 genuinely buys**, stated so it is not oversold again: an EditMode test path for
`Net/Client`'s own 25 files — `Reconciler`, `RemoteActorRegistry`, the killfeed model — which have
**no tests today**. That is worth doing. It is a different thing from what the earlier line promised.

## 3. The measurement the sequencing comes from

| Folder | Files | Distinct legacy types | Heaviest | Phase |
|---|---|---|---|---|
| `Net/Headless` | 1 | **0** | — | done |
| `Net/Input` | 8 | **2** (measured) | `LoadoutUi`, `OptionsUi` | **C2 — done 2026-08-26** |
| `Net/Diagnostics` | **13** | **13** — of which **5** legacy, **8** `Net/Client` | `LaneBCheckpointRecorder` (all 13) | **C3 — gate done 2026-08-26; asmdef → C4** |
| `Net/Client` | 25 | 31 *(unverified — see below)* | `Actor` 53×, `Vehicle` 47×, `Weapon` 23× | **C4** |

> **The `Net/Input` row said `~8 real`, with `Helicopter` at 16 references and
> `FpsActorController` at 15. All three numbers were wrong, and wrong in the same way.**
> C2 enumerated rather than trusting them, as its § 3.1 required, and found **two**:
> `LoadoutUi` and `OptionsUi`, both static-singleton reads in `LocalInputSource`. Every one of
> the 16 `Helicopter` hits is `Net/Input`'s own `HelicopterAxes` / `HelicopterControls` /
> `HelicopterAxisMap`, or the `"Helicopter Pitch"` axis string. Every one of the 15
> `FpsActorController` hits is an XML doc comment, bar one inside a `Debug.Log` literal.
> Neither legacy type is referenced at all. The measurement had been a substring grep that
> counted comments and the folder's own type names.
>
> **The C3 and C4 counts come from the same grep and are therefore unverified.** Do not size
> either phase from this table — enumerate first, stripping comments and string literals, the
> way `tools/check-net-layering.ps1` RULE 5b now does. C4's `Actor 53× / Vehicle 47×` is
> plausible on its face, but "plausible" is what `Helicopter 16×` looked like too.
>
> **C3 enumerated on 2026-08-26 and the warning paid for itself twice.** 11 files were 13;
> 15 types were 13 (`Path` is `System.IO.Path`; `State` matched a `private` nested enum in
> `ActiveRaggy`) — and, decisively, **8 of the 13 are declared in `Net/Client`, not in
> `Assembly-CSharp` at all.** They only *look* legacy because C4 has not run yet. That killed
> C3's asmdef rather than its gate: sealing before C4 means 8 interfaces deleted the moment
> `Net/Client` becomes an assembly Diagnostics can simply reference. See
> [`phase-c3-net-diagnostics.md`](phases/phase-c3-net-diagnostics.md) § 0.
>
> **C4's own row is now the last unverified one, and it is the one most likely to be wrong** —
> `Actor 53×` and `Vehicle 47×` were counted over a tree in which `Net/Client` and
> `Net/Diagnostics` were both still inside `Assembly-CSharp`, so an unknown share of those hits
> is `Net/Client` naming its own neighbours. Enumerate before sizing.

`Net/Server` and `Net/Shared` are already sealed. **`Net/Server/Bindings/` is the pattern to copy** —
`IAiDriver`, `ICapturePointDirectory`, `ISpawnPointDirectory`: the server does not name a legacy
type, it names an interface a legacy component implements.

## 4. Verification is Editor-only, and this is not a formality

**`Assets/Scripts/Net/Shared` has zero references, so `dotnet build` staying green proves *nothing*
about layering.** A green `dotnet build` is the canonical false green on this track
(`green-that-proves-nothing.md`). Every step is graded by a **Unity compile driven over MCP**, plus
`tools/check-net-layering.ps1`.

## 5. Phases

| # | Phase | Goal | Effort |
|---|---|---|---|
| **C2** | [`phase-c2-net-input.md`](phases/phase-c2-net-input.md) | `Net/Input` behind one `ILocalInputEnvironment` binding for `LoadoutUi` / `OptionsUi` | M (3 d) |
| **C3** | [`phase-c3-net-diagnostics.md`](phases/phase-c3-net-diagnostics.md) | The player-build exclusion gated. **Asmdef deferred into C4** — 8 of its 13 crossings are `Net/Client` types | S (1–2 d) |
| **C4** | [`phase-c4-net-client.md`](phases/phase-c4-net-client.md) | `Net/Client` behind ~10 bindings; EditMode tests become possible. **Now also folds in `Net/Diagnostics`** — 5 bindings, not 13, once the Client assembly exists | L (multi-phase) |

> **The stated reason for this ordering did not survive C2, but the ordering still holds.**
> `Net/Input` does not reference `FpsActorController` at all, so the two halves never shared it
> and C2 bound nothing C4 could contradict. What C4 must not contradict instead is the seam C2
> built: `ILocalInputEnvironment` + `NetInputBindings`, registered from
> `NetBindings/IronfrontNetBindings.Install`. `Net/Client` **does** reach `FpsActorController`
> (`ClientVehicleStage` reads `_localController.InputSource`), so the rival-abstraction risk is
> real — it is simply C4's alone, and phase C2's AC-3 was grading a shared surface that had no
> second party.

### The `autoReferenced: false` step, and why it is not C2's

Phase C2 § 3.4 said to set `autoReferenced: false` on the new asmdef. **It shipped `true`,
deliberately.** Four files consume `Net/Input` types and none can add an explicit reference:

| Consumer | Assembly | Can it ever add a reference? |
|---|---|---|
| `Assembly-CSharp/FpsActorController.cs` | Assembly-CSharp | **No** — permanently predefined. 31 `inputSource` sites; it *constructs* `LocalInputSource` and holds `NullInputSource.Instance` |
| `NetBindings/NetDriverInputSink.cs` | Assembly-CSharp | **No** — the bindings folder is predefined by design |
| `Net/Client/ClientVehicleStage.cs` | Assembly-CSharp | Only after **C4** |
| `Net/Diagnostics/ScriptedInputSource.cs` | Assembly-CSharp | Only after **C4** — was "after C3"; the Diagnostics asmdef moved there on 2026-08-26, § 3 |

`Net/Server` ships `autoReferenced: true` today for the same reason. Flipping either to `false`
requires inverting `FpsActorController`'s pull-model into a pushed control surface, which is a
behaviour-shaped change and the thing C2's AC-6 forbids. **It is one step, taken once, after C4
has landed** (C3 no longer produces an assembly, so it is no longer a precondition) — not a
per-phase instruction. Whoever takes it kills `NetBindings` with it,
per § 2.

## 6. Success criteria

1. Each of `Net/Input`, `Net/Diagnostics`, `Net/Client` compiles as its own assembly, with **zero**
   references to `Assembly-CSharp`. — `Net/Input` **met** (C2). The other two are **both C4's**:
   8 of Diagnostics' 13 crossings are `Net/Client` types, so the two assemblies land together or
   the first pays for interfaces the second deletes.
2. Every legacy dependency crossing a seam does so through an interface owned by the sealed side,
   mirroring `Net/Server/Bindings/`.
3. Every phase is graded by a **Unity compile over MCP**, not by `dotnet build`.
4. `tools/check-net-layering.ps1` stays green and gains a rule per new seam.
5. `Net/Diagnostics` is excluded from player builds, and something fails if it is re-included.
   — **Met 2026-08-26.** The exclusion shipped 2026-08-21 (`#if !IRONFRONT_NO_DIAGNOSTICS` on all
   13 files, demonstrated by `EditorBuildWindowsHarness -noDiagnostics`); the *something* is
   `tools/check-diagnostics-exclusion.ps1`, wired into `ci.ps1` and `ci.yml`, nine mutations
   observed RED and one negative control GREEN. It does **not** depend on the asmdef, and is
   written to be deleted when C4 replaces it with `defineConstraints`.
6. `Net/Client` has at least one EditMode test that could not have been written before C4 — the
   deliverable that justifies the phase.

## 7. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| A green `dotnet build` is read as proof of layering | 4 | 5 | **20** | § 4; success criterion 3 names the grading tool |
| C4 starts before C2 and `FpsActorController` gets two rival bindings | 3 | 5 | **15** | § 5's ordering note, repeated in C4's own depends-on line |
| C4's binding count grows past ~10 and the phase never lands | 4 | 3 | 12 | C4 is declared multi-phase up front; it splits by binding cluster, not by file count |
| The track is sold as unlocking P-D6 / P-D9 again | 3 | 3 | 9 | § 2 records both as false, with the reason |
| A refactor lands mid-harness and a run difference becomes unattributable | 3 | 4 | 12 | The start gate: after phase 3E |
