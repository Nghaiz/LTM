# Thành tựu (achievements): thiết kế lại từ đầu

> **Trạng thái: ĐÃ DUYỆT (2026-10-09, bản 5), ĐANG LÀM.** Tên và dòng mô tả trong game giữ tiếng
> Anh vì toàn bộ giao diện game là tiếng Anh; phần giải thích viết tiếng Việt. Mục 9 ghi tiến độ làm
> và những gì đo được trong lúc làm; tôi cập nhật mục đó tới khi phát hành xong.
>
> Bản 5 (anh duyệt kèm sửa): lên **80 thành tựu**, 10 cái thêm là **practice** rải đều các bản đồ và
> các chế độ, trong đó có **GOLD STANDARD** (cờ lê vàng, mở bằng mã `ISEEGOLD` như game gốc);
> PARTICIPATION TROPHY so cả bot; Night Mode tính theo **việc có bật night vision (phím N) hay
> không**; chọn đội **tự do hoàn toàn**; thành tựu ẩn có **câu gợi ý riêng** thay cho dòng "Earn it
> to read the conditions".

Toàn bộ 50 thành tựu hiện tại bị xoá. Thay bằng **80 thành tựu mới**, một danh sách duy nhất không
chia nhóm, xếp từ dễ tới khó. Trong đó **15 cái ẩn**, **16 cái Mythic** và **15 cái practice**.

---

## 1. Vì sao phải đập đi làm lại

| Vấn đề | Ví dụ trong bộ cũ |
|---|---|
| Ngưỡng quá thấp so với một trận 100 bot | UNSTOPPABLE = 10 mạng liên tiếp, TRIPLE THREAT = 3 mạng trong 3 giây, LONG SHOT = 150 m, EAGLE EYE = headshot 300 m. Một trận Forest Lake 100 bot cho dư những thứ này. |
| Hạng không khớp độ khó thật | FLAWLESS (15 mạng, 0 chết) là Platinum nhưng vs bot thì dễ hơn nhiều thành tựu Gold. |
| Practice phụ thuộc đồng đội bot | ONE-MAN ARMY = "đội anh thắng trận practice 100 bot". 50 bot phe anh tự thắng hộ. |
| Không có gì thật sự khó | Platinum khó nhất là 25 mạng liên tiếp và 10.000 mạng. |
| Thành tựu ẩn chỉ là ô "???" xám | Không có gì để đoán. |
| Không thấy thông báo khi mở khoá | Hôm nay practice mở BASIC TRAINING và ONE-MAN ARMY mà anh không thấy banner nào (mục 5.1). |

## 2. Tham khảo từ game khác (chỉ những gì đã kiểm chứng)

- **Google Play Games, checklist chất lượng thành tựu** (tài liệu chính thức): rải độ khó; không trao
  quá 1 thành tựu trong 5 phút đầu; thành tựu tích luỹ có thanh tiến độ; tên và mô tả phải cho người
  chơi biết rõ cần làm gì; thành tựu ẩn để tạo bất ngờ; điểm tỉ lệ với công sức; mọi thành tựu phải
  lấy được.
- **Steamworks**: thành tựu tích luỹ có thanh tiến độ; Steam cho so sánh thành tựu với bạn bè trên
  một trang hai cột.
- **Deep Rock Galactic** (FPS indie co-op): khoảng 67 thành tựu, khoảng 22 cái ẩn; thành tựu ẩn là
  những khoảnh khắc buồn cười ("Darwin Award") chứ không chỉ là thành tích.
- **Hollow Knight**: 63 thành tựu, cỡ ngang con số 80.
- **Ravenfield (game gốc)**: cờ lê vàng SUPER WRENCH bị giấu khỏi màn chọn trang bị; gõ `ISEEGOLD` ở
  menu thì hiện ra (đọc trong mã gốc đã khôi phục, `WeaponManager.Update`).

## 3. Luật chung

### 3.1 Năm hạng

| Hạng | Nghĩa | Tỉ lệ người có mong muốn | Điểm | Số lượng |
|---|---|---|---|---|
| **Bronze** | Vài giờ chơi; cũng là chỗ của các thành tựu "thảm hoạ" vui | trên 40% | 10 | 13 |
| **Silver** | Chơi đều, có tay nghề | 10–40% | 25 | 16 |
| **Gold** | Giỏi thật hoặc rất bền bỉ | 2–10% | 50 | 21 |
| **Platinum** | Đỉnh của một kỹ năng, một vai trò, hoặc một tình huống hiếm | dưới 2% | 100 | 14 |
| **MYTHIC** | Thứ người chơi kể lại nhiều năm sau | dưới 0,1% | 250 | 16 |

Tổng **80 thành tựu, 6.980 điểm**. Hạng là độ khó theo thiết kế; độ hiếm đo thật (phần trăm người
chơi đã có) hiện riêng.

### 3.2 Platinum và Mythic phải có nghĩa

Mỗi cái là một khoảnh khắc trọn vẹn: một hiệp hoàn hảo, một phát bắn không tưởng, một trận lật kèo,
một mình chống cả đội. Không ghép điều kiện tuỳ tiện, không cái nào chỉ dựa vào may rủi, và cái nào
cũng được tôi kiểm tra là làm được trước khi phát hành (mục 7.2).

### 3.3 Online và practice

- **Online** (65 cái): game server đếm trong hiệp, master server chấm và lưu. Không gian lận được
  từ client.
- **Practice** (15 cái): máy người chơi tự chấm vì không có server nào xem trận offline, rồi claim cho
  tài khoản lần sau đăng nhập.
- **"Hoàn thành / thắng một hiệp"** chỉ tính khi có mặt lúc hiệp kết thúc **và đã chơi ít nhất 5
  phút**. Thành tựu nào cần chơi lâu hơn thì ghi rõ số phút trong mô tả.
- **"Human player"** = người thật, không tính bot. Thành tựu nào không ghi thì bot và người đều tính.
- **"Without dying"** = trong một mạng sống; chết là đếm lại từ đầu.
- **"Shoot down" (bắn rơi) trực thăng** = phá huỷ nó khi có phi công phe địch ngồi trong và nó đang cách
  mặt đất từ **5 m** trở lên (server đo bằng tia chiếu xuống mặt đất). Trực thăng đậu dưới đất hay
  trống người lái thì phá huỷ vẫn tính cho CAN OPENER nhưng không tính là bắn rơi.
- **Accuracy (độ chính xác)** chỉ đếm súng cầm tay: RK-44, S-IND7, S-IND7 [SUP], 76 EAGLE, SL-DEFENDER,
  SIGNAL DMR, RECON LRR. Một lần bóp cò là một phát; phát đó "trúng" nếu gây sát thương cho lính phe
  địch. Súng gắn trên xe, lựu đạn, rocket, tên lửa và cận chiến không tính.
- **"Without turning on night vision"**: ai cũng có phím N để bật night vision, kể cả khi không mang
  kính trong loadout, nên thành tựu tính theo việc **có bật night vision lần nào trong hiệp hay
  không**. Game của người chơi báo cho server mỗi lần bật; bản game cũ không biết báo thì server coi là
  không xác minh được và không trao các thành tựu này.

### 3.4 Mô tả ghi rõ điều kiện và con số

Tên giữ chất, dòng mô tả là chính xác luật code chấm, có con số, đọc là hiểu. Ví dụ WINDREADER:
*"Kill an enemy with a headshot from 500 m or more."*

### 3.5 Thanh tiến độ

- **Thành tựu cày**: thanh tiến độ cá nhân có số, ví dụ `642 / 1,000`. Đánh dấu 📊.
- **Thành tựu nhiều phần** (đủ 3 bản đồ, đủ 12 vũ khí...): thanh tổng và bấm vào thấy từng phần.
- **Thành tựu một khoảnh khắc**: hiện **kỷ lục cá nhân**, ví dụ `Your best: 412 m`, `Your best: 22
  kills`, để người chơi biết mình còn cách bao xa.

### 3.6 Thành tựu ẩn: hiện tên, giấu điều kiện, có câu gợi ý

15 cái ẩn (🔒 trong bảng). Khi chưa mở: **hiện tên thật**, **icon là bóng đen đặc** của chính hình
vẽ, và dòng mô tả là **câu gợi ý riêng** của thành tựu đó (cột "Gợi ý khi chưa mở" ở mục 4.6), không
có thanh tiến độ hay kỷ lục. Khi mở: toast có hiệu ứng "giải mật" (bóng đen được tô màu dần từ dưới
lên), rồi hiện mô tả rõ ràng như mọi thành tựu khác.

---

## 4. Danh sách 80 thành tựu

★ = ý tưởng của anh. Thẻ: **ON** online, **PR** practice, **NM** Night Mode, **🔒** ẩn. Cột **Đo
bằng**: "có sẵn" = server đã đếm stat này; "mới" = phải thêm bộ đếm (mục 7); "client" = game của
người chơi tự đo (practice).

### 4.1 Bronze (13)

| # | Tên | Thẻ | Mô tả trong game | 📊 | Đo bằng |
|---|---|---|---|---|---|
| 1 | ROLL CALL | ON | Finish an online round. You must play at least 5 minutes of it. | | có sẵn |
| 2 | LIGHTS OUT | ON NM | Finish an online round in Night Mode. You must play at least 5 minutes of it. | | có sẵn |
| 3 | BAPTISM OF FIRE | ON | Get 100 kills in online rounds. | 📊 | có sẵn |
| 4 | STEADY HAND | ON | Get 50 headshot kills. | 📊 | có sẵn |
| 5 | FLAG RUNNER | ON | Capture 10 flags: stand in a flag's zone when it turns to your side. | 📊 | có sẵn |
| 6 | TASTE OF VICTORY | ON | Win 10 online rounds. Each must have at least 5 minutes of your play. | 📊 | có sẵn |
| 7 | SPEED BUMP | ON | Run over 10 enemies with a vehicle. | 📊 | có sẵn |
| 8 | BY THE BOOK | PR | Open every page of the How to play guide. | 📊 trang | client |
| 9 | CADET | PR | Finish a practice round on each map: Dustbowl, Island and Forest Lake. Each must have at least 5 minutes of your play. | 📊 0/3 | client |
| 10 | TURNCOAT | PR | Win a practice round on the blue side and another on the red side. Each must have at least 5 minutes of your play. | 📊 0/2 | client |
| 11 | VICTORY LAP | ON 🔒 | Honk the horn within 3 seconds of running an enemy over. | | mới |
| 12 | ★ BULLET SPONGE | ON 🔒 | Die more times than any other human player in an online round with at least 3 human players. You must play at least 5 minutes of it. | | mới |
| 13 | ★ PARTICIPATION TROPHY | ON 🔒 | Finish an online round with the lowest score of everyone in it, bots included. You must play at least 5 minutes of it. | | mới |

### 4.2 Silver (16)

| # | Tên | Thẻ | Mô tả trong game | 📊 | Đo bằng |
|---|---|---|---|---|---|
| 14 | THREE FRONTS | ON | Finish an online round on each map: Dustbowl, Island and Forest Lake. Each must have at least 5 minutes of your play. | 📊 0/3 | có sẵn |
| 15 | UNBROKEN | ON | Get 15 kills without dying. | kỷ lục | có sẵn |
| 16 | PREDATOR | ON | Kill 100 human players. | 📊 | có sẵn |
| 17 | DEAD CENTRE | ON | Get 500 headshot kills. | 📊 | có sẵn |
| 18 | OVERWATCH | ON | Kill an enemy from 300 m or more. | kỷ lục | có sẵn |
| 19 | STEEL RAIN | ON | Get 100 kills from a tank. | 📊 | có sẵn |
| 20 | ROTORHEAD | ON | Get 50 kills from a helicopter. | 📊 | có sẵn |
| 21 | FORWARD SUPPLY | ON | Refill or heal teammates 200 times with your AMMO BAG or MEDIPACK. | 📊 | mới |
| 22 | NIGHT SHIFT | ON NM | Get 250 kills in Night Mode. | 📊 | có sẵn |
| 23 | ★ CANNON FODDER | ON | Die 1,000 times in online rounds. | 📊 | có sẵn |
| 24 | DUST DEVIL | PR | Get 40 kills in one practice round on Dustbowl with at least 50 bots. | kỷ lục | client |
| 25 | FIRST PAST THE POST | PR | Win a practice round that uses the FIRST TO win rule, with at least 30 kills of your own and at least 5 minutes of your play. | | client |
| 26 | BOOTS ONLY | PR | Win a practice round with vehicles turned off and at least 50 bots, with the most kills of anyone in it, after at least 5 minutes of your play. | | client |
| 27 | NINE LIVES | ON 🔒 | Survive a fall with 5 health or less left. | | mới |
| 28 | MAN OVERBOARD | ON 🔒 | Run an enemy over with a boat. | | mới |
| 29 | MUTUAL DESTRUCTION | ON 🔒 | Kill yourself and at least one enemy with the same explosion. | | mới |

### 4.3 Gold (21)

| # | Tên | Thẻ | Mô tả trong game | 📊 | Đo bằng |
|---|---|---|---|---|---|
| 30 | GRIM ARITHMETIC | ON | Get 10,000 kills in online rounds. | 📊 | có sẵn |
| 31 | JUGGERNAUT | ON | Get 30 kills without dying. | kỷ lục | có sẵn |
| 32 | CROWD CONTROL | ON | Kill 3 enemies with a single grenade. | | mới |
| 33 | COLD STEEL | ON | Get 25 melee kills. | 📊 | có sẵn |
| 34 | ARMOURER | ON | Get a kill with each of the 12 weapons: RK-44, S-IND7, S-IND7 [SUP], 76 EAGLE, SL-DEFENDER, SIGNAL DMR, RECON LRR, FRAG, SPEARHEAD, BEU AW1, BIL SCALPEL and WRENCH. | 📊 0/12 | mới |
| 35 | CAN OPENER | ON | Destroy 50 enemy vehicles. | 📊 | mới |
| 36 | LONG CAMPAIGN | ON | Win 100 online rounds. Each must have at least 5 minutes of your play. | 📊 | có sẵn |
| 37 | TOP BRASS | ON | Be the MVP of 10 rounds: win with the most points of anyone in the round, bots included, after at least 5 minutes of your play. | 📊 0/10 | có sẵn |
| 38 | ★ CLEAN SHEET | ON | Win an online round without dying once. You must play at least 15 minutes and get at least 10 kills. | | mới |
| 39 | NAKED EYE | ON NM | Win a Night Mode round without turning on night vision. You must play at least 15 minutes and get at least 10 kills. | | mới |
| 40 | NIGHT TERROR | ON NM | Get 10 melee kills in one Night Mode round. | kỷ lục | mới |
| 41 | HELL WEEK | PR | Get 75 kills in one practice round with 100 bots. | kỷ lục | client |
| 42 | ISLAND HOPPER | PR | In one practice round on Island, help capture every flag on the map: be in its zone when it turns to your side. | | client |
| 43 | LAKE MONSTER | PR | Get 10 kills from a boat in one practice round on Forest Lake. | kỷ lục | client |
| 44 | GRAVEYARD SHIFT | PR NM | Win a Night Mode practice round with at least 50 bots without turning on night vision, after at least 5 minutes of your play. | | client |
| 45 | MOTOR POOL | PR | In one practice round, get a kill from a jeep or quad bike, a tank, a helicopter and a boat. | 📊 0/4 | client |
| 46 | ★ JACK OF ALL TRADES | ON 🔒 | In one life, get a kill with a primary weapon, a kill with your pistol, a grenade kill and a kill with the BEU AW1 or BIL SCALPEL. | | mới |
| 47 | TOUCHDOWN | ON 🔒 | Kill an enemy by landing or crashing a helicopter on them. | | mới |
| 48 | BUCKSHOT SNIPER | ON 🔒 | Kill an enemy with the 76 EAGLE shotgun from 60 m or more. | | mới |
| 49 | DOGFIGHT | ON 🔒 | While you pilot a helicopter 5 m or more above the ground, shoot down an enemy helicopter that is also 5 m or more up, with its pilot aboard. | | mới |
| 50 | GOLD STANDARD | PR 🔒 | Get a kill with the golden wrench in practice. | | client |

### 4.4 Platinum (14)

| # | Tên | Thẻ | Mô tả trong game | 📊 | Đo bằng |
|---|---|---|---|---|---|
| 51 | WINDREADER | ON | Kill an enemy with a headshot from 500 m or more. | kỷ lục | có sẵn |
| 52 | ALL FRONTS MASTERED | ON | Win 50 online rounds on each of the 3 maps. Each must have at least 5 minutes of your play. | 📊 0/150 | mới |
| 53 | AIR DEFENSE | ON | On foot, shoot down 25 enemy helicopters: destroy them with their pilot aboard while they are 5 m or more above the ground. | 📊 | mới |
| 54 | UNDEFEATED | ON | Win 10 online rounds in a row. Any round you play for 5 minutes or more and do not win resets the count. | chuỗi hiện tại | mới (master) |
| 55 | MOONLIGHT MARKSMAN | ON NM | Kill an enemy with a headshot from 300 m or more in Night Mode. | kỷ lục | mới |
| 56 | ★ OUTNUMBERED | ON | Win an online round as the only human player on your side against at least 3 human players. You must play at least 10 minutes. | | mới |
| 57 | ★ ON BORROWED TIME | ON | After an enemy brings you down to 5 health or less, get 10 more kills without healing and without dying. | kỷ lục | mới |
| 58 | ★ PACIFIST | ON | Win an online round with 0 kills and 0 deaths while capturing more flags than anyone else in it, bots included. You must play at least 15 minutes. | | mới |
| 59 | ★ NEMESIS | ON | Kill the same human player 7 times in one round without them killing you once. | kỷ lục | mới |
| 60 | ★ ABSOLUTE DOMINANCE | ON | Finish an online round first of everyone in it, bots included, in kills, in flag captures and in accuracy (at least 50 shots), with nobody dying fewer times than you. You must play at least 5 minutes. | | mới |
| 61 | DRILL SERGEANT | PR | Win a practice round with no allied bots against 20 or more enemy bots, under LEAD BY 200 or more or FIRST TO 500 or more, after at least 5 minutes of your play. | | client |
| 62 | GRAND TOUR | PR | Win practice rounds on all 3 maps under both win rules (LEAD BY 200 or more, FIRST TO 500 or more), and win a Night Mode practice round. | 📊 0/7 | client |
| 63 | IMPOSSIBLE ANGLE | ON 🔒 | Shoot down an enemy helicopter that is 10 m or more above the ground with a tank's main gun. | | mới |
| 64 | FROM THE GRAVE | ON 🔒 | After you die, kill 3 enemies with a grenade you threw before dying. | | mới |

Ghi chú:
- **ON BORROWED TIME** chỉ tính khi máu tụt do **địch gây ra** (đạn, nổ, bị cán), không tính tự ngã từ
  trên cao hay tự nổ. Hồi máu bằng bất cứ cách nào (medipack, tiếp tế) là đếm lại từ đầu.
- **ABSOLUTE DOMINANCE** và **PARTICIPATION TROPHY**: so với **mọi actor** trong hiệp, kể cả bot (trận
  100 bot và 4 người thì so cả 104). Accuracy chỉ so giữa những actor đã bắn ít nhất 50 viên. "Nobody
  dying fewer times" và "lowest score" đều tính cả trường hợp hoà. Vì so cả bot nên trận nào cũng có
  người nhận PARTICIPATION TROPHY.
- **OUTNUMBERED** tính số người thật ở mỗi phe lúc hiệp kết thúc. Chọn đội giờ tự do hoàn toàn (mục
  7.1), nên 1 người đấu 3 hay 1 đấu 10 đều chơi được.
- **DRILL SERGEANT** và **GRAND TOUR** chỉ tính luật thắng mặc định hoặc khó hơn (lead by từ 200, first
  to từ 500).

### 4.5 Mythic (16)

| # | Tên | Thẻ | Mô tả trong game | 📊 | Đo bằng |
|---|---|---|---|---|---|
| 65 | CENTURION | ON | Get 100 kills without dying. | kỷ lục | có sẵn |
| 66 | RAMPAGE | ON | Get 10 kills in a row, each one within 3 seconds of the last. | kỷ lục | có sẵn |
| 67 | CURVATURE | ON | Kill an enemy with a headshot from 900 m or more. | kỷ lục | có sẵn |
| 68 | PERFECT TEN | ON | Get 10 headshot kills in a row without dying. A kill that is not a headshot resets the count. | kỷ lục | có sẵn |
| 69 | ★ DEAD EYE | ON | Win an online round with at least 15 kills and 100% accuracy: fire at least 15 bullets, and every one hits an enemy. | | mới |
| 70 | ★ UNTOUCHABLE | ON | Win an online round without taking any damage. You must play at least 15 minutes and get at least 15 kills. | | mới |
| 71 | BLADE ONLY | ON | Finish an online round with at least 25 kills, every one of them a melee kill. You must play at least 5 minutes. | kỷ lục | mới |
| 72 | TANK ACE | ON | Get 50 kills in one tank without leaving it or it being destroyed. | kỷ lục | mới |
| 73 | SKY KING | ON | Get 30 kills in one helicopter flight without leaving your seat or the helicopter being destroyed. | kỷ lục | mới |
| 74 | MAP PAINTER | ON | In one round, help capture every flag on the map, never die, and win. You must play at least 5 minutes. | | mới |
| 75 | HAIL MARY | ON | Win a round after the enemy came within 10 points of winning it. You must play at least 5 minutes. | | mới |
| 76 | CREATURE OF THE NIGHT | ON NM | Win a Night Mode round with at least 30 kills and 0 deaths, without turning on night vision. You must play at least 5 minutes. | | mới |
| 77 | IMMACULATE | PR | Get 100 kills without dying in a practice round with 100 bots. | kỷ lục | client |
| 78 | IRONCLAD | ON | Unlock all 79 other achievements. | 📊 0/79 | mới (master) |
| 79 | MID-AIR | ON 🔒 | Kill an enemy helicopter pilot with a headshot from 300 m or more while the helicopter is 5 m or more above the ground. | | mới |
| 80 | ★ COUNTER-SNIPER | ON 🔒 | With your pistol, kill an enemy holding a sniper or marksman rifle (SL-DEFENDER, SIGNAL DMR or RECON LRR) from more than 150 m. | | mới |

Ghi chú:
- **DEAD EYE**: accuracy đếm theo **phát bắn** của súng (shotgun một phát là một lần bắn, trúng nếu có
  ít nhất một viên ghém trúng); một phát chỉ tính "trúng" khi gây sát thương cho **lính địch**. Lựu
  đạn, rocket, tên lửa và cận chiến không tính vào accuracy.
- **UNTOUCHABLE**: mọi nguồn sát thương đều tính (đạn, nổ, ngã, bị cán, chết đuối).
- **HAIL MARY** tính theo luật của phòng: với "lead by 200" là lúc địch dẫn từ 190 điểm; với "first
  to 500" là lúc địch đạt từ 490 điểm.
- **MAP PAINTER**, **ISLAND HOPPER**: "help capture" = có mặt trong vùng cờ lúc cờ đổi sang phe mình.

### 4.6 Thành tựu ẩn: tên, câu gợi ý và bóng đen

Trước khi mở, người chơi thấy **tên thật**, **bóng đen** và **câu gợi ý** dưới đây (không phải mô tả
thật). Sau khi mở thì thấy mô tả thật ở bảng trên.

| # | Tên | Gợi ý khi chưa mở | Bóng đen |
|---|---|---|---|
| 11 | VICTORY LAP | Some victories deserve a little noise. | cái còi xe |
| 12 | BULLET SPONGE | Somebody has to soak up the bullets. | tấm bia tập bắn thủng lỗ chỗ |
| 13 | PARTICIPATION TROPHY | Everyone gets a medal. Yes, even you. | cúp nhựa bị mẻ |
| 27 | NINE LIVES | Gravity tried. Gravity failed. | con mèo |
| 28 | MAN OVERBOARD | Not every road is made of dirt. | mũi thuyền rẽ sóng |
| 29 | MUTUAL DESTRUCTION | If you are going down, take company. | hai đầu lâu chạm nhau |
| 46 | JACK OF ALL TRADES | Why pick one tool when you carry four? | bốn vũ khí xếp chéo |
| 47 | TOUCHDOWN | Not every landing happens on a pad. | trực thăng chúc đầu |
| 48 | BUCKSHOT SNIPER | Nobody told the pellets about range. | vỏ đạn ghém trong ống ngắm |
| 49 | DOGFIGHT | The sky is not big enough for two. | hai trực thăng bắn nhau |
| 50 | GOLD STANDARD | Old soldiers still whisper about a wrench made of gold. | cờ lê vàng |
| 63 | IMPOSSIBLE ANGLE | Tanks were never meant to look up. | nòng tăng chĩa lên trực thăng |
| 64 | FROM THE GRAVE | Dying is only half of the plan. | bia mộ có chốt lựu đạn |
| 79 | MID-AIR | Pilots think they are safe up there. | tâm ngắm trên buồng lái trực thăng |
| 80 | COUNTER-SNIPER | Bring a pistol to a sniper fight. | khẩu súng ngắn đối diện ống ngắm |

### 4.7 Practice rải đều bản đồ và chế độ

| Bản đồ / chế độ | Thành tựu practice |
|---|---|
| Mọi bản đồ | CADET (đủ 3 bản đồ), GRAND TOUR (thắng đủ 3 bản đồ x 2 luật + Night Mode) |
| Dustbowl | DUST DEVIL |
| Island | ISLAND HOPPER |
| Forest Lake | LAKE MONSTER |
| Night Mode | GRAVEYARD SHIFT |
| Luật FIRST TO | FIRST PAST THE POST |
| Tắt xe | BOOTS ONLY |
| Bật xe | MOTOR POOL |
| Chọn phe | TURNCOAT |
| Số bot / một mình | HELL WEEK, DRILL SERGEANT, IMMACULATE |
| Hướng dẫn, bí mật | BY THE BOOK, GOLD STANDARD |

### 4.8 Night Mode trải khắp các hạng

| Hạng | Thành tựu Night Mode |
|---|---|
| Bronze | LIGHTS OUT |
| Silver | NIGHT SHIFT |
| Gold | NAKED EYE, NIGHT TERROR, GRAVEYARD SHIFT (practice) |
| Platinum | MOONLIGHT MARKSMAN |
| Mythic | CREATURE OF THE NIGHT |

### 4.9 Kiểm tra lại: một trận 100 bot lấy được bao nhiêu?

Một người chơi giỏi, một trận Forest Lake 100 bot dài: ROLL CALL, STEADY HAND, FLAG RUNNER, có thể
BAPTISM OF FIRE, UNBROKEN, OVERWATCH, và PARTICIPATION TROPHY cho người đứng bét. Khoảng **4 đến 7 trên
80**, đa số Bronze. Trong 5 phút đầu: **không có cái nào**.

---

## 5. Thông báo (toast) khi mở khoá

### 5.1 Đã kiểm tra lỗi "không thấy thông báo" hôm nay

- Log của anh ghi đúng `[achievements] earned in practice: basic_training` và `one_man_army`: game
  đã ghi nhận và đã xếp hai banner vào hàng đợi.
- Tôi chạy chính bản v4.6.0 của anh (một bản thứ hai, log riêng) và mở lại một thành tựu thật:
  **banner có hiện** (STUDENT OF WAR, trên giữa màn hình). Trong Editor, banner hiện cả lúc hết trận
  practice, trên màn chọn loadout và trên trang How to play.
- Tôi **chưa tái hiện được** đúng trường hợp hết trận practice trên bản release. Bản mới làm lại hàng
  đợi (5.2) và thêm bảng tổng kết cuối hiệp, nên dù banner có bị lỡ, người chơi vẫn thấy những gì
  vừa mở.

### 5.2 Thiết kế mới

1. **Một hàng đợi duy nhất, không mất cái nào.** Mỗi lần mở khoá đúng một toast. Nhiều cái cùng lúc
   thì xếp hàng: cái trước trượt lên biến mất, 0,3 giây sau cái sau trượt xuống. Góc toast có bộ đếm
   `1/3`, `2/3`...
2. **Hàng đợi sống qua chuyển cảnh và cả khi thoát game.** Tắt game giữa chừng thì lần mở game sau
   hiện tiếp.
3. **Practice: hiện ngay lúc đạt điều kiện.**
4. **Online: hiện vài giây sau khi đạt.** Game server gửi tiến độ hiệp lên master mỗi 15 giây và lúc
   hết hiệp; master chấm và đẩy xuống client.
5. **Chỉ người mở khoá thấy thông báo.** Không thông báo cho cả server.
6. **Tổng kết cuối hiệp**: lúc hiệp kết thúc hiện thẻ `ROUND ACHIEVEMENTS` ở giữa phía dưới màn hình
   trong 20 giây, không cần giữ Tab: các thành tựu vừa mở, các mốc 50% / 90%, và tối đa 3 tiến độ đã
   tăng gần đích nhất (ví dụ `+42 · GRIM ARITHMETIC 642 / 10,000`). Mở bảng Tab thì thẻ tạm mờ đi để
   không che bảng. (Đổi so với bản duyệt, xem mục 9.)
7. **Giao diện toast** (trên giữa màn hình):
   - Khung và ánh sáng theo màu hạng; icon bật lên kiểu "pop", một vệt sáng quét ngang.
   - Dòng nhỏ: `ACHIEVEMENT UNLOCKED` hoặc `CLASSIFIED ACHIEVEMENT DECLASSIFIED` (ẩn); bên phải hạng
     và **+điểm**. Dòng lớn: tên; dòng dưới: mô tả thật.
   - **Âm thanh riêng từng hạng**: Bronze một tiếng chuông, Silver hai nốt, Gold ba nốt kèn đồng,
     Platinum fanfare ngắn, Mythic trống trầm và hợp xướng ngắn. Thành tựu "thảm hoạ" (BULLET SPONGE,
     PARTICIPATION TROPHY, CANNON FODDER) có tiếng kèn trôm-pét buồn cười thay cho chuông.
   - **Mythic**: banner rộng gấp đôi, khung đen obsidian vân đỏ, hạt lửa bay lên, giữ 8 giây.
   - Đang chiến đấu thì toast nhỏ hơn một chút để không che tầm nhìn (Mythic vẫn đầy đủ).
8. **Báo tiến độ ở mốc lớn**: thành tựu cày qua 50% và 90% thì hiện một dòng nhỏ lúc hết hiệp.

## 6. Giao diện

### 6.1 Trang thành tựu của mình

- **Đầu trang**: tên, vòng tròn hoàn thành `27 / 80`, tổng điểm `1,230 / 6,980`, số huy chương mỗi
  hạng (5 icon kim loại kèm số), dòng luật "rounds count when you play at least 5 minutes".
- **Không chia tab theo nhóm.** Một lưới thẻ duy nhất với:
  - **Lọc theo hạng**: All / Bronze / Silver / Gold / Platinum / Mythic.
  - **Lọc theo trạng thái**: All / Unlocked / Locked / In progress.
  - **Lọc theo thẻ**: Online / Practice / Night Mode / Hidden.
  - **Sắp xếp**: độ khó, độ hiếm, mới mở gần đây, **gần mở nhất**.
- **Thẻ thành tựu**: icon 96 px; tên; mô tả (hoặc câu gợi ý nếu là thành tựu ẩn chưa mở); chip hạng;
  độ hiếm thật (`3.2% of players`, nhãn COMMON / UNCOMMON / RARE / EPIC / LEGENDARY); ngày mở; thanh
  tiến độ hoặc kỷ lục cá nhân.
  - Chưa mở: icon xám kèm ổ khoá. Ẩn chưa mở: tên thật, bóng đen, câu gợi ý.
  - Mythic: viền đỏ obsidian có hạt lửa chuyển động nhẹ, kể cả khi chưa mở.
- **Bấm vào thẻ**: bảng chi tiết với icon lớn, mô tả, độ hiếm, ngày mở, tiến độ từng phần. Mythic có
  dòng "First unlocked by `<tên>` on `<ngày>`" (chỉ hiện ở đây; với thành tựu ẩn chỉ hiện nếu chính
  người xem đã mở).

Nhãn độ hiếm: COMMON từ 50%, UNCOMMON 20–50%, RARE 5–20%, EPIC 1–5%, LEGENDARY dưới 1%.

### 6.2 GLOBAL RANKING: xem và so sánh thành tựu với bất kỳ ai

Steam chỉ cho so với bạn bè; ở đây so được với **mọi người trong bảng xếp hạng**.

- **Bảng xếp hạng** thêm cột `ACHIEVEMENTS` (số đã mở và điểm) và cột ★ (số Mythic). Mỗi dòng có nút
  **COMPARE**; bấm vào tên mở **thẻ người chơi** (chỉ số chính, tóm tắt thành tựu, 3 thành tựu hiếm
  nhất mà người xem được phép thấy).
- **Màn so sánh side-by-side** (cùng bộ lọc và sắp xếp như 6.1):

```
 YOU  ·  NGHAIZ                                VS                    PLAYER  ·  CLAUDETEST1
 ███████████████░░░░░  38 / 80 · 2,310 pts                    29 / 80 · 1,540 pts  ██████████░░░░░░░░░░
 Bronze 12  Silver 11  Gold 9  Platinum 3  Mythic 3          Bronze 12  Silver 10  Gold 5  Platinum 2  Mythic 0

 ✔ 09 Oct 2026      [badge] BAPTISM OF FIRE · Get 100 kills in online rounds.       ✔ 02 Oct 2026
   7,412/10,000 ███████▒░  [badge] GRIM ARITHMETIC · Get 10,000 kills ...  ░▒███ 2,190/10,000
   best 64          [badge] CENTURION · Get 100 kills without dying.               best 41
 ✔ 09 Oct 2026      [badge] NINE LIVES · Survive a fall with 5 health or less.     ✔ 30 Sep 2026   (cả hai đã có)
 ✔ 07 Oct 2026      [badge] VICTORY LAP · Honk the horn within 3 seconds ...       chưa có         (chỉ mình có)
 chưa có            [bóng đen] TOUCHDOWN · Not every landing happens on a pad.    CLASSIFIED      (mình chưa có)
```

  - Cột giữa là thành tựu; trái là mình, phải là họ; thanh tiến độ hai bên mọc đối xứng ra từ giữa.
    Dòng chỉ một người có được tô nhẹ màu của người đó.
  - **Lọc thêm**: Both have / Only me / Only them / Neither.
- **Luật thành tựu ẩn (master kiểm soát, client không tự lọc):**
  - **Cả hai đã mở** → hiện đầy đủ.
  - **Chỉ mình đã mở** → mình thấy đầy đủ, cột của họ hiện "chưa có".
  - **Mình chưa mở** (dù họ có hay không) → dòng hiện tên, bóng đen, câu gợi ý; **cột của họ chỉ hiện
    CLASSIFIED, không cho biết họ đã có hay chưa**. Đầu trang chỉ ghi tổng số ẩn mỗi người đã có.
  - Master **không bao giờ gửi** ngày mở hay tiến độ của một thành tựu ẩn mà người xem chưa có.

### 6.3 Icon

- **80 icon riêng**, vẽ bằng code như bộ hiện tại (`tools/ui/badges.py` + `make_icons.py`).
- Mỗi icon = **khung huy hiệu theo hạng** (đồng / bạc / vàng / bạch kim có cánh và sao / Mythic
  obsidian có vết nứt phát sáng đỏ và vương miện gai) + **hình vẽ giữa** riêng cho từng thành tựu.
  Thành tựu Night Mode có thêm vầng trăng nhỏ trên khung; thành tựu practice có thêm biểu tượng bia
  tập bắn nhỏ.
- Mỗi icon xuất 3 trạng thái: màu (đã mở), xám có khoá (chưa mở), **bóng đen** (ẩn chưa mở).
- Màu hạng: Bronze `#CD7F32`, Silver `#C9D1DB`, Gold `#F2C14E`, Platinum `#8FE9F5`, Mythic
  `#FF3D3D` trên nền `#14080A`.

### 6.4 Cờ lê vàng (GOLD STANDARD)

- Gõ `ISEEGOLD` ở menu chính (không cần ô nhập, như game gốc): một tiếng chuông vàng vang lên và dòng
  `THE GOLDEN WRENCH IS YOURS` hiện thoáng trên màn hình.
- Từ đó **SUPER WRENCH** hiện trong danh sách gear của màn chọn loadout **chỉ trong practice**. Game
  nhớ việc đã mở khoá cho những lần chơi sau.
- Online không có cờ lê vàng: màn chọn loadout của trận online không hiện nó và server không nhận nó.
- Đánh trúng vật thể thì vật đó hoá vàng và bắn ra hạt tiền, như game gốc; đánh trúng lính thì gây sát
  thương cận chiến của cờ lê vàng.

---

## 7. Phần kỹ thuật

### 7.1 Phạm vi

- **Chọn đội tự do**: master bỏ giới hạn "một phe tối đa nửa số ghế", chỉ giữ tổng số ghế của phòng;
  game server khi một phe hết thân xác trống thì chuyển một thân xác trống của phe kia sang (thân xác
  người chơi được tạo sẵn theo phe, và số actor tối đa 128 không đủ để tạo gấp đôi). Phòng 1 xanh đấu
  10 đỏ bắt đầu và chơi bình thường.
- **Một danh mục chung** (`AchievementCatalog.cs`) cho master và client, viết lại 80 mục; mỗi mục có
  hạng, thẻ, điểm, kiểu tiến độ (thanh / nhiều phần / kỷ lục), ẩn hay không, câu gợi ý, bóng đen.
- **Bộ đếm mới trên game server** (`MatchCareerTally` và nơi xử lý phát bắn), đều đo được từ dữ liệu
  server đang có:
  - **Phát bắn và phát trúng của mọi actor, kể cả bot** (DEAD EYE, ABSOLUTE DOMINANCE).
  - **Sát thương nhận vào và nguồn gây ra**, mỗi lần hồi máu (UNTOUCHABLE, ON BORROWED TIME).
  - **Ai là người thật, ở phe nào**, lúc hiệp kết thúc; điểm của mọi actor (OUTNUMBERED, BULLET SPONGE,
    PARTICIPATION TROPHY, NEMESIS, ABSOLUTE DOMINANCE).
  - **Vũ khí nạn nhân đang cầm lúc chết** (COUNTER-SNIPER); **việc bật night vision**, do game của người
    chơi báo lên (NAKED EYE, CREATURE OF THE NIGHT).
  - Mạng theo vũ khí và mặt nạ bit vũ khí đã dùng (ARMOURER, JACK OF ALL TRADES, BLADE ONLY).
  - Lựu đạn 3 mạng một quả; lựu đạn giết người sau khi người ném đã chết; chết chung một vụ nổ.
  - Mạng trong một lần ngồi xe tăng / một chuyến bay; xe địch bị phá huỷ (`Vehicle` đã nhớ ai phá huỷ
    nó qua `_lastDamagedBy`); trực thăng bị bắn rơi từ dưới đất, từ trực thăng khác, hoặc bằng pháo
    tăng, kèm độ cao.
  - Cán chết bằng trực thăng / thuyền; bấm còi sau khi cán; headshot phi công đang bay.
  - Cờ mỗi người góp công chiếm trong hiệp; điểm cách chiến thắng của phe địch (HAIL MARY).
  - Tiếp đạn / hồi máu cho đồng đội: `Medipack` là một `Projectile` nên biết người ném.
  - Ngã còn từ 5 máu; bị cùng một người giết nhiều lần.
- **Practice** (game của người chơi tự chấm): bản đồ, luật thắng, Night Mode, bật/tắt xe, số bot hai phe,
  phe của người chơi, mạng theo phương tiện, cờ đã góp công chiếm, việc bật night vision, mạng bằng cờ lê
  vàng.
- **Master**: chấm theo career; thêm chuỗi thắng, số thắng theo từng bản đồ, số thành tựu đã mở, luật số
  phút, báo cáo tiến độ giữa hiệp, nhớ người đầu tiên mở mỗi Mythic, và **yêu cầu mới "xem / so sánh
  người chơi"** có lọc thành tựu ẩn theo mục 6.2.
- **Xoá sạch thành tựu cũ** trong database master và thành tựu practice cũ trên máy; **career giữ
  nguyên**, nên ai đã có số liệu sẽ mở ngay các thành tựu cày đã đủ ở hiệp đầu tiên sau cập nhật.
- **Tương thích**: tin nhắn mới đều là bổ sung; bản v4.6.0 đã tải vẫn vào được server, chỉ không thấy
  thành tựu mới cho tới khi tải bản v4.6.0 thay thế.

### 7.2 Kiểm tra khả thi trước khi phát hành

| Thành tựu | Kiểm tra | Nếu không làm được |
|---|---|---|
| CURVATURE | Có tầm nhìn thật trên Forest Lake dài hơn 900 m; súng ngắm còn giết được bằng headshot ở 900 m | Hạ về mức xa nhất còn giết được, ghi số vào đây |
| COUNTER-SNIPER | Súng ngắn còn gây sát thương ở 150 m (tầm 200 m) | Hạ khoảng cách, ghi số |
| MID-AIR | Phi công trực thăng có trúng đạn được không | Đổi thành headshot người ngồi súng trên trực thăng |
| IMPOSSIBLE ANGLE | Nòng pháo tăng ngóc đủ cao để trúng trực thăng cách đất 10 m | Hạ độ cao xuống mức trúng được |
| OUTNUMBERED | Phòng 1 người đấu 3 người bắt đầu và cả 4 vào được | Bắt buộc phải được sau khi sửa chọn đội |
| DRILL SERGEANT | Tự chơi thử một mình trước 20 bot | Hạ số bot, ghi lại |
| GOLD STANDARD | Gõ ISEEGOLD ở menu mở được cờ lê, mang vào practice và giết được lính | Bắt buộc phải được |
| DEAD EYE, ABSOLUTE DOMINANCE | Server đếm đủ phát bắn của mọi súng, kể cả bot | Bắt buộc phải đủ trước khi phát hành |
| UNTOUCHABLE | Server đếm đủ mọi nguồn sát thương | Bắt buộc phải đủ trước khi phát hành |

### 7.3 Test

Test cho từng bộ đếm mới (mỗi cái có ca "vừa đủ" và "thiếu một"); danh mục đủ 80 mục, 15 ẩn, 15
practice, phân bổ hạng 13/16/21/14/16; hàng đợi toast không bỏ sót khi 5 cái mở cùng lúc; master không
gửi thành tựu ẩn người xem chưa có trong màn so sánh; phòng 1 đấu nhiều người bắt đầu được.

## 8. Mặc định đang áp dụng

1. **Career cũ được giữ** (người có sẵn số liệu mở ngay các thành tựu cày đã đủ).
2. **Các con số** như trong bảng.
3. **Toast giữa trận** cho online (sau vài giây), ngay lập tức cho practice.
4. **Điểm thành tựu** 10/25/50/100/250 và cột điểm trên bảng xếp hạng.

## 9. Tiến độ làm

| Bước | Trạng thái |
|---|---|
| Thiết kế (tài liệu này) | Đã duyệt 2026-10-09 |
| Chọn đội tự do | Xong, PR #588 (master bỏ giới hạn mỗi phe; game server chuyển thân xác trống sang phe thiếu) |
| Cờ lê vàng + ISEEGOLD | Xong, PR #589 (gõ ở menu, chỉ practice, game nhớ, có thông báo) |
| Danh mục 80 + bộ đếm server + master | Xong phần lõi, PR #590: danh mục 80, game server đếm mọi số liệu trong mục 7.1 cho mọi actor kể cả bot, master chấm và lưu, báo tiến độ giữa hiệp mỗi 15 giây, báo hiệp của người rời trận, xoá thành tựu cũ và giữ career, game báo việc bật night vision. Practice: 15 thành tựu chấm ngay trong game. Test: dotnet 8 bộ xanh, EditMode 632/632 |
| Toast, trang thành tựu, tổng kết cuối hiệp, tuỳ chọn số bot practice | Xong (PR "achievements v2 UI"). Toast: một hàng đợi lưu qua chuyển cảnh và cả khi thoát game, bộ đếm `2 / 3`, màu và khung theo hạng, Mythic rộng gấp đôi nền obsidian có hạt lửa giữ 8 giây, thành tựu ẩn "giải mật" màu dâng từ dưới lên, nhỏ lại khi đang bắn. Trang: lọc theo hạng / trạng thái / loại, sắp xếp độ khó / độ hiếm / mới mở / gần mở nhất, độ hiếm thật và nhãn COMMON…LEGENDARY, thanh tiến độ hoặc kỷ lục, bảng chi tiết có từng phần và người đầu tiên mở Mythic. Practice có thêm "BLUE / RED, ALONE VS ALL BOTS". **Khác bản duyệt**: tổng kết cuối hiệp là thẻ riêng ở dưới màn hình (không nằm trong bảng Tab) để người chơi thấy ngay mà không phải giữ Tab, giống màn tổng kết sau trận của các game lớn; thẻ tự mờ khi mở bảng Tab. Âm thanh từng hạng và icon đến ở bước "Icon và âm thanh"; tới lúc đó toast dùng tiếng chuông cũ và ô vuông thay icon. Test: dotnet 8 bộ xanh |
| So sánh trong GLOBAL RANKING | Xong (PR "ranking compare"). Bảng xếp hạng thêm cột ACHIEVEMENTS (số đã mở · điểm) và MYTHIC; bấm tên mở thẻ người chơi (chỉ số chính, huy chương theo hạng, số ẩn, 3 thành tựu hiếm nhất mình được thấy); COMPARE (trên mỗi dòng và trên thẻ) mở màn so sánh hai bên: mình trái, họ phải, thanh tiến độ mọc từ giữa, lọc theo hạng / loại / BOTH HAVE / ONLY ME / ONLY THEM / NEITHER, sắp xếp như trang thành tựu. Master áp luật ẩn (yêu cầu mới `PLAYER_PROFILE` 0x0046/0x0047, giao thức 14.0.5): thành tựu ẩn mình chưa có không bao giờ được gửi, cả ngày mở lẫn số liệu career chỉ phục vụ nó; cột của họ ghi CLASSIFIED |
| Icon và âm thanh | Xong (PR "achievement badges"). 80 huy hiệu vẽ bằng code (`tools/ui/badges.py`, `make_icons.py --badges`): Bronze tròn, Silver lục giác 1 sao, Gold khiên 2 sao, Platinum hình sao 3 sao, Mythic obsidian có vương miện gai và vết nứt đỏ; thành tựu Night Mode có vầng trăng nhỏ, practice có bia tập bắn nhỏ trên khung. 15 thành tựu ẩn có thêm bóng đen `<id>_shadow` (hình khung đen, hình vẽ giữa mờ). Hạng, ẩn, night, practice đọc thẳng từ danh mục nên không lệch được; 50 huy hiệu cũ đã xoá. Âm thanh (`make_achievement_sound.py`): Bronze một tiếng chuông, Silver hai nốt, Gold ba nốt kèn đồng, Platinum fanfare ngắn, Mythic ba tiếng trống trầm rồi hợp xướng, thảm hoạ là kèn trôm-pét buồn "wah wah wah waaah"; tiếng chuông cũ giữ làm dự phòng. Test kiểm tra đủ huy hiệu, đủ bóng đen, không thừa file cũ, đủ âm thanh |
| Deploy master + game server, cập nhật main, thay 3 bản v4.6.0 | Chưa |
