# Dữ liệu demo cho cơ sở dữ liệu thật

Hai script tạo cơ sở dữ liệu thật trên SQL Server LocalDB (đủ mọi migration) và nạp dữ liệu demo qua API của chính backend,
để mở web quản trị, web học tập và app mobile là có số liệu để trình diễn.

| Tệp | Việc |
|---|---|
| `scripts/tao-csdl-demo.ps1` | Kiểm môi trường, dựng và chạy backend (Development) trên cơ sở dữ liệu đích, gọi script Python, tắt backend, in bảng tài khoản |
| `scripts/du-lieu-demo/nap-du-lieu-demo.py` | Nạp dữ liệu demo qua API (chạy riêng được khi đã có backend); `--chi-mo-hinh` chỉ gắn mô hình 3D thật vào cơ sở dữ liệu đã nạp |
| `scripts/du-lieu-demo/tai-mo-hinh-3d.py` | Tải mô hình 3D giấy phép mở trong `seed-data/mo-hinh-3d.json` từ Sketchfab và nén thành `.glb` dưới 15 MB vào `seed-data/mo-hinh-3d/` |

## Cách chạy

Cần: .NET SDK 9, SQL Server LocalDB, Python 3, `sqlcmd` trong PATH.

```powershell
pwsh -File scripts/tao-csdl-demo.ps1                                  # cơ sở dữ liệu sep490_dsvhvn, cổng 5180
pwsh -File scripts/tao-csdl-demo.ps1 -Database dsvhvn_tam_thu -Port 5190
```

Tham số:

- `-Database`: tên cơ sở dữ liệu (mặc định `sep490_dsvhvn`). Chưa có thì backend tự tạo và áp migration.
- `-Port`: cổng backend tạm thời (mặc định 5180). Script không dùng cổng 5080 của backend chạy hằng ngày.
- `-AdminPassword`: mật khẩu chung đặt cho 4 quản trị hệ thống (`admin.nho`, `admin.lam`, `admin.trieu`, `admin.huy`) khi cơ sở dữ liệu còn mới.
  Dài 8-64 ký tự, có chữ hoa, chữ thường và chữ số. Không truyền thì script hỏi bằng `Read-Host -AsSecureString`. Mật khẩu chỉ đi vào backend
  qua biến môi trường của tiến trình con, không ghi ra tệp hay log. Cơ sở dữ liệu đã có quản trị rồi thì phải nhập đúng mật khẩu đang dùng.

Muốn chạy backend hằng ngày (cổng 5080) với cùng mật khẩu quản trị, đặt mật khẩu trong User Secrets một lần, trước lần chạy đầu trên cơ sở dữ liệu mới:

```powershell
dotnet user-secrets set "Seed:AdminPassword" "<mật khẩu của bạn>" --project src/DSVHVN.Api
```

Cách an toàn:

- Email luôn ở chế độ Log (`Email__Mode=Log`): không gửi thư thật dù User Secrets đang cấu hình SMTP. Liên kết đặt mật khẩu được đọc từ log của backend.
- Nơi lưu tệp là `Local` (tệp mô hình 3D nằm trong `src/DSVHVN.Api/storage/`, đã được bỏ qua khỏi git), dịch vụ AI chạy bản giả lập.
- Cơ sở dữ liệu đã có trường demo thì script báo và thoát, không nạp trùng. Muốn nạp lại thì xoá cơ sở dữ liệu rồi chạy lại.
- Tiến trình backend do script bật được tắt theo PID khi xong hoặc khi lỗi.
- Tệp kết quả JSON (mã học sinh, mã bài học, ai đã nộp bài) nằm trong thư mục tạm `%TEMP%\dsvhvn-demo-<giờ>\`; không có mật khẩu quản trị.

## Dữ liệu gồm gì

**Di sản** (nạp khi backend khởi động, từ `seed-data/`): 34 tỉnh/thành, 20 di sản, tất cả được xuất bản. Mười hai di sản tiêu biểu có bản dịch và
tóm tắt: tiếng Việt và tiếng Anh đã duyệt, tiếng Hàn, Trung, Nhật là bản dịch máy. Vì dịch vụ AI là bản giả lập nên chữ dịch có tiền tố mã ngôn ngữ
(ví dụ `[en] ...`): đủ để thử chế độ khách ở 5 ngôn ngữ, không phải bản dịch thật.

**Mô hình 3D**: 14 di sản có mô hình 3D thật lấy từ Sketchfab (giấy phép CC BY hoặc CC0, ghi công đủ tác giả, giấy phép, trang nguồn), nếu tệp `.glb`
đã được tải về `seed-data/mo-hinh-3d/` (xem mục dưới). Chưa có tệp thì hai di sản (Khu Trung tâm Hoàng thành Thăng Long và Quần thể di tích Cố đô Huế)
có mô hình mẫu tối thiểu (một hình tam giác), giấy phép CC0, ghi công "Mô hình mẫu để thử tính năng xem 3D".

**Trường demo** "Trường Liên cấp Demo Di sản Việt" (gói Miễn phí): 1 quản trị trường, 2 giáo viên, 3 lớp năm học 2026-2027, mỗi lớp 8 học sinh đã đổi sang mật khẩu demo.

| Lớp | Khối (cấp) | Giáo viên | Bài học đã phát hành | Dạng câu hỏi dùng |
|---|---|---|---|---|
| 3A | 3 (cấp 1) | Trần Thị Minh Anh | Hoàng thành Thăng Long qua hình ảnh; Vịnh Hạ Long và quần đảo Cát Bà | Đúng/Sai có hình, chọn hình đúng, ghép cặp hình - tên, sắp xếp |
| 8A1 | 8 (cấp 2) | Trần Thị Minh Anh | Quần thể di tích Cố đô Huế; Thành nhà Hồ | Một đáp án đúng, Đúng/Sai, sắp xếp, điền từ |
| 11A2 | 11 (cấp 3) | Lê Hoàng Nam | Khu di tích Đền Hùng; Đô thị cổ Hội An | Một đáp án đúng, nhiều mệnh đề Đúng/Sai, đọc tư liệu rồi trả lời |

Mỗi bài học có mã bài học, nội dung soạn bằng AI giả lập, và một bài kiểm tra 5 câu đang mở đến hết ngày 30/06/2027. Năm em đầu của mỗi lớp đã làm và nộp
bài kiểm tra thứ nhất với điểm giảm dần (đúng 5, 4, 3, 2, 1 trong 5 câu); ba em đầu đã nộp bài thứ hai. Các em còn lại chưa làm để trình diễn làm bài
(mỗi học sinh chỉ có một lượt cho mỗi bài kiểm tra).

## Mô hình 3D thật

`seed-data/mo-hinh-3d.json` liệt kê 27 mô hình giấy phép mở trên Sketchfab cho 14 di sản: mỗi di sản một mô hình chọn chính (`"role": "chinh"`) và các
mô hình dự phòng. Mỗi mục có tên hiển thị, mức gắn với di sản ("hiện vật của di sản", "công trình của di sản" hoặc "liên quan, minh họa"), tác giả,
giấy phép và trang nguồn. Tệp `.glb` nặng nên không nằm trong git; tải và nén một lần:

```powershell
# Khóa API Sketchfab (Settings → Password & API) lưu ở %USERPROFILE%\.sketchfab_token, hoặc biến môi trường SKETCHFAB_TOKEN
python scripts/du-lieu-demo/tai-mo-hinh-3d.py            # 14 mô hình chính, cần Node.js
```

Sau đó `tao-csdl-demo.ps1` tự gắn mô hình thật cho cơ sở dữ liệu mới. Cơ sở dữ liệu đã nạp từ trước (có mô hình mẫu) thì chạy riêng phần mô hình:

```powershell
$env:BASE = "http://localhost:5080"; $env:ADMIN_PASSWORD = "<mật khẩu admin.nho>"
python -X utf8 scripts/du-lieu-demo/nap-du-lieu-demo.py --chi-mo-hinh
```

Phần này gỡ mô hình mẫu, tải mô hình thật lên và cho hiện; di sản đã có mô hình cùng trang nguồn thì bỏ qua (chạy lại không trùng); di sản đang hiện
một mô hình khác do người dùng tự thêm thì mô hình thật chỉ lưu nháp. Giấy phép CC BY yêu cầu ghi rõ đã chỉnh sửa nên giấy phép được ghi
"CC BY 4.0, đã nén để xem trên web". Đổi mô hình cho một di sản: đổi `role` trong tệp JSON, tải tệp mới bằng `--chi <slug>`.

## Tài khoản demo

Mật khẩu chung cho quản trị trường, giáo viên và học sinh: `DemoDsvhvn2026`.

| Vai trò | Tên đăng nhập | Email |
|---|---|---|
| Quản trị hệ thống | `admin.nho`, `admin.lam`, `admin.trieu`, `admin.huy` | (email nội bộ theo cấu hình seed) |
| Quản trị trường (Nguyễn Thị Hương Giang) | `huonggiang.demo` | `huonggiang@demo.dsvhvn.local` |
| Giáo viên (Trần Thị Minh Anh, lớp 3A và 8A1) | `minhanh.demo` | `minhanh@demo.dsvhvn.local` |
| Giáo viên (Lê Hoàng Nam, lớp 11A2) | `hoangnam.demo` | `hoangnam@demo.dsvhvn.local` |

Quản trị hệ thống: mật khẩu là mật khẩu đã truyền cho script (hoặc `Seed:AdminPassword` trong User Secrets), không có mật khẩu mặc định trong repo.

Học sinh đăng nhập ở web học tập (hoặc chế độ Học sinh của app) bằng **mã học sinh** và mật khẩu `DemoDsvhvn2026`. Mã học sinh do hệ thống sinh ngẫu nhiên khi
tạo, nên khác nhau giữa các lần nạp; xem trong bảng script in ra ở cuối, trong tệp JSON kết quả, hoặc truy vấn:

```sql
SELECT s.student_code, s.full_name, c.name AS lop, c.grade AS khoi
FROM students s JOIN classes c ON c.id = s.class_id ORDER BY c.grade, s.id;
```

Tên học sinh theo thứ tự trong lớp (năm em đầu đã nộp bài):

- 3A: Nguyễn Minh Khang, Trần Gia Hân, Lê Bảo Nam, Phạm Khánh Linh, Hoàng Anh Tuấn, Vũ Thảo My, Đặng Quốc Bảo, Bùi Ngọc Diệp
- 8A1: Ngô Đức Anh, Dương Thùy Chi, Lý Hoàng Long, Đỗ Phương Thảo, Trịnh Minh Quân, Phan Hải Yến, Võ Thành Đạt, Đinh Mai Phương
- 11A2: Nguyễn Hữu Phúc, Lê Thị Kim Ngân, Trần Quang Vinh, Phạm Thu Hà, Hồ Gia Bảo, Mai Xuân Trang, Cao Tiến Dũng, Lương Thanh Tâm

Mã bài học cũng được sinh ngẫu nhiên; xem trong bảng script in ra, hoặc trên màn hình bài học của giáo viên.

## Sửa dữ liệu demo

Tên, câu hỏi và số liệu nằm đầu tệp `nap-du-lieu-demo.py` (danh sách tên, `question_sets()`, `DONE_COUNT`). Sửa rồi nạp lại trên cơ sở dữ liệu mới.
