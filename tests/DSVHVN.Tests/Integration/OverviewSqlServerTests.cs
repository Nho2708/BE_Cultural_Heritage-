using System.Diagnostics;
using DSVHVN.Application.Overview;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Tổng quan và kiểm duyệt trên SQL Server LocalDB (database tạm tạo bằng migration, tự xóa khi xong): mọi truy vấn dịch được sang SQL và cho cùng số liệu như bản SQLite
/// (đếm theo trường chưa ngừng, trung bình theo lớp, câu cần xem lại, sắp xếp theo tỉ lệ đúng, gom theo ngày Việt Nam), và một trường 45 học sinh × 20 bài kiểm tra
/// × 20 câu trả lời được phục vụ trong 3 giây ở mỗi màn.
/// </summary>
public sealed class OverviewSqlServerTests(SqlServerApiFactory factory, ITestOutputHelper output) : IClassFixture<SqlServerApiFactory>
{
    private readonly StatsTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string A = QuestionTestKit.Admin;

    [LocalDbFact]
    public async Task Every_overview_and_moderation_endpoint_runs_on_sql_server_with_the_same_numbers()
    {
        _ = factory.Server;
        var admin = await _k.Quizzes.Questions.Lesson.Discovery.Api.AdminTokenAsync();
        var before = await DiscoveryTestKit.Expect(Http.GetAsync<SystemOverviewDto>("/api/v1/admin/overview", admin));

        var quiz = await _k.Attempts.OpenQuizAsync(2, [QuestionType.SINGLE_CHOICE, QuestionType.TRUE_FALSE, QuestionType.ORDERING]);
        await _k.Quizzes.Questions.Lesson.Classroom.ImportAsync(quiz.TeacherToken, quiz.Setup.ClassId, Enumerable.Range(1, 10).Select(i => $"Em {i}"));
        var ids = await factory.WithDbAsync(db => db.Students.Where(s => s.ClassId == quiz.Setup.ClassId && s.FullName!.StartsWith("Em ")).OrderBy(s => s.Id).Select(s => s.Id).ToListAsync());
        for (var j = 0; j < ids.Count; j++)
        {
            var student = j;
            await _k.SeedAttemptAsync(quiz.Id, ids[j], i => i == 0 ? student == 0 : i == 1 || student % 2 == 0,
                submittedAt: new DateTime(2026, 3, 10, 16, 59, 59, DateTimeKind.Utc).AddSeconds(student % 2 * 1));
        }

        // Tổng quan trường.
        var school = await DiscoveryTestKit.Expect(Http.GetAsync<SchoolOverviewDto>("/api/v1/org/overview", quiz.Setup.School.OrgAdminToken));
        Assert.Equal((1, 11, 10), (school.ClassCount, school.Students.Active, school.Attempts.Finished));
        Assert.Equal(53.33m, school.Attempts.AveragePercent);                     // trung bình của 100%, bốn lượt 66,67% và năm lượt 33,33%
        Assert.Equal((1, 1), (school.Quizzes.Published, school.Quizzes.Open));
        Assert.Equal(PlanNoticeKind.NONE, school.PlanNotice.Kind);

        // Tổng quan hệ thống và biểu đồ.
        var after = await DiscoveryTestKit.Expect(Http.GetAsync<SystemOverviewDto>("/api/v1/admin/overview", admin));
        Assert.Equal((1, 1, 0), (after.Schools.Total - before.Schools.Total, after.Schools.WithEffectivePlan - before.Schools.WithEffectivePlan, after.Schools.WithoutEffectivePlan - before.Schools.WithoutEffectivePlan));
        Assert.Equal((11, 10), (after.ActiveStudents - before.ActiveStudents, after.Content.FinishedAttempts - before.Content.FinishedAttempts));
        Assert.False(after.Revenue.Available);
        var trend = await DiscoveryTestKit.Expect(Http.GetAsync<SystemTrendDto>("/api/v1/admin/overview/trend?from=2026-03-09&to=2026-03-12", admin));
        Assert.Equal([0, 5, 5, 0], trend.Points.Select(p => p.AttemptsFinished));   // 16:59:59Z còn ngày 10/03, 17:00:00Z sang 11/03 giờ Việt Nam
        var monthly = await DiscoveryTestKit.Expect(Http.GetAsync<SystemTrendDto>("/api/v1/admin/overview/trend?from=2026-01-01&to=2026-05-31", admin));
        Assert.Equal(10, monthly.Points.Single(p => p.Period == "2026-03").AttemptsFinished);

        // Kiểm duyệt: cờ, lọc, sắp xếp, ẩn, hiện lại.
        var heritage = quiz.Setup.Heritage.Id;
        var list = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionQualityPageDto>($"{A}/quality?heritageId={heritage}&sort=LOWEST_RATE&pageSize=100", admin));
        Assert.Equal([quiz.Questions[0].Id, quiz.Questions[2].Id, quiz.Questions[1].Id], list.Items.Select(i => i.Question.Id));
        Assert.Equal([(10, 1, true), (10, 5, false), (10, 10, false)], list.Items.Select(i => (i.Answered, i.Correct, i.LowQuality)));
        var flagged = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionQualityPageDto>($"{A}/quality?heritageId={heritage}&lowQualityOnly=true", admin));
        Assert.Equal([quiz.Questions[0].Id], flagged.Items.Select(i => i.Question.Id));
        Assert.True(flagged.LowQualityCount >= 1 && flagged.ApprovedCount >= 3);
        Assert.Equal([quiz.Questions[0].Id], (await DiscoveryTestKit.Expect(Http.GetAsync<QuestionQualityPageDto>(
            $"{A}/quality?organizationId={quiz.Setup.School.OrgId}&questionType=SINGLE_CHOICE", admin))).Items.Select(i => i.Question.Id));

        var weak = quiz.Questions[0].Id;
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{A}/{weak}/hide", new { reason = "Câu gây nhầm lẫn" }, admin));
        var hidden = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionQualityPageDto>($"{A}/quality?heritageId={heritage}&status=REJECTED", admin));
        Assert.Equal((weak, true, false), (hidden.Items.Single().Question.Id, hidden.Items.Single().Question.IsHidden, hidden.Items.Single().LowQuality));
        Assert.NotNull(hidden.Items.Single().HiddenAt);
        var restored = await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{A}/{weak}/restore", new { reason = "Đã sửa nội dung" }, admin));
        Assert.Equal(ReviewStatus.APPROVED, restored.ReviewStatus);
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.QUESTION_RESTORED && l.TargetId == weak)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.QUESTION_HIDDEN && l.TargetId == weak)));
    }

    [LocalDbFact]
    public async Task A_school_of_45_students_with_20_quizzes_of_20_questions_is_served_within_three_seconds_per_screen()
    {
        _ = factory.Server;
        const int studentCount = 45;
        const int quizCount = 20;
        const int questionCount = 20;

        var s = await _k.Quizzes.SetupAsync(2);
        var questions = await _k.Quizzes.QuestionsAsync(s, questionCount);
        var template = await _k.Quizzes.CreateAsync(s, "Bài mẫu", new { difficultyLevel = DifficultyLevel.MEDIUM, openAt = QuizTestKit.OpenSoon, closeAt = QuizTestKit.CloseLater });
        await _k.Quizzes.AddQuestionsAsync(s.Token, template.Id, questions.Select(q => q.Id), 1m);
        var published = (await _k.Quizzes.PublishAsync(s.Token, template.Id)).Quiz;
        await _k.Quizzes.Questions.Lesson.Classroom.ImportAsync(s.Token, s.ClassId, Enumerable.Range(1, studentCount).Select(i => $"Học sinh {i:00}"));

        var attempts = await factory.WithDbAsync(async db =>
        {
            var source = await db.Quizzes.AsNoTracking().SingleAsync(q => q.Id == published.Id);
            var items = await db.QuizQuestions.AsNoTracking().Where(x => x.QuizId == published.Id).OrderBy(x => x.SortOrder).ToListAsync();
            var students = await db.Students.AsNoTracking().Where(x => x.ClassId == s.ClassId).Select(x => x.Id).ToListAsync();
            var quizzes = new List<Quiz> { source };
            for (var i = 1; i < quizCount; i++)
            {
                var copy = new Quiz
                {
                    LessonId = source.LessonId, CreatedBy = source.CreatedBy, Name = $"Bài {i + 1}", DifficultyLevel = source.DifficultyLevel,
                    OpenAt = DateTime.UtcNow.AddDays(-30), CloseAt = DateTime.UtcNow.AddDays(30), TotalPoint = source.TotalPoint, Status = QuizStatus.PUBLISHED,
                    PublishedAt = DateTime.UtcNow.AddDays(-30),
                };
                foreach (var item in items) copy.Questions.Add(new QuizQuestion { QuestionId = item.QuestionId, Point = item.Point, SortOrder = item.SortOrder });
                db.Quizzes.Add(copy);
                quizzes.Add(copy);
            }
            await db.SaveChangesAsync();

            var random = new Random(11);
            foreach (var studentId in students)
                foreach (var quiz in quizzes)
                {
                    var at = DateTime.UtcNow.AddMinutes(-random.Next(60, 60 * 24 * 20));
                    var attempt = new Attempt
                    {
                        QuizId = quiz.Id, StudentId = studentId, StartedAt = at.AddMinutes(-10), SubmittedAt = at, DurationSeconds = 600, Status = AttemptStatus.SUBMITTED,
                        TotalPoint = questionCount, TotalQuestions = questionCount,
                    };
                    foreach (var item in items)
                    {
                        var ok = random.NextDouble() < 0.4;
                        attempt.Answers.Add(new AttemptAnswer
                        {
                            QuestionId = item.QuestionId, IsCorrect = ok, PartsTotal = 1, PartsCorrect = (short)(ok ? 1 : 0), PointEarned = ok ? 1m : 0m, AnsweredAt = at,
                        });
                    }
                    attempt.CorrectCount = attempt.Answers.Count(a => a.IsCorrect);
                    attempt.Score = attempt.CorrectCount;
                    db.Attempts.Add(attempt);
                }
            return await db.SaveChangesAsync();
        });
        output.WriteLine($"Đã ghi {attempts} dòng.");
        var admin = await _k.Quizzes.Questions.Lesson.Discovery.Api.AdminTokenAsync();

        var school = await Timed("Tổng quan trường", () => DiscoveryTestKit.Expect(Http.GetAsync<SchoolOverviewDto>("/api/v1/org/overview", s.School.OrgAdminToken)));
        Assert.Equal((studentCount * quizCount, studentCount + 0), (school.Attempts.Finished, school.Students.Active));
        var system = await Timed("Tổng quan hệ thống", () => DiscoveryTestKit.Expect(Http.GetAsync<SystemOverviewDto>("/api/v1/admin/overview", admin)));
        Assert.True(system.Content.FinishedAttempts >= studentCount * quizCount);
        var trend = await Timed("Biểu đồ theo thời gian", () => DiscoveryTestKit.Expect(Http.GetAsync<SystemTrendDto>("/api/v1/admin/overview/trend?granularity=DAY&from=" + DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd"), admin)));
        Assert.True(trend.Totals.AttemptsFinished >= studentCount * quizCount);
        var quality = await Timed("Kiểm duyệt (câu cần xem lại trước)", () => DiscoveryTestKit.Expect(Http.GetAsync<QuestionQualityPageDto>(
            $"{A}/quality?heritageId={s.Heritage.Id}&sort=LOWEST_RATE&pageSize=100", admin)));
        Assert.Equal(questionCount, quality.Items.Count(i => i.Answered == studentCount * quizCount));
        Assert.True(quality.LowQualityCount >= 1);
        await Timed("Kiểm duyệt (chỉ câu cần xem lại)", () => DiscoveryTestKit.Expect(Http.GetAsync<QuestionQualityPageDto>(
            $"{A}/quality?heritageId={s.Heritage.Id}&lowQualityOnly=true", admin)));
    }

    private async Task<T> Timed<T>(string name, Func<Task<T>> call)
    {
        var watch = Stopwatch.StartNew();
        var result = await call();
        watch.Stop();
        output.WriteLine($"{name}: {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"{name} mất {watch.ElapsedMilliseconds} ms, quá 3 giây.");
        return result;
    }
}
