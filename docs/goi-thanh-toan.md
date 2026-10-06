# Luồng Gói và thanh toán VNPay (Backend)

Phần backend của việc **bán gói dịch vụ cho trường**. Quản trị hệ thống tạo, sửa, bật và tắt bán gói. Quản trị trường chọn gói, hệ thống tạo **yêu cầu mua chờ thanh toán + hóa đơn chưa trả + giao dịch chờ**
rồi trả địa chỉ **VNPay sandbox** có chữ ký để trình duyệt chuyển sang trả tiền. Gói chỉ có hiệu lực khi **IPN** (máy chủ VNPay gọi máy chủ này) hợp lệ về tới backend: kiểm chữ ký, kiểm số tiền, ghi giao dịch,
hóa đơn đã trả, gói đang hiệu lực, tất cả trong một giao dịch CSDL và **chỉ đổi một lần dù IPN gửi lặp hay đồng thời**. Trang VNPay đưa trình duyệt về chỉ đọc kết quả. Trường chưa có gói thì kích hoạt gói
**Miễn phí** ngay, không qua VNPay. Tác vụ nền chuyển giao dịch treo thành thất bại, hủy hóa đơn quá hạn và chuyển gói quá hạn sang hết hạn.

| Mục | Giá trị |
|---|---|
| Chức năng | Quản lý gói dịch vụ · Mua, gia hạn hoặc nâng cấp gói · Xử lý kết quả thanh toán VNPay (hệ thống) · Quản lý gói và hóa đơn của trường |
| Màn hình phục vụ | Web quản trị: Gói dịch vụ (quản trị hệ thống); Trường và gói (tab Gói và hạn mức, tab Hóa đơn và giao dịch), Chọn gói và thanh toán, Kết quả thanh toán (quản trị trường). Không có màn hình cho IPN |
| Bảng | `plans`, `subscriptions`, `invoices`, `payment_providers`, `payments` (đã có từ CSDL v3, **không thêm bảng, không thêm cột**); ghi `audit_logs` |
| Migration | `ThemRangBuocGoiVaThanhToan`: chỉ **thêm chỉ mục** (xem mục 3); bỏ hai chỉ mục thường thừa vì đã có chỉ mục mới bao trùm cột đó. Chạy được trên CSDL có dữ liệu nếu chưa có hai yêu cầu mua chưa trả của cùng một trường hay tên gói trùng |
| Lớp mới | `Domain/Rules/BillingRules` (phân loại mua, ngày bắt đầu, số hóa đơn, hạn); `Application/Billing/` (`PlanService`, `BillingService`, `BillingQueries`, `VnPayCallbackService`, `SubscriptionActivator`, `BillingMaintenanceService`, DTO, `BillingMessages`); `Infrastructure/Payments/` (`VnPayGateway`, `BillingMaintenanceWorker`); `Api/Controllers/` (`AdminPlansController`, `AdminInvoicesController`, `OrgBillingController`, `VnPayController`) |
| Nguồn | `doc/plan/spec` (use case Quản lý gói dịch vụ, Mua, gia hạn hoặc nâng cấp gói, Xử lý kết quả thanh toán VNPay, Quản lý gói và hóa đơn của trường; màn hình Gói dịch vụ, Trường và gói, Chọn gói và thanh toán, Kết quả thanh toán; quy tắc Gói dịch vụ và gói Miễn phí, Trạng thái đăng ký gói và gói hiệu lực, Mua, gia hạn, nâng cấp và hủy gói, Hóa đơn, giao dịch và doanh thu, Xử lý IPN của VNPay, Hạn mức AI theo kỳ gói) · `doc/database/dsvhvn-v4.dbml` |

## 1. Quy tắc

| Quy tắc | Nội dung |
|---|---|
| Gói | Tên không trùng (không phân biệt hoa thường, dấu; ở ứng dụng còn coi "Cơ bản" và "Co ban" là một), tối đa 100 ký tự. Giá là **số nguyên đồng**, đã gồm thuế, từ 0 đến **999.999.999** (VNPay không nhận giao dịch từ một tỷ đồng). Thời hạn 1-730 ngày. Số giáo viên tối đa 1-10.000. Số lượt AI mỗi kỳ 0-1.000.000. Đổi giá **không hồi tố**: hóa đơn đã tạo giữ số tiền lúc tạo. Không có thao tác xóa gói. Gói không giới hạn số học sinh |
| Gói Miễn phí | Gói giá 0 duy nhất (dữ liệu khởi tạo, giá trị theo môi trường). Giá luôn bằng 0, **không đổi tên, không tắt bán được**; thời hạn và hai hạn mức sửa được. Gói trả phí mới bắt buộc giá lớn hơn 0. Kích hoạt **ngay**, không qua VNPay, không tạo hóa đơn, chỉ khi trường **không có gói hiệu lực và không có gói gia hạn chờ** (yêu cầu mua chưa trả không cản); có thể kích hoạt lại sau khi gói hết hạn hoặc bị hủy |
| Bán và tắt bán | Chỉ gói đang bán (`is_active`) mua được và gia hạn được. Tắt bán thì đăng ký đang có **vẫn dùng tới hết hạn**; gói Miễn phí không hiện ở màn hình Chọn gói |
| Trạng thái đăng ký | `PENDING` (chờ thanh toán) → `ACTIVE` (chỉ khi IPN thành công) · `PENDING` → `CANCELLED` (trường hủy hoặc hóa đơn quá hạn) · `ACTIVE` → `EXPIRED` (qua `end_date`) · `ACTIVE` → `CANCELLED` (nâng cấp hoặc trường hủy). `end_date` = `start_date` + thời hạn − 1; dùng tới hết ngày đó theo giờ Việt Nam. Mỗi trường có tối đa **một yêu cầu `PENDING`** (chỉ mục duy nhất có lọc của CSDL) |
| Phân loại mua | Hệ thống tự phân loại theo gói hiệu lực hiện tại: chưa có gói → **mua** (từ ngày nhận IPN); gói mới giá **cao hơn** → **nâng cấp** (kể cả từ gói Miễn phí); còn lại (cùng gói, gói khác giá bằng hoặc thấp hơn) → **gia hạn** = mua lại, bắt đầu từ ngày sau khi gói hiện tại hết hạn, hoặc từ ngày nhận IPN nếu IPN tới sau ngày đó |
| Nâng cấp | Hiệu lực ngay từ ngày nhận IPN; gói hiện tại và gói gia hạn chờ chuyển `CANCELLED`; **không hoàn phần chưa dùng** (không dùng `REFUNDED`). Kỳ lượt AI của gói mới bắt đầu từ ngày đó, lượt đã dùng trong ngày nâng cấp vẫn tính |
| Chặn mua | Trường đã có yêu cầu mua chưa trả (`PENDING_PURCHASE_EXISTS`); gia hạn khi đã có gói gia hạn chờ (nâng cấp thì vẫn được); trường chỉ còn gói gia hạn chờ bắt đầu (chưa mua thêm được); gói đã tắt bán hoặc gói Miễn phí (`PLAN_NOT_ON_SALE`). Mọi trường hợp 422 kèm lý do |
| Hủy | Quản trị trường hủy **yêu cầu mua chưa trả** (hóa đơn `UNPAID` → `CANCELLED`, giao dịch chờ → `FAILED`) hoặc **gói trả phí đang hiệu lực** (kết thúc ngay, không hoàn tiền, trường không còn gói hiệu lực cho tới khi mua gói mới hoặc kích hoạt gói Miễn phí). **Không hủy được**: gói Miễn phí; gói gia hạn đã thanh toán; gói hiện tại khi đã có gói gia hạn chờ (vì gói gia hạn không hủy được) |
| Hóa đơn | Tạo cùng yêu cầu mua trong **một giao dịch CSDL**: số tiền = giá gói lúc tạo, số hóa đơn `HD` + ngày `yyyyMMdd` + `-` + số thứ tự 6 chữ số trong ngày (vd `HD20261008-000012`; **khóa ứng dụng** `sp_getapplock` giữ tới hết giao dịch nên hai trường mua cùng lúc không lấy trùng số, chỉ mục duy nhất là lưới an toàn), hạn thanh toán = lúc xuất + **24 giờ**. Là **biên nhận nội bộ**, không phải hóa đơn giá trị gia tăng; không sửa, không xóa, không gửi email. Quản trị trường chỉ xem hóa đơn của trường mình (id trường khác trả 404); quản trị hệ thống xem toàn bộ |
| Giao dịch | Mỗi lần "Thanh toán" tạo một dòng `payments` `PENDING` với cổng `VNPAY`; mã tham chiếu gửi cổng (`vnp_TxnRef`) là `payments.id`; thời hạn thanh toán gửi cổng **15 phút**. Mỗi hóa đơn có **tối đa một giao dịch chờ** (chỉ mục duy nhất có lọc): bấm "Thanh toán" lại khi còn giao dịch chờ thì giao dịch cũ được chuyển `FAILED` (không có dữ liệu của cổng) rồi tạo giao dịch mới. Hóa đơn chưa trả thanh toán lại được tới hạn của hóa đơn |
| VNPay sandbox | Chỉ VNPay, chỉ sandbox: `payment_providers` có `VNPAY` bật, `MOMO`, `ZALOPAY`, `BANK_TRANSFER` tắt (dữ liệu khởi tạo, không ghi đè cổng đã có). Hệ thống không lưu số thẻ hay tài khoản ngân hàng; `raw_response` lưu dữ liệu IPN đã đổi sang JSON khóa-giá trị |
| IPN | Chỉ IPN do VNPay gửi mới đổi trạng thái giao dịch, hóa đơn, gói. Chấp nhận khi đồng thời: chữ ký HMAC-SHA512 hợp lệ, `vnp_TxnRef` là giao dịch `VNPAY` có thật, `vnp_Amount` = số tiền × 100. Mã phản hồi và trạng thái giao dịch đều `00` → giao dịch `SUCCEEDED` (ghi `provider_transaction_id`, `paid_at`, `raw_response`), hóa đơn `PAID`, đăng ký `ACTIVE` với ngày theo loại mua; ngược lại giao dịch `FAILED`, hóa đơn vẫn `UNPAID` (thanh toán lại được). Mã giao dịch của cổng `"0"` hoặc trống (giao dịch chưa tạo ở cổng) không được lưu |
| Chống trùng | Một câu lệnh cập nhật có điều kiện "chỉ khi còn chờ" **chiếm** giao dịch trước khi đổi gì khác: IPN lặp hoặc đồng thời chỉ có một IPN thắng, các IPN còn lại nhận "02 đã xử lý" và không đổi dữ liệu; chỉ mục duy nhất có lọc (`provider_id`, `provider_transaction_id`) và kiểm trước để một mã giao dịch của cổng không thuộc hai dòng. Mọi thao tác của một trường (mua, thanh toán lại, hủy, IPN, tác vụ nền) khóa dòng trường nên chạy tuần tự |
| IPN thành công tới muộn | Giao dịch đã bị **hệ thống** đánh `FAILED` (quá 30 phút, bị thay bằng giao dịch mới, yêu cầu bị hủy; chưa có dữ liệu của cổng) mà IPN thành công tới sau vẫn được ghi `SUCCEEDED` và kích hoạt gói (kể cả khi hóa đơn và đăng ký đã `CANCELLED`) vì tiền đã thu, ghi `"late": true` vào nhật ký. IPN thất bại tới sau khi giao dịch đã `FAILED` chỉ là IPN lặp. Hóa đơn đã `PAID` từ giao dịch khác (người trả hai lần) thì chỉ ghi giao dịch và nhật ký để đối soát, **không kích hoạt thêm gói** (không có hoàn tiền tự động) |
| IPN bị từ chối | Sai chữ ký (97), không thấy giao dịch hoặc thiếu mã tham chiếu (01), lệch số tiền (04), mã giao dịch của cổng đã thuộc dòng khác hoặc thiếu khóa cấu hình (99): **không đổi dữ liệu**, ghi nhật ký `IPN_REJECTED` với lý do, địa chỉ gửi và các tham số `vnp_*` nhận được (cắt ngắn, không kèm khóa) |
| Trang trả về | VNPay đưa trình duyệt về `GET /api/v1/payments/vnpay/return`: kiểm chữ ký rồi chuyển (302) sang trang Kết quả thanh toán của web quản trị (`?payment={id}`, hoặc `?error=invalid` khi chữ ký sai). **Không đọc, không ghi dữ liệu**. Trang kết quả gọi API đọc trạng thái do IPN đã ghi |
| Tác vụ nền | Chạy mỗi 60 giây (đổi bằng `Billing:Maintenance:IntervalSeconds`; tắt bằng `Billing:Maintenance:Enabled` = false), theo thứ tự: **(1)** giao dịch `PENDING` tạo quá **30 phút** không nhận IPN → `FAILED`; **(2)** hóa đơn `UNPAID` quá hạn và không còn giao dịch chờ → hóa đơn và đăng ký `PENDING` → `CANCELLED`; **(3)** mỗi ngày một lần từ **00:05** giờ Việt Nam: gói `ACTIVE` qua `end_date` → `EXPIRED` (không tự chuyển trường về gói Miễn phí). Mỗi thay đổi ghi `BILLING_STATUS_CHANGED` do hệ thống |
| Khi trường không có gói hiệu lực | Chặn tạo, nhân bản bài học, gọi AI, tạo, nhân bản, phát hành bài kiểm tra, thêm giáo viên (đã làm ở các luồng trước, đọc gói qua `PlanGuard`); học sinh không bị chặn. Gói hiệu lực chỉ tính theo ngày nên gói qua ngày hết hạn mất hiệu lực đúng ngày dù tác vụ nền chưa chạy |
| Phạm vi quyền | Quản trị hệ thống: gói (tạo, sửa, bật, tắt), xem hóa đơn và giao dịch của mọi trường. Quản trị trường: gói và hóa đơn của **trường mình** (mua, thanh toán lại, hủy, kích hoạt gói Miễn phí). Giáo viên, học sinh, khách: không có quyền nào ở luồng này (403). IPN và trang trả về công khai, bảo vệ bằng chữ ký |
| Nhật ký thao tác | `PLAN_UPDATED` (tạo, sửa, bật, tắt gói; giá trị trước và sau); `SUBSCRIPTION_CHANGED` (tạo yêu cầu mua, kích hoạt gói sau IPN, hủy yêu cầu, hủy gói, kích hoạt gói Miễn phí); `BILLING_STATUS_CHANGED` (hệ thống đổi trạng thái giao dịch, hóa đơn, gói: IPN, tác vụ nền; người dùng thay giao dịch chờ bằng giao dịch mới); `IPN_REJECTED`. Không ghi khóa, chữ ký, số thẻ |

## 2. Thông điệp (tiếng Việt, xưng "bạn")

Câu xác nhận, câu ở trang kết quả và câu xác nhận hủy chép nguyên danh mục thông điệp đã chốt (phần động điền sẵn); câu báo thao tác bị chặn theo khuôn "Không thể … . …" với mã `BLOCKED` hoặc mã riêng.

| Tình huống | Nội dung |
|---|---|
| Xác nhận trước khi chuyển sang VNPay (`PAYMENT_CONFIRM`, trong báo giá) | "Trường sẽ thanh toán {số tiền} đồng cho gói {tên gói} qua VNPay. {hiệu lực} Tiếp tục?" với {hiệu lực}: mua "Gói có hiệu lực từ ngày thanh toán."; gia hạn "Gói mới bắt đầu từ ngày {ngày}."; nâng cấp "Gói mới có hiệu lực ngay, gói hiện tại kết thúc và không được hoàn phần chưa dùng." |
| Thanh toán thành công (`PAYMENT_SUCCEEDED`) | "Thanh toán thành công. Gói {tên gói} có hiệu lực từ ngày {bắt đầu} đến hết ngày {hết hạn}." |
| Thất bại, hóa đơn còn hạn (`PAYMENT_FAILED`) | "Thanh toán chưa thành công. Hóa đơn {số} vẫn chờ thanh toán đến {giờ ngày}; bạn có thể thanh toán lại." |
| Thất bại, hóa đơn đã quá hạn hoặc bị hủy | "Thanh toán chưa thành công và hóa đơn {số} không còn thanh toán được. Bạn có thể chọn gói và thanh toán lại." (câu thêm, cùng mã) |
| Chưa nhận IPN (`PAYMENT_WAITING`) | "Hệ thống đang chờ xác nhận từ VNPay. Trang sẽ tự cập nhật khi có kết quả." |
| Xác nhận hủy gói (`CANCEL_PLAN_CONFIRM`, trong tổng quan) | "Bạn có chắc muốn hủy gói {tên gói}? Gói sẽ kết thúc ngay, không được hoàn tiền; …" (đủ câu theo danh mục) |
| Kích hoạt gói Miễn phí, tạo gói, lưu gói, hủy | "{Thao tác} thành công." |
| Cổng chưa sẵn sàng (503, `PAYMENT_GATEWAY_UNAVAILABLE`) | "Không thể thanh toán lúc này. Cổng VNPay chưa sẵn sàng, bạn hãy thử lại sau hoặc liên hệ quản trị viên." |
| Cảnh báo vượt hạn mức giáo viên khi chọn gói (`teacherWarning`) | "Trường đang có {n} tài khoản giáo viên, nhiều hơn {tối đa} của gói {tên gói}. Không ai bị khóa, nhưng trường chưa thêm được giáo viên mới cho tới khi số giáo viên giảm xuống dưới hạn mức." |

## 3. Dữ liệu và chỉ mục

Không thêm bảng, không thêm cột. Migration `ThemRangBuocGoiVaThanhToan`:

| Chỉ mục | Nội dung |
|---|---|
| `UX_plans_name` | duy nhất trên `plans.name` (lọc `name IS NOT NULL`); theo collation `Vietnamese_CI_AI` không phân biệt hoa thường và dấu thanh (chữ Ơ, Ư, Â, Ă, Ê, Ô, Đ là chữ cái riêng ở CSDL; ứng dụng kiểm chặt hơn bằng dạng gấp không dấu) |
| `UX_subscriptions_one_pending` | duy nhất trên `subscriptions.organization_id` lọc `status = 'PENDING'`: một yêu cầu mua chưa trả mỗi trường, kể cả khi hai yêu cầu đến cùng lúc |
| `UX_payments_one_pending_per_invoice` | duy nhất trên `payments.invoice_id` lọc `status = 'PENDING'`: một giao dịch chờ mỗi hóa đơn |
| `IX_subscriptions_organization_status`, `IX_payments_status_created_at`, `IX_invoices_status_due_at` | tra gói của trường, và hai việc nền tìm giao dịch treo, hóa đơn quá hạn |

Hai chỉ mục thường `IX_subscriptions_organization_id` và `IX_payments_invoice_id` được thay bằng các chỉ mục trên. Số liệu sau migration: 34 bảng, 320 cột, 53 khóa ngoại, 38 CHECK, **8 UNIQUE có lọc** (thêm 3), 15 UNIQUE thường.

Dữ liệu khởi tạo (chạy lúc khởi động Api, không ghi đè): gói Miễn phí như các luồng trước; **bốn cổng thanh toán** (chỉ `VNPAY` bật); **gói mẫu** `Seed:Plans` (chỉ khai báo ở `appsettings.Development.json`: "Cơ bản" 500.000 đồng, "Nâng cao" 1.990.000 đồng, 365 ngày) và chỉ nạp khi chưa có gói trả phí nào. UAT và Release 1.0 do quản trị hệ thống tự tạo gói ở màn hình Gói dịch vụ.

## 4. API quản trị hệ thống (tiền tố `/api/v1/admin`; 401 chưa đăng nhập, 403 sai vai trò)

| Method | Đường dẫn | Nội dung | Thành công | Lỗi |
|---|---|---|---|---|
| GET | `/plans` | mọi gói (cả gói đã tắt bán), giá tăng dần, kèm `isFree` và `schoolCount` (số trường đang có gói hiệu lực thuộc gói, không tính trường đã ngừng) | 200 `PlanDto[]` | |
| GET | `/plans/{id}` | một gói | 200 | 404 |
| POST | `/plans` | JSON `name`, `price`, `durationDays`, `maxTeachers`, `maxAiRequests`; gói mới đang bán | 201 `PlanDto` | 400 (lỗi theo trường) · 409 tên trùng |
| PUT | `/plans/{id}` | như trên, thay toàn bộ; gói Miễn phí chỉ đổi được thời hạn và hai hạn mức | 200 | 400 · 404 · 409 |
| POST | `/plans/{id}/disable` | tắt bán (gói Miễn phí: 422) | 200 | 404 · 422 |
| POST | `/plans/{id}/enable` | bật bán | 200 | 404 |
| GET | `/invoices?organizationId=&status=&from=&to=&page=&pageSize=` | hóa đơn của mọi trường, mới nhất trước | 200 `PagedResult<InvoiceDto>` | 400 |
| GET | `/invoices/{id}` | một hóa đơn kèm giao dịch | 200 `InvoiceDetailDto` | 404 |

## 5. API quản trị trường (tiền tố `/api/v1/org`; 401, 403 sai vai trò; trường luôn là trường của người gọi)

| Method | Đường dẫn | Nội dung | Thành công | Lỗi |
|---|---|---|---|---|
| GET | `/billing` | tổng quan tab Gói và hạn mức: `current` (gói hiệu lực, hoặc gói gần nhất đã hết hạn, đã hủy), `upcoming` (gói gia hạn chờ bắt đầu), `pending` (yêu cầu mua chưa trả, kèm hóa đơn và `hasWaitingPayment`), `hasEffectivePlan`, `teacherQuota`, `aiQuota`, `canActivateFreePlan`, `canCancelCurrent`, `cancelConfirmation` | 200 `BillingOverviewDto` | |
| GET | `/plans` | gói đang bán (không gồm gói Miễn phí), giá tăng dần: `kind` (`PURCHASE`, `RENEW`, `UPGRADE`), `isCurrent`, `available`, `unavailableReason` | 200 `PlanOfferDto[]` | |
| GET | `/plans/{planId}/quote` | khối xác nhận: `kind`, `amount`, `expectedStartDate`, `expectedEndDate`, `confirmation`, `teacherWarning` | 200 `PurchaseQuoteDto` | 404 · 422 |
| POST | `/billing/checkout` | JSON `{ "planId": 2 }`: tạo đăng ký `PENDING` + hóa đơn `UNPAID` + giao dịch `PENDING`, trả `paymentUrl` (địa chỉ VNPay có chữ ký), `expiresAt`, `kind`, `invoice`, `paymentId` | 201 `PaymentStartDto` | 400 · 404 · 422 · 503 |
| POST | `/billing/free-plan` | kích hoạt gói Miễn phí | 200 `BillingOverviewDto` | 422 |
| POST | `/subscriptions/{id}/cancel` | hủy yêu cầu mua chưa trả hoặc gói trả phí đang hiệu lực | 200 `BillingOverviewDto` | 404 · 422 |
| GET | `/invoices?status=&from=&to=&page=&pageSize=` | hóa đơn của trường, mới nhất trước; `canPay` cho nút "Thanh toán" | 200 `PagedResult<InvoiceDto>` | 400 |
| GET | `/invoices/{id}` | một hóa đơn kèm các giao dịch | 200 `InvoiceDetailDto` | 404 |
| POST | `/invoices/{id}/pay` | thanh toán (lại) hóa đơn chưa trả: giao dịch mới và `paymentUrl` | 200 `PaymentStartDto` | 404 · 422 · 503 |
| GET | `/payments/{id}` | trang Kết quả thanh toán: `outcome` (`PAID`, `UNPAID`, `PROCESSING`), `message`, hóa đơn, giao dịch, gói đã kích hoạt, `canPayAgain`; chỉ đọc, trang tự tải lại khi `PROCESSING` | 200 `PaymentResultDto` | 404 |

## 6. Địa chỉ công khai cho VNPay (không đăng nhập, bảo vệ bằng chữ ký)

| Method | Đường dẫn | Nội dung |
|---|---|---|
| GET | `/api/v1/payments/vnpay/ipn` | IPN: tham số `vnp_*` trên query string. Trả **đúng khuôn của VNPay** `{"RspCode":"00","Message":"Confirm Success"}` (không qua khuôn phản hồi chung): `00` đã ghi nhận, `01` không thấy giao dịch, `02` giao dịch đã xử lý trước đó (IPN lặp), `04` sai số tiền, `97` sai chữ ký, `99` lỗi khác. Lỗi hệ thống (CSDL) trả 500 để VNPay gửi lại |
| GET | `/api/v1/payments/vnpay/return` | trang trả về của trình duyệt: 302 sang `{App:FrontendBaseUrl}{App:PaymentResultPath}?payment={id}` hoặc `?error=invalid` |

Chữ ký: HMAC-SHA512 với khóa bí mật trên chuỗi `khóa=giá trị` của các tham số `vnp_*` khác rỗng, sắp theo tên khóa, giá trị mã hóa URL, nối bằng `&` (không gồm `vnp_SecureHash`, `vnp_SecureHashType`), hex chữ thường,
so sánh không phụ thuộc hoa thường và theo thời gian không đổi. Địa chỉ thanh toán gồm `vnp_Version` 2.1.0, `vnp_Command` pay, `vnp_TmnCode`, `vnp_Amount` (đồng × 100), `vnp_CreateDate` và `vnp_ExpireDate`
(giờ Việt Nam, hạn 15 phút), `vnp_CurrCode` VND, `vnp_IpAddr` (IPv4 của người trả, không có thì 127.0.0.1), `vnp_Locale`, `vnp_OrderInfo` (tiếng Việt không dấu), `vnp_OrderType` other, `vnp_ReturnUrl`, `vnp_TxnRef`.

## 7. Cấu hình

| Khóa | Mặc định | Ghi chú |
|---|---|---|
| `VnPay:TmnCode`, `VnPay:HashSecret` | trống | **Khóa sandbox của cửa hàng; chỉ đặt ở User Secrets hoặc biến môi trường** (`dotnet user-secrets set "VnPay:HashSecret" … --project src/DSVHVN.Api`, hoặc `VnPay__HashSecret`). Chưa đặt thì Api vẫn chạy, nhưng tạo thanh toán trả 503 và IPN bị từ chối. Test dùng khóa thử riêng, không phải khóa của VNPay |
| `VnPay:PaymentUrl` | `https://sandbox.vnpayment.vn/paymentv2/vpcpay.html` | trang thanh toán sandbox |
| `VnPay:ReturnUrl` | `http://localhost:5080/api/v1/payments/vnpay/return` | địa chỉ trình duyệt của người trả mở được; khi triển khai đổi sang địa chỉ công khai của backend |
| `VnPay:Locale` | `vn` | `vn` hoặc `en` |
| `App:FrontendBaseUrl`, `App:PaymentResultPath` | `http://localhost:5173`, `/truong/ket-qua-thanh-toan` | nơi trang trả về chuyển tiếp tới |
| `Billing:Maintenance:Enabled`, `IntervalSeconds` | `true`, `60` | tác vụ nền |
| `Seed:Plans` | không có | gói mẫu cho môi trường phát triển |

**IPN và máy phát triển**: VNPay chỉ gọi được địa chỉ công khai, nên trên máy phát triển (localhost) IPN **không tới** và kết quả hiện "đang chờ xác nhận" cho tới khi hết hạn. Muốn thử đủ vòng với sandbox thật: mở đường hầm công khai tới
`/api/v1/payments/vnpay/ipn` rồi khai địa chỉ đó trong trang quản lý cửa hàng sandbox của VNPay. Không có khóa sandbox hoặc không có đường hầm thì thử bằng IPN giả có chữ ký (smoke test bên dưới).

## 8. Kiểm thử

| Test | Nội dung |
|---|---|
| `Unit/BillingRulesTests` | phân loại mua, ngày bắt đầu (gia hạn, IPN tới muộn, nâng cấp), số hóa đơn, giá hợp lệ, số tiền ×100, định dạng tiền và giờ, giờ chạy việc hết hạn |
| `Unit/VnPayGatewayTests` | địa chỉ thanh toán khớp **chữ ký tính độc lập** (HMAC-SHA512 của Python); số tiền, giờ Việt Nam, hạn 15 phút; chữ ký đúng, sai từng trường, thiếu, rỗng, sai khóa; chữ hoa thường; thiếu khóa cấu hình; địa chỉ chuyển tiếp |
| `Unit/PlanServiceTests` | tạo, sửa, lỗi theo trường, tên trùng không phân biệt dấu, gói giá 0 duy nhất, gói Miễn phí cố định, bật tắt, nhật ký, đếm trường đang dùng, đổi giá không hồi tố |
| `Unit/BillingServiceTests` | danh sách gói theo loại, báo giá (ba loại, cảnh báo giáo viên), tạo yêu cầu mua đủ ba dòng, địa chỉ VNPay có chữ ký, mua trùng bị chặn, số hóa đơn, thiếu khóa VNPay, hủy (yêu cầu, gói, các trường hợp không hủy được), gói Miễn phí, thanh toán lại thay giao dịch chờ, hóa đơn quá hạn, danh sách hóa đơn theo trường |
| `Unit/VnPayCallbackServiceTests` | IPN thành công mua, nâng cấp, gia hạn (cả khi IPN tới sau hạn); thất bại; từng cặp mã; IPN lặp 5 lần không đổi gì; sai chữ ký (4 kiểu), mã lạ, thiếu mã, lệch tiền, mã giao dịch của cổng trùng; IPN thành công tới muộn sau tác vụ nền, sau hủy; trả hai lần; trang trả về không đổi dữ liệu; trang kết quả |
| `Unit/BillingMaintenanceTests` | giao dịch treo (ranh giới 30 phút), hóa đơn quá hạn kèm đăng ký, thứ tự việc, giao dịch chờ gần đây giữ hóa đơn, gói hết hạn đúng ngày, gia hạn chờ |
| `Integration/BillingApiTests` (Api thật, SQLite) | quản lý gói qua HTTP, mua gói tới IPN tới trang kết quả, IPN lặp và sai, thất bại rồi thanh toán lại, hủy, gói Miễn phí, kiểm đầu vào, ma trận quyền (giáo viên, quản trị hệ thống, chưa đăng nhập), trường khác nhận 404, quản trị hệ thống xem hóa đơn, trang trả về 302, thiếu khóa VNPay → 503 |
| `Integration/BillingSqlServerTests` (database tạm của LocalDB) | cổng thanh toán khởi tạo; **chính CSDL từ chối** yêu cầu mua chưa trả thứ hai, giao dịch chờ thứ hai, tên gói trùng, số hóa đơn trùng; mua trọn vẹn trên lược đồ thật; **8 IPN giống nhau cùng lúc chỉ đổi dữ liệu một lần**; IPN thành công và thất bại tranh nhau; **6 lần mua cùng lúc của một trường chỉ tạo một yêu cầu**; **8 trường mua cùng lúc nhận 8 số hóa đơn khác nhau**; tác vụ nền trên SQL Server |
| `Integration/SqlServerDatabaseTests` | cập nhật danh sách UNIQUE có lọc (8) |

Smoke test trên Api chạy thật: `scripts/smoke-goi-thanh-toan.sh` (cách chạy ở đầu tệp; dùng database tạm tên bắt đầu `dsvhvn_tam_a2_`, khóa VNPay thử không phải khóa thật, IPN giả có chữ ký tính bằng Python); 116 bước chạy đúng trên Api thật và database tạm: quản lý gói, hai trường, mua, chữ ký của địa chỉ VNPay tính lại độc lập, IPN sai bị từ chối, thất bại rồi thanh toán lại, IPN lặp, gia hạn, nâng cấp, hủy, gói Miễn phí, tách biệt giữa các trường, quyền, nhật ký.

## 9. Chưa làm ở luồng này

- Giao diện: màn hình Gói dịch vụ, tab Gói và hạn mức, tab Hóa đơn và giao dịch, Chọn gói và thanh toán, Kết quả thanh toán (web quản trị).
- Tổng quan doanh thu của quản trị hệ thống (thuộc luồng Quản trị và tổng quan; dữ liệu đã có: giao dịch `SUCCEEDED` theo ngày `paid_at`).
- Hoàn tiền, thanh toán tự động trừ tiền định kỳ, các cổng khác ngoài VNPay, hóa đơn giá trị gia tăng, gửi hóa đơn qua email: ngoài phạm vi.
- Thử với VNPay sandbox thật cần khóa của cửa hàng sandbox và địa chỉ IPN công khai (mục 7).
