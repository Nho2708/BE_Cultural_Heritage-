using DSVHVN.Application.Attempts;
using DSVHVN.Application.Classes;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Statistics;

/// <summary>
/// Thống kê của một lớp cho giáo viên phụ trách: tiến độ (bảng bài kiểm tra và bảng học sinh × bài kiểm tra), mức thành thạo (theo di sản, tỉnh, loại di sản,
/// dạng câu, bảng nhiệt) và hồ sơ học tập của từng học sinh. Mọi số liệu tính từ <c>attempts</c> và <c>attempt_answers</c>, không có bảng mới; huy hiệu tính khi
/// hiển thị; chỉ tính lượt đã nộp của học sinh đang hoạt động; giáo viên luôn thấy điểm. Lớp của giáo viên khác hoặc của trường khác như không tồn tại (404).
/// </summary>
public sealed class ClassStatisticsService(IAppDbContext db, TimeProvider clock)
{
    private const int HeatmapHeritageLimit = 60;
    private const string NoProvince = "Chưa xác định tỉnh";

    private sealed record QuizRow(long Id, string? Name, long LessonId, string? LessonTitle, QuizStatus? Status, DateTime? OpenAt, DateTime? CloseAt);

    private sealed record AttemptRow(long Id, long QuizId, long StudentId, AttemptStatus? Status, decimal? Score, decimal? TotalPoint, DateTime? SubmittedAt);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Quiz đã phát hành (đang mở hoặc đã đóng) và đã tới giờ mở, thuộc các bài học đã xuất bản của lớp: tập để tính tiến độ.</summary>
    private async Task<List<QuizRow>> PublishedQuizzesAsync(long classId, CancellationToken ct)
    {
        var now = Now;
        return await db.Quizzes.AsNoTracking()
            .Where(q => q.DeletedAt == null && (q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED) && (q.OpenAt == null || q.OpenAt <= now)
                        && q.Lesson!.DeletedAt == null && q.Lesson.Status == ContentStatus.PUBLISHED && q.Lesson.ClassId == classId)
            .OrderBy(q => q.LessonId).ThenBy(q => q.OpenAt).ThenBy(q => q.Id)
            .Select(q => new QuizRow(q.Id, q.Name, q.LessonId, q.Lesson!.Title, q.Status, q.OpenAt, q.CloseAt))
            .ToListAsync(ct);
    }

    // ---- Tiến độ ----

    public async Task<ClassProgressDto> ProgressAsync(Actor actor, long classId, CancellationToken ct)
    {
        var schoolClass = await db.FindClassAsync(actor, classId, "xem tiến độ lớp", false, ct);
        var level = SchoolClass.SchoolLevelOf(schoolClass.Grade);
        var students = await db.ActiveStudents(classId).AsNoTracking()
            .OrderBy(s => s.StudentCode).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.StudentCode, s.FullName, LastLoginAt = s.User!.LastLoginAt })
            .ToListAsync(ct);
        var quizzes = await PublishedQuizzesAsync(classId, ct);
        var quizIds = quizzes.Select(q => q.Id).ToHashSet();
        var attempts = await db.AllAttempts(classId)
            .Select(a => new AttemptRow(a.Id, a.QuizId, a.StudentId, a.Status, a.Score, a.TotalPoint, a.SubmittedAt))
            .ToListAsync(ct);
        var finished = attempts.Where(a => a.Status != AttemptStatus.IN_PROGRESS).ToList();
        var finishedByStudent = finished.ToLookup(a => a.StudentId);

        var enrolled = students.Count;
        var submittedInSet = finished.Count(a => quizIds.Contains(a.QuizId));
        var denominator = enrolled * quizzes.Count;

        var quizRows = quizzes.Select(q =>
        {
            var submitted = finished.Count(a => a.QuizId == q.Id);
            return new ClassQuizProgressDto(q.Id, q.Name, q.LessonId, q.LessonTitle, q.Status, q.OpenAt, q.CloseAt, submitted, enrolled,
                enrolled == 0 ? 0m : AttemptRules.RatePercent(submitted, enrolled));
        }).ToList();

        var lessonQuizzes = quizzes.GroupBy(q => q.LessonId).ToDictionary(g => g.Key, g => g.Select(q => q.Id).ToList());
        var studentRows = students.Select(s =>
        {
            var mine = finishedByStudent[s.Id].ToList();
            var doneQuizzes = mine.Select(a => a.QuizId).ToHashSet();
            return new ClassStudentProgressDto(
                s.Id, s.StudentCode, s.FullName, s.LastLoginAt, s.LastLoginAt is null,
                doneQuizzes.Count(quizIds.Contains), quizzes.Count,
                lessonQuizzes.Values.Count(ids => ids.All(doneQuizzes.Contains)), lessonQuizzes.Count,
                level == 1 ? mine.Sum(a => AttemptRules.Stars(a.Score ?? 0m, a.TotalPoint ?? 0m)) : null,
                AveragePercent(mine), BadgeCounts(mine.Select(a => (a.Status, a.Score, a.TotalPoint)).ToList()));
        }).ToList();

        var cells = attempts.Where(a => quizIds.Contains(a.QuizId))
            .OrderBy(a => a.StudentId).ThenBy(a => a.QuizId)
            .Select(a =>
            {
                var done = a.Status != AttemptStatus.IN_PROGRESS;
                return new ClassProgressCellDto(
                    a.StudentId, a.QuizId, a.Id, StatsMath.StatusOf(a.Status), done ? a.Score ?? 0m : null, done ? a.TotalPoint ?? 0m : null,
                    done ? AttemptRules.Percent(a.Score ?? 0m, a.TotalPoint ?? 0m) : null,
                    done && level == 1 ? AttemptRules.Stars(a.Score ?? 0m, a.TotalPoint ?? 0m) : null);
            }).ToList();

        return new ClassProgressDto(
            classId, schoolClass.Name, schoolClass.Grade, level, SchoolLevels.NameOf(level), enrolled, students.Count(s => s.LastLoginAt is null),
            quizzes.Count, submittedInSet, denominator == 0 ? 0m : AttemptRules.RatePercent(submittedInSet, denominator),
            quizRows, studentRows, cells, finished.Count == 0 ? Messages.ClassNoResultsYet : null);
    }

    // ---- Mức thành thạo ----

    public async Task<ClassMasteryDto> MasteryAsync(Actor actor, long classId, int? provinceId, HeritageType? heritageType, CancellationToken ct)
    {
        var schoolClass = await db.FindClassAsync(actor, classId, "xem mức thành thạo của lớp", false, ct);
        var students = await db.ActiveStudents(classId).AsNoTracking()
            .OrderBy(s => s.StudentCode).ThenBy(s => s.Id)
            .Select(s => new HeatmapStudentDto(s.Id, s.StudentCode, s.FullName))
            .ToListAsync(ct);

        var perStudent = await db.FinishedAnswers(classId)
            .GroupBy(a => new { a.Attempt!.StudentId, a.Question!.HeritageId })
            .Select(g => new { g.Key.StudentId, g.Key.HeritageId, Answered = g.Count(), Correct = g.Count(x => x.IsCorrect) })
            .ToListAsync(ct);
        var perType = await db.FinishedAnswers(classId)
            .GroupBy(a => a.Question!.QuestionType)
            .Select(g => new { Type = g.Key, Answered = g.Count(), Correct = g.Count(x => x.IsCorrect) })
            .ToListAsync(ct);

        var info = await HeritageInfoAsync(perStudent.Select(x => x.HeritageId).Distinct().ToList(), ct);
        var perHeritage = perStudent.GroupBy(x => x.HeritageId)
            .Select(g => (HeritageId: g.Key, Answered: g.Sum(x => x.Answered), Correct: g.Sum(x => x.Correct)))
            .ToList();

        var filtered = perHeritage
            .Where(h => info.TryGetValue(h.HeritageId, out var i)
                        && (provinceId is null || i.ProvinceId == provinceId) && (heritageType is null || i.Type == heritageType))
            .ToList();
        var byHeritage = filtered
            .Select(h => StatsMath.Mastery(h.HeritageId, info[h.HeritageId].Name, h.Answered, h.Correct))
            .OrderBy(r => r.RatePercent).ThenBy(r => r.Name).ThenBy(r => r.Id)
            .ToList();

        var byProvince = AggregateByProvince(perHeritage, info);
        var byType = AggregateByType(perHeritage, info);
        var byQuestionType = perType
            .Select(t => (Type: t.Type ?? QuestionType.SINGLE_CHOICE, t.Answered, t.Correct))
            .OrderBy(t => t.Type)
            .Select(t => StatsMath.Topic(null, t.Type, QuestionTypes.NameOf(t.Type), t.Answered, t.Correct))
            .ToList();

        var heatmapHeritages = filtered.OrderByDescending(h => h.Answered).ThenBy(h => h.HeritageId).Take(HeatmapHeritageLimit).Select(h => h.HeritageId).ToHashSet();
        var cells = perStudent.Where(x => heatmapHeritages.Contains(x.HeritageId))
            .OrderBy(x => x.StudentId).ThenBy(x => x.HeritageId)
            .Select(x => new HeatmapCellDto(x.StudentId, x.HeritageId, x.Answered, x.Correct, AttemptRules.RatePercent(x.Correct, x.Answered), AttemptRules.MasteryOf(x.Correct, x.Answered)))
            .ToList();

        return new ClassMasteryDto(
            classId, schoolClass.Name, provinceId, heritageType, byHeritage, byProvince, byType, byQuestionType, students, cells,
            perStudent.Count == 0 ? Messages.ClassNoResultsYet : null);
    }

    // ---- Hồ sơ học tập của một học sinh ----

    public async Task<StudentProfileDto> StudentProfileAsync(Actor actor, long classId, long studentId, CancellationToken ct)
    {
        const string action = "xem hồ sơ học tập";
        var schoolClass = await db.FindClassAsync(actor, classId, action, false, ct);
        var level = SchoolClass.SchoolLevelOf(schoolClass.Grade);
        var student = await db.Students.AsNoTracking()
            .Where(s => s.Id == studentId && s.ClassId == classId && s.DeletedAt == null)
            .Select(s => new { s.Id, s.StudentCode, s.FullName, s.IsActive, LastLoginAt = s.User!.LastLoginAt })
            .SingleOrDefaultAsync(ct)
            ?? throw AppException.NotFound(Messages.NotFound(action, "học sinh của lớp này"));

        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.StudentId == studentId && a.Status != AttemptStatus.IN_PROGRESS && a.Quiz!.DeletedAt == null
                        && a.Quiz.Lesson!.DeletedAt == null && a.Quiz.Lesson.ClassId == classId)
            .OrderBy(a => a.SubmittedAt).ThenBy(a => a.Id)
            .Select(a => new
            {
                a.Id, a.QuizId, QuizName = a.Quiz!.Name, a.Quiz.LessonId, LessonTitle = a.Quiz.Lesson!.Title, a.Status, a.SubmittedAt, a.Score, a.TotalPoint,
            })
            .ToListAsync(ct);

        var history = attempts.AsEnumerable().Reverse().Select(a =>
        {
            var score = a.Score ?? 0m;
            var total = a.TotalPoint ?? 0m;
            return new StudentHistoryRowDto(
                a.Id, a.QuizId, a.QuizName, a.LessonId, a.LessonTitle, a.Status!.Value, AttemptRules.StatusLabel(a.Status), a.SubmittedAt, score, total,
                AttemptRules.Percent(score, total), level == 1 ? AttemptRules.Stars(score, total) : null,
                AttemptRules.AttemptBadges(a.Status, a.Score, a.TotalPoint).Select(AttemptResultService.Badge).ToList());
        }).ToList();
        var trend = attempts.Select(a => new ScorePointDto(a.Id, a.SubmittedAt, a.QuizName, AttemptRules.Percent(a.Score ?? 0m, a.TotalPoint ?? 0m))).ToList();

        // Bài học đã hoàn thành: em đã nộp mọi quiz đã phát hành của bài.
        var quizzes = await PublishedQuizzesAsync(classId, ct);
        var doneQuizzes = attempts.Select(a => a.QuizId).ToHashSet();
        var completed = quizzes.GroupBy(q => q.LessonId)
            .Where(g => g.All(q => doneQuizzes.Contains(q.Id)))
            .Select(g => new CompletedLessonDto(g.Key, g.First().LessonTitle, g.Count(), g.Count()))
            .OrderBy(l => l.Title).ThenBy(l => l.LessonId)
            .ToList();

        // Mức thành thạo theo di sản, tỉnh, loại di sản: qua mọi lượt đã nộp của em.
        var perHeritage = await db.AttemptAnswers.AsNoTracking()
            .Where(a => a.Attempt!.StudentId == studentId && a.Attempt.Status != AttemptStatus.IN_PROGRESS && a.Attempt.Quiz!.DeletedAt == null
                        && a.Attempt.Quiz.Lesson!.DeletedAt == null && a.Attempt.Quiz.Lesson.ClassId == classId)
            .GroupBy(a => a.Question!.HeritageId)
            .Select(g => new { HeritageId = g.Key, Answered = g.Count(), Correct = g.Count(x => x.IsCorrect) })
            .ToListAsync(ct);
        var info = await HeritageInfoAsync(perHeritage.Select(x => x.HeritageId).ToList(), ct);
        var rows = perHeritage.Select(x => (x.HeritageId, x.Answered, x.Correct)).ToList();
        var byHeritage = rows.Select(h => StatsMath.Mastery(h.HeritageId, info.GetValueOrDefault(h.HeritageId)?.Name, h.Answered, h.Correct))
            .OrderBy(r => r.Name).ThenBy(r => r.Id).ToList();

        var finishedTuples = attempts.Select(a => (a.Status, a.Score, a.TotalPoint)).ToList();
        return new StudentProfileDto(
            student.Id, student.StudentCode, student.FullName, classId, schoolClass.Name, level, student.IsActive, student.LastLoginAt,
            doneQuizzes.Count(id => quizzes.Any(q => q.Id == id)), quizzes.Count, attempts.Count,
            AveragePercent(attempts.Select(a => (decimal?)AttemptRules.Percent(a.Score ?? 0m, a.TotalPoint ?? 0m)).ToList()),
            level == 1 ? attempts.Sum(a => AttemptRules.Stars(a.Score ?? 0m, a.TotalPoint ?? 0m)) : null,
            history, trend, completed, byHeritage, AggregateByProvince(rows, info), AggregateByType(rows, info), BadgeCounts(finishedTuples),
            attempts.Count == 0 ? "Học sinh này chưa nộp bài kiểm tra nào." : null);
    }

    // ---- Dùng chung ----

    private sealed record HeritageInfo(string? Name, int? ProvinceId, string? ProvinceName, HeritageType? Type);

    private async Task<Dictionary<long, HeritageInfo>> HeritageInfoAsync(List<long> ids, CancellationToken ct) =>
        await db.Heritages.AsNoTracking().Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.Name, h.ProvinceId, ProvinceName = h.Province == null ? null : h.Province.Name, h.HeritageType })
            .ToDictionaryAsync(h => h.Id, h => new HeritageInfo(h.Name, h.ProvinceId, h.ProvinceName, h.HeritageType), ct);

    private static List<MasteryRowDto> AggregateByProvince(IEnumerable<(long HeritageId, int Answered, int Correct)> rows, Dictionary<long, HeritageInfo> info) =>
        rows.Where(r => info.ContainsKey(r.HeritageId))
            .GroupBy(r => info[r.HeritageId].ProvinceId)
            .Select(g => StatsMath.Mastery(g.Key, g.Key is null ? NoProvince : info[g.First().HeritageId].ProvinceName, g.Sum(x => x.Answered), g.Sum(x => x.Correct)))
            .OrderBy(r => r.RatePercent).ThenBy(r => r.Name).ToList();

    private static List<MasteryRowDto> AggregateByType(IEnumerable<(long HeritageId, int Answered, int Correct)> rows, Dictionary<long, HeritageInfo> info) =>
        rows.Where(r => info.ContainsKey(r.HeritageId) && info[r.HeritageId].Type is not null)
            .GroupBy(r => info[r.HeritageId].Type!.Value)
            .Select(g => StatsMath.Mastery((long)g.Key, HeritageLabels.Type(g.Key), g.Sum(x => x.Answered), g.Sum(x => x.Correct)))
            .OrderBy(r => r.RatePercent).ThenBy(r => r.Name).ToList();

    private static decimal? AveragePercent(List<AttemptRow> finished) =>
        finished.Count == 0 ? null : Math.Round(finished.Average(a => AttemptRules.Percent(a.Score ?? 0m, a.TotalPoint ?? 0m)), 1, MidpointRounding.AwayFromZero);

    private static decimal? AveragePercent(List<decimal?> percents) =>
        percents.Count == 0 ? null : Math.Round(percents.Average(p => p ?? 0m), 1, MidpointRounding.AwayFromZero);

    /// <summary>Huy hiệu đã đạt qua các lượt đã nộp: Điểm tuyệt đối, Xuất sắc, Nộp đúng hạn theo từng lượt; Kiên trì một lần khi có từ 5 lượt.</summary>
    private static List<BadgeCountDto> BadgeCounts(IReadOnlyList<(AttemptStatus? Status, decimal? Score, decimal? TotalPoint)> finished)
    {
        var counts = new List<BadgeCountDto>();
        foreach (var code in new[] { BadgeCode.PERFECT_SCORE, BadgeCode.EXCELLENT, BadgeCode.ON_TIME })
        {
            var n = finished.Count(a => AttemptRules.AttemptBadges(a.Status, a.Score, a.TotalPoint).Contains(code));
            if (n > 0) counts.Add(new BadgeCountDto(code, AttemptRules.BadgeName(code), n));
        }
        if (AttemptRules.HasPerseverance(finished.Count)) counts.Add(new BadgeCountDto(BadgeCode.PERSEVERANCE, AttemptRules.BadgeName(BadgeCode.PERSEVERANCE), 1));
        return counts;
    }
}
