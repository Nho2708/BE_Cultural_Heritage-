using System.Text.RegularExpressions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Speech;

/// <summary>
/// Lời đọc to của bài chơi cấp 1, dựng đúng từng ký tự như giao diện web dựng: câu hỏi kèm phương án và lời hướng dẫn, lời phản hồi sau khi kiểm.
/// Máy chủ đặt tên tệp tiếng theo băm của chữ, nên chữ tạo trước phải trùng chữ em bấm nghe thì mới có ngay. Đổi câu chữ ở đây thì đổi cùng lúc ở web
/// (lời đọc câu hỏi và lời phản hồi của bài chơi cấp 1); hai bên giữ cùng một bộ câu mẫu trong kiểm thử.
/// Các lời đọc khác của khu học cấp 1 (lời chào, trang kết quả, câu mang về nhà, album, các câu cố định) cũng dựng ở đây để máy chủ biết chữ nào
/// là chữ của sản phẩm hay của nội dung bài (xem <see cref="SpeechTextPolicy"/>).
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

    // ---- Các lời đọc khác của khu học cấp 1 (web học tập và app), dựng đúng như giao diện dựng ----

    /// <summary>Lời khen khi câu đúng mà chưa có giải thích.</summary>
    private const string CorrectWithoutExplanation = "Em nhớ bài giỏi lắm!";

    /// <summary>Lời chào đầu trang học khi đọc to: không có tên em (tên học sinh không gửi sang dịch vụ giọng đọc).</summary>
    public const string Greeting = "Chào em!";

    /// <summary>Lời báo khi mạng chập chờn lúc kiểm câu.</summary>
    public const string OfflineLine = "Mạng đang chập chờn. Em bấm Thử lại nhé.";

    /// <summary>Lời nhắc đứng trước câu mang về nhà.</summary>
    public const string TakeHomeHeading = "Về nhà hỏi bố mẹ nhé";

    /// <summary>Tiêu đề phần xem lại đáp án ở trang kết quả, có nút nghe.</summary>
    public const string ReviewTitle = "Đáp án và giải thích";

    /// <summary>Lời đọc đầu trang học: lời chào không tên rồi lời cổ vũ của máy chủ.</summary>
    public static string GreetingWith(string? cheer) => string.IsNullOrWhiteSpace(cheer) ? Greeting : $"{Greeting} {cheer.Trim()}";

    /// <summary>"Em được 2 sao!": lời đầu trang kết quả.</summary>
    public static string StarsHeadline(int stars) => $"Em được {stars} sao!";

    /// <summary>Trang kết quả đọc số sao và lời khen cùng một lượt.</summary>
    public static string ResultSpeech(int stars, string? praise) => $"{StarsHeadline(stars)} {praise?.Trim()}".Trim();

    /// <summary>Lời đọc khối câu mang về nhà: lời nhắc rồi câu hỏi.</summary>
    public static string TakeHome(string text) => $"{TakeHomeHeading}. {text}";

    /// <summary>Các ý em cần nhớ đọc liền một lượt, nối bằng ". ".</summary>
    public static string KeyPoints(IEnumerable<string> points) => string.Join(". ", points);

    /// <summary>Lời đọc trang giới thiệu bài chơi: số câu rồi lời nhắc (nếu có).</summary>
    public static string QuizIntro(int questionCount, string? notice) => $"Bài chơi có {questionCount} câu. {notice}".Trim();

    /// <summary>Lời đọc thẻ trong album: tên di sản rồi mẩu chuyện. Web đọc "Em đã học về …" khi di sản chưa có mô tả ngắn, app đọc mỗi tên.</summary>
    public static IReadOnlyList<string> AlbumCard(string? name, string? shortDescription)
    {
        var title = name?.Trim() ?? string.Empty;
        if (title.Length == 0) return [];
        var story = shortDescription?.Trim();
        return string.IsNullOrEmpty(story) ? [$"{title}. Em đã học về {title}.", $"{title}. "] : [$"{title}. {story}"];
    }

    /// <summary>Lời đọc khung "Lớp của em" ở trang học: tên lớp, rồi thầy cô nếu có.</summary>
    public static string ClassCard(string? className, string? teacherName) =>
        $"Em học lớp {className}.{(string.IsNullOrEmpty(teacherName) ? string.Empty : $" Thầy cô của em là {teacherName}.")}";

    /// <summary>
    /// Lời chỉ đáp án khi câu chưa có giải thích: nói đáp án đúng bằng lời theo nhãn phương án (chưa biết phương án nào đúng nên lấy mọi nhãn).
    /// Ghép cặp, sắp xếp và nhãn rỗng dùng câu cố định trong <see cref="FixedPhrases"/>.
    /// </summary>
    public static IReadOnlyList<string> RevealWithoutExplanation(QuestionType type, IEnumerable<string?> optionLabels)
    {
        if (type is QuestionType.MATCHING or QuestionType.ORDERING) return [];
        return optionLabels
            .Select(label => label?.Trim())
            .Where(label => !string.IsNullOrEmpty(label))
            .Select(label => type == QuestionType.IMAGE_CHOICE ? $"{RevealTitle} Đáp án đúng là hình “{label}”." : $"{RevealTitle} Đáp án đúng là: {label}.")
            .ToList();
    }

    /// <summary>Lời gợi ý khi em ghép cặp hoặc sắp xếp đúng một phần ở lần kiểm đầu: mọi số phần đúng từ 1 tới <paramref name="total"/> - 1.</summary>
    public static IReadOnlyList<string> PartialRetry(QuestionType type, int total)
    {
        var lines = new List<string>();
        for (var right = 1; right < total; right++)
        {
            if (type == QuestionType.MATCHING)
                lines.Add($"Gần đúng rồi! Em nối đúng {right} trên {total} cặp. Các cặp chưa đúng đã được gỡ ra, em nối lại nhé!");
            else if (type == QuestionType.ORDERING)
                lines.Add($"Gần đúng rồi! Em xếp đúng {right} trên {total} bước. Em đổi chỗ các bước chưa đúng rồi kiểm tra lại nhé!");
        }
        return lines;
    }

    /// <summary>
    /// Các câu cố định mà giao diện cấp 1 đọc to đúng nguyên văn, không phụ thuộc nội dung bài: lời hướng dẫn từng dạng câu, lời khen khi câu chưa có giải thích,
    /// lời gợi ý thử lại khi chưa đúng phần nào, lời chỉ đáp án không cần nhãn, số sao và lời khen chung ở trang kết quả, lời chào kèm các lời cổ vũ cố định,
    /// lời báo mạng và tiêu đề phần xem lại đáp án. Là chữ của sản phẩm nên máy chủ luôn được tạo giọng cho các câu này.
    /// </summary>
    public static IReadOnlyList<string> FixedPhrases { get; } = BuildFixedPhrases();

    private static List<string> BuildFixedPhrases()
    {
        QuestionType[] types = [QuestionType.TRUE_FALSE, QuestionType.IMAGE_CHOICE, QuestionType.MATCHING, QuestionType.ORDERING, QuestionType.SINGLE_CHOICE];
        var lines = new List<string>();
        lines.AddRange(types.Select(Instruction));
        lines.AddRange(CorrectTitles.Select(title => $"{title} {CorrectWithoutExplanation}"));
        lines.AddRange([
            "Chưa đúng rồi. Em nhớ lại câu chuyện rồi thử lại nhé!",
            "Chưa phải hình này. Em nhớ lại câu chuyện rồi chọn hình khác nhé!",
            "Chưa đúng rồi. Em thử nối lại các cặp nhé!",
            "Chưa đúng rồi. Em thử xếp lại các bước nhé!",
            $"{RevealTitle} Các cặp đúng đang hiện trên màn hình.",
            $"{RevealTitle} Thứ tự đúng đang hiện trên màn hình.",
            $"{RevealTitle} Đáp án đúng là hình đang sáng.",
            $"{RevealTitle} Đáp án đúng đang sáng trên màn hình.",
        ]);
        for (var stars = 1; stars <= 3; stars++)
        {
            lines.Add(StarsHeadline(stars));
            lines.Add(ResultSpeech(stars, AttemptRules.Praise(stars)));
        }
        lines.Add(Greeting);
        // Lời cổ vũ không nhắc tên bài hay số sao: có lượt đang dở, lớp chưa có bài, lời rủ khám phá.
        lines.Add(GreetingWith(AttemptRules.LevelOneCheer(0, hasInProgress: true, null, hasLessons: true)));
        lines.Add(GreetingWith(AttemptRules.LevelOneCheer(0, hasInProgress: false, null, hasLessons: false)));
        lines.Add(GreetingWith(AttemptRules.LevelOneCheer(0, hasInProgress: false, null, hasLessons: true)));
        lines.Add(OfflineLine);
        lines.Add(ReviewTitle);
        return lines;
    }

    [GeneratedRegex("_{3,}")]
    private static partial Regex Blanks();

    [GeneratedRegex(@"[.!?…:]\z")]
    private static partial Regex SentenceEnd();
}
