# P18 — the scoreboard, with real numbers on it

- **Plan:** [`../phases/phase-p18-scoreboard.md`](../phases/phase-p18-scoreboard.md)
- **Branch:** `feat/p18-scoreboard` · **Base:** `develop`
- **Date:** 2026-09-02
- **Run:** `artifacts/lane-b/p18-01` (three clients, `sbclose` set, spawn 0, RK-44)

---

## 1. What shipped

| § | Thing | Where |
|---|---|---|
| 3.1 | `S_PLAYER_SCORES` (0x51): the codec, the writer, the route, the client table | `PlayerScoresMessage`, `ServerEventWriter.WritePlayerScores`, `ClientMessageRouter.OnPlayerScores`, `PlayerScoreTable` |
| 3.1 | The send rule — on change, coalesced to one per tick | `ServerTickLoop._scoresDirty` + `EmitPlayerScores` |
| 3.2 | § 4.11 amended, § 4.13 written, § 4.1 row, § 15 row, `PROTOCOL_VERSION` 6 → 7 | `plans/00-shared/protocol-spec.md` |
| 3.3 | The Tab board: two columns, names, kills, deaths, totals, team colours | `IMatchHud` + `MatchHud` + `NetClientCombatPresenter.PushScoreboard` |
| 3.3 | Tab released by the victory banner, which keeps a dismissal on `V` | `ScoreUi.Update` |
| 3.4 | Seven authoring detectors, each observed RED; 0x51's subscriber check likewise | `MatchHudWiringDetectors`, `GateRunner` |

**New files**

```
Ironfront.Net.Protocol/Messages/PlayerScoresMessage.cs      the codec
Ironfront.Net.Replication/Client/PlayerScoreTable.cs        the client table
Ironfront.Net.Replication.Tests/PlayerScoreTableTests.cs    its tests
tools/lane-b/scoreboard-*.json, sbclose-*.json              the capture sets
```

---

## 2. Three decisions the plan did not make, and one it made wrongly

### 2.1 The row carries a `u8 team`, which § 1.3 said it would not

§ 1.3 gives the row as `actorId`, `kills`, `deaths`, and § 3.3 says "teams from the snapshot".
**The snapshot cannot answer.** `InterestManager` emits actors in relevance buckets under a
per-snapshot ceiling with a shed cursor, so a client holds a team only for the actors it can
currently see. On the run below that is 3 of 56 — the rest of a 41-bot roster would have landed
on no side at all, and criterion 2 asks for each name on the correct side.

So the row is six bytes, not five: worst case `1 + 64 × 6 = 385 B` against the same 1181-byte
budget § 1.2's table is drawn on, which is the whole reason a new opcode was affordable in the
first place. The reasoning is in `PlayerScoreEntry.Team`, in spec § 4.13, and in the changelog row
— **not** left as a diff a later reader has to reconstruct.

It is not the second source of truth § 4.11 refuses. That objection is about the team **score**, a
single number that changes many times a match; this is a per-actor assignment that changes at most
once a life, written from one answer in one tick loop.

### 2.2 `PROTOCOL_VERSION` 6 → 7, and the arithmetic was checked rather than assumed

§ 3.1 asks for the check. v5 (P11) and v6 (P13) have both shipped — live in
`ProtocolConstants.cs`, in the spec header, and each with its own § 15 row — so this is 6 → 7 on
its own rather than an amendment to the v6 row.

**A new opcode counts as a wire change** on the **3.0.0** row's precedent, where six of them were
recorded as one. It is milder than any bump before it and the changelog row says so plainly: a v6
client receiving 0x51 counts it in `UnknownMessages` and drops it, so nothing decodes wrongly. The
bump is still right — the alternative is a fleet where "which opcodes does the other end know" has
no answer on the wire.

### 2.3 The victory banner's dismissal moved to `V`

§ 3.3 rules that the scoreboard takes Tab and the banner keeps a dismissal of its own; it does not
say which key. `V` is free in every code poll **and** in `ProjectSettings/InputManager.asset` —
checked, not assumed, because the last key chosen without reading that file was `Return`, which
the `Loadout` axis already owned and which therefore opened the chat line and toggled the deploy
screen in the same press (`ClientChatSender._openKey`'s remark). The banner also still self-hides
after five seconds, so `V` is a convenience over a screen that cannot get stuck.

### 2.4 Bot rows are sent — § 3.3's default, taken

48 rows on team 1 and 8 on team 2 in the run below, and the totals under each heading are what
criterion 7 reconciles with.

---

## 3. Evidence

### 3.1 The board, captured

`artifacts/lane-b/p18-01/observer-a-07-scoreboard.png` and its two siblings. Both columns, both
headings with roster size and totals, names where `S_PLAYER_LIST` has supplied them and `actor N`
where it has not, and the side's colour from `ITeamPalette`.

### 3.2 The numbers, and all three clients agreeing exactly

From the `scoreboard` block each client wrote beside its own screenshot:

| Client | rows | kills | deaths | `actor 43` | `actor 41` |
|---|---|---|---|---|---|
| DRIVER | 56 | 10 | 10 | OBS-B 7/0 | DRIVER 3/7 |
| OBS-A | 56 | 10 | 10 | OBS-B 7/0 | DRIVER 3/7 |
| OBS-B | 56 | 10 | 10 | OBS-B 7/0 | **(unnamed)** 3/7 |

**Criterion 3 is met.** Seven kills and seven deaths on two named rows; an all-zero column would
not have passed and did not have to be argued about, because the block carries `totalKills`.

**Criterion 4's client-to-client half is met exactly** — not approximately: identical rows,
identical numbers, at revisions 12/13/13. The server-side half is **not independently sampled**:
`MatchScoreTally` is read by `ServerMasterReporter` at match end and this run did not end a match,
so what is shown is that three clients agree with each other and with the one message the tally
writes. Stated rather than glossed.

**Criterion 5 is met, and by accident rather than by construction** — which is better evidence.
OBS-B's own board carries `actor 41` with 3 kills and 7 deaths and **no name**, while the other two
clients name it DRIVER. That is exactly the case § 3.3 predicted: two messages arriving
independently, scores first. A row keyed on the name table would have shown 55 rows there.

### 3.3 The detectors, each observed RED

Seven prefab references nulled one at a time, the gate run after each:

```
RED  _scoreboardRoot            RED  _scoreboardTeam0Header    RED  _scoreboardTeam0Names
RED  _scoreboardTeam0Scores     RED  _scoreboardTeam1Header    RED  _scoreboardTeam1Names
RED  _scoreboardTeam1Scores
```

and the subscriber check, with `OnPlayerScores += _scores.Apply` deleted:

```
[G1] ClientMessageRouter.OnPlayerScores has no production subscriber. The server frames it,
     the client decodes it, the router raises it, and the delegate is null.
```

**That one is necessary and not sufficient, and § 3.4 says so.** G1 retires an event on
SUBSCRIPTION; the whole of this plan exists because a subscribed opcode that draws nothing reports
green. Criterion 3 is what grades the pixels.

### 3.4 Suite and gates

`tools/ci.ps1 -SkipUnity` PASSED in 2:24 — 8 test projects, 2,226 tests, zero failures;
`SpecChecker` green on 90 constants; layering, meta, duplicate-assembly and diagnostics-strip
checks all pass. The Unity compile was proved in the live Editor instead (the prefab builder ran
through MCP reflection, which requires both changed assemblies to have compiled), and the player
built cleanly for the run above.

---

## 4. Two defects this work surfaced

### 4.1 No bot kill can ever be credited — `ServerCombatEvents.ReportDeath` names no killer

**The first capture attempt produced 12 deaths and zero kills**, on every client. That was not a
scoreboard fault. Every killfeed row on that board read:

```
killerActorId: 65535, cause: Bullet, environment: true
```

`ServerCombatEvents.ReportDeath` — whose own summary says it covers "a bot's bullet, a grenade, a
fall, a vehicle" — passes `DeathMessage.EnvironmentKiller` unconditionally. So every bot-versus-bot
bullet kill lands in `MatchScoreTally.UnattributedDeaths`: counted, never scored, exactly as that
class documents. The tally is right; its input is not.

The killfeed has been rendering "The world → actor 21" for every bot kill since it shipped, and
nobody could see it, because before P17 the killfeed had no names and before this phase there was
no column where a missing kill would show up as a zero. **This is the scoreboard doing its job on
its first run.**

Not fixed here. The fix threads the attacker from `Actor.cs` — the offline damage path, in
`Assembly-CSharp`, outside this phase's ownership and outside § 6, which puts the tally out of
scope. It wants its own phase and its own verification. The evidence is in
`artifacts/lane-b/p18-01`'s predecessor run and reproduces on any bot-only engagement.

### 4.2 The lane-B recorder wrote JSON that no JSONL reader accepts — fixed

`LaneBCheckpointRecorder.Escape` escaped backslash and quote and nothing else. `ScoreUi`'s flag
labels read `"2\n"` off the prefab, so `AppendHud` split its record across three physical lines,
and **25 of 119 records on one file would not parse** — including every record this phase added a
block to. Found while reading P18's own artifact back.

Fixed at source: full RFC 8259 escaping, every character below U+0020. Exhaustive rather than the
three familiar ones, because the next label to break it would carry something rarer and would look,
again, like a truncated file rather than an escaping bug.

### 4.3 And one layout fault only the artifact could show

The first authoring put 32 rows at 26 px from y = 280 and ran team 1's column **straight off the
bottom of the screen**. Every reference resolved, every detector was green, and the JSON block was
perfect. It was visible only on the PNG — which is § 3.4's own point about `ClientWiringGate`,
arriving one layer up.

---

## 5. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | Hex sample pins 0x51's bytes; SpecChecker green; §§ 4.1, 4.11, 4.13 and a § 15 row updated; version correct in the fenced block **and** the header | **met** |
| 2 | Screenshot: holding Tab shows both teams' rosters, each name on the correct side | **met** — `p18-01/*-scoreboard.png`, three clients |
| 3 | The captured artifact shows NON-ZERO kills and deaths | **met** — 10 kills, 10 deaths, two named rows at 7/0 and 3/7 |
| 4 | The clients' scoreboards agree with each other and with the server's tally | **half met** — clients agree exactly; the server tally was not independently sampled (§ 3.2) |
| 5 | A row appears for an actor whose name has not arrived, keyed on actor id | **met** — OBS-B's board, `actor 41` unnamed with 3/7 |
| 6 | Tab no longer collides with the victory screen, and the screen is still dismissable | **met** — `V`, checked against `InputManager.asset` (§ 2.3) |
| 7 | The bot-row decision is stated and the totals reconcile with the team score | **met, with the gap named** — bots are sent (§ 2.4); the board's 10 kills sit under a red score of 14, and the difference is the environment deaths of § 4.1, which the tally counts and cannot credit. The arithmetic reconciles once that defect is read as part of it |
| 8 | Every new element gated by a detector observed RED; 0x51 has a subscriber | **met** — § 3.3 |
| 9 | The `MAX_ACTORS` pin extended rather than duplicated | **met** — `PlayerListVersionPinTests` now covers both opcodes in one assertion |
| 10 | `tools/ci.ps1` green | **met** — 2:24, zero failures |

---

## 6. What a reader should not conclude

- **Not that the tally is verified end to end.** Criterion 4's server half is open (§ 3.2).
- **Not that bot kills work.** They are counted as deaths and credited to nobody (§ 4.1).
- **Not that the row cap is enforced anywhere but in the HUD.** `ScoreboardRowsPerTeam` is read by
  the builder and by nothing in CI — a team of more than 32 renders 32 rows and states the true
  count in its heading, which makes the truncation visible but does not prevent it.
