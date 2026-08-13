# Warmup — the TCP framing problem

- **Author:** Dev D (Master Server & Services)
- **Phase:** [phases/phase-00-foundation.md](../phases/phase-00-foundation.md) · Task 1, acceptance criterion 1
- **Companion code:** `Ironfront.Net.Protocol/Msp/MspFrame.cs` (the reader), `Ironfront.MasterServer/Net/ClientConnection.cs` (the receive loop), `Ironfront.Net.Protocol.Tests/Conformance/MspFramingTests.cs` (12 framing tests), `Ironfront.MasterServer.Tests/Net/TcpListenerHostTests.cs` (7 connection tests)

> The task says a working reader already exists, and to do the experiment anyway — because
> *watching* the failure is what turns "TCP has no message boundaries" from something you were
> told into something you know. This is that experiment, with real numbers from a loopback run,
> and the conclusions that go into the defence.

---

## 1. What the experiment does

A single throwaway program (full source in the appendix) opens real TCP sockets over the loopback
interface and runs four parts:

- **A** — a client sends three MSP frames in three back-to-back `Send()` calls; the server does
  one `Receive()`.
- **B** — a client sends one 100 KB frame in a single `Send()`; the server counts how many
  `Receive()` calls it takes to collect it.
- **C** — 200 tiny request/reply round-trips, timed with Nagle on and off.
- **D** — 6 MB of large frames fed to `MspFrameReader` in 256-byte chunks, timed against the
  naive re-allocating parser that newcomers reach for.

The numbers below are from one representative run on this dev machine (Windows, .NET 8, loopback).
Loopback timings are noisy; C especially varies run to run, so it is reported as a range across
three runs.

---

## 2. Observation A — three `Send()`s arrive in one `Receive()`

```
client sent 3 frames = 40 bytes in 3 Send() calls
server's FIRST Receive() returned 40 bytes
  -> all three frames arrived glued in a single Receive().
naive parser reads length=9 from the prefix, then treats the
   remaining 36 bytes as one body — frames 2 and 3 are silently lost.
MspFrameReader on the same bytes: 3 frames parsed. Correct.
```

Three separate application-level `Send()` calls produced **one** `Receive()` of all 40 bytes. The
naive parser — the one that reads the length prefix once and assumes the receive *is* one message —
reads `length = 9` from the first frame, and every byte after that first frame is either
misinterpreted or dropped. Frames 2 and 3 vanish.

The reason it glues: with Nagle's algorithm on (the default), the client's three small writes are
coalesced into one segment, and the OS hands the server everything that had arrived when it called
`Receive()`. But **this is not a Nagle artifact you can switch off** — even with `NoDelay = true`,
the server is free to return however many bytes happen to be buffered when it reads. Gluing is
intrinsic to a byte stream.

## 3. Observation B — one `Send()` arrives in many `Receive()`s

```
client sends ONE frame of 100014 bytes in a single Send()
server needed 2 Receive() calls to collect 100014 bytes
chunk sizes ranged 34478..65536 bytes — no receive equalled the send
```

The mirror image: one large `Send()` came back as **two** `Receive()`s, neither of which equalled
the send. On loopback the chunks are large (up to 64 KB) because the loopback MTU is huge. **On a
real NIC the same 100 KB message arrives in ~1400-byte chunks** — one Ethernet MSS each — so a real
deployment would see ~70 receives, not 2. The count is an artifact of the medium; the *fact that it
splits at all* is the point, and it holds on every medium.

## 4. The conclusion — for the report and the defence

> **TCP guarantees byte ordering, not message boundaries.** `Send()` and `Receive()` do not
> correspond one-to-one: one send can become many receives (§3), and many sends can become one
> receive (§2).

This is the fundamental difference from UDP. A UDP `ReceiveFrom()` returns exactly one datagram,
whole — the boundary *is* the packet. TCP has no such boundary, so the application has to impose one.
MSP does it with a 4-byte big-endian length prefix (`protocol-spec.md § 10`): read the prefix, then
read exactly that many bytes, no matter how the reads fall. That is the entire job of
`MspFrameReader`, and it is why an **accumulating buffer** — not parsing directly on the socket
buffer — is mandatory: the leftover bytes of a split frame have to survive until the next receive.

The two traps this produces, and where they are handled in the shipped code:

- **Gluing → deadlock.** If the receive loop parsed only *one* frame per `Receive()` and returned,
  the second and third glued frames would sit in the buffer while the client blocks waiting for a
  reply that never comes. `ClientConnection.Ingest` drains in a `while` loop, not an `if`, for
  exactly this reason (phase-00 trap 1). The `ThreeMessagesGluedIntoOneSegment_ParseIntoThree` and
  `OneByteAtATime_StillParses` tests pin it.
- **A malicious length.** A peer sending `length = 0xFFFFFFFF` would make a naive reader try to
  allocate 4 GB. The reader checks the declared length against the 64 KB cap *before* allocating or
  waiting for any body (phase-00 trap 4). `ALengthOverSixtyFourKilobytes_FaultsTheConnection`
  pins it.

---

## 5. Nagle's algorithm — the `NoDelay` measurement

```
NoDelay=False: 200 round-trips in ~41–55 ms (~0.21–0.27 ms/round-trip)
NoDelay=True : 200 round-trips in ~16–37 ms (~0.08–0.18 ms/round-trip)
```

Across three runs, disabling Nagle roughly **halved** the round-trip latency of a tight
request/reply loop. The effect is real but its size is noisy on loopback.

The mechanism is worth stating precisely, because "NoDelay saves 200 ms" is the parroted version and
it is not quite right. Nagle's algorithm holds a small outbound segment until the previous one is
ACKed, to avoid flooding the network with tiny packets. On its own that is cheap. The latency comes
from its **interaction with delayed ACK**: the receiver delays its ACK (up to ~40–200 ms) hoping to
piggyback it on a reply, while the sender's Nagle waits for that very ACK before sending its next
small segment. The two wait on each other. A request/reply protocol with small messages — which is
exactly what a lobby is — is the pattern that triggers it.

**Decision for the master server:** `socket.NoDelay = true` on every accepted connection
(`TcpListenerHost.Admit`). A lobby is nothing but small messages that need a fast reply, so Nagle is
pure added latency here. The opposite would be true for a bulk file transfer, where coalescing small
writes into full segments is what you want — which is the real lesson: `NoDelay` is a trade, not an
upgrade.

---

## 6. Why write the reader by hand, and why not `System.IO.Pipelines`

`conventions.md § 3.4` names `MspFrameReader` as one of the two classes the team writes by hand
despite the BCL already solving the problem. Part D measures whether the hand-written version earns
its place, by comparing it to the trap it is easy to fall into:

```
400 frames of 16014 bytes = 6255 KB, fed in 256-byte chunks
MspFrameReader : 400 frames in ~8.6–9.0 ms (~680–714 MB/s)
naive re-alloc : 400 frames in ~25–30 ms  (~2.8–3.5x slower)
```

The naive parser allocates a fresh, ever-growing `byte[]` on every feed and copies the accumulated
leftover into it. When frames are large relative to the receive chunk — a 16 KB frame arriving in
256-byte pieces — the leftover grows for ~64 feeds before the frame completes, and re-copying it
every time is **O(n²)** in the frame size. That is why the naive version is 3× slower here and would
get arbitrarily worse with larger frames. Crucially, *tiny frames hide the bug*: a frame that
completes inside one feed never grows the accumulator, so the naive parser looks fine in a unit test
and falls over on the first `ROOM_LIST_RES`. `MspFrameReader` avoids it by compacting leftovers in
place in one buffer and growing by doubling (amortized O(1)).

**Why not `System.IO.Pipelines`, then?** Pipelines solves the same problem — accumulation,
backpressure, and zero-copy reads over a `ReadOnlySequence<byte>` — and in a system tuned for maximum
throughput it would be the right answer. Two reasons it is not used here:

1. **This is a Network Programming capstone.** The framing-over-a-byte-stream problem is the thing
   being learned and defended; delegating it to a library would delete the exercise. `§ 3.4` calls
   this out explicitly.
2. **The load does not need it.** MSP traffic is a few messages per minute per client for a few
   dozen clients. `MspFrameReader` sustains ~700 MB/s single-threaded — four orders of magnitude
   above what a lobby will ever push at it. Pipelines' `ReadOnlySequence` and its multi-segment
   discipline buy throughput we have no use for, at the cost of code that is meaningfully harder to
   read than a single compacting buffer. The `while`-loop-until-`NeedMoreData` drain contract is
   something a reviewer can hold in their head; a `SequencePosition`-based reader is not.

So the hand-written reader is not the *naive* thing (Part D proves that), and it is not the *maximal*
thing either — it is the one sized to the problem, which is the same argument the plan makes for
choosing TCP over hand-rolled reliable UDP in the first place.

---

## 7. How this maps to the shipped code

| Experiment finding | Where it lives in the codebase |
|---|---|
| Drain every glued frame, not just the first | `ClientConnection.Ingest` — `while (true)` around `TryReadFrame` |
| Keep split-frame leftovers across receives | `MspFrameReader` accumulating buffer + `Compact()` |
| Validate `length` before allocating | `MspFrameReader.TryReadFrame` — cap check before the body wait |
| `NoDelay` for a small-message lobby | `TcpListenerHost.Admit` — `socket.NoDelay = true` |
| Byte-stream behaviour is testable | `MspFramingTests` (12) + `TcpListenerHostTests` (7, real sockets) |

---

## Appendix — the experiment source

Kept per the task note ("deleting the experiment because working code exists would trade the only
part of this task that has teaching value"). It is a standalone `dotnet run` program that references
`Ironfront.Net.Protocol` for `MspFrame`/`MspFrameReader`; it is not part of the solution build.

```csharp
// Part A — three Send()s, one Receive(): messages glue together.
var (listener, port) = StartListener();                 // loopback, ephemeral port
byte[] m1 = Frame(MspMessageType.LoginRequest,   "{\"u\":1}");
byte[] m2 = Frame(MspMessageType.RoomListRequest, "{}");
byte[] m3 = Frame(MspMessageType.ChatSend,        "{\"text\":\"hi\"}");

// server thread: sleep so all three sends land, then ONE Receive
using Socket s = listener.Accept();
Thread.Sleep(100);
int n = s.Receive(buf);                                 // returns all 40 bytes at once
uint firstLen = Endian.ReadU32BE(buf.AsSpan(0), 0);     // naive: reads 9, loses frames 2 & 3
var reader = new MspFrameReader();                       // correct: drains all three
reader.Append(buf.AsSpan(0, n));
while (reader.TryReadFrame(out _, out _) == MspReadResult.Frame) framesParsed++;

// client thread: three back-to-back sends
client.NoDelay = false;
client.Send(m1); client.Send(m2); client.Send(m3);

// Part B — one 100 KB Send(), counted across many Receive()s.
client.Send(big);                                       // 100014 bytes, one Send
while (total < big.Length) { int r = s.Receive(buf); total += r; receives++; }

// Part C — 200 request/reply round-trips, timed with NoDelay on and off.
client.NoDelay = noDelay;
var sw = Stopwatch.StartNew();
for (int i = 0; i < 200; i++) { client.Send(ping); client.Receive(reply /* 1 byte */); }

// Part D — accumulating reader vs the O(n^2) naive re-allocation, large frames / small chunks.
reader.Append(stream.AsSpan(off, 256)); /* drain */     // MspFrameReader: compact in place
var grown = new byte[acc.Length + take];                // naive: fresh alloc + full copy each feed
Buffer.BlockCopy(acc, 0, grown, 0, acc.Length);         //   -> O(n^2) when leftovers accumulate
```
