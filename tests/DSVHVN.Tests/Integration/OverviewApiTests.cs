using System.Net;
using System.Net.Http.Headers;
using DSVHVN.Application.Common;
using DSVHVN.Application.Overview;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Tổng quan của quản trị trường (giáo viên so với hạn mức, lớp theo cấp, học sinh đã và chưa đăng nhập, bài học, bài kiểm tra, lượt nộp, điểm trung bình theo lớp và theo cấp,
/// lượt AI so với hạn mức, gói và lời nhắc hết hạn, chỉ số tổng không lộ tên) và của quản trị hệ thống (trường, giáo viên, học sinh, nội dung, lượt AI, số trường theo gói,
/// doanh thu từ giao dịch VNPay thành công và danh sách giao dịch, biểu đồ theo thời gian theo giờ Việt Nam).
/// </summary>
public sealed class OverviewApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly StatsTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string Org = "/api/v1/org/overview";
    private const string Sys = "/api/v1/admin/overview";

    private Task<string> AdminAsync() => _k.Quizzes.Questions.Lesson.Discovery.Api.AdminTokenAsync();

    private async Task<SchoolOverviewDto> SchoolAsync(string orgAdminToken) =>
        await DiscoveryTestKit.Expect(Http.GetAsync<SchoolOverviewDto>(Org, orgAdminToken));

    private async Task<SystemOverviewDto> SystemAsync(string admin) =>
        await DiscoveryTestKit.Expect(Http.GetAsync<SystemOverviewDto>(Sys, admin));

    private async Task<SystemTrendDto> TrendAsync(string admin, string query = "") =>
        await DiscoveryTestKit.Expect(Http.GetAsync<SystemTrendDto>($"{Sys}/trend{(query.Length == 0 ? "" : "?" + query)}", admin));

    /// <summary>
    /// Trường có lớp cấp 2 với ba học sinh (Em 1 đúng hết, Em 2 sai hết, Em 3 chưa làm và chưa đăng nhập lại), một bài học đã xuất bản và một bài kiểm tra
    /// hai câu đang mở: hai lượt đã nộp, điểm 100% và 0%.
    /// </summary>
    private async Task<(AttemptTestKit.Quiz Quiz, long Em3)> SchoolSceneAsync()
    {
        var s = await _k.Quizzes.SetupAsync(2);
        var quiz = await _k.QuizAsync(s, "Em 1", [QuestionType.SINGLE_CHOICE, QuestionType.TRUE_FALSE]);
        var em2 = await _k.StudentTokenAsync(s, "Em 2");
        await _k.StudentTokenAsync(s, "Em 3");
        await _k.AllCorrectAsync(quiz);
        await _k.AllWrongAsync(quiz, em2);
        var em3 = await _k.StudentIdAsync(s.ClassId, "Em 3");
        await _k.SetLastLoginAsync(em3, null);
        return (quiz, em3);
    }

    // ---- Tổng quan trường ----

    [Fact]
    public async Task The_school_overview_counts_teachers_classes_students_lessons_quizzes_attempts_and_the_average_by_class_and_level()
    {
        var (quiz, _) = await SchoolSceneAsync();
        var s = quiz.Setup;
        var overview = await SchoolAsync(s.School.OrgAdminToken);

        Assert.Equal(s.School.OrgId, overview.OrganizationId);
        Assert.Equal((1, 2, true), (overview.Teachers.Used, overview.Teachers.Max, overview.Teachers.CanAddTeacher));
        Assert.Equal((3, 2, 1), (overview.Students.Active, overview.Students.LoggedIn, overview.Students.NeverLoggedIn));
        Assert.Equal((1, 1), (overview.Lessons.Total, overview.Lessons.Published));
        Assert.Equal((1, 1), (overview.Quizzes.Published, overview.Quizzes.Open));
        Assert.Equal((2, 50m), (overview.Attempts.Finished, overview.Attempts.AveragePercent));

        Assert.Equal(1, overview.ClassCount);
        var row = Assert.Single(overview.Classes);
        Assert.Equal((s.ClassId, 2, 3, 2, 50m), (row.ClassId, row.SchoolLevel, row.ActiveStudents, row.FinishedAttempts, row.AveragePercent));
        Assert.Equal("Cấp 2", row.SchoolLevelName);

        Assert.Equal([1, 2, 3], overview.Levels.Select(l => l.SchoolLevel));
        var level2 = overview.Levels[1];
        Assert.Equal((1, 3, 2, 50m), (level2.ClassCount, level2.ActiveStudents, level2.FinishedAttempts, level2.AveragePercent));
        Assert.All(new[] { overview.Levels[0], overview.Levels[2] }, l => Assert.Equal((0, 0, 0, (decimal?)null), (l.ClassCount, l.ActiveStudents, l.FinishedAttempts, l.AveragePercent)));
    }

    [Fact]
    public async Task Only_submitted_attempts_of_active_students_in_live_classes_count_and_a_deleted_class_disappears()
    {
        var (quiz, _) = await SchoolSceneAsync();
        var s = quiz.Setup;
        var token = s.School.OrgAdminToken;
        var em1 = await _k.StudentIdAsync(s.ClassId, "Em 1");
        var em2 = await _k.StudentIdAsync(s.ClassId, "Em 2");

        await _k.SetStudentActiveAsync(em2, false);                                  // học sinh đã vô hiệu: bỏ khỏi sĩ số và điểm
        var afterDeactivate = await SchoolAsync(token);
        Assert.Equal((2, 1, 100m), (afterDeactivate.Students.Active, afterDeactivate.Attempts.Finished, afterDeactivate.Attempts.AveragePercent));
        await _k.SetStudentActiveAsync(em2, true);

        // Lượt đang làm dở không tính.
        var extra = await _k.StudentIdAsync(s.ClassId, "Em 3");
        await factory.WithDbAsync(async db =>
        {
            db.Attempts.Add(new DSVHVN.Domain.Quizzes.Attempt { QuizId = quiz.Id, StudentId = extra, StartedAt = DateTime.UtcNow, Status = AttemptStatus.IN_PROGRESS });
            return await db.SaveChangesAsync();
        });
        Assert.Equal(2, (await SchoolAsync(token)).Attempts.Finished);

        // Bài kiểm tra đã xóa không còn trong số lượt nộp, số phát hành.
        await _k.SoftDeleteQuizAsync(quiz.Id);
        var noQuiz = await SchoolAsync(token);
        Assert.Equal((0, 0, 0, (decimal?)null), (noQuiz.Quizzes.Published, noQuiz.Quizzes.Open, noQuiz.Attempts.Finished, noQuiz.Attempts.AveragePercent));
        Assert.Equal(em1, await _k.StudentIdAsync(s.ClassId, "Em 1"));

        // Lớp đã xóa mềm biến khỏi tổng quan cùng học sinh và bài học của lớp.
        await factory.WithDbAsync(async db =>
        {
            (await db.Classes.SingleAsync(c => c.Id == s.ClassId)).DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });
        var gone = await SchoolAsync(token);
        Assert.Equal((0, 0, 0, 0), (gone.ClassCount, gone.Students.Active, gone.Lessons.Total, gone.Quizzes.Published));
    }

    [Fact]
    public async Task A_school_never_sees_the_numbers_of_another_school()
    {
        var (first, _) = await SchoolSceneAsync();
        var (second, _) = await SchoolSceneAsync();
        await _k.AllWrongAsync(second, await _k.StudentTokenAsync(second.Setup, "Em 9"));

        var a = await SchoolAsync(first.Setup.School.OrgAdminToken);
        var b = await SchoolAsync(second.Setup.School.OrgAdminToken);
        Assert.Equal((first.Setup.School.OrgId, 2, 50m, 3), (a.OrganizationId, a.Attempts.Finished, a.Attempts.AveragePercent, a.Students.Active));
        Assert.Equal((second.Setup.School.OrgId, 3, 33.33m, 4), (b.OrganizationId, b.Attempts.Finished, b.Attempts.AveragePercent, b.Students.Active));
        Assert.Equal([first.Setup.ClassId], a.Classes.Select(c => c.ClassId));
    }

    [Fact]
    public async Task The_school_overview_has_only_totals_and_never_a_student_or_teacher_name()
    {
        var (quiz, _) = await SchoolSceneAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, Org);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", quiz.Setup.School.OrgAdminToken);
        var raw = await (await Http.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("Em 1", raw);
        Assert.DoesNotContain("Em 2", raw);
        Assert.DoesNotContain(quiz.Setup.School.Teacher.FullName!, raw);
        Assert.DoesNotContain(quiz.StudentCode, raw);
        Assert.DoesNotContain("studentCode", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_ai_usage_against_the_quota_follows_the_period_of_the_plan()
    {
        var setup = await _k.Quizzes.Questions.LessonAsync(2);
        var token = setup.School.OrgAdminToken;
        var before = (await SchoolAsync(token)).Ai;
        Assert.True(before.HasEffectivePlan);
        Assert.Equal(20, before.Max);

        await _k.Quizzes.Questions.GenerateAsync(setup.Token, setup.LessonId, new { count = 1 });
        var after = (await SchoolAsync(token)).Ai;
        Assert.Equal((before.Used + 1, 20 - before.Used - 1), (after.Used, after.Remaining));
        Assert.Equal(before.PeriodEnd, after.PeriodEnd);
    }

    [Fact]
    public async Task The_plan_notice_is_silent_with_a_long_plan_warns_in_the_last_fourteen_days_and_reports_a_missing_plan_without_blocking_the_page()
    {
        var s = await _k.Quizzes.SetupAsync(2);
        var token = s.School.OrgAdminToken;
        var today = VietnamTime.Today(DateTimeOffset.UtcNow);

        var fresh = await SchoolAsync(token);
        Assert.Equal((PlanNoticeKind.NONE, null), (fresh.PlanNotice.Kind, fresh.PlanNotice.Message));
        Assert.True(fresh.Plan is { IsEffective: true, PlanName: Plan.FreePlanName });
        Assert.InRange(fresh.PlanNotice.DaysLeft!.Value, 29, 31);

        await SetPlanEndAsync(s.School.OrgId, today.AddDays(5));
        var soon = await SchoolAsync(token);
        Assert.Equal((PlanNoticeKind.EXPIRING_SOON, 6), (soon.PlanNotice.Kind, soon.PlanNotice.DaysLeft));
        Assert.Contains("còn 6 ngày", soon.PlanNotice.Message);
        Assert.Contains(today.AddDays(5).ToString("dd/MM/yyyy"), soon.PlanNotice.Message);

        await SetPlanEndAsync(s.School.OrgId, today.AddDays(13));                    // đúng 14 ngày: vẫn nhắc
        Assert.Equal(PlanNoticeKind.EXPIRING_SOON, (await SchoolAsync(token)).PlanNotice.Kind);
        await SetPlanEndAsync(s.School.OrgId, today.AddDays(14));                    // 15 ngày: chưa nhắc
        Assert.Equal(PlanNoticeKind.NONE, (await SchoolAsync(token)).PlanNotice.Kind);

        await SetPlanEndAsync(s.School.OrgId, today.AddDays(-1));                    // quá hạn: không còn gói hiệu lực
        var expired = await SchoolAsync(token);
        Assert.Equal((PlanNoticeKind.NO_EFFECTIVE_PLAN, null), (expired.PlanNotice.Kind, expired.PlanNotice.DaysLeft));
        Assert.Contains("học sinh vẫn học bình thường", expired.PlanNotice.Message);
        Assert.True(expired.Plan is { IsEffective: false });
        Assert.False(expired.Ai.HasEffectivePlan);
        Assert.Null(expired.Teachers.Max);
        Assert.Equal(1, expired.Lessons.Total);                                      // dữ liệu có sẵn vẫn hiện

        await _k.Quizzes.ExpirePlanAsync(s.School.OrgId);                            // hủy hoặc hết hạn hẳn: vẫn xem được
        Assert.Equal(PlanNoticeKind.NO_EFFECTIVE_PLAN, (await SchoolAsync(token)).PlanNotice.Kind);
    }

    [Fact]
    public async Task The_school_overview_is_for_the_school_admin_only()
    {
        var (quiz, _) = await SchoolSceneAsync();
        var admin = await AdminAsync();
        foreach (var token in new[] { quiz.Setup.Token, admin, quiz.StudentToken })
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.GetAsync<object>(Org, token)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync<object>(Org)).Response.StatusCode);
    }

    private Task SetPlanEndAsync(long orgId, DateOnly end) =>
        factory.WithDbAsync(async db =>
        {
            var sub = await db.Subscriptions.Where(x => x.OrganizationId == orgId && x.Status == SubscriptionStatus.ACTIVE).SingleAsync();
            sub.EndDate = end;
            return await db.SaveChangesAsync();
        });

    // ---- Tổng quan hệ thống ----

    [Fact]
    public async Task The_system_overview_grows_by_exactly_what_a_new_school_adds_and_shrinks_when_the_school_is_stopped()
    {
        var admin = await AdminAsync();
        var before = await SystemAsync(admin);

        var (quiz, _) = await SchoolSceneAsync();                                   // 1 trường, 1 giáo viên, 3 học sinh, 1 bài học, 1 bài kiểm tra, 2 lượt nộp, 2 câu
        var mid = await SystemAsync(admin);
        var batch = await _k.Quizzes.Questions.GenerateAsync(quiz.TeacherToken, quiz.Setup.LessonId, new { count = 1 });
        var after = await SystemAsync(admin);

        Assert.Equal((1, 1, 0), Delta(after.Schools, before.Schools));
        Assert.Equal((1, 1), (after.Teachers.Total - before.Teachers.Total, after.Teachers.Active - before.Teachers.Active));
        Assert.Equal(3, after.ActiveStudents - before.ActiveStudents);
        Assert.Equal((1, 1, 1, 2), (after.Content.Lessons - before.Content.Lessons, after.Content.PublishedLessons - before.Content.PublishedLessons,
            after.Content.PublishedQuizzes - before.Content.PublishedQuizzes, after.Content.FinishedAttempts - before.Content.FinishedAttempts));
        Assert.Equal((2, batch.Questions.Count, 0), (after.Content.Questions.Approved - before.Content.Questions.Approved,
            after.Content.Questions.Pending - before.Content.Questions.Pending, after.Content.Questions.Hidden - before.Content.Questions.Hidden));
        // Một lượt sinh câu hỏi của giáo viên: tăng đúng một ở tổng, phần giáo viên và tháng này; phần của quản trị hệ thống (dịch, tóm tắt) không đổi.
        Assert.Equal((1, 1, 0, 1), (after.Ai.Total - mid.Ai.Total, after.Ai.ByTeachers - mid.Ai.ByTeachers, after.Ai.ByAdmin - mid.Ai.ByAdmin, after.Ai.ThisMonth - mid.Ai.ThisMonth));
        Assert.Equal(0, mid.Ai.ByAdmin - before.Ai.ByAdmin);
        Assert.True(mid.Ai.ByTeachers >= before.Ai.ByTeachers);

        var free = after.Plans.Single(p => p.PlanName == Plan.FreePlanName);
        Assert.Equal(1, free.ActiveSchools - before.Plans.Single(p => p.PlanName == Plan.FreePlanName).ActiveSchools);

        // Gói quá hạn: trường chuyển sang nhóm không có gói; hết hiệu lực thì không còn đếm vào gói.
        await _k.Quizzes.ExpirePlanAsync(quiz.Setup.School.OrgId);
        var expired = await SystemAsync(admin);
        Assert.Equal((1, 0, 1), Delta(expired.Schools, before.Schools));
        Assert.Equal(0, expired.Plans.Single(p => p.PlanName == Plan.FreePlanName).ActiveSchools - before.Plans.Single(p => p.PlanName == Plan.FreePlanName).ActiveSchools);

        // Ngừng trường: mọi thứ của trường biến khỏi số liệu.
        await factory.WithDbAsync(async db =>
        {
            (await db.Organizations.SingleAsync(o => o.Id == quiz.Setup.School.OrgId)).DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });
        var stopped = await SystemAsync(admin);
        Assert.Equal((0, 0, 0), Delta(stopped.Schools, before.Schools));
        Assert.Equal((0, 0), (stopped.Teachers.Total - before.Teachers.Total, stopped.ActiveStudents - before.ActiveStudents));
        Assert.Equal((0, 0, 0), (stopped.Content.Lessons - before.Content.Lessons, stopped.Content.PublishedQuizzes - before.Content.PublishedQuizzes,
            stopped.Content.FinishedAttempts - before.Content.FinishedAttempts));
    }

    private static (int Total, int With, int Without) Delta(SystemSchoolsDto now, SystemSchoolsDto was) =>
        (now.Total - was.Total, now.WithEffectivePlan - was.WithEffectivePlan, now.WithoutEffectivePlan - was.WithoutEffectivePlan);

    [Fact]
    public async Task Translation_requests_of_the_admin_count_apart_and_failed_or_abandoned_requests_do_not_count()
    {
        var admin = await AdminAsync();
        var setup = await _k.Quizzes.Questions.LessonAsync(2);
        var before = await SystemAsync(admin);
        var adminId = await factory.WithDbAsync(db => db.Users.Where(u => u.Username == ApiFactory.AdminUsername).Select(u => u.Id).SingleAsync());

        await factory.WithDbAsync(async db =>
        {
            DSVHVN.Domain.Lessons.AiGeneration Row(AiTaskStatus status, DateTime at) => new()
            {
                TaskType = AiTaskType.HERITAGE_TRANSLATION, HeritageId = setup.Heritage.Id, RequestedBy = adminId, Status = status, CreatedAt = at,
            };
            db.AiGenerations.AddRange(
                Row(AiTaskStatus.SUCCEEDED, DateTime.UtcNow),
                Row(AiTaskStatus.PROCESSING, DateTime.UtcNow),                     // đang xử lý chưa quá lâu: tính
                Row(AiTaskStatus.FAILED, DateTime.UtcNow),                         // lỗi: không tính
                Row(AiTaskStatus.PROCESSING, DateTime.UtcNow.AddHours(-2)));       // bị bỏ dở: không tính
            return await db.SaveChangesAsync();
        });

        var after = await SystemAsync(admin);
        Assert.Equal((2, 0, 2, 2), (after.Ai.Total - before.Ai.Total, after.Ai.ByTeachers - before.Ai.ByTeachers,
            after.Ai.ByAdmin - before.Ai.ByAdmin, after.Ai.ThisMonth - before.Ai.ThisMonth));
    }

    [Fact]
    public async Task Hiding_a_question_moves_it_from_approved_to_hidden_in_the_system_overview()
    {
        var admin = await AdminAsync();
        var setup = await _k.Quizzes.Questions.LessonAsync(2);
        var created = await _k.Quizzes.Questions.CreateAsync(setup.Token, QuestionTestKit.Samples(setup.Heritage.Id, setup.Images)[QuestionType.SINGLE_CHOICE]);
        var before = await SystemAsync(admin);

        await DiscoveryTestKit.Expect(Http.PostAsync<DSVHVN.Application.Questions.QuestionDetailDto>($"{QuestionTestKit.Admin}/{created.Id}/hide", null, admin));
        var after = await SystemAsync(admin);
        Assert.Equal((-1, 1), (after.Content.Questions.Approved - before.Content.Questions.Approved, after.Content.Questions.Hidden - before.Content.Questions.Hidden));
    }

    [Fact]
    public async Task Revenue_counts_only_succeeded_vnpay_payments_and_lists_every_transaction()
    {
        var admin = await AdminAsync();
        var billing = new BillingTestKit(factory);
        var plan = await billing.CreatePlanAsync(admin, price: 450_000m);
        var (paidOrg, paidOrgAdmin) = await billing.Api.OrgWithAdminAsync();
        var (_, failedOrgAdmin) = await billing.Api.OrgWithAdminAsync();
        var today = BillingTestKit.Today;
        var before = await SystemAsync(admin);
        var trendBefore = await TrendAsync(admin, $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");

        var paid = await billing.CheckoutAsync(paidOrgAdmin, plan.Id);
        Assert.Equal("00", await billing.PayAsync(paid));
        var failed = await billing.CheckoutAsync(failedOrgAdmin, plan.Id);
        Assert.Equal("00", await billing.PayAsync(failed, responseCode: "24", transactionStatus: "02"));   // người mua hủy: giao dịch thất bại

        var after = await SystemAsync(admin);
        Assert.Equal(1, after.RecentFailedPayments - before.RecentFailedPayments);
        Assert.True(after.Revenue.Available);
        Assert.Contains("VNPay", after.Revenue.Message);
        Assert.Contains("Google", after.Revenue.Message);
        Assert.Equal((450_000m, 450_000m, 450_000m, 1),
            (after.Revenue.Total - before.Revenue.Total, after.Revenue.ThisMonth - before.Revenue.ThisMonth,
             after.Revenue.Today - before.Revenue.Today, after.Revenue.SucceededPayments - before.Revenue.SucceededPayments));

        var trend = await TrendAsync(admin, $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        Assert.Equal(450_000m, trend.Points.Single().Revenue - trendBefore.Points.Single().Revenue);
        Assert.Equal(trend.Points.Single().Revenue, trend.Totals.Revenue);

        var (res, body) = await Http.GetAsync<PagedResult<SystemPaymentDto>>($"{Sys}/payments?organizationId={paidOrg.Id}", admin);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var row = Assert.Single(body.Data!.Items);
        Assert.Equal((paid.PaymentId, paid.Invoice.InvoiceNumber, paidOrg.Name, plan.Name, "VNPAY", 450_000m, PaymentStatus.SUCCEEDED, true),
            (row.Id, row.InvoiceNumber, row.OrganizationName, row.PlanName, row.ProviderCode, row.Amount!.Value, row.Status!.Value, row.PaidAt is not null));

        var (_, failedBody) = await Http.GetAsync<PagedResult<SystemPaymentDto>>($"{Sys}/payments?status=FAILED&from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}", admin);
        Assert.Contains(failedBody.Data!.Items, p => p.Id == failed.PaymentId && p.PaidAt is null);
        Assert.DoesNotContain(failedBody.Data!.Items, p => p.Status != PaymentStatus.FAILED);

        var (bad, _) = await Http.GetAsync<object>($"{Sys}/payments?from={today:yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}", admin);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.NotEmpty(after.Plans);
    }

    [Fact]
    public async Task The_system_overview_and_trend_are_for_the_system_admin_only()
    {
        var (quiz, _) = await SchoolSceneAsync();
        foreach (var url in new[] { Sys, $"{Sys}/trend", $"{Sys}/payments" })
        {
            foreach (var token in new[] { quiz.Setup.Token, quiz.Setup.School.OrgAdminToken, quiz.StudentToken })
                Assert.Equal(HttpStatusCode.Forbidden, (await Http.GetAsync<object>(url, token)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync<object>(url)).Response.StatusCode);
        }
    }

    // ---- Theo thời gian ----

    [Fact]
    public async Task The_trend_buckets_attempts_by_vietnam_day_with_empty_days_included_and_unfinished_attempts_left_out()
    {
        var admin = await AdminAsync();
        var s = await _k.Quizzes.SetupAsync(2);
        var quiz = await _k.QuizAsync(s, "Em 1", [QuestionType.SINGLE_CHOICE]);
        await _k.Quizzes.Questions.Lesson.Classroom.ImportAsync(s.Token, s.ClassId, Enumerable.Range(2, 5).Select(i => $"Em {i}"));
        var ids = await factory.WithDbAsync(db => db.Students.Where(x => x.ClassId == s.ClassId).OrderBy(x => x.Id).Select(x => x.Id).ToListAsync());

        // Giờ Việt Nam = UTC + 7: 16:59:59Z còn ở ngày đó, 17:00:00Z đã sang ngày sau.
        var times = new[]
        {
            new DateTime(2026, 3, 10, 16, 59, 59, DateTimeKind.Utc),   // 10/03 23:59:59
            new DateTime(2026, 3, 10, 17, 0, 0, DateTimeKind.Utc),     // 11/03 00:00:00
            new DateTime(2026, 3, 12, 16, 59, 59, DateTimeKind.Utc),   // 12/03 23:59:59
            new DateTime(2026, 3, 12, 17, 0, 0, DateTimeKind.Utc),     // 13/03 00:00:00 (ngoài khoảng)
        };
        for (var i = 0; i < times.Length; i++)
            await _k.SeedAttemptAsync(quiz.Id, ids[i], _ => true, i == 1 ? AttemptStatus.EXPIRED : AttemptStatus.SUBMITTED, times[i]);
        await _k.SeedAttemptAsync(quiz.Id, ids[4], _ => true, AttemptStatus.IN_PROGRESS, new DateTime(2026, 3, 10, 5, 0, 0, DateTimeKind.Utc));

        var trend = await TrendAsync(admin, "from=2026-03-09&to=2026-03-12");
        Assert.Equal(TrendGranularity.DAY, trend.Granularity);
        Assert.Equal(["2026-03-09", "2026-03-10", "2026-03-11", "2026-03-12"], trend.Points.Select(p => p.Period));
        Assert.Equal([0, 1, 1, 1], trend.Points.Select(p => p.AttemptsFinished));
        Assert.Equal(3, trend.Totals.AttemptsFinished);
        Assert.Equal("total", trend.Totals.Period);

        // Cả khoảng gom theo tháng: tháng 3 có đủ 4 lượt, tháng khác không có.
        var months = await TrendAsync(admin, "from=2026-02-20&to=2026-04-10&granularity=MONTH");
        Assert.Equal(["2026-02", "2026-03", "2026-04"], months.Points.Select(p => p.Period));
        Assert.Equal([0, 4, 0], months.Points.Select(p => p.AttemptsFinished));
    }

    [Fact]
    public async Task A_long_range_is_grouped_by_month_by_default_and_a_short_one_by_day()
    {
        var admin = await AdminAsync();
        var longRange = await TrendAsync(admin, "from=2026-01-15&to=2026-05-20");
        Assert.Equal(TrendGranularity.MONTH, longRange.Granularity);
        Assert.Equal(["2026-01", "2026-02", "2026-03", "2026-04", "2026-05"], longRange.Points.Select(p => p.Period));

        var shortRange = await TrendAsync(admin, "from=2026-01-01&to=2026-03-03");        // 62 ngày
        Assert.Equal((TrendGranularity.DAY, 62), (shortRange.Granularity, shortRange.Points.Count));

        var oneDay = await TrendAsync(admin, "from=2026-03-10&to=2026-03-10");
        Assert.Single(oneDay.Points);
    }

    [Fact]
    public async Task Without_parameters_the_trend_covers_the_last_thirty_days_and_shows_what_was_just_created()
    {
        var admin = await AdminAsync();
        await SchoolSceneAsync();
        var today = VietnamTime.Today(DateTimeOffset.UtcNow);
        var trend = await TrendAsync(admin);

        Assert.Equal((today.AddDays(-29), today, 30, TrendGranularity.DAY), (trend.From, trend.To, trend.Points.Count, trend.Granularity));
        Assert.Equal(today.ToString("yyyy-MM-dd"), trend.Points[^1].Period);
        var last = trend.Points[^1];
        Assert.True(last.NewSchools >= 1 && last.NewTeachers >= 1 && last.NewStudents >= 3 && last.LessonsCreated >= 1 && last.QuizzesPublished >= 1);
        Assert.Equal(trend.Points.Sum(p => p.NewStudents), trend.Totals.NewStudents);
        Assert.Equal(trend.Points.Sum(p => p.LessonsCreated), trend.Totals.LessonsCreated);
    }

    [Fact]
    public async Task The_trend_rejects_a_reversed_range_a_range_over_366_days_and_daily_grouping_over_92_days()
    {
        var admin = await AdminAsync();
        foreach (var (query, field) in new[]
                 {
                     ("from=2026-03-10&to=2026-03-09", "to"),
                     ("from=2025-01-01&to=2026-03-09", "from"),
                     ("from=2026-01-01&to=2026-06-30&granularity=DAY", "granularity"),
                 })
        {
            var (res, body) = await Http.GetAsync<object>($"{Sys}/trend?{query}", admin);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains(field, body.Errors!.Keys);
        }
        Assert.Equal(13, (await TrendAsync(admin, "from=2025-03-10&to=2026-03-10")).Points.Count);        // đúng 366 ngày: gom theo tháng
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync<object>($"{Sys}/trend?from=not-a-date", admin)).Response.StatusCode);
    }
}
