using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Application.Translations;

/// <summary>Kết quả một lượt gọi Dịch vụ AI sau các lần thử: có giá trị hoặc có lỗi (không bao giờ cả hai).</summary>
public sealed record AiAttempt<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Gọi Dịch vụ AI tóm tắt và dịch di sản với thời gian chờ mỗi lần (mặc định 90 giây vì nội dung dài) và thử lại tối đa 2 lần, như hợp đồng của các tác vụ AI khác.
/// Lỗi mạng, quá thời gian, 5xx và kết quả sai lược đồ được thử lại; 4xx thì không. Không ghi CSDL: người gọi ghi nhật ký <c>ai_generations</c>.
/// Kết quả được kiểm tra và cắt về độ rộng cột của CSDL ngay trong lần thử, nên kết quả hỏng cũng được thử lại.
/// </summary>
public sealed class HeritageAiRunner(IHeritageAiClient client, IOptions<AiOptions> options, ILogger<HeritageAiRunner> logger)
{
    public string ModelName => client.ModelName;

    public Task<AiAttempt<AiSummarizeResult>> SummarizeAsync(AiSummarizeRequest request, string correlationId, CancellationToken ct) =>
        RunAsync(request.HeritageId, "tóm tắt", async token =>
            NormalizeSummary(await client.SummarizeHeritageAsync(request, correlationId, token)), ct);

    public Task<AiAttempt<AiTranslateResult>> TranslateAsync(AiTranslateRequest request, string correlationId, CancellationToken ct) =>
        RunAsync(request.HeritageId, $"dịch sang {request.TargetLanguage}", async token =>
            NormalizeTranslation(await client.TranslateHeritageAsync(request, correlationId, token), request), ct);

    private async Task<AiAttempt<T>> RunAsync<T>(long heritageId, string what, Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        var settings = options.Value;
        var attempts = 1 + Math.Max(0, settings.MaxRetries);
        var error = "Dịch vụ AI không phản hồi.";
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(settings.HeritageAttemptTimeout);
            try
            {
                return new AiAttempt<T>(await call(timeout.Token), null);
            }
            catch (AiServiceException ex)
            {
                error = ex.Message;
                logger.LogWarning(ex, "Dịch vụ AI lỗi khi {What} di sản {HeritageId} ở lần {Attempt}/{Max}", what, heritageId, attempt, attempts);
                if (!ex.Retryable) break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                error = $"Quá thời gian chờ {settings.HeritageAttemptTimeout.TotalSeconds:0.##} giây.";
                logger.LogWarning("Dịch vụ AI quá thời gian chờ khi {What} di sản {HeritageId} ở lần {Attempt}/{Max}", what, heritageId, attempt, attempts);
            }
            catch (HttpRequestException ex)
            {
                error = "Không kết nối được dịch vụ AI.";
                logger.LogWarning(ex, "Dịch vụ AI không kết nối được khi {What} di sản {HeritageId} ở lần {Attempt}/{Max}", what, heritageId, attempt, attempts);
            }
        }
        return new AiAttempt<T>(default, error);
    }

    // ---- Kiểm kết quả ----

    /// <summary>Tóm tắt phải có cả bản ngắn lẫn bản chi tiết; bản ngắn cắt về 2000 ký tự (độ rộng cột).</summary>
    internal static AiSummarizeResult NormalizeSummary(AiSummarizeResult raw)
    {
        if (string.IsNullOrWhiteSpace(raw.ShortText) || string.IsNullOrWhiteSpace(raw.DetailedText))
            throw new AiServiceException("Dịch vụ AI trả bản tóm tắt thiếu bản ngắn hoặc bản chi tiết.", retryable: true);
        return raw with { ShortText = Cut(raw.ShortText.Trim(), TranslationRules.SummaryShortMaxLength), DetailedText = raw.DetailedText.Trim() };
    }

    /// <summary>
    /// Bản dịch phải có tên; giai đoạn, sự kiện, ảnh chỉ nhận mã đã gửi (mã lạ bị bỏ) và phần chữ rỗng bị bỏ; mọi chuỗi cắt về độ rộng cột.
    /// Yêu cầu có tóm tắt mà kết quả không có là lỗi đầu ra (thử lại như lỗi mạng).
    /// </summary>
    internal static AiTranslateResult NormalizeTranslation(AiTranslateResult raw, AiTranslateRequest request)
    {
        var content = raw.Content;
        if (content is null || string.IsNullOrWhiteSpace(content.Name))
            throw new AiServiceException("Dịch vụ AI trả bản dịch không có tên di sản.", retryable: true);

        var timelineIds = request.Content.Timelines.Select(t => t.Id).ToHashSet();
        var eventIds = request.Content.Events.Select(e => e.Id).ToHashSet();
        var mediaIds = request.Content.Media.Select(m => m.Id).ToHashSet();

        var normalized = new AiHeritageText(
            Cut(content.Name.Trim(), TranslationRules.NameMaxLength),
            Blank(content.ShortDescription) is { } sd ? Cut(sd, TranslationRules.ShortDescriptionMaxLength) : null,
            Blank(content.Content),
            (content.Timelines ?? []).Where(t => timelineIds.Contains(t.Id) && !string.IsNullOrWhiteSpace(t.Name)).DistinctBy(t => t.Id)
                .Select(t => t with { Name = Cut(t.Name.Trim(), TranslationRules.NameMaxLength), Description = Blank(t.Description) }).ToList(),
            (content.Events ?? []).Where(e => eventIds.Contains(e.Id) && !string.IsNullOrWhiteSpace(e.Name)).DistinctBy(e => e.Id)
                .Select(e => e with { Name = Cut(e.Name.Trim(), TranslationRules.NameMaxLength), Description = Blank(e.Description) }).ToList(),
            (content.Media ?? []).Where(m => mediaIds.Contains(m.Id) && !string.IsNullOrWhiteSpace(m.Caption)).DistinctBy(m => m.Id)
                .Select(m => m with { Caption = Cut(m.Caption.Trim(), TranslationRules.CaptionMaxLength) }).ToList());

        AiSummaryText? summary = null;
        if (request.Summary is not null)
        {
            if (raw.Summary is null || string.IsNullOrWhiteSpace(raw.Summary.ShortText) || string.IsNullOrWhiteSpace(raw.Summary.DetailedText))
                throw new AiServiceException("Dịch vụ AI không trả bản tóm tắt đã dịch.", retryable: true);
            summary = new AiSummaryText(Cut(raw.Summary.ShortText.Trim(), TranslationRules.SummaryShortMaxLength), raw.Summary.DetailedText.Trim());
        }

        return raw with { Content = normalized, Summary = summary };
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
