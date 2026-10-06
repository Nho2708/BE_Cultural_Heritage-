# Cơ sở dữ liệu trong backend (v3 và phần v4 đã làm theo từng luồng)

Backend hiện thực **đủ 27 bảng** của CSDL v3 (`doc/database/dsvhvn-v3.dbml` của nhóm, 8 nhóm bảng) bằng EF Core 9 trên
SQL Server 2019. Lược đồ tạo bằng **một migration khởi tạo** `KhoiTaoCsdl`; mọi thay đổi về sau đi qua migration mới. Từ luồng
"Bài học cho lớp theo cấp" backend thêm dần phần **CSDL v4** (`doc/database/dsvhvn-v4.dbml`) đúng phần luồng cần, mỗi luồng một migration
(mục 5 và 6): hiện có bảng `lesson_sections`, các cột mới của `ai_generations` (luồng bài học) và các cột mới của `questions`, `answers` cho 8 dạng câu (luồng ngân hàng câu hỏi) và bảng `attempt_answer_parts` cùng hai cột mới của `attempt_answers` (luồng học tập của học sinh), tổng **29 bảng**.

| Mục | Giá trị |
|---|---|
| CSDL phát triển | `sep490_dsvhvn_v3_dev` trên `(localdb)\MSSQLLocalDB`, collation `Vietnamese_CI_AI` (EF Core tạo database với collation của model) |
| Migration | `src/DSVHVN.Infrastructure/Persistence/Migrations/20260928164123_KhoiTaoCsdl.cs` (lược đồ); `20260929173018_ChuanHoaLoaiNguonThamChieu.cs` (chỉ sửa dữ liệu, xem cuối mục 2); `20260929182919_ThemCotTimKiemKhongDauChoDiSan.cs` (thêm 2 cột tìm kiếm, xem mục 3); `20261005200057_ThemPhanNoiDungBaiHocTheoCap.cs` (phần v4 của luồng bài học, xem mục 5); `20261005212316_ThemDangCauHoiVaCapPhuHop.cs` (phần v4 của luồng ngân hàng câu hỏi, xem mục 6) |
| Số liệu | **29 bảng** (27 của v3 + `lesson_sections` + `attempt_answer_parts`) · 233 cột theo DBML v3 + 2 cột tìm kiếm + 13 cột của luồng bài học (12 của `lesson_sections`, 1 của `ai_generations`) + 9 cột của luồng ngân hàng câu hỏi (5 của `questions`, 4 của `answers`) + 11 cột của luồng học tập (9 của `attempt_answer_parts`, 2 của `attempt_answers`) = **268 trong database** · 46 khóa ngoại · 27 CHECK · 6 UNIQUE có lọc · 10 UNIQUE thường · 16 kiểu enum (18 cột) |
| Dữ liệu khởi tạo trong migration khởi tạo | 4 vai trò: 1 ADMIN, 2 ORG_ADMIN, 3 TEACHER, 4 STUDENT |
| Dữ liệu nạp lúc khởi động | Gói "Miễn phí"; tài khoản quản trị hệ thống (mật khẩu từ User Secrets); 34 tỉnh/thành và di sản đợt 1 (chỉ Development) |

## 1. Quy ước ánh xạ

| Quy ước | Cách làm trong code |
|---|---|
| Tên | Bảng, cột `snake_case` đúng DBML (đổi tự động từ tên thuộc tính C#); khóa ngoại `fk_{bảng}_{cột}` như khối Ref của DBML; khóa chính `PK_{bảng}` |
| Kiểu | `nvarchar(n)` / `nvarchar(max)`; `datetime2(3)` lưu UTC (đọc ra gắn `Kind=Utc`); `date` cho ngày của gói; `decimal(p,s)` đúng DBML; `tinyint` cho khối lớp; `smallint` cho tháng, ngày của sự kiện |
| NULL / NOT NULL | Đúng DBML cho mọi cột. **32 khóa ngoại bắt buộc về nghiệp vụ là NOT NULL** (DBML ghi `not null` từ 29/09/2026, theo đặc tả mô hình dữ liệu của nhóm). 13 khóa ngoại được NULL ở bản hiện tại (danh sách đầy đủ trong `SqlServerDatabaseTests`; 8 của bản đầu là): `users.organization_id`, `heritages.province_id`, `media.heritage_id`, `media.event_id`, `questions.ai_generation_id`, `questions.reviewed_by`, `attempt_answers.answer_id`, `audit_logs.user_id`. Thuộc tính C# nullable đúng theo cột |
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

Từ luồng Kho di sản, lần nạp **luôn ghi di sản ở DRAFT** (bỏ qua `status` trong tệp) và quy đổi loại nguồn `GOV_WEBSITE`, `UNESCO` về `WEBSITE`
(`source_type` chỉ nhận `GOV_DOC`, `WEBSITE`, `BOOK`); di sản chỉ lên PUBLISHED qua chức năng xuất bản của quản trị hệ thống
([kho-di-san.md](kho-di-san.md)). Migration `ChuanHoaLoaiNguonThamChieu` (chỉ sửa dữ liệu, không đổi lược đồ) đưa hai loại nguồn cũ về `WEBSITE`
ở database đã nạp từ trước.

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

## 3. Đối chiếu với DBML (phần v3)

Bảng so sánh dưới đây là của thiết kế v3 (27 bảng); phần v4 thêm ở mục 5.

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

**Hai cột thêm ngoài DBML (29/09/2026, luồng Khám phá di sản công khai)**: `heritages.search_name nvarchar(260)` và
`heritages.search_text nvarchar(max)`, cùng NULL được, do migration `ThemCotTimKiemKhongDauChoDiSan` thêm. Lý do: collation
`Vietnamese_CI_AI` chỉ bỏ dấu thanh, còn coi ă, â, ê, ô, ơ, ư, đ là chữ riêng nên "thang long" không tìm ra "Thăng Long". Hai cột
lưu tên (và tên, mô tả ngắn, địa chỉ, nội dung) đã chuẩn hóa thành chữ thường không dấu, do ứng dụng tự tính mỗi khi lưu di sản
và điền cho dữ liệu cũ lúc khởi động; không nhập tay, không trả ra API. Bảng so sánh trên là của thiết kế DBML (233 cột); DBML cần
thêm hai cột này để khớp lại (việc của nhóm cơ sở dữ liệu, không sửa trong repo này). Chi tiết: [kham-pha-di-san.md](kham-pha-di-san.md).

Khác biệt kỹ thuật, không đổi ý nghĩa lược đồ: tên khóa chính, tên CHECK của cột enum và tên UNIQUE (DBML để SQL Server tự
đặt tên, migration đặt `PK_`, `CK_`, `UX_`); 8 UNIQUE một cột khai báo bằng constraint ở DBML, bằng unique index ở migration;
35 chỉ mục thường chỉ có ở migration (34 chỉ mục trên cột khóa ngoại + `IX_audit_logs_created_at`).

## 4. Kiểm thử trên SQL Server

Ngoài bộ test trên SQLite trong bộ nhớ, lớp `SqlServerDatabaseTests` chạy trên **database tạm của LocalDB** (tên
`dsvhvn_kiem_thu_…`): Api khởi động áp migration khởi tạo và nạp dữ liệu, test kiểm lược đồ (29 bảng, 268 cột, 46 khóa ngoại,
một Cascade, 27 CHECK, 6 UNIQUE có lọc đúng điều kiện, 13 khóa ngoại được NULL, 18 cột enum `nvarchar(20)`, DEFAULT của cột bit,
collation), dữ liệu di sản, luồng web quản trị qua HTTP và các ràng buộc của tài khoản học sinh, bài học, thanh toán; xong thì
xóa database tạm. Máy không có LocalDB thì các test này được bỏ qua (Skip); không có thư mục dữ liệu di sản thì bỏ qua riêng
test nạp di sản.

## 5. Phần CSDL v4 của luồng "Bài học cho lớp theo cấp"

Migration `ThemPhanNoiDungBaiHocTheoCap` (05/10/2026) đưa vào đúng phần của `dsvhvn-v4.dbml` mà luồng cần; các bảng và cột v4 khác (bản dịch,
khách tham quan, quảng cáo, dạng câu hỏi mới...) thuộc các luồng sau và sẽ có migration riêng.

| Thay đổi | Chi tiết |
|---|---|
| Bảng mới `lesson_sections` | Các phần nội dung của bài theo khung của cấp: `id`, `lesson_id` (FK), `section_type` (`STORY`, `IMAGE`, `READING`, `MILESTONE`, `SOURCE_EXCERPT`, `REFLECTION`; `nvarchar(20)` + CHECK), `title`, `body`, `image_url`, `milestone_year`, `event_id` (FK tới `events`, được NULL), `source_citation`, `sort_order`, `created_at`, `updated_at`. Chỉ mục `IX_lesson_sections_lesson_order` (`lesson_id`, `sort_order`). CHECK `CK_lesson_sections_required`: mốc phải có năm, đoạn trích phải có nguồn, hình phải có đường dẫn. Loại phần dùng được theo cấp của lớp kiểm ở tầng code (`LessonFrames`) |
| `ai_generations.lesson_id` | Từ NOT NULL thành được NULL (dịch, tóm tắt di sản không thuộc bài học) |
| `ai_generations.heritage_id` | Cột mới (FK tới `heritages`, được NULL): di sản được dịch hoặc tóm tắt |
| `ai_generations.task_type` | Thêm `HERITAGE_TRANSLATION`, `HERITAGE_SUMMARY` vào CHECK `CK_ai_generations_task_type` (giữ `LESSON_SUMMARY`, `QUIZ_GENERATION`); `AiTaskType` thêm hai giá trị tương ứng |
| CHECK mới `CK_ai_generations_subject` | Mỗi dòng thuộc đúng một bài học (tác vụ `LESSON_SUMMARY`, `QUIZ_GENERATION`) hoặc đúng một di sản (`HERITAGE_TRANSLATION`, `HERITAGE_SUMMARY`). Dòng cũ (đều có bài học) thỏa CHECK mới |
| Không đổi | `lessons` (đã có `class_id`, `access_code` + `UX_lessons_access_code` từ v3), `lesson_heritages`, `quizzes`, `plans`. Cột `lessons.summary` giữ nghĩa "nội dung chính của bài" |

Số liệu sau migration trên SQL Server (kiểm bằng `SqlServerDatabaseTests`): 28 bảng, 248 cột, 43 khóa ngoại (thêm `fk_lesson_sections_lesson_id`,
`fk_lesson_sections_event_id`, `fk_ai_generations_heritage_id`), 22 CHECK (thêm hai của `lesson_sections` và `CK_ai_generations_subject`),
11 khóa ngoại được NULL (thêm `ai_generations.heritage_id`, `ai_generations.lesson_id`, `lesson_sections.event_id`). Chi tiết luồng:
[bai-hoc-theo-cap.md](bai-hoc-theo-cap.md).

## 6. Phần CSDL v4 của luồng "Ngân hàng câu hỏi theo dạng"

Migration `ThemDangCauHoiVaCapPhuHop` (05/10/2026) đưa vào phần của `dsvhvn-v4.dbml` cho 8 dạng câu hỏi và cấp phù hợp; không thêm bảng.

| Thay đổi | Chi tiết |
|---|---|
| `questions` | Thêm `image_url`, `passage`, `passage_source`, `level_min` (NOT NULL, DEFAULT 1), `level_max` (NOT NULL, DEFAULT 3); CHECK `CK_questions_levels`, `CK_questions_passage`; `CK_questions_question_type` nhận đủ 8 dạng |
| `answers` | Thêm `image_url`, `match_content`, `correct_position`, `blank_index`; CHECK `CK_answers_positions` (1..20) |
| Câu cũ | `SINGLE_CHOICE` → cấp 2-3; `TRUE_FALSE` → cấp 2 |
| Chưa làm | `attempt_answers.parts_total`, `parts_correct`, bảng `attempt_answer_parts`: đã làm ở luồng học tập (mục 8) |

Số liệu sau migration: 28 bảng, 257 cột, 43 khóa ngoại, 25 CHECK, 11 khóa ngoại được NULL, DEFAULT thêm hai cột `level_min`, `level_max`. Chi tiết luồng và đường nâng cấp câu cũ:
[ngan-hang-cau-hoi.md](ngan-hang-cau-hoi.md) mục 6.

## 7. Luồng "Bài kiểm tra của bài học": không thêm migration

Luồng này dùng `quizzes`, `quiz_questions` của v3 **nguyên trạng**: `dsvhvn-v4.dbml` không đổi hai bảng này (mọi cột và ràng buộc cần cho soạn, phát hành, đóng đã có: `status` DRAFT/PUBLISHED/CLOSED có CHECK, `total_point decimal(6,2)`, `point decimal(5,2)`, khóa chính kép, độ khó, giờ mở, giờ đóng, thời gian làm, hai cờ xáo câu và hiện đáp án). Độ khó của bài học **không lưu**, tính khi truy vấn. Thời gian làm theo cấp, 1-50 câu, điểm bước 0,25 là quy tắc ở tầng code, không có CHECK trong CSDL (đúng ghi chú DBML). `attempts` và `attempt_answers` chỉ được luồng này cập nhật khi đóng bài (tự nộp lượt đang dở); phần v4 của luồng làm bài vẫn chưa thêm. Chi tiết: [bai-kiem-tra.md](bai-kiem-tra.md).

## 8. Luồng "Học tập của học sinh theo cấp": migration `ThemCauTraLoiTungPhan`

Thêm phần v4 của việc làm bài, đúng `dsvhvn-v4.dbml`:

| Thay đổi | Chi tiết |
|---|---|
| `attempt_answers` | Thêm `parts_total smallint NOT NULL DEFAULT 1`, `parts_correct smallint NOT NULL DEFAULT 0`; CHECK `CK_attempt_answers_parts` (`parts_total >= 1 AND parts_correct BETWEEN 0 AND parts_total`) |
| `attempt_answer_parts` (bảng mới, 9 cột) | `id`, `attempt_answer_id`, `answer_id` (NULL), `blank_index tinyint` (NULL), `selected_answer_id` (NULL), `selected_position tinyint` (NULL), `selected_bool bit` (NULL), `response_text nvarchar(255)` (NULL), `is_correct bit NOT NULL DEFAULT 0`; CHECK `CK_attempt_answer_parts_target` (đúng một trong `answer_id`, `blank_index`); UNIQUE có lọc `UX_attempt_answer_parts_answer` (`attempt_answer_id`, `answer_id`) khi `answer_id` không NULL và `UX_attempt_answer_parts_blank` (`attempt_answer_id`, `blank_index`) khi `blank_index` không NULL; 3 khóa ngoại (đều Restrict) |

Dòng cũ của `attempt_answers` (từ luồng đóng bài) lấy giá trị mặc định (1 phần, 0 phần đúng). Số liệu sau migration: 29 bảng, 268 cột, 46 khóa ngoại, 27 CHECK, 13 khóa ngoại được NULL, 6 UNIQUE có lọc, thêm ba DEFAULT
(`is_correct` của bảng mới, `parts_total`, `parts_correct`). Chi tiết: [hoc-tap-theo-cap.md](hoc-tap-theo-cap.md).
