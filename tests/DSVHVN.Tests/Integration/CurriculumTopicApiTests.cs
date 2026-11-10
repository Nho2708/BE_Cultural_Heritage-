using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Curriculum;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Chủ đề chương trình môn học qua HTTP: quản trị hệ thống thêm, sửa, ngừng dùng, dùng lại chủ đề và thay danh sách di sản; giáo viên chỉ thấy chủ đề đang dùng và di sản
/// đang xuất bản; chủ đề của bài học chỉ lấy chủ đề cùng khối với lớp; lọc thư viện bài mẫu theo chủ đề; quyền theo vai trò và nhật ký thao tác.
/// </summary>
public sealed class CurriculumTopicApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Adm = "/api/v1/admin/curriculum-topics";
    private const string Tea = "/api/v1/teacher/curriculum-topics";
    private const string Lib = "/api/v1/teacher/lesson-library";
    private const string AdmLib = "/api/v1/admin/lesson-library";

    private readonly LessonTestKit _l = new(factory);
    private HttpClient Http => _l.Http;

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

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private Task<string> AdminAsync() => _l.Discovery.Api.AdminTokenAsync();

    private static object Body(string title, int grade = 8, string subject = "Lịch sử", string group = "Chủ đề lớn thử", bool local = false, int order = 1,
        string? description = "Mô tả thử", string? sourceUrl = "https://example.org/chuong-trinh") =>
        new { subject, grade, groupTitle = group, title, description, localProvince = local, sortOrder = order, sourceUrl };

    private async Task<AdminCurriculumTopicDto> CreateAsync(string admin, int grade = 8, string? title = null, string subject = "Lịch sử",
        string group = "Chủ đề lớn thử", bool local = false, int order = 1) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>(Adm, Body(title ?? "Chủ đề " + Unique(), grade, subject, group, local, order), admin),
            HttpStatusCode.Created);

    private async Task<AdminCurriculumTopicHeritagesDto> SetHeritagesAsync(string admin, int topicId, params long[] heritageIds) =>
        await DiscoveryTestKit.Expect(Http.PutAsync<AdminCurriculumTopicHeritagesDto>($"{Adm}/{topicId}/heritages", new { heritageIds }, admin));

    private Task<Envelope<object>> FailAsync(Task<(HttpResponseMessage Response, Envelope<object> Body)> call, HttpStatusCode status, string code) =>
        DiscoveryTestKit.Fail(call, status, code);

    private async Task<List<TeacherCurriculumTopicDto>> TeacherListAsync(string token, int? grade = null) =>
        (await DiscoveryTestKit.Expect(Http.GetAsync<TeacherCurriculumTopicListDto>(grade is null ? Tea : $"{Tea}?grade={grade}", token))).Items.ToList();

    private async Task<List<AdminCurriculumTopicDto>> AdminListAsync(string admin, string query = "") =>
        (await DiscoveryTestKit.Expect(Http.GetAsync<AdminCurriculumTopicListDto>($"{Adm}{query}", admin))).Items.ToList();

    private Task<List<AuditLog>> AuditAsync(AuditAction action, long topicId) =>
        factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == action && a.TargetId == topicId).OrderBy(a => a.Id).ToListAsync());

    // ---- Thêm, sửa ----

    [Fact]
    public async Task Admin_adds_a_topic_that_starts_active_with_no_heritages_and_the_creation_is_logged()
    {
        var admin = await AdminAsync();
        var title = "  Chủ đề thêm mới " + Unique() + "  ";

        var topic = await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>(Adm,
            Body(title, 5, "Lịch sử và Địa lí", " Vùng đất Nam Bộ ", local: true, order: 7, description: "  ", sourceUrl: " https://example.org/nguon "), admin), HttpStatusCode.Created);

        Assert.Equal((title.Trim(), 5, "Lịch sử và Địa lí", "Vùng đất Nam Bộ", true, 7, true, 0, null, "https://example.org/nguon"),
            (topic.Title, topic.Grade, topic.Subject, topic.GroupTitle, topic.LocalProvince, topic.SortOrder, topic.IsActive, topic.HeritageCount,
                topic.Description, topic.SourceUrl));
        Assert.Contains(await AdminListAsync(admin, "?grade=5"), t => t.Id == topic.Id);

        var log = Assert.Single(await AuditAsync(AuditAction.CURRICULUM_TOPIC_CREATED, topic.Id));
        Assert.Equal((AuditTargets.CurriculumTopics, ActorRole.ADMIN), (log.TargetType, log.ActorRole));
        Assert.Contains(topic.Title, log.NewValue);
    }

    [Fact]
    public async Task A_topic_with_the_same_grade_subject_group_and_title_is_a_conflict_ignoring_case_and_accents_but_other_groups_or_grades_are_fine()
    {
        var admin = await AdminAsync();
        var title = "Thăng Long " + Unique();
        var first = await CreateAsync(admin, 4, title, "Lịch sử và Địa lí", "Đồng bằng Bắc Bộ");

        var (res, body) = await Http.PostAsync<object>(Adm, Body(title.ToUpperInvariant(), 4, "LICH SU VA DIA LI", "dong bang bac bo"), admin);
        Assert.Equal((HttpStatusCode.Conflict, MessageCodes.Duplicate), (res.StatusCode, body.ErrorCode));
        Assert.Contains(title.ToUpperInvariant(), body.Errors!["title"]);
        Assert.Equal(body.Message, body.Errors["title"]);

        await CreateAsync(admin, 4, title, "Lịch sử và Địa lí", "Nhóm khác");
        await CreateAsync(admin, 5, title, "Lịch sử và Địa lí", "Đồng bằng Bắc Bộ");
        Assert.Equal(1, (await AdminListAsync(admin, "?grade=4")).Count(t => t.Id == first.Id));
    }

    [Theory]
    [InlineData("subject", "", "subject", "Vui lòng nhập môn học.")]
    [InlineData("grade", 0, "grade", "Khối không hợp lệ: phải là số nguyên từ 1 đến 12.")]
    [InlineData("grade", 13, "grade", "Khối không hợp lệ: phải là số nguyên từ 1 đến 12.")]
    [InlineData("groupTitle", " ", "groupTitle", "Vui lòng nhập chủ đề lớn.")]
    [InlineData("title", "", "title", "Vui lòng nhập nội dung chủ đề.")]
    [InlineData("sortOrder", -1, "sortOrder", "Thứ tự không hợp lệ: phải là số nguyên từ 0 đến 1.000.000.")]
    [InlineData("sortOrder", 1_000_001, "sortOrder", "Thứ tự không hợp lệ: phải là số nguyên từ 0 đến 1.000.000.")]
    public async Task Invalid_fields_are_rejected_per_field_in_vietnamese(string field, object value, string errorField, string message)
    {
        var admin = await AdminAsync();
        var body = new Dictionary<string, object?>
        {
            ["subject"] = "Lịch sử", ["grade"] = 8, ["groupTitle"] = "Nhóm", ["title"] = "Chủ đề " + Unique(), ["localProvince"] = false, ["sortOrder"] = 1,
        };
        body[field] = value;

        var (res, resp) = await Http.PostAsync<object>(Adm, body, admin);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(message, resp.Errors![errorField]);
    }

    [Fact]
    public async Task Lengths_follow_the_columns_and_missing_required_numbers_and_flags_are_reported()
    {
        var admin = await AdminAsync();
        string Ok(int n) => new('a', n);

        async Task<Dictionary<string, string>> Errors(object body)
        {
            var (res, resp) = await Http.PostAsync<object>(Adm, body, admin);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return resp.Errors!;
        }

        Assert.Equal("Môn học không hợp lệ: tối đa 100 ký tự.", (await Errors(Body("t" + Unique(), subject: Ok(101))))["subject"]);
        Assert.Equal("Chủ đề lớn không hợp lệ: tối đa 255 ký tự.", (await Errors(Body("t" + Unique(), group: Ok(256))))["groupTitle"]);
        Assert.Equal("Nội dung chủ đề không hợp lệ: tối đa 255 ký tự.", (await Errors(Body(Ok(256))))["title"]);
        Assert.Equal("Mô tả không hợp lệ: tối đa 1000 ký tự.", (await Errors(Body("t" + Unique(), description: Ok(1001))))["description"]);
        Assert.Equal("Nguồn không hợp lệ: tối đa 500 ký tự.", (await Errors(Body("t" + Unique(), sourceUrl: Ok(501))))["sourceUrl"]);

        var missing = await Errors(new { subject = "Lịch sử", groupTitle = "Nhóm", title = "t" + Unique() });
        Assert.Equal(["grade", "localProvince", "sortOrder"], missing.Keys.Order().ToArray());

        // Đúng giới hạn thì lưu được.
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>(Adm,
            Body(Ok(255), 8, Ok(100), Ok(255), description: Ok(1000), sourceUrl: Ok(500)), admin), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Admin_edits_a_topic_keeping_its_state_and_heritages_and_the_edit_is_logged_with_old_and_new_values()
    {
        var admin = await AdminAsync();
        var h = await _l.HeritageAsync("Di sản sửa chủ đề");
        var topic = await CreateAsync(admin, 8);
        await SetHeritagesAsync(admin, topic.Id, h.Id);
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>($"{Adm}/{topic.Id}/deactivate", null, admin));
        var newTitle = "Đã sửa " + Unique();

        var edited = await DiscoveryTestKit.Expect(Http.PutAsync<AdminCurriculumTopicDto>($"{Adm}/{topic.Id}", Body(newTitle, 9, "Lịch sử", "Nhóm mới", order: 3), admin));

        Assert.Equal((newTitle, 9, "Nhóm mới", 3, false, 1), (edited.Title, edited.Grade, edited.GroupTitle, edited.SortOrder, edited.IsActive, edited.HeritageCount));
        var log = Assert.Single(await AuditAsync(AuditAction.CURRICULUM_TOPIC_UPDATED, topic.Id));
        Assert.Contains(topic.Title, log.OldValue);
        Assert.Contains(newTitle, log.NewValue);
    }

    [Fact]
    public async Task Saving_a_topic_unchanged_is_fine_but_matching_another_topic_is_a_conflict_and_an_unknown_topic_is_not_found()
    {
        var admin = await AdminAsync();
        var a = await CreateAsync(admin, 8, "Chủ đề A " + Unique());
        var b = await CreateAsync(admin, 8, "Chủ đề B " + Unique());

        await DiscoveryTestKit.Expect(Http.PutAsync<AdminCurriculumTopicDto>($"{Adm}/{a.Id}", Body(a.Title, 8, a.Subject, a.GroupTitle), admin));
        var (res, body) = await Http.PutAsync<object>($"{Adm}/{b.Id}", Body(a.Title, 8, a.Subject, a.GroupTitle), admin);
        Assert.Equal((HttpStatusCode.Conflict, MessageCodes.Duplicate), (res.StatusCode, body.ErrorCode));

        await FailAsync(Http.PutAsync<object>($"{Adm}/999999", Body("x" + Unique()), admin), HttpStatusCode.NotFound, MessageCodes.Blocked);
        await FailAsync(Http.PostAsync<object>($"{Adm}/999999/deactivate", null, admin), HttpStatusCode.NotFound, MessageCodes.Blocked);
        await FailAsync(Http.GetAsync<object>($"{Adm}/999999/heritages", admin), HttpStatusCode.NotFound, MessageCodes.Blocked);
        await FailAsync(Http.PutAsync<object>($"{Adm}/999999/heritages", new { heritageIds = Array.Empty<long>() }, admin), HttpStatusCode.NotFound, MessageCodes.Blocked);
    }

    // ---- Ngừng dùng, dùng lại ----

    [Fact]
    public async Task A_deactivated_topic_is_hidden_from_teachers_but_keeps_its_links_and_comes_back_when_reactivated()
    {
        var admin = await AdminAsync();
        var school = await _l.Classroom.SchoolAsync();
        var h = await _l.HeritageAsync("Di sản chủ đề ngừng");
        var topic = await CreateAsync(admin, 7);
        await SetHeritagesAsync(admin, topic.Id, h.Id);
        Assert.Contains(await TeacherListAsync(school.TeacherToken, 7), t => t.Id == topic.Id);

        var off = await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>($"{Adm}/{topic.Id}/deactivate", null, admin));
        Assert.False(off.IsActive);
        Assert.Equal(1, off.HeritageCount);
        Assert.DoesNotContain(await TeacherListAsync(school.TeacherToken, 7), t => t.Id == topic.Id);
        await FailAsync(Http.GetAsync<object>($"{Tea}/{topic.Id}/heritages", school.TeacherToken), HttpStatusCode.NotFound, MessageCodes.Blocked);
        Assert.DoesNotContain(await AdminListAsync(admin, "?grade=7"), t => t.Id == topic.Id);
        Assert.Contains(await AdminListAsync(admin, "?grade=7&includeInactive=true"), t => t.Id == topic.Id && !t.IsActive && t.HeritageCount == 1);

        var on = await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>($"{Adm}/{topic.Id}/activate", null, admin));
        Assert.True(on.IsActive);
        var shown = await DiscoveryTestKit.Expect(Http.GetAsync<TeacherCurriculumTopicHeritagesDto>($"{Tea}/{topic.Id}/heritages", school.TeacherToken));
        Assert.Equal([h.Id], shown.Items.Select(i => i.Id).ToArray());

        Assert.Single(await AuditAsync(AuditAction.CURRICULUM_TOPIC_DEACTIVATED, topic.Id));
        Assert.Single(await AuditAsync(AuditAction.CURRICULUM_TOPIC_ACTIVATED, topic.Id));
    }

    [Fact]
    public async Task Deactivating_or_activating_twice_changes_nothing_and_logs_once()
    {
        var admin = await AdminAsync();
        var topic = await CreateAsync(admin, 6);

        await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>($"{Adm}/{topic.Id}/activate", null, admin));
        Assert.Empty(await AuditAsync(AuditAction.CURRICULUM_TOPIC_ACTIVATED, topic.Id));
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>($"{Adm}/{topic.Id}/deactivate", null, admin));
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>($"{Adm}/{topic.Id}/deactivate", null, admin));
        Assert.Single(await AuditAsync(AuditAction.CURRICULUM_TOPIC_DEACTIVATED, topic.Id));
    }

    // ---- Danh sách theo khối ----

    [Fact]
    public async Task Topics_are_filtered_by_grade_and_ordered_by_the_curriculum_order_and_a_bad_grade_is_rejected()
    {
        var admin = await AdminAsync();
        var school = await _l.Classroom.SchoolAsync();
        var tag = Unique();
        var late = await CreateAsync(admin, 12, "Muộn " + tag, order: 20);
        var early = await CreateAsync(admin, 12, "Sớm " + tag, order: 10);
        var other = await CreateAsync(admin, 11, "Khối khác " + tag, order: 1);

        var grade12 = (await TeacherListAsync(school.TeacherToken, 12)).Where(t => t.Title.EndsWith(tag)).Select(t => t.Id).ToList();
        Assert.Equal([early.Id, late.Id], grade12);
        var all = (await TeacherListAsync(school.TeacherToken)).Where(t => t.Title.EndsWith(tag)).Select(t => t.Id).ToList();
        Assert.Equal([other.Id, early.Id, late.Id], all);

        await FailAsync(Http.GetAsync<object>($"{Tea}?grade=0", school.TeacherToken), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await FailAsync(Http.GetAsync<object>($"{Adm}?grade=13", admin), HttpStatusCode.BadRequest, MessageCodes.Invalid);
    }

    // ---- Di sản của chủ đề ----

    [Fact]
    public async Task Admin_replaces_the_heritage_list_including_drafts_and_archived_ones_and_teachers_only_see_published_ones()
    {
        var admin = await AdminAsync();
        var school = await _l.Classroom.SchoolAsync();
        var published = await _l.HeritageAsync("Di sản xuất bản");
        var archived = await _l.HeritageAsync("Di sản đã ẩn");
        await _l.ArchiveHeritageAsync(archived.Id);
        var draft = await _l.Discovery.DraftAsync(admin, new DiscoveryTestKit.Spec("Di sản nháp " + Unique()));
        var topic = await CreateAsync(admin, 10);

        var set = await SetHeritagesAsync(admin, topic.Id, published.Id, archived.Id, draft.Id);

        Assert.Equal(3, set.Topic.HeritageCount);
        Assert.Equal([ContentStatus.DRAFT, ContentStatus.PUBLISHED, ContentStatus.ARCHIVED], set.Items.Select(i => i.Status!.Value).Order().ToArray());
        Assert.Equal(set.Items.Select(i => i.Id).Order().ToArray(), (await DiscoveryTestKit.Expect(Http.GetAsync<AdminCurriculumTopicHeritagesDto>($"{Adm}/{topic.Id}/heritages", admin)))
            .Items.Select(i => i.Id).Order().ToArray());
        Assert.All(set.Items, i => Assert.False(string.IsNullOrEmpty(i.Slug)));
        Assert.All(set.Items, i => Assert.Equal("Thành phố Hà Nội", i.ProvinceName));

        // Giáo viên: chỉ di sản đang xuất bản, thẻ như danh sách công khai.
        var shown = await DiscoveryTestKit.Expect(Http.GetAsync<TeacherCurriculumTopicHeritagesDto>($"{Tea}/{topic.Id}/heritages", school.TeacherToken));
        var card = Assert.Single(shown.Items);
        Assert.Equal((published.Id, published.Slug, published.Name, "Thành phố Hà Nội", HeritageType.TANGIBLE, RecognitionLevel.NATIONAL),
            (card.Id, card.Slug, card.Name, card.ProvinceName, card.HeritageType, card.RecognitionLevel));
        Assert.False(string.IsNullOrEmpty(card.ThumbnailUrl));
        Assert.Equal((topic.Id, 1), (shown.Topic.Id, shown.Topic.HeritageCount));
        Assert.Equal(1, (await TeacherListAsync(school.TeacherToken, 10)).Single(t => t.Id == topic.Id).HeritageCount);
        Assert.Equal(3, (await AdminListAsync(admin, "?grade=10")).Single(t => t.Id == topic.Id).HeritageCount);

        // Thay danh sách: bỏ hai di sản, giữ một; danh sách rỗng gỡ hết.
        var again = await SetHeritagesAsync(admin, topic.Id, draft.Id);
        Assert.Equal([draft.Id], again.Items.Select(i => i.Id).ToArray());
        Assert.Empty((await SetHeritagesAsync(admin, topic.Id)).Items);

        var logs = await AuditAsync(AuditAction.CURRICULUM_TOPIC_HERITAGES_CHANGED, topic.Id);
        Assert.Equal(3, logs.Count);
        Assert.Contains(published.Id.ToString(), logs[0].NewValue);
        Assert.Contains(archived.Id.ToString(), logs[1].NewValue);
    }

    [Fact]
    public async Task Saving_the_same_heritage_list_again_is_not_logged_and_invalid_lists_are_rejected()
    {
        var admin = await AdminAsync();
        var h1 = await _l.HeritageAsync("Di sản một");
        var h2 = await _l.HeritageAsync("Di sản hai");
        var topic = await CreateAsync(admin, 9);
        await SetHeritagesAsync(admin, topic.Id, h1.Id, h2.Id);
        await SetHeritagesAsync(admin, topic.Id, h2.Id, h1.Id);
        Assert.Single(await AuditAsync(AuditAction.CURRICULUM_TOPIC_HERITAGES_CHANGED, topic.Id));

        var dup = await DiscoveryTestKit.Fail(Http.PutAsync<object>($"{Adm}/{topic.Id}/heritages", new { heritageIds = new[] { h1.Id, h1.Id } }, admin),
            HttpStatusCode.BadRequest, MessageCodes.Invalid);
        Assert.Equal("Danh sách di sản không hợp lệ: mỗi di sản chỉ được chọn một lần.", dup.Errors!["heritageIds"]);

        var missing = await DiscoveryTestKit.Fail(Http.PutAsync<object>($"{Adm}/{topic.Id}/heritages", new { heritageIds = new[] { h1.Id, 987654321L } }, admin),
            HttpStatusCode.BadRequest, MessageCodes.Invalid);
        Assert.Equal("Danh sách di sản không hợp lệ: có 1 di sản không tồn tại hoặc đã bị xóa.", missing.Errors!["heritageIds"]);

        var none = await DiscoveryTestKit.Fail(Http.PutAsync<object>($"{Adm}/{topic.Id}/heritages", new { }, admin), HttpStatusCode.BadRequest, MessageCodes.Required);
        Assert.Equal("Vui lòng nhập danh sách di sản của chủ đề.", none.Errors!["heritageIds"]);

        // Lỗi không làm đổi danh sách đang có.
        Assert.Equal(2, (await DiscoveryTestKit.Expect(Http.GetAsync<AdminCurriculumTopicHeritagesDto>($"{Adm}/{topic.Id}/heritages", admin))).Items.Count);
    }

    [Fact]
    public async Task A_topic_takes_at_most_200_distinct_heritages()
    {
        var admin = await AdminAsync();
        var topic = await CreateAsync(admin, 9);

        var tooMany = await DiscoveryTestKit.Fail(Http.PutAsync<object>($"{Adm}/{topic.Id}/heritages", new { heritageIds = Enumerable.Range(1, 201).Select(i => (long)i).ToArray() }, admin),
            HttpStatusCode.BadRequest, MessageCodes.Invalid);
        Assert.Equal("Danh sách di sản không hợp lệ: tối đa 200 di sản cho một chủ đề.", tooMany.Errors!["heritageIds"]);

        // 200 mã không có thật vẫn bị từ chối vì di sản không tồn tại, không phải vì quá số lượng.
        var ghost = await DiscoveryTestKit.Fail(Http.PutAsync<object>($"{Adm}/{topic.Id}/heritages", new { heritageIds = Enumerable.Range(900_000, 200).Select(i => (long)i).ToArray() }, admin),
            HttpStatusCode.BadRequest, MessageCodes.Invalid);
        Assert.Equal("Danh sách di sản không hợp lệ: có 200 di sản không tồn tại hoặc đã bị xóa.", ghost.Errors!["heritageIds"]);
    }

    [Fact]
    public async Task Soft_deleted_heritages_drop_out_of_the_counts_and_the_lists_and_cannot_be_linked()
    {
        var admin = await AdminAsync();
        var school = await _l.Classroom.SchoolAsync();
        var keep = await _l.HeritageAsync("Di sản giữ");
        var gone = await _l.HeritageAsync("Di sản bị xóa");
        var topic = await CreateAsync(admin, 4);
        await SetHeritagesAsync(admin, topic.Id, keep.Id, gone.Id);

        await factory.WithDbAsync(async db =>
        {
            (await db.Heritages.SingleAsync(x => x.Id == gone.Id)).DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });

        Assert.Equal(1, (await AdminListAsync(admin, "?grade=4")).Single(t => t.Id == topic.Id).HeritageCount);
        Assert.Equal([keep.Id], (await DiscoveryTestKit.Expect(Http.GetAsync<AdminCurriculumTopicHeritagesDto>($"{Adm}/{topic.Id}/heritages", admin))).Items.Select(i => i.Id).ToArray());
        Assert.Equal([keep.Id], (await DiscoveryTestKit.Expect(Http.GetAsync<TeacherCurriculumTopicHeritagesDto>($"{Tea}/{topic.Id}/heritages", school.TeacherToken)))
            .Items.Select(i => i.Id).ToArray());
        await DiscoveryTestKit.Fail(Http.PutAsync<object>($"{Adm}/{topic.Id}/heritages", new { heritageIds = new[] { keep.Id, gone.Id } }, admin),
            HttpStatusCode.BadRequest, MessageCodes.Invalid);
    }

    [Fact]
    public async Task The_local_province_topic_is_flagged_and_has_no_fixed_heritages()
    {
        var admin = await AdminAsync();
        var school = await _l.Classroom.SchoolAsync();
        var topic = await CreateAsync(admin, 3, "Địa phương em " + Unique(), group: "Địa phương em (tỉnh, thành phố)", local: true);

        var seen = (await TeacherListAsync(school.TeacherToken, 3)).Single(t => t.Id == topic.Id);

        Assert.True(seen.LocalProvince);
        Assert.Equal(0, seen.HeritageCount);
        Assert.Empty((await DiscoveryTestKit.Expect(Http.GetAsync<TeacherCurriculumTopicHeritagesDto>($"{Tea}/{topic.Id}/heritages", school.TeacherToken))).Items);
    }

    // ---- Chi tiết di sản của quản trị ----

    [Fact]
    public async Task Admin_heritage_detail_lists_the_active_topics_the_heritage_belongs_to()
    {
        var admin = await AdminAsync();
        var h = await _l.HeritageAsync("Di sản có chủ đề");
        var late = await CreateAsync(admin, 8, order: 9);
        var early = await CreateAsync(admin, 5, order: 5);
        var off = await CreateAsync(admin, 8, order: 1);
        foreach (var t in new[] { late, early, off }) await SetHeritagesAsync(admin, t.Id, h.Id);
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>($"{Adm}/{off.Id}/deactivate", null, admin));

        var detail = await _l.HeritageOnlyAsync(h.Id);

        Assert.Equal([early.Id, late.Id], detail.CurriculumTopics!.Select(t => t.Id).ToArray());
        var first = detail.CurriculumTopics![0];
        Assert.Equal((early.Subject, early.Grade, early.GroupTitle, early.Title), (first.Subject, first.Grade, first.GroupTitle, first.Title));
        Assert.Empty((await _l.HeritageOnlyAsync((await _l.HeritageAsync("Di sản không chủ đề")).Id)).CurriculumTopics!);
    }

    // ---- Chủ đề của bài học ----

    [Fact]
    public async Task A_lesson_shows_only_active_topics_of_its_own_grade_that_hold_one_of_its_heritages()
    {
        var admin = await AdminAsync();
        var (school, schoolClass) = await _l.SchoolWithClassAsync(2);   // lớp khối 8
        var h1 = await _l.HeritageAsync("Di sản bài một");
        var h2 = await _l.HeritageAsync("Di sản bài hai");
        var other = await _l.HeritageAsync("Di sản ngoài bài");
        var lesson = await _l.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [h1.Id, h2.Id], "Bài chủ đề " + Unique());
        Assert.Empty(lesson.CurriculumTopics!);

        var second = await CreateAsync(admin, 8, "Hai " + Unique(), order: 20);
        var first = await CreateAsync(admin, 8, "Một " + Unique(), order: 10);
        var otherGrade = await CreateAsync(admin, 9, "Khối khác " + Unique());
        var notInLesson = await CreateAsync(admin, 8, "Ngoài bài " + Unique());
        var inactive = await CreateAsync(admin, 8, "Ngừng " + Unique());
        await SetHeritagesAsync(admin, second.Id, h2.Id);
        await SetHeritagesAsync(admin, first.Id, h1.Id, h2.Id);   // hai di sản của bài cùng thuộc chủ đề: chỉ hiện một lần
        await SetHeritagesAsync(admin, otherGrade.Id, h1.Id);
        await SetHeritagesAsync(admin, notInLesson.Id, other.Id);
        await SetHeritagesAsync(admin, inactive.Id, h1.Id);
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>($"{Adm}/{inactive.Id}/deactivate", null, admin));

        var detail = await _l.GetAsync(school.TeacherToken, lesson.Id);

        Assert.Equal([first.Id, second.Id], detail.CurriculumTopics!.Select(t => t.Id).ToArray());
        var shown = detail.CurriculumTopics![0];
        Assert.Equal((first.Subject, 8, first.GroupTitle, first.Title), (shown.Subject, shown.Grade, shown.GroupTitle, shown.Title));

        // Đổi danh sách di sản của chủ đề thì chủ đề của bài đổi theo (tính khi xem, không lưu).
        await SetHeritagesAsync(admin, first.Id);
        Assert.Equal([second.Id], (await _l.GetAsync(school.TeacherToken, lesson.Id)).CurriculumTopics!.Select(t => t.Id).ToArray());
    }

    // ---- Thư viện bài mẫu ----

    private async Task<(long LessonId, long ClassId, string Token)> ApprovedSampleAsync(long heritageId)
    {
        var (school, schoolClass) = await _l.SchoolWithClassAsync(2);
        var lesson = await _l.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [heritageId], "Bài mẫu chủ đề " + Unique());
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonSampleResultDto>($"{LessonTestKit.Teacher}/{lesson.Id}/sample-request", null, school.TeacherToken));
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminLessonLibraryResultDto>($"{AdmLib}/{lesson.Id}/approve", null, await AdminAsync()));
        return (lesson.Id, schoolClass.Id, school.TeacherToken);
    }

    private async Task<List<LessonLibraryItemDto>> LibraryAsync(string token, string query) =>
        (await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<LessonLibraryItemDto>>($"{Lib}?{query}", token))).Items.ToList();

    [Fact]
    public async Task The_library_filters_by_topic_and_each_card_lists_the_topics_of_its_grade()
    {
        var admin = await AdminAsync();
        var h = await _l.HeritageAsync("Di sản thư viện chủ đề", events: 3);
        var (lessonId, _, token) = await ApprovedSampleAsync(h.Id);
        var (otherLessonId, _, _) = await ApprovedSampleAsync((await _l.HeritageAsync("Di sản thư viện khác", events: 3)).Id);

        var topic = await CreateAsync(admin, 8, "Thư viện " + Unique());
        var wrongGrade = await CreateAsync(admin, 9, "Thư viện khối 9 " + Unique());
        var empty = await CreateAsync(admin, 8, "Thư viện rỗng " + Unique());
        await SetHeritagesAsync(admin, topic.Id, h.Id);
        await SetHeritagesAsync(admin, wrongGrade.Id, h.Id);

        var filtered = await LibraryAsync(token, $"topicId={topic.Id}");
        var card = Assert.Single(filtered);
        Assert.Equal(lessonId, card.Id);
        Assert.Equal([topic.Id], card.CurriculumTopics!.Select(t => t.Id).ToArray());
        Assert.Equal(topic.Title, card.CurriculumTopics![0].Title);

        Assert.Empty(await LibraryAsync(token, $"topicId={wrongGrade.Id}"));   // chủ đề khác khối với lớp của bài
        Assert.Empty(await LibraryAsync(token, $"topicId={empty.Id}"));
        Assert.Empty(await LibraryAsync(token, "topicId=999999"));

        // Không lọc thì cả hai bài hiện; bài không có chủ đề có danh sách rỗng.
        var all = await LibraryAsync(token, "pageSize=100");
        Assert.Empty(all.Single(i => i.Id == otherLessonId).CurriculumTopics!);
        Assert.Equal([topic.Id], all.Single(i => i.Id == lessonId).CurriculumTopics!.Select(t => t.Id).ToArray());

        // Chi tiết xem trước cũng có chủ đề trên thẻ.
        var detail = await DiscoveryTestKit.Expect(Http.GetAsync<LessonLibraryDetailDto>($"{Lib}/{lessonId}", token));
        Assert.Equal([topic.Id], detail.Item.CurriculumTopics!.Select(t => t.Id).ToArray());

        // Chủ đề ngừng dùng thì không còn lọc được và không hiện trên thẻ.
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminCurriculumTopicDto>($"{Adm}/{topic.Id}/deactivate", null, admin));
        Assert.Empty(await LibraryAsync(token, $"topicId={topic.Id}"));
        Assert.Empty((await LibraryAsync(token, "pageSize=100")).Single(i => i.Id == lessonId).CurriculumTopics!);

        await FailAsync(Http.GetAsync<object>($"{Lib}?topicId=0", token), HttpStatusCode.BadRequest, MessageCodes.Invalid);
    }

    // ---- Quyền ----

    [Fact]
    public async Task Teachers_read_and_only_system_admins_manage_while_school_admins_students_and_visitors_are_turned_away()
    {
        var admin = await AdminAsync();
        var (school, schoolClass) = await _l.SchoolWithClassAsync(2);
        var studentToken = await _l.StudentTokenAsync(school.TeacherToken, schoolClass.Id);
        var topic = await CreateAsync(admin, 8);
        var body = Body("x" + Unique());

        // Giáo viên không quản lý.
        foreach (var call in new Func<Task<(HttpResponseMessage Response, Envelope<object> Body)>>[]
                 {
                     () => Http.GetAsync<object>(Adm, school.TeacherToken),
                     () => Http.PostAsync<object>(Adm, body, school.TeacherToken),
                     () => Http.PutAsync<object>($"{Adm}/{topic.Id}", body, school.TeacherToken),
                     () => Http.PostAsync<object>($"{Adm}/{topic.Id}/deactivate", null, school.TeacherToken),
                     () => Http.PostAsync<object>($"{Adm}/{topic.Id}/activate", null, school.TeacherToken),
                     () => Http.GetAsync<object>($"{Adm}/{topic.Id}/heritages", school.TeacherToken),
                     () => Http.PutAsync<object>($"{Adm}/{topic.Id}/heritages", new { heritageIds = Array.Empty<long>() }, school.TeacherToken),
                 })
            await FailAsync(call(), HttpStatusCode.Forbidden, MessageCodes.Forbidden);

        // Quản trị trường: không quản lý và cũng không đọc khu giáo viên.
        await FailAsync(Http.GetAsync<object>(Adm, school.OrgAdminToken), HttpStatusCode.Forbidden, MessageCodes.Forbidden);
        await FailAsync(Http.PostAsync<object>(Adm, body, school.OrgAdminToken), HttpStatusCode.Forbidden, MessageCodes.Forbidden);
        await FailAsync(Http.GetAsync<object>(Tea, school.OrgAdminToken), HttpStatusCode.Forbidden, MessageCodes.Forbidden);
        await FailAsync(Http.GetAsync<object>($"{Tea}/{topic.Id}/heritages", school.OrgAdminToken), HttpStatusCode.Forbidden, MessageCodes.Forbidden);

        // Quản trị hệ thống không dùng khu giáo viên; học sinh không vào được cả hai.
        await FailAsync(Http.GetAsync<object>(Tea, admin), HttpStatusCode.Forbidden, MessageCodes.Forbidden);
        await FailAsync(Http.GetAsync<object>(Tea, studentToken), HttpStatusCode.Forbidden, MessageCodes.Forbidden);
        await FailAsync(Http.GetAsync<object>(Adm, studentToken), HttpStatusCode.Forbidden, MessageCodes.Forbidden);

        // Chưa đăng nhập.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync<object>(Tea)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync<object>(Adm)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.PostAsync<object>(Adm, body)).Response.StatusCode);

        // Sau mọi lần bị từ chối, chủ đề không đổi.
        Assert.Contains(await AdminListAsync(admin, "?grade=8"), t => t.Id == topic.Id && t.Title == topic.Title);
    }
}
