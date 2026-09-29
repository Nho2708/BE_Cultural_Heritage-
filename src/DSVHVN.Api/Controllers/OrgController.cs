using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Accounts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Organizations;
using DSVHVN.Application.Teachers;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Quản trị trường (ORG_ADMIN): tab Thông tin trường, màn hình Giáo viên của trường và biểu mẫu tài khoản giáo viên.
/// Trường luôn là trường của người gọi (claim đã đối chiếu CSDL); id giáo viên của trường khác trả 404.
/// </summary>
[Route("api/v1/org")]
[Authorize(Policy = Policies.OrgAdmin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class OrgController(MyOrganizationService myOrganization, TeacherService teachers) : ApiControllerBase
{
    /// <summary>Thông tin trường của mình, gói hiện tại, bộ đếm giáo viên.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<MyOrganizationDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<MyOrganizationDto>>> Get(CancellationToken ct)
        => OkEnvelope(await myOrganization.GetAsync(CurrentActor, ct));

    /// <summary>Sửa địa chỉ, số điện thoại, email liên hệ (tên trường chỉ ADMIN sửa).</summary>
    [HttpPut]
    [ProducesResponseType<ApiResponse<MyOrganizationDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<MyOrganizationDto>>> Update(UpdateOrganizationContactRequest request, CancellationToken ct)
        => OkEnvelope(await myOrganization.UpdateContactAsync(CurrentActor, request, ct), Messages.Success("Lưu thông tin trường").Text);

    /// <summary>Giáo viên của trường: tìm theo tên, tên đăng nhập, email; lọc trạng thái; kèm bộ đếm hạn mức.</summary>
    [HttpGet("teachers")]
    [ProducesResponseType<ApiResponse<TeacherListDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<TeacherListDto>>> ListTeachers(
        [FromQuery] string? keyword, [FromQuery] UserStatus? status, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => OkEnvelope(await teachers.ListAsync(CurrentActor, new TeacherListQuery(keyword, status, page, pageSize), ct));

    /// <summary>Tạo tài khoản giáo viên trong hạn mức max_teachers; gửi email đặt mật khẩu lần đầu.</summary>
    [HttpPost("teachers")]
    [ProducesResponseType<ApiResponse<AccountDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<AccountDto>>> CreateTeacher(NewAccountRequest request, CancellationToken ct)
    {
        var created = await teachers.CreateAsync(CurrentActor, request, ct);
        return CreatedEnvelope(created.Data, Messages.PasswordLinkSent(created.NotifiedEmail).Text);
    }

    [HttpGet("teachers/{id:long}")]
    [ProducesResponseType<ApiResponse<AccountDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AccountDto>>> GetTeacher(long id, CancellationToken ct)
        => OkEnvelope(await teachers.GetAsync(CurrentActor, id, ct));

    [HttpPost("teachers/{id:long}/lock")]
    [ProducesResponseType<ApiResponse<AccountDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<AccountDto>>> LockTeacher(long id, CancellationToken ct)
        => OkEnvelope(await teachers.LockAsync(CurrentActor, id, ct), Messages.Success("Khóa tài khoản").Text);

    [HttpPost("teachers/{id:long}/unlock")]
    [ProducesResponseType<ApiResponse<AccountDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<AccountDto>>> UnlockTeacher(long id, CancellationToken ct)
        => OkEnvelope(await teachers.UnlockAsync(CurrentActor, id, ct), Messages.Success("Mở khóa tài khoản").Text);

    /// <summary>Xóa mềm giáo viên: lớp, bài học, câu hỏi, quiz, kết quả giữ nguyên; email và tên đăng nhập dùng lại được.</summary>
    [HttpDelete("teachers/{id:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTeacher(long id, CancellationToken ct)
    {
        await teachers.DeleteAsync(CurrentActor, id, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa tài khoản giáo viên").Text);
    }
}
