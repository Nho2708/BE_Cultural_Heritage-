using System.Globalization;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Chữ hiển thị của mốc thời gian trên timeline. Năm âm là trước Công nguyên, hiển thị "{n} TCN". Ngày tháng viết
/// dd/mm/yyyy, thiếu ngày thì mm/yyyy, thiếu cả tháng thì chỉ năm; giai đoạn chưa có năm kết thúc là còn kéo dài đến nay.
/// </summary>
public static class HeritageTimeText
{
    public const string OngoingLabel = "nay";
    private const string RangeSeparator = " – ";

    public static string Year(int year) =>
        year < 0 ? $"{(-year).ToString(CultureInfo.InvariantCulture)} TCN" : year.ToString(CultureInfo.InvariantCulture);

    /// <summary>Mốc thời điểm: "15/03/1010", "03/1010" hoặc "1010"; năm âm thêm hậu tố TCN ("03/179 TCN").</summary>
    public static string Moment(int year, int? month, int? day)
    {
        var y = year < 0 ? $"{-year} TCN" : year.ToString(CultureInfo.InvariantCulture);
        if (month is null) return y;
        var m = month.Value.ToString("00", CultureInfo.InvariantCulture);
        return day is null ? $"{m}/{y}" : $"{day.Value.ToString("00", CultureInfo.InvariantCulture)}/{m}/{y}";
    }

    /// <summary>Khoảng của sự kiện: chỉ có thời điểm bắt đầu thì là một mốc; có kết thúc thì "bắt đầu – kết thúc".</summary>
    public static string EventRange(int startYear, int? startMonth, int? startDay, int? endYear, int? endMonth, int? endDay)
    {
        var start = Moment(startYear, startMonth, startDay);
        if (endYear is null) return start;
        var end = Moment(endYear.Value, endMonth, endDay);
        return end == start ? start : start + RangeSeparator + end;
    }

    /// <summary>
    /// Khoảng năm của giai đoạn: "1010 – 1821"; chưa có năm kết thúc thì "1962 – nay"; chỉ có năm kết thúc thì "đến 1821";
    /// không có năm nào thì chuỗi rỗng.
    /// </summary>
    public static string PhaseRange(int? startYear, int? endYear) => (startYear, endYear) switch
    {
        (null, null) => string.Empty,
        (null, { } end) => "đến " + Year(end),
        ({ } start, null) => Year(start) + RangeSeparator + OngoingLabel,
        ({ } start, { } end) => start == end ? Year(start) : Year(start) + RangeSeparator + Year(end),
    };
}
