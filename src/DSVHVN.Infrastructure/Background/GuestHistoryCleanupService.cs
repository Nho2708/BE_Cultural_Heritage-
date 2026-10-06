using DSVHVN.Application.Guests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Background;

/// <summary>
/// Tác vụ nền xóa thật mọi lượt lịch sử của khách tham quan cũ hơn 90 ngày (quy tắc Lịch sử của khách). Chạy một lần lúc khởi động rồi theo chu kỳ của
/// <see cref="GuestCleanupOptions"/>; lỗi một vòng chỉ ghi log, vòng sau chạy lại. Không ghi mã thiết bị hay nội dung lịch sử vào log, chỉ ghi số lượt đã xóa.
/// </summary>
public sealed class GuestHistoryCleanupService(IServiceScopeFactory scopes, IOptions<GuestCleanupOptions> options, ILogger<GuestHistoryCleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = options.Value.CleanupIntervalHours;
        if (hours <= 0)
        {
            logger.LogInformation("Tác vụ dọn lịch sử khách tham quan đang tắt (Guests:CleanupIntervalHours <= 0).");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<GuestService>().PurgeExpiredAsync(stoppingToken);
                if (deleted > 0) logger.LogInformation("Đã xóa {Deleted} lượt lịch sử khách tham quan quá 90 ngày.", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tác vụ dọn lịch sử khách tham quan gặp lỗi, sẽ thử lại ở vòng sau.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
