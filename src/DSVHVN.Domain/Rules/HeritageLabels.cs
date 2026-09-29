using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>Nhãn tiếng Việt của loại di sản, cấp xếp hạng và vùng miền cho giao diện công khai (khớp nhãn của giao diện quản trị).</summary>
public static class HeritageLabels
{
    public static string Type(HeritageType type) => type switch
    {
        HeritageType.TANGIBLE => "Vật thể",
        HeritageType.INTANGIBLE => "Phi vật thể",
        HeritageType.NATURAL => "Thiên nhiên",
        HeritageType.MIXED => "Hỗn hợp",
        HeritageType.DOCUMENTARY => "Tư liệu",
        _ => type.ToString(),
    };

    public static string Level(RecognitionLevel level) => level switch
    {
        RecognitionLevel.UNESCO => "Di sản thế giới (UNESCO)",
        RecognitionLevel.NATIONAL_SPECIAL => "Quốc gia đặc biệt",
        RecognitionLevel.NATIONAL => "Quốc gia",
        RecognitionLevel.PROVINCIAL => "Cấp tỉnh",
        RecognitionLevel.NONE => "Chưa xếp hạng",
        _ => level.ToString(),
    };

    /// <summary>Ba vùng miền như lưu trong <c>provinces.region</c>.</summary>
    public static readonly IReadOnlyList<string> Regions = ["Bắc", "Trung", "Nam"];

    public static string Region(string region) => "Miền " + region;

    /// <summary>Đưa cách gõ vùng (có hoặc không dấu, hoa thường) về giá trị lưu trong CSDL; không nhận ra thì null.</summary>
    public static string? NormalizeRegion(string? input)
    {
        var key = SearchText.Fold(input);
        return Regions.FirstOrDefault(r => SearchText.Fold(r) == key);
    }
}
