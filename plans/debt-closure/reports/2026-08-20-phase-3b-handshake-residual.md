# Phase 3B — the third client nobody counted

- **Plan:** [`phase-3b-handshake-residual.md`](../phases/phase-3b-handshake-residual.md)
- **Date:** 2026-08-20
- **Branch:** `fix/phase-3b-handshake-residual`
- **Issue:** #151

---

## 1. The gate, and why the phase ran anyway

The plan's *runs only if* clause says 3B runs only while `--smoke` is still red after 3A, and
otherwise closes as *not reproducible*. The smoke is **green**. But AC-1 forbids treating
`BadSignature` as gone by inference, and it is not gone: it still fires, once per Editor play
session, from a participant the original investigation never counted. So the phase closes on the
stronger of its two branches — **explained with evidence** — rather than on *not reproducible*.

## 2. The smoke, re-run with #154 merged

```
$ dotnet run --project Ironfront.Net.LoadHarness -c Release -- --smoke --port 27015

tickets     signed, from IRONFRONT_SHARED_SECRET (.env at D:\Coding\LTM\.env)
all 2 client(s) connected in 601 ms
ran 30.6s, 2/2 client(s) held to the end
snapshots applied  1200
decoded agreement  0 disagreement(s) over 4186 entity comparison(s) across 600 tick(s)
malformed/unknown  0/0
HARNESS_EXIT=0
```

Phase 3 acceptance criterion 1 — *`--smoke` connects, both processes exit 0* — is **MET**, first
time. 3A's `ServerPlayerSlotPool` is what turned it: the two clients that used to be admitted and
then thrown out for want of a player body now each get one.

**The Editor had to be moved to UDP to run this at all.** `Dustbowl.unity` ships
`_useLoopbackTransport` true and `.env` ships `IRONFRONT_GAMESERVER_TRANSPORT` deliberately blank,
so a default Editor play session binds no socket and the harness cannot reach it — the first probe
read `transport=LoopbackServer`. `IRONFRONT_GAMESERVER_TRANSPORT=udp` for the duration of the run,
reverted after. That is worth knowing before anyone re-runs this and reads a timeout as a defect.

## 3. `BadSignature`, accounted for

The Editor log for the run above, `[net]` lines only, in order:

```
[net] server up on UDP :27015, 16 connections
[net] 16 player slots ready
[net] join rejected: BadSignature          <-- once, before either harness client
[net] disconnected: InvalidTicket
[net] join admitted for player 1
[net] conn 1 joined as actor 41 (127.0.0.1:60753)
[net] join admitted for player 2
[net] conn 2 joined as actor 42 (127.0.0.1:60754)
```

Both harness clients were admitted. The rejection belongs to neither of them.

**It is the Editor's own local client.** An Editor play session runs `NetClientBootstrap`
alongside the server, and it joins with `PendingJoin.CreateUnsignedTicket()` — 64 zero bytes
(`PendingJoin.cs:78`, used at `NetClientBootstrap.cs:180`). Measured inside the live server
process, against the secret that server is actually holding:

```
unsignedTicket len=64 allZero=True
JoinTicket.Verify(unsigned) = BadSignature
JoinTicket.Verify(signed)   = Valid
server acceptUnsignedTickets=True
local NetClientBootstrap in play session = True
OnValidateTicket subscribers = 1
```

`JoinTicket.Verify` returns `BadSignature` from exactly one branch — the HMAC comparison at
`JoinTicket.cs:126` — so a zero ticket can produce nothing else. The client is told the generic
`InvalidTicket`, never the specific reason (`NetServerBootstrap.cs:373` logs it server-side and
deliberately withholds it, so the handshake is not an oracle), which is the
`[net] disconnected: InvalidTicket` that follows.

So the contradiction #151 was filed on was never a contradiction. `--smoke` runs two clients and
the Editor runs a third; three participants failing two different ways were read as one handshake
telling two stories.

## 4. The surviving hypothesis is dead, and its source was a comment

Plan § 4 expected an accumulating `OnValidateTicket` multicast list: `NetServerBootstrap` registers
a `serverId: 0` validator, `ServerMasterReporter` supposedly `+=` a stricter one, and
`UdpTransportServer.ValidateTicket` refuses if **any** subscriber refuses. A ticket minted for
`serverId: 0` would then pass one and fail the other.

Falsified three ways:

| | Evidence |
|---|---|
| Statically | `ServerMasterReporter.cs` has one `+=` in 135 lines, and it is `_controller.Match.MatchEnded` |
| Codebase-wide | `new TicketValidator` appears once outside the tests — `NetServerBootstrap.cs:358` |
| At runtime | `OnValidateTicket subscribers = 1`, read off the live transport mid-session |

The hypothesis came from a remark at `NetServerBootstrap.cs:353-357` asserting that
`ServerMasterReporter` re-registers a stricter validator once it has a server id. Nothing does. The
plan believed the comment, and the comment cost a phase — which is § 6's own argument about reports
that stay wrong, one file over. **Corrected in place**, with the reason recorded so it is not
re-derived: the walk really is refuse-if-any, so a second validator really would have been a
plausible source, and the next person to add a serverId-aware validator has to pick
replace-vs-accumulate deliberately.

Because the multicast list is **not** the cause, plan § 5.3 did not fire and the transport's
semantics are unchanged — so AC-4 is not owed. `ValidateTicket` also refuses on an empty list, so
the fail-closed default is intact.

## 5. A defect found on the way

`IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS=1` is **inert whenever a shared secret is set**.
`RegisterTicketValidator` only consults the flag on the branch where the secret is missing; with a
secret present it installs signed validation and never looks at the flag again. The operator asked
for unsigned tickets to be accepted, they are not, and nothing says so.

The visible cost is that an in-Editor local client can never join a local server that has a secret
configured, and the log blames a signature — which is exactly the trail that consumed this phase
and part of #152's. Out of § 7's file ownership to fix here. Tracked on #151, re-titled to it.

## 6. Acceptance criteria

| # | Criterion | Result |
|---|---|---|
| 1 | `BadSignature` explained with evidence, or recorded as not reproducible — never assumed gone | **Met, on the explained branch.** § 3: the local client's 64-zero ticket, measured against the live secret, with the one HMAC branch that can return it. |
| 2 | #151 carries the corrected root cause | **Met.** Comment posted with both halves — `ServerFull` = 3A's slot defect, `BadSignature` = the local client — and the issue re-titled to the residual `AcceptUnsignedTickets` defect, kept open as its tracker. |
| 3 | The proof report no longer contains the false premise | **Met.** `2026-08-20-phase-3-task-3.2-proof.txt` corrected in place: the two `ServerFull` senders replace *"sent from exactly one place"*, hypothesis 5 added with its measurements, and why 1-4 could not have reached the defect. The three demonstrated layers and the RED record are kept. |
| 4 | If `OnValidateTicket`'s semantics change, a pin covers them | **Not owed.** § 5.3 did not fire — the multicast list is not the cause (§ 4). No semantics changed, so there is nothing to pin. |

## 7. Scope of the negative

"`ServerMasterReporter` registers no validator" and "one subscriber at runtime" were established
over: `Ironfront_Reborn/Assets/Scripts/Net/**`, `Ironfront.Net.Transport/**`,
`Ironfront.Net.MasterLink/**` for `OnValidateTicket` / `RegisterTicketValidator` / `BadSignature`,
and a repo-wide `new TicketValidator` excluding `bin`/`obj`. The runtime count is one Editor play
session on `Dustbowl.unity` with `IRONFRONT_GAMESERVER_TRANSPORT=udp`; a session that had reached
`GS_REGISTER` with a live master was not exercised, because no master host is configured
(`[net] master link: standalone`).

## 8. Handoffs

**The local client cannot join a secured local server.** § 5. Until it is fixed, in-Editor manual
multiplayer against a secret-configured server is not possible, and the log misdescribes why.

**The smoke needs `IRONFRONT_GAMESERVER_TRANSPORT=udp`.** § 2. Whichever phase owns harness
ergonomics should either default it or fail loudly when the harness is pointed at a loopback
server, instead of timing out.

**Phase 3 tasks 3.3 and 3.4 are unblocked.** AC-1 was the gate; it is green.
