# Báo cáo kiến trúc và giao diện hệ thống (LaTeX)

Bốn báo cáo cá nhân dùng chung phần nhóm. Sửa ở đâu:

| Muốn sửa | Tệp |
|---|---|
| Lớp, nhóm, đề tài, dòng tiêu đề trên bìa | `common/bia.tex` (các `\newcommand` ở đầu tệp) |
| Font, lề, màu, kiểu tiêu đề, mục lục, style sơ đồ | `common/preamble.tex` |
| Phần I -- nội dung nhóm (giống nhau ở cả 4 bản) | `common/phan1-nhom.tex` |
| Phần II -- nội dung cá nhân | `individual/A-client.tex`, `B-transport.tex`, `C-replication.tex`, `D-masterserver.tex` |
| Tên, mã sinh viên của từng bản | dòng `\BiaBaoCao{...}{...}` trong `BaoCao_*.tex` |
| Khung bìa và logo | `image/khung-bia.png`, `image/logo-ptit.png` |

## Môi trường biên dịch

Chuyển LaTeX sang PDF **cần một trình biên dịch XeLaTeX** (tài liệu dùng `fontspec` để có font tiếng Việt,
nên `pdflatex` không dùng được). Đây không phải dự án Python nên không dùng `venv`; thay vào đó có ba cách,
chọn một:

### Cách 1 -- Không cài gì: để script tự tải (khuyên dùng)

`build.sh` / `build.ps1` tự tìm trình biên dịch. Nếu máy chưa có, script tải **Tectonic** (một tệp chạy
khoảng 20 MB, bản XeLaTeX gọn nhẹ) vào thư mục cục bộ `latex/.tools/` -- tương tự một môi trường ảo:
không cần sudo/admin, không đụng tới hệ thống, xoá `.tools/` là gỡ sạch.

```bash
# Linux / macOS
cd docs/report/latex
./build.sh                                        # cả 4 bản
./build.sh BaoCao_D_NguyenTuKien_MasterServer.tex # một bản
```

```powershell
# Windows (PowerShell)
cd docs\report\latex
pwsh build.ps1
pwsh build.ps1 BaoCao_D_NguyenTuKien_MasterServer.tex
```

Yêu cầu: có Internet ở **lần chạy đầu** (tải Tectonic, sau đó Tectonic tải các gói TeX cần dùng, khoảng
vài chục MB, lưu vào bộ nhớ đệm của người dùng). Các lần sau chạy offline được.

PDF được chuyển ra `docs/report/`; tệp trung gian bị xoá.

### Cách 2 -- Cài TeX đầy đủ trên máy

| Hệ điều hành | Lệnh cài |
|---|---|
| Ubuntu / Debian | `sudo apt install texlive-xetex texlive-latex-extra texlive-pictures texlive-lang-other fonts-texgyre` |
| macOS | `brew install --cask mactex-no-gui` |
| Windows | Cài [MiKTeX](https://miktex.org/download) (tự tải gói còn thiếu khi biên dịch) |

Sau khi cài, `build.sh` / `build.ps1` tự dùng `xelatex` (chạy 2 lần để có mục lục và số hình). Hoặc chạy tay:

```bash
xelatex BaoCao_D_NguyenTuKien_MasterServer.tex
xelatex BaoCao_D_NguyenTuKien_MasterServer.tex
```

### Cách 3 -- Overleaf (không cài gì trên máy)

Nén cả thư mục `latex/` thành zip, tải lên Overleaf (New Project → Upload Project), chọn
Menu → Compiler → **XeLaTeX**, và đặt Main document là tệp `BaoCao_*.tex` muốn xuất.

### Font

Nếu máy có Times New Roman (Windows, Overleaf) thì font đó được dùng; nếu không, tự chuyển sang
TeX Gyre Termes -- bản sao Times có đủ dấu tiếng Việt, đi kèm Tectonic và TeX Live.

## Sơ đồ

Tất cả sơ đồ vẽ bằng TikZ, toạ độ tính bằng cm (khổ nội dung rộng khoảng 16 cm). Macro dùng chung trong
`common/preamble.tex`:

- `\seqactor`, `\seqmsg`, `\seqret`, `\seqlost`, `\seqself`, `\seqnote`, `\seqloopbegin/\seqloopend`, `\seqend` -- sơ đồ tuần tự
- `\buoc{tên}{(x,y)}{số}{tiêu đề}{mô tả}{màu}` -- hộp bước đánh số
- style `bxB`, `bxG`, `bxO`, `bxP`, `bxY` (hộp màu), `dec` (hình thoi), `db` (CSDL), `st` (trạng thái), `arr`/`darr` (mũi tên)

Giao diện IMGUI (Hình 3.4 báo cáo A) dùng các macro `\uiPanel`, `\uiT`, `\uiF`, `\uiB`, `\uiN` ở đầu `individual/A-client.tex`.
Hình được đánh số tự động theo mục (Hình 3.1, 3.2...); tham chiếu hình trong văn bản bằng `\ref{fig:...}`.
