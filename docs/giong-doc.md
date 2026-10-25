# Giọng đọc tiếng Việt tự host cho cấp 1

Nút Nghe của cấp 1 xin giọng từ máy chủ trước, thay cho giọng của thiết bị (thiết bị không có giọng tiếng Việt thì đọc chữ Việt bằng giọng Anh). Máy chủ tự tạo giọng bằng
VieNeu-TTS giọng "Mai Anh" (cùng giọng thuyết minh tạo sẵn của tóm tắt), qua Dịch vụ AI nội bộ; không dùng dịch vụ ngoài. Đặc tả: `doc/huong-moi/09-dac-ta-cap-1-moi.md` mục 7.

## 1. Cấu hình

Mục `Tts` của cấu hình (`appsettings.json`):

| Khóa | Mặc định | Ý nghĩa |
|---|---|---|
| `Tts:Provider` | `FptAi` | `FptAi` như trước (chưa có khóa thì trả liên kết null) · `SelfHosted` giọng tự host có bộ nhớ đệm · `None` luôn trả null |
| `Tts:Voice` | `vieneu-mai-anh` | Mã giọng gửi sang Dịch vụ AI và dùng làm tên thư mục trong kho tệp (chữ thường, số, gạch nối, tối đa 64 ký tự; sai thì dùng giọng mặc định) |
| `Tts:WaitSeconds` | `30` | Yêu cầu nghe ngay chờ tối đa chừng này giây rồi trả null; lần sinh vẫn chạy tiếp và được nhớ đệm |
| `Tts:AiTimeoutSeconds` | `120` | Thời gian chờ một lần gọi Dịch vụ AI |
| `Tts:PrefetchRetrySeconds`, `Tts:PrefetchMaxAttempts` | `5`, `3` | Nghỉ và số lần thử một đoạn tạo trước bị lỗi (vd Dịch vụ AI đang nạp mô hình) |

`SelfHosted` dùng chung `Ai:BaseUrl` và `Ai:InternalKey` với các việc AI khác (tiêu đề `X-Internal-Key`). Google Cloud đặt `SelfHosted` (`deploy/google-cloud/tao-cau-hinh-may-chu.ps1`); máy demo giữ mặc định.
Nguồn giọng chọn lúc dịch vụ được tạo lần đầu, không đọc ngay lúc đăng ký, nên cấu hình ghi đè về sau vẫn có hiệu lực.

## 2. API

- `POST /api/v1/tts` `{ "text": "..." }` → 200 `{ "audioUrl": "/files/giong-doc/vieneu-mai-anh/<băm>.mp3" | null }`. Cần đăng nhập, giới hạn tần suất công khai. `null` khi chữ rỗng hoặc dài quá 1000 ký tự (sau khi gộp khoảng trắng),
  nguồn là `None` hoặc `FptAi` chưa có khóa, Dịch vụ AI lỗi, 503 (mô hình chưa nạp xong) hoặc chưa sinh kịp trong `Tts:WaitSeconds`: giao diện lùi về giọng thiết bị nếu thiết bị có giọng tiếng Việt.
- `POST /api/v1/tts/prefetch` `{ "texts": ["...", ...] }` → 202 `{ "queued": n }`. Cần đăng nhập. Tối đa 40 đoạn, mỗi đoạn không quá 1000 ký tự sau khi cắt hai đầu (quá thì 400); đoạn rỗng bị bỏ;
  `queued` là số đoạn được nhận vào hàng đợi (đoạn trùng hoặc đã chờ sẵn không tính). Nguồn khác `SelfHosted` thì 202 với `queued: 0`.

## 3. Cách hoạt động (`SelfHostedTtsService`)

1. **Chuẩn hóa và băm.** Cắt hai đầu, gộp mọi dãy khoảng trắng thành một dấu cách; băm SHA-256 của `{giọng}\n{chữ}` (quy tắc thuần ở `SpeechRules`, có kiểm thử).
2. **Bộ nhớ đệm bền.** Bảng `speech_clips` có dòng cho (băm, giọng) thì trả ngay đường dẫn công khai của tệp MP3 trong kho (`giong-doc/{giọng}/{băm}.mp3`, phục vụ ở `/files`, Cloudinary lưu loại raw).
3. **Chưa có thì sinh.** Gọi `POST {Ai:BaseUrl}/v1/tts` `{ text, voice }`; 200 trả `audio/mpeg` kèm `X-Duration-Ms`. Lưu tệp vào kho rồi thêm dòng; hai lần sinh cùng băm chạm nhau ở UNIQUE (vd hai máy chủ) thì lần thua dùng lại dòng của lần thắng.
4. **Mỗi chữ một lần sinh.** Các yêu cầu cùng băm chạy đồng thời dùng chung một lần sinh (single-flight); lần sinh chạy tách khỏi yêu cầu khởi động nó nên người nghe hết kiên nhẫn thì kết quả vẫn được nhớ đệm.
5. **Mỗi lúc một yêu cầu sang Dịch vụ AI** (`PriorityGate`), hai bậc ưu tiên: yêu cầu nghe ngay luôn được chọn trước việc tạo trước chạy nền; cùng bậc thì vào trước ra trước. Việc đang chạy không bị ngắt. Nếu việc nền cho đúng đoạn chữ đó đang chờ
   mà có người cần nghe ngay thì việc nền được nâng bậc.
6. **Lỗi.** Mọi lỗi (mạng, mã khác 200, tệp không phải MP3, quá hạn) trả null và ghi log mức Information chỉ có mã trả về, loại lỗi và 8 ký tự đầu của băm; **không bao giờ ghi nội dung chữ** (có thể là nội dung bài học).

## 4. Hàng đợi tạo trước

`ISpeechPrefetcher.Enqueue` xếp chữ vào kênh có giới hạn (2000 đoạn, đầy thì bỏ bớt), bỏ trùng theo băm. `SpeechPrefetchWorker` (tác vụ nền, chỉ chạy khi nguồn là `SelfHosted`) lấy từng đoạn: đã có đệm thì bỏ qua,
chưa có thì sinh ở bậc thấp; lỗi thì nghỉ rồi xếp lại tối đa `Tts:PrefetchMaxAttempts` lần.

Tạo trước lúc xuất bản (`SpeechWarmUp`, chỉ khi `SelfHosted`; xếp vào hàng đợi rồi đi tiếp, không chờ sinh và không bao giờ làm hỏng lần xuất bản):

- Xuất bản bài học của lớp khối 1-5 (cấp 1): tiêu đề và từng câu của các phần Kể chuyện, từng câu chú thích của phần Hình, từng câu của các ý chính, câu mang về nhà (`AlbumRules.TakeHomeQuestion`).
- Phát hành bài kiểm tra của bài học thuộc lớp khối 1-5: nội dung và giải thích của từng câu hỏi, chữ trên thẻ hình của câu Chọn hình đúng, lời đọc cả câu như bài chơi web
  đọc (lời câu, phương án, lời hướng dẫn; trừ ghép cặp và sắp xếp vì mỗi lượt xáo một kiểu) và lời phản hồi có giải thích theo vị trí câu (bài xáo câu thì cả ba lời khen).
  Chữ dựng ở `LevelOneSpeech`, khớp từng ký tự với lời đọc của web; hai bên giữ cùng bộ câu mẫu trong kiểm thử (`LevelOneSpeechTests` và `quiz/speech.test.ts` của web học tập).
- Học sinh cấp 1 bắt đầu một lượt bài chơi: lời đọc cả câu của mọi câu theo đúng thứ tự câu, phương án, hình và bước đang hiện cho em (`AttemptService`, chỉ lúc tạo lượt mới).
- Chữ đi qua `PlainText.FromMarkdown` trước khi tách câu (dấu kết câu `. ! ? …`, có thể kèm ngoặc hoặc nháy đóng, rồi khoảng trắng), giống cách giao diện chuẩn bị chữ để đọc, nên băm khớp với lời gọi nghe ngay.

## 5. Triển khai

Compose của máy chủ Google Cloud: dịch vụ `ai` gắn volume có tên `mo-hinh` ở `/srv/mo-hinh` với `HF_HOME=/srv/mo-hinh` (mô hình khoảng 530 MB tải một lần), kèm việc chạy một lần `ai-mo-hinh` đặt quyền ghi cho người dùng
uid 10002 của ảnh AI. Xem `deploy/google-cloud/README.md`, mục "Giọng đọc tự host".

## 6. Kiểm thử

Quy tắc thuần (`SpeechRulesTests`, `SpeechWarmUpTextsTests`), cổng ưu tiên (`PriorityGateTests`), dịch vụ với Dịch vụ AI giả (`SelfHostedTtsServiceTests`: sai rồi trúng chỉ một lời gọi, đồng thời một lần sinh,
mỗi lúc một lời gọi, ưu tiên và nâng bậc, chờ có hạn, 503 và các lỗi, hàng đợi tạo trước, nhật ký không chứa chữ), API (`SpeechApiTests`: hợp đồng, tệp phục vụ ở `/files`, 202 và kiểm lỗi của prefetch, tạo trước lúc xuất bản bài
lớp khối 2 mà không phải lớp khối 8, nguồn `FptAi` và `None` không đổi) và SQL Server LocalDB (`SpeechSqlServerTests`: migration, ràng buộc, hai máy chủ cùng sinh).
