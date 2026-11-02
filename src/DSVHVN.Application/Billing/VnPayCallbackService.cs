using System.Text.Encodings.Web;
using System.Text.Json;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Billing;

/// <summary>Phản hồi IPN theo chuẩn VNPay: <c>RspCode</c> và <c>Message</c>, không đi qua khuôn phản hồi chung của API.</summary>
public sealed record IpnResponse(string RspCode, string Message)
{
    /// <summary>Đã ghi nhận (giao dịch thành công hoặc thất bại đã lưu).</summary>
    public static readonly IpnResponse Confirmed = new("00", "Confirm Success");

    /// <summary>Giao dịch đã được xử lý trước đó, không đổi dữ liệu (IPN lặp).</summary>
    public static readonly IpnResponse AlreadyConfirmed = new("02", "Order already confirmed");

    public static readonly IpnResponse OrderNotFound = new("01", "Order not found");
    public static readonly IpnResponse InvalidAmount = new("04", "Invalid amount");
    public static readonly IpnResponse InvalidSignature = new("97", "Invalid signature");
    public static readonly IpnResponse UnknownError = new("99", "Unknown error");
}

/// <summary>
/// Xử lý dữ liệu VNPay gửi về. <b>Chỉ IPN</b> (máy chủ VNPay gọi máy chủ này) mới đổi trạng thái giao dịch, hóa đơn và gói; trang trả về
/// của trình duyệt chỉ kiểm chữ ký rồi chuyển người dùng sang trang kết quả, trang đó chỉ đọc.
/// Thứ tự xử lý IPN: kiểm chữ ký, đọc mã tham chiếu, rồi giao cho <see cref="PaymentSettlementService"/> (tìm giao dịch, so số tiền, chiếm giao dịch,
/// ghi kết quả, hóa đơn PAID và đăng ký ACTIVE trong một giao dịch CSDL). IPN bị từ chối không đổi dữ liệu và được ghi nhật ký thao tác.
/// </summary>
public sealed class VnPayCallbackService(
    IAppDbContext db,
    IVnPayGateway gateway,
    SubscriptionActivator activator,
    IAuditLogger audit,
    TimeProvider clock)
{
    // Phần xử lý chung của mọi cổng; dựng từ chính các phụ thuộc này nên chữ ký hàm khởi tạo của lớp không đổi.
    private readonly PaymentSettlementService settlement = new(db, activator, audit, clock);

    private static readonly JsonSerializerOptions RawJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Số tham số và độ dài mỗi giá trị tối đa khi ghi vào nhật ký (dữ liệu từ ngoài, không tin).</summary>
    private const int MaxLoggedParameters = 40;
    private const int MaxLoggedValueLength = 256;

    public async Task<IpnResponse> HandleIpnAsync(IReadOnlyDictionary<string, string> query, string? ip, CancellationToken ct)
    {
        if (!gateway.IsConfigured) return await RejectAsync("gateway not configured", null, query, ip, IpnResponse.UnknownError, ct);

        var callback = gateway.Parse(query);
        if (!callback.SignatureValid)
            return await RejectAsync("invalid signature", callback.PaymentId, query, ip, IpnResponse.InvalidSignature, ct);
        if (callback.PaymentId is not { } paymentId)
            return await RejectAsync("missing transaction reference", null, query, ip, IpnResponse.OrderNotFound, ct);

        var raw = JsonSerializer.Serialize(new SortedDictionary<string, string>(callback.Parameters.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal), RawJson);
        var result = await settlement.SettleAsync(new GatewayPayment(
            paymentId, BillingRules.VnPayProviderCode,
            // VNPay báo số tiền đã nhân 100; số tiền lẻ (không chia hết cho 100) cho ra phần thập phân và bị coi là lệch.
            callback.Amount is { } paid ? paid / 100m : null,
            callback.IsSuccess, PaymentSettlementService.NormalizeGatewayTransactionId(callback.TransactionNo), raw,
            new Dictionary<string, object?>
            {
                ["responseCode"] = callback.ResponseCode,
                ["transactionStatus"] = callback.TransactionStatus,
            }), ct);

        return result.Kind switch
        {
            SettleKind.Confirmed => IpnResponse.Confirmed,
            SettleKind.AlreadyConfirmed => IpnResponse.AlreadyConfirmed,
            SettleKind.NotFound => await RejectAsync(result.Reason, paymentId, query, ip, IpnResponse.OrderNotFound, ct),
            SettleKind.AmountMismatch => await RejectAsync(result.Reason, paymentId, query, ip, IpnResponse.InvalidAmount, ct),
            _ => await RejectAsync(result.Reason, paymentId, query, ip, IpnResponse.UnknownError, ct),
        };
    }

    /// <summary>Trang trả về của trình duyệt: kiểm chữ ký rồi chuyển sang trang kết quả của web quản trị. Không đọc, không ghi dữ liệu.</summary>
    public string ResolveReturnUrl(IReadOnlyDictionary<string, string> query)
    {
        var callback = gateway.IsConfigured ? gateway.Parse(query) : null;
        var valid = callback?.SignatureValid == true;
        return gateway.BuildResultPageUrl(valid ? callback!.PaymentId : null, valid);
    }

    private async Task<IpnResponse> RejectAsync(string reason, long? paymentId, IReadOnlyDictionary<string, string> query, string? ip,
        IpnResponse response, CancellationToken ct)
    {
        var received = query.Where(p => p.Key.StartsWith("vnp_", StringComparison.Ordinal))
            .Take(MaxLoggedParameters)
            .ToDictionary(p => p.Key, p => p.Value.Length > MaxLoggedValueLength ? p.Value[..MaxLoggedValueLength] : p.Value);
        audit.Record(AuditAction.IPN_REJECTED, AuditTargets.Payments, paymentId, actor: null,
            newValue: new { reason, responseCode = response.RspCode, ip, received });
        await db.SaveChangesAsync(ct);
        return response;
    }
}
