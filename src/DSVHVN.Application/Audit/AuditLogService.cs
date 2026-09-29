using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Audit;

/// <summary>Bộ lọc của màn hình Nhật ký kiểm toán: người làm (id hoặc từ khóa tên đăng nhập/họ tên), vai trò, hành động, đối tượng, khoảng thời gian.</summary>
public sealed record AuditLogQuery(
    long? UserId,
    string? Actor,
    ActorRole? ActorRole,
    AuditAction? Action,
    string? TargetType,
    long? TargetId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? Page,
    int? PageSize);

public sealed record AuditLogDto(
    long Id,
    DateTime CreatedAt,
    long? UserId,
    string? Username,
    string? FullName,
    ActorRole ActorRole,
    string ActorRoleLabel,
    AuditAction Action,
    string ActionLabel,
    string TargetType,
    long? TargetId,
    string? OldValue,
    string? NewValue,
    string? IpAddress);

/// <summary>Xem nhật ký kiểm toán (chỉ ADMIN, chỉ đọc).</summary>
public sealed class AuditLogService(IAppDbContext db)
{
    public async Task<PagedResult<AuditLogDto>> ListAsync(AuditLogQuery query, CancellationToken ct)
    {
        var logs = db.AuditLogs.AsNoTracking();
        if (query.UserId is { } userId) logs = logs.Where(l => l.UserId == userId);
        if (query.ActorRole is { } role) logs = logs.Where(l => l.ActorRole == role);
        if (query.Action is { } action) logs = logs.Where(l => l.Action == action);
        if (!string.IsNullOrWhiteSpace(query.TargetType))
        {
            var targetType = query.TargetType.Trim();
            logs = logs.Where(l => l.TargetType == targetType);
        }
        if (query.TargetId is { } targetId) logs = logs.Where(l => l.TargetId == targetId);
        if (query.From is { } from)
        {
            var fromUtc = from.UtcDateTime;
            logs = logs.Where(l => l.CreatedAt >= fromUtc);
        }
        if (query.To is { } to)
        {
            var toUtc = to.UtcDateTime;
            logs = logs.Where(l => l.CreatedAt <= toUtc);
        }

        var rows =
            from l in logs
            join u in db.Users.AsNoTracking() on l.UserId equals (long?)u.Id into users
            from u in users.DefaultIfEmpty()
            select new { Log = l, Username = u == null ? null : u.Username, FullName = u == null ? null : u.FullName };

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            var keyword = query.Actor.Trim();
            rows = rows.Where(r => r.Username != null && (r.Username.Contains(keyword) || r.FullName!.Contains(keyword)));
        }

        var page = await rows
            .OrderByDescending(r => r.Log.CreatedAt).ThenByDescending(r => r.Log.Id)
            .ToPagedAsync(query.Page, query.PageSize, ct);

        var items = page.Items.Select(r => new AuditLogDto(
            r.Log.Id, r.Log.CreatedAt, r.Log.UserId, r.Username, r.FullName,
            r.Log.ActorRole, AuditLabels.Of(r.Log.ActorRole), r.Log.Action, AuditLabels.Of(r.Log.Action),
            r.Log.TargetType, r.Log.TargetId, r.Log.OldValue, r.Log.NewValue, r.Log.IpAddress)).ToList();

        return new PagedResult<AuditLogDto>(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }
}
