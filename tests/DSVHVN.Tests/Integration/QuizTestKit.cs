using System.Net;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Application.Quizzes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dựng dữ liệu cho test bài kiểm tra qua chính API: trường có giáo viên, lớp theo cấp, di sản đủ ảnh, bài học đã xuất bản (có mã), câu hỏi mẫu hợp cấp,
/// bài kiểm tra đã đủ điều kiện phát hành, và các lời gọi API của khu Giáo viên.
/// </summary>
public sealed class QuizTestKit(ApiFactory factory)
{
    public const string Quizzes = "/api/v1/teacher/quizzes";

    public QuestionTestKit Questions { get; } = new(factory);
    public ApiFactory Factory { get; } = factory;
    public HttpClient Http => Questions.Http;

    public sealed record Setup(
        ClassroomClient.School School,
        long ClassId,
        int Level,
        HeritageDetailDto Heritage,
        List<string> Images,
        long LessonId,
        string? AccessCode)
    {
        public string Token => School.TeacherToken;
    }

    /// <summary>Trường có giáo viên, lớp theo cấp, di sản và một bài học; bài đã xuất bản (có mã bài học) hoặc còn Nháp.</summary>
    public async Task<Setup> SetupAsync(int level = 2, bool publishLesson = true)
    {
        var (school, schoolClass) = await Questions.Lesson.SchoolWithClassAsync(level);
        var (heritage, images) = await Questions.HeritageAsync();
        string? code = null;
        long lessonId;
        if (publishLesson)
        {
            var lesson = await Questions.Lesson.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id]);
            (lessonId, code) = (lesson.Id, lesson.AccessCode);
        }
        else
        {
            lessonId = (await Questions.Lesson.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id])).Id;
        }
        return new Setup(school, schoolClass.Id, level, heritage, images, lessonId, code);
    }

    /// <summary>Thêm một bài học cho cùng giáo viên và lớp (đã xuất bản).</summary>
    public async Task<LessonDetailDto> AnotherLessonAsync(Setup s, string title = "Bài học thứ hai") =>
        await Questions.Lesson.PublishedLessonAsync(s.Token, s.ClassId, [s.Heritage.Id], title);

    // ---- Câu hỏi ----

    public Dictionary<QuestionType, SaveQuestionRequest> Samples(Setup s) => QuestionTestKit.Samples(s.Heritage.Id, s.Images);

    /// <summary>Tạo câu đã duyệt của dạng cho trước (mặc định dạng đầu tiên dùng được ở cấp của lớp).</summary>
    public async Task<QuestionDetailDto> QuestionAsync(Setup s, QuestionType? type = null, DifficultyLevel? difficulty = null, string? token = null)
    {
        var chosen = type ?? QuestionTypes.ForLevel(s.Level)[0];
        var request = Samples(s)[chosen];
        if (difficulty is not null) request = request with { DifficultyLevel = difficulty };
        return await Questions.CreateAsync(token ?? s.Token, request);
    }

    /// <summary>Các câu khác nhau, lần lượt theo các dạng dùng được ở cấp của lớp.</summary>
    public async Task<List<QuestionDetailDto>> QuestionsAsync(Setup s, int count, DifficultyLevel? difficulty = null)
    {
        var types = QuestionTypes.ForLevel(s.Level);
        var list = new List<QuestionDetailDto>();
        for (var i = 0; i < count; i++) list.Add(await QuestionAsync(s, types[i % types.Count], difficulty));
        return list;
    }

    // ---- Lời gọi API ----

    public static DateTime OpenSoon => DateTime.UtcNow.AddHours(1);
    public static DateTime CloseLater => DateTime.UtcNow.AddHours(3);

    public async Task<QuizDetailDto> CreateAsync(Setup s, string name = "Kiểm tra 15 phút", object? settings = null, long? lessonId = null)
    {
        var body = settings ?? new { };
        var merged = new Dictionary<string, object?> { ["lessonId"] = lessonId ?? s.LessonId, ["name"] = name };
        foreach (var p in body.GetType().GetProperties()) merged[p.Name] = p.GetValue(body);
        return await DiscoveryTestKit.Expect(Http.PostAsync<QuizDetailDto>(Quizzes, merged, s.Token), HttpStatusCode.Created);
    }

    public async Task<QuizDetailDto> GetAsync(string token, long id) =>
        await DiscoveryTestKit.Expect(Http.GetAsync<QuizDetailDto>($"{Quizzes}/{id}", token));

    public async Task<QuizDetailDto> AddQuestionsAsync(string token, long id, IEnumerable<long> questionIds, decimal? point = null) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<QuizDetailDto>($"{Quizzes}/{id}/questions", new { questionIds, point }, token));

    public async Task<PublishQuizResultDto> PublishAsync(string token, long id) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<PublishQuizResultDto>($"{Quizzes}/{id}/publish", null, token));

    public async Task<CloseQuizResultDto> CloseAsync(string token, long id) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<CloseQuizResultDto>($"{Quizzes}/{id}/close", null, token));

    /// <summary>Bài kiểm tra Nháp đủ điều kiện phát hành: <paramref name="questions"/> câu hợp cấp, giờ mở và đóng ở tương lai, thời gian làm theo cấp.</summary>
    public async Task<QuizDetailDto> ReadyQuizAsync(Setup s, string name = "Kiểm tra 15 phút", int questions = 3, DifficultyLevel? difficulty = null)
    {
        var quiz = await CreateAsync(s, name, new
        {
            difficultyLevel = DifficultyLevel.MEDIUM,
            timeLimitMinutes = s.Level == 1 ? (int?)null : 30,
            openAt = OpenSoon,
            closeAt = CloseLater,
        });
        var created = await QuestionsAsync(s, questions, difficulty);
        return await AddQuestionsAsync(s.Token, quiz.Id, created.Select(q => q.Id), 2m);
    }

    public async Task<QuizDetailDto> PublishedQuizAsync(Setup s, string name = "Kiểm tra 15 phút", int questions = 3, DifficultyLevel? difficulty = null)
    {
        var ready = await ReadyQuizAsync(s, name, questions, difficulty);
        return (await PublishAsync(s.Token, ready.Id)).Quiz;
    }

    // ---- Chỉnh thẳng CSDL (dùng để ép tình huống mà API không tạo được) ----

    public Task SetQuestionReviewAsync(long questionId, ReviewStatus status, long? reviewedBy = null) =>
        Factory.WithDbAsync(async db =>
        {
            var q = await db.Questions.SingleAsync(x => x.Id == questionId);
            q.ReviewStatus = status;
            if (reviewedBy is not null) q.ReviewedBy = reviewedBy;
            return await db.SaveChangesAsync();
        });

    public Task SetQuizTimesAsync(long quizId, DateTime? openAt, DateTime? closeAt) =>
        Factory.WithDbAsync(async db =>
        {
            var q = await db.Quizzes.SingleAsync(x => x.Id == quizId);
            (q.OpenAt, q.CloseAt) = (openAt, closeAt);
            return await db.SaveChangesAsync();
        });

    public Task SetClassGradeAsync(long classId, byte grade) =>
        Factory.WithDbAsync(async db =>
        {
            var c = await db.Classes.SingleAsync(x => x.Id == classId);
            c.Grade = grade;
            return await db.SaveChangesAsync();
        });

    /// <summary>Thêm một lượt làm bài đang dở của một học sinh mới vào bài kiểm tra (luồng làm bài chưa có API ở nhánh này).</summary>
    public async Task<long> AddAttemptAsync(Setup s, long quizId, AttemptStatus status = AttemptStatus.IN_PROGRESS, string fullName = "Trần Thị Bình")
    {
        await Questions.Lesson.StudentTokenAsync(s.Token, s.ClassId, fullName);
        return await Factory.WithDbAsync(async db =>
        {
            var studentId = await db.Students.Where(x => x.ClassId == s.ClassId && x.FullName == fullName).OrderByDescending(x => x.Id).Select(x => x.Id).FirstAsync();
            var attempt = new Attempt { QuizId = quizId, StudentId = studentId, StartedAt = DateTime.UtcNow.AddMinutes(-5), Status = status };
            if (status != AttemptStatus.IN_PROGRESS) attempt.SubmittedAt = DateTime.UtcNow;
            db.Attempts.Add(attempt);
            await db.SaveChangesAsync();
            return attempt.Id;
        });
    }

    public Task ExpirePlanAsync(long orgId) =>
        Factory.WithDbAsync(async db =>
        {
            foreach (var sub in await db.Subscriptions.Where(x => x.OrganizationId == orgId).ToListAsync())
                sub.Status = SubscriptionStatus.EXPIRED;
            return await db.SaveChangesAsync();
        });
}
