using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Organizations;

namespace DSVHVN.Domain.Identity;

/// <summary>
/// Bảng <c>users</c>: tài khoản ADMIN, ORG_ADMIN, TEACHER. Học sinh không có tài khoản.
/// ADMIN có <see cref="OrganizationId"/> NULL; ORG_ADMIN, TEACHER thuộc đúng một trường và không chuyển trường.
/// </summary>
public class User : IHasCreatedAt, IHasUpdatedAt, ISoftDeletable
{
    public long Id { get; set; }

    public long? OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int RoleId { get; set; }
    public Role? Role { get; set; }

    /// <summary>Tên đăng nhập, lưu chữ thường (3-50 ký tự, chữ không dấu, số, dấu chấm, gạch dưới).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Email đăng nhập và nhận liên kết đặt mật khẩu, lưu chữ thường đã cắt khoảng trắng.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Băm PBKDF2 có muối. Tài khoản mới mang băm của một mật khẩu ngẫu nhiên không ai biết.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string? Phone { get; set; }

    public string? AvatarUrl { get; set; }

    public UserStatus Status { get; set; } = UserStatus.ACTIVE;

    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public RoleCode RoleCode => Roles.CodeOf(RoleId);
}
