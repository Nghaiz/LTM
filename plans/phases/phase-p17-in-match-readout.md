# Phase P17 — the readout a player fights with

- **Plan:** [`../plan.md`](../plan.md) · **Block:** C (3 of 3) · **Size:** L · **Effort:** 1 session
- **Depends on:** **P12 landed** — the local team must genuinely be known before anything can
  display it, and before a scoreboard can sort friend from enemy. **P15 landed** for `ITeamPalette`.
- **Contracts:** [`../00-shared/team-multiplayer-contracts.md`](../00-shared/team-multiplayer-contracts.md)
  **§ 6** (assembly seal — required reading) and **§ 4** (team values, `ColorScheme.TeamColor`).
- **Filed:** 2026-09-01, from the player-facing audit's **F7** and **F8**.

---

## 1. Identity is on the wire and stops at a debug killfeed

**`PlayerListMessage` (0x4B) has exactly one consumer.** `NetClientCombatPresenter.cs:104`
`_client.Router.OnPlayerList += _names.Apply` into a `PlayerNameTable` (`:52`). Its only rendering
is an IMGUI killfeed: `:143-165` draws `killer → victim` with `GUI.Label`, default-on (`:48`). Its
own remark calls it a stopgap (`:127-136`): *"A real HUD element belongs on `Ingame UI
Container.prefab`."* No team colouring on those lines.

**There is no Tab scoreboard.** `ScoreUi.cs:346` binds Tab to `HideVictoryScreen()`; `:350` binds
Home to toggling the canvas. `MatchScoreboard` is four counters plus events (`AddScore:100`,
`AddFlag:122`) and holds **no per-player rows**.

**There are no nametags.** Scope searched: `Ironfront_Reborn/Assets/Scripts` for
`nametag|name ?tag|Your team|You are on|localteam` — the only hits are `MinimapUi.cs:194-207` local
variables and the `TryResolveLocalTeam` plumbing at `NetClientPresenterGuard.cs:128-143`.

**The only player-identity figure on screen is a count**: `ScoreUi.cs:227-229`
`humanCountText.text = "N players"` from `MatchStateMessage.HumanPlayerCount`.

### 1.1 Death is silent

`NetClientLocalCombatDriver.OnDied()` (`:343-351`) calls `local.DisableInput()` and sets
`_inputSuppressedByDeath`. **No screen is shown.** The offline path does show one —
`FpsActorController.cs:440-450` `if (actor.dead) OpenLoadout()` → `LoadoutUi.Show()`.

Respawn is a keypress: `_respawnKey = KeyCode.Space` (`:44`), gated at `:225-228`. The request
carries **no spawn point**: `:296` writes `ClientMessageType.SpawnRequest` with
`ReadOnlySpan<byte>.Empty`.

The server chooses, and it *is* team-filtered — `ServerTickLoop.cs:1342` →
`ServerCombatBridge.cs:291` → `:394` → `:725 ChooseSpawnIndex(spawnPoints, actor.Team)`. The
minimap's spawn buttons exist and are correctly team-gated (`MinimapUi.cs:199-207,237`), but
**nothing in the networked death path opens that minimap**, and pressing a button there could not
influence `SpawnRequest` anyway — the message has no body.

### 1.2 The scope boundary this phase does not cross

Giving the player a spawn *choice* means putting a spawn-point index in `SpawnRequest`, which is a
**UDP wire change** and another `PROTOCOL_VERSION` bump. That is a feature, not a fix.

**Decision: this phase builds the deploy SCREEN and not the spawn CHOICE.** The screen shows death,
the respawn timer, and a Deploy button that sends the same empty `SpawnRequest` the spacebar sends
today. The choice becomes a ledger row. **The owner should review this** — it is a narrowing of
"deploy screen on death", taken because the alternative drags a fourth protocol bump into a UI
phase.

---

## 2. File ownership

```
Ironfront_Reborn/Assets/Scripts/Net/Client/Hud/**                    NEW — team readout, scoreboard, deploy
Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientCombatPresenter.cs   killfeed retired to the HUD
Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientLocalCombatDriver.cs OnDied → deploy screen
Ironfront_Reborn/Assets/Scripts/Net/Shared/IMatchHud.cs               NEW seam
Ironfront_Reborn/Assets/Scripts/Net/Shared/NetClientBindings.cs       register it
Ironfront_Reborn/Assets/Scripts/NetBindings/ClientSceneBindings.cs    implement it
Ironfront_Reborn/Assets/Prefab/Ingame UI Container.prefab             the new elements, via the Editor
Ironfront.Net.Replication/Match/MatchScoreTally.cs                    per-player rows, if extended
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerTickLoop.cs          PlayerList content only
Ironfront.Net.Replication.Tests/**
tools/ClientWiringGate/**                                              authoring detectors
```

**Not owned:** `ScoreUi.SetAuthoritativeState` (**P11**), `ScoreUi.Awake`/`UpdateUi` (**P12**),
the minimap filter (**P12**), anything in the Menu scene (**P15/P16**).

---

## 3. Tasks

### 3.1 — Tell the player which side they are on (S)

The smallest thing on this page and the one a player notices first. A persistent HUD element
reading the local team, coloured through **`ITeamPalette`** (P15 § 3.3) — never a literal.

The value comes from `NetClientPresenterGuard.TryResolveLocalTeam` (`:128-143`), which P12 already
routes to the local body. **Read it from the same place P12 does**; a second resolution path is a
second thing that can be wrong, and this element exists precisely to make a wrong answer visible.

Before the first snapshot the team is unknown. Render **blank**, not "Blue" — the same discipline
`ScoreUi.cs:225` already applies to a zero human count ("*before the first broadcast there is no
answer, and stating one would be a fabricated zero*").

### 3.2 — A Tab scoreboard from `PlayerListMessage` (L)

Hold Tab, see two team columns of players with name, kills, deaths and score.

**Tab is currently bound to `HideVictoryScreen()`** (`ScoreUi.cs:346`). Decide and record what
happens to that binding — the victory screen is shown at match end and Tab is the conventional
scoreboard key. Default: the scoreboard takes Tab, and the victory screen gets its own dismissal
(it already has `Home` at `:350` toggling the canvas). Do not leave two behaviours on one key.

**What 0x4B carries versus what the scoreboard needs.** `PlayerListMessage` feeds a
`PlayerNameTable` — names and ids. Kills, deaths and score are tallied server-side by
`MatchScoreTally` (P6 built it for `GS_MATCH_ENDED`; `ServerMasterReporter.CollectScores` walks
it). Establish **before writing UI** whether those per-player numbers reach the client at all, and
say which of these is true:

- they already ride on 0x4B → render them;
- they do not → either extend 0x4B (a **UDP wire change**, another version bump, and it should
  then ride with P11's if P11 has not shipped) or render **names and teams only** and record the
  numbers as a ledger row.

**Default: names and teams only, numbers deferred, ledger row filed.** A roster that shows who is
on which side is most of the value and needs no protocol change; a scoreboard that shows fabricated
zeroes is worse than one that shows none — that is `CollectScores`'s own recorded reasoning
(`ServerMasterReporter.cs:125-134`: *"rows of all-zero scores are indistinguishable from a match
where nobody scored"*). **The owner should review this narrowing.**

**Retire the IMGUI killfeed onto the HUD** in the same task — its own remark asks for it, and
leaving both means two renderings of the same data drifting apart. Colour the killfeed's names by
team through `ITeamPalette` while it moves; that is the one thing the IMGUI version could never do.

### 3.3 — A deploy screen on death (M)

`NetClientLocalCombatDriver.OnDied()` (`:343-351`) opens a screen instead of only suppressing
input. It shows: that you died, who killed you (0x4B already names them — that is what the
killfeed uses), the respawn timer, and a **Deploy** button.

- **Deploy sends the same empty `SpawnRequest`** the spacebar sends (`:296`). Per § 1.2 the spawn
  *choice* is out of scope.
- **Keep the spacebar.** It works, it is muscle memory, and removing it in the phase that adds a
  button makes a broken button indistinguishable from a broken respawn.
- **Do not reuse `LoadoutUi`.** It is the offline path's screen (`FpsActorController.cs:440-450`)
  and it lives in Assembly-CSharp, which `Net/Client` cannot name
  ([contracts § 6](../00-shared/team-multiplayer-contracts.md#6-where-the-new-ui-code-lives--the-assembly-seal)).
  If the offline look is wanted, cross at the `IMatchHud` seam.
- **The screen must close on respawn**, including a respawn the player did not ask for (a server
  force-respawn, a match reset). Drive it from the same alive/dead signal the input suppression
  uses, not from the button click.

### 3.4 — Author the elements, and gate them (M)

New elements on `Ingame UI Container.prefab`, **through the Editor**, never by editing YAML
(P3 § 3.3).

A detector per element, following `ScoreUiTextRefsAreAssigned` — assigned, resolves to an object
that exists, and not an object another field already drives. That detector has its three-part shape
**because three mutations proved a weaker draft green**, and the nine existing authoring checks
passed this exact prefab with `capturePointMarkerPrefab` null (P3 § 3.3).

Mutation-test every one. Project memory: *a detector is unverified until the real artifact is
mutated and it goes red.*

---

## 4. Acceptance

| # | Criterion | Evidence |
|---|---|---|
| 1 | **Screenshot from a team-1 client: the HUD names its team as red**, and a team-0 client names blue, in the same run | two screenshots |
| 2 | Before the first snapshot the team element is blank, not "Blue" | screenshot at join |
| 3 | **Screenshot: holding Tab shows both teams' rosters with real names from a two-client run**, each name on the correct side | screenshot from both machines |
| 4 | Tab no longer collides with the victory screen, and the victory screen is still dismissable | screenshot of each |
| 5 | **Screenshot: the deploy screen appears on death, names the killer, counts down, and Deploy respawns** | screenshot sequence |
| 6 | The spacebar still respawns | stated, exercised in the same run |
| 7 | The deploy screen closes on a respawn the player did not request | scripted run |
| 8 | The killfeed renders on the HUD with team-coloured names, and the IMGUI version is gone | screenshot + diff |
| 9 | Every new element is gated by a detector observed RED | mutation results |
| 10 | Whatever § 3.2 established about per-player numbers is **stated**, and if deferred, a ledger row exists | report + ledger diff |
| 11 | `tools/ci.ps1` green | CI |

Ten of eleven are things on a screen. That is the point of the phase and the reason none of this
was caught: `ClientWiringGate` reports `KnownUnwiredEvents` **empty** — 0x4B has a subscriber, and
has had one throughout.

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| The team readout resolves the local team by its own path and disagrees with P12's; the element that exists to expose a wrong answer becomes one | 3 | 5 | **15** | § 3.1 mandates the same resolver; criterion 1 is graded on a team-1 client |
| The scoreboard renders fabricated zeroes for kills/deaths | 4 | 3 | 12 | § 3.2's default is names-only, with `CollectScores`' own reasoning cited |
| 0x4B extended for scores, dragging an unplanned protocol bump into a UI phase | 3 | 3 | 9 | § 3.2 forces the question **before** UI is written, and defaults to deferring |
| Tab bound to two behaviours; the victory screen becomes undismissable | 3 | 3 | 9 | § 3.2 makes it a recorded decision; criterion 4 |
| Deploy screen driven by the button rather than by the alive signal; it survives a force-respawn and blocks the player | 3 | 4 | 12 | § 3.3's last bullet; criterion 7 |
| `LoadoutUi` reached from `Net/Client` — a compile error only Unity/CI reports | 3 | 3 | 9 | § 3.3 names it; contracts § 6 explains why `dotnet build` stays green |

---

## 6. Out of scope

- **Choosing a spawn point.** Needs a body on `SpawnRequest` — a UDP wire change. Ledger row (§ 1.2).
- **World-space nametags.** None exist today and none is in the agreed scope; the roster and the
  team-coloured killfeed cover identity.
- **Per-player kills/deaths on the scoreboard**, unless § 3.2 finds they already ride on 0x4B.
- **The score bars and ticket labels** — **P11** and **P12** own `ScoreUi` between them.
- **The end-of-match scoreboard.** `ScoreUi`'s victory screen already exists; this phase only stops
  Tab from colliding with it.
