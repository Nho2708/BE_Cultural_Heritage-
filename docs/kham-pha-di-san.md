# Luồng Khám phá di sản công khai (Backend)

Phần backend của khu khám phá công khai trên **web học tập**: khách và học sinh xem trang chủ, tìm kiếm và lọc di sản, bản đồ, chi tiết,
timeline **không cần đăng nhập**. Chỉ đọc; dùng lại dịch vụ và dữ liệu của luồng Kho di sản ([kho-di-san.md](kho-di-san.md)).

| Mục | Giá trị |
|---|---|
| Chức năng | Xem trang chủ · Tìm kiếm và lọc di sản · Khám phá bản đồ di sản · Xem timeline di sản · Xem chi tiết di sản |
| Màn hình phục vụ | Trang chủ, Tìm kiếm và lọc di sản, Bản đồ di sản, Chi tiết di sản, Dòng thời gian di sản (web học tập) |
| Bảng | Đọc `heritages`, `provinces`, `timelines`, `events`, `media`, `heritage_references`; không ghi |
| Cột mới | `heritages.search_name`, `heritages.search_text` (migration `ThemCotTimKiemKhongDauChoDiSan`, xem mục 3) |
| Lớp mới | `Domain/Rules/SearchText`, `HeritagePeriods`, `HeritageTimeText`, `HeritageLabels`; `Application/Discovery/DiscoveryService` và DTO; `Api/Controllers/PublicHeritagesController`; `Api/Security/PublicRateLimit` |
| Nguồn | `doc/plan/spec` (quy tắc Trang khám phá công khai, Timeline và thời kỳ, giới hạn tần suất) |

## 1. Phạm vi hiển thị

- Mọi endpoint ở `/api/v1/public` là công khai (`[AllowAnonymous]`). Có gửi kèm access token hỏng hay hết hạn cũng vẫn được xem như khách
  (không trả 401). Học sinh đã đăng nhập gọi giống hệt khách; giao diện theo cấp học là việc của web, API không đổi.
- Chỉ thấy di sản **PUBLISHED chưa xóa mềm**. Nháp, đã ẩn (ARCHIVED), đã xóa đều như không tồn tại: chi tiết và timeline trả 404
  (`errorCode` `BLOCKED`, "Không thể xem di sản. Không tìm thấy di sản."), tìm kiếm và bản đồ không có. Ẩn hay hiện lại có hiệu lực ngay.
- Không trả trạng thái, điều kiện xuất bản, mốc tạo/sửa, mã tham chiếu ngoài hay hai cột tìm kiếm.
- Danh mục 34 tỉnh mở cho khách ở `/public/provinces`. Endpoint quản trị `/api/v1/admin/provinces` giữ nguyên (vẫn cần quyền ADMIN) để web quản trị không đổi.
- CORS thêm `http://localhost:5174` (web học tập) bên cạnh `http://localhost:5173`.

## 2. Endpoint (tiền tố `/api/v1/public`, không cần đăng nhập)

Khuôn phản hồi chung `{ success, data, message, errorCode, traceId, errors }`. Giá trị enum viết HOA như CSDL.

| Method | Đường dẫn | Tham số | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/home` | — | 200 `{ featured[], totalHeritages, byType[], byRegion[] }`. `featured`: tối đa 8 thẻ di sản cấp UNESCO hoặc quốc gia đặc biệt, **mới cập nhật trước** (quy tắc cố định trong mã, không cấu hình). `byType` (5 loại) và `byRegion` (Bắc, Trung, Nam) là `{ value, label, count }`, luôn đủ mục, số 0 khi trống | — |
| GET | `/provinces` | — | 200 34 tỉnh `{ id, code, name, region }` theo mã. Trình duyệt lưu tạm 1 giờ (`Cache-Control: public, max-age=3600`) | — |
| GET | `/filters` | — | 200 `{ provinces[], regions[], heritageTypes[], recognitionLevels[], periods[] }`, mỗi lựa chọn `{ value, label }`; `periods[]` thêm `fromYear`, `toYear` (null là không giới hạn). Lưu tạm 1 giờ | — |
| GET | `/heritages` | `keyword`, `provinceId`, `region`, `heritageType`, `recognitionLevel`, `period`, `page`, `pageSize` (mặc định 20, tối đa 100) | 200 trang thẻ `{ id, slug, name, shortDescription, thumbnailUrl, provinceId, provinceName, region, heritageType, recognitionLevel, recognizedYear }` | 400 `INVALID` kèm `errors` theo trường (`keyword`, `region`, `heritageType`, `recognitionLevel`, `period`, `page`, `pageSize`) |
| GET | `/heritages/map` | như `/heritages` trừ `page`, `pageSize` | 200 `{ pins[], totalMatched, withoutLocation, isTruncated }`. `pins[]` tối đa 500, chỉ di sản có tọa độ: `{ id, slug, name, latitude, longitude, shortDescription, thumbnailUrl, provinceId, provinceName, heritageType, recognitionLevel, hasTimeline }`. `withoutLocation` là số di sản khớp bộ lọc nhưng không có vị trí (thường là phi vật thể) để giao diện ghi chú | 400 `INVALID` |
| GET | `/heritages/{slug}` | `slug` không phân biệt hoa thường | 200 chi tiết `{ id, slug, name, shortDescription, content, heritageType, recognitionLevel, recognizedYear, province, address, latitude, longitude, thumbnailUrl, hasTimeline, media[], references[], timeline[] }`. `media[]`: `{ id, mediaType, url, caption, credit, author, license, source, isThumbnail }` theo thứ tự thư viện, ghi công tách ba phần để hiện dưới ảnh. `references[]`: `{ id, heritageId, title, url, sourceType, sourceTypeLabel }` theo thứ tự nhập (ghép thành "Nguồn: …" ở giao diện). `timeline[]` rút gọn: `{ id, name, startYear, endYear, rangeLabel, eventCount }` | 404 `BLOCKED` |
| GET | `/heritages/{slug}/timeline` | — | 200 `{ heritageId, slug, name, thumbnailUrl, provinceName, phases[] }`; `phases[]`: `{ id, name, description, startYear, endYear, rangeLabel, events[] }`; `events[]`: `{ id, name, description, startYear, startMonth, startDay, endYear, endMonth, endDay, dateLabel, media[] }`. Di sản chưa có giai đoạn vẫn 200 với `phases` rỗng | 404 `BLOCKED` |

Giá trị bộ lọc:

- `region`: `Bắc`, `Trung`, `Nam`; gõ không dấu hay chữ thường đều nhận (`bac`, `NAM`). Di sản không có tỉnh (phi vật thể trải nhiều tỉnh) không hiện khi lọc theo vùng hoặc tỉnh.
- `heritageType`: `TANGIBLE`, `INTANGIBLE`, `NATURAL`, `MIXED`, `DOCUMENTARY`. `recognitionLevel`: `UNESCO`, `NATIONAL_SPECIAL`, `NATIONAL`, `PROVINCIAL`, `NONE`.
  Enum gửi lên không phân biệt hoa thường; giá trị lạ (kể cả số) bị 400.
- `period` (thời kỳ, khoảng năm cố định trong mã, năm âm là trước Công nguyên): `PREHISTORIC` (đến 180 TCN), `NORTHERN_RULE` (179 TCN đến 938),
  `INDEPENDENT_FEUDAL` (939 đến 1857), `MODERN` (1858 đến 1944), `CONTEMPORARY` (1945 đến nay).
- Các điều kiện kết hợp theo **VÀ**. Kết quả xếp: khớp nguyên cụm từ khóa trong tên trước, rồi cấp xếp hạng cao trước (UNESCO, quốc gia đặc biệt, quốc gia,
  cấp tỉnh, chưa xếp hạng), rồi theo tên.

### Chữ hiển thị sẵn cho timeline

Năm âm hiển thị "{n} TCN". `rangeLabel` của giai đoạn: `1010 – 1821`, `1962 – nay` (chưa có năm kết thúc là còn kéo dài đến nay), `đến 1821`, `28000 TCN – 1010`.
`dateLabel` của sự kiện xuống tới mức chi tiết đã biết: `1010`, `03/1010`, `15/03/1010`, `179 TCN`; có kết thúc thì `13/03/1954 – 07/05/1954`.
Giai đoạn xếp theo `sortOrder`; sự kiện xếp theo năm, tháng, ngày rồi `sortOrder` (thiếu tháng hoặc ngày đứng trước trong cùng kỳ).

## 3. Tìm kiếm có dấu và không dấu

**Vấn đề**: collation `Vietnamese_CI_AI` của CSDL chỉ bỏ qua dấu thanh (à á ả ã ạ); ă, â, ê, ô, ơ, ư, đ được coi là chữ riêng, nên
`LIKE '%thang long%'` không khớp "Thăng Long" và "den" không khớp "Đền".

**Cách làm**: chuẩn hóa hai phía về cùng một dạng ("gấp" chữ, `SearchText.Fold`): bỏ mọi dấu (đ thành d), chữ thường, mọi ký tự không phải
chữ hoặc số thành một dấu cách. Nội dung đã gấp lưu sẵn ở hai cột mới của `heritages` (migration `ThemCotTimKiemKhongDauChoDiSan`, cho phép NULL):

| Cột | Nội dung |
|---|---|
| `search_name` `nvarchar(260)` | tên đã gấp, bắt đầu bằng một dấu cách; dùng xếp kết quả khớp tên lên trước |
| `search_text` `nvarchar(max)` | tên, mô tả ngắn, địa chỉ, nội dung đã gấp, ngăn nhau bằng ` \| `, bắt đầu bằng một dấu cách; dùng tìm từ khóa |

- **Luôn khớp nội dung**: `AppDbContext.SaveChanges` tính lại hai cột khi di sản mới thêm hoặc khi tên, mô tả ngắn, địa chỉ hay nội dung đổi, nên mọi đường ghi
  (API quản trị, nạp dữ liệu) đều đúng. Di sản có trước migration được điền lúc khởi động Api (`DatabaseInitializer`) bằng `ExecuteUpdate`, **không đổi `updated_at`**
  nên thứ tự "mới cập nhật" của di sản nổi bật không bị xáo. Cột chỉ để tìm, không có trong DTO nào.
- **Từ khóa**: 2-100 ký tự sau khi bỏ khoảng trắng hai đầu (vi phạm thì 400 `INVALID`, "Từ khóa không hợp lệ: phải dài 2-100 ký tự."; toàn dấu câu thì
  "phải có ít nhất một chữ cái hoặc chữ số"); trống hoặc toàn khoảng trắng là không tìm theo từ khóa. Tìm trên tên, mô tả ngắn, địa chỉ và nội dung.
- **Khớp**: từ khóa gấp rồi tách thành từng từ (tối đa 8 từ); mỗi từ phải khớp **đầu một từ** của di sản (`LIKE '% từ%'`) và di sản phải có đủ mọi từ, không cần đứng liền nhau.
  Ví dụ "thang lo", "Thăng Long", "THANG LONG", "hoàng thành thăng" đều ra "Hoàng thành Thăng Long"; "hang" không ra "thang" vì nằm giữa từ.
- **An toàn**: từ khóa chỉ còn chữ và số sau khi gấp nên `%`, `_`, `[`, dấu nháy không thành ký tự đại diện; mẫu LIKE luôn đi qua tham số của EF Core (không ghép chuỗi SQL).
- **Hiệu năng**: LIKE với dấu `%` đầu mẫu quét cột; với mục tiêu vài chục đến vài trăm di sản là đủ (mục tiêu kiểm tải 40 di sản). Nếu số di sản lên hàng nghìn thì cân nhắc chỉ mục toàn văn.

## 4. Giới hạn tần suất và bộ nhớ đệm

- **Giới hạn**: 300 yêu cầu mỗi phút mỗi địa chỉ IP cho toàn bộ `/api/v1/public` (cửa sổ trượt, đếm trong bộ nhớ máy chủ, khởi động lại thì về 0, không thêm bảng).
  Vượt thì **429** với `errorCode` `TOO_MANY_REQUESTS`, "Có quá nhiều yêu cầu gửi tới trong thời gian ngắn. Vui lòng thử lại sau {n} giây." và header `Retry-After` (giây).
  Đổi ngưỡng bằng cấu hình `RateLimiting:PublicRequestsPerMinute`. Đăng nhập, API quản trị và `/health` không nằm trong bộ đếm này (mỗi loại có giới hạn riêng).
  Một trang web chỉ vài yêu cầu nên một lớp học chung IP vẫn dư sức. **Lưu ý triển khai**: đặt sau proxy ngược thì phải cấu hình chuyển tiếp địa chỉ client (forwarded headers), không thì mọi người chung một IP của proxy.
- **Bộ nhớ đệm**: chỉ hai danh mục cố định `/public/provinces` và `/public/filters` có `Cache-Control: public, max-age=3600` (trình duyệt và CDN). Trang chủ, tìm kiếm, bản đồ, chi tiết,
  timeline **không** lưu đệm để ẩn hoặc xuất bản di sản có hiệu lực ngay; nếu cần thì thêm sau khi đo tải.

## 5. Kiểm thử

- Đơn vị: `DiscoveryRulesTests` (gấp chữ, thời kỳ, chữ hiển thị mốc thời gian, vùng).
- Tích hợp trên SQLite (`DiscoveryApiTests`, `DiscoveryHomeTests`, `PublicRateLimitTests`): công khai không cần token và token hỏng vẫn xem được; chỉ di sản đã xuất bản (nháp, ẩn, xóa, hiện lại);
  từ khóa có dấu, không dấu, hoa thường, nhiều từ, đầu từ, ký tự đặc biệt; sửa di sản thì kết quả tìm đổi theo; điền lại cột tìm kiếm cho dữ liệu cũ mà không đổi `updated_at`;
  từng bộ lọc và kết hợp; năm thời kỳ (giai đoạn, sự kiện, giai đoạn chưa có năm kết thúc, di sản không có timeline); phân trang và trần 100; thẻ chỉ có trường công khai; bản đồ (chỉ có tọa độ, đếm không có vị trí, trần 500 ghim);
  chi tiết (ảnh có ghi công, nguồn, timeline rút gọn, không lộ trạng thái); timeline (thứ tự, nhãn, năm TCN, ảnh sự kiện); danh mục tỉnh, bộ lọc và tiêu đề lưu đệm; trang chủ (8 di sản nổi bật, thứ tự cập nhật, số đếm); 429.
- Tích hợp trên SQL Server LocalDB (`DiscoverySeededSqlServerTests`, `DiscoveryCollationSqlServerTests`, tự xóa database tạm): 20 di sản đợt 1 ở DRAFT thì công khai không thấy gì, xuất bản xong thì trang chủ, tìm không dấu
  (Thăng Long, Đền Hùng, Quan họ, Đờn ca tài tử…), bộ lọc, thời kỳ, bản đồ, chi tiết và timeline của cả 20 di sản đọc được; ẩn một di sản thì biến mất ngay; đủ chữ ă â ê ô ơ ư đ; nội dung ~85.000 ký tự tìm được từ cuối;
  xếp hạng theo tên và cấp trên SQL Server.
