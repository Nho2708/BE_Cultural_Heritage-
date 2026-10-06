using System.Net;
using System.Net.Sockets;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Billing;

/// <summary>
/// Các thao tác ghi của quản trị trường về gói của trường: mua, gia hạn, nâng cấp qua VNPay (tạo đăng ký chờ thanh toán, hóa đơn chưa trả và giao dịch chờ
/// trong một giao dịch CSDL rồi dựng địa chỉ VNPay), thanh toán lại hóa đơn chưa trả, hủy gói hoặc hủy yêu cầu mua, kích hoạt gói Miễn phí.
/// Mọi thao tác khóa dòng trường trước khi đọc trạng thái gói để hai yêu cầu cùng lúc của một trường chạy tuần tự. Gói chỉ ACTIVE khi IPN hợp lệ
/// về tới <see cref="VnPayCallbackService"/>; các thao tác ở đây không bao giờ kích hoạt gói trả phí.
/// </summary>
public sealed class BillingService(
    IAppDbContext db,
    RequestValidator validator,
    IVnPayGateway gateway,
    PlanGuard planGuard,
    BillingQueries queries,
    IAuditLogger audit,
    TimeProvider clock)
{
    /// <summary>Số lần thử lại khi hai trường cùng lúc xin trùng số hóa đơn trong ngày.</summary>
    private const int InvoiceNumberAttempts = 3;

    /// <summary>
    /// Mua, gia hạn hoặc nâng cấp: tạo đăng ký PENDING, hóa đơn UNPAID (số tiền = giá gói lúc này, hạn 24 giờ) và giao dịch PENDING, trả địa chỉ VNPay.
    /// Loại thao tác do hệ thống phân loại theo gói hiện tại của trường.
    /// </summary>
    public async Task<PaymentStartDto> StartCheckoutAsync(Actor actor, CheckoutRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var orgId = actor.RequireOrganizationId();
        EnsureGatewayReady();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CheckoutOnceAsync(actor, orgId, request.PlanId!.Value, ct);
            }
            catch (DbUpdateException) when (attempt < InvoiceNumberAttempts)
            {
                // Số hóa đơn trong ngày bị trường khác lấy trước: bỏ thay đổi chưa lưu và xin số mới.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<PaymentStartDto> CheckoutOnceAsync(Actor actor, long orgId, int planId, CancellationToken ct)
    {
        Invoice invoice;
        Payment payment;
        PurchaseKind kind;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.LockOrganizationAsync(orgId, ct);
            var plan = await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == planId, ct)
                       ?? throw AppException.NotFound(BillingMessages.PlanNotFound("mua gói"));
            var now = Now();
            var today = VietnamTime.Today(new DateTimeOffset(now, TimeSpan.Zero));
            var snapshot = await BillingSnapshot.LoadAsync(db, orgId, today, tracking: false, ct);
            var (assessedKind, block) = PurchaseRules.Assess(snapshot, plan);
            if (block is not null) throw AppException.BusinessRule(block);
            kind = assessedKind;
            var provider = await ActiveProviderAsync(ct);

            var subscription = new Subscription { OrganizationId = orgId, PlanId = plan.Id, Status = SubscriptionStatus.PENDING };
            invoice = new Invoice
            {
                OrganizationId = orgId,
                Subscription = subscription,
                InvoiceNumber = await NextInvoiceNumberAsync(today, ct),
                Amount = plan.Price,
                Description = $"{VerbOf(kind)} {plan.Name} ({plan.DurationDays} ngày)",
                Status = InvoiceStatus.UNPAID,
                IssuedAt = now,
                DueAt = now + BillingRules.InvoiceDueIn,
            };
            payment = new Payment { Invoice = invoice, ProviderId = provider.Id, Amount = plan.Price, Status = PaymentStatus.PENDING, CreatedAt = now };
            db.Subscriptions.Add(subscription);
            db.Invoices.Add(invoice);
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);

            audit.Record(AuditAction.SUBSCRIPTION_CHANGED, AuditTargets.Subscriptions, subscription.Id, actor, newValue: new
            {
                kind, plan = plan.Name, planId = plan.Id, status = SubscriptionStatus.PENDING,
                invoiceId = invoice.Id, invoiceNumber = invoice.InvoiceNumber, amount = invoice.Amount,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return await StartDtoAsync(actor, invoice, payment, kind, ct);
    }

    /// <summary>
    /// "Thanh toán" hóa đơn chưa trả (kể cả sau một lần thất bại): tạo giao dịch mới. Mỗi hóa đơn chỉ có tối đa một giao dịch chờ nên giao dịch chờ cũ
    /// (nếu còn) được chuyển thất bại trước; nếu cổng VNPay vẫn ghi nhận kết quả thành công cho giao dịch cũ thì IPN đó vẫn được xử lý.
    /// Hóa đơn quá hạn, đã trả hoặc đã hủy thì 422.
    /// </summary>
    public async Task<PaymentStartDto> PayInvoiceAsync(Actor actor, long invoiceId, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        EnsureGatewayReady();

        Invoice invoice;
        Payment payment;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.LockOrganizationAsync(orgId, ct);
            invoice = await db.Invoices.Include(i => i.Subscription!).ThenInclude(s => s.Plan)
                          .SingleOrDefaultAsync(i => i.Id == invoiceId && i.OrganizationId == orgId, ct)
                      ?? throw AppException.NotFound(BillingMessages.InvoiceNotFound("thanh toán hóa đơn"));
            var now = Now();
            if (invoice.Status == InvoiceStatus.PAID) throw AppException.BusinessRule(BillingMessages.InvoiceNotPayable("Hóa đơn đã được thanh toán."));
            if (invoice.Status == InvoiceStatus.CANCELLED) throw AppException.BusinessRule(BillingMessages.InvoiceNotPayable("Hóa đơn đã bị hủy."));
            if (invoice.DueAt <= now) throw AppException.BusinessRule(BillingMessages.InvoiceNotPayable(
                $"Hóa đơn đã quá hạn thanh toán lúc {BillingRules.FormatMoment(invoice.DueAt!.Value)}. Hãy chọn gói và mua lại."));
            if (invoice.Subscription?.Status != SubscriptionStatus.PENDING)
                throw AppException.BusinessRule(BillingMessages.InvoiceNotPayable("Yêu cầu mua gói của hóa đơn này không còn chờ thanh toán."));

            var provider = await ActiveProviderAsync(ct);
            var stale = await db.Payments.AsNoTracking().Where(p => p.InvoiceId == invoice.Id && p.Status == PaymentStatus.PENDING)
                .Select(p => p.Id).ToListAsync(ct);
            foreach (var id in stale)
            {
                if (!await PaymentClaims.TryFailPendingAsync(db, id, ct)) continue;
                audit.Record(AuditAction.BILLING_STATUS_CHANGED, AuditTargets.Payments, id, actor,
                    oldValue: new { status = PaymentStatus.PENDING },
                    newValue: new { status = PaymentStatus.FAILED, reason = "replaced by a new payment attempt", invoiceId = invoice.Id });
            }

            payment = new Payment { InvoiceId = invoice.Id, ProviderId = provider.Id, Amount = invoice.Amount, Status = PaymentStatus.PENDING, CreatedAt = now };
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return await StartDtoAsync(actor, invoice, payment, kind: null, ct);
    }

    /// <summary>
    /// Hủy một đăng ký của trường mình. Yêu cầu mua chưa thanh toán (PENDING): đăng ký và hóa đơn chuyển CANCELLED, giao dịch chờ chuyển thất bại.
    /// Gói trả phí đang hiệu lực: chuyển CANCELLED ngay, không hoàn tiền. Gói Miễn phí và gói gia hạn đã thanh toán không hủy được; gói hiện tại cũng không
    /// hủy được khi đã có gói gia hạn chờ (vì gói gia hạn không hủy được).
    /// </summary>
    public async Task<BillingOverviewDto> CancelAsync(Actor actor, long subscriptionId, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.LockOrganizationAsync(orgId, ct);
            var subscription = await db.Subscriptions.Include(s => s.Plan)
                                   .SingleOrDefaultAsync(s => s.Id == subscriptionId && s.OrganizationId == orgId, ct)
                               ?? throw AppException.NotFound(BillingMessages.SubscriptionNotFound("hủy gói"));
            var today = planGuard.Today();

            if (subscription.Status == SubscriptionStatus.PENDING)
                await CancelPendingAsync(actor, subscription, ct);
            else if (subscription.Status == SubscriptionStatus.ACTIVE && subscription.EndDate >= today)
                await CancelActiveAsync(actor, subscription, orgId, today, ct);
            else
                throw AppException.BusinessRule(BillingMessages.NothingToCancel);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await queries.GetOverviewAsync(actor, ct);
    }

    private async Task CancelPendingAsync(Actor actor, Subscription subscription, CancellationToken ct)
    {
        subscription.Status = SubscriptionStatus.CANCELLED;
        var invoice = await db.Invoices.Where(i => i.SubscriptionId == subscription.Id && i.Status == InvoiceStatus.UNPAID).ToListAsync(ct);
        foreach (var i in invoice) i.Status = InvoiceStatus.CANCELLED;

        var invoiceIds = invoice.Select(i => i.Id).ToList();
        var pendingPayments = await db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.PENDING && invoiceIds.Contains(p.InvoiceId))
            .Select(p => p.Id).ToListAsync(ct);
        foreach (var id in pendingPayments)
        {
            if (await PaymentClaims.TryFailPendingAsync(db, id, ct))
                audit.Record(AuditAction.BILLING_STATUS_CHANGED, AuditTargets.Payments, id, actor,
                    oldValue: new { status = PaymentStatus.PENDING },
                    newValue: new { status = PaymentStatus.FAILED, reason = "purchase request cancelled" });
        }

        audit.Record(AuditAction.SUBSCRIPTION_CHANGED, AuditTargets.Subscriptions, subscription.Id, actor,
            oldValue: new { status = SubscriptionStatus.PENDING },
            newValue: new
            {
                action = "purchase request cancelled", status = SubscriptionStatus.CANCELLED, plan = subscription.Plan?.Name,
                invoiceNumbers = invoice.Select(i => i.InvoiceNumber).ToList(),
            });
    }

    private async Task CancelActiveAsync(Actor actor, Subscription subscription, long orgId, DateOnly today, CancellationToken ct)
    {
        if (PurchaseRules.PriceOf(subscription.Plan) <= 0m) throw AppException.BusinessRule(BillingMessages.FreePlanCannotBeCancelled);
        if (subscription.StartDate > today)
            throw AppException.BusinessRule(BillingMessages.QueuedPlanCannotBeCancelled(subscription.Plan?.Name ?? string.Empty));

        var queued = await db.Subscriptions.AsNoTracking()
            .Where(s => s.OrganizationId == orgId && s.Status == SubscriptionStatus.ACTIVE && s.StartDate > today)
            .OrderBy(s => s.StartDate).FirstOrDefaultAsync(ct);
        if (queued?.StartDate is { } queuedStart)
            throw AppException.BusinessRule(BillingMessages.CurrentPlanHasQueuedRenewal(queuedStart));

        subscription.Status = SubscriptionStatus.CANCELLED;
        audit.Record(AuditAction.SUBSCRIPTION_CHANGED, AuditTargets.Subscriptions, subscription.Id, actor,
            oldValue: new { status = SubscriptionStatus.ACTIVE, startDate = subscription.StartDate, endDate = subscription.EndDate },
            newValue: new { action = "plan cancelled", status = SubscriptionStatus.CANCELLED, plan = subscription.Plan?.Name });
    }

    /// <summary>
    /// Kích hoạt gói Miễn phí ngay, không qua VNPay, không tạo hóa đơn: chỉ khi trường không có gói hiệu lực và không có gói gia hạn chờ.
    /// Yêu cầu mua chưa thanh toán không cản trở.
    /// </summary>
    public async Task<BillingOverviewDto> ActivateFreePlanAsync(Actor actor, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.LockOrganizationAsync(orgId, ct);
            var snapshot = await BillingSnapshot.LoadAsync(db, orgId, planGuard.Today(), tracking: false, ct);
            if (snapshot.HasAnyActive) throw AppException.BusinessRule(BillingMessages.FreePlanNeedsNoPlan);

            var (subscription, plan) = await planGuard.AddFreeSubscriptionAsync(orgId, ct);
            await db.SaveChangesAsync(ct);
            audit.Record(AuditAction.SUBSCRIPTION_CHANGED, AuditTargets.Subscriptions, subscription.Id, actor, newValue: new
            {
                action = "free plan activated", plan = plan.Name, planId = plan.Id, status = SubscriptionStatus.ACTIVE,
                startDate = subscription.StartDate, endDate = subscription.EndDate,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await queries.GetOverviewAsync(actor, ct);
    }

    // ---- Dùng chung ----

    private async Task<PaymentStartDto> StartDtoAsync(Actor actor, Invoice invoice, Payment payment, PurchaseKind? kind, CancellationToken ct)
    {
        var created = payment.CreatedAt!.Value;
        var expires = created + BillingRules.PaymentWindow;
        var orderInfo = SearchText.Fold($"Thanh toan hoa don {invoice.InvoiceNumber}");
        var url = gateway.CreatePaymentUrl(new VnPayPaymentRequest(
            payment.Id, payment.Amount ?? 0m, orderInfo, created, expires, IPv4Of(actor.IpAddress)));

        var loaded = await db.Invoices.AsNoTracking().Include(i => i.Subscription!).ThenInclude(s => s.Plan)
            .SingleAsync(i => i.Id == invoice.Id, ct);
        return new PaymentStartDto(BillingMapper.ToDto(loaded, Now()), payment.Id, url, expires, kind);
    }

    private void EnsureGatewayReady()
    {
        if (!gateway.IsConfigured) throw new AppException(503, BillingMessages.GatewayUnavailable);
    }

    private async Task<PaymentProvider> ActiveProviderAsync(CancellationToken ct) =>
        await db.PaymentProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Code == BillingRules.VnPayProviderCode && p.IsActive, ct)
        ?? throw new AppException(503, BillingMessages.GatewayUnavailable);

    /// <summary>Số hóa đơn kế tiếp của ngày: số lớn nhất đã có cộng một. Hai trường lấy cùng số thì chỉ mục duy nhất từ chối một bên và bên đó thử lại.</summary>
    private async Task<string> NextInvoiceNumberAsync(DateOnly today, CancellationToken ct)
    {
        var prefix = InvoiceNumber.DayPrefix(today);
        var last = await db.Invoices.AsNoTracking().Where(i => i.InvoiceNumber!.StartsWith(prefix))
            .OrderByDescending(i => i.InvoiceNumber).Select(i => i.InvoiceNumber).FirstOrDefaultAsync(ct);
        return InvoiceNumber.Format(today, InvoiceNumber.SequenceOf(last, today) + 1);
    }

    private static string VerbOf(PurchaseKind kind) => kind switch
    {
        PurchaseKind.RENEW => "Gia hạn gói",
        PurchaseKind.UPGRADE => "Nâng cấp lên gói",
        _ => "Mua gói",
    };

    /// <summary>VNPay yêu cầu địa chỉ IPv4 của người mua; không có (kể cả IPv6) thì dùng địa chỉ nội bộ.</summary>
    private static string IPv4Of(string? ip) =>
        IPAddress.TryParse(ip, out var address) && address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() : "127.0.0.1";

    /// <summary>Giờ hiện tại cắt về mili giây cho khớp datetime2(3).</summary>
    private DateTime Now()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}

/// <summary>
/// Chuyển trạng thái giao dịch có điều kiện bằng một câu lệnh cập nhật duy nhất ("chỉ khi còn PENDING"), để tác vụ nền, IPN và thao tác của người dùng
/// không ghi đè kết quả của nhau. Trả true nếu chính lần gọi này đổi được.
/// </summary>
public static class PaymentClaims
{
    public static async Task<bool> TryFailPendingAsync(IAppDbContext db, long paymentId, CancellationToken ct) =>
        await db.Payments.Where(p => p.Id == paymentId && p.Status == PaymentStatus.PENDING)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.FAILED), ct) == 1;
}
