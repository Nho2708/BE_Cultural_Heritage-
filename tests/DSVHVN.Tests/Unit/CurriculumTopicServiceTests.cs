using DSVHVN.Application.Common;
using DSVHVN.Application.Curriculum;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Curriculum;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Unit;

/// <summary>Chủ đề chương trình ở tầng service trên SQLite: quyền theo vai trò, kiểm hợp lệ từng trường, đếm di sản theo trạng thái, nhật ký, quy tắc khối.</summary>
public sealed class CurriculumTopicServiceTests : IDisposable
{
    private readonly ServiceHarness _h = new();
    private Actor _admin = null!;
    private Actor _teacher = null!;
    private Actor _orgAdmin = null!;

    public CurriculumTopicServiceTests()
    {
        var org = _h.AddOrganizationAsync().GetAwaiter().GetResult();
        var admin = _h.AddUserAsync("quantri.he.thong", RoleCode.ADMIN, null).GetAwaiter().GetResult();
        var teacher = _h.AddUserAsync("giao.vien", RoleCode.TEACHER, org.Id).GetAwaiter().GetResult();
        var orgAdmin = _h.AddUserAsync("quantri.truong", RoleCode.ORG_ADMIN, org.Id).GetAwaiter().GetResult();
        _admin = new Actor(admin.Id, RoleCode.ADMIN, null, "203.0.113.5");
        _teacher = new Actor(teacher.Id, RoleCode.TEACHER, org.Id, "203.0.113.6");
        _orgAdmin = new Actor(orgAdmin.Id, RoleCode.ORG_ADMIN, org.Id, "203.0.113.7");
    }

    public void Dispose() => _h.Dispose();

    private Task<T> Svc<T>(Func<CurriculumTopicService, Task<T>> action) => _h.Service(action);

    private static CurriculumTopicRequest Req(string title = "Thăng Long - Hà Nội", int? grade = 4, string subject = "Lịch sử và Địa lí",
        string group = "Đồng bằng Bắc Bộ", bool? local = false, int? order = 1) =>
        new(subject, grade, group, title, "Mô tả", local, order, "https://example.org");

    private async Task<long> AddHeritageAsync(string slug, ContentStatus status, bool deleted = false) =>
        await _h.QueryAsync(async db =>
        {
            var heritage = new Heritage { Name = "Di sản " + slug, Slug = slug, Status = status, DeletedAt = deleted ? DateTime.UtcNow : null };
            db.Heritages.Add(heritage);
            await db.SaveChangesAsync();
            return heritage.Id;
        });

    [Fact]
    public void Grades_one_to_twelve_are_valid_and_the_limits_follow_the_columns()
    {
        Assert.All(Enumerable.Range(1, 12), g => Assert.True(CurriculumRules.IsValidGrade(g)));
        Assert.All(new[] { -1, 0, 13, 100 }, g => Assert.False(CurriculumRules.IsValidGrade(g)));
        Assert.Equal((100, 255, 255, 1000, 500, 200), (CurriculumRules.SubjectMaxLength, CurriculumRules.GroupTitleMaxLength, CurriculumRules.TitleMaxLength,
            CurriculumRules.DescriptionMaxLength, CurriculumRules.SourceUrlMaxLength, CurriculumRules.MaxHeritagesPerTopic));
    }

    [Fact]
    public async Task Every_operation_checks_the_role_before_touching_the_data()
    {
        var topic = await Svc(s => s.CreateAsync(_admin, Req(), default));

        var teacherCalls = new Func<CurriculumTopicService, Task>[]
        {
            s => s.ListForAdminAsync(_teacher, null, false, default),
            s => s.CreateAsync(_teacher, Req("Khác"), default),
            s => s.UpdateAsync(_teacher, topic.Id, Req("Khác"), default),
            s => s.DeactivateAsync(_teacher, topic.Id, default),
            s => s.ActivateAsync(_teacher, topic.Id, default),
            s => s.GetHeritagesForAdminAsync(_teacher, topic.Id, default),
            s => s.SetHeritagesAsync(_teacher, topic.Id, new SetTopicHeritagesRequest([]), default),
        };
        foreach (var call in teacherCalls)
            Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => _h.Service(call))).StatusCode);

        var readCalls = new Func<CurriculumTopicService, Task>[]
        {
            s => s.ListForTeacherAsync(_orgAdmin, null, default),
            s => s.HeritagesForTeacherAsync(_orgAdmin, topic.Id, default),
            s => s.ListForTeacherAsync(_admin, null, default),
            s => s.ListForAdminAsync(_orgAdmin, null, false, default),
            s => s.CreateAsync(_orgAdmin, Req("Khác"), default),
        };
        foreach (var call in readCalls)
            Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => _h.Service(call))).StatusCode);

        Assert.Single((await Svc(s => s.ListForAdminAsync(_admin, null, true, default))).Items);
    }

    [Fact]
    public async Task An_empty_request_reports_every_required_field_and_saves_nothing()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _h.Service<CurriculumTopicService>(s => s.CreateAsync(_admin,
            new CurriculumTopicRequest(null, null, null, null, null, null, null, null), default)));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(["grade", "groupTitle", "localProvince", "sortOrder", "subject", "title"], ex.Errors!.Keys.Order().ToArray());
        Assert.Empty(await _h.QueryAsync(db => db.CurriculumTopics.ToListAsync()));
        Assert.Empty(await _h.QueryAsync(db => db.AuditLogs.ToListAsync()));
    }

    [Fact]
    public async Task The_heritage_count_depends_on_the_audience_published_only_for_teachers_and_every_undeleted_one_for_admins()
    {
        var topic = await Svc(s => s.CreateAsync(_admin, Req(), default));
        var published = await AddHeritageAsync("xuat-ban", ContentStatus.PUBLISHED);
        var draft = await AddHeritageAsync("nhap", ContentStatus.DRAFT);
        var archived = await AddHeritageAsync("da-an", ContentStatus.ARCHIVED);
        var deleted = await AddHeritageAsync("da-xoa", ContentStatus.PUBLISHED, deleted: true);
        await _h.QueryAsync(async db =>
        {
            db.HeritageCurriculumTopics.AddRange(new[] { published, draft, archived, deleted }.Select(h => new HeritageCurriculumTopic { HeritageId = h, TopicId = topic.Id }));
            return await db.SaveChangesAsync();
        });

        Assert.Equal(1, (await Svc(s => s.ListForTeacherAsync(_teacher, 4, default))).Items.Single().HeritageCount);
        Assert.Equal(3, (await Svc(s => s.ListForAdminAsync(_admin, 4, false, default))).Items.Single().HeritageCount);
        var forTeacher = await Svc(s => s.HeritagesForTeacherAsync(_teacher, topic.Id, default));
        Assert.Equal([published], forTeacher.Items.Select(i => i.Id).ToArray());
        var forAdmin = await Svc(s => s.GetHeritagesForAdminAsync(_admin, topic.Id, default));
        Assert.Equal(new[] { archived, draft, published }.Order().ToArray(), forAdmin.Items.Select(i => i.Id).Order().ToArray());
    }

    [Fact]
    public async Task Replacing_the_list_also_clears_links_to_deleted_heritages_and_logs_the_difference()
    {
        var topic = await Svc(s => s.CreateAsync(_admin, Req(), default));
        var keep = await AddHeritageAsync("giu", ContentStatus.PUBLISHED);
        var gone = await AddHeritageAsync("xoa", ContentStatus.PUBLISHED, deleted: true);
        var add = await AddHeritageAsync("them", ContentStatus.DRAFT);
        await _h.QueryAsync(async db =>
        {
            db.HeritageCurriculumTopics.AddRange(new HeritageCurriculumTopic { HeritageId = keep, TopicId = topic.Id },
                new HeritageCurriculumTopic { HeritageId = gone, TopicId = topic.Id });
            return await db.SaveChangesAsync();
        });

        var result = await Svc(s => s.SetHeritagesAsync(_admin, topic.Id, new SetTopicHeritagesRequest([keep, add]), default));

        Assert.Equal(new[] { keep, add }.Order().ToArray(), result.Items.Select(i => i.Id).Order().ToArray());
        Assert.Equal(new[] { keep, add }.Order().ToArray(), (await _h.QueryAsync(db => db.HeritageCurriculumTopics.Select(l => l.HeritageId).ToListAsync())).Order().ToArray());
        var log = await _h.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.CURRICULUM_TOPIC_HERITAGES_CHANGED));
        Assert.Contains($"\"added\":[{add}]", log.NewValue);
        Assert.Contains($"\"removed\":[{gone}]", log.NewValue);
    }

    [Fact]
    public async Task Duplicates_are_compared_inside_the_same_grade_ignoring_case_and_accents_and_edits_can_keep_their_own_key()
    {
        var first = await Svc(s => s.CreateAsync(_admin, Req("Đền Hùng", 4), default));
        await Svc(s => s.CreateAsync(_admin, Req("Đền Hùng", 5), default));   // khối khác: không trùng

        var ex = await Assert.ThrowsAsync<AppException>(() => _h.Service<CurriculumTopicService>(s => s.CreateAsync(_admin, Req("  DEN HUNG ", 4), default)));
        Assert.Equal((409, MessageCodes.Duplicate), (ex.StatusCode, ex.AppMessage.Code));

        var edited = await Svc(s => s.UpdateAsync(_admin, first.Id, Req("Đền Hùng", 4, order: 9), default));
        Assert.Equal(9, edited.SortOrder);
        Assert.Equal(2, (await Svc(s => s.ListForAdminAsync(_admin, null, true, default))).Items.Count);
    }

    [Fact]
    public async Task Deactivating_a_topic_keeps_its_row_and_its_links()
    {
        var topic = await Svc(s => s.CreateAsync(_admin, Req(), default));
        var heritage = await AddHeritageAsync("lien-ket", ContentStatus.PUBLISHED);
        await Svc(s => s.SetHeritagesAsync(_admin, topic.Id, new SetTopicHeritagesRequest([heritage]), default));
        await Svc(s => s.DeactivateAsync(_admin, topic.Id, default));

        Assert.Equal(1, await _h.QueryAsync(db => db.CurriculumTopics.CountAsync()));
        Assert.Equal(1, await _h.QueryAsync(db => db.HeritageCurriculumTopics.CountAsync()));
    }
}
