# Dev B — Phase 00: Nền móng và Network Simulator

**Tuần 1–2** · Mốc **M0** · Ước lượng **2.0 người-tuần**

> Mục tiêu một câu: **gửi được byte qua UDP, và mô phỏng được mạng tệ.**

`NetworkSimulator` là deliverable quan trọng nhất phase này, **quan trọng hơn cả việc gửi được
gói tin**. Không có nó, mọi bug reliability ở phase sau sẽ phải debug bằng cách chơi game thật,
mỗi vòng lặp mất 5 phút thay vì 50 mili giây.

---

## 1. Mục tiêu

| # | Mục tiêu | Vì sao |
|---|---|---|
| 1 | Ôn kiến thức socket, làm 2 bài tập khởi động | Nền tảng, và là chương 1 báo cáo |
| 2 | Setup project .NET + test + CI | Không có test từ đầu thì sau không ai viết |
| 3 | `UdpPeer` gửi/nhận datagram thô, parse header 16 byte | Nền của mọi thứ |
| 4 | **`NetworkSimulator`** đầy đủ 5 loại nhiễu | Chặn rủi ro R1/B1 |
| 5 | `BufferPool` không cấp phát | Chặn B3 |
| 6 | `LoopbackTransport` cho A và C dùng sớm | Chặn B6 |
| 7 | Đóng băng API công khai | A và C code dựa vào nó |

---

## 2. Task chi tiết

### Task 1 — Bài tập khởi động (2 ngày)

Không phải phí thời gian: đây là chương 1 của báo cáo đồ án và là cách bạn kiểm chứng hiểu biết
trước khi viết thứ phức tạp.

**Bài 1 — Echo UDP.** Server nhận datagram, gửi trả nguyên văn. Client gửi 10.000 gói đánh số,
đếm bao nhiêu gói về, bao nhiêu về sai thứ tự.

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

Ghi lại: LAN mất bao nhiêu %? Qua Internet (VPS) mất bao nhiêu? Gửi nhanh 10.000 gói liên tục
có mất không (buffer overflow ở kernel)?

**Bài 2 — Echo TCP tương đương.** Cùng số lượng, đo độ trễ end-to-end của gói thứ 5000 khi
gói thứ 4999 bị mất. Dùng `clumsy` (Windows) hoặc `tc netem` (Linux) để tạo mất gói.

**Kết quả mong đợi:** TCP sẽ cho thấy độ trễ tăng vọt ở gói ngay sau gói mất (head-of-line
blocking), UDP thì không. Đây là **bằng chứng thực nghiệm** cho quyết định kiến trúc AD-8.
Viết 1 trang nhận xét kèm biểu đồ.

**Bài 3 — MTU.** Gửi datagram 2000 byte, bắt bằng Wireshark, quan sát IP fragmentation. Gửi
1200 byte, quan sát không fragment. Giải thích vì sao ta chọn 1200.

### Task 2 — Setup project (nửa ngày)

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

**Vì sao `netstandard2.1`:** Unity hỗ trợ nó. Nếu bạn dùng `net8.0` thì Unity không load được
DLL. Đây là lỗi hay gặp, phát hiện muộn rất tốn công.

**Cạm bẫy — `Span<byte>` trên netstandard2.1:** cần package `System.Memory`. Thêm vào csproj:
```xml
<PackageReference Include="System.Memory" Version="4.5.5" />
```
Và khi copy DLL sang Unity, phải copy cả `System.Memory.dll`, `System.Buffers.dll`,
`System.Runtime.CompilerServices.Unsafe.dll`. Ghi rõ trong `tools/build-libs.ps1`.

Bật cảnh báo thành lỗi:
```xml
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<Nullable>enable</Nullable>
```

### Task 3 — `PacketHeader` (1 ngày)

Đọc/ghi header 16 byte theo đúng
[`protocol-spec.md § 2`](../../00-shared/protocol-spec.md#2-header-gsp-16-byte-mọi-datagram).

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
        if (pid != ProtocolConstants.PROTOCOL_ID) return false;   // gói rác, drop im lặng
        ushort payLen = ReadU16(src, 14);
        if (src.Length < SIZE + payLen) return false;             // gói cụt
        h = new PacketHeader(pid, src[2], src[3], ReadU16(src,4), ReadU16(src,6),
                             ReadU32(src,8), ReadU16(src,12), payLen);
        return true;
    }

    public void Write(Span<byte> dst) { /* đối xứng */ }

    // Đọc/ghi thủ công, KHÔNG dùng BitConverter (phụ thuộc endianness máy)
    private static ushort ReadU16(ReadOnlySpan<byte> s, int o)
        => (ushort)(s[o] | (s[o + 1] << 8));
    private static uint ReadU32(ReadOnlySpan<byte> s, int o)
        => (uint)(s[o] | (s[o+1] << 8) | (s[o+2] << 16) | (s[o+3] << 24));
}
```

> **Cạm bẫy:** `BitConverter.ToUInt16` dùng endianness của máy. Trên x86 nó là little-endian
> nên "chạy được" — rồi vỡ nếu ai đó chạy trên ARM big-endian. Viết shift thủ công ngay từ đầu,
> chi phí bằng 0 và không bao giờ sai.

**Test bắt buộc:**
- Round-trip mọi trường với giá trị biên (0, max)
- `TryRead` với `protocolId` sai → false
- `TryRead` với buffer ngắn hơn 16 byte → false
- `TryRead` với `payloadLength` lớn hơn dữ liệu thực → false

### Task 4 — `BufferPool` (1 ngày)

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
        // Pool cạn: tạo mới nhưng CẢNH BÁO — pool sizing sai
        Interlocked.Increment(ref _created);
        NetLog.Warn($"BufferPool cạn, đã tạo {_created} buffer. Tăng capacity.");
        return new byte[_bufferSize];
    }

    public void Return(byte[] b)
    {
        if (b.Length != _bufferSize) return;      // không phải của pool này
        Interlocked.Decrement(ref _rented);
        _pool.Add(b);
    }

    public int RentedCount => _rented;
}
```

**Cạm bẫy ownership:** buffer trả về pool rồi mà còn giữ tham chiếu → đọc phải dữ liệu của
người khác. Bug này biểu hiện là "thỉnh thoảng packet có nội dung lạ", cực khó tìm.

Phòng ngừa: trong Debug build, ghi đè buffer bằng `0xDD` khi trả về pool. Ai đọc buffer đã trả
sẽ thấy toàn `0xDD`, lộ bug ngay.

```csharp
#if DEBUG
    Array.Fill(b, (byte)0xDD);
#endif
```

### Task 5 — `NetworkSimulator` (3 ngày) — DELIVERABLE QUAN TRỌNG NHẤT

Chèn giữa `UdpPeer` và socket thật. Mô phỏng 5 loại nhiễu.

```csharp
// Ironfront.Net.Transport/Simulation/NetworkSimulator.cs
public sealed class SimulatorConfig
{
    public bool  Enabled          = false;
    public float LatencyMs        = 0f;      // độ trễ cơ bản một chiều
    public float JitterMs         = 0f;      // dao động ± quanh LatencyMs
    public float PacketLossPercent= 0f;      // 0..100
    public float DuplicatePercent = 0f;      // gói bị nhân đôi
    public float ReorderPercent   = 0f;      // gói bị đảo thứ tự
    public int   RandomSeed       = 12345;   // TÁI LẬP ĐƯỢC — cực kỳ quan trọng

    // Preset
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

    /// <summary>Trả false nếu gói bị "mất" — người gọi không gửi thật.</summary>
    public bool ShouldSend(ReadOnlySpan<byte> data, EndPoint ep, double nowMs, BufferPool pool)
    {
        if (!_cfg.Enabled) return true;

        if (Roll() < _cfg.PacketLossPercent) return false;      // mất

        int copies = Roll() < _cfg.DuplicatePercent ? 2 : 1;    // nhân đôi
        for (int i = 0; i < copies; i++)
        {
            double delay = _cfg.LatencyMs + (_rng.NextDouble() * 2 - 1) * _cfg.JitterMs;
            if (Roll() < _cfg.ReorderPercent) delay += _cfg.LatencyMs;  // đẩy lùi → đảo thứ tự
            delay = Math.Max(0, delay);

            var buf = pool.Rent();
            data.CopyTo(buf);
            _inFlight.Add(new DelayedPacket {
                DeliverAtMs = nowMs + delay, Data = buf, Length = data.Length, Endpoint = ep });
        }
        return false;    // luôn false: gói thật được gửi ở Flush()
    }

    /// <summary>Gọi mỗi Poll(). Gửi những gói đã tới hạn.</summary>
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

**Vì sao `RandomSeed` quan trọng:** khi test tìm ra bug ở seed 12345, bạn chạy lại đúng seed đó
sẽ tái hiện chính xác cùng chuỗi mất/đảo gói. Không có seed cố định thì bug "thỉnh thoảng xảy
ra" sẽ không bao giờ bắt được. **Đây là kỹ thuật quan trọng nhất trong debug netcode.**

**Cạm bẫy — reorder không thực sự đảo.** Cài đặt trên chỉ *đẩy lùi* gói. Nếu gói sau không được
đẩy lùi thì nó vượt lên → đảo thứ tự. Nhưng nếu `LatencyMs = 0`, đẩy lùi 0ms thì không đảo gì.
Test phải đặt `LatencyMs > 0` khi test reorder. Ghi chú này vào XML doc.

**Bật/tắt runtime:** đọc từ biến môi trường để bật khi chạy game thật mà không cần build lại.
```
IRONFRONT_SIM=typical   dotnet run
IRONFRONT_SIM=bad       .\Ironfront_Reborn.exe
```

### Task 6 — `UdpPeer` gửi/nhận thô (2 ngày)

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
            Blocking = false,                 // B-AD-1: một thread, non-blocking
            ReceiveBufferSize = 1 << 20,      // 1 MB, tránh mất gói ở kernel khi burst
            SendBufferSize    = 1 << 20,
        };
        _socket.Bind(new IPEndPoint(IPAddress.Any, bindPort));
        DisableIcmpPortUnreachable();
        _pool = new BufferPool(256, ProtocolConstants.MTU_SAFE);
        _sim  = new NetworkSimulator(simCfg);
    }

    /// <summary>Windows: tắt SIO_UDP_CONNRESET. BẮT BUỘC, xem cạm bẫy dưới.</summary>
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
            { NetLog.Warn($"recv lỗi {e.SocketErrorCode}"); _pool.Return(buf); continue; }

            if (PacketHeader.TryRead(buf.AsSpan(0, n), out var header))
                Dispatch(header, buf.AsSpan(PacketHeader.SIZE, header.PayloadLength), remote);
            // header sai → drop im lặng, KHÔNG trả lời (chống amplification)
            _pool.Return(buf);
        }
    }
}
```

> **Cạm bẫy Windows nghiêm trọng — `SIO_UDP_CONNRESET`.**
> Trên Windows, nếu bạn gửi UDP tới một cổng đóng, OS nhận ICMP Port Unreachable và làm lần
> `ReceiveFrom` **tiếp theo** ném `SocketException` với `ConnectionReset`. Điều này khiến vòng
> lặp nhận của bạn chết dù chẳng có gì sai. Biểu hiện: "server chạy được 1 phút rồi ngừng nhận
> gói". Bắt buộc gọi `IOControl(SIO_UDP_CONNRESET, ...)` như trên. Không có trên Linux.
>
> Đây là một trong những bug tốn thời gian nhất khi tự viết UDP trên Windows.

### Task 6.5 — Bit-packing serializer (2 ngày) — MỚI NHẬN TỪ DEV C

Ba class, đặt trong `Ironfront.Net.Replication/Serialization/`. Cùng loại việc byte-level bạn
đang làm với `PacketHeader`, nên đặt cạnh nhau về mặt tư duy.

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
        Debug.Assert(bits == 32 || value < (1u << bits), $"giá trị {value} vượt {bits} bit");
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

`Quantize` — copy **nguyên văn** công thức từ
[`protocol-spec.md § 4.4`](../../00-shared/protocol-spec.md#44-quantization--hằng-số-bắt-buộc-dùng-chung).
**Không sáng tạo lại.** Thấy công thức có vẻ sai thì sửa spec trước qua PR, rồi mới sửa code.

**Ba cạm bẫy:**

1. **Thứ tự bit.** Cài đặt trên ghi bit thấp trước (LSB-first). `BitReader` phải đọc cùng thứ
   tự. Nếu sau này bạn tối ưu `BitWriter` mà quên đổi `BitReader`, mọi thứ vỡ theo cách rất khó
   hiểu. Test round-trip là bắt buộc.
2. **Vòng lặp từng bit chậm** — tốn ~8× so với ghi cả word. Với 48 actor × 100 bit × 20 Hz =
   ~96K lần lặp/giây, vẫn chấp nhận được. **Làm đúng trước, tối ưu sau nếu benchmark chỉ ra.**
3. **Tràn buffer.** `WriteBits` không kiểm tra `_buf` còn chỗ. Thêm kiểm tra ở Debug build.

> **Dev C viết test conformance kiểm định code này**, với dữ liệu hex cứng viết tay theo spec.
> Test của C có thể đỏ trên code bạn vừa viết — đó là tính năng, không phải xung đột. Khi đỏ,
> hai người cùng mở spec § 4.4 xem ai lệch.
>
> Bạn vẫn nên viết test round-trip của riêng mình (~8 test). Hai bộ test bổ sung nhau: của bạn
> chứng minh nhất quán nội bộ, của C chứng minh khớp spec.

### Task 7 — `LoopbackTransport` (1 ngày)

Cho A và C dùng ngay tuần 3, không cần chờ reliability xong.

```csharp
/// <summary>Transport in-memory, không qua socket. Có thể gắn NetworkSimulator.</summary>
public sealed class LoopbackTransport : ITransportClient, ITransportServer
{
    // Hai hàng đợi, client↔server. Vẫn qua simulator để mô phỏng mạng tệ.
}
```

Giá trị: A test client-side prediction với 200ms latency mô phỏng **mà không cần bất kỳ socket
nào**, chạy trong Unity Editor một process duy nhất.

---

## 3. Tiêu chí nghiệm thu

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | 3 bài tập khởi động xong, có 1 trang nhận xét + biểu đồ | File `reports/warmup-udp-vs-tcp.md` |
| 2 | `dotnet build` sạch, 0 warning | Output CI |
| 3 | `PacketHeader` round-trip đúng, ≥8 test xanh | `dotnet test` |
| 4 | `BufferPool` không cấp phát sau khi ấm | Benchmark: 100k Rent/Return → 0 GC gen0 |
| 5 | `NetworkSimulator` tái lập được với cùng seed | Test: chạy 2 lần cùng seed → chuỗi gói mất giống hệt |
| 6 | Simulator mô phỏng đủ 5 loại nhiễu | 5 test riêng, mỗi loại kiểm chứng thống kê trên 10.000 gói |
| 7 | `UdpPeer` gửi/nhận 10.000 gói qua localhost, không mất | Integration test |
| 8 | `SIO_UDP_CONNRESET` đã tắt, chạy 10 phút với 1 client tắt giữa chừng | Server không crash |
| 9 | `LoopbackTransport` giao được cho A và C dùng | A xác nhận |
| 10 | API công khai đóng băng, có XML doc đầy đủ | Review với A và C |

---

## 4. Rủi ro

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| Chưa quen socket, mất nhiều hơn 2 ngày ôn | | Chấp nhận tới 3 ngày. Nếu hơn, báo nhóm — có thể cần đổi phân công |
| `netstandard2.1` + `Span` không load được vào Unity | Unity báo lỗi thiếu assembly | Copy đủ 3 DLL phụ thuộc. Test load sớm ở tuần 2, đừng để tới tuần 6 |
| `SIO_UDP_CONNRESET` không biết → mất nhiều ngày | Server ngừng nhận gói sau vài phút | Đã ghi ở Task 6. Làm đúng từ đầu |
| Simulator thiết kế sai, không tái lập được | Bug không tái hiện được | `RandomSeed` cố định, `Random` riêng cho simulator, không dùng `Random.Shared` |

---

## 5. Bàn giao cuối phase

Gửi cho A và C trước cuối tuần 2:
- DLL `Ironfront.Net.Transport.dll` + 3 DLL phụ thuộc, đã test load được trong Unity
- `LoopbackTransport` dùng được
- File XML doc mô tả rõ **ownership của buffer trong `OnMessage`**
- Hướng dẫn bật simulator bằng biến môi trường
