using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DSVHVN.Application.Ads;

/// <summary>
/// Cổng quảng cáo: quyết định tóm tắt và nghe thuyết minh của một di sản đã mở cho người xem hay chưa. Cổng CHỈ khóa hai việc này; mọi nội dung khác miễn phí.
/// <list type="bullet">
/// <item><b>Cổng không áp dụng</b> (mở thẳng) khi: học sinh đã đăng nhập (máy chủ quyết định theo phiên đăng nhập); cổng tắt hoặc chưa cấu hình đơn vị quảng cáo nào;
/// hoặc thiết bị khai báo "không có quảng cáo" mà môi trường cho phép khai báo này (bản demo iOS bằng Expo Go). Môi trường thật bỏ qua khai báo nên khách không tự tắt được cổng.</item>
/// <item><b>Còn hạn mở khóa</b> của đúng thiết bị cho đúng di sản (24 giờ, theo giờ máy chủ, ở mọi ngôn ngữ) thì mở; ngược lại 403 <c>AD_UNLOCK_REQUIRED</c>.</item>
/// <item>Mở khóa chỉ dựa trên dòng <c>ad_unlocks</c> máy chủ đã ghi, không dựa trên tham số máy khách tự khai.</item>
/// </list>
/// </summary>
public sealed class AdUnlockService(IAppDbContext db, IOptions<AdsOptions> options, TimeProvider clock)
{
    public const string ReasonStudent = "STUDENT";
    public const string ReasonGateDisabled = "GATE_DISABLED";
    public const string ReasonNoAdsDevice = "NO_ADS_DEVICE";

    private DateTime Now() => clock.UtcNowMilliseconds();

    /// <summary>Cấu hình công khai cho giao diện (cổng, thời hạn, mức xếp hạng nội dung, đơn vị quảng cáo đầu tiên của từng nền tảng).</summary>
    public AdConfigDto GetConfig()
    {
        var o = options.Value;
        return new AdConfigDto(
            o.GateActive, (int)AdRules.UnlockDuration.TotalHours, AdRules.MaxAdContentRating,
            o.AdMob.AndroidAdUnitIds.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
            o.AdMob.IosAdUnitIds.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
            o.AdManager.AdUnitPaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
            o.AllowNoAdsDeclaration);
    }

    /// <summary>Lý do cổng không áp dụng cho người xem (<c>STUDENT</c>, <c>GATE_DISABLED</c>, <c>NO_ADS_DEVICE</c>); cổng áp dụng thì null.</summary>
    public string? OpenReason(PublicViewer viewer)
    {
        var o = options.Value;
        if (viewer.IsStudent) return ReasonStudent;
        if (!o.GateActive) return ReasonGateDisabled;
        if (viewer.DeclaresNoAds && o.AllowNoAdsDeclaration) return ReasonNoAdsDevice;
        return null;
    }

    /// <summary>Hạn của lượt mở khóa còn hiệu lực của thiết bị cho di sản (hạn xa nhất); không còn thì null.</summary>
    public async Task<DateTime?> ActiveExpiryAsync(string deviceKey, long heritageId, CancellationToken ct)
    {
        var now = Now();
        return await db.AdUnlocks.AsNoTracking()
            .Where(u => u.GuestDevice!.DeviceKey == deviceKey && u.HeritageId == heritageId && u.ExpiresAt > now)
            .MaxAsync(u => (DateTime?)u.ExpiresAt, ct);
    }

    /// <summary>Trạng thái mở khóa của di sản (theo đường dẫn) trên thiết bị của yêu cầu; di sản không có hoặc chưa xuất bản thì 404.</summary>
    public async Task<AdUnlockStatusDto> GetStatusAsync(PublicViewer viewer, string? slug, CancellationToken ct)
    {
        var key = slug?.Trim().ToLowerInvariant();
        var heritage = HeritageRules.IsValidSlug(key)
            ? await db.Heritages.AsNoTracking()
                .Where(h => h.Slug == key && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED)
                .Select(h => new { h.Id, h.Slug }).SingleOrDefaultAsync(ct)
            : null;
        if (heritage is null) throw AppException.NotFound(Messages.NotFound("xem trạng thái mở khóa", "di sản"));
        return await StatusAsync(viewer, heritage.Id, heritage.Slug!, ct);
    }

    /// <summary>Trạng thái mở khóa theo mã di sản đã biết là đã xuất bản.</summary>
    public async Task<AdUnlockStatusDto> StatusAsync(PublicViewer viewer, long heritageId, string slug, CancellationToken ct)
    {
        if (OpenReason(viewer) is { } reason) return new AdUnlockStatusDto(heritageId, slug, false, true, null, reason);
        var expires = viewer.DeviceKey is null ? null : await ActiveExpiryAsync(viewer.DeviceKey, heritageId, ct);
        return new AdUnlockStatusDto(heritageId, slug, true, expires is not null, expires, null);
    }

    /// <summary>Cổng chặn (403 <c>AD_UNLOCK_REQUIRED</c>) khi cổng áp dụng cho người xem và thiết bị chưa có lượt mở khóa còn hạn cho di sản này.</summary>
    public async Task EnsureUnlockedAsync(PublicViewer viewer, long heritageId, string heritageName, CancellationToken ct)
    {
        if (OpenReason(viewer) is not null) return;
        if (viewer.DeviceKey is not null && await ActiveExpiryAsync(viewer.DeviceKey, heritageId, ct) is not null) return;
        throw AppException.Forbidden(AdMessages.UnlockRequired(heritageName));
    }

    /// <summary>
    /// Cổng của việc "nghe thuyết minh" (app ghi lượt nghe vì giọng đọc chạy ở thiết bị): di sản chưa xuất bản hoặc chưa có tóm tắt tiếng Việt đã duyệt thì không chặn
    /// (việc ghi tự trả 404; di sản chưa có tóm tắt không hiện cổng, không mở khóa); ngược lại như <see cref="EnsureUnlockedAsync"/>.
    /// </summary>
    public async Task EnsureListenAllowedAsync(PublicViewer viewer, string? slug, CancellationToken ct)
    {
        if (OpenReason(viewer) is not null) return;
        var key = slug?.Trim().ToLowerInvariant();
        if (!HeritageRules.IsValidSlug(key)) return;
        var heritage = await db.Heritages.AsNoTracking()
            .Where(h => h.Slug == key && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED)
            .Select(h => new { h.Id, h.Name }).SingleOrDefaultAsync(ct);
        if (heritage is null) return;
        var hasSummary = await db.HeritageSummaries.AsNoTracking()
            .AnyAsync(s => s.HeritageId == heritage.Id && s.LanguageCode == LanguageCode.vi && s.Status == TranslationStatus.APPROVED, ct);
        if (!hasSummary) return;
        await EnsureUnlockedAsync(viewer, heritage.Id, heritage.Name ?? string.Empty, ct);
    }

    /// <summary>Xóa thật mọi lượt mở khóa cũ hơn 12 tháng (tác vụ nền hằng ngày). Trả số dòng đã xóa.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var cutoff = AdRules.RetentionCutoff(Now());
        return await db.AdUnlocks.Where(u => u.UnlockedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}

internal static class ClockExtensions
{
    /// <summary>Giờ UTC hiện tại cắt về mili giây (khớp độ chính xác của datetime2(3)).</summary>
    public static DateTime UtcNowMilliseconds(this TimeProvider clock)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
