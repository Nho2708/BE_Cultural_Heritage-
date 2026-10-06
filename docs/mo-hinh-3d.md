# Luồng Mô hình 3D di sản (Backend)

Phần backend của việc đưa **mô hình 3D giấy phép mở** vào di sản. Quản trị hệ thống khai báo mô hình ở tab "Mô hình 3D" của màn hình Biên tập di sản: tải tệp `.glb` lên kho tệp (hoặc dán
đường dẫn của tệp đã nằm trong kho), điền ghi công đủ ba phần (tác giả · giấy phép mở · nguồn), rồi bật hiển thị. Khách tham quan và học sinh thấy nút "Xem 3D" ở trang chi tiết di sản khi di sản có
mô hình đang hiển thị, và mở **trình xem 3D dùng chung** của web học tập và app. **Xem 3D miễn phí, không cần quảng cáo, không cần đăng nhập.** Backend chỉ cấp dữ liệu (đường dẫn tệp, dung lượng,
ghi công); việc xoay, phóng to, đặt lại góc nhìn và thanh tiến trình là của trình xem ở giao diện.

| Mục | Giá trị |
|---|---|
| Chức năng | Khai báo mô hình 3D di sản (quản trị hệ thống) · Xem mô hình 3D di sản (khách tham quan, học sinh) |
| Màn hình phục vụ | Web quản trị: tab Mô hình 3D của Biên tập di sản, cột và bộ lọc "có mô hình 3D" ở Quản lý di sản. Web học tập và app: nút "Xem 3D" ở Chi tiết di sản, màn hình Xem mô hình 3D (toàn màn hình), trang xem trước bản nháp |
| Bảng (CSDL v4, migration `ThemMoHinh3DCuaDiSan`) | `heritage_models` (mới); ghi `audit_logs`; đọc `heritages`. Tệp nằm trong kho tệp `IFileStorage` (bản `Local` hoặc `Cloudinary`, loại raw) |
| Lớp mới | `Domain/Heritages/HeritageModel`, `Domain/Rules/HeritageModelRules`, enum `ModelFormat`; `Application/Models/` (`HeritageModelService`, `PublicModelService`, validator, DTO, `ModelMessages`); `Api/Controllers/AdminHeritageModelsController`; cấu hình EF `HeritageModelConfiguration`; thêm `SizeOfAsync` vào `IFileStorage` |
| Nguồn | `doc/plan/spec` (use case Xem mô hình 3D di sản và Khai báo mô hình 3D di sản, màn hình Xem mô hình 3D và tab Mô hình 3D, quy tắc Mô hình 3D di sản, thông báo không tải được 3D) · `doc/database/dsvhvn-v4.dbml` |

## 1. Quy tắc

| Quy tắc | Nội dung |
|---|---|
| Định dạng | Chỉ **`.glb`** (glTF 2.0 đóng gói một tệp); `.gltf` nhiều tệp và mọi định dạng khác bị từ chối. Kiểm **bằng nội dung** (đúng dấu đầu tệp, độ dài khớp, phiên bản 2.0), không tin đuôi tệp hay loại nội dung khai báo. Cột `file_format` chỉ nhận `GLB` (CHECK) |
| Dung lượng | Tối đa **15 MB** mỗi mô hình; `file_size_bytes > 0` (CHECK). Dung lượng do **máy chủ đo** (từ tệp vừa tải, hoặc hỏi kho tệp khi khai báo bằng đường dẫn), không nhận số do người gửi khai |
| Không tham chiếu ngoài | Kết cấu và dữ liệu phải nằm trong tệp: `.glb` có `uri` trỏ ra ngoài (trừ `data:` nhúng) bị từ chối. glTF không chứa mã chạy |
| Chỉ tệp trong kho | Dán đường dẫn thì phải là đường dẫn của một tệp `.glb` **đang có trong chính kho tệp này** (`/files/models/{mã}.glb`, hoặc địa chỉ đủ của kho: `Storage:PublicBaseUrl`, hoặc `https://res.cloudinary.com/{tên}/raw/upload/{thư mục}/models/{mã}.glb` của đúng tài khoản và thư mục đang cấu hình). Trang ngoài, kho khác, tệp không tồn tại bị từ chối, để mô hình không biến mất hay đổi giấy phép. Địa chỉ lưu vào CSDL là **địa chỉ chuẩn của kho** |
| Ghi công bắt buộc | Đủ ba phần **tác giả · giấy phép · đường dẫn nguồn**. Dòng ghi công (`credit`) do hệ thống ghép thành `tác giả · giấy phép · nguồn`, hiện ngay trong trình xem ở mọi ngôn ngữ và **không dịch**. Tác giả và giấy phép không chứa dấu "·" |
| Giấy phép mở | Chỉ **CC0, CC BY, CC BY-SA** (kèm số phiên bản, vd "CC BY 4.0"), **phạm vi công cộng** (public domain) hoặc **mô hình nhóm tự dựng**. Không nhận CC BY-NC, CC BY-ND, giấy phép không rõ. Mô hình nhóm tự dựng được **bỏ trống đường dẫn nguồn**, khi đó phần nguồn của ghi công là "Nhóm thực hiện"; mọi giấy phép khác bắt buộc có đường dẫn nguồn http hoặc https |
| Một mô hình hiển thị | Mỗi di sản có tối đa **một** mô hình đang hiển thị (`PUBLISHED`, chưa gỡ), do **chỉ mục duy nhất có lọc** `UX_heritage_models_visible` của chính CSDL bảo vệ, kể cả khi hai quản trị bấm cùng lúc (người đến sau nhận 422). Mô hình nháp, đã ẩn hoặc đã gỡ không chiếm chỗ. Mục tiêu 5-10 di sản có mô hình là mục tiêu dữ liệu, không cưỡng chế |
| Hiển thị cho khách | Mô hình chỉ tới khách khi **mô hình đang hiển thị và di sản đã xuất bản**. Di sản bị ẩn thì mô hình ẩn theo (trạng thái của mô hình giữ nguyên, di sản hiện lại thì mô hình hiện lại). Di sản không có mô hình hiển thị thì không có nút "Xem 3D", chỉ có bộ ảnh |
| Miễn phí | Không qua cổng quảng cáo, không đăng nhập; học sinh và khách đều xem. Khách gửi kèm `X-Device-Key` thì lượt xem 3D được ghi vào lịch sử của thiết bị (chỉ khi có mô hình, học sinh đăng nhập không bị ghi): [khach-tham-quan.md](khach-tham-quan.md) |
| Nhật ký thao tác | Thêm, thay tệp, sửa thông tin và ghi công, hiện, ẩn, gỡ đều ghi mã `HERITAGE_MODEL_CHANGED` (nhãn "Thêm, thay, gỡ, ẩn hoặc hiện mô hình 3D của di sản", đối tượng `heritage_models`). `new_value` có việc đã làm (`added`, `replaced`, `updated`, `shown`, `hidden`, `removed`), mã di sản, trạng thái, giấy phép, dung lượng; **không ghi đường dẫn tệp**. Yêu cầu thất bại không ghi gì |
| Gỡ | Xóa mềm (`deleted_at`), tệp trong kho **giữ lại** (kể cả tệp cũ khi thay tệp) |

## 2. Dữ liệu

Bảng `heritage_models` (17 cột; tên cột, kiểu, CHECK theo `dsvhvn-v4.dbml`, thêm hai chỉ mục):

| Cột | Ghi chú |
|---|---|
| `id`, `heritage_id` | khóa ngoại tới `heritages`, **Restrict** (di sản có mô hình không xóa cứng được) |
| `title` | tên mô hình (tiếng Việt, không dịch), tối đa 255 |
| `file_url` | `nvarchar(500)`, địa chỉ chuẩn của tệp trong kho |
| `file_format` | `GLB` (CHECK `CK_heritage_models_file_format`) |
| `file_size_bytes` | `bigint`, `> 0` (CHECK `CK_heritage_models_file_size`) |
| `poster_url` | ảnh xem trước trong lúc tải: đường dẫn http, https hoặc ảnh đã tải lên kho |
| `license_name`, `license_url`, `author`, `source_url`, `credit` | ghi công; `credit` ≤ 500 ký tự |
| `status` | `DRAFT` (chưa hiện), `PUBLISHED` (hiện), `ARCHIVED` (ẩn) (CHECK `CK_heritage_models_status`) |
| `sort_order`, `created_at`, `updated_at`, `deleted_at` | thứ tự khi có nhiều mô hình; mốc thời gian; xóa mềm |

Chỉ mục: `UX_heritage_models_visible` (duy nhất trên `heritage_id`, lọc `status = 'PUBLISHED' AND deleted_at IS NULL`), `IX_heritage_models_heritage_status` (`heritage_id`, `status`).
Migration `ThemMoHinh3DCuaDiSan` chỉ **thêm** bảng nên chạy được trên CSDL có dữ liệu. Số liệu sau migration trên SQL Server: **34 bảng**, 320 cột, 53 khóa ngoại, 38 CHECK,
5 UNIQUE có lọc (thêm `UX_heritage_models_visible`), 27 cột enum `nvarchar(20)` (xem [co-so-du-lieu.md](co-so-du-lieu.md) mục 8).

## 3. API quản trị (tiền tố `/api/v1/admin`, chỉ quản trị hệ thống; 401 chưa đăng nhập, 403 sai vai trò)

| Method | Đường dẫn | Nội dung | Thành công | Lỗi |
|---|---|---|---|---|
| GET | `/heritages/{heritageId}/models` | mô hình chưa gỡ của di sản, đang hiển thị trước | 200 `HeritageModelDto[]` | 404 |
| POST | `/heritages/{heritageId}/models` | JSON: `title`, `fileUrl` (tệp đã có trong kho), `posterUrl`, `author`, `license`, `licenseUrl`, `sourceUrl`, `sortOrder`, `show` | 201 `HeritageModelDto` | 400 · 404 · 422 |
| POST | `/heritages/{heritageId}/models/upload` | multipart: `file` (`.glb`) và các trường trên (trừ `fileUrl`) | 201 | 400 · 404 · 413 · 422 · 503 |
| PUT | `/models/{modelId}` | JSON như trên, **thay toàn bộ**; có `fileUrl` thì thay tệp bằng tệp khác trong kho, bỏ trống thì giữ tệp (không có `show`: dùng hiện/ẩn) | 200 | 400 · 404 |
| PUT | `/models/{modelId}/file` | multipart: `file`; thay tệp, giữ ghi công; tệp cũ vẫn nằm trong kho | 200 | 400 · 404 · 413 · 503 |
| POST | `/models/{modelId}/show` | hiện (từ nháp hoặc đã ẩn) | 200 | 404 · 422 |
| POST | `/models/{modelId}/hide` | ẩn mô hình đang hiển thị | 200 | 404 · 422 |
| DELETE | `/models/{modelId}` | gỡ (xóa mềm) | 200 | 404 |
| POST | `/files/models` | (đã có) tải riêng một tệp `.glb` vào kho, trả `url`, `size`; sau đó khai báo bằng `fileUrl` | 201 | 400 · 413 |
| GET | `/heritages?hasModel=true\|false` | danh sách di sản có thêm `hasModel` mỗi dòng và bộ lọc `hasModel` (mô hình đang hiển thị) | 200 | |

`HeritageModelDto`: `id`, `heritageId`, `title`, `fileUrl`, `fileFormat`, `fileSizeBytes`, `posterUrl`, `license`, `licenseUrl`, `author`, `sourceUrl`, `credit`, `status`,
`statusLabel` (Nháp, Đang hiển thị, Đã ẩn), **`isVisibleToPublic`** (mô hình đang hiển thị **và** di sản đã xuất bản), `sortOrder`, `createdAt`, `updatedAt`.

Lỗi: dữ liệu sai (giấy phép hạn chế, thiếu tác giả hoặc nguồn, đường dẫn ngoài kho, tệp không phải `.glb`, quá 15 MB) → 400 `INVALID` hoặc `REQUIRED` kèm lỗi theo trường (`file`, `fileUrl`, `author`,
`license`, `licenseUrl`, `sourceUrl`, `posterUrl`); hiện mô hình khi di sản đã có mô hình khác hiển thị, hiện mô hình đang hiện, ẩn mô hình không hiển thị → 422 `BLOCKED`; kho tệp (Cloudinary) lỗi →
503 `STORAGE_UNAVAILABLE`, không ghi dòng nào. Khai báo kèm `show: true` mà đã có mô hình khác hiển thị thì báo 422 **trước khi nhận tệp**. Kiểm thông tin trước khi lưu tệp; lưu tệp rồi ghi dòng
không được thì tệp vừa lưu bị gỡ lại.

## 4. API công khai (không cần đăng nhập; giới hạn 300 yêu cầu mỗi phút mỗi IP)

| Method | Đường dẫn | Nội dung |
|---|---|---|
| GET | `/api/v1/public/heritages/{slug}` | chi tiết di sản có thêm cờ **`hasModel`** để bật nút "Xem 3D" |
| GET | `/api/v1/public/heritages/{slug}/model` | dữ liệu trình xem: `id`, `title`, `fileUrl`, `contentType` (`model/gltf-binary`), `fileSizeBytes` (để cảnh báo trước khi tải trên mạng di động), `posterUrl`, `author`, `license`, `licenseUrl`, `sourceUrl`, `credit` |
| GET | `/api/v1/preview/heritages/{mã}/model` | như trên cho bản nháp (trang xem trước; chỉ quản trị hệ thống đang đăng nhập kèm mã còn hạn, cùng điều kiện và cùng 404 với trang xem trước); chi tiết xem trước có `hasModel` |
| GET | `/files/models/{mã}.glb` | tệp (bản `Local`): `model/gltf-binary`, không cần đăng nhập, lưu đệm dài hạn |

Di sản không có mô hình hiển thị, mô hình vừa bị ẩn, di sản chưa xuất bản hoặc đã ẩn → **404 với mã `MODEL_UNAVAILABLE`** và câu "Không tải được mô hình 3D. Bạn có thể xem bộ ảnh của di sản."
(`ModelMessages.ViewerFallback`). Trình xem hiện đúng câu này (qua tệp chuỗi 5 ngôn ngữ, theo mã) và quay về bộ ảnh khi gặp 404 hoặc khi tải tệp lỗi hoặc thiết bị không hỗ trợ. Slug sai dạng, không có
hoặc của di sản chưa xuất bản trả 404 `BLOCKED` như các API công khai khác (không lộ di sản có tồn tại hay không).

## 5. Dùng với Cloudinary

Mô hình tải lên loại **raw** với public id `{thư mục}/models/{mã}.glb`; địa chỉ lưu và trả cho khách là URL HTTPS của Cloudinary. Dung lượng khi dán đường dẫn do Cloudinary báo (`bytes`). Cloudinary lỗi
khi tải hoặc khi hỏi dung lượng thì 503 và không ghi gì. Gói miễn phí giới hạn dung lượng tệp raw, `.glb` gần 15 MB có thể bị Cloudinary từ chối (khi đó người dùng thấy thông báo tải lại). Web học tập
và app nếu có chính sách bảo mật nội dung (CSP) thì thêm `https://res.cloudinary.com` vào nguồn tải mô hình.

## 6. Kiểm thử

| Test | Nội dung |
|---|---|
| `Unit/HeritageModelRulesTests` | giấy phép mở và hạn chế (CC BY-NC, CC BY-ND, lẫn chữ), mô hình nhóm tự dựng, ghép dòng ghi công, nhận đúng khóa tệp từ đường dẫn của kho (cục bộ, có địa chỉ máy chủ, Cloudinary raw) và từ chối trang ngoài, thư mục lạ, thoát đường dẫn |
| `Unit/HeritageModelServiceTests` (SQLite trong bộ nhớ, kho tệp giả) | khai báo bằng đường dẫn đo dung lượng từ kho; đường dẫn ngoài kho hoặc tệp không có bị từ chối; chỉ quản trị hệ thống; tệp hoặc ghi công sai thì chưa lưu gì; kho lỗi không để lại dòng; mô hình khác hiển thị chen vào giữa chừng thì tệp mới bị gỡ, không để lại dòng hay nhật ký; thay tệp giữ tệp cũ; gỡ là xóa mềm giữ tệp và trả chỗ; cờ hiển thị theo trạng thái di sản |
| `Unit/CloudinaryFileStorageTests` | thêm: `SizeOfAsync` đọc `bytes`, không có tệp thì null, lỗi thì lỗi kho tệp |
| `Integration/HeritageModelApiTests` (SQLite trong bộ nhớ, chạy cả Api) | quyền (chưa đăng nhập 401, quản trị trường và giáo viên 403, khách đọc được); tải lên và khai báo, tệp phục vụ công khai; tệp sai, quá 15 MB, giấy phép hạn chế, thiếu ghi công, mọi giấy phép mở; nhóm tự dựng; khai báo bằng đường dẫn (số do khách gửi không được tin); đường dẫn ngoài kho; một mô hình hiển thị, ẩn, gỡ trả chỗ; hiển thị theo trạng thái di sản (ẩn, hiện lại, xuất bản sau); sửa ghi công; thay tệp bằng tải lên hoặc đường dẫn; gỡ giữ tệp; danh sách và bộ lọc `hasModel`; xem trước bản nháp; nhật ký thao tác không có đường dẫn; thứ tự danh sách |
| `Integration/HeritageModelCloudinaryApiTests` (Api thật, HTTP giả của Cloudinary) | tải raw và địa chỉ HTTPS; đường dẫn dán phải đúng đám mây, thư mục và loại raw; dung lượng từ Cloudinary; Cloudinary lỗi thì 503 và không ghi gì |
| `Integration/HeritageModelSqlServerTests` (database tạm của LocalDB) | luồng tải, hiện, đọc, lọc trên SQL Server, chữ tiếng Việt và dấu "·" nguyên vẹn; CHECK, chỉ mục duy nhất có lọc, khóa ngoại Restrict do chính CSDL từ chối; **hai quản trị hiện hai mô hình cùng lúc chỉ một thành công** (3 vòng); hai lần tải kèm hiện cùng lúc không để lại tệp thừa |
| `Unit/AppDbContextTests`, `Integration/SqlServerDatabaseTests` | cập nhật số liệu lược đồ: 34 bảng, 320 cột, 53 khóa ngoại, 38 CHECK, chỉ mục lọc mới |

Smoke test trên Api chạy thật: `scripts/smoke-mo-hinh-3d.sh` (cách chạy ở đầu tệp; nên dùng database tạm, tên bắt đầu `dsvhvn_tam_a2_`); 66 bước gồm quyền, tệp và ghi công bị từ chối, tải và hiện,
một mô hình hiển thị, sửa, thay tệp, khai báo bằng đường dẫn, mô hình đi theo di sản, gỡ và nhật ký.

## 7. Chưa làm ở luồng này

- Ghi lượt xem 3D vào lịch sử của khách và gợi ý theo lượt xem 3D: **đã làm** ở luồng Khách tham quan ([khach-tham-quan.md](khach-tham-quan.md)); `GET /api/v1/public/heritages/{slug}/model` tự ghi khi có `X-Device-Key`, trình xem không cần gọi API riêng.
- Trình xem 3D, nút "Xem 3D", tab Mô hình 3D, xem trước trong trình xem dùng chung: giao diện web quản trị, web học tập và app.
- Dữ liệu 5-10 mô hình thật (tìm và nhập glTF giấy phép mở): việc nhập liệu của nhóm qua tab Mô hình 3D.
