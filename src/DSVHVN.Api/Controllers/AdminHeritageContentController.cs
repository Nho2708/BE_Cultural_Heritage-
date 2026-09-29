using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Phần con của di sản cho màn hình Biên tập di sản và Biểu mẫu giai đoạn và sự kiện: timeline (giai đoạn, sự kiện),
/// thư viện ảnh và nguồn tham chiếu. Thêm phần con đi qua đường dẫn của cha; sửa, xóa đi thẳng theo id của phần con.
/// </summary>
[Route("api/v1/admin")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
public sealed class AdminHeritageContentController(
    TimelineService timelines,
    HeritageMediaService media,
    HeritageReferenceService references) : ApiControllerBase
{
    // ---- Giai đoạn ----

    /// <summary>Thêm giai đoạn vào di sản (xếp cuối nếu không gửi sortOrder). Năm âm là trước Công nguyên.</summary>
    [HttpPost("heritages/{heritageId:long}/timelines")]
    [ProducesResponseType<ApiResponse<TimelineDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<TimelineDto>>> CreateTimeline(long heritageId, TimelineRequest request, CancellationToken ct)
        => CreatedEnvelope(await timelines.CreateTimelineAsync(heritageId, request, ct), Messages.Success("Thêm giai đoạn").Text);

    /// <summary>Đặt thứ tự các giai đoạn: body <c>{ "ids": [...] }</c> là toàn bộ giai đoạn của di sản theo thứ tự mới; trả lại danh sách đã xếp.</summary>
    [HttpPut("heritages/{heritageId:long}/timelines/order")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<TimelineDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TimelineDto>>>> ReorderTimelines(
        long heritageId, OrderRequest request, CancellationToken ct)
        => OkEnvelope(await timelines.ReorderTimelinesAsync(heritageId, request, ct), Messages.Success("Sắp xếp giai đoạn").Text);

    [HttpPut("timelines/{timelineId:long}")]
    [ProducesResponseType<ApiResponse<TimelineDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<TimelineDto>>> UpdateTimeline(long timelineId, TimelineRequest request, CancellationToken ct)
        => OkEnvelope(await timelines.UpdateTimelineAsync(timelineId, request, ct), Messages.Success("Lưu giai đoạn").Text);

    /// <summary>Xóa giai đoạn cùng các sự kiện và ảnh của sự kiện trong giai đoạn.</summary>
    [HttpDelete("timelines/{timelineId:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTimeline(long timelineId, CancellationToken ct)
    {
        await timelines.DeleteTimelineAsync(timelineId, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa giai đoạn").Text);
    }

    // ---- Sự kiện ----

    /// <summary>Thêm sự kiện vào giai đoạn: tên và năm bắt đầu bắt buộc, tháng, ngày và thời điểm kết thúc tùy chọn.</summary>
    [HttpPost("timelines/{timelineId:long}/events")]
    [ProducesResponseType<ApiResponse<EventDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<EventDto>>> CreateEvent(long timelineId, EventRequest request, CancellationToken ct)
        => CreatedEnvelope(await timelines.CreateEventAsync(timelineId, request, ct), Messages.Success("Thêm sự kiện").Text);

    /// <summary>Đặt thứ tự các sự kiện của giai đoạn (có tác dụng khi cùng năm, tháng, ngày); trả lại danh sách sự kiện.</summary>
    [HttpPut("timelines/{timelineId:long}/events/order")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<EventDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<EventDto>>>> ReorderEvents(
        long timelineId, OrderRequest request, CancellationToken ct)
        => OkEnvelope(await timelines.ReorderEventsAsync(timelineId, request, ct), Messages.Success("Sắp xếp sự kiện").Text);

    [HttpPut("events/{eventId:long}")]
    [ProducesResponseType<ApiResponse<EventDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<EventDto>>> UpdateEvent(long eventId, EventRequest request, CancellationToken ct)
        => OkEnvelope(await timelines.UpdateEventAsync(eventId, request, ct), Messages.Success("Lưu sự kiện").Text);

    /// <summary>Xóa sự kiện cùng ảnh minh họa của sự kiện.</summary>
    [HttpDelete("events/{eventId:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteEvent(long eventId, CancellationToken ct)
    {
        await timelines.DeleteEventAsync(eventId, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa sự kiện").Text);
    }

    // ---- Ảnh ----

    /// <summary>
    /// Thêm ảnh, video, audio vào thư viện của di sản (đường dẫn, không tải tệp). Ghi công bắt buộc: author, license
    /// (CC0, CC BY, CC BY-SA, phạm vi công cộng hoặc ảnh nhóm tự chụp), source. <c>setAsThumbnail</c> true để chọn luôn làm ảnh đại diện.
    /// </summary>
    [HttpPost("heritages/{heritageId:long}/media")]
    [ProducesResponseType<ApiResponse<MediaDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<MediaDto>>> AddHeritageMedia(long heritageId, MediaRequest request, CancellationToken ct)
        => CreatedEnvelope(await media.AddToHeritageAsync(heritageId, request, ct), Messages.Success("Thêm ảnh").Text);

    /// <summary>Đặt thứ tự thư viện ảnh của di sản (không gồm ảnh của sự kiện).</summary>
    [HttpPut("heritages/{heritageId:long}/media/order")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<MediaDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MediaDto>>>> ReorderHeritageMedia(
        long heritageId, OrderRequest request, CancellationToken ct)
        => OkEnvelope(await media.ReorderHeritageMediaAsync(heritageId, request, ct), Messages.Success("Sắp xếp ảnh").Text);

    /// <summary>Thêm ảnh minh họa cho sự kiện (cùng quy tắc ghi công; không chọn được làm ảnh đại diện).</summary>
    [HttpPost("events/{eventId:long}/media")]
    [ProducesResponseType<ApiResponse<MediaDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<MediaDto>>> AddEventMedia(long eventId, MediaRequest request, CancellationToken ct)
        => CreatedEnvelope(await media.AddToEventAsync(eventId, request, ct), Messages.Success("Thêm ảnh").Text);

    /// <summary>Đặt thứ tự ảnh minh họa của sự kiện.</summary>
    [HttpPut("events/{eventId:long}/media/order")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<MediaDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MediaDto>>>> ReorderEventMedia(
        long eventId, OrderRequest request, CancellationToken ct)
        => OkEnvelope(await media.ReorderEventMediaAsync(eventId, request, ct), Messages.Success("Sắp xếp ảnh").Text);

    /// <summary>Sửa ảnh (ảnh của di sản hoặc của sự kiện). <c>setAsThumbnail</c> true chọn làm ảnh đại diện, false bỏ chọn.</summary>
    [HttpPut("media/{mediaId:long}")]
    [ProducesResponseType<ApiResponse<MediaDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<MediaDto>>> UpdateMedia(long mediaId, MediaRequest request, CancellationToken ct)
        => OkEnvelope(await media.UpdateAsync(mediaId, request, ct), Messages.Success("Lưu ảnh").Text);

    /// <summary>Xóa ảnh; ảnh đại diện của di sản đang xuất bản thì 422 (chọn ảnh đại diện khác trước).</summary>
    [HttpDelete("media/{mediaId:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteMedia(long mediaId, CancellationToken ct)
    {
        await media.DeleteAsync(mediaId, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa ảnh").Text);
    }

    // ---- Nguồn tham chiếu ----

    /// <summary>Thêm nguồn tham chiếu: tiêu đề, loại nguồn GOV_DOC | WEBSITE | BOOK, đường dẫn (bắt buộc với WEBSITE).</summary>
    [HttpPost("heritages/{heritageId:long}/references")]
    [ProducesResponseType<ApiResponse<ReferenceDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<ReferenceDto>>> AddReference(long heritageId, ReferenceRequest request, CancellationToken ct)
        => CreatedEnvelope(await references.AddAsync(heritageId, request, ct), Messages.Success("Thêm nguồn tham chiếu").Text);

    [HttpPut("heritage-references/{referenceId:long}")]
    [ProducesResponseType<ApiResponse<ReferenceDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<ReferenceDto>>> UpdateReference(long referenceId, ReferenceRequest request, CancellationToken ct)
        => OkEnvelope(await references.UpdateAsync(referenceId, request, ct), Messages.Success("Lưu nguồn tham chiếu").Text);

    /// <summary>Xóa nguồn; nguồn cuối cùng của di sản đang xuất bản thì 422.</summary>
    [HttpDelete("heritage-references/{referenceId:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteReference(long referenceId, CancellationToken ct)
    {
        await references.DeleteAsync(referenceId, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa nguồn tham chiếu").Text);
    }
}
