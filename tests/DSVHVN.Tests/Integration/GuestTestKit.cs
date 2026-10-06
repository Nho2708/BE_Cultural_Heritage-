using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DSVHVN.Application.Common;
using DSVHVN.Application.Guests;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Guests;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dựng dữ liệu và gọi API cho các test khách tham quan: di sản đã xuất bản ở nhiều loại, tỉnh và tọa độ; gọi API công khai kèm mã thiết bị;
/// ghi thẳng lịch sử vào CSDL với mốc thời gian tùy ý (để thử hạn 90 ngày, lượt xem lặp quá 10 phút, cửa sổ 20 lượt).
/// </summary>
public sealed class GuestTestKit(ApiFactory factory)
{
    public const string Root = "/api/v1/public/guest";
    public const string PublicRoot = "/api/v1/public";

    public DiscoveryTestKit Discovery { get; } = new(factory);
    public ApiFactory Factory { get; } = factory;
    public HttpClient Http => Discovery.Http;

    public static string NewKey() => Guid.NewGuid().ToString();

    public Task<string> AdminAsync() => Discovery.Api.AdminTokenAsync();

    /// <summary>Gọi một API công khai với mã thiết bị (và nền tảng, token học sinh nếu có); không có <paramref name="deviceKey"/> thì như khách chưa gửi mã.</summary>
    public async Task<(HttpResponseMessage Response, Envelope<T> Body)> SendAsync<T>(
        HttpMethod method, string url, string? deviceKey, object? body = null, string? platform = null, string? token = null, string? acceptLanguage = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (deviceKey is not null) request.Headers.TryAddWithoutValidation("X-Device-Key", deviceKey);
        if (platform is not null) request.Headers.TryAddWithoutValidation("X-Client-Platform", platform);
        if (acceptLanguage is not null) request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: HttpTestExtensions.Json);
        var response = await Http.SendAsync(request);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(HttpTestExtensions.Json);
        return (response, envelope!);
    }

    public Task<(HttpResponseMessage Response, Envelope<T> Body)> GetAsync<T>(string url, string? deviceKey, string? token = null, string? platform = null) =>
        SendAsync<T>(HttpMethod.Get, url, deviceKey, token: token, platform: platform);

    public Task<(HttpResponseMessage Response, Envelope<T> Body)> PostAsync<T>(string url, string? deviceKey, object? body = null, string? token = null) =>
        SendAsync<T>(HttpMethod.Post, url, deviceKey, body ?? new { }, token: token);

    public async Task<T> ExpectAsync<T>(Task<(HttpResponseMessage Response, Envelope<T> Body)> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (response, body) = await call;
        Assert.True(response.StatusCode == expected, $"Mong {(int)expected}, nhận {(int)response.StatusCode} {body.ErrorCode}: {body.Message}");
        return body.Data!;
    }

    public async Task FailAsync(Task<(HttpResponseMessage Response, Envelope<object> Body)> call, HttpStatusCode expected, string errorCode)
    {
        var (response, body) = await call;
        Assert.Equal((expected, errorCode), (response.StatusCode, body.ErrorCode));
        Assert.False(body.Success);
    }

    // ---- Lịch sử ----

    public Task<PagedResult<GuestHistoryItemDto>> HistoryAsync(string key, string query = "") =>
        ExpectAsync(GetAsync<PagedResult<GuestHistoryItemDto>>($"{Root}/history?{query}", key));

    public Task<GuestRecommendationsDto> RecommendationsAsync(string? key, string query = "", string? token = null) =>
        ExpectAsync(GetAsync<GuestRecommendationsDto>($"{Root}/recommendations?{query}", key, token));

    public Task<NearbyDto> NearbyAsync(double latitude, double longitude, string query = "") =>
        ExpectAsync(PostAsync<NearbyDto>($"{Root}/nearby?{query}", null, new { latitude, longitude }));

    public Task<GuestActivityResultDto> RecordAsync(string key, string type, string? slug = null, string? query = null, string? lang = null) =>
        ExpectAsync(PostAsync<GuestActivityResultDto>($"{Root}/activities" + (lang is null ? "" : "?lang=" + lang), key, new { type, slug, query }));

    // ---- Dữ liệu ----

    /// <summary>Di sản đã xuất bản. Mặc định: vật thể cấp quốc gia, Hà Nội, tọa độ Hà Nội.</summary>
    public async Task<HeritageDetailDto> HeritageAsync(string admin, string name, string type = "TANGIBLE", string province = "01", string level = "NATIONAL",
        decimal? latitude = 21.033333m, decimal? longitude = 105.85m, string? extra = null) =>
        await Discovery.PublishedAsync(admin, new Spec(name + " " + Unique(), type, level, province, latitude, longitude, Extra: extra));

    public async Task ArchiveAsync(string admin, long heritageId) =>
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Admin}/heritages/{heritageId}/archive", null, admin));

    /// <summary>Một dòng lịch sử ghi thẳng vào CSDL: loại, di sản (hoặc từ khóa) và thời điểm cách đây bao lâu.</summary>
    public sealed record Seed(GuestActivityType Type, long? HeritageId, TimeSpan Ago, string? Query = null, LanguageCode Language = LanguageCode.vi);

    /// <summary>Tạo thiết bị (nếu chưa có) và thêm các dòng lịch sử với mốc thời gian đã cho (tính từ lúc chạy).</summary>
    public async Task SeedAsync(string key, params Seed[] rows)
    {
        await Factory.WithDbAsync(async db =>
        {
            var device = await db.GuestDevices.SingleOrDefaultAsync(d => d.DeviceKey == key);
            if (device is null)
            {
                device = new GuestDevice { DeviceKey = key, Platform = ClientPlatform.WEB, LanguageCode = LanguageCode.vi, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow };
                db.GuestDevices.Add(device);
                await db.SaveChangesAsync();
            }
            foreach (var row in rows)
                db.GuestActivities.Add(new GuestActivity
                {
                    GuestDeviceId = device.Id, ActivityType = row.Type, HeritageId = row.HeritageId, SearchQuery = row.Query,
                    LanguageCode = row.Language, CreatedAt = DateTime.UtcNow - row.Ago,
                });
            return await db.SaveChangesAsync();
        });
    }

    public Task<List<GuestActivity>> ActivitiesAsync(string key) =>
        Factory.WithDbAsync(db => db.GuestActivities.AsNoTracking().Where(a => a.GuestDevice!.DeviceKey == key).OrderBy(a => a.Id).ToListAsync());

    public Task<GuestDevice?> DeviceAsync(string key) =>
        Factory.WithDbAsync(db => db.GuestDevices.AsNoTracking().SingleOrDefaultAsync(d => d.DeviceKey == key));

    public Task<int> DeviceCountAsync() => Factory.WithDbAsync(db => db.GuestDevices.CountAsync());

    public static TimeSpan Minutes(double minutes) => TimeSpan.FromMinutes(minutes);

    public static TimeSpan Days(double days) => TimeSpan.FromDays(days);
}
