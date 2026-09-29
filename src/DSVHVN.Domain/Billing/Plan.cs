using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Organizations;

namespace DSVHVN.Domain.Billing;

/// <summary>
/// Bảng <c>plans</c>, tạo từ luồng nền tảng để có "kiểm gói và hạn mức" từ đầu. Màn hình quản lý gói thuộc luồng gói dịch vụ.
/// Dữ liệu khởi tạo có đúng một gói giá 0 là gói "Miễn phí".
/// </summary>
public class Plan
{
    public const string FreePlanName = "Miễn phí";

    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Đồng Việt Nam, đã gồm thuế. Lưu decimal(12,2) theo DBML.</summary>
    public decimal Price { get; set; }

    public int DurationDays { get; set; }

    public int MaxTeachers { get; set; }

    public int MaxAiRequests { get; set; }

    /// <summary>Còn bán không; tắt thì không bán mới, đăng ký đang có vẫn dùng tới hết hạn.</summary>
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

    public SubscriptionStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Gói hiệu lực = ACTIVE và <c>start_date</c> ≤ hôm nay ≤ <c>end_date</c>.</summary>
    public bool IsEffectiveOn(DateOnly today) =>
        Status == SubscriptionStatus.ACTIVE && StartDate <= today && today <= EndDate;

    /// <summary><c>end_date</c> = <c>start_date</c> + <c>duration_days</c> − 1.</summary>
    public static DateOnly EndDateFor(DateOnly startDate, int durationDays) => startDate.AddDays(durationDays - 1);
}
