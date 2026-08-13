# Report — Phase 03: Operations and diagnostics

- **Status:** Code and local verification complete; VPS/Unity evidence pending external integration
- **Scope:** packet capture/replay, runtime metrics, operational hardening

## Delivered

- `PacketLogger` writes versioned `IFPC` captures with direction, monotonic timestamp, IPv4/port,
  length and raw datagram bytes.
- `PacketCaptureReader` validates the header/version and rejects truncated or oversized records.
- `Ironfront.Tools.PacketReplay` supports replay filtering and `--analyze` sequence-gap,
  retransmit and ACK-correlated RTT summaries.
- `TransportStats` now exposes byte rates, five-second sent/received loss estimates, congestion
  mode, pending fragments and pool rentals.
- `UdpPeer` logs at the socket boundary only when `IRONFRONT_PCAP` or an injected logger is used.
- Reliability slot collisions fail loudly; challenge retries, endpoint reconnect denials and
  CONNECT_ACCEPTED metadata are explicit.

## Local evidence

```text
Ironfront.Net.Transport.Tests: 83 passed, 0 failed
PacketLoggerTests: capture round-trip, malformed capture cleanup and UdpPeer boundary logging
ControlAndSocketTests: metadata and one-second diagnostic rate window
```

## Acceptance audit

| Criterion | Status | Evidence / blocker |
|---|---|---|
| 16 clients over VPS for 10 minutes | Pending | No VPS endpoint or approved game-server run in workspace |
| 5-minute idle NAT test | Pending | Requires real NAT path |
| Logger and replay round-trip | Met locally | `PacketLoggerTests`, `PacketReplay` Release build |
| Analyzer correctness | Partially met | Parser and deterministic calculations are implemented; needs a captured production session cross-check |
| LAN/VPS measurement table | Pending | LAN benchmark exists; VPS columns require D/integration machine |
| A's F3 overlay | API ready, integration pending | `ITransportClient.Stats` contains all requested metrics; no Unity screenshot available |
| 2-hour/overnight pool soak | Pending | Must run with external 16-client bots and periodic log collection |

## Security and operational notes

Capture files may contain join tickets and gameplay payloads. They are diagnostic artifacts, not
source files, and must stay under ignored `artifacts/`. The logger is disabled by default. The
transport continues to reject malformed traffic and keeps the receive budget/rate limits active
while diagnostics are enabled.
