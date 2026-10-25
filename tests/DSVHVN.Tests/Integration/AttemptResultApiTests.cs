using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Nộp bài và chấm: chấm cố định từng câu và từng phần của cả tám dạng, câu bỏ trống ghi 0 điểm, nộp hai lần không chấm lại, và kết quả trình bày theo cấp
/// hiện tại của lớp (cấp 1 sao và lời khen không điểm, cấp 2 điểm và giải thích, cấp 3 thêm phân tích theo chủ đề).
/// </summary>
public sealed class AttemptResultApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly AttemptTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    // ---- Chấm ----

    [Fact]
    public async Task A_perfect_attempt_stores_the_full_score_the_counts_and_every_part_as_correct()
    {
        var q = await _k.OpenQuizAsync(2);   // một đáp án đúng, Đúng/Sai, sắp xếp, điền từ: 4 câu × 2 điểm
        var result = await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);

        Assert.Equal((AttemptStatus.SUBMITTED, 8m, 8m, 100m, 4, 0, 0, 4), (result.Status, result.Score, result.TotalPoint, result.Percent, result.CorrectCount, result.WrongCount, result.BlankCount, result.TotalQuestions));
        Assert.Equal([BadgeCode.PERFECT_SCORE, BadgeCode.ON_TIME], result.Badges.Select(b => b.Code));

        var attempt = await _k.AttemptRowAsync(result.AttemptId);
        Assert.Equal((AttemptStatus.SUBMITTED, 8m, 8m, 4, 4), (attempt.Status, attempt.Score, attempt.TotalPoint, attempt.CorrectCount, attempt.TotalQuestions));
        Assert.NotNull(attempt.SubmittedAt);
        Assert.InRange(attempt.DurationSeconds!.Value, 0, 120);

        var rows = await _k.AnswerRowsAsync(result.AttemptId);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsCorrect && r.PartsCorrect == r.PartsTotal && r.PointEarned == 2m));
        var ordering = rows.Single(r => r.QuestionId == q.Questions[2].Id);
        Assert.Equal((4, (short)4), (ordering.Parts.Count, ordering.PartsTotal));
        var fill = rows.Single(r => r.QuestionId == q.Questions[3].Id);
        var blank = Assert.Single(fill.Parts);
        Assert.Equal(((byte?)1, (long?)null, "Lý Thái Tổ", true), (blank.BlankIndex, blank.AnswerId, blank.ResponseText, blank.IsCorrect));
        Assert.NotNull(blank.SelectedAnswerId);   // câu có kho từ: ghi cả dòng đáp án em đã chọn
    }

    [Fact]
    public async Task A_wrong_attempt_scores_nothing_and_a_missing_answer_gets_a_zero_row_with_all_its_parts()
    {
        var q = await _k.OpenQuizAsync(2);
        var wrong = await _k.AnswerAllAndSubmitAsync(q, (shown, detail) => shown.QuestionType == QuestionType.FILL_BLANK ? null : AttemptTestKit.Wrong(shown, detail));

        Assert.Equal((0m, 0, 3, 1, 4), (wrong.Score, wrong.CorrectCount, wrong.WrongCount, wrong.BlankCount, wrong.TotalQuestions));
        Assert.Equal([BadgeCode.ON_TIME], wrong.Badges.Select(b => b.Code));

        var rows = await _k.AnswerRowsAsync(wrong.AttemptId);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.True(!r.IsCorrect && r.PointEarned == 0m && r.PartsCorrect == 0));
        var fill = rows.Single(r => r.QuestionId == q.Questions[3].Id);
        Assert.Null(fill.AnsweredAt);                                   // câu em chưa trả lời: ghi thêm lúc nộp
        var blank = Assert.Single(fill.Parts);
        Assert.Equal(((byte?)1, (string?)null, false), (blank.BlankIndex, blank.ResponseText, blank.IsCorrect));
        var ordering = rows.Single(r => r.QuestionId == q.Questions[2].Id);
        Assert.Equal((4, (short)4), (ordering.Parts.Count, ordering.PartsTotal));
    }

    [Fact]
    public async Task Submitting_without_any_answer_gives_zero_with_a_row_and_all_parts_for_every_question()
    {
        var q = await _k.OpenQuizAsync(1);
        var result = await _k.AnswerAllAndSubmitAsync(q, (_, _) => null);

        Assert.Equal((1, (decimal?)null, (int?)null), (result.Stars, result.Score, result.CorrectCount));
        var rows = await _k.AnswerRowsAsync(result.AttemptId);
        Assert.Equal(4, rows.Count);
        Assert.Equal([1, 1, 3, 4], rows.OrderBy(r => r.QuestionId).Select(r => (int)r.PartsTotal));   // Đúng/Sai, chọn hình, ghép cặp (3 cặp), sắp xếp (4 mục)
        Assert.Equal([0, 0, 3, 4], rows.OrderBy(r => r.QuestionId).Select(r => r.Parts.Count));
        Assert.All(rows, r => Assert.Equal((false, 0m), (r.IsCorrect, r.PointEarned!.Value)));
        var attempt = await _k.AttemptRowAsync(result.AttemptId);
        Assert.Equal((0m, 8m, 0, 4), (attempt.Score, attempt.TotalPoint, attempt.CorrectCount, attempt.TotalQuestions));
    }

    [Fact]
    public async Task Multi_part_questions_are_graded_by_part_and_an_unanswered_statement_does_not_count()
    {
        var q = await _k.OpenQuizAsync(3, types: [QuestionType.MULTI_TRUE_FALSE, QuestionType.SINGLE_CHOICE, QuestionType.SOURCE_READING], point: 3m);
        var result = await _k.AnswerAllAndSubmitAsync(q, (shown, detail) => shown.QuestionType switch
        {
            // Hai mệnh đề đầu đúng, mệnh đề thứ ba chưa chọn: 2 trên 3 phần.
            QuestionType.MULTI_TRUE_FALSE => AttemptTestKit.Correct(shown, detail) with
            {
                Statements = AttemptTestKit.Correct(shown, detail).Statements!.Select((s, i) => i == 2 ? null : s).ToList(),
            },
            QuestionType.SINGLE_CHOICE => AttemptTestKit.Correct(shown, detail),
            _ => AttemptTestKit.Wrong(shown, detail),
        });

        Assert.Equal((5m, 9m, 1), (result.Score, result.TotalPoint, result.CorrectCount));   // 3 × 2/3 + 3 + 0
        var rows = await _k.AnswerRowsAsync(result.AttemptId);
        var multi = rows.Single(r => r.QuestionId == q.Questions[0].Id);
        Assert.Equal((false, (short)2, (short)3, 2m), (multi.IsCorrect, multi.PartsCorrect, multi.PartsTotal, multi.PointEarned));
        Assert.Equal(1, multi.Parts.Count(p => p.SelectedBool is null));
        Assert.Equal(2, multi.Parts.Count(p => p.IsCorrect));
        // Phần trăm của kết quả cấp 3 và số phần đúng của câu nhiều phần.
        Assert.Equal(55.6m, result.Percent);
        Assert.Equal((2, 3), (result.Analysis!.PartsCorrect, result.Analysis.PartsTotal));
    }

    [Fact]
    public async Task Submitting_twice_returns_the_same_result_and_never_grades_again()
    {
        var q = await _k.OpenQuizAsync(2);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        foreach (var question in attempt.Questions)
            await _k.SaveAsync(q.StudentToken, attempt.Id, question.QuestionId, AttemptTestKit.Correct(question, AttemptTestKit.DetailOf(q, question.QuestionId)));

        var first = await _k.SubmitAsync(q.StudentToken, attempt.Id);
        var second = await _k.SubmitAsync(q.StudentToken, attempt.Id);
        Assert.Equal((first.SubmittedAt, first.Score, first.DurationSeconds), (second.SubmittedAt, second.Score, second.DurationSeconds));
        Assert.Equal(4, (await _k.AnswerRowsAsync(attempt.Id)).Count);
        Assert.Equal(5, (await _k.AnswerRowsAsync(attempt.Id)).Sum(r => r.Parts.Count));   // 4 mục sắp xếp + 1 chỗ trống; nộp lần hai không ghi thêm
    }

    [Fact]
    public async Task Editing_the_question_bank_later_does_not_change_a_graded_attempt()
    {
        var q = await _k.OpenQuizAsync(2, types: [QuestionType.SINGLE_CHOICE]);
        var result = await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);
        Assert.Equal(2m, result.Score);

        // Câu đã bị ẩn khỏi ngân hàng: lượt đã chấm giữ nguyên điểm và vẫn xem được.
        await _k.Quizzes.SetQuestionReviewAsync(q.Questions[0].Id, ReviewStatus.REJECTED);
        var again = await _k.ResultAsync(q.StudentToken, result.AttemptId);
        Assert.Equal((2m, 1), (again.Score, again.CorrectCount));
    }

    // ---- Kết quả theo cấp ----

    [Fact]
    public async Task Level_one_shows_stars_praise_and_badges_but_no_score_percent_or_counts()
    {
        var q = await _k.OpenQuizAsync(1);
        var perfect = await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);

        Assert.Equal((1, "Cấp 1", "STARS", 3), (perfect.Level, perfect.LevelName, perfect.ResultView, perfect.Stars));
        Assert.Equal($"Em nhớ đúng hết 4 câu về {q.Setup.Heritage.Name}! Giỏi quá!", perfect.Praise);
        Assert.Equal(4, perfect.FirstTryCorrect);
        Assert.Equal((null, null, null, null, null, null, 4), (perfect.Score, perfect.TotalPoint, perfect.Percent, perfect.CorrectCount, perfect.WrongCount, perfect.BlankCount, perfect.TotalQuestions));
        Assert.Null(perfect.Analysis);
        Assert.Equal([BadgeCode.PERFECT_SCORE, BadgeCode.ON_TIME], perfect.Badges.Select(b => b.Code));

        // Xem lại: đáp án đúng có nhưng không có đúng sai, số phần hay điểm.
        Assert.True(perfect.AnswersShown);
        Assert.Equal(4, perfect.Questions!.Count);
        Assert.All(perfect.Questions, r => Assert.True(r.IsCorrect is null && r.PartsCorrect is null && r.PartsTotal is null && r.PointEarned is null && r.Question.Point is null));
        Assert.All(perfect.Questions, r => Assert.NotNull(r.Explanation));
    }

    [Theory]
    [InlineData(0, 3)]   // 100%
    [InlineData(1, 2)]   // 75%
    [InlineData(2, 2)]   // 50%: từ 50% là 2 sao
    [InlineData(3, 1)]   // 25%
    [InlineData(4, 1)]
    public async Task Level_one_stars_follow_the_share_of_correct_answers(int wrongCount, int expectedStars)
    {
        // Mỗi câu 1 điểm: tổng 4 điểm; số câu sai quyết định số sao (từ 80% 3 sao, từ 50% 2 sao, còn lại 1 sao).
        var q = await _k.OpenQuizAsync(1, point: 1m);
        var seen = 0;
        var result = await _k.AnswerAllAndSubmitAsync(q, (shown, detail) =>
            seen++ < wrongCount ? AttemptTestKit.Wrong(shown, detail) : AttemptTestKit.Correct(shown, detail));
        Assert.Equal(expectedStars, result.Stars);
    }

    [Fact]
    public async Task Level_two_shows_score_counts_time_and_the_answer_with_the_explanation_of_each_question()
    {
        var q = await _k.OpenQuizAsync(2);
        var result = await _k.AnswerAllAndSubmitAsync(q, (shown, detail) =>
            shown.QuestionType == QuestionType.SINGLE_CHOICE ? AttemptTestKit.Wrong(shown, detail) : AttemptTestKit.Correct(shown, detail));

        Assert.Equal((2, "SCORE", 6m, 8m, 75m, 3, 1, 0), (result.Level, result.ResultView, result.Score, result.TotalPoint, result.Percent, result.CorrectCount, result.WrongCount, result.BlankCount));
        Assert.Null(result.Stars);
        Assert.Null(result.Praise);
        Assert.Null(result.Analysis);
        Assert.NotNull(result.DurationSeconds);

        var review = result.Questions!;
        Assert.Equal(4, review.Count);
        var single = review.Single(r => r.Question.QuestionType == QuestionType.SINGLE_CHOICE);
        Assert.Equal((false, 0m, 2m), (single.IsCorrect, single.PointEarned!.Value, single.Question.Point!.Value));
        Assert.Equal(q.Questions[0].Explanation, single.Explanation);
        // Đáp án đúng ở cùng thứ tự đang hiện, và khác câu trả lời của em.
        Assert.Equal("Năm 2010", single.Question.Options[single.CorrectAnswer.SelectedIndex!.Value].Content);
        Assert.NotEqual(single.CorrectAnswer.SelectedIndex, single.Question.Answer!.SelectedIndex);

        var ordering = review.Single(r => r.Question.QuestionType == QuestionType.ORDERING);
        Assert.Equal(q.Questions[2].Steps, ordering.CorrectAnswer.Order!.Select(i => ordering.Question.Items[i]));
        var fill = review.Single(r => r.Question.QuestionType == QuestionType.FILL_BLANK);
        Assert.Equal(["Lý Thái Tổ"], fill.CorrectAnswer.Blanks);
    }

    [Fact]
    public async Task Answers_are_hidden_when_the_quiz_does_not_allow_showing_them()
    {
        var q = await _k.OpenQuizAsync(2, showAnswers: false);
        var result = await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);
        Assert.Equal((false, null, 8m), (result.AnswersShown, result.Questions, result.Score));
        Assert.Null((await _k.ResultAsync(q.StudentToken, result.AttemptId)).Questions);
    }

    [Fact]
    public async Task Level_three_adds_the_analysis_by_heritage_and_by_question_type_and_the_heritages_to_review()
    {
        var q = await _k.OpenQuizAsync(3, types: [QuestionType.SINGLE_CHOICE, QuestionType.SOURCE_READING, QuestionType.MULTI_TRUE_FALSE], point: 2m);
        var wrongOnSource = await _k.AnswerAllAndSubmitAsync(q, (shown, detail) =>
            shown.QuestionType == QuestionType.SOURCE_READING ? AttemptTestKit.Wrong(shown, detail) : AttemptTestKit.Correct(shown, detail));

        Assert.Equal((3, "ANALYSIS"), (wrongOnSource.Level, wrongOnSource.ResultView));
        var analysis = wrongOnSource.Analysis!;
        var heritage = Assert.Single(analysis.ByHeritage);
        Assert.Equal((q.Setup.Heritage.Id, 3, 2, 66.7m), (heritage.HeritageId, heritage.Answered, heritage.Correct, heritage.RatePercent));
        Assert.Empty(analysis.HeritagesToReview);                          // 66,7% chưa dưới 50%
        Assert.Equal(3, analysis.ByQuestionType.Count);
        Assert.Equal(0m, analysis.ByQuestionType.Single(t => t.QuestionType == QuestionType.SOURCE_READING).RatePercent);
        Assert.Equal(100m, analysis.ByQuestionType.Single(t => t.QuestionType == QuestionType.SINGLE_CHOICE).RatePercent);

        var allWrong = await _k.AnswerAllAndSubmitAsync(
            await _k.OpenQuizAsync(3, types: [QuestionType.SINGLE_CHOICE, QuestionType.SOURCE_READING], setup: q.Setup),
            AttemptTestKit.Wrong);
        var review = Assert.Single(allWrong.Analysis!.HeritagesToReview);
        Assert.Equal((q.Setup.Heritage.Id, 0m), (review.HeritageId, review.RatePercent));
    }

    [Fact]
    public async Task The_result_follows_the_current_level_of_the_class_not_the_level_at_the_time_of_the_attempt()
    {
        var q = await _k.OpenQuizAsync(2, types: [QuestionType.TRUE_FALSE, QuestionType.ORDERING]);
        var result = await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);
        Assert.Equal((2, 4m), (result.Level, result.Score));

        await _k.Quizzes.SetClassGradeAsync(q.Setup.ClassId, 3);   // lớp xuống cấp 1
        var asLevelOne = await _k.ResultAsync(q.StudentToken, result.AttemptId);
        Assert.Equal((1, "STARS", 3, (decimal?)null), (asLevelOne.Level, asLevelOne.ResultView, asLevelOne.Stars, asLevelOne.Score));
        Assert.Equal(4m, (await _k.AttemptRowAsync(result.AttemptId)).Score);   // điểm đã chấm không đổi

        await _k.Quizzes.SetClassGradeAsync(q.Setup.ClassId, 11);  // lớp lên cấp 3
        var asLevelThree = await _k.ResultAsync(q.StudentToken, result.AttemptId);
        Assert.Equal((3, 4m), (asLevelThree.Level, asLevelThree.Score));
        Assert.NotNull(asLevelThree.Analysis);
    }

    [Fact]
    public async Task Viewing_the_result_of_an_attempt_still_in_progress_is_refused()
    {
        var q = await _k.OpenQuizAsync(2);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var (res, body) = await _k.ResultRawAsync(q.StudentToken, attempt.Id);
        Assert.Equal((HttpStatusCode.Conflict, "ATTEMPT_IN_PROGRESS"), (res.StatusCode, body.ErrorCode));
    }

    [Fact]
    public async Task The_result_can_be_viewed_again_after_the_quiz_is_closed_by_the_teacher()
    {
        var q = await _k.OpenQuizAsync(2);
        var result = await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);
        await _k.Quizzes.CloseAsync(q.TeacherToken, q.Id);

        var again = await _k.ResultAsync(q.StudentToken, result.AttemptId);
        Assert.Equal((AttemptStatus.SUBMITTED, 8m), (again.Status, again.Score));
        Assert.Equal(StudentQuizAction.VIEW_RESULT, (await _k.IntroAsync(q.StudentToken, q.Id)).Action);
        Assert.Equal(1, await factory.WithDbAsync(db => db.Attempts.CountAsync(a => a.QuizId == q.Id)));
    }
}
