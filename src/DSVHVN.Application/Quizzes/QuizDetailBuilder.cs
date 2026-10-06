using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Quizzes;

/// <summary>Dựng chi tiết bài kiểm tra cho giáo viên: thiết lập, các câu (kèm cảnh báo câu không dùng được), điều kiện phát hành, quyền thao tác.</summary>
public sealed class QuizDetailBuilder(IAppDbContext db, TimeProvider clock)
{
    /// <summary>Người gọi phải nạp sẵn <c>Lesson</c> kèm lớp và <c>Questions</c> kèm <c>Question</c>.</summary>
    public async Task<QuizDetailDto> BuildAsync(Quiz quiz, CancellationToken ct)
    {
        var lesson = quiz.Lesson!;
        var schoolClass = lesson.Class!;
        var level = SchoolClass.SchoolLevelOf(schoolClass.Grade);
        var now = clock.GetUtcNow().UtcDateTime;

        var heritageIds = quiz.Questions.Select(x => x.Question!.HeritageId).Distinct().ToList();
        var heritageNames = await db.Heritages.AsNoTracking().Where(h => heritageIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, h => h.Name, ct);

        var questions = quiz.Questions.OrderBy(x => x.SortOrder).ThenBy(x => x.QuestionId).Select(x =>
        {
            var q = x.Question!;
            var reason = QuizQuestionRules.UnusableReason(q, level);
            return new QuizQuestionDto(
                q.Id, x.SortOrder ?? 0, x.Point ?? QuizRules.DefaultPoint, q.QuestionType, QuestionTypes.NameOf(q.QuestionType), q.Content ?? string.Empty,
                q.ImageUrl, q.DifficultyLevel, q.HeritageId, heritageNames.GetValueOrDefault(q.HeritageId), q.LevelMin, q.LevelMax, reason is null, reason);
        }).ToList();

        var submitted = await db.Attempts.CountAsync(a => a.QuizId == quiz.Id && a.Status != AttemptStatus.IN_PROGRESS, ct);
        var students = await db.Students.CountAsync(s => s.ClassId == schoolClass.Id && s.DeletedAt == null && s.IsActive, ct);

        var check = quiz.Status == QuizStatus.DRAFT ? Evaluate(quiz, now) : new QuizReadinessDto(true, [], []);
        var state = QuizRules.OpenStateOf(quiz.Status, quiz.OpenAt, quiz.CloseAt, now);
        var draft = quiz.Status == QuizStatus.DRAFT;
        var published = quiz.Status == QuizStatus.PUBLISHED;
        var access = new QuizAccessDto(
            CanEditSettings: draft,
            CanEditCloseTime: draft || published,
            CanEditQuestions: draft,
            CanPublish: draft && check.IsReady,
            CanClose: published,
            CanDelete: draft,
            CanDuplicate: lesson.Status != ContentStatus.ARCHIVED);

        var notice = draft && check.IsReady
            ? Messages.QuizPublishNotice(schoolClass.Name ?? string.Empty, quiz.Name ?? string.Empty, lesson.AccessCode, quiz.OpenAt, quiz.CloseAt)
            : null;

        return new QuizDetailDto(
            quiz.Id, quiz.Name, quiz.Description, lesson.Id, lesson.Title, lesson.Status, lesson.AccessCode, schoolClass.Id, schoolClass.Name,
            schoolClass.Grade, level, SchoolLevels.NameOf(level), quiz.DifficultyLevel, quiz.TimeLimitMinutes, QuizRules.TimeLimitRuleOf(level),
            QuizRules.TimeLimitRuleText(level), quiz.OpenAt, quiz.CloseAt, quiz.ShuffleQuestions, quiz.ShowAnswersAfterSubmit,
            questions.Sum(x => x.Point), quiz.Status, QuizRules.StatusLabel(quiz.Status), state, QuizRules.OpenStateLabel(state), quiz.PublishedAt,
            questions, questions.Count, check, access, submitted, students, notice, quiz.CreatedAt, quiz.UpdatedAt);
    }

    /// <summary>
    /// Điều kiện phát hành (quy tắc Phát hành, sửa và đóng quiz). <c>Missing</c> liệt kê mọi thứ còn thiếu để giáo viên sửa một lượt.
    /// Chỉ có ý nghĩa với bài Nháp; bài đã phát hành hoặc đã đóng báo sẵn sàng (không còn phát hành).
    /// </summary>
    public QuizReadinessDto Evaluate(Quiz quiz, DateTime now)
    {
        var lesson = quiz.Lesson!;
        var level = SchoolClass.SchoolLevelOf(lesson.Class!.Grade);
        var missing = new List<string>();
        var warnings = new List<string>();

        if (lesson.Status != ContentStatus.PUBLISHED) missing.Add("bài học của bài kiểm tra chưa xuất bản");

        var count = quiz.Questions.Count;
        if (count < QuizRules.MinQuestions) missing.Add("ít nhất một câu hỏi");
        else if (count > QuizRules.MaxQuestions) missing.Add($"không quá {QuizRules.MaxQuestions} câu hỏi");

        var unusable = quiz.Questions.Select(x => QuizQuestionRules.UnusableReason(x.Question!, level)).Where(r => r is not null).ToList();
        if (unusable.Count > 0)
        {
            var distinct = unusable.Distinct().ToList();
            missing.Add($"{unusable.Count} câu hỏi không dùng được ({string.Join(" ", distinct)}). Hãy bỏ các câu này");
        }

        if (quiz.OpenAt is null) missing.Add("giờ mở");
        if (quiz.CloseAt is null) missing.Add("giờ đóng");
        if (quiz.OpenAt is { } open && quiz.CloseAt is { } close && open >= close) missing.Add("giờ mở phải trước giờ đóng");
        if (quiz.CloseAt is { } closeAt && closeAt <= now) missing.Add("giờ đóng phải sau thời điểm hiện tại");

        if (QuizRules.CheckTimeLimit(level, quiz.TimeLimitMinutes, forPublish: true) is { } timeProblem) missing.Add(timeProblem);

        if (quiz.DifficultyLevel is null) warnings.Add("chưa chọn độ khó chung của bài kiểm tra");
        if (count > 0 && quiz.Questions.Sum(x => x.Point ?? 0m) <= 0) warnings.Add("tổng điểm bằng 0");

        return new QuizReadinessDto(missing.Count == 0, missing, warnings);
    }
}

/// <summary>Đóng bài kiểm tra: dùng chung cho giáo viên bấm đóng và tác vụ nền tự đóng khi quá giờ đóng.</summary>
internal static class QuizLifecycle
{
    /// <summary>
    /// Chuyển bài sang đã đóng, tự nộp các lượt làm bài đang dở (trạng thái hết giờ, điểm tính từ các câu đã chấm của lượt) và ghi nhật ký.
    /// <paramref name="actor"/> null nghĩa là hệ thống tự đóng. Người gọi tự lưu. Trả số lượt đã tự nộp.
    /// </summary>
    public static async Task<int> CloseAsync(IAppDbContext db, IAuditLogger audit, Quiz quiz, DateTime now, Actor? actor, CancellationToken ct)
    {
        var attempts = await db.Attempts.Include(a => a.Answers)
            .Where(a => a.QuizId == quiz.Id && a.Status == AttemptStatus.IN_PROGRESS).ToListAsync(ct);
        var questionCount = await db.QuizQuestions.CountAsync(x => x.QuizId == quiz.Id, ct);
        foreach (var attempt in attempts)
        {
            attempt.Status = AttemptStatus.EXPIRED;
            attempt.SubmittedAt = now;
            var seconds = attempt.StartedAt is { } started ? (int)Math.Max(0, (now - started).TotalSeconds) : 0;
            attempt.DurationSeconds = quiz.TimeLimitMinutes is { } limit ? Math.Min(seconds, limit * 60) : seconds;
            attempt.Score = attempt.Answers.Sum(a => a.PointEarned ?? 0m);
            attempt.TotalPoint = quiz.TotalPoint;
            attempt.CorrectCount = attempt.Answers.Count(a => a.IsCorrect);
            attempt.TotalQuestions = questionCount;
        }

        var before = quiz.Status;
        quiz.Status = QuizStatus.CLOSED;
        audit.Record(AuditAction.QUIZ_CLOSED, AuditTargets.Quizzes, quiz.Id, actor,
            oldValue: new { status = before },
            newValue: new { status = QuizStatus.CLOSED, quizName = quiz.Name, lessonId = quiz.LessonId, expiredAttempts = attempts.Count, byActor = actor is null ? "SYSTEM" : "TEACHER" });
        return attempts.Count;
    }
}

/// <summary>
/// Tác vụ nền tự đóng bài kiểm tra đã phát hành quá giờ đóng (quy tắc Phát hành, sửa và đóng quiz): đổi sang đã đóng, tự nộp các lượt đang dở,
/// ghi nhật ký với vai trò hệ thống. Chạy lặp được: bài đã đóng không bị xử lý lần nữa.
/// </summary>
public sealed class QuizAutoCloser(IAppDbContext db, IAuditLogger audit, TimeProvider clock)
{
    private const int BatchSize = 100;

    public async Task<AutoCloseResultDto> CloseDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));

        var due = await db.Quizzes
            .Where(q => q.DeletedAt == null && q.Status == QuizStatus.PUBLISHED && q.CloseAt != null && q.CloseAt <= now)
            .OrderBy(q => q.CloseAt).Take(BatchSize).ToListAsync(ct);

        var expired = 0;
        foreach (var quiz in due) expired += await QuizLifecycle.CloseAsync(db, audit, quiz, now, actor: null, ct);
        if (due.Count > 0) await db.SaveChangesAsync(ct);
        return new AutoCloseResultDto(due.Count, expired);
    }
}

/// <summary>
/// Độ khó của bài học, tính khi truy vấn (không lưu): mức độ khó chiếm nhiều câu nhất trong các câu của các bài kiểm tra chưa xóa của bài
/// (bài kiểm tra ở mọi trạng thái), hai mức bằng nhau lấy mức cao hơn. Bài chưa có bài kiểm tra nào có câu thì không có trong kết quả.
/// </summary>
internal static class LessonDifficultyReader
{
    public static Task<Dictionary<long, DifficultyLevel?>> ReadAsync(IAppDbContext db, IQueryable<long> lessonIds, CancellationToken ct) =>
        ReadAsync(db.QuizQuestions.AsNoTracking().Where(x => lessonIds.Contains(x.Quiz!.LessonId)), ct);

    public static Task<Dictionary<long, DifficultyLevel?>> ReadAsync(IAppDbContext db, IReadOnlyCollection<long> lessonIds, CancellationToken ct)
    {
        var ids = lessonIds.ToList();
        return ReadAsync(db.QuizQuestions.AsNoTracking().Where(x => ids.Contains(x.Quiz!.LessonId)), ct);
    }

    private static async Task<Dictionary<long, DifficultyLevel?>> ReadAsync(IQueryable<QuizQuestion> items, CancellationToken ct)
    {
        var counts = await items
            .Where(x => x.Quiz!.DeletedAt == null && x.Question!.DifficultyLevel != null)
            .GroupBy(x => new { x.Quiz!.LessonId, Level = x.Question!.DifficultyLevel })
            .Select(g => new { g.Key.LessonId, g.Key.Level, Count = g.Count() })
            .ToListAsync(ct);

        return counts.GroupBy(c => c.LessonId).ToDictionary(
            g => g.Key,
            g => LessonDifficulty.Dominant(g.ToDictionary(c => c.Level!.Value, c => c.Count)));
    }
}
