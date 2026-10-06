# Luồng Quản trị và tổng quan: tổng quan trường, tổng quan hệ thống, kiểm duyệt ngân hàng câu hỏi (Backend)

Phần backend cho ba việc của khu quản trị: **quản trị trường** xem **tổng quan trường** (giáo viên so với hạn mức, lớp theo cấp, học sinh, bài học, bài kiểm tra, lượt nộp, điểm trung bình theo lớp, lượt AI so với hạn mức, gói và lời nhắc hết hạn);
**quản trị hệ thống** xem **tổng quan hệ thống** (trường, giáo viên, học sinh, nội dung, lượt AI, số trường theo từng gói, biểu đồ theo thời gian) và **kiểm duyệt ngân hàng câu hỏi dùng chung**
(danh sách kèm chất lượng tính từ kết quả làm bài, ẩn câu, hiện lại câu, lý do vào nhật ký thao tác). Mọi số liệu **tính khi hiển thị** từ dữ liệu có sẵn; **không có bảng mới, không có cột mới, không có migration**.

| Mục | Giá trị |
|---|---|
| Chức năng | Ẩn câu hỏi kém chất lượng (kiểm duyệt ngân hàng câu hỏi) · Xem tổng quan trường · Xem tổng quan hệ thống (phần doanh thu: xem mục 4) |
| Màn hình phục vụ | Web quản trị: Tổng quan trường (quản trị trường), Tổng quan hệ thống (quản trị hệ thống, tab Tổng quan), Kiểm duyệt ngân hàng câu hỏi (quản trị hệ thống) |
| Bảng | Chỉ **đọc** (trừ `questions` và `audit_logs` khi ẩn, hiện lại câu): `organizations`, `users`, `classes`, `students`, `lessons`, `quizzes`, `attempts`, `attempt_answers`, `questions`, `quiz_questions`, `heritages`, `ai_generations`, `plans`, `subscriptions` |
| Migration mới | **Không có.** Đặc tả ghi "không có bảng mới". Các truy vấn đã đo trên SQL Server với 45 học sinh × 20 bài kiểm tra × 20 câu (mục 7) nên chưa cần thêm chỉ mục |
| Lớp mới | `Domain/Rules/OverviewRules` (ngưỡng sắp hết hạn, phần trăm điểm, khoảng thời gian và cách gom của biểu đồ) và `QuestionQualityRules` (ngưỡng câu cần xem lại); `Application/Overview`: `SchoolOverviewService`, `SystemOverviewService`, `OverviewDtos`; `Application/Questions/QuestionQualityDtos`; mở rộng `AdminQuestionService`; `Api/Controllers/AdminOverviewController`, endpoint `overview` ở `OrgController`, endpoint `quality` và `restore` ở `AdminQuestionsController` |
| Dùng lại | `PlanGuard` (gói hiệu lực, hạn mức giáo viên và lượt AI của trường, "hôm nay" theo giờ Việt Nam), `VietnamTime`, `QuestionPresenter` (dòng câu hỏi), `AuditLogger`, `ApplyFilters` của ngân hàng câu hỏi |
| Nguồn | Đặc tả của nhóm: use case Ẩn câu hỏi kém chất lượng, Xem tổng quan trường, Xem tổng quan doanh thu; quy tắc Duyệt câu hỏi và ẩn câu kém chất lượng, Ngân hàng câu hỏi dùng chung, Tiến độ và mức thành thạo, Hạn mức AI theo kỳ gói, Phạm vi quyền theo vai trò |

## 1. Phân quyền

| Vai trò | Được làm |
|---|---|
| Quản trị hệ thống (`ADMIN`) | Mọi endpoint `/api/v1/admin/overview...` và `/api/v1/admin/questions...` (mục 2.2, 2.3). **403** ở `/api/v1/org/overview` |
| Quản trị trường (`ORG_ADMIN`) | `GET /api/v1/org/overview`, **chỉ của trường mình** (claim đã đối chiếu CSDL ở mỗi yêu cầu). **403** ở mọi endpoint `/api/v1/admin/...` |
| Giáo viên, học sinh | **403** ở mọi endpoint của luồng này |
| Chưa đăng nhập | **401** |

Quản trị trường **không xem lớp, học sinh hay bài làm cụ thể**: lớp chỉ hiện thành một dòng số (tên lớp, khối, cấp, năm học, sĩ số, số lượt nộp, điểm trung bình), **không có tên giáo viên, tên học sinh hay mã học sinh** (có test kiểm trên nội dung phản hồi thô).
Quản trị hệ thống vẫn không xem lớp, học sinh, bài làm: các số của hệ thống chỉ là số đếm.

## 2. Endpoint

Khuôn phản hồi chung `{ success, data, message, errorCode, traceId, errors }`. Enum viết HOA. Giờ trả về là UTC (`Z`); ngày của biểu đồ và của gói là ngày theo lịch Việt Nam.

### 2.1 Quản trị trường (`/api/v1/org`)

| Method | Đường dẫn | Nội dung | Lỗi chính |
|---|---|---|---|
| GET | `/overview` | **Tổng quan trường** (mục 3.1) | 401, 403 |

`SchoolOverviewDto`: `{ organizationId, organizationName, generatedAt, plan, planNotice, teachers, classCount, levels[], students, lessons, quizzes, attempts, classes[], ai }`.

- `plan`: gói hiển thị (gói hiệu lực nếu có, không thì đăng ký gần nhất): `{ id, planId, planName, status, startDate, endDate, isEffective }`; trường chưa từng có gói thì `null`.
- `planNotice`: `{ kind, daysLeft, message }`, `kind` là `NONE`, `EXPIRING_SOON` hoặc `NO_EFFECTIVE_PLAN`; `daysLeft` chỉ có khi gói còn hiệu lực.
- `teachers`: `{ used, max, planName, canAddTeacher }` (bộ đếm "x / `max_teachers`", cùng nguồn với màn hình Giáo viên của trường).
- `levels[]`: ba phần tử cấp 1, 2, 3: `{ schoolLevel, schoolLevelName, classCount, activeStudents, finishedAttempts, averagePercent }` (cấp không có lớp: các số 0, `averagePercent` null).
- `students`: `{ active, loggedIn, neverLoggedIn }`. `lessons`: `{ total, published }`. `quizzes`: `{ published, open }`. `attempts`: `{ finished, averagePercent }`.
- `classes[]`: `{ classId, className, grade, schoolLevel, schoolLevelName, schoolYear, activeStudents, finishedAttempts, averagePercent }`, xếp theo khối rồi tên lớp.
- `ai`: `{ hasEffectivePlan, planName, used, max, remaining, periodStart, periodEnd }` (cùng cách tính hạn mức AI theo kỳ gói với màn hình Gói và hạn mức).

Endpoint `GET /api/v1/org/school-summary` của luồng Lớp và học sinh vẫn giữ nguyên (chỉ số lớp và học sinh theo cấp).

### 2.2 Quản trị hệ thống: tổng quan (`/api/v1/admin/overview`)

| Method | Đường dẫn | Nội dung | Lỗi chính |
|---|---|---|---|
| GET | `/` | **Tổng quan hệ thống** (mục 3.2) | 401, 403 |
| GET | `/trend?from&to&granularity` | **Biểu đồ theo thời gian** (mục 3.3) | 400, 401, 403 |

`SystemOverviewDto`: `{ generatedAt, schools, teachers, activeStudents, content, ai, plans[], revenue }`.

- `schools`: `{ total, withEffectivePlan, withoutEffectivePlan }`. `teachers`: `{ total, active }` (`active` = tài khoản đang hoạt động, không bị khóa hay vô hiệu).
- `content`: `{ publishedHeritages, lessons, publishedLessons, publishedQuizzes, finishedAttempts, questions: { approved, pending, hidden } }`.
- `ai`: `{ total, byTeachers, byAdmin, thisMonth }`.
- `plans[]`: mỗi gói một dòng `{ planId, planName, price, isActive, activeSchools, expiringSoonSchools }`, gồm mọi gói trong bảng gói, kể cả gói đã ngừng bán (`isActive` false) còn trường đang dùng.
- `revenue`: `{ available, message }`, hiện `available` = false (mục 4).

`SystemTrendDto`: `{ from, to, granularity, points[], totals }`; mỗi điểm `{ period, newSchools, newTeachers, newStudents, lessonsCreated, quizzesPublished, attemptsFinished, aiRequests }`; `totals` là tổng cả khoảng (`period` = `total`).

### 2.3 Quản trị hệ thống: kiểm duyệt ngân hàng câu hỏi (`/api/v1/admin/questions`)

Hai endpoint cũ giữ nguyên: danh sách chỉ đọc `GET /` và chi tiết `GET /{id}`; ẩn `POST /{id}/hide`. Luồng này thêm:

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/quality` | Danh sách kiểm duyệt kèm chất lượng (mục 3.4) | 200 `QuestionQualityPageDto` | 400, 401, 403 |
| POST | `/{id}/restore` | **Hiện lại** câu đã bị ẩn; body tùy chọn `{ reason }` (tối đa 500 ký tự) | 200 `QuestionDetailDto` | 400 (lý do quá dài), 404, 422 (câu chưa bị ẩn) |

Bộ lọc của `/quality`: `status` (`APPROVED` đang trong ngân hàng hoặc `REJECTED` đã bị ẩn; bỏ trống là cả hai, `PENDING` trả 400), `lowQualityOnly` (true chỉ giữ câu cần xem lại), `sort` (`NEWEST` mặc định hoặc `LOWEST_RATE`),
`questionType`, `level`, `difficultyLevel`, `heritageId`, `organizationId` (trường của người tạo), `createdBy`, `keyword`, `page`, `pageSize`.
`QuestionQualityPageDto`: `{ items[], page, pageSize, totalItems, totalPages, approvedCount, hiddenCount, lowQualityCount, minAnswers, lowCorrectPercent }`;
mỗi dòng `{ question (dòng câu hỏi của ngân hàng, kèm người tạo và trường), answered, correct, correctRatePercent, lowQuality, hiddenAt }`.
`QuestionAccessDto` của chi tiết (phía quản trị hệ thống) có thêm `canRestore`: true khi câu đang bị ẩn (`canHide` true khi câu đang được duyệt).

## 3. Quy tắc

### 3.1 Tổng quan trường

- **Phạm vi:** chỉ trường của người gọi; lớp đã xóa mềm, bài học và bài kiểm tra đã xóa, học sinh đã xóa hoặc đã vô hiệu **không tính**. Trường đã ngừng thì tài khoản không vào được (403).
- **Giáo viên:** số tài khoản giáo viên chưa xóa mềm của trường (mọi trạng thái, không tính quản trị trường) so với `max_teachers` của gói hiệu lực; không có gói hiệu lực thì `max` null.
- **Học sinh đang hoạt động** = chưa xóa mềm, chưa vô hiệu. **Chưa đăng nhập lần nào** = tài khoản chưa có `last_login_at`; `loggedIn` = `active` trừ `neverLoggedIn`.
- **Bài kiểm tra đã phát hành** = trạng thái đã phát hành hoặc đã đóng (bài nháp không tính); **đang mở** = đã phát hành, chưa quá giờ đóng. **Bài học đã xuất bản** = trạng thái xuất bản (không tính nháp và lưu trữ).
- **Lượt nộp** = lượt đã nộp hoặc hết giờ tự nộp (lượt đang làm dở không tính) của học sinh đang hoạt động ở bài kiểm tra của lớp.
- **Điểm trung bình** = trung bình cộng của phần trăm điểm từng lượt (điểm ÷ tổng điểm của chính bài đó, làm tròn 2 chữ số), để các bài có tổng điểm khác nhau vẫn so sánh được; theo lớp, theo cấp (gộp mọi lượt của các lớp trong cấp) và cả trường. Chưa có lượt nào thì null, không phải 0.
  Giáo viên thấy điểm ở mọi cấp; ở tổng quan trường cũng hiện phần trăm cho lớp cấp 1 (học sinh cấp 1 vẫn chỉ thấy sao, quy tắc cách hiện kết quả theo cấp không đổi).
- **Lượt AI so với hạn mức** theo kỳ của gói hiệu lực, dùng đúng `PlanGuard` của quy tắc Hạn mức AI theo kỳ gói (lượt thành công hoặc đang xử lý chưa quá lâu của giáo viên trong trường; lượt lỗi không tính; gợi ý và dịch của quản trị hệ thống không tính).
- **Lời nhắc về gói** (`planNotice`): không có gói hiệu lực (hết hạn, bị hủy, chỉ có đăng ký chờ thanh toán, chưa có gói) → `NO_EFFECTIVE_PLAN`, lời nhắc nói rõ không tạo được bài học, bài kiểm tra mới và không dùng được AI nhưng **học sinh vẫn học bình thường**;
  gói hiệu lực còn **không quá 14 ngày** (tính cả hôm nay và ngày cuối) → `EXPIRING_SOON` kèm số ngày và ngày hết hạn; còn lại `NONE`. Trường không có gói hiệu lực **vẫn xem được** tổng quan (quy tắc Khi trường không có gói hiệu lực chỉ chặn thao tác tạo, không chặn xem thống kê).

### 3.2 Tổng quan hệ thống

- Chỉ tính **trường chưa ngừng** và **lớp chưa xóa**: ngừng một trường thì giáo viên, học sinh, bài học, bài kiểm tra, lượt nộp của trường biến khỏi các số (test kiểm).
- **Trường có gói hiệu lực** = có ít nhất một đăng ký đang hoạt động và hôm nay (giờ Việt Nam) nằm trong ngày bắt đầu và kết thúc; `withoutEffectivePlan` = tổng trừ số này.
- **Học sinh** = học sinh đang hoạt động của lớp chưa xóa. **Giáo viên** = tài khoản giáo viên chưa xóa của trường chưa ngừng.
- **Số trường theo từng gói:** mỗi trường chỉ đếm một lần cho mỗi gói dù có đăng ký chồng nhau; `expiringSoonSchools` = số trường dùng gói đó mà gói còn không quá 14 ngày.
- **Ngân hàng câu hỏi:** đã duyệt, chờ duyệt (của mọi người tạo), đã bị quản trị hệ thống ẩn (đã bị loại bởi quản trị hệ thống; câu người tạo tự loại không tính).
- **Lượt AI toàn hệ thống:** lượt thành công hoặc đang xử lý chưa quá 5 phút, không tính lượt lỗi và lượt bị bỏ dở. `byTeachers` = lượt gắn với bài học (soạn bài, sinh câu hỏi); `byAdmin` = lượt gắn với di sản (dịch, tóm tắt, không tính vào hạn mức của trường nào); `thisMonth` = lượt tạo từ ngày 1 của tháng hiện tại theo giờ Việt Nam.

### 3.3 Biểu đồ theo thời gian

- `from`, `to` là ngày dạng `yyyy-MM-dd` theo lịch Việt Nam; bỏ trống `to` thì lấy hôm nay, bỏ trống `from` thì lấy 30 ngày gần nhất (tính cả hôm nay). Khoảng tối đa **366 ngày**.
- Gom theo **ngày** khi khoảng không quá **92 ngày**, dài hơn gom theo **tháng**; có thể chỉ định `granularity` = `DAY` hoặc `MONTH` (`DAY` chỉ cho khoảng tới 92 ngày). Mọi kỳ trong khoảng đều có một điểm, kể cả kỳ không có gì (số 0), nên biểu đồ không bị đứt.
- Một thời điểm thuộc ngày theo **giờ Việt Nam** (UTC+7): 16:59:59 UTC vẫn là ngày đó, 17:00:00 UTC đã sang ngày sau (test kiểm đúng biên).
- Nguồn từng chuỗi: trường, giáo viên, học sinh, bài học tạo mới theo `created_at`; bài kiểm tra phát hành theo `published_at`; lượt nộp theo `submitted_at` của lượt đã nộp hoặc hết giờ tự nộp; lượt AI theo `created_at` (cùng điều kiện đếm lượt AI ở mục 3.2).
- Sai khoảng (`to` trước `from`, quá 366 ngày, `granularity` = `DAY` cho khoảng dài) trả **400** với lỗi theo trường.

### 3.4 Kiểm duyệt ngân hàng câu hỏi

- **Chỉ thấy câu đã duyệt và câu đã bị chính quản trị hệ thống ẩn**; câu chờ duyệt hay do người tạo tự loại là việc riêng của giáo viên (404 khi mở theo mã, như luồng Ngân hàng câu hỏi).
- **Chất lượng tính từ kết quả làm bài**, không lưu: `answered` = số lượt trả lời (tính cả bỏ trống) của mọi lượt đã nộp hoặc hết giờ tự nộp ở mọi trường; `correct` = số lượt đúng hoàn toàn (câu nhiều phần chỉ đúng khi đúng mọi phần);
  `correctRatePercent` = `correct` ÷ `answered` (1 chữ số), null khi chưa ai làm.
- **Câu cần xem lại** (`lowQuality`) = có ít nhất **10 lượt trả lời** và tỉ lệ đúng **dưới 30%** (so sánh bằng số nguyên: `correct × 100 < 30 × answered`; đúng 30% thì chưa cờ). Câu đã bị ẩn không còn cờ. Ngưỡng nằm ở `QuestionQualityRules` và được trả trong `minAnswers`, `lowCorrectPercent` để giao diện giải thích.
- **Không có chức năng "báo cáo câu hỏi":** đặc tả và CSDL v4 không có bảng báo cáo, nên "câu bị báo cáo" ở đây là câu cần xem lại suy ra từ kết quả làm bài. Nếu sau này có báo cáo của giáo viên thì thêm bảng riêng, không đụng quy tắc này.
- **Sắp xếp** `LOWEST_RATE`: tỉ lệ đúng thấp nhất trước, câu chưa ai làm xếp cuối; mặc định câu mới nhất trước.
- **Ẩn** (đã có): chỉ câu đã duyệt; chuyển sang bị loại với người duyệt là quản trị hệ thống; `reason` tùy chọn tối đa 500 ký tự, vào `new_value` của nhật ký; câu không chọn được vào bài kiểm tra mới, không nhân bản được; bài kiểm tra đã phát hành và kết quả cũ giữ nguyên (cả `answered` và `correct` của câu vẫn còn).
- **Hiện lại** (mới): chỉ câu đang bị ẩn (câu khác trả 422 "Không thể hiện lại câu hỏi. Chỉ hiện lại được câu đã bị ẩn."). Câu về trạng thái đã duyệt với **người duyệt là người tạo** (đúng như lúc câu được duyệt ban đầu, vì câu đã duyệt luôn do người tạo duyệt) và thời điểm duyệt là lúc hiện lại;
  câu hiện lại cho mọi giáo viên mọi trường, người tạo sửa, xóa, nhân bản được như câu thường (câu đã nằm trong bài kiểm tra vẫn chỉ đọc). `reason` tùy chọn vào nhật ký.
- **Nhật ký thao tác:** ẩn ghi `QUESTION_HIDDEN` (có sẵn), hiện lại ghi `QUESTION_RESTORED` (nhãn "Hiện lại câu hỏi"); cả hai ghi người làm, địa chỉ IP, `old_value` và `new_value` (trạng thái, người duyệt, lý do) và hiện ở màn Nhật ký thao tác lọc theo hành động.

## 4. Phần doanh thu (chưa nối)

Nhánh này **chưa có gói trả phí và thanh toán VNPay**, nên tổng quan hệ thống **không có số doanh thu**: `revenue` = `{ available: false, message: "Doanh thu theo kỳ và danh sách giao dịch sẽ hiện khi nối với gói dịch vụ và thanh toán VNPay. Doanh thu quảng cáo xem ở bảng điều khiển của Google." }`.
Giao diện hiện đúng câu này ở chỗ biểu đồ doanh thu và danh sách giao dịch. **Khi gộp với nhánh gói dịch vụ và thanh toán VNPay** sẽ thêm: doanh thu theo kỳ (tổng số tiền giao dịch thành công theo ngày thanh toán giờ Việt Nam, gói Miễn phí không tạo doanh thu),
danh sách giao dịch lọc theo trạng thái, trường, khoảng ngày; khi đó `available` = true và mọi số khác của tổng quan không đổi. Doanh thu quảng cáo không bao giờ nằm ở đây (hệ thống không nhận số liệu doanh thu từ Google; trang quản trị chỉ dẫn sang bảng điều khiển của Google).

## 5. Mã lỗi

Dùng các mã chung của hệ thống: `INVALID` (400) với lỗi theo trường (`to`, `from`, `granularity`, `status`, `level`, `page`, `pageSize`, `reason`), `FORBIDDEN` (403), `BLOCKED` (404 khi mở câu không thuộc tầm nhìn, kèm lời "Không tìm thấy câu hỏi."; 422 khi hiện lại câu chưa bị ẩn).

## 6. Điểm hiện thực cần đồng bộ với đặc tả

- **"Hiện lại câu" là bổ sung** so với đặc tả: use case Ẩn câu hỏi kém chất lượng và quy tắc Duyệt câu hỏi và ẩn câu kém chất lượng chỉ có ẩn, và danh sách mã nhật ký của đặc tả không có mã hiện lại. Code thêm `QUESTION_RESTORED` (không đổi CSDL vì cột `action` là chuỗi không có CHECK);
  danh sách mã trong code là 22. Nếu nhóm không muốn hiện lại thì bỏ endpoint `restore`, mã `QUESTION_RESTORED` và nút ở giao diện, các phần còn lại không bị ảnh hưởng.
- **"Câu bị báo cáo" chưa có nguồn dữ liệu** (mục 3.4): dùng dấu hiệu từ kết quả làm bài (10 lượt, dưới 30%). Hai ngưỡng này là giá trị đề xuất của luồng, đặc tả chưa quy định; đổi ở `QuestionQualityRules`.
- **Điểm trung bình theo phần trăm** thay cho điểm tuyệt đối, vì đặc tả cần "điểm trung bình theo lớp" mà các bài có tổng điểm khác nhau.
- **Quản trị trường thấy tên lớp** (đặc tả cần "điểm trung bình theo lớp"): đây là số tổng hợp theo lớp, không lộ học sinh hay giáo viên. Nếu nhóm muốn kín hơn thì bỏ tên lớp và chỉ giữ theo cấp.
- **Biểu đồ gom trong bộ nhớ:** mỗi chuỗi lấy các mốc thời gian trong khoảng rồi gom theo ngày Việt Nam ở máy chủ, vì cách đổi múi giờ trong SQL khác nhau giữa SQLite và SQL Server. Đo ở mục 7 chưa cần tối ưu; khi dữ liệu lớn hơn nhiều thì chuyển sang gom ở CSDL.

## 7. Kiểm thử và chạy thử

- **Unit** `OverviewRulesTests` (ngày còn lại và biên 14 ngày, phần trăm điểm và làm tròn, trung bình, ngưỡng gom theo ngày hoặc tháng, kỳ qua ranh giới tháng và năm, ngưỡng 10 lượt và 30% đúng biên) và `AuditLabelTests` (22 mã, mỗi mã một nhãn tiếng Việt).
- **Tích hợp trên SQLite** (Api thật):
  `OverviewApiTests` (16 test: số liệu tổng quan trường kèm trung bình theo lớp và cấp; chỉ tính lượt đã nộp của học sinh đang hoạt động, lớp xóa mềm biến khỏi số liệu; trường này không thấy số của trường khác; phản hồi không có tên học sinh, giáo viên, mã học sinh; lượt AI so với hạn mức;
  lời nhắc gói ở các biên 14 và 15 ngày, hết hạn, hủy mà trang vẫn xem được; quyền; tổng quan hệ thống tăng đúng bằng những gì một trường mới thêm và giảm khi ngừng trường, ẩn câu chuyển từ đã duyệt sang đã ẩn, lượt AI của giáo viên và của quản trị hệ thống đếm riêng, lượt lỗi hoặc bỏ dở không tính;
  phần doanh thu báo chưa nối; biểu đồ theo ngày Việt Nam đúng biên 16:59:59 và 17:00:00, kỳ trống vẫn có điểm, lượt đang làm dở không tính, gom theo tháng, mặc định 30 ngày, lỗi khoảng thời gian),
  `QuestionQualityApiTests` (8 test: số lượt trả lời, tỉ lệ đúng và cờ cần xem lại; chưa đủ 10 lượt thì không cờ và lượt đang làm không tính; lọc cần xem lại và sắp xếp theo tỉ lệ đúng; câu ẩn rời danh sách cờ nhưng còn số liệu; lọc theo trường, người tạo, dạng; hiện lại và nhật ký; chỉ hiện lại được câu đã ẩn; quyền).
- **SQL Server LocalDB** (`OverviewSqlServerTests`, database tạm tự xóa): mọi endpoint dịch được sang SQL và cho cùng số liệu như SQLite; trường 45 học sinh × 20 bài kiểm tra × 20 câu (900 lượt, 18.900 dòng) trong giới hạn 3 giây ở mỗi màn
  (đo: tổng quan trường 23 ms, tổng quan hệ thống 14 ms, biểu đồ 15 ms, kiểm duyệt 165-193 ms).
- **Smoke** `scripts/smoke-quan-tri-tong-quan.sh` trên API thật (cổng 5180) và database tạm `dsvhvn_tam_wt_…` (đã xóa sau khi chạy), `Email__Mode=Log`, `Ai__Mode=Simulated`: **62 bước, đúng 62** (trường mới, thêm giáo viên, lớp, học sinh, số liệu hai tổng quan thay đổi đúng,
  biểu đồ mặc định, theo tháng và ba lỗi khoảng thời gian, tạo câu, danh sách kiểm duyệt, ẩn với lý do, hiện lại, hiện lại lần hai bị chặn, nhật ký có nhãn tiếng Việt và lý do, phân quyền 401, 403, 404). Số liệu cần lượt làm bài, gói sắp hết hạn và biểu đồ theo ngày kiểm trong bộ test tích hợp.

Lệnh chạy: xem đầu tệp smoke (cần nạp di sản một lần bằng `-- --chi-khoi-tao-csdl`; từ worktree đặt thêm hai biến `Seed__HeritageData__ProvincesFile`, `Seed__HeritageData__HeritagesFile` trỏ tuyệt đối tới `doc/du-lieu-di-san`).
