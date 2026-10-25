using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của album di sản, câu mang về nhà và chọn câu ôn của bước Nhớ lại ở cấp 1.</summary>
public sealed class AlbumRulesTests
{
    [Theory]
    [InlineData(HeritageType.TANGIBLE, "Di tích và đồ vật xưa")]
    [InlineData(HeritageType.INTANGIBLE, "Lễ hội, nghề và làn điệu")]
    [InlineData(HeritageType.NATURAL, "Cảnh đẹp thiên nhiên")]
    [InlineData(HeritageType.MIXED, "Cảnh đẹp thiên nhiên")]
    [InlineData(HeritageType.DOCUMENTARY, "Sách và tư liệu xưa")]
    [InlineData(null, "Di sản khác")]
    public void The_group_name_follows_the_heritage_type(HeritageType? type, string expected) =>
        Assert.Equal(expected, AlbumRules.GroupName(type));

    [Theory]
    [InlineData(HeritageType.TANGIBLE, "Nhà mình đã từng đi thăm Chùa Một Cột hay một di tích nào gần nhà chưa?")]
    [InlineData(HeritageType.INTANGIBLE, "Ông bà, bố mẹ đã từng xem hay tham gia Chùa Một Cột chưa?")]
    [InlineData(HeritageType.NATURAL, "Bố mẹ đã từng đến Chùa Một Cột chưa? Ở đó có gì đẹp?")]
    [InlineData(HeritageType.MIXED, "Bố mẹ đã từng đến Chùa Một Cột chưa? Ở đó có gì đẹp?")]
    [InlineData(HeritageType.DOCUMENTARY, "Nhà mình có giữ sách, ảnh hay giấy tờ xưa nào không?")]
    [InlineData(null, "Em kể cho bố mẹ nghe về Chùa Một Cột nhé. Bố mẹ biết gì về di sản này?")]
    public void The_take_home_question_follows_the_type_of_the_first_heritage(HeritageType? type, string expected) =>
        Assert.Equal(expected, AlbumRules.TakeHomeQuestion(type, " Chùa Một Cột "));

    [Fact]
    public void Only_true_false_and_image_choice_questions_are_used_for_recall()
    {
        Assert.True(AlbumRules.IsRecallType(QuestionType.TRUE_FALSE));
        Assert.True(AlbumRules.IsRecallType(QuestionType.IMAGE_CHOICE));
        Assert.False(AlbumRules.IsRecallType(QuestionType.MATCHING));
        Assert.False(AlbumRules.IsRecallType(QuestionType.ORDERING));
        Assert.False(AlbumRules.IsRecallType(null));
    }

    private static AlbumRules.RecallCandidate Candidate(long question, long attempt, int day, bool correct) =>
        new(question, attempt, new DateTime(2026, 10, day, 8, 0, 0, DateTimeKind.Utc), correct);

    [Fact]
    public void Recall_prefers_wrong_questions_then_the_most_recent_submission_and_never_repeats_a_question()
    {
        var picked = AlbumRules.PickRecall(
        [
            Candidate(1, 10, 20, true),
            Candidate(2, 10, 20, false),
            Candidate(3, 11, 22, true),
            Candidate(4, 9, 18, false),
            Candidate(5, 12, 24, false),
            Candidate(5, 9, 18, true),     // cùng câu làm hai lần: chỉ lấy một lần
            Candidate(6, 12, 24, true),
        ]);

        // Câu sai trước (lượt mới nhất trước): 5, 2, 4; không lấy quá 3 câu.
        Assert.Equal([5L, 2L, 4L], picked);
    }

    [Fact]
    public void Recall_falls_back_to_correct_questions_and_is_empty_without_any_submission()
    {
        // Chỉ có câu đúng: lượt nộp gần nhất trước.
        Assert.Equal([6L, 3L, 1L], AlbumRules.PickRecall(
        [
            Candidate(1, 10, 20, true), Candidate(3, 11, 22, true), Candidate(6, 12, 24, true), Candidate(7, 8, 1, true),
        ]));
        Assert.Empty(AlbumRules.PickRecall([]));
    }
}
