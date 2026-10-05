using System.Net;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Quản trị hệ thống với ngân hàng câu hỏi: xem chỉ đọc câu đã duyệt, ẩn câu kém chất lượng (chuyển sang bị loại, ghi nhật ký thao tác), câu bị ẩn
/// biến khỏi ngân hàng dùng chung, người tạo chỉ đọc và không nhân bản được; giáo viên, quản trị trường, học sinh không dùng được khu quản trị.
/// </summary>
public sealed class QuestionAdminApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly QuestionTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string T = QuestionTestKit.Teacher;
    private const string A = QuestionTestKit.Admin;

    public Task InitializeAsync()
    {
        factory.QuestionAi.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<string> AdminAsync() => _k.Lesson.Discovery.Api.AdminTokenAsync();

    [Fact]
    public async Task The_admin_sees_approved_questions_read_only_and_never_pending_ones()
    {
        var setup = await _k.LessonAsync(2);
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        var admin = await AdminAsync();
        var approved = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);
        var pending = (await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 1 })).Questions[0];

        var list = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPageDto>($"{A}?heritageId={setup.Heritage.Id}&pageSize=100", admin));
        Assert.Contains(list.Items, i => i.Id == approved.Id);
        Assert.DoesNotContain(list.Items, i => i.Id == pending.Id);
        var row = list.Items.Single(i => i.Id == approved.Id);
        Assert.Equal((setup.School.Teacher.FullName, false, false), (row.CreatorName, row.IsMine, row.IsHidden));

        var detail = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionDetailDto>($"{A}/{approved.Id}", admin));
        Assert.Equal(approved.Options.Select(o => o.IsCorrect), detail.Options.Select(o => o.IsCorrect));          // thấy cả đáp án
        Assert.True(detail.Access is { IsReadOnly: true, CanEdit: false, CanDelete: false, CanApprove: false, CanDuplicate: false, CanHide: true });
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync<object>($"{A}/{pending.Id}", admin)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>($"{A}/{pending.Id}/hide", new { }, admin)).Response.StatusCode);

        // Lọc như ngân hàng của giáo viên.
        Assert.Equal([approved.Id], (await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPageDto>($"{A}?questionType=SINGLE_CHOICE&level=2&createdBy={setup.School.Teacher.Id}", admin))).Items.Select(i => i.Id));
        Assert.Empty((await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPageDto>($"{A}?questionType=MATCHING&heritageId={setup.Heritage.Id}", admin))).Items);
    }

    [Fact]
    public async Task Hiding_a_question_rejects_it_records_who_hid_it_and_writes_the_audit_log()
    {
        var setup = await _k.LessonAsync(2);
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        var admin = await AdminAsync();
        var created = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);
        var adminId = await factory.WithDbAsync(db => db.Users.Where(u => u.Username == ApiFactory.AdminUsername).Select(u => u.Id).SingleAsync());

        var hidden = await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{A}/{created.Id}/hide", new { reason = "Đáp án chưa chính xác" }, admin));

        Assert.Equal((ReviewStatus.REJECTED, adminId), (hidden.ReviewStatus, hidden.ReviewedBy));
        Assert.True(hidden.ReviewedAt > created.ReviewedAt || hidden.ReviewedAt == created.ReviewedAt);
        Assert.False(hidden.Access.CanHide);

        var log = await factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(l => l.TargetType == "questions" && l.TargetId == created.Id));
        Assert.Equal((AuditAction.QUESTION_HIDDEN, ActorRole.ADMIN, adminId, "203.0.113.10"), (log.Action, log.ActorRole, log.UserId, log.IpAddress));
        Assert.Contains("Đáp án chưa chính xác", log.NewValue);
        Assert.Contains("APPROVED", log.OldValue);
        Assert.Contains("REJECTED", log.NewValue);

        // Hiện trong nhật ký thao tác với nhãn tiếng Việt.
        var page = await DiscoveryTestKit.Expect(Http.GetAsync<DSVHVN.Application.Common.PagedResult<DSVHVN.Application.Audit.AuditLogDto>>($"/api/v1/admin/audit-logs?action=QUESTION_HIDDEN&targetId={created.Id}", admin));
        Assert.Equal("Ẩn câu hỏi", Assert.Single(page.Items).ActionLabel);
    }

    [Fact]
    public async Task A_hidden_question_leaves_the_shared_bank_and_is_read_only_for_its_creator_without_duplicate()
    {
        var setup = await _k.LessonAsync(2);
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        var admin = await AdminAsync();
        var other = (await _k.Lesson.Classroom.SchoolAsync()).TeacherToken;
        var created = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{A}/{created.Id}/hide", null, admin));

        // Người khác không còn thấy câu.
        Assert.DoesNotContain((await _k.ListAsync(other, "pageSize=100")).Items, i => i.Id == created.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync<object>($"{T}/{created.Id}", other)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>($"{T}/{created.Id}/duplicate", null, other)).Response.StatusCode);

        // Người tạo thấy ở tab Của tôi, bị loại, chỉ đọc, không nhân bản, không duyệt lại.
        var mine = (await _k.ListAsync(setup.Token, "scope=MINE&status=REJECTED")).Items.Single(i => i.Id == created.Id);
        Assert.True(mine.IsHidden);
        var detail = await _k.GetAsync(setup.Token, created.Id);
        Assert.True(detail.Access is { IsReadOnly: true, Reason: QuestionReadOnlyReason.HIDDEN_BY_ADMIN, CanEdit: false, CanDelete: false, CanApprove: false, CanDuplicate: false });
        Assert.Equal("Câu hỏi này chỉ xem được vì đã bị quản trị hệ thống ẩn. Câu bị ẩn không nhân bản được; bạn có thể tạo câu mới.", detail.Access.Message);

        foreach (var call in new Func<Task<(HttpResponseMessage Response, Envelope<object> Body)>>[]
                 {
                     () => Http.PutAsync<object>($"{T}/{created.Id}", samples[QuestionType.SINGLE_CHOICE], setup.Token),
                     () => Http.DeleteAsync<object>($"{T}/{created.Id}", setup.Token),
                     () => Http.PostAsync<object>($"{T}/{created.Id}/approve", null, setup.Token),
                     () => Http.PostAsync<object>($"{T}/{created.Id}/duplicate", null, setup.Token),
                 })
        {
            var (res, body) = await call();
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (res.StatusCode, body.ErrorCode));
            Assert.Contains("quản trị hệ thống", body.Message);
        }
        // Người tạo có thể tạo câu mới với cùng nội dung.
        Assert.Equal(ReviewStatus.APPROVED, (await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE])).ReviewStatus);
    }

    [Fact]
    public async Task A_question_hidden_while_inside_a_quiz_keeps_the_quiz_and_the_admin_still_sees_it_hidden()
    {
        var setup = await _k.LessonAsync(2);
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        var admin = await AdminAsync();
        var created = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);
        await _k.AddToQuizAsync(setup.LessonId, created.Id);

        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{A}/{created.Id}/hide", null, admin));

        Assert.Equal(1, await factory.WithDbAsync(db => db.QuizQuestions.CountAsync(q => q.QuestionId == created.Id)));      // quiz giữ nguyên
        var list = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPageDto>($"{A}?status=REJECTED&heritageId={setup.Heritage.Id}", admin));
        var row = Assert.Single(list.Items);
        Assert.Equal((created.Id, true, 1), (row.Id, row.IsHidden, row.QuizCount));
        Assert.Empty((await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPageDto>($"{A}?status=APPROVED&heritageId={setup.Heritage.Id}", admin))).Items);
    }

    [Fact]
    public async Task Only_approved_questions_can_be_hidden_and_only_once()
    {
        var setup = await _k.LessonAsync(2);
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        var admin = await AdminAsync();
        var created = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{A}/{created.Id}/hide", null, admin));

        var (res, body) = await Http.PostAsync<object>($"{A}/{created.Id}/hide", null, admin);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (res.StatusCode, body.ErrorCode));
        Assert.Equal("Không thể ẩn câu hỏi. Chỉ ẩn được câu đã duyệt.", body.Message);
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.QUESTION_HIDDEN && l.TargetId == created.Id)));

        // Câu người tạo tự loại không nằm trong tầm nhìn của quản trị hệ thống.
        var rejectedByCreator = (await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 1 })).Questions[0];
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{rejectedByCreator.Id}/reject", null, setup.Token));
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync<object>($"{A}/{rejectedByCreator.Id}", admin)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>($"{A}/99999999/hide", null, admin)).Response.StatusCode);

        var (long1, longBody) = await Http.PostAsync<object>($"{A}/{created.Id}/hide", new { reason = new string('x', 501) }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, long1.StatusCode);
        Assert.Contains("reason", longBody.Errors!.Keys);
    }

    [Fact]
    public async Task The_admin_area_is_closed_to_everyone_but_the_system_admin()
    {
        var setup = await _k.LessonAsync(2);
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        var created = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);
        var student = await _k.Lesson.StudentTokenAsync(setup.Token, setup.ClassId);

        foreach (var (token, expected) in new (string?, HttpStatusCode)[]
                 {
                     (null, HttpStatusCode.Unauthorized), (setup.Token, HttpStatusCode.Forbidden),
                     (setup.School.OrgAdminToken, HttpStatusCode.Forbidden), (student, HttpStatusCode.Forbidden),
                 })
        {
            Assert.Equal(expected, (await Http.GetAsync<object>(A, token)).Response.StatusCode);
            Assert.Equal(expected, (await Http.GetAsync<object>($"{A}/{created.Id}", token)).Response.StatusCode);
            Assert.Equal(expected, (await Http.PostAsync<object>($"{A}/{created.Id}/hide", null, token)).Response.StatusCode);
        }
        Assert.Equal(ReviewStatus.APPROVED, (await _k.GetAsync(setup.Token, created.Id)).ReviewStatus);
    }
}
