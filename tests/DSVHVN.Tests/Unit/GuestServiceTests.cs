using DSVHVN.Application.Common;
using DSVHVN.Application.Guests;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Khách tham quan trên SQLite trong bộ nhớ với đồng hồ tua được: bản ghi thiết bị chỉ tạo khi cần lưu, lượt lặp trong 10 phút ghi một lần, mỗi thiết bị giữ tối đa 500 lượt,
/// lượt quá 90 ngày không hiện và bị dọn, xóa từng mục hoặc xóa hết là xóa thật, thiết bị này không đụng được lịch sử của thiết bị khác.
/// </summary>
public sealed class GuestServiceTests : IDisposable
{
    private readonly ServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly string KeyA = "3f2b8c1e-9a47-4d6b-8e10-5c7a9b2d4f61";
    private static readonly string KeyB = "9d8c7b6a-5e4f-4a3b-9c2d-1e0f9a8b7c6d";

    private Task<T> Guests<T>(Func<GuestService, Task<T>> action) => _h.Service(action);

    private Task<long> HeritageAsync(string slug, ContentStatus status = ContentStatus.PUBLISHED) =>
        _h.QueryAsync(async db =>
        {
            var heritage = new Heritage { Name = "Di sản " + slug, Slug = slug, Status = status };
            db.Heritages.Add(heritage);
            await db.SaveChangesAsync();
            return heritage.Id;
        });

    private Task<bool> RecordAsync(string key, GuestActivityType type, string? slug = null, string? query = null, LanguageCode language = LanguageCode.vi,
        ClientPlatform? platform = null) =>
        Guests(g => g.RecordAsync(key, platform, language, type, slug, query, default));

    private Task<int> CountAsync(string? key = null) =>
        _h.QueryAsync(db => key is null
            ? db.GuestActivities.CountAsync()
            : db.GuestActivities.CountAsync(a => a.GuestDevice!.DeviceKey == key));

    // ---- Thiết bị ----

    [Fact]
    public async Task Reading_with_an_unseen_key_creates_nothing()
    {
        var device = await Guests(g => g.GetDeviceAsync(KeyA, LanguageCode.ko, default));
        var history = await Guests(g => g.ListHistoryAsync(KeyA, LanguageCode.vi, null, null, default));
        var deleted = await Guests(g => g.DeleteAllAsync(KeyA, default));

        Assert.False(device.IsRegistered);
        Assert.Equal(("ko", false), (device.Language, device.LocationAllowed));
        Assert.Empty(history.Items);
        Assert.Equal(0, deleted);
        Assert.Equal(0, await _h.QueryAsync(db => db.GuestDevices.CountAsync()));
    }

    [Fact]
    public async Task Registering_creates_the_device_once_and_updates_it_afterwards()
    {
        var (first, created) = await Guests(g => g.RegisterAsync(KeyA.ToUpperInvariant(), new GuestDeviceRequest(ClientPlatform.ANDROID, "zh-Hans", true), null, default));
        Assert.True(created);
        Assert.Equal((KeyA, ClientPlatform.ANDROID, "zh-Hans", true, true), (first.DeviceKey, first.Platform, first.Language, first.LocationAllowed, first.IsRegistered));
        Assert.Equal(_h.Clock.GetUtcNow().UtcDateTime, first.CreatedAt);

        _h.Clock.Advance(TimeSpan.FromHours(2));
        var (second, createdAgain) = await Guests(g => g.RegisterAsync(KeyA, new GuestDeviceRequest(null, "ja", null), null, default));
        Assert.False(createdAgain);
        // Trường không gửi thì giữ nguyên; lần thấy cuối đổi, lần đầu thấy giữ nguyên.
        Assert.Equal((ClientPlatform.ANDROID, "ja", true), (second.Platform, second.Language, second.LocationAllowed));
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(_h.Clock.GetUtcNow().UtcDateTime, second.LastSeenAt);
        Assert.Equal(1, await _h.QueryAsync(db => db.GuestDevices.CountAsync()));
    }

    [Fact]
    public async Task The_platform_header_is_the_default_and_web_is_the_last_resort()
    {
        var (fromHeader, _) = await Guests(g => g.RegisterAsync(KeyA, new GuestDeviceRequest(null, null, null), ClientPlatform.IOS, default));
        var (fallback, _) = await Guests(g => g.RegisterAsync(KeyB, new GuestDeviceRequest(null, null, null), null, default));
        Assert.Equal((ClientPlatform.IOS, "vi"), (fromHeader.Platform, fromHeader.Language));
        Assert.Equal(ClientPlatform.WEB, fallback.Platform);
    }

    [Fact]
    public async Task Without_a_key_the_server_issues_a_valid_one_and_a_bad_key_is_refused()
    {
        var (issued, created) = await Guests(g => g.RegisterAsync(null, new GuestDeviceRequest(null, null, null), null, default));
        Assert.True(created);
        Assert.Equal(issued.DeviceKey, GuestRules.NormalizeDeviceKey(issued.DeviceKey));

        var bad = await Assert.ThrowsAsync<AppException>(() => Guests(g => g.RegisterAsync("khong-phai-uuid", new GuestDeviceRequest(null, null, null), null, default)));
        Assert.Equal((400, MessageCodes.Invalid), (bad.StatusCode, bad.AppMessage.Code));
        var missing = Assert.Throws<AppException>(() => GuestService.RequireKey("  "));
        Assert.Equal((400, MessageCodes.Required), (missing.StatusCode, missing.AppMessage.Code));
    }

    [Fact]
    public async Task Settings_reject_an_unknown_language_or_platform()
    {
        var language = await Assert.ThrowsAsync<AppException>(() => Guests(g => g.UpdateSettingsAsync(KeyA, new GuestDeviceRequest(null, "fr", null), null, default)));
        var traditional = await Assert.ThrowsAsync<AppException>(() => Guests(g => g.UpdateSettingsAsync(KeyA, new GuestDeviceRequest(null, "zh-Hant", null), null, default)));
        var platform = await Assert.ThrowsAsync<AppException>(() =>
            Guests(g => g.UpdateSettingsAsync(KeyA, new GuestDeviceRequest((ClientPlatform)99, null, null), null, default)));
        Assert.All([language, traditional, platform], e => Assert.Equal((400, MessageCodes.Invalid), (e.StatusCode, e.AppMessage.Code)));
        Assert.Equal(0, await _h.QueryAsync(db => db.GuestDevices.CountAsync()));
    }

    // ---- Ghi lịch sử ----

    [Fact]
    public async Task Recording_creates_the_device_on_the_first_activity_and_stores_the_language_and_platform()
    {
        await HeritageAsync("van-mieu");
        Assert.True(await RecordAsync(KeyA, GuestActivityType.VIEW, "Van-Mieu", language: LanguageCode.ko, platform: ClientPlatform.IOS));

        var (device, activity) = await _h.QueryAsync(async db => (await db.GuestDevices.SingleAsync(), await db.GuestActivities.SingleAsync()));
        Assert.Equal((KeyA, ClientPlatform.IOS, LanguageCode.ko), (device.DeviceKey, device.Platform, device.LanguageCode));
        Assert.Equal((device.Id, GuestActivityType.VIEW, LanguageCode.ko), (activity.GuestDeviceId, activity.ActivityType, activity.LanguageCode));
        Assert.Null(activity.SearchQuery);
        Assert.Equal(_h.Clock.GetUtcNow().UtcDateTime, activity.CreatedAt);
    }

    [Fact]
    public async Task A_repeat_of_the_same_activity_within_ten_minutes_is_recorded_once()
    {
        await HeritageAsync("van-mieu");
        await HeritageAsync("hue");
        Assert.True(await RecordAsync(KeyA, GuestActivityType.VIEW, "van-mieu"));

        _h.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(await RecordAsync(KeyA, GuestActivityType.VIEW, "van-mieu"));          // lặp trong 10 phút
        Assert.True(await RecordAsync(KeyA, GuestActivityType.LISTEN, "van-mieu"));         // khác loại
        Assert.True(await RecordAsync(KeyA, GuestActivityType.VIEW, "hue"));                // khác di sản
        Assert.True(await RecordAsync(KeyB, GuestActivityType.VIEW, "van-mieu"));           // khác thiết bị
        Assert.Equal(4, await CountAsync());

        _h.Clock.Advance(TimeSpan.FromMinutes(2));                                          // đã quá 10 phút kể từ lượt đầu
        Assert.True(await RecordAsync(KeyA, GuestActivityType.VIEW, "van-mieu"));
        Assert.Equal(4, await CountAsync(KeyA));
    }

    [Fact]
    public async Task A_repeated_search_text_counts_once_and_a_different_one_counts_again()
    {
        Assert.True(await RecordAsync(KeyA, GuestActivityType.SEARCH, query: "  hoàng   thành "));
        Assert.False(await RecordAsync(KeyA, GuestActivityType.SEARCH, query: "hoàng thành"));
        Assert.True(await RecordAsync(KeyA, GuestActivityType.SEARCH, query: "văn miếu"));

        var stored = await _h.QueryAsync(db => db.GuestActivities.OrderBy(a => a.Id).Select(a => new { a.SearchQuery, a.HeritageId }).ToListAsync());
        Assert.Equal(["hoàng thành", "văn miếu"], stored.Select(s => s.SearchQuery));
        Assert.All(stored, s => Assert.Null(s.HeritageId));
    }

    [Fact]
    public async Task Only_published_heritage_is_recorded_and_bad_input_is_refused_without_creating_a_device()
    {
        await HeritageAsync("nhap", ContentStatus.DRAFT);
        await HeritageAsync("luu-tru", ContentStatus.ARCHIVED);
        var deleted = await HeritageAsync("da-xoa");
        await _h.QueryAsync(async db =>
        {
            (await db.Heritages.SingleAsync(h => h.Id == deleted)).DeletedAt = _h.Clock.GetUtcNow().UtcDateTime;
            return await db.SaveChangesAsync();
        });

        foreach (var slug in new[] { "nhap", "luu-tru", "da-xoa", "khong-co", "SAI DINH DANG!" })
        {
            var notFound = await Assert.ThrowsAsync<AppException>(() => RecordAsync(KeyA, GuestActivityType.VIEW, slug));
            Assert.Equal(404, notFound.StatusCode);
        }
        foreach (var (type, slug, query) in new (GuestActivityType, string?, string?)[]
                 {
                     (GuestActivityType.VIEW, null, null), (GuestActivityType.VIEW, "  ", null), (GuestActivityType.SEARCH, null, null), (GuestActivityType.SEARCH, null, "a"),
                     (GuestActivityType.SEARCH, null, new string('x', 101)), ((GuestActivityType)99, "van-mieu", null),
                 })
        {
            var invalid = await Assert.ThrowsAsync<AppException>(() => RecordAsync(KeyA, type, slug, query));
            Assert.Equal(400, invalid.StatusCode);
        }
        Assert.Equal(0, await _h.QueryAsync(db => db.GuestDevices.CountAsync()));
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task A_failure_while_recording_never_breaks_the_main_request_but_a_cancellation_is_still_reported()
    {
        await _h.Service<GuestService>(g => g.TryRecordAsync(KeyA, null, LanguageCode.vi, GuestActivityType.VIEW, "khong-co", null, default));   // 404 bên trong, bị nuốt
        Assert.Equal(0, await CountAsync());

        await HeritageAsync("van-mieu");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _h.Service<GuestService>(g => g.TryRecordAsync(KeyA, null, LanguageCode.vi, GuestActivityType.VIEW, "van-mieu", null, cancelled.Token)));
    }

    [Fact]
    public async Task Each_device_keeps_only_its_latest_500_activities()
    {
        for (var i = 1; i <= GuestRules.MaxActivitiesPerDevice + 7; i++)
        {
            _h.Clock.Advance(TimeSpan.FromSeconds(1));
            await RecordAsync(KeyA, GuestActivityType.SEARCH, query: $"từ khóa {i:000}");
        }
        await RecordAsync(KeyB, GuestActivityType.SEARCH, query: "thiết bị khác");

        var queries = await _h.QueryAsync(db => db.GuestActivities.Where(a => a.GuestDevice!.DeviceKey == KeyA).OrderBy(a => a.Id).Select(a => a.SearchQuery).ToListAsync());
        Assert.Equal(GuestRules.MaxActivitiesPerDevice, queries.Count);
        Assert.Equal("từ khóa 008", queries[0]);   // bảy lượt cũ nhất đã bị xóa
        Assert.Equal("từ khóa 507", queries[^1]);
        Assert.Equal(1, await CountAsync(KeyB));
    }

    // ---- Xem và xóa lịch sử ----

    [Fact]
    public async Task History_is_newest_first_and_hides_heritage_that_is_no_longer_public()
    {
        var hue = await HeritageAsync("hue");
        var hidden = await HeritageAsync("sap-an");
        await HeritageAsync("van-mieu");
        await RecordAsync(KeyA, GuestActivityType.SEARCH, query: "thăng long");
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        await RecordAsync(KeyA, GuestActivityType.VIEW, "hue");
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        await RecordAsync(KeyA, GuestActivityType.LISTEN, "sap-an");
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        await RecordAsync(KeyA, GuestActivityType.VIEW_3D, "van-mieu");
        await _h.QueryAsync(async db =>
        {
            (await db.Heritages.SingleAsync(h => h.Id == hidden)).Status = ContentStatus.ARCHIVED;
            return await db.SaveChangesAsync();
        });

        var page = await Guests(g => g.ListHistoryAsync(KeyA, LanguageCode.vi, null, null, default));
        Assert.Equal(3, page.TotalItems);
        Assert.Equal([GuestActivityType.VIEW_3D, GuestActivityType.VIEW, GuestActivityType.SEARCH], page.Items.Select(i => i.Type));
        Assert.Equal(["Xem mô hình 3D", "Xem di sản", "Tìm kiếm"], page.Items.Select(i => i.TypeLabel));
        Assert.Equal(["van-mieu", "hue", null], page.Items.Select(i => i.Heritage?.Slug));
        Assert.Equal("thăng long", page.Items[2].Query);
        Assert.Equal(hue, page.Items[1].Heritage!.Id);
        Assert.All(page.Items, i => Assert.Equal("vi", i.Language));
    }

    [Fact]
    public async Task History_pages_and_validates_the_page_size()
    {
        for (var i = 1; i <= 5; i++)
        {
            _h.Clock.Advance(TimeSpan.FromSeconds(1));
            await RecordAsync(KeyA, GuestActivityType.SEARCH, query: $"tìm {i}");
        }

        var second = await Guests(g => g.ListHistoryAsync(KeyA, LanguageCode.vi, 2, 2, default));
        Assert.Equal((2, 2, 5, 3), (second.Page, second.PageSize, second.TotalItems, second.TotalPages));
        Assert.Equal(["tìm 3", "tìm 2"], second.Items.Select(i => i.Query));
        await Assert.ThrowsAsync<AppException>(() => Guests(g => g.ListHistoryAsync(KeyA, LanguageCode.vi, 1, 101, default)));
    }

    [Fact]
    public async Task Activities_older_than_ninety_days_are_not_shown_and_the_purge_deletes_them_for_real()
    {
        await HeritageAsync("van-mieu");
        await RecordAsync(KeyA, GuestActivityType.VIEW, "van-mieu");
        await RecordAsync(KeyB, GuestActivityType.SEARCH, query: "cũ của thiết bị khác");

        _h.Clock.Advance(TimeSpan.FromDays(89));
        await RecordAsync(KeyA, GuestActivityType.SEARCH, query: "còn hạn");
        Assert.Equal(2, (await Guests(g => g.ListHistoryAsync(KeyA, LanguageCode.vi, null, null, default))).TotalItems);

        _h.Clock.Advance(TimeSpan.FromDays(2));   // lượt đầu của cả hai thiết bị đã 91 ngày
        var visible = await Guests(g => g.ListHistoryAsync(KeyA, LanguageCode.vi, null, null, default));
        Assert.Equal(["còn hạn"], visible.Items.Select(i => i.Query));
        Assert.Equal(3, await CountAsync());     // chưa dọn thì còn trong bảng

        Assert.Equal(2, await Guests(g => g.PurgeExpiredAsync(default)));
        Assert.Equal(["còn hạn"], await _h.QueryAsync(db => db.GuestActivities.Select(a => a.SearchQuery).ToListAsync()));
        Assert.Equal(0, await Guests(g => g.PurgeExpiredAsync(default)));
        Assert.Equal(2, await _h.QueryAsync(db => db.GuestDevices.CountAsync()));   // bản ghi thiết bị không bị xóa
    }

    [Fact]
    public async Task Deleting_one_item_or_all_deletes_for_real_and_only_on_the_own_device()
    {
        await HeritageAsync("van-mieu");
        await RecordAsync(KeyA, GuestActivityType.VIEW, "van-mieu");
        await RecordAsync(KeyA, GuestActivityType.SEARCH, query: "hoàng thành");
        await RecordAsync(KeyB, GuestActivityType.VIEW, "van-mieu");
        var ids = await _h.QueryAsync(db => db.GuestActivities.OrderBy(a => a.Id).Select(a => new { a.Id, Key = a.GuestDevice!.DeviceKey }).ToListAsync());
        var mine = ids.First(i => i.Key == KeyA).Id;
        var others = ids.Single(i => i.Key == KeyB).Id;

        // Mục của thiết bị khác, mục không có, thiết bị chưa từng thấy: cùng một 404.
        foreach (var (key, id) in new[] { (KeyA, others), (KeyA, 999_999L), ("0a0a0a0a-0a0a-4a0a-8a0a-0a0a0a0a0a0a", mine) })
        {
            var notFound = await Assert.ThrowsAsync<AppException>(() => _h.Service<GuestService>(g => g.DeleteOneAsync(key, id, default)));
            Assert.Equal(404, notFound.StatusCode);
        }
        Assert.Equal(3, await CountAsync());

        await _h.Service<GuestService>(g => g.DeleteOneAsync(KeyA, mine, default));
        Assert.Equal(2, await CountAsync());
        Assert.Equal(1, await Guests(g => g.DeleteAllAsync(KeyA, default)));
        Assert.Equal((0, 1), (await CountAsync(KeyA), await CountAsync(KeyB)));
        Assert.Equal(2, await _h.QueryAsync(db => db.GuestDevices.CountAsync()));   // cài đặt thiết bị còn nguyên
        Assert.Equal(1, await Guests(g => g.DeleteAllAsync(KeyB, default)));
        Assert.Equal(0, await Guests(g => g.DeleteAllAsync(KeyB, default)));
    }
}
