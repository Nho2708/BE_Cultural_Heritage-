using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;

namespace DSVHVN.Api.Security;

/// <summary>
/// Policy phân quyền theo 3 vai trò. Backend là nơi thực thi phân quyền; phạm vi theo trường
/// (ORG_ADMIN, TEACHER chỉ thấy trường mình) nằm ở tầng Application.
/// Mọi endpoint mặc định phải đăng nhập (FallbackPolicy); endpoint công khai tự khai báo [AllowAnonymous].
/// </summary>
public static class Policies
{
    /// <summary>Mọi vai trò đã đăng nhập (màn hình Hồ sơ).</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>Quản trị hệ thống (khu /quan-tri).</summary>
    public const string Admin = "Admin";

    /// <summary>Quản trị trường (khu /truong).</summary>
    public const string OrgAdmin = "OrgAdmin";

    /// <summary>Giáo viên (khu /giao-vien, dùng ở các luồng soạn bài học và bài kiểm tra).</summary>
    public const string Teacher = "Teacher";

    public static void Register(AuthorizationOptions options)
    {
        var authenticated = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        options.DefaultPolicy = authenticated;
        options.FallbackPolicy = authenticated;

        options.AddPolicy(Authenticated, authenticated);
        options.AddPolicy(Admin, p => p.RequireAuthenticatedUser().RequireRole(nameof(RoleCode.ADMIN)));
        options.AddPolicy(OrgAdmin, p => p.RequireAuthenticatedUser().RequireRole(nameof(RoleCode.ORG_ADMIN)));
        options.AddPolicy(Teacher, p => p.RequireAuthenticatedUser().RequireRole(nameof(RoleCode.TEACHER)));
    }

    /// <summary>Loại claim vai trò trong JWT, dùng khi dựng ClaimsIdentity.</summary>
    public const string RoleClaimType = JwtClaimNames.Role;
}
