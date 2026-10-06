# BE_Cultural_Heritage- — Backend DSVHVN

Backend của **DSVHVN — nền tảng giáo dục di sản văn hóa cho trường học** (đồ án FA26SE171, FPT University HCM):
giáo viên tạo bài học và bài kiểm tra cho lớp từ dữ liệu của Cục Di sản văn hóa; học sinh có tài khoản (đăng nhập bằng mã học sinh
+ mật khẩu) ở web học tập và app mobile; gói dịch vụ bán cho trường. ASP.NET Core 9 (controllers) + EF Core 9 + SQL Server 2019.
Nội dung và thông điệp chỉ tiếng Việt.

Làm theo từng luồng (vertical slice): bảng của luồng → migration → API → UI → bổ sung cột bằng migration mới.
Lược đồ nguồn: `doc/database/dsvhvn-v3.dbml` (27 bảng); backend đã có **đủ 27 bảng** trong một migration khởi tạo, tên bảng,
tên cột giữ đúng DBML (`snake_case`). Phần CSDL v4 (`doc/database/dsvhvn-v4.dbml`) được thêm **dần theo từng luồng bằng migration mới** (hiện có bảng
`lesson_sections` và các cột mới của `ai_generations`, tổng 28 bảng) — xem [docs/co-so-du-lieu.md](docs/co-so-du-lieu.md).

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
| Ngân hàng câu hỏi theo dạng, AI sinh câu hỏi: ngân hàng **dùng chung mọi trường** với **8 dạng câu** (một đáp án đúng · Đúng/Sai · chọn hình đúng · ghép cặp · sắp xếp · điền từ · nhiều mệnh đề Đúng/Sai · đọc tư liệu) và **cấp phù hợp** theo bảng dạng câu theo cấp; trình soạn có kiểm hợp lệ theo dạng (lỗi theo trường), xem trước như học sinh của từng cấp, **chấm thử từng phần**; **AI sinh câu đúng dạng và cấp của lớp** từ một bài học (qua giao diện Dịch vụ AI + **bản giả lập**, ghi `ai_generations`, tính hạn mức lượt AI của gói), câu AI vào ngân hàng **chờ duyệt** chỉ người tạo thấy; duyệt, loại, duyệt nhiều câu, sửa, xóa mềm, **nhân bản** (câu đã nằm trong quiz hoặc của người khác chỉ đọc); tìm và lọc theo dạng, cấp, độ khó, di sản, trạng thái, người tạo; quản trị hệ thống **ẩn** câu kém chất lượng (ghi nhật ký thao tác) | Xong phần backend | [docs/ngan-hang-cau-hoi.md](docs/ngan-hang-cau-hoi.md) |

## Cấu trúc

```
src/DSVHVN.Domain          Thực thể 27 bảng của CSDL v3 và lesson_sections của v4, enum, quy tắc nghiệp vụ thuần (tài khoản, di sản, lớp và mã học sinh, khung bài học theo cấp, mã bài học, bảng dạng câu hỏi theo cấp)
src/DSVHVN.Application     Dịch vụ nghiệp vụ, kiểm gói và hạn mức, validator, danh mục thông điệp tiếng Việt, nhật ký thao tác
src/DSVHVN.Infrastructure  EF Core (DbContext, cấu hình theo nhóm bảng, migration, seed, nạp dữ liệu di sản), JWT,
                           băm mật khẩu PBKDF2, gửi thư (ghi log hoặc SMTP qua MailKit), đọc và dựng tệp danh sách học sinh (ClosedXML),
                           gọi Dịch vụ AI (bản giả lập hoặc HTTP tới dịch vụ nội bộ), bộ đếm nhập sai mã bài học,
                           kho tệp tải lên (lưu trên đĩa, xử lý ảnh bằng SkiaSharp)
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

# Tạo CSDL sep490_dsvhvn_v3_dev rồi nạp dữ liệu khởi tạo (gói Miễn phí, ADMIN, 34 tỉnh/thành, di sản) và thoát
dotnet tool run dotnet-ef database update --project src/DSVHVN.Infrastructure --startup-project src/DSVHVN.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/DSVHVN.Api --no-launch-profile -- --chi-khoi-tao-csdl

dotnet run --project src/DSVHVN.Api --launch-profile http > api.log 2>&1 &   # http://localhost:5080/swagger

dotnet test                                           # có LocalDB thì chạy thêm test trên database tạm
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-nen-tang.sh
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-lop-hoc-sinh.sh   # nên chạy trên database tạm, xem docs/lop-hoc-sinh.md mục 9
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-bai-hoc-theo-cap.sh # nên chạy trên database tạm, xem docs/bai-hoc-theo-cap.md mục 8
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-ngan-hang-cau-hoi.sh # nên chạy trên database tạm, xem docs/ngan-hang-cau-hoi.md mục 9
```

CSDL dev: `sep490_dsvhvn_v3_dev` trên `(localdb)\MSSQLLocalDB`, collation `Vietnamese_CI_AI` (không dùng các CSDL cũ `dsvhvn_dev`,
`sep490_dsvhvn_dev`, `sep490_dsvhvn_v2_dev`). Dùng CSDL khác thì đặt biến môi trường
`ConnectionStrings__Default` (cả cho `dotnet-ef` lẫn Api). Dữ liệu di sản đọc từ thư mục `doc/du-lieu-di-san` của nhóm nằm cạnh
repo (`Seed:HeritageData:*` trong `appsettings.Development.json`); máy không có thư mục này thì bước nạp di sản được bỏ qua.
Chi tiết endpoint, cấu hình và quyết định hiện thực ở [docs/nen-tang-to-chuc.md](docs/nen-tang-to-chuc.md).

## Dịch vụ AI soạn bài học và sinh câu hỏi

Dịch vụ AI thật (FastAPI) **chưa có**. Backend gọi AI qua giao diện theo hợp đồng của nhóm và có **bản giả lập** (sinh nội dung mẫu đúng cấp từ dữ liệu di sản, không gọi nhà cung cấp mô hình nào,
không dùng khóa API nào). Chọn bản bằng cấu hình `Ai:Mode`: `Simulated` (mặc định) hoặc `Remote` (gọi dịch vụ nội bộ ở `Ai:BaseUrl`, khóa nội bộ `Ai:InternalKey` đặt bằng biến môi trường
`Ai__InternalKey`, không ghi vào appsettings). Chờ 45 giây mỗi lần gọi, thử lại tối đa 2 lần (`Ai:AttemptTimeoutSeconds`, `Ai:MaxRetries`). Chi tiết hợp đồng, quy trình và hạn mức lượt AI:
[docs/bai-hoc-theo-cap.md](docs/bai-hoc-theo-cap.md) mục 5. Sinh câu hỏi theo dạng và cấp dùng chung mục cấu hình `Ai` (cùng chế độ, địa chỉ, khóa, thời gian chờ) qua giao diện `IQuestionAiClient`
(`POST /v1/generate-questions`) và có bản giả lập riêng: [docs/ngan-hang-cau-hoi.md](docs/ngan-hang-cau-hoi.md) mục 5.

## Kho tệp tải lên (ảnh, mô hình 3D)

Ảnh di sản, ảnh sự kiện và mô hình 3D (`.glb`) tải lên được lưu qua giao diện `IFileStorage`, chọn nhà cung cấp bằng `Storage:Provider`: `Local` (mặc định, lưu trên đĩa máy chủ) hoặc `Cloud`
(**chỗ để sẵn cho dịch vụ đám mây, chưa có bản cài đặt**; chọn thì Api dừng lúc khởi động và báo rõ). Bản `Local` lưu trong thư mục `Storage:Local:RootPath` (mặc định `storage`, tính từ thư mục chạy
của Api, đã nằm trong `.gitignore`) và phục vụ tệp qua đường dẫn `/files/...` không cần đăng nhập, chỉ cho đuôi `jpg`, `png`, `webp`, `glb`. Ngoài máy phát triển nên đặt thư mục **ngoài mã nguồn** và sao lưu nó:

```bash
export Storage__Local__RootPath=/var/lib/dsvhvn/tep         # Windows: set Storage__Local__RootPath=D:\dsvhvn-tep
export Storage__PublicBaseUrl=https://api.dsvhvn.vn         # tùy chọn: web học tập và app ở địa chỉ khác API thì đặt địa chỉ đủ; để trống thì đường dẫn tệp là /files/...
```

Quy tắc ảnh, mô hình, endpoint và cách kiểm thử: [docs/tai-tep-len.md](docs/tai-tep-len.md).

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
