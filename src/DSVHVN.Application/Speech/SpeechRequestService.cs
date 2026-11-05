using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Application.Speech;

/// <summary>
/// Yêu cầu giọng đọc của người dùng (nút nghe và tạo trước của giao diện), áp quy tắc <see cref="SpeechTextPolicy"/>:
/// <list type="number">
/// <item>Chuẩn hóa chữ (rỗng hoặc quá 1000 ký tự thì trả null).</item>
/// <item>Tiếng của chữ đã có trong bộ nhớ đệm của giọng hiện tại thì trả như trước, cho mọi người dùng.</item>
/// <item>Quy tắc đang bật mà chữ không thuộc bộ chữ của người gọi thì trả null (giao diện dùng giọng của thiết bị, không lỗi) hoặc không xếp vào hàng đợi;
/// ghi nhật ký mức Information với mã người dùng và vài ký tự đầu của băm, không bao giờ ghi chữ.</item>
/// <item>Còn lại tạo giọng như trước.</item>
/// </list>
/// Việc máy chủ tự tạo trước giọng (lúc xuất bản bài, lúc em bắt đầu lượt) gọi thẳng <see cref="ISpeechPrefetcher"/>, không đi qua đây.
/// </summary>
public sealed class SpeechRequestService(ITtsService tts, ISpeechPrefetcher prefetcher, SpeechTextPolicy policy, ILogger<SpeechRequestService> logger)
{
    public async Task<string?> SynthesizeAsync(Actor actor, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Quy tắc tắt: đúng như trước, chữ chuyển nguyên cho nguồn giọng.
        if (!policy.Enabled) return await tts.SynthesizeAsync(text, ct);

        var normalized = SpeechRules.Normalize(text);
        if (normalized is null) return null;
        if (await tts.FindCachedAsync(normalized, ct) is { } cached) return cached;
        if (!await policy.IsAllowedAsync(actor, normalized, ct))
        {
            logger.LogInformation("Không tạo giọng đọc {Hash} cho người dùng {UserId}: chữ không thuộc nội dung đã xuất bản người này được xem.",
                ShortHash(normalized), actor.UserId);
            return null;
        }
        return await tts.SynthesizeAsync(normalized, ct);
    }

    /// <summary>Xếp các chữ được phép vào hàng đợi tạo trước; trả số đoạn thật sự được nhận. Nguồn giọng không có hàng đợi thì trả 0.</summary>
    public async Task<int> PrefetchAsync(Actor actor, IReadOnlyList<string?> texts, CancellationToken ct)
    {
        if (!prefetcher.Enabled) return 0;
        if (!policy.Enabled) return prefetcher.Enqueue(texts);

        var allowed = new List<string>();
        var refused = 0;
        foreach (var normalized in texts.Select(SpeechRules.Normalize).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (await policy.IsAllowedAsync(actor, normalized, ct)) allowed.Add(normalized);
            else refused++;
        }
        if (refused > 0)
            logger.LogInformation("Bỏ {Refused} đoạn chữ tạo trước của người dùng {UserId}: không thuộc nội dung đã xuất bản người này được xem.", refused, actor.UserId);
        return prefetcher.Enqueue(allowed);
    }

    /// <summary>Tám ký tự đầu của băm theo giọng mặc định (trùng tên tệp tiếng khi máy chủ dùng giọng mặc định), để dò nhật ký mà không lộ chữ.</summary>
    private static string ShortHash(string normalized) => SpeechRules.Hash(normalized, SpeechRules.DefaultVoice)[..8];
}
