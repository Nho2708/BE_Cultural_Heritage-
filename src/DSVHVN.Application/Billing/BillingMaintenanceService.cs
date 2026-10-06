using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Billing;

/// <summary>Số dòng đã đổi trạng thái trong một lần chạy tác vụ nền của gói và thanh toán.</summary>
public sealed record BillingMaintenanceResult(int PaymentsFailed, int InvoicesCancelled, int SubscriptionsExpired)
{
    public int Total => PaymentsFailed + InvoicesCancelled + SubscriptionsExpired;
}

/// <summary>
/// Ba việc nền của gói và thanh toán, mỗi thay đổi ghi nhật ký do hệ thống:
/// (1) giao dịch chờ quá 30 phút không nhận IPN chuyển thất bại; (2) hóa đơn chưa trả quá hạn (24 giờ) và không còn giao dịch chờ thì hóa đơn và đăng ký
/// chờ thanh toán chuyển hủy; (3) gói đang hiệu lực qua ngày hết hạn chuyển hết hạn (không tự chuyển trường về gói Miễn phí). Việc (1) chạy trước (2).
/// Mỗi việc dùng câu lệnh có điều kiện hoặc khóa dòng trường nên chạy lặp lại hay chạy cùng IPN và thao tác của người dùng đều không làm hỏng dữ liệu.
/// </summary>
public sealed class BillingMaintenanceService(IAppDbContext db, IAuditLogger audit, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<BillingMaintenanceResult> RunAsync(bool includeExpiry, CancellationToken ct)
    {
        var failed = await FailStalePaymentsAsync(ct);
        var cancelled = await CancelOverdueInvoicesAsync(ct);
        var expired = includeExpiry ? await ExpireSubscriptionsAsync(ct) : 0;
        return new BillingMaintenanceResult(failed, cancelled, expired);
    }

    /// <summary>Giao dịch PENDING tạo quá 30 phút trước mà chưa có IPN chuyển FAILED. Trả số giao dịch đã đổi.</summary>
    public async Task<int> FailStalePaymentsAsync(CancellationToken ct)
    {
        var before = Now - BillingRules.PendingPaymentTimeout;
        var stale = await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.PENDING && p.CreatedAt < before)
            .Select(p => new { p.Id, p.InvoiceId, p.Invoice!.OrganizationId }).ToListAsync(ct);

        var changed = 0;
        foreach (var item in stale)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.LockOrganizationAsync(item.OrganizationId, ct);
            if (!await PaymentClaims.TryFailPendingAsync(db, item.Id, ct)) continue;
            audit.Record(AuditAction.BILLING_STATUS_CHANGED, AuditTargets.Payments, item.Id, actor: null,
                oldValue: new { status = PaymentStatus.PENDING },
                newValue: new { status = PaymentStatus.FAILED, reason = "no payment notification within 30 minutes", invoiceId = item.InvoiceId });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            changed++;
        }
        return changed;
    }

    /// <summary>Hóa đơn UNPAID quá <c>due_at</c> không còn giao dịch chờ: hóa đơn và đăng ký PENDING chuyển CANCELLED. Trả số hóa đơn đã hủy.</summary>
    public async Task<int> CancelOverdueInvoicesAsync(CancellationToken ct)
    {
        var now = Now;
        var overdue = await db.Invoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.UNPAID && i.DueAt < now)
            .Select(i => new { i.Id, i.OrganizationId }).ToListAsync(ct);

        var changed = 0;
        foreach (var item in overdue)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.LockOrganizationAsync(item.OrganizationId, ct);
            var invoice = await db.Invoices.Include(i => i.Subscription).SingleAsync(i => i.Id == item.Id, ct);
            if (invoice.Status != InvoiceStatus.UNPAID || invoice.DueAt >= now) continue;
            if (await db.Payments.AnyAsync(p => p.InvoiceId == invoice.Id && p.Status == PaymentStatus.PENDING, ct)) continue;

            invoice.Status = InvoiceStatus.CANCELLED;
            var subscription = invoice.Subscription;
            var subscriptionCancelled = subscription is { Status: SubscriptionStatus.PENDING };
            if (subscriptionCancelled) subscription!.Status = SubscriptionStatus.CANCELLED;
            audit.Record(AuditAction.BILLING_STATUS_CHANGED, AuditTargets.Invoices, invoice.Id, actor: null,
                oldValue: new { status = InvoiceStatus.UNPAID },
                newValue: new
                {
                    status = InvoiceStatus.CANCELLED, reason = "overdue and no pending payment", invoiceNumber = invoice.InvoiceNumber,
                    subscriptionId = invoice.SubscriptionId, subscriptionCancelled,
                });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            changed++;
        }
        return changed;
    }

    /// <summary>Đăng ký ACTIVE có <c>end_date</c> trước hôm nay (giờ Việt Nam) chuyển EXPIRED. Trả số đăng ký đã đổi.</summary>
    public async Task<int> ExpireSubscriptionsAsync(CancellationToken ct)
    {
        var today = VietnamTime.Today(clock.GetUtcNow());
        var due = await db.Subscriptions.Include(s => s.Plan)
            .Where(s => s.Status == SubscriptionStatus.ACTIVE && s.EndDate < today).ToListAsync(ct);
        if (due.Count == 0) return 0;

        foreach (var subscription in due)
        {
            subscription.Status = SubscriptionStatus.EXPIRED;
            audit.Record(AuditAction.BILLING_STATUS_CHANGED, AuditTargets.Subscriptions, subscription.Id, actor: null,
                oldValue: new { status = SubscriptionStatus.ACTIVE },
                newValue: new
                {
                    status = SubscriptionStatus.EXPIRED, reason = "end date passed", plan = subscription.Plan?.Name,
                    organizationId = subscription.OrganizationId, endDate = subscription.EndDate,
                });
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}
