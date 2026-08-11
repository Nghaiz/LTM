# Dev B — Phase 00: Foundation and the Network Simulator

**Weeks 1–2** · Milestone **M0** · Estimate **2.0 person-weeks**

> Goal in one sentence: **get bytes across UDP, and be able to simulate a bad network.**

`NetworkSimulator` is the most important deliverable of this phase — **more important than actually
sending packets**. Without it, every reliability bug in later phases has to be debugged by playing
the real game, at 5 minutes per iteration instead of 50 milliseconds.

---

## 1. Objectives

| # | Objective | Why |
|---|---|---|
| 1 | Refresh socket knowledge, complete 2 warm-up exercises | The foundation, and chapter 1 of the report |
| 2 | Set up the .NET project + tests + CI | If tests aren't there from the start, nobody writes them later |
| 3 | `UdpPeer` sending/receiving raw datagrams, parsing the 16-byte header | The basis for everything |
| 4 | **`NetworkSimulator`** with all 5 impairment types | Mitigates risks R1/B1 |
| 5 | A non-allocating `BufferPool` | Mitigates B3 |
| 6 | `LoopbackTransport` for A and C to use early | Mitigates B6 |
| 7 | Freeze the public API | A and C write code against it |

---

## 2. Detailed tasks

### Task 1 — Warm-up exercises (2 days)

Not wasted time: this is chapter 1 of the capstone report, and it's how you check your understanding
before writing anything complex.

**Exercise 1 — UDP echo.** The server receives a datagram and sends it straight back. The client
sends 10,000 numbered packets and counts how many come back and how many arrive out of order.

```csharp
// tools/warmup/UdpEcho/Program.cs
var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
sock.Bind(new IPEndPoint(IPAddress.Any, 9000));
var buf = new byte[1500];
EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
while (true)
{
    int n = sock.ReceiveFrom(buf, ref remote);
    sock.SendTo(buf, 0, n, SocketFlags.None, remote);
}
```

Record: what percentage is lost on LAN? Over the Internet (a VPS)? Does sending 10,000 packets
rapidly back to back lose any (kernel buffer overflow)?

**Exercise 2 — The equivalent TCP echo.** Same packet count; measure the end-to-end latency of
packet 5000 when packet 4999 is lost. Use `clumsy` (Windows) or `tc netem` (Linux) to induce the
loss.

**Expected result:** TCP will show a latency spike on the packet immediately after the lost one
(head-of-line blocking); UDP won't. This is **experimental evidence** for architectural decision
AD-8. Write a one-page commentary with a chart.

**Exercise 3 — MTU.** Send a 2000-byte datagram, capture it in Wireshark, observe the IP
fragmentation. Send 1200 bytes and observe that it doesn't fragment. Explain why we chose 1200.

### Task 2 — Project setup (half a day)

```
Ironfront.Net.Transport/
├── Ironfront.Net.Transport.csproj      <TargetFramework>netstandard2.1</TargetFramework>
├── UdpPeer.cs
├── Connection.cs
├── BufferPool.cs
├── PacketHeader.cs
├── Simulation/NetworkSimulator.cs
└── Loopback/LoopbackTransport.cs

Ironfront.Net.Transport.Tests/
├── Ironfront.Net.Transport.Tests.csproj  <TargetFramework>net8.0</TargetFramework>
└── ...
```

**Why `netstandard2.1`:** Unity supports it. If you target `net8.0`, Unity can't load the DLL. This
is a common mistake and expensive to discover late.

**Trap — `Span<byte>` on netstandard2.1:** it needs the `System.Memory` package. Add to the csproj:
```xml
<PackageReference Include="System.Memory" Version="4.5.5" />
```
And when copying DLLs into Unity you must also copy `System.Memory.dll`, `System.Buffers.dll` and
`System.Runtime.CompilerServices.Unsafe.dll`. Document this in `tools/build-libs.ps1`.

Turn warnings into errors:
```xml
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<Nullable>enable</Nullable>
```

### Task 3 — `PacketHeader` (1 day)

Read/write the 16-byte header exactly per
[`protocol-spec.md § 2`](../../00-shared/protocol-spec.md#2-gsp-header-16-bytes-every-datagram).

```csharp
public readonly struct PacketHeader
{
    public const int SIZE = 16;

    public readonly ushort ProtocolId;
    public readonly byte   PacketType;
    public readonly byte   Flags;
    public readonly ushort Sequence;
    public readonly ushort Ack;
    public readonly uint   AckBitfield;
    public readonly ushort ConnectionId;
    public readonly ushort PayloadLength;

    public static bool TryRead(ReadOnlySpan<byte> src, out PacketHeader h)
    {
        h = default;
        if (src.Length < SIZE) return false;
        ushort pid = ReadU16(src, 0);
        if (pid != ProtocolConstants.PROTOCOL_ID) return false;   // junk packet, drop silently
        ushort payLen = ReadU16(src, 14);
        if (src.Length < SIZE + payLen) return false;             // truncated packet
        h = new PacketHeader(pid, src[2], src[3], ReadU16(src,4), ReadU16(src,6),
                             ReadU32(src,8), ReadU16(src,12), payLen);
        return true;
    }

    public void Write(Span<byte> dst) { /* symmetric */ }

    // Read/write manually, NOT with BitConverter (which depends on machine endianness)
    private static ushort ReadU16(ReadOnlySpan<byte> s, int o)
        => (ushort)(s[o] | (s[o + 1] << 8));
    private static uint ReadU32(ReadOnlySpan<byte> s, int o)
        => (uint)(s[o] | (s[o+1] << 8) | (s[o+2] << 16) | (s[o+3] << 24));
}
```

> **Trap:** `BitConverter.ToUInt16` uses the machine's endianness. On x86 that's little-endian, so it
> "works" — and then breaks if anyone runs it on big-endian ARM. Write the shifts manually from the
> start; it costs nothing and can never be wrong.

**Mandatory tests:**
- Round-trip every field with boundary values (0, max)
- `TryRead` with a wrong `protocolId` → false
- `TryRead` with a buffer shorter than 16 bytes → false
- `TryRead` with a `payloadLength` larger than the actual data → false

### Task 4 — `BufferPool` (1 day)

```csharp
public sealed class BufferPool
{
    private readonly ConcurrentBag<byte[]> _pool = new();
    private readonly int _bufferSize;
    private int _rented, _created;

    public BufferPool(int capacity, int bufferSize)
    {
        _bufferSize = bufferSize;
        for (int i = 0; i < capacity; i++) _pool.Add(new byte[bufferSize]);
        _created = capacity;
    }

    public byte[] Rent()
    {
        if (_pool.TryTake(out var b)) { Interlocked.Increment(ref _rented); return b; }
        // Pool exhausted: allocate, but WARN — the pool is sized wrong
        Interlocked.Increment(ref _created);
        NetLog.Warn($"BufferPool exhausted, {_created} buffers created. Raise the capacity.");
        return new byte[_bufferSize];
    }

    public void Return(byte[] b)
    {
        if (b.Length != _bufferSize) return;      // not from this pool
        Interlocked.Decrement(ref _rented);
        _pool.Add(b);
    }

    public int RentedCount => _rented;
}
```

**The ownership trap:** holding a reference to a buffer after returning it to the pool → you end up
reading someone else's data. This bug presents as "packets occasionally have strange contents" and
is extremely hard to find.

Prevention: in Debug builds, overwrite the buffer with `0xDD` on return. Anyone reading a returned
buffer sees nothing but `0xDD`, exposing the bug immediately.

```csharp
#if DEBUG
    Array.Fill(b, (byte)0xDD);
#endif
```

### Task 5 — `NetworkSimulator` (3 days) — THE MOST IMPORTANT DELIVERABLE

Inserted between `UdpPeer` and the real socket. Simulates 5 kinds of impairment.

```csharp
// Ironfront.Net.Transport/Simulation/NetworkSimulator.cs
public sealed class SimulatorConfig
{
    public bool  Enabled          = false;
    public float LatencyMs        = 0f;      // base one-way latency
    public float JitterMs         = 0f;      // ± variation around LatencyMs
    public float PacketLossPercent= 0f;      // 0..100
    public float DuplicatePercent = 0f;      // packets duplicated
    public float ReorderPercent   = 0f;      // packets reordered
    public int   RandomSeed       = 12345;   // REPRODUCIBLE — extremely important

    // Presets
    public static SimulatorConfig Lan()  => new() { Enabled = true, LatencyMs = 1 };
    public static SimulatorConfig Good() => new() { Enabled = true, LatencyMs = 30, JitterMs = 5,
                                                    PacketLossPercent = 0.5f };
    public static SimulatorConfig Typical() => new() { Enabled = true, LatencyMs = 50, JitterMs = 20,
                                                    PacketLossPercent = 5f, ReorderPercent = 2f };
    public static SimulatorConfig Bad()  => new() { Enabled = true, LatencyMs = 100, JitterMs = 50,
                                                    PacketLossPercent = 15f, ReorderPercent = 5f,
                                                    DuplicatePercent = 2f };
    public static SimulatorConfig Awful()=> new() { Enabled = true, LatencyMs = 150, JitterMs = 100,
                                                    PacketLossPercent = 30f, ReorderPercent = 10f,
                                                    DuplicatePercent = 5f };
}

internal sealed class NetworkSimulator
{
    private struct DelayedPacket
    {
        public double  DeliverAtMs;
        public byte[]  Data;
        public int     Length;
        public EndPoint Endpoint;
    }

    private readonly List<DelayedPacket> _inFlight = new();
    private readonly Random _rng;
    private readonly SimulatorConfig _cfg;

    public NetworkSimulator(SimulatorConfig cfg) { _cfg = cfg; _rng = new Random(cfg.RandomSeed); }

    /// <summary>Returns false if the packet was "lost" — the caller must not really send it.</summary>
    public bool ShouldSend(ReadOnlySpan<byte> data, EndPoint ep, double nowMs, BufferPool pool)
    {
        if (!_cfg.Enabled) return true;

        if (Roll() < _cfg.PacketLossPercent) return false;      // lost

        int copies = Roll() < _cfg.DuplicatePercent ? 2 : 1;    // duplicated
        for (int i = 0; i < copies; i++)
        {
            double delay = _cfg.LatencyMs + (_rng.NextDouble() * 2 - 1) * _cfg.JitterMs;
            if (Roll() < _cfg.ReorderPercent) delay += _cfg.LatencyMs;  // push it back → reordered
            delay = Math.Max(0, delay);

            var buf = pool.Rent();
            data.CopyTo(buf);
            _inFlight.Add(new DelayedPacket {
                DeliverAtMs = nowMs + delay, Data = buf, Length = data.Length, Endpoint = ep });
        }
        return false;    // always false: the real packet is sent from Flush()
    }

    /// <summary>Called on every Poll(). Sends packets whose time has come.</summary>
    public void Flush(double nowMs, Action<byte[], int, EndPoint> reallySend, BufferPool pool)
    {
        for (int i = _inFlight.Count - 1; i >= 0; i--)
        {
            if (_inFlight[i].DeliverAtMs > nowMs) continue;
            var p = _inFlight[i];
            reallySend(p.Data, p.Length, p.Endpoint);
            pool.Return(p.Data);
            _inFlight.RemoveAt(i);
        }
    }

    private double Roll() => _rng.NextDouble() * 100.0;
}
```

**Why `RandomSeed` matters:** when a test finds a bug at seed 12345, re-running that exact seed
reproduces the identical sequence of losses and reorderings. Without a fixed seed, "it only happens
sometimes" bugs are never caught. **This is the single most important technique in netcode
debugging.**

**Trap — reordering that doesn't actually reorder.** The implementation above only *pushes packets
back*. If the following packet isn't pushed back, it overtakes → reordering. But with
`LatencyMs = 0`, pushing back by 0 ms reorders nothing. Reordering tests must set `LatencyMs > 0`.
Note this in the XML docs.

**Runtime toggle:** read it from an environment variable so it can be enabled in the real game
without a rebuild.
```
IRONFRONT_SIM=typical   dotnet run
IRONFRONT_SIM=bad       .\Ironfront_Reborn.exe
```

### Task 6 — Raw send/receive in `UdpPeer` (2 days)

```csharp
public sealed class UdpPeer : IDisposable
{
    private readonly Socket _socket;
    private readonly BufferPool _pool;
    private readonly NetworkSimulator _sim;

    public UdpPeer(int bindPort, SimulatorConfig simCfg)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false,                 // B-AD-1: one thread, non-blocking
            ReceiveBufferSize = 1 << 20,      // 1 MB, avoids kernel packet loss during bursts
            SendBufferSize    = 1 << 20,
        };
        _socket.Bind(new IPEndPoint(IPAddress.Any, bindPort));
        DisableIcmpPortUnreachable();
        _pool = new BufferPool(256, ProtocolConstants.MTU_SAFE);
        _sim  = new NetworkSimulator(simCfg);
    }

    /// <summary>Windows: disable SIO_UDP_CONNRESET. MANDATORY, see the trap below.</summary>
    private void DisableIcmpPortUnreachable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        const int SIO_UDP_CONNRESET = -1744830452;
        _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
    }

    public void Poll(double nowMs)
    {
        _sim.Flush(nowMs, RawSend, _pool);
        while (_socket.Available > 0)
        {
            var buf = _pool.Rent();
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = _socket.ReceiveFrom(buf, ref remote); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
            { _pool.Return(buf); break; }
            catch (SocketException e)
            { NetLog.Warn($"recv error {e.SocketErrorCode}"); _pool.Return(buf); continue; }

            if (PacketHeader.TryRead(buf.AsSpan(0, n), out var header))
                Dispatch(header, buf.AsSpan(PacketHeader.SIZE, header.PayloadLength), remote);
            // bad header → drop silently, do NOT reply (anti-amplification)
            _pool.Return(buf);
        }
    }
}
```

> **A serious Windows trap — `SIO_UDP_CONNRESET`.**
> On Windows, if you send UDP to a closed port, the OS receives an ICMP Port Unreachable and makes
> your **next** `ReceiveFrom` throw a `SocketException` with `ConnectionReset`. That kills your
> receive loop even though nothing is actually wrong. It presents as "the server runs for a minute
> and then stops receiving packets". You must call `IOControl(SIO_UDP_CONNRESET, ...)` as shown
> above. It doesn't exist on Linux.
>
> This is one of the most time-consuming bugs when hand-writing UDP on Windows.

### Task 6.5 — Bit-packing serializer (2 days) — NEWLY TAKEN FROM DEV C

Three classes, placed in `Ironfront.Net.Replication/Serialization/`. It's the same byte-level work
you're already doing with `PacketHeader`, so mentally it belongs next to it.

```csharp
// Ironfront.Net.Replication/Serialization/BitWriter.cs
public ref struct BitWriter
{
    private readonly Span<byte> _buf;
    private int _bitPos;

    public BitWriter(Span<byte> buffer) { _buf = buffer; _bitPos = 0; }
    public int BytesWritten => (_bitPos + 7) / 8;

    public void WriteBits(uint value, int bits)
    {
        Debug.Assert(bits > 0 && bits <= 32);
        Debug.Assert(bits == 32 || value < (1u << bits), $"value {value} exceeds {bits} bits");
        for (int i = 0; i < bits; i++)
        {
            int byteIdx = _bitPos >> 3, bitIdx = _bitPos & 7;
            if (bitIdx == 0) _buf[byteIdx] = 0;
            if ((value & (1u << i)) != 0) _buf[byteIdx] |= (byte)(1 << bitIdx);
            _bitPos++;
        }
    }

    public void WriteBool(bool v)     => WriteBits(v ? 1u : 0u, 1);
    public void WriteByte(byte v)     => WriteBits(v, 8);
    public void WriteUInt16(ushort v) => WriteBits(v, 16);
    public void WriteUInt32(uint v)   => WriteBits(v, 32);
    public void AlignToByte() { while ((_bitPos & 7) != 0) WriteBool(false); }
}
```

For `Quantize` — copy the formulas **verbatim** from
[`protocol-spec.md § 4.4`](../../00-shared/protocol-spec.md#44-quantization--mandatory-shared-constants).
**Don't reinvent them.** If a formula looks wrong, fix the spec first via a PR, then the code.

**Three traps:**

1. **Bit order.** The implementation above writes least-significant bit first (LSB-first).
   `BitReader` must read in the same order. If you later optimize `BitWriter` and forget to change
   `BitReader`, everything breaks in very confusing ways. Round-trip tests are mandatory.
2. **The per-bit loop is slow** — roughly 8× the cost of writing whole words. At 48 actors ×
   100 bits × 20 Hz = ~96K iterations/second, that's still acceptable. **Get it right first,
   optimize later only if the benchmark says so.**
3. **Buffer overrun.** `WriteBits` doesn't check that `_buf` still has room. Add a check in Debug
   builds.

> **Dev C writes the conformance tests that verify this code**, using hand-written hex data taken
> from the spec. C's tests may go red against code you just wrote — that's a feature, not a
> conflict. When it happens, the two of you open spec § 4.4 and see who diverged.
>
> You should still write your own round-trip tests (~8 of them). The two suites complement each
> other: yours prove internal consistency, C's prove conformance to the spec.

### Task 7 — `LoopbackTransport` (1 day)

So A and C can start using it in week 3, without waiting for reliability to be finished.

```csharp
/// <summary>An in-memory transport that bypasses the socket. Can be attached to NetworkSimulator.</summary>
public sealed class LoopbackTransport : ITransportClient, ITransportServer
{
    // Two queues, client↔server. Still passes through the simulator to model a bad network.
}
```

The value: A can test client-side prediction with 200 ms of simulated latency **without any socket
at all**, running in a single process inside the Unity Editor.

---

## 3. Acceptance criteria

| # | Criterion | How to verify |
|---|---|---|
| 1 | All 3 warm-up exercises done, with a one-page commentary + chart | The file `reports/warmup-udp-vs-tcp.md` |
| 2 | `dotnet build` clean, 0 warnings | CI output |
| 3 | `PacketHeader` round-trips correctly, ≥8 tests green | `dotnet test` |
| 4 | `BufferPool` doesn't allocate once warm | Benchmark: 100k Rent/Return → 0 gen0 GCs |
| 5 | `NetworkSimulator` is reproducible with the same seed | Test: two runs with the same seed → identical loss sequences |
| 6 | The simulator models all 5 impairment types | 5 separate tests, each statistically verified over 10,000 packets |
| 7 | `UdpPeer` sends/receives 10,000 packets over localhost with no loss | Integration test |
| 8 | `SIO_UDP_CONNRESET` is disabled; run 10 minutes with one client killed mid-run | The server doesn't crash |
| 9 | `LoopbackTransport` delivered to A and C | Confirmed by A |
| 10 | The public API is frozen and fully XML-documented | Reviewed with A and C |

---

## 4. Risks

| Risk | Sign | Handling |
|---|---|---|
| Unfamiliar with sockets, the refresher takes more than 2 days | | Up to 3 days is fine. Beyond that, tell the team — the assignment may need to change |
| `netstandard2.1` + `Span` won't load into Unity | Unity reports a missing assembly | Copy all 3 dependency DLLs. Test the load early in week 2, not in week 6 |
| Not knowing about `SIO_UDP_CONNRESET` → days lost | The server stops receiving packets after a few minutes | Documented in Task 6. Do it right from the start |
| A badly designed simulator that isn't reproducible | Bugs can't be reproduced | A fixed `RandomSeed`, a dedicated `Random` for the simulator, never `Random.Shared` |

---

## 5. End-of-phase handoff

Send to A and C before the end of week 2:
- `Ironfront.Net.Transport.dll` + the 3 dependency DLLs, verified to load in Unity
- A working `LoopbackTransport`
- XML docs clearly describing **buffer ownership in `OnMessage`**
- Instructions for enabling the simulator via environment variables
