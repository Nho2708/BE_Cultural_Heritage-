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
/// Chuyển văn bản thành giọng nói tiếng Việt qua FPT.AI TTS (giọng Ban Mai / Thu Minh). Chưa cấu hình khóa <c>FptAi:ApiKey</c> thì
/// trả null mà không gọi ra ngoài; khi đó (hoặc khi FPT.AI hết hạn ngạch, lỗi) trình duyệt của người dùng tự dùng giọng đọc có sẵn.
/// Chỉ lưu tạm đường dẫn tệp âm thanh FPT.AI trả về (chuỗi ngắn), không lưu nội dung âm thanh.
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

        // FPT.AI giới hạn tối đa 5000 ký tự mỗi yêu cầu
        if (trimmed.Length > 5000)
        {
            trimmed = trimmed[..5000];
        }

        var opt = options.Value;
        var voice = string.IsNullOrWhiteSpace(opt.Voice) ? "banmai" : opt.Voice;
        var speed = string.IsNullOrWhiteSpace(opt.Speed) ? "0" : opt.Speed;

        // Chưa có khóa FPT.AI thì không gọi ra ngoài: trả null để client dùng giọng đọc của trình duyệt.
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;

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

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<FptTtsResponse>(body);
                if (result?.Async != null && (!result.Error.HasValue || result.Error == 0))
                {
                    var audioUrl = result.Async;

                    // Thăm dò nhẹ để tệp MP3 sẵn sàng
                    for (var i = 0; i < 3; i++)
                    {
                        try
                        {
                            await Task.Delay(600, ct);
                            using var checkReq = new HttpRequestMessage(HttpMethod.Head, audioUrl);
                            using var checkRes = await http.SendAsync(checkReq, ct);
                            if (checkRes.IsSuccessStatusCode) break;
                        }
                        catch
                        {
                            // Bỏ qua nếu đang xử lý
                        }
                    }

                    cache.Set(cacheKey, audioUrl, TimeSpan.FromDays(7));
                    return audioUrl;
                }
            }
            else
            {
                logger.LogWarning("FPT.AI trả về HTTP {Status}; trình duyệt sẽ dùng giọng đọc có sẵn.", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Gọi FPT.AI thất bại; trình duyệt sẽ dùng giọng đọc có sẵn.");
        }

        return null;
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
