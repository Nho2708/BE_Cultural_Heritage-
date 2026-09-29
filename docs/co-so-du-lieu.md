# Cơ sở dữ liệu v3 trong backend

Backend hiện thực **đủ 27 bảng** của CSDL v3 (`doc/database/dsvhvn-v3.dbml` của nhóm, 8 nhóm bảng) bằng EF Core 9 trên
SQL Server 2019. Lược đồ tạo bằng **một migration khởi tạo** `KhoiTaoCsdl`; mọi thay đổi về sau đi qua migration mới.

| Mục | Giá trị |
|---|---|
| CSDL phát triển | `sep490_dsvhvn_v3_dev` trên `(localdb)\MSSQLLocalDB`, collation `Vietnamese_CI_AI` (EF Core tạo database với collation của model) |
| Migration | `src/DSVHVN.Infrastructure/Persistence/Migrations/20260928164123_KhoiTaoCsdl.cs` |
| Số liệu | 27 bảng · 233 cột · 40 khóa ngoại · 19 CHECK · 4 UNIQUE có lọc · 10 UNIQUE thường · 15 kiểu enum (17 cột) |
| Dữ liệu khởi tạo trong migration | 4 vai trò: 1 ADMIN, 2 ORG_ADMIN, 3 TEACHER, 4 STUDENT |
| Dữ liệu nạp lúc khởi động | Gói "Miễn phí"; tài khoản quản trị hệ thống (mật khẩu từ User Secrets); 34 tỉnh/thành và di sản đợt 1 (chỉ Development) |

## 1. Quy ước ánh xạ

| Quy ước | Cách làm trong code |
|---|---|
| Tên | Bảng, cột `snake_case` đúng DBML (đổi tự động từ tên thuộc tính C#); khóa ngoại `fk_{bảng}_{cột}` như khối Ref của DBML; khóa chính `PK_{bảng}` |
| Kiểu | `nvarchar(n)` / `nvarchar(max)`; `datetime2(3)` lưu UTC (đọc ra gắn `Kind=Utc`); `date` cho ngày của gói; `decimal(p,s)` đúng DBML; `tinyint` cho khối lớp; `smallint` cho tháng, ngày của sự kiện |
| NULL / NOT NULL | Đúng DBML cho mọi cột. **32 khóa ngoại bắt buộc về nghiệp vụ là NOT NULL** (DBML ghi `not null` từ 29/09/2026, theo đặc tả mô hình dữ liệu của nhóm). 8 khóa ngoại được NULL: `users.organization_id`, `heritages.province_id`, `media.heritage_id`, `media.event_id`, `questions.ai_generation_id`, `questions.reviewed_by`, `attempt_answers.answer_id`, `audit_logs.user_id`. Thuộc tính C# nullable đúng theo cột |
| DEFAULT | Chỉ 8 cột `bit` có DEFAULT như DBML (`must_change_password`, `is_correct` ×2, `shuffle_questions`, `show_answers_after_submit` = 0; `students.is_active`, `plans.is_active`, `payment_providers.is_active` = 1). Ứng dụng luôn gửi giá trị khi thêm dòng. `created_at` không có DEFAULT (DBML không quy định): DbContext tự điền giờ UTC |
| Enum | `nvarchar(20)` + CHECK `CK_{bảng}_{cột}` liệt kê đúng giá trị; EF `HasConversion<string>()`. `roles.code`, `audit_logs.actor_role`, `audit_logs.action` là chuỗi không CHECK |
| CHECK khác | `CK_classes_grade` (khối 1-12), `CK_media_owner` (ảnh thuộc đúng một trong di sản hoặc sự kiện) |
| UNIQUE có lọc | `UX_users_username` WHERE `deleted_at IS NULL`; `UX_users_email` WHERE `email IS NOT NULL AND deleted_at IS NULL`; `UX_lessons_access_code` WHERE `access_code IS NOT NULL`; `UX_payments_provider_txn` (`provider_id`, `provider_transaction_id`) WHERE `provider_transaction_id IS NOT NULL` |
| UNIQUE thường | `roles.code`, `password_reset_tokens.token_hash`, `students.user_id`, `students.student_code`, `provinces.code`, `heritages.slug`, `invoices.invoice_number`, `payment_providers.code`, (`attempts.quiz_id`, `student_id`), (`attempt_answers.attempt_id`, `question_id`). Khai báo `HasFilter(null)` để SQL Server provider không tự thêm bộ lọc `IS NOT NULL` |
| Khóa ngoại | Mọi khóa ngoại `Restrict` (SQL Server: NO ACTION); ngoại lệ duy nhất `password_reset_tokens.user_id` Cascade như ghi chú của DBML |
| Chỉ mục thường | EF Core tạo chỉ mục trên cột khóa ngoại (34 chỉ mục) và `IX_audit_logs_created_at` cho màn hình Nhật ký thao tác. DBML không mô tả chỉ mục hiệu năng nên đây là khác biệt kỹ thuật |
| Xóa mềm | 8 bảng có `deleted_at`: `organizations`, `users`, `classes`, `students`, `heritages`, `lessons`, `questions`, `quizzes` |
| Nhật ký | `audit_logs` chỉ thêm: DbContext chặn sửa, xóa |

Thực thể theo nhóm: `Domain/Identity` (vai trò, tài khoản, token đặt mật khẩu), `Domain/Organizations`, `Domain/Audit`,
`Domain/Classes` (`SchoolClass`, `Student`), `Domain/Heritages` (`Province`, `Heritage`, `Timeline`, `TimelineEvent`, `Media`,
`HeritageReference`), `Domain/Lessons` (`Lesson`, `LessonHeritage`, `AiGeneration`), `Domain/Questions`, `Domain/Quizzes`
(`Quiz`, `QuizQuestion`, `Attempt`, `AttemptAnswer`), `Domain/Billing` (`Plan`, `Subscription`, `Invoice`, `PaymentProvider`,
`Payment`). Cấu hình EF Core ở `Infrastructure/Persistence/Configurations`, một tệp cho mỗi nhóm bảng.

## 2. Tạo CSDL phát triển

```bash
dotnet tool restore
dotnet user-secrets set "Seed:AdminPassword" "<mật khẩu 8-64 ký tự, có chữ hoa, chữ thường, chữ số>" --project src/DSVHVN.Api

# Cách 1: tạo bảng bằng dotnet-ef rồi nạp dữ liệu khởi tạo
dotnet tool run dotnet-ef database update --project src/DSVHVN.Infrastructure --startup-project src/DSVHVN.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/DSVHVN.Api --no-launch-profile -- --chi-khoi-tao-csdl

# Cách 2: chạy Api bình thường (Development tự áp migration và nạp dữ liệu khi khởi động)
dotnet run --project src/DSVHVN.Api --launch-profile http
```

`--chi-khoi-tao-csdl`: áp migration, nạp dữ liệu khởi tạo rồi thoát, không mở cổng HTTP. Mọi bước nạp đều chạy lại được
(không nhân đôi, không ghi đè). `dotnet-ef` lấy chuỗi kết nối từ biến môi trường `ConnectionStrings__Default`, không có thì
dùng `sep490_dsvhvn_v3_dev` trên LocalDB.

### Dữ liệu tỉnh/thành và di sản

Khóa cấu hình `Seed:HeritageData:ProvincesFile` và `Seed:HeritageData:HeritagesFile` (đường dẫn tương đối theo thư mục
`src/DSVHVN.Api`). `appsettings.Development.json` trỏ tới `../../../doc/du-lieu-di-san/provinces.json` và
`../../../doc/du-lieu-di-san/seed-dot-1.json` (thư mục dữ liệu của nhóm nằm cạnh repo). Máy không có thư mục này thì đặt lại
đường dẫn bằng User Secrets hoặc biến môi trường `Seed__HeritageData__ProvincesFile`; không thấy tệp thì bỏ qua và ghi cảnh báo.

- Tỉnh đã có mã thì bỏ qua; di sản đã có slug thì bỏ qua cả di sản (kèm giai đoạn, sự kiện, ảnh, nguồn).
- Dữ liệu không vừa cột (chuỗi quá độ dài, số thập phân không vừa `decimal(p,s)`, giá trị enum lạ, sự kiện thiếu năm) thì
  **không ép**: bỏ qua di sản đó và ghi lý do vào log.
- Trường của tệp không có cột tương ứng được liệt kê trong log và không lưu: `key`, `map_point_note`.

Kết quả nạp ngày 28/09/2026 vào `sep490_dsvhvn_v3_dev`: 34 tỉnh/thành, 20 di sản (PUBLISHED), 37 giai đoạn, 158 sự kiện,
64 ảnh (đều gắn với di sản), 110 nguồn tham chiếu; 0 di sản bị bỏ qua. Kèm 4 vai trò, 4 tài khoản quản trị hệ thống,
gói "Miễn phí" (Development: 365 ngày, 10 giáo viên, 300 lượt AI).

Tìm kiếm không dấu: collation `Vietnamese_CI_AI` bỏ qua dấu thanh ("hoang thanh" tìm ra "Hoàng thành") nhưng coi ă, â, ê, ô,
ơ, ư, đ là chữ riêng ("thang long" không tìm ra "Thăng Long"). Chức năng tìm kiếm di sản cần tự chuẩn hóa chuỗi nếu muốn tìm
được cả trường hợp này.

## 3. Đối chiếu với DBML

Cách đối chiếu (script của nhóm, ngoài repo): tạo một database tạm từ DBML bằng `dbml2sql` (vá bốn lỗi đã biết: cột enum
`nvarchar(255)` → `nvarchar(20)`, tiền tố `N` cho mô tả, bộ lọc của 4 UNIQUE, `QUOTED_IDENTIFIER`), so cấu trúc với database
do migration tạo (bảng; cột: kiểu, độ dài, NULL, identity, DEFAULT, collation, thứ tự; khóa chính; khóa ngoại và ON DELETE;
CHECK; UNIQUE và bộ lọc; chỉ mục thường; collation), rồi xóa database tạm.

| Chỉ số | Database tạo từ DBML | `sep490_dsvhvn_v3_dev` |
|---|---:|---:|
| Bảng | 27 | 27 |
| Cột | 233 | 233 |
| Khóa ngoại | 40 | 40 |
| CHECK | 19 | 19 |
| UNIQUE có lọc | 4 | 4 |
| UNIQUE thường | 10 | 10 |
| Chỉ mục thường | 0 | 35 |
| Collation | Vietnamese_CI_AI | Vietnamese_CI_AI |

Khác biệt cấu trúc ngoài dự kiến: **0**. Kiểu, độ dài, DEFAULT, biểu thức CHECK, bộ lọc UNIQUE, thứ tự cột, khóa chính đều khớp.

Khác biệt có chủ đích: **0** (so lại ngày 29/09/2026). Trước đó có 29 khác biệt: 28 cột khóa ngoại bắt buộc để ngỏ NULL ở DBML
và `password_reset_tokens.user_id` chỉ ghi Cascade bằng chữ. DBML nay khai báo `not null` cho 28 cột đó và `[delete: cascade]`
cho khóa ngoại này, nên database tạo từ DBML và database do migration tạo khớp nhau về cấu trúc.

Khác biệt kỹ thuật, không đổi ý nghĩa lược đồ: tên khóa chính, tên CHECK của cột enum và tên UNIQUE (DBML để SQL Server tự
đặt tên, migration đặt `PK_`, `CK_`, `UX_`); 8 UNIQUE một cột khai báo bằng constraint ở DBML, bằng unique index ở migration;
35 chỉ mục thường chỉ có ở migration (34 chỉ mục trên cột khóa ngoại + `IX_audit_logs_created_at`).

## 4. Kiểm thử trên SQL Server

Ngoài bộ test trên SQLite trong bộ nhớ, lớp `SqlServerDatabaseTests` chạy trên **database tạm của LocalDB** (tên
`dsvhvn_kiem_thu_…`): Api khởi động áp migration khởi tạo và nạp dữ liệu, test kiểm lược đồ (27 bảng, 233 cột, 40 khóa ngoại,
một Cascade, 19 CHECK, 4 UNIQUE có lọc đúng điều kiện, 8 khóa ngoại được NULL, 17 cột enum `nvarchar(20)`, DEFAULT của cột bit,
collation), dữ liệu di sản, luồng web quản trị qua HTTP và các ràng buộc của tài khoản học sinh, bài học, thanh toán; xong thì
xóa database tạm. Máy không có LocalDB thì các test này được bỏ qua (Skip); không có thư mục dữ liệu di sản thì bỏ qua riêng
test nạp di sản.
