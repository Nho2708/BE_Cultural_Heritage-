using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Quy tắc thuần của album di sản, câu mang về nhà và bước Nhớ lại ở cấp 1: tên nhóm thẻ theo loại di sản, câu em hỏi bố mẹ, và chọn các câu ôn
/// từ những bài chơi em đã nộp. Đổi giá trị ở đây là đổi quy tắc nghiệp vụ.
/// </summary>
public static class AlbumRules
{
    /// <summary>Số bài học mới nhất của lớp hiện trên con đường ở trang học.</summary>
    public const int ClassLessonLimit = 30;

    /// <summary>Số tên di sản tối đa ở mỗi bài trên con đường.</summary>
    public const int HeritageNamesPerLesson = 3;

    /// <summary>Tên nhóm dễ hiểu với học sinh nhỏ, theo loại di sản; chưa rõ loại thì "Di sản khác".</summary>
    public static string GroupName(HeritageType? type) => type switch
    {
        HeritageType.TANGIBLE => "Di tích và đồ vật xưa",
        HeritageType.INTANGIBLE => "Lễ hội, nghề và làn điệu",
        HeritageType.NATURAL or HeritageType.MIXED => "Cảnh đẹp thiên nhiên",
        HeritageType.DOCUMENTARY => "Sách và tư liệu xưa",
        _ => "Di sản khác",
    };

    /// <summary>
    /// Câu em mang về hỏi bố mẹ, theo loại của di sản đầu tiên của bài (<paramref name="heritageName"/> là tên di sản đó).
    /// </summary>
    public static string TakeHomeQuestion(HeritageType? type, string? heritageName)
    {
        var name = (heritageName ?? string.Empty).Trim();
        return type switch
        {
            HeritageType.TANGIBLE => $"Nhà mình đã từng đi thăm {name} hay một di tích nào gần nhà chưa?",
            HeritageType.INTANGIBLE => $"Ông bà, bố mẹ đã từng xem hay tham gia {name} chưa?",
            HeritageType.NATURAL or HeritageType.MIXED => $"Bố mẹ đã từng đến {name} chưa? Ở đó có gì đẹp?",
            HeritageType.DOCUMENTARY => "Nhà mình có giữ sách, ảnh hay giấy tờ xưa nào không?",
            _ => $"Em kể cho bố mẹ nghe về {name} nhé. Bố mẹ biết gì về di sản này?",
        };
    }

    // ---- Bước Nhớ lại ----

    /// <summary>Bước Nhớ lại cho em ôn tối đa 3 câu.</summary>
    public const int RecallMaxQuestions = 3;

    /// <summary>Chỉ hai dạng câu Đúng/Sai và Chọn hình đúng dùng được cho bước Nhớ lại (trả lời một lần chạm).</summary>
    public static readonly QuestionType[] RecallTypes = [QuestionType.TRUE_FALSE, QuestionType.IMAGE_CHOICE];

    public static bool IsRecallType(QuestionType? type) => type is { } t && RecallTypes.Contains(t);

    /// <summary>Một câu em đã làm ở bài chơi đã nộp, ứng viên của bước Nhớ lại.</summary>
    public sealed record RecallCandidate(long QuestionId, long AttemptId, DateTime? SubmittedAt, bool Correct);

    /// <summary>
    /// Chọn các câu ôn: ưu tiên câu em làm chưa đúng rồi tới câu đúng; trong mỗi nhóm, lượt nộp gần nhất trước; mỗi câu chỉ một lần. Trả mã câu,
    /// tối đa <see cref="RecallMaxQuestions"/>.
    /// </summary>
    public static IReadOnlyList<long> PickRecall(IEnumerable<RecallCandidate> candidates) =>
        candidates
            .OrderBy(c => c.Correct)
            .ThenByDescending(c => c.SubmittedAt)
            .ThenByDescending(c => c.AttemptId)
            .ThenBy(c => c.QuestionId)
            .Select(c => c.QuestionId)
            .Distinct()
            .Take(RecallMaxQuestions)
            .ToList();
}
