using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của gợi ý: mức khó mục tiêu của em, độ lệch độ khó, ngưỡng "nên ôn" của lớp và câu lý do (cấp 1 không có số liệu).</summary>
public sealed class RecommendationRulesTests
{
    [Theory]
    [InlineData(2, 8, 10, DifficultyLevel.HARD)]      // 80% trở lên hợp Khó
    [InlineData(2, 4, 5, DifficultyLevel.HARD)]
    [InlineData(2, 7, 10, DifficultyLevel.MEDIUM)]    // 50% tới dưới 80% hợp Trung bình
    [InlineData(2, 5, 10, DifficultyLevel.MEDIUM)]
    [InlineData(3, 4, 10, DifficultyLevel.EASY)]      // dưới 50% hợp Dễ
    [InlineData(3, 0, 6, DifficultyLevel.EASY)]
    public void The_target_difficulty_follows_the_overall_mastery(int level, int correct, int answered, DifficultyLevel expected) =>
        Assert.Equal(expected, RecommendationRules.TargetDifficulty(level, correct, answered));

    [Theory]
    [InlineData(1, 0, 0, DifficultyLevel.EASY)]       // chưa có kết quả: theo cấp học
    [InlineData(2, 0, 0, DifficultyLevel.MEDIUM)]
    [InlineData(3, 0, 0, DifficultyLevel.MEDIUM)]
    [InlineData(3, 2, 2, DifficultyLevel.MEDIUM)]     // dưới 3 câu vẫn là chưa đủ kết quả dù đúng hết
    [InlineData(1, 2, 2, DifficultyLevel.EASY)]
    public void Without_enough_results_the_target_difficulty_follows_the_school_level(int level, int correct, int answered, DifficultyLevel expected) =>
        Assert.Equal(expected, RecommendationRules.TargetDifficulty(level, correct, answered));

    [Fact]
    public void A_quiz_closest_to_the_target_difficulty_has_the_smallest_distance_and_an_unset_difficulty_counts_as_one()
    {
        Assert.Equal(0, RecommendationRules.DifficultyDistance(DifficultyLevel.MEDIUM, DifficultyLevel.MEDIUM));
        Assert.Equal(1, RecommendationRules.DifficultyDistance(DifficultyLevel.EASY, DifficultyLevel.MEDIUM));
        Assert.Equal(2, RecommendationRules.DifficultyDistance(DifficultyLevel.EASY, DifficultyLevel.HARD));
        Assert.Equal(1, RecommendationRules.DifficultyDistance(null, DifficultyLevel.HARD));
    }

    [Theory]
    [InlineData(4, 10, true)]      // đúng 40% và đúng 10 câu: nên ôn
    [InlineData(5, 12, true)]
    [InlineData(3, 10, false)]     // 30%
    [InlineData(9, 9, false)]      // sai hết nhưng chưa đủ 10 câu
    [InlineData(10, 10, true)]
    [InlineData(0, 0, false)]
    public void A_heritage_is_for_the_class_to_review_from_forty_percent_wrong_and_at_least_ten_answers(int wrong, int answered, bool expected) =>
        Assert.Equal(expected, RecommendationRules.ClassShouldReview(wrong, answered));

    [Theory]
    [InlineData(4, 5, true)]       // 80% sai trên năm câu của một bài
    [InlineData(1, 1, true)]
    [InlineData(1, 3, false)]      // 33%
    [InlineData(0, 0, false)]
    public void A_single_quiz_has_no_minimum_number_of_answers(int wrong, int answered, bool expected) =>
        Assert.Equal(expected, RecommendationRules.ClassShouldReview(wrong, answered, singleQuiz: true));

    [Fact]
    public void The_class_reason_names_the_wrong_percent_and_the_number_of_answers()
    {
        Assert.Equal("Lớp sai 42% ở 12 câu về di sản này", RecommendationRules.ClassReviewReason(5, 12));
        Assert.Equal("Lớp sai 50% ở 10 câu về di sản này", RecommendationRules.ClassReviewReason(5, 10));
    }

    [Fact]
    public void Reasons_for_a_level_one_pupil_carry_no_numbers_or_percentages()
    {
        var reasons = new[]
        {
            RecommendationRules.StudentReviewReason(1, 4),
            RecommendationRules.WeakQuizReason(1, 3),
            RecommendationRules.ReviewLessonReason(1, 33.3m),
        };
        Assert.All(reasons, r =>
        {
            Assert.DoesNotContain(r, c => char.IsDigit(c));
            Assert.DoesNotContain("%", r);
        });
        Assert.Contains("4", RecommendationRules.StudentReviewReason(2, 4));
        Assert.Contains("33%", RecommendationRules.ReviewLessonReason(3, 33.3m));
    }

    [Fact]
    public void The_closing_soon_reason_counts_whole_days()
    {
        var now = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Sắp đóng", RecommendationRules.ClosingSoonReason(now.AddHours(5), now));
        Assert.Equal("Còn 3 ngày", RecommendationRules.ClosingSoonReason(now.AddDays(3), now));
        Assert.Equal("Còn 3 ngày", RecommendationRules.ClosingSoonReason(now.AddDays(2).AddHours(3), now));
    }

    [Fact]
    public void Each_student_section_has_at_most_three_items_and_each_class_section_at_most_five()
    {
        Assert.Equal(3, RecommendationRules.StudentSectionLimit);
        Assert.Equal(5, RecommendationRules.ClassSectionLimit);
    }
}
