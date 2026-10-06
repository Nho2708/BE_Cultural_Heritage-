using DSVHVN.Application.Common;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Attempts;

/// <summary>Em đang đăng nhập: lớp, cấp học hiện tại (suy từ khối lớp mới nhất), giáo viên.</summary>
internal sealed record StudentContext(
    long Id,
    long ClassId,
    byte Grade,
    int Level,
    string? FullName,
    string? StudentCode,
    string? ClassName,
    string? TeacherName)
{
    public string LevelName => SchoolLevels.NameOf(Level);

    /// <summary>Học sinh đang hoạt động của lớp chưa xóa, từ tài khoản của người gọi; không phải học sinh hoặc không còn hoạt động thì từ chối.</summary>
    public static async Task<StudentContext> LoadAsync(IAppDbContext db, Actor actor, CancellationToken ct)
    {
        if (actor.Role != RoleCode.STUDENT) throw AppException.Forbidden(Messages.Forbidden);
        var student = await db.Students.AsNoTracking()
            .Where(s => s.UserId == actor.UserId && s.DeletedAt == null && s.IsActive && s.Class!.DeletedAt == null)
            .Select(s => new { s.Id, s.ClassId, s.Class!.Grade, s.FullName, s.StudentCode, ClassName = s.Class.Name, TeacherName = s.Class.Teacher!.FullName })
            .SingleOrDefaultAsync(ct)
            ?? throw AppException.Unauthorized(Messages.Forbidden);
        return new StudentContext(student.Id, student.ClassId, student.Grade, SchoolClass.SchoolLevelOf(student.Grade),
            student.FullName, student.StudentCode, student.ClassName, student.TeacherName);
    }
}

/// <summary>
/// Chốt một lượt làm bài: chấm lại từng câu từ các dòng đã lưu (bỏ trống thì ghi thêm một dòng 0 điểm kèm đủ các phần), cộng điểm, đếm câu đúng,
/// ghi thời gian làm và trạng thái. Dùng chung cho em bấm nộp, hết giờ tự nộp, và đóng bài kiểm tra (giáo viên hoặc tác vụ nền).
/// Người gọi tự lưu và tự lo khóa lượt.
/// </summary>
internal static class AttemptFinisher
{
    /// <summary>Hạn nộp của một lượt theo cấp hiện tại của lớp: cấp 1 không có thời gian làm (hạn nộp là giờ đóng).</summary>
    public static DateTime? DeadlineOf(Attempt attempt, Quiz quiz, int level) =>
        attempt.StartedAt is { } started
            ? AttemptRules.DeadlineOf(started, AttemptRules.EffectiveTimeLimit(level, quiz.TimeLimitMinutes), quiz.CloseAt)
            : quiz.CloseAt;

    /// <summary>
    /// Nạp phần cần có để chốt: các câu của bài kiểm tra (kèm câu và đáp án) và các dòng trả lời (kèm các phần) của lượt.
    /// </summary>
    public static async Task<List<QuizQuestion>> LoadQuizQuestionsAsync(IAppDbContext db, long quizId, CancellationToken ct) =>
        await db.QuizQuestions
            .Include(x => x.Question).ThenInclude(q => q!.Answers)
            .Where(x => x.QuizId == quizId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.QuestionId)
            .ToListAsync(ct);

    public static async Task<List<AttemptAnswer>> LoadAnswersAsync(IAppDbContext db, long attemptId, CancellationToken ct) =>
        await db.AttemptAnswers.Include(a => a.Parts).Where(a => a.AttemptId == attemptId).ToListAsync(ct);

    /// <summary>
    /// Chấm và chốt <paramref name="attempt"/>. <paramref name="answers"/> là các dòng đã lưu của lượt (được cập nhật và bổ sung).
    /// <paramref name="submittedAt"/> không muộn hơn hạn nộp; thời gian làm không vượt thời gian làm của bài.
    /// </summary>
    public static void Finish(
        IAppDbContext db,
        Quiz quiz,
        IReadOnlyList<QuizQuestion> items,
        Attempt attempt,
        List<AttemptAnswer> answers,
        AttemptStatus status,
        DateTime submittedAt,
        int level)
    {
        foreach (var item in items)
        {
            var layout = QuestionLayout.Build(item.Question!, attempt.Id);
            var row = answers.FirstOrDefault(a => a.QuestionId == item.QuestionId);
            if (row is null)
            {
                row = new AttemptAnswer { AttemptId = attempt.Id, QuestionId = item.QuestionId };
                answers.Add(row);
                db.AttemptAnswers.Add(row);
            }
            var stored = AttemptAnswerCodec.ToStored(layout, row);
            foreach (var part in AttemptAnswerCodec.Apply(row, layout, stored, item.Point)) db.AttemptAnswerParts.Add(part);
        }

        var total = items.Sum(x => x.Point ?? 0m);
        attempt.Status = status;
        attempt.SubmittedAt = submittedAt;
        attempt.DurationSeconds = attempt.StartedAt is { } started
            ? AttemptRules.DurationSeconds(started, submittedAt, AttemptRules.EffectiveTimeLimit(level, quiz.TimeLimitMinutes))
            : 0;
        attempt.Score = answers.Where(a => items.Any(i => i.QuestionId == a.QuestionId)).Sum(a => a.PointEarned ?? 0m);
        attempt.TotalPoint = quiz.TotalPoint ?? total;
        attempt.CorrectCount = answers.Count(a => a.IsCorrect);
        attempt.TotalQuestions = items.Count;
    }

    /// <summary>Lúc nộp: trước hạn nộp thì giờ hiện tại; quá hạn (tự nộp) thì đúng hạn nộp.</summary>
    public static DateTime SubmitTime(DateTime now, DateTime? deadline) => deadline is { } d && d < now ? d : now;
}
