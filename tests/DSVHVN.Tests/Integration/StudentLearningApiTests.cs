using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Trang học, lịch sử bài làm và tiến độ của em: quiz còn mở em chưa làm (nhãn "Cần mã bài học"), lượt đang làm dở, bài học của em suy từ lượt làm bài,
/// lịch sử theo cấp (cấp 1 sao thay điểm), tiến độ quiz và bài học, mức thành thạo theo di sản và huy hiệu tính khi hiển thị.
/// </summary>
public sealed class StudentLearningApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly AttemptTestKit _k = new(factory);

    /// <summary>Một học sinh, hai quiz đã phát hành của cùng một bài học (mỗi quiz 4 câu × 2 điểm).</summary>
    private async Task<(AttemptTestKit.Quiz First, AttemptTestKit.Quiz Second)> TwoQuizzesAsync(int level = 2)
    {
        var first = await _k.OpenQuizAsync(level);
        var second = await _k.OpenQuizAsync(level, setup: first.Setup);
        return (first, second);
    }

    // ---- Trang học ----

    [Fact]
    public async Task The_home_page_lists_open_quizzes_with_the_lesson_code_label_until_the_student_has_an_attempt_in_that_lesson()
    {
        var (a, b) = await TwoQuizzesAsync();
        var home = await _k.HomeAsync(a.StudentToken);

        Assert.Equal((a.StudentCode, "Lớp thử", 2, "Cấp 2", "FREE_STEPS"), (home.StudentCode, home.ClassName, home.Level, home.LevelName, home.Layout));
        Assert.Equal(a.Setup.School.Teacher.FullName, home.TeacherName);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), home.OpenQuizzes.Select(x => x.QuizId).Order());
        Assert.All(home.OpenQuizzes, x => Assert.Equal((true, StudentLearningService.NeedsLessonCodeLabel, 4), (x.NeedsLessonCode, x.Label, x.QuestionCount)));
        Assert.Empty(home.InProgress);
        Assert.Empty(home.MyLessons);
        Assert.Empty(home.RecentBadges);
        Assert.Null(home.TotalStars);

        // Bắt đầu quiz đầu tiên: hiện ở "đang làm", quiz còn lại không còn cần mã vì em đã có lượt ở bài này.
        var attempt = await _k.StartAsync(a.StudentToken, a.Id);
        var during = await _k.HomeAsync(a.StudentToken);
        var running = Assert.Single(during.InProgress);
        Assert.Equal((attempt.Id, a.Id, a.Setup.LessonId), (running.AttemptId, running.QuizId, running.LessonId));
        Assert.NotNull(running.Deadline);
        var rest = Assert.Single(during.OpenQuizzes);
        Assert.Equal((b.Id, false, (string?)null), (rest.QuizId, rest.NeedsLessonCode, rest.Label));

        // Nộp xong: bài học của em hiện kèm mã để mở lại, huy hiệu gần đây có.
        await _k.SubmitAsync(a.StudentToken, attempt.Id);
        var after = await _k.HomeAsync(a.StudentToken);
        Assert.Empty(after.InProgress);
        var lesson = Assert.Single(after.MyLessons);
        Assert.Equal((a.Setup.LessonId, a.Setup.AccessCode, 1, 2), (lesson.LessonId, lesson.AccessCode, lesson.QuizzesDone, lesson.QuizzesPublished));
        Assert.Equal(new[] { BadgeCode.ON_TIME }, after.RecentBadges.Select(x => x.Code).Where(c => c == BadgeCode.ON_TIME));
        Assert.Equal(b.Id, Assert.Single(after.OpenQuizzes).QuizId);
    }

    [Fact]
    public async Task The_home_page_hides_quizzes_that_are_closed_not_yet_open_draft_or_of_another_class_and_never_leaks_other_students()
    {
        var (a, b) = await TwoQuizzesAsync();
        await _k.Quizzes.SetQuizTimesAsync(b.Id, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(5));   // chưa tới giờ mở
        await _k.Quizzes.ReadyQuizAsync(a.Setup, "Còn nháp");                                                  // nháp
        var stranger = await _k.OpenQuizAsync(2);                                                              // lớp khác

        var home = await _k.HomeAsync(a.StudentToken);
        Assert.Equal([a.Id], home.OpenQuizzes.Select(x => x.QuizId));

        await _k.Quizzes.SetQuizTimesAsync(a.Id, DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddMinutes(-5));
        Assert.Empty((await _k.HomeAsync(a.StudentToken)).OpenQuizzes);

        var strangerHome = await _k.HomeAsync(stranger.StudentToken);
        Assert.Equal([stranger.Id], strangerHome.OpenQuizzes.Select(x => x.QuizId));
    }

    [Fact]
    public async Task The_home_page_of_a_level_one_class_counts_stars_and_hides_the_time_limit()
    {
        var q = await _k.OpenQuizAsync(1);
        var result = await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);
        var home = await _k.HomeAsync(q.StudentToken);
        Assert.Equal((1, "STEP_BY_STEP", (int?)3), (home.Level, home.Layout, home.TotalStars));
        Assert.Equal(result.AttemptId, Assert.Single(home.RecentBadges.Where(b => b.Code == BadgeCode.PERFECT_SCORE)).AttemptId);

        var next = await _k.OpenQuizAsync(1, setup: q.Setup);
        var again = await _k.HomeAsync(q.StudentToken);
        var open = Assert.Single(again.OpenQuizzes);
        Assert.Equal((next.Id, (int?)null), (open.QuizId, open.TimeLimitMinutes));
    }

    // ---- Lịch sử ----

    [Fact]
    public async Task The_history_lists_the_submitted_attempts_newest_first_with_score_and_badges_and_never_the_ones_in_progress()
    {
        var (a, b) = await TwoQuizzesAsync();
        var third = await _k.OpenQuizAsync(2, setup: a.Setup);
        var good = await _k.AnswerAllAndSubmitAsync(a, AttemptTestKit.Correct);
        var bad = await _k.AnswerAllAndSubmitAsync(b, AttemptTestKit.Wrong, a.StudentToken);
        await _k.StartAsync(a.StudentToken, third.Id);   // đang làm: không vào lịch sử

        var history = await _k.HistoryAsync(a.StudentToken);
        Assert.Equal(2, history.TotalItems);
        Assert.Equal([bad.AttemptId, good.AttemptId], history.Items.Select(x => x.AttemptId));
        var newest = history.Items[0];
        Assert.Equal((b.Id, AttemptStatus.SUBMITTED, "Đã nộp", (decimal?)0m, (decimal?)8m, (decimal?)0m, (int?)null), (newest.QuizId, newest.Status, newest.StatusLabel, newest.Score, newest.TotalPoint, newest.Percent, newest.Stars));
        Assert.Equal([BadgeCode.ON_TIME], newest.Badges.Select(x => x.Code));
        var oldest = history.Items[1];
        Assert.Equal((100m, a.Setup.LessonId), (oldest.Percent, oldest.LessonId));
        Assert.Equal([BadgeCode.PERFECT_SCORE, BadgeCode.ON_TIME], oldest.Badges.Select(x => x.Code));

        var paged = await _k.HistoryAsync(a.StudentToken, "?page=2&pageSize=1");
        Assert.Equal((2, 1, good.AttemptId), (paged.TotalItems, paged.Items.Count, paged.Items[0].AttemptId));
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Http.GetAsync<object>($"{AttemptTestKit.Student}/attempts?pageSize=500", a.StudentToken)).Response.StatusCode);
    }

    [Fact]
    public async Task The_history_of_a_level_one_class_shows_stars_instead_of_the_score()
    {
        var q = await _k.OpenQuizAsync(1);
        await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);
        var row = Assert.Single((await _k.HistoryAsync(q.StudentToken)).Items);
        Assert.Equal(((int?)3, (decimal?)null, (decimal?)null, (decimal?)null), (row.Stars, row.Score, row.TotalPoint, row.Percent));
    }

    [Fact]
    public async Task An_attempt_ended_by_the_deadline_shows_in_the_history_as_auto_submitted()
    {
        var q = await _k.OpenQuizAsync(3, types: [QuestionType.SINGLE_CHOICE]);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        await _k.SetAttemptStartAsync(attempt.Id, DateTime.UtcNow.AddMinutes(-45));
        await _k.GetRawAsync(q.StudentToken, attempt.Id);   // mở lại: hệ thống chốt

        var row = Assert.Single((await _k.HistoryAsync(q.StudentToken)).Items);
        Assert.Equal((AttemptStatus.EXPIRED, "Tự nộp khi hết giờ", 0m), (row.Status, row.StatusLabel, row.Score));
        Assert.DoesNotContain(row.Badges, b => b.Code == BadgeCode.ON_TIME);
    }

    // ---- Tiến độ ----

    [Fact]
    public async Task Progress_counts_submitted_quizzes_completed_lessons_mastery_by_heritage_and_earned_badges()
    {
        var (a, b) = await TwoQuizzesAsync();
        var lesson2 = await _k.Quizzes.AnotherLessonAsync(a.Setup, "Bài học khác");
        var other = await _k.OpenQuizAsync(2, setup: a.Setup);   // quiz thứ ba, cùng bài học đầu
        var empty = await _k.Quizzes.CreateAsync(a.Setup, "Quiz bài khác", lessonId: lesson2.Id);   // nháp: không tính

        var before = await _k.ProgressAsync(a.StudentToken);
        Assert.Equal((0, 3, 0m, 0, 1, 0), (before.QuizzesSubmitted, before.QuizzesPublished, before.ProgressPercent, before.LessonsCompleted, before.LessonsWithQuiz, before.FinishedAttempts));
        Assert.Empty(before.Mastery);
        Assert.Empty(before.Badges);

        await _k.AnswerAllAndSubmitAsync(a, AttemptTestKit.Correct);
        await _k.AnswerAllAndSubmitAsync(b, AttemptTestKit.Wrong, a.StudentToken);
        var mid = await _k.ProgressAsync(a.StudentToken);
        Assert.Equal((2, 3, 66.7m, 0, 1, 2), (mid.QuizzesSubmitted, mid.QuizzesPublished, mid.ProgressPercent, mid.LessonsCompleted, mid.LessonsWithQuiz, mid.FinishedAttempts));   // chưa nộp quiz thứ ba

        await _k.AnswerAllAndSubmitAsync(other, AttemptTestKit.Correct, a.StudentToken);
        var done = await _k.ProgressAsync(a.StudentToken);
        Assert.Equal((3, 3, 100m, 1, 1, 3), (done.QuizzesSubmitted, done.QuizzesPublished, done.ProgressPercent, done.LessonsCompleted, done.LessonsWithQuiz, done.FinishedAttempts));

        var heritage = Assert.Single(done.Mastery);
        Assert.Equal((a.Setup.Heritage.Id, 12, 8, 66.7m, MasteryLevel.FAIR, "Đạt", 2), (heritage.HeritageId, heritage.Answered, heritage.Correct, heritage.Percent, heritage.Level, heritage.Label, heritage.Stars));
        Assert.Equal(a.Setup.Heritage.Name, heritage.Name);
        Assert.Null(done.TotalStars);

        var badges = done.Badges.ToDictionary(x => x.Code);
        Assert.Equal((2, 3), (badges[BadgeCode.PERFECT_SCORE].Count, badges[BadgeCode.ON_TIME].Count));
        Assert.Equal(["Điểm tuyệt đối", "Nộp đúng hạn"], done.Badges.Select(x => x.Name));
        Assert.NotNull(badges[BadgeCode.ON_TIME].LastAttemptId);
        Assert.False(badges.ContainsKey(BadgeCode.PERSEVERANCE));
    }

    [Fact]
    public async Task Progress_of_a_level_one_class_shows_a_star_bar_and_the_total_stars_instead_of_percentages()
    {
        var q = await _k.OpenQuizAsync(1);
        await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);
        var progress = await _k.ProgressAsync(q.StudentToken);

        Assert.Equal((1, (int?)3, 1, 1), (progress.Level, progress.TotalStars, progress.QuizzesSubmitted, progress.LessonsCompleted));
        var heritage = Assert.Single(progress.Mastery);
        Assert.Equal((4, 4, (decimal?)null, MasteryLevel.MASTERED, 3), (heritage.Answered, heritage.Correct, heritage.Percent, heritage.Level, heritage.Stars));
    }

    [Fact]
    public async Task Mastery_needs_at_least_three_answers_and_counts_unanswered_questions()
    {
        var q = await _k.OpenQuizAsync(2, types: [QuestionType.SINGLE_CHOICE, QuestionType.TRUE_FALSE]);
        await _k.AnswerAllAndSubmitAsync(q, (shown, detail) => shown.QuestionType == QuestionType.TRUE_FALSE ? null : AttemptTestKit.Correct(shown, detail));
        var two = Assert.Single((await _k.ProgressAsync(q.StudentToken)).Mastery);
        Assert.Equal((2, 1, MasteryLevel.INSUFFICIENT_DATA, "Chưa đủ dữ liệu", 0), (two.Answered, two.Correct, two.Level, two.Label, two.Stars));

        var next = await _k.OpenQuizAsync(2, types: [QuestionType.SINGLE_CHOICE, QuestionType.TRUE_FALSE], setup: q.Setup);
        await _k.AnswerAllAndSubmitAsync(next, (_, _) => null, q.StudentToken);   // bỏ trống cả hai câu vẫn tính là đã trả lời
        var four = Assert.Single((await _k.ProgressAsync(q.StudentToken)).Mastery);
        Assert.Equal((4, 1, 25m, MasteryLevel.NEEDS_REVIEW), (four.Answered, four.Correct, four.Percent, four.Level));
    }

    [Fact]
    public async Task The_perseverance_badge_comes_with_the_fifth_finished_attempt()
    {
        var first = await _k.OpenQuizAsync(2, types: [QuestionType.SINGLE_CHOICE]);
        var quizzes = new List<AttemptTestKit.Quiz> { first };
        for (var i = 0; i < 4; i++) quizzes.Add(await _k.OpenQuizAsync(2, types: [QuestionType.SINGLE_CHOICE], setup: first.Setup));

        for (var i = 0; i < 4; i++) await _k.AnswerAllAndSubmitAsync(quizzes[i], AttemptTestKit.Wrong, first.StudentToken);
        Assert.DoesNotContain(BadgeCode.PERSEVERANCE, (await _k.ProgressAsync(first.StudentToken)).Badges.Select(x => x.Code));

        var fifth = await _k.AnswerAllAndSubmitAsync(quizzes[4], AttemptTestKit.Wrong, first.StudentToken);
        Assert.Contains(BadgeCode.PERSEVERANCE, fifth.Badges.Select(x => x.Code));
        var progress = await _k.ProgressAsync(first.StudentToken);
        var kien = progress.Badges.Single(x => x.Code == BadgeCode.PERSEVERANCE);
        Assert.Equal((1, "Kiên trì", fifth.AttemptId), (kien.Count, kien.Name, kien.LastAttemptId));
    }

    [Fact]
    public async Task A_student_only_sees_their_own_attempts_progress_and_history()
    {
        var q = await _k.OpenQuizAsync(2);
        var classmate = await _k.AnotherStudentAsync(q);
        await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);

        Assert.Empty((await _k.HistoryAsync(classmate)).Items);
        var progress = await _k.ProgressAsync(classmate);
        Assert.Equal((0, 1, 0), (progress.QuizzesSubmitted, progress.QuizzesPublished, progress.FinishedAttempts));
        Assert.Empty(progress.Mastery);
        Assert.Empty((await _k.HomeAsync(classmate)).MyLessons);
        Assert.Equal(q.Id, Assert.Single((await _k.HomeAsync(classmate)).OpenQuizzes).QuizId);
    }
}
