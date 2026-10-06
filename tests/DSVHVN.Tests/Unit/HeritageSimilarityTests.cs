using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Độ tương đồng content-based giữa hai di sản: cùng loại, tỉnh hoặc vùng, cấp xếp hạng, khoảng năm, chữ giống nhau; phương án dự phòng; câu lý do.</summary>
public sealed class HeritageSimilarityTests
{
    private static HeritageFeatures H(
        long id, string name = "Di sản", HeritageType? type = HeritageType.TANGIBLE, int? province = 1, string? region = "Bắc",
        RecognitionLevel? level = RecognitionLevel.NATIONAL, int? from = null, int? to = null, string? text = null) =>
        new(id, name, type, province, region, level, from, to, text ?? name);

    [Fact]
    public void Two_heritages_with_nothing_in_common_have_no_factors_and_score_zero()
    {
        var a = H(1, "Hoàng thành", HeritageType.TANGIBLE, 1, "Bắc", RecognitionLevel.UNESCO);
        var b = H(2, "Ca trù", HeritageType.INTANGIBLE, 30, "Nam", RecognitionLevel.PROVINCIAL);
        var (score, factors) = HeritageSimilarity.Compare(a, b);
        Assert.Equal(0d, score);
        Assert.Empty(factors);
    }

    [Fact]
    public void The_same_province_counts_instead_of_the_same_region_and_both_never_add_up()
    {
        var sameProvince = HeritageSimilarity.Compare(H(1, province: 1, region: "Bắc"), H(2, "Khác", type: null, province: 1, region: "Bắc", level: null));
        Assert.Contains(SimilarityFactor.SAME_PROVINCE, sameProvince.Factors);
        Assert.DoesNotContain(SimilarityFactor.SAME_REGION, sameProvince.Factors);

        var sameRegion = HeritageSimilarity.Compare(H(1, province: 1, region: "Bắc"), H(2, "Khác", type: null, province: 2, region: "Bắc", level: null));
        Assert.Contains(SimilarityFactor.SAME_REGION, sameRegion.Factors);
        Assert.DoesNotContain(SimilarityFactor.SAME_PROVINCE, sameRegion.Factors);
        Assert.True(sameProvince.Score > sameRegion.Score);
    }

    [Fact]
    public void The_same_type_weighs_more_than_the_same_region_and_an_unrated_level_is_never_a_match()
    {
        var typeOnly = HeritageSimilarity.Compare(
            H(1, type: HeritageType.NATURAL, province: 1, region: "Bắc", level: RecognitionLevel.NONE),
            H(2, "Khác", HeritageType.NATURAL, 40, "Nam", RecognitionLevel.NONE));
        Assert.Equal([SimilarityFactor.SAME_TYPE], typeOnly.Factors);
        Assert.Equal(HeritageSimilarity.TypeWeight, typeOnly.Score);
    }

    [Theory]
    [InlineData(1010, 1225, 1200, 1400, true)]     // giao nhau
    [InlineData(1010, 1225, 1225, 1300, true)]     // chạm đầu mút
    [InlineData(1010, 1225, 1300, 1400, false)]
    [InlineData(1010, null, 1010, null, true)]     // chỉ có năm bắt đầu
    [InlineData(null, null, 1010, 1100, false)]    // không có khoảng năm
    public void The_eras_overlap_when_the_year_ranges_intersect(int? from1, int? to1, int? from2, int? to2, bool expected) =>
        Assert.Equal(expected, HeritageSimilarity.EraOverlaps(H(1, from: from1, to: to1), H(2, from: from2, to: to2)));

    [Fact]
    public void Similar_text_ignores_accents_case_short_words_and_common_connectors()
    {
        var high = HeritageSimilarity.TextSimilarity("Chùa Một Cột Hà Nội", "chua mot cot ha noi");
        Assert.Equal(1d, high);
        var partial = HeritageSimilarity.TextSimilarity("Thành nhà Hồ Thanh Hóa", "Thành cổ Thanh Hóa xưa");
        Assert.InRange(partial, 0.2, 0.9);
        Assert.Equal(0d, HeritageSimilarity.TextSimilarity("Ca trù", "Hoàng thành"));
        Assert.Equal(0d, HeritageSimilarity.TextSimilarity(null, "Hoàng thành"));
        Assert.Equal(0d, HeritageSimilarity.TextSimilarity("của và các", "của và các"));   // chỉ toàn từ nối
    }

    [Fact]
    public void The_best_match_takes_the_highest_weighted_reference_and_names_it()
    {
        var candidate = H(10, "Văn Miếu", HeritageType.TANGIBLE, 1, "Bắc", RecognitionLevel.NATIONAL);
        var weakRef = H(1, "Chùa Hương", HeritageType.TANGIBLE, 1, "Bắc", RecognitionLevel.NATIONAL);
        var otherRef = H(2, "Ca trù", HeritageType.INTANGIBLE, 30, "Nam", RecognitionLevel.PROVINCIAL);

        var best = HeritageSimilarity.Best(candidate, [(otherRef, 5d), (weakRef, 1d)]);
        Assert.NotNull(best);
        Assert.Equal(1, best.ReferenceId);

        var weighted = HeritageSimilarity.Best(candidate, [(weakRef, 2d)]);
        Assert.Equal(best.Score * 2, weighted!.Score);
        Assert.Null(HeritageSimilarity.Best(candidate, [(otherRef, 3d)]));
        Assert.Null(HeritageSimilarity.Best(candidate, []));
    }

    [Fact]
    public void The_fallback_only_looks_at_the_same_province_or_the_same_type()
    {
        var candidate = H(10, "Văn Miếu", HeritageType.TANGIBLE, 1, "Bắc", RecognitionLevel.UNESCO, text: "văn miếu quốc tử giám");
        var sameTextOnly = H(1, "Văn Miếu Mao Điền", HeritageType.INTANGIBLE, 50, "Nam", RecognitionLevel.PROVINCIAL, text: "văn miếu quốc tử giám");
        Assert.Null(HeritageSimilarity.BestSameProvinceOrType(candidate, [(sameTextOnly, 1d)]));

        var sameType = H(2, "Chùa", HeritageType.TANGIBLE, 50, "Nam");
        var sameProvince = H(3, "Đền", HeritageType.INTANGIBLE, 1, "Bắc");
        Assert.Equal(1d, HeritageSimilarity.BestSameProvinceOrType(candidate, [(sameType, 1d)])!.Score);
        Assert.Equal(HeritageSimilarity.ProvinceWeight, HeritageSimilarity.BestSameProvinceOrType(candidate, [(sameProvince, 1d)])!.Score);
    }

    [Fact]
    public void The_reason_lists_at_most_two_common_points_and_names_the_reference()
    {
        var match = new SimilarityMatch(5, [SimilarityFactor.SAME_TYPE, SimilarityFactor.SAME_PROVINCE, SimilarityFactor.SAME_LEVEL], 7, "Hoàng thành");
        Assert.Equal("Cùng loại, cùng tỉnh với Hoàng thành", HeritageSimilarity.Reason(match));
        Assert.Equal("Cùng cấp xếp hạng", HeritageSimilarity.Reason(new SimilarityMatch(1, [SimilarityFactor.SAME_LEVEL], 7, null)));
    }
}
