# Kiểm thử hiệu năng bằng k6

Sáu tệp k6 đo các yêu cầu hiệu năng của hệ thống (đặc tả, mục 4.2.3) trên môi trường một máy chủ. P95 là phân vị 95: 95% yêu cầu nhanh hơn mức đó.

> **CẢNH BÁO.** Các kịch bản đăng nhập, lưu câu trả lời, nộp bài và ghi lịch sử **ghi dữ liệu thật** và tạo tải lớn (200 người dùng ảo).
> **Không chạy tải đầy đủ lên máy chủ thật (`https://diviet.id.vn` và các máy đang phục vụ người dùng) khi chưa được người phụ trách đồng ý.**
> Chạy tải đầy đủ trên máy thử riêng hoặc backend cục bộ. Lên máy chủ thật chỉ chạy lượt nhẹ `NHE=1` của kịch bản đọc nội dung công khai (chỉ GET).

## Các tệp

| Tệp | Đo gì | Tải | Ngưỡng |
|---|---|---|---|
| `01-dang-nhap-hoc-sinh.js` | Đăng nhập và mở trang học; chạy với `KICH_BAN=ca_lop` thì là cả lớp vào làm bài | 200 em trong 60 giây; cả lớp: 5 lớp song song, mỗi lớp 40 em, chung một IP | đăng nhập P95 ≤ 1 giây, trang học P95 ≤ 2 giây, 0 lỗi máy chủ, 0 em bị chặn nhầm |
| `02-luu-cau-tra-loi.js` | Lưu từng câu trả lời | 200 em, mỗi em 1 câu / 10 giây (khoảng 20 yêu cầu/giây), 3 phút | P95 ≤ 500 ms, mọi lần lưu thành công |
| `03-nop-bai.js` | Nộp bài 20 câu | 200 em nộp trong khung 30 giây | P95 ≤ 2 giây, 0 bài mất (nộp xong xem lại được kết quả) |
| `04-noi-dung-cong-khai.js` | Trang chủ, danh sách, chi tiết, dòng thời gian, 5 ngôn ngữ | 100 người dùng đồng thời, 2 phút | trung bình ≤ 800 ms, P95 ≤ 1,5 giây |
| `05-ban-do-tim-loc.js` | Tìm, lọc theo vùng, tải ghim bản đồ | 100 người đồng thời, 2 phút | P95 ≤ 2 giây, tối đa 500 ghim |
| `06-khach-dong-thoi.js` | Khách đọc và ghi lịch sử | 200 khách, mỗi khách 1 yêu cầu / 3 giây, 5 phút | đọc P95 ≤ 1,5 giây, ghi lịch sử P95 ≤ 300 ms, 0 lỗi máy chủ |
| `chung.js` | Dùng chung: địa chỉ gốc, nạp dữ liệu, gọi API, bộ đếm lỗi | | |
| `tao-du-lieu-tai.py` | Tạo 5 lớp, 200 học sinh, bài học và bài kiểm tra qua API | | |
| `du-lieu/du-lieu.example.json` | Tệp mẫu của dữ liệu học sinh và mã bài (không có dữ liệu thật) | | |

Ngoài ngưỡng thời gian, mọi kịch bản đều có ngưỡng `loi_may_chu` (số trả lời 5xx) = 0 và `bi_gioi_han_tan_suat` (số trả lời 429) = 0.
Ngưỡng thứ hai có mặt để **kịch bản thất bại rõ ràng khi bị giới hạn tần suất chặn**, thay vì cho ra số đo sai (xem mục Giới hạn tần suất).

## Cần có

- k6 (bản Windows tải ở https://github.com/grafana/k6/releases, giải nén là chạy được, không cần cài). Đã thử với k6 v2.3.0.
- Python 3 (chỉ để tạo dữ liệu tải).
- Một backend thử đã nạp dữ liệu demo (xem dưới).

k6 **không tự đọc biến môi trường của hệ thống**: luôn truyền bằng `-e TÊN=giá_trị` như các ví dụ.

## Chuẩn bị dữ liệu (cho các kịch bản học sinh: 01, 02, 03)

Các kịch bản 04, 05, 06 chỉ cần backend có di sản đã xuất bản, không cần tệp dữ liệu.

1. Dựng backend thử với cơ sở dữ liệu tạm và nạp dữ liệu demo (di sản đã xuất bản, trường, giáo viên). Script này bật backend rồi tắt khi xong:

   ```powershell
   pwsh -File scripts/tao-csdl-demo.ps1 -Database dsvhvn_hieu_nang -Port 5190
   ```

   Mật khẩu giáo viên demo và tên đăng nhập `minhanh.demo` được in ở cuối (xem `scripts/du-lieu-demo/README.md`).

2. Bật lại backend trên cơ sở dữ liệu đó, **nới giới hạn tần suất** (xem mục sau). Ví dụ cục bộ, từ thư mục gốc repo:

   ```powershell
   $env:ConnectionStrings__Default = 'Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_hieu_nang;Trusted_Connection=True;TrustServerCertificate=True'
   $env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:Email__Mode = 'Log'; $env:Ai__Mode = 'Simulated'; $env:Storage__Provider = 'Local'
   $env:RateLimiting__PublicRequestsPerMinute = '100000'
   $env:RateLimiting__GuestHistoryEventsPerMinute = '1000'
   dotnet run --project src/DSVHVN.Api -c Release --no-launch-profile --urls http://localhost:5190
   ```

3. Tạo 5 lớp x 40 học sinh, bài học và ba bài kiểm tra mỗi lớp (cho cả lớp vào làm, cho lưu câu trả lời, cho nộp bài):

   ```powershell
   $env:BASE = 'http://localhost:5190'
   $env:TEACHER_PASSWORD = '<mật khẩu giáo viên minhanh.demo>'
   python -X utf8 tests/hieu-nang/tao-du-lieu-tai.py
   ```

   Kết quả ghi vào `tests/hieu-nang/du-lieu/du-lieu.json` (mã học sinh, mật khẩu, mã bài học, mã bài kiểm tra). Tệp này **nằm ngoài git** (`.gitignore`), đừng đưa lên.
   Muốn dùng tệp ở nơi khác: `-e DU_LIEU=<đường dẫn>` (đường dẫn tương đối tính từ thư mục chứa tệp k6) hoặc `OUT=` khi tạo.
   Cấu trúc tệp xem `du-lieu/du-lieu.example.json`; tự chuẩn bị cũng được miễn đúng cấu trúc đó (ít nhất 200 học sinh, 5 lớp, mỗi lớp 40 em, bài kiểm tra 20 câu chọn một phương án).

### Chạy lại

Mỗi lượt làm bài chỉ nộp được một lần, và lượt chưa nộp có hạn theo thời gian làm của bài (30 phút). Sau mỗi lần chạy kịch bản 01 (chế độ cả lớp), 02 hoặc 03,
chạy lại `tao-du-lieu-tai.py`: khi tệp kết quả đã có, script giữ nguyên lớp và học sinh, chỉ tạo **đợt bài mới** (bài học và ba bài kiểm tra mới) và ghi đè mã bài trong tệp.

## Chạy từng kịch bản

Từ thư mục `tests/hieu-nang`. Ví dụ với k6 đặt ở `C:\công-cụ\k6\k6.exe` và backend ở cổng 5190:

```powershell
$k6 = 'C:\cong-cu\k6\k6.exe'; $u = 'http://localhost:5190'
& $k6 run -e BASE_URL=$u 01-dang-nhap-hoc-sinh.js                       # 200 em đăng nhập trong 60 giây
& $k6 run -e BASE_URL=$u -e KICH_BAN=ca_lop 01-dang-nhap-hoc-sinh.js    # 5 lớp x 40 em vào làm bài
& $k6 run -e BASE_URL=$u 02-luu-cau-tra-loi.js                          # lưu câu trả lời
& $k6 run -e BASE_URL=$u 03-nop-bai.js                                  # nộp bài (mất khoảng 3 phút)
& $k6 run -e BASE_URL=$u 04-noi-dung-cong-khai.js
& $k6 run -e BASE_URL=$u 05-ban-do-tim-loc.js
& $k6 run -e BASE_URL=$u 06-khach-dong-thoi.js
```

Biến tùy chọn: `BASE_URL` (mặc định `http://localhost:5180`, cố ý là máy cục bộ), `DU_LIEU`, `THOI_GIAN` (02, 04, 05, 06: ví dụ `-e THOI_GIAN=30s` để thử ngắn),
`MOC_NOP_GIAY` (03: giây kể từ lúc chạy tới lúc bắt đầu nộp, mặc định 150) và `NHE=1` (lượt nhẹ: 1 người dùng ảo, 5 lượt lặp).

Kiểm cú pháp không cần backend: `k6 inspect -e BASE_URL=http://localhost:5190 <tệp>.js`.

Lượt nhẹ trên môi trường công khai (chỉ GET, không đăng nhập, không ghi):

```powershell
& $k6 run -e BASE_URL=https://diviet.id.vn -e NHE=1 04-noi-dung-cong-khai.js
```

Chỉ kịch bản 04 an toàn chạy kiểu này. **Các kịch bản còn lại không chạy lên máy chủ thật.**

## Giới hạn tần suất: đọc trước khi đo

Backend cố ý có các giới hạn chống lạm dụng. Từ một máy chạy k6, mọi người dùng ảo cùng một địa chỉ IP, nên có thể đo nhầm 429 thay vì hiệu năng.

| Giới hạn | Mức mặc định | Ảnh hưởng tới kịch bản |
|---|---|---|
| API công khai, mỗi IP | 300 yêu cầu/phút (`RateLimiting:PublicRequestsPerMinute`) | **04, 05, 06 chắc chắn bị chặn**: 100 hoặc 200 người dùng từ một IP vượt xa 300/phút (06 cần khoảng 4.000/phút). Phải nới giới hạn trên máy thử (đặt biến môi trường `RateLimiting__PublicRequestsPerMinute=100000` cho backend) hoặc chạy k6 từ nhiều IP |
| Ghi lịch sử khách, mỗi mã thiết bị | 120/phút (`RateLimiting:GuestHistoryEventsPerMinute`) | 06 mỗi khách ghi khoảng 4 lần/phút, không chạm giới hạn; vẫn có thể nới khi chạy thủ công |
| Đọc tóm tắt, mỗi mã thiết bị | 60/phút (`RateLimiting:GuestSummaryRequestsPerMinute`) | Các kịch bản không gọi tóm tắt (bị cổng quảng cáo chặn) |
| Đăng nhập học sinh sai | 5 lần sai liên tiếp trong 15 phút thì khóa 15 phút (đếm theo tài khoản) | Không ảnh hưởng nếu mật khẩu trong tệp dữ liệu đúng; sai mật khẩu hàng loạt sẽ khóa tài khoản và bị tính là "chặn nhầm" |
| Nhập mã bài sai | 10 lần/tài khoản và 30 lần/IP trong 10 phút | Không ảnh hưởng nếu mã đúng (01 chế độ cả lớp dùng mã đúng) |
| Quên mật khẩu, liên hệ trường, AI của giáo viên | có giới hạn riêng | Các kịch bản không gọi |

Các đường học sinh (đăng nhập, trang học, làm bài, nộp bài) không bị giới hạn theo IP nên 40 em hoặc 200 em chung một IP không bị chặn nhầm; kịch bản cả lớp kiểm chính điều đó.

Máy chủ chạy sau proxy ngược: nếu chưa cấu hình chuyển tiếp địa chỉ client, backend thấy mọi người dùng cùng IP của proxy và giới hạn 300/phút áp cho cả hệ thống.
Nên đo trên máy thử có cấu hình giống máy chủ thật, với giới hạn đã nới, rồi ghi rõ điều này trong báo cáo.
Khi thấy `bi_gioi_han_tan_suat` khác 0, kết quả của lượt chạy đó **không dùng được** để kết luận về hiệu năng.

## Đọc kết quả

Cuối mỗi lượt chạy k6 in từng ngưỡng với dấu `✓` (đạt) hoặc `✗` (không đạt) kèm giá trị đo được; mã thoát khác 0 khi có ngưỡng không đạt. Các chỉ số cần ghi lại:

- `http_req_duration{name:...}`: thời gian trả lời theo từng thao tác (`dang_nhap`, `trang_hoc`, `luu_cau_tra_loi`, `nop_bai`, `tai_ghim_ban_do`...). `avg` là trung bình, `p(95)` là P95.
- `http_reqs{name:luu_cau_tra_loi}` (kịch bản 02): tốc độ yêu cầu mỗi giây, cần gần 20.
- `loi_may_chu`: số trả lời 5xx, phải bằng 0.
- `hoc_sinh_bi_chan_nham`: số lần học sinh có tài khoản đúng bị từ chối đăng nhập (401, 403, 429), phải bằng 0.
- `bai_mat`: số bài nộp không thành công hoặc không xem lại được, phải bằng 0.
- `bi_gioi_han_tan_suat`: số 429, phải bằng 0 (xem mục trên).
- Các yêu cầu chuẩn bị (đăng nhập vào bài, bắt đầu lượt, lưu câu để chuẩn bị nộp) mang nhãn `chuan_bi`, không tính vào ngưỡng.

Muốn lưu số liệu chi tiết: thêm `--summary-export=ket-qua/<tên>.json` (thư mục `ket-qua/` bị git bỏ qua).

## Ghi kết quả vào Report 5

Với mỗi yêu cầu hiệu năng, ghi một dòng trong bảng kết quả kiểm thử phi chức năng của Report 5, bằng tiếng Anh theo mẫu của template:

1. Tên yêu cầu bằng lời (ví dụ "Student sign-in: 200 students in 60 seconds"), không dùng mã nội bộ.
2. Môi trường: cấu hình máy (CPU, RAM), cơ sở dữ liệu, phiên bản backend (mã commit), phiên bản k6, và **các giới hạn tần suất đã nới** (nếu có).
3. Tải: số người dùng ảo, thời lượng, tốc độ yêu cầu thực tế.
4. Kết quả đo: trung bình, P95, số lỗi máy chủ, số em bị chặn nhầm hoặc số bài mất; ngưỡng đặt ra; kết luận Đạt hoặc Không đạt.
5. Nếu không đạt: ghi nguyên nhân nghi ngờ và biện pháp (ví dụ thêm chỉ mục, tăng cấu hình máy) rồi chạy lại, ghi cả hai lượt.

Không dán tên tài khoản, mật khẩu hay mã bài thật vào report.
