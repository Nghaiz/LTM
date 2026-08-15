# Step 06 — Master connection and the TCP → UDP junction

**Feeds** Dev A phase-03 tasks 2 and 3 · **Session size** large · **Editor needed after** none

> Goal: the client can log in, list rooms, join one, receive a joinTicket, and hand it to the UDP
> transport — the half of the online flow that is currently missing entirely.

---

## What exists and what does not

The server half is done and tested. `MspMessageDispatcher.cs:260` already answers a room join with
everything the client needs:

```csharp
new { ok = true, gameServerIp = server.PublicIp,
      gameServerPort = server.UdpPort,
      joinTicket = Convert.ToBase64String(ticket), ... }
```

The client half does not exist. `MasterClient` is referenced in `Assets/Scripts` only by *server*
components (`MasterLinkBootstrap`, `ServerMasterReporter`). `NetClientBootstrap` dials a host and port
from the inspector or the environment and sends an **empty** ticket.

Until this step lands, "connect to the VPS and play online" is not a thing the client can do, no
matter how complete the server is.

## Deliverable

1. **Login** — `IMasterClient.LoginAsync(user, hash)`, session token held, failure mapped to an error
   the UI can show. Hash client-side (`SHA256(password + username)`); never send plaintext, TLS or
   not. Phase-03 trap 2.
2. **Room list and join** — and on success, a `PendingJoin { Ip, Port, Ticket }`.
3. **The junction** — hand `PendingJoin` to the transport: `Connect(ip, port, ticket)`, with a
   connect timeout well under the ticket's 60-second validity. Phase-03 uses 10 s.
4. **The holding queue** — phase-03 trap 3. Scene loading takes 2–5 seconds and the server is already
   sending snapshots; processing them before the scene is ready is a flood of
   `NullReferenceException`. Buffer, then on ready process the newest and **discard the stale ones**,
   because a snapshot from four seconds ago describes a world that no longer exists.

## The threading question, settled by reading rather than by asking

Phase-03 trap 1 says to settle with Dev D in week 11 whether `IMasterClient` callbacks arrive on the
main thread. **They do not arrive anywhere until you ask for them.** The client is poll-driven:
`MasterLinkBootstrap.Update()` calls `_link.Poll()`, and everything — every event and every task
continuation — fires on the thread that made that call. `MasterClientPollTests` in
`Ironfront.MasterServer.Tests` is the executable statement of that contract.

So the `ConcurrentQueue` marshaller phase-03 keeps in reserve is not needed: call `Poll()` from
`Update()` and continuations are already on the main thread. One frame of latency on a lobby link,
which is the trade Dev D documented.

Do not build the marshaller "just in case" — `coding-guidelines.md` § 2.

## What this step proves, and what it does not

**Proves:** by test, with a fake `IMasterClient` — login success and failure paths, the join
response becoming a well-formed `PendingJoin`, the connect timeout firing, and the holding queue
keeping the newest while dropping the stale.

**Cannot prove:** an end-to-end login against a running master. That needs a master, a game server
and a client at once — phase-03 criterion 1's video, and the handoff item *"run the login → join flow
with D 10 times without an error"*.

**Dev A checks:** the ten-run handoff with Dev D, once step 07 gives it a face.

## Done when

- Login, room list, join, junction and holding queue exist with tests against a fake master
- No `ConcurrentQueue` marshaller, and a comment saying why
- The error codes the client surfaces are cross-checked against `protocol-spec.md` § 13
- Merged and green
