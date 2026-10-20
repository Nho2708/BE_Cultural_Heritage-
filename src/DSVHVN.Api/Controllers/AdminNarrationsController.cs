using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Narrations;
using DSVHVN.Domain.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>Biểu mẫu tải thuyết minh lên (multipart/form-data): tệp <c>audio</c> cùng băm, giọng, thời lượng và danh sách câu.</summary>
public sealed class UploadNarrationForm
{
    /// <summary>Tệp MP3 (<c>audio/mpeg</c>), tối đa 10 MB, kiểm bằng nội dung.</summary>
    public IFormFile? Audio { get; set; }

    /// <summary>Băm 64 ký tự hex của bản tóm tắt ngắn, lấy từ danh sách còn thiếu và gửi lại nguyên văn.</summary>
    public string? TextHash { get; set; }

    /// <summary>Mã giọng đã dùng, 1..100 ký tự (ví dụ <c>vieneu-mai-anh</c>, <c>melo-en-us</c>).</summary>
    public string? Voice { get; set; }

    /// <summary>Thời lượng tệp, 1..1.800.000 mili giây.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Chuỗi JSON mảng 1..300 câu <c>[{ "text", "startMs", "endMs" }]</c>.</summary>
    public string? Sentences { get; set; }

    public UploadNarrationRequest ToRequest() => new(TextHash, Voice, DurationMs, Sentences);
}

/// <summary>
/// Thuyết minh tạo sẵn của tóm tắt di sản (quản trị hệ thống; công cụ tạo giọng trên máy phát triển đăng nhập bằng tài khoản quản trị rồi gọi):
/// danh sách tóm tắt đang hiện công khai mà chưa có thuyết minh khớp băm, và tải tệp MP3 kèm mốc câu lên. Máy chủ không tạo giọng.
/// Mã ngôn ngữ nhận như các API bản dịch (vi, en, ko, zh-Hans hoặc zh, ja); mã trả ra là mã ngắn (zh).
/// </summary>
[Route("api/v1/admin")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminNarrationsController(NarrationService narrations) : ApiControllerBase
{
    // Giới hạn thân yêu cầu rộng hơn tệp một chút để chừa chỗ cho băm, giọng và danh sách câu; tệp quá cỡ bị UploadSizeLimit chặn bằng câu rõ ràng.
    private const long NarrationRequestLimit = StoredFileRules.MaxNarrationBytes + UploadSizeLimitAttribute.FormHeadroom;

    /// <summary>
    /// Tóm tắt ngắn đang hiện công khai ở một ngôn ngữ (đúng quy tắc chọn tóm tắt của <c>GET /public/heritages/{slug}/summary</c>) mà chưa có thuyết minh, hoặc thuyết minh
    /// có băm khác chữ hiện tại: <c>items[]</c> (<c>heritageId</c>, <c>slug</c>, <c>name</c>, <c>language</c>, <c>shortText</c>, <c>textHash</c>) tối đa <c>limit</c> mục (1..1000, mặc định 500) và <c>total</c>.
    /// </summary>
    [HttpGet("narrations/missing")]
    [ProducesResponseType<ApiResponse<MissingNarrationsDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<MissingNarrationsDto>>> Missing([FromQuery] string? language, [FromQuery] int? limit, CancellationToken ct)
        => OkEnvelope(await narrations.ListMissingAsync(language, limit, ct));

    /// <summary>
    /// Tải thuyết minh của một (di sản, ngôn ngữ) lên: tệp MP3 (≤ 10 MB, kiểm bằng nội dung), <c>textHash</c> phải bằng băm của bản tóm tắt ngắn hiện tại (khác thì 409
    /// <c>NARRATION_STALE</c>: tóm tắt đã đổi, tạo lại), <c>voice</c>, <c>durationMs</c>, <c>sentences</c> (JSON). Tải lên lần nữa thì thay bản cũ. 404 khi không có di sản
    /// hoặc không có tóm tắt ngôn ngữ đó. Ghi nhật ký thao tác.
    /// </summary>
    [HttpPut("heritages/{heritageId:long}/narrations/{language}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(NarrationRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = NarrationRequestLimit)]
    [UploadSizeLimit("Tệp âm thanh", StoredFileRules.MaxNarrationBytes, field: "audio")]
    [ProducesResponseType<ApiResponse<HeritageNarrationDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<ApiResponse<HeritageNarrationDto>>> Upload(long heritageId, string language, [FromForm] UploadNarrationForm form, CancellationToken ct)
    {
        var saved = await narrations.UploadAsync(CurrentActor, heritageId, language, form.ToRequest(),
            await FormFiles.ReadAsync(form.Audio, StoredFileRules.MaxNarrationBytes, ct), ct);
        return OkEnvelope(saved, NarrationMessages.Saved(Languages.InlineLabel(Languages.TryParse(language)!.Value)).Text);
    }
}
