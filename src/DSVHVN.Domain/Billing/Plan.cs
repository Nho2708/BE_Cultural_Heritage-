using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Organizations;

namespace DSVHVN.Domain.Billing;

/// <summary>
/// Bảng <c>plans</c>: gói dịch vụ bán cho trường. Có từ nền tảng để kiểm gói và hạn mức ngay từ đầu;
/// màn hình quản lý gói thuộc chức năng gói dịch vụ. Dữ liệu khởi tạo có đúng một gói giá 0 là gói "Miễn phí".
/// CSDL để các cột giá, thời hạn, hạn mức được NULL; chức năng quản lý gói bắt buộc nhập đủ.
/// </summary>
public class Plan
{
    public const string FreePlanName = "Miễn phí";

    public int Id { get; set; }

    public string? Name { get; set; }

    /// <summary>Đồng Việt Nam, đã gồm thuế. Lưu decimal(12,2).</summary>
    public decimal? Price { get; set; }

    public int? DurationDays { get; set; }

    public int? MaxTeachers { get; set; }

    public int? MaxAiRequests { get; set; }

    /// <summary>Còn bán không; tắt thì không bán mới, đăng ký đang có vẫn dùng tới hết hạn. bit NOT NULL DEFAULT 1.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Bảng <c>subscriptions</c>: gói của từng trường theo thời gian.
/// <c>start_date</c>/<c>end_date</c> là ngày theo lịch Việt Nam; gói dùng tới hết ngày <c>end_date</c>.
/// Đăng ký PENDING (chờ thanh toán) chưa có ngày: ngày được đặt khi IPN thành công.
/// </summary>
public class Subscription : IHasCreatedAt
{
    public long Id { get; set; }

    public long OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int PlanId { get; set; }
    public Plan? Plan { get; set; }

    public DateOnly? StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public SubscriptionStatus? Status { get; set; }

    public DateTime? CreatedAt { get; set; }

    /// <summary>Gói hiệu lực = ACTIVE và <c>start_date</c> ≤ hôm nay ≤ <c>end_date</c>.</summary>
    public bool IsEffectiveOn(DateOnly today) =>
        Status == SubscriptionStatus.ACTIVE && StartDate <= today && today <= EndDate;

    /// <summary><c>end_date</c> = <c>start_date</c> + <c>duration_days</c> − 1.</summary>
    public static DateOnly EndDateFor(DateOnly startDate, int durationDays) => startDate.AddDays(durationDays - 1);
}

/// <summary>
/// Bảng <c>invoices</c>: hóa đơn phát sinh khi trường đăng ký hoặc gia hạn gói. Một hóa đơn có thể có nhiều lần thanh toán
/// (lần đầu lỗi, lần sau thành công).
/// </summary>
public class Invoice
{
    public long Id { get; set; }

    public long OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Đăng ký gói mà hóa đơn thu tiền (đăng ký PENDING khi chưa trả).</summary>
    public long SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }

    /// <summary>Số hóa đơn, không trùng.</summary>
    public string? InvoiceNumber { get; set; }

    public decimal? Amount { get; set; }

    public string? Description { get; set; }

    public InvoiceStatus? Status { get; set; }

    public DateTime? IssuedAt { get; set; }

    public DateTime? DueAt { get; set; }
}

/// <summary>Bảng <c>payment_providers</c>: danh mục cổng thanh toán (VNPAY, MOMO, ZALOPAY, BANK_TRANSFER).</summary>
public class PaymentProvider
{
    public int Id { get; set; }

    /// <summary>Mã cổng dùng trong code, không trùng.</summary>
    public string? Code { get; set; }

    public string? Name { get; set; }

    /// <summary>Cổng còn được dùng không. bit NOT NULL DEFAULT 1.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Bảng <c>payments</c>: từng giao dịch thanh toán cho một hóa đơn, kèm mã giao dịch phía cổng và dữ liệu IPN để đối soát.
/// (<c>provider_id</c>, <c>provider_transaction_id</c>) không trùng khi đã có mã giao dịch.
/// </summary>
public class Payment
{
    public long Id { get; set; }

    public long InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public int ProviderId { get; set; }
    public PaymentProvider? Provider { get; set; }

    public string? ProviderTransactionId { get; set; }

    public decimal? Amount { get; set; }

    public PaymentStatus? Status { get; set; }

    /// <summary>Toàn bộ dữ liệu callback / IPN (chuỗi JSON).</summary>
    public string? RawResponse { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime? CreatedAt { get; set; }
}
