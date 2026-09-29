using DSVHVN.Api.Common;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Đăng nhập và đặt mật khẩu qua email. Không có tự đăng ký, không có refresh token:
/// đăng xuất là giao diện xóa access token.
/// </summary>
[Route("api/v1/auth")]
[AllowAnonymous]
public sealed class AuthController(AuthService auth) : ApiControllerBase
{
    /// <summary>Đăng nhập: một ô email hoặc tên đăng nhập + mật khẩu; nhận access token 8 giờ và hồ sơ.</summary>
    [HttpPost("login")]
    [ProducesResponseType<ApiResponse<LoginResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<LoginResultDto>>> Login(LoginRequest request, CancellationToken ct)
        => OkEnvelope(await auth.LoginAsync(request, ClientIp, ct), Messages.Success("Đăng nhập").Text);

    /// <summary>Nhánh quên mật khẩu: luôn trả cùng một câu dù email có thuộc tài khoản nào hay không.</summary>
    [HttpPost("forgot-password")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<object>>> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await auth.ForgotPasswordAsync(request, ct);
        return OkEnvelope<object>(null, Messages.ForgotPasswordAccepted.Text);
    }

    /// <summary>Màn hình Đặt mật khẩu: kiểm liên kết (lần đầu hoặc đặt lại) trước khi hiện ô nhập mật khẩu.</summary>
    [HttpPost("password-token/check")]
    [ProducesResponseType<ApiResponse<PasswordTokenInfoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<PasswordTokenInfoDto>>> CheckPasswordToken(PasswordTokenRequest request, CancellationToken ct)
        => OkEnvelope(await auth.CheckPasswordTokenAsync(request, ct));

    /// <summary>Đặt mật khẩu bằng liên kết trong thư — dùng cho cả tài khoản mới (48 giờ) và quên mật khẩu (30 phút).</summary>
    [HttpPost("set-password")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> SetPassword(SetPasswordRequest request, CancellationToken ct)
    {
        await auth.SetPasswordAsync(request, ct);
        return OkEnvelope<object>(null, Messages.Success("Đặt mật khẩu").Text);
    }
}
