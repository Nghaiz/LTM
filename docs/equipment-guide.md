# Danh mục vũ khí và trang bị

Tài liệu này liệt kê **17 trang bị trong loadout của nhân vật** ở phiên bản mã nguồn hiện tại. Cột **ô trang bị** dùng đúng cách phân nhóm của game: vũ khí chính, vũ khí phụ, gear và gear cỡ lớn. Cột **loại** mô tả cách sử dụng của từng món.

| ID | Tên trong game | Ô trang bị | Loại | Công dụng |
|---:|---|---|---|---|
| 1 | RK-44 | Vũ khí chính | Súng trường tấn công | Bắn tự động để giao chiến ở cự ly gần và trung bình. |
| 2 | S-IND7 | Vũ khí phụ | Súng ngắn | Vũ khí dự phòng, bắn từng phát. |
| 3 | S-IND7 [SUP] | Vũ khí phụ | Súng ngắn giảm thanh | Phiên bản giảm thanh của S-IND7, dùng làm vũ khí dự phòng. |
| 4 | 76 EAGLE | Vũ khí chính | Shotgun | Bắn nhiều viên ghém trong một phát, phù hợp giao chiến tầm gần. |
| 5 | BEU AW1 | Gear cỡ lớn | Súng phóng rocket | Phóng rocket gây nổ để tấn công mục tiêu và phương tiện. |
| 6 | SL-DEFENDER | Vũ khí chính | Súng bắn tỉa | Bắn chính xác ở cự ly xa. Ống ngắm 6x / 12x / 25x (lăn chuột khi đang ngắm), chỉnh zero 100–1000 m (Page Up / Page Down), có máy đo xa; giữ phím chạy (Shift) khi ngắm để nín thở cho tâm ngắm đứng yên. |
| 7 | FRAG | Gear | Lựu đạn nổ | Ném lựu đạn; phát nổ sau thời gian kích nổ để gây sát thương trong vùng. |
| 8 | SPEARHEAD | Gear | Lựu đạn nổ | Ném lựu đạn gây nổ trong vùng; có nhiều đạn dự trữ hơn FRAG. |
| 9 | BINOCS | Gear | Ống nhòm | Quan sát và đo khoảng cách tới điểm ngắm; có thể đánh dấu điểm để ra lệnh cho đội AI di chuyển. |
| 10 | AMMO BAG | Gear | Túi tiếp đạn | Ném túi tiếp tế để bổ sung đạn cho nhân vật ở gần. |
| 11 | MEDIPACK | Gear | Túi cứu thương | Ném túi cứu thương để hồi máu cho nhân vật ở gần. |
| 12 | BIL SCALPEL | Gear cỡ lớn | Tên lửa dẫn đường | Khóa mục tiêu, đặc biệt là phương tiện, rồi phóng tên lửa; cũng hỗ trợ chọn điểm đích thủ công. |
| 13 | SIGNAL DMR | Vũ khí chính | Súng trường thiện xạ | Bắn chính xác ở cự ly trung bình và xa. Ống ngắm lăng kính 4x cố định. |
| 14 | N.V. GOGGLES | Gear | Kính nhìn đêm | Bật hoặc tắt hiệu ứng nhìn đêm để quan sát trong môi trường tối. |
| 15 | RECON LRR | Vũ khí chính | Súng trường có ống ngắm | Bắn từng phát chính xác ở cự ly xa. Ống ngắm 6x cố định với vạch chữ V, chỉnh zero 100–800 m (Page Up / Page Down); giữ Shift khi ngắm để nín thở. |
| 16 | WRENCH | Gear | Vũ khí cận chiến kiêm dụng cụ sửa chữa | Đánh ở cự ly gần và sửa chữa phương tiện khi đánh trúng. |
| 17 | SUPER WRENCH | Gear | Cờ lê đặc biệt | Biến thể mạnh hơn của WRENCH, có hiệu ứng tác động vật lý và đổi bề mặt vật trúng sang màu vàng. **Ẩn khỏi màn hình chọn trang bị** cho tới khi gõ mã `ISEEGOLD` ở menu chính (bí mật của game gốc); sau đó chỉ xuất hiện trong practice. |

## Tóm tắt theo ô trang bị

| Ô trang bị | Số mục | Hiện trên màn hình chọn trang bị |
|---|---:|---:|
| Vũ khí chính | 5 | 5 |
| Vũ khí phụ | 2 | 2 |
| Gear | 8 | 7 |
| Gear cỡ lớn | 2 | 2 |
| **Tổng** | **17** | **16** |

## Độ giật và độ tản của súng

Từ bản v4.6.1, đạn luôn bay đúng theo hướng ngắm (tâm ngắm), còn độ giật làm **chính tâm ngắm** nhích lên sau mỗi phát; đạn đi theo tâm ngắm nên người chơi ghì chuột xuống để giữ loạt đạn trên mục tiêu, như súng thật. Ngừng bắn một lúc thì tay tự kéo lại một phần (40–85 % tùy súng), phần còn lại là của người chơi. Ngắm bằng ống ngắm (chuột phải) cho độ tản nhỏ nhất; bắn không ngắm (hip-fire) và bắn khi đang di chuyển thì đạn tản rộng hơn, tối đa gấp 3 lần.

| Súng | Nguyên mẫu | Độ giật mỗi phát | Ghi chú |
|---|---|---|---|
| RK-44 | AK, 7.62×39 | nhẹ (0,45°) | Loạt dài vẫn kiểm soát được. |
| SIGNAL DMR | súng thiện xạ 7.62×51 bắn liên thanh | trung bình (0,85°) | Nên bắn từng phát hoặc loạt ngắn. |
| RECON LRR | bullpup .308 bán tự động | mạnh (1,4°) | |
| SL-DEFENDER | bolt-action .338 | rất mạnh (3°) | Tay kéo lại gần hết trong lúc lên đạn. |
| S-IND7 / [SUP] | súng ngắn 9 mm | 1,1° / 0,9° | Bản giảm thanh giật nhẹ hơn. |
| 76 EAGLE | shotgun bơm 12 ga | rất mạnh (4°) | |

## Trang bị trên phương tiện

**CAR HORN (ID 18)** là còi xe: dùng để bấm còi và làm người ngồi trên xe bị AI chú ý. Nó có ID vũ khí để đồng bộ sự kiện trong mạng, nhưng **không nằm trong danh sách loadout** của nhân vật.

## Nguồn đối chiếu

- [Danh sách tên, ID, ô trang bị và trạng thái ẩn](../Ironfront_Reborn/Assets/Resources/_Managers.prefab)
- [Định nghĩa các ô trang bị](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/WeaponManager.cs)
- [Cấu hình và phân loại vũ khí phía server](../Ironfront.Net.Replication/Combat/WeaponCatalog.cs)
- [ID vũ khí, gồm còi xe](../Ironfront.Net.Protocol/WeaponIds.cs)
- [Chức năng ống nhòm](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Binoculars.cs), [kính nhìn đêm](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/NightVision.cs), [túi tiếp đạn](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Ammobox.cs), [túi cứu thương](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Medipack.cs), [cờ lê](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Wrench.cs) và [tên lửa dẫn đường](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Javelin.cs)
