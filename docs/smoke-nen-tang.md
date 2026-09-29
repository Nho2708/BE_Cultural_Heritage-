# Smoke test luồng Nền tảng tổ chức trên LocalDB

| Mục | Giá trị |
|---|---|
| Ngày chạy | 28/09/2026 (giờ Việt Nam), sau khi dựng CSDL v3 |
| CSDL | `(localdb)\MSSQLLocalDB` · database tạm `dsvhvn_kiem_thu_smoke` (chuỗi kết nối qua biến môi trường `ConnectionStrings__Default`, đã xóa sau khi chạy) · collation `Vietnamese_CI_AI` · migration `20260928164123_KhoiTaoCsdl` |
| API | `dotnet run --no-launch-profile` (Development, `http://localhost:5088`), mật khẩu ADMIN seed từ User Secrets `Seed:AdminPassword` |
| Lệnh | `BASE=http://localhost:5088 API_LOG=… ADMIN_PASSWORD=… bash scripts/smoke-nen-tang.sh` — body JSON tiếng Việt đi qua tệp (`curl --data-binary @file`), liên kết đặt mật khẩu đọc từ log thư (API chạy với `Email:Mode = Log`, mặc định) |
| Kết quả | **39/39 bước đúng mã HTTP và mã thông điệp** |

Log khởi động của lần chạy: áp migration khởi tạo; seed gói Miễn phí (365 ngày, 10 giáo viên, 300 lượt AI); seed 4 ADMIN; nạp
34 tỉnh, 20 di sản, 37 giai đoạn, 158 sự kiện, 64 ảnh, 110 nguồn; trường `key`, `map_point_note` của tệp dữ liệu không có cột nên không lưu.

| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |
|---|---|---|---|---|---|
| 01-health | GET /health | 200 | 200 | ĐÚNG |  |
| 02-login-sai | POST /api/v1/auth/login | 401 | 401 | ĐÚNG | INVALID_CREDENTIALS Email, tên đăng nhập hoặc mật khẩu không đúng. |
| 03-login-admin | POST /api/v1/auth/login | 200 | 200 | ĐÚNG | Đăng nhập thành công. |
| 04-me-admin | GET /api/v1/me | 200 | 200 | ĐÚNG |  |
| 05-tao-truong-a | POST /api/v1/admin/organizations | 201 | 201 | ĐÚNG | Đã gửi email đặt mật khẩu tới thu.235457@nguyendu.edu.vn. Liên kết có hiệu lực 48 giờ. |
| 06-tao-truong-trung | POST /api/v1/admin/organizations | 409 | 409 | ĐÚNG | DUPLICATE Email "thu.235457@nguyendu.edu.vn" đã được sử dụng cho một tài khoản khác. |
| 07-ds-truong | GET /api/v1/admin/organizations?keyword=235457&status=ACTIVE | 200 | 200 | ĐÚNG |  |
| 08-kiem-lien-ket | POST /api/v1/auth/password-token/check | 200 | 200 | ĐÚNG |  |
| 09-dat-mk-lan-dau | POST /api/v1/auth/set-password | 200 | 200 | ĐÚNG | Đặt mật khẩu thành công. |
| 10-dung-lai-lien-ket | POST /api/v1/auth/set-password | 422 | 422 | ĐÚNG | PASSWORD_LINK_INVALID Liên kết đã hết hạn hoặc không còn dùng được. Vui lòng yêu cầu gửi liên kết mới. |
| 11-login-org-admin | POST /api/v1/auth/login | 200 | 200 | ĐÚNG | Đăng nhập thành công. |
| 12-xem-truong | GET /api/v1/org | 200 | 200 | ĐÚNG |  |
| 13-sua-lien-he | PUT /api/v1/org | 200 | 200 | ĐÚNG | Lưu thông tin trường thành công. |
| 14-tao-giao-vien | POST /api/v1/org/teachers | 201 | 201 | ĐÚNG | Đã gửi email đặt mật khẩu tới linh.235457@nguyendu.edu.vn. Liên kết có hiệu lực 48 giờ. |
| 15-ds-giao-vien | GET /api/v1/org/teachers | 200 | 200 | ĐÚNG |  |
| 16-gv-dat-mk | POST /api/v1/auth/set-password | 200 | 200 | ĐÚNG | Đặt mật khẩu thành công. |
| 17-login-gv | POST /api/v1/auth/login | 200 | 200 | ĐÚNG | Đăng nhập thành công. |
| 18-gv-vao-khu-truong | GET /api/v1/org/teachers | 403 | 403 | ĐÚNG | FORBIDDEN Bạn không có quyền thực hiện thao tác này. |
| 19-org-admin-vao-qt | GET /api/v1/admin/organizations | 403 | 403 | ĐÚNG | FORBIDDEN Bạn không có quyền thực hiện thao tác này. |
| 20-tao-truong-b | POST /api/v1/admin/organizations | 201 | 201 | ĐÚNG | Đã gửi email đặt mật khẩu tới hung.235457@leloi.edu.vn. Liên kết có hiệu lực 48 giờ. |
| 21-b-dat-mk | POST /api/v1/auth/set-password | 200 | 200 | ĐÚNG | Đặt mật khẩu thành công. |
| 22-login-b | POST /api/v1/auth/login | 200 | 200 | ĐÚNG | Đăng nhập thành công. |
| 23-b-xem-gv-cua-a | GET /api/v1/org/teachers/6 | 404 | 404 | ĐÚNG | BLOCKED Không thể xem tài khoản. Không tìm thấy giáo viên trong trường của bạn. |
| 24-b-khoa-gv-cua-a | POST /api/v1/org/teachers/6/lock | 404 | 404 | ĐÚNG | BLOCKED Không thể khóa tài khoản. Không tìm thấy giáo viên trong trường của bạn. |
| 25-khoa-gv | POST /api/v1/org/teachers/6/lock | 200 | 200 | ĐÚNG | Khóa tài khoản thành công. |
| 26-gv-bi-khoa | GET /api/v1/me | 401 | 401 | ĐÚNG | ACCOUNT_BLOCKED Tài khoản không đăng nhập được. Tài khoản đã bị khóa. Vui lòng liên hệ quản trị viên. |
| 27-mo-khoa-gv | POST /api/v1/org/teachers/6/unlock | 200 | 200 | ĐÚNG | Mở khóa tài khoản thành công. |
| 28-gv-vao-lai | GET /api/v1/me | 200 | 200 | ĐÚNG |  |
| 29-doi-mk | PUT /api/v1/me/password | 200 | 200 | ĐÚNG | Đổi mật khẩu thành công. |
| 30-token-cu | GET /api/v1/me | 401 | 401 | ĐÚNG | FORBIDDEN Bạn không có quyền thực hiện thao tác này. |
| 31-xoa-gv | DELETE /api/v1/org/teachers/6 | 200 | 200 | ĐÚNG | Xóa tài khoản giáo viên thành công. |
| 32-gv-da-xoa-login | POST /api/v1/auth/login | 401 | 401 | ĐÚNG | INVALID_CREDENTIALS Email, tên đăng nhập hoặc mật khẩu không đúng. |
| 33-nhat-ky | GET /api/v1/admin/audit-logs?pageSize=50 | 200 | 200 | ĐÚNG |  |
| 34-ngung-truong-b | DELETE /api/v1/admin/organizations/3 | 200 | 200 | ĐÚNG | Ngừng trường thành công. |
| 35-b-token-cu | GET /api/v1/me | 401 | 401 | ĐÚNG | ACCOUNT_BLOCKED Tài khoản không đăng nhập được. Trường của bạn đã ngừng sử dụng nền tảng. |
| 36-b-login | POST /api/v1/auth/login | 403 | 403 | ĐÚNG | ACCOUNT_BLOCKED Tài khoản không đăng nhập được. Trường của bạn đã ngừng sử dụng nền tảng. |
| 37-khong-token | GET /api/v1/me | 401 | 401 | ĐÚNG | FORBIDDEN Bạn không có quyền thực hiện thao tác này. |
| 38-quen-mk | POST /api/v1/auth/forgot-password | 200 | 200 | ĐÚNG | Nếu email này thuộc một tài khoản, liên kết đặt lại mật khẩu sẽ được gửi tới hộp thư. Liên kết có hiệu lực 30 phút. |
| 39-chi-tiet-truong-a | GET /api/v1/admin/organizations/1 | 200 | 200 | ĐÚNG |  |

Hậu tố dữ liệu của lần chạy: `235457` (trường A id 1, trường B id 3 vì id 2 bị hủy cùng giao dịch ở bước 06, giáo viên id 6).

## Đối chiếu CSDL

Lược đồ do migration tạo được đối chiếu với DBML ở [co-so-du-lieu.md](co-so-du-lieu.md) mục 3 và kiểm tự động trong
`SqlServerDatabaseTests` (27 bảng, 40 khóa ngoại, một Cascade, 19 CHECK, 4 UNIQUE có lọc, collation, dữ liệu di sản, tài khoản học sinh).
