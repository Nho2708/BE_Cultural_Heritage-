using System.Text.RegularExpressions;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Speech;

/// <summary>
/// Lời đọc to của bài chơi cấp 1, dựng đúng từng ký tự như giao diện web dựng: câu hỏi kèm phương án và lời hướng dẫn, lời phản hồi sau khi kiểm.
/// Máy chủ đặt tên tệp tiếng theo băm của chữ, nên chữ tạo trước phải trùng chữ em bấm nghe thì mới có ngay. Đổi câu chữ ở đây thì đổi cùng lúc ở web
/// (lời đọc câu hỏi và lời phản hồi của bài chơi cấp 1); hai bên giữ cùng một bộ câu mẫu trong kiểm thử.
/// </summary>
public static partial class LevelOneSpeech
{
    private static readonly string[] CorrectTitles = ["Đúng rồi!", "Giỏi quá!", "Chính xác!"];

    private const string RevealTitle = "Đáp án đúng ở đây nè!";

    /// <summary>Lời hướng dẫn ngắn dưới câu hỏi theo dạng câu.</summary>
    public static string Instruction(QuestionType type) => type switch
    {
        QuestionType.TRUE_FALSE => "Em chạm Đúng hoặc Sai.",
        QuestionType.IMAGE_CHOICE => "Em chạm vào hình đúng.",
        QuestionType.MATCHING => "Em chạm một hình, rồi chạm tên của hình đó.",
        QuestionType.ORDERING => "Em chạm các bước theo đúng thứ tự.",
        _ => "Em chọn câu trả lời nhé.",
    };

    /// <summary>
    /// Lời đọc của một câu: lời câu (chỗ trống đọc là "chỗ trống"), các phương án chữ của câu chọn một đáp án, tên các hình của ghép cặp hoặc các bước của
    /// sắp xếp theo thứ tự đang hiện, rồi lời hướng dẫn. Câu Đúng/Sai và chọn hình không đọc phương án. Mỗi phần thiếu dấu kết câu thì thêm dấu chấm.
    /// </summary>
    public static string Question(
        QuestionType type, string? content, IReadOnlyList<string?> optionLabels, IReadOnlyList<string> rightItems, IReadOnlyList<string> items)
    {
        var parts = new List<string> { Blanks().Replace(content ?? string.Empty, "chỗ trống").Trim() };
        if (type is QuestionType.SINGLE_CHOICE or QuestionType.SOURCE_READING)
            parts.AddRange(optionLabels.Select((label, i) => $"{(char)('A' + i)}: {label ?? "hình"}"));
        if (type == QuestionType.MATCHING) parts.AddRange(rightItems);
        if (type == QuestionType.ORDERING) parts.AddRange(items);
        parts.Add(Instruction(type));
        return string.Join(' ', parts
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .Select(part => SentenceEnd().IsMatch(part) ? part : part + "."));
    }

    /// <summary>
    /// Lời phản hồi có giải thích: lời khen khi em đúng (lời khen đổi theo vị trí câu; chưa biết vị trí thì lấy cả ba) và lời chỉ đáp án khi em sai lần
    /// hai. Câu chưa có giải thích thì phản hồi là câu cố định, giao diện tự nhờ tạo trước, nên trả rỗng.
    /// </summary>
    public static IReadOnlyList<string> Feedback(string? explanation, int? position)
    {
        var text = explanation?.Trim();
        if (string.IsNullOrEmpty(text)) return [];
        string[] titles = position is { } p ? [CorrectTitles[(p - 1 + CorrectTitles.Length) % CorrectTitles.Length]] : CorrectTitles;
        return [.. titles.Select(title => $"{title} {text}"), $"{RevealTitle} {text}"];
    }

    [GeneratedRegex("_{3,}")]
    private static partial Regex Blanks();

    [GeneratedRegex(@"[.!?…:]\z")]
    private static partial Regex SentenceEnd();
}
