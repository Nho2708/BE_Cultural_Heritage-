using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Ads;

/// <summary>
/// Thống kê cho quản trị hệ thống (chỉ tổng hợp, theo khoảng ngày Việt Nam, mặc định 30 ngày gần nhất, tối đa 366 ngày): lượt mở khóa bằng quảng cáo theo ngày, nền tảng, mạng quảng cáo (kèm nguồn báo),
/// di sản được mở khóa nhiều nhất; số lượt bị từ chối theo lý do (lấy từ nhật ký thao tác; lần gọi lại trùng mã giao dịch không tính là bị từ chối, lượt vượt giới hạn tần suất của web chỉ ghi một dòng
/// mỗi thiết bị mỗi giờ); số thiết bị khách hoạt động, ngôn ngữ dùng nhiều, di sản được xem, nghe, xem 3D nhiều. Không có doanh thu (xem ở bảng điều khiển của Google) và không tra cứu theo từng mã thiết bị.
/// </summary>
public sealed class AdStatsService(IAppDbContext db, TimeProvider clock)
{
    public const string RevenueNote =
        "Doanh thu quảng cáo không tính ở đây: xem ở bảng điều khiển Google AdMob và Google Ad Manager. Mỗi lượt từ chối do vượt giới hạn tần suất của web chỉ ghi một dòng cho mỗi thiết bị trong một giờ.";

    public const string AdMobConsoleUrl = "https://apps.admob.com";
    public const string AdManagerConsoleUrl = "https://admanager.google.com";

    public async Task<AdStatsDto> GetAsync(Actor actor, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (actor.Role != RoleCode.ADMIN) throw AppException.Forbidden(Messages.Forbidden);

        var today = VietnamTime.Today(clock.GetUtcNow());
        var end = to ?? today;
        var start = from ?? end.AddDays(1 - AdRules.DefaultStatsDays);
        if (end < start) throw AppException.Validation("to", Messages.Invalid("Ngày kết thúc", "không được trước ngày bắt đầu"));
        if (end.DayNumber - start.DayNumber + 1 > AdRules.MaxStatsDays)
            throw AppException.Validation("from", Messages.Invalid("Khoảng thời gian", $"tối đa {AdRules.MaxStatsDays} ngày"));

        // Mốc UTC của đầu ngày Việt Nam; so sánh nửa mở để mọi thời điểm trong ngày cuối đều vào khoảng.
        var fromUtc = VietnamTime.StartOfDayUtc(start);
        var toUtc = VietnamTime.StartOfDayUtc(end.AddDays(1));

        var unlocks = await UnlockStatsAsync(start, end, fromUtc, toUtc, ct);
        var rejections = await RejectionStatsAsync(fromUtc, toUtc, ct);
        var guests = await GuestStatsAsync(fromUtc, toUtc, ct);
        return new AdStatsDto(start, end, unlocks, rejections, guests, RevenueNote, AdMobConsoleUrl, AdManagerConsoleUrl);
    }

    private async Task<AdUnlockStatsDto> UnlockStatsAsync(DateOnly start, DateOnly end, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var rows = db.AdUnlocks.AsNoTracking().Where(u => u.UnlockedAt >= fromUtc && u.UnlockedAt < toUtc);

        var times = await rows.Select(u => u.UnlockedAt).ToListAsync(ct);
        var perDay = times.GroupBy(t => DateOnly.FromDateTime(t + VietnamTime.Offset)).ToDictionary(g => g.Key, g => g.Count());
        var byDay = new List<AdDayCountDto>();
        for (var day = start; day <= end; day = day.AddDays(1)) byDay.Add(new AdDayCountDto(day, perDay.GetValueOrDefault(day)));

        var byPlatform = (await rows.GroupBy(u => u.Platform).Select(g => new { Platform = g.Key, Count = g.Count() }).ToListAsync(ct))
            .OrderBy(x => x.Platform).Select(x => new AdPlatformCountDto(x.Platform, x.Count)).ToList();
        var byNetwork = (await rows.GroupBy(u => u.AdNetwork).Select(g => new { Network = g.Key, Count = g.Count() }).ToListAsync(ct))
            .OrderBy(x => x.Network)
            .Select(x => new AdNetworkCountDto(x.Network, AdRules.SourceOf(x.Network), AdRules.SourceLabel(x.Network), x.Count)).ToList();

        var top = await rows.GroupBy(u => u.HeritageId).Select(g => new { HeritageId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.HeritageId).Take(AdRules.TopLimit).ToListAsync(ct);
        return new AdUnlockStatsDto(times.Count, byDay, byPlatform, byNetwork, await WithNamesAsync(top.Select(t => (t.HeritageId, t.Count)).ToList(), ct));
    }

    private async Task<AdRejectionStatsDto> RejectionStatsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var logs = db.AuditLogs.AsNoTracking().Where(a => a.Action == AuditAction.AD_REWARD_REJECTED && a.CreatedAt >= fromUtc && a.CreatedAt < toUtc);
        var byReason = new List<AdRejectionCountDto>();
        foreach (var reason in AdRules.RejectionReasons)
        {
            // new_value là JSON ngắn {"reason":"...","network":"..."}; lý do là mã chữ hoa nên so chuỗi con là đủ.
            var marker = $"\"reason\":\"{reason}\"";
            var count = await logs.CountAsync(a => a.NewValue!.Contains(marker), ct);
            byReason.Add(new AdRejectionCountDto(reason, AdRules.RejectionLabel(reason), count));
        }

        return new AdRejectionStatsDto(await logs.CountAsync(ct), byReason);
    }

    private async Task<AdGuestStatsDto> GuestStatsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var activities = db.GuestActivities.AsNoTracking().Where(a => a.CreatedAt >= fromUtc && a.CreatedAt < toUtc);

        // Thiết bị hoạt động: có lượt trong khoảng, hoặc lần thấy cuối rơi vào khoảng.
        var withActivity = await activities.Select(a => a.GuestDeviceId).Distinct().ToListAsync(ct);
        var seen = await db.GuestDevices.AsNoTracking().Where(d => d.LastSeenAt >= fromUtc && d.LastSeenAt < toUtc).Select(d => d.Id).ToListAsync(ct);
        var active = withActivity.Union(seen).Count();

        var pairs = await activities.Select(a => new { a.GuestDeviceId, a.LanguageCode }).Distinct().ToListAsync(ct);
        var byLanguage = pairs.GroupBy(p => p.LanguageCode)
            .Select(g => new AdLanguageCountDto(Languages.Tag(g.Key), g.Count()))
            .OrderByDescending(l => l.Devices).ThenBy(l => l.Language, StringComparer.Ordinal).ToList();

        return new AdGuestStatsDto(active, byLanguage,
            await TopActivityAsync(activities, GuestActivityType.VIEW, ct),
            await TopActivityAsync(activities, GuestActivityType.LISTEN, ct),
            await TopActivityAsync(activities, GuestActivityType.VIEW_3D, ct));
    }

    private async Task<IReadOnlyList<AdHeritageCountDto>> TopActivityAsync(IQueryable<Domain.Guests.GuestActivity> activities, GuestActivityType type, CancellationToken ct)
    {
        var top = await activities.Where(a => a.ActivityType == type && a.HeritageId != null)
            .GroupBy(a => a.HeritageId!.Value).Select(g => new { HeritageId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.HeritageId).Take(AdRules.TopLimit).ToListAsync(ct);
        return await WithNamesAsync(top.Select(t => (t.HeritageId, t.Count)).ToList(), ct);
    }

    private async Task<IReadOnlyList<AdHeritageCountDto>> WithNamesAsync(IReadOnlyList<(long HeritageId, int Count)> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.HeritageId).ToList();
        var names = await db.Heritages.AsNoTracking().Where(h => ids.Contains(h.Id)).Select(h => new { h.Id, h.Slug, h.Name }).ToDictionaryAsync(h => h.Id, ct);
        return rows.Select(r => names.TryGetValue(r.HeritageId, out var h)
            ? new AdHeritageCountDto(r.HeritageId, h.Slug, h.Name, r.Count)
            : new AdHeritageCountDto(r.HeritageId, null, null, r.Count)).ToList();
    }
}
