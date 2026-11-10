using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Thư viện bài mẫu và viết lại một phần trên SQL Server LocalDB (database tạm tạo bằng migration, tự xóa khi xong): cột, khóa ngoại, chỉ mục lọc và CHECK do migration tạo,
/// loại tác vụ <c>LESSON_SECTION_REWRITE</c> (dài 22 ký tự) lưu được, và các truy vấn của thư viện (lọc, xếp theo lượt dùng, đếm, duyệt) dịch được sang SQL.
/// </summary>
public sealed class LessonSampleSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>, IAsyncLifetime
{
    private readonly QuizTestKit _q = new(factory);
    private LessonTestKit L => _q.Questions.Lesson;
    private HttpClient Http => L.Http;
    private const string T = LessonTestKit.Teacher;
    private const string Lib = "/api/v1/teacher/lesson-library";
    private const string Adm = "/api/v1/admin/lesson-library";

    public Task InitializeAsync()
    {
        factory.Ai.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<List<T2>> SqlAsync<T2>(FormattableString sql) => factory.WithDbAsync(db => db.Database.SqlQuery<T2>(sql).ToListAsync());

    [LocalDbFact]
    public async Task The_migration_adds_the_library_columns_keys_filtered_indexes_check_and_a_wider_task_type()
    {
        _ = factory.Server;

        var columns = await SqlAsync<string>($"""
            SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.max_length > 0 AND TYPE_NAME(c.user_type_id) = 'nvarchar' THEN '(' + CAST(c.max_length / 2 AS varchar(10)) + ')' ELSE '' END
                   + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('lessons') AND c.name LIKE 'sample[_]%' OR c.object_id = OBJECT_ID('lessons') AND c.name = 'source_lesson_id'
            ORDER BY c.name
            """);
        Assert.Equal(
        [
            "sample_note nvarchar(500) NULL", "sample_requested_at datetime2 NULL", "sample_reviewed_at datetime2 NULL", "sample_reviewed_by bigint NULL",
            "sample_status nvarchar(20) NULL", "source_lesson_id bigint NULL",
        ], columns);

        Assert.Equal(["fk_lessons_sample_reviewed_by -> users", "fk_lessons_source_lesson_id -> lessons"], await SqlAsync<string>($"""
            SELECT fk.name + ' -> ' + OBJECT_NAME(fk.referenced_object_id) AS [Value] FROM sys.foreign_keys fk
            WHERE fk.parent_object_id = OBJECT_ID('lessons') AND fk.name IN ('fk_lessons_sample_reviewed_by', 'fk_lessons_source_lesson_id') ORDER BY fk.name
            """));
        Assert.Equal(["NO_ACTION", "NO_ACTION"], await SqlAsync<string>($"""
            SELECT fk.delete_referential_action_desc AS [Value] FROM sys.foreign_keys fk
            WHERE fk.name IN ('fk_lessons_sample_reviewed_by', 'fk_lessons_source_lesson_id')
            """));
        Assert.Equal(["IX_lessons_sample_status ([sample_status] IS NOT NULL)", "IX_lessons_source_lesson_id ([source_lesson_id] IS NOT NULL)"], await SqlAsync<string>($"""
            SELECT i.name + ' ' + i.filter_definition AS [Value] FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID('lessons') AND i.name IN ('IX_lessons_sample_status', 'IX_lessons_source_lesson_id') ORDER BY i.name
            """));
        var check = (await SqlAsync<string>($"""
            SELECT definition AS [Value] FROM sys.check_constraints WHERE name = 'CK_lessons_sample_status'
            """)).Single();
        foreach (var status in new[] { "PENDING", "APPROVED", "REJECTED" }) Assert.Contains($"[sample_status]='{status}'", check);

        // task_type rộng 30 ký tự và hai CHECK nhận loại tác vụ mới.
        Assert.Equal(30, (await SqlAsync<int>($"SELECT max_length / 2 AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('ai_generations') AND name = 'task_type'")).Single());
        foreach (var name in new[] { "CK_ai_generations_task_type", "CK_ai_generations_subject" })
            Assert.Contains("LESSON_SECTION_REWRITE", (await SqlAsync<string>($"SELECT definition AS [Value] FROM sys.check_constraints WHERE name = {name}")).Single());
    }

    [LocalDbFact]
    public async Task The_database_enforces_the_library_status_the_keys_and_the_rewrite_task_subject()
    {
        _ = factory.Server;
        var s = await _q.Questions.LessonAsync(2);
        var h = s.Heritage;

        async Task Fails(FormattableString sql, string constraint)
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(sql)));
            Assert.Contains(constraint, ex.Message);
        }

        await Fails($"UPDATE lessons SET sample_status = 'LẠ' WHERE id = {s.LessonId}", "CK_lessons_sample_status");
        await Fails($"UPDATE lessons SET sample_reviewed_by = 987654321 WHERE id = {s.LessonId}", "fk_lessons_sample_reviewed_by");
        await Fails($"UPDATE lessons SET source_lesson_id = 987654321 WHERE id = {s.LessonId}", "fk_lessons_source_lesson_id");
        foreach (var status in new[] { "PENDING", "APPROVED", "REJECTED" })
            await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE lessons SET sample_status = {status} WHERE id = {s.LessonId}"));
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE lessons SET sample_status = NULL WHERE id = {s.LessonId}"));

        // Loại tác vụ mới dài 22 ký tự: lưu được khi gắn bài học, không lưu được khi gắn di sản hoặc thiếu đối tượng.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO ai_generations (task_type, lesson_id, requested_by, status) VALUES ('LESSON_SECTION_REWRITE', {s.LessonId}, {s.School.Teacher.Id}, 'SUCCEEDED')"));
        Assert.Equal(1, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM ai_generations WHERE lesson_id = {s.LessonId} AND task_type = 'LESSON_SECTION_REWRITE'")).Single());
        await Fails($"INSERT INTO ai_generations (task_type, heritage_id, requested_by, status) VALUES ('LESSON_SECTION_REWRITE', {h.Id}, {s.School.Teacher.Id}, 'SUCCEEDED')",
            "CK_ai_generations_subject");
        await Fails($"INSERT INTO ai_generations (task_type, requested_by, status) VALUES ('LESSON_SECTION_REWRITE', {s.School.Teacher.Id}, 'SUCCEEDED')",
            "CK_ai_generations_subject");
        await Fails($"INSERT INTO ai_generations (task_type, lesson_id, requested_by, status) VALUES ('LESSON_SECTION_REWRITES', {s.LessonId}, {s.School.Teacher.Id}, 'SUCCEEDED')",
            "CK_ai_generations_");   // một trong hai CHECK (loại tác vụ, đối tượng) chặn giá trị lạ
    }

    [LocalDbFact]
    public async Task The_whole_library_flow_runs_on_sql_server_including_filters_popularity_use_with_quizzes_and_the_review_lists()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (school, schoolClass) = await L.SchoolWithClassAsync(2);
        var (heritage, images) = await _q.Questions.HeritageAsync();
        var lesson = await L.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id], $"Thăng Long {tag}");
        var setup = new QuizTestKit.Setup(school, schoolClass.Id, 2, heritage, images, lesson.Id, lesson.AccessCode);
        await _q.ReadyQuizAsync(setup, "Kiểm tra mẫu", questions: 3);
        var admin = await L.Discovery.Api.AdminTokenAsync();

        // Đề xuất, danh sách duyệt (mặc định chờ duyệt), duyệt.
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonSampleResultDto>($"{T}/{lesson.Id}/sample-request", null, school.TeacherToken));
        var pending = await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<AdminLessonLibraryItemDto>>($"{Adm}?keyword=THĂNG LONG {tag}", admin));
        Assert.Equal((lesson.Id, LessonSampleStatus.PENDING), (Assert.Single(pending.Items).Id, pending.Items[0].Status));
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminLessonLibraryResultDto>($"{Adm}/{lesson.Id}/approve", null, admin));

        // Thư viện: tìm tiêu đề không phân biệt hoa thường (không bỏ dấu ă), lọc cấp, khối, di sản; xếp theo lượt dùng.
        var teacher = (await L.SchoolWithClassAsync(2));
        var token = teacher.School.TeacherToken;
        async Task<PagedResult<LessonLibraryItemDto>> Library(string query) =>
            await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<LessonLibraryItemDto>>($"{Lib}?{query}", token));
        Assert.Single((await Library($"keyword=THĂNG LONG {tag}")).Items);
        Assert.Empty((await Library($"keyword=Thang Long {tag}")).Items);
        Assert.Single((await Library($"keyword={tag}&level=2&grade=8&heritageId={heritage.Id}&sort=popular")).Items);
        Assert.Empty((await Library($"keyword={tag}&level=1")).Items);

        var detail = await DiscoveryTestKit.Expect(Http.GetAsync<LessonLibraryDetailDto>($"{Lib}/{lesson.Id}", token));
        Assert.Equal(("Kiểm tra mẫu", 3), (Assert.Single(detail.Quizzes).Title, detail.Quizzes[0].QuestionCount));

        // Dùng kèm bài kiểm tra: một giao dịch, bài mới là Nháp, đếm lượt dùng.
        var target = await L.Classroom.CreateClassAsync(token, "Lớp nhận SQL", 9);
        var used = await DiscoveryTestKit.Expect(
            Http.PostAsync<DuplicateLessonResultDto>($"{Lib}/{lesson.Id}/use", new { targetClassId = target.Id, includeQuizzes = true }, token), HttpStatusCode.Created);
        Assert.Equal((1, 0, ContentStatus.DRAFT), (used.CopiedQuizzes, used.SkippedQuestions, used.Lesson.Status));
        var sourceId = await factory.WithDbAsync(db => db.Lessons.Where(l => l.Id == used.Lesson.Id).Select(l => l.SourceLessonId).SingleAsync());
        Assert.Equal(lesson.Id, sourceId);
        Assert.Equal(1, Assert.Single((await Library($"keyword={tag}&sort=popular")).Items).UseCount);
        Assert.Equal(1, (await L.GetAsync(school.TeacherToken, lesson.Id)).Sample!.UseCount);
        Assert.Equal(1, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM audit_logs WHERE action = 'LESSON_SAMPLE_USED' AND target_id = {lesson.Id}")).Single());

        // Sửa bài đang ở thư viện thì về chờ duyệt (ghi vào cột trên SQL Server), từ chối rồi xem ở thẻ Đã từ chối.
        var (_, edit) = await Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new { title = $"Thăng Long {tag} sửa" }, school.TeacherToken);
        Assert.Equal(LessonSampleStatus.PENDING, edit.Data!.Sample!.Status);
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminLessonLibraryResultDto>($"{Adm}/{lesson.Id}/reject", new { note = "Chưa đạt yêu cầu." }, admin));
        var rejected = await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<AdminLessonLibraryItemDto>>($"{Adm}?status=REJECTED&level=2&keyword={tag}", admin));
        Assert.Equal("Chưa đạt yêu cầu.", Assert.Single(rejected.Items).Note);
        Assert.Empty((await Library($"keyword={tag}")).Items);
    }
}
