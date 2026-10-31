using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của bài kiểm tra: điểm, thời gian làm theo cấp, câu nào dùng được ở cấp nào, giai đoạn mở đóng, độ khó của bài học, giờ Việt Nam.</summary>
public sealed class QuizRulesTests
{
    // ---- Điểm ----

    [Theory]
    [InlineData("0.25", true)]
    [InlineData("0.5", true)]
    [InlineData("1", true)]
    [InlineData("1.75", true)]
    [InlineData("99.75", true)]
    [InlineData("100", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("0.1", false)]
    [InlineData("0.3", false)]
    [InlineData("1.255", false)]
    [InlineData("100.25", false)]
    [InlineData("101", false)]
    public void A_point_is_above_zero_up_to_one_hundred_in_steps_of_a_quarter(string point, bool valid) =>
        Assert.Equal(valid, QuizRules.IsValidPoint(decimal.Parse(point, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void Only_primary_school_quizzes_skip_the_open_and_close_times(int level, bool required) =>
        Assert.Equal(required, QuizRules.RequiresOpenWindow(level));

    [Fact]
    public void A_quiz_code_starts_with_KT_and_never_passes_as_a_lesson_code()
    {
        var code = QuizCodes.NewCode();

        Assert.True(QuizCodes.IsWellFormed(code));
        Assert.StartsWith("KT", code);
        Assert.False(LessonCodes.IsWellFormed(code));
        Assert.False(QuizCodes.IsWellFormed(LessonCodes.NewCode()));
    }

    [Fact]
    public void The_default_point_and_the_limits_follow_the_rules()
    {
        Assert.Equal((1m, 100m, 0.25m), (QuizRules.DefaultPoint, QuizRules.MaxPoint, QuizRules.PointStep));
        Assert.Equal((1, 50), (QuizRules.MinQuestions, QuizRules.MaxQuestions));
        Assert.Equal(5000m, QuizRules.MaxQuestions * QuizRules.MaxPoint);   // vừa decimal(6,2): tối đa 9999,99
        Assert.True(QuizRules.MaxQuestions * QuizRules.MaxPoint < 9999.99m);
    }

    // ---- Thời gian làm bài theo cấp ----

    [Theory]
    [InlineData(1, TimeLimitRule.NOT_ALLOWED)]
    [InlineData(2, TimeLimitRule.OPTIONAL)]
    [InlineData(3, TimeLimitRule.REQUIRED)]
    public void The_time_limit_rule_follows_the_level(int level, TimeLimitRule rule) => Assert.Equal(rule, QuizRules.TimeLimitRuleOf(level));

    [Theory]
    // cấp 1: chỉ để trống
    [InlineData(1, null, false, null)]
    [InlineData(1, null, true, null)]
    [InlineData(1, 15, false, "lớp cấp 1 không giới hạn thời gian làm bài, hãy để trống")]
    // cấp 2: để trống hoặc 1-180
    [InlineData(2, null, true, null)]
    [InlineData(2, 1, true, null)]
    [InlineData(2, 180, true, null)]
    [InlineData(2, 0, true, "thời gian làm bài từ 1 đến 180 phút")]
    [InlineData(2, 181, false, "thời gian làm bài từ 1 đến 180 phút")]
    // cấp 3: đang soạn được để trống, phát hành thì bắt buộc
    [InlineData(3, null, false, null)]
    [InlineData(3, null, true, "lớp cấp 3 phải đặt thời gian làm bài")]
    [InlineData(3, 45, true, null)]
    [InlineData(3, -1, false, "thời gian làm bài từ 1 đến 180 phút")]
    public void Checking_the_time_limit_follows_the_level_and_whether_it_is_for_publishing(int level, int? minutes, bool forPublish, string? expected) =>
        Assert.Equal(expected, QuizRules.CheckTimeLimit(level, minutes, forPublish));

    [Theory]
    [InlineData(1, 30, null)]
    [InlineData(1, null, null)]
    [InlineData(2, 30, 30)]
    [InlineData(3, 30, 30)]
    [InlineData(3, null, null)]
    public void Duplicating_to_another_level_drops_the_limit_for_level_one_and_keeps_it_otherwise(int level, int? minutes, int? expected) =>
        Assert.Equal(expected, QuizRules.NormalizeTimeLimit(level, minutes));

    // ---- Câu dùng được ở cấp nào ----

    [Fact]
    public void A_question_fits_a_level_only_when_both_its_level_range_and_its_type_allow_it()
    {
        // Mỗi dạng chỉ dùng được ở khoảng cấp của bảng dạng câu theo cấp, kể cả khi khoảng cấp của câu rộng hơn.
        foreach (var info in QuestionTypes.All)
            for (var level = 1; level <= 3; level++)
                Assert.Equal(info.AllowsLevel(level), QuizRules.QuestionFitsLevel(info.Type, 1, 3, level));

        // Khoảng cấp của câu bị thu hẹp thì cấp ngoài khoảng không dùng được dù dạng cho phép.
        Assert.True(QuizRules.QuestionFitsLevel(QuestionType.ORDERING, 2, 2, 2));
        Assert.False(QuizRules.QuestionFitsLevel(QuestionType.ORDERING, 2, 2, 1));
        Assert.False(QuizRules.QuestionFitsLevel(null, 1, 3, 2));
    }

    [Theory]
    [InlineData(QuestionType.IMAGE_CHOICE, 1, true)]
    [InlineData(QuestionType.IMAGE_CHOICE, 2, false)]
    [InlineData(QuestionType.SINGLE_CHOICE, 1, false)]
    [InlineData(QuestionType.SINGLE_CHOICE, 3, true)]
    [InlineData(QuestionType.FILL_BLANK, 2, true)]
    [InlineData(QuestionType.FILL_BLANK, 3, false)]
    [InlineData(QuestionType.MULTI_TRUE_FALSE, 3, true)]
    [InlineData(QuestionType.TRUE_FALSE, 3, false)]
    public void Spot_checks_of_the_type_table_by_level(QuestionType type, int level, bool fits) =>
        Assert.Equal(fits, QuizRules.QuestionFitsLevel(type, 1, 3, level));

    // ---- Giai đoạn mở đóng ----

    private static readonly DateTime Now = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(QuizStatus.DRAFT, -60, 60, QuizOpenState.NONE)]
    [InlineData(QuizStatus.PUBLISHED, 30, 120, QuizOpenState.NOT_OPEN)]
    [InlineData(QuizStatus.PUBLISHED, -30, 120, QuizOpenState.OPEN)]
    [InlineData(QuizStatus.PUBLISHED, 0, 120, QuizOpenState.OPEN)]          // đúng giờ mở là đã mở
    [InlineData(QuizStatus.PUBLISHED, -120, 0, QuizOpenState.ENDED)]        // đúng giờ đóng là đã đóng
    [InlineData(QuizStatus.PUBLISHED, -120, -1, QuizOpenState.ENDED)]
    [InlineData(QuizStatus.CLOSED, -60, 60, QuizOpenState.ENDED)]           // đóng bằng tay dù chưa tới giờ đóng
    public void The_open_state_follows_the_status_and_the_times(QuizStatus status, int openMinutes, int closeMinutes, QuizOpenState expected) =>
        Assert.Equal(expected, QuizRules.OpenStateOf(status, Now.AddMinutes(openMinutes), Now.AddMinutes(closeMinutes), Now));

    [Fact]
    public void Missing_times_mean_no_limit_on_that_side_and_a_missing_status_has_no_state()
    {
        Assert.Equal(QuizOpenState.OPEN, QuizRules.OpenStateOf(QuizStatus.PUBLISHED, null, null, Now));
        Assert.Equal(QuizOpenState.NOT_OPEN, QuizRules.OpenStateOf(QuizStatus.PUBLISHED, Now.AddMinutes(1), null, Now));
        Assert.Equal(QuizOpenState.NONE, QuizRules.OpenStateOf(null, null, null, Now));
    }

    [Fact]
    public void Labels_are_in_vietnamese()
    {
        Assert.Equal(["Nháp", "Đang phát hành", "Đã đóng", ""], new QuizStatus?[] { QuizStatus.DRAFT, QuizStatus.PUBLISHED, QuizStatus.CLOSED, null }.Select(QuizRules.StatusLabel));
        Assert.Equal(["", "Chưa tới giờ mở", "Đang nhận bài", "Đã hết hạn nhận bài"],
            new[] { QuizOpenState.NONE, QuizOpenState.NOT_OPEN, QuizOpenState.OPEN, QuizOpenState.ENDED }.Select(QuizRules.OpenStateLabel));
        Assert.Equal(["Dễ", "Trung bình", "Khó", "Chưa có bài kiểm tra"],
            new DifficultyLevel?[] { DifficultyLevel.EASY, DifficultyLevel.MEDIUM, DifficultyLevel.HARD, null }.Select(LessonDifficulty.LabelOf));
        Assert.Contains("cấp 1", QuizRules.TimeLimitRuleText(1));
        Assert.Contains("để trống", QuizRules.TimeLimitRuleText(2));
        Assert.Contains("bắt buộc", QuizRules.TimeLimitRuleText(3));
    }

    // ---- Độ khó của bài học ----

    private static DifficultyLevel? Dominant(int easy, int medium, int hard) => LessonDifficulty.Dominant(new Dictionary<DifficultyLevel, int>
    {
        [DifficultyLevel.EASY] = easy, [DifficultyLevel.MEDIUM] = medium, [DifficultyLevel.HARD] = hard,
    });

    [Theory]
    [InlineData(3, 1, 0, DifficultyLevel.EASY)]
    [InlineData(1, 3, 1, DifficultyLevel.MEDIUM)]
    [InlineData(0, 1, 4, DifficultyLevel.HARD)]
    [InlineData(2, 2, 0, DifficultyLevel.MEDIUM)]       // hòa lấy mức cao hơn
    [InlineData(0, 2, 2, DifficultyLevel.HARD)]
    [InlineData(2, 0, 2, DifficultyLevel.HARD)]
    [InlineData(3, 3, 3, DifficultyLevel.HARD)]
    [InlineData(1, 0, 0, DifficultyLevel.EASY)]
    public void The_dominant_level_has_the_most_questions_and_a_tie_takes_the_higher_level(int easy, int medium, int hard, DifficultyLevel expected) =>
        Assert.Equal(expected, Dominant(easy, medium, hard));

    [Fact]
    public void No_question_means_no_difficulty()
    {
        Assert.Null(Dominant(0, 0, 0));
        Assert.Null(LessonDifficulty.Dominant(new Dictionary<DifficultyLevel, int>()));
    }

    [Fact]
    public void The_dominant_level_does_not_depend_on_the_order_of_the_counts()
    {
        var a = new Dictionary<DifficultyLevel, int> { [DifficultyLevel.HARD] = 2, [DifficultyLevel.EASY] = 2, [DifficultyLevel.MEDIUM] = 1 };
        var b = new Dictionary<DifficultyLevel, int> { [DifficultyLevel.EASY] = 2, [DifficultyLevel.MEDIUM] = 1, [DifficultyLevel.HARD] = 2 };
        Assert.Equal(LessonDifficulty.Dominant(a), LessonDifficulty.Dominant(b));
        Assert.Equal(DifficultyLevel.HARD, LessonDifficulty.Dominant(a));
    }

    // ---- Giờ ----

    [Fact]
    public void A_time_without_a_zone_is_vietnam_time_and_a_time_with_a_zone_is_converted()
    {
        Assert.Equal(new DateTime(2026, 10, 7, 1, 30, 0, DateTimeKind.Utc), QuizRules.ToUtc(new DateTime(2026, 10, 7, 8, 30, 0, DateTimeKind.Unspecified)));
        Assert.Equal(new DateTime(2026, 10, 7, 8, 30, 0, DateTimeKind.Utc), QuizRules.ToUtc(new DateTime(2026, 10, 7, 8, 30, 0, DateTimeKind.Utc)));
        var local = new DateTime(2026, 10, 7, 8, 30, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime(), QuizRules.ToUtc(local));
        Assert.Equal(DateTimeKind.Utc, QuizRules.ToUtc(new DateTime(2026, 10, 7, 8, 30, 0, DateTimeKind.Unspecified)).Kind);
    }

    [Fact]
    public void Times_are_cut_to_milliseconds_like_the_column_and_shown_in_vietnam_time()
    {
        var precise = new DateTime(2026, 10, 7, 1, 30, 0, DateTimeKind.Utc).AddTicks(1234);
        Assert.Equal(new DateTime(2026, 10, 7, 1, 30, 0, DateTimeKind.Utc), QuizRules.ToUtc(precise));

        Assert.Equal("08:30 07/10/2026", QuizRules.FormatVietnamTime(new DateTime(2026, 10, 7, 1, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("00:05 08/10/2026", QuizRules.FormatVietnamTime(new DateTime(2026, 10, 7, 17, 5, 0, DateTimeKind.Utc)));   // qua nửa đêm giờ Việt Nam
        Assert.Equal("chưa đặt", QuizRules.FormatVietnamTime(null));
    }
}
