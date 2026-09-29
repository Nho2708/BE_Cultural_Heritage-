using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;

namespace DSVHVN.Application.Auth;

/// <summary>Màn hình Đăng nhập: một ô "Email hoặc tên đăng nhập" + mật khẩu.</summary>
public sealed record LoginRequest(string? EmailOrUsername, string? Password);

public sealed record ForgotPasswordRequest(string? Email);

/// <summary>Màn hình Đặt mật khẩu mở từ liên kết trong thư: kiểm token trước khi hiện ô nhập mật khẩu.</summary>
public sealed record PasswordTokenRequest(string? Token);

/// <summary>Màn hình Đặt mật khẩu: dùng cho cả đặt mật khẩu lần đầu lẫn đặt lại sau khi quên.</summary>
public sealed record SetPasswordRequest(string? Token, string? NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>Kết quả kiểm liên kết: tài khoản sẽ được đặt mật khẩu và hạn của liên kết.</summary>
public sealed record PasswordTokenInfoDto(string Username, DateTime ExpiresAt);

public sealed record OrganizationRefDto(long Id, string Name);

/// <summary>Hồ sơ trả cho giao diện (tab Thông tin của màn hình Hồ sơ; điều hướng theo vai trò sau đăng nhập).</summary>
public sealed record ProfileDto(
    long Id,
    string Username,
    string Email,
    string FullName,
    string? Phone,
    string? AvatarUrl,
    RoleCode Role,
    string RoleName,
    UserStatus Status,
    OrganizationRefDto? Organization,
    DateTime? LastLoginAt,
    DateTime CreatedAt)
{
    /// <summary><paramref name="user"/> phải đã nạp <see cref="User.Organization"/> nếu có trường.</summary>
    public static ProfileDto From(User user) => new(
        user.Id, user.Username, user.Email, user.FullName, user.Phone, user.AvatarUrl,
        user.RoleCode, Roles.NameOf(user.RoleCode), user.Status,
        user.Organization is null ? null : new OrganizationRefDto(user.Organization.Id, user.Organization.Name),
        user.LastLoginAt, user.CreatedAt);
}

/// <summary>Chỉ có access token: hết hạn thì đăng nhập lại; đăng xuất = trình duyệt xóa token.</summary>
public sealed record AccessTokenDto(string TokenType, string AccessToken, DateTime ExpiresAt);

public sealed record LoginResultDto(string TokenType, string AccessToken, DateTime ExpiresAt, ProfileDto User);
