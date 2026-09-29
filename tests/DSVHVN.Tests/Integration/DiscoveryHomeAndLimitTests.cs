using System.Net;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Trang chủ công khai. Kết quả toàn cục (di sản nổi bật, số đếm) nên cả kịch bản nằm trong một test chạy trên CSDL riêng của lớp này,
/// dựng dần từng bước.
/// </summary>
public sealed class DiscoveryHomeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly DiscoveryTestKit _kit = new(factory);

    [Fact]
    public async Task Home_features_at_most_eight_unesco_or_special_heritages_newest_update_first_and_counts_by_type_and_region()
    {
        var admin = await _kit.Api.AdminTokenAsync();
        var http = _kit.Http;

        // Trống: vẫn trả đủ các mục đếm với số 0.
        var empty = await Expect(http.GetAsync<HomeDto>($"{Public}/home"));
        Assert.Empty(empty.Featured);
        Assert.Equal(0, empty.TotalHeritages);
        Assert.Equal(5, empty.ByType.Count);
        Assert.Equal(["Bắc", "Trung", "Nam"], empty.ByRegion.Select(r => r.Value));
        Assert.All(empty.ByType.Concat(empty.ByRegion), c => Assert.Equal(0, c.Count));

        // 9 di sản UNESCO/quốc gia đặc biệt, 2 cấp quốc gia, 1 nháp UNESCO, 1 đã ẩn quốc gia đặc biệt.
        var featured = new List<HeritageDetailDto>();
        for (var i = 1; i <= 9; i++)
            featured.Add(await _kit.PublishedAsync(admin, new Spec($"Nổi bật {i}", Level: i % 2 == 0 ? "UNESCO" : "NATIONAL_SPECIAL",
                ProvinceCode: i <= 5 ? "01" : i <= 7 ? "46" : "79", Type: i == 9 ? "INTANGIBLE" : "TANGIBLE",
                Latitude: i == 9 ? null : 21m, Longitude: i == 9 ? null : 105.8m)));
        await _kit.PublishedAsync(admin, new Spec("Cấp quốc gia 1", Level: "NATIONAL"));
        await _kit.PublishedAsync(admin, new Spec("Cấp quốc gia 2", Level: "NATIONAL", ProvinceCode: "46"));
        await _kit.DraftAsync(admin, new Spec("Nháp UNESCO", Level: "UNESCO"));
        var archived = await _kit.PublishedAsync(admin, new Spec("Đã ẩn đặc biệt", Level: "NATIONAL_SPECIAL"));
        await Expect(http.PostAsync<HeritageDetailDto>($"{Admin}/heritages/{archived.Id}/archive", null, admin));

        // Đặt mốc cập nhật rõ ràng: di sản đánh số càng lớn càng mới.
        await factory.WithDbAsync(async db =>
        {
            foreach (var (h, i) in featured.Select((h, i) => (h, i)))
            {
                var stamp = new DateTime(2026, 1, 1 + i, 8, 0, 0, DateTimeKind.Utc);
                await db.Heritages.Where(x => x.Id == h.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, stamp));
            }
            return 0;
        });

        var home = await Expect(http.GetAsync<HomeDto>($"{Public}/home"));

        Assert.Equal(DiscoveryService.FeaturedLimit, home.Featured.Count);
        Assert.Equal(featured.AsEnumerable().Reverse().Take(8).Select(h => h.Name), home.Featured.Select(f => f.Name));   // Nổi bật 9 … 2
        Assert.All(home.Featured, f => Assert.Contains(f.RecognitionLevel, new RecognitionLevel?[] { RecognitionLevel.UNESCO, RecognitionLevel.NATIONAL_SPECIAL }));
        Assert.DoesNotContain(home.Featured, f => f.Name is "Nháp UNESCO" or "Đã ẩn đặc biệt" or "Nổi bật 1");
        var first = home.Featured[0];
        Assert.Equal((featured[8].Slug, featured[8].ThumbnailUrl, "Thành phố Hồ Chí Minh", "Nam"),
            (first.Slug, first.ThumbnailUrl, first.ProvinceName, first.Region));

        // 9 nổi bật + 2 quốc gia đang xuất bản; nháp và đã ẩn không đếm.
        Assert.Equal(11, home.TotalHeritages);
        Assert.Equal(("Vật thể", 10), (home.ByType[0].Label, home.ByType[0].Count));
        Assert.Equal(("INTANGIBLE", 1), (home.ByType[1].Value, home.ByType[1].Count));
        Assert.Equal([0, 0, 0], home.ByType.Skip(2).Select(c => c.Count));
        Assert.Equal([("Bắc", "Miền Bắc", 6), ("Trung", "Miền Trung", 3), ("Nam", "Miền Nam", 2)],
            home.ByRegion.Select(r => (r.Value, r.Label, r.Count)));

        // Sửa di sản cũ nhất thì nó mới cập nhật nhất và lên đầu.
        var oldest = featured[1];   // "Nổi bật 2": di sản nổi bật thấp nhất còn trong danh sách
        var province = await _kit.ProvinceIdAsync("01");
        await Expect(http.PutAsync<HeritageDetailDto>($"{Admin}/heritages/{oldest.Id}", new
        {
            name = oldest.Name,
            shortDescription = "Vừa được sửa",
            content = oldest.Content,
            heritageType = "TANGIBLE",
            recognitionLevel = "UNESCO",
            recognizedYear = 1999,
            provinceId = province,
            latitude = 21m,
            longitude = 105.8m,
        }, admin));
        var reordered = await Expect(http.GetAsync<HomeDto>($"{Public}/home"));
        Assert.Equal(oldest.Name, reordered.Featured[0].Name);
        Assert.Equal("Vừa được sửa", reordered.Featured[0].ShortDescription);
        Assert.Equal(8, reordered.Featured.Count);
    }
}

/// <summary>Api chạy với ngưỡng công khai thấp (5 yêu cầu mỗi phút) để kiểm được giới hạn tần suất.</summary>
public sealed class ThrottledApiFactory : ApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
        [new("RateLimiting:PublicRequestsPerMinute", "5")];
}

/// <summary>Giới hạn tần suất của API công khai: vượt ngưỡng theo IP thì 429 kèm thời gian chờ; API khác không bị ảnh hưởng.</summary>
public sealed class PublicRateLimitTests(ThrottledApiFactory factory) : IClassFixture<ThrottledApiFactory>
{
    [Fact]
    public async Task Exceeding_the_public_limit_returns_429_with_retry_after_and_leaves_other_apis_alone()
    {
        var api = new PlatformClient(factory);
        var http = api.Http;

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync<object>($"{Public}/home")).Response.StatusCode);

        var (blocked, body) = await http.GetAsync<object>($"{Public}/heritages");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.False(body.Success);
        Assert.Equal("TOO_MANY_REQUESTS", body.ErrorCode);
        Assert.StartsWith("Có quá nhiều yêu cầu gửi tới trong thời gian ngắn. Vui lòng thử lại sau ", body.Message);
        Assert.EndsWith(" giây.", body.Message);
        Assert.True(blocked.Headers.TryGetValues("Retry-After", out var retry));
        var seconds = int.Parse(retry!.Single());
        Assert.InRange(seconds, 1, 60);
        Assert.Contains($"sau {seconds} giây", body.Message);
        Assert.True(blocked.Headers.Contains("X-Correlation-Id"));

        // Mọi endpoint công khai dùng chung một bộ đếm theo IP.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.GetAsync<object>($"{Public}/provinces")).Response.StatusCode);

        // Đăng nhập, API quản trị và kiểm tra sức khỏe không nằm trong bộ đếm công khai.
        var admin = await api.AdminTokenAsync();
        for (var i = 0; i < 8; i++)
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync<object>("/api/v1/admin/provinces", admin)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
    }
}
