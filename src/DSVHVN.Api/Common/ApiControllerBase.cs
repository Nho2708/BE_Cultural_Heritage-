using System.Globalization;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Common;

[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected ActionResult<ApiResponse<T>> OkEnvelope<T>(T? data, string? message = null) =>
        Ok(ApiResponse.Ok(data, message, HttpContext.TraceIdentifier));

    protected ActionResult<ApiResponse<T>> CreatedEnvelope<T>(T? data, string? message = null) =>
        StatusCode(StatusCodes.Status201Created, ApiResponse.Ok(data, message, HttpContext.TraceIdentifier));

    /// <summary>
    /// Người gọi, dựng từ claim của access token. Claim sub, role, org đã được đối chiếu CSDL ở mỗi yêu cầu
    /// (<see cref="JwtAuthentication"/>), nên dùng được làm phạm vi dữ liệu.
    /// </summary>
    protected Actor CurrentActor
    {
        get
        {
            if (!long.TryParse(User.FindFirst(JwtClaimNames.Subject)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || !Enum.TryParse<RoleCode>(User.FindFirst(JwtClaimNames.Role)?.Value, out var role))
                throw AppException.Unauthorized(Messages.Forbidden);

            long? org = long.TryParse(User.FindFirst(JwtClaimNames.Organization)?.Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var orgId) ? orgId : null;
            return new Actor(id, role, org, ClientIp);
        }
    }

    /// <summary>IP của yêu cầu cho nhật ký kiểm toán; IPv4 ánh xạ trong IPv6 đổi về dạng IPv4.</summary>
    protected string? ClientIp
    {
        get
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            if (ip is null) return null;
            return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        }
    }
}
