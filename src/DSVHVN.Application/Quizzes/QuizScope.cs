using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Quizzes;

/// <summary>
/// Phạm vi bài kiểm tra của giáo viên: bài do chính người gọi tạo, thuộc bài học của chính mình ở lớp của trường mình, bài kiểm tra, bài học
/// và lớp đều chưa xóa mềm. Bài kiểm tra của giáo viên khác (kể cả cùng trường), của trường khác hay của lớp đã xóa đều như không tồn tại (404).
/// </summary>
internal static class QuizScope
{
    public static IQueryable<Quiz> QuizzesOf(this IAppDbContext db, Actor actor)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        var orgId = actor.RequireOrganizationId();
        return db.Quizzes.Where(q => q.DeletedAt == null && q.CreatedBy == actor.UserId
                                     && q.Lesson!.TeacherId == actor.UserId && q.Lesson.DeletedAt == null
                                     && q.Lesson.Class!.OrganizationId == orgId && q.Lesson.Class.DeletedAt == null);
    }

    /// <summary>Bài kiểm tra kèm bài học, lớp và các câu (kèm nội dung câu).</summary>
    public static async Task<Quiz> FindQuizAsync(this IAppDbContext db, Actor actor, long quizId, string action, bool tracking, CancellationToken ct)
    {
        var query = db.QuizzesOf(actor)
            .Include(q => q.Lesson).ThenInclude(l => l!.Class)
            .Include(q => q.Questions).ThenInclude(x => x.Question)
            .AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(q => q.Id == quizId, ct)
               ?? throw AppException.NotFound(Messages.NotFound(action, "bài kiểm tra của bạn"));
    }
}

/// <summary>Câu hỏi nào dùng được trong bài kiểm tra của lớp ở một cấp (quy tắc Soạn quiz và tổng điểm, Dạng câu hỏi và cấp phù hợp).</summary>
internal static class QuizQuestionRules
{
    /// <summary>Lý do câu không dùng được, hoặc null nếu dùng được: câu đã xóa, đã bị ẩn hoặc không còn được duyệt, dạng và cấp không hợp cấp của lớp.</summary>
    public static string? UnusableReason(Question q, int level)
    {
        if (q.DeletedAt is not null) return "Câu hỏi đã bị xóa.";
        if (q.ReviewStatus != ReviewStatus.APPROVED) return "Câu hỏi đã bị ẩn hoặc không còn được duyệt.";
        if (!QuizRules.QuestionFitsLevel(q.QuestionType, q.LevelMin, q.LevelMax, level))
            return Messages.QuestionLevelMismatch(QuestionTypes.NameOf(q.QuestionType), level).Text;
        return null;
    }

    /// <summary>Câu bị bỏ khi nhân bản bài kiểm tra: câu đã xóa hoặc không còn được duyệt (đã bị ẩn).</summary>
    public static bool IsGone(Question q) => q.DeletedAt is not null || q.ReviewStatus != ReviewStatus.APPROVED;
}

/// <summary>Chép bài kiểm tra thành bản nháp mới: dùng khi nhân bản bài kiểm tra và khi nhân bản bài học kèm bài kiểm tra.</summary>
internal static class QuizCopier
{
    /// <param name="source">Bài kiểm tra nguồn, đã nạp <c>Questions</c> kèm <c>Question</c>.</param>
    /// <param name="targetLevel">Cấp của lớp đích khi nhân bản sang lớp khác (bỏ câu không hợp cấp, chuẩn hóa thời gian làm); null khi ở cùng lớp.</param>
    public static (Quiz Copy, int SkippedQuestions) Copy(Quiz source, long createdBy, string? name, int? targetLevel)
    {
        var copy = new Quiz
        {
            CreatedBy = createdBy,
            Name = name ?? source.Name,
            Description = source.Description,
            DifficultyLevel = source.DifficultyLevel,
            TimeLimitMinutes = targetLevel is { } level ? QuizRules.NormalizeTimeLimit(level, source.TimeLimitMinutes) : source.TimeLimitMinutes,
            ShuffleQuestions = source.ShuffleQuestions,
            ShowAnswersAfterSubmit = source.ShowAnswersAfterSubmit,
            Status = QuizStatus.DRAFT,
        };

        var skipped = 0;
        var order = 0;
        foreach (var item in source.Questions.OrderBy(x => x.SortOrder).ThenBy(x => x.QuestionId))
        {
            var question = item.Question!;
            var drop = QuizQuestionRules.IsGone(question)
                       || (targetLevel is { } l && !QuizRules.QuestionFitsLevel(question.QuestionType, question.LevelMin, question.LevelMax, l));
            if (drop)
            {
                skipped++;
                continue;
            }
            copy.Questions.Add(new QuizQuestion { QuestionId = item.QuestionId, Point = item.Point, SortOrder = ++order });
        }
        copy.TotalPoint = copy.Questions.Sum(x => x.Point ?? 0m);
        return (copy, skipped);
    }
}
