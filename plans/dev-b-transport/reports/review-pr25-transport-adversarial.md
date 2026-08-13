# Adversarial Review — PR #25 UDP Transport (Ironfront.Net.Transport)

Branch `feat/dev-b-transport-phases-0-2` · worktree `d:/Coding/LTM-pr25` · 2,446 lines reviewed
Read-only pass. No files modified in either worktree.

Legend — **CONFIRMED** = traced end-to-end through the code; **PLAUSIBLE** = strong reading, not
fully traced. Categories: **BUG** / **MISSING FEATURE** / **HARDENING** / **SPEC DEVIATION**.

Headline: three independent defects each cause the reliable-ordered channel to die permanently and
silently, with no error surfaced to either peer and no effect on the 345 green tests. One of them
(C1) is remotely triggerable by an unauthenticated attacker.

---

# CRITICAL

## C1 — BUG — Two independent sequence spaces toward the same client permanently poison the client's ack window

**CONFIRMED.**

| Site | What it does |
|---|---|
| `UdpTransportServer.cs:48` | `_controlSequence` — **one server-global** u16 counter |
| `UdpTransportServer.cs:382` | every handshake packet (CHALLENGE / DENIED / ACCEPTED) to **any** endpoint stamps `_controlSequence++` |
| `Connection.cs:413` | after the connection exists, packets use the **per-connection** `_reliability.NextSequence()`, which starts at **0** |
| `Connection.cs:324`, `Connection.cs:336` | the client latches `_remoteSequence` from CHALLENGE and ACCEPTED — i.e. from `_controlSequence` values |
| `ReliabilityLayer.cs:112-126` | a sequence more than 32 behind `_remoteSequence` is discarded and never advances it |

### Concrete failure

Server has been up long enough for `_controlSequence` to reach 3000 (16 players × 3 control packets,
plus one `SendDenied` for every port scan and version-mismatched request).

1. New client finishes the handshake → client `_remoteSequence = 3000`.
2. Server's brand-new `Connection` sends its first packet with sequence **0**.
3. Client: `Distance(0, 3000) = -3000`; `behind = 3000 > ACK_BITFIELD_BITS` → packet is dropped from
   the ack window. `_remoteSequence` stays 3000. Every ack the client emits reads `ack = 3000,
   bitfield = 0`.
4. Server: `ProcessIncomingAck(3000, 0, …)` → `AckPacket(3000)` → slot `3000 % 1024 = 952`, whose
   `Sequence` is not 3000 → **no-op**. `ReliabilityLayer.cs:183`.
5. **No server→client packet is ever acknowledged.** Every reliable one retransmits 10× and is then
   silently abandoned (`ReliabilityLayer.cs:161-165`). `_unackedReliableCount` saturates at 64,
   `CanSendReliable` (`Connection.cs:65`) goes false, and the server can no longer send **any**
   reliable data to that client. The client's ordered channel head-of-line stalls (see C2).
6. Recovery requires the server's `_localSequence` to climb past 3000 — ~3000 packets, ≈100 s at
   30 Hz — assuming the connection is not already dead.

### Remote trigger, unauthenticated

`SendDenied` (`UdpTransportServer.cs:212`) increments `_controlSequence` and is reached **before**
ticket validation, on a protocol-version mismatch. The only gate is the per-IP limiter at 5/s.
200 source addresses × 5/s = 1000 increments/s → the u16 wraps in ~65 s, so every subsequently
joining player draws an effectively random 0–65535 offset. Median damage ≈ 32k packets of dead
reliability, i.e. permanent for the match.

With **no attacker at all** the bug is still live — the second player to connect already sees an
offset of ≥3. Small offsets self-heal within a few packets, which is exactly why the suite is green.

### Why the tests miss it

`ControlAndSocketTests.cs:136`, `:151`, `:284` construct a fresh server per test, so
`_controlSequence` is 0–48 at connect time. `ServerMaintainsSixteenIndependentConnections` gets
closest but only asserts `ConnectionCount` and `State` — never that reliable traffic reaches the
*last* client to join.

### Fix direction

Let the `Connection` own **all** sequence numbers toward its endpoint (send ACCEPTED through
`connection.SendPacket` after `ActivateServer`), or seed the new `Connection`'s `_localSequence` from
the `_controlSequence` value used for its ACCEPTED. Regression test: connect client N *after* the
server has emitted ≥100 control packets, then assert a reliable server→client message arrives.

---

## C2 — BUG — An abandoned reliable packet permanently and silently kills the reliable-ordered channel

**CONFIRMED.**

- `ReliabilityLayer.cs:161-165` — after `MaxResends = 10` the packet is dropped with a
  `NetLog.Warn` and **nothing else**: no callback, no disconnect, no signal to the application.
- `ChannelSet.cs:127-144` — the ordered channel delivers strictly from `_nextOrdered` and advances
  only when the exact next sequence is present.
- `ChannelSet.cs:107-111` — anything ≥ `OrderedWindow` (256) ahead of the stalled `_nextOrdered` is
  rejected.

Channel-2 message N is lost; its carrying packet exhausts 10 resends (≈10 s at the 1000 ms RTO
ceiling, ≈300 ms at the 30 ms floor). The sender moves on. The receiver's `_nextOrdered` is stuck at
N forever: N+1…N+255 sit in `_ordered` pinning 255 pooled buffers, and N+256 onward are dropped.
**Every gameplay event, spawn/despawn, death and chat message stops being delivered for the rest of
the session**, with no error on either side. Keep-alives keep flowing, so the 10 s timeout never
fires — the connection looks perfectly healthy.

protocol-spec § 5 specifies channel 2 as *"Retransmitted until acked"*. Giving up after 10 attempts
and continuing as if nothing happened violates that contract. Correct behaviour is to fail the
connection (`DisconnectReason.TransportError`) when a reliable packet becomes unrecoverable.

Independently reachable under ordinary loss; guaranteed the moment C1 fires.

---

## C3 — BUG — `FlowControl._pauseNewReliable` latches on and is never cleared in practice

**CONFIRMED.**

- `FlowControl.cs:29-33` — `ApplyRemote` sets `_pauseNewReliable = true` when the peer advertises
  `BufferPressurePercent > 80` or 64+ pending. It is only ever cleared by a *later* `ApplyRemote`
  with lower values. `Reset()` (`FlowControl.cs:35`) exists but **no caller anywhere invokes it**.
- `Connection.cs:142-143` — flow-control info is applied **only** from a `Keepalive` payload.
- `Connection.cs:132-134` / `:202-204` — flow-control info is written **only** into keep-alives.
  Keep-alives are sent only (a) when a reliable packet was just received, or (b) after
  `KEEPALIVE_MS` of send-idleness.
- `Connection.cs:200` — `if (nowMs - _lastSendMs >= KEEPALIVE_MS)`, and `_lastSendMs` is refreshed by
  **every** send (`Connection.cs:430`).

### Concrete failure

Server streams snapshots at 20 Hz on channel 1 (unreliable), so its `_lastSendMs` is never 1 s stale
and the idle keep-alive **never fires**. Client streams input at 30 Hz on channel 3 (unreliable), so
the server never receives a *reliable* packet and never emits the on-receipt keep-alive either.

One transient burst pushes the server past 52 unacked reliable packets → it advertises
`pressure = 81` in the one keep-alive that happens to go out → the client latches
`_pauseNewReliable = true` → `CanSendReliable` is false forever. **The client can never send another
reliable message for the lifetime of the connection**, even after the server fully drains, because no
further keep-alive is ever produced to carry the update.

`ControlAndSocketTests.cs:35-43` tests `ApplyRemote` + `Reset` at unit level and passes — but nothing
in the transport calls `Reset`, and no integration test exercises the un-pause path.

Fix: piggyback flow-control on every packet (or expire the pause after ~2 × `KEEPALIVE_MS` without a
fresh advertisement), and call `Reset()` on ack progress.

---

# HIGH

## H1 — SPEC DEVIATION — PAYLOAD wire layout does not match the frozen spec (§ 2 / § 4)

**CONFIRMED.** No in-repo runtime symptom — the whole solution is internally consistent — but the
bytes on the wire are not the bytes the frozen spec defines.

- Spec § 2 puts `payload[payloadLength]` at offset 16; § 4 defines that payload as
  `u8 channelId; u16 messageCount; { u8 msgType; u16 msgLength; u8[] body } × messageCount`.
- `Ironfront.Net.Protocol/Gsp/PayloadFrame.cs:40-53` implements exactly that (`HeaderSize = 3`).
- `Connection.cs:233-236` prepends its **own** undocumented 3-byte envelope
  (`u8 channelId; u16 channelSequence`) and copies the caller's `PayloadFrame` after it.
  `Connection.cs:352-360` strips it symmetrically on receive;
  `Ironfront.Net.Replication/Server/ServerMessageRouter.cs:70` then parses the remainder with
  `PayloadFrameReader`. `Loopback/LoopbackTransport.cs:50` uses the same private envelope.

On the wire:

```
[GSP 16][channelId][channelSeq u16][channelId][messageCount u16][messages…]
             ^ undocumented envelope     ^ spec §4 frame, displaced 3 bytes
```

Consequences:

1. **Interop break with the referee.** Dev C's conformance hex-sample suite (§ 14, *"the referee
   whenever two people disagree"*) writes expected bytes from the § 4 table by hand. A
   transport-produced PAYLOAD does not match: offset 17-18 reads as `messageCount` but carries the
   channel sequence, and `msgType` is read from the duplicated `channelId` byte. Every message routes
   to the wrong handler.
2. `channelId` is on the wire twice and the two copies are never cross-checked.
3. Usable payload silently drops from 1184 to **1181** bytes (`Connection.cs:230-231`), so a
   spec-legal 1184-byte batch fragments unexpectedly.

The per-channel sequence the transport genuinely needs is absent from the spec (§ 5 mandates stale-drop
on channels 1/3 but defines no sequence field) — that is a real spec gap. The fix is a spec amendment
with a § 15 changelog row and a `PROTOCOL_VERSION` bump, not a silent re-purposing of the
`messageCount` offset.

## H2 — BUG (spec violation) — The challenge handshake allocates server state before address validation

**CONFIRMED.** protocol-spec § 3.1 states the entire purpose of the challenge: *"An attacker spoofing
a victim's IP in a CONNECT_REQUEST never receives the serverSalt, so they can't complete the handshake
and **the server allocates no resources**."*

`UdpTransportServer.cs:239-248` allocates on the CONNECT_REQUEST: a `PendingChallenge`, a cloned
`IPEndPoint` (`CloneEndpoint`), a CSPRNG draw, and a dictionary entry — before the source address has
been proven reachable.

**Exploit.** An attacker holding one valid joinTicket (any legitimate player has one; see M3 — it is
never bound to a source address and never marked single-use) replays it from spoofed source
addresses. The per-IP limiter (`:206`) is keyed on the address the *attacker* chose, so spoofing
bypasses it entirely. `_pending` fills to `MaxPendingChallenges = 2048`; every further request then
calls `EvictOldestChallenge` (`:421-434`), which evicts **oldest first** — i.e. legitimate clients
mid-handshake.

An evicted legitimate client is **unrecoverable**: it is in `Challenged` state and
`Connection.cs:186-187` only ever resends CONNECT_RESPONSE from there; a re-issued challenge with a
fresh salt is ignored because `Connection.cs:319` requires `State == Connecting`. The client burns
its 20 attempts and reports `Timeout`. Result: handshake denial-of-service against every joining
player, at a cost of a few hundred spoofed packets per second.

`EvictOldestChallenge` is also an O(n) scan over 2048 entries per request while under attack.

Correct design: a stateless cookie — `serverSalt = HMAC(srcAddr ‖ srcPort ‖ secret ‖ epoch)`,
recomputed and verified on CONNECT_RESPONSE. No table, nothing to evict, nothing to spoof into.

## H3 — BUG — Unbounded receive drain: one flood starves the simulation tick

**CONFIRMED.** `UdpPeer.cs:124` — `while (_socket.Poll(0, SelectMode.SelectRead)) { … }`. No
per-poll packet budget, no wall-clock budget. The loop exits only when the 1 MB kernel receive
buffer (`UdpPeer.cs:49`) drains.

An attacker sustaining a packet rate above the drain rate keeps `UdpPeer.Poll` — and therefore
`UdpTransportServer.Poll`, and therefore the host's entire game tick — inside this loop indefinitely.
Connection `Update`, retransmission, keep-alive and the simulation never run; existing players time
out at 10 s. Per-packet rejection being cheap is irrelevant when the loop has no exit condition other
than the attacker stopping.

**Related gap vs. architecture.md § 9**, which promises *"Packet floods → Per-IP rate limiting at the
transport layer; connections over the threshold are dropped."* `RateLimiter.Allow` is invoked in
**exactly one place** — `HandleConnectRequest` (`UdpTransportServer.cs:206`). CONNECT_RESPONSE,
PAYLOAD, FRAGMENT, KEEPALIVE, DISCONNECT and all unknown-source traffic are unlimited.
`PacketsFromUnknown` (`:59`) counts them; nothing acts on the counter.

Fix: cap the drain (`maxPacketsPerPoll`, or a ~2 ms budget) and apply per-IP limiting to all
pre-connection packets.

---

# MEDIUM

## M1 — BUG — Simulated mode can deliver CONNECT_DENIED to the wrong host (endpoint aliasing)

**CONFIRMED**, simulator-enabled paths only.

`UdpPeer.cs:142-143` reuses a single `ReusableIpv4EndPoint` instance for every received datagram —
mutated in place by each `ReceiveFrom`. `UdpTransportServer.cs:174` passes that live instance to
`HandleConnectRequest` → `SendDenied(remote, …)` → `SendControl` → `_peer.Send(…, endpoint, …)` →
`NetworkSimulator.ShouldSend` (`Simulation/NetworkSimulator.cs:126`) stores the **reference** in
`_inFlight`. By the time `Flush` (`:170`) fires, later `ReceiveFrom` calls have overwritten the
object, so `RawSend` → `GetSendSocketAddress` (`UdpPeer.cs:221-222`) serialises a **different peer's**
address.

Every other server send path is safe because it uses `CloneEndpoint` (`UdpTransportServer.cs:241`,
`:449-454`); `SendDenied` is the one that does not. Because the simulator is the project's primary
verification tool, this can both mask real behaviour and manufacture flaky results. Fix: clone before
`SendDenied`, or have `NetworkSimulator` clone `TDestination` when it is a reference type.

## M2 — BUG — `ReliabilityLayer` slot reuse silently drops an unacked reliable packet

**CONFIRMED mechanism, PLAUSIBLE reachability.**

`ReliabilityLayer.cs:76-78` — `OnPacketSent` calls `ReleaseSlot(ref old)` on the slot at
`sequence % 1024`. If that slot still holds an **unacked reliable** packet, `ReleaseSlot`
(`:207-219`) decrements `_unackedReliableCount`, returns its buffer, and marks it `Acked = true`.
The packet is dropped: never retransmitted, never reported, and the send window silently gains a
free slot.

Reaching it needs 1024 sequence numbers to elapse while a reliable packet is still pending. Sequence
numbers are burned by every payload, every idle keep-alive, and — notably —
**one keep-alive per reliable packet received** (`Connection.cs:130-135`). At ~50–80 sequences/s that
is ~15 s, versus the ≤10 s abandonment ceiling (`MaxResends × MaxRtoMs`). The margin is thin and
undefended: any future RTO or `MaxResends` change re-opens it, and the failure is completely silent.

`ReliabilityLayerTests.cs:91-101` (`AnOverwrittenHistorySlotCannotBeAcknowledgedAsTheNewPacket`)
enshrines this behaviour rather than flagging it — it asserts `PendingReliableCount == 1024` after
1025 reliable sends, i.e. exactly one packet silently vanished.

## M3 — MISSING FEATURE — joinTicket is validated but never *used*: no playerId binding, no replay protection

**CONFIRMED.**

`Ironfront.Net.Protocol/Security/JoinTicket.cs` ships a complete implementation — HMAC `Verify`,
expiry check, `TryReadFields` exposing `playerId`, and `ToDenyReason`. The transport uses **none of
it**: `UdpTransportServer.ValidateTicket` (`:251-266`) reduces the ticket to a `bool` via the
`OnValidateTicket` event and then discards it (`:231`). Consequences:

1. **architecture.md § 9's impersonation control is not implemented.** The table states
   *"connectionId is bound to the playerId from the HMAC-signed joinTicket."* Nothing in the
   transport reads `playerId`, and `ConnectionInfo` (`ITransport.cs:39-54`) has no field for it, so
   no consumer can perform the binding either.
2. **No single-use / replay protection.** The same ticket, valid for 60 s, can open unlimited
   simultaneous connections. `ConnectDenyReason.AlreadyConnected` (code 6, spec § 3.2) exists in the
   enum and is **never sent** anywhere in the solution.
3. `ConnectAcceptedPayload(connectionId, 0, 0, 0)` (`UdpTransportServer.cs:358`) hardcodes
   `serverTick`, `mapId` and `myPlayerId` to **0**, and `ITransportServer` exposes no API for the
   host to supply them. Spec § 3.1 requires all four fields. Dev A/D cannot learn their `playerId`
   or the map from the handshake.

Fail-closed behaviour when no validator is registered (`:255`) is correct and well tested — the gap
is everything downstream of the boolean.

## M4 — BUG — A client whose server-side pending challenge is lost can never recover

**CONFIRMED.** `Connection.cs:176-188` — in `Challenged` state, `Update` only ever calls
`SendConnectResponse`. `Connection.cs:318-328` — a CONNECT_CHALLENGE is only accepted when
`State == Connecting`.

So once the client has moved to `Challenged`, a server-side salt change (eviction per H2, or expiry
via `CleanupChallenges`, `UdpTransportServer.cs:401-419`) is terminal: the client keeps replaying a
response computed against a salt the server no longer holds, the server silently ignores it
(`:292`), and the client dies at 20 × 250 ms. Accepting a fresh challenge from `Challenged` state
(re-deriving the response) would make this self-healing.

Related: `UdpTransportServer.cs:294-295` removes the pending entry **before** checking
`_freeIds.Count == 0`, so a client that loses the last slot in a race gets no DENIED at all — it just
times out.

## M5 — BUG — A client reconnecting from the same address is silently ignored for up to 10 s

**CONFIRMED.** `UdpTransportServer.cs:216` — `if (_byEndpoint.ContainsKey(key)) return;`. A client
that crashed and restarted on the same source port is dropped without a reply until its old
connection times out (`TIMEOUT_MS = 10000`). The client meanwhile exhausts its 20 attempts in 5 s and
reports `Timeout`, so the reconnect **always** fails on the first try. Spec § 3.2 provides
`ConnectDenyReason.AlreadyConnected` for exactly this — it is never sent (see M3).

## M6 — SPEC DEVIATION — RTT smoothing does not match spec § 8

**CONFIRMED.** protocol-spec § 8 mandates `smoothedRtt = smoothedRtt * 0.9f + newSample * 0.1f`.
`ReliabilityLayer.cs:202-204` implements Jacobson/Karels instead (`SmoothedRttMs += 0.125f * delta`,
with a jitter EWMA at 0.25). Technically the better estimator, but the spec is FROZEN: this needs a
§ 15 changelog row (no `PROTOCOL_VERSION` bump — not a wire change) or the code should match.

Same class: `CongestionControl.cs:20,42-44` doubles the bad-mode dwell to 20 s when
`_goodStreak < 10` — behaviour that appears nowhere in spec § 8, and which
`ControlAndSocketTests.cs:27-32` pins as expected.

## M7 — BUG — Off-by-one loses the bit-31 ack on an exactly-32 sequence jump

**CONFIRMED, low impact.** `ReliabilityLayer.cs:117` — `distance >= ProtocolConstants.ACK_BITFIELD_BITS
? 0u : …`. With a 32-bit field, bit *i* represents `remoteSequence - (i+1)`, so a jump of exactly 32
leaves the previous `_remoteSequence` representable at bit 31; the guard discards the whole history
instead. The reorder path at `:125` (`behind <= ACK_BITFIELD_BITS`, `1u << (behind-1)`) correctly
*does* handle 32 — the two paths are asymmetric. Should be `distance > ACK_BITFIELD_BITS`. Costs one
spurious retransmission per exactly-32 jump.

---

# HARDENING (no confirmed exploit, worth doing)

- **`Connection.cs:130-135`** — every received reliable packet triggers an immediate keep-alive.
  A 64-fragment message therefore produces 64 keep-alives, doubling the packet rate on the fragment
  path and burning 64 sequence numbers (feeds M2). Coalesce to at most one ack-keepalive per poll.
- **`UdpTransportServer.cs:189-193`** — spoofing defence rests entirely on
  `endpoint match + connectionId match`, with no per-packet MAC. Deliberate (architecture § 9 declines
  encryption), but note the specific consequence: an off-path attacker who guesses `connectionId`
  (16 bits, and it is handed out sequentially from 1) and spoofs the source address can inject
  `ack = X, bitfield = 0xFFFFFFFF` and falsely acknowledge 33 in-flight reliable packets
  (`ReliabilityLayer.cs:133-141`), causing silent reliable-message loss. Randomising `connectionId`
  raises the cost from trivial to 1-in-65535 per attempt.
- **`FragmentAssembler.cs:68-72`** — a single fragment carrying a mismatched `fragmentCount` destroys
  an in-progress legitimate group for the *same* connection. Bounded blast radius (same peer), but
  dropping only the offending fragment would be strictly better than dropping the group.
- **`BufferPool.cs:71-85`** — `Return` validates length but not provenance, so a double-return pushes
  the same array twice and two renters alias one buffer. I found no double-return path (all sites use
  `try/finally` with clear single ownership, and `ChannelSet.cs:132` nulls the slot before
  `deliver`), but a `HashSet` identity check under `#if DEBUG` would make the invariant enforced
  rather than merely observed.
- **`BufferPool.cs:53-62`** — the pool grows on demand and **never shrinks**; `TotalBuffers` is a
  one-way ratchet. Peak concurrent rentals (8 fragment groups × 64 parts + 256 ordered + 64 reliable
  ≈ 830 × 1200 B ≈ 1 MB per connection) become permanent per-connection resident memory.
- **`Connection.cs:200-205`** — keep-alives pass `trackReliability: false`, so they never yield an
  RTT sample. Spec § 3 lists KEEPALIVE as *"Keeps the connection alive, measures RTT."* In practice
  RTT still works because `Connection.cs:243` tracks unreliable payloads too, but a connection idle
  except for keep-alives reports `SmoothedRttMs = 0` forever, which silently disables congestion
  control and any lag compensation that reads it.
- **`Connection.cs:111`** — `PacketFlags.ReservedMask = 0xF0` (matching spec § 2.1) means a packet
  with `COMPRESSED` (bit 3) set is accepted and its payload processed as uncompressed. v1 cannot
  decompress; it should reject the flag rather than misparse.
- **`UdpTransportServer.cs:113-133`** — `Send` / `Broadcast` / `Disconnect` use `_nowMs`, refreshed
  only inside `Poll()`. A host that sends outside the poll cadence records a stale `SentAtMs`
  (`ReliabilityLayer.cs:93`), producing an immediate spurious retransmit and an inflated RTT sample.
- **`UdpTransportClient.cs:47-65`** — after a `Timeout` the client's `State` returns to
  `Disconnected` while `_peer` stays alive; a second `Connect()` overwrites `_peer` and leaks the
  previous socket.

---

# SPEC COMPLETENESS — sections 2 / 3 / 5 / 6 / 8

| Spec item | Status |
|---|---|
| § 2 GSP header, 16 B, LE, `protocolId` filter | **Implemented and correct.** `GspHeader.TryParse` validates length, `protocolId`, `payloadLength ≤ MAX_PAYLOAD` **and** `src.Length ≥ 16 + payloadLength` — no malformed datagram can slice past the buffer. |
| § 2.2 ack + 32-bit bitfield, bit *i* = `ack-1-i` | Implemented; convention matches `GspHeader.BuildAckBitfield`/`IsAcked`. One off-by-one (M7). Broken in practice by C1. |
| § 2.3 wrap-safe sequence comparison | **Fully compliant** — see the audit below. |
| § 3 packet types | All 9 declared; `ConnectDenyReason` 5 (`ServerShuttingDown`) and 6 (`AlreadyConnected`) are **never sent** (M3, M5). |
| § 3.1 four-message handshake, XOR challenge, 250 ms × 20 retries | Implemented (`Connection.cs:14-15`). Violates the "allocates no resources" requirement (H2). `CONNECT_ACCEPTED` fields `serverTick`/`mapId`/`myPlayerId` are hardcoded 0 — **missing** (M3). |
| § 5 four channels | Implemented. Channels 1/3 stale-drop via `SequenceMath.IsNewer` ✔. Channel 2 ordering ✔ but permanently stalls on an abandoned packet (C2). |
| § 6 fragmentation, `MAX_FRAGMENTS = 64`, `FRAGMENT_TIMEOUT_MS`, 8-group cap, drop-oldest | **Fully implemented and correctly ordered** — `FragmentHeader.TryParse` rejects `count == 0`, `count > 64`, `index >= count`; `FragmentAssembler.cs:49-66` checks the 8-group cap and evicts **before** allocating anything sized from attacker input; `data.Length > _pool.BufferSize` is rejected. This is the strongest part of the PR. |
| § 8 congestion control, 250/200 ms hysteresis, 20/10 Hz | Implemented. RTT smoothing formula deviates (M6); un-specced 20 s escalated dwell. |
| § 12 joinTicket HMAC | **Not used by the transport** (M3). |
| architecture § 9 per-IP flood limiting | **Only on CONNECT_REQUEST** (H3). |
| architecture § 9 connectionId↔playerId binding | **Not implemented** (M3). |

## Sequence-wrapping audit (brief item 1) — PASS

I checked every comparison and every arithmetic operation on a sequence value in the changed files.
**Zero raw `>` / `<` / `-` on a sequence number.**

| Site | Form |
|---|---|
| `ReliabilityLayer.cs:112` | `sequence == _remoteSequence` — equality, wrap-safe |
| `ReliabilityLayer.cs:114` | `SequenceMath.Distance` ✔ |
| `ReliabilityLayer.cs:76`, `:181` | `sequence % 1024` index + exact `packet.Sequence != sequence` guard ✔ |
| `ReliabilityLayer.cs:139` | `(ushort)(ack - 1 - bit)` — wraps correctly; `Directory.Build.props` sets no `CheckForOverflowUnderflow`, so the unchecked cast is safe by default (worth pinning explicitly, since enabling checked arithmetic project-wide would turn this into an `OverflowException` on every `ack < 32`) |
| `ChannelSet.cs:71` | `SequenceMath.IsNewer` ✔ |
| `ChannelSet.cs:106-111` | `IsNewer` + `Distance` ✔ |
| `ChannelSet.cs:113`, `:129` | `% OrderedWindow` + exact `ready.Sequence != _nextOrdered` guard ✔ |
| `GspHeader.cs:163` | `SequenceMath.Distance` ✔ |
| `Loopback/LoopbackTransport.cs:223` | `SequenceMath.IsNewer` ✔ |
| `Connection.cs:254` | `_nextFragmentGroup++` — u16, wrap is intentional and safe (8-group cap + 2 s timeout make collision unreachable) |

C1 is *not* a wrap bug — the arithmetic is correct; the two counters are simply unrelated.

## Ack-bitfield checklist (brief item 2)

- Bit 0 vs bit 31 — consistent between `ReliabilityLayer` and `GspHeader.BuildAckBitfield`/`IsAcked`.
  One off-by-one at the 32 boundary (M7).
- Ack older than the window — `AckPacket` (`:183`) guards on `InUse`, exact `Sequence` match, and
  `Acked`; an out-of-window ack lands on a slot whose `Sequence` differs and is a no-op. Correct.
- Duplicate ack → double free / double RTT — **not possible**: `ReleaseSlot` clears `InUse` and nulls
  `Data`, and `AckPacket` re-checks both. Correct.
- Karn's algorithm — **correctly implemented**: `ReliabilityLayer.cs:186` samples RTT only when
  `ResendCount == 0`, and `:169` refreshes `SentAtMs` on each resend. Covered by
  `KarnsAlgorithmDoesNotSampleAResentPacket`.

## Threading (brief item 6) — no race found

There is no receive thread. `UdpPeer.Poll` (`UdpPeer.cs:116`) drains the socket **synchronously on
the calling thread**, `Socket.Blocking = false`, and every callback runs inline before `Poll` returns.
`UdpTransportServer.Poll` / `UdpTransportClient.Poll` are the only drivers. `BufferPool.cs:19-20`
documents "not thread-safe; single-threaded by design (B-AD-1)" and that invariant holds throughout.
No unsynchronised cross-thread `Dictionary` access exists. Re-entrancy is present (a receive callback
sends, which rents from the same pool) but ownership is disjoint at every step. The design is correct
— but nothing *enforces* single-threading; a `Thread.CurrentThread.ManagedThreadId` assertion in
`Poll` under `#if DEBUG` would keep it that way.

## Buffer-pool / callback-lifetime audit (brief item 4) — PASS

`ITransport.cs:70-77` documents that `OnMessage` memory is valid only for the call. Verified honoured:
`UdpPeer.cs:161-171` returns the receive buffer in `finally` *after* the callback;
`ChannelSet.cs:115-124` **copies** anything that must wait behind a gap and `:135-142` returns it in
`finally` after delivery; `FragmentAssembler.cs:76-79` copies each fragment;
`ReliabilityLayer.cs:83-84` copies reliable packets into pool-owned storage before the caller's
datagram is returned; `NetworkSimulator.cs:118-119` copies into its own rented buffer.
`Connection.Fail` (`:459-461`) clears all three sub-systems before firing `Disconnected`, so no
connection teardown leaks pool buffers. `OneThousandReliablePacketsArriveThroughThirtyPercentLoss`
asserts `RentedCount == 0` at the end — a genuinely good invariant test.
The `#if DEBUG` `0xDD` fill (`BufferPool.cs:81`) is the right call. **No use-after-return found.**

---

# TEST QUALITY (brief item 8)

The 48 tests are better than typical happy-path suites — `KarnsAlgorithmDoesNotSampleAResentPacket`,
`AnOverwrittenHistorySlotCannotBeAcknowledgedAsTheNewPacket`, `FragmentGroupsAreCappedAtEight`,
`InvalidFragmentInputDoesNotAllocateAPendingGroup`, `MissingTicketValidatorFailsClosed`,
`AllTicketValidatorsMustApprove`, `OrderedChannelCopiesDataThatWaitsInTheBuffer` and the 30 %-loss
soak with a pool-leak assertion are all real adversarial tests. The gap is that **every security
limit is tested at unit level only** — nothing verifies the server actually *invokes* it.

### Vacuous / over-claiming

| Test | Problem |
|---|---|
| `ControlAndSocketTests.cs:46` `RateLimiterAllowsFiveRequestsThenRejectsTheRest` | Tests `RateLimiter` in isolation. **Delete line `UdpTransportServer.cs:206` entirely and all 345 tests still pass.** No test asserts `RateLimitedRequests > 0`. |
| `ControlAndSocketTests.cs:232` `LocalhostWrongTicketIsDeniedWithoutAllocatingAConnection` | Name claims "without allocating"; the body only asserts `ConnectionCount == 0`. It does not check that `_pending` stayed empty — which is precisely the H2 defect. |
| `ChannelAndFragmentTests.cs:105` `FragmentGroupsAreCappedAtEight` | Good at unit level, but nothing tests the cap **through `Connection.ProcessFragment`**, so a regression in the wiring is invisible. |
| `ControlAndSocketTests.cs:67` `RateLimiterEntryTableIsBounded` | `Assert.InRange(count, 1, 10_000)` passes for any bound ≤ 10 000, including a bound of 1. Weak, though it would catch "unbounded". |
| `ReliabilityLayerTests.cs:91` | Asserts `PendingReliableCount == 1024` — pins the M2 silent-drop as *expected* behaviour rather than flagging it. |
| `UdpPeerTests.cs` (44 lines, **1 test**) | The raw socket layer has essentially no adversarial coverage: no truncated datagram, no wrong `protocolId`, no over-claimed `payloadLength`, no flood. |

### Missing failure-mode coverage (each maps to a finding above)

1. Connect a client to a server that has already issued ≥100 control packets, then assert reliable
   server→client delivery — would catch **C1**.
2. Drop one channel-2 packet past `MaxResends`, then assert the connection fails loudly rather than
   stalling silently — **C2**.
3. Advertise `pressure = 81`, drain, then assert reliable sending resumes — **C3**.
4. Assert a spoofed `connectionId` from a known endpoint increments `PacketsWithBadConnectionId` —
   nothing references that counter, so deleting `UdpTransportServer.cs:189-193` keeps the suite green.
5. Duplicate CONNECT_REQUEST from a connected endpoint; CONNECT_REQUEST during `Challenged`;
   DISCONNECT mid-handshake; timeout mid-handshake — **no handshake-race test exists at all**.
6. Connection-id reuse: disconnect a client, connect a new one, replay an old-owner packet, assert it
   is not delivered to the new player. (The code appears correct — `_freeIds` is FIFO and the
   `connectionId` check rejects stale packets — but it is unverified.)
7. Ordered-channel sequence wrap across 65535→0.
8. `Connection`-level send-window cap (`Connection.cs:227,252`) — only `FlowControl`/`ReliabilityLayer`
   are tested in isolation.
9. Hostile datagram fuzz through `UdpPeer.Poll` asserting no exception escapes.

---

# SUMMARY

| Severity | Count | Items |
|---|---|---|
| Critical (blocking) | 3 | C1, C2, C3 |
| High (blocking) | 3 | H1, H2, H3 |
| Medium | 7 | M1–M7 |
| Hardening | 9 | see section |

**Blocking for merge:** C1, C2, C3, H2, H3. H1 is blocking for *interop* and needs a spec decision
(amend § 4 with a `PROTOCOL_VERSION` bump, or move the channel sequence out of the payload) before
Dev A/C/D build on it — it is the one finding that cannot be fixed inside this project alone.

**What is genuinely good and should not be touched:** the fragmentation path (§ 6 is implemented
exactly as specified, with the anti-DoS limit checked before any attacker-sized allocation), the
`SequenceMath` discipline (zero raw comparisons — the project's own stated review-blocker is clean),
Karn's algorithm, the buffer-ownership contract and its `finally`-based enforcement, the fail-closed
ticket validator, silent drops on junk packets, and the `NetworkSimulator` being built before the
transport it tests.

### Score: 5/10

Well-structured, well-documented, disciplined about the traps it anticipated — and it has three
separate ways for the reliable channel to die permanently and silently in production while every test
stays green. The common root is that **no failure path is loud**: `MaxResends` exhaustion, ordered-window
stall, and the flow-control latch all degrade to "the connection looks healthy but delivers nothing,"
which is the single worst failure mode for a hand-written transport and the one the project's own
`development-principles.md` ("Errors Over Silent Fallbacks") explicitly forbids.

### Process note

The mandated edge-case-scouting sub-agent could not be spawned — no `Agent`/`Task` tool is available
in this session's toolset. The edge-case enumeration above was performed inline by reading every
listed file plus `GspHeader`, `FragmentHeader`, `Fragmenter`, `PayloadFrame`, `SequenceMath`,
`ProtocolConstants`, `ConnectMessages`, `JoinTicket`, `NetworkSimulator`, `LoopbackTransport`,
`ServerMessageRouter` and protocol-spec §§ 0–15 / architecture § 9 end-to-end.
