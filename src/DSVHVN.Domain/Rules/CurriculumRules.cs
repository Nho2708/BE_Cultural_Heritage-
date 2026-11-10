using DSVHVN.Domain.Classes;

namespace DSVHVN.Domain.Rules;

/// <summary>Quy tắc thuần của chủ đề chương trình môn học: độ dài các trường theo cột, khối 1-12 và số di sản tối đa của một chủ đề. Không chạm CSDL.</summary>
public static class CurriculumRules
{
    public const int SubjectMaxLength = 100;
    public const int GroupTitleMaxLength = 255;
    public const int TitleMaxLength = 255;
    public const int DescriptionMaxLength = 1000;
    public const int SourceUrlMaxLength = 500;

    /// <summary>Số di sản tối đa gắn với một chủ đề.</summary>
    public const int MaxHeritagesPerTopic = 200;

    /// <summary>Thứ tự trong chương trình: số nguyên từ 0 đến 1.000.000.</summary>
    public const int MaxSortOrder = 1_000_000;

    public static bool IsValidGrade(int grade) => grade is >= SchoolClass.MinGrade and <= SchoolClass.MaxGrade;
}
