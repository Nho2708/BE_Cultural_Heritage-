using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Quy tắc của khách tham quan: mã thiết bị là UUID phiên bản 4, hệ số các loại lượt, khoảng cách haversine, làm tròn tọa độ, khung Việt Nam nới 1 độ,
/// chữ khoảng cách ("350 m", "1,2 km") và điểm dự phòng khi Dịch vụ AI lỗi.
/// </summary>
public sealed class GuestRulesTests
{
    [Theory]
    [InlineData("3f2b8c1e-9a47-4d6b-8e10-5c7a9b2d4f61")]
    [InlineData("3F2B8C1E-9A47-4D6B-9E10-5C7A9B2D4F61")]
    [InlineData("  3f2b8c1e-9a47-4d6b-ae10-5c7a9b2d4f61  ")]
    [InlineData("00000000-0000-4000-8000-000000000000")]
    public void A_well_formed_version_4_uuid_is_accepted_and_lowercased(string raw) =>
        Assert.Equal(raw.Trim().ToLowerInvariant(), GuestRules.NormalizeDeviceKey(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uuid")]
    [InlineData("3f2b8c1e9a474d6b8e105c7a9b2d4f61")]                      // không có dấu gạch
    [InlineData("{3f2b8c1e-9a47-4d6b-8e10-5c7a9b2d4f61}")]                // có ngoặc
    [InlineData("3f2b8c1e-9a47-1d6b-8e10-5c7a9b2d4f61")]                  // phiên bản 1
    [InlineData("3f2b8c1e-9a47-4d6b-7e10-5c7a9b2d4f61")]                  // sai biến thể
    [InlineData("3f2b8c1e-9a47-4d6b-8e10-5c7a9b2d4f6")]                   // thiếu một ký tự
    [InlineData("3f2b8c1e-9a47-4d6b-8e10-5c7a9b2d4f611")]                 // thừa một ký tự
    [InlineData("3f2b8c1e-9a47-4d6b-8e10-5c7a9b2d4f6g")]                  // không phải chữ số thập lục phân
    [InlineData("3f2b8c1e-9a47-4d6b-8e10-5c7a9b2d4f61 -- drop table")]
    public void Anything_else_is_refused(string? raw) => Assert.Null(GuestRules.NormalizeDeviceKey(raw));

    [Fact]
    public void Keys_issued_by_the_server_are_valid_and_never_repeat()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => GuestRules.NewDeviceKey()).ToList();
        Assert.All(keys, key => Assert.Equal(key, GuestRules.NormalizeDeviceKey(key)));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Deep_activities_weigh_double_and_a_search_has_no_weight()
    {
        Assert.Equal(1d, GuestRules.Weight(GuestActivityType.VIEW));
        Assert.All([GuestActivityType.READ_SUMMARY, GuestActivityType.LISTEN, GuestActivityType.VIEW_3D], t => Assert.Equal(2d, GuestRules.Weight(t)));
        Assert.Equal(0d, GuestRules.Weight(GuestActivityType.SEARCH));
    }

    [Fact]
    public void The_fixed_numbers_match_the_business_rules()
    {
        Assert.Equal(90, GuestRules.RetentionDays);
        Assert.Equal(500, GuestRules.MaxActivitiesPerDevice);
        Assert.Equal(TimeSpan.FromMinutes(10), GuestRules.RepeatWindow);
        Assert.Equal(20, GuestRules.InterestWindow);
        Assert.Equal(6, GuestRules.ResultLimit);
        Assert.Equal(3, GuestRules.ExcludeAfterViews);
        Assert.Equal((2, 100), (GuestRules.SearchQueryMinLength, GuestRules.SearchQueryMaxLength));
        Assert.Equal(new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc), GuestRules.RetentionCutoff(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Query_text_loses_surplus_white_space_and_empty_becomes_null()
    {
        Assert.Equal("hoàng thành Thăng Long", GuestRules.NormalizeQuery("  hoàng   thành\tThăng Long \n"));
        Assert.Null(GuestRules.NormalizeQuery("   "));
        Assert.Null(GuestRules.NormalizeQuery(null));
    }

    [Fact]
    public void Distance_is_the_great_circle_distance_in_meters()
    {
        Assert.Equal(0d, GuestRules.DistanceMeters(21.0285, 105.8542, 21.0285, 105.8542), 3);

        // Hà Nội - Thành phố Hồ Chí Minh: khoảng 1.137 km đường chim bay.
        var hanoiToSaigon = GuestRules.DistanceMeters(21.0285, 105.8542, 10.8231, 106.6297);
        Assert.InRange(hanoiToSaigon, 1_130_000, 1_145_000);
        Assert.Equal(hanoiToSaigon, GuestRules.DistanceMeters(10.8231, 106.6297, 21.0285, 105.8542), 3);

        // 0,001 độ vĩ độ gần 111 m.
        Assert.InRange(GuestRules.DistanceMeters(16.0, 108.0, 16.001, 108.0), 110d, 112d);
    }

    [Theory]
    [InlineData(21.03333449, 21.033)]
    [InlineData(21.0336, 21.034)]
    [InlineData(-21.0336, -21.034)]
    [InlineData(105.85, 105.85)]
    public void Coordinates_are_rounded_to_three_decimals(double raw, double expected) => Assert.Equal(expected, GuestRules.RoundCoordinate(raw));

    [Theory]
    [InlineData(21.03, 105.85, true)]      // Hà Nội
    [InlineData(8.0, 102.0, true)]         // góc khung của quy tắc dữ liệu
    [InlineData(23.5, 117.5, true)]
    [InlineData(7.0, 101.0, true)]         // nới 1 độ mỗi phía
    [InlineData(24.5, 118.5, true)]
    [InlineData(6.999, 105.0, false)]
    [InlineData(24.501, 105.0, false)]
    [InlineData(15.0, 100.999, false)]
    [InlineData(15.0, 118.501, false)]
    [InlineData(35.68, 139.69, false)]     // Tokyo
    [InlineData(48.85, 2.35, false)]       // Paris
    public void Vietnam_area_is_the_data_box_widened_by_one_degree(double latitude, double longitude, bool inside) =>
        Assert.Equal(inside, GuestRules.IsNearVietnam(latitude, longitude));

    [Theory]
    [InlineData(0d, 0d, true)]
    [InlineData(90d, 180d, true)]
    [InlineData(-90d, -180d, true)]
    [InlineData(90.0001, 0d, false)]
    [InlineData(0d, -180.0001, false)]
    [InlineData(double.NaN, 0d, false)]
    [InlineData(0d, double.PositiveInfinity, false)]
    public void Coordinates_must_be_real_geographic_positions(double latitude, double longitude, bool valid) =>
        Assert.Equal(valid, GuestRules.IsValidCoordinate(latitude, longitude));

    [Theory]
    [InlineData(0d, LanguageCode.vi, "0 m")]
    [InlineData(349.6, LanguageCode.vi, "350 m")]
    [InlineData(999.4, LanguageCode.en, "999 m")]
    [InlineData(999.6, LanguageCode.vi, "1,0 km")]
    [InlineData(1234d, LanguageCode.vi, "1,2 km")]
    [InlineData(1234d, LanguageCode.en, "1.2 km")]
    [InlineData(1234d, LanguageCode.ko, "1.2 km")]
    [InlineData(15049d, LanguageCode.ja, "15.0 km")]
    [InlineData(15051d, LanguageCode.zh, "15.1 km")]
    public void Distance_text_is_in_meters_below_one_kilometer_else_kilometers_with_one_digit(double meters, LanguageCode language, string expected) =>
        Assert.Equal(expected, GuestRules.FormatDistance(meters, language));

    [Fact]
    public void Fallback_score_is_two_for_the_same_type_and_one_for_the_same_region()
    {
        HeritageFeatures F(long id, HeritageType? type, string? region) => new(id, "x", type, null, region, null, null, null, null);
        var reference = F(1, HeritageType.TANGIBLE, "Bắc");

        Assert.Equal(3d, GuestRules.FallbackScore(F(2, HeritageType.TANGIBLE, "Bắc"), reference));
        Assert.Equal(2d, GuestRules.FallbackScore(F(3, HeritageType.TANGIBLE, "Nam"), reference));
        Assert.Equal(1d, GuestRules.FallbackScore(F(4, HeritageType.NATURAL, "Bắc"), reference));
        Assert.Equal(0d, GuestRules.FallbackScore(F(5, HeritageType.NATURAL, "Nam"), reference));
        Assert.Equal(0d, GuestRules.FallbackScore(F(6, null, null), F(7, null, null)));
    }
}
