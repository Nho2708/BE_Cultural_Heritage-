using DSVHVN.Application.Audit;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Mua, gia hạn, nâng cấp, thanh toán lại, hủy và kích hoạt gói Miễn phí ở tầng service. Đồng hồ cố định ở 08:00 giờ Việt Nam ngày 29/09/2026.
/// Bước kích hoạt gói bằng IPN nằm ở <see cref="VnPayCallbackServiceTests"/>; ở đây IPN chỉ dùng để dựng trạng thái.
/// </summary>
public sealed class BillingServiceTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 29);
    private readonly BillingHarness _b = new();

    public void Dispose() => _b.Dispose();

    private async Task<AppException> Fails(Func<Task> action) => await Assert.ThrowsAsync<AppException>(action);

    private Task<T> Billing<T>(Func<BillingService, Task<T>> action) => _b.H.Service(action);

    private Task<T> Queries<T>(Func<BillingQueries, Task<T>> action) => _b.H.Service(action);

    // ---- Danh sách gói và xác nhận ----

    [Fact]
    public async Task Offers_list_paid_plans_on_sale_with_the_kind_of_purchase_for_the_current_plan()
    {
        var (_, free) = await _b.NewOrgAsync();
        var (_, none) = await _b.NewOrgAsync(withFreePlan: false);

        var onFree = await Queries(q => q.ListOffersAsync(free, default));
        Assert.Equal(["Cơ bản", "Nâng cao"], onFree.Select(o => o.Name));                    // không có gói Miễn phí, giá tăng dần
        Assert.All(onFree, o => Assert.Equal((PurchaseKind.UPGRADE, true, false), (o.Kind, o.Available, o.IsCurrent)));
        Assert.All(await Queries(q => q.ListOffersAsync(none, default)), o => Assert.Equal(PurchaseKind.PURCHASE, o.Kind));

        await _b.BuyAsync(none, _b.BasicId);
        var afterBuying = await Queries(q => q.ListOffersAsync(none, default));
        var basic = afterBuying.Single(o => o.Name == "Cơ bản");
        Assert.Equal((PurchaseKind.RENEW, true, true), (basic.Kind, basic.IsCurrent, basic.Available));
        Assert.Equal(PurchaseKind.UPGRADE, afterBuying.Single(o => o.Name == "Nâng cao").Kind);
    }

    [Fact]
    public async Task Plans_that_are_not_on_sale_are_not_offered_and_cannot_be_bought()
    {
        var (_, actor) = await _b.NewOrgAsync();
        await _b.H.Service<PlanService, PlanDto>(s => s.DisableAsync(_b.ProId, _b.Admin, default));

        Assert.Equal(["Cơ bản"], (await Queries(q => q.ListOffersAsync(actor, default))).Select(o => o.Name));
        var ex = await Fails(() => _b.CheckoutAsync(actor, _b.ProId));
        Assert.Equal((422, "PLAN_NOT_ON_SALE", "Không thể mua gói. Gói Nâng cao hiện không được bán."), (ex.StatusCode, ex.AppMessage.Code, ex.Message));
    }

    [Fact]
    public async Task The_free_plan_is_not_sold_through_vnpay()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var freeId = await _b.H.QueryAsync(db => db.Plans.Where(p => p.Price == 0m).Select(p => p.Id).SingleAsync());

        var ex = await Fails(() => _b.CheckoutAsync(actor, freeId));

        Assert.Equal((422, "PLAN_NOT_ON_SALE"), (ex.StatusCode, ex.AppMessage.Code));
        Assert.Contains("Kích hoạt gói Miễn phí", ex.Message);
        Assert.Equal(0, await _b.H.QueryAsync(db => db.Invoices.CountAsync()));
    }

    [Fact]
    public async Task Quote_for_a_first_purchase_has_the_catalog_wording_and_starts_on_the_payment_day()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);

        var quote = await Queries(q => q.GetQuoteAsync(actor, _b.BasicId, default));

        Assert.Equal((PurchaseKind.PURCHASE, 500_000m, Day, Day.AddDays(29)), (quote.Kind, quote.Amount, quote.ExpectedStartDate, quote.ExpectedEndDate));
        Assert.Equal("Trường sẽ thanh toán 500.000 đồng cho gói Cơ bản qua VNPay. Gói có hiệu lực từ ngày thanh toán. Tiếp tục?", quote.Confirmation);
        Assert.Null(quote.TeacherWarning);
    }

    [Fact]
    public async Task Quote_for_an_upgrade_from_the_free_plan_says_it_ends_and_old_ai_requests_do_not_carry_over()
    {
        var (_, actor) = await _b.NewOrgAsync();

        var quote = await Queries(q => q.GetQuoteAsync(actor, _b.ProId, default));

        Assert.Equal(PurchaseKind.UPGRADE, quote.Kind);
        Assert.Equal("Trường sẽ thanh toán 1.990.000 đồng cho gói Nâng cao qua VNPay. " +
                     "Gói mới có hiệu lực ngay. Gói Miễn phí sẽ kết thúc ngay khi gói mới có hiệu lực (không cộng dồn lượt AI cũ). Tiếp tục?", quote.Confirmation);
        Assert.Equal((Day, Day.AddDays(364)), (quote.ExpectedStartDate, quote.ExpectedEndDate));
    }

    [Fact]
    public async Task Quote_for_a_renewal_starts_the_day_after_the_current_plan_ends_and_warns_when_there_are_too_many_teachers()
    {
        var (org, actor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(actor, _b.BasicId);                                   // 29/09 → 28/10, tối đa 5 giáo viên
        for (var i = 0; i < 6; i++) await _b.H.AddUserAsync($"gv{i}.{org.Id}", RoleCode.TEACHER, org.Id);

        var quote = await Queries(q => q.GetQuoteAsync(actor, _b.BasicId, default));

        Assert.Equal((PurchaseKind.RENEW, new DateOnly(2026, 10, 29), new DateOnly(2026, 11, 27)), (quote.Kind, quote.ExpectedStartDate, quote.ExpectedEndDate));
        Assert.Contains("Gói mới bắt đầu từ ngày 29/10/2026.", quote.Confirmation);
        Assert.Equal("Trường đang có 6 tài khoản giáo viên, nhiều hơn 5 của gói Cơ bản. Không ai bị khóa, nhưng trường chưa thêm được giáo viên mới " +
                     "cho tới khi số giáo viên giảm xuống dưới hạn mức.", quote.TeacherWarning);
    }

    [Fact]
    public async Task Quote_of_a_plan_that_cannot_be_bought_is_blocked_with_the_reason()
    {
        var (_, actor) = await _b.NewOrgAsync();
        await _b.CheckoutAsync(actor, _b.BasicId);

        var ex = await Fails(() => Queries(q => q.GetQuoteAsync(actor, _b.ProId, default)));

        Assert.Equal((422, "PENDING_PURCHASE_EXISTS"), (ex.StatusCode, ex.AppMessage.Code));
        Assert.Equal(404, (await Fails(() => Queries(q => q.GetQuoteAsync(actor, 9999, default)))).StatusCode);
    }

    // ---- Tạo yêu cầu mua ----

    [Fact]
    public async Task Checkout_creates_a_pending_subscription_an_unpaid_invoice_and_a_pending_payment_in_one_step()
    {
        var (org, actor) = await _b.NewOrgAsync();            // đang dùng gói Miễn phí

        var start = await _b.CheckoutAsync(actor, _b.BasicId);

        Assert.Equal(PurchaseKind.UPGRADE, start.Kind);
        var invoice = start.Invoice;
        Assert.Equal(("HD20260929-000001", 500_000m, InvoiceStatus.UNPAID, "Cơ bản", SubscriptionStatus.PENDING, true),
            (invoice.InvoiceNumber, invoice.Amount, invoice.Status, invoice.PlanName, invoice.SubscriptionStatus, invoice.CanPay));
        Assert.Equal("Nâng cấp lên gói Cơ bản (30 ngày)", invoice.Description);
        Assert.Equal(_b.H.Clock.GetUtcNow().UtcDateTime, invoice.IssuedAt!.Value);
        Assert.Equal(invoice.IssuedAt!.Value.AddHours(24), invoice.DueAt);
        Assert.Equal(invoice.IssuedAt.Value.AddMinutes(15), start.ExpiresAt);

        var subscriptions = await _b.SubscriptionsAsync(org.Id);
        Assert.Equal([SubscriptionStatus.ACTIVE, SubscriptionStatus.PENDING], subscriptions.Select(s => s.Status));   // gói Miễn phí giữ nguyên tới khi IPN về
        var pending = subscriptions[1];
        Assert.Equal((_b.BasicId, null, null), (pending.PlanId, pending.StartDate, pending.EndDate));
        var payment = await _b.PaymentAsync(start.PaymentId);
        Assert.Equal((PaymentStatus.PENDING, 500_000m, invoice.Id, null), (payment.Status, payment.Amount, payment.InvoiceId, payment.ProviderTransactionId));
        Assert.Equal(invoice.IssuedAt, payment.CreatedAt);
    }

    [Fact]
    public async Task Checkout_returns_a_signed_vnpay_url_for_the_payment_with_the_amount_times_100()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);

        var start = await _b.CheckoutAsync(actor, _b.ProId);

        var parameters = VnPayTestKit.ParseUrl(start.PaymentUrl);
        Assert.StartsWith("https://sandbox.vnpayment.vn/paymentv2/vpcpay.html?", start.PaymentUrl);
        Assert.Equal((start.PaymentId.ToString(), "199000000", "203.0.113.6"), (parameters["vnp_TxnRef"], parameters["vnp_Amount"], parameters["vnp_IpAddr"]));
        Assert.Equal("thanh toan hoa don hd20260929 000001", parameters["vnp_OrderInfo"]);       // không dấu, không ký tự đặc biệt
        Assert.Equal("20260929080000", parameters["vnp_CreateDate"]);                              // 08:00 giờ Việt Nam
        Assert.Equal("20260929081500", parameters["vnp_ExpireDate"]);
        var parsed = await _b.H.RunAsync(sp => Task.FromResult(sp.GetRequiredService<IVnPayGateway>().Parse(parameters)));
        Assert.True(parsed.SignatureValid);
    }

    [Fact]
    public async Task Checkout_logs_the_purchase_request()
    {
        var (_, actor) = await _b.NewOrgAsync();

        var start = await _b.CheckoutAsync(actor, _b.BasicId);

        var log = await _b.H.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.SUBSCRIPTION_CHANGED));
        Assert.Equal((actor.UserId, ActorRole.ORG_ADMIN, AuditTargets.Subscriptions, "203.0.113.6"), (log.UserId!.Value, log.ActorRole, log.TargetType, log.IpAddress));
        Assert.Contains("HD20260929-000001", log.NewValue);
        Assert.Contains("UPGRADE", log.NewValue);
        Assert.Contains("PENDING", log.NewValue);
        Assert.DoesNotContain("vnp_", log.NewValue);
        Assert.Equal(start.Invoice.SubscriptionId, log.TargetId);
    }

    [Fact]
    public async Task A_school_with_an_unpaid_purchase_cannot_start_another_and_nothing_is_created()
    {
        var (org, actor) = await _b.NewOrgAsync();
        var first = await _b.CheckoutAsync(actor, _b.BasicId);

        var ex = await Fails(() => _b.CheckoutAsync(actor, _b.ProId));

        Assert.Equal((422, "PENDING_PURCHASE_EXISTS"), (ex.StatusCode, ex.AppMessage.Code));
        Assert.Equal("Không thể mua gói. Trường đã có yêu cầu mua gói Cơ bản chưa thanh toán (hóa đơn HD20260929-000001). " +
                     "Hãy thanh toán hoặc hủy yêu cầu đó trước.", ex.Message);
        Assert.Equal(1, await _b.H.QueryAsync(db => db.Invoices.CountAsync(i => i.OrganizationId == org.Id)));
        Assert.Equal(1, await _b.H.QueryAsync(db => db.Payments.CountAsync()));
        Assert.NotNull(first);
    }

    [Fact]
    public async Task Invoice_numbers_count_up_within_a_day_across_schools_and_restart_the_next_day()
    {
        var (_, a) = await _b.NewOrgAsync();
        var (_, b) = await _b.NewOrgAsync();

        var first = await _b.CheckoutAsync(a, _b.BasicId);
        var second = await _b.CheckoutAsync(b, _b.BasicId);
        _b.H.Clock.Advance(TimeSpan.FromDays(1));
        var (_, c) = await _b.NewOrgAsync();
        var nextDay = await _b.CheckoutAsync(c, _b.BasicId);

        Assert.Equal(("HD20260929-000001", "HD20260929-000002", "HD20260930-000001"),
            (first.Invoice.InvoiceNumber, second.Invoice.InvoiceNumber, nextDay.Invoice.InvoiceNumber));
    }

    [Fact]
    public async Task Checkout_input_is_validated_and_unknown_plans_are_not_found()
    {
        var (_, actor) = await _b.NewOrgAsync();

        var missing = await Fails(() => Billing(s => s.StartCheckoutAsync(actor, new CheckoutRequest(null), default)));
        Assert.Equal((400, "Vui lòng nhập gói cần mua."), (missing.StatusCode, missing.Errors!["planId"]));
        Assert.Equal(404, (await Fails(() => _b.CheckoutAsync(actor, 9999))).StatusCode);
    }

    [Fact]
    public async Task Without_vnpay_keys_checkout_is_refused_before_anything_is_created()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var created = await _b.H.RunAsync(async sp =>
        {
            var service = new BillingService(sp.GetRequiredService<IAppDbContext>(), sp.GetRequiredService<RequestValidator>(), new UnconfiguredGateway(),
                sp.GetRequiredService<PlanGuard>(), sp.GetRequiredService<BillingQueries>(), sp.GetRequiredService<IAuditLogger>(), sp.GetRequiredService<TimeProvider>());
            var ex = await Assert.ThrowsAsync<AppException>(() => service.StartCheckoutAsync(actor, new CheckoutRequest(_b.BasicId), default));
            return (ex.StatusCode, ex.AppMessage.Code);
        });

        Assert.Equal((503, "PAYMENT_GATEWAY_UNAVAILABLE"), created);
        Assert.Equal(0, await _b.H.QueryAsync(db => db.Invoices.CountAsync()));
    }

    [Fact]
    public async Task Checkout_is_refused_when_the_vnpay_provider_is_switched_off()
    {
        var (_, actor) = await _b.NewOrgAsync();
        await _b.H.QueryAsync(async db =>
        {
            (await db.PaymentProviders.SingleAsync(p => p.Code == "VNPAY")).IsActive = false;
            return await db.SaveChangesAsync();
        });

        var ex = await Fails(() => _b.CheckoutAsync(actor, _b.BasicId));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(0, await _b.H.QueryAsync(db => db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.PENDING)));
    }

    // ---- Gia hạn, nâng cấp: chặn ----

    [Fact]
    public async Task A_paid_renewal_waits_for_its_start_date_and_blocks_a_second_renewal_but_not_an_upgrade()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(actor, _b.BasicId);                         // 29/09 → 28/10
        var renewal = await _b.BuyAsync(actor, _b.BasicId);           // gia hạn: 29/10 → 27/11

        var queued = await _b.SubscriptionAsync(renewal.Invoice.SubscriptionId);
        Assert.Equal((SubscriptionStatus.ACTIVE, new DateOnly(2026, 10, 29), new DateOnly(2026, 11, 27)), (queued.Status, queued.StartDate!.Value, queued.EndDate!.Value));
        var overview = await _b.OverviewAsync(actor);
        Assert.Equal((new DateOnly(2026, 9, 29), new DateOnly(2026, 10, 29), true), (overview.Current!.StartDate!.Value, overview.Upcoming!.StartDate!.Value, overview.Current.IsEffective));
        Assert.False(overview.Upcoming.IsEffective);

        var again = await Fails(() => _b.CheckoutAsync(actor, _b.BasicId));
        Assert.Equal(422, again.StatusCode);
        Assert.StartsWith("Không thể mua gói. Trường đã có gói gia hạn bắt đầu từ ngày 29/10/2026.", again.Message);
        var offers = await Queries(q => q.ListOffersAsync(actor, default));
        Assert.Equal((false, true), (offers.Single(o => o.Name == "Cơ bản").Available, offers.Single(o => o.Name == "Nâng cao").Available));
    }

    [Fact]
    public async Task A_school_with_only_a_queued_plan_must_wait_for_it_before_buying()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(actor, _b.BasicId);
        var renewal = await _b.BuyAsync(actor, _b.BasicId);
        // Gói hiện tại bị hủy (ở đây bằng cách đặt đã hủy) còn lại gói gia hạn chờ: chưa mua thêm được.
        await _b.H.QueryAsync(async db =>
        {
            var current = await db.Subscriptions.OrderBy(s => s.Id).FirstAsync(s => s.Id != renewal.Invoice.SubscriptionId);
            current.Status = SubscriptionStatus.CANCELLED;
            return await db.SaveChangesAsync();
        });

        var ex = await Fails(() => _b.CheckoutAsync(actor, _b.BasicId));

        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("gói gia hạn bắt đầu từ ngày 29/10/2026", ex.Message);
        var overview = await _b.OverviewAsync(actor);
        Assert.False(overview.HasEffectivePlan);
        Assert.False(overview.CanActivateFreePlan);
    }

    // ---- Hủy ----

    [Fact]
    public async Task Cancelling_a_purchase_request_cancels_the_invoice_fails_the_waiting_payment_and_allows_a_new_purchase()
    {
        var (org, actor) = await _b.NewOrgAsync();
        var start = await _b.CheckoutAsync(actor, _b.BasicId);

        var overview = await Billing(s => s.CancelAsync(actor, start.Invoice.SubscriptionId, default));

        Assert.Null(overview.Pending);
        Assert.True(overview.HasEffectivePlan);
        Assert.Equal(SubscriptionStatus.CANCELLED, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status);
        Assert.Equal(InvoiceStatus.CANCELLED, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
        Assert.Equal(PaymentStatus.FAILED, (await _b.PaymentAsync(start.PaymentId)).Status);
        Assert.Equal(1, await _b.AuditCountAsync(AuditAction.BILLING_STATUS_CHANGED));       // giao dịch chờ bị chuyển thất bại
        Assert.Equal(2, await _b.AuditCountAsync(AuditAction.SUBSCRIPTION_CHANGED));         // tạo yêu cầu, hủy yêu cầu
        var again = await _b.CheckoutAsync(actor, _b.ProId);
        Assert.Equal(PurchaseKind.UPGRADE, again.Kind);
        Assert.Equal(2, await _b.H.QueryAsync(db => db.Subscriptions.CountAsync(s => s.OrganizationId == org.Id && s.Status != SubscriptionStatus.ACTIVE)));
    }

    [Fact]
    public async Task Cancelling_a_paid_plan_ends_it_now_without_refund()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var bought = await _b.BuyAsync(actor, _b.BasicId);

        var overview = await Billing(s => s.CancelAsync(actor, bought.Invoice.SubscriptionId, default));

        Assert.False(overview.HasEffectivePlan);
        Assert.Equal((SubscriptionStatus.CANCELLED, true), (overview.Current!.Status, overview.CanActivateFreePlan));
        Assert.Equal(InvoiceStatus.PAID, (await _b.InvoiceAsync(bought.Invoice.Id)).Status);             // hóa đơn đã trả giữ nguyên
        Assert.Equal(PaymentStatus.SUCCEEDED, (await _b.PaymentAsync(bought.PaymentId)).Status);         // không dùng REFUNDED
        var log = await _b.H.QueryAsync(db => db.AuditLogs.OrderByDescending(l => l.Id).FirstAsync(l => l.Action == AuditAction.SUBSCRIPTION_CHANGED));
        Assert.Contains("plan cancelled", log.NewValue);
        Assert.Equal(actor.UserId, log.UserId);
    }

    [Fact]
    public async Task The_free_plan_and_a_paid_renewal_cannot_be_cancelled_and_neither_can_the_plan_that_precedes_a_renewal()
    {
        var (org, freeActor) = await _b.NewOrgAsync();
        var freeSubscription = (await _b.SubscriptionsAsync(org.Id)).Single();
        var freeEx = await Fails(() => Billing(s => s.CancelAsync(freeActor, freeSubscription.Id, default)));
        Assert.Equal("Không thể hủy gói. Gói Miễn phí không hủy được.", freeEx.Message);

        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var current = await _b.BuyAsync(actor, _b.BasicId);
        var renewal = await _b.BuyAsync(actor, _b.BasicId);

        var queued = await Fails(() => Billing(s => s.CancelAsync(actor, renewal.Invoice.SubscriptionId, default)));
        Assert.Equal("Không thể hủy gói. Gói gia hạn Cơ bản đã thanh toán nên không hủy được.", queued.Message);
        var first = await Fails(() => Billing(s => s.CancelAsync(actor, current.Invoice.SubscriptionId, default)));
        Assert.StartsWith("Không thể hủy gói. Trường đã thanh toán gói gia hạn bắt đầu từ ngày 29/10/2026", first.Message);
        Assert.Equal((SubscriptionStatus.ACTIVE, SubscriptionStatus.ACTIVE),
            ((await _b.SubscriptionAsync(current.Invoice.SubscriptionId)).Status, (await _b.SubscriptionAsync(renewal.Invoice.SubscriptionId)).Status));
    }

    [Fact]
    public async Task Only_pending_requests_and_effective_paid_plans_can_be_cancelled_and_only_in_the_own_school()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var (_, other) = await _b.NewOrgAsync(withFreePlan: false);
        var bought = await _b.BuyAsync(actor, _b.BasicId);
        await Billing(s => s.CancelAsync(actor, bought.Invoice.SubscriptionId, default));

        var twice = await Fails(() => Billing(s => s.CancelAsync(actor, bought.Invoice.SubscriptionId, default)));
        Assert.StartsWith("Không thể hủy gói. Chỉ hủy được gói trả phí đang hiệu lực", twice.Message);

        var foreign = await Fails(() => Billing(s => s.CancelAsync(other, bought.Invoice.SubscriptionId, default)));
        Assert.Equal(404, foreign.StatusCode);
        Assert.Equal(404, (await Fails(() => Billing(s => s.CancelAsync(actor, 99999, default)))).StatusCode);
    }

    // ---- Gói Miễn phí ----

    [Fact]
    public async Task The_free_plan_is_activated_at_once_without_an_invoice_only_when_the_school_has_no_plan()
    {
        var (org, actor) = await _b.NewOrgAsync(withFreePlan: false);

        var overview = await Billing(s => s.ActivateFreePlanAsync(actor, default));

        Assert.True(overview.HasEffectivePlan);
        Assert.Equal(("Miễn phí", true, Day, Day.AddDays(29)), (overview.Current!.PlanName, overview.Current.IsFree, overview.Current.StartDate!.Value, overview.Current.EndDate!.Value));
        Assert.Equal(0, await _b.H.QueryAsync(db => db.Invoices.CountAsync()));
        Assert.Equal(0, await _b.H.QueryAsync(db => db.Payments.CountAsync()));
        Assert.Equal(1, await _b.AuditCountAsync(AuditAction.SUBSCRIPTION_CHANGED));
        Assert.False(overview.CanActivateFreePlan);

        var again = await Fails(() => Billing(s => s.ActivateFreePlanAsync(actor, default)));
        Assert.Equal((422, "Không thể kích hoạt gói Miễn phí. Trường đang có gói còn hiệu lực hoặc đã có gói gia hạn chờ bắt đầu."), (again.StatusCode, again.Message));
        Assert.Single(await _b.SubscriptionsAsync(org.Id));
    }

    [Fact]
    public async Task The_free_plan_can_be_activated_again_after_the_previous_plan_expired_and_a_pending_purchase_does_not_block_it()
    {
        var (org, actor) = await _b.NewOrgAsync();
        _b.H.Clock.Advance(TimeSpan.FromDays(31));                                // gói Miễn phí 30 ngày đã qua
        Assert.False((await _b.OverviewAsync(actor)).HasEffectivePlan);
        await _b.CheckoutAsync(actor, _b.BasicId);                                // đang có yêu cầu mua chưa trả

        var overview = await Billing(s => s.ActivateFreePlanAsync(actor, default));

        Assert.True(overview.HasEffectivePlan);
        Assert.NotNull(overview.Pending);
        Assert.Equal(2, (await _b.SubscriptionsAsync(org.Id)).Count(s => s.Status is SubscriptionStatus.ACTIVE));
    }

    // ---- Thanh toán lại ----

    [Fact]
    public async Task Paying_again_replaces_a_waiting_payment_with_a_new_one_so_the_invoice_has_one_pending_payment()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var first = await _b.CheckoutAsync(actor, _b.BasicId);
        _b.H.Clock.Advance(TimeSpan.FromMinutes(3));

        var second = await _b.PayAsync(actor, first.Invoice.Id);

        Assert.NotEqual(first.PaymentId, second.PaymentId);
        Assert.Equal(first.Invoice.Id, second.Invoice.Id);
        Assert.Equal((PaymentStatus.FAILED, null), ((await _b.PaymentAsync(first.PaymentId)).Status, (await _b.PaymentAsync(first.PaymentId)).RawResponse));
        Assert.Equal(PaymentStatus.PENDING, (await _b.PaymentAsync(second.PaymentId)).Status);
        Assert.Equal(1, await _b.H.QueryAsync(db => db.Payments.CountAsync(p => p.InvoiceId == first.Invoice.Id && p.Status == PaymentStatus.PENDING)));
        Assert.Equal(second.PaymentId.ToString(), VnPayTestKit.ParseUrl(second.PaymentUrl)["vnp_TxnRef"]);
        Assert.Null(second.Kind);
    }

    [Fact]
    public async Task An_invoice_can_be_paid_again_after_a_failed_attempt_until_it_is_due()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var first = await _b.CheckoutAsync(actor, _b.BasicId);
        Assert.Equal("00", (await _b.IpnAsync(first.PaymentId, 500_000m, responseCode: "24", transactionStatus: "02", transactionNo: "0")).RspCode);

        _b.H.Clock.Advance(TimeSpan.FromHours(23));
        var retry = await _b.PayAsync(actor, first.Invoice.Id);
        Assert.Equal("00", (await _b.IpnAsync(retry.PaymentId, 500_000m)).RspCode);

        Assert.Equal(InvoiceStatus.PAID, (await _b.InvoiceAsync(first.Invoice.Id)).Status);
        Assert.Equal(SubscriptionStatus.ACTIVE, (await _b.SubscriptionAsync(first.Invoice.SubscriptionId)).Status);
    }

    [Fact]
    public async Task An_overdue_paid_cancelled_or_foreign_invoice_cannot_be_paid()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var (_, other) = await _b.NewOrgAsync();
        var overdue = await _b.CheckoutAsync(actor, _b.BasicId);

        Assert.Equal(404, (await Fails(() => _b.PayAsync(other, overdue.Invoice.Id))).StatusCode);

        _b.H.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        var late = await Fails(() => _b.PayAsync(actor, overdue.Invoice.Id));
        Assert.Equal(422, late.StatusCode);
        Assert.StartsWith("Không thể thanh toán hóa đơn. Hóa đơn đã quá hạn thanh toán lúc 08:00 ngày 30/09/2026.", late.Message);

        var (_, payer) = await _b.NewOrgAsync(withFreePlan: false);
        var paid = await _b.BuyAsync(payer, _b.BasicId);
        Assert.Equal("Không thể thanh toán hóa đơn. Hóa đơn đã được thanh toán.", (await Fails(() => _b.PayAsync(payer, paid.Invoice.Id))).Message);

        await Billing(s => s.CancelAsync(actor, overdue.Invoice.SubscriptionId, default));
        Assert.Equal("Không thể thanh toán hóa đơn. Hóa đơn đã bị hủy.", (await Fails(() => _b.PayAsync(actor, overdue.Invoice.Id))).Message);
        Assert.Equal(404, (await Fails(() => _b.PayAsync(actor, 99999))).StatusCode);
    }

    // ---- Đọc ----

    [Fact]
    public async Task Overview_shows_the_current_plan_the_pending_purchase_and_which_buttons_apply()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var overview = await _b.OverviewAsync(actor);
        Assert.Equal(("Miễn phí", true, false, false, 30), (overview.Current!.PlanName, overview.HasEffectivePlan, overview.CanActivateFreePlan, overview.CanCancelCurrent, overview.Current.DaysLeft!.Value));
        Assert.Null(overview.CancelConfirmation);
        Assert.Equal((0, 2), (overview.TeacherQuota.Used, overview.TeacherQuota.Max!.Value));
        Assert.Equal((0, 20), (overview.AiQuota.Used, overview.AiQuota.Max!.Value));

        var start = await _b.CheckoutAsync(actor, _b.ProId);
        var pending = (await _b.OverviewAsync(actor)).Pending!;
        Assert.Equal((start.Invoice.SubscriptionId, "Nâng cao", "HD20260929-000001", 1_990_000m, true),
            (pending.SubscriptionId, pending.PlanName, pending.InvoiceNumber, pending.Amount!.Value, pending.HasWaitingPayment));

        _b.H.Clock.Advance(TimeSpan.FromMinutes(16));                           // quá thời hạn 15 phút của VNPay
        Assert.False((await _b.OverviewAsync(actor)).Pending!.HasWaitingPayment);
    }

    [Fact]
    public async Task Overview_of_a_paid_plan_offers_cancel_with_the_catalog_confirmation()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(actor, _b.BasicId);

        var overview = await _b.OverviewAsync(actor);

        Assert.True(overview.CanCancelCurrent);
        Assert.StartsWith("Bạn có chắc muốn hủy gói Cơ bản? Gói sẽ kết thúc ngay, không được hoàn tiền;", overview.CancelConfirmation);
        Assert.EndsWith("Học sinh vẫn học và làm bài kiểm tra đã phát hành bình thường.", overview.CancelConfirmation);
        Assert.Equal((100, 5), (overview.Current!.MaxAiRequests!.Value, overview.Current.MaxTeachers!.Value));
    }

    [Fact]
    public async Task Invoices_are_listed_newest_first_with_filters_and_only_for_the_own_school()
    {
        var (org, actor) = await _b.NewOrgAsync();
        var (_, other) = await _b.NewOrgAsync();
        await _b.CheckoutAsync(other, _b.BasicId);
        var first = await _b.CheckoutAsync(actor, _b.BasicId);
        await Billing(s => s.CancelAsync(actor, first.Invoice.SubscriptionId, default));
        _b.H.Clock.Advance(TimeSpan.FromHours(1));
        var second = await _b.CheckoutAsync(actor, _b.ProId);

        var all = await Queries(q => q.ListInvoicesAsync(org.Id, new InvoiceListQuery(null, null, null, null, null, null), default));
        Assert.Equal([second.Invoice.Id, first.Invoice.Id], all.Items.Select(i => i.Id));
        Assert.All(all.Items, i => Assert.Equal(org.Id, i.OrganizationId));

        var unpaid = await Queries(q => q.ListInvoicesAsync(org.Id, new InvoiceListQuery(InvoiceStatus.UNPAID, null, null, null, 1, 10), default));
        Assert.Equal([second.Invoice.Id], unpaid.Items.Select(i => i.Id));

        var window = await Queries(q => q.ListInvoicesAsync(org.Id,
            new InvoiceListQuery(null, null, new DateTimeOffset(_b.H.Clock.GetUtcNow().UtcDateTime.AddMinutes(-30), TimeSpan.Zero), null, null, null), default));
        Assert.Equal([second.Invoice.Id], window.Items.Select(i => i.Id));

        var detail = await Queries(q => q.GetInvoiceAsync(org.Id, first.Invoice.Id, default));
        Assert.Equal((InvoiceStatus.CANCELLED, 1, PaymentStatus.FAILED, "VNPAY"),
            (detail.Invoice.Status, detail.Payments.Count, detail.Payments.Single().Status!.Value, detail.Payments.Single().ProviderCode));
        var foreignId = await _b.H.QueryAsync(db => db.Invoices.Where(i => i.OrganizationId != org.Id).Select(i => i.Id).FirstAsync());
        Assert.Equal(404, (await Fails(() => Queries(q => q.GetInvoiceAsync(org.Id, foreignId, default)))).StatusCode);
    }

    [Fact]
    public async Task The_system_administrator_sees_every_invoice_and_can_filter_by_school()
    {
        var (org, a) = await _b.NewOrgAsync();
        var (_, b) = await _b.NewOrgAsync();
        await _b.CheckoutAsync(a, _b.BasicId);
        await _b.CheckoutAsync(b, _b.BasicId);

        var every = await Queries(q => q.ListInvoicesAsync(null, new InvoiceListQuery(null, null, null, null, null, null), default));
        var one = await Queries(q => q.ListInvoicesAsync(null, new InvoiceListQuery(null, org.Id, null, null, null, null), default));

        Assert.Equal(2, every.TotalItems);
        Assert.Equal([org.Id], one.Items.Select(i => i.OrganizationId));
        Assert.Equal(org.Name, one.Items.Single().OrganizationName);
    }

    /// <summary>Cổng VNPay chưa có khóa: mọi thao tác tạo địa chỉ thanh toán bị chặn.</summary>
    private sealed class UnconfiguredGateway : IVnPayGateway
    {
        public bool IsConfigured => false;
        public string CreatePaymentUrl(VnPayPaymentRequest request) => throw new InvalidOperationException();
        public VnPayCallback Parse(IReadOnlyDictionary<string, string> query) => throw new InvalidOperationException();
        public string BuildResultPageUrl(long? paymentId, bool signatureValid) => throw new InvalidOperationException();
    }
}
