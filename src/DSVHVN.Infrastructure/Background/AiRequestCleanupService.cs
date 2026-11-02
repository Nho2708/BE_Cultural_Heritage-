using DSVHVN.Application.Lessons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Background;

/// <summary>Mục <c>Ai:Maintenance</c> của cấu hình: bật hoặc tắt tác vụ nền dọn lượt gọi AI bị bỏ dở, và chu kỳ chạy (giây).</summary>
public sealed class AiMaintenanceOptions
{
    public const string Section = "Ai:Maintenance";

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Tác vụ nền chuyển lượt gọi AI đang xử lý quá ngưỡng (máy chủ dừng hoặc khởi động lại giữa chừng) sang thất bại, chạy ngay lúc khởi động rồi mỗi chu kỳ.
/// Lỗi một vòng chỉ ghi log, vòng sau chạy lại.
/// </summary>
public sealed class AiRequestCleanupService(IServiceScopeFactory scopes, IOptions<AiMaintenanceOptions> options, ILogger<AiRequestCleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || settings.IntervalSeconds <= 0)
        {
            logger.LogInformation("Tác vụ dọn lượt gọi AI bị bỏ dở đang tắt (Ai:Maintenance).");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, settings.IntervalSeconds)));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var failed = await scope.ServiceProvider.GetRequiredService<StaleAiRequestCleaner>().FailStaleAsync(stoppingToken);
                if (failed > 0) logger.LogInformation("Đã chuyển {Count} lượt gọi AI bị bỏ dở sang thất bại.", failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tác vụ dọn lượt gọi AI bị bỏ dở gặp lỗi, sẽ thử lại ở vòng sau.");
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
