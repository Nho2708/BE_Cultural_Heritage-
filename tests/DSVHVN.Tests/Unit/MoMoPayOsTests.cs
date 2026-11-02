using System.Text.Json;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Thanh toán qua MoMo sandbox và PayOS ở tầng service: tạo giao dịch (yêu cầu gửi cổng có chữ ký đúng), IPN và webhook (kích hoạt gói một lần,
/// lặp không đổi gì, sai chữ ký, lệch tiền, thất bại), chọn phương thức (mã lạ, cổng tắt, cổng lỗi mạng) và danh sách phương thức. Mạng được thay bằng
/// <see cref="StubHandler"/>; khóa là khóa thử của <see cref="PaymentGatewayTestKit"/>.
/// </summary>
public sealed class MoMoPayOsTests : IDisposable
{
    private readonly BillingHarness _b = new();

    public void Dispose() => _b.Dispose();

    private PaymentGatewayTestKit Kit => _b.H.RunAsync(sp => Task.FromResult(sp.GetRequiredService<PaymentGatewayTestKit>())).GetAwaiter().GetResult();

    private Task EnableAsync(params string[] codes) => _b.H.QueryAsync(async db =>
    {
        foreach (var code in codes) db.PaymentProviders.Add(new PaymentProvider { Code = code, Name = code, IsActive = true });
        return await db.SaveChangesAsync();
    });

    private Task<PaymentStartDto> CheckoutAsync(Actor actor, string? provider) =>
        _b.H.Service<BillingService, PaymentStartDto>(s => s.StartCheckoutAsync(actor, new CheckoutRequest(_b.BasicId, provider), default));

    private Task<MoMoIpnResult> MoMoIpnAsync(Dictionary<string, string> parameters) =>
        _b.H.Service<MoMoCallbackService, MoMoIpnResult>(s => s.HandleIpnAsync(parameters, "203.0.113.60", default));

    private Task PayOsWebhookAsync(string body) =>
        _b.H.Service<PayOsCallbackService>(s => s.HandleWebhookAsync(body, "203.0.113.61", default));

    private async Task<AppException> Fails(Func<Task> action) => await Assert.ThrowsAsync<AppException>(action);

    // ---- MoMo ----

    [Fact]
    public async Task MoMo_checkout_sends_a_correctly_signed_request_and_returns_the_pay_url()
    {
        await EnableAsync("MOMO");
        var (_, actor) = await _b.NewOrgAsync();

        var start = await CheckoutAsync(actor, "MOMO");

        Assert.Equal("https://test-payment.momo.vn/pay/abc", start.PaymentUrl);
        Assert.Equal("MOMO", start.ProviderCode);
        using var body = JsonDocument.Parse(Kit.MoMoHandler.LastBody!);
        string Get(string name) => body.RootElement.GetProperty(name).ToString();
        Assert.StartsWith($"{start.PaymentId}_", Get("orderId"));
        Assert.Equal(((long)BillingHarness.BasicPrice).ToString(), Get("amount"));
        var raw = $"accessKey={PaymentGatewayTestKit.MoMoAccessKey}&amount={Get("amount")}&extraData=&ipnUrl={Get("ipnUrl")}&orderId={Get("orderId")}"
                  + $"&orderInfo={Get("orderInfo")}&partnerCode={PaymentGatewayTestKit.MoMoPartnerCode}&redirectUrl={Get("redirectUrl")}"
                  + $"&requestId={Get("requestId")}&requestType={Get("requestType")}";
        Assert.Equal(PaymentGatewayTestKit.Hmac(raw, PaymentGatewayTestKit.MoMoSecretKey), Get("signature"));

        var payment = await _b.PaymentAsync(start.PaymentId);
        Assert.Equal(PaymentStatus.PENDING, payment.Status);
        var providerCode = await _b.H.QueryAsync(db => db.PaymentProviders.Where(p => p.Id == payment.ProviderId).Select(p => p.Code).SingleAsync());
        Assert.Equal("MOMO", providerCode);
    }

    [Fact]
    public async Task MoMo_ipn_activates_the_plan_once_and_a_repeat_changes_nothing()
    {
        await EnableAsync("MOMO");
        var (org, actor) = await _b.NewOrgAsync();
        var start = await CheckoutAsync(actor, "MOMO");

        var first = await MoMoIpnAsync(PaymentGatewayTestKit.MoMoCallback(start.PaymentId, BillingHarness.BasicPrice));
        var again = await MoMoIpnAsync(PaymentGatewayTestKit.MoMoCallback(start.PaymentId, BillingHarness.BasicPrice));

        Assert.Equal(204, first.HttpStatus);
        Assert.Equal(204, again.HttpStatus);
        var payment = await _b.PaymentAsync(start.PaymentId);
        Assert.Equal((PaymentStatus.SUCCEEDED, "3000001"), (payment.Status, payment.ProviderTransactionId));
        Assert.Equal(InvoiceStatus.PAID, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
        Assert.Equal(SubscriptionStatus.ACTIVE, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status);
        Assert.Equal(1, (await _b.SubscriptionsAsync(org.Id)).Count(s => s.Status == SubscriptionStatus.ACTIVE && s.Plan!.Price > 0m));
    }

    [Fact]
    public async Task MoMo_ipn_with_a_bad_signature_or_wrong_amount_is_rejected_and_changes_nothing()
    {
        await EnableAsync("MOMO");
        var (_, actor) = await _b.NewOrgAsync();
        var start = await CheckoutAsync(actor, "MOMO");

        var unsigned = await MoMoIpnAsync(PaymentGatewayTestKit.MoMoCallback(start.PaymentId, BillingHarness.BasicPrice, sign: false));
        var wrongKey = await MoMoIpnAsync(PaymentGatewayTestKit.MoMoCallback(start.PaymentId, BillingHarness.BasicPrice, secret: "khoa-sai"));
        var wrongAmount = await MoMoIpnAsync(PaymentGatewayTestKit.MoMoCallback(start.PaymentId, BillingHarness.BasicPrice, rawAmount: 1000));

        Assert.All(new[] { unsigned, wrongKey, wrongAmount }, r => Assert.Equal(400, r.HttpStatus));
        Assert.Equal(PaymentStatus.PENDING, (await _b.PaymentAsync(start.PaymentId)).Status);
        Assert.Equal(InvoiceStatus.UNPAID, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
        Assert.Equal(3, await _b.AuditCountAsync(AuditAction.IPN_REJECTED));
    }

    [Fact]
    public async Task MoMo_ipn_for_a_vnpay_payment_is_not_found()
    {
        var (_, actor) = await _b.NewOrgAsync();
        var start = await _b.CheckoutAsync(actor, _b.BasicId);   // mặc định VNPay

        var result = await MoMoIpnAsync(PaymentGatewayTestKit.MoMoCallback(start.PaymentId, BillingHarness.BasicPrice));

        Assert.Equal(400, result.HttpStatus);
        Assert.Equal(PaymentStatus.PENDING, (await _b.PaymentAsync(start.PaymentId)).Status);
    }

    [Fact]
    public async Task MoMo_ipn_with_a_failure_code_fails_the_payment_and_keeps_the_invoice_payable()
    {
        await EnableAsync("MOMO");
        var (_, actor) = await _b.NewOrgAsync();
        var start = await CheckoutAsync(actor, "MOMO");

        var result = await MoMoIpnAsync(PaymentGatewayTestKit.MoMoCallback(start.PaymentId, BillingHarness.BasicPrice, resultCode: 1006, transId: "0"));

        Assert.Equal(204, result.HttpStatus);
        Assert.Equal(PaymentStatus.FAILED, (await _b.PaymentAsync(start.PaymentId)).Status);
        Assert.Equal(InvoiceStatus.UNPAID, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
        Assert.Equal(SubscriptionStatus.PENDING, (await _b.SubscriptionAsync(start.Invoice.SubscriptionId)).Status);
    }

    // ---- PayOS ----

    [Fact]
    public async Task PayOs_checkout_sends_a_signed_request_with_the_offset_order_code()
    {
        await EnableAsync("PAYOS");
        var (_, actor) = await _b.NewOrgAsync();

        var start = await CheckoutAsync(actor, "PAYOS");

        Assert.Equal("https://pay.payos.vn/web/abc", start.PaymentUrl);
        Assert.Equal("PAYOS", start.ProviderCode);
        var handler = Kit.PayOsHandler;
        Assert.Equal("payos-client", handler.LastHeaders["x-client-id"]);
        Assert.Equal("payos-api-key", handler.LastHeaders["x-api-key"]);
        using var body = JsonDocument.Parse(handler.LastBody!);
        string Get(string name) => body.RootElement.GetProperty(name).ToString();
        Assert.Equal((start.PaymentId + PaymentGatewayTestKit.PayOsOffset).ToString(), Get("orderCode"));
        Assert.True(Get("description").Length <= 25);
        var raw = $"amount={Get("amount")}&cancelUrl={Get("cancelUrl")}&description={Get("description")}&orderCode={Get("orderCode")}&returnUrl={Get("returnUrl")}";
        Assert.Equal(PaymentGatewayTestKit.Hmac(raw, PaymentGatewayTestKit.PayOsChecksumKey), Get("signature"));
    }

    [Fact]
    public async Task PayOs_webhook_activates_the_plan_once_and_a_repeat_changes_nothing()
    {
        await EnableAsync("PAYOS");
        var (org, actor) = await _b.NewOrgAsync();
        var start = await CheckoutAsync(actor, "PAYOS");

        await PayOsWebhookAsync(PaymentGatewayTestKit.PayOsWebhook(start.PaymentId, BillingHarness.BasicPrice));
        await PayOsWebhookAsync(PaymentGatewayTestKit.PayOsWebhook(start.PaymentId, BillingHarness.BasicPrice));

        var payment = await _b.PaymentAsync(start.PaymentId);
        Assert.Equal((PaymentStatus.SUCCEEDED, "FT26001"), (payment.Status, payment.ProviderTransactionId));
        Assert.Equal(InvoiceStatus.PAID, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
        Assert.Equal(1, (await _b.SubscriptionsAsync(org.Id)).Count(s => s.Status == SubscriptionStatus.ACTIVE && s.Plan!.Price > 0m));
    }

    [Fact]
    public async Task PayOs_webhook_with_a_bad_signature_wrong_amount_or_unknown_order_changes_nothing()
    {
        await EnableAsync("PAYOS");
        var (_, actor) = await _b.NewOrgAsync();
        var start = await CheckoutAsync(actor, "PAYOS");

        await PayOsWebhookAsync(PaymentGatewayTestKit.PayOsWebhook(start.PaymentId, BillingHarness.BasicPrice, sign: false));
        await PayOsWebhookAsync(PaymentGatewayTestKit.PayOsWebhook(start.PaymentId, BillingHarness.BasicPrice, secret: "khoa-sai"));
        await PayOsWebhookAsync(PaymentGatewayTestKit.PayOsWebhook(start.PaymentId, BillingHarness.BasicPrice, rawAmount: 2000));
        await PayOsWebhookAsync(PaymentGatewayTestKit.PayOsWebhook(987654, BillingHarness.BasicPrice));   // mã đơn mẫu PayOS gửi khi khai webhook

        Assert.Equal(PaymentStatus.PENDING, (await _b.PaymentAsync(start.PaymentId)).Status);
        Assert.Equal(InvoiceStatus.UNPAID, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
        Assert.Equal(4, await _b.AuditCountAsync(AuditAction.IPN_REJECTED));
    }

    [Fact]
    public async Task PayOs_webhook_that_is_not_a_success_fails_the_payment()
    {
        await EnableAsync("PAYOS");
        var (_, actor) = await _b.NewOrgAsync();
        var start = await CheckoutAsync(actor, "PAYOS");

        await PayOsWebhookAsync(PaymentGatewayTestKit.PayOsWebhook(start.PaymentId, BillingHarness.BasicPrice, code: "01", success: false));

        Assert.Equal(PaymentStatus.FAILED, (await _b.PaymentAsync(start.PaymentId)).Status);
        Assert.Equal(InvoiceStatus.UNPAID, (await _b.InvoiceAsync(start.Invoice.Id)).Status);
    }

    [Fact]
    public async Task PayOs_webhook_with_garbage_body_is_ignored_without_error()
    {
        await EnableAsync("PAYOS");
        await PayOsWebhookAsync("không phải JSON");
        await PayOsWebhookAsync("""{"code":"00"}""");
        Assert.Equal(2, await _b.AuditCountAsync(AuditAction.IPN_REJECTED));
    }

    // ---- Chọn phương thức ----

    [Fact]
    public async Task An_unknown_provider_is_refused_and_a_switched_off_one_is_unavailable_without_creating_anything()
    {
        var (_, actor) = await _b.NewOrgAsync();

        var unknown = await Fails(() => CheckoutAsync(actor, "KHONG_CO"));
        var off = await Fails(() => CheckoutAsync(actor, "MOMO"));   // có khóa nhưng không có dòng bật trong payment_providers

        Assert.Equal((422, "INVALID"), (unknown.StatusCode, unknown.AppMessage.Code));
        Assert.Equal((503, "PAYMENT_GATEWAY_UNAVAILABLE"), (off.StatusCode, off.AppMessage.Code));
        Assert.Equal(0, await _b.H.QueryAsync(db => db.Invoices.CountAsync()));
    }

    [Fact]
    public async Task When_the_gateway_cannot_be_reached_checkout_answers_503_and_the_invoice_stays_payable_with_another_provider()
    {
        await EnableAsync("MOMO");
        var (_, actor) = await _b.NewOrgAsync();
        Kit.MoMoHandler.Fail = true;

        var ex = await Fails(() => CheckoutAsync(actor, "MOMO"));

        Assert.Equal((503, "PAYMENT_GATEWAY_UNAVAILABLE"), (ex.StatusCode, ex.AppMessage.Code));
        var payment = await _b.H.QueryAsync(db => db.Payments.AsNoTracking().SingleAsync());
        Assert.Equal(PaymentStatus.FAILED, payment.Status);

        var retry = await _b.H.Service<BillingService, PaymentStartDto>(s => s.PayInvoiceAsync(actor, payment.InvoiceId, "VNPAY", default));
        Assert.Equal("VNPAY", retry.ProviderCode);
        Assert.StartsWith("https://sandbox.vnpayment.vn/", retry.PaymentUrl);
    }

    [Fact]
    public async Task Pay_again_can_switch_to_another_provider()
    {
        await EnableAsync("MOMO");
        var (_, actor) = await _b.NewOrgAsync();
        var first = await _b.CheckoutAsync(actor, _b.BasicId);   // VNPay

        var second = await _b.H.Service<BillingService, PaymentStartDto>(s => s.PayInvoiceAsync(actor, first.Invoice.Id, "MOMO", default));

        Assert.Equal("MOMO", second.ProviderCode);
        Assert.Equal(PaymentStatus.FAILED, (await _b.PaymentAsync(first.PaymentId)).Status);   // giao dịch chờ cũ bị thay
        Assert.Equal(PaymentStatus.PENDING, (await _b.PaymentAsync(second.PaymentId)).Status);
    }

    [Fact]
    public async Task The_provider_list_shows_only_configured_and_switched_on_gateways_with_vnpay_first()
    {
        var onlyVnPay = await _b.H.Service<BillingQueries, IReadOnlyList<PaymentProviderDto>>(q => q.ListPaymentProvidersAsync(default));
        Assert.Equal(["VNPAY"], onlyVnPay.Select(p => p.Code));

        await EnableAsync("MOMO", "PAYOS");
        var all = await _b.H.Service<BillingQueries, IReadOnlyList<PaymentProviderDto>>(q => q.ListPaymentProvidersAsync(default));
        Assert.Equal(["VNPAY", "MOMO", "PAYOS"], all.Select(p => p.Code));
        Assert.All(all, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public async Task The_confirmation_text_names_the_chosen_gateway()
    {
        var (_, actor) = await _b.NewOrgAsync();

        var quote = await _b.H.Service<BillingQueries, PurchaseQuoteDto>(q => q.GetQuoteAsync(actor, _b.BasicId, default, "Ví MoMo"));

        Assert.Contains("qua Ví MoMo.", quote.Confirmation);
    }
}
