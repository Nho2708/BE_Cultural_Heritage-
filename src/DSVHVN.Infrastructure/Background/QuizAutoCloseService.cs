using DSVHVN.Application.Quizzes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Background;

/// <summary>
/// Tác vụ nền chuyển bài kiểm tra đã phát hành quá giờ đóng sang đã đóng và tự nộp các lượt làm bài đang dở. Lỗi một vòng chỉ ghi log,
/// vòng sau chạy lại; chu kỳ theo <see cref="QuizAutoCloseOptions"/>.
/// </summary>
public sealed class QuizAutoCloseService(IServiceScopeFactory scopes, IOptions<QuizAutoCloseOptions> options, ILogger<QuizAutoCloseService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.Value.AutoCloseIntervalSeconds;
        if (seconds <= 0)
        {
            logger.LogInformation("Tác vụ tự đóng bài kiểm tra đang tắt (Quizzes:AutoCloseIntervalSeconds <= 0).");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<QuizAutoCloser>().CloseDueAsync(stoppingToken);
                if (result.ClosedQuizzes > 0)
                    logger.LogInformation("Đã tự đóng {Quizzes} bài kiểm tra quá giờ đóng, tự nộp {Attempts} lượt làm bài đang dở.",
                        result.ClosedQuizzes, result.ExpiredAttempts);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tác vụ tự đóng bài kiểm tra gặp lỗi, sẽ thử lại ở vòng sau.");
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
