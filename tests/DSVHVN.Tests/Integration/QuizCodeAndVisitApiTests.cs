using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Quizzes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Mã bài kiểm tra và bài học của em: phát hành lần đầu sinh mã KT, gõ mã đó vào thẳng bài kiểm tra của đúng lớp; bài em đã mở bằng mã nằm trong
/// "Bài học của em" kể cả khi chưa có bài kiểm tra; lớp cấp 1 phát hành không cần giờ mở và đóng.
/// </summary>
public sealed class QuizCodeAndVisitApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly AttemptTestKit _k = new(factory);
    private LessonTestKit Lessons => _k.Quizzes.Questions.Lesson;

    [Fact]
    public async Task Publishing_gives_the_quiz_its_own_code_and_a_copy_does_not_keep_it()
    {
        var s = await _k.Quizzes.SetupAsync(2);
        var ready = await _k.Quizzes.ReadyQuizAsync(s);
        Assert.Null(ready.AccessCode);

        var result = await _k.Quizzes.PublishAsync(s.Token, ready.Id);

        Assert.True(QuizCodes.IsWellFormed(result.Quiz.AccessCode));
        Assert.Contains(result.Quiz.AccessCode!, result.Message);
        Assert.Contains(s.AccessCode!, result.Message);

        var (_, copy) = await _k.Http.PostAsync<DuplicateQuizResultDto>($"{QuizTestKit.Quizzes}/{ready.Id}/duplicate", new { }, s.Token);
        Assert.Null(copy.Data!.Quiz.AccessCode);
    }

    [Fact]
    public async Task A_quiz_code_opens_the_lesson_and_points_at_the_quiz_for_a_student_of_the_class()
    {
        var quiz = await _k.OpenQuizAsync(2);
        var code = (await _k.Quizzes.GetAsync(quiz.TeacherToken, quiz.Id)).AccessCode!;

        var (response, body) = await Lessons.OpenAsync(quiz.StudentToken, code.ToLowerInvariant());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((quiz.Setup.LessonId, quiz.Setup.AccessCode, quiz.Id), (body.Data!.Id, body.Data.AccessCode, body.Data.FocusQuizId));
    }

    [Fact]
    public async Task The_lesson_code_does_not_point_at_any_quiz()
    {
        var quiz = await _k.OpenQuizAsync(2);

        var (_, body) = await Lessons.OpenAsync(quiz.StudentToken, quiz.Setup.AccessCode!);

        Assert.Null(body.Data!.FocusQuizId);
    }

    [Fact]
    public async Task A_quiz_code_of_another_class_is_refused_like_a_wrong_code()
    {
        var mine = await _k.OpenQuizAsync(2);
        var other = await _k.OpenQuizAsync(2);
        var code = (await _k.Quizzes.GetAsync(other.TeacherToken, other.Id)).AccessCode!;

        var (response, body) = await Lessons.OpenAsync(mine.StudentToken, code);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LESSON_CODE_INVALID", body.ErrorCode);
    }

    [Fact]
    public async Task A_quiz_code_cannot_stand_in_for_the_lesson_code_when_finishing_a_lesson()
    {
        var quiz = await _k.OpenQuizAsync(1);
        var code = (await _k.Quizzes.GetAsync(quiz.TeacherToken, quiz.Id)).AccessCode!;

        var (response, _) = await _k.Http.PostAsync<object>($"{LessonTestKit.Student}/{code}/complete", null, quiz.StudentToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_lesson_opened_by_code_stays_in_my_lessons_even_without_a_quiz(int level)
    {
        var s = await _k.Quizzes.SetupAsync(level);
        var student = await Lessons.StudentTokenAsync(s.Token, s.ClassId);
        Assert.Empty((await _k.HomeAsync(student)).MyLessons);

        await Lessons.OpenAsync(student, s.AccessCode!);
        await Lessons.OpenAsync(student, s.AccessCode!);

        var lesson = Assert.Single((await _k.HomeAsync(student)).MyLessons);
        Assert.Equal((s.LessonId, s.AccessCode, 0, 0), (lesson.LessonId, lesson.AccessCode, lesson.QuizzesDone, lesson.QuizzesPublished));
        Assert.NotNull(lesson.LastAttemptAt);
        Assert.Equal(1, await _k.Factory.WithDbAsync(db => db.LessonVisits.CountAsync(v => v.LessonId == s.LessonId)));
    }

    [Fact]
    public async Task An_opened_lesson_that_was_archived_leaves_my_lessons()
    {
        var s = await _k.Quizzes.SetupAsync(2);
        var student = await Lessons.StudentTokenAsync(s.Token, s.ClassId);
        await Lessons.OpenAsync(student, s.AccessCode!);

        await _k.Factory.WithDbAsync(async db =>
        {
            var lesson = await db.Lessons.SingleAsync(l => l.Id == s.LessonId);
            lesson.Status = ContentStatus.ARCHIVED;
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Empty((await _k.HomeAsync(student)).MyLessons);
    }

    [Fact]
    public async Task Opening_the_lesson_by_code_removes_the_code_label_from_its_open_quizzes()
    {
        var quiz = await _k.OpenQuizAsync(2);
        Assert.True(Assert.Single((await _k.HomeAsync(quiz.StudentToken)).OpenQuizzes).NeedsLessonCode);

        await Lessons.OpenAsync(quiz.StudentToken, quiz.Setup.AccessCode!);

        var open = Assert.Single((await _k.HomeAsync(quiz.StudentToken)).OpenQuizzes);
        Assert.Equal((false, (string?)null), (open.NeedsLessonCode, open.Label));
    }

    [Fact]
    public async Task A_primary_school_quiz_is_published_without_open_and_close_times_and_opens_at_once()
    {
        var s = await _k.Quizzes.SetupAsync(1);
        var student = await Lessons.StudentTokenAsync(s.Token, s.ClassId);
        var quiz = await _k.Quizzes.CreateAsync(s, "Bài chơi");
        Assert.DoesNotContain("giờ mở", quiz.Readiness.Missing);
        Assert.DoesNotContain("giờ đóng", quiz.Readiness.Missing);
        var questions = await _k.Quizzes.QuestionsAsync(s, 2);
        var ready = await _k.Quizzes.AddQuestionsAsync(s.Token, quiz.Id, questions.Select(q => q.Id));
        Assert.True(ready.Readiness.IsReady);
        Assert.Contains("ngay sau khi phát hành", ready.PublishNotice);

        var result = await _k.Quizzes.PublishAsync(s.Token, quiz.Id);

        Assert.Equal((QuizOpenState.OPEN, (DateTime?)null, (DateTime?)null), (result.Quiz.OpenState, result.Quiz.OpenAt, result.Quiz.CloseAt));
        Assert.Contains("tới khi thầy cô đóng bài", result.Message);
        Assert.Equal(StudentQuizAction.START, (await _k.IntroAsync(student, quiz.Id)).Action);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Other_levels_still_need_both_times_before_publishing(int level)
    {
        var s = await _k.Quizzes.SetupAsync(level);

        var quiz = await _k.Quizzes.CreateAsync(s, "Bài kiểm tra");

        Assert.Contains("giờ mở", quiz.Readiness.Missing);
        Assert.Contains("giờ đóng", quiz.Readiness.Missing);
    }
}
