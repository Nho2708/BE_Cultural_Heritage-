using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>Huy hiệu của học sinh, tính khi hiển thị từ các lượt làm bài (không lưu, không có điểm thưởng).</summary>
public enum BadgeCode
{
    PERFECT_SCORE,
    EXCELLENT,
    ON_TIME,
    PERSEVERANCE,
}

/// <summary>Mức thành thạo của học sinh với một di sản (hoặc một dạng câu), tính từ tỉ lệ câu đúng.</summary>
public enum MasteryLevel
{
    /// <summary>Dưới 3 câu đã trả lời.</summary>
    INSUFFICIENT_DATA,

    /// <summary>Dưới 50%.</summary>
    NEEDS_REVIEW,

    /// <summary>Từ 50% đến dưới 80%.</summary>
    FAIR,

    /// <summary>Từ 80%.</summary>
    MASTERED,
}

/// <summary>
/// Quy tắc thuần của việc làm bài: hạn nộp, đồng hồ theo cấp, phần trăm, số sao của cấp 1, huy hiệu và mức thành thạo.
/// Đổi giá trị ở đây là đổi quy tắc nghiệp vụ.
/// </summary>
public static class AttemptRules
{
    /// <summary>Máy chủ vẫn nhận câu trả lời và bài nộp tới hạn nộp cộng 30 giây (độ trễ đường truyền).</summary>
    public const int GraceSeconds = 30;

    /// <summary>Số lượt đã nộp để có huy hiệu Kiên trì.</summary>
    public const int PerseveranceAttempts = 5;

    /// <summary>Từ ngưỡng này (tỉ lệ) là Xuất sắc khi chưa tuyệt đối.</summary>
    public const decimal ExcellentRatio = 0.9m;

    /// <summary>Ngưỡng 3 sao và Thành thạo.</summary>
    public const decimal HighRatio = 0.8m;

    /// <summary>Ngưỡng 2 sao và Đạt.</summary>
    public const decimal MidRatio = 0.5m;

    /// <summary>Số câu tối thiểu để tính mức thành thạo.</summary>
    public const int MasteryMinAnswered = 3;

    /// <summary>Số huy hiệu hiện ở khu "gần đây" của trang học.</summary>
    public const int RecentBadgeLimit = 5;

    /// <summary>Chữ tối đa của một chỗ trống học sinh gõ (cột <c>response_text</c>).</summary>
    public const int ResponseTextMaxLength = 255;

    /// <summary>
    /// Thời gian làm bài áp dụng cho học sinh: lớp cấp 1 không giới hạn (không đồng hồ) dù bài kiểm tra có đặt giờ từ trước khi lớp đổi cấp;
    /// cấp 2 và 3 theo giá trị của bài kiểm tra.
    /// </summary>
    public static int? EffectiveTimeLimit(int level, int? quizMinutes) => level == 1 ? null : quizMinutes;

    /// <summary>
    /// Hạn nộp = lúc bắt đầu + thời gian làm, nhưng không muộn hơn giờ đóng; bài không giới hạn thời gian thì hạn nộp là giờ đóng.
    /// Null khi cả hai đều không đặt.
    /// </summary>
    public static DateTime? DeadlineOf(DateTime startedAt, int? timeLimitMinutes, DateTime? closeAt)
    {
        DateTime? byLimit = timeLimitMinutes is { } minutes ? startedAt.AddMinutes(minutes) : null;
        if (byLimit is null) return closeAt;
        return closeAt is { } close && close < byLimit ? close : byLimit;
    }

    /// <summary>Hạn nhận bài của máy chủ: hạn nộp cộng ân hạn đường truyền. Null nếu không có hạn nộp.</summary>
    public static DateTime? AcceptUntil(DateTime? deadline) => deadline?.AddSeconds(GraceSeconds);

    /// <summary>Đã quá hạn nhận bài tại <paramref name="now"/> chưa.</summary>
    public static bool IsOverdue(DateTime? deadline, DateTime now) => AcceptUntil(deadline) is { } until && now > until;

    /// <summary>Có hiện đồng hồ đếm ngược không: cấp 3 luôn có (khi có hạn nộp), cấp 2 khi bài đặt thời gian làm, cấp 1 không.</summary>
    public static bool ShowsClock(int level, int? effectiveTimeLimit, DateTime? deadline) => level switch
    {
        1 => false,
        2 => effectiveTimeLimit is not null && deadline is not null,
        _ => deadline is not null,
    };

    /// <summary>
    /// Thời gian làm bài (giây) tính từ lúc bắt đầu tới lúc nộp, không vượt thời gian làm của bài (nếu có) và không âm.
    /// </summary>
    public static int DurationSeconds(DateTime startedAt, DateTime endedAt, int? timeLimitMinutes)
    {
        var seconds = (int)Math.Max(0, (endedAt - startedAt).TotalSeconds);
        return timeLimitMinutes is { } limit ? Math.Min(seconds, limit * 60) : seconds;
    }

    /// <summary>Phần trăm điểm đạt được, làm tròn 1 chữ số thập phân (nửa lên); tổng điểm bằng 0 thì 0.</summary>
    public static decimal Percent(decimal score, decimal totalPoint) =>
        totalPoint <= 0 ? 0m : Math.Round(score * 100m / totalPoint, 1, MidpointRounding.AwayFromZero);

    public static decimal Ratio(decimal score, decimal totalPoint) => totalPoint <= 0 ? 0m : score / totalPoint;

    /// <summary>Số sao của cấp 1 từ tỉ lệ đúng của lượt: từ 80% là 3 sao, từ 50% là 2 sao, còn lại 1 sao (nộp bài luôn có ít nhất 1 sao).</summary>
    public static int Stars(decimal score, decimal totalPoint) => Ratio(score, totalPoint) switch
    {
        >= HighRatio => 3,
        >= MidRatio => 2,
        _ => 1,
    };

    /// <summary>Lời khen của cấp 1: luôn tích cực, khác nhau theo số sao.</summary>
    public static string Praise(int stars) => stars switch
    {
        3 => "Giỏi quá! Em được 3 sao, em thật xuất sắc!",
        2 => "Làm tốt lắm! Em được 2 sao, cố lên nhé!",
        _ => "Em đã hoàn thành bài rồi! Em được 1 sao, lần sau mình thử thêm nhé!",
    };

    public static string StatusLabel(AttemptStatus? status) => status switch
    {
        AttemptStatus.IN_PROGRESS => "Đang làm",
        AttemptStatus.SUBMITTED => "Đã nộp",
        AttemptStatus.EXPIRED => "Tự nộp khi hết giờ",
        _ => string.Empty,
    };

    // ---- Huy hiệu ----

    public static string BadgeName(BadgeCode badge) => badge switch
    {
        BadgeCode.PERFECT_SCORE => "Điểm tuyệt đối",
        BadgeCode.EXCELLENT => "Xuất sắc",
        BadgeCode.ON_TIME => "Nộp đúng hạn",
        _ => "Kiên trì",
    };

    public static string BadgeDescription(BadgeCode badge) => badge switch
    {
        BadgeCode.PERFECT_SCORE => "Em làm đúng tất cả các câu của bài kiểm tra.",
        BadgeCode.EXCELLENT => "Em đạt từ 90% số điểm của bài kiểm tra.",
        BadgeCode.ON_TIME => "Em tự nộp bài trước khi hết giờ.",
        _ => $"Em đã nộp từ {PerseveranceAttempts} bài kiểm tra.",
    };

    /// <summary>Huy hiệu tính từ riêng một lượt đã nộp: Điểm tuyệt đối, Xuất sắc, Nộp đúng hạn.</summary>
    public static IReadOnlyList<BadgeCode> AttemptBadges(AttemptStatus? status, decimal? score, decimal? totalPoint)
    {
        var badges = new List<BadgeCode>();
        if (status is not (AttemptStatus.SUBMITTED or AttemptStatus.EXPIRED)) return badges;
        var s = score ?? 0m;
        var t = totalPoint ?? 0m;
        var perfect = t > 0 && s >= t;
        if (perfect) badges.Add(BadgeCode.PERFECT_SCORE);
        else if (t > 0 && Ratio(s, t) >= ExcellentRatio) badges.Add(BadgeCode.EXCELLENT);
        if (status == AttemptStatus.SUBMITTED) badges.Add(BadgeCode.ON_TIME);
        return badges;
    }

    /// <summary>Kiên trì là huy hiệu của học sinh: có từ 5 lượt đã nộp (nộp tay hoặc tự nộp khi hết giờ).</summary>
    public static bool HasPerseverance(int finishedAttempts) => finishedAttempts >= PerseveranceAttempts;

    // ---- Mức thành thạo ----

    /// <summary>Mức thành thạo từ số câu đúng trên số câu đã trả lời (câu bỏ trống tính là đã trả lời, không đúng).</summary>
    public static MasteryLevel MasteryOf(int correct, int answered)
    {
        if (answered < MasteryMinAnswered) return MasteryLevel.INSUFFICIENT_DATA;
        var ratio = (decimal)correct / answered;
        return ratio >= HighRatio ? MasteryLevel.MASTERED : ratio >= MidRatio ? MasteryLevel.FAIR : MasteryLevel.NEEDS_REVIEW;
    }

    public static string MasteryLabel(MasteryLevel level) => level switch
    {
        MasteryLevel.MASTERED => "Thành thạo",
        MasteryLevel.FAIR => "Đạt",
        MasteryLevel.NEEDS_REVIEW => "Cần ôn",
        _ => "Chưa đủ dữ liệu",
    };

    /// <summary>Thanh sao của cấp 1: Thành thạo 3 sao, Đạt 2 sao, Cần ôn 1 sao, chưa đủ dữ liệu 0 sao.</summary>
    public static int MasteryStars(MasteryLevel level) => level switch
    {
        MasteryLevel.MASTERED => 3,
        MasteryLevel.FAIR => 2,
        MasteryLevel.NEEDS_REVIEW => 1,
        _ => 0,
    };

    /// <summary>Tỉ lệ đúng theo phần trăm, làm tròn 1 chữ số thập phân; chưa trả lời câu nào thì 0.</summary>
    public static decimal RatePercent(int correct, int answered) =>
        answered <= 0 ? 0m : Math.Round(correct * 100m / answered, 1, MidpointRounding.AwayFromZero);

    /// <summary>Số ngày chăm chỉ: đếm tổng số ngày thực tế em đã tham gia học tập/nộp bài (không bị mất ngày khi nghỉ).</summary>
    public static int StreakDays(IEnumerable<DateTime> dates, DateTime today) =>
        dates.Select(d => d.Date).Distinct().Count();

    /// <summary>Lời cổ vũ vui vẻ của linh vật cho học sinh cấp 1 ở trang chủ.</summary>
    public static string LevelOneCheer(int stars, int streak, bool hasInProgress, bool hasOpenQuizzes)
    {
        if (hasInProgress) return "Em có bài đang làm dở kìa! Cùng hoàn thành để nhận thêm sao nhé!";
        if (stars > 0) return $"Tuyệt vời! Em đã tích lũy được {stars} ngôi sao sáng chói rồi đó! 🌟";
        if (hasOpenQuizzes) return "Có thử thách đố vui mới dành cho em! Vào chơi ngay nào! 🎮";
        return "Nhập mã bài học từ thầy cô để mở khóa các chặng di sản kỳ thú nhé! 🔑";
    }
}
