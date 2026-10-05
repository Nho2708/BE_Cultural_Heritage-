using System.Net;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Ai;
using DSVHVN.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Ngân hàng câu hỏi trên SQL Server LocalDB (database tạm tạo bằng migration, tự xóa khi xong): cột và ràng buộc của CSDL v4 do migration tạo,
/// CHECK của cấp, đoạn tư liệu, dạng câu, vị trí do CSDL thực thi, câu cũ lên v4 nhận cấp phù hợp mặc định, tìm lời câu theo collation tiếng Việt,
/// bộ lọc dịch được sang SQL, và hạn mức lượt AI không bị vượt khi nhiều yêu cầu đến cùng lúc.
/// </summary>
public sealed class QuestionSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>, IAsyncLifetime
{
    private readonly QuestionTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string T = QuestionTestKit.Teacher;
    private const string L = QuestionTestKit.Lessons;

    public Task InitializeAsync()
    {
        factory.QuestionAi.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        factory.QuestionAi.Reset();
        return Task.CompletedTask;
    }

    private Task<List<T2>> SqlAsync<T2>(FormattableString sql) => factory.WithDbAsync(db => db.Database.SqlQuery<T2>(sql).ToListAsync());

    [LocalDbFact]
    public async Task The_migration_adds_the_v4_columns_of_questions_and_answers_with_the_designed_types()
    {
        _ = factory.Server;

        Assert.Equal(new[]
        {
            "id bigint NOT NULL", "heritage_id bigint NOT NULL", "created_by bigint NOT NULL", "ai_generation_id bigint NULL", "content nvarchar NULL",
            "question_type nvarchar NULL", "difficulty_level nvarchar NULL", "explanation nvarchar NULL", "source nvarchar NULL", "review_status nvarchar NULL",
            "reviewed_by bigint NULL", "reviewed_at datetime2 NULL", "created_at datetime2 NULL", "updated_at datetime2 NULL", "deleted_at datetime2 NULL",
            "image_url nvarchar NULL", "passage nvarchar NULL", "passage_source nvarchar NULL", "level_min tinyint NOT NULL", "level_max tinyint NOT NULL",
        }.Order(), (await SqlAsync<string>($"""
            SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('questions')
            """)).Order());
        Assert.Equal(new[]
        {
            "id bigint NOT NULL", "question_id bigint NOT NULL", "content nvarchar NULL", "is_correct bit NOT NULL", "sort_order int NULL",
            "image_url nvarchar NULL", "match_content nvarchar NULL", "correct_position tinyint NULL", "blank_index tinyint NULL",
        }.Order(), (await SqlAsync<string>($"""
            SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('answers')
            """)).Order());

        // Độ dài cột theo thiết kế: nvarchar(500) cho hình và nguồn (1000 byte), nvarchar(1000) cho vế phải (2000 byte), passage nvarchar(max) (-1).
        Assert.Equal(new[] { "answers.image_url 1000", "answers.match_content 2000", "questions.image_url 1000", "questions.passage -1", "questions.passage_source 1000" }.Order(),
            (await SqlAsync<string>($"""
                SELECT OBJECT_NAME(c.object_id) + '.' + c.name + ' ' + CAST(c.max_length AS varchar(10)) AS [Value] FROM sys.columns c
                WHERE (c.object_id = OBJECT_ID('answers') AND c.name IN ('image_url', 'match_content'))
                   OR (c.object_id = OBJECT_ID('questions') AND c.name IN ('image_url', 'passage', 'passage_source'))
                """)).Order());

        Assert.Equal(["CK_questions_difficulty_level", "CK_questions_levels", "CK_questions_passage", "CK_questions_question_type", "CK_questions_review_status", "CK_questions_source"],
            await SqlAsync<string>($"SELECT name AS [Value] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('questions') ORDER BY name"));
        Assert.Equal(["CK_answers_positions"],
            await SqlAsync<string>($"SELECT name AS [Value] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('answers')"));
        var typeCheck = (await SqlAsync<string>($"SELECT definition AS [Value] FROM sys.check_constraints WHERE name = 'CK_questions_question_type'")).Single();
        foreach (var type in Enum.GetNames<QuestionType>()) Assert.Contains($"'{type}'", typeCheck);
        Assert.Equal(new[] { "(CONVERT([tinyint],(3)))", "(CONVERT([tinyint],(1)))" }.Order(),
            (await SqlAsync<string>($"""
                SELECT d.definition AS [Value] FROM sys.default_constraints d JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
                WHERE d.parent_object_id = OBJECT_ID('questions')
                """)).Order());
    }

    [LocalDbFact]
    public async Task The_database_itself_refuses_bad_levels_missing_passages_unknown_types_and_positions_out_of_range()
    {
        _ = factory.Server;
        var setup = await _k.LessonAsync(2);
        var made = await _k.CreateAsync(setup.Token, QuestionTestKit.Samples(setup.Heritage.Id, setup.Images)[QuestionType.SINGLE_CHOICE]);
        var (heritage, user) = (setup.Heritage.Id, setup.School.Teacher.Id);

        async Task Fails(FormattableString sql, string constraint)
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(sql)));
            Assert.Contains(constraint, ex.Message);
        }

        await Fails($"""
            INSERT INTO questions (heritage_id, created_by, content, question_type, level_min, level_max, created_at)
            VALUES ({heritage}, {user}, N'Câu hỏi thử', 'SINGLE_CHOICE', 3, 2, SYSUTCDATETIME())
            """, "CK_questions_levels");
        await Fails($"""
            INSERT INTO questions (heritage_id, created_by, content, question_type, level_min, level_max, created_at)
            VALUES ({heritage}, {user}, N'Câu hỏi thử', 'SINGLE_CHOICE', 0, 3, SYSUTCDATETIME())
            """, "CK_questions_levels");
        await Fails($"""
            INSERT INTO questions (heritage_id, created_by, content, question_type, level_min, level_max, created_at)
            VALUES ({heritage}, {user}, N'Câu hỏi thử', 'SINGLE_CHOICE', 1, 4, SYSUTCDATETIME())
            """, "CK_questions_levels");
        await Fails($"""
            INSERT INTO questions (heritage_id, created_by, content, question_type, level_min, level_max, created_at)
            VALUES ({heritage}, {user}, N'Câu hỏi thử', 'SOURCE_READING', 3, 3, SYSUTCDATETIME())
            """, "CK_questions_passage");
        await Fails($"""
            INSERT INTO questions (heritage_id, created_by, content, question_type, level_min, level_max, created_at)
            VALUES ({heritage}, {user}, N'Câu hỏi thử', 'ESSAY', 1, 3, SYSUTCDATETIME())
            """, "CK_questions_question_type");
        await Fails($"INSERT INTO answers (question_id, content, is_correct, correct_position) VALUES ({made.Id}, N'x', 1, 21)", "CK_answers_positions");
        await Fails($"INSERT INTO answers (question_id, content, is_correct, blank_index) VALUES ({made.Id}, N'x', 1, 0)", "CK_answers_positions");
        await Fails($"INSERT INTO answers (question_id, content, is_correct, blank_index) VALUES ({made.Id}, N'x', 1, 255)", "CK_answers_positions");

        // Hợp lệ thì vào được: đoạn tư liệu có, cấp một khoảng, vị trí biên 1 và 20.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"""
            INSERT INTO questions (heritage_id, created_by, content, question_type, passage, level_min, level_max, created_at)
            VALUES ({heritage}, {user}, N'Câu hỏi thử', 'SOURCE_READING', N'Đoạn tư liệu', 3, 3, SYSUTCDATETIME())
            """));
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"INSERT INTO answers (question_id, content, is_correct, correct_position, blank_index) VALUES ({made.Id}, N'x', 1, 20, 1)"));
        // Không nêu cấp thì mặc định cả ba cấp.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"""
            INSERT INTO questions (heritage_id, created_by, content, question_type, created_at) VALUES ({heritage}, {user}, N'Câu mặc định', 'SINGLE_CHOICE', SYSUTCDATETIME())
            """));
        Assert.Equal(["1 3"], await SqlAsync<string>($"SELECT CAST(level_min AS varchar(3)) + ' ' + CAST(level_max AS varchar(3)) AS [Value] FROM questions WHERE content = N'Câu mặc định'"));
    }

    [LocalDbFact]
    public async Task Questions_of_the_previous_version_get_their_default_levels_when_the_migration_runs()
    {
        var name = LocalDb.NewTemporaryName();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(LocalDb.ConnectionString(name)).Options;
            await using var db = new AppDbContext(options, TimeProvider.System);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261005200057_ThemPhanNoiDungBaiHocTheoCap");

            // Câu của phiên bản trước: chỉ hai dạng, chưa có cột cấp. Tắt kiểm khóa ngoại để chèn thẳng không cần dựng cả di sản.
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE questions NOCHECK CONSTRAINT ALL");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO questions (heritage_id, created_by, content, question_type, difficulty_level, source, review_status, created_at) VALUES
                (1, 1, N'Câu một đáp án đúng', 'SINGLE_CHOICE', 'EASY', 'MANUAL', 'APPROVED', SYSUTCDATETIME()),
                (1, 1, N'Câu đúng sai cũ', 'TRUE_FALSE', 'MEDIUM', 'AI', 'PENDING', SYSUTCDATETIME())
                """);

            await migrator.MigrateAsync();

            var rows = await db.Database.SqlQueryRaw<string>("""
                SELECT question_type + ' ' + CAST(level_min AS varchar(3)) + '-' + CAST(level_max AS varchar(3)) + ' ' + ISNULL(image_url, 'khong-hinh') AS [Value]
                FROM questions ORDER BY id
                """).ToListAsync();
            Assert.Equal(["SINGLE_CHOICE 2-3 khong-hinh", "TRUE_FALSE 2-2 khong-hinh"], rows);       // Đúng/Sai cũ chưa có hình nên chưa dùng được ở cấp 1
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            LocalDb.Drop(name);
        }
    }

    [LocalDbFact]
    public async Task Every_type_round_trips_through_sql_server_and_the_keyword_search_ignores_case_but_not_accents()
    {
        var setup = await _k.LessonAsync(2);
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        var ids = new Dictionary<QuestionType, long>();
        foreach (var (type, request) in samples) ids[type] = (await _k.CreateAsync(setup.Token, request)).Id;

        foreach (var (type, request) in samples)
        {
            var read = await _k.GetAsync(setup.Token, ids[type]);
            Assert.Equal((type, request.Content, request.Explanation), (read.QuestionType, read.Content, read.Explanation));    // tiếng Việt có dấu giữ nguyên
        }
        var fill = await _k.GetAsync(setup.Token, ids[QuestionType.FILL_BLANK]);
        Assert.Equal(["Lý Thái Tổ", "Lý Công Uẩn"], fill.Blanks.Single().Accepted);
        Assert.Equal(samples[QuestionType.SOURCE_READING].Passage, (await _k.GetAsync(setup.Token, ids[QuestionType.SOURCE_READING])).Passage);

        async Task<long[]> Found(string keyword) =>
            (await _k.ListAsync(setup.Token, $"scope=MINE&pageSize=100&keyword={Uri.EscapeDataString(keyword)}")).Items.Select(i => i.Id).Order().ToArray();

        Assert.Contains(ids[QuestionType.FILL_BLANK], await Found("HOA LƯ"));                    // không phân biệt hoa thường
        Assert.Contains(ids[QuestionType.FILL_BLANK], await Found("hoa lư"));
        Assert.DoesNotContain(ids[QuestionType.FILL_BLANK], await Found("hoa lu"));              // phân biệt dấu (ư khác u) theo collation của CSDL
        Assert.Equal([ids[QuestionType.SINGLE_CHOICE]], await Found("UNESCO ghi danh vào năm nào"));

        // Bộ lọc dịch được sang SQL.
        var multi = await _k.ListAsync(setup.Token, $"scope=MINE&level=3&difficultyLevel=HARD&questionType=MULTI_TRUE_FALSE&heritageId={setup.Heritage.Id}&source=MANUAL&status=APPROVED");
        Assert.Equal([ids[QuestionType.MULTI_TRUE_FALSE]], multi.Items.Select(i => i.Id));
        Assert.Equal(multi.Items[0].CreatedBy, setup.School.Teacher.Id);
        Assert.Equal(samples.Count, (await _k.ListAsync(setup.Token, $"createdBy={setup.School.Teacher.Id}&pageSize=100")).TotalItems);

        // Trong quiz thì đếm đúng và chỉ đọc; xóa mềm rời khỏi danh sách.
        await _k.AddToQuizAsync(setup.LessonId, ids[QuestionType.SINGLE_CHOICE]);
        Assert.Equal(1, (await _k.ListAsync(setup.Token, "scope=MINE&questionType=SINGLE_CHOICE")).Items.Single().QuizCount);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Http.DeleteAsync<object>($"{T}/{ids[QuestionType.SINGLE_CHOICE]}", setup.Token)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.DeleteAsync<object>($"{T}/{ids[QuestionType.MATCHING]}", setup.Token)).Response.StatusCode);
        Assert.DoesNotContain(ids[QuestionType.MATCHING], (await _k.ListAsync(setup.Token, "scope=MINE&pageSize=100")).Items.Select(i => i.Id));
    }

    [LocalDbFact]
    public async Task Ai_generation_and_the_review_flow_run_on_sql_server_and_the_admin_hides_a_question()
    {
        var setup = await _k.LessonAsync(1);
        var admin = await _k.Lesson.Discovery.Api.AdminTokenAsync();

        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 8 });
        Assert.Equal(8, batch.Received);
        var stored = await _k.RowsAsync(batch.GenerationId);
        Assert.Equal(8, stored.Count);
        Assert.All(stored, q => Assert.Equal(("1-1", ReviewStatus.PENDING, QuestionSource.AI), ($"{q.LevelMin}-{q.LevelMax}", q.ReviewStatus!.Value, q.Source!.Value)));
        Assert.All(stored.SelectMany(q => q.Answers), a => Assert.NotNull(a.SortOrder));

        var result = await DiscoveryTestKit.Expect(Http.PostAsync<ApproveQuestionsResultDto>($"{T}/approve", new { questionIds = batch.Questions.Select(q => q.Id).ToArray() }, setup.Token));
        Assert.Equal(8, result.Approved.Count);
        var other = (await _k.Lesson.Classroom.SchoolAsync()).TeacherToken;
        Assert.Equal(8, (await _k.ListAsync(other, $"scope=SHARED&level=1&heritageId={setup.Heritage.Id}&pageSize=100")).TotalItems);

        var hide = batch.Questions[0].Id;
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"/api/v1/admin/questions/{hide}/hide", new { reason = "Thử" }, admin));
        Assert.Equal(7, (await _k.ListAsync(other, $"scope=SHARED&level=1&heritageId={setup.Heritage.Id}&pageSize=100")).TotalItems);
        var rows = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPageDto>($"/api/v1/admin/questions?status=REJECTED&heritageId={setup.Heritage.Id}", admin));
        Assert.Equal(hide, Assert.Single(rows.Items).Id);
        Assert.True(rows.Items[0].IsHidden);
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.QUESTION_HIDDEN && l.TargetId == hide)));
    }

    [LocalDbFact]
    public async Task Parallel_question_generation_cannot_use_more_turns_than_the_plan_allows()
    {
        var setup = await _k.LessonAsync(2);
        var lessons = new List<long> { setup.LessonId };
        for (var i = 0; i < 5; i++) lessons.Add((await _k.Lesson.CreateLessonAsync(setup.Token, setup.ClassId, [setup.Heritage.Id], "Bài " + i)).Id);

        await factory.WithDbAsync(async db =>
        {
            var plan = new Plan { Name = "Gói 2 lượt " + DiscoveryTestKit.Unique(), Price = 0m, DurationDays = 30, MaxTeachers = 5, MaxAiRequests = 2 };
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            foreach (var s in await db.Subscriptions.Where(x => x.OrganizationId == setup.School.OrgId).ToListAsync()) s.PlanId = plan.Id;
            return await db.SaveChangesAsync();
        });
        // Dịch vụ AI chậm một chút để các yêu cầu chồng lên nhau.
        factory.QuestionAi.Behavior = async (_, request, ct) =>
        {
            await Task.Delay(300, ct);
            return await new SimulatedQuestionAiClient().GenerateQuestionsAsync(request, "x", ct);
        };

        var results = await Task.WhenAll(lessons.Select(id => Http.PostAsync<QuestionBatchDto>($"{L}/{id}/ai/questions", new { count = 2 }, setup.Token)));

        Assert.Equal(2, results.Count(r => r.Response.StatusCode == HttpStatusCode.OK));
        var refused = results.Where(r => r.Response.StatusCode != HttpStatusCode.OK).ToList();
        Assert.Equal(4, refused.Count);
        Assert.All(refused, r => Assert.Equal((HttpStatusCode.UnprocessableEntity, "AI_QUOTA_REACHED"), (r.Response.StatusCode, r.Body.ErrorCode)));
        Assert.Equal(2, factory.QuestionAi.Calls);                                                // chỉ 2 lần gọi dịch vụ AI thật
        Assert.Equal(2, await factory.WithDbAsync(db => db.AiGenerations.CountAsync(g => lessons.Contains(g.LessonId!.Value))));
        Assert.Equal(4, (await _k.ListAsync(setup.Token, "scope=PENDING")).TotalItems);
    }
}
