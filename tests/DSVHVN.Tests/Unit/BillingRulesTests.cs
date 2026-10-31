using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của gói, hóa đơn, thanh toán: phân loại mua, ngày bắt đầu, số hóa đơn, tiền, giờ chạy tác vụ nền.</summary>
public sealed class BillingRulesTests
{
    [Theory]
    [InlineData(null, 500_000, PurchaseKind.PURCHASE)]
    [InlineData(0, 500_000, PurchaseKind.UPGRADE)]            // từ gói Miễn phí lên gói trả phí là nâng cấp
    [InlineData(500_000, 1_200_000, PurchaseKind.UPGRADE)]
    [InlineData(500_000, 500_000, PurchaseKind.RENEW)]         // cùng gói hoặc cùng giá là gia hạn
    [InlineData(1_200_000, 500_000, PurchaseKind.RENEW)]       // sang gói rẻ hơn là gia hạn bằng gói khác, không phải hạ gói ngay
    public void Purchase_kind_follows_the_price_of_the_current_plan(int? current, int next, PurchaseKind expected) =>
        Assert.Equal(expected, BillingRules.Classify(current, next));

    [Fact]
    public void The_upgrade_note_only_mentions_refunds_when_a_paid_plan_is_replaced()
    {
        var free = new DSVHVN.Domain.Billing.Plan { Name = "Miễn phí", Price = 0m };
        var basic = new DSVHVN.Domain.Billing.Plan { Name = "Cơ bản", Price = 500_000m };

        Assert.DoesNotContain("hoàn", DSVHVN.Application.Billing.BillingMessages.EffectOf(PurchaseKind.UPGRADE, null, free));
        Assert.Equal("Gói mới có hiệu lực ngay. Nâng cấp không hoàn tiền hoặc khấu trừ thời gian chưa dùng của gói Cơ bản hiện tại.",
            DSVHVN.Application.Billing.BillingMessages.EffectOf(PurchaseKind.UPGRADE, null, basic));
    }

    [Fact]
    public void Start_date_is_today_unless_an_unexpired_plan_must_run_out_first_and_upgrades_always_start_today()
    {
        var today = new DateOnly(2026, 10, 8);

        Assert.Equal(today, BillingRules.StartDateFor(PurchaseKind.PURCHASE, today, null));
        Assert.Equal(new DateOnly(2026, 11, 1), BillingRules.StartDateFor(PurchaseKind.RENEW, today, new DateOnly(2026, 10, 31)));
        Assert.Equal(today, BillingRules.StartDateFor(PurchaseKind.RENEW, today, new DateOnly(2026, 10, 7)));      // đã qua hạn: từ ngày nhận IPN
        Assert.Equal(today, BillingRules.StartDateFor(PurchaseKind.RENEW, today, today.AddDays(-1)));
        Assert.Equal(today.AddDays(1), BillingRules.StartDateFor(PurchaseKind.RENEW, today, today));               // hết hạn hôm nay: bắt đầu ngày mai
        Assert.Equal(today, BillingRules.StartDateFor(PurchaseKind.UPGRADE, today, new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void Invoice_numbers_have_the_day_and_a_six_digit_sequence()
    {
        var day = new DateOnly(2026, 10, 8);

        Assert.Equal("HD20261008-", InvoiceNumber.DayPrefix(day));
        Assert.Equal("HD20261008-000001", InvoiceNumber.Format(day, 1));
        Assert.Equal("HD20261008-123456", InvoiceNumber.Format(day, 123456));
        Assert.Equal(12, InvoiceNumber.SequenceOf("HD20261008-000012", day));
        Assert.Equal(0, InvoiceNumber.SequenceOf("HD20261007-000012", day));       // số của ngày khác
        Assert.Equal(0, InvoiceNumber.SequenceOf("HD20261008-12", day));           // sai độ dài
        Assert.Equal(0, InvoiceNumber.SequenceOf(null, day));
        Assert.Equal(0, InvoiceNumber.SequenceOf("HD20261008-00001x", day));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(999_999_999, true)]
    [InlineData(1_000_000_000, false)]
    [InlineData(-1, false)]
    public void Price_is_a_non_negative_whole_number_of_dong_below_one_billion(long price, bool valid) =>
        Assert.Equal(valid, BillingRules.IsValidPrice(price));

    [Fact]
    public void Fractional_prices_are_rejected_and_vnpay_amounts_are_times_100()
    {
        Assert.False(BillingRules.IsValidPrice(1000.5m));
        Assert.Equal(199_000_000L, BillingRules.VnPayAmount(1_990_000m));
        Assert.Equal(199_000_000L, BillingRules.VnPayAmount(1_990_000.00m));
    }

    [Fact]
    public void Money_and_moments_are_written_the_vietnamese_way()
    {
        Assert.Equal("1.200.000", BillingRules.FormatMoney(1_200_000m));
        Assert.Equal("0", BillingRules.FormatMoney(0m));
        Assert.Equal("08/10/2026", BillingRules.FormatDate(new DateOnly(2026, 10, 8)));
        Assert.Equal("17:05 ngày 08/10/2026", BillingRules.FormatMoment(new DateTime(2026, 10, 8, 10, 5, 0, DateTimeKind.Utc)));   // UTC+7
    }

    [Fact]
    public void Expiry_runs_once_a_day_from_five_past_midnight_vietnam_time()
    {
        // 16:59 UTC ngày 07 = 23:59 giờ Việt Nam ngày 07; 17:04 UTC = 00:04 ngày 08 (chưa tới 00:05); 17:05 UTC = 00:05 ngày 08.
        var beforeFive = new DateTimeOffset(2026, 10, 7, 17, 4, 0, TimeSpan.Zero);
        var atFive = new DateTimeOffset(2026, 10, 7, 17, 5, 0, TimeSpan.Zero);
        var day = new DateOnly(2026, 10, 8);

        Assert.False(BillingRules.ExpiryDue(beforeFive, new DateOnly(2026, 10, 7)));
        Assert.True(BillingRules.ExpiryDue(atFive, new DateOnly(2026, 10, 7)));
        Assert.True(BillingRules.ExpiryDue(atFive, null));
        Assert.False(BillingRules.ExpiryDue(atFive.AddHours(6), day));            // đã chạy hôm nay rồi
        Assert.True(BillingRules.ExpiryDue(atFive.AddHours(24), day));            // sang ngày hôm sau
    }

    [Fact]
    public void Payment_windows_follow_the_chosen_rules()
    {
        Assert.Equal(TimeSpan.FromHours(24), BillingRules.InvoiceDueIn);
        Assert.Equal(TimeSpan.FromMinutes(15), BillingRules.PaymentWindow);
        Assert.Equal(TimeSpan.FromMinutes(30), BillingRules.PendingPaymentTimeout);
        Assert.Equal("VNPAY", BillingRules.VnPayProviderCode);
    }
}
