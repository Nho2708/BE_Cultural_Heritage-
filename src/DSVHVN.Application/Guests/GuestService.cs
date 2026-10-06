using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Guests;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Application.Guests;

/// <summary>
/// Khách tham quan không có tài khoản: nhận diện bằng mã thiết bị ẩn danh (UUID phiên bản 4 do app hoặc trình duyệt sinh), máy chủ chỉ giữ bản ghi thiết bị
/// (mã, nền tảng, ngôn ngữ, cờ "Gần bạn", mốc đầu và cuối thấy) và lịch sử tìm, xem, đọc tóm tắt, nghe, xem 3D. Không ghi địa chỉ IP, không ghi vị trí, không ghép
/// với tài khoản học sinh. Việc loại học sinh đã đăng nhập (không ghi lịch sử, không dùng chức năng của khách) là của tầng Api theo phiên đăng nhập.
/// <list type="bullet">
/// <item>Bản ghi thiết bị chỉ được tạo ở yêu cầu đầu tiên cần lưu (đăng ký, đổi cài đặt, ghi một lượt); mọi yêu cầu chỉ đọc với mã chưa từng thấy không tạo gì.</item>
/// <item>Chỉ ghi lượt gắn với di sản đã xuất bản. Lượt lặp (cùng thiết bị, cùng loại, cùng di sản hoặc cùng từ khóa) trong 10 phút chỉ ghi một lần.
/// Mỗi thiết bị giữ tối đa 500 lượt gần nhất; lượt quá 90 ngày do tác vụ nền xóa thật. Xóa lịch sử là xóa thật, không xóa mềm.</item>
/// <item>Lịch sử chỉ trả cho yêu cầu mang đúng mã của thiết bị; không có chức năng liệt kê hay tìm mã.</item>
/// </list>
/// </summary>
public sealed class GuestService(IAppDbContext db, DiscoveryService discovery, TimeProvider clock, ILogger<GuestService> logger)
{
    private DateTime Now()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    /// <summary>Mã thiết bị bắt buộc và đúng định dạng; trả mã đã đưa về chữ thường, sai thì 400.</summary>
    public static string RequireKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw AppException.Validation("deviceKey", GuestMessages.DeviceKeyRequired);
        return GuestRules.NormalizeDeviceKey(raw) ?? throw AppException.Validation("deviceKey", GuestMessages.DeviceKeyInvalid);
    }

    // ---- Thiết bị và cài đặt ----

    /// <summary>
    /// Đăng ký thiết bị (không đăng nhập). Có mã thì tạo bản ghi nếu chưa có, đã có thì cập nhật cài đặt; không có mã (client không sinh được) thì máy chủ cấp một mã mới.
    /// Trả thêm cờ "vừa tạo" để Api trả 201.
    /// </summary>
    public async Task<(GuestDeviceDto Device, bool Created)> RegisterAsync(
        string? rawKey, GuestDeviceRequest request, ClientPlatform? headerPlatform, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(rawKey) ? GuestRules.NewDeviceKey() : RequireKey(rawKey);
        var (device, created) = await UpsertAsync(key, request, headerPlatform, ct);
        return (ToDto(device), created);
    }

    /// <summary>Cài đặt của thiết bị. Mã chưa từng thấy thì trả mặc định và không tạo bản ghi.</summary>
    public async Task<GuestDeviceDto> GetDeviceAsync(string key, LanguageCode language, CancellationToken ct)
    {
        var device = await db.GuestDevices.AsNoTracking().SingleOrDefaultAsync(d => d.DeviceKey == key, ct);
        return device is null
            ? new GuestDeviceDto(key, false, null, Languages.Tag(language), false, null, null)
            : ToDto(device);
    }

    /// <summary>Đổi cài đặt (ngôn ngữ, bật hoặc tắt "Gần bạn"); thiết bị chưa có bản ghi thì tạo.</summary>
    public async Task<GuestDeviceDto> UpdateSettingsAsync(string key, GuestDeviceRequest request, ClientPlatform? headerPlatform, CancellationToken ct) =>
        ToDto((await UpsertAsync(key, request, headerPlatform, ct)).Device);

    /// <summary>
    /// Mã của bản ghi thiết bị, tạo nếu chưa có với nền tảng đã cho, nhưng KHÔNG chạm vào thiết bị đã có (không đổi nền tảng, không đặt lại lần thấy cuối): dùng khi việc ghi
    /// không do chính thiết bị gọi, ví dụ lời gọi xác minh phần thưởng của AdMob về máy chủ.
    /// </summary>
    public async Task<long> EnsureDeviceAsync(string key, ClientPlatform platform, CancellationToken ct)
    {
        var existing = await FindDeviceIdAsync(key, ct);
        if (existing is { } id) return id;

        var now = Now();
        var device = new GuestDevice { DeviceKey = key, Platform = platform, LanguageCode = LanguageCode.vi, LocationAllowed = false, CreatedAt = now, LastSeenAt = now };
        db.GuestDevices.Add(device);
        try
        {
            await db.SaveChangesAsync(ct);
            return device.Id;
        }
        catch (DbUpdateException)
        {
            db.GuestDevices.Remove(device);   // dòng vừa thêm chưa lưu: bỏ khỏi bộ theo dõi; yêu cầu thua đọc lại dòng của yêu cầu thắng
            return await FindDeviceIdAsync(key, ct) ?? throw new InvalidOperationException("Không tạo được thiết bị của khách.");
        }
    }

    private async Task<(GuestDevice Device, bool Created)> UpsertAsync(string key, GuestDeviceRequest request, ClientPlatform? headerPlatform, CancellationToken ct)
    {
        var platform = request.Platform ?? headerPlatform;
        if (platform is { } p && !Enum.IsDefined(p))
            throw AppException.Validation("platform", Messages.Invalid("Nền tảng", "chỉ nhận ANDROID, IOS hoặc WEB"));
        LanguageCode? language = null;
        if (!string.IsNullOrWhiteSpace(request.Language))
            language = Languages.TryParse(request.Language)
                       ?? throw AppException.Validation("language", Messages.Invalid("Ngôn ngữ", "chỉ nhận vi, en, ko, zh-Hans hoặc ja"));
        return await TouchAsync(key, platform, language, request.LocationAllowed, Now(), ct);
    }

    /// <summary>
    /// Tìm bản ghi thiết bị, chưa có thì tạo; đặt lại lần thấy cuối và cập nhật nền tảng, ngôn ngữ, cờ "Gần bạn" khi được truyền. Hai yêu cầu đầu tiên cùng lúc của một mã
    /// chỉ có một yêu cầu tạo được dòng (mã không trùng); yêu cầu thua đọc lại dòng vừa tạo.
    /// </summary>
    private async Task<(GuestDevice Device, bool Created)> TouchAsync(
        string key, ClientPlatform? platform, LanguageCode? language, bool? locationAllowed, DateTime now, CancellationToken ct)
    {
        var device = await db.GuestDevices.SingleOrDefaultAsync(d => d.DeviceKey == key, ct);
        if (device is null)
        {
            device = new GuestDevice
            {
                DeviceKey = key,
                Platform = platform ?? ClientPlatform.WEB,
                LanguageCode = language ?? LanguageCode.vi,
                LocationAllowed = locationAllowed ?? false,
                CreatedAt = now,
                LastSeenAt = now,
            };
            db.GuestDevices.Add(device);
            try
            {
                await db.SaveChangesAsync(ct);
                return (device, true);
            }
            catch (DbUpdateException)
            {
                db.GuestDevices.Remove(device);   // dòng vừa thêm chưa lưu: bỏ khỏi bộ theo dõi
                var existing = await db.GuestDevices.SingleOrDefaultAsync(d => d.DeviceKey == key, ct);
                if (existing is null) throw;
                device = existing;
            }
        }

        if (platform is { } newPlatform) device.Platform = newPlatform;
        if (language is { } newLanguage) device.LanguageCode = newLanguage;
        if (locationAllowed is { } allowed) device.LocationAllowed = allowed;
        device.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        return (device, false);
    }

    private static GuestDeviceDto ToDto(GuestDevice d) =>
        new(d.DeviceKey, true, d.Platform, Languages.Tag(d.LanguageCode), d.LocationAllowed, d.CreatedAt, d.LastSeenAt);

    // ---- Ghi lịch sử ----

    /// <summary>
    /// Ghi một lượt theo yêu cầu của khách. Tìm kiếm cần từ khóa 2-100 ký tự; các loại khác cần đường dẫn của di sản đã xuất bản (không có thì 404).
    /// Trả false khi lượt lặp trong 10 phút (không ghi thêm); vẫn cập nhật lần thấy cuối và ngôn ngữ của thiết bị.
    /// </summary>
    public async Task<bool> RecordAsync(
        string key, ClientPlatform? platform, LanguageCode language, GuestActivityType type, string? slug, string? query, CancellationToken ct)
    {
        if (!Enum.IsDefined(type))
            throw AppException.Validation("type", Messages.Invalid("Loại lượt", "chỉ nhận SEARCH, VIEW, READ_SUMMARY, LISTEN hoặc VIEW_3D"));

        long? heritageId = null;
        string? normalizedQuery = null;
        if (type == GuestActivityType.SEARCH)
        {
            normalizedQuery = GuestRules.NormalizeQuery(query);
            if (normalizedQuery is null) throw AppException.Validation("query", Messages.Required("từ khóa"));
            if (normalizedQuery.Length is < GuestRules.SearchQueryMinLength or > GuestRules.SearchQueryMaxLength)
                throw AppException.Validation("query", Messages.Invalid("Từ khóa",
                    $"phải dài {GuestRules.SearchQueryMinLength}-{GuestRules.SearchQueryMaxLength} ký tự"));
        }
        else
        {
            var slugKey = slug?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(slugKey)) throw AppException.Validation("slug", Messages.Required("đường dẫn di sản"));
            heritageId = HeritageRules.IsValidSlug(slugKey)
                ? await db.Heritages.AsNoTracking()
                    .Where(h => h.Slug == slugKey && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED)
                    .Select(h => (long?)h.Id).SingleOrDefaultAsync(ct)
                : null;
            if (heritageId is null) throw AppException.NotFound(Messages.NotFound("ghi lịch sử", "di sản"));
        }

        var now = Now();
        var (device, _) = await TouchAsync(key, platform, language, null, now, ct);

        // Lượt lặp (cùng thiết bị, loại, di sản hoặc từ khóa) trong 10 phút chỉ ghi một lần.
        var since = now - GuestRules.RepeatWindow;
        var recent = db.GuestActivities.AsNoTracking().Where(a => a.GuestDeviceId == device.Id && a.ActivityType == type && a.CreatedAt >= since);
        var duplicate = heritageId is { } targetId
            ? await recent.AnyAsync(a => a.HeritageId == targetId, ct)
            : await recent.AnyAsync(a => a.SearchQuery == normalizedQuery, ct);
        if (duplicate) return false;

        db.GuestActivities.Add(new GuestActivity
        {
            GuestDeviceId = device.Id,
            ActivityType = type,
            HeritageId = heritageId,
            SearchQuery = normalizedQuery,
            LanguageCode = language,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await TrimAsync(device.Id, ct);
        return true;
    }

    /// <summary>
    /// Ghi một lượt khi khách đang dùng chức năng khác (xem chi tiết, đọc tóm tắt, xem 3D, tìm kiếm). Ghi lịch sử không bao giờ làm hỏng việc chính: lỗi ghi chỉ ghi log
    /// (không kèm mã thiết bị) và bỏ qua; hủy yêu cầu vẫn được báo lên.
    /// </summary>
    public async Task TryRecordAsync(
        string key, ClientPlatform? platform, LanguageCode language, GuestActivityType type, string? slug, string? query, CancellationToken ct)
    {
        try
        {
            await RecordAsync(key, platform, language, type, slug, query, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Không ghi được lịch sử của khách (loại {Type}); bỏ qua để không ảnh hưởng yêu cầu chính.", type);
        }
    }

    /// <summary>Giữ tối đa 500 lượt gần nhất của thiết bị: vượt thì xóa thật các lượt cũ nhất.</summary>
    private async Task TrimAsync(long deviceId, CancellationToken ct)
    {
        var count = await db.GuestActivities.CountAsync(a => a.GuestDeviceId == deviceId, ct);
        var excess = count - GuestRules.MaxActivitiesPerDevice;
        if (excess <= 0) return;
        var oldest = await db.GuestActivities.Where(a => a.GuestDeviceId == deviceId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).Take(excess).Select(a => a.Id).ToListAsync(ct);
        await db.GuestActivities.Where(a => oldest.Contains(a.Id)).ExecuteDeleteAsync(ct);
    }

    // ---- Xem và xóa lịch sử ----

    private async Task<long?> FindDeviceIdAsync(string key, CancellationToken ct) =>
        await db.GuestDevices.AsNoTracking().Where(d => d.DeviceKey == key).Select(d => (long?)d.Id).SingleOrDefaultAsync(ct);

    /// <summary>
    /// Lịch sử của thiết bị, mới nhất trước, trong thời hạn lưu 90 ngày. Mục của di sản đã bị ẩn, lưu trữ hoặc xóa không hiện (khách không mở được). Thiết bị chưa có bản ghi thì rỗng.
    /// Tên di sản theo ngôn ngữ yêu cầu (thiếu bản dịch thì tiếng Việt kèm cờ <c>isFallback</c> trên thẻ).
    /// </summary>
    public async Task<PagedResult<GuestHistoryItemDto>> ListHistoryAsync(string key, LanguageCode language, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var deviceId = await FindDeviceIdAsync(key, ct);
        if (deviceId is null) return new PagedResult<GuestHistoryItemDto>([], p, size, 0, 0);

        var cutoff = GuestRules.RetentionCutoff(Now());
        var rows = await db.GuestActivities.AsNoTracking()
            .Where(a => a.GuestDeviceId == deviceId && a.CreatedAt >= cutoff
                        && (a.HeritageId == null || (a.Heritage!.DeletedAt == null && a.Heritage.Status == ContentStatus.PUBLISHED)))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Select(a => new HistoryRow(a.Id, a.ActivityType, a.HeritageId, a.SearchQuery, a.LanguageCode, a.CreatedAt))
            .ToPagedAsync(p, size, ct);

        var cards = (await discovery.GetCardsAsync(rows.Items.Where(r => r.HeritageId != null).Select(r => r.HeritageId!.Value).ToList(), language, ct))
            .ToDictionary(c => c.Id);
        var items = rows.Items.Select(r => new GuestHistoryItemDto(
            r.Id, r.Type, GuestRules.Label(r.Type), r.Query,
            r.HeritageId is { } id && cards.TryGetValue(id, out var card) ? card : null,
            Languages.Tag(r.Language), r.CreatedAt)).ToList();
        return new PagedResult<GuestHistoryItemDto>(items, rows.Page, rows.PageSize, rows.TotalItems, rows.TotalPages);
    }

    private sealed record HistoryRow(long Id, GuestActivityType Type, long? HeritageId, string? Query, LanguageCode Language, DateTime CreatedAt);

    /// <summary>Xóa thật một mục của thiết bị; mục không có hoặc của thiết bị khác thì 404. Gợi ý tính lại từ phần còn lại.</summary>
    public async Task DeleteOneAsync(string key, long activityId, CancellationToken ct)
    {
        var deviceId = await FindDeviceIdAsync(key, ct);
        var deleted = deviceId is null ? 0
            : await db.GuestActivities.Where(a => a.Id == activityId && a.GuestDeviceId == deviceId).ExecuteDeleteAsync(ct);
        if (deleted == 0) throw AppException.NotFound(Messages.NotFound("xóa mục lịch sử", "mục lịch sử"));
    }

    /// <summary>Xóa thật toàn bộ lịch sử của thiết bị (không xóa bản ghi thiết bị, cài đặt hay lượt mở khóa quảng cáo). Trả số mục đã xóa.</summary>
    public async Task<int> DeleteAllAsync(string key, CancellationToken ct)
    {
        var deviceId = await FindDeviceIdAsync(key, ct);
        return deviceId is null ? 0 : await db.GuestActivities.Where(a => a.GuestDeviceId == deviceId).ExecuteDeleteAsync(ct);
    }

    // ---- Dọn nền ----

    /// <summary>Xóa thật mọi lượt cũ hơn 90 ngày của mọi thiết bị (tác vụ nền hằng ngày). Trả số lượt đã xóa.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var cutoff = GuestRules.RetentionCutoff(Now());
        return await db.GuestActivities.Where(a => a.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
