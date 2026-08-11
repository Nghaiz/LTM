# Dev B — Phase 04: Report and defense

**Week 14** · Milestone **M4** · Estimate **1.0 person-week**

> Your part is the **academic centerpiece** of a Network Programming capstone. This week turns the
> data you've collected into an argument.

---

## 1. Tasks

### Task 1 — Complete the dataset (2 days)

Review `reports/measurements.csv` and fill in the empty cells. Six experiments must have complete
data:

#### Experiment 1 — Hand-written UDP vs TCP under packet loss

Identical conditions (same simulator, same seed, same data volume).

| Loss | TCP: p99 delivery latency | UDP+reliability: p99 (ch2) | UDP unreliable: p99 (ch1) |
|---|---|---|---|
| 0% | | | |
| 5% | | | |
| 15% | | | |
| 30% | | | |

**The claim to prove:** at the same loss rate, the unreliable channel (snapshots) has an essentially
flat p99 latency, while TCP's spikes. That is why games use UDP.

Say plainly that the converse is also true: our reliable-ordered channel has latency **comparable to
TCP** — because it solves the same problem. The strength isn't "UDP is faster than TCP", it's that
**we get to choose the guarantee per data type**, whereas TCP forces everything into one stream.

> This is the sharpest argument in the report. Don't say "UDP is faster than TCP" — that's a common
> misconception, and the marker will push back on it.

#### Experiment 2 — The effectiveness of the ack bitfield

| Mechanism | Ack bandwidth | Redundant retransmits | Loss-detection latency |
|---|---|---|---|
| Single ack (only acking the last packet) | | | |
| Ack + 32-bit bitfield | | | |

Implementation: add a config flag to disable the bitfield and re-run the same scenario.

**The claim:** the bitfield costs no extra packets (it's already in the header), yet eliminates
almost all redundant retransmits. Four bytes per packet buys a substantial traffic reduction on a
lossy network.

#### Experiment 3 — Head-of-line blocking

| Configuration | p99 snapshot delivery latency when 1 event is lost |
|---|---|
| Everything over one reliable-ordered channel | |
| Snapshots on ch1 (unreliable-seq) + events on ch2 (reliable-ord) | |

**The claim:** separating channels is an architectural reason, not a micro-optimization.

#### Experiment 4 — Congestion control

An RTT-over-time chart (60 seconds) with two series: congestion control on and off, at 20% loss.

**The claim:** with it off, RTT climbs steadily from bufferbloat and retransmit storms. With it on,
the system degrades in a controlled way and RTT stays stable.

#### Experiment 5 — Hand-written `BufferPool` vs .NET's `ArrayPool<T>`

> Team policy: **write it yourself first because that's the lesson, then compare against the standard
> library in the report.** See [conventions.md § 3.4](../../00-shared/conventions.md).

Benchmark 1 million Rent/Return operations at the same 1200-byte buffer size:

| Implementation | ns/op | Alloc | Gen0 GCs | Lines of code |
|---|---|---|---|---|
| `new byte[1200]` every time (baseline) | | | | 1 |
| The hand-written `BufferPool` | | | | ~40 |
| `ArrayPool<byte>.Shared` | | | | 1 |
| `ArrayPool<byte>.Create(1200, 256)` | | | | 2 |

**The conclusion to draw — honest in both directions:**

`ArrayPool<T>` is almost certainly faster or equal, with dramatically less code. But the hand-written
implementation gives two things `ArrayPool` doesn't: (a) a `RentedCount` for leak detection — which
already saved you in the phase-03 soak test, and (b) the `0xDD` fill in Debug builds that catches
use-after-return.

State it plainly: **production code should use `ArrayPool`**; the hand-written version exists to
understand the problem and to provide diagnostics. That's the answer to the challenge *"why not just
use the built-in library?"* — and it's far stronger than simply using the library and saying nothing.

#### Experiment 6 — Scalability

A chart with connection count (1 → 64) on the X axis, and per-tick processing time and CPU% on the Y
axis.

**The claim:** a single-threaded architecture is sufficient at the target scale, and we know where it
breaks.

### Task 2 — Write your report chapter (2 days)

The outline for your chapter (an expected 15–25 pages):

```
Chapter X: Designing and implementing a reliable transport layer over UDP

X.1  Problem statement
     X.1.1  Requirements of a real-time game application
     X.1.2  Why TCP doesn't fit (with experiment 1)
     X.1.3  Why not WebSocket
     X.1.4  Why not an off-the-shelf library (the academic goal)

X.2  Protocol design
     X.2.1  The 16-byte header structure, field by field
     X.2.2  Sequence numbers and the wrap-around problem
     X.2.3  The ack + bitfield mechanism (with experiment 2)
     X.2.4  The channel model and its semantics (with experiment 3)
     X.2.5  The handshake and IP-spoofing defense
     X.2.6  Fragmentation

X.3  Implementation
     X.3.1  Single-threaded architecture, non-blocking sockets
     X.3.2  Allocation-free memory management (BufferPool)
     X.3.3  Retransmission and RTO (Karn's algorithm)
     X.3.4  RTT and jitter estimation with EWMA
     X.3.5  Two-mode congestion control (with experiment 4)
     X.3.6  Sliding-window flow control

X.4  Security
     X.4.1  Anti-amplification
     X.4.2  Rate limiting
     X.4.3  Fragmentation-bomb defense
     X.4.4  What was left out (encryption) and why

X.5  Testing methodology
     X.5.1  A reproducible network simulator (random seeds)
     X.5.2  The 60+ unit test suite
     X.5.3  The packet logger and offline replay
     X.5.4  Soak testing

X.6  Experimental results
     X.6.1  The measurement environments (LAN, VPS)
     X.6.2  The four protocol experiments (tables + charts)
     X.6.3  Comparison against the standard library: BufferPool vs ArrayPool (experiment 5)
     X.6.4  Scalability (experiment 6)

X.7  Evaluation and limitations
     X.7.1  What was achieved
     X.7.2  Known limitations
     X.7.3  Future work
```

### Task 3 — Prepare for the defense (1 day)

**A 3-minute live demo** — the most convincing thing you can do:
1. Run the game normally, enable the F3 overlay, point at the 2 ms RTT (LAN)
2. Enable the simulator with `IRONFRONT_SIM=bad` **while still playing** — RTT jumps to 200 ms, loss
   to 15%
3. The game remains playable and the F3 metrics reflect it accurately
4. Open that session's pcap, run `--analyze`, and read out the results

**Likely challenge questions — prepare answers in advance:**

| Question | Short answer |
|---|---|
| "UDP is faster than TCP, right?" | No. At equal reliability the cost is comparable. The advantage is being able to **choose** the guarantee per data type — experiment 3 proves it |
| "Why not use QUIC?" | QUIC solves exactly this problem and better than my implementation. But the point of the capstone is to understand and implement the mechanisms. QUIC also mandates TLS, adding handshake cost that a LAN doesn't need |
| "Is your implementation fair to other TCP flows?" | Not entirely. Two-mode congestion control isn't AIMD, so on a shared network it takes more than its share from TCP. That's a known limitation, recorded in X.7.2 |
| "Why a 1200-byte MTU?" | 1500 (Ethernet) − 20 (IP) − 8 (UDP) = 1472, but PPPoE, VPNs and tunnels reduce it further. 1200 passes through every real-world path without IP fragmentation. There's a Wireshark experiment from phase 00 |
| "How many players before it falls over?" | Experiment 6: tick time crosses the threshold at N connections. The bottleneck is <fill in: CPU / bandwidth> |
| "How long before a 16-bit sequence wraps?" | 65536 / 30 packets/s ≈ 36 minutes. Handled by `SequenceMath.IsNewer`, with boundary unit tests |
| "How do you prevent cheating?" | The transport layer prevents DoS and connection spoofing. Gameplay anti-cheat is the server-authoritative design, which is C's part |

### Task 4 — Code documentation (1 day)

- `Ironfront.Net.Transport/README.md` — how to use the library, with a minimal example
- Complete XML docs for every public API, **especially buffer ownership**
- `docs/transport-troubleshooting.md` — symptom → cause → how to verify

```markdown
| Symptom | Common cause | How to verify |
|---|---|---|
| The server stops receiving packets after a few minutes (Windows) | SIO_UDP_CONNRESET not disabled | Catch SocketException ConnectionReset in the log |
| Measured RTT is negative or enormous | Karn's algorithm not applied | Check the ResendCount of the just-acked packet |
| Messages contain garbage | A buffer was returned to the pool but is still referenced | Enable the 0xDD fill in Debug builds |
| Bandwidth spikes and RTT climbs steadily | Retransmit storm, RTO too short | Check the PacketsResent / PacketsSent ratio |
| Clients drop after a few minutes over the Internet | NAT rebinding | Compare endpoints in the pcap before and after |
| RentedCount climbs steadily | Buffer leak | Find the exit path that forgot to call Return() |
```

---

## 2. Acceptance criteria (M4)

| # | Criterion |
|---|---|
| 1 | All 6 experiments have complete data, tables and charts |
| 2 | The report chapter is complete and follows the outline |
| 3 | The 3-minute demo has been rehearsed and works |
| 4 | Answers prepared for the 7 challenge questions |
| 5 | README + XML docs + troubleshooting guide written |
| 6 | ≥ 60 tests total, all green |
| 7 | The 8-hour soak test shows no leaks (from phase 03) |

---

## 3. Known limitations — a template to fill in

```markdown
## Transport layer limitations

### Deliberate, out of scope
- No payload encryption. Anyone capturing packets can read the contents. Acceptable for a LAN game
  and a capstone; a real application needs DTLS or a hand-rolled AEAD.
- Congestion control isn't AIMD, so it isn't fair to TCP flows on a shared network.
- No MTU discovery; fixed at 1200 bytes.
- No clock synchronization; assumes symmetric latency (RTT/2). Inaccurate under asymmetric routing.

### Technical limits
- Single-threaded: the ceiling is ~N connections (experiment 6). Beyond that needs multiple threads
  or multiple processes.
- A 256-message reliable window per channel. Exceeding it disconnects.
- 16-bit sequences wrap every 36 minutes — handled, but at a 120 Hz tick rate they'd wrap every
  9 minutes, so 32 bits would be worth considering.

### Untested
- Not tested on 4G/5G mobile networks (jitter and loss behave very differently from WiFi).
- IPv6 untested.
```
