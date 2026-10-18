using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSVHVN.Application.Speech;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Speech;

/// <summary>
/// Triển khai dịch vụ chuyển văn bản thành giọng nói dùng FPT.AI TTS v5 (giọng Ban Mai, Thu Dung, ...).
/// Kết quả âm thanh được lưu cache trong MemoryCache theo mã băm (MD5/SHA256) của nội dung để tiết kiệm hạn ngạch FPT.AI.
/// </summary>
public sealed class FptAiTtsService(
    HttpClient http,
    IOptions<FptAiOptions> options,
    IMemoryCache cache,
    ILogger<FptAiTtsService> logger) : ITtsService
{
    private sealed record FptTtsResponse(
        [property: JsonPropertyName("async")] string? Async,
        [property: JsonPropertyName("error")] int? Error,
        [property: JsonPropertyName("message")] string? Message
    );

    public async Task<string?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        var opt = options.Value;
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            logger.LogWarning("FPT.AI ApiKey chưa được cấu hình.");
            return null;
        }

        // FPT.AI giới hạn tối đa 5000 ký tự mỗi yêu cầu
        if (trimmed.Length > 5000)
        {
            trimmed = trimmed[..5000];
        }

        var voice = string.IsNullOrWhiteSpace(opt.Voice) ? "banmai" : opt.Voice;
        var speed = string.IsNullOrWhiteSpace(opt.Speed) ? "0" : opt.Speed;

        var cacheKey = $"fpt_tts_{ComputeHash($"{voice}:{speed}:{trimmed}")}";
        if (cache.TryGetValue<string>(cacheKey, out var cachedUrl) && !string.IsNullOrWhiteSpace(cachedUrl))
        {
            return cachedUrl;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, opt.Endpoint);
            request.Headers.TryAddWithoutValidation("api_key", opt.ApiKey);
            request.Headers.TryAddWithoutValidation("api-key", opt.ApiKey);
            request.Headers.TryAddWithoutValidation("voice", voice);
            request.Headers.TryAddWithoutValidation("speed", speed);
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            request.Content = new StringContent(trimmed, Encoding.UTF8, "text/plain");

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("FPT.AI TTS trả về lỗi: HTTP {Status}, Body: {Body}", (int)response.StatusCode, body);
                return null;
            }

            var result = JsonSerializer.Deserialize<FptTtsResponse>(body);
            if (result?.Async == null || (result.Error.HasValue && result.Error != 0))
            {
                logger.LogWarning("FPT.AI TTS phản hồi không thành công: Error={Error}, Message={Message}", result?.Error, result?.Message);
                return null;
            }

            var audioUrl = result.Async;

            // FPT.AI xử lý file mp3 bất đồng bộ; thăm dò nhẹ tối đa 3 lần để file sẵn sàng trước khi trả về
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    await Task.Delay(600, ct);
                    using var checkReq = new HttpRequestMessage(HttpMethod.Head, audioUrl);
                    using var checkRes = await http.SendAsync(checkReq, ct);
                    if (checkRes.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch
                {
                    // Tiếp tục thử hoặc bỏ qua để trả url
                }
            }

            // Lưu đệm kết quả 7 ngày để không gọi lại FPT.AI với cùng một câu nói
            cache.Set(cacheKey, audioUrl, TimeSpan.FromDays(7));
            return audioUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Lỗi xảy ra khi gọi dịch vụ FPT.AI TTS.");
            return null;
        }
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
