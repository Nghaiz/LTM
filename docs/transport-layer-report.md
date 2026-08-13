# Reliable transport over UDP — Dev B report draft

## Scope and claim

This chapter documents a transport that implements reliability above UDP while allowing the
application to choose a different guarantee for snapshots, input, events and small unsequenced
messages. The claim is not that UDP is intrinsically faster than TCP. At equal reliability the
latency cost is comparable; the architectural advantage is that a lost gameplay event does not
have to block an unrelated snapshot.

## Protocol design

Every datagram has the 16-byte little-endian GSP header from the frozen protocol. It carries a
connection-wide sequence, cumulative ACK and a 32-bit ACK history. The transport-owned channel
envelope then carries the channel id and its independent sequence. This separation matters because
keep-alives and packets on other channels must not make a snapshot look stale.

The four channels are:

| Channel | Guarantee | Intended data |
|---|---|---|
| 0 | Unreliable, unsequenced | ping/pong and disposable messages |
| 1 | Unreliable, sequenced | server snapshots |
| 2 | Reliable, ordered | events, spawn/despawn, chat |
| 3 | Unreliable, sequenced | client input |

Messages beyond the safe 1200-byte datagram are fragmented at the transport layer. Reassembly is
bounded to eight groups per connection and two seconds, so attacker-controlled fragment metadata
cannot allocate unbounded state.

The handshake uses a stateless, endpoint-bound cookie. The server does not retain challenge state
for an unproved source address. The ticket is validated only after the response proves the source
address, then its player id is bound to the connection. This avoids both spoofed-source state
exhaustion and unlimited replay of one real player's ticket while it is active.

## Implementation

`UdpPeer` owns a non-blocking socket and is polled on one thread. `BufferPool` owns fixed-size
datagram storage; callback memory is valid only until the callback returns. Reliable packets are
copied into pool storage until acknowledged. A resend exhaustion or reliability slot collision
ends the connection with `TransportError`, because silently continuing would permanently block the
ordered channel.

RTT uses only first-transmission ACKs (Karn's algorithm). The RTO has a 30 ms floor and 1000 ms
ceiling. Congestion control uses GOOD/BAD hysteresis at 200/250 ms and a ten-second BAD dwell.
Flow control caps outstanding reliable packets at 64 and advertises pressure in keep-alives.

## Security controls

- wrong protocol id, invalid lengths and reserved flags are silently dropped;
- handshake cookies are keyed and expire in bounded epochs;
- ticket validation is fail-closed and player ids are bound while connected;
- pre-auth requests are rate-limited per source address;
- the receive loop has a 1024-packet budget per poll;
- unknown connection ids and mismatched endpoint/connection pairs are dropped;
- fragment groups and reliable windows are bounded;
- payload encryption is intentionally not included in this capstone transport and is a known
  limitation before public deployment.

## Testing methodology

The seeded network simulator covers loss, latency, jitter, duplication and reorder. The current
transport suite covers handshake hardening, reliable delivery, ACK history, retransmission and
Karn behavior, channel ordering, fragmentation limits, socket boundaries, diagnostics capture,
server metadata, connection survival and flow-control behavior. Run:

```powershell
dotnet build Ironfront.sln -c Release
dotnet test Ironfront.sln -c Release
dotnet run --project tools/SpecChecker -c Release
```

The packet logger records raw datagrams in a versioned `.ifpcap` format. The offline replay tool
decodes headers, filters a time/connection window, estimates sequence gaps and correlates ACKs to
RTT samples. It reports congestion changes inferred from those RTT samples; the mode is not
authoritative because it is local control state and is not encoded on the wire.

## Experimental evidence

The repository contains deterministic localhost transport and benchmark evidence. A complete
academic comparison still requires the same TCP and UDP workloads under 0/5/15/30% loss, an ACK
bitfield on/off run, a head-of-line run, congestion on/off at 20% loss, a BufferPool/ArrayPool
comparison and a 1-to-64 connection sweep.

No VPS endpoint, packet impairment appliance, Wireshark capture or Unity F3 screenshot is present
in this workspace. Those values are intentionally not fabricated. The final report must append
the externally collected LAN/VPS table and the eight-hour soak log before claiming the M3/M4
acceptance criteria complete.

## Limitations and defense answers

1. **Is UDP faster than TCP?** No. Equal guarantees have comparable costs; independent channel
   guarantees are the advantage.
2. **Why not QUIC?** QUIC is the production-grade choice; this project implements the mechanisms
   for the network-programming capstone and keeps the wire behavior inspectable.
3. **Is congestion control TCP-fair?** Not fully. The two-mode policy is intentionally simpler
   than AIMD and this is a limitation before shared-network deployment.
4. **Why 1200 bytes?** It leaves margin for PPPoE, VPN and tunnel paths without IP fragmentation.
5. **How many players?** The measured target is 16, with a short-run 64-connection benchmark;
   the final ceiling must come from the connection sweep.
6. **When do sequences wrap?** At 30 packets/s, a 16-bit sequence wraps in about 36 minutes;
   wrap-safe sequence math and boundary tests cover it.
7. **How is cheating prevented?** The transport limits spoofing and resource abuse; authoritative
   gameplay and anti-cheat remain above this layer.
