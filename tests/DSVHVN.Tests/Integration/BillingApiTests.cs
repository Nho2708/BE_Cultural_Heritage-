using System.Net;
using System.Text.RegularExpressions;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DSVHVN.Tests.Integration;

/// <summary>Gói và thanh toán qua HTTP trên Api thật: quản lý gói, mua gói qua VNPay, IPN, trang trả về, phân quyền, tách biệt giữa các trường.</summary>
public sealed class BillingApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly BillingTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    // ---- Quản lý gói (quản trị hệ thống) ----

    [Fact]
    public async Task Admin_creates_lists_updates_and_switches_sale_of_plans()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var name = "Gói trường " + BillingTestKit.Unique();

        var (created, createdBody) = await Http.PostAsync<PlanDto>("/api/v1/admin/plans",
            new { name, price = 750_000, durationDays = 90, maxTeachers = 6, maxAiRequests = 120 }, admin);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Tạo gói thành công.", createdBody.Message);
        var plan = createdBody.Data!;
        Assert.Equal((name, 750_000m, 90, 6, 120, true, false), (plan.Name, plan.Price, plan.DurationDays, plan.MaxTeachers, plan.MaxAiRequests, plan.IsActive, plan.IsFree));

        var (updated, updatedBody) = await Http.PutAsync<PlanDto>($"/api/v1/admin/plans/{plan.Id}",
            new { name, price = 800_000, durationDays = 120, maxTeachers = 7, maxAiRequests = 150 }, admin);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal((800_000m, 120), (updatedBody.Data!.Price, updatedBody.Data.DurationDays));

        var (off, offBody) = await Http.PostAsync<PlanDto>($"/api/v1/admin/plans/{plan.Id}/disable", null, admin);
        Assert.Equal((HttpStatusCode.OK, false), (off.StatusCode, offBody.Data!.IsActive));
        var (on, onBody) = await Http.PostAsync<PlanDto>($"/api/v1/admin/plans/{plan.Id}/enable", null, admin);
        Assert.Equal((HttpStatusCode.OK, true), (on.StatusCode, onBody.Data!.IsActive));

        var (list, listBody) = await Http.GetAsync<IReadOnlyList<PlanDto>>("/api/v1/admin/plans", admin);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(listBody.Data!, p => p.Id == plan.Id && p.Price == 800_000m);
        Assert.Contains(listBody.Data!, p => p.IsFree && p.Name == "Miễn phí");
        var (get, getBody) = await Http.GetAsync<PlanDto>($"/api/v1/admin/plans/{plan.Id}", admin);
        Assert.Equal((HttpStatusCode.OK, name), (get.StatusCode, getBody.Data!.Name));
    }

    [Fact]
    public async Task Plan_requests_are_validated_and_names_and_free_plan_are_protected()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);

        var (bad, badBody) = await Http.PostAsync<object>("/api/v1/admin/plans", new { name = "", price = -5, durationDays = 0 }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(["durationDays", "maxAiRequests", "maxTeachers", "name", "price"], badBody.Errors!.Keys.Order(StringComparer.Ordinal));

        var (dup, dupBody) = await Http.PostAsync<object>("/api/v1/admin/plans",
            new { name = plan.Name!.ToUpperInvariant(), price = 100_000, durationDays = 30, maxTeachers = 1, maxAiRequests = 0 }, admin);
        Assert.Equal((HttpStatusCode.Conflict, "DUPLICATE"), (dup.StatusCode, dupBody.ErrorCode));

        var freeId = (await Http.GetAsync<IReadOnlyList<PlanDto>>("/api/v1/admin/plans", admin)).Body.Data!.Single(p => p.IsFree).Id;
        var (disableFree, disableBody) = await Http.PostAsync<object>($"/api/v1/admin/plans/{freeId}/disable", null, admin);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "Không thể tắt bán gói. Gói Miễn phí không tắt được."), (disableFree.StatusCode, disableBody.Message));

        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync<object>("/api/v1/admin/plans/999999", admin)).Response.StatusCode);
    }

    [Fact]
    public async Task There_is_no_endpoint_to_delete_a_plan()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/admin/plans/{plan.Id}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);
        var res = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<PlanDto>($"/api/v1/admin/plans/{plan.Id}", admin)).Response.StatusCode);
    }

    [Fact]
    public async Task Plan_management_is_for_the_system_administrator_only()
    {
        var (_, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var (_, teacher) = await _k.Api.TeacherAsync(orgAdmin);

        foreach (var token in new[] { orgAdmin, teacher })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.GetAsync<object>("/api/v1/admin/plans", token)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.PostAsync<object>("/api/v1/admin/plans", new { name = "x" }, token)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.GetAsync<object>("/api/v1/admin/invoices", token)).Response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync<object>("/api/v1/admin/plans")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.PostAsync<object>("/api/v1/admin/plans/1/disable", null)).Response.StatusCode);
    }

    [Fact]
    public async Task Plan_changes_are_in_the_audit_log_with_a_vietnamese_label()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        await Http.PostAsync<PlanDto>($"/api/v1/admin/plans/{plan.Id}/disable", null, admin);

        var (res, body) = await Http.GetAsync<PagedResult<DSVHVN.Application.Audit.AuditLogDto>>(
            $"/api/v1/admin/audit-logs?action=PLAN_UPDATED&targetType=plans&targetId={plan.Id}", admin);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, body.Data!.TotalItems);
        Assert.All(body.Data.Items, l => Assert.Equal(("Cập nhật gói dịch vụ", ActorRole.ADMIN), (l.ActionLabel, l.ActorRole)));
    }

    // ---- Mua gói ----

    [Fact]
    public async Task A_school_buys_a_plan_pays_on_vnpay_and_the_ipn_activates_it()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin, price: 1_200_000m, days: 365, teachers: 12, ai: 400);
        var (org, orgAdmin) = await _k.Api.OrgWithAdminAsync();

        var (offers, offersBody) = await Http.GetAsync<IReadOnlyList<PlanOfferDto>>("/api/v1/org/plans", orgAdmin);
        Assert.Equal(HttpStatusCode.OK, offers.StatusCode);
        var offer = offersBody.Data!.Single(o => o.Id == plan.Id);
        Assert.Equal((PurchaseKind.UPGRADE, true), (offer.Kind, offer.Available));
        Assert.DoesNotContain(offersBody.Data!, o => o.Price == 0m);

        var (quote, quoteBody) = await Http.GetAsync<PurchaseQuoteDto>($"/api/v1/org/plans/{plan.Id}/quote", orgAdmin);
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        Assert.Equal($"Trường sẽ thanh toán 1.200.000 đồng cho gói {plan.Name} qua VNPay. " +
                     "Gói mới có hiệu lực ngay, gói hiện tại kết thúc và không được hoàn phần chưa dùng. Tiếp tục?", quoteBody.Data!.Confirmation);

        var start = await _k.CheckoutAsync(orgAdmin, plan.Id);
        Assert.Matches(new Regex($@"^HD{BillingTestKit.Today:yyyyMMdd}-\d{{6}}$"), start.Invoice.InvoiceNumber!);
        Assert.Equal((InvoiceStatus.UNPAID, 1_200_000m, SubscriptionStatus.PENDING), (start.Invoice.Status, start.Invoice.Amount!.Value, start.Invoice.SubscriptionStatus!.Value));
        var url = VnPayTestKit.ParseUrl(start.PaymentUrl);
        Assert.Equal((start.PaymentId.ToString(), "120000000", VnPayTestKit.TmnCode, "203.0.113.10"), (url["vnp_TxnRef"], url["vnp_Amount"], url["vnp_TmnCode"], url["vnp_IpAddr"]));

        // Trước IPN: gói cũ còn nguyên, yêu cầu mua đang chờ; trang kết quả chỉ đọc.
        var before = await _k.OverviewAsync(orgAdmin);
        Assert.Equal(("Miễn phí", plan.Name), (before.Current!.PlanName, before.Pending!.PlanName));
        var (waiting, waitingBody) = await Http.GetAsync<PaymentResultDto>($"/api/v1/org/payments/{start.PaymentId}", orgAdmin);
        Assert.Equal((HttpStatusCode.OK, PaymentOutcome.PROCESSING), (waiting.StatusCode, waitingBody.Data!.Outcome));
        Assert.NotNull((await _k.OverviewAsync(orgAdmin)).Pending);                   // đọc trang kết quả không kích hoạt gói

        var (ipn, code, message) = await _k.IpnAsync(VnPayTestKit.Callback(start.PaymentId, 1_200_000m, transactionNo: "14999001"));
        Assert.Equal((HttpStatusCode.OK, "00", "Confirm Success"), (ipn.StatusCode, code, message));
        Assert.Equal("application/json", ipn.Content.Headers.ContentType!.MediaType);

        var after = await _k.OverviewAsync(orgAdmin);
        var today = BillingTestKit.Today;
        Assert.Equal((plan.Name, true, today, today.AddDays(364), 12, 400, true),
            (after.Current!.PlanName, after.HasEffectivePlan, after.Current.StartDate!.Value, after.Current.EndDate!.Value, after.TeacherQuota.Max!.Value, after.AiQuota.Max!.Value, after.Pending is null));
        var (paid, paidBody) = await Http.GetAsync<PaymentResultDto>($"/api/v1/org/payments/{start.PaymentId}", orgAdmin);
        Assert.Equal(PaymentOutcome.PAID, paidBody.Data!.Outcome);
        Assert.Equal($"Thanh toán thành công. Gói {plan.Name} có hiệu lực từ ngày {today:dd/MM/yyyy} đến hết ngày {today.AddDays(364):dd/MM/yyyy}.", paidBody.Data.Message);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        var (invoices, invoicesBody) = await Http.GetAsync<PagedResult<InvoiceDto>>("/api/v1/org/invoices", orgAdmin);
        Assert.Equal(HttpStatusCode.OK, invoices.StatusCode);
        var row = Assert.Single(invoicesBody.Data!.Items);
        Assert.Equal((start.Invoice.Id, InvoiceStatus.PAID, false, org.Id), (row.Id, row.Status, row.CanPay, row.OrganizationId));
        var (detail, detailBody) = await Http.GetAsync<InvoiceDetailDto>($"/api/v1/org/invoices/{start.Invoice.Id}", orgAdmin);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var payment = Assert.Single(detailBody.Data!.Payments);
        Assert.Equal((PaymentStatus.SUCCEEDED, "14999001", "VNPAY"), (payment.Status, payment.ProviderTransactionId, payment.ProviderCode));
    }

    [Fact]
    public async Task A_replayed_ipn_answers_already_confirmed_and_a_bad_one_is_refused_without_a_login()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (_, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var start = await _k.CheckoutAsync(orgAdmin, plan.Id);
        var ok = VnPayTestKit.Callback(start.PaymentId, 500_000m, transactionNo: "14999002");

        Assert.Equal("00", (await _k.IpnAsync(ok)).Code);
        for (var i = 0; i < 3; i++)
        {
            var replay = await _k.IpnAsync(ok);
            Assert.Equal(("02", "Order already confirmed"), (replay.Code, replay.Message));
        }

        var forged = VnPayTestKit.Callback(start.PaymentId, 500_000m);
        forged["vnp_Amount"] = "1";
        var refused = await _k.IpnAsync(forged);
        Assert.Equal((HttpStatusCode.OK, "97", "Invalid signature"), (refused.Response.StatusCode, refused.Code, refused.Message));
        Assert.Equal("01", (await _k.IpnAsync(VnPayTestKit.Callback(987_654_321, 500_000m))).Code);
        Assert.Equal("04", (await _k.IpnAsync(VnPayTestKit.Callback(start.PaymentId, 500_000m, rawAmount: 1))).Code);

        var payments = await factory.WithDbAsync(db => db.Payments.CountAsync(p => p.InvoiceId == start.Invoice.Id && p.Status == PaymentStatus.SUCCEEDED));
        Assert.Equal(1, payments);
        var rejected = await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.IPN_REJECTED && l.TargetId == start.PaymentId));
        Assert.Equal(2, rejected);                      // chữ ký sai và số tiền lệch; mã giao dịch lạ ghi theo mã đó
    }

    [Fact]
    public async Task A_failed_payment_leaves_the_invoice_payable_again_and_the_result_page_says_so()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (_, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var first = await _k.CheckoutAsync(orgAdmin, plan.Id);

        Assert.Equal("00", await _k.PayAsync(first, responseCode: "24", transactionStatus: "02"));

        var (res, body) = await Http.GetAsync<PaymentResultDto>($"/api/v1/org/payments/{first.PaymentId}", orgAdmin);
        Assert.Equal((HttpStatusCode.OK, PaymentOutcome.UNPAID, true), (res.StatusCode, body.Data!.Outcome, body.Data.CanPayAgain));
        Assert.StartsWith($"Thanh toán chưa thành công. Hóa đơn {first.Invoice.InvoiceNumber} vẫn chờ thanh toán đến", body.Data.Message);

        var (pay, payBody) = await Http.PostAsync<PaymentStartDto>($"/api/v1/org/invoices/{first.Invoice.Id}/pay", null, orgAdmin);
        Assert.Equal(HttpStatusCode.OK, pay.StatusCode);
        Assert.NotEqual(first.PaymentId, payBody.Data!.PaymentId);
        Assert.Equal("00", await _k.PayAsync(payBody.Data));
        Assert.Equal(InvoiceStatus.PAID, (await Http.GetAsync<InvoiceDetailDto>($"/api/v1/org/invoices/{first.Invoice.Id}", orgAdmin)).Body.Data!.Invoice.Status);

        var (again, againBody) = await Http.PostAsync<object>($"/api/v1/org/invoices/{first.Invoice.Id}/pay", null, orgAdmin);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "Không thể thanh toán hóa đơn. Hóa đơn đã được thanh toán."), (again.StatusCode, againBody.Message));
    }

    [Fact]
    public async Task A_second_purchase_while_one_is_unpaid_is_refused_and_cancelling_it_frees_the_school()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (_, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var start = await _k.CheckoutAsync(orgAdmin, plan.Id);

        var (second, secondBody) = await Http.PostAsync<object>("/api/v1/org/billing/checkout", new { planId = plan.Id }, orgAdmin);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "PENDING_PURCHASE_EXISTS"), (second.StatusCode, secondBody.ErrorCode));

        var (cancel, cancelBody) = await Http.PostAsync<BillingOverviewDto>($"/api/v1/org/subscriptions/{start.Invoice.SubscriptionId}/cancel", null, orgAdmin);
        Assert.Equal((HttpStatusCode.OK, "Hủy thành công."), (cancel.StatusCode, cancelBody.Message));
        Assert.Null(cancelBody.Data!.Pending);
        await _k.CheckoutAsync(orgAdmin, plan.Id);
    }

    [Fact]
    public async Task Cancelling_a_paid_plan_and_activating_the_free_plan_work_over_http()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (org, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        await _k.RemovePlansAsync(org.Id);                                    // trường chưa có gói
        Assert.True((await _k.OverviewAsync(orgAdmin)).CanActivateFreePlan);
        var start = await _k.CheckoutAsync(orgAdmin, plan.Id);
        Assert.Equal(PurchaseKind.PURCHASE, start.Kind);
        await _k.PayAsync(start);

        var (cancel, cancelBody) = await Http.PostAsync<BillingOverviewDto>($"/api/v1/org/subscriptions/{start.Invoice.SubscriptionId}/cancel", null, orgAdmin);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal((false, true), (cancelBody.Data!.HasEffectivePlan, cancelBody.Data.CanActivateFreePlan));

        var (free, freeBody) = await Http.PostAsync<BillingOverviewDto>("/api/v1/org/billing/free-plan", null, orgAdmin);
        Assert.Equal((HttpStatusCode.OK, "Kích hoạt gói Miễn phí thành công.", "Miễn phí"), (free.StatusCode, freeBody.Message, freeBody.Data!.Current!.PlanName));
        var (again, againBody) = await Http.PostAsync<object>("/api/v1/org/billing/free-plan", null, orgAdmin);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (again.StatusCode, againBody.ErrorCode));
    }

    [Fact]
    public async Task Checkout_input_is_validated_and_unknown_or_unsellable_plans_are_refused()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        await Http.PostAsync<PlanDto>($"/api/v1/admin/plans/{plan.Id}/disable", null, admin);
        var (_, orgAdmin) = await _k.Api.OrgWithAdminAsync();

        var (empty, emptyBody) = await Http.PostAsync<object>("/api/v1/org/billing/checkout", new { }, orgAdmin);
        Assert.Equal((HttpStatusCode.BadRequest, "Vui lòng nhập gói cần mua."), (empty.StatusCode, emptyBody.Errors!["planId"]));
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>("/api/v1/org/billing/checkout", new { planId = 999999 }, orgAdmin)).Response.StatusCode);
        var (off, offBody) = await Http.PostAsync<object>("/api/v1/org/billing/checkout", new { planId = plan.Id }, orgAdmin);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "PLAN_NOT_ON_SALE"), (off.StatusCode, offBody.ErrorCode));
        var (badJson, _) = await Http.PostAsync<object>("/api/v1/org/billing/checkout", new { planId = "abc" }, orgAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
    }

    // ---- Phân quyền và tách biệt giữa các trường ----

    [Fact]
    public async Task Only_the_school_administrator_uses_the_billing_endpoints()
    {
        var (_, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var (_, teacher) = await _k.Api.TeacherAsync(orgAdmin);
        var admin = await _k.Api.AdminTokenAsync();
        string[] gets = ["/api/v1/org/billing", "/api/v1/org/plans", "/api/v1/org/plans/1/quote", "/api/v1/org/invoices", "/api/v1/org/invoices/1", "/api/v1/org/payments/1"];
        string[] posts = ["/api/v1/org/billing/checkout", "/api/v1/org/billing/free-plan", "/api/v1/org/subscriptions/1/cancel", "/api/v1/org/invoices/1/pay"];

        foreach (var token in new[] { teacher, admin })
        {
            foreach (var url in gets) Assert.Equal(HttpStatusCode.Forbidden, (await Http.GetAsync<object>(url, token)).Response.StatusCode);
            foreach (var url in posts) Assert.Equal(HttpStatusCode.Forbidden, (await Http.PostAsync<object>(url, new { planId = 1 }, token)).Response.StatusCode);
        }
        foreach (var url in gets) Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync<object>(url)).Response.StatusCode);
        foreach (var url in posts) Assert.Equal(HttpStatusCode.Unauthorized, (await Http.PostAsync<object>(url, new { planId = 1 })).Response.StatusCode);
    }

    [Fact]
    public async Task A_school_cannot_see_or_touch_another_schools_invoices_payments_or_subscriptions()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (_, ownerToken) = await _k.Api.OrgWithAdminAsync();
        var (_, intruder) = await _k.Api.OrgWithAdminAsync();
        var start = await _k.CheckoutAsync(ownerToken, plan.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync<object>($"/api/v1/org/invoices/{start.Invoice.Id}", intruder)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync<object>($"/api/v1/org/payments/{start.PaymentId}", intruder)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>($"/api/v1/org/invoices/{start.Invoice.Id}/pay", null, intruder)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>($"/api/v1/org/subscriptions/{start.Invoice.SubscriptionId}/cancel", null, intruder)).Response.StatusCode);
        var (list, listBody) = await Http.GetAsync<PagedResult<InvoiceDto>>("/api/v1/org/invoices", intruder);
        Assert.Equal((HttpStatusCode.OK, 0), (list.StatusCode, listBody.Data!.TotalItems));
        Assert.Null((await _k.OverviewAsync(intruder)).Pending);
        Assert.Equal(SubscriptionStatus.PENDING, (await factory.WithDbAsync(db => db.Subscriptions.SingleAsync(s => s.Id == start.Invoice.SubscriptionId))).Status);
    }

    [Fact]
    public async Task The_system_administrator_reads_every_invoice_with_its_payments()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (org, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var start = await _k.CheckoutAsync(orgAdmin, plan.Id);
        await _k.PayAsync(start);

        var (list, listBody) = await Http.GetAsync<PagedResult<InvoiceDto>>($"/api/v1/admin/invoices?organizationId={org.Id}&status=PAID", admin);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var row = Assert.Single(listBody.Data!.Items);
        Assert.Equal((start.Invoice.Id, org.Name), (row.Id, row.OrganizationName));

        var (detail, detailBody) = await Http.GetAsync<InvoiceDetailDto>($"/api/v1/admin/invoices/{start.Invoice.Id}", admin);
        Assert.Equal((HttpStatusCode.OK, PaymentStatus.SUCCEEDED), (detail.StatusCode, detailBody.Data!.Payments.Single().Status!.Value));
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync<object>("/api/v1/admin/invoices/999999999", admin)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync<object>("/api/v1/admin/invoices?pageSize=0", admin)).Response.StatusCode);
    }

    // ---- Trang trả về ----

    [Fact]
    public async Task The_return_page_checks_the_signature_and_forwards_the_browser_to_the_result_page_without_changing_data()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (_, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var start = await _k.CheckoutAsync(orgAdmin, plan.Id);

        var good = await _k.NoRedirect.GetAsync(VnPayTestKit.ReturnPath(VnPayTestKit.Callback(start.PaymentId, 500_000m)));
        Assert.Equal(HttpStatusCode.Redirect, good.StatusCode);
        Assert.Equal($"http://localhost:5173/truong/ket-qua-thanh-toan?payment={start.PaymentId}", good.Headers.Location!.ToString());

        var forged = VnPayTestKit.Callback(start.PaymentId, 500_000m);
        forged["vnp_ResponseCode"] = "00";
        forged["vnp_Amount"] = "1";
        var bad = await _k.NoRedirect.GetAsync(VnPayTestKit.ReturnPath(forged));
        Assert.Equal((HttpStatusCode.Redirect, "http://localhost:5173/truong/ket-qua-thanh-toan?error=invalid"), (bad.StatusCode, bad.Headers.Location!.ToString()));

        // Một lần trả thành công trên trình duyệt không kích hoạt gói: chỉ IPN mới làm được.
        var overview = await _k.OverviewAsync(orgAdmin);
        Assert.Equal(("Miễn phí", plan.Name), (overview.Current!.PlanName, overview.Pending!.PlanName));
    }

    // ---- Chưa có khóa VNPay ----

    [Fact]
    public async Task Without_vnpay_keys_checkout_answers_503_and_ipn_is_refused()
    {
        using var unconfigured = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VnPay:TmnCode"] = "",
            ["VnPay:HashSecret"] = "",
        })));
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (org, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var http = unconfigured.CreateClient();

        var (res, body) = await http.PostAsync<object>("/api/v1/org/billing/checkout", new { planId = plan.Id }, orgAdmin);
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "PAYMENT_GATEWAY_UNAVAILABLE"), (res.StatusCode, body.ErrorCode));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Invoices.CountAsync(i => i.OrganizationId == org.Id)));

        var ipn = await http.GetAsync(VnPayTestKit.IpnPath(VnPayTestKit.Callback(1, 500_000m)));
        Assert.Contains("\"RspCode\":\"99\"", await ipn.Content.ReadAsStringAsync());
    }
}
