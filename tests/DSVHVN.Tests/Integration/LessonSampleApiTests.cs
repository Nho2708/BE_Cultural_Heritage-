using System.Net;
using System.Text.Json;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Thư viện bài mẫu dùng chung qua HTTP: giáo viên đề xuất và rút, quản trị hệ thống duyệt hoặc từ chối, bài đang ở thư viện sửa thì tự về chờ duyệt,
/// thư viện chỉ hiện đúng bài, dùng bài mẫu (nhân bản sang lớp của mình, kèm bài kiểm tra), quyền theo vai trò và nhật ký thao tác.
/// </summary>
public sealed class LessonSampleApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string T = LessonTestKit.Teacher;
    private const string Lib = "/api/v1/teacher/lesson-library";
    private const string Adm = "/api/v1/admin/lesson-library";

    private readonly QuizTestKit _q = new(factory);
    private LessonTestKit L => _q.Questions.Lesson;
    private HttpClient Http => L.Http;

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

    private static void AssertFail(HttpResponseMessage res, Envelope<object> body, HttpStatusCode status, string code)
    {
        Assert.Equal((status, code), (res.StatusCode, body.ErrorCode));
        Assert.False(body.Success);
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private Task<string> AdminTokenAsync() => L.Discovery.Api.AdminTokenAsync();

    /// <summary>Trường có giáo viên, lớp theo cấp, di sản và một bài học đã xuất bản (có tiêu đề riêng, chưa ở thư viện).</summary>
    private async Task<QuizTestKit.Setup> SetupAsync(int level = 2, string? title = null, bool publish = true)
    {
        var (school, schoolClass) = await L.SchoolWithClassAsync(level);
        var (heritage, images) = await _q.Questions.HeritageAsync();
        title ??= "Bài mẫu " + Unique();
        string? code = null;
        long lessonId;
        if (publish)
        {
            var lesson = await L.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id], title);
            (lessonId, code) = (lesson.Id, lesson.AccessCode);
        }
        else
        {
            lessonId = (await L.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id], title)).Id;
        }
        return new QuizTestKit.Setup(school, schoolClass.Id, level, heritage, images, lessonId, code);
    }

    private Task<(HttpResponseMessage Response, Envelope<LessonSampleResultDto> Body)> RequestRawAsync(string token, long lessonId) =>
        Http.PostAsync<LessonSampleResultDto>($"{T}/{lessonId}/sample-request", null, token);

    private async Task<LessonSampleResultDto> RequestAsync(string token, long lessonId) =>
        await DiscoveryTestKit.Expect(RequestRawAsync(token, lessonId));

    private async Task<AdminLessonLibraryResultDto> ApproveAsync(long lessonId) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminLessonLibraryResultDto>($"{Adm}/{lessonId}/approve", null, await AdminTokenAsync()));

    private async Task<QuizTestKit.Setup> ApprovedAsync(int level = 2, string? title = null)
    {
        var s = await SetupAsync(level, title);
        await RequestAsync(s.Token, s.LessonId);
        await ApproveAsync(s.LessonId);
        return s;
    }

    private async Task<PagedResult<LessonLibraryItemDto>> LibraryAsync(string token, string query = "") =>
        await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<LessonLibraryItemDto>>($"{Lib}{(query.Length == 0 ? "" : "?" + query)}", token));

    private Task<List<DSVHVN.Domain.Audit.AuditLog>> AuditAsync(AuditAction action, long lessonId) =>
        factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == action && a.TargetId == lessonId).OrderBy(a => a.Id).ToListAsync());

    private Task SetLessonAsync(long lessonId, Action<DSVHVN.Domain.Lessons.Lesson> change) =>
        factory.WithDbAsync(async db =>
        {
            change(await db.Lessons.SingleAsync(l => l.Id == lessonId));
            return await db.SaveChangesAsync();
        });

    // ---- Đề xuất và rút ----

    [Fact]
    public async Task A_published_ready_lesson_can_be_requested_and_shows_as_pending_in_the_detail_and_the_list_but_not_in_the_library()
    {
        var s = await SetupAsync(2, "Bài mẫu đề xuất " + Unique());
        Assert.Null((await L.GetAsync(s.Token, s.LessonId)).Sample);
        var usedBefore = (await L.GetAsync(s.Token, s.LessonId)).AiQuota.Used;

        var result = await RequestAsync(s.Token, s.LessonId);

        var sample = result.Lesson.Sample!;
        Assert.Equal(LessonSampleStatus.PENDING, sample.Status);
        Assert.NotNull(sample.RequestedAt);
        Assert.Null(sample.ReviewedAt);
        Assert.Null(sample.Note);
        Assert.Equal(0, sample.UseCount);
        Assert.StartsWith("Đã gửi đề xuất.", result.Message);
        Assert.Equal(LessonSampleStatus.PENDING, (await L.GetAsync(s.Token, s.LessonId)).Sample!.Status);

        var list = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{T}?classId={s.ClassId}", s.Token));
        Assert.Equal(LessonSampleStatus.PENDING, list.Items.Single(i => i.Id == s.LessonId).Sample!.Status);

        // Chưa duyệt thì chưa ở thư viện; đề xuất không tốn lượt AI.
        var other = await SetupAsync(2);
        Assert.DoesNotContain((await LibraryAsync(other.Token, $"heritageId={s.Heritage.Id}")).Items, i => i.Id == s.LessonId);
        Assert.Equal(usedBefore, (await L.GetAsync(s.Token, s.LessonId)).AiQuota.Used);

        var audit = Assert.Single(await AuditAsync(AuditAction.LESSON_SAMPLE_REQUESTED, s.LessonId));
        Assert.Equal((s.School.Teacher.Id, "lessons"), (audit.UserId, audit.TargetType));
        Assert.Equal(DSVHVN.Domain.Enums.ActorRole.TEACHER, audit.ActorRole);
    }

    [Fact]
    public async Task Draft_archived_pending_and_approved_lessons_are_blocked_and_a_published_lesson_that_is_not_ready_is_not_ready()
    {
        var draft = await SetupAsync(2, publish: false);
        var (res, body) = await Http.PostAsync<object>($"{T}/{draft.LessonId}/sample-request", null, draft.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("đã xuất bản", body.Message);

        var archived = await SetupAsync();
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"{T}/{archived.LessonId}/archive", null, archived.Token));
        (res, body) = await Http.PostAsync<object>($"{T}/{archived.LessonId}/sample-request", null, archived.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("lưu trữ", body.Message);

        var pending = await SetupAsync();
        await RequestAsync(pending.Token, pending.LessonId);
        (res, body) = await Http.PostAsync<object>($"{T}/{pending.LessonId}/sample-request", null, pending.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("chờ quản trị", body.Message);
        await ApproveAsync(pending.LessonId);
        (res, body) = await Http.PostAsync<object>($"{T}/{pending.LessonId}/sample-request", null, pending.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("đã ở trong thư viện", body.Message);

        var notReady = await SetupAsync();
        await SetLessonAsync(notReady.LessonId, l => l.Summary = null);
        (res, body) = await Http.PostAsync<object>($"{T}/{notReady.LessonId}/sample-request", null, notReady.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.Contains("tóm tắt", body.Message);
        Assert.Null((await L.GetAsync(notReady.Token, notReady.LessonId)).Sample);
    }

    [Fact]
    public async Task Only_the_owner_teacher_can_request_or_withdraw_and_other_roles_are_refused()
    {
        var s = await SetupAsync();
        var (_, sameSchoolToken) = await L.Classroom.AnotherTeacherAsync(s.School);
        var otherSchool = await SetupAsync();
        var studentToken = await L.StudentTokenAsync(s.Token, s.ClassId);

        foreach (var token in new[] { sameSchoolToken, otherSchool.Token })
        {
            var (res, body) = await Http.PostAsync<object>($"{T}/{s.LessonId}/sample-request", null, token);
            AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
            (res, body) = await Http.DeleteAsync<object>($"{T}/{s.LessonId}/sample-request", token);
            AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
        }
        foreach (var token in new[] { s.School.OrgAdminToken, studentToken })
        {
            var (res, _) = await Http.PostAsync<object>($"{T}/{s.LessonId}/sample-request", null, token);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        var (anonymous, _) = await Http.PostAsync<object>($"{T}/{s.LessonId}/sample-request", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null((await L.GetAsync(s.Token, s.LessonId)).Sample);
    }

    [Fact]
    public async Task Withdrawing_works_from_every_state_clears_the_review_and_is_blocked_when_the_lesson_is_not_in_the_library()
    {
        var s = await SetupAsync();
        var (res, body) = await Http.DeleteAsync<object>($"{T}/{s.LessonId}/sample-request", s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("chưa ở trong thư viện", body.Message);

        // Từ chờ duyệt.
        await RequestAsync(s.Token, s.LessonId);
        var withdrawn = await DiscoveryTestKit.Expect(Http.DeleteAsync<LessonSampleResultDto>($"{T}/{s.LessonId}/sample-request", s.Token));
        Assert.Null(withdrawn.Lesson.Sample);
        Assert.Equal("Đã rút bài học khỏi thư viện bài mẫu.", withdrawn.Message);

        // Từ đã trong thư viện: không còn ở thư viện.
        await RequestAsync(s.Token, s.LessonId);
        await ApproveAsync(s.LessonId);
        var other = await SetupAsync();
        Assert.Contains((await LibraryAsync(other.Token, $"heritageId={s.Heritage.Id}")).Items, i => i.Id == s.LessonId);
        await DiscoveryTestKit.Expect(Http.DeleteAsync<LessonSampleResultDto>($"{T}/{s.LessonId}/sample-request", s.Token));
        Assert.Empty((await LibraryAsync(other.Token, $"heritageId={s.Heritage.Id}")).Items);

        // Từ bị từ chối: xóa cả lý do, người duyệt, ngày duyệt.
        await RequestAsync(s.Token, s.LessonId);
        await Reject(s.LessonId, "Nội dung chưa đủ tốt.");
        Assert.Equal("Nội dung chưa đủ tốt.", (await L.GetAsync(s.Token, s.LessonId)).Sample!.Note);
        await DiscoveryTestKit.Expect(Http.DeleteAsync<LessonSampleResultDto>($"{T}/{s.LessonId}/sample-request", s.Token));
        var row = await factory.WithDbAsync(db => db.Lessons.AsNoTracking().SingleAsync(l => l.Id == s.LessonId));
        Assert.Equal((null, null, null, null, null), (row.SampleStatus, row.SampleRequestedAt, row.SampleReviewedAt, row.SampleReviewedBy, row.SampleNote));
        Assert.Equal(3, (await AuditAsync(AuditAction.LESSON_SAMPLE_WITHDRAWN, s.LessonId)).Count);
    }

    // ---- Duyệt, từ chối, thư viện ----

    private async Task<AdminLessonLibraryResultDto> Reject(long lessonId, string note) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<AdminLessonLibraryResultDto>($"{Adm}/{lessonId}/reject", new { note }, await AdminTokenAsync()));

    [Fact]
    public async Task Approving_puts_the_lesson_in_the_library_with_the_author_the_school_and_the_card_fields_but_no_contact_information()
    {
        var s = await SetupAsync(2, "Bài mẫu hiển thị " + Unique());
        await RequestAsync(s.Token, s.LessonId);
        var pendingToo = await SetupAsync();
        await RequestAsync(pendingToo.Token, pendingToo.LessonId);
        var draft = await SetupAsync(publish: false);

        var approved = await ApproveAsync(s.LessonId);

        Assert.Equal(LessonSampleStatus.APPROVED, approved.Item.Status);
        Assert.NotNull(approved.Item.ReviewedAt);
        Assert.Contains("Đã duyệt bài mẫu", approved.Message);
        var sample = (await L.GetAsync(s.Token, s.LessonId)).Sample!;
        Assert.Equal((LessonSampleStatus.APPROVED, 0), (sample.Status, sample.UseCount));
        var adminId = await factory.WithDbAsync(db => db.Users.Where(u => u.Username == ApiFactory.AdminUsername).Select(u => u.Id).SingleAsync());
        var row = await factory.WithDbAsync(db => db.Lessons.AsNoTracking().SingleAsync(l => l.Id == s.LessonId));
        Assert.Equal((adminId, null), (row.SampleReviewedBy, row.SampleNote));
        Assert.Single(await AuditAsync(AuditAction.LESSON_SAMPLE_APPROVED, s.LessonId));

        var viewer = await SetupAsync(1);
        var page = await LibraryAsync(viewer.Token, $"heritageId={s.Heritage.Id}");
        var item = Assert.Single(page.Items);
        var lesson = await L.GetAsync(s.Token, s.LessonId);
        var orgName = await factory.WithDbAsync(db => db.Organizations.Where(o => o.Id == s.School.OrgId).Select(o => o.Name).SingleAsync());
        Assert.Equal((s.LessonId, lesson.Title, 2, 8), (item.Id, item.Title, item.SchoolLevel, item.Grade));
        Assert.Equal(s.Heritage.Id, Assert.Single(item.Heritages).Id);
        Assert.Equal((s.School.Teacher.FullName, orgName, 0, 0, false), (item.AuthorName, item.SchoolName, item.UseCount, item.QuizCount, item.IsMine));
        Assert.Equal(lesson.Sections.Count, item.SectionCount);
        Assert.InRange(item.SummaryExcerpt.Length, 1, 200);
        Assert.NotNull(item.ApprovedAt);
        var json = JsonSerializer.Serialize(item);
        Assert.DoesNotContain(s.School.Teacher.Email!, json);
        Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.Single((await LibraryAsync(s.Token, $"heritageId={s.Heritage.Id}")).Items).IsMine);

        // Chờ duyệt hoặc Nháp thì không hiện.
        Assert.Empty((await LibraryAsync(viewer.Token, $"heritageId={pendingToo.Heritage.Id}")).Items);
        Assert.Empty((await LibraryAsync(viewer.Token, $"heritageId={draft.Heritage.Id}")).Items);
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_of_1_to_500_characters_and_the_teacher_sees_it_and_can_request_again()
    {
        var s = await SetupAsync();
        await RequestAsync(s.Token, s.LessonId);
        var admin = await AdminTokenAsync();

        foreach (var note in new object?[] { null, "", "   ", new string('x', 501) })
        {
            var (bad, badBody) = await Http.PostAsync<object>($"{Adm}/{s.LessonId}/reject", new { note }, admin);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.True(badBody.Errors!.ContainsKey("note"));
        }
        Assert.Equal(LessonSampleStatus.PENDING, (await L.GetAsync(s.Token, s.LessonId)).Sample!.Status);

        var rejected = await Reject(s.LessonId, "  " + new string('y', 500) + "  ");
        Assert.Equal((LessonSampleStatus.REJECTED, 500), (rejected.Item.Status, rejected.Item.Note!.Length));
        Assert.Contains("Đã từ chối đề xuất", rejected.Message);
        var audit = Assert.Single(await AuditAsync(AuditAction.LESSON_SAMPLE_REJECTED, s.LessonId));
        Assert.Contains("REJECTED", audit.NewValue);

        var detail = (await L.GetAsync(s.Token, s.LessonId)).Sample!;
        Assert.Equal((LessonSampleStatus.REJECTED, 500), (detail.Status, detail.Note!.Length));
        Assert.NotNull(detail.ReviewedAt);

        // Đề xuất lại được: về chờ duyệt, lý do cũ bị xóa.
        var again = (await RequestAsync(s.Token, s.LessonId)).Lesson.Sample!;
        Assert.Equal((LessonSampleStatus.PENDING, null), (again.Status, again.Note));
        Assert.Null(again.ReviewedAt);
    }

    [Fact]
    public async Task Removing_an_approved_lesson_from_the_library_needs_a_reason_and_hides_it()
    {
        var s = await ApprovedAsync();
        var viewer = await SetupAsync();
        Assert.Single((await LibraryAsync(viewer.Token, $"heritageId={s.Heritage.Id}")).Items);

        var removed = await Reject(s.LessonId, "Gỡ vì có sai sót về năm tháng.");

        Assert.Contains("Đã gỡ bài khỏi thư viện", removed.Message);
        Assert.Empty((await LibraryAsync(viewer.Token, $"heritageId={s.Heritage.Id}")).Items);
        Assert.Equal(LessonSampleStatus.REJECTED, (await L.GetAsync(s.Token, s.LessonId)).Sample!.Status);
    }

    [Fact]
    public async Task Approve_and_reject_follow_the_state_rules_and_unknown_lessons_are_404()
    {
        var admin = await AdminTokenAsync();
        var s = await SetupAsync();

        // Chưa đề xuất: không ở thư viện nên 404.
        var (res, body) = await Http.PostAsync<object>($"{Adm}/{s.LessonId}/approve", null, admin);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
        (res, body) = await Http.PostAsync<object>($"{Adm}/999999/approve", null, admin);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
        (res, body) = await Http.PostAsync<object>($"{Adm}/999999/reject", new { note = "x" }, admin);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");

        await RequestAsync(s.Token, s.LessonId);
        await ApproveAsync(s.LessonId);
        (res, body) = await Http.PostAsync<object>($"{Adm}/{s.LessonId}/approve", null, admin);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");

        await Reject(s.LessonId, "Gỡ.");
        (res, body) = await Http.PostAsync<object>($"{Adm}/{s.LessonId}/reject", new { note = "Lần hai" }, admin);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        (res, body) = await Http.PostAsync<object>($"{Adm}/{s.LessonId}/approve", null, admin);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");

        // Đang chờ duyệt mà bài không còn đủ điều kiện: không duyệt được.
        var notReady = await SetupAsync();
        await RequestAsync(notReady.Token, notReady.LessonId);
        await SetLessonAsync(notReady.LessonId, l => l.Summary = null);
        (res, body) = await Http.PostAsync<object>($"{Adm}/{notReady.LessonId}/approve", null, admin);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.Equal(LessonSampleStatus.PENDING, (await L.GetAsync(notReady.Token, notReady.LessonId)).Sample!.Status);
    }

    // ---- Sửa, lưu trữ, xóa ----

    [Fact]
    public async Task Editing_an_approved_lesson_sends_it_back_to_pending_with_a_notice_but_saving_the_same_content_keeps_it_approved()
    {
        var s = await ApprovedAsync();
        var before = (await L.GetAsync(s.Token, s.LessonId));
        var requestedBefore = before.Sample!.RequestedAt;

        // Lưu lại đúng nội dung cũ: giữ nguyên đã duyệt, không có câu báo.
        var (same, sameBody) = await Http.PutAsync<LessonDetailDto>($"{T}/{s.LessonId}", new
        {
            title = before.Title, summary = before.Summary, keyPoints = before.KeyPoints, sections = before.Sections.Select(LessonTestKit.ToSection).ToList(),
        }, s.Token);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(LessonSampleStatus.APPROVED, sameBody.Data!.Sample!.Status);
        Assert.DoesNotContain("thư viện bài mẫu", sameBody.Message);
        var (_, titleOnly) = await Http.PutAsync<LessonDetailDto>($"{T}/{s.LessonId}", new { title = before.Title }, s.Token);
        Assert.Equal(LessonSampleStatus.APPROVED, titleOnly.Data!.Sample!.Status);

        // Đổi tiêu đề: về chờ duyệt, thời điểm đề xuất mới, xóa người và lúc duyệt.
        var (res, body) = await Http.PutAsync<LessonDetailDto>($"{T}/{s.LessonId}", new { title = "Tiêu đề đã đổi " + Unique() }, s.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.EndsWith("Bài đang ở thư viện bài mẫu nên sẽ chờ quản trị duyệt lại.", body.Message);
        Assert.StartsWith("Lưu bài học thành công.", body.Message);
        var sample = body.Data!.Sample!;
        Assert.Equal(LessonSampleStatus.PENDING, sample.Status);
        Assert.True(sample.RequestedAt > requestedBefore);
        Assert.Null(sample.ReviewedAt);
        var row = await factory.WithDbAsync(db => db.Lessons.AsNoTracking().SingleAsync(l => l.Id == s.LessonId));
        Assert.Null(row.SampleReviewedBy);

        // Không còn trong thư viện cho tới khi duyệt lại; sửa lần nữa khi đang chờ thì vẫn chờ, không câu báo.
        var viewer = await SetupAsync();
        Assert.Empty((await LibraryAsync(viewer.Token, $"heritageId={s.Heritage.Id}")).Items);
        var (_, again) = await Http.PutAsync<LessonDetailDto>($"{T}/{s.LessonId}", new { summary = (before.Summary ?? "") + " Thêm một câu." }, s.Token);
        Assert.Equal(LessonSampleStatus.PENDING, again.Data!.Sample!.Status);
        Assert.DoesNotContain("thư viện bài mẫu", again.Message);
        await ApproveAsync(s.LessonId);
        Assert.Single((await LibraryAsync(viewer.Token, $"heritageId={s.Heritage.Id}")).Items);

        // Đổi một phần cũng tính là sửa.
        var current = await L.GetAsync(s.Token, s.LessonId);
        var sections = current.Sections.Select(x => (object)LessonTestKit.ToSection(x)).ToList();
        var (_, sections2) = await Http.PutAsync<LessonDetailDto>($"{T}/{s.LessonId}", new { sections = sections.Take(sections.Count - 1).ToList() }, s.Token);
        if (current.Sections.Count > 1) Assert.Equal(LessonSampleStatus.PENDING, sections2.Data!.Sample!.Status);
    }

    [Fact]
    public async Task Archiving_or_deleting_a_lesson_takes_it_out_of_the_library_and_the_review_list()
    {
        var admin = await AdminTokenAsync();
        var archived = await ApprovedAsync();
        var viewer = await SetupAsync();
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"{T}/{archived.LessonId}/archive", null, archived.Token));
        Assert.Empty((await LibraryAsync(viewer.Token, $"heritageId={archived.Heritage.Id}")).Items);
        Assert.Null((await L.GetAsync(archived.Token, archived.LessonId)).Sample);

        // Xuất bản lại không tự về thư viện: phải đề xuất lại.
        await L.PublishAsync(archived.Token, archived.LessonId);
        Assert.Empty((await LibraryAsync(viewer.Token, $"heritageId={archived.Heritage.Id}")).Items);

        var deleted = await SetupAsync();
        await RequestAsync(deleted.Token, deleted.LessonId);
        var (del, _) = await Http.DeleteAsync<object>($"{T}/{deleted.LessonId}", deleted.Token);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var row = await factory.WithDbAsync(db => db.Lessons.AsNoTracking().SingleAsync(l => l.Id == deleted.LessonId));
        Assert.Null(row.SampleStatus);
        var pending = await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<AdminLessonLibraryItemDto>>($"{Adm}?keyword=Bài mẫu&pageSize=100", admin));
        Assert.DoesNotContain(pending.Items, i => i.Id == deleted.LessonId);
        var (res, body) = await Http.PostAsync<object>($"{Adm}/{deleted.LessonId}/approve", null, admin);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task The_library_hides_lessons_whose_heritages_are_all_hidden_whose_class_or_school_is_gone_and_lists_only_published_heritages_otherwise()
    {
        var viewer = await SetupAsync();

        // Một di sản bị ẩn trong hai: bài vẫn hiện, chỉ còn di sản đang xuất bản trên thẻ.
        var (school, schoolClass) = await L.SchoolWithClassAsync(2);
        var (h1, _) = await _q.Questions.HeritageAsync();
        var (h2, _) = await _q.Questions.HeritageAsync();
        var two = await L.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [h1.Id, h2.Id], "Hai di sản " + Unique());
        await RequestAsync(school.TeacherToken, two.Id);
        await ApproveAsync(two.Id);
        Assert.Equal(2, Assert.Single((await LibraryAsync(viewer.Token, $"heritageId={h1.Id}")).Items).Heritages.Count);
        await L.ArchiveHeritageAsync(h2.Id);
        Assert.Equal([h1.Id], Assert.Single((await LibraryAsync(viewer.Token, $"heritageId={h1.Id}")).Items).Heritages.Select(h => h.Id));
        await L.ArchiveHeritageAsync(h1.Id);
        Assert.Empty((await LibraryAsync(viewer.Token, $"heritageId={h1.Id}")).Items);
        var (res, body) = await Http.GetAsync<object>($"{Lib}/{two.Id}", viewer.Token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");

        var classGone = await ApprovedAsync();
        await factory.WithDbAsync(async db =>
        {
            (await db.Classes.SingleAsync(c => c.Id == classGone.ClassId)).DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });
        Assert.Empty((await LibraryAsync(viewer.Token, $"heritageId={classGone.Heritage.Id}")).Items);

        var schoolGone = await ApprovedAsync();
        await factory.WithDbAsync(async db =>
        {
            (await db.Organizations.SingleAsync(o => o.Id == schoolGone.School.OrgId)).DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });
        Assert.Empty((await LibraryAsync(viewer.Token, $"heritageId={schoolGone.Heritage.Id}")).Items);
    }

    // ---- Lọc, xếp, xem trước ----

    [Fact]
    public async Task The_library_filters_by_level_grade_heritage_and_keyword_sorts_by_newest_or_popular_and_pages()
    {
        var tag = Unique();
        var a = await ApprovedAsync(1, $"Mẫu {tag} cấp một");
        var b = await ApprovedAsync(2, $"Mẫu {tag} cấp hai");
        var c = await ApprovedAsync(3, $"Mẫu {tag} cấp ba");
        var viewer = await SetupAsync(2);
        var viewerToken = viewer.Token;
        string Ids(PagedResult<LessonLibraryItemDto> p) => string.Join(',', p.Items.Select(i => i.Id));

        Assert.Equal($"{c.LessonId},{b.LessonId},{a.LessonId}", Ids(await LibraryAsync(viewerToken, $"keyword={tag}")));
        Assert.Equal($"{b.LessonId}", Ids(await LibraryAsync(viewerToken, $"keyword={tag}&level=2")));
        Assert.Equal($"{a.LessonId}", Ids(await LibraryAsync(viewerToken, $"keyword={tag}&grade=3")));
        Assert.Equal($"{c.LessonId}", Ids(await LibraryAsync(viewerToken, $"keyword={tag}&grade=11")));
        Assert.Equal($"{c.LessonId}", Ids(await LibraryAsync(viewerToken, $"heritageId={c.Heritage.Id}")));
        Assert.Empty((await LibraryAsync(viewerToken, $"keyword={tag}&level=2&grade=3")).Items);

        // Dùng nhiều: a được dùng hai lần, b một lần, c chưa.
        var viewerClass2 = await L.Classroom.CreateClassAsync(viewer.Token, "Lớp thêm", 9);
        foreach (var (lesson, times) in new[] { (a, 2), (b, 1) })
            for (var i = 0; i < times; i++)
                await DiscoveryTestKit.Expect(Http.PostAsync<DuplicateLessonResultDto>($"{Lib}/{lesson.LessonId}/use", new { targetClassId = viewer.ClassId }, viewerToken), HttpStatusCode.Created);
        Assert.Equal($"{a.LessonId},{b.LessonId},{c.LessonId}", Ids(await LibraryAsync(viewerToken, $"keyword={tag}&sort=popular")));
        Assert.Equal([2, 1, 0], (await LibraryAsync(viewerToken, $"keyword={tag}&sort=popular")).Items.Select(i => i.UseCount));
        Assert.Equal($"{c.LessonId},{b.LessonId},{a.LessonId}", Ids(await LibraryAsync(viewerToken, $"keyword={tag}&sort=newest")));
        Assert.NotNull(viewerClass2);

        var page = await LibraryAsync(viewerToken, $"keyword={tag}&pageSize=2&page=2");
        Assert.Equal((3, 2, 2, 1), (page.TotalItems, page.TotalPages, page.Page, page.Items.Count));

        foreach (var bad in new[] { "level=0", "level=4", "grade=0", "grade=13", "page=0", "pageSize=0", "pageSize=101", "sort=lạ" })
        {
            var (res, _) = await Http.GetAsync<object>($"{Lib}?{bad}", viewerToken);
            Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{bad}: {(int)res.StatusCode}");
        }
    }

    [Fact]
    public async Task The_library_detail_shows_the_lesson_as_students_see_it_without_code_class_or_class_quizzes_and_lists_the_quizzes_to_copy()
    {
        var s = await SetupAsync(2, "Bài mẫu xem trước " + Unique());
        var quiz = await _q.PublishedQuizAsync(s, "Kiểm tra mẫu", questions: 3);
        await RequestAsync(s.Token, s.LessonId);
        await ApproveAsync(s.LessonId);
        var viewer = await SetupAsync(2);

        var detail = await DiscoveryTestKit.Expect(Http.GetAsync<LessonLibraryDetailDto>($"{Lib}/{s.LessonId}", viewer.Token));

        var mine = await L.GetAsync(s.Token, s.LessonId);
        Assert.Equal(s.LessonId, detail.Item.Id);
        Assert.Equal(1, detail.Item.QuizCount);
        Assert.Equal(("Kiểm tra mẫu", 3), (Assert.Single(detail.Quizzes).Title, Assert.Single(detail.Quizzes).QuestionCount));
        var view = detail.Lesson;
        Assert.Equal((mine.Title, mine.Summary, 2), (view.Title, view.Summary, view.SchoolLevel));
        Assert.Equal(mine.Sections.Count, view.Sections.Count);
        Assert.Equal(string.Empty, view.AccessCode);
        Assert.Null(view.ClassName);
        Assert.Equal(s.School.Teacher.FullName, view.TeacherName);
        Assert.Empty(view.Quizzes);
        Assert.NotEmpty(view.Heritages);
        Assert.NotNull(quiz);
        Assert.DoesNotContain(s.AccessCode!, JsonSerializer.Serialize(detail));

        // Không ở thư viện: 404.
        var hidden = await SetupAsync();
        var (res, body) = await Http.GetAsync<object>($"{Lib}/{hidden.LessonId}", viewer.Token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
        (res, body) = await Http.GetAsync<object>($"{Lib}/999999", viewer.Token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
    }

    // ---- Dùng bài mẫu ----

    [Fact]
    public async Task Using_a_sample_creates_a_draft_for_my_class_with_the_same_content_records_the_source_counts_the_use_and_costs_no_ai_turn()
    {
        var s = await ApprovedAsync(2, "Bài mẫu dùng " + Unique());
        var teacher = await SetupAsync(2);
        var target = await L.Classroom.CreateClassAsync(teacher.Token, "Lớp dùng mẫu", 9);
        var quotaBefore = (await L.GetAsync(teacher.Token, teacher.LessonId)).AiQuota.Used;
        var aiRowsBefore = await factory.WithDbAsync(db => db.AiGenerations.CountAsync());
        var source = await L.GetAsync(s.Token, s.LessonId);

        var (res, body) = await Http.PostAsync<DuplicateLessonResultDto>($"{Lib}/{s.LessonId}/use", new { targetClassId = target.Id }, teacher.Token);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var result = body.Data!;
        Assert.StartsWith($"Đã tạo bài học nháp ở lớp {target.Name} từ bài mẫu.", body.Message);
        Assert.Equal((0, null, 0, 0), (result.SkippedHeritages, result.LevelNotice, result.CopiedQuizzes, result.SkippedQuestions));
        var copy = result.Lesson;
        Assert.Equal((ContentStatus.DRAFT, null, target.Id, source.Title, source.Summary), (copy.Status, copy.AccessCode, copy.ClassId, copy.Title, copy.Summary));
        Assert.Equal(source.KeyPoints, copy.KeyPoints);
        Assert.Equal(source.Sections.Select(x => (x.Type, x.Body, x.ImageUrl, x.MilestoneYear)), copy.Sections.Select(x => (x.Type, x.Body, x.ImageUrl, x.MilestoneYear)));
        Assert.Equal(source.Heritages.Select(h => h.Id), copy.Heritages.Select(h => h.Id));
        Assert.Null(copy.Sample);

        var row = await factory.WithDbAsync(db => db.Lessons.AsNoTracking().SingleAsync(l => l.Id == copy.Id));
        Assert.Equal((s.LessonId, teacher.School.Teacher.Id), (row.SourceLessonId, row.TeacherId));
        // Bài gốc không đổi và đếm một lượt dùng; không tốn lượt AI nào.
        var after = await L.GetAsync(s.Token, s.LessonId);
        Assert.Equal((LessonSampleStatus.APPROVED, 1), (after.Sample!.Status, after.Sample.UseCount));
        Assert.Equal(source.UpdatedAt, after.UpdatedAt);
        Assert.Equal(quotaBefore, (await L.GetAsync(teacher.Token, teacher.LessonId)).AiQuota.Used);
        Assert.Equal(aiRowsBefore, await factory.WithDbAsync(db => db.AiGenerations.CountAsync()));
        var item = Assert.Single((await LibraryAsync(teacher.Token, $"heritageId={s.Heritage.Id}")).Items);
        Assert.Equal(1, item.UseCount);
        var audit = Assert.Single(await AuditAsync(AuditAction.LESSON_SAMPLE_USED, s.LessonId));
        Assert.Equal(teacher.School.Teacher.Id, audit.UserId);
        Assert.Contains(copy.Id.ToString(), audit.NewValue);

        // Mỗi lần dùng là một bài mới; bài mới xuất bản được như bài thường.
        await DiscoveryTestKit.Expect(Http.PostAsync<DuplicateLessonResultDto>($"{Lib}/{s.LessonId}/use", new { targetClassId = target.Id }, teacher.Token), HttpStatusCode.Created);
        Assert.Equal(2, (await L.GetAsync(s.Token, s.LessonId)).Sample!.UseCount);
        var published = await L.PublishAsync(teacher.Token, copy.Id);
        Assert.NotNull(published.AccessCode);
    }

    [Fact]
    public async Task Using_a_sample_for_a_class_of_another_level_copies_the_content_with_a_level_notice()
    {
        var s = await ApprovedAsync(2);
        var teacher = await SetupAsync(1);
        var target = await L.Classroom.CreateClassAsync(teacher.Token, "Lớp cấp một", 4);

        var result = await DiscoveryTestKit.Expect(
            Http.PostAsync<DuplicateLessonResultDto>($"{Lib}/{s.LessonId}/use", new { targetClassId = target.Id }, teacher.Token), HttpStatusCode.Created);

        Assert.Contains("khác cấp 2 của bài gốc", result.LevelNotice);
        Assert.Equal((await L.GetAsync(s.Token, s.LessonId)).Sections.Count, result.Lesson.Sections.Count);
        Assert.Equal(1, result.Lesson.SchoolLevel);
    }

    [Fact]
    public async Task Using_a_sample_skips_heritages_that_were_hidden_after_approval()
    {
        var (school, schoolClass) = await L.SchoolWithClassAsync(2);
        var (h1, _) = await _q.Questions.HeritageAsync();
        var (h2, _) = await _q.Questions.HeritageAsync();
        var lesson = await L.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [h1.Id, h2.Id], "Mẫu hai di sản " + Unique());
        await RequestAsync(school.TeacherToken, lesson.Id);
        await ApproveAsync(lesson.Id);
        await L.ArchiveHeritageAsync(h2.Id);
        var teacher = await SetupAsync(2);
        var target = await L.Classroom.CreateClassAsync(teacher.Token, "Lớp nhận", 9);

        var (res, body) = await Http.PostAsync<DuplicateLessonResultDto>($"{Lib}/{lesson.Id}/use", new { targetClassId = target.Id }, teacher.Token);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(1, body.Data!.SkippedHeritages);
        Assert.Equal([h1.Id], body.Data.Lesson.Heritages.Select(h => h.Id));
        Assert.Contains("Bỏ qua 1 di sản đã ẩn", body.Message);
    }

    [Fact]
    public async Task Using_a_sample_with_quizzes_copies_drafts_including_questions_of_other_teachers_and_skips_hidden_and_unfit_questions()
    {
        var s = await SetupAsync(2, "Mẫu kèm kiểm tra " + Unique());
        var quiz = await _q.ReadyQuizAsync(s, "Kiểm tra kèm", questions: 3);
        // Câu đã duyệt của một giáo viên khác (ngân hàng dùng chung) cũng nằm trong bài kiểm tra mẫu; thêm một câu rồi bị quản trị ẩn.
        var strangerSchool = await L.Classroom.SchoolAsync();
        var types = DSVHVN.Domain.Rules.QuestionTypes.ForLevel(2);
        var foreign = await _q.QuestionAsync(s, types[3 % types.Count], token: strangerSchool.TeacherToken);
        var hidden = await _q.QuestionAsync(s, types[0], token: strangerSchool.TeacherToken);
        await _q.AddQuestionsAsync(s.Token, quiz.Id, [foreign.Id, hidden.Id]);
        var admin = await AdminTokenAsync();
        await DiscoveryTestKit.Expect(Http.PostAsync<object>($"{QuestionTestKit.Admin}/{hidden.Id}/hide", new { reason = "Ẩn thử" }, admin));
        await RequestAsync(s.Token, s.LessonId);
        await ApproveAsync(s.LessonId);

        // Lớp cùng cấp: chép hết trừ câu đã ẩn; câu của giáo viên khác vẫn được chép.
        var teacher = await SetupAsync(2);
        var sameLevel = await L.Classroom.CreateClassAsync(teacher.Token, "Lớp cùng cấp", 7);
        var withQuizzes = await DiscoveryTestKit.Expect(
            Http.PostAsync<DuplicateLessonResultDto>($"{Lib}/{s.LessonId}/use", new { targetClassId = sameLevel.Id, includeQuizzes = true }, teacher.Token), HttpStatusCode.Created);
        Assert.Equal((1, 1), (withQuizzes.CopiedQuizzes, withQuizzes.SkippedQuestions));
        var copied = await factory.WithDbAsync(db => db.Quizzes.AsNoTracking().Include(x => x.Questions).SingleAsync(x => x.LessonId == withQuizzes.Lesson.Id));
        Assert.Equal((QuizStatus.DRAFT, teacher.School.Teacher.Id, null, null), (copied.Status, copied.CreatedBy, copied.OpenAt, copied.CloseAt));
        Assert.Equal(3 + 1, copied.Questions.Count);   // 3 câu của người soạn + câu của giáo viên khác, không có câu đã ẩn
        Assert.Contains(foreign.Id, copied.Questions.Select(x => x.QuestionId));
        Assert.DoesNotContain(hidden.Id, copied.Questions.Select(x => x.QuestionId));
        Assert.Null(copied.AccessCode);

        // Lớp cấp 1: bỏ thêm câu không dùng được ở cấp 1.
        var level1 = await L.Classroom.CreateClassAsync(teacher.Token, "Lớp cấp một", 3);
        var down = await DiscoveryTestKit.Expect(
            Http.PostAsync<DuplicateLessonResultDto>($"{Lib}/{s.LessonId}/use", new { targetClassId = level1.Id, includeQuizzes = true }, teacher.Token), HttpStatusCode.Created);
        var fitting = await factory.WithDbAsync(db => db.Quizzes.AsNoTracking().Include(x => x.Questions).ThenInclude(x => x.Question).SingleAsync(x => x.LessonId == down.Lesson.Id));
        Assert.True(down.SkippedQuestions > 1);
        Assert.All(fitting.Questions, x => Assert.True(QuizRules.QuestionFitsLevel(x.Question!.QuestionType, x.Question.LevelMin, x.Question.LevelMax, 1)));

        // Không chọn chép kèm: không có bài kiểm tra nào.
        var plain = await DiscoveryTestKit.Expect(
            Http.PostAsync<DuplicateLessonResultDto>($"{Lib}/{s.LessonId}/use", new { targetClassId = sameLevel.Id }, teacher.Token), HttpStatusCode.Created);
        Assert.Equal(0, plain.CopiedQuizzes);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Quizzes.CountAsync(x => x.LessonId == plain.Lesson.Id)));
    }

    [Fact]
    public async Task Using_a_sample_needs_a_plan_my_own_class_a_target_and_a_sample_in_the_library_and_my_own_sample_follows_the_duplicate_rule()
    {
        var s = await ApprovedAsync();
        var teacher = await SetupAsync(2);
        var target = await L.Classroom.CreateClassAsync(teacher.Token, "Lớp đích", 9);
        string Use(long id) => $"{Lib}/{id}/use";

        // Lớp của người khác (kể cả lớp của chính tác giả) hoặc không có: 404; thiếu lớp: 400.
        var (res, body) = await Http.PostAsync<object>(Use(s.LessonId), new { targetClassId = s.ClassId }, teacher.Token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
        (res, body) = await Http.PostAsync<object>(Use(s.LessonId), new { targetClassId = 999999 }, teacher.Token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
        (res, body) = await Http.PostAsync<object>(Use(s.LessonId), new { }, teacher.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "REQUIRED");

        // Bài không ở thư viện: 404.
        var notInLibrary = await SetupAsync();
        (res, body) = await Http.PostAsync<object>(Use(notInLibrary.LessonId), new { targetClassId = target.Id }, teacher.Token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");

        // Dùng bài của chính mình: lớp đích phải khác lớp của bài, như nhân bản.
        (res, body) = await Http.PostAsync<object>(Use(s.LessonId), new { targetClassId = s.ClassId }, s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
        var ownClass = await L.Classroom.CreateClassAsync(s.Token, "Lớp khác của tôi", 9);
        Assert.Equal(HttpStatusCode.Created, (await Http.PostAsync<DuplicateLessonResultDto>(Use(s.LessonId), new { targetClassId = ownClass.Id }, s.Token)).Response.StatusCode);

        // Hết gói: 422, không tạo bài nào.
        var lessonsBefore = await factory.WithDbAsync(db => db.Lessons.CountAsync());
        await _q.ExpirePlanAsync(teacher.School.OrgId);
        (res, body) = await Http.PostAsync<object>(Use(s.LessonId), new { targetClassId = target.Id }, teacher.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "PLAN_REQUIRED");
        Assert.StartsWith("Không thể dùng bài mẫu.", body.Message);
        Assert.Equal(lessonsBefore, await factory.WithDbAsync(db => db.Lessons.CountAsync()));
    }

    // ---- Quyền theo vai trò ----

    [Fact]
    public async Task School_admins_students_and_anonymous_users_cannot_use_the_library_and_teachers_cannot_review()
    {
        var s = await ApprovedAsync();
        var admin = await AdminTokenAsync();
        var studentToken = await L.StudentTokenAsync(s.Token, s.ClassId);

        foreach (var token in new[] { s.School.OrgAdminToken, studentToken, admin })
        {
            foreach (var (method, url) in new[] { (HttpMethod.Get, Lib), (HttpMethod.Get, $"{Lib}/{s.LessonId}"), (HttpMethod.Post, $"{Lib}/{s.LessonId}/use") })
            {
                var (res, _) = await Http.SendJsonAsync<object>(method, url, method == HttpMethod.Post ? new { targetClassId = s.ClassId } : null, token);
                Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"{method} {url}: {(int)res.StatusCode}");
            }
        }
        foreach (var token in new[] { s.Token, s.School.OrgAdminToken, studentToken })
        {
            foreach (var (method, url) in new[]
                     {
                         (HttpMethod.Get, Adm), (HttpMethod.Get, $"{Adm}/{s.LessonId}"), (HttpMethod.Post, $"{Adm}/{s.LessonId}/approve"),
                         (HttpMethod.Post, $"{Adm}/{s.LessonId}/reject"),
                     })
            {
                var (res, _) = await Http.SendJsonAsync<object>(method, url, method == HttpMethod.Post ? new { note = "x" } : null, token);
                Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"{method} {url}: {(int)res.StatusCode}");
            }
        }
        foreach (var url in new[] { Lib, Adm })
        {
            var (anonymous, _) = await Http.GetAsync<object>(url);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
    }

    [Fact]
    public async Task The_admin_review_list_filters_by_status_level_and_keyword_and_the_detail_is_read_only_without_the_lesson_code()
    {
        var admin = await AdminTokenAsync();
        var tag = Unique();
        var pending = await SetupAsync(1, $"Duyệt {tag} chờ");
        await RequestAsync(pending.Token, pending.LessonId);
        var approved = await SetupAsync(2, $"Duyệt {tag} đã duyệt");
        await RequestAsync(approved.Token, approved.LessonId);
        await ApproveAsync(approved.LessonId);
        var rejected = await SetupAsync(3, $"Duyệt {tag} từ chối");
        await RequestAsync(rejected.Token, rejected.LessonId);
        await Reject(rejected.LessonId, "Chưa đạt.");
        var notInLibrary = await SetupAsync(2, $"Duyệt {tag} không đề xuất");

        async Task<PagedResult<AdminLessonLibraryItemDto>> List(string query) =>
            await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<AdminLessonLibraryItemDto>>($"{Adm}?keyword={tag}{query}", admin));

        // Mặc định là chờ duyệt.
        var defaultList = await List("");
        var pendingItem = Assert.Single(defaultList.Items);
        Assert.Equal((pending.LessonId, LessonSampleStatus.PENDING, 1), (pendingItem.Id, pendingItem.Status, pendingItem.SchoolLevel));
        Assert.NotNull(pendingItem.RequestedAt);
        Assert.Equal(pending.School.Teacher.FullName, pendingItem.AuthorName);
        Assert.Equal(pending.Heritage.Id, Assert.Single(pendingItem.Heritages).Id);
        Assert.DoesNotContain(pending.School.Teacher.Email!, JsonSerializer.Serialize(pendingItem));
        Assert.Equal(approved.LessonId, Assert.Single((await List("&status=APPROVED")).Items).Id);
        var rejectedItem = Assert.Single((await List("&status=REJECTED")).Items);
        Assert.Equal((rejected.LessonId, "Chưa đạt."), (rejectedItem.Id, rejectedItem.Note));
        Assert.Empty((await List("&status=APPROVED&level=3")).Items);
        Assert.Single((await List("&status=APPROVED&level=2")).Items);

        var (bad, _) = await Http.GetAsync<object>($"{Adm}?status=LẠ", admin);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        (bad, _) = await Http.GetAsync<object>($"{Adm}?level=4", admin);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Xem để duyệt: chỉ đọc, không lộ mã bài học, ở mọi trạng thái; bài không ở thư viện 404.
        var detail = await DiscoveryTestKit.Expect(Http.GetAsync<AdminLessonLibraryDetailDto>($"{Adm}/{pending.LessonId}", admin));
        Assert.Equal(pending.LessonId, detail.Item.Id);
        Assert.Equal(string.Empty, detail.Lesson.AccessCode);
        Assert.Null(detail.Lesson.ClassName);
        Assert.NotEmpty(detail.Lesson.Sections);
        Assert.Equal(LessonSampleStatus.REJECTED, (await DiscoveryTestKit.Expect(Http.GetAsync<AdminLessonLibraryDetailDto>($"{Adm}/{rejected.LessonId}", admin))).Item.Status);
        var (res, body) = await Http.GetAsync<object>($"{Adm}/{notInLibrary.LessonId}", admin);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
    }
}
