using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Quản trị hệ thống: duyệt thư viện bài mẫu (màn hình Thư viện bài mẫu của quản trị: ba thẻ Chờ duyệt, Trong thư viện, Đã từ chối). Xem bài chỉ đọc, duyệt, từ chối hoặc gỡ (bắt buộc lý do).
/// Mọi thao tác duyệt ghi nhật ký thao tác. Giáo viên, quản trị trường nhận 403.
/// </summary>
[Route("api/v1/admin/lesson-library")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminLessonLibraryController(LessonSampleService samples) : ApiControllerBase
{
    /// <summary>Bài đề xuất theo trạng thái <c>status</c> = PENDING (mặc định, cũ nhất trước), APPROVED hoặc REJECTED; lọc theo cấp và từ khóa trong tiêu đề.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedResult<AdminLessonLibraryItemDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<PagedResult<AdminLessonLibraryItemDto>>>> List(
        [FromQuery] LessonSampleStatus? status, [FromQuery] int? level, [FromQuery] string? keyword, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => OkEnvelope(await samples.ListAdminAsync(CurrentActor, new AdminLessonLibraryQuery(status, level, keyword, page, pageSize), ct));

    /// <summary>Xem bài để duyệt (chỉ đọc): dòng duyệt, bài như học sinh thấy, bài kiểm tra kèm theo. 404 khi bài không ở thư viện.</summary>
    [HttpGet("{lessonId:long}")]
    [ProducesResponseType<ApiResponse<AdminLessonLibraryDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AdminLessonLibraryDetailDto>>> Get(long lessonId, CancellationToken ct)
        => OkEnvelope(await samples.GetAdminAsync(CurrentActor, lessonId, ct));

    /// <summary>Duyệt đề xuất đang chờ: bài vẫn phải Đã xuất bản và đủ điều kiện xuất bản. 422 BLOCKED khi không ở trạng thái chờ duyệt; 422 NOT_READY khi chưa đủ điều kiện.</summary>
    [HttpPost("{lessonId:long}/approve")]
    [ProducesResponseType<ApiResponse<AdminLessonLibraryResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<AdminLessonLibraryResultDto>>> Approve(long lessonId, CancellationToken ct)
    {
        var result = await samples.ApproveAsync(CurrentActor, lessonId, ct);
        return OkEnvelope(result, result.Message);
    }

    /// <summary>Từ chối đề xuất đang chờ hoặc gỡ bài đang ở trong thư viện, kèm lý do bắt buộc 1-500 ký tự. 422 BLOCKED khi bài không ở trạng thái chờ duyệt hay trong thư viện.</summary>
    [HttpPost("{lessonId:long}/reject")]
    [ProducesResponseType<ApiResponse<AdminLessonLibraryResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<AdminLessonLibraryResultDto>>> Reject(long lessonId, RejectLessonSampleRequest request, CancellationToken ct)
    {
        var result = await samples.RejectAsync(CurrentActor, lessonId, request, ct);
        return OkEnvelope(result, result.Message);
    }
}
