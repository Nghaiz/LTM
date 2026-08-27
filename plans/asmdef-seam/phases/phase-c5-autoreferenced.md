# Phase C5 — `autoReferenced: false`, and `NetBindings` dies with it

- **Track:** [`../plan.md`](../plan.md) · **Effort:** M (3 d)
- **Depends on:** **C4, merged 2026-08-26.** The track's plan states this step is *"one step, taken
  once, after C4 has landed"* — not a per-phase instruction, and explicitly **not C2's**, whose AC-6
  forbids the behaviour-shaped change it needs.
- **Closes:** the asmdef-seam track's last named step. No ledger row; the ledger's asmdef rows
  (**E-11**, **E-11b**) are both closed, and this phase does not reopen either.

---

## 1. What is actually left, enumerated rather than carried

**Every count on this track that was carried rather than measured has been wrong** — `Helicopter 16×`
(C2), `11 files / 15 types` (C3), `31 types / Actor 53×` (C4). So this phase opens with a
measurement, and the measurement immediately found a trap the earlier ones would have walked into.

**The trap: `Net/Input`'s namespace is not its assembly name.** Its files declare
`namespace Ironfront.Net.Unity` — the *shared* namespace — while the assembly is
`Ironfront.Net.Unity.Input`. So `grep -rl "using Ironfront.Net.Unity.Input"` over
`Assembly-CSharp/` and `NetBindings/` returns **0**, and there are five real consumers. **A `using`
grep keyed to the assembly name cannot measure this seam.** Enumerate by TYPE
(`IInputSource`, `LocalInputSource`, `NullInputSource`, …), the way `check-net-layering.ps1` RULE 5b
does.

Measured 2026-08-26, by type for Input and by `using` for the other two (whose namespaces do match
their assembly names):

| Sealed assembly | `Assembly-CSharp` consumers | Of which legacy | Of which `NetBindings/` |
|---|---|---|---|
| `Ironfront.Net.Unity.Input` | **5** | 1 — `FpsActorController.cs` (**31** `inputSource` sites) | 4 — `IronfrontNetBindings`, `LocalInputEnvironmentBinding`, `LocalPlayerRigBinding`, `NetDriverInputSink` |
| `Ironfront.Net.Unity.Client` | **8** | 2 — `DecalManager.cs`, `MinimapUi.cs` | 6 — `ActorPresenceBinding`, `ClientSceneBindings`, `HitmarkerHudBinding`, `LocalPlayerRigBinding`, `ProjectileBodyBinding`, `VehicleBodyBinding` |
| `Ironfront.Net.Unity.Diagnostics` | **2** | 0 | 2 — `LaneBDiagnosticsProbe`, `LegacyMovementProbeBinding` |

**Diagnostics is nearly free** — two files, both bindings, no legacy consumer at all. **Client costs
two legacy files.** **Input costs `FpsActorController`,** and that is the whole of the difficulty.

## 2. Why this is a behaviour change and not four JSON edits

`autoReferenced: true` is what lets `Assembly-CSharp` see a sealed assembly without naming it. Since
`Assembly-CSharp` is predefined, it can never add an explicit reference — so flipping the flag does
not *break* a reference, it **removes the only channel that existed**. Every consumer above must
either move out of `Assembly-CSharp` or stop naming the sealed type.

`FpsActorController` is the hard one, and the plan already says why: it holds a **pull** model —
31 sites reading `inputSource`, *constructing* `LocalInputSource`, holding
`NullInputSource.Instance`. Turning that into a **pushed** control surface (the sealed side writes
into a plain struct or interface `FpsActorController` owns) is a behaviour-shaped change to shipped
gameplay input.

**And `NetBindings/` dies with the flip, deliberately.** That folder exists *because*
`Assembly-CSharp` is the only assembly that sees both halves. Once no sealed assembly is
auto-referenced, a binding living in `Assembly-CSharp` cannot name the interface it implements. Its
12 files move to the side that owns their interface — which is the outcome
[`../plan.md`](../plan.md) § 2 predicted when it said `autoReferenced: false` *"closes that and kills
`NetBindings` with it."*

## 3. Sequencing — cheapest first, and each one is independently shippable

**C5a — Diagnostics.** Two binding files. `Net/Diagnostics` is already `defineConstraints`-excluded
from player builds (C4d), so this touches nothing shipped.

**C5b — Client.** Six bindings plus `DecalManager` and `MinimapUi`. Both legacy files consume client
presenters; each needs the `Net/Server/Bindings/` treatment — an interface owned by the sealed side,
implemented by the legacy component.

**C5c — Input, and `FpsActorController`'s inversion.** The largest, and the one that may reasonably
be deferred with a written reason rather than forced. **If it is deferred, Input keeps
`autoReferenced: true` and the phase says so plainly** — a partially-sealed seam honestly reported is
worth more than a rushed inversion of the game's input path.

**C5d — the `NetBindings` funeral.** Only once a, b and c have moved everything out. Deleting the
folder while a binding still lives there is how a seam becomes a compile error nobody can localise.

## 4. Verification, and why `dotnet build` cannot do it

**`Assets/Scripts/Net/Shared` has zero references from any `dotnet` project, so a green
`dotnet build` proves nothing about layering** — this track's canonical false green
(`green-that-proves-nothing.md`, [`../plan.md`](../plan.md) § 4).

Each step is graded by a **Unity compile driven over MCP**, plus `tools/check-net-layering.ps1`.

**Two things C4d taught that this phase must not forget:**

1. **Verify against the runtime assembly manifest, not the asmdef file.** An asmdef says what was
   *asked for*; the manifest says what the compiler *did*.
2. **`Assembly-CSharp-firstpass` is a second predefined assembly** — 155 type names that every
   measurement on this track was blind to until C4d, and which cost one real miss only the compiler
   caught. Scan it too.

**A new layering rule per flip.** RULE 5 (Input), 6 (Client) and 7 (Diagnostics) each assert a
three-part seam today. Each gains the clause *"…and `Assembly-CSharp` does not name a type from this
assembly"*, and each new clause is **mutation-proved in both directions** before it ships.

## 5. What this phase does not do

- It does not reopen **P-D6** or **P-D9**. [`../plan.md`](../plan.md) § 2 records both as false and
  neither is affected by an `autoReferenced` flip. The V7 tests' subjects (`Weapon`,
  `ThrowableWeapon`, `GrenadeProjectile`, `Projectile`) stay in `Assembly-CSharp` throughout — that
  is the corrected reopening condition in `phase-v7-projectiles.md` § 6.1.1.
- It does not move gameplay code into a sealed assembly to make a reference go away. A seam is an
  interface owned by the sealed side, not a relocation.
- It does not force C5c. Deferring the `FpsActorController` inversion with a written reason is a
  legitimate outcome; leaving it undecided is not.

## 6. Acceptance criteria

1. `Ironfront.Net.Unity.Diagnostics` ships `autoReferenced: false`, verified against the **runtime
   assembly manifest**, with a Unity compile green over MCP (C5a).
2. `Ironfront.Net.Unity.Client` ships `autoReferenced: false`; `DecalManager` and `MinimapUi` reach
   it through interfaces owned by the client side, mirroring `Net/Server/Bindings/` (C5b).
3. `Ironfront.Net.Unity.Input` either ships `autoReferenced: false` with `FpsActorController`
   inverted to a pushed control surface, **or** keeps `true` with a written reason and a named
   reopening condition (C5c). No third outcome.
4. `NetBindings/` is deleted, or the files still in it are named with the assembly each is waiting on
   (C5d).
5. `check-net-layering.ps1` gains one clause per flipped assembly, each mutation-proved in both
   directions.
6. The scan covers `Assembly-CSharp` **and** `Assembly-CSharp-firstpass`.
7. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1`,
   `check-diagnostics-exclusion.ps1` exit 0, and a Unity compile is green over MCP at every step —
   **`dotnet build` alone grades nothing here.**

---

## 7. Outcome — 2026-08-28

**Two of three flipped. `Net/Input` did not, and § 6.3's second branch is why.**

| Assembly | Ships | Seam moved to Shared | Verified |
|---|---|---|---|
| `Ironfront.Net.Unity.Diagnostics` | `autoReferenced: false` | 3 types of 30 | manifest, C5a |
| `Ironfront.Net.Unity.Client` | `autoReferenced: false` | 10 types of 47, plus two facades | manifest, C5b |
| `Ironfront.Net.Unity.Input` | **`autoReferenced: true`** | 1 type (`IInputSource`) | deferred, § 7.3 |

The manifest, for all three predefined assemblies at once — the check § 4.1 asks for, and the
only one that distinguishes *sealed* from *deleted*:

```
Assembly-CSharp           -> [EditorHarness, Input, Server, Shared]
Assembly-CSharp-Editor    -> [EditorHarness, Input, Server, Shared]
Assembly-CSharp-firstpass -> [EditorHarness, Input, Server, Shared]
EXISTING                  -> [..., Client, Diagnostics, ...]
```

### 7.1 The measurement, and the third way it had been wrong

§ 1 opened by saying every carried count on this track had been wrong, then carried three of its
own. Enumerating by type over all predefined sources:

| Sealed assembly | § 1 said | Actually | What the miss was |
|---|---|---|---|
| Input | 5 consumers, `FpsActorController` 31 sites | 5 consumers, **20 sites** | site count carried, not counted |
| Client | **8** | **18** (12 legacy, 6 bindings) | ten reach it FULLY QUALIFIED, no `using` line to grep |
| Diagnostics | **2** | **3** | `IronfrontNetBindings.cs` reaches through a `Diagnostics.` prefix |

C2 miscounted by grepping comments, C4 by grepping substrings, C5 by grepping a `using` line that
was never there. § 1 was right that a `using` grep cannot measure this seam — and then used one
for the two assemblies whose namespaces do match their names.

### 7.2 § 2's prediction is false, and `NetBindings/` survives (C5d)

§ 2 said the bindings would "move to the side that owns their interface". They cannot, and the
reason is structural rather than circumstantial: a binding implements a sealed interface **in terms
of a legacy type** — `DecalSinkBinding` over `DecalManager`, `LaneBDiagnosticsProbe` over `ScoreUi`.
Moving one into the sealed assembly would make that assembly name a legacy type, which is exactly
what RULES 6b/7b forbid. Both halves are pinned by the same wall; only the interface between them
is free to move.

So the interface moved, into `Ironfront.Net.Unity.Shared` — which stays `autoReferenced: true` as
the one declared channel, and which is the move `ICapturePointDirectory` already made in the commit
that added `check-net-layering.ps1`.

**AC-4 answered: `NetBindings/` is NOT deleted, and none of its twelve files is waiting on an
assembly.** They are the `Assembly-CSharp` halves of seams whose other halves are sealed, which is
their permanent and correct home. AC-4 offered "deleted" or "waiting"; the tree gives a third
answer, recorded rather than forced into one of the two.

### 7.3 C5c deferred — the written reason AC-3 requires

`Ironfront.Net.Unity.Input` keeps `autoReferenced: true`. The predefined side names **8 of its 11
types**: `IInputSource`, `NullInputSource`, `NetInputSource`, `HelicopterAxes`,
`ILocalInputEnvironment`, `HelicopterControlOptions`, `HelicopterControlStyle`, `NetInputBindings`
— plus `LocalInputSource` and `InputShadowCompare`, which `FpsActorController` *constructs*.

Sealing it therefore means relocating six types into Shared and factory-ising two more, leaving
five behind. Two of the six — `NetInputSource` and `HelicopterAxes` — are **implementations, not
interfaces**. That is hollowing the assembly out to make a reference go away, which § 5 forbids in
terms: *"a seam is an interface owned by the sealed side, not a relocation."*

The inversion AC-3 offers as its first branch would seal Input honestly, but it rewrites 20 reads
on the game's shipped input path inside a refactor whose acceptance criteria forbid behaviour
change. Neither branch is cheap; the second is the honest one.

**Reopening condition, named as AC-3 requires:** when `FpsActorController`'s pull model is inverted
to a pushed control surface **for a reason of its own** — a gameplay or input-latency requirement,
not a layering one — `Ironfront.Net.Unity.Input` flips in the same change. Not before, and not to
close this phase.

`IInputSource` moved to Shared anyway, ahead of its phase: `ILocalPlayerRig.InputSource` returns one
and `LocalPlayerRigBinding` implements that interface from `Assembly-CSharp`, so C5b could not land
until it sat in an auto-referenced assembly. `Net/Input` gains a reference to Shared for it.

### 7.4 A third predefined assembly (AC-6, widened)

AC-6 asked the scan to cover `Assembly-CSharp` **and** `Assembly-CSharp-firstpass`. There is a
third. `Assets/Editor/` compiles into `Assembly-CSharp-Editor`, and
`Assets/Editor/NetVerificationHarness.cs` held `using Ironfront.Net.Unity.Client;` with five
`NetClientBootstrap` reads that no enumeration on this track had ever seen. C4d widened the scan
from one population to two and stopped one short; the Unity compile found the third, exactly as it
found the second.

It is fixed by explicit reference rather than by another seam — `autoReferenced: false` has never
prevented one. The harness moved into `Ironfront.Net.Unity.EditorHarness`, an Editor-only asmdef
naming Client directly, and its single legacy read (`ActorManager.spawnPoints.Length`) now goes
through `ISpawnPointDirectory.Count`, the seam the server assembly already declared for that data.
`check-net-layering.ps1` gains `-EditorPath`; its declared-type count moved 558 → 566, which is the
evidence the population is read rather than merely configured.

### 7.5 The gate (AC-5)

RULE 6d and RULE 7d, one clause per flipped assembly, each in two halves because one name can be
written two ways: a **qualified** `Ironfront.Net.Unity.<Asm>.` prefix — how the legacy tree actually
reaches these assemblies, and invisible to a `using` grep — and a **type name**, skipping any name
the predefined sources declare themselves (which is what keeps `TimedObjectActivator.Entry` from
reporting itself against `LaneBExplosionLog.Entry` with no allow-list row).

Each also asserts the flag directly, because 6a/7a stay green straight through a flip back to
`true`: the asmdef still exists and still carries its `defineConstraints` line, which is all they
read.

Mutation-proved before shipping, seven ways:

| Mutant | Fires |
|---|---|
| Diagnostics flag → `true` | RULE 7d (channel reopened) |
| Diagnostics, qualified crossing | RULE 7d (channel bypassed) `(qualified)` |
| Diagnostics, unqualified via `using` | RULE 7d (channel bypassed) `LaneBHarness` |
| Diagnostics, stale allow-list row | RULE 7d (stale) |
| Client flag → `true` | RULE 6d (channel reopened) |
| Client, qualified + unqualified crossings | RULE 6d, both |
| Crossing in `Assets/Editor/V0BehaviouralPass.cs` | RULE 6d — proves the third population is scanned |

One mutant proved nothing, and it is recorded because that is mutation testing's own failure mode:
the first Diagnostics mutant named `IDiagnosticsProbe`, which had already moved to Shared, so the
gate was right to stay green. A mutant must be a real instance of the fault, not one that looks
like it.

### 7.6 What the seal is worth, counted

Types reachable from `Assembly-CSharp` fall from **~158 to ~112**. Client (37) and Diagnostics (27)
are gone; Shared grew by 21 absorbing their seam, and Input's 11 remain. The wall is real and
narrower — it is not absolute, and Shared being `autoReferenced: true` is the deliberate reason why.

### 7.7 A near-miss worth recording

The relocated `NetPresenterGate.IsLocalActor` was first written with only the `actor == null` guard.
The shipped predicate is `actor == null || !actor.Exists` — the second half rejects a **destroyed**
body, which arrives non-null because the interface carries none of `UnityEngine.Object`'s overloaded
equality. Dropping it would have made a destroyed local actor start answering `true`: a behaviour
change wearing a refactor's clothes, in the one phase whose criteria forbid exactly that. It was
caught by reading the original, not by any gate — no test in this repo covers a destroyed local
actor's identity, and that gap is now on the record.

### 7.8 Verification

`dotnet test` 1857/1857 · Unity EditMode 87/87 · `SpecChecker` · `ClientWiringGate` ·
`check-net-layering` · `check-diagnostics-exclusion` · `check-unity-meta` ·
`check-duplicate-assemblies` · `check-plugin-define-constraints` — all exit 0. Unity compile green
over MCP at both C5a and C5b. `dotnet build` graded nothing here, as § 4 requires.
