using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Attempts;

/// <summary>Dựng câu như em thấy trong lượt làm bài và phần xem lại sau khi nộp.</summary>
internal static class AttemptViews
{
    /// <summary>
    /// Câu hiện cho em: không có đáp án đúng; thứ tự các thẻ, cột và mục theo cách xáo của lượt. <paramref name="point"/> null ở cấp 1.
    /// </summary>
    public static StudentAttemptQuestionDto Question(QuestionLayout l, int position, decimal? point, QuestionSubmission? shownAnswer)
    {
        var q = l.Question;
        var rows = l.Rows;
        IReadOnlyList<QuestionPreviewOptionDto> options = l.UsesOptions
            ? l.OptionOrder.Select(i => new QuestionPreviewOptionDto(rows[i].Content, rows[i].ImageUrl)).ToList()
            : [];
        IReadOnlyList<QuestionPreviewOptionDto> lefts = l.Type == QuestionType.MATCHING
            ? l.LeftOrder.Select(i => new QuestionPreviewOptionDto(rows[i].Content, rows[i].ImageUrl)).ToList()
            : [];
        IReadOnlyList<string> rights = l.Type == QuestionType.MATCHING
            ? l.RightOrder.Select(i => rows[i].MatchContent ?? string.Empty).ToList()
            : [];
        IReadOnlyList<string> items = l.Type == QuestionType.ORDERING
            ? l.ItemOrder.Select(i => rows[i].Content ?? string.Empty).ToList()
            : [];
        IReadOnlyList<string> statements = l.Type == QuestionType.MULTI_TRUE_FALSE
            ? rows.Select(r => r.Content ?? string.Empty).ToList()
            : [];

        return new StudentAttemptQuestionDto(
            q.Id, position, point, l.Type, QuestionTypes.NameOf(l.Type), q.Content ?? string.Empty, q.ImageUrl, q.Passage, q.PassageSource,
            options, lefts, rights, items, l.BlankCount, l.WordBank, statements, Math.Max(1, QuestionGrader.PartsTotal(l.Type, rows)),
            shownAnswer is not null, shownAnswer);
    }

    /// <summary>Đáp án đúng của câu theo cách em thấy (vị trí trên màn hình). Điền từ lấy cách viết đúng đầu tiên của mỗi chỗ trống.</summary>
    public static QuestionSubmission CorrectAnswer(QuestionLayout l) =>
        AttemptAnswerCodec.ToShown(l, QuestionGrader.AnswerKey(l.Type, l.Rows)) ?? new QuestionSubmission();

    /// <summary>Hết hạn nhận bài của một lượt đang làm tại <paramref name="now"/> (hạn nộp cộng ân hạn).</summary>
    public static bool IsOverdue(Attempt attempt, Quiz quiz, int level, DateTime now) =>
        attempt.Status == AttemptStatus.IN_PROGRESS && AttemptRules.IsOverdue(AttemptFinisher.DeadlineOf(attempt, quiz, level), now);

    public static int? RemainingSeconds(DateTime? deadline, DateTime now) =>
        deadline is { } d ? (int)Math.Max(0, Math.Ceiling((d - now).TotalSeconds)) : null;
}
