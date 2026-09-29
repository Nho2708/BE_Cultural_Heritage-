using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Khám phá di sản công khai qua HTTP (khách, không đăng nhập): chỉ thấy di sản đã xuất bản, tìm có dấu và không dấu, các bộ lọc
/// (tỉnh, vùng, loại, cấp xếp hạng, thời kỳ), phân trang, bản đồ, chi tiết, timeline, danh mục tỉnh và bộ lọc.
/// Mỗi test gắn một mã riêng vào tên di sản và tìm theo mã đó, nên không lẫn với dữ liệu của test khác trong cùng CSDL.
/// </summary>
public sealed class DiscoveryApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly DiscoveryTestKit _kit = new(factory);
    private HttpClient Http => _kit.Http;

    private Task<string> AdminAsync() => _kit.Api.AdminTokenAsync();

    private Task<PagedResult<HeritageCardDto>> SearchAsync(string query) =>
        Expect(Http.GetAsync<PagedResult<HeritageCardDto>>($"{Public}/heritages?{query}"));

    private static string K(string text) => "keyword=" + Uri.EscapeDataString(text);

    private static List<string> Names(PagedResult<HeritageCardDto> page) => page.Items.Select(i => i.Name).ToList();

    /// <summary>Sắp theo mã ký tự để so hai danh sách tên không phụ thuộc thứ tự hay văn hóa của máy chạy test.</summary>
    private static string[] S(params string?[] names) => names.Select(n => n ?? "").Order(StringComparer.Ordinal).ToArray();

    // ---- Quyền và phạm vi ----

    [Fact]
    public async Task Every_public_endpoint_works_without_logging_in_and_ignores_a_bad_token()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var h = await _kit.PublishedAsync(admin, new Spec($"Khách xem được {tag}"));
        var paths = new[]
        {
            $"{Public}/home", $"{Public}/provinces", $"{Public}/filters", $"{Public}/heritages?{K(tag)}",
            $"{Public}/heritages/map?{K(tag)}", $"{Public}/heritages/{h.Slug}", $"{Public}/heritages/{h.Slug}/timeline",
        };

        foreach (var path in paths)
        {
            var (anonymous, body) = await Http.GetAsync<object>(path);
            Assert.True(anonymous.StatusCode == HttpStatusCode.OK, $"{path}: {(int)anonymous.StatusCode} {body.Message}");

            // Token hỏng không biến yêu cầu công khai thành 401: coi như khách.
            var (withBadToken, _) = await Http.GetAsync<object>(path, "token-khong-hop-le");
            Assert.Equal(HttpStatusCode.OK, withBadToken.StatusCode);
        }
    }

    [Fact]
    public async Task Only_published_heritages_are_visible_everywhere_and_others_read_as_not_found()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var published = await _kit.PublishedAsync(admin, new Spec($"Đã xuất bản {tag}"));
        var draft = await _kit.DraftAsync(admin, new Spec($"Còn nháp {tag}"));
        var archived = await _kit.PublishedAsync(admin, new Spec($"Đã ẩn {tag}"));
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Admin}/heritages/{archived.Id}/archive", null, admin));
        var deleted = await _kit.DraftAsync(admin, new Spec($"Đã xóa {tag}"));
        await Expect(Http.DeleteAsync<object>($"{Admin}/heritages/{deleted.Id}", admin));

        Assert.Equal([published.Name], Names(await SearchAsync(K(tag))));
        var map = await Expect(Http.GetAsync<HeritageMapDto>($"{Public}/heritages/map?{K(tag)}"));
        Assert.Equal([published.Id], map.Pins.Select(p => p.Id));
        Assert.Equal(1, map.TotalMatched);

        await Expect(Http.GetAsync<PublicHeritageDetailDto>($"{Public}/heritages/{published.Slug}"));
        foreach (var hidden in new[] { draft, archived, deleted })
        {
            var detail = await Fail(Http.GetAsync<object>($"{Public}/heritages/{hidden.Slug}"), HttpStatusCode.NotFound, "BLOCKED");
            Assert.Equal("Không thể xem di sản. Không tìm thấy di sản.", detail.Message);
            await Fail(Http.GetAsync<object>($"{Public}/heritages/{hidden.Slug}/timeline"), HttpStatusCode.NotFound, "BLOCKED");
        }

        // Hiện lại thì xuất hiện lại.
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Admin}/heritages/{archived.Id}/restore", null, admin));
        Assert.Equal(2, (await SearchAsync(K(tag))).TotalItems);
    }

    [Fact]
    public async Task Unknown_or_malformed_slugs_are_not_found_not_errors()
    {
        await Fail(Http.GetAsync<object>($"{Public}/heritages/khong-co-di-san-nay"), HttpStatusCode.NotFound, "BLOCKED");
        await Fail(Http.GetAsync<object>($"{Public}/heritages/Sai%20Duong%20Dan!"), HttpStatusCode.NotFound, "BLOCKED");
        await Fail(Http.GetAsync<object>($"{Public}/heritages/khong-co-di-san-nay/timeline"), HttpStatusCode.NotFound, "BLOCKED");
    }

    // ---- Tìm kiếm ----

    [Fact]
    public async Task Keyword_finds_names_with_and_without_accents_in_any_case()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var h = await _kit.PublishedAsync(admin, new Spec($"Hoàng thành Thăng Long {tag}"));
        _ = await _kit.PublishedAsync(admin, new Spec($"Văn Miếu {tag}"));

        foreach (var keyword in new[] { $"Thăng Long {tag}", $"thang long {tag}", $"THĂNG LONG {tag}", $"thăng long {tag}", $"Thang Long {tag.ToUpperInvariant()}" })
            Assert.Equal([h.Name], Names(await SearchAsync(K(keyword))));

        Assert.Equal(2, (await SearchAsync(K(tag))).TotalItems);
        Assert.Empty((await SearchAsync(K($"khong co di san nay {tag}"))).Items);
    }

    [Fact]
    public async Task Keyword_matches_letters_that_the_database_collation_would_treat_as_different()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var h = await _kit.PublishedAsync(admin, new Spec($"Đền Hùng {tag}", ShortDescription: "Nơi thờ các vua Hùng dựng nước",
            Address: "Xã Hy Cương, Phú Thọ"));

        // ă â ê ô ơ ư đ được coi là a a e o o u d khi tìm.
        foreach (var keyword in new[] { "den hung", "đền hùng", "Đền Hùng", "DEN HUNG", "nơi thờ", "noi tho", "dung nuoc", "dựng nước", "hy cuong", "Hy Cương" })
            Assert.Equal([h.Name], Names(await SearchAsync(K($"{keyword} {tag}"))));
    }

    [Fact]
    public async Task Keyword_is_searched_in_name_short_description_address_and_content()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var h = await _kit.PublishedAsync(admin, new Spec($"Di sản {tag}", ShortDescription: "Mô tả có cụm chuôngvàng",
            Address: "Số 9 phố Quảng Xương", Extra: "Nội dung nhắc tới sông Lô hùng vĩ"));

        foreach (var keyword in new[] { "chuongvang", "quang xuong", "song lo hung vi", "Sông Lô" })
            Assert.Equal([h.Name], Names(await SearchAsync(K($"{keyword} {tag}"))));
        // Cụm không có trong di sản này thì không khớp, kể cả khi từng từ nằm rải rác ở hai trường khác nhau.
        Assert.Empty((await SearchAsync(K($"chuongvang quang {tag} xuong khong co"))).Items);
    }

    [Fact]
    public async Task Each_keyword_word_matches_the_start_of_a_word_so_typing_can_stop_midway()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var h = await _kit.PublishedAsync(admin, new Spec($"Hoàng thành Thăng Long {tag}"));

        foreach (var keyword in new[] { "thang lo", "thăng l", "hoa thanh", "th", "thang long hoang" })
            Assert.True(Names(await SearchAsync(K($"{keyword} {tag}"))).SequenceEqual([h.Name]), keyword);
        // Giữa từ hoặc cuối từ thì không khớp: "hang" nằm trong "thang" nhưng không phải đầu từ.
        foreach (var keyword in new[] { "hang", "ong", "oang thanh", "hang long" })
            Assert.Empty((await SearchAsync(K($"{keyword} {tag}"))).Items);
    }

    [Fact]
    public async Task Every_word_of_the_keyword_must_be_present()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var both = await _kit.PublishedAsync(admin, new Spec($"Chùa Một Cột {tag}", Extra: "kiến trúc độc đáo"));
        _ = await _kit.PublishedAsync(admin, new Spec($"Chùa Hương {tag}", Extra: "suối yến"));

        Assert.Equal([both.Name], Names(await SearchAsync(K($"chua cot {tag}"))));
        Assert.Equal([both.Name], Names(await SearchAsync(K($"kien truc doc dao {tag}"))));
        Assert.Equal(2, (await SearchAsync(K($"chua {tag}"))).TotalItems);
        Assert.Empty((await SearchAsync(K($"chua cot suoi yen {tag}"))).Items);
    }

    [Fact]
    public async Task Editing_a_heritage_updates_what_the_search_finds()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var h = await _kit.PublishedAsync(admin, new Spec($"Tên cũ {tag}"));
        Assert.Single((await SearchAsync(K($"ten cu {tag}"))).Items);

        var province = await _kit.ProvinceIdAsync("01");
        await Expect(Http.PutAsync<HeritageDetailDto>($"{Admin}/heritages/{h.Id}", new
        {
            name = $"Tên mới {tag}",
            shortDescription = "Mô tả đã sửa",
            content = Words(210) + " bổsung",
            heritageType = "TANGIBLE",
            recognitionLevel = "NATIONAL",
            recognizedYear = 1999,
            provinceId = province,
            latitude = 21.033333m,
            longitude = 105.85m,
        }, admin));

        Assert.Empty((await SearchAsync(K($"ten cu {tag}"))).Items);
        Assert.Equal([$"Tên mới {tag}"], Names(await SearchAsync(K($"ten moi {tag}"))));
        Assert.Single((await SearchAsync(K($"bosung {tag}"))).Items);
    }

    [Fact]
    public async Task Keyword_rules_are_two_to_a_hundred_characters_with_some_letter_or_digit()
    {
        foreach (var bad in new[] { "a", " a ", new string('x', 101) })
        {
            var error = await Fail(Http.GetAsync<object>($"{Public}/heritages?{K(bad)}"), HttpStatusCode.BadRequest, "INVALID");
            Assert.Equal("Từ khóa không hợp lệ: phải dài 2-100 ký tự.", error.Errors!["keyword"]);
            await Fail(Http.GetAsync<object>($"{Public}/heritages/map?{K(bad)}"), HttpStatusCode.BadRequest, "INVALID");
        }
        var symbols = await Fail(Http.GetAsync<object>($"{Public}/heritages?{K("!?")}"), HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Từ khóa không hợp lệ: phải có ít nhất một chữ cái hoặc chữ số.", symbols.Errors!["keyword"]);

        // Chuỗi trống hoặc toàn khoảng trắng coi như không nhập từ khóa; đúng 2 và đúng 100 ký tự thì được.
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<object>($"{Public}/heritages?keyword=")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<object>($"{Public}/heritages?keyword=%20%20")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<object>($"{Public}/heritages?{K("ab")}")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<object>($"{Public}/heritages?{K(new string('x', 100))}")).Response.StatusCode);
    }

    [Fact]
    public async Task Wildcard_characters_in_the_keyword_are_not_treated_as_patterns()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        _ = await _kit.PublishedAsync(admin, new Spec($"Di sản {tag}"));

        foreach (var keyword in new[] { $"%{tag}%", $"_{tag}", $"[a-z]{tag}", $"{tag}'; DROP TABLE heritages;--" })
        {
            var (res, body) = await Http.GetAsync<PagedResult<HeritageCardDto>>($"{Public}/heritages?{K(keyword)}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.True(body.Data!.TotalItems <= 1, keyword);
        }
        Assert.Equal(1, (await SearchAsync(K($"%{tag}%"))).TotalItems);       // dấu % bị bỏ như mọi dấu câu, không phải ký tự đại diện
        Assert.Empty((await SearchAsync(K($"%%%%{tag}x"))).Items);
    }

    [Fact]
    public async Task Results_with_the_whole_phrase_in_the_name_come_first_then_higher_recognition_level()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var inContent = await _kit.PublishedAsync(admin, new Spec($"{tag} Quần thể A", Level: "UNESCO", Extra: "gần cầu rồng"));
        var provincial = await _kit.PublishedAsync(admin, new Spec($"{tag} Cầu Rồng", Level: "PROVINCIAL"));
        var national = await _kit.PublishedAsync(admin, new Spec($"{tag} Cầu Rồng Đà Nẵng", Level: "NATIONAL"));

        // Cụm "tag cau rong" nằm trong tên của hai di sản sau (cấp quốc gia trước cấp tỉnh); di sản UNESCO chỉ có cụm trong nội dung nên đứng cuối.
        Assert.Equal([national.Name, provincial.Name, inContent.Name], Names(await SearchAsync(K($"{tag} cau rong"))));
        // Không có từ khóa: cấp cao trước, cùng cấp theo tên.
        var byLevel = await SearchAsync(K(tag));
        Assert.Equal([inContent.Name, national.Name, provincial.Name], Names(byLevel));
    }

    // ---- Bộ lọc ----

    [Fact]
    public async Task Filters_by_province_region_type_and_level_combine_with_and()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var hanoi = await _kit.PublishedAsync(admin, new Spec($"Hà Nội {tag}", ProvinceCode: "01", Type: "TANGIBLE", Level: "UNESCO"));
        var hue = await _kit.PublishedAsync(admin, new Spec($"Huế {tag}", ProvinceCode: "46", Type: "MIXED", Level: "UNESCO"));
        var saigon = await _kit.PublishedAsync(admin, new Spec($"Sài Gòn {tag}", ProvinceCode: "79", Type: "TANGIBLE", Level: "NATIONAL_SPECIAL"));
        var music = await _kit.PublishedAsync(admin, new Spec($"Nhã nhạc {tag}", Type: "INTANGIBLE", Level: "UNESCO", ProvinceCode: "46",
            Latitude: null, Longitude: null));
        var provinceHue = hue.Province!.Id;

        Assert.Equal(4, (await SearchAsync(K(tag))).TotalItems);
        Assert.Equal(S(hue.Name, music.Name), S([.. Names(await SearchAsync($"{K(tag)}&provinceId={provinceHue}"))]));
        Assert.Equal([hanoi.Name], Names(await SearchAsync($"{K(tag)}&region=B%E1%BA%AFc")));            // Bắc
        Assert.Equal([hanoi.Name], Names(await SearchAsync($"{K(tag)}&region=bac")));                    // không dấu, chữ thường
        Assert.Equal(S(hue.Name, music.Name), S([.. Names(await SearchAsync($"{K(tag)}&region=Trung"))]));
        Assert.Equal([saigon.Name], Names(await SearchAsync($"{K(tag)}&region=NAM")));
        Assert.Equal(S(hanoi.Name, saigon.Name), S([.. Names(await SearchAsync($"{K(tag)}&heritageType=TANGIBLE"))]));
        Assert.Equal([music.Name], Names(await SearchAsync($"{K(tag)}&heritageType=intangible")));      // enum không phân biệt hoa thường
        Assert.Equal([saigon.Name], Names(await SearchAsync($"{K(tag)}&recognitionLevel=NATIONAL_SPECIAL")));
        Assert.Equal([hue.Name], Names(await SearchAsync($"{K(tag)}&region=Trung&heritageType=MIXED&recognitionLevel=UNESCO")));
        Assert.Empty((await SearchAsync($"{K(tag)}&region=Nam&recognitionLevel=UNESCO")).Items);
        Assert.Empty((await SearchAsync($"{K(tag)}&provinceId=999999")).Items);
    }

    [Fact]
    public async Task Filter_values_outside_the_catalog_are_rejected_in_vietnamese_with_the_field_name()
    {
        var region = await Fail(Http.GetAsync<object>($"{Public}/heritages?region=Tay"), HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Vùng không hợp lệ: chỉ nhận Bắc, Trung hoặc Nam.", region.Errors!["region"]);
        await Fail(Http.GetAsync<object>($"{Public}/heritages?heritageType=KHONG_CO"), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.GetAsync<object>($"{Public}/heritages?recognitionLevel=99"), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.GetAsync<object>($"{Public}/heritages?period=NGAY_XUA"), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.GetAsync<object>($"{Public}/heritages?period=42"), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.GetAsync<object>($"{Public}/heritages/map?region=Tay"), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.GetAsync<object>($"{Public}/heritages?provinceId=abc"), HttpStatusCode.BadRequest, "INVALID");
    }

    [Fact]
    public async Task Period_filter_matches_phases_and_events_that_overlap_the_fixed_year_range()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        // Tiền sử → phong kiến: giai đoạn -28000..1010.
        var trangAn = await _kit.PublishedAsync(admin, new Spec($"Tràng An {tag}"));
        await _kit.AddPhaseAsync(admin, trangAn.Id, "Từ tiền sử tới Đại Việt", -28000, 1010);
        // Chỉ có sự kiện năm 40 và 43 (Bắc thuộc), giai đoạn không có năm.
        var haiBaTrung = await _kit.PublishedAsync(admin, new Spec($"Hai Bà Trưng {tag}"));
        var uprising = await _kit.AddPhaseAsync(admin, haiBaTrung.Id, "Khởi nghĩa", null, null);
        await _kit.AddEventAsync(admin, uprising.Id, "Khởi nghĩa nổ ra", 40);
        await _kit.AddEventAsync(admin, uprising.Id, "Kết thúc", 43);
        // Giai đoạn 1953-1954 (hiện đại), không phải cận đại.
        var dienBien = await _kit.PublishedAsync(admin, new Spec($"Điện Biên Phủ {tag}"));
        await _kit.AddPhaseAsync(admin, dienBien.Id, "Chiến dịch", 1953, 1954);
        // Giai đoạn kéo dài đến nay (chưa có năm kết thúc): từ 1903.
        var mySon = await _kit.PublishedAsync(admin, new Spec($"Mỹ Sơn {tag}"));
        await _kit.AddPhaseAsync(admin, mySon.Id, "Nghiên cứu và bảo tồn", 1903, null);
        // Chỉ có năm kết thúc: tính như một mốc tại năm đó (939).
        var openStart = await _kit.PublishedAsync(admin, new Spec($"Mốc 939 {tag}"));
        await _kit.AddPhaseAsync(admin, openStart.Id, "Đến năm 939", null, 939);
        // Không có timeline nào: không hiện khi lọc theo thời kỳ.
        var noTimeline = await _kit.PublishedAsync(admin, new Spec($"Chưa có timeline {tag}"));

        async Task<string[]> In(string period) => S([.. Names(await SearchAsync($"{K(tag)}&period={period}"))]);

        Assert.Equal(S(trangAn.Name), await In("PREHISTORIC"));
        Assert.Equal(S(trangAn.Name, haiBaTrung.Name), await In("NORTHERN_RULE"));
        Assert.Equal(S(trangAn.Name, openStart.Name), await In("INDEPENDENT_FEUDAL"));
        Assert.Equal(S(mySon.Name), await In("MODERN"));
        Assert.Equal(S(dienBien.Name, mySon.Name), await In("CONTEMPORARY"));
        Assert.DoesNotContain(noTimeline.Name, await In("CONTEMPORARY"));
        Assert.Equal(6, (await SearchAsync(K(tag))).TotalItems);
        Assert.Equal([dienBien.Name], Names(await SearchAsync($"period=contemporary&{K("dien bien " + tag)}")));

        // Bản đồ dùng cùng bộ lọc.
        var map = await Expect(Http.GetAsync<HeritageMapDto>($"{Public}/heritages/map?{K(tag)}&period=MODERN"));
        Assert.Equal([mySon.Id], map.Pins.Select(p => p.Id));
    }

    // ---- Phân trang ----

    [Fact]
    public async Task Results_are_paged_and_page_size_is_capped()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        for (var i = 1; i <= 5; i++) await _kit.PublishedAsync(admin, new Spec($"Di sản số {i} {tag}"));

        var first = await SearchAsync($"{K(tag)}&pageSize=2");
        Assert.Equal((1, 2, 5, 3, 2), (first.Page, first.PageSize, first.TotalItems, first.TotalPages, first.Items.Count));
        var third = await SearchAsync($"{K(tag)}&pageSize=2&page=3");
        Assert.Single(third.Items);
        var all = new List<string>();
        for (var p = 1; p <= 3; p++) all.AddRange(Names(await SearchAsync($"{K(tag)}&pageSize=2&page={p}")));
        Assert.Equal(5, all.Distinct().Count());
        Assert.Empty((await SearchAsync($"{K(tag)}&pageSize=2&page=4")).Items);

        var defaults = await SearchAsync(K(tag));
        Assert.Equal((1, Paging.DefaultPageSize), (defaults.Page, defaults.PageSize));
        Assert.Equal("Số dòng mỗi trang không hợp lệ: phải từ 1 đến 100.",
            (await Fail(Http.GetAsync<object>($"{Public}/heritages?pageSize=101"), HttpStatusCode.BadRequest, "INVALID")).Errors!["pageSize"]);
        await Fail(Http.GetAsync<object>($"{Public}/heritages?pageSize=0"), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.GetAsync<object>($"{Public}/heritages?page=0"), HttpStatusCode.BadRequest, "INVALID");
    }

    [Fact]
    public async Task A_card_carries_only_public_fields()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var h = await _kit.PublishedAsync(admin, new Spec($"Thẻ di sản {tag}", Level: "UNESCO", ProvinceCode: "46"));

        var card = Assert.Single((await SearchAsync(K(tag))).Items);
        Assert.Equal((h.Id, h.Slug, h.Name, h.ShortDescription, h.ThumbnailUrl), (card.Id, card.Slug, card.Name, card.ShortDescription, card.ThumbnailUrl));
        Assert.Equal(("Thành phố Huế", "Trung", HeritageType.TANGIBLE, RecognitionLevel.UNESCO, 1999),
            (card.ProvinceName, card.Region, card.HeritageType, card.RecognitionLevel, card.RecognizedYear));

        using var raw = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync($"{Public}/heritages?{K(tag)}"));
        var fields = raw.RootElement.GetProperty("data").GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(new[] { "id", "name", "provinceId", "provinceName", "recognitionLevel", "recognizedYear", "region", "heritageType",
            "shortDescription", "slug", "thumbnailUrl" }.Order(), fields);
    }

    // ---- Bản đồ ----

    [Fact]
    public async Task Map_pins_only_heritages_with_coordinates_and_counts_the_ones_without()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var pinned = await _kit.PublishedAsync(admin, new Spec($"Có vị trí {tag}", Level: "UNESCO", ProvinceCode: "46"));
        await _kit.AddPhaseAsync(admin, pinned.Id, "Giai đoạn", 1800, 1900);
        _ = await _kit.PublishedAsync(admin, new Spec($"Phi vật thể A {tag}", Type: "INTANGIBLE", Latitude: null, Longitude: null));
        _ = await _kit.PublishedAsync(admin, new Spec($"Phi vật thể B {tag}", Type: "INTANGIBLE", Latitude: null, Longitude: null));

        var map = await Expect(Http.GetAsync<HeritageMapDto>($"{Public}/heritages/map?{K(tag)}"));

        Assert.Equal((3, 2, false), (map.TotalMatched, map.WithoutLocation, map.IsTruncated));
        var pin = Assert.Single(map.Pins);
        Assert.Equal((pinned.Id, pinned.Slug, pinned.Name), (pin.Id, pin.Slug, pin.Name));
        Assert.Equal((21.033333m, 105.85m), (pin.Latitude, pin.Longitude));
        Assert.Equal(("Thành phố Huế", HeritageType.TANGIBLE, RecognitionLevel.UNESCO, true),
            (pin.ProvinceName, pin.HeritageType, pin.RecognitionLevel, pin.HasTimeline));
        Assert.Equal(pinned.ShortDescription, pin.ShortDescription);
        Assert.Equal(pinned.ThumbnailUrl, pin.ThumbnailUrl);

        var filtered = await Expect(Http.GetAsync<HeritageMapDto>($"{Public}/heritages/map?{K(tag)}&heritageType=INTANGIBLE"));
        Assert.Equal((0, 2, 2), (filtered.Pins.Count, filtered.WithoutLocation, filtered.TotalMatched));
    }

    [Fact]
    public async Task Map_returns_at_most_500_pins_keeping_the_highest_recognition_first()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var top = await _kit.PublishedAsync(admin, new Spec($"Quan trọng nhất {tag}", Level: "UNESCO"));
        var provinceId = await _kit.ProvinceIdAsync("01");
        await factory.WithDbAsync(async db =>
        {
            db.Heritages.AddRange(Enumerable.Range(0, DiscoveryService.MaxMapPins + 5).Select(i => new Heritage
            {
                ProvinceId = provinceId,
                Name = $"Ghim số {i} {tag}",
                Slug = $"ghim-{i}-{tag}",
                ShortDescription = "Ghim thử",
                HeritageType = HeritageType.TANGIBLE,
                RecognitionLevel = RecognitionLevel.PROVINCIAL,
                Latitude = 16m + i % 100 / 100m,
                Longitude = 107m + i % 100 / 100m,
                Status = ContentStatus.PUBLISHED,
            }));
            await db.SaveChangesAsync();
            return 0;
        });

        var map = await Expect(Http.GetAsync<HeritageMapDto>($"{Public}/heritages/map?{K(tag)}"));

        Assert.Equal(DiscoveryService.MaxMapPins, map.Pins.Count);
        Assert.True(map.IsTruncated);
        Assert.Equal(DiscoveryService.MaxMapPins + 6, map.TotalMatched);
        Assert.Equal(top.Id, map.Pins[0].Id);
        Assert.Equal(map.Pins.Count, map.Pins.Select(p => p.Id).Distinct().Count());
    }

    // ---- Chi tiết ----

    [Fact]
    public async Task Detail_has_content_gallery_with_credit_sources_and_a_short_timeline()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var h = await _kit.PublishedAsync(admin, new Spec($"Chi tiết {tag}", Level: "NATIONAL_SPECIAL", ProvinceCode: "48", Extra: "Nội dung riêng"));
        var second = await Expect(Http.PostAsync<MediaDto>($"{Admin}/heritages/{h.Id}/media", new
        {
            mediaType = "IMAGE",
            url = $"https://upload.wikimedia.org/{Unique()}.jpg",
            caption = "Ảnh thứ hai",
            author = "Trần Thị Bình",
            license = "CC BY 4.0",
            source = "Cục Di sản văn hóa",
            sortOrder = 5,
        }, admin), HttpStatusCode.Created);
        await Expect(Http.PostAsync<ReferenceDto>($"{Admin}/heritages/{h.Id}/references", new
        {
            title = "Trang thông tin — Cục Di sản văn hóa, truy cập 26/09/2026",
            url = "https://dsvh.gov.vn/di-san",
            sourceType = "WEBSITE",
        }, admin), HttpStatusCode.Created);
        var early = await _kit.AddPhaseAsync(admin, h.Id, "Thời kỳ đầu", -700, 100, order: 1);
        var late = await _kit.AddPhaseAsync(admin, h.Id, "Thời kỳ nay", 1962, null, order: 2);
        await _kit.AddEventAsync(admin, early.Id, "Sự kiện 1", -500);
        await _kit.AddEventAsync(admin, early.Id, "Sự kiện 2", 50);
        await _kit.AddEventAsync(admin, late.Id, "Sự kiện 3", 1962);

        var detail = await Expect(Http.GetAsync<PublicHeritageDetailDto>($"{Public}/heritages/{h.Slug}"));

        Assert.Equal((h.Id, h.Slug, h.Name, h.ShortDescription), (detail.Id, detail.Slug, detail.Name, detail.ShortDescription));
        Assert.StartsWith("Nội dung riêng", detail.Content);
        Assert.Equal((HeritageType.TANGIBLE, RecognitionLevel.NATIONAL_SPECIAL, 1999, "1 Phố Cổ, Hà Nội"),
            (detail.HeritageType, detail.RecognitionLevel, detail.RecognizedYear, detail.Address));
        Assert.Equal(("Thành phố Đà Nẵng", "Trung"), (detail.Province!.Name, detail.Province.Region));
        Assert.Equal((21.033333m, 105.85m), (detail.Latitude, detail.Longitude));
        Assert.True(detail.HasTimeline);

        Assert.Equal(2, detail.Media.Count);
        Assert.Equal(h.ThumbnailUrl, detail.Media[0].Url);
        Assert.True(detail.Media[0].IsThumbnail);
        Assert.Equal(("Nguyễn Văn An", "CC BY-SA 4.0", "Wikimedia Commons"), (detail.Media[0].Author, detail.Media[0].License, detail.Media[0].Source));
        Assert.Equal("Nguyễn Văn An · CC BY-SA 4.0 · Wikimedia Commons", detail.Media[0].Credit);
        Assert.Equal((second.Id, false, "Trần Thị Bình"), (detail.Media[1].Id, detail.Media[1].IsThumbnail, detail.Media[1].Author));

        Assert.Equal(["GOV_DOC", "WEBSITE"], detail.References.Select(r => r.SourceType));
        Assert.Equal(("Văn bản nhà nước", "Website"), (detail.References[0].SourceTypeLabel, detail.References[1].SourceTypeLabel));
        Assert.Equal("https://dsvh.gov.vn/di-san", detail.References[1].Url);

        Assert.Equal(["Thời kỳ đầu", "Thời kỳ nay"], detail.Timeline.Select(t => t.Name));
        Assert.Equal(["700 TCN – 100", "1962 – nay"], detail.Timeline.Select(t => t.RangeLabel));
        Assert.Equal([2, 1], detail.Timeline.Select(t => t.EventCount));

        // Đường dẫn tra không phân biệt hoa thường; không lộ trạng thái hay điều kiện xuất bản.
        var upper = await Expect(Http.GetAsync<PublicHeritageDetailDto>($"{Public}/heritages/{h.Slug!.ToUpperInvariant()}"));
        Assert.Equal(h.Id, upper.Id);
        var json = await Http.GetStringAsync($"{Public}/heritages/{h.Slug}");
        foreach (var hidden in new[] { "\"status\"", "\"readiness\"", "\"createdAt\"", "\"updatedAt\"", "\"placeId\"", "\"searchText\"", "\"searchName\"" })
            Assert.DoesNotContain(hidden, json);
    }

    [Fact]
    public async Task Detail_of_a_heritage_without_timeline_says_so()
    {
        var admin = await AdminAsync();
        var h = await _kit.PublishedAsync(admin, new Spec($"Không timeline {Unique()}", Type: "INTANGIBLE", Latitude: null, Longitude: null));

        var detail = await Expect(Http.GetAsync<PublicHeritageDetailDto>($"{Public}/heritages/{h.Slug}"));
        Assert.False(detail.HasTimeline);
        Assert.Empty(detail.Timeline);
        Assert.Null(detail.Latitude);

        var timeline = await Expect(Http.GetAsync<PublicTimelineDto>($"{Public}/heritages/{h.Slug}/timeline"));
        Assert.Empty(timeline.Phases);
        Assert.Equal(h.Name, timeline.Name);
    }

    // ---- Timeline ----

    [Fact]
    public async Task Timeline_lists_phases_in_order_with_events_by_date_and_ready_made_labels()
    {
        var admin = await AdminAsync();
        var h = await _kit.PublishedAsync(admin, new Spec($"Timeline {Unique()}", ProvinceCode: "01"));
        var modern = await _kit.AddPhaseAsync(admin, h.Id, "Giai đoạn sau", 1945, null, order: 2);
        var ancient = await _kit.AddPhaseAsync(admin, h.Id, "Giai đoạn đầu", -179, 938, order: 1);
        var e2 = await _kit.AddEventAsync(admin, modern.Id, "Sự kiện tháng 5", 1954, 5, 7, endYear: 1954, endMonth: 5, endDay: 8);
        var e1 = await _kit.AddEventAsync(admin, modern.Id, "Sự kiện tháng 3", 1954, 3, 13);
        var e3 = await _kit.AddEventAsync(admin, modern.Id, "Sự kiện chỉ có năm", 1954);
        var bc = await _kit.AddEventAsync(admin, ancient.Id, "Trước Công nguyên", -111);
        await Expect(Http.PostAsync<MediaDto>($"{Admin}/events/{e1.Id}/media", new
        {
            mediaType = "IMAGE",
            url = $"https://upload.wikimedia.org/{Unique()}.jpg",
            caption = "Ảnh sự kiện",
            author = "Lê Văn Cường",
            license = "CC0 1.0",
            source = "Wikimedia Commons",
        }, admin), HttpStatusCode.Created);

        var timeline = await Expect(Http.GetAsync<PublicTimelineDto>($"{Public}/heritages/{h.Slug}/timeline"));

        Assert.Equal((h.Id, h.Slug, h.Name, h.ThumbnailUrl, "Thành phố Hà Nội"),
            (timeline.HeritageId, timeline.Slug, timeline.Name, timeline.ThumbnailUrl, timeline.ProvinceName));
        Assert.Equal(["Giai đoạn đầu", "Giai đoạn sau"], timeline.Phases.Select(p => p.Name));
        Assert.Equal(["179 TCN – 938", "1945 – nay"], timeline.Phases.Select(p => p.RangeLabel));
        Assert.Equal([bc.Id], timeline.Phases[0].Events.Select(e => e.Id));
        Assert.Equal("111 TCN", timeline.Phases[0].Events[0].DateLabel);

        var events = timeline.Phases[1].Events;
        Assert.Equal([e3.Id, e1.Id, e2.Id], events.Select(e => e.Id));         // thiếu tháng đứng trước, rồi tháng 3, rồi tháng 5
        Assert.Equal(["1954", "13/03/1954", "07/05/1954 – 08/05/1954"], events.Select(e => e.DateLabel));
        Assert.Empty(events[0].Media);
        var media = Assert.Single(events[1].Media);
        Assert.Equal(("Lê Văn Cường", "CC0 1.0"), (media.Author, media.License));
        Assert.False(media.IsThumbnail);
    }

    // ---- Danh mục ----

    [Fact]
    public async Task Provinces_are_open_to_guests_and_cached_by_the_browser()
    {
        await _kit.ProvinceIdAsync("01");
        await _kit.ProvinceIdAsync("46");

        var response = await Http.GetAsync($"{Public}/provinces");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("max-age=3600", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.CacheControl?.Public);

        var provinces = await Expect(Http.GetAsync<List<ProvinceDto>>($"{Public}/provinces"));
        var codes = provinces.Select(p => p.Code).ToList();
        Assert.Equal(codes.Order(StringComparer.Ordinal), codes);
        Assert.Contains(provinces, p => p is { Code: "46", Name: "Thành phố Huế", Region: "Trung" });

        // Danh mục tỉnh của quản trị vẫn cần quyền ADMIN.
        var (anonymous, _) = await Http.GetAsync<object>($"{Admin}/provinces");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Filter_catalog_lists_every_option_with_its_vietnamese_label()
    {
        await _kit.ProvinceIdAsync("01");

        var response = await Http.GetAsync($"{Public}/filters");
        Assert.True(response.Headers.CacheControl?.Public);
        var catalog = await Expect(Http.GetAsync<FilterCatalogDto>($"{Public}/filters"));

        Assert.NotEmpty(catalog.Provinces);
        Assert.Equal([("Bắc", "Miền Bắc"), ("Trung", "Miền Trung"), ("Nam", "Miền Nam")],
            catalog.Regions.Select(r => (r.Value, r.Label)));
        Assert.Equal(["TANGIBLE", "INTANGIBLE", "NATURAL", "MIXED", "DOCUMENTARY"], catalog.HeritageTypes.Select(t => t.Value));
        Assert.Equal("Phi vật thể", catalog.HeritageTypes.Single(t => t.Value == "INTANGIBLE").Label);
        Assert.Equal(["UNESCO", "NATIONAL_SPECIAL", "NATIONAL", "PROVINCIAL", "NONE"], catalog.RecognitionLevels.Select(l => l.Value));
        Assert.Equal("Quốc gia đặc biệt", catalog.RecognitionLevels.Single(l => l.Value == "NATIONAL_SPECIAL").Label);
        Assert.Equal(
            [("PREHISTORIC", "Tiền sử và sơ sử", (int?)null, (int?)-180), ("NORTHERN_RULE", "Bắc thuộc", -179, 938),
             ("INDEPENDENT_FEUDAL", "Phong kiến độc lập", 939, 1857), ("MODERN", "Cận đại", 1858, 1944), ("CONTEMPORARY", "Hiện đại", 1945, null)],
            catalog.Periods.Select(p => (p.Value, p.Label, p.FromYear, p.ToYear)));

        // Mọi giá trị trong danh mục dùng lại được làm bộ lọc.
        foreach (var period in catalog.Periods)
            Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<object>($"{Public}/heritages?period={period.Value}")).Response.StatusCode);
        foreach (var region in catalog.Regions)
            Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<object>($"{Public}/heritages?region={Uri.EscapeDataString(region.Value)}")).Response.StatusCode);
    }

    // ---- Cột tìm kiếm ----

    [Fact]
    public async Task Search_columns_are_filled_when_saving_and_backfilled_for_older_rows_without_touching_updated_at()
    {
        var admin = await AdminAsync();
        var tag = Unique();
        var h = await _kit.PublishedAsync(admin, new Spec($"Dữ liệu cũ {tag}", Extra: "riêngbiệt"));
        var stamped = await factory.WithDbAsync(async db => await db.Heritages.AsNoTracking()
            .Where(x => x.Id == h.Id).Select(x => new { x.SearchName, x.SearchText, x.UpdatedAt }).SingleAsync());
        Assert.Equal($" du lieu cu {tag}", stamped.SearchName);
        Assert.StartsWith($" du lieu cu {tag} | mo ta ngan cua du lieu cu {tag} | 1 pho co ha noi | riengbiet tu tu", stamped.SearchText);

        // Giả lập dòng có trước migration: hai cột trống. Khởi động lại bước khởi tạo thì điền lại, mốc sửa gần nhất giữ nguyên.
        await factory.WithDbAsync(async db => await db.Heritages.Where(x => x.Id == h.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchName, (string?)null).SetProperty(x => x.SearchText, (string?)null)));
        Assert.Empty((await SearchAsync(K($"du lieu cu {tag}"))).Items);

        await factory.Services.InitializeDatabaseAsync();

        var restored = await factory.WithDbAsync(async db => await db.Heritages.AsNoTracking()
            .Where(x => x.Id == h.Id).Select(x => new { x.SearchName, x.SearchText, x.UpdatedAt }).SingleAsync());
        Assert.Equal(stamped.SearchName, restored.SearchName);
        Assert.Equal(stamped.SearchText, restored.SearchText);
        Assert.Equal(stamped.UpdatedAt, restored.UpdatedAt);
        Assert.Single((await SearchAsync(K($"du lieu cu {tag}"))).Items);
    }
}
