using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DSVHVN.Application.Billing;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Payments;

/// <summary>
/// Cấu hình mục <c>VnPay</c>. <see cref="TmnCode"/> và <see cref="HashSecret"/> là khóa sandbox của cửa hàng: không ghi vào appsettings,
/// đặt bằng User Secrets (<c>dotnet user-secrets set "VnPay:HashSecret" …</c>) hoặc biến môi trường <c>VnPay__HashSecret</c>.
/// Thiếu một trong hai thì backend vẫn chạy nhưng không tạo được thanh toán và từ chối mọi IPN.
/// </summary>
public sealed class VnPayOptions
{
    public const string Section = "VnPay";

    /// <summary>Địa chỉ trang thanh toán sandbox của VNPay.</summary>
    public const string SandboxPaymentUrl = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html";

    public string TmnCode { get; set; } = string.Empty;

    public string HashSecret { get; set; } = string.Empty;

    public string PaymentUrl { get; set; } = SandboxPaymentUrl;

    /// <summary>
    /// Địa chỉ VNPay đưa trình duyệt về sau khi trả: trang trả về của backend (<c>/api/v1/payments/vnpay/return</c>), nơi kiểm chữ ký rồi chuyển sang trang
    /// kết quả của web quản trị. Phải mở được từ trình duyệt của người trả. Địa chỉ nhận IPN khai trong trang quản lý cửa hàng của VNPay, không gửi theo yêu cầu.
    /// </summary>
    public string ReturnUrl { get; set; } = "http://localhost:5080/api/v1/payments/vnpay/return";

    /// <summary>Ngôn ngữ trang thanh toán của VNPay: <c>vn</c> hoặc <c>en</c>.</summary>
    public string Locale { get; set; } = "vn";
}

/// <summary>
/// Dựng địa chỉ thanh toán VNPay 2.1.0 và kiểm chữ ký dữ liệu VNPay gửi về. Chữ ký là HMAC-SHA512 (khóa là <c>HashSecret</c>) trên chuỗi
/// <c>khóa=giá trị</c> của các tham số <c>vnp_*</c> khác rỗng, sắp theo tên khóa tăng dần, giá trị mã hóa URL, nối bằng <c>&amp;</c>
/// (không gồm <c>vnp_SecureHash</c>, <c>vnp_SecureHashType</c>), viết thường dạng hex. Không gọi mạng.
/// </summary>
public sealed class VnPayGateway(IOptions<VnPayOptions> options, IOptions<AppLinkOptions> links) : IVnPayGateway
{
    private const string SecureHashKey = "vnp_SecureHash";
    private const string SecureHashTypeKey = "vnp_SecureHashType";

    private VnPayOptions Options => options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Options.TmnCode) && !string.IsNullOrWhiteSpace(Options.HashSecret);

    public string CreatePaymentUrl(VnPayPaymentRequest request)
    {
        if (!IsConfigured) throw new InvalidOperationException("Chưa cấu hình VnPay:TmnCode và VnPay:HashSecret.");

        var parameters = new Dictionary<string, string>
        {
            ["vnp_Version"] = "2.1.0",
            ["vnp_Command"] = "pay",
            ["vnp_TmnCode"] = Options.TmnCode,
            ["vnp_Amount"] = BillingRules.VnPayAmount(request.Amount).ToString(CultureInfo.InvariantCulture),
            ["vnp_CreateDate"] = Moment(request.CreatedAtUtc),
            ["vnp_CurrCode"] = "VND",
            ["vnp_IpAddr"] = request.IpAddress,
            ["vnp_Locale"] = Options.Locale,
            ["vnp_OrderInfo"] = request.OrderInfo,
            ["vnp_OrderType"] = "other",
            ["vnp_ReturnUrl"] = Options.ReturnUrl,
            ["vnp_TxnRef"] = request.PaymentId.ToString(CultureInfo.InvariantCulture),
            ["vnp_ExpireDate"] = Moment(request.ExpiresAtUtc),
        };
        var data = Canonical(parameters);
        return $"{Options.PaymentUrl}?{data}&{SecureHashKey}={Sign(data)}";
    }

    public VnPayCallback Parse(IReadOnlyDictionary<string, string> query)
    {
        var parameters = query.Where(p => p.Key.StartsWith("vnp_", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        var received = parameters.GetValueOrDefault(SecureHashKey);
        var valid = false;
        if (IsConfigured && !string.IsNullOrEmpty(received))
        {
            var expected = Encoding.ASCII.GetBytes(Sign(Canonical(parameters)));
            valid = CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(received.Trim().ToLowerInvariant()));
        }

        return new VnPayCallback(
            valid,
            long.TryParse(parameters.GetValueOrDefault("vnp_TxnRef"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null,
            long.TryParse(parameters.GetValueOrDefault("vnp_Amount"), NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ? amount : null,
            parameters.GetValueOrDefault("vnp_TmnCode"),
            parameters.GetValueOrDefault("vnp_ResponseCode"),
            parameters.GetValueOrDefault("vnp_TransactionStatus"),
            parameters.GetValueOrDefault("vnp_TransactionNo"),
            parameters);
    }

    public string BuildResultPageUrl(long? paymentId, bool signatureValid)
    {
        var link = links.Value;
        var page = link.FrontendBaseUrl.TrimEnd('/') + link.PaymentResultPath;
        return signatureValid && paymentId is { } id
            ? $"{page}?payment={id.ToString(CultureInfo.InvariantCulture)}"
            : $"{page}?error=invalid";
    }

    /// <summary>Chuỗi cần ký: tham số <c>vnp_*</c> khác rỗng (trừ hai tham số chữ ký), sắp theo tên khóa, giá trị mã hóa URL.</summary>
    public static string Canonical(IEnumerable<KeyValuePair<string, string>> parameters) => string.Join('&', parameters
        .Where(p => p.Key.StartsWith("vnp_", StringComparison.Ordinal) && p.Key is not SecureHashKey and not SecureHashTypeKey
                    && !string.IsNullOrEmpty(p.Value))
        .OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => $"{p.Key}={WebUtility.UrlEncode(p.Value)}"));

    private string Sign(string data) => Sign(data, Options.HashSecret);

    /// <summary>HMAC-SHA512 của <paramref name="data"/> bằng <paramref name="secret"/>, hex chữ thường.</summary>
    public static string Sign(string data, string secret)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    /// <summary>VNPay đọc ngày giờ theo giờ Việt Nam, dạng <c>yyyyMMddHHmmss</c>.</summary>
    private static string Moment(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(VietnamTime.Offset)
            .ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
}
