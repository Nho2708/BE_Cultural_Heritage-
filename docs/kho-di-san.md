# Luồng Kho di sản: di sản, timeline, ảnh có ghi công, nguồn tham chiếu (Backend)

Phần backend của luồng Kho di sản, chỉ dành cho **quản trị hệ thống** (ADMIN): biên soạn nội dung di sản từ nguồn Cục Di sản
văn hóa, dựng timeline riêng cho từng di sản, quản lý thư viện ảnh kèm ghi công và khai báo nguồn tham chiếu. Trang công khai
của web học tập là luồng Khám phá di sản ([kham-pha-di-san.md](kham-pha-di-san.md)); luồng này chỉ cấp API cho web quản trị. Chạy trên CSDL v3,
**không thêm bảng hay cột** ([co-so-du-lieu.md](co-so-du-lieu.md)); hai cột tìm kiếm không dấu của `heritages` do luồng Khám phá di sản thêm sau
và tự cập nhật mỗi khi lưu di sản.

| Mục | Giá trị |
|---|---|
| Chức năng | Quản lý di sản · Quản lý timeline di sản · Khai báo nguồn tham chiếu |
| Màn hình phục vụ | Quản lý di sản, Biên tập di sản (các tab Thông tin chung, Ảnh và media, Dòng thời gian, Nguồn tham chiếu), Biểu mẫu giai đoạn và sự kiện |
| Bảng | `provinces` (chỉ đọc), `heritages`, `timelines`, `events`, `media`, `heritage_references` |
| Lớp mới | `Domain/Rules/HeritageRules` (quy tắc thuần) và `HeritagePublishing` (điều kiện xuất bản); `Application/Heritages/`: `HeritageService`, `TimelineService`, `HeritageMediaService`, `HeritageReferenceService` cùng validator, DTO; `Api/Controllers/AdminHeritagesController` (kèm danh mục tỉnh) và `AdminHeritageContentController` |
| Nguồn | `doc/plan/spec` (use case, quy tắc, thông báo của nhóm) · `doc/database/dsvhvn-v3.dbml` · `doc/huong-moi/03-nguon-cuc-di-san.md` |

## 1. Vòng đời của di sản

```
        Xuất bản (đủ điều kiện)            Ẩn
DRAFT ─────────────────────────▶ PUBLISHED ─────▶ ARCHIVED
                                     ▲               │
                                     └───────────────┘
                                   Hiện lại (kiểm lại điều kiện)
```

- **Tạo**: luôn ở DRAFT, chỉ cần có tên. Các trường khác có thì phải hợp lệ; thiếu thì vẫn lưu nháp được.
- **Xuất bản** (`POST /publish`, chỉ từ DRAFT) và **hiện lại** (`POST /restore`, chỉ từ ARCHIVED) chỉ thành công khi đủ điều kiện ở mục 2;
  thiếu thì 422 `NOT_READY` kèm danh sách còn thiếu và không đổi gì. **Ẩn** (`POST /archive`, chỉ từ PUBLISHED) đưa di sản khỏi mọi trang
  công khai, không chọn được vào bài học hoặc câu hỏi mới; bài học, câu hỏi, kết quả cũ gắn với di sản giữ nguyên.
- **Sửa**: DRAFT, ARCHIVED sửa tự do. Di sản đang PUBLISHED vẫn sửa được nhưng sau khi sửa phải **còn đủ điều kiện xuất bản**, không thì 422 và
  không lưu gì. Vì vậy trên di sản đang xuất bản còn có ba việc bị chặn: bỏ ảnh đại diện, xóa ảnh đại diện, xóa nguồn tham chiếu cuối cùng.
- **Xóa mềm** (`DELETE`): chỉ di sản DRAFT hoặc ARCHIVED, và chưa gắn với bài học hay câu hỏi nào (đã gắn thì chỉ được ẩn). Di sản đang
  xuất bản phải ẩn trước. Đường dẫn của di sản đã xóa vẫn giữ chỗ vì cột `slug` của CSDL không trùng trên mọi dòng.
- Người kiểm khác người nhập là **quy trình của nhóm**, hệ thống không cưỡng chế; nhật ký ghi người xuất bản.

## 2. Điều kiện xuất bản

| Trường thiếu (`errors` và `readiness.missing[].field`) | Điều kiện |
|---|---|
| `name`, `slug` | Có tên di sản và đường dẫn |
| `shortDescription` | Có mô tả ngắn |
| `content` | Nội dung tóm lược tối thiểu 200 từ (dấu đánh dấu Markdown như `##` không tính là từ) |
| `heritageType`, `recognitionLevel` | Có loại di sản và cấp xếp hạng |
| `recognizedYear` | Cấp xếp hạng khác NONE thì phải có năm công nhận, không lớn hơn năm hiện tại |
| `thumbnailUrl` | Có ảnh đại diện, và đó là một ảnh (IMAGE) trong thư viện ảnh của chính di sản (để có ghi công) |
| `provinceId` | Có tỉnh/thành, trừ di sản phi vật thể (INTANGIBLE) hoặc tư liệu (DOCUMENTARY) trải nhiều tỉnh |
| `coordinates` | Có tọa độ với di sản vật thể (TANGIBLE), thiên nhiên (NATURAL), hỗn hợp (MIXED) |
| `references` | Có ít nhất một nguồn tham chiếu |

Chi tiết di sản luôn kèm `readiness { isReady, missing[], warnings[] }` để giao diện tô đậm tab còn thiếu và bật/tắt nút Xuất bản.
`warnings` chỉ **nhắc** theo quy trình của nhóm, không chặn: chưa có nguồn là văn bản nhà nước; chưa có nguồn là bài của Cục Di sản văn hóa hoặc
UNESCO (nhận ra qua tiêu đề hoặc tên miền dsvh.gov.vn, unesco.org).

## 3. Quy tắc dữ liệu đã hiện thực

**Thông tin chung**

| Trường | Quy tắc |
|---|---|
| `name` | Bắt buộc, 1-255 ký tự, có ít nhất một chữ cái hoặc chữ số |
| `slug` | Bỏ trống khi tạo thì sinh từ tên (bỏ dấu, `đ` thành `d`, chữ thường, nối bằng gạch ngang; nếu trùng thêm `-2`, `-3`...); bỏ trống khi sửa thì giữ đường dẫn hiện tại. Tự nhập thì phải đúng dạng `chu-thuong-khong-dau-2` và không trùng di sản khác (409 `DUPLICATE`) |
| `shortDescription` | Tối đa 300 ký tự (chặt hơn cột 1000 của CSDL, theo quy tắc nghiệp vụ) |
| `content` | Tối đa 200.000 ký tự khi lưu; 200 từ khi xuất bản |
| `provinceId` | Phải là một trong 34 tỉnh/thành trong `provinces` |
| `latitude`, `longitude` | Nhập đủ cả hai hoặc bỏ trống cả hai; vĩ độ 8,0-23,5; kinh độ 102,0-117,5; làm tròn 6 chữ số thập phân |
| `recognizedYear` | Năm dương lịch từ 1 đến năm hiện tại (giờ Việt Nam) |
| `address`, `placeId` | Tối đa 500 và 255 ký tự |
| `heritageType`, `recognitionLevel` | Đúng giá trị enum của CSDL (HOA) |

**Timeline** (theo từng di sản, không có trục thời gian chung)

- Giai đoạn: tên bắt buộc (tối đa 255); năm bắt đầu, kết thúc tùy chọn, **năm âm là trước Công nguyên** (từ -100000 đến năm hiện tại), năm kết thúc không
  nhỏ hơn năm bắt đầu. Xếp theo `sortOrder`; thêm mà không gửi `sortOrder` thì xếp cuối; sửa mà không gửi thì giữ nguyên.
- Sự kiện: tên và **năm bắt đầu bắt buộc**; tháng 1-12; ngày phải có thật theo tháng (kể cả năm nhuận) và chỉ nhập khi đã có tháng; thời điểm kết thúc
  (tùy chọn) không trước thời điểm bắt đầu, so đến mức chi tiết mà cả hai bên cùng có (kết thúc chỉ có năm thì so năm). Hiển thị theo năm, tháng, ngày, rồi
  `sortOrder` (sự kiện thiếu tháng hoặc ngày đứng trước trong cùng kỳ). Đặt thứ tự có tác dụng khi nhiều sự kiện cùng thời điểm.
- Xóa giai đoạn xóa luôn các sự kiện và ảnh của sự kiện; xóa sự kiện xóa luôn ảnh của sự kiện.

**Ảnh và media** (lưu **đường dẫn**, không tải tệp lên; video, audio là đường dẫn nhúng)

- `mediaType` là IMAGE, VIDEO hoặc AUDIO. `url` là đường dẫn http/https, tối đa 500 ký tự, không trùng trong cùng thư viện (di sản hoặc sự kiện).
- **Ghi công bắt buộc đủ ba phần**: `author` (tác giả), `license` (giấy phép), `source` (nguồn: đường dẫn hoặc tên nơi đăng). Hệ thống ghép thành
  `credit` = `tác giả · giấy phép · nguồn` (tối đa 255 ký tự cả ba) và trả lại cả ba phần đã tách. Ba phần không chứa dấu `·`.
- Giấy phép chỉ nhận: CC0, CC BY, CC BY-SA (kèm số phiên bản), phạm vi công cộng / Public domain, ảnh nhóm tự chụp. CC BY-NC, CC BY-ND... bị từ chối.
- Mỗi dòng thuộc đúng một trong hai: di sản hoặc sự kiện (CHECK `CK_media_owner` của CSDL; API chỉ tạo qua hai đường dẫn tương ứng).
- **Ảnh đại diện** là một ảnh IMAGE trong thư viện của di sản, nhận biết bằng đường dẫn trùng `heritages.thumbnail_url`: chọn bằng `setAsThumbnail` khi thêm hoặc
  sửa ảnh, hoặc `PUT /heritages/{id}/thumbnail`. Đổi đường dẫn của ảnh đại diện thì ảnh đại diện đổi theo. Ảnh của sự kiện không làm ảnh đại diện được.
  Xóa ảnh đại diện của di sản chưa xuất bản thì bỏ luôn ảnh đại diện.

**Nguồn tham chiếu**

- `title` bắt buộc (tối đa 500). Quy trình của nhóm ghi theo mẫu "Tên tài liệu — Cơ quan hoặc tác giả, truy cập dd/mm/yyyy", bài lấy từ Cục Di sản văn hóa ghi rõ
  "Cục Di sản văn hóa"; hệ thống chỉ nhắc (mục 2), không kiểm chữ.
- `sourceType` chỉ nhận **GOV_DOC** (văn bản nhà nước), **WEBSITE**, **BOOK** (sách). Trang của Cục Di sản văn hóa, cổng thông tin nhà nước và UNESCO đều là WEBSITE (cơ quan ghi
  trong tiêu đề). WEBSITE bắt buộc có `url`.

**Tỉnh/thành**: 34 dòng theo Quyết định 19/2025, chỉ đọc (không có chức năng sửa tỉnh).

## 4. Endpoint (tiền tố `/api/v1/admin`, quyền **ADMIN**)

Mọi endpoint trả khuôn `{ success, data, message, errorCode, traceId, errors }`. Ai không phải ADMIN nhận 403 `FORBIDDEN`; chưa đăng nhập nhận 401.
Đối tượng không có hoặc thuộc di sản đã xóa nhận 404 (`errorCode` = `BLOCKED`, cùng khuôn "Không thể …. Không tìm thấy …").

### Di sản

| Method | Đường dẫn | Body / query | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/provinces` | — | 200 danh sách 34 tỉnh `{ id, code, name, region }` theo mã | — |
| GET | `/heritages` | `keyword` (tên, không phân biệt hoa thường và dấu), `provinceId`, `heritageType`, `recognitionLevel`, `status` (DRAFT, PUBLISHED, ARCHIVED), `page`, `pageSize` (mặc định 20, tối đa 100) | 200 trang `HeritageSummary` `{ id, name, slug, thumbnailUrl, provinceId, provinceName, heritageType, recognitionLevel, status, timelineCount, mediaCount, referenceCount, createdAt, updatedAt }`, sửa gần nhất trước | 400 `INVALID` (page, pageSize) |
| GET | `/heritages/suggest-slug` | `name`, `heritageId` (loại chính di sản đang sửa) | 200 đường dẫn gợi ý, không trùng | 400 `REQUIRED` |
| POST | `/heritages` | `{ name, slug?, shortDescription?, content?, heritageType?, recognitionLevel?, recognizedYear?, provinceId?, address?, latitude?, longitude?, placeId? }` | 201 `HeritageDetail` (DRAFT) | 400 `REQUIRED`/`INVALID` kèm `errors` theo trường · 409 `DUPLICATE` (`slug`) |
| GET | `/heritages/{id}` | — | 200 `HeritageDetail`: các trường trên + `province`, `thumbnailUrl`, `status`, `readiness`, `timelines[]` (kèm `events[]`, mỗi sự kiện kèm `media[]`), `media[]`, `references[]` | 404 |
| PUT | `/heritages/{id}` | như POST (`slug` bỏ trống là giữ nguyên) | 200 `HeritageDetail` "Lưu di sản thành công." | 400 · 404 · 409 · 422 `NOT_READY` (đang xuất bản mà sửa xong thiếu điều kiện) |
| DELETE | `/heritages/{id}` | — | 200 "Xóa di sản thành công." | 404 · 422 `BLOCKED` (đang xuất bản; đã dùng trong bài học/câu hỏi) |
| PUT | `/heritages/{id}/thumbnail` | `{ mediaId }` (null để bỏ chọn) | 200 `HeritageDetail` | 400 `INVALID` (không thuộc thư viện của di sản, không phải IMAGE) · 422 `BLOCKED` (bỏ chọn khi đang xuất bản) |
| POST | `/heritages/{id}/publish` | — | 200 `HeritageDetail` PUBLISHED, ghi `HERITAGE_PUBLISHED` | 404 · 422 `NOT_READY` (kèm `errors` theo trường thiếu) · 422 `BLOCKED` (không ở DRAFT) |
| POST | `/heritages/{id}/archive` | — | 200 ARCHIVED, ghi `HERITAGE_ARCHIVED` | 404 · 422 `BLOCKED` (không ở PUBLISHED) |
| POST | `/heritages/{id}/restore` | — | 200 PUBLISHED, ghi `HERITAGE_PUBLISHED` | 404 · 422 `NOT_READY` · 422 `BLOCKED` (không ở ARCHIVED) |

### Giai đoạn và sự kiện

| Method | Đường dẫn | Body | Thành công | Lỗi chính |
|---|---|---|---|---|
| POST | `/heritages/{heritageId}/timelines` | `{ name, description?, startYear?, endYear?, sortOrder? }` | 201 `Timeline` | 400 · 404 |
| PUT | `/heritages/{heritageId}/timelines/order` | `{ ids: [id giai đoạn theo thứ tự mới] }` (đủ và đúng bộ hiện có) | 200 danh sách giai đoạn đã xếp | 400 `INVALID` |
| PUT | `/timelines/{timelineId}` | như POST | 200 `Timeline` | 400 · 404 |
| DELETE | `/timelines/{timelineId}` | — | 200 (xóa luôn sự kiện và ảnh của sự kiện) | 404 |
| POST | `/timelines/{timelineId}/events` | `{ name, description?, startYear, startMonth?, startDay?, endYear?, endMonth?, endDay?, sortOrder? }` | 201 `Event` | 400 · 404 |
| PUT | `/timelines/{timelineId}/events/order` | `{ ids }` | 200 danh sách sự kiện đã xếp | 400 |
| PUT | `/events/{eventId}` | như POST | 200 `Event` | 400 · 404 |
| DELETE | `/events/{eventId}` | — | 200 (xóa luôn ảnh của sự kiện) | 404 |

### Ảnh

| Method | Đường dẫn | Body | Thành công | Lỗi chính |
|---|---|---|---|---|
| POST | `/heritages/{heritageId}/media` | `{ mediaType, url, caption?, author, license, source, sortOrder?, setAsThumbnail? }` | 201 `Media` `{ id, heritageId, eventId, mediaType, url, caption, credit, author, license, source, sortOrder, isThumbnail }` | 400 (thiếu ghi công, giấy phép không được dùng…) · 404 · 409 `DUPLICATE` (`url`) |
| PUT | `/heritages/{heritageId}/media/order` | `{ ids }` (ảnh của di sản, không gồm ảnh của sự kiện) | 200 danh sách ảnh đã xếp | 400 |
| POST | `/events/{eventId}/media` | như trên (`setAsThumbnail` không dùng được) | 201 `Media` | 400 · 404 · 409 |
| PUT | `/events/{eventId}/media/order` | `{ ids }` | 200 | 400 |
| PUT | `/media/{mediaId}` | như POST; `setAsThumbnail` true chọn, false bỏ chọn, bỏ trống giữ nguyên | 200 `Media` | 400 · 404 · 409 · 422 `BLOCKED` (bỏ ảnh đại diện của di sản đang xuất bản) |
| DELETE | `/media/{mediaId}` | — | 200 | 404 · 422 `BLOCKED` (ảnh đại diện của di sản đang xuất bản) |

### Nguồn tham chiếu

| Method | Đường dẫn | Body | Thành công | Lỗi chính |
|---|---|---|---|---|
| POST | `/heritages/{heritageId}/references` | `{ title, url?, sourceType }` (`GOV_DOC`, `WEBSITE`, `BOOK`; `WEBSITE` bắt buộc `url`) | 201 `Reference` `{ id, heritageId, title, url, sourceType, sourceTypeLabel }` | 400 · 404 |
| PUT | `/heritage-references/{referenceId}` | như POST | 200 `Reference` | 400 · 404 |
| DELETE | `/heritage-references/{referenceId}` | — | 200 | 404 · 422 `BLOCKED` (nguồn cuối cùng của di sản đang xuất bản) |

## 5. Nhật ký thao tác

Danh mục hành động của nhóm chỉ có hai mã cho di sản, nên chỉ hai việc được ghi vào `audit_logs` (`target_type` = `heritages`, `target_id` = id di sản,
`actor_role` = ADMIN, người thực hiện và IP của yêu cầu):

| Việc | Mã | `old_value` → `new_value` |
|---|---|---|
| Xuất bản (DRAFT sang PUBLISHED) và hiện lại (ARCHIVED sang PUBLISHED) | `HERITAGE_PUBLISHED` | `{ status }` → `{ status, name, slug }` |
| Ẩn (PUBLISHED sang ARCHIVED) | `HERITAGE_ARCHIVED` | `{ status }` → `{ status, name, slug }` |

Việc tạo, sửa nháp, thêm bớt giai đoạn, sự kiện, ảnh, nguồn, xóa mềm **không** ghi nhật ký (không có mã hành động tương ứng trong danh mục); mốc
`heritages.updated_at` được cập nhật mỗi khi di sản hoặc phần con của nó đổi. Lần xuất bản ghi lại người xuất bản, đủ để đối chiếu với quy trình
"người kiểm khác người nhập". Ghi nhật ký cùng giao dịch với đổi trạng thái: không có bản ghi thì trạng thái cũng không đổi.

## 6. Thông báo lỗi

Chữ tiếng Việt theo danh mục thông báo của nhóm (`Messages`), mã theo nghĩa (`MessageCodes`):

| Tình huống | Mã | Ví dụ |
|---|---|---|
| Bỏ trống trường bắt buộc | `REQUIRED` | "Vui lòng nhập tên di sản." |
| Sai định dạng, vượt độ dài, tọa độ ngoài phạm vi | `INVALID` | "Vĩ độ không hợp lệ: phải từ 8,0 đến 23,5." |
| Trùng đường dẫn di sản, đường dẫn ảnh | `DUPLICATE` (409) | "Đường dẫn không hợp lệ: đã có di sản khác dùng đường dẫn này." |
| Thao tác bị quy tắc chặn, hoặc không tìm thấy | `BLOCKED` (422, 404) | "Không thể xóa di sản. Di sản đang xuất bản. Hãy ẩn di sản trước." |
| Xuất bản, hiện lại, lưu di sản đang xuất bản khi chưa đủ điều kiện | `NOT_READY` (422) | "Chưa đủ điều kiện xuất bản di sản. Còn thiếu: mô tả ngắn, nguồn tham chiếu (ít nhất một)." |

Lỗi theo trường nằm trong `errors` (khóa camelCase như JSON gửi lên; với `NOT_READY` khóa là tên trường còn thiếu ở mục 2).

## 7. Dữ liệu nạp đợt 1 (thay đổi ở luồng này)

- Lần nạp di sản đợt 1 (`HeritageDataSeeder`) từ nay **luôn ghi di sản ở DRAFT**, bỏ qua `status` trong tệp: di sản chỉ lên PUBLISHED qua chức năng xuất bản sau khi người khác
  người nhập đã kiểm. Kiểm thử trên SQL Server xác nhận cả 20 di sản đợt 1 đều đủ điều kiện xuất bản.
- Loại nguồn `GOV_WEBSITE` và `UNESCO` của tệp dữ liệu được quy đổi về `WEBSITE` khi nạp (cơ quan đã ghi trong tiêu đề).
- Migration **`ChuanHoaLoaiNguonThamChieu`** chỉ sửa dữ liệu, không đổi lược đồ: đổi hai loại nguồn cũ trên về `WEBSITE` ở database đã nạp từ trước để mọi nguồn hiện có sửa
  và kiểm được theo quy tắc mới. Migration cũ không sửa. Database `sep490_dsvhvn_v3_dev` đã áp migration này; 20 di sản đã nạp từ trước vẫn giữ trạng thái PUBLISHED
  (chỉ database mới nạp lần đầu mới bắt đầu bằng DRAFT).

## 8. Kiểm thử

| Nhóm | Nội dung |
|---|---|
| `Unit/HeritageRulesTests` | Sinh đường dẫn, số từ, tọa độ, ngày có thật (năm nhuận, năm âm), so sánh thời điểm kết thúc, giấy phép được dùng, ghép và tách ghi công, từng điều kiện xuất bản |
| `Integration/HeritageApiTests` (SQLite trong bộ nhớ, chạy cả Api) | Chỉ ADMIN dùng được; tạo nháp và đường dẫn; kiểm dữ liệu từng trường; xuất bản thiếu điều kiện không đổi gì; vòng ẩn, hiện lại; nhật ký ghi đúng người, IP, mã hành động; sửa di sản đang xuất bản; timeline và sự kiện (năm âm, thứ tự, xóa kéo theo); ảnh và ghi công; ảnh đại diện; nguồn; xóa mềm; danh sách, tìm không dấu, lọc, phân trang |
| `Integration/HeritageSqlServerTests` (database tạm của LocalDB, tự xóa) | 20 di sản đợt 1 nạp vào DRAFT, loại nguồn đã quy đổi, xuất bản được cả 20 và nhật ký đủ 20 dòng; tìm có dấu, không dấu, "thang long" theo collation `Vietnamese_CI_AI`; hai yêu cầu tranh một đường dẫn: một thành công, một 409 (không phải lỗi hệ thống); năm âm lưu đúng; thứ tự sự kiện của dữ liệu thật |

```bash
dotnet test --filter "FullyQualifiedName~Heritage"
```

Thử nhanh bằng Swagger (`http://localhost:5080/swagger`, chỉ Development): đăng nhập bằng `POST /api/v1/auth/login` với tài khoản ADMIN seed, dán access token vào nút Authorize.
