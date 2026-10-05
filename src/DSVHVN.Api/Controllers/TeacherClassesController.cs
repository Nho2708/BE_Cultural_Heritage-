using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Classes;
using DSVHVN.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Giáo viên: màn hình Lớp học. Giáo viên chỉ tạo, xem, sửa, xóa mềm lớp do chính mình phụ trách trong trường mình;
/// lớp của giáo viên khác hoặc trường khác trả 404. Quản trị trường và quản trị hệ thống nhận 403.
/// </summary>
[Route("api/v1/teacher/classes")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class TeacherClassesController(ClassService classes) : ApiControllerBase
{
    /// <summary>Danh sách lớp của giáo viên: tên, khối kèm cấp học, năm học, sĩ số, số bài học, số học sinh chưa đăng nhập lần nào.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<ClassPageDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ClassPageDto>>> List(
        [FromQuery] string? keyword, [FromQuery] string? schoolYear, [FromQuery] int? grade,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await classes.ListAsync(CurrentActor, new ClassListQuery(keyword, schoolYear, grade, page, pageSize), ct));

    /// <summary>Tạo lớp: tên (không trùng trong cùng giáo viên và năm học), khối 1-12 bắt buộc, năm học dạng YYYY-YYYY.</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<ClassDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<ClassDto>>> Create(ClassRequest request, CancellationToken ct)
        => CreatedEnvelope(await classes.CreateAsync(CurrentActor, request, ct), Messages.Success("Tạo lớp").Text);

    [HttpGet("{classId:long}")]
    [ProducesResponseType<ApiResponse<ClassDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ClassDto>>> Get(long classId, CancellationToken ct)
        => OkEnvelope(await classes.GetAsync(CurrentActor, classId, ct));

    /// <summary>
    /// Sửa tên, năm học, khối lớp. Sửa khối sang cấp khác khi lớp đã có bài học thì trả 422 kèm câu xác nhận
    /// (mã CLASS_LEVEL_CHANGE_NEEDS_CONFIRMATION); gửi lại với <c>confirmLevelChange</c> = true để đổi.
    /// </summary>
    [HttpPut("{classId:long}")]
    [ProducesResponseType<ApiResponse<ClassDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<ClassDto>>> Update(long classId, ClassRequest request, CancellationToken ct)
        => OkEnvelope(await classes.UpdateAsync(CurrentActor, classId, request, ct), Messages.Success("Lưu lớp").Text);

    /// <summary>
    /// Xóa mềm lớp (ẩn lớp). Bị chặn khi lớp còn bài kiểm tra đang mở; xóa thì vô hiệu mọi học sinh của lớp trong cùng giao dịch.
    /// </summary>
    [HttpDelete("{classId:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(long classId, CancellationToken ct)
    {
        await classes.DeleteAsync(CurrentActor, classId, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa lớp").Text);
    }
}
