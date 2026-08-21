# Bàn giao hạ tầng — ngtukien

- **Ngày:** 2026-08-21 · **Repo:** `Nghaiz/LTM` (đã chuyển chủ sở hữu, PUBLIC)
- **VM:** `20.214.142.73`, Korea Central, `Standard_B2s_v2`, Ubuntu 24.04, user `ironadmin`
- **Tiếp nối:** issue [#78](https://github.com/Nghaiz/LTM/issues/78) mục 3.2–3.6, [#127](https://github.com/Nghaiz/LTM/issues/127)

---

## 0. Đọc cái này trước

**Việc của bạn chỉ là cấu hình hạ tầng.** Không build, không sửa code, không `docker build`.
Mọi thứ thuộc phần mềm đã xong và đã kiểm chứng ở local.

Trước đây bạn kẹt ở mục 3.2 vì **chưa từng có image game-server**. Lý do thật không phải billing:
image tồn tại nhưng build từ một server **không mở nổi cổng UDP** — nó boot vào menu rồi đứng im,
container vẫn `Up`, không log lỗi nào. Không có bước cấu hình hạ tầng nào phát hiện được điều đó.
Lỗi ấy đã sửa và đã chứng minh trên chính artifact Linux (chi tiết:
[`plans/consolidation/plan.md`](../plans/consolidation/plan.md) § 2).

> **Chờ tín hiệu bắt đầu.** Bạn chỉ bắt đầu khi nhận được **digest** của image game-server.
> Chưa có digest thì chưa có gì để deploy — đừng chạy `deploy.sh up`, nó sẽ báo lỗi biến
> `IRONFRONT_GAMESERVER_IMAGE` chưa đặt và đó là hành vi đúng.

---

## 1. Điều kiện tiên quyết

| Cần | Lấy ở đâu |
|---|---|
| SSH key vào VM | key hiện có của bạn; IP `171.224.181.111` đã được cho phép |
| Một tên miền trỏ được về `20.214.142.73` | bạn chọn; ví dụ `master.ironfront.<domain>` |
| `IRONFRONT_SHARED_SECRET` | **tự sinh** (bước 4), rồi gửi lại cho chủ dự án qua kênh riêng |
| Digest image master + game-server | chủ dự án gửi, dạng `ghcr.io/nghaiz/...@sha256:...` |

Nếu IP nhà bạn đổi: sửa `ssh_source_cidrs` trong `infra/terraform/terraform.tfvars` rồi
`terraform apply`. Đừng mở `0.0.0.0/0`.

---

## 2. Kiểm tra cloud-init đã xong

```bash
ssh -i ~/.ssh/id_rsa_azure ironadmin@20.214.142.73
cat /opt/ironfront/.bootstrap-done     # phải tồn tại
```

Chưa có thì `sudo tail -50 /var/log/cloud-init-output.log` và dừng lại ở đây.

---

## 3. DNS

Tạo bản ghi **A**: `master.ironfront.<domain>` → `20.214.142.73`.

```bash
dig +short master.ironfront.<domain>    # phải trả về đúng 20.214.142.73
```

Chưa propagate thì **đừng sang bước 4** — Let's Encrypt sẽ fail và bạn bị rate-limit.

---

## 4. Chứng chỉ TLS và file `.env`

TLS là **bắt buộc**, không phải tuỳ chọn: đường MSP mang hash mật khẩu mà master coi *chính là*
mật khẩu, và link đăng ký game-server↔master mang shared secret.

```bash
cd /opt/ironfront
sudo ./issue-cert.sh master.ironfront.<domain>      # sinh /opt/ironfront/tls/master.pfx
```

Sinh secret (≥32 ký tự) và mật khẩu PFX:

```bash
openssl rand -base64 48    # dùng cho IRONFRONT_SHARED_SECRET
```

Chép template rồi điền:

```bash
cp .env.example .env
chmod 600 .env
nano .env
```

Các dòng **bắt buộc** phải đúng:

```bash
IRONFRONT_DOMAIN=master.ironfront.<domain>
IRONFRONT_PUBLIC_IP=20.214.142.73

IRONFRONT_SHARED_SECRET=<chuỗi openssl ở trên>
IRONFRONT_TLS_CERT_PASSWORD=<mật khẩu bạn đặt cho master.pfx>

IRONFRONT_MASTER_IMAGE=ghcr.io/nghaiz/ironfront-master@sha256:<digest>
IRONFRONT_GAMESERVER_IMAGE=ghcr.io/nghaiz/ironfront-game-server@sha256:<digest>

IRONFRONT_GAMESERVER_SCENE=Dustbowl
```

Bốn điều dễ sai, mỗi cái đều từng làm hỏng một lần deploy:

1. **Luôn ghim `@sha256:`, không dùng `:latest`.** Tag di chuyển được; digest thì không.
2. **`IRONFRONT_SHARED_SECRET` phải giống hệt nhau** cho master và cả hai game-server. Lệch một
   ký tự thì mọi `CONNECT_REQUEST` bị từ chối với reason 3, và log không nói vì sao.
3. **`IRONFRONT_PUBLIC_IP` phải là IP public thật.** Đây là địa chỉ master phát cho người chơi.
   Điền IP nội bộ của container thì client nhận được một địa chỉ không ai gọi tới được.
4. **`IRONFRONT_GAMESERVER_SCENE` phải là tên scene có trong build** — chỉ `Dustbowl` hoặc
   `Island`. Sai tên thì server **không mở cổng UDP** và vẫn trông khoẻ mạnh. Log sẽ có dòng
   `[server] scene '<tên>' is not in the build`.

`.env` **không bao giờ** được commit. Nó đã nằm trong `.gitignore`.

---

## 5. Kéo image và khởi động

```bash
cd /opt/ironfront
./deploy.sh digests      # GHI LẠI digest hiện tại TRƯỚC khi đổi bất cứ thứ gì
./deploy.sh up
docker compose ps
```

Mong đợi: `master` (healthy), `game-server-1`, `game-server-2` đều `Up`.

---

## 6. Nghiệm thu — bốn kiểm tra, làm đủ cả bốn

`Up` **không** có nghĩa là chạy được. Container `Up` mà không mở cổng chính là lỗi đã mất ba ngày
để tìm ra. Bốn kiểm tra dưới đây phân biệt được hai trạng thái đó.

**6.1 — Master nghe TCP + TLS**

```bash
openssl s_client -connect master.ironfront.<domain>:27000 -servername master.ironfront.<domain> </dev/null 2>&1 | grep -E "subject=|Verify return code"
```
Phải thấy `Verify return code: 0 (ok)`.

**6.2 — Game-server thật sự mở cổng UDP** ← quan trọng nhất

```bash
sudo ss -lunp | grep -E '2701[56]'
```
Phải thấy **cả 27015 và 27016**. Không thấy → xem `docker compose logs game-server-1 | grep '\[server\]'`;
gần như chắc chắn là `IRONFRONT_GAMESERVER_SCENE` sai.

**6.3 — Game-server đăng ký được với master**

```bash
curl -s http://127.0.0.1:27001/metrics | grep -iE 'gameserver|healthy|registered'
```
Phải thấy **2** server registered và healthy. Bằng 0 → sai `IRONFRONT_SHARED_SECRET`, hoặc TLS
chưa hợp lệ, hoặc `IRONFRONT_GAMESERVER_MASTER_TLS_TARGET_HOST` không khớp tên trên chứng chỉ.

**6.4 — Nới buffer nhận UDP** (đã đo được, không phải phòng xa)

Server báo: `socket receive buffer clamped to 425984 B (asked for 1048576 B)`. Mặc định của kernel
thấp hơn mức server xin, nên **sẽ mất gói khi đông người**.

```bash
echo 'net.core.rmem_max = 1048576' | sudo tee /etc/sysctl.d/60-ironfront.conf
sudo sysctl --system
cd /opt/ironfront && ./deploy.sh up      # khởi động lại để nhận buffer mới
docker compose logs game-server-1 | grep -c 'clamped'   # mong đợi: 0
```

---

## 7. Backup và cảnh báo

```bash
sudo systemctl enable --now ironfront-backup.timer ironfront-alert.timer
systemctl list-timers | grep ironfront
sudo systemctl start ironfront-backup.service     # chạy thử một lần
journalctl -u ironfront-backup.service -n 30      # phải thấy upload thành công
```

Đặt webhook cảnh báo vào `/opt/ironfront/.env` (`IRONFRONT_ALERT_WEBHOOK`). Bộ cảnh báo chạy
**trên host** chứ không trong container — thứ theo dõi phải sống ngoài thứ nó theo dõi.

---

## 8. Bảng lỗi → nguyên nhân

| Bạn thấy | Nghĩa là | Sửa |
|---|---|---|
| Container `Up`, `ss` không thấy 27015 | Server đang ở scene không có `NetServerBootstrap` | Sửa `IRONFRONT_GAMESERVER_SCENE` |
| `[server] scene 'X' is not in the build` | Sai tên scene | Chỉ dùng `Dustbowl` hoặc `Island` |
| Metrics báo 0 server registered | Secret lệch, hoặc TLS/tên host sai | Bước 4 mục 2, và `IRONFRONT_GAMESERVER_MASTER_TLS_TARGET_HOST` |
| Client bị `CONNECT_DENIED` reason 3 | Ticket ký bằng secret khác | Đồng bộ secret cho **cả ba** service rồi restart hết |
| `Verify return code` khác 0 | Chứng chỉ chưa hợp lệ / sai tên miền | Chạy lại bước 4 sau khi DNS đã propagate |
| `deploy.sh up` báo thiếu biến | `.env` thiếu dòng bắt buộc | Đó là hành vi đúng — điền vào, đừng đặt default |
| `clamped` vẫn xuất hiện sau 6.4 | Chưa restart container | `./deploy.sh up` lại |
| Client vào được rồi rớt sau ~1 giây | **Image cũ trước bản vá transport** | Kiểm tra digest đúng bản mới |

---

## 9. Ranh giới — việc KHÔNG thuộc về bạn

Build Unity · build/push Docker image · sửa code hay `.github/workflows/` · xử lý PR · chọn scene
mặc định trong code.

Gặp thứ nào trong số đó: dừng, báo lại kèm log, đừng tự sửa. Nếu cần một image mới, xin digest —
đừng build.

---

## 10. Khi xong

Báo lại đúng bốn thứ:

1. Tên miền đã cấp chứng chỉ
2. Kết quả **cả bốn** kiểm tra ở § 6 (dán output thật, không viết "OK")
3. Hai digest đang chạy (`./deploy.sh digests`)
4. Timer backup/alert đã bật và lần chạy thử đã thành công

Tham chiếu: [`operations.md`](operations.md) (vận hành hằng ngày) ·
[`infrastructure-handover.md`](infrastructure-handover.md) (ai giữ cái gì) ·
[`plans/consolidation/plan.md`](../plans/consolidation/plan.md) (bối cảnh kỹ thuật)
