# Danh mục vũ khí và trang bị

Tài liệu này liệt kê **17 trang bị trong loadout của nhân vật** ở phiên bản mã nguồn hiện tại. Cột **ô trang bị** dùng đúng cách phân nhóm của game: vũ khí chính, vũ khí phụ, gear và gear cỡ lớn. Cột **loại** mô tả cách sử dụng của từng món.

| ID | Tên trong game | Ô trang bị | Loại | Công dụng |
|---:|---|---|---|---|
| 1 | RK-44 | Vũ khí chính | Súng trường tấn công | Bắn tự động để giao chiến ở cự ly gần và trung bình. |
| 2 | S-IND7 | Vũ khí phụ | Súng ngắn | Vũ khí dự phòng, bắn từng phát. |
| 3 | S-IND7 [SUP] | Vũ khí phụ | Súng ngắn giảm thanh | Phiên bản giảm thanh của S-IND7, dùng làm vũ khí dự phòng. |
| 4 | 76 EAGLE | Vũ khí chính | Shotgun | Bắn nhiều viên ghém trong một phát, phù hợp giao chiến tầm gần. |
| 5 | BEU AW1 | Gear cỡ lớn | Súng phóng rocket | Phóng rocket gây nổ để tấn công mục tiêu và phương tiện. |
| 6 | SL-DEFENDER | Vũ khí chính | Súng bắn tỉa | Bắn chính xác ở cự ly xa. |
| 7 | FRAG | Gear | Lựu đạn nổ | Ném lựu đạn; phát nổ sau thời gian kích nổ để gây sát thương trong vùng. |
| 8 | SPEARHEAD | Gear | Lựu đạn nổ | Ném lựu đạn gây nổ trong vùng; có nhiều đạn dự trữ hơn FRAG. |
| 9 | BINOCS | Gear | Ống nhòm | Quan sát và đo khoảng cách tới điểm ngắm; có thể đánh dấu điểm để ra lệnh cho đội AI di chuyển. |
| 10 | AMMO BAG | Gear | Túi tiếp đạn | Ném túi tiếp tế để bổ sung đạn cho nhân vật ở gần. |
| 11 | MEDIPACK | Gear | Túi cứu thương | Ném túi cứu thương để hồi máu cho nhân vật ở gần. |
| 12 | BIL SCALPEL | Gear cỡ lớn | Tên lửa dẫn đường | Khóa mục tiêu, đặc biệt là phương tiện, rồi phóng tên lửa; cũng hỗ trợ chọn điểm đích thủ công. |
| 13 | SIGNAL DMR | Vũ khí chính | Súng trường thiện xạ | Bắn chính xác ở cự ly trung bình và xa. |
| 14 | N.V. GOGGLES | Gear | Kính nhìn đêm | Bật hoặc tắt hiệu ứng nhìn đêm để quan sát trong môi trường tối. |
| 15 | RECON LRR | Vũ khí chính | Súng trường có ống ngắm | Bắn từng phát chính xác ở cự ly xa. |
| 16 | WRENCH | Gear | Vũ khí cận chiến kiêm dụng cụ sửa chữa | Đánh ở cự ly gần và sửa chữa phương tiện khi đánh trúng. |
| 17 | SUPER WRENCH | Gear | Cờ lê đặc biệt | Biến thể mạnh hơn của WRENCH, có hiệu ứng tác động vật lý và đổi bề mặt vật trúng sang màu vàng. **Đang ẩn khỏi màn hình chọn trang bị thông thường.** |

## Tóm tắt theo ô trang bị

| Ô trang bị | Số mục | Hiện trên màn hình chọn trang bị |
|---|---:|---:|
| Vũ khí chính | 5 | 5 |
| Vũ khí phụ | 2 | 2 |
| Gear | 8 | 7 |
| Gear cỡ lớn | 2 | 2 |
| **Tổng** | **17** | **16** |

## Trang bị trên phương tiện

**CAR HORN (ID 18)** là còi xe: dùng để bấm còi và làm người ngồi trên xe bị AI chú ý. Nó có ID vũ khí để đồng bộ sự kiện trong mạng, nhưng **không nằm trong danh sách loadout** của nhân vật.

## Nguồn đối chiếu

- [Danh sách tên, ID, ô trang bị và trạng thái ẩn](../Ironfront_Reborn/Assets/Resources/_Managers.prefab)
- [Định nghĩa các ô trang bị](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/WeaponManager.cs)
- [Cấu hình và phân loại vũ khí phía server](../Ironfront.Net.Replication/Combat/WeaponCatalog.cs)
- [ID vũ khí, gồm còi xe](../Ironfront.Net.Protocol/WeaponIds.cs)
- [Chức năng ống nhòm](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Binoculars.cs), [kính nhìn đêm](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/NightVision.cs), [túi tiếp đạn](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Ammobox.cs), [túi cứu thương](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Medipack.cs), [cờ lê](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Wrench.cs) và [tên lửa dẫn đường](../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Javelin.cs)
