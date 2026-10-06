using System.Globalization;

namespace DSVHVN.Domain.Rules;

/// <summary>Loại giao dịch mua gói của trường: mua mới, gia hạn (mua lại) hoặc nâng cấp sang gói giá cao hơn.</summary>
public enum PurchaseKind
{
    PURCHASE,
    RENEW,
    UPGRADE,
}

/// <summary>
/// Hằng số và phép tính thuần của gói dịch vụ, hóa đơn và thanh toán. Đổi giá trị ở đây là đổi quy tắc nghiệp vụ
/// (giới hạn gói, hạn thanh toán, hạn giao dịch treo, cách đánh số hóa đơn, cách phân loại mua, gia hạn, nâng cấp).
/// </summary>
public static class BillingRules
{
    /// <summary>Mã cổng VNPay trong <c>payment_providers.code</c>; cổng duy nhất đang dùng (các cổng khác có sẵn nhưng tắt).</summary>
    public const string VnPayProviderCode = "VNPAY";

    public const int PlanNameMaxLength = 100;

    /// <summary>
    /// Giá gói tối đa (đồng): VNPay không nhận giao dịch từ một tỷ đồng, và cột lưu <c>decimal(12,2)</c>.
    /// Giá là số nguyên đồng, đã gồm thuế.
    /// </summary>
    public const decimal PlanMaxPrice = 999_999_999m;

    public const int DurationMinDays = 1;
    public const int DurationMaxDays = 730;

    public const int MaxTeachersMin = 1;
    public const int MaxTeachersMax = 10_000;

    public const int MaxAiRequestsMin = 0;
    public const int MaxAiRequestsMax = 1_000_000;

    /// <summary>Hóa đơn chưa trả thanh toán lại được tới <c>issued_at</c> + 24 giờ; quá hạn và không còn giao dịch treo thì bị hủy.</summary>
    public static readonly TimeSpan InvoiceDueIn = TimeSpan.FromHours(24);

    /// <summary>Thời hạn thanh toán gửi cổng VNPay cho mỗi giao dịch.</summary>
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(15);

    /// <summary>Giao dịch chờ quá thời gian này mà không nhận IPN thì tác vụ nền chuyển thất bại.</summary>
    public static readonly TimeSpan PendingPaymentTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Tác vụ nền chuyển gói quá hạn sang hết hạn từ 00:05 giờ Việt Nam mỗi ngày.</summary>
    public static readonly TimeOnly ExpiryRunTime = new(0, 5);

    /// <summary>
    /// Đến lúc chạy việc chuyển gói quá hạn sang hết hạn chưa: đã qua 00:05 giờ Việt Nam của hôm nay và chưa chạy trong ngày hôm nay.
    /// Mở máy sau 00:05 thì chạy ngay lần đầu; trong lúc đó gói vẫn tính hết hạn đúng theo ngày nên không ảnh hưởng quyền lợi.
    /// </summary>
    public static bool ExpiryDue(DateTimeOffset utcNow, DateOnly? lastRunDay)
    {
        var local = utcNow.ToOffset(VietnamTime.Offset);
        return TimeOnly.FromDateTime(local.DateTime) >= ExpiryRunTime && lastRunDay != DateOnly.FromDateTime(local.DateTime);
    }

    /// <summary>
    /// Phân loại theo gói hiệu lực hiện tại: chưa có thì mua mới; gói mới giá cao hơn thì nâng cấp; còn lại (cùng gói hoặc gói giá thấp hơn
    /// hay bằng) là gia hạn. Hệ thống tự phân loại, người dùng không chọn.
    /// </summary>
    public static PurchaseKind Classify(decimal? currentPrice, decimal newPrice) =>
        currentPrice is null ? PurchaseKind.PURCHASE
        : newPrice > currentPrice ? PurchaseKind.UPGRADE
        : PurchaseKind.RENEW;

    /// <summary>
    /// Ngày bắt đầu của gói mới: nâng cấp bắt đầu từ ngày nhận IPN thành công (gói cũ bị hủy). Mua mới và gia hạn bắt đầu từ ngày nhận IPN,
    /// trừ khi trường còn gói chưa hết hạn (<paramref name="lastEndDate"/> là ngày hết hạn cuối cùng của các gói đang hiệu lực hoặc chờ bắt đầu):
    /// khi đó bắt đầu từ ngày sau ngày đó. IPN tới sau ngày đó thì bắt đầu từ ngày nhận IPN.
    /// </summary>
    public static DateOnly StartDateFor(PurchaseKind kind, DateOnly today, DateOnly? lastEndDate) =>
        kind != PurchaseKind.UPGRADE && lastEndDate is { } last && last.AddDays(1) > today ? last.AddDays(1) : today;

    /// <summary>Giá bán là số nguyên đồng trong khoảng cho phép.</summary>
    public static bool IsValidPrice(decimal price) =>
        price >= 0m && price <= PlanMaxPrice && decimal.Truncate(price) == price;

    /// <summary>VNPay nhận số tiền nhân 100 (không có phần thập phân).</summary>
    public static long VnPayAmount(decimal amount) => checked((long)decimal.Truncate(amount) * 100);

    /// <summary>Tiền kiểu Việt Nam, dấu chấm ngăn nhóm nghìn: 1.200.000.</summary>
    public static string FormatMoney(decimal amount) =>
        decimal.Truncate(amount).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');

    public static string FormatDate(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>Giờ và ngày theo giờ Việt Nam, dạng "14:30 ngày 08/10/2026".</summary>
    public static string FormatMoment(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(VietnamTime.Offset)
            .ToString("HH:mm 'ngày' dd/MM/yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Số hóa đơn: <c>HD</c> + ngày <c>yyyyMMdd</c> + <c>-</c> + số thứ tự 6 chữ số trong ngày, ví dụ HD20261008-000012.</summary>
public static class InvoiceNumber
{
    public const string Prefix = "HD";
    public const int SequenceDigits = 6;

    /// <summary>Phần đầu chung của mọi số hóa đơn trong một ngày (ngày theo lịch Việt Nam).</summary>
    public static string DayPrefix(DateOnly date) =>
        $"{Prefix}{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-";

    public static string Format(DateOnly date, int sequence) =>
        DayPrefix(date) + sequence.ToString("D" + SequenceDigits, CultureInfo.InvariantCulture);

    /// <summary>Số thứ tự trong số hóa đơn của ngày; 0 nếu số hóa đơn không đúng dạng của ngày đó.</summary>
    public static int SequenceOf(string? invoiceNumber, DateOnly date)
    {
        var prefix = DayPrefix(date);
        if (invoiceNumber is null || !invoiceNumber.StartsWith(prefix, StringComparison.Ordinal)) return 0;
        var tail = invoiceNumber[prefix.Length..];
        return tail.Length == SequenceDigits && int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
