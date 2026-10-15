using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của việc làm bài: hạn nộp, ân hạn, đồng hồ theo cấp, phần trăm, số sao, huy hiệu và mức thành thạo.</summary>
public sealed class AttemptRulesTests
{
    private static readonly DateTime Start = new(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);

    // ---- Hạn nộp ----

    [Fact]
    public void The_deadline_is_the_start_plus_the_time_limit_but_never_after_the_closing_time()
    {
        Assert.Equal(Start.AddMinutes(30), AttemptRules.DeadlineOf(Start, 30, null));
        Assert.Equal(Start.AddMinutes(30), AttemptRules.DeadlineOf(Start, 30, Start.AddHours(2)));
        Assert.Equal(Start.AddMinutes(10), AttemptRules.DeadlineOf(Start, 30, Start.AddMinutes(10)));
        Assert.Equal(Start.AddHours(2), AttemptRules.DeadlineOf(Start, null, Start.AddHours(2)));   // không giới hạn thời gian: hạn nộp là giờ đóng
        Assert.Null(AttemptRules.DeadlineOf(Start, null, null));
    }

    [Fact]
    public void A_level_one_class_has_no_time_limit_and_the_other_levels_keep_the_quiz_value()
    {
        Assert.Null(AttemptRules.EffectiveTimeLimit(1, 20));
        Assert.Equal(20, AttemptRules.EffectiveTimeLimit(2, 20));
        Assert.Equal(20, AttemptRules.EffectiveTimeLimit(3, 20));
        Assert.Null(AttemptRules.EffectiveTimeLimit(2, null));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(30, false)]    // đúng 30 giây sau hạn nộp: còn nhận
    [InlineData(31, true)]
    [InlineData(600, true)]
    public void The_server_accepts_work_until_the_deadline_plus_thirty_seconds(int secondsAfterDeadline, bool overdue)
    {
        var deadline = Start.AddMinutes(30);
        Assert.Equal(overdue, AttemptRules.IsOverdue(deadline, deadline.AddSeconds(secondsAfterDeadline)));
        Assert.Equal(deadline.AddSeconds(30), AttemptRules.AcceptUntil(deadline));
    }

    [Fact]
    public void An_attempt_without_a_deadline_is_never_overdue()
    {
        Assert.False(AttemptRules.IsOverdue(null, DateTime.MaxValue.AddDays(-1)));
        Assert.Null(AttemptRules.AcceptUntil(null));
    }

    [Theory]
    [InlineData(1, null, false)]
    [InlineData(1, 30, false)]
    [InlineData(2, null, false)]
    [InlineData(2, 30, true)]
    [InlineData(3, null, true)]
    [InlineData(3, 30, true)]
    public void The_clock_shows_never_at_level_one_when_the_quiz_has_a_limit_at_level_two_and_always_at_level_three(int level, int? limit, bool shows)
    {
        var deadline = Start.AddHours(1);
        Assert.Equal(shows, AttemptRules.ShowsClock(level, level == 1 ? null : limit, deadline));
        Assert.False(AttemptRules.ShowsClock(level, level == 1 ? null : limit, null));
    }

    [Theory]
    [InlineData(600, 30, 600)]
    [InlineData(2400, 30, 1800)]    // không vượt thời gian làm
    [InlineData(2400, null, 2400)]
    [InlineData(-5, 30, 0)]
    public void The_duration_is_capped_at_the_time_limit_and_never_negative(int seconds, int? limit, int expected) =>
        Assert.Equal(expected, AttemptRules.DurationSeconds(Start, Start.AddSeconds(seconds), limit));

    // ---- Điểm, sao, lời khen ----

    [Theory]
    [InlineData(8, 8, 100.0)]
    [InlineData(6, 8, 75.0)]
    [InlineData(5, 9, 55.6)]
    [InlineData(1, 3, 33.3)]
    [InlineData(0, 8, 0.0)]
    [InlineData(0, 0, 0.0)]
    public void The_percent_is_rounded_to_one_decimal_place(double score, double total, double expected) =>
        Assert.Equal((decimal)expected, AttemptRules.Percent((decimal)score, (decimal)total));

    [Theory]
    [InlineData(10, 10, 3)]
    [InlineData(8, 10, 3)]      // đúng 80%
    [InlineData(7.9, 10, 2)]
    [InlineData(5, 10, 2)]      // đúng 50%
    [InlineData(4.9, 10, 1)]
    [InlineData(0, 10, 1)]      // nộp bài luôn có ít nhất một sao
    [InlineData(0, 0, 1)]
    public void Stars_come_from_the_correct_share_with_eighty_and_fifty_percent_thresholds(double score, double total, int stars) =>
        Assert.Equal(stars, AttemptRules.Stars((decimal)score, (decimal)total));

    [Fact]
    public void Every_praise_is_positive_and_different_for_each_star_count()
    {
        var praises = new[] { 1, 2, 3 }.Select(AttemptRules.Praise).ToList();
        Assert.Equal(3, praises.Distinct().Count());
        Assert.All(praises, p => Assert.DoesNotMatch("sai|kém|tệ|yếu", p));
    }

    // ---- Huy hiệu ----

    [Theory]
    [InlineData(AttemptStatus.SUBMITTED, 10, 10, new[] { BadgeCode.PERFECT_SCORE, BadgeCode.ON_TIME })]
    [InlineData(AttemptStatus.SUBMITTED, 9, 10, new[] { BadgeCode.EXCELLENT, BadgeCode.ON_TIME })]
    [InlineData(AttemptStatus.SUBMITTED, 8.9, 10, new[] { BadgeCode.ON_TIME })]
    [InlineData(AttemptStatus.EXPIRED, 10, 10, new[] { BadgeCode.PERFECT_SCORE })]
    [InlineData(AttemptStatus.EXPIRED, 9.5, 10, new[] { BadgeCode.EXCELLENT })]
    [InlineData(AttemptStatus.EXPIRED, 1, 10, new BadgeCode[0])]
    [InlineData(AttemptStatus.IN_PROGRESS, 10, 10, new BadgeCode[0])]
    [InlineData(AttemptStatus.SUBMITTED, 0, 0, new[] { BadgeCode.ON_TIME })]   // bài không có điểm không được Điểm tuyệt đối
    public void Badges_of_an_attempt_follow_the_score_and_how_it_was_submitted(AttemptStatus status, double score, double total, BadgeCode[] expected) =>
        Assert.Equal(expected, AttemptRules.AttemptBadges(status, (decimal)score, (decimal)total));

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(30, true)]
    public void Perseverance_needs_five_finished_attempts(int finished, bool earned) =>
        Assert.Equal(earned, AttemptRules.HasPerseverance(finished));

    [Fact]
    public void The_four_badges_have_names_and_descriptions()
    {
        Assert.Equal(["Điểm tuyệt đối", "Xuất sắc", "Nộp đúng hạn", "Kiên trì"], Enum.GetValues<BadgeCode>().Select(AttemptRules.BadgeName));
        Assert.All(Enum.GetValues<BadgeCode>(), b => Assert.False(string.IsNullOrWhiteSpace(AttemptRules.BadgeDescription(b))));
    }

    // ---- Mức thành thạo ----

    [Theory]
    [InlineData(0, 0, MasteryLevel.INSUFFICIENT_DATA, 0)]
    [InlineData(2, 2, MasteryLevel.INSUFFICIENT_DATA, 0)]    // dưới 3 câu
    [InlineData(3, 3, MasteryLevel.MASTERED, 3)]
    [InlineData(4, 5, MasteryLevel.MASTERED, 3)]             // đúng 80%
    [InlineData(7, 10, MasteryLevel.FAIR, 2)]
    [InlineData(1, 2, MasteryLevel.INSUFFICIENT_DATA, 0)]
    [InlineData(2, 4, MasteryLevel.FAIR, 2)]                 // đúng 50%
    [InlineData(1, 4, MasteryLevel.NEEDS_REVIEW, 1)]
    [InlineData(0, 3, MasteryLevel.NEEDS_REVIEW, 1)]
    public void Mastery_levels_follow_the_eighty_and_fifty_percent_thresholds_and_need_three_answers(int correct, int answered, MasteryLevel level, int stars)
    {
        Assert.Equal(level, AttemptRules.MasteryOf(correct, answered));
        Assert.Equal(stars, AttemptRules.MasteryStars(level));
    }

    [Fact]
    public void Mastery_labels_and_rate_percent()
    {
        Assert.Equal(["Chưa đủ dữ liệu", "Cần ôn", "Đạt", "Thành thạo"], Enum.GetValues<MasteryLevel>().Select(AttemptRules.MasteryLabel));
        Assert.Equal(66.7m, AttemptRules.RatePercent(2, 3));
        Assert.Equal(0m, AttemptRules.RatePercent(0, 0));
    }

    [Fact]
    public void Status_labels()
    {
        Assert.Equal(["Đang làm", "Đã nộp", "Tự nộp khi hết giờ"], Enum.GetValues<AttemptStatus>().Select(s => AttemptRules.StatusLabel(s)));
    }

    [Fact]
    public void Streak_days_and_level_one_cheer()
    {
        var today = new DateTime(2026, 10, 16);
        Assert.Equal(0, AttemptRules.StreakDays([], today));

        // Submitted today and yesterday -> streak 2
        Assert.Equal(2, AttemptRules.StreakDays([today, today.AddDays(-1), today.AddDays(-5)], today));

        // Submitted yesterday but not today -> streak 1
        Assert.Equal(1, AttemptRules.StreakDays([today.AddDays(-1)], today));

        // Submitted 2 days ago but neither today nor yesterday -> streak 0
        Assert.Equal(0, AttemptRules.StreakDays([today.AddDays(-2)], today));

        // Level 1 cheers
        Assert.Contains("làm dở", AttemptRules.LevelOneCheer(10, 2, hasInProgress: true, hasOpenQuizzes: true));
        Assert.Contains("ngôi sao", AttemptRules.LevelOneCheer(15, 2, hasInProgress: false, hasOpenQuizzes: true));
        Assert.Contains("thử thách", AttemptRules.LevelOneCheer(0, 0, hasInProgress: false, hasOpenQuizzes: true));
        Assert.Contains("mã bài học", AttemptRules.LevelOneCheer(0, 0, hasInProgress: false, hasOpenQuizzes: false));
    }
}
