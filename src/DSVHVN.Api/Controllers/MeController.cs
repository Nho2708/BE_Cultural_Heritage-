using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Application.Profile;
using DSVHVN.Domain.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>Biểu mẫu đổi ảnh đại diện (multipart/form-data).</summary>
public sealed class UploadAvatarForm
{
    /// <summary>Tệp ảnh JPG, PNG hoặc WEBP, tối đa 5 MB (định dạng xét theo nội dung tệp, không theo đuôi). Được cắt vuông, thu nhỏ và bỏ EXIF.</summary>
    public IFormFile? File { get; set; }
}

/// <summary>Quản lý hồ sơ cá nhân trên web quản trị, dùng chung cho ADMIN, ORG_ADMIN, TEACHER (học sinh: 403).</summary>
[Route("api/v1/me")]
[Authorize(Policy = Policies.Staff)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class MeController(ProfileService profiles, AuthService auth) : ApiControllerBase
{
    // Giới hạn thân yêu cầu rộng hơn tệp một chút để chừa chỗ cho phần đầu biểu mẫu; dung lượng tệp do dịch vụ kiểm lại.
    private const long AvatarRequestLimit = StoredFileRules.MaxImageBytes + 512 * 1024;

    /// <summary>Tab Thông tin: hồ sơ, vai trò, trường, cờ buộc đổi mật khẩu.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<ProfileDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProfileDto>>> Get(CancellationToken ct)
        => OkEnvelope(await profiles.GetAsync(CurrentActor, ct));

    /// <summary>Sửa họ tên, số điện thoại (ảnh đại diện đổi bằng <c>POST /me/avatar</c>). Email, tên đăng nhập, vai trò, trường chỉ đọc.</summary>
    [HttpPut]
    [ProducesResponseType<ApiResponse<ProfileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<ProfileDto>>> Update(UpdateProfileRequest request, CancellationToken ct)
        => OkEnvelope(await profiles.UpdateAsync(CurrentActor, request, ct), Messages.Success("Lưu hồ sơ").Text);

    /// <summary>
    /// Đổi ảnh đại diện của chính mình bằng ảnh tải lên (trường <c>file</c>). Trả lại hồ sơ với <c>avatarUrl</c> mới. Ảnh cũ vẫn được giữ trong kho tệp.
    /// </summary>
    [HttpPost("avatar")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(AvatarRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = AvatarRequestLimit)]
    [ProducesResponseType<ApiResponse<ProfileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<ApiResponse<ProfileDto>>> UploadAvatar([FromForm] UploadAvatarForm form, CancellationToken ct)
        => OkEnvelope(await profiles.UploadAvatarAsync(CurrentActor, await FormFiles.ReadAsync(form.File, StoredFileRules.MaxImageBytes, ct), ct),
            Messages.Success("Cập nhật ảnh đại diện").Text);

    /// <summary>Gỡ ảnh đại diện của chính mình (<c>avatarUrl</c> về null).</summary>
    [HttpDelete("avatar")]
    [ProducesResponseType<ApiResponse<ProfileDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProfileDto>>> RemoveAvatar(CancellationToken ct)
        => OkEnvelope(await profiles.RemoveAvatarAsync(CurrentActor, ct), Messages.Success("Gỡ ảnh đại diện").Text);

    /// <summary>Tab Đổi mật khẩu: mật khẩu mới khác mật khẩu hiện tại; token cũ hết dùng được, phiên hiện tại nhận access token mới.</summary>
    [HttpPut("password")]
    [ProducesResponseType<ApiResponse<AccessTokenDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<AccessTokenDto>>> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
        => OkEnvelope(await auth.ChangePasswordAsync(CurrentActor, request, ct), Messages.Success("Đổi mật khẩu").Text);
}
