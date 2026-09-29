using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Organizations;

namespace DSVHVN.Domain.Identity;

/// <summary>
/// Bảng <c>users</c>: tài khoản quản trị hệ thống, quản trị trường, giáo viên và học sinh.
/// Quản trị hệ thống có <see cref="OrganizationId"/> NULL; ba vai trò còn lại thuộc đúng một trường và không chuyển trường.
/// Người lớn đăng nhập web quản trị bằng email hoặc tên đăng nhập; học sinh không có email, tên đăng nhập là mã học sinh.
/// CSDL để phần lớn cột được NULL (giữ đúng thiết kế); tầng ứng dụng bảo đảm tài khoản do mình tạo luôn có
/// tên đăng nhập, họ tên, băm mật khẩu và trạng thái.
/// </summary>
public class User : IHasCreatedAt, IHasUpdatedAt, ISoftDeletable
{
    public long Id { get; set; }

    public long? OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Khóa ngoại bắt buộc: mỗi tài khoản đúng một vai trò.</summary>
    public int RoleId { get; set; }
    public Role? Role { get; set; }

    /// <summary>
    /// Tên đăng nhập, lưu chữ thường với người lớn (3-50 ký tự, chữ không dấu, số, dấu chấm, gạch dưới);
    /// với học sinh chính là mã học sinh.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>Email đăng nhập và nhận liên kết đặt mật khẩu, lưu chữ thường đã cắt khoảng trắng. NULL với học sinh.</summary>
    public string? Email { get; set; }

    /// <summary>Băm PBKDF2 có muối. Tài khoản người lớn mới mang băm của một mật khẩu ngẫu nhiên không ai biết.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Buộc đổi mật khẩu ở lần đăng nhập tới (học sinh mới tạo hoặc vừa được giáo viên đặt lại mật khẩu).</summary>
    public bool MustChangePassword { get; set; }

    public string? FullName { get; set; }

    public string? Phone { get; set; }

    public string? AvatarUrl { get; set; }

    public UserStatus? Status { get; set; } = UserStatus.ACTIVE;

    public DateTime? LastLoginAt { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public RoleCode RoleCode => Roles.CodeOf(RoleId);

    public bool IsStudent => RoleId == Roles.StudentId;
}
