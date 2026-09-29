using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>Xem nhật ký kiểm toán. Chỉ ADMIN, chỉ đọc: không có endpoint sửa, xóa.</summary>
[Route("api/v1/admin/audit-logs")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminAuditLogsController(AuditLogService auditLogs) : ApiControllerBase
{
    /// <summary>Lọc theo người làm (userId hoặc từ khóa actor), vai trò, hành động, đối tượng, khoảng thời gian; mới nhất trước.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedResult<AuditLogDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<PagedResult<AuditLogDto>>>> List(
        [FromQuery] long? userId, [FromQuery] string? actor, [FromQuery] ActorRole? actorRole, [FromQuery] AuditAction? action,
        [FromQuery] string? targetType, [FromQuery] long? targetId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await auditLogs.ListAsync(
            new AuditLogQuery(userId, actor, actorRole, action, targetType, targetId, from, to, page, pageSize), ct));
}
