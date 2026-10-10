# BE_Cultural_Heritage- — Backend DSVHVN

Backend của **DSVHVN — nền tảng giáo dục di sản văn hóa cho trường học** (đồ án FA26SE171, FPT University HCM):
giáo viên tạo bài học và bài kiểm tra cho lớp từ dữ liệu của Cục Di sản văn hóa; học sinh có tài khoản (đăng nhập bằng mã học sinh
+ mật khẩu) ở web học tập và app mobile; gói dịch vụ bán cho trường. ASP.NET Core 9 (controllers) + EF Core 9 + SQL Server 2019.
Web quản trị và khu học của học sinh chỉ có tiếng Việt; **phần công khai (di sản, tóm tắt, nghe thuyết minh) có 5 ngôn ngữ**: Việt, Anh, Hàn, Trung giản thể, Nhật.

Làm theo từng luồng (vertical slice): bảng của luồng → migration → API → UI → bổ sung cột bằng migration mới.
Lược đồ nguồn: `doc/database/dsvhvn-v3.dbml` (27 bảng); backend đã có **đủ 27 bảng** trong một migration khởi tạo, tên bảng,
tên cột giữ đúng DBML (`snake_case`). Phần CSDL v4 (`doc/database/dsvhvn-v4.dbml`) được thêm **dần theo từng luồng bằng migration mới** (hiện có bảng
`lesson_sections`, các cột mới của `ai_generations` và `questions`, `answers`, năm bảng đa ngôn ngữ, bảng `heritage_models` của mô hình 3D, bảng `attempt_answer_parts` và hai cột mới của `attempt_answers` của luồng học tập, hai bảng `guest_devices` và `guest_activities` của khách tham quan, bảng `ad_unlocks` của quảng cáo Google có thưởng, tổng 38 bảng) — xem [docs/co-so-du-lieu.md](docs/co-so-du-lieu.md).

| Luồng | Trạng thái | Tài liệu |
|---|---|---|
| Cơ sở dữ liệu v3: 27 bảng, migration khởi tạo, nạp 34 tỉnh/thành và di sản đợt 1, đối chiếu với DBML | Xong | [docs/co-so-du-lieu.md](docs/co-so-du-lieu.md) |
| Nền tảng: đăng nhập web quản trị bằng email/username, đặt mật khẩu qua email, hồ sơ, ADMIN quản lý trường, ORG_ADMIN quản lý giáo viên, nhật ký thao tác | Xong phần backend | [docs/nen-tang-to-chuc.md](docs/nen-tang-to-chuc.md) · [docs/smoke-nen-tang.md](docs/smoke-nen-tang.md) |
| Gửi thư đặt mật khẩu: ghi ra log (mặc định) hoặc gửi thật qua SMTP (Gmail…), bật/tắt bằng `Email:Mode` | Xong | [docs/gui-thu.md](docs/gui-thu.md) |
| Kho di sản (ADMIN): quản lý di sản (bản nháp, xuất bản khi đủ điều kiện, ẩn, hiện lại, xóa mềm), timeline (giai đoạn, sự kiện), ảnh có ghi công, ảnh đại diện, nguồn tham chiếu, danh sách 34 tỉnh; ghi nhật ký xuất bản và ẩn | Xong phần backend | [docs/kho-di-san.md](docs/kho-di-san.md) |
| Tải tệp lên (ADMIN): tải **ảnh** (JPG, PNG, WEBP, tối đa 5 MB; kiểm theo nội dung tệp, bỏ EXIF, sinh ảnh thu nhỏ, tên ngẫu nhiên, ghi công vẫn bắt buộc) cho di sản và sự kiện, tải **mô hình 3D** `.glb` (tối đa 15 MB) vào kho tệp; kho tệp qua giao diện `IFileStorage` thay được nhà cung cấp (bản lưu trên đĩa máy chủ, chỗ để sẵn cho dịch vụ đám mây); xóa ảnh giữ lại tệp (xóa mềm) | Xong phần backend | [docs/tai-tep-len.md](docs/tai-tep-len.md) |
| Khám phá di sản công khai (khách và học sinh, không đăng nhập): trang chủ, tìm kiếm và lọc có/không dấu, bản đồ, chi tiết, timeline; danh sách tỉnh mở cho khách; giới hạn 300 yêu cầu/phút/IP | Xong phần backend | [docs/kham-pha-di-san.md](docs/kham-pha-di-san.md) |
| Lớp, học sinh và tài khoản học sinh: giáo viên tạo/sửa/xóa lớp (khối 1-12, năm học, cấp học suy từ khối), thêm học sinh từng em hoặc nhập danh sách `.xlsx`/`.csv`/dán văn bản (xem trước, tối đa 60 dòng, lưu một giao dịch), mã học sinh `HS` + 6 ký tự, mật khẩu tạm và phiếu đăng nhập hiện một lần, đặt lại mật khẩu, vô hiệu/mở lại/xóa; học sinh đăng nhập bằng mã, buộc đổi mật khẩu lần đầu, xem lớp của em; quản trị trường chỉ xem số liệu tổng hợp | Xong phần backend | [docs/lop-hoc-sinh.md](docs/lop-hoc-sinh.md) |
| Bài học cho lớp theo cấp: giáo viên tạo bài cho **một lớp** từ 1-10 di sản đã xuất bản, nội dung theo khung của cấp 1/2/3 (câu chuyện từng bước có hình · bài đọc có mốc thời gian · bài phân tích có trích tư liệu gốc kèm nguồn), **AI soạn bản nháp đúng cấp** (qua giao diện Dịch vụ AI + **bản giả lập**, ghi `ai_generations`, tính hạn mức lượt AI của gói), giáo viên sửa và duyệt, xuất bản sinh **một mã bài học** (`BH` + 6 ký tự), lưu trữ, xóa, nhân bản sang lớp khác; học sinh đã đăng nhập gõ mã, bài phải thuộc lớp của em, nhận bài theo luồng của cấp; sai mã luôn cùng một thông báo, 10 lần sai/10 phút thì bị chặn | Xong phần backend | [docs/bai-hoc-theo-cap.md](docs/bai-hoc-theo-cap.md) |
| Đa ngôn ngữ, AI dịch và tóm tắt di sản, nghe thuyết minh: quản trị hệ thống bấm **"Dịch và tóm tắt bằng AI"** (qua giao diện Dịch vụ AI + **bản giả lập**, ghi `ai_generations`, **không tính hạn mức lượt AI của trường**): tóm tắt tiếng Việt (bản ngắn để nghe, bản chi tiết để đọc) và dịch mọi phần chữ (tên, mô tả, nội dung, giai đoạn, sự kiện, chú thích ảnh) sang Anh, Hàn, Trung giản thể, Nhật; **tiếng Việt (tóm tắt) và tiếng Anh chờ quản trị hệ thống duyệt** (từ chối ghi lý do vào nhật ký, sửa tại chỗ, dịch lại thay bản cũ), **Hàn, Trung, Nhật hiện ngay kèm nhãn "bản dịch máy"** và không có bước duyệt; sửa chữ tiếng Việt thì các bản đánh dấu "cần dịch lại" (vẫn hiện bản cũ); API công khai nhận `lang` hoặc `Accept-Language` (mã lạ thì tiếng Việt, học sinh đã đăng nhập luôn tiếng Việt), trả chi tiết, dòng thời gian, tìm kiếm (khớp cả bản dịch), bản đồ, tóm tắt theo ngôn ngữ, **thiếu bản dịch thì lùi về tiếng Việt kèm cờ**, không gọi AI khi xem; nghe thuyết minh là giọng đọc của thiết bị, backend chỉ cấp chữ | Xong phần backend | [docs/da-ngon-ngu.md](docs/da-ngon-ngu.md) |
| Mô hình 3D di sản: quản trị hệ thống khai báo mô hình ở tab Mô hình 3D của Biên tập di sản bằng **tải tệp `.glb`** (glTF 2.0 một tệp, tối đa 15 MB, kiểm theo nội dung, không tham chiếu tệp ngoài; kho tệp Local hoặc Cloudinary loại raw) hoặc **dán đường dẫn của tệp đã nằm trong kho** (trang ngoài kho bị từ chối, dung lượng do máy chủ đo); **ghi công bắt buộc** tác giả · giấy phép mở (CC0, CC BY, CC BY-SA, phạm vi công cộng, mô hình nhóm tự dựng; không nhận CC BY-NC, CC BY-ND) · đường dẫn nguồn; bật hiển thị, ẩn, thay tệp, sửa, gỡ, **mỗi di sản chỉ một mô hình hiển thị** (chỉ mục duy nhất có lọc của CSDL), mọi thay đổi ghi nhật ký thao tác; khách và học sinh thấy cờ `hasModel` ở chi tiết di sản và đọc mô hình qua API công khai **miễn phí, không cần đăng nhập, không qua quảng cáo** khi mô hình hiển thị và di sản đã xuất bản (không thì 404 `MODEL_UNAVAILABLE` để trình xem quay về bộ ảnh); xem trước bản nháp có cả mô hình; danh sách di sản quản trị có `hasModel` và bộ lọc | Xong phần backend (chưa ghi lịch sử xem 3D của khách) | [docs/mo-hinh-3d.md](docs/mo-hinh-3d.md) |
| Ngân hàng câu hỏi theo dạng, AI sinh câu hỏi: ngân hàng **dùng chung mọi trường** với **8 dạng câu** (một đáp án đúng · Đúng/Sai · chọn hình đúng · ghép cặp · sắp xếp · điền từ · nhiều mệnh đề Đúng/Sai · đọc tư liệu) và **cấp phù hợp** theo bảng dạng câu theo cấp; trình soạn có kiểm hợp lệ theo dạng (lỗi theo trường), xem trước như học sinh của từng cấp, **chấm thử từng phần**; **AI sinh câu đúng dạng và cấp của lớp** từ một bài học (qua giao diện Dịch vụ AI + **bản giả lập**, ghi `ai_generations`, tính hạn mức lượt AI của gói), câu AI vào ngân hàng **chờ duyệt** chỉ người tạo thấy; duyệt, loại, duyệt nhiều câu, sửa, xóa mềm, **nhân bản** (câu đã nằm trong quiz hoặc của người khác chỉ đọc); tìm và lọc theo dạng, cấp, độ khó, di sản, trạng thái, người tạo; quản trị hệ thống **ẩn** câu kém chất lượng (ghi nhật ký thao tác) | Xong phần backend | [docs/ngan-hang-cau-hoi.md](docs/ngan-hang-cau-hoi.md) |
| Gói và thanh toán VNPay: quản trị hệ thống tạo, sửa, **bật và tắt bán gói** (gói Miễn phí là gói giá 0 duy nhất, cố định tên và giá, không tắt được; không xóa gói; đổi giá không hồi tố); quản trị trường xem **gói hiện tại, gói gia hạn chờ, yêu cầu mua chưa trả, hạn mức giáo viên và lượt AI**, chọn gói (**mua, gia hạn, nâng cấp do hệ thống tự phân loại**; nâng cấp hiệu lực ngay, không hoàn phần dư; gia hạn bắt đầu sau khi gói hiện tại hết), hệ thống tạo trong một giao dịch **đăng ký chờ thanh toán + hóa đơn chưa trả (số `HD`+ngày+số thứ tự, hạn 24 giờ) + giao dịch chờ** và trả **địa chỉ VNPay sandbox có chữ ký HMAC-SHA512** (hạn 15 phút); **chỉ IPN hợp lệ của VNPay mới kích hoạt gói** (kiểm chữ ký, số tiền, **chống trùng và chống IPN đồng thời**, IPN thành công tới muộn vẫn ghi nhận), trang trả về chỉ chuyển trình duyệt và trang kết quả chỉ đọc; thanh toán lại, hủy yêu cầu hoặc hủy gói, kích hoạt gói Miễn phí; **tác vụ nền** chuyển giao dịch treo quá 30 phút thành thất bại, hủy hóa đơn quá hạn, chuyển gói quá ngày sang hết hạn; mọi thay đổi ghi nhật ký thao tác | Xong phần backend (chưa thử với VNPay sandbox thật: cần khóa cửa hàng và địa chỉ IPN công khai) | [docs/goi-thanh-toan.md](docs/goi-thanh-toan.md) |
| Bài kiểm tra của bài học, nhân bản bài học kèm bài kiểm tra: giáo viên tạo bài kiểm tra cho **một bài học của mình** (lớp và cấp là của bài học, **không có mã riêng**: học sinh vào qua mã bài học), chọn câu từ ngân hàng **chỉ câu đã duyệt hợp dạng và cấp của lớp** (hộp chọn câu mặc định lọc theo di sản của bài), điểm bước 0,25, thứ tự, tổng điểm tự tính, 1-50 câu; **thời gian làm theo cấp** (cấp 1 không giới hạn, cấp 2 tùy chọn, cấp 3 bắt buộc khi phát hành), giờ mở và đóng, xáo câu, cho xem đáp án sau khi nộp; **phát hành** (liệt kê đủ điều kiện còn thiếu, khóa danh sách câu, ghi nhật ký) rồi chỉ sửa được tên, mô tả, giờ đóng; **đóng** (không mở lại, tự nộp lượt đang dở) bằng tay hoặc **tự đóng khi quá giờ đóng** bằng tác vụ nền; **nhân bản** bài kiểm tra trong bài học; **nhân bản bài học kèm bài kiểm tra** sang lớp khác (nháp, bỏ giờ mở và đóng, bỏ câu đã ẩn và câu không hợp cấp lớp đích, chuẩn hóa thời gian làm); **lọc bài học theo độ khó** tính từ các câu trong bài kiểm tra (không lưu); không thêm migration | Xong phần backend | [docs/bai-kiem-tra.md](docs/bai-kiem-tra.md) |
| Học tập của học sinh theo cấp: trang học của em, vào quiz không cần nhập mã lần hai, **một lượt mỗi em mỗi quiz** (đồng hồ, lưu từng câu, vào tiếp khi mất kết nối, **hết giờ máy chủ tự nộp** sau 30 giây ân hạn, tác vụ nền mỗi phút), trả lời **cả 8 dạng câu** với thẻ, cột và mục xáo cố định theo lượt, **chấm từng phần** bằng bộ chấm của ngân hàng câu hỏi (ghi từng phần vào `attempt_answer_parts`), **kết quả theo cấp** (cấp 1 sao, lời khen, huy hiệu, không điểm · cấp 2 điểm, giải thích từng câu · cấp 3 thêm phân tích theo di sản và dạng câu), lịch sử bài làm, tiến độ, mức thành thạo theo di sản, 4 huy hiệu tính khi hiển thị; migration `ThemCauTraLoiTungPhan` | Xong phần backend | [docs/hoc-tap-theo-cap.md](docs/hoc-tap-theo-cap.md) |
| Thống kê và gợi ý: **"Gợi ý cho em"** của học sinh (năm phần: quiz nên làm tiếp, bài học nên ôn lại, di sản nên ôn, di sản liên quan, bài học mới nên học; content-based, cùng cách tính ở mọi cấp, bài mới không lộ mã bài học, dự phòng khi Dịch vụ AI lỗi), giáo viên xem **kết quả bài kiểm tra của lớp** (tổng quan và phổ điểm, từng câu kèm lỗi hay gặp, từng học sinh, theo chủ đề, chi tiết bài làm), **tiến độ và mức thành thạo của lớp** (theo di sản, tỉnh, loại, dạng câu, bảng nhiệt), **hồ sơ học tập** của từng học sinh và **gợi ý ôn tập cho lớp**; tính khi hiển thị từ lượt làm bài, không bảng mới, không migration | Xong phần backend | [docs/thong-ke-goi-y.md](docs/thong-ke-goi-y.md) |
| Quản trị và tổng quan: **tổng quan trường** cho quản trị trường (giáo viên so với hạn mức, lớp và học sinh theo cấp, học sinh chưa đăng nhập, bài học, bài kiểm tra đã phát hành, lượt nộp, điểm trung bình theo lớp và theo cấp, lượt AI so với hạn mức, gói và lời nhắc sắp hết hạn; chỉ số tổng, không lộ tên học sinh hay giáo viên), **tổng quan hệ thống** cho quản trị hệ thống (trường có và không có gói, giáo viên, học sinh, nội dung, ngân hàng câu hỏi, lượt AI, số trường theo từng gói, **biểu đồ theo thời gian** theo ngày hoặc tháng giờ Việt Nam, **doanh thu từ giao dịch VNPay thành công** theo ngày thanh toán (tổng, tháng này, hôm nay, theo kỳ trên biểu đồ) và **danh sách giao dịch** lọc theo trạng thái, trường, khoảng ngày; doanh thu quảng cáo không tính), **kiểm duyệt ngân hàng câu hỏi** (danh sách kèm số lượt trả lời, tỉ lệ đúng và dấu hiệu cần xem lại, ẩn câu kèm lý do, **hiện lại câu**, nhật ký thao tác); tính khi hiển thị, không bảng mới, không migration | Xong phần backend | [docs/quan-tri-tong-quan.md](docs/quan-tri-tong-quan.md) |
| Khách tham quan trên web và app (không tài khoản): **mã thiết bị ẩn danh** (UUID phiên bản 4 do app hoặc trình duyệt sinh, gửi ở tiêu đề `X-Device-Key`; máy chủ chỉ giữ mã, nền tảng, ngôn ngữ, cờ "Gần bạn", không ghi IP hay vị trí), đăng ký và cài đặt thiết bị, **lịch sử** tìm, xem, đọc tóm tắt, nghe, xem 3D (tự ghi khi gọi API công khai kèm mã, lượt lặp trong 10 phút ghi một lần, giữ 90 ngày và tối đa 500 lượt mỗi thiết bị, khách xóa từng mục hoặc hết là xóa thật, học sinh đăng nhập không bị ghi), **"Gợi ý cho bạn"** (tối đa 6 di sản giống di sản khách đã xem, đọc tóm tắt, nghe, xem 3D trong 20 lượt gần nhất, cùng bộ tính với gợi ý của học sinh, không gọi mô hình ngôn ngữ, bỏ di sản đã xem từ 3 lần, chưa có lịch sử thì di sản nổi bật, Dịch vụ AI lỗi thì máy chủ tự chọn cùng loại hoặc cùng vùng), **"Gần bạn"** (tối đa 6 di sản gần nhất, tọa độ gửi trong thân yêu cầu, làm tròn 3 chữ số, không lưu, không vào nhật ký, ngoài khung Việt Nam nới 1 độ thì không hiện), tác vụ nền dọn lịch sử quá 90 ngày; migration `ThemKhachThamQuanVaLichSu` | Xong | [docs/khach-tham-quan.md](docs/khach-tham-quan.md) |
| Quảng cáo Google có thưởng cho khách (cổng quảng cáo): khách xem hết một quảng cáo **thật của Google** (app: **AdMob**; web: **Ad Manager**) để mở **tóm tắt và nghe thuyết minh của một di sản trong 24 giờ trên đúng thiết bị đó** (mọi thứ khác miễn phí); **chỉ mở khóa khi Google xác nhận đã trao thưởng**: app qua **xác minh phía máy chủ của AdMob** (`GET /api/v1/ads/admob/ssv`, kiểm **chữ ký ECDSA bằng khóa công khai của Google** tải từ gstatic.com và đệm 24 giờ, đơn vị quảng cáo, thiết bị, di sản, thời điểm lệch tối đa 1 giờ, mã giao dịch không trùng), web qua **mã giao dịch một lần có chữ ký HMAC cấp lúc bắt đầu xem** (hết hạn sau 10 phút) và báo "đã cấp thưởng" từ trình duyệt với **giới hạn 10 lượt mỗi giờ mỗi thiết bị và 60 lượt mỗi giờ mỗi IP** (đếm trong bộ nhớ, IP không lưu); tóm tắt và ghi lượt nghe trả **403 `AD_UNLOCK_REQUIRED`** khi chưa mở khóa; học sinh đã đăng nhập, cổng tắt hoặc thiết bị khai báo không có quảng cáo (khi môi trường cho phép) mở thẳng; lời gọi gửi lại không ghi thêm, lời gọi sai bị từ chối và ghi nhật ký thao tác `AD_REWARD_REJECTED` (không IP, không chữ ký); mã đơn vị quảng cáo trong cấu hình (đơn vị thử của Google khi phát triển); quản trị hệ thống xem **thống kê lượt mở khóa** (theo ngày, nền tảng, mạng, di sản, lượt bị từ chối theo lý do) và khách; tác vụ nền xóa lượt quá 12 tháng; migration `ThemLuotMoKhoaQuangCao` | Xong phần backend (chưa có giao diện của cổng) | [docs/quang-cao-google.md](docs/quang-cao-google.md) |

## Cấu trúc

```
src/DSVHVN.Domain          Thực thể 27 bảng của CSDL v3 và mười một bảng của v4 (lesson_sections, năm bảng đa ngôn ngữ, heritage_models, attempt_answer_parts, guest_devices, guest_activities, ad_unlocks), enum, quy tắc nghiệp vụ thuần (tài khoản, di sản, lớp và mã học sinh, khung bài học theo cấp, mã bài học, bảng dạng câu hỏi theo cấp, quy tắc bài kiểm tra và độ khó của bài học)
src/DSVHVN.Application     Dịch vụ nghiệp vụ, kiểm gói và hạn mức, validator, danh mục thông điệp tiếng Việt, nhật ký thao tác, tổng quan trường và hệ thống (Overview)
src/DSVHVN.Infrastructure  EF Core (DbContext, cấu hình theo nhóm bảng, migration, seed, nạp dữ liệu di sản), JWT,
                           băm mật khẩu PBKDF2, gửi thư (ghi log hoặc SMTP qua MailKit), đọc và dựng tệp danh sách học sinh (ClosedXML),
                           gọi Dịch vụ AI (bản giả lập hoặc HTTP tới dịch vụ nội bộ; soạn bài, sinh câu hỏi, dịch và tóm tắt di sản, xếp hạng di sản liên quan cho gợi ý),
                           bộ đếm nhập sai mã bài học, kho tệp tải lên (lưu trên đĩa hoặc Cloudinary, xử lý ảnh bằng SkiaSharp),
                           tác vụ nền tự đóng bài kiểm tra quá giờ đóng
src/DSVHVN.Api             Controllers, middleware, phân quyền 4 vai trò (ADMIN, ORG_ADMIN, TEACHER, STUDENT)
tests/DSVHVN.Tests         xUnit: unit + integration (WebApplicationFactory trên SQLite trong bộ nhớ và trên database tạm
                           của SQL Server LocalDB, tự xóa khi xong)
```

## Chạy nhanh

Yêu cầu: .NET SDK 9.0.317, SQL Server LocalDB.

```bash
dotnet tool restore                                   # dotnet-ef 9.0.20 ghim trong .config/dotnet-tools.json

# Mật khẩu 4 tài khoản ADMIN seed (admin.nho, admin.lam, admin.trieu, admin.huy): chỉ đặt trong User Secrets, không commit
dotnet user-secrets set "Seed:AdminPassword" "<mật khẩu 8-64 ký tự, có chữ hoa, chữ thường, chữ số>" --project src/DSVHVN.Api

# Tạo CSDL sep490_dsvhvn rồi nạp dữ liệu khởi tạo (gói Miễn phí, ADMIN, 34 tỉnh/thành, di sản) và thoát
dotnet tool run dotnet-ef database update --project src/DSVHVN.Infrastructure --startup-project src/DSVHVN.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/DSVHVN.Api --no-launch-profile -- --chi-khoi-tao-csdl

dotnet run --project src/DSVHVN.Api --launch-profile http > api.log 2>&1 &   # http://localhost:5080/swagger

dotnet test                                           # có LocalDB thì chạy thêm test trên database tạm
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-nen-tang.sh
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-lop-hoc-sinh.sh   # nên chạy trên database tạm, xem docs/lop-hoc-sinh.md mục 9
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-bai-hoc-theo-cap.sh # nên chạy trên database tạm, xem docs/bai-hoc-theo-cap.md mục 8
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-ngan-hang-cau-hoi.sh # nên chạy trên database tạm, xem docs/ngan-hang-cau-hoi.md mục 9
ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-da-ngon-ngu.sh   # nên chạy trên database tạm, cách chạy ở đầu tệp và docs/da-ngon-ngu.md mục 12
ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-mo-hinh-3d.sh    # nên chạy trên database tạm, cách chạy ở đầu tệp và docs/mo-hinh-3d.md mục 6
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-goi-thanh-toan.sh   # nên chạy trên database tạm, cách chạy ở đầu tệp và docs/goi-thanh-toan.md mục 8
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-bai-kiem-tra.sh # nên chạy trên database tạm, xem docs/bai-kiem-tra.md mục 7
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-hoc-tap-theo-cap.sh # nên chạy trên database tạm, xem docs/hoc-tap-theo-cap.md mục 7
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-thong-ke-goi-y.sh # nên chạy trên database tạm, xem docs/thong-ke-goi-y.md mục 7
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-quan-tri-tong-quan.sh # nên chạy trên database tạm, xem docs/quan-tri-tong-quan.md mục 7
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-khach-tham-quan.sh # nên chạy trên database tạm, cách chạy ở đầu tệp và docs/khach-tham-quan.md mục 7
```

CSDL dev: `sep490_dsvhvn` trên `(localdb)\MSSQLLocalDB`, collation `Vietnamese_CI_AI` (không dùng các CSDL cũ `dsvhvn_dev`,
`sep490_dsvhvn_dev`, `sep490_dsvhvn_v2_dev`, `sep490_dsvhvn_v3_dev`). Dùng CSDL khác thì đặt biến môi trường
`ConnectionStrings__Default` (cả cho `dotnet-ef` lẫn Api). Dữ liệu tỉnh/thành và di sản nằm ngay trong repo, thư mục `seed-data/`
(`Seed:HeritageData:*` trong `appsettings.Development.json`), nên clone về là chạy backend ở môi trường Development sẽ tự tạo CSDL,
áp migration và nạp 34 tỉnh, 20 di sản, gói dịch vụ, 4 quản trị hệ thống (cần `Seed:AdminPassword` hợp lệ, xem trên).
Chi tiết endpoint, cấu hình và quyết định hiện thực ở [docs/nen-tang-to-chuc.md](docs/nen-tang-to-chuc.md).

### Tạo CSDL thật kèm dữ liệu demo

```powershell
pwsh -File scripts/tao-csdl-demo.ps1                     # CSDL sep490_dsvhvn, cổng 5180; hỏi mật khẩu đặt cho 4 quản trị hệ thống
pwsh -File scripts/tao-csdl-demo.ps1 -Database dsvhvn_tam_thu -Port 5190
```

Script dựng backend, tạo CSDL (đủ migration), nạp dữ liệu demo (một trường, giáo viên, ba lớp khối 3, 8, 11, bài học, bài kiểm tra,
học sinh đã làm bài, di sản đã dịch, mô hình 3D mẫu) qua API rồi tắt backend. Email luôn ở chế độ Log nên không gửi thư thật. CSDL đã có
dữ liệu demo thì script báo và thoát. Cách dùng, nội dung dữ liệu và tài khoản demo: [scripts/du-lieu-demo/README.md](scripts/du-lieu-demo/README.md).

## Dịch vụ AI soạn bài học, sinh câu hỏi, dịch và tóm tắt di sản

Dịch vụ AI thật (FastAPI) **chưa có**. Backend gọi AI qua giao diện theo hợp đồng của nhóm và có **bản giả lập** (sinh nội dung mẫu đúng cấp từ dữ liệu di sản, không gọi nhà cung cấp mô hình nào,
không dùng khóa API nào). Chọn bản bằng cấu hình `Ai:Mode`: `Simulated` (mặc định) hoặc `Remote` (gọi dịch vụ nội bộ ở `Ai:BaseUrl`, khóa nội bộ `Ai:InternalKey` đặt bằng biến môi trường
`Ai__InternalKey`, không ghi vào appsettings). Chờ 45 giây mỗi lần gọi, thử lại tối đa 2 lần (`Ai:AttemptTimeoutSeconds`, `Ai:MaxRetries`). Chi tiết hợp đồng, quy trình và hạn mức lượt AI:
[docs/bai-hoc-theo-cap.md](docs/bai-hoc-theo-cap.md) mục 5. Sinh câu hỏi theo dạng và cấp dùng chung mục cấu hình `Ai` (cùng chế độ, địa chỉ, khóa, thời gian chờ) qua giao diện `IQuestionAiClient`
(`POST /v1/generate-questions`) và có bản giả lập riêng: [docs/ngan-hang-cau-hoi.md](docs/ngan-hang-cau-hoi.md) mục 5. Dịch và tóm tắt di sản cũng dùng mục `Ai` qua `IHeritageAiClient`
(`POST /v1/summarize-heritage`, `POST /v1/translate-heritage`), chờ 90 giây mỗi lần (`Ai:HeritageAttemptTimeoutSeconds`), thử lại tối đa 2 lần, và có bản giả lập riêng: [docs/da-ngon-ngu.md](docs/da-ngon-ngu.md) mục 9 và 10.

## Kho tệp tải lên (ảnh, mô hình 3D)

Ảnh di sản, ảnh sự kiện và mô hình 3D (`.glb`; khai báo vào di sản ở [docs/mo-hinh-3d.md](docs/mo-hinh-3d.md)) tải lên được lưu qua giao diện `IFileStorage`, chọn nhà cung cấp bằng `Storage:Provider`: `Local` (mặc định, lưu trên đĩa máy chủ, cho phát triển và kiểm thử) hoặc `Cloudinary`
(khi triển khai: ảnh và mô hình nằm trên Cloudinary, địa chỉ là URL HTTPS; thiếu khóa thì Api dừng lúc khởi động và báo tên khóa còn thiếu). Bản `Local` lưu trong thư mục `Storage:Local:RootPath` (mặc định `storage`, tính từ thư mục chạy
của Api, đã nằm trong `.gitignore`) và phục vụ tệp qua đường dẫn `/files/...` không cần đăng nhập, chỉ cho đuôi `jpg`, `png`, `webp`, `glb`. Ngoài máy phát triển nên đặt thư mục **ngoài mã nguồn** và sao lưu nó:

```bash
export Storage__Local__RootPath=/var/lib/dsvhvn/tep         # Windows: set Storage__Local__RootPath=D:\dsvhvn-tep
export Storage__PublicBaseUrl=https://api.dsvhvn.vn         # tùy chọn: web học tập và app ở địa chỉ khác API thì đặt địa chỉ đủ; để trống thì đường dẫn tệp là /files/...
```

Dùng Cloudinary: tên đám mây, khóa API và bí mật API **chỉ** ở User Secrets hoặc biến môi trường, không vào tệp cấu hình hay mã:

```bash
dotnet user-secrets set "Storage:Cloudinary:CloudName" "<cloud name>" --project src/DSVHVN.Api
dotnet user-secrets set "Storage:Cloudinary:ApiKey"    "<api key>"    --project src/DSVHVN.Api
dotnet user-secrets set "Storage:Cloudinary:ApiSecret" "<api secret>" --project src/DSVHVN.Api
export Storage__Provider=Cloudinary                          # Windows: set Storage__Provider=Cloudinary (hoặc đặt trong biến môi trường của máy chủ)
```

Quy tắc ảnh, mô hình, endpoint, cách tạo tài khoản Cloudinary và cách kiểm thử: [docs/tai-tep-len.md](docs/tai-tep-len.md).

## Xem trước bản nháp di sản và xem như học sinh

- **Quản trị hệ thống**: `POST /api/v1/admin/heritages/{id}/preview` cấp liên kết xem trước (hạn `Preview:CodeMinutes`, mặc định 30 phút) cho di sản chưa xuất bản; trang xem trước của web học tập đọc bản nháp qua
  `GET /api/v1/preview/heritages/{mã}` (kèm token quản trị hệ thống). Di sản đã xuất bản dùng trang công khai bình thường. Chi tiết: [docs/kho-di-san.md](docs/kho-di-san.md) mục 9.
- **Giáo viên**: `GET /api/v1/teacher/lessons/{lessonId}/preview` ("Xem như học sinh") trả bài đúng bố cục của cấp lớp, kể cả bài Nháp, không ghi gì. Chi tiết: [docs/bai-hoc-theo-cap.md](docs/bai-hoc-theo-cap.md) mục 4.8.

## Thanh toán VNPay sandbox (gói dịch vụ của trường)

Trường mua, gia hạn, nâng cấp gói qua **VNPay sandbox** (cổng duy nhất, không tiền thật). Mã cửa hàng và khóa bí mật của VNPay **chỉ** ở User Secrets hoặc biến môi trường, không vào tệp cấu hình hay mã; chưa đặt thì Api vẫn chạy nhưng
tạo thanh toán trả 503 và IPN bị từ chối:

```bash
dotnet user-secrets set "VnPay:TmnCode"    "<mã cửa hàng sandbox>" --project src/DSVHVN.Api
dotnet user-secrets set "VnPay:HashSecret" "<khóa bí mật sandbox>" --project src/DSVHVN.Api
# VnPay:ReturnUrl (mặc định http://localhost:5080/api/v1/payments/vnpay/return) là địa chỉ trình duyệt của người trả mở được;
# địa chỉ IPN /api/v1/payments/vnpay/ipn phải công khai và khai trong trang quản lý cửa hàng sandbox của VNPay (localhost không nhận được IPN).
```

Hoặc chạy `pwsh -File scripts/dat-khoa-vnpay.ps1 -PublicBaseUrl https://<tên miền công khai>`: script hỏi hai giá trị (chuỗi bí mật nhập ẩn, không vào lịch sử lệnh), đặt vào User Secrets
và in địa chỉ IPN cần khai. Trên máy cá nhân, địa chỉ công khai lấy bằng đường hầm (vd `ngrok http 5080`).

Chỉ IPN hợp lệ mới kích hoạt gói. Tác vụ nền gói và thanh toán chạy cùng Api (`Billing:Maintenance:Enabled`, `IntervalSeconds`). Gói trả phí mẫu "Cơ bản", "Nâng cao" chỉ nạp ở môi trường Development (`Seed:Plans`); UAT và Release 1.0 do quản trị hệ thống tạo ở màn hình
Gói dịch vụ. Quy tắc, endpoint, mã phản hồi IPN, kiểm thử: [docs/goi-thanh-toan.md](docs/goi-thanh-toan.md).

## Quảng cáo Google có thưởng (cổng quảng cáo của khách)

Khách xem hết một quảng cáo của Google để mở tóm tắt và nghe thuyết minh của một di sản trong 24 giờ. Mã đơn vị quảng cáo nằm trong mục `Ads` của cấu hình; `appsettings.Development.json` đã điền **đơn vị quảng cáo thử của Google**
(dùng mã thật khi đang phát triển là vi phạm chính sách của Google). Cổng **tắt mặc định** (`Ads:GateEnabled`); mã thật chỉ đặt bằng User Secrets hoặc biến môi trường, không commit:

```bash
dotnet user-secrets set "Ads:GateEnabled" "true" --project src/DSVHVN.Api
# Địa chỉ cho xác minh phía máy chủ khai ở bảng điều khiển AdMob (HTTPS công khai): https://<máy chủ>/api/v1/ads/admob/ssv
# Ads:AllowNoAdsDeclaration chỉ bật ở môi trường phát triển và demo (bản demo iOS bằng Expo Go không có quảng cáo).
```

Quy tắc, endpoint, cấu hình, kiểm thử: [docs/quang-cao-google.md](docs/quang-cao-google.md).

## Thư đặt mật khẩu

Mặc định `Email:Mode = Log`: thư **không gửi đi**, nội dung (kể cả liên kết có token) ghi ra log để dev và smoke test không cần
SMTP. Bật gửi thật qua Gmail (cần bật Xác minh 2 bước và tạo Mật khẩu ứng dụng 16 ký tự, nhập liền không khoảng trắng):

```bash
dotnet user-secrets set "Email:Mode" "Smtp" --project src/DSVHVN.Api
dotnet user-secrets set "Email:Smtp:Host" "smtp.gmail.com" --project src/DSVHVN.Api
dotnet user-secrets set "Email:Smtp:Port" "587" --project src/DSVHVN.Api
dotnet user-secrets set "Email:Smtp:Username" "<tai-khoan>@gmail.com" --project src/DSVHVN.Api
dotnet user-secrets set "Email:Smtp:Password" "<app-password>" --project src/DSVHVN.Api
dotnet user-secrets set "Email:Smtp:FromAddress" "<tai-khoan>@gmail.com" --project src/DSVHVN.Api
```

Mật khẩu SMTP không ghi vào appsettings; ngoài Development đặt bằng biến môi trường `Email__Smtp__Password`. Thiếu cấu hình bắt
buộc thì API dừng lúc khởi động và báo mục còn thiếu. Email `*.local` của 4 tài khoản quản trị mẫu **không nhận được thư**; thử
bằng trường hoặc giáo viên tạo với email thật. Chi tiết, cách tắt và các lưu ý: [docs/gui-thu.md](docs/gui-thu.md).
