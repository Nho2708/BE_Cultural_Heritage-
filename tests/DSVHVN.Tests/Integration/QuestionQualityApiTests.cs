using System.Net;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Màn kiểm duyệt ngân hàng câu hỏi của quản trị hệ thống: số lượt trả lời và tỉ lệ đúng của từng câu tính từ lượt đã nộp, dấu hiệu "cần xem lại"
/// (đủ 10 lượt trả lời mà tỉ lệ đúng dưới 30%), lọc và sắp xếp theo chất lượng, hiện lại câu đã ẩn (ghi nhật ký thao tác), quyền theo vai trò.
/// </summary>
public sealed class QuestionQualityApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly StatsTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string A = QuestionTestKit.Admin;
    private const string T = QuestionTestKit.Teacher;

    private Task<string> AdminAsync() => _k.Quizzes.Questions.Lesson.Discovery.Api.AdminTokenAsync();

    /// <summary>
    /// Bài kiểm tra đang mở có ba câu (một đáp án đúng, Đúng/Sai, sắp xếp) và <paramref name="students"/> học sinh đã nộp: câu 1 chỉ em đầu đúng (10%),
    /// câu 2 mọi em đúng, câu 3 một nửa số em đúng.
    /// </summary>
    private async Task<(AttemptTestKit.Quiz Quiz, List<long> StudentIds)> SceneAsync(int students = 10)
    {
        var quiz = await _k.Attempts.OpenQuizAsync(2, [QuestionType.SINGLE_CHOICE, QuestionType.TRUE_FALSE, QuestionType.ORDERING]);
        var classroom = _k.Quizzes.Questions.Lesson.Classroom;
        await classroom.ImportAsync(quiz.TeacherToken, quiz.Setup.ClassId, Enumerable.Range(1, students).Select(i => $"Em {i}"));
        var ids = await factory.WithDbAsync(db => db.Students.Where(s => s.ClassId == quiz.Setup.ClassId && s.FullName!.StartsWith("Em "))
            .OrderBy(s => s.Id).Select(s => s.Id).ToListAsync());
        for (var j = 0; j < ids.Count; j++)
        {
            var student = j;
            await _k.SeedAttemptAsync(quiz.Id, ids[j], i => i == 0 ? student == 0 : i == 1 || student % 2 == 0);
        }
        return (quiz, ids);
    }

    private async Task<QuestionQualityPageDto> QualityAsync(string admin, string query = "") =>
        await DiscoveryTestKit.Expect(Http.GetAsync<QuestionQualityPageDto>($"{A}/quality{(query.Length == 0 ? "" : "?" + query)}", admin));

    [Fact]
    public async Task Each_row_carries_the_answers_the_correct_rate_and_the_needs_review_flag_from_submitted_attempts()
    {
        var (quiz, _) = await SceneAsync();
        var admin = await AdminAsync();
        var page = await QualityAsync(admin, $"heritageId={quiz.Setup.Heritage.Id}&pageSize=100");

        var rows = quiz.Questions.Select(q => page.Items.Single(i => i.Question.Id == q.Id)).ToList();
        Assert.Equal([(10, 1, 10m, true), (10, 10, 100m, false), (10, 5, 50m, false)],
            rows.Select(r => (r.Answered, r.Correct, r.CorrectRatePercent!.Value, r.LowQuality)));
        Assert.All(rows, r => Assert.Null(r.HiddenAt));
        Assert.Equal((10, 30), (page.MinAnswers, page.LowCorrectPercent));
        Assert.True(page.ApprovedCount >= 3 && page.LowQualityCount >= 1);
        var creator = quiz.Setup.School.Teacher.FullName;
        Assert.Equal(creator, rows[0].Question.CreatorName);
    }

    [Fact]
    public async Task A_question_with_fewer_than_ten_answers_is_never_flagged_and_unfinished_attempts_do_not_count()
    {
        var (quiz, ids) = await SceneAsync(students: 9);
        var admin = await AdminAsync();
        // Lượt đang làm dở (đúng hết cũng như sai hết) không tính vào lượt trả lời.
        var extra = await _k.StudentIdAsync(quiz.Setup.ClassId, "Nguyễn Văn An");
        await _k.SeedAttemptAsync(quiz.Id, extra, _ => false, AttemptStatus.IN_PROGRESS);

        var page = await QualityAsync(admin, $"heritageId={quiz.Setup.Heritage.Id}&pageSize=100");
        var first = page.Items.Single(i => i.Question.Id == quiz.Questions[0].Id);
        Assert.Equal((9, 1, false), (first.Answered, first.Correct, first.LowQuality));
        Assert.Empty((await QualityAsync(admin, $"heritageId={quiz.Setup.Heritage.Id}&lowQualityOnly=true")).Items);
        Assert.Equal(9, ids.Count);
    }

    [Fact]
    public async Task Low_quality_only_and_the_lowest_rate_sort_put_the_weakest_questions_first_and_unanswered_ones_last()
    {
        var (quiz, _) = await SceneAsync();
        var admin = await AdminAsync();
        var untouched = await _k.Quizzes.QuestionAsync(quiz.Setup, QuestionType.SINGLE_CHOICE);   // chưa ai làm

        var flagged = await QualityAsync(admin, $"heritageId={quiz.Setup.Heritage.Id}&lowQualityOnly=true");
        Assert.Equal([quiz.Questions[0].Id], flagged.Items.Select(i => i.Question.Id));
        Assert.Equal(1, flagged.TotalItems);

        var sorted = await QualityAsync(admin, $"heritageId={quiz.Setup.Heritage.Id}&sort=LOWEST_RATE&pageSize=100");
        Assert.Equal([quiz.Questions[0].Id, quiz.Questions[2].Id, quiz.Questions[1].Id, untouched.Id], sorted.Items.Select(i => i.Question.Id));
        Assert.Null(sorted.Items[^1].CorrectRatePercent);
        Assert.False(sorted.Items[^1].LowQuality);
    }

    [Fact]
    public async Task A_hidden_question_leaves_the_flagged_list_but_stays_in_the_hidden_tab_with_its_hiding_time()
    {
        var (quiz, _) = await SceneAsync();
        var admin = await AdminAsync();
        var weak = quiz.Questions[0];
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{A}/{weak.Id}/hide", new { reason = "Đáp án gây hiểu nhầm" }, admin));

        var approved = await QualityAsync(admin, $"heritageId={quiz.Setup.Heritage.Id}&status=APPROVED&lowQualityOnly=true");
        Assert.Empty(approved.Items);
        var hidden = await QualityAsync(admin, $"heritageId={quiz.Setup.Heritage.Id}&status=REJECTED");
        var row = Assert.Single(hidden.Items);
        Assert.Equal((weak.Id, true, false, 10), (row.Question.Id, row.Question.IsHidden, row.LowQuality, row.Answered));
        Assert.NotNull(row.HiddenAt);
        Assert.True(hidden.HiddenCount >= 1);
        // Quiz đã phát hành và kết quả cũ giữ nguyên: số liệu của câu vẫn còn.
        Assert.Equal(1, row.Correct);
    }

    [Fact]
    public async Task The_list_filters_by_school_creator_type_and_keyword_and_rejects_bad_parameters()
    {
        var (quiz, _) = await SceneAsync();
        var admin = await AdminAsync();
        var org = quiz.Setup.School.OrgId;
        var teacher = quiz.Setup.School.Teacher.Id;

        var bySchool = await QualityAsync(admin, $"organizationId={org}&pageSize=100");
        Assert.Equal(3, bySchool.Items.Count(i => quiz.Questions.Any(q => q.Id == i.Question.Id)));
        Assert.All(bySchool.Items, i => Assert.Equal(quiz.Setup.School.Teacher.FullName, i.Question.CreatorName));
        Assert.Empty((await QualityAsync(admin, $"organizationId={org + 100000}")).Items);
        Assert.Equal(3, (await QualityAsync(admin, $"createdBy={teacher}&pageSize=100")).Items.Count(i => quiz.Questions.Any(q => q.Id == i.Question.Id)));
        Assert.Equal([quiz.Questions[2].Id], (await QualityAsync(admin, $"organizationId={org}&questionType=ORDERING")).Items.Select(i => i.Question.Id));

        var (bad, body) = await Http.GetAsync<object>($"{A}/quality?status=PENDING", admin);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("status", body.Errors!.Keys);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync<object>($"{A}/quality?level=4", admin)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync<object>($"{A}/quality?page=0", admin)).Response.StatusCode);
    }

    // ---- Hiện lại ----

    [Fact]
    public async Task Restoring_a_hidden_question_approves_it_for_its_creator_returns_it_to_the_shared_bank_and_writes_the_audit_log()
    {
        var setup = await _k.Quizzes.Questions.LessonAsync(2);
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        var admin = await AdminAsync();
        var other = (await _k.Quizzes.Questions.Lesson.Classroom.SchoolAsync()).TeacherToken;
        var created = await _k.Quizzes.Questions.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);
        var adminId = await factory.WithDbAsync(db => db.Users.Where(u => u.Username == ApiFactory.AdminUsername).Select(u => u.Id).SingleAsync());
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{A}/{created.Id}/hide", null, admin));
        Assert.DoesNotContain((await _k.Quizzes.Questions.ListAsync(other, "pageSize=100")).Items, i => i.Id == created.Id);

        var hidden = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionDetailDto>($"{A}/{created.Id}", admin));
        Assert.True(hidden.Access is { CanHide: false, CanRestore: true });

        var restored = await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{A}/{created.Id}/restore", new { reason = "Đã kiểm tra lại, đáp án đúng" }, admin));
        Assert.Equal((ReviewStatus.APPROVED, created.CreatedBy), (restored.ReviewStatus, restored.ReviewedBy));
        Assert.True(restored.Access is { CanHide: true, CanRestore: false });

        // Hiện lại cho mọi giáo viên, người tạo sửa được như câu thường, nhân bản được.
        Assert.Contains((await _k.Quizzes.Questions.ListAsync(other, "pageSize=100")).Items, i => i.Id == created.Id);
        var detail = await _k.Quizzes.Questions.GetAsync(setup.Token, created.Id);
        Assert.True(detail.Access is { CanEdit: true, CanDelete: true, IsReadOnly: false });
        Assert.Equal(HttpStatusCode.Created, (await Http.PostAsync<QuestionDetailDto>($"{T}/{created.Id}/duplicate", null, other)).Response.StatusCode);

        var logs = await factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(l => l.TargetType == "questions" && l.TargetId == created.Id).OrderBy(l => l.Id).ToListAsync());
        Assert.Equal([AuditAction.QUESTION_HIDDEN, AuditAction.QUESTION_RESTORED], logs.Select(l => l.Action));
        var log = logs[1];
        Assert.Equal((ActorRole.ADMIN, adminId, "203.0.113.10"), (log.ActorRole, log.UserId, log.IpAddress));
        Assert.Contains("REJECTED", log.OldValue);
        Assert.Contains("APPROVED", log.NewValue);
        Assert.Contains("Đã kiểm tra lại, đáp án đúng", log.NewValue);

        var page = await DiscoveryTestKit.Expect(Http.GetAsync<DSVHVN.Application.Common.PagedResult<DSVHVN.Application.Audit.AuditLogDto>>(
            $"/api/v1/admin/audit-logs?action=QUESTION_RESTORED&targetId={created.Id}", admin));
        Assert.Equal("Hiện lại câu hỏi", Assert.Single(page.Items).ActionLabel);
    }

    [Fact]
    public async Task Only_hidden_questions_can_be_restored_and_only_the_system_admin_can_do_it()
    {
        var setup = await _k.Quizzes.Questions.LessonAsync(2);
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        var admin = await AdminAsync();
        var created = await _k.Quizzes.Questions.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);

        var (res, body) = await Http.PostAsync<object>($"{A}/{created.Id}/restore", null, admin);          // còn đang duyệt
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (res.StatusCode, body.ErrorCode));
        Assert.Equal("Không thể hiện lại câu hỏi. Chỉ hiện lại được câu đã bị ẩn.", body.Message);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>($"{A}/99999999/restore", null, admin)).Response.StatusCode);

        // Câu người tạo tự loại không thuộc tầm nhìn của quản trị hệ thống.
        var pending = (await _k.Quizzes.Questions.GenerateAsync(setup.Token, setup.LessonId, new { count = 1 })).Questions[0];
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{pending.Id}/reject", null, setup.Token));
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>($"{A}/{pending.Id}/restore", null, admin)).Response.StatusCode);

        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{A}/{created.Id}/hide", null, admin));
        var (tooLong, longBody) = await Http.PostAsync<object>($"{A}/{created.Id}/restore", new { reason = new string('x', 501) }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("reason", longBody.Errors!.Keys);

        foreach (var token in new[] { setup.Token, setup.School.OrgAdminToken })
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.PostAsync<object>($"{A}/{created.Id}/restore", null, token)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.PostAsync<object>($"{A}/{created.Id}/restore", null)).Response.StatusCode);
        Assert.Equal(ReviewStatus.REJECTED, (await _k.Quizzes.Questions.GetAsync(setup.Token, created.Id)).ReviewStatus);   // vẫn đang ẩn
    }

    [Fact]
    public async Task The_quality_list_is_for_the_system_admin_only()
    {
        var school = await _k.Quizzes.Questions.Lesson.Classroom.SchoolAsync();
        foreach (var token in new[] { school.TeacherToken, school.OrgAdminToken })
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.GetAsync<object>($"{A}/quality", token)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync<object>($"{A}/quality")).Response.StatusCode);
    }
}
