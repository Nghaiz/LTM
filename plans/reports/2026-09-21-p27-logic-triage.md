# P27 — 506 lines read, one real defect, and it was not the decompiler

Implementation report, 2026-09-21. Phase: [`phases/phase-p27-logic-triage.md`](../phases/phase-p27-logic-triage.md).
Closes the port-back track (P22–P27).

**The phase asked which of the diverging files are Ironfront on purpose and which are things the
2017 decompilation lost. Every line was read. Zero are losses from the decompilation.**

One real defect turned up anyway, and it belongs to a category the plan's taxonomy does not have:
a regression **we** introduced during our own refactor. `MatchScoreboard.Reset` shipped with zero
callers, so the second offline match in a process opens on the previous round's score with
`GameEnded` still latched — and can never end. Fixed, gated, and the gate mutation-tested.

The §7 risk the phase was most afraid of — restoring an (a) line and deleting netcode — never
came close to firing. 303 of 506 candidate lines are deliberate Ironfront work, and 12 of those
carry an in-place doc comment naming the defect they fixed and its ledger id.

---

## 1. The filter, and why the phase's own §5.1 was the wrong shape

§5.1 asks for a machine pass that normalises both sides and re-diffs, so that what survives is
worth reading. That works, but it reads twice as much as it needs to. One observation halves it:

> **A loss can only show up as a line the original has and we do not.**

An added line is, by construction, not something we lost. Half of a unified diff is therefore
irrelevant to the question — and our side adds a great deal, most of it doc comments.

`tools/classify_recovered_diff.py` drops the additions, the comments, the decompiler-rendering
differences and the lines that merely moved:

| Stage | Lines |
|---|---|
| Changed lines across all diverging files | 8,547 |
| After dropping additions, comments and moves | 809 |
| After normalising decompiler rendering (bucket (d) at the line level) | **663** |
| Less the six A* files P24 already classified (§9) | **506** |

506 lines across 70 files, 75 of which have no counterpart in our tree at all. That is a list a
person can read, and all 506 were read.

**The filter does not assign buckets, deliberately.** A regex cannot tell "deleted the pause key
because multiplayer has no pause" from "dropped a statement by accident" — both are a line we do
not have. A bucket column would have been a number nobody could check.

### The only reason to trust that number

The filter discards 94% of its input, so it has every opportunity to be quietly wrong.
`--self-test` injects five kinds of real defect into a file pair that currently has zero
survivors and requires the detector to report each one:

```
change-constant      RED (caught)     MinimapCamera.cs       private const int RESOLUTION = 1024;
flip-operator        RED (caught)     LightweightRVO.cs      for (int i = 0; i < agentCount; i++)
flip-equality        RED (caught)     LightweightRVO.cs      if (rVOSimulator == null)
negate-condition     RED (caught)     MountedWeapon.cs       if (!HasLoadedAmmo() && configuration.forceAutoReload)
delete-statement     RED (caught)     MountedWeapon.cs       spareAmmo = configuration.spareAmmo;
```

Mutation subjects are chosen from files whose two renderings genuinely *differ* — mutating a file
the trees agree on byte-for-byte would pass without ever consulting the normaliser — and the round
trip is byte-exact, because every file here is CRLF and a self-test that rewrites one as LF has
modified the repo to prove the repo is unmodified.

### What it cannot see, stated plainly

- **Position.** It is order-insensitive, so a statement reordered *inside* a method is invisible.
  Deliberate: the two decompilers emit members in different orders and an order-sensitive diff
  drowns in that. Between two renderings of one program a genuine reorder is vanishingly rare and
  would come with other changed lines.
- **A line that was already divergent.** If the original's line was already a candidate, deleting
  our rewritten version of it changes nothing in the set. Found by mutation-testing `--check` and
  watching it stay green; the positive case (a currently-matching line) does go red.
- **Anything outside the 506.** The count is a claim about lines the original has that we do not,
  in the 322 files both trees contain — not about the 12 files only we have, nor the 196 only the
  original has.

## 2. Re-measurement: 105 files, not 98

| | Plan §3, 2026-09-20 | This run, 2026-09-21 |
|---|---|---|
| Files in both trees | 322 | 322 |
| Identical after whitespace | 224 | 217 |
| Differing | 98 | **105** |

P23, P25 and P26 all merged between the two measurements and all three touched `Assets/`, so the
set was expected to move. 105 is the number this report is about. Two basenames are ambiguous
(`AssemblyInfo.cs`, `Extensions.cs`) and are reported rather than silently resolved.

## 3. The classification — 506 lines, all read

| lane | files | survivors | (a) | (b) | (c) | (d) |
|---|---|---|---|---|---|---|
| 1 Actor, Hitbox, Seat, ActiveRaggy | 4 | 52 | 37 | 12 | 0 | 3 |
| 2 Vehicle, VehicleSpawner, Car, Tank, Boat, Helicopter, CarHorn | 7 | 60 | 51 | 9 | 0 | 0 |
| 3 FpsActorController, AiActorController, Squad, +4 | 7 | 73 | 56 | 7 | 0 | 10 |
| 4 ScoreUi, MinimapUi, IngameUi, LoadoutUi, +4 | 8 | 83 | 63 | 3 | **4** | 13 |
| 5 CapturePoint, SpawnPoint, ActorManager, +5 | 8 | 78 | 43 | 4 | 0 | 31 |
| 6 Weapon, turrets, throwables, +6 | 10 | 52 | 33 | 5 | 0 | 14 |
| 7 projectiles, +5 | 9 | 24 | 20 | 2 | 0 | 2 |
| 8 A* library remainder | 22 | 84 | 0 | 0 | 0 | 84 |
| **total** | **70** | **506** | **303** | **42** | **4** | **157** |

The four (c) lines are one finding (§4). Per-file counts for the largest files:

| file | surv | (a) | (b) | (c) | (d) |
|---|---|---|---|---|---|
| ScoreUi.cs | 56 | 45 | 0 | 4 | 7 |
| Actor.cs | 47 | 36 | 8 | 0 | 3 |
| FpsActorController.cs | 40 | 40 | 0 | 0 | 0 |
| Vehicle.cs | 24 | 20 | 4 | 0 | 0 |
| ActorManager.cs | 23 | 15 | 0 | 0 | 8 |
| AiActorController.cs | 23 | 12 | 4 | 0 | 7 |
| CapturePoint.cs | 20 | 16 | 0 | 0 | 4 |
| ProceduralWorld.cs | 19 | 0 | 0 | 0 | 19 |
| VehicleSpawner.cs | 18 | 18 | 0 | 0 | 0 |
| TankTurret.cs | 13 | 10 | 0 | 0 | 3 |
| MountedTurret.cs | 11 | 9 | 0 | 0 | 2 |
| Weapon.cs | 9 | 9 | 0 | 0 | 0 |
| Helicopter.cs | 9 | 4 | 5 | 0 | 0 |
| LoadoutUi.cs | 9 | 3 | 3 | 0 | 3 |

**All 42 (b) are real Unity API removals**, and they are dominated by one pair: `Rigidbody.velocity`
→ `linearVelocity` and `.drag`/`.angularDrag` → `linearDamping`/`angularDamping`, 20 sites across
Actor, Vehicle, Helicopter, Hitbox and ActiveRaggy, every value unchanged. The rest are
`Transform.FindChild` → `Find`, `TerrainData.heightmapWidth`, `Mesh.Optimize()` (removed outright)
and `JointDriveMode` (P25's, already settled).

**Lane 8's (a) = 0 is a positive result, not an absence.** P24's Ironfront predicate
(`Ironfront|NetContext|NetServerActor|IsServer|isServer|#if|UNITY_[A-Z]`) plus `X-[0-9]|ledger`
over all 22 of our A* copies returns zero hits in any file. Nobody has edited that library for the
netcode, which confirms P24's finding one lane wider and means nothing there is at risk from a
restore pass. One rename explains 30 of its 84 lines: our decompiler names a gizmo polyline
accumulator `from` where the recovered tree names it `vector`, which cascades onto the next two
variables.

## 4. The finding — the offline scoreboard was never reset between matches

`ScoreUi.cs:124-127`. **Fixed in this PR.**

**Original.** `ScoreUi.Awake()` zeroed `blueScore`, `redScore`, `blueFlags`, `redFlags` every
match, and got that for free: the HUD prefab is re-instantiated per match.

**Ours.** That zeroing is `MatchScoreboard.Reset()`, which also clears `GameEnded`. It had **zero
callers**. Whole-repo grep for `MatchScoreboard` outside its own file returns `AddScore`
(`Actor.cs:1093`), `AddFlag` (`CapturePoint.cs:538`), one diagnostics read, doc mentions and the
wiring-gate exclusion list. No `Reset`, no `current = null`, no `sceneLoaded` hook. `current` is a
plain static and `MatchScoreboard` is not a `MonoBehaviour`, so nothing destroys it across a scene
load.

**This is not a restore-the-original.** `ScoreUi.Awake` documents why the reset left it — *"The
scoreboard outlives any one HUD: a match that started before this canvas woke has already scored,
and Reset() here would throw those points away. Resetting belongs to whatever starts a match, not
to whatever draws it."* That reasoning is right. It names an owner, `GameManager.StartGame()`, and
that owner was never handed the call. `Reset()`'s own summary reads *"Called when a match starts."*

**Symptom, offline only.** `NetRole.Offline` is the default role; networked matches score in
`MatchStateMachine` and are unaffected. The second offline match in a process opens holding the
previous round's scores and flags. The carried flags multiply every kill through `ScoreMultiplier`.
And because `GameEnded` stays latched, `Win()` early-returns — so the second match, and every
match after it, **can never end**.

**Why nothing caught it.** It is invisible to a playtest that starts one match, which is how anyone
playtests. And it is invisible to `dotnet test`: `Assembly-CSharp` is a predefined assembly no test
project can reference.

**The fix** is `MatchScoreboard.Current.Reset()` as the first statement of `StartGame()`, before
the HUD is instantiated, so the first paint cannot show the previous round. It deletes no netcode.

**The gate** is `G16` in `tools/ClientWiringGate`, beside `G13` — the same present-but-unwired
shape, found the same way, on `ActorManager.Drop`. A source rule is the only thing that can hold
this, which is exactly why the call went missing. Mutation-tested: delete the call and the gate
names the consequence and exits 1. `Program.cs` carries the E-6 discovery guard, so the rule
cannot pass by grading nothing if `GameManager.cs` moves or is renamed.

### Provenance: the taxonomy is missing a bucket

This is not (c) as §4 defines it. It is not something the 2017 decompilation lost — it is a
regression introduced by an Ironfront refactor, with correct reasoning, that stopped one step
short of wiring. Call it **(e)**. Worth naming, because the phase's four buckets would have forced
this into (a) — "Ironfront changed it on purpose" is true of every line involved, and the change
was still a defect.

## 5. Two residuals, decided

**`SpectatorCamera.cs:60` — restored.** Unity's rename from `Application.CaptureScreenshot` to
`ScreenCapture.CaptureScreenshot` dropped the `superSize: 3` argument, which the rename never
required — the `(string, int)` overload still exists in Unity 6 and nothing in the tree documents
the drop. Pressing **L** in spectator mode has been writing a 1x screenshot instead of 3x since the
Unity 6 migration. Minor, one token, no risk.

**`ForceRenderQueue.cs:10` — left alone.** `.material` → `.sharedMaterial` under
`[ExecuteInEditMode]`. `git log --follow` puts it in the original import commit `4eba579`, so it
has genuine (c) provenance — inherited from the 2017 tree, not written during the migration. It
fails the symptom test on evidence: the component is referenced by exactly one asset
(`Assets/Scenes/Splash.unity`, three instances at queues 2999/3000/3001) and those three renderers
use distinct materials, so no instance's queue can clobber another's. Restoring `.material` would
reintroduce Unity's edit-mode material leak for no visual gain.

## 6. Method faults worth recording

**The nearest-line column manufactured false findings, and two readers hit it independently.** The
candidate list paired each survivor with the most similar line anywhere in our file and called the
field `ours`. A renamed local shifts which line scores highest, so the match lands on a sibling
line in another method. Four examples that each read as a genuine bug:

- `MultiTargetPath.cs:515` — presented as an inverted condition *and* a changed loop construct.
  The matcher had reached back ten lines to an enclosing `while` that is byte-identical in both
  trees.
- `NodeLink2.cs:244` — presented as `Mathf.Cos(0f)` becoming `Mathf.Cos(f)` in a circle's seed
  point. Our line has the `0f`; the matcher jumped five lines into the loop body.
- `RichAI.cs:465` — paired with a line 179 lines away in a different method, which is present
  identically in the recovered tree too.
- `Actor.cs:377` — paired with an unrelated `if (!dead && fallenOver)`.

A field named `ours` beside a field named `recovered` promises the two correspond. Fixed in the
same session: the field is now `nearestLineInOurs`, the record carries a `readThisFirst` saying
what it is and is not, and every candidate names the `recoveredMember` it sits in, so the hint
points at a method to go and read. Detection was unchanged — the counts are identical.

**The multiset is count-based and per-file.** When the original holds eight copies of
`if (!aiControlled)` and ours rewrote six, six get flagged arbitrarily and the one reported may
have a byte-identical twin elsewhere. Three lanes hit this. Check for a duplicate before calling a
line absent.

**A survivor can be (d) as a line while the change is (a).**
`InvokeRepeating("Resupply")` → `nameof(Resupply)` is cosmetic, but ours now sits inside
`if (NetContext.IsOffline)`. Grade the change, not the string.

**One search-scope error of mine, worth not repeating.** The classification brief told readers to
grep `Ironfront.Net/`. No such directory exists — the projects are `Ironfront.Net.Protocol`,
`.Replication`, `.Transport`, `.Configuration`, `.MasterLink`. With `2>/dev/null` that produces a
clean false "absent". Caught by one lane and re-run repo-wide; the other lanes' absence claims were
verified against real paths.

## 7. Acceptance

| § | Criterion | Result |
|---|---|---|
| 6.1 | (a)/(b)/(c)/(d) table for every file, with reasons | §3, 506 lines across 70 files |
| 6.2 | (c) list with observable symptoms | §4, one finding, symptom stated |
| 6.3 | Regression test per fix, or why not | G16, mutation-tested. `dotnet test` cannot reach `Assembly-CSharp`, which is why it is a source rule |
| 6.4 | `tools/ci.ps1` green, EditMode no regression | CI PASSED in 1:56; EditMode **198/198**, Assembly-CSharp recompiled in the live Editor domain with zero errors |
| 6.5 | Lane-B verify after any Actor / Vehicle / Weapon change | **Not run, and not applicable** — no file in those three was changed. The two changed files are `GameManager.cs` and `SpectatorCamera.cs` |
| 6.6 | Owner plays it | Open. The fix is offline-only and needs **two** matches in one process to show |
| 6.7 | An empty (c) is a valid result | The decompilation-loss list IS empty, and §1 states the scope that claim covers |

**§6.6 is the one thing worth doing by hand**: start an offline match, end it, start a second one
in the same process, and check the score opens at 0-0 and that the second match can end. That is
the exact sequence that was broken.

## 8. What this closes

The port-back track set out to find what five years and a bad decompilation cost the game. Across
P22–P27 the answer is consistent and worth stating once: **the codebase was not quietly corrupted.**
224 of 322 shared files are byte-identical after whitespace, the divergence concentrates exactly
where a single-player game must change to run multiplayer, and of 506 lines the original has that
we do not, every one is explained by deliberate work, a Unity API removal, or two decompilers
disagreeing about how to print the same IL.

Four phases before this one went looking for a specific defect the plan named, and found the plan
wrong each time. This one went looking for a class of defect and found the class empty. The single
real bug it did surface came from our own refactor — which is the more useful lesson, and the one
G16 now holds.
