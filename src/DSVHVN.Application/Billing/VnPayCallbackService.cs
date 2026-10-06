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
/// Thứ tự xử lý IPN: kiểm chữ ký, tìm giao dịch theo mã tham chiếu, so số tiền, chiếm giao dịch (một câu lệnh có điều kiện nên IPN lặp hoặc đồng thời
/// chỉ đổi dữ liệu một lần), ghi kết quả kèm dữ liệu gốc, nếu thành công thì hóa đơn PAID và đăng ký ACTIVE, tất cả trong một giao dịch CSDL.
/// IPN bị từ chối không đổi dữ liệu và được ghi nhật ký thao tác.
/// </summary>
public sealed class VnPayCallbackService(
    IAppDbContext db,
    IVnPayGateway gateway,
    SubscriptionActivator activator,
    IAuditLogger audit,
    TimeProvider clock)
{
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

        var payment = await db.Payments.AsNoTracking().Include(p => p.Provider).Include(p => p.Invoice)
            .SingleOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null || payment.Provider?.Code != BillingRules.VnPayProviderCode || payment.Invoice is null)
            return await RejectAsync("transaction not found", paymentId, query, ip, IpnResponse.OrderNotFound, ct);
        if (payment.Amount is not { } amount || callback.Amount != BillingRules.VnPayAmount(amount))
            return await RejectAsync($"amount mismatch (expected {(payment.Amount is { } a ? BillingRules.VnPayAmount(a) : 0)})",
                paymentId, query, ip, IpnResponse.InvalidAmount, ct);

        var now = Now();
        var raw = JsonSerializer.Serialize(new SortedDictionary<string, string>(callback.Parameters.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal), RawJson);
        var gatewayTxn = GatewayTransactionId(callback.TransactionNo);
        var success = callback.IsSuccess;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockOrganizationAsync(payment.Invoice.OrganizationId, ct);

        // Cùng một mã giao dịch của cổng không thể thuộc hai dòng thanh toán.
        if (gatewayTxn is not null && await db.Payments.AnyAsync(p => p.ProviderId == payment.ProviderId
                && p.ProviderTransactionId == gatewayTxn && p.Id != payment.Id, ct))
        {
            await tx.RollbackAsync(ct);
            return await RejectAsync("gateway transaction id already used by another payment", paymentId, query, ip, IpnResponse.UnknownError, ct);
        }

        var previous = payment.Status;
        var claimed = success
            // Giao dịch thành công tới muộn: giao dịch đã bị hệ thống đánh thất bại (quá hạn, thay bằng giao dịch mới, hủy yêu cầu) mà chưa có dữ liệu của cổng
            // vẫn được ghi nhận vì tiền đã thu.
            ? await db.Payments.Where(p => p.Id == paymentId && (p.Status == PaymentStatus.PENDING || (p.Status == PaymentStatus.FAILED && p.RawResponse == null)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, PaymentStatus.SUCCEEDED)
                    .SetProperty(p => p.ProviderTransactionId, gatewayTxn)
                    .SetProperty(p => p.RawResponse, raw)
                    .SetProperty(p => p.PaidAt, now), ct) == 1
            : await db.Payments.Where(p => p.Id == paymentId && p.Status == PaymentStatus.PENDING)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, PaymentStatus.FAILED)
                    .SetProperty(p => p.ProviderTransactionId, gatewayTxn)
                    .SetProperty(p => p.RawResponse, raw), ct) == 1;
        if (!claimed)
        {
            await tx.RollbackAsync(ct);
            return IpnResponse.AlreadyConfirmed;
        }

        if (success)
        {
            await RecordSuccessAsync(paymentId, payment.InvoiceId, previous, now, ct);
        }
        else
        {
            audit.Record(AuditAction.BILLING_STATUS_CHANGED, AuditTargets.Payments, paymentId, actor: null,
                oldValue: new { status = PaymentStatus.PENDING },
                newValue: new
                {
                    status = PaymentStatus.FAILED, invoiceId = payment.InvoiceId, responseCode = callback.ResponseCode,
                    transactionStatus = callback.TransactionStatus, gatewayTransactionId = gatewayTxn,
                });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return IpnResponse.Confirmed;
    }

    /// <summary>
    /// Thanh toán thành công: hóa đơn PAID, đăng ký ACTIVE theo loại mua. Hóa đơn đã PAID từ một giao dịch khác (người dùng trả hai lần) thì chỉ ghi nhận giao dịch
    /// và nhật ký để đối soát, không kích hoạt thêm gói.
    /// </summary>
    private async Task RecordSuccessAsync(long paymentId, long invoiceId, PaymentStatus? previous, DateTime now, CancellationToken ct)
    {
        var invoice = await db.Invoices.Include(i => i.Subscription!).ThenInclude(s => s.Plan).SingleAsync(i => i.Id == invoiceId, ct);
        var subscription = invoice.Subscription!;
        var late = previous == PaymentStatus.FAILED;

        if (invoice.Status == InvoiceStatus.PAID || subscription.Status == SubscriptionStatus.ACTIVE
            || subscription.Plan is null || subscription.Status == SubscriptionStatus.EXPIRED)
        {
            audit.Record(AuditAction.BILLING_STATUS_CHANGED, AuditTargets.Payments, paymentId, actor: null,
                oldValue: new { status = previous },
                newValue: new
                {
                    status = PaymentStatus.SUCCEEDED, invoiceId = invoice.Id, invoiceStatus = invoice.Status, late,
                    note = "invoice was already paid or its subscription is no longer waiting; payment recorded for reconciliation, no plan change",
                });
            return;
        }

        var today = VietnamTime.Today(new DateTimeOffset(now, TimeSpan.Zero));
        var invoiceBefore = invoice.Status;
        var outcome = await activator.ActivateAsync(invoice, subscription, subscription.Plan, today, late ? "late ipn" : "ipn", ct);
        audit.Record(AuditAction.BILLING_STATUS_CHANGED, AuditTargets.Payments, paymentId, actor: null,
            oldValue: new { status = previous, invoiceStatus = invoiceBefore },
            newValue: new
            {
                status = PaymentStatus.SUCCEEDED, invoiceId = invoice.Id, invoiceNumber = invoice.InvoiceNumber, invoiceStatus = InvoiceStatus.PAID,
                subscriptionId = subscription.Id, kind = outcome.Kind, startDate = outcome.StartDate, endDate = outcome.EndDate, late,
            });
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

    /// <summary>Mã giao dịch của VNPay; giao dịch chưa tạo ở cổng báo "0" hoặc để trống thì không có mã (không được lưu để khỏi trùng nhau).</summary>
    private static string? GatewayTransactionId(string? transactionNo) =>
        string.IsNullOrWhiteSpace(transactionNo) || transactionNo.Trim() == "0" ? null : transactionNo.Trim();

    private DateTime Now()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
