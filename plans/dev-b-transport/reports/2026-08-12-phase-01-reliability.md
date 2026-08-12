# Report — Phase 01: Reliability Layer

- **Author:** Dev B (Transport)
- **Date:** 2026-08-12
- **Week:** 3–6
- **Phase:** [phase-01-reliability](../phases/phase-01-reliability.md)
- **Status:** Done for code and automated acceptance tests

## 1. Summary

The transport now implements the challenge-response connection lifecycle, cumulative ACK plus a
32-bit history window, reliable retransmission, Karn-safe RTT estimation, four channel policies,
bounded fragmentation, keep-alive/timeout/disconnect handling, and pooled ownership. Reliability
is per packet while delivery ordering is per channel, so stale snapshots do not block gameplay
events.

## 2. Acceptance criteria

| # | Criterion | Met | Evidence |
|---|---|---|---|
| 1 | At least 40 tests | Yes | 48 transport tests |
| 2 | Challenge handshake and wrong ticket | Yes | Localhost success and invalid-ticket tests |
| 3 | Reliable delivery under loss | Yes | `OneThousandReliablePacketsArriveThroughThirtyPercentLoss`: 1,000/1,000 through seeded 30% simulator |
| 4 | Stale sequenced packets dropped | Yes | `SnapshotChannelDropsOlderPackets` |
| 5 | 20 KB reassembly | Yes | `TwentyKilobytePayloadReassemblesByteForByte` |
| 6 | RTT estimator | Yes | First-sample and RTO tests; runtime latency matrix still needs simulator run |
| 7 | Karn algorithm | Yes | `KarnsAlgorithmDoesNotSampleAResentPacket` |
| 8 | Long soak | Yes | Phase-02 600-second, 16-connection soak: 16/16 remained connected |
| 9 | Sequence wrap | Yes | receive wrap and protocol conformance tests |
| 10 | Hot-path allocation | Covered by pool/benchmark | Benchmark reports socket-runtime allocations separately |

## 3. Test result

48/48 transport tests pass; the solution suite is now 345/345 green.

## 4. Bugs found

| Bug | Root cause | Fix |
|---|---|---|
| Far-jump bitfield corruption | Shifting a 32-bit field by a distance >=32 | Explicitly reset the field before shifting |
| Karn RTT skew | RTT sampled after resend | Only first-transmission ACKs update EWMA |
| Ordered payload corruption risk | Retaining callback memory in a gap buffer | Copy only packets that wait; return pooled memory in `finally` |
| Initial ACK ambiguity | Handshake packets were not represented in receive history | Client records valid challenge/accepted sequence before data starts |
| Lost `CONNECT_ACCEPTED` | Server discarded challenge state before the client could retry its response | Keep a bounded endpoint-bound accepted-handshake replay record until the first authenticated packet |

## 5. Security/impact

Challenge state is endpoint-bound and ticket validation happens before a live connection is
allocated. Connection IDs are checked on every post-handshake packet. Fragment groups are capped
at eight per connection and expire after the protocol's two seconds. Unknown endpoints and bad IDs
are dropped without response. Existing shared protocol wire bytes were not changed.

## 6. Handoff

The 16/64-connection benchmark and soak are recorded in the phase-02 report. Remaining work is
limited to the explicit phase-00 external warm-up gaps and the phase-02 protocol/profiling gaps.
