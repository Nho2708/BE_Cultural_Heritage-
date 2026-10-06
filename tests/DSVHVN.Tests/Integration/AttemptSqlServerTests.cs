using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Làm bài trên SQL Server LocalDB (database tạm tạo bằng migration, tự xóa khi xong): kiểu cột và ràng buộc của hai bảng trả lời, cả ba cấp chạy trọn vòng
/// bắt đầu - lưu từng dạng - nộp - kết quả, các truy vấn lịch sử và tiến độ dịch được sang SQL, và các yêu cầu đồng thời (bắt đầu, nộp, lưu cùng một câu)
/// không tạo dòng trùng hay chấm hai lần.
/// </summary>
public sealed class AttemptSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly AttemptTestKit _k = new(factory);

    private Task<List<T>> SqlAsync<T>(FormattableString sql) => factory.WithDbAsync(db => db.Database.SqlQuery<T>(sql).ToListAsync());

    [LocalDbFact]
    public async Task The_answer_tables_have_the_designed_column_types_checks_and_filtered_unique_indexes()
    {
        _ = factory.Server;

        Assert.Equal(new[]
        {
            "id bigint NOT NULL", "attempt_answer_id bigint NOT NULL", "answer_id bigint NULL", "blank_index tinyint NULL", "selected_answer_id bigint NULL",
            "selected_position tinyint NULL", "selected_bool bit NULL", "response_text nvarchar NULL", "is_correct bit NOT NULL",
        }.Order(), (await SqlAsync<string>($"""
            SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('attempt_answer_parts')
            """)).Order());
        Assert.Equal(["attempt_answers.parts_correct smallint NOT NULL", "attempt_answers.parts_total smallint NOT NULL"], (await SqlAsync<string>($"""
            SELECT OBJECT_NAME(c.object_id) + '.' + c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('attempt_answers') AND c.name IN ('parts_total', 'parts_correct')
            """)).Order());
        Assert.Equal(255, (await SqlAsync<int>($"SELECT CAST(max_length / 2 AS int) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('attempt_answer_parts') AND name = 'response_text'")).Single());
        Assert.Equal(["CK_attempt_answer_parts_target"],
            await SqlAsync<string>($"SELECT name AS [Value] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('attempt_answer_parts')"));
        Assert.Equal(["CK_attempt_answers_parts"],
            await SqlAsync<string>($"SELECT name AS [Value] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('attempt_answers')"));
        Assert.Equal(["UX_attempt_answer_parts_answer ([answer_id] IS NOT NULL)", "UX_attempt_answer_parts_blank ([blank_index] IS NOT NULL)"],
            await SqlAsync<string>($"SELECT name + ' ' + filter_definition AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID('attempt_answer_parts') AND is_unique = 1 AND is_primary_key = 0 ORDER BY name"));
    }

    [LocalDbFact]
    public async Task The_database_itself_rejects_a_part_without_a_target_a_part_with_two_targets_and_more_correct_parts_than_parts()
    {
        var q = await _k.OpenQuizAsync(2, types: [QuestionType.FILL_BLANK]);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        await _k.SaveAsync(q.StudentToken, attempt.Id, attempt.Questions[0].QuestionId, new QuestionSubmission(Blanks: ["Lý Thái Tổ"]));
        var rowId = (await _k.AnswerRowsAsync(attempt.Id)).Single().Id;
        var answerId = await factory.WithDbAsync(db => db.Answers.Where(a => a.QuestionId == q.Questions[0].Id).Select(a => a.Id).FirstAsync());

        var noTarget = await Assert.ThrowsAsync<SqlException>(() => factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"INSERT INTO attempt_answer_parts (attempt_answer_id, is_correct) VALUES ({rowId}, 0)")));
        Assert.Contains("CK_attempt_answer_parts_target", noTarget.Message);

        var twoTargets = await Assert.ThrowsAsync<SqlException>(() => factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"INSERT INTO attempt_answer_parts (attempt_answer_id, answer_id, blank_index, is_correct) VALUES ({rowId}, {answerId}, 2, 0)")));
        Assert.Contains("CK_attempt_answer_parts_target", twoTargets.Message);

        var duplicateBlank = await Assert.ThrowsAnyAsync<SqlException>(() => factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"INSERT INTO attempt_answer_parts (attempt_answer_id, blank_index, is_correct) VALUES ({rowId}, 1, 0)")));
        Assert.Contains("UX_attempt_answer_parts_blank", duplicateBlank.Message);

        var tooMany = await Assert.ThrowsAsync<SqlException>(() => factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE attempt_answers SET parts_correct = parts_total + 1 WHERE id = {rowId}")));
        Assert.Contains("CK_attempt_answers_parts", tooMany.Message);
    }

    [LocalDbFact]
    public async Task All_three_levels_run_the_whole_attempt_on_sql_server_with_every_part_stored()
    {
        foreach (var level in new[] { 1, 2, 3 })
        {
            var q = await _k.OpenQuizAsync(level);
            var result = await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);
            Assert.Equal((AttemptStatus.SUBMITTED, level), (result.Status, result.Level));

            var attempt = await _k.AttemptRowAsync(result.AttemptId);
            var expectedTotal = q.Questions.Count * 2m;
            Assert.Equal((AttemptStatus.SUBMITTED, expectedTotal, expectedTotal, q.Questions.Count), (attempt.Status, attempt.Score, attempt.TotalPoint, attempt.CorrectCount));

            var rows = await _k.AnswerRowsAsync(result.AttemptId);
            Assert.Equal(q.Questions.Count, rows.Count);
            Assert.All(rows, r => Assert.True(r.IsCorrect && r.PartsCorrect == r.PartsTotal));
            var multiPartTotal = rows.Where(r => r.Parts.Count > 0).Sum(r => r.Parts.Count);
            Assert.Equal(multiPartTotal, await factory.WithDbAsync(db => db.AttemptAnswerParts.CountAsync(p => p.AttemptAnswer!.AttemptId == result.AttemptId)));
            Assert.All(rows.SelectMany(r => r.Parts), p => Assert.True(p.IsCorrect));
        }
    }

    [LocalDbFact]
    public async Task History_progress_home_and_the_background_task_translate_to_sql()
    {
        var q = await _k.OpenQuizAsync(2);
        var second = await _k.OpenQuizAsync(2, setup: q.Setup);
        var done = await _k.AnswerAllAndSubmitAsync(q, AttemptTestKit.Correct);
        var late = await _k.StartAsync(q.StudentToken, second.Id);

        var history = await _k.HistoryAsync(q.StudentToken);
        Assert.Equal([done.AttemptId], history.Items.Select(x => x.AttemptId));
        var progress = await _k.ProgressAsync(q.StudentToken);
        Assert.Equal((1, 2, 0), (progress.QuizzesSubmitted, progress.QuizzesPublished, progress.LessonsCompleted));
        Assert.Equal((4, 4), (Assert.Single(progress.Mastery).Answered, progress.Mastery[0].Correct));
        var home = await _k.HomeAsync(q.StudentToken);
        Assert.Equal((late.Id, q.Setup.AccessCode), (Assert.Single(home.InProgress).AttemptId, Assert.Single(home.MyLessons).AccessCode));

        // Lượt quá hạn nộp 2 phút: tác vụ nền chốt, kết quả có đủ dòng.
        await _k.SetAttemptStartAsync(late.Id, DateTime.UtcNow.AddHours(-3));
        await _k.Quizzes.SetQuizTimesAsync(second.Id, DateTime.UtcNow.AddHours(-4), DateTime.UtcNow.AddMinutes(-2));
        using (var scope = factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<AttemptLifecycle>().ExpireOverdueAsync(CancellationToken.None) >= 1);
        Assert.Equal(["EXPIRED"], await SqlAsync<string>($"SELECT status AS [Value] FROM attempts WHERE id = {late.Id}"));
        Assert.Equal(4, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM attempt_answers WHERE attempt_id = {late.Id}")).Single());
    }

    // ---- Yêu cầu đồng thời ----

    [LocalDbFact]
    public async Task Simultaneous_starts_make_one_attempt_and_simultaneous_submits_grade_once()
    {
        var q = await _k.OpenQuizAsync(2);
        var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _k.StartRawAsync(q.StudentToken, q.Id)));
        Assert.All(starts, r => Assert.True(r.Response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{(int)r.Response.StatusCode} {r.Body.Message}"));
        Assert.Single(starts.Select(r => r.Body.Data!.Id).Distinct());
        Assert.Equal(1, await factory.WithDbAsync(db => db.Attempts.CountAsync(a => a.QuizId == q.Id)));

        var attempt = starts[0].Body.Data!;
        foreach (var question in attempt.Questions)
            await _k.SaveAsync(q.StudentToken, attempt.Id, question.QuestionId, AttemptTestKit.Correct(question, AttemptTestKit.DetailOf(q, question.QuestionId)));

        var submits = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _k.SubmitRawAsync(q.StudentToken, attempt.Id)));
        Assert.All(submits, r => Assert.Equal(HttpStatusCode.OK, r.Response.StatusCode));
        Assert.Single(submits.Select(r => r.Body.Data!.SubmittedAt).Distinct());
        Assert.All(submits, r => Assert.Equal(8m, r.Body.Data!.Score));

        Assert.Equal(4, await factory.WithDbAsync(db => db.AttemptAnswers.CountAsync(a => a.AttemptId == attempt.Id)));
        Assert.Equal(5, await factory.WithDbAsync(db => db.AttemptAnswerParts.CountAsync(p => p.AttemptAnswer!.AttemptId == attempt.Id)));   // 4 mục sắp xếp + 1 chỗ trống
    }

    [LocalDbFact]
    public async Task Saving_the_same_question_from_two_devices_at_once_keeps_one_row_with_the_last_answer_intact()
    {
        var q = await _k.OpenQuizAsync(1, types: [QuestionType.MATCHING]);
        var attempt = await _k.StartAsync(q.StudentToken, q.Id);
        var question = attempt.Questions[0];
        var good = AttemptTestKit.Correct(question, q.Questions[0]);
        var bad = AttemptTestKit.Wrong(question, q.Questions[0]);

        var saves = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => _k.SaveRawAsync(q.StudentToken, attempt.Id, question.QuestionId, i % 2 == 0 ? good : bad)));
        Assert.All(saves, r => Assert.Equal(HttpStatusCode.OK, r.Response.StatusCode));

        var row = Assert.Single(await _k.AnswerRowsAsync(attempt.Id));
        Assert.Equal(3, row.Parts.Count);   // đủ ba cặp, không dòng phần nào bị nhân đôi
        Assert.Equal(row.PartsCorrect, (short)row.Parts.Count(p => p.IsCorrect));
        Assert.True(row.PartsCorrect is 3 or 0);
    }
}
