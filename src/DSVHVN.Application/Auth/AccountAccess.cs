using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;

namespace DSVHVN.Application.Auth;

/// <summary>
/// Chỉ tài khoản ACTIVE, chưa xóa mềm, thuộc trường chưa ngừng mới dùng được hệ thống.
/// Dùng ở đăng nhập và ở lần kiểm mỗi yêu cầu, để khóa tài khoản hay ngừng trường có hiệu lực ngay.
/// </summary>
public static class AccountAccess
{
    /// <summary>Lý do chặn (phần <c>{ly_do}</c> của thông điệp không đăng nhập được), hoặc null nếu tài khoản dùng được. Tài khoản đã xóa mềm không đi qua đây.</summary>
    public static string? BlockedReason(UserStatus status, bool organizationDeactivated) =>
        organizationDeactivated
            ? Messages.SignInBlockedReasons.OrganizationDeactivated
            : status switch
            {
                UserStatus.ACTIVE => null,
                UserStatus.LOCKED => Messages.SignInBlockedReasons.Locked,
                _ => Messages.SignInBlockedReasons.Inactive,
            };

    public static string? BlockedReason(User user) =>
        BlockedReason(user.Status, user.Organization?.DeletedAt is not null);
}
