using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Questions;

/// <summary>
/// Đọc các dòng <c>answers</c> của một câu thành cấu trúc theo dạng (cùng khuôn với <see cref="SaveQuestionRequest"/>) và ngược lại:
/// nguồn chung cho chi tiết câu, xem trước, nhân bản và sửa câu.
/// </summary>
public sealed record QuestionShape(
    bool? AnswerIsTrue,
    IReadOnlyList<QuestionOption> Options,
    IReadOnlyList<QuestionPair> Pairs,
    IReadOnlyList<string> Steps,
    IReadOnlyList<QuestionBlank> Blanks,
    IReadOnlyList<string> Distractors,
    IReadOnlyList<string> WordBank,
    IReadOnlyList<QuestionStatement> Statements)
{
    public static QuestionShape Of(Question q)
    {
        var rows = q.Answers.OrderBy(a => a.SortOrder ?? int.MaxValue).ThenBy(a => a.Id).ToList();
        var type = q.QuestionType ?? QuestionType.SINGLE_CHOICE;

        bool? answerIsTrue = type == QuestionType.TRUE_FALSE ? rows.FirstOrDefault(r => r.IsCorrect)?.Content == QuestionRules.TrueFalseOptions[0] : null;

        IReadOnlyList<QuestionOption> options = type is QuestionType.SINGLE_CHOICE or QuestionType.IMAGE_CHOICE or QuestionType.SOURCE_READING or QuestionType.TRUE_FALSE
            ? rows.Select(r => new QuestionOption(r.Content, r.ImageUrl, r.IsCorrect)).ToList()
            : [];

        IReadOnlyList<QuestionPair> pairs = type == QuestionType.MATCHING
            ? rows.Select(r => new QuestionPair(r.Content, r.ImageUrl, r.MatchContent)).ToList()
            : [];

        IReadOnlyList<string> steps = type == QuestionType.ORDERING
            ? rows.OrderBy(r => r.CorrectPosition ?? int.MaxValue).Select(r => r.Content ?? string.Empty).ToList()
            : [];

        var accepted = type == QuestionType.FILL_BLANK ? rows.Where(r => r.BlankIndex is not null && r.IsCorrect).ToList() : new List<Answer>();
        IReadOnlyList<QuestionBlank> blanks = accepted.GroupBy(r => r.BlankIndex!.Value).OrderBy(g => g.Key)
            .Select(g => new QuestionBlank(g.Select(r => r.Content ?? string.Empty).ToList())).ToList();
        IReadOnlyList<string> distractors = type == QuestionType.FILL_BLANK
            ? rows.Where(r => r.BlankIndex is null && !r.IsCorrect).Select(r => r.Content ?? string.Empty).ToList()
            : [];
        IReadOnlyList<string> bank = distractors.Count == 0
            ? []
            : blanks.Select(b => b.Accepted![0]).Concat(distractors).ToList();

        IReadOnlyList<QuestionStatement> statements = type == QuestionType.MULTI_TRUE_FALSE
            ? rows.Select(r => new QuestionStatement(r.Content, r.IsCorrect)).ToList()
            : [];

        return new QuestionShape(answerIsTrue, options, pairs, steps, blanks, distractors, bank, statements);
    }

    /// <summary>Nội dung đầy đủ của câu dưới dạng yêu cầu lưu (để nhân bản, hoặc kiểm lại câu cũ theo quy tắc hiện hành).</summary>
    public static SaveQuestionRequest ToRequest(Question q)
    {
        var shape = Of(q);
        return new SaveQuestionRequest(
            q.QuestionType, q.HeritageId, q.Content, q.Explanation, q.DifficultyLevel, q.LevelMin, q.LevelMax, q.ImageUrl, q.Passage,
            q.PassageSource, shape.AnswerIsTrue,
            q.QuestionType == QuestionType.TRUE_FALSE ? null : shape.Options.ToList(),
            shape.Pairs.ToList(), shape.Steps.ToList(), shape.Blanks.ToList(), shape.Distractors.ToList(), shape.Statements.ToList());
    }

    /// <summary>Đường dẫn mọi ảnh câu đang dùng (hình của câu, thẻ hình, vế trái hình).</summary>
    public static IReadOnlyList<string> ImageUrlsOf(Question q) =>
        new[] { q.ImageUrl }.Concat(q.Answers.Select(a => a.ImageUrl)).Where(u => !string.IsNullOrEmpty(u)).Select(u => u!).Distinct().ToList();
}

/// <summary>Dựng cách học sinh của một cấp thấy câu hỏi (xem trước): không có đáp án đúng, các cột và kho từ đã xáo ổn định.</summary>
public static class QuestionPreviewBuilder
{
    public static QuestionPreviewDto Build(Question q, int level)
    {
        var type = q.QuestionType ?? QuestionType.SINGLE_CHOICE;
        var rows = q.Answers.OrderBy(a => a.SortOrder ?? int.MaxValue).ThenBy(a => a.Id).ToList();
        var shape = QuestionShape.Of(q);
        var random = new Random(unchecked((int)(q.Id * 7919 + level * 104_729 + rows.Count)));

        IReadOnlyList<QuestionPreviewOptionDto> options = type is QuestionType.SINGLE_CHOICE or QuestionType.IMAGE_CHOICE
            or QuestionType.SOURCE_READING or QuestionType.TRUE_FALSE
            ? rows.Select(r => new QuestionPreviewOptionDto(r.Content, r.ImageUrl)).ToList()
            : [];
        // Thẻ hình hiện theo thứ tự lưu cho đọc ổn định; Đúng/Sai luôn Đúng rồi Sai.
        IReadOnlyList<QuestionPreviewOptionDto> lefts = type == QuestionType.MATCHING
            ? Shuffle(rows.Select(r => new QuestionPreviewOptionDto(r.Content, r.ImageUrl)).ToList(), random)
            : [];
        IReadOnlyList<string> rights = type == QuestionType.MATCHING
            ? Shuffle(rows.Select(r => r.MatchContent ?? string.Empty).ToList(), random)
            : [];
        IReadOnlyList<string> items = type == QuestionType.ORDERING ? ShuffleOrdering(shape.Steps, random) : [];
        var blankCount = type == QuestionType.FILL_BLANK ? shape.Blanks.Count : 0;

        return new QuestionPreviewDto(
            level, LayoutOf(level), level == 1, type, QuestionTypes.NameOf(type), q.Content ?? string.Empty, q.ImageUrl, q.Passage, q.PassageSource,
            options, lefts, rights, items, blankCount, Shuffle(shape.WordBank.ToList(), random), shape.Statements.Select(s => s.Text ?? string.Empty).ToList());
    }

    /// <summary>Cấp 1 thẻ lớn và đọc to lời câu; cấp 2 chuẩn; cấp 3 gọn, tối ưu đọc nhiều chữ.</summary>
    public static string LayoutOf(int level) => level switch { 1 => "LARGE_CARDS", 2 => "STANDARD", _ => "COMPACT" };

    private static List<T> Shuffle<T>(List<T> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    /// <summary>Xáo các mục sắp xếp, không để trùng thứ tự đúng (nếu có nhiều hơn một mục).</summary>
    private static List<string> ShuffleOrdering(IReadOnlyList<string> correct, Random random)
    {
        var items = correct.ToList();
        if (items.Count < 2) return items;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Shuffle(items, random);
            if (!items.SequenceEqual(correct)) return items;
        }
        items.Reverse();
        return items;
    }
}
