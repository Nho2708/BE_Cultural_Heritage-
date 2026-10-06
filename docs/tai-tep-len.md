# Tải tệp lên: ảnh di sản, ảnh sự kiện, mô hình 3D (Backend)

Quản trị hệ thống tải **ảnh** lên thư viện của di sản và của sự kiện, và tải **mô hình 3D** (`.glb`) lên kho tệp, thay vì chỉ dán đường dẫn ảnh ngoài.
Dán đường dẫn http/https vẫn dùng được như cũ ([kho-di-san.md](kho-di-san.md), mục Ảnh và media). **Không thêm bảng hay cột**: ảnh tải lên là một dòng `media`
như mọi ảnh khác, cột `url` giữ đường dẫn tới tệp trong kho.

## 1. Kho tệp thay được nhà cung cấp

Nghiệp vụ chỉ biết giao diện `IFileStorage` (`Application/Files`): ghi tệp, kiểm có tệp, gỡ một lần ghi chưa kịp ghi nhận, và dựng đường dẫn công khai.
Nhà cung cấp chọn bằng cấu hình `Storage:Provider`:

| Giá trị | Trạng thái | Ghi chú |
|---|---|---|
| `Local` (mặc định) | Có | Lưu trên đĩa của máy chủ, trong thư mục `Storage:Local:RootPath`; Api phục vụ tệp qua đường dẫn `/files/...` |
| `Cloud` | **Chỗ để sẵn, chưa làm** | Chọn giá trị này thì Api dừng ngay lúc khởi động với thông báo "Storage:Provider = Cloud chưa có bản cài đặt, hãy dùng Local". Khi có dịch vụ đám mây chỉ cần viết một lớp `IFileStorage` mới và đăng ký trong `Infrastructure/DependencyInjection` |

### Cấu hình

```json
"Storage": {
  "Provider": "Local",
  "PublicBaseUrl": "",
  "Local": { "RootPath": "storage" }
}
```

- `Local:RootPath`: thư mục chứa tệp. Đường dẫn tương đối tính từ thư mục chạy của Api (`src/DSVHVN.Api/storage` khi chạy bằng `dotnet run`). Nên đặt **ngoài mã nguồn**
  (ví dụ `D:\dsvhvn-tep` hoặc `/var/lib/dsvhvn/tep`), cấp quyền ghi cho tài khoản chạy Api và đưa vào lịch sao lưu. Thư mục tự được tạo. Đặt bằng biến môi trường:
  `Storage__Local__RootPath=D:\dsvhvn-tep`. Thư mục `storage` mặc định đã nằm trong `.gitignore`; không đưa tệp tải lên vào git.
- `PublicBaseUrl`: để trống thì đường dẫn trả về là **tương đối** (`/files/images/...`), giao diện ghép với địa chỉ API. Đặt địa chỉ đầy đủ (ví dụ `https://api.dsvhvn.vn`)
  khi web học tập và app mobile ở địa chỉ khác máy chủ API, để đường dẫn ảnh lưu trong CSDL mở được ở mọi nơi. Đổi giá trị này về sau **không** sửa đường dẫn của ảnh đã lưu.
- Tệp được phục vụ qua `GET /files/...` **không cần đăng nhập** (ảnh của trang công khai phải mở được), nhưng có kiểm soát: chỉ đuôi `jpg`, `png`, `webp`, `glb`; tệp lạ, thư mục
  và đường dẫn thoát khỏi thư mục kho đều không được phục vụ; tên tệp là mã ngẫu nhiên nên cho trình duyệt giữ lâu (`Cache-Control: public, max-age=31536000, immutable`).
- Đứng sau proxy ngược (nginx, IIS): chuyển tiếp `/files` về Api, hoặc cho proxy đọc thẳng thư mục kho nếu muốn nhanh hơn.

## 2. Endpoint (chỉ quản trị hệ thống; multipart/form-data)

| Method | Đường dẫn | Trường gửi | Thành công | Lỗi chính |
|---|---|---|---|---|
| POST | `/api/v1/admin/heritages/{heritageId}/media/upload` | `file`, `author`, `license`, `source`, `caption?`, `sortOrder?`, `setAsThumbnail?` | 201 `Media` (thêm `previewUrl`) | 400 (tệp hoặc ghi công) · 401 · 403 · 404 · 413 |
| POST | `/api/v1/admin/events/{eventId}/media/upload` | như trên; `setAsThumbnail` không dùng được | 201 `Media` | như trên |
| POST | `/api/v1/admin/files/models` | `file` (`.glb`) | 201 `{ url, previewUrl: null, size, contentType }` | 400 · 401 · 403 · 413 |
| GET | `/files/images/{mã}.jpg` `.png` `.webp`, `/files/images/{mã}-thumb.webp`, `/files/models/{mã}.glb` | — | 200, không cần đăng nhập | 404 |

`Media` trả `url` (ảnh đã xử lý) và `previewUrl` (ảnh thu nhỏ; `null` với ảnh dán đường dẫn ngoài). Chi tiết di sản ở web quản trị và chi tiết di sản công khai đều có `previewUrl`.
Tải ảnh ghi một dòng `media` rồi mới trả về; ảnh đại diện chọn được ngay bằng `setAsThumbnail=true`.

## 3. Quy tắc

**Ảnh** (di sản, sự kiện)

- Chỉ **JPG, PNG, WEBP**, tối đa **5 MB**. Định dạng xác định bằng **nội dung tệp** (đọc đầu tệp và giải mã thật), **không** theo đuôi tên hay loại nội dung khai báo: PNG đặt tên `.jpg`
  vẫn được nhận và lưu là `.png`; GIF, tệp văn bản, chương trình đổi đuôi thành `.jpg` bị từ chối; ảnh bị cắt dở hoặc hỏng bị từ chối.
- Tối đa 30 triệu điểm ảnh (chặn ảnh nhỏ về dung lượng nhưng giải ra chiếm bộ nhớ lớn).
- **Bỏ dữ liệu EXIF**: ảnh được giải mã rồi mã hóa lại nên vị trí GPS, thiết bị, thời điểm chụp, ghi chú nhúng không còn trong bản lưu. Chiều xoay ghi trong EXIF được áp dụng vào điểm ảnh
  trước khi bỏ, nên ảnh chụp dọc từ điện thoại vẫn đứng đúng. Đổi lại ảnh JPG và WEBP được nén lại một lần (chất lượng 90).
- Sinh **ảnh thu nhỏ** WEBP, cạnh dài tối đa 320 điểm ảnh (không phóng to ảnh nhỏ), lưu cùng nơi với đuôi `-thumb.webp`.
- **Tên tệp ngẫu nhiên** (mã 32 ký tự), không bao giờ dùng tên người tải đặt; kho chỉ nhận đúng các dạng tên do hệ thống sinh nên không thể ghi ra ngoài thư mục kho.
- **Ghi công vẫn bắt buộc** như thêm bằng đường dẫn: tác giả, giấy phép (CC0, CC BY, CC BY-SA, phạm vi công cộng hoặc ảnh nhóm tự chụp), nguồn. Thiếu hoặc sai thì từ chối trước khi lưu tệp.
- Thứ tự xử lý: kiểm ghi công và tìm di sản/sự kiện (404) → kiểm tệp → lưu hai tệp → ghi dòng `media`. Ghi dòng lỗi thì hai tệp vừa lưu được gỡ lại, không để tệp thừa.
- **Xóa ảnh khỏi di sản chỉ gỡ dòng `media`; tệp trong kho được giữ lại** (xóa mềm, không có thao tác xóa tệp). Chưa có việc dọn tệp mồ côi; khi cần, đối chiếu tên tệp trong kho với cột
  `media.url` rồi dọn có chủ đích.
- Thư viện ảnh cho bài học và thẻ hình của câu hỏi vẫn lấy từ kho di sản như cũ; ảnh tải lên dùng được ở đó ngay vì là ảnh của di sản, có ghi công.

**Mô hình 3D**

- Chỉ **`.glb`** (glTF 2.0 đóng gói một tệp), tối đa **15 MB**, kiểm bằng nội dung: đúng dấu đầu tệp, độ dài khớp, phiên bản 2.0, **không tham chiếu tài nguyên ngoài** (kết cấu và dữ liệu phải
  nằm trong tệp; dữ liệu nhúng dạng `data:` được chấp nhận). `.gltf` nhiều tệp và các định dạng khác bị từ chối.
- Endpoint này **chỉ lưu tệp và trả đường dẫn trong kho**. Gắn mô hình vào di sản kèm ghi công đủ ba phần, giấy phép mở, bật/tắt hiển thị, ghi nhật ký thao tác và trình xem 3D thuộc luồng
  Xem 3D (bảng mô hình 3D của CSDL v4), luồng đó sẽ dùng đường dẫn này.

**Giới hạn yêu cầu**: thân yêu cầu tối đa 5,5 MB (ảnh) hoặc 15,5 MB (mô hình); vượt quá thì 413 với khuôn phản hồi chung. Tệp quá dung lượng nhưng vẫn trong giới hạn thân yêu cầu bị từ chối ở mức
dịch vụ với 400 và thông báo "Tệp ảnh không hợp lệ: tối đa 5 MB." (khóa lỗi `file`).

## 4. Thông báo lỗi (khóa `file`)

| Tình huống | Mã | Thông báo |
|---|---|---|
| Không gửi tệp | `REQUIRED` | "Vui lòng nhập tệp ảnh." / "Vui lòng nhập tệp mô hình 3D." |
| Quá dung lượng | `INVALID` | "Tệp ảnh không hợp lệ: tối đa 5 MB." / "Tệp mô hình 3D không hợp lệ: tối đa 15 MB." |
| Sai định dạng (theo nội dung) | `INVALID` | "Tệp ảnh không hợp lệ: chỉ nhận định dạng JPG, PNG hoặc WEBP." |
| Không đọc được, ảnh hỏng | `INVALID` | "Tệp ảnh không hợp lệ: không đọc được, tệp không phải ảnh hoặc đã bị hỏng." |
| Quá nhiều điểm ảnh | `INVALID` | "Tệp ảnh không hợp lệ: có quá nhiều điểm ảnh (tối đa 30 triệu điểm ảnh)." |
| Mô hình sai loại, sai phiên bản, tham chiếu ngoài | `INVALID` | "Tệp mô hình 3D không hợp lệ: chỉ nhận tệp .glb …" · "chỉ nhận glTF 2.0" · "không được tham chiếu tệp ngoài …" |

## 5. Thư viện và giấy phép

Xử lý ảnh dùng **SkiaSharp** (giấy phép MIT, dùng tự do cho sản phẩm bán cho trường), kèm gói thư viện gốc cho Linux (`SkiaSharp.NativeAssets.Linux.NoDependencies`) để chạy trên máy chủ
Linux không cần cài thêm gói hệ thống; trên Windows thư viện gốc đi kèm sẵn. Đã cân nhắc ImageSharp nhưng giấy phép chia đôi của nó đòi mua giấy phép thương mại khi bán sản phẩm.

## 6. Kiểm thử

| Nhóm | Nội dung |
|---|---|
| `Unit/StoredFileRulesTests` | Chỉ nhận khóa tệp do hệ thống sinh (chặn `..`, đường dẫn tuyệt đối, đuôi lạ); đường dẫn tương đối tới tệp; đường dẫn ảnh thu nhỏ; giới hạn 5 MB và 15 MB |
| `Unit/SkiaImageProcessorTests` | Nhận JPG, PNG, WEBP theo nội dung; từ chối GIF, văn bản, chương trình, ảnh cụt; chặn quá nhiều điểm ảnh; **EXIF bị bỏ** (tệp mẫu mang chuỗi GPS dễ nhận ra); chiều xoay EXIF áp vào điểm ảnh (xoay 90, 180, lật); ảnh thu nhỏ WEBP không phóng to |
| `Unit/GlbInspectorTests` | `.glb` hợp lệ; `data:` nhúng được; mọi tham chiếu ra ngoài bị từ chối; sai phiên bản; tệp hỏng, `.gltf` JSON thuần, độ dài không khớp |
| `Unit/LocalFileStorageTests`, `StorageOptionsValidatorTests` | Ghi, kiểm, gỡ; khóa có thể thoát thư mục bị từ chối; đường dẫn công khai tương đối hoặc đủ địa chỉ; `Cloud` và địa chỉ gốc sai bị từ chối |
| `Unit/HeritageMediaUploadTests` | Tải xong có đủ hai tệp và một dòng; lỗi ghi ở tệp thứ nhất hoặc thứ hai thì không còn tệp nào và không có dòng; xóa ảnh không đụng tới tệp; tệp bị từ chối thì chưa lưu gì; mô hình lưu dưới tên sinh ra |
| `Integration/FileUploadApiTests` (SQLite trong bộ nhớ, chạy cả Api) | Chỉ ADMIN; ảnh tải lên không còn EXIF và phục vụ công khai kèm ảnh thu nhỏ; định dạng theo nội dung; quá 5 MB, thiếu tệp, đúng 5 MB; ghi công bắt buộc và không lưu gì khi bị từ chối; ảnh đại diện; ảnh sự kiện; 404; xóa ảnh giữ tệp; dán đường dẫn tệp trong kho; `.glb` hợp lệ và các kiểu bị từ chối, quá 15 MB; phục vụ tệp có kiểm soát |
| `Integration/FileUploadSqlServerTests` (database tạm của LocalDB, tự xóa) | Dòng `media` ghi đúng bằng migration thật, ảnh đại diện, tệp và ảnh thu nhỏ mở được |

Các test dùng thư mục tạm riêng cho kho tệp (`Storage:Local:RootPath` đặt trong `ApiFactory`) và xóa khi xong; không chạm thư mục `storage` thật.

```bash
dotnet test --filter "FullyQualifiedName~FileUpload|FullyQualifiedName~StoredFile|FullyQualifiedName~SkiaImage|FullyQualifiedName~Glb|FullyQualifiedName~LocalFileStorage|FullyQualifiedName~HeritageMediaUpload"
```

Thử nhanh bằng curl (đăng nhập ADMIN lấy token, xem [kho-di-san.md](kho-di-san.md)):

```bash
curl -X POST http://localhost:5080/api/v1/admin/heritages/1/media/upload -H "Authorization: Bearer $TOKEN" \
  -F "file=@anh.jpg" -F "author=Nguyễn Văn An" -F "license=CC BY-SA 4.0" -F "source=Nhóm tự chụp" -F "setAsThumbnail=true"
```
