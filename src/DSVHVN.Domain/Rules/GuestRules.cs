using System.Globalization;
using System.Text.RegularExpressions;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Quy tắc của khách tham quan (không tài khoản): mã thiết bị ẩn danh, lịch sử, vị trí "Gần bạn" và hệ số gợi ý. Các con số là quy tắc nghiệp vụ cố định trong mã,
/// không cấu hình được: đổi ở đây là đổi quy tắc.
/// </summary>
public static partial class GuestRules
{
    /// <summary>Thời hạn lưu lịch sử của khách: 90 ngày, sau đó tác vụ nền xóa thật.</summary>
    public const int RetentionDays = 90;

    /// <summary>Mỗi thiết bị giữ tối đa 500 lượt gần nhất; vượt thì xóa lượt cũ nhất.</summary>
    public const int MaxActivitiesPerDevice = 500;

    /// <summary>Lượt lặp (cùng thiết bị, cùng loại, cùng di sản hoặc cùng từ khóa) trong khoảng này chỉ ghi một lần.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);

    /// <summary>Từ khóa tìm lưu trong lịch sử dài 2-100 ký tự (cùng khoảng với từ khóa tìm kiếm công khai).</summary>
    public const int SearchQueryMinLength = SearchText.KeywordMinLength;
    public const int SearchQueryMaxLength = SearchText.KeywordMaxLength;

    /// <summary>Gợi ý chỉ xét 20 lượt gần nhất gắn với di sản; từ khóa tìm không thành sở thích.</summary>
    public const int InterestWindow = 20;

    /// <summary>"Gợi ý cho bạn" và "Gần bạn" mỗi mục tối đa 6 di sản.</summary>
    public const int ResultLimit = 6;

    /// <summary>Di sản khách đã xem chi tiết từ số lần này trở lên trong thời hạn lưu thì không được gợi ý nữa.</summary>
    public const int ExcludeAfterViews = 3;

    /// <summary>Lượt đọc tóm tắt, nghe và xem 3D nặng gấp đôi lượt xem chi tiết.</summary>
    public const double DeepWeight = 2d;
    public const double ViewWeight = 1d;

    /// <summary>Tọa độ khách gửi lên được làm tròn 3 chữ số thập phân (khoảng 100 m) trước khi tính khoảng cách.</summary>
    public const int LocationDecimals = 3;

    // Khung tọa độ Việt Nam của quy tắc Dữ liệu tỉnh và di sản (vĩ độ 8,0-23,5, kinh độ 102,0-117,5; gồm Hoàng Sa, Trường Sa) nới thêm 1 độ mỗi phía.
    public const double MinLatitude = 7.0;
    public const double MaxLatitude = 24.5;
    public const double MinLongitude = 101.0;
    public const double MaxLongitude = 118.5;

    private const double EarthRadiusMeters = 6_371_000d;

    // ---- Mã thiết bị ----

    /// <summary>
    /// Mã thiết bị hợp lệ là UUID phiên bản 4 dạng chuẩn 8-4-4-4-12 chữ số thập lục phân (không nhận dấu ngoặc, không nhận chuỗi rút gọn). Trả mã đã đưa về chữ thường;
    /// không hợp lệ thì null.
    /// </summary>
    public static string? NormalizeDeviceKey(string? raw)
    {
        var key = raw?.Trim();
        if (string.IsNullOrEmpty(key) || !DeviceKeyPattern().IsMatch(key)) return null;
        return key.ToLowerInvariant();
    }

    /// <summary>Sinh một mã thiết bị mới (UUID phiên bản 4 từ bộ sinh số ngẫu nhiên an toàn của hệ điều hành).</summary>
    public static string NewDeviceKey() => Guid.NewGuid().ToString("D");

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-4[0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$")]
    private static partial Regex DeviceKeyPattern();

    // ---- Lịch sử ----

    /// <summary>Hạn xóa: lượt cũ hơn mốc này bị tác vụ nền xóa.</summary>
    public static DateTime RetentionCutoff(DateTime now) => now.AddDays(-RetentionDays);

    /// <summary>Từ khóa tìm đã bỏ khoảng trắng thừa; rỗng thì null.</summary>
    public static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        return string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Hệ số của một lượt trong gợi ý: đọc tóm tắt, nghe, xem 3D gấp đôi xem chi tiết; tìm kiếm không có hệ số.</summary>
    public static double Weight(GuestActivityType type) => type switch
    {
        GuestActivityType.VIEW => ViewWeight,
        GuestActivityType.READ_SUMMARY or GuestActivityType.LISTEN or GuestActivityType.VIEW_3D => DeepWeight,
        _ => 0d,
    };

    /// <summary>Nhãn tiếng Việt của loại lượt cho màn Lịch sử và cài đặt.</summary>
    public static string Label(GuestActivityType type) => type switch
    {
        GuestActivityType.SEARCH => "Tìm kiếm",
        GuestActivityType.VIEW => "Xem di sản",
        GuestActivityType.READ_SUMMARY => "Đọc tóm tắt",
        GuestActivityType.LISTEN => "Nghe thuyết minh",
        GuestActivityType.VIEW_3D => "Xem mô hình 3D",
        _ => type.ToString(),
    };

    // ---- Vị trí ----

    public static bool IsValidCoordinate(double latitude, double longitude) =>
        double.IsFinite(latitude) && double.IsFinite(longitude) && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    /// <summary>Làm tròn tọa độ về 3 chữ số thập phân (nửa ra xa số 0).</summary>
    public static double RoundCoordinate(double value) => Math.Round(value, LocationDecimals, MidpointRounding.AwayFromZero);

    /// <summary>Vị trí nằm trong khung Việt Nam đã nới 1 độ mỗi phía; ngoài khung thì không hiện danh sách "Gần bạn".</summary>
    public static bool IsNearVietnam(double latitude, double longitude) =>
        latitude is >= MinLatitude and <= MaxLatitude && longitude is >= MinLongitude and <= MaxLongitude;

    /// <summary>Khoảng cách trên mặt cầu (công thức haversine) giữa hai tọa độ, tính bằng mét.</summary>
    public static double DistanceMeters(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        var phiA = ToRadians(latitudeA);
        var phiB = ToRadians(latitudeB);
        var deltaPhi = ToRadians(latitudeB - latitudeA);
        var deltaLambda = ToRadians(longitudeB - longitudeA);
        var h = Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2)
                + Math.Cos(phiA) * Math.Cos(phiB) * Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1d, Math.Sqrt(h)));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;

    /// <summary>
    /// Khoảng cách để hiện: dưới 1000 m hiện mét nguyên ("350 m"), từ 1000 m hiện km làm tròn 1 chữ số ("1,2 km" trong tiếng Việt; các ngôn ngữ khác dùng dấu chấm).
    /// </summary>
    public static string FormatDistance(double meters, LanguageCode language)
    {
        var rounded = (long)Math.Round(meters, MidpointRounding.AwayFromZero);
        if (rounded < 1000) return $"{rounded} m";
        var culture = language == LanguageCode.vi ? new CultureInfo("vi-VN") : CultureInfo.InvariantCulture;
        var km = Math.Round(meters / 1000d, 1, MidpointRounding.AwayFromZero);
        return km.ToString("0.0", culture) + " km";
    }

    // ---- Dự phòng khi Dịch vụ AI lỗi ----

    /// <summary>Điểm dự phòng của một ứng viên so với di sản xem gần nhất: cùng loại 2 điểm, cùng vùng 1 điểm; 0 là không có điểm chung.</summary>
    public static double FallbackScore(HeritageFeatures candidate, HeritageFeatures reference)
    {
        var score = 0d;
        if (candidate.Type is not null && candidate.Type == reference.Type) score += 2d;
        if (!string.IsNullOrEmpty(candidate.Region) && candidate.Region == reference.Region) score += 1d;
        return score;
    }
}
