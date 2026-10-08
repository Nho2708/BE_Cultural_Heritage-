using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Recommendations;
using DSVHVN.Application.Statistics;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dựng dữ liệu cho test thống kê và gợi ý: một lớp có nhiều bài học, mỗi bài một di sản riêng (tỉnh, loại tùy chọn) và một bài kiểm tra đang mở; học sinh làm bài qua
/// chính API (đúng hoặc sai từng câu) hoặc được ghi lượt thẳng vào CSDL khi cần số lượng; và các lời gọi API gợi ý, thống kê.
/// </summary>
public sealed class StatsTestKit(ApiFactory factory)
{
    public const string Student = "/api/v1/student";
    public const string Teacher = "/api/v1/teacher";

    public AttemptTestKit Attempts { get; } = new(factory);
    public QuizTestKit Quizzes => Attempts.Quizzes;
    public ApiFactory Factory { get; } = factory;
    public HttpClient Http => Attempts.Http;

    // ---- Dựng lớp ----

    /// <summary>
    /// Thêm một di sản đã xuất bản (tỉnh, loại, cấp xếp hạng tùy chọn) và một bài học đã xuất bản về nó cho cùng lớp; trả bộ dựng dùng di sản và bài học mới
    /// (cùng giáo viên, cùng lớp) để tạo bài kiểm tra tiếp.
    /// </summary>
    public async Task<QuizTestKit.Setup> LessonOnNewHeritageAsync(
        QuizTestKit.Setup s, string name, string provinceCode = "01", HeritageType type = HeritageType.TANGIBLE, string? title = null,
        RecognitionLevel level = RecognitionLevel.NATIONAL, bool sameEra = true)
    {
        var (heritage, images) = await Quizzes.Questions.HeritageAsync(name, provinceCode: provinceCode);
        await TuneHeritageAsync(heritage.Id, type, level, sameEra);
        var lesson = await Quizzes.Questions.Lesson.PublishedLessonAsync(s.Token, s.ClassId, [heritage.Id], title ?? "Bài về " + name);
        return s with { Heritage = heritage, Images = images, LessonId = lesson.Id, AccessCode = lesson.AccessCode };
    }

    /// <summary>Một di sản đã xuất bản không có bài học nào (chỉ để làm ứng viên "di sản liên quan").</summary>
    public async Task<long> LooseHeritageAsync(
        string name, string provinceCode = "01", HeritageType type = HeritageType.TANGIBLE, RecognitionLevel level = RecognitionLevel.NATIONAL,
        bool sameEra = true)
    {
        var heritage = await Quizzes.Questions.Lesson.HeritageAsync(name, 1, provinceCode);   // một sự kiện: có giai đoạn 1010-1225 như các di sản có bài
        await TuneHeritageAsync(heritage.Id, type, level, sameEra);
        return heritage.Id;
    }

    /// <summary>Đặt loại và cấp xếp hạng; mô tả ngắn chỉ là tên (mô tả mẫu của bộ dựng có chữ chung nên làm mọi di sản trông giống nhau).</summary>
    private Task TuneHeritageAsync(long heritageId, HeritageType type, RecognitionLevel level, bool sameEra = true) =>
        Factory.WithDbAsync(async db =>
        {
            var row = await db.Heritages.SingleAsync(h => h.Id == heritageId);
            (row.HeritageType, row.RecognitionLevel, row.ShortDescription) = (type, level, row.Name);
            // Bộ dựng cho mọi di sản cùng một giai đoạn 1010-1225; di sản "khác thời" được dời sang 1900-1950.
            if (!sameEra)
                foreach (var phase in await db.Timelines.Where(t => t.HeritageId == heritageId).ToListAsync())
                    (phase.StartYear, phase.EndYear) = (1900, 1950);
            return await db.SaveChangesAsync();
        });

    /// <summary>Bài kiểm tra đang mở gồm một câu mỗi dạng của cấp, về di sản và bài học của <paramref name="s"/>, kèm một học sinh mới tên <paramref name="studentName"/>.</summary>
    public Task<AttemptTestKit.Quiz> QuizAsync(QuizTestKit.Setup s, string studentName, IReadOnlyList<QuestionType>? types = null, bool showAnswers = true) =>
        Attempts.OpenQuizAsync(s.Level, types, showAnswers, setup: s, studentName: studentName);

    /// <summary>
    /// Bài kiểm tra đang mở gồm các câu <paramref name="picks"/> (dạng câu và bộ dựng cho di sản của câu) và một học sinh mới; dùng khi cần một bài có câu của nhiều di sản.
    /// </summary>
    public async Task<AttemptTestKit.Quiz> MixedQuizAsync(QuizTestKit.Setup s, string studentName, params (QuizTestKit.Setup From, QuestionType Type)[] picks)
    {
        var quiz = await Quizzes.CreateAsync(s, "Hỗn hợp " + Guid.NewGuid().ToString("N")[..6], new
        {
            difficultyLevel = DifficultyLevel.MEDIUM,
            timeLimitMinutes = s.Level == 1 ? null : s.Level == 3 ? 30 : (int?)null,
            openAt = QuizTestKit.OpenSoon,
            closeAt = QuizTestKit.CloseLater,
            shuffleQuestions = false,
            showAnswersAfterSubmit = true,
        });
        var questions = new List<DSVHVN.Application.Questions.QuestionDetailDto>();
        foreach (var (from, type) in picks) questions.Add(await Quizzes.QuestionAsync(from, type));
        await Quizzes.AddQuestionsAsync(s.Token, quiz.Id, questions.Select(q => q.Id), 2m);
        var published = (await Quizzes.PublishAsync(s.Token, quiz.Id)).Quiz;
        await Quizzes.SetQuizTimesAsync(published.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(3));
        var token = await StudentTokenAsync(s, studentName);
        var code = await Factory.WithDbAsync(db => db.Students.Where(x => x.ClassId == s.ClassId && x.FullName == studentName)
            .OrderByDescending(x => x.Id).Select(x => x.StudentCode!).FirstAsync());
        return new AttemptTestKit.Quiz(s, published, questions, token, code);
    }

    public Task<string> StudentTokenAsync(QuizTestKit.Setup s, string name) =>
        Quizzes.Questions.Lesson.StudentTokenAsync(s.Token, s.ClassId, name);

    public Task<long> StudentIdAsync(long classId, string name) =>
        Factory.WithDbAsync(db => db.Students.Where(x => x.ClassId == classId && x.FullName == name).OrderByDescending(x => x.Id).Select(x => x.Id).FirstAsync());

    // ---- Làm bài ----

    public Task<StudentAttemptResultDto> AllWrongAsync(AttemptTestKit.Quiz quiz, string? token = null) =>
        Attempts.AnswerAllAndSubmitAsync(quiz, AttemptTestKit.Wrong, token);

    public Task<StudentAttemptResultDto> AllCorrectAsync(AttemptTestKit.Quiz quiz, string? token = null) =>
        Attempts.AnswerAllAndSubmitAsync(quiz, AttemptTestKit.Correct, token);

    /// <summary>
    /// Ghi thẳng một lượt đã chốt (không qua API) của <paramref name="studentId"/> ở <paramref name="quizId"/>: <paramref name="correct"/> quyết định từng câu đúng hay sai
    /// (theo thứ tự trong bài, từ 0). Dùng khi cần nhiều câu trả lời mà không cần làm bài từng người.
    /// </summary>
    public Task<long> SeedAttemptAsync(
        long quizId, long studentId, Func<int, bool> correct, AttemptStatus status = AttemptStatus.SUBMITTED, DateTime? submittedAt = null) =>
        Factory.WithDbAsync(async db =>
        {
            var items = await db.QuizQuestions.Where(x => x.QuizId == quizId).OrderBy(x => x.SortOrder).ThenBy(x => x.QuestionId).ToListAsync();
            var total = await db.Quizzes.Where(q => q.Id == quizId).Select(q => q.TotalPoint).SingleAsync() ?? items.Sum(x => x.Point ?? 0m);
            var submitted = submittedAt ?? DateTime.UtcNow.AddMinutes(-5);
            var attempt = new Attempt
            {
                QuizId = quizId, StudentId = studentId, StartedAt = submitted.AddMinutes(-10), SubmittedAt = submitted, DurationSeconds = 600, Status = status,
                TotalPoint = total, TotalQuestions = items.Count,
            };
            for (var i = 0; i < items.Count; i++)
            {
                var ok = correct(i);
                attempt.Answers.Add(new AttemptAnswer
                {
                    QuestionId = items[i].QuestionId, IsCorrect = ok, PartsTotal = 1, PartsCorrect = (short)(ok ? 1 : 0),
                    PointEarned = ok ? items[i].Point ?? 0m : 0m, AnsweredAt = submitted,
                });
            }
            attempt.CorrectCount = attempt.Answers.Count(a => a.IsCorrect);
            attempt.Score = attempt.Answers.Sum(a => a.PointEarned ?? 0m);
            db.Attempts.Add(attempt);
            await db.SaveChangesAsync();
            return attempt.Id;
        });

    // ---- Chỉnh thẳng CSDL ----

    public Task SetQuizDifficultyAsync(long quizId, DifficultyLevel? difficulty) =>
        Factory.WithDbAsync(async db =>
        {
            (await db.Quizzes.SingleAsync(q => q.Id == quizId)).DifficultyLevel = difficulty;
            return await db.SaveChangesAsync();
        });

    public Task SetStudentActiveAsync(long studentId, bool active) =>
        Factory.WithDbAsync(async db =>
        {
            (await db.Students.SingleAsync(s => s.Id == studentId)).IsActive = active;
            return await db.SaveChangesAsync();
        });

    public Task SetLastLoginAsync(long studentId, DateTime? lastLoginAt) =>
        Factory.WithDbAsync(async db =>
        {
            var userId = await db.Students.Where(s => s.Id == studentId).Select(s => s.UserId).SingleAsync();
            (await db.Users.SingleAsync(u => u.Id == userId)).LastLoginAt = lastLoginAt;
            return await db.SaveChangesAsync();
        });

    public Task SoftDeleteQuizAsync(long quizId) =>
        Factory.WithDbAsync(async db =>
        {
            (await db.Quizzes.SingleAsync(q => q.Id == quizId)).DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });

    public Task SetLessonStatusAsync(long lessonId, ContentStatus status) =>
        Factory.WithDbAsync(async db =>
        {
            (await db.Lessons.SingleAsync(l => l.Id == lessonId)).Status = status;
            return await db.SaveChangesAsync();
        });

    public Task RenameHeritageAsync(long heritageId, string name) =>
        Factory.WithDbAsync(async db =>
        {
            var row = await db.Heritages.SingleAsync(h => h.Id == heritageId);
            (row.Name, row.ShortDescription) = (name, name);
            return await db.SaveChangesAsync();
        });

    /// <summary>Mã tỉnh mới (chưa heritage nào khác dùng) để một nhóm di sản của một test không bị di sản của test khác trong cùng CSDL chen vào bảng xếp hạng.</summary>
    public static string NewProvinceCode() => "T" + Guid.NewGuid().ToString("N")[..8];

    public async Task SetHeritageProvinceAsync(long heritageId, string provinceCode)
    {
        var provinceId = await Quizzes.Questions.Lesson.Discovery.ProvinceIdAsync(provinceCode);
        await Factory.WithDbAsync(async db =>
        {
            (await db.Heritages.SingleAsync(h => h.Id == heritageId)).ProvinceId = provinceId;
            return await db.SaveChangesAsync();
        });
    }

    // ---- Lời gọi API của học sinh ----

    public Task<(HttpResponseMessage Response, Envelope<StudentRecommendationsDto> Body)> RecommendationsRawAsync(string? token) =>
        Http.GetAsync<StudentRecommendationsDto>($"{Student}/recommendations", token);

    public async Task<StudentRecommendationsDto> RecommendationsAsync(string token) =>
        await DiscoveryTestKit.Expect(RecommendationsRawAsync(token));

    public Task<(HttpResponseMessage Response, Envelope<StudentRecommendationsDto> Body)> AttemptRecommendationsRawAsync(string token, long attemptId) =>
        Http.GetAsync<StudentRecommendationsDto>($"{Student}/attempts/{attemptId}/recommendations", token);

    public async Task<StudentRecommendationsDto> AttemptRecommendationsAsync(string token, long attemptId) =>
        await DiscoveryTestKit.Expect(AttemptRecommendationsRawAsync(token, attemptId));

    // ---- Lời gọi API của giáo viên ----

    public Task<(HttpResponseMessage Response, Envelope<T> Body)> TeacherGetAsync<T>(string token, string path) => Http.GetAsync<T>(Teacher + path, token);

    public async Task<T> TeacherAsync<T>(string token, string path) => await DiscoveryTestKit.Expect(TeacherGetAsync<T>(token, path));

    public Task<QuizResultsOverviewDto> OverviewAsync(string token, long quizId) => TeacherAsync<QuizResultsOverviewDto>(token, $"/quizzes/{quizId}/results");

    public Task<QuizQuestionStatsDto> QuestionStatsAsync(string token, long quizId) => TeacherAsync<QuizQuestionStatsDto>(token, $"/quizzes/{quizId}/results/questions");

    public Task<QuizStudentsDto> QuizStudentsAsync(string token, long quizId) => TeacherAsync<QuizStudentsDto>(token, $"/quizzes/{quizId}/results/students");

    public Task<QuizTopicStatsDto> QuizTopicsAsync(string token, long quizId) => TeacherAsync<QuizTopicStatsDto>(token, $"/quizzes/{quizId}/results/topics");

    public Task<ClassRecommendationsDto> QuizRecommendationsAsync(string token, long quizId, string query = "") =>
        TeacherAsync<ClassRecommendationsDto>(token, $"/quizzes/{quizId}/results/recommendations{query}");

    public Task<TeacherAttemptDetailDto> AttemptDetailAsync(string token, long attemptId) => TeacherAsync<TeacherAttemptDetailDto>(token, $"/attempts/{attemptId}");

    public Task<ClassProgressDto> ProgressAsync(string token, long classId) => TeacherAsync<ClassProgressDto>(token, $"/classes/{classId}/progress");

    public Task<ClassMasteryDto> MasteryAsync(string token, long classId, string query = "") =>
        TeacherAsync<ClassMasteryDto>(token, $"/classes/{classId}/mastery{query}");

    public Task<ClassRecommendationsDto> ClassRecommendationsAsync(string token, long classId, string query = "") =>
        TeacherAsync<ClassRecommendationsDto>(token, $"/classes/{classId}/recommendations{query}");

    public Task<StudentLearningProfileDto> ProfileAsync(string token, long classId, long studentId) =>
        TeacherAsync<StudentLearningProfileDto>(token, $"/classes/{classId}/students/{studentId}/profile");

    /// <summary>Gọi một đường dẫn của giáo viên và trả mã HTTP (kiểm quyền).</summary>
    public async Task<HttpStatusCode> StatusOfAsync(string path, string? token) => (await Http.GetAsync<object>(path, token)).Response.StatusCode;
}
