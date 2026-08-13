# Report — Phase 03: Operations and diagnostics

- **Status:** Code and local verification complete; VPS/NAT/soak and Unity runtime evidence intentionally deferred
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
- The benchmark can run an idle keep-alive soak and emit one-minute CSV rows with connection
  count, pool rentals, RTT, loss, Gen0 and working set (`--idle --report`).
- `TransportDiagnosticsFormatter` provides one stable, engine-free F3 text format, and
  `TransportDebugOverlay` provides an optional Unity `Shift+F3` binding surface without creating
  sockets or fabricating values while unbound.
- Reliability slot collisions fail loudly; challenge retries, endpoint reconnect denials and
  CONNECT_ACCEPTED metadata are explicit.

## Local evidence

```text
Ironfront.Net.Transport.Tests: 85 passed, 0 failed
PacketLoggerTests: capture round-trip, malformed capture cleanup and UdpPeer boundary logging
ControlAndSocketTests: metadata and one-second diagnostic rate window
```

## Acceptance audit

| Criterion | Status | Evidence / blocker |
|---|---|---|
| 16 clients over VPS for 10 minutes | Deferred | Requires the team's VPS/game-server run; no Internet result is claimed here |
| 5-minute idle NAT test | Deferred | Requires a real NAT path; localhost cannot prove this criterion |
| Logger and replay round-trip | Met locally | `PacketLoggerTests`, `PacketReplay` Release build |
| Analyzer correctness | Partially met | Parser and deterministic calculations are implemented; needs a captured production session cross-check |
| LAN/VPS measurement table | Deferred | Local benchmark exists; real LAN/VPS columns require the integration machine |
| A's F3 overlay | Source integration ready | `TransportDebugOverlay` binds `ITransportClient`; Unity Editor compile and screenshot remain external evidence |
| 2-hour/overnight pool soak | Deferred | Requires external 16-client bots and periodic log collection |

## Security and operational notes

Capture files may contain join tickets and gameplay payloads. They are diagnostic artifacts, not
source files, and must stay under ignored `artifacts/`. The logger is disabled by default. The
transport continues to reject malformed traffic and keeps the receive budget/rate limits active
while diagnostics are enabled.
