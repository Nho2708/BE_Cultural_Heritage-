# Luồng Học tập của học sinh theo cấp: làm bài kiểm tra, chấm từng phần, kết quả theo cấp (Backend)

Phần backend của khu học của học sinh (web học tập và chế độ Học sinh của app dùng chung một API): em mở **trang học** của mình, vào **bài kiểm tra** của bài học đã mở bằng mã,
**làm bài một lượt** (đồng hồ, lưu từng câu, vào tiếp khi mất kết nối, hết giờ máy chủ tự nộp), trả lời **cả tám dạng câu**, được **chấm từng phần** bằng đúng bộ chấm của ngân hàng câu hỏi,
xem **kết quả theo cấp của lớp** (cấp 1 sao và huy hiệu, không điểm; cấp 2 điểm và giải thích từng câu; cấp 3 thêm phân tích theo chủ đề), và xem **lịch sử bài làm, tiến độ, huy hiệu** của chính em.
Việc học các bước của bài (nghe chuyện, đọc bài, dòng thời gian) **không ghi vào CSDL**; chỉ lượt làm bài được ghi.

| Mục | Giá trị |
|---|---|
| Chức năng | Trang học của em · Vào quiz và bắt đầu làm bài · Lưu câu trả lời từng câu (tám dạng) · Nộp bài, hết giờ tự nộp · Kết quả theo cấp · Lịch sử bài làm · Tiến độ, mức thành thạo, huy hiệu |
| Màn hình phục vụ | Web học tập và app (chế độ Học sinh): Trang học, Giới thiệu bài kiểm tra, Làm bài kiểm tra, Kết quả bài làm, Lịch sử bài làm, Tiến độ của em |
| Bảng | `attempts`, `attempt_answers` (v3), **`attempt_answer_parts`** (mới, v4) và hai cột mới `attempt_answers.parts_total`, `parts_correct`; đọc `quizzes`, `quiz_questions`, `questions`, `answers`, `lessons`, `classes`, `students`, `heritages` |
| Migration mới | `ThemCauTraLoiTungPhan`: hai cột `parts_total smallint NOT NULL DEFAULT 1`, `parts_correct smallint NOT NULL DEFAULT 0` và CHECK `CK_attempt_answers_parts` ở `attempt_answers`; bảng `attempt_answer_parts` (CHECK `CK_attempt_answer_parts_target`, hai UNIQUE có lọc `UX_attempt_answer_parts_answer`, `UX_attempt_answer_parts_blank`) |
| Lớp mới | `Domain/Rules/AttemptRules` (hạn nộp, ân hạn, đồng hồ theo cấp, phần trăm, sao, huy hiệu, mức thành thạo); `Application/Attempts`: `AttemptService`, `AttemptResultService`, `StudentLearningService`, `AttemptLifecycle`, `AttemptFinisher`, `AttemptLayout` (xáo tất định và mã hóa câu trả lời); `Api/Controllers/StudentAttemptsController`; tác vụ nền mở rộng ở `QuizAutoCloseService` |
| Dùng lại | `QuestionGrader` (chấm tám dạng theo phần, `Score` làm tròn 2 chữ số), `QuestionShape`, `QuestionPreviewBuilder.LayoutOf` |
| Nguồn | Đặc tả của nhóm: nhóm quy tắc Làm bài và kết quả (Điều kiện vào làm quiz, Một lượt, hạn nộp và tự nộp, Chấm điểm và trang kết quả), Tiến độ và mức thành thạo, Huy hiệu tính khi hiển thị, Cách hiện kết quả theo cấp, Luồng học theo cấp; `doc/database/dsvhvn-v4.dbml` |

## 1. Phân quyền

| Vai trò | Được làm |
|---|---|
| Học sinh đã đăng nhập và đã đổi mật khẩu (`STUDENT`) | Mọi endpoint ở mục 2, **chỉ với quiz của lớp mình và lượt của chính mình** |
| Học sinh khác (kể cả cùng lớp) | Lượt của em khác trả **404** như không tồn tại, ở mọi endpoint (đọc, lưu, nộp, kết quả) |
| Học sinh bị buộc đổi mật khẩu | 403 yêu cầu đặt mật khẩu mới (như các endpoint khu học khác) |
| Giáo viên, quản trị trường, quản trị hệ thống | **403** |
| Chưa đăng nhập | 401 |

Học sinh bị vô hiệu, lớp đã xóa hay trường đã ngừng thì không đăng nhập được (kiểm ở mỗi yêu cầu); mở lại thì xem lại được kết quả cũ. Gói của trường hết hạn **không** chặn làm bài.

## 2. Endpoint

Khuôn phản hồi chung `{ success, data, message, errorCode, traceId, errors }`. Tiền tố `/api/v1/student`. Enum viết HOA. Giờ trả về là UTC (`Z`).

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/home` | Trang học: `{ studentId, studentCode, fullName, className, grade, level, levelName, layout, teacherName, totalStars (chỉ cấp 1), inProgress[], openQuizzes[], myLessons[], recentBadges[] }` | 200 | 401, 403 |
| GET | `/quizzes/{quizId}` | Giới thiệu quiz: `{ name, description, lessonId, lessonTitle, level, layout, questionCount, totalPoint, timeLimitMinutes, showsClock, openAt, closeAt, state, action, actionLabel, attemptId, attemptStatus, notice }` | 200 | 404 |
| POST | `/quizzes/{quizId}/attempts` | Bắt đầu làm bài, không thân. Có lượt đang làm thì trả đúng lượt đó | **201** lượt mới; **200** vào tiếp lượt cũ (`StudentAttemptDto`) | 404; 409 `ATTEMPT_FINISHED` (đã nộp); 422 `QUIZ_NOT_OPEN`, `QUIZ_ENDED` |
| GET | `/attempts/{attemptId}` | Lượt đang làm: các câu như em thấy, câu trả lời đã lưu, hạn nộp, số giây còn lại | 200 | 404; 409 `ATTEMPT_FINISHED` (đã nộp hoặc vừa hết giờ) |
| PUT | `/attempts/{attemptId}/answers/{questionId}` | Lưu câu trả lời của một câu, **ghi đè cả câu**: `{ selectedIndex? , matches? , order? , blanks? , statements? }` (mục 4); thân `{}` là xóa câu trả lời | 200 `{ questionId, answered, savedAt, answeredCount, questionCount, remainingSeconds }` | 400 `INVALID`; 404; 409 `ATTEMPT_FINISHED` |
| POST | `/attempts/{attemptId}/submit` | Nộp bài, trả kết quả theo cấp (như `GET .../result`). Nộp lần hai trả lại kết quả cũ | 200 | 404 |
| GET | `/attempts/{attemptId}/result` | Kết quả (mục 5), kể cả khi quiz đã đóng hay bài học đã lưu trữ | 200 | 404; 409 `ATTEMPT_IN_PROGRESS` |
| GET | `/attempts` | Lịch sử các lượt đã nộp (nộp tay hoặc tự nộp), mới nhất trước; `page`, `pageSize` (mặc định 20, tối đa 100) | 200 trang `{ attemptId, quizId, quizName, lessonId, lessonTitle, status, statusLabel, submittedAt, stars, score, totalPoint, percent, badges[] }`; cấp 1 có `stars`, không có điểm | 400 |
| GET | `/progress` | Tiến độ (mục 6) | 200 | 401, 403 |

`StudentAttemptDto`: `{ id, quizId, quizName, lessonId, lessonTitle, level, layout, readAloud, showsClock, status, startedAt, deadline, remainingSeconds, serverTime, timeLimitMinutes, questionCount, answeredCount, totalPoint, questions[] }`.
`questions[]`: `{ questionId, position, point, questionType, typeName, content, imageUrl, passage, passageSource, options[], leftItems[], rightItems[], items[], blankCount, wordBank[], statements[], partsTotal, answered, answer }`.
**Không có đáp án đúng, không có giải thích** trong đề. `point` và `totalPoint` là null ở lớp cấp 1. `layout` của lượt theo cấp: `LARGE_CARDS` (cấp 1, thẻ lớn, đọc to lời câu: `readAloud` = true), `STANDARD` (cấp 2), `COMPACT` (cấp 3); `layout` của trang học là khung của bài học theo cấp (mục 6).

## 3. Bắt đầu, một lượt, hạn nộp, tự nộp

- **Điều kiện tạo lượt:** em còn hoạt động; quiz đã phát hành, chưa xóa, thuộc bài học chưa xóa **của lớp em**; bài học đang **Đã xuất bản**; quiz đang mở (`openAt` ≤ bây giờ < `closeAt`); em chưa có lượt. Quiz nháp, của lớp khác hoặc của bài học không xuất bản (khi em chưa có lượt) trả 404. Không có mã quiz và không nhập mã lần hai.
- **Một lượt mỗi em mỗi quiz:** ràng buộc `UX_attempts_quiz_student` chặn cả hai yêu cầu bắt đầu gửi đồng thời; yêu cầu thua thấy lượt của yêu cầu thắng (200). Web học tập và app dùng chung lượt vì lượt gắn với **tài khoản**.
- **Hạn nộp** = `started_at` + thời gian làm, nhưng không muộn hơn `close_at`; quiz không giới hạn thời gian thì hạn nộp là `close_at`. Lớp **cấp 1 không có thời gian làm** dù quiz còn giữ giá trị cũ (lớp đổi cấp): hạn nộp là giờ đóng.
  Đồng hồ: cấp 1 không; cấp 2 khi quiz đặt thời gian làm; cấp 3 luôn có (đếm tới hạn nộp). `showsClock` cho giao diện biết.
- **Ân hạn 30 giây:** máy chủ vẫn nhận câu trả lời và bài nộp tới hạn nộp cộng 30 giây. Nộp trong khoảng đó là **Đã nộp** với giờ nộp là đúng hạn nộp; quá thì lượt được chốt thành **hết giờ** (`EXPIRED`) với các câu đã lưu.
- **Chốt hết giờ** xảy ra (a) khi em mở lại, lưu, nộp hoặc xem kết quả lượt đã quá hạn nhận bài, (b) ở **tác vụ nền mỗi phút** (`AttemptLifecycle.ExpireOverdueAsync`, chạy cùng tác vụ tự đóng quiz), (c) khi quiz được **đóng** (tay hoặc quá giờ đóng): mọi lượt đang dở được chấm và chuyển `EXPIRED`. Tác vụ tự đóng chỉ đóng quiz sau giờ đóng cộng 30 giây để không cắt ngang em đang nộp.
  Chốt lặp được và có khóa dòng lượt: không chấm hai lần.
- **Khóa:** lưu câu, nộp và chốt hết giờ của cùng một lượt chạy tuần tự bằng khóa dòng `attempts` (`UPDLOCK, HOLDLOCK` trong giao dịch). Hai thiết bị lưu cùng một câu một lúc không tạo dòng trùng; hai lần bấm Nộp chỉ chấm một lần.
- **Thứ tự câu và thứ tự xáo** tính lại mỗi lần tải đề từ seed là **mã lượt** (không lưu, không đổi lược đồ), bằng bộ số ngẫu nhiên tự cài (SplitMix64, không phụ thuộc phiên bản .NET): quiz bật xáo câu thì thứ tự câu cố định suốt lượt; **luôn** xáo hai cột ghép cặp, các mục sắp xếp (không bao giờ trùng thứ tự đúng), kho từ và thẻ chọn hình; phương án câu một đáp án đúng, Đúng/Sai, đọc tư liệu và mệnh đề giữ thứ tự lưu.

## 4. Câu trả lời và cách chấm

Mọi vị trí gửi lên tính **theo thứ tự đang hiện trên màn hình em** (máy chủ đổi về thứ tự lưu). Dùng chung khuôn `QuestionSubmission`:

| Dạng | Gửi lên | Hợp lệ khi | Phần chấm | Lưu |
|---|---|---|---|---|
| Một đáp án đúng, Đúng/Sai, chọn hình đúng, đọc tư liệu | `selectedIndex` | trong `0..options-1`, hoặc bỏ trống | 1 | `attempt_answers.answer_id` |
| Ghép cặp | `matches[i]` = vị trí vế phải nối với hình bên trái thứ `i` (hoặc null) | không quá số hình, mỗi vế phải nối **một lần**, trong phạm vi | mỗi cặp nối đúng | một dòng `attempt_answer_parts` mỗi hình: `answer_id` = hình, `selected_answer_id` = vế phải đã nối |
| Sắp xếp | `order` = vị trí các mục theo thứ tự em đặt | **hoán vị đầy đủ** mọi mục (hoặc rỗng = chưa sắp xếp) | mỗi mục ở đúng vị trí tuyệt đối | mỗi mục một dòng: `answer_id`, `selected_position` (1..n) |
| Điền từ | `blanks[i]` = chữ ở chỗ trống `i+1` (gõ hoặc chọn từ kho) | không quá số chỗ trống, mỗi chữ tối đa 255 ký tự | mỗi chỗ trống khớp một cách viết đúng (bỏ khoảng trắng đầu cuối, gộp khoảng trắng, không phân biệt hoa thường, **phân biệt dấu**) | mỗi chỗ trống một dòng: `blank_index`, `response_text`, và `selected_answer_id` khi chọn từ kho |
| Nhiều mệnh đề Đúng/Sai | `statements[i]` = đúng, sai hoặc null | không quá số mệnh đề | mỗi mệnh đề chọn đúng; chưa chọn thì không được | mỗi mệnh đề một dòng: `answer_id`, `selected_bool` |

Sai cấu trúc bị từ chối **400 ngay lúc lưu** (không ghi gì). Câu có `parts_total` phần được `point_earned` = điểm của câu trong quiz × `parts_correct` ÷ `parts_total`, làm tròn 2 chữ số (nửa lên); `is_correct` chỉ khi **đúng mọi phần**; không trừ điểm khi sai; câu bỏ trống 0 điểm.
Mỗi lần lưu chấm tạm và ghi đủ một dòng cho **mỗi phần** (kể cả phần bỏ trống). **Lúc nộp** (hoặc hết giờ) máy chủ chấm lại **mọi câu từ các dòng đã lưu**, ghi thêm dòng 0 điểm kèm đủ các phần cho câu em chưa trả lời, rồi:
`score` = tổng `point_earned`; `correct_count` = số câu `is_correct`; `total_questions` = số câu của quiz; `total_point` chép từ quiz; `duration_seconds` = giờ nộp − `started_at`, không vượt thời gian làm. Kết quả lưu cố định: ẩn hay sửa câu về sau không đổi điểm.

## 5. Kết quả theo cấp

Tính khi hiển thị, theo cấp **hiện tại** của lớp (lớp đổi cấp thì lượt cũ hiện theo cấp mới, điểm đã chấm không đổi). `resultView`: `STARS` (cấp 1), `SCORE` (cấp 2), `ANALYSIS` (cấp 3).

| Thành phần | Cấp 1 | Cấp 2 | Cấp 3 |
|---|---|---|---|
| `stars`, `praise` | **1-3 sao** (từ 80% là 3 sao, từ 50% là 2 sao, còn lại 1 sao) và lời khen tích cực | không | không |
| `score`, `totalPoint`, `percent` (1 chữ số thập phân), `correctCount`, `wrongCount`, `blankCount`, `durationSeconds` | **không** (null) | có | có |
| `badges` | có | có | có |
| `questions[]` (chỉ khi quiz bật cho xem đáp án): câu như em thấy kèm `answer` của em, `correctAnswer` cùng thứ tự đang hiện, `explanation` | đáp án và giải thích, **không** `isCorrect`, `partsCorrect`, `partsTotal`, `pointEarned`, `point` | đủ, kèm đúng sai, số phần đúng, điểm câu | như cấp 2 |
| `analysis` | không | không | tỉ lệ đúng **theo di sản** và **theo dạng câu**, các di sản có tỉ lệ đúng dưới 50% (nên ôn), số phần đúng của câu nhiều phần |

`wrongCount` = số câu − số câu đúng − số câu bỏ trống. Quiz không bật `showAnswersAfterSubmit` thì `questions` là null (`answersShown` = false) ở mọi cấp.

## 6. Tiến độ, lịch sử, huy hiệu, trang học

- **Huy hiệu** (4, tính khi hiển thị từ `attempts`, không lưu, không điểm thưởng): *Điểm tuyệt đối* (`score` = `total_point` > 0); *Xuất sắc* (từ 90% nhưng chưa tuyệt đối); *Nộp đúng hạn* (lượt `SUBMITTED`); *Kiên trì* (em có từ **5 lượt đã nộp**, tính cả nộp tay lẫn hết giờ tự nộp; hiện ở kết quả, ở trang tiến độ gắn vào lượt thứ 5). Giống nhau ở mọi cấp.
- **Tiến độ** `GET /progress`: `quizzesSubmitted` / `quizzesPublished` (quiz đã phát hành hoặc đã đóng của các bài học **đã xuất bản** của lớp em và **đã tới giờ mở**), `progressPercent`, `lessonsCompleted` (bài có ít nhất một quiz đã phát hành và em đã nộp **mọi** quiz đó) / `lessonsWithQuiz`, `finishedAttempts`, `totalStars` (cấp 1), `mastery[]`, `badges[]` (số lần và lần gần nhất).
- **Mức thành thạo** theo di sản = số câu đúng ÷ số câu đã trả lời (câu bỏ trống tính là đã trả lời), qua mọi lượt đã nộp hoặc hết giờ: từ 80% *Thành thạo*, 50% đến dưới 80% *Đạt*, dưới 50% *Cần ôn*, dưới 3 câu *Chưa đủ dữ liệu*. Cấp 1 không có `percent`, hiện `stars` (3, 2, 1, 0 sao theo bốn mức).
- **Trang học** `GET /home`: `inProgress[]` (lượt đang làm kèm hạn nộp; lượt đã quá hạn được chốt trước khi liệt kê), `openQuizzes[]` (quiz đang mở em chưa làm, hạn đóng gần nhất trước, tối đa 20; `needsLessonCode` và nhãn **"Cần mã bài học"** khi em chưa có lượt nào ở bài học đó), `myLessons[]` (bài học suy từ lượt làm bài, **kèm mã bài học** để mở lại, số quiz đã làm trên số quiz đã phát hành), `recentBadges[]` (huy hiệu của 5 lượt đã nộp gần nhất).
  Cấp 1 thêm `totalStars`. Cấp ảnh hưởng `layout` (`STEP_BY_STEP`, `FREE_STEPS`, `DOCUMENT`) và `timeLimitMinutes` của từng quiz (null ở cấp 1).

## 7. Kiểm thử và chạy thử

- **Unit** (`AttemptRulesTests`): hạn nộp, ân hạn 30 giây đúng biên, đồng hồ theo cấp, thời gian làm bị chặn, phần trăm, ngưỡng sao, huy hiệu, ngưỡng thành thạo.
- **Tích hợp trên SQLite** (Api thật): `AttemptStartApiTests` (trang giới thiệu, điều kiện bắt đầu, vào tiếp, đề không lộ đáp án, xáo cố định, hạn nộp), `AttemptAnswerApiTests` (lưu và đọc lại cả tám dạng ở ba cấp, ghi đè và xóa, câu trả lời sai cấu trúc, chỉ chủ lượt), `AttemptResultApiTests` (chấm đủ tám dạng và từng phần, câu bỏ trống, nộp hai lần, kết quả ba cấp, đổi cấp lớp), `AttemptTimingApiTests` (quá hạn, ân hạn, tác vụ nền, đóng quiz tự nộp), `StudentLearningApiTests` (trang học, lịch sử, tiến độ, mức thành thạo, huy hiệu).
- **SQL Server LocalDB** (`AttemptSqlServerTests`, database tạm tự xóa): kiểu cột và ràng buộc của hai bảng trả lời, CHECK và UNIQUE có lọc thật sự từ chối dữ liệu sai, ba cấp chạy trọn vòng, các truy vấn lịch sử và tiến độ dịch được sang SQL, **bắt đầu, nộp và lưu cùng một câu đồng thời** không tạo dòng trùng hay chấm hai lần.
- **Smoke** `scripts/smoke-hoc-tap-theo-cap.sh` trên API thật và database tạm: dựng trường, ba lớp ba cấp, tám loại câu, ba quiz đang mở rồi cho ba học sinh làm bài đủ vòng, kiểm kết quả từng cấp, lịch sử, tiến độ, trang học, phân quyền. Cách chạy ở đầu tệp (nhớ `Email__Mode=Log` và, ở worktree riêng, trỏ `Seed__HeritageData__ProvincesFile`, `Seed__HeritageData__HeritagesFile` về `doc/du-lieu-di-san`).

## 8. Quyết định và phần chưa làm

- **Chấm tạm lúc lưu, chấm cố định lúc nộp:** mỗi lần lưu ghi điểm tạm để việc đóng quiz và tự nộp không phải chấm lại từ đầu; lúc nộp luôn chấm lại từ các dòng thô nên điểm cuối không phụ thuộc điểm tạm.
- **Hết giờ ghi giờ nộp là hạn nộp** (không phải lúc em mở lại), để thời gian làm và huy hiệu không bị lệch vì em mở muộn.
- **Kiên trì tính cả lượt hết giờ.** Đặc tả ghi "5 lượt đã nộp"; bản này hiểu là lượt đã chốt (`SUBMITTED` hoặc `EXPIRED`). Đổi ở `AttemptRules.HasPerseverance` nếu nhóm muốn chỉ tính nộp tay.
- **Quiz mở được không cần mở bài bằng mã trước:** đúng bốn điều kiện tạo lượt của đặc tả (không có điều kiện "đã nhập mã"); trang học gắn nhãn "Cần mã bài học" để hướng em vào qua trang bài học.
- **Chưa làm ở luồng này:** phần **gợi ý cá nhân hóa** (năm phần "Gợi ý cho em", tính ở Dịch vụ AI, dùng lại ở trang kết quả) và các màn hình xem của giáo viên (kết quả của lớp, tiến độ từng học sinh); giao diện web học tập và app.
