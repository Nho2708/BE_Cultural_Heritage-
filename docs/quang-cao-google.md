# Luồng Quảng cáo Google có thưởng (Backend)

Phần backend cho **cổng quảng cáo** của khách tham quan: khách xem hết một quảng cáo **thật của Google** (app: **AdMob**, quảng cáo có thưởng; web: **Ad Manager**, quảng cáo có thưởng cho web) thì mở được
**tóm tắt và nghe thuyết minh của một di sản** trong **24 giờ**, trên đúng thiết bị đó. Mọi thứ khác miễn phí, không cần quảng cáo: tìm kiếm, bản đồ, dòng thời gian, nội dung đầy đủ của di sản, ảnh,
xem 3D, gợi ý, lịch sử. Chỉ mở khóa khi **Google xác nhận người xem đã nhận thưởng**; máy chủ **không bao giờ** tin tham số máy khách tự khai. Học sinh đã đăng nhập không bao giờ thấy quảng cáo.
Không có chiến dịch quảng cáo tự quản lý, không lưu doanh thu (xem ở bảng điều khiển AdMob và Ad Manager).

| Mục | Giá trị |
|---|---|
| Chức năng | Xem quảng cáo có thưởng để mở tóm tắt · Xử lý xác minh phần thưởng quảng cáo (hệ thống) · Xem thống kê khách và lượt mở khóa bằng quảng cáo (quản trị hệ thống) |
| Màn hình phục vụ | Web học tập và app, chế độ Khách tham quan: lời mời xem quảng cáo ở nút "Tóm tắt" và "Nghe"; web quản trị của quản trị hệ thống: thống kê khách và quảng cáo |
| Bảng (CSDL v4, migration `ThemLuotMoKhoaQuangCao`) | `ad_unlocks` (mới); đọc `guest_devices`, `guest_activities`, `heritages`, `heritage_summaries`, `audit_logs` |
| Lớp mới | `Domain/Guests/AdUnlock`, `Domain/Rules/AdRules`, enum `AdNetwork`, mã thao tác `AD_REWARD_REJECTED`; `Application/Ads` (`AdUnlockService`, `AdRewardService`, `AdStatsService`, `AdMobSignature`, `AdsOptions`, DTO, `AdMessages`); `Api/Controllers/AdsController`, `AdMobVerificationController`, `AdminAdsController`; `Infrastructure/Ads/GoogleAdMobKeyStore`, `Infrastructure/Security/HmacAdRewardTokenCodec`, `InMemoryAdRewardThrottle` |
| Nguồn | `doc/plan/spec` (use case Xem quảng cáo có thưởng để mở tóm tắt, Xử lý xác minh phần thưởng quảng cáo, Xem thống kê khách và lượt mở khóa bằng quảng cáo; các quy tắc nhóm Quảng cáo Google có thưởng; các thông báo của cổng) · `doc/huong-moi/05-quyet-dinh-can-chot.md` (mục "Sửa" quảng cáo Google thật) · `doc/database/dsvhvn-v4.dbml` |

## 1. Quy tắc

| Quy tắc | Nội dung |
|---|---|
| Cổng chỉ khóa hai việc | **Tóm tắt** (cả bản ngắn lẫn bản chi tiết, `GET /api/v1/public/heritages/{slug}/summary`) và **nghe thuyết minh** (app ghi lượt `LISTEN` ở `POST /api/v1/public/guest/activities`, vì giọng đọc chạy ở thiết bị). Mọi API khác của phần công khai không qua cổng. Không biểu ngữ, không quảng cáo xen kẽ |
| Cổng không áp dụng | (1) **Học sinh đã đăng nhập** (web và app): máy chủ quyết định theo **phiên đăng nhập**, không theo khai báo của máy khách. (2) **Cổng tắt**: `Ads:GateEnabled` = false, hoặc chưa cấu hình đơn vị quảng cáo nào (cổng tự tắt). (3) **Thiết bị khai báo không có quảng cáo** (bản demo iOS bằng Expo Go, không có mô-đun quảng cáo): tiêu đề `X-Ads-Unavailable: true`, máy chủ **chỉ chấp nhận khi `Ads:AllowNoAdsDeclaration` bật** (môi trường phát triển và demo); môi trường thật bỏ qua nên khách không tự tắt được cổng |
| Chưa mở khóa | Khi cổng áp dụng và thiết bị chưa có lượt mở khóa **còn hạn** cho đúng di sản: **403 `AD_UNLOCK_REQUIRED`** kèm lời mời "Xem một quảng cáo ngắn để mở tóm tắt và nghe thuyết minh của "{tên di sản}" trong 24 giờ? Các nội dung khác vẫn miễn phí." Thiếu `X-Device-Key` cũng coi là chưa mở khóa |
| Di sản chưa có tóm tắt | Cổng kiểm **sau** khi biết di sản có tóm tắt tiếng Việt đã duyệt. Chưa có thì vẫn 404 `SUMMARY_UNAVAILABLE` như cũ: **không hiện cổng**, không mở khóa, không tính lượt. Ghi lượt `LISTEN` cho di sản như vậy cũng không bị chặn |
| Thời gian mở khóa | **24 giờ** kể từ lúc **máy chủ** ghi lượt (giờ máy chủ, không theo đồng hồ máy khách), cho **đúng một di sản trên đúng một thiết bị**, ở **mọi ngôn ngữ** (đổi ngôn ngữ không phải xem lại), mở lại bao nhiêu lần cũng được. Hết hạn thì cổng hiện lại. Không chuyển sang thiết bị hay di sản khác. Con số 24 giờ cố định trong mã (`AdRules`) |
| Đang mở khóa thì không mời thêm | Một lượt mới cho cùng di sản và thiết bị **khi chưa hết hạn** (ví dụ lời gọi xác minh đến muộn) **không ghi dòng mới và không kéo dài hạn**; kết quả `recorded` = false, `reason` = `ALREADY_UNLOCKED`. Web: `start` trả `alreadyUnlocked` = true và không cấp mã |
| Lượt mở khóa còn hạn không mất | Khi khách xóa lịch sử hoặc đổi ngôn ngữ; mất khi khách xóa dữ liệu app (đổi mã thiết bị) |
| **App: chỉ AdMob xác minh phía máy chủ mới mở khóa** | Khi tải quảng cáo, app gắn **mã thiết bị làm `user_id`** và **mã di sản (số) làm `custom_data`** vào tùy chọn xác minh phía máy chủ. Khi người xem nhận thưởng, **AdMob gọi `GET` về máy chủ**; chỉ lời gọi hợp lệ mới ghi lượt mở khóa. Sự kiện "đã nhận thưởng" trên máy **không đủ**: app chỉ hỏi `GET /api/v1/public/ads/unlock` (mỗi 2 giây, tối đa 20 giây) rồi mở nếu có |
| Lời gọi xác minh hợp lệ | Đồng thời: (a) **chữ ký ECDSA** (P-256, SHA-256, DER, base64 an toàn cho đường dẫn) đúng theo **khóa công khai của Google** (`key_id`); chữ ký tính trên chuỗi truy vấn **thô** đứng trước `&signature=`; (b) `ad_unit` thuộc danh sách đã cấu hình (so theo **phần số sau dấu gạch chéo**, vì AdMob chỉ gửi phần số), đồng thời cho biết **Android hay iOS**; (c) `user_id` là UUID phiên bản 4; (d) `custom_data` là mã của một di sản **đã xuất bản**; (e) `timestamp` (mili giây) **không lệch quá 1 giờ** so với giờ máy chủ; (f) `transaction_id` có và dài tối đa 255 ký tự; (g) mã giao dịch chưa ghi |
| Gọi lại cùng mã giao dịch | Mã giao dịch đã ghi (Google gọi lại do lỗi mạng hoặc phản hồi chậm) thì **không ghi thêm**, vẫn trả **200** để Google không gọi lại, `reason` = `DUPLICATE`; **không phải lượt bị từ chối**, không ghi nhật ký. Kiểm ngay sau khi chữ ký đúng, trước kiểm thời điểm, nên gọi lại rất muộn vẫn là gọi lại |
| Không hợp lệ | **Thiếu hoặc sai chữ ký**: **400 `AD_REWARD_REJECTED`** (lời chung "Chưa mở được tóm tắt...", không nói rõ lý do). **Chữ ký đúng** (lời gọi thật của Google) nhưng sai điều kiện khác: **200** `{ recorded: false, reason: <lý do> }`, vì trả lỗi chỉ làm Google gọi lại vô ích và lời gọi thử của nút kiểm địa chỉ (Verify URL) trong bảng điều khiển AdMob mang đơn vị và tham số thử. Cả hai: **không mở khóa**, không tạo cả thiết bị, ghi **một dòng nhật ký thao tác** `AD_REWARD_REJECTED` (đối tượng `ad_unlocks`, không có `target_id`, người thực hiện là hệ thống): chỉ có lý do, mạng quảng cáo, nền tảng (nếu đã biết). **Không** ghi IP, chữ ký, mã thiết bị, mã giao dịch |
| Sáu lý do từ chối | `INVALID_SIGNATURE` (chữ ký sai hoặc mã khóa lạ) · `UNKNOWN_AD_UNIT` (đơn vị quảng cáo lạ) · `INVALID_PARAMETER` (thiếu hoặc sai tham số, kể cả thiếu chữ ký, di sản không có hoặc chưa xuất bản) · `STALE_TIMESTAMP` (thời điểm lệch quá 1 giờ) · `INVALID_WEB_TRANSACTION` (mã web sai chữ ký, sai thiết bị hoặc di sản, hết hạn) · `RATE_LIMITED` (web vượt giới hạn tần suất) |
| **Web: trình duyệt báo, kém tin cậy hơn** | Không có xác minh phía máy chủ của Google, nên: `POST web/start` **cấp mã giao dịch một lần** khi khách bắt đầu xem (chuỗi ngẫu nhiên + **chữ ký HMAC-SHA256** bằng khóa suy ra từ khóa bí mật của máy chủ, chứa mã thiết bị, mã di sản và thời điểm cấp, **hết hạn sau 10 phút**, không lưu bảng nào); trình duyệt chỉ gọi `POST web/rewarded` khi nhận **sự kiện "đã cấp thưởng" của Google** (không phải "sẵn sàng" hay "đã đóng"), gửi lại mã cùng di sản và đơn vị quảng cáo đã phát. Máy chủ kiểm chữ ký, hạn, khớp thiết bị và di sản, đơn vị quảng cáo thuộc cấu hình. Lượt ghi có mạng `AD_MANAGER`, nền tảng `WEB` (nguồn "trình duyệt báo" suy từ mạng) |
| Mã giao dịch không trùng | Ràng buộc `UX_ad_unlocks_network_transaction`: không trùng **trong một mạng**; một mã chỉ mở khóa một lần, kể cả khi hai lời gọi đến cùng lúc (lời gọi thua coi là gửi lại) |
| Giới hạn tần suất (chỉ web) | Tối đa **10 lượt mở khóa mỗi giờ cho một mã thiết bị** và **60 lượt mỗi giờ cho một địa chỉ IP** (nhiều khách có thể chung IP ở điểm tham quan). Vượt thì **429 `AD_REWARD_RATE_LIMITED`** ("Hãy thử lại sau {số phút} phút") kèm `Retry-After` (giây). Bộ đếm **trong bộ nhớ máy chủ** (cửa sổ trượt 1 giờ, mất khi khởi động lại, không thêm bảng); **IP chỉ nằm trong bộ đếm này**, không vào cơ sở dữ liệu hay nhật ký. Báo lại mã đã ghi hoặc di sản đang mở khóa không tính vào giới hạn. Nhật ký thao tác chỉ ghi **tối đa một dòng mỗi thiết bị mỗi giờ** cho lý do này. **App không giới hạn theo giờ** (đã có xác minh của Google) |
| Đơn vị quảng cáo thử | Khi phát triển, kiểm thử và demo **chỉ dùng đơn vị quảng cáo thử do Google công bố** (quảng cáo thật của SDK với nội dung thử, không sinh doanh thu); dùng mã thật khi đang phát triển hoặc tự bấm quảng cáo thật là vi phạm chính sách của Google. **Mã đơn vị nằm trong cấu hình môi trường**, không viết cứng trong mã; mã thật không commit vào kho |
| Đồng ý quảng cáo cá nhân hóa | Hộp đồng ý **UMP** cho khách ở EU và UK, và mức xếp hạng nội dung **phù hợp mọi lứa tuổi (G)** là việc của **app và trình duyệt** (SDK của Google). Máy chủ **không lưu** lựa chọn đồng ý, không gắn với mã thiết bị, không nhận thông tin nào của học sinh; chỉ công bố mức `G` ở `GET config` để giao diện đặt đúng |
| Không lưu | Địa chỉ IP, mã quảng cáo của người xem, doanh thu, nội dung quảng cáo, chiến dịch. Dòng mở khóa giữ **12 tháng** rồi tác vụ nền **xóa thật** (`AdRules.RetentionMonths`) |
| Khóa công khai của Google | Danh sách tải từ `https://www.gstatic.com/admob/reward/verifier-keys.json` (cấu hình `Ads:AdMob:VerifierKeysUrl`, chỉ nhận HTTPS), **đệm trong bộ nhớ 24 giờ**; gặp **mã khóa lạ** thì tải lại ngay (Google có thể vừa xoay khóa), **tối đa một lần tải lại mỗi lời gọi** và **không thường hơn một lần mỗi 60 giây** (để lời gọi giả mạo không bắt máy chủ tải liên tục). Tải lỗi thì ghi cảnh báo và giữ danh sách cũ; khóa không có trong danh sách thì từ chối (không bao giờ chấp nhận khi không kiểm được chữ ký) |

## 2. Dữ liệu (migration `ThemLuotMoKhoaQuangCao`)

`ad_unlocks` (9 cột):

| Cột | Ghi chú |
|---|---|
| `id` | khóa chính tự tăng |
| `guest_device_id` | khóa ngoại tới `guest_devices`, **Restrict**; AdMob lấy từ `user_id` của xác minh phía máy chủ. Thiết bị chưa có bản ghi thì được tạo (nền tảng theo đơn vị quảng cáo), thiết bị đã có thì **không bị chạm** (không đổi nền tảng, ngôn ngữ, lần thấy cuối) vì lời gọi do Google, không do thiết bị |
| `heritage_id` | khóa ngoại tới `heritages`, **Restrict**; AdMob lấy từ `custom_data` |
| `platform` | `ANDROID`, `IOS` (AdMob) hoặc `WEB` (Ad Manager); AdMob suy ra **từ đơn vị quảng cáo đã cấu hình**, vì xác minh phía máy chủ không cho biết nền tảng. CHECK `CK_ad_unlocks_platform` |
| `ad_network` | `ADMOB` hoặc `AD_MANAGER` (CHECK `CK_ad_unlocks_ad_network`); **nguồn báo** không có cột riêng, suy từ mạng (AdMob: máy chủ xác minh với Google; Ad Manager: trình duyệt báo) |
| `transaction_id` | `nvarchar(255)`; AdMob: `transaction_id` của Google; web: mã một lần có chữ ký do máy chủ cấp |
| `ad_unit_id` | `nvarchar(100)`, được NULL; đơn vị quảng cáo đã phát |
| `unlocked_at`, `expires_at` | `datetime2(3)` UTC; `expires_at` = `unlocked_at` + 24 giờ |

Ràng buộc: UNIQUE thường **`UX_ad_unlocks_network_transaction`** (`ad_network`, `transaction_id`); CHECK **`CK_ad_unlocks_network_platform`** (AdMob chỉ Android hoặc iOS, Ad Manager chỉ web); CHECK
**`CK_ad_unlocks_expiry`** (`expires_at` > `unlocked_at`); chỉ mục **`IX_ad_unlocks_device_heritage`** (`guest_device_id`, `heritage_id`, `expires_at`) cho việc kiểm còn hạn; hai khóa ngoại Restrict.
Mã thao tác mới của `audit_logs.action`: **`AD_REWARD_REJECTED`** (cột không có CHECK nên không cần đổi bảng); `target_type` = `ad_unlocks`.

## 3. API

### 3.1 Cho khách (tiền tố `/api/v1/public/ads`, không cần đăng nhập)

Khách gửi mã thiết bị ở **`X-Device-Key`** (UUID phiên bản 4). Phản hồi **không được lưu đệm**; giới hạn như API công khai (300 yêu cầu mỗi phút mỗi IP, vượt thì 429).

| Phương thức, đường dẫn | Việc làm |
|---|---|
| `GET config` | `gateActive`, `unlockHours` (24), `maxContentRating` (`G`), `androidAdUnitId`, `iosAdUnitId`, `webAdUnitPath` (đơn vị đầu tiên từ cấu hình), `allowNoAdsDeclaration`. Không có thông tin riêng của thiết bị |
| `GET unlock?slug=` | Trạng thái mở khóa của di sản trên thiết bị: `gateRequired`, `unlocked`, `expiresAt`, `openReason` (`STUDENT`, `GATE_DISABLED`, `NO_ADS_DEVICE` khi cổng không áp dụng). 404 nếu di sản không có hoặc chưa xuất bản. **App hỏi lại mỗi 2 giây, tối đa 20 giây** sau khi xem hết quảng cáo |
| `POST web/start` `{ slug }` | Cần `X-Device-Key`. Cấp `transactionId` (hết hạn sau 10 phút, `transactionExpiresAt`) và `adUnitPath`. Đang mở khóa (hoặc cổng không áp dụng) thì `alreadyUnlocked` = true, không cấp mã. Học sinh: 403 `GUEST_ONLY`. Chưa cấu hình đơn vị cho web: 422 `AD_UNAVAILABLE` |
| `POST web/rewarded` `{ slug, transactionId, adUnit }` | Trình duyệt báo đã nhận thưởng. 200 `{ recorded, reason, expiresAt }` (`recorded` true: dòng mới; false: `DUPLICATE` hoặc `ALREADY_UNLOCKED`); 400 `AD_REWARD_REJECTED`; 429 `AD_REWARD_RATE_LIMITED` kèm `Retry-After`. Học sinh: 403 `GUEST_ONLY` |

### 3.2 Xác minh phía máy chủ của AdMob (hệ thống)

`GET /api/v1/ads/admob/ssv` (không đăng nhập, bảo vệ bằng chữ ký của Google; giới hạn như API công khai). Khai địa chỉ này (HTTPS, công khai) trong bảng điều khiển AdMob, mục xác minh phía máy chủ của
đơn vị quảng cáo có thưởng. Tham số do Google gửi: `ad_network`, `ad_unit`, `custom_data`, `reward_amount`, `reward_item`, `timestamp`, `transaction_id`, `user_id`, `signature`, `key_id`.
Hợp lệ (kể cả gọi lại) thì **200** `{ recorded, reason, expiresAt }`; thiếu hoặc sai chữ ký thì **400 `AD_REWARD_REJECTED`**; chữ ký đúng nhưng sai điều kiện khác thì **200** với `recorded` = false
và `reason` là lý do từ chối (không mở khóa, có ghi nhật ký).

### 3.3 Cổng ở API có sẵn

| API | Hành vi mới |
|---|---|
| `GET /api/v1/public/heritages/{slug}/summary` | Khi cổng áp dụng và chưa mở khóa: **403 `AD_UNLOCK_REQUIRED`**. Phản hồi không lưu đệm (phụ thuộc thiết bị). Thứ tự: 404 di sản không có → 404 `SUMMARY_UNAVAILABLE` (chưa có tóm tắt) → 403 (cổng) → 200 |
| `POST /api/v1/public/guest/activities` với `type` = `LISTEN` | Cùng cổng: 403 `AD_UNLOCK_REQUIRED` và lượt **không được ghi**. Các loại lượt khác không qua cổng |

### 3.4 Quản trị hệ thống

`GET /api/v1/admin/ads/stats?from=&to=` (chỉ ADMIN; ngày Việt Nam `yyyy-MM-dd`, mặc định 30 ngày gần nhất, tối đa 366 ngày; sai khoảng thì 400): lượt mở khóa tổng, **theo ngày** (đủ từng ngày, kể cả ngày
không có lượt), **theo nền tảng**, **theo mạng quảng cáo** (kèm nguồn báo), **di sản được mở khóa nhiều nhất** (tối đa 10); lượt **bị từ chối theo lý do** (lấy từ nhật ký thao tác, đủ sáu lý do kèm nhãn
tiếng Việt; gọi lại trùng mã giao dịch không tính); **khách**: số thiết bị hoạt động (có lượt trong khoảng hoặc lần thấy cuối trong khoảng), số thiết bị theo ngôn ngữ, di sản được **xem, nghe, xem 3D** nhiều
nhất (tối đa 10 mỗi bảng); `note` và hai liên kết sang bảng điều khiển AdMob và Ad Manager. **Không có doanh thu** và không tra cứu theo từng mã thiết bị.

## 4. Tác vụ nền

`GuestHistoryCleanupService` (cùng chu kỳ `Guests:CleanupIntervalHours`) xóa thật lượt mở khóa cũ hơn **12 tháng** cùng với lịch sử khách quá 90 ngày; chỉ ghi số dòng đã xóa vào log.

## 5. Cấu hình

Mục `Ads` (`appsettings.json`; tệp phát triển `appsettings.Development.json` đã điền **đơn vị quảng cáo thử của Google**):

| Khóa | Mặc định | Ý nghĩa |
|---|---|---|
| `Ads:GateEnabled` | `false` | Bật cổng. Tắt thì tóm tắt và nghe mở thẳng. Bật nhưng không có đơn vị quảng cáo nào thì cổng vẫn tắt. **Ở máy phát triển đang để `false`** cho tới khi giao diện của web và app có cổng; bật bằng `Ads__GateEnabled=true` |
| `Ads:AllowNoAdsDeclaration` | `false` (phát triển: `true`) | Chấp nhận tiêu đề `X-Ads-Unavailable: true` của bản demo iOS bằng Expo Go. **Môi trường thật để `false`** |
| `Ads:AdMob:AndroidAdUnitIds`, `IosAdUnitIds` | rỗng (phát triển: đơn vị thử `ca-app-pub-3940256099942544/5224354917` và `.../1712485313`) | Đơn vị quảng cáo có thưởng của app; ghi đầy đủ hoặc chỉ phần số |
| `Ads:AdMob:VerifierKeysUrl` | địa chỉ của Google | Danh sách khóa công khai; chỉ HTTPS |
| `Ads:AdMob:KeyCacheHours`, `KeyMinRefreshSeconds` | `24`, `60` | Đệm danh sách khóa; cách tối thiểu giữa hai lần tải lại |
| `Ads:AdManager:AdUnitPaths` | rỗng (phát triển: đơn vị thử `/22639388115/rewarded_web_example`) | Đơn vị quảng cáo có thưởng cho web |

Mã thật (khi nhóm có tài khoản AdMob và Ad Manager đã được Google duyệt) chỉ đặt bằng **User Secrets hoặc biến môi trường** (`Ads__AdMob__AndroidAdUnitIds__0`, ...), không commit. Khóa ký mã giao dịch
của web suy ra từ `Jwt:SigningKey` (đổi khóa JWT thì mã đang có mất hiệu lực).

## 6. Quyết định hiện thực và khác với đặc tả

- **Nền tảng của lượt AdMob suy từ đơn vị quảng cáo** (hai danh sách cấu hình Android và iOS), vì xác minh phía máy chủ của Google không gửi nền tảng; nhờ vậy lượt vẫn đúng khi thiết bị chưa đăng ký hoặc đã đăng ký nền tảng khác.
- **`ad_unit` của AdMob chỉ là phần số** sau dấu gạch chéo, nên so sánh theo phần số; cấu hình ghi đầy đủ hay chỉ phần số đều khớp.
- **Báo thưởng của web bắt buộc kèm di sản và đơn vị quảng cáo** (ngoài mã giao dịch) và đơn vị phải thuộc cấu hình. Môi trường **không có đơn vị cho web** thì web không tự mở khóa được (không có đường vòng), dù cổng đang hoạt động vì còn đơn vị AdMob.
- **Dòng nhật ký "vượt giới hạn tần suất" chỉ ghi lần từ chối đầu tiên của thiết bị trong giờ** và không kèm số lần: nhật ký thao tác chỉ thêm, không sửa được dòng đã ghi, nên không cập nhật bộ đếm vào dòng cũ. Số lượt vượt giới hạn trong thống kê vì thế là số thiết bị bị chặn theo giờ, không phải số yêu cầu bị chặn.
- **Gọi lại trùng mã giao dịch** được kiểm ngay sau chữ ký (trước thời điểm), và **di sản đang mở khóa** được kiểm sau đó; cả hai trả 200 không ghi.
- **Thống kê khách** gồm số thiết bị hoạt động, ngôn ngữ, di sản xem, nghe, xem 3D nhiều nhất; chưa có "thiết bị mới theo ngày" (cột `created_at` đã có, chỉ chưa dựng truy vấn).
- **Thông báo của cổng** (lời mời, chưa mở được, vượt giới hạn) có ở máy chủ bằng tiếng Việt kèm mã (`AD_UNLOCK_REQUIRED`, `AD_REWARD_REJECTED`, `AD_REWARD_RATE_LIMITED`); bản 5 ngôn ngữ do tệp chuỗi của giao diện dựa vào mã.
- **Cổng tắt mặc định ở mọi môi trường**, kể cả phát triển, để web học tập và app hiện có không bị chặn khi giao diện của cổng chưa làm.

## 7. Kiểm thử

Unit: `AdRulesTests` (quy tắc và cấu hình), `AdMobSignatureTests` (ký như Google bằng khóa tự sinh, mọi sửa đổi bị từ chối), `AdRewardSecurityTests` (mã giao dịch của web và bộ giới hạn tần suất),
`GoogleAdMobKeyStoreTests` (đệm 24 giờ, tải lại khi gặp mã khóa lạ, giữ danh sách cũ khi lỗi, chỉ HTTPS). Tích hợp (SQLite): `AdsGateApiTests`, `AdsRewardApiTests`, `AdsStatsApiTests`
(ký lời gọi xác minh bằng khóa tự sinh thay cho khóa của Google; chạy với đơn vị quảng cáo thử). Trên SQL Server LocalDB (database tạm `dsvhvn_tam_a2_…` tự xóa): `AdsSqlServerTests` (CHECK, UNIQUE, khóa ngoại,
Google gọi đồng thời cùng một giao dịch chỉ ghi một dòng, thống kê, dọn quá 12 tháng) và `SqlServerDatabaseTests` (38 bảng, 354 cột, 60 khóa ngoại, 49 CHECK).

```bash
dotnet test --filter "FullyQualifiedName~Ads|FullyQualifiedName~AdMob|FullyQualifiedName~AdReward|FullyQualifiedName~AdRules"
```

## 8. Chạy thử bằng tay

1. Bật cổng và đặt chế độ thử: `Ads__GateEnabled=true` (đơn vị quảng cáo thử đã có sẵn ở môi trường Development).
2. Gọi `GET /api/v1/public/heritages/{slug}/summary` với `X-Device-Key: <uuid v4>`: 403 `AD_UNLOCK_REQUIRED`.
3. Web: `POST /api/v1/public/ads/web/start`, rồi `POST /api/v1/public/ads/web/rewarded` với mã nhận được và `adUnit` = `/22639388115/rewarded_web_example`; gọi lại tóm tắt: 200.
4. App: khai địa chỉ `https://<máy chủ công khai>/api/v1/ads/admob/ssv` ở AdMob (cần HTTPS công khai; khi chạy trên máy cá nhân dùng đường hầm); khi xem hết quảng cáo thử, AdMob gọi về địa chỉ này
   (không giả được chữ ký của Google; kiểm thử tự động ký bằng khóa tự sinh).
5. Học sinh (đã đăng nhập) và thiết bị gửi `X-Ads-Unavailable: true` (khi `Ads:AllowNoAdsDeclaration` bật) mở thẳng.

## 9. Chưa làm ở luồng này

- **Giao diện** của cổng: lời mời, tải quảng cáo bằng SDK (AdMob trong bản dựng phát triển của app, Google Publisher Tag trên web), hộp đồng ý UMP, chờ xác nhận (hỏi `unlock` mỗi 2 giây, tối đa 20 giây), nhãn "Đã mở đến {giờ}", ghi chú "bản demo iOS không có quảng cáo", màn thống kê của quản trị hệ thống: luồng giao diện của web học tập, app và web quản trị.
- **Tài khoản AdMob và Ad Manager thật** đã được Google duyệt, mã đơn vị thật và thiết bị kiểm thử khai ở Google: chờ nhóm; tài khoản Apple Developer cho bản dựng iOS có quảng cáo: chờ người dùng quyết định.
- Thống kê "thiết bị mới theo ngày" của khách.
