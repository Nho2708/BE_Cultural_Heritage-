using System.Net;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// "Xem như học sinh" của giáo viên: bài (kể cả Nháp chưa có mã) trả đúng khung, luồng và dữ liệu mà học sinh lớp đó thấy; chỉ giáo viên của bài;
/// không ghi gì, không gọi Dịch vụ AI, không tính lượt AI.
/// </summary>
public sealed class TeacherLessonPreviewApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly LessonTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    private static string PreviewUrl(long lessonId) => $"{LessonTestKit.Teacher}/{lessonId}/preview";

    private async Task<LessonPreviewDto> PreviewOkAsync(string token, long lessonId) =>
        await DiscoveryTestKit.Expect(Http.GetAsync<LessonPreviewDto>(PreviewUrl(lessonId), token));

    private async Task<(ClassroomClient.School School, LessonDetailDto Draft)> DraftLessonAsync(int level)
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(level, "Lớp " + level);
        var heritage = await _k.HeritageAsync("Hoàng thành", 6);
        var lesson = await _k.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id], "Bài thử cấp " + level);
        await _k.SaveDraftAsync(school.TeacherToken, lesson.Id, await _k.ComposeAsync(school.TeacherToken, lesson.Id));
        return (school, lesson);
    }

    [Theory]
    [InlineData(1, "STEP_BY_STEP")]
    [InlineData(2, "FREE_STEPS")]
    [InlineData(3, "DOCUMENT")]
    public async Task A_draft_lesson_previews_in_the_layout_of_the_class_level_without_a_code(int level, string layout)
    {
        var (school, draft) = await DraftLessonAsync(level);

        var preview = await PreviewOkAsync(school.TeacherToken, draft.Id);

        Assert.True(preview.QuizzesViewOnly);
        Assert.Equal(ContentStatus.DRAFT, preview.Status);
        var lesson = preview.Lesson;
        Assert.Equal((draft.Id, level, "Lớp " + level, school.Teacher.FullName), (lesson.Id, lesson.SchoolLevel, lesson.ClassName, lesson.TeacherName));
        Assert.Equal(layout, lesson.Layout.Layout);
        Assert.Equal(string.Empty, lesson.AccessCode);
        Assert.NotEmpty(lesson.Sections);
        Assert.Single(lesson.Heritages);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task The_preview_is_exactly_what_a_student_of_that_class_sees_after_publishing(int level)
    {
        var (school, draft) = await DraftLessonAsync(level);
        var before = await PreviewOkAsync(school.TeacherToken, draft.Id);

        var published = (await _k.PublishAsync(school.TeacherToken, draft.Id)).Lesson;
        var classId = (await factory.WithDbAsync(db => db.Lessons.Where(l => l.Id == draft.Id).Select(l => l.ClassId).SingleAsync()));
        var student = await _k.StudentTokenAsync(school.TeacherToken, classId);
        var seen = (await DiscoveryTestKit.Expect(_k.OpenAsync(student, published.AccessCode!)));
        var after = await PreviewOkAsync(school.TeacherToken, draft.Id);

        Assert.Equal(ContentStatus.PUBLISHED, after.Status);
        Assert.Equal(published.AccessCode, after.Lesson.AccessCode);

        foreach (var preview in new[] { before.Lesson, after.Lesson })
        {
            Assert.Equal((seen.Layout.Layout, seen.Layout.SequentialFlow, seen.SchoolLevel, seen.SchoolLevelName, seen.Title, seen.Summary),
                (preview.Layout.Layout, preview.Layout.SequentialFlow, preview.SchoolLevel, preview.SchoolLevelName, preview.Title, preview.Summary));
            Assert.Equal(seen.Layout.Steps.Select(s => s.Label), preview.Layout.Steps.Select(s => s.Label));
            Assert.Equal(seen.KeyPoints, preview.KeyPoints);
            Assert.Equal(seen.Sections.Select(s => (s.Type, s.Title, s.Body, s.ImageUrl, s.MilestoneYear, s.SortOrder)),
                preview.Sections.Select(s => (s.Type, s.Title, s.Body, s.ImageUrl, s.MilestoneYear, s.SortOrder)));
            Assert.Equal(seen.Heritages.Select(h => (h.Id, h.Slug, h.Name)), preview.Heritages.Select(h => (h.Id, h.Slug, h.Name)));
        }
    }

    [Fact]
    public async Task Previewing_writes_nothing_and_does_not_call_the_ai_or_use_up_its_quota()
    {
        var (school, draft) = await DraftLessonAsync(2);
        var quotaBefore = await DiscoveryTestKit.Expect(Http.GetAsync<AiQuotaDto>($"{LessonTestKit.Teacher}/ai-quota", school.TeacherToken));
        factory.Ai.Reset();
        async Task<int[]> CountsAsync() => await factory.WithDbAsync(async db => new[]
        {
            await db.AiGenerations.CountAsync(), await db.Attempts.CountAsync(), await db.AuditLogs.CountAsync(),
            await db.Lessons.CountAsync(), await db.LessonSections.CountAsync(), await db.Quizzes.CountAsync(),
        });
        var updatedBefore = await factory.WithDbAsync(db => db.Lessons.Where(l => l.Id == draft.Id).Select(l => l.UpdatedAt).SingleAsync());
        var countsBefore = await CountsAsync();
        var callsBefore = factory.Ai.Calls;

        for (var i = 0; i < 3; i++) await PreviewOkAsync(school.TeacherToken, draft.Id);

        Assert.Equal(countsBefore, await CountsAsync());
        Assert.Equal(callsBefore, factory.Ai.Calls);
        Assert.Equal(updatedBefore, await factory.WithDbAsync(db => db.Lessons.Where(l => l.Id == draft.Id).Select(l => l.UpdatedAt).SingleAsync()));
        var quotaAfter = await DiscoveryTestKit.Expect(Http.GetAsync<AiQuotaDto>($"{LessonTestKit.Teacher}/ai-quota", school.TeacherToken));
        Assert.Equal(quotaBefore, quotaAfter);
    }

    [Fact]
    public async Task The_response_is_not_cached_and_an_archived_lesson_can_still_be_previewed()
    {
        var (school, draft) = await DraftLessonAsync(1);
        await _k.PublishAsync(school.TeacherToken, draft.Id);
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"{LessonTestKit.Teacher}/{draft.Id}/archive", null, school.TeacherToken));

        var (res, body) = await Http.GetAsync<LessonPreviewDto>(PreviewUrl(draft.Id), school.TeacherToken);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("no-store", res.Headers.CacheControl?.ToString());
        Assert.Equal(ContentStatus.ARCHIVED, body.Data!.Status);
    }

    [Fact]
    public async Task Quizzes_show_as_a_student_who_has_not_started_would_see_them_and_drafts_stay_hidden()
    {
        var (school, draft) = await DraftLessonAsync(3);
        var now = DateTime.UtcNow;
        await _k.AddQuizAsync(draft.Id, QuizStatus.PUBLISHED, now.AddHours(-1), now.AddDays(1), "Đang mở");
        await _k.AddQuizAsync(draft.Id, QuizStatus.PUBLISHED, now.AddDays(1), now.AddDays(2), "Chưa mở");
        await _k.AddQuizAsync(draft.Id, QuizStatus.DRAFT, null, null, "Nháp");

        var preview = await PreviewOkAsync(school.TeacherToken, draft.Id);

        var states = preview.Lesson.Quizzes.ToDictionary(q => q.Name!, q => q.State);
        Assert.Equal(2, states.Count);
        Assert.Equal(StudentQuizState.OPEN, states["Đang mở"]);
        Assert.Equal(StudentQuizState.NOT_OPEN, states["Chưa mở"]);
        Assert.True(preview.QuizzesViewOnly);
    }

    [Fact]
    public async Task Only_the_teacher_of_the_lesson_can_preview_it()
    {
        var (school, draft) = await DraftLessonAsync(2);
        var (_, otherTeacher) = await _k.Classroom.AnotherTeacherAsync(school);
        var otherSchool = await _k.Classroom.SchoolAsync();
        var student = await _k.StudentTokenAsync(school.TeacherToken,
            await factory.WithDbAsync(db => db.Lessons.Where(l => l.Id == draft.Id).Select(l => l.ClassId).SingleAsync()));
        var admin = await _k.Discovery.Api.AdminTokenAsync();

        async Task<HttpStatusCode> StatusAsync(string? token) => (await Http.GetAsync<LessonPreviewDto>(PreviewUrl(draft.Id), token)).Response.StatusCode;

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(school.TeacherToken));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(otherTeacher));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(otherSchool.TeacherToken));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(school.OrgAdminToken));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(admin));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(student));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(null));
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync<LessonPreviewDto>(PreviewUrl(987654321), school.TeacherToken)).Response.StatusCode);
    }

    [Fact]
    public async Task A_teacher_cannot_preview_another_teachers_lesson_of_the_same_school()
    {
        var (school, draft) = await DraftLessonAsync(1);
        var (_, colleague) = await _k.Classroom.AnotherTeacherAsync(school);

        var (res, body) = await Http.GetAsync<LessonPreviewDto>(PreviewUrl(draft.Id), colleague);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Null(body.Data);
    }
}
