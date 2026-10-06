using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Attempts;

/// <summary>
/// Trang học, lịch sử bài làm và tiến độ của em. Mọi số liệu tính từ <c>attempts</c> và <c>attempt_answers</c>, không có bảng mới; huy hiệu và sao tính khi
/// hiển thị. Chỉ tính lượt đã nộp hoặc hết giờ; chỉ của chính em.
/// </summary>
public sealed class StudentLearningService(IAppDbContext db, AttemptLifecycle lifecycle)
{
    public const string NeedsLessonCodeLabel = "Cần mã bài học";

    private const int OpenQuizLimit = 20;
    private const int MyLessonLimit = 20;

    /// <summary>Quiz đã phát hành của các bài học đã xuất bản của lớp em và đã tới giờ mở (cả quiz đã đóng): tập để tính tiến độ.</summary>
    private IQueryable<Domain.Quizzes.Quiz> PublishedQuizzes(StudentContext student, DateTime now) =>
        db.Quizzes.Where(q => q.DeletedAt == null && (q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED)
                              && (q.OpenAt == null || q.OpenAt <= now)
                              && q.Lesson!.DeletedAt == null && q.Lesson.Status == ContentStatus.PUBLISHED && q.Lesson.ClassId == student.ClassId);

    // ---- Trang học ----

    public async Task<StudentHomeDto> HomeAsync(Actor actor, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        var now = lifecycle.Now;

        // Lượt đã quá hạn nhận bài thì chốt trước, để không hiện "làm tiếp" cho lượt đã hết.
        var open = await db.Attempts.AsNoTracking().Where(a => a.StudentId == student.Id && a.Status == AttemptStatus.IN_PROGRESS).Select(a => a.Id).ToListAsync(ct);
        foreach (var id in open) await lifecycle.ExpireIfOverdueAsync(id, student.Level, ct);

        var inProgressRaw = await db.Attempts.AsNoTracking()
            .Where(a => a.StudentId == student.Id && a.Status == AttemptStatus.IN_PROGRESS)
            .OrderBy(a => a.StartedAt)
            .Select(a => new
            {
                a.Id, a.QuizId, QuizName = a.Quiz!.Name, a.Quiz.LessonId, LessonTitle = a.Quiz.Lesson!.Title, a.StartedAt,
                a.Quiz.TimeLimitMinutes, a.Quiz.CloseAt,
            })
            .ToListAsync(ct);
        var inProgress = inProgressRaw.Select(a =>
        {
            var deadline = a.StartedAt is { } started
                ? AttemptRules.DeadlineOf(started, AttemptRules.EffectiveTimeLimit(student.Level, a.TimeLimitMinutes), a.CloseAt)
                : a.CloseAt;
            return new StudentInProgressDto(a.Id, a.QuizId, a.QuizName, a.LessonId, a.LessonTitle, a.StartedAt ?? now, deadline, AttemptViews.RemainingSeconds(deadline, now));
        }).ToList();

        var lessonsWithAttempts = db.Attempts.Where(a => a.StudentId == student.Id).Select(a => a.Quiz!.LessonId);
        var openQuizzes = await db.Quizzes.AsNoTracking()
            .Where(q => q.DeletedAt == null && q.Status == QuizStatus.PUBLISHED
                        && (q.OpenAt == null || q.OpenAt <= now) && (q.CloseAt == null || q.CloseAt > now)
                        && q.Lesson!.DeletedAt == null && q.Lesson.Status == ContentStatus.PUBLISHED && q.Lesson.ClassId == student.ClassId
                        && !db.Attempts.Any(a => a.QuizId == q.Id && a.StudentId == student.Id))
            .OrderBy(q => q.CloseAt == null).ThenBy(q => q.CloseAt).ThenBy(q => q.Id)
            .Take(OpenQuizLimit)
            .Select(q => new
            {
                q.Id, q.Name, q.LessonId, LessonTitle = q.Lesson!.Title, q.OpenAt, q.CloseAt, q.TimeLimitMinutes, Questions = q.Questions.Count,
                NeedsCode = !lessonsWithAttempts.Contains(q.LessonId),
            })
            .ToListAsync(ct);

        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.StudentId == student.Id)
            .Select(a => new { a.Id, a.QuizId, a.Quiz!.LessonId, a.Status, a.Score, a.TotalPoint, a.StartedAt, a.SubmittedAt, QuizName = a.Quiz.Name })
            .ToListAsync(ct);
        var finished = attempts.Where(a => a.Status != AttemptStatus.IN_PROGRESS).OrderByDescending(a => a.SubmittedAt).ThenByDescending(a => a.Id).ToList();

        var lessonIds = attempts.Select(a => a.LessonId).Distinct().ToList();
        var lessons = await db.Lessons.AsNoTracking().Where(l => lessonIds.Contains(l.Id) && l.DeletedAt == null)
            .Select(l => new { l.Id, l.Title, l.AccessCode,
                Published = db.Quizzes.Count(q => q.LessonId == l.Id && q.DeletedAt == null && (q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED)) })
            .ToListAsync(ct);
        var myLessons = lessons
            .Select(l => new StudentLessonRefDto(
                l.Id, l.Title, l.AccessCode,
                finished.Where(a => a.LessonId == l.Id).Select(a => a.QuizId).Distinct().Count(), l.Published,
                attempts.Where(a => a.LessonId == l.Id).Max(a => a.SubmittedAt ?? a.StartedAt)))
            .OrderByDescending(l => l.LastAttemptAt).ThenBy(l => l.LessonId)
            .Take(MyLessonLimit).ToList();

        var recentBadges = finished.Take(AttemptRules.RecentBadgeLimit)
            .SelectMany(a => AttemptRules.AttemptBadges(a.Status, a.Score, a.TotalPoint)
                .Select(code => new StudentRecentBadgeDto(code, AttemptRules.BadgeName(code), a.Id, a.QuizName, a.SubmittedAt)))
            .Take(AttemptRules.RecentBadgeLimit).ToList();

        int? totalStars = student.Level == 1 ? finished.Sum(a => AttemptRules.Stars(a.Score ?? 0m, a.TotalPoint ?? 0m)) : null;

        return new StudentHomeDto(
            student.Id, student.StudentCode, student.FullName, student.ClassName, student.Grade, student.Level, student.LevelName,
            LessonFrames.For(student.Level).Layout, student.TeacherName, totalStars, inProgress,
            openQuizzes.Select(q => new StudentOpenQuizDto(
                q.Id, q.Name, q.LessonId, q.LessonTitle, q.OpenAt, q.CloseAt, AttemptRules.EffectiveTimeLimit(student.Level, q.TimeLimitMinutes),
                q.Questions, q.NeedsCode, q.NeedsCode ? NeedsLessonCodeLabel : null)).ToList(),
            myLessons, recentBadges);
    }

    // ---- Lịch sử bài làm ----

    /// <summary>Các lượt đã nộp của em, mới nhất trước. Cấp 1 hiện sao thay điểm.</summary>
    public async Task<PagedResult<StudentAttemptRowDto>> HistoryAsync(Actor actor, int? page, int? pageSize, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        var query = db.Attempts.AsNoTracking()
            .Where(a => a.StudentId == student.Id && a.Status != AttemptStatus.IN_PROGRESS)
            .OrderByDescending(a => a.SubmittedAt).ThenByDescending(a => a.Id)
            .Select(a => new
            {
                a.Id, a.QuizId, QuizName = a.Quiz!.Name, a.Quiz.LessonId, LessonTitle = a.Quiz.Lesson!.Title, a.Status, a.SubmittedAt, a.Score, a.TotalPoint,
            });
        var paged = await query.ToPagedAsync(page, pageSize, ct);

        var rows = paged.Items.Select(a =>
        {
            var score = a.Score ?? 0m;
            var total = a.TotalPoint ?? 0m;
            return new StudentAttemptRowDto(
                a.Id, a.QuizId, a.QuizName, a.LessonId, a.LessonTitle, a.Status!.Value, AttemptRules.StatusLabel(a.Status), a.SubmittedAt,
                student.Level == 1 ? AttemptRules.Stars(score, total) : null,
                student.Level == 1 ? null : score, student.Level == 1 ? null : total, student.Level == 1 ? null : AttemptRules.Percent(score, total),
                AttemptRules.AttemptBadges(a.Status, a.Score, a.TotalPoint).Select(AttemptResultService.Badge).ToList());
        }).ToList();
        return new PagedResult<StudentAttemptRowDto>(rows, paged.Page, paged.PageSize, paged.TotalItems, paged.TotalPages);
    }

    // ---- Tiến độ ----

    public async Task<StudentProgressDto> ProgressAsync(Actor actor, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        var now = lifecycle.Now;

        var published = await PublishedQuizzes(student, now).AsNoTracking().Select(q => new { q.Id, q.LessonId }).ToListAsync(ct);
        var finished = await db.Attempts.AsNoTracking()
            .Where(a => a.StudentId == student.Id && a.Status != AttemptStatus.IN_PROGRESS)
            .OrderBy(a => a.SubmittedAt).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.QuizId, a.Status, a.Score, a.TotalPoint, a.SubmittedAt })
            .ToListAsync(ct);
        var finishedQuizIds = finished.Select(a => a.QuizId).ToHashSet();

        var submitted = published.Count(q => finishedQuizIds.Contains(q.Id));
        var byLesson = published.GroupBy(q => q.LessonId).ToList();
        var completed = byLesson.Count(g => g.All(q => finishedQuizIds.Contains(q.Id)));

        var mastery = await db.AttemptAnswers.AsNoTracking()
            .Where(a => a.Attempt!.StudentId == student.Id && a.Attempt.Status != AttemptStatus.IN_PROGRESS)
            .GroupBy(a => a.Question!.HeritageId)
            .Select(g => new { HeritageId = g.Key, Answered = g.Count(), Correct = g.Count(x => x.IsCorrect) })
            .ToListAsync(ct);
        var ids = mastery.Select(m => m.HeritageId).ToList();
        var names = await db.Heritages.AsNoTracking().Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, ct);
        var masteryRows = mastery.Select(m =>
            {
                var level = AttemptRules.MasteryOf(m.Correct, m.Answered);
                return new StudentMasteryDto(
                    m.HeritageId, names.GetValueOrDefault(m.HeritageId), m.Answered, m.Correct,
                    student.Level == 1 ? null : AttemptRules.RatePercent(m.Correct, m.Answered), level, AttemptRules.MasteryLabel(level), AttemptRules.MasteryStars(level));
            })
            .OrderBy(m => m.Name).ThenBy(m => m.HeritageId).ToList();

        var earned = new List<StudentEarnedBadgeDto>();
        var perAttempt = finished.Select(a => (a.Id, a.SubmittedAt, Badges: AttemptRules.AttemptBadges(a.Status, a.Score, a.TotalPoint))).ToList();
        foreach (var code in new[] { BadgeCode.PERFECT_SCORE, BadgeCode.EXCELLENT, BadgeCode.ON_TIME })
        {
            var hits = perAttempt.Where(a => a.Badges.Contains(code)).ToList();
            if (hits.Count == 0) continue;
            var last = hits[^1];
            earned.Add(new StudentEarnedBadgeDto(code, AttemptRules.BadgeName(code), AttemptRules.BadgeDescription(code), hits.Count, last.SubmittedAt, last.Id));
        }
        if (AttemptRules.HasPerseverance(finished.Count))
        {
            var fifth = finished[AttemptRules.PerseveranceAttempts - 1];
            earned.Add(new StudentEarnedBadgeDto(BadgeCode.PERSEVERANCE, AttemptRules.BadgeName(BadgeCode.PERSEVERANCE),
                AttemptRules.BadgeDescription(BadgeCode.PERSEVERANCE), 1, fifth.SubmittedAt, fifth.Id));
        }

        int? totalStars = student.Level == 1 ? finished.Sum(a => AttemptRules.Stars(a.Score ?? 0m, a.TotalPoint ?? 0m)) : null;
        var percent = published.Count == 0 ? 0m : Math.Round(submitted * 100m / published.Count, 1, MidpointRounding.AwayFromZero);
        return new StudentProgressDto(
            student.Level, student.LevelName, submitted, published.Count, percent, completed, byLesson.Count, finished.Count, totalStars, masteryRows, earned);
    }
}
