using System.Net;
using System.Net.Http.Json;
using DSVHVN.Application.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>Bước lặp lại của luồng gói và thanh toán qua HTTP: tạo gói, mua gói, gửi IPN VNPay như cổng gửi.</summary>
public sealed class BillingTestKit(ApiFactory factory)
{
    public PlatformClient Api { get; } = new(factory);
    public HttpClient Http => Api.Http;
    public ApiFactory Factory { get; } = factory;

    /// <summary>Máy khách không tự theo chuyển hướng, để đọc được địa chỉ chuyển tới của trang trả về.</summary>
    public HttpClient NoRedirect { get; } = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static DateOnly Today => VietnamTime.Today(DateTimeOffset.UtcNow);

    public static string Unique() => PlatformClient.Unique();

    /// <summary>Tạo gói trả phí qua API của quản trị hệ thống; tên có hậu tố để không trùng giữa các test dùng chung CSDL.</summary>
    public async Task<PlanDto> CreatePlanAsync(string adminToken, decimal price = 500_000m, int days = 30, int teachers = 5, int ai = 100, string? name = null)
    {
        var (res, body) = await Http.PostAsync<PlanDto>("/api/v1/admin/plans",
            new { name = name ?? "Gói thử " + Unique(), price, durationDays = days, maxTeachers = teachers, maxAiRequests = ai }, adminToken);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"Tạo gói: {(int)res.StatusCode} {body.Message}");
        return body.Data!;
    }

    public async Task<PaymentStartDto> CheckoutAsync(string orgAdminToken, int planId)
    {
        var (res, body) = await Http.PostAsync<PaymentStartDto>("/api/v1/org/billing/checkout", new { planId }, orgAdminToken);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"Mua gói: {(int)res.StatusCode} {body.Message}");
        return body.Data!;
    }

    /// <summary>Gửi IPN như VNPay (HTTPS GET, không đăng nhập) và đọc khuôn phản hồi <c>{RspCode, Message}</c>.</summary>
    public async Task<(HttpResponseMessage Response, string Code, string Message)> IpnAsync(IReadOnlyDictionary<string, string> parameters)
    {
        var response = await Http.GetAsync(VnPayTestKit.IpnPath(parameters));
        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        return (response, json!["RspCode"], json["Message"]);
    }

    public async Task<string> PayAsync(PaymentStartDto start, string responseCode = "00", string transactionStatus = "00")
    {
        var (_, code, _) = await IpnAsync(VnPayTestKit.Callback(start.PaymentId, start.Invoice.Amount!.Value, responseCode, transactionStatus,
            transactionNo: "VNP" + Unique()));
        return code;
    }

    public async Task<BillingOverviewDto> OverviewAsync(string token)
    {
        var (res, body) = await Http.GetAsync<BillingOverviewDto>("/api/v1/org/billing", token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return body.Data!;
    }

    /// <summary>Làm trường mất gói hiệu lực (đặt mọi gói của trường thành đã hủy) để thử mua mới và kích hoạt gói Miễn phí.</summary>
    public Task RemovePlansAsync(long organizationId) => Factory.WithDbAsync(async db =>
    {
        foreach (var s in await db.Subscriptions.Where(s => s.OrganizationId == organizationId).ToListAsync()) s.Status = SubscriptionStatus.CANCELLED;
        return await db.SaveChangesAsync();
    });
}
