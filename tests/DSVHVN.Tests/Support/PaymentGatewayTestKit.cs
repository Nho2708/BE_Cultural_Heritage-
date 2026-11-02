using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DSVHVN.Tests.Support;

/// <summary>
/// Máy chủ giả của MoMo và PayOS: ghi lại yêu cầu cuối cùng gửi đi (thân, tiêu đề) và trả câu trả lời xếp sẵn, không gọi mạng.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    public string? LastBody { get; private set; }

    public Dictionary<string, string> LastHeaders { get; private set; } = [];

    public int Calls { get; private set; }

    public string ResponseJson { get; set; } = "{}";

    /// <summary>Đặt true để giả lập mất mạng.</summary>
    public bool Fail { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        if (Fail) throw new HttpRequestException("giả lập mất mạng");
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        LastHeaders = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// Dữ liệu MoMo và PayOS giả cho kiểm thử: khóa thử (không phải khóa thật), dựng IPN và webhook có chữ ký. Phép ký viết lại độc lập ở đây
/// (không dùng hàm của mã nguồn) để kiểm thử đối chiếu được chữ ký mà cổng tạo ra và chấp nhận.
/// </summary>
public sealed class PaymentGatewayTestKit
{
    public const string MoMoPartnerCode = "MOMOTEST";
    public const string MoMoAccessKey = "momo-test-access";
    public const string MoMoSecretKey = "momo-test-secret-0123456789";
    public const string PayOsChecksumKey = "payos-test-checksum-0123456789";
    public const long PayOsOffset = 5_000_000;

    public StubHandler MoMoHandler { get; } = new()
    {
        ResponseJson = """{"resultCode":0,"message":"Thanh cong","payUrl":"https://test-payment.momo.vn/pay/abc"}""",
    };

    public StubHandler PayOsHandler { get; } = new()
    {
        ResponseJson = """{"code":"00","desc":"success","data":{"checkoutUrl":"https://pay.payos.vn/web/abc","paymentLinkId":"link-1"}}""",
    };

    public static string Hmac(string data, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    /// <summary>Tham số IPN (hoặc query trang trả về) của MoMo cho giao dịch <paramref name="paymentId"/>; <paramref name="rawAmount"/> ép số tiền lệch.</summary>
    public static Dictionary<string, string> MoMoCallback(long paymentId, decimal amount, int resultCode = 0, string transId = "3000001",
        bool sign = true, long? rawAmount = null, string secret = MoMoSecretKey)
    {
        var orderId = $"{paymentId}_1790000000000";
        var amountText = (rawAmount ?? (long)amount).ToString();
        const string requestId = "req-1";
        const string responseTime = "1790000005000";
        var raw = $"accessKey={MoMoAccessKey}&amount={amountText}&extraData=&message=Successful.&orderId={orderId}&orderInfo=thanh toan hoa don"
                  + $"&orderType=momo_wallet&partnerCode={MoMoPartnerCode}&payType=qr&requestId={requestId}&responseTime={responseTime}"
                  + $"&resultCode={resultCode}&transId={transId}";
        var parameters = new Dictionary<string, string>
        {
            ["partnerCode"] = MoMoPartnerCode,
            ["orderId"] = orderId,
            ["requestId"] = requestId,
            ["amount"] = amountText,
            ["orderInfo"] = "thanh toan hoa don",
            ["orderType"] = "momo_wallet",
            ["transId"] = transId,
            ["resultCode"] = resultCode.ToString(),
            ["message"] = "Successful.",
            ["payType"] = "qr",
            ["responseTime"] = responseTime,
            ["extraData"] = "",
        };
        if (sign) parameters["signature"] = Hmac(raw, secret);
        return parameters;
    }

    /// <summary>Thân webhook của PayOS cho giao dịch <paramref name="paymentId"/> (orderCode = mã + <see cref="PayOsOffset"/>).</summary>
    public static string PayOsWebhook(long paymentId, decimal amount, string code = "00", bool success = true, string? reference = "FT26001",
        bool sign = true, long? rawAmount = null, string secret = PayOsChecksumKey)
    {
        var data = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["orderCode"] = paymentId + PayOsOffset,
            ["amount"] = rawAmount ?? (long)amount,
            ["description"] = "HD20260929000001",
            ["accountNumber"] = "12345678",
            ["reference"] = reference,
            ["transactionDateTime"] = "2026-09-29 08:30:00",
            ["currency"] = "VND",
            ["paymentLinkId"] = "link-1",
            ["code"] = code,
            ["desc"] = "Thành công",
            ["counterAccountBankId"] = "",
            ["counterAccountBankName"] = null,
        };
        var text = string.Join('&', data.Select(p => $"{p.Key}={p.Value}"));   // null cho chuỗi rỗng, số in như JSON
        var body = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["desc"] = "success",
            ["success"] = success,
            ["data"] = data,
            ["signature"] = sign ? Hmac(text, secret) : null,
        };
        return JsonSerializer.Serialize(body);
    }
}
