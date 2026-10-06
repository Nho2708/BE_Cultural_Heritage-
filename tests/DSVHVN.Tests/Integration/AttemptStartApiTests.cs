using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Vào bài kiểm tra và bắt đầu làm: trang trước khi bắt đầu theo trạng thái và theo cấp, điều kiện tạo lượt (đúng lớp, bài học đã xuất bản, quiz đang mở),
/// mỗi em một lượt, vào tiếp đúng lượt đang làm, đề đúng như em thấy (không lộ đáp án, xáo cố định theo lượt).
/// </summary>
public sealed class AttemptStartApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly AttemptTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    // ---- Trang trước khi bắt đầu ----

    [Fact]
    public async Task The_intro_follows_the_quiz_state_and_offers_the_right_action()
    {
        var q = await _k.OpenQuizAsync(2, timeLimitMinutes: 20);

        var intro = await _k.IntroAsync(q.StudentToken, q.Id);
        Assert.Equal((q.Id, 4, (decimal?)8m, (int?)20, StudentQuizState.OPEN, StudentQuizAction.START, "Bắt đầu", (long?)null, 2, "STANDARD"),
            (intro.Id, intro.QuestionCount, intro.TotalPoint, intro.TimeLimitMinutes, intro.State, intro.Action, intro.ActionLabel, intro.AttemptId, intro.Level, intro.Layout));
        Assert.Equal(Messages.QuizOnlyOnceNotice, intro.Notice);
        Assert.True(intro.ShowsClock);

        await _k.Quizzes.SetQuizTimesAsync(q.Id, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(3));
        var notOpen = await _k.IntroAsync(q.StudentToken, q.Id);
        Assert.Equal((StudentQuizAction.NOT_OPEN, "Chưa mở", (string?)null), (notOpen.Action, notOpen.StateLabel, notOpen.Notice));

        await _k.Quizzes.SetQuizTimesAsync(q.Id, DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal((StudentQuizAction.CLOSED, StudentQuizState.CLOSED), ((await _k.IntroAsync(q.StudentToken, q.Id)).Action, (await _k.IntroAsync(q.StudentToken, q.Id)).State));

        await _k.Quizzes.SetQuizTimesAsync(q.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(3));
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var running = await _k.IntroAsync(q.StudentToken, q.Id);
        Assert.Equal((StudentQuizAction.CONTINUE, attempt.Id, AttemptStatus.IN_PROGRESS, "Làm tiếp", (string?)null), (running.Action, running.AttemptId, running.AttemptStatus, running.ActionLabel, running.Notice));

        await _k.SubmitAsync(q.StudentToken, attempt.Id);
        var done = await _k.IntroAsync(q.StudentToken, q.Id);
        Assert.Equal((StudentQuizAction.VIEW_RESULT, StudentQuizState.SUBMITTED, AttemptStatus.SUBMITTED), (done.Action, done.State, done.AttemptStatus));

        // Bài đã đóng vẫn cho em xem kết quả của lượt đã làm.
        await _k.Quizzes.SetQuizTimesAsync(q.Id, DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(StudentQuizAction.VIEW_RESULT, (await _k.IntroAsync(q.StudentToken, q.Id)).Action);
    }

    [Fact]
    public async Task The_intro_hides_points_and_the_clock_from_a_level_one_class_and_always_shows_the_clock_at_level_three()
    {
        var one = await _k.OpenQuizAsync(1);
        var introOne = await _k.IntroAsync(one.StudentToken, one.Id);
        Assert.Equal((1, "LARGE_CARDS", (decimal?)null, (int?)null, false), (introOne.Level, introOne.Layout, introOne.TotalPoint, introOne.TimeLimitMinutes, introOne.ShowsClock));

        var three = await _k.OpenQuizAsync(3);
        var introThree = await _k.IntroAsync(three.StudentToken, three.Id);
        Assert.Equal((3, "COMPACT", (decimal?)6m, (int?)30, true), (introThree.Level, introThree.Layout, introThree.TotalPoint, introThree.TimeLimitMinutes, introThree.ShowsClock));
    }

    // ---- Điều kiện bắt đầu ----

    [Fact]
    public async Task Starting_creates_one_attempt_and_asking_again_returns_the_same_attempt_with_the_same_clock()
    {
        var q = await _k.OpenQuizAsync(2);

        var (first, firstBody) = await _k.StartRawAsync(q.StudentToken, q.Id);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var attempt = firstBody.Data!;
        Assert.Equal((AttemptStatus.IN_PROGRESS, 4, 0, (decimal?)8m), (attempt.Status, attempt.QuestionCount, attempt.AnsweredCount, attempt.TotalPoint));

        var (second, secondBody) = await _k.StartRawAsync(q.StudentToken, q.Id);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((attempt.Id, attempt.StartedAt), (secondBody.Data!.Id, secondBody.Data.StartedAt));
        Assert.Equal(1, await factory.WithDbAsync(db => db.Attempts.CountAsync(a => a.QuizId == q.Id)));
    }

    [Fact]
    public async Task Starting_is_refused_before_the_opening_time_after_the_closing_time_and_for_a_draft_quiz()
    {
        var q = await _k.OpenQuizAsync(2);

        await _k.Quizzes.SetQuizTimesAsync(q.Id, DateTime.UtcNow.AddHours(2), DateTime.UtcNow.AddHours(4));
        var early = await DiscoveryTestKit.Fail(Http.PostAsync<object>($"{AttemptTestKit.Student}/quizzes/{q.Id}/attempts", null, q.StudentToken),
            HttpStatusCode.UnprocessableEntity, "QUIZ_NOT_OPEN");
        Assert.StartsWith("Bài kiểm tra chưa mở. Em quay lại làm bài từ ", early.Message);

        await _k.Quizzes.SetQuizTimesAsync(q.Id, DateTime.UtcNow.AddHours(-4), DateTime.UtcNow.AddMinutes(-1));
        var late = await DiscoveryTestKit.Fail(Http.PostAsync<object>($"{AttemptTestKit.Student}/quizzes/{q.Id}/attempts", null, q.StudentToken),
            HttpStatusCode.UnprocessableEntity, "QUIZ_ENDED");
        Assert.Equal(Messages.QuizEnded.Text, late.Message);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Attempts.CountAsync(a => a.QuizId == q.Id)));

        var draft = await _k.Quizzes.ReadyQuizAsync(q.Setup, "Còn nháp");
        await DiscoveryTestKit.Fail(Http.PostAsync<object>($"{AttemptTestKit.Student}/quizzes/{draft.Id}/attempts", null, q.StudentToken), HttpStatusCode.NotFound, "BLOCKED");
        await DiscoveryTestKit.Fail(Http.GetAsync<object>($"{AttemptTestKit.Student}/quizzes/{draft.Id}", q.StudentToken), HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task A_quiz_of_another_class_or_of_an_unpublished_lesson_looks_like_it_does_not_exist()
    {
        var q = await _k.OpenQuizAsync(2);
        var other = await _k.OpenQuizAsync(2);   // lớp khác, trường khác

        await DiscoveryTestKit.Fail(Http.PostAsync<object>($"{AttemptTestKit.Student}/quizzes/{other.Id}/attempts", null, q.StudentToken), HttpStatusCode.NotFound, "BLOCKED");
        await DiscoveryTestKit.Fail(Http.GetAsync<object>($"{AttemptTestKit.Student}/quizzes/{other.Id}", q.StudentToken), HttpStatusCode.NotFound, "BLOCKED");
        await DiscoveryTestKit.Fail(Http.PostAsync<object>($"{AttemptTestKit.Student}/quizzes/999999/attempts", null, q.StudentToken), HttpStatusCode.NotFound, "BLOCKED");

        await factory.WithDbAsync(async db =>
        {
            (await db.Lessons.SingleAsync(l => l.Id == q.Setup.LessonId)).Status = ContentStatus.ARCHIVED;
            return await db.SaveChangesAsync();
        });
        await DiscoveryTestKit.Fail(Http.PostAsync<object>($"{AttemptTestKit.Student}/quizzes/{q.Id}/attempts", null, q.StudentToken), HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task Only_a_signed_in_student_can_use_the_quiz_area()
    {
        var q = await _k.OpenQuizAsync(2);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync<object>($"{AttemptTestKit.Student}/quizzes/{q.Id}")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.GetAsync<object>($"{AttemptTestKit.Student}/quizzes/{q.Id}", q.TeacherToken)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.PostAsync<object>($"{AttemptTestKit.Student}/quizzes/{q.Id}/attempts", null, q.TeacherToken)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.GetAsync<object>($"{AttemptTestKit.Student}/home", q.TeacherToken)).Response.StatusCode);
    }

    [Fact]
    public async Task A_lesson_that_was_archived_after_the_attempt_started_still_lets_the_student_continue_and_see_the_result()
    {
        var q = await _k.OpenQuizAsync(2);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        await factory.WithDbAsync(async db =>
        {
            (await db.Lessons.SingleAsync(l => l.Id == q.Setup.LessonId)).Status = ContentStatus.ARCHIVED;
            return await db.SaveChangesAsync();
        });

        Assert.Equal(StudentQuizAction.CONTINUE, (await _k.IntroAsync(q.StudentToken, q.Id)).Action);
        await _k.GetAsync(q.StudentToken, attempt.Id);
        var result = await _k.SubmitAsync(q.StudentToken, attempt.Id);
        Assert.Equal(AttemptStatus.SUBMITTED, result.Status);
        Assert.Equal(StudentQuizAction.VIEW_RESULT, (await _k.IntroAsync(q.StudentToken, q.Id)).Action);
    }

    // ---- Đề như em thấy ----

    [Fact]
    public async Task The_attempt_shows_each_question_without_the_answer_key_and_in_a_fixed_order()
    {
        var q = await _k.OpenQuizAsync(2, types: [QuestionType.SINGLE_CHOICE, QuestionType.ORDERING, QuestionType.FILL_BLANK, QuestionType.TRUE_FALSE]);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);

        Assert.Equal((2, "STANDARD", false, 4, 0, (decimal?)8m), (attempt.Level, attempt.Layout, attempt.ReadAloud, attempt.QuestionCount, attempt.AnsweredCount, attempt.TotalPoint));
        Assert.Equal([1, 2, 3, 4], attempt.Questions.Select(x => x.Position));
        Assert.All(attempt.Questions, x => Assert.Equal(((decimal?)2m, false, (QuestionSubmission?)null), (x.Point, x.Answered, x.Answer)));

        var single = attempt.Questions[0];
        var detail = q.Questions[0];
        Assert.Equal(detail.Options.Select(o => o.Content), single.Options.Select(o => o.Content));   // phương án giữ thứ tự lưu

        var ordering = attempt.Questions[1];
        Assert.Equal(q.Questions[1].Steps.Order(), ordering.Items.Order());
        Assert.NotEqual(q.Questions[1].Steps, ordering.Items);                                       // thứ tự ban đầu không bao giờ là thứ tự đúng

        var fill = attempt.Questions[2];
        Assert.Equal((1, 3), (fill.BlankCount, fill.WordBank.Count));                               // đáp án đầu tiên và hai từ gây nhiễu
        Assert.Equal(new[] { "Lê Lợi", "Lý Thái Tổ", "Trần Hưng Đạo" }.Order(), fill.WordBank.Order());

        // Không có chỗ nào của đề chứa đáp án đúng của câu một đáp án: chỉ nội dung phương án.
        var json = System.Text.Json.JsonSerializer.Serialize(attempt, HttpTestExtensions.Json);
        Assert.DoesNotContain("isCorrect", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("explanation", json, StringComparison.OrdinalIgnoreCase);

        // Tải lại hay vào từ thiết bị khác ra đúng thứ tự cũ.
        var again = await _k.GetAsync(q.StudentToken, attempt.Id);
        Assert.Equal(ordering.Items, again.Questions[1].Items);
        Assert.Equal(fill.WordBank, again.Questions[2].WordBank);
        Assert.Equal(attempt.StartedAt, again.StartedAt);
    }

    [Fact]
    public async Task Level_one_pictures_are_shuffled_with_the_pairs_split_into_two_shuffled_columns()
    {
        var q = await _k.OpenQuizAsync(1);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);

        Assert.Equal((1, "LARGE_CARDS", true, false, (decimal?)null), (attempt.Level, attempt.Layout, attempt.ReadAloud, attempt.ShowsClock, attempt.TotalPoint));
        Assert.All(attempt.Questions, x => Assert.Null(x.Point));

        var image = attempt.Questions.Single(x => x.QuestionType == QuestionType.IMAGE_CHOICE);
        Assert.Equal(q.Questions.Single(x => x.QuestionType == QuestionType.IMAGE_CHOICE).Options.Select(o => o.Content).Order(), image.Options.Select(o => o.Content).Order());
        Assert.All(image.Options, o => Assert.NotNull(o.ImageUrl));

        var matching = attempt.Questions.Single(x => x.QuestionType == QuestionType.MATCHING);
        var pairs = q.Questions.Single(x => x.QuestionType == QuestionType.MATCHING).Pairs;
        Assert.Equal(pairs.Select(p => p.LeftImageUrl).Order(), matching.LeftItems.Select(l => l.ImageUrl).Order());
        Assert.Equal(pairs.Select(p => p.Right).Order(), matching.RightItems.Order());
    }

    [Fact]
    public async Task A_quiz_that_shuffles_questions_keeps_the_same_shuffled_order_for_the_whole_attempt()
    {
        var q = await _k.OpenQuizAsync(3, types: Enumerable.Repeat(QuestionType.SINGLE_CHOICE, 8).ToList(), shuffle: true);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var ids = attempt.Questions.Select(x => x.QuestionId).ToList();

        Assert.Equal(q.Questions.Select(x => x.Id).Order(), ids.Order());
        Assert.Equal(Enumerable.Range(1, 8), attempt.Questions.Select(x => x.Position));
        Assert.Equal(ids, (await _k.GetAsync(q.StudentToken, attempt.Id)).Questions.Select(x => x.QuestionId));
        Assert.Equal(ids, (await _k.StartAsync(q.StudentToken, q.Id)).Questions.Select(x => x.QuestionId));
    }

    [Fact]
    public async Task The_deadline_is_the_start_plus_the_time_limit_but_never_later_than_the_closing_time()
    {
        var q = await _k.OpenQuizAsync(3);   // cấp 3: 30 phút
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        Assert.Equal((true, 30), (attempt.ShowsClock, attempt.TimeLimitMinutes));
        Assert.True(Math.Abs((attempt.Deadline!.Value - attempt.StartedAt).TotalSeconds - 1800) < 1);
        Assert.InRange(attempt.RemainingSeconds!.Value, 1795, 1800);

        var soon = await _k.OpenQuizAsync(3);
        var closing = DateTime.UtcNow.AddMinutes(5);
        await _k.Quizzes.SetQuizTimesAsync(soon.Id, DateTime.UtcNow.AddHours(-1), closing);
        var capped = await _k.StartAsync(soon.StudentToken, soon.Id);
        Assert.True(Math.Abs((capped.Deadline!.Value - closing).TotalSeconds) < 1);
        Assert.InRange(capped.RemainingSeconds!.Value, 295, 300);
    }

    [Fact]
    public async Task A_level_two_quiz_without_a_time_limit_has_no_clock_and_the_deadline_is_the_closing_time()
    {
        var q = await _k.OpenQuizAsync(2);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        Assert.Equal((false, (int?)null), (attempt.ShowsClock, attempt.TimeLimitMinutes));
        Assert.True(Math.Abs((attempt.Deadline!.Value - q.Detail.CloseAt!.Value).TotalMinutes) < 190);   // giờ đóng đã được đặt lại bằng CSDL: sau 3 giờ
        Assert.NotNull(attempt.RemainingSeconds);
    }
}
