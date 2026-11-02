using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Billing;

public enum SettleKind
{
    /// <summary>Đã ghi nhận kết quả (thành công hoặc thất bại).</summary>
    Confirmed,

    /// <summary>Giao dịch đã được xử lý trước đó (IPN lặp hoặc đồng thời): không đổi dữ liệu.</summary>
    AlreadyConfirmed,

    /// <summary>Không thấy giao dịch của đúng cổng.</summary>
    NotFound,

    /// <summary>Số tiền cổng báo khác số tiền của giao dịch.</summary>
    AmountMismatch,

    /// <summary>Mã giao dịch của cổng đã thuộc một dòng thanh toán khác.</summary>
    TransactionIdConflict,
}

public sealed record SettleResult(SettleKind Kind, string Reason = "");

/// <summary>
/// Kết quả thanh toán đã được cổng xác thực chữ ký, ở dạng chung cho mọi cổng. <see cref="PaidAmount"/> là số tiền đồng cổng báo (null nếu thiếu),
/// <see cref="Details"/> là các giá trị riêng của cổng để ghi vào nhật ký khi giao dịch thất bại (mã phản hồi, mô tả lỗi; không có khóa hay chữ ký).
/// </summary>
public sealed record GatewayPayment(
    long PaymentId,
    string ProviderCode,
    decimal? PaidAmount,
    bool Success,
    string? GatewayTransactionId,
    string RawResponse,
    IReadOnlyDictionary<string, object?> Details);

/// <summary>
/// Phần xử lý chung mọi cổng sau khi chữ ký đã được kiểm: tìm giao dịch của đúng cổng, so số tiền, chiếm giao dịch bằng một câu lệnh cập nhật có điều kiện
/// (IPN lặp hoặc đồng thời chỉ đổi dữ liệu một lần), ghi kết quả kèm dữ liệu gốc, nếu thành công thì hóa đơn PAID và đăng ký ACTIVE, tất cả trong một
/// giao dịch CSDL khóa dòng trường. Không đọc, không kiểm gì của riêng cổng nào.
/// </summary>
public sealed class PaymentSettlementService(
    IAppDbContext db,
    SubscriptionActivator activator,
    IAuditLogger audit,
    TimeProvider clock)
{
    public async Task<SettleResult> SettleAsync(GatewayPayment gateway, CancellationToken ct)
    {
        var paymentId = gateway.PaymentId;
        var payment = await db.Payments.AsNoTracking().Include(p => p.Provider).Include(p => p.Invoice)
            .SingleOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null || payment.Provider?.Code != gateway.ProviderCode || payment.Invoice is null)
            return new SettleResult(SettleKind.NotFound, "transaction not found");
        if (payment.Amount is not { } amount || gateway.PaidAmount != decimal.Truncate(amount))
            return new SettleResult(SettleKind.AmountMismatch,
                $"amount mismatch (expected {(payment.Amount is { } a ? decimal.Truncate(a) : 0)})");

        var now = Now();
        var gatewayTxn = gateway.GatewayTransactionId;
        var raw = gateway.RawResponse;
        var success = gateway.Success;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockOrganizationAsync(payment.Invoice.OrganizationId, ct);

        // Cùng một mã giao dịch của cổng không thể thuộc hai dòng thanh toán.
        if (gatewayTxn is not null && await db.Payments.AnyAsync(p => p.ProviderId == payment.ProviderId
                && p.ProviderTransactionId == gatewayTxn && p.Id != payment.Id, ct))
        {
            await tx.RollbackAsync(ct);
            return new SettleResult(SettleKind.TransactionIdConflict, "gateway transaction id already used by another payment");
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
            return new SettleResult(SettleKind.AlreadyConfirmed);
        }

        if (success)
        {
            await RecordSuccessAsync(paymentId, payment.InvoiceId, previous, now, ct);
        }
        else
        {
            var details = new Dictionary<string, object?>
            {
                ["status"] = PaymentStatus.FAILED,
                ["invoiceId"] = payment.InvoiceId,
                ["gatewayTransactionId"] = gatewayTxn,
            };
            foreach (var (key, value) in gateway.Details) details[key] = value;
            audit.Record(AuditAction.BILLING_STATUS_CHANGED, AuditTargets.Payments, paymentId, actor: null,
                oldValue: new { status = PaymentStatus.PENDING },
                newValue: details);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new SettleResult(SettleKind.Confirmed);
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

    /// <summary>Ghi nhật ký <c>IPN_REJECTED</c> (không đổi dữ liệu nào khác) cho dữ liệu cổng bị từ chối. <paramref name="received"/> đã được cắt ngắn, không kèm khóa hay chữ ký.</summary>
    public async Task RecordRejectionAsync(string reason, long? paymentId, string responseCode, string? ip, object received, CancellationToken ct)
    {
        audit.Record(AuditAction.IPN_REJECTED, AuditTargets.Payments, paymentId, actor: null,
            newValue: new { reason, responseCode, ip, received });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Mã giao dịch của cổng; để trống hoặc "0" (giao dịch chưa tạo ở cổng) thì không có mã, không được lưu để khỏi trùng nhau.</summary>
    public static string? NormalizeGatewayTransactionId(string? id) =>
        string.IsNullOrWhiteSpace(id) || id.Trim() == "0" ? null : id.Trim();

    private DateTime Now()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
