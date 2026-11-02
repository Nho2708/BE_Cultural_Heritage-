using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Billing;

/// <summary>
/// Xử lý webhook PayOS (POST JSON). <b>Chỉ webhook có chữ ký hợp lệ</b> mới đổi trạng thái giao dịch, hóa đơn và gói; trang trả về và trang hủy
/// của trình duyệt (PayOS không ký) chỉ chuyển sang trang kết quả chỉ đọc. Webhook lặp hoặc không phải của hệ thống này vẫn trả 200 để PayOS thôi gửi lại
/// (lúc khai địa chỉ webhook PayOS gửi thử một lần với mã đơn mẫu, mã đó không thấy trong CSDL và chỉ bị ghi nhật ký).
/// </summary>
public sealed class PayOsCallbackService(IPayOsGateway gateway, PaymentSettlementService settlement)
{
    private const int MaxLoggedLength = 1024;

    public async Task HandleWebhookAsync(string rawBody, string? ip, CancellationToken ct)
    {
        if (!gateway.IsConfigured)
        {
            await RejectAsync("gateway not configured", null, rawBody, ip, ct);
            return;
        }

        var webhook = gateway.ParseWebhook(rawBody);
        if (!webhook.SignatureValid)
        {
            await RejectAsync("invalid signature", webhook.PaymentId, rawBody, ip, ct);
            return;
        }
        if (webhook.PaymentId is not { } paymentId)
        {
            await RejectAsync("missing transaction reference", null, rawBody, ip, ct);
            return;
        }

        var result = await settlement.SettleAsync(new GatewayPayment(
            paymentId, BillingRules.PayOsProviderCode, webhook.Amount,
            webhook.IsSuccess,
            PaymentSettlementService.NormalizeGatewayTransactionId(webhook.Reference ?? webhook.PaymentLinkId), webhook.RawJson,
            new Dictionary<string, object?> { ["code"] = webhook.Code }), ct);

        if (result.Kind is not (SettleKind.Confirmed or SettleKind.AlreadyConfirmed))
            await RejectAsync(result.Reason, paymentId, rawBody, ip, ct);
    }

    public string ResolveReturnUrl(IReadOnlyDictionary<string, string> query) => gateway.BuildResultPageUrl(query);

    private Task RejectAsync(string reason, long? paymentId, string rawBody, string? ip, CancellationToken ct) =>
        settlement.RecordRejectionAsync(reason, paymentId, "200", ip,
            new { body = rawBody.Length > MaxLoggedLength ? rawBody[..MaxLoggedLength] : rawBody }, ct);
}
