using System.Net;
using DSVHVN.Application.Billing;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Gói và thanh toán trên SQL Server thật (database tạm của LocalDB, lược đồ do migration tạo): ràng buộc của chính CSDL, dữ liệu khởi tạo cổng thanh toán,
/// luồng mua gói tới IPN, và các tình huống đồng thời mà SQLite không kiểm được: IPN lặp cùng lúc, mua cùng lúc của một trường, mua cùng lúc của nhiều trường.
/// </summary>
public sealed class BillingSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly BillingTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    private Task<List<T>> SqlAsync<T>(FormattableString sql) =>
        factory.WithDbAsync(db => db.Database.SqlQuery<T>(sql).ToListAsync());

    [LocalDbFact]
    public async Task Providers_are_seeded_with_only_vnpay_switched_on_and_the_free_plan_exists()
    {
        _ = factory.Server;

        var providers = await factory.WithDbAsync(db => db.PaymentProviders.AsNoTracking().OrderBy(p => p.Code).ToListAsync());

        Assert.Equal([("BANK_TRANSFER", false), ("MOMO", false), ("VNPAY", true), ("ZALOPAY", false)], providers.Select(p => (p.Code!, p.IsActive)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.Plans.CountAsync(p => p.Price == 0m)));
    }

    [LocalDbFact]
    public async Task The_database_itself_refuses_a_second_pending_request_a_second_pending_payment_and_a_duplicate_plan_name()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var tag = BillingTestKit.Unique();
        var plan = await _k.CreatePlanAsync(admin, name: "Gói thử Cơ Bản " + tag);
        var (org, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var start = await _k.CheckoutAsync(orgAdmin, plan.Id);

        // Hai yêu cầu mua chưa trả của cùng một trường.
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.Subscriptions.Add(new Subscription { OrganizationId = org.Id, PlanId = plan.Id, Status = SubscriptionStatus.PENDING });
            return await db.SaveChangesAsync();
        }));

        // Hai giao dịch chờ của cùng một hóa đơn.
        var provider = await factory.WithDbAsync(db => db.PaymentProviders.AsNoTracking().SingleAsync(p => p.Code == "VNPAY"));
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.Payments.Add(new Payment { InvoiceId = start.Invoice.Id, ProviderId = provider.Id, Amount = 1m, Status = PaymentStatus.PENDING });
            return await db.SaveChangesAsync();
        }));

        // Tên gói không phân biệt hoa thường và dấu thanh (collation Vietnamese_CI_AI); Ơ vẫn là một chữ cái riêng.
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.Plans.Add(new Plan { Name = "GOI THU CƠ BAN " + tag.ToUpperInvariant(), Price = 1m });
            return await db.SaveChangesAsync();
        }));

        // Số hóa đơn không trùng.
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.Invoices.Add(new Invoice
            {
                OrganizationId = org.Id, SubscriptionId = start.Invoice.SubscriptionId, InvoiceNumber = start.Invoice.InvoiceNumber, Amount = 1m,
                Status = InvoiceStatus.CANCELLED,
            });
            return await db.SaveChangesAsync();
        }));

        // Giao dịch đã xong (thất bại) thì thêm giao dịch chờ mới được.
        await factory.WithDbAsync(async db =>
        {
            (await db.Payments.SingleAsync(p => p.Id == start.PaymentId)).Status = PaymentStatus.FAILED;
            return await db.SaveChangesAsync();
        });
        await factory.WithDbAsync(async db =>
        {
            db.Payments.Add(new Payment { InvoiceId = start.Invoice.Id, ProviderId = provider.Id, Amount = 1m, Status = PaymentStatus.PENDING });
            return await db.SaveChangesAsync();
        });
    }

    [LocalDbFact]
    public async Task A_purchase_runs_end_to_end_with_the_real_schema_and_amounts_are_stored_exactly()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin, price: 1_990_000m, days: 365, teachers: 20, ai: 1000);
        var (org, orgAdmin) = await _k.Api.OrgWithAdminAsync();

        var start = await _k.CheckoutAsync(orgAdmin, plan.Id);
        var (_, code, _) = await _k.IpnAsync(VnPayTestKit.Callback(start.PaymentId, 1_990_000m, transactionNo: "SQL-" + BillingTestKit.Unique()));

        Assert.Equal("00", code);
        var today = BillingTestKit.Today;
        var rows = await factory.WithDbAsync(async db => new
        {
            Invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == start.Invoice.Id),
            Payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == start.PaymentId),
            Subscriptions = await db.Subscriptions.AsNoTracking().Where(s => s.OrganizationId == org.Id).OrderBy(s => s.Id).ToListAsync(),
        });
        Assert.Equal((InvoiceStatus.PAID, 1_990_000m, PaymentStatus.SUCCEEDED, 1_990_000m), (rows.Invoice.Status, rows.Invoice.Amount, rows.Payment.Status, rows.Payment.Amount));
        Assert.Equal([SubscriptionStatus.CANCELLED, SubscriptionStatus.ACTIVE], rows.Subscriptions.Select(s => s.Status));      // gói Miễn phí bị thay
        Assert.Equal((today, today.AddDays(364)), (rows.Subscriptions[1].StartDate, rows.Subscriptions[1].EndDate));
        Assert.Contains("\"vnp_TxnRef\"", rows.Payment.RawResponse);
        Assert.NotNull(rows.Payment.PaidAt);
    }

    [LocalDbFact]
    public async Task The_same_ipn_arriving_many_times_at_once_changes_the_data_exactly_once()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (org, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var start = await _k.CheckoutAsync(orgAdmin, plan.Id);
        var ipn = VnPayTestKit.Callback(start.PaymentId, 500_000m, transactionNo: "CONC-" + BillingTestKit.Unique());

        var codes = (await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _k.IpnAsync(ipn)))).Select(r => r.Code).Order().ToList();

        Assert.Equal(1, codes.Count(c => c == "00"));
        Assert.Equal(7, codes.Count(c => c == "02"));
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.BILLING_STATUS_CHANGED && l.TargetId == start.PaymentId)));
        var subscriptions = await factory.WithDbAsync(db => db.Subscriptions.AsNoTracking().Where(s => s.OrganizationId == org.Id && s.PlanId == plan.Id).ToListAsync());
        Assert.Equal(SubscriptionStatus.ACTIVE, Assert.Single(subscriptions).Status);
    }

    [LocalDbFact]
    public async Task A_success_and_a_failure_for_the_same_payment_at_once_end_in_one_consistent_state()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (_, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var start = await _k.CheckoutAsync(orgAdmin, plan.Id);
        var success = VnPayTestKit.Callback(start.PaymentId, 500_000m, transactionNo: "RACE-" + BillingTestKit.Unique());
        var failure = VnPayTestKit.Callback(start.PaymentId, 500_000m, responseCode: "24", transactionStatus: "02", transactionNo: "0");

        var results = await Task.WhenAll(_k.IpnAsync(success), _k.IpnAsync(failure), _k.IpnAsync(success), _k.IpnAsync(failure));

        Assert.All(results, r => Assert.Contains(r.Code, new[] { "00", "02" }));          // mọi phản hồi là đã ghi nhận hoặc đã xử lý trước đó
        Assert.Equal(1, results.Count(r => r.Code == "00"));
        var state = await factory.WithDbAsync(async db => new
        {
            Payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == start.PaymentId),
            Invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == start.Invoice.Id),
            Subscription = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == start.Invoice.SubscriptionId),
        });
        if (state.Payment.Status == PaymentStatus.SUCCEEDED)
            Assert.Equal((InvoiceStatus.PAID, SubscriptionStatus.ACTIVE), (state.Invoice.Status, state.Subscription.Status));
        else
            Assert.Equal((PaymentStatus.FAILED, InvoiceStatus.UNPAID, SubscriptionStatus.PENDING), (state.Payment.Status, state.Invoice.Status, state.Subscription.Status));
    }

    [LocalDbFact]
    public async Task Several_purchases_at_once_by_one_school_create_exactly_one_request()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (org, orgAdmin) = await _k.Api.OrgWithAdminAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Http.PostAsync<PaymentStartDto>("/api/v1/org/billing/checkout", new { planId = plan.Id }, orgAdmin)));

        Assert.Equal(1, results.Count(r => r.Response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.Response.StatusCode == HttpStatusCode.UnprocessableEntity && r.Body.ErrorCode == "PENDING_PURCHASE_EXISTS"));
        Assert.Equal(1, await factory.WithDbAsync(db => db.Invoices.CountAsync(i => i.OrganizationId == org.Id)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.Subscriptions.CountAsync(s => s.OrganizationId == org.Id && s.Status == SubscriptionStatus.PENDING)));
    }

    [LocalDbFact]
    public async Task Purchases_at_once_by_many_schools_get_distinct_invoice_numbers()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var tokens = new List<string>();
        for (var i = 0; i < 8; i++) tokens.Add((await _k.Api.OrgWithAdminAsync()).OrgAdminToken);

        var results = await Task.WhenAll(tokens.Select(t => Http.PostAsync<PaymentStartDto>("/api/v1/org/billing/checkout", new { planId = plan.Id }, t)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Response.StatusCode));
        var numbers = results.Select(r => r.Body.Data!.Invoice.InvoiceNumber!).ToList();
        Assert.Equal(8, numbers.Distinct().Count());
        Assert.All(numbers, n => Assert.Matches($@"^HD{BillingTestKit.Today:yyyyMMdd}-\d{{6}}$", n));
    }

    [LocalDbFact]
    public async Task Background_jobs_fail_stale_payments_cancel_overdue_invoices_and_expire_plans_on_the_real_database()
    {
        var admin = await _k.Api.AdminTokenAsync();
        var plan = await _k.CreatePlanAsync(admin);
        var (org, orgAdmin) = await _k.Api.OrgWithAdminAsync();
        var stale = await _k.CheckoutAsync(orgAdmin, plan.Id);
        var (otherOrg, otherAdmin) = await _k.Api.OrgWithAdminAsync();
        var overdue = await _k.CheckoutAsync(otherAdmin, plan.Id);

        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments SET created_at = DATEADD(minute, -31, SYSUTCDATETIME()) WHERE id IN ({stale.PaymentId}, {overdue.PaymentId});
            UPDATE invoices SET due_at = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE id = {overdue.Invoice.Id};
            UPDATE subscriptions SET end_date = DATEADD(day, -1, CAST(SYSUTCDATETIME() AS date))
                WHERE organization_id IN ({org.Id}, {otherOrg.Id}) AND status = 'ACTIVE';
            """));

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<BillingMaintenanceService>().RunAsync(includeExpiry: true, default);

        Assert.Equal((2, 1, 2), (result.PaymentsFailed, result.InvoicesCancelled, result.SubscriptionsExpired));
        var rows = await factory.WithDbAsync(async db => new
        {
            Stale = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == stale.PaymentId),
            StaleInvoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == stale.Invoice.Id),
            OverdueInvoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == overdue.Invoice.Id),
            OverdueSubscription = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == overdue.Invoice.SubscriptionId),
        });
        Assert.Equal(PaymentStatus.FAILED, rows.Stale.Status);
        Assert.Equal(InvoiceStatus.UNPAID, rows.StaleInvoice.Status);                          // còn hạn: thanh toán lại được
        Assert.Equal((InvoiceStatus.CANCELLED, SubscriptionStatus.CANCELLED), (rows.OverdueInvoice.Status, rows.OverdueSubscription.Status));
    }
}
