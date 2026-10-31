using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Recommendations;

/// <summary>
/// "Gợi ý cho em": năm phần tính riêng cho từng học sinh khi hiển thị, từ cấp học (khối lớp), tiến độ và kết quả làm bài của em; "sở thích" suy từ các di sản
/// của những bài em đã có lượt làm. Content-based, không gọi mô hình ngôn ngữ, không tính lượt AI, không lưu, không thêm bảng. Ứng viên chỉ lấy từ CSDL và chỉ
/// thuộc lớp của em, nên web học tập và app cho cùng một kết quả; không bao giờ gợi ý bài học hoặc quiz của lớp khác, và chỉ gợi ý di sản đã xuất bản.
/// Chỉ phần xếp hạng di sản liên quan đi qua <see cref="RelatedRanker"/> (Dịch vụ AI, có phương án dự phòng); các phần còn lại là quy tắc cố định tính ở máy chủ.
/// </summary>
public sealed class StudentRecommendationService(IAppDbContext db, AttemptLifecycle lifecycle, RelatedRanker ranker)
{
    private sealed record AnswerFact(long AttemptId, long LessonId, long HeritageId, bool IsCorrect, decimal? PointEarned, decimal? Point);

    private sealed record QuizRow(long Id, long LessonId, string? Name, QuizStatus? Status, DateTime? OpenAt, DateTime? CloseAt, DifficultyLevel? Difficulty);

    private sealed record LessonRow(long Id, string? Title, string? AccessCode, DateTime? CreatedAt);

    /// <param name="attemptId">Trang kết quả: phần "di sản nên ôn" chỉ tính từ lượt này. Null (trang học): tính từ mọi lượt đã nộp.</param>
    public async Task<StudentRecommendationsDto> GetAsync(Actor actor, long? attemptId, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        var now = lifecycle.Now;

        if (attemptId is { } id) await EnsureFinishedAttemptAsync(student, id, ct);

        // Lượt đã nộp của em: từng câu trả lời kèm di sản và điểm đã mất.
        var facts = await db.AttemptAnswers.AsNoTracking()
            .Where(a => a.Attempt!.StudentId == student.Id && a.Attempt.Status != AttemptStatus.IN_PROGRESS)
            .Select(a => new AnswerFact(
                a.AttemptId, a.Attempt!.Quiz!.LessonId, a.Question!.HeritageId, a.IsCorrect, a.PointEarned,
                a.Attempt.Quiz.Questions.Where(x => x.QuestionId == a.QuestionId).Select(x => x.Point).FirstOrDefault()))
            .ToListAsync(ct);
        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.StudentId == student.Id)
            .Select(a => new { a.QuizId, a.Quiz!.LessonId, a.Status })
            .ToListAsync(ct);
        var attemptedLessons = attempts.Select(a => a.LessonId).ToHashSet();
        var openedLessons = (await db.LessonVisits.AsNoTracking().Where(v => v.StudentId == student.Id).Select(v => v.LessonId).ToListAsync(ct)).ToHashSet();
        var attemptedQuizzes = attempts.Select(a => a.QuizId).ToHashSet();
        var finishedLessons = attempts.Where(a => a.Status != AttemptStatus.IN_PROGRESS).Select(a => a.LessonId).ToHashSet();
        // Em chưa có lượt nào: quiz nên làm tiếp và bài học mới xếp theo mức khó mục tiêu theo cấp trước (quy tắc gợi ý cá nhân hóa cho học sinh).
        var hasAttempts = attempts.Count > 0;

        // Mức thành thạo theo di sản và chung, từ mọi lượt đã nộp.
        var byHeritage = facts.GroupBy(f => f.HeritageId)
            .ToDictionary(g => g.Key, g => (Answered: g.Count(), Correct: g.Count(f => f.IsCorrect)));
        var target = RecommendationRules.TargetDifficulty(student.Level, facts.Count(f => f.IsCorrect), facts.Count);
        var weak = byHeritage.Where(h => AttemptRules.MasteryOf(h.Value.Correct, h.Value.Answered) == MasteryLevel.NEEDS_REVIEW).Select(h => h.Key).ToHashSet();

        // Ứng viên: chỉ bài học đã xuất bản của lớp em.
        var lessons = await db.Lessons.AsNoTracking()
            .Where(l => l.DeletedAt == null && l.Status == ContentStatus.PUBLISHED && l.ClassId == student.ClassId)
            .Select(l => new LessonRow(l.Id, l.Title, l.AccessCode, l.CreatedAt))
            .ToListAsync(ct);
        var lessonIds = lessons.Select(l => l.Id).ToList();
        var quizzes = await db.Quizzes.AsNoTracking()
            .Where(q => q.DeletedAt == null && lessonIds.Contains(q.LessonId) && (q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED))
            .Select(q => new QuizRow(q.Id, q.LessonId, q.Name, q.Status, q.OpenAt, q.CloseAt, q.DifficultyLevel))
            .ToListAsync(ct);
        var quizIds = quizzes.Select(q => q.Id).ToList();
        var quizHeritages = (await db.QuizQuestions.AsNoTracking()
                .Where(x => quizIds.Contains(x.QuizId))
                .Select(x => new { x.QuizId, x.Question!.HeritageId })
                .ToListAsync(ct))
            .GroupBy(x => x.QuizId).ToDictionary(g => g.Key, g => g.Select(x => x.HeritageId).ToList());

        var heritageLessonIds = lessonIds.Concat(attemptedLessons).Distinct().ToList();
        var lessonHeritages = (await db.LessonHeritages.AsNoTracking()
                .Where(x => heritageLessonIds.Contains(x.LessonId))
                .Select(x => new { x.LessonId, x.HeritageId })
                .ToListAsync(ct))
            .GroupBy(x => x.LessonId).ToDictionary(g => g.Key, g => g.Select(x => x.HeritageId).ToList());

        var catalog = await HeritageCatalog.LoadAsync(db, ct);

        // (1) Quiz nên làm tiếp: đang mở, em chưa làm. Em đã có lượt: di sản em còn yếu trước, rồi hạn đóng gần, rồi độ khó gần mức mục tiêu;
        // em chưa có lượt nào: độ khó gần mức mục tiêu theo cấp trước, rồi hạn đóng gần.
        var openQuizzes = quizzes
            .Where(q => q.Status == QuizStatus.PUBLISHED && (q.OpenAt is null || q.OpenAt <= now) && (q.CloseAt is null || q.CloseAt > now)
                        && !attemptedQuizzes.Contains(q.Id))
            .Select(q => (Quiz: q, Weak: quizHeritages.GetValueOrDefault(q.Id)?.Count(weak.Contains) ?? 0));
        var nextQuizzes = OrderNextQuizzes(openQuizzes, hasAttempts, target)
            .ThenBy(x => x.Quiz.Id)
            .Take(RecommendationRules.StudentSectionLimit)
            .Select(x =>
            {
                var needsCode = !attemptedLessons.Contains(x.Quiz.LessonId) && !openedLessons.Contains(x.Quiz.LessonId);
                var reason = x.Weak > 0 ? RecommendationRules.WeakQuizReason(student.Level, x.Weak)
                    : x.Quiz.CloseAt is { } close && close - now <= RecommendationRules.ClosingSoon ? RecommendationRules.ClosingSoonReason(close, now)
                    : RecommendationRules.OpenQuizReason;
                return new RecommendedQuizDto(
                    x.Quiz.Id, x.Quiz.Name, x.Quiz.LessonId, lessons.First(l => l.Id == x.Quiz.LessonId).Title, x.Quiz.CloseAt, x.Quiz.Difficulty,
                    x.Quiz.Difficulty is null ? null : RecommendationRules.DifficultyName(x.Quiz.Difficulty), needsCode,
                    needsCode ? StudentLearningService.NeedsLessonCodeLabel : null, reason);
            })
            .ToList();

        // (2) Bài học nên ôn lại: em đã làm quiz của bài và đúng dưới 50% ở các di sản của bài.
        var lessonsToReview = lessons
            .Where(l => finishedLessons.Contains(l.Id))
            .Select(l =>
            {
                var rows = (lessonHeritages.GetValueOrDefault(l.Id) ?? []).Where(byHeritage.ContainsKey).Select(h => byHeritage[h]).ToList();
                var answered = rows.Sum(r => r.Answered);
                return (Lesson: l, Answered: answered, Rate: answered == 0 ? 100m : AttemptRules.RatePercent(rows.Sum(r => r.Correct), answered));
            })
            .Where(x => x.Answered > 0 && x.Rate < RecommendationRules.ReviewLessonRatio * 100m)
            .OrderBy(x => x.Rate).ThenBy(x => x.Lesson.Id)
            .Take(RecommendationRules.StudentSectionLimit)
            .Select(x => new RecommendedLessonDto(x.Lesson.Id, x.Lesson.Title, x.Lesson.AccessCode, null, RecommendationRules.ReviewLessonReason(student.Level, x.Rate)))
            .ToList();

        // (3) Di sản nên ôn: từ câu sai hoặc bỏ trống, xếp theo tổng điểm bị mất. Trang kết quả chỉ tính từ lượt đang xem.
        var scope = attemptId is { } viewed ? facts.Where(f => f.AttemptId == viewed).ToList() : facts;
        var heritagesToReview = scope.Where(f => !f.IsCorrect)
            .GroupBy(f => f.HeritageId)
            .Select(g => (HeritageId: g.Key, Wrong: g.Count(), Lost: g.Sum(f => Math.Max(0m, (f.Point ?? 0m) - (f.PointEarned ?? 0m)))))
            .Where(x => catalog.IsPublished(x.HeritageId))
            .OrderByDescending(x => x.Lost).ThenByDescending(x => x.Wrong).ThenBy(x => x.HeritageId)
            .Take(RecommendationRules.StudentSectionLimit)
            .Select(x => catalog.Card(x.HeritageId, RecommendationRules.StudentReviewReason(student.Level, x.Wrong))!)
            .ToList();
        var praise = scope.Count > 0 && scope.All(f => f.IsCorrect) ? RecommendationRules.ReviewPraiseForLevel(student.Level) : null;

        // (4) Di sản liên quan và (5) bài học mới: một lần xếp hạng chung cho cả hai.
        var newLessons = lessons.Where(l => !attemptedLessons.Contains(l.Id)).ToList();
        var seen = attemptedLessons.SelectMany(l => lessonHeritages.GetValueOrDefault(l) ?? []).Concat(byHeritage.Keys).ToHashSet();
        var references = seen
            .Select(h => catalog.Features(h))
            .OfType<HeritageFeatures>()
            .Select(f => new WeightedHeritage(f, weak.Contains(f.Id) ? 2d : 1d))
            .ToList();
        var candidates = catalog.Published.Where(h => !seen.Contains(h.Id)).ToDictionary(h => h.Id);
        foreach (var lesson in newLessons)
            foreach (var h in lessonHeritages.GetValueOrDefault(lesson.Id) ?? [])
                if (catalog.Features(h) is { } features) candidates[h] = features;

        var ranked = await ranker.RankAsync(new RelatedRequest(student.Id, student.Level, target, references, candidates.Values.ToList()), ct);

        var related = candidates.Keys.Where(h => !seen.Contains(h) && ranked.Of(h) is not null && catalog.IsPublished(h))
            .Select(h => (HeritageId: h, Score: ranked.Of(h)!))
            .OrderByDescending(x => x.Score.Score).ThenBy(x => x.HeritageId)
            .Take(RecommendationRules.StudentSectionLimit)
            .Select(x => catalog.Card(x.HeritageId, x.Score.Reason)!)
            .ToList();

        var hasReferences = references.Count > 0;
        var distances = quizzes.GroupBy(q => q.LessonId)
            .ToDictionary(g => g.Key, g => g.Min(q => RecommendationRules.DifficultyDistance(q.Difficulty, target)));
        var newRanked = newLessons
            .Select(l =>
            {
                var best = (lessonHeritages.GetValueOrDefault(l.Id) ?? []).Select(ranked.Of).OfType<RelatedHeritage>().OrderByDescending(r => r.Score).FirstOrDefault();
                return (Lesson: l, Match: best, Distance: distances.GetValueOrDefault(l.Id, 2));
            })
            .ToList();
        var newCards = OrderNewLessons(newRanked, hasAttempts, hasReferences)
            .ThenByDescending(x => x.Lesson.Id)
            .Take(RecommendationRules.StudentSectionLimit)
            .Select(x => new RecommendedLessonDto(x.Lesson.Id, x.Lesson.Title, null, RecommendationRules.AskTeacherForLessonCode, x.Match?.Reason ?? RecommendationRules.NewLessonReason))
            .ToList();

        var message = !hasAttempts || lessons.Count == 0 ? Messages.StudentNoAttemptsYet : null;
        return new StudentRecommendationsDto(
            student.Level, student.LevelName, LessonFrames.For(student.Level).Layout, attemptId, target, RecommendationRules.DifficultyName(target),
            nextQuizzes, lessonsToReview, heritagesToReview, related, newCards, praise, message, ranked.UsedFallback);
    }

    /// <summary>
    /// Thứ tự phần "quiz nên làm tiếp". Em đã có lượt: quiz có câu về di sản em còn yếu trước, rồi hạn đóng gần nhất (không hạn thì sau cùng), rồi độ khó
    /// gần mức khó mục tiêu. Em chưa có lượt nào: độ khó gần mức khó mục tiêu theo cấp trước, rồi hạn đóng gần nhất.
    /// </summary>
    private static IOrderedEnumerable<(QuizRow Quiz, int Weak)> OrderNextQuizzes(
        IEnumerable<(QuizRow Quiz, int Weak)> open, bool hasAttempts, DifficultyLevel target) =>
        hasAttempts
            ? open.OrderByDescending(x => x.Weak > 0)
                .ThenBy(x => x.Quiz.CloseAt is null)
                .ThenBy(x => x.Quiz.CloseAt)
                .ThenBy(x => RecommendationRules.DifficultyDistance(x.Quiz.Difficulty, target))
            : open.OrderBy(x => RecommendationRules.DifficultyDistance(x.Quiz.Difficulty, target))
                .ThenBy(x => x.Quiz.CloseAt is null)
                .ThenBy(x => x.Quiz.CloseAt);

    /// <summary>
    /// Thứ tự phần "bài học mới nên học". Có di sản tham chiếu: độ tương đồng cao trước, rồi độ khó các quiz của bài gần mức khó mục tiêu, rồi bài mới tạo trước.
    /// Em đã có lượt nhưng chưa có gì để so: bài mới tạo trước, rồi độ khó. Em chưa có lượt nào: độ khó gần mức khó mục tiêu theo cấp trước, rồi bài mới tạo trước.
    /// </summary>
    private static IOrderedEnumerable<(LessonRow Lesson, RelatedHeritage? Match, int Distance)> OrderNewLessons(
        IEnumerable<(LessonRow Lesson, RelatedHeritage? Match, int Distance)> lessons, bool hasAttempts, bool hasReferences) =>
        hasReferences
            ? lessons.OrderByDescending(x => x.Match?.Score ?? 0d).ThenBy(x => x.Distance).ThenByDescending(x => x.Lesson.CreatedAt)
            : hasAttempts
                ? lessons.OrderByDescending(x => x.Lesson.CreatedAt).ThenBy(x => x.Distance)
                : lessons.OrderBy(x => x.Distance).ThenByDescending(x => x.Lesson.CreatedAt);

    /// <summary>Lượt phải là của em (lượt của em khác như không tồn tại) và đã nộp; lượt đã quá hạn nhận bài thì chốt hết giờ trước.</summary>
    private async Task EnsureFinishedAttemptAsync(StudentContext student, long attemptId, CancellationToken ct)
    {
        var status = await db.Attempts.AsNoTracking().Where(a => a.Id == attemptId && a.StudentId == student.Id).Select(a => a.Status).SingleOrDefaultAsync(ct);
        if (status is null) throw AppException.NotFound(Messages.NotFound("xem gợi ý", "bài làm của em"));
        if (status == AttemptStatus.IN_PROGRESS && !await lifecycle.ExpireIfOverdueAsync(attemptId, student.Level, ct))
            throw new AppException(409, Messages.AttemptInProgress);
    }
}
