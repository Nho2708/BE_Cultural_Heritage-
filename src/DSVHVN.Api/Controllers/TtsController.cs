using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Speech;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DSVHVN.Api.Controllers;

public sealed record TtsRequest(string? Text);
public sealed record TtsResponse(string? AudioUrl);

/// <summary>
/// Chuyển đổi văn bản thành giọng nói (Text-to-Speech) qua cloud service (FPT.AI).
/// Cho phép gọi công khai (học sinh, giáo viên, khách tham quan nghe bài học và mô tả di sản).
/// </summary>
[Route("api/v1/tts")]
[AllowAnonymous]
[EnableRateLimiting(PublicRateLimit.PolicyName)]
public sealed class TtsController(ITtsService tts) : ApiControllerBase
{
    /// <summary>
    /// Chuyển văn bản thành giọng nói, trả về liên kết tệp MP3 (hoặc null nếu dịch vụ không khả dụng).
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
}
