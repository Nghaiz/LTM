# Dev D — Phase 02 report: matchmaking, joinTickets, game server registry

**Date** 2026-08-14 · **Milestone** M2 · **Phase** [`phase-02-matchmaking.md`](../phases/phase-02-matchmaking.md)

---

## 1. What this phase actually was

The six objectives were implemented in #35, which landed the whole master server in one PR and
never wrote a phase report. So this phase did not start from an empty folder; it started from
working code with no evidence attached to it. The work was therefore: check every M2 criterion
against what is actually in the tree, close the gaps, and record the result — including the two
things that turned out to be wrong.

That reframing is the honest version. The alternative framing, "phase 02 was already done", is
what the absence of a report was quietly asserting, and it was not true: criterion 13 asks for
≥45 tests and the project had 42, five criteria had no test at all, and one behaviour did not
match its own phase document.

## 2. Criteria

| # | Criterion | State | Evidence |
|---|---|---|---|
| 1 | A game server registers and appears in the registry | Met | `TheRegistryHandsBackExactlyTheAddressItWasGiven`, `RegistryAuthenticatesOwnerAndReleasesAssignedRoomOnDisconnect` |
| 2 | `GS_REGISTER` with a wrong secret is rejected | Met | `RegistryAuthenticatesOwnerAndReleasesAssignedRoomOnDisconnect` (constant-time compare in `GameServerRegistry.TryRegister`) |
| 3 | 30 s without a heartbeat → server removed, room released | Met, **new** | `ThirtySecondsWithoutAHeartbeatRemovesTheServerAndReleasesItsRoom`, `AHeartbeatResetsTheReaperClock` |
| 4 | A valid joinTicket is accepted by the game server | Met (C) | `Ironfront.Net.Replication.Tests/TicketValidationTests` |
| 5 | A bad HMAC is rejected | Met | `Protocol.Tests/Conformance/JoinTicketTests.ATamperedSignatureIsRejectedAsDenyCode3` |
| 6 | An expired ticket is rejected | Met | `AnExpiredTicketIsRejectedAsDenyCode3`, `ATicketIsExpiredAtItsExactExpiryInstant` |
| 7 | A diacritic-bearing Vietnamese name does not corrupt | Met | `VietnameseDisplayNameIsTruncatedAtAUtf8CharacterBoundary` |
| 8 | No free servers → error 3000 | Met, **new** | `NoAllocatableServerYieldsNothingForEveryReasonAndTheErrorIsThreeThousand` |
| 9 | Matchmaking puts 2 people into the same room | Met, **new** | `MatchmakingPutsTwoQueuedPlayersIntoOneRoom`, `EnqueueJoinsAnOpenRoomOnTheSameMapImmediately` |
| 10 | Leaving the queue on disconnect | Met, **new** | `ADisconnectedPlayerLeavesTheQueueRatherThanBeingMatched` |
| 11 | Chat works, strips controls, rate-limits | Met | `ChatStripsControlsAndEnforcesWindowLimit` |
| 12 | Match results are written to the DB | Met, **new** | `MatchResultsAreWrittenToTheDatabase` |
| 13 | ≥45 tests green | Met | 59 in `Ironfront.MasterServer.Tests`, 745 across the solution |
| 14 | End-to-end: login → join → into a UDP match, on video | **Not met** | Needs A and C in the room; nothing in this phase can substitute for it |

13 of 14. Criterion 14 is the one that cannot be closed alone and is not claimed.

## 3. What was wrong

### 3.1 The 60-second relaxation did not relax anything

`MatchmakingService.Tick` grouped the queue by map id and, for anyone waiting past 60 s,
substituted map id `0` as their group key. The phase document's intent for that step is
"accept any map". The implementation did the opposite: it moved the relaxed player into a
*separate bucket*, so a player who had already waited a minute could match only against other
players who had also waited a minute. A relaxed player and a fresh map-5 player sat side by side
in the queue and never matched.

With two to sixteen players this is not a rare corner. It is the ordinary case for the second
person to join an empty queue, and it fails silently — no error, no log, the player simply waits.

Relaxed entries are now placed into whichever real group is closest to starting, longest waiter
first, and only form a group of their own when nobody is waiting on a specific map. Ties between
equally full groups break on the lower map id so a tick is reproducible.

Pinned by `APlayerPastSixtySecondsIsMatchedWithSomebodyWantingASpecificMap`, with
`TwoPlayersOnDifferentMapsDoNotMatchWhileBothAreStillPicky` on the other side so the fix cannot
degrade into "match everyone with everyone immediately".

### 3.2 An assumption about the database that was wrong, in our favour

Writing the criterion-12 test, the first version inserted results for bare player ids and threw:

```
SQLite Error 19: 'FOREIGN KEY constraint failed'
```

`match_results.player_id` references `accounts(player_id)`, and Microsoft.Data.Sqlite turns
foreign keys **on** — unlike the sqlite3 CLI, where they are off unless you ask. So a scoreboard
naming a player who does not exist is refused by storage, not merely by convention. That is a
better guarantee than assumed, and it is now recorded in
`AResultForAnUnknownPlayerIsRefusedByTheDatabase` rather than left as folklore.

It also means the `IsMember` filter in `HandleMatchEnded` is load-bearing beyond correctness: it
is what keeps a malformed scoreboard from raising `SqliteException` out of a dispatch path that
only catches `JsonException`. Removing that filter would turn a bad payload from a game server
into an unhandled exception. Not changed here — flagging it, because the safety of the current
code depends on a filter whose purpose reads as "skip non-members", not "prevent a crash".

## 4. What was added, and what was left alone

Added:

- `SqliteDatabase.FindMatchResults(roomId)` + `MatchResultRecord`. Criterion 12 says "inspect the
  DB", which meant a human with a SQLite browser. It is now a query the test suite can make.
- `Phase02MatchmakingTests` — 14 tests over the reaper, allocation refusal, matchmaking, queue
  cleanup, ownership of a room, and result storage.

Deliberately not added:

- joinTicket tests in this project. `Issue` and `Verify` live together in
  `Ironfront.Net.Protocol` precisely so both sides share one implementation; their tests live
  there too. A second copy under the master server would be a second thing to drift.
- A dispatcher-level integration test for `HandleMatchEnded`. It needs a connected, registered,
  room-holding game server, which is criterion 14's territory and belongs in the end-to-end run
  rather than in a unit test that would mostly assert the mock.

## 5. Verification

- `dotnet test Ironfront.sln -c Release` — **745 passed, 0 failed** (59 master server)
- `dotnet build -c Release` — 0 warnings under `TreatWarningsAsErrors`

## 6. Handoff

- **To C:** `JoinTicket.Issue` / `JoinTicket.Verify` are unchanged this phase; the shared-secret
  contract from #35 still holds. `GameServerRegistry.OwnsRoom` is what gates `GS_MATCH_ENDED`, so
  a game server must post results on the same TCP connection it registered on.
- **To A:** the error-code table is unchanged — 3000 `NoGameServerAvailable`, 3001
  `GameServerNotResponding`, both already matching [`protocol-spec.md § 13`](../../00-shared/protocol-spec.md#13-shared-error-codes).
- **Open:** criterion 14. Needs A's client, C's game server and this master server up at once.

## 7. Next

Phase 03 — operations: metrics, structured logging, backup, and the load test at 16 players.
