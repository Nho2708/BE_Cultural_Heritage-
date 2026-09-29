using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của kho di sản: đường dẫn, số từ, tọa độ, ngày của sự kiện, ghi công ảnh, điều kiện xuất bản.</summary>
public sealed class HeritageRulesTests
{
    [Theory]
    [InlineData("Khu Trung tâm Hoàng thành Thăng Long – Hà Nội", "khu-trung-tam-hoang-thanh-thang-long-ha-noi")]
    [InlineData("  Đền Hùng   (Phú Thọ)  ", "den-hung-phu-tho")]
    [InlineData("Quần thể danh thắng Tràng An!!!", "quan-the-danh-thang-trang-an")]
    [InlineData("Ca trù, 2 giai đoạn", "ca-tru-2-giai-doan")]
    [InlineData("ĐÔNG SƠN", "dong-son")]
    [InlineData("---", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Slug_is_generated_without_accents_lowercase_and_hyphenated(string? name, string expected) =>
        Assert.Equal(expected, HeritageRules.Slugify(name));

    [Fact]
    public void Slug_is_cut_to_the_column_length_without_a_trailing_hyphen()
    {
        var slug = HeritageRules.Slugify(string.Join(' ', Enumerable.Repeat("dong", 100)));
        Assert.True(slug.Length <= HeritageRules.SlugMaxLength);
        Assert.False(slug.EndsWith('-'));
    }

    [Theory]
    [InlineData("hoang-thanh-thang-long", true)]
    [InlineData("a", true)]
    [InlineData("di-san-2", true)]
    [InlineData("", false)]
    [InlineData("-abc", false)]
    [InlineData("abc-", false)]
    [InlineData("ab--cd", false)]
    [InlineData("Ab-cd", false)]
    [InlineData("đền-hùng", false)]
    [InlineData("ab cd", false)]
    public void Slug_validity(string slug, bool expected) => Assert.Equal(expected, HeritageRules.IsValidSlug(slug));

    [Fact]
    public void Words_are_counted_without_markdown_markers()
    {
        Assert.Equal(0, HeritageRules.CountWords(null));
        Assert.Equal(0, HeritageRules.CountWords("  \n "));
        Assert.Equal(9, HeritageRules.CountWords("## Giới thiệu\nKhu di tích - nơi 3 triều đại"));
    }

    [Theory]
    [InlineData(2026, 2, 29, false)]
    [InlineData(2024, 2, 29, true)]
    [InlineData(1900, 2, 29, false)]
    [InlineData(2000, 2, 29, true)]
    [InlineData(-179, 2, 28, true)]
    [InlineData(1010, 4, 31, false)]
    [InlineData(1010, 12, 31, true)]
    public void Day_must_exist_in_the_month(int year, int month, int day, bool valid) =>
        Assert.Equal(valid, HeritageRules.IsValidDay(year, month, day));

    [Fact]
    public void A_day_needs_a_month_and_the_month_must_be_1_to_12()
    {
        Assert.True(HeritageRules.IsValidDay(1010, null, null));
        Assert.False(HeritageRules.IsValidDay(1010, null, 5));
        Assert.False(HeritageRules.IsValidDay(1010, 13, 1));
        Assert.False(HeritageRules.IsValidDay(1010, 3, 0));
    }

    [Theory]
    [InlineData(1010, 3, 5, 1009, null, null, true)]          // năm kết thúc nhỏ hơn
    [InlineData(1010, 3, 5, 1010, 2, null, true)]             // cùng năm, tháng nhỏ hơn
    [InlineData(1010, 3, 5, 1010, 3, 4, true)]                // cùng tháng, ngày nhỏ hơn
    [InlineData(1010, 3, 5, 1010, 3, 5, false)]               // trùng thời điểm
    [InlineData(1010, 3, 5, 1010, null, null, false)]         // chỉ có năm, cùng năm: không so tiếp
    [InlineData(1010, null, null, 1010, 1, 1, false)]         // bắt đầu chỉ có năm
    [InlineData(-200, null, null, -180, null, null, false)]   // năm âm: -180 sau -200
    [InlineData(-180, null, null, -200, null, null, true)]
    public void End_is_compared_only_at_the_precision_both_sides_have(
        int sy, int? sm, int? sd, int ey, int? em, int? ed, bool endsBefore) =>
        Assert.Equal(endsBefore, HeritageRules.EndsBeforeStart(sy, sm, sd, ey, em, ed));

    [Theory]
    [InlineData(8.0, true)]
    [InlineData(23.5, true)]
    [InlineData(7.99, false)]
    [InlineData(23.51, false)]
    public void Latitude_range(double value, bool valid) => Assert.Equal(valid, HeritageRules.IsLatitude((decimal)value));

    [Theory]
    [InlineData(102.0, true)]
    [InlineData(117.5, true)]
    [InlineData(101.99, false)]
    [InlineData(117.51, false)]
    public void Longitude_range(double value, bool valid) => Assert.Equal(valid, HeritageRules.IsLongitude((decimal)value));

    [Theory]
    [InlineData("CC0", true)]
    [InlineData("CC0 1.0", true)]
    [InlineData("cc by 4.0", true)]
    [InlineData("CC BY-SA 3.0 (kèm GFDL)", true)]
    [InlineData("Public domain (PD-old-100)", true)]
    [InlineData("Phạm vi công cộng", true)]
    [InlineData("Ảnh nhóm tự chụp", true)]
    [InlineData("CC BY-NC 4.0", false)]
    [InlineData("CC BY-ND 2.0", false)]
    [InlineData("CC BYX", false)]
    [InlineData("All rights reserved", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_open_licenses_are_accepted(string? license, bool allowed) =>
        Assert.Equal(allowed, HeritageRules.IsAllowedLicense(license));

    [Fact]
    public void Credit_is_composed_from_three_parts_and_parsed_back()
    {
        var credit = HeritageRules.ComposeCredit(" Christophe95 ", "CC BY-SA 4.0", " Wikimedia Commons ");
        Assert.Equal("Christophe95 · CC BY-SA 4.0 · Wikimedia Commons", credit);
        Assert.Equal(("Christophe95", "CC BY-SA 4.0", "Wikimedia Commons"), HeritageRules.ParseCredit(credit));
        Assert.Null(HeritageRules.ParseCredit("Ảnh nhóm tự chụp"));
        Assert.Null(HeritageRules.ParseCredit(null));
    }

    private static Heritage CompleteHeritage() => new()
    {
        Name = "Hoàng thành Thăng Long",
        Slug = "hoang-thanh-thang-long",
        ShortDescription = "Trung tâm quyền lực của kinh đô Thăng Long.",
        Content = string.Join(' ', Enumerable.Repeat("từ", HeritageRules.MinContentWords)),
        HeritageType = HeritageType.TANGIBLE,
        RecognitionLevel = RecognitionLevel.UNESCO,
        RecognizedYear = 2010,
        ProvinceId = 1,
        Latitude = 21.03m,
        Longitude = 105.83m,
        ThumbnailUrl = "https://example.org/a.jpg",
    };

    private static string[] Missing(Heritage h, bool thumbnailInLibrary = true, int references = 1) =>
        HeritagePublishing.FindMissing(h, thumbnailInLibrary, references, 2026).Select(m => m.Field).ToArray();

    [Fact]
    public void A_complete_heritage_can_be_published() => Assert.Empty(Missing(CompleteHeritage()));

    [Fact]
    public void An_empty_draft_lists_everything_that_is_missing()
    {
        Assert.Equal(["name", "slug", "shortDescription", "content", "heritageType", "recognitionLevel", "thumbnailUrl", "provinceId", "references"],
            Missing(new Heritage(), thumbnailInLibrary: false, references: 0));
    }

    [Fact]
    public void Content_needs_200_words()
    {
        var h = CompleteHeritage();
        h.Content = string.Join(' ', Enumerable.Repeat("từ", HeritageRules.MinContentWords - 1));
        Assert.Equal(["content"], Missing(h));
    }

    [Fact]
    public void Thumbnail_must_be_in_the_heritage_media_library()
    {
        Assert.Equal(["thumbnailUrl"], Missing(CompleteHeritage(), thumbnailInLibrary: false));
    }

    [Theory]
    [InlineData(HeritageType.TANGIBLE, true)]
    [InlineData(HeritageType.NATURAL, true)]
    [InlineData(HeritageType.MIXED, true)]
    [InlineData(HeritageType.INTANGIBLE, false)]
    [InlineData(HeritageType.DOCUMENTARY, false)]
    public void Coordinates_are_required_only_for_tangible_natural_and_mixed(HeritageType type, bool required)
    {
        var h = CompleteHeritage();
        h.HeritageType = type;
        h.Latitude = h.Longitude = null;
        Assert.Equal(required ? new[] { "coordinates" } : Array.Empty<string>(), Missing(h));
    }

    [Theory]
    [InlineData(HeritageType.TANGIBLE, true)]
    [InlineData(HeritageType.INTANGIBLE, false)]
    [InlineData(HeritageType.DOCUMENTARY, false)]
    public void Province_may_be_empty_only_for_intangible_or_documentary(HeritageType type, bool required)
    {
        var h = CompleteHeritage();
        h.HeritageType = type;
        h.ProvinceId = null;
        h.Latitude = 21m;
        h.Longitude = 105m;
        Assert.Equal(required ? new[] { "provinceId" } : Array.Empty<string>(), Missing(h));
    }

    [Fact]
    public void Recognized_year_is_required_once_ranked_and_not_in_the_future()
    {
        var h = CompleteHeritage();
        h.RecognizedYear = null;
        Assert.Equal(["recognizedYear"], Missing(h));
        h.RecognizedYear = 2027;
        Assert.Equal(["recognizedYear"], Missing(h));
        h.RecognitionLevel = RecognitionLevel.NONE;
        Assert.Empty(Missing(h));
    }
}
