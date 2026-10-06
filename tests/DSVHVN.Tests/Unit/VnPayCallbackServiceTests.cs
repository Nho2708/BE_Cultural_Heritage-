using System.Text.Json;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Payments;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Xử lý IPN của VNPay: chỉ IPN đổi trạng thái; kiểm chữ ký, tìm giao dịch, so số tiền; thành công thì hóa đơn trả, gói kích hoạt theo loại mua;
/// IPN lặp không đổi gì; IPN sai bị từ chối và ghi nhật ký; IPN thành công tới muộn vẫn được ghi nhận.
/// </summary>
public sealed class VnPayCallbackServiceTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 29);
    private readonly BillingHarness _b = new();

    public void Dispose() => _b.Dispose();

    /// <summary>Ảnh chụp mọi thứ IPN có thể đổi, để so sánh trước và sau một IPN không được phép đổi gì.</summary>
    private async Task<string> StateAsync() => await _b.H.QueryAsync(async db => JsonSerializer.Serialize(new
    {
        subscriptions = await db.Subscriptions.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.Status, s.StartDate, s.EndDate }).ToListAsync(),
        invoices = await db.Invoices.AsNoTracking().OrderBy(i => i.Id).Select(i => new { i.Id, i.Status }).ToListAsync(),
        payments = await db.Payments.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Status, p.ProviderTransactionId, p.RawResponse, p.PaidAt }).ToListAsync(),
        auditLogs = await db.AuditLogs.CountAsync(l => l.Action != AuditAction.IPN_REJECTED),
    }));

    private Task<IpnResponse> Ipn(IReadOnlyDictionary<string, string> query) =>
        _b.H.Service<VnPayCallbackService, IpnResponse>(s => s.HandleIpnAsync(query, "203.0.113.50", default));

    // ---- Thành công: kích hoạt gói ----

    [Fact]
    public async Task A_successful_ipn_records_the_payment_pays_the_invoice_and_activates_the_plan_from_the_payment_day()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);

        var response = await _b.IpnAsync(start.PaymentId, 500_000m, transactionNo: "14012345");

        Assert.Equal(new IpnResponse("00", "Confirm Success"), response);
        var payment = await _b.PaymentAsync(start.PaymentId);
        Assert.Equal((PaymentStatus.SUCCEEDED, "14012345", _b.H.Clock.GetUtcNow().UtcDateTime), (payment.Status, payment.ProviderTransactionId, payment.PaidAt!.Value));
        Assert.Equal(InvoiceStatus.PAID, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
        var subscription = await _b.SubscriptionAsync(start.Invoice.SubscriptionId);
        Assert.Equal((SubscriptionStatus.ACTIVE, Day, Day.AddDays(29)), (subscription.Status, subscription.StartDate!.Value, subscription.EndDate!.Value));

        var overview = await _b.OverviewAsync(actor);
        Assert.Equal((true, 100, Day, Day.AddDays(29)), (overview.HasEffectivePlan, overview.AiQuota.Max!.Value, overview.AiQuota.PeriodStart!.Value, overview.AiQuota.PeriodEnd!.Value));
        Assert.Null(overview.Pending);
    }

    [Fact]
    public async Task The_raw_notification_is_stored_as_key_value_json_without_card_data()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        await _b.IpnAsync(start.PaymentId, 500_000m);

        var raw = JsonDocument.Parse((await _b.PaymentAsync(start.PaymentId)).RawResponse!).RootElement;

        Assert.Equal(("00", "00", start.PaymentId.ToString(), "50000000"),
            (raw.GetProperty("vnp_ResponseCode").GetString(), raw.GetProperty("vnp_TransactionStatus").GetString(),
             raw.GetProperty("vnp_TxnRef").GetString(), raw.GetProperty("vnp_Amount").GetString()));
        Assert.Equal(JsonValueKind.String, raw.GetProperty("vnp_SecureHash").ValueKind);
        Assert.All(raw.EnumerateObject(), p => Assert.StartsWith("vnp_", p.Name));                 // chỉ tham số của VNPay, không số thẻ hay tài khoản
    }

    [Fact]
    public async Task The_ipn_is_logged_by_the_system_not_by_a_person()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        await _b.IpnAsync(start.PaymentId, 500_000m);

        var billing = await _b.H.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.BILLING_STATUS_CHANGED));
        Assert.Equal((null, ActorRole.SYSTEM, "payments", start.PaymentId), (billing.UserId, billing.ActorRole, billing.TargetType, billing.TargetId));
        Assert.Contains("SUCCEEDED", billing.NewValue);
        Assert.Contains("PAID", billing.NewValue);
        var change = await _b.H.QueryAsync(db => db.AuditLogs.Where(l => l.Action == AuditAction.SUBSCRIPTION_CHANGED).OrderByDescending(l => l.Id).FirstAsync());
        Assert.Equal((null, ActorRole.SYSTEM, "subscriptions"), (change.UserId, change.ActorRole, change.TargetType));
        Assert.Contains("\"kind\":\"PURCHASE\"", change.NewValue);
    }

    [Fact]
    public async Task An_upgrade_takes_effect_today_and_cancels_the_current_plan_without_refund()
    {
        var (org, actor) = await _b.NewOrgAsync();                                   // gói Miễn phí
        var bought = await _b.BuyAsync(actor, _b.ProId);

        var subscriptions = await _b.SubscriptionsAsync(org.Id);
        Assert.Equal([SubscriptionStatus.CANCELLED, SubscriptionStatus.ACTIVE], subscriptions.Select(s => s.Status));
        var pro = subscriptions[1];
        Assert.Equal((Day, Day.AddDays(364)), (pro.StartDate!.Value, pro.EndDate!.Value));
        var overview = await _b.OverviewAsync(actor);
        Assert.Equal(("Nâng cao", 20, 1000), (overview.Current!.PlanName, overview.TeacherQuota.Max!.Value, overview.AiQuota.Max!.Value));
        Assert.Equal(PaymentStatus.SUCCEEDED, (await _b.PaymentAsync(bought.PaymentId)).Status);                  // không có REFUNDED
        Assert.DoesNotContain(await _b.H.QueryAsync(db => db.Payments.Select(p => p.Status).ToListAsync()), s => s == PaymentStatus.REFUNDED);
    }

    [Fact]
    public async Task A_renewal_starts_the_day_after_the_current_plan_ends()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(actor, _b.BasicId);                                        // 29/09 → 28/10
        _b.H.Clock.Advance(TimeSpan.FromDays(10));

        var renewal = await _b.BuyAsync(actor, _b.BasicId);

        var queued = await _b.SubscriptionAsync(renewal.Invoice.SubscriptionId);
        Assert.Equal((new DateOnly(2026, 10, 29), new DateOnly(2026, 11, 27)), (queued.StartDate!.Value, queued.EndDate!.Value));
    }

    [Fact]
    public async Task A_renewal_whose_ipn_arrives_after_the_current_plan_ended_starts_on_the_ipn_day()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(actor, _b.BasicId);                                        // hết hạn 28/10
        _b.H.Clock.Advance(TimeSpan.FromDays(29) - TimeSpan.FromHours(1));
        var renewal = await _b.CheckoutAsync(actor, _b.BasicId);                     // tạo ngày 28/10, vẫn là gia hạn
        Assert.Equal(PurchaseKind.RENEW, renewal.Kind!.Value);

        _b.H.Clock.Advance(TimeSpan.FromDays(3));                                    // IPN tới ngày 31/10
        await _b.IpnAsync(renewal.PaymentId, 500_000m);

        var subscription = await _b.SubscriptionAsync(renewal.Invoice.SubscriptionId);
        Assert.Equal((new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 29)), (subscription.StartDate!.Value, subscription.EndDate!.Value));
    }

    [Fact]
    public async Task An_upgrade_also_cancels_a_paid_renewal_that_was_waiting_to_start()
    {
        var (org, actor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(actor, _b.BasicId);
        await _b.BuyAsync(actor, _b.BasicId);                                        // gia hạn chờ

        await _b.BuyAsync(actor, _b.ProId);

        var states = (await _b.SubscriptionsAsync(org.Id)).Select(s => (s.Plan!.Name, s.Status)).ToList();
        Assert.Equal([("Cơ bản", SubscriptionStatus.CANCELLED), ("Cơ bản", SubscriptionStatus.CANCELLED), ("Nâng cao", SubscriptionStatus.ACTIVE)], states);
        Assert.Null((await _b.OverviewAsync(actor)).Upcoming);
    }

    [Fact]
    public async Task Buying_after_the_previous_plan_expired_starts_today()
    {
        var (_, actor) = await _b.NewOrgAsync();
        _b.H.Clock.Advance(TimeSpan.FromDays(40));                                   // gói Miễn phí đã hết hạn

        var bought = await _b.BuyAsync(actor, _b.BasicId);

        var subscription = await _b.SubscriptionAsync(bought.Invoice.SubscriptionId);
        var today = _b.Today;
        Assert.Equal((today, today.AddDays(29)), (subscription.StartDate!.Value, subscription.EndDate!.Value));
    }

    // ---- Thất bại ----

    [Fact]
    public async Task A_failed_payment_keeps_the_invoice_unpaid_and_the_request_pending()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var start = await _b.CheckoutAsync(actor, _b.BasicId);

        var response = await _b.IpnAsync(start.PaymentId, 500_000m, responseCode: "24", transactionStatus: "02", transactionNo: "0");

        Assert.Equal("00", response.RspCode);                                        // đã ghi nhận kết quả thất bại
        var payment = await _b.PaymentAsync(start.PaymentId);
        Assert.Equal((PaymentStatus.FAILED, null, null), (payment.Status, payment.ProviderTransactionId, payment.PaidAt));
        Assert.Contains("\"vnp_ResponseCode\":\"24\"", payment.RawResponse);
        Assert.Equal(InvoiceStatus.UNPAID, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
        Assert.Equal(SubscriptionStatus.PENDING, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status);
        var overview = await _b.OverviewAsync(actor);
        Assert.Equal("Miễn phí", overview.Current!.PlanName);
        Assert.NotNull(overview.Pending);
    }

    [Theory]
    [InlineData("00", "02")]     // mã phản hồi thành công nhưng giao dịch không thành công
    [InlineData("07", "00")]     // nghi ngờ gian lận
    [InlineData("00", "01")]
    public async Task Both_the_response_code_and_the_transaction_status_must_be_00_for_a_success(string responseCode, string transactionStatus)
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);

        await _b.IpnAsync(start.PaymentId, 500_000m, responseCode, transactionStatus);

        Assert.Equal(PaymentStatus.FAILED, (await _b.PaymentAsync(start.PaymentId)).Status);
        Assert.Equal(SubscriptionStatus.PENDING, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status);
    }

    [Fact]
    public async Task A_missing_transaction_status_is_not_a_success()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        var parameters = VnPayTestKit.Callback(start.PaymentId, 500_000m, sign: false);
        parameters.Remove("vnp_TransactionStatus");
        Sign(parameters);

        await Ipn(parameters);

        Assert.Equal(PaymentStatus.FAILED, (await _b.PaymentAsync(start.PaymentId)).Status);
        Assert.Equal(InvoiceStatus.UNPAID, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
    }

    // ---- Chống trùng ----

    [Fact]
    public async Task Replaying_a_successful_ipn_answers_already_confirmed_and_changes_nothing()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        Assert.Equal("00", (await _b.IpnAsync(start.PaymentId, 500_000m, transactionNo: "777")).RspCode);
        var before = await StateAsync();

        for (var i = 0; i < 5; i++)
            Assert.Equal(new IpnResponse("02", "Order already confirmed"), await _b.IpnAsync(start.PaymentId, 500_000m, transactionNo: "777"));

        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task Replaying_a_failed_ipn_or_a_success_after_a_failure_changes_nothing()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        await _b.IpnAsync(start.PaymentId, 500_000m, responseCode: "24", transactionStatus: "02");
        var before = await StateAsync();

        Assert.Equal("02", (await _b.IpnAsync(start.PaymentId, 500_000m, responseCode: "24", transactionStatus: "02")).RspCode);
        Assert.Equal("02", (await _b.IpnAsync(start.PaymentId, 500_000m)).RspCode);       // VNPay đã báo thất bại thì thành công về sau không được chấp nhận

        Assert.Equal(before, await StateAsync());
        Assert.Equal(SubscriptionStatus.PENDING, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status);
    }

    [Fact]
    public async Task The_same_gateway_transaction_id_cannot_belong_to_two_payments()
    {
        var (_, a) = await _b.NewOrgAsync(withFreePlan: false);
        var (_, b) = await _b.NewOrgAsync(withFreePlan: false);
        var first = await _b.CheckoutAsync(a, _b.BasicId);
        var second = await _b.CheckoutAsync(b, _b.BasicId);
        await _b.IpnAsync(first.PaymentId, 500_000m, transactionNo: "SAME-1");

        var response = await _b.IpnAsync(second.PaymentId, 500_000m, transactionNo: "SAME-1");

        Assert.Equal("99", response.RspCode);
        Assert.Equal((PaymentStatus.PENDING, SubscriptionStatus.PENDING),
            ((await _b.PaymentAsync(second.PaymentId)).Status, (await _b.SubscriptionAsync(second.Invoice.SubscriptionId)).Status));
        Assert.Equal(1, await _b.AuditCountAsync(AuditAction.IPN_REJECTED));
    }

    // ---- Từ chối ----

    [Theory]
    [InlineData("no-signature")]
    [InlineData("wrong-secret")]
    [InlineData("tampered-amount")]
    [InlineData("tampered-reference")]
    public async Task An_ipn_with_a_bad_signature_is_refused_without_touching_anything_and_is_logged(string kind)
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        var before = await StateAsync();
        var parameters = kind switch
        {
            "no-signature" => VnPayTestKit.Callback(start.PaymentId, 500_000m, sign: false),
            "wrong-secret" => VnPayTestKit.Callback(start.PaymentId, 500_000m, secret: "not-the-secret"),
            _ => VnPayTestKit.Callback(start.PaymentId, 500_000m),
        };
        if (kind == "tampered-amount") parameters["vnp_Amount"] = "1";
        if (kind == "tampered-reference") parameters["vnp_TxnRef"] = (start.PaymentId + 1).ToString();

        var response = await Ipn(parameters);

        Assert.Equal(new IpnResponse("97", "Invalid signature"), response);
        Assert.Equal(before, await StateAsync());
        var log = await _b.H.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.IPN_REJECTED));
        Assert.Equal((null, ActorRole.SYSTEM, "payments"), (log.UserId, log.ActorRole, log.TargetType));
        Assert.Contains("invalid signature", log.NewValue);
        Assert.Contains("203.0.113.50", log.NewValue);
        Assert.DoesNotContain(VnPayTestKit.HashSecret, log.NewValue);
    }

    [Fact]
    public async Task An_ipn_for_an_unknown_transaction_is_refused_even_with_a_valid_signature()
    {
        var response = await _b.IpnAsync(987654, 500_000m);

        Assert.Equal(new IpnResponse("01", "Order not found"), response);
        Assert.Equal(1, await _b.AuditCountAsync(AuditAction.IPN_REJECTED));
    }

    [Fact]
    public async Task An_ipn_without_a_usable_transaction_reference_is_refused()
    {
        var parameters = VnPayTestKit.Callback(1, 500_000m, sign: false);
        parameters["vnp_TxnRef"] = "abc";
        Sign(parameters);
        var withoutReference = VnPayTestKit.Callback(1, 500_000m, sign: false);
        withoutReference.Remove("vnp_TxnRef");
        Sign(withoutReference);

        Assert.Equal("01", (await Ipn(parameters)).RspCode);
        Assert.Equal("01", (await Ipn(withoutReference)).RspCode);
        Assert.Equal(2, await _b.AuditCountAsync(AuditAction.IPN_REJECTED));
    }

    [Theory]
    [InlineData(49_999_999L)]
    [InlineData(50_000_001L)]
    [InlineData(500_000L)]       // quên nhân 100
    public async Task An_ipn_with_a_different_amount_is_refused_and_the_payment_stays_pending(long rawAmount)
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        var before = await StateAsync();

        var response = await _b.IpnAsync(start.PaymentId, 500_000m, rawAmount: rawAmount);

        Assert.Equal(new IpnResponse("04", "Invalid amount"), response);
        Assert.Equal(before, await StateAsync());
        var log = await _b.H.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.IPN_REJECTED));
        Assert.Equal(start.PaymentId, log.TargetId);
        Assert.Contains("amount mismatch", log.NewValue);
    }

    [Fact]
    public async Task Ipn_details_in_the_log_are_limited_to_vnp_fields_and_truncated()
    {
        var parameters = VnPayTestKit.Callback(1, 500_000m, sign: false);
        parameters["vnp_OrderInfo"] = new string('x', 5000);
        parameters["session"] = "secret-cookie";
        Sign(parameters);
        parameters["vnp_SecureHash"] = "bad";

        await Ipn(parameters);

        var log = await _b.H.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.IPN_REJECTED));
        Assert.DoesNotContain("secret-cookie", log.NewValue);
        Assert.True(log.NewValue!.Length < 3000);
    }

    [Fact]
    public async Task Without_keys_every_ipn_is_refused()
    {
        var response = await _b.H.RunAsync(async sp =>
        {
            var service = new VnPayCallbackService(sp.GetRequiredService<IAppDbContext>(), new UnconfiguredGateway(),
                sp.GetRequiredService<SubscriptionActivator>(), sp.GetRequiredService<IAuditLogger>(), sp.GetRequiredService<TimeProvider>());
            return await service.HandleIpnAsync(VnPayTestKit.Callback(1, 500_000m), null, default);
        });

        Assert.Equal("99", response.RspCode);
        Assert.Equal(1, await _b.AuditCountAsync(AuditAction.IPN_REJECTED));
    }

    // ---- IPN thành công tới muộn ----

    [Fact]
    public async Task A_success_that_arrives_after_the_background_job_failed_the_payment_still_activates_the_plan()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        _b.H.Clock.Advance(TimeSpan.FromMinutes(31));
        await _b.H.Service<BillingMaintenanceService, int>(s => s.FailStalePaymentsAsync(default));
        Assert.Equal(PaymentStatus.FAILED, (await _b.PaymentAsync(start.PaymentId)).Status);

        var response = await _b.IpnAsync(start.PaymentId, 500_000m, transactionNo: "LATE-1");

        Assert.Equal("00", response.RspCode);
        Assert.Equal((PaymentStatus.SUCCEEDED, "LATE-1"), ((await _b.PaymentAsync(start.PaymentId)).Status, (await _b.PaymentAsync(start.PaymentId)).ProviderTransactionId));
        Assert.Equal((InvoiceStatus.PAID, SubscriptionStatus.ACTIVE),
            ((await _b.InvoiceAsync(start.Invoice.Id)).Status, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status));
        var log = await _b.H.QueryAsync(db => db.AuditLogs.Where(l => l.Action == AuditAction.BILLING_STATUS_CHANGED).OrderByDescending(l => l.Id).FirstAsync());
        Assert.Contains("\"late\":true", log.NewValue);
    }

    [Fact]
    public async Task A_late_success_revives_a_request_the_job_or_the_school_already_cancelled_because_the_money_was_taken()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var byJob = await _b.CheckoutAsync(actor, _b.BasicId);
        _b.H.Clock.Advance(TimeSpan.FromHours(25));
        await _b.H.Service<BillingMaintenanceService, BillingMaintenanceResult>(s => s.RunAsync(false, default));
        Assert.Equal((InvoiceStatus.CANCELLED, SubscriptionStatus.CANCELLED),
            ((await _b.InvoiceAsync(byJob.Invoice.Id)).Status, (await _b.SubscriptionAsync(byJob.Invoice.SubscriptionId)).Status));

        Assert.Equal("00", (await _b.IpnAsync(byJob.PaymentId, 500_000m)).RspCode);

        Assert.Equal((InvoiceStatus.PAID, SubscriptionStatus.ACTIVE),
            ((await _b.InvoiceAsync(byJob.Invoice.Id)).Status, (await _b.SubscriptionAsync(byJob.Invoice.SubscriptionId)).Status));
        Assert.Equal(_b.Today, (await _b.SubscriptionAsync(byJob.Invoice.SubscriptionId)).StartDate!.Value);

        var (_, other) = await _b.NewOrgAsync(withFreePlan: false);
        var byUser = await _b.CheckoutAsync(other, _b.BasicId);
        await _b.H.Service<BillingService, BillingOverviewDto>(s => s.CancelAsync(other, byUser.Invoice.SubscriptionId, default));
        Assert.Equal("00", (await _b.IpnAsync(byUser.PaymentId, 500_000m)).RspCode);
        Assert.Equal(SubscriptionStatus.ACTIVE, (await _b.SubscriptionAsync(byUser.Invoice.SubscriptionId)).Status);
    }

    [Fact]
    public async Task Paying_twice_activates_the_plan_once_and_keeps_both_payments_for_reconciliation()
    {
        var (org, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var first = await _b.CheckoutAsync(actor, _b.BasicId);
        var second = await _b.PayAsync(actor, first.Invoice.Id);                  // giao dịch thứ nhất bị thay bằng giao dịch thứ hai

        Assert.Equal("00", (await _b.IpnAsync(first.PaymentId, 500_000m, transactionNo: "DUP-A")).RspCode);   // người trả vẫn trả ở trang cũ
        var activated = await _b.SubscriptionAsync(first.Invoice.SubscriptionId);
        _b.H.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal("00", (await _b.IpnAsync(second.PaymentId, 500_000m, transactionNo: "DUP-B")).RspCode); // và trả luôn ở trang mới

        Assert.Equal((PaymentStatus.SUCCEEDED, PaymentStatus.SUCCEEDED),
            ((await _b.PaymentAsync(first.PaymentId)).Status, (await _b.PaymentAsync(second.PaymentId)).Status));
        var after = await _b.SubscriptionsAsync(org.Id);
        Assert.Single(after);
        Assert.Equal((activated.StartDate, activated.EndDate), (after[0].StartDate, after[0].EndDate));
        var note = await _b.H.QueryAsync(db => db.AuditLogs.Where(l => l.Action == AuditAction.BILLING_STATUS_CHANGED).OrderByDescending(l => l.Id).FirstAsync());
        Assert.Contains("no plan change", note.NewValue);
    }

    [Fact]
    public async Task A_failure_after_the_system_already_failed_the_payment_is_just_a_replay()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        _b.H.Clock.Advance(TimeSpan.FromMinutes(31));
        await _b.H.Service<BillingMaintenanceService, int>(s => s.FailStalePaymentsAsync(default));

        var response = await _b.IpnAsync(start.PaymentId, 500_000m, responseCode: "24", transactionStatus: "02");

        Assert.Equal("02", response.RspCode);
        Assert.Null((await _b.PaymentAsync(start.PaymentId)).RawResponse);
    }

    // ---- Trang trả về ----

    [Fact]
    public async Task The_return_page_only_forwards_the_browser_and_never_changes_data()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        var before = await StateAsync();
        var success = VnPayTestKit.Callback(start.PaymentId, 500_000m);

        var valid = await _b.H.Service<VnPayCallbackService, string>(s => Task.FromResult(s.ResolveReturnUrl(success)));
        var forged = VnPayTestKit.Callback(start.PaymentId, 500_000m);
        forged["vnp_Amount"] = "1";
        var invalid = await _b.H.Service<VnPayCallbackService, string>(s => Task.FromResult(s.ResolveReturnUrl(forged)));

        Assert.Equal($"http://localhost:5173/truong/ket-qua-thanh-toan?payment={start.PaymentId}", valid);
        Assert.Equal("http://localhost:5173/truong/ket-qua-thanh-toan?error=invalid", invalid);
        Assert.Equal(before, await StateAsync());
        Assert.Equal(SubscriptionStatus.PENDING, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status);
    }

    // ---- Trang kết quả ----

    [Fact]
    public async Task The_result_page_reads_the_state_the_ipn_wrote()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var start = await _b.CheckoutAsync(actor, _b.BasicId);

        var waiting = await _b.H.Service<BillingQueries, PaymentResultDto>(q => q.GetPaymentResultAsync(actor, start.PaymentId, default));
        Assert.Equal((PaymentOutcome.PROCESSING, "Hệ thống đang chờ xác nhận từ VNPay. Trang sẽ tự cập nhật khi có kết quả.", false),
            (waiting.Outcome, waiting.Message, waiting.CanPayAgain));
        Assert.Equal(SubscriptionStatus.PENDING, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status);          // đọc trang không kích hoạt gì

        await _b.IpnAsync(start.PaymentId, 500_000m, responseCode: "24", transactionStatus: "02");
        var failed = await _b.H.Service<BillingQueries, PaymentResultDto>(q => q.GetPaymentResultAsync(actor, start.PaymentId, default));
        Assert.Equal((PaymentOutcome.UNPAID, true), (failed.Outcome, failed.CanPayAgain));
        Assert.Equal("Thanh toán chưa thành công. Hóa đơn HD20260929-000001 vẫn chờ thanh toán đến 08:00 ngày 30/09/2026; bạn có thể thanh toán lại.", failed.Message);

        var retry = await _b.PayAsync(actor, start.Invoice.Id);
        await _b.IpnAsync(retry.PaymentId, 500_000m);
        var paid = await _b.H.Service<BillingQueries, PaymentResultDto>(q => q.GetPaymentResultAsync(actor, retry.PaymentId, default));
        Assert.Equal((PaymentOutcome.PAID, "Thanh toán thành công. Gói Cơ bản có hiệu lực từ ngày 29/09/2026 đến hết ngày 28/10/2026.", false),
            (paid.Outcome, paid.Message, paid.CanPayAgain));
        Assert.Equal(SubscriptionStatus.ACTIVE, paid.Subscription!.Status);
    }

    [Fact]
    public async Task The_result_page_of_an_expired_invoice_says_to_buy_again_and_other_schools_get_not_found()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var (_, other) = await _b.NewOrgAsync();
        var start = await _b.CheckoutAsync(actor, _b.BasicId);
        await _b.IpnAsync(start.PaymentId, 500_000m, responseCode: "24", transactionStatus: "02");
        _b.H.Clock.Advance(TimeSpan.FromHours(25));

        var result = await _b.H.Service<BillingQueries, PaymentResultDto>(q => q.GetPaymentResultAsync(actor, start.PaymentId, default));

        Assert.Equal((PaymentOutcome.UNPAID, false), (result.Outcome, result.CanPayAgain));
        Assert.Equal("Thanh toán chưa thành công và hóa đơn HD20260929-000001 không còn thanh toán được. Bạn có thể chọn gói và thanh toán lại.", result.Message);
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            _b.H.Service<BillingQueries, PaymentResultDto>(q => q.GetPaymentResultAsync(other, start.PaymentId, default)));
        Assert.Equal(404, ex.StatusCode);
    }

    // ---- Trợ giúp ----

    private static void Sign(Dictionary<string, string> parameters) =>
        parameters["vnp_SecureHash"] = VnPayGateway.Sign(VnPayGateway.Canonical(parameters), VnPayTestKit.HashSecret);

    private sealed class UnconfiguredGateway : IVnPayGateway
    {
        public bool IsConfigured => false;
        public string CreatePaymentUrl(VnPayPaymentRequest request) => throw new InvalidOperationException();
        public VnPayCallback Parse(IReadOnlyDictionary<string, string> query) => throw new InvalidOperationException();
        public string BuildResultPageUrl(long? paymentId, bool signatureValid) => throw new InvalidOperationException();
    }
}
