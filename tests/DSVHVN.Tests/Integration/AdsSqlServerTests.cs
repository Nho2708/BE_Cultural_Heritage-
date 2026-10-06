using System.Net;
using DSVHVN.Application.Ads;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DSVHVN.Tests.Integration.AdsTestKit;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Quảng cáo có thưởng trên SQL Server thật (database tạm của LocalDB, tạo bằng migration): CSDL tự từ chối dòng sai (mã giao dịch trùng trong một mạng, mạng và nền tảng không khớp,
/// hạn không sau thời điểm mở khóa, giá trị ngoài danh sách, khóa ngoại), mã giao dịch cho phép trùng ở hai mạng khác nhau, Google gọi đồng thời cùng một giao dịch chỉ ghi một dòng,
/// báo thưởng của web, cổng và thống kê chạy đúng với câu lệnh SQL thật.
/// </summary>
public sealed class AdsSqlServerTests(AdsSqlServerApiFactory factory) : IClassFixture<AdsSqlServerApiFactory>
{
    private readonly AdsTestKit _k = new(factory);

    private Task<List<T>> SqlAsync<T>(FormattableString sql) => factory.WithDbAsync(db => db.Database.SqlQuery<T>(sql).ToListAsync());

    private static async Task Refused(Func<Task> action, string constraint)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(action);
        var inner = error as SqlException ?? error.InnerException as SqlException;
        Assert.NotNull(inner);
        Assert.Contains(constraint, inner!.Message);
    }

    [LocalDbFact]
    public async Task The_database_itself_refuses_bad_unlock_rows()
    {
        _ = factory.Server;
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var row = await _k.SeedUnlockAsync(device, heritage.Id, DateTime.UtcNow);
        var deviceId = row.GuestDeviceId;
        var now = DateTime.UtcNow;

        Task Add(long deviceKeyId, long heritageId, string platform, string network, string transaction, DateTime unlockedAt, DateTime expiresAt) =>
            factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO ad_unlocks (guest_device_id, heritage_id, platform, ad_network, transaction_id, unlocked_at, expires_at) VALUES ({deviceKeyId}, {heritageId}, {platform}, {network}, {transaction}, {unlockedAt}, {expiresAt})"));

        // Cùng mạng, cùng mã giao dịch: từ chối. Khác mạng thì được (mã chỉ duy nhất trong một mạng).
        await Refused(() => Add(deviceId, heritage.Id, "ANDROID", "ADMOB", row.TransactionId, now, now.AddHours(24)), "UX_ad_unlocks_network_transaction");
        await Add(deviceId, heritage.Id, "WEB", "AD_MANAGER", row.TransactionId, now, now.AddHours(24));

        // AdMob chỉ ở Android hoặc iOS, Ad Manager chỉ ở web.
        await Refused(() => Add(deviceId, heritage.Id, "WEB", "ADMOB", "t1" + Unique(), now, now.AddHours(24)), "CK_ad_unlocks_network_platform");
        await Refused(() => Add(deviceId, heritage.Id, "ANDROID", "AD_MANAGER", "t2" + Unique(), now, now.AddHours(24)), "CK_ad_unlocks_network_platform");
        await Refused(() => Add(deviceId, heritage.Id, "IOS", "AD_MANAGER", "t3" + Unique(), now, now.AddHours(24)), "CK_ad_unlocks_network_platform");

        // Hạn phải sau thời điểm mở khóa.
        await Refused(() => Add(deviceId, heritage.Id, "ANDROID", "ADMOB", "t4" + Unique(), now, now), "CK_ad_unlocks_expiry");
        await Refused(() => Add(deviceId, heritage.Id, "ANDROID", "ADMOB", "t5" + Unique(), now, now.AddSeconds(-1)), "CK_ad_unlocks_expiry");

        // Giá trị ngoài danh sách và khóa ngoại.
        await Refused(() => Add(deviceId, heritage.Id, "SYMBIAN", "ADMOB", "t6" + Unique(), now, now.AddHours(24)), "CK_ad_unlocks_");
        await Refused(() => Add(deviceId, heritage.Id, "ANDROID", "FACEBOOK", "t7" + Unique(), now, now.AddHours(24)), "CK_ad_unlocks_");
        await Refused(() => Add(987654321, heritage.Id, "ANDROID", "ADMOB", "t8" + Unique(), now, now.AddHours(24)), "fk_ad_unlocks_guest_device_id");
        await Refused(() => Add(deviceId, 987654321, "ANDROID", "ADMOB", "t9" + Unique(), now, now.AddHours(24)), "fk_ad_unlocks_heritage_id");

        // Cột bắt buộc: mã giao dịch không được trống.
        await Refused(() => factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ad_unlocks (guest_device_id, heritage_id, platform, ad_network, transaction_id, unlocked_at, expires_at) VALUES ({deviceId}, {heritage.Id}, 'ANDROID', 'ADMOB', NULL, {now}, {now.AddHours(1)})")), "transaction_id");

        // Kiểu cột đúng như CSDL v4.
        var columns = await SqlAsync<string>($@"SELECT COLUMN_NAME + ':' + DATA_TYPE + ':' + COALESCE(CAST(CHARACTER_MAXIMUM_LENGTH AS varchar(10)), '-') + ':' + IS_NULLABLE AS [Value]
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'ad_unlocks' ORDER BY ORDINAL_POSITION");
        Assert.Equal(
        [
            "id:bigint:-:NO", "guest_device_id:bigint:-:NO", "heritage_id:bigint:-:NO", "platform:nvarchar:20:NO", "ad_network:nvarchar:20:NO",
            "transaction_id:nvarchar:255:NO", "ad_unit_id:nvarchar:100:YES", "unlocked_at:datetime2:-:NO", "expires_at:datetime2:-:NO",
        ], columns);

        var indexes = await SqlAsync<string>($@"SELECT name AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID('ad_unlocks') AND name IS NOT NULL");
        Assert.Contains("UX_ad_unlocks_network_transaction", indexes);
        Assert.Contains("IX_ad_unlocks_device_heritage", indexes);
    }

    [LocalDbFact]
    public async Task Google_calling_at_the_same_moment_with_the_same_transaction_writes_one_row_and_every_call_succeeds()
    {
        _ = factory.Server;
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var url = _k.Ssv(heritage.Id, device, Guid.NewGuid().ToString("N"));

        var calls = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _k.CallSsvAsync(url)));

        Assert.All(calls, c => Assert.Equal(HttpStatusCode.OK, c.Response.StatusCode));
        Assert.Single(await _k.UnlocksAsync(device));
        Assert.Equal(1, calls.Count(c => c.Body.Data!.Recorded));
        Assert.Equal(7, calls.Count(c => !c.Body.Data!.Recorded));
        Assert.All(calls.Where(c => !c.Body.Data!.Recorded), c => Assert.Contains(c.Body.Data!.Reason, new[] { "DUPLICATE", "ALREADY_UNLOCKED" }));
    }

    [LocalDbFact]
    public async Task Different_devices_calling_at_once_with_different_transactions_each_get_their_own_unlock_and_one_device_row()
    {
        _ = factory.Server;
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var devices = Enumerable.Range(0, 6).Select(_ => NewKey()).ToList();

        var calls = await Task.WhenAll(devices.Select(d => _k.CallSsvAsync(_k.Ssv(heritage.Id, d))));

        Assert.All(calls, c => Assert.True(c.Body.Data!.Recorded));
        foreach (var device in devices)
        {
            Assert.Single(await _k.UnlocksAsync(device));
            Assert.Equal(1, await factory.WithDbAsync(db => db.GuestDevices.CountAsync(d => d.DeviceKey == device)));
        }
    }

    [LocalDbFact]
    public async Task The_gate_the_web_flow_and_the_limit_work_on_SQL_Server()
    {
        _ = factory.Server;
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var device = NewKey();

        await _k.AssertSummaryLockedAsync(heritage.Slug!, device);
        Assert.True((await _k.UnlockViaWebAsync(heritage.Slug!, device)).Recorded);
        Assert.Equal(heritage.Name, (await _k.SummaryAsync(heritage.Slug!, device)).Name);

        var row = Assert.Single(await _k.UnlocksAsync(device));
        Assert.Equal((AdNetwork.AD_MANAGER, ClientPlatform.WEB, TimeSpan.FromHours(24)), (row.AdNetwork, row.Platform, row.ExpiresAt - row.UnlockedAt));
        Assert.Equal(DateTimeKind.Utc, row.UnlockedAt.Kind);

        // Hết hạn theo giờ máy chủ: sửa hạn về quá khứ thì cổng đóng lại (so sánh thời gian trên SQL Server).
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ad_unlocks SET unlocked_at = {DateTime.UtcNow.AddHours(-30)}, expires_at = {DateTime.UtcNow.AddHours(-6)} WHERE id = {row.Id}"));
        await _k.AssertSummaryLockedAsync(heritage.Slug!, device);

        // Hạn mở khóa sau lúc kiểm: mở lại bằng xác minh của AdMob.
        Assert.True((await _k.UnlockViaAdMobAsync(heritage.Id, device)).Recorded);
        await _k.SummaryAsync(heritage.Slug!, device);
        Assert.Equal(2, (await _k.UnlocksAsync(device)).Count);
    }

    [LocalDbFact]
    public async Task Statistics_run_on_SQL_Server_with_the_same_numbers_as_the_rows_written()
    {
        _ = factory.Server;
        var admin = await _k.AdminAsync();
        var first = await _k.HeritageWithoutSummaryAsync(admin);
        var second = await _k.HeritageWithoutSummaryAsync(admin);
        var today = DateOnly.FromDateTime(DateTime.UtcNow + VietnamTime.Offset);
        var url = $"{AdsTestKit.StatsUrl}?from={today.AddDays(-3):yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        var before = await DiscoveryTestKit.Expect(_k.SendAsync<AdStatsDto>(HttpMethod.Get, url, token: admin));

        await _k.UnlockViaAdMobAsync(first.Id, NewKey());
        await _k.UnlockViaAdMobAsync(first.Id, NewKey(), adUnit: "1712485313");
        await _k.UnlockViaWebAsync(second.Slug!, NewKey());
        await _k.CallSsvAsync(_k.Ssv(first.Id, NewKey(), adUnit: "999"));
        await _k.CallSsvAsync(_k.Ssv(first.Id, NewKey(), mutate: q => q.Replace("reward_amount=1", "reward_amount=3")));
        await _k.Guests.SeedAsync(NewKey(), new GuestTestKit.Seed(GuestActivityType.VIEW_3D, first.Id, TimeSpan.FromMinutes(5)));

        var after = await DiscoveryTestKit.Expect(_k.SendAsync<AdStatsDto>(HttpMethod.Get, url, token: admin));

        Assert.Equal(3, after.Unlocks.Total - before.Unlocks.Total);
        Assert.Equal(2, after.Rejections.Total - before.Rejections.Total);
        Assert.Equal(1, after.Rejections.ByReason.Single(r => r.Reason == AdRules.RejectedUnknownAdUnit).Count - before.Rejections.ByReason.Single(r => r.Reason == AdRules.RejectedUnknownAdUnit).Count);
        Assert.Equal(1, after.Rejections.ByReason.Single(r => r.Reason == AdRules.RejectedBadSignature).Count - before.Rejections.ByReason.Single(r => r.Reason == AdRules.RejectedBadSignature).Count);
        Assert.Equal(2, after.Unlocks.TopHeritages.Single(h => h.HeritageId == first.Id).Count);
        Assert.Equal(1, after.Unlocks.TopHeritages.Single(h => h.HeritageId == second.Id).Count);
        Assert.Equal(3, after.Unlocks.ByPlatform.Sum(p => p.Count) - before.Unlocks.ByPlatform.Sum(p => p.Count));
        Assert.Equal(1, after.Guests.MostViewed3D.Single(h => h.HeritageId == first.Id).Count);
        Assert.Equal(after.Unlocks.Total, after.Unlocks.ByDay.Sum(d => d.Count));
    }

    [LocalDbFact]
    public async Task Purging_old_unlocks_runs_as_a_bulk_delete_on_SQL_Server()
    {
        _ = factory.Server;
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var old = await _k.SeedUnlockAsync(device, heritage.Id, DateTime.UtcNow.AddMonths(-13));
        var recent = await _k.SeedUnlockAsync(device, heritage.Id, DateTime.UtcNow.AddDays(-1));

        using var scope = factory.Services.CreateScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<AdUnlockService>().PurgeExpiredAsync(default);

        Assert.True(deleted >= 1);
        Assert.Equal([recent.Id], (await _k.UnlocksAsync(device)).Select(u => u.Id).ToList());
        Assert.DoesNotContain(await _k.UnlocksAsync(device), u => u.Id == old.Id);
    }
}
