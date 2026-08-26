# Transport track — closed. The code shipped; the reports stay.

**Closed 2026-08-26.** `plan.md` and `phases/phase-03-operations.md` are deleted; everything under
[`reports/`](reports/) is kept and some of it is still cited.

## Why the plan is gone

`plan.md` was a **four-dev role document** — *"You write a reliable transport layer… game logic is
not your job, that's C's"*, with an ownership table granting rights per developer. The project went
single-owner in `899e75d` (#120), which deleted the structure the whole document was written for.
Its phase table listed `phase-00`…`phase-04`; only `phase-03` was ever authored as a file, and the
first sweep recorded in [`../00-shared/README.md`](../00-shared/README.md) removed 00–02 and 04 and
missed it.

## What shipped

`UdpPeer`, `UdpTransportClient`, `UdpTransportServer`, the reliability layer (sequence, ack,
bitfield, retransmit), four channels, fragmentation, RTT estimation, congestion and flow control,
`NetworkSimulator` with lan / typical / bad profiles, `BufferPool`, `BitWriter` / `BitReader`, a
packet logger with offline replay, and `TransportDiagnosticsFormatter`. **89 tests**, engine-free.

Every milestone has a closure report: M0 and M1 and M2 (2026-08-12), M3 (2026-08-13), M4
(2026-08-13/14). [`../00-shared/roadmap.md`](../00-shared/roadmap.md) § 1 records the outcome in one
line — *"UDP shipped… the 2026-08-13 critical path is closed."*

## What was deferred, and where each piece went

Phase 03's report closed with *"Code and local verification complete; VPS/NAT/soak and Unity runtime
evidence intentionally deferred"* and named four rows. **They were tracked in no live document** —
searched 2026-08-26 across every `*.md` under `plans/` and `docs/`, excluding this track: the
strings `VPS`, `NAT test`, `overnight soak` returned nothing that referred to them. Recorded here so
they stop being invisible:

| Deferred row | Where it belongs now |
|---|---|
| 16 clients over a VPS for 10 minutes | **V9.** [`../replication/phases/phase-v9-integration.md`](../replication/phases/phase-v9-integration.md) § "Verify" connects 16 clients and plays a full round — locally. The *VPS* half is the row below |
| 2-hour / overnight pool soak | **V9.** Its five-match soak with an audit captured at each reset, and a 17-hour run gated behind a `--smoke` pass per `preview-first-batch.md` |
| LAN / VPS measurement table | **Blocked on infrastructure, by the same thing that blocks the game server.** [`../debt-closure/plan.md`](../debt-closure/plan.md) § 4b: fly.io carries no UDP over public IPv6 and wants a bind to `fly-global-services`, while the design is IPv6-only and `UdpPeer.cs:92` binds `IPAddress.Any`; the Azure VM waits on ngtukien. Until one of those moves, there is no public UDP endpoint to measure against |
| 5-minute idle NAT test | Same blocker. A NAT path needs a host outside the LAN |

**Two of the four are V9's and two are infrastructure's.** Neither pair is code work, which is why
the track can close with them open — but "deferred in a report nobody re-reads" and "deferred with
an owner" are different states, and this table is the difference.

## Still cited, so do not delete

- [`reports/2026-08-14-phase-04-local-experiments.csv`](reports/2026-08-14-phase-04-local-experiments.csv)
  — cited by [`../../docs/transport-layer-report.md`](../../docs/transport-layer-report.md) as the
  source of its ACK-history and congestion-hysteresis evidence.
- [`reports/measurements.csv`](reports/measurements.csv) and
  [`reports/warmup-udp-vs-tcp.md`](reports/warmup-udp-vs-tcp.md) — the UDP-vs-TCP numbers the
  capstone's measurement chapter rests on.

## Recovering the deleted files

```
git show ce100d6:plans/transport/plan.md
git show ce100d6:plans/transport/phases/phase-03-operations.md
```
