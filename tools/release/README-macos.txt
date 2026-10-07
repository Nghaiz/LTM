IRONFRONT: REBORN  (LTM10)  -  macOS
Bản build {{VERSION}} ({{COMMIT}})

==============================================================
TIẾNG VIỆT
==============================================================

CÀI ĐẶT
  1. Mở file zip (bấm đúp). macOS tự giải nén ra thư mục
     IronfrontReborn-{{VERSION}}, bên trong có Ironfront.app.
  2. Kéo Ironfront.app vào thư mục Applications (Ứng dụng).
     Nếu đã có bản cũ ở đó, chọn "Replace" (Thay thế).
  3. Mở Ironfront.app. Game chưa được Apple công chứng (notarize) nên lần
     đầu macOS sẽ chặn. Làm như sau, chỉ cần một lần:
     - Bấm "Done" (hoặc "OK") trên thông báo của macOS.
     - Mở System Settings > Privacy & Security, kéo xuống dưới, bấm
       "Open Anyway" bên cạnh dòng nói về Ironfront, rồi nhập mật khẩu máy.
     - Trên macOS 14 trở về trước có cách nhanh hơn: giữ phím Control,
       bấm vào Ironfront.app, chọn Open, rồi bấm Open lần nữa.
     Nếu macOS báo game "is damaged and can't be opened", mở Terminal
     và chạy lệnh sau, rồi mở lại game:
       xattr -dr com.apple.quarantine /Applications/Ironfront.app
  4. Nếu macOS hỏi cho phép kết nối mạng đến: bấm "Allow".

VÀO TRẬN
  1. Ở màn hình đầu, bấm MULTIPLAYER.
  2. Lần đầu chơi: bấm "Create an account" để tạo tên đăng nhập và mật khẩu.
     Tài khoản lưu trên server nên máy nào cũng dùng được, kể cả máy Windows.
     Những lần sau: nhập tên, mật khẩu rồi bấm LOG IN.
  3. Bấm BROWSE ROOMS để xem danh sách phòng.
  4. Bấm JOIN để vào phòng có sẵn, hoặc CREATE ROOM để tạo phòng mới.
  5. Trong phòng, bấm READY UP. Trận tự bắt đầu khi có ít nhất 2 người
     READY trong cùng một phòng. Một mình thì phòng sẽ đứng chờ.
  6. Chọn điểm xuất phát trên bản đồ nhỏ rồi bấm DEPLOY.

Người chơi Mac và Windows vào chung phòng được, miễn là cùng phiên bản.
Không cần cấu hình gì thêm. Địa chỉ server đã có sẵn trong game.

YÊU CẦU
  - macOS 12 (Monterey) trở lên, máy Mac chip Intel hoặc Apple (M1 trở lên).
  - Nên dùng chuột. Trên trackpad, nhấp hai ngón là chuột phải.
  - Có Internet, và mạng cho phép gửi UDP ra ngoài qua cổng 27015 đến 27017.
    Một số mạng công ty hoặc trường học chặn UDP. Khi đó bạn vẫn đăng nhập
    được nhưng không vào được trận.
  - Máy dùng GPU tích hợp (MacBook Air, Mac mini) nên chọn mức đồ họa Low
    trong phần cài đặt của game.

GẶP LỖI
  Gửi file log này cho người phát hành game, kèm số bản build ở đầu file:
    ~/Library/Logs/LTM10/IronfrontReborn/Player.log
  Cách mở: trong Finder bấm Go > Go to Folder..., dán dòng trên rồi nhấn Enter.

==============================================================
ENGLISH
==============================================================

INSTALL
  1. Open the zip. macOS extracts a folder holding Ironfront.app.
  2. Drag Ironfront.app into Applications (replace any older copy).
  3. Open Ironfront.app. The game is not notarized by Apple, so macOS
     blocks the first launch. Once only:
     - Click "Done" on the macOS message.
     - Open System Settings > Privacy & Security, scroll down, click
       "Open Anyway" next to the Ironfront line and enter your password.
     - On macOS 14 and older you can instead Control-click Ironfront.app,
       choose Open, then click Open again.
     If macOS says the game "is damaged and can't be opened", run this
     in Terminal and open the game again:
       xattr -dr com.apple.quarantine /Applications/Ironfront.app
  4. If macOS asks to accept incoming network connections: click "Allow".

PLAY
  1. Main menu: MULTIPLAYER.
  2. First time: "Create an account". Accounts live on the server and work
     on Windows too. Later: enter your name and password, then LOG IN.
  3. BROWSE ROOMS, then JOIN a room or CREATE ROOM.
  4. Press READY UP. The match starts by itself once at least 2 players
     in the room are ready; a room with one player just waits.
  5. Pick a spawn point on the minimap and press DEPLOY.

Mac and Windows players share rooms when they run the same version.
Nothing to configure: the server address is built into the game.

REQUIREMENTS
  macOS 12 (Monterey) or newer on an Intel or Apple silicon Mac, a mouse
  (a two-finger trackpad click is a right click), and an internet
  connection that allows outgoing UDP on ports 27015 to 27017. On Macs
  with integrated graphics, start with the Low graphics preset.

PROBLEMS
  Send this log file together with the build number above:
    ~/Library/Logs/LTM10/IronfrontReborn/Player.log
  In Finder: Go > Go to Folder..., paste the path, press Enter.
