using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Speech;

/// <summary>
/// Tác vụ nền tạo trước giọng đọc: chạy hàng đợi của <see cref="SelfHostedTtsService"/> khi <c>Tts:Provider = SelfHosted</c>, các chế độ khác thì thoát ngay.
/// Việc tạo trước luôn nhường yêu cầu nghe ngay: xem <see cref="PriorityGate"/>.
/// </summary>
public sealed class SpeechPrefetchWorker(IServiceProvider services, IOptions<TtsOptions> options, ILogger<SpeechPrefetchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Provider != TtsProvider.SelfHosted)
        {
            logger.LogInformation("Tạo trước giọng đọc đang tắt (Tts:Provider không phải SelfHosted).");
            return;
        }

        var service = services.GetRequiredService<SelfHostedTtsService>();
        await Task.Yield();
        await service.RunPrefetchAsync(stoppingToken);
    }
}
