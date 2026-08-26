# Phase C2 — `Net/Input` behind a control-surface binding

- **Track:** [`plan.md`](../plan.md) · **Effort:** M (3 d) · **Landed:** 2026-08-26
- **Depends on:** [`plans/debt-closure/phases/phase-3e-run-and-ledger.md`](../../debt-closure/phases/phase-3e-run-and-ledger.md)
- **Unblocks:** [`phase-c4-net-client.md`](phase-c4-net-client.md) — which must not start before this lands

---

## 0. What the enumeration returned, and what it cost this phase

> § 3.1 below said the count was ~8 and to enumerate rather than trust it. **It is two.**

`Net/Input`'s eight files name exactly **`LoadoutUi`** and **`OptionsUi`** — both static-singleton
reads, both in `LocalInputSource`, with `LoadoutUi` also read by `InputShadowCompare`. `Helicopter`
and `FpsActorController`, the two types this phase was built around, are **not referenced at all**:

| Claimed | Measured |
|---|---|
| `Helicopter` — 16 references | 16 substring hits, **0 references**. All are `Net/Input`'s own `HelicopterAxes` / `HelicopterControls` / `HelicopterAxisMap`, or the `"Helicopter Pitch"` axis string |
| `FpsActorController` — 15 references | 15 substring hits, **0 references**. Fourteen are XML doc comments; the fifteenth is inside a `Debug.Log` string literal |
| ~8 distinct legacy types | **2** — `LoadoutUi`, `OptionsUi` |

The original measurement was a substring grep that counted comments and the folder's own type
names. Two things follow, and both are recorded rather than quietly worked around:

1. **§ 2's control-surface design and AC-3 had no subject.** `Helicopter` and `FpsActorController`
   cannot share an abstraction in a phase that touches neither. The risk AC-3 guards is real but
   belongs to **C4**, which does reach `FpsActorController` — see [`plan.md`](../plan.md) § 5.
2. **`tools/check-net-layering.ps1` RULE 5b strips comments and string literals before matching**,
   so the class of error that produced those three numbers cannot produce a finding again.

## 1. Scope

Eight files, two legacy types, one binding. The folder move and the binding are both small; what
made the phase worth three days was establishing which of the numbers in the plan were real.

## 2. The pattern, already in the repo

`Net/Server/Bindings/` — `IAiDriver`, `ICapturePointDirectory`, `ISpawnPointDirectory`. The sealed
side declares an interface; a legacy `MonoBehaviour` implements it; the sealed side never names the
legacy type. Copied, not reinvented.

`LoadoutUi` and `OptionsUi` are both *the client context a raw device reading is interpreted in* —
read by the same object, on the same frame, for the same purpose — so they take **one** interface
between them, `ILocalInputEnvironment`, not one each.

## 3. Work

1. ~~Enumerate every legacy type `Net/Input`'s eight files name.~~ **Done — see § 0. Two.**
2. ~~Declare the binding interfaces in the smallest set that covers the enumeration.~~
   **Done — one interface, `Net/Input/Bindings/ILocalInputEnvironment.cs`, plus the
   `NetInputBindings` registry mirroring `NetSceneBindings`.**
3. ~~Implement them on the legacy components.~~ **Done —
   `NetBindings/LocalInputEnvironmentBinding.cs`, registered from `IronfrontNetBindings.Install`
   at `BeforeSceneLoad`, which precedes the first `FpsActorController.Awake`.**
4. ~~Add the asmdef.~~ **Done — `Ironfront.Net.Unity.Input`, `references: []`.**
   **`autoReferenced` shipped `true`, not `false`.** Four consumers cannot add an explicit
   reference and two of them never will; the flip is one step after C3 and C4, and the table
   naming each consumer is in [`plan.md`](../plan.md) § 5.
5. ~~Unity compile over MCP.~~ **Done — output kept, see § 6.**
6. ~~Add the `check-net-layering.ps1` rule, observed RED first.~~ **Done — RULE 5a (the asmdef
   exists) and RULE 5b (no `Net/Input` source names a predefined-assembly type), each mutated
   and observed RED before shipping. Evidence in § 6.**

## 4. Acceptance criteria

1. **Met.** `Net/Input` compiles as `Ironfront.Net.Unity.Input` with `references: []` — zero
   `Assembly-CSharp` references, and none possible.
2. **Met.** The only two crossings, `LoadoutUi` and `OptionsUi`, go through
   `ILocalInputEnvironment`, owned by the sealed side.
3. **Void — the premise was false.** Neither `Helicopter` nor `FpsActorController` is referenced
   by `Net/Input`; there was no shared surface to get right. Moved to C4, which does reach
   `FpsActorController`. See § 0.
4. **Met.** RULE 5a and RULE 5b were each observed RED against a deliberate violation before
   landing, and green after. § 6 records the mutations.
5. **Met.** Graded by a Unity compile over MCP. `dotnet build` was not used as the grade.
6. **Met.** Behaviour preserved read-by-read. The helicopter-style fall-through is the one place
   it could have drifted: `LocalInputSource` tested CUSTOM, then BATTLEFIELD, then fell through to
   the ARMA mapping for *any* other value. `LocalInputEnvironmentBinding.ToStyle` reproduces that
   `default` arm rather than throwing, which is what keeps this a move.

## 5. Risk — how each one landed

| Risk | Score | Outcome |
|---|---|---|
| Eight one-type interfaces instead of one abstraction | 12 | **Did not fire.** Two types, one interface |
| `dotnet build` used as the grade | **15** | **Did not fire.** Graded by MCP compile |
| Behaviour drifts during the move | 8 | **Did not fire.** The one drift-prone site — the style fall-through — is called out in AC-6 and preserved explicitly |
| *(unforeseen)* The plan's own measurement is wrong | — | **Fired.** Cost the phase its stated design and one acceptance criterion. Mitigated forward: RULE 5b cannot repeat the error, and `plan.md` § 3 now marks the C3/C4 counts unverified because they came from the same grep |

## 6. Evidence

All of it produced 2026-08-26 against the live Editor over MCP, Unity `6000.3.21f1`.

### The compile, and why "it compiled" is not the claim

`AssetDatabase.Refresh(ForceUpdate)`, then the loaded assemblies read back by reflection:

```
Input asm loaded: True | Assembly-CSharp loaded: True
Input refs: Ironfront.Net.Protocol, netstandard, UnityEngine.CoreModule, UnityEngine.InputLegacyModule
refs Assembly-CSharp: False
```

**`refs Assembly-CSharp: False` on its own proves nothing** — Unity refuses to let any asmdef
reference `Assembly-CSharp`, so that line reads `False` whether or not this phase did anything.
What carries AC-1 is the pair: the assembly **exists** (`Input asm loaded: True` — it reads
`False` the moment the asmdef goes missing), and `IInputSource`, `LocalInputSource`,
`ILocalInputEnvironment`, `NetInputBindings`, `HelicopterControlOptions` and
`HelicopterControlStyle` all resolve **inside it** rather than inside `Assembly-CSharp`.

`LoadoutUi`, `OptionsUi` and `LocalInputEnvironmentBinding` resolve in `Assembly-CSharp`, and
`ILocalInputEnvironment.IsAssignableFrom(LocalInputEnvironmentBinding)` is `True` — AC-2.

### The seam agrees with the code it replaced — AC-6

Read through the binding and straight off the legacy statics in the same call:

```
[3] seam:   LoadoutScreenOpen=False Style=Arma MouseSens=0.5 HeliSens=0.5 InvertPitch=False InvertThrottle=True
[4] legacy: IsOpen=False helicopterType=1 mouseSensitivity=0.5 helicopterSensitivity=0.5 heliInvertPitch=False heliInvertThrottle=True
[5] SEAM AGREES WITH LEGACY ON EVERY FIELD: True
```

`Style=Arma` against `helicopterType=1` is the useful part: `Arma` is reached through
`ToStyle`'s `default` arm, which is the one place behaviour could have drifted, and it is the
arm the shipped `PlayerPrefs` default actually lands on. Enum pinning checked separately —
`Battlefield 0 / Arma 1 / Custom 2` each equal their `HELICOPTER_TYPE_*` constant.

### A grade that measured the wrong population, and the fix

The first wiring check read `NetInputBindings.Environment` in **edit mode** and reported
`WIRED: False`. That is correct behaviour, not a defect: `IronfrontNetBindings.Install` is
`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` and edit mode never runs it. **Whoever grades
C3 or C4 will hit this** — a registry read outside Play mode returns the null object and looks
like a broken binding. The check was re-run as: assert the attribute is present and its
`loadType` is `BeforeSceneLoad`, invoke `Install` explicitly, then re-read. `WIRED: True`.

### Gate mutations — each rule observed RED before it shipped

| Mutation | Expected | Observed |
|---|---|---|
| `LoadoutUi.IsOpen()` reintroduced into `LocalInputSource` | RED, RULE 5b | exit 1, **one** finding naming `LoadoutUi` |
| asmdef removed | RED, RULE 5a | exit 1, RULE 5a only — RULE 5b stayed silent, so the two regressions do not mask each other |
| `LoadoutUi OptionsUi FpsActorController Helicopter Vehicle Actor` in a comment **and** a string literal | **GREEN** | exit 0 — the negative control, and the property the original substring grep lacked |
| unregistered binding read through the seam | one `LogError`, zeroed values | exactly one `[net-input] …never set…`, `MouseSens=0 HeliSens=0` |

The first run of RULE 5b was itself wrong and is worth recording: PowerShell hashtables and
`-match` are case-**insensitive**, so it matched the local `options` against the type `Options`
and the parameter `weaponSlot` against `WeaponSlot`, and reported both as legacy references.
C# is case-sensitive; the rule now uses an ordinal `HashSet`. A gate that fires on every
lowercase local is one nobody keeps.

### The regression this phase actually caused, and CI caught

`VehicleClientSourceInvariantTests.TheHelicopterScalingLivesOnTheSenderRatherThanTheController`
went **RED on the first push**. It reads `LocalInputSource.cs` off disk and asserts it contains
`helicopterSensitivity`, `heliInvertPitch`, `heliInvertYaw`, `heliInvertRoll`,
`heliInvertThrottle` — the `OptionsUi.Options` field names. The seam re-spells all five.

The invariant it pins is **where the scaling happens**, not how the fields are cased, and that
invariant is intact — so the names were updated rather than the assertions dropped. Two things
were then strengthened, both only possible because of this phase:

- **`NoOptionsUiReadOnAnyServerRolePath` widened from `Net/Server` to all of `Net/`.** It had to
  stop at `Net/Server` because `LocalInputSource` was the one file under `Net/` that legitimately
  read `OptionsUi`. It no longer does, so `Net/` now contains **zero** reads and the gate says
  so — a superset of the V5-D9 claim it made before.
- **The far end of "moved, not deleted" is now pinned too.** The five legacy field names must
  still be read by `LocalInputEnvironmentBinding`, and `LocalInputSource` must not name
  `OptionsUi` at all. Without the first, every assertion above passes on a seam that silently
  drops the invert flags.

Both were mutation-tested: `options.heliInvertYaw` → `options.heliInvertPitch` in the binding —
the exact copy-paste bug the pin exists for — went **RED**; a re-added `OptionsUi.GetOptions()`
in `LocalInputSource` went **RED on two tests**; reverting both went green.

**The miss was mine:** only `Ironfront.Client.Input.Tests` was run before pushing, because it is
the project that links `Net/Input` sources. The test that broke lives in
`Ironfront.Net.Replication.Tests` and reads those sources off disk rather than linking them, so
project-affinity was the wrong way to choose what to run.

### Regression

`dotnet test` across the whole solution — **1780 passed, 0 failed** (39 of them
`Ironfront.Client.Input.Tests`). That project links six
`Net/Input` sources and compiles them a second time under stricter settings (nullable,
warnings-as-errors). Its one rule is that a linked file must not touch `UnityEngine`, which is
why `NullLocalInputEnvironment` was split out of `ILocalInputEnvironment.cs`: the interface,
the enum and the options struct stay linkable for whoever writes the first test against them.

Unity Console after the run: one pre-existing error from the MCP plugin's own
`McpManagerClientHub`, timestamped at Editor startup and unrelated to this change. No compile
errors.
