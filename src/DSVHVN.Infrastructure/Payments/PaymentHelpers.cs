using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DSVHVN.Infrastructure.Email;

namespace DSVHVN.Infrastructure.Payments;

/// <summary>Các hàm dùng chung của các cổng MoMo và PayOS: chữ ký HMAC-SHA256 và địa chỉ trang kết quả của web quản trị.</summary>
internal static class PaymentHelpers
{
    /// <summary>HMAC-SHA256 của <paramref name="data"/> bằng <paramref name="secret"/>, hex chữ thường.</summary>
    public static string SignSha256(string data, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    /// <summary>So chữ ký không phụ thuộc hoa thường và theo thời gian không đổi.</summary>
    public static bool SignatureMatches(string expectedHex, string? received) =>
        !string.IsNullOrEmpty(received)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedHex), Encoding.ASCII.GetBytes(received.Trim().ToLowerInvariant()));

    /// <summary>Trang Kết quả thanh toán của web quản trị: <c>?payment={id}</c> khi hợp lệ, <c>?error=invalid</c> khi không.</summary>
    public static string ResultPageUrl(AppLinkOptions link, long? paymentId, bool valid)
    {
        var page = link.FrontendBaseUrl.TrimEnd('/') + link.PaymentResultPath;
        return valid && paymentId is { } id ? $"{page}?payment={id.ToString(CultureInfo.InvariantCulture)}" : $"{page}?error=invalid";
    }

    /// <summary>Thời gian chờ tối đa mỗi lần gọi cổng.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);
}
