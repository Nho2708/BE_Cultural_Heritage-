using System.Web;
using DSVHVN.Infrastructure.Payments;

namespace DSVHVN.Tests.Support;

/// <summary>
/// Dữ liệu VNPay giả cho kiểm thử: khóa thử (không phải khóa thật của VNPay), dựng tham số IPN có chữ ký và đọc địa chỉ thanh toán backend trả về.
/// Ký bằng đúng hàm của <see cref="VnPayGateway"/>; phép ký được đối chiếu riêng với một kết quả tính sẵn ở <c>VnPayGatewayTests</c>.
/// </summary>
public static class VnPayTestKit
{
    public const string TmnCode = "TESTTMN1";
    public const string HashSecret = "test-only-secret-for-hmac-sha512-0123456789";

    /// <summary>
    /// Tham số IPN hoặc trang trả về của VNPay cho giao dịch <paramref name="paymentId"/> (<c>vnp_TxnRef</c>). <paramref name="priceVnd"/> là số tiền đồng
    /// (hàm tự nhân 100 như VNPay); <paramref name="rawAmount"/> để ép số tiền lệch.
    /// </summary>
    public static Dictionary<string, string> Callback(long paymentId, decimal priceVnd, string responseCode = "00", string transactionStatus = "00",
        string? transactionNo = "14000001", string secret = HashSecret, bool sign = true, long? rawAmount = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["vnp_TmnCode"] = TmnCode,
            ["vnp_Amount"] = (rawAmount ?? (long)priceVnd * 100).ToString(),
            ["vnp_BankCode"] = "NCB",
            ["vnp_BankTranNo"] = "VNP14000001",
            ["vnp_CardType"] = "ATM",
            ["vnp_OrderInfo"] = "thanh toan hoa don",
            ["vnp_PayDate"] = "20261008103000",
            ["vnp_ResponseCode"] = responseCode,
            ["vnp_TransactionStatus"] = transactionStatus,
            ["vnp_TxnRef"] = paymentId.ToString(),
        };
        if (transactionNo is not null) parameters["vnp_TransactionNo"] = transactionNo;
        if (sign)
        {
            parameters["vnp_SecureHashType"] = "HmacSHA512";
            parameters["vnp_SecureHash"] = VnPayGateway.Sign(VnPayGateway.Canonical(parameters), secret);
        }
        return parameters;
    }

    public static string QueryString(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    /// <summary>Địa chỉ gọi IPN của backend với tham số đã dựng.</summary>
    public static string IpnPath(IEnumerable<KeyValuePair<string, string>> parameters) => "/api/v1/payments/vnpay/ipn?" + QueryString(parameters);

    public static string ReturnPath(IEnumerable<KeyValuePair<string, string>> parameters) => "/api/v1/payments/vnpay/return?" + QueryString(parameters);

    /// <summary>Tham số trên địa chỉ thanh toán VNPay mà backend dựng.</summary>
    public static Dictionary<string, string> ParseUrl(string paymentUrl)
    {
        var query = HttpUtility.ParseQueryString(new Uri(paymentUrl).Query);
        return query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => query[k]!);
    }
}
