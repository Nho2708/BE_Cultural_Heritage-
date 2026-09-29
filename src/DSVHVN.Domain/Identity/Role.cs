using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Identity;

/// <summary>Bảng <c>roles</c>: danh mục vai trò, dữ liệu khởi tạo cố định 4 dòng (<see cref="Roles"/>).</summary>
public class Role
{
    public int Id { get; set; }

    /// <summary>Mã vai trò (không trùng). CSDL để cột được NULL; bốn dòng khởi tạo đều có mã.</summary>
    public RoleCode? Code { get; set; }

    /// <summary>Tên hiển thị tiếng Việt.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// Id cố định của 4 vai trò seed trong migration khởi tạo (bảng <c>roles</c>). Không có chức năng thêm, đổi vai trò,
/// nên code so sánh thẳng với id thay vì tra bảng mỗi lần.
/// </summary>
public static class Roles
{
    public const int AdminId = 1;
    public const int OrgAdminId = 2;
    public const int TeacherId = 3;
    public const int StudentId = 4;

    /// <summary>Ba vai trò người lớn dùng web quản trị; học sinh chỉ dùng web học tập và app mobile.</summary>
    public static readonly IReadOnlyList<int> AdultRoleIds = [AdminId, OrgAdminId, TeacherId];

    public static int IdOf(RoleCode code) => code switch
    {
        RoleCode.ADMIN => AdminId,
        RoleCode.ORG_ADMIN => OrgAdminId,
        RoleCode.TEACHER => TeacherId,
        RoleCode.STUDENT => StudentId,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    public static RoleCode CodeOf(int roleId) => roleId switch
    {
        AdminId => RoleCode.ADMIN,
        OrgAdminId => RoleCode.ORG_ADMIN,
        TeacherId => RoleCode.TEACHER,
        StudentId => RoleCode.STUDENT,
        _ => throw new ArgumentOutOfRangeException(nameof(roleId), roleId, null),
    };

    public static string NameOf(RoleCode code) => code switch
    {
        RoleCode.ADMIN => "Quản trị hệ thống",
        RoleCode.ORG_ADMIN => "Quản trị trường",
        RoleCode.TEACHER => "Giáo viên",
        RoleCode.STUDENT => "Học sinh",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    /// <summary>Vai trò ghi vào <c>audit_logs.actor_role</c>.</summary>
    public static ActorRole ToActorRole(RoleCode code) => code switch
    {
        RoleCode.ADMIN => ActorRole.ADMIN,
        RoleCode.ORG_ADMIN => ActorRole.ORG_ADMIN,
        RoleCode.TEACHER => ActorRole.TEACHER,
        RoleCode.STUDENT => ActorRole.STUDENT,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}
