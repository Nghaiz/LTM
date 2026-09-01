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

### 1.1 The opening-ownership invariant is already held — do not build a check for it

`MatchScoreboard.ScoreMultiplier(flags) => flags` (`:160-163`) is the identity function, so a team
holding zero capture points scores zero per kill. That is the rule rather than a hazard, and the
case where **both** sides open on zero cannot occur on either shipped map and is already caught
loudly: `MatchController.cs:199-205` logs an error when no point is authored to either team, and
its own remark names the real consequence — `ApplyElimination` reads two zero spawn-point counts
as a double wipe-out *"one second into Playing -- the loop X-53 was."* Both maps open one point per
side (Dustbowl `1, -1, -1, 0, -1, -1`; Island `0, 1, -1, -1, -1`), so a match opens 1/1 at
multiplier x1.

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

### 3.2 — Pin the scoring event before porting it (S)

Read [contracts § 1.1a](../00-shared/team-multiplayer-contracts.md#11a-the-scoring-event-pinned)
and hold all three invariants through the port. Restated here only as the checklist:

- **One point to the team OPPOSITE the victim's, keyed on the victim's team and nothing else.**
  No branch reads the killer. Friendly fire therefore scores for the enemy, and **bots count the
  same as humans**.
- **Only an actual death scores; a death scores once.** Damage that does not kill scores nothing.
  The single-fire edge is structural — `ServerActorDamageSink.ApplyDamage` flips
  `victim.IsAlive = false` (`:92`) so the next call reports `died:false`, and its remark says that
  is *"what makes the edge single-fire without anyone having to remember to make it so."*
  **Do not add a scoring call in the damage path.**
- **The hook does not move.** `ServerTickLoop.ReportDeathToMatch(victimActorId)` (`:1188`, reached
  from `:1114`) is already the death edge; only the direction of `:1194` changes. And note the
  server **never calls `Actor.Die()`** (`ServerActorDamageSink.cs:86-90` — it is private and
  reaches for `IngameUi`/`ScoreUi`), which is exactly why 3.1 extracts a shared rule rather than
  routing one runtime through the other's method.

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
   All three invariants from § 3.2 apply to this edit and it is the only place they can be lost.
   **This is where friendly fire pays:** the award is keyed on the victim's team, so a team-kill
   hands the enemy a point — no gate, by owner decision
   ([contracts § 1.4](../00-shared/team-multiplayer-contracts.md#14-friendly-fire--deliberately-not-gated)).
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

### 3.4a — Back-fill the missing 4.0.0 changelog row (S)

**Owner ruling, 2026-09-01: P11 patches this rather than leaving it open.** The reasoning is that
P11 is already editing § 15 and is therefore the right person at the right time. This closes ledger
row **X-79**.

§ 15's changelog holds rows for 1.0.0, 2.0.0, 2.0.1, 3.0.0 and 3.0.0 (amended) — and **nothing for
4.0.0**, although the header and `ProtocolConstants.cs:16` both say 4. So the live v4 bump cleared
three of its own four gate conditions and not the third.

**The content is established, not invented.** It was reconstructed from the commit that made the
bump, and every clause below is quoted or measured rather than inferred:

- **Commit** `9172920`, `fix(replication): the match plays and bodies stand on the ground`, **PR
  #222**, 2026-08-28.
- **Files touched in `Ironfront.Net.Protocol/`:** `ProtocolConstants.cs`
  (`PROTOCOL_VERSION 3 → 4`), `Quantize.cs`, and `plans/00-shared/protocol-spec.md`. Nothing else.
- **The wire change:** `Quantize.POS_MIN` `−2048f → −1024f` and `POS_MAX` `2048f → 3072f`.
  `POS_RANGE` stays **4096** — the diff's own trailing comment says `// 4096, unchanged` — so the
  resolution stays 6.25 cm and the encoded size stays 6 bytes. **The window moved; it did not
  widen.**
- **Why:** § 4.4's existing note (`protocol-spec.md:420-429`) already carries the reasoning and is
  the source to cite — Dustbowl's authored play volume is `(650, −50, 620) .. (2350, 650, 2220)`,
  so 302 m of x and 172 m of z were unrepresentable, *including the Oasis capture point at
  `x = 2085.6`, team 0's opening base*; everything out there encoded to exactly `2048.00` on every
  client. Ledger **X-53**.
- **Wire change? YES.** The commit message states the consequence in its own words: *"a v3 client
  cannot talk to it, because the same i16 now decodes to a different metre."*

Write the row from that. **Do not restate § 4.4's note in the row** — cite it, the way the 2.0.1
row cites its new § 4.8.

**Two things this task must NOT do.** It must not invent a reason for anything the commit does not
evidence; if some part of the v4 change cannot be established from `git show 9172920` or the spec
body, **say so in the row** rather than filling the gap. And it must not renumber, reword or
re-date any existing row — this adds one missing row and changes nothing else.

Close **X-79** in `plans/debt-ledger.md` in the same PR, and re-run
`python tools/recount_debt_ledger.py --check` so the roll-up stays honest.

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
| 3 | **Two-client artifact: a TEAM-KILL moves the OPPOSING side's score up by one, and a non-lethal hit moves neither side.** Graded on the captured numbers, not on the damage log | lane-B record or screenshot pair, with the shot and the score before/after |
| 4 | A hex-sample test pins the 10 bytes of `S_MATCH_STATE` v5; `SpecChecker` green; § 15 has a 5.0.0 row; `PROTOCOL_VERSION` reads 5 in the fenced block **and** the prose header | `tools/ci.ps1` + the diff |
| 4a | **§ 15 has a 4.0.0 row as well as a 5.0.0 row.** The 4.0.0 row names commit `9172920` / PR #222, the `POS_MIN`/`POS_MAX` move with `POS_RANGE` unchanged, and "Wire change? Yes"; anything not evidenced by that commit is stated as unestablished rather than filled in. Ledger **X-79** closed and `recount_debt_ledger.py --check` passes | diff + the recount |
| 5 | **Two-client run: both clients' score numbers ascend, agree with each other, and agree with the server's log.** A kill moves the killer's side up, not the victim's side down | lane-B record + screenshot |
| 6 | **Screenshot: the score bar moves with the server's numbers**, and the bar's position matches the margin the numbers imply | screenshot, both branches of `:317` if reachable in one run |
| 7 | A match reaches `Ended` by the margin rule, and `WinningTeam` names the side that was ahead by `VictoryPoints` — verified against the server's own end decision | lane-B record |
| 8 | An old (v4) client is refused with `CONNECT_DENIED` code 2 rather than joining and rendering backwards | one deliberate mismatched-version run |
| 9 | `tools/ci.ps1` green | CI |

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| A second scoring call added in the damage path, so a multi-hit kill scores twice | 3 | 4 | 12 | Step 3.2 invariant 2; the `IsAlive` flip already makes the edge single-fire, and criterion 3's non-lethal half would catch a damage-path call |
| Bleed deleted, stalemates never resolve, and nobody notices until a long run | 3 | 4 | 12 | Step 3.3 edit 3 forces the decision to be recorded; criterion 7 needs a match that actually ends |
| Version bumped in the fenced block but not the prose header — SpecChecker stays green | 3 | 3 | 9 | Condition 4 is explicitly by-eye; it has drifted on this project before (`protocol-spec.md:1412`) |
| `ScoreUi` conflict with P12 | 3 | 2 | 6 | P11 owns `SetAuthoritativeState`; P12 owns `Awake`/`UpdateUi`. Land P11 first |
| The offline path regresses while nobody is playing offline | 2 | 4 | 8 | Criterion 2 — pre-existing tests pass **unchanged**, not adjusted |
| Plugin DLLs not rebuilt; Editor compiles against the old struct | 2 | 4 | 8 | Step 3.6, standing rule 5 |
| The back-filled 4.0.0 row invents content the commit does not evidence | 2 | 4 | 8 | 3.4a quotes the diff and the commit message, and requires "unestablished" in writing over a guess |

Nothing scores ≥ 15. The risk that used to sit at 20 — a zero-flag multiplier pinning both scores
at 0 — was **withdrawn on 2026-09-01 as a phantom**: both shipped maps open one point per side and
`MatchController.cs:199-205` already catches the case loudly (§ 1.1). A risk table that keeps a
hazard which cannot occur teaches the next reader to discount the ones that can.

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
- **Renumbering, rewording or re-dating any existing § 15 row.** Task 3.4a adds the one missing
  4.0.0 row and touches nothing else in that table.
