using DSVHVN.Application.Common;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Attempts;

/// <summary>
/// Kết quả một lượt đã nộp, trình bày theo cấp hiện tại của lớp (tính khi hiển thị từ điểm đã chấm, không lưu thêm gì).
/// <list type="bullet">
/// <item>Cấp 1: số sao 1-3 (từ 80% là 3 sao, từ 50% là 2 sao, còn lại 1 sao), huy hiệu và lời khen nói đúng điều em nhớ (số câu đúng ngay lần đầu, tên di sản);
/// thêm các thẻ di sản của bài đã vào album, số thẻ em đã có và một câu mang về nhà; không điểm, không phần trăm, không số câu đúng sai.</item>
/// <item>Cấp 2: điểm, số câu đúng, sai, bỏ trống, thời gian làm, huy hiệu, và đáp án cùng giải thích từng câu khi bài cho xem đáp án.</item>
/// <item>Cấp 3: như cấp 2, thêm phân tích tỉ lệ đúng theo di sản (chủ đề) và theo dạng câu, các di sản nên ôn.</item>
/// </list>
/// Em chỉ xem lượt của chính mình (lượt của em khác như không tồn tại), kể cả khi bài đã đóng hay bài học đã lưu trữ.
/// </summary>
public sealed class AttemptResultService(IAppDbContext db, AttemptLifecycle lifecycle, AlbumService albums)
{
    internal async Task<StudentAttemptResultDto> GetAsync(StudentContext student, long attemptId, CancellationToken ct)
    {
        var status = await db.Attempts.AsNoTracking().Where(a => a.Id == attemptId && a.StudentId == student.Id).Select(a => a.Status).SingleOrDefaultAsync(ct);
        if (status is null) throw AppException.NotFound(Messages.NotFound("xem kết quả", "bài làm của em"));
        if (status == AttemptStatus.IN_PROGRESS)
        {
            // Quá hạn nhận bài mà chưa ai nộp thì chốt ngay để em xem được kết quả.
            if (!await lifecycle.ExpireIfOverdueAsync(attemptId, student.Level, ct)) throw new AppException(409, Messages.AttemptInProgress);
        }

        var attempt = await db.Attempts.AsNoTracking().Include(a => a.Quiz).ThenInclude(q => q!.Lesson).SingleAsync(a => a.Id == attemptId, ct);
        var quiz = attempt.Quiz!;
        var items = await AttemptFinisher.LoadQuizQuestionsAsync(db, quiz.Id, ct);
        var answers = await AttemptFinisher.LoadAnswersAsync(db, attemptId, ct);
        var heritageIds = items.Select(i => i.Question!.HeritageId).Distinct().ToList();
        var heritageNames = await db.Heritages.AsNoTracking().Where(h => heritageIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name ?? string.Empty, ct);
        var finished = await db.Attempts.CountAsync(a => a.StudentId == student.Id && a.Status != AttemptStatus.IN_PROGRESS, ct);

        var level = student.Level;
        var score = attempt.Score ?? 0m;
        var total = attempt.TotalPoint ?? 0m;
        var layouts = items.ToDictionary(i => i.QuestionId, i => QuestionLayout.Build(i.Question!, attempt.Id));
        var rows = items.ToDictionary(i => i.QuestionId, i => answers.FirstOrDefault(a => a.QuestionId == i.QuestionId));

        var blank = items.Count(i => AttemptAnswerCodec.ToStored(layouts[i.QuestionId], rows[i.QuestionId]) is null);
        var correct = answers.Count(a => a.IsCorrect);
        var wrong = Math.Max(0, items.Count - correct - blank);

        var badgeCodes = AttemptRules.AttemptBadges(attempt.Status, attempt.Score, attempt.TotalPoint).ToList();
        if (AttemptRules.HasPerseverance(finished)) badgeCodes.Add(BadgeCode.PERSEVERANCE);
        var badges = badgeCodes.Select(Badge).ToList();

        int? stars = level == 1 ? AttemptRules.Stars(score, total) : null;
        var showAnswers = quiz.ShowAnswersAfterSubmit;

        List<StudentQuestionReviewDto>? review = null;
        if (showAnswers)
        {
            review = [];
            var ordered = QuestionLayout.OrderQuestions(items, quiz.ShuffleQuestions, attempt.Id);
            for (var i = 0; i < ordered.Count; i++)
            {
                var item = ordered[i];
                var layout = layouts[item.QuestionId];
                var row = rows[item.QuestionId];
                var shown = AttemptAnswerCodec.ToShown(layout, AttemptAnswerCodec.ToStored(layout, row));
                var view = AttemptViews.Question(layout, i + 1, level == 1 ? null : item.Point, shown);
                review.Add(new StudentQuestionReviewDto(
                    view, AttemptViews.CorrectAnswer(layout), item.Question!.Explanation,
                    level == 1 ? null : row?.IsCorrect ?? false,
                    level == 1 ? null : row?.PartsCorrect,
                    level == 1 ? null : row?.PartsTotal,
                    level == 1 ? null : row?.PointEarned ?? 0m));
            }
        }

        // Cấp 1: lời khen cụ thể theo lần trả lời đầu tiên của từng câu (câu đã lưu là lần đầu), thẻ di sản của bài và câu mang về nhà.
        var firstTryCorrect = items.Count(i => rows[i.QuestionId]?.IsCorrect == true);
        string? praise = stars is { } starCount ? AttemptRules.Praise(starCount) : null;
        List<StudentAlbumCardDto>? cards = null;
        AlbumData? album = null;
        string? takeHome = null;
        if (level == 1)
        {
            praise = AttemptRules.Praise(stars!.Value, items
                .Select(i => new AttemptRules.PraiseQuestion(heritageNames.GetValueOrDefault(i.Question!.HeritageId), rows[i.QuestionId]?.IsCorrect == true)).ToList());
            album = await albums.LoadAsync(student, ct);
            cards = await albums.LessonCardsAsync(album, quiz.LessonId, quiz.Lesson?.Title, attempt.SubmittedAt, ct);
            takeHome = await albums.TakeHomeAsync(quiz.LessonId, ct);
        }

        return new StudentAttemptResultDto(
            attempt.Id, quiz.Id, quiz.Name, quiz.LessonId, quiz.Lesson?.Title, level, student.LevelName,
            level switch { 1 => "STARS", 2 => "SCORE", _ => "ANALYSIS" },
            attempt.Status!.Value, AttemptRules.StatusLabel(attempt.Status), attempt.StartedAt, attempt.SubmittedAt, attempt.DurationSeconds,
            stars, praise,
            level == 1 ? null : score, level == 1 ? null : total, level == 1 ? null : AttemptRules.Percent(score, total),
            level == 1 ? null : correct, level == 1 ? null : wrong, level == 1 ? null : blank, items.Count,
            badges, showAnswers, review, level == 3 ? Analysis(items, answers, heritageNames) : null,
            level == 1 ? firstTryCorrect : null, cards, album?.Collected, album?.Total, takeHome);
    }

    public static StudentBadgeDto Badge(BadgeCode code) => new(code, AttemptRules.BadgeName(code), AttemptRules.BadgeDescription(code));

    /// <summary>Tỉ lệ đúng của lượt theo di sản và theo dạng câu; di sản có tỉ lệ đúng dưới 50% là nên ôn.</summary>
    private static StudentAnalysisDto Analysis(List<QuizQuestion> items, List<AttemptAnswer> answers, Dictionary<long, string> heritageNames)
    {
        var joined = items.Select(i => (Item: i, Row: answers.FirstOrDefault(a => a.QuestionId == i.QuestionId))).ToList();

        var byHeritage = joined.GroupBy(x => x.Item.Question!.HeritageId)
            .Select(g => Topic(g.Key, null, heritageNames.GetValueOrDefault(g.Key, string.Empty), g.Count(), g.Count(x => x.Row?.IsCorrect == true)))
            .OrderBy(t => t.RatePercent).ThenBy(t => t.Name).ToList();
        var byType = joined.GroupBy(x => x.Item.Question!.QuestionType ?? QuestionType.SINGLE_CHOICE)
            .Select(g => Topic(null, g.Key, QuestionTypes.NameOf(g.Key), g.Count(), g.Count(x => x.Row?.IsCorrect == true)))
            .OrderBy(t => t.QuestionType).ToList();
        var review = byHeritage.Where(t => t.RatePercent < AttemptRules.MidRatio * 100m).ToList();

        var multi = joined.Where(x => QuestionTypes.Of(x.Item.Question!.QuestionType ?? QuestionType.SINGLE_CHOICE).MultiPart && x.Row is not null).ToList();
        return new StudentAnalysisDto(byHeritage, byType, review, multi.Sum(x => (int)x.Row!.PartsCorrect), multi.Sum(x => (int)x.Row!.PartsTotal));
    }

    private static StudentTopicDto Topic(long? heritageId, QuestionType? type, string name, int answered, int correct) =>
        new(heritageId, type, name, answered, correct, AttemptRules.RatePercent(correct, answered));
}
