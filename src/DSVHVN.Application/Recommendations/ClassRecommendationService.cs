using DSVHVN.Application.Classes;
using DSVHVN.Application.Common;
using DSVHVN.Application.Quizzes;
using DSVHVN.Application.Statistics;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Recommendations;

/// <summary>
/// Gợi ý ôn tập cho lớp (cùng bộ tính với gợi ý cho học sinh): tỉ lệ sai theo di sản = số câu trả lời sai hoặc bỏ trống ÷ tổng số câu trả lời gắn di sản đó,
/// trên các lượt đã nộp của lớp, lọc được theo bài kiểm tra và khoảng thời gian nộp. "Nên ôn": tỉ lệ sai từ 40% và ít nhất 10 câu trả lời, tối đa 5.
/// "Liên quan": tối đa 5 di sản giống nhất với các di sản của những bài học đã giao cho lớp. Chỉ gợi ý di sản đã xuất bản; không gửi gì về học sinh sang Dịch vụ AI.
/// </summary>
public sealed class ClassRecommendationService(IAppDbContext db, RelatedRanker ranker)
{
    /// <summary>Gợi ý của lớp chỉ tính từ một bài kiểm tra (tab Gợi ý của kết quả bài kiểm tra); lớp là lớp của bài học chứa bài kiểm tra.</summary>
    public async Task<ClassRecommendationsDto> GetForQuizAsync(Actor actor, long quizId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var classId = await db.QuizzesOf(actor).Where(q => q.Id == quizId).Select(q => (long?)q.Lesson!.ClassId).SingleOrDefaultAsync(ct)
                      ?? throw AppException.NotFound(Messages.NotFound("xem gợi ý ôn tập", "bài kiểm tra của bạn"));
        return await GetAsync(actor, classId, quizId, from, to, ct);
    }

    public async Task<ClassRecommendationsDto> GetAsync(Actor actor, long classId, long? quizId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        const string action = "xem gợi ý ôn tập";
        var schoolClass = await db.FindClassAsync(actor, classId, action, false, ct);
        if (from is not null && to is not null && from > to)
            throw AppException.Validation("from", Messages.Invalid("Khoảng thời gian", "ngày bắt đầu phải trước ngày kết thúc"));
        if (quizId is { } id)
        {
            var inClass = await db.QuizzesOf(actor).AnyAsync(q => q.Id == id && q.Lesson!.ClassId == classId, ct);
            if (!inClass) throw AppException.NotFound(Messages.NotFound(action, "bài kiểm tra của lớp này"));
        }

        var level = Domain.Classes.SchoolClass.SchoolLevelOf(schoolClass.Grade);
        var answers = db.FinishedAnswers(classId);
        if (quizId is not null) answers = answers.Where(a => a.Attempt!.QuizId == quizId);
        if (from is not null) answers = answers.Where(a => a.Attempt!.SubmittedAt >= from);
        if (to is not null) answers = answers.Where(a => a.Attempt!.SubmittedAt <= to);

        var attemptsQuery = db.FinishedAttempts(classId);
        if (quizId is not null) attemptsQuery = attemptsQuery.Where(a => a.QuizId == quizId);
        if (from is not null) attemptsQuery = attemptsQuery.Where(a => a.SubmittedAt >= from);
        if (to is not null) attemptsQuery = attemptsQuery.Where(a => a.SubmittedAt <= to);
        var submitted = await attemptsQuery.CountAsync(ct);

        var rates = await answers
            .GroupBy(a => a.Question!.HeritageId)
            .Select(g => new { HeritageId = g.Key, Answered = g.Count(), Wrong = g.Count(x => !x.IsCorrect) })
            .ToListAsync(ct);
        var answered = rates.Sum(r => r.Answered);

        if (submitted == 0)
            return new ClassRecommendationsDto(classId, schoolClass.Name, level, quizId, from, to, 0, 0, [], [], [], Messages.ClassNoResultsYet, false);

        var catalog = await HeritageCatalog.LoadAsync(db, ct);
        var review = rates
            .Where(r => RecommendationRules.ClassShouldReview(r.Wrong, r.Answered) && catalog.IsPublished(r.HeritageId))
            .OrderByDescending(r => RecommendationRules.WrongRatio(r.Wrong, r.Answered)).ThenByDescending(r => r.Answered).ThenBy(r => r.HeritageId)
            .Take(RecommendationRules.ClassSectionLimit)
            .Select(r => catalog.ReviewCard(r.HeritageId, r.Answered, r.Wrong)!)
            .ToList();

        // Di sản liên quan: giống nhất với các di sản của bài học đã xuất bản của lớp, di sản nên ôn nặng hơn.
        var assigned = await db.LessonHeritages.AsNoTracking()
            .Where(x => x.Lesson!.ClassId == classId && x.Lesson.DeletedAt == null && x.Lesson.Status == ContentStatus.PUBLISHED)
            .Select(x => x.HeritageId).Distinct().ToListAsync(ct);
        var reviewIds = review.Select(r => r.HeritageId).ToHashSet();
        var references = assigned.Select(catalog.Features).OfType<HeritageFeatures>()
            .Select(f => new WeightedHeritage(f, reviewIds.Contains(f.Id) ? 2d : 1d)).ToList();
        var assignedSet = assigned.ToHashSet();
        var candidates = catalog.Published.Where(h => !assignedSet.Contains(h.Id)).ToList();
        var ranked = await ranker.RankAsync(new RelatedRequest(null, null, null, references, candidates), ct);
        var related = ranked.Scores.Values
            .Where(r => catalog.IsPublished(r.HeritageId))
            .OrderByDescending(r => r.Score).ThenBy(r => r.HeritageId)
            .Take(RecommendationRules.ClassSectionLimit)
            .Select(r => catalog.Card(r.HeritageId, r.Reason)!)
            .ToList();

        return new ClassRecommendationsDto(
            classId, schoolClass.Name, level, quizId, from, to, submitted, answered, review, related,
            review.Select(r => r.HeritageId).ToList(), null, ranked.UsedFallback);
    }
}
