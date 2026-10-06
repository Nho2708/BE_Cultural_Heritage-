using DSVHVN.Application.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Tác vụ nền của gói và thanh toán: giao dịch treo quá 30 phút thất bại, hóa đơn quá hạn không còn giao dịch chờ bị hủy kèm đăng ký, gói quá ngày hết hạn
/// chuyển hết hạn. Đồng hồ tua được nên không phải chờ.
/// </summary>
public sealed class BillingMaintenanceTests : IDisposable
{
    private readonly BillingHarness _b = new();

    public void Dispose() => _b.Dispose();

    private Task<BillingMaintenanceResult> Run(bool includeExpiry = true) =>
        _b.H.Service<BillingMaintenanceService, BillingMaintenanceResult>(s => s.RunAsync(includeExpiry, default));

    [Fact]
    public async Task A_payment_waiting_over_30_minutes_fails_but_not_before()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var start = await _b.CheckoutAsync(actor, _b.BasicId);

        _b.H.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, (await Run()).PaymentsFailed);                                // đúng 30 phút chưa tính
        Assert.Equal(PaymentStatus.PENDING, (await _b.PaymentAsync(start.PaymentId)).Status);

        _b.H.Clock.Advance(TimeSpan.FromSeconds(1));
        var result = await Run();

        Assert.Equal((1, 0), (result.PaymentsFailed, result.InvoicesCancelled));
        Assert.Equal(PaymentStatus.FAILED, (await _b.PaymentAsync(start.PaymentId)).Status);
        Assert.Equal(InvoiceStatus.UNPAID, (await _b.InvoiceAsync(start.Invoice.Id)).Status);                 // hóa đơn vẫn thanh toán lại được
        Assert.Equal(SubscriptionStatus.PENDING, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status);
        var log = await _b.H.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.BILLING_STATUS_CHANGED));
        Assert.Equal((null, ActorRole.SYSTEM, start.PaymentId), (log.UserId, log.ActorRole, log.TargetId));
        Assert.Equal(0, (await Run()).Total);                                          // chạy lại không đổi gì thêm
    }

    [Fact]
    public async Task Finished_payments_are_left_alone()
    {
        var (_, a) = await _b.NewOrgAsync(withFreePlan: false);
        var (_, b) = await _b.NewOrgAsync(withFreePlan: false);
        var paid = await _b.BuyAsync(a, _b.BasicId);
        var failed = await _b.CheckoutAsync(b, _b.BasicId);
        await _b.IpnAsync(failed.PaymentId, 500_000m, responseCode: "24", transactionStatus: "02");
        _b.H.Clock.Advance(TimeSpan.FromHours(2));

        var result = await Run(includeExpiry: false);

        Assert.Equal(0, result.PaymentsFailed);
        Assert.Equal((PaymentStatus.SUCCEEDED, PaymentStatus.FAILED), ((await _b.PaymentAsync(paid.PaymentId)).Status, (await _b.PaymentAsync(failed.PaymentId)).Status));
        Assert.Contains("\"vnp_ResponseCode\":\"24\"", (await _b.PaymentAsync(failed.PaymentId)).RawResponse);   // dữ liệu của cổng giữ nguyên
    }

    [Fact]
    public async Task An_overdue_invoice_without_a_waiting_payment_is_cancelled_together_with_its_pending_subscription()
    {
        var (org, actor) = await _b.NewOrgAsync();
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        await _b.IpnAsync(start.PaymentId, 500_000m, responseCode: "24", transactionStatus: "02");

        _b.H.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(0, (await Run()).InvoicesCancelled);                              // đúng hạn chưa hủy
        _b.H.Clock.Advance(TimeSpan.FromSeconds(1));
        var result = await Run();

        Assert.Equal(1, result.InvoicesCancelled);
        Assert.Equal(InvoiceStatus.CANCELLED, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
        Assert.Equal(SubscriptionStatus.CANCELLED, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status);
        var log = await _b.H.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.BILLING_STATUS_CHANGED && l.TargetType == "invoices"));
        Assert.Equal((null, ActorRole.SYSTEM, start.Invoice.Id), (log.UserId, log.ActorRole, log.TargetId));
        var overview = await _b.OverviewAsync(actor);
        Assert.Null(overview.Pending);
        var again = await _b.CheckoutAsync(actor, _b.BasicId);                         // trường mua lại được
        Assert.NotEqual(start.Invoice.Id, again.Invoice.Id);
        Assert.Equal(2, (await _b.SubscriptionsAsync(org.Id)).Count(s => s.Status is SubscriptionStatus.PENDING or SubscriptionStatus.CANCELLED));
    }

    [Fact]
    public async Task One_run_fails_stale_payments_before_cancelling_overdue_invoices()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        _b.H.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));          // giao dịch treo cũ hơn 24 giờ và hóa đơn quá hạn

        var result = await Run();

        Assert.Equal((1, 1), (result.PaymentsFailed, result.InvoicesCancelled));
        Assert.Equal(PaymentStatus.FAILED, (await _b.PaymentAsync(start.PaymentId)).Status);
        Assert.Equal(InvoiceStatus.CANCELLED, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
    }

    [Fact]
    public async Task An_overdue_invoice_with_a_recent_waiting_payment_is_kept_until_that_payment_is_resolved()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var first = await _b.CheckoutAsync(actor, _b.BasicId);
        _b.H.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(50));
        var last = await _b.PayAsync(actor, first.Invoice.Id);                         // giao dịch mới ngay trước hạn

        _b.H.Clock.Advance(TimeSpan.FromMinutes(20));                                   // hóa đơn quá hạn 10 phút, giao dịch mới 20 phút tuổi
        var during = await Run();
        Assert.Equal(0, during.Total);
        Assert.Equal((InvoiceStatus.UNPAID, PaymentStatus.PENDING), ((await _b.InvoiceAsync(first.Invoice.Id)).Status, (await _b.PaymentAsync(last.PaymentId)).Status));

        _b.H.Clock.Advance(TimeSpan.FromMinutes(11));
        var after = await Run();
        Assert.Equal((1, 1), (after.PaymentsFailed, after.InvoicesCancelled));
        Assert.Equal(InvoiceStatus.CANCELLED, (await _b.InvoiceAsync(first.Invoice.Id)).Status);
    }

    [Fact]
    public async Task Paid_invoices_and_active_subscriptions_are_never_cancelled_by_the_job()
    {
        var (org, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var bought = await _b.BuyAsync(actor, _b.BasicId);
        _b.H.Clock.Advance(TimeSpan.FromDays(3));

        var result = await Run();

        Assert.Equal(0, result.Total);
        Assert.Equal((InvoiceStatus.PAID, SubscriptionStatus.ACTIVE), ((await _b.InvoiceAsync(bought.Invoice.Id)).Status, (await _b.SubscriptionsAsync(org.Id)).Single().Status));
    }

    [Fact]
    public async Task A_plan_becomes_expired_the_day_after_its_end_date_and_not_before()
    {
        var (org, actor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(actor, _b.BasicId);                                          // 29/09 → 28/10

        _b.H.Clock.Advance(TimeSpan.FromDays(29));                                      // 28/10, ngày cuối còn dùng được
        Assert.Equal(0, (await Run()).SubscriptionsExpired);
        Assert.True((await _b.OverviewAsync(actor)).HasEffectivePlan);

        _b.H.Clock.Advance(TimeSpan.FromDays(1));                                       // 29/10
        var result = await Run();

        Assert.Equal(1, result.SubscriptionsExpired);
        Assert.Equal(SubscriptionStatus.EXPIRED, (await _b.SubscriptionsAsync(org.Id)).Single().Status);
        var overview = await _b.OverviewAsync(actor);
        Assert.Equal((false, SubscriptionStatus.EXPIRED, true), (overview.HasEffectivePlan, overview.Current!.Status, overview.CanActivateFreePlan));
        var log = await _b.H.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.BILLING_STATUS_CHANGED && l.TargetType == "subscriptions"));
        Assert.Equal((null, ActorRole.SYSTEM), (log.UserId, log.ActorRole));
        Assert.Contains("EXPIRED", log.NewValue);
        Assert.Equal(0, (await Run()).SubscriptionsExpired);
    }

    [Fact]
    public async Task Expiry_is_skipped_unless_asked_and_a_queued_renewal_expires_on_its_own_date()
    {
        var (org, actor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(actor, _b.BasicId);                                          // 29/09 → 28/10
        await _b.BuyAsync(actor, _b.BasicId);                                          // 29/10 → 27/11
        _b.H.Clock.Advance(TimeSpan.FromDays(35));                                      // 03/11: gói đầu hết, gói gia hạn đang chạy

        Assert.Equal(0, (await Run(includeExpiry: false)).SubscriptionsExpired);
        Assert.Equal(1, (await Run()).SubscriptionsExpired);

        var states = (await _b.SubscriptionsAsync(org.Id)).Select(s => s.Status).ToList();
        Assert.Equal([SubscriptionStatus.EXPIRED, SubscriptionStatus.ACTIVE], states);
        Assert.True((await _b.OverviewAsync(actor)).HasEffectivePlan);                  // gói gia hạn đã tới ngày bắt đầu
    }
}
