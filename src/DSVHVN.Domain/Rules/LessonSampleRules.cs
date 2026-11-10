using System.Text.RegularExpressions;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Quy tắc thuần của thư viện bài mẫu dùng chung: bài hay của một giáo viên, sau khi quản trị hệ thống duyệt, vào thư viện cho mọi trường;
/// giáo viên khác xem trước rồi dùng cho lớp của mình. Không chạm CSDL.
/// </summary>
public static class LessonSampleRules
{
    /// <summary>Lý do từ chối hoặc gỡ: bắt buộc, 1-500 ký tự.</summary>
    public const int NoteMaxLength = 500;

    /// <summary>Đoạn trích tóm tắt trên thẻ bài của thư viện: tối đa 200 ký tự.</summary>
    public const int SummaryExcerptMaxLength = 200;

    /// <summary>
    /// Lý do chưa đề xuất được, hoặc null nếu đề xuất được: bài phải Đã xuất bản và chưa ở trạng thái chờ duyệt hay đã trong thư viện.
    /// Bài từng bị từ chối hoặc chưa ở thư viện thì đề xuất lại được. Điều kiện xuất bản đầy đủ kiểm ở dịch vụ (cần nội dung bài).
    /// </summary>
    public static string? RequestBlockReason(ContentStatus? status, LessonSampleStatus? sample)
    {
        if (status == ContentStatus.ARCHIVED) return "Bài học đã lưu trữ. Hãy xuất bản lại trước khi đề xuất.";
        if (status != ContentStatus.PUBLISHED) return "Chỉ đề xuất được bài học đã xuất bản.";
        return sample switch
        {
            LessonSampleStatus.PENDING => "Bài học đang chờ quản trị hệ thống duyệt.",
            LessonSampleStatus.APPROVED => "Bài học đã ở trong thư viện bài mẫu.",
            _ => null,
        };
    }

    /// <summary>Giáo viên chủ bài rút bài khỏi thư viện từ mọi trạng thái đang có (chờ duyệt, trong thư viện, bị từ chối).</summary>
    public static bool CanWithdraw(LessonSampleStatus? sample) => sample is not null;

    /// <summary>Quản trị hệ thống duyệt chỉ đề xuất đang chờ duyệt.</summary>
    public static bool CanApprove(LessonSampleStatus? sample) => sample == LessonSampleStatus.PENDING;

    /// <summary>Quản trị hệ thống từ chối đề xuất đang chờ duyệt, hoặc gỡ bài đang ở trong thư viện.</summary>
    public static bool CanReject(LessonSampleStatus? sample) => sample is LessonSampleStatus.PENDING or LessonSampleStatus.APPROVED;

    /// <summary>
    /// Đoạn trích tóm tắt cho thẻ bài: chữ thuần đã gộp khoảng trắng, nếu dài quá 200 ký tự thì cắt ở ranh giới từ và thêm dấu chấm lửng
    /// (tổng không quá 200 ký tự).
    /// </summary>
    public static string SummaryExcerpt(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return string.Empty;
        var text = Regex.Replace(plainText.Trim(), @"\s+", " ");
        if (text.Length <= SummaryExcerptMaxLength) return text;

        var cut = text[..(SummaryExcerptMaxLength - 1)];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > SummaryExcerptMaxLength / 2) cut = cut[..lastSpace];
        return cut.TrimEnd(' ', ',', ';', ':', '.', '!', '?') + "…";
    }
}

/// <summary>
/// Quy tắc "Viết lại một phần bằng AI": loại phần được viết lại và kiểm kết quả theo giới hạn của từng loại. Giới hạn vượt chỉ thành cảnh báo,
/// không chặn (giáo viên quyết định dùng bản mới hay giữ bản cũ). Chữ rỗng là lỗi đầu ra, kiểm ở dịch vụ.
/// </summary>
public static partial class SectionRewriteRules
{
    /// <summary>Chữ gửi lên: 1-4000 ký tự.</summary>
    public const int TextMaxLength = 4000;

    /// <summary>Yêu cầu của giáo viên (như ô yêu cầu thêm khi soạn bài): 0-500 ký tự.</summary>
    public const int InstructionsMaxLength = 500;

    /// <summary>Nội dung chính hiện tại của bài gửi kèm để AI giữ mạch: tối đa 6000 ký tự.</summary>
    public const int LessonContentMaxLength = 6000;

    public const int StoryMaxWords = 50;
    public const int StoryMaxAverageSentenceWords = 15;
    public const int StoryMinSentences = 2;
    public const int StoryMaxSentences = 4;
    public const int Level1CaptionMaxWords = 20;
    public const int CaptionMaxWords = 40;
    public const int ReadingMinWords = 20;
    public const int ReadingMaxWords = 400;
    public const int MilestoneMaxWords = 60;
    public const int MilestoneMaxSentences = 2;
    public const int ReflectionMaxWords = 60;

    /// <summary>
    /// Loại phần viết lại được: thuộc khung của cấp và không phải đoạn trích tư liệu gốc (đoạn trích phải nguyên văn).
    /// Cấp 1: bước câu chuyện, hình. Cấp 2: đoạn bài đọc, hình, mốc thời gian. Cấp 3: đoạn bài đọc, mốc thời gian, câu hỏi gợi suy nghĩ.
    /// </summary>
    public static bool CanRewrite(int level, LessonSectionType type) =>
        type != LessonSectionType.SOURCE_EXCERPT && LessonFrames.For(level).Allows(type);

    /// <summary>
    /// Kiểm chữ AI viết lại theo giới hạn của loại phần (bước câu chuyện 2-4 câu, tối đa 50 từ, câu trung bình không quá 15 từ; hình 1 câu, cấp 1 tối đa 20 từ,
    /// cấp 2 và 3 tối đa 40 từ; bài đọc 20-400 từ; mốc 1-2 câu, tối đa 60 từ; câu hỏi gợi suy nghĩ 1 câu hỏi kết thúc bằng dấu hỏi, tối đa 60 từ)
    /// và chữ thuần (không dấu Markdown). Trả danh sách cảnh báo; rỗng nghĩa là đạt.
    /// </summary>
    public static IReadOnlyList<string> Check(int level, LessonSectionType type, string text)
    {
        var warnings = new List<string>();
        var words = LessonText.CountWords(text);
        var sentences = SpeechRules.SplitSentences(text).Count;

        switch (type)
        {
            case LessonSectionType.STORY:
                if (sentences is < StoryMinSentences or > StoryMaxSentences)
                    warnings.Add($"Bước câu chuyện có {sentences} câu; nên có {StoryMinSentences}-{StoryMaxSentences} câu.");
                if (words > StoryMaxWords)
                    warnings.Add($"Bước câu chuyện dài {words} từ; nên tối đa {StoryMaxWords} từ.");
                if (sentences > 0 && words > StoryMaxAverageSentenceWords * sentences)
                    warnings.Add($"Câu trung bình dài {words / (double)sentences:0.#} từ; cấp 1 nên giữ câu ngắn, trung bình không quá {StoryMaxAverageSentenceWords} từ.");
                break;
            case LessonSectionType.IMAGE:
                var captionMax = level == 1 ? Level1CaptionMaxWords : CaptionMaxWords;
                if (sentences > 1) warnings.Add($"Chú thích hình có {sentences} câu; nên chỉ một câu.");
                if (words > captionMax) warnings.Add($"Chú thích hình dài {words} từ; nên tối đa {captionMax} từ.");
                break;
            case LessonSectionType.READING:
                if (words is < ReadingMinWords or > ReadingMaxWords)
                    warnings.Add($"Đoạn bài đọc dài {words} từ; nên dài {ReadingMinWords}-{ReadingMaxWords} từ.");
                break;
            case LessonSectionType.MILESTONE:
                if (sentences > MilestoneMaxSentences) warnings.Add($"Mốc thời gian có {sentences} câu; nên có 1-{MilestoneMaxSentences} câu.");
                if (words > MilestoneMaxWords) warnings.Add($"Mốc thời gian dài {words} từ; nên tối đa {MilestoneMaxWords} từ.");
                break;
            case LessonSectionType.REFLECTION:
                if (sentences > 1) warnings.Add($"Câu hỏi gợi suy nghĩ có {sentences} câu; nên chỉ một câu hỏi.");
                if (!text.TrimEnd().EndsWith('?')) warnings.Add("Câu hỏi gợi suy nghĩ nên kết thúc bằng dấu hỏi.");
                if (words > ReflectionMaxWords) warnings.Add($"Câu hỏi gợi suy nghĩ dài {words} từ; nên tối đa {ReflectionMaxWords} từ.");
                break;
        }

        if (MarkdownMarks().IsMatch(text)) warnings.Add("Chữ còn dấu định dạng Markdown; bài học chỉ dùng văn bản thuần.");
        return warnings;
    }

    [GeneratedRegex(@"\*\*|__|`|^\s{0,3}#{1,6}\s|^\s*[-*+]\s", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownMarks();
}
