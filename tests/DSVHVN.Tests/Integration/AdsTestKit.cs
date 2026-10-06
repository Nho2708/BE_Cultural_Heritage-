using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DSVHVN.Application.Ads;
using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Guests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>Khóa công khai của Google thay bằng khóa tự sinh: kiểm thử ký như Google và máy chủ kiểm bằng khóa công khai tương ứng.</summary>
public sealed class FakeAdMobKeyStore : IAdMobKeyStore
{
    public const long KeyId = 3335741209;

    private readonly Dictionary<long, byte[]> _keys = new();

    /// <summary>Khóa riêng mà kiểm thử dùng để ký; khóa công khai của nó nằm trong danh sách với mã <see cref="KeyId"/>.</summary>
    public ECDsa Signer { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public FakeAdMobKeyStore() => _keys[KeyId] = Signer.ExportSubjectPublicKeyInfo();

    public Task<byte[]?> FindPublicKeyAsync(long keyId, CancellationToken ct) => Task.FromResult(_keys.GetValueOrDefault(keyId));
}

/// <summary>
/// Api có bật cổng quảng cáo với các đơn vị quảng cáo thử của Google và khóa xác minh tự sinh. Mặc định không chấp nhận khai báo "thiết bị không có quảng cáo"
/// (như môi trường thật); <see cref="AdsDevApiFactory"/> chấp nhận (môi trường phát triển) và <see cref="AdsGateOffApiFactory"/> tắt cổng.
/// </summary>
public class AdsApiFactory : ApiFactory
{
    public const string AndroidUnit = "ca-app-pub-3940256099942544/5224354917";
    public const string IosUnit = "ca-app-pub-3940256099942544/1712485313";
    public const string WebUnit = "/22639388115/rewarded_web_example";

    public FakeAdMobKeyStore Keys { get; } = new();

    public AdsApiFactory() { }

    protected AdsApiFactory(string sqlServerConnection) : base(sqlServerConnection) { }

    protected virtual bool GateEnabled => true;

    protected virtual bool AllowNoAds => false;

    protected virtual bool WebUnits => true;

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings
    {
        get
        {
            yield return new("Ads:GateEnabled", GateEnabled ? "true" : "false");
            yield return new("Ads:AllowNoAdsDeclaration", AllowNoAds ? "true" : "false");
            yield return new("Ads:AdMob:AndroidAdUnitIds:0", AndroidUnit);
            yield return new("Ads:AdMob:IosAdUnitIds:0", IosUnit);
            if (WebUnits) yield return new("Ads:AdManager:AdUnitPaths:0", WebUnit);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAdMobKeyStore>();
            services.AddSingleton<IAdMobKeyStore>(Keys);
        });
    }
}

public sealed class AdsDevApiFactory : AdsApiFactory
{
    protected override bool AllowNoAds => true;
}

public sealed class AdsGateOffApiFactory : AdsApiFactory
{
    protected override bool GateEnabled => false;
}

/// <summary>Chỉ có đơn vị quảng cáo của app, không có đơn vị của web: web không có quảng cáo để hiện.</summary>
public sealed class AdsAppOnlyApiFactory : AdsApiFactory
{
    protected override bool WebUnits => false;
}

/// <summary>Như <see cref="SqlServerApiFactory"/> (database tạm của LocalDB tạo bằng migration, tự xóa) nhưng có cổng quảng cáo và khóa xác minh tự sinh.</summary>
public sealed class AdsSqlServerApiFactory : AdsApiFactory
{
    public AdsSqlServerApiFactory() : this(LocalDb.NewTemporaryName()) { }

    private AdsSqlServerApiFactory(string databaseName) : base(LocalDb.ConnectionString(databaseName)) => DatabaseName = databaseName;

    public string DatabaseName { get; }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && LocalDb.Available) LocalDb.Drop(DatabaseName);
    }
}

/// <summary>
/// Dựng dữ liệu và gọi API cho các test quảng cáo: di sản đã xuất bản (có hoặc chưa có tóm tắt đã duyệt), lời gọi xác minh phía máy chủ ký như Google, báo thưởng của web,
/// ghi thẳng lượt mở khóa vào CSDL với mốc thời gian tùy ý, đọc nhật ký từ chối.
/// </summary>
public sealed class AdsTestKit(AdsApiFactory factory)
{
    public const string PublicRoot = "/api/v1/public";
    public const string AdsRoot = "/api/v1/public/ads";
    public const string SsvRoot = "/api/v1/ads/admob/ssv";
    public const string StatsUrl = "/api/v1/admin/ads/stats";

    public TranslationTestKit Translation { get; } = new(factory);
    public AdsApiFactory Factory { get; } = factory;
    public HttpClient Http => Translation.Http;
    public GuestTestKit Guests { get; } = new(factory);

    public static string NewKey() => Guid.NewGuid().ToString();

    public Task<string> AdminAsync() => Translation.AdminAsync();

    /// <summary>Di sản đã xuất bản và có tóm tắt tiếng Việt, tiếng Anh đã duyệt (nút "Tóm tắt" và "Nghe" có nội dung).</summary>
    public async Task<HeritageDetailDto> HeritageWithSummaryAsync(string admin)
    {
        var seed = await Translation.SeedAsync(admin);
        await Translation.GenerateAsync(admin, seed.Heritage.Id);
        await Translation.ApproveAllAsync(admin, seed.Heritage.Id);
        return seed.Heritage;
    }

    /// <summary>Di sản đã xuất bản nhưng chưa có tóm tắt nào.</summary>
    public Task<HeritageDetailDto> HeritageWithoutSummaryAsync(string admin) =>
        Translation.Discovery.PublishedAsync(admin, new Spec("Chùa thử " + Unique()));

    // ---- Gọi API ----

    public async Task<(HttpResponseMessage Response, Envelope<T> Body)> SendAsync<T>(
        HttpMethod method, string url, string? deviceKey = null, object? body = null, string? token = null, bool noAds = false, string? platform = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (deviceKey is not null) request.Headers.TryAddWithoutValidation("X-Device-Key", deviceKey);
        if (platform is not null) request.Headers.TryAddWithoutValidation("X-Client-Platform", platform);
        if (noAds) request.Headers.TryAddWithoutValidation("X-Ads-Unavailable", "true");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: HttpTestExtensions.Json);
        var response = await Http.SendAsync(request);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(HttpTestExtensions.Json);
        return (response, envelope!);
    }

    public Task<(HttpResponseMessage Response, Envelope<PublicSummaryDto> Body)> SummaryRawAsync(
        string slug, string? deviceKey, string? lang = null, string? token = null, bool noAds = false) =>
        SendAsync<PublicSummaryDto>(HttpMethod.Get, $"{PublicRoot}/heritages/{slug}/summary" + (lang is null ? "" : "?lang=" + lang), deviceKey, token: token, noAds: noAds);

    public async Task<PublicSummaryDto> SummaryAsync(string slug, string? deviceKey, string? lang = null, string? token = null, bool noAds = false)
    {
        var (response, body) = await SummaryRawAsync(slug, deviceKey, lang, token, noAds);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Mong 200, nhận {(int)response.StatusCode} {body.ErrorCode}: {body.Message}");
        return body.Data!;
    }

    public async Task AssertSummaryLockedAsync(string slug, string? deviceKey, string? lang = null)
    {
        var (response, body) = await SummaryRawAsync(slug, deviceKey, lang);
        Assert.Equal((HttpStatusCode.Forbidden, AdMessages.UnlockRequiredCode), (response.StatusCode, body.ErrorCode));
        Assert.False(body.Success);
        Assert.Null(body.Data);
    }

    public Task<AdUnlockStatusDto> StatusAsync(string slug, string? deviceKey, string? token = null, bool noAds = false) =>
        DiscoveryTestKit.Expect(SendAsync<AdUnlockStatusDto>(HttpMethod.Get, $"{AdsRoot}/unlock?slug={slug}", deviceKey, token: token, noAds: noAds));

    // ---- AdMob: xác minh phía máy chủ ----

    /// <summary>Một lời gọi xác minh như Google gửi, ký bằng khóa của kiểm thử. <paramref name="mutate"/> sửa chuỗi truy vấn SAU khi ký (làm sai chữ ký).</summary>
    public string Ssv(
        long heritageId, string deviceKey, string? transactionId = null, string adUnit = "5224354917", DateTime? at = null,
        string? customData = null, string? userId = null, long keyId = FakeAdMobKeyStore.KeyId, Func<string, string>? mutate = null,
        bool leaveOutUserId = false, bool leaveOutCustomData = false, bool leaveOutAdUnit = false, bool leaveOutTransaction = false, string? timestamp = null)
    {
        var milliseconds = new DateTimeOffset(at ?? DateTime.UtcNow, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var parts = new List<string> { "ad_network=5450213213286189855" };
        if (!leaveOutAdUnit) parts.Add("ad_unit=" + adUnit);
        if (!leaveOutCustomData) parts.Add("custom_data=" + (customData ?? heritageId.ToString()));
        parts.Add("reward_amount=1");
        parts.Add("reward_item=summary");
        parts.Add("timestamp=" + (timestamp ?? milliseconds.ToString()));
        if (!leaveOutTransaction) parts.Add("transaction_id=" + (transactionId ?? Guid.NewGuid().ToString("N")));
        if (!leaveOutUserId) parts.Add("user_id=" + (userId ?? deviceKey));
        var content = string.Join('&', parts);
        var signature = AdMobSignature.Sign(content, factory.Keys.Signer);
        var query = $"{content}&signature={signature}&key_id={keyId}";
        return $"{SsvRoot}?{(mutate is null ? query : mutate(query))}";
    }

    public Task<(HttpResponseMessage Response, Envelope<AdRewardResultDto> Body)> CallSsvAsync(string url) =>
        SendAsync<AdRewardResultDto>(HttpMethod.Get, url);

    /// <summary>Gọi xác minh hợp lệ (200) và trả kết quả; dùng để "mở khóa" một di sản trên một thiết bị như Google làm sau khi người xem nhận thưởng.</summary>
    public async Task<AdRewardResultDto> UnlockViaAdMobAsync(long heritageId, string deviceKey, string adUnit = "5224354917", string? transactionId = null)
    {
        var (response, body) = await CallSsvAsync(Ssv(heritageId, deviceKey, transactionId, adUnit));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Mong 200, nhận {(int)response.StatusCode} {body.ErrorCode}: {body.Message}");
        return body.Data!;
    }

    // ---- Web: Ad Manager ----

    public Task<(HttpResponseMessage Response, Envelope<WebRewardStartDto> Body)> StartWebRawAsync(string? slug, string? deviceKey, string? token = null) =>
        SendAsync<WebRewardStartDto>(HttpMethod.Post, $"{AdsRoot}/web/start", deviceKey, new { slug }, token);

    public Task<WebRewardStartDto> StartWebAsync(string slug, string deviceKey) =>
        DiscoveryTestKit.Expect(StartWebRawAsync(slug, deviceKey));

    public Task<(HttpResponseMessage Response, Envelope<AdRewardResultDto> Body)> ReportWebRawAsync(
        string? slug, string? transactionId, string? adUnit, string? deviceKey, string? token = null) =>
        SendAsync<AdRewardResultDto>(HttpMethod.Post, $"{AdsRoot}/web/rewarded", deviceKey, new { slug, transactionId, adUnit }, token);

    /// <summary>Luồng web đầy đủ: bắt đầu, nhận mã giao dịch, báo đã nhận thưởng. Trả kết quả của lần báo.</summary>
    public async Task<AdRewardResultDto> UnlockViaWebAsync(string slug, string deviceKey)
    {
        var start = await StartWebAsync(slug, deviceKey);
        Assert.False(start.AlreadyUnlocked);
        return await DiscoveryTestKit.Expect(ReportWebRawAsync(slug, start.TransactionId, AdsApiFactory.WebUnit, deviceKey));
    }

    // ---- Cơ sở dữ liệu ----

    public Task<List<AdUnlock>> UnlocksAsync(string deviceKey) =>
        Factory.WithDbAsync(db => db.AdUnlocks.AsNoTracking().Where(u => u.GuestDevice!.DeviceKey == deviceKey).OrderBy(u => u.Id).ToListAsync());

    public Task<int> UnlockCountAsync() => Factory.WithDbAsync(db => db.AdUnlocks.CountAsync());

    public Task<List<AuditLog>> RejectionsAsync() =>
        Factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == AuditAction.AD_REWARD_REJECTED).OrderBy(a => a.Id).ToListAsync());

    public async Task<List<AuditLog>> NewRejectionsAsync(int before) => (await RejectionsAsync()).Skip(before).ToList();

    public async Task<int> RejectionCountAsync() => (await RejectionsAsync()).Count;

    /// <summary>Ghi thẳng một lượt mở khóa với mốc thời gian tùy ý (để thử hạn, thống kê theo ngày, dọn sau 12 tháng). Tạo thiết bị nếu chưa có.</summary>
    public async Task<AdUnlock> SeedUnlockAsync(
        string deviceKey, long heritageId, DateTime unlockedAt, AdNetwork network = AdNetwork.ADMOB, ClientPlatform platform = ClientPlatform.ANDROID,
        TimeSpan? lifetime = null, string? transactionId = null, string? adUnit = "5224354917")
    {
        return await Factory.WithDbAsync(async db =>
        {
            var device = await db.GuestDevices.SingleOrDefaultAsync(d => d.DeviceKey == deviceKey);
            if (device is null)
            {
                device = new GuestDevice { DeviceKey = deviceKey, Platform = platform, LanguageCode = LanguageCode.vi, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow };
                db.GuestDevices.Add(device);
                await db.SaveChangesAsync();
            }

            var row = new AdUnlock
            {
                GuestDeviceId = device.Id, HeritageId = heritageId, Platform = platform, AdNetwork = network,
                TransactionId = transactionId ?? Guid.NewGuid().ToString("N"), AdUnitId = adUnit,
                UnlockedAt = unlockedAt, ExpiresAt = unlockedAt + (lifetime ?? TimeSpan.FromHours(24)),
            };
            db.AdUnlocks.Add(row);
            await db.SaveChangesAsync();
            return row;
        });
    }

    public static string ReasonOf(AuditLog log) => System.Text.Json.JsonDocument.Parse(log.NewValue!).RootElement.GetProperty("reason").GetString()!;
}
