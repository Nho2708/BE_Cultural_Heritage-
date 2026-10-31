using System.Net;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Quizzes;
using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Bài kiểm tra trên SQL Server LocalDB (database tạm tạo bằng migration, tự xóa khi xong): kiểu cột và ràng buộc do migration tạo, vòng đời đầy đủ,
/// tìm theo tên có dấu, lọc độ khó dịch được sang SQL, nhân bản bài học kèm bài kiểm tra, tác vụ tự đóng, và hai yêu cầu đồng thời không phát hành hai lần
/// hay làm bài vượt 50 câu.
/// </summary>
public sealed class QuizSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly QuizTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string Q = QuizTestKit.Quizzes;

    private Task<List<T>> SqlAsync<T>(FormattableString sql) => factory.WithDbAsync(db => db.Database.SqlQuery<T>(sql).ToListAsync());

    [LocalDbFact]
    public async Task The_quiz_tables_have_the_designed_column_types_checks_and_unique_indexes()
    {
        _ = factory.Server;

        Assert.Equal(new[]
        {
            "id bigint NOT NULL", "lesson_id bigint NOT NULL", "created_by bigint NOT NULL", "name nvarchar NULL", "description nvarchar NULL", "access_code nvarchar NULL",
            "difficulty_level nvarchar NULL", "time_limit_minutes int NULL", "open_at datetime2 NULL", "close_at datetime2 NULL",
            "shuffle_questions bit NOT NULL", "show_answers_after_submit bit NOT NULL", "total_point decimal NULL", "status nvarchar NULL",
            "published_at datetime2 NULL", "created_at datetime2 NULL", "updated_at datetime2 NULL", "deleted_at datetime2 NULL",
        }.Order(), (await SqlAsync<string>($"""
            SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('quizzes')
            """)).Order());
        Assert.Equal(["quiz_questions.point decimal(5,2)", "quizzes.total_point decimal(6,2)"],
            (await SqlAsync<string>($"""
                SELECT OBJECT_NAME(c.object_id) + '.' + c.name + ' ' + TYPE_NAME(c.user_type_id) + '(' + CAST(c.precision AS varchar(3)) + ',' + CAST(c.scale AS varchar(3)) + ')' AS [Value]
                FROM sys.columns c
                WHERE (c.object_id = OBJECT_ID('quizzes') AND c.name = 'total_point') OR (c.object_id = OBJECT_ID('quiz_questions') AND c.name = 'point')
                """)).Order());
        Assert.Equal(["CK_quizzes_difficulty_level", "CK_quizzes_status"],
            await SqlAsync<string>($"SELECT name AS [Value] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('quizzes') ORDER BY name"));
        Assert.Equal(["UX_attempt_answers_attempt_question", "UX_attempts_quiz_student"],
            await SqlAsync<string>($"SELECT name AS [Value] FROM sys.indexes WHERE is_unique = 1 AND is_primary_key = 0 AND object_id IN (OBJECT_ID('attempts'), OBJECT_ID('attempt_answers')) ORDER BY name"));
        Assert.Equal(["quiz_id,question_id"], await SqlAsync<string>($"""
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS [Value]
            FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('quiz_questions') AND i.is_primary_key = 1
            """));
    }

    [LocalDbFact]
    public async Task The_full_life_cycle_runs_on_sql_server_and_the_database_keeps_points_times_and_status()
    {
        var s = await _k.SetupAsync(3);
        var quiz = await _k.CreateAsync(s, "Kiểm tra Hoàng thành Thăng Long", new
        {
            description = "Em đọc kỹ đề rồi trả lời.", difficultyLevel = DifficultyLevel.HARD, timeLimitMinutes = 40,
            openAt = QuizTestKit.OpenSoon, closeAt = QuizTestKit.CloseLater, shuffleQuestions = true, showAnswersAfterSubmit = true,
        });
        var questions = await _k.QuestionsAsync(s, 3);
        await _k.AddQuestionsAsync(s.Token, quiz.Id, questions.Select(x => x.Id), 1.25m);
        var published = await _k.PublishAsync(s.Token, quiz.Id);

        Assert.Equal((QuizStatus.PUBLISHED, 3.75m), (published.Quiz.Status, published.Quiz.TotalPoint));
        var row = (await SqlAsync<string>($"""
            SELECT status + '|' + CAST(total_point AS varchar(20)) + '|' + CAST(time_limit_minutes AS varchar(5)) + '|' + CAST(shuffle_questions AS varchar(1)) + CAST(show_answers_after_submit AS varchar(1)) + '|' + CASE WHEN published_at IS NULL THEN 'chua' ELSE 'co' END AS [Value]
            FROM quizzes WHERE id = {quiz.Id}
            """)).Single();
        Assert.Equal("PUBLISHED|3.75|40|11|co", row);
        Assert.Equal(["1.25|1", "1.25|2", "1.25|3"], await SqlAsync<string>($"SELECT CAST(point AS varchar(10)) + '|' + CAST(sort_order AS varchar(3)) AS [Value] FROM quiz_questions WHERE quiz_id = {quiz.Id} ORDER BY sort_order"));

        // Bài kiểm tra có dấu tiếng Việt, tìm theo từ khóa không phân biệt hoa thường.
        var found = await DiscoveryTestKit.Expect(Http.GetAsync<QuizPageDto>($"{Q}?keyword={Uri.EscapeDataString("HOÀNG THÀNH")}", s.Token));
        Assert.Equal(quiz.Id, Assert.Single(found.Items).Id);

        var closed = await _k.CloseAsync(s.Token, quiz.Id);
        Assert.Equal(QuizStatus.CLOSED, closed.Quiz.Status);
        Assert.Equal(["QUIZ_CLOSED", "QUIZ_PUBLISHED"], await SqlAsync<string>($"SELECT action AS [Value] FROM audit_logs WHERE target_type = 'quizzes' AND target_id = {quiz.Id} ORDER BY action"));
    }

    [LocalDbFact]
    public async Task Two_simultaneous_publishes_write_one_audit_row_and_one_published_time()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.ReadyQuizAsync(s);

        var calls = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Http.PostAsync<PublishQuizResultDto>($"{Q}/{quiz.Id}/publish", null, s.Token)));

        Assert.All(calls, c => Assert.Equal(HttpStatusCode.OK, c.Response.StatusCode));
        Assert.Equal(1, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM audit_logs WHERE action = 'QUIZ_PUBLISHED' AND target_id = {quiz.Id}")).Single());
        Assert.Single(calls.Select(c => c.Body.Data!.Quiz.PublishedAt).Distinct());
    }

    [LocalDbFact]
    public async Task Two_simultaneous_requests_cannot_push_a_quiz_past_fifty_questions()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.CreateAsync(s);
        var ids = new List<long>();
        for (var i = 0; i < 52; i++) ids.Add((await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE)).Id);

        var first = Http.PostAsync<QuizDetailDto>($"{Q}/{quiz.Id}/questions", new { questionIds = ids.Take(26) }, s.Token);
        var second = Http.PostAsync<QuizDetailDto>($"{Q}/{quiz.Id}/questions", new { questionIds = ids.Skip(26) }, s.Token);
        var results = await Task.WhenAll(first, second);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity], results.Select(r => r.Response.StatusCode).Order());
        Assert.Equal(26, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM quiz_questions WHERE quiz_id = {quiz.Id}")).Single());
        Assert.Equal(26m, (await _k.GetAsync(s.Token, quiz.Id)).TotalPoint);
    }

    [LocalDbFact]
    public async Task Duplicating_a_lesson_with_quizzes_and_the_difficulty_filter_translate_to_sql()
    {
        var s = await _k.SetupAsync(2);
        var lessonB = await _k.AnotherLessonAsync(s, "Bài khó");
        var quizB = await _k.CreateAsync(s, "Khó", lessonId: lessonB.Id);
        var hard = new List<long>();
        for (var i = 0; i < 2; i++) hard.Add((await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE, DifficultyLevel.HARD)).Id);
        await _k.AddQuestionsAsync(s.Token, quizB.Id, hard);
        var quizA = await _k.PublishedQuizAsync(s, "Bài đã phát hành", questions: 4, difficulty: DifficultyLevel.EASY);
        await _k.SetQuestionReviewAsync(quizA.Questions[2].QuestionId, ReviewStatus.REJECTED);
        var target = await _k.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "9A1", 9);

        var result = await DiscoveryTestKit.Expect(Http.PostAsync<DuplicateLessonResultDto>($"{LessonTestKit.Teacher}/{s.LessonId}/duplicate",
            new { targetClassId = target.Id, includeQuizzes = true }, s.Token), HttpStatusCode.Created);
        Assert.Equal((1, 1), (result.CopiedQuizzes, result.SkippedQuestions));

        var hardPage = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{LessonTestKit.Teacher}?difficulty=HARD&pageSize=100", s.Token));
        Assert.Equal([lessonB.Id], hardPage.Items.Select(x => x.Id));
        var easyPage = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{LessonTestKit.Teacher}?difficulty=EASY&pageSize=100", s.Token));
        Assert.Equal(new[] { s.LessonId, result.Lesson.Id }.Order(), easyPage.Items.Select(x => x.Id).Order());   // bài chép kèm cũng có bài kiểm tra dễ
    }

    [LocalDbFact]
    public async Task The_background_task_closes_overdue_quizzes_on_sql_server()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.PublishedQuizAsync(s, "Quá hạn");
        var attempt = await _k.AddAttemptAsync(s, quiz.Id);
        await _k.SetQuizTimesAsync(quiz.Id, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddMinutes(-1));

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<QuizAutoCloser>().CloseDueAsync(CancellationToken.None);

        Assert.Equal(QuizStatus.CLOSED, (await _k.GetAsync(s.Token, quiz.Id)).Status);
        Assert.Equal(["EXPIRED"], await SqlAsync<string>($"SELECT status AS [Value] FROM attempts WHERE id = {attempt}"));
        Assert.Equal(["SYSTEM"], await SqlAsync<string>($"SELECT actor_role AS [Value] FROM audit_logs WHERE action = 'QUIZ_CLOSED' AND target_id = {quiz.Id}"));
    }

    [LocalDbFact]
    public async Task The_database_rejects_a_second_attempt_of_the_same_student_and_a_status_outside_the_list()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.PublishedQuizAsync(s);
        await _k.AddAttemptAsync(s, quiz.Id, AttemptStatus.SUBMITTED, "Hoàng Văn Em");
        var studentId = (await SqlAsync<long>($"SELECT id AS [Value] FROM students WHERE full_name = N'Hoàng Văn Em' AND class_id = {s.ClassId}")).Single();

        var duplicate = await Assert.ThrowsAnyAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.Attempts.Add(new DSVHVN.Domain.Quizzes.Attempt { QuizId = quiz.Id, StudentId = studentId, Status = AttemptStatus.IN_PROGRESS, StartedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        }));
        Assert.Contains("UX_attempts_quiz_student", duplicate.InnerException!.Message);

        var bad = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE quizzes SET status = 'ARCHIVED' WHERE id = {quiz.Id}")));
        Assert.Contains("CK_quizzes_status", bad.Message);
    }
}
