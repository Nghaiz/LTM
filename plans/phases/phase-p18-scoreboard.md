# Phase P18 — the scoreboard, with real numbers on it

- **Plan:** [`../plan.md`](../plan.md) · **Block:** C (4 of 4) · **Size:** L · **Effort:** 1 session
- **Depends on:** **P17 landed** (the HUD and the `IMatchHud` seam it built), **P12 landed** (the
  local team), **P15 landed** (`ITeamPalette`).
- **Contracts:** [`../00-shared/team-multiplayer-contracts.md`](../00-shared/team-multiplayer-contracts.md)
  **§ 6** (assembly seal — required reading), **§ 4** (team values), **§ 2.3** (the freeze gate this
  phase clears for the second time).
- **Spec:** [`../00-shared/protocol-spec.md`](../00-shared/protocol-spec.md) is a **build input** —
  `tools/SpecChecker/Program.cs:32` opens it at runtime.
- **Filed:** 2026-09-01, split out of P17 on the owner's ruling that per-player kills and deaths
  ship rather than defer. P17 § 1.3 records why it is its own phase.

---

## 1. Three facts that decide the whole design

### 1.1 The server already counts. This is not a counting problem

`Ironfront.Net.Replication/Match/MatchScoreTally.cs` holds

```csharp
private readonly int[] _kills  = new int[ProtocolConstants.MAX_ACTORS];
private readonly int[] _deaths = new int[ProtocolConstants.MAX_ACTORS];
public int KillsOf(ushort actorId)
public int DeathsOf(ushort actorId)
public bool IsUntouched(ushort actorId) => KillsOf(actorId) == 0 && DeathsOf(actorId) == 0;
```

and it is live on the server at `ServerTickLoop.cs:1207` `public MatchScoreTally Scores`. P6 built
it for `GS_MATCH_ENDED`, and `ServerMasterReporter.cs:157` reads it today.

It also already distinguishes the cases that would otherwise be silently wrong:
`UnattributedDeaths` (the world or the victim itself killed them — counted, never scored) and
`OutOfRangeIds`. **Do not write a second tally.** The numbers exist; they have simply never left
the server.

### 1.2 They cannot go on `S_PLAYER_LIST` (0x4B) — the arithmetic forbids it

Today's entry (`PlayerListMessage.cs:51-67`, spec § 4.11):

```
u8   playerCount
repeat: u8 actorId · u8 nameLength · utf8 name (≤ 16 B)      → EntryHeaderSize 2 + 16 = 18 B
worst case 1 + 64 × 18 = 1153 B
```

`MAX_CHANNEL_PAYLOAD` is `MTU_SAFE 1200 − GSP_HEADER_SIZE 16 − CHANNEL_ENVELOPE_SIZE 3` = **1181**.
So 0x4B has **28 bytes of headroom in total**, and the spec's § 4.11 leans on that explicitly:
*"Worst case is `1 + 64 × 18 = 1153 B`, inside one un-fragmented channel-2 payload."*

| Change | Worst case | Fits 1181? |
|---|---|---|
| today | 1 + 64 × 18 = 1153 | yes, 28 B spare |
| + `u8` kills only | 1 + 64 × 19 = 1217 | **no, +36** |
| + `u8` kills, `u8` deaths | 1 + 64 × 20 = 1281 | **no, +100** |
| + `u16` kills, `u16` deaths | 1 + 64 × 22 = 1409 | **no, +228** |

**Every option overflows.** Extending 0x4B in place would silently trade the un-fragmented
guarantee for a scoreboard, on the map with the most players — exactly when it matters.

### 1.3 The spec's objection does not apply, and saying so is part of the work

Spec § 4.11 currently reads:

> **Names only, no scores**, despite what the § 4.1 row used to promise. Score and match time
> already travel in `S_MATCH_STATE` (0x45); a second copy here would be a second source of truth
> for the number that changes most often.

That objection is about the **team** score, and it is correct: `S_MATCH_STATE` carries `score0` /
`score1` (`u16` each after P11), and duplicating them would give two sources for one number.

**Per-player kills and deaths are not that number.** They travel nowhere on the wire today —
searched `Ironfront.Net.Protocol/**` and `Ironfront.Net.Replication/**`; `MatchScoreTally` is
server-only and its only reader is `ServerMasterReporter`. So this phase adds a number that has no
second source, and it must **amend § 4.11's sentence rather than contradict it** — leaving that
sentence standing beside a new scores message is how the next reader concludes one of the two is
wrong.

**Design, following from § 1.1–1.3: a new message, not a wider one.**

```
S_PLAYER_SCORES = 0x51        NEW — 0x51 is free (0x4B..0x50 are the highest assigned)
u8   playerCount
repeat playerCount times:
    u8   actorId              same u8 space as 0x4B, same MAX_ACTORS 64 justification
    u16  kills
    u16  deaths
worst case 1 + 64 × 5 = 321 B      comfortably inside 1181
```

`u16` rather than `u8` because a bot on a 40-bot map over a long session passes 255 deaths, and a
wrapped counter renders as a plausible small number rather than as an error. Two extra bytes per
entry costs 128 B at the theoretical worst case and buys a counter that cannot lie.

**Names stay in 0x4B, unchanged.** They have a different cadence — § 4.11 says *"Sent on join and
on change — names do not move"* — while scores change on every death. Splitting them lets each
keep its own send rule, which is the other half of why a new message beats a wider one.

---

## 2. File ownership

```
Ironfront.Net.Protocol/Enums/MessageTypes.cs                 S_PLAYER_SCORES = 0x51
Ironfront.Net.Protocol/Messages/PlayerScoresMessage.cs       NEW
Ironfront.Net.Replication/Server/ServerEventWriter.cs        the writer
Ironfront.Net.Replication/Client/ClientMessageRouter.cs      the route
Ironfront.Net.Replication/Client/PlayerScoreTable.cs         NEW — sibling of PlayerNameTable
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerTickLoop.cs send rule only
Ironfront_Reborn/Assets/Scripts/Net/Client/Hud/**            the Tab scoreboard
Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ScoreUi.cs   the Tab binding at :346 ONLY
Ironfront_Reborn/Assets/Prefab/Ingame UI Container.prefab    scoreboard elements, via the Editor
Ironfront.Net.Protocol.Tests/**                              hex sample + the version pin
Ironfront.Net.Replication.Tests/**
plans/00-shared/protocol-spec.md                             § 4.1 row, § 4.11 amendment, new § 4.12, § 15 row
Ironfront_Reborn/Assets/Plugins/Ironfront.Net.Protocol.dll    rebuilt artifact
Ironfront_Reborn/Assets/Plugins/Ironfront.Net.Replication.dll rebuilt artifact
tools/ClientWiringGate/**                                     authoring + wiring detectors
```

**Not owned:** `MatchScoreTally` itself — it already does the job (§ 1.1). `ScoreUi` beyond the one
Tab `KeyCode` binding: `SetAuthoritativeState` is **P11**'s and `Awake`/`UpdateUi` are **P12**'s.

---

## 3. Tasks

### 3.1 — `S_PLAYER_SCORES` (0x51) (L)

Implement the layout in § 1.3. Mirror `PlayerListMessage` (`Messages/PlayerListMessage.cs`) — same
file shape, same `HeaderSize` / `EntryHeaderSize` / `MaxBodySize` constant style, same
`SizeFor(entries)` helper — so the two read as a pair.

- **Writer:** `ServerEventWriter`, beside the `PlayerListMessage` writer.
- **Route:** `ClientMessageRouter`, and a `PlayerScoreTable` sibling to `PlayerNameTable`
  (`Client/PlayerNameTable.cs`) so the client keeps names and scores in two tables with two
  lifetimes, matching the two messages.
- **Send rule:** on change, not per tick. A death is the only thing that moves these numbers, and
  `ServerTickLoop` already knows when one happens — it is what calls `RecordDeath`. Coalesce to at
  most one send per tick; do **not** add a timer.
- **`MAX_ACTORS` pin:** the `u8 actorId` is safe only while `MAX_ACTORS ≤ 256`.
  `PlayerListVersionPinTests.cs` already pins that for 0x4B — extend it to 0x51 rather than writing
  a rival test, and follow § 4.11's stated reason: raising `MAX_ACTORS` past 256 would truncate ids
  silently and *"the symptom would be a scoreboard naming the wrong player"*. That sentence is now
  literally about this phase.

**Freeze gate — all four conditions** ([contracts § 2.3](../00-shared/team-multiplayer-contracts.md#23-the-version-bump--mandatory-and-why)):

1. `SpecChecker` green.
2. A hex-sample conformance test pinning the exact bytes of 0x51, in
   `Ironfront.Net.Protocol.Tests/Conformance/PacketHexSampleTests.cs` where the others live.
3. A § 15 changelog row with "Wire change?" filled in.
4. `PROTOCOL_VERSION` bumped in **both** the § 1 fenced block **and** the prose header line —
   condition 1 covers only the fenced block, and the two have drifted before.

**Version arithmetic — check, do not assume.** P11 takes 4 → 5 and P13 may ride inside it. If
this phase ships in the same unreleased train it rides inside 5 too, per the spec's own
`3.0.0 (amended)` precedent; if 5 has shipped, this is 6. **State which in the changelog row.**

### 3.2 — Amend spec § 4.11 and add § 4.12 (S)

Per § 1.3. Replace § 4.11's *"Names only, no scores"* sentence with one that says **why** names are
still alone in 0x4B — different cadence, and the 28-byte headroom that made a wider entry
impossible — and points at § 4.12. Add § 4.12 for `S_PLAYER_SCORES` in the shape of § 4.11.
Update the § 4.1 opcode table with the 0x51 row.

**Do not delete § 4.11's reasoning about `S_MATCH_STATE`.** It is still true and still binding: the
team score does not go here.

### 3.3 — The Tab scoreboard (L)

Hold Tab, see two team columns with name, kills, deaths, sorted. Names from `PlayerNameTable`,
numbers from `PlayerScoreTable`, teams from the snapshot, colours from **`ITeamPalette`**.

**Tab is currently bound to `HideVictoryScreen()`** (`ScoreUi.cs:346`); `:350` binds Home to
toggling the canvas. **Decision: the scoreboard takes Tab, and the victory screen keeps a
dismissal of its own.** Do not leave two behaviours on one key, and do not leave the victory screen
undismissable — criterion 6 grades both halves.

**A player with no name yet must render as a player, not vanish.** The two tables fill from two
messages that arrive independently; a row keyed only on the name table disappears whenever scores
arrive first. Key rows on the actor id.

**Rows for bots.** `MatchScoreTally` counts bots — they kill and die like anybody else, and
`ServerMasterReporter.cs:139` says so. 0x4B carries names for whoever the server puts in it, so
the scoreboard shows whatever it is sent. **Decide and record** whether the server sends bot rows
on 0x51: including them makes a 21v21 scoreboard, excluding them makes a scoreboard whose totals
do not explain the team score. Default: **send them** — the team score is driven by every death,
so a scoreboard that omits bots cannot be reconciled with the number above it.

### 3.4 — Author the elements, and gate them (M)

Scoreboard elements on `Ingame UI Container.prefab`, **through the Editor**, never by editing YAML.

Detectors follow `ScoreUiTextRefsAreAssigned`'s three-part shape (assigned; resolves to an object
that exists; not an object another field already drives), each mutation-tested.

**One more detector, and it is the important one:** `ClientWiringGate` must show `0x51` has a
subscriber. That is exactly the check that was green on every defect this whole plan exists to fix
(`GateRunner.cs:72-75` — it retires on subscription, not on pixels), so it is **necessary and not
sufficient**, and criterion 3 is what actually grades the phase.

---

## 4. Acceptance

| # | Criterion | Evidence |
|---|---|---|
| 1 | Hex-sample test pins the bytes of 0x51; `SpecChecker` green; § 4.1, § 4.11, § 4.12 and a § 15 row all updated; `PROTOCOL_VERSION` correct in the fenced block **and** the prose header | `tools/ci.ps1` + diff |
| 2 | **Screenshot: holding Tab shows both teams' rosters with real names from a two-client run**, each name on the correct side | screenshot from both machines |
| 3 | **The captured artifact shows NON-ZERO kills and deaths.** An all-zero column does not pass | two-client lane-B record or screenshot, taken after at least one kill on each side |
| 4 | The two clients' scoreboards agree with each other **and** with the server's `MatchScoreTally` | both screenshots + server log |
| 5 | A row appears for an actor whose name has not arrived yet, keyed on actor id | scripted run or test |
| 6 | Tab no longer collides with the victory screen, and the victory screen is still dismissable | screenshot of each |
| 7 | Whatever § 3.3 decided about bot rows is stated, and the scoreboard's totals reconcile with the team score on screen | screenshot + one line of arithmetic |
| 8 | Every new element is gated by a detector observed RED; 0x51 has a subscriber in `ClientWiringGate` | mutation results + gate output |
| 9 | `MaxNameBytes`/`MAX_ACTORS` pin extended to 0x51 rather than duplicated | diff |
| 10 | `tools/ci.ps1` green | CI |

**Criterion 3 is the owner's ruling made gradeable.** `CollectScores`' recorded concern — *"rows of
all-zero scores are indistinguishable from a match where nobody scored"*
(`ServerMasterReporter.cs:125-134`) — is not a reason to defer this work; it is the reason the
artifact must contain a non-zero number before the phase is done.

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Scores added to 0x4B instead, overflowing the un-fragmented payload at full player count — visible only on a full server | 3 | 5 | **15** | § 1.2's table is the arithmetic; § 1.3 is the design. Criterion 1 pins the new opcode instead |
| An all-zero scoreboard passes as "working" | 4 | 4 | **16** | Criterion 3 requires a non-zero number in a captured artifact — the phase cannot be closed on a fresh match |
| Rows keyed on the name table; players vanish when scores arrive first | 3 | 4 | 12 | § 3.3 keys on actor id; criterion 5 |
| Version arithmetic wrong because P11/P13's train state was assumed | 3 | 3 | 9 | § 3.1 forces the check before the changelog row is written |
| § 4.11's "names only, no scores" left standing beside a scores message; the next reader thinks one is wrong | 3 | 3 | 9 | § 3.2 amends it in the same PR and keeps its `S_MATCH_STATE` reasoning |
| Bot rows omitted; the scoreboard cannot be reconciled with the team score | 3 | 3 | 9 | § 3.3's default is to send them; criterion 7 is the arithmetic |
| `u8` counters wrap on a long session and render as small plausible numbers | 2 | 4 | 8 | `u16` chosen in § 1.3, at 2 B per entry |

Two at ≥ 15, and both are mitigated by an acceptance criterion that a green suite cannot satisfy.

---

## 6. Out of scope

- **The team score.** It travels in `S_MATCH_STATE` and stays there — spec § 4.11's reasoning
  survives this phase (§ 1.3).
- **A second tally.** `MatchScoreTally` already counts; this phase transports and renders (§ 1.1).
- **End-of-match reporting.** `ServerMasterReporter.CollectScores` already reads the tally for
  `GS_MATCH_ENDED`; nothing here changes it.
- **Assists, streaks, per-weapon breakdowns, score-per-player beyond K/D.** Not in the ruling.
- **World-space nametags** and **spawn choice** — P17 § 6.
