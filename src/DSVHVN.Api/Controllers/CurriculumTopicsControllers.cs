using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Curriculum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Giáo viên: chủ đề chương trình môn học để chọn di sản khi tạo bài (hộp Tạo bài học, bộ lọc Thư viện bài mẫu). Chỉ đọc; chỉ chủ đề đang dùng và di sản đang xuất bản.
/// Quản trị trường, học sinh nhận 403.
/// </summary>
[Route("api/v1/teacher/curriculum-topics")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class TeacherCurriculumTopicsController(CurriculumTopicService topics) : ApiControllerBase
{
    /// <summary>Chủ đề đang dùng, lọc theo khối (<c>grade</c> 1-12, bỏ trống thì mọi khối), theo thứ tự chương trình; <c>heritageCount</c> chỉ đếm di sản đang xuất bản.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<TeacherCurriculumTopicListDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<TeacherCurriculumTopicListDto>>> List([FromQuery] int? grade, CancellationToken ct)
        => OkEnvelope(await topics.ListForTeacherAsync(CurrentActor, grade, ct));

    /// <summary>Di sản đang xuất bản của một chủ đề, dạng thẻ như danh sách công khai. 404 khi chủ đề ngừng dùng hoặc không có.</summary>
    [HttpGet("{id:int}/heritages")]
    [ProducesResponseType<ApiResponse<TeacherCurriculumTopicHeritagesDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<TeacherCurriculumTopicHeritagesDto>>> Heritages(int id, CancellationToken ct)
        => OkEnvelope(await topics.HeritagesForTeacherAsync(CurrentActor, id, ct));
}

/// <summary>
/// Màn hình Chủ đề chương trình của quản trị hệ thống: bảng chủ đề theo khối kèm số di sản; thêm, sửa, ngừng dùng, dùng lại; xem và thay danh sách di sản của chủ đề.
/// Không có thao tác xóa chủ đề. Mọi thay đổi ghi nhật ký thao tác. Quản trị trường nhận 403.
/// </summary>
[Route("api/v1/admin/curriculum-topics")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminCurriculumTopicsController(CurriculumTopicService topics) : ApiControllerBase
{
    /// <summary>Chủ đề theo khối (<c>grade</c>, bỏ trống thì mọi khối); chủ đề ngừng dùng chỉ hiện khi <c>includeInactive</c> = true. <c>heritageCount</c> đếm mọi di sản chưa xóa.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<AdminCurriculumTopicListDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<AdminCurriculumTopicListDto>>> List(
        [FromQuery] int? grade, [FromQuery] bool includeInactive, CancellationToken ct)
        => OkEnvelope(await topics.ListForAdminAsync(CurrentActor, grade, includeInactive, ct));

    /// <summary>Thêm chủ đề (đang dùng, chưa có di sản). Trùng (khối, môn, chủ đề lớn, nội dung) thì 409.</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<AdminCurriculumTopicDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<AdminCurriculumTopicDto>>> Create(CurriculumTopicRequest request, CancellationToken ct)
        => CreatedEnvelope(await topics.CreateAsync(CurrentActor, request, ct), Messages.Success("Thêm chủ đề chương trình").Text);

    /// <summary>Sửa chủ đề; không đổi trạng thái dùng và danh sách di sản. Trùng với chủ đề khác thì 409.</summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType<ApiResponse<AdminCurriculumTopicDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<AdminCurriculumTopicDto>>> Update(int id, CurriculumTopicRequest request, CancellationToken ct)
        => OkEnvelope(await topics.UpdateAsync(CurrentActor, id, request, ct), Messages.Success("Lưu chủ đề chương trình").Text);

    /// <summary>Ngừng dùng: giáo viên không thấy nữa, liên kết với di sản vẫn giữ.</summary>
    [HttpPost("{id:int}/deactivate")]
    [ProducesResponseType<ApiResponse<AdminCurriculumTopicDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AdminCurriculumTopicDto>>> Deactivate(int id, CancellationToken ct)
        => OkEnvelope(await topics.DeactivateAsync(CurrentActor, id, ct), Messages.Success("Ngừng dùng chủ đề chương trình").Text);

    /// <summary>Dùng lại chủ đề đã ngừng.</summary>
    [HttpPost("{id:int}/activate")]
    [ProducesResponseType<ApiResponse<AdminCurriculumTopicDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AdminCurriculumTopicDto>>> Activate(int id, CancellationToken ct)
        => OkEnvelope(await topics.ActivateAsync(CurrentActor, id, ct), Messages.Success("Dùng lại chủ đề chương trình").Text);

    /// <summary>Chủ đề kèm các di sản đang gắn (mọi di sản chưa xóa, kể cả bản nháp và đã ẩn).</summary>
    [HttpGet("{id:int}/heritages")]
    [ProducesResponseType<ApiResponse<AdminCurriculumTopicHeritagesDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AdminCurriculumTopicHeritagesDto>>> Heritages(int id, CancellationToken ct)
        => OkEnvelope(await topics.GetHeritagesForAdminAsync(CurrentActor, id, ct));

    /// <summary>Thay toàn bộ danh sách di sản của chủ đề: 0-200 di sản, không trùng, chưa xóa. Trả danh sách mới như thao tác xem.</summary>
    [HttpPut("{id:int}/heritages")]
    [ProducesResponseType<ApiResponse<AdminCurriculumTopicHeritagesDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AdminCurriculumTopicHeritagesDto>>> SetHeritages(int id, SetTopicHeritagesRequest request, CancellationToken ct)
        => OkEnvelope(await topics.SetHeritagesAsync(CurrentActor, id, request, ct), Messages.Success("Lưu danh sách di sản của chủ đề").Text);
}
