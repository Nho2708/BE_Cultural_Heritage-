using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Một dạng câu hỏi: tên hiển thị, các cấp học dùng được và có chấm từng phần hay không (bảng "dạng câu theo cấp", cố định trong code).
/// Mọi dạng dùng được ở một khoảng cấp liền nhau nên hai cột <c>level_min</c>, <c>level_max</c> biểu diễn được đủ.
/// </summary>
public sealed record QuestionTypeInfo(QuestionType Type, string Name, int MinLevel, int MaxLevel, bool MultiPart)
{
    public bool AllowsLevel(int level) => level >= MinLevel && level <= MaxLevel;
}

/// <summary>Bảng dạng câu theo cấp: cấp 1 có 4 dạng, cấp 2 có 4 dạng, cấp 3 có 3 dạng; tổng 8 dạng.</summary>
public static class QuestionTypes
{
    public static readonly IReadOnlyList<QuestionTypeInfo> All =
    [
        new(QuestionType.SINGLE_CHOICE, "Một đáp án đúng", 2, 3, false),
        new(QuestionType.TRUE_FALSE, "Đúng/Sai", 1, 2, false),
        new(QuestionType.IMAGE_CHOICE, "Chọn hình đúng", 1, 1, false),
        new(QuestionType.MATCHING, "Ghép cặp hình - tên", 1, 1, true),
        new(QuestionType.ORDERING, "Sắp xếp", 1, 2, true),
        new(QuestionType.FILL_BLANK, "Điền từ", 2, 2, true),
        new(QuestionType.MULTI_TRUE_FALSE, "Nhiều mệnh đề Đúng/Sai", 3, 3, true),
        new(QuestionType.SOURCE_READING, "Đọc tư liệu rồi trả lời", 3, 3, false),
    ];

    public static QuestionTypeInfo Of(QuestionType type) => All.Single(t => t.Type == type);

    /// <summary>Các dạng dùng được ở một cấp (1, 2, 3), theo thứ tự của bảng.</summary>
    public static IReadOnlyList<QuestionType> ForLevel(int level) => All.Where(t => t.AllowsLevel(level)).Select(t => t.Type).ToList();

    /// <summary>Tên dạng câu hiển thị cho giao diện.</summary>
    public static string NameOf(QuestionType? type) => type is null ? string.Empty : Of(type.Value).Name;
}

/// <summary>
/// Hằng số nghiệp vụ của câu hỏi: độ dài và số lượng theo từng dạng (quy tắc Cấu trúc câu hỏi), độ dài theo cột CSDL.
/// Đổi giá trị ở đây là đổi quy tắc nghiệp vụ.
/// </summary>
public static class QuestionRules
{
    public const int ContentMinLength = 10;
    public const int ContentMaxLength = 1000;
    public const int ExplanationMaxLength = 2000;
    public const int UrlMaxLength = 500;

    // ---- Phương án (một đáp án đúng, đọc tư liệu) ----
    public const int ChoiceOptionCount = 4;
    public const int OptionMaxLength = 1000;

    // ---- Chọn hình đúng ----
    public const int ImageChoiceMinCards = 3;
    public const int ImageChoiceMaxCards = 4;
    public const int LabelMinLength = 1;
    public const int LabelMaxLength = 50;

    // ---- Ghép cặp ----
    public const int MatchingMinPairs = 3;
    public const int MatchingMaxPairs = 5;
    public const int MatchLeftMaxLength = 1000;

    // ---- Sắp xếp ----
    public const int OrderingMinItems = 3;
    /// <summary>Cấp 1 sắp xếp các bước của câu chuyện: tối đa 4 mục. Cấp 2 sắp xếp sự kiện: tối đa 6 mục.</summary>
    public const int OrderingMaxItemsLevel1 = 4;
    public const int OrderingMaxItems = 6;
    public const int OrderingItemMaxLength = 200;

    // ---- Điền từ ----
    public const string BlankMarker = "___";
    public const int FillMinBlanks = 1;
    public const int FillMaxBlanks = 3;
    public const int AcceptedMinCount = 1;
    public const int AcceptedMaxCount = 5;
    public const int AcceptedMaxLength = 100;
    public const int WordBankMinWords = 3;
    public const int WordBankMaxWords = 8;

    // ---- Nhiều mệnh đề Đúng/Sai ----
    public const int StatementMinCount = 3;
    public const int StatementMaxCount = 5;
    public const int StatementMinLength = 10;
    public const int StatementMaxLength = 500;

    // ---- Đọc tư liệu ----
    public const int PassageMaxWords = 150;
    public const int PassageSourceMaxLength = 500;

    // ---- Sinh bằng AI ----
    public const int AiMinCount = 1;
    public const int AiMaxCount = 20;

    /// <summary>Hai phương án cố định của Đúng/Sai, theo thứ tự.</summary>
    public static readonly IReadOnlyList<string> TrueFalseOptions = ["Đúng", "Sai"];

    /// <summary>Vị trí đúng và số thứ tự chỗ trống lưu ở cột tinyint, CHECK 1..20.</summary>
    public const int MaxPosition = 20;
}
