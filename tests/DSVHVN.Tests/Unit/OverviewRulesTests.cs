using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của tổng quan: cảnh báo gói sắp hết hạn, phần trăm điểm, khoảng thời gian và cách gom nhóm của biểu đồ, ngưỡng câu hỏi cần xem lại.</summary>
public sealed class OverviewRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 7);

    [Theory]
    [InlineData(0, 1)]       // hết hôm nay: còn đúng hôm nay
    [InlineData(1, 2)]
    [InlineData(13, 14)]
    [InlineData(30, 31)]
    [InlineData(-1, 0)]      // đã hết hạn hôm qua
    [InlineData(-40, 0)]
    public void Days_left_counts_today_and_the_end_date_and_never_goes_below_zero(int endOffset, int expected) =>
        Assert.Equal(expected, OverviewRules.DaysLeft(Today.AddDays(endOffset), Today));

    [Theory]
    [InlineData(0, true)]
    [InlineData(13, true)]    // còn 14 ngày: đúng ngưỡng
    [InlineData(14, false)]   // còn 15 ngày
    [InlineData(60, false)]
    [InlineData(-1, false)]   // hết hạn rồi không phải "sắp" hết hạn
    public void A_plan_is_expiring_soon_when_at_most_fourteen_days_remain(int endOffset, bool expected) =>
        Assert.Equal(expected, OverviewRules.IsExpiringSoon(Today.AddDays(endOffset), Today));

    [Theory]
    [InlineData("8", "10", "80")]
    [InlineData("7.5", "10", "75")]
    [InlineData("1", "3", "33.33")]
    [InlineData("2", "3", "66.67")]
    [InlineData("0", "10", "0")]
    public void The_percent_of_an_attempt_is_score_over_total_rounded_to_two_places(string score, string total, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            OverviewRules.PercentOf(decimal.Parse(score, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void The_percent_cannot_be_computed_without_a_score_or_a_positive_total()
    {
        Assert.Null(OverviewRules.PercentOf(null, 10m));
        Assert.Null(OverviewRules.PercentOf(5m, null));
        Assert.Null(OverviewRules.PercentOf(5m, 0m));
    }

    [Fact]
    public void The_average_percent_is_the_mean_of_the_attempts_and_null_when_there_are_none()
    {
        Assert.Null(OverviewRules.AveragePercent([]));
        Assert.Equal(66.67m, OverviewRules.AveragePercent([100m, 100m, 0m]));
        Assert.Equal(50m, OverviewRules.AveragePercent([100m, 0m]));
        Assert.Equal(33.33m, OverviewRules.AveragePercent([100m, 0m, 0m]));
    }

    [Theory]
    [InlineData(1, TrendGranularity.DAY)]
    [InlineData(30, TrendGranularity.DAY)]
    [InlineData(92, TrendGranularity.DAY)]
    [InlineData(93, TrendGranularity.MONTH)]
    [InlineData(366, TrendGranularity.MONTH)]
    public void The_trend_is_grouped_by_day_up_to_92_days_and_by_month_beyond(int days, TrendGranularity expected) =>
        Assert.Equal(expected, OverviewRules.GranularityFor(Today, Today.AddDays(days - 1)));

    [Fact]
    public void Daily_periods_cover_every_day_including_both_ends()
    {
        var keys = OverviewRules.Periods(new DateOnly(2026, 2, 27), new DateOnly(2026, 3, 2), TrendGranularity.DAY);
        Assert.Equal(["2026-02-27", "2026-02-28", "2026-03-01", "2026-03-02"], keys);
    }

    [Fact]
    public void Monthly_periods_start_from_the_month_of_the_first_day_and_cross_year_ends()
    {
        var keys = OverviewRules.Periods(new DateOnly(2026, 11, 20), new DateOnly(2027, 2, 3), TrendGranularity.MONTH);
        Assert.Equal(["2026-11", "2026-12", "2027-01", "2027-02"], keys);
        Assert.Equal("2027-01", OverviewRules.PeriodKey(new DateOnly(2027, 1, 31), TrendGranularity.MONTH));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(9, 0, false)]     // chưa đủ 10 lượt: không đánh giá
    [InlineData(10, 0, true)]     // đủ 10 lượt, không ai đúng
    [InlineData(10, 2, true)]     // 20%
    [InlineData(10, 3, false)]    // đúng 30%: không dưới ngưỡng
    [InlineData(100, 29, true)]
    [InlineData(100, 30, false)]
    [InlineData(11, 3, true)]     // 27,3%
    public void A_question_needs_review_with_ten_answers_and_a_correct_rate_below_thirty_percent(int answered, int correct, bool expected) =>
        Assert.Equal(expected, QuestionQualityRules.IsLowQuality(answered, correct));

    [Fact]
    public void The_correct_rate_is_a_percent_with_one_decimal_and_null_without_answers()
    {
        Assert.Null(QuestionQualityRules.CorrectRatePercent(0, 0));
        Assert.Equal(27.3m, QuestionQualityRules.CorrectRatePercent(11, 3));
        Assert.Equal(100m, QuestionQualityRules.CorrectRatePercent(4, 4));
        Assert.Equal((10, 30), (QuestionQualityRules.MinAnswers, QuestionQualityRules.LowCorrectPercent));
    }
}
