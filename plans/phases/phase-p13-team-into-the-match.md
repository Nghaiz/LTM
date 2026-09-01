# Phase P13 — the team the lobby chose, carried into the match

- **Plan:** [`../plan.md`](../plan.md) · **Block:** A · **Size:** M · **Effort:** 1 session
- **Depends on:** **P11 landed** (the protocol version this rides on — read
  [contracts § 3.2](../00-shared/team-multiplayer-contracts.md#32-after-p13--shrink-the-name-do-not-grow-the-ticket)
  on whether you are bumping 4→5 or 5→6). Independent of P12.
- **Contracts:** [`../00-shared/team-multiplayer-contracts.md`](../00-shared/team-multiplayer-contracts.md)
  §§ 3, 4 — the join-ticket layout and the team value space. **Read § 3 before step 1.**
- **Filed:** 2026-09-01, from the brainstorm's block A and the server audit's ranked finding #2.

---

## 1. The lobby balances teams and then throws the answer away

`RoomMember.Team` exists (`Ironfront.MasterServer/Lobby/LobbyService.cs:12`) and is auto-balanced
on join (`:157-162` — count both sides, take the smaller). It is broadcast to clients
(`MspMessageDispatcher.cs:423`).

Then the ticket does not carry it. `JoinTicket.cs:25-31` is `playerId, serverId, roomId,
expiresAt, displayName` — 32 payload bytes, **exactly full**, no team. So the game server
re-derives team from slot parity and the lobby's balancing never arrives:

```
ServerPlayerSlotPool.cs:118   var team = (byte)(i % 2);
ServerPlayerSlotPool.cs:131   body.Team = team;
ServerActorRegistry.cs:153-168  first unclaimed slot in registry order wins  ← team-blind
```

**A player's side is an accident of join order.** Scope of "no team on any client→server message":
searched `Ironfront_Reborn/Assets/Scripts`, `Ironfront.Net.Protocol`, `Ironfront.Net.Replication`,
`Ironfront.Net.Transport` for `teamrequest|requestteam|switchteam|chooseteam|jointeam|teamselect|
preferredteam|C_TEAM` — zero. `OnClientConnected` (`ServerTickLoop.cs:1513-1564`) reads only
`displayName` and `playerId` from `ConnectionInfo` (`:1527-1528`).

### 1.1 First-fit also strands both humans on one side

This is the server audit's ranked finding #2 and it is a live defect, not a hypothetical:

`Release()` frees a body **at its own index** (`ServerActorRegistry.cs:170-173` →
`NetServerActor.cs:567-572`), and `TryClaimPlayerSlot` refills the **lowest** free index. So
occupancy is a prefix only until somebody in the middle leaves.

> Three joiners take slots {0,1,2} = teams {0,1,0}. The slot-1 player disconnects. The live set is
> {0,2} = **two players, both on team 0, nobody on team 1.** Nothing corrects it.

Claiming **by team** fixes the arrival case and this case with the same code, which is why they
are one task and not two.

---

## 2. File ownership

```
Ironfront.Net.Protocol/Security/JoinTicket.cs                       layout + Issue + Verify
Ironfront.Net.Protocol/Enums/GspEnums.cs                            ConnectDenyReason
Ironfront.MasterServer/Lobby/LobbyService.cs                        RoomMember.Team settable
Ironfront.MasterServer/**                                            ticket issue call site
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerActorRegistry.cs   claim by team
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerTickLoop.cs        OnClientConnected only
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerPlayerSlotPool.cs
Ironfront.Net.Protocol.Tests/**                                      hex sample + truncation
Ironfront.MasterServer.Tests/**
plans/00-shared/protocol-spec.md                                     § 12 ticket, § 3.2 codes, § 15 row
Ironfront_Reborn/Assets/Plugins/Ironfront.Net.Protocol.dll           rebuilt artifact
```

**Not owned:** the lobby UI that lets a player pick a side (**P16**); `GsMatchStarted` and the
real `roomId` (**P14**); anything under `Net/Client/`.

---

## 3. Tasks

### 3.1 — Carry `team` in the ticket (M)

Implement
[contracts § 3.2](../00-shared/team-multiplayer-contracts.md#32-after-p13--shrink-the-name-do-not-grow-the-ticket)
exactly: `displayName` 16 → 15, one `u8 team` inserted **before** the name.
`Size` stays 64, `SignedPayloadSize` stays 32, the HMAC still covers exactly the first 32 bytes.

The field order is not cosmetic — the name stays the trailing run so a truncation bug loses a name
character rather than the team byte.

**Three tests, and the second is the one that will actually catch something:**

1. Round-trip: issue with team 1, verify, read team 1 back.
2. **Truncation.** 15 bytes of UTF-8 is 15 ASCII characters or fewer non-ASCII ones. A multi-byte
   character straddling the 15th byte must be dropped **whole** or the name renders as a
   replacement glyph. Test a 16-character ASCII name and a name whose 15th byte is mid-sequence.
3. Tamper: flip the team byte, verify fails `BadSignature`. The byte is inside the signed payload
   and this proves it.

Then the freeze gate (`protocol-spec.md` § 15, four conditions). **Check first whether P11 has
already shipped**: if it has not, this rides inside the same `5.0.0` row exactly as the spec's own
`3.0.0 (amended)` row did for the vehicle-and-projectile track; if it has, this is `6.0.0`. Say
which in the changelog row.

### 3.2 — Claim by team, not by index (M)

`TryClaimPlayerSlot(out NetServerActor actor)` becomes `TryClaimPlayerSlot(byte team, out
NetServerActor actor)`: walk for an unclaimed body **whose `Team` matches**, and return false if
the side is full.

Then `OnClientConnected` (`ServerTickLoop.cs:1513-1564`) reads the ticket's team — it already has
the verified ticket, since it verifies it to accept the connection at all — and passes it in.

**What must NOT change:**

- The pool's `i % 2` alternation (`ServerPlayerSlotPool.cs:118`). It is what guarantees an equal
  number of bodies per side; claiming by team is what makes that guarantee reach players.
- `Release()`'s in-place free. Releasing to the same index is correct once claims are team-keyed —
  the body returning to the pool is the right team's body.
- The `AvailableForPlayers` / `IsClaimed` predicates.

**One consequence to decide and record:** with `MaxConnections` 16 the pool holds 8 bodies per
side, so a side is full at 8 even if the other side is empty. That is the intended behaviour of a
team-keyed claim, and it is what makes step 3.3 necessary rather than theoretical.

### 3.3 — Refuse a full side with a reason the UI can show (S)

Today a failed claim has one outcome and one code: `ConnectDenyReason.ServerFull = 1`
(`GspEnums.cs:67`, spec § 3.2 table). "Your side is full, the other one is not" and "the server is
full" are different facts and must not render as the same sentence — the first has a remedy the
player can act on.

Add `ConnectDenyReason.TeamFull = 7` and the matching row to the spec's § 3.2 code table.

**This is not a wire change and does not bump `PROTOCOL_VERSION` on its own.** The reason field is
already a `u8`; adding a value moves no byte. The spec makes the same call for weapon ids
(`protocol-spec.md:716`): *"Adding an id is not a wire change."* It still needs a § 15 changelog
row with "Wire change? **No**".

**An old client reading code 7 does not know it.** It must degrade to a generic refusal message
rather than to silence or to a wrong one — the same defensive shape § 4.9 already specifies for an
unknown id. Write that fallback now; P16 renders the specific message when it builds the lobby.

### 3.4 — Make `RoomMember.Team` settable (S)

`LobbyService.cs:12` declares `public byte Team { get; init; }` — **init-only**. `Ready` beside it
at `:13` is `{ get; set; }`. A player switching sides in the lobby cannot happen through an
init-only property.

Change it to `{ get; set; }` and leave the auto-balance at `:157-162` as the **default**, not the
only writer. The lobby UI and the switch message are P16's; this phase supplies the field they
need and no more.

**Do not add the switch endpoint here.** A settable field with no caller is a small, honest gap;
a half-built endpoint with no UI is the `Register`/`RoomCreate`/`Chat` situation the brainstorm
already recorded — implemented server-side with **zero Unity callers**.

### 3.5 — Rebuild the plugin DLL (S)

`tools/build-libs.ps1`. `Ironfront.Net.Protocol.dll` changed; Unity reads the DLL, not the source
(standing rule 5).

---

## 4. Acceptance

| # | Criterion | Evidence |
|---|---|---|
| 1 | A ticket issued for team 1 verifies as team 1; a tampered team byte fails `BadSignature`; `Size` is still 64 and `SignedPayloadSize` still 32 | tests |
| 2 | A 16-character name and a name with a multi-byte character on the 15th byte both round-trip without a replacement glyph | test, with the two names named in the report |
| 3 | Hex-sample test pins the 64 ticket bytes; `SpecChecker` green; § 12 and § 3.2 updated; a § 15 row states whether the version moved and why | `tools/ci.ps1` + diff |
| 4 | **Two clients, each assigned a different team by the master, land on that team in the match** — verified against the server's own log, not against what the client believes | lane-B record + server log |
| 5 | **The three-join, middle-disconnect sequence from § 1.1 leaves one human per side**, not two on team 0 | scripted run, the sequence stated |
| 6 | **A join onto a full side is refused with `TeamFull`, and the client shows a message distinguishable from "server full"** | screenshot of the refusal |
| 7 | An old client receiving code 7 shows a generic refusal, not silence and not the wrong reason | one deliberate old-client run |
| 8 | `tools/ci.ps1` green | CI |

Criterion 4 is graded against the **server's** log on purpose: after P12 the client can display
its team, and a client that displays the team it was told is not evidence that the team it was
told is the team it has.

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Name truncation lands mid-character and ships a replacement glyph in every killfeed line | 4 | 3 | 12 | Step 3.1 test 2, named with two concrete inputs |
| Team-keyed claim makes a side full at 8 of 16 and reads to the player as a broken server | 3 | 4 | 12 | Step 3.3's distinct `TeamFull` code and criterion 6's screenshot |
| P11 not yet shipped and the version arithmetic goes wrong in the changelog | 3 | 3 | 9 | Step 3.1 forces the check before writing the row |
| Team byte outside the signed payload — forgeable side selection | 2 | 5 | 10 | Layout puts it in the first 32 bytes; test 3 proves the HMAC covers it |
| `Release()` returns a body to the wrong side's free set | 2 | 4 | 8 | Bodies are team-fixed at fill; release is in-place and unchanged. Criterion 5 exercises it |
| A settable `RoomMember.Team` with no caller looks like an unfinished feature | 2 | 1 | 2 | Step 3.4 says so out loud; P16 is the caller |

---

## 6. Out of scope

- **Choosing a team.** The player picks a side in the lobby room — **P16**. This phase carries
  whatever the master decided.
- **Mid-match team switching.** Owner decision: team locks when the match starts; switching means
  leaving the room.
- **A `C_TEAM_REQUEST` message.** No client→server team message exists and none is added: the
  choice is made over TCP to the master before the ticket is issued, which is why the ticket is
  the right carrier.
- **`GsMatchStarted` and the real `roomId`** — P14.
- **Re-balancing an already-running match.** Team-keyed claiming prevents the strand; it does not
  move a player who is already in.
