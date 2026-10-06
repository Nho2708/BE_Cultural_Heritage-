using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Quy tắc thuần của tổng quan: cảnh báo gói sắp hết hạn, phần trăm điểm của một lượt, khoảng thời gian và cách gom nhóm của biểu đồ theo thời gian.
/// Đổi giá trị ở đây là đổi quy tắc nghiệp vụ.
/// </summary>
public static class OverviewRules
{
    /// <summary>Gói còn tối đa bấy nhiêu ngày (tính cả hôm nay) thì tổng quan trường cảnh báo "sắp hết hạn".</summary>
    public const int ExpiringSoonDays = 14;

    /// <summary>Biểu đồ theo thời gian mặc định lấy 30 ngày gần nhất (tính cả hôm nay), tối đa 366 ngày một lần xem.</summary>
    public const int DefaultTrendDays = 30;
    public const int MaxTrendDays = 366;

    /// <summary>Biểu đồ gom theo ngày chỉ cho khoảng tối đa 92 ngày; dài hơn thì gom theo tháng để biểu đồ không quá dày.</summary>
    public const int MaxDailyTrendDays = 92;

    /// <summary>Số ngày còn lại của gói tính từ hôm nay tới hết <paramref name="endDate"/> (gồm cả hai ngày); gói đã hết hạn thì 0.</summary>
    public static int DaysLeft(DateOnly endDate, DateOnly today) => Math.Max(0, endDate.DayNumber - today.DayNumber + 1);

    /// <summary>Gói đang hiệu lực mà còn không quá <see cref="ExpiringSoonDays"/> ngày.</summary>
    public static bool IsExpiringSoon(DateOnly endDate, DateOnly today) =>
        endDate >= today && DaysLeft(endDate, today) <= ExpiringSoonDays;

    /// <summary>Điểm của một lượt theo phần trăm tổng điểm, làm tròn 2 chữ số; tổng điểm không dương thì không tính được (null).</summary>
    public static decimal? PercentOf(decimal? score, decimal? totalPoint) =>
        score is { } s && totalPoint is > 0m ? Math.Round(s * 100m / totalPoint.Value, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>Trung bình các phần trăm, làm tròn 2 chữ số; không có lượt nào thì null.</summary>
    public static decimal? AveragePercent(IReadOnlyCollection<decimal> percents) =>
        percents.Count == 0 ? null : Math.Round(percents.Sum() / percents.Count, 2, MidpointRounding.AwayFromZero);

    /// <summary>Cách gom nhóm tự chọn theo độ dài khoảng: tới <see cref="MaxDailyTrendDays"/> ngày thì theo ngày, dài hơn thì theo tháng.</summary>
    public static TrendGranularity GranularityFor(DateOnly from, DateOnly to) =>
        to.DayNumber - from.DayNumber + 1 <= MaxDailyTrendDays ? TrendGranularity.DAY : TrendGranularity.MONTH;

    /// <summary>Nhãn của kỳ chứa <paramref name="date"/>: <c>yyyy-MM-dd</c> khi gom theo ngày, <c>yyyy-MM</c> khi gom theo tháng.</summary>
    public static string PeriodKey(DateOnly date, TrendGranularity granularity) =>
        granularity == TrendGranularity.DAY ? date.ToString("yyyy-MM-dd") : date.ToString("yyyy-MM");

    /// <summary>Các kỳ liên tiếp phủ khoảng [<paramref name="from"/>, <paramref name="to"/>], kể cả kỳ không có số liệu.</summary>
    public static IReadOnlyList<string> Periods(DateOnly from, DateOnly to, TrendGranularity granularity)
    {
        var keys = new List<string>();
        if (granularity == TrendGranularity.DAY)
        {
            for (var d = from; d <= to; d = d.AddDays(1)) keys.Add(PeriodKey(d, granularity));
            return keys;
        }
        for (var d = new DateOnly(from.Year, from.Month, 1); d <= to; d = d.AddMonths(1)) keys.Add(PeriodKey(d, granularity));
        return keys;
    }
}

/// <summary>
/// Quy tắc thuần của chất lượng câu hỏi ở màn kiểm duyệt của quản trị hệ thống. Ngân hàng không có chức năng báo cáo câu hỏi nên "câu cần xem lại"
/// là câu có nhiều học sinh làm mà tỉ lệ đúng quá thấp, tính khi hiển thị từ câu trả lời của các lượt đã nộp, không lưu.
/// </summary>
public static class QuestionQualityRules
{
    /// <summary>Cần ít nhất bấy nhiêu lượt trả lời (tính cả bỏ trống) mới đủ dữ liệu để đánh giá một câu.</summary>
    public const int MinAnswers = 10;

    /// <summary>Tỉ lệ đúng dưới ngưỡng này (phần trăm) thì câu bị đánh dấu cần xem lại.</summary>
    public const int LowCorrectPercent = 30;

    /// <summary>Đủ dữ liệu và tỉ lệ đúng dưới <see cref="LowCorrectPercent"/>. So sánh bằng số nguyên để mọi hệ cơ sở dữ liệu cho cùng kết quả.</summary>
    public static bool IsLowQuality(int answered, int correct) =>
        answered >= MinAnswers && correct * 100 < LowCorrectPercent * answered;

    /// <summary>Tỉ lệ đúng theo phần trăm, làm tròn 1 chữ số; chưa có lượt trả lời nào thì null.</summary>
    public static decimal? CorrectRatePercent(int answered, int correct) =>
        answered <= 0 ? null : Math.Round(correct * 100m / answered, 1, MidpointRounding.AwayFromZero);
}
