using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using FluentValidation;

namespace DSVHVN.Application.Billing;

// ---- Gói dịch vụ (quản trị hệ thống) ----

/// <summary>Tạo hoặc sửa gói (khung bên phải của màn hình Gói dịch vụ). Bật, tắt bán đi qua thao tác riêng.</summary>
public sealed record PlanRequest(string? Name, decimal? Price, int? DurationDays, int? MaxTeachers, int? MaxAiRequests);

/// <summary>Dòng của bảng Gói dịch vụ: thông tin gói, đang bán không và số trường đang dùng (có gói hiệu lực thuộc gói này).</summary>
public sealed record PlanDto(
    int Id,
    string? Name,
    decimal? Price,
    int? DurationDays,
    int? MaxTeachers,
    int? MaxAiRequests,
    bool IsActive,
    bool IsFree,
    int SchoolCount);

public sealed class PlanRequestValidator : AbstractValidator<PlanRequest>
{
    public PlanRequestValidator()
    {
        RuleFor(x => x.Name).RequiredText("tên gói", "Tên gói", BillingRules.PlanNameMaxLength);

        var priceRequired = Messages.Required("giá gói");
        var priceInvalid = Messages.Invalid("Giá gói",
            $"phải là số nguyên đồng từ 0 đến {BillingRules.FormatMoney(BillingRules.PlanMaxPrice)}");
        RuleFor(x => x.Price).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(priceRequired.Code).WithMessage(priceRequired.Text)
            .Must(p => BillingRules.IsValidPrice(p!.Value)).WithErrorCode(priceInvalid.Code).WithMessage(priceInvalid.Text);

        RuleFor(x => x.DurationDays).Range("thời hạn", "Thời hạn", BillingRules.DurationMinDays, BillingRules.DurationMaxDays, " ngày");
        RuleFor(x => x.MaxTeachers).Range("số giáo viên tối đa", "Số giáo viên tối đa", BillingRules.MaxTeachersMin, BillingRules.MaxTeachersMax);
        RuleFor(x => x.MaxAiRequests).Range("số lượt AI mỗi kỳ", "Số lượt AI mỗi kỳ", BillingRules.MaxAiRequestsMin, BillingRules.MaxAiRequestsMax);
    }
}

public static class BillingValidationRules
{
    /// <summary>Số nguyên bắt buộc trong khoảng đóng [min, max].</summary>
    public static IRuleBuilderOptions<T, int?> Range<T>(this IRuleBuilderInitial<T, int?> rule, string field, string label, int min, int max,
        string unit = "")
    {
        var required = Messages.Required(field);
        var invalid = Messages.Invalid(label, $"phải là số nguyên từ {min} đến {max}{unit}");
        return rule.Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(required.Code).WithMessage(required.Text)
            .InclusiveBetween(min, max).WithErrorCode(invalid.Code).WithMessage(invalid.Text);
    }
}

// ---- Gói của trường ----

/// <summary>Đăng ký gói của trường kèm thông tin gói, cho tab Gói và hạn mức.</summary>
public sealed record SubscriptionDetailDto(
    long Id,
    int PlanId,
    string? PlanName,
    decimal? Price,
    SubscriptionStatus? Status,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool IsEffective,
    bool IsFree,
    int? DaysLeft,
    int? MaxTeachers,
    int? MaxAiRequests);

/// <summary>Yêu cầu mua đang chờ thanh toán của trường (tối đa một): gói, hóa đơn và hạn thanh toán.</summary>
public sealed record PendingPurchaseDto(
    long SubscriptionId,
    int PlanId,
    string? PlanName,
    long? InvoiceId,
    string? InvoiceNumber,
    decimal? Amount,
    DateTime? DueAt,
    bool HasWaitingPayment);

/// <summary>
/// Tổng quan tab Gói và hạn mức: gói đang dùng (hoặc gói gần nhất đã hết hạn, đã hủy), gói gia hạn chờ bắt đầu, yêu cầu mua chưa thanh toán,
/// bộ đếm giáo viên và lượt AI, và các nút nào được bấm.
/// </summary>
public sealed record BillingOverviewDto(
    SubscriptionDetailDto? Current,
    SubscriptionDetailDto? Upcoming,
    PendingPurchaseDto? Pending,
    bool HasEffectivePlan,
    TeacherQuotaDto TeacherQuota,
    AiQuotaDto AiQuota,
    bool CanActivateFreePlan,
    bool CanCancelCurrent,
    string? CancelConfirmation);

/// <summary>Một gói đang bán trên màn hình Chọn gói: loại thao tác nếu mua và có mua được không.</summary>
public sealed record PlanOfferDto(
    int Id,
    string? Name,
    decimal? Price,
    int? DurationDays,
    int? MaxTeachers,
    int? MaxAiRequests,
    bool IsCurrent,
    PurchaseKind? Kind,
    bool Available,
    string? UnavailableReason);

/// <summary>Khối xác nhận sau khi chọn gói: loại, số tiền, ngày hiệu lực dự kiến, câu xác nhận và cảnh báo vượt hạn mức giáo viên.</summary>
public sealed record PurchaseQuoteDto(
    int PlanId,
    string? PlanName,
    PurchaseKind Kind,
    decimal Amount,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate,
    string Confirmation,
    string? TeacherWarning);

public sealed record CheckoutRequest(int? PlanId);

public sealed class CheckoutRequestValidator : AbstractValidator<CheckoutRequest>
{
    public CheckoutRequestValidator()
    {
        var required = Messages.Required("gói cần mua");
        RuleFor(x => x.PlanId).NotNull().WithErrorCode(required.Code).WithMessage(required.Text);
    }
}

// ---- Hóa đơn và giao dịch ----

public sealed record InvoiceDto(
    long Id,
    long OrganizationId,
    string? OrganizationName,
    string? InvoiceNumber,
    string? Description,
    decimal? Amount,
    InvoiceStatus? Status,
    DateTime? IssuedAt,
    DateTime? DueAt,
    long SubscriptionId,
    string? PlanName,
    SubscriptionStatus? SubscriptionStatus,
    bool CanPay);

public sealed record PaymentDto(
    long Id,
    string? ProviderCode,
    string? ProviderTransactionId,
    decimal? Amount,
    PaymentStatus? Status,
    DateTime? CreatedAt,
    DateTime? PaidAt);

public sealed record InvoiceDetailDto(InvoiceDto Invoice, IReadOnlyList<PaymentDto> Payments);

public sealed record InvoiceListQuery(InvoiceStatus? Status, long? OrganizationId, DateTimeOffset? From, DateTimeOffset? To, int? Page, int? PageSize);

/// <summary>Kết quả tạo yêu cầu thanh toán: hóa đơn, giao dịch và địa chỉ VNPay để chuyển trình duyệt tới.</summary>
public sealed record PaymentStartDto(
    InvoiceDto Invoice,
    long PaymentId,
    string PaymentUrl,
    DateTime ExpiresAt,
    PurchaseKind? Kind);

/// <summary>Kết quả của một lần thanh toán cho trang kết quả: chỉ đọc trạng thái do IPN ghi nhận.</summary>
public enum PaymentOutcome
{
    /// <summary>Hóa đơn đã thanh toán, gói đã kích hoạt.</summary>
    PAID,

    /// <summary>Thất bại, hủy hoặc quá hạn; hóa đơn chưa thanh toán.</summary>
    UNPAID,

    /// <summary>Chưa nhận IPN.</summary>
    PROCESSING,
}

public sealed record PaymentResultDto(
    PaymentOutcome Outcome,
    string Message,
    InvoiceDto Invoice,
    PaymentDto Payment,
    SubscriptionDetailDto? Subscription,
    bool CanPayAgain);
