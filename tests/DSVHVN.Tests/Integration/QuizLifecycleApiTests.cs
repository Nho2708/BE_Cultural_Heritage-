using System.Net;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Quizzes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Phát hành, sửa sau khi phát hành, đóng bằng tay và tự đóng, nhân bản bài kiểm tra qua Api thật: điều kiện phát hành liệt kê đủ những gì còn thiếu,
/// phát hành khóa danh sách câu và ghi nhật ký, bài đã phát hành chỉ sửa tên, mô tả, giờ đóng, đóng không mở lại và tự nộp lượt đang dở.
/// </summary>
public sealed class QuizLifecycleApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly QuizTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string Q = QuizTestKit.Quizzes;

    private static void AssertFail(HttpResponseMessage res, Envelope<object> body, HttpStatusCode status, string code)
    {
        Assert.Equal((status, code), (res.StatusCode, body.ErrorCode));
        Assert.False(body.Success);
    }

    private Task<List<DSVHVN.Domain.Audit.AuditLog>> AuditOfAsync(long quizId, AuditAction action) =>
        factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(l => l.TargetType == "quizzes" && l.TargetId == quizId && l.Action == action).ToListAsync());

    // ---- Phát hành ----

    [Fact]
    public async Task Publishing_a_ready_quiz_locks_it_records_the_total_and_writes_one_audit_row()
    {
        var s = await _k.SetupAsync(2);
        var ready = await _k.ReadyQuizAsync(s, "Kiểm tra 15 phút", questions: 3);
        Assert.True(ready.Readiness.IsReady);
        Assert.True(ready.Access.CanPublish);
        Assert.StartsWith("Sau khi phát hành, danh sách câu hỏi và điểm sẽ bị khóa.", ready.PublishNotice);
        Assert.Contains($"mã bài học {s.AccessCode}", ready.PublishNotice);

        var result = await _k.PublishAsync(s.Token, ready.Id);
        var quiz = result.Quiz;

        Assert.Equal((QuizStatus.PUBLISHED, "Đang phát hành", 6m), (quiz.Status, quiz.StatusLabel, quiz.TotalPoint));
        Assert.NotNull(quiz.PublishedAt);
        Assert.Equal(QuizOpenState.NOT_OPEN, quiz.OpenState);
        Assert.True(quiz.Access is { CanEditSettings: false, CanEditQuestions: false, CanDelete: false, CanPublish: false, CanClose: true, CanEditCloseTime: true, CanDuplicate: true });
        Assert.Equal($"Đã phát hành bài kiểm tra \"Kiểm tra 15 phút\". Học sinh lớp {quiz.ClassName} làm bài qua mã bài học {s.AccessCode} từ {QuizRules.FormatVietnamTime(quiz.OpenAt)} đến {QuizRules.FormatVietnamTime(quiz.CloseAt)}.",
            result.Message);

        var stored = await factory.WithDbAsync(db => db.Quizzes.AsNoTracking().SingleAsync(x => x.Id == ready.Id));
        Assert.Equal((QuizStatus.PUBLISHED, 6m), (stored.Status, stored.TotalPoint));

        var log = Assert.Single(await AuditOfAsync(ready.Id, AuditAction.QUIZ_PUBLISHED));
        Assert.Equal((s.School.Teacher.Id, ActorRole.TEACHER, "203.0.113.10"), (log.UserId, log.ActorRole, log.IpAddress));
        Assert.Contains("\"questionCount\":3", log.NewValue);

        // Phát hành lần hai không đổi gì và không ghi thêm nhật ký.
        var again = await _k.PublishAsync(s.Token, ready.Id);
        Assert.Equal(quiz.PublishedAt, again.Quiz.PublishedAt);
        Assert.Equal("Phát hành bài kiểm tra thành công.", again.Message);
        Assert.Single(await AuditOfAsync(ready.Id, AuditAction.QUIZ_PUBLISHED));
    }

    [Fact]
    public async Task Publishing_lists_everything_that_is_missing_at_once()
    {
        var s = await _k.SetupAsync(3, publishLesson: false);   // bài học chưa xuất bản, cấp 3
        var quiz = await _k.CreateAsync(s, "Chưa đủ");

        var (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/publish", null, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.StartsWith("Chưa đủ điều kiện phát hành bài kiểm tra. Còn thiếu: ", body.Message);
        foreach (var missing in new[] { "bài học của bài kiểm tra chưa xuất bản", "ít nhất một câu hỏi", "giờ mở", "giờ đóng", "lớp cấp 3 phải đặt thời gian làm bài" })
            Assert.Contains(missing, body.Message);
        Assert.Equal(QuizStatus.DRAFT, (await _k.GetAsync(s.Token, quiz.Id)).Status);
        Assert.Empty(await AuditOfAsync(quiz.Id, AuditAction.QUIZ_PUBLISHED));
    }

    [Fact]
    public async Task Publishing_needs_the_close_time_in_the_future_and_the_open_time_before_it_and_the_time_limit_that_the_level_asks_for()
    {
        var s = await _k.SetupAsync(3);
        var quiz = await _k.ReadyQuizAsync(s);

        // Giờ đóng đã qua (đặt thẳng vào CSDL vì API chỉ chặn giờ mở sau giờ đóng).
        await _k.SetQuizTimesAsync(quiz.Id, DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddHours(-1));
        var (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/publish", null, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.Contains("giờ đóng phải sau thời điểm hiện tại", body.Message);

        await _k.SetQuizTimesAsync(quiz.Id, DateTime.UtcNow.AddHours(3), DateTime.UtcNow.AddHours(2));
        (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/publish", null, s.Token);
        Assert.Contains("giờ mở phải trước giờ đóng", body.Message);

        // Cấp 3 phải đặt thời gian làm.
        await _k.SetQuizTimesAsync(quiz.Id, QuizTestKit.OpenSoon, QuizTestKit.CloseLater);
        await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{Q}/{quiz.Id}", new { name = "Bỏ giờ làm", openAt = QuizTestKit.OpenSoon, closeAt = QuizTestKit.CloseLater }, s.Token));
        (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/publish", null, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.Contains("lớp cấp 3 phải đặt thời gian làm bài", body.Message);

        await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{Q}/{quiz.Id}", new { name = "Có giờ làm", timeLimitMinutes = 45, openAt = QuizTestKit.OpenSoon, closeAt = QuizTestKit.CloseLater }, s.Token));
        Assert.Equal(QuizStatus.PUBLISHED, (await _k.PublishAsync(s.Token, quiz.Id)).Quiz.Status);
    }

    [Fact]
    public async Task A_level_one_quiz_publishes_without_a_time_limit_and_a_level_two_quiz_with_or_without_one()
    {
        var one = await _k.SetupAsync(1);
        var q1 = await _k.PublishedQuizAsync(one);
        Assert.Null(q1.TimeLimitMinutes);

        var two = await _k.SetupAsync(2);
        var withLimit = await _k.PublishedQuizAsync(two, "Có giờ làm");
        Assert.Equal(30, withLimit.TimeLimitMinutes);
        var noLimit = await _k.ReadyQuizAsync(two, "Không giờ làm");
        await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{Q}/{noLimit.Id}", new { name = "Không giờ làm", openAt = QuizTestKit.OpenSoon, closeAt = QuizTestKit.CloseLater }, two.Token));
        Assert.Null((await _k.PublishAsync(two.Token, noLimit.Id)).Quiz.TimeLimitMinutes);
    }

    [Fact]
    public async Task Publishing_is_blocked_while_the_lesson_is_not_published_and_allowed_once_it_is()
    {
        var s = await _k.SetupAsync(2, publishLesson: false);
        var quiz = await _k.CreateAsync(s, "Của bài nháp", new { timeLimitMinutes = 20, openAt = QuizTestKit.OpenSoon, closeAt = QuizTestKit.CloseLater });
        await _k.AddQuestionsAsync(s.Token, quiz.Id, [(await _k.QuestionAsync(s)).Id]);

        var (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/publish", null, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.Contains("bài học của bài kiểm tra chưa xuất bản", body.Message);

        var lessons = _k.Questions.Lesson;
        await lessons.SaveDraftAsync(s.Token, s.LessonId, await lessons.ComposeAsync(s.Token, s.LessonId));
        await lessons.PublishAsync(s.Token, s.LessonId);
        Assert.Equal(QuizStatus.PUBLISHED, (await _k.PublishAsync(s.Token, quiz.Id)).Quiz.Status);
    }

    [Fact]
    public async Task Publishing_is_blocked_when_a_question_was_hidden_or_the_class_moved_to_another_level_and_works_after_removing_them()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.ReadyQuizAsync(s, questions: 4);   // một đáp án đúng, Đúng/Sai, sắp xếp, điền từ
        var hiddenId = quiz.Questions[0].QuestionId;
        await _k.SetQuestionReviewAsync(hiddenId, ReviewStatus.REJECTED);

        var detail = await _k.GetAsync(s.Token, quiz.Id);
        Assert.False(detail.Questions[0].IsUsable);
        Assert.Equal("Câu hỏi đã bị ẩn hoặc không còn được duyệt.", detail.Questions[0].UnusableReason);
        Assert.False(detail.Readiness.IsReady);
        var (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/publish", null, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.Contains("1 câu hỏi không dùng được", body.Message);

        await Http.DeleteAsync<QuizDetailDto>($"{Q}/{quiz.Id}/questions/{hiddenId}", s.Token);
        Assert.True((await _k.GetAsync(s.Token, quiz.Id)).Readiness.IsReady);

        // Lớp chuyển từ khối 8 (cấp 2) sang khối 3 (cấp 1): câu điền từ chỉ dùng ở cấp 2 nên không còn dùng được (cảnh báo theo câu),
        // và lớp cấp 1 không có thời gian làm bài.
        await _k.SetClassGradeAsync(s.ClassId, 3);
        var moved = await _k.GetAsync(s.Token, quiz.Id);
        Assert.Equal(1, moved.SchoolLevel);
        var fill = Assert.Single(moved.Questions, x => !x.IsUsable);
        Assert.Equal((QuestionType.FILL_BLANK, "Câu hỏi dạng \"Điền từ\" không dùng được ở lớp cấp 1."), (fill.QuestionType, fill.UnusableReason));
        (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/publish", null, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.Contains("không dùng được ở lớp cấp 1", body.Message);
        Assert.Contains("lớp cấp 1 không giới hạn thời gian làm bài", body.Message);
    }

    [Fact]
    public async Task Publishing_needs_an_effective_plan_and_the_lesson_must_belong_to_the_teacher()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.ReadyQuizAsync(s);
        await _k.ExpirePlanAsync(s.School.OrgId);

        var (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/publish", null, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "PLAN_REQUIRED");
        Assert.Contains("Không thể phát hành bài kiểm tra", body.Message);
        Assert.Equal(QuizStatus.DRAFT, (await _k.GetAsync(s.Token, quiz.Id)).Status);
    }

    // ---- Sửa bài đã phát hành ----

    [Fact]
    public async Task A_published_quiz_only_changes_name_description_and_close_time_and_the_locked_fields_must_stay_equal()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.PublishedQuizAsync(s);
        object Body() => new
        {
            name = "Tên mới",
            description = "Mô tả mới",
            difficultyLevel = quiz.DifficultyLevel,
            timeLimitMinutes = quiz.TimeLimitMinutes,
            openAt = quiz.OpenAt,
            closeAt = quiz.CloseAt!.Value.AddHours(1),
            shuffleQuestions = quiz.ShuffleQuestions,
            showAnswersAfterSubmit = quiz.ShowAnswersAfterSubmit,
        };

        var saved = await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{Q}/{quiz.Id}", Body(), s.Token));
        Assert.Equal(("Tên mới", "Mô tả mới", quiz.CloseAt!.Value.AddHours(1)), (saved.Name, saved.Description, saved.CloseAt));
        Assert.Equal((QuizStatus.PUBLISHED, quiz.TimeLimitMinutes, quiz.OpenAt, 3), (saved.Status, saved.TimeLimitMinutes, saved.OpenAt, saved.QuestionCount));

        // Rút ngắn giờ đóng cũng được miễn không sớm hơn hiện tại và vẫn sau giờ mở.
        var shorter = await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{Q}/{quiz.Id}", new
        {
            name = "Tên mới", timeLimitMinutes = quiz.TimeLimitMinutes, difficultyLevel = quiz.DifficultyLevel, openAt = quiz.OpenAt, closeAt = quiz.OpenAt!.Value.AddMinutes(30),
        }, s.Token));
        Assert.Equal(quiz.OpenAt!.Value.AddMinutes(30), shorter.CloseAt);

        var locked = new (string Field, object Body)[]
        {
            ("độ khó", new { name = "x", difficultyLevel = DifficultyLevel.HARD, timeLimitMinutes = quiz.TimeLimitMinutes, openAt = quiz.OpenAt, closeAt = shorter.CloseAt }),
            ("thời gian làm", new { name = "x", difficultyLevel = quiz.DifficultyLevel, timeLimitMinutes = 99, openAt = quiz.OpenAt, closeAt = shorter.CloseAt }),
            ("giờ mở", new { name = "x", difficultyLevel = quiz.DifficultyLevel, timeLimitMinutes = quiz.TimeLimitMinutes, openAt = quiz.OpenAt!.Value.AddMinutes(5), closeAt = shorter.CloseAt }),
            ("xáo câu", new { name = "x", difficultyLevel = quiz.DifficultyLevel, timeLimitMinutes = quiz.TimeLimitMinutes, openAt = quiz.OpenAt, closeAt = shorter.CloseAt, shuffleQuestions = true }),
            ("hiện đáp án", new { name = "x", difficultyLevel = quiz.DifficultyLevel, timeLimitMinutes = quiz.TimeLimitMinutes, openAt = quiz.OpenAt, closeAt = shorter.CloseAt, showAnswersAfterSubmit = true }),
        };
        foreach (var (field, payload) in locked)
        {
            var (res, body) = await Http.PutAsync<object>($"{Q}/{quiz.Id}", payload, s.Token);
            Assert.True((res.StatusCode, body.ErrorCode) == (HttpStatusCode.UnprocessableEntity, "BLOCKED"), $"Đổi {field}: {(int)res.StatusCode} {body.ErrorCode}");
            Assert.Contains("chỉ sửa được tên, mô tả và giờ đóng", body.Message);
        }

        // Giờ đóng phải sau giờ mở; không sớm hơn hiện tại (bài đã mở từ một giờ trước); bỏ trống thì báo thiếu.
        var (before, beforeBody) = await Http.PutAsync<object>($"{Q}/{quiz.Id}", new
        {
            name = "x", difficultyLevel = quiz.DifficultyLevel, timeLimitMinutes = quiz.TimeLimitMinutes, openAt = quiz.OpenAt, closeAt = quiz.OpenAt!.Value.AddMinutes(-1),
        }, s.Token);
        AssertFail(before, beforeBody, HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Giờ đóng không hợp lệ: phải sau giờ mở.", beforeBody.Errors!["closeAt"]);

        var openedAt = new DateTime((DateTime.UtcNow.AddHours(-1).Ticks / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        await _k.SetQuizTimesAsync(quiz.Id, openedAt, DateTime.UtcNow.AddHours(2));
        var (past, pastBody) = await Http.PutAsync<object>($"{Q}/{quiz.Id}", new
        {
            name = "x", difficultyLevel = quiz.DifficultyLevel, timeLimitMinutes = quiz.TimeLimitMinutes, openAt = openedAt, closeAt = DateTime.UtcNow.AddMinutes(-1),
        }, s.Token);
        AssertFail(past, pastBody, HttpStatusCode.BadRequest, "INVALID");
        Assert.Contains("không được sớm hơn hiện tại", pastBody.Errors!["closeAt"]);
        var (none, noneBody) = await Http.PutAsync<object>($"{Q}/{quiz.Id}", new { name = "x", difficultyLevel = quiz.DifficultyLevel, timeLimitMinutes = quiz.TimeLimitMinutes, openAt = openedAt }, s.Token);
        AssertFail(none, noneBody, HttpStatusCode.BadRequest, "REQUIRED");
    }

    // ---- Đóng ----

    [Fact]
    public async Task Closing_ends_the_quiz_for_good_self_submits_unfinished_attempts_and_writes_one_audit_row()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.PublishedQuizAsync(s, "Sắp đóng");
        var open = await _k.AddAttemptAsync(s, quiz.Id, AttemptStatus.IN_PROGRESS, "Lê Văn Cường");
        var done = await _k.AddAttemptAsync(s, quiz.Id, AttemptStatus.SUBMITTED, "Phạm Thị Dung");

        var result = await _k.CloseAsync(s.Token, quiz.Id);

        Assert.Equal((QuizStatus.CLOSED, "Đã đóng", QuizOpenState.ENDED, 1), (result.Quiz.Status, result.Quiz.StatusLabel, result.Quiz.OpenState, result.ExpiredAttempts));
        Assert.Equal("Đã đóng bài kiểm tra \"Sắp đóng\". 1 lượt làm bài đang dở đã được tự nộp.", result.Message);
        Assert.True(result.Quiz.Access is { CanClose: false, CanEditSettings: false, CanEditCloseTime: false, CanDelete: false, CanDuplicate: true });
        Assert.Equal((2, 2), (result.Quiz.SubmittedCount, result.Quiz.StudentCount));

        var attempts = await factory.WithDbAsync(db => db.Attempts.AsNoTracking().Where(a => a.QuizId == quiz.Id).OrderBy(a => a.Id).ToListAsync());
        Assert.Equal((AttemptStatus.EXPIRED, AttemptStatus.SUBMITTED), (attempts[0].Status, attempts[1].Status));
        Assert.Equal(open, attempts[0].Id);
        Assert.NotNull(attempts[0].SubmittedAt);
        Assert.Equal((0m, 6m, 3), (attempts[0].Score, attempts[0].TotalPoint, attempts[0].TotalQuestions));
        Assert.Equal(done, attempts[1].Id);

        var log = Assert.Single(await AuditOfAsync(quiz.Id, AuditAction.QUIZ_CLOSED));
        Assert.Equal((s.School.Teacher.Id, ActorRole.TEACHER), (log.UserId, log.ActorRole));
        Assert.Contains("\"expiredAttempts\":1", log.NewValue);

        // Đóng lại không đổi gì; không mở lại; không sửa được nữa.
        var again = await _k.CloseAsync(s.Token, quiz.Id);
        Assert.Equal(("Đóng bài kiểm tra thành công.", 0), (again.Message, again.ExpiredAttempts));
        Assert.Single(await AuditOfAsync(quiz.Id, AuditAction.QUIZ_CLOSED));
        var (res, body) = await Http.PutAsync<object>($"{Q}/{quiz.Id}", new { name = "Sửa sau khi đóng" }, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("Bài kiểm tra đã đóng", body.Message);
        (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/publish", null, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
    }

    [Fact]
    public async Task A_draft_cannot_be_closed()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.ReadyQuizAsync(s);
        var (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/close", null, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("Chỉ đóng được bài kiểm tra đã phát hành", body.Message);
    }

    [Fact]
    public async Task The_background_task_closes_quizzes_past_their_close_time_once_and_writes_a_system_audit_row()
    {
        var s = await _k.SetupAsync(2);
        var overdue = await _k.PublishedQuizAsync(s, "Quá hạn");
        var stillOpen = await _k.PublishedQuizAsync(s, "Còn hạn");
        var draft = await _k.ReadyQuizAsync(s, "Nháp quá hạn");
        var attempt = await _k.AddAttemptAsync(s, overdue.Id);
        await _k.SetQuizTimesAsync(overdue.Id, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddMinutes(-1));
        await _k.SetQuizTimesAsync(draft.Id, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddMinutes(-1));

        AutoCloseResultDto result;
        using (var scope = factory.Services.CreateScope())
            result = await scope.ServiceProvider.GetRequiredService<QuizAutoCloser>().CloseDueAsync(CancellationToken.None);

        Assert.True(result.ClosedQuizzes >= 1 && result.ExpiredAttempts >= 1);
        Assert.Equal(QuizStatus.CLOSED, (await _k.GetAsync(s.Token, overdue.Id)).Status);
        Assert.Equal(QuizStatus.PUBLISHED, (await _k.GetAsync(s.Token, stillOpen.Id)).Status);
        Assert.Equal(QuizStatus.DRAFT, (await _k.GetAsync(s.Token, draft.Id)).Status);   // bài nháp không bị đóng
        Assert.Equal(AttemptStatus.EXPIRED, await factory.WithDbAsync(db => db.Attempts.Where(a => a.Id == attempt).Select(a => a.Status!.Value).SingleAsync()));

        var log = Assert.Single(await AuditOfAsync(overdue.Id, AuditAction.QUIZ_CLOSED));
        Assert.Equal((null, ActorRole.SYSTEM, null), (log.UserId, log.ActorRole, log.IpAddress));
        Assert.Contains("\"byActor\":\"SYSTEM\"", log.NewValue);

        // Chạy lại không đóng thêm và không ghi thêm nhật ký.
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<QuizAutoCloser>().CloseDueAsync(CancellationToken.None);
        Assert.Single(await AuditOfAsync(overdue.Id, AuditAction.QUIZ_CLOSED));
    }

    [Fact]
    public async Task A_lesson_with_an_open_quiz_cannot_be_archived_until_the_quiz_is_closed()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.PublishedQuizAsync(s);

        var (res, body) = await Http.PostAsync<object>($"/api/v1/teacher/lessons/{s.LessonId}/archive", null, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");

        await _k.CloseAsync(s.Token, quiz.Id);
        var archived = await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"/api/v1/teacher/lessons/{s.LessonId}/archive", null, s.Token));
        Assert.Equal(ContentStatus.ARCHIVED, archived.Status);
        Assert.Equal(1, archived.QuizCount);
    }

    // ---- Nhân bản bài kiểm tra ----

    [Fact]
    public async Task Duplicating_a_published_quiz_makes_a_draft_with_the_same_questions_points_and_settings_but_no_times()
    {
        var s = await _k.SetupAsync(2);
        var source = await _k.PublishedQuizAsync(s, "Bài gốc", questions: 3);
        await _k.AddAttemptAsync(s, source.Id, AttemptStatus.SUBMITTED);

        var result = await DiscoveryTestKit.Expect(Http.PostAsync<DuplicateQuizResultDto>($"{Q}/{source.Id}/duplicate", new { }, s.Token), HttpStatusCode.Created);
        var copy = result.Quiz;

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(("Bài gốc (bản sao)", QuizStatus.DRAFT, s.LessonId), (copy.Name, copy.Status, copy.LessonId));
        Assert.Equal((null, null, null), (copy.OpenAt, copy.CloseAt, copy.PublishedAt));
        Assert.Equal((source.DifficultyLevel, source.TimeLimitMinutes, source.ShuffleQuestions, source.ShowAnswersAfterSubmit),
            (copy.DifficultyLevel, copy.TimeLimitMinutes, copy.ShuffleQuestions, copy.ShowAnswersAfterSubmit));
        Assert.Equal(source.Questions.Select(x => (x.QuestionId, x.SortOrder, x.Point)), copy.Questions.Select(x => (x.QuestionId, x.SortOrder, x.Point)));
        Assert.Equal((6m, 0, 0), (copy.TotalPoint, copy.SubmittedCount, result.SkippedQuestions));   // không chép lượt làm bài
        Assert.Equal("Đã tạo bài kiểm tra nháp \"Bài gốc (bản sao)\".", result.Message);
        Assert.True(copy.Access.CanEditQuestions);

        // Bài gốc không đổi.
        var original = await _k.GetAsync(s.Token, source.Id);
        Assert.Equal((QuizStatus.PUBLISHED, source.OpenAt, 1), (original.Status, original.OpenAt, original.SubmittedCount));
    }

    [Fact]
    public async Task Duplicating_drops_hidden_questions_reports_how_many_and_accepts_a_name()
    {
        var s = await _k.SetupAsync(2);
        var source = await _k.PublishedQuizAsync(s, "Bài gốc", questions: 4);
        await _k.SetQuestionReviewAsync(source.Questions[1].QuestionId, ReviewStatus.REJECTED);
        await _k.SetQuestionReviewAsync(source.Questions[3].QuestionId, ReviewStatus.REJECTED);
        await _k.CloseAsync(s.Token, source.Id);   // bài đã đóng vẫn nhân bản được

        var result = await DiscoveryTestKit.Expect(Http.PostAsync<DuplicateQuizResultDto>($"{Q}/{source.Id}/duplicate", new { name = "  Làm lại tuần sau  " }, s.Token), HttpStatusCode.Created);

        Assert.Equal(2, result.SkippedQuestions);
        Assert.Equal("Làm lại tuần sau", result.Quiz.Name);
        Assert.Equal("Đã tạo bài kiểm tra nháp \"Làm lại tuần sau\". Bỏ qua 2 câu hỏi đã bị ẩn.", result.Message);
        Assert.Equal(new[] { (source.Questions[0].QuestionId, 1), (source.Questions[2].QuestionId, 2) }, result.Quiz.Questions.Select(x => (x.QuestionId, x.SortOrder)));
        Assert.Equal(4m, result.Quiz.TotalPoint);

        var (res, body) = await Http.PostAsync<object>($"{Q}/{source.Id}/duplicate", new { name = new string('x', 256) }, s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
    }

    [Fact]
    public async Task Duplicating_a_quiz_with_a_very_long_name_keeps_the_copy_name_within_the_limit()
    {
        var s = await _k.SetupAsync(2);
        var source = await _k.CreateAsync(s, new string('A', 255));
        var copy = (await DiscoveryTestKit.Expect(Http.PostAsync<DuplicateQuizResultDto>($"{Q}/{source.Id}/duplicate", new { }, s.Token), HttpStatusCode.Created)).Quiz;
        Assert.Equal(255, copy.Name!.Length);
        Assert.EndsWith(QuizRules.CopySuffix, copy.Name);
    }

    [Fact]
    public async Task A_student_sees_the_published_quiz_on_the_lesson_page_with_its_state()
    {
        var s = await _k.SetupAsync(2);
        await _k.ReadyQuizAsync(s, "Còn nháp");
        await _k.PublishedQuizAsync(s, "Sắp mở");
        var open = await _k.PublishedQuizAsync(s, "Đang mở");
        var closed = await _k.PublishedQuizAsync(s, "Đã đóng");
        await _k.SetQuizTimesAsync(open.Id, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddHours(1));
        await _k.CloseAsync(s.Token, closed.Id);
        var student = await _k.Questions.Lesson.StudentTokenAsync(s.Token, s.ClassId);

        var (res, body) = await _k.Questions.Lesson.OpenAsync(student, s.AccessCode!);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var quizzes = body.Data!.Quizzes.ToDictionary(x => x.Name!);
        Assert.False(quizzes.ContainsKey("Còn nháp"));   // bài nháp không hiện cho học sinh
        Assert.Equal(StudentQuizState.NOT_OPEN, quizzes["Sắp mở"].State);
        Assert.Equal((StudentQuizState.OPEN, 3, 30), (quizzes["Đang mở"].State, quizzes["Đang mở"].QuestionCount, quizzes["Đang mở"].TimeLimitMinutes));
        Assert.Equal(StudentQuizState.CLOSED, quizzes["Đã đóng"].State);
    }
}
