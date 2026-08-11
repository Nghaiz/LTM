# Dev D — Phase 03: Triển khai VPS, TLS, monitoring

**Tuần 11–13** · Mốc **M3** · Ước lượng **2.5 người-tuần**

> Mục tiêu một câu: **hệ thống chạy trên Internet thật, 16 người thật, và ta nhìn thấy nó đang
> làm gì.**

---

## 1. Mục tiêu

| # | Mục tiêu |
|---|---|
| 1 | Triển khai master + game server lên VPS |
| 2 | TLS cho kết nối TCP (có truyền mật khẩu) |
| 3 | Monitoring: log có cấu trúc, chỉ số, cảnh báo |
| 4 | Load test 16 client, tìm và sửa nút thắt |
| 5 | Độ bền: chạy nhiều ngày không sập |
| 6 | Hỗ trợ tích hợp M3 |

---

## 2. Task chi tiết

### Task 1 — Chuẩn bị VPS (2 ngày)

**Cấu hình tối thiểu:** 2 vCPU, 4 GB RAM, Ubuntu 22.04. Game server headless Unity ăn RAM nhiều
hơn master server nhiều lần.

```
┌─ VPS ────────────────────────────────┐
│  master server   :27000/tcp (TLS)    │
│  game server 1   :27015/udp          │
│  game server 2   :27016/udp (dự phòng)│
└──────────────────────────────────────┘
```

**Firewall:**
```bash
sudo ufw allow 27000/tcp
sudo ufw allow 27015:27020/udp
sudo ufw enable
```

**systemd unit cho master:**
```ini
# /etc/systemd/system/ironfront-master.service
[Unit]
Description=Ironfront Master Server
After=network.target

[Service]
Type=simple
User=ironfront
WorkingDirectory=/opt/ironfront/master
EnvironmentFile=/opt/ironfront/.env
ExecStart=/usr/bin/dotnet Ironfront.MasterServer.dll
Restart=always
RestartSec=5
StandardOutput=append:/var/log/ironfront/master.log
StandardError=append:/var/log/ironfront/master.err.log

[Install]
WantedBy=multi-user.target
```

**Game server cần thêm:** Unity headless build Linux, cần `libc6`, và chạy với
`-batchmode -nographics -logFile`.

**Cạm bẫy 1 — `Restart=always` che giấu crash loop.** Nếu server crash mỗi 3 giây, systemd sẽ
restart mãi và trông như "đang chạy". Thêm `StartLimitBurst=5` và `StartLimitIntervalSec=60`
để nó dừng hẳn sau 5 lần crash trong 1 phút — bạn sẽ thấy vấn đề thay vì bị che.

**Cạm bẫy 2 — múi giờ và NTP.** joinTicket dựa vào timestamp. Nếu VPS lệch đồng hồ, ticket hết
hạn sai. Kiểm tra: `timedatectl status` phải thấy `NTP service: active`.

### Task 2 — TLS cho TCP (2 ngày)

Bạn đang truyền hash mật khẩu và session token qua Internet. Bắt buộc có TLS trước khi công khai.

```csharp
// Ironfront.MasterServer/Net/TlsWrapper.cs
public sealed class TlsClientConnection
{
    private SslStream _ssl;

    public async Task<bool> AuthenticateAsServerAsync(Socket socket, X509Certificate2 cert)
    {
        var net = new NetworkStream(socket, ownsSocket: false);
        _ssl = new SslStream(net, leaveInnerStreamOpen: false);
        try
        {
            await _ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = cert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
            return true;
        }
        catch (AuthenticationException e)
        { NetLog.Warn($"TLS handshake thất bại: {e.Message}"); return false; }
    }
}
```

**Điểm quan trọng — TLS KHÔNG thay thế framing.** `SslStream` vẫn là byte stream, vẫn không có
ranh giới message. `MspFraming` của bạn vẫn cần thiết y nguyên, chỉ là đọc từ `SslStream` thay
vì `Socket` trực tiếp. Đây là hiểu lầm phổ biến, đáng nêu trong báo cáo.

**Chứng chỉ:**
- Dev/LAN: self-signed, client bỏ qua validation (**chỉ** khi có cờ `--insecure`)
- VPS: Let's Encrypt qua `certbot` nếu có domain; nếu chỉ có IP thì self-signed + pin fingerprint
  trong client

```csharp
// Client pin fingerprint — an toàn hơn "bỏ qua mọi lỗi"
private bool ValidateServerCert(object s, X509Certificate cert, X509Chain chain, SslPolicyErrors e)
{
    if (e == SslPolicyErrors.None) return true;
    // Self-signed: chấp nhận nếu fingerprint khớp giá trị đã build vào client
    return cert.GetCertHashString(HashAlgorithmName.SHA256)
               .Equals(PINNED_FINGERPRINT, StringComparison.OrdinalIgnoreCase);
}
```

> **Không bao giờ** viết `(s, c, ch, e) => true` trong build phát hành. Nó vô hiệu hóa hoàn toàn
> TLS và mở đường cho tấn công man-in-the-middle. Nếu buộc phải có cho dev, gắn `#if DEBUG` và
> in cảnh báo đỏ ra console.

**Game server ↔ master cũng phải TLS** — nó truyền `serverSecret`.

**UDP không mã hóa** (quyết định B-AD-3, ngoài scope). Ghi rõ trong báo cáo là hạn chế đã biết.

### Task 3 — Monitoring (2 ngày)

**Log có cấu trúc** (JSON, một dòng một sự kiện — dễ grep và phân tích):

```csharp
public static class StructuredLog
{
    public static void Event(string type, object data)
        => Console.WriteLine(JsonSerializer.Serialize(new {
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), type, data }));
}

// Dùng:
StructuredLog.Event("login", new { playerId = 42, ip = "1.2.3.4", latencyMs = 15 });
StructuredLog.Event("room_join", new { playerId = 42, roomId = 7 });
StructuredLog.Event("gs_heartbeat", new { serverId = 1, players = 12, tickMs = 18.3 });
StructuredLog.Event("error", new { code = 3000, msg = "no server available" });
```

**Endpoint chỉ số** — thêm một TCP port riêng trả JSON (không dùng HTTP/ASP.NET, giữ nguyên tắc
TCP thuần):

```
$ nc localhost 27001
{
  "uptimeSec": 84213,
  "connections": { "current": 14, "peak": 17, "totalAccepted": 342 },
  "accounts":    { "total": 23, "onlineNow": 14 },
  "rooms":       { "active": 2, "inMatch": 1 },
  "gameServers": { "registered": 2, "healthy": 2, "allocated": 1 },
  "rates":       { "loginsPerMin": 3.2, "errorsPerMin": 0.1 },
  "resources":   { "workingSetMB": 78, "gen2Collections": 12, "threadCount": 14 }
}
```

**Cảnh báo tự động** — script chạy mỗi phút, gửi tin nhắn nhóm nếu:
- Không có game server nào healthy
- `errorsPerMin` > 10
- `workingSetMB` tăng > 50% so với 1 giờ trước (dấu hiệu rò rỉ)
- Master server không phản hồi

Đơn giản là đủ: một script bash + webhook Discord/Telegram.

### Task 4 — Load test 16 client thật (2 ngày)

Chạy `Ironfront.Tools.LoadTest` từ **máy khác VPS** (để đo cả đường truyền thật).

| Kịch bản | Thời lượng | Kiểm tra |
|---|---|---|
| 16 client `random-walk` | 30 phút | Băng thông, RTT, không rớt |
| 16 client `spin` (xấu nhất cho delta) | 15 phút | Băng thông đỉnh |
| 16 client `join-leave` liên tục | 15 phút | Rò rỉ session, rò rỉ room |
| 16 client `disconnect-abrupt` | 10 phút | Dọn dẹp server |
| 32 client (vượt thiết kế) | 10 phút | Xác định ngưỡng gãy |
| 100 kết nối TCP đồng thời tới master | 5 phút | Master chịu được |

**Số liệu cần thu, so LAN với Internet:**

| Chỉ số | LAN | VPS |
|---|---|---|
| Độ trễ login (p50 / p99) | | |
| Độ trễ room list | | |
| RTT UDP (p50 / p99) | | |
| Jitter UDP | | |
| Packet loss thực | | |
| Băng thông xuống/client | | |
| RAM master (16 client) | | |
| CPU master (16 client) | | |
| RAM game server | | |
| CPU game server | | |

### Task 5 — Độ bền (1 ngày)

**Chạy liên tục từ tuần 12 tới hết dự án.** Không tắt. Ghi chỉ số mỗi phút vào CSV.

Cuối kỳ vẽ biểu đồ: RAM, số kết nối, số lỗi theo thời gian. **Đường RAM tăng đơn điệu = rò rỉ.**

Đây là bằng chứng thuyết phục nhất về chất lượng hệ thống, và là thứ phân biệt "chạy được lúc
demo" với "chạy được".

### Task 6 — Sao lưu và khôi phục (nửa ngày)

```bash
# tools/backup.sh — cron mỗi 6 giờ
sqlite3 /opt/ironfront/ironfront.db ".backup /opt/ironfront/backups/db-$(date +%F-%H).db"
find /opt/ironfront/backups -name "db-*.db" -mtime +7 -delete
```

Dùng `.backup` chứ không phải `cp` — `cp` trên SQLite đang ghi sẽ cho file hỏng.

Test khôi phục một lần: dừng server, thay DB bằng bản backup, khởi động, kiểm tra đăng nhập được.
Backup chưa test khôi phục thì không phải backup.

---

## 3. Tiêu chí nghiệm thu (M3)

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | Master + 2 game server chạy trên VPS | `systemctl status` |
| 2 | TLS hoạt động, client kết nối được | Wireshark: không thấy plaintext |
| 3 | `MspFraming` vẫn đúng qua `SslStream` | Test tích hợp |
| 4 | Client build phát hành **không** bỏ qua validation cert | Code review |
| 5 | 16 client thật chơi 30 phút không rớt | Load test + log |
| 6 | Ngưỡng gãy đã xác định (32 client) | Load test |
| 7 | Endpoint chỉ số trả JSON đúng | `nc localhost 27001` |
| 8 | Cảnh báo tự động hoạt động | Test: tắt game server, chờ tin nhắn |
| 9 | Chạy liên tục 72 giờ, RAM không tăng đơn điệu | Biểu đồ CSV |
| 10 | Backup chạy tự động, đã test khôi phục | Log cron + test thủ công |
| 11 | Không có secret trong log | `grep -i secret /var/log/ironfront/*` |
| 12 | Bảng so sánh LAN vs VPS đã điền | `reports/` |

---

## 4. Rủi ro

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| VPS không đủ RAM cho game server Unity | OOM kill | Đo RAM game server trên máy dev trước khi thuê. Unity headless thường 500 MB – 1.5 GB |
| TLS handshake thất bại trên một số máy | Client không login được | Log chi tiết `AuthenticationException`. Thường là do cert self-signed hoặc protocol version |
| Lệch đồng hồ làm ticket hỏng | Vào trận thất bại ngẫu nhiên | `timedatectl`, bật NTP cả hai máy |
| Rò rỉ chỉ lộ sau nhiều ngày | RAM tăng chậm | Soak test từ tuần 12, không để tới tuần 14 |
| Chi phí VPS | | VPS 4GB khoảng 5–10 USD/tháng. Chia 4 người, 1 tháng. Hoặc dùng gói miễn phí của sinh viên (GitHub Student Pack, Azure/AWS free tier) |
| Trễ tuần 13 | | Contingency: bỏ TLS (demo LAN), bỏ monitoring nâng cao (chỉ log file) |

---

## 5. Danh sách kiểm tra trước khi mời người ngoài vào test

- [ ] TLS bật
- [ ] `IRONFRONT_SHARED_SECRET` là giá trị thật, không phải mặc định
- [ ] Log level = Info, không phải Debug (tránh đầy đĩa)
- [ ] Backup DB đã chạy ít nhất 1 lần
- [ ] Firewall chỉ mở đúng cổng cần
- [ ] Tài khoản test đã tạo sẵn, có hướng dẫn
- [ ] Cảnh báo tự động đã bật
- [ ] Có người trực trong lúc test
- [ ] Đã test luồng đầy đủ 10 lần từ máy ngoài mạng
