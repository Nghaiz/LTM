# Dev B — Phase 01: Reliability layer

**Weeks 3–6** · Milestone **M1 (the make-or-break milestone)** · Estimate **4.0 person-weeks**

> Goal in one sentence: **important packets always arrive, stale packets are dropped at the right
> moment, and both remain true at 30% packet loss.**

This is the academic core of the capstone. Do it thoroughly.

---

## 1. Objectives

| # | Objective |
|---|---|
| 1 | A 4-step handshake resistant to IP spoofing |
| 2 | Sequence + ack + 32-bit ack bitfield |
| 3 | Retransmitting unacked reliable packets |
| 4 | 4 channels with distinct semantics |
| 5 | Fragmentation / reassembly with DoS protection |
| 6 | RTT estimation (EWMA) + jitter |
| 7 | Keep-alive, timeout, clean disconnect |
| 8 | **≥40 unit tests** |

---

## 2. Detailed tasks

### Task 1 — Handshake and the `Connection` state machine (3 days)

Per [`protocol-spec.md § 3.1`](../../00-shared/protocol-spec.md#31-handshake).

```csharp
public sealed class Connection
{
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ushort   ConnectionId { get; internal set; }
    public EndPoint RemoteEndPoint { get; }

    private ulong _clientSalt, _serverSalt;
    private double _lastSendMs, _lastRecvMs;
    private int    _connectAttempts;

    public void Update(double nowMs)
    {
        switch (State)
        {
            case ConnectionState.Connecting:
                if (nowMs - _lastSendMs > 250)          // retry every 250ms
                {
                    if (++_connectAttempts > 20)         // 5 seconds
                    { Fail(DisconnectReason.Timeout); return; }
                    SendConnectRequest(nowMs);
                }
                break;

            case ConnectionState.Connected:
                if (nowMs - _lastRecvMs > ProtocolConstants.TIMEOUT_MS)
                { Fail(DisconnectReason.Timeout); return; }
                if (nowMs - _lastSendMs > ProtocolConstants.KEEPALIVE_MS)
                    SendKeepAlive(nowMs);
                _reliability.Update(nowMs);              // retransmit
                break;
        }
    }
}
```

**Why challenge–response:** it prevents *IP-spoofing amplification*. An attacker sends a
`CONNECT_REQUEST` with the victim's IP as the source. If the server allocated resources immediately,
it would both waste RAM and send data to the victim (amplifying the attack). With a challenge, the
server **stores nothing** until the client proves it received the `serverSalt` — something an IP
spoofer cannot do.

**An important detail:** at the `CONNECT_CHALLENGE` step, the server does **not** create a
`Connection` object. It computes `serverSalt = HMAC(clientEndpoint + clientSalt, serverSecret)` —
stateless, costing no memory. It only allocates once a correct `CONNECT_RESPONSE` arrives. This is
the *SYN cookie* idea from TCP, applied here.

**Clean disconnect:** send `DISCONNECT` **3 times** (unreliably, since we're closing). The other side
only needs 1 of the 3. If all 3 are lost, the peer times out after 10 seconds — still correct, just
slower.

### Task 2 — `ReliabilityLayer`: sequence, ack, bitfield (4 days)

The heart of the transport layer.

```csharp
public sealed class ReliabilityLayer
{
    private const int SENT_BUFFER_SIZE = 1024;      // history of sent packets

    private struct SentPacket
    {
        public ushort Sequence;
        public double SentAtMs;
        public bool   Acked;
        public bool   IsReliable;
        public byte[] Data;        // null if unreliable (no need to keep it for resending)
        public int    Length;
        public int    ResendCount;
    }

    private readonly SentPacket[] _sent = new SentPacket[SENT_BUFFER_SIZE];
    private ushort _localSequence;          // the seq of the next packet WE send
    private ushort _remoteSequence;         // the highest seq WE have received from them
    private uint   _receivedBitfield;       // the 32 packets before _remoteSequence

    // ===== SENDING =====
    public ushort NextSequence() => _localSequence++;

    public void OnPacketSent(ushort seq, ReadOnlySpan<byte> data, bool reliable, double nowMs)
    {
        int i = seq % SENT_BUFFER_SIZE;
        if (_sent[i].Data != null) _pool.Return(_sent[i].Data);   // overwriting an old slot
        byte[] copy = null;
        if (reliable) { copy = _pool.Rent(); data.CopyTo(copy); }
        _sent[i] = new SentPacket {
            Sequence = seq, SentAtMs = nowMs, Acked = false,
            IsReliable = reliable, Data = copy, Length = data.Length, ResendCount = 0 };
    }

    // ===== RECEIVING =====
    public void OnPacketReceived(ushort seq)
    {
        if (SequenceMath.IsNewer(seq, _remoteSequence))
        {
            int shift = SequenceMath.Distance(seq, _remoteSequence);
            _receivedBitfield = shift >= 32 ? 0u : (_receivedBitfield << shift);
            _receivedBitfield |= 1u << (shift - 1);      // the old seq now sits at bit (shift-1)
            _remoteSequence = seq;
        }
        else
        {
            int diff = SequenceMath.Distance(_remoteSequence, seq);
            if (diff >= 1 && diff <= 32)
                _receivedBitfield |= 1u << (diff - 1);   // a late arrival, mark it received
            // diff > 32: too old, ignore (outside the window)
        }
    }

    public (ushort ack, uint bitfield) BuildAck() => (_remoteSequence, _receivedBitfield);

    // ===== PROCESSING THE PEER'S ACKS =====
    public void ProcessIncomingAck(ushort ack, uint bitfield, double nowMs)
    {
        AckPacket(ack, nowMs);
        for (int bit = 0; bit < 32; bit++)
            if ((bitfield & (1u << bit)) != 0)
                AckPacket((ushort)(ack - 1 - bit), nowMs);
    }

    private void AckPacket(ushort seq, double nowMs)
    {
        int i = seq % SENT_BUFFER_SIZE;
        ref var p = ref _sent[i];
        if (p.Sequence != seq || p.Acked) return;        // slot overwritten, or a duplicate ack
        p.Acked = true;
        UpdateRtt(nowMs - p.SentAtMs);
        if (p.Data != null) { _pool.Return(p.Data); p.Data = null; }
    }

    // ===== RETRANSMIT =====
    public void Update(double nowMs, Action<byte[], int> resend)
    {
        double rto = Math.Clamp(SmoothedRttMs * 1.5 + 4 * JitterMs, 30, 1000);
        for (int i = 0; i < SENT_BUFFER_SIZE; i++)
        {
            ref var p = ref _sent[i];
            if (p.Acked || !p.IsReliable || p.Data == null) continue;
            if (nowMs - p.SentAtMs < rto) continue;

            if (++p.ResendCount > 10)                    // 10 resends and still nothing
            { NetLog.Warn($"seq {p.Sequence} giving up after 10 attempts"); p.Acked = true;
              _pool.Return(p.Data); p.Data = null; continue; }

            resend(p.Data, p.Length);
            p.SentAtMs = nowMs;
            Stats.PacketsResent++;
        }
    }
}
```

**Trap 1 — shifting the bitfield when the sequence jumps far.** Receiving seq 200 while
`_remoteSequence` is 100 gives `shift = 100`. `_receivedBitfield << 100` is **undefined behavior** in
C# (in practice it shifts by `100 % 32 = 4` bits — completely wrong). You must check `shift >= 32` →
set 0. This bug is very hard to find because it only occurs after a temporary disconnect and
reconnect.

**Trap 2 — overwritten slots.** The buffer has 1024 slots indexed by `seq % 1024`. If you send 1025
packets while the first is still unacked, its slot is overwritten by the new packet → it's lost
forever and never retransmitted. At 30 packets/s, 1024 packets = 34 seconds. Safe. But you must
check `p.Sequence != seq` in `AckPacket` so you don't wrongly ack the newer packet.

**Trap 3 — too short an RTO causes a retransmit storm.** If `rto` is shorter than the true RTT, every
packet is resent before its ack can return → traffic doubles → more congestion → slower still. This
is *congestion collapse*. The `rtt * 1.5 + 4 * jitter` formula with a 30 ms floor is the safe level.

**Trap 4 — measuring RTT from a retransmitted packet.** If a packet was resent and an ack comes back,
you don't know which transmission it acknowledges → the RTT measurement is wrong (possibly negative
or wildly large). This is *Karn's algorithm*: **never update RTT from a retransmitted packet**.

```csharp
private void AckPacket(ushort seq, double nowMs)
{
    // ...
    if (p.ResendCount == 0)              // Karn's algorithm
        UpdateRtt(nowMs - p.SentAtMs);
}
```

### Task 3 — RTT and jitter estimation (1 day)

```csharp
public float SmoothedRttMs { get; private set; }
public float JitterMs      { get; private set; }

private void UpdateRtt(double sampleMs)
{
    if (SmoothedRttMs <= 0f) { SmoothedRttMs = (float)sampleMs; return; }   // first sample

    // EWMA, idea taken from RFC 6298
    float delta = (float)sampleMs - SmoothedRttMs;
    SmoothedRttMs += 0.125f * delta;                    // alpha = 1/8
    JitterMs      += 0.25f * (Math.Abs(delta) - JitterMs);  // beta = 1/4
}
```

Log these to `measurements.csv` once per second so you can chart them for the report.

### Task 4 — The four channels (3 days)

Per [`protocol-spec.md § 5`](../../00-shared/protocol-spec.md#5-channels).

```csharp
public abstract class Channel
{
    public byte Id { get; }
    public abstract void OnSend(ReadOnlySpan<byte> payload, PacketQueue queue);
    public abstract void OnReceive(ushort channelSeq, ReadOnlyMemory<byte> payload,
                                   Action<ReadOnlyMemory<byte>> deliver);
}

/// <summary>Channel 0: deliver immediately, ignoring order and duplicates.</summary>
public sealed class UnreliableUnsequencedChannel : Channel
{
    public override void OnReceive(ushort seq, ReadOnlyMemory<byte> p, Action<ReadOnlyMemory<byte>> d)
        => d(p);
}

/// <summary>Channels 1 and 3: deliver only if NEWER than the last delivered. Older packets are DROPPED.</summary>
public sealed class UnreliableSequencedChannel : Channel
{
    private ushort _lastDelivered;
    private bool   _hasDelivered;

    public override void OnReceive(ushort seq, ReadOnlyMemory<byte> p, Action<ReadOnlyMemory<byte>> d)
    {
        if (_hasDelivered && !SequenceMath.IsNewer(seq, _lastDelivered))
        { Stats.StalePacketsDropped++; return; }        // a snapshot older than what we have is worthless
        _lastDelivered = seq; _hasDelivered = true;
        d(p);
    }
}

/// <summary>Channel 2: deliver in order, losing nothing. Early arrivals wait in the buffer.</summary>
public sealed class ReliableOrderedChannel : Channel
{
    private const int WINDOW = 256;
    private readonly ReadOnlyMemory<byte>?[] _pending = new ReadOnlyMemory<byte>?[WINDOW];
    private ushort _nextExpected;

    public override void OnReceive(ushort seq, ReadOnlyMemory<byte> p, Action<ReadOnlyMemory<byte>> d)
    {
        if (!SequenceMath.IsNewer(seq, (ushort)(_nextExpected - 1))) return;   // duplicate, already delivered
        if (SequenceMath.Distance(seq, _nextExpected) >= WINDOW)
        { NetLog.Warn("packet beyond the reliable window, disconnecting"); return; }

        // The buffer must be COPIED — the original memory returns to the pool right after this method
        _pending[seq % WINDOW] = CopyToOwnedBuffer(p);

        while (_pending[_nextExpected % WINDOW] is { } ready)   // deliver consecutively
        {
            _pending[_nextExpected % WINDOW] = null;
            d(ready);
            ReturnOwnedBuffer(ready);
            _nextExpected++;
        }
    }
}
```

**Trap 5 — buffer ownership in the reliable-ordered channel.** Early arrivals have to wait, but the
`ReadOnlyMemory` points at a buffer that returns to the pool the moment the method returns. **You
must copy.** Forget it and after a few seconds you'll be delivering garbage. This is a very hard bug
to find, because it only occurs when packets are lost.

**Trap 6 — head-of-line blocking in channel 2 is DELIBERATE.** Don't "fix" it. If "actor 5 died"
arrives before "actor 5 spawned", processing them out of order corrupts the game state. That's
precisely why events use ordered delivery while snapshots use a different channel. **This is the core
argument in the comparison against TCP** — state it clearly in the report: TCP forces *everything*
into one stream; we get to choose per category.

### Task 5 — Fragmentation / reassembly (3 days)

Per [`protocol-spec.md § 6`](../../00-shared/protocol-spec.md#6-fragmentation).

```csharp
public sealed class FragmentAssembler
{
    private const int MAX_PENDING_GROUPS = 8;           // anti-DoS

    private sealed class Group
    {
        public ushort   GroupId;
        public byte     Count, Received;
        public double   FirstSeenMs;
        public byte[][] Parts;
        public int[]    Lengths;
    }

    private readonly Dictionary<ushort, Group> _groups = new();

    public bool TryReassemble(ushort groupId, byte fragIndex, byte fragCount,
                              ReadOnlySpan<byte> data, double nowMs, out byte[] full, out int len)
    {
        full = null; len = 0;
        if (fragCount == 0 || fragCount > ProtocolConstants.MAX_FRAGMENTS) return false;
        if (fragIndex >= fragCount) return false;

        if (!_groups.TryGetValue(groupId, out var g))
        {
            if (_groups.Count >= MAX_PENDING_GROUPS) EvictOldest();   // prevent RAM exhaustion
            g = new Group { GroupId = groupId, Count = fragCount, FirstSeenMs = nowMs,
                            Parts = new byte[fragCount][], Lengths = new int[fragCount] };
            _groups[groupId] = g;
        }
        if (g.Count != fragCount) { _groups.Remove(groupId); return false; }  // inconsistent
        if (g.Parts[fragIndex] != null) return false;                        // duplicate fragment

        g.Parts[fragIndex] = _pool.Rent();
        data.CopyTo(g.Parts[fragIndex]);
        g.Lengths[fragIndex] = data.Length;
        g.Received++;

        if (g.Received < g.Count) return false;
        // All fragments present → reassemble
        len = g.Lengths.Sum();
        full = new byte[len];                     // large and rare, so allocating is acceptable
        int off = 0;
        for (int i = 0; i < g.Count; i++)
        { Array.Copy(g.Parts[i], 0, full, off, g.Lengths[i]); off += g.Lengths[i];
          _pool.Return(g.Parts[i]); }
        _groups.Remove(groupId);
        return true;
    }

    public void Update(double nowMs)
    {
        foreach (var (id, g) in _groups.ToList())
            if (nowMs - g.FirstSeenMs > ProtocolConstants.FRAGMENT_TIMEOUT_MS)
            { ReturnParts(g); _groups.Remove(id); Stats.FragmentGroupsTimedOut++; }
    }
}
```

**Trap 7 — DoS via fragmentation.** An attacker sends thousands of packets with `fragmentCount = 64`
but only one fragment per group. Each group occupies 64 buffer slots. Without a limit, RAM is
exhausted in seconds. `MAX_PENDING_GROUPS = 8` plus a 2 s timeout is mandatory, not an optimization.

**Trap 8 — fragments must be reliable.** Sent unreliably, losing 1 fragment loses the whole 64-piece
group. At 5% loss with 20 fragments, the chance of losing at least one is `1 - 0.95^20 = 64%`.
Unacceptable.

### Task 6 — Unit tests (3 days) — ≥40 tests

| Group | Tests | Content |
|---|---|---|
| `SequenceMath` | 6 | Wrap boundaries: (0,65535), (65535,0), (5,65530), (32768,0), equality, distance |
| `PacketHeader` | 8 | Round-trip, wrong protocolId, short buffer, wrong payloadLength, all boundary values |
| `ReliabilityLayer` — ack | 8 | In-order receipt, reordered, duplicated, jumps > 32, correct bitfield, acking multiple packets |
| `ReliabilityLayer` — resend | 6 | Resend after RTO, stop on ack, give up after 10 attempts, Karn's algorithm |
| Channels | 8 | 2 tests per channel: normal behavior + behavior under loss/reordering |
| Fragmentation | 6 | Correct reassembly, missing fragment, duplicate fragment, timeout, exceeding MAX_PENDING_GROUPS, inconsistent fragCount |
| Handshake | 4 | Success, wrong challenge, timeout, server full |
| **Total** | **46** | |

**The single most important test — the bitfield on a far jump:**

```csharp
[Fact]
public void AckBitfield_WhenSequenceJumpsMoreThan32_MustReset()
{
    var r = new ReliabilityLayer();
    r.OnPacketReceived(1);
    r.OnPacketReceived(2);
    r.OnPacketReceived(3);
    var (ack1, bits1) = r.BuildAck();
    Assert.Equal(3, ack1);
    Assert.Equal(0b11u, bits1);            // received 2 and 1

    r.OnPacketReceived(200);               // a jump of 197 — beyond the 32-packet window
    var (ack2, bits2) = r.BuildAck();
    Assert.Equal(200, ack2);
    Assert.Equal(0u, bits2);               // MUST reset, not garbage from an undefined shift
}

[Fact]
public void SequenceMath_AcrossTheWrapBoundary_MustBeCorrect()
{
    Assert.True (SequenceMath.IsNewer(0, 65535));      // 0 is newer than 65535 (just wrapped)
    Assert.False(SequenceMath.IsNewer(65535, 0));
    Assert.True (SequenceMath.IsNewer(5, 65530));
    Assert.Equal(6, SequenceMath.Distance(5, 65535));
}
```

---

## 3. Acceptance criteria (M1)

| # | Criterion | How to verify |
|---|---|---|
| 1 | ≥40 unit tests green | `dotnet test` |
| 2 | The 4-step handshake works and resists spoofing | Test: a `CONNECT_RESPONSE` with the wrong salt → rejected |
| 3 | Reliable packets **always** arrive at 30% loss | Test: send 1000 reliable packets through the simulator at 30% loss → all 1000 received, in order |
| 4 | Unreliable-sequenced **drops stale packets** | Test: send seq 1,2,3 but receive in order 3,1,2 → only 3 is delivered |
| 5 | Fragmentation correctly reassembles a 20 KB message | Byte-by-byte round-trip test |
| 6 | Measured RTT is within 10% of the simulator's latency | Sim at 100 ms → measures 95–110 ms |
| 7 | Karn's algorithm: RTT isn't skewed by retransmits | A test that verifies it |
| 8 | A connection survives 30 minutes without dropping or leaking | Long run, watching that `BufferPool.RentedCount` doesn't grow |
| 9 | **A sequence wrap after 36 minutes causes no errors** | Test: drive the sequence from 65500 to 100 and check continuous operation |
| 10 | 0 heap allocations in the hot path | Benchmark counting gen0 GC collections |
| 11 | Integration: A can run 2 clients that see each other | Confirmed jointly with A |

---

## 4. Risks

| Risk | Sign | Handling |
|---|---|---|
| The far-jump bitfield bug | Randomly wrong acks after a network interruption | Test #1 above. Check `shift >= 32` |
| Retransmit storm | Bandwidth spikes, RTT climbs steadily | RTO has a 30 ms floor, Karn's algorithm |
| Buffer leak | `RentedCount` grows over time | Log `RentedCount` every 10 seconds. Investigate the moment it climbs |
| Garbage data on reliable-ordered | Messages have strange contents when packets are lost | You forgot to copy the buffer (trap 5). Enable the `0xDD` fill in Debug |
| Week 6 arrives unfinished | | Contingency: drop fragmentation (cap messages at ≤ 1184 B, C has to split snapshots). Saves 3 days |

---

## 5. Data that must be collected for the report

Run the following matrix, 60 seconds per cell, recording into `reports/measurements.csv`:

| Loss | Reliable throughput | Retransmit % | Mean delivery latency (ordered) | P99 delivery latency |
|---|---|---|---|---|
| 0% | | | | |
| 5% | | | | |
| 15% | | | | |
| 30% | | | | |

**Also measure TCP for comparison** (reuse the warm-up exercise code): under identical conditions,
what's TCP's P99 latency? This is the most important chart in the report — it proves why games use
UDP.
