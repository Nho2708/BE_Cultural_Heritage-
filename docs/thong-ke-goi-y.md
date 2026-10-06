# Luồng Thống kê và gợi ý: gợi ý cho em, kết quả bài kiểm tra của lớp, tiến độ, mức thành thạo, hồ sơ học tập (Backend)

Phần backend cho hai việc: học sinh nhận **"Gợi ý cho em"** (ở trang học và ở trang kết quả của một lượt), và giáo viên xem **thống kê của lớp** (kết quả từng bài kiểm tra, tiến độ, mức thành thạo,
hồ sơ học tập của từng học sinh) cùng **gợi ý ôn tập cho lớp**. Mọi số liệu **tính khi hiển thị** từ `attempts`, `attempt_answers` và `attempt_answer_parts`; huy hiệu cũng tính khi hiển thị; **không có bảng
gợi ý riêng, không có bảng thống kê, không có migration mới**. Gợi ý theo kiểu content-based (so thuộc tính di sản), không gọi mô hình ngôn ngữ, không tính lượt AI, không ghi `ai_generations`.

| Mục | Giá trị |
|---|---|
| Chức năng | Xem kết quả bài kiểm tra của lớp (tổng quan, từng câu, từng học sinh, theo chủ đề, chi tiết bài làm) · Theo dõi tiến độ và mức thành thạo của lớp · Hồ sơ học tập của một học sinh · Gợi ý ôn tập cho lớp · Nhận gợi ý bài học và bài kiểm tra cá nhân hóa |
| Màn hình phục vụ | Web quản trị (giáo viên): Chi tiết lớp (tab Tiến độ, Thành thạo, Gợi ý ôn tập), Hồ sơ học tập của học sinh, Kết quả bài kiểm tra (tab Tổng quan, Từng câu, Từng học sinh, Gợi ý), Chi tiết bài làm. Web học tập và app (chế độ Học sinh): khối "Gợi ý cho em" ở Trang học và ở Kết quả bài làm |
| Bảng | Chỉ **đọc**: `attempts`, `attempt_answers`, `attempt_answer_parts`, `quizzes`, `quiz_questions`, `questions`, `answers`, `lessons`, `lesson_heritages`, `classes`, `students`, `users` (lần đăng nhập gần nhất), `heritages`, `timelines`, `provinces` |
| Migration mới | **Không có.** Bảng đặc tả cũng ghi "không có bảng mới". Các truy vấn đã đo trên SQL Server với 45 học sinh × 20 bài kiểm tra × 20 câu (mục 6) nên chưa cần thêm chỉ mục |
| Lớp mới | `Domain/Rules/HeritageSimilarity` (độ tương đồng giữa hai di sản), `Domain/Rules/RecommendationRules` (số mục, mức khó mục tiêu, ngưỡng nên ôn, câu lý do); `Application/Recommendations`: `StudentRecommendationService`, `ClassRecommendationService`, `RelatedRanker` (gọi Dịch vụ AI, có dự phòng), `IRecommendationAiClient`, `HeritageCatalog`; `Application/Statistics`: `QuizResultsService`, `ClassStatisticsService`, `ClassStatsScope`, `StatsMath`; `Infrastructure/Ai`: `SimulatedRecommendationAiClient`, `HttpRecommendationAiClient`; bốn controller ở `Api/Controllers` |
| Dùng lại | `AttemptRules` (sao, huy hiệu, mức thành thạo, phần trăm), `StudentContext`, `AttemptLifecycle` (đồng hồ và chốt hết giờ), `AttemptLayout`/`AttemptViews` (dựng câu và đáp án như em thấy), `ClassScope`/`QuizScope` (phạm vi giáo viên) |
| Nguồn | Đặc tả của nhóm: nhóm quy tắc Thống kê và gợi ý (Gợi ý cá nhân hóa cho học sinh, Gợi ý ôn tập cho lớp, Tiến độ và mức thành thạo, Huy hiệu tính khi hiển thị, Cách hiện kết quả theo cấp) và các use case Xem kết quả quiz của lớp, Theo dõi tiến độ và mức thành thạo của lớp, Xem gợi ý ôn tập cho lớp, Nhận gợi ý bài học và quiz cá nhân hóa |

## 1. Phân quyền

| Vai trò | Được làm |
|---|---|
| Học sinh đã đăng nhập và đã đổi mật khẩu (`STUDENT`) | Hai endpoint gợi ý ở mục 2.1, **chỉ của chính em**; lượt của em khác (kể cả cùng lớp) như không tồn tại (404) |
| Giáo viên (`TEACHER`) | Mọi endpoint ở mục 2.2, **chỉ với lớp và bài kiểm tra của chính mình**; lớp, bài kiểm tra, lượt của giáo viên khác (kể cả cùng trường) hoặc trường khác trả **404**; bài kiểm tra hoặc lớp đã xóa mềm trả 404 |
| Quản trị trường, quản trị hệ thống | **403** ở mọi endpoint (quản trị trường chỉ xem số tổng hợp của trường, thuộc luồng khác) |
| Chưa đăng nhập | 401 |

Không có số liệu của học sinh nào đi ra ngoài lớp của giáo viên phụ trách; học sinh không thấy gì của học sinh khác. Giáo viên **luôn thấy điểm** ở mọi cấp (lớp cấp 1 kèm số sao), còn học sinh cấp 1 chỉ thấy sao.

## 2. Endpoint

Khuôn phản hồi chung `{ success, data, message, errorCode, traceId, errors }`. Enum viết HOA. Giờ trả về là UTC (`Z`). Mọi endpoint không bị lưu đệm (`no-store`).

### 2.1 Học sinh (`/api/v1/student`)

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/recommendations` | "Gợi ý cho em" ở trang học: đủ năm phần (mục 3), `di sản nên ôn` tính từ **mọi** lượt đã nộp | 200 `StudentRecommendationsDto` | 401, 403 |
| GET | `/attempts/{attemptId}/recommendations` | Như trên nhưng `di sản nên ôn` chỉ tính từ các câu sai hoặc bỏ trống của **lượt đang xem** | 200 | 404 (lượt không phải của em); 409 `ATTEMPT_IN_PROGRESS` (lượt còn đang làm) |

`StudentRecommendationsDto`: `{ level, levelName, layout, attemptId, targetDifficulty, targetDifficultyName, nextQuizzes[], lessonsToReview[], heritagesToReview[], relatedHeritages[], newLessons[], reviewPraise, message, relatedFromFallback }`.
`nextQuizzes[]`: `{ quizId, name, lessonId, lessonTitle, closeAt, difficultyLevel, difficultyName, needsLessonCode, label, reason }` (`label` = "Cần mã bài học" khi em chưa có lượt nào ở bài học của quiz).
`lessonsToReview[]` và `newLessons[]`: `{ lessonId, title, accessCode, hint, reason }`; bài nên ôn lại có `accessCode` (em đã mở bài), **bài mới không bao giờ có mã**: `accessCode` null, `hint` = "Nhờ thầy cô đưa mã bài học".
`heritagesToReview[]` và `relatedHeritages[]`: `{ heritageId, slug, name, thumbnailUrl, shortDescription, provinceName, typeName, reason }` (thẻ di sản; `slug` để mở trang di sản công khai).

### 2.2 Giáo viên (`/api/v1/teacher`)

| Method | Đường dẫn | Nội dung | Lỗi chính |
|---|---|---|---|
| GET | `/quizzes/{quizId}/results` | **Tổng quan**: sĩ số, đã nộp, hết giờ tự nộp, đang làm, chưa làm, điểm trung bình, cao nhất, thấp nhất (điểm và phần trăm), phổ điểm 10 khoảng | 404 |
| GET | `/quizzes/{quizId}/results/questions` | **Từng câu**: tỉ lệ đúng; câu nhiều phần thêm tỉ lệ đúng từng phần; phương án bị chọn nhiều nhất (dạng một đáp án đúng, Đúng/Sai, chọn hình đúng, đọc tư liệu); lỗi hay gặp (ghép cặp, sắp xếp, điền từ, nhiều mệnh đề); tỉ lệ đúng theo dạng câu | 404 |
| GET | `/quizzes/{quizId}/results/students` | **Từng học sinh**: mỗi học sinh đang hoạt động một dòng (Đã nộp, Hết giờ tự nộp, Đang làm, Chưa làm), điểm, thời gian làm, số sao (lớp cấp 1), huy hiệu, `attemptId` | 404 |
| GET | `/quizzes/{quizId}/results/topics` | Tỉ lệ đúng **theo di sản (chủ đề)** và theo dạng câu trong riêng bài này, kèm mức thành thạo và cờ nên ôn | 404 |
| GET | `/quizzes/{quizId}/results/recommendations?from&to` | Gợi ý ôn tập tính riêng từ bài kiểm tra này (mục 4) | 400, 404 |
| GET | `/attempts/{attemptId}` | **Chi tiết bài làm**: từng câu như em đã thấy, câu em trả lời đặt cạnh đáp án đúng, đúng hay sai, điểm đạt (câu nhiều phần có số phần đúng), giải thích; xem được kể cả khi bài không cho em xem đáp án | 404; 409 `BLOCKED` (lượt còn đang làm) |
| GET | `/classes/{classId}/progress` | **Tiến độ lớp**: bài kiểm tra đã phát hành kèm tỉ lệ đã nộp, bảng học sinh (đã nộp trên đã phát hành, bài học đã hoàn thành, sao ở lớp cấp 1, điểm trung bình, huy hiệu, lần đăng nhập gần nhất, chưa đăng nhập lần nào), ô học sinh × bài kiểm tra | 404 |
| GET | `/classes/{classId}/mastery?provinceId&heritageType` | **Mức thành thạo lớp**: theo di sản (lọc theo tỉnh và loại), theo tỉnh, theo loại di sản, theo dạng câu, bảng nhiệt học sinh × di sản (tối đa 60 di sản nhiều câu nhất) | 400 (`heritageType` lạ), 404 |
| GET | `/classes/{classId}/recommendations?quizId&from&to` | **Gợi ý ôn tập cho lớp** (mục 4) | 400, 404 |
| GET | `/classes/{classId}/students/{studentId}/profile` | **Hồ sơ học tập**: lịch sử làm bài kèm huy hiệu từng lượt, biểu đồ điểm theo thời gian (cũ nhất trước), bài học đã hoàn thành, mức thành thạo theo di sản, tỉnh và loại di sản, huy hiệu | 404 (học sinh không thuộc lớp này) |

Các DTO nằm ở `Application/Statistics/StatisticsDtos.cs` và `Application/Recommendations/RecommendationDtos.cs`, đều có chú thích tiếng Việt trên từng trường.
Bài ôn tập từ di sản được gợi ý: giáo viên dùng `suggestedHeritageIds` gọi API tạo bài học đã có (`POST /api/v1/teacher/lessons` với lớp và các di sản đó); bài mới là bản nháp theo khung của cấp lớp.

## 3. Gợi ý cho em (năm phần)

Tính riêng cho từng học sinh khi hiển thị, từ **cấp học** (suy từ khối lớp), **tiến độ** và **kết quả làm bài**; "sở thích" suy từ các di sản của những bài em đã có lượt làm. Ứng viên **chỉ lấy từ CSDL** và **chỉ thuộc lớp của em**
(không dùng mã bài học lưu trên thiết bị), nên web học tập và app cho cùng một kết quả. Không bao giờ gợi ý bài học hoặc quiz của lớp khác, bài học chưa xuất bản hoặc đã lưu trữ, quiz chưa tới giờ mở hoặc đã đóng; chỉ gợi ý **di sản đã xuất bản**.

- **Mức khó mục tiêu của em:** mức thành thạo chung (đúng ÷ đã trả lời, qua mọi lượt đã nộp) từ 80% hợp *Khó*, từ 50% đến dưới 80% hợp *Trung bình*, dưới 50% hợp *Dễ*; dưới 3 câu đã trả lời (chưa đủ kết quả) thì theo cấp học: cấp 1 *Dễ*, cấp 2 và cấp 3 *Trung bình*.
  Đây là cách khối lớp đi vào điểm gợi ý. Di sản em **còn yếu** là di sản có mức *Cần ôn* (đúng dưới 50% và từ 3 câu).
1. **Quiz nên làm tiếp** (tối đa 3): quiz đang mở em chưa có lượt nào, của mọi bài học đã xuất bản của lớp em. Xếp: có câu về di sản em còn yếu trước, rồi hạn đóng gần, rồi độ khó của quiz gần mức khó mục tiêu, rồi mã quiz. Quiz của bài học em chưa có lượt nào gắn nhãn "Cần mã bài học".
2. **Bài học nên ôn lại** (tối đa 3): bài học còn xuất bản của lớp em mà em đã nộp quiz của bài và đúng **dưới 50%** ở các di sản của bài (tính trên mọi câu em đã trả lời thuộc các di sản đó); xếp tỉ lệ đúng thấp trước. Có mã bài học để mở lại.
3. **Di sản nên ôn** (tối đa 3): từ câu sai hoặc bỏ trống, xếp theo **tổng điểm bị mất** giảm dần (rồi số câu sai, rồi mã di sản). Ở trang kết quả chỉ tính từ lượt đang xem, ở trang học tính từ mọi lượt. **Làm đúng hết** thì phần này rỗng và `reviewPraise` có lời khen.
4. **Di sản liên quan** (tối đa 3): di sản đã xuất bản khác với các di sản của những bài em đã có lượt làm, có **độ tương đồng** cao nhất với các di sản em đã học (di sản em còn yếu nặng gấp đôi). Em chưa có lượt nào thì phần này rỗng.
5. **Bài học mới nên học** (tối đa 3): bài học đã xuất bản của lớp em mà em **chưa có lượt làm nào** ở các quiz của bài. Xếp theo độ tương đồng giữa di sản của bài với di sản em còn yếu hoặc đã học, rồi độ khó các quiz của bài gần mức mục tiêu, rồi bài mới tạo trước; em chưa có kết quả thì theo bài mới tạo trước
   rồi độ khó. Thẻ **chỉ có tiêu đề và câu "Nhờ thầy cô đưa mã bài học"**, không có mã, không mở trực tiếp (giữ nguyên quy tắc một mã bài học).

Em chưa có lượt nào, hoặc lớp chưa có bài nào xuất bản: `message` = lời mời nhập mã bài học ("Em chưa có bài làm nào. Em hãy nhập mã bài học thầy cô đưa để bắt đầu học."); phần quiz và bài mới vẫn hiện nếu lớp có bài.
**Cùng nội dung và cùng cách tính ở mọi cấp**; riêng cấp 1 các câu lý do **không có số liệu** (không phần trăm, không số câu đúng sai), vd "Em ôn lại di sản này nhé!".

### Độ tương đồng giữa hai di sản

Cộng điểm: **cùng loại** 3 · **cùng tỉnh** 2 (không thì **cùng vùng** 1) · **cùng cấp xếp hạng** 1 (không tính "chưa xếp hạng") · **khoảng năm của dòng thời gian giao nhau** 1 · **chữ giống nhau** (tên cộng mô tả ngắn, bỏ dấu và từ nối, độ giống
Jaccard từ 0,12 trở lên) tối đa 2. Điểm của một ứng viên = điểm cao nhất theo từng di sản tham chiếu nhân trọng số. Không có điểm chung nào thì không có trong kết quả. Lý do ghi tối đa hai điểm chung, vd "Cùng loại, cùng tỉnh với Hoàng thành Thăng Long".

### Dịch vụ AI và dự phòng

Phần xếp hạng di sản liên quan (mục 4 và dùng cho mục 5) đi qua `IRecommendationAiClient`: `Ai:Mode = Simulated` (mặc định) dùng bản giả lập tính ngay bằng bộ độ tương đồng trên; `Ai:Mode = Remote` gọi Dịch vụ AI nội bộ
`POST {Ai:BaseUrl}/v1/rank-related` (header `X-Internal-Key`). **Chỉ gửi** id học sinh dạng số nội bộ, cấp học, mức khó mục tiêu, id và thuộc tính của di sản (loại, tỉnh, vùng, cấp xếp hạng, khoảng năm, tên và mô tả ngắn) cùng trọng số; **không gửi** họ tên,
mã học sinh, tên lớp. Thời gian chờ **2 giây**; lỗi, quá giờ hoặc trả sai lược đồ thì máy chủ **tự tính dự phòng**: di sản liên quan thay bằng di sản **cùng tỉnh hoặc cùng loại** (tỉnh nặng hơn), bài học mới theo độ tương đồng dự phòng đó, các phần còn lại không đổi (đều là quy tắc cố định tính ở máy chủ);
`relatedFromFallback` = true. Hủy từ phía người gọi (ngắt kết nối) vẫn được báo lên, không bị nuốt. Chưa có dịch vụ Python phía bên kia: bản gọi HTTP đã sẵn theo hợp đồng này và đã thử bằng máy chủ giả, bản giả lập là mặc định.

## 4. Gợi ý ôn tập cho lớp

Theo từng lớp của giáo viên, cùng bộ tính với gợi ý cho học sinh, trên các lượt đã nộp của học sinh đang hoạt động; lọc được theo **bài kiểm tra** (`quizId`) và **khoảng thời gian nộp** (`from`, `to`, giờ UTC; `from` phải không sau `to`, nếu không 400).

- **Tỉ lệ sai theo di sản** = số câu trả lời sai hoặc bỏ trống ÷ tổng số câu trả lời gắn di sản đó.
- **Di sản nên ôn:** tỉ lệ sai **từ 40%** và **ít nhất 10 câu trả lời**, tối đa **5**, xếp tỉ lệ sai giảm dần; lý do "Lớp sai {x}% ở {n} câu về di sản này" (x làm tròn nguyên). Có `answered`, `wrong`, `wrongPercent` (1 chữ số thập phân).
- **Di sản liên quan:** tối đa **5**, giống nhất với các di sản của các bài học đã xuất bản của lớp (di sản nên ôn nặng gấp đôi), loại các di sản đã giao.
- `suggestedHeritageIds` là các di sản nên ôn, để giáo viên tạo nhanh bài học ôn tập. Chưa có lượt nộp: `message` = "Chưa có học sinh nào nộp bài. Thống kê và gợi ý sẽ hiện khi có kết quả." và các danh sách rỗng.
- Không gửi gì về học sinh sang Dịch vụ AI (`studentId` và `level` null); AI lỗi thì phần nên ôn vẫn hiện (máy chủ tự tính), phần liên quan dự phòng như mục 3.

## 5. Thống kê của lớp

Chỉ tính lượt **đã nộp hoặc hết giờ tự nộp** của **học sinh đang hoạt động** (chưa xóa mềm, chưa vô hiệu) ở bài kiểm tra chưa xóa của bài học chưa xóa của lớp. Em bị vô hiệu thì số liệu của em tạm rời khỏi thống kê; mở lại thì quay về.

- **Kết quả bài kiểm tra:** điểm trung bình (làm tròn 2 chữ số), cao nhất, thấp nhất; phần trăm = điểm ÷ tổng điểm (1 chữ số); **phổ điểm** 10 khoảng 0-10%, ..., 90-100% (100% vào khoảng cuối). Sĩ số = học sinh đang hoạt động; "chưa làm" = sĩ số trừ số em đã có lượt.
- **Mức thành thạo** theo di sản, tỉnh, loại di sản, dạng câu = số câu đúng ÷ số câu đã trả lời (tính cả bỏ trống) qua mọi lượt; câu nhiều phần chỉ tính đúng khi đúng toàn bộ các phần. Mức: từ 80% *Thành thạo*, 50% tới dưới 80% *Đạt*, dưới 50% *Cần ôn*, dưới 3 câu *Chưa đủ dữ liệu*; thanh sao 3, 2, 1, 0.
  Với loại di sản trong `byHeritageType`, `id` là giá trị số của enum loại di sản.
- **Tiến độ của học sinh** = số quiz em đã nộp ÷ số quiz đã phát hành (đang mở hoặc đã đóng) **đã tới giờ mở** thuộc các bài học **đã xuất bản** của lớp. **Bài học đã hoàn thành** = bài có ít nhất một quiz đã phát hành và em đã nộp **mọi** quiz đã phát hành của bài.
  **Tiến độ lớp** = tổng lượt đã nộp ÷ (số học sinh đang hoạt động × số quiz đã phát hành đã tới giờ mở). Quiz nháp, chưa tới giờ mở, đã xóa hoặc của bài học đã lưu trữ không tính.
- **Chưa đăng nhập lần nào** = tài khoản chưa có `last_login_at`.
- **Huy hiệu** (tính khi hiển thị, không lưu): Điểm tuyệt đối, Xuất sắc (từ 90% nhưng chưa tuyệt đối), Nộp đúng hạn (tự nộp trước hạn), Kiên trì (từ 5 lượt đã nộp). Ở bảng lớp và hồ sơ trả số lần đạt từng huy hiệu.
- **Lỗi hay gặp của câu nhiều phần:** các phần có người sai, xếp số lần sai giảm dần, tối đa 3; ghép cặp ghi tên em hay nối nhầm, điền từ ghi chữ gõ sai hay gặp, nhiều mệnh đề ghi "Em chọn Đúng" hay "Em chọn Sai"; "Bỏ trống" khi em không chọn.

## 6. Hiệu năng

Mọi truy vấn gộp (đếm, nhóm theo di sản) chạy ở CSDL; phần cộng điểm thập phân làm trong bộ nhớ vì SQLite của kiểm thử không gộp được kiểu `decimal`. Danh mục di sản (tối đa 5000 di sản chưa xóa, chỉ vài cột nhẹ cộng giai đoạn) nạp một lần mỗi yêu cầu gợi ý.
Đo trên SQL Server LocalDB với **45 học sinh × 20 bài kiểm tra × 20 câu** (900 lượt, 18.000 câu trả lời), sau khi làm nóng: tiến độ lớp 34 ms, mức thành thạo lớp 178 ms, kết quả một bài 85 ms, từng câu của một bài 180 ms, từng học sinh của một bài 42 ms, gợi ý ôn tập lớp 210 ms,
hồ sơ một học sinh 169 ms, gợi ý cho em 9 ms; giới hạn của yêu cầu phi chức năng: giáo viên 3 giây, gợi ý 2 giây, tiến độ và lịch sử một học sinh 1 giây (test `StatisticsSqlServerTests` kiểm các giới hạn này).

## 7. Kiểm thử và chạy thử

- **Unit:** `HeritageSimilarityTests` (từng điểm chung, vùng và tỉnh không cộng dồn, khoảng năm giao nhau, chữ giống nhau, điểm cao nhất theo trọng số, phương án dự phòng, câu lý do), `RecommendationRulesTests` (mức khó mục tiêu theo ngưỡng 80% và 50%, theo cấp khi chưa đủ kết quả, ngưỡng 40% và 10 câu của lớp đúng biên, câu lý do cấp 1 không có số), `RelatedRankerTests` (bản giả lập, không gọi AI khi không có gì để so, bỏ điểm của di sản không phải ứng viên, lỗi và quá 2 giây thì dự phòng, hủy từ người gọi không bị nuốt, bản HTTP theo hợp đồng và không gửi dữ liệu cá nhân).
- **Tích hợp trên SQLite** (Api thật): `StudentRecommendationApiTests` (20 test: học sinh mới, lớp chưa có bài, năm phần sau khi làm sai, khen khi đúng hết, trang kết quả chỉ tính lượt đang xem, nhãn cần mã, quiz xếp theo di sản yếu rồi hạn đóng rồi độ khó, lý do còn bao nhiêu ngày, quiz đóng hoặc chưa mở hoặc lớp khác không bao giờ hiện, di sản đã ẩn không được gợi ý, cấp 1 không số liệu, mức khó mục tiêu theo cấp, AI lỗi thì dự phòng, dữ liệu gửi sang AI, quyền 401, 403, 404, 409),
  `TeacherQuizResultsApiTests` (14 test: tổng quan và phổ điểm, đủ trạng thái học sinh, học sinh bị vô hiệu rời số liệu, lớp cấp 1 thấy điểm kèm sao, chi tiết bài làm kể cả bài không cho xem đáp án, từng câu cho cả tám dạng, chủ đề, quyền và 404),
  `TeacherClassStatisticsApiTests` (11 test: tiến độ lớp và từng học sinh, bài nháp, chưa tới giờ mở, đã xóa và bài học lưu trữ không tính còn bài đã đóng thì tính, thành thạo theo di sản, tỉnh, loại, dạng câu và lọc, bảng nhiệt, hồ sơ học sinh, quyền),
  `TeacherRecommendationApiTests` (10 test: đúng biên 40% và 10 câu, lý do, lọc theo bài và theo thời gian, bài ôn tập chỉ đủ khi đủ câu, dự phòng, không gửi dữ liệu học sinh sang AI, quyền).
- **SQL Server LocalDB** (`StatisticsSqlServerTests`, database tạm tự xóa): mọi endpoint dịch được sang SQL và cho cùng số liệu như SQLite; lớp 45 học sinh × 20 bài × 20 câu trong giới hạn thời gian của mục 6.
- **Smoke** `scripts/smoke-thong-ke-goi-y.sh` trên API thật và database tạm (xem đầu tệp): dựng trường, giáo viên, hai di sản đã xuất bản, hai bài học và bài kiểm tra, ba học sinh làm bài đúng và sai, rồi kiểm 111 bước (gợi ý ở trang học và trang kết quả, lời mời khi mới vào, kết quả bài, từng câu, từng học sinh, chủ đề, chi tiết bài làm, tiến độ, thành thạo, hồ sơ, gợi ý ôn tập lớp, quyền).

## 8. Quyết định và phần chưa làm

- **Không lưu gì:** không bảng gợi ý, không bảng thống kê, không cột mới; vì vậy không có migration. Đổi quy tắc là đổi code ở `RecommendationRules`, `HeritageSimilarity`, `AttemptRules`.
- **Phần xếp hạng quiz và bài học là quy tắc cố định ở máy chủ**, chỉ **độ tương đồng giữa di sản** đi qua Dịch vụ AI. Đặc tả mô tả cả bài học và quiz ứng viên đi qua AI, nhưng kết quả xếp hạng giống nhau và có dự phòng; gửi ít dữ liệu hơn cũng an toàn hơn.
- **"Bài học nên ôn lại"** đo trên mọi câu em đã trả lời thuộc các di sản của bài (đặc tả nói "tỉ lệ đúng dưới 50% ở các di sản của bài"), không chỉ các câu của quiz thuộc bài đó. Em có ít nhất một câu thì tính.
- **"Bài mới tạo trước"** (khi em chưa có kết quả) được hiểu là bài **mới tạo gần đây nhất** trước, theo `lessons.created_at` (CSDL không lưu thời điểm xuất bản bài học).
- **Đã nộp = đã chốt:** lượt hết giờ tự nộp cũng tính vào thống kê, như quy tắc Kiên trì của luồng làm bài.
- **Tab "Gợi ý" của kết quả bài** dùng cùng bộ tính với tab của lớp nhưng lọc theo bài; tạo bài ôn tập đi qua API tạo bài học đã có, không có endpoint riêng.
- **Chưa làm ở luồng này:** màn hình web quản trị và khu học (luồng giao diện), tổng quan trường cho quản trị trường (luồng sau), gợi ý cho khách tham quan (luồng khách), dịch vụ Python phía Dịch vụ AI cho `POST /v1/rank-related`.
