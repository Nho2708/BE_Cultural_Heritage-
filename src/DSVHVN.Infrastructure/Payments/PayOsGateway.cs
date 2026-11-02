using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using DSVHVN.Application.Billing;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Payments;

/// <summary>
/// Cấu hình mục <c>PayOs</c>. <see cref="ClientId"/>, <see cref="ApiKey"/>, <see cref="ChecksumKey"/> lấy ở my.payos.vn (kênh thanh toán), đặt bằng User Secrets
/// hoặc biến môi trường (<c>PayOs__ChecksumKey</c>), không ghi vào appsettings. PayOS không có môi trường thử riêng: giao dịch là chuyển khoản thật số tiền nhỏ.
/// </summary>
public sealed class PayOsOptions
{
    public const string Section = "PayOs";

    public const string DefaultEndpoint = "https://api-merchant.payos.vn/v2/payment-requests";

    public string ClientId { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ChecksumKey { get; set; } = string.Empty;

    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>Địa chỉ PayOS đưa trình duyệt về sau khi trả hoặc hủy (dùng chung cho hai trường hợp, PayOS tự thêm <c>cancel</c>, <c>status</c>, <c>orderCode</c>).</summary>
    public string ReturnUrl { get; set; } = "http://localhost:5080/api/v1/payments/payos/return";

    /// <summary>
    /// <c>orderCode</c> gửi PayOS = <c>payments.id</c> + số này. PayOS không nhận mã đơn trùng, nên khi CSDL thử nghiệm bị dựng lại (mã bắt đầu lại từ 1)
    /// hãy tăng số này lên một giá trị lớn hơn mọi mã đã dùng. Thực tế dùng cố định một giá trị.
    /// </summary>
    public long OrderCodeOffset { get; set; }
}

/// <summary>
/// Cổng PayOS. Tạo link thanh toán bằng POST JSON (<c>x-client-id</c>, <c>x-api-key</c>, chữ ký HMAC-SHA256 bằng checksum key trên
/// <c>amount, cancelUrl, description, orderCode, returnUrl</c> sắp theo tên); kiểm chữ ký webhook trên toàn bộ trường của <c>data</c> sắp theo tên.
/// <c>orderCode</c> là số nguyên nên không cần cột ánh xạ riêng: <c>orderCode - OrderCodeOffset = payments.id</c>.
/// </summary>
public sealed class PayOsGateway(HttpClient http, IOptions<PayOsOptions> options, IOptions<AppLinkOptions> links) : IPayOsGateway
{
    private const long MaxSafeOrderCode = 9_007_199_254_740_991;

    private PayOsOptions Options => options.Value;

    public string Code => BillingRules.PayOsProviderCode;

    public string DisplayName => "PayOS (QR ngân hàng)";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Options.ClientId) && !string.IsNullOrWhiteSpace(Options.ApiKey)
                                && !string.IsNullOrWhiteSpace(Options.ChecksumKey);

    public async Task<string> CreatePaymentUrlAsync(PaymentStartContext context, CancellationToken ct)
    {
        if (!IsConfigured) throw new PaymentGatewayException("Chưa cấu hình PayOs:ClientId, PayOs:ApiKey và PayOs:ChecksumKey.");

        var o = Options;
        var orderCode = OrderCodeOf(context.PaymentId);
        if (orderCode > MaxSafeOrderCode) throw new PaymentGatewayException("Mã đơn PayOS vượt giới hạn cho phép.");
        var amount = (long)decimal.Truncate(context.Amount);
        // PayOS giới hạn mô tả 25 ký tự: dùng số hóa đơn bỏ dấu gạch.
        var description = context.InvoiceNumber.Replace("-", string.Empty);
        if (description.Length > 25) description = description[..25];

        var raw = $"amount={amount}&cancelUrl={o.ReturnUrl}&description={description}&orderCode={orderCode}&returnUrl={o.ReturnUrl}";
        var body = new Dictionary<string, object?>
        {
            ["orderCode"] = orderCode,
            ["amount"] = amount,
            ["description"] = description,
            ["items"] = new[] { new { name = "Goi dich vu DSVHVN", quantity = 1, price = amount } },
            ["returnUrl"] = o.ReturnUrl,
            ["cancelUrl"] = o.ReturnUrl,
            ["expiredAt"] = new DateTimeOffset(DateTime.SpecifyKind(context.ExpiresAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            ["signature"] = PaymentHelpers.SignSha256(raw, o.ChecksumKey),
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, o.Endpoint) { Content = JsonContent.Create(body) };
            request.Headers.Add("x-client-id", o.ClientId);
            request.Headers.Add("x-api-key", o.ApiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PaymentHelpers.CallTimeout);
            var response = await http.SendAsync(request, timeout.Token);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var code = json.TryGetProperty("code", out var c) ? c.GetString() : null;
            string? checkoutUrl = null;
            if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("checkoutUrl", out var cu))
                checkoutUrl = cu.GetString();
            if (code != "00" || string.IsNullOrWhiteSpace(checkoutUrl))
            {
                var desc = json.TryGetProperty("desc", out var d) ? d.GetString() : null;
                throw new PaymentGatewayException($"PayOS từ chối tạo link (code {code}): {desc}");
            }
            return checkoutUrl;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new PaymentGatewayException("Không gọi được PayOS.", ex);
        }
    }

    public PayOsWebhook ParseWebhook(string rawBody)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawBody); }
        catch (JsonException) { return Invalid(); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return Invalid();

            var valid = false;
            if (IsConfigured && root.TryGetProperty("signature", out var sig) && sig.ValueKind == JsonValueKind.String)
                valid = PaymentHelpers.SignatureMatches(PaymentHelpers.SignSha256(DataString(data), Options.ChecksumKey), sig.GetString());

            var orderCode = data.TryGetProperty("orderCode", out var oc) && oc.TryGetInt64(out var code) ? code : (long?)null;
            var paymentId = orderCode - Options.OrderCodeOffset is { } id and > 0 ? id : (long?)null;
            var amount = data.TryGetProperty("amount", out var am) && am.TryGetInt64(out var a) ? a : (long?)null;
            var rootOk = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True
                         && root.TryGetProperty("code", out var rc) && rc.GetString() == "00";
            var dataCode = data.TryGetProperty("code", out var dc) ? dc.GetString() : null;
            return new PayOsWebhook(valid, paymentId, amount, rootOk && dataCode == "00", dataCode,
                StringOf(data, "reference"), StringOf(data, "paymentLinkId"), data.GetRawText());
        }

        static PayOsWebhook Invalid() => new(false, null, null, false, null, null, null, "{}");
    }

    public string BuildResultPageUrl(IReadOnlyDictionary<string, string> query)
    {
        long? paymentId = null;
        if (long.TryParse(query.GetValueOrDefault("orderCode"), NumberStyles.None, CultureInfo.InvariantCulture, out var orderCode)
            && orderCode - Options.OrderCodeOffset is { } id and > 0)
            paymentId = id;
        return PaymentHelpers.ResultPageUrl(links.Value, paymentId, paymentId is not null);
    }

    /// <summary><c>orderCode</c> gửi PayOS cho một dòng thanh toán.</summary>
    public long OrderCodeOf(long paymentId) => paymentId + Options.OrderCodeOffset;

    /// <summary>Chuỗi cần ký của webhook: mọi trường của <c>data</c> sắp theo tên, dạng <c>tên=giá trị</c> nối bằng <c>&amp;</c>; giá trị rỗng (null) ghi chuỗi rỗng.</summary>
    internal static string DataString(JsonElement data)
    {
        var parts = data.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={ValueText(p.Value)}");
        return string.Join('&', parts);
    }

    private static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    private static string? StringOf(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
