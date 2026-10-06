using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Dịch và tóm tắt di sản bằng AI, duyệt bản dịch (quản trị hệ thống): tab Bản dịch và tóm tắt của màn hình Biên tập di sản và màn hình Duyệt bản dịch và tóm tắt.
/// Mã ngôn ngữ trong đường dẫn: vi (chỉ tóm tắt), en, ko, zh-Hans (nhận cả zh), ja. Chỉ quản trị hệ thống.
/// </summary>
[Route("api/v1/admin")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminTranslationsController(HeritageTranslationService translations) : ApiControllerBase
{
    /// <summary>Số đếm của các tab (chờ duyệt, đã duyệt, bản dịch máy) và số bản cần dịch lại.</summary>
    [HttpGet("translations/counts")]
    [ProducesResponseType<ApiResponse<TranslationCountsDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<TranslationCountsDto>>> Counts(CancellationToken ct)
        => OkEnvelope(await translations.GetCountsAsync(ct));

    /// <summary>
    /// Danh sách bản dịch và tóm tắt theo tab (<c>PENDING</c> mặc định, <c>APPROVED</c>, <c>MACHINE</c>, <c>REJECTED</c>), lọc theo di sản, ngôn ngữ, loại (<c>TRANSLATION</c> hoặc
    /// <c>SUMMARY</c>), mới yêu cầu trước, phân trang.
    /// </summary>
    [HttpGet("translations")]
    [ProducesResponseType<ApiResponse<PagedResult<TranslationQueueItemDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<PagedResult<TranslationQueueItemDto>>>> List(
        [FromQuery] QueueTab? tab, [FromQuery] long? heritageId, [FromQuery] string? language, [FromQuery] TranslationKind? kind,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await translations.ListAsync(new TranslationQueueQuery(tab, heritageId, language, kind, page, pageSize), ct));

    /// <summary>Bảng bốn bản dịch và năm bản tóm tắt của một di sản với trạng thái từng ô, số bản cần dịch lại và có bấm được "Dịch và tóm tắt bằng AI" hay không.</summary>
    [HttpGet("heritages/{heritageId:long}/translations")]
    [ProducesResponseType<ApiResponse<TranslationOverviewDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<TranslationOverviewDto>>> Overview(long heritageId, CancellationToken ct)
        => OkEnvelope(await translations.GetOverviewAsync(heritageId, ct));

    /// <summary>
    /// Nút "Dịch và tóm tắt bằng AI": tóm tắt tiếng Việt rồi dịch nội dung và tóm tắt sang en, ko, zh-Hans, ja. Bản tiếng Việt (tóm tắt) và tiếng Anh vào chờ duyệt; bản Hàn, Trung, Nhật hiện ngay
    /// kèm nhãn "bản dịch máy". Dịch lại thay bản cũ. Không tính lượt AI của trường; ghi <c>ai_generations</c>. Mất tới vài phút khi nội dung dài: Dịch vụ AI chờ tối đa 90 giây mỗi lần, thử lại 2 lần.
    /// 422 khi di sản chưa có nội dung tiếng Việt hoặc đang bị ẩn; 409 khi di sản đang được dịch; 503 khi Dịch vụ AI lỗi (không lưu gì, bản cũ giữ nguyên).
    /// </summary>
    [HttpPost("heritages/{heritageId:long}/translations/generate")]
    [ProducesResponseType<ApiResponse<TranslationOverviewDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<TranslationOverviewDto>>> Generate(long heritageId, CancellationToken ct)
        => OkEnvelope(await translations.GenerateAsync(CurrentActor, heritageId, HttpContext.TraceIdentifier, ct), TranslationMessages.Generated.Text);

    /// <summary>Hai cột để duyệt: bản gốc tiếng Việt và bản dịch (en, ko, zh-Hans, ja) của mọi phần chữ (mô tả ngắn, nội dung, giai đoạn, sự kiện, chú thích ảnh).</summary>
    [HttpGet("heritages/{heritageId:long}/translations/{language}/translation")]
    [ProducesResponseType<ApiResponse<TranslationDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<TranslationDetailDto>>> GetTranslation(long heritageId, string language, CancellationToken ct)
        => OkEnvelope(await translations.GetTranslationAsync(heritageId, language, ct));

    /// <summary>Sửa chữ của bản dịch tiếng Anh tại chỗ; bản đã duyệt hoặc bị từ chối được sửa thì quay về chờ duyệt. Bản Hàn, Trung, Nhật là bản dịch máy nên 422.</summary>
    [HttpPut("heritages/{heritageId:long}/translations/{language}/translation")]
    [ProducesResponseType<ApiResponse<TranslationDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<TranslationDetailDto>>> UpdateTranslation(
        long heritageId, string language, UpdateTranslationRequest request, CancellationToken ct)
        => OkEnvelope(await translations.UpdateTranslationAsync(heritageId, language, request, ct), Messages.Success("Lưu bản dịch").Text);

    /// <summary>Tóm tắt của một ngôn ngữ (vi, en, ko, zh-Hans, ja): bản ngắn để nghe, bản chi tiết để đọc, kèm nội dung tiếng Việt của di sản và tóm tắt tiếng Việt để đối chiếu.</summary>
    [HttpGet("heritages/{heritageId:long}/translations/{language}/summary")]
    [ProducesResponseType<ApiResponse<SummaryDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<SummaryDetailDto>>> GetSummary(long heritageId, string language, CancellationToken ct)
        => OkEnvelope(await translations.GetSummaryAsync(heritageId, language, ct));

    /// <summary>Sửa chữ của tóm tắt tiếng Việt hoặc tiếng Anh; bản đã duyệt hoặc bị từ chối được sửa thì quay về chờ duyệt.</summary>
    [HttpPut("heritages/{heritageId:long}/translations/{language}/summary")]
    [ProducesResponseType<ApiResponse<SummaryDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<SummaryDetailDto>>> UpdateSummary(
        long heritageId, string language, UpdateSummaryRequest request, CancellationToken ct)
        => OkEnvelope(await translations.UpdateSummaryAsync(heritageId, language, request, ct), Messages.Success("Lưu tóm tắt").Text);

    /// <summary>
    /// Duyệt bản dịch (<c>TRANSLATION</c>, chỉ en), tóm tắt (<c>SUMMARY</c>, vi hoặc en) hoặc cả hai (<c>BOTH</c>) của một ngôn ngữ: hiện công khai, ghi người và thời điểm duyệt, ghi nhật ký
    /// TRANSLATION_APPROVED. Duyệt bản dịch tiếng Anh thì bản Hàn, Trung, Nhật được dịch lại từ bản tiếng Anh đã duyệt (lỗi ở bước này chỉ trả cảnh báo). Hàn, Trung, Nhật không có bước duyệt (422).
    /// </summary>
    [HttpPost("heritages/{heritageId:long}/translations/{language}/approve")]
    [ProducesResponseType<ApiResponse<ReviewResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<ReviewResultDto>>> Approve(
        long heritageId, string language, ApproveTranslationRequest request, CancellationToken ct)
    {
        var result = await translations.ApproveAsync(CurrentActor, heritageId, language, request, HttpContext.TraceIdentifier, ct);
        return OkEnvelope(result, TranslationMessages.Approved(Label(request.Kind, language)).Text);
    }

    /// <summary>
    /// Từ chối (yêu cầu dịch lại) bản dịch, tóm tắt hoặc cả hai của một ngôn ngữ: ngừng hiện, ghi nhật ký TRANSLATION_REJECTED kèm lý do (không bắt buộc, tối đa 500 ký tự).
    /// Từ chối được bản đang chờ duyệt và cả bản đã duyệt (gỡ một bản đã hiện mà còn sai khỏi trang công khai); bản đã bị từ chối thì 422.
    /// </summary>
    [HttpPost("heritages/{heritageId:long}/translations/{language}/reject")]
    [ProducesResponseType<ApiResponse<ReviewResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<ReviewResultDto>>> Reject(
        long heritageId, string language, RejectTranslationRequest request, CancellationToken ct)
    {
        var result = await translations.RejectAsync(CurrentActor, heritageId, language, request, HttpContext.TraceIdentifier, ct);
        return OkEnvelope(result, TranslationMessages.Rejected(Label(request.Kind, language)).Text);
    }

    private static string Label(ReviewScope? scope, string language) =>
        HeritageTranslationService.ReviewLabel(scope ?? ReviewScope.BOTH, Languages.TryParse(language) ?? LanguageCode.en);
}
