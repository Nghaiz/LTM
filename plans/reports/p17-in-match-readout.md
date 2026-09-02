# P17 — the readout a player fights with

- **Plan:** [`../phases/phase-p17-in-match-readout.md`](../phases/phase-p17-in-match-readout.md)
- **Branch:** `p17-in-match-readout` · **Base:** `develop`
- **Date:** 2026-09-02

---

## 1. What shipped

Three elements on `Ingame UI Container.prefab`, one component behind them, and one seam.

| § | Element | Where |
|---|---|---|
| 3.1 | The local team, named and coloured through `ITeamPalette`, blank until the snapshot answers | `MatchHud._teamReadoutText` |
| 3.2 | A deploy screen on death: who killed you, the countdown, and a Deploy button | `MatchHud._deployRoot` and its three children |
| 3.3 | The killfeed, moved off IMGUI onto the HUD with team-coloured names | `MatchHud._killfeedRows` |
| 3.4 | Two authoring checks, sixteen mutations, every one observed RED | `tools/ClientWiringGate/MatchHudWiringDetectors.cs` |

**New files**

```
Ironfront_Reborn/Assets/Scripts/Net/Shared/IMatchHud.cs          the seam
Ironfront_Reborn/Assets/Scripts/Net/Client/Hud/MatchHud.cs       the one implementation
Ironfront_Reborn/Assets/Editor/NetVerification/BuildMatchHud.cs  the authoring command
tools/ClientWiringGate/MatchHudWiringDetectors.cs                the checks
```

**Retired**

`NetClientCombatPresenter.OnGUI` (the killfeed stopgap) and `NetClientLocalCombatDriver.OnGUI`
(the death screen). Both remarks asked to be deleted when a HUD element read the models; both
are gone rather than left beside the replacement, because two renderings of one model drift.

---

## 2. Three decisions the plan left open

### 2.1 `IMatchHud` is implemented in `Net/Client`, not in `ClientSceneBindings`

§ 2's ownership table reads `NetBindings/ClientSceneBindings.cs — implement it`, and that cannot
be done as written: the assembly seal is two-way (contracts § 6.1), so `Assembly-CSharp` may not
name `MatchHud`, which the same table places in `Net/Client/Hud/**`. One of the two rows had to
give, and the placement rule in contracts § 6.3 settles which — network-flow UI lives in
`Net/Client/`.

So the seam is declared in `Net/Shared`, registered on `NetClientBindings`, and implemented by
the `Net/Client` component itself, which registers on `OnEnable`. **`ClientSceneBindings.cs` is
untouched.**

The seam still earns its keep, and not for the reason the other eleven exist. The presenters are
authored into the map scene; the HUD is instantiated at runtime by `GameManager.StartGame`. Neither
can hold a serialized reference to the other, so without a registry the presenters would reach for
the HUD with a per-frame scene search — which is the same argument `NetClientBindings.Hud` already
makes for the hitmarker.

### 2.2 The deploy screen is driven by the alive flag, not by `OnDied`

§ 3.2 says "`OnDied()` opens a screen". It does not, and the difference is load-bearing.
`ClientCombatState.OnDied` is raised from **inside** `ApplyDeath`, before the caller has seen the
message — so a screen opened there cannot name the killer. Worse, the snapshot's `IsAlive` bit and
`S_DEATH` are produced on the same tick and **either can arrive first** (that method's own remark),
so an `OnDied`-driven screen names the killer on one ordering and not the other.

`SyncMatchHud` therefore runs once a frame off `_state.IsAlive` — the same flag the input
suppression uses, which is what § 3.2's last bullet asks for and what criterion 5 is graded on. A
death whose `S_DEATH` lands after the snapshot raises the screen blank and rewrites the killer line
on the frame the message arrives.

### 2.3 The spawn CHOICE stays out, per § 1.2

Deploy sends the same empty `C_SPAWN_REQUEST` the spacebar sends. The plan flagged this narrowing
for owner review; it is taken as written, and the ledger row § 1.2 asks for is below.

---

## 3. What the plan says that is no longer true

**§ 1.1 "Death is silent … No screen is shown."** There was one:
`NetClientLocalCombatDriver.OnGUI` drew `"You are dead. Respawn in Ns"` behind a `_drawDeathScreen`
field defaulting to true, and the class remark called it a stopgap in the same words the killfeed's
used. The plan's line numbers for that file (`:343-351` for `OnDied`) are also a revision behind —
`OnDied` sits at `:404` on `develop`.

This changes nothing about the work: an IMGUI stopgap and no screen produce the same phase. It is
recorded because the plan reads as though the deploy screen is the first screen, and the next
reader would go looking for a gap that had already been half closed.

---

## 4. The authoring gate

Two checks, registered with the runner:

- `MatchHudRefsAreAssigned` — every reference on `MatchHud`, graded on four clauses.
- `MatchHudTeamColoursComeFromThePalette` — the source still calls the palette, and the prefab
  authors one ink under the elements the palette drives.

**The grading is `MenuScreenWiringDetectors`', called rather than copied.** Its three clauses were
earned by mutation against `ScoreUi`, and a second copy under a different constant is two checks
free to disagree. What differs per screen — the ledger row, the plan clause, the Editor command
that repairs it — now travels on the `Screen` itself.

### 4.1 A fourth clause, and the hole that earned it

The three inherited clauses are *assigned*, *resolves*, *not already driven*. A `Button` field
pointed at a `Text` passes all three: it resolves to a real anchor, no other field names it, and
**Unity loads a type mismatch as null** — so the Deploy control does nothing, which is the
unassigned case wearing a resolving fileID. Aimed at the deploy screen's own heading label, the
gate reported **clean**.

`GradeDeclaredTypes` closes it. Field types are read from the screen's own source; a field declared
as `GameObject` must resolve to a class-1 document, and two fields declared as different C# types
may not resolve to the same script. The guid is never hardcoded — `ScoreUiTextRefsAreAssigned`
already records why (uGUI's `Text` carries one guid in the legacy DLL form and another in the
package form, and this tree is mid-migration), so what is compared is agreement rather than a
constant.

The clause applies to P15's and P16's menu screens too; the gate stays green on them.

**Residual gap, recorded rather than left silent:** a field pointed at a component type *no other
field on the screen declares* — a `Button` field aimed at the `Image` beside it — has nothing to
disagree with and passes. Closing it means pinning uGUI's guids, which is the thing the paragraph
above says goes wrong.

### 4.2 Mutation results — sixteen, all RED

Every mutation edits the **real** prefab or the **real** source, runs the gate, and restores from
memory. (Restoring with `git checkout` is wrong here and cost one run: the authoring under test is
uncommitted, so a checkout reverts to a prefab with no `MatchHud` on it and every later mutation
becomes a no-op against the wrong file.)

| # | Mutation | Clause that fired |
|---|---|---|
| 1–5 | each of `_teamReadoutText`, `_deployRoot`, `_deployKillerText`, `_deployTimerText`, `_deployButton` set to `fileID: 0` | assigned |
| 6 | `_killfeedRows` shortened to 4 | array length |
| 7 | two killfeed rows are one object | array distinctness |
| 8 | killer and timer are one object | field distinctness |
| 9 | `_teamReadoutText` names a fileID no object carries | resolves |
| 10 | `_deployButton` names a Text another field drives | field distinctness |
| 11 | `_deployButton` names an **undriven** Text | **declared type** |
| 12 | `_deployRoot` names a component another field drives | field distinctness |
| 13 | `_deployRoot` names an **undriven** component | **declared type, class-1** |
| 14 | one killfeed row painted red in the prefab | authored ink |
| 15 | `MatchHud` stops calling `NetClientBindings.TeamColourRgb(` | palette source |
| 16 | the `MatchHud` document deleted | on no GameObject |

Rows 11 and 13 exist because 10 and 12 were caught by the *wrong* clause — distinctness, not type —
which left the new clause unproven. Each fault the gate claims to catch has been observed catching
it exactly once.

---

## 5. Runtime evidence

Five lane-B runs, three programme sets plus one written for this phase. Artifacts under
`artifacts/lane-b/p17-01` (combat), `-02` (duel), `-03` (pointblank), `-04`/`-05` (a temporary
fire/reload set, not committed).

### 5.1 What is on screen

**Criterion 1 — met, in one run.** `p17-01`, the default 0/1/0 roster:

| Client | Snapshot team | Readout |
|---|---|---|
| DRIVER (`driver-02-approach.png`) | 0 | **TEAM 1**, blue, top-left |
| OBS-A (`observer-a-02-approach.png`) | 1 | **TEAM 2**, red, top-left |

Same run, same build, two colours from `ITeamPalette` — nothing is authored red or blue in the
prefab, and the gate's palette check is what holds that.

**Criterion 6 — half met.** The killfeed renders on the HUD, top-right, newest first, two lines
stacked correctly (`driver-07-respawn-window.png`, `p17-02`), and the IMGUI drawer is gone from the
diff. **The team colouring is NOT exercised**, and the reason is worth stating precisely rather
than glossing: every kill observed across all five runs was `The world → actor N` — an environment
death of a bot — and in each the victim was outside that client's interest radius, so
`TryResolveActorTeam` correctly answered false and both names rendered in the neutral grey. That
is the right behaviour for the data, and it is not evidence that a coloured name renders.

What DOES exercise the same call is the team readout in the very same screenshots: `TeamColourRgb`
is one method, and the readout's blue and red are its output.

**Offline is inert — checked, not assumed.** Play Mode on Dustbowl in the Editor runs the offline
path, and the capture shows the legacy loadout screen with **no team readout and no overlay**:
`MatchHud.Awake` fails `NetClientPresenterGuard.IsPresentable` and disables before `OnEnable` can
register. That is the branch that keeps the bot match unchanged.

### 5.2 What is NOT graded, and why

**Criteria 2, 3, 4 and 5 were not reached. No client ever died, in any run.**

| Set | Contact | Outcome |
|---|---|---|
| `combat` (p17-01) | 555 m at `firing` | never closed; 95 s of sprint approach covers ~115 m |
| `duel` (p17-02) | 1010 m → 895 m | 150 s of approach closed 115 m |
| `pointblank` (p17-03) | **3.7 m** | 30 shots, 4 hitmarkers, victim 100 → **65 hp**, then the clip ran dry |
| `p17kill` (p17-04) | 3.7 m at hold 3.0 m | 20 shots, **0** hitmarkers |
| `p17kill` (p17-05) | hold 6.0 m, 6 fire/reload cycles | **84 shots, 12 hitmarkers**, victim **100 hp throughout** |

The harness limits are real and they are not this phase's: the scripted driver **empties one clip
and never reloads** unless the programme says so (`ScriptedInputStep.reload` exists and p17-05 uses
it — 84 shots against 30), and even with ammunition its hits land on bots rather than on the
co-spawned victim. `distanceM` is also frozen across checkpoints in these records, so it cannot be
read as a live range; both clients' `localActor` positions are the only honest source.

So the deploy screen, the countdown, the Deploy button, the spacebar and the closes-on-respawn rule
are **unproven on screen**. They are covered by the type system, the gate and the argument in § 2.2
— which is not the same thing, and is not claimed to be.

**Criterion 2** (blank before the first snapshot) is also ungraded: lane-B's earliest checkpoint is
5–7 s in, by which time the snapshot has long arrived. The behaviour is pinned at both ends —
`BuildMatchHud` authors the label with an empty string, and `SetLocalTeam` writes `string.Empty` on
`TeamId.None` — but no capture shows the window.

**What would close them:** a lane-B programme whose driver actually kills the victim. That is a
harness change (aim/LOS at point-blank, and a reload cadence in the shipped sets), and it belongs
with whoever owns lane-B rather than being smuggled into a UI phase. Ledger row **P17-4**.

---

## 6. Ledger rows this opens

| Row | What |
|---|---|
| **P17-1** | **Choosing a spawn point.** `C_SPAWN_REQUEST` carries no body, so Deploy and the spacebar send the same empty message and the minimap's team-gated spawn buttons still cannot influence it. Needs a UDP wire change and a `PROTOCOL_VERSION` bump — § 1.2. |
| **P17-2** | **The declared-type clause is one component deep.** § 4.1's residual gap. |
| **P17-3** | **`_drawKillfeed` and `_drawDeathScreen` are the on/off fields, and only `_drawKillfeed` is authored** (Dustbowl, `1`). P19 § 3.3 authors whatever Island's presenter needs; both fields survive this phase unrenamed, and `_drawKillfeed` now gates the PUSH rather than an `OnGUI`. |
| **P17-4** | **Lane-B cannot kill a player, so four of this phase's criteria are ungradeable.** § 5.2 has the measurements. Two separate faults: the shipped sets never `reload`, and the driver's fire lands on bots rather than on the actor it is aimed at from 3.7 m. Blocks P17 criteria 2, 3, 4, 5 — and P18's scoreboard needs kills on the board too. |
| **P17-5** | **`localBodyTeam: -1` against `snapshotTeam: 0`** on every lane-B record, including three P13 runs before this phase. Pre-existing, P12's territory, and now visible on screen. |

---

## 7. Not done, and why

**Criteria 2, 3, 4 and 5 are the gap, and they are the milestone half of the table.** Six of
this phase's eight criteria are things on a screen. Criterion 1 is met, criterion 6 is half met,
and the remaining four are not reached because no scripted client can currently die.

What was NOT attempted, deliberately:

- **Faking a death.** Injecting a synthetic `DeathMessage` through the router would have produced
  the screenshots this report is missing, and would have graded the code against itself. The
  screen would have been real and the death would not.
- **A second protocol bump for the spawn choice** — § 1.2, and P17-1.
- **Fixing lane-B's aim** so the driver hits the player it is aimed at. It is the blocker for four
  criteria and it is squarely a harness defect (P17-4), not a readout one.

**One pre-existing finding this phase's own element exposed.** Every lane-B record on `develop`
carries `localBodyTeam: -1` beside `snapshotTeam: 0` — the server says team 0 and the local body
believes it is on no team. Checked across five runs from this phase and the three P13 runs of
2026-09-02 00:33–00:40 that precede it, so it is **not a P17 regression**. It is exactly the
disagreement P12 § D-1 was filed against, and the readout now renders the authoritative half of it
on screen. Ledger row **P17-5**.
