# Phase P11 — the win condition the netcode never had

- **Plan:** [`../plan.md`](../plan.md) · **Block:** D (1 of 2) · **Size:** L · **Effort:** 1 session
- **Contracts:** [`../00-shared/team-multiplayer-contracts.md`](../00-shared/team-multiplayer-contracts.md)
  §§ 1, 2, 4 — the score model, the `S_MATCH_STATE` v5 layout, the team value space.
  **Read that file before step 1.** It holds the shapes; this file holds the work.
- **Spec:** [`../00-shared/protocol-spec.md`](../00-shared/protocol-spec.md) is a **build input**,
  not a document — `tools/SpecChecker/Program.cs:32` opens it at runtime.
- **Filed:** 2026-09-01, from the owner's correction to the multiplayer brainstorm. The brainstorm
  ranked friendly fire first; the owner cancelled that (friendly fire is intended) and named this
  instead as *"the single largest correctness item"*.

---

## 1. The defect, stated once

**The networked match and the offline match are playing two different games.**

The game's own rule — the one `MatchScoreboard` implements, `Actor.Die` feeds and `ScoreUi` draws —
is: score **ascends** on kills, each point multiplied by the scoring team's flag count, and a team
wins when it leads by `VictoryPoints`. The netcode instead **descends** 200 tickets, charges the
**victim's own side** for the death, bleeds by flag differential, and ends when either side hits 0.

The full side-by-side, with every line number, is
[contracts § 1.2](../00-shared/team-multiplayer-contracts.md#12-what-the-netcode-does-instead).
It is not restated here on purpose: one copy, one place to correct it.

Two consequences that are worse than "the numbers differ":

- **`MatchStateMessage.WinningTeam` is meaningless under a margin rule.** `MatchMessages.cs:69-76`
  returns `Tickets0 > Tickets1`. A margin rule does not end when someone is ahead; it ends when
  someone is ahead **by 200**. The message can name a winner in a match that is not over, and the
  server ends the match by a rule the message does not know.
- **The score bar on screen is drawing the wrong quantity.** `ScoreUi.UpdateUi:328` renders
  `(blueScore − redScore + victoryPoints) / (2 × victoryPoints)` — a **margin** bar. It has been
  fed ticket counts. Even when the numbers were right, the picture was of a different rule.

### 1.1 The trap that will silently kill this phase

`MatchScoreboard.ScoreMultiplier(flags) => flags` (`:160-163`) is the identity function, so

> **a team holding zero capture points scores zero per kill.**

Offline this never shows: Dustbowl opens with Oasis owned by team 0 and Fortress by team 1, so
both sides open on 1. On the server, if the capture points are not yet owned when `Playing`
begins, both scores stay pinned at 0, no margin is ever reached, and the match runs forever
looking like nothing is broken. **Step 3.2 asserts the opening flag counts before anything else,
and acceptance criterion 3 grades it.** Do not skip it because the offline game works.

---

## 2. File ownership

This phase owns these paths and nothing else. Anything outside them is a different phase's.

```
Ironfront.Net.Replication/Match/ConquestScoreRule.cs          NEW
Ironfront.Net.Replication/Match/MatchStateMachine.cs
Ironfront.Net.Replication/Match/MatchRules.cs
Ironfront.Net.Protocol/Messages/MatchMessages.cs
Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/MatchScoreboard.cs
Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ScoreUi.cs        (SetAuthoritativeState only)
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerTickLoop.cs      (ReportDeath call site only)
Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientObjectivePresenter.cs
Ironfront_Reborn/Assets/Scripts/NetBindings/ClientSceneBindings.cs
Ironfront.Net.Protocol.Tests/**                                   (hex sample)
Ironfront.Net.Replication.Tests/**
plans/00-shared/protocol-spec.md                                  (§ 4.1 row, § 15 row, header, fenced block)
Ironfront_Reborn/Assets/Plugins/Ironfront.Net.Protocol.dll        rebuilt artifact
Ironfront_Reborn/Assets/Plugins/Ironfront.Net.Replication.dll     rebuilt artifact
```

**`ScoreUi.UpdateUi` and `ScoreUi.Awake` are P12's, not this phase's.** P11 grows
`SetAuthoritativeState` a parameter and writes the bars from it; P12 gates the offline renderer
that currently overwrites them. If both phases edit `ScoreUi.cs`, land P11 first and P12 rebases.

---

## 3. Tasks

### 3.1 — Extract the rule into one function (S)

Create `Ironfront.Net.Replication/Match/ConquestScoreRule.cs` exactly as
[contracts § 1.3](../00-shared/team-multiplayer-contracts.md#13-the-ssot-decision-and-why-it-is-not-simply-call-matchscoreboard)
specifies: pure statics, no state, no events.

Then make `MatchScoreboard` **delegate**:

- `MatchScoreboard.ScoreMultiplier` becomes a public static **forwarder** to
  `ConquestScoreRule.ScoreMultiplier`. Do not delete it — `ScoreUi` and existing tests name it,
  and the forwarder is what makes "one copy" checkable by grep.
- `MatchScoreboard.AddScore`'s two `>=` comparisons become one `ConquestScoreRule.Decide` call.
  The `Win(bool)` latch, the `Changed`/`Scored`/`Ended` events and the `GameEnded` short-circuit
  all stay where they are — those are state, and state does not move.

**Behaviour must not change offline.** The offline path has tests; they are the guard. If any
existing `MatchScoreboard` test goes red, the extraction is wrong, not the test.

**Why not the other direction:** Assembly-CSharp compiles last and no asmdef references back
(`tools/check-net-layering.ps1:11-12`), so `Replication` cannot call `MatchScoreboard`, ever.
Assembly-CSharp already imports `Ironfront.Net.Replication.Match` (`CapturePoint.cs`,
`MatchScoreboard.cs`), so no asmdef changes and no new reference is introduced.

### 3.2 — Assert the opening flag counts on the server, BEFORE changing the score (S)

Read § 1.1 again. Before any score code changes, establish on the server what
`MatchStateMachine`'s capture-point ownership is at the instant `Playing` begins, on **Dustbowl**:

- If both teams open on ≥ 1 owned point, the multiplier is safe and the migration proceeds.
- If either opens on 0, the multiplier pins that side's score at 0 forever and **the migration
  must carry an answer** — a floor of 1 in `ScoreMultiplier`, or an opening-ownership fix, or an
  explicit "no score before first capture" design. Decide it here, with the measurement in hand.
  Do not decide it from the offline game's behaviour, which cannot exhibit the case.

`MatchController.cs:178-205` already logs an error when no capture point is authored to either
team, naming Dustbowl's Oasis (team 0) and Fortress (team 1). That log is the cheapest place to
take this reading.

**Record the answer in the phase's report.** P18 (Island) depends on it: an Island authored with
no team-owned capture point reproduces exactly this, on a map with no offline history to hide it.

### 3.3 — Migrate `MatchStateMachine` to the margin rule (L)

Six edits, and they are one change — none is separately shippable:

1. **Direction.** `_ticketsFloat0` / `_ticketsFloat1` become ascending score accumulators
   starting at 0. Rename them; a field called `tickets` holding a score is how the next reader
   re-introduces the bug.
2. **`ReportDeath(byte team)`** (`:202-212`) currently subtracts `TicketsPerDeath` from the
   **victim's** side. It must **award the opponent**:
   `team == Team0` ⇒ team 1 scores; `team == Team1` ⇒ team 0 scores. Each award goes through
   `ConquestScoreRule.Award(points, flagsOfScoringTeam)`. The `Phase != Playing` early-return
   (`:204`) stays, and its remark stays with it.
   **This is where friendly fire pays.** `Actor.cs:905` already credits the victim's opponent, so
   a team-kill hands the enemy a point on both runtimes once this lands. No gate, by owner
   decision — see
   [contracts § 1.4](../00-shared/team-multiplayer-contracts.md#14-friendly-fire--deliberately-not-gated).
3. **Flags become a multiplier, not a bleed.** `DrainTickets` (`:382-394`) counts owned points per
   team and subtracts from the side with fewer. Under the margin rule the flag count is already
   in the score through `Award`. Deleting `DrainTickets` outright removes the only pressure that
   makes a stalemate resolve, so the phase must **decide and record** which of these it does:
   - delete it, and rely on the multiplier alone (holding more flags makes every kill worth more —
     which is the offline game's own answer, and it has no bleed either); **or**
   - keep it as an ascending trickle to the side holding more points.
   The offline rule has no bleed. Default to matching the offline rule — that is the whole point
   of this phase — and record the deletion in the phase report so it is a decision, not a loss.
4. **End condition.** `:286` `if (_ticketsFloat0 <= 0f || _ticketsFloat1 <= 0f)` becomes
   `if (ConquestScoreRule.Decide(Score0, Score1, VictoryPoints) != TeamId.None)`.
5. **`MatchRules`.** `StartTickets` (`:33`) and `TicketsPerDeath` (`:36`) either retire or become
   `VictoryPoints` / `PointsPerKill`. `MatchRules`' own remark at `:32` already calls
   `StartTickets` *"the original `GameManager.victoryPoints`"* — so the number was always meant to
   be the same 200, under the wrong verb. `BleedPerPointPerSecond` follows edit 3.
   **`ActorIdQuarantineSeconds`, `CaptureSendThreshold`, `MaxCaptureHeadcount`, `WarmupSeconds`,
   `PostMatchSeconds`, `MinPlayersToStart` are untouched.**
6. **Elimination stays.** `MatchStateMachine.cs:409-447` (lose every spawn point ⇒ lose) is the
   networked twin of `MatchScoreboard.cs:139-146` and is **retained unchanged**, including its
   opening-seconds gate. Two win conditions is correct; the offline game has two.

### 3.4 — `S_MATCH_STATE` v5 (M)

Implement the layout in
[contracts § 2.2](../00-shared/team-multiplayer-contracts.md#22-after-p11) — `Size` 8 → 10,
`victoryPoints` appended as a `u16`, `tickets0`/`tickets1` renamed to `score0`/`score1`.

`WinningTeam` (`:69-76`) becomes `ConquestScoreRule.Decide(Score0, Score1, VictoryPoints)` behind
the existing `Phase != Ended && Phase != Resetting` guard. The tie case disappears into `Decide`,
which already returns `None` when neither margin is met.

Then clear the freeze gate — all four conditions, listed with their checks in
[contracts § 2.3](../00-shared/team-multiplayer-contracts.md#23-the-version-bump--mandatory-and-why).
**Condition 4 is checked by eye**: `SpecChecker` parses only the fenced block, and the prose
header and the fenced block have already drifted once on this project.

### 3.5 — Draw the right bar (M)

`ScoreUi.SetAuthoritativeState` (`:180`) grows a `victoryPoints` parameter and, for the first
time, **writes `blueBar` / `redBar` / `intercept`**. Today those three are touched only by
`UpdateUi` — the offline renderer — which is why the most prominent element on screen has never
been networked (audit F3).

Port the two-branch geometry from `UpdateUi:317-333` verbatim into the authoritative path. Do not
reimplement it from the formula in this document: the branch condition, the `1f −` on the red
anchor, and the `Clamp01` are each doing something, and `UpdateUi` is the working copy.

Extract the geometry into one private static that both callers use, so the offline and networked
bars cannot diverge — that is the same "one copy" discipline as step 3.1, one layer up.

The early-return at `:187-195` grows the new parameter into its comparison, or the bar will not
update when only `victoryPoints` changes.

Thread the value through: `MatchStateMessage.VictoryPoints` → `NetClientObjectivePresenter:182-183`
→ `ClientSceneBindings.cs:120-122` → `ScoreUi.SetAuthoritativeState`.

### 3.6 — Rebuild the plugin DLLs (S)

`tools/build-libs.ps1`. `Ironfront.Net.Protocol.dll` and `Ironfront.Net.Replication.dll` both
changed. **Unity reads the DLLs, not the source** — a new type or a changed struct is invisible to
the Editor until this runs, and standing rule 5 requires the rebuilt artifacts in the same PR.

---

## 4. Acceptance

Criteria 3, 5 and 6 are the ones that cannot be satisfied by a green suite.

| # | Criterion | Evidence |
|---|---|---|
| 1 | `ConquestScoreRule` is the only implementation of the multiplier and the margin test. `grep -rn "ScoreMultiplier\|>= .*+ *VictoryPoints"` finds the rule once and forwarders elsewhere | grep output in the report |
| 2 | Every pre-existing `MatchScoreboard` and `ScoreUi` offline test passes **unchanged** | `dotnet test`, 8 projects |
| 3 | **The opening flag count per team on Dustbowl is measured, stated, and its consequence for `ScoreMultiplier` answered** — not inferred from the offline game | server log excerpt in the report |
| 4 | A hex-sample test pins the 10 bytes of `S_MATCH_STATE` v5; `SpecChecker` green; § 15 has a 5.0.0 row; `PROTOCOL_VERSION` reads 5 in the fenced block **and** the prose header | `tools/ci.ps1` + the diff |
| 5 | **Two-client run: both clients' score numbers ascend, agree with each other, and agree with the server's log.** A kill moves the killer's side up, not the victim's side down | lane-B record + screenshot |
| 6 | **Screenshot: the score bar moves with the server's numbers**, and the bar's position matches the margin the numbers imply | screenshot, both branches of `:317` if reachable in one run |
| 7 | A match reaches `Ended` by the margin rule, and `WinningTeam` names the side that was ahead by `VictoryPoints` — verified against the server's own end decision | lane-B record |
| 8 | An old (v4) client is refused with `CONNECT_DENIED` code 2 rather than joining and rendering backwards | one deliberate mismatched-version run |
| 9 | `tools/ci.ps1` green | CI |

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Zero-flag multiplier pins both scores at 0; match never ends and looks fine | 4 | 5 | **20** | Step 3.2 measures it before any score code changes; criterion 3 grades it |
| Bleed deleted, stalemates never resolve, and nobody notices until a long run | 3 | 4 | 12 | Step 3.3 edit 3 forces the decision to be recorded; criterion 7 needs a match that actually ends |
| Version bumped in the fenced block but not the prose header — SpecChecker stays green | 3 | 3 | 9 | Condition 4 is explicitly by-eye; it has drifted on this project before (`protocol-spec.md:1412`) |
| `ScoreUi` conflict with P12 | 3 | 2 | 6 | P11 owns `SetAuthoritativeState`; P12 owns `Awake`/`UpdateUi`. Land P11 first |
| The offline path regresses while nobody is playing offline | 2 | 4 | 8 | Criterion 2 — pre-existing tests pass **unchanged**, not adjusted |
| Plugin DLLs not rebuilt; Editor compiles against the old struct | 2 | 4 | 8 | Step 3.6, standing rule 5 |

Nothing scores ≥ 15 except the multiplier trap, and step 3.2 is its mandated mitigation — it runs
**first**, not alongside.

---

## 6. Out of scope

- **A friendly-fire gate.** Cancelled by the owner; friendly fire is intended. See
  [contracts § 1.4](../00-shared/team-multiplayer-contracts.md#14-friendly-fire--deliberately-not-gated).
  `ServerActorDamageSink.ApplyDamage` ignoring `attackerId` is correct and is not edited here.
- **`ScoreUi.Awake` / `UpdateUi` gating** — P12.
- **Per-player scores.** `MatchScoreboard` holds four counters and no rows; the Tab scoreboard is
  P17 and builds from `PlayerListMessage`, not from here.
- **`GameManager.victoryPoints`' ownership.** Ledger C-5 records it among five unowned loose
  values; this phase reads it and does not relocate it.
- **The missing 4.0.0 changelog row.** Noted in contracts § 2.3; back-filling it is the owner's
  call, not this phase's.
