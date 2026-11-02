using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using DSVHVN.Application.Billing;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Payments;

/// <summary>
/// Cấu hình mục <c>MoMo</c> (sandbox). <see cref="PartnerCode"/>, <see cref="AccessKey"/>, <see cref="SecretKey"/> đặt bằng User Secrets hoặc biến môi trường
/// (<c>MoMo__SecretKey</c>), không ghi vào appsettings. Thiếu một trong ba thì backend vẫn chạy nhưng cổng MoMo không dùng được.
/// </summary>
public sealed class MoMoOptions
{
    public const string Section = "MoMo";

    public const string SandboxEndpoint = "https://test-payment.momo.vn/v2/gateway/api/create";

    public string PartnerCode { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Endpoint { get; set; } = SandboxEndpoint;

    /// <summary>Trang MoMo đưa trình duyệt về sau khi trả: <c>/api/v1/payments/momo/return</c> của backend. Phải mở được từ trình duyệt của người trả.</summary>
    public string ReturnUrl { get; set; } = "http://localhost:5080/api/v1/payments/momo/return";

    /// <summary>Địa chỉ MoMo gọi (POST JSON) để báo kết quả: <c>/api/v1/payments/momo/ipn</c>. Phải là địa chỉ công khai (localhost không nhận được).</summary>
    public string IpnUrl { get; set; } = "http://localhost:5080/api/v1/payments/momo/ipn";

    /// <summary>Loại thanh toán: <c>captureWallet</c> (ví MoMo, QR) hoặc <c>payWithMethod</c>.</summary>
    public string RequestType { get; set; } = "captureWallet";

    public string Lang { get; set; } = "vi";
}

/// <summary>
/// Cổng MoMo (API v2). Tạo giao dịch bằng POST JSON tới <see cref="MoMoOptions.Endpoint"/>, chữ ký HMAC-SHA256 trên các trường sắp theo tên;
/// kiểm chữ ký dữ liệu MoMo gửi về (IPN và trang trả về cùng bộ trường). Mã tham chiếu gửi MoMo là <c>{payments.id}_{mili giây}</c> để không trùng
/// khi CSDL thử nghiệm bị dựng lại (bộ khóa sandbox của MoMo dùng chung cho mọi người).
/// </summary>
public sealed class MoMoGateway(HttpClient http, IOptions<MoMoOptions> options, IOptions<AppLinkOptions> links) : IMoMoGateway
{
    /// <summary>Các trường tham gia chữ ký của IPN và trang trả về, theo thứ tự tên.</summary>
    private static readonly string[] CallbackKeys =
        ["amount", "extraData", "message", "orderId", "orderInfo", "orderType", "partnerCode", "payType", "requestId", "responseTime", "resultCode", "transId"];

    private MoMoOptions Options => options.Value;

    public string Code => BillingRules.MoMoProviderCode;

    public string DisplayName => "Ví MoMo";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Options.PartnerCode) && !string.IsNullOrWhiteSpace(Options.AccessKey)
                                && !string.IsNullOrWhiteSpace(Options.SecretKey);

    public async Task<string> CreatePaymentUrlAsync(PaymentStartContext context, CancellationToken ct)
    {
        if (!IsConfigured) throw new PaymentGatewayException("Chưa cấu hình MoMo:PartnerCode, MoMo:AccessKey và MoMo:SecretKey.");

        var o = Options;
        var orderId = OrderIdOf(context);
        var requestId = orderId;
        var amount = decimal.Truncate(context.Amount).ToString(CultureInfo.InvariantCulture);
        const string extraData = "";

        var raw = $"accessKey={o.AccessKey}&amount={amount}&extraData={extraData}&ipnUrl={o.IpnUrl}&orderId={orderId}&orderInfo={context.OrderInfo}"
                  + $"&partnerCode={o.PartnerCode}&redirectUrl={o.ReturnUrl}&requestId={requestId}&requestType={o.RequestType}";
        var body = new Dictionary<string, object?>
        {
            ["partnerCode"] = o.PartnerCode,
            ["requestId"] = requestId,
            ["amount"] = long.Parse(amount, CultureInfo.InvariantCulture),
            ["orderId"] = orderId,
            ["orderInfo"] = context.OrderInfo,
            ["redirectUrl"] = o.ReturnUrl,
            ["ipnUrl"] = o.IpnUrl,
            ["requestType"] = o.RequestType,
            ["extraData"] = extraData,
            ["lang"] = o.Lang,
            ["signature"] = PaymentHelpers.SignSha256(raw, o.SecretKey),
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PaymentHelpers.CallTimeout);
            var response = await http.PostAsJsonAsync(o.Endpoint, body, timeout.Token);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var resultCode = json.TryGetProperty("resultCode", out var rc) && rc.TryGetInt32(out var code) ? code : -1;
            var payUrl = json.TryGetProperty("payUrl", out var pu) ? pu.GetString() : null;
            if (resultCode != 0 || string.IsNullOrWhiteSpace(payUrl))
            {
                var message = json.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new PaymentGatewayException($"MoMo từ chối tạo giao dịch (resultCode {resultCode}): {message}");
            }
            return payUrl;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new PaymentGatewayException("Không gọi được MoMo.", ex);
        }
    }

    public MoMoCallback Parse(IReadOnlyDictionary<string, string> parameters)
    {
        var valid = false;
        if (IsConfigured && parameters.TryGetValue("signature", out var received))
        {
            var raw = $"accessKey={Options.AccessKey}&" + string.Join('&', CallbackKeys.Select(k => $"{k}={parameters.GetValueOrDefault(k) ?? string.Empty}"));
            valid = PaymentHelpers.SignatureMatches(PaymentHelpers.SignSha256(raw, Options.SecretKey), received);
        }

        return new MoMoCallback(
            valid,
            PaymentIdOf(parameters.GetValueOrDefault("orderId")),
            long.TryParse(parameters.GetValueOrDefault("amount"), NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ? amount : null,
            parameters.GetValueOrDefault("partnerCode"),
            int.TryParse(parameters.GetValueOrDefault("resultCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null,
            parameters.GetValueOrDefault("transId"),
            parameters.GetValueOrDefault("message"),
            parameters);
    }

    public string BuildResultPageUrl(long? paymentId, bool signatureValid) => PaymentHelpers.ResultPageUrl(links.Value, paymentId, signatureValid);

    /// <summary><c>{payments.id}_{mili giây lúc tạo}</c>: chỉ chữ số và dấu gạch dưới, đúng ký tự MoMo cho phép.</summary>
    public static string OrderIdOf(PaymentStartContext context) =>
        $"{context.PaymentId.ToString(CultureInfo.InvariantCulture)}_{new DateTimeOffset(DateTime.SpecifyKind(context.CreatedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()}";

    /// <summary>Lấy <c>payments.id</c> từ mã tham chiếu <c>{id}_{mili giây}</c>.</summary>
    public static long? PaymentIdOf(string? orderId)
    {
        var head = orderId?.Split('_')[0];
        return long.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }
}
