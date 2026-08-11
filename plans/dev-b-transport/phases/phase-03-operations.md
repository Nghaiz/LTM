# Dev B — Phase 03: Real-world operation and diagnostic tooling

**Weeks 11–13** · Milestone **M3** · Estimate **2.5 person-weeks**

> Goal in one sentence: **the transport works over the real Internet, and when something goes wrong
> we know why.**

This is the phase where every assumption about the network meets reality. A LAN hides a great deal.

---

## 1. Objectives

| # | Objective |
|---|---|
| 1 | Works over the real Internet (a VPS), with real measurements |
| 2 | A packet logger + offline replay |
| 3 | Real-time diagnostics for A to display (the F3 overlay) |
| 4 | Handle the problems that only appear on the Internet: NAT, real MTU, real jitter |
| 5 | Support M3 integration |

---

## 2. Detailed tasks

### Task 1 — Packet logger and replay (3 days)

The single most valuable tool for debugging netcode: record every packet and replay it offline to
reproduce bugs.

```csharp
// Ironfront.Net.Transport/Diagnostics/PacketLogger.cs
public sealed class PacketLogger : IDisposable
{
    // The .ifpcap format — binary and simple
    // File header: magic "IFPC" (4B) + version u16 + startUnixMs u64
    // Each record:  direction u8 (0=recv,1=send) + timestampMs u32 (since start)
    //             + endpoint (u32 ip + u16 port) + length u16 + data[length]

    private readonly BinaryWriter _w;
    private readonly double _startMs;

    public void Log(bool outgoing, ReadOnlySpan<byte> data, EndpointKey ep, double nowMs)
    {
        if (_w == null) return;
        _w.Write((byte)(outgoing ? 1 : 0));
        _w.Write((uint)(nowMs - _startMs));
        _w.Write(ep.Address); _w.Write(ep.Port);
        _w.Write((ushort)data.Length);
        _w.Write(data);
    }
}
```

**Enabled via an environment variable** so it costs nothing when off:
```
IRONFRONT_PCAP=session-2026-xx-xx.ifpcap
```

**The replay tool** — more important than the logger itself:

```csharp
// Ironfront.Tools.PacketReplay/Program.cs
// Reads an .ifpcap file, replays it through ReliabilityLayer, prints what happened
//   dotnet run -- session.ifpcap --filter conn=3 --from 12000 --to 15000
// Output:
//   [12043ms] RECV seq=1042 ack=998  bits=0xFFFFFFFE  ch=1 len=512
//   [12045ms] SEND seq= 891 ack=1042 bits=0xFFFFFFFF  ch=3 len=29
//   [12078ms] !! seq 1043 MISSING (gap)
//   [12310ms] !! RESEND seq=889 (attempt 2, rto=145ms)
```

The practical value: when A reports "the game stuttered at 15:32", you open the pcap, jump to that
point, and immediately see "8 consecutive packets lost, 3 retransmits". Without this tool, all you
can do is guess.

**Add an automatic analysis mode:**
```
dotnet run -- session.ifpcap --analyze
# Summary:
#   Duration: 312s
#   Packets sent: 9,360   received: 9,102   lost (estimated): 2.76%
#   Longest loss burst: 11 packets (at 187.4s)
#   Retransmits: 258 (2.76%)   redundant (packet arrived but the ack was lost): 12
#   RTT: min 42ms  avg 87ms  p95 143ms  p99 211ms  max 890ms
#   Congestion mode changes: 4
```

### Task 2 — Real-time metrics for A (1 day)

A needs data for the F3 debug overlay. Supply it through the existing `TransportStats`, extended
with:

```csharp
public struct TransportStats
{
    // ... the existing fields
    public float BytesPerSecondSent, BytesPerSecondReceived;
    public float PacketLossPercentSent;      // estimated from acks that never arrive
    public float PacketLossPercentReceived;  // estimated from gaps in the sequence
    public int   CongestionMode;             // 0=Good 1=Bad
    public int   PendingFragmentGroups;
    public int   BufferPoolRented;           // for leak monitoring
}
```

**How to estimate packet loss in both directions:**
- **Sent (upstream):** count reliable packets that needed retransmitting / total reliable packets
  sent
- **Received (downstream):** count gaps in the received sequence over the last 5 seconds

Both are *estimates*; say so in the docs so A doesn't read them as exact figures.

### Task 3 — VPS deployment and real measurements (3 days)

Coordinate with D (D owns the VPS infrastructure).

**Pre-flight checklist before going to the VPS:**
- [ ] The firewall opens the right UDP port (27015 by default)
- [ ] The server binds `IPAddress.Any`, not `127.0.0.1`
- [ ] `SIO_UDP_CONNRESET` is disabled (if it's a Windows VPS)
- [ ] Debug logging is off (it would flood the disk)
- [ ] `IRONFRONT_SIM` is **off** (easy to forget, and it makes the measurements confusing)

**Problems that only appear on the Internet:**

| Problem | Symptom | Handling |
|---|---|---|
| Real MTU < 1500 | 1200-byte packets still get through (that's why we chose 1200), but if you'd ever raised it to 1400 they'd be silently dropped by some ISPs | Stay at 1200. MTU discovery could be added later; not needed at this scope |
| NAT timeout | A client goes silent for 30 s (sitting in a menu) and loses the connection | The 1 s keep-alive handles it. Verify by leaving a client idle for 5 minutes |
| NAT rebinding | Clients drop after a few minutes | Already handled in phase 02, task 1 |
| Real jitter far higher than on LAN | The 100 ms interpolation buffer may not be enough | Measure the real jitter and tell A. If p99 jitter > 80 ms, propose raising the buffer to 150 ms |
| ISPs blocking or throttling UDP | Some mobile ISPs deprioritize UDP | Note it; nothing can be done. Mention it in the report as a limitation |
| Asymmetric routing / asymmetric latency | RTT/2 doesn't equal the one-way delay | Affects lag compensation. Note it. Doing it properly needs clock synchronization (NTP-style), which is out of scope |

**Required measurements on the real VPS:**

| Metric | LAN | VPS (same city) | VPS (different region) |
|---|---|---|---|
| Mean RTT | | | |
| RTT p95 / p99 | | | |
| Mean jitter | | | |
| Real packet loss | | | |
| Longest loss burst | | | |
| Congestion mode (% of time in BAD) | | | |

This is the **most important experimental data** in the report. The simulator gives you control; the
VPS gives you authenticity. You need both.

### Task 4 — Integration support (2 days)

At M3, A and C will hit bugs and suspect the transport layer. Your job is to **quickly prove** where
the fault lies.

The standard procedure when a bug is reported:
1. Enable `IRONFRONT_PCAP` and reproduce it
2. Run `--analyze` to see whether the transport looks abnormal
3. If the transport is clean (low loss, no unusual retransmits, no gaps) → the bug is a layer up;
   hand the evidence to C or A
4. If the transport is abnormal → write a unit test that reproduces it, then fix it

**Don't accept blame without evidence, and don't deflect blame without checking.** The pcap file is
the referee.

---

## 3. Acceptance criteria (M3)

| # | Criterion | How to verify |
|---|---|---|
| 1 | 16 clients connect to the VPS over the Internet and play for 10 minutes | Server logs + video |
| 2 | A client idle for 5 minutes doesn't hit a NAT timeout | Manual test |
| 3 | The packet logger records and the replay tool reads it back correctly | Round-trip test |
| 4 | `--analyze` produces a correct report from a real file | Cross-check against the runtime figures |
| 5 | The LAN vs VPS measurement table is fully filled in | `reports/measurements.csv` |
| 6 | A can display every metric on the F3 overlay | Screenshot |
| 7 | 2 hours of continuous operation with `BufferPoolRented` flat | Periodic logging |

---

## 4. Risks

| Risk | Handling |
|---|---|
| The VPS isn't ready (depends on D) | Use a team member's machine + router port forwarding. Worse, but enough to surface the NAT problems |
| Internet jitter far exceeds expectations | Tell A to raise the interpolation buffer. It's good data for the report |
| Integration bugs blamed on the transport | Always have a pcap as evidence |
| Leaks that only surface after hours | Run an overnight soak test in week 12, not in week 14 |

---

## 5. Overnight soak test (mandatory in week 12)

Run 8 continuous hours with D's 16 bot clients, logging every minute:

```
timestamp, connCount, bufferPoolRented, rttAvg, lossPercent, gen0Collections, workingSetMB
```

Then chart it. **Any line that rises monotonically is a leak.** This is the only way to catch slow
leaks, and it's what separates code that runs from code that runs reliably.
