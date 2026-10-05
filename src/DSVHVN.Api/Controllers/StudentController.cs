using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.StudentAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Đăng nhập của học sinh bằng mã học sinh, dùng chung cho khu học của web học tập và chế độ Học sinh của app mobile.
/// Không có tự đăng ký và không có quên mật khẩu qua email: học sinh quên mật khẩu thì nhờ giáo viên đặt lại.
/// </summary>
[Route("api/v1/student/auth")]
[AllowAnonymous]
public sealed class StudentAuthController(StudentAuthService studentAuth) : ApiControllerBase
{
    /// <summary>
    /// Mã học sinh + mật khẩu. Nhận access token 8 giờ kèm thông tin học sinh và cấp học của lớp. Sai thông tin luôn trả cùng một
    /// thông điệp; sai 5 lần liên tiếp trong 15 phút thì tạm khóa đăng nhập 15 phút; <c>mustChangePassword</c> bật thì chỉ được đổi mật khẩu.
    /// </summary>
    [HttpPost("login")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentSessionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<StudentSessionDto>>> Login(StudentLoginRequest request, CancellationToken ct)
        => OkEnvelope(await studentAuth.LoginAsync(request, ClientIp, ct), Messages.Success("Đăng nhập").Text);
}

/// <summary>
/// Khu học của học sinh: thông tin của em, đổi mật khẩu, lớp của em. Chỉ vai trò học sinh; học sinh đang bị buộc đổi mật khẩu
/// chỉ gọi được thông tin của em và đổi mật khẩu, các endpoint khác trả 403 yêu cầu đặt mật khẩu mới.
/// </summary>
[Route("api/v1/student")]
[Authorize(Policy = Policies.Student)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class StudentController(StudentAuthService studentAuth) : ApiControllerBase
{
    /// <summary>Thông tin của em, lớp và cấp học (giao diện lấy cấp học mới nhất ở đây sau khi giáo viên sửa khối lớp).</summary>
    [HttpGet("me")]
    [ProducesResponseType<ApiResponse<StudentProfileDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StudentProfileDto>>> Me(CancellationToken ct)
        => OkEnvelope(await studentAuth.GetProfileAsync(CurrentActor, ct));

    /// <summary>
    /// Đổi mật khẩu. Đang bị buộc đổi (lần đầu hoặc sau khi giáo viên đặt lại) thì không cần mật khẩu hiện tại, mật khẩu mới phải khác
    /// mật khẩu tạm; ngược lại phải nhập đúng mật khẩu hiện tại. Mật khẩu mới nhập hai lần. Trả token mới, token cũ hết dùng.
    /// </summary>
    [HttpPut("me/password")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentSessionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<StudentSessionDto>>> ChangePassword(StudentPasswordRequest request, CancellationToken ct)
        => OkEnvelope(await studentAuth.ChangePasswordAsync(CurrentActor, request, ct), Messages.Success("Đổi mật khẩu").Text);

    /// <summary>Lớp của em: tên, khối, cấp học, năm học, giáo viên phụ trách, trường.</summary>
    [HttpGet("class")]
    [Authorize(Policy = Policies.StudentReady)]
    [ProducesResponseType<ApiResponse<StudentClassDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StudentClassDto>>> Class(CancellationToken ct)
        => OkEnvelope(await studentAuth.GetClassAsync(CurrentActor, ct));
}
