using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Khám phá công khai trên SQL Server thật (database tạm của LocalDB, collation Vietnamese_CI_AI) với 20 di sản đợt 1 của nhóm:
/// nạp ở DRAFT nên công khai chưa thấy gì, xuất bản xong thì trang chủ, tìm không dấu, bộ lọc, bản đồ, chi tiết, timeline đều đọc được.
/// </summary>
public sealed class DiscoverySeededSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly DiscoveryTestKit _kit = new(factory);
    private HttpClient Http => _kit.Http;

    private Task<PagedResult<HeritageCardDto>> SearchAsync(string query) =>
        Expect(Http.GetAsync<PagedResult<HeritageCardDto>>($"{Public}/heritages?pageSize=100&{query}"));

    private static string K(string text) => "keyword=" + Uri.EscapeDataString(text);

    private static List<string> Slugs(PagedResult<HeritageCardDto> page) => page.Items.Select(i => i.Slug).ToList();

    [LocalDbHeritageDataFact]
    public async Task Seeded_heritages_are_invisible_as_drafts_and_fully_discoverable_once_published()
    {
        var admin = await _kit.Api.AdminTokenAsync();

        // 1. Đang nháp: công khai không thấy gì, kể cả khi biết đường dẫn.
        Assert.Equal(0, (await SearchAsync("")).TotalItems);
        Assert.Empty((await Expect(Http.GetAsync<HeritageMapDto>($"{Public}/heritages/map"))).Pins);
        Assert.Empty((await Expect(Http.GetAsync<HomeDto>($"{Public}/home"))).Featured);
        await Fail(Http.GetAsync<object>($"{Public}/heritages/khu-trung-tam-hoang-thanh-thang-long-ha-noi"), HttpStatusCode.NotFound, "BLOCKED");

        // 2. Xuất bản cả 20 di sản.
        var drafts = await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>($"{Admin}/heritages?status=DRAFT&pageSize=100", admin));
        Assert.Equal(20, drafts.TotalItems);
        foreach (var row in drafts.Items)
            await Expect(Http.PostAsync<HeritageDetailDto>($"{Admin}/heritages/{row.Id}/publish", null, admin));

        var all = await SearchAsync("");
        Assert.Equal(20, all.TotalItems);
        var expected = await factory.WithDbAsync(async db => await db.Heritages.AsNoTracking()
            .Select(h => new
            {
                h.Slug, h.HeritageType, h.RecognitionLevel, h.Latitude,
                Region = h.Province == null ? null : h.Province.Region,
                ProvinceId = h.ProvinceId,
            }).ToListAsync());

        // 3. Trang chủ: 8 di sản nổi bật cấp cao nhất, số đếm khớp CSDL.
        var home = await Expect(Http.GetAsync<HomeDto>($"{Public}/home"));
        Assert.Equal(8, home.Featured.Count);
        Assert.All(home.Featured, f => Assert.True(f.RecognitionLevel is RecognitionLevel.UNESCO or RecognitionLevel.NATIONAL_SPECIAL));
        Assert.Equal(20, home.TotalHeritages);
        Assert.Equal(20, home.ByType.Sum(c => c.Count));
        foreach (var region in new[] { "Bắc", "Trung", "Nam" })
            Assert.Equal(expected.Count(e => e.Region == region), home.ByRegion.Single(r => r.Value == region).Count);

        // 4. Tìm có dấu, không dấu, chữ hoa: ă â ê ô ơ ư đ đều khớp dù collation coi là chữ riêng.
        foreach (var keyword in new[] { "Thăng Long", "thang long", "THANG LONG", "thăng long", "Hoàng thành", "hoang thanh" })
            Assert.Contains("khu-trung-tam-hoang-thanh-thang-long-ha-noi", Slugs(await SearchAsync(K(keyword))));
        foreach (var keyword in new[] { "Đền Hùng", "den hung", "đền hùng" })
            Assert.Contains("den-hung", Slugs(await SearchAsync(K(keyword))));
        Assert.Contains("dan-ca-quan-ho-bac-ninh", Slugs(await SearchAsync(K("quan họ"))));
        Assert.Contains("dan-ca-quan-ho-bac-ninh", Slugs(await SearchAsync(K("QUAN HO"))));
        Assert.Contains("nghe-thuat-don-ca-tai-tu-nam-bo", Slugs(await SearchAsync(K("đờn ca tài tử"))));
        Assert.Contains("nghe-thuat-don-ca-tai-tu-nam-bo", Slugs(await SearchAsync(K("don ca tai tu"))));
        Assert.Empty((await SearchAsync(K("zzqx wwvy"))).Items);
        // Tên khớp nguyên cụm đứng đầu.
        Assert.Equal("khu-trung-tam-hoang-thanh-thang-long-ha-noi", (await SearchAsync(K("hoang thanh thang long"))).Items[0].Slug);

        // 5. Bộ lọc khớp với dữ liệu trong CSDL.
        Assert.Equal(expected.Count(e => e.Region == "Trung"), (await SearchAsync("region=Trung")).TotalItems);
        Assert.Equal(expected.Count(e => e.Region == "Bắc"), (await SearchAsync("region=bac")).TotalItems);
        Assert.Equal(expected.Count(e => e.HeritageType == HeritageType.INTANGIBLE), (await SearchAsync("heritageType=INTANGIBLE")).TotalItems);
        Assert.Equal(expected.Count(e => e.RecognitionLevel == RecognitionLevel.NATIONAL_SPECIAL),
            (await SearchAsync("recognitionLevel=NATIONAL_SPECIAL")).TotalItems);
        var hanoi = expected.First(e => e.Slug == "khu-trung-tam-hoang-thanh-thang-long-ha-noi").ProvinceId;
        Assert.Contains("khu-trung-tam-hoang-thanh-thang-long-ha-noi", Slugs(await SearchAsync($"provinceId={hanoi}")));

        // 6. Thời kỳ theo timeline: Điện Biên Phủ (1953-1954) là hiện đại, không phải cận đại; Tràng An bắt đầu từ 28000 TCN.
        var contemporary = Slugs(await SearchAsync("period=CONTEMPORARY"));
        Assert.Contains("chien-truong-dien-bien-phu", contemporary);
        Assert.DoesNotContain("chien-truong-dien-bien-phu", Slugs(await SearchAsync("period=MODERN")));
        Assert.Contains("quan-the-danh-thang-trang-an", Slugs(await SearchAsync("period=PREHISTORIC")));
        Assert.DoesNotContain("khu-trung-tam-hoang-thanh-thang-long-ha-noi", Slugs(await SearchAsync("period=PREHISTORIC")));
        Assert.Contains("khu-trung-tam-hoang-thanh-thang-long-ha-noi", Slugs(await SearchAsync("period=INDEPENDENT_FEUDAL")));
        Assert.Contains("quan-the-danh-thang-trang-an", Slugs(await SearchAsync("period=NORTHERN_RULE")));

        // 7. Bản đồ: chỉ di sản có tọa độ, còn lại được đếm riêng.
        var located = expected.Count(e => e.Latitude != null);
        var map = await Expect(Http.GetAsync<HeritageMapDto>($"{Public}/heritages/map"));
        Assert.Equal((located, 20 - located, 20, false), (map.Pins.Count, map.WithoutLocation, map.TotalMatched, map.IsTruncated));
        Assert.All(map.Pins, p =>
        {
            Assert.True(HeritageRules.IsLatitude(p.Latitude) && HeritageRules.IsLongitude(p.Longitude), p.Slug);
            Assert.False(string.IsNullOrWhiteSpace(p.ShortDescription));
        });
        var trungMap = await Expect(Http.GetAsync<HeritageMapDto>($"{Public}/heritages/map?region=Trung&heritageType=TANGIBLE"));
        Assert.NotEmpty(trungMap.Pins);
        Assert.All(trungMap.Pins, p => Assert.Equal(HeritageType.TANGIBLE, p.HeritageType));

        // 8. Chi tiết và timeline của cả 20 di sản đọc được; timeline xếp đúng thứ tự.
        foreach (var slug in expected.Select(e => e.Slug!))
        {
            var detail = await Expect(Http.GetAsync<PublicHeritageDetailDto>($"{Public}/heritages/{slug}"));
            Assert.NotEmpty(detail.References);
            Assert.NotEmpty(detail.Media);
            Assert.True(detail.HasTimeline, slug);
            Assert.All(detail.Media, m => Assert.NotNull(m.Author));
            Assert.Contains(detail.Media, m => m.IsThumbnail);

            var timeline = await Expect(Http.GetAsync<PublicTimelineDto>($"{Public}/heritages/{slug}/timeline"));
            Assert.Equal(detail.Timeline.Count, timeline.Phases.Count);
            foreach (var phase in timeline.Phases)
            {
                var keys = phase.Events.Select(e => (e.StartYear, (int)(e.StartMonth ?? 0), (int)(e.StartDay ?? 0))).ToList();
                Assert.Equal(keys.Order(), keys);
                Assert.All(phase.Events, e => Assert.False(string.IsNullOrWhiteSpace(e.DateLabel)));
            }
        }
        var hoangThanh = await Expect(Http.GetAsync<PublicHeritageDetailDto>($"{Public}/heritages/khu-trung-tam-hoang-thanh-thang-long-ha-noi"));
        Assert.Equal("1010 – 1821", hoangThanh.Timeline[0].RangeLabel);
        var trangAn = await Expect(Http.GetAsync<PublicTimelineDto>($"{Public}/heritages/quan-the-danh-thang-trang-an/timeline"));
        Assert.Equal("28000 TCN – 1010", trangAn.Phases[0].RangeLabel);
        Assert.StartsWith("28000 TCN", trangAn.Phases[0].Events[0].DateLabel);

        // 9. Ẩn một di sản thì rời mọi trang công khai ngay.
        var hidden = drafts.Items.Single(d => d.Slug == "den-hung");
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Admin}/heritages/{hidden.Id}/archive", null, admin));
        Assert.Equal(19, (await SearchAsync("")).TotalItems);
        Assert.DoesNotContain("den-hung", Slugs(await SearchAsync(K("den hung"))));
        await Fail(Http.GetAsync<object>($"{Public}/heritages/den-hung"), HttpStatusCode.NotFound, "BLOCKED");
        Assert.DoesNotContain(hidden.Id, (await Expect(Http.GetAsync<HeritageMapDto>($"{Public}/heritages/map"))).Pins.Select(p => p.Id));
    }
}

/// <summary>Truy vấn công khai chạy đúng trên SQL Server: chữ có dấu khác nhau, nội dung dài trên nvarchar(max), thời kỳ, xếp hạng.</summary>
public sealed class DiscoveryCollationSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly DiscoveryTestKit _kit = new(factory);
    private HttpClient Http => _kit.Http;

    private Task<PagedResult<HeritageCardDto>> SearchAsync(string query) =>
        Expect(Http.GetAsync<PagedResult<HeritageCardDto>>($"{Public}/heritages?{query}"));

    private static string K(string text) => "keyword=" + Uri.EscapeDataString(text);

    [LocalDbFact]
    public async Task Every_vietnamese_letter_with_its_own_accent_is_found_by_its_plain_form()
    {
        var admin = await _kit.Api.AdminTokenAsync();
        var tag = Unique();
        var h = await _kit.PublishedAsync(admin, new Spec($"Đặng Tiến Ngưỡng Ơn Ê Ô Ấ Ắ Ữ Ỳ {tag}", ShortDescription: "Chữ đặc biệt: ăâêôơưđ"));

        foreach (var keyword in new[] { "dang tien nguong on e o a a u y", "ĐẶNG TIẾN NGƯỠNG", "ăâêôơưđ", "aaeooud", "chu dac biet", "Ơn Ê Ô" })
        {
            var page = await SearchAsync(K($"{keyword} {tag}"));
            Assert.Equal([h.Name], page.Items.Select(i => i.Name));
        }
        Assert.Empty((await SearchAsync(K($"dang tien nguong khong co {tag}"))).Items);
    }

    [LocalDbFact]
    public async Task Long_content_is_searched_to_its_last_word_and_special_characters_are_harmless()
    {
        var admin = await _kit.Api.AdminTokenAsync();
        var tag = Unique();
        var province = await _kit.ProvinceIdAsync("01");
        var filler = string.Concat(Enumerable.Repeat("Đoạn nội dung dài về di sản để kiểm tra nvarchar(max). ", 1500));   // ~ 85.000 ký tự
        var created = await Expect(Http.PostAsync<HeritageDetailDto>($"{Admin}/heritages", new
        {
            name = $"Nội dung dài {tag}",
            shortDescription = "Mô tả",
            content = Words(210) + " " + filler + "chữ cuốicùng",
            heritageType = "TANGIBLE",
            recognitionLevel = "NONE",
            provinceId = province,
            latitude = 21m,
            longitude = 105.8m,
        }, admin), HttpStatusCode.Created);
        await Expect(Http.PostAsync<MediaDto>($"{Admin}/heritages/{created.Id}/media", new
        {
            mediaType = "IMAGE", url = $"https://upload.wikimedia.org/{Unique()}.jpg", author = "An", license = "CC0 1.0",
            source = "Wikimedia Commons", setAsThumbnail = true,
        }, admin), HttpStatusCode.Created);
        await Expect(Http.PostAsync<ReferenceDto>($"{Admin}/heritages/{created.Id}/references",
            new { title = "Nguồn — Cục Di sản văn hóa", url = "https://dsvh.gov.vn/x", sourceType = "WEBSITE" }, admin), HttpStatusCode.Created);
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Admin}/heritages/{created.Id}/publish", null, admin));

        Assert.Single((await SearchAsync(K($"cuoicung {tag}"))).Items);
        Assert.Single((await SearchAsync(K($"chữ cuốicùng {tag}"))).Items);
        foreach (var hostile in new[] { $"{tag}' OR 1=1 --", $"{tag}%", $"[{tag}]", $"{tag}\\", "'; DROP TABLE heritages; --" })
            Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<object>($"{Public}/heritages?{K(hostile)}")).Response.StatusCode);
        Assert.Equal(1, await factory.WithDbAsync(db => db.Heritages.CountAsync(x => x.Id == created.Id)));
    }

    [LocalDbFact]
    public async Task Period_recognition_ranking_and_map_queries_run_on_sql_server()
    {
        var admin = await _kit.Api.AdminTokenAsync();
        var tag = Unique();
        var special = await _kit.PublishedAsync(admin, new Spec($"{tag} Cầu Rồng", Level: "NATIONAL_SPECIAL"));
        var national = await _kit.PublishedAsync(admin, new Spec($"{tag} Cầu Rồng Đà Nẵng", Level: "NATIONAL"));
        var inContent = await _kit.PublishedAsync(admin, new Spec($"{tag} Quần thể", Level: "UNESCO", Extra: "gần cầu rồng"));
        var phase = await _kit.AddPhaseAsync(admin, special.Id, "Cận đại", 1900, 1930);
        await _kit.AddEventAsync(admin, phase.Id, "Xây cầu", 1929);

        Assert.Equal([special.Name, national.Name, inContent.Name],
            (await SearchAsync(K($"{tag} cau rong"))).Items.Select(i => i.Name));
        Assert.Equal([special.Name], (await SearchAsync($"{K(tag)}&period=MODERN")).Items.Select(i => i.Name));
        Assert.Empty((await SearchAsync($"{K(tag)}&period=CONTEMPORARY")).Items);

        var map = await Expect(Http.GetAsync<HeritageMapDto>($"{Public}/heritages/map?{K(tag)}"));
        Assert.Equal([inContent.Id, special.Id, national.Id], map.Pins.Select(p => p.Id));
        Assert.Equal([false, true, false], map.Pins.Select(p => p.HasTimeline));

        var home = await Expect(Http.GetAsync<HomeDto>($"{Public}/home"));
        Assert.Contains(home.Featured, f => f.Id == special.Id);
    }
}
