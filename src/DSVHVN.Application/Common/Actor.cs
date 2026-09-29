using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;

namespace DSVHVN.Application.Common;

/// <summary>
/// Người gọi đã xác thực. Tầng Api dựng từ claim của access token; claim đã được đối chiếu CSDL ở mỗi yêu cầu,
/// nên <see cref="OrganizationId"/> là trường thật của tài khoản — mọi truy vấn của ORG_ADMIN, TEACHER lọc theo giá trị này.
/// </summary>
public sealed record Actor(long UserId, RoleCode Role, long? OrganizationId, string? IpAddress)
{
    public ActorRole AuditRole => Roles.ToActorRole(Role);

    /// <summary>Trường của ORG_ADMIN/TEACHER. ADMIN không có trường nên gọi ở đây là lỗi lập trình, trả 403.</summary>
    public long RequireOrganizationId() =>
        OrganizationId ?? throw AppException.Forbidden(Messages.Forbidden);
}
