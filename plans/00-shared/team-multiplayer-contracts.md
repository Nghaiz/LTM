# Team multiplayer — the three shapes two phases have to agree on

- **Created:** 2026-09-01, alongside phases **P11–P18**.
- **Why this file exists:** P11, P13, P14 and P17 each write one side of a shape the others read.
  A copy pasted into two phase files goes stale the moment one of them lands, and the second
  phase then codes confidently against a snapshot. This file is the single copy. Phases link to
  it; they do not restate it.
- **Read with:** [`protocol-spec.md`](protocol-spec.md) — that file is a **build input**
  (`tools/SpecChecker/Program.cs:32` opens it at runtime), this one is not. Anything here that
  changes bytes must also land there, in the § 15 changelog, before the phase is done.

> **Scope of every "does not exist" below.** Searched `Ironfront.Net.Protocol/**`,
> `Ironfront.Net.Replication/**`, `Ironfront.MasterServer/**`, `Ironfront.Net.MasterLink/**`,
> `Ironfront_Reborn/Assets/Scripts/**`. Excluded `Library/`, `obj/`, `bin/`, `artifacts/`.

---

## 1. The score model — one rule, two runtimes

### 1.1 The rule the game actually has

`Assets/Scripts/Assembly-CSharp/MatchScoreboard.cs` is the offline match's scoreboard and the
only place the project's own win condition is written down:

```csharp
// MatchScoreboard.cs:100-119
public void AddScore(int blue, int red)
{
    BlueScore += blue * ScoreMultiplier(BlueFlags);
    RedScore  += red  * ScoreMultiplier(RedFlags);
    ...
    if (BlueScore >= RedScore + VictoryPoints)      Win(true);
    else if (RedScore >= BlueScore + VictoryPoints) Win(false);
}

// MatchScoreboard.cs:160-163
public static int ScoreMultiplier(int flags) => flags;

// MatchScoreboard.cs:74-75
public int VictoryPoints =>
    GameManager.instance != null ? GameManager.instance.victoryPoints : DefaultVictoryPoints;
```

Four properties of that rule, each load-bearing and each easy to lose in a migration:

1. **Score ASCENDS.** A team gains points; it never spends them.
2. **A team scores when an actor of the OTHER team dies.** `Actor.cs:905`
   `AddScore((team == 1) ? 1 : 0, (team == 0) ? 1 : 0)` — the argument is chosen from the
   **victim's** team, so a kill credits the victim's opponent. Under friendly fire (which is
   intended — see § 1.4) a team-kill therefore hands a point to the enemy. That is the correct
   penalty and it is already implemented; nothing needs adding for it.
3. **Each point is multiplied by the scoring team's flag count**, and `ScoreMultiplier` is the
   identity function. **A team holding zero capture points scores zero per kill.** This is the
   single most dangerous detail in the migration: a networked match whose flag counts open at
   0/0 accrues no score at all and can never reach the victory margin. Dustbowl opens 1/1
   (Oasis to team 0, Fortress to team 1) and four points neutral, so the offline game never
   exhibits it. Any phase touching this must assert the opening flag counts on the server.
4. **Victory is a MARGIN, not a total.** `own >= other + VictoryPoints`. `VictoryPoints`
   defaults to 200 (`GameManager.cs:31`) and is editable at `MainMenu.cs:87`.
   `MatchScoreboard.DefaultVictoryPoints` is **100** (`:78`) and applies only when no
   `GameManager` exists — a test or a bare scene. The two numbers differ on purpose; do not
   "fix" one to match the other.

There is a **second win condition**, and it is not the margin: losing every spawn point.
`MatchScoreboard.cs:139-146` — `!ActorManager.HasSpawnPoint(0)` ⇒ red wins, and vice versa,
behind an elapsed-time gate (`:135`, `ElapsedGameTime() <= 1f`) whose own comment says it exists
so the opening moments do not read as one team having already lost every point. The networked
side already has its own version of this at `MatchStateMachine.cs:409-447`; it is retained.

### 1.2 What the netcode does instead

| | Offline (`MatchScoreboard`) | Networked (`MatchStateMachine`) |
|---|---|---|
| Direction | ascends from 0 | **descends** from 200 (`MatchRules.cs:33`) |
| Per death | +1 to the victim's **opponent** | **−1** to the victim's **own** side (`MatchRules.cs:36`, `ServerTickLoop.cs:1194` `ReportDeath(victim.Team)`) |
| Flags | multiply the scoring team's points | drive a 0.5/s **bleed** on the side with fewer (`MatchRules.cs:47`, `MatchStateMachine.cs:382-394`) |
| Ends when | a team leads by `VictoryPoints` | either side reaches 0 (`MatchStateMachine.cs:286`) |
| Winner | latched in `Win(bool)` | **computed**, `Tickets0 > Tickets1` (`MatchMessages.cs:69-76`) |

These are two different games. The margin rule and the ticket rule disagree about who is winning
in ordinary play, and `WinningTeam`'s `Tickets0 > Tickets1` is **meaningless under a margin
rule** — under the ticket rule the higher number is ahead; under the margin rule the higher
number is also ahead, but the match does not end where either side thinks it does.

### 1.3 The SSOT decision, and why it is not simply "call `MatchScoreboard`"

The owner's instruction is that `MatchScoreboard` is the SSOT for the rule and no second copy is
written. Two facts make the literal reading impossible:

1. **Assembly direction.** `MatchScoreboard` lives in Assembly-CSharp. Predefined assemblies
   compile last and reference every asmdef, and **no asmdef references back**
   (`tools/check-net-layering.ps1:11-12`). `Ironfront.Net.Replication` therefore cannot see
   `MatchScoreboard`, and never will.
2. **An existing CI gate forbids the reverse call.**
   `tools/ClientWiringGate/ClientWiringDetectors.cs:320-329` fails the build on any reference to
   `AddScore` / `AddFlag` from `Net/Client/`, because those mutators are delta-only and feeding
   server **totals** through them would re-run `ScoreMultiplier` and double-drive the win check
   (recorded as V10 D11, and restated in `MatchScoreboard`'s own remarks at `:19-26` and
   `ScoreUi.cs:142-148`).

**So the rule moves down, not sideways.** Extract the *pure* rule — no state, no events, no
`MonoBehaviour` — into `Ironfront.Net.Replication/Match/` as a static, and have **both**
runtimes call it:

```
Ironfront.Net.Replication/Match/ConquestScoreRule.cs   ← the ONE copy of the rule
  static int  ScoreMultiplier(int flags)                  => flags
  static int  Award(int points, int flags)                => points * ScoreMultiplier(flags)
  static byte Decide(int score0, int score1, int victoryPoints)
         => score0 >= score1 + victoryPoints ? TeamId.Team0
          : score1 >= score0 + victoryPoints ? TeamId.Team1
          : TeamId.None

MatchScoreboard (Assembly-CSharp)  → delegates; keeps its own state, events and Win() latch
MatchStateMachine (Replication)    → delegates; keeps its own state and phase machine
```

Assembly-CSharp already imports `Ironfront.Net.Replication.Match`
(`CapturePoint.cs`, `MatchScoreboard.cs`), so the reference exists and no asmdef changes.
`MatchScoreboard.ScoreMultiplier` stays as a public static **forwarder** — deleting it would
break `ScoreUi` and any test that names it, and the forwarder is what makes "one copy" checkable
by grep.

**The gate at `ClientWiringDetectors.cs:329` stays exactly as it is.** It forbids `Net/Client/`
calling the delta mutators; it does not forbid a shared pure function, and nothing in this design
routes server totals through `AddScore`.

### 1.4 Friendly fire — DELIBERATELY NOT GATED

The 2026-09-01 brainstorm's item **D1** proposed a friendly-fire gate in
`ServerActorDamageSink.ApplyDamage`. **The owner cancelled it on 2026-09-01: friendly fire is
intended.** `ServerActorDamageSink.ApplyDamage` accepting `attackerId` and never reading it is
**correct behaviour**, not the defect the server audit ranked #1.

This paragraph exists so nobody re-files it. If you are reading the server audit
(`plans/reports/review-server-team-audit.md` § 3, ranked finding #1) or the brainstorm
(`plans/reports/2026-09-01-multiplayer-readiness-brainstorm.md` § 2.4 D-a and § 5 block D), those
sections are **superseded here**. The penalty for a team-kill is economic, not mechanical: under
§ 1.1 property 2 the kill credits the enemy a point, which is a stiffer and more legible penalty
than a blocked shot.

`AiActorController.cs:1060` still makes bots refuse to fire on their own side. That is bot
target-selection, not a damage gate, and it is left alone.

---

## 2. `S_MATCH_STATE` (0x45) — the message whose meaning flips

### 2.1 Today

`Ironfront.Net.Protocol/Messages/MatchMessages.cs`, `Size = 8` (`:34`):

```
u8   phase                 MatchPhase
u16  tickets0              DESCENDING, starts at 200
u16  tickets1              DESCENDING, starts at 200
u16  phaseSecondsRemaining 0xFFFF-style "no clock" is signalled by the presenter, not here
u8   humanPlayerCount
```

`WinningTeam` (`:69-76`) is a computed property: `None` unless phase is `Ended`/`Resetting`,
`None` on a tie, else `Tickets0 > Tickets1`.

Spec row: § 4.1 `| 0x45 | S_MATCH_STATE | 2 | Score, time, match state |` (line 280).

### 2.2 After P11

```
u8   phase                 unchanged
u16  score0                ASCENDING from 0          ← same two bytes, INVERTED MEANING
u16  score1                ASCENDING from 0          ← same two bytes, INVERTED MEANING
u16  phaseSecondsRemaining unchanged
u8   humanPlayerCount      unchanged
u16  victoryPoints         NEW — the margin needed to win
                           Size 8 → 10
```

`WinningTeam` becomes `ConquestScoreRule.Decide(Score0, Score1, VictoryPoints)` — the same
function the server ends the match with, so the two can no longer disagree.

**Why `victoryPoints` crosses the wire rather than being a shared constant.** The client cannot
draw the score bar without it: `ScoreUi.UpdateUi` (`:312-333`) is a two-branch renderer and both
branches divide by `victoryPoints` —

```csharp
bool flag = blueScore + redScore >= victoryPoints;          // :317
if (!flag) { blueBar.anchorMax.x = blueScore / victoryPoints;      // :321  two independent bars
             redBar.anchorMin.x  = 1 - redScore / victoryPoints; } // :322
else       { x3 = clamp01((blueScore - redScore + victoryPoints)   // :328  one margin bar
                          / (2 * victoryPoints)); ... }
```

— and `victoryPoints` is a **host-editable match setting** (`MainMenu.cs:87`), not a constant, so
it cannot live in `ProtocolConstants` and cannot be assumed. Sending it beside the two numbers it
scales keeps them un-drift-able, at a cost of 2 bytes on a message broadcast a few times a
second. The alternative — a separate one-shot match-config message — puts the scale and the
scaled values in two packets that can arrive out of order, and gives a late joiner a bar it
cannot draw until the next config broadcast.

### 2.3 The version bump — mandatory, and why

**`PROTOCOL_VERSION` 4 → 5.** Two independent reasons, either sufficient:

1. **The bytes changed.** `Size` 8 → 10. That is the spec's own stated trigger
   (`protocol-spec.md:1391-1393`).
2. **Even at unchanged size, the meaning of `tickets0`/`tickets1` inverts.** An old client
   reading a new server would render an ascending score as a descending ticket count: a match
   opening at 0/0 reads as "both sides have already lost", and the winner computation answers
   backwards for the whole round. This is worse than a decode failure, because it *looks* like
   it works. A mismatched `PROTOCOL_VERSION` produces `CONNECT_DENIED` code 2 — a refusal the
   player can be shown — which is the outcome we want.

**The freeze policy is satisfied, not waived** (`protocol-spec.md` § 15, "the wire gate"):

| # | Condition | How P11 clears it |
|---|---|---|
| 1 | `SpecChecker` green | run `dotnet run --project tools/SpecChecker`; `ci.ps1` runs it too |
| 2 | Hex-sample conformance test pinning the new bytes | new test in `Ironfront.Net.Protocol.Tests` for the 10-byte `S_MATCH_STATE` |
| 3 | A § 15 changelog row with "Wire change?" filled in | add the **5.0.0** row |
| 4 | `PROTOCOL_VERSION` bumped in the § 1 fenced block **and** the prose header line | condition 1 covers only the fenced block; check the header by eye (this is why the spec spells it out — the two drifted for the whole of v2's life) |

> **A gap found while writing this file. Owner ruled 2026-09-01: P11 patches it.** § 15's
> changelog has rows for 1.0.0, 2.0.0, 2.0.1 and 3.0.0 (amended). **There is no 4.0.0 row**, though
> the header and the code both say 4 — so condition 3 was never met for the v4 bump, and nothing
> mechanical will ever notice, because `SpecChecker` parses the § 1 fenced block and not § 15.
> **P11 writes BOTH rows**: the missing 4.0.0 (reconstructed from commit `9172920` / PR #222 — the
> `POS_MIN`/`POS_MAX` window move with `POS_RANGE` unchanged, ledger **X-53**) and its own 5.0.0.
> The reconstruction and its evidence are in [P11 § 3.4a](../phases/phase-p11-win-condition.md);
> ledger row **X-79** closes with it.

### 2.4 What must change together

Nothing in this list may land without the rest, because each half is silently wrong alone:

- `MatchStateMessage` struct, `Write`, `Read`, `Size`, `WinningTeam`
- `MatchStateMachine` — ascending accumulate, flag multiplier, margin end, remove the bleed's
  role as the ticket drain (see P11 for what happens to bleed)
- `MatchRules` — `StartTickets`/`TicketsPerDeath` retire or change meaning
- `ServerTickLoop.cs:1194` `ReportDeath(victim.Team)` → award the **opponent**
- `NetClientObjectivePresenter` → `ScoreUi.SetAuthoritativeState`, which grows a
  `victoryPoints` parameter
- `protocol-spec.md` § 4.1 row + § 15 row + header + fenced block
- the hex-sample test

---

## 3. `joinTicket` — carrying the team byte

### 3.1 Today — the payload is exactly full

`Ironfront.Net.Protocol/JoinTicket.cs:25-31`, 64 bytes total:

```
u32     playerId              4
u16     serverId              2
u16     roomId                2
u64     expiresAtUnixMs       8
u8[16]  displayNameUtf8      16   ← DisplayNameSize = 16 (:60)
                             ──
                             32   = SignedPayloadSize (:56)
u8[32]  hmac                 32   = HMAC-SHA256(first 32 bytes, SHARED_SECRET)
                             ──
                             64   = JOIN_TICKET_SIZE
```

There is no spare byte. `SignedPayloadSize` is 32 and `Size` is 64, and both are asserted.

### 3.2 After P13 — shrink the name, do not grow the ticket

**Owner decision (2026-09-01): shrink `displayName` 16 → 15.**

```
u32     playerId              4
u16     serverId              2
u16     roomId                2
u64     expiresAtUnixMs       8
u8      team                  1   ← NEW. 0 or 1; TeamId.None is not a legal ticket value
u8[15]  displayNameUtf8      15   ← DisplayNameSize 16 → 15
                             ──
                             32   unchanged
u8[32]  hmac                 32   unchanged
                             ──
                             64   unchanged
```

**`Size` does not change, `SignedPayloadSize` does not change, and the HMAC still covers exactly
the first 32 bytes.** The field ORDER above puts `team` before the name so the name stays the
trailing run — a truncation bug then loses a name character rather than the team byte, and a hex
sample reads left-to-right in declaration order.

**This is still a wire change** and still bumps the version — but it lands **inside the same
version bump as § 2.3** if P11 and P13 ship before the next release, exactly as the spec's own
3.0.0-amended row did for the vehicle-and-projectile track ("one protocol bump covers the whole
track, the client and server ship together"). If P11 has already shipped when P13 lands, P13
bumps 5 → 6 on its own. **P13 must check which case it is in and say so in its changelog row.**

**Truncation risk, named because it is silent.** 15 bytes of UTF-8 is 15 ASCII characters or
fewer non-ASCII ones. `JoinTicket.Issue` already truncates; a multi-byte character straddling the
15th byte must be dropped whole rather than cut, or the name renders as a replacement glyph. P13
owns a test for a 16-character name and a name whose 15th byte is mid-sequence.

### 3.3 Where the byte comes from and where it goes

```
LobbyService.RoomMember.Team          Ironfront.MasterServer/Lobby/LobbyService.cs:12
   auto-balanced on join at :157-162 (count both sides, take the smaller)
   NOTE: declared `byte Team { get; init; }` — INIT-ONLY.
         A lobby team switch requires making it settable, like `Ready` at :13.
        ↓ master issues the ticket
joinTicket.team
        ↓ client passes through verbatim in CONNECT_REQUEST
ServerTickLoop.OnClientConnected  :1513-1564  — today reads only displayName + playerId (:1527-1528)
        ↓
ServerActorRegistry.TryClaimPlayerSlot  :153-168 — today a first-fit linear walk, team-blind
        ↓
NetServerActor.Team / body.Team
```

P13 owns the whole run. Everything upstream of `RoomMember.Team` (the lobby UI that lets a player
pick a side) is P16.

---

## 4. Team values

`TeamId` lives at `Ironfront.Net.Protocol/Enums/GameplayEnums.cs:201-204`: `Team0`, `Team1`,
`None`. Use it everywhere; do not introduce a second encoding.

- **On the wire and on the server**, team 0 / team 1.
- **On screen**, team 0 is **blue** and team 1 is **red** — `ColorScheme.TeamColor` is the one
  place that mapping is written and every new UI element must go through it, not through a
  literal colour.
- **`TeamId.None` is legal in `S_MATCH_STATE.WinningTeam`** (undecided or drawn) and in
  `SpawnPoint.owner` as `-1` (any team). It is **not** legal in a join ticket or in
  `NetServerActor.Team`.

---

## 5. Consumers, so a change here can be traced

| Shape | Written by | Read by |
|---|---|---|
| `ConquestScoreRule` | **P11** | `MatchScoreboard` (offline), `MatchStateMachine` (server), `MatchStateMessage.WinningTeam` |
| `S_MATCH_STATE` v5 | **P11** | `NetClientObjectivePresenter` → `ScoreUi.SetAuthoritativeState` (P11, P17) |
| `S_PLAYER_SCORES` (0x51) | **P18** | the Tab scoreboard (P18). Per-player kills/deaths only — the TEAM score stays in `S_MATCH_STATE`, per spec § 4.11's surviving reasoning |
| `joinTicket.team` | **P13** | `ServerTickLoop.OnClientConnected`, `ServerActorRegistry.TryClaimPlayerSlot` (P13); produced from `RoomMember.Team` (P13), chosen by the player (P16) |
| `TeamId` / `ColorScheme.TeamColor` | already exists | P12 (local team, minimap), P16 (roster columns), P17 (HUD readout, scoreboard rows) |

---

## 6. Where the new UI code lives — the assembly seal

**P15, P16 and P17 all build Canvas UI that needs both halves of the codebase. They cannot put it
in one assembly, and the reason is structural.**

### 6.1 The seal, measured

```
Ironfront.Net.Unity.Client.asmdef   references: [ Shared, Input ]     autoReferenced: FALSE
Ironfront.Net.Unity.Shared.asmdef   references: [ ]                   autoReferenced: TRUE
Assembly-CSharp (predefined)        references: [ EditorHarness, Input, Server, Shared ]
```

Two consequences, both one-way and both hard:

- **`Net/Client/` cannot name any Assembly-CSharp type.** No `ColorScheme`, no `GameManager`, no
  `ActorManager`, no `MainMenu`, no `ScoreUi`. `tools/check-net-layering.ps1` RULE 6a fails the
  build on the *type name*, not on a resolved reference — so `EventType` in a `Net/Client` file
  fails even though it names nothing legacy. **Only CI and that script catch this;
  `dotnet build` never will**, because `Assets/Scripts/Net/Shared` has zero references and the
  Unity compile is the only thing that sees the real graph.
- **Assembly-CSharp cannot name any `Net/Client` type either.** The Client asmdef is
  `autoReferenced: false` and absent from the predefined manifest above. This surprises people —
  the seal is two-way.

`UnityEngine.UI` (`Button`, `Text`, `InputField`, `Canvas`, `Image`) is in neither camp and is
available to both. Only *project* types are sealed.

### 6.2 The pattern that already solves it

It is in the repo and it works. `Net/Shared` (autoReferenced, so **both** sides see it) declares
the seam; `Net/Client` calls through it; `Assets/Scripts/NetBindings/` — which is Assembly-CSharp —
implements it with real legacy types and registers itself:

```
Net/Shared/IObjectiveHud.cs        the interface
Net/Shared/NetClientBindings.cs    the registry:  NetClientBindings.Objectives?.…
Net/Client/NetClientObjectivePresenter.cs:182     calls through the registry
NetBindings/ClientSceneBindings.cs:120-122        implements it over ScoreUi
```

Eleven of these already exist (`IMinimapMarkers`, `IHitmarkerHud`, `ILocalPlayerRig`, …). **Add to
the set; do not invent a rival mechanism, and do not move `Net/Client` code into Assembly-CSharp to
dodge the seal.**

### 6.3 The two new seams Block C needs

| Seam | Declared in | Implemented in | Why |
|---|---|---|---|
| `ITeamPalette` | `Net/Shared` | `NetBindings/`, over `ColorScheme.TeamColor` | Every roster row, scoreboard row and team readout is coloured by team, and `ColorScheme` is Assembly-CSharp. A hardcoded blue/red in the UI would be a second copy of the mapping (contracts § 4) |
| `IPracticeLauncher` | `Net/Shared` | `NetBindings/`, over `MainMenu`/`GameManager`/`ActorManager` | The Practice entry starts the offline bot match, which is entirely legacy. `Net/Client` must not name `MainMenu.StartLevel` |

**Placement rule for Block C:** the new menu screens are network-flow UI and live in
`Net/Client/`. Anything they need from the legacy game crosses at one of the two seams above.
