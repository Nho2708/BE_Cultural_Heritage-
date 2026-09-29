using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;

namespace DSVHVN.Api.Security;

/// <summary>
/// Policy phân quyền theo 4 vai trò. Backend là nơi thực thi phân quyền; phạm vi theo trường
/// (ORG_ADMIN, TEACHER chỉ thấy trường mình) nằm ở tầng Application.
/// Mọi endpoint mặc định phải đăng nhập (FallbackPolicy); endpoint công khai tự khai báo [AllowAnonymous].
/// Web quản trị chỉ nhận ba vai trò người lớn (<see cref="Staff"/>); khu học của học sinh dùng <see cref="Student"/>.
/// </summary>
public static class Policies
{
    /// <summary>Mọi vai trò đã đăng nhập.</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>Ba vai trò người lớn của web quản trị (màn hình Hồ sơ): ADMIN, ORG_ADMIN, TEACHER.</summary>
    public const string Staff = "Staff";

    /// <summary>Quản trị hệ thống (khu /quan-tri).</summary>
    public const string Admin = "Admin";

    /// <summary>Quản trị trường (khu /truong).</summary>
    public const string OrgAdmin = "OrgAdmin";

    /// <summary>Giáo viên (khu /giao-vien: lớp, học sinh, bài học, bài kiểm tra).</summary>
    public const string Teacher = "Teacher";

    /// <summary>Học sinh (khu học của web học tập và app mobile).</summary>
    public const string Student = "Student";

    public static void Register(AuthorizationOptions options)
    {
        var authenticated = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        options.DefaultPolicy = authenticated;
        options.FallbackPolicy = authenticated;

        options.AddPolicy(Authenticated, authenticated);
        options.AddPolicy(Staff, p => p.RequireAuthenticatedUser()
            .RequireRole(nameof(RoleCode.ADMIN), nameof(RoleCode.ORG_ADMIN), nameof(RoleCode.TEACHER)));
        options.AddPolicy(Admin, p => p.RequireAuthenticatedUser().RequireRole(nameof(RoleCode.ADMIN)));
        options.AddPolicy(OrgAdmin, p => p.RequireAuthenticatedUser().RequireRole(nameof(RoleCode.ORG_ADMIN)));
        options.AddPolicy(Teacher, p => p.RequireAuthenticatedUser().RequireRole(nameof(RoleCode.TEACHER)));
        options.AddPolicy(Student, p => p.RequireAuthenticatedUser().RequireRole(nameof(RoleCode.STUDENT)));
    }

    /// <summary>Loại claim vai trò trong JWT, dùng khi dựng ClaimsIdentity.</summary>
    public const string RoleClaimType = JwtClaimNames.Role;
}
