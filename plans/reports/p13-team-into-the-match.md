# P13 — the team the lobby chose, carried into the match

**Phase:** [`../phases/phase-p13-team-into-the-match.md`](../phases/phase-p13-team-into-the-match.md) ·
**Branch:** `feat/p13-team-into-the-match` · **Date:** 2026-09-02

---

## 1. What was wrong

The master server's lobby balanced teams on every join — count both sides, take the smaller —
and then had nowhere to put the answer. The join ticket's 32 signed bytes were **exactly full**,
so the game server re-derived a side from slot parity (`i % 2` over the pool, first-fit over the
registry). A player's team was an accident of join order, and `RoomMember.Team` was computed and
discarded on every single join.

## 2. What shipped

**`PROTOCOL_VERSION` 5 → 6.** `displayName` 16 → 15 bytes; a `u8 team` takes the freed byte at
offset 16, ahead of the name. `Size` stays 64, `SignedPayloadSize` stays 32, the HMAC still
covers exactly the first 32 bytes.

The field order is load-bearing twice over. The name stays the **trailing** run, so a truncation
bug costs a name character rather than a side. The team is **inside** the signed span, so a
player cannot pick their team by editing one byte of their own ticket.

**The version arithmetic, checked rather than assumed.** Contracts § 3.2 requires knowing whether
P11 shipped first. It had: `PROTOCOL_VERSION` was already 5, the spec header read 5.0.0, and the
changelog carried a 5.0.0 row for P11 (commit `91492d7`, #241). So this is **5 → 6 on its own**,
not an amendment to the v5 row, and the changelog row says so.

**Claim by team.** `TryClaimPlayerSlot(byte team, out …)` walks for an unclaimed body on that
side. This closes the arrival case and the mid-set-departure strand with the same code, which is
why they were one task.

**Two facts, two answers.** A side can be full at 8 of 16, so `ConnectDenyReason.TeamFull = 7`
was added — and, because the slot claim happens *after* the handshake,
`DisconnectReason.TeamFull = 9` alongside it. An unrecognised deny code now maps to
`DisconnectReason.Refused` instead of `InvalidTicket`: an unknown reason rather than a wrong one.

**`RoomMember.Team` is settable.** P16 is the caller; there is deliberately no switch endpoint.

## 3. Three things the phase said that turned out to be wrong

Recorded because each one would have produced confidently broken work.

**The tick loop does not have the ticket.** § 3.2 says `OnClientConnected` "already has the
verified ticket, since it verifies it to accept the connection at all." It does not.
`ConnectionInfo` carries `PlayerId` and `DisplayName` and nothing else from the ticket; the
verification happens in the transport at handshake time and the tick loop sees only what that
parse kept. The team rides `ConnectionInfo` the same way ledger X-36 carried the display name —
which puts `Ironfront.Net.Transport` in scope although § 2 does not list it.

**Code 7 alone could never reach a player.** § 3.3 asks for `ConnectDenyReason.TeamFull` and the
spec row. But the failing claim calls `Transport.Disconnect(connectionId, DisconnectReason.…)`,
because the claim runs after the handshake completes. Without `DisconnectReason.TeamFull` the
criterion would have been a value in an enum and a screenshot nobody could take.

**§ 1.1's own example does not discriminate.** Simulating both strategies against the lobby's
real balancer — 2–8 joins × every departure slot × 0–3 further joins — found **zero**
single-departure sequences where first-fit and a team-keyed claim end with different live teams.
For `{0,1,2}` with slot 1 leaving, **both** leave two players on team 0 and **both** give the
fourth joiner a team-1 body. The 2v0 state is reached under the fix too, and the only remedy —
moving a player already in the match — is what § 6 puts out of scope.

Escalated rather than fudged. On the owner's decision, criterion 5 and § 1.1 were reworded to the
**two-departure** sequence that genuinely reds on the old code: three join `{0,1,0}`, the first
two leave, the fourth joins onto the empty side. First-fit hands them the lowest free index — a
team 0 body — and both humans end up on one side.

## 4. Acceptance

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | Team round-trips; tampered byte fails `BadSignature`; sizes unchanged | **MET** | Round-trip at 0 and 1; tamper at offset 16 → `BadSignature`; `4+2+2+8+1+15 == SignedPayloadSize` asserted |
| 2 | A 16-char name and a mid-sequence name both round-trip without a glyph | **MET** | `"abcdefghijklmnop"` → `"abcdefghijklmno"`; `"abcdefghijklmnư"` → `"abcdefghijklmn"` (2-byte); `"abcdefghijklmệ"` → `"abcdefghijklm"` (3-byte) |
| 3 | Hex sample pins 64 bytes; SpecChecker green; §§ 12, 3.2, 15 updated | **MET** | `JoinTicket_IssuesTheExpectedSixtyFourBytes`; SpecChecker OK, 90 constants |
| 4 | Two clients on different teams land on that team, per the **server's** log | **MET** | lane-B: `conn 1 … team 0 (ticket) -> actor 41 team 0 (body)`, `conn 2 … team 1 -> actor 42 team 1`, `conn 3 … team 0 -> actor 43 team 0` |
| 5 | The § 1.1 sequence leaves one human per side (reworded) | **MET** | `ADepartureInTheMiddle_DoesNotStrandTheNextJoinerOnTheWrongSide`, mutation-red |
| 6 | A full side refused with `TeamFull`, distinguishable from "server full" | **MET in substance, no screenshot** | Two live runs — see below |
| 7 | An old client on code 7 shows a generic refusal | **MET** | Codes 8/9/255 → `Refused`; client text asserted; mutation-red |
| 8 | `tools/ci.ps1` green | **MET** | See § 6 |

### Criterion 4 — the server's own log

Graded on the server on purpose: a client that displays the team it was told is not evidence that
the team it was told is the team it has. Both numbers are printed, so the line distinguishes
"ticket 1, body 1" from "ticket 1, body 0".

### Criterion 6 — two controlled runs, same binary

| Run | Config | Server said | Client received |
|---|---|---|---|
| Team full | 4 slots (2 a side), clients on teams **0,0,0** | `conn 3 joined for team 0, which is full — the other side is not` | `disconnected: TeamFull` |
| Server full | 2 slots (1 a side), clients on teams **0,1,0** | *(no `conn 3` — refused by the transport at capacity)* | `disconnected: ServerFull` |

Two different facts producing two different answers, from the same client build. The first run
also shows the team-keyed walk directly: **`conn 2` took actor 43, skipping the free actor 42**,
because 42 is a team-1 body.

**The screenshot is not available**, and this is a real gap rather than a formality: a refused
lane-B client renders no frame before quitting, and `LobbyShellOverlay`, which draws
`MasterSession.LastError` in red, sits on the master-server shell flow rather than the standalone
path lane-B uses. The sentences themselves are pinned by
`AFullSIDEDoesNotReadLikeAFullSERVER`.

## 5. Mutation testing — five for five

A detector is unverified until the artifact is broken and it goes red.

| Mutation | Reds | Passes through |
|---|---|---|
| Swap `OffsetTeam` / `OffsetDisplayName` | 2 hex-sample tests | **288**, incl. every team round-trip |
| Delete the UTF-8 back-off loop | the 2 straddling-byte-15 cases | 288 |
| Drop `connection.Team = ticketTeam` | `ATicketsTeamReachesTheServersConnectionInfo(1)` | 102 |
| Remove `candidate.Team != team` | the strand test + the half-capacity test | 12 |
| Restore the old `_ => InvalidTicket` arm | the 3 unknown-code cases | 111 |

The first row is the point of freeze-gate condition 2: a round-trip test cannot see a layout
move, because both halves move together. The third is ledger X-36's trap one field over — the
parser stays perfect while the value is discarded a line later.

## 6. Verification

- **`dotnet test Ironfront.sln`** — 2131 passed, 0 failed, across 8 projects. Project count
  checked explicitly: `dotnet test` exits 0 when a project fails to *build*.
- **`tools/ci.ps1`** — see the run log at `artifacts/p13-ci.log`.
- **SpecChecker** — OK, 90 constants. Freeze-gate condition 4's prose header checked by eye.
- **Unity EditMode** — 109/111. The two failures are `SpawnPointSelectionTests` and are
  **pre-existing**: that test, `PinnedSpawnPointDirectory` and `ServerCombatBridge` are all
  byte-identical to `develop`, and the throw is Assembly-CSharp constructor validation with no
  dependency on any rebuilt DLL. They survive on `develop` because `ci.ps1` step 4 runs a Unity
  *compile* check, not the EditMode suite — a gap worth its own ticket.

## 7. Known gaps, stated rather than implied

- **`ConnectDenyReason.TeamFull = 7` has no sender.** The reachable refusal is
  `DisconnectReason.TeamFull`; code 7 is a documented spec value reserved for a handshake-stage
  team check that does not exist. Present, not wired.
- **`ServerTickLoop`'s `ServerFull` arm is defensive.** The pool is sized from `MaxConnections`,
  so the transport reaches capacity first — the contrast run's third client never reached
  `OnClientConnected`. Equally true before P13; recorded so the contrast is not misread as proof
  of that branch.
- **`S_PLAYER_LIST.MaxNameBytes` is still 16** while a ticket now delivers at most 15. Not a
  break — it is a bound, not a fixed width — but the two numbers now differ on purpose.
- **`IRONFRONT_CLIENT_TEAM` defaults to 0.** Any other runner that mints its own tickets and does
  not set it will put every client on one side. `run-lane-b.ps1` was given an explicit alternating
  roster and the load harness alternates by `clientIndex`.
- **Unity EditMode tests are not in CI.** Two of them have been red on `develop` for some time.

## 8. Files

Protocol: `JoinTicket.cs`, `GspEnums.cs`, `ProtocolConstants.cs` ·
Transport: `ITransport.cs`, `Connection.cs`, `UdpTransportServer.cs` ·
Replication: `TicketValidator.cs` (call site) ·
Master: `LobbyService.cs`, `MspMessageDispatcher.cs` ·
Config: `EnvRegistry.cs`, `GameClientConfig.cs`, `.env.example` ·
Harness: `JoinTicketSource.cs`, `tools/run-lane-b.ps1` ·
Unity: `ServerActorRegistry.cs`, `ServerTickLoop.cs`, `NetClientBootstrap.cs`, `Plugins/*.dll` ·
Spec: `protocol-spec.md` §§ 12, 3.2, 15 · Phase file: §§ 1.1 and criterion 5 amended.
