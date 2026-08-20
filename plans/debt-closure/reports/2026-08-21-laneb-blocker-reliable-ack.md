# The reliable burst nobody acks — why every lane-B client is dropped at join

- **Found by:** phase 3D lane B, first three-client run against a real headless server
- **Status:** blocker for every check in [`phase-3-harness.md`](../phases/phase-3-harness.md) § 2 lane B
- **Evidence:** [`lane-b/2026-08-21-smoke-06-server-transport.txt`](lane-b/2026-08-21-smoke-06-server-transport.txt),
  [`lane-b/2026-08-21-smoke-06-driver-transport.txt`](lane-b/2026-08-21-smoke-06-driver-transport.txt),
  [`lane-b/2026-08-21-smoke-06-run.json`](lane-b/2026-08-21-smoke-06-run.json)

---

## 1. What happens

Every rendered client joins the server successfully — it is admitted, it is told its actor id,
and it applies a snapshot — and is then dropped with `TransportError` about a second later. At the
first checkpoint (t = 2.10 s) the client already reads `connectionId: 0`, `localActorId: 0`,
`rttMs: 0`, and its body is falling through an empty world.

Server side:

```
[net] conn 1 joined as actor 41 (127.0.0.1:57774)
[transport] reliable sequence 0 abandoned after 10 resends
[transport] reliable sequence 2 abandoned after 10 resends
[transport] connection 1: a reliable packet was abandoned; the ordered channel cannot recover, disconnecting
[net] conn 1 left (TransportError)
```

All three clients, every run. The server then re-issues actor 41 to the next one, because by the
time it arrives nobody is left holding it.

## 2. What is established, and what is not

**Established.** The budget the server gives its opening reliable burst is small and fixed:

| Constant | Value | Where |
|---|---|---|
| `ReliabilityLayer.MinRtoMs` | **30 ms** | `Ironfront.Net.Transport/ReliabilityLayer.cs:19` |
| `ReliabilityLayer.MaxResends` | **10** | `Ironfront.Net.Transport/ReliabilityLayer.cs:18` |

With no RTT sample yet the retransmission timeout sits at its floor, so ten resends is **300 ms**
from join to abandonment. That is the whole window in which the server must see an ack.

Also established, and it is stronger than the first look suggested: **the client acknowledges
nothing, for the whole life of the connection.** Connections 1 and 3 abandon sequences 0 and 2 and
die there, because failing sequence 0 ends the connection before anything else is sent. Connection
2 in the same run lived slightly longer and abandoned **0, 2, and then 3 through 58 consecutively**
— every reliable packet the server sent it, without exception, while unreliable snapshots flowed
and the client applied them.

Sequence **1** is not the exception it appeared to be in the first two runs: it is simply not
reliable, and the abandonment logic only tracks reliable packets. The mundane reading is the right
one, and the "why does 1 survive?" clue this note originally built on is retracted.

**NOT established — and a first draft of this note asserted it wrongly.** The obvious explanation
is "an ack can only ride an outgoing packet, and the client's only guaranteed packet is the 1000 ms
keep-alive, so the 300 ms budget expires first." **That explanation is false**, and the code says so
in as many words: `Connection.Receive` sends a prompt keep-alive on *every* reliable receipt —

```csharp
// There is no standalone ACK datagram in GSP. A prompt keep-alive carries the
// freshly updated ack window so a quiet receiver does not make the sender give up
// before the next one-second idle keep-alive.
if (header.IsReliable) { ... SendPacket(PacketType.Keepalive, ...); }
```

— which is exactly the mechanism the false explanation says is missing, written for exactly this
failure. So the client *does* answer each reliable packet immediately, and the server still gives
up. The cause is therefore one of: the ack-keep-alive is not sent, is not delivered, is delivered
and rejected before it reaches the connection (`UdpTransportServer.ReceivePacket` drops a packet
whose `header.ConnectionId` does not match, counting it in `PacketsWithBadConnectionId`), or is
applied and does not cover sequences 0 and 2.

**Naming which one requires the transport's own counters, which no run has yet printed.** They
exist (`PacketsWithBadConnectionId`, `PacketsFromUnknown`, `ReliablePacketsResent`,
`ReliablePacketsRetried`); nothing reads them. That is the next measurement, and it is a
measurement rather than another hypothesis.

## 3. Why it took three runs to see

`NetLog.Warning` had **no subscriber anywhere in the shipped project**. The two lines that name
this cause — `reliable sequence N abandoned after M resends` and `reliable sequence slot collision
at N` — were formatted and handed to a null delegate on every run since the transport was written.
`Connection.Update`'s own comment says it ends the connection *"loudly instead of continuing
quietly"*; the loud half reached nobody, and a dropped client presented as a bare reason code with
no cause.

The harness now attaches the sink for its own processes (`LaneBHarness.AttachTransportLog`), which
is what produced § 1's excerpt. **The shipped-side gap is a separate defect** — a production client
or dedicated server still discards every transport warning it raises.

## 4. What this is not

- **Not the `.env` / port-collision failure** closed on 2026-08-21 (`6f2747e`). That one had every
  client binding 27015 behind the server and taking a `SocketException`; this run's clients open no
  socket, take no `SocketException`, and are dropped by the *server's* abandonment, not their own.
- **Not caused by the phase-3D harness changes.** A player built with
  `Assets/Scripts/Net/Diagnostics/` reverted to `6f2747e` — the exact state that produced the clean
  00:26 smoke — reproduces it on all three clients:
  [`lane-b/2026-08-21-ab-pre-change-client.txt`](lane-b/2026-08-21-ab-pre-change-client.txt).
- **Not new, and not reliably absent before.** The 00:26 smoke recorded zero disconnects and is
  not wrong about that; the same binary two hours later drops every client. So this is
  intermittent by nature and is currently reproducing 100% of the time — which makes now the
  cheapest it will ever be to diagnose.
- **Not visible to any gate that existed before this run.** Exit code, checkpoint count, both seeds
  and the player id all read clean on a run where nobody was connected;
  `artifacts/lane-b/combat-02/run.json` says `"passed": true` with `"failures": []`. The runner now
  grades `lostConnection`, which is the only row that can see it.

## 4a. The measurement, taken — one candidate eliminated

Evidence: [`lane-b/2026-08-21-transport-counters.txt`](lane-b/2026-08-21-transport-counters.txt).
The server prints its own packet counters once a second:

```
t=21s conns=0 fromUnknown=0   badConnId=0 rateLimited=0 playerIdRejects=0
t=22s conns=1 fromUnknown=0   badConnId=0 ...      <- client connected
t=23s conns=0 fromUnknown=0   badConnId=0 ...      <- already dropped
t=24s conns=0 fromUnknown=679 badConnId=0 ...      <- it is still talking to a server that forgot it
```

**`PacketsWithBadConnectionId` never moves.** So the acks are not arriving and being rejected on a
connection-id mismatch — `UdpTransportServer.ReceivePacket`'s reject path is never taken, and that
candidate is dead. `PacketsFromUnknown` stays at 0 for the whole life of the connection and only
explodes *after* the server has removed it, which is the client continuing to send into a hole.

What survives: either the client's outgoing packets do not reach the server during the live window
at all, or they reach it and carry an ack that covers nothing. Both are client-send-path questions,
and neither is answerable from the server's counters alone — the next measurement is the client's
`ReliablePacketsSent` / `AckCursor` against the server's, which needs a handle the client bootstrap
does not currently expose.

## 5. Next steps, before any fix

Per [`phase-3-harness.md`](../phases/phase-3-harness.md) § 7 a defect found by the harness is filed
and fixed in its own commit, never patched inside the harness — and per § 2 above, the cause is not
yet named, so there is nothing to fix yet. The order is:

1. **Done** — see § 4a. The connection-id rejection candidate is eliminated; the question is now
   entirely on the client's send path.
2. **Read the client's counters against the server's.** `Connection` exposes `AckCursor`,
   `HasSeededAckCursor` and the reliability statistics; `NetClientBootstrap` exposes none of them.
   The one number that settles it is whether the client's `AckCursor` ever advances past 0 —
   i.e. whether `_hasReceived` is true on the side that is receiving.
3. Only then, the fix — with a test that pins the **failure**, not the success: a connection whose
   peer answers exactly as the shipped receive path answers must not be abandoned. Note that
   `Ironfront.Net.Transport.Tests` is 85 tests green, so whatever this is, it is not covered by
   them; the reproduction belongs there before the fix does.

Two candidate directions worth arguing once the measurement is in, neither adopted here: a floor on
the initial RTO until the first RTT sample exists (30 ms is a loopback number no real link beats,
and 300 ms is the whole budget it buys), and a time-based abandonment rather than a count-based one
so the budget is stated in the units the failure is measured in.
