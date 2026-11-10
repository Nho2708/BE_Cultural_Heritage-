using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Giáo viên: thư viện bài mẫu dùng chung mọi trường (màn hình Thư viện bài mẫu). Chỉ hiện bài đã được quản trị hệ thống duyệt, đang Đã xuất bản và còn di sản đang xuất bản;
/// người soạn hiện họ tên và tên trường, không liên hệ. Giáo viên xem trước rồi dùng cho lớp của mình (tạo bài Nháp mới bằng cơ chế nhân bản). Quản trị trường, học sinh nhận 403.
/// </summary>
[Route("api/v1/teacher/lesson-library")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class TeacherLessonLibraryController(LessonSampleService samples) : ApiControllerBase
{
    /// <summary>
    /// Thư viện bài mẫu: lọc theo cấp (<c>level</c> 1-3), khối (<c>grade</c> 1-12), di sản, từ khóa trong tiêu đề; <c>sort</c> = NEWEST (mặc định, bài mới duyệt trước)
    /// hoặc POPULAR (dùng nhiều trước). Mỗi dòng có trích tóm tắt, số phần, số bài kiểm tra, người soạn, trường, số lượt dùng và nhãn "bài của tôi".
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedResult<LessonLibraryItemDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<PagedResult<LessonLibraryItemDto>>>> List(
        [FromQuery] int? level, [FromQuery] int? grade, [FromQuery] long? heritageId, [FromQuery] string? keyword, [FromQuery] LessonLibrarySort? sort,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await samples.ListLibraryAsync(CurrentActor, new LessonLibraryQuery(level, grade, heritageId, keyword, sort, page, pageSize), ct));

    /// <summary>
    /// Xem trước một bài mẫu: thẻ bài, bài như học sinh thấy (không mã bài học, không tên lớp) và các bài kiểm tra sẽ chép nếu chọn chép kèm. 404 khi bài không ở thư viện.
    /// </summary>
    [HttpGet("{lessonId:long}")]
    [ProducesResponseType<ApiResponse<LessonLibraryDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<LessonLibraryDetailDto>>> Get(long lessonId, CancellationToken ct)
        => OkEnvelope(await samples.GetLibraryAsync(CurrentActor, lessonId, ct));

    /// <summary>
    /// Dùng bài mẫu cho một lớp của chính giáo viên: tạo bài Nháp mới bằng đúng cơ chế nhân bản sang lớp khác (cần gói hiệu lực, không tốn lượt AI). <c>includeQuizzes</c> = true chép kèm
    /// các bài kiểm tra thành bản nháp (bỏ câu đã ẩn và câu không hợp cấp lớp đích). Bài gốc không đổi; ghi nhật ký thao tác.
    /// </summary>
    [HttpPost("{lessonId:long}/use")]
    [ProducesResponseType<ApiResponse<DuplicateLessonResultDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<DuplicateLessonResultDto>>> Use(long lessonId, DuplicateLessonRequest request, CancellationToken ct)
    {
        var result = await samples.UseAsync(CurrentActor, lessonId, request, ct);
        return CreatedEnvelope(result, result.Message);
    }
}
