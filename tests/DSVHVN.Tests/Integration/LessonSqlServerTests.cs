using System.Net;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Ai;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Bài học trên SQL Server LocalDB (database tạm tạo bằng migration, tự xóa khi xong): lược đồ lesson_sections và ai_generations của CSDL v4 do migration tạo,
/// ràng buộc CHECK và UNIQUE có lọc của mã bài học, luồng từ tạo bài đến học sinh mở bài, và các việc phải chạy tuần tự khi nhiều yêu cầu đến cùng lúc
/// (giữ chỗ lượt AI theo hạn mức, xuất bản cùng một bài hai lần).
/// </summary>
public sealed class LessonSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>, IAsyncLifetime
{
    private readonly LessonTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string T = LessonTestKit.Teacher;

    public Task InitializeAsync()
    {
        factory.Ai.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        factory.Ai.Reset();
        return Task.CompletedTask;
    }

    private Task<List<T2>> SqlAsync<T2>(FormattableString sql) => factory.WithDbAsync(db => db.Database.SqlQuery<T2>(sql).ToListAsync());

    [LocalDbFact]
    public async Task The_migration_creates_lesson_sections_and_reshapes_ai_generations_as_in_the_v4_design()
    {
        _ = factory.Server;

        var columns = await SqlAsync<string>($"""
            SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('lesson_sections') ORDER BY c.column_id
            """);
        Assert.Equal(
        [
            "id bigint NOT NULL", "lesson_id bigint NOT NULL", "section_type nvarchar NOT NULL", "title nvarchar NULL", "body nvarchar NULL",
            "image_url nvarchar NULL", "milestone_year int NULL", "event_id bigint NULL", "source_citation nvarchar NULL", "sort_order int NOT NULL",
            "created_at datetime2 NOT NULL", "updated_at datetime2 NULL",
        ], columns);

        Assert.Equal(["IX_lesson_sections_lesson_order"], await SqlAsync<string>($"""
            SELECT i.name AS [Value] FROM sys.indexes i WHERE i.object_id = OBJECT_ID('lesson_sections') AND i.name LIKE 'IX_lesson_sections_lesson%'
            """));
        Assert.Equal(["CK_lesson_sections_required", "CK_lesson_sections_section_type"], await SqlAsync<string>($"""
            SELECT name AS [Value] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('lesson_sections') ORDER BY name
            """));
        Assert.Equal(["fk_lesson_sections_event_id", "fk_lesson_sections_lesson_id"], await SqlAsync<string>($"""
            SELECT name AS [Value] FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('lesson_sections') ORDER BY name
            """));

        // ai_generations: lesson_id được NULL, có heritage_id, CHECK về đối tượng và loại tác vụ.
        Assert.Equal(["heritage_id", "lesson_id"], await SqlAsync<string>($"""
            SELECT name AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('ai_generations') AND is_nullable = 1 AND name IN ('lesson_id', 'heritage_id') ORDER BY name
            """));
        Assert.Equal(["CK_ai_generations_status", "CK_ai_generations_subject", "CK_ai_generations_task_type"], await SqlAsync<string>($"""
            SELECT name AS [Value] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('ai_generations') ORDER BY name
            """));
        Assert.Contains("HERITAGE_SUMMARY", (await SqlAsync<string>($"""
            SELECT definition AS [Value] FROM sys.check_constraints WHERE name = 'CK_ai_generations_task_type'
            """)).Single());
        Assert.Equal(1, (await SqlAsync<int>($"""
            SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE name = 'fk_ai_generations_heritage_id'
            """)).Single());
    }

    [LocalDbFact]
    public async Task The_database_enforces_the_section_ai_and_lesson_code_constraints()
    {
        _ = factory.Server;
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var lesson = await _k.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id]);
        var other = await _k.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id]);

        async Task Fails(FormattableString sql, string constraint)
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(sql)));
            Assert.Contains(constraint, ex.Message);
        }

        await Fails($"INSERT INTO lesson_sections (lesson_id, section_type, sort_order, created_at) VALUES ({lesson.Id}, 'MILESTONE', 1, SYSUTCDATETIME())",
            "CK_lesson_sections_required");
        await Fails($"INSERT INTO lesson_sections (lesson_id, section_type, sort_order, created_at) VALUES ({lesson.Id}, 'SOURCE_EXCERPT', 1, SYSUTCDATETIME())",
            "CK_lesson_sections_required");
        await Fails($"INSERT INTO lesson_sections (lesson_id, section_type, sort_order, created_at) VALUES ({lesson.Id}, 'IMAGE', 1, SYSUTCDATETIME())",
            "CK_lesson_sections_required");
        await Fails($"INSERT INTO lesson_sections (lesson_id, section_type, sort_order, created_at) VALUES ({lesson.Id}, 'VIDEO', 1, SYSUTCDATETIME())",
            "CK_lesson_sections_section_type");
        await Fails($"INSERT INTO ai_generations (task_type, requested_by, status) VALUES ('LESSON_SUMMARY', {school.Teacher.Id}, 'SUCCEEDED')",
            "CK_ai_generations_subject");
        await Fails($"INSERT INTO ai_generations (task_type, lesson_id, heritage_id, requested_by, status) VALUES ('LESSON_SUMMARY', {lesson.Id}, {h.Id}, {school.Teacher.Id}, 'SUCCEEDED')",
            "CK_ai_generations_subject");
        await Fails($"INSERT INTO ai_generations (task_type, lesson_id, requested_by, status) VALUES ('HERITAGE_TRANSLATION', {lesson.Id}, {school.Teacher.Id}, 'SUCCEEDED')",
            "CK_ai_generations_subject");

        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO ai_generations (task_type, heritage_id, requested_by, status) VALUES ('HERITAGE_SUMMARY', {h.Id}, {school.Teacher.Id}, 'SUCCEEDED')"));

        // Mã bài học: duy nhất khi có giá trị (kể cả khác hoa thường, theo collation CI), nhiều bài chưa có mã thì được.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE lessons SET access_code = 'BH7F3K2Q' WHERE id = {lesson.Id}"));
        await Fails($"UPDATE lessons SET access_code = 'bh7f3k2q' WHERE id = {other.Id}", "UX_lessons_access_code");
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE lessons SET access_code = NULL WHERE id = {lesson.Id}"));   // gỡ mã thì mã được dùng lại
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE lessons SET access_code = 'BH7F3K2Q' WHERE id = {other.Id}"));
    }

    [LocalDbFact]
    public async Task The_whole_flow_runs_on_the_migrated_schema_and_the_title_search_ignores_case()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(1);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var token = school.TeacherToken;

        var lesson = await _k.PublishedLessonAsync(token, schoolClass.Id, [h.Id], "Chuyện kể Thăng Long");
        var student = await _k.StudentTokenAsync(token, schoolClass.Id);
        var (res, body) = await _k.OpenAsync(student, lesson.AccessCode!.ToLowerInvariant());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal((lesson.Id, "STEP_BY_STEP", lesson.Sections.Count), (body.Data!.Id, body.Data.Layout.Layout, body.Data.Sections.Count));
        Assert.Equal(lesson.Summary, body.Data.Summary);                                    // tiếng Việt có dấu lưu và đọc đúng
        Assert.Contains("Hoàng thành", body.Data.Summary);

        var list = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{T}?keyword=THĂNG LONG", token));
        Assert.Single(list.Items);
        var wrongMarks = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{T}?keyword=Thang Long", token));
        Assert.Empty(wrongMarks.Items);                                                     // ă khác a theo collation Vietnamese_CI_AI

        var rows = await factory.WithDbAsync(db => db.AiGenerations.AsNoTracking().Where(g => g.LessonId == lesson.Id).ToListAsync());
        var row = Assert.Single(rows);
        Assert.Equal((AiTaskStatus.SUCCEEDED, AiTaskType.LESSON_SUMMARY), (row.Status, row.TaskType));
        Assert.Contains("Hoàng thành", row.InputSnapshot);                                 // nvarchar(max) giữ nguyên chữ Việt
    }

    [LocalDbFact]
    public async Task Parallel_ai_requests_cannot_use_more_turns_than_the_plan_allows()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var token = school.TeacherToken;
        var lessons = new List<long>();
        for (var i = 0; i < 6; i++) lessons.Add((await _k.CreateLessonAsync(token, schoolClass.Id, [h.Id], "Bài " + i)).Id);

        await factory.WithDbAsync(async db =>
        {
            var plan = new Plan { Name = "Gói 2 lượt " + DiscoveryTestKit.Unique(), Price = 0m, DurationDays = 30, MaxTeachers = 5, MaxAiRequests = 2 };
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            foreach (var s in await db.Subscriptions.Where(x => x.OrganizationId == school.OrgId).ToListAsync()) s.PlanId = plan.Id;
            return await db.SaveChangesAsync();
        });
        // Dịch vụ AI chậm một chút để các yêu cầu chồng lên nhau.
        factory.Ai.Behavior = async (_, request, ct) =>
        {
            await Task.Delay(300, ct);
            return await new SimulatedLessonAiClient().ComposeLessonAsync(request, "x", ct);
        };

        var results = await Task.WhenAll(lessons.Select(id => Http.PostAsync<LessonDraftDto>($"{T}/{id}/ai/compose", new { }, token)));

        Assert.Equal(2, results.Count(r => r.Response.StatusCode == HttpStatusCode.OK));
        var refused = results.Where(r => r.Response.StatusCode != HttpStatusCode.OK).ToList();
        Assert.Equal(4, refused.Count);
        Assert.All(refused, r => Assert.Equal((HttpStatusCode.UnprocessableEntity, "AI_QUOTA_REACHED"), (r.Response.StatusCode, r.Body.ErrorCode)));
        Assert.Equal(2, factory.Ai.Calls);                                                  // chỉ 2 lần gọi dịch vụ AI thật
        var used = await factory.WithDbAsync(db => db.AiGenerations.CountAsync(g => lessons.Contains(g.LessonId!.Value)));
        Assert.Equal(2, used);
    }

    [LocalDbFact]
    public async Task Publishing_the_same_lesson_twice_at_once_gives_one_code_to_both_callers()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var token = school.TeacherToken;
        var lesson = await _k.CreateLessonAsync(token, schoolClass.Id, [h.Id]);
        await _k.SaveDraftAsync(token, lesson.Id, await _k.ComposeAsync(token, lesson.Id));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Http.PostAsync<PublishLessonResultDto>($"{T}/{lesson.Id}/publish", null, token)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Response.StatusCode));
        Assert.Single(results.Select(r => r.Body.Data!.AccessCode).Distinct());
        Assert.Equal(1, results.Count(r => r.Body.Data!.CodeCreated));
        Assert.Equal(results[0].Body.Data!.AccessCode, (await _k.GetAsync(token, lesson.Id)).AccessCode);
    }

    [LocalDbFact]
    public async Task Deleting_a_timeline_event_that_a_milestone_points_to_does_not_break_on_the_foreign_key()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var token = school.TeacherToken;
        var lesson = await _k.CreateLessonAsync(token, schoolClass.Id, [h.Id]);
        var materials = await DiscoveryTestKit.Expect(Http.GetAsync<LessonMaterialsDto>($"{T}/{lesson.Id}/materials", token));
        await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new
        {
            sections = materials.Events.Select(e => new { type = "MILESTONE", body = e.Name, milestoneYear = e.StartYear, eventId = (long?)e.EventId }).ToArray(),
        }, token));

        // Xóa cả giai đoạn chứa 3 sự kiện: các mốc giữ năm và nội dung, gỡ liên kết.
        var admin = await _k.Discovery.Api.AdminTokenAsync();
        var timelineId = await factory.WithDbAsync(db => db.Timelines.Where(t => t.HeritageId == h.Id).Select(t => t.Id).SingleAsync());
        var (del, _) = await Http.DeleteAsync<object>($"{DiscoveryTestKit.Admin}/timelines/{timelineId}", admin);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var after = await _k.GetAsync(token, lesson.Id);
        Assert.Equal(3, after.Sections.Count);
        Assert.All(after.Sections, s => Assert.Null(s.EventId));
        Assert.All(after.Sections, s => Assert.NotNull(s.MilestoneYear));
    }
}
