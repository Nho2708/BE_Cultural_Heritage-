namespace DSVHVN.Domain.Rules;

/// <summary>
/// Năm mốc của bộ lọc "thời kỳ": khoảng năm cố định trong mã, không có bảng thời kỳ. Năm âm là trước Công nguyên.
/// </summary>
public enum HeritagePeriod
{
    /// <summary>Tiền sử và sơ sử: đến 180 TCN.</summary>
    PREHISTORIC,

    /// <summary>Bắc thuộc: 179 TCN đến 938.</summary>
    NORTHERN_RULE,

    /// <summary>Phong kiến độc lập: 939 đến 1857.</summary>
    INDEPENDENT_FEUDAL,

    /// <summary>Cận đại: 1858 đến 1944.</summary>
    MODERN,

    /// <summary>Hiện đại: 1945 đến nay.</summary>
    CONTEMPORARY,
}

/// <summary>Khoảng năm của một thời kỳ; <c>FromYear</c> hoặc <c>ToYear</c> null là không giới hạn phía đó.</summary>
public sealed record PeriodRange(HeritagePeriod Period, string Label, int? FromYear, int? ToYear);

public static class HeritagePeriods
{
    public static readonly IReadOnlyList<PeriodRange> All =
    [
        new(HeritagePeriod.PREHISTORIC, "Tiền sử và sơ sử", null, -180),
        new(HeritagePeriod.NORTHERN_RULE, "Bắc thuộc", -179, 938),
        new(HeritagePeriod.INDEPENDENT_FEUDAL, "Phong kiến độc lập", 939, 1857),
        new(HeritagePeriod.MODERN, "Cận đại", 1858, 1944),
        new(HeritagePeriod.CONTEMPORARY, "Hiện đại", 1945, null),
    ];

    public static PeriodRange Of(HeritagePeriod period) => All.Single(r => r.Period == period);
}
