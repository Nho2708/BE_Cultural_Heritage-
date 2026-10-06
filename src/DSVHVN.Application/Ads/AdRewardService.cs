using System.Globalization;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Application.Guests;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Guests;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DSVHVN.Application.Ads;

/// <summary>
/// Ghi lượt mở khóa khi Google xác nhận khách đã nhận thưởng. Hai đường:
/// <list type="bullet">
/// <item><b>App, AdMob (xác minh phía máy chủ):</b> AdMob gọi <c>GET</c> về máy chủ khi người xem nhận thưởng. Hợp lệ khi đồng thời: chữ ký ECDSA đúng theo khóa công khai của Google; đơn vị quảng cáo
/// thuộc cấu hình (đồng thời cho biết Android hay iOS); mã thiết bị (<c>user_id</c>) đúng UUID phiên bản 4; mã di sản (<c>custom_data</c>) là di sản đã xuất bản; thời điểm không lệch quá 1 giờ;
/// mã giao dịch chưa ghi. Mã giao dịch đã ghi thì không ghi thêm và vẫn trả thành công (không phải lượt bị từ chối) để Google không gọi lại. Không hợp lệ thì từ chối và ghi nhật ký thao tác.</item>
/// <item><b>Web, Ad Manager (trình duyệt báo):</b> máy chủ cấp một mã giao dịch dùng một lần khi khách bắt đầu xem (ký HMAC, gắn thiết bị và di sản, hết hạn sau 10 phút); trình duyệt gửi lại mã
/// khi nhận sự kiện "đã cấp thưởng". Vì không có xác minh của Google nên kém tin cậy hơn: có giới hạn tần suất (10 lượt mỗi giờ mỗi thiết bị, 60 lượt mỗi giờ mỗi IP).</item>
/// </list>
/// Di sản đang còn mở khóa thì lượt mới không ghi và không kéo dài hạn. Mở khóa chỉ dựa trên dòng đã ghi; không lưu địa chỉ IP, mã quảng cáo của người xem, doanh thu hay nội dung quảng cáo.
/// </summary>
public sealed class AdRewardService(
    IAppDbContext db,
    IAuditLogger audit,
    GuestService guests,
    AdUnlockService unlocks,
    IAdMobKeyStore keys,
    IAdRewardTokenCodec codec,
    IAdRewardThrottle throttle,
    IOptions<AdsOptions> options,
    TimeProvider clock)
{
    public const string ReasonDuplicate = "DUPLICATE";
    public const string ReasonAlreadyUnlocked = "ALREADY_UNLOCKED";

    private const int MaxTransactionIdLength = 255;
    private const int MaxAdUnitLength = 100;

    private DateTime Now() => clock.UtcNowMilliseconds();

    // ---- App: xác minh phía máy chủ của AdMob ----

    /// <summary>
    /// Xử lý một lời gọi xác minh của AdMob. <paramref name="rawQueryString"/> là chuỗi truy vấn thô (để kiểm chữ ký), <paramref name="query"/> là các tham số đã giải mã.
    /// Hợp lệ (kể cả lần gọi lại trùng mã giao dịch) thì trả kết quả để Api trả 200; không hợp lệ thì ghi nhật ký thao tác rồi ném 400.
    /// </summary>
    public async Task<AdRewardResultDto> HandleAdMobAsync(string? rawQueryString, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        string? Param(string name) => query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        var signedContent = AdMobSignature.SignedContent(rawQueryString);
        var signature = Param("signature");
        if (signedContent is null || signature is null || !long.TryParse(Param("key_id"), NumberStyles.None, CultureInfo.InvariantCulture, out var keyId))
            throw await RejectAsync(AdRules.RejectedBadParameter, AdNetwork.ADMOB, null, ct);

        var publicKey = await keys.FindPublicKeyAsync(keyId, ct);
        if (publicKey is null || !AdMobSignature.Verify(signedContent, signature, publicKey))
            throw await RejectAsync(AdRules.RejectedBadSignature, AdNetwork.ADMOB, null, ct);

        var adUnit = Param("ad_unit");
        var transactionId = Param("transaction_id");
        var userId = Param("user_id");
        var customData = Param("custom_data");
        var timestamp = Param("timestamp");
        if (adUnit is null || transactionId is null || userId is null || customData is null || timestamp is null
            || transactionId.Length > MaxTransactionIdLength || adUnit.Length > MaxAdUnitLength)
            throw await RejectAsync(AdRules.RejectedBadParameter, AdNetwork.ADMOB, null, ct);

        var platform = options.Value.PlatformOfAdMobUnit(adUnit);
        if (platform is null) throw await RejectAsync(AdRules.RejectedUnknownAdUnit, AdNetwork.ADMOB, null, ct);

        // Google gọi lại cùng một giao dịch (do lỗi mạng hoặc phản hồi chậm): đã ghi thì trả thành công, không ghi thêm, không ghi nhật ký từ chối.
        if (await FindByTransactionAsync(AdNetwork.ADMOB, transactionId, ct) is { } existing)
            return new AdRewardResultDto(false, ReasonDuplicate, existing.ExpiresAt);

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds <= 0)
            throw await RejectAsync(AdRules.RejectedBadParameter, AdNetwork.ADMOB, platform, ct);
        DateTime sentAt;
        try
        {
            sentAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw await RejectAsync(AdRules.RejectedBadParameter, AdNetwork.ADMOB, platform, ct);
        }

        var now = Now();
        if ((now - sentAt).Duration() > AdRules.MaxTimestampSkew)
            throw await RejectAsync(AdRules.RejectedStaleTimestamp, AdNetwork.ADMOB, platform, ct);

        var deviceKey = GuestRules.NormalizeDeviceKey(userId);
        if (deviceKey is null || !long.TryParse(customData, NumberStyles.None, CultureInfo.InvariantCulture, out var heritageId) || heritageId <= 0
            || !await IsPublishedAsync(heritageId, ct))
            throw await RejectAsync(AdRules.RejectedBadParameter, AdNetwork.ADMOB, platform, ct);

        return await RecordAsync(deviceKey, heritageId, platform.Value, AdNetwork.ADMOB, transactionId, adUnit, ct);
    }

    // ---- Web: Ad Manager, trình duyệt báo ----

    /// <summary>
    /// Khách bắt đầu xem quảng cáo trên web: cấp mã giao dịch một lần (10 phút) và đơn vị quảng cáo để tải. Di sản đang còn mở khóa hoặc cổng không áp dụng thì không cấp mã.
    /// Môi trường chưa cấu hình đơn vị quảng cáo cho web thì 422 <c>AD_UNAVAILABLE</c> (không có quảng cáo để hiện).
    /// </summary>
    public async Task<WebRewardStartDto> StartWebAsync(PublicViewer viewer, string? slug, CancellationToken ct)
    {
        var deviceKey = viewer.DeviceKey ?? throw AppException.Validation("deviceKey", GuestMessages.DeviceKeyRequired);
        var status = await unlocks.GetStatusAsync(viewer, slug, ct);
        if (!status.GateRequired || status.Unlocked) return new WebRewardStartDto(true, null, null, null, status);

        var path = options.Value.AdManager.AdUnitPaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim();
        if (path is null) throw AppException.BusinessRule(AdMessages.Unavailable);

        var ticket = codec.Issue(deviceKey, status.HeritageId);
        return new WebRewardStartDto(false, ticket.Code, ticket.ExpiresAt, path, status);
    }

    /// <summary>
    /// Trình duyệt báo đã nhận sự kiện "đã cấp thưởng" của Google. Kiểm theo thứ tự: mã giao dịch đúng chữ ký, còn hạn, khớp thiết bị và di sản; đơn vị quảng cáo thuộc cấu hình;
    /// mã đã ghi (gửi lại) thì thành công không ghi thêm; di sản đang mở khóa thì không ghi thêm; giới hạn tần suất (429 kèm <c>Retry-After</c>); rồi ghi lượt mở khóa nguồn trình duyệt.
    /// </summary>
    public async Task<AdRewardResultDto> ReportWebAsync(PublicViewer viewer, WebRewardReportRequest request, string? ipAddress, CancellationToken ct)
    {
        var deviceKey = viewer.DeviceKey ?? throw AppException.Validation("deviceKey", GuestMessages.DeviceKeyRequired);
        var slug = request.Slug?.Trim().ToLowerInvariant();
        var heritageId = HeritageRules.IsValidSlug(slug)
            ? await db.Heritages.AsNoTracking()
                .Where(h => h.Slug == slug && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED)
                .Select(h => (long?)h.Id).SingleOrDefaultAsync(ct)
            : null;
        if (heritageId is null) throw await RejectAsync(AdRules.RejectedBadParameter, AdNetwork.AD_MANAGER, ClientPlatform.WEB, ct);

        var ticket = codec.Read(request.TransactionId);
        if (ticket is null || ticket.DeviceKey != deviceKey || ticket.HeritageId != heritageId)
            throw await RejectAsync(AdRules.RejectedBadWebTransaction, AdNetwork.AD_MANAGER, ClientPlatform.WEB, ct);

        var adUnit = request.AdUnit?.Trim();
        if (!options.Value.IsAdManagerUnit(adUnit))
            throw await RejectAsync(AdRules.RejectedUnknownAdUnit, AdNetwork.AD_MANAGER, ClientPlatform.WEB, ct);

        if (await FindByTransactionAsync(AdNetwork.AD_MANAGER, ticket.Code, ct) is { } existing)
            return new AdRewardResultDto(false, ReasonDuplicate, existing.ExpiresAt);

        if (await unlocks.ActiveExpiryAsync(deviceKey, heritageId.Value, ct) is { } active)
            return new AdRewardResultDto(false, ReasonAlreadyUnlocked, active);

        var decision = throttle.TryAcquire(deviceKey, ipAddress);
        if (!decision.Allowed)
        {
            // Nhật ký thao tác chỉ ghi lần từ chối đầu tiên của thiết bị trong giờ để kẻ lạm dụng không làm tràn bảng.
            if (throttle.ShouldLogRejection(deviceKey)) await LogRejectionAsync(AdRules.RejectedRateLimited, AdNetwork.AD_MANAGER, ClientPlatform.WEB, ct);
            var minutes = (int)Math.Ceiling(decision.RetryAfter.TotalMinutes);
            throw AppException.TooManyRequests(AdMessages.RateLimited(minutes), decision.RetryAfter);
        }

        return await RecordAsync(deviceKey, heritageId.Value, ClientPlatform.WEB, AdNetwork.AD_MANAGER, ticket.Code, adUnit, ct);
    }

    // ---- Dùng chung ----

    private async Task<AdRewardResultDto> RecordAsync(
        string deviceKey, long heritageId, ClientPlatform platform, AdNetwork network, string transactionId, string? adUnit, CancellationToken ct)
    {
        // Di sản đang còn mở khóa thì không ghi thêm và không kéo dài hạn (lời gọi xác minh đến muộn, hoặc khách xem hai lần).
        if (await unlocks.ActiveExpiryAsync(deviceKey, heritageId, ct) is { } active)
            return new AdRewardResultDto(false, ReasonAlreadyUnlocked, active);

        var now = Now();
        var deviceId = await guests.EnsureDeviceAsync(deviceKey, platform, ct);
        var unlock = new AdUnlock
        {
            GuestDeviceId = deviceId,
            HeritageId = heritageId,
            Platform = platform,
            AdNetwork = network,
            TransactionId = transactionId,
            AdUnitId = adUnit?.Length > MaxAdUnitLength ? adUnit[..MaxAdUnitLength] : adUnit,
            UnlockedAt = now,
            ExpiresAt = AdRules.ExpiryOf(now),
        };
        db.AdUnlocks.Add(unlock);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Hai lời gọi cùng mã giao dịch chạy đồng thời: ràng buộc không trùng chỉ cho một lời gọi ghi được; lời gọi thua coi là gửi lại.
            db.AdUnlocks.Remove(unlock);
            if (await FindByTransactionAsync(network, transactionId, ct) is { } existing)
                return new AdRewardResultDto(false, ReasonDuplicate, existing.ExpiresAt);
            throw;
        }

        return new AdRewardResultDto(true, null, unlock.ExpiresAt);
    }

    private Task<AdUnlock?> FindByTransactionAsync(AdNetwork network, string transactionId, CancellationToken ct) =>
        db.AdUnlocks.AsNoTracking().Where(u => u.AdNetwork == network && u.TransactionId == transactionId).FirstOrDefaultAsync(ct);

    private Task<bool> IsPublishedAsync(long heritageId, CancellationToken ct) =>
        db.Heritages.AsNoTracking().AnyAsync(h => h.Id == heritageId && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED, ct);

    /// <summary>Ghi nhật ký từ chối (chỉ lý do, mạng, nền tảng; không ghi IP, chữ ký, mã thiết bị) rồi trả lỗi 400 để ném.</summary>
    private async Task<AppException> RejectAsync(string reason, AdNetwork network, ClientPlatform? platform, CancellationToken ct)
    {
        await LogRejectionAsync(reason, network, platform, ct);
        return new AppException(400, AdMessages.RewardRejected);
    }

    private async Task LogRejectionAsync(string reason, AdNetwork network, ClientPlatform? platform, CancellationToken ct)
    {
        audit.Record(AuditAction.AD_REWARD_REJECTED, AuditTargets.AdUnlocks, null, actor: null, newValue: new { reason, network, platform });
        await db.SaveChangesAsync(ct);
    }
}
