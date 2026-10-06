using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Lưu câu trả lời trong lúc làm: lưu ngay từng câu theo cả tám dạng, vào tiếp đúng câu trả lời đã lưu (kể cả sau khi tải lại), ghi đè và xóa, từ chối
/// câu trả lời sai cấu trúc, và chỉ chủ lượt mới gửi và xem được.
/// </summary>
public sealed class AttemptAnswerApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly AttemptTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    /// <summary>Lưu một câu trả lời đúng cho mỗi dạng, ở cấp 1, 2 và 3 (mỗi cấp một bộ dạng), rồi đọc lại đúng như đã lưu.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Every_question_type_of_the_level_saves_immediately_and_comes_back_as_the_student_left_it(int level)
    {
        var q = await _k.OpenQuizAsync(level);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var sent = new Dictionary<long, QuestionSubmission>();

        foreach (var question in attempt.Questions)
        {
            var answer = AttemptTestKit.Correct(question, AttemptTestKit.DetailOf(q, question.QuestionId));
            var saved = await _k.SaveAsync(q.StudentToken, attempt.Id, question.QuestionId, answer);
            Assert.Equal((question.QuestionId, true, q.Questions.Count), (saved.QuestionId, saved.Answered, saved.QuestionCount));
            sent[question.QuestionId] = answer;
        }

        var reloaded = await _k.GetAsync(q.StudentToken, attempt.Id);
        Assert.Equal(q.Questions.Count, reloaded.AnsweredCount);
        foreach (var question in reloaded.Questions)
        {
            Assert.True(question.Answered);
            var expected = sent[question.QuestionId];
            var actual = question.Answer!;
            Assert.Equal(expected.SelectedIndex, actual.SelectedIndex);
            Assert.Equal(expected.Matches, actual.Matches);
            Assert.Equal(expected.Order, actual.Order);
            Assert.Equal(expected.Blanks, actual.Blanks);
            Assert.Equal(expected.Statements, actual.Statements);
        }
    }

    [Fact]
    public async Task Saving_overwrites_the_whole_answer_and_an_empty_body_clears_it()
    {
        var q = await _k.OpenQuizAsync(2, types: [QuestionType.SINGLE_CHOICE, QuestionType.FILL_BLANK]);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var (single, fill) = (attempt.Questions[0], attempt.Questions[1]);

        var first = await _k.SaveAsync(q.StudentToken, attempt.Id, single.QuestionId, new QuestionSubmission(SelectedIndex: 0));
        Assert.Equal(1, first.AnsweredCount);
        await _k.SaveAsync(q.StudentToken, attempt.Id, single.QuestionId, new QuestionSubmission(SelectedIndex: 3));
        Assert.Equal(3, (await _k.GetAsync(q.StudentToken, attempt.Id)).Questions[0].Answer!.SelectedIndex);

        await _k.SaveAsync(q.StudentToken, attempt.Id, fill.QuestionId, new QuestionSubmission(Blanks: ["  lý   thái tổ "]));
        Assert.Equal(["lý   thái tổ"], (await _k.GetAsync(q.StudentToken, attempt.Id)).Questions[1].Answer!.Blanks);   // cắt khoảng trắng đầu cuối

        var cleared = await _k.SaveAsync(q.StudentToken, attempt.Id, single.QuestionId, new QuestionSubmission());
        Assert.Equal((false, 1), (cleared.Answered, cleared.AnsweredCount));
        var reloaded = await _k.GetAsync(q.StudentToken, attempt.Id);
        Assert.Equal((false, (int?)null), (reloaded.Questions[0].Answered, reloaded.Questions[0].Answer?.SelectedIndex));

        // Mỗi câu một dòng trong CSDL dù lưu nhiều lần.
        var rows = await _k.AnswerRowsAsync(attempt.Id);
        Assert.Equal(2, rows.Count);
        Assert.Null(rows.Single(r => r.QuestionId == single.QuestionId).AnswerId);
    }

    [Fact]
    public async Task A_saved_answer_is_graded_and_stored_with_its_parts()
    {
        var q = await _k.OpenQuizAsync(1, types: [QuestionType.MATCHING, QuestionType.ORDERING, QuestionType.IMAGE_CHOICE]);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var (matching, ordering, image) = (attempt.Questions[0], attempt.Questions[1], attempt.Questions[2]);

        await _k.SaveAsync(q.StudentToken, attempt.Id, matching.QuestionId, AttemptTestKit.Correct(matching, q.Questions[0]));
        await _k.SaveAsync(q.StudentToken, attempt.Id, ordering.QuestionId, AttemptTestKit.Wrong(ordering, q.Questions[1]));
        await _k.SaveAsync(q.StudentToken, attempt.Id, image.QuestionId, AttemptTestKit.Correct(image, q.Questions[2]));

        var rows = await _k.AnswerRowsAsync(attempt.Id);
        var m = rows.Single(r => r.QuestionId == matching.QuestionId);
        Assert.Equal((true, (short)3, (short)3, 2m, 3, 3), (m.IsCorrect, m.PartsTotal, m.PartsCorrect, m.PointEarned, m.Parts.Count, m.Parts.Count(p => p.IsCorrect)));
        Assert.All(m.Parts, p => Assert.True(p.AnswerId is not null && p.BlankIndex is null && p.SelectedAnswerId == p.AnswerId));

        var o = rows.Single(r => r.QuestionId == ordering.QuestionId);
        Assert.Equal((false, (short)4, (short)0, 0m, 4), (o.IsCorrect, o.PartsTotal, o.PartsCorrect, o.PointEarned, o.Parts.Count));
        Assert.Equal([1, 2, 3, 4], o.Parts.Select(p => (int)p.SelectedPosition!.Value).Order());   // đủ mọi vị trí em đã đặt

        var i = rows.Single(r => r.QuestionId == image.QuestionId);
        Assert.Equal((true, (short)1, (short)1, 2m, 0), (i.IsCorrect, i.PartsTotal, i.PartsCorrect, i.PointEarned, i.Parts.Count));
        Assert.NotNull(i.AnswerId);
    }

    // ---- Câu trả lời sai cấu trúc ----

    [Fact]
    public async Task An_answer_that_does_not_fit_the_question_is_rejected_when_saving()
    {
        var q = await _k.OpenQuizAsync(1, types: [QuestionType.IMAGE_CHOICE, QuestionType.MATCHING, QuestionType.ORDERING]);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var (image, matching, ordering) = (attempt.Questions[0], attempt.Questions[1], attempt.Questions[2]);

        async Task AssertRejectedAsync(long questionId, QuestionSubmission answer)
        {
            var (res, body) = await _k.SaveRawAsync(q.StudentToken, attempt.Id, questionId, answer);
            Assert.Equal((HttpStatusCode.BadRequest, "INVALID", false), (res.StatusCode, body.ErrorCode, body.Success));
            Assert.Contains("Câu trả lời không hợp lệ", body.Message);
        }

        await AssertRejectedAsync(image.QuestionId, new QuestionSubmission(SelectedIndex: 7));
        await AssertRejectedAsync(image.QuestionId, new QuestionSubmission(SelectedIndex: -1));
        await AssertRejectedAsync(matching.QuestionId, new QuestionSubmission(Matches: [0, 0, 1]));           // một vế phải nối hai lần
        await AssertRejectedAsync(matching.QuestionId, new QuestionSubmission(Matches: [0, 1, 5]));           // vế nối ngoài phạm vi
        await AssertRejectedAsync(matching.QuestionId, new QuestionSubmission(Matches: [0, 1, 2, 0]));        // nhiều cặp hơn số hình
        await AssertRejectedAsync(ordering.QuestionId, new QuestionSubmission(Order: [0, 1, 2]));              // thiếu một mục
        await AssertRejectedAsync(ordering.QuestionId, new QuestionSubmission(Order: [0, 1, 2, 2]));           // một mục hai lần
        await AssertRejectedAsync(ordering.QuestionId, new QuestionSubmission(Order: [0, 1, 2, 9]));           // mục không có trong câu

        Assert.Empty(await _k.AnswerRowsAsync(attempt.Id));   // không câu nào bị ghi
    }

    [Fact]
    public async Task Fill_in_and_statement_answers_are_checked_against_the_question_too()
    {
        var level2 = await _k.OpenQuizAsync(2, types: [QuestionType.FILL_BLANK]);
        var attempt2 = await _k.StartAsync(level2.StudentToken, level2.Id);
        var fill = attempt2.Questions[0];
        var (tooMany, bodyMany) = await _k.SaveRawAsync(level2.StudentToken, attempt2.Id, fill.QuestionId, new QuestionSubmission(Blanks: ["a", "b"]));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID"), (tooMany.StatusCode, bodyMany.ErrorCode));
        var (tooLong, _) = await _k.SaveRawAsync(level2.StudentToken, attempt2.Id, fill.QuestionId, new QuestionSubmission(Blanks: [new string('a', 256)]));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var level3 = await _k.OpenQuizAsync(3, types: [QuestionType.MULTI_TRUE_FALSE]);
        var attempt3 = await _k.StartAsync(level3.StudentToken, level3.Id);
        var (many, _) = await _k.SaveRawAsync(level3.StudentToken, attempt3.Id, attempt3.Questions[0].QuestionId, new QuestionSubmission(Statements: [true, false, true, true]));
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);

        // Chỉ chọn một phần của các mệnh đề vẫn hợp lệ: mệnh đề chưa chọn không được điểm.
        var partial = await _k.SaveAsync(level3.StudentToken, attempt3.Id, attempt3.Questions[0].QuestionId, new QuestionSubmission(Statements: [true]));
        Assert.True(partial.Answered);
    }

    [Fact]
    public async Task Saving_to_a_question_that_is_not_in_the_quiz_is_not_found()
    {
        var q = await _k.OpenQuizAsync(2);
        var other = await _k.OpenQuizAsync(2);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var (res, body) = await _k.SaveRawAsync(q.StudentToken, attempt.Id, other.Questions[0].Id, new QuestionSubmission(SelectedIndex: 0));
        Assert.Equal((HttpStatusCode.NotFound, "BLOCKED"), (res.StatusCode, body.ErrorCode));
    }

    // ---- Chỉ chủ lượt ----

    [Fact]
    public async Task Only_the_owner_of_an_attempt_can_read_save_submit_or_view_it()
    {
        var q = await _k.OpenQuizAsync(2);
        var classmate = await _k.AnotherStudentAsync(q);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var question = attempt.Questions[0];

        Assert.Equal(HttpStatusCode.NotFound, (await _k.GetRawAsync(classmate, attempt.Id)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.SaveRawAsync(classmate, attempt.Id, question.QuestionId, new QuestionSubmission(SelectedIndex: 0))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.SubmitRawAsync(classmate, attempt.Id)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.ResultRawAsync(classmate, attempt.Id)).Response.StatusCode);
        Assert.Empty(await _k.AnswerRowsAsync(attempt.Id));

        // Bạn cùng lớp bắt đầu lượt của chính mình, không đụng lượt của em.
        var mine = await _k.StartAsync(classmate, q.Id);
        Assert.NotEqual(attempt.Id, mine.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.GetRawAsync(q.StudentToken, mine.Id)).Response.StatusCode);
    }

    [Fact]
    public async Task A_finished_attempt_refuses_new_answers_and_is_no_longer_readable_as_a_quiz_to_continue()
    {
        var q = await _k.OpenQuizAsync(2);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        await _k.SubmitAsync(q.StudentToken, attempt.Id);

        var (res, body) = await _k.SaveRawAsync(q.StudentToken, attempt.Id, attempt.Questions[0].QuestionId, new QuestionSubmission(SelectedIndex: 0));
        Assert.Equal((HttpStatusCode.Conflict, "ATTEMPT_FINISHED", "Em đã nộp bài này rồi. Em hãy xem kết quả."), (res.StatusCode, body.ErrorCode, body.Message));
        var (get, getBody) = await _k.GetRawAsync(q.StudentToken, attempt.Id);
        Assert.Equal((HttpStatusCode.Conflict, "ATTEMPT_FINISHED"), (get.StatusCode, getBody.ErrorCode));
        var (start, startBody) = await _k.StartRawAsync(q.StudentToken, q.Id);
        Assert.Equal((HttpStatusCode.Conflict, "ATTEMPT_FINISHED"), (start.StatusCode, startBody.ErrorCode));
        Assert.Equal(1, await factory.WithDbAsync(db => db.Attempts.CountAsync(a => a.QuizId == q.Id)));
    }
}
