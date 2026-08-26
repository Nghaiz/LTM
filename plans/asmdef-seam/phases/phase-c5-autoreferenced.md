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
