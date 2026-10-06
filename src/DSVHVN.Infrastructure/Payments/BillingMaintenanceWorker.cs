using DSVHVN.Application.Billing;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Payments;

/// <summary>Cấu hình mục <c>Billing:Maintenance</c>: bật hoặc tắt tác vụ nền gói và thanh toán, và chu kỳ chạy.</summary>
public sealed class BillingMaintenanceOptions
{
    public const string Section = "Billing:Maintenance";

    public bool Enabled { get; set; } = true;

    /// <summary>Chu kỳ chạy việc giao dịch treo và hóa đơn quá hạn (giây); việc hết hạn gói chỉ chạy một lần mỗi ngày từ 00:05 giờ Việt Nam.</summary>
    public int IntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Tác vụ nền của gói và thanh toán, chạy trong cùng tiến trình với Api: mỗi chu kỳ chuyển giao dịch treo thành thất bại, hủy hóa đơn quá hạn
/// và (mỗi ngày một lần từ 00:05 giờ Việt Nam) chuyển gói quá hạn sang hết hạn. Lỗi một lần chạy chỉ ghi log, lần sau chạy lại.
/// </summary>
public sealed class BillingMaintenanceWorker(
    IServiceScopeFactory scopes,
    IOptions<BillingMaintenanceOptions> options,
    TimeProvider clock,
    ILogger<BillingMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Tác vụ nền gói và thanh toán đang tắt (Billing:Maintenance:Enabled)");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, settings.IntervalSeconds)));
        DateOnly? lastExpiryDay = null;
        do
        {
            try
            {
                var now = clock.GetUtcNow();
                var includeExpiry = BillingRules.ExpiryDue(now, lastExpiryDay);
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<BillingMaintenanceService>().RunAsync(includeExpiry, stoppingToken);
                if (includeExpiry) lastExpiryDay = VietnamTime.Today(now);
                if (result.Total > 0)
                    logger.LogInformation("Tác vụ nền gói và thanh toán: {Failed} giao dịch treo thất bại, {Cancelled} hóa đơn quá hạn bị hủy, {Expired} gói hết hạn",
                        result.PaymentsFailed, result.InvoicesCancelled, result.SubscriptionsExpired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tác vụ nền gói và thanh toán lỗi, sẽ thử lại ở chu kỳ sau");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stop)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stop);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
