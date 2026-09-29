using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của khám phá di sản công khai: gấp chữ để tìm không dấu, thời kỳ, chữ hiển thị của mốc thời gian, vùng.</summary>
public sealed class DiscoveryRulesTests
{
    [Theory]
    [InlineData("Thăng Long", "thang long")]
    [InlineData("THĂNG LONG", "thang long")]
    [InlineData("thang long", "thang long")]
    [InlineData("Đền Hùng", "den hung")]
    [InlineData("đền hùng", "den hung")]
    [InlineData("Ơ, Ư, Â, Ê, Ô, Ă", "o u a e o a")]
    [InlineData("Ca trù (Hà Nội) – 2 giai đoạn!", "ca tru ha noi 2 giai doan")]
    [InlineData("  nhiều   khoảng\ttrắng \n", "nhieu khoang trang")]
    [InlineData("50%_[x]", "50 x")]
    [InlineData("---", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Fold_removes_accents_case_and_punctuation(string? text, string expected) =>
        Assert.Equal(expected, SearchText.Fold(text));

    [Fact]
    public void Fold_treats_precomposed_and_decomposed_vietnamese_the_same()
    {
        var precomposed = "Thăng Long".Normalize(System.Text.NormalizationForm.FormC);
        var decomposed = "Thăng Long".Normalize(System.Text.NormalizationForm.FormD);
        Assert.Equal(SearchText.Fold(precomposed), SearchText.Fold(decomposed));
    }

    [Fact]
    public void Tokens_are_folded_distinct_and_limited()
    {
        Assert.Equal(["thang", "long", "ha"], SearchText.Tokens("Thăng  Long, thăng, Hà"));
        Assert.Empty(SearchText.Tokens("!!"));
        Assert.Equal(SearchText.MaxKeywordTokens, SearchText.Tokens("a1 a2 a3 a4 a5 a6 a7 a8 a9 a10").Count);
    }

    [Fact]
    public void Composed_search_text_keeps_fields_apart_so_a_phrase_cannot_span_two_fields()
    {
        var text = SearchText.Compose("Đền Hùng", "Nơi thờ vua", "Phú Thọ", "Nội dung dài");
        Assert.Equal(" den hung | noi tho vua | phu tho | noi dung dai", text);
        Assert.Equal(" den hung", SearchText.ComposeName("Đền Hùng"));
        Assert.Equal(string.Empty, SearchText.ComposeName(" ! "));
        Assert.Equal("% thang lo%", SearchText.WordPrefixPattern("thang lo"));
        Assert.DoesNotContain("hung noi", text);
        Assert.DoesNotContain(SearchText.FieldSeparator.ToString(), SearchText.Fold("a | b"));
    }

    [Fact]
    public void Period_ranges_are_the_five_fixed_intervals_in_order()
    {
        Assert.Equal(
            [(HeritagePeriod.PREHISTORIC, (int?)null, (int?)-180), (HeritagePeriod.NORTHERN_RULE, -179, 938),
             (HeritagePeriod.INDEPENDENT_FEUDAL, 939, 1857), (HeritagePeriod.MODERN, 1858, 1944), (HeritagePeriod.CONTEMPORARY, 1945, null)],
            HeritagePeriods.All.Select(p => (p.Period, p.FromYear, p.ToYear)));
        Assert.Equal("Bắc thuộc", HeritagePeriods.Of(HeritagePeriod.NORTHERN_RULE).Label);
    }

    [Theory]
    [InlineData(1010, "1010")]
    [InlineData(-179, "179 TCN")]
    [InlineData(-28000, "28000 TCN")]
    public void Years_before_the_common_era_read_tcn(int year, string expected) => Assert.Equal(expected, HeritageTimeText.Year(year));

    [Theory]
    [InlineData(1010, null, null, "1010")]
    [InlineData(1010, 3, null, "03/1010")]
    [InlineData(1010, 3, 5, "05/03/1010")]
    [InlineData(-179, null, null, "179 TCN")]
    [InlineData(-179, 3, null, "03/179 TCN")]
    public void Moments_read_day_month_year_down_to_the_detail_that_is_known(int year, int? month, int? day, string expected) =>
        Assert.Equal(expected, HeritageTimeText.Moment(year, month, day));

    [Fact]
    public void Event_ranges_are_a_single_moment_or_start_to_end()
    {
        Assert.Equal("1010", HeritageTimeText.EventRange(1010, null, null, null, null, null));
        Assert.Equal("1953 – 1954", HeritageTimeText.EventRange(1953, null, null, 1954, null, null));
        Assert.Equal("13/03/1954 – 07/05/1954", HeritageTimeText.EventRange(1954, 3, 13, 1954, 5, 7));
        Assert.Equal("1954", HeritageTimeText.EventRange(1954, null, null, 1954, null, null));
    }

    [Fact]
    public void Phase_ranges_show_ongoing_and_open_start()
    {
        Assert.Equal("1010 – 1821", HeritageTimeText.PhaseRange(1010, 1821));
        Assert.Equal("1962 – nay", HeritageTimeText.PhaseRange(1962, null));
        Assert.Equal("đến 1821", HeritageTimeText.PhaseRange(null, 1821));
        Assert.Equal("28000 TCN – 1010", HeritageTimeText.PhaseRange(-28000, 1010));
        Assert.Equal("1010", HeritageTimeText.PhaseRange(1010, 1010));
        Assert.Equal(string.Empty, HeritageTimeText.PhaseRange(null, null));
    }

    [Theory]
    [InlineData("Bắc", "Bắc")]
    [InlineData("bac", "Bắc")]
    [InlineData(" TRUNG ", "Trung")]
    [InlineData("nam", "Nam")]
    [InlineData("Tây", null)]
    [InlineData("", null)]
    public void Region_input_is_matched_with_or_without_accents(string input, string? expected) =>
        Assert.Equal(expected, HeritageLabels.NormalizeRegion(input));
}
