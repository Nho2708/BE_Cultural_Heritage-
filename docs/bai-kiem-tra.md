# Luồng Bài kiểm tra của bài học, nhân bản bài học kèm bài kiểm tra (Backend)

Phần backend của khu **Bài kiểm tra** của giáo viên (web quản trị): giáo viên tạo bài kiểm tra cho một bài học của mình, chọn câu từ ngân hàng (đúng cấp của lớp), đặt điểm,
thứ tự, thời gian làm, giờ mở và giờ đóng, xáo câu, cho xem đáp án sau khi nộp; **phát hành** để khóa danh sách câu; **đóng** khi muốn dừng nhận bài; **nhân bản** để đổi câu hoặc dùng lại.
Luồng này cũng làm hai việc ở phía bài học: **nhân bản bài học kèm bài kiểm tra** sang lớp khác và **lọc bài học theo độ khó** (tính từ các câu trong bài kiểm tra).
Bài kiểm tra **không có mã riêng**: học sinh của lớp mở bài kiểm tra qua **mã bài học**, đã có từ luồng bài học.

| Mục | Giá trị |
|---|---|
| Chức năng | Soạn bài kiểm tra cho bài học · Phát hành và đóng bài kiểm tra · Nhân bản bài học sang lớp khác (kèm bài kiểm tra) · Lọc bài học theo độ khó |
| Màn hình phục vụ | Web quản trị (giáo viên): Bài kiểm tra của tôi, Soạn và phát hành bài kiểm tra, Chọn câu từ ngân hàng, Nhân bản bài học sang lớp khác (ô "Chép kèm các bài kiểm tra"), Bài học của tôi (cột và bộ lọc độ khó) |
| Bảng | `quizzes`, `quiz_questions` (có từ v3, **không đổi cột**); đọc `lessons`, `lesson_heritages`, `classes`, `students`, `questions`, `heritages`, `attempts`, `subscriptions`, `plans`; ghi `audit_logs` (phát hành, đóng); khi đóng ghi `attempts` của các lượt đang dở |
| Migration mới | **Không có.** Hai bảng `quizzes`, `quiz_questions` của v3 đã đủ cột cho luồng này (`dsvhvn-v4.dbml` không đổi chúng). Phần v4 của luồng làm bài (`attempt_answers.parts_total`, `parts_correct`, bảng `attempt_answer_parts`) vẫn thuộc luồng làm bài |
| Lớp mới | `Domain/Rules/QuizRules` (hằng số, thời gian làm theo cấp, câu hợp cấp, giai đoạn mở đóng, độ khó của bài học, giờ Việt Nam); `Application/Quizzes` (`QuizService`, `QuizDetailBuilder`, `QuizAutoCloser`, `QuizCopier`, `QuizScope`, DTO, validator); `Infrastructure/Background/QuizAutoCloseService` (tác vụ nền); `Api/Controllers/TeacherQuizzesController`. Sửa `LessonService` (nhân bản kèm bài kiểm tra, lọc độ khó), `QuestionPresenter` (câu trong bài kiểm tra đã xóa không còn chỉ đọc) |
| Nguồn | Đặc tả của nhóm: nhóm quy tắc Quiz (Soạn quiz và tổng điểm, Phát hành, sửa và đóng quiz, Nhân bản bài học sang lớp khác, Bài học dành riêng cho lớp, Dạng câu hỏi và cấp phù hợp), màn hình Bài kiểm tra của tôi, Soạn và phát hành bài kiểm tra, Chọn câu từ ngân hàng, Nhân bản bài học sang lớp khác; luồng "Quiz của bài học, nhân bản bài học" |

## 1. Phân quyền

| Vai trò | Được làm |
|---|---|
| Giáo viên (`TEACHER`) | Tạo, xem, sửa, xóa (nháp), phát hành, đóng, nhân bản bài kiểm tra **do chính mình tạo** cho bài học của mình; chọn câu từ ngân hàng dùng chung; nhân bản bài học của mình kèm bài kiểm tra; lọc bài học của mình theo độ khó |
| Giáo viên khác (kể cả cùng trường) | Bài kiểm tra của người khác trả **404** như không tồn tại, ở mọi endpoint |
| Quản trị trường, quản trị hệ thống | Không dùng khu này (**403**). Quản trị hệ thống xem được nhật ký phát hành, đóng |
| Học sinh | 403 ở khu giáo viên. Học sinh của lớp thấy bài kiểm tra **đã phát hành hoặc đã đóng** của bài trên trang bài học (mở bằng mã bài học, luồng bài học), bài nháp không hiện |
| Chưa đăng nhập | 401 |

Bài kiểm tra thuộc phạm vi của giáo viên khi: do chính người gọi tạo, thuộc bài học của chính người gọi, ở lớp của trường mình, và bài kiểm tra, bài học, lớp đều chưa xóa mềm.

## 2. Endpoint

Khuôn phản hồi chung `{ success, data, message, errorCode, traceId, errors }`. Tiền tố `/api/v1`. Enum viết HOA như trong CSDL. Giờ gửi lên: có `Z` hoặc `+07:00` thì đổi về UTC, **không kèm múi giờ thì hiểu là giờ Việt Nam**;
giờ trả về là UTC (`Z`), giao diện đổi sang giờ Việt Nam khi hiển thị.

### 2.1 Giáo viên: bài kiểm tra — `/teacher/quizzes`

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/teacher/quizzes` | Lọc `lessonId`, `classId`, `status` (`DRAFT`/`PUBLISHED`/`CLOSED`), `keyword` (trong tên); `page`, `pageSize` | 200 trang `{ id, name, lessonId, lessonTitle, classId, className, grade, schoolLevel, status, statusLabel, openState, openStateLabel, openAt, closeAt, timeLimitMinutes, difficultyLevel, questionCount, totalPoint, submittedCount, studentCount, publishedAt, createdAt, updatedAt }`, mới sửa trước | 400 (phân trang, enum lạ) |
| POST | `/teacher/quizzes` | `{ lessonId, name, description?, difficultyLevel?, timeLimitMinutes?, openAt?, closeAt?, shuffleQuestions?, showAnswersAfterSubmit? }` | 201 chi tiết, trạng thái `DRAFT`, lớp và cấp lấy từ bài học | 400 (tên, độ dài, giờ đóng không sau giờ mở, thời gian làm sai cấp hoặc ngoài 1-180); 404 (bài học không phải của mình); 422 `PLAN_REQUIRED`, `BLOCKED` (bài Lưu trữ) |
| GET | `/teacher/quizzes/{id}` | — | 200 chi tiết (mục 2.2) | 404 |
| PUT | `/teacher/quizzes/{id}` | Như tạo nhưng không có `lessonId`; **gửi đủ mọi trường**, trường để trống là xóa giá trị (hai cờ `shuffleQuestions`, `showAnswersAfterSubmit` để trống là `false`) | 200 chi tiết | 400; 404; 422 (bài đã đóng; bài đã phát hành mà đổi trường khóa) |
| DELETE | `/teacher/quizzes/{id}` | — | 200, **xóa mềm** bài nháp | 404; 422 (đã phát hành hoặc đã đóng) |
| GET | `/teacher/quizzes/{id}/question-candidates` | `questionType`, `difficultyLevel`, `heritageId`, `lessonHeritagesOnly` (mặc định `true`), `keyword`, `page`, `pageSize` | 200 trang `{ question{ như một dòng của ngân hàng câu hỏi }, inQuiz }` kèm `level` và `allowedTypes`; chỉ câu đã duyệt, chưa xóa, dùng được ở cấp của lớp | 404 |
| POST | `/teacher/quizzes/{id}/questions` | `{ questionIds[], point? }` (`point` mặc định 1) | 200 chi tiết; thêm vào cuối, đánh thứ tự tiếp, tính lại tổng điểm | 400 (câu không chọn được, đã có trong bài, điểm sai); 404; 422 `QUESTION_LEVEL_MISMATCH` (dạng hoặc cấp không hợp lớp), `BLOCKED` (đã phát hành, quá 50 câu) |
| PUT | `/teacher/quizzes/{id}/questions` | `{ items[{ questionId, point }] }`: danh sách đầy đủ theo thứ tự mới | 200 chi tiết; câu không có trong danh sách bị bỏ; thứ tự đánh lại từ 1 | 400 (câu chưa có trong bài, trùng, điểm sai); 404; 422 (đã phát hành) |
| DELETE | `/teacher/quizzes/{id}/questions/{questionId}` | — | 200 chi tiết; các câu còn lại đánh lại thứ tự | 404; 422 (đã phát hành) |
| POST | `/teacher/quizzes/{id}/publish` | — | 200 `{ quiz, message }` | 404; 422 `NOT_READY` (kèm danh sách còn thiếu), `PLAN_REQUIRED`, `BLOCKED` (đã đóng) |
| POST | `/teacher/quizzes/{id}/close` | — | 200 `{ quiz, expiredAttempts, message }` | 404; 422 (bài nháp) |
| POST | `/teacher/quizzes/{id}/duplicate` | `{ name? }` (có thể bỏ trống toàn bộ) | 201 `{ quiz, skippedQuestions, message }`, bài nháp mới trong cùng bài học | 400 (tên); 404; 422 `PLAN_REQUIRED`, `BLOCKED` (bài học Lưu trữ) |

### 2.2 Chi tiết bài kiểm tra

`{ id, name, description, lessonId, lessonTitle, lessonStatus, lessonAccessCode, classId, className, grade, schoolLevel, schoolLevelName, difficultyLevel, timeLimitMinutes, timeLimitRule, timeLimitRuleText,
openAt, closeAt, shuffleQuestions, showAnswersAfterSubmit, totalPoint, status, statusLabel, openState, openStateLabel, publishedAt, questions[], questionCount, readiness, access, submittedCount, studentCount, publishNotice, createdAt, updatedAt }`

- `questions[]`: `{ questionId, sortOrder, point, questionType, typeName, content, imageUrl, difficultyLevel, heritageId, heritageName, levelMin, levelMax, isUsable, unusableReason }`. Câu đã bị ẩn, bị xóa hoặc không còn hợp cấp của lớp có `isUsable` = false kèm lý do để giao diện hiện **cảnh báo đỏ**.
- `timeLimitRule`: `NOT_ALLOWED` (cấp 1), `OPTIONAL` (cấp 2), `REQUIRED` (cấp 3); `timeLimitRuleText` là câu chữ cho ô nhập.
- `readiness`: `{ isReady, missing[], warnings[] }`. Chỉ tính cho bài nháp; bài đã phát hành hoặc đã đóng luôn `isReady` = true.
- `access`: `{ canEditSettings, canEditCloseTime, canEditQuestions, canPublish, canClose, canDelete, canDuplicate }` để bật tắt nút.
- `openState`: `NONE` (bài nháp), `NOT_OPEN` (chưa tới giờ mở), `OPEN` (đang nhận bài), `ENDED` (quá giờ đóng hoặc đã đóng).
- `submittedCount` / `studentCount`: lượt đã nộp (đã nộp hoặc hết giờ) trên sĩ số (học sinh đang hoạt động của lớp).
- `publishNotice`: lời xác nhận trước khi phát hành, có khi bài đủ điều kiện ("Sau khi phát hành, danh sách câu hỏi và điểm sẽ bị khóa. Học sinh lớp … làm được bài kiểm tra "…" qua mã bài học … từ … đến …").

### 2.3 Bài học (sửa ở luồng này) — `/teacher/lessons`

| Method | Đường dẫn | Thay đổi |
|---|---|---|
| GET | `/teacher/lessons` | Thêm bộ lọc `difficulty` (`EASY`/`MEDIUM`/`HARD`); mỗi dòng có thêm `difficulty` và `difficultyLabel` ("Dễ", "Trung bình", "Khó", "Chưa có bài kiểm tra") |
| POST | `/teacher/lessons/{id}/duplicate` | Thêm `includeQuizzes` (mặc định `false`); kết quả có thêm `copiedQuizzes`, `skippedQuestions` |

## 3. Quy tắc

### 3.1 Tạo bài kiểm tra

- Bài kiểm tra gắn **một bài học của chính giáo viên**, bài đang **Nháp hoặc Đã xuất bản** (không gắn vào bài Lưu trữ). Một bài học có nhiều bài kiểm tra. Lớp làm bài là lớp của bài học; **không chọn lớp, không có mã riêng**.
- Tạo cần **gói hiệu lực** (422 `PLAN_REQUIRED`). Xem, sửa, xóa nháp, đóng không cần gói.
- Tên 1-255 ký tự, mô tả tối đa 4000 ký tự, độ khó chung tùy chọn (giáo viên chọn). Giờ đóng phải sau giờ mở khi đặt cả hai.

### 3.2 Thời gian làm theo cấp của lớp

| Cấp | Thời gian làm bài | Khi soạn | Khi phát hành |
|---|---|---|---|
| 1 | Không giới hạn, không đồng hồ (chỉ có giờ đóng) | Phải để trống (400 nếu điền) | Phải để trống |
| 2 | Tùy chọn, 1-180 phút | Để trống hoặc 1-180 | Để trống hoặc 1-180 |
| 3 | Bắt buộc, 1-180 phút | Được để trống khi đang soạn (điền sau) | **Phải đã đặt** |

### 3.3 Chọn câu, điểm, tổng điểm

- Chỉ nhận câu **đã duyệt**, **chưa xóa**, có **cấp phù hợp chứa cấp của lớp** và **dạng dùng được ở cấp đó** (bảng dạng câu theo cấp). Câu khác cấp trả 422 `QUESTION_LEVEL_MISMATCH` với câu "Câu hỏi dạng "{dạng}" không dùng được ở lớp cấp {cấp}.".
- Mỗi câu xuất hiện **một lần** trong bài (khóa chính kép); bài có **1-50 câu**.
- `point` mỗi câu **lớn hơn 0, không quá 100, bước 0,25**, mặc định 1. `sort_order` **liên tục từ 1**, đánh lại sau mỗi lần bỏ câu hoặc đổi thứ tự.
- `total_point` = tổng `point`, tính lại **trong cùng giao dịch** mỗi khi thêm, bỏ câu, đổi điểm (bài nháp) và một lần nữa khi phát hành. Tối đa 50 × 100 = 5000, vừa `decimal(6,2)`.
- Một câu sai trong danh sách thêm thì **không thêm câu nào**.
- Câu hỏi không bắt buộc thuộc di sản của bài; hộp chọn câu **mặc định lọc theo các di sản của bài học** (`lessonHeritagesOnly=false` để xem mọi di sản).

### 3.4 Phát hành

Chỉ khi đủ **mọi điều kiện** (422 `NOT_READY` liệt kê hết những gì còn thiếu một lượt):

1. bài học của bài kiểm tra **đã xuất bản**;
2. có 1-50 câu, **mọi câu còn dùng được** ở cấp hiện tại của lớp (chưa bị ẩn, chưa xóa, còn hợp dạng và cấp; lớp vừa đổi khối sang cấp khác thì phải bỏ các câu không còn hợp);
3. đã đặt **giờ mở** và **giờ đóng**, giờ mở trước giờ đóng, giờ đóng sau thời điểm hiện tại;
4. thời gian làm đúng quy tắc của cấp (mục 3.2);
5. trường có **gói hiệu lực** (422 `PLAN_REQUIRED`).

Một giao dịch (có khóa dòng lớp, hai lần bấm đồng thời không ghi hai lần): tính lại `total_point`, đặt `status` = `PUBLISHED`, `published_at`, ghi `audit_logs` `QUIZ_PUBLISHED`. **Không sinh mã, không chọn lớp**: học sinh lớp của bài vào bài kiểm tra qua mã bài học.
Phát hành bài đã phát hành không đổi gì và không ghi thêm nhật ký.

### 3.5 Sau khi phát hành

- **Khóa**: danh sách câu, điểm, thứ tự, bài học, thời gian làm, giờ mở, xáo câu, cờ hiện đáp án, độ khó. Thêm, bỏ, đổi điểm câu trả 422.
- **Còn sửa được**: tên, mô tả, **giờ đóng** (lùi hoặc kéo dài; giờ đóng mới không sớm hơn hiện tại và phải sau giờ mở; giữ nguyên giờ đóng cũ thì không bị chặn). Gửi lên trường khóa khác giá trị đang có thì 422.
- Câu trong bài đã phát hành bị quản trị hệ thống **ẩn**: bài giữ nguyên (kết quả cũ không đổi); chi tiết hiện câu đó `isUsable` = false.
- Muốn đổi câu thì **nhân bản** bài kiểm tra; muốn dùng cho lớp khác thì nhân bản bài học kèm bài kiểm tra.

### 3.6 Đóng

- Giáo viên đóng bài đã phát hành **bất kỳ lúc nào**: `CLOSED`, không mở lại, ghi `QUIZ_CLOSED`. Đóng bài đã đóng không đổi gì; bài nháp không đóng được (xóa nếu không dùng).
- Các lượt `IN_PROGRESS` của bài được **tự nộp ở trạng thái `EXPIRED`** trong cùng giao dịch: `submitted_at` = lúc đóng, `duration_seconds` (không quá thời gian làm), `score` cộng từ các câu đã chấm của lượt, `total_point` = tổng điểm của bài, `correct_count`, `total_questions`.
- **Tự đóng khi quá giờ đóng**: tác vụ nền `QuizAutoCloseService` (mặc định chạy mỗi 60 giây, `Quizzes:AutoCloseIntervalSeconds`; ≤ 0 thì tắt) chuyển mọi bài `PUBLISHED` có `close_at` đã qua sang `CLOSED`, tự nộp lượt đang dở, ghi `QUIZ_CLOSED` với vai trò `SYSTEM`. Chạy lặp an toàn.
  Trong lúc chờ tác vụ, `openState` đã là `ENDED` và học sinh thấy bài là "Đã đóng" (tính theo giờ).
- Bài kiểm tra đang mở (đã phát hành, chưa tới giờ đóng) vẫn **chặn lưu trữ bài học và xóa lớp** (đã có từ luồng bài học và luồng lớp).

### 3.7 Xóa

Chỉ **xóa mềm bài nháp**. Bài đã phát hành, đã đóng giữ nguyên để giữ kết quả. Bài học đã có bài kiểm tra (chưa xóa) chỉ lưu trữ, không xóa (đã có từ luồng bài học).
Câu hỏi nằm trong bài kiểm tra **chưa xóa** thì chỉ đọc; bài nháp xóa rồi thì câu **được mở khóa** lại.

### 3.8 Nhân bản bài kiểm tra

Trong cùng bài học: bài `DRAFT` mới cùng câu, điểm, thứ tự, mô tả, độ khó, thời gian làm, xáo câu, cờ hiện đáp án; **không chép giờ mở, giờ đóng, lượt làm bài**. Tên mặc định "{tên} (bản sao)" (cắt cho vừa 255 ký tự).
Câu đã bị ẩn hoặc xóa **không được chép**, kết quả báo số câu bỏ qua. Làm được từ bài ở mọi trạng thái (kể cả đã đóng), cần gói hiệu lực, bài học không phải Lưu trữ.
Câu không còn hợp cấp của lớp (do lớp đổi cấp) vẫn được chép và hiện cảnh báo, việc phát hành bị chặn cho tới khi bỏ.

### 3.9 Nhân bản bài học kèm bài kiểm tra

`POST /teacher/lessons/{id}/duplicate` với `includeQuizzes` = `true` chép **mọi bài kiểm tra chưa xóa** của bài (nháp, đã phát hành, đã đóng) thành **bài kiểm tra nháp** của bài mới ở lớp đích:

- Chép tên, mô tả, độ khó, xáo câu, cờ hiện đáp án, điểm từng câu, thứ tự; **giờ mở, giờ đóng để trống** cho giáo viên đặt lại; người tạo là người nhân bản; không chép lượt làm bài.
- **Bỏ** câu đã bị ẩn hoặc đã xóa, và câu có **dạng hoặc cấp phù hợp không dùng được ở cấp của lớp đích**; thứ tự đánh lại liên tục, tổng điểm tính lại; kết quả báo `skippedQuestions`. Bài không còn câu nào vẫn được tạo (nháp, rỗng).
- **Thời gian làm chuẩn hóa theo cấp lớp đích**: cấp 1 bỏ giới hạn; cấp 2, cấp 3 giữ giá trị cũ (cấp 3 chưa có thì để trống cho giáo viên đặt).
- Toàn bộ nằm trong **một lần lưu** với bài mới (cùng giao dịch). Bài gốc và bài kiểm tra gốc không đổi. Bài mới chưa có mã; muốn phát hành bài kiểm tra phải xuất bản bài mới trước (có mã riêng).
- Thông điệp: "Đã tạo bài học nháp ở lớp {lớp}. Đã chép {n} bài kiểm tra thành bản nháp; hãy đặt lại giờ mở và giờ đóng. Bỏ qua {m} câu hỏi đã ẩn hoặc không dùng được ở cấp của lớp." (hai vế sau chỉ có khi n, m lớn hơn 0).
- Không chọn `includeQuizzes` thì không chép bài kiểm tra nào (hành vi cũ).

### 3.10 Độ khó của bài học

Độ khó của bài học **không lưu**, tính khi truy vấn: là mức độ khó (Dễ, Trung bình, Khó) chiếm **nhiều câu nhất** trong các câu của các bài kiểm tra **chưa xóa** của bài (mọi trạng thái: nháp, đã phát hành, đã đóng; qua `quiz_questions` nối `questions.difficulty_level`).
**Hai mức bằng nhau thì lấy mức cao hơn.** Bài chưa có bài kiểm tra nào có câu thì `difficulty` = `null`, nhãn "Chưa có bài kiểm tra", và **không khớp bộ lọc độ khó nào**. Câu bị ẩn sau đó vẫn tính (bài kiểm tra giữ nguyên). Bộ lọc `difficulty` kết hợp được với lớp, trạng thái, loại di sản, từ khóa.

## 4. Nhật ký

| Hành động | Khi nào | Đối tượng |
|---|---|---|
| `QUIZ_PUBLISHED` | Phát hành bài kiểm tra (một dòng mỗi lần phát hành thật) | `quizzes`; giá trị sau có tên, bài học, lớp, số câu, tổng điểm, giờ mở, giờ đóng, thời gian làm |
| `QUIZ_CLOSED` | Giáo viên đóng, hoặc tác vụ nền tự đóng (vai trò `SYSTEM`, không có IP) | `quizzes`; giá trị sau có số lượt tự nộp |

Hai mã có sẵn trong danh sách 21 mã của đặc tả; không thêm mã mới. Tạo, sửa, xóa nháp, nhân bản không vào `audit_logs`.

## 5. Mã lỗi

| Mã HTTP | `errorCode` | Khi nào |
|---|---|---|
| 400 | `REQUIRED`, `INVALID` | Thiếu tên, thiếu bài học; tên, mô tả quá dài; độ khó lạ; thời gian làm sai cấp hoặc ngoài 1-180; giờ đóng không sau giờ mở; giờ đóng sớm hơn hiện tại (bài đã phát hành); điểm sai bước; câu không chọn được, đã có trong bài, trùng; phân trang sai (kèm `errors` theo trường như `questionIds[1]`, `items[2].point`, `timeLimitMinutes`, `closeAt`) |
| 401 / 403 | `FORBIDDEN` | Chưa đăng nhập; không phải giáo viên |
| 404 | `BLOCKED` | Bài kiểm tra, bài học, lớp không tồn tại, đã xóa hoặc của giáo viên khác; câu không có trong bài khi bỏ |
| 422 | `NOT_READY` | Phát hành khi chưa đủ điều kiện (kèm danh sách còn thiếu) |
| 422 | `QUESTION_LEVEL_MISMATCH` | Thêm câu có dạng hoặc cấp không dùng được ở cấp của lớp |
| 422 | `BLOCKED` | Bài đã phát hành mà thêm, bỏ, đổi câu hoặc đổi trường khóa; bài đã đóng mà sửa hoặc phát hành; xóa bài đã phát hành; đóng bài nháp; quá 50 câu; bài học Lưu trữ |
| 422 | `PLAN_REQUIRED` | Tạo, nhân bản, phát hành khi trường không có gói hiệu lực |

## 6. Cấu hình

| Khóa | Mặc định | Ý nghĩa |
|---|---|---|
| `Quizzes:AutoCloseIntervalSeconds` | 60 | Chu kỳ tác vụ nền tự đóng bài quá giờ đóng; ≤ 0 thì tắt (bộ test tắt để gọi thẳng `QuizAutoCloser`) |

## 7. Chạy thử và kiểm thử

```bash
dotnet test                          # có LocalDB thì chạy thêm các test trên database tạm của SQL Server (tự xóa)
# Smoke test qua HTTP thật trên database tạm (không gửi thư thật: Email__Mode=Log; AI chạy bản giả lập):
ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_kiem_thu_smoke;Trusted_Connection=True;TrustServerCertificate=True" \
Email__Mode=Log Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5092 > api.log 2>&1 &
BASE=http://localhost:5092 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-bai-kiem-tra.sh
```

Smoke (`scripts/smoke-bai-kiem-tra.sh`, 140 bước): xuất bản một di sản và thêm ảnh; tạo trường, hai giáo viên, bốn lớp (cấp 1, 2, 2, 3), ba bài đã xuất bản (mã bài học) và một bài nháp; tạo sáu câu; lỗi nhập liệu (thiếu tên, giờ đóng trước giờ mở,
cấp 1 có thời gian làm, quá 180 phút, bài của giáo viên khác); bài kiểm tra cấp 2: thêm câu hợp cấp và từ chối câu chọn hình, tổng điểm, hộp chọn câu (chỉ câu hợp cấp, đánh dấu câu đã có), đổi thứ tự, bỏ câu, câu trong bài kiểm tra thành chỉ đọc;
phát hành (thiếu giờ thì chặn, đủ thì khóa), các thao tác bị khóa, sửa tên, mô tả, giờ đóng, bài có bài kiểm tra mở không lưu trữ hay xóa được; học sinh thấy bài kiểm tra "Đang mở" rồi "Đã đóng" qua mã bài học; nhân bản bài kiểm tra, đóng (lần hai không đổi), nhật ký
phát hành và đóng; cấp 1 không có thời gian làm, cấp 3 bắt buộc; danh sách và lọc; nhân bản bài học có và không chép bài kiểm tra, sang lớp cấp 1 thì bỏ câu không hợp cấp; độ khó của bài học và bộ lọc; phân quyền. Đã chạy thật trên API ở cổng riêng với database tạm:
140/140 bước đúng, và kiểm tay tác vụ nền: đặt `close_at` về quá khứ thì sau chu kỳ kế tiếp hai bài `PUBLISHED` thành `CLOSED` với `QUIZ_CLOSED` vai trò `SYSTEM`.

Bộ test (xUnit) của luồng:

- **Đơn vị** (`QuizRulesTests`): điểm hợp lệ (bước 0,25, tới 100), thời gian làm theo cấp (soạn và phát hành, chuẩn hóa khi nhân bản), câu hợp cấp theo bảng 8 dạng × 3 cấp, giai đoạn mở đóng (biên đúng giờ mở, đúng giờ đóng), độ khó của bài học (nhiều nhất, hòa lấy cao hơn, rỗng, không phụ thuộc thứ tự), đổi giờ về UTC và giờ Việt Nam.
- **API** (`QuizAuthoringApiTests`, `QuizLifecycleApiTests`, `QuizLessonApiTests` qua `WebApplicationFactory`): tạo theo cấp, lỗi theo trường, bài học Nháp, Đã xuất bản, Lưu trữ, của người khác; gói hết hạn; sửa thay toàn bộ; xóa nháp; thêm câu (thứ tự, điểm, tổng, câu sai không thêm câu nào, tối đa 50, câu bị ẩn hoặc xóa);
  đổi thứ tự, bỏ câu; hộp chọn câu; danh sách và lọc; phạm vi giáo viên ở mọi endpoint và 403 các vai trò khác; khóa sau khi phát hành; phát hành (đủ điều kiện, liệt kê thiếu, giờ, cấp 3, bài học nháp, câu bị ẩn, lớp đổi cấp, gói, một dòng nhật ký, phát hành lại); sửa bài đã phát hành (trường khóa, giờ đóng);
  đóng (tự nộp lượt đang dở, nhật ký, lặp lại, bài nháp, bài đóng không sửa); tác vụ tự đóng (chỉ bài quá giờ, bài nháp không đóng, nhật ký `SYSTEM`, chạy lặp); lưu trữ bài học sau khi đóng; nhân bản bài kiểm tra; học sinh thấy trạng thái bài kiểm tra;
  nhân bản bài học kèm bài kiểm tra (cùng cấp, sang cấp 1 và cấp 3, câu bị ẩn, bài rỗng, phát hành sau khi xuất bản bài mới); độ khó và bộ lọc; câu trong bài kiểm tra nháp đã xóa được mở khóa.
- **SQL Server LocalDB** (`QuizSqlServerTests`, database tạm tạo bằng migration, tự xóa): kiểu cột và CHECK của `quizzes`, `quiz_questions`, hai UNIQUE của lượt làm bài; vòng đời đầy đủ; tìm tên có dấu; lọc độ khó và nhân bản kèm bài kiểm tra dịch được sang SQL;
  tác vụ tự đóng; **bốn lần phát hành đồng thời chỉ ghi một dòng nhật ký**; **hai yêu cầu thêm câu đồng thời không vượt 50 câu**; CSDL từ chối lượt làm bài thứ hai của cùng học sinh và trạng thái ngoài danh sách.

## 8. Điểm hiện thực cần đồng bộ với đặc tả

| Điểm | Hiện thực | Cần làm |
|---|---|---|
| Thời gian làm bài của cấp 3 khi soạn | Quy tắc Soạn quiz ghi "cấp 3 bắt buộc 1-180", quy tắc Phát hành ghi "cấp 3 phải đã đặt". Hiện thực cho **để trống khi đang soạn**, bắt buộc khi phát hành (màn hình ghi "bắt buộc nhập") | Xác nhận với giao diện: ô bắt buộc khi phát hành |
| Cách gửi danh sách câu | Đặc tả mô tả thao tác (chọn nhiều, kéo thả, bỏ câu), chưa nêu API. Hiện thực ba endpoint: thêm (`POST`), đặt lại thứ tự, điểm và bỏ câu (`PUT` toàn bộ danh sách), bỏ một câu (`DELETE`) | Giao diện dùng đúng ba endpoint này |
| Giờ không kèm múi giờ | Hiểu là giờ Việt Nam; có `Z` hoặc độ lệch thì đổi về UTC | Giao diện nên gửi giờ có độ lệch `+07:00` |
| Sửa bài đã phát hành | Đặc tả: chỉ sửa tên, mô tả, giờ đóng. Hiện thực bắt giáo viên **gửi lại nguyên giá trị** các trường khóa (PUT toàn bộ), khác giá trị thì 422; độ khó chung cũng khóa vì đặc tả không liệt kê vào phần sửa được | Xác nhận độ khó có nên sửa được sau phát hành không |
| Đóng bài có lượt đang dở | Đặc tả: lượt `IN_PROGRESS` "được chấm ngay ở trạng thái EXPIRED". Luồng làm bài chưa có nên hiện thực chỉ **tự nộp** (`EXPIRED`) và tính điểm từ các câu đã chấm của lượt (thường là 0 vì câu chấm lúc nộp); luồng làm bài sẽ chấm đủ các câu trả lời đã lưu | Làm khớp ở luồng làm bài |
| Số đã nộp trên sĩ số | Số lượt không còn `IN_PROGRESS` trên số học sinh đang hoạt động của lớp | — |
| Câu hỏi nằm trong bài kiểm tra đã xóa mềm | Đổi so với luồng ngân hàng câu hỏi: bài nháp xóa mềm **không còn giữ câu ở trạng thái chỉ đọc** (trước đó vẫn tính). Bài đã phát hành hoặc đã đóng không xóa được nên vẫn giữ câu chỉ đọc | — |
| Độ khó của bài học | Tính từ **mọi** bài kiểm tra chưa xóa của bài (kể cả nháp), mức hòa lấy cao hơn; câu bị ẩn vẫn tính | Xác nhận có nên chỉ tính bài đã phát hành |
| "Xem như học sinh" cho bài kiểm tra | Chưa làm: thuộc luồng học tập của học sinh (xem thử câu hỏi, không nộp được) | Làm cùng luồng học tập |
| Lớp đổi sang cấp khác khi đã có bài kiểm tra | Bài kiểm tra nháp giữ nguyên, câu hoặc thời gian làm không hợp cấp mới hiện cảnh báo và chặn phát hành; bài đã phát hành không đổi (đúng thông báo xác nhận đổi khối của luồng lớp) | — |
