# P22 — Cứu bản chuẩn khôi phục vào repo

- **Created:** 2026-09-20. Đứng trước mọi phase khác của track port-back vì lý do dưới đây.
- **Base:** `develop`. **Track:** [`../plan.md`](../plan.md) §4.2.
- **Nguồn:** [`../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md`](../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md)
- **Kind:** tooling + data. Không đổi scope game, không đụng `Assets/Scripts`.

---

## 1. Vì sao phase này chạy trước

Chủ dự án tự reverse-engineer bản build Ravenfield Beta 5 gốc thành một project Unity 5.4.0f3 gần
như nguyên vẹn (408 file `Assembly-CSharp`, 5 631/5 631 method khớp IL, 0 asset reference gãy). Nó
nằm ở `tmp/recovered/` — **191 MB, và `tmp` có trong `.gitignore` (dòng 268)**.

Nghĩa là: bản chuẩn duy nhất để đối chiếu cho bốn phase còn lại **không được versioned**. Một lệnh
dọn `tmp/` là mất sạch, và việc reverse-engineer lại không rẻ. P22 trích phần bền vững ra, sau đó
xoá `tmp/recovered` cũng không mất gì.

**Không commit cả 191 MB.** Phần cần giữ là *dữ liệu đã trích*, không phải project.

## 2. Số đo — đã kiểm chứng trực tiếp trên `Ironfront_Reborn/` ngày 2026-09-20

Không lấy từ tài liệu trong `tmp/recovered`; đo lại bằng grep/parse trên chính hai cây thư mục.

| Hạng mục | Bản gốc | `Ironfront_Reborn` | Lệnh đo |
|---|---|---|---|
| `m_StaticEditorFlags` ≠ 0 — Dustbowl | 1 096 | **0** | `grep -o 'm_StaticEditorFlags: [0-9]*' <scene> \| grep -v ': 0$' \| wc -l` |
| `m_StaticEditorFlags` ≠ 0 — Island | 345 | **1** | ↑ |
| `GameObject` — Dustbowl | 5 587 | 5 465 (−122) | `grep -c '^GameObject:'` |
| `MeshRenderer` — Dustbowl | 2 307 | 2 173 (−134) | `grep -c '^MeshRenderer:'` |
| `MeshRenderer` — Island | 829 | **829** | ↑ |
| Material trỏ `m_Shader: {fileID: 45}` | — | **71 / 255** | `grep -rl 'm_Shader: {fileID: 45' --include=*.mat` |
| Shader file | 46 | 21 | `find -name '*.shader' \| wc -l` |
| `m_Script` GUID không giải được | 0 | **0** | so GUID tham chiếu vs GUID có trong `.meta` |
| Component rụng trên object khớp chắc | — | **8** (6×`Cloth`, 2×`GUILayer`) | so `m_Component` trên 4 363 object khớp fileID+tên |

Hai kết quả âm tính quan trọng, kèm phạm vi đã tìm: **không có `m_Script` GUID gãy nào** trên toàn
bộ `.unity`/`.prefab`/`.asset` dưới `Ironfront_Reborn/Assets` (37 GUID không giải được đều là asset
built-in của Unity, không có trong bản gốc lẫn bản này), và **component rụng chỉ 8 cái**, trong đó
`GUILayer` là component Unity đã xoá khỏi engine nên rụng là đúng. Lỗi logic của dự án **không** đến
từ script mất hay component rụng — đừng đào ở đó.

## 3. Khoá khớp object — `fileID` vô dụng, đã đo

Đây là dữ kiện quyết định cách viết mọi script của P23 và P26.

| Khoá | Dustbowl | Island |
|---|---|---|
| `fileID` | **0 / 1 096** | 1 / 345 |
| `m_Name` | 179 duy nhất, **917 mơ hồ** | 155 duy nhất, 189 mơ hồ |
| **`(m_Name, m_LocalPosition)`** | **792 duy nhất**, 55 mơ hồ, 241 không khớp | **341 duy nhất**, **0 mơ hồ**, 4 không khớp |

2 183 fileID có trùng giữa hai scene, nhưng **không cái nào là object static** — chúng là probe,
light, manager còn giữ ID Unity 5.4; toàn bộ prop hình học bị cấp ID mới khi project bị nâng lên
2017.3. 241 object Dustbowl không khớp trùng khớp với phần hình học bị mất, chỉ giải được ở P26.

## 4. Bẫy cú pháp YAML — hai scene dùng hai định dạng khác nhau

Bắt buộc phải xử lý trong mọi parser của track này:

```yaml
# bản gốc (Unity 5.4)          # project (Unity 6)
m_Component:                   m_Component:
- 4: {fileID: 6419}            - component: {fileID: 6419}
- 215: {fileID: 18473}
```

Parser viết cho một định dạng sẽ **im lặng trả về rỗng** trên định dạng kia — không lỗi, không cảnh
báo. Đó chính xác là cách một lần đo trong quá trình khảo sát cho ra danh sách component rỗng và
suýt dẫn tới kết luận sai.

## 5. Prior art — dùng lại, đừng viết mới

Đã tìm trên `tools/` và `Ironfront_Reborn/Assets/Editor/`:

| Có sẵn | Dùng cho |
|---|---|
| `tools/ClientWiringGate/UnityAssetYaml.cs` | parser YAML asset Unity bằng C# — **mở rộng cái này**, đừng viết parser thứ hai |
| `tools/extract_weapon_registry.py` | mẫu script Python trích dữ liệu từ asset Unity |
| `tools/unity-wire-dustbowl-netclient.cs` | mẫu script sửa scene có kiểm soát |
| `tools/strip-removed-components.ps1` | tiền lệ xử lý component bị engine xoá |
| `Ironfront_Reborn/Assets/Editor/AssetRipperPatches/` | nơi đặt Editor script vá sản phẩm AssetRipper |

## 6. Việc phải làm

### 6.1 Trích dữ liệu

```
tools/recovered/static-flags.Dustbowl.json    1096 mục: {name, parentPath, localPos, flags}
tools/recovered/static-flags.Island.json      345 mục, cùng schema
tools/recovered/material-shader-map.json      71 material → shader đúng + property list gốc
tools/recovered/missing-objects.Dustbowl.json 122 object + cây con + component + transform
tools/recovered/scene-baseline.json           GO / MeshRenderer / static count mỗi scene
```

`parentPath` là đường dẫn hierarchy đầy đủ, **bắt buộc có** — dùng để gỡ 55 mục mơ hồ của Dustbowl
ở P23 mà không phải đoán.

### 6.2 Trích shader

3 shader custom đã được viết lại tay trong `tmp/recovered/` (`Custom/Flag`,
`Custom/Multiply No Soft`, `Custom/StandardDoubleSide`) → `Ironfront_Reborn/Assets/Shader/`.
**Chưa gán vào material ở phase này** — chỉ đưa file vào repo. Gán là việc của P26.

### 6.3 Trích tài liệu

`docs/recovered-baseline.md` — 4 file `.md` gốc trong `tmp/recovered/` (README, SHADERS_TODO,
SO_SANH_VOI_REPO_CONG_KHAI, STATIC_OBJECTS) hợp nhất, cộng bảng đo ở §2–§4 của phase này. Ghi rõ
dòng nào là *lời tài liệu gốc* và dòng nào là *đo lại được*.

### 6.4 Script trích, chạy lại được

`tools/extract-recovered.py` — nhận đường dẫn tới `tmp/recovered/`, sinh ra toàn bộ JSON ở §6.1.
Không phải script dùng một lần: nếu bản khôi phục được cập nhật, chạy lại là ra dữ liệu mới. Script
phải **fail lớn tiếng** khi đường dẫn không tồn tại, không được sinh file rỗng.

## 7. Nghiệm thu

1. `python tools/extract-recovered.py` chạy sạch, sinh đúng 5 file JSON.
2. Số dòng mỗi JSON khớp §2: 1096 / 345 / 71 / 122 / 4 scene.
3. `docs/recovered-baseline.md` tồn tại, chứa cả bảng đo lẫn bẫy YAML §4.
4. **Kiểm tra quyết định:** đổi tên `tmp/recovered` → chạy `tools/ci.ps1` → vẫn xanh. Chứng minh
   không phase nào sau này phụ thuộc vào một thư mục gitignore.
5. 3 shader custom có mặt dưới `Assets/Shader/` kèm `.meta`.

## 8. Risk Assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Parser viết cho 1 định dạng YAML, im lặng trả rỗng trên định dạng kia | 4 | 4 | **16** | Test parser trên **cả hai** scene, khẳng định số lượng khớp §2 trước khi ghi JSON |
| `tmp/recovered` bị xoá trước khi trích xong | 2 | 5 | 10 | Chạy P22 trước tiên; §7.4 chứng minh đã độc lập |
| JSON trích ra thiếu `parentPath` → P23 không gỡ được 55 mục mơ hồ | 3 | 3 | 9 | `parentPath` là trường bắt buộc trong schema, kiểm ở §7.2 |
| Commit nhầm cả 191 MB | 2 | 3 | 6 | Chỉ commit `tools/recovered/*.json`, `docs/`, 3 `.shader`; `git status` trước khi push |

Không có rủi ro ≥ 15 nào chưa được giảm thiểu.

## 9. Timeline

| Việc | Effort | Ghi chú |
|---|---|---|
| Parser hai định dạng + test | S | Mở rộng `UnityAssetYaml.cs` hoặc Python thuần |
| 5 file JSON + script trích | S | |
| `docs/recovered-baseline.md` | S | |
| **Tổng** | **S (~1 ngày)** | Không phụ thuộc phase nào |

## 10. Không thuộc phase này

Không đặt cờ static, không gán shader, không dựng object, không đụng `Assets/Scripts`. P22 chỉ
chuyển ground truth từ chỗ mất được sang chỗ không mất được.
