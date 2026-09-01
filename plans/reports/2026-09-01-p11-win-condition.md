# P11 — the win condition the netcode never had

- **Phase:** [`../phases/phase-p11-win-condition.md`](../phases/phase-p11-win-condition.md) ·
  **Contracts:** [`../00-shared/team-multiplayer-contracts.md`](../00-shared/team-multiplayer-contracts.md)
- **Branch:** `feat/p11-win-condition` · **Commit:** `df64df4`
- **Date:** 2026-09-01

---

## 1. What shipped

The networked match now plays the game the project has. `ConquestScoreRule` is the one copy of the
rule; `MatchScoreboard` and `MatchStateMachine` both call it and both keep their own state.
`S_MATCH_STATE` is v5 — ascending scores, an appended `victoryPoints`, and a `WinningTeam` that is
the same margin test the server ends the round with. The client draws the score bar from the
server's numbers for the first time.

Every task in § 3 landed. Two of them landed differently from the plan, and both are recorded
below rather than in a commit body nobody re-reads.

---

## 2. Two decisions the phase asked to be recorded

### 2.1 The bleed was DELETED — step 3.3 edit 3

The phase gave two options and named a default. The default was taken: `DrainTickets` is gone, and
the flag count reaches the score through `ConquestScoreRule.Award` instead.

**What that costs, stated plainly.** The bleed was the only pressure that made a stalemate resolve
on its own. Two evenly-matched sides that stop killing each other now play until somebody scores;
nothing in the machine will end that round for them except elimination.

**Why it is still right.** The offline match has no bleed, and matching the offline rule is the
entire purpose of this phase. A second pressure the offline game does not have would have re-opened
the divergence one mechanism over — the networked round would end at a moment the offline round
would not, which is the shape of the defect P11 exists to close. The reasoning is carried in
`MatchStateMachine.OwnedPointCount`'s remark, next to the code that replaced it, so a reader who
goes looking for the bleed finds the answer rather than a gap.

`MatchRules.BleedPerPointPerSecond` is deleted with it. `StartTickets` → `VictoryPoints` and
`TicketsPerDeath` → `PointsPerKill`; the other six knobs are untouched, as the phase required.

### 2.2 `ConquestScoreRule` is in `Ironfront.Net.Protocol`, not `Ironfront.Net.Replication.Match`

**This is a deviation from the plan, and the plan could not have been followed as written.**

Contracts § 1.3 places the rule in `Ironfront.Net.Replication/Match/`. Contracts § 2.2 requires
`MatchStateMessage.WinningTeam` to be `ConquestScoreRule.Decide(...)`. `MatchStateMessage` lives in
`Ironfront.Net.Protocol`, and Protocol does not reference Replication — Replication references
Protocol, and reversing that is not a thing that can happen. The two halves of the contract are
mutually exclusive at the assembly boundary.

Protocol wins, for three reasons:

1. `Decide` returns a `TeamId`, which is a protocol value **declared in that assembly**. A rule
   whose output type is a wire value is not out of place beside it.
2. The alternative is worse in exactly the way P11 exists to prevent: leaving the rule in
   Replication forces `WinningTeam` to grow its own copy of the margin test, and acceptance
   criterion 1 is that there is only one.
3. **No game-design number moved.** `MatchRules`' remark is right that warmup lengths and victory
   targets must not become protocol constants — and none did. `victoryPoints` and the per-kill
   award are **parameters**; `ConquestScoreRule` holds no values at all.

The file's own remarks carry this so the next reader does not re-litigate it. **The contract file
was not edited** — it is P13/P14/P17's shared input too, and rewriting a shared contract from
inside one phase is how those files go stale. It is flagged here instead.

### 2.3 Two files were edited that the ownership list omits

`Net/Server/MatchController.cs` and `Net/Shared/IObjectiveHud.cs`. Neither is discretionary:
`MatchController` constructs `MatchRules` and reads `Tickets0`/`Tickets1`, and `IObjectiveHud` is
the interface `ClientSceneBindings` implements and the presenter calls — the parameter cannot be
threaded through the two named files without passing through the one between them. Both edits are
mechanical and confined to the renames.

`MatchController._startTickets` is a `[SerializeField]` that **Dustbowl authors** (`Dustbowl.unity`
holds `_startTickets: 200`). It was renamed with `[FormerlySerializedAs("_startTickets")]` rather
than by editing the scene YAML, so the authored value survives and no scene asset is touched.

---

## 3. Acceptance

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | One implementation of the multiplier and the margin test | **PASS** | § 3.1 below |
| 2 | Pre-existing offline tests pass unchanged | **PASS**, with a scope note | § 3.2 |
| 3 | Two-client artifact: team-kill moves the OPPOSING score, non-lethal hit moves neither | **NOT RUN — owed** | § 4 |
| 4 | Hex sample, SpecChecker, § 15 5.0.0 row, version in both places | **PASS** | § 3.3 |
| 4a | § 15 has a 4.0.0 row; X-79 closed; recount passes | **PASS** | § 3.4 |
| 5 | Two-client run: both clients' scores ascend and agree | **NOT RUN — owed** | § 4 |
| 6 | Screenshot: the bar moves with the server's numbers | **NOT RUN — owed** | § 4 |
| 7 | A match reaches `Ended` by the margin rule | **NOT RUN — owed** | § 4 |
| 8 | A v4 client is refused with `CONNECT_DENIED` code 2 | **NOT RUN — owed** | § 4 |
| 9 | `tools/ci.ps1` green | **PASS** | § 3.5 |

### 3.1 Criterion 1 — one copy

```
grep -rn "ScoreMultiplier\|>= .*+ *VictoryPoints\|>= .*+ *victoryPoints" --include=*.cs .
```

Sixteen hits. **Three are the implementation**, all in `ConquestScoreRule.cs` — `:54` the
multiplier, `:80-81` the margin test. **Two are forwarders** — `MatchScoreboard.cs:174` →
`ConquestScoreRule`, `ScoreUi.cs:144` → `MatchScoreboard`. **Two are call sites**
(`MatchScoreboard.cs:103-104`). The remaining nine are doc-comments and gate strings.

No second `>= other + victoryPoints` survives anywhere. `MatchScoreboard.AddScore`'s two
comparisons are now one `Decide` call.

### 3.2 Criterion 2 — a scope note, because a bare PASS here would overstate

**There are no `MatchScoreboard` or `ScoreUi` unit tests to pass.** Searched
`Ironfront.Net.Replication.Tests/**`, `Ironfront.Net.Protocol.Tests/**`,
`Ironfront.Client.*.Tests/**` — the only test naming either type is
`ClientWiringGateTests.TheHudNeverRoutesTicketsThroughAddScore`, a source-invariant gate that
matches text rather than behaviour. It passes unchanged. Assembly-CSharp is not compiled by
`dotnet test`, so the offline scoreboard has no CI-reachable behavioural coverage and this
criterion could not have failed.

What actually guards the offline path here is narrower and worth naming: the extraction is a
forwarder plus one substitution whose equivalence is visible by inspection
(`a >= b + v || b >= a + v` against `Decide`), and the Unity compile is clean. **That is not the
same as a passing behavioural test, and it is not claimed to be.**

`MatchStateMachine`'s own tests DID change, correctly — they described the ticket rule, which is
the thing being removed. § 2.4 of the contracts lists the machine among what must change together.
Four ticket tests were replaced by margin/multiplier equivalents rather than deleted:

| Was | Is | Why |
|---|---|---|
| `ADeathCostsTheDyingTeamATicket` | `ADeathScoresForTheTeamOppositeTheVictim` | the direction reversed |
| `RunningOutOfTicketsEndsTheMatch…` | `LeadingByTheVictoryMarginEndsTheMatch…` | the end condition |
| `HoldingMorePointsBleedsTheOtherSide` | `HoldingMorePointsMakesEveryKillWorthMore` | the flag mechanism |
| `AnEvenSplitBleedsNobody` | `AnEvenSplitScoresBothSidesAtTheSameRate` | ditto |
| `ABleedTooSmallToChange…SendsNothing` | `AKillIsWorthAMessageAndAQuietTickIsNot` | the sub-integer case it guarded cannot arise once nothing accrues between ticks |
| `TicketsNeverGoNegative` | `NeitherSideEverLosesPoints` | the inverse property |

Six tests were **added** for invariants the ticket rule had no way to state:
`ATeamKillScoresForTheEnemyBecauseNothingReadsTheKiller`,
`ATeamHoldingNoPointsScoresNothingForItsKills`,
`ALevelScoreNeverEndsTheRoundHoweverHighItGets`, the v5 hex pair, and
`MatchState_TenBytesFromAV4SenderWouldDecodeBackwards`.

**Test-fixture note that would otherwise bite the next reader.** Every scoring test now needs a
machine with capture points. The multiplier is the identity function, so on a machine with none —
which is what `new MatchStateMachine(FastRules())` gives you — every kill is worth zero and no
round can ever end. That is § 1.1 property 3 working correctly, not a defect, and the
`OneBaseEach` helper reproduces what both shipped maps author. The phase-transition tests keep the
point-free machine, because they never score.

### 3.3 Criterion 4 — the wire gate, all four conditions

1. **SpecChecker green** — `[SpecChecker] OK — 90 constant(s) match`.
2. **Hex sample** — `MatchStateHex = "02 8A 00 2C 00 00 00 0C C8 00"`, written from the layout in
   declaration order, not captured from the implementation. Serialize and parse both pinned, plus
   `MatchState_IsTenBytes`.
3. **§ 15 has a 5.0.0 row**, with "Wire change?" answered and the reason for both halves.
4. **Checked by eye, as the gate requires.** `protocol-spec.md:3` (prose header) reads
   `**Version: 5.0.0** … Wire PROTOCOL_VERSION = 5`; `:50` (fenced block) reads
   `PROTOCOL_VERSION  = 5`; `ProtocolConstants.cs:16` agrees. All three.

**Four version pins went red and were advanced, not re-pinned.** `PlayerListVersionPinTests`,
`ChannelEnvelopeTests`, `ActorLifecycleMessageTests` and `VehicleRoutingTests` each assert
`PROTOCOL_VERSION == 4` beside the layout constants they actually guard. Direction checked first,
per `pinned-baseline-test-companion.md`: the version rose for a reason **outside** each test's own
subject, and every layout constant beside it is unmoved — which is the case the pins' own remarks
describe and the case the existing `3 -> 4 in X-53` annotation set the precedent for. Each now
reads `5` with the P11 reason appended to that annotation, so the trail stays readable. Had a
layout constant moved too, the correct response would have been the opposite one.

### 3.4 Criterion 4a — the back-filled 4.0.0 row

Every clause is quoted or measured, none inferred. Verified against `git show 9172920` directly:

- **Commit** `9172920`, PR **#222**, 2026-08-28 — confirmed from `git show`.
- **Files under `Ironfront.Net.Protocol/`:** exactly `ProtocolConstants.cs` and `Quantize.cs`.
  (`QuantizeTests.cs` and the plugin DLL are also in the commit, outside that directory.)
- **The wire change:** `POS_MIN` `-2048f → -1024f`, `POS_MAX` `2048f → 3072f`, with the diff's own
  trailing comment reading `// 4096, unchanged` on `POS_RANGE`.
- **Wire change? Yes** — quoted from the commit message.
- **Reason cited, not restated** — the row points at § 4.4's existing note and X-53, the way the
  2.0.1 row cites its own new section.

Nothing beyond that diff is claimed, and the row says so in writing. No existing row was
renumbered, reworded or re-dated.

**X-79 closed** in `plans/debt-ledger.md`; roll-up moved 16/19 → 15/20 open/closed and
`python tools/recount_debt_ledger.py --check` reports *"Roll-up in the file agrees with the
recount."*

### 3.5 Criterion 9 — CI

| Step | Result |
|---|---|
| 1. Build | PASS |
| 2. Test | **PASS — 2112 tests, 0 failures, 8 projects** |
| 3. Protocol constants match the spec | PASS (90 constants) |
| 3b–3f. meta / duplicate assemblies / harness / **layering** / diagnostics | PASS |
| style, analyzers (advisory) | PASS |
| 4. Unity compile check | **PASS — exit 0, zero `error CS`** |

Per-project test counts: Protocol 279, Replication 1394, Transport 100, MasterServer 88,
Client.Flow 98, Configuration 73, LoadHarness 41, Client.Input 39.

**The Unity step needed the Editor closed and is worth a note**, because its first run looked
exactly like a real failure: `Aborting batchmode due to fatal error`, exit 1, **zero compiler
output**. That is the project lock, not a broken compile — a second batchmode run cannot start
while the Editor holds the project. Closed the Editor via `.claude/scripts/unity-editor.ps1`, re-ran,
exit 0. And the pass is not vacuous: `Library/ScriptAssemblies/Assembly-CSharp.dll` has a
`LastWriteTime` of 22:16:15, inside the run, so the assembly genuinely recompiled rather than
being served warm.

Plugin DLLs rebuilt with `tools/build-libs.ps1` and committed — `Ironfront.Net.Protocol.dll` and
`Ironfront.Net.Replication.dll` both changed, as step 3.6 requires. Unity reads the DLLs, not the
source, so `ConquestScoreRule` would have been invisible to the Editor without it.

---

## 4. What is owed — five criteria that need two real clients

**Criteria 3, 5, 6, 7 and 8 are NOT satisfied by this PR and are not claimed to be.** Each needs a
two-client run or a deliberate version-mismatch run, and the phase itself says 3, 5 and 6 "cannot
be satisfied by a green suite". Stated as a list so nothing here reads as done:

- **3** — a team-kill moves the OPPOSING side's score up by one, and a non-lethal hit moves
  neither. The unit half is covered
  (`ATeamKillScoresForTheEnemyBecauseNothingReadsTheKiller`); the graded-on-captured-numbers half
  is not.
- **5** — both clients' scores ascend, agree with each other, and agree with the server's log.
- **6** — screenshot of the bar tracking the server's numbers, both branches of the geometry if
  one run reaches them.
- **7** — a match actually reaching `Ended` by the margin rule, with `WinningTeam` naming the side
  that was ahead by `VictoryPoints`.
- **8** — a v4 client refused with `CONNECT_DENIED` code 2 rather than joining and rendering
  backwards.

Two hazards to carry into that run, both from existing ledger rows rather than from this change:
the server has been observed **walking idle claimed bodies** (X-71 family), so only engagements
before the first death grade cleanly; and `aim.distanceM` in lane-B records is known to freeze, so
distances must be computed from both clients' `localActor` blocks.

---

## 5. Risks, re-graded after the work

| Risk | Phase score | After |
|---|---|---|
| A second scoring call in the damage path | 12 | **Not introduced.** The only call site is `ServerTickLoop.ReportDeathToMatch`, and its remark now states the direction and the do-not-add rule explicitly. Criterion 3's non-lethal half is still the real check |
| Bleed deleted, stalemates never resolve | 12 | **Live, and accepted.** § 2.1. Criterion 7 still needs a match that ends |
| Version bumped in the fenced block but not the header | 9 | **Closed.** Both, checked by eye, § 3.3 condition 4 |
| `ScoreUi` conflict with P12 | 6 | **Unchanged.** P11 touched `SetAuthoritativeState` and the doc-comments, plus a four-line substitution inside `UpdateUi` where its geometry moved into `ApplyScoreBars`. P12 rebases on that one hunk |
| The offline path regresses unnoticed | 8 | **Higher than the phase assumed.** The mitigation named was "pre-existing tests pass unchanged" — and there are none (§ 3.2). The real guards are inspection and the Unity compile |
| Plugin DLLs not rebuilt | 8 | **Closed.** § 3.5 |
| The 4.0.0 row invents content | 8 | **Closed.** Every clause verified against `git show 9172920`, § 3.4 |

**One risk the phase did not have.** The contract's two halves could not both be implemented
(§ 2.2). It cost a decision rather than a defect, and it is the kind of thing that is only visible
once the compiler is asked.

---

## 6. Out of scope, and honoured

No friendly-fire gate — `ServerActorDamageSink.ApplyDamage` is untouched and still ignores
`attackerId`, which is correct. `ScoreUi.Awake` untouched. No per-player scores.
`GameManager.victoryPoints` read, not relocated (ledger C-5 stands). No existing § 15 row
renumbered, reworded or re-dated.
