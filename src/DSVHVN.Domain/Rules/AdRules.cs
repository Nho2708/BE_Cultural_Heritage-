using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Quy tắc của quảng cáo Google có thưởng (cổng quảng cáo chỉ khóa tóm tắt và nghe thuyết minh của di sản). Các con số là quy tắc nghiệp vụ cố định trong mã:
/// đổi ở đây là đổi quy tắc. Mã đơn vị quảng cáo và việc bật cổng là cấu hình môi trường, không nằm ở đây.
/// </summary>
public static class AdRules
{
    /// <summary>Một lượt mở khóa có hiệu lực 24 giờ kể từ lúc máy chủ ghi, cho đúng một di sản trên đúng một thiết bị, ở mọi ngôn ngữ.</summary>
    public static readonly TimeSpan UnlockDuration = TimeSpan.FromHours(24);

    /// <summary>Lời gọi xác minh của AdMob có thời điểm lệch quá 1 giờ so với giờ máy chủ thì bị từ chối.</summary>
    public static readonly TimeSpan MaxTimestampSkew = TimeSpan.FromHours(1);

    /// <summary>Mã giao dịch của web hết hạn sau 10 phút kể từ lúc máy chủ cấp.</summary>
    public static readonly TimeSpan WebTransactionLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Web (trình duyệt tự báo) tối đa 10 lượt mở khóa mỗi giờ cho một mã thiết bị.</summary>
    public const int WebMaxPerDevicePerHour = 10;

    /// <summary>Web tối đa 60 lượt mỗi giờ cho một địa chỉ IP (nhiều khách có thể chung IP ở điểm tham quan nên ngưỡng IP rộng hơn).</summary>
    public const int WebMaxPerIpPerHour = 60;

    /// <summary>Cửa sổ đếm của giới hạn tần suất.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);

    /// <summary>Lượt mở khóa giữ 12 tháng rồi tác vụ nền xóa thật.</summary>
    public const int RetentionMonths = 12;

    /// <summary>Số ngày tối đa của một khoảng thống kê, như tổng quan hệ thống.</summary>
    public const int MaxStatsDays = 366;

    public const int DefaultStatsDays = 30;

    /// <summary>Số mục cao nhất của mỗi bảng xếp hạng trong thống kê.</summary>
    public const int TopLimit = 10;

    /// <summary>Mức xếp hạng nội dung tối đa của mọi yêu cầu quảng cáo: phù hợp mọi lứa tuổi (nhãn G của Google).</summary>
    public const string MaxAdContentRating = "G";

    /// <summary>Hạn xóa: lượt mở khóa cũ hơn mốc này bị tác vụ nền xóa.</summary>
    public static DateTime RetentionCutoff(DateTime now) => now.AddMonths(-RetentionMonths);

    /// <summary>Hạn của một lượt mở khóa ghi lúc <paramref name="unlockedAt"/>.</summary>
    public static DateTime ExpiryOf(DateTime unlockedAt) => unlockedAt + UnlockDuration;

    /// <summary>Nền tảng hợp lệ của mạng quảng cáo: AdMob chạy trong app (Android, iOS), Ad Manager chạy trên web.</summary>
    public static bool PlatformMatchesNetwork(AdNetwork network, ClientPlatform platform) => network switch
    {
        AdNetwork.ADMOB => platform is ClientPlatform.ANDROID or ClientPlatform.IOS,
        AdNetwork.AD_MANAGER => platform == ClientPlatform.WEB,
        _ => false,
    };

    /// <summary>
    /// Mã đơn vị quảng cáo AdMob để so sánh: AdMob gửi lại phần số sau dấu gạch chéo (<c>ca-app-pub-xxxxxxxx/1234567890</c> thành <c>1234567890</c>), nên cấu hình ghi đầy đủ
    /// hay chỉ phần số đều so được. Chuỗi rỗng thì null.
    /// </summary>
    public static string? NormalizeAdMobUnit(string? unit)
    {
        var text = unit?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var slash = text.LastIndexOf('/');
        return slash >= 0 ? text[(slash + 1)..] : text;
    }

    /// <summary>Nguồn báo phần thưởng suy từ mạng: AdMob do máy chủ xác minh, Ad Manager do trình duyệt báo.</summary>
    public static string SourceOf(AdNetwork network) => network == AdNetwork.ADMOB ? "SERVER" : "BROWSER";

    // ---- Lý do phần thưởng bị từ chối (ghi vào nhật ký thao tác, thống kê theo lý do) ----

    public const string RejectedBadSignature = "INVALID_SIGNATURE";
    public const string RejectedUnknownAdUnit = "UNKNOWN_AD_UNIT";
    public const string RejectedBadParameter = "INVALID_PARAMETER";
    public const string RejectedStaleTimestamp = "STALE_TIMESTAMP";
    public const string RejectedBadWebTransaction = "INVALID_WEB_TRANSACTION";
    public const string RejectedRateLimited = "RATE_LIMITED";

    /// <summary>Sáu lý do từ chối theo thứ tự hiển thị của thống kê.</summary>
    public static readonly IReadOnlyList<string> RejectionReasons =
    [
        RejectedBadSignature, RejectedUnknownAdUnit, RejectedBadParameter, RejectedStaleTimestamp, RejectedBadWebTransaction, RejectedRateLimited,
    ];

    /// <summary>Nhãn tiếng Việt của lý do từ chối cho màn thống kê của quản trị hệ thống.</summary>
    public static string RejectionLabel(string reason) => reason switch
    {
        RejectedBadSignature => "Chữ ký không đúng",
        RejectedUnknownAdUnit => "Đơn vị quảng cáo lạ",
        RejectedBadParameter => "Thiếu hoặc sai tham số",
        RejectedStaleTimestamp => "Thời điểm lệch quá 1 giờ",
        RejectedBadWebTransaction => "Mã giao dịch web sai hoặc hết hạn",
        RejectedRateLimited => "Vượt giới hạn tần suất",
        _ => reason,
    };

    /// <summary>Nhãn tiếng Việt của nguồn báo.</summary>
    public static string SourceLabel(AdNetwork network) => network == AdNetwork.ADMOB ? "Máy chủ xác minh với Google" : "Trình duyệt báo";
}
