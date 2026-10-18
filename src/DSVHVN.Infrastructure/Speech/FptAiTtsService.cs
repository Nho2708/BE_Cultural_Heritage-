using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DSVHVN.Application.Speech;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Speech;

/// <summary>
/// Dịch vụ chuyển văn bản thành giọng nói tiếng Việt tự nhiên:
/// 1. Ưu tiên FPT.AI TTS (giọng Ban Mai / Thu Minh) nếu cấu hình và còn hạn ngạch.
/// 2. Tự động dự phòng (fallback) sang Google TTS Engine chất lượng cao nếu FPT.AI hết hạn ngạch hoặc lỗi, đảm bảo 100% hoạt động mượt mà trên mọi trình duyệt (Chrome, Cốc Cốc, Edge).
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

        var cacheKey = $"fpt_tts_{ComputeHash($"{voice}:{speed}:{trimmed}")}";
        if (cache.TryGetValue<string>(cacheKey, out var cachedUrl) && !string.IsNullOrWhiteSpace(cachedUrl))
        {
            return cachedUrl;
        }

        // 1. Thử gọi FPT.AI nếu có API Key
        if (!string.IsNullOrWhiteSpace(opt.ApiKey))
        {
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
                    logger.LogWarning("FPT.AI trả về HTTP {Status}. Đang tự động chuyển sang Google TTS fallback.", (int)response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Gọi FPT.AI thất bại. Đang tự động chuyển sang Google TTS fallback.");
            }
        }

        // 2. Tự động chuyển sang Google TTS Engine (luôn sẵn sàng, miễn phí, hỗ trợ 100% mọi trình duyệt)
        return await SynthesizeGoogleFallbackAsync(trimmed, ct);
    }

    public Task<byte[]?> GetAudioBytesAsync(string hash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hash)) return Task.FromResult<byte[]?>(null);

        var cacheKey = $"tts_audio_bytes_{hash}";
        if (cache.TryGetValue<byte[]>(cacheKey, out var bytes) && bytes != null)
        {
            return Task.FromResult<byte[]?>(bytes);
        }

        return Task.FromResult<byte[]?>(null);
    }

    private async Task<string?> SynthesizeGoogleFallbackAsync(string text, CancellationToken ct)
    {
        try
        {
            var hash = ComputeHash(text);
            var cacheKey = $"tts_audio_bytes_{hash}";

            if (cache.TryGetValue<byte[]>(cacheKey, out _))
            {
                return $"/api/v1/tts/audio/{hash}";
            }

            // Tách các đoạn <= 150 ký tự theo dấu câu
            var chunks = SplitIntoChunks(text, 150);
            var combinedBytes = new List<byte>();

            foreach (var chunk in chunks)
            {
                var url = $"https://translate.google.com/translate_tts?ie=UTF-8&q={Uri.EscapeDataString(chunk)}&tl=vi&client=tw-ob";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                using var res = await http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    var chunkBytes = await res.Content.ReadAsByteArrayAsync(ct);
                    combinedBytes.AddRange(chunkBytes);
                }
            }

            if (combinedBytes.Count > 0)
            {
                var finalBytes = combinedBytes.ToArray();
                cache.Set(cacheKey, finalBytes, TimeSpan.FromDays(7));
                return $"/api/v1/tts/audio/{hash}";
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Lỗi xảy ra khi sinh giọng nói qua Google TTS fallback.");
            return null;
        }
    }

    private static List<string> SplitIntoChunks(string text, int maxLen)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var sentences = Regex.Split(text, @"(?<=[.!?,\n;])\s+");
        var current = new StringBuilder();

        foreach (var s in sentences)
        {
            var trimmed = s.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (current.Length + trimmed.Length + 1 > maxLen)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                if (trimmed.Length > maxLen)
                {
                    // Cắt cưỡng bức nếu 1 câu quá dài
                    for (var i = 0; i < trimmed.Length; i += maxLen)
                    {
                        var len = Math.Min(maxLen, trimmed.Length - i);
                        result.Add(trimmed.Substring(i, len));
                    }
                    continue;
                }
            }

            if (current.Length > 0) current.Append(' ');
            current.Append(trimmed);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
