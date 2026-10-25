using DSVHVN.Application.Attempts;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Bài chơi cấp 1 dạng luyện tập trên SQL Server LocalDB (database tạm tạo bằng migration): kiểu cột và ràng buộc của số lần kiểm và của bảng bài đã học xong,
/// kiểm câu, kết quả, trang học, album và bước Nhớ lại dịch được sang SQL, và các yêu cầu đồng thời (kiểm cùng một câu, ghi cùng một bài) không tạo dòng trùng.
/// </summary>
public sealed class LevelOnePracticeSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly AttemptTestKit _k = new(factory);
    private LessonTestKit Lessons => _k.Quizzes.Questions.Lesson;

    private Task<List<T>> SqlAsync<T>(FormattableString sql) => factory.WithDbAsync(db => db.Database.SqlQuery<T>(sql).ToListAsync());

    [LocalDbFact]
    public async Task The_try_count_column_and_the_lesson_completion_table_have_the_designed_types_and_constraints()
    {
        _ = factory.Server;

        Assert.Equal(["attempt_answers.try_count tinyint NOT NULL (CONVERT([tinyint],(0)))"], await SqlAsync<string>($"""
            SELECT OBJECT_NAME(c.object_id) + '.' + c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END
                   + ' ' + d.definition AS [Value]
            FROM sys.columns c JOIN sys.default_constraints d ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
            WHERE c.object_id = OBJECT_ID('attempt_answers') AND c.name = 'try_count'
            """));
        Assert.Equal(new[] { "id bigint NOT NULL", "student_id bigint NOT NULL", "lesson_id bigint NOT NULL", "completed_at datetime2 NOT NULL" }.Order(), (await SqlAsync<string>($"""
            SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('lesson_completions')
            """)).Order());
        Assert.Equal(["fk_lesson_completions_lesson_id", "fk_lesson_completions_student_id"],
            await SqlAsync<string>($"SELECT name AS [Value] FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('lesson_completions') ORDER BY name"));
        Assert.Equal(["UX_lesson_completions_student_lesson"],
            await SqlAsync<string>($"SELECT name AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID('lesson_completions') AND is_unique = 1 AND is_primary_key = 0"));
    }

    [LocalDbFact]
    public async Task The_whole_level_one_path_runs_on_sql_server_and_two_checks_or_two_completions_at_once_keep_one_row()
    {
        var q = await _k.OpenQuizAsync(1);
        var token = q.StudentToken;
        var other = await _k.Quizzes.Questions.HeritageAsync("Hội Gióng");
        var lessonB = await Lessons.PublishedLessonAsync(q.Setup.Token, q.Setup.ClassId, [other.Heritage.Id], "Bài B");

        var attempt = await _k.StartAsync(token, q.Id);
        var tf = attempt.Questions.Single(x => x.QuestionType == QuestionType.TRUE_FALSE);
        var detail = AttemptTestKit.DetailOf(q, tf.QuestionId);

        // Hai lần kiểm cùng lúc: một dòng, số lần kiểm là 2, lần đầu giữ nguyên.
        var results = await Task.WhenAll(
            _k.CheckRawAsync(token, attempt.Id, tf.QuestionId, AttemptTestKit.Wrong(tf, detail)),
            _k.CheckRawAsync(token, attempt.Id, tf.QuestionId, AttemptTestKit.Correct(tf, detail)));
        Assert.All(results, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.Response.StatusCode));
        Assert.Equal([1, 2], results.Select(r => r.Body.Data!.TryCount).Order());
        var rows = await factory.WithDbAsync(db => db.AttemptAnswers.Where(a => a.AttemptId == attempt.Id && a.QuestionId == tf.QuestionId).ToListAsync());
        var row = Assert.Single(rows);
        Assert.Equal(2, row.TryCount);

        foreach (var question in attempt.Questions.Where(x => x.QuestionId != tf.QuestionId))
            await _k.CheckAsync(token, attempt.Id, question.QuestionId, AttemptTestKit.Correct(question, AttemptTestKit.DetailOf(q, question.QuestionId)));
        var result = await _k.SubmitAsync(token, attempt.Id);
        Assert.Equal(row.IsCorrect ? 4 : 3, result.FirstTryCorrect);
        Assert.Equal((1, 2), (result.AlbumCollected, result.AlbumTotal));   // bài đầu đã có thẻ, bài B chưa

        // Hai yêu cầu ghi bài đã học xong cùng lúc: một dòng, đúng một yêu cầu là lần đầu.
        var completions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Lessons.CompleteRawAsync(token, lessonB.AccessCode!)));
        Assert.All(completions, c => Assert.Equal(System.Net.HttpStatusCode.OK, c.Response.StatusCode));
        Assert.Equal(1, completions.Count(c => c.Body.Data!.FirstTime));
        Assert.Equal(1, await factory.WithDbAsync(db => db.LessonCompletions.CountAsync(c => c.LessonId == lessonB.Id)));

        var album = await _k.AlbumAsync(token);
        Assert.Equal((2, 2), (album.Collected, album.Total));
        var home = await _k.HomeAsync(token);
        Assert.Equal((2, 2), (home.AlbumCollected, home.AlbumTotal));
        Assert.All(home.ClassLessons, l => Assert.True(l.Completed));
        Assert.NotNull(home.Cheer);

        // Bước Nhớ lại dịch được sang SQL và chỉ đọc.
        var recall = await DiscoveryTestKit.Expect(Lessons.RecallRawAsync(token, lessonB.AccessCode!));
        Assert.NotEmpty(recall.Questions);
        Assert.True(recall.Questions.Count <= 3);
    }
}
