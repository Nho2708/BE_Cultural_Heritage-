# Luồng Lớp, học sinh và tài khoản học sinh (Backend)

Phần backend của khu **Lớp học** (web quản trị, giáo viên) và khu **đăng nhập của học sinh** (web học tập và chế độ Học sinh của app mobile).
Giáo viên tạo lớp, thêm học sinh (thêm học sinh **là** tạo tài khoản), in phiếu đăng nhập, đặt lại mật khẩu; học sinh đăng nhập bằng mã học sinh,
đổi mật khẩu lần đầu rồi xem lớp của em.

| Mục | Giá trị |
|---|---|
| Chức năng | Học sinh đăng nhập bằng mã học sinh · Đổi mật khẩu của học sinh · Quản lý lớp học · Thêm học sinh vào lớp và tạo tài khoản (từng em, nhập danh sách) · Đặt lại mật khẩu cho học sinh · Quản lý thông tin học sinh |
| Màn hình phục vụ | Web quản trị (giáo viên): Lớp học, Chi tiết lớp (tab Học sinh), Thêm học sinh và tạo tài khoản, Phiếu đăng nhập của học sinh. Web học tập và app: Đăng nhập học sinh, Đổi mật khẩu, lớp của em |
| Bảng | `classes`, `students`, `users` (vai trò học sinh), `audit_logs`. Không bảng mới, **không migration mới**: CSDL v4 không đổi các bảng này so với v3 (chỉ thêm ghi chú cho `classes.grade` và `users.username`) |
| Lớp mới | `Domain/Rules/ClassRules` (sĩ số, năm học, cấp học, sinh mã và mật khẩu tạm); `Application/Classes`, `Application/Students`, `Application/StudentAuth`; `Infrastructure/Files/StudentListFileReader`; `Api/Controllers/TeacherClassesController`, `TeacherStudentsController`, `StudentController`; `OrgController.SchoolSummary` |
| Gói thư viện mới | `ClosedXML` (đọc và dựng tệp `.xlsx`, giấy phép MIT) ở `DSVHVN.Infrastructure` |
| Nguồn | `doc/plan/spec` (nhóm quy tắc Lớp, học sinh và tài khoản học sinh; thông báo của đăng nhập và đổi mật khẩu học sinh) |

## 1. Phân quyền

| Vai trò | Được làm |
|---|---|
| Giáo viên (`TEACHER`) | Chỉ lớp **do mình phụ trách** trong trường mình và học sinh của các lớp đó. Lớp, học sinh của giáo viên khác (cùng trường hay khác trường) trả **404** như không tồn tại |
| Quản trị trường (`ORG_ADMIN`) | **Không** xem, sửa lớp hay học sinh, không đặt lại mật khẩu học sinh (403). Chỉ xem số liệu tổng hợp của trường ở `GET /api/v1/org/school-summary` (số lớp, số học sinh đang hoạt động, theo từng cấp học) |
| Quản trị hệ thống (`ADMIN`) | Không xem lớp, học sinh (403) |
| Học sinh (`STUDENT`) | Chỉ thông tin và mật khẩu của chính mình, lớp của mình; không thấy học sinh khác. Không vào được khu giáo viên, quản trị trường, quản trị hệ thống |

Thêm học sinh không cần gói hiệu lực, không tốn lượt AI hay chỗ giáo viên. Giáo viên bị khóa hay xóa mềm **không** chặn học sinh của họ;
quản trị hệ thống ngừng trường thì mọi tài khoản của trường, kể cả học sinh, không đăng nhập được.

## 2. Endpoint

Khuôn phản hồi chung `{ success, data, message, errorCode, traceId, errors }`. Tiền tố `/api/v1`. Chữ hiển thị tiếng Việt, mã lỗi theo nghĩa.

### 2.1 Lớp học (giáo viên) — `/teacher/classes`

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/teacher/classes` | `keyword`, `schoolYear`, `grade`, `page`, `pageSize` | 200 trang lớp `{ id, name, grade, schoolLevel, schoolLevelName, schoolYear, activeStudentCount, maxStudents, lessonCount, neverLoggedInCount, createdAt }`; xếp năm học mới trước, rồi khối, rồi tên | 400 `INVALID` |
| POST | `/teacher/classes` | `{ name, grade, schoolYear }` | 201 lớp vừa tạo | 400 `REQUIRED`/`INVALID` theo trường; 409 `DUPLICATE` (tên lớp đã có trong cùng giáo viên và năm học, không phân biệt hoa thường) |
| GET | `/teacher/classes/{classId}` | — | 200 | 404 `BLOCKED` |
| PUT | `/teacher/classes/{classId}` | `{ name, grade, schoolYear, confirmLevelChange }` | 200 | 400, 404, 409; **422 `CLASS_LEVEL_CHANGE_NEEDS_CONFIRMATION`** khi đổi khối sang cấp khác trong lúc lớp đã có bài học (gửi lại với `confirmLevelChange` = true) |
| DELETE | `/teacher/classes/{classId}` | — | 200; xóa mềm lớp và **vô hiệu mọi học sinh đang hoạt động** trong cùng giao dịch | 404; 422 `BLOCKED` khi lớp còn bài kiểm tra đang mở (đã phát hành, chưa tới giờ đóng) |

- Lớp thuộc trường của giáo viên tạo, do chính người đó phụ trách, không đổi người phụ trách.
- Tên lớp 1-100 ký tự. Khối lớp bắt buộc, số nguyên 1-12 (CSDL cũng chặn). Năm học `YYYY-YYYY` gồm hai năm liên tiếp (`2026-2027`).
- Cấp học **suy từ khối lớp, không lưu**: 1-5 cấp 1, 6-9 cấp 2, 10-12 cấp 3.
- Sĩ số tối đa **60 học sinh đang hoạt động** (chưa xóa, chưa vô hiệu).

### 2.2 Học sinh của lớp (giáo viên) — `/teacher/classes/{classId}/students`

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `` | `keyword` (họ tên hoặc mã), `status` = `all` (mặc định) / `active` / `inactive`, `page`, `pageSize` | 200 `{ items[], page, pageSize, totalItems, totalPages, activeCount, maxStudents }`; mỗi dòng `{ id, studentCode, fullName, isActive, lastLoginAt, passwordChanged, createdAt }` | 400 (`status` lạ) |
| POST | `` | `{ fullName }` (1-255 ký tự) | 201 `{ students[], sheet }` — học sinh vừa tạo và **phiếu đăng nhập** | 400; 422 `BLOCKED` (lớp đủ 60 học sinh) |
| GET | `/import/template` | `format` = `xlsx` (mặc định) / `csv` | 200 tệp mẫu một cột "Họ và tên" (csv UTF-8 có BOM để Excel mở đúng tiếng Việt) | 400 |
| POST | `/import/preview` | `multipart/form-data`: `file` (`.xlsx` hoặc `.csv` UTF-8, ≤ 1 MB) **hoặc** `text` (dán, mỗi dòng một họ tên) | 200 xem trước `{ rows[], totalRows, validCount, errorCount, warningCount, remainingCapacity, maxStudents }`; mỗi dòng `{ rowNumber, fullName, isValid, errors[], warnings[] }`. **Chưa lưu gì** | 400 (sai loại tệp, không đọc được, không phải UTF-8, trống, quá 60 dòng, gửi cả tệp lẫn văn bản) |
| POST | `/import` | `{ rows: [{ rowNumber, fullName }] }` (các dòng lấy từ bước xem trước) | 201 `{ addedCount, skippedCount, skipped[], message, created: { students[], sheet } }`; **dòng hợp lệ lưu trong một giao dịch**, dòng lỗi bị bỏ qua và báo đủ | 400; 422 `BLOCKED` (không có dòng hợp lệ nào) |
| POST | `/reset-password` | `{ studentIds: [..] }` (1-60 em đang hoạt động của lớp) | 200 phiếu đăng nhập mới `sheet` | 404 (có em không thuộc lớp), 422 `BLOCKED` (có em đang bị vô hiệu) |
| PUT | `/{studentId}` | `{ fullName }` | 200 | 400, 404 |
| POST | `/{studentId}/deactivate` | — | 200 | 404; 422 (đã vô hiệu; đang có lượt làm bài dở) |
| POST | `/{studentId}/reactivate` | — | 200 | 404; 422 (đang hoạt động; lớp đã đủ 60 học sinh) |
| DELETE | `/{studentId}` | — | 200 xóa mềm học sinh **và** tài khoản | 404; 422 (đang có lượt làm bài dở) |

Quy tắc nhập danh sách:

- Tối đa **60 dòng** mỗi lần (tệp, văn bản và lần lưu). Dòng đầu là "Họ và tên" (hoặc "Họ tên", "Tên học sinh", "Name"…) thì bỏ qua như dòng tiêu đề; dòng trống ở cuối bỏ đi.
  `rowNumber` luôn là số dòng trong tệp hoặc văn bản gốc (tiêu đề là dòng 1), nên báo lỗi trùng với ô người dùng nhìn thấy.
- Tệp `.csv` đọc cột đầu, nhận dấu ngăn `,` hoặc `;` (Excel tiếng Việt xuất `;`), ô trong dấu nháy kép; không phải UTF-8 thì báo "chọn CSV UTF-8". Tệp `.xlsx` đọc cột đầu bảng tính đầu tiên.
- Mỗi dòng: **trống** → lỗi "Họ tên đang trống."; **quá 255 ký tự** → lỗi; **vượt sĩ số 60** (tính theo số chỗ còn lại, từ trên xuống) → lỗi
  "Vượt sĩ số tối đa 60 học sinh đang hoạt động của lớp."; **trùng họ tên** (không phân biệt hoa thường và dấu) với học sinh đã có hay dòng trên → chỉ **cảnh báo**, vẫn thêm, phân biệt bằng mã học sinh.
- Họ tên được chuẩn hóa: cắt khoảng trắng hai đầu, gộp khoảng trắng liên tiếp, Unicode dựng sẵn.
- Thông điệp kết quả: "Đã thêm {n} học sinh vào lớp {tên lớp}. {m} dòng bị bỏ qua: dòng 3 (Họ tên đang trống); …".

### 2.3 Học sinh — đăng nhập và khu học

| Method | Đường dẫn | Chính sách | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|---|
| POST | `/student/auth/login` | công khai | `{ studentCode, password }` | 200 `{ tokenType, accessToken, expiresAt, student }`; `student` gồm `userId, studentId, studentCode, fullName, mustChangePassword, schoolLevel, schoolLevelName, class{…}, lastLoginAt` | 400 `REQUIRED`; 401 `STUDENT_INVALID_CREDENTIALS`; 403 `STUDENT_ACCOUNT_BLOCKED` |
| GET | `/student/me` | học sinh (kể cả đang bị buộc đổi mật khẩu) | — | 200 thông tin của em, lớp, cấp học mới nhất | 401 |
| PUT | `/student/me/password` | học sinh (kể cả đang bị buộc đổi mật khẩu) | `{ currentPassword, newPassword, confirmNewPassword }` | 200 `{ tokenType, accessToken, expiresAt, student }` — **token mới** | 400 `PASSWORD_POLICY`, `PASSWORD_MISMATCH`, `SAME_PASSWORD`, `REQUIRED`/`INVALID` (mật khẩu hiện tại) |
| GET | `/student/class` | học sinh **không** còn bị buộc đổi mật khẩu | — | 200 lớp của em `{ id, name, grade, schoolLevel, schoolLevelName, schoolYear, teacherName, organization }` | 403 `PASSWORD_CHANGE_REQUIRED` |

Mọi endpoint khu học về sau (mở bài, làm bài…) dùng chính sách `StudentReady` như `/student/class`.

## 3. Mã học sinh, mật khẩu tạm, phiếu đăng nhập

- **Mã học sinh** = `HS` + 6 ký tự lấy bằng bộ sinh số ngẫu nhiên an toàn từ bảng 32 ký tự (chữ số 2-9 và chữ A-Z bỏ `O`, `I`; không có `0`, `1`), ví dụ `HS8K2QX7`.
  Cũng là tên đăng nhập (`users.username`). Không trùng toàn hệ thống (`UX_students_student_code` **không lọc** `deleted_at`), không đổi, **không cấp lại** kể cả sau khi xóa.
  Trước khi lưu, mã được kiểm với bảng `students` và `users`; nếu hai yêu cầu đua nhau và ràng buộc không trùng chặn thì sinh lô mã khác, **tối đa 5 lần**, vẫn trùng thì lỗi hệ thống (500) và ghi log.
- **Mã do người dùng gõ** được chuẩn hóa: bỏ khoảng trắng và dấu gạch, đổi chữ hoa (`hs 8k2-qx7` → `HS8K2QX7`).
- **Mật khẩu tạm** 8 ký tự, luôn có ít nhất một chữ hoa, một chữ thường, một chữ số, bỏ ký tự dễ nhầm `0 O o 1 I l`. Chỉ lưu bản băm (PBKDF2), băm song song cho cả lô.
  Chỉ xuất hiện **một lần** trong phản hồi tạo/đặt lại (`Cache-Control: no-store`); không có trong `audit_logs`, log hay email.
- **Phiếu đăng nhập** (`sheet`): `className`, `loginUrl` (web học tập), `appDownloadUrl` (có thể null), `instructions[]` (mở web, hoặc cài app và chọn chế độ "Học sinh", đăng nhập bằng mã và mật khẩu tạm),
  `firstLoginReminder` ("Em sẽ phải đổi mật khẩu ở lần đăng nhập đầu tiên."), `notice` ("Mật khẩu tạm chỉ hiện một lần…") và `entries[]` `{ studentId, fullName, className, studentCode, temporaryPassword }`.
  Việc in nhiều phiếu mỗi trang A4 và tải PDF là việc của giao diện. Cấu hình ở mục `App` của `appsettings`: `LearningWebBaseUrl` (mặc định `http://localhost:5174`), `LearningLoginPath` (`/dang-nhap`), `MobileAppDownloadUrl` (để trống thì phiếu chỉ hướng dẫn chung).
- Tài khoản học sinh: vai trò học sinh, thuộc trường của lớp, **không email**, `status` = `ACTIVE`, `must_change_password` = 1. Học sinh chỉ lưu họ tên, mã và tài khoản (không ngày sinh, email, số điện thoại).

## 4. Đăng nhập và đổi mật khẩu của học sinh

- **Đăng nhập**: sai mã hay sai mật khẩu luôn trả cùng một câu "Mã học sinh hoặc mật khẩu chưa đúng. Em hãy kiểm tra lại phiếu đăng nhập." (401), kể cả khi người lớn đăng nhập nhầm ở đây
  (tài khoản người lớn coi như không tồn tại; thời gian trả lời cũng không lộ, dùng băm giả). Ngược lại học sinh đăng nhập ở web quản trị nhận thông điệp sai thông tin của web quản trị.
- **Tạm khóa**: sai 5 lần liên tiếp trong 15 phút thì khóa đăng nhập 15 phút (403 `STUDENT_ACCOUNT_BLOCKED`, "…Em đã nhập sai nhiều lần. Em hãy thử lại sau {n} phút hoặc nhờ thầy cô đặt lại mật khẩu."),
  đếm trong bộ nhớ máy chủ theo tài khoản (mã không tồn tại đếm theo chuỗi đã gõ), **không đổi trạng thái tài khoản**, ghi `LOGIN_LOCKED_OUT` (vai trò `STUDENT`). Giáo viên đặt lại mật khẩu thì gỡ ngay.
- **Đúng mật khẩu mới xét điều kiện**: tài khoản hoạt động và chưa xóa; học sinh chưa vô hiệu, chưa xóa; lớp chưa xóa; trường chưa ngừng. Không đạt thì 403 "…Em hãy báo thầy cô phụ trách lớp." Tài khoản học sinh không dùng trạng thái khóa.
- **Token**: access token 8 giờ, không refresh token. Thêm claim `lvl` (cấp học 1/2/3 của lớp lúc phát token) để chọn bộ giao diện; **giá trị chuẩn là `schoolLevel` ở `/student/me`** (giáo viên sửa khối lớp thì học sinh thấy cấp mới từ lần tải trang hoặc mở app kế tiếp).
  Ghi `users.last_login_at` trước khi phát token; **không** ghi nhật ký lần đăng nhập thành công của học sinh.
- **Kiểm lại mỗi yêu cầu** (`JwtAuthentication`): tài khoản, học sinh, lớp, trường và dấu bảo mật. Vô hiệu học sinh, xóa học sinh, xóa lớp, ngừng trường có hiệu lực **ngay** ở yêu cầu kế tiếp (401 kèm thông điệp "chưa đăng nhập được");
  mở lại học sinh thì phiên cũ dùng tiếp được.
- **Buộc đổi mật khẩu** (`must_change_password` = 1, lần đầu hoặc sau khi giáo viên đặt lại): token chỉ gọi được `/student/me` và `/student/me/password`; mọi endpoint khu học khác trả 403 `PASSWORD_CHANGE_REQUIRED`
  ("Em hãy đặt mật khẩu mới của riêng em để bắt đầu học. …"). Chế độ này **không hỏi mật khẩu hiện tại**; mật khẩu mới phải đạt quy tắc (8-64 ký tự, chữ hoa, chữ thường, chữ số), nhập hai lần giống nhau và **khác mật khẩu tạm**.
  Chế độ tự đổi (đã đổi xong) bắt buộc nhập đúng mật khẩu hiện tại và mật khẩu mới phải khác.
- **Đổi xong trả token mới** trong cùng phản hồi; token cũ và phiên trên thiết bị khác hết dùng ngay vì dấu bảo mật suy từ `password_hash` đổi theo. Đặt lại mật khẩu bởi giáo viên cũng làm mọi phiên cũ hết dùng;
  phiên đang mở **không** được chuyển sang màn hình đổi mật khẩu, học sinh phải đăng nhập lại bằng mật khẩu tạm trên phiếu mới.
  (Cách làm dấu bảo mật chung của hệ thống thay cho việc so `iat` của token với `users.updated_at` nên sửa họ tên học sinh không làm học sinh bị đăng xuất.)

## 5. Nhật ký thao tác (`audit_logs`)

| Hành động | Khi nào | Đối tượng | `new_value` |
|---|---|---|---|
| `STUDENT_ACCOUNTS_CREATED` | Một dòng cho mỗi lần thêm hoặc nhập danh sách | `classes` (id lớp) | `{ classId, className, count }` — **không có mật khẩu tạm** |
| `STUDENT_PASSWORD_RESET` | Một dòng cho mỗi lần đặt lại (nhiều em) | `classes` | `{ classId, className, studentIds[], count }` |
| `STUDENT_DEACTIVATED` | Vô hiệu một em, xóa mềm một em | `students` | `{ isActive: false, studentCode }` hoặc `{ deleted: true, studentCode }` |
| `STUDENT_DEACTIVATED` | Xóa lớp vô hiệu các em | `classes` | `{ classId, className, count, reason: "classDeleted" }` |
| `STUDENT_REACTIVATED` | Mở lại một em | `students` | `{ isActive: true, studentCode }` |
| `LOGIN_LOCKED_OUT` | Học sinh bị tạm khóa do sai 5 lần | `users` | `{ lockedMinutes: 15 }` |

Bảng nhật ký vẫn chỉ thêm (DbContext chặn sửa, xóa). Màn Nhật ký thao tác của quản trị hệ thống dùng nhãn tiếng Việt sẵn có.

## 6. Giao dịch, đồng thời và ràng buộc

- Thêm học sinh, nhập danh sách, mở lại học sinh, xóa lớp chạy trong **một giao dịch có khóa dòng lớp** (`UPDLOCK, HOLDLOCK` trên `classes`, `IAppDbContext.LockClassAsync`): kiểm sĩ số rồi thêm không bị hai yêu cầu đồng thời vượt 60.
  Kiểm trên LocalDB: hai lần nhập 40 dòng cùng lúc vào lớp trống lưu đúng 20 + 40 = 60; 16 yêu cầu thêm từng em vào lớp còn 10 chỗ thì đúng 10 thành công.
- Nhật ký ghi cùng lần `SaveChanges` với dữ liệu nên lỗi giữa chừng hoàn tác tất cả (có test ép lỗi ghi nhật ký sau khi đã lưu học sinh: không còn học sinh hay tài khoản nào).
- Đặt lại mật khẩu nhiều em trong một giao dịch; bộ đếm tạm khóa gỡ sau khi lưu.
- Không vô hiệu hay xóa học sinh đang có lượt làm bài `IN_PROGRESS` (kiểm trên `attempts`).
- Không có chức năng chuyển học sinh sang lớp khác: năm học mới thì tạo lớp mới và thêm học sinh (tài khoản mới), lịch sử cũ ở lớp cũ.

## 7. Thông điệp (mã theo nghĩa)

`STUDENT_INVALID_CREDENTIALS`, `STUDENT_ACCOUNT_BLOCKED`, `PASSWORD_CHANGE_REQUIRED`, `PASSWORD_MISMATCH`, `SAME_PASSWORD` (dùng chung với người lớn), `STUDENTS_ADDED`,
`CLASS_LEVEL_CHANGE_NEEDS_CONFIRMATION`, `REQUIRED`, `INVALID`, `DUPLICATE`, `BLOCKED`, `FORBIDDEN`. Lời với học sinh xưng "em", với giáo viên xưng "bạn". Dòng nhắc cố định của màn đăng nhập
"Quên mật khẩu? Em hãy nhờ thầy cô đặt lại." có sẵn ở `Messages.StudentForgotPasswordHint` (không có liên kết quên mật khẩu qua email cho học sinh).

## 8. Kiểm thử

- `StudentRulesTests`, `StudentListFileReaderTests`, `StudentServiceTests` (SQLite, đồng hồ tua được): sinh mã và mật khẩu, chuẩn hóa mã, năm học, cấp học, đọc `.csv`/`.xlsx`/văn bản, tệp mẫu,
  mã trùng và thử lại 5 lần, giao dịch tất cả hoặc không có gì, tạm khóa hết sau 15 phút, mật khẩu chỉ lưu băm, sĩ số 60, các tình huống bị chặn khi đăng nhập.
- `ClassesApiTests`, `StudentsApiTests`, `StudentLoginApiTests` (Api thật, JWT, kiểm tài khoản mỗi yêu cầu): phân quyền theo vai trò và trường (404 chéo giáo viên, trường), lớp, nhập danh sách qua `multipart`, đặt lại mật khẩu, vô hiệu/mở lại/xóa, buộc đổi mật khẩu, token cũ hết dùng.
- `StudentSqlServerTests` (database tạm của LocalDB tạo bằng migration, tự xóa): đồng thời giữ sĩ số, mã không cấp lại, tên lớp và tìm kiếm theo collation.
- `scripts/smoke-lop-hoc-sinh.sh`: smoke qua HTTP thật (48 bước) trên database tạm; xem mục 9.

## 9. Smoke trên Kestrel thật

Ngày chạy 06/10/2026, database tạm `dsvhvn_kiem_thu_smokels` (LocalDB, migration hiện có, đã xóa sau khi chạy), API `dotnet run --no-launch-profile --urls http://localhost:5091`
với `Email__Mode=Log`: **48/48 bước đúng mã HTTP**, gồm tạo trường và giáo viên, tạo lớp, thêm học sinh, xem trước bằng văn bản và `.csv`, nhập danh sách, tải tệp mẫu, đăng nhập sai và đúng, buộc đổi mật khẩu,
token cũ bị từ chối, đặt lại mật khẩu, vô hiệu và mở lại, tạm khóa sau 5 lần sai, xóa lớp, và kiểm mật khẩu không xuất hiện trong nhật ký.

```
ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_kiem_thu_smokels;Trusted_Connection=True;TrustServerCertificate=True" \
Email__Mode=Log Seed__AdminPassword='<mật khẩu tạm>' dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5091 > api.log 2>&1 &
BASE=http://localhost:5091 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu tạm>' bash scripts/smoke-lop-hoc-sinh.sh
```

## 10. Điều chưa làm hoặc khác với spec

- Quản trị trường chỉ có **số liệu tổng hợp** (`/org/school-summary`), không có danh sách lớp, học sinh: spec nhóm quy tắc phạm vi quyền cấm quản trị trường xem lớp và học sinh.
- Spec ghi "từ chối token phát hành trước `users.updated_at`"; hiện thực dùng dấu bảo mật suy từ `password_hash` (cơ chế chung của mọi vai trò), cho cùng kết quả (đổi hoặc đặt lại mật khẩu làm mọi phiên cũ hết dùng) nhưng không bị sửa họ tên làm đăng xuất.
- Phiếu đăng nhập trả **dữ liệu** để giao diện in và tạo PDF; backend không dựng tệp PDF.
- Thông điệp "Phiên đăng nhập đã hết. Vui lòng đăng nhập lại." cho token hết hạn hay bị thay chưa có riêng: 401 vẫn dùng thông điệp chung như web quản trị (`FORBIDDEN`); giao diện xử lý theo mã 401 và đưa về màn đăng nhập.
- Giới hạn tần suất theo IP chỉ áp cho API công khai; đăng nhập học sinh chỉ có tạm khóa theo tài khoản (lớp dùng chung một IP không bị chặn lẫn nhau).
- Hạn mức `max_teachers` và gói không liên quan tới học sinh; các chức năng bài học, quiz, làm bài của học sinh thuộc luồng sau.
