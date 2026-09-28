using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Application.Profile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>Quản lý hồ sơ cá nhân trên web quản trị, dùng chung cho ADMIN, ORG_ADMIN, TEACHER (học sinh: 403).</summary>
[Route("api/v1/me")]
[Authorize(Policy = Policies.Staff)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class MeController(ProfileService profiles, AuthService auth) : ApiControllerBase
{
    /// <summary>Tab Thông tin: hồ sơ, vai trò, trường, cờ buộc đổi mật khẩu.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<ProfileDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProfileDto>>> Get(CancellationToken ct)
        => OkEnvelope(await profiles.GetAsync(CurrentActor, ct));

    /// <summary>Sửa họ tên, số điện thoại, ảnh đại diện. Email, tên đăng nhập, vai trò, trường chỉ đọc.</summary>
    [HttpPut]
    [ProducesResponseType<ApiResponse<ProfileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<ProfileDto>>> Update(UpdateProfileRequest request, CancellationToken ct)
        => OkEnvelope(await profiles.UpdateAsync(CurrentActor, request, ct), Messages.Success("Lưu hồ sơ").Text);

    /// <summary>Tab Đổi mật khẩu: mật khẩu mới khác mật khẩu hiện tại; token cũ hết dùng được, phiên hiện tại nhận access token mới.</summary>
    [HttpPut("password")]
    [ProducesResponseType<ApiResponse<AccessTokenDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<AccessTokenDto>>> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
        => OkEnvelope(await auth.ChangePasswordAsync(CurrentActor, request, ct), Messages.Success("Đổi mật khẩu").Text);
}
