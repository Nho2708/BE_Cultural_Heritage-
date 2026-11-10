using System.Net;
using System.Text.Json;
using DSVHVN.Application.Common;
using DSVHVN.Application.Curriculum;
using DSVHVN.Application.Lessons;
using DSVHVN.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Chủ đề chương trình trên SQL Server LocalDB (database tạm tạo bằng migration, tự xóa khi xong): dữ liệu nạp sẵn đủ 40 chủ đề, hai bảng với CHECK, UNIQUE theo collation tiếng Việt,
/// khóa ngoại và mặc định đúng thiết kế, truy vấn của bài học, thư viện bài mẫu và chi tiết di sản dịch được sang SQL; migration nối liên kết theo slug khi CSDL đã có di sản.
/// </summary>
public sealed class CurriculumTopicSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>, IAsyncLifetime
{
    private const string Adm = "/api/v1/admin/curriculum-topics";
    private const string Tea = "/api/v1/teacher/curriculum-topics";
    private const string PreviousMigration = "20261110165816_ThemThuVienBaiMauVaVietLaiMotPhan";

    private readonly LessonTestKit _l = new(factory);
    private HttpClient Http => _l.Http;

    public Task InitializeAsync()
    {
        factory.Ai.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<List<T>> SqlAsync<T>(FormattableString sql) => factory.WithDbAsync(db => db.Database.SqlQuery<T>(sql).ToListAsync());

    private async Task Fails(FormattableString sql, string constraint)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(sql)));
        Assert.Contains(constraint, ex.Message);
    }

    private static string SeedFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "seed-data", "chu-de-chuong-trinh.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Không thấy seed-data/chu-de-chuong-trinh.json.");
    }

    private sealed record SeedTopic(string Subject, int Grade, string Group, string Title, int SortOrder, bool LocalProvince, string[] HeritageSlugs);

    private static List<SeedTopic> ReadSeed()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SeedFile()));
        return doc.RootElement.GetProperty("topics").EnumerateArray().Select(t => new SeedTopic(
            t.GetProperty("subject").GetString()!, t.GetProperty("grade").GetInt32(), t.GetProperty("group").GetString()!, t.GetProperty("title").GetString()!,
            t.GetProperty("sortOrder").GetInt32(), t.GetProperty("localProvince").GetBoolean(),
            t.GetProperty("heritageSlugs").EnumerateArray().Select(s => s.GetString()!).ToArray())).ToList();
    }

    [LocalDbFact]
    public async Task The_migration_creates_both_tables_with_the_columns_check_unique_key_defaults_and_the_cascade_from_topic_to_links()
    {
        _ = factory.Server;

        Assert.Equal(
        [
            "created_at datetime2 NULL", "description nvarchar(1000) NULL", "grade tinyint NOT NULL", "group_title nvarchar(255) NOT NULL", "id int NOT NULL",
            "is_active bit NOT NULL", "local_province bit NOT NULL", "sort_order int NOT NULL", "source_url nvarchar(500) NULL", "subject nvarchar(100) NOT NULL",
            "title nvarchar(255) NOT NULL", "updated_at datetime2 NULL",
        ], await SqlAsync<string>($"""
            SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN TYPE_NAME(c.user_type_id) = 'nvarchar' THEN '(' + CAST(c.max_length / 2 AS varchar(10)) + ')' ELSE '' END
                   + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('curriculum_topics') ORDER BY c.name
            """));
        Assert.Equal(["heritage_id bigint NOT NULL", "topic_id int NOT NULL"], await SqlAsync<string>($"""
            SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END AS [Value]
            FROM sys.columns c WHERE c.object_id = OBJECT_ID('heritage_curriculum_topics') ORDER BY c.name
            """));

        Assert.Equal(["fk_heritage_curriculum_topics_heritage_id -> heritages NO_ACTION", "fk_heritage_curriculum_topics_topic_id -> curriculum_topics CASCADE"],
            await SqlAsync<string>($"""
                SELECT fk.name + ' -> ' + OBJECT_NAME(fk.referenced_object_id) + ' ' + fk.delete_referential_action_desc COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.foreign_keys fk WHERE fk.parent_object_id = OBJECT_ID('heritage_curriculum_topics') ORDER BY fk.name
                """));
        Assert.Equal("([grade]>=(1) AND [grade]<=(12))", (await SqlAsync<string>($"SELECT definition AS [Value] FROM sys.check_constraints WHERE name = 'CK_curriculum_topics_grade'")).Single());
        Assert.Equal(["grade", "subject", "group_title", "title"], await SqlAsync<string>($"""
            SELECT c.name AS [Value] FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = 'UX_curriculum_topics_grade_subject_group_title' AND i.is_unique = 1 AND i.has_filter = 0 ORDER BY ic.key_ordinal
            """));
        Assert.Equal(["((0))", "((1))"], (await SqlAsync<string>($"""
            SELECT dc.definition AS [Value] FROM sys.default_constraints dc WHERE dc.parent_object_id = OBJECT_ID('curriculum_topics')
            """)).Order().ToList());   // is_active mặc định 1, local_province mặc định 0

        // Mặc định của cột bit, CHECK khối, UNIQUE không phân biệt hoa thường theo collation Vietnamese_CI_AI.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var subject = "Môn thử " + tag;
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO curriculum_topics (subject, grade, group_title, title, sort_order) VALUES ({subject}, 8, N'Nhóm thử', N'Nội dung thử', 1)"));
        var row = (await SqlAsync<string>($"SELECT CAST(local_province AS varchar(1)) + CAST(is_active AS varchar(1)) AS [Value] FROM curriculum_topics WHERE subject = {subject}")).Single();
        Assert.Equal("01", row);
        await Fails($"INSERT INTO curriculum_topics (subject, grade, group_title, title, sort_order) VALUES ({subject}, 0, N'Nhóm', N'Chủ đề', 1)", "CK_curriculum_topics_grade");
        await Fails($"INSERT INTO curriculum_topics (subject, grade, group_title, title, sort_order) VALUES ({subject}, 13, N'Nhóm', N'Chủ đề', 1)", "CK_curriculum_topics_grade");
        await Fails($"INSERT INTO curriculum_topics (subject, grade, group_title, title, sort_order) VALUES ({subject.ToUpper()}, 8, N'NHÓM THỬ', N'NỘI DUNG THỬ', 2)",
            "UX_curriculum_topics_grade_subject_group_title");
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO curriculum_topics (subject, grade, group_title, title, sort_order) VALUES ({subject}, 9, N'Nhóm thử', N'Nội dung thử', 1)"));   // khối khác: không trùng

        // Liên kết: khóa ngoại tới di sản chặn mã lạ, khóa chính chặn trùng, xóa chủ đề xóa liên kết theo.
        var topicId = (await SqlAsync<int>($"SELECT id AS [Value] FROM curriculum_topics WHERE subject = {subject} AND grade = 8")).Single();
        var heritage = await _l.HeritageAsync("Di sản ràng buộc chủ đề");
        await Fails($"INSERT INTO heritage_curriculum_topics (heritage_id, topic_id) VALUES (987654321, {topicId})", "fk_heritage_curriculum_topics_heritage_id");
        await Fails($"INSERT INTO heritage_curriculum_topics (heritage_id, topic_id) VALUES ({heritage.Id}, 987654321)", "fk_heritage_curriculum_topics_topic_id");
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"INSERT INTO heritage_curriculum_topics (heritage_id, topic_id) VALUES ({heritage.Id}, {topicId})"));
        await Fails($"INSERT INTO heritage_curriculum_topics (heritage_id, topic_id) VALUES ({heritage.Id}, {topicId})", "PK_heritage_curriculum_topics");
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"DELETE FROM curriculum_topics WHERE id = {topicId}"));
        Assert.Equal(0, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM heritage_curriculum_topics WHERE topic_id = {topicId}")).Single());
    }

    [LocalDbFact]
    public async Task The_database_is_seeded_with_exactly_the_40_programme_topics_and_no_links_when_no_heritage_matches_yet()
    {
        _ = factory.Server;
        var seed = ReadSeed();

        // Dữ liệu nạp sẵn (khối 4, 5, 10; các kiểm thử khác của lớp chỉ thêm chủ đề khối 8 và 9): có created_at, mọi dòng đang dùng.
        var rows = await factory.WithDbAsync(db => db.CurriculumTopics.AsNoTracking().Where(t => t.Grade == 4 || t.Grade == 5 || t.Grade == 10)
            .OrderBy(t => t.Id).ToListAsync());
        Assert.Equal(40, seed.Count);
        Assert.Equal(40, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsActive));
        Assert.All(rows, r => Assert.NotNull(r.CreatedAt));
        Assert.Equal(seed.Select(s => $"{s.Grade}|{s.Subject}|{s.Group}|{s.Title}|{s.SortOrder}|{s.LocalProvince}").Order(StringComparer.Ordinal).ToList(),
            rows.Select(r => $"{r.Grade}|{r.Subject}|{r.GroupTitle}|{r.Title}|{r.SortOrder}|{r.LocalProvince}").Order(StringComparer.Ordinal).ToList());
        Assert.Equal(new[] { 4, 5, 10 }, rows.Select(r => (int)r.Grade).Distinct().Order().ToArray());
        Assert.Equal(new[] { "Lịch sử", "Lịch sử và Địa lí" }, rows.Select(r => r.Subject).Distinct().Order().ToArray());
        Assert.Equal(2, rows.Count(r => r.LocalProvince));   // hai chủ đề "Địa phương em" không gắn di sản cố định
        Assert.All(rows.Where(r => r.LocalProvince), r => Assert.StartsWith("Địa phương em", r.GroupTitle));

        // CSDL tạm của kiểm thử không có di sản trùng slug lúc chạy migration nên chưa có liên kết nào từ migration.
        var links = await factory.WithDbAsync(db => db.HeritageCurriculumTopics.CountAsync(l => l.Topic!.Grade == 4 || l.Topic.Grade == 5 || l.Topic.Grade == 10));
        Assert.Equal(0, links);

        // Giáo viên thấy 40 chủ đề theo khối, theo thứ tự chương trình; quản trị hệ thống thấy cùng danh sách.
        var school = await _l.Classroom.SchoolAsync();
        var admin = await _l.Discovery.Api.AdminTokenAsync();
        var teacherGrades = new Dictionary<int, int>();
        foreach (var grade in new[] { 4, 5, 10 })
        {
            var items = (await DiscoveryTestKit.Expect(Http.GetAsync<TeacherCurriculumTopicListDto>($"{Tea}?grade={grade}", school.TeacherToken))).Items;
            teacherGrades[grade] = items.Count;
            Assert.Equal(seed.Where(s => s.Grade == grade).OrderBy(s => s.SortOrder).Select(s => s.Title).ToList(), items.Select(i => i.Title).ToList());   // thứ tự chương trình
            var adminItems = (await DiscoveryTestKit.Expect(Http.GetAsync<AdminCurriculumTopicListDto>($"{Adm}?grade={grade}", admin))).Items;
            Assert.Equal(items.Select(i => i.Id).ToList(), adminItems.Select(i => i.Id).ToList());
        }
        Assert.Equal(seed.GroupBy(s => s.Grade).ToDictionary(g => g.Key, g => g.Count()), teacherGrades);
    }

    [LocalDbFact]
    public async Task The_migration_links_topics_to_heritages_by_slug_and_skips_slugs_that_are_not_in_the_database()
    {
        var seed = ReadSeed();
        var withHeritages = seed.Where(s => s.HeritageSlugs.Length > 0).ToList();
        var present = withHeritages.SelectMany(s => s.HeritageSlugs).Distinct().Take(6).ToList();   // chỉ một phần di sản có sẵn trong CSDL
        Assert.True(present.Count == 6);

        var name = LocalDb.NewTemporaryName();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(LocalDb.ConnectionString(name)).Options;
        try
        {
            await using (var db = new AppDbContext(options, TimeProvider.System))
            {
                // CSDL đã có các migration trước và đã có vài di sản (như máy chủ thật) rồi mới áp migration chủ đề chương trình.
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                foreach (var slug in present)
                    await db.Database.ExecuteSqlAsync($"INSERT INTO heritages (name, slug, status) VALUES ({"Di sản " + slug}, {slug}, 'PUBLISHED')");
                await db.GetService<IMigrator>().MigrateAsync();
            }

            await using var check = new AppDbContext(options, TimeProvider.System);
            Assert.Equal(40, await check.CurriculumTopics.CountAsync());
            var expected = seed.SelectMany(s => s.HeritageSlugs.Distinct().Where(present.Contains).Select(slug => $"{s.Grade}|{s.Subject}|{s.Group}|{s.Title}|{slug}"))
                .Order(StringComparer.Ordinal).ToList();
            var actual = (await check.HeritageCurriculumTopics.AsNoTracking()
                    .Select(l => new { l.Topic!.Grade, l.Topic.Subject, l.Topic.GroupTitle, l.Topic.Title, l.Heritage!.Slug }).ToListAsync())
                .Select(x => $"{x.Grade}|{x.Subject}|{x.GroupTitle}|{x.Title}|{x.Slug}")
                .Order(StringComparer.Ordinal).ToList();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);

            // Hai chủ đề "Địa phương em" không có liên kết nào.
            Assert.Equal(0, await check.HeritageCurriculumTopics.CountAsync(l => l.Topic!.LocalProvince));

            // Hoàn tác migration xóa sạch hai bảng; áp lại thì nạp lại đủ.
            await check.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.Equal(0, await check.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name LIKE '%curriculum%'").SingleAsync());
        }
        finally
        {
            LocalDb.Drop(name);
        }
    }

    [LocalDbFact]
    public async Task The_lesson_library_and_heritage_queries_with_topics_run_on_sql_server()
    {
        var admin = await _l.Discovery.Api.AdminTokenAsync();
        var (school, schoolClass) = await _l.SchoolWithClassAsync(2);   // lớp khối 8
        var heritage = await _l.HeritageAsync("Di sản chủ đề SQL", events: 3);
        var other = await _l.HeritageAsync("Di sản chủ đề SQL khác");
        var tag = Guid.NewGuid().ToString("N")[..8];

        async Task<AdminCurriculumTopicDto> Create(int grade, string title, int order) => await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>(Adm,
            new { subject = "Lịch sử", grade, groupTitle = "Nhóm " + tag, title, description = (string?)null, localProvince = false, sortOrder = order, sourceUrl = (string?)null }, admin),
            HttpStatusCode.Created);
        var topic = await Create(8, "Chủ đề SQL " + tag, 1);
        var sameKeyOtherCase = await Http.PostAsync<object>(Adm,
            new { subject = "LỊCH SỬ", grade = 8, groupTitle = "NHÓM " + tag, title = "CHỦ ĐỀ SQL " + tag, localProvince = false, sortOrder = 2 }, admin);
        Assert.Equal(HttpStatusCode.Conflict, sameKeyOtherCase.Response.StatusCode);   // kiểm trùng ở dịch vụ, cùng kết quả như SQLite
        var otherGrade = await Create(9, "Chủ đề SQL " + tag, 1);

        foreach (var (id, ids) in new[] { (topic.Id, new[] { heritage.Id }), (otherGrade.Id, new[] { heritage.Id, other.Id }) })
            await DiscoveryTestKit.Expect(Http.PutAsync<AdminCurriculumTopicHeritagesDto>($"{Adm}/{id}/heritages", new { heritageIds = ids }, admin));

        // Bài học: chỉ chủ đề cùng khối.
        var lesson = await _l.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id], "Bài chủ đề SQL " + tag);
        Assert.Equal([topic.Id], (await _l.GetAsync(school.TeacherToken, lesson.Id)).CurriculumTopics!.Select(t => t.Id).ToArray());

        // Thư viện: lọc theo chủ đề và nhãn chủ đề trên thẻ.
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonSampleResultDto>($"{LessonTestKit.Teacher}/{lesson.Id}/sample-request", null, school.TeacherToken));
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminLessonLibraryResultDto>($"/api/v1/admin/lesson-library/{lesson.Id}/approve", null, admin));
        var viewer = await _l.Classroom.SchoolAsync();
        var library = await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<LessonLibraryItemDto>>($"/api/v1/teacher/lesson-library?topicId={topic.Id}", viewer.TeacherToken));
        var card = Assert.Single(library.Items);
        Assert.Equal(lesson.Id, card.Id);
        Assert.Equal([topic.Id], card.CurriculumTopics!.Select(t => t.Id).ToArray());
        Assert.Empty((await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<LessonLibraryItemDto>>($"/api/v1/teacher/lesson-library?topicId={otherGrade.Id}", viewer.TeacherToken))).Items);

        // Danh sách của giáo viên đếm di sản đang xuất bản; di sản đã ẩn không tính.
        await _l.ArchiveHeritageAsync(other.Id);
        var teacherItems = (await DiscoveryTestKit.Expect(Http.GetAsync<TeacherCurriculumTopicListDto>($"{Tea}?grade=9", viewer.TeacherToken))).Items;
        Assert.Equal(1, teacherItems.Single(t => t.Id == otherGrade.Id).HeritageCount);
        Assert.Equal(2, (await DiscoveryTestKit.Expect(Http.GetAsync<AdminCurriculumTopicListDto>($"{Adm}?grade=9", admin))).Items.Single(t => t.Id == otherGrade.Id).HeritageCount);

        // Chi tiết di sản của quản trị có chủ đề.
        Assert.Equal([topic.Id, otherGrade.Id], (await _l.HeritageOnlyAsync(heritage.Id)).CurriculumTopics!.Select(t => t.Id).ToArray());

        // Nhật ký thao tác ghi đủ các mã mới.
        var actions = await factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.TargetId == topic.Id && a.TargetType == "curriculum_topics")
            .Select(a => a.Action.ToString()).ToListAsync());
        Assert.Contains("CURRICULUM_TOPIC_CREATED", actions);
        Assert.Contains("CURRICULUM_TOPIC_HERITAGES_CHANGED", actions);
    }
}
