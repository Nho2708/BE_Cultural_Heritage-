using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Dịch vụ bài học ghép với hạ tầng thật trên SQLite trong bộ nhớ: sinh mã bài học khi trùng, đếm lượt AI theo kỳ của gói (ranh giới theo giờ Việt Nam),
/// ràng buộc của bảng lesson_sections và ai_generations, trạng thái bài kiểm tra với học sinh.
/// </summary>
public sealed class LessonServiceTests : IDisposable
{
    private readonly ServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private sealed record Fixture(long OrgId, Actor Teacher, long TeacherId, long ClassId, long HeritageId, long EventId, string ImageUrl);

    private async Task<Fixture> SetupAsync(int grade = 8)
    {
        var org = await _h.AddOrganizationAsync();
        var teacher = await _h.AddUserAsync("gv.thu", RoleCode.TEACHER, org.Id);
        var (heritageId, eventId, image) = await _h.QueryAsync(async db =>
        {
            var heritage = new Heritage
            {
                Name = "Hoàng thành", Slug = "hoang-thanh-" + Guid.NewGuid().ToString("N")[..6], Status = ContentStatus.PUBLISHED,
                ShortDescription = "Mô tả", Content = "Nội dung. " + string.Join(' ', Enumerable.Repeat("từ", 200)),
            };
            db.Heritages.Add(heritage);
            await db.SaveChangesAsync();
            var timeline = new Timeline { HeritageId = heritage.Id, Name = "Thời Lý", StartYear = 1010 };
            db.Timelines.Add(timeline);
            await db.SaveChangesAsync();
            var evt = new TimelineEvent { TimelineId = timeline.Id, Name = "Dời đô", StartYear = 1010 };
            db.Events.Add(evt);
            var url = "https://upload.wikimedia.org/a.jpg";
            db.Media.Add(new Media { HeritageId = heritage.Id, MediaType = "IMAGE", Url = url, Caption = "Cổng thành", Credit = "Tác giả · CC BY-SA 4.0 · Wikimedia" });
            await db.SaveChangesAsync();
            return (heritage.Id, evt.Id, url);
        });
        var classId = await _h.QueryAsync(async db =>
        {
            var c = new SchoolClass { OrganizationId = org.Id, TeacherId = teacher.Id, Name = "8A1", Grade = (byte)grade, SchoolYear = "2026-2027" };
            db.Classes.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        });
        return new Fixture(org.Id, new Actor(teacher.Id, RoleCode.TEACHER, org.Id, "127.0.0.1"), teacher.Id, classId, heritageId, eventId, image);
    }

    /// <summary>Bài cấp 2 đủ điều kiện xuất bản (tóm tắt, ý chính, một mốc).</summary>
    private async Task<long> PublishableLessonAsync(Fixture f, string title = "Bài thử")
    {
        var created = await _h.Service<LessonService, LessonDetailDto>(s => s.CreateAsync(f.Teacher,
            new CreateLessonRequest(f.ClassId, title, [f.HeritageId]), default));
        await _h.Service<LessonService, LessonDetailDto>(s => s.UpdateAsync(f.Teacher, created.Id, new UpdateLessonRequest(
            null, "Tóm tắt bài.", ["Ý chính"],
            [new LessonSectionRequest(LessonSectionType.MILESTONE, null, "Dời đô", null, 1010, f.EventId, null)], null), default));
        return created.Id;
    }

    // ---- Mã bài học ----

    [Fact]
    public async Task Publishing_skips_codes_already_taken_including_codes_of_deleted_lessons()
    {
        var f = await SetupAsync();
        var first = await PublishableLessonAsync(f, "Bài một");
        _h.Codes.Enqueue("BH222222");
        var published = await _h.Service<LessonService, PublishLessonResultDto>(s => s.PublishAsync(f.Teacher, first, default));
        Assert.Equal("BH222222", published.AccessCode);

        // Xóa mềm bài đó: mã vẫn không cấp lại.
        await _h.QueryAsync(async db =>
        {
            (await db.Lessons.SingleAsync(l => l.Id == first)).DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });

        var second = await PublishableLessonAsync(f, "Bài hai");
        _h.Codes.Enqueue("BH222222", "BH222222", "BH333333");
        var again = await _h.Service<LessonService, PublishLessonResultDto>(s => s.PublishAsync(f.Teacher, second, default));

        Assert.Equal("BH333333", again.AccessCode);
        Assert.Equal(4, _h.Codes.Calls);     // 1 (bài một) + 3 (hai lần trùng rồi mã mới)
    }

    [Fact]
    public async Task Five_taken_codes_in_a_row_fail_with_a_system_error_and_leave_the_lesson_a_draft()
    {
        var f = await SetupAsync();
        var first = await PublishableLessonAsync(f, "Bài một");
        _h.Codes.Enqueue("BH222222");
        await _h.Service<LessonService, PublishLessonResultDto>(s => s.PublishAsync(f.Teacher, first, default));

        var second = await PublishableLessonAsync(f, "Bài hai");
        _h.Codes.Enqueue(Enumerable.Repeat("BH222222", LessonCodes.MaxGenerateAttempts).ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _h.Service<LessonService, PublishLessonResultDto>(s => s.PublishAsync(f.Teacher, second, default)));

        var lesson = await _h.QueryAsync(db => db.Lessons.AsNoTracking().SingleAsync(l => l.Id == second));
        Assert.Equal((ContentStatus.DRAFT, null), (lesson.Status, lesson.AccessCode));

        // Lần bấm sau (mã mới sinh ngẫu nhiên) xuất bản được.
        var ok = await _h.Service<LessonService, PublishLessonResultDto>(s => s.PublishAsync(f.Teacher, second, default));
        Assert.Matches("^BH[2-9A-HJ-NP-Z]{6}$", ok.AccessCode);
    }

    [Fact]
    public async Task The_database_refuses_two_lessons_with_the_same_code_but_allows_many_without_a_code()
    {
        var f = await SetupAsync();
        await _h.QueryAsync(async db =>
        {
            for (var i = 0; i < 3; i++) db.Lessons.Add(new Lesson { TeacherId = f.TeacherId, ClassId = f.ClassId, Title = "Nháp " + i, Status = ContentStatus.DRAFT });
            return await db.SaveChangesAsync();
        });

        await _h.QueryAsync(async db =>
        {
            db.Lessons.Add(new Lesson { TeacherId = f.TeacherId, ClassId = f.ClassId, Title = "A", Status = ContentStatus.PUBLISHED, AccessCode = "BH777777" });
            db.Lessons.Add(new Lesson { TeacherId = f.TeacherId, ClassId = f.ClassId, Title = "B", Status = ContentStatus.PUBLISHED, AccessCode = "BH777777" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            return 0;
        });
    }

    // ---- Hạn mức lượt AI ----

    private Task AddAiRowAsync(Fixture f, AiTaskStatus status, DateTime createdAt, long lessonId, bool heritageTask = false, long? requester = null) =>
        _h.QueryAsync(async db =>
        {
            db.AiGenerations.Add(new AiGeneration
            {
                TaskType = heritageTask ? AiTaskType.HERITAGE_SUMMARY : AiTaskType.LESSON_SUMMARY,
                LessonId = heritageTask ? null : lessonId,
                HeritageId = heritageTask ? f.HeritageId : null,
                RequestedBy = requester ?? f.TeacherId,
                Status = status,
                CreatedAt = createdAt,
            });
            return await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Ai_turns_count_succeeded_and_recent_processing_rows_of_the_school_inside_the_plan_period()
    {
        var f = await SetupAsync();
        var lesson = await PublishableLessonAsync(f);
        var now = _h.Clock.GetUtcNow().UtcDateTime;                      // 29/09/2026 08:00 giờ Việt Nam
        var admin = (await _h.AddUserAsync("admin.he.thong", RoleCode.ADMIN, null)).Id;
        var otherOrg = await _h.AddOrganizationAsync("Trường khác");
        var otherTeacher = await _h.AddUserAsync("gv.khac", RoleCode.TEACHER, otherOrg.Id);

        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, now, lesson);                                    // tính
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, now.AddHours(-1), lesson);                       // tính
        await AddAiRowAsync(f, AiTaskStatus.PROCESSING, now.AddMinutes(-1), lesson);                    // đang xử lý: tính
        await AddAiRowAsync(f, AiTaskStatus.PROCESSING, now.AddMinutes(-6), lesson);                    // bị bỏ dở quá 5 phút: không tính
        await AddAiRowAsync(f, AiTaskStatus.FAILED, now, lesson);                                       // thất bại: không tính
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, now, lesson, heritageTask: true, requester: admin); // dịch di sản: không tính
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, now, lesson, requester: otherTeacher.Id);        // trường khác: không tính
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, now.AddDays(-5), lesson);                        // trước kỳ của gói: không tính

        var quota = await _h.Service<PlanGuard, AiQuotaDto>(g => g.GetAiQuotaAsync(f.OrgId, default));

        Assert.Equal((true, 3, 20, 17, "Miễn phí"), (quota.HasEffectivePlan, quota.Used, quota.Max, quota.Remaining, quota.PlanName));
        Assert.Equal((new DateOnly(2026, 9, 29), new DateOnly(2026, 10, 28)), (quota.PeriodStart, quota.PeriodEnd));
    }

    [Fact]
    public async Task The_period_starts_and_ends_at_midnight_in_vietnam()
    {
        var f = await SetupAsync();
        var lesson = await PublishableLessonAsync(f);
        // Kỳ 29/09 → 28/10/2026 giờ Việt Nam = [28/09 17:00 UTC, 28/10 17:00 UTC).
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, new DateTime(2026, 9, 28, 16, 59, 59, DateTimeKind.Utc), lesson);   // 23:59:59 ngày 28/09: trước kỳ
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, new DateTime(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc), lesson);     // 00:00 ngày 29/09: trong kỳ
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, new DateTime(2026, 10, 28, 16, 59, 59, DateTimeKind.Utc), lesson);  // 23:59:59 ngày 28/10: trong kỳ
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, new DateTime(2026, 10, 28, 17, 0, 0, DateTimeKind.Utc), lesson);    // 00:00 ngày 29/10: sau kỳ

        _h.Clock.Set(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));   // ngày cuối của gói vẫn còn hiệu lực
        var quota = await _h.Service<PlanGuard, AiQuotaDto>(g => g.GetAiQuotaAsync(f.OrgId, default));

        Assert.Equal((true, 2), (quota.HasEffectivePlan, quota.Used));
    }

    [Fact]
    public async Task Upgrading_starts_a_new_period_and_the_turns_used_that_day_still_count()
    {
        var f = await SetupAsync();
        var lesson = await PublishableLessonAsync(f);
        var now = _h.Clock.GetUtcNow().UtcDateTime;
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, now, lesson);
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, now.AddDays(-3), lesson);   // trước ngày nâng cấp: thuộc kỳ cũ

        _h.Clock.Advance(TimeSpan.FromDays(3));
        await _h.QueryAsync(async db =>
        {
            var plan = new Plan { Name = "Nâng cao", Price = 500_000m, DurationDays = 30, MaxTeachers = 10, MaxAiRequests = 100 };
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            var today = VietnamTime.Today(_h.Clock.GetUtcNow());
            foreach (var s in await db.Subscriptions.Where(x => x.OrganizationId == f.OrgId).ToListAsync()) s.Status = SubscriptionStatus.CANCELLED;
            db.Subscriptions.Add(new Subscription
            {
                OrganizationId = f.OrgId, PlanId = plan.Id, Status = SubscriptionStatus.ACTIVE, StartDate = today,
                EndDate = Subscription.EndDateFor(today, 30),
            });
            return await db.SaveChangesAsync();
        });
        await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, _h.Clock.GetUtcNow().UtcDateTime.AddHours(-2), lesson);   // ngày nâng cấp, trước lúc nâng cấp

        var quota = await _h.Service<PlanGuard, AiQuotaDto>(g => g.GetAiQuotaAsync(f.OrgId, default));

        Assert.Equal(("Nâng cao", 1, 100, 99), (quota.PlanName, quota.Used, quota.Max, quota.Remaining));
    }

    [Fact]
    public async Task Ensuring_availability_refuses_when_there_is_no_plan_or_no_turn_left()
    {
        var f = await SetupAsync();
        var lesson = await PublishableLessonAsync(f);
        var now = _h.Clock.GetUtcNow().UtcDateTime;
        for (var i = 0; i < 20; i++) await AddAiRowAsync(f, AiTaskStatus.SUCCEEDED, now, lesson);

        var full = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<PlanGuard, AiQuotaDto>(g => g.EnsureAiAvailableAsync(f.OrgId, "soạn nội dung bằng AI", default)));
        Assert.Equal((422, "AI_QUOTA_REACHED"), (full.StatusCode, full.AppMessage.Code));
        Assert.Equal("Trường đã dùng hết 20 lượt AI của kỳ gói hiện tại (đến hết ngày 28/10/2026). Vui lòng liên hệ quản trị trường để nâng cấp gói.", full.Message);

        await _h.QueryAsync(async db =>
        {
            foreach (var s in await db.Subscriptions.ToListAsync()) s.Status = SubscriptionStatus.EXPIRED;
            return await db.SaveChangesAsync();
        });
        var none = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<PlanGuard, AiQuotaDto>(g => g.EnsureAiAvailableAsync(f.OrgId, "soạn nội dung bằng AI", default)));
        Assert.Equal((422, "PLAN_REQUIRED"), (none.StatusCode, none.AppMessage.Code));
        var quota = await _h.Service<PlanGuard, AiQuotaDto>(g => g.GetAiQuotaAsync(f.OrgId, default));
        Assert.Equal((false, 0, null), (quota.HasEffectivePlan, quota.Used, quota.Remaining));
    }

    // ---- Ràng buộc của bảng ----

    [Fact]
    public async Task Ai_generations_belong_to_exactly_one_lesson_or_one_heritage_and_the_task_type_must_match()
    {
        var f = await SetupAsync();
        var lesson = await PublishableLessonAsync(f);

        AiGeneration Row(AiTaskType type, long? lessonId, long? heritageId) =>
            new() { TaskType = type, LessonId = lessonId, HeritageId = heritageId, RequestedBy = f.TeacherId, Status = AiTaskStatus.SUCCEEDED };

        foreach (var good in new[]
                 {
                     Row(AiTaskType.LESSON_SUMMARY, lesson, null), Row(AiTaskType.QUIZ_GENERATION, lesson, null),
                     Row(AiTaskType.HERITAGE_TRANSLATION, null, f.HeritageId), Row(AiTaskType.HERITAGE_SUMMARY, null, f.HeritageId),
                 })
            await _h.QueryAsync(async db => { db.AiGenerations.Add(good); return await db.SaveChangesAsync(); });

        foreach (var bad in new[]
                 {
                     Row(AiTaskType.LESSON_SUMMARY, null, null),                // không thuộc đối tượng nào
                     Row(AiTaskType.LESSON_SUMMARY, lesson, f.HeritageId),      // thuộc cả hai
                     Row(AiTaskType.LESSON_SUMMARY, null, f.HeritageId),        // tác vụ bài học mà gắn di sản
                     Row(AiTaskType.HERITAGE_TRANSLATION, lesson, null),        // tác vụ di sản mà gắn bài học
                 })
            await _h.QueryAsync(async db =>
            {
                db.AiGenerations.Add(bad);
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                return 0;
            });
    }

    [Fact]
    public async Task Lesson_sections_need_a_year_for_milestones_a_source_for_excerpts_and_a_url_for_images()
    {
        var f = await SetupAsync();
        var lesson = await PublishableLessonAsync(f);

        LessonSection Section(LessonSectionType type, int? year = null, string? source = null, string? image = null) => new()
        {
            LessonId = lesson, SectionType = type, Body = "x", MilestoneYear = year, SourceCitation = source, ImageUrl = image, SortOrder = 9,
        };

        foreach (var good in new[]
                 {
                     Section(LessonSectionType.STORY), Section(LessonSectionType.READING), Section(LessonSectionType.REFLECTION),
                     Section(LessonSectionType.MILESTONE, year: -179), Section(LessonSectionType.SOURCE_EXCERPT, source: "Nguồn"),
                     Section(LessonSectionType.IMAGE, image: "https://a/b.jpg"),
                 })
            await _h.QueryAsync(async db => { db.LessonSections.Add(good); return await db.SaveChangesAsync(); });

        foreach (var bad in new[]
                 {
                     Section(LessonSectionType.MILESTONE), Section(LessonSectionType.SOURCE_EXCERPT), Section(LessonSectionType.IMAGE),
                 })
            await _h.QueryAsync(async db =>
            {
                db.LessonSections.Add(bad);
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                return 0;
            });
    }

    // ---- Phân quyền của dịch vụ ----

    [Fact]
    public async Task Only_teachers_use_the_authoring_service_and_only_students_open_lessons()
    {
        var f = await SetupAsync();
        var admin = new Actor(1, RoleCode.ADMIN, null, null);
        var orgAdmin = new Actor(2, RoleCode.ORG_ADMIN, f.OrgId, null);

        foreach (var actor in new[] { admin, orgAdmin })
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => _h.Service<LessonService, LessonPageDto>(s => s.ListAsync(actor, new LessonListQuery(null, null, null, null, null, null), default)));
            Assert.Equal(403, ex.StatusCode);
            var ex2 = await Assert.ThrowsAsync<AppException>(() => _h.Service<LessonService, LessonDetailDto>(s => s.CreateAsync(actor, new CreateLessonRequest(f.ClassId, "x", [f.HeritageId]), default)));
            Assert.Equal(403, ex2.StatusCode);
            var ex3 = await Assert.ThrowsAsync<AppException>(() => _h.Service<LessonAiService, AiQuotaDto>(s => s.GetQuotaAsync(actor, default)));
            Assert.Equal(403, ex3.StatusCode);
        }

        var asTeacher = await Assert.ThrowsAsync<AppException>(() => _h.Service<StudentLessonService, StudentLessonDto>(s => s.OpenByCodeAsync(f.Teacher, "BH222222", default)));
        Assert.Equal(403, asTeacher.StatusCode);
    }

    // ---- Trạng thái bài kiểm tra với học sinh ----

    [Theory]
    [InlineData(QuizStatus.PUBLISHED, -1, 1, null, StudentQuizState.OPEN)]
    [InlineData(QuizStatus.PUBLISHED, 1, 2, null, StudentQuizState.NOT_OPEN)]
    [InlineData(QuizStatus.PUBLISHED, -2, -1, null, StudentQuizState.CLOSED)]
    [InlineData(QuizStatus.CLOSED, -2, 5, null, StudentQuizState.CLOSED)]
    [InlineData(QuizStatus.PUBLISHED, null, null, null, StudentQuizState.OPEN)]
    [InlineData(QuizStatus.PUBLISHED, -1, 1, AttemptStatus.IN_PROGRESS, StudentQuizState.IN_PROGRESS)]
    [InlineData(QuizStatus.PUBLISHED, -1, 1, AttemptStatus.SUBMITTED, StudentQuizState.SUBMITTED)]
    [InlineData(QuizStatus.CLOSED, -2, -1, AttemptStatus.EXPIRED, StudentQuizState.SUBMITTED)]
    public void The_quiz_state_for_a_student_follows_the_attempt_first_then_the_times(QuizStatus status, int? openHours, int? closeHours,
        AttemptStatus? attempt, StudentQuizState expected)
    {
        var now = new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc);
        DateTime? At(int? hours) => hours is null ? null : now.AddHours(hours.Value);

        Assert.Equal(expected, StudentLessonService.StateOf(status, At(openHours), At(closeHours), attempt, now));
    }

    [Fact]
    public void The_quiz_opens_exactly_at_open_time_and_closes_exactly_at_close_time()
    {
        var open = new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc);
        var close = open.AddHours(1);
        Assert.Equal(StudentQuizState.NOT_OPEN, StudentLessonService.StateOf(QuizStatus.PUBLISHED, open, close, null, open.AddTicks(-1)));
        Assert.Equal(StudentQuizState.OPEN, StudentLessonService.StateOf(QuizStatus.PUBLISHED, open, close, null, open));
        Assert.Equal(StudentQuizState.OPEN, StudentLessonService.StateOf(QuizStatus.PUBLISHED, open, close, null, close.AddTicks(-1)));
        Assert.Equal(StudentQuizState.CLOSED, StudentLessonService.StateOf(QuizStatus.PUBLISHED, open, close, null, close));
        Assert.Equal(["Chưa mở", "Đang mở", "Em đang làm", "Em đã làm", "Đã đóng"],
            Enum.GetValues<StudentQuizState>().Select(StudentLessonService.LabelOf));
    }
}
