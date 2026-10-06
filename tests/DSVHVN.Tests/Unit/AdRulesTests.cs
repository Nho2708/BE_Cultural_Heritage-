using DSVHVN.Application.Ads;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc quảng cáo có thưởng: các con số cố định, so khớp đơn vị quảng cáo, nền tảng theo mạng, nhãn lý do từ chối, bật tắt cổng theo cấu hình.</summary>
public sealed class AdRulesTests
{
    [Fact]
    public void The_fixed_numbers_follow_the_agreed_rules()
    {
        Assert.Equal(TimeSpan.FromHours(24), AdRules.UnlockDuration);
        Assert.Equal(TimeSpan.FromHours(1), AdRules.MaxTimestampSkew);
        Assert.Equal(TimeSpan.FromMinutes(10), AdRules.WebTransactionLifetime);
        Assert.Equal((10, 60, TimeSpan.FromHours(1)), (AdRules.WebMaxPerDevicePerHour, AdRules.WebMaxPerIpPerHour, AdRules.RateWindow));
        Assert.Equal(12, AdRules.RetentionMonths);
        Assert.Equal("G", AdRules.MaxAdContentRating);
    }

    [Fact]
    public void An_unlock_expires_exactly_24_hours_after_it_is_recorded_and_old_rows_are_purged_after_12_months()
    {
        var at = new DateTime(2026, 10, 3, 4, 5, 6, DateTimeKind.Utc);
        Assert.Equal(at.AddHours(24), AdRules.ExpiryOf(at));
        Assert.Equal(new DateTime(2025, 10, 3, 4, 5, 6, DateTimeKind.Utc), AdRules.RetentionCutoff(at));
    }

    [Theory]
    [InlineData(AdNetwork.ADMOB, ClientPlatform.ANDROID, true)]
    [InlineData(AdNetwork.ADMOB, ClientPlatform.IOS, true)]
    [InlineData(AdNetwork.ADMOB, ClientPlatform.WEB, false)]
    [InlineData(AdNetwork.AD_MANAGER, ClientPlatform.WEB, true)]
    [InlineData(AdNetwork.AD_MANAGER, ClientPlatform.ANDROID, false)]
    [InlineData(AdNetwork.AD_MANAGER, ClientPlatform.IOS, false)]
    public void AdMob_runs_in_the_app_and_Ad_Manager_on_the_web(AdNetwork network, ClientPlatform platform, bool expected) =>
        Assert.Equal(expected, AdRules.PlatformMatchesNetwork(network, platform));

    [Theory]
    [InlineData("ca-app-pub-3940256099942544/5224354917", "5224354917")]
    [InlineData("5224354917", "5224354917")]
    [InlineData("  ca-app-pub-1111111111111111/42  ", "42")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void An_AdMob_unit_compares_by_the_number_after_the_slash(string? unit, string? expected) =>
        Assert.Equal(expected, AdRules.NormalizeAdMobUnit(unit));

    [Fact]
    public void The_source_follows_the_network_and_every_rejection_reason_has_its_own_vietnamese_label()
    {
        Assert.Equal(("SERVER", "BROWSER"), (AdRules.SourceOf(AdNetwork.ADMOB), AdRules.SourceOf(AdNetwork.AD_MANAGER)));
        Assert.NotEqual(AdRules.SourceLabel(AdNetwork.ADMOB), AdRules.SourceLabel(AdNetwork.AD_MANAGER));

        Assert.Equal(6, AdRules.RejectionReasons.Count);
        Assert.Equal(6, AdRules.RejectionReasons.Distinct().Count());
        var labels = AdRules.RejectionReasons.Select(AdRules.RejectionLabel).ToList();
        Assert.All(AdRules.RejectionReasons, r => Assert.NotEqual(r, AdRules.RejectionLabel(r)));
        Assert.Equal(6, labels.Distinct().Count());
        // Lý do là mã chữ hoa và không chứa nhau (thống kê đếm bằng chuỗi con).
        Assert.All(AdRules.RejectionReasons, r => Assert.Matches("^[A-Z_]+$", r));
        foreach (var a in AdRules.RejectionReasons)
            foreach (var b in AdRules.RejectionReasons.Where(b => b != a))
                Assert.DoesNotContain("\"" + a + "\"", "\"" + b + "\"");
    }

    // ---- Cấu hình ----

    private static AdsOptions Options(bool gate = true, string[]? android = null, string[]? ios = null, string[]? web = null) => new()
    {
        GateEnabled = gate,
        AdMob = new AdMobOptions { AndroidAdUnitIds = android ?? [], IosAdUnitIds = ios ?? [] },
        AdManager = new AdManagerOptions { AdUnitPaths = web ?? [] },
    };

    [Fact]
    public void The_gate_works_only_when_it_is_switched_on_and_at_least_one_ad_unit_is_configured()
    {
        Assert.False(new AdsOptions().GateActive);                                     // mặc định: tắt
        Assert.False(Options(gate: true).GateActive);                                  // bật nhưng chưa có đơn vị nào: cổng tự tắt
        Assert.False(Options(gate: false, android: ["ca-app-pub-1/2"]).GateActive);    // có đơn vị nhưng tắt
        Assert.False(Options(gate: true, android: ["  "], ios: [""], web: [" "]).GateActive);   // đơn vị rỗng không tính
        Assert.True(Options(android: ["ca-app-pub-1/2"]).GateActive);
        Assert.True(Options(ios: ["3"]).GateActive);
        Assert.True(Options(web: ["/1/rewarded"]).GateActive);
    }

    [Fact]
    public void An_AdMob_unit_tells_the_platform_and_unknown_units_match_nothing()
    {
        var options = Options(android: ["ca-app-pub-3940256099942544/5224354917"], ios: ["1712485313"]);
        Assert.Equal(ClientPlatform.ANDROID, options.PlatformOfAdMobUnit("5224354917"));
        Assert.Equal(ClientPlatform.ANDROID, options.PlatformOfAdMobUnit("ca-app-pub-3940256099942544/5224354917"));
        Assert.Equal(ClientPlatform.IOS, options.PlatformOfAdMobUnit("ca-app-pub-3940256099942544/1712485313"));
        Assert.Null(options.PlatformOfAdMobUnit("999"));
        Assert.Null(options.PlatformOfAdMobUnit(null));
        Assert.Null(options.PlatformOfAdMobUnit(""));
    }

    [Fact]
    public void An_Ad_Manager_unit_must_match_a_configured_path_exactly()
    {
        var options = Options(web: ["/22639388115/rewarded_web_example"]);
        Assert.True(options.IsAdManagerUnit("/22639388115/rewarded_web_example"));
        Assert.True(options.IsAdManagerUnit(" /22639388115/rewarded_web_example "));
        Assert.False(options.IsAdManagerUnit("/22639388115/other"));
        Assert.False(options.IsAdManagerUnit("/22639388115/REWARDED_WEB_EXAMPLE"));
        Assert.False(options.IsAdManagerUnit(null));
        Assert.False(options.IsAdManagerUnit(""));
        Assert.False(Options().IsAdManagerUnit("/anything"));
    }
}
