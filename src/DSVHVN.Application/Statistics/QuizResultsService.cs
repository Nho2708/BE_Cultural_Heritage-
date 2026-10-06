using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Questions;
using DSVHVN.Application.Quizzes;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Statistics;

/// <summary>
/// Kết quả một bài kiểm tra của lớp cho giáo viên phụ trách (các tab Tổng quan, Từng câu, Từng học sinh, chủ đề, và chi tiết bài làm). Chỉ tính lượt đã nộp
/// (đã nộp hoặc hết giờ tự nộp) của học sinh đang hoạt động; giáo viên luôn thấy điểm số ở mọi cấp. Bài kiểm tra của giáo viên khác hoặc của trường khác
/// như không tồn tại (404). Mọi số liệu tính từ <c>attempts</c>, <c>attempt_answers</c> và <c>attempt_answer_parts</c>, không lưu gì thêm.
/// </summary>
public sealed class QuizResultsService(IAppDbContext db)
{
    private const string Action = "xem kết quả bài kiểm tra";
    private const int CommonErrorLimit = 3;
    private const string BlankLabel = "Bỏ trống";

    private sealed record AttemptRow(
        long Id, long StudentId, AttemptStatus? Status, DateTime? StartedAt, DateTime? SubmittedAt, int? DurationSeconds,
        decimal? Score, decimal? TotalPoint, int? CorrectCount, int? TotalQuestions);

    private async Task<Quiz> LoadQuizAsync(Actor actor, long quizId, CancellationToken ct) =>
        await db.QuizzesOf(actor).AsNoTracking().Include(q => q.Lesson).ThenInclude(l => l!.Class)
            .SingleOrDefaultAsync(q => q.Id == quizId, ct)
        ?? throw AppException.NotFound(Messages.NotFound(Action, "bài kiểm tra của bạn"));

    private async Task<List<AttemptRow>> LoadAttemptsAsync(long quizId, CancellationToken ct) =>
        await db.Attempts.AsNoTracking()
            .Where(a => a.QuizId == quizId && a.Student!.DeletedAt == null && a.Student.IsActive)
            .OrderBy(a => a.Id)
            .Select(a => new AttemptRow(a.Id, a.StudentId, a.Status, a.StartedAt, a.SubmittedAt, a.DurationSeconds, a.Score, a.TotalPoint, a.CorrectCount, a.TotalQuestions))
            .ToListAsync(ct);

    private static bool IsFinished(AttemptRow a) => a.Status is AttemptStatus.SUBMITTED or AttemptStatus.EXPIRED;

    // ---- Tổng quan ----

    public async Task<QuizResultsOverviewDto> OverviewAsync(Actor actor, long quizId, CancellationToken ct)
    {
        var quiz = await LoadQuizAsync(actor, quizId, ct);
        var schoolClass = quiz.Lesson!.Class!;
        var level = SchoolClass.SchoolLevelOf(schoolClass.Grade);
        var enrolled = await db.ActiveStudents(schoolClass.Id).CountAsync(ct);
        var attempts = await LoadAttemptsAsync(quizId, ct);
        var questionCount = await db.QuizQuestions.CountAsync(x => x.QuizId == quizId, ct);

        var finished = attempts.Where(IsFinished).ToList();
        var submitted = finished.Count(a => a.Status == AttemptStatus.SUBMITTED);
        var expired = finished.Count - submitted;
        var inProgress = attempts.Count(a => a.Status == AttemptStatus.IN_PROGRESS);
        var notStarted = Math.Max(0, enrolled - attempts.Count);

        var percents = finished.Select(a => AttemptRules.Percent(a.Score ?? 0m, a.TotalPoint ?? 0m)).ToList();
        decimal? avgScore = null, maxScore = null, minScore = null, avgPercent = null, maxPercent = null, minPercent = null;
        if (finished.Count > 0)
        {
            var scores = finished.Select(a => a.Score ?? 0m).ToList();
            avgScore = StatsMath.Round2(scores.Average());
            maxScore = scores.Max();
            minScore = scores.Min();
            avgPercent = Math.Round(percents.Average(), 1, MidpointRounding.AwayFromZero);
            maxPercent = percents.Max();
            minPercent = percents.Min();
        }

        return new QuizResultsOverviewDto(
            quiz.Id, quiz.Name, quiz.Status, quiz.LessonId, quiz.Lesson.Title, schoolClass.Id, schoolClass.Name, level, SchoolLevels.NameOf(level),
            quiz.TotalPoint, questionCount, enrolled, submitted, expired, finished.Count, inProgress, notStarted,
            avgScore, maxScore, minScore, avgPercent, maxPercent, minPercent, StatsMath.Distribution(percents),
            finished.Count == 0 ? Messages.ClassNoResultsYet : null);
    }

    // ---- Từng học sinh ----

    public async Task<QuizStudentsDto> StudentsAsync(Actor actor, long quizId, CancellationToken ct)
    {
        var quiz = await LoadQuizAsync(actor, quizId, ct);
        var schoolClass = quiz.Lesson!.Class!;
        var level = SchoolClass.SchoolLevelOf(schoolClass.Grade);
        var students = await db.ActiveStudents(schoolClass.Id).AsNoTracking()
            .OrderBy(s => s.StudentCode).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.StudentCode, s.FullName })
            .ToListAsync(ct);
        var attempts = (await LoadAttemptsAsync(quizId, ct)).ToDictionary(a => a.StudentId);

        var rows = students.Select(s =>
        {
            if (!attempts.TryGetValue(s.Id, out var a))
                return new QuizStudentRowDto(s.Id, s.StudentCode, s.FullName, QuizStudentStatus.NOT_STARTED, StatsMath.StudentStatusLabel(QuizStudentStatus.NOT_STARTED),
                    null, null, null, null, null, null, null, null, null, null, []);
            var status = StatsMath.StatusOf(a.Status);
            var done = IsFinished(a);
            var score = a.Score ?? 0m;
            var total = a.TotalPoint ?? 0m;
            return new QuizStudentRowDto(
                s.Id, s.StudentCode, s.FullName, status, StatsMath.StudentStatusLabel(status), a.Id, a.StartedAt, a.SubmittedAt, a.DurationSeconds,
                done ? score : null, done ? total : null, done ? AttemptRules.Percent(score, total) : null,
                done && level == 1 ? AttemptRules.Stars(score, total) : null, done ? a.CorrectCount : null, done ? a.TotalQuestions : null,
                done ? AttemptRules.AttemptBadges(a.Status, a.Score, a.TotalPoint).Select(AttemptResultService.Badge).ToList() : []);
        }).ToList();
        return new QuizStudentsDto(quiz.Id, quiz.Name, level, rows);
    }

    // ---- Từng câu ----

    public async Task<QuizQuestionStatsDto> QuestionsAsync(Actor actor, long quizId, CancellationToken ct)
    {
        var quiz = await LoadQuizAsync(actor, quizId, ct);
        var finished = (await LoadAttemptsAsync(quizId, ct)).Count(IsFinished);
        var data = await LoadQuestionDataAsync(quizId, ct);

        var stats = new List<QuestionStatDto>();
        for (var i = 0; i < data.Items.Count; i++)
        {
            var item = data.Items[i];
            stats.Add(BuildQuestionStat(item, i + 1, data));
        }

        var byType = data.Items
            .GroupBy(x => x.Question!.QuestionType ?? QuestionType.SINGLE_CHOICE)
            .OrderBy(g => g.Key)
            .Select(g => StatsMath.Topic(null, g.Key, QuestionTypes.NameOf(g.Key),
                g.Sum(x => data.AnswersOf(x.QuestionId).Count), g.Sum(x => data.AnswersOf(x.QuestionId).Count(a => a.IsCorrect))))
            .ToList();
        return new QuizQuestionStatsDto(quiz.Id, quiz.Name, finished, stats, byType, finished == 0 ? Messages.ClassNoResultsYet : null);
    }

    // ---- Chủ đề ----

    public async Task<QuizTopicStatsDto> TopicsAsync(Actor actor, long quizId, CancellationToken ct)
    {
        var quiz = await LoadQuizAsync(actor, quizId, ct);
        var finished = (await LoadAttemptsAsync(quizId, ct)).Count(IsFinished);
        var data = await LoadQuestionDataAsync(quizId, ct);

        var byHeritage = data.Items
            .GroupBy(x => x.Question!.HeritageId)
            .Select(g => StatsMath.Topic(g.Key, null, data.HeritageNames.GetValueOrDefault(g.Key) ?? string.Empty,
                g.Sum(x => data.AnswersOf(x.QuestionId).Count), g.Sum(x => data.AnswersOf(x.QuestionId).Count(a => a.IsCorrect))))
            .OrderBy(t => t.RatePercent).ThenBy(t => t.Name).ThenBy(t => t.HeritageId)
            .ToList();
        var byType = data.Items
            .GroupBy(x => x.Question!.QuestionType ?? QuestionType.SINGLE_CHOICE)
            .OrderBy(g => g.Key)
            .Select(g => StatsMath.Topic(null, g.Key, QuestionTypes.NameOf(g.Key),
                g.Sum(x => data.AnswersOf(x.QuestionId).Count), g.Sum(x => data.AnswersOf(x.QuestionId).Count(a => a.IsCorrect))))
            .ToList();
        return new QuizTopicStatsDto(quiz.Id, quiz.Name, finished, byHeritage, byType, finished == 0 ? Messages.ClassNoResultsYet : null);
    }

    // ---- Chi tiết bài làm ----

    public async Task<TeacherAttemptDetailDto> AttemptDetailAsync(Actor actor, long attemptId, CancellationToken ct)
    {
        const string action = "xem chi tiết bài làm";
        var attempt = await db.Attempts.AsNoTracking()
            .Include(a => a.Student)
            .Include(a => a.Quiz).ThenInclude(q => q!.Lesson).ThenInclude(l => l!.Class)
            .FirstOrDefaultAsync(a => a.Id == attemptId && db.QuizzesOf(actor).Any(q => q.Id == a.QuizId), ct);
        if (attempt?.Student is null || attempt.Student.DeletedAt is not null) throw AppException.NotFound(Messages.NotFound(action, "bài làm của lớp bạn"));
        if (attempt.Status == AttemptStatus.IN_PROGRESS) throw new AppException(409, Messages.Blocked(action, "Học sinh chưa nộp bài này."));

        var quiz = attempt.Quiz!;
        var schoolClass = quiz.Lesson!.Class!;
        var level = SchoolClass.SchoolLevelOf(schoolClass.Grade);
        var items = await AttemptFinisher.LoadQuizQuestionsAsync(db, quiz.Id, ct);
        var answers = await AttemptFinisher.LoadAnswersAsync(db, attemptId, ct);
        var layouts = items.ToDictionary(i => i.QuestionId, i => QuestionLayout.Build(i.Question!, attempt.Id));
        var rows = items.ToDictionary(i => i.QuestionId, i => answers.FirstOrDefault(a => a.QuestionId == i.QuestionId));

        var review = new List<StudentQuestionReviewDto>();
        var ordered = QuestionLayout.OrderQuestions(items, quiz.ShuffleQuestions, attempt.Id);
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var layout = layouts[item.QuestionId];
            var row = rows[item.QuestionId];
            var shown = AttemptAnswerCodec.ToShown(layout, AttemptAnswerCodec.ToStored(layout, row));
            review.Add(new StudentQuestionReviewDto(
                AttemptViews.Question(layout, i + 1, item.Point, shown), AttemptViews.CorrectAnswer(layout), item.Question!.Explanation,
                row?.IsCorrect ?? false, row?.PartsCorrect, row?.PartsTotal, row?.PointEarned ?? 0m));
        }

        var score = attempt.Score ?? 0m;
        var total = attempt.TotalPoint ?? 0m;
        return new TeacherAttemptDetailDto(
            attempt.Id, attempt.StudentId, attempt.Student.StudentCode, attempt.Student.FullName, quiz.Id, quiz.Name, quiz.LessonId, quiz.Lesson.Title,
            schoolClass.Name, level, attempt.Status!.Value, AttemptRules.StatusLabel(attempt.Status), attempt.StartedAt, attempt.SubmittedAt, attempt.DurationSeconds,
            score, total, AttemptRules.Percent(score, total), level == 1 ? AttemptRules.Stars(score, total) : null,
            attempt.CorrectCount ?? answers.Count(a => a.IsCorrect), attempt.TotalQuestions ?? items.Count,
            AttemptRules.AttemptBadges(attempt.Status, attempt.Score, attempt.TotalPoint).Select(AttemptResultService.Badge).ToList(), review);
    }

    // ---- Dữ liệu từng câu ----

    private sealed record AnswerRow(long QuestionId, long? AnswerId, bool IsCorrect);

    private sealed record PartRow(long QuestionId, long? AnswerId, byte? BlankIndex, bool IsCorrect, string? ResponseText, long? SelectedAnswerId, bool? SelectedBool);

    private sealed class QuestionData
    {
        public required List<QuizQuestion> Items { get; init; }
        public required ILookup<long, AnswerRow> Answers { get; init; }
        public required ILookup<long, PartRow> Parts { get; init; }
        public required Dictionary<long, string?> HeritageNames { get; init; }

        public List<AnswerRow> AnswersOf(long questionId) => Answers[questionId].ToList();
    }

    private async Task<QuestionData> LoadQuestionDataAsync(long quizId, CancellationToken ct)
    {
        var items = await AttemptFinisher.LoadQuizQuestionsAsync(db, quizId, ct);
        var answerRows = await db.AttemptAnswers.AsNoTracking()
            .Where(a => a.Attempt!.QuizId == quizId && a.Attempt.Status != AttemptStatus.IN_PROGRESS
                        && a.Attempt.Student!.DeletedAt == null && a.Attempt.Student.IsActive)
            .Select(a => new AnswerRow(a.QuestionId, a.AnswerId, a.IsCorrect))
            .ToListAsync(ct);
        var partRows = await db.AttemptAnswerParts.AsNoTracking()
            .Where(p => p.AttemptAnswer!.Attempt!.QuizId == quizId && p.AttemptAnswer.Attempt.Status != AttemptStatus.IN_PROGRESS
                        && p.AttemptAnswer.Attempt.Student!.DeletedAt == null && p.AttemptAnswer.Attempt.Student.IsActive)
            .Select(p => new PartRow(p.AttemptAnswer!.QuestionId, p.AnswerId, p.BlankIndex, p.IsCorrect, p.ResponseText, p.SelectedAnswerId, p.SelectedBool))
            .ToListAsync(ct);
        var heritageIds = items.Select(i => i.Question!.HeritageId).Distinct().ToList();
        var names = await db.Heritages.AsNoTracking().Where(h => heritageIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, ct);
        return new QuestionData
        {
            Items = items,
            Answers = answerRows.ToLookup(a => a.QuestionId),
            Parts = partRows.ToLookup(p => p.QuestionId),
            HeritageNames = names,
        };
    }

    private static QuestionStatDto BuildQuestionStat(QuizQuestion item, int position, QuestionData data)
    {
        var q = item.Question!;
        var type = q.QuestionType ?? QuestionType.SINGLE_CHOICE;
        var info = QuestionTypes.Of(type);
        var rows = data.AnswersOf(q.Id);
        var answered = rows.Count;
        var correct = rows.Count(r => r.IsCorrect);
        var ordered = q.Answers.OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToList();

        var parts = new List<PartStatDto>();
        var choices = new List<ChoiceStatDto>();
        var errors = new List<QuestionErrorDto>();
        ChoiceStatDto? mostChosen = null;

        if (!info.MultiPart)
        {
            foreach (var option in ordered)
            {
                var count = rows.Count(r => r.AnswerId == option.Id);
                choices.Add(new ChoiceStatDto(option.Id, option.Content, option.ImageUrl, option.IsCorrect, count, Share(count, answered)));
            }
            var blank = rows.Count(r => r.AnswerId is null);
            if (blank > 0) choices.Add(new ChoiceStatDto(null, null, null, false, blank, Share(blank, answered)));
            mostChosen = choices.Where(c => c.AnswerId is not null && c.Count > 0).OrderByDescending(c => c.Count).FirstOrDefault();
        }
        else
        {
            var partRows = data.Parts[q.Id].ToList();
            var wrongCommon = new Dictionary<string, string?>();
            if (type == QuestionType.FILL_BLANK)
            {
                foreach (var group in partRows.Where(p => p.BlankIndex is not null).GroupBy(p => p.BlankIndex!.Value).OrderBy(g => g.Key))
                {
                    var label = $"Chỗ trống {group.Key}";
                    parts.Add(new PartStatDto(label, group.Count(), group.Count(p => p.IsCorrect), AttemptRules.RatePercent(group.Count(p => p.IsCorrect), group.Count())));
                    wrongCommon[label] = Mode(group.Where(p => !p.IsCorrect).Select(p => string.IsNullOrWhiteSpace(p.ResponseText) ? BlankLabel : p.ResponseText!.Trim()));
                }
            }
            else
            {
                var index = 0;
                foreach (var answer in ordered)
                {
                    index++;
                    var group = partRows.Where(p => p.AnswerId == answer.Id).ToList();
                    var label = string.IsNullOrWhiteSpace(answer.Content) ? $"Hình {index}" : answer.Content!;
                    parts.Add(new PartStatDto(label, group.Count, group.Count(p => p.IsCorrect), AttemptRules.RatePercent(group.Count(p => p.IsCorrect), group.Count)));
                    var wrong = group.Where(p => !p.IsCorrect).ToList();
                    wrongCommon[label] = type switch
                    {
                        QuestionType.MATCHING => Mode(wrong.Select(p => p.SelectedAnswerId is { } id ? ordered.FirstOrDefault(a => a.Id == id)?.MatchContent ?? BlankLabel : BlankLabel)),
                        QuestionType.MULTI_TRUE_FALSE => Mode(wrong.Select(p => p.SelectedBool switch { true => "Em chọn Đúng", false => "Em chọn Sai", _ => BlankLabel })),
                        _ => null,
                    };
                }
            }
            errors = parts
                .Select(p => (Part: p, Wrong: p.Answered - p.Correct))
                .Where(x => x.Wrong > 0)
                .OrderByDescending(x => x.Wrong).ThenBy(x => x.Part.Label, StringComparer.Ordinal)
                .Take(CommonErrorLimit)
                .Select(x => new QuestionErrorDto(x.Part.Label, x.Wrong, Share(x.Wrong, x.Part.Answered), wrongCommon.GetValueOrDefault(x.Part.Label)))
                .ToList();
        }

        return new QuestionStatDto(
            q.Id, position, q.Content, type, info.Name, item.Point, q.HeritageId, data.HeritageNames.GetValueOrDefault(q.HeritageId),
            answered, correct, AttemptRules.RatePercent(correct, answered), parts, choices, mostChosen, errors);
    }

    private static decimal Share(int part, int whole) => AttemptRules.RatePercent(part, whole);

    /// <summary>Giá trị gặp nhiều nhất (hòa thì lấy theo thứ tự chữ); null khi không có giá trị nào.</summary>
    private static string? Mode(IEnumerable<string> values) =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault();
}
