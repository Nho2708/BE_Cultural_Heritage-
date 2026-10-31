using DSVHVN.Application.Common;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Billing;

/// <summary>Mã thông điệp riêng của luồng Gói và thanh toán (trả ở <c>errorCode</c>).</summary>
public static class BillingMessageCodes
{
    public const string PlanNotOnSale = "PLAN_NOT_ON_SALE";
    public const string PendingPurchaseExists = "PENDING_PURCHASE_EXISTS";
    public const string PaymentGatewayUnavailable = "PAYMENT_GATEWAY_UNAVAILABLE";
    public const string PaymentConfirm = "PAYMENT_CONFIRM";
    public const string PaymentSucceeded = "PAYMENT_SUCCEEDED";
    public const string PaymentFailed = "PAYMENT_FAILED";
    public const string PaymentWaiting = "PAYMENT_WAITING";
    public const string CancelPlanConfirm = "CANCEL_PLAN_CONFIRM";
}

/// <summary>
/// Thông điệp của luồng Gói và thanh toán VNPay. Câu xác nhận trước khi chuyển sang VNPay, câu ở trang kết quả thanh toán và câu xác nhận hủy gói
/// chép nguyên danh mục thông điệp đã chốt, phần <c>{…}</c> điền sẵn; câu báo thao tác bị chặn theo khuôn chung "Không thể … . …".
/// Lời với quản trị viên xưng "bạn".
/// </summary>
public static class BillingMessages
{
    // ---- Danh mục đã chốt ----

    /// <summary>Hộp xác nhận trước khi chuyển sang VNPay; <c>{hieu_luc}</c> theo loại mua.</summary>
    public static AppMessage PaymentConfirm(decimal amount, string planName, PurchaseKind kind, DateOnly? startDate, Plan? currentPlan = null) => new(
        BillingMessageCodes.PaymentConfirm,
        $"Trường sẽ thanh toán {BillingRules.FormatMoney(amount)} đồng cho gói {planName} qua VNPay. {EffectOf(kind, startDate, currentPlan)} Tiếp tục?");

    /// <summary>Phần <c>{hieu_luc}</c> của hộp xác nhận. Nâng cấp từ gói Miễn phí không có tiền để hoàn nên chỉ báo gói Miễn phí kết thúc.</summary>
    public static string EffectOf(PurchaseKind kind, DateOnly? startDate, Plan? currentPlan = null) => kind switch
    {
        PurchaseKind.RENEW when startDate is { } start => $"Gói mới bắt đầu từ ngày {BillingRules.FormatDate(start)}.",
        PurchaseKind.UPGRADE when currentPlan is { Price: 0m } =>
            "Gói mới có hiệu lực ngay. Gói Miễn phí sẽ kết thúc ngay khi gói mới có hiệu lực (không cộng dồn lượt AI cũ).",
        PurchaseKind.UPGRADE when currentPlan is not null =>
            $"Gói mới có hiệu lực ngay. Nâng cấp không hoàn tiền hoặc khấu trừ thời gian chưa dùng của gói {currentPlan.Name} hiện tại.",
        PurchaseKind.UPGRADE => "Gói mới có hiệu lực ngay, gói hiện tại kết thúc và không được hoàn phần chưa dùng.",
        _ => "Gói có hiệu lực từ ngày thanh toán.",
    };

    /// <summary>Trang kết quả: thanh toán thành công.</summary>
    public static AppMessage PaymentSucceeded(string planName, DateOnly start, DateOnly end) => new(
        BillingMessageCodes.PaymentSucceeded,
        $"Thanh toán thành công. Gói {planName} có hiệu lực từ ngày {BillingRules.FormatDate(start)} đến hết ngày {BillingRules.FormatDate(end)}.");

    /// <summary>Trang kết quả: thất bại hoặc quá thời hạn thanh toán, hóa đơn vẫn thanh toán lại được.</summary>
    public static AppMessage PaymentFailed(string invoiceNumber, DateTime dueAt) => new(
        BillingMessageCodes.PaymentFailed,
        $"Thanh toán chưa thành công. Hóa đơn {invoiceNumber} vẫn chờ thanh toán đến {BillingRules.FormatMoment(dueAt)}; bạn có thể thanh toán lại.");

    /// <summary>Trang kết quả: thất bại và hóa đơn đã quá hạn hoặc đã bị hủy, phải chọn gói và mua lại.</summary>
    public static AppMessage PaymentFailedInvoiceClosed(string invoiceNumber) => new(
        BillingMessageCodes.PaymentFailed,
        $"Thanh toán chưa thành công và hóa đơn {invoiceNumber} không còn thanh toán được. Bạn có thể chọn gói và thanh toán lại.");

    /// <summary>Trang kết quả: chưa nhận IPN.</summary>
    public static readonly AppMessage PaymentWaiting = new(
        BillingMessageCodes.PaymentWaiting, "Hệ thống đang chờ xác nhận từ VNPay. Trang sẽ tự cập nhật khi có kết quả.");

    /// <summary>Hộp xác nhận hủy gói trả phí đang hiệu lực.</summary>
    public static AppMessage CancelPlanConfirm(string planName) => new(
        BillingMessageCodes.CancelPlanConfirm,
        $"Bạn có chắc muốn hủy gói {planName}? Gói sẽ kết thúc ngay, không được hoàn tiền; trường sẽ không tạo hoặc nhân bản được bài học, " +
        "không dùng được AI, không tạo, nhân bản hoặc phát hành được bài kiểm tra và không thêm được tài khoản giáo viên cho tới khi mua gói mới " +
        "hoặc kích hoạt gói Miễn phí. Học sinh vẫn học và làm bài kiểm tra đã phát hành bình thường.");

    // ---- Thao tác bị chặn (khuôn chung) ----

    public static AppMessage PlanNameTaken(string planName) => new(MessageCodes.Duplicate, $"Tên gói \"{planName}\" đã tồn tại trong hệ thống.");

    public static AppMessage PlanNotOnSale(string planName) => new(BillingMessageCodes.PlanNotOnSale,
        $"Không thể mua gói. Gói {planName} hiện không được bán.");

    public static readonly AppMessage FreePlanNotSold = new(BillingMessageCodes.PlanNotOnSale,
        "Không thể mua gói. Gói Miễn phí không bán qua VNPay; hãy bấm \"Kích hoạt gói Miễn phí\" khi trường chưa có gói.");

    public static AppMessage PendingPurchaseExists(string? invoiceNumber, string planName) => new(BillingMessageCodes.PendingPurchaseExists,
        $"Không thể mua gói. Trường đã có yêu cầu mua gói {planName} chưa thanh toán" +
        (invoiceNumber is null ? "" : $" (hóa đơn {invoiceNumber})") + ". Hãy thanh toán hoặc hủy yêu cầu đó trước.");

    public static AppMessage RenewalAlreadyQueued(DateOnly start) => Messages.Blocked("mua gói",
        $"Trường đã có gói gia hạn bắt đầu từ ngày {BillingRules.FormatDate(start)}. Chỉ mua được gói mới khi chưa có gói gia hạn chờ, " +
        "hoặc khi nâng cấp sang gói giá cao hơn.");

    public static readonly AppMessage FreePlanNeedsNoPlan = Messages.Blocked("kích hoạt gói Miễn phí",
        "Trường đang có gói còn hiệu lực hoặc đã có gói gia hạn chờ bắt đầu.");

    public static readonly AppMessage FreePlanCannotBeCancelled = Messages.Blocked("hủy gói", "Gói Miễn phí không hủy được.");

    public static AppMessage QueuedPlanCannotBeCancelled(string planName) => Messages.Blocked("hủy gói",
        $"Gói gia hạn {planName} đã thanh toán nên không hủy được.");

    public static AppMessage CurrentPlanHasQueuedRenewal(DateOnly start) => Messages.Blocked("hủy gói",
        $"Trường đã thanh toán gói gia hạn bắt đầu từ ngày {BillingRules.FormatDate(start)} và gói đó không hủy được, nên chưa thể hủy gói hiện tại.");

    public static readonly AppMessage NothingToCancel = Messages.Blocked("hủy gói",
        "Chỉ hủy được gói trả phí đang hiệu lực hoặc yêu cầu mua gói chưa thanh toán.");

    public static AppMessage InvoiceNotPayable(string reason) => Messages.Blocked("thanh toán hóa đơn", reason);

    public static readonly AppMessage GatewayUnavailable = new(BillingMessageCodes.PaymentGatewayUnavailable,
        "Không thể thanh toán lúc này. Cổng VNPay chưa sẵn sàng, bạn hãy thử lại sau hoặc liên hệ quản trị viên.");

    public static AppMessage PlanNotFound(string action) => Messages.NotFound(action, "gói dịch vụ");

    public static AppMessage InvoiceNotFound(string action) => Messages.NotFound(action, "hóa đơn");

    public static AppMessage PaymentNotFound(string action) => Messages.NotFound(action, "giao dịch");

    public static AppMessage SubscriptionNotFound(string action) => Messages.NotFound(action, "gói của trường");

    public static readonly AppMessage FreePlanCannotBeDisabled = Messages.Blocked("tắt bán gói", "Gói Miễn phí không tắt được.");
}
