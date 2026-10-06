# Cơ sở dữ liệu trong backend (v3 và phần v4 đã làm theo từng luồng)

Backend hiện thực **đủ 27 bảng** của CSDL v3 (`doc/database/dsvhvn-v3.dbml` của nhóm, 8 nhóm bảng) bằng EF Core 9 trên
SQL Server 2019. Lược đồ tạo bằng **một migration khởi tạo** `KhoiTaoCsdl`; mọi thay đổi về sau đi qua migration mới. Từ luồng
"Bài học cho lớp theo cấp" backend thêm dần phần **CSDL v4** (`doc/database/dsvhvn-v4.dbml`) đúng phần luồng cần, mỗi luồng một migration
(mục 5 đến 11): hiện có bảng `lesson_sections`, các cột mới của `ai_generations` (luồng bài học), các cột mới của `questions`, `answers` cho 8 dạng câu (luồng ngân hàng câu hỏi), năm bảng đa ngôn ngữ (luồng đa ngôn ngữ), bảng `heritage_models` (luồng mô hình 3D), bảng `attempt_answer_parts` cùng hai cột mới của `attempt_answers` (luồng học tập của học sinh), hai bảng `guest_devices`, `guest_activities` (luồng khách tham quan, mục 13), bảng `ad_unlocks` (luồng quảng cáo Google có thưởng, mục 14), tổng **38 bảng**.

| Mục | Giá trị |
|---|---|
| CSDL phát triển | `sep490_dsvhvn_v3_dev` trên `(localdb)\MSSQLLocalDB`, collation `Vietnamese_CI_AI` (EF Core tạo database với collation của model) |
| Migration | `src/DSVHVN.Infrastructure/Persistence/Migrations/20260928164123_KhoiTaoCsdl.cs` (lược đồ); `20260929173018_ChuanHoaLoaiNguonThamChieu.cs` (chỉ sửa dữ liệu, xem cuối mục 2); `20260929182919_ThemCotTimKiemKhongDauChoDiSan.cs` (thêm 2 cột tìm kiếm, xem mục 3); `20261005200057_ThemPhanNoiDungBaiHocTheoCap.cs` (phần v4 của luồng bài học, xem mục 5); `20261005212316_ThemDangCauHoiVaCapPhuHop.cs` (phần v4 của luồng ngân hàng câu hỏi, xem mục 6); `20261006144217_ThemBanDichVaTomTatDiSan.cs` (năm bảng đa ngôn ngữ, xem mục 7); `20261006150550_ThemCauTraLoiTungPhan.cs` (phần v4 của luồng học tập, xem mục 11); `20261006162139_ThemMoHinh3DCuaDiSan.cs` (bảng mô hình 3D, xem mục 8); `20261006172743_ThemRangBuocGoiVaThanhToan.cs` (chỉ mục của nhóm Thanh toán, xem mục 9); `20261006193419_ThemKhachThamQuanVaLichSu.cs` (hai bảng của khách tham quan, xem mục 13); `20261006205730_ThemLuotMoKhoaQuangCao.cs` (bảng lượt mở khóa quảng cáo, xem mục 14) |
| Số liệu | **38 bảng** (27 của v3 + `lesson_sections` + 5 bảng đa ngôn ngữ + `heritage_models` + `attempt_answer_parts` + `guest_devices` + `guest_activities` + `ad_unlocks`) · 233 cột theo DBML v3 + 2 cột tìm kiếm + 13 cột của luồng bài học (12 của `lesson_sections`, 1 của `ai_generations`) + 9 cột của luồng ngân hàng câu hỏi (5 của `questions`, 4 của `answers`) + 46 cột của năm bảng đa ngôn ngữ + 17 cột của `heritage_models` + 11 cột của luồng học tập (9 của `attempt_answer_parts`, 2 của `attempt_answers`) + 14 cột của hai bảng khách tham quan (7 + 7) + 9 cột của `ad_unlocks` = **354 trong database** · 60 khóa ngoại (4 Cascade) · 49 CHECK · 10 UNIQUE có lọc · 17 UNIQUE thường · 22 kiểu enum (33 cột) |
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
`dsvhvn_tam_a2_…`): Api khởi động áp migration khởi tạo và nạp dữ liệu, test kiểm lược đồ (38 bảng, 354 cột, 60 khóa ngoại,
bốn Cascade, 49 CHECK, 10 UNIQUE có lọc đúng điều kiện, 18 khóa ngoại được NULL, 33 cột enum `nvarchar(20)`, DEFAULT của cột bit và của ngôn ngữ thiết bị,
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
| Chưa làm | `attempt_answers.parts_total`, `parts_correct`, bảng `attempt_answer_parts`: đã làm ở luồng học tập (mục 11) |

Số liệu sau migration: 28 bảng, 257 cột, 43 khóa ngoại, 25 CHECK, 11 khóa ngoại được NULL, DEFAULT thêm hai cột `level_min`, `level_max`. Chi tiết luồng và đường nâng cấp câu cũ:
[ngan-hang-cau-hoi.md](ngan-hang-cau-hoi.md) mục 6.

## 7. Phần CSDL v4 của luồng "Đa ngôn ngữ, AI dịch và tóm tắt di sản"

Migration `ThemBanDichVaTomTatDiSan` (06/10/2026) thêm năm bảng của nhóm Đa ngôn ngữ trong `dsvhvn-v4.dbml`; không sửa bảng cũ. `ai_generations` đã có `heritage_id`, hai loại tác vụ di sản và CHECK `CK_ai_generations_subject`
từ migration của luồng bài học nên không đổi.

| Bảng mới | Cột | Ràng buộc |
|---|---|---|
| `heritage_translations` | `id`, `heritage_id`, `language_code`, `name`, `short_description`, `content`, `address`, `status`, `is_outdated` (DEFAULT 0), `ai_generation_id`, `reviewed_by`, `reviewed_at`, `created_at`, `updated_at` | UNIQUE `UX_heritage_translations_heritage_language`; CHECK `CK_heritage_translations_language_code` (5 mã), `CK_heritage_translations_status` (`en`: `PENDING`, `APPROVED`, `REJECTED`; `ko`, `zh`, `ja`: `MACHINE`; không có `vi`) |
| `timeline_translations` | `id`, `timeline_id`, `language_code`, `name`, `description`, `created_at`, `updated_at` | UNIQUE (`timeline_id`, `language_code`); CHECK `language_code <> 'vi'`; khóa ngoại **Cascade** |
| `event_translations` | như trên, `event_id`, `name`, `description` | như trên, Cascade theo `events` |
| `media_translations` | `id`, `media_id`, `language_code`, `caption`, `created_at`, `updated_at` | UNIQUE (`media_id`, `language_code`); CHECK `language_code <> 'vi'`; Cascade theo `media` |
| `heritage_summaries` | `id`, `heritage_id`, `language_code`, `short_text` (2000), `detailed_text`, `status`, `is_outdated` (DEFAULT 0), `ai_generation_id`, `reviewed_by`, `reviewed_at`, `created_at`, `updated_at` | UNIQUE `UX_heritage_summaries_heritage_language`; CHECK `CK_heritage_summaries_status` (`vi`, `en`: `PENDING`, `APPROVED`, `REJECTED`; `ko`, `zh`, `ja`: `MACHINE`) |

- `language_code` lưu chữ thường (`vi`, `en`, `ko`, `zh`, `ja`; tên thành viên của enum `LanguageCode` cũng viết thường, khác các enum còn lại), `status` lưu `MACHINE`, `PENDING`, `APPROVED`, `REJECTED`; cả hai là `nvarchar(20)`.
  CHECK trạng thái theo ngôn ngữ đã bao luôn danh sách giá trị của enum trạng thái nên không thêm CHECK riêng cho `status`.
- Khóa ngoại tên `fk_{bảng}_{cột}` như DBML; `ai_generation_id`, `reviewed_by` được NULL (bản nhập tay, chưa duyệt, bản dịch máy). Chỉ ba bảng con Cascade; mọi khóa ngoại khác Restrict.
- Nhật ký thao tác thêm hai mã `TRANSLATION_APPROVED`, `TRANSLATION_REJECTED` (cột `audit_logs.action` không có CHECK nên không cần migration) và hai tên đối tượng `heritage_translations`, `heritage_summaries`.

Số liệu sau migration: 33 bảng, 303 cột (thêm 46), 52 khóa ngoại (thêm 9, trong đó 3 Cascade), 35 CHECK (thêm 10), 15 UNIQUE thường (thêm 5), 15 khóa ngoại được NULL (thêm 4), 25 cột enum (thêm 7), hai DEFAULT bit.
Chi tiết luồng: [da-ngon-ngu.md](da-ngon-ngu.md).

## 8. Phần CSDL v4 của luồng "Mô hình 3D di sản"

Migration `ThemMoHinh3DCuaDiSan` (06/10/2026) thêm bảng `heritage_models` của nhóm Đa ngôn ngữ và 3D trong `dsvhvn-v4.dbml`; không sửa bảng cũ.

| Bảng mới | Cột | Ràng buộc |
|---|---|---|
| `heritage_models` | `id`, `heritage_id`, `title` (255), `file_url` (500), `file_format`, `file_size_bytes`, `poster_url` (500), `license_name` (100), `license_url` (500), `author` (255), `source_url` (500), `credit` (500), `status`, `sort_order`, `created_at`, `updated_at`, `deleted_at` | CHECK `CK_heritage_models_file_format` (`GLB`), `CK_heritage_models_status` (`DRAFT`, `PUBLISHED`, `ARCHIVED`), `CK_heritage_models_file_size` (`> 0`); UNIQUE có lọc `UX_heritage_models_visible` (`heritage_id` khi `status = 'PUBLISHED'` và `deleted_at IS NULL`: một mô hình hiển thị mỗi di sản); chỉ mục `IX_heritage_models_heritage_status`; khóa ngoại `fk_heritage_models_heritage_id` **Restrict** |

- `created_at` NOT NULL như các bảng đa ngôn ngữ (ứng dụng luôn điền); `deleted_at` là xóa mềm (gỡ mô hình), bảng thứ chín có `deleted_at`.
- Nhật ký thao tác thêm mã `HERITAGE_MODEL_CHANGED` (không cần migration) và tên đối tượng `heritage_models`.

Số liệu sau migration: 34 bảng, 320 cột (thêm 17), 53 khóa ngoại (thêm 1, Restrict), 38 CHECK (thêm 3), 5 UNIQUE có lọc (thêm 1), 15 UNIQUE thường, 27 cột enum (thêm 2), hai DEFAULT bit.
Chi tiết luồng: [mo-hinh-3d.md](mo-hinh-3d.md).

## 9. Phần CSDL của luồng "Gói và thanh toán VNPay"

Migration `ThemRangBuocGoiVaThanhToan` (06/10/2026) **không thêm bảng và cột** (năm bảng `plans`, `subscriptions`, `invoices`, `payment_providers`, `payments` có từ CSDL v3); chỉ thêm chỉ mục để chính CSDL giữ các quy tắc:

| Chỉ mục | Bảo vệ |
|---|---|
| `UX_plans_name` (duy nhất, lọc `name IS NOT NULL`) | tên gói không trùng, không phân biệt hoa thường và dấu thanh |
| `UX_subscriptions_one_pending` (duy nhất trên `organization_id`, lọc `status = 'PENDING'`) | mỗi trường tối đa một yêu cầu mua chưa thanh toán |
| `UX_payments_one_pending_per_invoice` (duy nhất trên `invoice_id`, lọc `status = 'PENDING'`) | mỗi hóa đơn tối đa một giao dịch chờ |
| `IX_subscriptions_organization_status`, `IX_payments_status_created_at`, `IX_invoices_status_due_at` | tra gói của trường; tác vụ nền tìm giao dịch treo và hóa đơn quá hạn |

Bỏ hai chỉ mục thường `IX_subscriptions_organization_id` và `IX_payments_invoice_id` (chỉ mục mới đã bao trùm cột đầu). Dữ liệu khởi tạo thêm bốn dòng `payment_providers` (chỉ `VNPAY` bật) và, ở môi trường phát triển, hai gói mẫu.
Số liệu sau migration: 34 bảng, 320 cột, 53 khóa ngoại, 38 CHECK, **8 UNIQUE có lọc** (thêm 3), 15 UNIQUE thường. Chi tiết: [goi-thanh-toan.md](goi-thanh-toan.md).

## 10. Luồng "Bài kiểm tra của bài học": không thêm migration

Luồng này dùng `quizzes`, `quiz_questions` của v3 **nguyên trạng**: `dsvhvn-v4.dbml` không đổi hai bảng này (mọi cột và ràng buộc cần cho soạn, phát hành, đóng đã có: `status` DRAFT/PUBLISHED/CLOSED có CHECK, `total_point decimal(6,2)`, `point decimal(5,2)`, khóa chính kép, độ khó, giờ mở, giờ đóng, thời gian làm, hai cờ xáo câu và hiện đáp án). Độ khó của bài học **không lưu**, tính khi truy vấn. Thời gian làm theo cấp, 1-50 câu, điểm bước 0,25 là quy tắc ở tầng code, không có CHECK trong CSDL (đúng ghi chú DBML). `attempts` và `attempt_answers` chỉ được luồng này cập nhật khi đóng bài (tự nộp lượt đang dở); phần v4 của luồng làm bài vẫn chưa thêm. Chi tiết: [bai-kiem-tra.md](bai-kiem-tra.md).

## 11. Luồng "Học tập của học sinh theo cấp": migration `ThemCauTraLoiTungPhan`

Thêm phần v4 của việc làm bài, đúng `dsvhvn-v4.dbml`:

| Thay đổi | Chi tiết |
|---|---|
| `attempt_answers` | Thêm `parts_total smallint NOT NULL DEFAULT 1`, `parts_correct smallint NOT NULL DEFAULT 0`; CHECK `CK_attempt_answers_parts` (`parts_total >= 1 AND parts_correct BETWEEN 0 AND parts_total`) |
| `attempt_answer_parts` (bảng mới, 9 cột) | `id`, `attempt_answer_id`, `answer_id` (NULL), `blank_index tinyint` (NULL), `selected_answer_id` (NULL), `selected_position tinyint` (NULL), `selected_bool bit` (NULL), `response_text nvarchar(255)` (NULL), `is_correct bit NOT NULL DEFAULT 0`; CHECK `CK_attempt_answer_parts_target` (đúng một trong `answer_id`, `blank_index`); UNIQUE có lọc `UX_attempt_answer_parts_answer` (`attempt_answer_id`, `answer_id`) khi `answer_id` không NULL và `UX_attempt_answer_parts_blank` (`attempt_answer_id`, `blank_index`) khi `blank_index` không NULL; 3 khóa ngoại (đều Restrict) |

Dòng cũ của `attempt_answers` (từ luồng đóng bài) lấy giá trị mặc định (1 phần, 0 phần đúng). Phần thêm của migration này: 1 bảng, 11 cột, 3 khóa ngoại (Restrict), 2 CHECK, 2 khóa ngoại được NULL, 2 UNIQUE có lọc, ba DEFAULT
(`is_correct` của bảng mới, `parts_total`, `parts_correct`). Chi tiết: [hoc-tap-theo-cap.md](hoc-tap-theo-cap.md).

## 12. Gộp hai chuỗi nhánh: thứ tự migration và số liệu cuối

Luồng học tập (`ThemCauTraLoiTungPhan`, 06/10/2026 15:05) được làm song song với luồng đa ngôn ngữ, mô hình 3D và gói thanh toán. Các migration không đụng chung bảng nên
được giữ nguyên nội dung; EF Core áp theo thứ tự mã thời gian: `ThemBanDichVaTomTatDiSan` → `ThemCauTraLoiTungPhan` → `ThemMoHinh3DCuaDiSan` → `ThemRangBuocGoiVaThanhToan`.
Database đã có ba migration kia chỉ cần `dotnet ef database update` để áp thêm `ThemCauTraLoiTungPhan`. Ảnh chụp mô hình (`AppDbContextModelSnapshot`) sau khi gộp
khớp model (`dotnet ef migrations has-pending-model-changes` báo không có thay đổi), nên không cần migration đồng bộ.

Số liệu sau khi gộp: **35 bảng, 331 cột, 56 khóa ngoại (4 Cascade), 40 CHECK, 10 UNIQUE có lọc, 15 UNIQUE thường, 17 khóa ngoại được NULL**.
Doanh thu của tổng quan hệ thống đọc thẳng `payments` (SUCCEEDED, `paid_at`) nối `invoices`, `subscriptions`, `plans`; không thêm bảng, cột hay chỉ mục.

## 13. Phần CSDL v4 của luồng "Khách tham quan trên web và app"

Migration `ThemKhachThamQuanVaLichSu` (06/10/2026) thêm hai bảng của nhóm Khách tham quan và quảng cáo trong `dsvhvn-v4.dbml`; không sửa bảng cũ, nên chạy được trên CSDL có dữ liệu. Bảng `ad_unlocks` của
nhóm này thêm ở luồng Quảng cáo Google có thưởng (mục 14).

| Bảng mới | Cột | Ràng buộc |
|---|---|---|
| `guest_devices` | `id`, `device_key` (64), `platform`, `language_code` (DEFAULT `'vi'`), `location_allowed` (bit, DEFAULT 0), `created_at`, `last_seen_at` | UNIQUE thường `UX_guest_devices_device_key`; CHECK `CK_guest_devices_platform` (`ANDROID`, `IOS`, `WEB`), `CK_guest_devices_language_code` (`vi`, `en`, `ko`, `zh`, `ja`) |
| `guest_activities` | `id`, `guest_device_id`, `activity_type`, `heritage_id` (được NULL), `search_query` (255), `language_code`, `created_at` | CHECK `CK_guest_activities_activity_type` (`SEARCH`, `VIEW`, `READ_SUMMARY`, `LISTEN`, `VIEW_3D`), `CK_guest_activities_language_code`, `CK_guest_activities_target` (tìm kiếm cần từ khóa, loại khác cần di sản); chỉ mục `IX_guest_activities_device_time` (`guest_device_id`, `created_at`); khóa ngoại `fk_guest_activities_guest_device_id`, `fk_guest_activities_heritage_id` **Restrict** |

- Không có `deleted_at`: lịch sử của khách **xóa hẳn** (khách xóa, tác vụ nền xóa lượt quá 90 ngày, mỗi thiết bị giữ tối đa 500 lượt gần nhất).
- Hai bảng không có cột vị trí, địa chỉ IP hay thông tin cá nhân. Mã thiết bị không ghép với `users`; không có khóa ngoại nào tới `users`.
- Không ghi nhật ký thao tác cho hoạt động của khách (đã có lịch sử khách).

Số liệu sau migration: 37 bảng, 345 cột (thêm 14), 58 khóa ngoại (thêm 2, Restrict), 45 CHECK (thêm 5), 10 UNIQUE có lọc, 16 UNIQUE thường (thêm 1), 31 cột enum (thêm 4), hai DEFAULT mới (`language_code`, `location_allowed`).
Chi tiết luồng: [khach-tham-quan.md](khach-tham-quan.md).

## 14. Phần CSDL v4 của luồng "Quảng cáo Google có thưởng"

Migration `ThemLuotMoKhoaQuangCao` (06/10/2026) thêm bảng `ad_unlocks` của nhóm Khách tham quan và quảng cáo trong `dsvhvn-v4.dbml`; không sửa bảng cũ, nên chạy được trên CSDL có dữ liệu.

| Bảng mới | Cột | Ràng buộc |
|---|---|---|
| `ad_unlocks` | `id`, `guest_device_id`, `heritage_id`, `platform` (`ANDROID`, `IOS`, `WEB`), `ad_network` (`ADMOB`, `AD_MANAGER`), `transaction_id` (255), `ad_unit_id` (100, được NULL), `unlocked_at`, `expires_at` | UNIQUE thường `UX_ad_unlocks_network_transaction` (`ad_network`, `transaction_id`); CHECK `CK_ad_unlocks_platform`, `CK_ad_unlocks_ad_network`, `CK_ad_unlocks_network_platform` (AdMob chỉ Android hoặc iOS, Ad Manager chỉ web), `CK_ad_unlocks_expiry` (`expires_at` > `unlocked_at`); chỉ mục `IX_ad_unlocks_device_heritage` (`guest_device_id`, `heritage_id`, `expires_at`); khóa ngoại `fk_ad_unlocks_guest_device_id`, `fk_ad_unlocks_heritage_id` **Restrict** |

- Không có `deleted_at`: lượt mở khóa **xóa hẳn** khi cũ hơn 12 tháng (tác vụ nền). Không có bảng chiến dịch quảng cáo, không lưu doanh thu, địa chỉ IP, mã quảng cáo của người xem hay nội dung quảng cáo.
- Nguồn báo không có cột riêng: suy từ `ad_network` (AdMob do máy chủ xác minh với Google, Ad Manager do trình duyệt báo).
- Mã thao tác mới `AD_REWARD_REJECTED` của `audit_logs.action` (cột không có CHECK): phần thưởng bị từ chối, `target_type` = `ad_unlocks`, `new_value` ghi lý do, mạng quảng cáo, nền tảng; lượt hợp lệ và lần gọi lại trùng mã giao dịch không ghi.

Số liệu sau migration: 38 bảng, 354 cột (thêm 9), 60 khóa ngoại (thêm 2, Restrict), 49 CHECK (thêm 4), 10 UNIQUE có lọc, 17 UNIQUE thường (thêm 1), 33 cột enum (thêm 2), không thêm DEFAULT.
Chi tiết luồng: [quang-cao-google.md](quang-cao-google.md).
