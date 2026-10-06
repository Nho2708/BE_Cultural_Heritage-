using System.Diagnostics;
using System.Net;
using DSVHVN.Application.Statistics;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Thống kê và gợi ý trên SQL Server LocalDB (database tạm tạo bằng migration, tự xóa khi xong): mọi truy vấn của học sinh và giáo viên dịch được sang SQL và cho cùng
/// số liệu như bản SQLite, và một lớp 45 học sinh × 20 bài kiểm tra × 20 câu trả lời trong thời gian của yêu cầu phi chức năng (giáo viên 3 giây, gợi ý 2 giây,
/// tiến độ và lịch sử của một học sinh 1 giây).
/// </summary>
public sealed class StatisticsSqlServerTests(SqlServerApiFactory factory, ITestOutputHelper output) : IClassFixture<SqlServerApiFactory>
{
    private readonly StatsTestKit _k = new(factory);

    [LocalDbFact]
    public async Task Every_student_and_teacher_endpoint_runs_on_sql_server_with_the_same_numbers()
    {
        _ = factory.Server;
        var a = await _k.Quizzes.SetupAsync(2);
        var b = await _k.LessonOnNewHeritageAsync(a, "Ca trù", "79", HeritageType.INTANGIBLE);
        var qa = await _k.QuizAsync(a, "Em 1");
        var qb = await _k.QuizAsync(b, "Em 2");
        var token2 = await _k.StudentTokenAsync(a, "Em 3");

        var first = await _k.AllCorrectAsync(qa);
        var second = await _k.AllWrongAsync(qb, qa.StudentToken);
        await _k.AllWrongAsync(qa, token2);
        var em1 = await _k.StudentIdAsync(a.ClassId, "Em 1");

        // Học sinh.
        var home = await _k.RecommendationsAsync(qa.StudentToken);
        Assert.Equal([b.Heritage.Id], home.HeritagesToReview.Select(h => h.HeritageId));
        Assert.Equal([b.LessonId], home.LessonsToReview.Select(l => l.LessonId));
        Assert.Empty(home.NextQuizzes);
        var page = await _k.AttemptRecommendationsAsync(qa.StudentToken, second.AttemptId);
        Assert.Equal([b.Heritage.Id], page.HeritagesToReview.Select(h => h.HeritageId));
        Assert.Empty((await _k.AttemptRecommendationsAsync(qa.StudentToken, first.AttemptId)).HeritagesToReview);
        Assert.Null((await _k.RecommendationsAsync(token2)).Message);   // Em 3 đã có lượt nên không có lời mời

        // Giáo viên: kết quả một bài.
        var overview = await _k.OverviewAsync(a.Token, qa.Id);
        Assert.Equal((3, 2, 2, 0, 1), (overview.Enrolled, overview.Submitted, overview.Finished, overview.InProgress, overview.NotStarted));
        Assert.Equal((4m, 8m, 0m), (overview.AverageScore, overview.HighestScore, overview.LowestScore));
        Assert.Equal(4, (await _k.QuestionStatsAsync(a.Token, qa.Id)).Questions.Count);
        Assert.Equal(3, (await _k.QuizStudentsAsync(a.Token, qa.Id)).Students.Count);
        Assert.Single((await _k.QuizTopicsAsync(a.Token, qa.Id)).ByHeritage);
        Assert.Equal(4, (await _k.AttemptDetailAsync(a.Token, first.AttemptId)).Questions.Count);

        // Giáo viên: lớp.
        var progress = await _k.ProgressAsync(a.Token, a.ClassId);
        Assert.Equal((3, 2, 3), (progress.Enrolled, progress.QuizzesPublished, progress.SubmittedTotal));
        var mastery = await _k.MasteryAsync(a.Token, a.ClassId);
        Assert.Equal(2, mastery.ByHeritage.Count);
        Assert.Equal(2, mastery.ByProvince.Count);
        Assert.Equal(2, mastery.ByHeritageType.Count);
        Assert.Equal(4, mastery.ByQuestionType.Count);
        Assert.Equal([b.Heritage.Id], (await _k.MasteryAsync(a.Token, a.ClassId, "?heritageType=INTANGIBLE")).ByHeritage.Select(r => r.Id!.Value));
        var profile = await _k.ProfileAsync(a.Token, a.ClassId, em1);
        Assert.Equal((2, 50m), (profile.FinishedAttempts, profile.AveragePercent));
        Assert.Equal(2, profile.MasteryByProvince.Count);
        var recs = await _k.ClassRecommendationsAsync(a.Token, a.ClassId);
        Assert.Equal((3, 12), (recs.SubmittedAttempts, recs.AnsweredQuestions));
        Assert.Equal(HttpStatusCode.OK, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/quizzes/{qa.Id}/results/recommendations", a.Token));
    }

    [LocalDbFact]
    public async Task A_class_of_45_students_with_20_quizzes_of_20_questions_is_served_within_the_non_functional_limits()
    {
        _ = factory.Server;
        const int studentCount = 45;
        const int quizCount = 20;
        const int questionCount = 20;

        var s = await _k.Quizzes.SetupAsync(2);
        var questions = await _k.Quizzes.QuestionsAsync(s, questionCount);
        var template = await _k.Quizzes.CreateAsync(s, "Bài mẫu", new
        {
            difficultyLevel = DifficultyLevel.MEDIUM,
            openAt = QuizTestKit.OpenSoon,
            closeAt = QuizTestKit.CloseLater,
        });
        await _k.Quizzes.AddQuestionsAsync(s.Token, template.Id, questions.Select(q => q.Id), 1m);
        var published = (await _k.Quizzes.PublishAsync(s.Token, template.Id)).Quiz;
        await _k.Quizzes.SetQuizTimesAsync(published.Id, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(30));

        var names = Enumerable.Range(1, studentCount).Select(i => $"Học sinh {i:00}").ToList();
        await _k.Quizzes.Questions.Lesson.Classroom.ImportAsync(s.Token, s.ClassId, names);

        var seeded = await factory.WithDbAsync(async db =>
        {
            var template = await db.Quizzes.AsNoTracking().SingleAsync(q => q.Id == published.Id);
            var items = await db.QuizQuestions.AsNoTracking().Where(x => x.QuizId == published.Id).OrderBy(x => x.SortOrder).ToListAsync();
            var students = await db.Students.AsNoTracking().Where(x => x.ClassId == s.ClassId).Select(x => x.Id).ToListAsync();

            var quizzes = new List<Quiz> { template };
            for (var i = 1; i < quizCount; i++)
            {
                var copy = new Quiz
                {
                    LessonId = template.LessonId, CreatedBy = template.CreatedBy, Name = $"Bài {i + 1}", DifficultyLevel = template.DifficultyLevel,
                    OpenAt = DateTime.UtcNow.AddDays(-30), CloseAt = DateTime.UtcNow.AddDays(30), TotalPoint = template.TotalPoint, Status = QuizStatus.PUBLISHED,
                    PublishedAt = DateTime.UtcNow.AddDays(-30),
                };
                foreach (var item in items) copy.Questions.Add(new QuizQuestion { QuestionId = item.QuestionId, Point = item.Point, SortOrder = item.SortOrder });
                db.Quizzes.Add(copy);
                quizzes.Add(copy);
            }
            await db.SaveChangesAsync();

            var random = new Random(7);
            var count = 0;
            foreach (var studentId in students)
            {
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
                        var ok = random.NextDouble() < 0.65;
                        attempt.Answers.Add(new AttemptAnswer
                        {
                            QuestionId = item.QuestionId, IsCorrect = ok, PartsTotal = 1, PartsCorrect = (short)(ok ? 1 : 0), PointEarned = ok ? 1m : 0m, AnsweredAt = at,
                        });
                    }
                    attempt.CorrectCount = attempt.Answers.Count(a => a.IsCorrect);
                    attempt.Score = attempt.CorrectCount;
                    db.Attempts.Add(attempt);
                    count++;
                }
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
            return (quizzes.Select(q => q.Id).ToList(), count);
        });
        Assert.Equal(studentCount * quizCount, seeded.count);
        var watcher = await _k.StudentTokenAsync(s, "Em theo dõi");   // em thứ 46, chưa có lượt nào

        // Làm nóng một lần (biên dịch truy vấn), rồi đo.
        await _k.ProgressAsync(s.Token, s.ClassId);
        var quizId = seeded.Item1[3];

        var teacher = new (string Name, Func<Task> Call)[]
        {
            ("tiến độ lớp", async () => Assert.Equal(studentCount + 1, (await _k.ProgressAsync(s.Token, s.ClassId)).Enrolled)),
            ("mức thành thạo lớp", async () => Assert.NotEmpty((await _k.MasteryAsync(s.Token, s.ClassId)).ByHeritage)),
            ("kết quả một bài", async () => Assert.Equal(studentCount, (await _k.OverviewAsync(s.Token, quizId)).Finished)),
            ("từng câu của một bài", async () => Assert.Equal(questionCount, (await _k.QuestionStatsAsync(s.Token, quizId)).Questions.Count)),
            ("từng học sinh của một bài", async () => Assert.Equal(studentCount + 1, (await _k.QuizStudentsAsync(s.Token, quizId)).Students.Count)),
            ("gợi ý ôn tập lớp", async () => Assert.Equal(studentCount * quizCount, (await _k.ClassRecommendationsAsync(s.Token, s.ClassId)).SubmittedAttempts)),
        };
        foreach (var (name, call) in teacher)
        {
            var watch = Stopwatch.StartNew();
            await call();
            output.WriteLine($"{name}: {watch.ElapsedMilliseconds} ms");
            Assert.True(watch.ElapsedMilliseconds <= 3000, $"{name}: {watch.ElapsedMilliseconds} ms, yêu cầu tối đa 3000 ms");
        }

        var firstStudent = await factory.WithDbAsync(db => db.Students.Where(x => x.ClassId == s.ClassId).OrderBy(x => x.Id).Select(x => x.Id).FirstAsync());
        var profileWatch = Stopwatch.StartNew();
        Assert.Equal(quizCount, (await _k.ProfileAsync(s.Token, s.ClassId, firstStudent)).FinishedAttempts);
        output.WriteLine($"hồ sơ một học sinh: {profileWatch.ElapsedMilliseconds} ms");
        Assert.True(profileWatch.ElapsedMilliseconds <= 1000, $"hồ sơ một học sinh: {profileWatch.ElapsedMilliseconds} ms, yêu cầu tối đa 1000 ms");

        // Em theo dõi chưa có lượt nào nên làm thêm vài lượt để có dữ liệu; gợi ý của một em phải trong 2 giây.
        var own = await _k.Attempts.StartAsync(watcher, quizId);
        await _k.Attempts.SubmitAsync(watcher, own.Id);
        await _k.RecommendationsAsync(watcher);
        var recsWatch = Stopwatch.StartNew();
        var recs = await _k.RecommendationsAsync(watcher);
        output.WriteLine($"gợi ý cho em: {recsWatch.ElapsedMilliseconds} ms");
        Assert.True(recsWatch.ElapsedMilliseconds <= 2000, $"gợi ý cho em: {recsWatch.ElapsedMilliseconds} ms, yêu cầu tối đa 2000 ms");
        Assert.Equal(3, recs.NextQuizzes.Count);
    }
}
