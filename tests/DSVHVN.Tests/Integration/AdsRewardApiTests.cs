using System.Net;
using DSVHVN.Application.Ads;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using static DSVHVN.Tests.Integration.AdsTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Phần thưởng quảng cáo qua API thật: xác minh phía máy chủ của AdMob (ký như Google bằng khóa tự sinh) và báo thưởng của web (mã giao dịch một lần có chữ ký, giới hạn tần suất).
/// Hợp lệ thì ghi đúng một dòng; gọi lại trùng mã giao dịch và di sản đang mở khóa không ghi thêm, không bị coi là từ chối; mọi trường hợp không hợp lệ bị từ chối, không mở khóa
/// và để lại một dòng nhật ký thao tác không chứa IP, chữ ký hay mã thiết bị.
/// </summary>
public sealed class AdsRewardApiTests(AdsApiFactory factory) : IClassFixture<AdsApiFactory>
{
    private readonly AdsTestKit _k = new(factory);

    // ---- AdMob ----

    [Fact]
    public async Task A_valid_Google_callback_records_one_unlock_for_24_hours_and_creates_the_device_when_the_app_has_not_registered_it_yet()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var transaction = Guid.NewGuid().ToString("N");
        var before = DateTime.UtcNow;

        var (response, body) = await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, transaction));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.Success);
        Assert.Equal((true, null), (body.Data!.Recorded, body.Data.Reason));
        var row = Assert.Single(await _k.UnlocksAsync(device));
        Assert.Equal((AdNetwork.ADMOB, ClientPlatform.ANDROID, transaction, "5224354917", heritage.Id),
            (row.AdNetwork, row.Platform, row.TransactionId, row.AdUnitId, row.HeritageId));
        Assert.Equal(TimeSpan.FromHours(24), row.ExpiresAt - row.UnlockedAt);
        Assert.InRange(row.UnlockedAt, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(row.ExpiresAt, body.Data.ExpiresAt);
        Assert.NotNull(await _k.Guests.DeviceAsync(device));
    }

    [Fact]
    public async Task The_ad_unit_tells_Android_from_iOS_and_an_existing_device_row_is_left_alone()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var android = NewKey();
        var ios = NewKey();
        var registered = NewKey();
        await _k.SendAsync<object>(HttpMethod.Post, $"{GuestTestKit.Root}/devices", registered, new { language = "ko" }, platform: "WEB");
        var deviceBefore = await _k.Guests.DeviceAsync(registered);

        await _k.UnlockViaAdMobAsync(heritage.Id, android, adUnit: "5224354917");
        await _k.UnlockViaAdMobAsync(heritage.Id, ios, adUnit: "1712485313");
        await _k.UnlockViaAdMobAsync(heritage.Id, registered, adUnit: "ca-app-pub-3940256099942544/5224354917");   // đơn vị gửi dạng đầy đủ vẫn khớp

        Assert.Equal(ClientPlatform.ANDROID, (await _k.UnlocksAsync(android)).Single().Platform);
        Assert.Equal(ClientPlatform.IOS, (await _k.UnlocksAsync(ios)).Single().Platform);
        Assert.Equal(ClientPlatform.ANDROID, (await _k.UnlocksAsync(registered)).Single().Platform);

        // Lời gọi của Google không phải của thiết bị: không đổi nền tảng, ngôn ngữ hay lần thấy cuối của thiết bị đã đăng ký.
        var deviceAfter = await _k.Guests.DeviceAsync(registered);
        Assert.Equal((deviceBefore!.Platform, deviceBefore.LanguageCode, deviceBefore.LastSeenAt), (deviceAfter!.Platform, deviceAfter.LanguageCode, deviceAfter.LastSeenAt));
    }

    [Fact]
    public async Task Google_calling_again_with_the_same_transaction_changes_nothing_and_is_not_a_rejection()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var transaction = Guid.NewGuid().ToString("N");
        var first = await _k.UnlockViaAdMobAsync(heritage.Id, device, transactionId: transaction);
        var rejectionsBefore = await _k.RejectionCountAsync();

        var (response, body) = await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, transaction));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((false, "DUPLICATE", first.ExpiresAt), (body.Data!.Recorded, body.Data.Reason, body.Data.ExpiresAt));

        // Gọi lại rất muộn (thời điểm cũ hơn 1 giờ) vẫn là gọi lại, không phải "thời điểm lệch".
        var (late, lateBody) = await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, transaction, at: DateTime.UtcNow.AddHours(-5)));
        Assert.Equal((HttpStatusCode.OK, "DUPLICATE"), (late.StatusCode, lateBody.Data!.Reason));

        Assert.Single(await _k.UnlocksAsync(device));
        Assert.Equal(rejectionsBefore, await _k.RejectionCountAsync());
    }

    [Fact]
    public async Task A_new_transaction_for_a_heritage_that_is_still_unlocked_adds_nothing_and_does_not_extend_the_expiry()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var first = await _k.UnlockViaAdMobAsync(heritage.Id, device);

        var second = await _k.UnlockViaAdMobAsync(heritage.Id, device);

        Assert.Equal((false, "ALREADY_UNLOCKED", first.ExpiresAt), (second.Recorded, second.Reason, second.ExpiresAt));
        Assert.Single(await _k.UnlocksAsync(device));
    }

    [Fact]
    public async Task A_callback_for_another_heritage_on_the_same_device_unlocks_that_heritage_separately()
    {
        var admin = await _k.AdminAsync();
        var one = await _k.HeritageWithoutSummaryAsync(admin);
        var two = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();

        Assert.True((await _k.UnlockViaAdMobAsync(one.Id, device)).Recorded);
        Assert.True((await _k.UnlockViaAdMobAsync(two.Id, device)).Recorded);
        Assert.Equal([one.Id, two.Id], (await _k.UnlocksAsync(device)).Select(u => u.HeritageId).Order().ToList());
    }

    [Fact]
    public async Task A_timestamp_within_an_hour_of_the_server_clock_is_accepted()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var past = NewKey();
        var future = NewKey();

        Assert.Equal(HttpStatusCode.OK, (await _k.CallSsvAsync(_k.Ssv(heritage.Id, past, at: DateTime.UtcNow.AddMinutes(-59)))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _k.CallSsvAsync(_k.Ssv(heritage.Id, future, at: DateTime.UtcNow.AddMinutes(59)))).Response.StatusCode);
    }

    [Fact]
    public async Task Every_invalid_callback_leaves_no_unlock_and_one_log_line_and_only_a_missing_or_wrong_signature_gets_400()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var archived = await _k.HeritageWithoutSummaryAsync(admin);
        await _k.Guests.ArchiveAsync(admin, archived.Id);
        var device = NewKey();

        var cases = new (string Name, string Url, string Reason, bool Signed)[]
        {
            ("sửa tham số sau khi ký", _k.Ssv(heritage.Id, device, mutate: q => q.Replace("reward_amount=1", "reward_amount=9")), AdRules.RejectedBadSignature, false),
            ("đổi di sản sau khi ký", _k.Ssv(heritage.Id, device, mutate: q => q.Replace($"custom_data={heritage.Id}", "custom_data=1")), AdRules.RejectedBadSignature, false),
            ("mã khóa lạ", _k.Ssv(heritage.Id, device, keyId: 12345), AdRules.RejectedBadSignature, false),
            ("chữ ký rác", _k.Ssv(heritage.Id, device, mutate: q => System.Text.RegularExpressions.Regex.Replace(q, "signature=[^&]+", "signature=AAAA")), AdRules.RejectedBadSignature, false),
            ("đơn vị quảng cáo lạ", _k.Ssv(heritage.Id, device, adUnit: "999"), AdRules.RejectedUnknownAdUnit, true),
            ("đơn vị của Ad Manager không dùng cho AdMob", _k.Ssv(heritage.Id, device, adUnit: "rewarded_web_example"), AdRules.RejectedUnknownAdUnit, true),
            ("thiếu mã thiết bị", _k.Ssv(heritage.Id, device, leaveOutUserId: true), AdRules.RejectedBadParameter, true),
            ("thiếu mã di sản", _k.Ssv(heritage.Id, device, leaveOutCustomData: true), AdRules.RejectedBadParameter, true),
            ("thiếu đơn vị quảng cáo", _k.Ssv(heritage.Id, device, leaveOutAdUnit: true), AdRules.RejectedBadParameter, true),
            ("thiếu mã giao dịch", _k.Ssv(heritage.Id, device, leaveOutTransaction: true), AdRules.RejectedBadParameter, true),
            ("mã thiết bị không phải UUID", _k.Ssv(heritage.Id, device, userId: "khong-phai-uuid"), AdRules.RejectedBadParameter, true),
            ("mã thiết bị là UUID phiên bản 1", _k.Ssv(heritage.Id, device, userId: "3f2b8c1e-9a47-1d6b-8e10-5c7a9b2d4f61"), AdRules.RejectedBadParameter, true),
            ("mã di sản không phải số", _k.Ssv(heritage.Id, device, customData: "abc"), AdRules.RejectedBadParameter, true),
            ("mã di sản bằng không", _k.Ssv(heritage.Id, device, customData: "0"), AdRules.RejectedBadParameter, true),
            ("mã di sản âm", _k.Ssv(heritage.Id, device, customData: "-5"), AdRules.RejectedBadParameter, true),
            ("di sản không có", _k.Ssv(heritage.Id, device, customData: "99999999"), AdRules.RejectedBadParameter, true),
            ("di sản đã ẩn", _k.Ssv(archived.Id, device), AdRules.RejectedBadParameter, true),
            ("thời điểm không phải số", _k.Ssv(heritage.Id, device, timestamp: "abc"), AdRules.RejectedBadParameter, true),
            ("thời điểm bằng không", _k.Ssv(heritage.Id, device, timestamp: "0"), AdRules.RejectedBadParameter, true),
            ("mã giao dịch quá dài", _k.Ssv(heritage.Id, device, transactionId: new string('T', 256)), AdRules.RejectedBadParameter, true),
            ("không có chữ ký", _k.Ssv(heritage.Id, device, mutate: q => q[..q.IndexOf("&signature=", StringComparison.Ordinal)]), AdRules.RejectedBadParameter, false),
            ("mã khóa không phải số", _k.Ssv(heritage.Id, device, mutate: q => q.Replace($"key_id={FakeAdMobKeyStore.KeyId}", "key_id=abc")), AdRules.RejectedBadParameter, false),
            ("thời điểm cũ hơn 1 giờ", _k.Ssv(heritage.Id, device, at: DateTime.UtcNow.AddMinutes(-61)), AdRules.RejectedStaleTimestamp, true),
            ("thời điểm ở tương lai quá 1 giờ", _k.Ssv(heritage.Id, device, at: DateTime.UtcNow.AddMinutes(61)), AdRules.RejectedStaleTimestamp, true),
        };

        var unlocksBefore = await _k.UnlockCountAsync();
        var logsBefore = await _k.RejectionCountAsync();
        var expected = 0;
        foreach (var (name, url, reason, signed) in cases)
        {
            var (response, body) = await _k.CallSsvAsync(url);
            if (signed)
            {
                // Chữ ký đúng là lời gọi thật của Google: 200 để Google không gọi lại, nhưng không ghi lượt và nêu lý do.
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{name}: {(int)response.StatusCode} {body.ErrorCode}");
                Assert.True((false, reason, (DateTime?)null) == (body.Data!.Recorded, body.Data.Reason, body.Data.ExpiresAt), $"{name}: {body.Data.Recorded} {body.Data.Reason}");
            }
            else
            {
                Assert.True((HttpStatusCode.BadRequest, AdMessages.RewardRejectedCode) == (response.StatusCode, body.ErrorCode), $"{name}: {(int)response.StatusCode} {body.ErrorCode}");
                Assert.False(body.Success);
                Assert.Null(body.Data);
            }
            expected++;

            var logs = await _k.NewRejectionsAsync(logsBefore);
            Assert.True(logs.Count == expected, $"{name}: mong {expected} dòng nhật ký, có {logs.Count}");
            Assert.True(ReasonOf(logs[^1]) == reason, $"{name}: lý do {ReasonOf(logs[^1])}, mong {reason}");
        }

        Assert.Equal(unlocksBefore, await _k.UnlockCountAsync());
        Assert.Empty(await _k.UnlocksAsync(device));
        Assert.Null(await _k.Guests.DeviceAsync(device));   // lời gọi bị từ chối không tạo cả thiết bị

        // Dòng nhật ký: hệ thống tự ghi, không IP, không chữ ký, không mã thiết bị, không mã giao dịch.
        var all = await _k.NewRejectionsAsync(logsBefore);
        Assert.All(all, log =>
        {
            Assert.Equal((ActorRole.SYSTEM, (long?)null, AuditTargets.AdUnlocks, (long?)null, (string?)null), (log.ActorRole, log.UserId, log.TargetType, log.TargetId, log.IpAddress));
            Assert.Contains("\"network\":\"ADMOB\"", log.NewValue);
            Assert.DoesNotContain(device, log.NewValue);
            Assert.DoesNotContain("signature", log.NewValue);
        });
    }

    [Fact]
    public async Task A_google_callback_replayed_for_a_known_signature_with_edited_user_is_refused()
    {
        // Kẻ có một URL hợp lệ không đổi được thiết bị hay di sản: mọi sửa đổi làm hỏng chữ ký.
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var victim = NewKey();
        var attacker = NewKey();
        var url = _k.Ssv(heritage.Id, victim);

        var (response, body) = await _k.CallSsvAsync(url.Replace(victim, attacker));
        Assert.Equal((HttpStatusCode.BadRequest, AdMessages.RewardRejectedCode), (response.StatusCode, body.ErrorCode));
        Assert.Empty(await _k.UnlocksAsync(attacker));
        Assert.Equal(HttpStatusCode.OK, (await _k.CallSsvAsync(url)).Response.StatusCode);
        Assert.Single(await _k.UnlocksAsync(victim));
    }

    [Fact]
    public async Task Rejected_rewards_show_up_in_the_audit_log_for_the_system_admin_with_a_vietnamese_label()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        await _k.CallSsvAsync(_k.Ssv(heritage.Id, NewKey(), adUnit: "999"));

        var (response, body) = await _k.SendAsync<PagedResult<DSVHVN.Application.Audit.AuditLogDto>>(
            HttpMethod.Get, "/api/v1/admin/audit-logs?action=AD_REWARD_REJECTED&targetType=ad_unlocks&pageSize=5", token: admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = body.Data!.Items.First();
        Assert.Equal(("Từ chối phần thưởng quảng cáo", ActorRole.SYSTEM, null), (item.ActionLabel, item.ActorRole, item.IpAddress));
        Assert.Contains(AdRules.RejectedUnknownAdUnit, item.NewValue);
    }

    // ---- Web ----

    [Fact]
    public async Task The_web_flow_issues_a_one_time_code_and_the_browser_report_unlocks_with_the_browser_source()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var device = NewKey();

        await _k.AssertSummaryLockedAsync(heritage.Slug!, device);
        var start = await _k.StartWebAsync(heritage.Slug!, device);
        Assert.Equal((false, AdsApiFactory.WebUnit, false), (start.AlreadyUnlocked, start.AdUnitPath, start.Status.Unlocked));
        Assert.False(string.IsNullOrEmpty(start.TransactionId));
        Assert.InRange(start.TransactionExpiresAt!.Value, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(11));
        Assert.Empty(await _k.UnlocksAsync(device));   // cấp mã chưa mở khóa

        var result = await DiscoveryTestKit.Expect(_k.ReportWebRawAsync(heritage.Slug, start.TransactionId, AdsApiFactory.WebUnit, device));
        Assert.True(result.Recorded);

        var row = Assert.Single(await _k.UnlocksAsync(device));
        Assert.Equal((AdNetwork.AD_MANAGER, ClientPlatform.WEB, start.TransactionId, AdsApiFactory.WebUnit, heritage.Id),
            (row.AdNetwork, row.Platform, row.TransactionId, row.AdUnitId, row.HeritageId));
        Assert.Equal(TimeSpan.FromHours(24), row.ExpiresAt - row.UnlockedAt);
        Assert.Equal(heritage.Name, (await _k.SummaryAsync(heritage.Slug!, device)).Name);

        // Đang mở khóa thì không mời xem thêm: không cấp mã mới.
        var again = await _k.StartWebAsync(heritage.Slug!, device);
        Assert.True(again.AlreadyUnlocked);
        Assert.Null(again.TransactionId);
        Assert.True(again.Status.Unlocked);
    }

    [Fact]
    public async Task Reporting_the_same_code_twice_is_a_resend_not_a_second_unlock_and_not_a_rejection()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var start = await _k.StartWebAsync(heritage.Slug!, device);
        var first = await DiscoveryTestKit.Expect(_k.ReportWebRawAsync(heritage.Slug, start.TransactionId, AdsApiFactory.WebUnit, device));
        var logs = await _k.RejectionCountAsync();

        var second = await DiscoveryTestKit.Expect(_k.ReportWebRawAsync(heritage.Slug, start.TransactionId, AdsApiFactory.WebUnit, device));

        Assert.Equal((false, "DUPLICATE", first.ExpiresAt), (second.Recorded, second.Reason, second.ExpiresAt));
        Assert.Single(await _k.UnlocksAsync(device));
        Assert.Equal(logs, await _k.RejectionCountAsync());
    }

    [Fact]
    public async Task A_second_code_for_a_heritage_that_is_still_unlocked_does_not_extend_it()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var early = await _k.StartWebAsync(heritage.Slug!, device);   // hai mã được cấp trước khi mở khóa
        var late = await _k.StartWebAsync(heritage.Slug!, device);
        var first = await DiscoveryTestKit.Expect(_k.ReportWebRawAsync(heritage.Slug, early.TransactionId, AdsApiFactory.WebUnit, device));

        var second = await DiscoveryTestKit.Expect(_k.ReportWebRawAsync(heritage.Slug, late.TransactionId, AdsApiFactory.WebUnit, device));

        Assert.Equal((false, "ALREADY_UNLOCKED", first.ExpiresAt), (second.Recorded, second.Reason, second.ExpiresAt));
        Assert.Single(await _k.UnlocksAsync(device));
    }

    [Fact]
    public async Task A_bad_web_report_is_refused_with_its_reason_and_unlocks_nothing()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var otherHeritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();
        var stranger = NewKey();
        var start = await _k.StartWebAsync(heritage.Slug!, device);
        var strangerStart = await _k.StartWebAsync(heritage.Slug!, stranger);
        var code = start.TransactionId!;
        var tampered = (code[0] == 'A' ? "B" : "A") + code[1..];

        var cases = new (string Name, string? Slug, string? Code, string? Unit, string? Device, string Reason)[]
        {
            ("mã bị sửa", heritage.Slug, tampered, AdsApiFactory.WebUnit, device, AdRules.RejectedBadWebTransaction),
            ("không có mã", heritage.Slug, null, AdsApiFactory.WebUnit, device, AdRules.RejectedBadWebTransaction),
            ("mã rác", heritage.Slug, "khong-phai-ma", AdsApiFactory.WebUnit, device, AdRules.RejectedBadWebTransaction),
            ("mã của thiết bị khác", heritage.Slug, strangerStart.TransactionId, AdsApiFactory.WebUnit, device, AdRules.RejectedBadWebTransaction),
            ("mã cấp cho di sản khác", otherHeritage.Slug, code, AdsApiFactory.WebUnit, device, AdRules.RejectedBadWebTransaction),
            ("đơn vị quảng cáo lạ", heritage.Slug, code, "/1/khong-co", device, AdRules.RejectedUnknownAdUnit),
            ("thiếu đơn vị quảng cáo", heritage.Slug, code, null, device, AdRules.RejectedUnknownAdUnit),
            ("đơn vị của AdMob không dùng cho web", heritage.Slug, code, AdsApiFactory.AndroidUnit, device, AdRules.RejectedUnknownAdUnit),
            ("di sản không có", "khong-co-di-san", code, AdsApiFactory.WebUnit, device, AdRules.RejectedBadParameter),
            ("thiếu đường dẫn di sản", null, code, AdsApiFactory.WebUnit, device, AdRules.RejectedBadParameter),
        };

        var logsBefore = await _k.RejectionCountAsync();
        var expected = 0;
        foreach (var (name, slug, token, unit, who, reason) in cases)
        {
            var (response, body) = await _k.ReportWebRawAsync(slug, token, unit, who);
            Assert.True((HttpStatusCode.BadRequest, AdMessages.RewardRejectedCode) == (response.StatusCode, body.ErrorCode), $"{name}: {(int)response.StatusCode} {body.ErrorCode}");
            expected++;
            var logs = await _k.NewRejectionsAsync(logsBefore);
            Assert.True(logs.Count == expected && ReasonOf(logs[^1]) == reason, $"{name}: {logs.Count} dòng, lý do {(logs.Count > 0 ? ReasonOf(logs[^1]) : "-")}, mong {reason}");
            Assert.Contains("\"network\":\"AD_MANAGER\"", logs[^1].NewValue);
            Assert.Contains("\"platform\":\"WEB\"", logs[^1].NewValue);
        }

        Assert.Empty(await _k.UnlocksAsync(device));
        Assert.Empty(await _k.UnlocksAsync(stranger));

        // Sau chừng ấy lần thử sai, mã thật vẫn dùng được một lần.
        Assert.True((await DiscoveryTestKit.Expect(_k.ReportWebRawAsync(heritage.Slug, code, AdsApiFactory.WebUnit, device))).Recorded);
    }

    [Fact]
    public async Task The_web_endpoints_need_a_device_key_and_an_existing_published_heritage()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();

        var (noKey, noKeyBody) = await _k.StartWebRawAsync(heritage.Slug, null);
        Assert.Equal((HttpStatusCode.BadRequest, MessageCodes.Required), (noKey.StatusCode, noKeyBody.ErrorCode));
        var (badKey, badKeyBody) = await _k.StartWebRawAsync(heritage.Slug, "khong-phai-uuid");
        Assert.Equal((HttpStatusCode.BadRequest, MessageCodes.Invalid), (badKey.StatusCode, badKeyBody.ErrorCode));
        var (unknown, unknownBody) = await _k.StartWebRawAsync("khong-co-di-san", device);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(MessageCodes.Blocked, unknownBody.ErrorCode);
        var (missing, _) = await _k.StartWebRawAsync(null, device);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var (report, reportBody) = await _k.ReportWebRawAsync(heritage.Slug, "x", AdsApiFactory.WebUnit, null);
        Assert.Equal((HttpStatusCode.BadRequest, MessageCodes.Required), (report.StatusCode, reportBody.ErrorCode));
    }

    [Fact]
    public async Task The_web_allows_ten_unlocks_an_hour_per_device_then_answers_429_with_retry_after_and_logs_it_once()
    {
        var admin = await _k.AdminAsync();
        var device = NewKey();
        var heritages = new List<DSVHVN.Application.Heritages.HeritageDetailDto>();
        for (var i = 0; i < AdRules.WebMaxPerDevicePerHour + 1; i++) heritages.Add(await _k.HeritageWithoutSummaryAsync(admin));

        for (var i = 0; i < AdRules.WebMaxPerDevicePerHour; i++)
            Assert.True((await _k.UnlockViaWebAsync(heritages[i].Slug!, device)).Recorded);

        var logsBefore = await _k.RejectionCountAsync();
        var start = await _k.StartWebAsync(heritages[^1].Slug!, device);   // cấp mã vẫn được; chặn ở lúc báo thưởng
        var (response, body) = await _k.ReportWebRawAsync(heritages[^1].Slug, start.TransactionId, AdsApiFactory.WebUnit, device);

        Assert.Equal((HttpStatusCode.TooManyRequests, AdMessages.RateLimitedCode), (response.StatusCode, body.ErrorCode));
        Assert.Matches("thử lại sau \\d+ phút", body.Message);
        var wait = response.Headers.RetryAfter!.Delta!.Value;
        Assert.InRange(wait, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(60));
        Assert.Equal(AdRules.WebMaxPerDevicePerHour, (await _k.UnlocksAsync(device)).Count);

        // Thử tiếp trong giờ: vẫn 429 nhưng nhật ký chỉ có đúng một dòng cho thiết bị này.
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.TooManyRequests, (await _k.ReportWebRawAsync(heritages[^1].Slug, start.TransactionId, AdsApiFactory.WebUnit, device)).Response.StatusCode);
        var logs = await _k.NewRejectionsAsync(logsBefore);
        var rateLogs = logs.Where(l => ReasonOf(l) == AdRules.RejectedRateLimited).ToList();
        Assert.Single(rateLogs);
        Assert.Null(rateLogs[0].IpAddress);

        // Di sản đã mở khóa thì báo lại hay hỏi lại vẫn bình thường, không bị giới hạn chặn.
        Assert.True((await _k.StartWebAsync(heritages[0].Slug!, device)).AlreadyUnlocked);

        // Thiết bị khác không bị ảnh hưởng.
        Assert.True((await _k.UnlockViaWebAsync(heritages[^1].Slug!, NewKey())).Recorded);
    }

    [Fact]
    public async Task Web_unlocks_never_count_against_the_app_which_has_no_hourly_limit()
    {
        var admin = await _k.AdminAsync();
        var device = NewKey();
        for (var i = 0; i < AdRules.WebMaxPerDevicePerHour + 2; i++)
        {
            var heritage = await _k.HeritageWithoutSummaryAsync(admin);
            Assert.True((await _k.UnlockViaAdMobAsync(heritage.Id, device)).Recorded);
        }
        Assert.Equal(AdRules.WebMaxPerDevicePerHour + 2, (await _k.UnlocksAsync(device)).Count);
    }
}

/// <summary>Môi trường chỉ cấu hình đơn vị quảng cáo của app: web không có quảng cáo để hiện và không thể tự mở khóa.</summary>
public sealed class AdsAppOnlyApiTests(AdsAppOnlyApiFactory factory) : IClassFixture<AdsAppOnlyApiFactory>
{
    private readonly AdsTestKit _k = new(factory);

    [Fact]
    public async Task Without_a_web_ad_unit_the_web_cannot_start_or_report_and_the_gate_still_holds()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithSummaryAsync(admin);
        var device = NewKey();

        var (start, startBody) = await _k.StartWebRawAsync(heritage.Slug, device);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, AdMessages.UnavailableCode), (start.StatusCode, startBody.ErrorCode));

        // Kẻ tự bịa mã và đơn vị quảng cáo cũng không qua được: không có đơn vị web nào để khớp.
        var forged = factory.Services.GetService(typeof(IAdRewardTokenCodec)) as IAdRewardTokenCodec;
        var ticket = forged!.Issue(device, heritage.Id);
        var (report, reportBody) = await _k.ReportWebRawAsync(heritage.Slug, ticket.Code, "/22639388115/rewarded_web_example", device);
        Assert.Equal((HttpStatusCode.BadRequest, AdMessages.RewardRejectedCode), (report.StatusCode, reportBody.ErrorCode));
        Assert.Empty(await _k.UnlocksAsync(device));
        await _k.AssertSummaryLockedAsync(heritage.Slug!, device);

        // App vẫn mở khóa bình thường.
        await _k.UnlockViaAdMobAsync(heritage.Id, device);
        await _k.SummaryAsync(heritage.Slug!, device);
    }
}
