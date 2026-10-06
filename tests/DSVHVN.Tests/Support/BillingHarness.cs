using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Support;

/// <summary>
/// Cảnh dựng sẵn cho kiểm thử gói và thanh toán ở tầng service: một quản trị hệ thống, hai gói trả phí (Cơ bản 500.000 đ / 30 ngày, Nâng cao 1.990.000 đ / 365 ngày)
/// và các thao tác ngắn (trường mới, mua, gửi IPN, đọc trạng thái). Đồng hồ là <see cref="ServiceHarness.Clock"/> (08:00 giờ Việt Nam ngày 29/09/2026).
/// </summary>
public sealed class BillingHarness : IDisposable
{
    public const decimal BasicPrice = 500_000m;
    public const decimal ProPrice = 1_990_000m;

    public ServiceHarness H { get; } = new();
    public Actor Admin { get; private set; } = null!;
    public int BasicId { get; private set; }
    public int ProId { get; private set; }

    public BillingHarness()
    {
        H.QueryAsync(async db =>
        {
            db.PaymentProviders.Add(new PaymentProvider { Code = Domain.Rules.BillingRules.VnPayProviderCode, Name = "VNPay", IsActive = true });
            return await db.SaveChangesAsync();
        }).GetAwaiter().GetResult();
        var admin = H.AddUserAsync("admin.test", RoleCode.ADMIN, null).GetAwaiter().GetResult();
        Admin = new Actor(admin.Id, RoleCode.ADMIN, null, "203.0.113.5");
        BasicId = CreatePlanAsync("Cơ bản", BasicPrice, 30, 5, 100).GetAwaiter().GetResult().Id;
        ProId = CreatePlanAsync("Nâng cao", ProPrice, 365, 20, 1000).GetAwaiter().GetResult().Id;
    }

    public DateOnly Today => Domain.Rules.VietnamTime.Today(H.Clock.GetUtcNow());

    public void Dispose() => H.Dispose();

    public Task<PlanDto> CreatePlanAsync(string name, decimal price, int days, int teachers, int ai) =>
        H.Service<PlanService, PlanDto>(s => s.CreateAsync(new PlanRequest(name, price, days, teachers, ai), Admin, default));

    /// <summary>Trường mới (mặc định có gói Miễn phí như khi quản trị hệ thống tạo trường) và quản trị trường của trường đó.</summary>
    public async Task<(Organization Org, Actor Actor)> NewOrgAsync(bool withFreePlan = true, string? name = null)
    {
        var org = await H.AddOrganizationAsync(name ?? "Trường " + Guid.NewGuid().ToString("N")[..6], withFreePlan);
        var user = await H.AddUserAsync("qt." + Guid.NewGuid().ToString("N")[..8], RoleCode.ORG_ADMIN, org.Id);
        return (org, new Actor(user.Id, RoleCode.ORG_ADMIN, org.Id, "203.0.113.6"));
    }

    public Task<PaymentStartDto> CheckoutAsync(Actor actor, int planId) =>
        H.Service<BillingService, PaymentStartDto>(s => s.StartCheckoutAsync(actor, new CheckoutRequest(planId), default));

    public Task<PaymentStartDto> PayAsync(Actor actor, long invoiceId) =>
        H.Service<BillingService, PaymentStartDto>(s => s.PayInvoiceAsync(actor, invoiceId, default));

    public Task<BillingOverviewDto> OverviewAsync(Actor actor) =>
        H.Service<BillingQueries, BillingOverviewDto>(s => s.GetOverviewAsync(actor, default));

    private int _transactionCounter = 14_000_000;

    /// <summary>Mã giao dịch của cổng tự đánh số mới ở mỗi lần gọi (<c>"auto"</c>); truyền chuỗi khác để ép mã, <c>null</c> để bỏ tham số.</summary>
    public Task<IpnResponse> IpnAsync(long paymentId, decimal price, string responseCode = "00", string transactionStatus = "00",
        string? transactionNo = "auto", bool sign = true, long? rawAmount = null, string secret = VnPayTestKit.HashSecret)
    {
        if (transactionNo == "auto") transactionNo = Interlocked.Increment(ref _transactionCounter).ToString();
        return H.Service<VnPayCallbackService, IpnResponse>(s => s.HandleIpnAsync(
            VnPayTestKit.Callback(paymentId, price, responseCode, transactionStatus, transactionNo, secret, sign, rawAmount), "203.0.113.50", default));
    }

    /// <summary>Mua gói và gửi IPN thành công cho giao dịch vừa tạo; trả kết quả mua.</summary>
    public async Task<PaymentStartDto> BuyAsync(Actor actor, int planId, string? transactionNo = null)
    {
        var start = await CheckoutAsync(actor, planId);
        var price = start.Invoice.Amount!.Value;
        var response = await IpnAsync(start.PaymentId, price, transactionNo: transactionNo ?? ("T" + start.PaymentId));
        Assert.Equal("00", response.RspCode);
        return start;
    }

    public Task<List<Subscription>> SubscriptionsAsync(long orgId) =>
        H.QueryAsync(db => db.Subscriptions.AsNoTracking().Include(s => s.Plan).Where(s => s.OrganizationId == orgId).OrderBy(s => s.Id).ToListAsync());

    public Task<Subscription> SubscriptionAsync(long id) =>
        H.QueryAsync(db => db.Subscriptions.AsNoTracking().Include(s => s.Plan).SingleAsync(s => s.Id == id));

    public Task<Invoice> InvoiceAsync(long id) => H.QueryAsync(db => db.Invoices.AsNoTracking().SingleAsync(i => i.Id == id));

    public Task<Payment> PaymentAsync(long id) => H.QueryAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.Id == id));

    /// <summary>Số dòng nhật ký của một mã thao tác.</summary>
    public Task<int> AuditCountAsync(AuditAction action) => H.QueryAsync(db => db.AuditLogs.CountAsync(l => l.Action == action));
}
