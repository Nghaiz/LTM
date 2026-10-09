IRONFRONT: REBORN  (LTM10)  -  Linux
Bản build {{VERSION}} ({{COMMIT}})

==============================================================
TIẾNG VIỆT
==============================================================

CÀI ĐẶT
  1. Giải nén file zip ra một thư mục MỚI, ví dụ ~/Games:
       unzip IronfrontReborn-{{VERSION}}-linux-x64.zip -d ~/Games
     Đừng giải nén đè lên bản cũ: file cũ còn sót lại sẽ làm game lỗi.
  2. Mở game:
       ~/Games/IronfrontReborn-{{VERSION}}/Ironfront.x86_64
     Hoặc bấm đúp vào Ironfront.x86_64 trong trình quản lý file.
     Nếu báo "Permission denied" (một số trình giải nén bỏ quyền chạy),
     chạy lệnh này một lần rồi mở lại:
       chmod +x ~/Games/IronfrontReborn-{{VERSION}}/Ironfront.x86_64

VÀO TRẬN
  1. Ở màn hình đầu, bấm MULTIPLAYER.
  2. Lần đầu chơi: bấm "Create an account" để tạo tên đăng nhập và mật khẩu.
     Tài khoản lưu trên server nên máy nào cũng dùng được, kể cả máy Windows và Mac.
     Những lần sau: nhập tên, mật khẩu rồi bấm LOG IN. Tích "Remember me"
     thì 30 ngày tới máy này tự đăng nhập.
  3. Đăng nhập xong là thấy ngay danh sách phòng. Bấm JOIN để vào một phòng,
     QUICK MATCH để vào phòng đông nhất còn chỗ, hoặc CREATE ROOM để tạo
     phòng mới (DUSTBOWL, ISLAND hoặc FOREST LAKE).
  4. Trong phòng, bấm READY UP. Trận tự bắt đầu khi có ít nhất 2 người
     READY trong cùng một phòng. Một mình thì phòng sẽ đứng chờ.
  5. Chọn điểm xuất phát trên bản đồ nhỏ rồi bấm DEPLOY.

Chưa biết chơi? Bấm HOW TO PLAY ở màn hình đầu, hoặc phím H ở các màn hình
khác. Đổi phím trong SETTINGS. Bảng xếp hạng (GLOBAL RANKING) và thành tích
(ACHIEVEMENTS) có ở màn hình đầu và trong menu Esc khi đang chơi.

Không cần cấu hình gì thêm. Địa chỉ server đã có sẵn trong game.

YÊU CẦU
  - Linux 64-bit (x86_64), ví dụ Ubuntu 22.04 trở lên, có giao diện đồ họa.
  - Driver card đồ họa hỗ trợ Vulkan hoặc OpenGL 4.5.
  - Có Internet, và mạng cho phép gửi UDP ra ngoài qua cổng 27015 đến 27017.
    Một số mạng công ty hoặc trường học chặn UDP. Khi đó bạn vẫn đăng nhập
    được nhưng không vào được trận.

GẶP LỖI
  Gửi file log này cho người phát hành game, kèm số bản build ở đầu file:
    ~/.config/unity3d/LTM10/IronfrontReborn/Player.log

==============================================================
ENGLISH
==============================================================

INSTALL
  1. Unzip into a NEW folder, for example:
       unzip IronfrontReborn-{{VERSION}}-linux-x64.zip -d ~/Games
     Do not unzip over an older build.
  2. Run ~/Games/IronfrontReborn-{{VERSION}}/Ironfront.x86_64, or double-click
     it in your file manager. "Permission denied" means your unzip tool
     dropped the executable bit; run this once:
       chmod +x ~/Games/IronfrontReborn-{{VERSION}}/Ironfront.x86_64

PLAY
  1. Main menu: MULTIPLAYER.
  2. First time: "Create an account". Accounts live on the server and work on Windows and Mac too.
     Later: enter your name and password, then LOG IN. Tick "Remember me"
     and this computer signs you in by itself for 30 days.
  3. Signing in opens the room list. JOIN a room, QUICK MATCH into the
     fullest room with a free place, or CREATE ROOM (DUSTBOWL, ISLAND or
     FOREST LAKE).
  4. Press READY UP. The match starts by itself once at least 2 players
     in the room are ready; a room with one player just waits.
  5. Pick a spawn point on the minimap and press DEPLOY.

New here? HOW TO PLAY on the main menu, or H on any other menu screen.
Rebind keys in SETTINGS. GLOBAL RANKING and ACHIEVEMENTS are on the main
menu and in the Esc menu during a match.

Nothing to configure: the server address is built into the game.

REQUIREMENTS
  64-bit (x86_64) Linux with a desktop, such as Ubuntu 22.04 or newer, a GPU
  driver with Vulkan or OpenGL 4.5, and an internet connection that allows
  outgoing UDP on ports 27015 to 27017.

PROBLEMS
  Send this log file together with the build number above:
    ~/.config/unity3d/LTM10/IronfrontReborn/Player.log
