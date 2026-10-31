using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>Cách thời gian làm bài của quiz áp dụng theo cấp của lớp: cấp 1 không có, cấp 2 tùy chọn, cấp 3 bắt buộc.</summary>
public enum TimeLimitRule
{
    NOT_ALLOWED,
    OPTIONAL,
    REQUIRED,
}

/// <summary>
/// Quiz đang ở giai đoạn nào so với giờ hiện tại: chưa phát hành (không có giai đoạn), chưa tới giờ mở, đang nhận bài, đã hết hạn nhận bài
/// (quá giờ đóng hoặc đã đóng bằng tay).
/// </summary>
public enum QuizOpenState
{
    NONE,
    NOT_OPEN,
    OPEN,
    ENDED,
}

/// <summary>
/// Hằng số và phép kiểm thuần của quiz: tên, số câu, điểm, thời gian làm theo cấp, câu nào dùng được ở cấp nào, độ khó của bài học
/// suy từ các quiz. Đổi giá trị ở đây là đổi quy tắc nghiệp vụ.
/// </summary>
public static class QuizRules
{
    public const int NameMaxLength = 255;
    public const int DescriptionMaxLength = 4000;

    /// <summary>Quiz có 1-50 câu.</summary>
    public const int MinQuestions = 1;
    public const int MaxQuestions = 50;

    /// <summary>Điểm mỗi câu lớn hơn 0, không quá 100, bước 0,25; mặc định 1.</summary>
    public const decimal MaxPoint = 100m;
    public const decimal PointStep = 0.25m;
    public const decimal DefaultPoint = 1m;

    /// <summary>Thời gian làm bài tính bằng phút, 1-180.</summary>
    public const int TimeLimitMin = 1;
    public const int TimeLimitMax = 180;

    /// <summary>Hậu tố tên của bản sao quiz.</summary>
    public const string CopySuffix = " (bản sao)";

    public static bool IsValidPoint(decimal point) => point > 0 && point <= MaxPoint && point % PointStep == 0;

    public static TimeLimitRule TimeLimitRuleOf(int level) => level switch
    {
        1 => TimeLimitRule.NOT_ALLOWED,
        2 => TimeLimitRule.OPTIONAL,
        _ => TimeLimitRule.REQUIRED,
    };

    public static string TimeLimitRuleText(int level) => TimeLimitRuleOf(level) switch
    {
        TimeLimitRule.NOT_ALLOWED => "Lớp cấp 1 không giới hạn thời gian làm bài, không hiện đồng hồ",
        TimeLimitRule.OPTIONAL => "Lớp cấp 2 có thể đặt thời gian làm bài (1-180 phút) hoặc để trống là không giới hạn",
        _ => "Lớp cấp 3 bắt buộc đặt thời gian làm bài (1-180 phút)",
    };

    /// <summary>
    /// Kiểm thời gian làm bài với cấp của lớp. Khi <paramref name="forPublish"/> là false (đang soạn) thì cấp 3 được để trống, chỉ chặn giá trị
    /// sai; khi phát hành thì cấp 3 phải đã đặt. Trả null nếu hợp lệ, không thì câu lý do.
    /// </summary>
    public static string? CheckTimeLimit(int level, int? minutes, bool forPublish)
    {
        var rule = TimeLimitRuleOf(level);
        if (rule == TimeLimitRule.NOT_ALLOWED)
            return minutes is null ? null : "lớp cấp 1 không giới hạn thời gian làm bài, hãy để trống";
        if (minutes is null) return rule == TimeLimitRule.REQUIRED && forPublish ? "lớp cấp 3 phải đặt thời gian làm bài" : null;
        return minutes is >= TimeLimitMin and <= TimeLimitMax ? null : $"thời gian làm bài từ {TimeLimitMin} đến {TimeLimitMax} phút";
    }

    /// <summary>Thời gian làm bài đổi theo cấp của lớp đích khi nhân bản bài học: cấp 1 bỏ giới hạn, cấp 2 và cấp 3 giữ giá trị đang có.</summary>
    public static int? NormalizeTimeLimit(int level, int? minutes) => level == 1 ? null : minutes;

    /// <summary>
    /// Cấp 1 không đặt giờ mở và đóng để em không bị áp lực: bài chơi mở ngay khi phát hành và nhận bài tới khi thầy cô đóng. Cấp 2 và cấp 3 bắt buộc đặt cả hai.
    /// </summary>
    public static bool RequiresOpenWindow(int level) => level != 1;

    /// <summary>
    /// Câu dùng được ở cấp của lớp khi cấp nằm trong khoảng cấp phù hợp của câu và dạng của câu dùng được ở cấp đó
    /// (bảng dạng câu theo cấp).
    /// </summary>
    public static bool QuestionFitsLevel(QuestionType? type, int levelMin, int levelMax, int level) =>
        level >= levelMin && level <= levelMax && type is { } t && QuestionTypes.Of(t).AllowsLevel(level);

    public static string StatusLabel(QuizStatus? status) => status switch
    {
        QuizStatus.DRAFT => "Nháp",
        QuizStatus.PUBLISHED => "Đang phát hành",
        QuizStatus.CLOSED => "Đã đóng",
        _ => string.Empty,
    };

    /// <summary>
    /// Giai đoạn của quiz tại <paramref name="now"/>: quiz Nháp không có giai đoạn; đã đóng hoặc quá giờ đóng là hết hạn nhận bài;
    /// chưa tới giờ mở là chưa mở; còn lại là đang nhận bài (chưa đặt giờ coi như không giới hạn phía đó).
    /// </summary>
    public static QuizOpenState OpenStateOf(QuizStatus? status, DateTime? openAt, DateTime? closeAt, DateTime now)
    {
        if (status == QuizStatus.CLOSED) return QuizOpenState.ENDED;
        if (status != QuizStatus.PUBLISHED) return QuizOpenState.NONE;
        if (closeAt is { } close && now >= close) return QuizOpenState.ENDED;
        if (openAt is { } open && now < open) return QuizOpenState.NOT_OPEN;
        return QuizOpenState.OPEN;
    }

    public static string OpenStateLabel(QuizOpenState state) => state switch
    {
        QuizOpenState.NOT_OPEN => "Chưa tới giờ mở",
        QuizOpenState.OPEN => "Đang nhận bài",
        QuizOpenState.ENDED => "Đã hết hạn nhận bài",
        _ => string.Empty,
    };

    /// <summary>Giờ không kèm múi giờ hiểu là giờ Việt Nam; giờ kèm múi giờ đổi về UTC. Cắt tới mili giây như cột <c>datetime2(3)</c>.</summary>
    public static DateTime ToUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value - VietnamTime.Offset, DateTimeKind.Utc),
        };
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
    }

    /// <summary>Giờ hiển thị dạng "08:30 07/10/2026" theo giờ Việt Nam.</summary>
    public static string FormatVietnamTime(DateTime? utc) => utc is { } v
        ? (DateTime.SpecifyKind(v, DateTimeKind.Utc) + VietnamTime.Offset).ToString("HH:mm dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)
        : "chưa đặt";
}

/// <summary>
/// Mã bài kiểm tra: <c>KT</c> + 6 ký tự của cùng bảng 32 ký tự với mã bài học, sinh lúc phát hành lần đầu. Em gõ mã này ở ô nhập mã để vào thẳng bài kiểm tra.
/// </summary>
public static class QuizCodes
{
    public const string Prefix = "KT";
    public const int Length = LessonCodes.Length;

    public static string NewCode() => LessonCodes.NewCode(Prefix);

    /// <summary>Mã đã chuẩn hóa có đúng hình dạng <c>KT</c> + 6 ký tự của bảng 32 ký tự hay không.</summary>
    public static bool IsWellFormed(string? normalized) => LessonCodes.HasShape(normalized, Prefix);
}

/// <summary>
/// Độ khó của bài học: không lưu, tính khi truy vấn từ độ khó của các câu trong các quiz chưa xóa của bài. Mức nhiều câu nhất thắng;
/// hai mức bằng nhau thì lấy mức cao hơn; bài chưa có quiz nào có câu thì chưa có độ khó.
/// </summary>
public static class LessonDifficulty
{
    public const string NoQuizLabel = "Chưa có bài kiểm tra";

    public static DifficultyLevel? Dominant(IReadOnlyDictionary<DifficultyLevel, int> counts)
    {
        DifficultyLevel? best = null;
        var bestCount = 0;
        foreach (var (level, count) in counts)
        {
            if (count <= 0) continue;
            if (count > bestCount || (count == bestCount && best is { } b && level > b))
            {
                best = level;
                bestCount = count;
            }
        }
        return best;
    }

    public static string LabelOf(DifficultyLevel? level) => level switch
    {
        DifficultyLevel.EASY => "Dễ",
        DifficultyLevel.MEDIUM => "Trung bình",
        DifficultyLevel.HARD => "Khó",
        _ => NoQuizLabel,
    };
}
