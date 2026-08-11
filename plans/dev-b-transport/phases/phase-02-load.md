# Dev B — Phase 02: Load handling, congestion control, DoS defenses

**Weeks 7–10** · Milestone **M2** · Estimate **3.0 person-weeks**

> Goal in one sentence: **16 simultaneous connections running stably, degrading in a controlled way
> when the network is bad, and not falling over when flooded with junk.**

---

## 1. Objectives

| # | Objective |
|---|---|
| 1 | The server handles 16 simultaneous connections, measurably |
| 2 | Congestion control: reduce load automatically when the network degrades |
| 3 | Flow control: don't overwhelm the receiver |
| 4 | DoS defenses: rate limiting, flood protection, anti-amplification |
| 5 | A full benchmark suite, with data for the report |
| 6 | Support A and C through combat integration |

---

## 2. Detailed tasks

### Task 1 — Multi-connection server (3 days)

`UdpPeer` currently handles raw datagrams. Now it has to route them to the right `Connection`.

```csharp
public sealed class UdpTransportServer : ITransportServer
{
    private readonly Dictionary<EndPoint, Connection> _byEndpoint = new();
    private readonly Connection[] _byId = new Connection[ProtocolConstants.MAX_PLAYERS + 8];
    private readonly Queue<ushort> _freeIds = new();

    private void Dispatch(in PacketHeader h, ReadOnlySpan<byte> payload, EndPoint from)
    {
        // Handshake packets: handled separately, there's no connectionId yet
        if (h.PacketType <= PacketType.CONNECT_DENIED)
        { HandleHandshake(h, payload, from); return; }

        if (!_byEndpoint.TryGetValue(from, out var conn))
        { Stats.PacketsFromUnknown++; return; }        // drop silently

        // Check the connectionId matches — stops someone spoofing from a different IP
        if (h.ConnectionId != conn.ConnectionId)
        { Stats.PacketsWithBadConnId++; return; }

        conn.OnPacketReceived(h, payload);
    }
}
```

**Trap 1 — `EndPoint` lookups are slow.** `IPEndPoint` doesn't override `GetHashCode` efficiently in
some .NET versions, and every `ReceiveFrom` allocates a fresh `IPEndPoint` → GC pressure. The fix:
use a struct key `(uint ipv4, ushort port)`:

```csharp
private readonly struct EndpointKey : IEquatable<EndpointKey>
{
    public readonly uint   Address;
    public readonly ushort Port;
    public bool Equals(EndpointKey o) => Address == o.Address && Port == o.Port;
    public override int GetHashCode() => (int)(Address * 397) ^ Port;
}
```

And use `ReceiveFromInto` with a `SocketAddress` to avoid allocating (or reuse a single
`IPEndPoint` — `ReceiveFrom` writes into it).

**Trap 2 — NAT rebinding.** A client behind NAT can change its source port mid-session (the router's
mapping times out). Packets then arrive from a new endpoint, `_byEndpoint` doesn't find it → the
client is treated as disconnected even though they're still playing.

Handling: if a packet has a valid `connectionId` but an unfamiliar endpoint, **and** it passes a
light challenge (checking a token agreed during the handshake), update the endpoint. Without that
anti-spoofing step, an attacker who learns a `connectionId` can hijack the connection.

```csharp
// Simple and secure enough for this scope: require the packet to carry the challengeToken
if (h.ConnectionId < _byId.Length && _byId[h.ConnectionId] is { } c
    && payload.StartsWith(c.RebindToken))
{
    _byEndpoint.Remove(c.RemoteEndPointKey);
    c.UpdateEndpoint(from);
    _byEndpoint[new EndpointKey(from)] = c;
    NetLog.Warn($"conn {h.ConnectionId} rebound its endpoint (NAT)");
}
```

### Task 2 — Congestion control (3 days)

Per [`protocol-spec.md § 8`](../../00-shared/protocol-spec.md#8-congestion-control).

We're not implementing full TCP-style AIMD (complex, and we're not competing fairly against other
TCP flows at this scope). We use a **two-mode model with hysteresis** — simple, easy to explain,
easy to measure, and good enough.

```csharp
public sealed class CongestionControl
{
    public enum Mode { Good, Bad }
    public Mode CurrentMode { get; private set; } = Mode.Good;

    private const float RTT_THRESHOLD_TO_BAD  = 250f;
    private const float RTT_THRESHOLD_TO_GOOD = 200f;   // 50ms of hysteresis
    private const float MIN_BAD_DURATION_S    = 10f;
    private const float GOOD_STREAK_TO_SHRINK = 10f;    // reward for staying stable

    private float _badTimer, _goodStreak;

    public void Update(float dt, float smoothedRttMs)
    {
        if (CurrentMode == Mode.Good)
        {
            if (smoothedRttMs > RTT_THRESHOLD_TO_BAD)
            {
                CurrentMode = Mode.Bad;
                _badTimer = MIN_BAD_DURATION_S;
                // Reward/penalty: if we were only briefly in Good, extend the penalty
                if (_goodStreak < GOOD_STREAK_TO_SHRINK) _badTimer *= 2f;
                _goodStreak = 0f;
                NetLog.Warn($"congestion → BAD (rtt {smoothedRttMs:F0}ms)");
            }
            else _goodStreak += dt;
        }
        else
        {
            _badTimer -= dt;
            if (_badTimer <= 0f && smoothedRttMs < RTT_THRESHOLD_TO_GOOD)
            { CurrentMode = Mode.Good; NetLog.Warn("congestion → GOOD"); }
        }
    }

    /// <summary>The snapshot rate the layer above should use.</summary>
    public int RecommendedSendRateHz => CurrentMode == Mode.Good ? 20 : 10;
    public bool ShouldReduceDetail   => CurrentMode == Mode.Bad;
}
```

**Why hysteresis:** with a single threshold in both directions, RTT hovering around 250 ms makes the
system flip Good↔Bad several times a second, which destabilizes things worse than the congestion
itself. A 50 ms dead band plus a 10 s minimum in BAD eliminates it.

**Why the `_goodStreak` escalating penalty:** if we return to Good for only 2 seconds and then fall
back to Bad, the network really is bad rather than momentarily noisy → double the penalty duration.
The idea comes from TCP exponential backoff.

**How the layer above consumes it:** C reads `RecommendedSendRateHz` to lower the snapshot rate, and
`ShouldReduceDetail` to drop the velocity field and tighten the cull threshold. You only
*recommend*; you never decide the content — that's C's job.

### Task 3 — Flow control (2 days)

Congestion control is about the *network*; flow control is about the *receiver*. If the client
processes slowly (weak machine, loading a scene), the server sends faster than the client consumes →
buffers fill → packets are lost.

```csharp
// The receiver reports its headroom in the keepalive
public struct FlowControlInfo
{
    public ushort PendingReliableCount;   // reliable packets awaiting processing
    public byte   BufferPressurePercent;  // 0-100
}

// The sender reacts
if (remoteFlowInfo.BufferPressurePercent > 80)
{
    // Stop sending new reliables, keep only retransmits
    _pauseNewReliable = true;
}
```

A simpler approach that's good enough: **cap the number of unacked reliable packets**. Above 64
unacked, stop sending new reliables until it clears.

```csharp
public bool CanSendReliable => _unackedReliableCount < MAX_UNACKED_RELIABLE;  // 64
```

That's the classic *sliding window* — state it explicitly in the report.

### Task 4 — DoS defenses (3 days)

A public server on a VPS will be port-scanned and flooded with junk within the first few hours. The
must-do list:

| Attack vector | Defense |
|---|---|
| Junk packets with the wrong protocolId | Already handled: dropped silently in `PacketHeader.TryRead` |
| CONNECT_REQUEST flood from spoofed IPs | Stateless challenge–response (phase 01). The server allocates nothing before step 3 |
| CONNECT_REQUEST flood from real IPs | Per-IP rate limit: max 5 requests/second/IP |
| Large-packet flood to saturate bandwidth | Not preventable at the application layer. Note it; needs a firewall/cloud |
| Fragmentation bomb | Already handled: `MAX_PENDING_GROUPS = 8` + timeout |
| Amplification (small request, large reply) | Handshake responses are **always ≤** the request. `CONNECT_REQUEST` is padded to ≥ 200 bytes so the amplification ratio stays < 1 |
| Packets with a fake oversized payloadLength | Already checked in `TryRead` |
| Connect then go silent (slowloris) | 10-second timeout |
| The same playerId connecting repeatedly | Checked in `OnValidateTicket`, rejected with code 6 |

```csharp
public sealed class RateLimiter
{
    private readonly Dictionary<uint, (double windowStartMs, int count)> _byIp = new();
    private const int MAX_PER_SECOND = 5;

    public bool Allow(uint ipv4, double nowMs)
    {
        if (!_byIp.TryGetValue(ipv4, out var e) || nowMs - e.windowStartMs > 1000)
        { _byIp[ipv4] = (nowMs, 1); return true; }
        if (e.count >= MAX_PER_SECOND) { Stats.RateLimited++; return false; }
        _byIp[ipv4] = (e.windowStartMs, e.count + 1);
        return true;
    }

    /// <summary>Call every 10 seconds to purge old entries — otherwise the dictionary grows without bound.</summary>
    public void Cleanup(double nowMs) { /* ... */ }
}
```

> **Trap 3 — the rate limiter is itself a DoS vector.** If you create a dictionary entry per IP and
> never clean up, an attacker sending from a million spoofed IPs exhausts RAM. A periodic
> `Cleanup()` plus a cap on total entries (say 10,000, above which you drop the oldest half) is
> mandatory.

**Amplification — the concrete arithmetic.** `CONNECT_REQUEST` carries a 64-byte joinTicket + a
16-byte header = 80 bytes. `CONNECT_CHALLENGE` carries an 8-byte serverSalt + a 16-byte header =
24 bytes. The ratio 24/80 = 0.3 — safe (< 1). If it were the other way round (small request, large
reply), the server becomes a DDoS amplifier aimed at someone else. **Always check this ratio for
every packet processed before authentication.**

### Task 5 — Benchmarks (2 days)

```csharp
// Ironfront.Net.Transport.Bench/Program.cs — using BenchmarkDotNet or hand-rolled
```

| Benchmark | Measures | Acceptable threshold |
|---|---|---|
| Header parsing | ns/packet | < 50 ns |
| Reliability `OnPacketReceived` | ns/packet | < 100 ns |
| Full send path (one 200 B packet) | ns | < 2 µs |
| Full receive path | ns | < 2 µs |
| 16 conns × 30 packets/s for 60 s | CPU %, allocations | < 5% of one core, 0 alloc/s once warm |
| Max throughput on one connection | MB/s | > 10 MB/s on localhost |
| Max connections before tick > 5 ms | conns | ≥ 64 |

**Measure the connection ceiling even though we only need 16** — knowing the headroom is good data
for the report, and it answers the "how far does your system scale?" question at the defense.

---

## 3. Acceptance criteria (M2)

| # | Criterion | How to verify |
|---|---|---|
| 1 | 16 simultaneous connections for 10 minutes with no drops | D's load-test bots |
| 2 | 64 connections still work (headroom) | Load test |
| 3 | Congestion moves GOOD→BAD→GOOD correctly without oscillating | Test: ramp the simulated latency up, log the mode changes |
| 4 | Flow control kicks in when the receiver is slow | Test: make the receiver sleep 200 ms/frame deliberately |
| 5 | Rate limiting blocks a 100 req/s flood from one IP | Test |
| 6 | A fragmentation bomb doesn't exhaust RAM | Test: 1000 incomplete fragment groups → RAM stays flat |
| 7 | Amplification ratio < 1 for every pre-authentication packet | Manual check + test |
| 8 | 0 alloc/s once warm, at 16 conns | Benchmark GC counters |
| 9 | CPU < 5% of one core at 16 conns | Benchmark |
| 10 | ≥ 60 tests total, all green | `dotnet test` |

---

## 4. Risks

| Risk | Sign | Handling |
|---|---|---|
| NAT rebinding dropping clients over the Internet | Clients randomly disconnect after a few minutes over the VPS | Task 1, trap 2. It only surfaces in phase 03 on the VPS — build it now |
| The rate limiter becomes a DoS vector itself | RAM grows while being scanned | `Cleanup()` + an entry cap |
| Congestion oscillating Good↔Bad | The log fills with mode-change lines | Hysteresis + a minimum dwell time |
| `IPEndPoint` allocating per packet | Steadily rising gen0 GCs | The `EndpointKey` struct, reuse an `IPEndPoint` |
| Week 10 arrives unfinished | | Contingency: drop flow control (keep only the 64-unacked cap), drop NAT rebinding (accept drops over the Internet) |

---

## 5. Data for the report

| Experiment | Table/chart needed |
|---|---|
| Congestion control on vs. off at 20% loss | RTT over time, two series |
| Head-of-line: channel 2 vs. sending everything reliable-ordered | P99 snapshot latency when an event is lost |
| Ack bitfield vs. single ack | Redundant retransmit rate, % bandwidth saved |
| Scaling: 1 → 64 connections | CPU and tick time vs. connection count |
