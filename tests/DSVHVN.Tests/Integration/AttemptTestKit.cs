using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Questions;
using DSVHVN.Application.Quizzes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dựng dữ liệu cho test làm bài qua chính API: bài kiểm tra đã phát hành và đang mở với các câu cho trước, học sinh đã đăng nhập,
/// các lời gọi khu làm bài, và cách trả lời đúng hoặc sai một câu theo đúng những gì em nhìn thấy.
/// </summary>
public sealed class AttemptTestKit(ApiFactory factory)
{
    public const string Student = "/api/v1/student";

    public QuizTestKit Quizzes { get; } = new(factory);
    public ApiFactory Factory { get; } = factory;
    public HttpClient Http => Quizzes.Http;

    /// <summary>Bài kiểm tra đang mở kèm câu (với đáp án để trả lời đúng) và học sinh của lớp.</summary>
    public sealed record Quiz(
        QuizTestKit.Setup Setup,
        QuizDetailDto Detail,
        List<QuestionDetailDto> Questions,
        string StudentToken,
        string StudentCode)
    {
        public long Id => Detail.Id;
        public string TeacherToken => Setup.Token;
    }

    /// <summary>
    /// Bài kiểm tra đã phát hành và đang mở (mở từ 1 giờ trước, đóng sau 3 giờ). <paramref name="types"/> để trống thì lấy lần lượt các dạng dùng được ở cấp.
    /// </summary>
    public async Task<Quiz> OpenQuizAsync(
        int level = 2,
        IReadOnlyList<QuestionType>? types = null,
        bool showAnswers = true,
        bool shuffle = false,
        int? timeLimitMinutes = null,
        decimal point = 2m,
        QuizTestKit.Setup? setup = null,
        string studentName = "Nguyễn Văn An")
    {
        var s = setup ?? await Quizzes.SetupAsync(level);
        var kinds = types ?? QuestionTypes.ForLevel(s.Level);
        var quiz = await Quizzes.CreateAsync(s, "Kiểm tra " + Guid.NewGuid().ToString("N")[..6], new
        {
            difficultyLevel = DifficultyLevel.MEDIUM,
            timeLimitMinutes = s.Level == 1 ? null : timeLimitMinutes ?? (s.Level == 3 ? 30 : (int?)null),
            openAt = QuizTestKit.OpenSoon,
            closeAt = QuizTestKit.CloseLater,
            shuffleQuestions = shuffle,
            showAnswersAfterSubmit = showAnswers,
        });
        var questions = new List<QuestionDetailDto>();
        foreach (var kind in kinds) questions.Add(await Quizzes.QuestionAsync(s, kind));
        await Quizzes.AddQuestionsAsync(s.Token, quiz.Id, questions.Select(q => q.Id), point);
        var published = (await Quizzes.PublishAsync(s.Token, quiz.Id)).Quiz;
        await Quizzes.SetQuizTimesAsync(published.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(3));

        var token = await Quizzes.Questions.Lesson.StudentTokenAsync(s.Token, s.ClassId, studentName);
        var code = await Factory.WithDbAsync(db => db.Students.Where(x => x.ClassId == s.ClassId && x.FullName == studentName)
            .OrderByDescending(x => x.Id).Select(x => x.StudentCode!).FirstAsync());
        return new Quiz(s, published, questions, token, code);
    }

    /// <summary>Thêm một học sinh nữa vào lớp của bài và trả token.</summary>
    public Task<string> AnotherStudentAsync(Quiz quiz, string name = "Trần Thị Bình") =>
        Quizzes.Questions.Lesson.StudentTokenAsync(quiz.TeacherToken, quiz.Setup.ClassId, name);

    // ---- Lời gọi API ----

    public Task<(HttpResponseMessage Response, Envelope<StudentQuizIntroDto> Body)> IntroRawAsync(string token, long quizId) =>
        Http.GetAsync<StudentQuizIntroDto>($"{Student}/quizzes/{quizId}", token);

    public async Task<StudentQuizIntroDto> IntroAsync(string token, long quizId) =>
        await DiscoveryTestKit.Expect(IntroRawAsync(token, quizId));

    public Task<(HttpResponseMessage Response, Envelope<StudentAttemptDto> Body)> StartRawAsync(string token, long quizId) =>
        Http.PostAsync<StudentAttemptDto>($"{Student}/quizzes/{quizId}/attempts", null, token);

    public async Task<StudentAttemptDto> StartAsync(string token, long quizId)
    {
        var (res, body) = await StartRawAsync(token, quizId);
        Assert.True(res.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"Bắt đầu: {(int)res.StatusCode} {body.ErrorCode} {body.Message}");
        return body.Data!;
    }

    public Task<(HttpResponseMessage Response, Envelope<StudentAttemptDto> Body)> GetRawAsync(string token, long attemptId) =>
        Http.GetAsync<StudentAttemptDto>($"{Student}/attempts/{attemptId}", token);

    public async Task<StudentAttemptDto> GetAsync(string token, long attemptId) =>
        await DiscoveryTestKit.Expect(GetRawAsync(token, attemptId));

    public Task<(HttpResponseMessage Response, Envelope<SavedAnswerDto> Body)> SaveRawAsync(string token, long attemptId, long questionId, QuestionSubmission? answer) =>
        Http.PutAsync<SavedAnswerDto>($"{Student}/attempts/{attemptId}/answers/{questionId}", answer ?? new QuestionSubmission(), token);

    public async Task<SavedAnswerDto> SaveAsync(string token, long attemptId, long questionId, QuestionSubmission? answer) =>
        await DiscoveryTestKit.Expect(SaveRawAsync(token, attemptId, questionId, answer));

    public Task<(HttpResponseMessage Response, Envelope<AnswerCheckDto> Body)> CheckRawAsync(string token, long attemptId, long questionId, QuestionSubmission? answer) =>
        Http.PostAsync<AnswerCheckDto>($"{Student}/attempts/{attemptId}/answers/{questionId}/check", answer ?? new QuestionSubmission(), token);

    public async Task<AnswerCheckDto> CheckAsync(string token, long attemptId, long questionId, QuestionSubmission? answer) =>
        await DiscoveryTestKit.Expect(CheckRawAsync(token, attemptId, questionId, answer));

    public async Task<StudentAlbumDto> AlbumAsync(string token) => await DiscoveryTestKit.Expect(Http.GetAsync<StudentAlbumDto>($"{Student}/album", token));

    public Task<(HttpResponseMessage Response, Envelope<StudentAttemptResultDto> Body)> SubmitRawAsync(string token, long attemptId) =>
        Http.PostAsync<StudentAttemptResultDto>($"{Student}/attempts/{attemptId}/submit", null, token);

    public async Task<StudentAttemptResultDto> SubmitAsync(string token, long attemptId) =>
        await DiscoveryTestKit.Expect(SubmitRawAsync(token, attemptId));

    public Task<(HttpResponseMessage Response, Envelope<StudentAttemptResultDto> Body)> ResultRawAsync(string token, long attemptId) =>
        Http.GetAsync<StudentAttemptResultDto>($"{Student}/attempts/{attemptId}/result", token);

    public async Task<StudentAttemptResultDto> ResultAsync(string token, long attemptId) =>
        await DiscoveryTestKit.Expect(ResultRawAsync(token, attemptId));

    public async Task<StudentHomeDto> HomeAsync(string token) => await DiscoveryTestKit.Expect(Http.GetAsync<StudentHomeDto>($"{Student}/home", token));

    public async Task<StudentProgressDto> ProgressAsync(string token) =>
        await DiscoveryTestKit.Expect(Http.GetAsync<StudentProgressDto>($"{Student}/progress", token));

    public async Task<PagedResult<StudentAttemptRowDto>> HistoryAsync(string token, string query = "") =>
        await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<StudentAttemptRowDto>>($"{Student}/attempts{query}", token));

    // ---- Trả lời theo những gì em thấy ----

    public static QuestionDetailDto DetailOf(Quiz quiz, long questionId) => quiz.Questions.Single(q => q.Id == questionId);

    /// <summary>Cách trả lời đúng hết một câu, tìm theo chữ trên màn hình (thứ tự thẻ, cột, mục là thứ tự đã xáo).</summary>
    public static QuestionSubmission Correct(StudentAttemptQuestionDto shown, QuestionDetailDto detail)
    {
        switch (shown.QuestionType)
        {
            case QuestionType.SINGLE_CHOICE:
            case QuestionType.TRUE_FALSE:
            case QuestionType.IMAGE_CHOICE:
            case QuestionType.SOURCE_READING:
            {
                var right = detail.Options.Single(o => o.IsCorrect);
                return new QuestionSubmission(SelectedIndex: shown.Options.ToList().FindIndex(o => o.Content == right.Content));
            }
            case QuestionType.MATCHING:
                return new QuestionSubmission(Matches: shown.LeftItems.Select(left =>
                {
                    var pair = detail.Pairs.Single(p => p.LeftImageUrl is not null ? p.LeftImageUrl == left.ImageUrl : p.Left == left.Content);
                    return (int?)shown.RightItems.ToList().IndexOf(pair.Right!);
                }).ToList());
            case QuestionType.ORDERING:
                return new QuestionSubmission(Order: detail.Steps.Select(step => shown.Items.ToList().IndexOf(step)).ToList());
            case QuestionType.FILL_BLANK:
                return new QuestionSubmission(Blanks: detail.Blanks.Select(b => (string?)b.Accepted![0]).ToList());
            case QuestionType.MULTI_TRUE_FALSE:
                return new QuestionSubmission(Statements: shown.Statements.Select(text => (bool?)detail.Statements.Single(s => s.Text == text).IsTrue).ToList());
            default:
                throw new ArgumentOutOfRangeException(nameof(shown));
        }
    }

    /// <summary>Cách trả lời sai hết một câu (mọi phần đều sai).</summary>
    public static QuestionSubmission Wrong(StudentAttemptQuestionDto shown, QuestionDetailDto detail)
    {
        switch (shown.QuestionType)
        {
            case QuestionType.SINGLE_CHOICE:
            case QuestionType.TRUE_FALSE:
            case QuestionType.IMAGE_CHOICE:
            case QuestionType.SOURCE_READING:
            {
                var right = detail.Options.Single(o => o.IsCorrect);
                return new QuestionSubmission(SelectedIndex: shown.Options.ToList().FindIndex(o => o.Content != right.Content));
            }
            case QuestionType.MATCHING:
            {
                // Nối mỗi hình với tên của hình kế tiếp (xoay vòng) nên không cặp nào đúng.
                var correct = Correct(shown, detail).Matches!;
                var n = correct.Count;
                return new QuestionSubmission(Matches: correct.Select(c => (int?)((c!.Value + 1) % n)).ToList());
            }
            case QuestionType.ORDERING:
            {
                var correct = Correct(shown, detail).Order!;
                var reversed = correct.AsEnumerable().Reverse().ToList();
                return new QuestionSubmission(Order: reversed);
            }
            case QuestionType.FILL_BLANK:
                return new QuestionSubmission(Blanks: detail.Blanks.Select(_ => (string?)"sai hoàn toàn").ToList());
            case QuestionType.MULTI_TRUE_FALSE:
                return new QuestionSubmission(Statements: shown.Statements.Select(text => (bool?)!detail.Statements.Single(s => s.Text == text).IsTrue).ToList());
            default:
                throw new ArgumentOutOfRangeException(nameof(shown));
        }
    }

    /// <summary>Trả lời mọi câu bằng <paramref name="answer"/> rồi nộp; trả kết quả.</summary>
    public async Task<StudentAttemptResultDto> AnswerAllAndSubmitAsync(
        Quiz quiz, Func<StudentAttemptQuestionDto, QuestionDetailDto, QuestionSubmission?> answer, string? token = null)
    {
        token ??= quiz.StudentToken;
        var attempt = await StartAsync(token, quiz.Id);
        foreach (var q in attempt.Questions)
        {
            var response = answer(q, DetailOf(quiz, q.QuestionId));
            if (response is not null) await SaveAsync(token, attempt.Id, q.QuestionId, response);
        }
        return await SubmitAsync(token, attempt.Id);
    }

    // ---- Chỉnh thẳng CSDL ----

    public Task SetAttemptStartAsync(long attemptId, DateTime startedAt) =>
        Factory.WithDbAsync(async db =>
        {
            var a = await db.Attempts.SingleAsync(x => x.Id == attemptId);
            a.StartedAt = startedAt;
            return await db.SaveChangesAsync();
        });

    public Task<Attempt> AttemptRowAsync(long attemptId) =>
        Factory.WithDbAsync(db => db.Attempts.AsNoTracking().SingleAsync(x => x.Id == attemptId));

    public Task<List<AttemptAnswer>> AnswerRowsAsync(long attemptId) =>
        Factory.WithDbAsync(db => db.AttemptAnswers.AsNoTracking().Include(a => a.Parts).Where(a => a.AttemptId == attemptId).OrderBy(a => a.QuestionId).ToListAsync());
}
