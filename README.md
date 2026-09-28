# BE_Cultural_Heritage- — Backend DSVHVN

Backend của **DSVHVN — nền tảng giáo dục di sản văn hóa cho trường học** (đồ án FA26SE171, FPT University HCM):
giáo viên tạo bài học và bài kiểm tra cho lớp từ dữ liệu của Cục Di sản văn hóa; học sinh có tài khoản (đăng nhập bằng mã học sinh
+ mật khẩu) ở web học tập và app mobile; gói dịch vụ bán cho trường. ASP.NET Core 9 (controllers) + EF Core 9 + SQL Server 2019.
Nội dung và thông điệp chỉ tiếng Việt.

Làm theo từng luồng (vertical slice): bảng của luồng → migration → API → UI → bổ sung cột bằng migration mới.
Lược đồ nguồn: `doc/database/dsvhvn-v3.dbml` (27 bảng); backend đã có **đủ 27 bảng** trong một migration khởi tạo, tên bảng,
tên cột giữ đúng DBML (`snake_case`) — xem [docs/co-so-du-lieu.md](docs/co-so-du-lieu.md).

| Luồng | Trạng thái | Tài liệu |
|---|---|---|
| Cơ sở dữ liệu v3: 27 bảng, migration khởi tạo, nạp 34 tỉnh/thành và di sản đợt 1, đối chiếu với DBML | Xong | [docs/co-so-du-lieu.md](docs/co-so-du-lieu.md) |
| Nền tảng: đăng nhập web quản trị bằng email/username, đặt mật khẩu qua email, hồ sơ, ADMIN quản lý trường, ORG_ADMIN quản lý giáo viên, nhật ký thao tác | Xong phần backend | [docs/nen-tang-to-chuc.md](docs/nen-tang-to-chuc.md) · [docs/smoke-nen-tang.md](docs/smoke-nen-tang.md) |

## Cấu trúc

```
src/DSVHVN.Domain          Thực thể đủ 27 bảng theo CSDL v3, enum, quy tắc nghiệp vụ thuần
src/DSVHVN.Application     Dịch vụ nghiệp vụ, kiểm gói và hạn mức, validator, danh mục thông điệp tiếng Việt, nhật ký thao tác
src/DSVHVN.Infrastructure  EF Core (DbContext, cấu hình theo nhóm bảng, migration, seed, nạp dữ liệu di sản), JWT,
                           băm mật khẩu PBKDF2, email giả
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
```

CSDL dev: `sep490_dsvhvn_v3_dev` trên `(localdb)\MSSQLLocalDB`, collation `Vietnamese_CI_AI` (không dùng các CSDL cũ `dsvhvn_dev`,
`sep490_dsvhvn_dev`, `sep490_dsvhvn_v2_dev`). Dùng CSDL khác thì đặt biến môi trường
`ConnectionStrings__Default` (cả cho `dotnet-ef` lẫn Api). Dữ liệu di sản đọc từ thư mục `doc/du-lieu-di-san` của nhóm nằm cạnh
repo (`Seed:HeritageData:*` trong `appsettings.Development.json`); máy không có thư mục này thì bước nạp di sản được bỏ qua.
Thư đặt mật khẩu hiện là bản giả ghi ra log (có liên kết kèm token). Chi tiết endpoint, cấu hình và quyết định hiện thực ở
[docs/nen-tang-to-chuc.md](docs/nen-tang-to-chuc.md).
