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

Also established: sequences **0 and 2** are abandoned and **1 is not**, on every connection, on
every run. Whatever this is, it is selective and repeatable rather than a general loss of acks.

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

## 5. Next measurement, before any fix

Per [`phase-3-harness.md`](../phases/phase-3-harness.md) § 7 a defect found by the harness is filed
and fixed in its own commit, never patched inside the harness — and per § 2 above, the cause is not
yet named, so there is nothing to fix yet. The order is:

1. **Print the transport counters** from the lane-B server and client once a second
   (`PacketsWithBadConnectionId`, `PacketsFromUnknown`, `ReliablePacketsResent`,
   `ReliablePacketsRetried`, and the connection's ack cursor). Diagnostics-owned, so it belongs to
   this phase; it distinguishes "the ack never arrived" from "the ack arrived and was rejected"
   from "the ack arrived and did not cover sequence 0".
2. **Explain why 1 survives while 0 and 2 do not.** Any theory that does not account for that is
   not the theory.
3. Only then, the fix — with a test that pins the **failure**, not the success: a connection whose
   peer answers exactly as the shipped receive path answers must not be abandoned.

Two candidate directions worth arguing once the measurement is in, neither adopted here: a floor on
the initial RTO until the first RTT sample exists (30 ms is a loopback number no real link beats,
and 300 ms is the whole budget it buys), and a time-based abandonment rather than a count-based one
so the budget is stated in the units the failure is measured in.
