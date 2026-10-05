using System.Net;
using DSVHVN.Application.Classes;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Students;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Học sinh đã đăng nhập mở bài học bằng mã bài học: chỉ bài đã xuất bản của đúng lớp của em, một thông báo chung khi sai, bài trả về theo khung và
/// luồng của cấp lớp em (nội dung, các phần, thẻ di sản, bài kiểm tra kèm trạng thái), mở bài không ghi CSDL và không phụ thuộc gói của trường.
/// </summary>
public sealed class StudentLessonApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly LessonTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    private const string WrongCodeMessage = "Mã bài học chưa đúng hoặc không dành cho lớp của em. Em hãy kiểm tra lại mã thầy cô đã đưa.";

    private async Task<(ClassroomClient.School School, ClassDto Class, LessonDetailDto Lesson, string Student)> SetupAsync(int level, int events = 6)
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(level, "Lớp " + level);
        var h = await _k.HeritageAsync("Hoàng thành", events);
        var lesson = await _k.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id], "Thăng Long xưa");
        var student = await _k.StudentTokenAsync(school.TeacherToken, schoolClass.Id);
        return (school, schoolClass, lesson, student);
    }

    private static async Task<StudentLessonDto> OpenOkAsync(LessonTestKit k, string token, string code)
    {
        var (res, body) = await k.OpenAsync(token, code);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{code}: {(int)res.StatusCode} {body.ErrorCode} {body.Message}");
        return body.Data!;
    }

    private async Task AssertWrongCodeAsync(string token, string code)
    {
        var (res, body) = await _k.OpenAsync(token, code);
        Assert.Equal((HttpStatusCode.NotFound, "LESSON_CODE_INVALID", WrongCodeMessage), (res.StatusCode, body.ErrorCode, body.Message));
        Assert.False(body.Success);
        Assert.Null(body.Data);
    }

    // ---- Bài theo cấp ----

    [Fact]
    public async Task Level_one_student_gets_the_step_by_step_lesson_with_story_steps_pictures_and_heritage_cards()
    {
        var (school, _, lesson, student) = await SetupAsync(1);

        // Gõ chữ thường, có khoảng trắng và dấu gạch vẫn đúng.
        var typed = lesson.AccessCode!.ToLowerInvariant().Insert(2, " ").Insert(6, "-");
        var dto = await OpenOkAsync(_k, student, typed);

        Assert.Equal((lesson.Id, lesson.AccessCode, "Thăng Long xưa", "Lớp 1", 1, "Cấp 1"),
            (dto.Id, dto.AccessCode, dto.Title, dto.ClassName, dto.SchoolLevel, dto.SchoolLevelName));
        Assert.Equal(school.Teacher.FullName, dto.TeacherName);
        Assert.Equal(("STEP_BY_STEP", true), (dto.Layout.Layout, dto.Layout.SequentialFlow));
        Assert.Equal(["Nghe chuyện", "Xem hình", "Chơi", "Nhận sao"], dto.Layout.Steps.Select(s => s.Label));
        Assert.Equal(lesson.Summary, dto.Summary);
        Assert.Equal(lesson.KeyPoints, dto.KeyPoints);
        Assert.All(dto.Sections, s => Assert.Contains(s.Type, new[] { LessonSectionType.STORY, LessonSectionType.IMAGE }));
        Assert.All(dto.Sections.Where(s => s.Type == LessonSectionType.STORY), s =>
        {
            Assert.NotNull(s.ImageUrl);
            Assert.NotNull(s.Image?.Author);
            Assert.NotNull(s.Image?.License);
            Assert.NotNull(s.Image?.Source);
        });
        Assert.Equal(lesson.Sections.Select(s => s.Body), dto.Sections.Select(s => s.Body));

        var card = Assert.Single(dto.Heritages);
        Assert.Equal((lesson.Heritages[0].Id, true, "Thành phố Hà Nội"), (card.Id, card.HasTimeline, card.ProvinceName));
        Assert.False(string.IsNullOrEmpty(card.Slug));
        Assert.NotNull(card.ThumbnailUrl);
        Assert.Empty(dto.Quizzes);
    }

    [Fact]
    public async Task Level_two_student_gets_free_steps_with_a_reading_and_milestones()
    {
        var (_, _, lesson, student) = await SetupAsync(2);
        var dto = await OpenOkAsync(_k, student, lesson.AccessCode!);

        Assert.Equal((2, "FREE_STEPS", false), (dto.SchoolLevel, dto.Layout.Layout, dto.Layout.SequentialFlow));
        Assert.Equal(["Đọc bài", "Xem dòng thời gian", "Làm bài kiểm tra", "Xem giải thích"], dto.Layout.Steps.Select(s => s.Label));
        Assert.Contains(dto.Sections, s => s.Type == LessonSectionType.MILESTONE && s.MilestoneYear is not null && s.MilestoneLabel is not null);
        Assert.All(dto.Sections, s => Assert.Contains(s.Type, new[] { LessonSectionType.IMAGE, LessonSectionType.MILESTONE, LessonSectionType.READING }));
        Assert.Equal(lesson.Summary, dto.Summary);
    }

    [Fact]
    public async Task Level_three_student_gets_the_document_with_excerpts_that_carry_their_sources()
    {
        var (_, _, lesson, student) = await SetupAsync(3);
        var dto = await OpenOkAsync(_k, student, lesson.AccessCode!);

        Assert.Equal((3, "DOCUMENT", false), (dto.SchoolLevel, dto.Layout.Layout, dto.Layout.SequentialFlow));
        Assert.Equal(["Đọc tài liệu", "Làm bài kiểm tra có giờ", "Xem phân tích kết quả"], dto.Layout.Steps.Select(s => s.Label));
        var excerpts = dto.Sections.Where(s => s.Type == LessonSectionType.SOURCE_EXCERPT).ToList();
        Assert.NotEmpty(excerpts);
        Assert.All(excerpts, e => Assert.False(string.IsNullOrWhiteSpace(e.SourceCitation)));
        Assert.Contains(dto.Sections, s => s.Type == LessonSectionType.REFLECTION);
    }

    [Fact]
    public async Task The_student_sees_only_the_sections_of_the_current_level_after_the_teacher_changes_the_grade()
    {
        var (school, schoolClass, lesson, student) = await SetupAsync(2);
        Assert.Contains((await OpenOkAsync(_k, student, lesson.AccessCode!)).Sections, s => s.Type == LessonSectionType.IMAGE);

        // Giáo viên chuyển lớp từ khối 8 lên khối 11 (xác nhận đổi cấp): nội dung bài giữ nguyên, em thấy khung cấp 3.
        var (res, body) = await Http.PutAsync<ClassDto>($"/api/v1/teacher/classes/{schoolClass.Id}",
            new { name = schoolClass.Name, grade = 11, schoolYear = schoolClass.SchoolYear, confirmLevelChange = true }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(3, body.Data!.SchoolLevel);

        var dto = await OpenOkAsync(_k, student, lesson.AccessCode!);   // token cũ mang cấp 2; cấp lấy từ lớp hiện tại
        Assert.Equal((3, "DOCUMENT"), (dto.SchoolLevel, dto.Layout.Layout));
        Assert.DoesNotContain(dto.Sections, s => s.Type == LessonSectionType.IMAGE);
        Assert.Contains(dto.Sections, s => s.Type == LessonSectionType.MILESTONE);
        Assert.Equal(lesson.Summary, dto.Summary);

        // Giáo viên vẫn thấy đủ các phần đã soạn và cảnh báo phần thuộc khung cấp khác.
        var teacherView = await _k.GetAsync(school.TeacherToken, lesson.Id);
        Assert.Equal(lesson.Sections.Count, teacherView.Sections.Count);
        Assert.Contains(teacherView.Readiness.Warnings, w => w.Contains("thuộc khung của cấp khác"));
    }

    // ---- Điều kiện mở bài ----

    [Fact]
    public async Task Every_way_the_code_does_not_apply_gives_the_same_not_found_message()
    {
        var (school, schoolClass, lesson, student) = await SetupAsync(2);

        // Mã không tồn tại, sai dạng, rỗng-hợp-lệ-sau-chuẩn-hóa.
        await AssertWrongCodeAsync(student, "BH2222AA");
        await AssertWrongCodeAsync(student, "ABC");
        await AssertWrongCodeAsync(student, "BH0O1I22");

        // Bài của lớp khác (cùng giáo viên) và của trường khác: cùng một thông báo, không lộ mã có thật.
        var otherClass = await _k.Classroom.CreateClassAsync(school.TeacherToken, "Lớp khác", 8);
        var h = await _k.HeritageAsync("Văn Miếu", events: 3);
        var inOtherClass = await _k.PublishedLessonAsync(school.TeacherToken, otherClass.Id, [h.Id], "Bài của lớp khác");
        await AssertWrongCodeAsync(student, inOtherClass.AccessCode!);

        var (school2, class2) = await _k.SchoolWithClassAsync(2);
        var otherSchool = await _k.PublishedLessonAsync(school2.TeacherToken, class2.Id, [h.Id], "Bài của trường khác");
        await AssertWrongCodeAsync(student, otherSchool.AccessCode!);

        // Bài lưu trữ như không tồn tại; xuất bản lại thì mở được, cùng mã.
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"{LessonTestKit.Teacher}/{lesson.Id}/archive", null, school.TeacherToken));
        await AssertWrongCodeAsync(student, lesson.AccessCode!);
        await _k.PublishAsync(school.TeacherToken, lesson.Id);
        Assert.Equal(lesson.Id, (await OpenOkAsync(_k, student, lesson.AccessCode!)).Id);

        // Bài đã xóa mềm như không tồn tại.
        var deleted = await _k.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id], "Bài sẽ xóa");
        Assert.Equal(deleted.Id, (await OpenOkAsync(_k, student, deleted.AccessCode!)).Id);
        await Http.DeleteAsync<object>($"{LessonTestKit.Teacher}/{deleted.Id}", school.TeacherToken);
        await AssertWrongCodeAsync(student, deleted.AccessCode!);

        // Mã rỗng (sau chuẩn hóa) là lỗi nhập, không tính là nhập sai mã.
        var (empty, emptyBody) = await _k.OpenAsync(student, " - ");
        Assert.Equal((HttpStatusCode.BadRequest, "REQUIRED"), (empty.StatusCode, emptyBody.ErrorCode));
    }

    [Fact]
    public async Task Only_a_signed_in_student_who_finished_the_first_password_change_can_open_a_lesson()
    {
        var (school, schoolClass, lesson, student) = await SetupAsync(2);

        var (anonymous, _) = await Http.GetAsync<object>($"{LessonTestKit.Student}/{lesson.AccessCode}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var (asTeacher, _) = await _k.OpenAsync(school.TeacherToken, lesson.AccessCode!);
        Assert.Equal(HttpStatusCode.Forbidden, asTeacher.StatusCode);
        var (asOrgAdmin, _) = await _k.OpenAsync(school.OrgAdminToken, lesson.AccessCode!);
        Assert.Equal(HttpStatusCode.Forbidden, asOrgAdmin.StatusCode);

        // Học sinh mới đăng nhập bằng mật khẩu tạm, chưa đổi mật khẩu: 403 yêu cầu đặt mật khẩu mới.
        var created = await _k.Classroom.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Lê Thị Mai");
        var first = await _k.Classroom.StudentLoginAsync(created.Sheet.Entries.Single().StudentCode!, created.Sheet.Entries.Single().TemporaryPassword);
        var (res, body) = await _k.OpenAsync(first.AccessToken, lesson.AccessCode!);
        Assert.Equal((HttpStatusCode.Forbidden, "PASSWORD_CHANGE_REQUIRED"), (res.StatusCode, body.ErrorCode));

        // Học sinh bị vô hiệu: mất quyền ngay ở yêu cầu kế tiếp.
        var students = await DiscoveryTestKit.Expect(Http.GetAsync<StudentPageDto>($"/api/v1/teacher/classes/{schoolClass.Id}/students", school.TeacherToken));
        var target = students.Items.First(s => s.FullName == "Nguyễn Văn An");
        var (off, _) = await Http.PostAsync<object>($"/api/v1/teacher/classes/{schoolClass.Id}/students/{target.Id}/deactivate", null, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var (blocked, _) = await _k.OpenAsync(student, lesson.AccessCode!);
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
    }

    // ---- Bài kiểm tra, di sản, ghi CSDL, gói ----

    [Fact]
    public async Task The_quiz_list_shows_published_and_closed_quizzes_with_the_state_for_this_student()
    {
        var (_, _, lesson, student) = await SetupAsync(2);
        var now = DateTime.UtcNow;
        var open = await _k.AddQuizAsync(lesson.Id, QuizStatus.PUBLISHED, now.AddHours(-2), now.AddDays(1), "Đang mở");
        var later = await _k.AddQuizAsync(lesson.Id, QuizStatus.PUBLISHED, now.AddDays(1), now.AddDays(2), "Chưa mở");
        var expired = await _k.AddQuizAsync(lesson.Id, QuizStatus.PUBLISHED, now.AddDays(-3), now.AddDays(-1), "Hết giờ");
        var closed = await _k.AddQuizAsync(lesson.Id, QuizStatus.CLOSED, now.AddDays(-3), now.AddDays(5), "Đóng tay");
        await _k.AddQuizAsync(lesson.Id, QuizStatus.DRAFT, null, null, "Nháp");
        var done = await _k.AddQuizAsync(lesson.Id, QuizStatus.PUBLISHED, now.AddHours(-2), now.AddDays(1), "Em đã làm");
        var working = await _k.AddQuizAsync(lesson.Id, QuizStatus.PUBLISHED, now.AddHours(-2), now.AddDays(1), "Em đang làm");

        var studentId = await factory.WithDbAsync(db => db.Students.Where(s => s.FullName == "Nguyễn Văn An").OrderByDescending(s => s.Id).Select(s => s.Id).FirstAsync());
        await factory.WithDbAsync(async db =>
        {
            db.Attempts.Add(new Attempt { QuizId = done, StudentId = studentId, Status = AttemptStatus.SUBMITTED, StartedAt = now.AddMinutes(-30) });
            db.Attempts.Add(new Attempt { QuizId = working, StudentId = studentId, Status = AttemptStatus.IN_PROGRESS, StartedAt = now.AddMinutes(-5) });
            return await db.SaveChangesAsync();
        });

        var dto = await OpenOkAsync(_k, student, lesson.AccessCode!);

        var states = dto.Quizzes.ToDictionary(q => q.Name!, q => (q.State, q.StateLabel));
        Assert.Equal(6, states.Count);                // bài nháp không hiện
        Assert.Equal((StudentQuizState.OPEN, "Đang mở"), states["Đang mở"]);
        Assert.Equal((StudentQuizState.NOT_OPEN, "Chưa mở"), states["Chưa mở"]);
        Assert.Equal((StudentQuizState.CLOSED, "Đã đóng"), states["Hết giờ"]);
        Assert.Equal((StudentQuizState.CLOSED, "Đã đóng"), states["Đóng tay"]);
        Assert.Equal((StudentQuizState.SUBMITTED, "Em đã làm"), states["Em đã làm"]);
        Assert.Equal((StudentQuizState.IN_PROGRESS, "Em đang làm"), states["Em đang làm"]);
        Assert.Equal(15, dto.Quizzes.First().TimeLimitMinutes);
        Assert.Equal(0, dto.Quizzes.First().QuestionCount);
        Assert.NotEqual(0, open + later + expired + closed);
    }

    [Fact]
    public async Task A_hidden_heritage_card_disappears_but_the_lesson_stays()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var h1 = await _k.HeritageAsync("Hoàng thành", events: 3);
        var h2 = await _k.HeritageAsync("Văn Miếu");
        var lesson = await _k.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [h1.Id, h2.Id]);
        var student = await _k.StudentTokenAsync(school.TeacherToken, schoolClass.Id);
        Assert.Equal(2, (await OpenOkAsync(_k, student, lesson.AccessCode!)).Heritages.Count);

        await _k.ArchiveHeritageAsync(h2.Id);

        var dto = await OpenOkAsync(_k, student, lesson.AccessCode!);
        Assert.Equal([h1.Id], dto.Heritages.Select(h => h.Id));
        Assert.Equal(lesson.Summary, dto.Summary);
    }

    [Fact]
    public async Task Opening_a_lesson_writes_nothing_works_without_a_plan_and_shows_no_contact_details()
    {
        var (school, _, lesson, student) = await SetupAsync(2);
        var snapshot = async () => await factory.WithDbAsync(async db => (
            await db.AuditLogs.CountAsync(), await db.AiGenerations.CountAsync(), await db.Lessons.CountAsync(),
            await db.Lessons.Where(l => l.Id == lesson.Id).Select(l => l.UpdatedAt).SingleAsync(), await db.Attempts.CountAsync()));
        var before = await snapshot();

        // Trường hết gói: học sinh vẫn mở bài bình thường.
        await factory.WithDbAsync(async db =>
        {
            foreach (var s in await db.Subscriptions.Where(x => x.OrganizationId == school.OrgId).ToListAsync()) s.Status = SubscriptionStatus.EXPIRED;
            return await db.SaveChangesAsync();
        });

        for (var i = 0; i < 3; i++) await OpenOkAsync(_k, student, lesson.AccessCode!);
        Assert.Equal(before, await snapshot());

        // Không có email, số điện thoại hay tài khoản của giáo viên trong phản hồi.
        var raw = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{LessonTestKit.Student}/{lesson.AccessCode}")
        {
            Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", student) },
        });
        var json = await raw.Content.ReadAsStringAsync();
        Assert.DoesNotContain(school.Teacher.Email!, json);
        Assert.DoesNotContain("\"username\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"email\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"phone\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
