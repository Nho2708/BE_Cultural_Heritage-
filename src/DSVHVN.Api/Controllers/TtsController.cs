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
/// Chuyển đổi văn bản thành giọng nói (Text-to-Speech). Nguồn giọng theo <c>Tts:Provider</c>: giọng tự host có bộ nhớ đệm (mặc định), FPT.AI hoặc tắt.
/// Cần đăng nhập (mọi vai trò, theo chính sách mặc định của hệ thống). Tiếng đã có sẵn được trả cho mọi người; giọng mới chỉ được tạo cho chữ thuộc nội dung đã xuất bản
/// mà người gọi được xem và các câu cố định của khu học cấp 1 (<c>Tts:OnlyPublishedContent</c>, mặc định bật; quy tắc ở <see cref="SpeechTextPolicy"/>).
/// Chữ ngoài phạm vi đó, hay khi máy chủ không có giọng, thì trả liên kết null và client dùng giọng đọc có sẵn của thiết bị, không bao giờ báo lỗi.
/// </summary>
[Route("api/v1/tts")]
[EnableRateLimiting(PublicRateLimit.PolicyName)]
public sealed class TtsController(SpeechRequestService speech) : ApiControllerBase
{
    /// <summary>
    /// Chuyển văn bản thành giọng nói, trả về liên kết tệp MP3 (hoặc null nếu máy chủ chưa có giọng, đang tắt, chưa sinh kịp, hoặc chữ chưa có tiếng mà không thuộc
    /// nội dung người gọi được xem: khi đó dùng giọng của thiết bị).
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<TtsResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<TtsResponse>>> Synthesize([FromBody] TtsRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Text))
        {
            return OkEnvelope(new TtsResponse(null));
        }

        var audioUrl = await speech.SynthesizeAsync(CurrentActor, request.Text, ct);
        return OkEnvelope(new TtsResponse(audioUrl));
    }

    /// <summary>
    /// Xếp trước các đoạn chữ vào hàng đợi tạo giọng đọc chạy nền (giọng tự host), để lúc em bấm nghe đã có sẵn. Tối đa 40 đoạn, mỗi đoạn không quá 1000 ký tự sau khi cắt hai đầu;
    /// đoạn rỗng bị bỏ, đoạn không thuộc nội dung người gọi được xem cũng bị bỏ (không lỗi). Trả 202 và số đoạn được nhận vào hàng đợi (đoạn trùng hoặc đã chờ sẵn
    /// không tính); nguồn giọng khác giọng tự host thì trả 202 với 0.
    /// </summary>
    [HttpPost("prefetch")]
    [ProducesResponseType<ApiResponse<TtsPrefetchResponse>>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<ApiResponse<TtsPrefetchResponse>>> Prefetch([FromBody] TtsPrefetchRequest request, CancellationToken ct)
    {
        var texts = request?.Texts ?? [];
        if (texts.Length > SpeechRules.PrefetchMaxTexts)
            throw AppException.Validation("texts", Messages.Invalid("Danh sách chữ", $"tối đa {SpeechRules.PrefetchMaxTexts} đoạn mỗi lần"));
        if (texts.Any(t => t is not null && t.Trim().Length > SpeechRules.TextMaxLength))
            throw AppException.Validation("texts", Messages.Invalid("Mỗi đoạn chữ", $"tối đa {SpeechRules.TextMaxLength} ký tự"));

        var queued = await speech.PrefetchAsync(CurrentActor, texts, ct);
        return StatusCode(StatusCodes.Status202Accepted, ApiResponse.Ok(new TtsPrefetchResponse(queued), null, HttpContext.TraceIdentifier));
    }
}
