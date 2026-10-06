using DSVHVN.Application.Common;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Billing;

/// <summary>Đổi thực thể gói, hóa đơn, giao dịch sang DTO.</summary>
public static class BillingMapper
{
    public static SubscriptionDetailDto ToDetail(Subscription s, DateOnly today)
    {
        var effective = s.IsEffectiveOn(today);
        return new SubscriptionDetailDto(
            s.Id, s.PlanId, s.Plan?.Name, s.Plan?.Price, s.Status, s.StartDate, s.EndDate, effective, s.Plan?.Price == 0m,
            effective && s.EndDate is { } end ? end.DayNumber - today.DayNumber + 1 : null,
            s.Plan?.MaxTeachers, s.Plan?.MaxAiRequests);
    }

    /// <summary>Hóa đơn cần nạp sẵn <c>Subscription.Plan</c>; nạp thêm <c>Organization</c> nếu cần tên trường.</summary>
    public static InvoiceDto ToDto(Invoice i, DateTime nowUtc) => new(
        i.Id, i.OrganizationId, i.Organization?.Name, i.InvoiceNumber, i.Description, i.Amount, i.Status, i.IssuedAt, i.DueAt,
        i.SubscriptionId, i.Subscription?.Plan?.Name, i.Subscription?.Status, CanPay(i, nowUtc));

    /// <summary>Thanh toán (lại) được khi hóa đơn chưa trả, chưa quá hạn và yêu cầu mua còn chờ.</summary>
    public static bool CanPay(Invoice i, DateTime nowUtc) =>
        i.Status == InvoiceStatus.UNPAID && i.DueAt > nowUtc && i.Subscription?.Status == SubscriptionStatus.PENDING;

    public static PaymentDto ToDto(Payment p) =>
        new(p.Id, p.Provider?.Code, p.ProviderTransactionId, p.Amount, p.Status, p.CreatedAt, p.PaidAt);
}

/// <summary>
/// Các thao tác đọc của luồng Gói và thanh toán: tổng quan gói của trường, danh sách gói đang bán, xác nhận mua, hóa đơn và giao dịch,
/// trang kết quả thanh toán. Quản trị trường chỉ thấy dữ liệu của trường mình (id của trường khác trả 404); quản trị hệ thống thấy toàn bộ.
/// </summary>
public sealed class BillingQueries(IAppDbContext db, PlanGuard planGuard, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<BillingOverviewDto> GetOverviewAsync(Actor actor, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        var today = planGuard.Today();
        var snapshot = await BillingSnapshot.LoadAsync(db, orgId, today, tracking: false, ct);

        var shown = snapshot.Effective ?? await db.Subscriptions.AsNoTracking().Include(s => s.Plan)
            .Where(s => s.OrganizationId == orgId && s.Status != SubscriptionStatus.PENDING)
            .OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);

        var canCancel = snapshot.Effective is { } e && PurchaseRules.PriceOf(e.Plan) > 0m && snapshot.Queued is null;
        return new BillingOverviewDto(
            shown is null ? null : BillingMapper.ToDetail(shown, today),
            snapshot.Queued is null ? null : BillingMapper.ToDetail(snapshot.Queued, today),
            await PendingAsync(snapshot, ct),
            snapshot.Effective is not null,
            await planGuard.GetTeacherQuotaAsync(orgId, ct),
            await planGuard.GetAiQuotaAsync(orgId, ct),
            CanActivateFreePlan: !snapshot.HasAnyActive,
            canCancel,
            canCancel ? BillingMessages.CancelPlanConfirm(snapshot.Effective!.Plan?.Name ?? string.Empty).Text : null);
    }

    private async Task<PendingPurchaseDto?> PendingAsync(BillingSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Pending is not { } pending) return null;
        var invoice = snapshot.PendingInvoice;
        var waiting = false;
        if (invoice is not null)
        {
            var since = Now - BillingRules.PaymentWindow;
            waiting = await db.Payments.AnyAsync(p => p.InvoiceId == invoice.Id && p.Status == PaymentStatus.PENDING && p.CreatedAt > since, ct);
        }
        return new PendingPurchaseDto(pending.Id, pending.PlanId, pending.Plan?.Name, invoice?.Id, invoice?.InvoiceNumber,
            invoice?.Amount, invoice?.DueAt, waiting);
    }

    /// <summary>Các gói đang bán (không gồm gói Miễn phí, gói này không bán qua VNPay), theo giá tăng dần, kèm loại thao tác và có mua được không.</summary>
    public async Task<IReadOnlyList<PlanOfferDto>> ListOffersAsync(Actor actor, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        var snapshot = await BillingSnapshot.LoadAsync(db, orgId, planGuard.Today(), tracking: false, ct);
        var plans = (await db.Plans.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct))
            .Where(p => PurchaseRules.PriceOf(p) > 0m)
            .OrderBy(p => p.Price).ThenBy(p => p.Id);

        return plans.Select(p =>
        {
            var (kind, block) = PurchaseRules.Assess(snapshot, p);
            return new PlanOfferDto(p.Id, p.Name, p.Price, p.DurationDays, p.MaxTeachers, p.MaxAiRequests,
                snapshot.Effective?.PlanId == p.Id, kind, block is null, block?.Text);
        }).ToList();
    }

    /// <summary>Khối xác nhận sau khi chọn gói; gói không mua được thì 422 kèm lý do.</summary>
    public async Task<PurchaseQuoteDto> GetQuoteAsync(Actor actor, int planId, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        var plan = await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == planId, ct)
                   ?? throw AppException.NotFound(BillingMessages.PlanNotFound("mua gói"));
        var snapshot = await BillingSnapshot.LoadAsync(db, orgId, planGuard.Today(), tracking: false, ct);
        var (kind, block) = PurchaseRules.Assess(snapshot, plan);
        if (block is not null) throw AppException.BusinessRule(block);

        var start = PurchaseRules.ExpectedStart(snapshot, kind);
        var end = Subscription.EndDateFor(start, PurchaseRules.DurationOf(plan));
        var used = await planGuard.CountTeachersAsync(orgId, ct);
        var warning = plan.MaxTeachers is { } max && used > max
            ? $"Trường đang có {used} tài khoản giáo viên, nhiều hơn {max} của gói {plan.Name}. Không ai bị khóa, nhưng trường chưa thêm được " +
              "giáo viên mới cho tới khi số giáo viên giảm xuống dưới hạn mức."
            : null;
        return new PurchaseQuoteDto(plan.Id, plan.Name, kind, plan.Price ?? 0m, start, end,
            BillingMessages.PaymentConfirm(plan.Price ?? 0m, plan.Name ?? string.Empty, kind, start).Text, warning);
    }

    /// <summary>
    /// Hóa đơn, mới nhất trước. <paramref name="organizationId"/> khác null là phạm vi của quản trị trường; null là quản trị hệ thống (lọc theo
    /// <see cref="InvoiceListQuery.OrganizationId"/> nếu có).
    /// </summary>
    public async Task<PagedResult<InvoiceDto>> ListInvoicesAsync(long? organizationId, InvoiceListQuery query, CancellationToken ct)
    {
        var invoices = db.Invoices.AsNoTracking().Include(i => i.Subscription!).ThenInclude(s => s.Plan).Include(i => i.Organization).AsQueryable();
        var scope = organizationId ?? query.OrganizationId;
        if (scope is { } org) invoices = invoices.Where(i => i.OrganizationId == org);
        if (query.Status is { } status) invoices = invoices.Where(i => i.Status == status);
        if (query.From is { } from) invoices = invoices.Where(i => i.IssuedAt >= from.UtcDateTime);
        if (query.To is { } to) invoices = invoices.Where(i => i.IssuedAt < to.UtcDateTime);

        var page = await invoices.OrderByDescending(i => i.IssuedAt).ThenByDescending(i => i.Id)
            .ToPagedAsync(query.Page, query.PageSize, ct);
        var now = Now;
        return new PagedResult<InvoiceDto>(page.Items.Select(i => BillingMapper.ToDto(i, now)).ToList(),
            page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    public async Task<InvoiceDetailDto> GetInvoiceAsync(long? organizationId, long invoiceId, CancellationToken ct)
    {
        var invoices = db.Invoices.AsNoTracking().Include(i => i.Subscription!).ThenInclude(s => s.Plan).Include(i => i.Organization)
            .Where(i => i.Id == invoiceId);
        if (organizationId is { } org) invoices = invoices.Where(i => i.OrganizationId == org);
        var invoice = await invoices.SingleOrDefaultAsync(ct) ?? throw AppException.NotFound(BillingMessages.InvoiceNotFound("xem hóa đơn"));

        var payments = await db.Payments.AsNoTracking().Include(p => p.Provider).Where(p => p.InvoiceId == invoiceId)
            .OrderByDescending(p => p.Id).ToListAsync(ct);
        return new InvoiceDetailDto(BillingMapper.ToDto(invoice, Now), payments.Select(BillingMapper.ToDto).ToList());
    }

    /// <summary>
    /// Trang kết quả thanh toán: chỉ đọc trạng thái do IPN ghi nhận, không đổi dữ liệu. Giao dịch của trường khác trả 404.
    /// </summary>
    public async Task<PaymentResultDto> GetPaymentResultAsync(Actor actor, long paymentId, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        var payment = await db.Payments.AsNoTracking().Include(p => p.Provider)
                          .Include(p => p.Invoice!).ThenInclude(i => i.Subscription!).ThenInclude(s => s.Plan)
                          .SingleOrDefaultAsync(p => p.Id == paymentId && p.Invoice!.OrganizationId == orgId, ct)
                      ?? throw AppException.NotFound(BillingMessages.PaymentNotFound("xem kết quả thanh toán"));
        var invoice = payment.Invoice!;
        var subscription = invoice.Subscription!;
        var now = Now;
        var invoiceDto = BillingMapper.ToDto(invoice, now);
        var number = invoice.InvoiceNumber ?? string.Empty;

        if (invoice.Status == InvoiceStatus.PAID && subscription.StartDate is { } start && subscription.EndDate is { } end)
            return new PaymentResultDto(PaymentOutcome.PAID, BillingMessages.PaymentSucceeded(subscription.Plan?.Name ?? string.Empty, start, end).Text,
                invoiceDto, BillingMapper.ToDto(payment), BillingMapper.ToDetail(subscription, planGuard.Today()), false);

        if (payment.Status == PaymentStatus.PENDING)
            return new PaymentResultDto(PaymentOutcome.PROCESSING, BillingMessages.PaymentWaiting.Text, invoiceDto, BillingMapper.ToDto(payment), null, false);

        var canPayAgain = invoiceDto.CanPay;
        var message = canPayAgain && invoice.DueAt is { } due
            ? BillingMessages.PaymentFailed(number, due)
            : BillingMessages.PaymentFailedInvoiceClosed(number);
        return new PaymentResultDto(PaymentOutcome.UNPAID, message.Text, invoiceDto, BillingMapper.ToDto(payment), null, canPayAgain);
    }
}
