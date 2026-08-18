# Report — Phase 00: Foundation and Network Simulator

- **Author:** the transport track (Transport)
- **Date:** 2026-08-12
- **Week:** 1–2
- **Phase:** `phase-00-foundation`
- **Status:** Partially done — implementation complete; external TCP/Wireshark warm-ups pending

## 1. Summary

Phase 00 now has the non-blocking raw UDP peer, pooled buffers, deterministic five-impairment
simulator, loopback transport already present in the repository, bit serializer already present,
and the public client/server transport API. Invalid datagrams are silently dropped, Windows UDP
ICMP reset handling is installed, and callback memory ownership is documented. The only unmet
items are environment measurements requiring `clumsy`/`tc netem`, Wireshark, and a real network.

## 2. Acceptance criteria

| # | Criterion | Met | Evidence |
|---|---|---|---|
| 1 | Warm-up exercises and commentary | Partial | [warmup-udp-vs-tcp.md](warmup-udp-vs-tcp.md); TCP induced-loss/Wireshark unavailable on this host |
| 2 | Clean build, 0 warnings | Yes | `dotnet build Ironfront.sln -c Release` |
| 3 | Header/serializer tests | Yes | Existing protocol conformance: 160 tests; B bit-stream tests green |
| 4 | Warm pool does not allocate | Yes | `BufferPoolDoesNotGrowAfterItIsWarm`, `GrewCount=0` |
| 5 | Reproducible simulator | Yes | Existing seeded simulator tests |
| 6 | Five impairments | Yes | Existing simulator loss/latency/jitter/duplicate/reorder tests |
| 7 | 10,000 localhost datagrams | Yes | `UdpPeerTests.LocalhostCarriesTenThousandRawDatagramsWithoutLoss` |
| 8 | Windows ICMP reset mitigation | Yes | `DisableIcmpPortUnreachable()` in `UdpPeer` |
| 9 | Loopback handoff | Yes | Existing loopback tests and replication integration tests |
| 10 | Frozen API/XML ownership docs | Yes | `ITransport.cs`, `UdpTransportClient`, `UdpTransportServer` |

## 3. Test result

The full solution result after implementation was 345 passing tests: 160 protocol, 137 replication,
and 48 transport. No failures or skips.

## 4. Measurements

See [measurements.csv](measurements.csv). Header parsing measured 3.5–3.9 ns/op with 0 B/op in
Release; 100,000 pool rent/return operations grew the pool zero times.

## 5. Technical decisions

| Problem | Chosen | Reason |
|---|---|---|
| Socket loop | One non-blocking polling thread | Matches B-AD-1 and avoids races |
| Simulator destination | Generic destination type | Reuses one deterministic simulator for UDP and loopback without endpoint boxing |
| Raw peer memory | Fixed pool plus callback-scoped ownership | Avoids hot-path allocations and makes lifetime explicit |
| Invalid packet response | Silent drop | Prevents port-scan amplification |

## 6. Failed approaches

The first raw integration test sent to `0.0.0.0`, the bind address, and received zero packets.
The test was corrected to send to `127.0.0.1`; this was an endpoint-test error, not a socket
implementation failure.

## 7. Security/impact

Malformed header lengths are rejected by the frozen protocol parser before payload slicing. The
peer never replies to malformed traffic. Buffers are returned in `finally` blocks, and Debug builds
fill returned buffers with `0xDD` to expose lifetime violations. No existing Protocol or Replication
files were changed.

## 8. Pending external work

TCP loss comparison, Wireshark capture, and Internet/VPS measurements require D/integration-machine
support and must not be fabricated locally.
