# Luồng Khách tham quan trên web và app (Backend)

Phần backend cho **khách tham quan**: người dùng phần công khai của web học tập hoặc chế độ Khách tham quan của app, **không có tài khoản**. Khách được nhận diện bằng một **mã thiết bị ẩn danh** (UUID phiên bản 4)
do chính app hoặc trình duyệt sinh và gửi kèm yêu cầu; máy chủ nhờ đó nhớ cài đặt, ghi **lịch sử** (tìm, xem, đọc tóm tắt, nghe, xem 3D), tính **"Gợi ý cho bạn"** theo lịch sử và **"Gần bạn"** theo vị trí
khách gửi lên (vị trí không lưu). Học sinh đã đăng nhập không bị ghi lịch sử và không dùng các chức năng riêng của khách. Cổng quảng cáo mở khóa tóm tắt, thống kê khách cho quản trị và các màn hình
thuộc luồng khác (xem mục 9).

| Mục | Giá trị |
|---|---|
| Chức năng | Chọn chế độ sử dụng app (phần máy chủ: cấp và nhận mã thiết bị) · Nhận gợi ý di sản cho khách ("Gợi ý cho bạn", "Gần bạn") · Quản lý lịch sử và cài đặt của khách |
| Màn hình phục vụ | Web học tập và app, chế độ Khách tham quan: trang chủ khách (Gợi ý cho bạn, Gần bạn), Lịch sử và cài đặt, bản đồ, chi tiết di sản (cuối trang có Gợi ý cho bạn) |
| Bảng (CSDL v4, migration `ThemKhachThamQuanVaLichSu`) | `guest_devices`, `guest_activities` (mới); đọc `heritages`, `provinces`, `timelines`, `heritage_translations`, `heritage_summaries`, `heritage_models` |
| Lớp mới | `Domain/Guests` (`GuestDevice`, `GuestActivity`), `Domain/Rules/GuestRules`, enum `ClientPlatform`, `GuestActivityType`; `Application/Guests` (`GuestService`, `GuestRecommendationService`, DTO, `GuestMessages`); `Api/Controllers/GuestController`, `Api/Common/GuestHeaders`; `Infrastructure/Background/GuestHistoryCleanupService` |
| Nguồn | `doc/plan/spec` (use case Chọn chế độ sử dụng app, Nhận gợi ý di sản cho khách, Quản lý lịch sử và cài đặt của khách; màn hình của chế độ Khách tham quan; các quy tắc App hai chế độ, Mã thiết bị của khách, Lịch sử của khách, Vị trí của khách và "Gần bạn", Gợi ý di sản cho khách; thông báo "Gần bạn" không dùng được, chưa có lịch sử) · `doc/database/dsvhvn-v4.dbml` |

## 1. Quy tắc

| Quy tắc | Nội dung |
|---|---|
| Mã thiết bị | **UUID phiên bản 4** dạng chuẩn `xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx` (`y` là 8, 9, a hoặc b), do app hoặc trình duyệt sinh bằng bộ sinh số ngẫu nhiên an toàn ở lần mở đầu và lưu cục bộ. Gửi ở tiêu đề **`X-Device-Key`**. Máy chủ đưa về chữ thường; sai định dạng (ngoặc, thiếu gạch, phiên bản khác, quá dài) thì **400 `INVALID`**, thiếu ở endpoint bắt buộc thì **400 `REQUIRED`**. Mã không phải bí mật xác thực: không có API liệt kê hay tìm mã, lịch sử chỉ trả cho yêu cầu mang đúng mã |
| Không lấy gì ngoài mã | Không mã phần cứng, số điện thoại, MAC, mã quảng cáo, email. Bản ghi thiết bị chỉ có mã, nền tảng, ngôn ngữ dùng gần nhất, cờ "Gần bạn", lần đầu và lần cuối thấy. **Không ghi địa chỉ IP**, không ghi vị trí. Đăng nhập của học sinh không liên kết với mã thiết bị |
| Tạo bản ghi thiết bị | Ở **yêu cầu đầu tiên cần lưu** (đăng ký, đổi cài đặt, ghi một lượt); yêu cầu chỉ đọc với mã chưa từng thấy (xem cài đặt, lịch sử, gợi ý) **không tạo gì**. Hai yêu cầu đầu tiên cùng lúc của một mã chỉ tạo một thiết bị (mã không trùng, yêu cầu thua đọc lại dòng vừa tạo) |
| Ghi lịch sử | Gồm tìm (từ khóa 2-100 ký tự), xem chi tiết, đọc tóm tắt, nghe, xem 3D. **Chỉ ghi với di sản đã xuất bản.** **Không ghi** khi yêu cầu mang phiên đăng nhập của học sinh. Ghi lịch sử **không bao giờ làm hỏng việc chính** (lỗi ghi chỉ vào nhật ký, không kèm mã thiết bị). Không ghi vị trí, không ghi IP |
| Lượt lặp | Lượt lặp trong **10 phút** (cùng thiết bị, cùng loại, cùng di sản hoặc cùng từ khóa) chỉ ghi một lần; lần lặp vẫn cập nhật lần thấy cuối và ngôn ngữ của thiết bị. Vì vậy `language_code` luôn là **ngôn ngữ dùng gần nhất** (lượt ghi kế tiếp ở ngôn ngữ khác sẽ đổi nó, kể cả sau `PUT /device`); lựa chọn ngôn ngữ thật của khách nằm trên thiết bị |
| Thời hạn lưu | **90 ngày**: lượt cũ hơn không hiện ở lịch sử, không dùng cho gợi ý, và tác vụ nền hằng ngày **xóa thật**. Mỗi thiết bị giữ tối đa **500 lượt** gần nhất (vượt thì xóa thật lượt cũ nhất ngay khi ghi). Hai con số cố định trong mã (`GuestRules`), không cấu hình |
| Xóa lịch sử | Khách xóa **từng mục** hoặc **hết** bất cứ lúc nào; xóa thật trên máy chủ, không xóa mềm. Xóa hết không xóa bản ghi thiết bị, không xóa cài đặt, không đụng lượt mở khóa quảng cáo (luồng khác). Gợi ý quay về mặc định |
| Lịch sử hiện ra | Mới nhất trước; mục của di sản đã bị ẩn, lưu trữ hoặc xóa **không hiện** (khách không mở được). Thẻ di sản theo ngôn ngữ yêu cầu (thiếu bản dịch thì tiếng Việt kèm cờ `isFallback`) |
| Gợi ý cho bạn | Tối đa **6** di sản đã xuất bản. Sở thích = di sản trong **20 lượt gần nhất** (trong 90 ngày) khách đã xem chi tiết, đọc tóm tắt, nghe hoặc xem 3D; **từ khóa tìm không thành sở thích**. Hệ số: xem chi tiết **1**, đọc tóm tắt, nghe, xem 3D **2** (cộng dồn theo di sản). Ứng viên xếp theo độ tương đồng (cùng loại, cùng tỉnh hoặc vùng, khoảng năm của timeline giao nhau, cùng cấp xếp hạng, chữ giống nhau) bằng **cùng bộ tính** với "di sản liên quan" của học sinh, qua Dịch vụ AI, có phương án dự phòng; **không gọi mô hình ngôn ngữ**, không ghi `ai_generations`, không tính lượt AI, không lưu kết quả. **Bỏ** di sản đã xem chi tiết **từ 3 lần** trong 90 ngày và chính các di sản đang làm sở thích |
| Gợi ý khi chưa có gì | Chưa có lịch sử (hoặc vừa xóa hết, hoặc chỉ có từ khóa tìm, hoặc lịch sử quá 90 ngày) thì trả **di sản nổi bật** như trang chủ (UNESCO hoặc quốc gia đặc biệt, mới cập nhật trước, tối đa 6), `isPersonalized` = false kèm lời mời "Hãy xem thử vài di sản, mục "Gợi ý cho bạn" sẽ xuất hiện ở đây." Có lịch sử mà không tìm được di sản giống thì cũng trả di sản nổi bật nhưng **không** kèm lời mời |
| Dịch vụ AI lỗi | Dịch vụ AI lỗi hoặc quá 2 giây: máy chủ tự chọn di sản **cùng loại hoặc cùng vùng** với di sản xem gần nhất (cùng loại 2 điểm, cùng vùng 1 điểm, 0 điểm thì bỏ; bằng nhau thì theo id), `usedFallback` = true |
| Dữ liệu gửi Dịch vụ AI | Chỉ id, thuộc tính công khai của di sản (loại, tỉnh, vùng, cấp xếp hạng, khoảng năm, tên và mô tả ngắn) và trọng số. **Không** gửi mã thiết bị, ngôn ngữ, nền tảng, vị trí, từ khóa tìm; `studentId`, `level`, `targetDifficulty` để trống |
| Gần bạn | Tối đa **6** di sản đã xuất bản **có tọa độ**, gần nhất trước (bằng nhau thì theo id), mỗi di sản kèm khoảng cách theo **công thức khoảng cách trên mặt cầu**: dưới 1000 m hiện mét ("350 m"), từ 1000 m hiện km làm tròn 1 chữ số ("1,2 km" trong tiếng Việt, "1.2 km" ở các ngôn ngữ khác) |
| Vị trí | Thiết bị gửi tọa độ làm tròn 3 chữ số thập phân; **máy chủ làm tròn lại** trước khi tính. Chỉ dùng để tính trong bộ nhớ: **không lưu** (không bảng, không cột), **không ghi vào lịch sử**, không gửi Dịch vụ AI hay bên thứ ba, **không vào nhật ký yêu cầu** (tọa độ gửi trong thân yêu cầu `POST`, không để trên đường dẫn; đối tượng yêu cầu che tọa độ khi bị đổi thành chữ). Ngoài khung tọa độ Việt Nam (vĩ độ 8,0-23,5, kinh độ 102,0-117,5, **nới thêm 1 độ mỗi phía**) thì danh sách rỗng, `unavailableReason` = `OUTSIDE_VIETNAM` kèm lời báo. Cờ `locationAllowed` chỉ nhớ lựa chọn bật "Gần bạn" của khách (và để thống kê tỉ lệ bật), **không quyết định** việc tính khoảng cách |
| Học sinh | Phiên đăng nhập của học sinh: không bị ghi lịch sử (kể cả xem trang di sản công khai), ngôn ngữ luôn tiếng Việt, và các chức năng riêng của khách (đăng ký thiết bị, cài đặt, lịch sử, gợi ý, gần bạn) trả **403 `GUEST_ONLY`**. Riêng ghi tay một lượt (`POST activities`) trả 200 với `recorded` = false, lý do `STUDENT_SESSION`, để app học sinh gọi nhầm cũng không hỏng |
| Không đệm | Mọi phản hồi của `/api/v1/public/guest` mang `Cache-Control: no-store` (dữ liệu riêng của thiết bị) |
| Tần suất | Như API công khai: 300 yêu cầu mỗi phút mỗi IP (`RateLimiting:PublicRequestsPerMinute`), vượt thì 429 |

## 2. Dữ liệu (migration `ThemKhachThamQuanVaLichSu`)

`guest_devices` (7 cột):

| Cột | Ghi chú |
|---|---|
| `id` | khóa chính tự tăng |
| `device_key` | `nvarchar(64)`, **không trùng** (`UX_guest_devices_device_key`, UNIQUE thường); UUID chữ thường 36 ký tự |
| `platform` | `ANDROID`, `IOS`, `WEB` (CHECK `CK_guest_devices_platform`) |
| `language_code` | `vi`, `en`, `ko`, `zh`, `ja`; DEFAULT `'vi'` (CHECK `CK_guest_devices_language_code`); chỉ để thống kê, máy chủ không dùng để chọn ngôn ngữ hiển thị |
| `location_allowed` | `bit`, DEFAULT 0; cờ nhớ lựa chọn "Gần bạn", không chứa tọa độ |
| `created_at`, `last_seen_at` | `datetime2(3)` UTC; lần đầu và lần cuối thiết bị gọi API có mang mã |

`guest_activities` (7 cột):

| Cột | Ghi chú |
|---|---|
| `id` | khóa chính tự tăng |
| `guest_device_id` | khóa ngoại tới `guest_devices`, **Restrict** |
| `activity_type` | `SEARCH`, `VIEW`, `READ_SUMMARY`, `LISTEN`, `VIEW_3D` (CHECK `CK_guest_activities_activity_type`) |
| `heritage_id` | khóa ngoại tới `heritages`, **Restrict**, được NULL (rỗng với `SEARCH`) |
| `search_query` | `nvarchar(255)`, bắt buộc với `SEARCH`; mã chỉ nhận 2-100 ký tự |
| `language_code` | ngôn ngữ đang dùng lúc thực hiện (CHECK `CK_guest_activities_language_code`) |
| `created_at` | `datetime2(3)` UTC |

CHECK `CK_guest_activities_target`: tìm kiếm cần từ khóa, các loại còn lại cần di sản. Chỉ mục `IX_guest_activities_device_time` (`guest_device_id`, `created_at`) cho màn Lịch sử, gợi ý và việc xóa quá hạn;
EF Core thêm chỉ mục cho khóa ngoại `heritage_id`. Hai bảng **không có** cột nào chứa tọa độ, địa chỉ IP hay thông tin cá nhân (có test khẳng định danh sách cột). Dữ liệu **xóa hẳn**, không có `deleted_at`.
Migration chỉ **thêm** hai bảng nên chạy được trên CSDL có dữ liệu. Số liệu sau migration trên SQL Server: **37 bảng**, 345 cột, 58 khóa ngoại, 45 CHECK, 16 UNIQUE thường (thêm
`UX_guest_devices_device_key`), 31 cột enum `nvarchar(20)` (xem [co-so-du-lieu.md](co-so-du-lieu.md) mục 13).

## 3. API (tiền tố `/api/v1/public/guest`, không cần đăng nhập)

Tiêu đề: **`X-Device-Key`** (mã thiết bị) và **`X-Client-Platform`** (`ANDROID`, `IOS` hoặc `WEB`, không phân biệt hoa thường; mặc định `WEB`; sai giá trị thì 400). Ngôn ngữ như API công khai: tham số `lang`
(`vi`, `en`, `ko`, `zh-Hans`, `ja`) hoặc `Accept-Language`, mặc định tiếng Việt.

| Method | Đường dẫn | Nội dung | Thành công | Lỗi |
|---|---|---|---|---|
| POST | `/devices` | Đăng ký thiết bị. Thân tùy chọn `{ platform?, language?, locationAllowed? }`. Có `X-Device-Key` thì tạo nếu chưa có, có rồi thì cập nhật cài đặt; **không có tiêu đề** thì máy chủ **cấp một mã mới** (cho client không có bộ sinh an toàn) và trả trong `deviceKey` | 201 (vừa tạo) hoặc 200 `GuestDeviceDto` | 400 · 403 |
| GET | `/device` | Cài đặt của thiết bị (bắt buộc mã). Mã chưa từng thấy: `isRegistered` = false và giá trị mặc định, **không tạo bản ghi** | 200 `GuestDeviceDto` | 400 · 403 |
| PUT | `/device` | Đổi cài đặt: `language`, `locationAllowed`, `platform`; trường không gửi thì giữ nguyên; chưa có bản ghi thì tạo | 200 `GuestDeviceDto` | 400 · 403 |
| POST | `/activities` | Ghi một lượt: `{ type, slug?, query? }`. `SEARCH` cần `query`; loại khác cần `slug` của di sản đã xuất bản. Dùng cho **nghe thuyết minh** (chạy ở thiết bị) và cho client muốn tự ghi | 200 `{ recorded, reason }` (`reason` = `DUPLICATE` hoặc `STUDENT_SESSION` khi không ghi) | 400 · 404 |
| GET | `/history` | Lịch sử của thiết bị, mới nhất trước, trong 90 ngày; `page`, `pageSize` (mặc định 20, tối đa 100); bắt buộc mã; mã chưa từng thấy thì rỗng | 200 `PagedResult<GuestHistoryItemDto>` | 400 · 403 |
| DELETE | `/history/{id}` | Xóa thật một mục; mục không có hoặc của thiết bị khác thì như nhau | 200 | 400 · 403 · 404 |
| DELETE | `/history` | Xóa thật toàn bộ lịch sử của thiết bị; trả số mục đã xóa | 200 `{ deleted }` | 400 · 403 |
| GET | `/recommendations` | "Gợi ý cho bạn". Mã tùy chọn: không gửi hoặc chưa có lịch sử thì trả di sản nổi bật | 200 `GuestRecommendationsDto` | 400 · 403 |
| POST | `/nearby` | "Gần bạn". Thân `{ latitude, longitude }`; **không cần mã thiết bị** | 200 `NearbyDto` | 400 · 403 |

`GuestDeviceDto`: `deviceKey`, `isRegistered`, `platform`, `language` (thẻ `vi`, `en`, `ko`, `zh-Hans`, `ja`), `locationAllowed`, `createdAt`, `lastSeenAt`.
`GuestHistoryItemDto`: `id`, `type`, `typeLabel` (Tìm kiếm, Xem di sản, Đọc tóm tắt, Nghe thuyết minh, Xem mô hình 3D), `query` (mục tìm kiếm), `heritage` (thẻ di sản như kết quả tìm kiếm công khai, mục của di sản), `language`, `createdAt`.
`GuestRecommendationsDto`: `items[]`, `isPersonalized`, `usedFallback`, `message`. Mỗi mục: `heritage` (thẻ di sản theo ngôn ngữ), `reasons[]` (điểm chung: `SAME_TYPE`, `SAME_PROVINCE`, `SAME_REGION`,
`SAME_LEVEL`, `OVERLAPPING_ERA`, `SIMILAR_TEXT`; **giao diện tự ghép câu theo ngôn ngữ**), `becauseOfHeritageId`, `becauseOfName` (di sản khách quan tâm giống nhất, tên theo ngôn ngữ). Di sản nổi bật thay thế thì `reasons` rỗng.
`NearbyDto`: `items[]` (`heritage`, `distanceMeters`, `distanceText`), `unavailableReason` (`OUTSIDE_VIETNAM`), `message`.

Lỗi: `400 INVALID` hoặc `REQUIRED` kèm lỗi theo trường (`deviceKey`, `platform`, `language`, `type`, `slug`, `query`, `latitude`, `longitude`, `page`, `pageSize`); `403 GUEST_ONLY` với học sinh đã đăng nhập;
`404 BLOCKED` khi di sản không có, chưa xuất bản (ghi lượt) hoặc mục lịch sử không phải của thiết bị (xóa); `429` vượt giới hạn tần suất.

### Tự ghi lịch sử khi gọi API công khai

Khách gửi kèm `X-Device-Key` (và `X-Client-Platform`) khi gọi các API công khai sẵn có thì máy chủ **tự ghi** lượt tương ứng **sau khi việc chính thành công**; không gửi mã, hoặc yêu cầu của học sinh, thì không ghi.
Mã hoặc nền tảng sai định dạng bị từ chối **trước** khi làm việc chính.

| API công khai | Lượt được ghi | Ghi chú |
|---|---|---|
| `GET /api/v1/public/heritages?keyword=...` | `SEARCH` với từ khóa | Chỉ trang đầu có từ khóa (chuyển trang không phải lượt tìm mới); chỉ lọc không có từ khóa thì không ghi; từ khóa sai độ dài là 400 nên không ghi |
| `GET /api/v1/public/heritages/{slug}` | `VIEW` | |
| `GET /api/v1/public/heritages/{slug}/summary` | `READ_SUMMARY` | Chỉ khi di sản có tóm tắt (404 thì không ghi) |
| `GET /api/v1/public/heritages/{slug}/model` | `VIEW_3D` | Chỉ khi có mô hình đang hiển thị (404 thì không ghi) |
| `POST /api/v1/public/guest/activities` | loại khách gửi | Dùng cho `LISTEN` |

Bản đồ, dòng thời gian, trang chủ, tìm kiếm chỉ lọc không phải là một lượt xem hay tìm nên không ghi. Hai cách ghi dùng chung bộ lọc lượt lặp 10 phút nên client có thể làm cả hai mà không tính đôi.

## 4. Tác vụ nền dọn lịch sử

`GuestHistoryCleanupService` chạy một lần lúc khởi động rồi theo chu kỳ `Guests:CleanupIntervalHours` (mặc định 24 giờ; 0 hoặc âm thì tắt), xóa thật mọi lượt cũ hơn 90 ngày của mọi thiết bị bằng một lệnh xóa hàng loạt.
Lỗi một vòng chỉ vào nhật ký, vòng sau chạy lại. Nhật ký chỉ ghi số lượt đã xóa, không ghi mã thiết bị hay nội dung. **Bản ghi thiết bị không bị xóa** (không có thông tin cá nhân; còn dùng cho lượt mở khóa quảng cáo của luồng khác).

## 5. Cấu hình

| Khóa | Mặc định | Ý nghĩa |
|---|---|---|
| `Guests:CleanupIntervalHours` | 24 | Chu kỳ tác vụ dọn lịch sử; `<= 0` tắt |
| `RateLimiting:PublicRequestsPerMinute` | 300 | Như API công khai |
| `Ai:Mode` | `Simulated` | `Simulated` dùng bộ tính cục bộ (không gọi mạng); `Remote` gọi Dịch vụ AI nội bộ `POST /v1/rank-related` (dùng chung với gợi ý của học sinh) |

Không có khóa bí mật nào ở luồng này.

## 6. Quyết định hiện thực và khác với đặc tả

- **Máy chủ có thể cấp mã thiết bị.** Đặc tả để app hoặc trình duyệt sinh mã; `POST /devices` không kèm `X-Device-Key` cấp một mã đúng định dạng (cho client không có bộ sinh số ngẫu nhiên an toàn). Client có bộ sinh thì tự sinh như đặc tả.
- **Lượt lặp 10 phút áp cho mọi loại lượt**, theo (thiết bị, loại, di sản hoặc từ khóa). Đặc tả nói về lượt xem lặp; áp cho cả nghe, đọc tóm tắt, xem 3D và tìm để một lần bấm lặp không tính đôi trọng số gợi ý.
- **Chỉ trang đầu của kết quả tìm được ghi.** Chuyển trang là cùng một lượt tìm.
- **Di sản đang làm sở thích không được gợi ý lại**, cộng với di sản đã xem từ 3 lần như đặc tả. Nếu không loại, di sản vừa xem sẽ tự giống chính nó nhất và đứng đầu mọi danh sách.
- **"Gần bạn" dùng `POST` có thân**, không dùng `GET` có tham số, để tọa độ không nằm trên đường dẫn: nhật ký yêu cầu của máy chủ web ghi cả chuỗi truy vấn khi bật mức Information. Kiểm thử bật mọi mức nhật ký
  (kể cả nhật ký yêu cầu và câu lệnh của EF Core) rồi khẳng định tọa độ không xuất hiện, và có đối chứng từ khóa trên đường dẫn thì có xuất hiện.
- **Dữ liệu gửi Dịch vụ AI** theo hợp đồng sẵn có của gợi ý (id, thuộc tính công khai của di sản, trọng số), không chỉ id và trọng số như câu chữ của đặc tả; không có gì thuộc về thiết bị.
- **Điểm chung của gợi ý trả dạng mã** (`reasons`) thay vì một câu tiếng Việt, vì phần công khai có 5 ngôn ngữ; giao diện ghép câu.
- **Không giới hạn tần suất riêng theo thiết bị** cho việc tạo thiết bị: dùng giới hạn chung của API công khai theo IP (nhiều khách có thể chung IP ở điểm tham quan nên không đặt ngưỡng chặt hơn).
- **Học sinh gọi chức năng của khách là 403**, riêng ghi tay một lượt trả 200 không ghi, như mục 1.

## 7. Kiểm thử

- **Unit** (`GuestRulesTests`, `GuestServiceTests`, trên SQLite với đồng hồ tua được): mã thiết bị đúng và sai, hệ số, khoảng cách, làm tròn, khung Việt Nam nới 1 độ, chữ khoảng cách; tạo thiết bị chỉ khi cần lưu, lượt lặp,
  mức 500 lượt, hạn 90 ngày và dọn, xóa từng mục và xóa hết chỉ trên thiết bị của mình, lỗi khi ghi không làm hỏng việc chính.
- **API** (`GuestApiTests`, `GuestRecommendationApiTests`, `GuestNearbyApiTests`, `GuestCleanupTests`, trên SQLite): đăng ký, cài đặt, tự ghi tìm, xem, tóm tắt, 3D, ghi tay, học sinh không bị ghi và bị 403, lịch sử và
  phân trang, xóa; gợi ý theo lịch sử, hệ số, cửa sổ 20 lượt, loại di sản đã xem 3 lần, không gợi ý di sản nháp hoặc đã lưu trữ, ngôn ngữ, Dịch vụ AI lỗi, dữ liệu gửi Dịch vụ AI, không gọi mô hình ngôn ngữ; gần bạn
  (thứ tự, khoảng cách, giới hạn 6, ngoài Việt Nam, tọa độ sai, tọa độ không lọt vào nhật ký, bảng không có cột vị trí); tác vụ nền dọn.
- **SQL Server** (`GuestSqlServerTests`, database tạm của LocalDB): CSDL tự từ chối dòng sai (mã trùng, nền tảng, ngôn ngữ, lượt không đúng đích, khóa ngoại), giá trị mặc định, hình dạng cột và chỉ mục, mười yêu cầu
  đăng ký cùng lúc chỉ tạo một thiết bị, mức 500 lượt và dọn quá hạn bằng lệnh xóa hàng loạt, cả luồng đăng ký, ghi, xem, gợi ý, gần bạn, xóa. `SqlServerDatabaseTests` cập nhật số liệu lược đồ.
- **Smoke test trên Api chạy thật**: `scripts/smoke-khach-tham-quan.sh` (cách chạy ở đầu tệp; nên dùng database tạm, tên bắt đầu `dsvhvn_tam_a2_`).

## 8. Chạy thử bằng tay

```bash
KEY=$(python -c "import uuid; print(uuid.uuid4())")
curl -s -X POST http://localhost:5080/api/v1/public/guest/devices -H "X-Device-Key: $KEY" -H "X-Client-Platform: ANDROID" -H "Content-Type: application/json" -d '{"language":"en"}'
curl -s "http://localhost:5080/api/v1/public/heritages/<slug>?lang=en" -H "X-Device-Key: $KEY"             # tự ghi lượt xem
curl -s "http://localhost:5080/api/v1/public/guest/history" -H "X-Device-Key: $KEY"
curl -s "http://localhost:5080/api/v1/public/guest/recommendations?lang=en" -H "X-Device-Key: $KEY"
curl -s -X POST http://localhost:5080/api/v1/public/guest/nearby -H "Content-Type: application/json" -d '{"latitude":21.033,"longitude":105.85}'
curl -s -X DELETE http://localhost:5080/api/v1/public/guest/history -H "X-Device-Key: $KEY"
```

## 9. Chưa làm ở luồng này

- **Giao diện** (chọn chế độ, trang chủ khách, Lịch sử và cài đặt, nút Gần bạn, xin quyền vị trí, tệp chuỗi 5 ngôn ngữ, sinh và lưu mã thiết bị ở app và trình duyệt): luồng giao diện của web học tập và app.
- **Cổng quảng cáo mở khóa tóm tắt và nghe** và bảng `ad_unlocks`: đã làm ở luồng Quảng cáo Google có thưởng, xem [quang-cao-google.md](quang-cao-google.md) (luồng đó đọc bảng thiết bị của luồng này và có thống kê lượt mở khóa).
- **Thống kê khách cho quản trị hệ thống**: đã có ở `GET /api/v1/admin/ads/stats` (thiết bị hoạt động, ngôn ngữ, di sản xem, nghe, xem 3D nhiều nhất; xem [quang-cao-google.md](quang-cao-google.md)); còn thiếu "thiết bị mới theo ngày" và theo nền tảng (không tra theo từng mã thiết bị).
- **Dịch vụ Python phía Dịch vụ AI** cho `POST /v1/rank-related`: dùng chung với gợi ý của học sinh, chưa có; bản giả lập và phương án dự phòng chạy được.
- **Đồng bộ lịch sử giữa hai thiết bị**: không làm theo quy tắc (hai thiết bị của cùng một người là hai khách khác nhau, không có tài khoản để ghép).
