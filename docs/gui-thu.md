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
| `Email:Smtp:FromName` | `DiViet` | Tên người gửi hiện trong hộp thư |
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

**Hộp thư theo tên miền (Mắt Bão).** Hộp thư `support@diviet.id.vn` nằm trên máy chủ thư của Mắt Bão (bản ghi MX của tên miền).
Gửi bằng hộp thư này: `Email:Smtp:Host` = `s129d209.emailserver.vn` (tên trong chứng chỉ TLS `*.emailserver.vn`; `mail.diviet.id.vn`
trỏ về cùng máy nhưng không khớp chứng chỉ), cổng `587` với STARTTLS (mặc định), `Username` và `FromAddress` đều là địa chỉ đầy đủ,
`Password` là mật khẩu của hộp thư. Máy Google Cloud mở được cổng 587 và 465 tới máy chủ này (Google chặn cổng 25).

## 4. Lưu ý

- **Địa chỉ `*.local` bị bỏ qua.** 4 tài khoản quản trị hệ thống seed ở Development (`nho.admin@dsvhvn.local`,
  `lam.admin@dsvhvn.local`, …) và tài khoản của dữ liệu mẫu có email đuôi `.local` không tồn tại. Chế độ `Smtp` không gửi
  tới các địa chỉ này (log ghi "Bỏ qua thư tới …"), thư liên hệ của trường cũng không tính họ là người nhận, nên Gmail không
  phải gửi lại thư báo không chuyển được. Chế độ `Log` vẫn ghi thư như cũ để smoke test đọc token. "Quên mật khẩu" cho các tài
  khoản này vẫn trả cùng một câu nhưng không ai nhận được liên kết. Máy chủ và máy demo chỉ có một quản trị hệ thống với email
  thật, khai ở `deploy/quan-tri-he-thong.json`. Muốn thử quên mật khẩu với tài khoản quản trị hệ thống trên máy dev thì
  đổi email của tài khoản đó trong CSDL dev, vd
  `UPDATE users SET email = N'<email-that>' WHERE username = 'admin.nho';` (email lưu chữ thường). Sửa `Seed:Admins` không
  có tác dụng với tài khoản đã seed.
- **Đường dẫn trong thư trỏ tới `App:FrontendBaseUrl`** (mặc định `http://localhost:5173`): chỉ mở được trên máy đang chạy web
  quản trị. Mở thư trên điện thoại hoặc máy khác thì đặt `App:FrontendBaseUrl` về địa chỉ máy đó truy cập được.
- Máy chủ thư thử nghiệm trên máy phát triển (smtp4dev, Papercut…) không cần đăng nhập: `Email:Mode=Smtp`,
  `Email:Smtp:Host=localhost`, `Email:Smtp:Port=2525` (cổng của công cụ), `Email:Smtp:UseStartTls=false`, `Username` để trống.
- Smoke test `scripts/smoke-nen-tang.sh` đọc token từ log nên phải chạy với `Email:Mode=Log`.

## 5. Thư liên hệ của trường (trang Dành cho nhà trường)

Ngoài hai loại thư đặt mật khẩu, biểu mẫu "Liên hệ để được tạo tài khoản cho trường" của web học tập gửi một thư cho **quản trị hệ thống**
qua cùng `IEmailSender` (cùng `Email:Mode`, cùng cấu hình SMTP).

| Mục | Giá trị |
|---|---|
| Endpoint | `POST /api/v1/public/school-inquiries`, không cần đăng nhập, không có bảng mới |
| Dữ liệu | `schoolName` (bắt buộc, <= 200), `province` (<= 100), `contactName` (bắt buộc, <= 100), `position` (<= 100), `email` (bắt buộc, đúng định dạng), `phone` (8-20 ký tự), `message` (bắt buộc, <= 2000), `website` (ô bẫy ẩn) |
| Thành công | `202` và câu "Đã gửi. Nhóm sẽ liên hệ lại qua email." Ô bẫy `website` có giá trị thì vẫn `202` nhưng **không gửi thư** |
| Lỗi | `400` theo khuôn lỗi chung (lỗi theo trường, tiếng Việt); `429` khi vượt giới hạn; `503` "Chưa gửi được, vui lòng thử lại sau." khi không có người nhận hoặc không thư nào gửi đi được |
| Người nhận | Các địa chỉ khai ở `SchoolInquiry:Recipients` (mảng, vd biến môi trường `SchoolInquiry__Recipients__0`), vd hộp thư chung của nhóm. Danh sách này trống (hoặc không còn địa chỉ dùng được) thì gửi tới email của mọi tài khoản **quản trị hệ thống** đang hoạt động (chưa bị khóa, chưa xóa). Bỏ trùng, bỏ địa chỉ sai định dạng và đuôi `.local` |
| Giới hạn tần suất | 5 lần mỗi 10 phút mỗi IP, cửa sổ cố định, đổi bằng `RateLimiting:SchoolInquiryPermits` và `RateLimiting:SchoolInquiryWindowMinutes` |

Tiêu đề thư: `DiViet – Trường liên hệ: <tên trường>`. Mỗi người nhận một thư riêng, có địa chỉ trả lời là email người nhập để quản trị
trả lời thẳng. Mọi chữ người dùng nhập được mã hóa HTML ở bản HTML; giá trị một dòng được gộp khoảng trắng và bỏ ký tự điều khiển. Hệ thống
**không gửi thư xác nhận** về địa chỉ người nhập. Một người nhận lỗi chỉ ghi log (địa chỉ đã che bớt) và không chặn người khác. Máy demo dùng
`Email:Mode=Log` thì nội dung thư chỉ hiện trong log.

Máy demo: các tài khoản quản trị hệ thống mẫu có email đuôi `.local` nên không nhận được thư. Khai hộp thư nhận thật trong User Secrets của
`src/DSVHVN.Api` (`dotnet user-secrets set "SchoolInquiry:Recipients:0" "<email>"`); `scripts/trien-khai/tao-cau-hinh-may-demo.ps1` chép mảng này
vào `appsettings.Production.json` của máy demo.
