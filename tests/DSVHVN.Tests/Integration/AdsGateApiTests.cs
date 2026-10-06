using System.Net;
using DSVHVN.Application.Ads;
using DSVHVN.Application.Common;
using DSVHVN.Application.Guests;
using DSVHVN.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using static DSVHVN.Tests.Integration.AdsTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Cổng quảng cáo qua API thật (SQLite, đơn vị quảng cáo thử của Google, khóa xác minh tự sinh): tóm tắt và nghe thuyết minh của khách bị khóa tới khi Google xác nhận phần thưởng;
/// mở theo đúng thiết bị, đúng di sản, mọi ngôn ngữ, tới hết 24 giờ; học sinh, cổng tắt và khai báo "không có quảng cáo" mở thẳng; di sản chưa có tóm tắt không hiện cổng.
/// </summary>
public sealed class AdsGateApiTests(AdsApiFactory factory) : IClassFixture<AdsApiFactory>
{
    private readonly AdsTestKit _k = new(factory);

    [Fact]
    public async Task Config_tells_the_clients_the_gate_the_units_and_the_content_rating()
    {
        var config = await DiscoveryTestKit.Expect(_k.SendAsync<AdConfigDto>(HttpMethod.Get, $"{AdsTestKit.AdsRoot}/config"));
        Assert.Equal((true, 24, "G", AdsApiFactory.AndroidUnit, AdsApiFactory.IosUnit, AdsApiFactory.WebUnit, false),
            (config.GateActive, config.UnlockHours, config.MaxContentRating, config.AndroidAdUnitId, config.IosAdUnitId, config.WebAdUnitPath, config.AllowNoAdsDeclaration));
    }

    [Fact]
    public async Task A_guest_cannot_read_the_summary_until_Google_confirms_the_reward_and_then_only_on_that_device_for_that_heritage()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var other = await _k.HeritageWithSummaryAsync(admin);
        var device = NewKey();
        var slug = heritage.Slug!;

        // Chưa mở khóa: 403 kèm lời mời xem quảng cáo, không có nội dung.
        var (response, body) = await _k.SummaryRawAsync(slug, device);
        Assert.Equal((HttpStatusCode.Forbidden, AdMessages.UnlockRequiredCode), (response.StatusCode, body.ErrorCode));
        Assert.Contains($"\"{heritage.Name}\"", body.Message);
        Assert.Contains("24 giờ", body.Message);
        Assert.Null(body.Data);

        var locked = await _k.StatusAsync(slug, device);
        Assert.Equal((true, false, null, null), (locked.GateRequired, locked.Unlocked, locked.ExpiresAt, locked.OpenReason));

        // Không gửi mã thiết bị cũng bị khóa (không có gì để mở khóa cho).
        await _k.AssertSummaryLockedAsync(slug, null);

        // Google xác nhận phần thưởng: mở khóa đúng di sản trên đúng thiết bị.
        var unlock = await _k.UnlockViaAdMobAsync(heritage.Id, device);
        Assert.True(unlock.Recorded);

        var (opened, openedBody) = await _k.SummaryRawAsync(slug, device);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Contains("no-store", opened.Headers.CacheControl!.ToString());   // nội dung phụ thuộc thiết bị nên không được lưu đệm
        var summary = openedBody.Data!;
        Assert.Equal(heritage.Name, summary.Name);
        Assert.False(string.IsNullOrWhiteSpace(summary.ShortText));
        Assert.False(string.IsNullOrWhiteSpace(summary.DetailedText));

        var open = await _k.StatusAsync(slug, device);
        Assert.Equal((true, true, unlock.ExpiresAt, null), (open.GateRequired, open.Unlocked, open.ExpiresAt, open.OpenReason));

        // Mở khóa không chuyển sang thiết bị khác hay di sản khác.
        await _k.AssertSummaryLockedAsync(slug, NewKey());
        await _k.AssertSummaryLockedAsync(other.Slug!, device);
        Assert.False((await _k.StatusAsync(other.Slug!, device)).Unlocked);
    }

    [Fact]
    public async Task One_unlock_covers_every_language_and_the_other_free_content_never_needed_one()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var device = NewKey();
        var slug = heritage.Slug!;

        // Nội dung khác miễn phí: chi tiết, dòng thời gian, tìm kiếm, bản đồ, mô hình 3D (không có thì 404 riêng), gợi ý.
        Assert.Equal(HttpStatusCode.OK, (await _k.SendAsync<object>(HttpMethod.Get, $"{PublicRoot}/heritages/{slug}", device)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _k.SendAsync<object>(HttpMethod.Get, $"{PublicRoot}/heritages/{slug}/timeline", device)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _k.SendAsync<object>(HttpMethod.Get, $"{PublicRoot}/heritages?pageSize=5", device)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _k.SendAsync<object>(HttpMethod.Get, $"{PublicRoot}/home", device)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _k.SendAsync<object>(HttpMethod.Get, $"{PublicRoot}/guest/recommendations", device)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.SendAsync<object>(HttpMethod.Get, $"{PublicRoot}/heritages/{slug}/model", device)).Response.StatusCode);

        await _k.AssertSummaryLockedAsync(slug, device, "ko");
        await _k.UnlockViaAdMobAsync(heritage.Id, device);

        foreach (var language in new[] { "vi", "en", "ko", "zh-Hans", "ja" })
            Assert.Equal(language, (await _k.SummaryAsync(slug, device, language)).Localization.RequestedLanguage);
        // Đổi ngôn ngữ không phải xem lại quảng cáo: mọi ngôn ngữ đều 200 với cùng một lượt mở khóa (bản tiếng Anh đã duyệt hiện đúng bản tiếng Anh).
        Assert.Equal("en", (await _k.SummaryAsync(slug, device, "en")).Localization.Language);
        Assert.Single(await _k.UnlocksAsync(device));
    }

    [Fact]
    public async Task An_unlock_works_until_it_expires_by_the_server_clock()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var stillOpen = NewKey();
        var expired = NewKey();
        await _k.SeedUnlockAsync(stillOpen, heritage.Id, DateTime.UtcNow.AddHours(-23).AddMinutes(-50));
        await _k.SeedUnlockAsync(expired, heritage.Id, DateTime.UtcNow.AddHours(-24).AddMinutes(-1));

        await _k.SummaryAsync(heritage.Slug!, stillOpen);
        await _k.AssertSummaryLockedAsync(heritage.Slug!, expired);
        Assert.False((await _k.StatusAsync(heritage.Slug!, expired)).Unlocked);

        // Hết hạn thì xem lại quảng cáo được và mở tiếp 24 giờ.
        var again = await _k.UnlockViaAdMobAsync(heritage.Id, expired);
        Assert.True(again.Recorded);
        await _k.SummaryAsync(heritage.Slug!, expired);
        Assert.Equal(2, (await _k.UnlocksAsync(expired)).Count);
    }

    [Fact]
    public async Task Clearing_the_history_does_not_take_back_an_unlock_that_is_still_valid()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var device = NewKey();
        await _k.UnlockViaAdMobAsync(heritage.Id, device);
        await _k.SummaryAsync(heritage.Slug!, device);

        Assert.Equal(HttpStatusCode.OK, (await _k.SendAsync<GuestDeletedDto>(HttpMethod.Delete, $"{PublicRoot}/guest/history", device)).Response.StatusCode);
        await _k.SummaryAsync(heritage.Slug!, device);
    }

    [Fact]
    public async Task A_signed_in_student_never_sees_the_gate_whatever_the_request_claims()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var lessons = new LessonTestKit(factory);
        var (school, schoolClass) = await lessons.SchoolWithClassAsync(2);
        var student = await lessons.StudentTokenAsync(school.TeacherToken, schoolClass.Id);

        var summary = await _k.SummaryAsync(heritage.Slug!, null, token: student);
        Assert.Equal(heritage.Name, summary.Name);
        await _k.SummaryAsync(heritage.Slug!, NewKey(), token: student);

        var status = await _k.StatusAsync(heritage.Slug!, NewKey(), token: student);
        Assert.Equal((false, true, "STUDENT"), (status.GateRequired, status.Unlocked, status.OpenReason));

        // Học sinh không dùng đường ghi nhận quảng cáo của khách.
        var (startResponse, start) = await _k.StartWebRawAsync(heritage.Slug, NewKey(), student);
        Assert.Equal((HttpStatusCode.Forbidden, MessageCodes.GuestOnly), (startResponse.StatusCode, start.ErrorCode));
        var (reportResponse, report) = await _k.ReportWebRawAsync(heritage.Slug, "x", AdsApiFactory.WebUnit, NewKey(), student);
        Assert.Equal((HttpStatusCode.Forbidden, MessageCodes.GuestOnly), (reportResponse.StatusCode, report.ErrorCode));
    }

    [Fact]
    public async Task A_heritage_with_no_summary_shows_no_gate_and_nothing_is_unlocked()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var before = await _k.UnlockCountAsync();

        var (response, body) = await _k.SummaryRawAsync(heritage.Slug!, device);
        Assert.Equal((HttpStatusCode.NotFound, MessageCodes.SummaryUnavailable), (response.StatusCode, body.ErrorCode));
        Assert.Equal(before, await _k.UnlockCountAsync());
    }

    [Fact]
    public async Task Listening_is_gated_like_the_summary_and_nothing_else_in_the_history_is()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var noSummary = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var slug = heritage.Slug!;

        // Nghe thuyết minh chưa mở khóa: 403, lượt không được ghi.
        var (locked, lockedBody) = await _k.SendAsync<GuestActivityResultDto>(HttpMethod.Post, $"{PublicRoot}/guest/activities", device, new { type = "LISTEN", slug });
        Assert.Equal((HttpStatusCode.Forbidden, AdMessages.UnlockRequiredCode), (locked.StatusCode, lockedBody.ErrorCode));
        Assert.DoesNotContain(await _k.Guests.ActivitiesAsync(device), a => a.ActivityType == GuestActivityType.LISTEN);

        // Xem, xem 3D và các lượt khác của lịch sử không bị chặn.
        Assert.True((await _k.Guests.RecordAsync(device, "VIEW", slug)).Recorded);
        Assert.True((await _k.Guests.RecordAsync(device, "VIEW_3D", slug)).Recorded);

        // Di sản chưa có tóm tắt thì không có gì để nghe và không hiện cổng.
        Assert.True((await _k.Guests.RecordAsync(device, "LISTEN", noSummary.Slug)).Recorded);

        await _k.UnlockViaAdMobAsync(heritage.Id, device);
        Assert.True((await _k.Guests.RecordAsync(device, "LISTEN", slug)).Recorded);
        Assert.Contains(await _k.Guests.ActivitiesAsync(device), a => a.ActivityType == GuestActivityType.LISTEN && a.HeritageId == heritage.Id);

        // Di sản không có thì vẫn là 404 của việc ghi lượt, không phải cổng.
        var (missing, _) = await _k.SendAsync<object>(HttpMethod.Post, $"{PublicRoot}/guest/activities", device, new { type = "LISTEN", slug = "khong-co-di-san-nay" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_no_ads_declaration_is_ignored_when_the_environment_does_not_allow_it()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var device = NewKey();

        var (response, body) = await _k.SummaryRawAsync(heritage.Slug!, device, noAds: true);
        Assert.Equal((HttpStatusCode.Forbidden, AdMessages.UnlockRequiredCode), (response.StatusCode, body.ErrorCode));
        Assert.True((await _k.StatusAsync(heritage.Slug!, device, noAds: true)).GateRequired);
    }

    [Fact]
    public async Task Moving_the_recorded_expiry_into_the_past_locks_the_gate_again()
    {
        // Mở khóa chỉ dựa trên dòng máy chủ đã ghi: sửa tay hạn về quá khứ thì khóa lại ngay.
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var device = NewKey();
        await _k.UnlockViaAdMobAsync(heritage.Id, device);
        await _k.SummaryAsync(heritage.Slug!, device);

        await factory.WithDbAsync(async db =>
        {
            var row = db.AdUnlocks.Single(u => u.GuestDevice!.DeviceKey == device);
            row.UnlockedAt = DateTime.UtcNow.AddHours(-30);
            row.ExpiresAt = DateTime.UtcNow.AddHours(-6);
            return await db.SaveChangesAsync();
        });
        await _k.AssertSummaryLockedAsync(heritage.Slug!, device);
    }

    [Fact]
    public async Task Old_unlocks_are_purged_after_twelve_months_and_recent_ones_stay()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var old = await _k.SeedUnlockAsync(device, heritage.Id, DateTime.UtcNow.AddMonths(-13));
        var edge = await _k.SeedUnlockAsync(device, heritage.Id, DateTime.UtcNow.AddMonths(-11));
        var recent = await _k.SeedUnlockAsync(device, heritage.Id, DateTime.UtcNow.AddDays(-1));

        using var scope = factory.Services.CreateScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<AdUnlockService>().PurgeExpiredAsync(default);

        Assert.True(deleted >= 1);
        Assert.Equal([edge.Id, recent.Id], (await _k.UnlocksAsync(device)).Select(u => u.Id).Order().ToList());
        Assert.DoesNotContain(await _k.UnlocksAsync(device), u => u.Id == old.Id);
    }
}

/// <summary>Môi trường phát triển và demo cho phép khai báo "thiết bị không có quảng cáo" (bản demo iOS bằng Expo Go): tóm tắt và nghe mở thẳng.</summary>
public sealed class AdsDevApiTests(AdsDevApiFactory factory) : IClassFixture<AdsDevApiFactory>
{
    private readonly AdsTestKit _k = new(factory);

    [Fact]
    public async Task A_device_that_declares_it_has_no_ads_opens_straight_away_but_others_still_see_the_gate()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var declared = NewKey();
        var other = NewKey();

        Assert.Equal(heritage.Name, (await _k.SummaryAsync(heritage.Slug!, declared, noAds: true)).Name);
        var status = await _k.StatusAsync(heritage.Slug!, declared, noAds: true);
        Assert.Equal((false, true, "NO_ADS_DEVICE"), (status.GateRequired, status.Unlocked, status.OpenReason));

        await _k.AssertSummaryLockedAsync(heritage.Slug!, other);
        Assert.True((await _k.Guests.RecordAsync(declared, "VIEW", heritage.Slug)).Recorded);
        var (listen, _) = await _k.SendAsync<GuestActivityResultDto>(HttpMethod.Post, $"{PublicRoot}/guest/activities", declared, new { type = "LISTEN", slug = heritage.Slug }, noAds: true);
        Assert.Equal(HttpStatusCode.OK, listen.StatusCode);

        var config = await DiscoveryTestKit.Expect(_k.SendAsync<AdConfigDto>(HttpMethod.Get, $"{AdsTestKit.AdsRoot}/config"));
        Assert.True(config.AllowNoAdsDeclaration);
    }
}

/// <summary>Cổng tắt trong cấu hình (dù đã có đơn vị quảng cáo): tóm tắt và nghe mở thẳng cho mọi khách.</summary>
public sealed class AdsGateOffApiTests(AdsGateOffApiFactory factory) : IClassFixture<AdsGateOffApiFactory>
{
    private readonly AdsTestKit _k = new(factory);

    [Fact]
    public async Task With_the_gate_switched_off_everything_is_open()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var device = NewKey();

        Assert.Equal(heritage.Name, (await _k.SummaryAsync(heritage.Slug!, device)).Name);
        Assert.Equal(heritage.Name, (await _k.SummaryAsync(heritage.Slug!, null)).Name);
        var status = await _k.StatusAsync(heritage.Slug!, device);
        Assert.Equal((false, true, "GATE_DISABLED"), (status.GateRequired, status.Unlocked, status.OpenReason));
        Assert.False((await DiscoveryTestKit.Expect(_k.SendAsync<AdConfigDto>(HttpMethod.Get, $"{AdsTestKit.AdsRoot}/config"))).GateActive);

        var (listen, _) = await _k.SendAsync<GuestActivityResultDto>(HttpMethod.Post, $"{PublicRoot}/guest/activities", device, new { type = "LISTEN", slug = heritage.Slug });
        Assert.Equal(HttpStatusCode.OK, listen.StatusCode);
    }
}
