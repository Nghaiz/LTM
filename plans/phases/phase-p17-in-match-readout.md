# Phase P17 — the readout a player fights with

- **Plan:** [`../plan.md`](../plan.md) · **Block:** C (3 of 4) · **Size:** M · **Effort:** 1 session
- **Depends on:** **P12 landed** — the local team must genuinely be known before anything can
  display it. **P15 landed** for `ITeamPalette`.
- **Followed by:** **[P18](phase-p18-scoreboard.md)**, which builds the Tab scoreboard on a new
  protocol message. These were one phase until 2026-09-01; see § 1.3.
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
the killer, the respawn timer, and a Deploy button that sends the same empty `SpawnRequest` the
spacebar sends today. The choice becomes a ledger row. **The owner should review this** — it is a
narrowing of "deploy screen on death", taken because the alternative drags a fourth protocol bump
into a UI phase.

### 1.3 Why the scoreboard left this phase

The owner ruled on 2026-09-01 that per-player kills and deaths **ship**, rather than being deferred
behind a names-only scoreboard. Two measurements then made the scoreboard a phase of its own:

- **The server already counts them.** `MatchScoreTally` holds `int[MAX_ACTORS]` for kills and
  deaths with `KillsOf(actorId)` / `DeathsOf(actorId)`, live on `ServerTickLoop.Scores:1207`. So
  the work is not counting — it is a wire and a screen.
- **They cannot be added to 0x4B.** The entry is `u8 actorId + u8 nameLength + ≤16 name` = 18 B,
  and `1 + 64 × 18 = 1153` fits `MAX_CHANNEL_PAYLOAD` **1181** with 28 bytes spare. Two more bytes
  per entry gives `1 + 64 × 20 = 1281` — **100 bytes over**, breaking the un-fragmented guarantee
  the spec's § 4.11 explicitly relies on. Even one byte overflows.

So the scoreboard needs a **new opcode**, a spec section, a hex-sample test and a changelog row —
a protocol phase, bolted onto a UI phase. Per the owner's standing instruction to split rather
than shrink, it is **[P18](phase-p18-scoreboard.md)**. This phase keeps the three items that need
no wire change at all.

---

## 2. File ownership

```
Ironfront_Reborn/Assets/Scripts/Net/Client/Hud/**                        NEW — team readout, deploy
Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientCombatPresenter.cs   killfeed retired to the HUD
Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientLocalCombatDriver.cs OnDied → deploy screen
Ironfront_Reborn/Assets/Scripts/Net/Shared/IMatchHud.cs                  NEW seam
Ironfront_Reborn/Assets/Scripts/Net/Shared/NetClientBindings.cs          register it
Ironfront_Reborn/Assets/Scripts/NetBindings/ClientSceneBindings.cs       implement it
Ironfront_Reborn/Assets/Prefab/Ingame UI Container.prefab                new elements, via the Editor
tools/ClientWiringGate/**                                                 authoring detectors
```

**Not owned:** the Tab scoreboard and anything touching `S_PLAYER_LIST` / `S_PLAYER_SCORES`
(**P18**); `ScoreUi.SetAuthoritativeState` (**P11**); `ScoreUi.Awake`/`UpdateUi` and the minimap
filter (**P12**); the Menu scene (**P15/P16**).

---

## 3. Tasks

### 3.1 — Tell the player which side they are on (S)

The smallest thing on this page and the one a player notices first. A persistent HUD element
reading the local team, coloured through **`ITeamPalette`** (P15 § 3.3) — never a literal.

The value comes from `NetClientPresenterGuard.TryResolveLocalTeam` (`:128-143`), which P12 already
routes to the local body. **Read it from the same place P12 does**; a second resolution path is a
second thing that can be wrong, and this element exists precisely to make a wrong answer visible.

Before the first snapshot the team is unknown. Render **blank**, not "Blue" — the same discipline
`ScoreUi.cs:225` already applies to a zero human count (*"before the first broadcast there is no
answer, and stating one would be a fabricated zero"*).

### 3.2 — A deploy screen on death (M)

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

### 3.3 — Retire the IMGUI killfeed onto the HUD (M)

Its own remark asks for it (`NetClientCombatPresenter.cs:127-136`), and leaving both means two
renderings of the same data drifting apart.

Colour the killfeed's names by team through `ITeamPalette` while it moves — that is the one thing
the IMGUI version could never do, and it is the reason to move it now rather than when the
scoreboard lands.

**`_drawKillfeed` (`:48`) stays as the on/off field** so a lane-B run can turn it off; only its
renderer changes. **[P19](phase-p19-island.md) § 3.3 authors whatever this phase leaves** on
Island's `NetClientCombatPresenter`, so record the field's final shape in the report.

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
| 3 | **Screenshot: the deploy screen appears on death, names the killer, counts down, and Deploy respawns** | screenshot sequence |
| 4 | The spacebar still respawns | stated, exercised in the same run |
| 5 | The deploy screen closes on a respawn the player did not request | scripted run |
| 6 | **Screenshot: the killfeed renders on the HUD with team-coloured names**, and the IMGUI version is gone | screenshot + diff |
| 7 | Every new element is gated by a detector observed RED | mutation results |
| 8 | `tools/ci.ps1` green | CI |

Six of eight are things on a screen. That is the point of the phase and the reason none of this
was caught: `ClientWiringGate` reports `KnownUnwiredEvents` **empty** — 0x4B has a subscriber, and
has had one throughout.

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| The team readout resolves the local team by its own path and disagrees with P12's; the element that exists to expose a wrong answer becomes one | 3 | 5 | **15** | § 3.1 mandates the same resolver; criterion 1 is graded on a team-1 client |
| Deploy screen driven by the button rather than by the alive signal; it survives a force-respawn and blocks the player | 3 | 4 | 12 | § 3.2's last bullet; criterion 5 |
| `LoadoutUi` reached from `Net/Client` — a compile error only Unity/CI reports | 3 | 3 | 9 | § 3.2 names it; contracts § 6 explains why `dotnet build` stays green |
| The killfeed moves and loses the `_drawKillfeed` off-switch a lane-B run needs | 2 | 3 | 6 | § 3.3 keeps the field; P19 § 3.3 authors whatever it becomes |

---

## 6. Out of scope

- **The Tab scoreboard and per-player kills/deaths** — **[P18](phase-p18-scoreboard.md)**, which
  owns the new protocol message. § 1.3 is why.
- **Choosing a spawn point.** Needs a body on `SpawnRequest` — a UDP wire change. Ledger row (§ 1.2).
- **World-space nametags.** None exist today and none is in the agreed scope.
- **The score bars and ticket labels** — **P11** and **P12** own `ScoreUi` between them.
- **The end-of-match victory screen.** It already exists; P18 owns the Tab key that currently
  collides with it.
