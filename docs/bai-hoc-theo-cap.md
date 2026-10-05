# Luồng Bài học cho lớp theo cấp, AI soạn nội dung, mã bài học (Backend)

Phần backend của khu **Bài học** của giáo viên (web quản trị) và của việc **học sinh mở bài bằng mã bài học** (web học tập, chế độ Học sinh của app).
Giáo viên tạo bài học cho **một lớp** của mình từ các di sản đã xuất bản; nội dung bài theo **khung của cấp 1, 2 hoặc 3** của lớp đó; AI soạn bản nháp
đúng cấp, giáo viên sửa và duyệt rồi xuất bản; lần xuất bản đầu hệ thống sinh **một mã bài học** cho lớp; học sinh của lớp đăng nhập, gõ mã và nhận bài
theo luồng của cấp mình.

| Mục | Giá trị |
|---|---|
| Chức năng | Soạn và xuất bản bài học cho lớp · Yêu cầu AI soạn nội dung bài học theo cấp · Nhân bản bài học sang lớp khác · Mở bài học bằng mã bài học |
| Màn hình phục vụ | Web quản trị (giáo viên): Bài học của tôi, Tạo bài học (chọn lớp và di sản), Soạn bài học, Mã bài học, Nhân bản bài học sang lớp khác. Web học tập và app (học sinh): Nhập mã bài học, Trang bài học theo bố cục từng cấp |
| Bảng | `lessons`, `lesson_heritages`, `ai_generations` (có từ v3), **`lesson_sections` và các cột mới của `ai_generations` (CSDL v4, migration mới)**; đọc `classes`, `students`, `heritages`, `timelines`, `events`, `media`, `heritage_references`, `quizzes`, `attempts`, `subscriptions`, `plans` |
| Migration mới | `20261005200057_ThemPhanNoiDungBaiHocTheoCap`: bảng `lesson_sections`; `ai_generations` cho `lesson_id` được NULL, thêm `heritage_id`, mở rộng `task_type`, thêm CHECK đối tượng. Chi tiết ở [co-so-du-lieu.md](co-so-du-lieu.md) mục 5 |
| Lớp mới | `Domain/Rules/LessonRules` (khung theo cấp, điều kiện xuất bản, mã bài học, hằng số); `Application/Lessons` (`LessonService`, `LessonAiService`, `StudentLessonService`, hợp đồng AI, thư viện ảnh và dòng thời gian); `PlanGuard` thêm tính hạn mức lượt AI; `Infrastructure/Ai` (`SimulatedLessonAiClient`, `HttpLessonAiClient`); `Infrastructure/Security/InMemoryLessonCodeThrottle`; `Api/Controllers/TeacherLessonsController`, `StudentLessonsController` |
| Nguồn | Đặc tả của nhóm (nhóm quy tắc Bài học & AI, hợp đồng Backend và Dịch vụ AI trong kiến trúc, bảng mở rộng theo cấp 1/2/3), `doc/database/dsvhvn-v4.dbml` |

## 1. Phân quyền

| Vai trò | Được làm |
|---|---|
| Giáo viên (`TEACHER`) | Chỉ bài **do mình tạo** ở lớp **do mình phụ trách** trong trường mình. Bài của giáo viên khác (cùng trường hay khác trường) trả **404** như không tồn tại |
| Quản trị trường, quản trị hệ thống | Không xem, sửa bài học (403). Quản trị trường chỉ thấy số liệu tổng hợp của trường |
| Học sinh (`STUDENT`) | Chỉ **mở** bài bằng mã, và chỉ khi bài đã xuất bản và thuộc **lớp của em**. Đã đổi mật khẩu lần đầu (học sinh còn bị buộc đổi mật khẩu nhận 403) |
| Chưa đăng nhập | 401. Mã bài học **không phải quyền truy cập công khai** |

## 2. Endpoint

Khuôn phản hồi chung `{ success, data, message, errorCode, traceId, errors }`. Tiền tố `/api/v1`. Chữ hiển thị tiếng Việt, mã lỗi theo nghĩa.

### 2.1 Giáo viên — `/teacher/lessons`

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/teacher/lessons` | `classId`, `status` (`DRAFT`/`PUBLISHED`/`ARCHIVED`), `heritageType`, `keyword` (trong tiêu đề), `page`, `pageSize` | 200 trang bài `{ id, title, classId, className, grade, schoolLevel, status, accessCode, heritages[], quizCount, createdAt, updatedAt }`, bài mới sửa xếp trước | 400 |
| POST | `/teacher/lessons` | `{ classId, title, heritageIds[] }` | 201 chi tiết bài **Nháp** (chưa có mã). Cần **gói hiệu lực**; 1-10 di sản đã xuất bản, không lặp; lớp phải là lớp của giáo viên | 400 theo trường (`REQUIRED`/`INVALID`); 404 (lớp không phải của mình); 422 `PLAN_REQUIRED` |
| GET | `/teacher/lessons/{lessonId}` | — | 200 chi tiết: nội dung, các phần, di sản, **khung của cấp** (`frame`: khoảng số từ, ba mức độ dài AI, loại phần dùng được, luồng học), số từ, **điều kiện xuất bản** (`readiness`: `missing`, `warnings`), số bài kiểm tra, mã và đường dẫn nếu đã xuất bản, **hạn mức lượt AI** (`aiQuota`) | 404 |
| PUT | `/teacher/lessons/{lessonId}` | `{ title?, summary?, keyPoints[]?, sections[]?, heritageIds[]? }`; trường để null thì giữ nguyên, có giá trị thì thay toàn bộ | 200 chi tiết sau khi lưu | 400 theo trường (`sections[2].imageUrl`, `keyPoints[1]`...); 404; 422 `NOT_READY` (bài Đã xuất bản không còn đủ điều kiện, **không lưu gì**), 422 `BLOCKED` (bài Lưu trữ; đổi di sản khi bài không còn là Nháp) |
| DELETE | `/teacher/lessons/{lessonId}` | — | 200, xóa mềm | 404; 422 `BLOCKED` khi bài đã có bài kiểm tra (chỉ lưu trữ) |
| POST | `/teacher/lessons/{lessonId}/publish` | — | 200 `{ lesson, codeCreated, accessCode, message }`; lần đầu sinh mã (`message` là câu "Đã xuất bản bài học. Mã bài học: …"), xuất bản lại giữ mã | 404; 422 `NOT_READY` ("Chưa đủ điều kiện xuất bản bài học. Còn thiếu: …") |
| POST | `/teacher/lessons/{lessonId}/archive` | — | 200 | 404; 422 `BLOCKED` (bài Nháp; bài còn bài kiểm tra đang mở) |
| POST | `/teacher/lessons/{lessonId}/duplicate` | `{ targetClassId }` | 201 `{ lesson, skippedHeritages, levelNotice, message }` | 400 (lớp đích trùng lớp nguồn); 404; 422 `PLAN_REQUIRED` |
| GET | `/teacher/lessons/{lessonId}/materials` | — | 200 `{ images[], events[] }`: ảnh (kèm ghi công tách tác giả, giấy phép, nguồn) và sự kiện trên dòng thời gian của các di sản đang xuất bản của bài, để chọn hình và mốc thời gian | 404 |
| POST | `/teacher/lessons/{lessonId}/ai/compose` | `{ length? }` = `SHORT` / `MEDIUM` (mặc định) / `DETAILED` | 200 **bản nháp** `{ generationId, level, length, summary, keyPoints[], sections[], wordCount, warnings[], model, aiQuota, message }` | 400; 404; 422 `PLAN_REQUIRED`, `AI_QUOTA_REACHED`, `BLOCKED`; 503 `AI_UNAVAILABLE` |
| GET | `/teacher/lessons/ai-quota` | — | 200 `{ hasEffectivePlan, planName, used, max, remaining, periodStart, periodEnd }` | — |

### 2.2 Học sinh — `/student/lessons`

| Method | Đường dẫn | Nội dung | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/student/lessons/{code}` | Mã gõ chữ thường, có khoảng trắng hay dấu gạch vẫn nhận ("bh 7f3k-2q") | 200 bài theo cấp lớp em (mục 4.6). **Không ghi vào CSDL** | 400 `REQUIRED` (mã rỗng); 404 `LESSON_CODE_INVALID`; 429 `LESSON_CODE_BLOCKED`; 401/403 |

## 3. Cấp học và khung nội dung

Bài học dành cho **đúng một lớp** (chọn khi tạo, không đổi); **cấp của bài là cấp của lớp** (khối 1-5 cấp 1, 6-9 cấp 2, 10-12 cấp 3), không chọn tay,
không lưu riêng. Đổi khối lớp sang cấp khác **không tự đổi nội dung** bài đã tạo (giáo viên được cảnh báo khi đổi khối, học sinh thấy khung của cấp mới
và chỉ các phần thuộc khung ấy).

| | Cấp 1 (khối 1-5) | Cấp 2 (khối 6-9) | Cấp 3 (khối 10-12) |
|---|---|---|---|
| Nội dung chính (`lessons.summary`) | Câu chuyện **150-250 từ**, câu đơn giản (khoảng 15 từ trở xuống) | Bài đọc **300-500 từ** | Bài phân tích bối cảnh và ý nghĩa **600-900 từ** |
| Loại phần dùng được (`lesson_sections`) | `STORY` (bước câu chuyện, mỗi bước một hình), `IMAGE` | `READING`, `IMAGE` (kèm chú thích), `MILESTONE` | `READING`, `SOURCE_EXCERPT` (trích tư liệu gốc, **bắt buộc có nguồn**), `MILESTONE`, `REFLECTION` (câu hỏi gợi suy nghĩ) |
| Phần bắt buộc để xuất bản | Có ít nhất một bước câu chuyện và **mọi bước đều có hình** | Có ít nhất một mốc thời gian | Có ít nhất một đoạn trích và đoạn nào cũng có nguồn |
| Cảnh báo (không chặn) | Số từ ngoài 150-250; số bước ngoài 3-6; bước dài quá 50 từ | Số từ ngoài 300-500; số mốc ngoài 3-8; chưa có hình kèm chú thích | Số từ ngoài 600-900; quá 3 đoạn trích; đoạn trích dài quá 150 từ; số câu hỏi ngoài 1-5 |
| Luồng học (`layout`) | `STEP_BY_STEP`, **đi lần lượt**: Nghe chuyện → Xem hình → Chơi → Nhận sao | `FREE_STEPS`, **nhảy tự do**: Đọc bài → Xem dòng thời gian → Làm bài kiểm tra → Xem giải thích | `DOCUMENT`, trang tài liệu: Đọc tài liệu → Làm bài kiểm tra có giờ → Xem phân tích kết quả |
| Độ dài AI soạn | Ngắn / Vừa / Chi tiết = khoảng 150 / 200 / 250 từ | 300 / 400 / 500 | 600 / 750 / 900 |

- **Mọi cấp** cần tiêu đề (1-255 ký tự), ít nhất một di sản còn đang xuất bản, tóm tắt không rỗng và 1-10 ý chính (mỗi ý tối đa 300 ký tự).
- **Nội dung chính** đếm theo từ tách bằng khoảng trắng: số từ của `summary` cộng các đoạn `READING` thêm; tiêu đề, ý chính, chú thích, mốc, đoạn trích,
  câu hỏi gợi suy nghĩ không tính. Ở cấp 1, `summary` là cả câu chuyện kể liền và các `STORY` là câu chuyện ấy chia thành bước, nên không đếm hai lần.
- **Hình** của bước câu chuyện, hình, chú thích chọn trong **thư viện ảnh của các di sản của bài** (`GET …/materials`), luôn kèm ghi công (tác giả, giấy phép, nguồn);
  đường dẫn ngoài thư viện bị từ chối. **Mốc thời gian** có năm (năm âm là trước Công nguyên, hiển thị "179 TCN") và có thể nối với một sự kiện trên dòng thời gian
  của các di sản của bài (`eventId`); xóa sự kiện đó ở kho di sản thì mốc giữ năm và nội dung, chỉ gỡ liên kết.
- Loại phần **không thuộc khung của cấp** bị từ chối khi lưu (400, lỗi ở `sections[i].type`).
- Bớt di sản khỏi bài Nháp thì hình và sự kiện đến từ di sản đó bị gỡ khỏi các phần; gửi cùng lúc các phần tham chiếu chúng thì bị từ chối.
- Bài học **chỉ tiếng Việt**.

## 4. Quy tắc

### 4.1 Trạng thái và lưu

`DRAFT` → `PUBLISHED` ⇄ `ARCHIVED`; **không trở về Nháp**. Bài Nháp và Đã xuất bản sửa được (bài Đã xuất bản sửa thì học sinh thấy ngay và bài **vẫn phải đủ điều kiện xuất bản**,
không đủ thì từ chối và không lưu gì). Bài Lưu trữ phải xuất bản lại mới sửa được. Di sản của bài chỉ đổi khi bài còn là Nháp.

### 4.2 Gói dịch vụ của trường

Trường **không có gói hiệu lực** thì chặn **tạo bài**, **nhân bản bài** và **gọi AI** (422 `PLAN_REQUIRED`). Vẫn cho xem, sửa, xuất bản, lưu trữ bài đã có.
**Không chặn học sinh**: học sinh vẫn mở bài bằng mã.

### 4.3 Mã bài học

- Dạng `BH` + 6 ký tự lấy ngẫu nhiên bằng bộ sinh số an toàn từ 32 ký tự **không có 0, O, 1, I** (ví dụ `BH7F3K2Q`), khoảng một tỉ tổ hợp.
- **Sinh ở lần xuất bản đầu**; bài Nháp chưa có mã. Mã **giữ nguyên** khi sửa bài, khi lưu trữ rồi xuất bản lại. Không có chức năng đổi mã. Bài nhân bản có mã riêng khi được xuất bản.
- **Một mã mở cả bài học lẫn mọi bài kiểm tra** của bài; không có mã bài kiểm tra riêng.
- Không trùng toàn hệ thống, **kể cả mã của bài đã xóa mềm** (không cấp lại): ràng buộc `UX_lessons_access_code` (UNIQUE có lọc `access_code IS NOT NULL`, không lọc xóa mềm).
  Sinh trùng thì thử mã khác, tối đa 5 lần; vẫn trùng thì báo lỗi hệ thống (500) và ghi log, bài giữ nguyên là Nháp.
- Các lần xuất bản của cùng một lớp chạy **tuần tự** (khóa dòng lớp trong giao dịch), nên bấm xuất bản nhiều lần cùng lúc chỉ sinh một mã.

### 4.4 Nhân bản sang lớp khác

Mọi trạng thái trừ đã xóa, sang **một lớp khác** của chính giáo viên (chưa xóa), cần gói hiệu lực, **không tốn lượt AI**. Tạo bài **Nháp** mới chưa có mã, chép tiêu đề, tóm tắt, ý chính,
các phần nội dung và di sản. Di sản đã ẩn bị bỏ (báo số lượng) và hình, mốc lấy từ chúng bị gỡ. Lớp đích **khác cấp** thì vẫn chép nguyên nội dung kèm lời nhắc
(`levelNotice`) rằng nội dung đang theo khung của cấp cũ, nên xem lại hoặc nhờ AI soạn lại. Bài gốc không đổi.
**Chép kèm bài kiểm tra chưa làm ở luồng này**: bài kiểm tra có luồng riêng; khi luồng đó xong sẽ thêm tùy chọn chép kèm.

### 4.5 Lưu trữ và xóa

Lưu trữ chỉ với bài Đã xuất bản, bị chặn khi bài còn bài kiểm tra **đang mở** (đã phát hành, chưa tới giờ đóng); bài Lưu trữ thì học sinh không mở bằng mã nữa, kết quả cũ giữ nguyên.
Xóa mềm chỉ với bài **chưa có bài kiểm tra nào** (bài đã có bài kiểm tra chỉ lưu trữ). Di sản của bài bị ẩn sau đó thì bài vẫn giữ, thẻ di sản ấy không hiện cho học sinh,
và bài Đã xuất bản không còn di sản đang xuất bản nào sẽ không lưu lại được cho tới khi gắn di sản khác.

### 4.6 Học sinh mở bài bằng mã

Điều kiện đúng (kiểm đồng thời): mã tồn tại; bài chưa xóa và đang `PUBLISHED`; **bài thuộc đúng lớp của em**; lớp chưa xóa; trường chưa ngừng; em còn hoạt động.
Sai **bất kỳ** điều kiện nào đều trả **cùng một thông báo** (404 `LESSON_CODE_INVALID`): "Mã bài học chưa đúng hoặc không dành cho lớp của em. Em hãy kiểm tra lại mã thầy cô đã đưa." —
không cho biết mã có tồn tại ở lớp khác hay không — và **tính vào giới hạn nhập sai**.

Hợp lệ thì trả (theo cấp lớp em, lấy từ lớp hiện tại chứ không từ token): tiêu đề, lớp, **tên giáo viên** (không email, số điện thoại), cấp, `layout` (`layout`, `sequentialFlow`, các bước),
tóm tắt, ý chính, **các phần của khung cấp em** (phần của cấp khác bị lọc; mỗi hình kèm ghi công), **thẻ di sản đang xuất bản** (`id`, `slug`, tên, mô tả ngắn, ảnh đại diện, tỉnh, tọa độ,
`hasTimeline`; để dẫn sang chi tiết, bản đồ, dòng thời gian), và **bài kiểm tra đã phát hành hoặc đã đóng** của bài kèm trạng thái với riêng em:
`NOT_OPEN` chưa mở · `OPEN` đang mở · `IN_PROGRESS` em đang làm · `SUBMITTED` em đã làm · `CLOSED` đã đóng (bài kiểm tra nháp không hiện; lượt của em được xét trước giờ mở, giờ đóng).
Không nhập mã lần hai để làm bài kiểm tra. Việc học các bước **không ghi vào CSDL**; bước dở của cấp 1 và danh sách mã đã mở lưu trên thiết bị (phía giao diện).

### 4.7 Giới hạn nhập sai mã

Đếm trong bộ nhớ máy chủ (không có bảng; khởi động lại thì về 0):

| Phạm vi | Ngưỡng | Chặn |
|---|---|---|
| Mỗi tài khoản học sinh | 10 lần sai trong 10 phút | nhập mã 10 phút |
| Mỗi địa chỉ IP (cả lớp thường chung một IP) | 30 lần sai trong 10 phút | nhập mã từ IP đó 15 phút |

"Sai" gồm mọi trường hợp bị từ chối ở mục 4.6, kể cả mã **đúng của lớp khác**. Lần sai làm khóa bắt đầu và mọi lần nhập khi đang bị chặn trả **429** `LESSON_CODE_BLOCKED`
("Em đã nhập sai mã quá nhiều lần. Em hãy thử lại sau N phút."); khi bị chặn, **mã đúng cũng chưa mở được**. Mở bài thành công không xóa bộ đếm. Học sinh bị chặn vẫn dùng các chức năng khác
(xem lớp, làm bài kiểm tra đã mở). Địa chỉ mã trong đường dẫn được che khỏi log lỗi (`/api/v1/student/lessons/{code}`).

## 5. AI soạn nội dung bài học

### 5.1 Dịch vụ AI chưa có: giao diện + bản giả lập

Dịch vụ AI thật (FastAPI, tiến trình riêng, không truy cập CSDL) **chưa được làm**. Backend gọi AI qua giao diện `ILessonAiClient` theo hợp đồng
`POST /v1/compose-lesson` trong kiến trúc của nhóm; có hai bản, **chọn bằng cấu hình `Ai:Mode`**:

| `Ai:Mode` | Bản dùng | Ghi chú |
|---|---|---|
| `Simulated` (**mặc định**) | `SimulatedLessonAiClient` | **Bản giả lập**: ghép câu từ chính dữ liệu di sản (mô tả ngắn, nội dung, dòng thời gian, nguồn, ảnh) vào đúng khung của cấp. Tất định, không gọi mạng, **không gọi nhà cung cấp mô hình nào, không dùng khóa API nào**. Chất lượng chữ chỉ ở mức mẫu để chạy thử luồng; giáo viên vẫn phải duyệt |
| `Remote` | `HttpLessonAiClient` | Gọi Dịch vụ AI nội bộ thật khi nó có: `Ai:BaseUrl` (địa chỉ tuyệt đối), `Ai:InternalKey` (khóa nội bộ giữa hai dịch vụ của nhóm, đặt bằng biến môi trường `Ai__InternalKey` hoặc User Secrets, **không ghi vào appsettings**). Thiếu một trong hai thì API dừng lúc khởi động và nêu mục thiếu |

Các giá trị khác của mục `Ai`: `AttemptTimeoutSeconds` (mặc định 45, thời gian chờ mỗi lần gọi), `MaxRetries` (mặc định 2, số lần thử lại thêm). Mọi thứ phía sau giao diện (giữ lượt, nhật ký, kiểm kết quả, bản nháp, duyệt, xuất bản)
giống nhau ở hai chế độ, nên khi Dịch vụ AI thật có chỉ cần đổi `Ai:Mode`, không đổi code backend.

Bản giả lập, theo cấp:

- **Cấp 1**: câu chuyện câu ngắn (mỗi câu lấy từ nội dung di sản được cắt tối đa 10 từ, thêm câu mở đầu và câu đệm), chia 4-6 bước theo độ dài, mỗi bước một ảnh có sẵn của di sản (không có ảnh thì để trống,
  không bịa), thêm tối đa 3 hình kèm câu ngắn.
- **Cấp 2**: bài đọc có câu mở đầu, các câu từ mô tả, nội dung và dòng thời gian của di sản; tối đa 8 mốc chọn trải đều trên dòng thời gian (theo thứ tự năm, nối với sự kiện thật); tối đa 2 hình có chú thích.
  Di sản chưa có dòng thời gian thì không có mốc, giáo viên tự thêm.
- **Cấp 3**: bài phân tích bối cảnh và ý nghĩa; 1-3 đoạn trích **nguyên văn** (chuỗi con của nội dung di sản) kèm nguồn lấy từ nguồn tham chiếu của di sản; 3 câu hỏi gợi suy nghĩ.
- Số từ của nội dung chính nằm trong khoảng của cấp theo độ dài chọn (cửa sổ 10 từ), 3-5 ý chính (tối đa 7).

### 5.2 Hợp đồng gọi (backend ↔ Dịch vụ AI)

`POST {Ai:BaseUrl}/v1/compose-lesson`, JSON UTF-8 camelCase, header `X-Internal-Key` và `X-Correlation-Id` (mã truy vết của yêu cầu, trùng `traceId` trả cho client).

Gửi đi (`AiComposeRequest`): `generationId`, `lessonId`, `gradeLevel` (1, 2, 3), `length` (`SHORT`/`MEDIUM`/`DETAILED`), `currentContent`, và `heritages[]`, mỗi di sản đang xuất bản của bài:
`{ heritageId, name, shortDescription, content, timeline[{ eventId, phase, name, description, startYear, dateLabel }], sources[{ title, url }], images[{ mediaId, url, caption }] }`.
**Không gửi** họ tên, mã học sinh, tên lớp, email của giáo viên hay học sinh, mã thiết bị. Bản sao nguyên vẹn lưu ở `ai_generations.input_snapshot`.

Nhận về (`AiComposeResult`): `content` (nội dung chính tiếng Việt), `steps[{ kind, text, mediaId, year, eventId, title }]` với `kind` là `STORY`, `IMAGE`, `MILESTONE` hoặc `REFLECTION`,
`sourceQuotes[{ heritageId, quote, source }]` (chỉ cấp 3), `keyPoints[]`, `gradeLevel`, `length`, `model`, `usage{ inputTokens, outputTokens }`.

Mã phản hồi: `200`; `401` sai khóa; `422` đầu vào sai lược đồ; `5xx`/`408`/`429`/lỗi mạng/quá thời gian là lỗi **thử lại được**; `400`, `401`, `403`, `422` là lỗi **không thử lại**.

Backend **kiểm kết quả** trước khi dùng: nội dung rỗng hoặc không có ý chính nào là lỗi đầu ra (thử lại như lỗi mạng); ý chính cắt về tối đa 7 ý, mỗi ý 300 ký tự; hình phải nằm trong danh sách ảnh đã gửi
(không thì bỏ ảnh); mốc phải có năm (liên kết sự kiện không có trong danh sách thì gỡ liên kết); đoạn trích phải là chuỗi con nguyên văn của nội dung di sản và có nguồn (không thì bỏ); phần không thuộc khung của cấp bị bỏ.

### 5.3 Quy trình mỗi lần gọi

1. Kiểm bài: của giáo viên, **Nháp hoặc Đã xuất bản** (Lưu trữ thì không), có ít nhất một di sản đang xuất bản.
2. **Giữ chỗ lượt AI** trong một giao dịch có khóa dòng trường: gói hiệu lực (422 `PLAN_REQUIRED`), còn lượt (422 `AI_QUOTA_REACHED`), thêm dòng `ai_generations` `PROCESSING`
   (`task_type` = `LESSON_SUMMARY`, `lesson_id`, `requested_by`, `input_snapshot`, `params`). Khóa để hai giáo viên bấm cùng lúc không vượt hạn mức.
3. Gọi dịch vụ AI **ngoài giao dịch**: chờ tối đa 45 giây mỗi lần, **thử lại tối đa 2 lần** trong cùng một dòng nhật ký.
4. Thành công: dòng `SUCCEEDED`, `model`, `raw_output`, `completed_at`; trả **bản nháp** (không lưu vào bài, không xuất bản). Giáo viên xem, sửa rồi lưu bằng `PUT`; bản nháp kèm `wordCount` và `warnings` về độ dài.
5. Hết lượt thử: dòng `FAILED` kèm `error_message`, **không tính lượt**, trả 503 `AI_UNAVAILABLE` ("Dịch vụ AI đang bận. Lượt này không bị tính. …"), nội dung bài giữ nguyên.
   Người gọi ngắt kết nối giữa chừng thì dòng cũng được đóng `FAILED`.
6. "Tạo lại" là một lần gọi mới: một lượt, một dòng nhật ký mới.

### 5.4 Hạn mức lượt AI của gói

- Kỳ = [`start_date`, `end_date`] của gói **hiệu lực**, ranh giới theo **giờ Việt Nam** (00:00 ngày bắt đầu tới 00:00 sau ngày kết thúc).
- **Lượt đã dùng** = số dòng `ai_generations` của **mọi giáo viên thuộc trường** (`requested_by` → `users.organization_id`), **gắn với bài học**, trạng thái `SUCCEEDED` hoặc `PROCESSING`, tạo trong kỳ.
  `FAILED` không tính. Dòng `PROCESSING` quá 5 phút coi là bị bỏ dở (máy chủ khởi động lại giữa chừng) và không tính.
- Dịch, tóm tắt di sản do quản trị hệ thống gọi (dòng gắn với di sản, người gọi không thuộc trường nào) **không bao giờ tính** vào hạn mức của một trường và không cần gói.
- Lượt không dùng hết không chuyển sang kỳ sau; nâng cấp bắt đầu kỳ mới (lượt đã dùng trong ngày nâng cấp vẫn tính vào kỳ mới).
- `GET /teacher/lessons/ai-quota` và `aiQuota` trong chi tiết bài cho giao diện hiện "còn N lượt".

## 6. Nhật ký

- **Lượt gọi AI** ghi ở `ai_generations` (truy vết đầu vào, tham số, mô hình, kết quả thô, trạng thái, lỗi), không ghi vào `audit_logs`.
- Theo đặc tả, `audit_logs` chỉ ghi các hành động đã liệt kê (không có tạo, xuất bản, lưu trữ, nhân bản bài học, cũng không có việc mở bài học). Luồng này **không thêm mã thao tác mới**;
  nếu nhóm quyết định ghi nhật ký xuất bản hay lưu trữ bài học thì thêm mã vào đặc tả trước rồi mới thêm vào `AuditAction`.
- Mật khẩu tạm, mã bài học không xuất hiện trong `audit_logs`.

## 7. Mã lỗi

| Mã HTTP | `errorCode` | Khi nào |
|---|---|---|
| 400 | `REQUIRED`, `INVALID` | Thiếu hoặc sai trường; loại phần không thuộc khung của cấp; hình ngoài thư viện; sự kiện ngoài di sản của bài; lớp đích trùng lớp nguồn; độ dài AI lạ |
| 401 / 403 | `FORBIDDEN`... | Chưa đăng nhập; sai vai trò; học sinh chưa đổi mật khẩu lần đầu (`PASSWORD_CHANGE_REQUIRED`) |
| 404 | `BLOCKED` | Lớp hoặc bài không phải của giáo viên (kể cả cùng trường), hoặc không tồn tại |
| 404 | `LESSON_CODE_INVALID` | Mã sai, bài chưa xuất bản, đã lưu trữ, đã xóa, hoặc không thuộc lớp của em |
| 422 | `NOT_READY` | Xuất bản hoặc lưu bài Đã xuất bản khi chưa đủ điều kiện ("Chưa đủ điều kiện …. Còn thiếu: …") |
| 422 | `BLOCKED` | Bài Lưu trữ; đổi di sản khi không là Nháp; xóa bài đã có bài kiểm tra; lưu trữ bài Nháp hoặc bài còn bài kiểm tra đang mở; gọi AI khi bài Lưu trữ hoặc không còn di sản đang xuất bản |
| 422 | `PLAN_REQUIRED` | Trường không có gói hiệu lực khi tạo, nhân bản bài hoặc gọi AI |
| 422 | `AI_QUOTA_REACHED` | Trường đã dùng hết lượt AI của kỳ |
| 429 | `LESSON_CODE_BLOCKED` | Nhập sai mã quá nhiều lần (theo tài khoản hoặc IP) |
| 503 | `AI_UNAVAILABLE` | Dịch vụ AI quá thời gian chờ hoặc lỗi sau các lần thử |

## 8. Chạy thử và kiểm thử

```bash
dotnet test                          # 590 test; có LocalDB thì chạy thêm các test trên database tạm của SQL Server (tự xóa)
# Smoke test qua HTTP thật trên database tạm (không gửi thư thật: Email__Mode=Log; AI chạy bản giả lập):
ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_kiem_thu_smoke;Trusted_Connection=True;TrustServerCertificate=True" \
Email__Mode=Log Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5091 > api.log 2>&1 &
BASE=http://localhost:5091 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-bai-hoc-theo-cap.sh
```

Smoke (`scripts/smoke-bai-hoc-theo-cap.sh`, 99 bước): xuất bản hai di sản nạp sẵn, tạo trường, giáo viên, ba lớp (cấp 1, 2, 3); mỗi lớp một bài: tạo, xuất bản khi chưa đủ điều kiện (422), AI soạn, lưu bản nháp, xuất bản và nhận mã,
học sinh đăng nhập và mở bài (kể cả gõ mã chữ thường có khoảng trắng); mã của lớp khác và mã không tồn tại (404 cùng thông báo); sửa, giữ mã, lưu trữ, học sinh hết mở được, xuất bản lại, nhân bản, xóa; tạo lại AI và hạn mức; 10 lần nhập sai bị chặn (429); mã không lộ trong nhật ký.
Script cố ý nhập sai mã 12 lần nên chạy lần thứ ba trong 10 phút trên cùng một API sẽ chạm giới hạn theo IP.

Bộ test (xUnit) của luồng: quy tắc thuần (khung theo cấp, điều kiện xuất bản và cảnh báo, mã bài học), bản giả lập AI (mọi cấp × ba độ dài, số từ, bước, mốc, trích nguyên văn), bản gọi HTTP với máy chủ giả (đường dẫn, header,
phân loại lỗi), giới hạn nhập sai mã (tua giờ), hạn mức lượt AI (ranh giới giờ Việt Nam, nâng cấp, dòng bỏ dở), sinh mã khi trùng, ràng buộc CHECK và UNIQUE; API giáo viên và học sinh qua `WebApplicationFactory`
(phạm vi theo giáo viên, khung theo cấp, xuất bản, lưu trữ, nhân bản, AI thử lại và lỗi, 404 chung, trạng thái bài kiểm tra); trên SQL Server LocalDB: lược đồ do migration tạo, ràng buộc, luồng đầy đủ,
**nhiều yêu cầu AI cùng lúc không vượt hạn mức**, **xuất bản cùng lúc chỉ một mã**, xóa giai đoạn có mốc nối tới.

## 9. Điểm hiện thực cần đồng bộ với đặc tả

| Điểm | Hiện thực | Cần làm |
|---|---|---|
| Cấu trúc `steps` trong hợp đồng AI | Đặc tả chỉ ghi "mảng"; hiện thực chốt kiểu phần tử như mục 5.2 | Ghi vào hợp đồng khi viết Dịch vụ AI thật |
| Nội dung chính | `lessons.summary` là nội dung chính; các phần `lesson_sections` là cấu trúc kèm theo; bước câu chuyện cấp 1 không đếm thêm vào số từ | Ghi rõ vào mô tả bảng `lessons` của v4 nếu nhóm đồng ý |
| Chép kèm bài kiểm tra khi nhân bản | Chưa có (bài kiểm tra chưa có API) | Làm cùng luồng Bài kiểm tra |
| Xuất bản, lưu trữ bài học vào nhật ký thao tác | Không ghi (theo danh sách hành động của đặc tả) | Quyết định của nhóm |
| Lưu trữ bài Nháp | Không cho (trạng thái chỉ đi DRAFT → PUBLISHED ⇄ ARCHIVED); bài Nháp thì xóa | Giữ như đặc tả |
| Chọn trùng di sản khi tạo | Từ chối (400), không tự gộp | — |
