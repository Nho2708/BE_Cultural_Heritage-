using System.Net;
using DSVHVN.Application.Ads;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.AdsTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Thống kê khách và lượt mở khóa cho quản trị hệ thống: chỉ ADMIN xem; đếm theo ngày (giờ Việt Nam), nền tảng, mạng quảng cáo, di sản; lượt bị từ chối theo lý do
/// (lần gọi lại trùng mã giao dịch không tính); khách hoạt động, ngôn ngữ, di sản được xem, nghe, xem 3D; khoảng ngày kiểm hợp lệ; không có doanh thu.
/// </summary>
public sealed class AdsStatsApiTests(AdsApiFactory factory) : IClassFixture<AdsApiFactory>
{
    private readonly AdsTestKit _k = new(factory);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow + VietnamTime.Offset);

    private async Task<AdStatsDto> StatsAsync(string admin, DateOnly? from = null, DateOnly? to = null)
    {
        var query = from is null && to is null ? "" : $"?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
        return await DiscoveryTestKit.Expect(_k.SendAsync<AdStatsDto>(HttpMethod.Get, AdsTestKit.StatsUrl + query, token: admin));
    }

    private static int Reason(AdStatsDto s, string reason) => s.Rejections.ByReason.Single(r => r.Reason == reason).Count;

    [Fact]
    public async Task Only_the_system_admin_reads_the_statistics()
    {
        var (response, _) = await _k.SendAsync<object>(HttpMethod.Get, AdsTestKit.StatsUrl);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var lessons = new LessonTestKit(factory);
        var (school, _) = await lessons.SchoolWithClassAsync(2);
        var (teacher, _) = await _k.SendAsync<object>(HttpMethod.Get, AdsTestKit.StatsUrl, token: school.TeacherToken);
        Assert.Equal(HttpStatusCode.Forbidden, teacher.StatusCode);
        var (orgAdmin, _) = await _k.SendAsync<object>(HttpMethod.Get, AdsTestKit.StatsUrl, token: school.OrgAdminToken);
        Assert.Equal(HttpStatusCode.Forbidden, orgAdmin.StatusCode);
    }

    [Fact]
    public async Task Unlocks_are_counted_by_day_platform_network_and_heritage_and_there_is_no_revenue()
    {
        var admin = await _k.AdminAsync();
        var first = await _k.HeritageWithoutSummaryAsync(admin);
        var second = await _k.HeritageWithoutSummaryAsync(admin);
        var from = Today.AddDays(-5);
        var before = await StatsAsync(admin, from, Today);

        var now = DateTime.UtcNow;
        await _k.SeedUnlockAsync(NewKey(), first.Id, now, AdNetwork.ADMOB, ClientPlatform.ANDROID);
        await _k.SeedUnlockAsync(NewKey(), first.Id, now, AdNetwork.ADMOB, ClientPlatform.IOS);
        await _k.SeedUnlockAsync(NewKey(), first.Id, now.AddDays(-2), AdNetwork.AD_MANAGER, ClientPlatform.WEB, adUnit: AdsApiFactory.WebUnit);
        await _k.SeedUnlockAsync(NewKey(), second.Id, now.AddDays(-2), AdNetwork.ADMOB, ClientPlatform.ANDROID);
        await _k.SeedUnlockAsync(NewKey(), second.Id, now.AddDays(-40), AdNetwork.ADMOB, ClientPlatform.ANDROID);   // ngoài khoảng

        var after = await StatsAsync(admin, from, Today);

        Assert.Equal(4, after.Unlocks.Total - before.Unlocks.Total);
        Assert.Equal(6, after.Unlocks.ByDay.Count);                                    // đủ từng ngày, kể cả ngày không có lượt
        Assert.Equal(from, after.Unlocks.ByDay[0].Date);
        Assert.Equal(Today, after.Unlocks.ByDay[^1].Date);
        Assert.Equal(2, DayCount(after, Today) - DayCount(before, Today));
        Assert.Equal(2, DayCount(after, Today.AddDays(-2)) - DayCount(before, Today.AddDays(-2)));
        Assert.Equal(0, DayCount(after, Today.AddDays(-4)) - DayCount(before, Today.AddDays(-4)));
        Assert.Equal(after.Unlocks.Total, after.Unlocks.ByDay.Sum(d => d.Count));

        Assert.Equal(2, PlatformCount(after, ClientPlatform.ANDROID) - PlatformCount(before, ClientPlatform.ANDROID));
        Assert.Equal(1, PlatformCount(after, ClientPlatform.IOS) - PlatformCount(before, ClientPlatform.IOS));
        Assert.Equal(1, PlatformCount(after, ClientPlatform.WEB) - PlatformCount(before, ClientPlatform.WEB));

        Assert.Equal(3, NetworkCount(after, AdNetwork.ADMOB) - NetworkCount(before, AdNetwork.ADMOB));
        Assert.Equal(1, NetworkCount(after, AdNetwork.AD_MANAGER) - NetworkCount(before, AdNetwork.AD_MANAGER));
        var admob = after.Unlocks.ByNetwork.Single(n => n.Network == AdNetwork.ADMOB);
        var manager = after.Unlocks.ByNetwork.Single(n => n.Network == AdNetwork.AD_MANAGER);
        Assert.Equal(("SERVER", "BROWSER"), (admob.Source, manager.Source));
        Assert.NotEqual(admob.SourceLabel, manager.SourceLabel);

        var topFirst = after.Unlocks.TopHeritages.Single(h => h.HeritageId == first.Id);
        var topSecond = after.Unlocks.TopHeritages.Single(h => h.HeritageId == second.Id);
        Assert.Equal((3, 1, first.Name, first.Slug), (topFirst.Count, topSecond.Count, topFirst.Name, topFirst.Slug));
        Assert.True(after.Unlocks.TopHeritages.Count <= AdRules.TopLimit);
        Assert.Equal(after.Unlocks.TopHeritages.OrderByDescending(h => h.Count).Select(h => h.Count), after.Unlocks.TopHeritages.Select(h => h.Count));

        // Không có doanh thu: chỉ ghi chú và liên kết sang bảng điều khiển của Google.
        Assert.Contains("AdMob", after.Note);
        Assert.Contains("Ad Manager", after.Note);
        Assert.StartsWith("https://", after.AdMobConsoleUrl);
        Assert.StartsWith("https://", after.AdManagerConsoleUrl);
    }

    [Fact]
    public async Task Rejections_are_counted_by_reason_and_a_resent_transaction_is_not_one_of_them()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var from = Today.AddDays(-1);
        var before = await StatsAsync(admin, from, Today);

        await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, mutate: q => q.Replace("reward_amount=1", "reward_amount=2")));
        await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, keyId: 1));
        await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, adUnit: "999"));
        await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, leaveOutUserId: true));
        await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, customData: "abc"));
        await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, at: DateTime.UtcNow.AddHours(-3)));
        await _k.ReportWebRawAsync(heritage.Slug, "ma-sai", AdsApiFactory.WebUnit, device);

        // Một lượt hợp lệ rồi gửi lại: không tăng số lượt bị từ chối.
        var transaction = Guid.NewGuid().ToString("N");
        await _k.UnlockViaAdMobAsync(heritage.Id, device, transactionId: transaction);
        await _k.UnlockViaAdMobAsync(heritage.Id, device, transactionId: transaction);

        var after = await StatsAsync(admin, from, Today);
        Assert.Equal(7, after.Rejections.Total - before.Rejections.Total);
        Assert.Equal(2, Reason(after, AdRules.RejectedBadSignature) - Reason(before, AdRules.RejectedBadSignature));
        Assert.Equal(1, Reason(after, AdRules.RejectedUnknownAdUnit) - Reason(before, AdRules.RejectedUnknownAdUnit));
        Assert.Equal(2, Reason(after, AdRules.RejectedBadParameter) - Reason(before, AdRules.RejectedBadParameter));
        Assert.Equal(1, Reason(after, AdRules.RejectedStaleTimestamp) - Reason(before, AdRules.RejectedStaleTimestamp));
        Assert.Equal(1, Reason(after, AdRules.RejectedBadWebTransaction) - Reason(before, AdRules.RejectedBadWebTransaction));
        Assert.Equal(0, Reason(after, AdRules.RejectedRateLimited) - Reason(before, AdRules.RejectedRateLimited));

        Assert.Equal(AdRules.RejectionReasons, after.Rejections.ByReason.Select(r => r.Reason));
        Assert.All(after.Rejections.ByReason, r => Assert.Equal(AdRules.RejectionLabel(r.Reason), r.Label));
        Assert.Equal(1, after.Unlocks.Total - before.Unlocks.Total);
    }

    [Fact]
    public async Task Guest_statistics_count_devices_languages_and_the_most_viewed_listened_and_3D_heritages()
    {
        var admin = await _k.AdminAsync();
        var popular = await _k.HeritageWithoutSummaryAsync(admin);
        var quiet = await _k.HeritageWithoutSummaryAsync(admin);
        var from = Today.AddDays(-2);
        var before = await StatsAsync(admin, from, Today);

        var a = NewKey();
        var b = NewKey();
        var c = NewKey();
        await _k.Guests.SeedAsync(a,
            new GuestTestKit.Seed(GuestActivityType.VIEW, popular.Id, TimeSpan.FromHours(1)),
            new GuestTestKit.Seed(GuestActivityType.LISTEN, popular.Id, TimeSpan.FromHours(1), Language: LanguageCode.ko),
            new GuestTestKit.Seed(GuestActivityType.VIEW_3D, popular.Id, TimeSpan.FromHours(1)));
        await _k.Guests.SeedAsync(b,
            new GuestTestKit.Seed(GuestActivityType.VIEW, popular.Id, TimeSpan.FromHours(2), Language: LanguageCode.en),
            new GuestTestKit.Seed(GuestActivityType.VIEW, quiet.Id, TimeSpan.FromHours(2), Language: LanguageCode.en),
            new GuestTestKit.Seed(GuestActivityType.LISTEN, popular.Id, TimeSpan.FromHours(2), Language: LanguageCode.en));
        await _k.Guests.SeedAsync(c, new GuestTestKit.Seed(GuestActivityType.VIEW, popular.Id, TimeSpan.FromDays(10)));   // ngoài khoảng
        await factory.WithDbAsync(db => db.GuestDevices.Where(d => d.DeviceKey == c).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, DateTime.UtcNow.AddDays(-10))));

        var after = await StatsAsync(admin, from, Today);

        Assert.Equal(2, after.Guests.ActiveDevices - before.Guests.ActiveDevices);
        Assert.Equal(2, HeritageCount(after.Guests.MostViewed, popular.Id));
        Assert.Equal(1, HeritageCount(after.Guests.MostViewed, quiet.Id));
        Assert.Equal(2, HeritageCount(after.Guests.MostListened, popular.Id));
        Assert.Equal(1, HeritageCount(after.Guests.MostViewed3D, popular.Id));
        Assert.Equal(popular.Name, after.Guests.MostViewed.Single(h => h.HeritageId == popular.Id).Name);
        Assert.Equal(1, LanguageDevices(after, "ko") - LanguageDevices(before, "ko"));
        Assert.Equal(1, LanguageDevices(after, "en") - LanguageDevices(before, "en"));
        Assert.Equal(1, LanguageDevices(after, "vi") - LanguageDevices(before, "vi"));
        Assert.True(after.Guests.MostViewed.Count <= AdRules.TopLimit);
    }

    [Fact]
    public async Task The_range_defaults_to_the_last_30_days_and_bad_ranges_are_refused()
    {
        var admin = await _k.AdminAsync();
        var stats = await StatsAsync(admin);
        Assert.Equal((Today.AddDays(-29), Today), (stats.From, stats.To));
        Assert.Equal(30, stats.Unlocks.ByDay.Count);

        var (reversed, reversedBody) = await _k.SendAsync<object>(HttpMethod.Get, $"{AdsTestKit.StatsUrl}?from={Today:yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}", token: admin);
        Assert.Equal((HttpStatusCode.BadRequest, MessageCodes.Invalid), (reversed.StatusCode, reversedBody.ErrorCode));

        var (tooLong, _) = await _k.SendAsync<object>(HttpMethod.Get, $"{AdsTestKit.StatsUrl}?from={Today.AddDays(-366):yyyy-MM-dd}&to={Today:yyyy-MM-dd}", token: admin);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var full = await StatsAsync(admin, Today.AddDays(-365), Today);
        Assert.Equal(366, full.Unlocks.ByDay.Count);

        var (garbage, _) = await _k.SendAsync<object>(HttpMethod.Get, $"{AdsTestKit.StatsUrl}?from=hom-qua", token: admin);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    private static int DayCount(AdStatsDto s, DateOnly day) => s.Unlocks.ByDay.Single(d => d.Date == day).Count;

    private static int PlatformCount(AdStatsDto s, ClientPlatform platform) => s.Unlocks.ByPlatform.SingleOrDefault(p => p.Platform == platform)?.Count ?? 0;

    private static int NetworkCount(AdStatsDto s, AdNetwork network) => s.Unlocks.ByNetwork.SingleOrDefault(n => n.Network == network)?.Count ?? 0;

    private static int HeritageCount(IReadOnlyList<AdHeritageCountDto> list, long id) => list.SingleOrDefault(h => h.HeritageId == id)?.Count ?? 0;

    private static int LanguageDevices(AdStatsDto s, string language) => s.Guests.ByLanguage.SingleOrDefault(l => l.Language == language)?.Devices ?? 0;
}
