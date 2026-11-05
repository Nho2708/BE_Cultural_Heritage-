# Giọng đọc tiếng Việt tự host cho cấp 1

Nút Nghe của cấp 1 xin giọng từ máy chủ trước, thay cho giọng của thiết bị (thiết bị không có giọng tiếng Việt thì đọc chữ Việt bằng giọng Anh). Máy chủ tự tạo giọng bằng
VieNeu-TTS giọng "Mai Anh" (cùng giọng thuyết minh tạo sẵn của tóm tắt), qua Dịch vụ AI nội bộ; không dùng dịch vụ ngoài. Đặc tả: `doc/huong-moi/09-dac-ta-cap-1-moi.md` mục 7.

Sản phẩm có ba đường âm thanh:

| Đường | Dùng cho | Nơi tạo và lưu |
|---|---|---|
| Thuyết minh tạo sẵn | Tab "Nghe" của di sản (tóm tắt ngắn, năm ngôn ngữ) | Tạo bằng công cụ ngoại tuyến trên máy phát triển rồi tải lên qua API quản trị; bảng `heritage_narrations`, thư mục `thuyet-minh/`. Máy chủ không tạo giọng cho đường này (xem `docs/da-ngon-ngu.md` mục 14) |
| Giọng của máy chủ cho cấp 1 | Nút Nghe và tự đọc to của khu học cấp 1 (web học tập, app chế độ Học sinh) | Máy chủ tạo khi cần hoặc tạo trước (tài liệu này); bảng `speech_clips`, thư mục `giong-doc/`. Chỉ tạo giọng mới cho nội dung đã xuất bản người gọi được xem (mục 3) |
| Giọng của thiết bị | Mọi chỗ còn lại, và khi máy chủ trả liên kết `null` | Trình duyệt hoặc điện thoại tự đọc, chỉ khi thiết bị có giọng tiếng Việt |

## 1. Cấu hình

Mục `Tts` của cấu hình (`appsettings.json`):

| Khóa | Mặc định | Ý nghĩa |
|---|---|---|
| `Tts:Provider` | `SelfHosted` | `SelfHosted` giọng tự host có bộ nhớ đệm · `FptAi` dịch vụ ngoài, chỉ khi cấu hình rõ (chưa có khóa thì trả liên kết null) · `None` luôn trả null |
| `Tts:Voice` | `vieneu-mai-anh` | Mã giọng gửi sang Dịch vụ AI và dùng làm tên thư mục trong kho tệp (chữ thường, số, gạch nối, tối đa 64 ký tự; sai thì dùng giọng mặc định) |
| `Tts:WaitSeconds` | `30` | Yêu cầu nghe ngay chờ tối đa chừng này giây rồi trả null; lần sinh vẫn chạy tiếp và được nhớ đệm |
| `Tts:AiTimeoutSeconds` | `120` | Thời gian chờ một lần gọi Dịch vụ AI |
| `Tts:PrefetchRetrySeconds`, `Tts:PrefetchMaxAttempts` | `5`, `3` | Nghỉ và số lần thử một đoạn tạo trước bị lỗi (vd Dịch vụ AI đang nạp mô hình) |
| `Tts:OnlyPublishedContent` | `true` | Chỉ tạo giọng mới cho chữ thuộc nội dung đã xuất bản mà người gọi được xem (mục 3). `false` trở lại như cũ: mọi người đã đăng nhập, mọi chữ không quá 1000 ký tự. Đọc ở `SpeechAccessOptions` (tầng Application) |

`SelfHosted` dùng chung `Ai:BaseUrl` và `Ai:InternalKey` với các việc AI khác (tiêu đề `X-Internal-Key`). `SelfHosted` là mặc định của mọi máy (Google Cloud, máy demo, máy dev); máy không có Dịch vụ AI (hoặc thiếu `Ai:InternalKey`) thì API trả liên kết `null` và giao diện dùng giọng thiết bị, không lỗi.
Nguồn giọng chọn lúc dịch vụ được tạo lần đầu, không đọc ngay lúc đăng ký, nên cấu hình ghi đè về sau vẫn có hiệu lực.

## 2. API

- `POST /api/v1/tts` `{ "text": "..." }` → 200 `{ "audioUrl": "/files/giong-doc/vieneu-mai-anh/<băm>.mp3" | null }`. Cần đăng nhập, giới hạn tần suất công khai. `null` khi chữ rỗng hoặc dài quá 1000 ký tự (sau khi gộp khoảng trắng),
  chữ chưa có tiếng mà không thuộc nội dung người gọi được xem (mục 3), nguồn là `None` hoặc `FptAi` chưa có khóa, Dịch vụ AI lỗi, 503 (mô hình chưa nạp xong) hoặc chưa sinh kịp trong `Tts:WaitSeconds`:
  giao diện lùi về giọng thiết bị nếu thiết bị có giọng tiếng Việt. Không bao giờ trả lỗi vì chữ ngoài phạm vi.
- `POST /api/v1/tts/prefetch` `{ "texts": ["...", ...] }` → 202 `{ "queued": n }`. Cần đăng nhập. Tối đa 40 đoạn, mỗi đoạn không quá 1000 ký tự sau khi cắt hai đầu (quá thì 400); đoạn rỗng bị bỏ,
  đoạn không thuộc nội dung người gọi được xem cũng bị bỏ (không lỗi); `queued` là số đoạn được nhận vào hàng đợi (đoạn trùng hoặc đã chờ sẵn không tính). Nguồn khác `SelfHosted` thì 202 với `queued: 0`.

## 3. Chỉ tạo giọng cho nội dung đã xuất bản (`SpeechRequestService`, `SpeechTextPolicy`)

Bật bằng `Tts:OnlyPublishedContent` (mặc định). Mỗi yêu cầu nghe: chuẩn hóa chữ → **tiếng đã có** trong `speech_clips` cho giọng hiện tại thì trả như trước, cho mọi người (tiếng đó đã được tạo từ nội dung)
→ chữ **không thuộc bộ chữ của người gọi** thì trả `null` (tạo trước thì bỏ đoạn đó) và ghi nhật ký mức Information gồm mã người dùng và 8 ký tự đầu của băm, không ghi chữ → còn lại tạo giọng như trước.
Việc máy chủ tự tạo trước (lúc xuất bản, lúc em bắt đầu lượt) không đi qua quy tắc này.

Chữ được tạo giọng mới khi:

- Là **câu cố định của khu học cấp 1** (`LevelOneSpeech.FixedPhrases`): lời hướng dẫn từng dạng câu, lời khen khi câu chưa có giải thích, lời gợi ý thử lại, lời chỉ đáp án không cần nhãn, số sao và lời khen chung,
  lời chào kèm các lời cổ vũ cố định, lời báo mạng, tiêu đề phần xem lại đáp án. Đổi câu chữ ở giao diện thì đổi cùng lúc ở đây.
- Là chữ **máy chủ vừa viết riêng cho người gọi** (ghi nhận trong bộ nhớ 2 giờ): lời đọc số sao và lời khen ở trang kết quả của lượt cấp 1, lời đọc cả câu của lượt đang làm (ghi nhận lúc mở lượt).
- Thuộc **bộ chữ của người gọi** (nhớ 3 phút mỗi người):
  - Học sinh lớp cấp 1 (khối 1-5), còn hoạt động: bài học Đã xuất bản, chưa xóa của lớp em (từng câu như lúc tạo trước, tiêu đề phần Kể chuyện, chú thích cả đoạn, các ý cần nhớ đọc liền, câu mang về nhà kèm lời nhắc;
    câu bất kỳ nằm trong một đoạn Kể chuyện, chú thích, tóm tắt hay ý chính, vì web, app và máy chủ tách câu hơi khác nhau), thẻ album của di sản đã xuất bản của các bài, bài chơi đã phát hành hoặc đã đóng của các bài
    (nội dung, giải thích, lời đọc cả câu, lời phản hồi, lời chỉ đáp án theo nhãn, lời gợi ý khi ghép cặp hay sắp xếp đúng một phần, lời giới thiệu bài chơi), lượt em đang làm, khung "Lớp của em",
    lời chào nhắc tên bài hoặc số sao.
  - Học sinh cấp 2, 3, giáo viên, quản trị trường, quản trị hệ thống: rỗng. Giao diện các vai trò này không xin giọng của máy chủ (xem trước của giáo viên trong web quản trị dùng giọng của thiết bị).

Còn lại dùng giọng của thiết bị: thẻ học sinh ở trang Hồ sơ (có số sao và số ngày học), câu ôn của bước Nhớ lại lấy từ bài đã lưu trữ, thông báo lỗi máy chủ đọc trong bảng phản hồi, và trong tối đa
3 phút sau khi thầy cô xuất bản bài mới nếu tiếng tạo trước chưa xong.

## 4. Cách hoạt động (`SelfHostedTtsService`)

1. **Chuẩn hóa và băm.** Cắt hai đầu, gộp mọi dãy khoảng trắng thành một dấu cách; băm SHA-256 của `{giọng}\n{chữ}` (quy tắc thuần ở `SpeechRules`, có kiểm thử).
2. **Bộ nhớ đệm bền.** Bảng `speech_clips` có dòng cho (băm, giọng) thì trả ngay đường dẫn công khai của tệp MP3 trong kho (`giong-doc/{giọng}/{băm}.mp3`, phục vụ ở `/files`, Cloudinary lưu loại raw).
3. **Chưa có thì sinh.** Gọi `POST {Ai:BaseUrl}/v1/tts` `{ text, voice }`; 200 trả `audio/mpeg` kèm `X-Duration-Ms`. Lưu tệp vào kho rồi thêm dòng; hai lần sinh cùng băm chạm nhau ở UNIQUE (vd hai máy chủ) thì lần thua dùng lại dòng của lần thắng.
4. **Mỗi chữ một lần sinh.** Các yêu cầu cùng băm chạy đồng thời dùng chung một lần sinh (single-flight); lần sinh chạy tách khỏi yêu cầu khởi động nó nên người nghe hết kiên nhẫn thì kết quả vẫn được nhớ đệm.
5. **Mỗi lúc một yêu cầu sang Dịch vụ AI** (`PriorityGate`), hai bậc ưu tiên: yêu cầu nghe ngay luôn được chọn trước việc tạo trước chạy nền; cùng bậc thì vào trước ra trước. Việc đang chạy không bị ngắt. Nếu việc nền cho đúng đoạn chữ đó đang chờ
   mà có người cần nghe ngay thì việc nền được nâng bậc.
6. **Lỗi.** Mọi lỗi (mạng, mã khác 200, tệp không phải MP3, quá hạn) trả null và ghi log mức Information chỉ có mã trả về, loại lỗi và 8 ký tự đầu của băm; **không bao giờ ghi nội dung chữ** (có thể là nội dung bài học).

## 5. Hàng đợi tạo trước

`ISpeechPrefetcher.Enqueue` xếp chữ vào kênh có giới hạn (2000 đoạn, đầy thì bỏ bớt), bỏ trùng theo băm. `SpeechPrefetchWorker` (tác vụ nền, chỉ chạy khi nguồn là `SelfHosted`) lấy từng đoạn: đã có đệm thì bỏ qua,
chưa có thì sinh ở bậc thấp; lỗi thì nghỉ rồi xếp lại tối đa `Tts:PrefetchMaxAttempts` lần.

Tạo trước lúc xuất bản (`SpeechWarmUp`, chỉ khi `SelfHosted`; xếp vào hàng đợi rồi đi tiếp, không chờ sinh và không bao giờ làm hỏng lần xuất bản):

- Xuất bản bài học của lớp khối 1-5 (cấp 1): tiêu đề và từng câu của các phần Kể chuyện, từng câu chú thích của phần Hình, từng câu của các ý chính, câu mang về nhà (`AlbumRules.TakeHomeQuestion`).
- Phát hành bài kiểm tra của bài học thuộc lớp khối 1-5: nội dung và giải thích của từng câu hỏi, chữ trên thẻ hình của câu Chọn hình đúng, lời đọc cả câu như bài chơi web
  đọc (lời câu, phương án, lời hướng dẫn; trừ ghép cặp và sắp xếp vì mỗi lượt xáo một kiểu) và lời phản hồi có giải thích theo vị trí câu (bài xáo câu thì cả ba lời khen).
  Chữ dựng ở `LevelOneSpeech`, khớp từng ký tự với lời đọc của web; hai bên giữ cùng bộ câu mẫu trong kiểm thử (`LevelOneSpeechTests` và `quiz/speech.test.ts` của web học tập).
- Học sinh cấp 1 bắt đầu một lượt bài chơi: lời đọc cả câu của mọi câu theo đúng thứ tự câu, phương án, hình và bước đang hiện cho em (`AttemptService`, chỉ lúc tạo lượt mới).
- Chữ đi qua `PlainText.FromMarkdown` trước khi tách câu (dấu kết câu `. ! ? …`, có thể kèm ngoặc hoặc nháy đóng, rồi khoảng trắng), giống cách giao diện chuẩn bị chữ để đọc, nên băm khớp với lời gọi nghe ngay.

## 6. Triển khai

Compose của máy chủ Google Cloud: dịch vụ `ai` gắn volume có tên `mo-hinh` ở `/srv/mo-hinh` với `HF_HOME=/srv/mo-hinh` (mô hình khoảng 530 MB tải một lần), kèm việc chạy một lần `ai-mo-hinh` đặt quyền ghi cho người dùng
uid 10002 của ảnh AI. Xem `deploy/google-cloud/README.md`, mục "Giọng đọc tự host".

## 7. Kiểm thử

Quy tắc thuần (`SpeechRulesTests`, `SpeechWarmUpTextsTests`), cổng ưu tiên (`PriorityGateTests`), dịch vụ với Dịch vụ AI giả (`SelfHostedTtsServiceTests`: sai rồi trúng chỉ một lời gọi, đồng thời một lần sinh,
mỗi lúc một lời gọi, ưu tiên và nâng bậc, chờ có hạn, 503 và các lỗi, hàng đợi tạo trước, nhật ký không chứa chữ), API (`SpeechApiTests`: hợp đồng, tệp phục vụ ở `/files`, 202 và kiểm lỗi của prefetch, tạo trước lúc xuất bản bài
lớp khối 2 mà không phải lớp khối 8, nguồn `FptAi` và `None` không đổi), quy tắc chỉ tạo giọng cho nội dung đã xuất bản (`SpeechTextPolicyTests` trên SQLite: bài của lớp em, chữ lạ, bài nháp,
lớp khác, giáo viên, chữ ghi nhận, tiếng có sẵn, tắt quy tắc; `SpeechContentRuleApiTests` qua HTTP) và SQL Server LocalDB (`SpeechSqlServerTests`: migration, ràng buộc, hai máy chủ cùng sinh).
