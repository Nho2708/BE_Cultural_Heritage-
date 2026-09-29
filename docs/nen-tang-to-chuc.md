# Luồng Nền tảng tổ chức: tài khoản, trường, giáo viên, nhật ký (Backend)

Làm lại theo **hướng mới 25/09/2026** (nền tảng cho trường học, giáo viên là người dùng chính). Bản này chạy trên CSDL v2;
thay toàn bộ phần nền tảng của hướng cũ (người học tự đăng ký, danh mục, sở thích, refresh token).

| Mục | Giá trị |
|---|---|
| Chức năng | Đăng nhập · Đặt mật khẩu qua email · Hồ sơ cá nhân · Quản lý thông tin trường · Quản lý tài khoản giáo viên · Xem nhật ký kiểm toán |
| Quy tắc nghiệp vụ | Tài khoản do quản trị tạo (không tự đăng ký), mật khẩu 8-64 ký tự có chữ hoa, chữ thường, chữ số; chống dò mật khẩu; liên kết đặt mật khẩu; phạm vi theo vai trò và trường; hạn mức giáo viên; nhật ký kiểm toán; gói "Miễn phí", đăng ký ACTIVE khi tạo trường, gói hiệu lực; chặn tạo giáo viên khi không có gói hiệu lực |
| Màn hình phục vụ | Đăng nhập, Đặt mật khẩu, Hồ sơ, Thông tin trường (tab Thông tin trường), Giáo viên của trường, biểu mẫu tài khoản, Quản lý trường, Chi tiết trường, Nhật ký kiểm toán |
| Chức năng nền | Kiểm gói và hạn mức (phần `max_teachers`) · thư đặt mật khẩu (bản giả) · ghi nhật ký · xác thực, phân quyền, tách dữ liệu theo trường |
| Nguồn | `doc/huong-moi/05-quyet-dinh-can-chot.md` · `doc/database/dsvhvn-v2.dbml` · đặc tả nội bộ của nhóm |

## 1. Cấu trúc solution

```
src/DSVHVN.Domain          Thực thể theo DBML v2 (Organization, Role, User, PasswordResetToken, AuditLog, Plan, Subscription),
                           enum viết HOA đúng giá trị CSDL, quy tắc thuần (PasswordPolicy, UsernamePolicy, VietnamTime)
src/DSVHVN.Application     AuthService, ProfileService, OrganizationService (ADMIN), MyOrganizationService + TeacherService (ORG_ADMIN),
                           AccountProvisioner, AccountActions, PasswordTokenService, PlanGuard (kiểm gói và hạn mức),
                           AuditLogger + AuditLogService, validator FluentValidation, danh mục thông điệp (Messages, MessageCodes)
src/DSVHVN.Infrastructure  EF Core 9 SQL Server (AppDbContext, cấu hình bảng, migration NenTangToChuc, seed), JWT, PBKDF2,
                           bộ đếm khóa đăng nhập trong bộ nhớ, thư giả ghi log
src/DSVHVN.Api             Controllers, middleware (Correlation Id, khuôn phản hồi, header bảo mật), policy 3 vai trò
tests/DSVHVN.Tests         xUnit: Unit/ (SQLite trong bộ nhớ) + Integration/ (WebApplicationFactory + SQLite trong bộ nhớ)
```

Phụ thuộc một chiều: Api → Application ← Infrastructure; Domain không phụ thuộc gì.

## 2. Endpoint (tiền tố `/api/v1`)

Mọi endpoint mặc định phải đăng nhập (FallbackPolicy); endpoint công khai khai báo `[AllowAnonymous]`.
Cột "Lỗi chính" ghi mã HTTP và `errorCode` trả về.

| Method | Đường dẫn | Quyền | Thành công | Lỗi chính |
|---|---|---|---|---|
| POST | `/auth/login` `{ emailOrUsername, password }` | Công khai | 200 access token 8 giờ + hồ sơ | 400 `REQUIRED` · 401 `INVALID_CREDENTIALS` · 403 `ACCOUNT_BLOCKED` |
| POST | `/auth/forgot-password` `{ email }` | Công khai | 200 (luôn cùng một câu) | 400 `REQUIRED`/`INVALID` |
| POST | `/auth/password-token/check` `{ token }` | Công khai | 200 `{ username, expiresAt }` | 422 `PASSWORD_LINK_INVALID` |
| POST | `/auth/set-password` `{ token, newPassword }` | Công khai | 200 "Đặt mật khẩu thành công." | 400 `PASSWORD_POLICY` · 422 `PASSWORD_LINK_INVALID` |
| GET | `/me` | 3 vai trò | 200 hồ sơ (vai trò, trường) | 401 `ACCOUNT_BLOCKED`/`FORBIDDEN` |
| PUT | `/me` `{ fullName, phone, avatarUrl }` | 3 vai trò | 200 "Lưu hồ sơ thành công." | 400 `REQUIRED`/`INVALID` |
| PUT | `/me/password` `{ currentPassword, newPassword }` | 3 vai trò | 200 access token mới | 400 `INVALID`/`PASSWORD_POLICY` |
| GET | `/admin/organizations?keyword&status&page&pageSize` | ADMIN | 200 trang trường (gói, số giáo viên, trạng thái) | 400 `INVALID` |
| POST | `/admin/organizations` `{ name, address, phone, email, orgAdmin{ fullName, username, email, phone } }` | ADMIN | 201 "Đã gửi email đặt mật khẩu tới …" | 400 · 409 `DUPLICATE` (`orgAdmin.email`, `orgAdmin.username`) |
| GET | `/admin/organizations/{id}` | ADMIN | 200 chi tiết + quản trị trường + gói + hạn mức | 404 |
| PUT | `/admin/organizations/{id}` `{ name, address, phone, email }` | ADMIN | 200 "Lưu thông tin trường thành công." | 404 · 422 `BLOCKED` (trường đã ngừng) |
| DELETE | `/admin/organizations/{id}` | ADMIN | 200 "Ngừng trường thành công." (xóa mềm) | 404 · 422 `BLOCKED` |
| POST | `/admin/organizations/{id}/org-admins` `{ fullName, username, email, phone }` | ADMIN | 201 "Đã gửi email đặt mật khẩu tới …" | 404 · 409 `DUPLICATE` |
| POST | `/admin/organizations/{id}/org-admins/{userId}/lock` · `/unlock` | ADMIN | 200 | 404 · 422 `BLOCKED` |
| GET | `/admin/audit-logs?userId&actor&actorRole&action&targetType&targetId&from&to&page&pageSize` | ADMIN | 200 trang nhật ký, mới nhất trước, có nhãn tiếng Việt | 400 `INVALID` |
| GET | `/org` | ORG_ADMIN | 200 trường của mình + gói + hạn mức giáo viên | — |
| PUT | `/org` `{ address, phone, email }` | ORG_ADMIN | 200 (tên trường chỉ ADMIN sửa) | 400 `INVALID` |
| GET | `/org/teachers?keyword&status&page&pageSize` | ORG_ADMIN | 200 trang giáo viên + `quota { used, max, planName, canAddTeacher }` | — |
| POST | `/org/teachers` `{ fullName, username, email, phone }` | ORG_ADMIN | 201 "Đã gửi email đặt mật khẩu tới …" | 409 `DUPLICATE` · 422 `TEACHER_LIMIT_REACHED` (đủ `max_teachers`) · 422 `BLOCKED` (không có gói hiệu lực) |
| GET | `/org/teachers/{id}` | ORG_ADMIN | 200 | 404 (kể cả giáo viên trường khác) |
| POST | `/org/teachers/{id}/lock` · `/unlock` | ORG_ADMIN | 200 | 404 · 422 `BLOCKED` |
| DELETE | `/org/teachers/{id}` | ORG_ADMIN | 200 (xóa mềm) | 404 |
| GET | `/health` (ngoài `/api/v1`) | Công khai | 200 `Healthy` | — |

Policy vai trò: `Authenticated` (3 vai trò), `Admin`, `OrgAdmin`, `Teacher` (dùng ở các luồng soạn bài). Sai vai trò → 403 `FORBIDDEN`;
không token hoặc token hết hạn → 401 `FORBIDDEN`.

### Hành vi chính

- **Đăng nhập**: có `@` thì tra email, không thì tra username (username không được chứa `@`); cả hai lưu chữ thường, so khớp không phân biệt hoa thường. Tài khoản đã xóa mềm coi như không tồn tại (`INVALID_CREDENTIALS`). Sai 5 lần trong 15 phút → tạm khóa 15 phút (403 `ACCOUNT_BLOCKED`, ghi `LOGIN_LOCKED_OUT`); bộ đếm theo **tài khoản** nên xen kẽ email/username không thêm lượt đoán. Đúng mật khẩu mới xét `LOCKED`, `INACTIVE`, trường đã ngừng (403 `ACCOUNT_BLOCKED` kèm lý do). Thành công: ghi `last_login_at` và `LOGIN`.
- **Chỉ access token**: hạn 8 giờ, không refresh token, không bảng phiên; đăng xuất = giao diện xóa token. Claim: `sub`, `preferred_username`, `name`, `role` (ADMIN | ORG_ADMIN | TEACHER), `org` (id trường, không có với ADMIN), `sst`, `jti`.
- **Kiểm mỗi yêu cầu**: tài khoản còn, chưa xóa mềm, ACTIVE, trường chưa ngừng, vai trò và trường khớp token, `sst` khớp mật khẩu hiện tại. Khóa tài khoản, ngừng trường, đổi mật khẩu có hiệu lực ngay ở yêu cầu kế tiếp (401).
- **Đặt mật khẩu qua email**: một bảng `password_reset_tokens`, hai nhánh. Tài khoản mới do ADMIN/ORG_ADMIN tạo mang băm của mật khẩu ngẫu nhiên không ai biết và nhận liên kết **48 giờ**; quên mật khẩu nhận liên kết **30 phút**, tối đa 3 yêu cầu/giờ/email, tài khoản bị chặn không nhận thư. Token 32 byte base64url, CSDL chỉ giữ SHA-256 hex chữ thường; dùng một lần; phát token mới, khóa, xóa mềm tài khoản hoặc ngừng trường đều vô hiệu token chưa dùng. Một endpoint `set-password` cho cả hai nhánh; đặt xong không tự đăng nhập.
- **Tạo trường**: một giao dịch — trường, đăng ký gói "Miễn phí" ACTIVE từ hôm nay (ngày Việt Nam), `end_date` = `start_date` + `duration_days` − 1, ORG_ADMIN đầu tiên, token 48 giờ, `ORG_CREATED` + `ACCOUNT_CREATED`. Trùng email/username → rollback toàn bộ, 409 báo dưới `orgAdmin.*`. Thư gửi sau khi commit.
- **Tạo giáo viên**: giao dịch có khóa dòng `organizations` (`UPDLOCK, HOLDLOCK`) → phải có gói hiệu lực (không có: 422 `BLOCKED`) → số TEACHER chưa xóa mềm (mọi trạng thái, không tính ORG_ADMIN) < `max_teachers` (đủ: 422 `TEACHER_LIMIT_REACHED`) → tạo tài khoản. Hai yêu cầu đồng thời không vượt hạn mức.
- **Khóa, mở khóa, xóa mềm**: ADMIN với ORG_ADMIN của một trường; ORG_ADMIN với TEACHER của trường mình. Ghi `ACCOUNT_LOCKED`, `ACCOUNT_UNLOCKED`, `ACCOUNT_DELETED` kèm giá trị trước/sau. Xóa mềm giữ dữ liệu; email, username dùng lại được (unique có lọc `deleted_at IS NULL`).
- **Tách dữ liệu theo trường**: ORG_ADMIN chỉ truy vấn qua `TeacherService.TeachersOf(organization_id của token)`; id giáo viên trường khác trả **404** như không tồn tại. ADMIN thấy mọi trường, kể cả trường đã ngừng.
- **Ngừng trường**: xóa mềm `organizations`; mọi tài khoản của trường bị chặn ngay (token cũ 401 `ACCOUNT_BLOCKED`, đăng nhập 403 `ACCOUNT_BLOCKED`), dữ liệu giữ nguyên; `ORG_DEACTIVATED`.

### Khuôn phản hồi

```json
{ "success": false, "data": null, "message": "Vui lòng nhập tên trường.", "errorCode": "REQUIRED",
  "traceId": "7b31…", "errors": { "name": "Vui lòng nhập tên trường.", "orgAdmin.username": "Tên đăng nhập không hợp lệ: …" } }
```

- `traceId` = header `X-Correlation-Id`. `errors` chỉ có khi lỗi theo trường; tên trường camelCase, trường lồng nhau nối bằng dấu chấm.
- JSON camelCase, enum dạng chuỗi viết HOA như CSDL (`"ORG_ADMIN"`, `"ACTIVE"`), thời gian ISO 8601 UTC có `Z`, ngày của gói dạng `yyyy-MM-dd` (ngày Việt Nam). Giao diện tự đổi sang nhãn và giờ Việt Nam; riêng nhật ký trả sẵn `actionLabel`, `actorRoleLabel`.
- Lỗi ngoài dự kiến trả 500 `SYSTEM_ERROR`, không lộ stack trace.

### Mã thông điệp (`errorCode`)

| Mã | Khi nào |
|---|---|
| `REQUIRED` | Trường bắt buộc bị bỏ trống |
| `INVALID` | Giá trị sai định dạng, vượt độ dài, JSON sai cú pháp |
| `PASSWORD_POLICY` | Mật khẩu không đạt 8-64 ký tự, có chữ hoa, chữ thường và chữ số |
| `DUPLICATE` | Email hoặc tên đăng nhập đã có tài khoản khác dùng |
| `INVALID_CREDENTIALS` | Sai thông tin đăng nhập |
| `ACCOUNT_BLOCKED` | Không đăng nhập được: tạm khóa do sai 5 lần · bị khóa · ngừng sử dụng · trường đã ngừng |
| `PASSWORD_LINK_INVALID` | Liên kết đặt mật khẩu hết hạn, đã dùng hoặc đã bị thay |
| `FORBIDDEN` | Ngoài phạm vi vai trò, chưa đăng nhập, phiên hết hạn |
| `BLOCKED` | Thao tác bị quy tắc chặn; cũng dùng cho 404 |
| `TEACHER_LIMIT_REACHED` | Trường đã đủ `max_teachers` |
| `SYSTEM_ERROR` | Lỗi ngoài dự kiến |

Các mã `FORGOT_PASSWORD_ACCEPTED`, `PASSWORD_LINK_SENT`, `SUCCESS` chỉ đi kèm câu thông báo thành công, không nằm trong `errorCode`.

## 3. Bảng của luồng (DBML v2, nhóm Auth / Tổ chức + `plans`, `subscriptions`)

Tên bảng, tên cột, tên khóa ngoại đúng DBML (`snake_case`, `fk_{bảng}_{cột}`). Thời gian `datetime2(3)` UTC, `created_at` mặc định `SYSUTCDATETIME()`. Enum `nvarchar(20)` + CHECK. CSDL collation `Vietnamese_CI_AI`.

| Bảng | Cột (kiểu theo DBML) | Ràng buộc |
|---|---|---|
| `organizations` | id bigint, name nvarchar(255) NOT NULL, address(500), phone(20), email(255), created_at, updated_at, deleted_at | PK |
| `roles` | id int, code nvarchar(50) NOT NULL, name nvarchar(100) NOT NULL | `UX_roles_code`; seed 1 ADMIN, 2 ORG_ADMIN, 3 TEACHER; `code` không CHECK |
| `users` | id bigint, organization_id NULL, role_id NOT NULL, username(100), email(255), password_hash(255), full_name(255) NOT NULL, phone(20), avatar_url(500), status, last_login_at, created_at, updated_at, deleted_at | `CK_users_status` (ACTIVE, LOCKED, INACTIVE); `UX_users_username`, `UX_users_email` **WHERE `deleted_at` IS NULL**; `fk_users_organization_id`, `fk_users_role_id` NO ACTION |
| `password_reset_tokens` | id bigint, user_id NOT NULL, token_hash nvarchar(255), expires_at, used_at, created_at | `UX_password_reset_tokens_token_hash`; `fk_password_reset_tokens_user_id` **CASCADE** (ngoại lệ duy nhất); không có `updated_at` |
| `audit_logs` | id bigint, created_at, user_id NULL, actor_role nvarchar(20), action nvarchar(50), target_type nvarchar(50) NOT NULL, target_id, old_value/new_value nvarchar(max), ip_address(45) | Chỉ thêm (DbContext chặn sửa/xóa); `actor_role`, `action` không CHECK; `IX_audit_logs_created_at`; FK NO ACTION |
| `plans` | id int, name(100), price decimal(12,2), duration_days, max_teachers, max_ai_requests, is_active bit DEFAULT 1 | Gói "Miễn phí" seed lúc khởi động theo môi trường |
| `subscriptions` | id bigint, organization_id, plan_id NOT NULL, start_date/end_date date NULL, status, created_at | `CK_subscriptions_status` đủ 4 giá trị PENDING, ACTIVE, EXPIRED, CANCELLED; FK NO ACTION |

**Nhật ký của luồng ghi 8/18 mã hành động**: `LOGIN`, `LOGIN_LOCKED_OUT`, `ORG_CREATED`, `ORG_DEACTIVATED`, `ACCOUNT_CREATED`, `ACCOUNT_LOCKED`, `ACCOUNT_UNLOCKED`, `ACCOUNT_DELETED`. 10 mã còn lại có sẵn trong enum `AuditAction` cho các luồng sau gọi qua cùng `IAuditLogger`. `old_value`/`new_value` là JSON rút gọn, không chứa mật khẩu, token.

## 4. Migration, seed, cấu hình

| Migration | Nội dung |
|---|---|
| `20260925184908_NenTangToChuc` | Migration đầu tiên của hướng mới: 7 bảng, chỉ mục, CHECK, seed 3 vai trò. Sẽ được thay bằng migration khởi tạo trên CSDL v3 |

Seed lúc khởi động (`DatabaseInitializer`, idempotent, chạy sau `Database:InitMode`):

| Seed | Cấu hình | Ghi chú |
|---|---|---|
| Gói "Miễn phí" giá 0 | `Seed:FreePlan:{DurationDays, MaxTeachers, MaxAiRequests}` | `appsettings.json` (UAT, Release 1.0): 30 ngày, 2 giáo viên, 20 lượt AI. `appsettings.Development.json`: 365, 10, 300 tới khi có chức năng quản lý gói. Đã có gói giá 0 thì không ghi đè |
| 4 tài khoản ADMIN | `Seed:Admins[]` `{ Username, Email, FullName, Password? }` + `Seed:AdminPassword` | Development khai 4 thành viên (`admin.nho`, `admin.lam`, `admin.trieu`, `admin.huy`); **mật khẩu chỉ đặt trong User Secrets hoặc biến môi trường**, thiếu thì bỏ qua và ghi cảnh báo |

| Khóa cấu hình | Mặc định | Ghi chú |
|---|---|---|
| `ConnectionStrings:Default` | Development: LocalDB `sep490_dsvhvn_v2_dev` | Không dùng `dsvhvn_dev`, `sep490_dsvhvn_dev` (CSDL hướng cũ) |
| `Database:InitMode` | `None`; Development `Migrate` | `EnsureCreated` chỉ cho kiểm thử |
| `Jwt:SigningKey` | trống (Development/Testing sinh khóa ngẫu nhiên mỗi lần chạy) | Môi trường khác bắt buộc, ≥ 32 byte |
| `Jwt:AccessTokenMinutes` | 480 | Access token 8 giờ |
| `App:FrontendBaseUrl`, `App:SetPasswordPath` | `http://localhost:5173`, `/dat-mat-khau` | Liên kết trong thư: `{FrontendBaseUrl}{SetPasswordPath}?token=…` (màn hình Đặt mật khẩu) |
| `Cors:AllowedOrigins` | `http://localhost:5173` | Chỉ mở cho origin của ứng dụng React |

Email: `IEmailSender` bản giả `ConsoleEmailSender` ghi nguyên thư (kể cả liên kết có token) ra log — chỉ dùng ở máy phát triển; gửi qua SMTP thật thuộc luồng sau.

## 5. Chạy và kiểm thử

```bash
dotnet tool restore
dotnet user-secrets set "Seed:AdminPassword" "<mật khẩu 8-64 ký tự, có chữ hoa, chữ thường, chữ số>" --project src/DSVHVN.Api
dotnet tool run dotnet-ef database update --project src/DSVHVN.Infrastructure --startup-project src/DSVHVN.Api
dotnet run --project src/DSVHVN.Api --launch-profile http > api.log 2>&1 &     # http://localhost:5080/swagger
dotnet test                                                                    # không cần LocalDB
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-nen-tang.sh
```

Kiểm thử tự động: **99 test** (78 unit, 21 integration). Unit chạy service thật trên SQLite trong bộ nhớ với đồng hồ tua được (hạn 30 phút/48 giờ, tạm khóa 15 phút, hết hạn gói, ngày Việt Nam). Integration chạy cả Api qua HTTP: ma trận 3 vai trò, tách dữ liệu hai trường (404), khóa có hiệu lực ngay, đổi mật khẩu vô hiệu token cũ, ngừng trường, nhật ký. Smoke test trên LocalDB: [smoke-nen-tang.md](smoke-nen-tang.md).

## 6. Quyết định hiện thực và điểm lệch cần biết

| # | Điểm | Cách làm | Lý do |
|---|---|---|---|
| 1 | `plans`, `subscriptions` nằm ở luồng nền tảng | Tạo 2 bảng mức tối thiểu + seed gói "Miễn phí" + `PlanGuard` | `max_teachers` và gán gói khi tạo trường cần có ngay từ đầu. Hóa đơn, thanh toán, màn hình quản lý gói vẫn ở luồng gói dịch vụ |
| 2 | NOT NULL | Bắt buộc: khóa ngoại nghiệp vụ, tên, email, username, băm mật khẩu, trạng thái, `created_at`; được NULL: liên hệ, ảnh, các mốc tùy chọn, `start_date`/`end_date` (đăng ký PENDING chưa có ngày) | DBML v2 để ngỏ NULL/NOT NULL; cột chốt theo migration |
| 3 | Username, email lưu chữ thường | Chuẩn hóa khi lưu và khi so | Quy tắc "so khớp không phân biệt hoa thường"; SQLite kiểm thử phân biệt hoa thường nên không dựa riêng vào collation |
| 4 | 404 không có câu riêng trong danh mục thông điệp | Dùng khuôn "thao tác bị chặn": "Không thể {thao tác}. Không tìm thấy {đối tượng}." | Không cho biết đối tượng có tồn tại ở trường khác; không tự thêm câu ngoài danh mục. **Đề nghị** thêm một câu riêng cho 404 vào danh mục |
| 5 | Chặn tạo giáo viên khi không có gói hiệu lực | 422 `BLOCKED` "Không thể thêm giáo viên. Trường chưa có gói dịch vụ còn hiệu lực." | Câu banner gói hết hạn cần `{ngay_het_han}`, không hợp làm lỗi API |
| 6 | Token cũ bị vô hiệu khi đổi mật khẩu | Claim `sst` = 16 ký tự hex SHA-256 của `password_hash`, đối chiếu mỗi yêu cầu | Không có refresh token nên không có danh sách phiên để thu hồi; không thêm cột |
| 7 | Khóa, xóa mềm, ngừng trường vô hiệu liên kết đặt mật khẩu chưa dùng | Ghi `used_at` | Mở khóa xong, người dùng lấy liên kết mới ở "Quên mật khẩu" |
| 8 | Chưa làm ở luồng này | Đếm lượt AI → khi có `ai_generations`; tác vụ nền ACTIVE → EXPIRED, "Kích hoạt gói Miễn phí", mua gói → luồng gói dịch vụ; SMTP thật; giao diện → repo frontend | Gói quá `end_date` vẫn bị coi là không hiệu lực vì `PlanGuard` so ngày trực tiếp |
| 9 | Thứ tự trường trong lỗi trùng | Kiểm email trước username | Mỗi lần báo một trường; giao diện hiện dưới đúng ô |
| 10 | `errorCode` đặt theo nghĩa | `REQUIRED`, `INVALID_CREDENTIALS`, `ACCOUNT_BLOCKED`… (bảng ở mục 2) | Giao diện xử lý theo mã, không theo câu chữ |
