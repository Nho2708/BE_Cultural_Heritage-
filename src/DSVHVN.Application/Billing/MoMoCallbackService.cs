using System.Text.Encodings.Web;
using System.Text.Json;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Billing;

/// <summary>Kết quả xử lý IPN của MoMo: <see cref="HttpStatus"/> trả cho MoMo (204 khi đã xử lý, 400 khi bị từ chối) và lý do để ghi nhật ký.</summary>
public sealed record MoMoIpnResult(int HttpStatus, string Reason)
{
    public static readonly MoMoIpnResult Accepted = new(204, "ok");
}

/// <summary>
/// Xử lý dữ liệu MoMo gửi về. Giống VNPay: <b>chỉ IPN</b> (máy chủ MoMo gọi máy chủ này, POST JSON) mới đổi trạng thái giao dịch, hóa đơn và gói; trang trả về
/// của trình duyệt chỉ kiểm chữ ký rồi chuyển sang trang kết quả. Mọi kiểm tra sau chữ ký nằm ở <see cref="PaymentSettlementService"/>.
/// IPN lặp trả 204 như lần đầu để MoMo thôi gửi lại.
/// </summary>
public sealed class MoMoCallbackService(IMoMoGateway gateway, PaymentSettlementService settlement)
{
    private static readonly JsonSerializerOptions RawJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const int MaxLoggedParameters = 40;
    private const int MaxLoggedValueLength = 256;

    public async Task<MoMoIpnResult> HandleIpnAsync(IReadOnlyDictionary<string, string> parameters, string? ip, CancellationToken ct)
    {
        if (!gateway.IsConfigured) return await RejectAsync("gateway not configured", null, parameters, ip, 400, ct);

        var callback = gateway.Parse(parameters);
        if (!callback.SignatureValid) return await RejectAsync("invalid signature", callback.PaymentId, parameters, ip, 400, ct);
        if (callback.PaymentId is not { } paymentId) return await RejectAsync("missing transaction reference", null, parameters, ip, 400, ct);

        var raw = JsonSerializer.Serialize(new SortedDictionary<string, string>(callback.Parameters.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal), RawJson);
        var result = await settlement.SettleAsync(new GatewayPayment(
            paymentId, BillingRules.MoMoProviderCode, callback.Amount,
            callback.IsSuccess, PaymentSettlementService.NormalizeGatewayTransactionId(callback.TransId), raw,
            new Dictionary<string, object?> { ["resultCode"] = callback.ResultCode, ["message"] = Trim(callback.Message) }), ct);

        return result.Kind switch
        {
            SettleKind.Confirmed or SettleKind.AlreadyConfirmed => MoMoIpnResult.Accepted,
            _ => await RejectAsync(result.Reason, paymentId, parameters, ip, 400, ct),
        };
    }

    /// <summary>Trang trả về của trình duyệt: kiểm chữ ký rồi chuyển sang trang kết quả. Không đọc, không ghi dữ liệu.</summary>
    public string ResolveReturnUrl(IReadOnlyDictionary<string, string> query)
    {
        var callback = gateway.IsConfigured ? gateway.Parse(query) : null;
        var valid = callback?.SignatureValid == true;
        return gateway.BuildResultPageUrl(valid ? callback!.PaymentId : null, valid);
    }

    private async Task<MoMoIpnResult> RejectAsync(string reason, long? paymentId, IReadOnlyDictionary<string, string> parameters, string? ip, int status, CancellationToken ct)
    {
        var received = parameters.Where(p => !p.Key.Equals("signature", StringComparison.OrdinalIgnoreCase))
            .Take(MaxLoggedParameters)
            .ToDictionary(p => p.Key, p => Trim(p.Value) ?? string.Empty);
        await settlement.RecordRejectionAsync(reason, paymentId, status.ToString(), ip, received, ct);
        return new MoMoIpnResult(status, reason);
    }

    private static string? Trim(string? value) => value is { Length: > MaxLoggedValueLength } ? value[..MaxLoggedValueLength] : value;
}
