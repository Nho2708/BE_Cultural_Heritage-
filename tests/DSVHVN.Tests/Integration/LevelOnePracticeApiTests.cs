using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Bài chơi cấp 1 dạng luyện tập: kiểm từng câu ngay (lần kiểm đầu được chấm, các lần sau chỉ cho em thử lại), đúng sai từng phần theo thứ tự đang hiện,
/// các lỗi 400, 404, 409, 422, bản app cũ vẫn lưu câu trả lời bằng PUT, lời nhắc trước khi chơi và kết quả cấp 1 (câu đúng ngay lần đầu, lời khen cụ thể,
/// thẻ di sản, câu mang về nhà).
/// </summary>
public sealed class LevelOnePracticeApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly AttemptTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    private static StudentAttemptQuestionDto Of(StudentAttemptDto attempt, QuestionType type) => attempt.Questions.Single(q => q.QuestionType == type);

    private static void AssertSameAnswer(QuestionSubmission expected, QuestionSubmission actual)
    {
        Assert.Equal(expected.SelectedIndex, actual.SelectedIndex);
        Assert.Equal(expected.Matches, actual.Matches);
        Assert.Equal(expected.Order, actual.Order);
    }

    // ---- Kiểm từng câu ----

    [Fact]
    public async Task The_first_check_is_the_graded_answer_and_later_checks_only_tell_right_from_wrong()
    {
        var q = await _k.OpenQuizAsync(1);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        Assert.True(attempt.PracticeMode);
        Assert.All(attempt.Questions, x => Assert.False(x.Checked));

        var tf = Of(attempt, QuestionType.TRUE_FALSE);
        var detail = AttemptTestKit.DetailOf(q, tf.QuestionId);
        var wrong = AttemptTestKit.Wrong(tf, detail);
        var right = AttemptTestKit.Correct(tf, detail);

        var first = await _k.CheckAsync(q.StudentToken, attempt.Id, tf.QuestionId, wrong);
        Assert.Equal((tf.QuestionId, false, false, 1, 1, 4), (first.QuestionId, first.IsCorrect, first.FirstTryCorrect, first.TryCount, first.AnsweredCount, first.QuestionCount));
        Assert.Equal([false], first.Parts);
        AssertSameAnswer(right, first.CorrectAnswer);
        Assert.Equal(detail.Explanation, first.Explanation);

        var second = await _k.CheckAsync(q.StudentToken, attempt.Id, tf.QuestionId, right);
        Assert.Equal((true, false, 2, 1), (second.IsCorrect, second.FirstTryCorrect, second.TryCount, second.AnsweredCount));
        Assert.Equal([true], second.Parts);

        // Lượt đang làm: câu đã kiểm, câu trả lời đã lưu vẫn là lần đầu (sai); các câu khác chưa kiểm.
        var reloaded = await _k.GetAsync(q.StudentToken, attempt.Id);
        Assert.True(reloaded.PracticeMode);
        var shown = reloaded.Questions.Single(x => x.QuestionId == tf.QuestionId);
        Assert.True(shown.Checked);
        Assert.Equal(wrong.SelectedIndex, shown.Answer!.SelectedIndex);
        Assert.All(reloaded.Questions.Where(x => x.QuestionId != tf.QuestionId), x => Assert.False(x.Checked));

        var row = await factory.WithDbAsync(db => db.AttemptAnswers.SingleAsync(a => a.AttemptId == attempt.Id && a.QuestionId == tf.QuestionId));
        Assert.Equal((2, false), ((int)row.TryCount, row.IsCorrect));
    }

    [Fact]
    public async Task Saving_with_put_does_not_overwrite_a_checked_question_and_an_old_client_still_works_on_a_question_never_checked()
    {
        var q = await _k.OpenQuizAsync(1);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var tf = Of(attempt, QuestionType.TRUE_FALSE);
        var image = Of(attempt, QuestionType.IMAGE_CHOICE);
        var tfDetail = AttemptTestKit.DetailOf(q, tf.QuestionId);
        var imageDetail = AttemptTestKit.DetailOf(q, image.QuestionId);

        // Câu chưa kiểm: PUT ghi đè và xóa như trước (app cũ).
        await _k.SaveAsync(q.StudentToken, attempt.Id, image.QuestionId, AttemptTestKit.Wrong(image, imageDetail));
        var overwritten = await _k.SaveAsync(q.StudentToken, attempt.Id, image.QuestionId, AttemptTestKit.Correct(image, imageDetail));
        Assert.True(overwritten.Answered);
        var cleared = await _k.SaveAsync(q.StudentToken, attempt.Id, image.QuestionId, new QuestionSubmission());
        Assert.False(cleared.Answered);

        // Một câu đã lưu bằng PUT (chưa kiểm) thì lần kiểm đầu tiên vẫn là lần được chấm, thay câu đã lưu.
        await _k.SaveAsync(q.StudentToken, attempt.Id, tf.QuestionId, AttemptTestKit.Wrong(tf, tfDetail));
        var check = await _k.CheckAsync(q.StudentToken, attempt.Id, tf.QuestionId, AttemptTestKit.Correct(tf, tfDetail));
        Assert.Equal((true, true, 1), (check.IsCorrect, check.FirstTryCorrect, check.TryCount));

        // Đã kiểm rồi: PUT không đổi câu trả lời đã chốt, kể cả gửi rỗng, và trả như đã lưu.
        var kept = await _k.SaveAsync(q.StudentToken, attempt.Id, tf.QuestionId, AttemptTestKit.Wrong(tf, tfDetail));
        Assert.True(kept.Answered);
        var keptAfterClear = await _k.SaveAsync(q.StudentToken, attempt.Id, tf.QuestionId, new QuestionSubmission());
        Assert.True(keptAfterClear.Answered);
        var shown = (await _k.GetAsync(q.StudentToken, attempt.Id)).Questions.Single(x => x.QuestionId == tf.QuestionId);
        Assert.Equal(AttemptTestKit.Correct(tf, tfDetail).SelectedIndex, shown.Answer!.SelectedIndex);
    }

    [Fact]
    public async Task Matching_and_ordering_report_each_part_in_the_order_shown_on_screen()
    {
        var q = await _k.OpenQuizAsync(1);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);

        var matching = Of(attempt, QuestionType.MATCHING);
        var matchDetail = AttemptTestKit.DetailOf(q, matching.QuestionId);
        var good = AttemptTestKit.Correct(matching, matchDetail).Matches!;
        var swapped = good.ToList();
        (swapped[0], swapped[1]) = (swapped[1], swapped[0]);   // hai hình đầu nối nhầm tên của nhau
        var matchCheck = await _k.CheckAsync(q.StudentToken, attempt.Id, matching.QuestionId, new QuestionSubmission(Matches: swapped));
        Assert.False(matchCheck.IsCorrect);
        Assert.Equal(matching.LeftItems.Count, matchCheck.Parts.Count);
        Assert.Equal(Enumerable.Range(0, good.Count).Select(i => i >= 2).ToList(), matchCheck.Parts);
        Assert.Equal(good, matchCheck.CorrectAnswer.Matches);

        var ordering = Of(attempt, QuestionType.ORDERING);
        var orderDetail = AttemptTestKit.DetailOf(q, ordering.QuestionId);
        var correctOrder = AttemptTestKit.Correct(ordering, orderDetail).Order!;
        var swappedOrder = correctOrder.ToList();
        (swappedOrder[0], swappedOrder[1]) = (swappedOrder[1], swappedOrder[0]);   // hai mục đầu đổi chỗ
        var orderCheck = await _k.CheckAsync(q.StudentToken, attempt.Id, ordering.QuestionId, new QuestionSubmission(Order: swappedOrder));
        Assert.False(orderCheck.IsCorrect);
        Assert.Equal(ordering.Items.Count, orderCheck.Parts.Count);
        // Từng mục của "items" theo thứ tự đang hiện: mục nào đang ở đúng vị trí.
        var expectedParts = Enumerable.Range(0, ordering.Items.Count).Select(item => item != correctOrder[0] && item != correctOrder[1]).ToList();
        Assert.Equal(expectedParts, orderCheck.Parts);
        Assert.Equal(correctOrder, orderCheck.CorrectAnswer.Order);

        var ok = await _k.CheckAsync(q.StudentToken, attempt.Id, ordering.QuestionId, new QuestionSubmission(Order: correctOrder));
        Assert.True(ok.IsCorrect);
        Assert.All(ok.Parts, Assert.True);
        Assert.False(ok.FirstTryCorrect);
    }

    [Fact]
    public async Task An_empty_answer_or_matching_not_fully_connected_is_refused_and_nothing_is_stored()
    {
        var q = await _k.OpenQuizAsync(1);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var tf = Of(attempt, QuestionType.TRUE_FALSE);
        var matching = Of(attempt, QuestionType.MATCHING);

        var empty = await DiscoveryTestKit.Fail(
            Http.PostAsync<object>($"{AttemptTestKit.Student}/attempts/{attempt.Id}/answers/{tf.QuestionId}/check", new { }, q.StudentToken),
            HttpStatusCode.BadRequest, "ANSWER_REQUIRED_TO_CHECK");
        Assert.Equal("Em hãy chọn câu trả lời trước khi kiểm tra.", empty.Message);

        var good = AttemptTestKit.Correct(matching, AttemptTestKit.DetailOf(q, matching.QuestionId)).Matches!;
        var partial = good.ToList();
        partial[^1] = null;
        var incomplete = await DiscoveryTestKit.Fail(
            Http.PostAsync<object>($"{AttemptTestKit.Student}/attempts/{attempt.Id}/answers/{matching.QuestionId}/check", new { matches = partial }, q.StudentToken),
            HttpStatusCode.BadRequest, "MATCHES_INCOMPLETE");
        Assert.Equal("Em nối đủ các cặp rồi kiểm tra nhé.", incomplete.Message);
        await DiscoveryTestKit.Fail(
            Http.PostAsync<object>($"{AttemptTestKit.Student}/attempts/{attempt.Id}/answers/{matching.QuestionId}/check", new { }, q.StudentToken),
            HttpStatusCode.BadRequest, "MATCHES_INCOMPLETE");

        // Câu trả lời sai cấu trúc vẫn bị từ chối như khi lưu.
        await DiscoveryTestKit.Fail(
            Http.PostAsync<object>($"{AttemptTestKit.Student}/attempts/{attempt.Id}/answers/{tf.QuestionId}/check", new { selectedIndex = 9 }, q.StudentToken),
            HttpStatusCode.BadRequest, "INVALID");

        Assert.Equal(0, await factory.WithDbAsync(db => db.AttemptAnswers.CountAsync(a => a.AttemptId == attempt.Id)));
    }

    [Fact]
    public async Task Checking_is_for_level_one_only_for_the_owner_of_an_open_attempt_and_for_questions_of_the_quiz()
    {
        var levelTwo = await _k.OpenQuizAsync(2);
        var attempt2 = await _k.StartAsync(levelTwo.StudentToken, levelTwo.Id);
        var q2 = attempt2.Questions[0];
        Assert.False(attempt2.PracticeMode);
        Assert.All(attempt2.Questions, x => Assert.False(x.Checked));
        var refused = await DiscoveryTestKit.Fail(_k.CheckRawAsync(levelTwo.StudentToken, attempt2.Id, q2.QuestionId,
            AttemptTestKit.Correct(q2, AttemptTestKit.DetailOf(levelTwo, q2.QuestionId))).AsObjectAsync(),
            HttpStatusCode.UnprocessableEntity, "PRACTICE_ONLY_FOR_YOUNG_STUDENTS");
        Assert.Equal("Chỉ bài chơi cấp 1 mới kiểm từng câu.", refused.Message);

        var q = await _k.OpenQuizAsync(1);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var tf = Of(attempt, QuestionType.TRUE_FALSE);
        var answer = AttemptTestKit.Correct(tf, AttemptTestKit.DetailOf(q, tf.QuestionId));

        // Bạn cùng lớp không kiểm được lượt của em; câu không thuộc bài; chưa đăng nhập.
        var friend = await _k.AnotherStudentAsync(q);
        await DiscoveryTestKit.Fail(_k.CheckRawAsync(friend, attempt.Id, tf.QuestionId, answer).AsObjectAsync(), HttpStatusCode.NotFound, "BLOCKED");
        await DiscoveryTestKit.Fail(_k.CheckRawAsync(q.StudentToken, attempt.Id, 999999, answer).AsObjectAsync(), HttpStatusCode.NotFound, "BLOCKED");
        var (anonymous, _) = await Http.PostAsync<object>($"{AttemptTestKit.Student}/attempts/{attempt.Id}/answers/{tf.QuestionId}/check", answer);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // Lượt đã nộp thì 409.
        await _k.SubmitAsync(q.StudentToken, attempt.Id);
        await DiscoveryTestKit.Fail(_k.CheckRawAsync(q.StudentToken, attempt.Id, tf.QuestionId, answer).AsObjectAsync(), HttpStatusCode.Conflict, "ATTEMPT_FINISHED");
    }

    [Fact]
    public async Task The_intro_tells_a_level_one_class_to_answer_question_by_question_and_keeps_the_old_notice_for_the_other_levels()
    {
        var one = await _k.OpenQuizAsync(1);
        Assert.Equal("Em trả lời từng câu. Chưa đúng thì em được thử lại. Mỗi bài em chơi một lần.", (await _k.IntroAsync(one.StudentToken, one.Id)).Notice);
        var two = await _k.OpenQuizAsync(2);
        Assert.Equal(Messages.QuizOnlyOnceNotice, (await _k.IntroAsync(two.StudentToken, two.Id)).Notice);
    }

    // ---- Kết quả ----

    [Fact]
    public async Task The_result_counts_first_try_answers_names_the_heritage_and_gives_the_cards_and_the_take_home_question()
    {
        var q = await _k.OpenQuizAsync(1, point: 1m);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);

        // Ba câu đúng ngay lần đầu; câu sắp xếp sai lần đầu rồi sửa lại: vẫn tính sai.
        foreach (var question in attempt.Questions.Where(x => x.QuestionType != QuestionType.ORDERING))
            await _k.CheckAsync(q.StudentToken, attempt.Id, question.QuestionId, AttemptTestKit.Correct(question, AttemptTestKit.DetailOf(q, question.QuestionId)));
        var ordering = Of(attempt, QuestionType.ORDERING);
        var orderDetail = AttemptTestKit.DetailOf(q, ordering.QuestionId);
        await _k.CheckAsync(q.StudentToken, attempt.Id, ordering.QuestionId, AttemptTestKit.Wrong(ordering, orderDetail));
        await _k.CheckAsync(q.StudentToken, attempt.Id, ordering.QuestionId, AttemptTestKit.Correct(ordering, orderDetail));

        var result = await _k.SubmitAsync(q.StudentToken, attempt.Id);
        Assert.Equal((1, 3, 2), (result.Level, result.FirstTryCorrect, result.Stars));   // 3 trên 4 câu: 75% là 2 sao
        Assert.Equal($"Em nhớ đúng ngay 3 trên 4 câu về {q.Setup.Heritage.Name}!", result.Praise);
        Assert.Null(result.Score);

        var card = Assert.Single(result.Cards!);
        Assert.Equal((q.Setup.Heritage.Id, q.Setup.Heritage.Name, true, "Di tích và đồ vật xưa", q.Setup.LessonId),
            (card.HeritageId, card.Name, card.Collected, card.GroupName, card.LessonId));
        Assert.NotNull(card.CollectedAt);
        Assert.Equal((1, 1), (result.AlbumCollected, result.AlbumTotal));
        Assert.Equal($"Nhà mình đã từng đi thăm {q.Setup.Heritage.Name} hay một di tích nào gần nhà chưa?", result.TakeHome);

        // Xem lại kết quả sau đó cho đúng như vậy.
        var again = await _k.ResultAsync(q.StudentToken, attempt.Id);
        Assert.Equal((result.FirstTryCorrect, result.Praise, result.TakeHome), (again.FirstTryCorrect, again.Praise, again.TakeHome));

        // Thống kê của giáo viên tính theo lần đầu: câu sắp xếp tính là sai.
        var row = await factory.WithDbAsync(db => db.AttemptAnswers.SingleAsync(a => a.AttemptId == attempt.Id && a.QuestionId == ordering.QuestionId));
        Assert.Equal((false, 2), (row.IsCorrect, (int)row.TryCount));
    }

    [Fact]
    public async Task A_level_one_pupil_who_missed_every_question_is_told_to_remember_more_next_time_and_cards_do_not_appear_at_other_levels()
    {
        var q = await _k.OpenQuizAsync(1);
        var result = await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Wrong);
        Assert.Equal(0, result.FirstTryCorrect);
        Assert.Equal($"Em đã chơi hết bài về {q.Setup.Heritage.Name}. Lần sau mình cùng nhớ thêm nhé!", result.Praise);
        Assert.Single(result.Cards!);

        var two = await _k.OpenQuizAsync(2);
        var resultTwo = await _k.AnswerAllAndSubmitAsync(two, AttemptTestKit.Correct);
        Assert.Null(resultTwo.FirstTryCorrect);
        Assert.Null(resultTwo.Cards);
        Assert.Null(resultTwo.AlbumCollected);
        Assert.Null(resultTwo.AlbumTotal);
        Assert.Null(resultTwo.TakeHome);
        Assert.Null(resultTwo.Praise);
    }
}

internal static class EnvelopeExtensions
{
    /// <summary>Đổi kiểu dữ liệu của phản hồi sang <c>object</c> để dùng chung <see cref="DiscoveryTestKit.Fail"/>.</summary>
    public static async Task<(HttpResponseMessage Response, Envelope<object> Body)> AsObjectAsync<T>(this Task<(HttpResponseMessage Response, Envelope<T> Body)> call)
    {
        var (response, body) = await call;
        return (response, new Envelope<object>(body.Success, null, body.Message, body.ErrorCode, body.TraceId, body.Errors));
    }
}
