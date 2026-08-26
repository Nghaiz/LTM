# Phase C3 — `Net/Diagnostics` out of the player build, and why the asmdef waits

- **Track:** [`plan.md`](../plan.md) · **Effort:** S (1–2 d) · **Landed:** 2026-08-26 (partial — see § 0.3)
- **Depends on:** [`phase-c2-net-input.md`](phase-c2-net-input.md)

---

## 0. What the enumeration returned, and what it cost this phase

> [`plan.md`](../plan.md) § 3 marked this row **unverified** because it came from the same
> substring grep that told C2 there were "~8" legacy types when there were two. It was right to.

### 0.1 The counts

| Claimed | Measured |
|---|---|
| 11 files | **13** |
| 15 distinct legacy types | **13.** `Path` is `System.IO.Path`, not `Pathfinding/Path.cs`. `State` is `client.State` on the transport client, matched against a **`private`** nested enum in `ActiveRaggy`. Both are bare-identifier collisions — the exact class of error C2 found, one folder over |
| "the overlays — `VehicleReplicationOverlay`, `TransportDebugOverlay`, `MovementShadowCompare` — plus the scripted-input driver" | **Two of those three overlays name nothing.** `MovementShadowCompare` and `TransportDebugOverlay` are clean, as are `ScriptedAim`, `ScriptedInputCursor`, `ScriptedInputProgramme`, `ScriptedInputSource`, `LaneBExplosionLog`, `LaneBSpawnPin` and `IronfrontLog` — **9 of 13 files**. All 13 crossings live in 4 files, and `LaneBCheckpointRecorder.cs` alone carries every one |

Measured with RULE 5b's discipline — comment lines dropped, double-quoted literals blanked,
ordinal comparison — because a measurement that skips any of the three is how the plan got here.

### 0.2 The finding that changed the phase's shape

**Only 5 of the 13 crossings are legacy at all.** The other 8 are declared in `Net/Client`.

| Where the type lives | Count | Types |
|---|---|---|
| `Assembly-CSharp` (genuinely legacy) | **5** | `CapturePoint`, `FpsActorController`, `MatchScoreboard`, `ScoreUi`, `Vehicle` |
| `Net/Client` — **C4's folder** | **8** | `ClientPredictionStage`, `ClientVehicleStage`, `NetClientBootstrap`, `NetClientCombatPresenter`, `NetClientLocalCombatDriver`, `NetClientVehicle`, `RemoteActorRegistry`, `RemoteVehicleRegistry` |

`Net/Client` is inside `Assembly-CSharp` **today**, which is the only reason those 8 look like
legacy crossings. Sealing `Net/Diagnostics` before C4 means declaring 8 interfaces whose entire
purpose evaporates the moment `Net/Client` becomes an assembly `Ironfront.Net.Unity.Diagnostics`
can name in its `references` list. Diagnostics observing client state is what diagnostics is for;
no rule forbids that edge, and RULE 4 already permits Diagnostics the *server* namespace.

`Vehicle` sharpens it: the only reference is `vehicle.Vehicle.transform`, reached through
`NetClientVehicle.Vehicle`, which is declared **`internal`**. Post-C4 that member is not visible
across the assembly boundary at all, so the binding C3 would write for it is not merely redundant
later — it is answering a question that stops existing.

### 0.3 What this phase therefore shipped, and what it did not

**Shipped: the gate.** § 2's item 4 and [`plan.md`](../plan.md) success criterion 5 — *"something
fails if it is re-included"* — had **no implementation**. The exclusion itself has been real since
2026-08-21: all 13 files are wrapped in `#if !IRONFRONT_NO_DIAGNOSTICS`, and
`EditorBuildWindowsHarness`'s `-noDiagnostics` flag demonstrates it by building the same player
both ways. Nothing checked that it stayed that way. `tools/check-diagnostics-exclusion.ps1` is
now that check, and § 6 records each of its rules observed RED first.

**Deferred to C4: the asmdef.** Per § 0.2, and by decision on 2026-08-26 rather than by drift.
The mechanism when it comes is one line — `defineConstraints: ["!IRONFRONT_NO_DIAGNOSTICS"]` —
replacing 13 `#if` blocks, and at that point RULE 1 and RULE 2 of the new gate are deleted and
RULE 3 is replaced by the compiler. The gate is written to be thrown away; its header says so.

**This is not the phase quietly shrinking.** § 1 of the original text already argued that "the
seam is worth less here than the *exclusion* is". The enumeration turned that from a framing into
a measurement: the exclusion was the unguarded half, and the seam was the half that would have
been rebuilt at C4.

## 1. Scope

Thirteen files, 13 real crossings, of which 5 are legacy. **These are test-only**, which is what
makes the exclusion the deliverable: diagnostics compiled into a shipped player is code paths and
allocation nobody asked for, in a build nobody profiles for it.

## 2. Work

1. ~~Enumerate the 15 legacy types.~~ **Done — see § 0. Thirteen, and 8 of them are `Net/Client`.**
2. ~~Asmdef with a platform/define constraint that keeps it out of player builds.~~
   **Deferred to C4, § 0.2.** The define constraint is the right mechanism; the assembly it
   attaches to cannot exist without 8 interfaces C4 deletes.
3. ~~Unity compile in both configurations.~~ **Not applicable to what shipped** — see § 5.
4. ~~A gate that fails if the exclusion is undone, observed RED.~~ **Done —
   `tools/check-diagnostics-exclusion.ps1`, four rules, nine mutations, evidence in § 6.**
   Wired into `tools/ci.ps1` (step 3f) and `.github/workflows/ci.yml` in the same commit.

## 3. The thing to watch

Lane B's runner **depends on these overlays**, so excluding diagnostics from player builds must
not exclude it from the **harness** build.

**It cannot, and that is structural rather than lucky.** `IRONFRONT_NO_DIAGNOSTICS` is opt-in and
inverted: `BuildPlayerOptions.extraScriptingDefines` can only *add* a symbol, so the guard is true
everywhere by default and the lane-B harness would have to pass `-noDiagnostics` to strip itself.
The gate's RULE 4 holds the other end — the one build that ever sets the symbol must keep setting
it, or the strip becomes a mechanism nobody exercises and rules 1–3 grade nothing.

Since this phase changed no file under `Assets/`, lane B's inputs are byte-identical. **AC-4 is
therefore satisfied by argument, not by a run** — and that is stated rather than glossed, because
the original criterion demanded a run precisely to stop this sentence being written carelessly.
The argument is checkable: `git diff --stat` for this phase touches `tools/`, `.github/` and
`plans/` only.

## 4. Acceptance criteria

1. **Deferred to C4** — `Net/Diagnostics` does not compile as its own assembly. § 0.2 gives the
   measurement, § 0.3 the decision.
2. **Met, on the half that was missing.** It is excluded from player builds (since 2026-08-21),
   and a gate now fails when the exclusion is removed — nine mutations, each observed RED, plus a
   negative control that stays GREEN. § 6.
3. **Void — no Unity source changed.** Both configurations were last compiled 2026-08-21. A
   ceremonial re-compile of an untouched tree would be evidence of nothing, and running one to
   tick a box is how a green stops meaning anything.
4. **Met by argument, not by a run.** See § 3. No file under `Assets/` was touched.
5. **Void — no interfaces were declared.** The criterion presumed the asmdef.

## 5. Risk — how each one landed

| Risk | Score | Outcome |
|---|---|---|
| The exclusion silently breaks lane B | **15** | **Did not fire, and cannot from this change** — nothing under `Assets/` was touched. § 3 |
| Only the "included" configuration is compiled | 9 | **Void.** No compile was performed, and § 4 says so instead of claiming one |
| The exclusion gate is written un-failable | 8 | **Did not fire.** Nine mutations RED before shipping, § 6. The first draft of RULE 4 *was* un-failable in the other direction — it fired on a healthy tree — and § 6 records that too |
| *(unforeseen)* The plan's own measurement is wrong, again | — | **Fired, as [`plan.md`](../plan.md) § 3 warned it would.** 11→13 files, 15→13 types, and 8 of the 13 belong to C4. Cost the phase its asmdef and three of five acceptance criteria |
| *(unforeseen)* The seam would have been rebuilt at C4 | — | **Caught before it fired**, by enumerating first. This is what § 3's "do not size either phase from this table" was for |

## 6. Evidence

### The gate, on the unmutated tree

```
=== Net/Diagnostics compiles out of a shipping player ===
PASS: 13 file(s) under Net/Diagnostics, each wrapped whole in '#if !IRONFRONT_NO_DIAGNOSTICS'.
      RULE 3: 16 top-level type(s) declared there, named by none of the 551 other
              .cs file(s) under Ironfront_Reborn/Assets — comments and string literals excluded.
      SCOPE: this is a claim about Assets/. The linked .NET test projects DO name
             these types and are meant to: they compile the folder a second time with
             the define absent, and never ship in a player.
      RULE 4: EditorBuildWindowsHarness still builds the excluded configuration, so
              the strip above is demonstrated rather than asserted.
```

`551` is the population, stated because a rule of this shape is only as good as what it scanned.

### Every rule observed RED first

| # | Mutation | Expected | Observed |
|---|---|---|---|
| M1 | guard deleted from `TransportDebugOverlay.cs` | RED, RULE 1 | exit 1, RULE 1 naming the file |
| M2 | a new unguarded `.cs` lands in the folder | RED, RULE 1 | exit 1, RULE 1 naming the new file |
| M3 | guard flipped to positive `#if IRONFRONT_DIAGNOSTICS` | RED, RULE 1 | exit 1 — the literal match is what catches this; a looser pattern would have passed |
| M4 | `public static class …` appended after the closing `#endif` | RED, RULE 2 (partial) | exit 1, quoting the trailing line and its number |
| M5 | trailing `#endif` deleted | RED, RULE 2 (unbalanced) | exit 1, `depth 1 at EOF` |
| M6 | `typeof(TransportDebugOverlay)` added to `Net/Client/LobbyShellOverlay.cs` | RED, RULE 3 | exit 1, one finding |
| M7 | **negative control** — six Diagnostics type names in a comment *and* in a string literal | **GREEN** | exit 0 |
| M8 | the define const renamed to `IRONFRONT_NOPE` | RED, RULE 4 | exit 1 |
| M9 | `extraScriptingDefines` renamed away | RED, RULE 4 | exit 1 |

M7 is the one that matters most. `LobbyShellOverlay.cs` legitimately discusses
`TransportDebugOverlay` in three doc-comments today; a gate that flags prose is a gate whose first
fix is deleting the prose. M3 is the mirror: it fails *because* the match is exact, and an
inverted guard is the single change that would silently strip the folder from every build
including lane B's.

### A rule that was un-failable in the other direction, and why

RULE 4's first draft reused `Get-CodeLines`, which blanks double-quoted literals — and it reported
**FAIL on a healthy tree**. A scripting define can only reach `BuildPlayerOptions` as a string;
`private const string NoDiagnosticsDefine = "IRONFRONT_NO_DIAGNOSTICS"` *is* a literal, so the
helper erased the only evidence the rule looks for. Fixed by stripping comment lines alone there,
with the reason recorded inline so nobody "tidies" it back to the shared helper.

Worth stating plainly: the failure was loud, so it cost minutes. Had the false positive gone the
other way — a rule that could not fail — it would have shipped green and been believed.

### What RULE 3 replaced

`MovementShadowCompare.cs`'s header carries:

> *"Nothing outside `Assets/Scripts/Net/Diagnostics/` names a type from this folder: the ten
> mentions elsewhere are doc-comments, checked 2026-08-21."*

A stored negative result with nothing re-checking it, and it is the claim that makes the strip
safe — a stripped folder whose types are named from outside leaves a dangling reference. Re-run
2026-08-26: **fifteen** mentions across 9 files, still all doc-comments. The claim held; the count
had moved 50% in five days and nobody had looked. RULE 3 is that sentence made to fail.

### The gap that is recorded rather than gated

All 13 `.cs.meta` files carry a GUID, and **zero** of those GUIDs appear in any `.unity` or
`.prefab` under `Assets/` — every diagnostics component is added at runtime with `AddComponent`,
so the strip cannot orphan an authored one. Not gated: dropping `TransportDebugOverlay` onto a
scene to look at something is a legitimate debugging move, and a gate that fails an investigation
gets deleted during the investigation. The failure it would prevent is a missing-script *warning*
in a shipping client build that does not exist yet. The gate's header carries this so the next
reader knows the question was asked, not missed.

### Regression

`dotnet test` across the solution, and `tools/ci.ps1`. Numbers in the phase report.
