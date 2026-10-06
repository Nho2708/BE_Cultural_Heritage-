using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Hạn nộp và tự nộp: hạn = lúc bắt đầu + thời gian làm (không muộn hơn giờ đóng), máy chủ nhận bài thêm 30 giây ân hạn, quá hạn thì chấm các câu đã lưu và chuyển
/// sang hết giờ (khi em mở lại, khi lưu câu, khi nộp và ở tác vụ nền), đóng bài cũng tự nộp lượt đang dở.
/// </summary>
public sealed class AttemptTimingApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly AttemptTestKit _k = new(factory);

    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(30);

    private async Task<(AttemptTestKit.Quiz Quiz, StudentAttemptDto Attempt)> OneAnsweredAttemptAsync(int level = 3)
    {
        QuestionType[] types = level == 3
            ? [QuestionType.SINGLE_CHOICE, QuestionType.MULTI_TRUE_FALSE, QuestionType.SOURCE_READING]
            : [QuestionType.SINGLE_CHOICE, QuestionType.TRUE_FALSE, QuestionType.FILL_BLANK];
        var q = await _k.OpenQuizAsync(level, types: types);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var first = attempt.Questions[0];
        await _k.SaveAsync(q.StudentToken, attempt.Id, first.QuestionId, AttemptTestKit.Correct(first, AttemptTestKit.DetailOf(q, first.QuestionId)));
        return (q, attempt);
    }

    [Fact]
    public async Task Opening_an_attempt_past_the_grace_period_grades_the_saved_answers_and_marks_it_expired_at_the_deadline()
    {
        var (q, attempt) = await OneAnsweredAttemptAsync();
        var started = DateTime.UtcNow.AddMinutes(-31);
        await _k.SetAttemptStartAsync(attempt.Id, started);

        var (res, body) = await _k.GetRawAsync(q.StudentToken, attempt.Id);
        Assert.Equal((HttpStatusCode.Conflict, "ATTEMPT_FINISHED", "Đã hết giờ làm bài nên bài của em được nộp tự động. Em hãy xem kết quả."), (res.StatusCode, body.ErrorCode, body.Message));

        var row = await _k.AttemptRowAsync(attempt.Id);
        Assert.Equal((AttemptStatus.EXPIRED, 2m, 6m, 1, 3, 1800), (row.Status, row.Score, row.TotalPoint, row.CorrectCount, row.TotalQuestions, row.DurationSeconds));
        Assert.True(Math.Abs((row.SubmittedAt!.Value - (started + Limit)).TotalSeconds) < 1);   // lúc nộp là đúng hạn nộp, không phải lúc em mở lại
        Assert.Equal(3, (await _k.AnswerRowsAsync(attempt.Id)).Count);                               // câu chưa làm có dòng 0 điểm

        var result = await _k.ResultAsync(q.StudentToken, attempt.Id);
        Assert.Equal((AttemptStatus.EXPIRED, "Tự nộp khi hết giờ", 2m, 1), (result.Status, result.StatusLabel, result.Score, result.CorrectCount));
        Assert.DoesNotContain(result.Badges, b => b.Code == DSVHVN.Domain.Rules.BadgeCode.ON_TIME);
    }

    [Fact]
    public async Task Saving_after_the_grace_period_is_refused_and_ends_the_attempt_but_within_the_grace_it_is_accepted()
    {
        var (q, attempt) = await OneAnsweredAttemptAsync();
        var second = attempt.Questions[1];
        var answer = AttemptTestKit.Correct(second, AttemptTestKit.DetailOf(q, second.QuestionId));

        // Quá hạn nộp 10 giây nhưng còn trong 30 giây ân hạn: vẫn nhận.
        await _k.SetAttemptStartAsync(attempt.Id, DateTime.UtcNow - Limit - TimeSpan.FromSeconds(10));
        var accepted = await _k.SaveAsync(q.StudentToken, attempt.Id, second.QuestionId, answer);
        Assert.Equal((true, 2), (accepted.Answered, accepted.AnsweredCount));
        Assert.Equal(0, accepted.RemainingSeconds);

        // Quá hạn nộp 45 giây: hết ân hạn.
        await _k.SetAttemptStartAsync(attempt.Id, DateTime.UtcNow - Limit - TimeSpan.FromSeconds(45));
        var third = attempt.Questions[2];
        var (res, body) = await _k.SaveRawAsync(q.StudentToken, attempt.Id, third.QuestionId, AttemptTestKit.Correct(third, AttemptTestKit.DetailOf(q, third.QuestionId)));
        Assert.Equal((HttpStatusCode.Conflict, "ATTEMPT_FINISHED"), (res.StatusCode, body.ErrorCode));

        var row = await _k.AttemptRowAsync(attempt.Id);
        Assert.Equal((AttemptStatus.EXPIRED, 2), (row.Status, row.CorrectCount));   // chỉ hai câu đã lưu trước hạn: câu một đúng hết, câu hai chấm theo phần
        var rows = await _k.AnswerRowsAsync(attempt.Id);
        Assert.Null(rows.Single(r => r.QuestionId == third.QuestionId).AnsweredAt);
        Assert.False(rows.Single(r => r.QuestionId == third.QuestionId).IsCorrect);
    }

    [Fact]
    public async Task Submitting_within_the_grace_period_counts_as_submitted_on_time_and_the_duration_is_capped_at_the_time_limit()
    {
        var (q, attempt) = await OneAnsweredAttemptAsync();
        var started = DateTime.UtcNow - Limit - TimeSpan.FromSeconds(12);
        await _k.SetAttemptStartAsync(attempt.Id, started);

        var result = await _k.SubmitAsync(q.StudentToken, attempt.Id);
        Assert.Equal((AttemptStatus.SUBMITTED, 1800), (result.Status, result.DurationSeconds));   // quá hạn nộp 12 giây nhưng còn trong ân hạn: vẫn là đã nộp, tính đúng hạn nộp
        var row = await _k.AttemptRowAsync(attempt.Id);
        Assert.True(Math.Abs((row.SubmittedAt!.Value - (started + Limit)).TotalSeconds) < 1);

        // Nộp sớm trong hạn là Đã nộp và có huy hiệu Nộp đúng hạn.
        var (q2, attempt2) = await OneAnsweredAttemptAsync();
        var onTime = await _k.SubmitAsync(q2.StudentToken, attempt2.Id);
        Assert.Equal(AttemptStatus.SUBMITTED, onTime.Status);
        Assert.Contains(onTime.Badges, b => b.Code == DSVHVN.Domain.Rules.BadgeCode.ON_TIME);
        Assert.InRange(onTime.DurationSeconds!.Value, 0, 120);
    }

    [Fact]
    public async Task The_background_task_finishes_only_the_attempts_past_their_deadline_plus_the_grace()
    {
        var (late, lateAttempt) = await OneAnsweredAttemptAsync();
        var (fresh, freshAttempt) = await OneAnsweredAttemptAsync();
        var (graceful, gracefulAttempt) = await OneAnsweredAttemptAsync();
        await _k.SetAttemptStartAsync(lateAttempt.Id, DateTime.UtcNow - Limit - TimeSpan.FromMinutes(2));
        await _k.SetAttemptStartAsync(gracefulAttempt.Id, DateTime.UtcNow - Limit - TimeSpan.FromSeconds(10));

        int expired;
        using (var scope = factory.Services.CreateScope())
            expired = await scope.ServiceProvider.GetRequiredService<AttemptLifecycle>().ExpireOverdueAsync(CancellationToken.None);

        Assert.True(expired >= 1);
        Assert.Equal(AttemptStatus.EXPIRED, (await _k.AttemptRowAsync(lateAttempt.Id)).Status);
        Assert.Equal(AttemptStatus.IN_PROGRESS, (await _k.AttemptRowAsync(freshAttempt.Id)).Status);
        Assert.Equal(AttemptStatus.IN_PROGRESS, (await _k.AttemptRowAsync(gracefulAttempt.Id)).Status);

        // Chạy lại không chốt lần nữa.
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AttemptLifecycle>().ExpireOverdueAsync(CancellationToken.None);
        Assert.Equal(3, (await _k.AnswerRowsAsync(lateAttempt.Id)).Count);
        Assert.Equal(AttemptStatus.EXPIRED, (await _k.ResultAsync(late.StudentToken, lateAttempt.Id)).Status);
    }

    [Fact]
    public async Task A_level_one_attempt_has_no_time_limit_even_when_the_quiz_still_holds_one_from_before_the_class_changed_level()
    {
        var q = await _k.OpenQuizAsync(2, types: [QuestionType.TRUE_FALSE, QuestionType.ORDERING], timeLimitMinutes: 10);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        await _k.SetAttemptStartAsync(attempt.Id, DateTime.UtcNow.AddMinutes(-60));

        await _k.Quizzes.SetClassGradeAsync(q.Setup.ClassId, 2);   // lớp xuống cấp 1: không còn thời gian làm
        var view = await _k.GetAsync(q.StudentToken, attempt.Id);
        Assert.Equal((AttemptStatus.IN_PROGRESS, false, (int?)null), (view.Status, view.ShowsClock, view.TimeLimitMinutes));
        Assert.True(view.Deadline > DateTime.UtcNow.AddHours(1));   // hạn nộp là giờ đóng của bài
    }

    [Fact]
    public async Task Closing_the_quiz_grades_the_saved_answers_and_ends_every_attempt_in_progress()
    {
        var (q, attempt) = await OneAnsweredAttemptAsync(level: 2);
        var other = await _k.AnotherStudentAsync(q);
        var otherAttempt = await _k.StartAsync(other, q.Id);

        var closed = await _k.Quizzes.CloseAsync(q.TeacherToken, q.Id);
        Assert.Equal(2, closed.ExpiredAttempts);

        var row = await _k.AttemptRowAsync(attempt.Id);
        Assert.Equal((AttemptStatus.EXPIRED, 2m, 6m, 1, 3), (row.Status, row.Score, row.TotalPoint, row.CorrectCount, row.TotalQuestions));
        Assert.Equal(3, (await _k.AnswerRowsAsync(attempt.Id)).Count);
        var untouched = await _k.AttemptRowAsync(otherAttempt.Id);
        Assert.Equal((AttemptStatus.EXPIRED, 0m), (untouched.Status, untouched.Score));

        var (res, body) = await _k.GetRawAsync(q.StudentToken, attempt.Id);
        Assert.Equal((HttpStatusCode.Conflict, "ATTEMPT_FINISHED"), (res.StatusCode, body.ErrorCode));
        Assert.Equal(2m, (await _k.ResultAsync(q.StudentToken, attempt.Id)).Score);
        // Quiz đã đóng: em chưa làm không bắt đầu được nữa.
        var late = await _k.AnotherStudentAsync(q, "Lê Văn Cường");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _k.StartRawAsync(late, q.Id)).Response.StatusCode);
    }

    [Fact]
    public async Task The_quiz_auto_closer_waits_for_the_grace_period_after_the_closing_time()
    {
        var q = await _k.OpenQuizAsync(2);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);

        await _k.Quizzes.SetQuizTimesAsync(q.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddSeconds(-10));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DSVHVN.Application.Quizzes.QuizAutoCloser>().CloseDueAsync(CancellationToken.None);
        Assert.Equal(QuizStatus.PUBLISHED, (await _k.Quizzes.GetAsync(q.TeacherToken, q.Id)).Status);
        // Còn trong 30 giây ân hạn: em vẫn nộp được bài đang làm.
        Assert.Equal(AttemptStatus.SUBMITTED, (await _k.SubmitAsync(q.StudentToken, attempt.Id)).Status);

        await _k.Quizzes.SetQuizTimesAsync(q.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(-2));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DSVHVN.Application.Quizzes.QuizAutoCloser>().CloseDueAsync(CancellationToken.None);
        Assert.Equal(QuizStatus.CLOSED, (await _k.Quizzes.GetAsync(q.TeacherToken, q.Id)).Status);
    }
}
