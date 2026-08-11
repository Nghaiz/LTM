# Dev B — Phase 01: Reliability layer

**Tuần 3–6** · Mốc **M1 (mốc sinh tử)** · Ước lượng **4.0 người-tuần**

> Mục tiêu một câu: **gói tin quan trọng luôn tới, gói tin cũ bị bỏ đúng lúc, và cả hai chuyện
> đó vẫn đúng ở 30% packet loss.**

Đây là phần lõi học thuật của đồ án. Làm kỹ.

---

## 1. Mục tiêu

| # | Mục tiêu |
|---|---|
| 1 | Handshake 4 bước, chống IP spoofing |
| 2 | Sequence + ack + ack bitfield 32 bit |
| 3 | Retransmit gói reliable chưa được ack |
| 4 | 4 channel với semantics khác nhau |
| 5 | Fragmentation / reassembly, có chống DoS |
| 6 | RTT estimation (EWMA) + jitter |
| 7 | Keep-alive, timeout, ngắt kết nối sạch |
| 8 | **≥40 unit test** |

---

## 2. Task chi tiết

### Task 1 — Handshake và `Connection` state machine (3 ngày)

Theo [`protocol-spec.md § 3.1`](../../00-shared/protocol-spec.md#31-handshake).

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
                if (nowMs - _lastSendMs > 250)          // retry mỗi 250ms
                {
                    if (++_connectAttempts > 20)         // 5 giây
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

**Vì sao challenge–response:** chống *IP spoofing amplification*. Kẻ tấn công gửi
`CONNECT_REQUEST` với IP nguồn giả là nạn nhân. Nếu server cấp phát tài nguyên ngay, nó vừa tốn
RAM vừa gửi dữ liệu tới nạn nhân (khuếch đại tấn công). Với challenge, server **không lưu gì**
cho tới khi client chứng minh nhận được `serverSalt` — điều mà kẻ giả IP không làm được.

**Chi tiết quan trọng:** ở bước `CONNECT_CHALLENGE`, server **không** tạo `Connection` object.
Nó tính `serverSalt = HMAC(clientEndpoint + clientSalt, serverSecret)` — stateless, không tốn
bộ nhớ. Chỉ khi `CONNECT_RESPONSE` đúng mới cấp phát. Đây gọi là *SYN cookie* trong TCP, ta áp
dụng ý tưởng tương tự.

**Ngắt kết nối sạch:** gửi `DISCONNECT` **3 lần** (không reliable, vì đang đóng). Bên kia nhận
được 1 trong 3 là đủ. Nếu mất cả 3, bên kia sẽ timeout sau 10 giây — vẫn đúng, chỉ chậm hơn.

### Task 2 — `ReliabilityLayer`: sequence, ack, bitfield (4 ngày)

Trái tim của tầng transport.

```csharp
public sealed class ReliabilityLayer
{
    private const int SENT_BUFFER_SIZE = 1024;      // lịch sử gói đã gửi

    private struct SentPacket
    {
        public ushort Sequence;
        public double SentAtMs;
        public bool   Acked;
        public bool   IsReliable;
        public byte[] Data;        // null nếu unreliable (không cần giữ để resend)
        public int    Length;
        public int    ResendCount;
    }

    private readonly SentPacket[] _sent = new SentPacket[SENT_BUFFER_SIZE];
    private ushort _localSequence;          // seq của gói TA gửi tiếp theo
    private ushort _remoteSequence;         // seq lớn nhất TA đã nhận từ họ
    private uint   _receivedBitfield;       // 32 gói trước _remoteSequence

    // ===== GỬI =====
    public ushort NextSequence() => _localSequence++;

    public void OnPacketSent(ushort seq, ReadOnlySpan<byte> data, bool reliable, double nowMs)
    {
        int i = seq % SENT_BUFFER_SIZE;
        if (_sent[i].Data != null) _pool.Return(_sent[i].Data);   // ghi đè slot cũ
        byte[] copy = null;
        if (reliable) { copy = _pool.Rent(); data.CopyTo(copy); }
        _sent[i] = new SentPacket {
            Sequence = seq, SentAtMs = nowMs, Acked = false,
            IsReliable = reliable, Data = copy, Length = data.Length, ResendCount = 0 };
    }

    // ===== NHẬN =====
    public void OnPacketReceived(ushort seq)
    {
        if (SequenceMath.IsNewer(seq, _remoteSequence))
        {
            int shift = SequenceMath.Distance(seq, _remoteSequence);
            _receivedBitfield = shift >= 32 ? 0u : (_receivedBitfield << shift);
            _receivedBitfield |= 1u << (shift - 1);      // seq cũ giờ nằm ở bit (shift-1)
            _remoteSequence = seq;
        }
        else
        {
            int diff = SequenceMath.Distance(_remoteSequence, seq);
            if (diff >= 1 && diff <= 32)
                _receivedBitfield |= 1u << (diff - 1);   // gói tới muộn, đánh dấu đã nhận
            // diff > 32: quá cũ, bỏ qua (đã ra khỏi cửa sổ)
        }
    }

    public (ushort ack, uint bitfield) BuildAck() => (_remoteSequence, _receivedBitfield);

    // ===== XỬ LÝ ACK TỪ ĐỐI PHƯƠNG =====
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
        if (p.Sequence != seq || p.Acked) return;        // slot đã bị ghi đè, hoặc ack trùng
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

            if (++p.ResendCount > 10)                    // gửi lại 10 lần vẫn không được
            { NetLog.Warn($"seq {p.Sequence} bỏ cuộc sau 10 lần"); p.Acked = true;
              _pool.Return(p.Data); p.Data = null; continue; }

            resend(p.Data, p.Length);
            p.SentAtMs = nowMs;
            Stats.PacketsResent++;
        }
    }
}
```

**Cạm bẫy 1 — dịch bitfield khi seq nhảy xa.** Nếu nhận seq 200 khi `_remoteSequence` là 100,
`shift = 100`. `_receivedBitfield << 100` trong C# là **undefined behavior** (thực tế nó dịch
`100 % 32 = 4` bit — sai hoàn toàn). Phải kiểm tra `shift >= 32` → gán 0. Đây là bug rất khó
tìm vì chỉ xảy ra sau khi mất kết nối tạm rồi nối lại.

**Cạm bẫy 2 — slot bị ghi đè.** Buffer 1024 slot, `seq % 1024`. Nếu gửi 1025 gói mà gói đầu
chưa được ack, slot của nó bị gói mới ghi đè → mất luôn, không bao giờ retransmit. Ở 30 gói/s,
1024 gói = 34 giây. An toàn. Nhưng phải kiểm tra `p.Sequence != seq` trong `AckPacket` để không
ack nhầm gói mới.

**Cạm bẫy 3 — RTO quá ngắn gây bão retransmit.** Nếu `rto` nhỏ hơn RTT thật, mọi gói đều bị gửi
lại trước khi ack kịp về → lưu lượng nhân đôi → càng tắc nghẽn → càng chậm. Đây là *congestion
collapse*. Công thức `rtt * 1.5 + 4 * jitter`, kẹp sàn 30ms, là mức an toàn.

**Cạm bẫy 4 — đo RTT từ gói đã retransmit.** Nếu gói được gửi lại và ack về, bạn không biết ack
đó cho lần gửi nào → RTT đo sai (có thể âm hoặc quá lớn). Đây là *Karn's algorithm*: **không
cập nhật RTT từ gói đã retransmit**.

```csharp
private void AckPacket(ushort seq, double nowMs)
{
    // ...
    if (p.ResendCount == 0)              // Karn's algorithm
        UpdateRtt(nowMs - p.SentAtMs);
}
```

### Task 3 — RTT và jitter estimation (1 ngày)

```csharp
public float SmoothedRttMs { get; private set; }
public float JitterMs      { get; private set; }

private void UpdateRtt(double sampleMs)
{
    if (SmoothedRttMs <= 0f) { SmoothedRttMs = (float)sampleMs; return; }   // mẫu đầu

    // EWMA, ý tưởng từ RFC 6298
    float delta = (float)sampleMs - SmoothedRttMs;
    SmoothedRttMs += 0.125f * delta;                    // alpha = 1/8
    JitterMs      += 0.25f * (Math.Abs(delta) - JitterMs);  // beta = 1/4
}
```

Ghi lại vào `measurements.csv` mỗi giây để vẽ biểu đồ cho báo cáo.

### Task 4 — Bốn channel (3 ngày)

Theo [`protocol-spec.md § 5`](../../00-shared/protocol-spec.md#5-channel).

```csharp
public abstract class Channel
{
    public byte Id { get; }
    public abstract void OnSend(ReadOnlySpan<byte> payload, PacketQueue queue);
    public abstract void OnReceive(ushort channelSeq, ReadOnlyMemory<byte> payload,
                                   Action<ReadOnlyMemory<byte>> deliver);
}

/// <summary>Channel 0: giao ngay, không quan tâm thứ tự hay trùng lặp.</summary>
public sealed class UnreliableUnsequencedChannel : Channel
{
    public override void OnReceive(ushort seq, ReadOnlyMemory<byte> p, Action<ReadOnlyMemory<byte>> d)
        => d(p);
}

/// <summary>Channel 1, 3: giao nếu MỚI HƠN gói mới nhất đã giao. Gói cũ bị DROP.</summary>
public sealed class UnreliableSequencedChannel : Channel
{
    private ushort _lastDelivered;
    private bool   _hasDelivered;

    public override void OnReceive(ushort seq, ReadOnlyMemory<byte> p, Action<ReadOnlyMemory<byte>> d)
    {
        if (_hasDelivered && !SequenceMath.IsNewer(seq, _lastDelivered))
        { Stats.StalePacketsDropped++; return; }        // snapshot cũ hơn cái đã có = vô giá trị
        _lastDelivered = seq; _hasDelivered = true;
        d(p);
    }
}

/// <summary>Channel 2: giao đúng thứ tự, không mất. Gói tới sớm nằm chờ trong buffer.</summary>
public sealed class ReliableOrderedChannel : Channel
{
    private const int WINDOW = 256;
    private readonly ReadOnlyMemory<byte>?[] _pending = new ReadOnlyMemory<byte>?[WINDOW];
    private ushort _nextExpected;

    public override void OnReceive(ushort seq, ReadOnlyMemory<byte> p, Action<ReadOnlyMemory<byte>> d)
    {
        if (!SequenceMath.IsNewer(seq, (ushort)(_nextExpected - 1))) return;   // trùng, đã giao
        if (SequenceMath.Distance(seq, _nextExpected) >= WINDOW)
        { NetLog.Warn("gói vượt cửa sổ reliable, ngắt kết nối"); return; }

        // Buffer phải COPY — memory gốc sẽ bị trả về pool ngay sau hàm này
        _pending[seq % WINDOW] = CopyToOwnedBuffer(p);

        while (_pending[_nextExpected % WINDOW] is { } ready)   // giao liên tiếp
        {
            _pending[_nextExpected % WINDOW] = null;
            d(ready);
            ReturnOwnedBuffer(ready);
            _nextExpected++;
        }
    }
}
```

**Cạm bẫy 5 — buffer ownership ở reliable-ordered channel.** Gói tới sớm phải nằm chờ, nhưng
`ReadOnlyMemory` trỏ vào buffer sẽ được trả về pool ngay sau khi hàm trả về. **Bắt buộc copy.**
Nếu quên, sau vài giây bạn sẽ giao ra dữ liệu rác. Đây là bug rất khó tìm vì nó chỉ xảy ra khi
có mất gói.

**Cạm bẫy 6 — head-of-line blocking trong channel 2 là CỐ Ý.** Đừng "sửa" nó. Nếu message
"actor 5 chết" tới trước "actor 5 spawn", xử lý sai thứ tự sẽ vỡ trạng thái game. Đó là lý do
event dùng ordered, còn snapshot dùng channel khác. **Đây chính là luận điểm cốt lõi so sánh với
TCP** — hãy nêu rõ trong báo cáo: TCP bắt *mọi thứ* chung một dòng, ta chọn được từng loại.

### Task 5 — Fragmentation / reassembly (3 ngày)

Theo [`protocol-spec.md § 6`](../../00-shared/protocol-spec.md#6-fragmentation).

```csharp
public sealed class FragmentAssembler
{
    private const int MAX_PENDING_GROUPS = 8;           // chống DoS

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
            if (_groups.Count >= MAX_PENDING_GROUPS) EvictOldest();   // chống cạn RAM
            g = new Group { GroupId = groupId, Count = fragCount, FirstSeenMs = nowMs,
                            Parts = new byte[fragCount][], Lengths = new int[fragCount] };
            _groups[groupId] = g;
        }
        if (g.Count != fragCount) { _groups.Remove(groupId); return false; }  // không nhất quán
        if (g.Parts[fragIndex] != null) return false;                        // mảnh trùng

        g.Parts[fragIndex] = _pool.Rent();
        data.CopyTo(g.Parts[fragIndex]);
        g.Lengths[fragIndex] = data.Length;
        g.Received++;

        if (g.Received < g.Count) return false;
        // Đủ mảnh → ghép
        len = g.Lengths.Sum();
        full = new byte[len];                     // gói lớn, hiếm, cấp phát chấp nhận được
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

**Cạm bẫy 7 — DoS qua fragmentation.** Kẻ tấn công gửi hàng nghìn gói với `fragmentCount = 64`
nhưng chỉ 1 mảnh mỗi nhóm. Mỗi nhóm chiếm 64 slot buffer. Không giới hạn → cạn RAM trong vài
giây. `MAX_PENDING_GROUPS = 8` + timeout 2s là bắt buộc, không phải tối ưu.

**Cạm bẫy 8 — mảnh phải reliable.** Nếu gửi unreliable, mất 1 mảnh = mất cả nhóm 64 mảnh. Với
5% loss và 20 mảnh, xác suất mất ít nhất 1 mảnh = `1 - 0.95^20 = 64%`. Không chấp nhận được.

### Task 6 — Unit test (3 ngày) — ≥40 test

| Nhóm | Số test | Nội dung |
|---|---|---|
| `SequenceMath` | 6 | Biên wrap: (0,65535), (65535,0), (5,65530), (32768,0), bằng nhau, distance |
| `PacketHeader` | 8 | Round-trip, protocolId sai, buffer ngắn, payloadLength sai, mọi giá trị biên |
| `ReliabilityLayer` — ack | 8 | Nhận theo thứ tự, nhận đảo, nhận trùng, nhảy xa >32, bitfield đúng, ack nhiều gói |
| `ReliabilityLayer` — resend | 6 | Resend sau RTO, dừng khi ack, bỏ cuộc sau 10 lần, Karn's algorithm |
| Channel | 8 | Mỗi channel 2 test: hành vi bình thường + hành vi khi mất/đảo gói |
| Fragmentation | 6 | Ghép đúng, thiếu mảnh, mảnh trùng, timeout, vượt MAX_PENDING_GROUPS, fragCount không nhất quán |
| Handshake | 4 | Thành công, sai challenge, timeout, server đầy |
| **Tổng** | **46** | |

**Mẫu test quan trọng nhất — bitfield khi nhảy xa:**

```csharp
[Fact]
public void AckBitfield_KhiSequenceNhayXaHon32_PhaiReset()
{
    var r = new ReliabilityLayer();
    r.OnPacketReceived(1);
    r.OnPacketReceived(2);
    r.OnPacketReceived(3);
    var (ack1, bits1) = r.BuildAck();
    Assert.Equal(3, ack1);
    Assert.Equal(0b11u, bits1);            // đã nhận 2 và 1

    r.OnPacketReceived(200);               // nhảy 197 — vượt cửa sổ 32
    var (ack2, bits2) = r.BuildAck();
    Assert.Equal(200, ack2);
    Assert.Equal(0u, bits2);               // PHẢI reset, không được là rác do shift UB
}

[Fact]
public void SequenceMath_QuaBienWrap_PhaiDung()
{
    Assert.True (SequenceMath.IsNewer(0, 65535));      // 0 mới hơn 65535 (vừa wrap)
    Assert.False(SequenceMath.IsNewer(65535, 0));
    Assert.True (SequenceMath.IsNewer(5, 65530));
    Assert.Equal(6, SequenceMath.Distance(5, 65535));
}
```

---

## 3. Tiêu chí nghiệm thu (M1)

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | ≥40 unit test xanh | `dotnet test` |
| 2 | Handshake 4 bước hoạt động, chống spoof | Test: `CONNECT_RESPONSE` sai salt → bị từ chối |
| 3 | Gói reliable **luôn** tới ở 30% loss | Test: gửi 1000 gói reliable qua sim 30% loss → nhận đủ 1000, đúng thứ tự |
| 4 | Gói unreliable-sequenced **bỏ gói cũ** | Test: gửi seq 1,2,3 nhưng nhận thứ tự 3,1,2 → chỉ giao 3 |
| 5 | Fragmentation ghép đúng gói 20 KB | Test round-trip byte-by-byte |
| 6 | RTT đo được sai lệch < 10% so với latency simulator | Sim 100ms → đo được 95–110ms |
| 7 | Karn's algorithm: RTT không nhiễu bởi retransmit | Test có kiểm chứng |
| 8 | Kết nối sống 30 phút không rớt, không rò rỉ | Chạy dài, theo dõi `BufferPool.RentedCount` không tăng |
| 9 | **Sequence wrap sau 36 phút không gây lỗi** | Test: bơm sequence từ 65500 tới 100, kiểm tra hoạt động liên tục |
| 10 | 0 cấp phát heap trong hot path | Benchmark, đếm GC gen0 collection |
| 11 | Tích hợp: A chạy được 2 client thấy nhau | Cùng A xác nhận |

---

## 4. Rủi ro

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| Bug bitfield khi nhảy xa | Ack sai ngẫu nhiên sau khi mạng gián đoạn | Test #1 ở trên. Kiểm tra `shift >= 32` |
| Bão retransmit | Bandwidth tăng vọt, RTT tăng dần | RTO có sàn 30ms, Karn's algorithm |
| Rò rỉ buffer | `RentedCount` tăng dần theo thời gian | Log `RentedCount` mỗi 10 giây. Điều tra ngay khi thấy tăng |
| Dữ liệu rác ở reliable-ordered | Message có nội dung lạ khi có mất gói | Quên copy buffer (cạm bẫy 5). Bật `0xDD` fill trong Debug |
| Trễ tuần 6 | | Contingency: bỏ fragmentation (giới hạn message ≤ 1184 B, C phải chia nhỏ snapshot). Tiết kiệm 3 ngày |

---

## 5. Số liệu bắt buộc thu thập cho báo cáo

Chạy ma trận sau, mỗi ô 60 giây, ghi vào `reports/measurements.csv`:

| Loss | Reliable throughput | Retransmit % | Độ trễ giao trung bình (ordered) | Độ trễ giao P99 |
|---|---|---|---|---|
| 0% | | | | |
| 5% | | | | |
| 15% | | | | |
| 30% | | | | |

**Đo thêm để so sánh với TCP** (dùng lại code bài tập khởi động): cùng điều kiện, TCP cho độ trễ
P99 bao nhiêu? Đây là biểu đồ quan trọng nhất trong báo cáo — nó chứng minh vì sao game dùng UDP.
