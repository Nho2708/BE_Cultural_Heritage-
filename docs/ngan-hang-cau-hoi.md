# Luồng Ngân hàng câu hỏi theo dạng, AI sinh câu hỏi (Backend)

Phần backend của khu **Ngân hàng câu hỏi** của giáo viên (web quản trị) và việc **ẩn câu kém chất lượng** của quản trị hệ thống. Ngân hàng **dùng chung mọi trường**,
có **8 dạng câu** (mỗi dạng dùng được ở những cấp 1/2/3 nhất định); giáo viên tạo câu bằng trình soạn theo dạng hoặc nhờ **AI sinh câu** từ một bài học (đúng dạng và cấp của lớp),
duyệt câu AI trước khi dùng, nhân bản câu của người khác để sửa. Backend cũng có phép **chấm từng phần** theo dạng, dùng để chấm thử khi soạn và để kiểm câu hợp lệ;
luồng học tập của học sinh dùng lại đúng phép chấm này ([hoc-tap-theo-cap.md](hoc-tap-theo-cap.md)).

| Mục | Giá trị |
|---|---|
| Chức năng | Yêu cầu AI sinh câu hỏi · Duyệt câu hỏi do AI sinh · Quản lý câu hỏi trong ngân hàng · Soạn câu hỏi theo dạng của cấp học · Ẩn câu kém chất lượng (quản trị hệ thống) |
| Màn hình phục vụ | Web quản trị (giáo viên): Sinh và duyệt câu hỏi bằng AI, Ngân hàng câu hỏi (ba tab Dùng chung, Của tôi, Chờ duyệt), Biểu mẫu câu hỏi theo dạng (tạo, sửa, duyệt, chỉ đọc, xem trước). Quản trị hệ thống: xem câu chỉ đọc và Ẩn |
| Bảng | `questions`, `answers` (có từ v3, **thêm cột ở CSDL v4**), `ai_generations`; đọc `heritages`, `media`, `events`, `lessons`, `lesson_heritages`, `classes`, `users`, `organizations`, `quiz_questions`, `subscriptions`, `plans`; ghi `audit_logs` (ẩn câu) |
| Migration mới | `20261005212316_ThemDangCauHoiVaCapPhuHop`: 5 cột của `questions`, 4 cột của `answers`, `question_type` đủ 8 dạng, 3 CHECK mới, gán cấp phù hợp cho câu cũ. Chi tiết ở mục 6 |
| Lớp mới | `Domain/Rules/QuestionRules` (bảng dạng câu theo cấp, hằng số); `Application/Questions` (`QuestionBuilder` kiểm hợp lệ và dựng dòng đáp án, `QuestionGrader` chấm từng phần, `QuestionService`, `QuestionAiService`, `AdminQuestionService`, hợp đồng AI `IQuestionAiClient`); `Application/Lessons/AiHeritageInputBuilder` (tách ra từ soạn nội dung bài để hai luồng dùng chung); `Infrastructure/Ai` (`SimulatedQuestionAiClient`, `HttpQuestionAiClient`); `Api/Controllers/TeacherQuestionsController`, `TeacherQuestionAiController`, `AdminQuestionsController` |
| Nguồn | Đặc tả của nhóm (nhóm quy tắc Ngân hàng câu hỏi, bảng dạng câu theo cấp, hợp đồng backend và Dịch vụ AI trong kiến trúc), `doc/database/dsvhvn-v4.dbml` |

## 1. Phân quyền

| Vai trò | Được làm |
|---|---|
| Giáo viên (`TEACHER`) | Tìm, xem câu **đã duyệt** của mọi trường (kèm tên người tạo và tên trường); xem câu **chờ duyệt, bị loại** chỉ của mình; tạo câu thủ công; **sửa, xóa mềm, duyệt, loại** câu của mình chưa nằm trong quiz nào; nhân bản câu đã duyệt; gọi AI sinh câu từ bài học của mình |
| Giáo viên khác (kể cả cùng trường) | Câu chờ duyệt, bị loại của người khác trả **404** như không tồn tại. Câu đã duyệt của người khác xem được nhưng sửa, xóa, duyệt, loại trả **403** |
| Quản trị hệ thống (`ADMIN`) | Chỉ ở `/admin/questions`: xem **chỉ đọc** câu đã duyệt và câu đã bị ẩn, **ẩn** câu đã duyệt. Không tạo, sửa, duyệt. Không thấy câu chờ duyệt hay câu người tạo tự loại. Vào khu giáo viên trả 403 |
| Quản trị trường, học sinh | Không dùng ngân hàng (403). Học sinh chỉ thấy câu khi làm quiz và xem kết quả (luồng làm bài) |
| Chưa đăng nhập | 401 |

## 2. Endpoint

Khuôn phản hồi chung `{ success, data, message, errorCode, traceId, errors }`. Tiền tố `/api/v1`. Chữ hiển thị tiếng Việt, mã lỗi theo nghĩa. Giá trị enum viết HOA như trong CSDL
(`SINGLE_CHOICE`, `EASY`, `PENDING`...).

### 2.1 Giáo viên: ngân hàng câu hỏi — `/teacher/questions`

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/teacher/questions` | `scope` = `SHARED` (mặc định: câu đã duyệt của mọi trường) · `MINE` (câu của mình, lọc thêm `status`) · `PENDING` (câu AI chờ duyệt của mình); lọc `questionType`, `level` (1-3), `difficultyLevel`, `heritageId`, `source` (`MANUAL`/`AI`), `createdBy`, `keyword` (trong lời câu); `page`, `pageSize` | 200 trang `{ id, questionType, typeName, content, imageUrl, heritageId, heritageName, difficultyLevel, levelMin, levelMax, source, reviewStatus, createdBy, creatorName, organizationName, isMine, isHidden, quizCount, createdAt, updatedAt }`, mới tạo trước | 400 (`level` ngoài 1-3, `pageSize` ngoài 1-100, enum lạ) |
| GET | `/teacher/questions/counts` | — | 200 `{ shared, mine, pending, rejected }` (số đếm các tab) | — |
| GET | `/teacher/questions/types` | — | 200 `{ types[{ type, name, minLevel, maxLevel, multiPart }], byLevel{ "1": [...], "2": [...], "3": [...] } }` (bảng dạng câu theo cấp cho trình soạn) | — |
| POST | `/teacher/questions` | Nội dung câu (mục 3), `heritageId` bắt buộc | 201 chi tiết câu, **đã duyệt ngay** (`source` = `MANUAL`, người duyệt là người tạo) | 400 kèm lỗi theo trường (`options[1].content`, `pairs[0].right`, `steps[2]`, `blanks[0].accepted`, `statements[1].text`, `imageUrl`, `levelMin`...) |
| POST | `/teacher/questions/validate` | `{ question, previewLevel?, submission? }` | 200 `{ isValid, errors, partsTotal, preview, trial }`. **Không lưu**; câu chưa hợp lệ vẫn trả 200 với `isValid` = false để trình soạn hiện lỗi ngay khi gõ | 400 (thiếu `question`) |
| POST | `/teacher/questions/approve` | `{ questionIds[] }` (tối đa 100) | 200 `{ approved[], skipped[{ questionId, reason }], message }`; câu không duyệt được bị bỏ qua kèm lý do, các câu còn lại vẫn duyệt | 400 (rỗng, quá 100) |
| GET | `/teacher/questions/{id}` | — | 200 chi tiết có **đáp án**, ảnh kèm ghi công, `quizCount` và `access` (mục 4.3) | 404 |
| PUT | `/teacher/questions/{id}` | Như tạo; thay toàn bộ nội dung và đáp án; **trạng thái giữ nguyên** | 200 chi tiết | 400; 403 (không phải người tạo); 404; 422 (câu đã nằm trong quiz, đã bị ẩn, hoặc đổi dạng của câu do AI sinh) |
| DELETE | `/teacher/questions/{id}` | — | 200, xóa mềm | 403; 404; 422 (nằm trong quiz, đã bị ẩn) |
| POST | `/teacher/questions/{id}/approve` | — | 200 chi tiết `APPROVED`, ghi người và thời điểm duyệt; câu đã duyệt giữ nguyên | 403; 404; 422 (đã bị ẩn; kiểm lại cấu trúc không đạt, kèm `errors`) |
| POST | `/teacher/questions/{id}/reject` | — | 200 chi tiết `REJECTED`, ghi người và thời điểm | 403; 404; 422 (chỉ loại được câu chờ duyệt) |
| POST | `/teacher/questions/{id}/duplicate` | — | 201 câu mới của người nhân bản: `MANUAL`, đã duyệt ngay, chép dạng, đáp án, giải thích, độ khó, di sản, cấp phù hợp | 404; 422 (câu bị ẩn, câu chưa duyệt, di sản không còn xuất bản) |
| GET | `/teacher/questions/{id}/preview` | `level` (mặc định cấp thấp nhất của câu) | 200 câu **như học sinh của cấp thấy**: không có đáp án đúng, giải thích; ghép cặp, sắp xếp, kho từ đã xáo ổn định; `layout` (`LARGE_CARDS` cấp 1, `STANDARD` cấp 2, `COMPACT` cấp 3), `readAloud` (cấp 1) | 400 (cấp ngoài cấp phù hợp của câu); 404 |
| POST | `/teacher/questions/{id}/try-grade` | Cách làm `{ selectedIndex?, matches?, order?, blanks?, statements? }` | 200 `{ partsCorrect, partsTotal, parts[], isCorrect, fraction }`, theo đúng phép chấm của bài kiểm tra, không ghi gì | 404 |

### 2.2 Giáo viên: AI sinh câu hỏi — `/teacher/lessons/{lessonId}/ai/questions`

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| POST | `/teacher/lessons/{lessonId}/ai/questions` | `{ count?, difficultyLevel?, types?[], heritageId? }`: `count` 1-20 (mặc định 5), độ khó (mặc định `MEDIUM`), `types` trong các dạng của **cấp của lớp** (để trống thì lấy mọi dạng của cấp), `heritageId` trong các di sản đang xuất bản của bài (để trống thì dùng cả bài) | 200 `{ generationId, level, levelName, requested, received, dropped, questions[chi tiết, PENDING], aiQuota, model, message }` | 400 (`count`, `types` ngoài cấp, `heritageId` ngoài bài); 404 (bài không phải của mình); 422 `PLAN_REQUIRED`, `AI_QUOTA_REACHED`, `BLOCKED` (bài Lưu trữ, bài không còn di sản đang xuất bản); 503 `AI_UNAVAILABLE` |
| GET | `/teacher/lessons/{lessonId}/ai/questions/quota` | — | 200 `{ hasEffectivePlan, planName, used, max, remaining, periodStart, periodEnd }` (cùng hạn mức với soạn nội dung bài) | — |

### 2.3 Quản trị hệ thống — `/admin/questions`

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/admin/questions` | Như danh sách của giáo viên (không có `scope`); `status` = `APPROVED` hoặc `REJECTED` (câu đã bị ẩn) | 200 trang; chỉ có câu đã duyệt và câu đã bị ẩn | 400 |
| GET | `/admin/questions/{id}` | — | 200 chi tiết chỉ đọc (`access.canHide` khi câu đang được duyệt) | 404 (kể cả câu chờ duyệt) |
| POST | `/admin/questions/{id}/hide` | `{ reason? }` (tối đa 500 ký tự) | 200 chi tiết, câu thành `REJECTED`, `reviewedBy` là quản trị hệ thống; ghi `audit_logs` | 400; 404; 422 (câu chưa duyệt hoặc đã bị ẩn) |

## 3. Tám dạng câu hỏi và cấp phù hợp

Cấp phù hợp của câu là khoảng `levelMin`..`levelMax` (1-3). **Phải nằm trong các cấp mà dạng cho phép**; để trống thì lấy đủ các cấp đó, có thể thu hẹp (ví dụ câu sắp xếp chỉ cấp 2).
Quiz của lớp cấp *c* chỉ nhận câu có `levelMin` ≤ *c* ≤ `levelMax` (kiểm ở luồng quiz). Câu AI sinh cho một bài có cấp phù hợp đúng bằng cấp của lớp.

| Dạng (`questionType`) | Tên | Cấp | Số phần chấm | Cấu trúc đáp án khi gửi lên | Lưu ở `answers` |
|---|---|---|---|---|---|
| `SINGLE_CHOICE` | Một đáp án đúng | 2-3 | 1 | `options`: đúng 4 phương án (1-1000 ký tự, không trùng), đúng một `isCorrect` | mỗi dòng một phương án |
| `TRUE_FALSE` | Đúng/Sai | 1-2 | 1 | `answerIsTrue`; `imageUrl` tùy chọn nhưng **bắt buộc khi câu dùng được ở cấp 1** | hai dòng "Đúng", "Sai" theo thứ tự, một dòng `is_correct`; hình ở `questions.image_url` |
| `IMAGE_CHOICE` | Chọn hình đúng | 1 | 1 | `options`: 3-4 thẻ, mỗi thẻ `imageUrl` khác nhau và nhãn 1-50 ký tự (không trùng), đúng một thẻ đúng | `image_url` + `content` (nhãn) |
| `MATCHING` | Ghép cặp hình - tên | 1 | số cặp | `pairs`: 3-5 cặp `{ left?, leftImageUrl?, right }`; vế trái là chữ hoặc hình, vế phải 1-50 ký tự; các vế trái không trùng, các vế phải không trùng | mỗi dòng một cặp: `content`/`image_url` (trái), `match_content` (phải), `is_correct` = 1 |
| `ORDERING` | Sắp xếp | 1-2 | số mục | `steps`: 3-6 mục (1-200 ký tự, không trùng) **liệt kê theo thứ tự đúng**; câu dùng được ở cấp 1 tối đa 4 mục | `content`, `correct_position` = 1..n |
| `FILL_BLANK` | Điền từ | 2 | số chỗ trống | `content` có 1-3 chỗ trống đánh dấu bằng **ba dấu gạch dưới liền nhau** (`___`); `blanks[i].accepted`: 1-5 cách viết đúng (1-100 ký tự, không trùng) cho chỗ trống thứ *i*; `distractors` (từ gây nhiễu) bật **kho từ** 3-8 từ | dòng cách viết đúng: `blank_index`, `is_correct` = 1; dòng từ gây nhiễu: `blank_index` NULL, `is_correct` = 0 |
| `MULTI_TRUE_FALSE` | Nhiều mệnh đề Đúng/Sai | 3 | số mệnh đề | `content` là đoạn dẫn; `statements`: 3-5 mệnh đề `{ text (10-500 ký tự), isTrue }`, không để mọi mệnh đề cùng Đúng hoặc cùng Sai | mỗi dòng một mệnh đề, `is_correct` = mệnh đề đúng |
| `SOURCE_READING` | Đọc tư liệu rồi trả lời | 3 | 1 | `passage` (tối đa 150 từ) và `passageSource` (nguồn) **bắt buộc**; `content` là câu hỏi con; `options` như một đáp án đúng (không có câu con điền từ) | `questions.passage`, `questions.passage_source`; phương án như `SINGLE_CHOICE` |

Chung cho mọi dạng: `content` 10-1000 ký tự; `explanation` (giải thích, tối đa 2000 ký tự) và `difficultyLevel` bắt buộc; `heritageId` bắt buộc và là di sản **đang xuất bản**;
**mọi ảnh** (`imageUrl`, thẻ hình, vế trái hình) phải là ảnh trong **thư viện ảnh của di sản được gắn** (ảnh của di sản và ảnh của các sự kiện trên dòng thời gian), nên luôn có ghi công;
trường của dạng khác bị bỏ qua. Mọi lần lưu còn **chấm thử đáp án chuẩn** (mục 4.6): câu phải có ít nhất một phần, đáp án chuẩn phải đúng đủ, câu bỏ trống phải 0 điểm.

Chi tiết trả về có thêm: `partsTotal`, `levels`, `wordBank` (kho từ học sinh thấy: từ đúng đầu tiên của mỗi chỗ trống cộng từ gây nhiễu), `images[{ url, caption, credit }]`, `creatorName`,
`organizationName`, `quizCount`, `access`.

## 4. Quy tắc

### 4.1 Trạng thái

`PENDING` (chờ duyệt) · `APPROVED` (đã duyệt) · `REJECTED` (bị loại). Câu **tự tạo** vào ngân hàng ở `APPROVED` ngay (người duyệt là người tạo, thời điểm duyệt là lúc tạo); câu **AI sinh** ở `PENDING`
(người tạo là giáo viên yêu cầu, `ai_generation_id` là lần gọi sinh ra câu). Chỉ câu `APPROVED` vào được quiz. Sửa câu **không đổi** trạng thái (sửa câu chờ duyệt vẫn chờ duyệt, phải bấm Duyệt).
Câu `REJECTED` do người tạo loại sửa được và duyệt lại; câu `APPROVED` không loại ngược về `REJECTED` bởi người tạo (muốn bỏ thì xóa mềm nếu chưa nằm trong quiz).

**Duyệt kiểm lại** cấu trúc theo quy tắc hiện hành (di sản còn xuất bản, ảnh còn trong thư viện, đúng cấu trúc theo dạng); không đạt thì 422 kèm lỗi theo trường và câu giữ nguyên. Duyệt nhiều câu một lần:
các câu duyệt được vẫn được duyệt, câu không duyệt được nằm ở `skipped`.

### 4.2 Ngân hàng dùng chung mọi trường

Câu `APPROVED` chưa xóa hiện cho **mọi giáo viên mọi trường** tìm, xem, nhân bản, kèm họ tên người tạo và tên trường (câu của tài khoản đã xóa hay trường đã ngừng vẫn dùng được). Câu `PENDING`, `REJECTED` chỉ người tạo thấy.
Quản trị trường và học sinh không dùng ngân hàng. Bộ lọc: dạng, cấp, độ khó, di sản, trạng thái (tab Của tôi), nguồn, người tạo (tab Dùng chung), từ khóa trong lời câu
(không phân biệt hoa thường, **phân biệt dấu** theo collation `Vietnamese_CI_AI` của SQL Server: "hoa lư" tìm được "Hoa Lư", "hoa lu" thì không).

### 4.3 Câu chỉ đọc

`access` trong chi tiết cho giao diện bật tắt nút. Câu **chỉ đọc** (`isReadOnly`) khi:

| `reason` | Khi nào | Thông báo hiện ở đầu câu |
|---|---|---|
| `USED_IN_QUIZ` | Câu của mình nhưng đã nằm trong ít nhất một quiz chưa xóa (mọi trạng thái, mọi trường). Quiz nháp đã xóa mềm không còn giữ câu: quiz nháp chưa có lượt làm bài nên kết quả cũ không bị ảnh hưởng, còn quiz đã phát hành hoặc đã đóng không xóa được | "Câu hỏi này chỉ xem được vì đã được dùng trong bài kiểm tra. Bấm "Nhân bản" để tạo bản của bạn rồi sửa." |
| `NOT_OWNER` | Câu do người khác tạo | "Câu hỏi này chỉ xem được vì do người khác tạo. Bấm "Nhân bản" để tạo bản của bạn rồi sửa." |
| `HIDDEN_BY_ADMIN` | Quản trị hệ thống đã ẩn câu | "Câu hỏi này chỉ xem được vì đã bị quản trị hệ thống ẩn. Câu bị ẩn không nhân bản được; bạn có thể tạo câu mới." |

Sửa hoặc xóa câu chỉ đọc: người không phải người tạo nhận **403**; người tạo nhận **422** `BLOCKED` ("Không thể sửa câu hỏi. Câu hỏi đã nằm trong bài kiểm tra. Bấm "Nhân bản" để tạo bản của bạn rồi sửa.").
Câu đã nằm trong quiz **không đổi dạng và cấp**: không sửa được là không đổi được.

### 4.4 Nhân bản

Nhân bản câu `APPROVED` (của mình hoặc dùng chung) tạo câu mới do người nhân bản sở hữu: `MANUAL`, duyệt ngay, `ai_generation_id` NULL; chép dạng, đáp án, giải thích, độ khó, di sản, cấp phù hợp; câu gốc không đổi.
Nội dung được kiểm lại như khi tạo, nên di sản đã ẩn thì không nhân bản được (422). **Không nhân bản được câu bị quản trị hệ thống ẩn**, kể cả người tạo (422); câu chưa duyệt của người khác là 404.

### 4.5 Ẩn câu kém chất lượng (quản trị hệ thống)

Chuyển câu `APPROVED` sang `REJECTED` với `reviewed_by` là tài khoản quản trị hệ thống, ghi `audit_logs` mã `QUESTION_HIDDEN` (đối tượng `questions`, giá trị trước/sau, lý do, IP). Câu bị ẩn biến khỏi tab Dùng chung,
không chọn được vào quiz mới, không chép khi nhân bản quiz hay bài học; quiz đã phát hành và kết quả cũ giữ nguyên. Với người tạo, câu hiện ở tab Của tôi (bị loại, chỉ đọc), không duyệt lại, không nhân bản.
Phân biệt câu bị ẩn với câu người tạo tự loại: câu `REJECTED` có người duyệt là quản trị hệ thống.

### 4.6 Chấm từng phần và chấm thử

`QuestionGrader` (một chỗ duy nhất, chạy từ các dòng `answers` đã lưu) theo quy tắc Cách chấm từng dạng câu:

- Điểm một câu = điểm tối đa × số phần đúng ÷ tổng số phần, làm tròn 2 chữ số thập phân (nửa lên). Không trừ điểm khi sai; bỏ trống 0 điểm.
- Một đáp án đúng, Đúng/Sai, chọn hình, đọc tư liệu: **một phần**. Ghép cặp: mỗi cặp nối đúng một phần (mỗi vế phải chỉ nối một lần). Sắp xếp: mỗi mục ở đúng **vị trí tuyệt đối** một phần; chưa sắp xếp lần nào thì bỏ trống.
  Điền từ: mỗi chỗ trống một phần; khớp một cách viết đúng sau khi bỏ khoảng trắng đầu cuối, gộp khoảng trắng liên tiếp, **không phân biệt hoa thường nhưng phân biệt dấu**, chữ có dấu dựng sẵn hay tổ hợp so sánh như nhau.
  Nhiều mệnh đề: mỗi mệnh đề chọn đúng một phần, mệnh đề chưa chọn không được.
- Cách làm gửi lên theo **vị trí trong thứ tự lưu** (từ 0): `selectedIndex`; `matches[i]` = vị trí vế phải đã nối với vế trái *i*; `order` = các vị trí mục theo thứ tự học sinh đặt; `blanks[j]` = chữ ở chỗ trống *j*; `statements[i]` = Đúng/Sai.
- **Kiểm hợp lệ bằng chấm thử** (`QuestionGrader.SelfCheck`) chạy trong mọi lần dựng câu: câu phải có ít nhất một phần; đáp án chuẩn dựng từ chính các dòng đáp án phải chấm ra đúng đủ mọi phần; câu bỏ trống phải 0 phần.
- Luồng làm bài (chưa có ở nhánh này) sẽ ghi số phần đúng, tổng số phần của từng câu theo cột `attempt_answers.parts_total`, `parts_correct` và bảng `attempt_answer_parts` của CSDL v4 (xem mục 6).

### 4.7 Gói dịch vụ

Trường **không có gói hiệu lực** thì chặn **gọi AI sinh câu** (422 `PLAN_REQUIRED`). Xem ngân hàng, tạo thủ công, sửa, duyệt, nhân bản **vẫn dùng được**. Học sinh không bị ảnh hưởng.

## 5. AI sinh câu hỏi

### 5.1 Dịch vụ AI chưa có: giao diện + bản giả lập

Dịch vụ AI thật chưa được làm. Backend gọi AI qua giao diện `IQuestionAiClient` theo hợp đồng `POST /v1/generate-questions`, **cùng cách với soạn nội dung bài học** (cùng mục cấu hình `Ai`):

| `Ai:Mode` | Bản dùng | Ghi chú |
|---|---|---|
| `Simulated` (**mặc định**) | `SimulatedQuestionAiClient` | **Bản giả lập** tất định: ghép câu từ chính dữ liệu di sản đã gửi (nội dung, dòng thời gian, nguồn, ảnh có sẵn) đúng cấu trúc của từng dạng. Không gọi mạng, **không gọi nhà cung cấp mô hình nào, không dùng khóa API nào** |
| `Remote` | `HttpQuestionAiClient` | Gọi Dịch vụ AI nội bộ: `Ai:BaseUrl`, `Ai:InternalKey` (biến môi trường `Ai__InternalKey`, không ghi vào appsettings); chờ 45 giây mỗi lần, thử lại tối đa 2 lần |

Bản giả lập sinh các dạng lần lượt theo vòng và các di sản theo vòng; câu thứ hai của cùng dạng và di sản lấy ý khác. Dạng thiếu dữ liệu (chọn hình và ghép cặp cần ít nhất 3 ảnh trong thư viện, sắp xếp sự kiện cần ít nhất 3 mốc
khác năm, điền từ và đọc tư liệu cần nội dung di sản) bị bỏ qua nên có thể nhận **ít câu hơn** yêu cầu; không dạng nào làm được thì lỗi không thử lại (503 sau khi đóng dòng nhật ký `FAILED`).
Phương án gây nhiễu của bản giả lập là câu nói sai mẫu về di sản; bản thật dùng mô hình ngôn ngữ và giáo viên vẫn phải duyệt từng câu.

### 5.2 Hợp đồng gọi

`POST {Ai:BaseUrl}/v1/generate-questions`, JSON UTF-8 camelCase, header `X-Internal-Key` và `X-Correlation-Id`.

Gửi đi (`AiQuestionRequest`): `generationId`, `lessonId`, `gradeLevel` (1, 2, 3), `count`, `difficulty`, `types[]` (đều dùng được ở `gradeLevel`), và `heritages[]` như hợp đồng soạn bài:
`{ heritageId, name, shortDescription, content, timeline[], sources[], images[{ mediaId, url, caption }] }`. **Không gửi** họ tên, mã học sinh, tên lớp, email. Bản sao lưu ở `ai_generations.input_snapshot`.

Nhận về (`AiQuestionResult`): `questions[]`, `model`, `usage{ inputTokens, outputTokens }`. Mỗi câu `{ heritageId, questionType, content, explanation, imageUrl, passage, passageSource, answerIsTrue, options[], pairs[], steps[], blanks[], distractors[], statements[] }`
cùng khuôn đáp án với mục 3 (không có cấp và độ khó: backend đặt cấp của lớp và độ khó đã yêu cầu). Mã phản hồi: `5xx`, `408`, `429`, lỗi mạng, quá thời gian, kết quả sai lược đồ là lỗi **thử lại được**; `400`, `401`, `403`, `422` thì không.

### 5.3 Quy trình mỗi lần gọi

1. Kiểm bài: của giáo viên, **Nháp hoặc Đã xuất bản** (Lưu trữ thì không; không đòi bài đã xuất bản vì câu vào ngân hàng chờ duyệt, không gắn vào bài), có ít nhất một di sản đang xuất bản. Kiểm `count`, `types` (đều thuộc dạng của cấp lớp), `heritageId`.
2. **Giữ chỗ lượt AI** trong một giao dịch có khóa dòng trường: gói hiệu lực (422 `PLAN_REQUIRED`), còn lượt (422 `AI_QUOTA_REACHED`), thêm dòng `ai_generations` `PROCESSING` (`task_type` = `QUIZ_GENERATION`, `lesson_id`).
3. Gọi dịch vụ AI ngoài giao dịch, chờ 45 giây mỗi lần, thử lại tối đa 2 lần trong cùng một dòng nhật ký.
4. **Kiểm từng câu** (mục 5.4), bỏ câu không đạt; không còn câu nào là lỗi đầu ra (thử lại như lỗi mạng).
5. Thành công: trong **một lần lưu**, thêm các câu (`PENDING`, `AI`, người tạo là giáo viên, `ai_generation_id`) và đóng dòng nhật ký `SUCCEEDED` (`model`, `raw_output`, `completed_at`): hoặc có cả hai, hoặc không có gì.
6. Hết lượt thử: dòng `FAILED` kèm `error_message`, **không tính lượt**, không thêm câu nào, 503 `AI_UNAVAILABLE`. "Tạo lại" là một lần gọi mới: một lượt, một dòng nhật ký mới.

### 5.4 Kiểm kết quả

Mỗi câu AI trả về phải: có dạng nằm trong các dạng đã chọn; gắn một di sản đã gửi; chỉ dùng ảnh trong danh sách ảnh đã gửi của di sản đó; đạt quy tắc cấu trúc (mục 3) với cấp phù hợp là **cấp của lớp** và độ khó đã chọn;
không trùng một câu đã nhận (cùng dạng, di sản, lời câu, hình, đáp án). Nhận tối đa `count` câu; phần thừa và câu không đạt tính vào `dropped`. `received` là số câu thực sự vào ngân hàng.

### 5.5 Hạn mức lượt AI

Dùng chung hạn mức với soạn nội dung bài học (`PlanGuard`): kỳ của gói hiệu lực, giờ Việt Nam, mọi giáo viên của trường cùng đếm, mỗi lần gọi (kể cả "Tạo lại") là **một lượt bất kể số câu**,
`FAILED` không tính, dòng `PROCESSING` quá 5 phút coi là bỏ dở. Dịch và tóm tắt di sản của quản trị hệ thống không tính vào hạn mức trường.

## 6. Migration CSDL v4: `ThemDangCauHoiVaCapPhuHop`

Đưa vào đúng phần của `dsvhvn-v4.dbml` mà luồng cần. Bảng không đổi tên, chỉ thêm cột và ràng buộc.

| Thay đổi | Chi tiết |
|---|---|
| `questions` thêm cột | `image_url nvarchar(500)` (hình minh họa của câu), `passage nvarchar(max)` (đoạn tư liệu), `passage_source nvarchar(500)`, `level_min tinyint NOT NULL DEFAULT 1`, `level_max tinyint NOT NULL DEFAULT 3` |
| `answers` thêm cột | `image_url nvarchar(500)`, `match_content nvarchar(1000)` (vế phải của cặp), `correct_position tinyint` (vị trí đúng), `blank_index tinyint` (chỗ trống) |
| `question_type` | `CK_questions_question_type` nhận đủ 8 giá trị; enum `QuestionType` thêm `IMAGE_CHOICE`, `MATCHING`, `ORDERING`, `FILL_BLANK`, `MULTI_TRUE_FALSE`, `SOURCE_READING` |
| CHECK mới | `CK_questions_levels` (cấp 1-3 và `level_min` ≤ `level_max`) · `CK_questions_passage` (dạng đọc tư liệu bắt buộc có `passage`) · `CK_answers_positions` (`correct_position`, `blank_index` NULL hoặc 1..20) |
| Câu cũ lên v4 | Gán cấp phù hợp mặc định theo bảng dạng câu theo cấp: `SINGLE_CHOICE` → cấp 2-3; `TRUE_FALSE` → cấp 2 (câu Đúng/Sai cũ chưa có hình nên chưa dùng được ở cấp 1 cho tới khi người tạo thêm hình) |
| **Thêm sau, ở luồng học tập** | `attempt_answers.parts_total`, `parts_correct` và bảng `attempt_answer_parts` của v4: migration `ThemCauTraLoiTungPhan` (xem [hoc-tap-theo-cap.md](hoc-tap-theo-cap.md)), dùng lại đúng phép chấm của luồng này |
| Dạng nào dùng ở cấp nào | Kiểm ở tầng code (bảng `QuestionTypes`), không có CHECK trong CSDL, đúng ghi chú của DBML |

Số liệu sau migration (kiểm bằng `SqlServerDatabaseTests` và `QuestionSqlServerTests`): 28 bảng, **257 cột**, 43 khóa ngoại, **25 CHECK**, 4 UNIQUE có lọc.
`QuestionSqlServerTests` còn kiểm CSDL thật từ chối cấp sai, thiếu đoạn tư liệu, dạng lạ, vị trí ngoài 1..20, và đường nâng cấp: dựng database tạm tới migration trước, chèn câu cũ, chạy migration mới, kiểm cấp mặc định.

## 7. Nhật ký

- **Lượt gọi AI** ghi ở `ai_generations` (`QUIZ_GENERATION`: đầu vào, tham số, mô hình, kết quả thô, trạng thái, lỗi). Câu sinh ra truy được về lần gọi qua `questions.ai_generation_id`.
- **Ẩn câu** ghi `audit_logs` mã `QUESTION_HIDDEN` (có sẵn trong danh sách 21 mã của đặc tả). Tạo, sửa, duyệt, loại, xóa, nhân bản câu **không** vào `audit_logs` vì đặc tả không liệt kê; dấu vết duyệt nằm ở `reviewed_by`, `reviewed_at`.

## 8. Mã lỗi

| Mã HTTP | `errorCode` | Khi nào |
|---|---|---|
| 400 | `REQUIRED`, `INVALID` | Thiếu hoặc sai trường; sai cấu trúc theo dạng (kèm `errors` theo trường); ảnh ngoài thư viện; di sản không xuất bản; `count`, `types`, `level`, phân trang sai |
| 401 / 403 | `FORBIDDEN`... | Chưa đăng nhập; sai vai trò; không phải người tạo khi sửa, xóa, duyệt, loại |
| 404 | `BLOCKED` | Câu không tồn tại, đã xóa, hoặc chờ duyệt, bị loại của người khác; bài học không phải của giáo viên |
| 422 | `BLOCKED` | Câu đã nằm trong quiz; câu bị ẩn; đổi dạng của câu AI; loại câu không chờ duyệt; nhân bản câu chưa duyệt; duyệt câu không còn đạt; gọi AI khi bài Lưu trữ hoặc không còn di sản đang xuất bản |
| 422 | `PLAN_REQUIRED`, `AI_QUOTA_REACHED` | Trường không có gói hiệu lực hoặc hết lượt AI |
| 503 | `AI_UNAVAILABLE` | Dịch vụ AI quá thời gian chờ hoặc lỗi sau các lần thử |

## 9. Chạy thử và kiểm thử

```bash
dotnet test                          # có LocalDB thì chạy thêm các test trên database tạm của SQL Server (tự xóa)
# Smoke test qua HTTP thật trên database tạm (không gửi thư thật: Email__Mode=Log; AI chạy bản giả lập):
ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_kiem_thu_smoke;Trusted_Connection=True;TrustServerCertificate=True" \
Email__Mode=Log Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5092 > api.log 2>&1 &
BASE=http://localhost:5092 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-ngan-hang-cau-hoi.sh
```

Smoke (`scripts/smoke-ngan-hang-cau-hoi.sh`, 134 bước): xuất bản một di sản và thêm ảnh vào thư viện; tạo trường, hai giáo viên, ba lớp (cấp 1, 2, 3) và ba bài; tạo thủ công đủ 8 dạng; lỗi theo trường (cấu trúc, ảnh ngoài thư viện, cấp sai);
kiểm không lưu, chấm thử (hoa thường, sai dấu), xem trước theo cấp; AI sinh 12 câu cho mỗi cấp và kiểm dạng đúng cấp, dạng sai cấp bị từ chối, hạn mức lượt AI; duyệt, loại, duyệt nhiều câu, câu chờ duyệt không lộ cho giáo viên khác;
tìm và lọc, số đếm tab; nhân bản; quản trị hệ thống xem, ẩn, nhật ký, câu bị ẩn biến khỏi ngân hàng và người tạo chỉ đọc; phân quyền năm vai trò; đổi dạng câu thủ công, xóa mềm. Bài kiểm tra chưa có API nên phần "câu nằm trong quiz thì chỉ đọc" nằm trong bộ test tích hợp.

Bộ test (xUnit) của luồng: bảng dạng câu theo cấp và kiểm hợp lệ từng dạng (số phần tử, độ dài, không trùng, đúng một đáp án đúng, ảnh trong thư viện, cấp phù hợp, câu Đúng/Sai cấp 1 bắt buộc hình, sắp xếp cấp 1 tối đa 4 mục, kho từ 3-8);
chấm từng phần (mọi dạng, hoa thường, sai dấu, chữ dựng sẵn và tổ hợp, làm tròn, vế phải nối hai lần, mục sắp xếp lặp, chấm thử đáp án chuẩn); bản giả lập AI (mọi cấp, mọi dạng đạt quy tắc, tất định, ảnh chỉ lấy từ ảnh đã gửi, thiếu dữ liệu);
bản gọi HTTP với máy chủ giả (đường dẫn, header, đủ các dạng đáp án, phân loại lỗi); API qua `WebApplicationFactory` (tạo, đọc, sửa, xóa đủ 8 dạng, quyền xem và sửa giữa các trường và giữa các giáo viên, chỉ đọc khi nằm trong quiz, nhân bản,
tìm và lọc, xem trước, chấm thử, kiểm không lưu, AI sinh theo cấp, hạn mức, thử lại và lỗi, câu AI sai bị bỏ, duyệt, loại, duyệt nhiều câu, quản trị hệ thống ẩn câu); trên SQL Server LocalDB: lược đồ do migration tạo, ràng buộc CHECK do CSDL thực thi,
**nâng cấp câu cũ lên v4**, tìm theo collation tiếng Việt, **nhiều yêu cầu AI cùng lúc không vượt hạn mức**.

## 10. Điểm hiện thực cần đồng bộ với đặc tả

| Điểm | Hiện thực | Cần làm |
|---|---|---|
| Đánh dấu chỗ trống của câu điền từ | Quy tắc Cấu trúc câu hỏi ghi **ba dấu gạch dưới liền nhau**; ghi chú cột `questions.content` trong DBML v4 ghi `[1]`, `[2]`. Hiện thực theo quy tắc: `___`, chỗ trống thứ *i* ứng với `blank_index` = *i* theo thứ tự xuất hiện | Thống nhất ghi chú DBML với quy tắc |
| Số cặp, số mệnh đề, số mục | Màn hình Biểu mẫu câu hỏi ghi ghép cặp 3-6 cặp, nhiều mệnh đề 2-5; quy tắc ghi ghép cặp 3-5, nhiều mệnh đề 3-5, sắp xếp 3-6 (cấp 1: 3-4). Hiện thực theo **quy tắc** | Sửa màn hình cho khớp |
| AI sinh từ "bài học hoặc di sản" | Chỉ từ **bài học** (như màn hình Sinh và duyệt câu hỏi bằng AI): cấp lấy từ lớp của bài, di sản chọn trong các di sản của bài. Ràng buộc `CK_ai_generations_subject` của v4 cũng buộc tác vụ sinh câu gắn bài học | Giữ như đặc tả; muốn sinh từ di sản không qua bài học thì phải sửa CHECK |
| Sắp xếp sự kiện theo thời gian (cấp 2) | Mỗi mục là một câu chữ; chưa lưu năm riêng cho từng mục nên chưa kiểm "không có hai mục cùng năm" (bản giả lập AI chỉ lấy các sự kiện khác năm) | Quyết định có thêm cột năm vào `answers` không |
| Kho từ của câu điền từ | Kho gồm từ đúng **đầu tiên** của mỗi chỗ trống cộng từ gây nhiễu (3-8 từ); các cách viết đúng khác chỉ dùng khi gõ chữ | Xác nhận với giao diện học sinh |
| Câu nằm trong quiz đã xóa mềm | Không tính: chỉ quiz nháp xóa mềm được (chưa có kết quả) nên câu được mở khóa lại (đổi ở luồng Bài kiểm tra) | — |
| Độ khó khi AI sinh | Không chọn thì mặc định `MEDIUM`; số câu mặc định 5 | — |
| Ghi nhật ký thao tác | Chỉ ẩn câu (`QUESTION_HIDDEN`) theo danh sách hành động của đặc tả | Quyết định của nhóm |
| `attempt_answers.parts_*`, `attempt_answer_parts` | Đã thêm ở luồng học tập | Migration `ThemCauTraLoiTungPhan` |
