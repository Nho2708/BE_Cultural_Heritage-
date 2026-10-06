using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Quy tắc thuần của gợi ý cho học sinh và cho lớp: số mục mỗi phần, mức khó mục tiêu của em, ngưỡng "nên ôn" của lớp và các câu lý do.
/// Đổi giá trị ở đây là đổi quy tắc nghiệp vụ.
/// </summary>
public static class RecommendationRules
{
    /// <summary>Mỗi phần gợi ý cho học sinh có tối đa 3 mục.</summary>
    public const int StudentSectionLimit = 3;

    /// <summary>Mỗi phần gợi ý cho lớp có tối đa 5 mục.</summary>
    public const int ClassSectionLimit = 5;

    /// <summary>Di sản "nên ôn" của lớp: tỉ lệ sai từ 40%.</summary>
    public const decimal ClassWrongRatio = 0.4m;

    /// <summary>Di sản "nên ôn" của lớp: ít nhất 10 câu trả lời.</summary>
    public const int ClassMinAnswers = 10;

    /// <summary>Bài học "nên ôn lại" của em: tỉ lệ đúng dưới 50% ở các di sản của bài.</summary>
    public const decimal ReviewLessonRatio = AttemptRules.MidRatio;

    /// <summary>Quiz đóng trong khoảng này thì lý do ghi "sắp đóng".</summary>
    public static readonly TimeSpan ClosingSoon = TimeSpan.FromDays(3);

    /// <summary>
    /// Mức khó mục tiêu của em: mức thành thạo chung từ 80% hợp Khó, từ 50% đến dưới 80% hợp Trung bình, dưới 50% hợp Dễ; chưa đủ kết quả
    /// (dưới 3 câu đã trả lời) thì theo cấp học: cấp 1 Dễ, cấp 2 và cấp 3 Trung bình.
    /// </summary>
    public static DifficultyLevel TargetDifficulty(int level, int correct, int answered)
    {
        if (answered < AttemptRules.MasteryMinAnswered) return level == 1 ? DifficultyLevel.EASY : DifficultyLevel.MEDIUM;
        var ratio = (decimal)correct / answered;
        return ratio >= AttemptRules.HighRatio ? DifficultyLevel.HARD : ratio >= AttemptRules.MidRatio ? DifficultyLevel.MEDIUM : DifficultyLevel.EASY;
    }

    public static string DifficultyName(DifficultyLevel? level) => level switch
    {
        DifficultyLevel.EASY => "Dễ",
        DifficultyLevel.MEDIUM => "Trung bình",
        DifficultyLevel.HARD => "Khó",
        _ => string.Empty,
    };

    /// <summary>Độ lệch giữa độ khó của quiz và mức khó mục tiêu: 0 là đúng mức; quiz chưa đặt độ khó coi như lệch 1.</summary>
    public static int DifficultyDistance(DifficultyLevel? quiz, DifficultyLevel target) => quiz is null ? 1 : Math.Abs((int)quiz - (int)target);

    /// <summary>Tỉ lệ sai của lớp ở một di sản: số câu sai hoặc bỏ trống chia tổng số câu trả lời; 0 khi chưa có câu nào.</summary>
    public static decimal WrongRatio(int wrong, int answered) => answered <= 0 ? 0m : (decimal)wrong / answered;

    /// <summary>Di sản nên ôn của lớp: tỉ lệ sai từ 40% và có ít nhất 10 câu trả lời.</summary>
    public static bool ClassShouldReview(int wrong, int answered) =>
        answered >= ClassMinAnswers && WrongRatio(wrong, answered) >= ClassWrongRatio;

    /// <summary>Phần trăm nguyên (làm tròn nửa lên) để ghi vào câu lý do của lớp.</summary>
    public static int WholePercent(int part, int whole) => whole <= 0 ? 0 : (int)Math.Round(part * 100m / whole, 0, MidpointRounding.AwayFromZero);

    // ---- Lý do ----

    public static string ClassReviewReason(int wrong, int answered) =>
        $"Lớp sai {WholePercent(wrong, answered)}% ở {answered} câu về di sản này";

    /// <summary>Lý do "nên ôn" của em: cấp 1 không có số liệu (không phần trăm, không số câu đúng sai).</summary>
    public static string StudentReviewReason(int level, int wrongQuestions) => level == 1
        ? "Em ôn lại di sản này nhé!"
        : $"Em chưa đúng {wrongQuestions} câu về di sản này";

    public static string WeakQuizReason(int level, int weakQuestions) => level == 1
        ? "Bài này giúp em ôn lại chỗ còn yếu."
        : $"Có {weakQuestions} câu về di sản em còn yếu";

    public static string ClosingSoonReason(DateTime closeAt, DateTime now)
    {
        var days = (int)Math.Ceiling((closeAt - now).TotalDays);
        return days <= 1 ? "Sắp đóng" : $"Còn {days} ngày";
    }

    public const string OpenQuizReason = "Bài kiểm tra đang mở cho lớp em";

    public static string ReviewLessonReason(int level, decimal ratePercent) => level == 1
        ? "Em mở lại bài này để ôn nhé!"
        : $"Em đúng {(int)Math.Round(ratePercent, 0, MidpointRounding.AwayFromZero)}% ở các di sản của bài";

    public const string NewLessonReason = "Bài học mới của lớp em";

    public const string AskTeacherForLessonCode = "Nhờ thầy cô đưa mã bài học";

    /// <summary>Phần nên ôn thay bằng lời khen khi em làm đúng hết.</summary>
    public const string ReviewPraise = "Em làm đúng hết rồi, giỏi quá! Em hãy thử bài mới nhé.";
}
