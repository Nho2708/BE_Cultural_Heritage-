# Gửi thư đặt mật khẩu

Hệ thống có hai loại thư, chỉ gửi cho tài khoản người lớn (có email):

| Thư | Khi nào | Thời hạn liên kết |
|---|---|---|
| Đặt mật khẩu lần đầu | Quản trị hệ thống tạo trường hoặc thêm quản trị trường; quản trị trường thêm giáo viên | 48 giờ |
| Đặt lại mật khẩu | Người dùng dùng chức năng "Quên mật khẩu" | 30 phút |

Mỗi thư có bản HTML (nút bấm "Đặt mật khẩu" / "Đặt lại mật khẩu") và bản chữ thuần cùng nội dung, tiếng Việt có dấu:
lời chào theo họ tên, tên đăng nhập và tên trường (thư lần đầu), đường dẫn
`{App:FrontendBaseUrl}{App:SetPasswordPath}?token=…`, thời hạn và giờ hết hạn theo giờ Việt Nam, dòng
"Nếu bạn không yêu cầu, hãy bỏ qua thư này". Thời hạn lấy từ `AccountRules` (`FirstPasswordLinkLifetime`,
`ForgotPasswordLinkLifetime`), đổi ở đó thì nội dung thư đổi theo.

## 1. Hai chế độ

| `Email:Mode` | Bản gửi | Dùng khi |
|---|---|---|
| `Log` (mặc định) | `LogEmailSender`: không gửi, ghi bản chữ thuần của thư ra log, **kể cả đường dẫn có token** | Máy phát triển, smoke test (`scripts/smoke-nen-tang.sh` đọc token từ log) |
| `Smtp` | `SmtpEmailSender` (MailKit): gửi thật, mỗi thư một kết nối | Khi cần người nhận thật mở thư |

Ở chế độ `Smtp`, log chỉ ghi địa chỉ người nhận đã che bớt (`qu***@gmail.com`) và tiêu đề, **không bao giờ ghi nội dung
thư hay token**.

Thư gửi **sau khi** giao dịch tạo tài khoản đã lưu. Gửi không được (máy chủ SMTP từ chối, sai mật khẩu, mất mạng, quá thời gian
chờ) thì tài khoản vẫn được tạo, API vẫn trả 201, log ghi lỗi dạng
`Gửi thư đặt mật khẩu lần đầu thất bại cho tài khoản 12` kèm lý do
(`Không gửi được thư qua SMTP smtp.gmail.com:587 tới th***@truong.edu.vn: máy chủ từ chối tên đăng nhập hoặc mật khẩu SMTP …`).
Người dùng lấy liên kết mới bằng "Quên mật khẩu" ở trang đăng nhập (liên kết 30 phút); hệ thống không có nút gửi lại thư riêng.
Quên mật khẩu gửi lỗi thì câu trả lời vẫn như cũ (không để lộ email có tài khoản hay không).

## 2. Cấu hình

| Khóa | Mặc định | Ghi chú |
|---|---|---|
| `Email:Mode` | `Log` | `Log` hoặc `Smtp` (không phân biệt hoa thường) |
| `Email:Smtp:Host` | trống | Bắt buộc khi `Smtp`, vd `smtp.gmail.com` |
| `Email:Smtp:Port` | `587` | |
| `Email:Smtp:UseStartTls` | `true` | `true`: bắt buộc STARTTLS, máy chủ không hỗ trợ thì báo lỗi chứ không gửi trần. `false`: cổng 465 mã hóa ngay khi kết nối, cổng khác dùng STARTTLS nếu máy chủ có |
| `Email:Smtp:Username` | trống | Để trống nếu máy chủ không yêu cầu đăng nhập |
| `Email:Smtp:Password` | — | **Không ghi vào appsettings.** Chỉ đặt bằng User Secrets hoặc biến môi trường `Email__Smtp__Password`. Bắt buộc khi có `Username` |
| `Email:Smtp:FromAddress` | trống | Bắt buộc khi `Smtp`, phải là địa chỉ email hợp lệ |
| `Email:Smtp:FromName` | `DSVHVN — Nền tảng học di sản` | Tên người gửi hiện trong hộp thư |
| `Email:Smtp:TimeoutSeconds` | `30` | Thời gian chờ tối đa mỗi bước kết nối, đăng nhập, gửi |
| `App:FrontendBaseUrl`, `App:SetPasswordPath` | `http://localhost:5173`, `/dat-mat-khau` | Đường dẫn trong thư |

`Email:Mode = Smtp` mà thiếu mục bắt buộc thì API **dừng ngay lúc khởi động** và liệt kê đủ các mục còn thiếu, vd:

```
Microsoft.Extensions.Options.OptionsValidationException: Cấu hình gửi thư qua SMTP chưa đủ (Email:Mode = Smtp):
thiếu Email:Smtp:FromAddress (địa chỉ người gửi); có Email:Smtp:Username nhưng thiếu Email:Smtp:Password
(đặt bằng User Secrets hoặc biến môi trường Email__Smtp__Password, không ghi vào appsettings).
```

## 3. Bật gửi thật bằng Gmail

Chuẩn bị tài khoản Gmail dùng để gửi (nên là một hộp thư riêng của nhóm):

1. Bật **Xác minh 2 bước** cho tài khoản Google: <https://myaccount.google.com/security>.
2. Tạo **Mật khẩu ứng dụng** (App Password): <https://myaccount.google.com/apppasswords>, đặt tên vd "DSVHVN API".
   Google hiện 16 ký tự dạng `abcd efgh ijkl mnop`; nhập **liền, bỏ khoảng trắng**. Đây không phải mật khẩu Gmail thường;
   dùng mật khẩu Gmail thường sẽ bị từ chối (`535 5.7.8 Username and Password not accepted`).
   Tài khoản Google Workspace có thể bị quản trị viên tắt mật khẩu ứng dụng.

Chạy từ thư mục gốc của repo (User Secrets chỉ nạp khi `ASPNETCORE_ENVIRONMENT=Development`, đúng với launch profile `http`):

```bash
dotnet user-secrets set "Email:Mode" "Smtp" --project src/DSVHVN.Api
dotnet user-secrets set "Email:Smtp:Host" "smtp.gmail.com" --project src/DSVHVN.Api
dotnet user-secrets set "Email:Smtp:Port" "587" --project src/DSVHVN.Api
dotnet user-secrets set "Email:Smtp:Username" "<tai-khoan>@gmail.com" --project src/DSVHVN.Api
dotnet user-secrets set "Email:Smtp:Password" "<app-password>" --project src/DSVHVN.Api
dotnet user-secrets set "Email:Smtp:FromAddress" "<tai-khoan>@gmail.com" --project src/DSVHVN.Api
```

`FromAddress` để trùng `Username`: Gmail tự đổi người gửi về tài khoản đã đăng nhập nếu khác. `UseStartTls` và `FromName` dùng
mặc định. Khởi động lại API; log có dòng `Thư đặt mật khẩu gửi thật qua SMTP smtp.gmail.com:587, người gửi …`.

Kiểm tra: tạo một trường mới (hoặc một giáo viên) với **email thật** của thành viên nhóm, mở hộp thư (xem cả mục Thư rác),
bấm nút đặt mật khẩu. Log có dòng `Đã gửi thư qua SMTP tới th***@… | Tiêu đề: …`.

Tắt gửi thật, quay về ghi log:

```bash
dotnet user-secrets set "Email:Mode" "Log" --project src/DSVHVN.Api
```

`dotnet user-secrets list --project src/DSVHVN.Api` hiện cả mật khẩu ứng dụng ở dạng chữ rõ; không chụp màn hình, không dán vào
tài liệu. Môi trường không phải Development (máy chủ UAT) đặt bằng biến môi trường: `Email__Mode=Smtp`,
`Email__Smtp__Host=smtp.gmail.com`, `Email__Smtp__Username=…`, `Email__Smtp__Password=…`, `Email__Smtp__FromAddress=…`.
Gmail cá nhân giới hạn khoảng 500 người nhận mỗi ngày, đủ cho kiểm thử và demo.

## 4. Lưu ý

- **Địa chỉ `*.local` không nhận được thư.** 4 tài khoản quản trị hệ thống seed ở Development (`nho.admin@dsvhvn.local`,
  `lam.admin@dsvhvn.local`, …) có email đuôi `.local` không tồn tại: "Quên mật khẩu" cho các tài khoản này vẫn trả cùng một
  câu, còn thư thì Gmail nhận rồi gửi lại thư báo không chuyển được vào hộp thư người gửi (hoặc từ chối ngay, khi đó log ghi
  lỗi gửi thư). Không ai nhận được liên kết. Muốn thử quên mật khẩu với tài khoản quản trị hệ thống thì
  đổi email của tài khoản đó trong CSDL dev, vd
  `UPDATE users SET email = N'<email-that>' WHERE username = 'admin.nho';` (email lưu chữ thường). Sửa `Seed:Admins` không
  có tác dụng với tài khoản đã seed.
- **Đường dẫn trong thư trỏ tới `App:FrontendBaseUrl`** (mặc định `http://localhost:5173`): chỉ mở được trên máy đang chạy web
  quản trị. Mở thư trên điện thoại hoặc máy khác thì đặt `App:FrontendBaseUrl` về địa chỉ máy đó truy cập được.
- Máy chủ thư thử nghiệm trên máy phát triển (smtp4dev, Papercut…) không cần đăng nhập: `Email:Mode=Smtp`,
  `Email:Smtp:Host=localhost`, `Email:Smtp:Port=2525` (cổng của công cụ), `Email:Smtp:UseStartTls=false`, `Username` để trống.
- Smoke test `scripts/smoke-nen-tang.sh` đọc token từ log nên phải chạy với `Email:Mode=Log`.
