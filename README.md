# BE_Cultural_Heritage- — Backend DSVHVN

Backend của **DSVHVN — nền tảng giáo dục di sản văn hóa cho trường học** (đồ án FA26SE171, FPT University HCM):
giáo viên tạo bài học và bài kiểm tra từ dữ liệu của Cục Di sản văn hóa; học sinh không có tài khoản, làm bài bằng mã;
gói dịch vụ bán cho trường. ASP.NET Core 9 (controllers) + EF Core 9 + SQL Server 2019. Nội dung và thông điệp chỉ tiếng Việt.

Làm theo từng luồng (vertical slice): bảng của luồng → migration → API → UI → bổ sung cột bằng migration mới.
Lược đồ nguồn: `doc/database/dsvhvn-v2.dbml` (28 bảng); tên bảng, tên cột giữ đúng DBML (`snake_case`).

| Luồng | Trạng thái | Tài liệu |
|---|---|---|
| Nền tảng: đăng nhập email/username, đặt mật khẩu qua email, hồ sơ, ADMIN quản lý trường, ORG_ADMIN quản lý giáo viên, nhật ký kiểm toán | Xong phần backend (7 bảng) | [docs/nen-tang-to-chuc.md](docs/nen-tang-to-chuc.md) · [docs/smoke-nen-tang.md](docs/smoke-nen-tang.md) |

## Cấu trúc

```
src/DSVHVN.Domain          Thực thể theo DBML v2, enum, quy tắc nghiệp vụ thuần
src/DSVHVN.Application     Dịch vụ nghiệp vụ, kiểm gói và hạn mức, validator, danh mục thông điệp tiếng Việt, nhật ký kiểm toán
src/DSVHVN.Infrastructure  EF Core (DbContext, migration, seed), JWT, băm mật khẩu PBKDF2, email giả
src/DSVHVN.Api             Controllers, middleware, phân quyền 3 vai trò (ADMIN, ORG_ADMIN, TEACHER)
tests/DSVHVN.Tests         xUnit: unit + integration (WebApplicationFactory + SQLite trong bộ nhớ)
```

## Chạy nhanh

Yêu cầu: .NET SDK 9.0.317, SQL Server LocalDB.

```bash
dotnet tool restore                                   # dotnet-ef 9.0.20 ghim trong .config/dotnet-tools.json

# Mật khẩu 4 tài khoản ADMIN seed (admin.nho, admin.lam, admin.trieu, admin.huy): chỉ đặt trong User Secrets, không commit
dotnet user-secrets set "Seed:AdminPassword" "<mật khẩu 8-64 ký tự, có chữ hoa, chữ thường, chữ số>" --project src/DSVHVN.Api

dotnet tool run dotnet-ef database update --project src/DSVHVN.Infrastructure --startup-project src/DSVHVN.Api
dotnet run --project src/DSVHVN.Api --launch-profile http > api.log 2>&1 &   # http://localhost:5080/swagger

dotnet test                                           # không cần LocalDB
API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu trên>' bash scripts/smoke-nen-tang.sh
```

CSDL dev: `sep490_dsvhvn_v2_dev` trên `(localdb)\MSSQLLocalDB`, collation `Vietnamese_CI_AI` (không dùng `dsvhvn_dev`,
`sep490_dsvhvn_dev` của hướng cũ). Thư đặt mật khẩu hiện là bản giả ghi ra log (có liên kết kèm token). Chi tiết endpoint,
bảng, cấu hình và quyết định hiện thực ở [docs/nen-tang-to-chuc.md](docs/nen-tang-to-chuc.md).
