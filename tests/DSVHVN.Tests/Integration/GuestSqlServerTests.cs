using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Guests;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.GuestTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Khách tham quan trên SQL Server thật (database tạm của LocalDB, tạo bằng migration): CSDL tự từ chối dòng sai (mã trùng, nền tảng và ngôn ngữ ngoài danh sách,
/// lượt tìm không có từ khóa, lượt xem không có di sản, khóa ngoại), hai yêu cầu đầu tiên cùng lúc của một mã chỉ tạo một thiết bị, giới hạn 500 lượt và xóa quá hạn chạy được
/// bằng lệnh xóa hàng loạt, và cả luồng đăng ký, ghi, xem, gợi ý, gần bạn, xóa chạy đúng.
/// </summary>
public sealed class GuestSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly GuestTestKit _k = new(factory);

    private Task<List<T>> SqlAsync<T>(FormattableString sql) => factory.WithDbAsync(db => db.Database.SqlQuery<T>(sql).ToListAsync());

    private static async Task Refused(Func<Task> action, string constraint)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(action);
        var inner = error as SqlException ?? error.InnerException as SqlException;
        Assert.NotNull(inner);
        Assert.Contains(constraint, inner!.Message);
    }

    [LocalDbFact]
    public async Task The_database_itself_refuses_bad_guest_rows_and_applies_the_defaults()
    {
        _ = factory.Server;
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageAsync(admin, "Thành nhà Hồ");
        var now = DateTime.UtcNow;
        var key = NewKey();

        Task AddDevice(string deviceKey, string platform = "WEB", string language = "vi") => factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO guest_devices (device_key, platform, language_code, location_allowed, created_at) VALUES ({deviceKey}, {platform}, {language}, 0, {now})"));
        Task AddActivity(long deviceId, string type, long? heritageId, string? query, string language = "vi") => factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO guest_activities (guest_device_id, activity_type, heritage_id, search_query, language_code, created_at) VALUES ({deviceId}, {type}, {heritageId}, {query}, {language}, {now})"));

        await AddDevice(key);
        await Refused(() => AddDevice(key), "UX_guest_devices_device_key");
        await Refused(() => AddDevice(NewKey(), platform: "SYMBIAN"), "CK_guest_devices_platform");
        await Refused(() => AddDevice(NewKey(), language: "fr"), "CK_guest_devices_language_code");
        await Refused(() => AddDevice(NewKey(), language: "zh-Hans"), "CK_guest_devices_language_code");   // lưu mã "zh", không phải thẻ BCP 47

        // Cột mang giá trị mặc định khi không truyền.
        var deviceKey2 = NewKey();
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO guest_devices (device_key, platform, created_at) VALUES ({deviceKey2}, 'ANDROID', {now})"));
        var device = await factory.WithDbAsync(db => db.GuestDevices.AsNoTracking().SingleAsync(d => d.DeviceKey == deviceKey2));
        Assert.Equal((LanguageCode.vi, false, ClientPlatform.ANDROID), (device.LanguageCode, device.LocationAllowed, device.Platform));

        var deviceId = (await SqlAsync<long>($"SELECT id AS [Value] FROM guest_devices WHERE device_key = {key}")).Single();
        await AddActivity(deviceId, "VIEW", heritage.Id, null);
        await AddActivity(deviceId, "SEARCH", null, "thăng long");
        await AddActivity(deviceId, "VIEW_3D", heritage.Id, null, language: "ja");
        await Refused(() => AddActivity(deviceId, "SEARCH", null, null), "CK_guest_activities_target");        // tìm kiếm cần từ khóa
        await Refused(() => AddActivity(deviceId, "VIEW", null, null), "CK_guest_activities_target");         // xem cần di sản
        await Refused(() => AddActivity(deviceId, "LISTEN", null, "nghe"), "CK_guest_activities_target");
        await Refused(() => AddActivity(deviceId, "DANCE", heritage.Id, null), "CK_guest_activities_activity_type");
        await Refused(() => AddActivity(deviceId, "VIEW", heritage.Id, null, language: "fr"), "CK_guest_activities_language_code");
        await Refused(() => AddActivity(long.MaxValue, "VIEW", heritage.Id, null), "fk_guest_activities_guest_device_id");
        await Refused(() => AddActivity(deviceId, "VIEW", long.MaxValue, null), "fk_guest_activities_heritage_id");

        // Di sản còn lịch sử trỏ tới thì không xóa cứng được; thiết bị còn lịch sử cũng vậy.
        var bare = "tran-quoc-" + DiscoveryTestKit.Unique();
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO heritages (name, slug, status, created_at) VALUES (N'Trấn Quốc', {bare}, 'DRAFT', {now})"));
        var bareId = await factory.WithDbAsync(db => db.Heritages.Where(h => h.Slug == bare).Select(h => h.Id).SingleAsync());
        await AddActivity(deviceId, "VIEW", bareId, null);
        await Refused(() => factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM heritages WHERE id = {bareId}")), "fk_guest_activities_heritage_id");
        await Refused(() => factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM guest_devices WHERE id = {deviceId}")), "fk_guest_activities_guest_device_id");

        // Hình dạng cột và chỉ mục đúng thiết kế.
        Assert.Equal(["nvarchar(64)"], (await SqlAsync<string>($"""
            SELECT TYPE_NAME(c.user_type_id) + '(' + CAST(c.max_length / 2 AS varchar(10)) + ')' AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('guest_devices') AND c.name = 'device_key'
            """)));
        Assert.Equal(["nvarchar(255)"], (await SqlAsync<string>($"""
            SELECT TYPE_NAME(c.user_type_id) + '(' + CAST(c.max_length / 2 AS varchar(10)) + ')' AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('guest_activities') AND c.name = 'search_query'
            """)));
        Assert.Equal(["guest_device_id,created_at"], await SqlAsync<string>($"""
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS [Value]
            FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = 'IX_guest_activities_device_time'
            """));
        Assert.Equal(["device_key"], await SqlAsync<string>($"""
            SELECT c.name AS [Value] FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = 'UX_guest_devices_device_key' AND i.is_unique = 1 AND i.has_filter = 0
            """));
    }

    [LocalDbFact]
    public async Task Two_first_requests_of_the_same_key_at_once_create_one_device_and_both_succeed()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageAsync(admin, "Thành Cổ Loa");
        var registerKey = NewKey();
        var viewKey = NewKey();

        var registrations = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            _k.SendAsync<GuestDeviceDto>(HttpMethod.Post, $"{Root}/devices", registerKey, new { language = "en" })));
        Assert.All(registrations, r => Assert.True(r.Response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{(int)r.Response.StatusCode} {r.Body.ErrorCode}"));
        Assert.Equal(1, registrations.Count(r => r.Response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, await factory.WithDbAsync(db => db.GuestDevices.CountAsync(d => d.DeviceKey == registerKey)));

        // Ghi lịch sử tự động từ nhiều yêu cầu đầu tiên song song: không yêu cầu nào lỗi, vẫn một thiết bị.
        var views = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _k.GetAsync<object>($"{PublicRoot}/heritages/{heritage.Slug}", viewKey)));
        Assert.All(views, v => Assert.Equal(HttpStatusCode.OK, v.Response.StatusCode));
        Assert.Equal(1, await factory.WithDbAsync(db => db.GuestDevices.CountAsync(d => d.DeviceKey == viewKey)));
        Assert.InRange((await _k.ActivitiesAsync(viewKey)).Count, 1, 8);
    }

    [LocalDbFact]
    public async Task The_cap_of_500_and_the_ninety_day_purge_run_as_bulk_deletes_on_sql_server()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageAsync(admin, "Hồ Tây");
        var capped = NewKey();
        var keeps = NewKey();
        await _k.SeedAsync(capped, new Seed(GuestActivityType.VIEW, heritage.Id, Minutes(1)));
        await _k.SeedAsync(keeps, new Seed(GuestActivityType.VIEW, heritage.Id, Days(89)), new Seed(GuestActivityType.VIEW_3D, heritage.Id, Days(91)),
            new Seed(GuestActivityType.SEARCH, null, Days(300), "xưa"));
        var deviceId = (await SqlAsync<long>($"SELECT id AS [Value] FROM guest_devices WHERE device_key = {capped}")).Single();
        var now = DateTime.UtcNow;
        // 499 lượt nữa ghi thẳng bằng SQL: tổng đủ 500, lượt cũ nhất là lượt tìm "mốc 000".
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH n AS (SELECT TOP 499 CAST(ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS int) AS i FROM sys.all_objects a CROSS JOIN sys.all_columns b)
            INSERT INTO guest_activities (guest_device_id, activity_type, heritage_id, search_query, language_code, created_at)
            SELECT {deviceId}, 'SEARCH', NULL, N'mốc ' + RIGHT('000' + CAST(i - 1 AS varchar(3)), 3), 'vi', DATEADD(MINUTE, -1000 + i, {now})
            FROM n
            """));
        Assert.Equal(500, (await _k.ActivitiesAsync(capped)).Count);

        // Một lượt nữa qua API: lượt cũ nhất bị xóa, còn đúng 500.
        Assert.True((await _k.RecordAsync(capped, "LISTEN", heritage.Slug)).Recorded);
        var rows = await _k.ActivitiesAsync(capped);
        Assert.Equal(500, rows.Count);
        Assert.DoesNotContain(rows, r => r.SearchQuery == "mốc 000");
        Assert.Contains(rows, r => r.SearchQuery == "mốc 001");
        Assert.Contains(rows, r => r.ActivityType == GuestActivityType.LISTEN);

        // Xóa quá hạn: lượt 91 ngày và 300 ngày bị xóa, lượt 89 ngày còn.
        var deleted = await factory.WithDbAsync(async db => await new GuestService(db, null!, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GuestService>.Instance).PurgeExpiredAsync(default));
        Assert.True(deleted >= 2);
        Assert.Equal([GuestActivityType.VIEW], (await _k.ActivitiesAsync(keeps)).Select(a => a.ActivityType));
        Assert.Equal(500, (await _k.ActivitiesAsync(capped)).Count);
    }

    [LocalDbFact]
    public async Task The_whole_guest_flow_runs_on_sql_server()
    {
        var admin = await _k.AdminAsync();
        // Hai di sản thiên nhiên ở Cà Mau, xa mọi di sản của các test khác trong cùng CSDL (đều ở Hà Nội); một di sản UNESCO duy nhất làm "nổi bật".
        var hn1 = await _k.HeritageAsync(admin, "Vườn quốc gia Mũi Cà Mau", "NATURAL", "79", "PROVINCIAL", 9.1766m, 105.15m);
        var hn2 = await _k.HeritageAsync(admin, "Rừng U Minh Hạ", "NATURAL", "79", "PROVINCIAL", 9.2m, 105.2m);
        var hue = await _k.HeritageAsync(admin, "Nhã nhạc cung đình", "INTANGIBLE", "46", "UNESCO", null, null);
        var key = NewKey();

        // Đăng ký, đổi cài đặt.
        var (registered, device) = await _k.SendAsync<GuestDeviceDto>(HttpMethod.Post, $"{Root}/devices", key, new { language = "ko" }, platform: "IOS");
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Equal(("ko", ClientPlatform.IOS), (device.Data!.Language, device.Data.Platform));
        var settings = await _k.ExpectAsync(_k.SendAsync<GuestDeviceDto>(HttpMethod.Put, $"{Root}/device", key, new { language = "zh-Hans", locationAllowed = true }));
        Assert.Equal(("zh-Hans", true), (settings.Language, settings.LocationAllowed));

        // Tìm không dấu, xem, nghe: tự ghi và ghi tay.
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages?keyword={Uri.EscapeDataString("ca mau")}", key));
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{hn1.Slug}", key));
        Assert.True((await _k.RecordAsync(key, "LISTEN", hn1.Slug)).Recorded);
        Assert.False((await _k.RecordAsync(key, "LISTEN", hn1.Slug)).Recorded);

        var history = await _k.HistoryAsync(key);
        Assert.Equal([GuestActivityType.LISTEN, GuestActivityType.VIEW, GuestActivityType.SEARCH], history.Items.Select(i => i.Type));
        Assert.Equal("ca mau", history.Items[2].Query);

        // Gợi ý: Rừng U Minh Hạ giống Vườn quốc gia Mũi Cà Mau (cùng loại, tỉnh, cấp) đứng đầu; di sản vừa xem không tự gợi ý lại.
        var recommendations = await _k.RecommendationsAsync(key);
        Assert.True(recommendations.IsPersonalized);
        Assert.Equal(hn2.Slug, recommendations.Items[0].Heritage.Slug);
        Assert.DoesNotContain(hn1.Slug, recommendations.Items.Select(i => i.Heritage.Slug));

        // Gần bạn: chỉ di sản có tọa độ (Nhã nhạc không có), di sản gần hơn đứng trước.
        var nearby = await _k.NearbyAsync(9.175, 105.15);
        Assert.Equal([hn1.Slug, hn2.Slug], nearby.Items.Take(2).Select(i => i.Heritage.Slug));
        Assert.DoesNotContain(hue.Slug, nearby.Items.Select(i => i.Heritage.Slug));
        Assert.True(nearby.Items[0].DistanceMeters < nearby.Items[1].DistanceMeters);
        Assert.Equal(NearbyUnavailable.OutsideVietnam, (await _k.NearbyAsync(35.68, 139.69)).UnavailableReason);

        // Xóa từng mục rồi xóa hết: xóa thật, gợi ý quay về mặc định.
        await _k.ExpectAsync(_k.SendAsync<object>(HttpMethod.Delete, $"{Root}/history/{history.Items[0].Id}", key));
        Assert.Equal(2, (await _k.HistoryAsync(key)).TotalItems);
        Assert.Equal(2, (await _k.ExpectAsync(_k.SendAsync<GuestDeletedDto>(HttpMethod.Delete, $"{Root}/history", key))).Deleted);
        Assert.Equal(0, await factory.WithDbAsync(db => db.GuestActivities.CountAsync(a => a.GuestDevice!.DeviceKey == key)));
        var reset = await _k.RecommendationsAsync(key);
        Assert.False(reset.IsPersonalized);
        Assert.Equal(GuestMessages.NoHistoryYet, reset.Message);
        Assert.Equal([hue.Slug], reset.Items.Select(i => i.Heritage.Slug));       // di sản UNESCO duy nhất = nổi bật
    }
}
