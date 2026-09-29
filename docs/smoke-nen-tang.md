# Smoke test luồng Nền tảng tổ chức trên LocalDB

| Mục | Giá trị |
|---|---|
| Ngày chạy | 26/09/2026 (giờ Việt Nam) |
| Nhánh | nhánh làm luồng Nền tảng tổ chức (trước khi gộp commit) |
| CSDL | `(localdb)\MSSQLLocalDB` · `sep490_dsvhvn_v2_dev` · collation `Vietnamese_CI_AI` · migration `20260925184908_NenTangToChuc` (khi chạy mang tên cũ, nội dung như nhau) |
| API | `dotnet run --launch-profile http` (Development, `http://localhost:5080`), mật khẩu ADMIN seed qua biến môi trường `Seed__AdminPassword` |
| Lệnh | `API_LOG=… ADMIN_PASSWORD=… bash scripts/smoke-nen-tang.sh` — body JSON tiếng Việt đi qua tệp (`curl --data-binary @file`), liên kết đặt mật khẩu đọc từ log thư giả |
| Kết quả | **39/39 bước đúng mã HTTP và mã thông điệp** (cột errorCode ghi theo tên mã hiện hành) |

| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |
|---|---|---|---|---|---|
| 01-health | GET /health | 200 | 200 | ĐÚNG |  |
| 02-login-sai | POST /api/v1/auth/login | 401 | 401 | ĐÚNG | INVALID_CREDENTIALS Email, tên đăng nhập hoặc mật khẩu không đúng. |
| 03-login-admin | POST /api/v1/auth/login | 200 | 200 | ĐÚNG | Đăng nhập thành công. |
| 04-me-admin | GET /api/v1/me | 200 | 200 | ĐÚNG |  |
| 05-tao-truong-a | POST /api/v1/admin/organizations | 201 | 201 | ĐÚNG | Đã gửi email đặt mật khẩu tới thu.015703@nguyendu.edu.vn. Liên kết có hiệu lực 48 giờ. |
| 06-tao-truong-trung | POST /api/v1/admin/organizations | 409 | 409 | ĐÚNG | DUPLICATE Email "thu.015703@nguyendu.edu.vn" đã được sử dụng cho một tài khoản khác. |
| 07-ds-truong | GET /api/v1/admin/organizations?keyword=015703&status=ACTIVE | 200 | 200 | ĐÚNG |  |
| 08-kiem-lien-ket | POST /api/v1/auth/password-token/check | 200 | 200 | ĐÚNG |  |
| 09-dat-mk-lan-dau | POST /api/v1/auth/set-password | 200 | 200 | ĐÚNG | Đặt mật khẩu thành công. |
| 10-dung-lai-lien-ket | POST /api/v1/auth/set-password | 422 | 422 | ĐÚNG | PASSWORD_LINK_INVALID Liên kết đã hết hạn hoặc không còn dùng được. Vui lòng yêu cầu gửi liên kết mới. |
| 11-login-org-admin | POST /api/v1/auth/login | 200 | 200 | ĐÚNG | Đăng nhập thành công. |
| 12-xem-truong | GET /api/v1/org | 200 | 200 | ĐÚNG |  |
| 13-sua-lien-he | PUT /api/v1/org | 200 | 200 | ĐÚNG | Lưu thông tin trường thành công. |
| 14-tao-giao-vien | POST /api/v1/org/teachers | 201 | 201 | ĐÚNG | Đã gửi email đặt mật khẩu tới linh.015703@nguyendu.edu.vn. Liên kết có hiệu lực 48 giờ. |
| 15-ds-giao-vien | GET /api/v1/org/teachers | 200 | 200 | ĐÚNG |  |
| 16-gv-dat-mk | POST /api/v1/auth/set-password | 200 | 200 | ĐÚNG | Đặt mật khẩu thành công. |
| 17-login-gv | POST /api/v1/auth/login | 200 | 200 | ĐÚNG | Đăng nhập thành công. |
| 18-gv-vao-khu-truong | GET /api/v1/org/teachers | 403 | 403 | ĐÚNG | FORBIDDEN Bạn không có quyền thực hiện thao tác này. |
| 19-org-admin-vao-qt | GET /api/v1/admin/organizations | 403 | 403 | ĐÚNG | FORBIDDEN Bạn không có quyền thực hiện thao tác này. |
| 20-tao-truong-b | POST /api/v1/admin/organizations | 201 | 201 | ĐÚNG | Đã gửi email đặt mật khẩu tới hung.015703@leloi.edu.vn. Liên kết có hiệu lực 48 giờ. |
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

Hậu tố dữ liệu của lần chạy: `015703` (trường A id 1, trường B id 3, giáo viên id 6).

## Đối chiếu CSDL sau khi chạy (sqlcmd, xuất Unicode)

| Kiểm | Kết quả |
|---|---|
| Collation CSDL | `Vietnamese_CI_AI` |
| 7 bảng + `__EFMigrationsHistory` | `audit_logs`, `organizations`, `password_reset_tokens`, `plans`, `roles`, `subscriptions`, `users` |
| Unique có lọc | `UX_users_email`, `UX_users_username`: `([deleted_at] IS NULL)` |
| Khóa ngoại | 5 NO ACTION; `fk_password_reset_tokens_user_id` CASCADE |
| Seed | 3 vai trò (Quản trị hệ thống, Quản trị trường, Giáo viên); gói "Miễn phí" 0 đ, 365 ngày, 10 giáo viên, 300 lượt AI (cấu hình Development); 4 ADMIN (Nguyễn Trần Quang Nhớ, Nguyễn Quang Lâm, Ông Trần Hải Triều, Nguyễn Việt Huy) |
| Tiếng Việt lưu đúng dấu | "Trường THCS Nguyễn Du 015703", "180 Cao Lỗ, Phường 4, Quận 8, TP. Hồ Chí Minh", "Đặng Thị Mỹ Linh" |
| Rollback khi trùng (bước 06) | Không có trường id 2 (identity đã cấp rồi bị hủy cùng giao dịch); không có đăng ký gói mồ côi |
| Gói khi tạo trường | 2 đăng ký ACTIVE, `start_date` 2026-09-26 (ngày Việt Nam, lúc chạy là 18:57 UTC ngày 25), `end_date` 2027-09-25 |
| Trạng thái sau cùng | Giáo viên id 6 đã xóa mềm; trường B có `deleted_at` |
| `audit_logs` | 12 dòng: LOGIN 4, ORG_CREATED 2, ACCOUNT_CREATED 3, ACCOUNT_LOCKED 1, ACCOUNT_UNLOCKED 1, ACCOUNT_DELETED 1, ORG_DEACTIVATED 1; `new_value` JSON giữ chữ Việt, có `ip_address` |
| `password_reset_tokens` | `token_hash` 64 ký tự hex; chỉ còn 1 token chưa dùng (liên kết quên mật khẩu ở bước 38) |
