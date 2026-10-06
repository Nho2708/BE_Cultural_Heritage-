using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Billing;

/// <summary>
/// Ảnh chụp gói của một trường tại một ngày: gói hiệu lực (ACTIVE, đang trong khoảng ngày), gói gia hạn chờ bắt đầu (ACTIVE, bắt đầu sau hôm nay),
/// yêu cầu mua chưa thanh toán (PENDING, tối đa một) và hóa đơn của yêu cầu đó.
/// </summary>
public sealed record BillingSnapshot(
    DateOnly Today,
    Subscription? Effective,
    Subscription? Queued,
    Subscription? Pending,
    Invoice? PendingInvoice)
{
    public static async Task<BillingSnapshot> LoadAsync(IAppDbContext db, long organizationId, DateOnly today, bool tracking,
        CancellationToken ct)
    {
        var query = db.Subscriptions.Include(s => s.Plan).Where(s => s.OrganizationId == organizationId
            && (s.Status == SubscriptionStatus.PENDING || (s.Status == SubscriptionStatus.ACTIVE && s.EndDate >= today)));
        var rows = await (tracking ? query : query.AsNoTracking()).ToListAsync(ct);

        var effective = rows.Where(s => s.Status == SubscriptionStatus.ACTIVE && s.StartDate <= today)
            .OrderByDescending(s => s.StartDate).ThenByDescending(s => s.Id).FirstOrDefault();
        var queued = rows.Where(s => s.Status == SubscriptionStatus.ACTIVE && s.StartDate > today)
            .OrderBy(s => s.StartDate).ThenBy(s => s.Id).FirstOrDefault();
        var pending = rows.Where(s => s.Status == SubscriptionStatus.PENDING).OrderByDescending(s => s.Id).FirstOrDefault();

        Invoice? pendingInvoice = null;
        if (pending is not null)
        {
            var invoices = db.Invoices.Where(i => i.SubscriptionId == pending.Id);
            pendingInvoice = await (tracking ? invoices : invoices.AsNoTracking()).OrderByDescending(i => i.Id).FirstOrDefaultAsync(ct);
        }
        return new BillingSnapshot(today, effective, queued, pending, pendingInvoice);
    }

    /// <summary>Có gói hiệu lực hoặc gói chờ bắt đầu.</summary>
    public bool HasAnyActive => Effective is not null || Queued is not null;

    /// <summary>Ngày hết hạn cuối cùng của các gói đang hiệu lực hoặc chờ bắt đầu.</summary>
    public DateOnly? LastEndDate => new[] { Effective?.EndDate, Queued?.EndDate }.Max();
}

/// <summary>Quy tắc mua, gia hạn, nâng cấp: phân loại, chặn và ngày hiệu lực dự kiến. Dùng chung cho danh sách gói, xác nhận và tạo hóa đơn.</summary>
public static class PurchaseRules
{
    public static decimal PriceOf(Plan? plan) => plan?.Price ?? 0m;

    public static PurchaseKind KindFor(BillingSnapshot snapshot, Plan plan) =>
        BillingRules.Classify(snapshot.Effective is null ? null : PriceOf(snapshot.Effective.Plan), PriceOf(plan));

    /// <summary>Loại thao tác và lý do chặn (null nếu mua được).</summary>
    public static (PurchaseKind Kind, AppMessage? Block) Assess(BillingSnapshot snapshot, Plan plan)
    {
        var kind = KindFor(snapshot, plan);
        var name = plan.Name ?? string.Empty;
        if (PriceOf(plan) <= 0m) return (kind, BillingMessages.FreePlanNotSold);
        if (!plan.IsActive) return (kind, BillingMessages.PlanNotOnSale(name));
        if (snapshot.Pending is not null)
            return (kind, BillingMessages.PendingPurchaseExists(snapshot.PendingInvoice?.InvoiceNumber, snapshot.Pending.Plan?.Name ?? name));
        if (snapshot.Queued is { StartDate: { } queuedStart } && kind != PurchaseKind.UPGRADE)
            return (kind, BillingMessages.RenewalAlreadyQueued(queuedStart));
        return (kind, null);
    }

    public static DateOnly ExpectedStart(BillingSnapshot snapshot, PurchaseKind kind) =>
        BillingRules.StartDateFor(kind, snapshot.Today, snapshot.LastEndDate);

    public static int DurationOf(Plan plan) =>
        plan.DurationDays ?? throw new InvalidOperationException($"Gói {plan.Name} chưa có thời hạn (duration_days).");
}

/// <summary>Kết quả kích hoạt một đăng ký sau khi thanh toán thành công.</summary>
public sealed record ActivationOutcome(PurchaseKind Kind, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<long> CancelledSubscriptionIds);

/// <summary>
/// Kích hoạt đăng ký đã thanh toán, trong giao dịch của người gọi: hóa đơn PAID, đăng ký ACTIVE với ngày theo loại mua tính tại thời điểm này
/// (mua mới từ ngày nhận IPN; gia hạn từ ngày sau khi gói hiện tại hết hạn, hoặc từ ngày nhận IPN nếu muộn hơn; nâng cấp có hiệu lực ngay,
/// gói hiện tại và gói gia hạn chờ chuyển CANCELLED, không hoàn tiền). Ghi nhật ký đổi gói do hệ thống.
/// </summary>
public sealed class SubscriptionActivator(IAppDbContext db, IAuditLogger audit)
{
    public async Task<ActivationOutcome> ActivateAsync(Invoice invoice, Subscription subscription, Plan plan, DateOnly today,
        string trigger, CancellationToken ct)
    {
        var others = await db.Subscriptions.Include(s => s.Plan)
            .Where(s => s.OrganizationId == subscription.OrganizationId && s.Id != subscription.Id
                        && s.Status == SubscriptionStatus.ACTIVE && s.EndDate >= today)
            .ToListAsync(ct);
        var effective = others.Where(s => s.StartDate <= today).OrderByDescending(s => s.StartDate).FirstOrDefault();
        var kind = BillingRules.Classify(effective is null ? null : PurchaseRules.PriceOf(effective.Plan), PurchaseRules.PriceOf(plan));

        var cancelled = new List<long>();
        if (kind == PurchaseKind.UPGRADE)
        {
            foreach (var other in others)
            {
                other.Status = SubscriptionStatus.CANCELLED;
                cancelled.Add(other.Id);
            }
        }

        var start = BillingRules.StartDateFor(kind, today, kind == PurchaseKind.UPGRADE ? null : others.Max(o => o.EndDate));
        var end = Subscription.EndDateFor(start, PurchaseRules.DurationOf(plan));
        var previousStatus = subscription.Status;
        subscription.StartDate = start;
        subscription.EndDate = end;
        subscription.Status = SubscriptionStatus.ACTIVE;
        invoice.Status = InvoiceStatus.PAID;

        audit.Record(AuditAction.SUBSCRIPTION_CHANGED, AuditTargets.Subscriptions, subscription.Id, actor: null,
            oldValue: new { status = previousStatus },
            newValue: new
            {
                kind, trigger, plan = plan.Name, planId = plan.Id, status = SubscriptionStatus.ACTIVE, startDate = start, endDate = end,
                invoiceId = invoice.Id, invoiceNumber = invoice.InvoiceNumber, cancelledSubscriptionIds = cancelled,
            });
        return new ActivationOutcome(kind, start, end, cancelled);
    }
}
