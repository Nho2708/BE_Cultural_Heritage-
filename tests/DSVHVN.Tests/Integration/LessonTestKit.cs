using System.Net;
using DSVHVN.Application.Classes;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Students;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dựng dữ liệu cho test bài học qua chính API: trường có giáo viên, lớp theo cấp, di sản đã xuất bản (có ảnh, nguồn, dòng thời gian),
/// bài học, bản nháp AI lưu thành nội dung bài, học sinh đã đăng nhập sẵn.
/// </summary>
public sealed class LessonTestKit(ApiFactory factory)
{
    public const string Teacher = "/api/v1/teacher/lessons";
    public const string Student = "/api/v1/student/lessons";

    public ClassroomClient Classroom { get; } = new(factory);
    public DiscoveryTestKit Discovery { get; } = new(factory);
    public ApiFactory Factory { get; } = factory;
    public HttpClient Http => Classroom.Http;

    public const string Story =
        "Hoàng thành Thăng Long là trung tâm quyền lực của nhiều triều đại phong kiến Việt Nam suốt hơn mười thế kỷ. " +
        "Khu di tích nằm giữa lòng Hà Nội và được UNESCO ghi danh là Di sản văn hóa thế giới vào năm hai nghìn không trăm mười. " +
        "Các nhà khảo cổ đã tìm thấy nhiều lớp văn hóa chồng lên nhau từ thế kỷ thứ bảy đến thế kỷ mười chín. " +
        "Trung tâm của hoàng thành là điện Kính Thiên, nơi các vua nhà Lê thiết triều. " +
        "Cổng Đoan Môn là lối vào chính của khu cung điện xưa. " +
        "Ngày nay du khách có thể tham quan khu khảo cổ học số mười tám Hoàng Diệu để xem các di vật quý. ";

    /// <summary>Di sản đã xuất bản có nội dung nhiều câu, một ảnh đại diện, hai nguồn và tùy chọn dòng thời gian với <paramref name="events"/> sự kiện.</summary>
    public async Task<HeritageDetailDto> HeritageAsync(string name, int events = 0, string provinceCode = "01")
    {
        var admin = await Discovery.Api.AdminTokenAsync();
        var heritage = await Discovery.PublishedAsync(admin, new DiscoveryTestKit.Spec(name + " " + DiscoveryTestKit.Unique(), ProvinceCode: provinceCode, Extra: Story));
        if (events > 0)
        {
            var phase = await Discovery.AddPhaseAsync(admin, heritage.Id, "Thời Lý", 1010, 1225);
            for (var i = 0; i < events; i++)
                await Discovery.AddEventAsync(admin, phase.Id, $"Sự kiện {i + 1} của {name}", 1010 + i * 20);
        }
        return heritage;
    }

    public async Task<HeritageDetailDto> HeritageOnlyAsync(long id)
    {
        var admin = await Discovery.Api.AdminTokenAsync();
        return await DiscoveryTestKit.Expect(Http.GetAsync<HeritageDetailDto>($"{DiscoveryTestKit.Admin}/heritages/{id}", admin));
    }

    public async Task ArchiveHeritageAsync(long id)
    {
        var admin = await Discovery.Api.AdminTokenAsync();
        await DiscoveryTestKit.Expect(Http.PostAsync<HeritageDetailDto>($"{DiscoveryTestKit.Admin}/heritages/{id}/archive", null, admin));
    }

    /// <summary>Trường có một giáo viên và một lớp (khối 3, 8 hoặc 11 theo cấp 1, 2, 3).</summary>
    public async Task<(ClassroomClient.School School, ClassDto Class)> SchoolWithClassAsync(int level, string className = "Lớp thử")
    {
        var school = await Classroom.SchoolAsync();
        var grade = level switch { 1 => 3, 2 => 8, _ => 11 };
        return (school, await Classroom.CreateClassAsync(school.TeacherToken, className, grade));
    }

    public async Task<LessonDetailDto> CreateLessonAsync(string token, long classId, IEnumerable<long> heritageIds, string title = "Bài học thử") =>
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>(Teacher, new { classId, title, heritageIds }, token), HttpStatusCode.Created);

    public async Task<LessonDetailDto> GetAsync(string token, long lessonId) =>
        await DiscoveryTestKit.Expect(Http.GetAsync<LessonDetailDto>($"{Teacher}/{lessonId}", token));

    public async Task<LessonDraftDto> ComposeAsync(string token, long lessonId, LessonLength length = LessonLength.MEDIUM) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDraftDto>($"{Teacher}/{lessonId}/ai/compose", new { length }, token));

    /// <summary>Lưu bản nháp AI thành nội dung bài như giáo viên bấm chấp nhận: tóm tắt, ý chính và các phần.</summary>
    public async Task<LessonDetailDto> SaveDraftAsync(string token, long lessonId, LessonDraftDto draft) =>
        await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{Teacher}/{lessonId}", ToUpdate(draft), token));

    public static object ToUpdate(LessonDraftDto draft) => new
    {
        summary = draft.Summary,
        keyPoints = draft.KeyPoints,
        sections = draft.Sections.Select(ToSection).ToList(),
    };

    public static object ToSection(LessonSectionDto s) => new
    {
        type = s.Type, title = s.Title, body = s.Body, imageUrl = s.ImageUrl, milestoneYear = s.MilestoneYear, eventId = s.EventId,
        sourceCitation = s.SourceCitation,
    };

    public async Task<PublishLessonResultDto> PublishAsync(string token, long lessonId) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<PublishLessonResultDto>($"{Teacher}/{lessonId}/publish", null, token));

    /// <summary>Bài đã soạn bằng AI, lưu và xuất bản: trả bài đã xuất bản có mã.</summary>
    public async Task<LessonDetailDto> PublishedLessonAsync(string token, long classId, IEnumerable<long> heritageIds, string title = "Bài học thử")
    {
        var lesson = await CreateLessonAsync(token, classId, heritageIds, title);
        await SaveDraftAsync(token, lesson.Id, await ComposeAsync(token, lesson.Id));
        return (await PublishAsync(token, lesson.Id)).Lesson;
    }

    /// <summary>Học sinh mới của lớp, đã đổi mật khẩu lần đầu; trả token dùng khu học.</summary>
    public async Task<string> StudentTokenAsync(string teacherToken, long classId, string fullName = "Nguyễn Văn An")
    {
        var created = await Classroom.AddStudentAsync(teacherToken, classId, fullName);
        return await Classroom.ReadyStudentTokenAsync(created.Sheet.Entries.Single());
    }

    public Task<(HttpResponseMessage Response, Envelope<StudentLessonDto> Body)> OpenAsync(string studentToken, string code) =>
        Http.GetAsync<StudentLessonDto>($"{Student}/{Uri.EscapeDataString(code)}", studentToken);

    public Task<(HttpResponseMessage Response, Envelope<DSVHVN.Application.Attempts.LessonCompletionDto> Body)> CompleteRawAsync(string studentToken, string code) =>
        Http.PostAsync<DSVHVN.Application.Attempts.LessonCompletionDto>($"{Student}/{Uri.EscapeDataString(code)}/complete", null, studentToken);

    public Task<(HttpResponseMessage Response, Envelope<DSVHVN.Application.Attempts.StudentRecallDto> Body)> RecallRawAsync(string studentToken, string code) =>
        Http.GetAsync<DSVHVN.Application.Attempts.StudentRecallDto>($"{Student}/{Uri.EscapeDataString(code)}/recall", studentToken);

    /// <summary>Thêm bài kiểm tra cho bài thẳng vào CSDL (luồng bài kiểm tra chưa có API ở nhánh này).</summary>
    public async Task<long> AddQuizAsync(long lessonId, QuizStatus status, DateTime? openAt, DateTime? closeAt, string name = "Bài kiểm tra thử") =>
        await Factory.WithDbAsync(async db =>
        {
            var teacherId = await db.Lessons.Where(l => l.Id == lessonId).Select(l => l.TeacherId).SingleAsync();
            var quiz = new Quiz
            {
                LessonId = lessonId, CreatedBy = teacherId, Name = name, Status = status, OpenAt = openAt, CloseAt = closeAt, TimeLimitMinutes = 15,
            };
            db.Quizzes.Add(quiz);
            await db.SaveChangesAsync();
            return quiz.Id;
        });
}
