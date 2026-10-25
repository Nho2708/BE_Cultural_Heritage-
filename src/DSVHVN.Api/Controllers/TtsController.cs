using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Speech;
using DSVHVN.Domain.Rules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DSVHVN.Api.Controllers;

public sealed record TtsRequest(string? Text);
public sealed record TtsResponse(string? AudioUrl);
public sealed record TtsPrefetchRequest(string[]? Texts);
public sealed record TtsPrefetchResponse(int Queued);

/// <summary>
/// Chuyển đổi văn bản thành giọng nói (Text-to-Speech). Nguồn giọng theo <c>Tts:Provider</c>: FPT.AI (mặc định), giọng tự host có bộ nhớ đệm hoặc tắt.
/// Cần đăng nhập (mọi vai trò, theo chính sách mặc định của hệ thống); không có giọng của máy chủ thì trả liên kết null và client dùng giọng đọc có sẵn của thiết bị.
/// </summary>
[Route("api/v1/tts")]
[EnableRateLimiting(PublicRateLimit.PolicyName)]
public sealed class TtsController(ITtsService tts, ISpeechPrefetcher prefetcher) : ApiControllerBase
{
    /// <summary>
    /// Chuyển văn bản thành giọng nói, trả về liên kết tệp MP3 (hoặc null nếu máy chủ chưa có giọng, đang tắt, hoặc chưa sinh kịp: khi đó dùng giọng của thiết bị).
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<TtsResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<TtsResponse>>> Synthesize([FromBody] TtsRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Text))
        {
            return OkEnvelope(new TtsResponse(null));
        }

        var audioUrl = await tts.SynthesizeAsync(request.Text, ct);
        return OkEnvelope(new TtsResponse(audioUrl));
    }

    /// <summary>
    /// Xếp trước các đoạn chữ vào hàng đợi tạo giọng đọc chạy nền (giọng tự host), để lúc em bấm nghe đã có sẵn. Tối đa 40 đoạn, mỗi đoạn không quá 1000 ký tự sau khi cắt hai đầu;
    /// đoạn rỗng bị bỏ. Trả 202 và số đoạn được nhận vào hàng đợi (đoạn trùng hoặc đã chờ sẵn không tính); nguồn giọng khác giọng tự host thì trả 202 với 0.
    /// </summary>
    [HttpPost("prefetch")]
    [ProducesResponseType<ApiResponse<TtsPrefetchResponse>>(StatusCodes.Status202Accepted)]
    public ActionResult<ApiResponse<TtsPrefetchResponse>> Prefetch([FromBody] TtsPrefetchRequest request)
    {
        var texts = request?.Texts ?? [];
        if (texts.Length > SpeechRules.PrefetchMaxTexts)
            throw AppException.Validation("texts", Messages.Invalid("Danh sách chữ", $"tối đa {SpeechRules.PrefetchMaxTexts} đoạn mỗi lần"));
        if (texts.Any(t => t is not null && t.Trim().Length > SpeechRules.TextMaxLength))
            throw AppException.Validation("texts", Messages.Invalid("Mỗi đoạn chữ", $"tối đa {SpeechRules.TextMaxLength} ký tự"));

        var queued = prefetcher.Enabled ? prefetcher.Enqueue(texts) : 0;
        return StatusCode(StatusCodes.Status202Accepted, ApiResponse.Ok(new TtsPrefetchResponse(queued), null, HttpContext.TraceIdentifier));
    }
}
