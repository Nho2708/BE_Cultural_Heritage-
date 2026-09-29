using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;

namespace DSVHVN.Domain.Rules;

/// <summary>Một điều kiện xuất bản còn thiếu: khóa trường (camelCase, khớp JSON của API) và tên hiển thị bằng tiếng Việt.</summary>
public sealed record MissingItem(string Field, string Label);

/// <summary>
/// Điều kiện để chuyển di sản sang PUBLISHED (DRAFT hoặc ARCHIVED sang PUBLISHED): đủ tên, đường dẫn, mô tả ngắn,
/// nội dung tối thiểu 200 từ, loại, cấp xếp hạng, năm công nhận (khi đã xếp hạng), ảnh đại diện nằm trong thư viện ảnh của di sản,
/// tỉnh (trừ di sản phi vật thể hoặc tư liệu trải nhiều tỉnh), tọa độ (bắt buộc với vật thể, thiên nhiên, hỗn hợp)
/// và ít nhất một nguồn tham chiếu.
/// </summary>
public static class HeritagePublishing
{
    public static IReadOnlyList<MissingItem> FindMissing(Heritage heritage, bool thumbnailInLibrary, int referenceCount, int currentYear)
    {
        var missing = new List<MissingItem>();
        if (string.IsNullOrWhiteSpace(heritage.Name)) missing.Add(new("name", "tên di sản"));
        if (string.IsNullOrWhiteSpace(heritage.Slug)) missing.Add(new("slug", "đường dẫn"));
        if (string.IsNullOrWhiteSpace(heritage.ShortDescription)) missing.Add(new("shortDescription", "mô tả ngắn"));
        if (HeritageRules.CountWords(heritage.Content) < HeritageRules.MinContentWords)
            missing.Add(new("content", $"nội dung tối thiểu {HeritageRules.MinContentWords} từ"));
        if (heritage.HeritageType is null) missing.Add(new("heritageType", "loại di sản"));
        if (heritage.RecognitionLevel is null) missing.Add(new("recognitionLevel", "cấp xếp hạng"));
        if (heritage.RecognitionLevel is { } level && level != RecognitionLevel.NONE
            && (heritage.RecognizedYear is null || heritage.RecognizedYear > currentYear))
            missing.Add(new("recognizedYear", "năm công nhận"));
        if (string.IsNullOrWhiteSpace(heritage.ThumbnailUrl) || !thumbnailInLibrary)
            missing.Add(new("thumbnailUrl", "ảnh đại diện (chọn trong thư viện ảnh của di sản)"));
        if (heritage.ProvinceId is null && heritage.HeritageType is not (HeritageType.INTANGIBLE or HeritageType.DOCUMENTARY))
            missing.Add(new("provinceId", "tỉnh/thành"));
        if (heritage.HeritageType is HeritageType.TANGIBLE or HeritageType.NATURAL or HeritageType.MIXED
            && (heritage.Latitude is null || heritage.Longitude is null))
            missing.Add(new("coordinates", "tọa độ"));
        if (referenceCount < 1) missing.Add(new("references", "nguồn tham chiếu (ít nhất một)"));
        return missing;
    }
}
